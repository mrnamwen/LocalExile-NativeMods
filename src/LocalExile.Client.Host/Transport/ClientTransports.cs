using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;

namespace LocalExile.Client.Host.Transport;

/// <summary>
/// Pluggable client-side wire transport carrying <see cref="ModEnvelope"/> frames to and from the LocalExile server.
/// </summary>
public interface IClientTransport : IDisposable
{
    /// <summary>Describes the active transport configuration.</summary>
    NetTransportInfo Info { get; }

    /// <summary>Raised when a complete <see cref="ModEnvelope"/> arrives from the server.</summary>
    event Action<ModEnvelope>? EnvelopeReceived;

    /// <summary>Encodes and sends an envelope to the server.</summary>
    void SendEnvelope(ModEnvelope envelope);
}

/// <summary>
/// Primary NativeMods transport riding an unused slot in the 3.29 ruleset creator array:
/// S→C <c>0x2c9</c> (length-delimited by client filter, max 65535 bytes) and C→S <c>0x2ca</c>.
/// Stock clients without the extender simply log one line and drop traffic safely.
/// </summary>
public sealed class HostRulesetTransport : IClientTransport
{
    private readonly INativeBridge _bridge;
    private readonly byte _rulesetId;
    private readonly IDisposable _subscription;

    /// <inheritdoc/>
    public NetTransportInfo Info { get; }

    /// <inheritdoc/>
    public event Action<ModEnvelope>? EnvelopeReceived;

    /// <summary>Creates a new <see cref="HostRulesetTransport"/> bound to <paramref name="rulesetId"/>.</summary>
    public HostRulesetTransport(INativeBridge bridge, byte rulesetId = IRulesetRegistry.DefaultHostChannelRulesetId)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        if (rulesetId > IRulesetRegistry.MaxRulesetId)
        {
            throw new ArgumentOutOfRangeException(nameof(rulesetId));
        }

        _bridge = bridge;
        _rulesetId = rulesetId;
        Info = new NetTransportInfo(
            NetTransportKind.HostRuleset,
            IncomingOpcode: IOpcodeRegistry.ServerRulesetOpcode,
            OutgoingOpcode: IOpcodeRegistry.ClientRulesetOpcode,
            RulesetId: rulesetId,
            MaxPayloadBytes: MessageCodec.MaxRulesetPayloadBytes,
            IsConnected: true);

