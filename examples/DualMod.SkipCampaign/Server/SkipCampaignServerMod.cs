using System.Collections.Concurrent;
using DualMod.SkipCampaign.Shared;
using LocalExile.Client.ServerBridge;

namespace DualMod.SkipCampaign.Server;

/// <summary>
/// Gateway-independent projection of LocalExile <c>CharacterCreatedContext</c> passed into
/// <see cref="SkipCampaignHandler.OnCharacterCreated"/>.
/// </summary>
public sealed record SkipCampaignCharacterCreatedInput(
    string Account,
    Guid CharacterId,
    string CharacterName,
    string League,
    bool Hardcore,
    bool Ssf,
    IReadOnlyDictionary<string, string> CreationOptions);

/// <summary>
/// Result of evaluating a brand-new character against <see cref="SkipCampaignHandler"/> before
/// Auth sends <c>S-&gt;C 0x15 / 0x13</c> and assigns the first area.
/// </summary>
public sealed record SkipCampaignCharacterCreatedEffect(
    bool Applied,
    string? StartingAreaId = null,
    int? StartingLevel = null,
    bool SuppressStartKit = false,
    string? DenialReason = null);

/// <summary>
/// Gateway-independent projection of LocalExile <c>CharacterSaves.LeagueCharacterInfo</c> used to verify
/// prior league completion while enforcing exact league, SSF, and Hardcore-death isolation.
/// </summary>
public sealed record SkipCampaignLeagueCharacterSnapshot(
    Guid Id,
    string Name,
    int Level,
    string League,
    string AreaId,
    string? LastTown,
    bool HardcoreDead = false,
    bool PermanentlyDead = false);

/// <summary>
/// Abstraction over LocalExile's <c>CharacterSaves.GetLeagueCharacters(account, league, includeDead)</c>.
/// </summary>
public interface ISkipCampaignEligibilitySource
{
    /// <summary>
    /// Lists characters owned by <paramref name="account"/> in exact <paramref name="league"/>,
    /// excluding dead Hardcore characters when <paramref name="includeDead"/> is <c>false</c>.
    /// </summary>
    IReadOnlyList<SkipCampaignLeagueCharacterSnapshot> GetLeagueCharacters(
        string account,
        string league,
        bool includeDead = false);
}

/// <summary>
/// Abstraction over LocalExile's public <c>IPlayerWorld</c> progression facades so
/// <see cref="SkipCampaignHandler.OnFirstEnterAsync"/> can be tested deterministically without a live Gateway.
/// </summary>
public interface ISkipCampaignPlayerWorld
{
    /// <summary>Active character unique identifier.</summary>
    Guid CharacterId { get; }

    /// <summary>Active character name.</summary>
    string CharacterName { get; }

    /// <summary>Active character league identifier.</summary>
    string League { get; }

    /// <summary>Current account gold balance in this league.</summary>
    uint Gold { get; }

    /// <summary>Current character passive refund points.</summary>
    ushort RefundPoints { get; }

    /// <summary>Checks whether the character holds <paramref name="flag"/>.</summary>
    bool HasQuestFlag(string flag, bool earnedOnly = false);

    /// <summary>Applies character quest flags, clearing <c>UnearnedQuestFlags</c> so Kitava penalties and endgame gates apply.</summary>
    Task SetQuestFlagsAsync(IEnumerable<string> flags, bool on = true, CancellationToken cancellationToken = default);

