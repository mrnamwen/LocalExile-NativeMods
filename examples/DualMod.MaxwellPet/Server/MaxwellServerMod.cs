using System.Collections.Concurrent;
using System.Globalization;
using DualMod.MaxwellPet.Shared;
using LocalExile.Client.ServerBridge;

namespace DualMod.MaxwellPet.Server;

/// <summary>
/// Abstraction bridging <see cref="MaxwellServerMod"/> to either a live LocalExile Gateway
/// (<c>MaxwellLocalExileEntry</c>) or an in-memory test harness (<see cref="SimulatedMaxwellWorldBackend"/>).
/// </summary>
public interface IMaxwellWorldBackend
{
    /// <summary>Registers Maxwell as an appended Pet MTX entry in the server's MTX/Cosmetics catalogs.</summary>
    IDisposable RegisterPetMtx(CustomPetMtxDefinition pet);

    /// <summary>Grants the appended Maxwell MTX record to the connected client (S-&gt;C <c>0x0204</c>).</summary>
    Task GrantMtxRecordAsync(string connectionId, CustomPetMtxDefinition pet, CancellationToken cancellationToken = default);

    /// <summary>Spawns the authoritative pet entity (S-&gt;C <c>0x02bb</c> <c>PetObject</c> + <c>0x01a9</c> wake zone).</summary>
    Task<uint> SpawnPetEntityAsync(string connectionId, string characterName, string petMetadataPath, byte petSlot, CancellationToken cancellationToken = default);

