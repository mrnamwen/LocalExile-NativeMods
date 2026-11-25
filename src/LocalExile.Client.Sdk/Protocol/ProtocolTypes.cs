using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;

namespace LocalExile.Client.Sdk.Protocol;

/// <summary>
/// Self-delimiting cross-side message envelope transported over HostRuleset (<c>0x2c9</c>/<c>0x2ca</c>),
/// reserved opcodes (<c>0x2fc+</c>), or the chat fallback (<c>##NM </c> / <c>/nm </c>).
/// </summary>
/// <param name="Channel">Logical channel id (mod id, or <see cref="MessageCodec.SystemChannel"/> for host control/handshake/UI).</param>
/// <param name="MessageType">Message type discriminator (typically the record CLR name).</param>
/// <param name="PayloadJson">UTF-8 JSON serialized payload.</param>
/// <param name="ProtocolVersion">Wire protocol version (<see cref="SdkVersion.ProtocolVersion"/>).</param>
/// <param name="Sequence">Monotonic per-direction sequence number.</param>
public sealed record ModEnvelope(
    string Channel,
    string MessageType,
    string PayloadJson,
    int ProtocolVersion = SdkVersion.ProtocolVersion,
    long Sequence = 0);

/// <summary>
/// Summary of an installed client mod reported by the client host during handshake negotiation.
/// </summary>
/// <param name="Id">Unique mod identifier.</param>
/// <param name="Version">Mod version string.</param>
/// <param name="PayloadHash">Optional SHA-256 digest of the mod's client payload.</param>
public sealed record InstalledClientModInfo(
    string Id,
    string Version,
    string? PayloadHash = null);

/// <summary>
/// Handshake sent from the client host to the LocalExile server upon entering an area or connecting.
/// </summary>
public sealed record ClientHandshake(
    string ExtenderVersion,
    string SdkVersion,
    ClientBuildId Build,
    string BuildVersion,
    int ProtocolVersion,
    IReadOnlyList<InstalledClientModInfo> InstalledMods,
    IReadOnlyList<string> Capabilities);

/// <summary>
/// Handshake response sent from the LocalExile server bridge to the client host.
/// </summary>
public sealed record ServerHandshake(
    string ServerVersion,
    string BridgeVersion,
    int ProtocolVersion,
    byte HostRulesetId,
    NetTransportKind PreferredTransport,
    IReadOnlyList<string> RequiredMods,
    IReadOnlyList<string> ServerCapabilities);

/// <summary>
/// Closed set of server-driven client UI/media operations available even without a custom client mod DLL.
/// </summary>
public enum ServerUiCommandKind
{
    /// <summary>Open a <c>.ui</c> window by virtual path.</summary>
    OpenWindow = 0,

    /// <summary>Close an open window by id.</summary>
    CloseWindow = 1,

    /// <summary>Reload an open window from its <c>.ui</c> overlay.</summary>
    ReloadWindow = 2,

    /// <summary>Update the text of a control inside a window.</summary>
    SetText = 3,

    /// <summary>Show or hide a control inside a window.</summary>
    SetVisible = 4,

    /// <summary>Set an arbitrary string property on a control.</summary>
    SetProperty = 5,

    /// <summary>Subscribe to an interactive control event (for example <c>Clicked</c>, <c>CheckedChanged</c>, <c>Submitted</c>).</summary>
    SubscribeEvent = 6,

    /// <summary>Play an FMOD event or <c>.ogg</c> sound file.</summary>
    PlayAudio = 7,

    /// <summary>Play a visual effect on a world object.</summary>
    PlayEffect = 8,
}

/// <summary>
/// Server-to-client command driving native UI windows and presentation without requiring custom client code.
/// </summary>
public sealed record ServerUiCommand(
    ServerUiCommandKind Kind,
    string WindowId,
    string? VirtualPath = null,
    string? ControlPath = null,
    string? PropertyName = null,
    string? Value = null,
    bool? BoolValue = null,
    string? EventName = null);

