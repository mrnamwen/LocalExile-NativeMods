using System.Collections.Concurrent;
using DualMod.StashQol.Shared;

namespace DualMod.StashQol.Server;

/// <summary>Stash tab kind mirrored from LocalExile's <c>StashRules</c>.</summary>
public enum QolStashTabKind
{
    Normal = 0,
    Premium = 1,
    Trade = 2,
    Currency = 3,
    Unique = 4,
    Map = 5,
    Divination = 6,
    Quad = 7,
    Essence = 8,
    Fragment = 9,
    Delve = 12,
    Blight = 13,
    Ultimatum = 14,
    Delirium = 15,
    Folder = 16,
    Flask = 17,
    Gem = 18,
    Breach = 22,
}

/// <summary>Item rarity mirrored from LocalExile.</summary>
public enum QolItemRarity
{
    Normal = 0,
    Magic = 1,
    Rare = 2,
    Unique = 3,
}

/// <summary>Server-side item model used by <see cref="StashQolServerMod"/> for unit-testable QoL operations.</summary>
public sealed record QolItemRecord
{
    public uint Id { get; init; }
    public uint TypeHash { get; init; }
    public string MetadataPath { get; init; } = string.Empty;
    public string BaseName { get; init; } = string.Empty;
    public string ItemClass { get; init; } = string.Empty;
    public byte Width { get; init; } = 1;
    public byte Height { get; init; } = 1;
    public byte X { get; init; }
    public byte Y { get; init; }
    public ushort StackSize { get; init; } = 1;
    public ushort MaxStackSize { get; init; } = 1;
    public QolItemRarity Rarity { get; init; } = QolItemRarity.Normal;
    public byte ItemLevel { get; init; } = 1;
    public byte Quality { get; init; }
    public byte Sockets { get; init; }
    public byte MaxSockets { get; init; }
    public byte MaxLinkGroup { get; init; }
    public bool IsQuestItem { get; init; }
    public bool IsOffered { get; init; }
    public bool HasSocketedGems { get; init; }
    public bool HasSkinTransfer { get; init; }
    public bool IsCorrupted { get; init; }
    public bool IsMirrored { get; init; }
    public bool IsForeseeing { get; init; }
    public bool IsBlightedMap { get; init; }
    public bool IsInputSlot { get; init; }
    public int? MapTier { get; init; }
    public string? MapUpgradeTargetMetadata { get; init; }
    public string? MapUpgradeTargetName { get; init; }
    public IReadOnlyList<int> Affinities { get; init; } = [];
    public IReadOnlyList<string> ModLines { get; init; } = [];
}

/// <summary>Stash tab metadata and contents for QoL inspection and transactions.</summary>
public sealed record QolStashTab(
    int TabIndex,
    string Name,
    QolStashTabKind Kind,
    bool IsPublic = false,
    bool IsHidden = false,
    bool IsSubTab = false,
    int? ParentTabIndex = null,
    int MapGroup = 0,
    int? MapTier = null,
    uint AffinityMask = 0,
    int DisplayOrder = 0,
    byte GridWidth = 12,
    byte GridHeight = 12);

/// <summary>Vendor sale quote result returned by <c>GameEvents.VendorQuote</c>.</summary>
public sealed record QolVendorQuote(
    IReadOnlyList<QolItemRecord> Rewards,
    IReadOnlyList<string> Recipes,
    string? Error = null);

/// <summary>Parsed PoB passive skill plan with ordered nodes and per-mastery effect choices.</summary>
public sealed record QolPobPlan(
    IReadOnlyList<ushort> Nodes,
    IReadOnlyDictionary<ushort, ushort> MasteryEffects,
    bool AutoApplyOnLevelUp = true);

/// <summary>Current passive tree state for a character used to compute valid frontier allocations.</summary>
public sealed record QolPassiveTreeState(
    ushort ClassStartNode,
    IReadOnlySet<ushort> AllocatedNodes,
    IReadOnlyDictionary<ushort, IReadOnlyList<ushort>> Adjacency,
    int UnspentPoints);

/// <summary>Configurable server settings for <see cref="StashQolServerMod"/>.</summary>
public sealed record StashQolSettings(
    int MaxRegexRolls = 500,
    bool VendorAllRequiresConfirmation = true,
    int BulkUseMaxActions = 200,
    bool PobAutoApplyOnLevelUp = true,
    bool UpgradeCorruptedMaps = false);

/// <summary>Result of a Deposit All execution.</summary>
public sealed record DepositAllOutcome(bool Success, int DepositedCount, int SkippedCount, string Message);

