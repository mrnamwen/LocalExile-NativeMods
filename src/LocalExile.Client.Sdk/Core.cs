using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Raw;

namespace LocalExile.Client.Sdk;

/// <summary>
/// Version constants and compatibility checks for the LocalExile NativeMods Client SDK.
/// </summary>
public static class SdkVersion
{
    /// <summary>The current semantic version string of the Client SDK.</summary>
    public const string Current = "0.4.0";

    /// <summary>The current binary/envelope wire protocol version.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>The parsed major.minor.patch version of the Client SDK.</summary>
    public static Version Parsed { get; } = new(0, 4, 0);

    /// <summary>
    /// Evaluates whether <see cref="Current"/> satisfies a simplified SemVer range string
    /// (supports <c>*</c>, exact versions, <c>^x.y.z</c>, <c>~x.y.z</c>, and comparators
    /// <c>&gt;=</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&lt;</c>, <c>=</c> separated by spaces or commas).
    /// </summary>
    /// <param name="versionRange">The requested SDK version range, or null/empty for any.</param>
    /// <returns><c>true</c> if the current SDK satisfies the range; otherwise <c>false</c>.</returns>
    public static bool IsCompatible(string? versionRange)
    {
        if (string.IsNullOrWhiteSpace(versionRange))
        {
            return true;
        }

        string trimmed = versionRange.Trim();
        if (trimmed is "*" or "any")
        {
            return true;
        }

        string[] clauses = trimmed.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string clause in clauses)
        {
            if (!EvaluateClause(clause))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EvaluateClause(string clause)
    {
        if (string.Equals(clause, Current, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (clause.StartsWith(">=", StringComparison.Ordinal))
        {
            return TryParseCoreVersion(clause[2..], out Version? target) && Parsed >= target;
        }

        if (clause.StartsWith("<=", StringComparison.Ordinal))
        {
            return TryParseCoreVersion(clause[2..], out Version? target) && Parsed <= target;
        }

        if (clause.StartsWith('>'))
        {
            return TryParseCoreVersion(clause[1..], out Version? target) && Parsed > target;
        }

        if (clause.StartsWith('<'))
        {
            return TryParseCoreVersion(clause[1..], out Version? target) && Parsed < target;
        }

        if (clause.StartsWith('='))
        {
            string rest = clause[1..];
            if (string.Equals(rest, Current, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return TryParseCoreVersion(rest, out Version? target) && Parsed == target;
        }

        if (clause.StartsWith('^'))
        {
            if (!TryParseCoreVersion(clause[1..], out Version? target))
            {
                return false;
            }

            if (target.Major > 0)
            {
                return Parsed >= target && Parsed.Major == target.Major;
            }

            if (target.Minor > 0)
            {
                return Parsed >= target && Parsed.Major == 0 && Parsed.Minor == target.Minor;
            }

            return Parsed == target;
        }

        if (clause.StartsWith('~'))
        {
            if (!TryParseCoreVersion(clause[1..], out Version? target))
            {
                return false;
            }

            return Parsed >= target && Parsed.Major == target.Major && Parsed.Minor == target.Minor;
        }

        return TryParseCoreVersion(clause, out Version? exact) && Parsed == exact;
    }

    private static bool TryParseCoreVersion(string text, [NotNullWhen(true)] out Version? version)
    {
        string core = text.Trim();
        int dash = core.IndexOf('-');
        if (dash >= 0)
        {
            core = core[..dash];
        }

        int plus = core.IndexOf('+');
        if (plus >= 0)
        {
            core = core[..plus];
        }

        string[] parts = core.Split('.');
        if (parts.Length == 1 && int.TryParse(parts[0], out int maj))
        {
            version = new Version(maj, 0, 0);
            return true;
        }

        if (parts.Length == 2 && int.TryParse(parts[0], out maj) && int.TryParse(parts[1], out int min))
        {
            version = new Version(maj, min, 0);
            return true;
        }

        if (parts.Length >= 3 && int.TryParse(parts[0], out maj) && int.TryParse(parts[1], out min) && int.TryParse(parts[2], out int pat))
        {
            version = new Version(maj, min, pat);
            return true;
        }

        version = null;
        return false;
    }
}

/// <summary>
/// Entry point implemented by every managed LocalExile client mod assembly.
/// </summary>
public interface IClientMod
{
    /// <summary>
    /// Called once on the game thread when the client host loads the mod.
    /// </summary>
    /// <param name="context">The scoped mod context providing engine, extend, networking, and raw services.</param>
    void Load(IClientContext context);

    /// <summary>
    /// Optional callback invoked before the host disposes tracked resources and unloads the mod's load context.
    /// </summary>
    void Unload() { }
}

/// <summary>
/// Metadata describing a client mod, parsed from either a standalone client manifest or a LocalExile dual-side <c>mod.json</c>.
/// </summary>
/// <param name="Id">Unique lowercase mod identifier matching its folder name.</param>
/// <param name="Name">Human-readable display name.</param>
/// <param name="Version">Semantic version of the mod.</param>
/// <param name="Author">Mod author attribution.</param>
/// <param name="Description">Short one-sentence description.</param>
/// <param name="SdkVersionRange">Compatible <see cref="SdkVersion"/> range (for example <c>&gt;=0.1.0-poc &lt;0.2.0</c>).</param>
/// <param name="Assembly">Optional client DLL filename relative to the mod folder.</param>
/// <param name="PayloadDir">Optional loose client asset directory relative to the mod folder.</param>
/// <param name="Depends">Optional mod identifiers that must load before this mod.</param>
/// <param name="Replaces">Stock virtual paths overridden by this mod's overlays.</param>
/// <param name="RawAccess">Whether this mod explicitly requests access to <see cref="IRawMemory"/>.</param>
/// <param name="RawAccessJustification">Human-readable justification shown to players when <see cref="RawAccess"/> is <c>true</c>.</param>
/// <param name="PayloadHash">Optional SHA-256 digest of the managed client mod assembly DLL (<c>payloadHash</c>/<c>assemblyPayloadHash</c>/<c>assemblySha256</c>).</param>
/// <param name="Enabled">Whether the mod is enabled by default.</param>
/// <param name="BundleSha256">Optional SHA-256 digest of the packed client <c>.bundle.bin</c> asset archive (<c>bundleSha256</c>/<c>integrity.packageSha256</c>).</param>
public sealed record ClientModManifest(
    string Id,
    string Name,
    string Version,
    string Author = "",
    string Description = "",
    string SdkVersionRange = "*",
    string? Assembly = null,
    string? PayloadDir = null,
    IReadOnlyList<string>? Depends = null,
    IReadOnlyList<string>? Replaces = null,
    bool RawAccess = false,
    string? RawAccessJustification = null,
    string? PayloadHash = null,
    bool Enabled = true,
    string? BundleSha256 = null)
{
    /// <summary>Gets the non-null dependency list.</summary>
    public IReadOnlyList<string> EffectiveDepends => Depends ?? [];

    /// <summary>Gets the non-null list of replaced stock paths.</summary>
    public IReadOnlyList<string> EffectiveReplaces => Replaces ?? [];

    /// <summary>
    /// Reads and parses a <see cref="ClientModManifest"/> from a JSON file on disk.
    /// </summary>
    /// <param name="path">Path to the manifest JSON file.</param>
    /// <returns>The parsed manifest.</returns>
    public static ClientModManifest LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Parses a <see cref="ClientModManifest"/> from JSON, supporting both flat client manifests and
    /// LocalExile dual-side <c>mod.json</c> files containing a <c>nativeClient</c> object.
    /// </summary>
    /// <param name="json">JSON document content.</param>
    /// <returns>The parsed manifest.</returns>
    public static ClientModManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        JsonElement root = doc.RootElement;
        bool hasNativeClient = root.TryGetProperty("nativeClient", out JsonElement nc) && nc.ValueKind == JsonValueKind.Object;
        JsonElement clientSection = hasNativeClient ? nc : root;

        string id = GetString(root, "id") ?? GetString(clientSection, "id")
            ?? throw new FormatException("Client mod manifest is missing required property 'id'.");
        string name = GetString(root, "name") ?? id;
        string version = GetString(root, "version") ?? "0.0.0";
        string author = GetString(root, "author") ?? string.Empty;
        string description = GetString(root, "description") ?? string.Empty;
        string sdkRange = GetString(clientSection, "sdkVersionRange")
            ?? GetString(root, "sdkVersionRange")
            ?? "*";
        string? assembly = GetString(clientSection, "assembly") ?? GetString(root, "assembly");
        string? payloadDir = GetString(clientSection, "payloadDir") ?? GetString(root, "payloadDir");
        IReadOnlyList<string> depends;
        if (hasNativeClient && TryGetStringList(clientSection, "depends", out List<string> clientDepends))
        {
            depends = clientDepends;
        }
        else
        {
            List<string> rootDepends = GetStringList(root, "depends");
            if (hasNativeClient)
            {
                rootDepends.RemoveAll(d => string.Equals(d, "Game", StringComparison.OrdinalIgnoreCase));
            }

            depends = rootDepends;
        }

        List<string> replaces = GetStringList(clientSection, "replaces");
        if (replaces.Count == 0)
        {
            replaces = GetStringList(root, "replaces");
        }

        bool rawAccess = GetBool(clientSection, "rawAccess") ?? GetBool(root, "rawAccess") ?? false;
        string? rawJustification = GetString(clientSection, "rawAccessJustification")
            ?? GetString(root, "rawAccessJustification");
        string? payloadHash = GetString(clientSection, "payloadHash")
            ?? GetString(clientSection, "assemblyPayloadHash")
            ?? GetString(clientSection, "assemblySha256")
            ?? GetString(root, "payloadHash")
            ?? GetString(root, "assemblyPayloadHash")
            ?? GetString(root, "assemblySha256");

        string? bundleSha256 = GetString(clientSection, "bundleSha256")
            ?? GetString(root, "bundleSha256");
        if (bundleSha256 is null
            && clientSection.TryGetProperty("integrity", out JsonElement integrityElem)
            && integrityElem.ValueKind == JsonValueKind.Object)
        {
            bundleSha256 = GetString(integrityElem, "packageSha256");
        }
        bool enabled = (GetBool(root, "enabled") ?? true) && (GetBool(clientSection, "enabled") ?? true);

        return new ClientModManifest(
            Id: id,
            Name: name,
            Version: version,
            Author: author,
            Description: description,
            SdkVersionRange: sdkRange,
            Assembly: assembly,
            PayloadDir: payloadDir,
            Depends: depends,
            Replaces: replaces,
            RawAccess: rawAccess,
            RawAccessJustification: rawJustification,
            PayloadHash: payloadHash,
            Enabled: enabled,
            BundleSha256: bundleSha256);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        foreach (JsonProperty prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }

        return null;
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        foreach (JsonProperty prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                && (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False))
            {
                return prop.Value.GetBoolean();
            }
        }

        return null;
    }

    private static bool TryGetStringList(JsonElement element, string propertyName, out List<string> values)
    {
        foreach (JsonProperty prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Array)
            {
                values = [];
                foreach (JsonElement item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                    {
                        values.Add(text);
                    }
                }

                return true;
            }
        }

        values = [];
        return false;
    }

    private static List<string> GetStringList(JsonElement element, string propertyName) =>
        TryGetStringList(element, propertyName, out List<string> values) ? values : [];
}

/// <summary>
/// Per-mod host context passed to <see cref="IClientMod.Load"/>.
/// </summary>
public interface IClientContext
{
    /// <summary>The parsed manifest for this client mod.</summary>
    ClientModManifest Manifest { get; }

    /// <summary>The root directory on disk containing this mod's files.</summary>
    string ModDirectory { get; }

    /// <summary>The active Client SDK version string.</summary>
    string SdkVersion { get; }

    /// <summary>Structured logger scoped to this mod's identifier.</summary>
    IModLogger Logger { get; }

    /// <summary>Game-thread work queue scheduler.</summary>
    IModScheduler Scheduler { get; }

    /// <summary>The build-matched address library for the running 3.29 client.</summary>
    IAddressLibrary Addresses { get; }

    /// <summary>High-level engine and script-extender services.</summary>
    IGameServices Game { get; }

    /// <summary>Typed bidirectional networking channel scoped to this mod's id.</summary>
    INetChannel Net { get; }

    /// <summary>
    /// Low-level memory, hook, and native call surface.
    /// Accessing this property throws <see cref="RawAccessDeniedException"/> unless
    /// <see cref="ClientModManifest.RawAccess"/> is <c>true</c>.
    /// </summary>
    /// <exception cref="RawAccessDeniedException">Thrown when the mod manifest does not declare <c>rawAccess: true</c>.</exception>
    IRawMemory Raw { get; }

    /// <summary>
    /// Registers an <see cref="IDisposable"/> handle owned by this mod so the host disposes it automatically on unload or reload.
    /// </summary>
    /// <param name="resource">The resource to track.</param>
    void TrackDisposable(IDisposable resource);
}

/// <summary>
/// Aggregate surface exposing all engine wrappers and script-extender registries.
/// </summary>
public interface IGameServices
{
    /// <summary>Native UI window and control bridge.</summary>
    IUiService Ui { get; }

    /// <summary>System clipboard UTF-8/UTF-16 text service.</summary>
    IClipboardService Clipboard { get; }

    /// <summary>Client chat command and S→C chat interceptor service.</summary>
    IChatService Chat { get; }

    /// <summary>Virtual, loose-directory, and bundle file overlay service.</summary>
    IFileOverlayService Files { get; }

    /// <summary>GEAL script invocation, validation, and built-in enumeration service.</summary>
    IScriptService Scripts { get; }

    /// <summary>FMOD Studio event and <c>.ogg</c> audio playback service.</summary>
    IAudioService Audio { get; }

    /// <summary>Client stat reader and display-only local stat writer.</summary>
    IStatsService Stats { get; }

    /// <summary>Current area world object inspection, client-only object spawning, and effects.</summary>
    IWorldService World { get; }

    /// <summary>Client frame, area, and connection lifecycle events.</summary>
    IGameLifecycle Lifecycle { get; }

    /// <summary>In-game and pre-game screen/panel state, active stash tab metadata, and managed auto-flow toolbar slots.</summary>
    IScreensService Screens { get; }

    /// <summary>Low-level suppressible keyboard/mouse hooks, text-input focus detection, and centralized keybind registry.</summary>
    IInputService Input { get; }

    /// <summary>Hovered UI control, hovered inventory item, and cursor-held/use-mode item inspection service.</summary>
    IHoverService Hover { get; }

    /// <summary>NativeMods-owned HUD toast notification overlay service anchored to <see cref="ScreenSlot.HudTopRight"/>.</summary>
    IToastService Toasts { get; }

    /// <summary>Pre-game character creation option injection (<c>C→S 0x12</c>) and creation result (<c>S→C 0x13</c>) service.</summary>
    IPreGameService PreGame { get; }

    /// <summary>Client item-filter visibility, style inspection, on-demand evaluation, and reload service.</summary>
    IItemFilterService ItemFilter { get; }

    /// <summary>Live camera projection, world-to-screen coordinate transform, and zoom override service.</summary>
    ICameraService Camera { get; }

    /// <summary>Registry for extending the GEAL VM with custom built-in functions.</summary>
    IGealBuiltinRegistry GealBuiltins { get; }

    /// <summary>Registry for extending the UI parser with custom control types.</summary>
    IUiControlRegistry UiControls { get; }

    /// <summary>Registry for registering custom per-area client rulesets (0x2c9/0x2ca).</summary>
    IRulesetRegistry Rulesets { get; }

    /// <summary>Registry for custom reserved network opcode handlers (0x2fc+).</summary>
    IOpcodeRegistry Opcodes { get; }

    /// <summary>Registry for append-only content declarations (dat rows, item bases, world objects, overlays).</summary>
    IContentRegistry Content { get; }

    /// <summary>
    /// Raised once per game frame on the game thread with the elapsed frame delta in seconds.
    /// Convenience alias for <see cref="IGameLifecycle.Frame"/>.
    /// </summary>
    event Action<float>? Frame;
}

/// <summary>
/// Structured logger available to each client mod.
/// </summary>
public interface IModLogger
{
    /// <summary>Writes a diagnostic debug message.</summary>
    void Debug(string message);

    /// <summary>Writes an informational message.</summary>
    void Info(string message);

    /// <summary>Writes a warning message.</summary>
    void Warn(string message);

    /// <summary>Writes an error message with an optional exception.</summary>
    void Error(string message, Exception? exception = null);
}

/// <summary>
/// Marshals callbacks onto the game's main frame thread so UI, audio, stats, and native hooks obey the 3.29 threading contract.
/// </summary>
public interface IModScheduler
{
    /// <summary>Gets whether the calling thread is currently the game's main thread.</summary>
    bool IsOnGameThread { get; }

    /// <summary>Enqueues an action to run on the next game-thread frame drain (or immediately if already on the game thread).</summary>
    /// <param name="action">The callback to execute on the game thread.</param>
    void RunOnGameThread(Action action);

    /// <summary>Asynchronously executes an action on the game thread.</summary>
    /// <param name="action">The callback to execute on the game thread.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task RunOnGameThreadAsync(Action action, CancellationToken cancellationToken = default);

    /// <summary>Asynchronously evaluates a function on the game thread and returns its result.</summary>
    /// <typeparam name="T">The return type.</typeparam>
    /// <param name="func">The function to execute on the game thread.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task<T> RunOnGameThreadAsync<T>(Func<T> func, CancellationToken cancellationToken = default);
}

/// <summary>
/// Exception thrown when a client mod attempts to access <see cref="IRawMemory"/> without declaring <c>rawAccess: true</c> in its manifest.
/// </summary>
public sealed class RawAccessDeniedException : UnauthorizedAccessException
{
    /// <summary>Gets the identifier of the mod that was denied raw access.</summary>
    public string ModId { get; }

    /// <summary>Creates a new <see cref="RawAccessDeniedException"/> for the given mod.</summary>
    /// <param name="modId">The mod identifier.</param>
    public RawAccessDeniedException(string modId)
        : base($"Client mod '{modId}' requested IRawMemory access without declaring 'rawAccess: true' in its manifest.")
    {
        ModId = modId;
    }
}
