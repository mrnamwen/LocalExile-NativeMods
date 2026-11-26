using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LocalExile.Client.ServerBridge;

/// <summary>
/// Declarative descriptor for a brand-new appended Currency item added by a NativeMods dual-side mod
/// across <c>BaseItemTypes.datc64</c>, <c>ItemVisualIdentity.datc64</c>, and <c>CurrencyItems.datc64</c>
/// (never overwriting any existing stock currency row).
/// </summary>
public sealed record CustomCurrencyItemDefinition
{
    /// <summary>Verified 3.29.3d stock row count in <c>BaseItemTypes.datc64</c> (<c>0..5460</c>).</summary>
    public const int Stock3293dBaseItemTypesCount = 5461;

    /// <summary>Verified 3.29.3d stock row count in <c>ItemVisualIdentity.datc64</c> (<c>0..15515</c>).</summary>
    public const int Stock3293dItemVisualIdentityCount = 15516;

    /// <summary>Verified 3.29.3d stock row count in <c>CurrencyItems.datc64</c> (<c>0..1961</c>).</summary>
    public const int Stock3293dCurrencyItemsCount = 1962;

    /// <summary>Stock 3.29.3d <c>ItemClasses.datc64</c> row index for <c>StackableCurrency</c>.</summary>
    public const int StackableCurrencyClassRow = 30;

    /// <summary>Stock 3.29.3d <c>ItemClasses.Id</c> for stackable currency items.</summary>
    public const string StackableCurrencyClassName = "StackableCurrency";

    /// <summary>Stock 3.29.3d <c>CurrencyItems.CurrencyUseType</c> for right-click-then-left-click-item currencies.</summary>
    public const int CurrencyUseTypeApplyToItem = 2;

    /// <summary>Stock 3.29.3d <c>BaseItemTypes.ModDomain</c> for currency items (matches Orb of Annulment and Charged Compass).</summary>
    public const int DefaultCurrencyModDomain = 43;

    /// <summary>
    /// Stock 3.29.3d <c>CurrencyItems.Action</c> that enables PoE's native right-click cursor-apply mode and
    /// <c>C-&gt;S 0x0071</c> emission against uncorrupted, unmirrored Magic or Rare items.
    /// </summary>
    public const string DefaultMagicOrRareTargetAction = "remove_random_mod";

    /// <summary>Serialized component list for a standard stackable currency inheriting <c>Metadata/Items/Currency/StackableCurrency</c>.</summary>
    public static readonly IReadOnlyList<string> StandardCurrencyComponents = ["Base", "Usable", "Stack"];

    /// <summary>
    /// Serialized component list for a currency carrying persistent item modifiers on its own item card
    /// (matches stock 3.29.3d <c>Metadata/Items/Currency/CurrencyItemisedSextantModifier</c> / Charged Compass).
    /// </summary>
    public static readonly IReadOnlyList<string> ModBearingCurrencyComponents = ["Base", "Usable", "Stack", "LocalStats", "Mods"];

    /// <summary>Appended 0-based row index in <c>BaseItemTypes.datc64</c> (must be &gt;= <see cref="Stock3293dBaseItemTypesCount"/>).</summary>
    public int BaseItemTypesRowIndex { get; init; }

    /// <summary>Appended 0-based row index in <c>ItemVisualIdentity.datc64</c> (must be &gt;= <see cref="Stock3293dItemVisualIdentityCount"/>).</summary>
    public int ItemVisualIdentityRowIndex { get; init; }

    /// <summary>Appended 0-based row index in <c>CurrencyItems.datc64</c> (must be &gt;= <see cref="Stock3293dCurrencyItemsCount"/>).</summary>
    public int CurrencyItemsRowIndex { get; init; }

    /// <summary>Unique <c>BaseItemTypes.Id</c> metadata path (for example <c>Metadata/Items/Currency/Mods/Siphon/SiphonOrbEmpty</c>).</summary>
    public string BaseItemTypeId { get; init; } = string.Empty;

    /// <summary>Display name shown on the item card and in inventory tooltips.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Unique <c>ItemVisualIdentity.Id</c> key.</summary>
    public string VisualIdentityId { get; init; } = string.Empty;

    /// <summary>Collision-free 16-bit <c>ItemVisualIdentity.HASH16</c> written to both the <c>.datc64</c> row and wire items.</summary>
    public ushort VisualIdentityHash16 { get; init; }

    /// <summary>Virtual path to the 2D inventory icon <c>.dds</c> asset.</summary>
    public string IconDdsPath { get; init; } = string.Empty;

    /// <summary>Inventory grid width in cells (defaults to 1).</summary>
    public byte Width { get; init; } = 1;

    /// <summary>Inventory grid height in cells (defaults to 1).</summary>
    public byte Height { get; init; } = 1;

    /// <summary>Maximum stack size in inventory (for example 20 for empty orbs, 1 for unique-state charged orbs).</summary>
    public ushort MaxStackSize { get; init; } = 20;