/// <summary>Result of a Vendor All execution.</summary>
public sealed record VendorAllOutcome(bool Success, int SoldCount, int RewardStacks, string Message, bool RequiresConfirmation = false);

/// <summary>Result of an Inventory Sort execution.</summary>
public sealed record InventorySortOutcome(bool Success, int MovedCount, string Message);

/// <summary>Result of a Map Tier 3-to-1 Upgrade execution.</summary>
public sealed record MapUpgradeOutcome(bool Success, int UpgradedCount, int ConsumedMaps, string Message);

/// <summary>Result of a Blight Oil Upgrade Max execution.</summary>
public sealed record BlightOilUpgradeOutcome(bool Success, int TotalUpgrades, string HighestTierReached, string Message);

/// <summary>Result of a Roll Until Regex Match execution.</summary>
public sealed record RegexRollOutcome(bool Success, bool Matched, int RollsUsed, string Message);

/// <summary>Result of a Ctrl+Shift+RMB Bulk Stack Use execution.</summary>
public sealed record BulkUseOutcome(bool Success, int ActionsExecuted, string Message);

/// <summary>Result of a bulk Stacked Deck open batch.</summary>
public sealed record BulkStackedDeckResult(bool Success, int OpenedCount, bool StoppedInventoryFull, string Error = "");

/// <summary>Result of a bulk Orb of Regret / Orb of Unmaking use batch.</summary>
public sealed record BulkRefundOrbResult(bool Success, int UsedCount, int NewBalance, string Error = "");

/// <summary>Result of a bulk targeted currency application batch.</summary>
public sealed record BulkTargetedCurrencyResult(bool Success, int AppliedCount, string StopReason, string Error = "");

/// <summary>Result of applying planned PoB passive nodes.</summary>
public sealed record PobApplyOutcome(bool Success, IReadOnlyList<ushort> AllocatedNodes, int RemainingPlanned, string Message);

/// <summary>
/// Server-world backend abstraction for <see cref="StashQolServerMod"/>.
/// Implemented in tests by <see cref="InMemoryStashQolWorldBackend"/> and in LocalExile Gateway by
/// <c>StashQolLocalExileEntry</c> using Part 2a APIs (<c>AccountStash.FindAffinityTab</c>,
/// <c>GameEvents.VendorQuote</c>, <c>IPlayerWorld.AllocatePassivesAsync</c>, <c>Saves.Atomically</c>, etc.).
/// </summary>
public interface IStashQolWorldBackend
{
    bool IsStashOpen(string connectionId);
    bool HasActiveTradeOrSale(string connectionId);
    IReadOnlyList<QolItemRecord> GetMainInventory(string connectionId);
    IReadOnlyList<QolStashTab> GetStashTabs(string connectionId);
    IReadOnlyList<QolItemRecord> GetStashTabItems(string connectionId, int tabIndex);
    int? FindAffinityTab(string connectionId, QolItemRecord item);
    bool TryDepositItem(string connectionId, uint itemId, int targetTabIndex);

    QolVendorQuote QuoteVendor(string connectionId, int sourceTabIndex, IReadOnlyList<QolItemRecord> offered);
    bool CommitVendorSale(string connectionId, int sourceTabIndex, IReadOnlyList<uint> soldItemIds, IReadOnlyList<QolItemRecord> rewards, out string error);

    bool CommitInventoryRepack(string connectionId, IReadOnlyList<(uint ItemId, byte X, byte Y)> placements, out string error);

    bool CommitMapUpgrades(
        string connectionId,
        int mapTabIndex,
        IReadOnlyList<(int SourceChildTab, uint ItemId)> consumed,
        IReadOnlyList<(string TargetMetadata, string TargetName, int TargetTier, byte ItemLevel)> created,
        out string error);

    bool CommitBlightOilUpgrades(
        string connectionId,
        int blightTabIndex,
        IReadOnlyDictionary<string, ushort> finalOilCounts,
        out string error);

    bool TryGetItem(string connectionId, uint inventoryId, uint itemId, out QolItemRecord item, out bool isWorn, out bool isPublicStash);
    int CountCurrency(string connectionId, string currencyMetadataPath);
    QolItemRecord? SimulateCurrencyRoll(string connectionId, uint targetInventoryId, QolItemRecord target, string currencyMetadataPath, int rollIndex);
    bool CommitRegexRoll(string connectionId, uint targetInventoryId, uint targetItemId, string currencyMetadataPath, int orbsConsumed, QolItemRecord finalItem, out string error);

