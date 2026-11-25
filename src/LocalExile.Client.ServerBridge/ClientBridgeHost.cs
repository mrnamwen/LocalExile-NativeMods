using System.Collections.Concurrent;
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
public sealed class ClientBridgeHost
{
    private readonly IGatewayPacketSink _sink;
    private readonly ClientBridgeOptions _options;
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Channel, string MessageType), List<Action<IClientSession, ModEnvelope>>> _globalHandlers = [];
    private readonly Lock _gate = new();
    private long _sequence;

    /// <summary>Active bridge configuration options.</summary>
    public ClientBridgeOptions Options => _options;

    /// <summary>Snapshot of all tracked sessions.</summary>
    public IReadOnlyCollection<IClientSession> Sessions => _sessions.Values.ToArray();

    /// <summary>Raised when a client completes a NativeMods handshake.</summary>
    public event Action<IClientSession>? ClientReady;

    /// <summary>Raised whenever any valid <see cref="ModEnvelope"/> arrives from a client.</summary>
    public event Action<IClientSession, ModEnvelope>? MessageReceived;

    /// <summary>Creates a new <see cref="ClientBridgeHost"/> writing outbound traffic through <paramref name="sink"/>.</summary>
    public ClientBridgeHost(IGatewayPacketSink sink, ClientBridgeOptions? options = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? new ClientBridgeOptions();
        if (_options.HostRulesetId > IRulesetRegistry.MaxRulesetId)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"HostRulesetId 0x{_options.HostRulesetId:X2} exceeds maximum 0x{IRulesetRegistry.MaxRulesetId:X2}.");
        }
    }

    /// <summary>Gets or creates the <see cref="IClientSession"/> for <paramref name="connectionId"/>.</summary>
    public IClientSession For(string connectionId) => GetOrCreateSession(connectionId);

    /// <summary>Gets or creates the <see cref="IClientSession"/> for <paramref name="connectionId"/>.</summary>
    public IClientSession GetOrCreateSession(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return _sessions.GetOrAdd(connectionId, id => new ClientSession(this, id, _options.DefaultTransport));
    }

    /// <summary>Removes a closed connection's session state.</summary>
    public bool RemoveSession(string connectionId) =>
        !string.IsNullOrWhiteSpace(connectionId) && _sessions.TryRemove(connectionId, out _);

    /// <summary>
    /// Subscribes to incoming typed messages from any connected client on <paramref name="channel"/>.
    /// </summary>
    public IDisposable On<T>(string channel, Action<IClientSession, T> handler, string? messageType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);
        string typeName = string.IsNullOrWhiteSpace(messageType) ? typeof(T).Name : messageType;
        var key = (channel.ToLowerInvariant(), typeName);
        Action<IClientSession, ModEnvelope> wrapper = (session, env) =>
        {
            T payload = MessageCodec.DeserializePayload<T>(env);
            handler(session, payload);
        };

        lock (_gate)
        {
            if (!_globalHandlers.TryGetValue(key, out List<Action<IClientSession, ModEnvelope>>? list))
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
                if (_globalHandlers.TryGetValue(key, out List<Action<IClientSession, ModEnvelope>>? list))
                {
                    list.Remove(wrapper);
                }
            }
        });
    }

    /// <summary>Sends a typed message to <paramref name="connectionId"/> on <paramref name="channel"/>.</summary>
    public Task SendAsync<T>(string connectionId, string channel, T message, string? messageType = null, CancellationToken cancellationToken = default) =>
        GetOrCreateSession(connectionId).SendAsync(channel, message, messageType, cancellationToken);

    /// <summary>
    /// Handles an incoming C→S packet from LocalExile's <c>GameEvents.Packet(0x02CA)</c> hook.
    /// Returns <c>true</c> if the packet belonged to NativeMods and was consumed; <c>false</c> so stock handlers continue running.
    /// </summary>
    /// <param name="connectionId">Source player connection identifier.</param>
    /// <param name="opcode">Incoming wire opcode (expected <c>0x02CA</c> for C→S ruleset traffic).</param>
    /// <param name="payload">Packet body bytes (without the 2-byte opcode prefix: <c>[u8 rulesetId][u16 BE len][envelope]</c>).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public async Task<bool> HandleIncomingPacketAsync(
        string connectionId,
        ushort opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (opcode != IOpcodeRegistry.ClientRulesetOpcode)
        {
            return false;
        }

        if (!MessageCodec.TryDecodeRulesetBody(payload, out byte rulesetId, out ReadOnlyMemory<byte> inner)
            || rulesetId != _options.HostRulesetId)
        {
            return false;
        }

        ModEnvelope envelope;
        try
        {
            envelope = MessageCodec.DecodeEnvelope(inner.Span);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        ClientSession session = (ClientSession)GetOrCreateSession(connectionId);
        session.SetActiveTransport(NetTransportKind.HostRuleset);
        await DispatchIncomingEnvelopeAsync(session, envelope, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Handles an incoming C→S chat command line (<c>/nm env &lt;base64&gt;</c>) for the chat fallback transport.
    /// Returns <c>true</c> if the command was a NativeMods envelope and was consumed.
    /// </summary>
    public async Task<bool> HandleIncomingChatCommandAsync(
        string connectionId,
        string commandLine,
        CancellationToken cancellationToken = default)
    {
        if (!MessageCodec.TryDecodeClientChatEnvelope(commandLine, out ModEnvelope? envelope) || envelope is null)
        {
            return false;
        }

        ClientSession session = (ClientSession)GetOrCreateSession(connectionId);
        session.SetActiveTransport(NetTransportKind.ChatFallback);
        await DispatchIncomingEnvelopeAsync(session, envelope, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal async Task SendEnvelopeAsync(ClientSession session, ModEnvelope envelope, CancellationToken cancellationToken)
    {
        if (session.ActiveTransport == NetTransportKind.ChatFallback)
        {
            string line = MessageCodec.EncodeServerChatEnvelope(envelope);
            await _sink.SendChatReplyAsync(session.ConnectionId, line, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] inner = MessageCodec.EncodeEnvelope(envelope);
        byte[] rulesetBody = MessageCodec.EncodeRulesetBody(_options.HostRulesetId, inner);
        await _sink.SendPacketAsync(session.ConnectionId, IOpcodeRegistry.ServerRulesetOpcode, rulesetBody, cancellationToken).ConfigureAwait(false);
    }

    internal long NextSequence() => Interlocked.Increment(ref _sequence);

    private async Task DispatchIncomingEnvelopeAsync(ClientSession session, ModEnvelope envelope, CancellationToken cancellationToken)
    {
        MessageReceived?.Invoke(session, envelope);

        if (string.Equals(envelope.Channel, MessageCodec.SystemChannel, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(envelope.MessageType, nameof(ClientHandshake), StringComparison.Ordinal))
            {
                ClientHandshake handshake = MessageCodec.DeserializePayload<ClientHandshake>(envelope);
                session.ApplyHandshake(handshake);

                var reply = new ServerHandshake(
                    ServerVersion: _options.ServerVersion,
                    BridgeVersion: _options.BridgeVersion,
                    ProtocolVersion: Sdk.SdkVersion.ProtocolVersion,
                    HostRulesetId: _options.HostRulesetId,
                    PreferredTransport: session.ActiveTransport,
                    RequiredMods: _options.RequiredMods,
                    ServerCapabilities: _options.ServerCapabilities);

                await session.SendAsync(MessageCodec.SystemChannel, reply, cancellationToken: cancellationToken).ConfigureAwait(false);
                ClientReady?.Invoke(session);
                return;
            }

            if (string.Equals(envelope.MessageType, nameof(ClientUiEventNotification), StringComparison.Ordinal))
            {
                ClientUiEventNotification uiEvent = MessageCodec.DeserializePayload<ClientUiEventNotification>(envelope);
                session.DispatchUiEvent(uiEvent);
                return;
            }
        }

        session.DispatchEnvelope(envelope);

        var key = (envelope.Channel.ToLowerInvariant(), envelope.MessageType);
        Action<IClientSession, ModEnvelope>[] globals;
        lock (_gate)
        {
            globals = _globalHandlers.TryGetValue(key, out List<Action<IClientSession, ModEnvelope>>? list) ? [.. list] : [];
        }

        foreach (Action<IClientSession, ModEnvelope> handler in globals)
        {
            handler(session, envelope);
        }
    }

    internal sealed class Subscription(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
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
    private readonly Dictionary<(string Channel, string MessageType), List<Action<IClientSession, ModEnvelope>>> _handlers = [];
    private readonly Lock _gate = new();

    public string ConnectionId { get; }
    public bool HasNativeHost => Handshake is not null;
    public ClientHandshake? Handshake { get; private set; }
    public NetTransportKind ActiveTransport { get; private set; }
    public ClientBuildId ClientBuild => Handshake?.Build ?? ClientBuildId.Unknown;
    public string? ExtenderVersion => Handshake?.ExtenderVersion;
    public string? ClientSdkVersion => Handshake?.SdkVersion;
    public IReadOnlyCollection<InstalledClientModInfo> InstalledMods => Handshake?.InstalledMods ?? [];
    public IClientUiSession Ui => _ui;

    public ClientSession(ClientBridgeHost host, string connectionId, NetTransportKind defaultTransport)
    {
        _host = host;
        ConnectionId = connectionId;
        ActiveTransport = defaultTransport;
        _ui = new ClientUiSession(this);
    }

    internal void SetActiveTransport(NetTransportKind kind) => ActiveTransport = kind;

    internal void ApplyHandshake(ClientHandshake handshake) => Handshake = handshake;

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
        ModEnvelope envelope = MessageCodec.CreateTypedEnvelope(channel, message, messageType, _host.NextSequence());
        return _host.SendEnvelopeAsync(this, envelope, cancellationToken);
    }

    public IDisposable On<T>(string channel, Action<IClientSession, T> handler, string? messageType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);
        string typeName = string.IsNullOrWhiteSpace(messageType) ? typeof(T).Name : messageType;
        var key = (channel.ToLowerInvariant(), typeName);
        Action<IClientSession, ModEnvelope> wrapper = (session, env) =>
        {
            T payload = MessageCodec.DeserializePayload<T>(env);
            handler(session, payload);
        };

        lock (_gate)
        {
            if (!_handlers.TryGetValue(key, out List<Action<IClientSession, ModEnvelope>>? list))
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
                if (_handlers.TryGetValue(key, out List<Action<IClientSession, ModEnvelope>>? list))
                {
                    list.Remove(wrapper);
                }
            }
        });
    }

    internal void DispatchEnvelope(ModEnvelope envelope)
    {
        var key = (envelope.Channel.ToLowerInvariant(), envelope.MessageType);
        Action<IClientSession, ModEnvelope>[] snapshot;
        lock (_gate)
        {
            snapshot = _handlers.TryGetValue(key, out List<Action<IClientSession, ModEnvelope>>? list) ? [.. list] : [];
        }

        foreach (Action<IClientSession, ModEnvelope> handler in snapshot)
        {
            handler(this, envelope);
        }
    }

    internal void DispatchUiEvent(ClientUiEventNotification uiEvent) => _ui.DispatchEvent(uiEvent);
}

internal sealed class ClientUiSession(ClientSession session) : IClientUiSession
{
    private readonly Dictionary<(string WindowId, string ControlPath, string EventName), List<Action<IClientSession, string?>>> _eventHandlers = [];
    private readonly Lock _gate = new();

    public async Task<ServerUiWindowHandle> OpenAsync(string virtualPath, string? windowId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        string resolvedId = !string.IsNullOrWhiteSpace(windowId)
            ? windowId.Trim()
            : Path.GetFileNameWithoutExtension(virtualPath);

        var cmd = new ServerUiCommand(ServerUiCommandKind.OpenWindow, resolvedId, VirtualPath: virtualPath);
        await session.SendAsync(MessageCodec.SystemChannel, cmd, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new ServerUiWindowHandle(this, resolvedId, virtualPath);
    }

    public Task CloseAsync(string windowId, CancellationToken cancellationToken = default) =>
        session.SendAsync(MessageCodec.SystemChannel, new ServerUiCommand(ServerUiCommandKind.CloseWindow, windowId), cancellationToken: cancellationToken);

    public Task ReloadAsync(string windowId, CancellationToken cancellationToken = default) =>
        session.SendAsync(MessageCodec.SystemChannel, new ServerUiCommand(ServerUiCommandKind.ReloadWindow, windowId), cancellationToken: cancellationToken);

    public Task SetTextAsync(string windowId, string controlPath, string text, CancellationToken cancellationToken = default) =>
        session.SendAsync(MessageCodec.SystemChannel, new ServerUiCommand(ServerUiCommandKind.SetText, windowId, ControlPath: controlPath, Value: text), cancellationToken: cancellationToken);

    public Task SetVisibleAsync(string windowId, string controlPath, bool visible, CancellationToken cancellationToken = default) =>
        session.SendAsync(MessageCodec.SystemChannel, new ServerUiCommand(ServerUiCommandKind.SetVisible, windowId, ControlPath: controlPath, BoolValue: visible), cancellationToken: cancellationToken);

    public Task SetPropertyAsync(string windowId, string controlPath, string propertyName, string value, CancellationToken cancellationToken = default) =>
        session.SendAsync(MessageCodec.SystemChannel, new ServerUiCommand(ServerUiCommandKind.SetProperty, windowId, ControlPath: controlPath, PropertyName: propertyName, Value: value), cancellationToken: cancellationToken);

    public IDisposable OnButtonClick(string windowId, string controlPath, Action<IClientSession> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnControlEvent(windowId, controlPath, "Clicked", (s, _) => handler(s));
    }

    public IDisposable OnControlEvent(string windowId, string controlPath, string eventName, Action<IClientSession, string?> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(handler);

        var key = (windowId.ToLowerInvariant(), controlPath.ToLowerInvariant(), eventName.ToLowerInvariant());
        bool firstForKey;
        lock (_gate)
        {
            if (!_eventHandlers.TryGetValue(key, out List<Action<IClientSession, string?>>? list))
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

        if (firstForKey)
        {
            var cmd = new ServerUiCommand(ServerUiCommandKind.SubscribeEvent, windowId, ControlPath: controlPath, EventName: eventName);
            session.SendAsync(MessageCodec.SystemChannel, cmd).GetAwaiter().GetResult();
        }

        return new ClientBridgeHost.Subscription(() =>
        {
            lock (_gate)
            {
                if (_eventHandlers.TryGetValue(key, out List<Action<IClientSession, string?>>? list))
                {
                    list.Remove(handler);
                }
            }
        });
    }

    public Task PlayAudioAsync(string pathOrEvent, CancellationToken cancellationToken = default) =>
        session.SendAsync(MessageCodec.SystemChannel, new ServerUiCommand(ServerUiCommandKind.PlayAudio, string.Empty, Value: pathOrEvent), cancellationToken: cancellationToken);

    public Task PlayEffectAsync(uint objectId, string effectPath, CancellationToken cancellationToken = default) =>
        session.SendAsync(MessageCodec.SystemChannel, new ServerUiCommand(ServerUiCommandKind.PlayEffect, string.Empty, ControlPath: objectId.ToString(System.Globalization.CultureInfo.InvariantCulture), Value: effectPath), cancellationToken: cancellationToken);

    internal void DispatchEvent(ClientUiEventNotification uiEvent)
    {
        var key = (uiEvent.WindowId.ToLowerInvariant(), uiEvent.ControlPath.ToLowerInvariant(), uiEvent.EventName.ToLowerInvariant());
        Action<IClientSession, string?>[] handlers;
        lock (_gate)
        {
            handlers = _eventHandlers.TryGetValue(key, out List<Action<IClientSession, string?>>? list) ? [.. list] : [];
        }

        foreach (Action<IClientSession, string?> handler in handlers)
        {
            handler(session, uiEvent.Value);
        }
    }
}