        _subscription = _bridge.RegisterRulesetReceiver(_rulesetId, OnRawRulesetPayload);
    }

    /// <inheritdoc/>
    public void SendEnvelope(ModEnvelope envelope)
    {
        byte[] encoded = MessageCodec.EncodeEnvelope(envelope);
        if (encoded.Length > MessageCodec.MaxRulesetPayloadBytes)
        {
            throw new InvalidOperationException(
                $"Encoded envelope ({encoded.Length} bytes) exceeds 3.29 ruleset payload limit ({MessageCodec.MaxRulesetPayloadBytes} bytes).");
        }

        _bridge.SendRulesetPacket(_rulesetId, encoded);
    }

    private void OnRawRulesetPayload(ReadOnlyMemory<byte> payload)
    {
        try
        {
            ModEnvelope env = MessageCodec.DecodeEnvelope(payload.Span);
            EnvelopeReceived?.Invoke(env);
        }
        catch (InvalidDataException)
        {
            // Ignore malformed ruleset frames rather than crashing the connection.
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _subscription.Dispose();
}

/// <summary>
/// Chat fallback transport matching spike S4:
/// S→C <c>0x17</c> chat lines prefixed with <c>##NM </c> are intercepted and hidden from chat;
/// C→S <c>0x15</c> chat commands prefixed with <c>/nm </c> carry encoded envelopes back to the server.
/// </summary>
public sealed class ChatFallbackTransport : IClientTransport
{
    private readonly INativeBridge _bridge;
    private readonly IDisposable _subscription;

    /// <inheritdoc/>
    public NetTransportInfo Info { get; }

    /// <inheritdoc/>
    public event Action<ModEnvelope>? EnvelopeReceived;

    /// <summary>Creates a new <see cref="ChatFallbackTransport"/>.</summary>
    public ChatFallbackTransport(INativeBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _bridge = bridge;
        Info = new NetTransportInfo(
            NetTransportKind.ChatFallback,
            IncomingOpcode: 0x0017,
            OutgoingOpcode: 0x0015,
            RulesetId: null,
            MaxPayloadBytes: 2048,
            IsConnected: true);

        _subscription = _bridge.RegisterChatInterceptor(OnIncomingChat);
    }

    /// <inheritdoc/>
    public void SendEnvelope(ModEnvelope envelope)
    {
        string cmd = MessageCodec.EncodeClientChatEnvelope(envelope);
        _bridge.SendChatLine(cmd);
    }

    private bool OnIncomingChat(string sender, string text)
    {
        if (!MessageCodec.TryDecodeServerChatEnvelope(text, out ModEnvelope? envelope) || envelope is null)
        {
            return false;
        }

        EnvelopeReceived?.Invoke(envelope);
        return true; // Suppress ##NM line from the in-game chat box.
    }

    /// <inheritdoc/>
    public void Dispose() => _subscription.Dispose();
}

/// <summary>
/// Central envelope router dispatching incoming <see cref="ModEnvelope"/> messages to per-channel <see cref="ClientNetChannel"/> instances.
/// </summary>
public sealed class ClientChannelRouter : IDisposable
{
    private readonly INativeBridge _bridge;
    private readonly IOpcodeRegistry _opcodes;
    private readonly Dictionary<(string Channel, string MessageType), List<Action<ModEnvelope>>> _typedHandlers = [];
    private readonly Lock _gate = new();
    private IClientTransport _transport;
    private long _sequence;

    /// <summary>Creates a router over <paramref name="transport"/>.</summary>
    public ClientChannelRouter(INativeBridge bridge, IOpcodeRegistry opcodes, IClientTransport transport)
    {
        _bridge = bridge;
        _opcodes = opcodes;
        _transport = transport;
        _transport.EnvelopeReceived += DispatchEnvelope;
    }

    /// <summary>Gets the active underlying transport info.</summary>
    public NetTransportInfo TransportInfo => _transport.Info;

    /// <summary>Switches the active transport (for example falling back from HostRuleset to ChatFallback).</summary>
    public void SwitchTransport(IClientTransport nextTransport)
    {
        ArgumentNullException.ThrowIfNull(nextTransport);
        lock (_gate)
        {
            _transport.EnvelopeReceived -= DispatchEnvelope;
            _transport.Dispose();
            _transport = nextTransport;
            _transport.EnvelopeReceived += DispatchEnvelope;
        }
    }

    /// <summary>Creates a scoped <see cref="INetChannel"/> for a mod or system channel.</summary>
    public INetChannel CreateChannel(string channelId, Action<string, Action>? faultGuard = null) =>
        new ClientNetChannel(this, _bridge, _opcodes, channelId, faultGuard);

    internal void SendTyped<T>(string channelId, T payload, string? messageType)
    {
        long seq = Interlocked.Increment(ref _sequence);
        ModEnvelope envelope = MessageCodec.CreateTypedEnvelope(channelId, payload, messageType, seq);
        _transport.SendEnvelope(envelope);
    }

    internal IDisposable SubscribeEnvelope(string channelId, string messageType, Action<ModEnvelope> handler)
    {
        var key = (channelId.ToLowerInvariant(), messageType);
        lock (_gate)
        {
            if (!_typedHandlers.TryGetValue(key, out List<Action<ModEnvelope>>? list))
            {
                list = [];
                _typedHandlers[key] = list;
            }

            list.Add(handler);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (_typedHandlers.TryGetValue(key, out List<Action<ModEnvelope>>? list))
                {
                    list.Remove(handler);
                }
            }
        });
    }

    internal void DispatchEnvelope(ModEnvelope envelope)
    {
        var key = (envelope.Channel.ToLowerInvariant(), envelope.MessageType);
        Action<ModEnvelope>[] handlers;
        lock (_gate)
        {
            handlers = _typedHandlers.TryGetValue(key, out List<Action<ModEnvelope>>? list) ? [.. list] : [];
        }

        foreach (Action<ModEnvelope> handler in handlers)
        {
            handler(envelope);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _transport.EnvelopeReceived -= DispatchEnvelope;
        _transport.Dispose();
    }
}

internal sealed class ClientNetChannel(
    ClientChannelRouter router,
    INativeBridge bridge,
    IOpcodeRegistry opcodes,
    string channelId,
    Action<string, Action>? faultGuard) : INetChannel
{
    public string ChannelId => channelId;

    public NetTransportInfo Transport => router.TransportInfo;

    public void Send<T>(T message, string? messageType = null) =>
        router.SendTyped(channelId, message, messageType);

    public void SendRaw(ushort opcode, ReadOnlySpan<byte> payload) =>
        bridge.SendRawPacket(opcode, payload);

    public IDisposable On<T>(Action<T> handler, string? messageType = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        string typeName = string.IsNullOrWhiteSpace(messageType) ? typeof(T).Name : messageType;
        return router.SubscribeEnvelope(channelId, typeName, env =>
        {
            T payload = MessageCodec.DeserializePayload<T>(env);
            if (faultGuard is not null)
            {
                faultGuard(channelId, () => handler(payload));
            }
            else
            {
                handler(payload);
            }
        });
    }

    public IDisposable OnRaw(ushort opcode, Action<ReadOnlyMemory<byte>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return opcodes.RegisterHandler(opcode, data =>
        {
            if (faultGuard is not null)
            {
                faultGuard(channelId, () => handler(data));
            }
            else
            {
                handler(data);
            }
        }, channelId);
    }
}
