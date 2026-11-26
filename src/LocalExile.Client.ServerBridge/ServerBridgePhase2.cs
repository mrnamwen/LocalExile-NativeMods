using System.Collections.Concurrent;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Protocol;

namespace LocalExile.Client.ServerBridge;

/// <summary>
/// JSON-serializable snapshot of a live entity in a connected client's active world (<c>EntityManager</c>).
/// Mirrors <see cref="WorldEntitySnapshot"/> using portable <see cref="ulong"/> native handles for cross-wire transport.
/// </summary>
/// <param name="EntityId">Unique world entity identifier (<c>&lt; 0x40000000</c> server, <c>&gt;= 0x40000000</c> client-only).</param>
/// <param name="EntityHandle">Optional client-side native <c>Entity*</c> address for diagnostics, or <c>0</c>.</param>
/// <param name="Kind">Classification and state bitflags for this entity.</param>
/// <param name="MetadataPath">Object type metadata path (for example <c>Metadata/MiscellaneousObjects/WorldItem</c>).</param>
/// <param name="GridX">Tile/grid X coordinate from <c>Positioned</c>.</param>
/// <param name="GridY">Tile/grid Y coordinate from <c>Positioned</c>.</param>
/// <param name="Position">3D world-space position from <c>Positioned</c>/<c>Render</c>.</param>
/// <param name="Hp">Current hit points from <c>Life</c>, or <c>0</c> if absent.</param>
/// <param name="MaxHp">Maximum hit points from <c>Life</c>, or <c>0</c> if absent.</param>
/// <param name="Rarity">Magic/rare/unique rarity from <c>ObjectMagicProperties</c> or item mods.</param>
/// <param name="IsFilterVisible">For <see cref="WorldEntityKind.WorldItem"/> entities, whether the active item filter shows the item.</param>
/// <param name="WorldItem">Populated item metadata snapshot when the entity carries a <c>WorldItem</c> component; otherwise <c>null</c>.</param>
public sealed record ClientWorldEntitySnapshot(
    uint EntityId,
    ulong EntityHandle,
    WorldEntityKind Kind,
    string MetadataPath,
    int GridX,
    int GridY,
    WorldPosition3D Position,
    int Hp = 0,
    int MaxHp = 0,
    ItemRarity Rarity = ItemRarity.Normal,
    bool IsFilterVisible = false,
    ClientHoveredItemSnapshot? WorldItem = null)
{
    /// <summary>Gets whether this entity is a player.</summary>
    public bool IsPlayer => (Kind & WorldEntityKind.Player) != 0;

    /// <summary>Gets whether this entity is a monster.</summary>
    public bool IsMonster => (Kind & WorldEntityKind.Monster) != 0;

    /// <summary>Gets whether this entity is a ground world item.</summary>
    public bool IsWorldItem => (Kind & WorldEntityKind.WorldItem) != 0;

    /// <summary>Gets whether this entity is alive.</summary>
    public bool IsAlive => (Kind & WorldEntityKind.Alive) != 0;

    /// <summary>Gets whether this entity is targetable.</summary>
    public bool IsTargetable => (Kind & WorldEntityKind.Targetable) != 0;

    /// <summary>Gets whether this entity was spawned locally on the client.</summary>
    public bool IsClientOnly => (Kind & WorldEntityKind.ClientOnly) != 0 || EntityId >= 0x4000_0000u;
}

/// <summary>
/// Filter criteria for querying live entities from a connected client via <see cref="IClientPhase2Session.QueryWorldEntitiesAsync"/>.
/// </summary>
/// <param name="RequiredKind">Bitmask of <see cref="WorldEntityKind"/> flags that MUST all be present on matching entities.</param>
/// <param name="ExcludedKind">Bitmask of <see cref="WorldEntityKind"/> flags that MUST NOT be present on matching entities.</param>
/// <param name="MetadataPrefix">Optional case-insensitive prefix filter on <see cref="ClientWorldEntitySnapshot.MetadataPath"/>.</param>
/// <param name="MaxDistance">Optional maximum 3D Euclidean distance from <paramref name="Origin"/> (or the local player if <paramref name="Origin"/> is <c>null</c>).</param>
/// <param name="Origin">Optional reference origin for <paramref name="MaxDistance"/> filtering; defaults to the local player's position.</param>
/// <param name="FilterVisibleOnly">When <c>true</c>, only includes entities with <see cref="ClientWorldEntitySnapshot.IsFilterVisible"/> set.</param>
/// <param name="MaxResults">Optional maximum number of entities to return.</param>
public sealed record ClientWorldEntityQuery(
    WorldEntityKind RequiredKind = WorldEntityKind.None,
    WorldEntityKind ExcludedKind = WorldEntityKind.None,
    string? MetadataPrefix = null,
    float? MaxDistance = null,
    WorldPosition3D? Origin = null,
    bool FilterVisibleOnly = false,
    int? MaxResults = null);