    /// <summary><c>CurrencyItems.CurrencyUseType</c> (2 = right-click then left-click target item).</summary>
    public int CurrencyUseType { get; init; } = CurrencyUseTypeApplyToItem;

    /// <summary><c>CurrencyItems.Action</c> driving PoE 3.29 client-side targeting validation.</summary>
    public string CurrencyAction { get; init; } = DefaultMagicOrRareTargetAction;

    /// <summary>Instruction line shown on the currency tooltip (<c>CurrencyItems.Directions</c>).</summary>
    public string Directions { get; init; } = string.Empty;

    /// <summary>Effect description shown on the currency tooltip (<c>CurrencyItems.Description</c>).</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Minimum drop level (<c>BaseItemTypes.DropLevel</c>).</summary>
    public int DropLevel { get; init; } = 1;

    /// <summary><c>BaseItemTypes.ModDomain</c> (defaults to <see cref="DefaultCurrencyModDomain"/>).</summary>
    public int ModDomain { get; init; } = DefaultCurrencyModDomain;

    /// <summary><c>BaseItemTypes.ItemClassesKey</c> row index (defaults to <see cref="StackableCurrencyClassRow"/>).</summary>
    public int ItemClassRow { get; init; } = StackableCurrencyClassRow;

    /// <summary><c>ItemClasses.Id</c> name (defaults to <see cref="StackableCurrencyClassName"/>).</summary>
    public string ItemClassName { get; init; } = StackableCurrencyClassName;

    /// <summary>
    /// When <c>true</c>, this currency includes <c>LocalStats</c> and <c>Mods</c> components so it can persist
    /// and natively display stored modifiers on its own hover tooltip (like stock Charged Compass).
    /// </summary>
    public bool SupportsStoredMods { get; init; }

    /// <summary>Optional explicit component override; defaults to <see cref="ModBearingCurrencyComponents"/> or <see cref="StandardCurrencyComponents"/>.</summary>
    public IReadOnlyList<string>? ExplicitComponents { get; init; }

    /// <summary>Effective serialized component list matching the item's <c>.it</c> inheritance chain.</summary>
    public IReadOnlyList<string> Components =>
        ExplicitComponents is { Count: > 0 }
            ? ExplicitComponents
            : SupportsStoredMods
                ? ModBearingCurrencyComponents
                : StandardCurrencyComponents;

    /// <summary>Deterministic 32-bit MurmurHash2 of <see cref="BaseItemTypeId"/> (<c>BaseItemTypes.HASH32</c>).</summary>
    public uint TypeHash => PoeContentHash.MurmurHash2(BaseItemTypeId);

