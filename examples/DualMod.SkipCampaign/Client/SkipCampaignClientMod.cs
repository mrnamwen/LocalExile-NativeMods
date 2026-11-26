using System.Globalization;
using DualMod.SkipCampaign.Shared;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;

namespace DualMod.SkipCampaign.Client;

/// <summary>
/// Client half of <c>DualMod.SkipCampaign</c> targeting <c>LocalExile.Client.Sdk</c> <c>0.3.0</c>.
/// Injects an opt-in <c>Skip Campaign</c> mode checkbox into every <c>0xdc35</c> league card via
/// <see cref="ScreenSlot.LeagueCardModes"/>, keeps a synchronized mirror checkbox in
/// <see cref="ScreenSlot.CreateCharacterBar"/>, tags outgoing <c>C-&gt;S 0x12</c> (<c>CreateCharacter.S78</c>)
/// via <see cref="IPreGameService.CharacterCreating"/>, resets cleanly on creation success while
/// preserving state on failure, and displays a HUD toast via <see cref="IToastService"/> on first arrival.
/// </summary>
public sealed class SkipCampaignClientMod : IClientMod
{
    private const string EmbeddedCreateCharacterBarUi = """
        // DualMod.SkipCampaign — CreateCharacterBar mirror toggle fallback.

        <DEFINITIONS>

        begin Frame
        	id = skip_campaign_bar;
        	size = 320, 36;
        	visible = true;
        	mouse_enabled = true;

        	begin CheckBox
        		id = skip_campaign_checkbox;
        		location = 6, 4;
        		size = 180, 28;
        		text = "Skip Campaign";
        		checked = false;
        		enabled = true;
        		visible = true;
        		tooltip = "Skip Acts 1-10 and start directly in Karui Shores.";
        	end

        	begin Label
        		id = skip_campaign_status;
        		location = 192, 6;
        		size = 122, 24;
        		auto_size = false;
        		text_align = MiddleLeft;
        		foreground_colour = rgb( 185, 178, 162 );
        		text = "Normal Start";
        	end
        end
        """;

    private readonly Dictionary<string, bool> _optInByLeague = new(StringComparer.OrdinalIgnoreCase);

    private IClientContext? _context;
    private ILeagueModeRegistration? _leagueModeReg;
    private ISlotAttachment? _barAttachment;
    private IUiControl? _boundBarControl;
    private ICheckBox? _mirrorCheckBox;
    private ILabel? _mirrorStatusLabel;
    private string? _activeLeague;
    private bool _globalOptIn;
    private bool _hasPendingMirrorBroadcast;
    private bool _syncingUi;
    private bool _unloading;

    /// <summary>Live <see cref="ScreenSlot.LeagueCardModes"/> registration handle.</summary>
    public ILeagueModeRegistration? LeagueModeRegistration => _leagueModeReg;

    /// <summary>Live <see cref="ScreenSlot.CreateCharacterBar"/> mirror slot attachment handle.</summary>
    public ISlotAttachment? CreateCharacterBarAttachment => _barAttachment;

    /// <summary>Currently active/most-recently-interacted league id, or <c>null</c> if none selected yet.</summary>
    public string? ActiveLeague => _activeLeague;

    /// <summary>Most recently received <see cref="SkipCampaignAppliedNotice"/> from the server.</summary>
    public SkipCampaignAppliedNotice? LastAppliedNotice { get; private set; }

    /// <summary>Toast identifier returned by <see cref="IToastService.Show"/> for the latest arrival notice.</summary>
    public ulong? LastToastId { get; private set; }

    /// <summary>Number of times a failed character creation preserved the player's opt-in state.</summary>
    public int PreservedOnFailureCount { get; private set; }

    /// <summary>Returns whether Skip Campaign is currently enabled for <paramref name="leagueId"/>.</summary>
    public bool IsOptInEnabled(string? leagueId = null)
    {
        if (!string.IsNullOrWhiteSpace(leagueId))
        {
            string trimmed = leagueId.Trim();
            if (_optInByLeague.TryGetValue(trimmed, out bool perLeague))
            {
                return perLeague;
            }

            // Support PoE 3.29 mode variants (e.g. "SSF Standard", "Hardcore Standard") inheriting from their base card.
            string stripped = StripModePrefixes(trimmed);
            if (!string.Equals(stripped, trimmed, StringComparison.OrdinalIgnoreCase) &&
                _optInByLeague.TryGetValue(stripped, out bool baseLeague))
            {
                return baseLeague;
            }

            if (_optInByLeague.Count > 0)
            {
                return false;
            }
        }

        return _globalOptIn;
    }

