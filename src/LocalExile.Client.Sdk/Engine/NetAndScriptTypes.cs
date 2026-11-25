namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Identifies the underlying wire transport used by <see cref="INetChannel"/>.
/// </summary>
public enum NetTransportKind
{
    /// <summary>No transport active.</summary>
    Unknown = 0,

    /// <summary>Host-ruleset transport using S→C <c>0x2c9</c> and C→S <c>0x2ca</c> (gracefully ignored by stock clients).</summary>
    HostRuleset = 1,

    /// <summary>Reserved handler-group transport using opcodes <c>&gt;= 0x2fc</c>.</summary>
    ReservedHandlerGroup = 2,

    /// <summary>Chat fallback transport using S→C <c>##NM </c> lines and C→S <c>/nm </c> commands (spike S4).</summary>
    ChatFallback = 3,

    /// <summary>In-memory loopback transport used by automated tests.</summary>
    Loopback = 4,
}

/// <summary>
/// Describes the active network transport configuration and limits.
/// </summary>
/// <param name="Kind">Active transport kind.</param>
/// <param name="IncomingOpcode">Server-to-client opcode (for example <c>0x02C9</c> or <c>0x0017</c>).</param>
/// <param name="OutgoingOpcode">Client-to-server opcode (for example <c>0x02CA</c> or <c>0x0015</c>).</param>
/// <param name="RulesetId">Host ruleset id when using <see cref="NetTransportKind.HostRuleset"/>.</param>
/// <param name="MaxPayloadBytes">Maximum safe payload size in bytes (65535 for rulesets).</param>
/// <param name="IsConnected">Whether the transport is currently connected to a server.</param>
public sealed record NetTransportInfo(
    NetTransportKind Kind,
    ushort IncomingOpcode,
    ushort OutgoingOpcode,
    byte? RulesetId,
    int MaxPayloadBytes,
    bool IsConnected);

/// <summary>
/// Scoped bidirectional networking channel between a client mod and its LocalExile server half.
/// </summary>
public interface INetChannel
{
    /// <summary>The logical channel identifier (typically the owning mod's id).</summary>
    string ChannelId { get; }

    /// <summary>Information about the active underlying transport.</summary>
    NetTransportInfo Transport { get; }

    /// <summary>Serializes and sends a typed message record to the server half of the mod.</summary>
    /// <typeparam name="T">Message payload type (JSON-serializable record/class).</typeparam>
    /// <param name="message">The message payload instance.</param>
    /// <param name="messageType">Optional explicit message type discriminator; defaults to <c>typeof(T).Name</c>.</param>
    void Send<T>(T message, string? messageType = null);

    /// <summary>Sends a raw opcode packet payload to the server.</summary>
    /// <param name="opcode">Wire opcode identifier.</param>
    /// <param name="payload">Raw packet payload bytes.</param>
    void SendRaw(ushort opcode, ReadOnlySpan<byte> payload);

    /// <summary>Subscribes to incoming typed messages from the server on this channel.</summary>
    /// <typeparam name="T">Expected message payload type.</typeparam>
    /// <param name="handler">Callback invoked on receipt.</param>
    /// <param name="messageType">Optional explicit message type discriminator; defaults to <c>typeof(T).Name</c>.</param>
    IDisposable On<T>(Action<T> handler, string? messageType = null);

    /// <summary>Subscribes to incoming raw payloads for a specific opcode.</summary>
    /// <param name="opcode">Wire opcode identifier.</param>
    /// <param name="handler">Callback receiving raw payload bytes.</param>
    IDisposable OnRaw(ushort opcode, Action<ReadOnlyMemory<byte>> handler);
}

/// <summary>
/// Value discriminator for GEAL script values passed across the managed-native script boundary.
/// </summary>
public enum GealValueKind
{
    /// <summary>Null or empty value.</summary>
    Null = 0,

    /// <summary>64-bit signed integer.</summary>
    Integer = 1,

    /// <summary>32-bit floating-point number.</summary>
    Float = 2,

    /// <summary>Boolean value.</summary>
    Bool = 3,

    /// <summary>String value.</summary>
    String = 4,

    /// <summary>World object identifier.</summary>
    ObjectId = 5,
}