    /// <summary>Validates that this currency appends brand-new rows and obeys 3.29.3d item invariants.</summary>
    public void Validate()
    {
        if (BaseItemTypesRowIndex < Stock3293dBaseItemTypesCount)
            throw new ArgumentOutOfRangeException(nameof(BaseItemTypesRowIndex), $"BaseItemTypesRowIndex ({BaseItemTypesRowIndex}) must be >= {Stock3293dBaseItemTypesCount} so stock items are never overwritten.");
        if (ItemVisualIdentityRowIndex < Stock3293dItemVisualIdentityCount)
            throw new ArgumentOutOfRangeException(nameof(ItemVisualIdentityRowIndex), $"ItemVisualIdentityRowIndex ({ItemVisualIdentityRowIndex}) must be >= {Stock3293dItemVisualIdentityCount} so stock visuals are never overwritten.");
        if (CurrencyItemsRowIndex < Stock3293dCurrencyItemsCount)
            throw new ArgumentOutOfRangeException(nameof(CurrencyItemsRowIndex), $"CurrencyItemsRowIndex ({CurrencyItemsRowIndex}) must be >= {Stock3293dCurrencyItemsCount} so stock currencies are never overwritten.");
        if (string.IsNullOrWhiteSpace(BaseItemTypeId) || !BaseItemTypeId.StartsWith("Metadata/Items/", StringComparison.Ordinal))
            throw new ArgumentException("BaseItemTypeId must start with 'Metadata/Items/'.", nameof(BaseItemTypeId));
        if (BaseItemTypeId.EndsWith(".it", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("BaseItemTypeId must omit the '.it' file extension.", nameof(BaseItemTypeId));
        if (string.IsNullOrWhiteSpace(DisplayName))
            throw new ArgumentException("DisplayName is required.", nameof(DisplayName));
        if (string.IsNullOrWhiteSpace(VisualIdentityId))
            throw new ArgumentException("VisualIdentityId is required.", nameof(VisualIdentityId));
        if (VisualIdentityHash16 == 0)
            throw new ArgumentOutOfRangeException(nameof(VisualIdentityHash16), "VisualIdentityHash16 must be non-zero.");
        if (Width == 0 || Height == 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "Width and Height must be at least 1.");
        if (MaxStackSize == 0)
            throw new ArgumentOutOfRangeException(nameof(MaxStackSize), "MaxStackSize must be at least 1.");
        if (SupportsStoredMods && MaxStackSize > 1)
            throw new InvalidOperationException("Mod-bearing currency items must have MaxStackSize == 1 so distinct stored mods never merge.");
    }
}

/// <summary>
/// Context passed to a registered custom currency handler when a player right-clicks a registered custom
/// currency item and left-clicks a target item (<c>C-&gt;S 0x0071</c> <c>UseItemOn</c> or <c>C-&gt;S 0x0072</c> <c>UseItemTypeOn</c>).
/// </summary>
/// <param name="ConnectionId">Gateway connection identifier.</param>
/// <param name="CharacterName">Active character name.</param>
/// <param name="CurrencyDefinition">Registered custom currency definition.</param>
/// <param name="CurrencyInventoryId">Inventory ID holding the currency (or <c>0</c> if unresolved during Shift-repeat).</param>
/// <param name="CurrencyItemId">Item ID of the currency stack (or <c>0</c> if unresolved during Shift-repeat).</param>
/// <param name="TargetInventoryId">Inventory ID holding the clicked target item.</param>
/// <param name="TargetItemId">Item ID of the clicked target item.</param>
/// <param name="IsShiftRepeat"><c>true</c> when triggered via <c>C-&gt;S 0x0072</c> (Shift-repeat by type hash).</param>
/// <param name="GatewayPlayer">Optional live LocalExile Gateway <c>Player</c> instance.</param>
public sealed record CurrencyUseContext(
    string ConnectionId,
    string CharacterName,
    CustomCurrencyItemDefinition CurrencyDefinition,
    uint CurrencyInventoryId,
    uint CurrencyItemId,
    uint TargetInventoryId,
    uint TargetItemId,
    bool IsShiftRepeat = false,
    object? GatewayPlayer = null);

/// <summary>
/// Decoded <c>C-&gt;S 0x0071</c> (<c>UseItemOn</c>) wire frame (18 bytes, big-endian).
/// </summary>
public readonly record struct DecodedUseItemOn(uint CurrencyInventoryId, uint CurrencyItemId, uint TargetInventoryId, uint TargetItemId);

/// <summary>
/// Decoded <c>C-&gt;S 0x0072</c> (<c>UseItemTypeOn</c>) Shift-repeat wire frame (23 bytes, big-endian).
/// </summary>
public readonly record struct DecodedUseItemTypeOn(uint CurrencyTypeHash, uint TargetInventoryId, uint TargetItemId);

/// <summary>
/// Reusable LocalExile Gateway helper that registers brand-new appended Currency items into LocalExile's live
/// <c>ItemCatalog</c> (with clean removal on <see cref="Dispose"/>), grants custom currency items into a player's
/// inventory, and intercepts <c>C-&gt;S 0x0071</c>/<c>0x0072</c> (<c>UseItemOn</c> / <c>UseItemTypeOn</c>) strictly
/// for registered custom currency <c>TypeHash</c>es without interfering with any stock Path of Exile currency.
/// </summary>
public sealed class LocalExileCurrencyRegistrar : IDisposable
{
    /// <summary>PoE 3.29 <c>C-&gt;S 0x0071</c> (<c>InstanceItems.UseItemOnId</c>) opcode (18 bytes).</summary>
    public const ushort UseItemOnOpcode = 0x0071;

    /// <summary>Exact wire byte length of <c>C-&gt;S 0x0071</c> including the 2-byte opcode header.</summary>
    public const int UseItemOnPacketLength = 18;

    /// <summary>PoE 3.29 <c>C-&gt;S 0x0072</c> (<c>InstanceItems.UseItemTypeOnId</c>) opcode (23 bytes).</summary>
    public const ushort UseItemTypeOnOpcode = 0x0072;

    /// <summary>Exact wire byte length of <c>C-&gt;S 0x0072</c> including the 2-byte opcode header.</summary>
    public const int UseItemTypeOnPacketLength = 23;

    private readonly List<CustomCurrencyItemDefinition> _definitions = [];
    private readonly ConcurrentDictionary<uint, Func<CurrencyUseContext, Task<bool>>> _handlers = new();
    private readonly List<Action> _cleanupActions = [];
    private readonly ConditionalWeakTable<object, HashSet<uint>> _appliedCatalogs = new();
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>All custom currency definitions currently registered with this registrar.</summary>
    public IReadOnlyList<CustomCurrencyItemDefinition> RegisteredCurrencies
    {
        get { lock (_gate) { return [.. _definitions]; } }
    }

    /// <summary>
    /// Registers a brand-new <see cref="CustomCurrencyItemDefinition"/> and optional right-click-on-item handler.
    /// Disposing the returned handle unregisters both the definition and its handler.
    /// </summary>
    public IDisposable Register(
        CustomCurrencyItemDefinition definition,
        Func<CurrencyUseContext, Task<bool>>? useHandler = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_definitions.Any(d => d.TypeHash == definition.TypeHash))
                throw new InvalidOperationException($"A custom currency with TypeHash 0x{definition.TypeHash:X8} ({definition.BaseItemTypeId}) is already registered.");

            _definitions.Add(definition);
            _definitions.Sort((a, b) => a.BaseItemTypesRowIndex.CompareTo(b.BaseItemTypesRowIndex));
        }

        if (useHandler is not null)
            _handlers[definition.TypeHash] = useHandler;

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _definitions.RemoveAll(d => d.TypeHash == definition.TypeHash);
            }

