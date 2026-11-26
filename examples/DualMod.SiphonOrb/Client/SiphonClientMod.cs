using DualMod.SiphonOrb.Shared;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;

namespace DualMod.SiphonOrb.Client;

/// <summary>
/// Per-area client ruleset registered for Orb of Siphoning updates (ruleset id <see cref="SiphonProtocol.SiphonRulesetId"/>).
/// </summary>
public sealed class SiphonRuleset : IClientRuleset
{
    public byte RulesetId => SiphonProtocol.SiphonRulesetId;
    public string Name => "SiphonOrbRuleset";
    public int UpdatesReceived { get; private set; }
    public int AreaEntries { get; private set; }

    public void OnAreaEntered(AreaEventArgs area) => AreaEntries++;

    public void OnRulesetUpdate(ReadOnlySpan<byte> payload) => UpdatesReceived++;

    public void OnFrame(float deltaSeconds) { }

    public void OnAreaLeft() { }
}

/// <summary>
/// Client half of the Orb of Siphoning dual-side mod, running inside the 3.29.3d client via
/// <c>LocalExile.Client.Host</c> inside a collectible <c>AssemblyLoadContext</c>.
/// Manages the native Recombinator-style <c>SiphonPicker.ui</c> modal across Steal and Imbue cycles.
/// </summary>
public sealed class SiphonClientMod : IClientMod
{
    private IClientContext? _context;
    private IUiWindow? _window;
    private OpenSiphonPicker? _activePicker;
    private SiphonResultNotification? _lastResult;
    private int _selectedAffixIndex = -1;
    private int _openCount;
    private bool _unloading;

    public IUiWindow? Window => _window;
    public OpenSiphonPicker? ActivePicker => _activePicker;
    public SiphonResultNotification? LastResult => _lastResult;
    public int SelectedAffixIndex => _selectedAffixIndex;
    public int OpenCount => _openCount;

