namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Plays FMOD Studio events and loose/overlaid <c>.ogg</c> audio files through the client's audio manager.
/// </summary>
public interface IAudioService
{
    /// <summary>Gets the number of currently active audio channels (client limit is 0x200).</summary>
    int ActiveChannelCount { get; }

    /// <summary>Plays a named FMOD Studio event (for example <c>event:/Harvest/PlotReady</c>).</summary>
    /// <param name="fmodEventPath">FMOD event path.</param>
    /// <param name="volume">Linear volume multiplier (0.0 to 1.0).</param>
    /// <returns>An opaque channel handle.</returns>
    int PlayEvent(string fmodEventPath, float volume = 1.0f);

    /// <summary>Plays an <c>.ogg</c> audio file resolved through the client's file layer.</summary>
    /// <param name="virtualOggPath">Virtual path to the <c>.ogg</c> file.</param>
    /// <param name="volume">Linear volume multiplier (0.0 to 1.0).</param>
    /// <returns>An opaque channel handle.</returns>
    int PlaySoundFile(string virtualOggPath, float volume = 1.0f);

    /// <summary>
    /// Plays an <c>.ogg</c> audio file resolved through the client's file overlay or disk with optional infinite looping.
    /// </summary>
    /// <param name="virtualOggPath">Virtual path to the <c>.ogg</c> file (e.g. <c>Audio/Mods/Maxwell/stock_market.ogg</c>).</param>
    /// <param name="volume">Linear volume multiplier (0.0 to 1.0).</param>
    /// <param name="loop"><c>true</c> to loop continuously until stopped via <see cref="StopChannel"/>.</param>
    /// <returns>An opaque channel handle.</returns>
    int PlaySoundFile(string virtualOggPath, float volume, bool loop) => PlaySoundFile(virtualOggPath, volume);

    /// <summary>Updates the linear volume (0.0 to 1.0) of an active audio channel.</summary>
    void SetChannelVolume(int channelHandle, float volume) { }

    /// <summary>Returns <c>true</c> while the specified audio channel is actively playing.</summary>
    bool IsChannelPlaying(int channelHandle) => false;

    /// <summary>Stops an active audio channel by handle.</summary>
    void StopChannel(int channelHandle);
}

/// <summary>
/// Reads client stats and sets display-only local stats (synchronously notifying client listeners).
/// Local stat writes are invisible to the server and overwritten on the next server sync.
/// </summary>
public interface IStatsService
{
    /// <summary>Reads a client stat by key, returning <paramref name="defaultValue"/> if unset.</summary>
    int GetStat(string statKey, int defaultValue = 0);

    /// <summary>Sets a display-only local stat and notifies any registered listeners.</summary>
    void SetLocalStat(string statKey, int value);

    /// <summary>Subscribes to changes for a specific stat key.</summary>
    IDisposable OnStatChanged(string statKey, Action<int> listener);

    /// <summary>
    /// Reads a live numeric stat (<c>StatsComponent</c>) on the given world entity, returning <c>0</c> if absent or unavailable.
    /// Passing <c>0</c> for <paramref name="entityId"/> reads from the local player entity.
    /// </summary>
    /// <param name="entityId">World entity id, or <c>0</c> for the local player.</param>
    /// <param name="statId">Numeric stat identifier (supports high-ID custom stats <c>&gt;= 0x10000</c>).</param>
    int ReadStat(uint entityId, int statId) => TryReadStat(entityId, statId, out int value) ? value : 0;

    /// <summary>
    /// Attempts to read a live numeric stat (<c>StatsComponent</c>) on the given world entity.
    /// Passing <c>0</c> for <paramref name="entityId"/> reads from the local player entity.
    /// </summary>
    /// <param name="entityId">World entity id, or <c>0</c> for the local player.</param>
    /// <param name="statId">Numeric stat identifier (supports high-ID custom stats <c>&gt;= 0x10000</c>).</param>
    /// <param name="value">Receives the stat value when found.</param>
    /// <returns><c>true</c> if the entity and stat were resolved; otherwise <c>false</c>.</returns>
    bool TryReadStat(uint entityId, int statId, out int value)
    {
        value = 0;
        return false;
    }