    /// <summary>Activates waypoints for the specified <c>WorldAreas</c> Ids and returns how many new waypoints were added.</summary>
    Task<int> GrantWaypointsAsync(IEnumerable<string> areaIds, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Sets the character's passive refund points and returns the resulting balance.</summary>
    Task<ushort> SetRefundPointsAsync(ushort points, CancellationToken cancellationToken = default);

    /// <summary>Awards Labyrinth ascendancy tiers (<c>normal</c>/<c>cruel</c>/<c>merciless</c>/<c>eternal</c>) and returns total earned points.</summary>
    Task<int> AwardAscendancyAsync(IEnumerable<string> awardKeys, CancellationToken cancellationToken = default);

    /// <summary>Adds gold to the player's league stash and returns the updated balance.</summary>
    Task<uint> AddGoldAsync(long delta, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Reads a persisted per-character marker from <c>IPlayerWorld.CharacterData</c>.</summary>
    string? GetCharacterData(string key);

    /// <summary>Atomically updates a persisted per-character marker in <c>IPlayerWorld.CharacterData</c>.</summary>
    bool ChangeCharacterData(string key, Func<string?, string?> change);
}

/// <summary>
/// Pending one-time post-entry grant queued during <c>GameEvents.CharacterCreated</c> and consumed on first login.
/// </summary>
public sealed record SkipCampaignPendingGrant(
    Guid CharacterId,
    string CharacterName,
    string Account,
    string League,
    string StartingAreaId,
    int StartingLevel,
    SkipCampaignSettings SettingsSnapshot,
    DateTimeOffset CreatedUtc);

/// <summary>
/// Result of running <see cref="SkipCampaignHandler.OnFirstEnterAsync"/> when a player enters an area.
/// </summary>
public sealed record SkipCampaignFirstEnterResult(
    bool Applied,
    bool AlreadyFinalized,
    SkipCampaignAppliedNotice? Notice = null);

/// <summary>
/// Core domain handler implementing opt-in Skip Campaign eligibility checks, <c>CharacterCreated</c>
/// starting-state overrides, and idempotent first-enter <c>IPlayerWorld</c> progression grants.
/// </summary>
public sealed class SkipCampaignHandler
{
    private readonly ISkipCampaignEligibilitySource _eligibilitySource;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, SkipCampaignPendingGrant> _pendingByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, SkipCampaignPendingGrant> _pendingById = new();
    private readonly ConcurrentDictionary<string, byte> _finalizedKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Active settings snapshot governing campaign skip rules.</summary>
    public SkipCampaignSettings Settings { get; }

    /// <summary>Creates a new <see cref="SkipCampaignHandler"/>.</summary>
    public SkipCampaignHandler(
        SkipCampaignSettings settings,
        ISkipCampaignEligibilitySource eligibilitySource,
        Action<string>? log = null)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _eligibilitySource = eligibilitySource ?? throw new ArgumentNullException(nameof(eligibilitySource));
        _log = log;
    }

    /// <summary>Returns <c>true</c> if a pending one-time first-enter grant is queued for the specified character.</summary>
    public bool HasPendingGrant(Guid characterId, string characterName) =>
        (characterId != Guid.Empty && _pendingById.ContainsKey(characterId)) ||
        (!string.IsNullOrWhiteSpace(characterName) && _pendingByName.ContainsKey(characterName.Trim()));

    /// <summary>
    /// Evaluates <c>GameEvents.CharacterCreated</c> options and league eligibility. When approved,
    /// records a pending one-time post-entry grant and returns <c>StartingAreaId</c>, <c>StartingLevel</c>,
    /// and <c>SuppressStartKit = true</c> so Auth assigns Karui Shores before first login.
    /// </summary>
    public SkipCampaignCharacterCreatedEffect OnCharacterCreated(SkipCampaignCharacterCreatedInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!Settings.Enabled)
        {
            return new SkipCampaignCharacterCreatedEffect(Applied: false, DenialReason: "Skip Campaign is disabled.");
        }

        if (!SkipCampaignProtocol.IsOptInEnabled(input.CreationOptions))
        {
            return new SkipCampaignCharacterCreatedEffect(Applied: false);
        }

        if (Settings.RequireLeagueCompletion && !HasCompletedCampaignInLeague(input))
        {
            string reason = $"Requires a living campaign-completed character (level {Settings.CompletionMinLevel}+ or in Karui Shores) in league '{input.League}'.";
            _log?.Invoke($"[skip-campaign] Denied for '{input.CharacterName}' ({input.Account}/{input.League}): {reason}");
            return new SkipCampaignCharacterCreatedEffect(Applied: false, DenialReason: reason);
        }

        string startingArea = string.IsNullOrWhiteSpace(Settings.StartingAreaId)
            ? SkipCampaignProtocol.DefaultStartingAreaId
            : Settings.StartingAreaId.Trim();
        int startingLevel = Math.Clamp(Settings.StartingLevel, 1, 100);

        var grant = new SkipCampaignPendingGrant(
            CharacterId: input.CharacterId,
            CharacterName: input.CharacterName,
            Account: input.Account,
            League: input.League,
            StartingAreaId: startingArea,
            StartingLevel: startingLevel,
            SettingsSnapshot: Settings,
            CreatedUtc: DateTimeOffset.UtcNow);

        if (!string.IsNullOrWhiteSpace(input.CharacterName))
        {
            _pendingByName[input.CharacterName.Trim()] = grant;
        }

        if (input.CharacterId != Guid.Empty)
        {
            _pendingById[input.CharacterId] = grant;
        }

        _log?.Invoke($"[skip-campaign] Approved for '{input.CharacterName}' ({input.Account}/{input.League}) -> {startingArea} Lv.{startingLevel} (SuppressStartKit=true).");
        return new SkipCampaignCharacterCreatedEffect(
            Applied: true,
            StartingAreaId: startingArea,
            StartingLevel: startingLevel,
            SuppressStartKit: true);
    }