/// <summary>
/// Tagged value representing a GEAL argument or return value.
/// </summary>
public readonly record struct GealValue
{
    /// <summary>A null GEAL value.</summary>
    public static GealValue Null => default;

    /// <summary>The runtime value kind.</summary>
    public GealValueKind Kind { get; }

    /// <summary>Integer payload when <see cref="Kind"/> is <see cref="GealValueKind.Integer"/>.</summary>
    public long IntValue { get; }

    /// <summary>Float payload when <see cref="Kind"/> is <see cref="GealValueKind.Float"/>.</summary>
    public float FloatValue { get; }

    /// <summary>Boolean payload when <see cref="Kind"/> is <see cref="GealValueKind.Bool"/>.</summary>
    public bool BoolValue { get; }

    /// <summary>String payload when <see cref="Kind"/> is <see cref="GealValueKind.String"/>.</summary>
    public string? StringValue { get; }

    /// <summary>Object id payload when <see cref="Kind"/> is <see cref="GealValueKind.ObjectId"/>.</summary>
    public uint ObjectIdValue { get; }

    private GealValue(GealValueKind kind, long intVal = 0, float floatVal = 0f, bool boolVal = false, string? strVal = null, uint objId = 0)
    {
        Kind = kind;
        IntValue = intVal;
        FloatValue = floatVal;
        BoolValue = boolVal;
        StringValue = strVal;
        ObjectIdValue = objId;
    }

    /// <summary>Creates an integer GEAL value.</summary>
    public static GealValue FromInt(long value) => new(GealValueKind.Integer, intVal: value);

    /// <summary>Creates a floating-point GEAL value.</summary>
    public static GealValue FromFloat(float value) => new(GealValueKind.Float, floatVal: value);

    /// <summary>Creates a boolean GEAL value.</summary>
    public static GealValue FromBool(bool value) => new(GealValueKind.Bool, boolVal: value);

    /// <summary>Creates a string GEAL value.</summary>
    public static GealValue FromString(string value) => new(GealValueKind.String, strVal: value ?? string.Empty);

    /// <summary>Creates a world object id GEAL value.</summary>
    public static GealValue FromObjectId(uint objectId) => new(GealValueKind.ObjectId, objId: objectId);

    /// <summary>Implicitly converts an <see cref="int"/> to a <see cref="GealValue"/>.</summary>
    public static implicit operator GealValue(int value) => FromInt(value);

    /// <summary>Implicitly converts a <see cref="float"/> to a <see cref="GealValue"/>.</summary>
    public static implicit operator GealValue(float value) => FromFloat(value);

    /// <summary>Implicitly converts a <see cref="bool"/> to a <see cref="GealValue"/>.</summary>
    public static implicit operator GealValue(bool value) => FromBool(value);

    /// <summary>Implicitly converts a <see cref="string"/> to a <see cref="GealValue"/>.</summary>
    public static implicit operator GealValue(string value) => FromString(value);
}

/// <summary>
/// Typed positional accessor for arguments passed into a custom GEAL built-in function.
/// </summary>
public sealed class GealArguments
{
    private readonly IReadOnlyList<GealValue> _values;

    /// <summary>Creates a new argument view over the supplied values.</summary>
    public GealArguments(IReadOnlyList<GealValue> values)
    {
        _values = values ?? [];
    }

    /// <summary>Gets the number of supplied arguments.</summary>
    public int Count => _values.Count;

    /// <summary>Gets the raw <see cref="GealValue"/> at <paramref name="index"/>.</summary>
    public GealValue this[int index] => _values[index];

    /// <summary>Reads the argument at <paramref name="index"/> as a 32-bit integer.</summary>
    public int Int(int index, int defaultValue = 0)
    {
        if (index < 0 || index >= _values.Count)
        {
            return defaultValue;
        }

        GealValue v = _values[index];
        return v.Kind switch
        {
            GealValueKind.Integer => (int)v.IntValue,
            GealValueKind.Float => (int)v.FloatValue,
            GealValueKind.Bool => v.BoolValue ? 1 : 0,
            GealValueKind.ObjectId => (int)v.ObjectIdValue,
            GealValueKind.String when int.TryParse(v.StringValue, out int parsed) => parsed,
            _ => defaultValue,
        };
    }

