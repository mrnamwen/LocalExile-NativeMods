using LocalExile.Client.Sdk.Engine;

namespace LocalExile.Client.Sdk.Extend;

/// <summary>
/// Registers custom GEAL built-in functions callable from <c>.ot</c> client scripts.
/// </summary>
public interface IGealBuiltinRegistry
{
    /// <summary>Names of all custom GEAL built-ins registered by client mods.</summary>
    IReadOnlyCollection<string> RegisteredNames { get; }

    /// <summary>Registers a custom GEAL built-in returning a <see cref="GealValue"/>.</summary>
    /// <param name="name">Built-in function name (case-insensitive unique).</param>
    /// <param name="handler">Callback receiving the invoking object id and positional arguments.</param>
    IDisposable Register(string name, Func<uint, GealArguments, GealValue> handler);

    /// <summary>Registers a custom void GEAL built-in.</summary>
    /// <param name="name">Built-in function name (case-insensitive unique).</param>
    /// <param name="handler">Callback receiving the invoking object id and positional arguments.</param>
    IDisposable Register(string name, Action<uint, GealArguments> handler);

    /// <summary>Returns <c>true</c> if a custom built-in with <paramref name="name"/> is registered.</summary>
    bool IsRegistered(string name);

    /// <summary>Invokes a registered custom GEAL built-in by name.</summary>
    GealInvocationResult Invoke(string name, uint objectId, params GealValue[] arguments);
}

/// <summary>
/// Contract implemented by custom UI controls registered via <see cref="IUiControlRegistry"/> and instantiated from <c>.ui</c> files.
/// </summary>
public interface ICustomUiControl
{
    /// <summary>The control type name used in <c>.ui</c> files (for example <c>GrowthGauge</c>).</summary>
    string ControlTypeName { get; }

    /// <summary>Called when the control is attached to a loaded <c>.ui</c> tree.</summary>
    /// <param name="hostControl">The backing <see cref="IUiControl"/> node in the UI tree.</param>
    void OnInitialize(IUiControl hostControl);

    /// <summary>Called whenever a property on the backing control is modified.</summary>
    void OnPropertyChanged(string propertyName, string value);

    /// <summary>Called once per game frame while the owning window is open.</summary>
    void OnFrame(float deltaSeconds);

    /// <summary>Called when the owning window closes or reloads.</summary>
    void OnDestroy();
}

/// <summary>
/// Registers custom UI control factories usable from <c>.ui</c> layouts alongside the client's 47 stock controls.
/// </summary>
public interface IUiControlRegistry
{
    /// <summary>Type names of all custom UI controls currently registered.</summary>
    IReadOnlyCollection<string> RegisteredControlTypes { get; }

    /// <summary>Registers a custom control type with a parameterless constructor.</summary>
    IDisposable Register<TControl>(string controlTypeName) where TControl : class, ICustomUiControl, new();

    /// <summary>Registers a custom control factory delegate.</summary>
    IDisposable Register(string controlTypeName, Func<ICustomUiControl> factory);

    /// <summary>Returns <c>true</c> if <paramref name="controlTypeName"/> is registered.</summary>
    bool IsRegistered(string controlTypeName);

    /// <summary>Instantiates a custom control by type name, or returns <c>null</c> if not registered.</summary>
    ICustomUiControl? CreateInstance(string controlTypeName);
}

/// <summary>
/// Custom per-area client ruleset backed by an unused slot in the 3.29 ruleset creator array (<c>0x00..0xAE</c>).
/// Receives S→C <c>0x2c9</c> updates for its <see cref="RulesetId"/>.
/// </summary>
public interface IClientRuleset
{
    /// <summary>Slot index in the client ruleset creator array (<c>0x00..0xAE</c>).</summary>
    byte RulesetId { get; }

    /// <summary>Human-readable ruleset name.</summary>
    string Name { get; }

    /// <summary>Called when the ruleset is instantiated for a newly entered area.</summary>
    void OnAreaEntered(AreaEventArgs area);

    /// <summary>Called when an S→C <c>0x2c9</c> ruleset update arrives for this <see cref="RulesetId"/>.</summary>
    void OnRulesetUpdate(ReadOnlySpan<byte> payload);

    /// <summary>Called once per game frame while active in the area.</summary>
    void OnFrame(float deltaSeconds);

    /// <summary>Called when leaving the area.</summary>
    void OnAreaLeft();
}

/// <summary>
/// Registers custom per-area client rulesets into empty slots of the 3.29 creator array (<c>0xaf</c> slots total).
/// </summary>
public interface IRulesetRegistry
{
    /// <summary>Maximum valid ruleset slot id (<c>0xaf - 1 = 0xae</c>).</summary>
    const byte MaxRulesetId = 0xAE;

    /// <summary>Default unoccupied ruleset id reserved for the NativeMods host channel.</summary>
    const byte DefaultHostChannelRulesetId = 0xA0;

    /// <summary>All ruleset ids currently registered by the host or client mods.</summary>
    IReadOnlyCollection<byte> RegisteredIds { get; }

    /// <summary>Registers a custom ruleset class using its <see cref="IClientRuleset.RulesetId"/>.</summary>
    IDisposable Register<TRuleset>() where TRuleset : class, IClientRuleset, new();

    /// <summary>Registers a custom ruleset factory for <paramref name="rulesetId"/>.</summary>
    IDisposable Register(byte rulesetId, Func<IClientRuleset> factory);