    public void Load(IClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var game = context.Game;

        // 1. Register optional generated siphon assets overlay directory (dist/siphon-assets)
        string? externalAssetsDir = Environment.GetEnvironmentVariable("NATIVEMODS_SIPHON_ASSETS_DIR");
        if (!string.IsNullOrWhiteSpace(externalAssetsDir) && Directory.Exists(externalAssetsDir))
        {
            context.TrackDisposable(game.Files.RegisterDirectory(externalAssetsDir, priority: 25));
        }

        // Ensure both .it metadata scripts are always resolvable via IFileOverlayService.
        if (!game.Files.Exists(SiphonProtocol.EmptyOrbItPath))
        {
            context.TrackDisposable(game.Files.RegisterVirtualTextFile(
                SiphonProtocol.EmptyOrbItPath,
                "version 3\r\nextends \"Metadata/Items/Currency/StackableCurrency\"\r\n",
                priority: 50));
        }

        if (!game.Files.Exists(SiphonProtocol.ChargedOrbItPath))
        {
            context.TrackDisposable(game.Files.RegisterVirtualTextFile(
                SiphonProtocol.ChargedOrbItPath,
                "version 3\r\nextends \"Metadata/Items/Currency/StackableCurrency\"\r\n\r\nBase\r\n{\r\n}\r\n\r\nLocalStats\r\n{\r\n}\r\n\r\nMods\r\n{\r\n\tstat_description_list = \"Metadata/StatDescriptions/stat_descriptions.txt\"\r\n\tmod_stat_description_list = \"Metadata/StatDescriptions/advanced_mod_stat_descriptions.txt\"\r\n\tenable_rarity = \"normal\"\r\n\tenable_rarity = \"magic\"\r\n\tenable_rarity = \"rare\"\r\n\tenable_rarity = \"unique\"\r\n}\r\n",
                priority: 50));
        }

        // 2. Register append-only content declarations for both Orb of Siphoning currencies (never replacing stock rows).
        context.TrackDisposable(game.Content.DeclareDatRow(new DatRowDeclaration(
            TableName: "BaseItemTypes",
            EntryKey: SiphonProtocol.EmptyOrbBaseId,
            Fields: new Dictionary<string, object?>
            {
                ["Id"] = SiphonProtocol.EmptyOrbBaseId,
                ["Name"] = SiphonProtocol.EmptyOrbDisplayName,
                ["HASH32"] = SiphonProtocol.EmptyOrbTypeHash,
                ["ItemVisualIdentity"] = SiphonProtocol.EmptyOrbVisualRow
            })));

        context.TrackDisposable(game.Content.DeclareDatRow(new DatRowDeclaration(
            TableName: "BaseItemTypes",
            EntryKey: SiphonProtocol.ChargedOrbBaseId,
            Fields: new Dictionary<string, object?>
            {
                ["Id"] = SiphonProtocol.ChargedOrbBaseId,
                ["Name"] = SiphonProtocol.ChargedOrbDisplayName,
                ["HASH32"] = SiphonProtocol.ChargedOrbTypeHash,
                ["ItemVisualIdentity"] = SiphonProtocol.ChargedOrbVisualRow
            })));

        context.TrackDisposable(game.Content.DeclareDatRow(new DatRowDeclaration(
            TableName: "ItemVisualIdentity",
            EntryKey: SiphonProtocol.EmptyOrbVisualId,
            Fields: new Dictionary<string, object?>
            {
                ["Id"] = SiphonProtocol.EmptyOrbVisualId,
                ["DDSFile"] = SiphonProtocol.EmptyOrbIconPath,
                ["HASH16"] = SiphonProtocol.EmptyOrbVisualHash16
            })));

        context.TrackDisposable(game.Content.DeclareDatRow(new DatRowDeclaration(
            TableName: "ItemVisualIdentity",
            EntryKey: SiphonProtocol.ChargedOrbVisualId,
            Fields: new Dictionary<string, object?>
            {
                ["Id"] = SiphonProtocol.ChargedOrbVisualId,
                ["DDSFile"] = SiphonProtocol.ChargedOrbIconPath,
                ["HASH16"] = SiphonProtocol.ChargedOrbVisualHash16
            })));

        context.TrackDisposable(game.Content.DeclareDatRow(new DatRowDeclaration(
            TableName: "CurrencyItems",
            EntryKey: SiphonProtocol.EmptyOrbBaseId,
            Fields: new Dictionary<string, object?>
            {
                ["BaseItemTypesKey"] = SiphonProtocol.EmptyOrbBaseItemTypesRow,
                ["StackSize"] = SiphonProtocol.EmptyOrbMaxStack,
                ["CurrencyUseType"] = 2,
                ["Action"] = "remove_random_mod"
            })));

        context.TrackDisposable(game.Content.DeclareDatRow(new DatRowDeclaration(
            TableName: "CurrencyItems",
            EntryKey: SiphonProtocol.ChargedOrbBaseId,
            Fields: new Dictionary<string, object?>
            {
                ["BaseItemTypesKey"] = SiphonProtocol.ChargedOrbBaseItemTypesRow,
                ["StackSize"] = SiphonProtocol.ChargedOrbMaxStack,
                ["CurrencyUseType"] = 2,
                ["Action"] = "remove_random_mod"
            })));

        // 3. Register per-area custom ruleset and custom opcodes.
        context.TrackDisposable(game.Rulesets.Register<SiphonRuleset>());
        context.TrackDisposable(game.Opcodes.RegisterHandler(
            SiphonProtocol.OpcodeOpenPicker,
            payload => context.Logger.Debug($"Received raw SiphonOpenPicker opcode ({payload.Length} bytes)."),
            SiphonProtocol.ModId));
        context.TrackDisposable(game.Opcodes.RegisterHandler(
            SiphonProtocol.OpcodeResultNotify,
            payload => context.Logger.Debug($"Received raw SiphonResultNotify opcode ({payload.Length} bytes)."),
            SiphonProtocol.ModId));

        // 4. Subscribe to typed Server <-> Client messages over context.Net (HostRuleset 0xA3 / ChatFallback),
        //    marshaling callbacks onto the game thread via IModScheduler.
        context.TrackDisposable(context.Net.On<OpenSiphonPicker>(picker => DispatchOnGameThread(() => HandleOpenPicker(picker))));
        context.TrackDisposable(context.Net.On<SiphonResultNotification>(result => DispatchOnGameThread(() => HandleResultNotification(result))));

        // 5. Disconnect cleanup: close picker cleanly on area/session disconnect.
        game.Lifecycle.Disconnected += OnDisconnected;
    }

    public void Unload()
    {
        _unloading = true;
        if (_context is not null)
        {
            _context.Game.Lifecycle.Disconnected -= OnDisconnected;
        }

        if (_window is { } window)
        {
            window.Closed -= OnWindowClosed;
            window.Close();
        }

        _window = null;
        _activePicker = null;
        _lastResult = null;
        _selectedAffixIndex = -1;
        _context = null;
    }

