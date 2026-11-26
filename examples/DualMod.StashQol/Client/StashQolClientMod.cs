using System.Globalization;
using System.Text.RegularExpressions;
using DualMod.StashQol.Shared;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;

namespace DualMod.StashQol.Client;

/// <summary>
/// Client half of <c>DualMod.StashQol</c> targeting NativeMods SDK <c>0.3.0</c>.
/// Registers conflict-free native toolbar buttons (<see cref="IScreensService"/>),
/// suppressible <c>Ctrl+Shift+RMB</c> bulk stack use (<see cref="IInputService"/> + <see cref="IHoverService"/>),
/// native Regex Roller &amp; PoB Import dialogs (<see cref="IUiService"/> + <see cref="IClipboardService"/>),
/// and HUD toast notifications (<see cref="IToastService"/>).
/// </summary>
public sealed class StashQolClientMod : IClientMod
{
    private const string DefaultRegexCurrency = StashQolProtocol.ChaosOrbMetadata;

    private IClientContext? _context;
    private IUiWindow? _regexWindow;
    private IUiWindow? _pobWindow;
    private HoveredItemInfo? _capturedTarget;
    private string _selectedCurrencyPath = DefaultRegexCurrency;
    private int? _pendingVendorConfirmTab;
    private bool _pobAutoApply = true;
    private bool _unloading;

    private int _bulkUseTriggers;
    private StashQolToastNotification? _lastToast;

    public IUiWindow? RegexWindow => _regexWindow;
    public IUiWindow? PobWindow => _pobWindow;
    public HoveredItemInfo? CapturedTarget => _capturedTarget;
    public int? PendingVendorConfirmTab => _pendingVendorConfirmTab;
    public bool PobAutoApplyEnabled => _pobAutoApply;
    public int BulkUseTriggerCount => _bulkUseTriggers;
    public StashQolToastNotification? LastToast => _lastToast;

    public void Load(IClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _unloading = false;
        IGameServices game = context.Game;

        EnsureVirtualUiFallbacks(context, game);

        // 1. Register managed conflict-free native toolbar buttons across stock panels.
        context.TrackDisposable(game.Screens.RegisterSlotButton(
            ScreenSlot.InventoryToolbar,
            StashQolProtocol.SlotDepositAllId,
            "Deposit All",
            OnDepositAllClicked,
            new SlotAttachmentOptions(Order: 10, Width: 112f, Height: 28f, Tooltip: "Deposit inventory items to matching affinity tabs or active stash tab.")));

        context.TrackDisposable(game.Screens.RegisterSlotButton(
            ScreenSlot.InventoryToolbar,
            StashQolProtocol.SlotSortInventoryId,
            "Sort",
            OnSortInventoryClicked,
            new SlotAttachmentOptions(Order: 20, Width: 72f, Height: 28f, Tooltip: "Deterministically bin-pack and sort the 12x5 inventory.")));

        context.TrackDisposable(game.Screens.RegisterSlotButton(
            ScreenSlot.InventoryToolbar,
            StashQolProtocol.SlotRegexRollerId,
            "Regex Roll",
            OpenRegexRoller,
            new SlotAttachmentOptions(Order: 30, Width: 104f, Height: 28f, Tooltip: "Open the Regex Item Roller dialog.")));

        context.TrackDisposable(game.Screens.RegisterSlotButton(
            ScreenSlot.InventoryToolbar,
            StashQolProtocol.SlotPobImportId,
            "PoB Import",
            OpenPobImport,
            new SlotAttachmentOptions(Order: 40, Width: 104f, Height: 28f, Tooltip: "Import a PoB passive tree and toggle auto-apply on level up.")));

        context.TrackDisposable(game.Screens.RegisterSlotButton(
            ScreenSlot.StashHeader,
            StashQolProtocol.SlotVendorAllId,
            "Vendor All",
            OnVendorAllClicked,
            new SlotAttachmentOptions(Order: 10, Width: 112f, Height: 28f, Tooltip: "Vendor eligible items in the active stash tab (requires confirmation).")));

        context.TrackDisposable(game.Screens.RegisterSlotButton(
            ScreenSlot.MapTabBar,
            StashQolProtocol.SlotUpgradeMapsId,
            "Upgrade 3->1",
            OnUpgradeMapsClicked,
            new SlotAttachmentOptions(Order: 10, Width: 118f, Height: 28f, Tooltip: "Upgrade normal group-0 maps 3-to-1 in the Map Stash.")));

        context.TrackDisposable(game.Screens.RegisterSlotButton(
            ScreenSlot.BlightTabBar,
            StashQolProtocol.SlotUpgradeOilsId,
            "Upgrade Oils",
            OnUpgradeOilsClicked,
            new SlotAttachmentOptions(Order: 10, Width: 118f, Height: 28f, Tooltip: "Cascade 3-to-1 upgrades across all 13 Blight oil tiers.")));

        // 2. Register discoverable keybind entry + low-level suppressible Ctrl+Shift+RMB handler.
        KeyChord defaultBulkChord = KeyChord.FromMouse(MouseButton.Right, KeyModifiers.Control | KeyModifiers.Shift);
        context.TrackDisposable(game.Input.Keybinds.Register(
            StashQolProtocol.BulkUseKeybindId,
            "Bulk Stack Use (Ctrl+Shift+RMB)",
            defaultBulkChord,
            static () => { },
            allowWhenTextInputFocused: false,
            suppressGameInput: false));

        context.TrackDisposable(game.Input.OnMouse(
            HandleMouseEvent,
            priority: 0,
            allowWhenTextInputFocused: false));

        // 3. Subscribe to screen/hover/disconnect events and server toast replies.
        game.Screens.ActiveStashTabChanged += OnActiveStashTabChanged;
        game.Hover.HoverChanged += OnHoverChanged;
        game.Lifecycle.Disconnected += OnDisconnected;

        context.TrackDisposable(context.Net.On<StashQolToastNotification>(
            notification => DispatchOnGameThread(() => HandleToastNotification(notification))));

        context.Logger.Info("DualMod.StashQol client initialized (SDK 0.3.0).");
    }

