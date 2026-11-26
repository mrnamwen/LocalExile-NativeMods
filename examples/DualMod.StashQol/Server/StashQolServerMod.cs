using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DualMod.StashQol.Shared;
using LocalExile.Client.ServerBridge;

namespace DualMod.StashQol.Server;

/// <summary>
/// Server half of <c>DualMod.StashQol</c> implementing all 8 stash &amp; inventory QoL operations,
/// deterministic 12×5 bin-pack sorting, PoB passive plan parsing &amp; frontier auto-allocation,
/// and <see cref="ClientBridgeHost"/> typed message handlers.
/// </summary>
public sealed class StashQolServerMod : IDisposable
{
    private readonly ClientBridgeHost _bridge;
    private readonly IStashQolWorldBackend _backend;
    private readonly StashQolSettings _settings;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly ConcurrentDictionary<string, QolPobPlan> _pobPlans = new(StringComparer.Ordinal);
    private bool _disposed;

    public StashQolServerMod(
        ClientBridgeHost bridge,
        IStashQolWorldBackend backend,
        StashQolSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(backend);
        _bridge = bridge;
        _backend = backend;
        _settings = settings ?? new StashQolSettings();
    }

    public StashQolSettings Settings => _settings;

    public QolPobPlan? GetPobPlan(string connectionId) =>
        _pobPlans.TryGetValue(connectionId, out QolPobPlan? plan) ? plan : null;

    /// <summary>Subscribes all typed QoL request handlers onto <see cref="ClientBridgeHost"/>.</summary>
    public void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _subscriptions.Add(_bridge.On<DepositAllRequest>(StashQolProtocol.ChannelName, async (session, req) =>
        {
            DepositAllOutcome outcome = ExecuteDepositAll(session.ConnectionId, req);
            await SendToastAsync(session, "deposit_all", "Deposit All", outcome.Message, outcome.Success ? "Success" : "Warning", outcome.DepositedCount);
        }));

        _subscriptions.Add(_bridge.On<VendorAllRequest>(StashQolProtocol.ChannelName, async (session, req) =>
        {
            VendorAllOutcome outcome = ExecuteVendorAll(session.ConnectionId, req);
            string severity = outcome.RequiresConfirmation ? "Warning" : outcome.Success ? "Success" : "Error";
            await SendToastAsync(session, "vendor_all", "Vendor All", outcome.Message, severity, outcome.SoldCount, outcome.RequiresConfirmation, outcome.RequiresConfirmation ? req.TabIndex : null);
        }));

        _subscriptions.Add(_bridge.On<InventorySortRequest>(StashQolProtocol.ChannelName, async (session, req) =>
        {
            InventorySortOutcome outcome = ExecuteInventorySort(session.ConnectionId, req);
            await SendToastAsync(session, "inventory_sort", "Inventory Sort", outcome.Message, outcome.Success ? "Success" : "Warning", outcome.MovedCount);
        }));

        _subscriptions.Add(_bridge.On<UpgradeMapTiersRequest>(StashQolProtocol.ChannelName, async (session, req) =>
        {
            MapUpgradeOutcome outcome = ExecuteMapTierUpgrades(session.ConnectionId, req);
            await SendToastAsync(session, "upgrade_maps", "Map Upgrade 3->1", outcome.Message, outcome.Success && outcome.UpgradedCount > 0 ? "Success" : "Info", outcome.UpgradedCount);
        }));

        _subscriptions.Add(_bridge.On<UpgradeBlightOilsRequest>(StashQolProtocol.ChannelName, async (session, req) =>
        {
            BlightOilUpgradeOutcome outcome = ExecuteBlightOilUpgrades(session.ConnectionId, req);
            await SendToastAsync(session, "upgrade_oils", "Blight Oils", outcome.Message, outcome.Success && outcome.TotalUpgrades > 0 ? "Success" : "Info", outcome.TotalUpgrades);
        }));

        _subscriptions.Add(_bridge.On<RollUntilRegexRequest>(StashQolProtocol.ChannelName, async (session, req) =>
        {
            RegexRollOutcome outcome = ExecuteRollUntilRegex(session.ConnectionId, req);
            string severity = outcome.Matched ? "Success" : outcome.Success ? "Warning" : "Error";
            await SendToastAsync(session, "regex_roll", "Regex Roller", outcome.Message, severity, outcome.RollsUsed);
        }));

        _subscriptions.Add(_bridge.On<BulkUseRequest>(StashQolProtocol.ChannelName, async (session, req) =>
        {
            BulkUseOutcome outcome = await ExecuteBulkUseAsync(session.ConnectionId, req);
            await SendToastAsync(session, "bulk_use", "Bulk Stack Use", outcome.Message, outcome.Success ? "Success" : "Warning", outcome.ActionsExecuted);
        }));

        _subscriptions.Add(_bridge.On<PobImportRequest>(StashQolProtocol.ChannelName, async (session, req) =>
        {
            PobApplyOutcome outcome = await ImportAndApplyPobPlanAsync(session.ConnectionId, req);
            await SendToastAsync(session, "pob_passives", "PoB Passives", outcome.Message, outcome.Success ? "Success" : "Error", outcome.AllocatedNodes.Count);
        }));

