using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;

namespace LocalExile.Client.ServerBridge;

/// <summary>
/// Transport adapter implemented by the LocalExile Gateway (or a test harness) to deliver outbound
/// packets (<c>0x02C9</c>) and chat fallback replies (<c>##NM </c>) to a connected client.
/// </summary>
public interface IGatewayPacketSink
{
    /// <summary>Sends a raw S→C packet with <paramref name="opcode"/> and <paramref name="payload"/> to <paramref name="connectionId"/>.</summary>
    /// <param name="connectionId">Unique Gateway connection identifier.</param>
    /// <param name="opcode">Wire opcode (for example <c>0x02C9</c> for host ruleset updates).</param>
    /// <param name="payload">Packet body bytes (without the 2-byte opcode prefix).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>Sends an S→C chat line (for example <c>##NM env ...</c>) to <paramref name="connectionId"/>.</summary>
    /// <param name="connectionId">Unique Gateway connection identifier.</param>
    /// <param name="text">Chat line text.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Configuration options for <see cref="ClientBridgeHost"/>.
/// </summary>
public sealed record ClientBridgeOptions
{
    /// <summary>LocalExile server version advertised in <see cref="ServerHandshake"/>.</summary>
    public string ServerVersion { get; init; } = "0.5.2-beta";

    /// <summary>ServerBridge version advertised in <see cref="ServerHandshake"/>.</summary>
    public string BridgeVersion { get; init; } = SdkVersion.Current;

    /// <summary>Host ruleset slot id (<c>0x00..0xAE</c>) used for <c>0x02C9</c>/<c>0x02CA</c> channel traffic.</summary>
    public byte HostRulesetId { get; init; } = IRulesetRegistry.DefaultHostChannelRulesetId;

    /// <summary>Default transport used before a client's handshake selects a transport.</summary>
    public NetTransportKind DefaultTransport { get; init; } = NetTransportKind.HostRuleset;

    /// <summary>Optional list of client mod ids requested by the server.</summary>
    public IReadOnlyList<string> RequiredMods { get; init; } = [];

    /// <summary>Server capabilities advertised in <see cref="ServerHandshake"/>.</summary>
    public IReadOnlyList<string> ServerCapabilities { get; init; } =
    [
        "ruleset.0x2c9",
        "chat.fallback",
        "ui.server-driven",
    ];
}

/// <summary>
/// Server-driven UI helper bound to a specific player connection, allowing server-only mods to open
/// and update <c>.ui</c> windows and receive control click/submit events without a custom client DLL.
/// </summary>
public interface IClientUiSession
{
    /// <summary>Instructs the client host to open a <c>.ui</c> window.</summary>
    /// <param name="virtualPath">Virtual path to the <c>.ui</c> layout.</param>
    /// <param name="windowId">Optional explicit window id; defaults to filename without extension.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task<ServerUiWindowHandle> OpenAsync(string virtualPath, string? windowId = null, CancellationToken cancellationToken = default);

    /// <summary>Instructs the client host to close a window.</summary>
    Task CloseAsync(string windowId, CancellationToken cancellationToken = default);

    /// <summary>Instructs the client host to reload a window from its <c>.ui</c> overlay.</summary>
    Task ReloadAsync(string windowId, CancellationToken cancellationToken = default);

    /// <summary>Updates the text of a control inside a window.</summary>
    Task SetTextAsync(string windowId, string controlPath, string text, CancellationToken cancellationToken = default);

    /// <summary>Shows or hides a control inside a window.</summary>
    Task SetVisibleAsync(string windowId, string controlPath, bool visible, CancellationToken cancellationToken = default);

