using System.Globalization;
using System.Text;
using DualMod.Phase0E2E.Shared;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;

namespace DualMod.Phase0E2E.Client;

/// <summary>
/// Per-area ruleset registered by <see cref="Phase0ClientMod"/> on slot <see cref="Phase0Protocol.RulesetId"/>.
/// Verifies <c>IsOnGameThread == true</c> inside <see cref="OnRulesetUpdate"/>.
/// </summary>
public sealed class Phase0Ruleset : IClientRuleset
{
    internal static IModScheduler? ActiveScheduler { get; set; }
    internal static bool? LastRulesetRxOnGameThread { get; private set; }

    public byte RulesetId => Phase0Protocol.RulesetId;
    public string Name => "Phase0E2ERuleset";
    public int UpdatesReceived { get; private set; }

    public void OnAreaEntered(AreaEventArgs area) { }

    public void OnRulesetUpdate(ReadOnlySpan<byte> payload)
    {
        UpdatesReceived++;
        LastRulesetRxOnGameThread = ActiveScheduler?.IsOnGameThread;
    }

    public void OnFrame(float deltaSeconds) { }

    public void OnAreaLeft() { }
}

/// <summary>
/// Phase 0 (0.2.0) live verification client mod.
/// Exercises every new 0.2.0 SDK &amp; C ABI capability on live PoE 3.29.3d and emits
/// structured <c>[PHASE0-E2E] ASSERT &lt;E2E-XX&gt; PASS|FAIL ...</c> lines.
/// </summary>
public sealed class Phase0ClientMod : IClientMod
{
    private IClientContext? _context;
    private IUiWindow? _probeWindow;
    private IUiWindow? _childWindow;
    private Action<float>? _frameHandler;
    private bool _loadOnGameThread;
    private bool _frameOnGameThreadObserved;
    private bool _uiClickOnGameThreadObserved;
    private bool _probesStarted;
    private bool _uiProbesCompleted;
    private bool _clipboardProbeCompleted;
    private bool _fileProbesCompleted;
    private bool _rawNetSent;
    private bool _rawMemoryResolved;
    private bool _probeWindowClosedObserved;
    private bool _suppressChatObserved;
    private bool _allowChatObserved;
    private bool _suppressOnGameThread;
    private bool _allowOnGameThread;
    private bool _chatAssertionPassed;
    private bool _pongReceived;
    private Phase0Pong? _lastPong;
    private bool _rawPacketReceived;
    private byte[]? _rawPacketBytes;
    private bool _e2e16Emitted;

