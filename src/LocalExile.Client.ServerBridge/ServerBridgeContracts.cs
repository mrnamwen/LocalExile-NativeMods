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
/// Represents a single outbound wire item (either a raw packet or a chat fallback line)
/// queued inside an <see cref="IClientCommandBatch"/> and flushed in order.
/// </summary>
/// <param name="IsChatReply"><c>true</c> for a chat fallback reply; <c>false</c> for a binary packet.</param>
/// <param name="Opcode">Wire opcode when <paramref name="IsChatReply"/> is <c>false</c>.</param>
/// <param name="Payload">Packet payload bytes when <paramref name="IsChatReply"/> is <c>false</c>.</param>
/// <param name="ChatText">Chat line text when <paramref name="IsChatReply"/> is <c>true</c>.</param>
public readonly record struct GatewayBatchItem(
    bool IsChatReply,
    ushort Opcode,
    ReadOnlyMemory<byte> Payload,
    string? ChatText);

/// <summary>
/// Optional extension of <see cref="IGatewayPacketSink"/> supporting batched delivery of outbound
/// packets and chat replies flushed from an <see cref="IClientCommandBatch"/>.
/// </summary>
public interface IBatchingGatewayPacketSink : IGatewayPacketSink
{
    /// <summary>Sends an ordered sequence of batched packets and/or chat lines to <paramref name="connectionId"/>.</summary>
    /// <param name="connectionId">Unique Gateway connection identifier.</param>
    /// <param name="items">Ordered batch items to deliver.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task SendBatchAsync(string connectionId, IReadOnlyList<GatewayBatchItem> items, CancellationToken cancellationToken = default);
}

/// <summary>
/// Deferred outbound command/UI batch scoped to a single <see cref="IClientSession"/>.
/// Commands queued while the batch is active are coalesced safely and flushed in order on
/// <see cref="FlushAsync"/> or <see cref="IAsyncDisposable.DisposeAsync"/>.
/// Synchronous <see cref="IDisposable.Dispose"/> flushes non-blockingly without deadlocking.
/// </summary>
public interface IClientCommandBatch : IAsyncDisposable, IDisposable
{
    /// <summary>Number of queued items currently buffered in the session batch.</summary>
    int Count { get; }

    /// <summary>Whether this batch has already been flushed.</summary>
    bool IsFlushed { get; }

    /// <summary>Whether this batch was discarded via <see cref="Discard"/>.</summary>
    bool IsDiscarded { get; }

    /// <summary>Flushes buffered operations (deferring to the outer batch if nested).</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards any operations queued since this batch began without sending them.</summary>
    void Discard();
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

    /// <summary>Optional default mod channel identifier used by channel-less <c>SendAsync</c> overloads.</summary>
    public string? DefaultChannel { get; init; }

    /// <summary>Optional list of client mod ids requested by the server.</summary>
    public IReadOnlyList<string> RequiredMods { get; init; } = [];

