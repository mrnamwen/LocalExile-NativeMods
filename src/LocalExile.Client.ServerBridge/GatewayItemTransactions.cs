using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;

namespace LocalExile.Client.ServerBridge;

/// <summary>
/// Resolved location of a live LocalExile Gateway item in either a character inventory row or an account stash tab.
/// </summary>
/// <param name="Tab">Stash tab index, or <c>null</c> if located in <c>PlayerInventories</c>.</param>
/// <param name="Row">Inventory row (<c>0</c> = MainInventory, <c>1..11</c> = equipment/flasks, <c>23</c> = passive jewels, <c>26</c> = stash tab).</param>
/// <param name="InventoryId">Wire inventory identifier.</param>
/// <param name="ItemId">Item instance identifier within that inventory.</param>
/// <param name="InventoryItem">Live Gateway <c>InventoryItem</c> record.</param>
/// <param name="Item">Live Gateway <c>GameServer.Core.Protocol.Instance.Item</c> record.</param>
/// <param name="IsWorn"><c>true</c> if the item is currently equipped in a worn equipment/flask/jewel slot.</param>
public sealed record GatewayItemLocation(
    int? Tab,
    byte Row,
    uint InventoryId,
    uint ItemId,
    object InventoryItem,
    object Item,
    bool IsWorn);

/// <summary>
/// Single container mutation entry passed to <see cref="LocalExileGatewayTransactions.GroupInventoryDiffs"/>
/// or <see cref="LocalExileGatewayTransactions.SendInventoryDiffsAsync"/>.
/// </summary>
/// <param name="Tab">Stash tab index, or <c>null</c> for character inventories.</param>
/// <param name="Row">Character inventory row when <paramref name="Tab"/> is <c>null</c>.</param>
/// <param name="RemovedIds">Item ids removed or replaced in this container.</param>
/// <param name="AddedItems">Live <c>InventoryItem</c> records added or updated in this container.</param>
public sealed record GatewayInventoryChange(
    int? Tab,
    byte Row,
    IReadOnlyList<uint> RemovedIds,
    IReadOnlyList<object> AddedItems);

/// <summary>
/// Grouped per-container inventory diff bucket ready for <c>S→C 0x00BF</c> emission.
/// </summary>
/// <param name="Tab">Stash tab index, or <c>null</c> for character inventories.</param>
/// <param name="Row">Character inventory row.</param>
/// <param name="RemovedIds">Ordered removed/replaced item ids.</param>
/// <param name="AddedItems">Ordered added/updated <c>InventoryItem</c> objects.</param>
public sealed record GatewayInventoryDiffBucket(
    int? Tab,
    byte Row,
    List<uint> RemovedIds,
    List<object> AddedItems);

/// <summary>
/// Outcome of granting one or more items into a player's main inventory via <see cref="LocalExileGatewayTransactions.GrantItems(object, object, uint, ushort, string?, bool, Func{object, object?}?)"/>.
/// </summary>
public sealed record GatewayGrantResult(
    bool Success,
    string Message,
    int GrantedCount,
    int RemainingCount,
    IReadOnlyList<uint> GrantedItemIds);

/// <summary>
/// Outcome of stealing an explicit affix from a live Gateway item into a Charged Currency item.
/// </summary>
public sealed record GatewayStealResult(
    bool Success,
    string? Error,
    StoredAffixPayload? StolenAffix,
    uint CreatedChargedItemId,
    ItemAffixView? UpdatedSourceView)
{
    /// <summary>Creates a failed steal result with <paramref name="error"/>.</summary>
    public static GatewayStealResult Fail(string error) => new(false, error, null, 0, null);
}

/// <summary>
/// Outcome of replacing an explicit affix on a live Gateway item using a Charged Currency item.
/// </summary>
public sealed record GatewayReplaceResult(
    bool Success,
    string? Error,
    ItemAffixSnapshot? ReplacedAffix,
    StoredAffixPayload? AppliedAffix,
    ItemAffixView? UpdatedTargetView)
{
    /// <summary>Creates a failed replace result with <paramref name="error"/>.</summary>
    public static GatewayReplaceResult Fail(string error) => new(false, error, null, null, null);
}

/// <summary>
/// Reusable live LocalExile Gateway item, inventory, stash, and affix transaction helpers
/// communicating with <c>PlayerInventories</c>, <c>AccountStash</c>, <c>ItemCatalog</c>, <c>Saves</c>,
/// <c>InstanceItems</c>, and <c>Modules/Core/Game</c> via cached reflection without requiring a
/// compile-time reference to Gateway assemblies.
/// </summary>
public static class LocalExileGatewayTransactions
{
    private const byte MainInventoryRow = 0;
    private const byte StashInventoryRow = 26;

    /// <summary>
    /// Locates a stack of <paramref name="typeHash"/> in main inventory first, then in account stash tabs
    /// (skipping public/trade tabs unless <paramref name="includePublicStashTabs"/> is <c>true</c>).
    /// </summary>
    public static (uint InventoryId, uint ItemId)? LocateCurrencyStack(
        object playerInventories,
        object? accountStash,
        uint typeHash,
        bool includePublicStashTabs = false)
    {
        ArgumentNullException.ThrowIfNull(playerInventories);
        Type invType = playerInventories.GetType();

        MethodInfo? itemsMethod = invType.GetMethod("Items", BindingFlags.Public | BindingFlags.Instance, [typeof(byte)]);
        MethodInfo? invIdMethod = invType.GetMethod("InventoryId", BindingFlags.Public | BindingFlags.Static, [typeof(byte)]);
        if (itemsMethod?.Invoke(playerInventories, [MainInventoryRow]) is IEnumerable mainItems)
        {
            foreach (object? invItem in mainItems)
            {
                if (invItem is null)
                {
                    continue;
                }

                object? item = GetProp<object>(invItem, "Item");
                if (item is not null && GetTypeHash(item) == typeHash && GetStackSize(item) > 0)
                {
                    uint mainInvId = invIdMethod is not null
                        ? Convert.ToUInt32(invIdMethod.Invoke(null, [MainInventoryRow]), CultureInfo.InvariantCulture)
                        : 1u;
                    return (mainInvId, GetItemId(invItem));
                }
            }
        }

        if (accountStash is null)
        {
            return null;
        }

        Type stashType = accountStash.GetType();
        int tabCount = GetProp<int>(accountStash, "Count");
        MethodInfo? isPublicMethod = stashType.GetMethod("IsPublic", BindingFlags.Public | BindingFlags.Instance, [typeof(int)]);
        MethodInfo? inventoryMethod = stashType.GetMethod("Inventory", BindingFlags.Public | BindingFlags.Instance, [typeof(int)]);
        MethodInfo? inventoryOfMethod = stashType.GetMethod("InventoryOf", BindingFlags.Public | BindingFlags.Instance, [typeof(int)]);

        for (int tab = 0; tab < tabCount; tab++)
        {
            if (!includePublicStashTabs &&
                isPublicMethod?.Invoke(accountStash, [tab]) is true)
            {
                continue;
            }

            object? tabInv = inventoryMethod?.Invoke(accountStash, [tab]);
            if (tabInv is null)
            {
                continue;
            }

            if (GetTupleOrPropEnumerable(tabInv, "Items", "Item2") is not { } tabItems)
            {
                continue;
            }

            foreach (object? invItem in tabItems)
            {
                if (invItem is null)
                {
                    continue;
                }

                object? item = GetProp<object>(invItem, "Item");
                if (item is not null && GetTypeHash(item) == typeHash && GetStackSize(item) > 0)
                {
                    uint tabInvId = inventoryOfMethod is not null
                        ? Convert.ToUInt32(inventoryOfMethod.Invoke(accountStash, [tab]), CultureInfo.InvariantCulture)
                        : (0x800u + (uint)tab);
                    return (tabInvId, GetItemId(invItem));
                }
            }
        }

        return null;
    }

    /// <summary>Alias for <see cref="LocateCurrencyStack"/>.</summary>
    public static (uint InventoryId, uint ItemId)? LocateStack(
        object playerInventories,
        object? accountStash,
        uint typeHash,
        bool includePublicStashTabs = false) =>
        LocateCurrencyStack(playerInventories, accountStash, typeHash, includePublicStashTabs);

