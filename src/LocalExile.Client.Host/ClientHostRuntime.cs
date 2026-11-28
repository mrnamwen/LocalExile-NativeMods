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

    /// <summary>Optional path for the centralized keybind registry JSON file (defaults to <c>Mods/Client/keybinds.json</c>).</summary>
    public string? KeybindsPath { get; init; }

    /// <summary>Optional <c>Mods/Client</c> root directory used to auto-detect <c>.merged-cache/</c> at <c>t=0</c>.</summary>
    public string? ModsRootDirectory { get; init; }

    /// <summary>Optional explicit path to <c>Mods/Client/.merged-cache/</c> mounted at <c>t=0</c> before any mod loads.</summary>
    public string? MergedCacheDirectory { get; init; }

    /// <summary>Whether to automatically mount <c>.merged-cache/</c> at <c>t=0</c> during runtime construction (default <c>true</c>).</summary>
    public bool AutoMountMergedCache { get; init; } = true;

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
    private sealed class UiEventKeyComparer : IEqualityComparer<(string WindowId, string ControlPath, string EventName)>
    {
        public static readonly UiEventKeyComparer Instance = new();

        public bool Equals((string WindowId, string ControlPath, string EventName) x, (string WindowId, string ControlPath, string EventName) y) =>
            string.Equals(x.WindowId, y.WindowId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ControlPath, y.ControlPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.EventName, y.EventName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string WindowId, string ControlPath, string EventName) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.WindowId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ControlPath),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.EventName));
    }

    private readonly INativeBridge _bridge;
    private readonly ClientHostOptions _options;
    private readonly IDisposable _frameSub;
    private readonly INetChannel _systemChannel;
    private readonly List<IDisposable> _systemSubs = [];
    private readonly Dictionary<(string WindowId, string ControlPath, string EventName), Action> _subscribedUiEvents = new(UiEventKeyComparer.Instance);
    private IDisposable? _mergedCacheOverlayHandle;
    private bool _started;
    private bool _disposed;
    private long _frameCount;
    private long _lastAutoHandshakeTimestamp;
    private long _lastConnPollTimestamp;
    private long _disconnectedSinceTimestamp;
    private ulong _lastObservedConnectionToken;
    private ulong _handshakeConnectionToken;
    private bool _isLiveConnected;

    internal bool IsDisposed => Volatile.Read(ref _disposed);

    internal int SubscribedUiEventCount => _subscribedUiEvents.Count;

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

    /// <summary>System clipboard service.</summary>
    public ClipboardService Clipboard { get; }

    /// <summary>Client chat interceptor &amp; send service.</summary>
    public ChatService Chat { get; }

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

    /// <summary>In-game and pre-game screen/panel state and managed auto-flow toolbar slots.</summary>
    public ScreensService Screens { get; }

    /// <summary>Centralized keybind registry persisted to <c>Mods/Client/keybinds.json</c>.</summary>
    public KeybindRegistry Keybinds { get; }

    /// <summary>Low-level suppressible keyboard/mouse input service.</summary>
    public InputService Input { get; }

    /// <summary>Hovered UI control, hovered item, and cursor item inspection service.</summary>
    public HoverService Hover { get; }

    /// <summary>NativeMods-owned HUD toast notification overlay service.</summary>
    public ToastService Toasts { get; }

    /// <summary>Pre-game Login-TCP character creation hook service.</summary>
    public PreGameService PreGame { get; }

    /// <summary>Client item-filter visibility, style inspection, on-demand evaluation, and reload service.</summary>
    public ItemFilterService ItemFilter { get; }

    /// <summary>Live camera projection, world-to-screen transform, and per-mod zoom override service.</summary>
    public CameraService Camera { get; }

    /// <summary>Stock item tooltip augmentation and custom anchored tooltip overlay service.</summary>
    public TooltipService Tooltips { get; }

    /// <summary>Custom Ctrl+Click stash affinity router and stock affinity bitset service.</summary>
    public StashAffinityService StashAffinity { get; }

    /// <summary>Extended 32-bit stack deserialization, millions stack formatting, and per-entity stack override service.</summary>
    public StackExtensionService StackExtension { get; }

    /// <summary>Client-side Tier 1 ServerBridge 0.3.0/0.4.0/1.2.0 protocol dispatcher.</summary>
    public ServerBridgeTier1Handler Tier1Handler { get; }

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

    /// <summary>Result of the <c>t=0</c> <c>Mods/Client/.merged-cache/</c> auto-mount, if a merged cache was discovered and mounted.</summary>
    public MergedCacheMountResult? MergedCacheMount { get; private set; }

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
        UiControls = new UiControlRegistry(_bridge);
        Ui = new UiService(FileOverlayService, UiControls, _bridge);
        Ui.WindowTreeInvalidated += DetachWindowUiEvents;
        Ui.ControlSubtreeRemoved += DetachControlSubtreeUiEvents;
        Clipboard = new ClipboardService(_bridge);
        Chat = new ChatService(_bridge);
        GealBuiltins = new GealBuiltinRegistry(_bridge);
        Scripts = new ScriptService(_bridge, GealBuiltins, Chat);
        Audio = new AudioService(_bridge);
        Stats = new StatsService(_bridge);
        Lifecycle = new GameLifecycle(_bridge);
        Screens = new ScreensService(_bridge, Ui, FileOverlayService);
        Keybinds = new KeybindRegistry(_options.KeybindsPath, Log);
        Input = new InputService(_bridge, Keybinds);
        Hover = new HoverService(_bridge, Ui);
        Tooltips = new TooltipService(_bridge, Ui, FileOverlayService, Hover);
        Ui.AttachPhase4Services(Tooltips, Input);
        StashAffinity = new StashAffinityService(_bridge);
        StackExtension = new StackExtensionService(_bridge);
        Toasts = new ToastService(Ui, FileOverlayService, Screens);
        PreGame = new PreGameService(_bridge, Log);
        World = new WorldService(_bridge, Lifecycle);
        ItemFilter = new ItemFilterService(_bridge, World, Log);
        Camera = new CameraService(_bridge);
        Rulesets = new RulesetRegistry(_bridge);
        Opcodes = new OpcodeRegistry(_bridge);
        Content = new ContentRegistry(FileOverlayService);
        RawMemory = new RawMemoryService(Addresses, _bridge);

        // t=0: mount Mods/Client/.merged-cache/ BEFORE any mod loads or game .datc64 tables are read!
        if (_options.AutoMountMergedCache)
        {
            string? resolvedMergedCache = ResolveMergedCacheDirectory(_options);
            if (!string.IsNullOrWhiteSpace(resolvedMergedCache) && Directory.Exists(resolvedMergedCache))
            {
                MountMergedCache(resolvedMergedCache);
            }
        }

        IClientTransport transport = CreateTransport(_options.TransportKind, _options.HostRulesetId);
        Router = new ClientChannelRouter(_bridge, Opcodes, transport);
        Loader = new ClientModLoader(this, _options.ShadowCopyRoot, _options.MaxModFaults);

        _systemChannel = Router.CreateChannel(MessageCodec.SystemChannel);
        _systemSubs.Add(_systemChannel.On<ServerHandshake>(HandleServerHandshake));
        _systemSubs.Add(_systemChannel.On<ServerUiCommand>(HandleServerUiCommand));
        _systemSubs.Add(_bridge.RegisterAreaLifecycleCallback(HandleNativeAreaEntered, HandleNativeAreaLeft));
        Tier1Handler = new ServerBridgeTier1Handler(
            _systemChannel,
            Scheduler,
            Screens,
            Input,
            Hover,
            Toasts,
            World,
            Stats,
            Lifecycle,
            ItemFilter,
            Camera,
            Ui,
            Tooltips,
            StashAffinity,
            Audio,
            StackExtension,
            Log);

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

    /// <summary>
    /// Mounts (or remounts) a <c>Mods/Client/.merged-cache/</c> directory into <see cref="FileOverlayService"/>
    /// and loads its <c>content-row-map.json</c> into <see cref="Content"/>.
    /// </summary>
    public MergedCacheMountResult MountMergedCache(string mergedCacheDirectory, int priority = 500)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mergedCacheDirectory);
        Interlocked.Exchange(ref _mergedCacheOverlayHandle, null)?.Dispose();

        MergedCacheMountResult result = FileOverlayService.MountMergedCache(mergedCacheDirectory, Content, priority, Log);
        if (result.Mounted)
        {
            _mergedCacheOverlayHandle = result.OverlayHandle;
            MergedCacheMount = result;
        }
        else
        {
            MergedCacheMount = null;
        }

        return result;
    }

    private static string? ResolveMergedCacheDirectory(ClientHostOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.MergedCacheDirectory))
        {
            return options.MergedCacheDirectory;
        }

        if (!string.IsNullOrWhiteSpace(options.ModsRootDirectory))
        {
            return Path.Combine(options.ModsRootDirectory, ".merged-cache");
        }

        string? envCache = Environment.GetEnvironmentVariable("NATIVEMODS_MERGED_CACHE_DIR");
        if (!string.IsNullOrWhiteSpace(envCache))
        {
            return envCache;
        }

        string? envMods = Environment.GetEnvironmentVariable("NATIVEMODS_MODS_DIR");
        if (!string.IsNullOrWhiteSpace(envMods))
        {
            return Path.Combine(envMods, ".merged-cache");
        }

        return null;
    }

    /// <summary>Sends a <see cref="ClientHandshake"/> advertising the extender, SDK, client build, installed mods, and mounted content row map digest.</summary>
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
                "screens.slots",
                "input.keybinds",
                "hover.items",
                "toasts.hud",
                "pregame.options",
                "world.entities",
                "stats.live",
                "lifecycle.areas",
                "itemfilter.live",
                "camera.projection",
                "content.row-map",
                "ui.rich-controls",
                "ui.item-grid",
                "tooltips.augment",
                "stash.affinity",
                "geal.native-builtins",
                "rulesets.native",
                "ui.custom-controls",
                "stacks.u32",
                "ui.compact-sockets",
                "audio.spatial",
                "world.play-effect",
            ],
            ContentRowMapDigest: Content.ContentRowMapDigest);

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

    private void HandleNativeAreaEntered(AreaInfo area)
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        byte[] byteRulesets = [.. area.ActiveRulesetIds.Where(id => id <= byte.MaxValue).Select(id => (byte)id)];
        uint seed = area.AreaSeed != 0 ? area.AreaSeed : area.Hash16;
        var args = new AreaEventArgs(area.AreaId, seed, byteRulesets) { Area = area };
        Lifecycle.RaiseAreaEntered(args);
        Rulesets.NotifyAreaEntered(args);
    }

    private void HandleNativeAreaLeft(string areaId)
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        NotifyAreaLeft(areaId);
    }

    /// <summary>Notifies lifecycle and rulesets that the client left an area.</summary>
    public void NotifyAreaLeft(string areaId)
    {
        Rulesets.NotifyAreaLeft();
        Lifecycle.RaiseAreaLeft(areaId);
    }

    /// <summary>Notifies lifecycle that the client connected to an instance server.</summary>
    public void NotifyConnected() => Lifecycle.RaiseConnected();

    /// <summary>Notifies lifecycle that the client disconnected from an instance server.</summary>
    public void NotifyDisconnected(string? reason = null)
    {
        Tier1Handler.HandleDisconnected();
        StackExtension.HandleDisconnected();
        Toasts.HandleDisconnected();
        Tooltips.HandleDisconnected();
        Lifecycle.RaiseDisconnected(reason);
    }

    internal void Log(string level, string source, string message) =>
        _options.LogSink?.Invoke(level, source, message);

    private IClientTransport CreateTransport(NetTransportKind kind, byte rulesetId) => kind switch
    {
        NetTransportKind.HostRuleset => new HostRulesetTransport(_bridge, rulesetId),
        NetTransportKind.ChatFallback => new ChatFallbackTransport(_bridge),
        _ => new HostRulesetTransport(_bridge, rulesetId),
    };

    /// <summary>Work run once on the next managed frame, on the game thread (the native entry point loads the client
    /// mods there).</summary>
    public Action? FirstFrameWork { get; set; }

    // Automatic handshakes not answered on the current connection: the interval doubles from 1.5 s up to a minute, and
    // after MaxUnansweredHandshakes the host stops asking until the connection changes (a server without the
    // ServerBridge never answers, and resending forever cost a send and a log line every 1.5 s).
    private int _unansweredHandshakes;
    private ulong _unansweredForToken;
    private const int MaxUnansweredHandshakes = 8;

    private TimeSpan NextHandshakeInterval(TimeSpan first)
    {
        double seconds = first.TotalSeconds * Math.Pow(2, Math.Min(_unansweredHandshakes, 6));
        return TimeSpan.FromSeconds(Math.Min(seconds, 60));
    }

    private void OnFrame(float deltaSeconds)
    {
        long frame = Interlocked.Increment(ref _frameCount);
        if (FirstFrameWork is { } firstFrame)
        {
            FirstFrameWork = null;
            try { firstFrame(); }
            catch (Exception ex) { Log("ERROR", "host", $"First-frame work failed: {ex.GetType().Name}: {ex.Message}"); }
        }
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
                if (_unansweredForToken != _lastObservedConnectionToken)
                {
                    _unansweredForToken = _lastObservedConnectionToken;
                    _unansweredHandshakes = 0;
                }

                long lastTs = Interlocked.Read(ref _lastAutoHandshakeTimestamp);
                if (_unansweredHandshakes < MaxUnansweredHandshakes &&
                    (lastTs == 0 || Stopwatch.GetElapsedTime(lastTs) >= NextHandshakeInterval(TimeSpan.FromMilliseconds(1500))))
                {
                    Interlocked.Exchange(ref _lastAutoHandshakeTimestamp, Stopwatch.GetTimestamp());
                    if (lastTs != 0) _unansweredHandshakes++;
                    if (_unansweredHandshakes == MaxUnansweredHandshakes)
                    {
                        Log("INFO", "host", $"No ServerBridge answered {MaxUnansweredHandshakes} handshakes on this connection; not asking again until it changes.");
                    }
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
        else if (_bridge is NativeShimBridge && LastServerHandshake is null && frame >= 30 && _unansweredHandshakes < MaxUnansweredHandshakes)
        {
            // Fallback for minimal mock ABI tables that omit GetActiveConnectionToken.
            long lastTs = Interlocked.Read(ref _lastAutoHandshakeTimestamp);
            if (lastTs == 0 || Stopwatch.GetElapsedTime(lastTs) >= NextHandshakeInterval(TimeSpan.FromSeconds(2)))
            {
                Interlocked.Exchange(ref _lastAutoHandshakeTimestamp, Stopwatch.GetTimestamp());
                if (lastTs != 0) _unansweredHandshakes++;
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

        Screens.Tick();
        Hover.Tick();
        Toasts.Tick(deltaSeconds);
        Audio.TickSpatialChannels();
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

    internal void HandleServerUiCommand(ServerUiCommand cmd)
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
        string windowId = !string.IsNullOrWhiteSpace(cmd.WindowId)
            ? cmd.WindowId
            : (Ui.OpenWindows.FirstOrDefault()?.Id ?? string.Empty);

        var key = (windowId, controlPath, eventName);
        if (_subscribedUiEvents.ContainsKey(key))
        {
            return;
        }

        IUiControl? control = FindTargetControl(windowId, controlPath);
        if (control is null)
        {
            return;
        }

        Action? detach = null;
        if (control is IButton button && string.Equals(eventName, "Clicked", StringComparison.OrdinalIgnoreCase))
        {
            void OnClicked() => _systemChannel.Send(new ClientUiEventNotification(windowId, controlPath, "Clicked"));
            button.Clicked += OnClicked;
            detach = () => button.Clicked -= OnClicked;
        }
        else if (control is ICheckBox checkBox && string.Equals(eventName, "CheckedChanged", StringComparison.OrdinalIgnoreCase))
        {
            void OnChecked(bool isChecked) => _systemChannel.Send(new ClientUiEventNotification(windowId, controlPath, "CheckedChanged", isChecked ? "true" : "false"));
            checkBox.CheckedChanged += OnChecked;
            detach = () => checkBox.CheckedChanged -= OnChecked;
        }
        else if (control is ITextBox textBox && string.Equals(eventName, "Submitted", StringComparison.OrdinalIgnoreCase))
        {
            void OnSubmitted(string value) => _systemChannel.Send(new ClientUiEventNotification(windowId, controlPath, "Submitted", value));
            textBox.Submitted += OnSubmitted;
            detach = () => textBox.Submitted -= OnSubmitted;
        }
        else if (control is ITextBox textBox2 && string.Equals(eventName, "TextChanged", StringComparison.OrdinalIgnoreCase))
        {
            void OnTextChanged(string value) => _systemChannel.Send(new ClientUiEventNotification(windowId, controlPath, "TextChanged", value));
            textBox2.TextChanged += OnTextChanged;
            detach = () => textBox2.TextChanged -= OnTextChanged;
        }
        else if (control is IComboBox comboBox && string.Equals(eventName, "SelectionChanged", StringComparison.OrdinalIgnoreCase))
        {
            void OnSelectionChanged(int index) => _systemChannel.Send(new ClientUiEventNotification(windowId, controlPath, "SelectionChanged", index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            comboBox.SelectionChanged += OnSelectionChanged;
            detach = () => comboBox.SelectionChanged -= OnSelectionChanged;
        }
        else if (control is ITabContainer tabContainer && string.Equals(eventName, "ActiveTabChanged", StringComparison.OrdinalIgnoreCase))
        {
            void OnActiveTabChanged(int index) => _systemChannel.Send(new ClientUiEventNotification(windowId, controlPath, "ActiveTabChanged", index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            tabContainer.ActiveTabChanged += OnActiveTabChanged;
            detach = () => tabContainer.ActiveTabChanged -= OnActiveTabChanged;
        }

        if (detach is not null)
        {
            _subscribedUiEvents[key] = detach;
        }
    }

    private void DetachWindowUiEvents(string windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId) || _subscribedUiEvents.Count == 0)
        {
            return;
        }

        var keys = _subscribedUiEvents.Keys
            .Where(k => string.Equals(k.WindowId, windowId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var k in keys)
        {
            if (_subscribedUiEvents.Remove(k, out Action? detach))
            {
                detach();
            }
        }
    }

    private void DetachControlSubtreeUiEvents(string windowId, string controlPath)
    {
        if (string.IsNullOrWhiteSpace(windowId) || string.IsNullOrWhiteSpace(controlPath) || _subscribedUiEvents.Count == 0)
        {
            return;
        }

        string prefix = controlPath + ".";
        var keys = _subscribedUiEvents.Keys
            .Where(k => string.Equals(k.WindowId, windowId, StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(k.ControlPath, controlPath, StringComparison.OrdinalIgnoreCase) ||
                         k.ControlPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        foreach (var k in keys)
        {
            if (_subscribedUiEvents.Remove(k, out Action? detach))
            {
                detach();
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        Volatile.Write(ref _disposed, true);
        Ui.WindowTreeInvalidated -= DetachWindowUiEvents;
        Ui.ControlSubtreeRemoved -= DetachControlSubtreeUiEvents;

        foreach (Action detach in _subscribedUiEvents.Values.ToArray())
        {
            detach();
        }

        _subscribedUiEvents.Clear();

        foreach (IDisposable sub in _systemSubs)
        {
            sub.Dispose();
        }

        _systemSubs.Clear();
        _frameSub.Dispose();
        Interlocked.Exchange(ref _mergedCacheOverlayHandle, null)?.Dispose();
        Tier1Handler.Dispose();
        Loader.Dispose();
        StackExtension.Dispose();
        Ui.ResetCompactSockets();
        StashAffinity.Dispose();
        Tooltips.Dispose();
        Camera.Dispose();
        ItemFilter.Dispose();
        Toasts.Dispose();
        Screens.Dispose();
        Input.Dispose();
        PreGame.Dispose();
        Router.Dispose();
    }
}