    /// <inheritdoc/>
    public void Load(IClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _unloading = false;
        _context = context;
        IGameServices game = context.Game;

        // 1. Ensure CreateCharacterBar mirror .ui layout is always resolvable even without loose files on disk.
        if (!game.Files.Exists(SkipCampaignProtocol.CreateCharacterBarUiPath))
        {
            context.TrackDisposable(game.Files.RegisterVirtualTextFile(
                SkipCampaignProtocol.CreateCharacterBarUiPath,
                EmbeddedCreateCharacterBarUi,
                priority: 50));
        }

        // 2. Register opt-in "Skip Campaign" checkbox in ScreenSlot.LeagueCardModes (auto-reinjected on every LeagueOverlay::Fill).
        _leagueModeReg = game.Screens.RegisterLeagueCardMode(
            new LeagueModeCheckboxOptions(
                Id: SkipCampaignProtocol.OptionKey,
                Caption: SkipCampaignProtocol.CheckboxCaption,
                DefaultChecked: false,
                Order: 10,
                Tooltip: "Start directly in Karui Shores at endgame level with campaign quest flags and waypoints applied.",
                NaturalWidth: 230f),
            OnLeagueCardModeChanged);
        context.TrackDisposable(_leagueModeReg);

        // 3. Register synchronized mirror checkbox/indicator in ScreenSlot.CreateCharacterBar.
        _barAttachment = game.Screens.RegisterSlot(
            ScreenSlot.CreateCharacterBar,
            SkipCampaignProtocol.CreateCharacterBarSlotId,
            SkipCampaignProtocol.CreateCharacterBarUiPath,
            new SlotAttachmentOptions(
                Order: 10,
                Width: 320f,
                Height: 36f,
                Spacing: 8f,
                Tooltip: "Skip Campaign (Acts 1-10)"));
        context.TrackDisposable(_barAttachment);

        // 4. Hook pre-game screens, C->S 0x12 creation options, S->C 0x13 creation result, and frame binding sync.
        game.Screens.PreGameScreenChanged += OnPreGameScreenChanged;
        game.Screens.LeagueOverlayFilled += OnLeagueOverlayFilled;
        game.PreGame.CharacterCreating += OnCharacterCreating;
        game.PreGame.CharacterCreated += OnCharacterCreated;
        game.Lifecycle.Frame += OnFrame;
        game.Lifecycle.Disconnected += OnDisconnected;

        // 5. Subscribe to ServerBridge first-arrival toast notice, marshaled onto the game thread.
        context.TrackDisposable(context.Net.On<SkipCampaignAppliedNotice>(
            notice => DispatchOnGameThread(() => HandleAppliedNotice(notice))));

        // Bind immediately if CreateCharacter screen is already active.
        BindAndSyncMirrorControl();
    }

    /// <inheritdoc/>
    public void Unload()
    {
        _unloading = true;

        if (_context is not null)
        {
            IGameServices game = _context.Game;
            game.Screens.PreGameScreenChanged -= OnPreGameScreenChanged;
            game.Screens.LeagueOverlayFilled -= OnLeagueOverlayFilled;
            game.PreGame.CharacterCreating -= OnCharacterCreating;
            game.PreGame.CharacterCreated -= OnCharacterCreated;
            game.Lifecycle.Frame -= OnFrame;
            game.Lifecycle.Disconnected -= OnDisconnected;
        }

        UnbindMirrorControl();

        if (_leagueModeReg is not null)
        {
            _leagueModeReg.Dispose();
            _leagueModeReg = null;
        }

        if (_barAttachment is not null)
        {
            _barAttachment.Dispose();
            _barAttachment = null;
        }

        _optInByLeague.Clear();
        _activeLeague = null;
        _globalOptIn = false;
        _hasPendingMirrorBroadcast = false;
        _syncingUi = false;
        _context = null;
    }