    Task<BulkStackedDeckResult> BulkOpenStackedDecksAsync(string connectionId, uint inventoryId, uint itemId, int maxCount, CancellationToken cancellationToken = default);
    Task<BulkRefundOrbResult> BulkUseRefundOrbAsync(string connectionId, uint inventoryId, uint itemId, bool isAtlasUnmaking, int maxCount, CancellationToken cancellationToken = default);
    Task<BulkTargetedCurrencyResult> BulkApplyTargetedCurrencyAsync(string connectionId, uint currencyInventoryId, uint currencyItemId, string currencyMetadataPath, uint targetInventoryId, uint targetItemId, int maxCount, CancellationToken cancellationToken = default);

    QolPassiveTreeState? GetPassiveTreeState(string connectionId);
    Task<string?> AllocatePassivesAsync(string connectionId, IReadOnlyList<ushort> nodes, ushort? masteryEffect, string reason, CancellationToken cancellationToken = default);
}

/// <summary>
/// Rich deterministic in-memory backend implementing all 8 <c>DualMod.StashQol</c> server surfaces for unit tests.
/// </summary>
public sealed class InMemoryStashQolWorldBackend : IStashQolWorldBackend
{
    public sealed class PlayerState
    {
        public bool StashOpen { get; set; } = true;
        public bool HasActiveTradeOrSale { get; set; }
        public List<QolItemRecord> MainInventory { get; } = [];
        public List<QolItemRecord> WornItems { get; } = [];
        public List<QolStashTab> StashTabs { get; } = [];
        public Dictionary<int, List<QolItemRecord>> TabItems { get; } = [];
        public Dictionary<string, int> CurrencyPool { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int RefundPoints { get; set; }
        public int AtlasRespecPoints { get; set; }
        public int MaxMainInventorySlots { get; set; } = StashQolProtocol.MainInventoryWidth * StashQolProtocol.MainInventoryHeight;
        public QolPassiveTreeState? PassiveTree { get; set; }
        public List<(IReadOnlyList<ushort> Nodes, ushort? MasteryEffect, string Reason)> PassiveAllocationLog { get; } = [];
        public Func<QolItemRecord, string, int, QolItemRecord?>? CustomRollSimulator { get; set; }
        public Func<IReadOnlyList<QolItemRecord>, QolVendorQuote>? CustomVendorQuoter { get; set; }
        public uint NextId { get; set; } = 1000;
    }

    private readonly ConcurrentDictionary<string, PlayerState> _players = new(StringComparer.Ordinal);

    public PlayerState GetOrCreatePlayer(string connectionId) =>
        _players.GetOrAdd(connectionId, static _ => new PlayerState());

    public bool IsStashOpen(string connectionId) => GetOrCreatePlayer(connectionId).StashOpen;

    public bool HasActiveTradeOrSale(string connectionId) => GetOrCreatePlayer(connectionId).HasActiveTradeOrSale;

    public IReadOnlyList<QolItemRecord> GetMainInventory(string connectionId) =>
        [.. GetOrCreatePlayer(connectionId).MainInventory];

    public IReadOnlyList<QolStashTab> GetStashTabs(string connectionId) =>
        [.. GetOrCreatePlayer(connectionId).StashTabs];

    public IReadOnlyList<QolItemRecord> GetStashTabItems(string connectionId, int tabIndex)
    {
        PlayerState p = GetOrCreatePlayer(connectionId);
        return p.TabItems.TryGetValue(tabIndex, out List<QolItemRecord>? items) ? [.. items] : [];
    }

    public int? FindAffinityTab(string connectionId, QolItemRecord item)
    {
        if (item.IsQuestItem || item.Affinities.Count == 0)
        {
            return null;
        }

        uint itemMask = 0;
        foreach (int affinity in item.Affinities)
        {
            if (affinity is > 0 and < 32)
            {
                itemMask |= 1u << affinity;
            }
        }

        if (itemMask == 0)
        {
            return null;
        }

        PlayerState p = GetOrCreatePlayer(connectionId);
        return p.StashTabs
            .Where(t => !t.IsHidden && !t.IsPublic && !t.IsSubTab && t.Kind != QolStashTabKind.Folder && (t.AffinityMask & itemMask) != 0)
            .OrderBy(t => t.DisplayOrder)
            .ThenBy(t => t.TabIndex)
            .Select(t => (int?)t.TabIndex)
            .FirstOrDefault();
    }