        _subscriptions.Add(_bridge.On<PobToggleAutoApplyRequest>(StashQolProtocol.ChannelName, async (session, req) =>
        {
            if (_pobPlans.TryGetValue(session.ConnectionId, out QolPobPlan? existing))
            {
                _pobPlans[session.ConnectionId] = existing with { AutoApplyOnLevelUp = req.Enabled };
            }

            await SendToastAsync(session, "pob_passives", "PoB Auto-Apply", req.Enabled ? "Enabled auto-apply on level up." : "Disabled auto-apply on level up.", "Info");
        }));

        _subscriptions.Add(_bridge.On<PobClearPlanRequest>(StashQolProtocol.ChannelName, async (session, req) =>
        {
            _ = req;
            _pobPlans.TryRemove(session.ConnectionId, out _);
            await SendToastAsync(session, "pob_passives", "PoB Passives", "Cleared stored PoB passive plan.", "Info");
        }));
    }

    public void OnPlayerLeft(string connectionId) => _pobPlans.TryRemove(connectionId, out _);

    /// <summary>1. Deposit All: routes eligible main-inventory items to affinity tabs or fallback tab.</summary>
    public DepositAllOutcome ExecuteDepositAll(string connectionId, DepositAllRequest request)
    {
        if (!_backend.IsStashOpen(connectionId))
        {
            return new DepositAllOutcome(false, 0, 0, "Open your stash before depositing items.");
        }

        IReadOnlyList<QolItemRecord> inventory = _backend.GetMainInventory(connectionId);
        if (inventory.Count == 0)
        {
            return new DepositAllOutcome(true, 0, 0, "Main inventory is already empty.");
        }

        IReadOnlyList<QolStashTab> tabs = _backend.GetStashTabs(connectionId);
        int? fallback = request.FallbackTabIndex;
        if (fallback is not int fb || !IsValidDepositFallback(tabs, fb))
        {
            fallback = tabs
                .Where(t => !t.IsHidden && !t.IsPublic && !t.IsSubTab && t.Kind != QolStashTabKind.Folder)
                .OrderBy(t => t.DisplayOrder)
                .ThenBy(t => t.TabIndex)
                .Select(t => (int?)t.TabIndex)
                .FirstOrDefault();
        }

        int deposited = 0;
        int skipped = 0;
        foreach (QolItemRecord item in inventory)
        {
            if (item.IsQuestItem || item.IsOffered)
            {
                skipped++;
                continue;
            }

            int? targetTab = request.RespectAffinities ? _backend.FindAffinityTab(connectionId, item) : null;
            targetTab ??= fallback;

            if (targetTab is int tabIndex && _backend.TryDepositItem(connectionId, item.Id, tabIndex))
            {
                deposited++;
            }
            else
            {
                skipped++;
            }
        }

        string msg = deposited > 0
            ? $"Deposited {deposited} item(s) to stash" + (skipped > 0 ? $" ({skipped} skipped)." : ".")
            : $"No items deposited ({skipped} skipped).";
        return new DepositAllOutcome(deposited > 0, deposited, skipped, msg);
    }

    /// <summary>2. Vendor All: refuses unsafe tabs/items, quotes via <c>VendorQuote</c>, and commits atomically.</summary>
    public VendorAllOutcome ExecuteVendorAll(string connectionId, VendorAllRequest request)
    {
        if (!_backend.IsStashOpen(connectionId))
        {
            return new VendorAllOutcome(false, 0, 0, "Open your stash before vendoring a tab.");
        }

        IReadOnlyList<QolStashTab> tabs = _backend.GetStashTabs(connectionId);
        QolStashTab? tab = tabs.FirstOrDefault(t => t.TabIndex == request.TabIndex);
        if (tab is null)
        {
            return new VendorAllOutcome(false, 0, 0, $"Stash tab {request.TabIndex} was not found.");
        }

        if (tab.IsPublic)
        {
            return new VendorAllOutcome(false, 0, 0, $"Refused: tab '{tab.Name}' is public/trade-listed.");
        }

        if (tab.IsHidden || tab.Kind == QolStashTabKind.Folder)
        {
            return new VendorAllOutcome(false, 0, 0, $"Refused: tab '{tab.Name}' is a folder or hidden tab.");
        }

        if (!tab.IsSubTab && tab.Kind is QolStashTabKind.Map or QolStashTabKind.Unique)
        {
            return new VendorAllOutcome(false, 0, 0, $"Refused: parent {tab.Kind} tabs cannot be bulk-vendored.");
        }

        IReadOnlyList<QolItemRecord> tabItems = _backend.GetStashTabItems(connectionId, tab.TabIndex);
        List<QolItemRecord> eligible = [.. tabItems.Where(i => !i.IsQuestItem && !i.HasSocketedGems && !i.HasSkinTransfer && !i.IsOffered)];
        if (eligible.Count == 0)
        {
            return new VendorAllOutcome(false, 0, 0, $"No eligible items to vendor in tab '{tab.Name}'.");
        }

        if (_settings.VendorAllRequiresConfirmation && !request.Confirmed)
        {
            return new VendorAllOutcome(
                false,
                0,
                0,
                $"Confirm Vendor All for {eligible.Count} eligible item(s) in '{tab.Name}'.",
                RequiresConfirmation: true);
        }

        QolVendorQuote quote = _backend.QuoteVendor(connectionId, eligible);
        if (!string.IsNullOrWhiteSpace(quote.Error))
        {
            return new VendorAllOutcome(false, 0, 0, $"Vendor quote refused: {quote.Error}");
        }

        if (!_backend.CommitVendorSale(connectionId, tab.TabIndex, [.. eligible.Select(i => i.Id)], quote.Rewards, out string commitError))
        {
            return new VendorAllOutcome(false, 0, 0, $"Vendor All failed: {commitError}");
        }

        return new VendorAllOutcome(
            true,
            eligible.Count,
            quote.Rewards.Count,
            $"Vendored {eligible.Count} item(s) from '{tab.Name}' for {quote.Rewards.Count} reward stack(s).");
    }

    /// <summary>3. Inventory Sort: deterministic 12×5 2D bin-pack sort committed in one atomic batch.</summary>
    public InventorySortOutcome ExecuteInventorySort(string connectionId, InventorySortRequest _)
    {
        if (_backend.HasActiveTradeOrSale(connectionId))
        {
            return new InventorySortOutcome(false, 0, "Cannot sort inventory while a trade or vendor sale is open.");
        }

        IReadOnlyList<QolItemRecord> items = _backend.GetMainInventory(connectionId);
        if (items.Count == 0)
        {
            return new InventorySortOutcome(true, 0, "Inventory is empty.");
        }

        if (items.Any(i => i.IsOffered))
        {
            return new InventorySortOutcome(false, 0, "Cannot sort inventory while offered trade imprints are present.");
        }

        IReadOnlyList<(uint ItemId, byte X, byte Y)>? packed = PackGrid(items, StashQolProtocol.MainInventoryWidth, StashQolProtocol.MainInventoryHeight);
        if (packed is null)
        {
            return new InventorySortOutcome(false, 0, "Could not pack all items into the 12x5 inventory grid.");
        }

        int moved = 0;
        Dictionary<uint, QolItemRecord> byId = items.ToDictionary(i => i.Id);
        foreach ((uint id, byte x, byte y) in packed)
        {
            if (byId[id].X != x || byId[id].Y != y)
            {
                moved++;
            }
        }

        if (moved == 0)
        {
            return new InventorySortOutcome(true, 0, "Inventory is already sorted.");
        }

        if (!_backend.CommitInventoryRepack(connectionId, packed, out string error))
        {
            return new InventorySortOutcome(false, 0, $"Inventory sort failed: {error}");
        }

        return new InventorySortOutcome(true, moved, $"Sorted inventory ({moved} item(s) repositioned).");
    }

    /// <summary>Deterministic 2D grid bin-packer (area desc -> height desc -> width desc -> class -> base name -> rarity desc).</summary>
    public static IReadOnlyList<(uint ItemId, byte X, byte Y)>? PackGrid(
        IReadOnlyList<QolItemRecord> items,
        int gridWidth = StashQolProtocol.MainInventoryWidth,
        int gridHeight = StashQolProtocol.MainInventoryHeight)
    {
        List<QolItemRecord> ordered =
        [
            .. items
                .OrderByDescending(i => i.Width * i.Height)
                .ThenByDescending(i => i.Height)
                .ThenByDescending(i => i.Width)
                .ThenBy(i => i.ItemClass, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.BaseName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(i => (int)i.Rarity)
                .ThenByDescending(i => i.StackSize)
                .ThenBy(i => i.Id),
        ];

        bool[,] occupied = new bool[gridWidth, gridHeight];
        List<(uint ItemId, byte X, byte Y)> result = new(ordered.Count);

        foreach (QolItemRecord item in ordered)
        {
            int w = Math.Max(1, (int)item.Width);
            int h = Math.Max(1, (int)item.Height);
            bool placed = false;

            // Column-major scan matches Path of Exile's left-to-right inventory packing ergonomics.
            for (int x = 0; x <= gridWidth - w && !placed; x++)
            {
                for (int y = 0; y <= gridHeight - h && !placed; y++)
                {
                    if (!FitsAt(occupied, x, y, w, h))
                    {
                        continue;
                    }

                    Mark(occupied, x, y, w, h);
                    result.Add((item.Id, (byte)x, (byte)y));
                    placed = true;
                }
            }

            if (!placed)
            {
                return null;
            }
        }

        return result;
    }

    /// <summary>4. Map Tier 3-to-1 Upgrade: group-0 normal maps only, skipping corrupted/mirrored/unique/blighted.</summary>
    public MapUpgradeOutcome ExecuteMapTierUpgrades(string connectionId, UpgradeMapTiersRequest request)
    {
        if (!_backend.IsStashOpen(connectionId))
        {
            return new MapUpgradeOutcome(false, 0, 0, "Open your Map Stash tab first.");
        }

        IReadOnlyList<QolStashTab> tabs = _backend.GetStashTabs(connectionId);
        QolStashTab? mapParent = tabs.FirstOrDefault(t => t.TabIndex == request.MapTabIndex && t.Kind == QolStashTabKind.Map && !t.IsSubTab);
        if (mapParent is null)
        {
            return new MapUpgradeOutcome(false, 0, 0, "Selected tab is not a Map Stash tab.");
        }

        bool allowCorrupted = request.IncludeCorrupted || _settings.UpgradeCorruptedMaps;
        List<QolStashTab> group0Children =
        [
            .. tabs.Where(t => t.ParentTabIndex == mapParent.TabIndex && t.IsSubTab && t.MapGroup == 0
                && (request.SpecificTier is null || t.MapTier == request.SpecificTier)),
        ];

        List<(int ChildTab, QolItemRecord Item)> candidates = [];
        foreach (QolStashTab child in group0Children)
        {
            foreach (QolItemRecord map in _backend.GetStashTabItems(connectionId, child.TabIndex))
            {
                if (map.Rarity == QolItemRarity.Unique || map.IsMirrored || map.IsBlightedMap)
                {
                    continue;
                }

                if (map.IsCorrupted && !allowCorrupted)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(map.MapUpgradeTargetMetadata) || map.MapTier is not int tier)
                {
                    continue;
                }

                if (request.SpecificTier is int onlyTier && tier != onlyTier)
                {
                    continue;
                }

                candidates.Add((child.TabIndex, map));
            }
        }

        List<(int SourceChildTab, uint ItemId)> consumed = [];
        List<(string TargetMetadata, string TargetName, int TargetTier, byte ItemLevel)> created = [];

        foreach (IGrouping<string, (int ChildTab, QolItemRecord Item)> group in candidates.GroupBy(c => c.Item.MetadataPath, StringComparer.OrdinalIgnoreCase))
        {
            List<(int ChildTab, QolItemRecord Item)> list = [.. group.OrderBy(c => c.Item.ItemLevel).ThenBy(c => c.Item.Id)];
            int triplets = list.Count / 3;
            for (int t = 0; t < triplets; t++)
            {
                (int ChildTab, QolItemRecord Item) a = list[t * 3];
                (int ChildTab, QolItemRecord Item) b = list[t * 3 + 1];
                (int ChildTab, QolItemRecord Item) c = list[t * 3 + 2];

                consumed.Add((a.ChildTab, a.Item.Id));
                consumed.Add((b.ChildTab, b.Item.Id));
                consumed.Add((c.ChildTab, c.Item.Id));

                byte minIlvl = Math.Min(a.Item.ItemLevel, Math.Min(b.Item.ItemLevel, c.Item.ItemLevel));
                int nextTier = (a.Item.MapTier ?? 1) + 1;
                created.Add((
                    a.Item.MapUpgradeTargetMetadata!,
                    a.Item.MapUpgradeTargetName ?? $"Tier {nextTier} Map",
                    nextTier,
                    minIlvl));
            }
        }

        if (created.Count == 0)
        {
            return new MapUpgradeOutcome(true, 0, 0, "No eligible 3-of-a-kind maps available to upgrade.");
        }

        if (!_backend.CommitMapUpgrades(connectionId, mapParent.TabIndex, consumed, created, out string error))
        {
            return new MapUpgradeOutcome(false, 0, 0, $"Map upgrade failed: {error}");
        }

        return new MapUpgradeOutcome(true, created.Count, consumed.Count, $"Upgraded {consumed.Count} maps into {created.Count} higher-tier map(s).");
    }

    /// <summary>5. Blight Oil Upgrade Max: 13-tier linear chain respecting input slots, special oils, and 5000 ceiling.</summary>
    public BlightOilUpgradeOutcome ExecuteBlightOilUpgrades(string connectionId, UpgradeBlightOilsRequest request)
    {
        if (!_backend.IsStashOpen(connectionId))
        {
            return new BlightOilUpgradeOutcome(false, 0, string.Empty, "Open your Blight Stash tab first.");
        }

        IReadOnlyList<QolStashTab> tabs = _backend.GetStashTabs(connectionId);
        QolStashTab? blightTab = tabs.FirstOrDefault(t => t.TabIndex == request.BlightTabIndex && t.Kind == QolStashTabKind.Blight);
        if (blightTab is null)
        {
            return new BlightOilUpgradeOutcome(false, 0, string.Empty, "Selected tab is not a Blight Stash tab.");
        }

        int stopIndex = StashQolProtocol.BlightOilTiers.Count - 1;
        if (!string.IsNullOrWhiteSpace(request.StopAtTier))
        {
            for (int i = 0; i < StashQolProtocol.BlightOilTiers.Count; i++)
            {
                if (StashQolProtocol.BlightOilTiers[i].Equals(request.StopAtTier.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    stopIndex = i;
                    break;
                }
            }
        }

        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (string tier in StashQolProtocol.BlightOilTiers)
        {
            counts[tier] = 0;
        }

        foreach (QolItemRecord item in _backend.GetStashTabItems(connectionId, blightTab.TabIndex))
        {
            if (item.IsInputSlot || !counts.ContainsKey(item.BaseName))
            {
                continue;
            }

            counts[item.BaseName] += item.StackSize;
        }

        int totalUpgrades = 0;
        string highestTier = string.Empty;

        for (int i = 0; i < stopIndex; i++)
        {
            string fromTier = StashQolProtocol.BlightOilTiers[i];
            string toTier = StashQolProtocol.BlightOilTiers[i + 1];

            int available = counts[fromTier];
            int destRoom = StashQolProtocol.BlightMaxStackSize - counts[toTier];
            int upgrades = Math.Min(available / 3, Math.Max(0, destRoom));
            if (upgrades <= 0)
            {
                continue;
            }

            counts[fromTier] -= upgrades * 3;
            counts[toTier] += upgrades;
            totalUpgrades += upgrades;
            highestTier = toTier;
        }

        if (totalUpgrades == 0)
        {
            return new BlightOilUpgradeOutcome(true, 0, string.Empty, "No Blight oils eligible to upgrade.");
        }

        Dictionary<string, ushort> finalCounts = counts.ToDictionary(
            kv => kv.Key,
            kv => (ushort)Math.Clamp(kv.Value, 0, StashQolProtocol.BlightMaxStackSize),
            StringComparer.OrdinalIgnoreCase);

        if (!_backend.CommitBlightOilUpgrades(connectionId, blightTab.TabIndex, finalCounts, out string error))
        {
            return new BlightOilUpgradeOutcome(false, 0, string.Empty, $"Blight oil upgrade failed: {error}");
        }

        return new BlightOilUpgradeOutcome(true, totalUpgrades, highestTier, $"Completed {totalUpgrades} Blight oil upgrade(s) (up to {highestTier}).");
    }

    /// <summary>6. Roll Until Regex Match: bounded loop with 50ms regex timeout and state guards.</summary>
    public RegexRollOutcome ExecuteRollUntilRegex(string connectionId, RollUntilRegexRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Pattern))
        {
            return new RegexRollOutcome(false, false, 0, "Regex pattern cannot be empty.");
        }

        Regex regex;
        try
        {
            RegexOptions opts = RegexOptions.CultureInvariant | (request.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            regex = new Regex(request.Pattern, opts, TimeSpan.FromMilliseconds(StashQolProtocol.RegexTimeoutMilliseconds));
        }
        catch (ArgumentException ex)
        {
            return new RegexRollOutcome(false, false, 0, $"Invalid regex pattern: {ex.Message}");
        }

        if (!_backend.TryGetItem(connectionId, request.TargetInventoryId, request.TargetItemId, out QolItemRecord target, out bool isWorn, out bool isPublic))
        {
            return new RegexRollOutcome(false, false, 0, "Target item could not be found.");
        }

        if (isWorn)
        {
            return new RegexRollOutcome(false, false, 0, "Refused: cannot roll an equipped item.");
        }

        if (isPublic)
        {
            return new RegexRollOutcome(false, false, 0, "Refused: cannot roll an item in a public stash tab.");
        }

        if (target.IsMirrored)
        {
            return new RegexRollOutcome(false, false, 0, "Refused: mirrored items cannot be modified.");
        }

        if (target.IsCorrupted)
        {
            return new RegexRollOutcome(false, false, 0, "Refused: corrupted items cannot be rolled.");
        }

        if (target.IsForeseeing)
        {
            return new RegexRollOutcome(false, false, 0, "Refused: item has an active Hinekora's Lock foresight.");
        }

        int hardCap = Math.Clamp(_settings.MaxRegexRolls, 1, StashQolProtocol.MaxRegexRollsHardCap);
        int maxRolls = Math.Clamp(request.MaxRolls, 1, hardCap);
        int availableOrbs = _backend.CountCurrency(connectionId, request.CurrencyMetadataPath);
        if (availableOrbs <= 0)
        {
            return new RegexRollOutcome(false, false, 0, $"No currency '{request.CurrencyMetadataPath}' available.");
        }

        int budget = Math.Min(maxRolls, availableOrbs);
        QolItemRecord current = target;
        int rollsUsed = 0;
        bool matched = false;

        try
        {
            for (int i = 1; i <= budget; i++)
            {
                QolItemRecord? next = _backend.SimulateCurrencyRoll(connectionId, current, request.CurrencyMetadataPath, i);
                if (next is null)
                {
                    break;
                }

                current = next;
                rollsUsed = i;

                if (MatchesItem(regex, current, request.MatchFullCard))
                {
                    matched = true;
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            if (rollsUsed > 0)
            {
                _backend.CommitRegexRoll(connectionId, request.TargetInventoryId, request.TargetItemId, request.CurrencyMetadataPath, rollsUsed, current, out _);
            }

            return new RegexRollOutcome(false, false, rollsUsed, $"Regex evaluation timed out after {rollsUsed} roll(s).");
        }

        if (rollsUsed == 0)
        {
            return new RegexRollOutcome(false, false, 0, "Currency could not be applied to the target item.");
        }

        if (!_backend.CommitRegexRoll(connectionId, request.TargetInventoryId, request.TargetItemId, request.CurrencyMetadataPath, rollsUsed, current, out string error))
        {
            return new RegexRollOutcome(false, false, 0, $"Failed to commit rolls: {error}");
        }

        string message = matched
            ? $"Matched regex on {current.BaseName} in {rollsUsed} roll(s)."
            : $"Stopped after {rollsUsed} roll(s) without a regex match.";
        return new RegexRollOutcome(true, matched, rollsUsed, message);
    }

    /// <summary>7. Ctrl+Shift+RMB Bulk Stack Use: self-use (Stacked Decks, Regret, Unmaking) and targeted currency.</summary>
    public async Task<BulkUseOutcome> ExecuteBulkUseAsync(string connectionId, BulkUseRequest request, CancellationToken cancellationToken = default)
    {
        int maxActions = Math.Clamp(
            request.MaxActions > 0 ? request.MaxActions : _settings.BulkUseMaxActions,
            1,
            Math.Min(_settings.BulkUseMaxActions, StashQolProtocol.MaxBulkUseHardCap));

        // Case A: Targeted currency on hovered item (cursor holds active currency use).
        if (request.CursorSourceInventoryId is uint cursorInv &&
            request.CursorItemId is uint cursorItem &&
            !string.IsNullOrWhiteSpace(request.CursorMetadataPath))
        {
            BulkTargetedCurrencyResult targetResult = await _backend.BulkApplyTargetedCurrencyAsync(
                connectionId,
                cursorInv,
                cursorItem,
                request.CursorMetadataPath,
                request.HoveredInventoryId,
                request.HoveredItemId,
                maxActions,
                cancellationToken);
            if (!targetResult.Success)
            {
                return new BulkUseOutcome(false, 0, targetResult.Error);
            }

            return new BulkUseOutcome(true, targetResult.AppliedCount, $"Applied currency {targetResult.AppliedCount}x ({targetResult.StopReason}).");
        }

        // Case B: Self-use Stacked Deck -> directly to MainInventory, stopping cleanly when full.
        if (request.HoveredMetadataPath.Equals(StashQolProtocol.StackedDeckMetadata, StringComparison.OrdinalIgnoreCase))
        {
            BulkStackedDeckResult deckResult = await _backend.BulkOpenStackedDecksAsync(
                connectionId,
                request.HoveredInventoryId,
                request.HoveredItemId,
                maxActions,
                cancellationToken);
            if (!deckResult.Success)
            {
                return new BulkUseOutcome(false, 0, deckResult.Error);
            }

            string suffix = deckResult.StoppedInventoryFull ? " (stopped: inventory full)." : ".";
            return new BulkUseOutcome(true, deckResult.OpenedCount, $"Opened {deckResult.OpenedCount} Stacked Deck(s){suffix}");
        }

        // Case C: Self-use Orb of Regret / Orb of Unmaking (inventory or non-public stash tab).
        bool isRegret = request.HoveredMetadataPath.Equals(StashQolProtocol.OrbOfRegretMetadata, StringComparison.OrdinalIgnoreCase);
        bool isUnmaking = request.HoveredMetadataPath.Equals(StashQolProtocol.OrbOfUnmakingMetadata, StringComparison.OrdinalIgnoreCase);
        if (isRegret || isUnmaking)
        {
            BulkRefundOrbResult refundResult = await _backend.BulkUseRefundOrbAsync(
                connectionId,
                request.HoveredInventoryId,
                request.HoveredItemId,
                isAtlasUnmaking: isUnmaking,
                maxActions,
                cancellationToken);
            if (!refundResult.Success)
            {
                return new BulkUseOutcome(false, 0, refundResult.Error);
            }

            string label = isUnmaking ? "Atlas respec" : "passive refund";
            return new BulkUseOutcome(true, refundResult.UsedCount, $"Used {refundResult.UsedCount} orb(s); {refundResult.NewBalance} {label} point(s) available.");
        }

        return new BulkUseOutcome(false, 0, $"Item '{request.HoveredMetadataPath}' does not support bulk right-click use.");
    }

    /// <summary>8a. Parses and stores a PoB passive plan (Base64/zlib, raw XML, or node ID list) and optionally allocates immediately.</summary>
    public async Task<PobApplyOutcome> ImportAndApplyPobPlanAsync(
        string connectionId,
        PobImportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryParsePobPlan(request.Payload, request.AutoApplyOnLevelUp, out QolPobPlan? plan, out string parseError))
        {
            return new PobApplyOutcome(false, [], 0, $"Invalid PoB plan: {parseError}");
        }

        _pobPlans[connectionId] = plan!;
        if (!request.ApplyImmediately)
        {
            return new PobApplyOutcome(true, [], plan!.Nodes.Count, $"Imported PoB plan ({plan.Nodes.Count} nodes; auto-apply={(plan.AutoApplyOnLevelUp ? "ON" : "OFF")}).");
        }

        return await ApplyPlannedPassivesAsync(connectionId, "PoB import", cancellationToken);
    }

    /// <summary>8b. Hook invoked on <c>GameEvents.LevelUp</c> (and quest passive point rewards) to auto-allocate planned frontier nodes and toast the client.</summary>
    public async Task<PobApplyOutcome> OnLevelUpAsync(
        string connectionId,
        int previousLevel,
        int newLevel,
        CancellationToken cancellationToken = default)
    {
        if (newLevel <= previousLevel || !_pobPlans.TryGetValue(connectionId, out QolPobPlan? plan) || !plan.AutoApplyOnLevelUp)
        {
            return new PobApplyOutcome(true, [], 0, string.Empty);
        }

        PobApplyOutcome outcome = await ApplyPlannedPassivesAsync(connectionId, $"PoB level {newLevel}", cancellationToken);
        if (outcome.AllocatedNodes.Count > 0 && _bridge.IsClientReady(connectionId))
        {
            IClientSession session = _bridge.For(connectionId);
            await SendToastAsync(session, "pob_passives", $"Level {newLevel} Auto-Passives", outcome.Message, "Success", outcome.AllocatedNodes.Count, cancellationToken: cancellationToken);
        }

        return outcome;
    }

    /// <summary>Allocates connected planned nodes along the passive frontier, isolating mastery-effect nodes into separate calls.</summary>
    public async Task<PobApplyOutcome> ApplyPlannedPassivesAsync(
        string connectionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!_pobPlans.TryGetValue(connectionId, out QolPobPlan? plan))
        {
            return new PobApplyOutcome(false, [], 0, "No PoB passive plan imported.");
        }

        QolPassiveTreeState? tree = _backend.GetPassiveTreeState(connectionId);
        if (tree is null)
        {
            return new PobApplyOutcome(false, [], plan.Nodes.Count, "Passive tree state unavailable.");
        }

        HashSet<ushort> allocated = [.. tree.AllocatedNodes];
        List<ushort> remaining = [.. plan.Nodes.Where(n => !allocated.Contains(n))];
        int unspent = tree.UnspentPoints;
        List<ushort> newlyAllocated = [];
        List<ushort> batch = [];

        while (unspent > 0 && remaining.Count > 0)
        {
            int nextIndex = FindNextFrontierIndex(remaining, tree.ClassStartNode, allocated, tree.Adjacency);
            if (nextIndex < 0)
            {
                break;
            }

            ushort node = remaining[nextIndex];
            remaining.RemoveAt(nextIndex);

            if (plan.MasteryEffects.TryGetValue(node, out ushort masteryEffect))
            {
                // Flush ordinary batch first; Game.ChangePassives takes a single masteryEffect per call.
                if (batch.Count > 0)
                {
                    string? batchErr = await _backend.AllocatePassivesAsync(connectionId, [.. batch], null, reason, cancellationToken);
                    if (batchErr is not null)
                    {
                        return new PobApplyOutcome(newlyAllocated.Count > 0, newlyAllocated, remaining.Count + 1, $"Stopped allocating passives: {batchErr}");
                    }

                    newlyAllocated.AddRange(batch);
                    batch.Clear();
                }

                string? masteryErr = await _backend.AllocatePassivesAsync(connectionId, [node], masteryEffect, reason, cancellationToken);
                if (masteryErr is not null)
                {
                    return new PobApplyOutcome(newlyAllocated.Count > 0, newlyAllocated, remaining.Count + 1, $"Stopped allocating mastery {node}: {masteryErr}");
                }

                allocated.Add(node);
                newlyAllocated.Add(node);
                unspent--;
            }
            else
            {
                allocated.Add(node);
                batch.Add(node);
                unspent--;
            }
        }

        if (batch.Count > 0)
        {
            string? batchErr = await _backend.AllocatePassivesAsync(connectionId, [.. batch], null, reason, cancellationToken);
            if (batchErr is not null)
            {
                return new PobApplyOutcome(newlyAllocated.Count > 0, newlyAllocated, remaining.Count + batch.Count, $"Stopped allocating passives: {batchErr}");
            }

            newlyAllocated.AddRange(batch);
        }

        string msg = newlyAllocated.Count > 0
            ? $"Allocated {newlyAllocated.Count} passive node(s) ({remaining.Count} remaining in PoB plan)."
            : $"PoB plan ready ({remaining.Count} node(s) remaining; no allocatable frontier points right now).";
        return new PobApplyOutcome(true, newlyAllocated, remaining.Count, msg);
    }

    /// <summary>Parses a PoB passive plan from raw XML, Base64/zlib PoB export string, or comma/whitespace node ID list (with optional <c>node:masteryEffect</c>).</summary>
    public static bool TryParsePobPlan(string? payload, bool autoApplyOnLevelUp, out QolPobPlan? plan, out string error)
    {
        plan = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
        {
            error = "Payload is empty.";
            return false;
        }

        string trimmed = payload.Trim();

        // 1. Try XML directly or via PoB Base64url + Zlib decoding.
        string? xmlText = trimmed.StartsWith('<') ? trimmed : TryDecodeZlibBase64Xml(trimmed);
        if (xmlText is not null)
        {
            return TryParsePobXml(xmlText, autoApplyOnLevelUp, out plan, out error);
        }

        // 2. Fallback: comma/whitespace-separated node IDs (optionally "nodeId:masteryEffectHash").
        List<ushort> nodes = [];
        Dictionary<ushort, ushort> masteries = [];
        HashSet<ushort> seen = [];

        foreach (string token in trimmed.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = token.IndexOf(':');
            string nodePart = colon >= 0 ? token[..colon] : token;
            if (!ushort.TryParse(nodePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort nodeId) || nodeId == 0)
            {
                error = $"Unrecognized passive node token '{token}'.";
                return false;
            }

            if (seen.Add(nodeId))
            {
                nodes.Add(nodeId);
            }

            if (colon >= 0)
            {
                string effPart = token[(colon + 1)..];
                if (!ushort.TryParse(effPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort effectId))
                {
                    error = $"Invalid mastery effect '{effPart}' for node {nodeId}.";
                    return false;
                }

                masteries[nodeId] = effectId;
            }
        }

        if (nodes.Count == 0)
        {
            error = "No passive nodes found.";
            return false;
        }

        plan = new QolPobPlan(nodes, masteries, autoApplyOnLevelUp);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IDisposable sub in _subscriptions)
        {
            sub.Dispose();
        }

        _subscriptions.Clear();
        _pobPlans.Clear();
    }

    private static bool TryParsePobXml(string xmlText, bool autoApplyOnLevelUp, out QolPobPlan? plan, out string error)
    {
        plan = null;
        error = string.Empty;
        try
        {
            XDocument doc = XDocument.Parse(xmlText);
            XElement? spec = doc.Descendants("Spec").FirstOrDefault();
            if (spec is null)
            {
                error = "XML does not contain a <Spec> element.";
                return false;
            }

            string nodesAttr = spec.Attribute("nodes")?.Value ?? string.Empty;
            List<ushort> nodes = [];
            HashSet<ushort> seen = [];
            foreach (string token in nodesAttr.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ushort.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort id) && id > 0 && seen.Add(id))
                {
                    nodes.Add(id);
                }
            }

            if (nodes.Count == 0)
            {
                error = "<Spec nodes=\"...\"> contains no valid passive node IDs.";
                return false;
            }

            Dictionary<ushort, ushort> masteries = [];
            string masteryAttr = spec.Attribute("masteryEffects")?.Value ?? string.Empty;
            foreach (Match m in Regex.Matches(masteryAttr, @"\{(\d+),(\d+)\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
            {
                if (ushort.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort nodeId)
                    && ushort.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort effectId))
                {
                    masteries[nodeId] = effectId;
                }
            }

            plan = new QolPobPlan(nodes, masteries, autoApplyOnLevelUp);
            return true;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException or RegexMatchTimeoutException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? TryDecodeZlibBase64Xml(string encoded)
    {
        try
        {
            string b64 = encoded.Replace('-', '+').Replace('_', '/');
            int pad = b64.Length % 4;
            if (pad != 0)
            {
                b64 += new string('=', 4 - pad);
            }

            byte[] compressed = Convert.FromBase64String(b64);
            if (compressed.Length < 4 || compressed.Length > 2 * 1024 * 1024)
            {
                return null;
            }

            using MemoryStream input = new(compressed);
            using ZLibStream zlib = new(input, CompressionMode.Decompress);
            using MemoryStream output = new();
            byte[] buffer = new byte[4096];
            int total = 0;
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > 4 * 1024 * 1024)
                {
                    return null;
                }

                output.Write(buffer, 0, read);
            }

            string text = Encoding.UTF8.GetString(output.ToArray()).Trim();
            return text.StartsWith('<') ? text : null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static int FindNextFrontierIndex(
        List<ushort> remaining,
        ushort classStart,
        HashSet<ushort> allocated,
        IReadOnlyDictionary<ushort, IReadOnlyList<ushort>> adjacency)
    {
        for (int i = 0; i < remaining.Count; i++)
        {
            ushort candidate = remaining[i];
            if (candidate == classStart)
            {
                return i;
            }

            if (adjacency.TryGetValue(classStart, out IReadOnlyList<ushort>? startAdj) && startAdj.Contains(candidate))
            {
                return i;
            }

            foreach (ushort alloc in allocated)
            {
                if (adjacency.TryGetValue(alloc, out IReadOnlyList<ushort>? adj) && adj.Contains(candidate))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool IsValidDepositFallback(IReadOnlyList<QolStashTab> tabs, int tabIndex)
    {
        QolStashTab? tab = tabs.FirstOrDefault(t => t.TabIndex == tabIndex);
        return tab is not null && !tab.IsHidden && !tab.IsPublic && !tab.IsSubTab && tab.Kind != QolStashTabKind.Folder;
    }

    private static bool FitsAt(bool[,] occupied, int x, int y, int width, int height)
    {
        for (int cx = x; cx < x + width; cx++)
        {
            for (int cy = y; cy < y + height; cy++)
            {
                if (occupied[cx, cy])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void Mark(bool[,] occupied, int x, int y, int width, int height)
    {
        for (int cx = x; cx < x + width; cx++)
        {
            for (int cy = y; cy < y + height; cy++)
            {
                occupied[cx, cy] = true;
            }
        }
    }

    private static bool MatchesItem(Regex regex, QolItemRecord item, bool matchFullCard)
    {
        if (matchFullCard)
        {
            string card = string.Join('\n', new[] { item.BaseName }.Concat(item.ModLines));
            return regex.IsMatch(card);
        }

        foreach (string line in item.ModLines)
        {
            if (regex.IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    private static Task SendToastAsync(
        IClientSession session,
        string feature,
        string title,
        string message,
        string severity,
        int affectedCount = 0,
        bool requiresConfirmation = false,
        int? pendingTabIndex = null,
        CancellationToken cancellationToken = default) =>
        session.SendAsync(
            StashQolProtocol.ChannelName,
            new StashQolToastNotification(feature, title, message, severity, affectedCount, requiresConfirmation, pendingTabIndex),
            messageType: null,
            cancellationToken: cancellationToken);
}