    public void Unload()
    {
        _unloading = true;
        if (_context is { } ctx)
        {
            ctx.Game.Screens.ActiveStashTabChanged -= OnActiveStashTabChanged;
            ctx.Game.Hover.HoverChanged -= OnHoverChanged;
            ctx.Game.Lifecycle.Disconnected -= OnDisconnected;
        }

        CloseRegexRoller();
        ClosePobImport();
        _pendingVendorConfirmTab = null;
        _capturedTarget = null;
        _context = null;
    }

    public void OpenRegexRoller()
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

            if (_regexWindow is { IsOpen: true })
            {
                _regexWindow.Visible = true;
                RefreshRegexPreview();
                return;
            }

            IUiWindow window = ctx.Game.Ui.Load(StashQolProtocol.RegexRollerUiPath, StashQolProtocol.RegexRollerWindowId);
            _regexWindow = window;
            window.Closed += () =>
            {
                if (ReferenceEquals(_regexWindow, window))
                {
                    _regexWindow = null;
                }
            };

            if (window.Find<IButton>("close_button") is { } closeBtn)
            {
                closeBtn.Clicked += CloseRegexRoller;
            }

            if (window.Find<IButton>("cancel_button") is { } cancelBtn)
            {
                cancelBtn.Clicked += CloseRegexRoller;
            }

            if (window.Find<IButton>("capture_hover_button") is { } captureBtn)
            {
                captureBtn.Clicked += CaptureCurrentHoverForRoller;
            }

            if (window.Find<IButton>("roll_button") is { } rollBtn)
            {
                rollBtn.Clicked += SubmitRegexRoll;
            }

            if (window.Find<ITextBox>("pattern_textbox") is { } patternBox)
            {
                patternBox.Submitted += _ => SubmitRegexRoll();
            }

