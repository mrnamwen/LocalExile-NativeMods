using System.Diagnostics;
using LocalExile.Client.Host.Addresses;
using LocalExile.Client.Host.Loading;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Host.Services;
using LocalExile.Client.Host.Transport;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.Sdk.Raw;

namespace LocalExile.Client.Host;

/// <summary>Configuration options for booting <see cref="ClientHostRuntime"/>.</summary>
public sealed record ClientHostOptions
{
    /// <summary>Extender semantic version advertised during handshake.</summary>
    public string ExtenderVersion { get; init; } = SdkVersion.Current;

    /// <summary>Preferred wire transport (defaults to <see cref="NetTransportKind.HostRuleset"/>).</summary>
    public NetTransportKind TransportKind { get; init; } = NetTransportKind.HostRuleset;

    /// <summary>Host ruleset slot id (<c>0x00..0xAE</c>) when using <see cref="NetTransportKind.HostRuleset"/>.</summary>
    public byte HostRulesetId { get; init; } = IRulesetRegistry.DefaultHostChannelRulesetId;

    /// <summary>Optional pre-loaded address library; when null, built from the active <see cref="INativeBridge"/>.</summary>
    public IAddressLibrary? AddressLibrary { get; init; }

    /// <summary>Maximum unhandled exceptions before a mod is automatically disabled by the fault wall.</summary>
    public int MaxModFaults { get; init; } = 3;

    /// <summary>Optional shadow-copy root directory for collectible mod loading.</summary>
    public string? ShadowCopyRoot { get; init; }

    /// <summary>Optional structured log sink <c>(level, source, message)</c>.</summary>
    public Action<string, string, string>? LogSink { get; init; }

    /// <summary>
    /// Debounce duration after an instance socket drops to <c>0</c> before raising <see cref="IGameLifecycle.Disconnected"/>,
    /// avoiding spurious disconnect events during rapid zone-transition socket handoffs.
    /// </summary>
    public TimeSpan DisconnectDebounce { get; init; } = TimeSpan.FromMilliseconds(1500);
}

/// <summary>
/// Core managed runtime booted by the native script extender shim (<c>LocalExile.ClientHost.dll</c>).
/// Coordinates the address library, engine services, script-extender registries, transport channels,
/// built-in server-driven UI handler, handshake negotiation, and collectible <see cref="ClientModLoader"/>.
/// </summary>
public sealed class ClientHostRuntime : IDisposable
{
    private readonly INativeBridge _bridge;
    private readonly ClientHostOptions _options;
    private readonly IDisposable _frameSub;
    private readonly INetChannel _systemChannel;
    private readonly List<IDisposable> _systemSubs = [];
    private readonly HashSet<(string WindowId, string ControlPath, string EventName)> _subscribedUiEvents = [];
    private bool _started;
    private bool _disposed;
    private long _frameCount;
    private long _lastAutoHandshakeTimestamp;
    private long _lastConnPollTimestamp;
    private long _disconnectedSinceTimestamp;
    private ulong _lastObservedConnectionToken;
    private ulong _handshakeConnectionToken;
    private bool _isLiveConnected;

    /// <summary>Total number of game frames processed by this host runtime.</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <summary>Underlying native shim or simulated bridge.</summary>
    public INativeBridge Bridge => _bridge;

    /// <summary>Active build-matched 3.29 address library.</summary>
    public IAddressLibrary Addresses { get; }

    /// <summary>Game-thread scheduler.</summary>
    public IModScheduler Scheduler { get; }

    /// <summary>File overlay service.</summary>
    public FileOverlayService FileOverlayService { get; }

    /// <summary>Custom UI control registry.</summary>
    public UiControlRegistry UiControls { get; }

    /// <summary>Native UI service.</summary>
    public UiService Ui { get; }

    /// <summary>Custom GEAL built-in registry.</summary>
    public GealBuiltinRegistry GealBuiltins { get; }

    /// <summary>GEAL script service.</summary>
    public ScriptService Scripts { get; }

    /// <summary>Audio playback service.</summary>
    public AudioService Audio { get; }

    /// <summary>Client stats service.</summary>
    public StatsService Stats { get; }