/// <summary>
/// Snapshot of all live numeric stats read from a world entity's <c>StatsComponent</c> on a connected client,
/// including high-ID (<c>&gt;= 0x10000</c>) custom stats.
/// </summary>
/// <param name="EntityId">Resolved world entity identifier (local player's entity id when queried with <c>0</c>, or <c>0</c> if unavailable).</param>
/// <param name="Stats">Dictionary mapping numeric stat identifiers to current values.</param>
public sealed record ClientEntityStatsSnapshot(
    uint EntityId,
    IReadOnlyDictionary<int, int> Stats)
{
    /// <summary>Returns the value for <paramref name="statId"/> if present; otherwise <paramref name="defaultValue"/>.</summary>
    public int GetValueOrDefault(int statId, int defaultValue = 0) =>
        Stats.TryGetValue(statId, out int value) ? value : defaultValue;

    /// <summary>Attempts to read the value for <paramref name="statId"/>.</summary>
    public bool TryGetValue(int statId, out int value) =>
        Stats.TryGetValue(statId, out value);
}

/// <summary>
/// Result of querying a single live numeric stat on a connected client's world entity or local player.
/// </summary>
/// <param name="EntityId">Resolved world entity identifier (local player's entity id when queried with <c>0</c>, or <c>0</c> if unavailable).</param>
/// <param name="StatId">Queried numeric stat identifier (supports high-ID custom stats <c>&gt;= 0x10000</c>).</param>
/// <param name="Found"><c>true</c> if the entity and stat were resolved; otherwise <c>false</c>.</param>
/// <param name="Value">Current stat value, or <c>0</c> when <paramref name="Found"/> is <c>false</c>.</param>
public sealed record ClientStatValueSnapshot(
    uint EntityId,
    int StatId,
    bool Found,
    int Value);

/// <summary>
/// Discriminator describing whether a <see cref="ClientAreaChangedEvent"/> represents entering or leaving an area.
/// </summary>
public enum ClientAreaChangeKind
{
    /// <summary>The client finished entering an area.</summary>
    Entered = 0,

    /// <summary>The client left its active area.</summary>
    Left = 1,
}

/// <summary>
/// Opt-in notification payload emitted when a connected client enters or leaves a game area.
/// </summary>
/// <param name="Kind">Whether the client entered or left the area.</param>
/// <param name="AreaId">Area identifier (for example <c>1_1_town</c> or <c>HideoutBattleScarred</c>).</param>
/// <param name="Area">Full <see cref="AreaInfo"/> metadata snapshot when <paramref name="Kind"/> is <see cref="ClientAreaChangeKind.Entered"/>; <c>null</c> on leave.</param>
/// <param name="Seed">Deterministic area generation seed when entering an area; <c>0</c> on leave.</param>
/// <param name="ActiveRulesetIds">Active ruleset identifiers reported for the area when entering; <c>null</c> on leave.</param>
public sealed record ClientAreaChangedEvent(
    ClientAreaChangeKind Kind,
    string AreaId,
    AreaInfo? Area = null,
    uint Seed = 0,
    IReadOnlyList<uint>? ActiveRulesetIds = null);