    public bool TryDepositItem(string connectionId, uint itemId, int targetTabIndex)
    {
        PlayerState p = GetOrCreatePlayer(connectionId);
        QolStashTab? tab = p.StashTabs.FirstOrDefault(t => t.TabIndex == targetTabIndex);
        if (tab is null || tab.IsHidden || tab.IsPublic || tab.Kind == QolStashTabKind.Folder)
        {
            return false;
        }

        int idx = p.MainInventory.FindIndex(i => i.Id == itemId);
        if (idx < 0)
        {
            return false;
        }

        QolItemRecord item = p.MainInventory[idx];
        if (item.IsQuestItem || item.IsOffered)
        {
            return false;
        }

        if (!p.TabItems.TryGetValue(targetTabIndex, out List<QolItemRecord>? dest))
        {
            dest = [];
            p.TabItems[targetTabIndex] = dest;
        }

        // Merge stacks if matching item exists in destination tab.
        if (item.MaxStackSize > 1)
        {
            int existingIdx = dest.FindIndex(i => i.MetadataPath.Equals(item.MetadataPath, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0)
            {
                ushort limit = tab.Kind is QolStashTabKind.Currency or QolStashTabKind.Blight or QolStashTabKind.Essence or QolStashTabKind.Fragment
                    ? StashQolProtocol.BlightMaxStackSize
                    : item.MaxStackSize;
                int room = limit - dest[existingIdx].StackSize;
                if (room > 0)
                {
                    int moved = Math.Min(room, item.StackSize);
                    dest[existingIdx] = dest[existingIdx] with { StackSize = (ushort)(dest[existingIdx].StackSize + moved) };
                    if (moved == item.StackSize)
                    {
                        p.MainInventory.RemoveAt(idx);
                    }
                    else
                    {
                        p.MainInventory[idx] = item with { StackSize = (ushort)(item.StackSize - moved) };
                    }

                    return true;
                }
            }
        }

        p.MainInventory.RemoveAt(idx);
        dest.Add(item);
        return true;
    }

    public QolVendorQuote QuoteVendor(string connectionId, int sourceTabIndex, IReadOnlyList<QolItemRecord> offered)
    {
        _ = sourceTabIndex;
        PlayerState p = GetOrCreatePlayer(connectionId);
        if (p.CustomVendorQuoter is not null)
        {
            return p.CustomVendorQuoter(offered);
        }

        if (offered.Count == 0)
        {
            return new QolVendorQuote([], []);
        }

        int fragments = offered.Sum(i => Math.Max(1, (int)i.StackSize));
        QolItemRecord reward = new()
        {
            Id = ++p.NextId,
            MetadataPath = "Metadata/Items/Currency/CurrencyIdentificationShard",
            BaseName = "Scroll Fragment",
            ItemClass = "StackableCurrency",
            StackSize = (ushort)Math.Min(ushort.MaxValue, fragments),
            MaxStackSize = 5,
        };
        return new QolVendorQuote([reward], ["basic sale"]);
    }

    public bool CommitVendorSale(string connectionId, int sourceTabIndex, IReadOnlyList<uint> soldItemIds, IReadOnlyList<QolItemRecord> rewards, out string error)
    {
        PlayerState p = GetOrCreatePlayer(connectionId);
        if (!p.TabItems.TryGetValue(sourceTabIndex, out List<QolItemRecord>? tabItems))
        {
            error = "Source tab not found.";
            return false;
        }

        int usedCells = p.MainInventory.Sum(i => i.Width * i.Height);
        int neededCells = rewards.Sum(r => r.Width * r.Height);
        if (usedCells + neededCells > p.MaxMainInventorySlots)
        {
            error = "Not enough space in main inventory for vendor rewards.";
            return false;
        }

        HashSet<uint> soldSet = [.. soldItemIds];
        tabItems.RemoveAll(i => soldSet.Contains(i.Id));
        foreach (QolItemRecord reward in rewards)
        {
            p.MainInventory.Add(reward with { Id = ++p.NextId });
        }

        error = string.Empty;
        return true;
    }

    public bool CommitInventoryRepack(string connectionId, IReadOnlyList<(uint ItemId, byte X, byte Y)> placements, out string error)
    {
        PlayerState p = GetOrCreatePlayer(connectionId);
        Dictionary<uint, (byte X, byte Y)> map = placements.ToDictionary(x => x.ItemId, x => (x.X, x.Y));
        for (int i = 0; i < p.MainInventory.Count; i++)
        {
            QolItemRecord item = p.MainInventory[i];
            if (map.TryGetValue(item.Id, out (byte X, byte Y) pos))
            {
                p.MainInventory[i] = item with { X = pos.X, Y = pos.Y };
            }
        }

        error = string.Empty;
        return true;
    }

    public bool CommitMapUpgrades(
        string connectionId,
        int mapTabIndex,
        IReadOnlyList<(int SourceChildTab, uint ItemId)> consumed,
        IReadOnlyList<(string TargetMetadata, string TargetName, int TargetTier, byte ItemLevel)> created,
        out string error)
    {
        PlayerState p = GetOrCreatePlayer(connectionId);
        foreach ((int childTab, uint itemId) in consumed)
        {
            if (p.TabItems.TryGetValue(childTab, out List<QolItemRecord>? list))
            {
                list.RemoveAll(i => i.Id == itemId);
            }
        }

        foreach ((string meta, string name, int tier, byte ilvl) in created)
        {
            QolStashTab? destChild = p.StashTabs.FirstOrDefault(t =>
                t.ParentTabIndex == mapTabIndex && t.IsSubTab && t.MapGroup == 0 && t.MapTier == tier);
            int destIdx = destChild?.TabIndex ?? mapTabIndex;
            if (!p.TabItems.TryGetValue(destIdx, out List<QolItemRecord>? list))
            {
                list = [];
                p.TabItems[destIdx] = list;
            }

            list.Add(new QolItemRecord
            {
                Id = ++p.NextId,
                MetadataPath = meta,
                BaseName = name,
                ItemClass = "Map",
                Rarity = QolItemRarity.Normal,
                ItemLevel = ilvl,
                MapTier = tier,
            });
        }

        error = string.Empty;
        return true;
    }

    public bool CommitBlightOilUpgrades(string connectionId, int blightTabIndex, IReadOnlyDictionary<string, ushort> finalOilCounts, out string error)
    {
        PlayerState p = GetOrCreatePlayer(connectionId);
        if (!p.TabItems.TryGetValue(blightTabIndex, out List<QolItemRecord>? list))
        {
            error = "Blight tab not found.";
            return false;
        }

        HashSet<string> tierSet = new(StashQolProtocol.BlightOilTiers, StringComparer.OrdinalIgnoreCase);
        // Preserve non-tiered oils and anything in anoint crafting input slots.
        list.RemoveAll(i => !i.IsInputSlot && tierSet.Contains(i.BaseName));
        foreach ((string oilName, ushort count) in finalOilCounts)
        {
            if (count == 0)
            {
                continue;
            }

            list.Add(new QolItemRecord
            {
                Id = ++p.NextId,
                MetadataPath = $"Metadata/Items/Currency/Oil{oilName.Replace(" ", string.Empty, StringComparison.Ordinal)}",
                BaseName = oilName,
                ItemClass = "StackableCurrency",
                StackSize = count,
                MaxStackSize = StashQolProtocol.BlightMaxStackSize,
            });
        }

        error = string.Empty;
        return true;
    }

    public bool TryGetItem(string connectionId, uint inventoryId, uint itemId, out QolItemRecord item, out bool isWorn, out bool isPublicStash)
    {
        PlayerState p = GetOrCreatePlayer(connectionId);
        if (inventoryId == 1)
        {
            QolItemRecord? found = p.MainInventory.FirstOrDefault(i => i.Id == itemId);
            if (found is not null)
            {
                item = found;
                isWorn = false;
                isPublicStash = false;
                return true;
            }
        }

        QolItemRecord? worn = p.WornItems.FirstOrDefault(i => i.Id == itemId);
        if (worn is not null)
        {
            item = worn;
            isWorn = true;
            isPublicStash = false;
            return true;
        }

        if (inventoryId >= 0x800)
        {
            int tabIdx = (int)(inventoryId - 0x800);
            QolStashTab? tab = p.StashTabs.FirstOrDefault(t => t.TabIndex == tabIdx);
            if (tab is not null && p.TabItems.TryGetValue(tabIdx, out List<QolItemRecord>? items))
            {
                QolItemRecord? found = items.FirstOrDefault(i => i.Id == itemId);
                if (found is not null)
                {
                    item = found;
                    isWorn = false;
                    isPublicStash = tab.IsPublic;
                    return true;
                }
            }
        }

        item = null!;
        isWorn = false;
        isPublicStash = false;
        return false;
    }

    public int CountCurrency(string connectionId, string currencyMetadataPath)
    {
        PlayerState p = GetOrCreatePlayer(connectionId);
        int total = p.CurrencyPool.GetValueOrDefault(currencyMetadataPath);
        total += p.MainInventory
            .Where(i => i.MetadataPath.Equals(currencyMetadataPath, StringComparison.OrdinalIgnoreCase))
            .Sum(i => i.StackSize);
        foreach (QolStashTab tab in p.StashTabs.Where(t => !t.IsPublic && !t.IsHidden))
        {
            if (p.TabItems.TryGetValue(tab.TabIndex, out List<QolItemRecord>? items))
            {
                total += items
                    .Where(i => i.MetadataPath.Equals(currencyMetadataPath, StringComparison.OrdinalIgnoreCase))
                    .Sum(i => i.StackSize);
            }
        }

        return total;
    }

    public QolItemRecord? SimulateCurrencyRoll(string connectionId, uint targetInventoryId, QolItemRecord target, string currencyMetadataPath, int rollIndex)
    {
        _ = targetInventoryId;
        PlayerState p = GetOrCreatePlayer(connectionId);
        if (p.CustomRollSimulator is not null)
        {
            return p.CustomRollSimulator(target, currencyMetadataPath, rollIndex);
        }

        return target with
        {
            Rarity = QolItemRarity.Rare,
            ModLines = [$"+{20 + rollIndex * 10} to maximum Life", $"+{10 + rollIndex * 5}% to Fire Resistance"],
        };
    }

    public bool CommitRegexRoll(string connectionId, uint targetInventoryId, uint targetItemId, string currencyMetadataPath, int orbsConsumed, QolItemRecord finalItem, out string error)
    {
        PlayerState p = GetOrCreatePlayer(connectionId);
        if (CountCurrency(connectionId, currencyMetadataPath) < orbsConsumed)
        {
            error = "Insufficient currency.";
            return false;
        }

        int remaining = orbsConsumed;
        if (p.CurrencyPool.TryGetValue(currencyMetadataPath, out int pooled) && pooled > 0)
        {
            int take = Math.Min(pooled, remaining);
            p.CurrencyPool[currencyMetadataPath] = pooled - take;
            remaining -= take;
        }

        remaining = ConsumeFromList(p.MainInventory, currencyMetadataPath, remaining);
        foreach (QolStashTab tab in p.StashTabs.Where(t => !t.IsPublic && !t.IsHidden))
        {
            if (remaining <= 0)
            {
                break;
            }

            if (p.TabItems.TryGetValue(tab.TabIndex, out List<QolItemRecord>? items))
            {
                remaining = ConsumeFromList(items, currencyMetadataPath, remaining);
            }
        }

        if (targetInventoryId == 1)
        {
            int idx = p.MainInventory.FindIndex(i => i.Id == targetItemId);
            if (idx >= 0)
            {
                p.MainInventory[idx] = finalItem;
            }
        }
        else if (targetInventoryId >= 0x800)
        {
            int tabIdx = (int)(targetInventoryId - 0x800);
            if (p.TabItems.TryGetValue(tabIdx, out List<QolItemRecord>? items))
            {
                int idx = items.FindIndex(i => i.Id == targetItemId);
                if (idx >= 0)
                {
                    items[idx] = finalItem;
                }
            }
        }

        error = string.Empty;
        return true;
    }

    public Task<BulkStackedDeckResult> BulkOpenStackedDecksAsync(string connectionId, uint inventoryId, uint itemId, int maxCount, CancellationToken cancellationToken = default)
    {
        if (!TryGetItem(connectionId, inventoryId, itemId, out QolItemRecord deck, out _, out bool isPublic))
        {
            return Task.FromResult(new BulkStackedDeckResult(false, 0, false, "Stacked Deck not found."));
        }

        if (isPublic)
        {
            return Task.FromResult(new BulkStackedDeckResult(false, 0, false, "Cannot use items from a public stash tab."));
        }

        PlayerState p = GetOrCreatePlayer(connectionId);
        int freeSlots = Math.Max(0, p.MaxMainInventorySlots - p.MainInventory.Sum(i => i.Width * i.Height));
        int toOpen = Math.Min(deck.StackSize, Math.Min(maxCount, freeSlots));
        if (toOpen == 0)
        {
            bool full = freeSlots == 0;
            return Task.FromResult(new BulkStackedDeckResult(false, 0, full, full ? "Main inventory is full." : "Nothing to open."));
        }

        ShrinkOrRemove(p, inventoryId, itemId, toOpen);
        for (int i = 0; i < toOpen; i++)
        {
            p.MainInventory.Add(new QolItemRecord
            {
                Id = ++p.NextId,
                MetadataPath = "Metadata/Items/DivinationCards/DivinationCardTheGambler",
                BaseName = "The Gambler",
                ItemClass = "DivinationCard",
                StackSize = 1,
                MaxStackSize = 5,
            });
        }

        bool stoppedFull = toOpen < Math.Min(deck.StackSize, maxCount);
        return Task.FromResult(new BulkStackedDeckResult(true, toOpen, stoppedFull));
    }

    public Task<BulkRefundOrbResult> BulkUseRefundOrbAsync(string connectionId, uint inventoryId, uint itemId, bool isAtlasUnmaking, int maxCount, CancellationToken cancellationToken = default)
    {
        if (!TryGetItem(connectionId, inventoryId, itemId, out QolItemRecord orb, out _, out bool isPublic))
        {
            return Task.FromResult(new BulkRefundOrbResult(false, 0, 0, "Orb stack not found."));
        }

        if (isPublic)
        {
            return Task.FromResult(new BulkRefundOrbResult(false, 0, 0, "Cannot use orbs from a public stash tab."));
        }

        PlayerState p = GetOrCreatePlayer(connectionId);
        int current = isAtlasUnmaking ? p.AtlasRespecPoints : p.RefundPoints;
        int capRoom = ushort.MaxValue - current;
        int count = Math.Min(orb.StackSize, Math.Min(maxCount, capRoom));
        if (count <= 0)
        {
            return Task.FromResult(new BulkRefundOrbResult(false, 0, current, "Respec point cap reached."));
        }

        ShrinkOrRemove(p, inventoryId, itemId, count);
        int newBalance;
        if (isAtlasUnmaking)
        {
            p.AtlasRespecPoints += count;
            newBalance = p.AtlasRespecPoints;
        }
        else
        {
            p.RefundPoints += count;
            newBalance = p.RefundPoints;
        }

        return Task.FromResult(new BulkRefundOrbResult(true, count, newBalance));
    }

    public Task<BulkTargetedCurrencyResult> BulkApplyTargetedCurrencyAsync(
        string connectionId,
        uint currencyInventoryId,
        uint currencyItemId,
        string currencyMetadataPath,
        uint targetInventoryId,
        uint targetItemId,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        int appliedCount = 0;
        string stopReason = string.Empty;

        if (!TryGetItem(connectionId, currencyInventoryId, currencyItemId, out QolItemRecord currency, out _, out bool currencyPublic))
        {
            return Task.FromResult(new BulkTargetedCurrencyResult(false, 0, string.Empty, "Currency stack not found."));
        }

        if (currencyPublic)
        {
            return Task.FromResult(new BulkTargetedCurrencyResult(false, 0, string.Empty, "Cannot consume currency from a public stash tab."));
        }

        if (!TryGetItem(connectionId, targetInventoryId, targetItemId, out QolItemRecord target, out bool isWorn, out bool targetPublic))
        {
            return Task.FromResult(new BulkTargetedCurrencyResult(false, 0, string.Empty, "Target item not found."));
        }

        if (targetPublic)
        {
            return Task.FromResult(new BulkTargetedCurrencyResult(false, 0, string.Empty, "Cannot modify an item in a public stash tab."));
        }

        if (target.IsMirrored || target.IsForeseeing || target.IsCorrupted)
        {
            return Task.FromResult(new BulkTargetedCurrencyResult(false, 0, string.Empty, "Target item cannot be modified by this currency."));
        }

        bool isQualityOrb = currencyMetadataPath.Contains("Quality", StringComparison.OrdinalIgnoreCase);
        bool isFusing = currencyMetadataPath.Contains("RerollSocketLinks", StringComparison.OrdinalIgnoreCase);
        bool isJeweller = currencyMetadataPath.Contains("RerollSocketNumbers", StringComparison.OrdinalIgnoreCase);

        if (isWorn && !isQualityOrb)
        {
            return Task.FromResult(new BulkTargetedCurrencyResult(false, 0, string.Empty, "Cannot use this currency on an equipped item."));
        }

        PlayerState p = GetOrCreatePlayer(connectionId);
        int limit = Math.Min(currency.StackSize, maxCount);
        QolItemRecord current = target;

        for (int i = 0; i < limit; i++)
        {
            if (isQualityOrb)
            {
                if (current.Quality >= 20)
                {
                    stopReason = "reached 20% quality";
                    break;
                }

                current = current with { Quality = (byte)Math.Min(20, current.Quality + 5) };
                appliedCount++;
                if (current.Quality >= 20)
                {
                    stopReason = "reached 20% quality";
                    break;
                }
            }
            else if (isJeweller)
            {
                byte maxS = current.MaxSockets > 0 ? current.MaxSockets : (byte)6;
                if (current.Sockets >= maxS)
                {
                    stopReason = $"reached max {maxS} sockets";
                    break;
                }

                current = current with { Sockets = (byte)Math.Min(maxS, current.Sockets + 1) };
                appliedCount++;
                if (current.Sockets >= maxS)
                {
                    stopReason = $"reached max {maxS} sockets";
                    break;
                }
            }
            else if (isFusing)
            {
                byte goalLinks = current.Sockets > 0 ? current.Sockets : (byte)6;
                if (current.MaxLinkGroup >= goalLinks)
                {
                    stopReason = $"reached {goalLinks}-link";
                    break;
                }

                current = current with { MaxLinkGroup = (byte)Math.Min(goalLinks, current.MaxLinkGroup + 1) };
                appliedCount++;
                if (current.MaxLinkGroup >= goalLinks)
                {
                    stopReason = $"reached {goalLinks}-link";
                    break;
                }
            }
            else
            {
                return Task.FromResult(new BulkTargetedCurrencyResult(false, 0, string.Empty, $"Unsupported bulk targeted currency '{currencyMetadataPath}'."));
            }
        }

        if (appliedCount == 0)
        {
            string err = string.IsNullOrEmpty(stopReason) ? "Target already at maximum." : $"Target already {stopReason}.";
            return Task.FromResult(new BulkTargetedCurrencyResult(false, 0, stopReason, err));
        }

        ShrinkOrRemove(p, currencyInventoryId, currencyItemId, appliedCount);
        ReplaceItem(p, targetInventoryId, targetItemId, current);
        if (string.IsNullOrEmpty(stopReason))
        {
            stopReason = "stack exhausted";
        }

        return Task.FromResult(new BulkTargetedCurrencyResult(true, appliedCount, stopReason));
    }

    public QolPassiveTreeState? GetPassiveTreeState(string connectionId) =>
        GetOrCreatePlayer(connectionId).PassiveTree;

    public Task<string?> AllocatePassivesAsync(string connectionId, IReadOnlyList<ushort> nodes, ushort? masteryEffect, string reason, CancellationToken cancellationToken = default)
    {
        PlayerState p = GetOrCreatePlayer(connectionId);
        if (p.PassiveTree is not { } tree)
        {
            return Task.FromResult<string?>("Passive tree state unavailable.");
        }

        if (nodes.Count > tree.UnspentPoints)
        {
            return Task.FromResult<string?>("Not enough unspent passive points.");
        }

        HashSet<ushort> next = [.. tree.AllocatedNodes];
        foreach (ushort node in nodes)
        {
            if (next.Contains(node))
            {
                return Task.FromResult<string?>($"Node {node} is already allocated.");
            }

            bool connected = node == tree.ClassStartNode
                || (tree.Adjacency.TryGetValue(tree.ClassStartNode, out IReadOnlyList<ushort>? startAdj) && startAdj.Contains(node))
                || next.Any(alloc => tree.Adjacency.TryGetValue(alloc, out IReadOnlyList<ushort>? adj) && adj.Contains(node));
            if (!connected)
            {
                return Task.FromResult<string?>($"Node {node} is not connected to allocated frontier.");
            }

            next.Add(node);
        }

        p.PassiveTree = tree with
        {
            AllocatedNodes = next,
            UnspentPoints = tree.UnspentPoints - nodes.Count,
        };
        p.PassiveAllocationLog.Add(([.. nodes], masteryEffect, reason));
        return Task.FromResult<string?>(null);
    }

    private static int ConsumeFromList(List<QolItemRecord> list, string metadataPath, int remaining)
    {
        for (int i = list.Count - 1; i >= 0 && remaining > 0; i--)
        {
            if (!list[i].MetadataPath.Equals(metadataPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int take = Math.Min(list[i].StackSize, remaining);
            if (take == list[i].StackSize)
            {
                list.RemoveAt(i);
            }
            else
            {
                list[i] = list[i] with { StackSize = (ushort)(list[i].StackSize - take) };
            }

            remaining -= take;
        }

        return remaining;
    }

    private static void ShrinkOrRemove(PlayerState p, uint inventoryId, uint itemId, int count)
    {
        List<QolItemRecord>? list = inventoryId == 1
            ? p.MainInventory
            : inventoryId >= 0x800 && p.TabItems.TryGetValue((int)(inventoryId - 0x800), out List<QolItemRecord>? tabList)
                ? tabList
                : null;
        if (list is null)
        {
            return;
        }

        int idx = list.FindIndex(i => i.Id == itemId);
        if (idx < 0)
        {
            return;
        }

        if (count >= list[idx].StackSize)
        {
            list.RemoveAt(idx);
        }
        else
        {
            list[idx] = list[idx] with { StackSize = (ushort)(list[idx].StackSize - count) };
        }
    }

    private static void ReplaceItem(PlayerState p, uint inventoryId, uint itemId, QolItemRecord updated)
    {
        List<QolItemRecord>? list = inventoryId == 1
            ? p.MainInventory
            : inventoryId >= 0x800 && p.TabItems.TryGetValue((int)(inventoryId - 0x800), out List<QolItemRecord>? tabList)
                ? tabList
                : null;
        if (list is null)
        {
            return;
        }

        int idx = list.FindIndex(i => i.Id == itemId);
        if (idx >= 0)
        {
            list[idx] = updated;
        }
    }
}