    /// <summary>Client lifecycle events.</summary>
    public GameLifecycle Lifecycle { get; }

    /// <summary>World object &amp; effect service.</summary>
    public WorldService World { get; }

    /// <summary>Custom ruleset registry.</summary>
    public RulesetRegistry Rulesets { get; }

    /// <summary>Custom opcode handler-group registry.</summary>
    public OpcodeRegistry Opcodes { get; }

    /// <summary>Append-only content registry.</summary>
    public ContentRegistry Content { get; }

    /// <summary>Raw memory service (only handed out to mods with <c>RawAccess = true</c>).</summary>
    public RawMemoryService RawMemory { get; }

    /// <summary>Channel router for mod and system envelopes.</summary>
    public ClientChannelRouter Router { get; }

    /// <summary>Collectible mod loader with hot reload and fault isolation.</summary>
    public ClientModLoader Loader { get; }

    /// <summary>Most recent <see cref="ServerHandshake"/> received from the LocalExile server, if negotiated.</summary>
    public ServerHandshake? LastServerHandshake { get; private set; }

    /// <summary>Raised when a <see cref="ServerHandshake"/> is received and processed.</summary>
    public event Action<ServerHandshake>? HandshakeCompleted;

    /// <summary>Creates a new <see cref="ClientHostRuntime"/> over <paramref name="bridge"/>.</summary>
    public ClientHostRuntime(INativeBridge bridge, ClientHostOptions? options = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _options = options ?? new ClientHostOptions();

        Addresses = _options.AddressLibrary ?? JsonAddressLibrary.FromBridge(_bridge);
        Scheduler = new GameThreadScheduler(_bridge);
        FileOverlayService = new FileOverlayService(_bridge);
        UiControls = new UiControlRegistry();
        Ui = new UiService(FileOverlayService, UiControls, _bridge);
        GealBuiltins = new GealBuiltinRegistry();
        Scripts = new ScriptService(_bridge, GealBuiltins);
        Audio = new AudioService(_bridge);
        Stats = new StatsService(_bridge);
        Lifecycle = new GameLifecycle();
        World = new WorldService(_bridge, Lifecycle);
        Rulesets = new RulesetRegistry(_bridge);
        Opcodes = new OpcodeRegistry(_bridge);
        Content = new ContentRegistry(FileOverlayService);
        RawMemory = new RawMemoryService(Addresses, _bridge);

        IClientTransport transport = CreateTransport(_options.TransportKind, _options.HostRulesetId);
        Router = new ClientChannelRouter(_bridge, Opcodes, transport);
        Loader = new ClientModLoader(this, _options.ShadowCopyRoot, _options.MaxModFaults);

        _systemChannel = Router.CreateChannel(MessageCodec.SystemChannel);
        _systemSubs.Add(_systemChannel.On<ServerHandshake>(HandleServerHandshake));
        _systemSubs.Add(_systemChannel.On<ServerUiCommand>(HandleServerUiCommand));

        // Passive S->C ##NM chat fallback listener when HostRuleset is primary so servers
        // sending either 0x2c9 ruleset packets OR ##NM chat lines reach client mods seamlessly.
        if (_options.TransportKind == NetTransportKind.HostRuleset)
        {
            _systemSubs.Add(_bridge.RegisterChatInterceptor((_, text) =>
            {
                if (Router.TransportInfo.Kind == NetTransportKind.ChatFallback)
                {
                    return false;
                }

                if (!MessageCodec.TryDecodeServerChatEnvelope(text, out ModEnvelope? env) || env is null)
                {
                    return false;
                }

                Router.DispatchEnvelope(env);
                return true;
            }));
        }

        _frameSub = _bridge.RegisterFrameCallback(OnFrame);
    }