    private void OnLeagueCardModeChanged(string leagueId, bool isChecked)
    {
        if (_unloading || _syncingUi || string.IsNullOrWhiteSpace(leagueId))
        {
            return;
        }

        string normalized = leagueId.Trim();
        _optInByLeague[normalized] = isChecked;
        _activeLeague = normalized;
        _globalOptIn = isChecked;
        _hasPendingMirrorBroadcast = false;

        SyncMirrorControlState();
    }

    private void OnLeagueOverlayFilled(LeagueOverlayFilledEventArgs args)
    {
        if (_unloading || args.Cards.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_activeLeague) ||
            !args.Cards.Any(c => string.Equals(c.LeagueId, _activeLeague, StringComparison.OrdinalIgnoreCase)))
        {
            _activeLeague = args.Cards[0].LeagueId;
        }

        bool broadcastMirror = _hasPendingMirrorBroadcast;
        _hasPendingMirrorBroadcast = false;
        _syncingUi = true;
        try
        {
            foreach (LeagueCardInfo card in args.Cards)
            {
                if (!_optInByLeague.ContainsKey(card.LeagueId))
                {
                    bool initial = broadcastMirror ? _globalOptIn : false;
                    _optInByLeague[card.LeagueId] = initial;
                    if (broadcastMirror && _leagueModeReg is not null)
                    {
                        _leagueModeReg.SetChecked(card.LeagueId, initial);
                    }
                }
            }
        }
        finally
        {
            _syncingUi = false;
        }