            CaptureCurrentHoverForRoller();
        });
    }

    public void CloseRegexRoller()
    {
        IUiWindow? window = _regexWindow;
        _regexWindow = null;
        if (window is { IsOpen: true })
        {
            window.Close();
        }
    }

    public void OpenPobImport()
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

            if (_pobWindow is { IsOpen: true })
            {
                _pobWindow.Visible = true;
                return;
            }

            IUiWindow window = ctx.Game.Ui.Load(StashQolProtocol.PobImportUiPath, StashQolProtocol.PobImportWindowId);
            _pobWindow = window;
            window.Closed += () =>
            {
                if (ReferenceEquals(_pobWindow, window))
                {
                    _pobWindow = null;
                }
            };

            if (window.Find<IButton>("close_button") is { } closeBtn)
            {
                closeBtn.Clicked += ClosePobImport;
            }

            if (window.Find<IButton>("cancel_button") is { } cancelBtn)
            {
                cancelBtn.Clicked += ClosePobImport;
            }

            if (window.Find<IButton>("paste_clipboard_button") is { } pasteBtn)
            {
                pasteBtn.Clicked += PasteClipboardIntoPobBox;
            }

            if (window.Find<ICheckBox>("auto_apply_checkbox") is { } autoBox)
            {
                autoBox.IsChecked = _pobAutoApply;
                autoBox.CheckedChanged += OnPobAutoApplyToggled;
            }

            if (window.Find<IButton>("import_button") is { } importBtn)
            {
                importBtn.Clicked += SubmitPobImport;
            }

            if (window.Find<IButton>("clear_plan_button") is { } clearBtn)
            {
                clearBtn.Clicked += SubmitPobClear;
            }
        });
    }

    public void ClosePobImport()
    {
        IUiWindow? window = _pobWindow;
        _pobWindow = null;
        if (window is { IsOpen: true })
        {
            window.Close();
        }
    }

    private void OnDepositAllClicked()
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        int? fallbackTab = ctx.Game.Screens.ActiveStashTab?.Index;
        ctx.Net.Send(new DepositAllRequest(fallbackTab, RespectAffinities: true));
    }

    private void OnSortInventoryClicked()
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        ctx.Net.Send(new InventorySortRequest(IncludeExpanded: false));
    }

    private void OnVendorAllClicked()
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        StashTabInfo? activeTab = ctx.Game.Screens.ActiveStashTab;
        if (activeTab is null)
        {
            ctx.Game.Toasts.Show("Vendor All", "Open a stash tab before vendoring.", ToastSeverity.Warning);
            return;
        }

        if (_pendingVendorConfirmTab != activeTab.Index)
        {
            _pendingVendorConfirmTab = activeTab.Index;
            ctx.Game.Toasts.Show(
                "Confirm Vendor All",
                $"Click Vendor All again to sell eligible items in tab '{activeTab.Name}'.",
                ToastSeverity.Warning);
            return;
        }

        _pendingVendorConfirmTab = null;
        ctx.Net.Send(new VendorAllRequest(activeTab.Index, Confirmed: true));
    }

    private void OnUpgradeMapsClicked()
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        StashTabInfo? activeTab = ctx.Game.Screens.ActiveStashTab;
        if (activeTab is null || activeTab.Type != StashTabType.Map)
        {
            ctx.Game.Toasts.Show("Map Upgrade", "Select a Map Stash tab first.", ToastSeverity.Warning);
            return;
        }

        ctx.Net.Send(new UpgradeMapTiersRequest(activeTab.Index, activeTab.MapTier, IncludeCorrupted: false));
    }

    private void OnUpgradeOilsClicked()
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        StashTabInfo? activeTab = ctx.Game.Screens.ActiveStashTab;
        if (activeTab is null || activeTab.Type != StashTabType.Blight)
        {
            ctx.Game.Toasts.Show("Blight Oils", "Select a Blight Stash tab first.", ToastSeverity.Warning);
            return;
        }

        ctx.Net.Send(new UpgradeBlightOilsRequest(activeTab.Index));
    }

    private void HandleMouseEvent(MouseEventArgs e)
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        if (e.Kind != InputEventKind.Down || e.IsTextInputFocused || ctx.Game.Input.IsTextInputFocused)
        {
            return;
        }

        KeyChord chord = ctx.Game.Input.Keybinds.GetBinding(StashQolProtocol.BulkUseKeybindId)
            ?? KeyChord.FromMouse(MouseButton.Right, KeyModifiers.Control | KeyModifiers.Shift);

        if (!chord.IsMouseChord || e.Button != chord.MouseButton || e.Modifiers != chord.Modifiers)
        {
            return;
        }

        HoveredItemInfo? hovered = ctx.Game.Hover.RefreshHoveredItem() ?? ctx.Game.Hover.HoveredItem;
        if (hovered is null)
        {
            return;
        }

        CursorItemInfo? cursor = ctx.Game.Hover.RefreshCursorItem() ?? ctx.Game.Hover.CursorItem;
        bool hasTargetedCursor = cursor is { Mode: CursorMode.UsingItem } && !string.IsNullOrWhiteSpace(cursor.MetadataPath);
        bool isSelfUsable = hovered.StackSize > 0 && (hovered.MaxStackSize > 1 || IsKnownSelfUsable(hovered.MetadataPath));

        if (!hasTargetedCursor && !isSelfUsable)
        {
            return;
        }

        // Suppress PoE's native RMB (and its matching button-up) before C->S 0x58 / 0x71 / 0x72 fires.
        e.Handled = true;
        _bulkUseTriggers++;

        BulkUseRequest request = new(
            HoveredInventoryId: hovered.InventoryId,
            HoveredItemId: hovered.ItemId,
            HoveredGridX: hovered.GridX,
            HoveredGridY: hovered.GridY,
            HoveredMetadataPath: hovered.MetadataPath,
            HoveredStackSize: hovered.StackSize,
            CursorSourceInventoryId: hasTargetedCursor ? cursor!.SourceInventoryId : null,
            CursorItemId: hasTargetedCursor ? cursor!.ItemId : null,
            CursorMetadataPath: hasTargetedCursor ? cursor!.MetadataPath : null,
            CursorStackSize: hasTargetedCursor ? cursor!.StackSize : 0u);

        ctx.Net.Send(request);
    }

    private void CaptureCurrentHoverForRoller()
    {
        if (_context is not { } ctx)
        {
            return;
        }

        HoveredItemInfo? hovered = ctx.Game.Hover.RefreshHoveredItem() ?? ctx.Game.Hover.HoveredItem;
        CursorItemInfo? cursor = ctx.Game.Hover.RefreshCursorItem() ?? ctx.Game.Hover.CursorItem;
        if (hovered is not null)
        {
            _capturedTarget = hovered;
        }

        if (cursor is { Mode: CursorMode.UsingItem } && !string.IsNullOrWhiteSpace(cursor.MetadataPath))
        {
            _selectedCurrencyPath = cursor.MetadataPath;
        }

        RefreshRegexPreview();
    }

    private void RefreshRegexPreview()
    {
        if (_regexWindow is not { IsOpen: true } window)
        {
            return;
        }

        if (window.Find<ILabel>("target_preview_label") is { } targetLabel)
        {
            targetLabel.Text = _capturedTarget is { } item
                ? $"Target: {item.BaseName} (Inv {item.InventoryId} @ {item.GridX},{item.GridY})"
                : "Target: Hover an item and click Capture";
        }

        if (window.Find<ILabel>("orb_type_label") is { } orbLabel)
        {
            string shortName = _selectedCurrencyPath.Split('/').LastOrDefault() ?? _selectedCurrencyPath;
            orbLabel.Text = $"Currency: {shortName}";
        }
    }

    private void SubmitRegexRoll()
    {
        if (_unloading || _context is not { } ctx || _regexWindow is not { IsOpen: true } window)
        {
            return;
        }

        HoveredItemInfo? target = _capturedTarget ?? ctx.Game.Hover.RefreshHoveredItem() ?? ctx.Game.Hover.HoveredItem;
        if (target is null)
        {
            SetWindowStatus(window, "No target item captured. Hover an item first.");
            ctx.Game.Toasts.Show("Regex Roller", "Hover or capture a target item first.", ToastSeverity.Warning);
            return;
        }

        string pattern = window.Find<ITextBox>("pattern_textbox")?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            SetWindowStatus(window, "Enter a regex pattern before rolling.");
            ctx.Game.Toasts.Show("Regex Roller", "Regex pattern cannot be empty.", ToastSeverity.Warning);
            return;
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(StashQolProtocol.RegexTimeoutMilliseconds));
        }
        catch (ArgumentException ex)
        {
            SetWindowStatus(window, $"Invalid regex: {ex.Message}");
            ctx.Game.Toasts.Show("Regex Roller", $"Invalid regex pattern: {ex.Message}", ToastSeverity.Error);
            return;
        }

        int maxRolls = 200;
        string? rawMax = window.Find<ITextBox>("max_rolls_textbox")?.Text;
        if (!string.IsNullOrWhiteSpace(rawMax) &&
            int.TryParse(rawMax.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            maxRolls = Math.Clamp(parsed, 1, StashQolProtocol.MaxRegexRollsHardCap);
        }

        bool ignoreCase = window.Find<ICheckBox>("case_insensitive_checkbox")?.IsChecked ?? true;
        bool matchFull = window.Find<ICheckBox>("match_all_lines_checkbox")?.IsChecked ?? false;

        SetWindowStatus(window, $"Rolling {target.BaseName} up to {maxRolls}x...");
        ctx.Net.Send(new RollUntilRegexRequest(
            TargetInventoryId: target.InventoryId,
            TargetItemId: target.ItemId,
            TargetGridX: target.GridX,
            TargetGridY: target.GridY,
            CurrencyMetadataPath: _selectedCurrencyPath,
            Pattern: pattern,
            MaxRolls: maxRolls,
            IgnoreCase: ignoreCase,
            MatchFullCard: matchFull));
    }

    private void PasteClipboardIntoPobBox()
    {
        if (_context is not { } ctx || _pobWindow is not { IsOpen: true } window)
        {
            return;
        }

        string? clip = ctx.Game.Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(clip))
        {
            SetWindowStatus(window, "Clipboard is empty.");
            ctx.Game.Toasts.Show("PoB Import", "Clipboard does not contain text.", ToastSeverity.Warning);
            return;
        }

        if (window.Find<ITextBox>("pob_code_textbox") is { } box)
        {
            box.Text = clip.Trim();
            SetWindowStatus(window, $"Pasted {box.Text.Length} character(s) from clipboard.");
        }
    }

    private void OnPobAutoApplyToggled(bool isChecked)
    {
        _pobAutoApply = isChecked;
        if (!_unloading && _context is { } ctx)
        {
            ctx.Net.Send(new PobToggleAutoApplyRequest(isChecked));
        }
    }

    private void SubmitPobImport()
    {
        if (_unloading || _context is not { } ctx || _pobWindow is not { IsOpen: true } window)
        {
            return;
        }

        string payload = window.Find<ITextBox>("pob_code_textbox")?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
        {
            SetWindowStatus(window, "Paste a PoB code, XML tree, or passive node list first.");
            ctx.Game.Toasts.Show("PoB Import", "PoB payload is empty.", ToastSeverity.Warning);
            return;
        }

        bool autoApply = window.Find<ICheckBox>("auto_apply_checkbox")?.IsChecked ?? _pobAutoApply;
        _pobAutoApply = autoApply;
        SetWindowStatus(window, "Importing passive plan...");
        ctx.Net.Send(new PobImportRequest(payload, AutoApplyOnLevelUp: autoApply, ApplyImmediately: true));
    }

    private void SubmitPobClear()
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        ctx.Net.Send(new PobClearPlanRequest());
        if (_pobWindow is { IsOpen: true } window)
        {
            if (window.Find<ITextBox>("pob_code_textbox") is { } box)
            {
                box.Text = string.Empty;
            }

            SetWindowStatus(window, "Cleared stored PoB passive plan.");
        }
    }

    private void HandleToastNotification(StashQolToastNotification notification)
    {
        if (_unloading || _context is not { } ctx)
        {
            return;
        }

        _lastToast = notification;
        if (notification.RequiresConfirmation && notification.PendingTabIndex is int pendingTab)
        {
            _pendingVendorConfirmTab = pendingTab;
        }

        ToastSeverity severity = ParseSeverity(notification.Severity);
        ctx.Game.Toasts.Show(notification.Title, notification.Message, severity);

        if (string.Equals(notification.Feature, "regex_roll", StringComparison.OrdinalIgnoreCase) && _regexWindow is { IsOpen: true } regexWin)
        {
            SetWindowStatus(regexWin, notification.Message);
        }
        else if (string.Equals(notification.Feature, "pob_passives", StringComparison.OrdinalIgnoreCase) && _pobWindow is { IsOpen: true } pobWin)
        {
            SetWindowStatus(pobWin, notification.Message);
        }
    }

    private void OnActiveStashTabChanged(ActiveStashTabChangedEventArgs args)
    {
        // Changing tabs always clears any pending Vendor All confirmation latch.
        if (_pendingVendorConfirmTab is int pending && args.Current?.Index != pending)
        {
            _pendingVendorConfirmTab = null;
        }
    }

    private void OnHoverChanged(HoverChangedEventArgs args)
    {
        if (_unloading)
        {
            return;
        }

        if (_regexWindow is { IsOpen: true } && args.Item is not null && _capturedTarget is null)
        {
            _capturedTarget = args.Item;
            RefreshRegexPreview();
        }
    }

    private void OnDisconnected(string? _)
    {
        DispatchOnGameThread(() =>
        {
            _pendingVendorConfirmTab = null;
            CloseRegexRoller();
            ClosePobImport();
        });
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
            ctx.Scheduler.RunOnGameThread(() =>
            {
                if (!_unloading)
                {
                    action();
                }
            });
        }
    }

    private static bool IsKnownSelfUsable(string metadataPath) =>
        metadataPath.Equals(StashQolProtocol.StackedDeckMetadata, StringComparison.OrdinalIgnoreCase)
        || metadataPath.Equals(StashQolProtocol.OrbOfRegretMetadata, StringComparison.OrdinalIgnoreCase)
        || metadataPath.Equals(StashQolProtocol.OrbOfUnmakingMetadata, StringComparison.OrdinalIgnoreCase);

    private static ToastSeverity ParseSeverity(string severity) => severity.ToUpperInvariant() switch
    {
        "SUCCESS" => ToastSeverity.Success,
        "WARNING" or "WARN" => ToastSeverity.Warning,
        "ERROR" => ToastSeverity.Error,
        _ => ToastSeverity.Info,
    };

    private static void SetWindowStatus(IUiWindow window, string message)
    {
        if (window.Find<ILabel>("status_label") is { } label)
        {
            label.Text = message;
        }
    }

    private static void EnsureVirtualUiFallbacks(IClientContext context, IGameServices game)
    {
        if (!game.Files.Exists(StashQolProtocol.RegexRollerUiPath))
        {
            context.TrackDisposable(game.Files.RegisterVirtualTextFile(
                StashQolProtocol.RegexRollerUiPath,
                FallbackRegexRollerUi,
                priority: 50));
        }

        if (!game.Files.Exists(StashQolProtocol.PobImportUiPath))
        {
            context.TrackDisposable(game.Files.RegisterVirtualTextFile(
                StashQolProtocol.PobImportUiPath,
                FallbackPobImportUi,
                priority: 50));
        }
    }

    private const string FallbackRegexRollerUi = """
// Fallback Regex Roller UI
<DEFINITIONS>
begin Frame
	id = stashqol_regex_roller;
	size = 640, 420;
	begin Label
		id = target_preview_label;
		text = "Target: None";
	end
	begin Label
		id = orb_type_label;
		text = "Currency: Chaos Orb";
	end
	begin BasicButton
		id = capture_hover_button;
		text = "Capture Hover";
	end
	begin TextBox
		id = pattern_textbox;
		text = "";
	end
	begin TextBox
		id = max_rolls_textbox;
		text = "200";
	end
	begin CheckBox
		id = case_insensitive_checkbox;
		checked = true;
	end
	begin CheckBox
		id = match_all_lines_checkbox;
		checked = false;
	end
	begin BasicButton
		id = roll_button;
		text = "Roll Until Match";
	end
	begin BasicButton
		id = cancel_button;
		text = "Close";
	end
	begin BasicButton
		id = close_button;
		text = "X";
	end
	begin Label
		id = status_label;
		text = "Ready.";
	end
end
""";

    private const string FallbackPobImportUi = """
// Fallback PoB Import UI
<DEFINITIONS>
begin Frame
	id = stashqol_pob_import;
	size = 660, 400;
	begin BasicButton
		id = paste_clipboard_button;
		text = "Paste Clipboard";
	end
	begin TextBox
		id = pob_code_textbox;
		text = "";
	end
	begin CheckBox
		id = auto_apply_checkbox;
		checked = true;
	end
	begin BasicButton
		id = import_button;
		text = "Import & Apply";
	end
	begin BasicButton
		id = clear_plan_button;
		text = "Clear Plan";
	end
	begin BasicButton
		id = cancel_button;
		text = "Close";
	end
	begin BasicButton
		id = close_button;
		text = "X";
	end
	begin Label
		id = status_label;
		text = "Ready.";
	end
end
""";
}