    /// <summary>Selects a modifier row in the currently open picker (used by both button clicks and tests).</summary>
    public bool SelectRow(int rowIndex)
    {
        if (_activePicker is null || rowIndex < 0 || rowIndex >= _activePicker.Affixes.Count)
            return false;

        SiphonAffixOption option = _activePicker.Affixes[rowIndex];
        if (!option.Selectable)
        {
            UpdateStatusText(option.DisabledReason ?? "That modifier cannot be selected.");
            return false;
        }

        _selectedAffixIndex = option.Index;
        RefreshRowStates();
        return true;
    }

    /// <summary>Submits the currently selected modifier to the server.</summary>
    public bool ConfirmSelection()
    {
        if (_context is null || _activePicker is null)
            return false;

        if (_selectedAffixIndex < 0)
        {
            UpdateStatusText("Select a modifier first before confirming.");
            return false;
        }

        SiphonAffixOption? chosen = _activePicker.Affixes.FirstOrDefault(a => a.Index == _selectedAffixIndex);
        if (chosen is null || !chosen.Selectable)
        {
            UpdateStatusText(chosen?.DisabledReason ?? "Selected modifier is no longer valid.");
            return false;
        }

        UpdateStatusText(_activePicker.Mode == SiphonPickerMode.Steal
            ? $"Siphoning [{chosen.SlotTag}] {chosen.StatText}..."
            : $"Imbuing stored modifier over [{chosen.SlotTag}] {chosen.StatText}...");
        _context.Net.Send(new SelectSiphonAffixRequest(_activePicker.SessionToken, _selectedAffixIndex));
        return true;
    }

    /// <summary>Cancels the active picker session without consuming any orb.</summary>
    public void CancelPicker()
    {
        if (_context is not null && _activePicker is { } active)
        {
            _context.Net.Send(new CancelSiphonPicker(active.SessionToken));
        }

        HideWindow();
    }

    private void OnDisconnected(string? reason) => DispatchOnGameThread(HideWindow);

    private void DispatchOnGameThread(Action action)
    {
        IClientContext? context = _context;
        if (_unloading || context is null)
            return;

        if (context.Scheduler.IsOnGameThread)
        {
            action();
            return;
        }

        context.Scheduler.RunOnGameThread(() =>
        {
            if (_unloading || _context is null)
                return;
            action();
        });
    }

    private void HandleOpenPicker(OpenSiphonPicker picker)
    {
        if (_unloading || _context is null)
            return;

        _activePicker = picker;
        _openCount++;

        // Pre-select the first selectable modifier so players can either click a row or confirm immediately.
        SiphonAffixOption? firstSelectable = picker.Affixes.FirstOrDefault(a => a.Selectable);
        _selectedAffixIndex = firstSelectable?.Index ?? -1;

        EnsureWindowLoaded();
        if (_window is not { IsOpen: true })
            return;

        _window.Visible = true;
        _window.SetProperty("siphon_mode", picker.Mode.ToString());
        _window.SetProperty("siphon_session", picker.SessionToken);
        _window.SetProperty("siphon_target_item", picker.TargetItemName);

        bool isImbue = picker.Mode == SiphonPickerMode.Imbue;
        bool isMagic = string.Equals(picker.TargetRarity, "Magic", StringComparison.OrdinalIgnoreCase);

        SetLabel("title_label", isImbue ? "Charged Orb of Siphoning" : "Orb of Siphoning");
        SetLabel("steal_mode_header", "CLICK A MODIFIER ON THE ITEM CARD TO SIPHON");
        SetLabel("subtitle_label", string.IsNullOrWhiteSpace(picker.PromptText)
            ? "Extracts the chosen explicit modifier into a Charged Orb of Siphoning."
            : picker.PromptText);

        SetVisible("steal_orb_box", !isImbue);
        SetVisible("stored_mod_box", isImbue && picker.StoredAffix is not null);

        if (isImbue && picker.StoredAffix is { } stored)
        {
            string storedSlot = stored.SlotTag.ToUpperInvariant();
            string tierPart = stored.Tier > 0 ? $"  T{stored.Tier}" : string.Empty;
            SetLabel("stored_mod_title", $"IMBUE INTO TARGET ITEM — STORED {storedSlot}{tierPart}");
            SetLabel("stored_mod_label", FormatStoredAffixLine(stored));
            SetLabel("stored_mod_hint", "Click a compatible modifier on the target item card below to replace it.");
        }
        else
        {
            SetLabel("stored_mod_label", "(None)");
        }

        // Authentic PoE Item Card header & separator (Rare 2-line gold vs Magic 1-line blue).
        SetVisible("item_header_rare", !isMagic);
        SetVisible("item_header_magic", isMagic);
        SetVisible("item_sep_rare_pic", !isMagic);
        SetVisible("item_sep_magic_pic", isMagic);

        string displayName = string.IsNullOrWhiteSpace(picker.TargetItemName) ? picker.TargetBaseType : picker.TargetItemName;
        string displayBase = string.IsNullOrWhiteSpace(picker.TargetBaseType) ? displayName : picker.TargetBaseType;
        if (isMagic)
        {
            SetLabel("item_magic_name_label", displayName);
        }
        else
        {
            SetLabel("item_rare_name_label", displayName);
            SetLabel("item_rare_base_label", displayBase);
        }

        IButton? confirmBtn = _window.Find<IButton>("confirm_button");
        if (confirmBtn is not null)
            confirmBtn.Text = isImbue ? "Replace Modifier" : "Siphon Modifier";

        RefreshRowStates();
    }