    /// <summary>Returns <c>true</c> if a custom ruleset is registered at <paramref name="rulesetId"/>.</summary>
    bool IsRegistered(byte rulesetId);

    /// <summary>Dispatches an incoming S→C <c>0x2c9</c> payload to the registered ruleset, returning <c>false</c> if absent (dropped silently).</summary>
    bool TryDispatchUpdate(byte rulesetId, ReadOnlySpan<byte> payload);
}

/// <summary>
/// Registers custom network opcode handlers via the client's handler-group mechanism.
/// Per verified 3.29.3d findings, stock opcodes end at <c>0x2fb</c>, so reserved custom opcodes begin at <c>0x2fc</c>.
/// </summary>
public interface IOpcodeRegistry
{
    /// <summary>S→C ruleset update wire opcode (<c>0x2c9</c>).</summary>
    const ushort ServerRulesetOpcode = 0x02C9;

    /// <summary>C→S ruleset event wire opcode (<c>0x2ca</c>).</summary>
    const ushort ClientRulesetOpcode = 0x02CA;

    /// <summary>First free opcode above the 3.29.3d instance dispatcher upper bound (<c>0x2fb</c>).</summary>
    const ushort FirstReservedOpcode = 0x02FC;

    /// <summary>Opcodes currently claimed by registered handlers.</summary>
    IReadOnlyCollection<ushort> RegisteredOpcodes { get; }

    /// <summary>Registers a handler callback for an incoming opcode.</summary>
    /// <param name="opcode">Wire opcode identifier.</param>
    /// <param name="handler">Callback invoked with the raw payload bytes.</param>
    /// <param name="groupName">Optional logical handler group name.</param>
    IDisposable RegisterHandler(ushort opcode, Action<ReadOnlyMemory<byte>> handler, string? groupName = null);

    /// <summary>Returns <c>true</c> if at least one handler is registered for <paramref name="opcode"/>.</summary>
    bool IsRegistered(ushort opcode);

    /// <summary>Dispatches a raw packet payload to registered handlers for <paramref name="opcode"/>.</summary>
    bool TryDispatch(ushort opcode, ReadOnlyMemory<byte> payload);
}

/// <summary>
/// Declares an append-only row to be merged into a client <c>.datc64</c> table.
/// </summary>
/// <param name="TableName">Target dat table name (for example <c>BaseItemTypes</c>, <c>MiscObjects</c>, <c>MonsterVarieties</c>).</param>
/// <param name="EntryKey">Unique entry key within the declaring mod.</param>
/// <param name="Fields">Column values for the appended row.</param>
public sealed record DatRowDeclaration(
    string TableName,
    string EntryKey,
    IReadOnlyDictionary<string, object?> Fields);

/// <summary>
/// Declares a new append-only item base riding an existing compiled <c>ItemClasses</c> enum value.
/// </summary>
public sealed record ItemBaseDeclaration(
    string MetadataPath,
    string Name,
    string InheritsFrom,
    string ItemClass,
    string ArtPath,
    int StackSize = 1,
    int Width = 1,
    int Height = 1);

/// <summary>
/// Declares a new server-creatable world object type using one of the 8 carrier dat tables (for example <c>MiscObjects</c>).
/// </summary>
public sealed record WorldObjectDeclaration(
    string MetadataPath,
    string CarrierTable,
    string? ScriptPath = null,
    string? OtOverlayContent = null);

/// <summary>
/// Declares an inline <c>.ot</c> or <c>.it</c> overlay text file.
/// </summary>
public sealed record ObjectOverlayDeclaration(
    string VirtualPath,
    string Content);

/// <summary>
/// Collects append-only content declarations (dat rows, item bases, world objects, and <c>.ot</c>/<c>.it</c> overlays)
/// with deterministic cross-side ID generation.
/// </summary>
public interface IContentRegistry
{
    /// <summary>All declared append-only dat table rows.</summary>
    IReadOnlyList<DatRowDeclaration> DatRows { get; }

    /// <summary>All declared item base definitions.</summary>
    IReadOnlyList<ItemBaseDeclaration> ItemBases { get; }

    /// <summary>All declared world object definitions.</summary>
    IReadOnlyList<WorldObjectDeclaration> WorldObjects { get; }

    /// <summary>All declared <c>.ot</c>/<c>.it</c> overlays.</summary>
    IReadOnlyList<ObjectOverlayDeclaration> Overlays { get; }

    /// <summary>Registers an append-only dat row declaration.</summary>
    IDisposable DeclareDatRow(DatRowDeclaration row);

    /// <summary>Registers a new item base declaration.</summary>
    IDisposable DeclareItemBase(ItemBaseDeclaration itemBase);

    /// <summary>Registers a new world object declaration (and its optional <c>.ot</c> overlay).</summary>
    IDisposable DeclareWorldObject(WorldObjectDeclaration worldObject);

    /// <summary>Registers a standalone <c>.ot</c> or <c>.it</c> overlay.</summary>
    IDisposable DeclareOverlay(ObjectOverlayDeclaration overlay);

    /// <summary>
    /// Computes a deterministic 32-bit FNV-1a identifier from <paramref name="modId"/> and <paramref name="entryKey"/>
    /// so client and server agree on content IDs across reloads without an extra sync round-trip.
    /// </summary>
    uint ComputeDeterministicId(string modId, string entryKey);
}