    /// <summary>Server capabilities advertised in <see cref="ServerHandshake"/>.</summary>
    public IReadOnlyList<string> ServerCapabilities { get; init; } =
    [
        "ruleset.0x2c9",
        "chat.fallback",
        "ui.server-driven",
        "tier1.slots",
        "tier1.toasts",
        "tier1.hotkeys",
        "tier1.queries",
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

    /// <summary>Subscribes asynchronously to button click events for <paramref name="controlPath"/> inside <paramref name="windowId"/>.</summary>
    IDisposable OnButtonClick(string windowId, string controlPath, Func<IClientSession, Task> handler);

    /// <summary>Subscribes asynchronously with cancellation support to button click events for <paramref name="controlPath"/> inside <paramref name="windowId"/>.</summary>
    IDisposable OnButtonClick(string windowId, string controlPath, Func<IClientSession, CancellationToken, Task> handler);

    /// <summary>Subscribes to button click events and awaits the remote event-subscription command.</summary>
    Task<IDisposable> OnButtonClickAsync(string windowId, string controlPath, Action<IClientSession> handler, CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously to button click events and awaits the remote event-subscription command.</summary>
    Task<IDisposable> OnButtonClickAsync(string windowId, string controlPath, Func<IClientSession, Task> handler, CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously with cancellation support to button click events and awaits the remote event-subscription command.</summary>
    Task<IDisposable> OnButtonClickAsync(string windowId, string controlPath, Func<IClientSession, CancellationToken, Task> handler, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to an arbitrary interactive control event (<c>Clicked</c>, <c>CheckedChanged</c>, <c>Submitted</c>).</summary>
    IDisposable OnControlEvent(string windowId, string controlPath, string eventName, Action<IClientSession, string?> handler);

    /// <summary>Subscribes asynchronously to an arbitrary interactive control event.</summary>
    IDisposable OnControlEvent(string windowId, string controlPath, string eventName, Func<IClientSession, string?, Task> handler);

    /// <summary>Subscribes asynchronously with cancellation support to an arbitrary interactive control event.</summary>
    IDisposable OnControlEvent(string windowId, string controlPath, string eventName, Func<IClientSession, string?, CancellationToken, Task> handler);

    /// <summary>Subscribes to a named control event and awaits the remote event-subscription command.</summary>
    Task<IDisposable> OnControlEventAsync(string windowId, string controlPath, string eventName, Action<IClientSession, string?> handler, CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously to a named control event and awaits the remote event-subscription command.</summary>
    Task<IDisposable> OnControlEventAsync(string windowId, string controlPath, string eventName, Func<IClientSession, string?, Task> handler, CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously with cancellation support to a named control event and awaits the remote event-subscription command.</summary>
    Task<IDisposable> OnControlEventAsync(string windowId, string controlPath, string eventName, Func<IClientSession, string?, CancellationToken, Task> handler, CancellationToken cancellationToken = default);

    /// <summary>Instructs the client host to play an FMOD event or <c>.ogg</c> audio file.</summary>
    Task PlayAudioAsync(string pathOrEvent, CancellationToken cancellationToken = default);

    /// <summary>Instructs the client host to play a visual effect on a world object.</summary>
    Task PlayEffectAsync(uint objectId, string effectPath, CancellationToken cancellationToken = default);

    /// <summary>Registers (or updates) a native toolbar button in a managed <see cref="ScreenSlot"/>.</summary>
    Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(
        ScreenSlot slot,
        string id,
        string text,
        SlotAttachmentOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Registers (or updates) a native toolbar button in a managed <see cref="ScreenSlot"/> with a synchronous click handler.</summary>
    Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(
        ScreenSlot slot,
        string id,
        string text,
        Action<IClientSession> onClick,
        SlotAttachmentOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Registers (or updates) a native toolbar button in a managed <see cref="ScreenSlot"/> with an asynchronous click handler.</summary>
    Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(
        ScreenSlot slot,
        string id,
        string text,
        Func<IClientSession, Task> onClick,
        SlotAttachmentOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Registers (or updates) a native toolbar button in a managed <see cref="ScreenSlot"/> with an asynchronous click handler supporting cancellation.</summary>
    Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(
        ScreenSlot slot,
        string id,
        string text,
        Func<IClientSession, CancellationToken, Task> onClick,
        SlotAttachmentOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a previously registered toolbar button from <paramref name="slot"/>.</summary>
    Task RemoveSlotButtonAsync(ScreenSlot slot, string id, CancellationToken cancellationToken = default);

    /// <summary>Subscribes synchronously to clicks on a toolbar button in <paramref name="slot"/>.</summary>
    Task<IDisposable> OnSlotButtonClickAsync(
        ScreenSlot slot,
        string id,
        Action<IClientSession> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously to clicks on a toolbar button in <paramref name="slot"/>.</summary>
    Task<IDisposable> OnSlotButtonClickAsync(
        ScreenSlot slot,
        string id,
        Func<IClientSession, Task> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously with cancellation support to clicks on a toolbar button in <paramref name="slot"/>.</summary>
    Task<IDisposable> OnSlotButtonClickAsync(
        ScreenSlot slot,
        string id,
        Func<IClientSession, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Displays a NativeMods HUD toast notification on the client.</summary>
    Task ShowToastAsync(
        string title,
        string message,
        ToastSeverity severity = ToastSeverity.Info,
        int durationMs = 4000,
        CancellationToken cancellationToken = default);

    /// <summary>Begins a command/UI batch sharing this session's ordered outbound queue.</summary>
    IClientCommandBatch BeginBatch();

    /// <summary>Flushes any buffered outbound commands/messages on this session.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>Executes <paramref name="action"/> inside a UI/command batch, flushing on success or discarding on exception.</summary>
    Task BatchAsync(Func<IClientUiSession, Task> action, CancellationToken cancellationToken = default);

    /// <summary>Executes <paramref name="action"/> inside a UI/command batch with cancellation, flushing on success or discarding on exception.</summary>
    Task BatchAsync(Func<IClientUiSession, CancellationToken, Task> action, CancellationToken cancellationToken = default);
}

/// <summary>
/// Ergonomic handle referencing an open server-driven window on a specific client connection.
/// </summary>
public sealed class ServerUiWindowHandle
{
    private readonly IClientUiSession _ui;
    private readonly Action<string, Exception>? _reportError;
    private readonly List<IDisposable> _boundSubscriptions = [];
    private readonly List<Func<CancellationToken, Task>> _closedHandlers = [];
    private readonly Lock _gate = new();
    private int _closed;

    /// <summary>The window identifier.</summary>
    public string WindowId { get; }

    /// <summary>The virtual <c>.ui</c> layout path.</summary>
    public string VirtualPath { get; }

    /// <summary>Whether this window handle is still open.</summary>
    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    /// <summary>Raised strictly once when this window is closed, replaced, or its session disconnects.</summary>
    public event Action? Closed;

    internal ServerUiWindowHandle(IClientUiSession ui, string windowId, string virtualPath, Action<string, Exception>? reportError = null)
    {
        _ui = ui;
        _reportError = reportError;
        WindowId = windowId;
        VirtualPath = virtualPath;
    }

    /// <summary>Registers a synchronous callback invoked once when this window closes.</summary>
    public IDisposable OnClosed(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnClosed(_ =>
        {
            handler();
            return Task.CompletedTask;
        });
    }

    /// <summary>Registers an asynchronous callback invoked once when this window closes.</summary>
    public IDisposable OnClosed(Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnClosed(_ => handler());
    }

    /// <summary>Registers an asynchronous callback with cancellation support invoked once when this window closes.</summary>
    public IDisposable OnClosed(Func<CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return EmptyDisposable.Instance;
            }

            _closedHandlers.Add(handler);
        }

        return new CallbackDisposable(() =>
        {
            lock (_gate)
            {
                _closedHandlers.Remove(handler);
            }
        });
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
    public IDisposable OnButtonClick(string controlPath, Action<IClientSession> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        ArgumentNullException.ThrowIfNull(handler);
        if (Volatile.Read(ref _closed) != 0)
        {
            return EmptyDisposable.Instance;
        }

        return TrackSubscription(_ui.OnButtonClick(WindowId, controlPath, handler));
    }

    /// <summary>Subscribes asynchronously to button click events on <paramref name="controlPath"/> inside this window.</summary>
    public IDisposable OnButtonClick(string controlPath, Func<IClientSession, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        ArgumentNullException.ThrowIfNull(handler);
        if (Volatile.Read(ref _closed) != 0)
        {
            return EmptyDisposable.Instance;
        }

        return TrackSubscription(_ui.OnButtonClick(WindowId, controlPath, handler));
    }

    /// <summary>Subscribes asynchronously with cancellation support to button click events on <paramref name="controlPath"/> inside this window.</summary>
    public IDisposable OnButtonClick(string controlPath, Func<IClientSession, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        ArgumentNullException.ThrowIfNull(handler);
        if (Volatile.Read(ref _closed) != 0)
        {
            return EmptyDisposable.Instance;
        }

        return TrackSubscription(_ui.OnButtonClick(WindowId, controlPath, handler));
    }

    /// <summary>Subscribes to button click events on <paramref name="controlPath"/> and awaits remote subscription delivery.</summary>
    public async Task<IDisposable> OnButtonClickAsync(string controlPath, Action<IClientSession> handler, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        ArgumentNullException.ThrowIfNull(handler);
        if (Volatile.Read(ref _closed) != 0)
        {
            return EmptyDisposable.Instance;
        }

        return TrackSubscription(await _ui.OnButtonClickAsync(WindowId, controlPath, handler, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Subscribes asynchronously to button click events on <paramref name="controlPath"/> and awaits remote subscription delivery.</summary>
    public async Task<IDisposable> OnButtonClickAsync(string controlPath, Func<IClientSession, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        ArgumentNullException.ThrowIfNull(handler);
        if (Volatile.Read(ref _closed) != 0)
        {
            return EmptyDisposable.Instance;
        }

        return TrackSubscription(await _ui.OnButtonClickAsync(WindowId, controlPath, handler, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Subscribes asynchronously with cancellation support to button click events on <paramref name="controlPath"/> and awaits remote subscription delivery.</summary>
    public async Task<IDisposable> OnButtonClickAsync(string controlPath, Func<IClientSession, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        ArgumentNullException.ThrowIfNull(handler);
        if (Volatile.Read(ref _closed) != 0)
        {
            return EmptyDisposable.Instance;
        }

        return TrackSubscription(await _ui.OnButtonClickAsync(WindowId, controlPath, handler, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Subscribes to a named control event on <paramref name="controlPath"/> inside this window.</summary>
    public IDisposable OnControlEvent(string controlPath, string eventName, Action<IClientSession, string?> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(handler);
        if (Volatile.Read(ref _closed) != 0)
        {
            return EmptyDisposable.Instance;
        }

        return TrackSubscription(_ui.OnControlEvent(WindowId, controlPath, eventName, handler));
    }

    /// <summary>Subscribes asynchronously to a named control event on <paramref name="controlPath"/> inside this window.</summary>
    public IDisposable OnControlEvent(string controlPath, string eventName, Func<IClientSession, string?, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(handler);
        if (Volatile.Read(ref _closed) != 0)
        {
            return EmptyDisposable.Instance;
        }

        return TrackSubscription(_ui.OnControlEvent(WindowId, controlPath, eventName, handler));
    }

    /// <summary>Subscribes asynchronously with cancellation support to a named control event on <paramref name="controlPath"/> inside this window.</summary>
    public IDisposable OnControlEvent(string controlPath, string eventName, Func<IClientSession, string?, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(handler);
        if (Volatile.Read(ref _closed) != 0)
        {
            return EmptyDisposable.Instance;
        }

        return TrackSubscription(_ui.OnControlEvent(WindowId, controlPath, eventName, handler));
    }

    /// <summary>Subscribes to a named control event on <paramref name="controlPath"/> and awaits remote subscription delivery.</summary>
    public async Task<IDisposable> OnControlEventAsync(string controlPath, string eventName, Action<IClientSession, string?> handler, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(handler);
        if (Volatile.Read(ref _closed) != 0)
        {
            return EmptyDisposable.Instance;
        }

        return TrackSubscription(await _ui.OnControlEventAsync(WindowId, controlPath, eventName, handler, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Subscribes asynchronously to a named control event on <paramref name="controlPath"/> and awaits remote subscription delivery.</summary>
    public async Task<IDisposable> OnControlEventAsync(string controlPath, string eventName, Func<IClientSession, string?, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(handler);
        if (Volatile.Read(ref _closed) != 0)
        {
            return EmptyDisposable.Instance;
        }

        return TrackSubscription(await _ui.OnControlEventAsync(WindowId, controlPath, eventName, handler, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Subscribes asynchronously with cancellation support to a named control event on <paramref name="controlPath"/> and awaits remote subscription delivery.</summary>
    public async Task<IDisposable> OnControlEventAsync(string controlPath, string eventName, Func<IClientSession, string?, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(handler);
        if (Volatile.Read(ref _closed) != 0)
        {
            return EmptyDisposable.Instance;
        }

        return TrackSubscription(await _ui.OnControlEventAsync(WindowId, controlPath, eventName, handler, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Reloads this window from its <c>.ui</c> overlay.</summary>
    public Task ReloadAsync(CancellationToken cancellationToken = default) =>
        _ui.ReloadAsync(WindowId, cancellationToken);

    /// <summary>Closes this window and raises <see cref="Closed"/> once.</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default) =>
        _ui.CloseAsync(WindowId, cancellationToken);

    /// <summary>Begins a command/UI batch on the underlying session.</summary>
    public IClientCommandBatch BeginBatch() => _ui.BeginBatch();

    /// <summary>Flushes any buffered commands on the underlying session.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) => _ui.FlushAsync(cancellationToken);

    /// <summary>Executes <paramref name="action"/> inside a UI batch targeting this window, flushing on success or discarding on exception.</summary>
    public async Task BatchAsync(Func<ServerUiWindowHandle, Task> action, CancellationToken cancellationToken = default)
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

    /// <summary>Executes <paramref name="action"/> inside a UI batch targeting this window with cancellation, flushing on success or discarding on exception.</summary>
    public async Task BatchAsync(Func<ServerUiWindowHandle, CancellationToken, Task> action, CancellationToken cancellationToken = default)
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

    internal async Task MarkClosedAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        IDisposable[] subs;
        Func<CancellationToken, Task>[] asyncHandlers;
        lock (_gate)
        {
            subs = [.. _boundSubscriptions];
            _boundSubscriptions.Clear();
            asyncHandlers = [.. _closedHandlers];
            _closedHandlers.Clear();
        }

        foreach (IDisposable sub in subs)
        {
            try
            {
                sub.Dispose();
            }
            catch (Exception ex)
            {
                _reportError?.Invoke($"Window({WindowId}).SubscriptionDispose", ex);
            }
        }

        if (Closed is { } closedEvent)
        {
            foreach (Delegate del in closedEvent.GetInvocationList())
            {
                try
                {
                    ((Action)del)();
                }
                catch (Exception ex)
                {
                    _reportError?.Invoke($"Window({WindowId}).Closed", ex);
                }
            }
        }

        foreach (Func<CancellationToken, Task> handler in asyncHandlers)
        {
            try
            {
                await handler(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _reportError?.Invoke($"Window({WindowId}).OnClosed", ex);
            }
        }
    }

    internal void MarkClosedNonBlocking()
    {
        Task task = MarkClosedAsync(CancellationToken.None);
        if (task.IsFaulted && task.Exception is not null)
        {
            _reportError?.Invoke($"Window({WindowId}).Closed", task.Exception.GetBaseException());
        }
        else if (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                t => _reportError?.Invoke($"Window({WindowId}).Closed", t.Exception?.GetBaseException() ?? new InvalidOperationException("Window closed handler failed.")),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private IDisposable TrackSubscription(IDisposable inner)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                inner.Dispose();
                return EmptyDisposable.Instance;
            }

            _boundSubscriptions.Add(inner);
        }

        return new CallbackDisposable(() =>
        {
            lock (_gate)
            {
                _boundSubscriptions.Remove(inner);
            }

            inner.Dispose();
        });
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class CallbackDisposable(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;
        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}

/// <summary>
/// Per-connection NativeMods session tracked by <see cref="ClientBridgeHost"/> on the LocalExile Gateway.
/// </summary>
public interface IClientSession : IClientPhase1Session
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

    /// <summary>Identifiers of installed client mods reported during handshake.</summary>
    IReadOnlyCollection<string> ClientMods { get; }

    /// <summary>Server-driven UI helper for this connection.</summary>
    IClientUiSession Ui { get; }

    /// <summary>Tier 1 <c>0.3.0</c> Phase 1 session facade (slots, HUD toasts, hotkeys, screen/hover queries).</summary>
    IClientPhase1Session Phase1 { get; }

    /// <summary>
    /// Checks whether this client has installed the client mod <paramref name="modId"/>
    /// (and optionally verifies that its reported payload hash matches <paramref name="expectedPayloadHash"/>).
    /// Enables graceful server-side degradation for players without the client mod.
    /// </summary>
    bool HasClientMod(string modId, string? expectedPayloadHash = null);

    /// <summary>Sends a typed message record to this client on <paramref name="channel"/>.</summary>
    Task SendAsync<T>(string channel, T message, string? messageType = null, CancellationToken cancellationToken = default);

    /// <summary>Sends a typed message record to this client using the host's default channel.</summary>
    Task SendAsync<T>(T message, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to incoming typed messages from this client on <paramref name="channel"/>.</summary>
    IDisposable On<T>(string channel, Action<IClientSession, T> handler, string? messageType = null);

    /// <summary>Subscribes asynchronously to incoming typed messages from this client on <paramref name="channel"/>.</summary>
    IDisposable On<T>(string channel, Func<IClientSession, T, Task> handler, string? messageType = null);

    /// <summary>Subscribes asynchronously with cancellation support to incoming typed messages from this client on <paramref name="channel"/>.</summary>
    IDisposable On<T>(string channel, Func<IClientSession, T, CancellationToken, Task> handler, string? messageType = null);

    /// <summary>Subscribes to incoming typed messages on any channel.</summary>
    IDisposable On<T>(Action<IClientSession, T> handler, string? messageType = null);

    /// <summary>Subscribes asynchronously to incoming typed messages on any channel.</summary>
    IDisposable On<T>(Func<IClientSession, T, Task> handler, string? messageType = null);

    /// <summary>Subscribes asynchronously with cancellation support to incoming typed messages on any channel.</summary>
    IDisposable On<T>(Func<IClientSession, T, CancellationToken, Task> handler, string? messageType = null);

    /// <summary>Begins a command/UI batch on this session.</summary>
    IClientCommandBatch BeginBatch();

    /// <summary>Flushes any buffered outbound commands/messages on this session.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>Executes <paramref name="action"/> inside a session batch, flushing on success or discarding on exception.</summary>
    Task BatchAsync(Func<IClientSession, Task> action, CancellationToken cancellationToken = default);

    /// <summary>Executes <paramref name="action"/> inside a session batch with cancellation, flushing on success or discarding on exception.</summary>
    Task BatchAsync(Func<IClientSession, CancellationToken, Task> action, CancellationToken cancellationToken = default);
}