    /// <summary>
    /// Sets a display-only local numeric stat on the given world entity and notifies native client stat listeners.
    /// Passing <c>0</c> for <paramref name="entityId"/> targets the local player entity.
    /// </summary>
    /// <param name="entityId">World entity id, or <c>0</c> for the local player.</param>
    /// <param name="statId">Numeric stat identifier (supports high-ID custom stats <c>&gt;= 0x10000</c>).</param>
    /// <param name="value">New local stat value.</param>
    void SetLocalStat(uint entityId, int statId, int value) { }

    /// <summary>
    /// Enumerates all numeric stats currently present on the given world entity's <c>StatsComponent</c>,
    /// including high-ID (<c>&gt;= 0x10000</c>) custom stats. Passing <c>0</c> targets the local player entity.
    /// </summary>
    /// <param name="entityId">World entity id, or <c>0</c> for the local player.</param>
    IReadOnlyDictionary<int, int> GetAllStats(uint entityId) => new Dictionary<int, int>();

    /// <summary>Reads a live numeric stat on the local player entity (convenience alias for <see cref="ReadStat(uint, int)"/> with <c>entityId = 0</c>).</summary>
    /// <param name="statId">Numeric stat identifier.</param>
    int ReadLocalPlayerStat(int statId) => ReadStat(0u, statId);

    /// <summary>Attempts to read a live numeric stat on the local player entity.</summary>
    /// <param name="statId">Numeric stat identifier.</param>
    /// <param name="value">Receives the stat value when found.</param>
    bool TryReadLocalPlayerStat(int statId, out int value) => TryReadStat(0u, statId, out value);

    /// <summary>Enumerates all numeric stats on the local player entity (convenience alias for <see cref="GetAllStats(uint)"/> with <c>entityId = 0</c>).</summary>
    IReadOnlyDictionary<int, int> GetLocalPlayerStats() => GetAllStats(0u);
}