    /// <summary>Despawns a previously spawned pet entity (S-&gt;C <c>0x02bd</c>).</summary>
    Task DespawnPetEntityAsync(string connectionId, uint petEntityId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default thread-safe in-memory backend used for standalone hosting and unit/E2E verification without a live Gateway.
/// </summary>
public sealed class SimulatedMaxwellWorldBackend : IMaxwellWorldBackend
{
    private readonly ConcurrentDictionary<uint, SpawnedPetRecord> _spawned = new();
    private readonly ConcurrentDictionary<string, List<uint>> _grantedRecords = new(StringComparer.Ordinal);
    private int _nextEntityId = 0x5000;
    private CustomPetMtxDefinition? _registeredMtx;

    /// <summary>Snapshot of a spawned authoritative pet entity.</summary>
    public sealed record SpawnedPetRecord(uint EntityId, string ConnectionId, string CharacterName, string PetMetadataPath, byte PetSlot);

    /// <summary>Currently registered Maxwell Pet MTX catalog entry, or <c>null</c> after dispose.</summary>
    public CustomPetMtxDefinition? RegisteredMtx => _registeredMtx;

    /// <summary>All currently active spawned Maxwell pet entities.</summary>
    public IReadOnlyDictionary<uint, SpawnedPetRecord> SpawnedPets => _spawned;

    /// <summary>Returns all MTX record IDs granted to <paramref name="connectionId"/>.</summary>
    public IReadOnlyList<uint> GetGrantedRecords(string connectionId) =>
        _grantedRecords.TryGetValue(connectionId, out var list) ? [.. list] : [];

    public IDisposable RegisterPetMtx(CustomPetMtxDefinition pet)
    {
        ArgumentNullException.ThrowIfNull(pet);
        pet.Validate();
        _registeredMtx = pet;
        return new ActionDisposable(() => Interlocked.CompareExchange(ref _registeredMtx, null, pet));
    }

    public Task GrantMtxRecordAsync(string connectionId, CustomPetMtxDefinition pet, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(pet);
        var list = _grantedRecords.GetOrAdd(connectionId, _ => []);
        lock (list)
        {
            if (!list.Contains(pet.RecordId))
                list.Add(pet.RecordId);
        }
        return Task.CompletedTask;
    }

    public Task<uint> SpawnPetEntityAsync(string connectionId, string characterName, string petMetadataPath, byte petSlot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(petMetadataPath);
        uint id = (uint)Interlocked.Increment(ref _nextEntityId);
        _spawned[id] = new SpawnedPetRecord(id, connectionId, characterName, petMetadataPath, petSlot);
        return Task.FromResult(id);
    }

    public Task DespawnPetEntityAsync(string connectionId, uint petEntityId, CancellationToken cancellationToken = default)
    {
        _spawned.TryRemove(petEntityId, out _);
        return Task.CompletedTask;
    }

    private sealed class ActionDisposable(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;
        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}

/// <summary>
/// Server half of the Maxwell the Cat Pet MTX dual-side mod.
/// Uses <see cref="CustomPetMtxDefinition"/> and <see cref="LocalExilePetMtxRegistrar"/> from
/// <c>LocalExile.Client.ServerBridge</c> so Maxwell works seamlessly both via the in-game
/// Cosmetics (<c>K</c>) menu (Pet Slots 18 &amp; 19) and via <c>/maxwell</c> / <c>MaxwellPanel.ui</c>,
/// with graceful chat fallback for vanilla 3.29 clients.
/// </summary>
public sealed class MaxwellServerMod : IDisposable
{
    public static readonly CustomPetMtxDefinition MaxwellPetDefinition = new()
    {
        MtxTypesRowIndex = MaxwellProtocol.MtxTypesAppendedRow,
        MtxTypeGameSpecificRowIndex = MaxwellProtocol.MtxTypeGameSpecificAppendedRow,
        PetRowIndex = MaxwellProtocol.PetAppendedRow,
        MtxTypeId = MaxwellProtocol.MtxTypeId,
        DisplayName = MaxwellProtocol.MtxDisplayName,
        Description = MaxwellProtocol.MtxDescription,
        PetMetadataPath = MaxwellProtocol.PetMetadataPath,
        IconDdsPath = MaxwellProtocol.PetIconPath,
        AllowedSlots = [CustomPetMtxDefinition.PetSlot1, CustomPetMtxDefinition.PetSlot2]
    };

    private readonly ClientBridgeHost _bridge;
    private readonly IMaxwellWorldBackend _backend;
    private readonly LocalExilePetMtxRegistrar _registrar;
    private readonly bool _ownsRegistrar;
    private readonly ConcurrentDictionary<string, CharacterMaxwellState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _connectionCharacters = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];
    private IDisposable? _mtxRegistrationHandle;
    private IDisposable? _registrarDefHandle;

    public IMaxwellWorldBackend Backend => _backend;
    public LocalExilePetMtxRegistrar PetMtxRegistrar => _registrar;

    public MaxwellServerMod(
        ClientBridgeHost bridge,
        IMaxwellWorldBackend? backend = null,
        LocalExilePetMtxRegistrar? registrar = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _backend = backend ?? new SimulatedMaxwellWorldBackend();
        _registrar = registrar ?? new LocalExilePetMtxRegistrar();
        _ownsRegistrar = registrar is null;
    }

    /// <summary>
    /// Registers Maxwell as a brand-new Pet MTX and attaches typed <see cref="ClientBridgeHost"/>
    /// and <see cref="LocalExilePetMtxRegistrar"/> Cosmetics (<c>K</c>) equip handlers.
    /// </summary>
    public void Attach()
    {
        if (_subscriptions.Count > 0)
            return;

        _registrarDefHandle = _registrar.Register(MaxwellPetDefinition);
        _mtxRegistrationHandle = _backend.RegisterPetMtx(MaxwellPetDefinition);
        _registrar.PetEquipChanged += OnCosmeticEquipObserved;

        _subscriptions.Add(_bridge.On<ToggleMaxwellRequest>(MaxwellProtocol.ChannelName, (session, req) =>
        {
            if (!session.HasClientMod(MaxwellProtocol.ModId))
                return;

            string character = _connectionCharacters.GetValueOrDefault(session.ConnectionId, session.ConnectionId);
            ApplyToggleRequestAsync(session.ConnectionId, character, req).GetAwaiter().GetResult();
        }));

        _subscriptions.Add(_bridge.On<MaxwellAudioControl>(MaxwellProtocol.ChannelName, (session, req) =>
        {
            if (!session.HasClientMod(MaxwellProtocol.ModId))
                return;

            string character = _connectionCharacters.GetValueOrDefault(session.ConnectionId, session.ConnectionId);
            ApplyAudioControlAsync(session.ConnectionId, character, req).GetAwaiter().GetResult();
        }));
    }

    public void Detach()
    {
        _registrar.PetEquipChanged -= OnCosmeticEquipObserved;

        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();

        _mtxRegistrationHandle?.Dispose();
        _mtxRegistrationHandle = null;

        _registrarDefHandle?.Dispose();
        _registrarDefHandle = null;

        if (_ownsRegistrar)
            _registrar.Dispose();
    }

    public void Dispose() => Detach();

    /// <summary>
    /// Called when a player enters an area: grants Maxwell Pet MTX ownership (<c>0x0204</c>),
    /// respawns Maxwell if currently active via <c>/maxwell</c>, and syncs rich clients.
    /// </summary>
    public async Task OnPlayerEnteredAsync(string connectionId, string characterName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);

        _connectionCharacters[connectionId] = characterName;
        await _backend.GrantMtxRecordAsync(connectionId, MaxwellPetDefinition, cancellationToken).ConfigureAwait(false);

        var state = _states.GetOrAdd(characterName, _ => new CharacterMaxwellState());
        bool shouldRespawn;
        byte slot;
        lock (state)
        {
            // When summoned via native Cosmetics (K), LocalExile's CosmeticsModule already spawns the pet on PlayerEntered
            // and LocalExilePetMtxRegistrar.ObserveLiveGatewayPlayer notifies OnCosmeticEquipObservedAsync with its entity ID.
            shouldRespawn = state.IsSummoned && !state.EquippedViaCosmeticsMenu;
            slot = state.PetSlot;
        }

        if (shouldRespawn)
        {
            uint newEntityId = await _backend.SpawnPetEntityAsync(
                connectionId,
                characterName,
                MaxwellProtocol.PetMetadataPath,
                slot,
                cancellationToken).ConfigureAwait(false);

            MaxwellStateSync sync;
            lock (state)
            {
                state.PetEntityId = newEntityId;
                sync = BuildSyncLocked(characterName, state, $"Maxwell followed {characterName} into the area (entity 0x{newEntityId:X}).");
            }

            await PushSyncIfRichClientAsync(connectionId, sync, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles a Cosmetics (<c>K</c>) menu equip/unequip transition observed by <see cref="LocalExilePetMtxRegistrar"/>.
    /// Does NOT double-spawn the pet entity (LocalExile's <c>CosmeticsFeature.SpawnPets</c> owns the entity);
    /// updates authoritative state and pushes <see cref="MaxwellStateSync"/> so NativeMods clients start/stop
    /// looping Stock Market audio and update <c>MaxwellPanel.ui</c> immediately.
    /// </summary>
    public async Task<MaxwellStateSync> OnCosmeticEquipObservedAsync(
        PetMtxEquipChangedEvent evt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        _connectionCharacters[evt.ConnectionId] = evt.CharacterName;

        var state = _states.GetOrAdd(evt.CharacterName, _ => new CharacterMaxwellState());
        MaxwellStateSync sync;
        lock (state)
        {
            if (evt.IsEquipped)
            {
                state.IsSummoned = true;
                state.EquippedViaCosmeticsMenu = true;
                state.PetEntityId = evt.PetEntityId;
                state.PetSlot = evt.PetSlot;
                state.MusicPlaying = true;
                sync = BuildSyncLocked(evt.CharacterName, state,
                    $"Equipped Maxwell the Cat from Cosmetics Pet Slot {(evt.PetSlot == CustomPetMtxDefinition.PetSlot1 ? 1 : 2)} (entity 0x{evt.PetEntityId:X}).");
            }
            else
            {
                state.IsSummoned = false;
                state.EquippedViaCosmeticsMenu = false;
                state.PetEntityId = 0;
                state.MusicPlaying = false;
                sync = BuildSyncLocked(evt.CharacterName, state,
                    "Unequipped Maxwell the Cat from Cosmetics. Stock Market music stopped.");
            }
        }

        await PushSyncIfRichClientAsync(evt.ConnectionId, sync, cancellationToken).ConfigureAwait(false);
        return sync;
    }

    /// <summary>
    /// Handles a direct Cosmetics tab pick (<c>C-&gt;S 0x01f9</c>) in standalone/simulated environments
    /// where LocalExile's <c>CosmeticsModule</c> is not driving <c>SpawnPets</c>.
    /// </summary>
    public async Task<MaxwellStateSync?> HandleCosmeticSlotPickedAsync(
        string connectionId,
        string characterName,
        byte slot,
        uint recordId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);

        if (slot is not (CustomPetMtxDefinition.PetSlot1 or CustomPetMtxDefinition.PetSlot2))
            return null;

        _connectionCharacters[connectionId] = characterName;
        if (recordId == MaxwellPetDefinition.RecordId)
        {
            return await SummonAsync(connectionId, characterName, slot, cancellationToken).ConfigureAwait(false);
        }

        var state = _states.GetOrAdd(characterName, _ => new CharacterMaxwellState());
        bool ownsSlot;
        lock (state)
        {
            ownsSlot = state.IsSummoned && state.PetSlot == slot;
        }

        return ownsSlot
            ? await DismissAsync(connectionId, characterName, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// Handles the <c>/maxwell</c> chat command with full NativeMods UI sync and graceful chat fallback for vanilla clients.
    /// </summary>
    public async Task<string> HandleMaxwellCommandAsync(
        string connectionId,
        string characterName,
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        args ??= [];

        _connectionCharacters[connectionId] = characterName;

        var session = _bridge.For(connectionId);
        bool hasRichClient = session.HasNativeHost && session.HasClientMod(MaxwellProtocol.ModId);

        if (args.Length == 0 || args[0].Equals("panel", StringComparison.OrdinalIgnoreCase))
        {
            var current = BuildSync(characterName, $"Maxwell the Cat Pet MTX ready for {characterName}.");
            if (hasRichClient)
            {
                await session.SendAsync(
                    MaxwellProtocol.ChannelName,
                    new OpenMaxwellPanel(characterName, current),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return $"[Maxwell Pet] Opened Maxwell control panel (summoned={current.IsSummoned}, spin={current.SpinSpeedDegPerSec:0} deg/s, float={current.FloatHeight:0.#}, music={(current.MusicPlaying && !current.MusicMuted ? "on" : "off")}).";
            }

            return BuildChatFallbackSummary(current);
        }

        string verb = args[0].ToLowerInvariant();
        switch (verb)
        {
            case "summon":
            case "on":
            {
                byte slot = args.Length > 1 && args[1] == "2" ? CustomPetMtxDefinition.PetSlot2 : CustomPetMtxDefinition.PetSlot1;
                var sync = await SummonAsync(connectionId, characterName, slot, cancellationToken).ConfigureAwait(false);
                return hasRichClient ? sync.StatusMessage : $"{sync.StatusMessage} [Chat Fallback]";
            }

            case "dismiss":
            case "off":
            {
                var sync = await DismissAsync(connectionId, characterName, cancellationToken).ConfigureAwait(false);
                return hasRichClient ? sync.StatusMessage : $"{sync.StatusMessage} [Chat Fallback]";
            }

            case "toggle":
            {
                var sync = await ApplyToggleRequestAsync(connectionId, characterName, new ToggleMaxwellRequest(), cancellationToken).ConfigureAwait(false);
                return hasRichClient ? sync.StatusMessage : $"{sync.StatusMessage} [Chat Fallback]";
            }

            case "spin":
            {
                if (args.Length < 2 || !float.TryParse(args[1], CultureInfo.InvariantCulture, out float speed))
                    return "[Maxwell Pet] Usage: /maxwell spin <0..720>";

                var sync = await ApplyToggleRequestAsync(connectionId, characterName, new ToggleMaxwellRequest(SpinSpeedDegPerSec: speed), cancellationToken).ConfigureAwait(false);
                return sync.StatusMessage;
            }

            case "float":
            {
                if (args.Length < 2 || !float.TryParse(args[1], CultureInfo.InvariantCulture, out float height))
                    return "[Maxwell Pet] Usage: /maxwell float <0..60>";

                var sync = await ApplyToggleRequestAsync(connectionId, characterName, new ToggleMaxwellRequest(FloatHeight: height), cancellationToken).ConfigureAwait(false);
                return sync.StatusMessage;
            }

            case "music":
            {
                if (args.Length < 2)
                    return "[Maxwell Pet] Usage: /maxwell music <on|off|mute|unmute|vol 0..1>";

                string mode = args[1].ToLowerInvariant();
                MaxwellAudioControl? control = mode switch
                {
                    "on" or "play" => new MaxwellAudioControl(Play: true, Muted: false),
                    "off" or "stop" => new MaxwellAudioControl(Play: false),
                    "mute" => new MaxwellAudioControl(Muted: true),
                    "unmute" => new MaxwellAudioControl(Muted: false),
                    "vol" or "volume" when args.Length >= 3 && float.TryParse(args[2], CultureInfo.InvariantCulture, out float vol)
                        => new MaxwellAudioControl(Volume: vol),
                    _ => null
                };

                if (control is null)
                    return "[Maxwell Pet] Usage: /maxwell music <on|off|mute|unmute|vol 0..1>";

                var sync = await ApplyAudioControlAsync(connectionId, characterName, control, cancellationToken).ConfigureAwait(false);
                return sync.StatusMessage;
            }

            case "dance":
            {
                var state = _states.GetOrAdd(characterName, _ => new CharacterMaxwellState());
                MaxwellStateSync sync;
                lock (state)
                {
                    state.DanceCount++;
                    state.SpinSpeedDegPerSec = Math.Clamp(state.SpinSpeedDegPerSec + 45f, MaxwellProtocol.MinSpinSpeedDegPerSec, MaxwellProtocol.MaxSpinSpeedDegPerSec);
                    sync = BuildSyncLocked(characterName, state, $"Maxwell dances! (dance #{state.DanceCount}, spin={state.SpinSpeedDegPerSec:0} deg/s)");
                }
                await PushSyncIfRichClientAsync(connectionId, sync, cancellationToken).ConfigureAwait(false);
                return sync.StatusMessage;
            }

            case "status":
                return BuildChatFallbackSummary(BuildSync(characterName, "Maxwell status snapshot."));

            default:
                return "[Maxwell Pet] Commands: /maxwell [panel|summon [1|2]|dismiss|toggle|spin <0..720>|float <0..60>|music <on|off|mute|unmute|vol 0..1>|dance|status]";
        }
    }

    public async Task<MaxwellStateSync> SummonAsync(
        string connectionId,
        string characterName,
        byte petSlot = CustomPetMtxDefinition.PetSlot1,
        CancellationToken cancellationToken = default)
    {
        byte normalizedSlot = petSlot == CustomPetMtxDefinition.PetSlot2 ? CustomPetMtxDefinition.PetSlot2 : CustomPetMtxDefinition.PetSlot1;
        var state = _states.GetOrAdd(characterName, _ => new CharacterMaxwellState());

        uint previousEntityId;
        lock (state)
        {
            previousEntityId = state.IsSummoned ? state.PetEntityId : 0u;
        }

        if (previousEntityId != 0)
        {
            await _backend.DespawnPetEntityAsync(connectionId, previousEntityId, cancellationToken).ConfigureAwait(false);
        }

        uint entityId = await _backend.SpawnPetEntityAsync(
            connectionId,
            characterName,
            MaxwellProtocol.PetMetadataPath,
            normalizedSlot,
            cancellationToken).ConfigureAwait(false);

        MaxwellStateSync sync;
        lock (state)
        {
            state.IsSummoned = true;
            state.EquippedViaCosmeticsMenu = false;
            state.PetEntityId = entityId;
            state.PetSlot = normalizedSlot;
            state.MusicPlaying = true;
            sync = BuildSyncLocked(characterName, state,
                $"Summoned Maxwell the Cat in Pet Slot {(normalizedSlot == CustomPetMtxDefinition.PetSlot1 ? 1 : 2)} (entity 0x{entityId:X}).");
        }

        await PushSyncIfRichClientAsync(connectionId, sync, cancellationToken).ConfigureAwait(false);
        return sync;
    }

    public async Task<MaxwellStateSync> DismissAsync(
        string connectionId,
        string characterName,
        CancellationToken cancellationToken = default)
    {
        var state = _states.GetOrAdd(characterName, _ => new CharacterMaxwellState());

        uint entityToRemove;
        lock (state)
        {
            entityToRemove = state.PetEntityId;
            state.IsSummoned = false;
            state.EquippedViaCosmeticsMenu = false;
            state.PetEntityId = 0;
            state.MusicPlaying = false;
        }

        if (entityToRemove != 0)
        {
            await _backend.DespawnPetEntityAsync(connectionId, entityToRemove, cancellationToken).ConfigureAwait(false);
        }

        MaxwellStateSync sync;
        lock (state)
        {
            sync = BuildSyncLocked(characterName, state, "Dismissed Maxwell the Cat. Stock Market music stopped.");
        }

        await PushSyncIfRichClientAsync(connectionId, sync, cancellationToken).ConfigureAwait(false);
        return sync;
    }

    public async Task<MaxwellStateSync> ApplyToggleRequestAsync(
        string connectionId,
        string characterName,
        ToggleMaxwellRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = _states.GetOrAdd(characterName, _ => new CharacterMaxwellState());

        bool hasParameterChange = request.SpinSpeedDegPerSec.HasValue || request.FloatHeight.HasValue;
        bool? targetSummon = request.Summon;
        if (!targetSummon.HasValue && !hasParameterChange)
        {
            lock (state)
            {
                targetSummon = !state.IsSummoned;
            }
        }

        lock (state)
        {
            if (request.SpinSpeedDegPerSec is float spin)
                state.SpinSpeedDegPerSec = Math.Clamp(spin, MaxwellProtocol.MinSpinSpeedDegPerSec, MaxwellProtocol.MaxSpinSpeedDegPerSec);
            if (request.FloatHeight is float height)
                state.FloatHeight = Math.Clamp(height, MaxwellProtocol.MinFloatHeight, MaxwellProtocol.MaxFloatHeight);
        }

        if (targetSummon == true)
            return await SummonAsync(connectionId, characterName, request.PetSlot, cancellationToken).ConfigureAwait(false);
        if (targetSummon == false)
            return await DismissAsync(connectionId, characterName, cancellationToken).ConfigureAwait(false);

        MaxwellStateSync sync;
        lock (state)
        {
            sync = BuildSyncLocked(characterName, state,
                $"Updated Maxwell motion: spin={state.SpinSpeedDegPerSec:0} deg/s, float={state.FloatHeight:0.#}.");
        }

        await PushSyncIfRichClientAsync(connectionId, sync, cancellationToken).ConfigureAwait(false);
        return sync;
    }

    public async Task<MaxwellStateSync> ApplyAudioControlAsync(
        string connectionId,
        string characterName,
        MaxwellAudioControl control,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(control);
        var state = _states.GetOrAdd(characterName, _ => new CharacterMaxwellState());

        MaxwellStateSync sync;
        lock (state)
        {
            if (control.Play is bool play)
                state.MusicPlaying = play;
            if (control.Muted is bool muted)
                state.MusicMuted = muted;
            if (control.Volume is float volume)
                state.MusicVolume = Math.Clamp(volume, 0f, 1f);

            string playback = !state.MusicPlaying ? "stopped" : state.MusicMuted ? "muted" : "playing (loop)";
            sync = BuildSyncLocked(characterName, state,
                $"Stock Market music: {playback}, volume={(int)MathF.Round(state.MusicVolume * 100f)}%.");
        }

        await PushSyncIfRichClientAsync(connectionId, sync, cancellationToken).ConfigureAwait(false);
        return sync;
    }

    public MaxwellStateSync BuildSync(string characterName, string statusMessage)
    {
        var state = _states.GetOrAdd(characterName, _ => new CharacterMaxwellState());
        lock (state)
        {
            return BuildSyncLocked(characterName, state, statusMessage);
        }
    }

    private void OnCosmeticEquipObserved(PetMtxEquipChangedEvent evt)
    {
        if (evt.Pet.MtxTypeHash != MaxwellPetDefinition.MtxTypeHash)
            return;

        OnCosmeticEquipObservedAsync(evt).GetAwaiter().GetResult();
    }

    private async Task PushSyncIfRichClientAsync(string connectionId, MaxwellStateSync sync, CancellationToken cancellationToken)
    {
        var session = _bridge.For(connectionId);
        if (session.HasNativeHost && session.HasClientMod(MaxwellProtocol.ModId))
        {
            await session.SendAsync(MaxwellProtocol.ChannelName, sync, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static MaxwellStateSync BuildSyncLocked(string characterName, CharacterMaxwellState state, string status) =>
        new(
            CharacterName: characterName,
            IsSummoned: state.IsSummoned,
            PetEntityId: state.PetEntityId,
            PetSlot: state.PetSlot,
            SpinSpeedDegPerSec: state.SpinSpeedDegPerSec,
            FloatHeight: state.FloatHeight,
            MusicPlaying: state.MusicPlaying,
            MusicMuted: state.MusicMuted,
            MusicVolume: state.MusicVolume,
            DanceCount: state.DanceCount,
            StatusMessage: status);

    private static string BuildChatFallbackSummary(MaxwellStateSync state) =>
        $"[Maxwell Pet — Chat Fallback] Summoned: {(state.IsSummoned ? $"Yes (0x{state.PetEntityId:X}, Slot {(state.PetSlot == CustomPetMtxDefinition.PetSlot1 ? 1 : 2)})" : "No")} | " +
        $"Spin: {state.SpinSpeedDegPerSec:0} deg/s | Float: {state.FloatHeight:0.#} | " +
        $"Music: {(state.MusicPlaying && !state.MusicMuted ? $"On ({(int)MathF.Round(state.MusicVolume * 100f)}%)" : "Off")} | " +
        "Use '/maxwell summon|dismiss|spin|float|music' or equip Maxwell the Cat Pet in Cosmetics (K). Install NativeMods via Waypoint for the native panel & looping audio.";

    private sealed class CharacterMaxwellState
    {
        public bool IsSummoned { get; set; }
        public bool EquippedViaCosmeticsMenu { get; set; }
        public uint PetEntityId { get; set; }
        public byte PetSlot { get; set; } = CustomPetMtxDefinition.PetSlot1;
        public float SpinSpeedDegPerSec { get; set; } = MaxwellProtocol.DefaultSpinSpeedDegPerSec;
        public float FloatHeight { get; set; } = MaxwellProtocol.DefaultFloatHeight;
        public bool MusicPlaying { get; set; }
        public bool MusicMuted { get; set; }
        public float MusicVolume { get; set; } = MaxwellProtocol.DefaultMusicVolume;
        public int DanceCount { get; set; }
    }
}
