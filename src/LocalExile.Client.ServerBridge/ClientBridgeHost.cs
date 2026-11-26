using System.Collections.Concurrent;
using System.Diagnostics;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;

namespace LocalExile.Client.ServerBridge;

/// <summary>
/// Gateway-side NativeMods bridge managing per-connection <see cref="IClientSession"/> state,
/// handshake negotiation, typed mod channels, server-driven UI commands, and both
/// <see cref="NetTransportKind.HostRuleset"/> (<c>0x02C9</c>/<c>0x02CA</c>) and
/// <see cref="NetTransportKind.ChatFallback"/> (<c>##NM </c>/<c>/nm </c>) transports.
/// </summary>
public sealed class ClientBridgeHost : IDisposable
{
    /// <summary>Reserved host system channel identifier (<see cref="MessageCodec.SystemChannel"/>).</summary>
    public const string HostChannelId = MessageCodec.SystemChannel;

    private readonly IGatewayPacketSink _sink;
    private readonly ClientBridgeOptions _options;
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Channel, string MessageType), List<Func<IClientSession, ModEnvelope, CancellationToken, Task>>> _globalHandlers = [];
    private readonly List<Func<IClientSession, CancellationToken, Task>> _clientReadyHandlers = [];
    private readonly Lock _gate = new();
    private GatewayBridgeRegistration? _busRegistration;
    private string? _defaultChannel;
    private string? _expectedContentRowMapDigest;
    private ContentRowMap? _contentRowMap;
    private long _sequence;
    private int _disposed;

    /// <summary>Active bridge configuration options.</summary>
    public ClientBridgeOptions Options => _options;

    /// <summary>Expected SHA-256 <c>content-row-map.json</c> digest enforced during client handshakes, or <c>null</c> if unset.</summary>
    public string? ExpectedContentRowMapDigest => Volatile.Read(ref _expectedContentRowMapDigest);

    /// <summary>Optional <see cref="ContentRowMap"/> bound to this bridge for server-side row/hash resolution.</summary>
    public ContentRowMap? ContentRowMap => Volatile.Read(ref _contentRowMap);

    /// <summary>Default channel used by channel-less <c>SendAsync</c> overloads when configured.</summary>
    public string? DefaultChannel
    {
        get => Volatile.Read(ref _defaultChannel);
        internal set => Volatile.Write(ref _defaultChannel, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    /// <summary>Snapshot of all tracked sessions.</summary>
    public IReadOnlyCollection<IClientSession> Sessions => _sessions.Values.ToArray();

    /// <summary>Whether this host is currently attached to a cross-ALC <see cref="GatewayBridgeBus"/>.</summary>
    public bool IsAttachedToGatewayBus => Volatile.Read(ref _busRegistration) is not null;

    internal IGatewayPacketSink Sink => _sink;

    /// <summary>Raised when a client completes a NativeMods handshake.</summary>
    public event Action<IClientSession>? ClientReady;

    /// <summary>Raised asynchronously when a client completes a NativeMods handshake.</summary>
    public event Func<IClientSession, CancellationToken, Task>? ClientReadyAsync;

    /// <summary>Raised whenever any valid <see cref="ModEnvelope"/> arrives from a client.</summary>
    public event Action<IClientSession, ModEnvelope>? MessageReceived;

    /// <summary>Raised whenever a subscribed sync or async handler throws an unhandled exception.</summary>
    public event Action<string, Exception>? HandlerError;

    /// <summary>
    /// Raised when a client's <see cref="ClientHandshake.ContentRowMapDigest"/> does not match
    /// <see cref="ExpectedContentRowMapDigest"/> (preventing silent row-ID desynchronization).
    /// </summary>
    public event Action<ContentRowMapDigestMismatchEventArgs>? ContentRowMapDigestMismatch;

    /// <summary>Creates a new <see cref="ClientBridgeHost"/> writing outbound traffic through <paramref name="sink"/>.</summary>
    public ClientBridgeHost(IGatewayPacketSink sink, ClientBridgeOptions? options = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? new ClientBridgeOptions();
        _defaultChannel = string.IsNullOrWhiteSpace(_options.DefaultChannel) ? null : _options.DefaultChannel.Trim();
        _contentRowMap = _options.ContentRowMap;
        string? configuredDigest = !string.IsNullOrWhiteSpace(_options.ExpectedContentRowMapDigest)
            ? _options.ExpectedContentRowMapDigest.Trim()
            : (!string.IsNullOrWhiteSpace(_options.ContentRowMap?.Digest) ? _options.ContentRowMap.Digest.Trim() : null);
        _expectedContentRowMapDigest = configuredDigest;
        if (_options.HostRulesetId > IRulesetRegistry.MaxRulesetId)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"HostRulesetId 0x{_options.HostRulesetId:X2} exceeds maximum 0x{IRulesetRegistry.MaxRulesetId:X2}.");
        }
    }

    /// <summary>Binds a server-loaded <see cref="ContentRowMap"/> and enforces its digest on future handshakes.</summary>
    public void SetExpectedContentRowMap(ContentRowMap? rowMap)
    {
        Volatile.Write(ref _contentRowMap, rowMap);
        Volatile.Write(ref _expectedContentRowMapDigest, string.IsNullOrWhiteSpace(rowMap?.Digest) ? null : rowMap.Digest.Trim());
    }

    /// <summary>Sets or clears the expected SHA-256 <c>content-row-map.json</c> digest enforced during client handshakes.</summary>
    public void SetExpectedContentRowMapDigest(string? digest) =>
        Volatile.Write(ref _expectedContentRowMapDigest, string.IsNullOrWhiteSpace(digest) ? null : digest.Trim());

    /// <summary>
    /// Attaches this bridge to the process-wide cross-<c>AssemblyLoadContext</c> <see cref="GatewayBridgeBus"/> so
    /// multiple LocalExile server mods loaded simultaneously receive fanned-out <c>ClientHandshake</c>,
    /// <c>ClientUiEventNotification</c>, and mod-channel packets over <c>0x02CA</c> and <c>/nm</c> while emitting
    /// only a single deduplicated <see cref="ServerHandshake"/> on the wire.
    /// Dispose the returned handle when the mod unloads.
    /// </summary>
    /// <param name="ownerId">Unique LocalExile mod identifier (for example <c>mod.Manifest.Id</c>).</param>
    /// <param name="onGatewayContext">
    /// Optional callback invoked with <c>(connectionId, characterName, gatewayContext)</c> before this host process
    /// a fanned-out packet or chat command so the mod can register the live Gateway player and character binding.
    /// </param>
    /// <param name="domainKey">Optional isolated <see cref="AppDomain"/> slot key (defaults to <see cref="GatewayBridgeBus.DefaultDomainKey"/>).</param>
    /// <param name="onDispatchError">Optional callback invoked when any bus participant throws during fan-out.</param>
    public GatewayBridgeRegistration AttachToGatewayBus(
        string ownerId,
        Action<string, string?, object?>? onGatewayContext = null,
        string? domainKey = null,
        Action<string, Exception>? onDispatchError = null)
    {
        GatewayBridgeRegistration next = GatewayBridgeBus.Attach(this, ownerId, onGatewayContext, domainKey, onDispatchError);
        GatewayBridgeRegistration? previous = Interlocked.Exchange(ref _busRegistration, next);
        previous?.Dispose();
        return next;
    }

    internal void DetachBusRegistration(GatewayBridgeRegistration registration) =>
        Interlocked.CompareExchange(ref _busRegistration, null, registration);

    /// <summary>Gets or creates the <see cref="IClientSession"/> for <paramref name="connectionId"/>.</summary>
    public IClientSession For(string connectionId) => GetOrCreateSession(connectionId);

    /// <summary>Gets or creates the <see cref="IClientSession"/> for <paramref name="connectionId"/>.</summary>
    public IClientSession GetOrCreateSession(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ClientSession session = _sessions.GetOrAdd(connectionId, id => new ClientSession(this, id, _options.DefaultTransport));
        if (!session.HasNativeHost && Volatile.Read(ref _busRegistration) is { } bus)
        {
            TryHydrateSessionFromBus(session, bus);
        }

        return session;
    }

    /// <summary>Returns <c>true</c> if <paramref name="connectionId"/> has completed a NativeMods client handshake.</summary>
    public bool IsClientReady(string connectionId) =>
        TryResolveExistingOrHydratedSession(connectionId) is { HasNativeHost: true };

    /// <summary>
    /// Returns <c>true</c> if <paramref name="connectionId"/> has completed a NativeMods handshake and
    /// reports <paramref name="modId"/> (and optional matching <paramref name="expectedPayloadHash"/>).
    /// </summary>
    public bool HasMod(string connectionId, string modId, string? expectedPayloadHash = null)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            return false;
        }

        return TryResolveExistingOrHydratedSession(connectionId) is { } session && session.HasClientMod(modId, expectedPayloadHash);
    }

    /// <summary>Removes a closed connection's session state and closes any open server UI windows.</summary>
    public bool RemoveSession(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return false;
        }

        Volatile.Read(ref _busRegistration)?.ClearCachedHandshake(connectionId);
        if (_sessions.TryRemove(connectionId, out ClientSession? session))
        {
            session.CloseSessionNonBlocking();
            return true;
        }

        return false;
    }

    /// <summary>Removes a closed connection's session state and awaits closing any open server UI windows.</summary>
    public async Task<bool> RemoveSessionAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return false;
        }

        Volatile.Read(ref _busRegistration)?.ClearCachedHandshake(connectionId);
        if (_sessions.TryRemove(connectionId, out ClientSession? session))
        {
            await session.CloseSessionAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>Subscribes a synchronous callback invoked when any client completes a NativeMods handshake.</summary>
    public IDisposable OnClientReady(Action<IClientSession> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnClientReady((session, _) =>
        {
            handler(session);
            return Task.CompletedTask;
        });
    }

    /// <summary>Subscribes an asynchronous callback invoked when any client completes a NativeMods handshake.</summary>
    public IDisposable OnClientReady(Func<IClientSession, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnClientReady((session, _) => handler(session));
    }

    /// <summary>Subscribes an asynchronous callback with cancellation support invoked when any client completes a NativeMods handshake.</summary>
    public IDisposable OnClientReady(Func<IClientSession, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _clientReadyHandlers.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _clientReadyHandlers.Remove(handler);
            }
        });
    }

    /// <summary>
    /// Subscribes to incoming typed messages from any connected client on <paramref name="channel"/>.
    /// </summary>
    public IDisposable On<T>(string channel, Action<IClientSession, T> handler, string? messageType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterGlobalHandler<T>(
            channel.ToLowerInvariant(),
            messageType,
            (session, payload, _) =>
            {
                handler(session, payload);
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Subscribes asynchronously to incoming typed messages from any connected client on <paramref name="channel"/>.
    /// </summary>
    public IDisposable On<T>(string channel, Func<IClientSession, T, Task> handler, string? messageType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterGlobalHandler<T>(
            channel.ToLowerInvariant(),
            messageType,
            (session, payload, _) => handler(session, payload));
    }

    /// <summary>
    /// Subscribes asynchronously with cancellation support to incoming typed messages from any connected client on <paramref name="channel"/>.
    /// </summary>
    public IDisposable On<T>(string channel, Func<IClientSession, T, CancellationToken, Task> handler, string? messageType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterGlobalHandler<T>(channel.ToLowerInvariant(), messageType, handler);
    }

    /// <summary>
    /// Subscribes to incoming typed messages from any connected client across any channel.
    /// </summary>
    public IDisposable On<T>(Action<IClientSession, T> handler, string? messageType = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterGlobalHandler<T>(
            string.Empty,
            messageType,
            (session, payload, _) =>
            {
                handler(session, payload);
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Subscribes asynchronously to incoming typed messages from any connected client across any channel.
    /// </summary>
    public IDisposable On<T>(Func<IClientSession, T, Task> handler, string? messageType = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterGlobalHandler<T>(
            string.Empty,
            messageType,
            (session, payload, _) => handler(session, payload));
    }

    /// <summary>
    /// Subscribes asynchronously with cancellation support to incoming typed messages from any connected client across any channel.
    /// </summary>
    public IDisposable On<T>(Func<IClientSession, T, CancellationToken, Task> handler, string? messageType = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterGlobalHandler<T>(string.Empty, messageType, handler);
    }

    private Subscription RegisterGlobalHandler<T>(
        string normalizedChannel,
        string? messageType,
        Func<IClientSession, T, CancellationToken, Task> handler)
    {
        string typeName = string.IsNullOrWhiteSpace(messageType) ? typeof(T).Name : messageType;
        var key = (normalizedChannel, typeName);
        Func<IClientSession, ModEnvelope, CancellationToken, Task> wrapper = (session, env, ct) =>
        {
            T payload = MessageCodec.DeserializePayload<T>(env);
            return handler(session, payload, ct);
        };

        lock (_gate)
        {
            if (!_globalHandlers.TryGetValue(key, out List<Func<IClientSession, ModEnvelope, CancellationToken, Task>>? list))
            {
                list = [];
                _globalHandlers[key] = list;
            }

            list.Add(wrapper);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (_globalHandlers.TryGetValue(key, out List<Func<IClientSession, ModEnvelope, CancellationToken, Task>>? list))
                {
                    list.Remove(wrapper);
                    if (list.Count == 0)
                    {
                        _globalHandlers.Remove(key);
                    }
                }
            }
        });
    }

    /// <summary>Sends a typed message to <paramref name="connectionId"/> on <paramref name="channel"/>.</summary>
    public Task SendAsync<T>(string connectionId, string channel, T message, string? messageType = null, CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).SendAsync(channel, message, messageType, cancellationToken);

    /// <summary>Sends a typed message to <paramref name="connectionId"/> using <see cref="DefaultChannel"/> (or <see cref="HostChannelId"/>).</summary>
    public Task SendAsync<T>(string connectionId, T message, CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).SendAsync(ResolveDefaultChannel(), message, messageType: null, cancellationToken);

    /// <summary>
    /// Attempts to send a typed message to an existing, handshake-ready <paramref name="connectionId"/> on <paramref name="channel"/>.
    /// Returns <c>false</c> without throwing if the session does not exist or is not ready.
    /// </summary>
    public async Task<bool> TrySendAsync<T>(string connectionId, string channel, T message, string? messageType = null, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            TryResolveExistingOrHydratedSession(connectionId) is not { HasNativeHost: true, IsClosed: false } session)
        {
            return false;
        }

        return await session.TryQueueOrSendEnvelopeAsync(channel, message, messageType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Attempts to send a typed message to an existing, handshake-ready <paramref name="connectionId"/> using <see cref="DefaultChannel"/>.
    /// Returns <c>false</c> without throwing if the session does not exist or is not ready.
    /// </summary>
    public Task<bool> TrySendAsync<T>(string connectionId, T message, CancellationToken cancellationToken = default) =>
        TrySendAsync(connectionId, ResolveDefaultChannel(), message, messageType: null, cancellationToken);

    /// <summary>Displays a NativeMods HUD toast notification on <paramref name="connectionId"/>.</summary>
    public Task ShowToastAsync(
        string connectionId,
        string title,
        string message,
        ToastSeverity severity = ToastSeverity.Info,
        int durationMs = 4000,
        CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).ShowToastAsync(title, message, severity, durationMs, cancellationToken);

    /// <summary>Queries the current active screen, open panels, active stash tab, and map tier on <paramref name="connectionId"/>.</summary>
    public Task<ClientScreenStateSnapshot> QueryScreenStateAsync(
        string connectionId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).QueryScreenStateAsync(timeout, cancellationToken);

    /// <summary>Queries the currently hovered control, hovered item, and cursor item on <paramref name="connectionId"/>.</summary>
    public Task<ClientHoverStateSnapshot> QueryHoverStateAsync(
        string connectionId,
        bool refresh = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).QueryHoverStateAsync(refresh, timeout, cancellationToken);

    /// <summary>Queries all currently filter-visible ground items on <paramref name="connectionId"/>.</summary>
    public Task<IReadOnlyList<ClientWorldEntitySnapshot>> QueryVisibleGroundItemsAsync(
        string connectionId,
        float? maxDistance = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).QueryVisibleGroundItemsAsync(maxDistance, timeout, cancellationToken);

    /// <summary>Queries live world entities on <paramref name="connectionId"/>.</summary>
    public Task<IReadOnlyList<ClientWorldEntitySnapshot>> QueryWorldEntitiesAsync(
        string connectionId,
        ClientWorldEntityQuery? query = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).QueryWorldEntitiesAsync(query, timeout, cancellationToken);

    /// <summary>Queries the local player entity snapshot on <paramref name="connectionId"/>.</summary>
    public Task<ClientWorldEntitySnapshot?> QueryLocalPlayerAsync(
        string connectionId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).QueryLocalPlayerAsync(timeout, cancellationToken);

    /// <summary>Queries all live numeric stats on an entity (or local player when <paramref name="entityId"/> is <c>0</c>) on <paramref name="connectionId"/>.</summary>
    public Task<ClientEntityStatsSnapshot> QueryEntityStatsAsync(
        string connectionId,
        uint entityId = 0,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).QueryEntityStatsAsync(entityId, timeout, cancellationToken);

    /// <summary>Queries a single live numeric stat on an entity (or local player when <paramref name="entityId"/> is <c>0</c>) on <paramref name="connectionId"/>.</summary>
    public Task<ClientStatValueSnapshot> QueryStatAsync(
        string connectionId,
        int statId,
        uint entityId = 0,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).QueryStatAsync(statId, entityId, timeout, cancellationToken);

    /// <summary>Queries the currently loaded game area metadata on <paramref name="connectionId"/>.</summary>
    public Task<AreaInfo?> QueryAreaInfoAsync(
        string connectionId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).QueryAreaInfoAsync(timeout, cancellationToken);

    /// <summary>Queries the active item-filter state on <paramref name="connectionId"/>.</summary>
    public Task<ItemFilterState> QueryItemFilterStateAsync(
        string connectionId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).QueryItemFilterStateAsync(timeout, cancellationToken);

    /// <summary>Triggers a game-thread reload of the currently selected item filter on <paramref name="connectionId"/>.</summary>
    public Task<bool> ReloadItemFilterAsync(
        string connectionId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).ReloadItemFilterAsync(timeout, cancellationToken);

    /// <summary>Evaluates a world-item or item entity against the active item filter on <paramref name="connectionId"/>.</summary>
    public Task<ItemFilterResult> EvaluateWorldItemFilterAsync(
        string connectionId,
        uint entityId,
        bool liveEvaluate = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).EvaluateWorldItemFilterAsync(entityId, liveEvaluate, timeout, cancellationToken);

    /// <summary>Queries the active in-game camera state on <paramref name="connectionId"/>.</summary>
    public Task<CameraState> QueryCameraStateAsync(
        string connectionId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).QueryCameraStateAsync(timeout, cancellationToken);

    /// <summary>Sets or clears a server-owned camera zoom override on <paramref name="connectionId"/> (auto-restored on disconnect/dispose).</summary>
    public Task SetCameraZoomOverrideAsync(
        string connectionId,
        float? zoomOverride,
        CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).SetCameraZoomOverrideAsync(zoomOverride, cancellationToken);

    internal string ResolveDefaultChannel() => DefaultChannel ?? MessageCodec.SystemChannel;

    /// <summary>
    /// Handles an incoming decoded <see cref="ModEnvelope"/> directly (useful for standalone tests and custom transports).
    /// When attached via <see cref="AttachToGatewayBus"/> and within the ruleset payload limit, fans out via the shared bus.
    /// </summary>
    public async Task<bool> HandleIncomingEnvelopeAsync(
        string connectionId,
        ModEnvelope envelope,
        string? characterName = null,
        object? gatewayContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(envelope);

        if (Volatile.Read(ref _busRegistration) is { } bus)
        {
            byte[] encoded = MessageCodec.EncodeEnvelope(envelope);
            if (encoded.Length <= MessageCodec.MaxRulesetPayloadBytes)
            {
                byte[] body = MessageCodec.EncodeRulesetBody(_options.HostRulesetId, encoded);
                return await bus.DispatchPacketAsync(
                    connectionId,
                    IOpcodeRegistry.ClientRulesetOpcode,
                    body,
                    characterName,
                    gatewayContext,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        ClientSession session = (ClientSession)GetOrCreateSession(connectionId);
        int result = await DispatchIncomingEnvelopeAsync(
            session,
            envelope,
            suppressServerHandshakeReply: false,
            mergedRequiredMods: null,
            mergedCapabilities: null,
            onServerHandshakeSent: null,
            cancellationToken).ConfigureAwait(false);
        return (result & GatewayBridgeBus.ResultHandled) != 0;
    }

    /// <summary>
    /// Handles an incoming C→S packet from LocalExile's <c>GameEvents.Packet(0x02CA)</c> hook.
    /// When attached via <see cref="AttachToGatewayBus"/>, fans out across all loaded mods' bridges
    /// (across separate <c>AssemblyLoadContext</c>s) and deduplicates <see cref="ServerHandshake"/> replies.
    /// Returns <c>true</c> if the packet belonged to NativeMods and was consumed; <c>false</c> so stock handlers continue running.
    /// </summary>
    /// <param name="connectionId">Source player connection identifier.</param>
    /// <param name="opcode">Incoming wire opcode (expected <c>0x02CA</c> for C→S ruleset traffic).</param>
    /// <param name="payload">Packet body bytes (without the 2-byte opcode prefix: <c>[u8 rulesetId][compact-uint len][envelope]</c>).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public Task<bool> HandleIncomingPacketAsync(
        string connectionId,
        ushort opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) =>
        HandleIncomingPacketAsync(connectionId, opcode, payload, characterName: null, gatewayContext: null, cancellationToken);

    /// <summary>
    /// Handles an incoming C→S packet from LocalExile's <c>GameEvents.Packet(0x02CA)</c> hook,
    /// propagating optional <paramref name="characterName"/> and live Gateway <paramref name="gatewayContext"/>
    /// (for example the live <c>Player</c> instance) to every mod attached to the <see cref="GatewayBridgeBus"/>.
    /// </summary>
    /// <param name="connectionId">Source player connection identifier.</param>
    /// <param name="opcode">Incoming wire opcode (expected <c>0x02CA</c> for C→S ruleset traffic).</param>
    /// <param name="payload">Packet body bytes (without the 2-byte opcode prefix).</param>
    /// <param name="characterName">Optional active character name associated with <paramref name="connectionId"/>.</param>
    /// <param name="gatewayContext">Optional live Gateway context object (such as <c>Player</c>) passed to each bus participant.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public async Task<bool> HandleIncomingPacketAsync(
        string connectionId,
        ushort opcode,
        ReadOnlyMemory<byte> payload,
        string? characterName,
        object? gatewayContext = null,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _busRegistration) is { } bus)
        {
            return await bus.DispatchPacketAsync(
                connectionId,
                opcode,
                payload,
                characterName,
                gatewayContext,
                cancellationToken).ConfigureAwait(false);
        }

        int result = await HandleIncomingPacketLocalAsync(
            connectionId,
            opcode,
            payload,
            suppressServerHandshakeReply: false,
            mergedRequiredMods: null,
            mergedCapabilities: null,
            onServerHandshakeSent: null,
            cancellationToken).ConfigureAwait(false);
        return (result & GatewayBridgeBus.ResultHandled) != 0;
    }

    /// <summary>
    /// Handles an incoming C→S chat command line (<c>/nm env &lt;base64&gt;</c>) for the chat fallback transport.
    /// When attached via <see cref="AttachToGatewayBus"/>, fans out across all loaded mods' bridges
    /// and deduplicates <see cref="ServerHandshake"/> replies.
    /// Returns <c>true</c> if the command was a NativeMods envelope and was consumed.
    /// </summary>
    public Task<bool> HandleIncomingChatCommandAsync(
        string connectionId,
        string commandLine,
        CancellationToken cancellationToken = default) =>
        HandleIncomingChatCommandAsync(connectionId, commandLine, characterName: null, gatewayContext: null, cancellationToken);

    /// <summary>
    /// Handles an incoming C→S chat command line (<c>/nm env &lt;base64&gt;</c>), propagating optional
    /// <paramref name="characterName"/> and live Gateway <paramref name="gatewayContext"/> to every mod
    /// attached to the <see cref="GatewayBridgeBus"/>.
    /// </summary>
    public async Task<bool> HandleIncomingChatCommandAsync(
        string connectionId,
        string commandLine,
        string? characterName,
        object? gatewayContext = null,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _busRegistration) is { } bus)
        {
            return await bus.DispatchChatCommandAsync(
                connectionId,
                commandLine,
                characterName,
                gatewayContext,
                cancellationToken).ConfigureAwait(false);
        }

        int result = await HandleIncomingChatCommandLocalAsync(
            connectionId,
            commandLine,
            suppressServerHandshakeReply: false,
            mergedRequiredMods: null,
            mergedCapabilities: null,
            onServerHandshakeSent: null,
            cancellationToken).ConfigureAwait(false);
        return (result & GatewayBridgeBus.ResultHandled) != 0;
    }

    internal async Task<int> HandleIncomingPacketLocalAsync(
        string connectionId,
        ushort opcode,
        ReadOnlyMemory<byte> payload,
        bool suppressServerHandshakeReply,
        IReadOnlyList<string>? mergedRequiredMods,
        IReadOnlyList<string>? mergedCapabilities,
        Action? onServerHandshakeSent,
        CancellationToken cancellationToken)
    {
        if (opcode != IOpcodeRegistry.ClientRulesetOpcode)
        {
            return GatewayBridgeBus.ResultNone;
        }

        if (!MessageCodec.TryDecodeRulesetBody(payload, out byte rulesetId, out ReadOnlyMemory<byte> inner)
            || rulesetId != _options.HostRulesetId)
        {
            return GatewayBridgeBus.ResultNone;
        }

        ModEnvelope envelope;
        try
        {
            envelope = MessageCodec.DecodeEnvelope(inner.Span);
        }
        catch (InvalidDataException)
        {
            return GatewayBridgeBus.ResultNone;
        }

        ClientSession session = (ClientSession)GetOrCreateSession(connectionId);
        session.SetActiveTransport(NetTransportKind.HostRuleset);
        return await DispatchIncomingEnvelopeAsync(
            session,
            envelope,
            suppressServerHandshakeReply,
            mergedRequiredMods,
            mergedCapabilities,
            onServerHandshakeSent,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<int> HandleIncomingChatCommandLocalAsync(
        string connectionId,
        string commandLine,
        bool suppressServerHandshakeReply,
        IReadOnlyList<string>? mergedRequiredMods,
        IReadOnlyList<string>? mergedCapabilities,
        Action? onServerHandshakeSent,
        CancellationToken cancellationToken)
    {
        if (!MessageCodec.TryDecodeClientChatEnvelope(commandLine, out ModEnvelope? envelope) || envelope is null)
        {
            return GatewayBridgeBus.ResultNone;
        }

        ClientSession session = (ClientSession)GetOrCreateSession(connectionId);
        session.SetActiveTransport(NetTransportKind.ChatFallback);
        return await DispatchIncomingEnvelopeAsync(
            session,
            envelope,
            suppressServerHandshakeReply,
            mergedRequiredMods,
            mergedCapabilities,
            onServerHandshakeSent,
            cancellationToken).ConfigureAwait(false);
    }

    private ClientSession? TryResolveExistingOrHydratedSession(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return null;
        }

        if (_sessions.TryGetValue(connectionId, out ClientSession? existing))
        {
            if (!existing.HasNativeHost && Volatile.Read(ref _busRegistration) is { } existingBus)
            {
                TryHydrateSessionFromBus(existing, existingBus);
            }

            return existing;
        }

        if (Volatile.Read(ref _busRegistration) is { } bus &&
            bus.TryGetCachedHandshake(connectionId, _options.HostRulesetId, out _, out _))
        {
            return (ClientSession)GetOrCreateSession(connectionId);
        }

        return null;
    }

    private void TryHydrateSessionFromBus(ClientSession session, GatewayBridgeRegistration bus)
    {
        if (!bus.TryGetCachedHandshake(session.ConnectionId, _options.HostRulesetId, out byte[] rawEnvelope, out bool isChatFallback))
        {
            return;
        }

        try
        {
            ModEnvelope envelope = MessageCodec.DecodeEnvelope(rawEnvelope);
            ClientHandshake handshake = MessageCodec.DeserializePayload<ClientHandshake>(envelope);
            session.TryApplyCachedHandshake(
                isChatFallback ? NetTransportKind.ChatFallback : NetTransportKind.HostRuleset,
                handshake);
        }
        catch (Exception)
        {
            // Ignore malformed cached handshake payloads; live handshake will still apply normally.
        }
    }

    internal GatewayBatchItem EncodeBatchItem(ClientSession session, ModEnvelope envelope)
    {
        if (session.ActiveTransport == NetTransportKind.ChatFallback)
        {
            string line = MessageCodec.EncodeServerChatEnvelope(envelope);
            return new GatewayBatchItem(IsChatReply: true, Opcode: 0, Payload: ReadOnlyMemory<byte>.Empty, ChatText: line);
        }

        byte[] inner = MessageCodec.EncodeEnvelope(envelope);
        byte[] rulesetBody = MessageCodec.EncodeRulesetBody(_options.HostRulesetId, inner);
        return new GatewayBatchItem(IsChatReply: false, Opcode: IOpcodeRegistry.ServerRulesetOpcode, Payload: rulesetBody, ChatText: null);
    }

    internal async Task SendEnvelopeAsync(ClientSession session, ModEnvelope envelope, CancellationToken cancellationToken)
    {
        GatewayBatchItem item = EncodeBatchItem(session, envelope);
        if (item.IsChatReply)
        {
            await _sink.SendChatReplyAsync(session.ConnectionId, item.ChatText ?? string.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _sink.SendPacketAsync(session.ConnectionId, item.Opcode, item.Payload, cancellationToken).ConfigureAwait(false);
    }

    internal async Task SendBatchItemsAsync(ClientSession session, IReadOnlyList<GatewayBatchItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        if (_sink is IBatchingGatewayPacketSink batchingSink)
        {
            await batchingSink.SendBatchAsync(session.ConnectionId, items, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (GatewayBatchItem item in items)
        {
            if (item.IsChatReply)
            {
                await _sink.SendChatReplyAsync(session.ConnectionId, item.ChatText ?? string.Empty, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _sink.SendPacketAsync(session.ConnectionId, item.Opcode, item.Payload, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal long NextSequence() => Interlocked.Increment(ref _sequence);

    internal void ReportHandlerError(string source, Exception exception)
    {
        if (HandlerError is { } errorEvent)
        {
            foreach (Delegate del in errorEvent.GetInvocationList())
            {
                try
                {
                    ((Action<string, Exception>)del)(source, exception);
                }
                catch
                {
                    // Never allow error subscriber exceptions to abort dispatch.
                }
            }
        }

        Trace.TraceWarning($"[ClientBridgeHost] Handler error in '{source}': {exception}");
    }

    private async Task<int> DispatchIncomingEnvelopeAsync(
        ClientSession session,
        ModEnvelope envelope,
        bool suppressServerHandshakeReply,
        IReadOnlyList<string>? mergedRequiredMods,
        IReadOnlyList<string>? mergedCapabilities,
        Action? onServerHandshakeSent,
        CancellationToken cancellationToken)
    {
        if (MessageReceived is { } msgReceived)
        {
            foreach (Delegate del in msgReceived.GetInvocationList())
            {
                try
                {
                    ((Action<IClientSession, ModEnvelope>)del)(session, envelope);
                }
                catch (Exception ex)
                {
                    ReportHandlerError($"MessageReceived({envelope.Channel}/{envelope.MessageType})", ex);
                }
            }
        }

        if (string.Equals(envelope.Channel, MessageCodec.SystemChannel, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(envelope.MessageType, nameof(ClientHandshake), StringComparison.Ordinal))
            {
                ClientHandshake handshake = MessageCodec.DeserializePayload<ClientHandshake>(envelope);
                session.ApplyHandshake(handshake);
                VerifyContentRowMapDigest(session, handshake);

                int flags = GatewayBridgeBus.ResultHandled | GatewayBridgeBus.ResultWasClientHandshake;
                if (!suppressServerHandshakeReply)
                {
                    var reply = new ServerHandshake(
                        ServerVersion: _options.ServerVersion,
                        BridgeVersion: _options.BridgeVersion,
                        ProtocolVersion: Sdk.SdkVersion.ProtocolVersion,
                        HostRulesetId: _options.HostRulesetId,
                        PreferredTransport: session.ActiveTransport,
                        RequiredMods: mergedRequiredMods ?? _options.RequiredMods,
                        ServerCapabilities: mergedCapabilities ?? _options.ServerCapabilities,
                        ExpectedContentRowMapDigest: ExpectedContentRowMapDigest);

                    await session.SendDirectAsync(MessageCodec.SystemChannel, reply, messageType: null, cancellationToken).ConfigureAwait(false);
                    onServerHandshakeSent?.Invoke();
                    flags |= GatewayBridgeBus.ResultSentServerHandshake;
                }

                await DispatchClientReadyAsync(session, cancellationToken).ConfigureAwait(false);
                return flags;
            }

            if (string.Equals(envelope.MessageType, nameof(ClientUiEventNotification), StringComparison.Ordinal))
            {
                ClientUiEventNotification uiEvent = MessageCodec.DeserializePayload<ClientUiEventNotification>(envelope);
                await session.DispatchUiEventAsync(uiEvent, cancellationToken).ConfigureAwait(false);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.SlotButtonClick, StringComparison.Ordinal))
            {
                Tier1SlotButtonClickPayload click = MessageCodec.DeserializePayload<Tier1SlotButtonClickPayload>(envelope);
                await session.DispatchTier1SlotButtonClickAsync(click, cancellationToken).ConfigureAwait(false);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.HotkeyTriggered, StringComparison.Ordinal))
            {
                ClientHotkeyTriggeredEvent hotkeyEvt = MessageCodec.DeserializePayload<ClientHotkeyTriggeredEvent>(envelope);
                await session.DispatchTier1HotkeyTriggeredAsync(hotkeyEvt, cancellationToken).ConfigureAwait(false);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.ScreenStateResponse, StringComparison.Ordinal))
            {
                Tier1ScreenStateResponsePayload screenResp = MessageCodec.DeserializePayload<Tier1ScreenStateResponsePayload>(envelope);
                session.HandleTier1ScreenStateResponse(screenResp);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.HoverStateResponse, StringComparison.Ordinal))
            {
                Tier1HoverStateResponsePayload hoverResp = MessageCodec.DeserializePayload<Tier1HoverStateResponsePayload>(envelope);
                session.HandleTier1HoverStateResponse(hoverResp);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.ScreenStateChanged, StringComparison.Ordinal))
            {
                ClientScreenChangedEvent screenChanged = MessageCodec.DeserializePayload<ClientScreenChangedEvent>(envelope);
                await session.DispatchTier1ScreenChangedAsync(screenChanged, cancellationToken).ConfigureAwait(false);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.HoverStateChanged, StringComparison.Ordinal))
            {
                ClientHoverStateSnapshot hoverChanged = MessageCodec.DeserializePayload<ClientHoverStateSnapshot>(envelope);
                await session.DispatchTier1HoverChangedAsync(hoverChanged, cancellationToken).ConfigureAwait(false);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.WorldEntitiesResponse, StringComparison.Ordinal))
            {
                Tier1WorldEntitiesResponsePayload worldResp = MessageCodec.DeserializePayload<Tier1WorldEntitiesResponsePayload>(envelope);
                session.HandleTier1WorldEntitiesResponse(worldResp);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.LocalPlayerResponse, StringComparison.Ordinal))
            {
                Tier1LocalPlayerResponsePayload playerResp = MessageCodec.DeserializePayload<Tier1LocalPlayerResponsePayload>(envelope);
                session.HandleTier1LocalPlayerResponse(playerResp);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.EntityStatsResponse, StringComparison.Ordinal))
            {
                Tier1EntityStatsResponsePayload statsResp = MessageCodec.DeserializePayload<Tier1EntityStatsResponsePayload>(envelope);
                session.HandleTier1EntityStatsResponse(statsResp);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.StatResponse, StringComparison.Ordinal))
            {
                Tier1StatResponsePayload statResp = MessageCodec.DeserializePayload<Tier1StatResponsePayload>(envelope);
                session.HandleTier1StatResponse(statResp);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.AreaInfoResponse, StringComparison.Ordinal))
            {
                Tier1AreaInfoResponsePayload areaResp = MessageCodec.DeserializePayload<Tier1AreaInfoResponsePayload>(envelope);
                session.HandleTier1AreaInfoResponse(areaResp);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.AreaChanged, StringComparison.Ordinal))
            {
                ClientAreaChangedEvent areaChanged = MessageCodec.DeserializePayload<ClientAreaChangedEvent>(envelope);
                await session.DispatchTier1AreaChangedAsync(areaChanged, cancellationToken).ConfigureAwait(false);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.ItemFilterStateResponse, StringComparison.Ordinal))
            {
                Tier1ItemFilterStateResponsePayload filterStateResp = MessageCodec.DeserializePayload<Tier1ItemFilterStateResponsePayload>(envelope);
                session.HandleTier1ItemFilterStateResponse(filterStateResp);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.ReloadItemFilterResponse, StringComparison.Ordinal))
            {
                Tier1ReloadItemFilterResponsePayload filterReloadResp = MessageCodec.DeserializePayload<Tier1ReloadItemFilterResponsePayload>(envelope);
                session.HandleTier1ReloadItemFilterResponse(filterReloadResp);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.EvaluateWorldItemFilterResponse, StringComparison.Ordinal))
            {
                Tier1EvaluateWorldItemFilterResponsePayload filterEvalResp = MessageCodec.DeserializePayload<Tier1EvaluateWorldItemFilterResponsePayload>(envelope);
                session.HandleTier1EvaluateWorldItemFilterResponse(filterEvalResp);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.FilterReloaded, StringComparison.Ordinal))
            {
                ItemFilterReloadedEventArgs filterReloaded = MessageCodec.DeserializePayload<ItemFilterReloadedEventArgs>(envelope);
                await session.DispatchTier1FilterReloadedAsync(filterReloaded, cancellationToken).ConfigureAwait(false);
                return GatewayBridgeBus.ResultHandled;
            }

            if (string.Equals(envelope.MessageType, Tier1MessageTypes.CameraStateResponse, StringComparison.Ordinal))
            {
                Tier1CameraStateResponsePayload cameraResp = MessageCodec.DeserializePayload<Tier1CameraStateResponsePayload>(envelope);
                session.HandleTier1CameraStateResponse(cameraResp);
                return GatewayBridgeBus.ResultHandled;
            }
        }

        await session.DispatchEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);

        string normalizedChannel = envelope.Channel.ToLowerInvariant();
        Func<IClientSession, ModEnvelope, CancellationToken, Task>[] exactGlobals;
        Func<IClientSession, ModEnvelope, CancellationToken, Task>[] wildcardGlobals;
        lock (_gate)
        {
            exactGlobals = _globalHandlers.TryGetValue((normalizedChannel, envelope.MessageType), out var exactList)
                ? [.. exactList]
                : [];
            wildcardGlobals = _globalHandlers.TryGetValue((string.Empty, envelope.MessageType), out var wildcardList)
                ? [.. wildcardList]
                : [];
        }

        await InvokeEnvelopeHandlersAsync(exactGlobals, session, envelope, cancellationToken).ConfigureAwait(false);
        await InvokeEnvelopeHandlersAsync(wildcardGlobals, session, envelope, cancellationToken).ConfigureAwait(false);

        return GatewayBridgeBus.ResultHandled;
    }

    private void VerifyContentRowMapDigest(ClientSession session, ClientHandshake handshake)
    {
        string? expected = Volatile.Read(ref _expectedContentRowMapDigest);
        if (string.IsNullOrWhiteSpace(expected))
        {
            return;
        }

        string? actual = string.IsNullOrWhiteSpace(handshake.ContentRowMapDigest)
            ? null
            : handshake.ContentRowMapDigest.Trim();

        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string message = actual is null
            ? $"Client '{session.ConnectionId}' reported no content-row-map.json digest, but server expects '{expected}'. Rebuild Mods/Client/.merged-cache/ via Waypoint to avoid row-ID desynchronization."
            : $"Client '{session.ConnectionId}' content-row-map.json digest '{actual}' does not match server expected digest '{expected}'. Rebuild Mods/Client/.merged-cache/ via Waypoint to avoid row-ID desynchronization.";

        Trace.TraceWarning($"[ClientBridgeHost] {message}");

        var args = new ContentRowMapDigestMismatchEventArgs(
            session.ConnectionId,
            session,
            actual,
            expected,
            message);

        if (ContentRowMapDigestMismatch is { } mismatchEvent)
        {
            foreach (Delegate del in mismatchEvent.GetInvocationList())
            {
                try
                {
                    ((Action<ContentRowMapDigestMismatchEventArgs>)del)(args);
                }
                catch (Exception ex)
                {
                    ReportHandlerError($"ContentRowMapDigestMismatch({session.ConnectionId})", ex);
                }
            }
        }
    }

    private async Task DispatchClientReadyAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (ClientReady is { } syncEvent)
        {
            foreach (Delegate del in syncEvent.GetInvocationList())
            {
                try
                {
                    ((Action<IClientSession>)del)(session);
                }
                catch (Exception ex)
                {
                    ReportHandlerError($"ClientReady({session.ConnectionId})", ex);
                }
            }
        }

        if (ClientReadyAsync is { } asyncEvent)
        {
            foreach (Delegate del in asyncEvent.GetInvocationList())
            {
                try
                {
                    await ((Func<IClientSession, CancellationToken, Task>)del)(session, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ReportHandlerError($"ClientReadyAsync({session.ConnectionId})", ex);
                }
            }
        }

        Func<IClientSession, CancellationToken, Task>[] readySnapshot;
        lock (_gate)
        {
            readySnapshot = [.. _clientReadyHandlers];
        }

        foreach (Func<IClientSession, CancellationToken, Task> handler in readySnapshot)
        {
            try
            {
                await handler(session, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ReportHandlerError($"OnClientReady({session.ConnectionId})", ex);
            }
        }
    }

    internal async Task InvokeEnvelopeHandlersAsync(
        Func<IClientSession, ModEnvelope, CancellationToken, Task>[] handlers,
        ClientSession session,
        ModEnvelope envelope,
        CancellationToken cancellationToken)
    {
        foreach (Func<IClientSession, ModEnvelope, CancellationToken, Task> handler in handlers)
        {
            try
            {
                await handler(session, envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ReportHandlerError($"On<{envelope.MessageType}>({envelope.Channel})", ex);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _busRegistration, null)?.Dispose();

        foreach (string key in _sessions.Keys.ToArray())
        {
            if (_sessions.TryRemove(key, out ClientSession? session))
            {
                session.CloseSessionNonBlocking();
            }
        }

        lock (_gate)
        {
            _globalHandlers.Clear();
            _clientReadyHandlers.Clear();
        }
    }

    internal sealed class Subscription(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    internal sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Ambient static facade matching LocalExile's <c>Client.For(conn)</c> server-mod ergonomic pattern (<c>11-client-sdk.md</c> §4).
/// </summary>
public static class Client
{
    private static ClientBridgeHost? _defaultHost;

    /// <summary>Gets the active ambient <see cref="ClientBridgeHost"/>, or <c>null</c> if not initialized.</summary>
    public static ClientBridgeHost? DefaultHost => _defaultHost;

    /// <summary>Binds the ambient <see cref="ClientBridgeHost"/> used by <see cref="For"/>.</summary>
    public static void Initialize(ClientBridgeHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _defaultHost = host;
    }

    /// <summary>Clears the ambient <see cref="ClientBridgeHost"/>.</summary>
    public static void Reset() => _defaultHost = null;

    /// <summary>Returns the <see cref="IClientSession"/> for <paramref name="connectionId"/> from <see cref="DefaultHost"/>.</summary>
    public static IClientSession For(string connectionId)
    {
        ClientBridgeHost host = _defaultHost
            ?? throw new InvalidOperationException("Client bridge has not been initialized. Call Client.Initialize(host) first.");
        return host.For(connectionId);
    }
}

internal sealed class ClientSession : IClientSession
{
    private readonly ClientBridgeHost _host;
    private readonly ClientUiSession _ui;
    private readonly ClientPhase1Session _phase1;
    private readonly ClientPhase2Session _phase2;
    private readonly Dictionary<(string Channel, string MessageType), List<Func<IClientSession, ModEnvelope, CancellationToken, Task>>> _handlers = [];
    private readonly List<PendingBatchEntry> _batchQueue = [];
    private readonly Lock _gate = new();
    private readonly Lock _batchGate = new();
    private int _batchDepth;
    private int _closed;

    public string ConnectionId { get; }
    internal bool IsClosed => Volatile.Read(ref _closed) != 0;
    public bool HasNativeHost => Handshake is not null;
    public ClientHandshake? Handshake { get; private set; }
    public NetTransportKind ActiveTransport { get; private set; }
    public ClientBuildId ClientBuild => Handshake?.Build ?? ClientBuildId.Unknown;
    public string? ExtenderVersion => Handshake?.ExtenderVersion;
    public string? ClientSdkVersion => Handshake?.SdkVersion;
    public string? ClientContentRowMapDigest => Handshake?.ContentRowMapDigest;
    public string? ExpectedContentRowMapDigest => _host.ExpectedContentRowMapDigest;
    public bool? IsContentRowMapDigestMatched
    {
        get
        {
            string? expected = _host.ExpectedContentRowMapDigest;
            if (string.IsNullOrWhiteSpace(expected) || Handshake is null)
            {
                return null;
            }

            return string.Equals(Handshake.ContentRowMapDigest?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
        }
    }
    public IReadOnlyCollection<InstalledClientModInfo> InstalledMods => Handshake?.InstalledMods ?? [];
    public IReadOnlyCollection<string> ClientMods =>
        Handshake?.InstalledMods is { Count: > 0 } mods ? mods.Select(m => m.Id).ToArray() : [];
    public IClientUiSession Ui => _ui;
    public IClientPhase1Session Phase1 => _phase1;
    public IClientPhase2Session Phase2 => _phase2;

    public ClientSession(ClientBridgeHost host, string connectionId, NetTransportKind defaultTransport)
    {
        _host = host;
        ConnectionId = connectionId;
        ActiveTransport = defaultTransport;
        _ui = new ClientUiSession(this, host);
        _phase1 = new ClientPhase1Session(this, host);
        _phase2 = new ClientPhase2Session(this, host);
    }

    internal void SetActiveTransport(NetTransportKind kind)
    {
        lock (_gate)
        {
            ActiveTransport = kind;
        }
    }

    internal void ApplyHandshake(ClientHandshake handshake)
    {
        lock (_gate)
        {
            Handshake = handshake;
        }
    }

    internal void TryApplyCachedHandshake(NetTransportKind kind, ClientHandshake handshake)
    {
        lock (_gate)
        {
            if (Handshake is null)
            {
                ActiveTransport = kind;
                Handshake = handshake;
            }
        }
    }

    public bool HasClientMod(string modId, string? expectedPayloadHash = null)
    {
        if (string.IsNullOrWhiteSpace(modId) || Handshake is null)
        {
            return false;
        }

        foreach (InstalledClientModInfo mod in Handshake.InstalledMods)
        {
            if (string.Equals(mod.Id, modId, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(expectedPayloadHash))
                {
                    return true;
                }

                return string.Equals(mod.PayloadHash, expectedPayloadHash, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    public Task SendAsync<T>(string channel, T message, string? messageType = null, CancellationToken cancellationToken = default)
    {
        if (IsClosed)
        {
            return Task.CompletedTask;
        }

        ModEnvelope envelope = MessageCodec.CreateTypedEnvelope(channel, message, messageType, _host.NextSequence());
        return QueueOrSendAsync(envelope, uiCommand: null, cancellationToken);
    }

    public Task SendAsync<T>(T message, CancellationToken cancellationToken = default) =>
        SendAsync(_host.ResolveDefaultChannel(), message, messageType: null, cancellationToken);

    internal Task<bool> TryQueueOrSendEnvelopeAsync<T>(string channel, T message, string? messageType, CancellationToken cancellationToken)
    {
        if (IsClosed)
        {
            return Task.FromResult(false);
        }

        ModEnvelope envelope = MessageCodec.CreateTypedEnvelope(channel, message, messageType, _host.NextSequence());
        return QueueOrSendAsync(envelope, uiCommand: null, cancellationToken);
    }

    internal Task SendDirectAsync<T>(string channel, T message, string? messageType, CancellationToken cancellationToken)
    {
        if (IsClosed)
        {
            return Task.CompletedTask;
        }

        ModEnvelope envelope = MessageCodec.CreateTypedEnvelope(channel, message, messageType, _host.NextSequence());
        return _host.SendEnvelopeAsync(this, envelope, cancellationToken);
    }

    internal Task QueueOrSendUiCommandAsync(ServerUiCommand command, CancellationToken cancellationToken)
    {
        if (IsClosed)
        {
            return Task.CompletedTask;
        }

        ModEnvelope envelope = MessageCodec.CreateTypedEnvelope(MessageCodec.SystemChannel, command, messageType: null, _host.NextSequence());
        return QueueOrSendAsync(envelope, command, cancellationToken);
    }

    internal Task QueueOrSendSystemMessageAsync<T>(string messageType, T payload, CancellationToken cancellationToken)
    {
        if (IsClosed)
        {
            return Task.CompletedTask;
        }

        ModEnvelope envelope = MessageCodec.CreateTypedEnvelope(MessageCodec.SystemChannel, payload, messageType, _host.NextSequence());
        return QueueOrSendAsync(envelope, uiCommand: null, cancellationToken);
    }

    // ---- IClientPhase1Session forwarding ----

    public Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(ScreenSlot slot, string id, string text, SlotAttachmentOptions? options = null, CancellationToken cancellationToken = default) =>
        _phase1.RegisterSlotButtonAsync(slot, id, text, options, cancellationToken);

    public Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(ScreenSlot slot, string id, string text, Action<IClientSession> onClick, SlotAttachmentOptions? options = null, CancellationToken cancellationToken = default) =>
        _phase1.RegisterSlotButtonAsync(slot, id, text, onClick, options, cancellationToken);

    public Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(ScreenSlot slot, string id, string text, Func<IClientSession, Task> onClick, SlotAttachmentOptions? options = null, CancellationToken cancellationToken = default) =>
        _phase1.RegisterSlotButtonAsync(slot, id, text, onClick, options, cancellationToken);

    public Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(ScreenSlot slot, string id, string text, Func<IClientSession, CancellationToken, Task> onClick, SlotAttachmentOptions? options = null, CancellationToken cancellationToken = default) =>
        _phase1.RegisterSlotButtonAsync(slot, id, text, onClick, options, cancellationToken);

    public Task RemoveSlotButtonAsync(ScreenSlot slot, string id, CancellationToken cancellationToken = default) =>
        _phase1.RemoveSlotButtonAsync(slot, id, cancellationToken);

    public Task<IDisposable> OnSlotButtonClickAsync(ScreenSlot slot, string id, Action<IClientSession> handler, CancellationToken cancellationToken = default) =>
        _phase1.OnSlotButtonClickAsync(slot, id, handler, cancellationToken);

    public Task<IDisposable> OnSlotButtonClickAsync(ScreenSlot slot, string id, Func<IClientSession, Task> handler, CancellationToken cancellationToken = default) =>
        _phase1.OnSlotButtonClickAsync(slot, id, handler, cancellationToken);

    public Task<IDisposable> OnSlotButtonClickAsync(ScreenSlot slot, string id, Func<IClientSession, CancellationToken, Task> handler, CancellationToken cancellationToken = default) =>
        _phase1.OnSlotButtonClickAsync(slot, id, handler, cancellationToken);

    public Task ShowToastAsync(string title, string message, ToastSeverity severity = ToastSeverity.Info, int durationMs = 4000, CancellationToken cancellationToken = default) =>
        _phase1.ShowToastAsync(title, message, severity, durationMs, cancellationToken);

    public Task<ServerHotkeyHandle> RegisterHotkeyAsync(string id, string displayName, KeyChord defaultChord, bool allowWhenTextInputFocused = false, bool suppressGameInput = false, CancellationToken cancellationToken = default) =>
        _phase1.RegisterHotkeyAsync(id, displayName, defaultChord, allowWhenTextInputFocused, suppressGameInput, cancellationToken);

    public Task<ServerHotkeyHandle> RegisterHotkeyAsync(string id, string displayName, string defaultChord, bool allowWhenTextInputFocused = false, bool suppressGameInput = false, CancellationToken cancellationToken = default) =>
        _phase1.RegisterHotkeyAsync(id, displayName, defaultChord, allowWhenTextInputFocused, suppressGameInput, cancellationToken);

    public Task<ServerHotkeyHandle> RegisterHotkeyAsync(string id, string displayName, KeyChord defaultChord, Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task> onTriggered, bool allowWhenTextInputFocused = false, bool suppressGameInput = false, CancellationToken cancellationToken = default) =>
        _phase1.RegisterHotkeyAsync(id, displayName, defaultChord, onTriggered, allowWhenTextInputFocused, suppressGameInput, cancellationToken);

    public Task<ServerHotkeyHandle> RegisterHotkeyAsync(string id, string displayName, string defaultChord, Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task> onTriggered, bool allowWhenTextInputFocused = false, bool suppressGameInput = false, CancellationToken cancellationToken = default) =>
        _phase1.RegisterHotkeyAsync(id, displayName, defaultChord, onTriggered, allowWhenTextInputFocused, suppressGameInput, cancellationToken);

    public Task<IDisposable> OnHotkeyAsync(string id, Action<IClientSession, ClientHotkeyTriggeredEvent> handler, CancellationToken cancellationToken = default) =>
        _phase1.OnHotkeyAsync(id, handler, cancellationToken);

    public Task<IDisposable> OnHotkeyAsync(string id, Func<IClientSession, ClientHotkeyTriggeredEvent, Task> handler, CancellationToken cancellationToken = default) =>
        _phase1.OnHotkeyAsync(id, handler, cancellationToken);

    public Task<IDisposable> OnHotkeyAsync(string id, Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task> handler, CancellationToken cancellationToken = default) =>
        _phase1.OnHotkeyAsync(id, handler, cancellationToken);

    public Task UnregisterHotkeyAsync(string id, CancellationToken cancellationToken = default) =>
        _phase1.UnregisterHotkeyAsync(id, cancellationToken);

    public Task<ClientScreenStateSnapshot> QueryScreenStateAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _phase1.QueryScreenStateAsync(timeout, cancellationToken);

    public Task<ClientHoverStateSnapshot> QueryHoverStateAsync(bool refresh = true, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _phase1.QueryHoverStateAsync(refresh, timeout, cancellationToken);

    public Task<IDisposable> OnScreenChangedAsync(Action<IClientSession, ClientScreenChangedEvent> handler, CancellationToken cancellationToken = default) =>
        _phase1.OnScreenChangedAsync(handler, cancellationToken);

    public Task<IDisposable> OnScreenChangedAsync(Func<IClientSession, ClientScreenChangedEvent, Task> handler, CancellationToken cancellationToken = default) =>
        _phase1.OnScreenChangedAsync(handler, cancellationToken);

    public Task<IDisposable> OnScreenChangedAsync(Func<IClientSession, ClientScreenChangedEvent, CancellationToken, Task> handler, CancellationToken cancellationToken = default) =>
        _phase1.OnScreenChangedAsync(handler, cancellationToken);

    public Task<IDisposable> OnHoverChangedAsync(Action<IClientSession, ClientHoverStateSnapshot> handler, CancellationToken cancellationToken = default) =>
        _phase1.OnHoverChangedAsync(handler, cancellationToken);

    public Task<IDisposable> OnHoverChangedAsync(Func<IClientSession, ClientHoverStateSnapshot, Task> handler, CancellationToken cancellationToken = default) =>
        _phase1.OnHoverChangedAsync(handler, cancellationToken);

    public Task<IDisposable> OnHoverChangedAsync(Func<IClientSession, ClientHoverStateSnapshot, CancellationToken, Task> handler, CancellationToken cancellationToken = default) =>
        _phase1.OnHoverChangedAsync(handler, cancellationToken);

    // ---- IClientPhase2Session forwarding ----

    public Task<IReadOnlyList<ClientWorldEntitySnapshot>> QueryVisibleGroundItemsAsync(float? maxDistance = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _phase2.QueryVisibleGroundItemsAsync(maxDistance, timeout, cancellationToken);

    public Task<IReadOnlyList<ClientWorldEntitySnapshot>> QueryWorldEntitiesAsync(ClientWorldEntityQuery? query = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _phase2.QueryWorldEntitiesAsync(query, timeout, cancellationToken);

    public Task<ClientWorldEntitySnapshot?> QueryLocalPlayerAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _phase2.QueryLocalPlayerAsync(timeout, cancellationToken);

    public Task<ClientEntityStatsSnapshot> QueryEntityStatsAsync(uint entityId = 0, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _phase2.QueryEntityStatsAsync(entityId, timeout, cancellationToken);

    public Task<ClientStatValueSnapshot> QueryStatAsync(int statId, uint entityId = 0, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _phase2.QueryStatAsync(statId, entityId, timeout, cancellationToken);

    public Task<AreaInfo?> QueryAreaInfoAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _phase2.QueryAreaInfoAsync(timeout, cancellationToken);

    public Task<IDisposable> OnAreaChangedAsync(Action<IClientSession, ClientAreaChangedEvent> handler, CancellationToken cancellationToken = default) =>
        _phase2.OnAreaChangedAsync(handler, cancellationToken);

    public Task<IDisposable> OnAreaChangedAsync(Func<IClientSession, ClientAreaChangedEvent, Task> handler, CancellationToken cancellationToken = default) =>
        _phase2.OnAreaChangedAsync(handler, cancellationToken);

    public Task<IDisposable> OnAreaChangedAsync(Func<IClientSession, ClientAreaChangedEvent, CancellationToken, Task> handler, CancellationToken cancellationToken = default) =>
        _phase2.OnAreaChangedAsync(handler, cancellationToken);

    public Task<ItemFilterState> QueryItemFilterStateAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _phase2.QueryItemFilterStateAsync(timeout, cancellationToken);

    public Task<bool> ReloadItemFilterAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _phase2.ReloadItemFilterAsync(timeout, cancellationToken);

    public Task<ItemFilterResult> EvaluateWorldItemFilterAsync(uint entityId, bool liveEvaluate = false, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _phase2.EvaluateWorldItemFilterAsync(entityId, liveEvaluate, timeout, cancellationToken);

    public Task<IDisposable> OnFilterReloadedAsync(Action<IClientSession, ItemFilterReloadedEventArgs> handler, CancellationToken cancellationToken = default) =>
        _phase2.OnFilterReloadedAsync(handler, cancellationToken);

    public Task<IDisposable> OnFilterReloadedAsync(Func<IClientSession, ItemFilterReloadedEventArgs, Task> handler, CancellationToken cancellationToken = default) =>
        _phase2.OnFilterReloadedAsync(handler, cancellationToken);

    public Task<IDisposable> OnFilterReloadedAsync(Func<IClientSession, ItemFilterReloadedEventArgs, CancellationToken, Task> handler, CancellationToken cancellationToken = default) =>
        _phase2.OnFilterReloadedAsync(handler, cancellationToken);

    public Task<CameraState> QueryCameraStateAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _phase2.QueryCameraStateAsync(timeout, cancellationToken);

    public Task SetCameraZoomOverrideAsync(float? zoomOverride, CancellationToken cancellationToken = default) =>
        _phase2.SetCameraZoomOverrideAsync(zoomOverride, cancellationToken);

    private Task<bool> QueueOrSendAsync(ModEnvelope envelope, ServerUiCommand? uiCommand, CancellationToken cancellationToken)
    {
        lock (_batchGate)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return Task.FromResult(false);
            }

            if (_batchDepth > 0)
            {
                _batchQueue.Add(new PendingBatchEntry(envelope, uiCommand));
                return Task.FromResult(true);
            }
        }

        return SendEnvelopeAndReturnTrueAsync(envelope, cancellationToken);
    }

    private async Task<bool> SendEnvelopeAndReturnTrueAsync(ModEnvelope envelope, CancellationToken cancellationToken)
    {
        await _host.SendEnvelopeAsync(this, envelope, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public IDisposable On<T>(string channel, Action<IClientSession, T> handler, string? messageType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterHandler<T>(
            channel.ToLowerInvariant(),
            messageType,
            (session, payload, _) =>
            {
                handler(session, payload);
                return Task.CompletedTask;
            });
    }

    public IDisposable On<T>(string channel, Func<IClientSession, T, Task> handler, string? messageType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterHandler<T>(
            channel.ToLowerInvariant(),
            messageType,
            (session, payload, _) => handler(session, payload));
    }

    public IDisposable On<T>(string channel, Func<IClientSession, T, CancellationToken, Task> handler, string? messageType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterHandler<T>(channel.ToLowerInvariant(), messageType, handler);
    }

    public IDisposable On<T>(Action<IClientSession, T> handler, string? messageType = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterHandler<T>(
            string.Empty,
            messageType,
            (session, payload, _) =>
            {
                handler(session, payload);
                return Task.CompletedTask;
            });
    }

    public IDisposable On<T>(Func<IClientSession, T, Task> handler, string? messageType = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterHandler<T>(
            string.Empty,
            messageType,
            (session, payload, _) => handler(session, payload));
    }

    public IDisposable On<T>(Func<IClientSession, T, CancellationToken, Task> handler, string? messageType = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterHandler<T>(string.Empty, messageType, handler);
    }

    private IDisposable RegisterHandler<T>(
        string normalizedChannel,
        string? messageType,
        Func<IClientSession, T, CancellationToken, Task> handler)
    {
        string typeName = string.IsNullOrWhiteSpace(messageType) ? typeof(T).Name : messageType;
        var key = (normalizedChannel, typeName);
        Func<IClientSession, ModEnvelope, CancellationToken, Task> wrapper = (session, env, ct) =>
        {
            T payload = MessageCodec.DeserializePayload<T>(env);
            return handler(session, payload, ct);
        };

        lock (_gate)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return ClientBridgeHost.NoOpDisposable.Instance;
            }

            if (!_handlers.TryGetValue(key, out List<Func<IClientSession, ModEnvelope, CancellationToken, Task>>? list))
            {
                list = [];
                _handlers[key] = list;
            }

            list.Add(wrapper);
        }

        return new ClientBridgeHost.Subscription(() =>
        {
            lock (_gate)
            {
                if (_handlers.TryGetValue(key, out List<Func<IClientSession, ModEnvelope, CancellationToken, Task>>? list))
                {
                    list.Remove(wrapper);
                    if (list.Count == 0)
                    {
                        _handlers.Remove(key);
                    }
                }
            }
        });
    }

    public IClientCommandBatch BeginBatch()
    {
        lock (_batchGate)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return new ClosedCommandBatch();
            }

            int watermark = _batchQueue.Count;
            _batchDepth++;
            return new ClientCommandBatch(this, watermark);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        FlushBatchQueueAsync(cancellationToken);

    public async Task BatchAsync(Func<IClientSession, Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        IClientCommandBatch batch = BeginBatch();
        try
        {
            await action(this).ConfigureAwait(false);
            await batch.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            batch.Discard();
            throw;
        }
    }

    public async Task BatchAsync(Func<IClientSession, CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        IClientCommandBatch batch = BeginBatch();
        try
        {
            await action(this, cancellationToken).ConfigureAwait(false);
            await batch.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            batch.Discard();
            throw;
        }
    }

    internal int BatchCount
    {
        get
        {
            lock (_batchGate)
            {
                return _batchQueue.Count;
            }
        }
    }

    internal void DiscardFromWatermark(int watermark)
    {
        lock (_batchGate)
        {
            if (watermark < _batchQueue.Count)
            {
                _batchQueue.RemoveRange(watermark, _batchQueue.Count - watermark);
            }

            if (_batchDepth > 0)
            {
                _batchDepth--;
            }
        }
    }

    internal Task CompleteBatchAsync(CancellationToken cancellationToken)
    {
        bool shouldFlush;
        lock (_batchGate)
        {
            if (_batchDepth > 0)
            {
                _batchDepth--;
            }

            shouldFlush = _batchDepth == 0 && _batchQueue.Count > 0;
        }

        return shouldFlush ? FlushBatchQueueAsync(cancellationToken) : Task.CompletedTask;
    }

    internal void CompleteBatchNonBlocking()
    {
        Task task = CompleteBatchAsync(CancellationToken.None);
        if (task.IsFaulted && task.Exception is not null)
        {
            _host.ReportHandlerError($"Batch.Dispose({ConnectionId})", task.Exception.GetBaseException());
        }
        else if (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                t => _host.ReportHandlerError($"Batch.Dispose({ConnectionId})", t.Exception?.GetBaseException() ?? new InvalidOperationException("Batch flush failed.")),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private Task FlushBatchQueueAsync(CancellationToken cancellationToken)
    {
        PendingBatchEntry[] snapshot;
        lock (_batchGate)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                _batchQueue.Clear();
                return Task.CompletedTask;
            }

            if (_batchQueue.Count == 0)
            {
                return Task.CompletedTask;
            }

            snapshot = [.. _batchQueue];
            _batchQueue.Clear();
        }

        List<PendingBatchEntry> coalesced = CoalesceBatchEntries(snapshot);
        var wireItems = new GatewayBatchItem[coalesced.Count];
        for (int i = 0; i < coalesced.Count; i++)
        {
            wireItems[i] = _host.EncodeBatchItem(this, coalesced[i].Envelope);
        }

        return _host.SendBatchItemsAsync(this, wireItems, cancellationToken);
    }

    private static List<PendingBatchEntry> CoalesceBatchEntries(PendingBatchEntry[] raw)
    {
        var result = new List<PendingBatchEntry>(raw.Length);
        int segmentStart = 0;

        foreach (PendingBatchEntry entry in raw)
        {
            if (entry.UiCommand is not { } cmd)
            {
                result.Add(entry);
                segmentStart = result.Count;
                continue;
            }

            if (cmd.Kind is ServerUiCommandKind.OpenWindow or ServerUiCommandKind.CloseWindow or ServerUiCommandKind.ReloadWindow)
            {
                result.Add(entry);
                segmentStart = result.Count;
                continue;
            }

            if (cmd.Kind is ServerUiCommandKind.SetText or ServerUiCommandKind.SetVisible or ServerUiCommandKind.SetProperty or ServerUiCommandKind.SubscribeEvent)
            {
                for (int i = result.Count - 1; i >= segmentStart; i--)
                {
                    if (result[i].UiCommand is { } prior && IsSameCoalesceKey(prior, cmd))
                    {
                        result.RemoveAt(i);
                        break;
                    }
                }
            }

            result.Add(entry);
        }

        return result;
    }

    private static bool IsSameCoalesceKey(ServerUiCommand a, ServerUiCommand b) =>
        a.Kind == b.Kind &&
        string.Equals(a.WindowId, b.WindowId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.ControlPath, b.ControlPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.PropertyName, b.PropertyName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.EventName, b.EventName, StringComparison.OrdinalIgnoreCase);

    internal async Task DispatchEnvelopeAsync(ModEnvelope envelope, CancellationToken cancellationToken)
    {
        string normalizedChannel = envelope.Channel.ToLowerInvariant();
        Func<IClientSession, ModEnvelope, CancellationToken, Task>[] exactHandlers;
        Func<IClientSession, ModEnvelope, CancellationToken, Task>[] wildcardHandlers;
        lock (_gate)
        {
            exactHandlers = _handlers.TryGetValue((normalizedChannel, envelope.MessageType), out var exactList)
                ? [.. exactList]
                : [];
            wildcardHandlers = _handlers.TryGetValue((string.Empty, envelope.MessageType), out var wildcardList)
                ? [.. wildcardList]
                : [];
        }

        await _host.InvokeEnvelopeHandlersAsync(exactHandlers, this, envelope, cancellationToken).ConfigureAwait(false);
        await _host.InvokeEnvelopeHandlersAsync(wildcardHandlers, this, envelope, cancellationToken).ConfigureAwait(false);
    }

    internal Task DispatchUiEventAsync(ClientUiEventNotification uiEvent, CancellationToken cancellationToken) =>
        _ui.DispatchEventAsync(uiEvent, cancellationToken);

    internal Task DispatchTier1SlotButtonClickAsync(Tier1SlotButtonClickPayload payload, CancellationToken cancellationToken) =>
        _phase1.DispatchSlotButtonClickAsync(payload, cancellationToken);

    internal Task DispatchTier1HotkeyTriggeredAsync(ClientHotkeyTriggeredEvent evt, CancellationToken cancellationToken) =>
        _phase1.DispatchHotkeyTriggeredAsync(evt, cancellationToken);

    internal void HandleTier1ScreenStateResponse(Tier1ScreenStateResponsePayload payload) =>
        _phase1.HandleScreenStateResponse(payload);

    internal void HandleTier1HoverStateResponse(Tier1HoverStateResponsePayload payload) =>
        _phase1.HandleHoverStateResponse(payload);

    internal Task DispatchTier1ScreenChangedAsync(ClientScreenChangedEvent evt, CancellationToken cancellationToken) =>
        _phase1.DispatchScreenChangedAsync(evt, cancellationToken);

    internal Task DispatchTier1HoverChangedAsync(ClientHoverStateSnapshot snapshot, CancellationToken cancellationToken) =>
        _phase1.DispatchHoverChangedAsync(snapshot, cancellationToken);

    internal void HandleTier1WorldEntitiesResponse(Tier1WorldEntitiesResponsePayload payload) =>
        _phase2.HandleWorldEntitiesResponse(payload);

    internal void HandleTier1LocalPlayerResponse(Tier1LocalPlayerResponsePayload payload) =>
        _phase2.HandleLocalPlayerResponse(payload);

    internal void HandleTier1EntityStatsResponse(Tier1EntityStatsResponsePayload payload) =>
        _phase2.HandleEntityStatsResponse(payload);

    internal void HandleTier1StatResponse(Tier1StatResponsePayload payload) =>
        _phase2.HandleStatResponse(payload);

    internal void HandleTier1AreaInfoResponse(Tier1AreaInfoResponsePayload payload) =>
        _phase2.HandleAreaInfoResponse(payload);

    internal Task DispatchTier1AreaChangedAsync(ClientAreaChangedEvent evt, CancellationToken cancellationToken) =>
        _phase2.DispatchAreaChangedAsync(evt, cancellationToken);

    internal void HandleTier1ItemFilterStateResponse(Tier1ItemFilterStateResponsePayload payload) =>
        _phase2.HandleItemFilterStateResponse(payload);

    internal void HandleTier1ReloadItemFilterResponse(Tier1ReloadItemFilterResponsePayload payload) =>
        _phase2.HandleReloadItemFilterResponse(payload);

    internal void HandleTier1EvaluateWorldItemFilterResponse(Tier1EvaluateWorldItemFilterResponsePayload payload) =>
        _phase2.HandleEvaluateWorldItemFilterResponse(payload);

    internal Task DispatchTier1FilterReloadedAsync(ItemFilterReloadedEventArgs evt, CancellationToken cancellationToken) =>
        _phase2.DispatchFilterReloadedAsync(evt, cancellationToken);

    internal void HandleTier1CameraStateResponse(Tier1CameraStateResponsePayload payload) =>
        _phase2.HandleCameraStateResponse(payload);

    internal async Task CloseSessionAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _closed, 1);
        lock (_gate)
        {
            _handlers.Clear();
        }

        lock (_batchGate)
        {
            _batchQueue.Clear();
            _batchDepth = 0;
        }

        _phase1.CloseSession();
        _phase2.CloseSession();
        await _ui.CloseAllWindowsAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void CloseSessionNonBlocking()
    {
        Interlocked.Exchange(ref _closed, 1);
        lock (_gate)
        {
            _handlers.Clear();
        }

        lock (_batchGate)
        {
            _batchQueue.Clear();
            _batchDepth = 0;
        }

        _phase1.CloseSession();
        _phase2.CloseSession();
        _ui.CloseAllWindowsNonBlocking();
    }

    private readonly record struct PendingBatchEntry(ModEnvelope Envelope, ServerUiCommand? UiCommand);

    private sealed class ClosedCommandBatch : IClientCommandBatch
    {
        private int _state;

        public int Count => 0;
        public bool IsFlushed => Volatile.Read(ref _state) == 1;
        public bool IsDiscarded => Volatile.Read(ref _state) == 2;

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.CompareExchange(ref _state, 1, 0);
            return Task.CompletedTask;
        }

        public void Discard() => Interlocked.CompareExchange(ref _state, 2, 0);

        public ValueTask DisposeAsync()
        {
            Interlocked.CompareExchange(ref _state, 1, 0);
            return ValueTask.CompletedTask;
        }

        public void Dispose() => Interlocked.CompareExchange(ref _state, 1, 0);
    }

    private sealed class ClientCommandBatch(ClientSession session, int watermark) : IClientCommandBatch
    {
        // 0 = active, 1 = flushed, 2 = discarded
        private int _state;

        public int Count => session.BatchCount;
        public bool IsFlushed => Volatile.Read(ref _state) == 1;
        public bool IsDiscarded => Volatile.Read(ref _state) == 2;

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                return Task.CompletedTask;
            }

            return session.CompleteBatchAsync(cancellationToken);
        }

        public void Discard()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0)
            {
                return;
            }

            session.DiscardFromWatermark(watermark);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                return;
            }

            await session.CompleteBatchAsync(CancellationToken.None).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                return;
            }

            session.CompleteBatchNonBlocking();
        }
    }
}

internal sealed class ClientUiSession(ClientSession session, ClientBridgeHost host) : IClientUiSession
{
    private readonly Dictionary<(string WindowId, string ControlPath, string EventName), List<Func<IClientSession, string?, CancellationToken, Task>>> _eventHandlers = [];
    private readonly Dictionary<string, ServerUiWindowHandle> _openWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private bool _closed;

    public async Task<ServerUiWindowHandle> OpenAsync(string virtualPath, string? windowId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        string resolvedId = !string.IsNullOrWhiteSpace(windowId)
            ? windowId.Trim()
            : Path.GetFileNameWithoutExtension(virtualPath);

        var nextHandle = new ServerUiWindowHandle(this, resolvedId, virtualPath, host.ReportHandlerError);
        ServerUiWindowHandle? replaced = null;
        bool isClosed;
        lock (_gate)
        {
            isClosed = _closed || session.IsClosed;
            if (!isClosed)
            {
                _openWindows.TryGetValue(resolvedId, out replaced);
                _openWindows[resolvedId] = nextHandle;
            }
        }

        if (isClosed)
        {
            await nextHandle.MarkClosedAsync(cancellationToken).ConfigureAwait(false);
            return nextHandle;
        }

        if (replaced is not null)
        {
            await replaced.MarkClosedAsync(cancellationToken).ConfigureAwait(false);
        }

        var cmd = new ServerUiCommand(ServerUiCommandKind.OpenWindow, resolvedId, VirtualPath: virtualPath);
        await session.QueueOrSendUiCommandAsync(cmd, cancellationToken).ConfigureAwait(false);
        return nextHandle;
    }

    public async Task CloseAsync(string windowId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ServerUiWindowHandle? handle;
        lock (_gate)
        {
            _openWindows.Remove(windowId, out handle);
        }

        await session.QueueOrSendUiCommandAsync(new ServerUiCommand(ServerUiCommandKind.CloseWindow, windowId), cancellationToken).ConfigureAwait(false);
        if (handle is not null)
        {
            await handle.MarkClosedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ReloadAsync(string windowId, CancellationToken cancellationToken = default) =>
        session.QueueOrSendUiCommandAsync(new ServerUiCommand(ServerUiCommandKind.ReloadWindow, windowId), cancellationToken);

    public Task SetTextAsync(string windowId, string controlPath, string text, CancellationToken cancellationToken = default) =>
        session.QueueOrSendUiCommandAsync(new ServerUiCommand(ServerUiCommandKind.SetText, windowId, ControlPath: controlPath, Value: text), cancellationToken);

    public Task SetVisibleAsync(string windowId, string controlPath, bool visible, CancellationToken cancellationToken = default) =>
        session.QueueOrSendUiCommandAsync(new ServerUiCommand(ServerUiCommandKind.SetVisible, windowId, ControlPath: controlPath, BoolValue: visible), cancellationToken);

    public Task SetPropertyAsync(string windowId, string controlPath, string propertyName, string value, CancellationToken cancellationToken = default) =>
        session.QueueOrSendUiCommandAsync(new ServerUiCommand(ServerUiCommandKind.SetProperty, windowId, ControlPath: controlPath, PropertyName: propertyName, Value: value), cancellationToken);

    public IDisposable OnButtonClick(string windowId, string controlPath, Action<IClientSession> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnControlEvent(windowId, controlPath, "Clicked", (s, _) => handler(s));
    }

    public IDisposable OnButtonClick(string windowId, string controlPath, Func<IClientSession, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnControlEvent(windowId, controlPath, "Clicked", (s, _) => handler(s));
    }

    public IDisposable OnButtonClick(string windowId, string controlPath, Func<IClientSession, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnControlEvent(windowId, controlPath, "Clicked", (s, _, ct) => handler(s, ct));
    }

    public Task<IDisposable> OnButtonClickAsync(string windowId, string controlPath, Action<IClientSession> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnControlEventAsync(windowId, controlPath, "Clicked", (s, _) => handler(s), cancellationToken);
    }

    public Task<IDisposable> OnButtonClickAsync(string windowId, string controlPath, Func<IClientSession, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnControlEventAsync(windowId, controlPath, "Clicked", (s, _) => handler(s), cancellationToken);
    }

    public Task<IDisposable> OnButtonClickAsync(string windowId, string controlPath, Func<IClientSession, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnControlEventAsync(windowId, controlPath, "Clicked", (s, _, ct) => handler(s, ct), cancellationToken);
    }

    public IDisposable OnControlEvent(string windowId, string controlPath, string eventName, Action<IClientSession, string?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnControlEvent(windowId, controlPath, eventName, (s, val, _) =>
        {
            handler(s, val);
            return Task.CompletedTask;
        });
    }

    public IDisposable OnControlEvent(string windowId, string controlPath, string eventName, Func<IClientSession, string?, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnControlEvent(windowId, controlPath, eventName, (s, val, _) => handler(s, val));
    }

    public IDisposable OnControlEvent(string windowId, string controlPath, string eventName, Func<IClientSession, string?, CancellationToken, Task> handler)
    {
        (IDisposable sub, ServerUiCommand? subscribeCmd) = RegisterControlEventHandler(windowId, controlPath, eventName, handler);
        if (subscribeCmd is not null)
        {
            Task sendTask = session.QueueOrSendUiCommandAsync(subscribeCmd, CancellationToken.None);
            if (sendTask.IsFaulted && sendTask.Exception is not null)
            {
                host.ReportHandlerError($"Ui.SubscribeEvent({windowId}/{controlPath}/{eventName})", sendTask.Exception.GetBaseException());
            }
            else if (!sendTask.IsCompleted)
            {
                _ = sendTask.ContinueWith(
                    t => host.ReportHandlerError($"Ui.SubscribeEvent({windowId}/{controlPath}/{eventName})", t.Exception?.GetBaseException() ?? new InvalidOperationException("SubscribeEvent failed.")),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        return sub;
    }

    public Task<IDisposable> OnControlEventAsync(string windowId, string controlPath, string eventName, Action<IClientSession, string?> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnControlEventAsync(windowId, controlPath, eventName, (s, val, _) =>
        {
            handler(s, val);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public Task<IDisposable> OnControlEventAsync(string windowId, string controlPath, string eventName, Func<IClientSession, string?, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnControlEventAsync(windowId, controlPath, eventName, (s, val, _) => handler(s, val), cancellationToken);
    }

    public async Task<IDisposable> OnControlEventAsync(string windowId, string controlPath, string eventName, Func<IClientSession, string?, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        (IDisposable sub, ServerUiCommand? subscribeCmd) = RegisterControlEventHandler(windowId, controlPath, eventName, handler);
        if (subscribeCmd is not null)
        {
            await session.QueueOrSendUiCommandAsync(subscribeCmd, cancellationToken).ConfigureAwait(false);
        }

        return sub;
    }

    private (IDisposable Subscription, ServerUiCommand? SubscribeCommand) RegisterControlEventHandler(
        string windowId,
        string controlPath,
        string eventName,
        Func<IClientSession, string?, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(handler);

        var key = (windowId.ToLowerInvariant(), controlPath.ToLowerInvariant(), eventName.ToLowerInvariant());
        bool firstForKey;
        lock (_gate)
        {
            if (_closed ||
                session.IsClosed ||
                (_openWindows.TryGetValue(windowId, out ServerUiWindowHandle? openWindow) && !openWindow.IsOpen))
            {
                return (ClientBridgeHost.NoOpDisposable.Instance, null);
            }

            if (!_eventHandlers.TryGetValue(key, out List<Func<IClientSession, string?, CancellationToken, Task>>? list))
            {
                list = [];
                _eventHandlers[key] = list;
                firstForKey = true;
            }
            else
            {
                firstForKey = list.Count == 0;
            }

            list.Add(handler);
        }

        ServerUiCommand? cmd = firstForKey
            ? new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, windowId, ControlPath: controlPath, EventName: eventName)
            : null;

        var subscription = new ClientBridgeHost.Subscription(() =>
        {
            lock (_gate)
            {
                if (_eventHandlers.TryGetValue(key, out List<Func<IClientSession, string?, CancellationToken, Task>>? list))
                {
                    list.Remove(handler);
                    if (list.Count == 0)
                    {
                        _eventHandlers.Remove(key);
                    }
                }
            }
        });

        return (subscription, cmd);
    }

    public Task PlayAudioAsync(string pathOrEvent, CancellationToken cancellationToken = default) =>
        session.QueueOrSendUiCommandAsync(new ServerUiCommand(ServerUiCommandKind.PlayAudio, string.Empty, Value: pathOrEvent), cancellationToken);

    public Task PlayEffectAsync(uint objectId, string effectPath, CancellationToken cancellationToken = default) =>
        session.QueueOrSendUiCommandAsync(new ServerUiCommand(ServerUiCommandKind.PlayEffect, string.Empty, ControlPath: objectId.ToString(System.Globalization.CultureInfo.InvariantCulture), Value: effectPath), cancellationToken);

    public Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(ScreenSlot slot, string id, string text, SlotAttachmentOptions? options = null, CancellationToken cancellationToken = default) =>
        session.Phase1.RegisterSlotButtonAsync(slot, id, text, options, cancellationToken);

    public Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(ScreenSlot slot, string id, string text, Action<IClientSession> onClick, SlotAttachmentOptions? options = null, CancellationToken cancellationToken = default) =>
        session.Phase1.RegisterSlotButtonAsync(slot, id, text, onClick, options, cancellationToken);

    public Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(ScreenSlot slot, string id, string text, Func<IClientSession, Task> onClick, SlotAttachmentOptions? options = null, CancellationToken cancellationToken = default) =>
        session.Phase1.RegisterSlotButtonAsync(slot, id, text, onClick, options, cancellationToken);

    public Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(ScreenSlot slot, string id, string text, Func<IClientSession, CancellationToken, Task> onClick, SlotAttachmentOptions? options = null, CancellationToken cancellationToken = default) =>
        session.Phase1.RegisterSlotButtonAsync(slot, id, text, onClick, options, cancellationToken);

    public Task RemoveSlotButtonAsync(ScreenSlot slot, string id, CancellationToken cancellationToken = default) =>
        session.Phase1.RemoveSlotButtonAsync(slot, id, cancellationToken);

    public Task<IDisposable> OnSlotButtonClickAsync(ScreenSlot slot, string id, Action<IClientSession> handler, CancellationToken cancellationToken = default) =>
        session.Phase1.OnSlotButtonClickAsync(slot, id, handler, cancellationToken);

    public Task<IDisposable> OnSlotButtonClickAsync(ScreenSlot slot, string id, Func<IClientSession, Task> handler, CancellationToken cancellationToken = default) =>
        session.Phase1.OnSlotButtonClickAsync(slot, id, handler, cancellationToken);

    public Task<IDisposable> OnSlotButtonClickAsync(ScreenSlot slot, string id, Func<IClientSession, CancellationToken, Task> handler, CancellationToken cancellationToken = default) =>
        session.Phase1.OnSlotButtonClickAsync(slot, id, handler, cancellationToken);

    public Task ShowToastAsync(string title, string message, ToastSeverity severity = ToastSeverity.Info, int durationMs = 4000, CancellationToken cancellationToken = default) =>
        session.Phase1.ShowToastAsync(title, message, severity, durationMs, cancellationToken);

    public IClientCommandBatch BeginBatch() => session.BeginBatch();

    public Task FlushAsync(CancellationToken cancellationToken = default) => session.FlushAsync(cancellationToken);

    public async Task BatchAsync(Func<IClientUiSession, Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        IClientCommandBatch batch = BeginBatch();
        try
        {
            await action(this).ConfigureAwait(false);
            await batch.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            batch.Discard();
            throw;
        }
    }

    public async Task BatchAsync(Func<IClientUiSession, CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        IClientCommandBatch batch = BeginBatch();
        try
        {
            await action(this, cancellationToken).ConfigureAwait(false);
            await batch.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            batch.Discard();
            throw;
        }
    }

    internal async Task DispatchEventAsync(ClientUiEventNotification uiEvent, CancellationToken cancellationToken)
    {
        if (string.Equals(uiEvent.EventName, "Closed", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(uiEvent.WindowId))
        {
            ServerUiWindowHandle? closedHandle;
            lock (_gate)
            {
                _openWindows.Remove(uiEvent.WindowId, out closedHandle);
            }

            if (closedHandle is not null)
            {
                await closedHandle.MarkClosedAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var key = (uiEvent.WindowId.ToLowerInvariant(), uiEvent.ControlPath.ToLowerInvariant(), uiEvent.EventName.ToLowerInvariant());
        Func<IClientSession, string?, CancellationToken, Task>[] handlers;
        lock (_gate)
        {
            handlers = _eventHandlers.TryGetValue(key, out List<Func<IClientSession, string?, CancellationToken, Task>>? list) ? [.. list] : [];
        }

        foreach (Func<IClientSession, string?, CancellationToken, Task> handler in handlers)
        {
            try
            {
                await handler(session, uiEvent.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                host.ReportHandlerError($"Ui.{uiEvent.EventName}({uiEvent.WindowId}/{uiEvent.ControlPath})", ex);
            }
        }
    }

    internal async Task CloseAllWindowsAsync(CancellationToken cancellationToken)
    {
        ServerUiWindowHandle[] handles;
        lock (_gate)
        {
            _closed = true;
            handles = [.. _openWindows.Values];
            _openWindows.Clear();
            _eventHandlers.Clear();
        }

        foreach (ServerUiWindowHandle handle in handles)
        {
            await handle.MarkClosedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal void CloseAllWindowsNonBlocking()
    {
        ServerUiWindowHandle[] handles;
        lock (_gate)
        {
            _closed = true;
            handles = [.. _openWindows.Values];
            _openWindows.Clear();
            _eventHandlers.Clear();
        }

        foreach (ServerUiWindowHandle handle in handles)
        {
            handle.MarkClosedNonBlocking();
        }
    }
}