    /// <summary>Starts the managed host runtime.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Log("INFO", "host", $"NativeMods managed host started (SDK {SdkVersion.Current}, build {Addresses.BuildVersion}, transport {Router.TransportInfo.Kind}).");
    }

    /// <summary>Sends a <see cref="ClientHandshake"/> advertising the extender, SDK, client build, and installed mods.</summary>
    public ClientHandshake SendHandshake()
    {
        var handshake = new ClientHandshake(
            ExtenderVersion: _options.ExtenderVersion,
            SdkVersion: SdkVersion.Current,
            Build: Addresses.Build,
            BuildVersion: Addresses.BuildVersion,
            ProtocolVersion: SdkVersion.ProtocolVersion,
            InstalledMods: Loader.GetInstalledModSummaries(),
            Capabilities:
            [
                "ui.server-driven",
                "files.overlay",
                "geal.builtins",
                "rulesets.0x2c9",
                "chat.fallback",
            ]);

        _systemChannel.Send(handshake);
        return handshake;
    }

    /// <summary>Switches the active wire transport at runtime.</summary>
    public void SwitchTransport(NetTransportKind kind, byte? rulesetId = null)
    {
        IClientTransport next = CreateTransport(kind, rulesetId ?? _options.HostRulesetId);
        Router.SwitchTransport(next);
    }

    /// <summary>Advances the host by one frame (draining game-thread queue, ticking mods, custom controls, rulesets, and lifecycle).</summary>
    public void TickFrame(float deltaSeconds = 1f / 60f)
    {
        if (_bridge is SimulatedNativeBridge sim)
        {
            sim.SimulateFrame(deltaSeconds);
            return;
        }

        _bridge.DrainGameThreadQueue(deltaSeconds);
        OnFrame(deltaSeconds);
    }

    /// <summary>Notifies lifecycle and rulesets that the client entered an area.</summary>
    public void NotifyAreaEntered(string areaId, uint seed = 0, IReadOnlyList<byte>? activeRulesets = null)
    {
        var args = new AreaEventArgs(areaId, seed, activeRulesets ?? []);
        Lifecycle.RaiseAreaEntered(args);
        Rulesets.NotifyAreaEntered(args);
    }

    /// <summary>Notifies lifecycle and rulesets that the client left an area.</summary>
    public void NotifyAreaLeft(string areaId)
    {
        Rulesets.NotifyAreaLeft();
        Lifecycle.RaiseAreaLeft(areaId);
    }

    /// <summary>Notifies lifecycle that the client connected to an instance server.</summary>
    public void NotifyConnected() => Lifecycle.RaiseConnected();

    /// <summary>Notifies lifecycle that the client disconnected.</summary>
    public void NotifyDisconnected(string? reason = null) => Lifecycle.RaiseDisconnected(reason);

    internal void Log(string level, string source, string message) =>
        _options.LogSink?.Invoke(level, source, message);

    private IClientTransport CreateTransport(NetTransportKind kind, byte rulesetId) => kind switch
    {
        NetTransportKind.HostRuleset => new HostRulesetTransport(_bridge, rulesetId),
        NetTransportKind.ChatFallback => new ChatFallbackTransport(_bridge),
        _ => new HostRulesetTransport(_bridge, rulesetId),
    };

    private void OnFrame(float deltaSeconds)
    {
        long frame = Interlocked.Increment(ref _frameCount);
        if (frame is 1 or 5 or 60 or 120 || frame % 300 == 0)
        {
            Log("INFO", "host", $"Managed frame tick #{frame} (dt={deltaSeconds:F4}s, loadedMods={Loader.LoadedMods.Count}).");
        }

        // Track live InstanceClient socket changes across area transitions (e.g. Lioneye's Watch -> The Coast)
        // and reconnects so every new Gateway TCP session immediately receives a fresh ClientHandshake.
        if (_bridge.SupportsConnectionToken)
        {
            bool shouldPoll = _bridge is not NativeShimBridge
                || _lastConnPollTimestamp == 0
                || Stopwatch.GetElapsedTime(_lastConnPollTimestamp) >= TimeSpan.FromMilliseconds(100);

            if (shouldPoll)
            {
                _lastConnPollTimestamp = Stopwatch.GetTimestamp();
                ulong currentToken = _bridge.GetActiveConnectionToken();

                if (currentToken != _lastObservedConnectionToken)
                {
                    ulong prevToken = _lastObservedConnectionToken;
                    _lastObservedConnectionToken = currentToken;

                    if (currentToken != 0)
                    {
                        _disconnectedSinceTimestamp = 0;
                        if (prevToken != 0 || LastServerHandshake is not null)
                        {
                            LastServerHandshake = null;
                            _handshakeConnectionToken = 0;
                            Interlocked.Exchange(ref _lastAutoHandshakeTimestamp, 0);
                            Log("INFO", "host", $"Detected new instance connection (token=0x{currentToken:X16}, prev=0x{prevToken:X16}); resetting handshake state for automatic re-handshake.");
                        }

                        if (!_isLiveConnected)
                        {
                            _isLiveConnected = true;
                            NotifyConnected();
                        }
                    }
                    else
                    {
                        if (LastServerHandshake is not null)
                        {
                            LastServerHandshake = null;
                            _handshakeConnectionToken = 0;
                            Interlocked.Exchange(ref _lastAutoHandshakeTimestamp, 0);
                        }

                        if (_isLiveConnected && _disconnectedSinceTimestamp == 0)
                        {
                            _disconnectedSinceTimestamp = Stopwatch.GetTimestamp();
                        }
                    }
                }

                if (_isLiveConnected && _lastObservedConnectionToken == 0 && _disconnectedSinceTimestamp != 0)
                {
                    if (_options.DisconnectDebounce <= TimeSpan.Zero ||
                        Stopwatch.GetElapsedTime(_disconnectedSinceTimestamp) >= _options.DisconnectDebounce)
                    {
                        _isLiveConnected = false;
                        _disconnectedSinceTimestamp = 0;
                        Log("INFO", "host", "Instance connection closed; notifying lifecycle listeners of disconnect.");
                        NotifyDisconnected("instance-disconnected");
                    }
                }
            }

            if (_lastObservedConnectionToken != 0 &&
                (LastServerHandshake is null || _handshakeConnectionToken != _lastObservedConnectionToken))
            {
                long lastTs = Interlocked.Read(ref _lastAutoHandshakeTimestamp);
                if (lastTs == 0 || Stopwatch.GetElapsedTime(lastTs) >= TimeSpan.FromMilliseconds(1500))
                {
                    Interlocked.Exchange(ref _lastAutoHandshakeTimestamp, Stopwatch.GetTimestamp());
                    try
                    {
                        SendHandshake();
                        Log("INFO", "host", $"Sent automatic live ClientHandshake on frame #{frame} (token=0x{_lastObservedConnectionToken:X16}, mods={Loader.LoadedMods.Count}).");
                    }
                    catch
                    {
                        // Socket may still be completing its initial TCP handshake; retry on next interval.
                    }
                }
            }
        }
        else if (_bridge is NativeShimBridge && LastServerHandshake is null && frame >= 30)
        {
            // Fallback for minimal mock ABI tables that omit GetActiveConnectionToken.
            long lastTs = Interlocked.Read(ref _lastAutoHandshakeTimestamp);
            if (lastTs == 0 || Stopwatch.GetElapsedTime(lastTs) >= TimeSpan.FromSeconds(2))
            {
                Interlocked.Exchange(ref _lastAutoHandshakeTimestamp, Stopwatch.GetTimestamp());
                try
                {
                    SendHandshake();
                    Log("INFO", "host", $"Sent automatic live ClientHandshake on frame #{frame} (mods={Loader.LoadedMods.Count}).");
                }
                catch
                {
                    // Expected before entering an instance (login / character select has no InstanceClient socket yet).
                }
            }
        }

        Lifecycle.RaiseFrame(deltaSeconds);
        Loader.TickAllMods(deltaSeconds);
        Ui.TickCustomControls(deltaSeconds);
        Rulesets.NotifyFrame(deltaSeconds);
    }

    private void HandleServerHandshake(ServerHandshake handshake)
    {
        LastServerHandshake = handshake;
        _handshakeConnectionToken = _lastObservedConnectionToken;
        Log("INFO", "host", $"Handshake acknowledged by server {handshake.ServerVersion} (preferred transport {handshake.PreferredTransport}, token=0x{_handshakeConnectionToken:X16}).");
        HandshakeCompleted?.Invoke(handshake);
    }

    private void HandleServerUiCommand(ServerUiCommand cmd)
    {
        switch (cmd.Kind)
        {
            case ServerUiCommandKind.OpenWindow:
                if (!string.IsNullOrWhiteSpace(cmd.VirtualPath))
                {
                    Ui.Load(cmd.VirtualPath, string.IsNullOrWhiteSpace(cmd.WindowId) ? null : cmd.WindowId);
                }
                break;

            case ServerUiCommandKind.CloseWindow:
                if (!string.IsNullOrWhiteSpace(cmd.WindowId))
                {
                    Ui.CloseWindow(cmd.WindowId);
                }
                break;

            case ServerUiCommandKind.ReloadWindow:
                if (!string.IsNullOrWhiteSpace(cmd.WindowId))
                {
                    Ui.ReloadWindow(cmd.WindowId);
                }
                break;

            case ServerUiCommandKind.SetText:
                if (FindTargetControl(cmd.WindowId, cmd.ControlPath) is { } textTarget)
                {
                    textTarget.Text = cmd.Value ?? string.Empty;
                }
                break;

            case ServerUiCommandKind.SetVisible:
                if (FindTargetControl(cmd.WindowId, cmd.ControlPath) is { } visTarget && cmd.BoolValue.HasValue)
                {
                    visTarget.Visible = cmd.BoolValue.Value;
                }
                break;

            case ServerUiCommandKind.SetProperty:
                if (FindTargetControl(cmd.WindowId, cmd.ControlPath) is { } propTarget && !string.IsNullOrWhiteSpace(cmd.PropertyName))
                {
                    propTarget.SetProperty(cmd.PropertyName, cmd.Value ?? string.Empty);
                }
                break;

            case ServerUiCommandKind.SubscribeEvent:
                WireServerUiEvent(cmd);
                break;

            case ServerUiCommandKind.PlayAudio:
                if (!string.IsNullOrWhiteSpace(cmd.Value))
                {
                    if (cmd.Value.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                    {
                        Audio.PlaySoundFile(cmd.Value);
                    }
                    else
                    {
                        Audio.PlayEvent(cmd.Value);
                    }
                }
                break;

            case ServerUiCommandKind.PlayEffect:
                if (!string.IsNullOrWhiteSpace(cmd.Value) && uint.TryParse(cmd.ControlPath, out uint objId))
                {
                    World.PlayEffect(objId, cmd.Value);
                }
                break;
        }
    }

    private IUiControl? FindTargetControl(string windowId, string? controlPath)
    {
        IUiWindow? window = !string.IsNullOrWhiteSpace(windowId)
            ? Ui.GetWindow(windowId)
            : Ui.OpenWindows.FirstOrDefault();

        if (window is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(controlPath) ? window : window.Find(controlPath);
    }

    private void WireServerUiEvent(ServerUiCommand cmd)
    {
        string eventName = cmd.EventName ?? "Clicked";
        string controlPath = cmd.ControlPath ?? string.Empty;
        var key = (cmd.WindowId, controlPath, eventName);
        if (!_subscribedUiEvents.Add(key))
        {
            return;
        }

        IUiControl? control = FindTargetControl(cmd.WindowId, controlPath);
        if (control is null)
        {
            _subscribedUiEvents.Remove(key);
            return;
        }

        if (control is IButton button && string.Equals(eventName, "Clicked", StringComparison.OrdinalIgnoreCase))
        {
            button.Clicked += () =>
                _systemChannel.Send(new ClientUiEventNotification(cmd.WindowId, controlPath, "Clicked"));
        }
        else if (control is ICheckBox checkBox && string.Equals(eventName, "CheckedChanged", StringComparison.OrdinalIgnoreCase))
        {
            checkBox.CheckedChanged += isChecked =>
                _systemChannel.Send(new ClientUiEventNotification(cmd.WindowId, controlPath, "CheckedChanged", isChecked ? "true" : "false"));
        }
        else if (control is ITextBox textBox && string.Equals(eventName, "Submitted", StringComparison.OrdinalIgnoreCase))
        {
            textBox.Submitted += value =>
                _systemChannel.Send(new ClientUiEventNotification(cmd.WindowId, controlPath, "Submitted", value));
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IDisposable sub in _systemSubs)
        {
            sub.Dispose();
        }

        _systemSubs.Clear();
        _frameSub.Dispose();
        Loader.Dispose();
        Router.Dispose();
    }
}
