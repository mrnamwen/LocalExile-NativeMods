using System.Globalization;
using System.Text.Json;
using DualMod.IdolsAndGearVault.Shared;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;

namespace DualMod.IdolsAndGearVault.Client;

/// <summary>
/// Phase 4 flagship showcase client mod targeting NativeMods SDK <c>1.1.0</c>:
/// <list type="bullet">
///   <item><b>Wishlist #13:</b> 32-cell 6×6 Idol Board (<see cref="IUiItemGrid"/> with 4 locked corners) + Alt-held stock item tooltip affix-tag augmentation (<see cref="ITooltipService.RegisterItemTooltipAugmenter"/>).</item>
///   <item><b>Wishlist #14:</b> Custom Gear-Affinity Stash Tab (<see cref="ITabContainer"/>, <see cref="IComboBox"/>, <see cref="IScrollableFrame"/>, <see cref="IListBox"/>, <see cref="IStashAffinityService.RegisterRoute"/> &amp; <see cref="IStashAffinityService.RegisterStockAffinityRule"/>).</item>
///   <item><b>Live Native Extend:</b> Custom GEAL builtin (<c>IdolBoardCount</c>), native ruleset on free slot <c>0xAE</c> (<see cref="IRulesetRegistry.FirstFreeNativeRulesetId"/>), and custom <c>.ui</c> control kind (<c>"IdolVault.StatusBadge"</c>).</item>
/// </list>
/// </summary>
public sealed class IdolsVaultClientMod : IClientMod
{
    private const string EmbeddedFallbackUi = """
        // Embedded fallback for in-process test hosts without loose overlays mounted.

        <DEFINITIONS>

        begin Frame
        	id = idolsvault_panel;
        	size = 720, 640;
        	visible = true;

        	begin Label
        		id = title_label;
        		location = 44, 0;
        		size = 632, 42;
        		text = "Idols Board & Gear Vault";
        	end

        	begin BasicButton
        		id = close_button;
        		location = 672, 2;
        		size = 40, 40;
        	end

        	begin Label
        		id = board_summary_label;
        		location = 12, 48;
        		size = 696, 20;
        		text = "Idol Board: 0 / 32 active cells (4 corners locked)";
        	end

        	begin Label
        		id = vault_summary_label;
        		location = 12, 70;
        		size = 696, 18;
        		text = "Gear Vault: Helmets -> Tab 4 (Stock Affinity: On)";
        	end

        	begin Label
        		id = extend_summary_label;
        		location = 12, 90;
        		size = 696, 18;
        		text = "Extend: GEAL IdolBoardCount | Ruleset 0xAE | Control IdolVault.StatusBadge";
        	end

        	begin Frame
        		id = tabs_host;
        		location = 20, 118;
        		size = 680, 410;
        	end

        	begin BasicButton
        		id = sync_state_button;
        		location = 20, 540;
        		size = 216, 40;
        	end

        	begin BasicButton
        		id = apply_vault_button;
        		location = 252, 540;
        		size = 216, 40;
        	end

        	begin BasicButton
        		id = close_footer_button;
        		location = 484, 540;
        		size = 216, 40;
        	end

        	begin Label
        		id = status_label;
        		location = 20, 592;
        		size = 680, 28;
        		text = "Ready.";
        	end
        end
        """;

    private readonly List<IDisposable> _registrations = [];
    private readonly List<IDisposable> _panelRowTooltipBindings = [];
    private readonly Dictionary<string, PlacedIdolRecord> _placedIdols = new(StringComparer.Ordinal);
    private readonly List<GearVaultItemRecord> _vaultItems = [];

    private IClientContext? _context;
    private IUiWindow? _panelWindow;
    private ITabContainer? _tabContainer;
    private IUiItemGrid? _idolGrid;
    private IComboBox? _idolPresetCombo;
    private ILabel? _boardOccupancyLabel;
    private IComboBox? _gearClassCombo;
    private IComboBox? _vaultTabCombo;
    private ICheckBox? _stockAffinityCheckBox;
    private IScrollableFrame? _vaultScrollFrame;
    private IListBox? _vaultListBox;
    private ILabel? _vaultSelectedLabel;

    private IdolBoardClientRuleset? _activeRuleset;
    private IdolVaultStatusBadgeControl? _lastBadgeControl;
    private bool _unloading;
    private int _nextClientIdolSequence = 1;

    private ushort _targetGearVaultTabIndex = IdolsVaultProtocol.DefaultGearVaultTabIndex;
    private string _gearClassFilter = IdolsVaultProtocol.DefaultGearClassFilter;
    private bool _stockAffinityEnabled = true;
    private byte _stockAffinityBitId = IdolsVaultProtocol.DefaultStockAffinityBitId;
    private bool _showAltTooltipAffixTags = true;
    private int _selectedPresetIndex;
    private string? _selectedIdolId;
    private int _lastRulesetIdolCount;
    private int _lastRulesetOccupiedCells;
    private IdolsVaultToastNotification? _lastToast;

    public IUiWindow? PanelWindow => _panelWindow;
    public ITabContainer? TabContainer => _tabContainer;
    public IUiItemGrid? IdolGrid => _idolGrid;
    public IComboBox? IdolPresetCombo => _idolPresetCombo;
    public IComboBox? GearClassCombo => _gearClassCombo;
    public IComboBox? VaultTabCombo => _vaultTabCombo;
    public IScrollableFrame? VaultScrollFrame => _vaultScrollFrame;
    public IListBox? VaultListBox => _vaultListBox;
    public IdolBoardClientRuleset? ActiveRuleset => _activeRuleset;
    public IdolVaultStatusBadgeControl? LastBadgeControl => _lastBadgeControl;

    public ushort TargetGearVaultTabIndex => _targetGearVaultTabIndex;
    public string GearClassFilter => _gearClassFilter;
    public bool StockAffinityEnabled => _stockAffinityEnabled;
    public byte StockAffinityBitId => _stockAffinityBitId;
    public bool ShowAltTooltipAffixTags => _showAltTooltipAffixTags;
    public int PlacedIdolCount => _placedIdols.Count;
    public int OccupiedCellCount => IdolBoardGeometry.CountOccupiedCells(_placedIdols.Values);
    public IReadOnlyCollection<PlacedIdolRecord> PlacedIdols => [.. _placedIdols.Values];
    public IReadOnlyList<GearVaultItemRecord> VaultItems => [.. _vaultItems];
    public int LastRulesetIdolCount => _lastRulesetIdolCount;
    public int LastRulesetOccupiedCells => _lastRulesetOccupiedCells;
    public IdolsVaultToastNotification? LastToast => _lastToast;