    public void Load(IClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        Phase0Ruleset.ActiveScheduler = context.Scheduler;
        _loadOnGameThread = context.Scheduler.IsOnGameThread;

        var game = context.Game;

        // 1. Verify E2E-01 immediately at Load (SdkVersion + game-thread deferral)
        EmitAssertion(
            "E2E-01",
            SdkVersion.Parsed >= new Version(0, 2, 0)
                && SdkVersion.Current.StartsWith("0.2.0", StringComparison.Ordinal)
                && _loadOnGameThread,
            $"sdk={SdkVersion.Current} parsed={SdkVersion.Parsed} isOnGameThread={_loadOnGameThread}");

        // 2. Register Phase0Ruleset (0xA4)
        context.TrackDisposable(game.Rulesets.Register<Phase0Ruleset>());

        // 3. Verify E2E-08: IOpcodeRegistry >= 0x02FC registration + < 0x02FC guard (kept alive until Unload)
        VerifyOpcodeRegistry();

        // 4. Register raw handler for E2E-07 (0x02FE)
        context.TrackDisposable(context.Net.OnRaw(Phase0Protocol.RawProbeOpcodeLoopback, bytes =>
        {
            _rawPacketReceived = true;
            _rawPacketBytes = bytes.ToArray();
            EmitAssertion(
                "E2E-07",
                context.Scheduler.IsOnGameThread && _rawPacketBytes.SequenceEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
                $"rawOnGameThread={context.Scheduler.IsOnGameThread} len={_rawPacketBytes.Length}");
        }));

        // 5. Subscribe Frame callback to verify IsOnGameThread == true during native frame
        _frameHandler = _ =>
        {
            if (!_frameOnGameThreadObserved)
            {
                _frameOnGameThreadObserved = context.Scheduler.IsOnGameThread;
                context.Logger.Info($"[PHASE0-E2E] FrameCallback IsOnGameThread={_frameOnGameThreadObserved}");
            }
        };
        game.Lifecycle.Frame += _frameHandler;

        // 6. Subscribe chat interceptor for E2E-11 (bool suppression: true for /e2e_suppress, false for /e2e_allow)
        context.TrackDisposable(game.Scripts.RegisterChatInterceptor((sender, text) =>
        {
            if (text.Contains(Phase0Protocol.SuppressChatPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _suppressChatObserved = true;
                _suppressOnGameThread = context.Scheduler.IsOnGameThread;
                TryEmitChatAssertionPass();
                return true;
            }

            if (text.Contains(Phase0Protocol.AllowChatPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _allowChatObserved = true;
                _allowOnGameThread = context.Scheduler.IsOnGameThread;
                TryEmitChatAssertionPass();
                return false;
            }

            return false;
        }));

        // 7. Subscribe ServerBridge typed messages
        context.TrackDisposable(context.Net.On<Phase0RunProbesRequest>(req =>
        {
            context.Logger.Info($"[PHASE0-E2E] Received Phase0RunProbesRequest suite={req.Suite} token={req.CorrelationToken}");
            switch (req.Suite.ToLowerInvariant())
            {
                case "run":
                    _probesStarted = true;
                    RunAllClientProbes(req.CorrelationToken);
                    break;
                case "close":
                    CloseProbeWindow();
                    break;
                case "raw":
                    _probesStarted = true;
                    RunRawMemoryAndHookChainProbe();
                    RunRawNetworkProbe();
                    break;
                case "item":
                    _probesStarted = true;
                    context.Net.Send(new Phase0ItemTestRequest(Phase0Protocol.ItemCustomNameProbe, true));
                    break;
                case "chat":
                    _probesStarted = true;
                    TryEmitChatAssertionPass();
                    break;
                case "finalize":
                    FinalizeLiveAssertions();
                    break;
            }
        }));

        context.TrackDisposable(context.Net.On<Phase0Pong>(pong =>
        {
            _pongReceived = true;
            _lastPong = pong;
            bool ok = pong.ClientReadyObserved && pong.HasPhase0Mod && pong.HasMaxwellMod && pong.HasSiphonMod;
            EmitAssertion(
                "E2E-12",
                ok,
                $"token={pong.CorrelationToken} ready={pong.ClientReadyObserved} phase0={pong.HasPhase0Mod} maxwell={pong.HasMaxwellMod} siphon={pong.HasSiphonMod}");
        }));

        context.TrackDisposable(context.Net.On<Phase0ItemTestResult>(res =>
        {
            EmitAssertion("E2E-15", res.Passed, $"observed='{res.ObservedName}' cleared={res.ClearedCleanly} {res.Detail}");
        }));

        context.TrackDisposable(context.Net.On<Phase0ServerEventNotice>(notice =>
        {
            context.Logger.Info($"[PHASE0-E2E] ServerEventNotice event={notice.EventName} detail={notice.Detail}");
        }));
    }

    public void Unload()
    {
        if (_frameHandler is not null && _context is not null)
        {
            _context.Game.Lifecycle.Frame -= _frameHandler;
            _frameHandler = null;
        }

        if (_probesStarted && !_e2e16Emitted)
        {
            FinalizeLiveAssertions();
        }

        _childWindow?.Close();
        _childWindow = null;
        CloseProbeWindow();
        Phase0Ruleset.ActiveScheduler = null;
    }

    private void RunAllClientProbes(string correlationToken)
    {
        if (_context is null)
        {
            return;
        }

        // E2E-02, E2E-03, E2E-05, E2E-06: UI Window, TextBox, CheckBox, Rect/Size/Pos/Enabled, CreateWindowInto/RemoveChild/Destroy/Closed
        RunUiProbes();

        // E2E-04: Win32 Clipboard CF_UNICODETEXT
        RunClipboardProbe();

        // E2E-09 & E2E-10: ReadStockGameFile, InvalidateCachedAsset, BOM-free UTF-16LE .sm/.mat/.amd
        RunFileAndBundleProbes();

        // E2E-07 & RawMemory address resolution probe
        RunRawMemoryAndHookChainProbe();
        RunRawNetworkProbe();

        // E2E-12: Send ping to server
        _context.Net.Send(new Phase0Ping(
            CorrelationToken: correlationToken,
            ClientSdkVersion: SdkVersion.Current,
            NativeAbiVersion: 0x00000200u,
            IsOnGameThread: _context.Scheduler.IsOnGameThread));
    }

    private void RunUiProbes()
    {
        if (_context is null)
        {
            return;
        }

        CloseProbeWindow();

        var ui = _context.Game.Ui;
        _probeWindow = ui.Load(Phase0Protocol.ProbeWindowUiPath, "phase0_probe_window");
        _probeWindowClosedObserved = false;
        _probeWindow.Closed += () => _probeWindowClosedObserved = true;
        _probeWindow.Visible = true;

        var runBtn = _probeWindow.Require<IButton>("probe_run_button");
        runBtn.Clicked += () => _uiClickOnGameThreadObserved = _context.Scheduler.IsOnGameThread;
        runBtn.Click();

        // E2E-02: ITextBox (+0x338, TextChanged, Submitted, MaxLength)
        var textBox = _probeWindow.Require<ITextBox>("probe_textbox");
        string? changedText = null;
        string? submittedText = null;
        textBox.TextChanged += txt => changedText = txt;
        textBox.Submitted += txt => submittedText = txt;
        textBox.MaxLength = 16;
        textBox.Text = "Phase0_Hello";
        string readBack = textBox.Text;
        textBox.Submit();

        textBox.Text = "0123456789ABCDEF_OVERFLOW";
        bool clamped = textBox.Text.Length <= 16;

        EmitAssertion(
            "E2E-02",
            readBack == "Phase0_Hello" && changedText == "Phase0_Hello" && submittedText == "Phase0_Hello" && clamped,
            $"read='{readBack}' changed='{changedText}' submitted='{submittedText}' clamped={clamped}");

        // E2E-03: ICheckBox (CheckedChanged)
        var checkBox = _probeWindow.Require<ICheckBox>("probe_checkbox");
        var checkTransitions = new List<bool>();
        checkBox.CheckedChanged += val => checkTransitions.Add(val);
        checkBox.IsChecked = true;
        bool stateTrue = checkBox.IsChecked;
        checkBox.IsChecked = false;
        bool stateFalse = checkBox.IsChecked;

        EmitAssertion(
            "E2E-03",
            stateTrue && !stateFalse && checkTransitions.SequenceEqual([true, false]),
            $"states=[{stateTrue},{stateFalse}] events=[{string.Join(',', checkTransitions)}]");

        // E2E-05: Rect / SetPosition / SetSize / IsEnabled
        var slotHost = _probeWindow.Require<IUiControl>("probe_slot_host");
        slotHost.SetPosition(120f, 140f);
        slotHost.SetSize(320f, 180f);
        var rect = slotHost.Rect;
        slotHost.IsEnabled = false;
        bool disabledOk = !slotHost.IsEnabled;
        slotHost.IsEnabled = true;
        bool enabledOk = slotHost.IsEnabled;

        bool rectOk = MathF.Abs(rect.X - 120f) <= 0.5f
            && MathF.Abs(rect.Y - 140f) <= 0.5f
            && MathF.Abs(rect.Width - 320f) <= 0.5f
            && MathF.Abs(rect.Height - 180f) <= 0.5f;

        EmitAssertion(
            "E2E-05",
            rectOk && disabledOk && enabledOk,
            string.Create(CultureInfo.InvariantCulture, $"rect=({rect.X},{rect.Y},{rect.Width},{rect.Height}) disabledOk={disabledOk} enabledOk={enabledOk} uiClickGameThread={_uiClickOnGameThreadObserved}"));

        // E2E-06: CreateWindowInto + RemoveChild + Destroy, plus IUiWindow.Closed on a dedicated disposable probe window,
        // keeping _probeWindow visible until /phase0 close (after 04-phase0-probe-ui screenshot).
        _childWindow = ui.CreateWindowInto(slotHost, Phase0Protocol.ChildCardUiPath);
        bool childPresent = slotHost.Children.Any(c => ReferenceEquals(c, _childWindow) || c.Id == _childWindow.Id);
        bool childClosed = false;
        _childWindow.Closed += () => childClosed = true;
        bool removed = slotHost.RemoveChild(_childWindow);
        _childWindow.Destroy();
        _childWindow = null;

        bool closeProbeFired = false;
        IUiWindow closeProbeWindow = ui.Load(Phase0Protocol.ChildCardUiPath, "phase0_close_probe");
        closeProbeWindow.Closed += () => closeProbeFired = true;
        closeProbeWindow.Close();

        EmitAssertion(
            "E2E-06",
            childPresent && removed && closeProbeFired,
            $"childPresent={childPresent} removed={removed} childClosed={childClosed} closeProbeFired={closeProbeFired}");

        _uiProbesCompleted = true;
    }

    private void CloseProbeWindow()
    {
        if (_probeWindow is null)
        {
            return;
        }

        _probeWindow.Close();
        _probeWindow = null;
        _context?.Logger.Info($"[PHASE0-E2E] Closed Phase0Probe.ui window (closedEventObserved={_probeWindowClosedObserved})");
    }

    private void RunClipboardProbe()
    {
        if (_context is null)
        {
            return;
        }

        _context.Game.Clipboard.SetText(Phase0Protocol.ClipboardProbeText);
        string? roundTrip = _context.Game.Clipboard.GetText();
        bool ok = string.Equals(roundTrip, Phase0Protocol.ClipboardProbeText, StringComparison.Ordinal);
        EmitAssertion("E2E-04", ok, $"expected='{Phase0Protocol.ClipboardProbeText}' actual='{roundTrip}'");
        _clipboardProbeCompleted = true;
    }

    private void RunFileAndBundleProbes()
    {
        if (_context is null)
        {
            return;
        }

        var files = _context.Game.Files;

        // E2E-09: ReadStockGameFile + InvalidateCachedAsset
        byte[]? stockPet = files.ReadStockGameFile(Phase0Protocol.StockPetDatPath);
        bool hasTrailer = stockPet is not null && stockPet.Length >= 8 && ContainsMagicTrailer(stockPet);
        files.InvalidateCachedAsset(Phase0Protocol.ProbeWindowUiPath);
        EmitAssertion(
            "E2E-09",
            hasTrailer,
            $"stockPetBytes={stockPet?.Length ?? 0} hasDat64Trailer={hasTrailer}");

        // E2E-10: BOM-free UTF-16LE .sm/.mat/.amd overlay readback
        bool smNoBom = CheckOverlayTextBomFree(files, Phase0Protocol.BomFreeSmPath, "SkinnedMeshData");
        bool matNoBom = CheckOverlayTextBomFree(files, Phase0Protocol.BomFreeMatPath, "defaultgraph");
        bool amdNoBom = CheckOverlayTextBomFree(files, Phase0Protocol.BomFreeAmdPath, "AnimationMetadata");
        EmitAssertion(
            "E2E-10",
            smNoBom && matNoBom && amdNoBom,
            $"smNoBom={smNoBom} matNoBom={matNoBom} amdNoBom={amdNoBom}");

        _fileProbesCompleted = true;
    }

    private static bool CheckOverlayTextBomFree(IFileOverlayService files, string virtualPath, string expectedSubstring)
    {
        byte[]? bytes = files.ReadAllBytes(virtualPath);
        if (bytes is null || bytes.Length < 4 || (bytes.Length & 1) != 0)
        {
            return false;
        }

        bool hasUtf16Bom = (bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF);
        bool hasUtf8Bom = bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (hasUtf16Bom || hasUtf8Bom)
        {
            return false;
        }

        // Verify UTF-16LE encoding (ASCII leading character has 0x00 high byte) so un-transcoded UTF-8 cannot pass.
        if (bytes[1] != 0x00)
        {
            return false;
        }

        string decoded = Encoding.Unicode.GetString(bytes);
        return decoded.Contains(expectedSubstring, StringComparison.Ordinal);
    }

    private static bool ContainsMagicTrailer(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> magic = [0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB];
        return data.IndexOf(magic) >= 0;
    }

    private void VerifyOpcodeRegistry()
    {
        if (_context is null)
        {
            return;
        }

        var opcodes = _context.Game.Opcodes;
        _context.TrackDisposable(opcodes.RegisterHandler(Phase0Protocol.RawProbeOpcodeA, _ => { }, Phase0Protocol.ModId));
        _context.TrackDisposable(opcodes.RegisterHandler(Phase0Protocol.RawProbeOpcodeB, _ => { }, Phase0Protocol.ModId));

        bool belowRejected = false;
        try
        {
            using var _ = opcodes.RegisterHandler(0x02FB, _ => { }, Phase0Protocol.ModId);
        }
        catch (ArgumentOutOfRangeException)
        {
            belowRejected = true;
        }
        catch (ArgumentException)
        {
            belowRejected = true;
        }

        EmitAssertion(
            "E2E-08",
            opcodes.IsRegistered(Phase0Protocol.RawProbeOpcodeA)
                && opcodes.IsRegistered(Phase0Protocol.RawProbeOpcodeB)
                && belowRejected,
            $"registeredA={opcodes.IsRegistered(Phase0Protocol.RawProbeOpcodeA)} registeredB={opcodes.IsRegistered(Phase0Protocol.RawProbeOpcodeB)} below0x02FCRejected={belowRejected}");
    }

    private void RunRawNetworkProbe()
    {
        if (_context is null)
        {
            return;
        }

        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        _context.Net.SendRaw(Phase0Protocol.RawProbeOpcodeLoopback, payload);
        _rawNetSent = true;
        if (!_rawPacketReceived)
        {
            _context.Logger.Info("[PHASE0-E2E] SendRaw(0x02FE) dispatched to native socket; awaiting server loopback echo.");
        }
    }

    private void RunRawMemoryAndHookChainProbe()
    {
        if (_context is null)
        {
            return;
        }

        // Multi-mod IRawMemory pre/post hook chaining, independent unhooking, vtable slotIndex, and 8-arg/float Call
        // are tested deterministically in C++ ctest, Wine smoke, and M2 host tests to avoid detouring live engine
        // functions with synthetic probe signatures; here we verify live symbol resolution of files.hash_to_wstring_view.
        var raw = _context.Raw;
        nint probeAddr = raw.GetAddress("files.hash_to_wstring_view");
        _rawMemoryResolved = probeAddr != 0;
        _context.Logger.Info($"[PHASE0-E2E] Resolved files.hash_to_wstring_view=0x{probeAddr:X} resolved={_rawMemoryResolved}");
    }

    private void TryEmitChatAssertionPass()
    {
        if (_context is null || _chatAssertionPassed)
        {
            return;
        }

        if (_suppressChatObserved && _allowChatObserved && _suppressOnGameThread && _allowOnGameThread)
        {
            _chatAssertionPassed = true;
            EmitAssertion(
                "E2E-11",
                true,
                $"suppress={_suppressChatObserved} allow={_allowChatObserved} suppressOnGameThread={_suppressOnGameThread} allowOnGameThread={_allowOnGameThread}");
        }
    }

    private void FinalizeLiveAssertions()
    {
        if (_context is null)
        {
            return;
        }

        CloseProbeWindow();

        if (!_rawPacketReceived)
        {
            EmitAssertion("E2E-07", false, $"rawNetSent={_rawNetSent} rawPacketReceived=false");
        }

        if (!_chatAssertionPassed)
        {
            EmitAssertion(
                "E2E-11",
                false,
                $"suppress={_suppressChatObserved} allow={_allowChatObserved} suppressOnGameThread={_suppressOnGameThread} allowOnGameThread={_allowOnGameThread}");
        }

        if (!_e2e16Emitted)
        {
            _e2e16Emitted = true;
            bool onGameThread = _context.Scheduler.IsOnGameThread;
            bool coLoaded = _lastPong is { ClientReadyObserved: true, HasPhase0Mod: true, HasMaxwellMod: true, HasSiphonMod: true };
            bool ok = _loadOnGameThread
                && _frameOnGameThreadObserved
                && _uiProbesCompleted
                && _clipboardProbeCompleted
                && _fileProbesCompleted
                && _rawNetSent
                && _rawMemoryResolved
                && _pongReceived
                && coLoaded
                && onGameThread;

            EmitAssertion(
                "E2E-16",
                ok,
                $"loadGameThread={_loadOnGameThread} frameGameThread={_frameOnGameThreadObserved} ui={_uiProbesCompleted} clip={_clipboardProbeCompleted} files={_fileProbesCompleted} rawNet={_rawNetSent} rawMem={_rawMemoryResolved} pong={_pongReceived} phase0={_lastPong?.HasPhase0Mod ?? false} maxwell={_lastPong?.HasMaxwellMod ?? false} siphon={_lastPong?.HasSiphonMod ?? false} finalizeGameThread={onGameThread}");
        }
    }

    private void EmitAssertion(string id, bool passed, string detail)
    {
        string line = Phase0Protocol.FormatAssertMarker(id, passed, detail);
        if (passed)
        {
            _context?.Logger.Info(line);
        }
        else
        {
            _context?.Logger.Error(line);
        }

        if (_pongReceived && _lastPong is not null)
        {
            _context?.Net.Send(new Phase0ProbeAssertionResult(id, passed, detail));
        }
    }
}