        SyncMirrorControlState();
    }

    private void OnPreGameScreenChanged(PreGameScreenChangedEventArgs args)
    {
        if (_unloading)
        {
            return;
        }

        if (args.Current == PreGameScreen.CreateCharacter)
        {
            BindAndSyncMirrorControl();
        }
        else if (args.Previous == PreGameScreen.CreateCharacter)
        {
            UnbindMirrorControl();
        }
    }

    private void OnFrame(float _)
    {
        if (_unloading)
        {
            return;
        }

        IUiControl? currentControl = _barAttachment is { IsAttached: true } ? _barAttachment.Control : null;
        if (!ReferenceEquals(currentControl, _boundBarControl))
        {
            BindAndSyncMirrorControl();
        }
    }

    private void OnCharacterCreating(CharacterCreateContext ctx)
    {
        if (_unloading || ctx is null)
        {
            return;
        }

        // Ensure we reflect any pending mirror checkbox binding before reading opt-in state.
        IUiControl? currentControl = _barAttachment is { IsAttached: true } ? _barAttachment.Control : null;
        if (!ReferenceEquals(currentControl, _boundBarControl))
        {
            BindAndSyncMirrorControl();
        }

        if (IsOptInEnabled(ctx.League))
        {
            ctx.SetOption(SkipCampaignProtocol.OptionKey, SkipCampaignProtocol.OptionEnabledValue);
        }
    }

    private void OnCharacterCreated(CharacterCreatedEventArgs args)
    {
        if (_unloading || args is null)
        {
            return;
        }

        if (!args.Succeeded)
        {
            // Preserve opt-in state on failure (e.g. name already taken) so the player does not have to re-check.
            PreservedOnFailureCount++;
            return;
        }

        ResetAllToggles();
    }

    private void OnDisconnected(string? _) => DispatchOnGameThread(() =>
    {
        if (_unloading)
        {
            return;
        }

        UnbindMirrorControl();
    });

    private void HandleAppliedNotice(SkipCampaignAppliedNotice notice)
    {
        if (_unloading || _context is null || notice is null)
        {
            return;
        }

        LastAppliedNotice = notice;
        string townName = FormatAreaDisplayName(notice.StartingAreaId);
        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"Lv.{notice.StartingLevel} in {townName} — {notice.WaypointsGranted} waypoints, {notice.RefundPoints} refund pts, {notice.AscendancyPointsEarned} ascendancy pts, {notice.GoldGranted:N0} gold.");
        LastToastId = _context.Game.Toasts.Show(
            "Campaign Skipped",
            message,
            ToastSeverity.Success,
            TimeSpan.FromSeconds(6));
    }

    private void BindAndSyncMirrorControl()
    {
        IUiControl? control = _barAttachment is { IsAttached: true } ? _barAttachment.Control : null;
        if (ReferenceEquals(control, _boundBarControl))
        {
            SyncMirrorControlState();
            return;
        }

        UnbindMirrorControl();
        if (control is null)
        {
            return;
        }

        _boundBarControl = control;
        _mirrorCheckBox = control.Find<ICheckBox>(SkipCampaignProtocol.CreateCharacterCheckBoxId);
        _mirrorStatusLabel = control.Find<ILabel>(SkipCampaignProtocol.CreateCharacterStatusLabelId);

        if (_mirrorCheckBox is not null)
        {
            _mirrorCheckBox.CheckedChanged += OnMirrorCheckBoxChanged;
        }

        SyncMirrorControlState();
    }

    private void UnbindMirrorControl()
    {
        if (_mirrorCheckBox is not null)
        {
            _mirrorCheckBox.CheckedChanged -= OnMirrorCheckBoxChanged;
            _mirrorCheckBox = null;
        }

        _mirrorStatusLabel = null;
        _boundBarControl = null;
    }

    private void OnMirrorCheckBoxChanged(bool isChecked)
    {
        if (_unloading || _syncingUi)
        {
            return;
        }

        _globalOptIn = isChecked;
        if (!string.IsNullOrWhiteSpace(_activeLeague))
        {
            _optInByLeague[_activeLeague] = isChecked;
            if (_leagueModeReg is not null)
            {
                _syncingUi = true;
                try
                {
                    _leagueModeReg.SetChecked(_activeLeague, isChecked);
                }
                finally
                {
                    _syncingUi = false;
                }
            }
        }
        else
        {
            _hasPendingMirrorBroadcast = true;
        }

        UpdateMirrorStatusLabel(isChecked);
    }

    private void SyncMirrorControlState()
    {
        bool checkedState = IsOptInEnabled(_activeLeague);
        if (_mirrorCheckBox is not null)
        {
            _syncingUi = true;
            try
            {
                if (_mirrorCheckBox.IsChecked != checkedState)
                {
                    _mirrorCheckBox.IsChecked = checkedState;
                }
            }
            finally
            {
                _syncingUi = false;
            }
        }

        UpdateMirrorStatusLabel(checkedState);
    }

    private void UpdateMirrorStatusLabel(bool enabled)
    {
        if (_mirrorStatusLabel is not null)
        {
            _mirrorStatusLabel.Text = enabled ? "Endgame Start" : "Normal Start";
        }
    }

    private void ResetAllToggles()
    {
        _globalOptIn = false;
        _hasPendingMirrorBroadcast = false;

        List<string> leaguesToClear = [.. _optInByLeague.Keys];
        if (!string.IsNullOrWhiteSpace(_activeLeague) &&
            !leaguesToClear.Contains(_activeLeague, StringComparer.OrdinalIgnoreCase))
        {
            leaguesToClear.Add(_activeLeague);
        }

        _syncingUi = true;
        try
        {
            foreach (string league in leaguesToClear)
            {
                _optInByLeague[league] = false;
                _leagueModeReg?.SetChecked(league, false);
            }
        }
        finally
        {
            _syncingUi = false;
        }

        SyncMirrorControlState();
    }

    private void DispatchOnGameThread(Action action)
    {
        IClientContext? context = _context;
        if (_unloading || context is null)
        {
            return;
        }

        if (context.Scheduler.IsOnGameThread)
        {
            action();
            return;
        }

        context.Scheduler.RunOnGameThread(() =>
        {
            if (_unloading || _context is null)
            {
                return;
            }

            action();
        });
    }

    private static string FormatAreaDisplayName(string areaId) =>
        string.Equals(areaId, SkipCampaignProtocol.DefaultStartingAreaId, StringComparison.OrdinalIgnoreCase)
            ? "Karui Shores"
            : areaId;

    private static string StripModePrefixes(string league)
    {
        string current = league;
        bool stripped;
        do
        {
            stripped = false;
            foreach (string prefix in new[] { "SSF ", "Hardcore ", "HC ", "Ruthless " })
            {
                if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    current = current[prefix.Length..].Trim();
                    stripped = true;
                }
            }
        }
        while (stripped);

        return current;
    }
}