            _handlers.TryRemove(definition.TypeHash, out _);
        });
    }

    /// <summary>Binds or replaces the asynchronous <c>UseCurrency</c> handler for a registered currency <paramref name="currencyTypeHash"/>.</summary>
    public IDisposable SetUseHandler(uint currencyTypeHash, Func<CurrencyUseContext, Task<bool>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[currencyTypeHash] = handler;
        return new ActionDisposable(() => _handlers.TryRemove(currencyTypeHash, out _));
    }

    /// <summary>Returns <c>true</c> if <paramref name="typeHash"/> matches a custom currency registered with this registrar.</summary>
    public bool IsRegisteredCurrency(uint typeHash)
    {
        lock (_gate)
        {
            return _definitions.Any(d => d.TypeHash == typeHash);
        }
    }

    /// <summary>Looks up a registered currency definition by its <c>BaseItemTypes.HASH32</c>.</summary>
    public bool TryGetDefinition(uint typeHash, out CustomCurrencyItemDefinition? definition)
    {
        lock (_gate)
        {
            definition = _definitions.FirstOrDefault(d => d.TypeHash == typeHash);
            return definition is not null;
        }
    }

    /// <summary>Looks up a registered currency definition by its <c>BaseItemTypes.Id</c>.</summary>
    public bool TryGetDefinitionById(string baseItemTypeId, out CustomCurrencyItemDefinition? definition)
    {
        lock (_gate)
        {
            definition = _definitions.FirstOrDefault(d =>
                string.Equals(d.BaseItemTypeId, baseItemTypeId, StringComparison.OrdinalIgnoreCase));
            return definition is not null;
        }
    }

    /// <summary>
    /// Appends all registered custom currencies into a live LocalExile <c>ItemCatalog</c> instance
    /// (<c>Program.Items</c> or <c>Game.ItemsFor(player)</c>) without modifying LocalExile core.
    /// Idempotent per catalog instance; all injected entries are cleanly removed on <see cref="Dispose"/>.
    /// </summary>
    /// <param name="itemCatalog">Live LocalExile <c>ItemCatalog</c> instance (or <c>null</c>).</param>
    /// <param name="log">Optional diagnostic logger callback.</param>
    /// <returns>Number of newly registered currency definitions added to <paramref name="itemCatalog"/>.</returns>
    public int ApplyToLiveCatalog(object? itemCatalog, Action<string>? log = null)
    {
        if (itemCatalog is null)
            return 0;

        CustomCurrencyItemDefinition[] snapshot = RegisteredCurrencies.ToArray();
        if (snapshot.Length == 0)
            return 0;

        HashSet<uint> appliedSet = _appliedCatalogs.GetOrCreateValue(itemCatalog);
        int added = 0;

        foreach (CustomCurrencyItemDefinition def in snapshot)
        {
            lock (_gate)
            {
                if (appliedSet.Contains(def.TypeHash))
                    continue;
            }

            if (TryApplySingleCurrencyToCatalog(def, itemCatalog, log))
            {
                lock (_gate)
                {
                    appliedSet.Add(def.TypeHash);
                }

                added++;
            }
        }

        return added;
    }

    /// <summary>Decodes a <c>C-&gt;S 0x0071</c> (<c>UseItemOn</c>) wire frame (with its 2-byte opcode header).</summary>
    public static bool TryDecodeUseItemOn(ReadOnlySpan<byte> packetBytes, out DecodedUseItemOn decoded)
    {
        decoded = default;
        if (packetBytes.Length < UseItemOnPacketLength)
            return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(packetBytes[..2]) != UseItemOnOpcode)
            return false;

        decoded = new DecodedUseItemOn(
            CurrencyInventoryId: BinaryPrimitives.ReadUInt32BigEndian(packetBytes.Slice(2, 4)),
            CurrencyItemId: BinaryPrimitives.ReadUInt32BigEndian(packetBytes.Slice(6, 4)),
            TargetInventoryId: BinaryPrimitives.ReadUInt32BigEndian(packetBytes.Slice(10, 4)),
            TargetItemId: BinaryPrimitives.ReadUInt32BigEndian(packetBytes.Slice(14, 4)));
        return true;
    }

    /// <summary>Decodes a <c>C-&gt;S 0x0072</c> (<c>UseItemTypeOn</c>) Shift-repeat wire frame (with its 2-byte opcode header).</summary>
    public static bool TryDecodeUseItemTypeOn(ReadOnlySpan<byte> packetBytes, out DecodedUseItemTypeOn decoded)
    {
        decoded = default;
        if (packetBytes.Length < UseItemTypeOnPacketLength)
            return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(packetBytes[..2]) != UseItemTypeOnOpcode)
            return false;

        decoded = new DecodedUseItemTypeOn(
            CurrencyTypeHash: BinaryPrimitives.ReadUInt32BigEndian(packetBytes.Slice(2, 4)),
            TargetInventoryId: BinaryPrimitives.ReadUInt32BigEndian(packetBytes.Slice(6, 4)),
            TargetItemId: BinaryPrimitives.ReadUInt32BigEndian(packetBytes.Slice(10, 4)));
        return true;
    }

    /// <summary>Encodes a 18-byte <c>C-&gt;S 0x0071</c> (<c>UseItemOn</c>) packet for testing or simulation.</summary>
    public static byte[] EncodeUseItemOn(uint currencyInventoryId, uint currencyItemId, uint targetInventoryId, uint targetItemId)
    {
        byte[] packet = new byte[UseItemOnPacketLength];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), UseItemOnOpcode);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(2, 4), currencyInventoryId);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(6, 4), currencyItemId);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(10, 4), targetInventoryId);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(14, 4), targetItemId);
        return packet;
    }

    /// <summary>Encodes a 23-byte <c>C-&gt;S 0x0072</c> (<c>UseItemTypeOn</c>) Shift-repeat packet for testing or simulation.</summary>
    public static byte[] EncodeUseItemTypeOn(uint currencyTypeHash, uint targetInventoryId, uint targetItemId)
    {
        byte[] packet = new byte[UseItemTypeOnPacketLength];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), UseItemTypeOnOpcode);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(2, 4), currencyTypeHash);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(6, 4), targetInventoryId);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(10, 4), targetItemId);
        return packet;
    }

    /// <summary>
    /// Inspects an incoming <c>C-&gt;S 0x0071</c> (<c>UseItemOn</c>) packet and dispatches it to the registered
    /// custom currency handler ONLY if the used item's <c>TypeHash</c> belongs to this registrar.
    /// Returns <c>false</c> for all stock Path of Exile currencies so stock crafting proceeds untouched.
    /// </summary>
    /// <param name="connectionId">Gateway connection identifier.</param>
    /// <param name="characterName">Active character name.</param>
    /// <param name="packetBytes">Full packet bytes including the 2-byte big-endian <c>0x0071</c> opcode.</param>
    /// <param name="resolveItemTypeHash">Callback returning the <c>BaseItemTypes.HASH32</c> of <c>(inventoryId, itemId)</c>, or <c>null</c>.</param>
    /// <param name="gatewayPlayer">Optional live Gateway <c>Player</c> instance.</param>
    /// <returns><c>true</c> if claimed by a registered custom currency handler; <c>false</c> otherwise.</returns>
    public async Task<bool> HandleUseItemOnPacketAsync(
        string connectionId,
        string characterName,
        ReadOnlyMemory<byte> packetBytes,
        Func<uint, uint, uint?> resolveItemTypeHash,
        object? gatewayPlayer = null)
    {
        ArgumentNullException.ThrowIfNull(resolveItemTypeHash);
        if (!TryDecodeUseItemOn(packetBytes.Span, out DecodedUseItemOn decoded))
            return false;

        uint? typeHash = resolveItemTypeHash(decoded.CurrencyInventoryId, decoded.CurrencyItemId);
        if (typeHash is null || !TryGetDefinition(typeHash.Value, out CustomCurrencyItemDefinition? def) || def is null)
            return false;

        if (!_handlers.TryGetValue(def.TypeHash, out var handler))
            return false;

        var ctx = new CurrencyUseContext(
            connectionId,
            characterName,
            def,
            decoded.CurrencyInventoryId,
            decoded.CurrencyItemId,
            decoded.TargetInventoryId,
            decoded.TargetItemId,
            IsShiftRepeat: false,
            GatewayPlayer: gatewayPlayer);
        return await handler(ctx).ConfigureAwait(false);
    }

    /// <summary>
    /// Inspects an incoming <c>C-&gt;S 0x0072</c> (<c>UseItemTypeOn</c>, Shift-repeat) packet and dispatches it
    /// ONLY if <c>TypeHash</c> belongs to this registrar. Returns <c>false</c> for all stock currencies.
    /// </summary>
    /// <param name="connectionId">Gateway connection identifier.</param>
    /// <param name="characterName">Active character name.</param>
    /// <param name="packetBytes">Full packet bytes including the 2-byte big-endian <c>0x0072</c> opcode.</param>
    /// <param name="locateStackByTypeHash">Callback locating an available <c>(inventoryId, itemId)</c> stack for the currency type hash.</param>
    /// <param name="gatewayPlayer">Optional live Gateway <c>Player</c> instance.</param>
    /// <returns><c>true</c> if claimed by a registered custom currency handler; <c>false</c> otherwise.</returns>
    public async Task<bool> HandleUseItemTypeOnPacketAsync(
        string connectionId,
        string characterName,
        ReadOnlyMemory<byte> packetBytes,
        Func<uint, (uint InventoryId, uint ItemId)?> locateStackByTypeHash,
        object? gatewayPlayer = null)
    {
        ArgumentNullException.ThrowIfNull(locateStackByTypeHash);
        if (!TryDecodeUseItemTypeOn(packetBytes.Span, out DecodedUseItemTypeOn decoded))
            return false;

        if (!TryGetDefinition(decoded.CurrencyTypeHash, out CustomCurrencyItemDefinition? def) || def is null)
            return false;

        if (!_handlers.TryGetValue(def.TypeHash, out var handler))
            return false;

        (uint InventoryId, uint ItemId)? stack = locateStackByTypeHash(def.TypeHash);
        if (stack is null)
            return true; // Claim the custom currency Shift-repeat packet even if depleted so stock handler doesn't log unknown.

        var ctx = new CurrencyUseContext(
            connectionId,
            characterName,
            def,
            stack.Value.InventoryId,
            stack.Value.ItemId,
            decoded.TargetInventoryId,
            decoded.TargetItemId,
            IsShiftRepeat: true,
            GatewayPlayer: gatewayPlayer);
        return await handler(ctx).ConfigureAwait(false);
    }

    /// <summary>
    /// Reflection-assisted helper that creates a live LocalExile <c>GameServer.Core.Protocol.Instance.Item</c>
    /// for <paramref name="definition"/> using <paramref name="itemCatalog"/>'s <c>ItemCatalog.Create(BaseType)</c>.
    /// </summary>
    public static object? CreateLiveItem(object itemCatalog, CustomCurrencyItemDefinition definition, ushort stackSize = 1)
    {
        ArgumentNullException.ThrowIfNull(itemCatalog);
        ArgumentNullException.ThrowIfNull(definition);

        Type catalogType = itemCatalog.GetType();
        MethodInfo? byHash = catalogType.GetMethod("ByHash", BindingFlags.Public | BindingFlags.Instance, [typeof(uint)]);
        object? baseType = byHash?.Invoke(itemCatalog, [definition.TypeHash]);
        if (baseType is null)
            return null;

        MethodInfo? createMethod = catalogType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == baseType.GetType());
        if (createMethod is null)
            return null;

        ParameterInfo[] createParams = createMethod.GetParameters();
        object?[] createArgs = new object?[createParams.Length];
        createArgs[0] = baseType;
        for (int i = 1; i < createParams.Length; i++)
            createArgs[i] = createParams[i].HasDefaultValue ? createParams[i].DefaultValue : GetDefaultValue(createParams[i].ParameterType);

        object? item = createMethod.Invoke(null, createArgs);
        if (item is null)
            return null;

        ushort clampedStack = Math.Clamp(stackSize, (ushort)1, definition.MaxStackSize);
        Type itemType = item.GetType();
        SetPropertyOrInit(itemType, item, "StackSize", clampedStack);
        SetPropertyOrInit(itemType, item, "Identified", true);
        if (definition.SupportsStoredMods)
            SetPropertyOrInit(itemType, item, "RequiredLevel", 0);

        return item;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Action[] cleanups;
        lock (_gate)
        {
            cleanups = [.. _cleanupActions];
            _cleanupActions.Clear();
            _definitions.Clear();
        }

        _handlers.Clear();

        foreach (Action cleanup in cleanups)
        {
            try
            {
                cleanup();
            }
            catch
            {
                // Best-effort catalog cleanup on mod unload.
            }
        }
    }

    private bool TryApplySingleCurrencyToCatalog(
        CustomCurrencyItemDefinition def,
        object itemCatalog,
        Action<string>? log)
    {
        Type catalogType = itemCatalog.GetType();
        if (TryApplySingleCurrencyViaPart2bOverlay(def, itemCatalog, catalogType, log))
        {
            return true;
        }

        FieldInfo? rowsField = catalogType.GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? byHashField = catalogType.GetField("_byHash", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? byIdField = catalogType.GetField("_byId", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? visualsField = catalogType.GetField("_visualsById", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? currencyUsesField = catalogType.GetField("_currencyUses", BindingFlags.Instance | BindingFlags.NonPublic);

        object? rowsTuple = rowsField?.GetValue(itemCatalog);
        if (rowsTuple is null)
            return false;

        FieldInfo? rowsItem1 = rowsTuple.GetType().GetField("Item1");
        if (rowsItem1?.GetValue(rowsTuple) is not IList rowsList)
            return false;

        if (byHashField?.GetValue(itemCatalog) is not IDictionary byHashDict ||
            byIdField?.GetValue(itemCatalog) is not IDictionary byIdDict)
            return false;

        if (byHashDict.Contains(def.TypeHash))
            return false;

        Type? baseTypeClr = rowsItem1.FieldType.IsGenericType ? rowsItem1.FieldType.GetGenericArguments()[0] : null;
        if (baseTypeClr is null)
            return false;

        int effectiveRow = rowsList.Count == def.BaseItemTypesRowIndex ? def.BaseItemTypesRowIndex : rowsList.Count;
        object? baseTypeInstance = CreateBaseTypeInstance(baseTypeClr, def, effectiveRow);
        if (baseTypeInstance is null)
            return false;

        rowsList.Add(baseTypeInstance);
        byHashDict[def.TypeHash] = baseTypeInstance;
        byIdDict[def.BaseItemTypeId] = baseTypeInstance;

        IDictionary? visualsDict = visualsField?.GetValue(itemCatalog) as IDictionary;
        bool addedVisual = false;
        if (visualsDict is not null && !visualsDict.Contains(def.VisualIdentityId))
        {
            visualsDict[def.VisualIdentityId] = def.VisualIdentityHash16;
            addedVisual = true;
        }

        IDictionary? actionsDict = null;
        bool addedAction = false;
        object? currencyUsesTuple = currencyUsesField?.GetValue(itemCatalog);
        if (currencyUsesTuple is not null && !string.IsNullOrWhiteSpace(def.CurrencyAction))
        {
            actionsDict = currencyUsesTuple.GetType().GetField("Item1")?.GetValue(currencyUsesTuple) as IDictionary;
            if (actionsDict is not null && !actionsDict.Contains(effectiveRow))
            {
                actionsDict[effectiveRow] = def.CurrencyAction;
                addedAction = true;
            }
        }

        lock (_gate)
        {
            _cleanupActions.Add(() =>
            {
                rowsList.Remove(baseTypeInstance);
                byHashDict.Remove(def.TypeHash);
                byIdDict.Remove(def.BaseItemTypeId);
                if (addedVisual && visualsDict is not null)
                    visualsDict.Remove(def.VisualIdentityId);
                if (addedAction && actionsDict is not null)
                    actionsDict.Remove(effectiveRow);
            });
        }

        log?.Invoke($"[ServerBridge] Registered custom currency '{def.DisplayName}' (Row={effectiveRow}, HASH32=0x{def.TypeHash:X8}, VisualHASH16=0x{def.VisualIdentityHash16:X4}, Stack={def.MaxStackSize}, StoredMods={def.SupportsStoredMods}).");
        return true;
    }

    private bool TryApplySingleCurrencyViaPart2bOverlay(
        CustomCurrencyItemDefinition def,
        object itemCatalog,
        Type catalogType,
        Action<string>? log)
    {
        MethodInfo? registerMethod = catalogType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
            {
                if (!string.Equals(m.Name, "Register", StringComparison.Ordinal))
                    return false;
                ParameterInfo[] ps = m.GetParameters();
                return ps.Length >= 2 && ps[0].ParameterType == typeof(string);
            });

        if (registerMethod is null)
            return false;

        // Avoid duplicate registration when already present in the catalog.
        MethodInfo? byHashMethod = catalogType.GetMethod("ByHash", BindingFlags.Public | BindingFlags.Instance, [typeof(uint)]);
        if (byHashMethod?.Invoke(itemCatalog, [def.TypeHash]) is not null)
            return false;

        ParameterInfo[] regParams = registerMethod.GetParameters();
        Type baseTypeClr = regParams[1].ParameterType;

        int existingCount = 0;
        if (catalogType.GetProperty("All", BindingFlags.Public | BindingFlags.Instance)?.GetValue(itemCatalog) is ICollection allCol)
        {
            existingCount = allCol.Count;
        }
        else if (catalogType.GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(itemCatalog) is { } rowsTuple &&
                 rowsTuple.GetType().GetField("Item1")?.GetValue(rowsTuple) is IList rowsList)
        {
            existingCount = rowsList.Count;
        }

        int effectiveRow = existingCount == def.BaseItemTypesRowIndex
            ? def.BaseItemTypesRowIndex
            : (existingCount > 0 ? existingCount : def.BaseItemTypesRowIndex);

        object? baseTypeInstance = CreateBaseTypeInstance(baseTypeClr, def, effectiveRow);
        if (baseTypeInstance is null)
            return false;

        object? optionsInstance = null;
        if (regParams.Length >= 3)
        {
            Type optType = regParams[2].ParameterType;
            ConstructorInfo? optCtor = optType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (optCtor is not null)
            {
                ParameterInfo[] ops = optCtor.GetParameters();
                object?[] optArgs = new object?[ops.Length];
                for (int i = 0; i < ops.Length; i++)
                {
                    string? pName = ops[i].Name;
                    if (string.Equals(pName, "VisualIdentityId", StringComparison.OrdinalIgnoreCase))
                        optArgs[i] = def.VisualIdentityId;
                    else if (string.Equals(pName, "CurrencyAction", StringComparison.OrdinalIgnoreCase))
                        optArgs[i] = string.IsNullOrWhiteSpace(def.CurrencyAction) ? null : def.CurrencyAction;
                    else
                        optArgs[i] = ops[i].HasDefaultValue ? ops[i].DefaultValue : GetDefaultValue(ops[i].ParameterType);
                }

                optionsInstance = optCtor.Invoke(optArgs);
            }
        }

        string ownerId = $"nativemods.currency.{def.BaseItemTypeId}";
        object?[] invokeArgs = new object?[regParams.Length];
        invokeArgs[0] = ownerId;
        invokeArgs[1] = baseTypeInstance;
        if (regParams.Length >= 3)
            invokeArgs[2] = optionsInstance;

        IDisposable? baseHandle = registerMethod.Invoke(itemCatalog, invokeArgs) as IDisposable;

        IDisposable? visualHandle = null;
        if (!string.IsNullOrWhiteSpace(def.VisualIdentityId))
        {
            MethodInfo? regVisual = catalogType.GetMethod(
                "RegisterVisual",
                BindingFlags.Public | BindingFlags.Instance,
                [typeof(string), typeof(string), typeof(ushort)]);
            visualHandle = regVisual?.Invoke(itemCatalog, [ownerId, def.VisualIdentityId, def.VisualIdentityHash16]) as IDisposable;
        }

        lock (_gate)
        {
            _cleanupActions.Add(() =>
            {
                visualHandle?.Dispose();
                baseHandle?.Dispose();
            });
        }

        log?.Invoke($"[ServerBridge] Registered custom currency '{def.DisplayName}' via Part 2b ItemCatalog.Register (Row={effectiveRow}, HASH32=0x{def.TypeHash:X8}, VisualHASH16=0x{def.VisualIdentityHash16:X4}, Stack={def.MaxStackSize}, StoredMods={def.SupportsStoredMods}).");
        return true;
    }

    private static object? CreateBaseTypeInstance(Type baseTypeClr, CustomCurrencyItemDefinition def, int rowIndex)
    {
        // LocalExile ItemCatalog.BaseType(
        //   int Row, string Id, string Name, uint Hash, byte Width, byte Height, ushort VisualIdentity,
        //   IReadOnlyList<string> Components, int MaxCharges, string Class = "", byte Stance = 0xff,
        //   int DropLevel = 0, ushort MaxStack = 1, int FullStackRow = -1, IReadOnlyList<ModCatalog.Mod>? Implicits = null)
        ConstructorInfo? ctor = baseTypeClr.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault(c =>
            {
                ParameterInfo[] p = c.GetParameters();
                return p.Length >= 8 &&
                       p[0].ParameterType == typeof(int) &&
                       p[1].ParameterType == typeof(string) &&
                       p[2].ParameterType == typeof(string) &&
                       p[3].ParameterType == typeof(uint);
            });

        if (ctor is null)
            return null;

        ParameterInfo[] parameters = ctor.GetParameters();
        object?[] args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            args[i] = i switch
            {
                0 => rowIndex,
                1 => def.BaseItemTypeId,
                2 => def.DisplayName,
                3 => def.TypeHash,
                4 => def.Width,
                5 => def.Height,
                6 => def.VisualIdentityHash16,
                7 => def.Components,
                8 => 0, // MaxCharges
                9 => def.ItemClassName,
                10 => (byte)0xFF, // Stance
                11 => def.DropLevel,
                12 => def.MaxStackSize,
                13 => -1, // FullStackRow
                _ => parameters[i].HasDefaultValue ? parameters[i].DefaultValue : GetDefaultValue(parameters[i].ParameterType),
            };
        }

        object instance = ctor.Invoke(args);
        SetPropertyOrInit(baseTypeClr, instance, "ModDomain", def.ModDomain);
        SetPropertyOrInit(baseTypeClr, instance, "ClassRow", def.ItemClassRow);
        SetPropertyOrInit(baseTypeClr, instance, "GroundSize", (byte)1);
        SetPropertyOrInit(baseTypeClr, instance, "Unmodifiable", false);
        SetPropertyOrInit(baseTypeClr, instance, "CanBeFractured", false);
        SetPropertyOrInit(baseTypeClr, instance, "CanBeMirrored", false);
        SetPropertyOrInit(baseTypeClr, instance, "CanBeCorrupted", false);
        return instance;
    }

    private static void SetPropertyOrInit(Type type, object instance, string propertyName, object? value)
    {
        PropertyInfo? prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        MethodInfo? setter = prop?.GetSetMethod(nonPublic: true);
        if (setter is not null)
        {
            setter.Invoke(instance, [value]);
            return;
        }

        // Support init-only compiler-generated backing fields if needed.
        FieldInfo? backing = type.GetField($"<{propertyName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        backing?.SetValue(instance, value);
    }

    private static object? GetDefaultValue(Type type) =>
        type.IsValueType ? Activator.CreateInstance(type) : null;

    private sealed class ActionDisposable(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;
        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