    /// <summary>
    /// Applies one-time campaign quest flags (stripping <c>UnearnedQuestFlags</c>), waypoints, refund points,
    /// optional Lab ascendancy points, and starting gold on the character's first in-game arrival.
    /// Strictly idempotent across relogs and concurrent area-enter notifications.
    /// </summary>
    public async Task<SkipCampaignFirstEnterResult> OnFirstEnterAsync(
        ISkipCampaignPlayerWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (!TryPeekPendingGrant(world.CharacterId, world.CharacterName, out SkipCampaignPendingGrant? pending) ||
            pending is null)
        {
            bool persisted = IsPersistedFinalized(world);
            return new SkipCampaignFirstEnterResult(Applied: false, AlreadyFinalized: persisted);
        }

        string finalizeKey = BuildFinalizationKey(world.CharacterId, world.CharacterName);
        if (IsPersistedFinalized(world) || !_finalizedKeys.TryAdd(finalizeKey, 1))
        {
            RemovePendingGrant(pending.CharacterId, pending.CharacterName);
            return new SkipCampaignFirstEnterResult(Applied: false, AlreadyFinalized: true);
        }

        try
        {
            SkipCampaignSettings cfg = pending.SettingsSnapshot;

            IReadOnlyList<string> questFlags = cfg.KitavaPenaltyQuestFlags ?? SkipCampaignProtocol.DefaultKitavaPenaltyQuestFlags;
            if (questFlags.Count > 0)
            {
                await world.SetQuestFlagsAsync(questFlags, on: true, cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<string> waypoints = cfg.CampaignWaypoints ?? SkipCampaignProtocol.DefaultCampaignWaypoints;
            int waypointsGranted = waypoints.Count > 0
                ? await world.GrantWaypointsAsync(waypoints, "skip-campaign", cancellationToken).ConfigureAwait(false)
                : 0;

            ushort refundPoints = cfg.RefundPoints > 0
                ? await world.SetRefundPointsAsync(cfg.RefundPoints, cancellationToken).ConfigureAwait(false)
                : world.RefundPoints;

            int ascendancyPoints = 0;
            IReadOnlyList<string> awardKeys = cfg.AscendancyAwardKeys ?? SkipCampaignProtocol.DefaultAscendancyAwardKeys;
            if (cfg.GrantAscendancyTrials && awardKeys.Count > 0)
            {
                ascendancyPoints = await world.AwardAscendancyAsync(awardKeys, cancellationToken).ConfigureAwait(false);
            }

            uint totalGold = cfg.StartingGold > 0
                ? await world.AddGoldAsync(cfg.StartingGold, "skip-campaign", cancellationToken).ConfigureAwait(false)
                : world.Gold;

            world.ChangeCharacterData(SkipCampaignProtocol.FinalizedCharacterDataKey, _ => "1");
            RemovePendingGrant(pending.CharacterId, pending.CharacterName);

            var notice = new SkipCampaignAppliedNotice(
                CharacterName: world.CharacterName,
                League: string.IsNullOrWhiteSpace(world.League) ? pending.League : world.League,
                StartingAreaId: pending.StartingAreaId,
                StartingLevel: pending.StartingLevel,
                WaypointsGranted: waypointsGranted,
                QuestFlagsApplied: questFlags.Count,
                RefundPoints: refundPoints,
                AscendancyPointsEarned: ascendancyPoints,
                GoldGranted: cfg.StartingGold,
                TotalGold: totalGold);

            _log?.Invoke($"[skip-campaign] Applied first-enter progression for '{world.CharacterName}': {questFlags.Count} flags, {waypointsGranted} waypoints, {refundPoints} refund pts, {ascendancyPoints} ascendancy pts, +{cfg.StartingGold} gold (total {totalGold}).");
            return new SkipCampaignFirstEnterResult(Applied: true, AlreadyFinalized: false, Notice: notice);
        }
        catch
        {
            // Release in-memory finalization guard if applying failed before persisting, so retry remains possible.
            _finalizedKeys.TryRemove(finalizeKey, out _);
            throw;
        }
    }

    private bool HasCompletedCampaignInLeague(SkipCampaignCharacterCreatedInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Account) || string.IsNullOrWhiteSpace(input.League))
        {
            return false;
        }

        IReadOnlyList<SkipCampaignLeagueCharacterSnapshot> chars =
            _eligibilitySource.GetLeagueCharacters(input.Account, input.League, includeDead: false);

        foreach (SkipCampaignLeagueCharacterSnapshot candidate in chars)
        {
            // Never allow the brand-new character to satisfy its own eligibility check.
            if ((input.CharacterId != Guid.Empty && candidate.Id == input.CharacterId) ||
                string.Equals(candidate.Name, input.CharacterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Defensively enforce exact league/SSF match and alive-only HC isolation.
            if (!string.Equals(candidate.League, input.League, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (candidate.HardcoreDead || candidate.PermanentlyDead)
            {
                continue;
            }

            if (IsCampaignCompleted(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsCampaignCompleted(SkipCampaignLeagueCharacterSnapshot character)
    {
        int minLevel = Math.Max(1, Settings.CompletionMinLevel);
        if (character.Level >= minLevel)
        {
            return true;
        }

        return IsEndgameAreaId(character.LastTown) || IsEndgameAreaId(character.AreaId);
    }

    private bool IsEndgameAreaId(string? areaId)
    {
        if (string.IsNullOrWhiteSpace(areaId))
        {
            return false;
        }

        string trimmed = areaId.Trim();
        return string.Equals(trimmed, SkipCampaignProtocol.DefaultStartingAreaId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, Settings.StartingAreaId, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("MapWorlds", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryPeekPendingGrant(Guid characterId, string characterName, out SkipCampaignPendingGrant? grant)
    {
        if (characterId != Guid.Empty && _pendingById.TryGetValue(characterId, out grant))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(characterName) && _pendingByName.TryGetValue(characterName.Trim(), out grant))
        {
            return true;
        }

        grant = null;
        return false;
    }

    private void RemovePendingGrant(Guid characterId, string characterName)
    {
        if (characterId != Guid.Empty)
        {
            _pendingById.TryRemove(characterId, out _);
        }

        if (!string.IsNullOrWhiteSpace(characterName))
        {
            _pendingByName.TryRemove(characterName.Trim(), out _);
        }
    }

    private static bool IsPersistedFinalized(ISkipCampaignPlayerWorld world) =>
        string.Equals(world.GetCharacterData(SkipCampaignProtocol.FinalizedCharacterDataKey), "1", StringComparison.Ordinal);

    private static string BuildFinalizationKey(Guid characterId, string characterName) =>
        characterId != Guid.Empty
            ? characterId.ToString("N")
            : characterName.Trim();
}

/// <summary>
/// Server mod coordinator binding <see cref="SkipCampaignHandler"/> to <see cref="ClientBridgeHost"/>
/// so <see cref="SkipCampaignAppliedNotice"/> is delivered reliably over <c>ServerBridge</c>
/// (even if <c>PlayerEntered</c> runs before the NativeMods client finishes its initial handshake).
/// </summary>
public sealed class SkipCampaignServerMod : IDisposable
{
    private readonly ClientBridgeHost _bridge;
    private readonly SkipCampaignHandler _handler;
    private readonly ConcurrentDictionary<string, SkipCampaignAppliedNotice> _pendingNotices = new(StringComparer.Ordinal);
    private IDisposable? _readySub;
    private bool _disposed;

    /// <summary>Underlying campaign-skip rules and progression handler.</summary>
    public SkipCampaignHandler Handler => _handler;

    /// <summary>Creates a new <see cref="SkipCampaignServerMod"/>.</summary>
    public SkipCampaignServerMod(ClientBridgeHost bridge, SkipCampaignHandler handler)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>Attaches <see cref="ClientBridgeHost.OnClientReady(Func{IClientSession, CancellationToken, Task})"/> notice flushing.</summary>
    public void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _readySub ??= _bridge.OnClientReady(async (session, ct) =>
        {
            if (_pendingNotices.TryRemove(session.ConnectionId, out SkipCampaignAppliedNotice? queued))
            {
                await _bridge.TrySendAsync(session.ConnectionId, SkipCampaignProtocol.ChannelName, queued, cancellationToken: ct).ConfigureAwait(false);
            }
        });
    }

    /// <summary>Delegates <c>GameEvents.CharacterCreated</c> evaluation to <see cref="SkipCampaignHandler"/>.</summary>
    public SkipCampaignCharacterCreatedEffect HandleCharacterCreated(SkipCampaignCharacterCreatedInput input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _handler.OnCharacterCreated(input);
    }

    /// <summary>
    /// Runs first-enter progression for <paramref name="world"/> and dispatches <see cref="SkipCampaignAppliedNotice"/>
    /// to <paramref name="connectionId"/> over <c>ServerBridge</c> (or queues it until client handshake completes).
    /// </summary>
    public async Task<SkipCampaignFirstEnterResult> OnPlayerEnteredAsync(
        string connectionId,
        ISkipCampaignPlayerWorld world,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(world);

        SkipCampaignFirstEnterResult result = await _handler.OnFirstEnterAsync(world, cancellationToken).ConfigureAwait(false);
        if (result.Applied && result.Notice is { } notice)
        {
            if (_bridge.IsClientReady(connectionId))
            {
                await _bridge.TrySendAsync(connectionId, SkipCampaignProtocol.ChannelName, notice, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _pendingNotices[connectionId] = notice;
            }
        }

        return result;
    }

    /// <summary>Drops any queued notice for a disconnecting connection.</summary>
    public void OnPlayerLeft(string connectionId)
    {
        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            _pendingNotices.TryRemove(connectionId, out _);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _readySub?.Dispose();
        _readySub = null;
        _pendingNotices.Clear();
    }
}