/// <summary>
/// Client-to-server notification emitted when a player interacts with a server-driven UI control.
/// </summary>
public sealed record ClientUiEventNotification(
    string WindowId,
    string ControlPath,
    string EventName,
    string? Value = null);

/// <summary>
/// Binary and chat-fallback wire codec for <see cref="ModEnvelope"/>, ruleset packets (<c>0x2c9</c>/<c>0x2ca</c>),
/// and GEAL function hashes (<c>0x285</c>).
/// </summary>
public static class MessageCodec
{
    /// <summary>Reserved channel identifier used for host handshake and server-driven UI traffic.</summary>
    public const string SystemChannel = "__host";

    /// <summary>4-byte ASCII magic header (<c>NMOD</c>) prefixing binary encoded envelopes.</summary>
    public const uint EnvelopeMagic = 0x4E4D4F44;

    /// <summary>Server-to-client chat prefix intercepted and hidden by the client host (spike S4).</summary>
    public const string ServerChatPrefix = "##NM ";

    /// <summary>Client-to-server chat command prefix routed to the server bridge (spike S4).</summary>
    public const string ClientChatCommandPrefix = "/nm ";

    /// <summary>Maximum payload length accepted by the 3.29 client ruleset filter (<c>0x2c9</c>).</summary>
    public const int MaxRulesetPayloadBytes = 0xFFFF;

    /// <summary>Shared JSON serializer options used by SDK and ServerBridge for non-collectible types.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    // Ephemeron table isolating System.Text.Json's JsonTypeInfo cache per collectible AssemblyLoadContext
    // so serializing/deserializing mod-defined message records never pins a client mod's ALC on hot reload / unload.
    private static readonly ConditionalWeakTable<AssemblyLoadContext, JsonSerializerOptions> CollectibleJsonOptions = [];