    private void HandleResultNotification(SiphonResultNotification result)
    {
        if (_unloading || _context is null)
            return;

        _lastResult = result;
        if (_window is { IsOpen: true } window)
        {
            window.SetProperty("siphon_last_result", result.Message);
            UpdateStatusText(result.Message);
        }

        if (result.ClosePicker)
        {
            HideWindow();
        }
    }

    private void EnsureWindowLoaded()
    {
        if (_unloading || _context is null || _window is { IsOpen: true })
            return;

        if (_window is { } stale)
        {
            stale.Closed -= OnWindowClosed;
            _window = null;
        }

        _window = _context.Game.Ui.Load(SiphonProtocol.PickerUiPath, SiphonProtocol.PickerWindowId);
        _window.Closed += OnWindowClosed;

        for (int i = 0; i < SiphonProtocol.MaxPickerAffixRows; i++)
        {
            int rowIndex = i;
            IButton? btn = _window.Find<IButton>($"affix_btn_{rowIndex}");
            if (btn is not null)
            {
                btn.Clicked += () => SelectRow(rowIndex);
            }
        }

        if (_window.Find<IButton>("confirm_button") is { } confirmBtn)
            confirmBtn.Clicked += () => ConfirmSelection();

        if (_window.Find<IButton>("cancel_button") is { } cancelBtn)
            cancelBtn.Clicked += CancelPicker;

        if (_window.Find<IButton>("close_button") is { } closeBtn)
            closeBtn.Clicked += CancelPicker;
    }

    private void OnWindowClosed()
    {
        if (_window is { } closedWindow)
        {
            closedWindow.Closed -= OnWindowClosed;
        }

        _window = null;
        if (!_unloading && _context is not null && _activePicker is { } active)
        {
            _context.Net.Send(new CancelSiphonPicker(active.SessionToken));
        }

        _activePicker = null;
        _selectedAffixIndex = -1;
    }

    private void RefreshRowStates()
    {
        if (_window is not { IsOpen: true } || _activePicker is null)
            return;

        bool isImbue = _activePicker.Mode == SiphonPickerMode.Imbue;
        IReadOnlyList<SiphonAffixOption> affixes = _activePicker.Affixes;
        for (int i = 0; i < SiphonProtocol.MaxPickerAffixRows; i++)
        {
            IUiControl? rowFrame = _window.Find<IUiControl>($"affix_row_{i}");
            IButton? btn = _window.Find<IButton>($"affix_btn_{i}");
            ILabel? label = _window.Find<ILabel>($"affix_label_{i}");

            if (i < affixes.Count)
            {
                SiphonAffixOption opt = affixes[i];
                bool isSelected = opt.Selectable && opt.Index == _selectedAffixIndex;

                if (rowFrame is not null)
                    rowFrame.Visible = true;
                if (btn is not null)
                {
                    btn.Visible = true;
                    // Managed state caption for tests/automation; BasicButton has no native text overlay.
                    btn.Text = !opt.Selectable
                        ? "Locked"
                        : isSelected
                            ? "[SELECTED]"
                            : "Select";
                    btn.SetProperty("siphon_state", !opt.Selectable ? "locked" : isSelected ? "selected" : "default");
                }

                SetVisible($"affix_sel_bg_{i}", isSelected);
                SetVisible($"affix_sel_border_{i}", isSelected);
                SetVisible($"affix_lock_bg_{i}", !opt.Selectable);

                SetVisible($"affix_tag_{i}", true);
                SetLabel($"affix_tag_{i}", FormatSlotBadge(opt));

                if (label is not null)
                {
                    label.Visible = true;
                    label.Text = FormatCardAffixLine(opt);
                }

                SetVisible($"affix_state_{i}", true);
                SetLabel($"affix_state_{i}", FormatStateBadge(opt, isSelected, isImbue));
            }
            else
            {
                if (rowFrame is not null)
                    rowFrame.Visible = false;
                if (btn is not null)
                    btn.Visible = false;
                SetVisible($"affix_sel_bg_{i}", false);
                SetVisible($"affix_sel_border_{i}", false);
                SetVisible($"affix_lock_bg_{i}", false);
                SetVisible($"affix_tag_{i}", false);
                if (label is not null)
                    label.Visible = false;
                SetVisible($"affix_state_{i}", false);
            }
        }

        _window.SetProperty("siphon_selected_index", _selectedAffixIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));