    /// <summary>
    /// Locates a live Gateway item by <paramref name="inventoryId"/> and <paramref name="itemId"/> across
    /// <paramref name="accountStash"/> and <paramref name="playerInventories"/>, computing whether it is worn.
    /// </summary>
    public static GatewayItemLocation? LocateGatewayItem(
        object playerInventories,
        object? accountStash,
        uint inventoryId,
        uint itemId)
    {
        ArgumentNullException.ThrowIfNull(playerInventories);

        if (accountStash is not null)
        {
            Type stashType = accountStash.GetType();
            MethodInfo? tabOfMethod = stashType.GetMethod("TabOf", BindingFlags.Public | BindingFlags.Instance, [typeof(uint)]);
            object? tabObj = tabOfMethod?.Invoke(accountStash, [inventoryId]);
            if (tabObj is int tab && tab >= 0)
            {
                MethodInfo? inventoryMethod = stashType.GetMethod("Inventory", BindingFlags.Public | BindingFlags.Instance, [typeof(int)]);
                object? tabInv = inventoryMethod?.Invoke(accountStash, [tab]);
                if (tabInv is not null && GetTupleOrPropEnumerable(tabInv, "Items", "Item2") is { } tabItems)
                {
                    foreach (object? invItem in tabItems)
                    {
                        if (invItem is not null && GetItemId(invItem) == itemId)
                        {
                            object item = GetProp<object>(invItem, "Item")
                                ?? throw new InvalidOperationException("InventoryItem.Item was null.");
                            return new GatewayItemLocation(tab, StashInventoryRow, inventoryId, itemId, invItem, item, IsWorn: false);
                        }
                    }
                }

                return null;
            }
        }

        Type invType = playerInventories.GetType();
        MethodInfo? rowOfMethod = invType.GetMethod("RowOf", BindingFlags.Public | BindingFlags.Instance, [typeof(uint)]);
        object? rowObj = rowOfMethod?.Invoke(playerInventories, [itemId]);
        if (rowObj is byte row)
        {
            MethodInfo? itemsMethod = invType.GetMethod("Items", BindingFlags.Public | BindingFlags.Instance, [typeof(byte)]);
            MethodInfo? invIdMethod = invType.GetMethod("InventoryId", BindingFlags.Public | BindingFlags.Static, [typeof(byte)]);
            uint resolvedInvId = invIdMethod is not null
                ? Convert.ToUInt32(invIdMethod.Invoke(null, [row]), CultureInfo.InvariantCulture)
                : (uint)row + 1;

            if (itemsMethod?.Invoke(playerInventories, [row]) is IEnumerable rowItems)
            {
                foreach (object? invItem in rowItems)
                {
                    if (invItem is not null && GetItemId(invItem) == itemId)
                    {
                        object item = GetProp<object>(invItem, "Item")
                            ?? throw new InvalidOperationException("InventoryItem.Item was null.");
                        bool isWorn = IsWornRow(row);
                        return new GatewayItemLocation(null, row, resolvedInvId, itemId, invItem, item, isWorn);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Grants <paramref name="count"/> items of <paramref name="typeHash"/> into <paramref name="playerInventories"/>
    /// main inventory (state-only; does not transmit network packets).
    /// </summary>
    public static GatewayGrantResult GrantItems(
        object itemCatalog,
        object playerInventories,
        uint typeHash,
        ushort count = 1,
        string? customName = null,
        bool requireFullFit = false,
        Func<object, object?>? shapeOf = null) =>
        GrantItemsCore(itemCatalog, playerInventories, typeHash, count, customName, requireFullFit, shapeOf).Result;

    /// <summary>
    /// Grants <paramref name="count"/> items matching <paramref name="definition"/> into <paramref name="playerInventories"/>
    /// main inventory (state-only).
    /// </summary>
    public static GatewayGrantResult GrantItems(
        object itemCatalog,
        object playerInventories,
        CustomCurrencyItemDefinition definition,
        ushort count = 1,
        string? customName = null,
        bool requireFullFit = false,
        Func<object, object?>? shapeOf = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return GrantItems(itemCatalog, playerInventories, definition.TypeHash, count, customName, requireFullFit, shapeOf);
    }

    /// <summary>
    /// Grants <paramref name="count"/> items of <paramref name="typeHash"/> into <paramref name="playerInventories"/>
    /// main inventory and sends grouped <c>0x00BF</c> inventory diff packets when <paramref name="player"/> is non-null.
    /// </summary>
    public static async Task<GatewayGrantResult> GrantItemsAsync(
        object? player,
        object itemCatalog,
        object playerInventories,
        uint typeHash,
        ushort count = 1,
        string? customName = null,
        bool requireFullFit = false,
        Func<object, object?>? shapeOf = null,
        Func<object, byte[], string?, Task>? sendPacket = null,
        CancellationToken cancellationToken = default)
    {
        (GatewayGrantResult result, List<GatewayInventoryChange> diffs) =
            GrantItemsCore(itemCatalog, playerInventories, typeHash, count, customName, requireFullFit, shapeOf);

        if (result.Success && player is not null && diffs.Count > 0)
        {
            await SendInventoryDiffsAsync(
                player,
                playerInventories,
                accountStash: null,
                diffs,
                label: $"grant {result.GrantedCount}x 0x{typeHash:x8}",
                sendPacket: sendPacket,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Grants <paramref name="count"/> items matching <paramref name="definition"/> into <paramref name="playerInventories"/>
    /// main inventory and sends grouped <c>0x00BF</c> inventory diff packets.
    /// </summary>
    public static Task<GatewayGrantResult> GrantItemsAsync(
        object? player,
        object itemCatalog,
        object playerInventories,
        CustomCurrencyItemDefinition definition,
        ushort count = 1,
        string? customName = null,
        bool requireFullFit = false,
        Func<object, object?>? shapeOf = null,
        Func<object, byte[], string?, Task>? sendPacket = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return GrantItemsAsync(player, itemCatalog, playerInventories, definition.TypeHash, count, customName, requireFullFit, shapeOf, sendPacket, cancellationToken);
    }

    private static (GatewayGrantResult Result, List<GatewayInventoryChange> Diffs) GrantItemsCore(
        object itemCatalog,
        object playerInventories,
        uint typeHash,
        ushort count,
        string? customName,
        bool requireFullFit,
        Func<object, object?>? shapeOf)
    {
        ArgumentNullException.ThrowIfNull(itemCatalog);
        ArgumentNullException.ThrowIfNull(playerInventories);

        if (count == 0)
        {
            return (new GatewayGrantResult(false, "Grant count must be at least 1.", 0, 0, []), []);
        }

        object? baseType = InvokeByHash(itemCatalog, typeHash);
        if (baseType is null)
        {
            return (new GatewayGrantResult(false, $"Item type 0x{typeHash:X8} is not registered in the live item catalog.", 0, count, []), []);
        }

        string displayName = GetProp<string>(baseType, "Name") ?? $"Item 0x{typeHash:X8}";
        ushort maxStack = Math.Max((ushort)1, GetProp<ushort>(baseType, "MaxStack"));
        object createdTemplate = CreateCatalogItem(itemCatalog, baseType);
        SetItemProperty(createdTemplate.GetType(), createdTemplate, "Identified", true);
        if (customName is not null)
        {
            createdTemplate = LocalExileItemAffixService.WithGatewayItemCustomName(createdTemplate, customName);
        }

        Type itemClrType = createdTemplate.GetType();
        Delegate shapeDelegate = BuildShapeDelegate(playerInventories, itemCatalog, itemClrType, shapeOf);
        Type invType = playerInventories.GetType();
        MethodInfo addStackedMethod = invType.GetMethod("AddStacked", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PlayerInventories.AddStacked not found.");
        MethodInfo? fitsMethod = invType.GetMethod("Fits", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo? stackAcrossMethod = invType.GetMethod("StackAcross", BindingFlags.Public | BindingFlags.Instance);

        if (requireFullFit)
        {
            if (count <= maxStack && fitsMethod is not null)
            {
                object probeItem = CloneItemWithStack(createdTemplate, count);
                bool fits = (bool)fitsMethod.Invoke(playerInventories, [MainInventoryRow, probeItem, shapeDelegate])!;
                if (!fits)
                {
                    return (new GatewayGrantResult(false, $"Not enough space in main inventory for {count}x {displayName}.", 0, count, []), []);
                }
            }
            else if (stackAcrossMethod is not null)
            {
                byte[] rows = [MainInventoryRow];
                int probeLeft = count;
                // Preview each stack chunk sequentially? Better: if maxStack > 1 and count > maxStack, simulate chunk by chunk?
                // StackAcross takes a single Item, so simulate per-chunk if count > maxStack only when count <= maxStack or single-stack;
                // for count > maxStack, check total free cells + partial stack headroom.
                if (count <= maxStack)
                {
                    object probeItem = CloneItemWithStack(createdTemplate, count);
                    var previewMap = (IDictionary)stackAcrossMethod.Invoke(playerInventories, [rows, probeItem, shapeDelegate, true])!;
                    object? stacking = previewMap[MainInventoryRow];
                    int left = stacking is not null ? GetProp<int>(stacking, "Left") : count;
                    if (left > 0)
                    {
                        return (new GatewayGrantResult(false, $"Not enough space in main inventory for {count}x {displayName}.", 0, count, []), []);
                    }
                }
                else
                {
                    if (!PreviewMultipleChunksFit(playerInventories, createdTemplate, count, maxStack, shapeDelegate))
                    {
                        return (new GatewayGrantResult(false, $"Not enough space in main inventory for {count}x {displayName}.", 0, count, []), []);
                    }
                }
            }
        }

        var removedIds = new List<uint>();
        var newlyAddedIds = new HashSet<uint>();
        var addedItems = new List<object>();
        var grantedIds = new List<uint>();
        int remaining = count;

        RunAtomically(playerInventories, () =>
        {
            while (remaining > 0)
            {
                ushort chunk = (ushort)Math.Min(remaining, maxStack);
                object itemChunk = CloneItemWithStack(createdTemplate, chunk);
                object stacking = addStackedMethod.Invoke(playerInventories, [MainInventoryRow, itemChunk, shapeDelegate])!;

                bool any = GetProp<bool>(stacking, "Any");
                int left = GetProp<int>(stacking, "Left");
                int grantedThisChunk = chunk - left;
                remaining -= grantedThisChunk;

                if (!any || grantedThisChunk == 0)
                {
                    break;
                }

                if (GetProp<IEnumerable>(stacking, "Changed") is { } changedSeq)
                {
                    foreach (object? ch in changedSeq)
                    {
                        if (ch is null) continue;
                        uint id = GetItemId(ch);
                        if (!newlyAddedIds.Contains(id) && !removedIds.Contains(id))
                        {
                            removedIds.Add(id);
                        }

                        // Replace prior snapshot of the same id in addedItems if a second chunk grew the same stack.
                        addedItems.RemoveAll(existing => GetItemId(existing) == id);
                        addedItems.Add(ch);
                        if (!grantedIds.Contains(id))
                        {
                            grantedIds.Add(id);
                        }
                    }
                }

                if (GetProp<IEnumerable>(stacking, "Added") is { } addedSeq)
                {
                    foreach (object? ad in addedSeq)
                    {
                        if (ad is null) continue;
                        uint id = GetItemId(ad);
                        newlyAddedIds.Add(id);
                        addedItems.Add(ad);
                        if (!grantedIds.Contains(id))
                        {
                            grantedIds.Add(id);
                        }
                    }
                }
            }
        });

        int grantedTotal = count - remaining;
        if (grantedTotal == 0)
        {
            return (new GatewayGrantResult(false, $"No free space in main inventory for {displayName}.", 0, count, []), []);
        }

        var diffs = new List<GatewayInventoryChange>
        {
            new(null, MainInventoryRow, removedIds, addedItems),
        };

        return (
            new GatewayGrantResult(
                Success: true,
                Message: $"Granted {grantedTotal}x {displayName} to your inventory.",
                GrantedCount: grantedTotal,
                RemainingCount: remaining,
                GrantedItemIds: grantedIds),
            diffs);
    }

    private static bool PreviewMultipleChunksFit(
        object playerInventories,
        object templateItem,
        int totalCount,
        ushort maxStack,
        Delegate shapeDelegate)
    {
        // Compute available capacity in MainInventory without mutating:
        // existing same-type stack headroom + empty 1x1-equivalent item slots matching shape.
        object? shape = shapeDelegate.DynamicInvoke(templateItem);
        if (shape is null)
        {
            return false;
        }

        byte width = GetProp<byte>(shape, "Width");
        byte height = GetProp<byte>(shape, "Height");
        uint typeHash = GetTypeHash(templateItem);

        int existingHeadroom = 0;
        var occupied = new bool[12, 5];
        MethodInfo? itemsMethod = playerInventories.GetType().GetMethod("Items", BindingFlags.Public | BindingFlags.Instance, [typeof(byte)]);
        if (itemsMethod?.Invoke(playerInventories, [MainInventoryRow]) is IEnumerable mainItems)
        {
            foreach (object? invItem in mainItems)
            {
                if (invItem is null) continue;
                byte ix = GetProp<byte>(invItem, "X");
                byte iy = GetProp<byte>(invItem, "Y");
                object? inner = GetProp<object>(invItem, "Item");
                if (inner is null) continue;

                if (GetTypeHash(inner) == typeHash)
                {
                    ushort stack = GetStackSize(inner);
                    if (stack < maxStack)
                    {
                        existingHeadroom += maxStack - stack;
                    }
                }

                object? s = shapeDelegate.DynamicInvoke(inner);
                byte sw = s is not null ? GetProp<byte>(s, "Width") : (byte)1;
                byte sh = s is not null ? GetProp<byte>(s, "Height") : (byte)1;
                for (int x = ix; x < ix + sw && x < 12; x++)
                {
                    for (int y = iy; y < iy + sh && y < 5; y++)
                    {
                        occupied[x, y] = true;
                    }
                }
            }
        }

        int freeCells = 0;
        for (int x = 0; x + width <= 12; x++)
        {
            for (int y = 0; y + height <= 5; y++)
            {
                bool blocked = false;
                for (int dx = 0; dx < width && !blocked; dx++)
                {
                    for (int dy = 0; dy < height; dy++)
                    {
                        if (occupied[x + dx, y + dy])
                        {
                            blocked = true;
                            break;
                        }
                    }
                }

                if (!blocked)
                {
                    freeCells++;
                    for (int dx = 0; dx < width; dx++)
                    {
                        for (int dy = 0; dy < height; dy++)
                        {
                            occupied[x + dx, y + dy] = true;
                        }
                    }
                }
            }
        }

        long totalCapacity = existingHeadroom + (long)freeCells * maxStack;
        return totalCapacity >= totalCount;
    }

    /// <summary>
    /// Atomically steals the explicit affix at <paramref name="affixIndex"/> from a Gateway item into a newly created
    /// Charged Currency item, validating all constraints before mutating any inventory/stash state.
    /// </summary>
    public static async Task<GatewayStealResult> StealAffixAsync(
        LocalExileItemAffixService affixService,
        object? player,
        object itemCatalog,
        object playerInventories,
        object? accountStash,
        uint currencyInventoryId,
        uint currencyItemId,
        uint targetInventoryId,
        uint targetItemId,
        int affixIndex,
        CustomCurrencyItemDefinition chargedCurrencyDef,
        object? statDescriptions = null,
        object? itemRolls = null,
        string? sourceCustomName = null,
        bool clearSourceCustomName = false,
        string? chargedCustomName = null,
        bool sendPackets = true,
        Func<object, object?>? shapeOf = null,
        Func<object, byte[], string?, Task>? sendPacket = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(affixService);
        ArgumentNullException.ThrowIfNull(itemCatalog);
        ArgumentNullException.ThrowIfNull(playerInventories);
        ArgumentNullException.ThrowIfNull(chargedCurrencyDef);

        GatewayItemLocation? usedLoc = LocateGatewayItem(playerInventories, accountStash, currencyInventoryId, currencyItemId);
        GatewayItemLocation? targetLoc = LocateGatewayItem(playerInventories, accountStash, targetInventoryId, targetItemId);
        if (usedLoc is null)
        {
            return GatewayStealResult.Fail("Source currency item could not be located.");
        }

        if (targetLoc is null)
        {
            return GatewayStealResult.Fail("Target item could not be located.");
        }

        if (usedLoc.Tab == targetLoc.Tab && usedLoc.Row == targetLoc.Row && usedLoc.ItemId == targetLoc.ItemId)
        {
            return GatewayStealResult.Fail("Currency item and target item cannot be the same item.");
        }

        if (targetLoc.IsWorn)
        {
            return GatewayStealResult.Fail("Unequip the target item before siphoning.");
        }

        ushort usedStack = GetStackSize(usedLoc.Item);
        if (usedStack < 1)
        {
            return GatewayStealResult.Fail("Source currency stack is empty.");
        }

        ItemAffixView view = affixService.InspectGatewayItem(targetInventoryId, targetItemId, targetLoc.Item, itemCatalog, statDescriptions, itemRolls);
        if (LocalExileItemAffixService.ValidateStealTarget(view) is { } targetError)
        {
            return GatewayStealResult.Fail(targetError);
        }

        if (affixIndex < 0 || affixIndex >= view.Affixes.Count)
        {
            return GatewayStealResult.Fail($"Selected modifier index {affixIndex} is out of range.");
        }

        ItemAffixSnapshot chosen = view.Affixes[affixIndex];
        if (chosen.IsFractured)
        {
            return GatewayStealResult.Fail("Fractured modifiers cannot be siphoned.");
        }

        if (InvokeByHash(itemCatalog, chargedCurrencyDef.TypeHash) is null)
        {
            return GatewayStealResult.Fail($"{chargedCurrencyDef.DisplayName} is not registered in the live item catalog.");
        }

        Type itemClrType = targetLoc.Item.GetType();
        Delegate shapeDelegate = BuildShapeDelegate(playerInventories, itemCatalog, itemClrType, shapeOf);

        bool orbVacatesMain = usedLoc.Tab is null && usedLoc.Row == MainInventoryRow && usedStack <= 1;
        if (!orbVacatesMain && CallFreeCell(playerInventories, MainInventoryRow, 1, 1, shapeDelegate) is null)
        {
            return GatewayStealResult.Fail($"Need at least 1 free inventory cell for {chargedCurrencyDef.DisplayName}.");
        }

        // Prepare updated source item (removing exact chosen Explicit ItemMod) and Charged Currency item.
        if (!TryBuildItemWithoutExplicitMod(targetLoc.Item, affixIndex, sourceCustomName, clearSourceCustomName, out object? updatedSource, out ushort stolenHash, out int[] stolenValues) ||
            updatedSource is null)
        {
            return GatewayStealResult.Fail("Could not locate selected explicit modifier on source item.");
        }

        object chargedOrbItem = LocalExileCurrencyRegistrar.CreateLiveItem(itemCatalog, chargedCurrencyDef, stackSize: 1)
            ?? throw new InvalidOperationException($"Failed to create live item for {chargedCurrencyDef.DisplayName}.");

        object enchantMod = CreateGatewayItemMod(chargedOrbItem.GetType(), ItemModSectionKind.Enchant, stolenHash, stolenValues);
        SetTypedModsList(chargedOrbItem, [enchantMod]);
        SetItemProperty(chargedOrbItem.GetType(), chargedOrbItem, "Rarity", 0);
        SetItemProperty(chargedOrbItem.GetType(), chargedOrbItem, "Identified", true);
        SetItemProperty(chargedOrbItem.GetType(), chargedOrbItem, "ItemLevel", 1);
        SetItemProperty(chargedOrbItem.GetType(), chargedOrbItem, "RequiredLevel", 0);
        SetItemProperty(chargedOrbItem.GetType(), chargedOrbItem, "StackSize", (ushort)1);
        if (chargedCustomName is not null)
        {
            chargedOrbItem = LocalExileItemAffixService.WithGatewayItemCustomName(chargedOrbItem, chargedCustomName);
        }

        object? usedLeft = null;
        object? changedTarget = null;
        object? addedCharged = null;
        bool txOk = false;

        RunAtomically(playerInventories, () =>
        {
            (bool consumed, object? left) = ConsumeOneAtLocation(playerInventories, accountStash, usedLoc);
            if (!consumed)
            {
                return;
            }

            object? updatedTargetInv = ChangeItemAtLocation(playerInventories, accountStash, targetLoc, updatedSource);
            if (updatedTargetInv is null)
            {
                return;
            }

            (byte X, byte Y)? freeCell = CallFreeCell(playerInventories, MainInventoryRow, 1, 1, shapeDelegate);
            if (freeCell is null)
            {
                throw new InvalidOperationException($"No free cell for {chargedCurrencyDef.DisplayName}.");
            }

            object addedInv = AddItemToInventory(playerInventories, MainInventoryRow, freeCell.Value.X, freeCell.Value.Y, chargedOrbItem);
            usedLeft = left;
            changedTarget = updatedTargetInv;
            addedCharged = addedInv;
            txOk = true;
        });

        if (!txOk || changedTarget is null || addedCharged is null)
        {
            return GatewayStealResult.Fail("Failed to commit siphon transaction (item moved or inventory full).");
        }

        if (sendPackets && player is not null)
        {
            var changes = new List<GatewayInventoryChange>
            {
                new(usedLoc.Tab, usedLoc.Row, [usedLoc.ItemId], usedLeft is not null ? [usedLeft] : []),
                new(targetLoc.Tab, targetLoc.Row, [targetLoc.ItemId], [changedTarget]),
                new(null, MainInventoryRow, [], [addedCharged]),
            };

            await SendInventoryDiffsAsync(
                player,
                playerInventories,
                accountStash,
                changes,
                label: "steal affix",
                sendPacket: sendPacket,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await SendItemUsedAsync(
                player,
                itemClrType,
                GetTypeHash(usedLoc.Item),
                currencyInventoryId,
                currencyItemId,
                sendPacket,
                cancellationToken).ConfigureAwait(false);
        }

        var stolenPayload = new StoredAffixPayload(
            ModId: chosen.ModId,
            RolledValues: [.. chosen.RolledValues],
            DisplayText: chosen.StatText,
            IsPrefix: chosen.IsPrefix,
            Group: chosen.Group,
            ModHash16: stolenHash,
            DisplayName: chosen.DisplayName,
            IsSuffix: chosen.IsSuffix,
            Tier: chosen.Tier,
            IsCrafted: chosen.IsCrafted);

        ItemAffixView updatedSourceView = affixService.InspectGatewayItem(
            targetInventoryId,
            targetItemId,
            GetProp<object>(changedTarget, "Item")!,
            itemCatalog,
            statDescriptions,
            itemRolls);

        return new GatewayStealResult(
            Success: true,
            Error: null,
            StolenAffix: stolenPayload,
            CreatedChargedItemId: GetItemId(addedCharged),
            UpdatedSourceView: updatedSourceView);
    }

    /// <summary>
    /// Atomically replaces the explicit affix at <paramref name="replaceIndex"/> on a Gateway item with the stored affix
    /// carried on a Charged Currency item, validating all constraints before any mutation.
    /// </summary>
    public static async Task<GatewayReplaceResult> ReplaceAffixAsync(
        LocalExileItemAffixService affixService,
        object? player,
        object itemCatalog,
        object playerInventories,
        object? accountStash,
        uint currencyInventoryId,
        uint currencyItemId,
        uint targetInventoryId,
        uint targetItemId,
        int replaceIndex,
        object? statDescriptions = null,
        object? itemRolls = null,
        string? customName = null,
        bool clearCustomName = false,
        bool sendPackets = true,
        Func<object, byte[], string?, Task>? sendPacket = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(affixService);
        ArgumentNullException.ThrowIfNull(itemCatalog);
        ArgumentNullException.ThrowIfNull(playerInventories);

        GatewayItemLocation? usedLoc = LocateGatewayItem(playerInventories, accountStash, currencyInventoryId, currencyItemId);
        GatewayItemLocation? targetLoc = LocateGatewayItem(playerInventories, accountStash, targetInventoryId, targetItemId);
        if (usedLoc is null)
        {
            return GatewayReplaceResult.Fail("Charged currency item could not be located.");
        }

        if (targetLoc is null)
        {
            return GatewayReplaceResult.Fail("Destination item could not be located.");
        }

        if (usedLoc.Tab == targetLoc.Tab && usedLoc.Row == targetLoc.Row && usedLoc.ItemId == targetLoc.ItemId)
        {
            return GatewayReplaceResult.Fail("Currency item and target item cannot be the same item.");
        }

        if (targetLoc.IsWorn)
        {
            return GatewayReplaceResult.Fail("Unequip the destination item before imbuing.");
        }

        if (GetStackSize(usedLoc.Item) < 1)
        {
            return GatewayReplaceResult.Fail("Charged currency stack is empty.");
        }

        StoredAffixPayload? stored = affixService.ReadStoredAffixFromGatewayItem(usedLoc.Item, itemCatalog, statDescriptions);
        if (stored is null || stored.ModHash16 == 0)
        {
            return GatewayReplaceResult.Fail("Charged currency item has no stored modifier.");
        }

        ItemAffixView view = affixService.InspectGatewayItem(targetInventoryId, targetItemId, targetLoc.Item, itemCatalog, statDescriptions, itemRolls);
        (bool allowed, string? reason) = LocalExileItemAffixService.CanReplaceAffix(view, replaceIndex, stored);
        if (!allowed)
        {
            return GatewayReplaceResult.Fail(reason ?? "Selected modifier cannot be replaced.");
        }

        ItemAffixSnapshot replacedSnapshot = view.Affixes[replaceIndex];
        Type itemClrType = targetLoc.Item.GetType();
        object replacementMod = CreateGatewayItemMod(itemClrType, ItemModSectionKind.Explicit, stored.ModHash16, [.. stored.RolledValues]);

        if (!TryBuildItemWithReplacedExplicitMod(targetLoc.Item, replaceIndex, replacementMod, customName, clearCustomName, out object? updatedTarget) ||
            updatedTarget is null)
        {
            return GatewayReplaceResult.Fail("Could not locate target explicit modifier.");
        }

        object? usedLeft = null;
        object? changedTarget = null;
        bool txOk = false;

        RunAtomically(playerInventories, () =>
        {
            (bool consumed, object? left) = ConsumeOneAtLocation(playerInventories, accountStash, usedLoc);
            if (!consumed)
            {
                return;
            }

            object? updatedTargetInv = ChangeItemAtLocation(playerInventories, accountStash, targetLoc, updatedTarget);
            if (updatedTargetInv is null)
            {
                return;
            }

            usedLeft = left;
            changedTarget = updatedTargetInv;
            txOk = true;
        });

        if (!txOk || changedTarget is null)
        {
            return GatewayReplaceResult.Fail("Failed to commit imbue transaction.");
        }

        if (sendPackets && player is not null)
        {
            var changes = new List<GatewayInventoryChange>
            {
                new(usedLoc.Tab, usedLoc.Row, [usedLoc.ItemId], usedLeft is not null ? [usedLeft] : []),
                new(targetLoc.Tab, targetLoc.Row, [targetLoc.ItemId], [changedTarget]),
            };

            await SendInventoryDiffsAsync(
                player,
                playerInventories,
                accountStash,
                changes,
                label: "replace affix",
                sendPacket: sendPacket,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await SendItemUsedAsync(
                player,
                itemClrType,
                GetTypeHash(usedLoc.Item),
                currencyInventoryId,
                currencyItemId,
                sendPacket,
                cancellationToken).ConfigureAwait(false);
        }

        ItemAffixView updatedTargetView = affixService.InspectGatewayItem(
            targetInventoryId,
            targetItemId,
            GetProp<object>(changedTarget, "Item")!,
            itemCatalog,
            statDescriptions,
            itemRolls);

        return new GatewayReplaceResult(
            Success: true,
            Error: null,
            ReplacedAffix: replacedSnapshot,
            AppliedAffix: stored,
            UpdatedTargetView: updatedTargetView);
    }

    /// <summary>
    /// Groups <paramref name="changes"/> by <c>(Tab, Row)</c> so each touched inventory or stash tab receives
    /// a single consolidated <c>S→C 0x00BF</c> diff packet.
    /// </summary>
    public static IReadOnlyList<GatewayInventoryDiffBucket> GroupInventoryDiffs(IEnumerable<GatewayInventoryChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var order = new List<(int? Tab, byte Row)>();
        var map = new Dictionary<(int? Tab, byte Row), GatewayInventoryDiffBucket>();

        foreach (GatewayInventoryChange change in changes)
        {
            var key = (change.Tab, change.Row);
            if (!map.TryGetValue(key, out GatewayInventoryDiffBucket? bucket))
            {
                bucket = new GatewayInventoryDiffBucket(change.Tab, change.Row, [], []);
                map[key] = bucket;
                order.Add(key);
            }

            if (change.RemovedIds is not null)
            {
                foreach (uint id in change.RemovedIds)
                {
                    bucket.RemovedIds.Add(id);
                }
            }

            if (change.AddedItems is not null)
            {
                foreach (object added in change.AddedItems)
                {
                    if (added is not null)
                    {
                        bucket.AddedItems.Add(added);
                    }
                }
            }
        }

        var result = new List<GatewayInventoryDiffBucket>(order.Count);
        foreach ((int? Tab, byte Row) key in order)
        {
            GatewayInventoryDiffBucket bucket = map[key];
            if (bucket.RemovedIds.Count > 0 || bucket.AddedItems.Count > 0)
            {
                result.Add(bucket);
            }
        }

        return result;
    }

    /// <summary>
    /// Groups and transmits <c>0x00BF</c> inventory/stash diff packets to <paramref name="player"/>, calling
    /// <c>Game.SendTabChange</c> for stash tabs and <c>Game.SendEquipment</c> for visible worn rows when loaded.
    /// </summary>
    public static async Task SendInventoryDiffsAsync(
        object player,
        object playerInventories,
        object? accountStash,
        IEnumerable<GatewayInventoryChange> changes,
        string label = "server-bridge",
        Func<object, byte[], string?, Task>? sendPacket = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(playerInventories);
        ArgumentNullException.ThrowIfNull(changes);

        IReadOnlyList<GatewayInventoryDiffBucket> buckets = GroupInventoryDiffs(changes);
        if (buckets.Count == 0)
        {
            return;
        }

        Type invType = playerInventories.GetType();
        Type? invItemType = ResolveInventoryItemType(invType);
        Type? instanceItemsType = FindCoreType(
            [invItemType?.Assembly, invType.Assembly, player.GetType().Assembly],
            "GameServer.Core.Protocol.Instance.InstanceItems");
        Type? gameType = sendPacket is null
            ? FindGlobalType(
                [invType.Assembly, player.GetType().Assembly],
                "Game",
                t => IsGameTypeCompatible(t, player, playerInventories))
            : null;

        MethodInfo? invChangeMethod = instanceItemsType?.GetMethod("InventoryChange", BindingFlags.Public | BindingFlags.Static);
        MethodInfo? invIdMethod = invType.GetMethod("InventoryId", BindingFlags.Public | BindingFlags.Static, [typeof(byte)]);
        MethodInfo? nextRevMethod = invType.GetMethod("NextRevision", BindingFlags.Public | BindingFlags.Instance, [typeof(byte)]);
        MethodInfo? showsOnModelMethod = invType.GetMethod("ShowsOnModel", BindingFlags.Public | BindingFlags.Static, [typeof(byte)]);
        MethodInfo? sendTabChangeMethod = sendPacket is null ? gameType?.GetMethod("SendTabChange", BindingFlags.Public | BindingFlags.Static) : null;
        MethodInfo? sendEquipmentMethod = sendPacket is null ? gameType?.GetMethod("SendEquipment", BindingFlags.Public | BindingFlags.Static) : null;

        bool refreshEquipment = false;

        foreach (GatewayInventoryDiffBucket bucket in buckets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IList typedAdded = CreateTypedList(invItemType, bucket.AddedItems);

            if (bucket.Tab is int tab)
            {
                if (accountStash is null)
                {
                    throw new InvalidOperationException($"Cannot emit stash diff for tab {tab} because accountStash is null.");
                }

                string tabLabel = $"0x00bf stash tab {tab}: {label}";
                if (sendTabChangeMethod is not null)
                {
                    await ((Task)sendTabChangeMethod.Invoke(null, [player, accountStash, tab, bucket.RemovedIds, typedAdded, tabLabel])!).ConfigureAwait(false);
                }
                else
                {
                    if (invChangeMethod is null)
                    {
                        throw new InvalidOperationException("InstanceItems.InventoryChange could not be resolved.");
                    }

                    Type stashType = accountStash.GetType();
                    uint stashInvId = Convert.ToUInt32(stashType.GetMethod("InventoryOf", [typeof(int)])!.Invoke(accountStash, [tab]), CultureInfo.InvariantCulture);
                    uint rev = Convert.ToUInt32(stashType.GetMethod("NextRevision", [typeof(int)])!.Invoke(accountStash, [tab]), CultureInfo.InvariantCulture);
                    bool isGuild = GetProp<bool>(accountStash, "IsGuild");
                    byte manager = isGuild ? (byte)1 : (byte)0;

                    byte[] fullFrame = (byte[])invChangeMethod.Invoke(null, [stashInvId, rev, bucket.RemovedIds, typedAdded, manager])!;
                    await TransmitFullPacketAsync(player, fullFrame, tabLabel, sendPacket, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                if (invChangeMethod is null)
                {
                    throw new InvalidOperationException("InstanceItems.InventoryChange could not be resolved.");
                }

                uint invId = invIdMethod is not null
                    ? Convert.ToUInt32(invIdMethod.Invoke(null, [bucket.Row]), CultureInfo.InvariantCulture)
                    : (uint)bucket.Row + 1;
                uint rev = nextRevMethod is not null
                    ? Convert.ToUInt32(nextRevMethod.Invoke(playerInventories, [bucket.Row]), CultureInfo.InvariantCulture)
                    : 1u;

                string invLabel = $"0x00bf inventory {invId}: {label}";
                byte[] fullFrame = (byte[])invChangeMethod.Invoke(null, [invId, rev, bucket.RemovedIds, typedAdded, (byte)0])!;
                await TransmitFullPacketAsync(player, fullFrame, invLabel, sendPacket, cancellationToken).ConfigureAwait(false);

                if (showsOnModelMethod?.Invoke(null, [bucket.Row]) is true)
                {
                    refreshEquipment = true;
                }
            }
        }

        if (refreshEquipment && sendEquipmentMethod is not null)
        {
            await ((Task)sendEquipmentMethod.Invoke(null, [player, playerInventories])!).ConfigureAwait(false);
        }
    }

    private static async Task SendItemUsedAsync(
        object player,
        Type itemClrType,
        uint usedTypeHash,
        uint currencyInventoryId,
        uint currencyItemId,
        Func<object, byte[], string?, Task>? sendPacket,
        CancellationToken cancellationToken)
    {
        Type? instanceItemsType = FindCoreType(itemClrType.Assembly, "GameServer.Core.Protocol.Instance.InstanceItems");
        MethodInfo? itemUsedMethod = instanceItemsType?.GetMethod("ItemUsed", BindingFlags.Public | BindingFlags.Static);
        if (itemUsedMethod is null)
        {
            return;
        }

        ParameterInfo[] ps = itemUsedMethod.GetParameters();
        object?[] args = new object?[ps.Length];
        args[0] = usedTypeHash;
        args[1] = currencyInventoryId;
        args[2] = currencyItemId;
        for (int i = 3; i < ps.Length; i++)
        {
            args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ushort)0;
        }

        byte[] fullFrame = (byte[])itemUsedMethod.Invoke(null, args)!;
        await TransmitFullPacketAsync(player, fullFrame, "0x0263 custom currency used", sendPacket, cancellationToken).ConfigureAwait(false);
    }

    private static Task TransmitFullPacketAsync(
        object player,
        byte[] fullFrame,
        string label,
        Func<object, byte[], string?, Task>? sendPacket,
        CancellationToken cancellationToken)
    {
        if (sendPacket is not null)
        {
            return sendPacket(player, fullFrame, label);
        }

        ushort opcode = BinaryPrimitives.ReadUInt16BigEndian(fullFrame);
        return ReflectionGatewayAdapter.Shared.SendPacketAsync(player, opcode, fullFrame.AsMemory(2), label, cancellationToken);
    }

    private static void RunAtomically(object hintObject, Action action)
    {
        Type? savesType = FindGlobalType([hintObject.GetType().Assembly], "Saves");
        PropertyInfo? isOpenProp = savesType?.GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Static);
        if (savesType is not null && isOpenProp?.GetValue(null) is true)
        {
            MethodInfo? atomically = savesType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Atomically" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            if (atomically is not null)
            {
                atomically.Invoke(null, [action]);
                return;
            }
        }

        action();
    }

    private static (bool Consumed, object? Left) ConsumeOneAtLocation(object playerInventories, object? accountStash, GatewayItemLocation loc)
    {
        if (loc.Tab is int tab)
        {
            if (accountStash is null)
            {
                return (false, null);
            }

            Type stashType = accountStash.GetType();
            ushort stack = GetStackSize(loc.Item);
            if (stack <= 1)
            {
                object? taken = stashType.GetMethod("Take", BindingFlags.Public | BindingFlags.Instance, [typeof(int), typeof(uint)])!
                    .Invoke(accountStash, [tab, loc.ItemId]);
                return (taken is not null, null);
            }

            object? shrunk = stashType.GetMethod("Shrink", BindingFlags.Public | BindingFlags.Instance, [typeof(int), typeof(uint), typeof(int)])!
                .Invoke(accountStash, [tab, loc.ItemId, 1]);
            return (shrunk is not null, shrunk);
        }

        MethodInfo consumeOne = playerInventories.GetType().GetMethod("ConsumeOne", BindingFlags.Public | BindingFlags.Instance, [typeof(byte), typeof(uint)])!;
        object? consumption = consumeOne.Invoke(playerInventories, [loc.Row, loc.ItemId]);
        if (consumption is null)
        {
            return (false, null);
        }

        object? left = GetProp<object>(consumption, "Left");
        return (true, left);
    }

    private static object? ChangeItemAtLocation(object playerInventories, object? accountStash, GatewayItemLocation loc, object updatedItem)
    {
        Type itemClrType = updatedItem.GetType();
        Type funcType = typeof(Func<,>).MakeGenericType(itemClrType, itemClrType);
        Func<object, object> bridge = _ => updatedItem;
        ParameterExpression param = Expression.Parameter(itemClrType, "i");
        Delegate changeDelegate = Expression.Lambda(
            funcType,
            Expression.Convert(
                Expression.Call(Expression.Constant(bridge), bridge.GetType().GetMethod("Invoke")!, Expression.Convert(param, typeof(object))),
                itemClrType),
            param).Compile();

        if (loc.Tab is int tab)
        {
            if (accountStash is null)
            {
                return null;
            }

            MethodInfo changeMethod = accountStash.GetType().GetMethod("Change", BindingFlags.Public | BindingFlags.Instance, [typeof(int), typeof(uint), funcType])!;
            return changeMethod.Invoke(accountStash, [tab, loc.ItemId, changeDelegate]);
        }
        else
        {
            MethodInfo changeMethod = playerInventories.GetType().GetMethod("Change", BindingFlags.Public | BindingFlags.Instance, [typeof(byte), typeof(uint), funcType])!;
            return changeMethod.Invoke(playerInventories, [loc.Row, loc.ItemId, changeDelegate]);
        }
    }

    private static object AddItemToInventory(object playerInventories, byte row, byte x, byte y, object item)
    {
        MethodInfo addMethod = playerInventories.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance, [typeof(byte), typeof(byte), typeof(byte), item.GetType()])!;
        return addMethod.Invoke(playerInventories, [row, x, y, item])!;
    }

    private static (byte X, byte Y)? CallFreeCell(object playerInventories, byte row, byte width, byte height, Delegate shapeDelegate)
    {
        MethodInfo freeCellMethod = playerInventories.GetType().GetMethod("FreeCell", BindingFlags.Public | BindingFlags.Instance)!;
        object? res = freeCellMethod.Invoke(playerInventories, [row, width, height, shapeDelegate]);
        if (res is ValueTuple<byte, byte> tuple)
        {
            return tuple;
        }

        return null;
    }

    private static Delegate BuildShapeDelegate(
        object playerInventories,
        object itemCatalog,
        Type itemClrType,
        Func<object, object?>? customShapeOf)
    {
        Type invType = playerInventories.GetType();
        Type shapeType = invType.GetNestedType("Shape", BindingFlags.Public)
            ?? throw new InvalidOperationException("PlayerInventories.Shape type not found.");
        Type nullableShapeType = typeof(Nullable<>).MakeGenericType(shapeType);
        Type delegateType = typeof(Func<,>).MakeGenericType(itemClrType, nullableShapeType);

        MethodInfo? rowMethod = itemCatalog.GetType().GetMethod("Row", BindingFlags.Public | BindingFlags.Instance, [typeof(int)]);
        MethodInfo? createMethod = itemCatalog.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length >= 1);
        Type funcItemType = typeof(Func<>).MakeGenericType(itemClrType);
        ConstructorInfo shapeCtor = shapeType.GetConstructors()[0];

        Func<object, object?> resolver = itemObj =>
        {
            if (customShapeOf is not null)
            {
                return customShapeOf(itemObj);
            }

            uint typeHash = GetTypeHash(itemObj);
            object? baseType = InvokeByHash(itemCatalog, typeHash);
            if (baseType is null)
            {
                return null;
            }

            byte w = GetProp<byte>(baseType, "Width");
            byte h = GetProp<byte>(baseType, "Height");
            string cls = GetProp<string>(baseType, "Class") ?? string.Empty;
            ushort maxStack = Math.Max((ushort)1, GetProp<ushort>(baseType, "MaxStack"));
            bool becomesFull = GetProp<bool>(baseType, "BecomesWhenFull");
            int fullStackRow = GetProp<int>(baseType, "FullStackRow");
            bool isFossil = GetProp<bool>(baseType, "IsFossil");

            Delegate? fullStackFactory = null;
            if (becomesFull && fullStackRow >= 0 && rowMethod is not null && createMethod is not null)
            {
                object? fullBase = rowMethod.Invoke(itemCatalog, [fullStackRow]);
                if (fullBase is not null && GetProp<bool>(fullBase, "Supported"))
                {
                    Func<object> rawFactory = () => CreateCatalogItem(itemCatalog, fullBase);
                    fullStackFactory = Expression.Lambda(
                        funcItemType,
                        Expression.Convert(
                            Expression.Call(Expression.Constant(rawFactory), rawFactory.GetType().GetMethod("Invoke")!),
                            itemClrType)).Compile();
                }
            }

            return shapeCtor.Invoke([w, h, cls, maxStack, fullStackFactory, isFossil]);
        };

        ParameterExpression itemParam = Expression.Parameter(itemClrType, "item");
        MethodCallExpression invokeResolver = Expression.Call(
            Expression.Constant(resolver),
            resolver.GetType().GetMethod("Invoke")!,
            Expression.Convert(itemParam, typeof(object)));

        // Convert object? -> Nullable<Shape>: if null -> default(Nullable<Shape>), else (Nullable<Shape>)(Shape)val
        ParameterExpression tmp = Expression.Variable(typeof(object), "shapeObj");
        ConditionalExpression body = Expression.Condition(
            Expression.Equal(tmp, Expression.Constant(null)),
            Expression.Default(nullableShapeType),
            Expression.Convert(Expression.Unbox(tmp, shapeType), nullableShapeType));

        BlockExpression block = Expression.Block([tmp], Expression.Assign(tmp, invokeResolver), body);
        return Expression.Lambda(delegateType, block, itemParam).Compile();
    }

    private static bool TryBuildItemWithoutExplicitMod(
        object sourceItem,
        int explicitIndex,
        string? customName,
        bool clearCustomName,
        out object? updatedItem,
        out ushort stolenHash,
        out int[] stolenValues)
    {
        updatedItem = null;
        stolenHash = 0;
        stolenValues = [];

        if (GetProp<IEnumerable>(sourceItem, "Mods") is not { } modsSeq)
        {
            return false;
        }

        var kept = new List<object>();
        int seenExplicit = 0;
        bool found = false;

        foreach (object? mod in modsSeq)
        {
            if (mod is null) continue;
            byte section = Convert.ToByte(GetProp<object>(mod, "Section"), CultureInfo.InvariantCulture);
            if (section == (byte)ItemModSectionKind.Explicit)
            {
                if (seenExplicit == explicitIndex)
                {
                    stolenHash = GetProp<ushort>(mod, "Hash");
                    stolenValues = ExtractIntList(GetProp<IEnumerable>(mod, "Values"));
                    found = true;
                    seenExplicit++;
                    continue;
                }

                seenExplicit++;
            }

            kept.Add(mod);
        }

        if (!found)
        {
            return false;
        }

        object cloned = CloneRecord(sourceItem);
        SetTypedModsList(cloned, kept);
        if (clearCustomName)
        {
            cloned = LocalExileItemAffixService.WithGatewayItemCustomName(cloned, null);
        }
        else if (customName is not null)
        {
            cloned = LocalExileItemAffixService.WithGatewayItemCustomName(cloned, customName);
        }

        updatedItem = cloned;
        return true;
    }

    private static bool TryBuildItemWithReplacedExplicitMod(
        object targetItem,
        int replaceIndex,
        object replacementMod,
        string? customName,
        bool clearCustomName,
        out object? updatedItem)
    {
        updatedItem = null;
        if (GetProp<IEnumerable>(targetItem, "Mods") is not { } modsSeq)
        {
            return false;
        }

        var updated = new List<object>();
        int seenExplicit = 0;
        bool replaced = false;

        foreach (object? mod in modsSeq)
        {
            if (mod is null) continue;
            byte section = Convert.ToByte(GetProp<object>(mod, "Section"), CultureInfo.InvariantCulture);
            if (section == (byte)ItemModSectionKind.Explicit)
            {
                if (seenExplicit == replaceIndex)
                {
                    updated.Add(replacementMod);
                    replaced = true;
                    seenExplicit++;
                    continue;
                }

                seenExplicit++;
            }

            updated.Add(mod);
        }

        if (!replaced)
        {
            return false;
        }

        object cloned = CloneRecord(targetItem);
        SetTypedModsList(cloned, updated);
        if (clearCustomName)
        {
            cloned = LocalExileItemAffixService.WithGatewayItemCustomName(cloned, null);
        }
        else if (customName is not null)
        {
            cloned = LocalExileItemAffixService.WithGatewayItemCustomName(cloned, customName);
        }

        updatedItem = cloned;
        return true;
    }

    private static object CreateGatewayItemMod(Type itemClrType, ItemModSectionKind section, ushort modHash, int[] values)
    {
        Type? itemModType = FindCoreType(itemClrType.Assembly, "GameServer.Core.Protocol.Instance.ItemMod");
        Type? modSectionType = FindCoreType(itemClrType.Assembly, "GameServer.Core.Protocol.Instance.ModSection");
        if (itemModType is null || modSectionType is null)
        {
            throw new InvalidOperationException("GameServer.Core.Protocol.Instance.ItemMod/ModSection types not found.");
        }

        object sectionEnum = Enum.ToObject(modSectionType, (byte)section);
        ConstructorInfo ctor = itemModType.GetConstructors()[0];
        return ctor.Invoke([sectionEnum, modHash, values]);
    }

    private static void SetTypedModsList(object item, List<object> mods)
    {
        Type itemType = item.GetType();
        PropertyInfo modsProp = itemType.GetProperty("Mods", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Item.Mods property not found.");

        Type? itemModType = FindCoreType(itemType.Assembly, "GameServer.Core.Protocol.Instance.ItemMod")
            ?? modsProp.PropertyType.GetGenericArguments()[0];
        IList typedList = CreateTypedList(itemModType, mods);

        MethodInfo setter = modsProp.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException("Item.Mods setter not found.");
        setter.Invoke(item, [typedList]);
    }

    private static IList CreateTypedList(Type? elementType, IEnumerable<object> items)
    {
        Type elem = elementType ?? typeof(object);
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elem))!;
        foreach (object item in items)
        {
            list.Add(item);
        }

        return list;
    }

    private static object CloneRecord(object record)
    {
        MethodInfo cloneMethod = record.GetType().GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Type '{record.GetType().FullName}' does not expose <Clone>$.");
        return cloneMethod.Invoke(record, null)!;
    }

    private static object CloneItemWithStack(object template, ushort stackSize)
    {
        object cloned = CloneRecord(template);
        SetItemProperty(cloned.GetType(), cloned, "StackSize", stackSize);
        return cloned;
    }

    private static object? InvokeByHash(object itemCatalog, uint typeHash) =>
        itemCatalog.GetType().GetMethod("ByHash", BindingFlags.Public | BindingFlags.Instance, [typeof(uint)])?.Invoke(itemCatalog, [typeHash]);

    private static object CreateCatalogItem(object itemCatalog, object baseType)
    {
        Type catalogType = itemCatalog.GetType();
        MethodInfo createMethod = catalogType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == baseType.GetType())
            ?? throw new InvalidOperationException("ItemCatalog.Create(BaseType) not found.");

        ParameterInfo[] ps = createMethod.GetParameters();
        object?[] args = new object?[ps.Length];
        args[0] = baseType;
        for (int i = 1; i < ps.Length; i++)
        {
            args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
        }

        return createMethod.Invoke(null, args)!;
    }

    private static void SetItemProperty(Type itemType, object target, string propertyName, object? value)
    {
        PropertyInfo? prop = itemType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null) return;

        object? converted = value;
        if (value is not null && prop.PropertyType.IsEnum && !prop.PropertyType.IsInstanceOfType(value))
        {
            converted = Enum.ToObject(prop.PropertyType, value);
        }
        else if (value is not null && !prop.PropertyType.IsInstanceOfType(value))
        {
            Type underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            converted = Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
        }

        MethodInfo? setter = prop.GetSetMethod(nonPublic: true);
        if (setter is not null)
        {
            setter.Invoke(target, [converted]);
            return;
        }

        itemType.GetField($"<{propertyName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(target, converted);
    }

    private static bool IsWornRow(byte row) => row == 23 || row is >= 1 and <= 11 || row is 54 or 78 or 102;

    private static uint GetItemId(object inventoryItem) => GetProp<uint>(inventoryItem, "Id");
    private static uint GetTypeHash(object item) => GetProp<uint>(item, "TypeHash");
    private static ushort GetStackSize(object item) => GetProp<ushort>(item, "StackSize");

    private static int[] ExtractIntList(IEnumerable? seq)
    {
        if (seq is null) return [];
        var list = new List<int>();
        foreach (object? item in seq)
        {
            if (item is not null)
            {
                list.Add(Convert.ToInt32(item, CultureInfo.InvariantCulture));
            }
        }

        return [.. list];
    }

    private static IEnumerable? GetTupleOrPropEnumerable(object target, string propName, string tupleFieldName)
    {
        Type t = target.GetType();
        if (t.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target) is IEnumerable propSeq)
        {
            return propSeq;
        }

        if (t.GetField(tupleFieldName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target) is IEnumerable fieldSeq)
        {
            return fieldSeq;
        }

        return null;
    }

    private static T GetProp<T>(object target, string propertyName)
    {
        object? val = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
        return val is T typed ? typed : default!;
    }

    private static Type? ResolveInventoryItemType(Type playerInventoriesType)
    {
        MethodInfo? itemsMethod = playerInventoriesType.GetMethod("Items", BindingFlags.Public | BindingFlags.Instance, [typeof(byte)]);
        if (itemsMethod is not null && itemsMethod.ReturnType.IsGenericType)
        {
            return itemsMethod.ReturnType.GetGenericArguments()[0];
        }

        return FindGlobalType([playerInventoriesType.Assembly], "InventoryItem")
            ?? FindCoreType([playerInventoriesType.Assembly], "GameServer.Core.Protocol.Instance.InventoryItem");
    }

    private static bool IsGameTypeCompatible(Type gameType, object player, object playerInventories)
    {
        MethodInfo? sendTab = gameType.GetMethod("SendTabChange", BindingFlags.Public | BindingFlags.Static);
        MethodInfo? sendEquip = gameType.GetMethod("SendEquipment", BindingFlags.Public | BindingFlags.Static);
        if (sendTab is null && sendEquip is null)
        {
            return false;
        }

        if (sendTab is not null)
        {
            ParameterInfo[] tabParams = sendTab.GetParameters();
            if (tabParams.Length < 2 || !tabParams[0].ParameterType.IsInstanceOfType(player))
            {
                return false;
            }
        }

        if (sendEquip is not null)
        {
            ParameterInfo[] equipParams = sendEquip.GetParameters();
            if (equipParams.Length < 2 ||
                !equipParams[0].ParameterType.IsInstanceOfType(player) ||
                !equipParams[1].ParameterType.IsInstanceOfType(playerInventories))
            {
                return false;
            }
        }

        return true;
    }

    private static Type? FindCoreType(Assembly hintAssembly, string fullName) =>
        FindTypeByHints([hintAssembly], fullName);

    private static Type? FindCoreType(Assembly?[] hintAssemblies, string fullName) =>
        FindTypeByHints(hintAssemblies, fullName);

    private static Type? FindGlobalType(Assembly?[] hintAssemblies, string fullName, Func<Type, bool>? predicate = null) =>
        FindTypeByHints(hintAssemblies, fullName, predicate);

    private static Type? FindTypeByHints(Assembly?[] hintAssemblies, string fullName, Func<Type, bool>? predicate = null)
    {
        if (predicate is not null)
        {
            Assembly[] allAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = allAssemblies.Length - 1; i >= 0; i--)
            {
                if (allAssemblies[i].GetType(fullName, throwOnError: false) is { } matched &&
                    predicate(matched))
                {
                    return matched;
                }
            }

            return null;
        }

        foreach (Assembly? hint in hintAssemblies)
        {
            if (hint is not null &&
                hint.GetType(fullName, throwOnError: false) is { } hinted &&
                (predicate is null || predicate(hinted)))
            {
                return hinted;
            }
        }

        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

        var checkedAlcs = new List<AssemblyLoadContext>();
        foreach (Assembly? hint in hintAssemblies)
        {
            if (hint is null)
            {
                continue;
            }

            AssemblyLoadContext? hintAlc = AssemblyLoadContext.GetLoadContext(hint);
            if (hintAlc is null || checkedAlcs.Contains(hintAlc))
            {
                continue;
            }

            checkedAlcs.Add(hintAlc);
            for (int i = assemblies.Length - 1; i >= 0; i--)
            {
                Assembly asm = assemblies[i];
                if (AssemblyLoadContext.GetLoadContext(asm) == hintAlc &&
                    asm.GetType(fullName, throwOnError: false) is { } sameAlc &&
                    (predicate is null || predicate(sameAlc)))
                {
                    return sameAlc;
                }
            }
        }

        for (int i = assemblies.Length - 1; i >= 0; i--)
        {
            if (assemblies[i].GetType(fullName, throwOnError: false) is { } found &&
                (predicate is null || predicate(found)))
            {
                return found;
            }
        }

        return null;
    }
}