/// <summary>
/// Tier 1 <c>ServerBridge 0.4.0</c> Phase 2 session surface allowing single-<c>.cs</c> Roslyn server mods
/// (with zero custom client DLL) to query live world entities &amp; visible ground loot, read entity/player stats
/// (including high-ID <c>&gt;= 0x10000</c>), inspect or subscribe to game area transitions, query/reload/evaluate
/// the active client item filter, and read camera state or control a disconnect-safe camera zoom override.
/// </summary>
public interface IClientPhase2Session
{
    /// <summary>
    /// Queries all currently item-filter-visible ground loot (<see cref="WorldEntityKind.WorldItem"/>) entities
    /// in the client's active area, optionally bounded by 3D distance from the local player.
    /// </summary>
    /// <param name="maxDistance">Optional maximum 3D distance in world units from the local player.</param>
    /// <param name="timeout">Optional query timeout (defaults to 5 seconds).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task<IReadOnlyList<ClientWorldEntitySnapshot>> QueryVisibleGroundItemsAsync(
        float? maxDistance = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries live entities in the client's active world (<c>EntityManager</c>), optionally filtered by <paramref name="query"/>.
    /// </summary>
    /// <param name="query">Optional filter criteria; pass <c>null</c> to return all entities.</param>
    /// <param name="timeout">Optional query timeout (defaults to 5 seconds).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task<IReadOnlyList<ClientWorldEntitySnapshot>> QueryWorldEntitiesAsync(
        ClientWorldEntityQuery? query = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the live snapshot of the local player entity in the client's active area, or <c>null</c> when not in-game.
    /// </summary>
    /// <param name="timeout">Optional query timeout (defaults to 5 seconds).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task<ClientWorldEntitySnapshot?> QueryLocalPlayerAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries all live numeric stats (including high-ID <c>&gt;= 0x10000</c> custom stats) on <paramref name="entityId"/>.
    /// Passing <c>0</c> targets the local player entity.
    /// </summary>
    /// <param name="entityId">World entity id, or <c>0</c> for the local player.</param>
    /// <param name="timeout">Optional query timeout (defaults to 5 seconds).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task<ClientEntityStatsSnapshot> QueryEntityStatsAsync(
        uint entityId = 0,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries a single live numeric stat on <paramref name="entityId"/> (supports high-ID custom stats <c>&gt;= 0x10000</c>).
    /// Passing <c>0</c> for <paramref name="entityId"/> targets the local player entity.
    /// </summary>
    /// <param name="statId">Numeric stat identifier.</param>
    /// <param name="entityId">World entity id, or <c>0</c> for the local player.</param>
    /// <param name="timeout">Optional query timeout (defaults to 5 seconds).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task<ClientStatValueSnapshot> QueryStatAsync(
        int statId,
        uint entityId = 0,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries full <see cref="AreaInfo"/> metadata for the client's currently loaded game area, or <c>null</c> when outside an area.
    /// </summary>
    /// <param name="timeout">Optional query timeout (defaults to 5 seconds).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task<AreaInfo?> QueryAreaInfoAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes synchronously to opt-in client area enter/leave notifications (ref-counted across mods).</summary>
    Task<IDisposable> OnAreaChangedAsync(
        Action<IClientSession, ClientAreaChangedEvent> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously to opt-in client area enter/leave notifications (ref-counted across mods).</summary>
    Task<IDisposable> OnAreaChangedAsync(
        Func<IClientSession, ClientAreaChangedEvent, Task> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously with cancellation support to opt-in client area enter/leave notifications (ref-counted across mods).</summary>
    Task<IDisposable> OnAreaChangedAsync(
        Func<IClientSession, ClientAreaChangedEvent, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Queries the client's active <see cref="ItemFilterState"/> configuration and highlight flags.</summary>
    /// <param name="timeout">Optional query timeout (defaults to 5 seconds).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task<ItemFilterState> QueryItemFilterStateAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>Triggers a game-thread reload of the client's currently selected item filter.</summary>
    /// <param name="timeout">Optional query timeout (defaults to 5 seconds).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns><c>true</c> if the client host dispatched the reload request; otherwise <c>false</c>.</returns>
    Task<bool> ReloadItemFilterAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the cached filter result (or triggers live <c>ItemFilter::Match</c> evaluation) for a ground world-item
    /// or item entity on the client.
    /// </summary>
    /// <param name="entityId">World entity identifier to inspect or evaluate.</param>
    /// <param name="liveEvaluate">When <c>true</c>, prefers live <c>ItemFilter::Match</c> evaluation over cached ground-label state.</param>
    /// <param name="timeout">Optional query timeout (defaults to 5 seconds).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task<ItemFilterResult> EvaluateWorldItemFilterAsync(
        uint entityId,
        bool liveEvaluate = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes synchronously to opt-in client item-filter reload notifications (ref-counted across mods).</summary>
    Task<IDisposable> OnFilterReloadedAsync(
        Action<IClientSession, ItemFilterReloadedEventArgs> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously to opt-in client item-filter reload notifications (ref-counted across mods).</summary>
    Task<IDisposable> OnFilterReloadedAsync(
        Func<IClientSession, ItemFilterReloadedEventArgs, Task> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously with cancellation support to opt-in client item-filter reload notifications (ref-counted across mods).</summary>
    Task<IDisposable> OnFilterReloadedAsync(
        Func<IClientSession, ItemFilterReloadedEventArgs, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Queries the client's active <see cref="CameraState"/> (position, target, zoom, FOV, viewport, and view-projection matrix).</summary>
    /// <param name="timeout">Optional query timeout (defaults to 5 seconds).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task<CameraState> QueryCameraStateAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets or clears a server-owned camera zoom override on the client (<c>CameraController + 0x4ac</c>).
    /// Passing <c>null</c> (or a negative value) clears the override and restores stock zoom (<c>-1.0f</c>).
    /// Automatically restored to stock zoom when the client disconnects or the host disposes.
    /// </summary>
    /// <param name="zoomOverride">Desired non-negative zoom override, or <c>null</c> to restore stock zoom.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task SetCameraZoomOverrideAsync(
        float? zoomOverride,
        CancellationToken cancellationToken = default);
}

// -----------------------------------------------------------------------------
// Internal Wire DTOs (JSON schema shared with LocalExile.Client.Host.Services.ServerBridgeTier1Handler)
// -----------------------------------------------------------------------------

internal sealed record Tier1QueryWorldEntitiesPayload(string RequestId, ClientWorldEntityQuery? Query = null);

internal sealed record Tier1WorldEntitiesResponsePayload(string RequestId, IReadOnlyList<ClientWorldEntitySnapshot> Entities);

internal sealed record Tier1QueryLocalPlayerPayload(string RequestId);

internal sealed record Tier1LocalPlayerResponsePayload(string RequestId, ClientWorldEntitySnapshot? Player);

internal sealed record Tier1QueryEntityStatsPayload(string RequestId, uint EntityId = 0);

internal sealed record Tier1EntityStatsResponsePayload(string RequestId, uint EntityId, IReadOnlyDictionary<int, int> Stats);

internal sealed record Tier1QueryStatPayload(string RequestId, int StatId, uint EntityId = 0);

internal sealed record Tier1StatResponsePayload(string RequestId, uint EntityId, int StatId, bool Found, int Value);

internal sealed record Tier1QueryAreaInfoPayload(string RequestId);

internal sealed record Tier1AreaInfoResponsePayload(string RequestId, AreaInfo? Area);

internal sealed record Tier1QueryItemFilterStatePayload(string RequestId);

internal sealed record Tier1ItemFilterStateResponsePayload(string RequestId, ItemFilterState State);

internal sealed record Tier1ReloadItemFilterPayload(string RequestId);

internal sealed record Tier1ReloadItemFilterResponsePayload(string RequestId, bool Dispatched);

internal sealed record Tier1EvaluateWorldItemFilterPayload(string RequestId, uint EntityId, bool LiveEvaluate = false);

internal sealed record Tier1EvaluateWorldItemFilterResponsePayload(string RequestId, ItemFilterResult Result);

internal sealed record Tier1QueryCameraStatePayload(string RequestId);

internal sealed record Tier1CameraStateResponsePayload(string RequestId, CameraState State);

internal sealed record Tier1SetCameraZoomOverridePayload(float? ZoomOverride);

// -----------------------------------------------------------------------------
// Session Implementation
// -----------------------------------------------------------------------------

internal sealed class ClientPhase2Session(ClientSession session, ClientBridgeHost host) : IClientPhase2Session
{
    private static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(5);

    private readonly string _sessionNonce = Guid.NewGuid().ToString("N")[..8];
    private readonly List<Func<IClientSession, ClientAreaChangedEvent, CancellationToken, Task>> _areaChangedHandlers = [];
    private readonly List<Func<IClientSession, ItemFilterReloadedEventArgs, CancellationToken, Task>> _filterReloadedHandlers = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyList<ClientWorldEntitySnapshot>>> _pendingWorldEntityQueries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ClientWorldEntitySnapshot?>> _pendingLocalPlayerQueries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ClientEntityStatsSnapshot>> _pendingEntityStatsQueries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ClientStatValueSnapshot>> _pendingStatQueries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AreaInfo?>> _pendingAreaInfoQueries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ItemFilterState>> _pendingFilterStateQueries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingFilterReloadQueries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ItemFilterResult>> _pendingFilterEvalQueries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CameraState>> _pendingCameraStateQueries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private long _nextRequestSeq;
    private bool _closed;

    public Task<IReadOnlyList<ClientWorldEntitySnapshot>> QueryVisibleGroundItemsAsync(
        float? maxDistance = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (maxDistance is float md && (float.IsNaN(md) || float.IsInfinity(md) || md < 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistance), "maxDistance must be a finite non-negative number.");
        }

        return QueryWorldEntitiesAsync(
            new ClientWorldEntityQuery(
                RequiredKind: WorldEntityKind.WorldItem,
                MaxDistance: maxDistance,
                FilterVisibleOnly: true),
            timeout,
            cancellationToken);
    }

    public Task<IReadOnlyList<ClientWorldEntitySnapshot>> QueryWorldEntitiesAsync(
        ClientWorldEntityQuery? query = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (query is { MaxDistance: float md } && (float.IsNaN(md) || float.IsInfinity(md) || md < 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Query MaxDistance must be a finite non-negative number.");
        }

        if (query is { MaxResults: < 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Query MaxResults must be non-negative.");
        }

        return ExecuteQueryAsync(
            _pendingWorldEntityQueries,
            "wld",
            Tier1MessageTypes.QueryWorldEntities,
            requestId => new Tier1QueryWorldEntitiesPayload(requestId, query),
            nameof(QueryWorldEntitiesAsync),
            timeout,
            cancellationToken);
    }

    public Task<ClientWorldEntitySnapshot?> QueryLocalPlayerAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        ExecuteQueryAsync(
            _pendingLocalPlayerQueries,
            "plr",
            Tier1MessageTypes.QueryLocalPlayer,
            requestId => new Tier1QueryLocalPlayerPayload(requestId),
            nameof(QueryLocalPlayerAsync),
            timeout,
            cancellationToken);

    public Task<ClientEntityStatsSnapshot> QueryEntityStatsAsync(
        uint entityId = 0,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        ExecuteQueryAsync(
            _pendingEntityStatsQueries,
            "sts",
            Tier1MessageTypes.QueryEntityStats,
            requestId => new Tier1QueryEntityStatsPayload(requestId, entityId),
            nameof(QueryEntityStatsAsync),
            timeout,
            cancellationToken);

    public Task<ClientStatValueSnapshot> QueryStatAsync(
        int statId,
        uint entityId = 0,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        ExecuteQueryAsync(
            _pendingStatQueries,
            "st1",
            Tier1MessageTypes.QueryStat,
            requestId => new Tier1QueryStatPayload(requestId, statId, entityId),
            nameof(QueryStatAsync),
            timeout,
            cancellationToken);

    public Task<AreaInfo?> QueryAreaInfoAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        ExecuteQueryAsync(
            _pendingAreaInfoQueries,
            "are",
            Tier1MessageTypes.QueryAreaInfo,
            requestId => new Tier1QueryAreaInfoPayload(requestId),
            nameof(QueryAreaInfoAsync),
            timeout,
            cancellationToken);

    public Task<IDisposable> OnAreaChangedAsync(
        Action<IClientSession, ClientAreaChangedEvent> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnAreaChangedAsync(
            (s, evt, _) =>
            {
                handler(s, evt);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    public Task<IDisposable> OnAreaChangedAsync(
        Func<IClientSession, ClientAreaChangedEvent, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnAreaChangedAsync((s, evt, _) => handler(s, evt), cancellationToken);
    }

    public async Task<IDisposable> OnAreaChangedAsync(
        Func<IClientSession, ClientAreaChangedEvent, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        bool shouldEnable;
        lock (_gate)
        {
            if (_closed || session.IsClosed)
            {
                return ClientBridgeHost.NoOpDisposable.Instance;
            }

            shouldEnable = _areaChangedHandlers.Count == 0;
            _areaChangedHandlers.Add(handler);
        }

        if (shouldEnable)
        {
            try
            {
                await session.QueueOrSendSystemMessageAsync(
                    Tier1MessageTypes.SetAreaSubscription,
                    new Tier1SetSubscriptionPayload(Enabled: true),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (_gate)
                {
                    _areaChangedHandlers.Remove(handler);
                }

                throw;
            }
        }

        return new ClientBridgeHost.Subscription(() =>
        {
            bool shouldDisable = false;
            lock (_gate)
            {
                if (_areaChangedHandlers.Remove(handler))
                {
                    shouldDisable = !_closed && !session.IsClosed && _areaChangedHandlers.Count == 0;
                }
            }

            if (shouldDisable)
            {
                SendSubscriptionToggleNonBlocking(Tier1MessageTypes.SetAreaSubscription, enabled: false);
            }
        });
    }

    public Task<ItemFilterState> QueryItemFilterStateAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        ExecuteQueryAsync(
            _pendingFilterStateQueries,
            "fst",
            Tier1MessageTypes.QueryItemFilterState,
            requestId => new Tier1QueryItemFilterStatePayload(requestId),
            nameof(QueryItemFilterStateAsync),
            timeout,
            cancellationToken);

    public Task<bool> ReloadItemFilterAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        ExecuteQueryAsync(
            _pendingFilterReloadQueries,
            "frl",
            Tier1MessageTypes.ReloadItemFilter,
            requestId => new Tier1ReloadItemFilterPayload(requestId),
            nameof(ReloadItemFilterAsync),
            timeout,
            cancellationToken);

    public Task<ItemFilterResult> EvaluateWorldItemFilterAsync(
        uint entityId,
        bool liveEvaluate = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        ExecuteQueryAsync(
            _pendingFilterEvalQueries,
            "fev",
            Tier1MessageTypes.EvaluateWorldItemFilter,
            requestId => new Tier1EvaluateWorldItemFilterPayload(requestId, entityId, liveEvaluate),
            nameof(EvaluateWorldItemFilterAsync),
            timeout,
            cancellationToken);

    public Task<IDisposable> OnFilterReloadedAsync(
        Action<IClientSession, ItemFilterReloadedEventArgs> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnFilterReloadedAsync(
            (s, evt, _) =>
            {
                handler(s, evt);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    public Task<IDisposable> OnFilterReloadedAsync(
        Func<IClientSession, ItemFilterReloadedEventArgs, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnFilterReloadedAsync((s, evt, _) => handler(s, evt), cancellationToken);
    }

    public async Task<IDisposable> OnFilterReloadedAsync(
        Func<IClientSession, ItemFilterReloadedEventArgs, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        bool shouldEnable;
        lock (_gate)
        {
            if (_closed || session.IsClosed)
            {
                return ClientBridgeHost.NoOpDisposable.Instance;
            }

            shouldEnable = _filterReloadedHandlers.Count == 0;
            _filterReloadedHandlers.Add(handler);
        }

        if (shouldEnable)
        {
            try
            {
                await session.QueueOrSendSystemMessageAsync(
                    Tier1MessageTypes.SetFilterSubscription,
                    new Tier1SetSubscriptionPayload(Enabled: true),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (_gate)
                {
                    _filterReloadedHandlers.Remove(handler);
                }

                throw;
            }
        }

        return new ClientBridgeHost.Subscription(() =>
        {
            bool shouldDisable = false;
            lock (_gate)
            {
                if (_filterReloadedHandlers.Remove(handler))
                {
                    shouldDisable = !_closed && !session.IsClosed && _filterReloadedHandlers.Count == 0;
                }
            }

            if (shouldDisable)
            {
                SendSubscriptionToggleNonBlocking(Tier1MessageTypes.SetFilterSubscription, enabled: false);
            }
        });
    }

    public Task<CameraState> QueryCameraStateAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        ExecuteQueryAsync(
            _pendingCameraStateQueries,
            "cam",
            Tier1MessageTypes.QueryCameraState,
            requestId => new Tier1QueryCameraStatePayload(requestId),
            nameof(QueryCameraStateAsync),
            timeout,
            cancellationToken);

    public Task SetCameraZoomOverrideAsync(
        float? zoomOverride,
        CancellationToken cancellationToken = default)
    {
        if (zoomOverride is float z && (float.IsNaN(z) || float.IsInfinity(z)))
        {
            throw new ArgumentOutOfRangeException(nameof(zoomOverride), "Camera zoom override must be a finite number.");
        }

        float? normalized = zoomOverride is float val && val >= 0f ? val : null;
        return session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.SetCameraZoomOverride,
            new Tier1SetCameraZoomOverridePayload(normalized),
            cancellationToken);
    }

    // ---- Incoming Response & Event Dispatch ----

    internal void HandleWorldEntitiesResponse(Tier1WorldEntitiesResponsePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.RequestId) &&
            _pendingWorldEntityQueries.TryRemove(payload.RequestId, out TaskCompletionSource<IReadOnlyList<ClientWorldEntitySnapshot>>? tcs))
        {
            tcs.TrySetResult(payload.Entities ?? []);
        }
    }

    internal void HandleLocalPlayerResponse(Tier1LocalPlayerResponsePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.RequestId) &&
            _pendingLocalPlayerQueries.TryRemove(payload.RequestId, out TaskCompletionSource<ClientWorldEntitySnapshot?>? tcs))
        {
            tcs.TrySetResult(payload.Player);
        }
    }

    internal void HandleEntityStatsResponse(Tier1EntityStatsResponsePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.RequestId) &&
            _pendingEntityStatsQueries.TryRemove(payload.RequestId, out TaskCompletionSource<ClientEntityStatsSnapshot>? tcs))
        {
            tcs.TrySetResult(new ClientEntityStatsSnapshot(payload.EntityId, payload.Stats ?? new Dictionary<int, int>()));
        }
    }

    internal void HandleStatResponse(Tier1StatResponsePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.RequestId) &&
            _pendingStatQueries.TryRemove(payload.RequestId, out TaskCompletionSource<ClientStatValueSnapshot>? tcs))
        {
            tcs.TrySetResult(new ClientStatValueSnapshot(payload.EntityId, payload.StatId, payload.Found, payload.Value));
        }
    }

    internal void HandleAreaInfoResponse(Tier1AreaInfoResponsePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.RequestId) &&
            _pendingAreaInfoQueries.TryRemove(payload.RequestId, out TaskCompletionSource<AreaInfo?>? tcs))
        {
            tcs.TrySetResult(payload.Area);
        }
    }

    internal void HandleItemFilterStateResponse(Tier1ItemFilterStateResponsePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.RequestId) &&
            _pendingFilterStateQueries.TryRemove(payload.RequestId, out TaskCompletionSource<ItemFilterState>? tcs))
        {
            tcs.TrySetResult(payload.State ?? ItemFilterState.Empty);
        }
    }

    internal void HandleReloadItemFilterResponse(Tier1ReloadItemFilterResponsePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.RequestId) &&
            _pendingFilterReloadQueries.TryRemove(payload.RequestId, out TaskCompletionSource<bool>? tcs))
        {
            tcs.TrySetResult(payload.Dispatched);
        }
    }

    internal void HandleEvaluateWorldItemFilterResponse(Tier1EvaluateWorldItemFilterResponsePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.RequestId) &&
            _pendingFilterEvalQueries.TryRemove(payload.RequestId, out TaskCompletionSource<ItemFilterResult>? tcs))
        {
            tcs.TrySetResult(payload.Result ?? ItemFilterResult.Unevaluated);
        }
    }