    public void Load(IClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _unloading = false;
        _placedIdols.Clear();
        _vaultItems.Clear();
        _selectedIdolId = null;
        _selectedPresetIndex = 0;

        IGameServices game = context.Game;
        LoadInitialSettings(context.ModDirectory);
        EnsureVirtualUiFallback(context, game);

        // 1. Register conflict-free toolbar buttons on HUD and Stash header.
        Track(context, game.Screens.RegisterSlotButton(
            ScreenSlot.HudTopRight,
            IdolsVaultProtocol.SlotOpenPanelId,
            "Idols & Vault",
            TogglePanel,
            new SlotAttachmentOptions(Order: 60, Width: 102f, Height: 26f, Tooltip: "Open the 32-Cell Idol Board & Gear Vault panel (Alt+I).")));

        Track(context, game.Screens.RegisterSlotButton(
            ScreenSlot.StashHeader,
            IdolsVaultProtocol.SlotStashVaultId,
            "Gear Vault",
            () => OpenPanel(initialTabIndex: 1),
            new SlotAttachmentOptions(Order: 40, Width: 92f, Height: 24f, Tooltip: "Configure custom Gear-Affinity stash routing.")));

        // 2. Centralized keybind (Alt+I) respecting !IsTextInputFocused.
        Track(context, game.Input.Keybinds.Register(
            IdolsVaultProtocol.TogglePanelKeybindId,
            "Toggle Idols Board & Gear Vault",
            KeyChord.FromKey('I', KeyModifiers.Alt),
            OnTogglePanelKeybind,
            allowWhenTextInputFocused: false,
            suppressGameInput: true));

        // 3. Wishlist #13: Stock item tooltip augmenter injecting Alt-held affix tags & tier summary.
        Track(context, game.Tooltips.RegisterItemTooltipAugmenter(BuildAltTooltipAugmentation, priority: 10));

        // 4. Wishlist #14: Custom Gear-Affinity Ctrl+Click router (C->S 0x00E1) + optional stock affinity bit rule.
        Track(context, game.StashAffinity.RegisterRoute(
            IdolsVaultProtocol.GearVaultRouteId,
            EvaluateGearVaultRoute,
            priority: -5));

        Track(context, game.StashAffinity.RegisterStockAffinityRule(
            IdolsVaultProtocol.GearStockAffinityRuleId,
            EvaluateStockAffinityRule,
            priority: 0));

        // 5. Live Native Extend Showcase: GEAL builtin, native ruleset on free slot 0xAE, custom .ui control kind.
        Track(context, game.GealBuiltins.Register(
            IdolsVaultProtocol.GealBuiltinIdolBoardCount,
            HandleGealIdolBoardCount));

        Track(context, game.Rulesets.RegisterNative(
            IRulesetRegistry.FirstFreeNativeRulesetId,
            CreateBoardRuleset));

        Track(context, game.UiControls.Register(
            IdolsVaultProtocol.CustomControlStatusBadge,
            CreateStatusBadgeControl));

        // 6. Server bridge typed RPC handlers.
        Track(context, context.Net.On<IdolBoardStateSync>(
            sync => DispatchOnGameThread(() => HandleIdolBoardSync(sync))));

        Track(context, context.Net.On<GearVaultStateSync>(
            sync => DispatchOnGameThread(() => HandleGearVaultSync(sync))));

        Track(context, context.Net.On<IdolsVaultToastNotification>(
            toast => DispatchOnGameThread(() => HandleServerToast(toast))));

        game.Lifecycle.Disconnected += OnDisconnected;

        context.Logger.Info("DualMod.IdolsAndGearVault client initialized (SDK 1.1.0).");
    }

    public void Unload()
    {
        _unloading = true;
        if (_context is { } ctx)
        {
            ctx.Game.Lifecycle.Disconnected -= OnDisconnected;
            try
            {
                ctx.Game.Tooltips.HideTooltip();
            }
            catch
            {
                // Ignore bridge teardown errors during unload.
            }
        }

        ClosePanel();

        for (int i = _registrations.Count - 1; i >= 0; i--)
        {
            try
            {
                _registrations[i].Dispose();
            }
            catch
            {
                // Ignore individual teardown errors during unload.
            }
        }

        _registrations.Clear();
        _placedIdols.Clear();
        _vaultItems.Clear();
        _activeRuleset = null;
        _lastBadgeControl = null;
        _selectedIdolId = null;
        _context = null;
    }

    public void TogglePanel()
    {
        if (_unloading || _context is null)
        {
            return;
        }

        DispatchOnGameThread(() =>
        {
            if (_unloading)
            {
                return;
            }

            if (_panelWindow is { IsOpen: true })
            {
                ClosePanel();
            }
            else
            {
                OpenPanel(initialTabIndex: 0);
            }
        });
    }