    private static JsonSerializerOptions CreateJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>
    /// Returns a <see cref="JsonSerializerOptions"/> instance safe for <paramref name="type"/>:
    /// collectible <see cref="AssemblyLoadContext"/> types get a dedicated <see cref="DefaultJsonTypeInfoResolver"/>
    /// scoped to that load context so unloading a client mod releases all cached metadata cleanly.
    /// </summary>
    public static JsonSerializerOptions GetJsonOptionsForType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        AssemblyLoadContext? collectibleAlc = FindCollectibleLoadContext(type);
        return collectibleAlc is not null
            ? CollectibleJsonOptions.GetValue(collectibleAlc, static _ => CreateJsonOptions())
            : JsonOptions;
    }

    private static AssemblyLoadContext? FindCollectibleLoadContext(Type type)
    {
        AssemblyLoadContext? alc = AssemblyLoadContext.GetLoadContext(type.Assembly);
        if (alc is not null && alc.IsCollectible)
        {
            return alc;
        }

        if (type.HasElementType && type.GetElementType() is { } elemType)
        {
            AssemblyLoadContext? elemAlc = FindCollectibleLoadContext(elemType);
            if (elemAlc is not null)
            {
                return elemAlc;
            }
        }

        if (type.IsGenericType)
        {
            foreach (Type arg in type.GetGenericArguments())
            {
                AssemblyLoadContext? argAlc = FindCollectibleLoadContext(arg);
                if (argAlc is not null)
                {
                    return argAlc;
                }
            }
        }

        return null;
    }

    /// <summary>Creates a <see cref="ModEnvelope"/> containing the JSON serialization of <paramref name="payload"/>.</summary>
    public static ModEnvelope CreateTypedEnvelope<T>(string channel, T payload, string? messageType = null, long sequence = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        string typeName = string.IsNullOrWhiteSpace(messageType) ? typeof(T).Name : messageType;
        Type runtimeType = payload is not null ? payload.GetType() : typeof(T);
        string json = FindCollectibleLoadContext(runtimeType) is not null
            ? CollectibleJsonCodec.Serialize(payload, runtimeType)
            : JsonSerializer.Serialize(payload, JsonOptions);
        return new ModEnvelope(channel, typeName, json, SdkVersion.ProtocolVersion, sequence);
    }

    /// <summary>Deserializes the JSON payload of <paramref name="envelope"/> into <typeparamref name="T"/>.</summary>
    public static T DeserializePayload<T>(ModEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        T? value = FindCollectibleLoadContext(typeof(T)) is not null
            ? (T?)CollectibleJsonCodec.Deserialize(envelope.PayloadJson, typeof(T))
            : JsonSerializer.Deserialize<T>(envelope.PayloadJson, JsonOptions);
        return value is not null
            ? value
            : throw new InvalidDataException($"Failed to deserialize envelope payload for '{envelope.Channel}/{envelope.MessageType}' into {typeof(T).Name}.");
    }

    /// <summary>
    /// Encodes a <see cref="ModEnvelope"/> into a compact big-endian self-delimiting binary frame:
    /// <c>[u32 magic][u16 protocolVersion][u16 channelLen][utf8 channel][u16 typeLen][utf8 type][u32 payloadLen][utf8 payload][i64 sequence]</c>.
    /// </summary>
    public static byte[] EncodeEnvelope(ModEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        byte[] channelBytes = Encoding.UTF8.GetBytes(envelope.Channel);
        byte[] typeBytes = Encoding.UTF8.GetBytes(envelope.MessageType);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(envelope.PayloadJson);

        if (channelBytes.Length > ushort.MaxValue)
        {
            throw new ArgumentException("Channel name exceeds maximum UTF-8 byte length of 65535.", nameof(envelope));
        }

        if (typeBytes.Length > ushort.MaxValue)
        {
            throw new ArgumentException("Message type exceeds maximum UTF-8 byte length of 65535.", nameof(envelope));
        }

        int totalLength = 4 + 2 + 2 + channelBytes.Length + 2 + typeBytes.Length + 4 + payloadBytes.Length + 8;
        byte[] buffer = new byte[totalLength];
        Span<byte> span = buffer;
        int offset = 0;

        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, 4), EnvelopeMagic);
        offset += 4;
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(offset, 2), checked((ushort)envelope.ProtocolVersion));
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(offset, 2), (ushort)channelBytes.Length);
        offset += 2;
        channelBytes.CopyTo(span.Slice(offset, channelBytes.Length));
        offset += channelBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(offset, 2), (ushort)typeBytes.Length);
        offset += 2;
        typeBytes.CopyTo(span.Slice(offset, typeBytes.Length));
        offset += typeBytes.Length;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, 4), (uint)payloadBytes.Length);
        offset += 4;
        payloadBytes.CopyTo(span.Slice(offset, payloadBytes.Length));
        offset += payloadBytes.Length;
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(offset, 8), envelope.Sequence);

        return buffer;
    }

    /// <summary>Decodes a binary frame created by <see cref="EncodeEnvelope"/>.</summary>
    public static ModEnvelope DecodeEnvelope(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4 + 2 + 2 + 2 + 4 + 8)
        {
            throw new InvalidDataException("Envelope frame is too short.");
        }

        int offset = 0;
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(offset, 4));
        offset += 4;
        if (magic != EnvelopeMagic)
        {
            throw new InvalidDataException($"Invalid envelope magic 0x{magic:X8}; expected 0x{EnvelopeMagic:X8}.");
        }

        ushort protocolVersion = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(offset, 2));
        offset += 2;

        ushort channelLen = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(offset, 2));
        offset += 2;
        if (offset + channelLen + 2 > frame.Length)
        {
            throw new InvalidDataException("Envelope frame truncated in channel field.");
        }

        string channel = Encoding.UTF8.GetString(frame.Slice(offset, channelLen));
        offset += channelLen;

        ushort typeLen = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(offset, 2));
        offset += 2;
        if (offset + typeLen + 4 > frame.Length)
        {
            throw new InvalidDataException("Envelope frame truncated in message type field.");
        }

        string messageType = Encoding.UTF8.GetString(frame.Slice(offset, typeLen));
        offset += typeLen;

        uint payloadLen = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(offset, 4));
        offset += 4;
        if (payloadLen > int.MaxValue || offset + (int)payloadLen + 8 > frame.Length)
        {
            throw new InvalidDataException("Envelope frame truncated in payload field.");
        }

        string payloadJson = Encoding.UTF8.GetString(frame.Slice(offset, (int)payloadLen));
        offset += (int)payloadLen;

        long sequence = BinaryPrimitives.ReadInt64BigEndian(frame.Slice(offset, 8));
        return new ModEnvelope(channel, messageType, payloadJson, protocolVersion, sequence);
    }

    /// <summary>
    /// Encodes a ruleset wire packet body matching the 3.29 client ruleset framing:
    /// <c>[u8 rulesetId][u16 BE payloadLength][payload]</c> (the 2-byte big-endian opcode <c>0x02C9</c> or <c>0x02CA</c>
    /// is carried in the packet opcode header or optionally prepended via <see cref="EncodeFullRulesetPacket"/>).
    /// </summary>
    public static byte[] EncodeRulesetBody(byte rulesetId, ReadOnlySpan<byte> innerPayload)
    {
        if (rulesetId > IRulesetRegistry.MaxRulesetId)
        {
            throw new ArgumentOutOfRangeException(nameof(rulesetId), $"Ruleset id 0x{rulesetId:X2} exceeds maximum 0x{IRulesetRegistry.MaxRulesetId:X2}.");
        }

        if (innerPayload.Length > MaxRulesetPayloadBytes)
        {
            throw new ArgumentException($"Ruleset payload length {innerPayload.Length} exceeds 3.29 client limit of {MaxRulesetPayloadBytes} bytes.", nameof(innerPayload));
        }

        byte[] body = new byte[3 + innerPayload.Length];
        body[0] = rulesetId;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1, 2), (ushort)innerPayload.Length);
        innerPayload.CopyTo(body.AsSpan(3));
        return body;
    }

    /// <summary>
    /// Decodes a ruleset wire packet body (<c>[u8 rulesetId][u16 BE length][payload]</c>).
    /// </summary>
    public static bool TryDecodeRulesetBody(ReadOnlyMemory<byte> body, out byte rulesetId, out ReadOnlyMemory<byte> innerPayload)
    {
        rulesetId = 0;
        innerPayload = ReadOnlyMemory<byte>.Empty;
        if (body.Length < 3)
        {
            return false;
        }

        ReadOnlySpan<byte> span = body.Span;
        rulesetId = span[0];
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(1, 2));
        if (3 + length > body.Length)
        {
            return false;
        }

        innerPayload = body.Slice(3, length);
        return true;
    }

    /// <summary>
    /// Encodes a complete ruleset packet including its 2-byte big-endian opcode (<c>0x02C9</c> S→C or <c>0x02CA</c> C→S).
    /// </summary>
    public static byte[] EncodeFullRulesetPacket(ushort opcode, byte rulesetId, ReadOnlySpan<byte> innerPayload)
    {
        byte[] body = EncodeRulesetBody(rulesetId, innerPayload);
        byte[] full = new byte[2 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(full.AsSpan(0, 2), opcode);
        Buffer.BlockCopy(body, 0, full, 2, body.Length);
        return full;
    }

    /// <summary>
    /// Decodes a complete ruleset packet beginning with its 2-byte big-endian opcode.
    /// </summary>
    public static bool TryDecodeFullRulesetPacket(ReadOnlyMemory<byte> packet, out ushort opcode, out byte rulesetId, out ReadOnlyMemory<byte> innerPayload)
    {
        opcode = 0;
        rulesetId = 0;
        innerPayload = ReadOnlyMemory<byte>.Empty;
        if (packet.Length < 5)
        {
            return false;
        }

        opcode = BinaryPrimitives.ReadUInt16BigEndian(packet.Span.Slice(0, 2));
        return TryDecodeRulesetBody(packet.Slice(2), out rulesetId, out innerPayload);
    }

    /// <summary>Encodes an envelope as a server-to-client chat line (<c>##NM env &lt;base64&gt;</c>).</summary>
    public static string EncodeServerChatEnvelope(ModEnvelope envelope)
    {
        byte[] raw = EncodeEnvelope(envelope);
        return $"{ServerChatPrefix}env {Convert.ToBase64String(raw)}";
    }

    /// <summary>Attempts to decode a server-to-client chat line (<c>##NM env &lt;base64&gt;</c> or spike S4 <c>##NM open|set ...</c>).</summary>
    public static bool TryDecodeServerChatEnvelope(string chatLine, out ModEnvelope? envelope)
    {
        envelope = null;
        if (string.IsNullOrEmpty(chatLine) || !chatLine.StartsWith(ServerChatPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string rest = chatLine[ServerChatPrefix.Length..].Trim();
        if (rest.StartsWith("env ", StringComparison.Ordinal))
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(rest[4..].Trim());
                envelope = DecodeEnvelope(bytes);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException)
            {
                return false;
            }
        }

        // Backwards compatibility with spike S4 plain chat commands ("##NM open <path>" / "##NM set <ctrl> <text>").
        int space = rest.IndexOf(' ');
        string verb = space < 0 ? rest : rest[..space];
        string arg = space < 0 ? string.Empty : rest[(space + 1)..];
        if (string.Equals(verb, "open", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(arg))
        {
            string winId = Path.GetFileNameWithoutExtension(arg);
            var cmd = new ServerUiCommand(ServerUiCommandKind.OpenWindow, winId, VirtualPath: arg.Trim());
            envelope = CreateTypedEnvelope(SystemChannel, cmd);
            return true;
        }

        if (string.Equals(verb, "set", StringComparison.OrdinalIgnoreCase))
        {
            int argSpace = arg.IndexOf(' ');
            if (argSpace > 0)
            {
                string target = arg[..argSpace];
                string text = arg[(argSpace + 1)..];
                var cmd = new ServerUiCommand(ServerUiCommandKind.SetText, string.Empty, ControlPath: target, Value: text);
                envelope = CreateTypedEnvelope(SystemChannel, cmd);
                return true;
            }
        }

        return false;
    }

    /// <summary>Encodes an envelope as a client-to-server chat command (<c>/nm env &lt;base64&gt;</c>).</summary>
    public static string EncodeClientChatEnvelope(ModEnvelope envelope)
    {
        byte[] raw = EncodeEnvelope(envelope);
        return $"{ClientChatCommandPrefix}env {Convert.ToBase64String(raw)}";
    }

    /// <summary>Attempts to decode a client-to-server chat command (<c>/nm env &lt;base64&gt;</c>).</summary>
    public static bool TryDecodeClientChatEnvelope(string commandLine, out ModEnvelope? envelope)
    {
        envelope = null;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        string trimmed = commandLine.Trim();
        if (!trimmed.StartsWith(ClientChatCommandPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string rest = trimmed[ClientChatCommandPrefix.Length..].Trim();
        if (!rest.StartsWith("env ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(rest[4..].Trim());
            envelope = DecodeEnvelope(bytes);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Computes the folded 16-bit FNV-1a hash used by the 3.29 client for S→C <c>0x285</c> GEAL <c>Functions</c> calls
    /// (verified in LocalExile's <c>ZoomMod.cs</c> and <c>10-verification.md</c> §4).
    /// </summary>
    public static ushort ComputeGealFunctionHash(string functionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        uint hash = 0;
        foreach (byte b in Encoding.ASCII.GetBytes(functionName))
        {
            hash = unchecked(hash * 0x1000193u ^ b);
        }

        byte hi = (byte)((hash >> 24) ^ (hash >> 8));
        byte lo = (byte)((hash >> 16) ^ hash);
        return (ushort)((hi << 8) | lo);
    }
}