    internal void HandleCameraStateResponse(Tier1CameraStateResponsePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.RequestId) &&
            _pendingCameraStateQueries.TryRemove(payload.RequestId, out TaskCompletionSource<CameraState>? tcs))
        {
            tcs.TrySetResult(payload.State ?? CameraState.Invalid);
        }
    }

    internal async Task DispatchAreaChangedAsync(ClientAreaChangedEvent evt, CancellationToken cancellationToken)
    {
        Func<IClientSession, ClientAreaChangedEvent, CancellationToken, Task>[] handlers;
        lock (_gate)
        {
            handlers = [.. _areaChangedHandlers];
        }

        foreach (Func<IClientSession, ClientAreaChangedEvent, CancellationToken, Task> handler in handlers)
        {
            try
            {
                await handler(session, evt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                host.ReportHandlerError($"Tier1.AreaChanged({session.ConnectionId})", ex);
            }
        }
    }

    internal async Task DispatchFilterReloadedAsync(ItemFilterReloadedEventArgs evt, CancellationToken cancellationToken)
    {
        Func<IClientSession, ItemFilterReloadedEventArgs, CancellationToken, Task>[] handlers;
        lock (_gate)
        {
            handlers = [.. _filterReloadedHandlers];
        }

        foreach (Func<IClientSession, ItemFilterReloadedEventArgs, CancellationToken, Task> handler in handlers)
        {
            try
            {
                await handler(session, evt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                host.ReportHandlerError($"Tier1.FilterReloaded({session.ConnectionId})", ex);
            }
        }
    }

    internal void CloseSession()
    {
        lock (_gate)
        {
            _closed = true;
            _areaChangedHandlers.Clear();
            _filterReloadedHandlers.Clear();
        }

        CancelAllPending(_pendingWorldEntityQueries);
        CancelAllPending(_pendingLocalPlayerQueries);
        CancelAllPending(_pendingEntityStatsQueries);
        CancelAllPending(_pendingStatQueries);
        CancelAllPending(_pendingAreaInfoQueries);
        CancelAllPending(_pendingFilterStateQueries);
        CancelAllPending(_pendingFilterReloadQueries);
        CancelAllPending(_pendingFilterEvalQueries);
        CancelAllPending(_pendingCameraStateQueries);
    }

    // ---- Shared Request/Response & Subscription Helpers ----

    private async Task<TResult> ExecuteQueryAsync<TPayload, TResult>(
        ConcurrentDictionary<string, TaskCompletionSource<TResult>> pendingMap,
        string prefix,
        string messageType,
        Func<string, TPayload> payloadFactory,
        string operationName,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        if (session.IsClosed || _closed)
        {
            throw new InvalidOperationException($"Cannot execute {operationName} on closed client session '{session.ConnectionId}'.");
        }

        TimeSpan effectiveTimeout = timeout ?? DefaultQueryTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Query timeout must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        string requestId = NextRequestId(prefix);
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingMap[requestId] = tcs;

        try
        {
            await session.SendDirectAsync(
                MessageCodec.SystemChannel,
                payloadFactory(requestId),
                messageType,
                cancellationToken).ConfigureAwait(false);

            return await WaitForQueryAsync(tcs.Task, effectiveTimeout, operationName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pendingMap.TryRemove(requestId, out _);
        }
    }

    private string NextRequestId(string prefix)
    {
        long seq = Interlocked.Increment(ref _nextRequestSeq);
        return $"{prefix}:{_sessionNonce}:{seq}";
    }

    private static void CancelAllPending<T>(ConcurrentDictionary<string, TaskCompletionSource<T>> map)
    {
        foreach (KeyValuePair<string, TaskCompletionSource<T>> pending in map)
        {
            if (map.TryRemove(pending.Key, out TaskCompletionSource<T>? tcs))
            {
                tcs.TrySetCanceled();
            }
        }
    }

    private static async Task<T> WaitForQueryAsync<T>(
        Task<T> responseTask,
        TimeSpan timeout,
        string operationName,
        CancellationToken cancellationToken)
    {
        if (responseTask.IsCompleted)
        {
            return await responseTask.ConfigureAwait(false);
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            return await responseTask.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"{operationName} timed out after {timeout.TotalMilliseconds:F0} ms.");
        }
    }

    private void SendSubscriptionToggleNonBlocking(string messageType, bool enabled)
    {
        Task sendTask = session.QueueOrSendSystemMessageAsync(
            messageType,
            new Tier1SetSubscriptionPayload(enabled),
            CancellationToken.None);

        if (sendTask.IsFaulted && sendTask.Exception is not null)
        {
            host.ReportHandlerError($"Tier1.{messageType}({session.ConnectionId})", sendTask.Exception.GetBaseException());
        }
        else if (!sendTask.IsCompleted)
        {
            _ = sendTask.ContinueWith(
                t => host.ReportHandlerError($"Tier1.{messageType}({session.ConnectionId})", t.Exception?.GetBaseException() ?? new InvalidOperationException("Subscription update failed.")),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