        SiphonAffixOption? selected = affixes.FirstOrDefault(a => a.Index == _selectedAffixIndex);
        if (selected is not null)
        {
            if (_activePicker.Mode == SiphonPickerMode.Steal)
            {
                string chosenShort = TruncateStatusText(selected.StatText, 54);
                UpdateStatusText($"Siphon [{selected.SlotTag}] {chosenShort}  ->  Charged Orb");
            }
            else
            {
                string chosenShort = TruncateStatusText(selected.StatText, 36);
                string storedShort = TruncateStatusText(_activePicker.StoredAffix?.StatText ?? "stored modifier", 36);
                UpdateStatusText($"Replace {chosenShort}  ->  {storedShort}");
            }
        }
        else
        {
            UpdateStatusText(_activePicker.Mode == SiphonPickerMode.Imbue
                ? "No compatible modifiers on this item can be replaced by the stored modifier."
                : "Click a modifier on the item card, then click Siphon Modifier.");
        }
    }

    private void HideWindow()
    {
        _activePicker = null;
        _selectedAffixIndex = -1;
        if (_window is { IsOpen: true } window)
            window.Visible = false;
    }

    private void SetLabel(string id, string text)
    {
        if (_window is { IsOpen: true } window && window.Find<ILabel>(id) is { } label)
            label.Text = text;
    }

    private void SetVisible(string id, bool visible)
    {
        if (_window is { IsOpen: true } window && window.Find<IUiControl>(id) is { } control)
            control.Visible = visible;
    }

    private void UpdateStatusText(string text) => SetLabel("status_label", text);

    private static string FormatSlotBadge(SiphonAffixOption opt)
    {
        if (!opt.Selectable)
            return "LOCKED";

        string tag = opt.IsPrefix ? "PREFIX" : opt.IsSuffix ? "SUFFIX" : "MOD";
        return opt.Tier > 0 ? $"{tag}  T{opt.Tier}" : tag;
    }

    private static string FormatStateBadge(SiphonAffixOption opt, bool isSelected, bool isImbue)
    {
        if (!opt.Selectable)
            return "LOCKED";
        if (isSelected)
            return isImbue ? "REPLACE" : "SELECTED";
        if (string.IsNullOrWhiteSpace(opt.DisplayName))
            return string.Empty;
        return opt.DisplayName.Length <= 14 ? opt.DisplayName : opt.DisplayName[..13] + ".";
    }

    private static string FormatCardAffixLine(SiphonAffixOption opt)
    {
        if (!opt.Selectable)
        {
            string reason = ShortenDisabledReason(opt.DisabledReason);
            return $"{opt.StatText}  ({reason})";
        }

        return opt.StatText;
    }

    private static string ShortenDisabledReason(string? disabledReason)
    {
        if (string.IsNullOrWhiteSpace(disabledReason))
            return "Incompatible";
        if (disabledReason.Contains("Fractured", StringComparison.OrdinalIgnoreCase))
            return "Fractured";
        if (disabledReason.Contains("already has", StringComparison.OrdinalIgnoreCase) ||
            disabledReason.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
            disabledReason.Contains("group", StringComparison.OrdinalIgnoreCase))
            return "Duplicate group";
        return disabledReason.Length <= 26 ? disabledReason : disabledReason[..25] + ".";
    }

    private static string FormatStoredAffixLine(SiphonStoredAffixDto stored)
    {
        string namePart = string.IsNullOrWhiteSpace(stored.DisplayName) ? string.Empty : $"  ({stored.DisplayName})";
        return $"{stored.StatText}{namePart}";
    }

    private static string TruncateStatusText(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxChars)
            return text;
        return text[..(maxChars - 3)].TrimEnd() + "...";
    }
}