    /// <summary>Sets an arbitrary string property on a control inside a window.</summary>
    Task SetPropertyAsync(string windowId, string controlPath, string propertyName, string value, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to button click events for <paramref name="controlPath"/> inside <paramref name="windowId"/>.</summary>
    IDisposable OnButtonClick(string windowId, string controlPath, Action<IClientSession> handler);

    /// <summary>Subscribes to an arbitrary interactive control event (<c>Clicked</c>, <c>CheckedChanged</c>, <c>Submitted</c>).</summary>
    IDisposable OnControlEvent(string windowId, string controlPath, string eventName, Action<IClientSession, string?> handler);

    /// <summary>Instructs the client host to play an FMOD event or <c>.ogg</c> audio file.</summary>
    Task PlayAudioAsync(string pathOrEvent, CancellationToken cancellationToken = default);

    /// <summary>Instructs the client host to play a visual effect on a world object.</summary>
    Task PlayEffectAsync(uint objectId, string effectPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Ergonomic handle referencing an open server-driven window on a specific client connection.
/// </summary>
public sealed class ServerUiWindowHandle
{
    private readonly IClientUiSession _ui;

    /// <summary>The window identifier.</summary>
    public string WindowId { get; }

    /// <summary>The virtual <c>.ui</c> layout path.</summary>
    public string VirtualPath { get; }

    internal ServerUiWindowHandle(IClientUiSession ui, string windowId, string virtualPath)
    {
        _ui = ui;
        WindowId = windowId;
        VirtualPath = virtualPath;
    }

    /// <summary>Sets the text of <paramref name="controlPath"/> inside this window.</summary>
    public Task SetTextAsync(string controlPath, string text, CancellationToken cancellationToken = default) =>
        _ui.SetTextAsync(WindowId, controlPath, text, cancellationToken);

    /// <summary>Sets the visibility of <paramref name="controlPath"/> inside this window.</summary>
    public Task SetVisibleAsync(string controlPath, bool visible, CancellationToken cancellationToken = default) =>
        _ui.SetVisibleAsync(WindowId, controlPath, visible, cancellationToken);

    /// <summary>Sets an arbitrary property on <paramref name="controlPath"/> inside this window.</summary>
    public Task SetPropertyAsync(string controlPath, string propertyName, string value, CancellationToken cancellationToken = default) =>
        _ui.SetPropertyAsync(WindowId, controlPath, propertyName, value, cancellationToken);

    /// <summary>Subscribes to button click events on <paramref name="controlPath"/> inside this window.</summary>
    public IDisposable OnButtonClick(string controlPath, Action<IClientSession> handler) =>
        _ui.OnButtonClick(WindowId, controlPath, handler);

    /// <summary>Subscribes to a named control event on <paramref name="controlPath"/> inside this window.</summary>
    public IDisposable OnControlEvent(string controlPath, string eventName, Action<IClientSession, string?> handler) =>
        _ui.OnControlEvent(WindowId, controlPath, eventName, handler);

    /// <summary>Reloads this window from its <c>.ui</c> overlay.</summary>
    public Task ReloadAsync(CancellationToken cancellationToken = default) =>
        _ui.ReloadAsync(WindowId, cancellationToken);

    /// <summary>Closes this window.</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default) =>
        _ui.CloseAsync(WindowId, cancellationToken);
}

/// <summary>
/// Per-connection NativeMods session tracked by <see cref="ClientBridgeHost"/> on the LocalExile Gateway.
/// </summary>
public interface IClientSession
{
    /// <summary>Unique connection identifier.</summary>
    string ConnectionId { get; }

    /// <summary>Gets whether this client completed a NativeMods extender handshake.</summary>
    bool HasNativeHost { get; }

    /// <summary>Most recent <see cref="ClientHandshake"/> received from this client, or <c>null</c> for stock clients.</summary>
    ClientHandshake? Handshake { get; }

    /// <summary>Active wire transport negotiated for this client.</summary>
    NetTransportKind ActiveTransport { get; }

    /// <summary>Detected 3.29 build of this client.</summary>
    ClientBuildId ClientBuild { get; }

    /// <summary>Extender version reported by this client, or <c>null</c> if stock.</summary>
    string? ExtenderVersion { get; }

    /// <summary>Client SDK version reported by this client, or <c>null</c> if stock.</summary>
    string? ClientSdkVersion { get; }

    /// <summary>Client mods reported as installed and enabled during handshake.</summary>
    IReadOnlyCollection<InstalledClientModInfo> InstalledMods { get; }

    /// <summary>Server-driven UI helper for this connection.</summary>
    IClientUiSession Ui { get; }

    /// <summary>
    /// Checks whether this client has installed the client mod <paramref name="modId"/>
    /// (and optionally verifies that its reported payload hash matches <paramref name="expectedPayloadHash"/>).
    /// Enables graceful server-side degradation for players without the client mod.
    /// </summary>
    bool HasClientMod(string modId, string? expectedPayloadHash = null);

    /// <summary>Sends a typed message record to this client on <paramref name="channel"/>.</summary>
    Task SendAsync<T>(string channel, T message, string? messageType = null, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to incoming typed messages from this client on <paramref name="channel"/>.</summary>
    IDisposable On<T>(string channel, Action<IClientSession, T> handler, string? messageType = null);
}
