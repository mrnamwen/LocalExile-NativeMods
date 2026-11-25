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
}

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
/// Inspects world objects in the current area, spawns client-only objects, and plays visual effects.
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
}

/// <summary>
/// Payload delivered when the client enters a game area.
/// </summary>
/// <param name="AreaId">The area identifier (for example <c>1_1_town</c> or <c>HarvestGrove</c>).</param>
/// <param name="Seed">Deterministic area generation seed.</param>
/// <param name="ActiveRulesetIds">Ruleset ids requested by the server for this area.</param>
public sealed record AreaEventArgs(
    string AreaId,
    uint Seed,
    IReadOnlyList<byte> ActiveRulesetIds);

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