    /// <summary>Reads the argument at <paramref name="index"/> as a single-precision float.</summary>
    public float Float(int index, float defaultValue = 0f)
    {
        if (index < 0 || index >= _values.Count)
        {
            return defaultValue;
        }

        GealValue v = _values[index];
        return v.Kind switch
        {
            GealValueKind.Float => v.FloatValue,
            GealValueKind.Integer => v.IntValue,
            GealValueKind.String when float.TryParse(v.StringValue, System.Globalization.CultureInfo.InvariantCulture, out float parsed) => parsed,
            _ => defaultValue,
        };
    }

    /// <summary>Reads the argument at <paramref name="index"/> as a boolean.</summary>
    public bool Bool(int index, bool defaultValue = false)
    {
        if (index < 0 || index >= _values.Count)
        {
            return defaultValue;
        }

        GealValue v = _values[index];
        return v.Kind switch
        {
            GealValueKind.Bool => v.BoolValue,
            GealValueKind.Integer => v.IntValue != 0,
            GealValueKind.String when bool.TryParse(v.StringValue, out bool parsed) => parsed,
            _ => defaultValue,
        };
    }

    /// <summary>Reads the argument at <paramref name="index"/> as a string.</summary>
    public string String(int index, string defaultValue = "")
    {
        if (index < 0 || index >= _values.Count)
        {
            return defaultValue;
        }

        GealValue v = _values[index];
        return v.Kind switch
        {
            GealValueKind.String => v.StringValue ?? defaultValue,
            GealValueKind.Integer => v.IntValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            GealValueKind.Float => v.FloatValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            GealValueKind.Bool => v.BoolValue ? "true" : "false",
            GealValueKind.ObjectId => v.ObjectIdValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => defaultValue,
        };
    }
}

/// <summary>
/// Diagnostic message emitted by GEAL script compile/lint validation.
/// </summary>
/// <param name="IsError"><c>true</c> for fatal errors that prevent client compilation; <c>false</c> for warnings.</param>
/// <param name="Code">Stable diagnostic code (for example <c>GEAL001</c>).</param>
/// <param name="Message">Human-readable diagnostic description.</param>
/// <param name="Line">1-based line number in the source text.</param>
public sealed record GealScriptDiagnostic(bool IsError, string Code, string Message, int Line);

/// <summary>
/// Outcome of invoking a GEAL built-in, object function, or object event.
/// </summary>
/// <param name="Succeeded">Whether invocation completed without error.</param>
/// <param name="ReturnValue">The returned GEAL value, or <see cref="GealValue.Null"/>.</param>
/// <param name="Error">Error message if <paramref name="Succeeded"/> is <c>false</c>.</param>
public sealed record GealInvocationResult(bool Succeeded, GealValue ReturnValue, string? Error = null);

/// <summary>
/// Interacts with the client's GEAL script VM (724 stock client built-ins plus any mod-registered built-ins).
/// </summary>
public interface IScriptService
{
    /// <summary>Invokes a named <c>Functions { ... }</c> entry on a client world object.</summary>
    GealInvocationResult InvokeFunction(uint objectId, string functionName, params GealValue[] arguments);

    /// <summary>Dispatches an <c>on_xxx</c> object script event on a client world object.</summary>
    GealInvocationResult InvokeEvent(uint objectId, string eventName, params GealValue[] arguments);

    /// <summary>
    /// Validates GEAL script source against known 3.29 client VM constraints (balanced braces,
    /// server-only built-in rejection, unbounded <c>Repeat</c> warnings, and argument limits).
    /// </summary>
    IReadOnlyList<GealScriptDiagnostic> ValidateScript(string gealSource);

    /// <summary>Returns all built-in names currently registered in the client VM (stock + mod extensions).</summary>
    IReadOnlyCollection<string> GetRegisteredBuiltins();

    /// <summary>Registers a custom GEAL built-in function returning a <see cref="GealValue"/>.</summary>
    IDisposable Register(string name, Func<uint, GealArguments, GealValue> callback);

    /// <summary>Registers a custom void GEAL built-in function.</summary>
    IDisposable Register(string name, Action<uint, GealArguments> callback);
}