    public void OpenPanel(int initialTabIndex = 0)
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        DispatchOnGameThread(() =>
        {
            if (_unloading || _context is null)
            {
                return;
            }

            if (_panelWindow is { IsOpen: true })
            {
                _panelWindow.Visible = true;
                if (_tabContainer is not null && initialTabIndex >= 0 && initialTabIndex < _tabContainer.TabCount)
                {
                    _tabContainer.SelectTab(initialTabIndex);
                }

                RefreshAllPanelVisuals();
                return;
            }

            IUiWindow window = ctx.Game.Ui.Load(IdolsVaultProtocol.PanelUiPath, IdolsVaultProtocol.PanelWindowId);
            _panelWindow = window;
            window.Closed += () =>
            {
                if (ReferenceEquals(_panelWindow, window))
                {
                    DisposePanelRowTooltips();
                    _panelWindow = null;
                    _tabContainer = null;
                    _idolGrid = null;
                    _idolPresetCombo = null;
                    _boardOccupancyLabel = null;
                    _gearClassCombo = null;
                    _vaultTabCombo = null;
                    _stockAffinityCheckBox = null;
                    _vaultScrollFrame = null;
                    _vaultListBox = null;
                    _vaultSelectedLabel = null;
                }
            };

            if (window.Find<IButton>("close_button") is { } closeBtn)
            {
                closeBtn.Clicked += ClosePanel;
            }

            if (window.Find<IButton>("close_footer_button") is { } closeFooterBtn)
            {
                closeFooterBtn.Clicked += ClosePanel;
            }

            if (window.Find<IButton>("sync_state_button") is { } syncBtn)
            {
                syncBtn.Clicked += RequestFullSync;
            }

            if (window.Find<IButton>("apply_vault_button") is { } applyVaultBtn)
            {
                applyVaultBtn.Clicked += ApplyGearVaultConfigFromUi;
            }

            IUiControl tabsHost = window.Find("tabs_host") ?? window;
            BuildRichTabs(ctx.Game, tabsHost);

            if (_tabContainer is not null && initialTabIndex >= 0 && initialTabIndex < _tabContainer.TabCount)
            {
                _tabContainer.SelectTab(initialTabIndex);
            }

            RefreshAllPanelVisuals();
            RequestFullSync();
        });
    }

    public void ClosePanel()
    {
        DisposePanelRowTooltips();
        IUiItemGrid? grid = _idolGrid;
        _idolGrid = null;
        if (grid is not null)
        {
            try
            {
                grid.Dispose();
            }
            catch
            {
                // Ignore if parent window already destroyed the grid.
            }
        }

        IUiWindow? window = _panelWindow;
        _panelWindow = null;
        _tabContainer = null;
        _idolPresetCombo = null;
        _boardOccupancyLabel = null;
        _gearClassCombo = null;
        _vaultTabCombo = null;
        _stockAffinityCheckBox = null;
        _vaultScrollFrame = null;
        _vaultListBox = null;
        _vaultSelectedLabel = null;

        if (window is { IsOpen: true })
        {
            window.Close();
        }
    }

    public void RequestFullSync()
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        DispatchOnGameThread(() =>
        {
            if (_unloading || _context is null)
            {
                return;
            }

            ctx.Net.Send(new SyncIdolBoardRequest());
            ctx.Net.Send(new SyncGearVaultRequest(_gearClassFilter));
        });
    }

    public void RequestPlaceIdol(IdolPreset preset, int col, int row, string? explicitIdolId = null)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        DispatchOnGameThread(() =>
        {
            if (_unloading || _context is null)
            {
                return;
            }

            string idolId = string.IsNullOrWhiteSpace(explicitIdolId)
                ? $"{preset.PresetId}_{_nextClientIdolSequence++}"
                : explicitIdolId.Trim();

            if (!IdolBoardGeometry.CanPlace(col, row, preset.Width, preset.Height, _placedIdols.Values, out string reason, candidateIdolId: idolId))
            {
                SetPanelStatus(reason);
                ctx.Game.Toasts.Show("Idol Board", reason, ToastSeverity.Warning);
                return;
            }

            ctx.Net.Send(new PlaceIdolRequest(
                IdolId: idolId,
                Name: preset.Name,
                MetadataPath: preset.MetadataPath,
                Col: col,
                Row: row,
                Width: preset.Width,
                Height: preset.Height,
                Rarity: preset.Rarity,
                IconPath: preset.IconPath,
                AffixTags: preset.AffixTags,
                ModSummaryLines: preset.ModSummaryLines));
        });
    }

    public void RequestRemoveIdol(string idolId)
    {
        if (_unloading || _context is not { } ctx || string.IsNullOrWhiteSpace(idolId))
        {
            return;
        }

        DispatchOnGameThread(() =>
        {
            if (_unloading || _context is null)
            {
                return;
            }

            ctx.Net.Send(new RemoveIdolRequest(idolId.Trim()));
        });
    }

    public void ConfigureGearVault(ushort targetTabIndex, string gearClassFilter, bool stockAffinityEnabled, byte? stockAffinityBitId = null)
    {
        _targetGearVaultTabIndex = Math.Min(targetTabIndex, IdolsVaultProtocol.MaxGearVaultTabIndex);
        _gearClassFilter = GearVaultClasses.Normalize(gearClassFilter);
        _stockAffinityEnabled = stockAffinityEnabled;
        if (stockAffinityBitId is byte bit)
        {
            _stockAffinityBitId = Math.Min(bit, IdolsVaultProtocol.MaxStockAffinityBitId);
        }

        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        DispatchOnGameThread(() =>
        {
            if (_unloading || _context is null)
            {
                return;
            }

            ctx.Net.Send(new ConfigureGearVaultRequest(
                _targetGearVaultTabIndex,
                _gearClassFilter,
                _stockAffinityEnabled,
                _stockAffinityBitId));

            SyncGearVaultControlsFromState();
            UpdateSummaryLabels();
            SetPanelStatus($"Gear Vault routes {_gearClassFilter} -> Tab {_targetGearVaultTabIndex}.");
        });
    }

    /// <summary>
    /// Evaluates Wishlist #13 stock item tooltip augmentation.
    /// Injects structured <c>[Affix Tags: ...]</c> and tier summary sections when Alt is held.
    /// </summary>
    public IReadOnlyList<TooltipSection> BuildAltTooltipAugmentation(ItemTooltipContext context)
    {
        if (_unloading || !_showAltTooltipAffixTags || !context.IsAltHeld)
        {
            return [];
        }

        HoveredItemInfo item = context.Item;
        bool isIdol = IsIdolItem(item);
        string? gearClass = GearVaultClasses.Classify(item.MetadataPath, item.BaseName, item.ItemClassId);

        if (!isIdol && gearClass is null && item.Rarity == ItemRarity.Normal)
        {
            return [];
        }

        List<string> tags = BuildAffixTagsForItem(item, isIdol, gearClass);
        string tagLine = $"[Affix Tags: {string.Join(", ", tags)}]";
        string tierLine = BuildTierSummaryLine(item, isIdol, gearClass);

        return
        [
            new TooltipSection(
                Lines:
                [
                    new TooltipLine(tagLine, Color: "rgb(136, 204, 255)", Centered: false, RgbaColor: 0x88CCFFFFu),
                    new TooltipLine(tierLine, Color: "rgb(255, 224, 136)", Centered: false, RgbaColor: 0xFFE088FFu),
                ],
                PrependSeparator: true),
        ];
    }

    private StashAffinityRoute? EvaluateGearVaultRoute(StashAffinityItemContext context)
    {
        if (_unloading || context.StashKind != StashKind.Personal)
        {
            return null;
        }

        string? itemClass = GearVaultClasses.Classify(context.Item.MetadataPath, context.Item.BaseName, context.Item.ItemClassId);
        if (itemClass is null || !string.Equals(itemClass, _gearClassFilter, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new StashAffinityRoute(
            TabIndex: _targetGearVaultTabIndex,
            AffinityId: 0,
            ReplaceStockTargets: true);
    }

    private byte? EvaluateStockAffinityRule(StashAffinityItemContext context)
    {
        if (_unloading || !_stockAffinityEnabled)
        {
            return null;
        }

        string? itemClass = GearVaultClasses.Classify(context.Item.MetadataPath, context.Item.BaseName, context.Item.ItemClassId);
        if (itemClass is not null && string.Equals(itemClass, _gearClassFilter, StringComparison.OrdinalIgnoreCase))
        {
            return _stockAffinityBitId;
        }

        return null;
    }

    private GealValue HandleGealIdolBoardCount(uint objectId, GealArguments args)
    {
        _ = objectId;
        if (_unloading)
        {
            return GealValue.FromInt(0);
        }

        string mode = args.Count > 0 ? args.String(0) : "idols";
        return mode.ToLowerInvariant() switch
        {
            "cells" or "occupied" => GealValue.FromInt(OccupiedCellCount),
            "free" or "remaining" => GealValue.FromInt(IdolBoardGeometry.ActiveCellCount - OccupiedCellCount),
            "capacity" or "max" => GealValue.FromInt(IdolBoardGeometry.ActiveCellCount),
            _ => GealValue.FromInt(_placedIdols.Count),
        };
    }

    private IClientRuleset CreateBoardRuleset()
    {
        var ruleset = new IdolBoardClientRuleset((idolCount, occupiedCells) =>
        {
            _lastRulesetIdolCount = idolCount;
            _lastRulesetOccupiedCells = occupiedCells;
            UpdateSummaryLabels();
        });
        _activeRuleset = ruleset;
        return ruleset;
    }

    private ICustomUiControl CreateStatusBadgeControl()
    {
        var badge = new IdolVaultStatusBadgeControl();
        _lastBadgeControl = badge;
        return badge;
    }

    private void BuildRichTabs(IGameServices game, IUiControl tabsHost)
    {
        ITabContainer tabs = game.Ui.CreateTabContainer(tabsHost, "idolsvault_tabs");
        tabs.SetPosition(8f, 8f);
        tabs.SetSize(664f, 394f);
        _tabContainer = tabs;

        // ── Tab 0: 32-Cell Idol Board (6x6 IUiItemGrid with 4 locked corner cells) ──
        IUiControl idolTab = tabs.AddTab("Idol Board (32 Cells)");
        BuildIdolBoardTab(game, idolTab);

        // ── Tab 1: Custom Gear-Affinity Vault (IComboBox + IScrollableFrame + IListBox) ──
        IUiControl vaultTab = tabs.AddTab("Gear Vault Affinity");
        BuildGearVaultTab(game, vaultTab);

        tabs.ActiveTabChanged += activeIdx =>
        {
            if (_unloading)
            {
                return;
            }

            SetPanelStatus(activeIdx == 0
                ? "Idol Board active: left-click an active cell to place, right-click to remove."
                : $"Gear Vault active: Ctrl+Clicking {_gearClassFilter} routes to Tab {_targetGearVaultTabIndex}.");
        };
    }

    private void BuildIdolBoardTab(IGameServices game, IUiControl idolTab)
    {
        IUiItemGrid grid = game.Ui.CreateItemGrid(
            idolTab,
            new UiItemGridOptions(
                Columns: IdolBoardGeometry.Columns,
                Rows: IdolBoardGeometry.Rows,
                CellSize: 48f,
                CellPadding: 2f,
                CellBackgroundTexture: "Art/2DArt/UIImages/InGame/CellBg.dds",
                ShowTooltipsOnHover: true),
            id: "idol_board_grid");
        grid.SetPosition(14f, 24f);
        _idolGrid = grid;

        // Visually & interactively lock the 4 corner cells: (0,0), (5,0), (0,5), (5,5) -> exact 32 active cells!
        (int Col, int Row)[] corners = [(0, 0), (5, 0), (0, 5), (5, 5)];
        foreach ((int col, int row) in corners)
        {
            if (grid.Find($"cell_{col}_{row}") is { } cornerFrame)
            {
                cornerFrame.SetProperty("blocked", "true");
                cornerFrame.SetProperty("corner_locked", "true");
                if (cornerFrame.Find<IButton>($"cell_btn_{col}_{row}") is { } cornerBtn)
                {
                    cornerBtn.Enabled = false;
                    cornerBtn.IsEnabled = false;
                }

                ILabel lockMark = game.Ui.CreateLabel(cornerFrame, "X", $"corner_lock_{col}_{row}");
                lockMark.SetPosition(0f, 0f);
                lockMark.SetSize(48f, 48f);
                lockMark.ForegroundColor = "rgb(140, 72, 72)";
            }
        }

        grid.CellClicked += OnIdolGridCellClicked;
        grid.CellRightClicked += OnIdolGridCellRemoveRequested;
        grid.CellCtrlClicked += OnIdolGridCellRemoveRequested;

        // Right-hand Idol controls sidebar.
        IUiControl sidePanel = game.Ui.CreateFrame(idolTab, "idol_controls_frame");
        sidePanel.SetPosition(332f, 24f);
        sidePanel.SetSize(316f, 300f);

        ILabel presetCaption = game.Ui.CreateLabel(sidePanel, "Select Idol Footprint Preset:", "idol_preset_caption");
        presetCaption.SetPosition(8f, 4f);
        presetCaption.SetSize(300f, 22f);

        IComboBox presetCombo = game.Ui.CreateComboBox(sidePanel, "idol_preset_combo");
        presetCombo.SetPosition(8f, 30f);
        presetCombo.SetSize(300f, 32f);
        for (int i = 0; i < IdolPresetCatalog.Presets.Count; i++)
        {
            IdolPreset p = IdolPresetCatalog.Presets[i];
            presetCombo.AddItem($"{p.Name} ({p.Width}x{p.Height})", enabled: true, tag: (uint)i);
        }

        presetCombo.SelectedIndex = Math.Clamp(_selectedPresetIndex, 0, IdolPresetCatalog.Presets.Count - 1);
        presetCombo.SelectionChanged += idx =>
        {
            if (idx >= 0 && idx < IdolPresetCatalog.Presets.Count)
            {
                _selectedPresetIndex = idx;
            }
        };
        _idolPresetCombo = presetCombo;

        IButton autoPlaceBtn = game.Ui.CreateButton(sidePanel, "Place Preset In First Free Cell", "place_auto_button");
        autoPlaceBtn.SetPosition(8f, 74f);
        autoPlaceBtn.SetSize(300f, 36f);
        autoPlaceBtn.Clicked += () =>
        {
            IdolPreset preset = IdolPresetCatalog.Presets[Math.Clamp(_selectedPresetIndex, 0, IdolPresetCatalog.Presets.Count - 1)];
            (int Col, int Row)? free = IdolBoardGeometry.FindFirstFreeCell(preset.Width, preset.Height, _placedIdols.Values);
            if (free is null)
            {
                SetPanelStatus($"No free space on the 32-cell board for {preset.Width}x{preset.Height} {preset.Name}.");
                return;
            }

            RequestPlaceIdol(preset, free.Value.Col, free.Value.Row);
        };

        IButton removeSelectedBtn = game.Ui.CreateButton(sidePanel, "Remove Selected Idol", "remove_selected_button");
        removeSelectedBtn.SetPosition(8f, 120f);
        removeSelectedBtn.SetSize(300f, 36f);
        removeSelectedBtn.Clicked += () =>
        {
            if (string.IsNullOrWhiteSpace(_selectedIdolId))
            {
                SetPanelStatus("Select or right-click a placed Idol to remove it.");
                return;
            }

            RequestRemoveIdol(_selectedIdolId);
        };

        ILabel occupancyLabel = game.Ui.CreateLabel(
            sidePanel,
            "Occupancy: 0 / 32 active cells (4 corners locked)",
            "board_occupancy_label");
        occupancyLabel.SetPosition(8f, 172f);
        occupancyLabel.SetSize(300f, 44f);
        _boardOccupancyLabel = occupancyLabel;

        ILabel hintLabel = game.Ui.CreateLabel(
            sidePanel,
            "Hover any Idol for anchored tooltip. Hold Alt on stock items for affix tags.",
            "board_hint_label");
        hintLabel.SetPosition(8f, 224f);
        hintLabel.SetSize(300f, 44f);
    }

    private void BuildGearVaultTab(IGameServices game, IUiControl vaultTab)
    {
        ILabel classCaption = game.Ui.CreateLabel(vaultTab, "Gear Class Filter:", "gear_class_caption");
        classCaption.SetPosition(14f, 16f);
        classCaption.SetSize(200f, 22f);

        IComboBox classCombo = game.Ui.CreateComboBox(vaultTab, "gear_class_combo");
        classCombo.SetPosition(14f, 40f);
        classCombo.SetSize(260f, 32f);
        int initialClassIdx = 0;
        for (int i = 0; i < GearVaultClasses.All.Count; i++)
        {
            string cls = GearVaultClasses.All[i];
            classCombo.AddItem(cls, enabled: true, tag: (uint)i);
            if (string.Equals(cls, _gearClassFilter, StringComparison.OrdinalIgnoreCase))
            {
                initialClassIdx = i;
            }
        }

        classCombo.SelectedIndex = initialClassIdx;
        classCombo.SelectionChanged += idx =>
        {
            if (_unloading || idx < 0 || idx >= GearVaultClasses.All.Count)
            {
                return;
            }

            _gearClassFilter = GearVaultClasses.All[idx];
            UpdateSummaryLabels();
            _context?.Net.Send(new ConfigureGearVaultRequest(
                _targetGearVaultTabIndex,
                _gearClassFilter,
                _stockAffinityEnabled,
                _stockAffinityBitId));
        };
        _gearClassCombo = classCombo;

        ILabel tabCaption = game.Ui.CreateLabel(vaultTab, "Target Gear Vault Tab (C->S 0x00E1):", "vault_tab_caption");
        tabCaption.SetPosition(296f, 16f);
        tabCaption.SetSize(280f, 22f);

        IComboBox tabCombo = game.Ui.CreateComboBox(vaultTab, "vault_tab_combo");
        tabCombo.SetPosition(296f, 40f);
        tabCombo.SetSize(220f, 32f);
        for (int i = 0; i < IdolsVaultProtocol.MaxTabChoicesInCombo; i++)
        {
            tabCombo.AddItem($"Stash Tab {i}", enabled: true, tag: (uint)i);
        }

        tabCombo.SelectedIndex = Math.Clamp((int)_targetGearVaultTabIndex, 0, IdolsVaultProtocol.MaxTabChoicesInCombo - 1);
        tabCombo.SelectionChanged += idx =>
        {
            if (_unloading || idx < 0)
            {
                return;
            }

            _targetGearVaultTabIndex = (ushort)Math.Clamp(idx, 0, IdolsVaultProtocol.MaxGearVaultTabIndex);
            UpdateSummaryLabels();
            _context?.Net.Send(new ConfigureGearVaultRequest(
                _targetGearVaultTabIndex,
                _gearClassFilter,
                _stockAffinityEnabled,
                _stockAffinityBitId));
        };
        _vaultTabCombo = tabCombo;

        ICheckBox stockAffinityBox = (ICheckBox)game.Ui.CreateControl(vaultTab, UiControlKind.CheckBox, "stock_affinity_checkbox");
        stockAffinityBox.SetPosition(14f, 82f);
        stockAffinityBox.SetSize(420f, 28f);
        stockAffinityBox.Text = $"Also map stock StashTabAffinityId bit {_stockAffinityBitId}";
        stockAffinityBox.IsChecked = _stockAffinityEnabled;
        stockAffinityBox.CheckedChanged += isChecked =>
        {
            if (_unloading)
            {
                return;
            }

            _stockAffinityEnabled = isChecked;
            UpdateSummaryLabels();
            _context?.Net.Send(new ConfigureGearVaultRequest(
                _targetGearVaultTabIndex,
                _gearClassFilter,
                _stockAffinityEnabled,
                _stockAffinityBitId));
        };
        _stockAffinityCheckBox = stockAffinityBox;

        // Scrollable viewport + selectable ListBox displaying items stored in the Gear Vault.
        IScrollableFrame scrollFrame = game.Ui.CreateScrollableFrame(vaultTab, "vault_scroll_frame");
        scrollFrame.SetPosition(14f, 118f);
        scrollFrame.SetSize(636f, 212f);
        _vaultScrollFrame = scrollFrame;

        IListBox listBox = game.Ui.CreateListBox(scrollFrame.ContentContainer, "vault_items_listbox");
        listBox.SetPosition(0f, 0f);
        listBox.SetSize(616f, 204f);
        listBox.SelectionChanged += OnVaultListSelectionChanged;
        _vaultListBox = listBox;

        ILabel selectedLabel = game.Ui.CreateLabel(vaultTab, "Selected Vault Item: None", "vault_selected_label");
        selectedLabel.SetPosition(14f, 338f);
        selectedLabel.SetSize(636f, 24f);
        _vaultSelectedLabel = selectedLabel;
    }

    private void OnIdolGridCellClicked(UiGridCellEventArgs args)
    {
        if (_unloading || _context is null)
        {
            return;
        }

        if (IdolBoardGeometry.IsCornerBlocked(args.Col, args.Row))
        {
            SetPanelStatus($"Cell ({args.Col}, {args.Row}) is locked — 4 corners are disabled on the 32-cell Idol Board.");
            return;
        }

        if (args.Item is not null)
        {
            _selectedIdolId = args.Item.ItemId;
            SetPanelStatus($"Selected Idol '{args.Item.Title ?? args.Item.ItemId}' ({args.Item.Width}x{args.Item.Height}). Right-click to remove.");
            _idolGrid?.TriggerCellHover(args.Col, args.Row);
            return;
        }

        IdolPreset preset = IdolPresetCatalog.Presets[Math.Clamp(_selectedPresetIndex, 0, IdolPresetCatalog.Presets.Count - 1)];
        RequestPlaceIdol(preset, args.Col, args.Row);
    }

    private void OnIdolGridCellRemoveRequested(UiGridCellEventArgs args)
    {
        if (_unloading || args.Item is null)
        {
            return;
        }

        RequestRemoveIdol(args.Item.ItemId);
    }

    private void OnVaultListSelectionChanged(int selectedIndex)
    {
        if (_unloading || _vaultSelectedLabel is null)
        {
            return;
        }

        if (selectedIndex < 0 || selectedIndex >= _vaultItems.Count)
        {
            _vaultSelectedLabel.Text = "Selected Vault Item: None";
            return;
        }

        GearVaultItemRecord item = _vaultItems[selectedIndex];
        string tags = item.AffixTags.Length > 0 ? string.Join(", ", item.AffixTags) : item.GearClass;
        _vaultSelectedLabel.Text = $"Selected: {item.Name} ({item.BaseName}, iLvl {item.ItemLevel}) [{tags}]";
    }

    private void ApplyGearVaultConfigFromUi()
    {
        if (_unloading)
        {
            return;
        }

        string selectedClass = _gearClassCombo is { SelectedIndex: >= 0 } && _gearClassCombo.SelectedIndex < GearVaultClasses.All.Count
            ? GearVaultClasses.All[_gearClassCombo.SelectedIndex]
            : _gearClassFilter;
        ushort selectedTab = _vaultTabCombo is { SelectedIndex: >= 0 }
            ? (ushort)Math.Clamp(_vaultTabCombo.SelectedIndex, 0, IdolsVaultProtocol.MaxGearVaultTabIndex)
            : _targetGearVaultTabIndex;
        bool stockEnabled = _stockAffinityCheckBox?.IsChecked ?? _stockAffinityEnabled;

        ConfigureGearVault(selectedTab, selectedClass, stockEnabled, _stockAffinityBitId);
    }

    private void HandleIdolBoardSync(IdolBoardStateSync sync)
    {
        if (_unloading)
        {
            return;
        }

        _placedIdols.Clear();
        foreach (PlacedIdolRecord idol in sync.Idols ?? [])
        {
            _placedIdols[idol.IdolId] = idol;
        }

        if (_selectedIdolId is not null && !_placedIdols.ContainsKey(_selectedIdolId))
        {
            _selectedIdolId = null;
        }

        SyncIdolGridVisuals();
        UpdateSummaryLabels();
        if (!string.IsNullOrWhiteSpace(sync.StatusMessage))
        {
            SetPanelStatus(sync.StatusMessage);
        }
    }

    private void HandleGearVaultSync(GearVaultStateSync sync)
    {
        if (_unloading)
        {
            return;
        }

        _targetGearVaultTabIndex = Math.Min(sync.TargetTabIndex, IdolsVaultProtocol.MaxGearVaultTabIndex);
        _gearClassFilter = GearVaultClasses.Normalize(sync.GearClassFilter);
        _stockAffinityEnabled = sync.StockAffinityEnabled;
        _stockAffinityBitId = Math.Min(sync.StockAffinityBitId, IdolsVaultProtocol.MaxStockAffinityBitId);

        _vaultItems.Clear();
        if (sync.Items is { Length: > 0 })
        {
            _vaultItems.AddRange(sync.Items);
        }

        SyncGearVaultControlsFromState();
        SyncVaultListVisuals();
        UpdateSummaryLabels();
        if (!string.IsNullOrWhiteSpace(sync.StatusMessage))
        {
            SetPanelStatus(sync.StatusMessage);
        }
    }

    private void HandleServerToast(IdolsVaultToastNotification toast)
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        _lastToast = toast;
        ToastSeverity severity = toast.Severity.ToUpperInvariant() switch
        {
            "SUCCESS" => ToastSeverity.Success,
            "WARNING" => ToastSeverity.Warning,
            "ERROR" => ToastSeverity.Error,
            _ => ToastSeverity.Info,
        };

        ctx.Game.Toasts.Show(toast.Title, toast.Message, severity);
        SetPanelStatus(toast.Message);
    }

    private void RefreshAllPanelVisuals()
    {
        SyncIdolGridVisuals();
        SyncGearVaultControlsFromState();
        SyncVaultListVisuals();
        UpdateSummaryLabels();
    }

    private void SyncIdolGridVisuals()
    {
        if (_idolGrid is not { } grid)
        {
            return;
        }

        grid.Clear();
        foreach (PlacedIdolRecord idol in _placedIdols.Values.OrderBy(i => i.Row).ThenBy(i => i.Col))
        {
            List<TooltipLine> lines =
            [
                new TooltipLine($"Footprint: {idol.Width}x{idol.Height} at ({idol.Col}, {idol.Row})", Color: "rgb(185, 195, 210)"),
            ];

            if (idol.AffixTags.Length > 0)
            {
                lines.Add(new TooltipLine($"[Affix Tags: {string.Join(", ", idol.AffixTags)}]", Color: "rgb(136, 204, 255)", RgbaColor: 0x88CCFFFFu));
            }

            foreach (string modLine in idol.ModSummaryLines)
            {
                lines.Add(new TooltipLine(modLine, Color: "rgb(160, 224, 168)"));
            }

            grid.PlaceItem(new UiGridItem(
                ItemId: idol.IdolId,
                Col: idol.Col,
                Row: idol.Row,
                Width: idol.Width,
                Height: idol.Height,
                IconPath: idol.IconPath,
                StackSize: 1,
                Rarity: Enum.IsDefined((ItemRarity)idol.Rarity) ? (ItemRarity)idol.Rarity : ItemRarity.Rare,
                Title: idol.Name,
                TooltipLines: lines,
                Tag: idol));
        }

        int occupied = OccupiedCellCount;
        if (_boardOccupancyLabel is not null)
        {
            _boardOccupancyLabel.Text = $"Occupancy: {occupied} / {IdolBoardGeometry.ActiveCellCount} active cells ({_placedIdols.Count} Idol(s), 4 corners locked)";
        }
    }

    private void SyncGearVaultControlsFromState()
    {
        if (_gearClassCombo is not null)
        {
            int idx = -1;
            for (int i = 0; i < GearVaultClasses.All.Count; i++)
            {
                if (string.Equals(GearVaultClasses.All[i], _gearClassFilter, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }

            if (idx >= 0 && _gearClassCombo.SelectedIndex != idx)
            {
                _gearClassCombo.SelectedIndex = idx;
            }
        }

        if (_vaultTabCombo is not null &&
            _targetGearVaultTabIndex < _vaultTabCombo.ItemCount &&
            _vaultTabCombo.SelectedIndex != _targetGearVaultTabIndex)
        {
            _vaultTabCombo.SelectedIndex = _targetGearVaultTabIndex;
        }

        if (_stockAffinityCheckBox is not null)
        {
            _stockAffinityCheckBox.Text = $"Also map stock StashTabAffinityId bit {_stockAffinityBitId}";
            if (_stockAffinityCheckBox.IsChecked != _stockAffinityEnabled)
            {
                _stockAffinityCheckBox.IsChecked = _stockAffinityEnabled;
            }
        }
    }

    private void SyncVaultListVisuals()
    {
        if (_vaultListBox is null || _vaultScrollFrame is null || _context is not { } ctx)
        {
            return;
        }

        DisposePanelRowTooltips();
        _vaultListBox.ClearItems();

        const float rowHeight = 26f;
        for (int i = 0; i < _vaultItems.Count; i++)
        {
            GearVaultItemRecord item = _vaultItems[i];
            ILabel rowLabel = ctx.Game.Ui.CreateLabel(
                _vaultScrollFrame.ContentContainer,
                $"{item.Name} — {item.BaseName} (iLvl {item.ItemLevel}, Tab {item.TabIndex})",
                $"vault_row_{item.ItemId}_{i}");
            rowLabel.SetPosition(4f, i * rowHeight);
            rowLabel.SetSize(600f, rowHeight);

            GearVaultItemRecord captured = item;
            IDisposable tipSub = ctx.Game.Tooltips.AttachTooltip(rowLabel, () => TooltipContent.FromLines(
                captured.Name,
                [
                    new TooltipLine($"{captured.BaseName} | Class: {captured.GearClass} | iLvl {captured.ItemLevel}", "rgb(200, 210, 224)"),
                    new TooltipLine($"[Affix Tags: {string.Join(", ", captured.AffixTags)}]", "rgb(136, 204, 255)", RgbaColor: 0x88CCFFFFu),
                    new TooltipLine($"Routed to Stash Tab {captured.TabIndex} (C->S 0x00E1)", "rgb(168, 226, 176)"),
                ],
                Enum.IsDefined((ItemRarity)captured.Rarity) ? (ItemRarity)captured.Rarity : ItemRarity.Rare));
            _panelRowTooltipBindings.Add(tipSub);

            _vaultListBox.InsertItem(i, rowLabel, userData: item.ItemId);
        }

        float contentHeight = _vaultItems.Count * rowHeight;
        float maxScroll = Math.Max(0f, contentHeight - 204f);
        _vaultScrollFrame.SetMaxScrollOffset(maxScroll);
        _vaultScrollFrame.UpdateContentBounds();

        if (_vaultSelectedLabel is not null)
        {
            _vaultSelectedLabel.Text = _vaultItems.Count > 0
                ? $"Vault Items ({_gearClassFilter}): {_vaultItems.Count} item(s) in Tab {_targetGearVaultTabIndex}"
                : $"Vault Items ({_gearClassFilter}): Empty in Tab {_targetGearVaultTabIndex}";
        }
    }

    private void UpdateSummaryLabels()
    {
        if (_panelWindow is not { IsOpen: true } window)
        {
            return;
        }

        if (window.Find<ILabel>("board_summary_label") is { } boardSummary)
        {
            boardSummary.Text = $"Idol Board: {OccupiedCellCount} / {IdolBoardGeometry.ActiveCellCount} active cells ({_placedIdols.Count} Idol(s), 4 corners locked)";
        }

        if (window.Find<ILabel>("vault_summary_label") is { } vaultSummary)
        {
            string stockLabel = _stockAffinityEnabled ? $"On (Bit {_stockAffinityBitId})" : "Off";
            vaultSummary.Text = $"Gear Vault: {_gearClassFilter} -> Tab {_targetGearVaultTabIndex} (Stock Affinity: {stockLabel})";
        }

        if (window.Find<ILabel>("extend_summary_label") is { } extendSummary)
        {
            extendSummary.Text = $"Extend: GEAL {IdolsVaultProtocol.GealBuiltinIdolBoardCount}={_placedIdols.Count} | Ruleset 0x{IRulesetRegistry.FirstFreeNativeRulesetId:X2} (sync={_lastRulesetIdolCount}/{_lastRulesetOccupiedCells}) | {IdolsVaultProtocol.CustomControlStatusBadge}";
        }
    }

    private void SetPanelStatus(string message)
    {
        if (_panelWindow is { IsOpen: true } window && window.Find<ILabel>("status_label") is { } statusLabel)
        {
            statusLabel.Text = message;
        }
    }

    private void OnTogglePanelKeybind()
    {
        if (_unloading || _context is not { } ctx || ctx.Game.Input.IsTextInputFocused)
        {
            return;
        }

        TogglePanel();
    }

    private void OnDisconnected(string? _)
    {
        DispatchOnGameThread(() =>
        {
            if (_unloading)
            {
                return;
            }

            ClosePanel();
        });
    }

    private void DisposePanelRowTooltips()
    {
        foreach (IDisposable binding in _panelRowTooltipBindings)
        {
            try
            {
                binding.Dispose();
            }
            catch
            {
                // Ignore teardown errors.
            }
        }

        _panelRowTooltipBindings.Clear();
    }

    private void Track(IClientContext context, IDisposable resource)
    {
        _registrations.Add(resource);
        context.TrackDisposable(resource);
    }

    private void DispatchOnGameThread(Action action)
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        if (ctx.Scheduler.IsOnGameThread)
        {
            action();
        }
        else
        {
            ctx.Scheduler.RunOnGameThread(action);
        }
    }

    private void EnsureVirtualUiFallback(IClientContext context, IGameServices game)
    {
        if (game.Files.Exists(IdolsVaultProtocol.PanelUiPath))
        {
            return;
        }

        string diskPath = Path.Combine(
            context.ModDirectory,
            "client",
            "Metadata",
            "UI",
            "Mods",
            "IdolsVault",
            "IdolsVaultPanel.ui");
        string content = File.Exists(diskPath) ? File.ReadAllText(diskPath) : EmbeddedFallbackUi;
        Track(context, game.Files.RegisterVirtualTextFile(IdolsVaultProtocol.PanelUiPath, content, priority: 50));
    }

    private void LoadInitialSettings(string modDirectory)
    {
        if (string.IsNullOrWhiteSpace(modDirectory))
        {
            return;
        }

        string settingsPath = Path.Combine(modDirectory, "settings.json");
        if (!File.Exists(settingsPath))
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!doc.RootElement.TryGetProperty("settings", out JsonElement settings) || settings.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (TryGetDefaultInt(settings, "defaultGearVaultTabIndex", out int tabIdx))
            {
                _targetGearVaultTabIndex = (ushort)Math.Clamp(tabIdx, 0, IdolsVaultProtocol.MaxGearVaultTabIndex);
            }

            if (TryGetDefaultString(settings, "defaultGearClassFilter", out string gearClass))
            {
                _gearClassFilter = GearVaultClasses.Normalize(gearClass);
            }

            if (TryGetDefaultBool(settings, "enableStockAffinityMapping", out bool enableStock))
            {
                _stockAffinityEnabled = enableStock;
            }

            if (TryGetDefaultInt(settings, "stockAffinityBitId", out int bitId))
            {
                _stockAffinityBitId = (byte)Math.Clamp(bitId, 0, IdolsVaultProtocol.MaxStockAffinityBitId);
            }

            if (TryGetDefaultBool(settings, "showAltTooltipAffixTags", out bool showAlt))
            {
                _showAltTooltipAffixTags = showAlt;
            }
        }
        catch
        {
            // Fall back cleanly to built-in defaults if settings.json cannot be parsed.
        }
    }

    private static bool IsIdolItem(HoveredItemInfo item) =>
        item.MetadataPath.Contains("Idol", StringComparison.OrdinalIgnoreCase) ||
        item.BaseName.Contains("Idol", StringComparison.OrdinalIgnoreCase);

    private static List<string> BuildAffixTagsForItem(HoveredItemInfo item, bool isIdol, string? gearClass)
    {
        List<string> tags = [];
        if (isIdol)
        {
            tags.Add("Attack");
            tags.Add("Life");
            tags.Add("Idol");
            if (item.Width * item.Height >= 3)
            {
                tags.Add("Atlas");
            }

            if (item.Rarity == ItemRarity.Unique)
            {
                tags.Add("Unique");
            }
        }
        else if (gearClass is not null)
        {
            switch (gearClass)
            {
                case GearVaultClasses.Weapons:
                    tags.Add("Attack");
                    tags.Add("Critical");
                    tags.Add("Speed");
                    break;
                case GearVaultClasses.Rings:
                case GearVaultClasses.Amulets:
                case GearVaultClasses.Belts:
                    tags.Add("Life");
                    tags.Add("Resistance");
                    tags.Add("Attribute");
                    break;
                default:
                    tags.Add("Defence");
                    tags.Add("Life");
                    tags.Add("Resistance");
                    break;
            }

            tags.Add(gearClass.Replace(" ", string.Empty, StringComparison.Ordinal));
        }
        else
        {
            tags.Add("Explicit");
            tags.Add(item.Rarity.ToString());
        }

        if (item.IsCorrupted)
        {
            tags.Add("Corrupted");
        }

        return tags;
    }

    private static string BuildTierSummaryLine(HoveredItemInfo item, bool isIdol, string? gearClass)
    {
        string tierBand = item.ItemLevel switch
        {
            >= 86 => "T1 Pinnacle Pool (iLvl 86+)",
            >= 84 => "T1 Explicit Pool (iLvl 84+)",
            >= 75 => "T2 Endgame Pool (iLvl 75+)",
            > 0 => string.Create(CultureInfo.InvariantCulture, $"Mid-Tier Pool (iLvl {item.ItemLevel})"),
            _ => "Base Tier Pool",
        };

        if (isIdol)
        {
            string footprint = $"{item.Width}x{item.Height}";
            bool valid = IdolBoardGeometry.IsValidIdolSize(item.Width, item.Height);
            return $"Tier Summary: {tierBand} | Idol {footprint} ({(valid ? "32-Cell Board Eligible" : "Non-Standard Size")})";
        }

        if (gearClass is not null)
        {
            return $"Tier Summary: {tierBand} | Gear Vault Class: {gearClass}";
        }

        return $"Tier Summary: {tierBand} | Rarity: {item.Rarity}";
    }

    private static bool TryGetDefaultInt(JsonElement settings, string key, out int value)
    {
        value = 0;
        return settings.TryGetProperty(key, out JsonElement entry) &&
               entry.TryGetProperty("default", out JsonElement def) &&
               def.ValueKind == JsonValueKind.Number &&
               def.TryGetInt32(out value);
    }

    private static bool TryGetDefaultBool(JsonElement settings, string key, out bool value)
    {
        value = false;
        if (settings.TryGetProperty(key, out JsonElement entry) &&
            entry.TryGetProperty("default", out JsonElement def) &&
            (def.ValueKind == JsonValueKind.True || def.ValueKind == JsonValueKind.False))
        {
            value = def.GetBoolean();
            return true;
        }

        return false;
    }

    private static bool TryGetDefaultString(JsonElement settings, string key, out string value)
    {
        value = string.Empty;
        if (settings.TryGetProperty(key, out JsonElement entry) &&
            entry.TryGetProperty("default", out JsonElement def) &&
            def.ValueKind == JsonValueKind.String &&
            def.GetString() is { } text)
        {
            value = text;
            return true;
        }

        return false;
    }
}