/// <summary>
/// 3D world-space coordinate in Path of Exile client world units.
/// </summary>
/// <param name="X">World X coordinate.</param>
/// <param name="Y">World Y coordinate.</param>
/// <param name="Z">World Z (elevation) coordinate.</param>
public readonly record struct WorldPosition3D(float X, float Y, float Z)
{
    /// <summary>Computes the 3D Euclidean distance from this position to <paramref name="other"/>.</summary>
    public float DistanceTo(WorldPosition3D other)
    {
        float dx = X - other.X;
        float dy = Y - other.Y;
        float dz = Z - other.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>Computes the 2D XY-plane distance from this position to <paramref name="other"/>.</summary>
    public float Distance2DTo(WorldPosition3D other)
    {
        float dx = X - other.X;
        float dy = Y - other.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }
}

/// <summary>
/// Component and classification flags describing a live world entity in the active client area.
/// </summary>
[Flags]
public enum WorldEntityKind : uint
{
    /// <summary>No classification flags set.</summary>
    None = 0u,

    /// <summary>Entity has a <c>Player</c> component.</summary>
    Player = 1u << 0,

    /// <summary>Entity is a monster or combat actor.</summary>
    Monster = 1u << 1,

    /// <summary>Entity is a ground loot drop with a <c>WorldItem</c> component.</summary>
    WorldItem = 1u << 2,

    /// <summary>Entity has a <c>Chest</c> component.</summary>
    Chest = 1u << 3,

    /// <summary>Entity has an <c>NPC</c> component.</summary>
    Npc = 1u << 4,

    /// <summary>Entity has a <c>Portal</c> component.</summary>
    Portal = 1u << 5,

    /// <summary>Entity has an <c>AreaTransition</c> component.</summary>
    AreaTransition = 1u << 6,

    /// <summary>Entity has a <c>MinimapIcon</c> component.</summary>
    MinimapIcon = 1u << 7,

    /// <summary>Entity is currently targetable by the player.</summary>
    Targetable = 1u << 8,

    /// <summary>Entity has a <c>Life</c> component with current HP &gt; 0.</summary>
    Alive = 1u << 9,

    /// <summary>Chest or transitionable entity is already opened.</summary>
    Opened = 1u << 10,

    /// <summary>Entity is the local player entity.</summary>
    LocalPlayer = 1u << 11,

    /// <summary>Entity was spawned locally on the client (<c>EntityId &gt;= 0x40000000</c>).</summary>
    ClientOnly = 1u << 12,
}

/// <summary>
/// Immutable snapshot describing a live entity in the active client world (<c>EntityManager</c>).
/// </summary>
/// <param name="EntityId">Unique world entity identifier (<c>&lt; 0x40000000</c> server, <c>&gt;= 0x40000000</c> client-only).</param>
/// <param name="EntityHandle">Native <c>Entity*</c> pointer (or <see cref="nint.Zero"/> when simulated).</param>
/// <param name="Kind">Classification and state bitflags for this entity.</param>
/// <param name="MetadataPath">Object type metadata path (for example <c>Metadata/Monsters/...</c> or <c>Metadata/MiscellaneousObjects/WorldItem</c>).</param>
/// <param name="GridX">Tile/grid X coordinate from <c>Positioned</c>.</param>
/// <param name="GridY">Tile/grid Y coordinate from <c>Positioned</c>.</param>
/// <param name="Position">3D world-space position from <c>Positioned</c>/<c>Render</c>.</param>
/// <param name="Hp">Current hit points from <c>Life</c>, or <c>0</c> if absent.</param>
/// <param name="MaxHp">Maximum hit points from <c>Life</c>, or <c>0</c> if absent.</param>
/// <param name="Rarity">Magic/rare/unique rarity from <c>ObjectMagicProperties</c> or item mods.</param>
/// <param name="IsFilterVisible">For <see cref="WorldEntityKind.WorldItem"/> entities, whether the active item filter shows the item.</param>
/// <param name="WorldItem">Populated item metadata snapshot when the entity carries a <c>WorldItem</c> component; otherwise <c>null</c>.</param>
public sealed record WorldEntitySnapshot(
    uint EntityId,
    nint EntityHandle,
    WorldEntityKind Kind,
    string MetadataPath,
    int GridX,
    int GridY,
    WorldPosition3D Position,
    int Hp = 0,
    int MaxHp = 0,
    ItemRarity Rarity = ItemRarity.Normal,
    bool IsFilterVisible = false,
    HoveredItemInfo? WorldItem = null)
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
/// Filter criteria for querying live entities from <see cref="IWorldService.GetEntities"/>.
/// </summary>
/// <param name="RequiredKind">Bitmask of <see cref="WorldEntityKind"/> flags that MUST all be present on matching entities.</param>
/// <param name="ExcludedKind">Bitmask of <see cref="WorldEntityKind"/> flags that MUST NOT be present on matching entities.</param>
/// <param name="MetadataPrefix">Optional case-insensitive prefix filter on <see cref="WorldEntitySnapshot.MetadataPath"/>.</param>
/// <param name="MaxDistance">Optional maximum 3D Euclidean distance from <paramref name="Origin"/> (or the local player if <paramref name="Origin"/> is <c>null</c>).</param>
/// <param name="Origin">Optional reference origin for <paramref name="MaxDistance"/> filtering; defaults to the local player's position.</param>
/// <param name="FilterVisibleOnly">When <c>true</c>, only includes entities with <see cref="WorldEntitySnapshot.IsFilterVisible"/> set.</param>
/// <param name="MaxResults">Optional maximum number of entities to return.</param>
public sealed record WorldEntityQuery(
    WorldEntityKind RequiredKind = WorldEntityKind.None,
    WorldEntityKind ExcludedKind = WorldEntityKind.None,
    string? MetadataPrefix = null,
    float? MaxDistance = null,
    WorldPosition3D? Origin = null,
    bool FilterVisibleOnly = false,
    int? MaxResults = null);

/// <summary>
/// Snapshot describing an object in the active client area.
/// </summary>
/// <param name="ObjectId">Unique world object identifier.</param>
/// <param name="MetadataPath">Object type metadata path (for example <c>Metadata/MiscellaneousObjects/Mods/Garden/Plot</c>).</param>
/// <param name="X">World X coordinate.</param>
/// <param name="Y">World Y coordinate.</param>
/// <param name="Z">World Z coordinate.</param>
/// <param name="IsClientOnly"><c>true</c> if spawned locally via the client-only object factory.</param>
/// <param name="Components">Optional snapshot of known component property values.</param>
public sealed record WorldObjectInfo(
    uint ObjectId,
    string MetadataPath,
    float X,
    float Y,
    float Z,
    bool IsClientOnly,
    IReadOnlyDictionary<string, string> Components);

/// <summary>
/// Inspects world objects and live entities in the current area, spawns client-only objects, and plays visual effects.
/// </summary>
public interface IWorldService
{
    /// <summary>The current area identifier (empty when not in an area).</summary>
    string CurrentAreaId { get; }

    /// <summary>Snapshot of all tracked objects in the current area.</summary>
    IReadOnlyCollection<WorldObjectInfo> Objects { get; }

    /// <summary>Finds an object by id, or <c>null</c> if absent.</summary>
    WorldObjectInfo? FindObject(uint objectId);

    /// <summary>Spawns a client-only object invisible to the server (requires no dat row).</summary>
    /// <param name="metadataPath">Path to the object's <c>.ot</c> definition.</param>
    /// <param name="x">World X coordinate.</param>
    /// <param name="y">World Y coordinate.</param>
    /// <param name="z">World Z coordinate.</param>
    /// <returns>The allocated client-only object id.</returns>
    uint SpawnClientObject(string metadataPath, float x, float y, float z = 0f);

    /// <summary>Removes a previously spawned client-only object.</summary>
    bool RemoveClientObject(uint objectId);

    /// <summary>Queues a visual effect on a target object.</summary>
    /// <param name="targetObjectId">Target world object id.</param>
    /// <param name="effectPath">Effect metadata or <c>MiscAnimated</c> path.</param>
    void PlayEffect(uint targetObjectId, string effectPath);

    /// <summary>Returns a snapshot of the local player entity in the current area, or <c>null</c> when not in-game.</summary>
    WorldEntitySnapshot? GetLocalPlayer() => null;

    /// <summary>Queries live entities in the current area's <c>EntityManager</c>, optionally filtered by <paramref name="query"/>.</summary>
    /// <param name="query">Optional filter criteria; pass <c>null</c> to return all entities.</param>
    IReadOnlyList<WorldEntitySnapshot> GetEntities(WorldEntityQuery? query = null) => [];

    /// <summary>Looks up a single live entity by its world entity id, or returns <c>null</c> if absent.</summary>
    /// <param name="entityId">World entity identifier.</param>
    WorldEntitySnapshot? GetEntity(uint entityId) => null;

    /// <summary>Returns all ground <c>WorldItem</c> entities in the current area, optionally filtering by item-filter visibility and distance from the local player.</summary>
    /// <param name="filterVisibleOnly">When <c>true</c>, only returns ground items shown by the active item filter.</param>
    /// <param name="maxDistance">Optional maximum 3D distance from the local player in world units.</param>
    IReadOnlyList<WorldEntitySnapshot> GetWorldItems(bool filterVisibleOnly = false, float? maxDistance = null) =>
        GetEntities(new WorldEntityQuery(
            RequiredKind: WorldEntityKind.WorldItem,
            MaxDistance: maxDistance,
            FilterVisibleOnly: filterVisibleOnly));

    /// <summary>Returns the current 3D world position of the specified entity, or <c>null</c> if not found.</summary>
    /// <param name="entityId">World entity identifier.</param>
    WorldPosition3D? GetEntityPosition(uint entityId) => GetEntity(entityId)?.Position;
}

/// <summary>
/// Rich metadata snapshot describing the currently loaded game area (<c>WorldAreasRow</c> + <c>ClientWorld</c>).
/// </summary>
/// <param name="AreaId">Unique <c>WorldAreas.Id</c> string (for example <c>1_1_town</c>).</param>
/// <param name="AreaName">Localized display name of the area (for example <c>Lioneye's Watch</c>).</param>
/// <param name="Act">Act number (<c>WorldAreasRow + 0x10</c>).</param>
/// <param name="AreaLevel">Base area level from <c>WorldAreasRow + 0x26</c>.</param>
/// <param name="EffectiveLevel">Effective runtime monster/area level from <c>ClientWorld + 0xd4</c>.</param>
/// <param name="Hash16">16-bit area hash (<c>WorldAreasRow + 0x2a</c>).</param>
/// <param name="IsTown"><c>true</c> if the area is a town.</param>
/// <param name="IsHideout"><c>true</c> if the area is a player hideout.</param>
/// <param name="IsGuildHideout"><c>true</c> if the area is a guild hideout.</param>
/// <param name="ActiveRulesetIds">Active ruleset identifiers reported by <c>InstanceClient</c> for this instance.</param>
/// <param name="AreaSeed">Deterministic area generation seed from <c>ClientWorld + 0x8e0</c>, if available.</param>
public sealed record AreaInfo(
    string AreaId,
    string AreaName,
    int Act,
    int AreaLevel,
    int EffectiveLevel,
    ushort Hash16,
    bool IsTown,
    bool IsHideout,
    bool IsGuildHideout,
    IReadOnlyList<uint> ActiveRulesetIds,
    uint AreaSeed = 0);

/// <summary>
/// Payload delivered when the client enters a game area.
/// </summary>
/// <param name="AreaId">The area identifier (for example <c>1_1_town</c> or <c>HarvestGrove</c>).</param>
/// <param name="Seed">Deterministic area generation seed.</param>
/// <param name="ActiveRulesetIds">Ruleset ids requested by the server for this area.</param>
public sealed record AreaEventArgs(
    string AreaId,
    uint Seed,
    IReadOnlyList<byte> ActiveRulesetIds)
{
    /// <summary>Optional full <see cref="AreaInfo"/> metadata snapshot populated by live 3.29 area hooks.</summary>
    public AreaInfo? Area { get; init; }
}

/// <summary>
/// Core client lifecycle events dispatched on the game thread.
/// </summary>
public interface IGameLifecycle
{
    /// <summary>Total number of frames processed since host initialization.</summary>
    long FrameCount { get; }

    /// <summary>Gets whether the client is currently connected to an instance server.</summary>
    bool IsConnected { get; }

    /// <summary>Gets the active area id, or <c>null</c> when outside an area.</summary>
    string? CurrentAreaId { get; }

    /// <summary>Gets the full metadata snapshot of the active game area, or <c>null</c> when outside an area.</summary>
    AreaInfo? CurrentArea => null;

    /// <summary>Raised once per frame on the game thread with elapsed delta seconds.</summary>
    event Action<float>? Frame;

    /// <summary>Raised when the client finishes entering an area.</summary>
    event Action<AreaEventArgs>? AreaEntered;

    /// <summary>Raised when the client leaves an area.</summary>
    event Action<string>? AreaLeft;

    /// <summary>Raised when an instance server connection is established.</summary>
    event Action? Connected;

    /// <summary>Raised when disconnected from the server, with an optional reason.</summary>
    event Action<string?>? Disconnected;
}
