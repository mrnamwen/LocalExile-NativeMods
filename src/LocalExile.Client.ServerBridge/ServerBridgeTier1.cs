using System.Collections.Concurrent;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Protocol;

namespace LocalExile.Client.ServerBridge;

/// <summary>
/// Wire message type names exchanged on <see cref="MessageCodec.SystemChannel"/> between
/// Tier 1 <c>ServerBridge 0.3.0</c> sessions and the client host's <c>ServerBridgeTier1Handler</c>.
/// </summary>
public static class Tier1MessageTypes
{
    /// <summary>S→C command registering or updating a managed <see cref="ScreenSlot"/> toolbar button.</summary>
    public const string RegisterSlotButton = "Tier1RegisterSlotButton";

    /// <summary>S→C command removing a previously registered <see cref="ScreenSlot"/> toolbar button.</summary>
    public const string RemoveSlotButton = "Tier1RemoveSlotButton";

    /// <summary>C→S event emitted when a player clicks a server-registered <see cref="ScreenSlot"/> button.</summary>
    public const string SlotButtonClick = "Tier1SlotButtonClick";

    /// <summary>S→C command displaying a NativeMods HUD toast notification.</summary>
    public const string ShowToast = "Tier1ShowToast";

    /// <summary>S→C command registering or updating a centralized keybind/hotkey (keyboard or mouse chord).</summary>
    public const string RegisterHotkey = "Tier1RegisterHotkey";

    /// <summary>S→C command unregistering a centralized keybind/hotkey.</summary>
    public const string UnregisterHotkey = "Tier1UnregisterHotkey";

    /// <summary>C→S event emitted when a server-registered hotkey fires on the client.</summary>
    public const string HotkeyTriggered = "Tier1HotkeyTriggered";

    /// <summary>S→C request querying the client's current screen, open panels, and active stash tab.</summary>
    public const string QueryScreenState = "Tier1QueryScreenState";

    /// <summary>C→S response carrying a <see cref="ClientScreenStateSnapshot"/>.</summary>
    public const string ScreenStateResponse = "Tier1ScreenStateResponse";

    /// <summary>S→C command enabling or disabling opt-in screen/stash-tab change notifications.</summary>
    public const string SetScreenSubscription = "Tier1SetScreenSubscription";

    /// <summary>C→S push notification emitted when screen, panel, or stash-tab state changes.</summary>
    public const string ScreenStateChanged = "Tier1ScreenStateChanged";

    /// <summary>S→C request querying the client's hovered UI control, hovered item, and cursor item.</summary>
    public const string QueryHoverState = "Tier1QueryHoverState";

    /// <summary>C→S response carrying a <see cref="ClientHoverStateSnapshot"/>.</summary>
    public const string HoverStateResponse = "Tier1HoverStateResponse";

    /// <summary>S→C command enabling or disabling opt-in hover/cursor change notifications.</summary>
    public const string SetHoverSubscription = "Tier1SetHoverSubscription";

    /// <summary>C→S push notification emitted when hovered control, hovered item, or cursor item changes.</summary>
    public const string HoverStateChanged = "Tier1HoverStateChanged";
}

/// <summary>
/// Serializable snapshot of a connected client's active screen state, open stock UI panels, and active stash tab.
/// </summary>
/// <param name="CurrentPreGameScreen">Active pre-game or in-game screen state.</param>
/// <param name="ActivePanels">Bitmask of currently open stock in-game UI panels.</param>
/// <param name="ActiveStashTab">Metadata for the active stash tab (including active map tier), or <c>null</c> when stash is closed.</param>
public sealed record ClientScreenStateSnapshot(
    PreGameScreen CurrentPreGameScreen,
    GamePanel ActivePanels,
    StashTabInfo? ActiveStashTab = null)
{
    /// <summary>Returns <c>true</c> when <see cref="CurrentPreGameScreen"/> is <see cref="PreGameScreen.InGame"/>.</summary>
    public bool IsInGame => CurrentPreGameScreen == PreGameScreen.InGame;

    /// <summary>Returns <c>true</c> if <paramref name="panel"/> is currently open.</summary>
    public bool IsPanelOpen(GamePanel panel) => panel != GamePanel.None && (ActivePanels & panel) == panel;

    /// <summary>Active map stash tab tier (1–17) when viewing a Map stash tab, or <c>null</c>.</summary>
    public int? ActiveMapTier => ActiveStashTab?.MapTier;
}

/// <summary>
/// Opt-in notification payload emitted when a client's screen, open panels, or active stash tab changes.
/// </summary>
/// <param name="State">Latest screen and stash tab snapshot.</param>
/// <param name="ChangedPanel">The panel that opened or closed, if this event was triggered by a panel toggle.</param>
/// <param name="PanelIsOpen"><c>true</c> if <paramref name="ChangedPanel"/> opened; <c>false</c> if it closed.</param>
/// <param name="StashTabChanged"><c>true</c> if this event was triggered by an active stash tab or map tier change.</param>
/// <param name="PreGameChanged"><c>true</c> if this event was triggered by a pre-game/in-game screen transition.</param>
public sealed record ClientScreenChangedEvent(
    ClientScreenStateSnapshot State,
    GamePanel? ChangedPanel = null,
    bool? PanelIsOpen = null,
    bool StashTabChanged = false,
    bool PreGameChanged = false);

/// <summary>
/// JSON-serializable snapshot of the inventory or stash item currently hovered by a client's mouse cursor.
/// </summary>
/// <param name="ItemId">Server/client item identifier within its inventory.</param>
/// <param name="InventoryId">Owning inventory identifier.</param>
/// <param name="GridX">Top-left X cell coordinate inside the inventory grid.</param>
/// <param name="GridY">Top-left Y cell coordinate inside the inventory grid.</param>
/// <param name="Width">Item width in grid cells.</param>
/// <param name="Height">Item height in grid cells.</param>
/// <param name="MetadataPath"><c>BaseItemTypes</c> metadata path.</param>
/// <param name="BaseName">Localized base item display name.</param>
/// <param name="StackSize">Current stack count.</param>
/// <param name="MaxStackSize">Maximum stack size.</param>
/// <param name="Rarity">Item rarity tier.</param>
/// <param name="IsIdentified"><c>true</c> if the item is identified.</param>
/// <param name="IsCorrupted"><c>true</c> if the item is corrupted.</param>
/// <param name="ItemLevel">Item level, or <c>0</c> if unavailable.</param>
/// <param name="ItemClassId">1-based <c>ItemClasses</c> identifier, or <c>0</c> if unavailable.</param>
/// <param name="EntityHandle">Optional client-side native <c>ItemEntity*</c> address for diagnostics.</param>
/// <param name="ControlHandle">Optional client-side native <c>InventoryItemView*</c> address for diagnostics.</param>
public sealed record ClientHoveredItemSnapshot(
    uint ItemId,
    uint InventoryId,
    int GridX,
    int GridY,
    int Width,
    int Height,
    string MetadataPath,
    string BaseName,
    uint StackSize,
    uint MaxStackSize,
    ItemRarity Rarity,
    bool IsIdentified,
    bool IsCorrupted,
    int ItemLevel = 0,
    uint ItemClassId = 0,
    ulong EntityHandle = 0,
    ulong ControlHandle = 0);

/// <summary>
/// JSON-serializable snapshot of the item currently held on a client's cursor or active in currency-use mode.
/// </summary>
/// <param name="Mode">Active cursor mode (<see cref="CursorMode.HoldingItem"/> or <see cref="CursorMode.UsingItem"/>).</param>
/// <param name="ItemId">Item identifier in the cursor or source inventory.</param>
/// <param name="SourceInventoryId">Source inventory identifier.</param>
/// <param name="SourceGridX">Source X grid cell when in <see cref="CursorMode.UsingItem"/> mode.</param>
/// <param name="SourceGridY">Source Y grid cell when in <see cref="CursorMode.UsingItem"/> mode.</param>
/// <param name="Width">Item width in grid cells.</param>
/// <param name="Height">Item height in grid cells.</param>
/// <param name="MetadataPath"><c>BaseItemTypes</c> metadata path.</param>
/// <param name="BaseName">Localized base item display name.</param>
/// <param name="StackSize">Current stack count.</param>
/// <param name="MaxStackSize">Maximum stack size.</param>
/// <param name="Rarity">Item rarity tier.</param>
/// <param name="IsIdentified"><c>true</c> if identified.</param>
/// <param name="IsCorrupted"><c>true</c> if corrupted.</param>
/// <param name="ShiftKeep"><c>true</c> if Shift is keeping currency-use mode active.</param>
/// <param name="EntityHandle">Optional client-side native <c>ItemEntity*</c> address for diagnostics.</param>
public sealed record ClientCursorItemSnapshot(
    CursorMode Mode,
    uint ItemId,
    uint SourceInventoryId,
    int SourceGridX,
    int SourceGridY,
    int Width,
    int Height,
    string MetadataPath,
    string BaseName,
    uint StackSize,
    uint MaxStackSize,
    ItemRarity Rarity,
    bool IsIdentified,
    bool IsCorrupted,
    bool ShiftKeep = false,
    ulong EntityHandle = 0);

/// <summary>
/// Serializable snapshot of a client's currently hovered UI control, hovered inventory/stash item, and cursor item.
/// </summary>
/// <param name="HoveredControlId">Identifier of the hovered UI control, or <c>null</c>.</param>
/// <param name="HoveredControlHandle">Client-side native control handle of the hovered control, or <c>0</c>.</param>
/// <param name="HoveredItem">Currently hovered inventory/stash item, or <c>null</c>.</param>
/// <param name="CursorItem">Item currently held on the cursor or active in currency-use mode, or <c>null</c>.</param>
public sealed record ClientHoverStateSnapshot(
    string? HoveredControlId = null,
    ulong HoveredControlHandle = 0,
    ClientHoveredItemSnapshot? HoveredItem = null,
    ClientCursorItemSnapshot? CursorItem = null);

/// <summary>
/// Payload emitted when a server-registered centralized keybind or suppressible mouse chord fires on the client.
/// </summary>
/// <param name="Id">Registered hotkey identifier.</param>
/// <param name="ActiveChord">Canonical string representation of the chord that fired (for example <c>Ctrl+Shift+RMB</c>).</param>
/// <param name="IsTextInputFocused"><c>true</c> if a text input was focused when the chord fired.</param>
/// <param name="MouseX">Client-space cursor X coordinate at trigger time.</param>
/// <param name="MouseY">Client-space cursor Y coordinate at trigger time.</param>
public sealed record ClientHotkeyTriggeredEvent(
    string Id,
    string ActiveChord,
    bool IsTextInputFocused = false,
    int MouseX = 0,
    int MouseY = 0);

/// <summary>
/// Ergonomic handle representing a server-registered native toolbar button in a managed <see cref="ScreenSlot"/>.
/// </summary>
public sealed class ServerSlotButtonHandle : IAsyncDisposable
{
    private readonly IClientPhase1Session _phase1;
    private readonly List<IDisposable> _boundSubscriptions = [];
    private readonly Lock _gate = new();
    private int _removed;

    /// <summary>Target managed screen slot.</summary>
    public ScreenSlot Slot { get; }

    /// <summary>Unique button identifier within <see cref="Slot"/>.</summary>
    public string Id { get; }

    /// <summary>Button caption text.</summary>
    public string Text { get; }

    /// <summary>Whether this button is still registered.</summary>
    public bool IsRegistered => Volatile.Read(ref _removed) == 0;

    internal ServerSlotButtonHandle(IClientPhase1Session phase1, ScreenSlot slot, string id, string text)
    {
        _phase1 = phase1;
        Slot = slot;
        Id = id;
        Text = text;
    }

    /// <summary>Subscribes a synchronous click callback bound to this button's lifetime.</summary>
    public async Task<IDisposable> OnClickAsync(Action<IClientSession> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!IsRegistered)
        {
            return ClientBridgeHost.NoOpDisposable.Instance;
        }

        return TrackSubscription(await _phase1.OnSlotButtonClickAsync(Slot, Id, handler, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Subscribes an asynchronous click callback bound to this button's lifetime.</summary>
    public async Task<IDisposable> OnClickAsync(Func<IClientSession, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!IsRegistered)
        {
            return ClientBridgeHost.NoOpDisposable.Instance;
        }

        return TrackSubscription(await _phase1.OnSlotButtonClickAsync(Slot, Id, handler, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Subscribes an asynchronous click callback with cancellation support bound to this button's lifetime.</summary>
    public async Task<IDisposable> OnClickAsync(Func<IClientSession, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!IsRegistered)
        {
            return ClientBridgeHost.NoOpDisposable.Instance;
        }

        return TrackSubscription(await _phase1.OnSlotButtonClickAsync(Slot, Id, handler, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Removes this button from the client host and disposes bound click subscriptions.</summary>
    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _removed, 1) != 0)
        {
            return;
        }

        DisposeSubscriptions();
        await _phase1.RemoveSlotButtonAsync(Slot, Id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await RemoveAsync(CancellationToken.None).ConfigureAwait(false);

    internal void TrackBoundSubscription(IDisposable subscription) => _ = TrackSubscription(subscription);

    internal void MarkRemovedLocally()
    {
        if (Interlocked.Exchange(ref _removed, 1) != 0)
        {
            return;
        }

        DisposeSubscriptions();
    }

    private IDisposable TrackSubscription(IDisposable inner)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _removed) != 0)
            {
                inner.Dispose();
                return ClientBridgeHost.NoOpDisposable.Instance;
            }

            _boundSubscriptions.Add(inner);
        }

        return new ClientBridgeHost.Subscription(() =>
        {
            lock (_gate)
            {
                _boundSubscriptions.Remove(inner);
            }

            inner.Dispose();
        });
    }

    private void DisposeSubscriptions()
    {
        IDisposable[] subs;
        lock (_gate)
        {
            subs = [.. _boundSubscriptions];
            _boundSubscriptions.Clear();
        }

        foreach (IDisposable sub in subs)
        {
            sub.Dispose();
        }
    }
}

/// <summary>
/// Ergonomic handle representing a server-registered centralized keybind/hotkey on a connected client.
/// </summary>
public sealed class ServerHotkeyHandle : IAsyncDisposable
{
    private readonly IClientPhase1Session _phase1;
    private readonly List<IDisposable> _boundSubscriptions = [];
    private readonly Lock _gate = new();
    private int _unregistered;

    /// <summary>Stable keybind identifier.</summary>
    public string Id { get; }

    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; }

    /// <summary>Default key or mouse chord registered for this hotkey.</summary>
    public KeyChord DefaultChord { get; }

    /// <summary>Whether this hotkey is still registered.</summary>
    public bool IsRegistered => Volatile.Read(ref _unregistered) == 0;

    internal ServerHotkeyHandle(IClientPhase1Session phase1, string id, string displayName, KeyChord defaultChord)
    {
        _phase1 = phase1;
        Id = id;
        DisplayName = displayName;
        DefaultChord = defaultChord;
    }

    /// <summary>Subscribes a synchronous trigger callback bound to this hotkey's lifetime.</summary>
    public async Task<IDisposable> OnTriggeredAsync(Action<IClientSession, ClientHotkeyTriggeredEvent> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!IsRegistered)
        {
            return ClientBridgeHost.NoOpDisposable.Instance;
        }

        return TrackSubscription(await _phase1.OnHotkeyAsync(Id, handler, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Subscribes an asynchronous trigger callback bound to this hotkey's lifetime.</summary>
    public async Task<IDisposable> OnTriggeredAsync(Func<IClientSession, ClientHotkeyTriggeredEvent, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!IsRegistered)
        {
            return ClientBridgeHost.NoOpDisposable.Instance;
        }

        return TrackSubscription(await _phase1.OnHotkeyAsync(Id, handler, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Subscribes an asynchronous trigger callback with cancellation support bound to this hotkey's lifetime.</summary>
    public async Task<IDisposable> OnTriggeredAsync(Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!IsRegistered)
        {
            return ClientBridgeHost.NoOpDisposable.Instance;
        }

        return TrackSubscription(await _phase1.OnHotkeyAsync(Id, handler, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Unregisters this hotkey on the client host and disposes bound callbacks.</summary>
    public async Task UnregisterAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _unregistered, 1) != 0)
        {
            return;
        }

        DisposeSubscriptions();
        await _phase1.UnregisterHotkeyAsync(Id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await UnregisterAsync(CancellationToken.None).ConfigureAwait(false);

    internal void TrackBoundSubscription(IDisposable subscription) => _ = TrackSubscription(subscription);

    internal void MarkUnregisteredLocally()
    {
        if (Interlocked.Exchange(ref _unregistered, 1) != 0)
        {
            return;
        }

        DisposeSubscriptions();
    }

    private IDisposable TrackSubscription(IDisposable inner)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _unregistered) != 0)
            {
                inner.Dispose();
                return ClientBridgeHost.NoOpDisposable.Instance;
            }

            _boundSubscriptions.Add(inner);
        }

        return new ClientBridgeHost.Subscription(() =>
        {
            lock (_gate)
            {
                _boundSubscriptions.Remove(inner);
            }

            inner.Dispose();
        });
    }

    private void DisposeSubscriptions()
    {
        IDisposable[] subs;
        lock (_gate)
        {
            subs = [.. _boundSubscriptions];
            _boundSubscriptions.Clear();
        }

        foreach (IDisposable sub in subs)
        {
            sub.Dispose();
        }
    }
}

/// <summary>
/// Tier 1 <c>ServerBridge 0.3.0</c> session surface allowing single-<c>.cs</c> server mods (with zero custom client DLL)
/// to attach native toolbar buttons to managed <see cref="ScreenSlot"/>s, display HUD toasts, register suppressible
/// keybinds/mouse chords, and query or subscribe to client screen, stash-tab, and item hover state.
/// </summary>
public interface IClientPhase1Session
{
    /// <summary>
    /// Registers (or updates) a native toolbar button inside a managed conflict-free <see cref="ScreenSlot"/>
    /// (such as <see cref="ScreenSlot.InventoryToolbar"/>, <see cref="ScreenSlot.StashHeader"/>,
    /// <see cref="ScreenSlot.StashFooter"/>, <see cref="ScreenSlot.MapTabBar"/>, <see cref="ScreenSlot.BlightTabBar"/>,
    /// <see cref="ScreenSlot.PassiveTreeHeader"/>, or <see cref="ScreenSlot.HudTopRight"/>).
    /// </summary>
    Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(
        ScreenSlot slot,
        string id,
        string text,
        SlotAttachmentOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers (or updates) a native toolbar button in <paramref name="slot"/> and binds a synchronous click handler.
    /// </summary>
    Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(
        ScreenSlot slot,
        string id,
        string text,
        Action<IClientSession> onClick,
        SlotAttachmentOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers (or updates) a native toolbar button in <paramref name="slot"/> and binds an asynchronous click handler.
    /// </summary>
    Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(
        ScreenSlot slot,
        string id,
        string text,
        Func<IClientSession, Task> onClick,
        SlotAttachmentOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers (or updates) a native toolbar button in <paramref name="slot"/> and binds an asynchronous click handler with cancellation support.
    /// </summary>
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

    /// <summary>Displays a NativeMods HUD toast notification anchored to <see cref="ScreenSlot.HudTopRight"/>.</summary>
    /// <param name="title">Short toast header line.</param>
    /// <param name="message">Toast body text.</param>
    /// <param name="severity">Visual severity tier.</param>
    /// <param name="durationMs">Display duration in milliseconds (defaults to <c>4000</c>).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    Task ShowToastAsync(
        string title,
        string message,
        ToastSeverity severity = ToastSeverity.Info,
        int durationMs = 4000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers (or updates) a centralized keybind/hotkey on the client, including suppressible mouse chords
    /// such as <c>KeyChord.FromMouse(MouseButton.Right, KeyModifiers.Control | KeyModifiers.Shift)</c>.
    /// </summary>
    Task<ServerHotkeyHandle> RegisterHotkeyAsync(
        string id,
        string displayName,
        KeyChord defaultChord,
        bool allowWhenTextInputFocused = false,
        bool suppressGameInput = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers (or updates) a centralized keybind/hotkey from a canonical chord string (for example <c>"Ctrl+Shift+RMB"</c> or <c>"Ctrl+F6"</c>).
    /// </summary>
    Task<ServerHotkeyHandle> RegisterHotkeyAsync(
        string id,
        string displayName,
        string defaultChord,
        bool allowWhenTextInputFocused = false,
        bool suppressGameInput = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers (or updates) a centralized hotkey and binds an asynchronous trigger callback.
    /// </summary>
    Task<ServerHotkeyHandle> RegisterHotkeyAsync(
        string id,
        string displayName,
        KeyChord defaultChord,
        Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task> onTriggered,
        bool allowWhenTextInputFocused = false,
        bool suppressGameInput = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers (or updates) a centralized hotkey from a canonical chord string (for example <c>"Ctrl+Shift+RMB"</c>)
    /// and binds an asynchronous trigger callback.
    /// </summary>
    Task<ServerHotkeyHandle> RegisterHotkeyAsync(
        string id,
        string displayName,
        string defaultChord,
        Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task> onTriggered,
        bool allowWhenTextInputFocused = false,
        bool suppressGameInput = false,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes synchronously to trigger events for hotkey <paramref name="id"/>.</summary>
    Task<IDisposable> OnHotkeyAsync(
        string id,
        Action<IClientSession, ClientHotkeyTriggeredEvent> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously to trigger events for hotkey <paramref name="id"/>.</summary>
    Task<IDisposable> OnHotkeyAsync(
        string id,
        Func<IClientSession, ClientHotkeyTriggeredEvent, Task> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously with cancellation support to trigger events for hotkey <paramref name="id"/>.</summary>
    Task<IDisposable> OnHotkeyAsync(
        string id,
        Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Unregisters a previously registered hotkey from the client host.</summary>
    Task UnregisterHotkeyAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Queries the client host's current active screen, open panels, active stash tab, and map tier.</summary>
    Task<ClientScreenStateSnapshot> QueryScreenStateAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>Queries the client host's currently hovered UI control, hovered inventory/stash item, and cursor item.</summary>
    Task<ClientHoverStateSnapshot> QueryHoverStateAsync(
        bool refresh = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes synchronously to opt-in client screen/panel/stash-tab change notifications.</summary>
    Task<IDisposable> OnScreenChangedAsync(
        Action<IClientSession, ClientScreenChangedEvent> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously to opt-in client screen/panel/stash-tab change notifications.</summary>
    Task<IDisposable> OnScreenChangedAsync(
        Func<IClientSession, ClientScreenChangedEvent, Task> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously with cancellation support to opt-in client screen/panel/stash-tab change notifications.</summary>
    Task<IDisposable> OnScreenChangedAsync(
        Func<IClientSession, ClientScreenChangedEvent, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes synchronously to opt-in client hover/cursor state change notifications.</summary>
    Task<IDisposable> OnHoverChangedAsync(
        Action<IClientSession, ClientHoverStateSnapshot> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously to opt-in client hover/cursor state change notifications.</summary>
    Task<IDisposable> OnHoverChangedAsync(
        Func<IClientSession, ClientHoverStateSnapshot, Task> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously with cancellation support to opt-in client hover/cursor state change notifications.</summary>
    Task<IDisposable> OnHoverChangedAsync(
        Func<IClientSession, ClientHoverStateSnapshot, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default);
}

// -----------------------------------------------------------------------------
// Internal Wire DTOs (JSON schema shared with LocalExile.Client.Host.Services.ServerBridgeTier1Handler)
// -----------------------------------------------------------------------------

internal sealed record Tier1RegisterSlotButtonPayload(
    ScreenSlot Slot,
    string Id,
    string Text,
    int Order = 0,
    float Width = 0f,
    float Height = 0f,
    float Spacing = 6f,
    string? Tooltip = null);

internal sealed record Tier1RemoveSlotButtonPayload(ScreenSlot Slot, string Id);

internal sealed record Tier1SlotButtonClickPayload(ScreenSlot Slot, string Id);

internal sealed record Tier1ShowToastPayload(
    string Title,
    string Message,
    ToastSeverity Severity = ToastSeverity.Info,
    int DurationMs = 4000);

internal sealed record Tier1RegisterHotkeyPayload(
    string Id,
    string DisplayName,
    string DefaultChord,
    bool AllowWhenTextInputFocused = false,
    bool SuppressGameInput = false);

internal sealed record Tier1UnregisterHotkeyPayload(string Id);

internal sealed record Tier1QueryScreenStatePayload(string RequestId);

internal sealed record Tier1ScreenStateResponsePayload(string RequestId, ClientScreenStateSnapshot State);

internal sealed record Tier1SetSubscriptionPayload(bool Enabled);

internal sealed record Tier1QueryHoverStatePayload(string RequestId, bool Refresh = true);

internal sealed record Tier1HoverStateResponsePayload(string RequestId, ClientHoverStateSnapshot State);

// -----------------------------------------------------------------------------
// Session Implementation
// -----------------------------------------------------------------------------

internal sealed class ClientPhase1Session(ClientSession session, ClientBridgeHost host) : IClientPhase1Session
{
    private static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(5);

    private readonly Dictionary<(ScreenSlot Slot, string Id), List<Func<IClientSession, CancellationToken, Task>>> _slotClickHandlers = [];
    private readonly Dictionary<(ScreenSlot Slot, string Id), ServerSlotButtonHandle> _slotButtonHandles = [];
    private readonly Dictionary<string, List<Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task>>> _hotkeyHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServerHotkeyHandle> _hotkeyHandles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Func<IClientSession, ClientScreenChangedEvent, CancellationToken, Task>> _screenChangedHandlers = [];
    private readonly List<Func<IClientSession, ClientHoverStateSnapshot, CancellationToken, Task>> _hoverChangedHandlers = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ClientScreenStateSnapshot>> _pendingScreenQueries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ClientHoverStateSnapshot>> _pendingHoverQueries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private long _nextRequestSeq;
    private bool _closed;

    public async Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(
        ScreenSlot slot,
        string id,
        string text,
        SlotAttachmentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (slot == ScreenSlot.LeagueCardModes)
        {
            throw new ArgumentException("ScreenSlot.LeagueCardModes is reserved for pre-game league mode checkboxes, not toolbar buttons.", nameof(slot));
        }

        string trimmedId = id.Trim();
        string normalizedId = trimmedId.ToLowerInvariant();
        SlotAttachmentOptions opts = options ?? new SlotAttachmentOptions();
        var handle = new ServerSlotButtonHandle(this, slot, trimmedId, text ?? string.Empty);

        ServerSlotButtonHandle? replaced = null;
        bool isClosed;
        lock (_gate)
        {
            isClosed = _closed || session.IsClosed;
            if (!isClosed)
            {
                var key = (slot, normalizedId);
                _slotButtonHandles.TryGetValue(key, out replaced);
                _slotButtonHandles[key] = handle;
            }
        }

        if (isClosed)
        {
            handle.MarkRemovedLocally();
            return handle;
        }

        replaced?.MarkRemovedLocally();

        var payload = new Tier1RegisterSlotButtonPayload(
            slot,
            trimmedId,
            text ?? string.Empty,
            opts.Order,
            opts.Width,
            opts.Height,
            opts.Spacing,
            opts.Tooltip);

        await session.QueueOrSendSystemMessageAsync(Tier1MessageTypes.RegisterSlotButton, payload, cancellationToken).ConfigureAwait(false);
        return handle;
    }

    public async Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(
        ScreenSlot slot,
        string id,
        string text,
        Action<IClientSession> onClick,
        SlotAttachmentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onClick);
        return await RegisterSlotButtonAsync(
            slot,
            id,
            text,
            (s, _) =>
            {
                onClick(s);
                return Task.CompletedTask;
            },
            options,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(
        ScreenSlot slot,
        string id,
        string text,
        Func<IClientSession, Task> onClick,
        SlotAttachmentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onClick);
        return await RegisterSlotButtonAsync(
            slot,
            id,
            text,
            (s, _) => onClick(s),
            options,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServerSlotButtonHandle> RegisterSlotButtonAsync(
        ScreenSlot slot,
        string id,
        string text,
        Func<IClientSession, CancellationToken, Task> onClick,
        SlotAttachmentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onClick);
        ServerSlotButtonHandle handle = await RegisterSlotButtonAsync(slot, id, text, options, cancellationToken).ConfigureAwait(false);
        if (handle.IsRegistered)
        {
            IDisposable sub = await OnSlotButtonClickAsync(slot, handle.Id, onClick, cancellationToken).ConfigureAwait(false);
            handle.TrackBoundSubscription(sub);
        }

        return handle;
    }

    public async Task RemoveSlotButtonAsync(ScreenSlot slot, string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        string trimmedId = id.Trim();
        var key = (slot, trimmedId.ToLowerInvariant());

        ServerSlotButtonHandle? handle;
        lock (_gate)
        {
            _slotButtonHandles.Remove(key, out handle);
            _slotClickHandlers.Remove(key);
        }

        handle?.MarkRemovedLocally();
        await session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.RemoveSlotButton,
            new Tier1RemoveSlotButtonPayload(slot, trimmedId),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<IDisposable> OnSlotButtonClickAsync(
        ScreenSlot slot,
        string id,
        Action<IClientSession> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnSlotButtonClickAsync(
            slot,
            id,
            (s, _) =>
            {
                handler(s);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    public Task<IDisposable> OnSlotButtonClickAsync(
        ScreenSlot slot,
        string id,
        Func<IClientSession, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnSlotButtonClickAsync(slot, id, (s, _) => handler(s), cancellationToken);
    }

    public Task<IDisposable> OnSlotButtonClickAsync(
        ScreenSlot slot,
        string id,
        Func<IClientSession, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(handler);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<IDisposable>(cancellationToken);
        }

        var key = (slot, id.Trim().ToLowerInvariant());
        lock (_gate)
        {
            if (_closed || session.IsClosed)
            {
                return Task.FromResult<IDisposable>(ClientBridgeHost.NoOpDisposable.Instance);
            }

            if (!_slotClickHandlers.TryGetValue(key, out List<Func<IClientSession, CancellationToken, Task>>? list))
            {
                list = [];
                _slotClickHandlers[key] = list;
            }

            list.Add(handler);
        }

        IDisposable sub = new ClientBridgeHost.Subscription(() =>
        {
            lock (_gate)
            {
                if (_slotClickHandlers.TryGetValue(key, out List<Func<IClientSession, CancellationToken, Task>>? list))
                {
                    list.Remove(handler);
                    if (list.Count == 0)
                    {
                        _slotClickHandlers.Remove(key);
                    }
                }
            }
        });

        return Task.FromResult(sub);
    }

    public Task ShowToastAsync(
        string title,
        string message,
        ToastSeverity severity = ToastSeverity.Info,
        int durationMs = 4000,
        CancellationToken cancellationToken = default)
    {
        var payload = new Tier1ShowToastPayload(
            title ?? string.Empty,
            message ?? string.Empty,
            severity,
            durationMs);
        return session.QueueOrSendSystemMessageAsync(Tier1MessageTypes.ShowToast, payload, cancellationToken);
    }

    public async Task<ServerHotkeyHandle> RegisterHotkeyAsync(
        string id,
        string displayName,
        KeyChord defaultChord,
        bool allowWhenTextInputFocused = false,
        bool suppressGameInput = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (defaultChord.IsEmpty)
        {
            throw new ArgumentException("Default keybind chord must not be empty.", nameof(defaultChord));
        }

        string trimmedId = id.Trim();
        var handle = new ServerHotkeyHandle(this, trimmedId, displayName.Trim(), defaultChord);

        ServerHotkeyHandle? replaced = null;
        bool isClosed;
        lock (_gate)
        {
            isClosed = _closed || session.IsClosed;
            if (!isClosed)
            {
                _hotkeyHandles.TryGetValue(trimmedId, out replaced);
                _hotkeyHandles[trimmedId] = handle;
            }
        }

        if (isClosed)
        {
            handle.MarkUnregisteredLocally();
            return handle;
        }

        replaced?.MarkUnregisteredLocally();

        var payload = new Tier1RegisterHotkeyPayload(
            trimmedId,
            displayName.Trim(),
            defaultChord.ToString(),
            allowWhenTextInputFocused,
            suppressGameInput);

        await session.QueueOrSendSystemMessageAsync(Tier1MessageTypes.RegisterHotkey, payload, cancellationToken).ConfigureAwait(false);
        return handle;
    }

    public Task<ServerHotkeyHandle> RegisterHotkeyAsync(
        string id,
        string displayName,
        string defaultChord,
        bool allowWhenTextInputFocused = false,
        bool suppressGameInput = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultChord);
        if (!KeyChord.TryParse(defaultChord, out KeyChord parsed) || parsed.IsEmpty)
        {
            throw new ArgumentException($"Invalid keybind chord '{defaultChord}'.", nameof(defaultChord));
        }

        return RegisterHotkeyAsync(id, displayName, parsed, allowWhenTextInputFocused, suppressGameInput, cancellationToken);
    }

    public async Task<ServerHotkeyHandle> RegisterHotkeyAsync(
        string id,
        string displayName,
        KeyChord defaultChord,
        Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task> onTriggered,
        bool allowWhenTextInputFocused = false,
        bool suppressGameInput = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onTriggered);
        ServerHotkeyHandle handle = await RegisterHotkeyAsync(
            id,
            displayName,
            defaultChord,
            allowWhenTextInputFocused,
            suppressGameInput,
            cancellationToken).ConfigureAwait(false);

        if (handle.IsRegistered)
        {
            IDisposable sub = await OnHotkeyAsync(handle.Id, onTriggered, cancellationToken).ConfigureAwait(false);
            handle.TrackBoundSubscription(sub);
        }

        return handle;
    }

    public Task<ServerHotkeyHandle> RegisterHotkeyAsync(
        string id,
        string displayName,
        string defaultChord,
        Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task> onTriggered,
        bool allowWhenTextInputFocused = false,
        bool suppressGameInput = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultChord);
        if (!KeyChord.TryParse(defaultChord, out KeyChord parsed) || parsed.IsEmpty)
        {
            throw new ArgumentException($"Invalid keybind chord '{defaultChord}'.", nameof(defaultChord));
        }

        return RegisterHotkeyAsync(id, displayName, parsed, onTriggered, allowWhenTextInputFocused, suppressGameInput, cancellationToken);
    }

    public Task<IDisposable> OnHotkeyAsync(
        string id,
        Action<IClientSession, ClientHotkeyTriggeredEvent> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnHotkeyAsync(
            id,
            (s, evt, _) =>
            {
                handler(s, evt);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    public Task<IDisposable> OnHotkeyAsync(
        string id,
        Func<IClientSession, ClientHotkeyTriggeredEvent, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnHotkeyAsync(id, (s, evt, _) => handler(s, evt), cancellationToken);
    }

    public Task<IDisposable> OnHotkeyAsync(
        string id,
        Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(handler);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<IDisposable>(cancellationToken);
        }

        string trimmedId = id.Trim();
        lock (_gate)
        {
            if (_closed || session.IsClosed)
            {
                return Task.FromResult<IDisposable>(ClientBridgeHost.NoOpDisposable.Instance);
            }

            if (!_hotkeyHandlers.TryGetValue(trimmedId, out List<Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task>>? list))
            {
                list = [];
                _hotkeyHandlers[trimmedId] = list;
            }

            list.Add(handler);
        }

        IDisposable sub = new ClientBridgeHost.Subscription(() =>
        {
            lock (_gate)
            {
                if (_hotkeyHandlers.TryGetValue(trimmedId, out List<Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task>>? list))
                {
                    list.Remove(handler);
                    if (list.Count == 0)
                    {
                        _hotkeyHandlers.Remove(trimmedId);
                    }
                }
            }
        });

        return Task.FromResult(sub);
    }

    public async Task UnregisterHotkeyAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        string trimmedId = id.Trim();

        ServerHotkeyHandle? handle;
        lock (_gate)
        {
            _hotkeyHandles.Remove(trimmedId, out handle);
            _hotkeyHandlers.Remove(trimmedId);
        }

        handle?.MarkUnregisteredLocally();
        await session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.UnregisterHotkey,
            new Tier1UnregisterHotkeyPayload(trimmedId),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClientScreenStateSnapshot> QueryScreenStateAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (session.IsClosed)
        {
            throw new InvalidOperationException($"Cannot query screen state on closed client session '{session.ConnectionId}'.");
        }

        TimeSpan effectiveTimeout = timeout ?? DefaultQueryTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Query timeout must be positive.");
        }

        string requestId = NextRequestId("scr");
        var tcs = new TaskCompletionSource<ClientScreenStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingScreenQueries[requestId] = tcs;

        try
        {
            await session.SendDirectAsync(
                MessageCodec.SystemChannel,
                new Tier1QueryScreenStatePayload(requestId),
                Tier1MessageTypes.QueryScreenState,
                cancellationToken).ConfigureAwait(false);

            return await WaitForQueryAsync(tcs.Task, effectiveTimeout, "QueryScreenStateAsync", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingScreenQueries.TryRemove(requestId, out _);
        }
    }

    public async Task<ClientHoverStateSnapshot> QueryHoverStateAsync(
        bool refresh = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (session.IsClosed)
        {
            throw new InvalidOperationException($"Cannot query hover state on closed client session '{session.ConnectionId}'.");
        }

        TimeSpan effectiveTimeout = timeout ?? DefaultQueryTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Query timeout must be positive.");
        }

        string requestId = NextRequestId("hov");
        var tcs = new TaskCompletionSource<ClientHoverStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingHoverQueries[requestId] = tcs;

        try
        {
            await session.SendDirectAsync(
                MessageCodec.SystemChannel,
                new Tier1QueryHoverStatePayload(requestId, refresh),
                Tier1MessageTypes.QueryHoverState,
                cancellationToken).ConfigureAwait(false);

            return await WaitForQueryAsync(tcs.Task, effectiveTimeout, "QueryHoverStateAsync", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingHoverQueries.TryRemove(requestId, out _);
        }
    }

    public Task<IDisposable> OnScreenChangedAsync(
        Action<IClientSession, ClientScreenChangedEvent> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnScreenChangedAsync(
            (s, evt, _) =>
            {
                handler(s, evt);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    public Task<IDisposable> OnScreenChangedAsync(
        Func<IClientSession, ClientScreenChangedEvent, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnScreenChangedAsync((s, evt, _) => handler(s, evt), cancellationToken);
    }

    public async Task<IDisposable> OnScreenChangedAsync(
        Func<IClientSession, ClientScreenChangedEvent, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        bool shouldEnable;
        lock (_gate)
        {
            if (_closed || session.IsClosed)
            {
                return ClientBridgeHost.NoOpDisposable.Instance;
            }

            shouldEnable = _screenChangedHandlers.Count == 0;
            _screenChangedHandlers.Add(handler);
        }

        if (shouldEnable)
        {
            await session.QueueOrSendSystemMessageAsync(
                Tier1MessageTypes.SetScreenSubscription,
                new Tier1SetSubscriptionPayload(Enabled: true),
                cancellationToken).ConfigureAwait(false);
        }

        return new ClientBridgeHost.Subscription(() =>
        {
            bool shouldDisable = false;
            lock (_gate)
            {
                if (_screenChangedHandlers.Remove(handler))
                {
                    shouldDisable = !_closed && !session.IsClosed && _screenChangedHandlers.Count == 0;
                }
            }

            if (shouldDisable)
            {
                SendSubscriptionToggleNonBlocking(Tier1MessageTypes.SetScreenSubscription, enabled: false);
            }
        });
    }

    public Task<IDisposable> OnHoverChangedAsync(
        Action<IClientSession, ClientHoverStateSnapshot> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnHoverChangedAsync(
            (s, snapshot, _) =>
            {
                handler(s, snapshot);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    public Task<IDisposable> OnHoverChangedAsync(
        Func<IClientSession, ClientHoverStateSnapshot, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnHoverChangedAsync((s, snapshot, _) => handler(s, snapshot), cancellationToken);
    }

    public async Task<IDisposable> OnHoverChangedAsync(
        Func<IClientSession, ClientHoverStateSnapshot, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        bool shouldEnable;
        lock (_gate)
        {
            if (_closed || session.IsClosed)
            {
                return ClientBridgeHost.NoOpDisposable.Instance;
            }

            shouldEnable = _hoverChangedHandlers.Count == 0;
            _hoverChangedHandlers.Add(handler);
        }

        if (shouldEnable)
        {
            await session.QueueOrSendSystemMessageAsync(
                Tier1MessageTypes.SetHoverSubscription,
                new Tier1SetSubscriptionPayload(Enabled: true),
                cancellationToken).ConfigureAwait(false);
        }

        return new ClientBridgeHost.Subscription(() =>
        {
            bool shouldDisable = false;
            lock (_gate)
            {
                if (_hoverChangedHandlers.Remove(handler))
                {
                    shouldDisable = !_closed && !session.IsClosed && _hoverChangedHandlers.Count == 0;
                }
            }

            if (shouldDisable)
            {
                SendSubscriptionToggleNonBlocking(Tier1MessageTypes.SetHoverSubscription, enabled: false);
            }
        });
    }

    internal async Task DispatchSlotButtonClickAsync(Tier1SlotButtonClickPayload payload, CancellationToken cancellationToken)
    {
        var key = (payload.Slot, (payload.Id ?? string.Empty).Trim().ToLowerInvariant());
        Func<IClientSession, CancellationToken, Task>[] handlers;
        lock (_gate)
        {
            handlers = _slotClickHandlers.TryGetValue(key, out List<Func<IClientSession, CancellationToken, Task>>? list)
                ? [.. list]
                : [];
        }

        foreach (Func<IClientSession, CancellationToken, Task> handler in handlers)
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
                host.ReportHandlerError($"Tier1.SlotButtonClick({payload.Slot}/{payload.Id})", ex);
            }
        }
    }

    internal async Task DispatchHotkeyTriggeredAsync(ClientHotkeyTriggeredEvent evt, CancellationToken cancellationToken)
    {
        string key = (evt.Id ?? string.Empty).Trim();
        Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task>[] handlers;
        lock (_gate)
        {
            handlers = _hotkeyHandlers.TryGetValue(key, out List<Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task>>? list)
                ? [.. list]
                : [];
        }

        foreach (Func<IClientSession, ClientHotkeyTriggeredEvent, CancellationToken, Task> handler in handlers)
        {
            try
            {
                await handler(session, evt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                host.ReportHandlerError($"Tier1.Hotkey({evt.Id})", ex);
            }
        }
    }

    internal void HandleScreenStateResponse(Tier1ScreenStateResponsePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.RequestId) &&
            _pendingScreenQueries.TryRemove(payload.RequestId, out TaskCompletionSource<ClientScreenStateSnapshot>? tcs))
        {
            tcs.TrySetResult(payload.State);
        }
    }

    internal void HandleHoverStateResponse(Tier1HoverStateResponsePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.RequestId) &&
            _pendingHoverQueries.TryRemove(payload.RequestId, out TaskCompletionSource<ClientHoverStateSnapshot>? tcs))
        {
            tcs.TrySetResult(payload.State);
        }
    }

    internal async Task DispatchScreenChangedAsync(ClientScreenChangedEvent evt, CancellationToken cancellationToken)
    {
        Func<IClientSession, ClientScreenChangedEvent, CancellationToken, Task>[] handlers;
        lock (_gate)
        {
            handlers = [.. _screenChangedHandlers];
        }

        foreach (Func<IClientSession, ClientScreenChangedEvent, CancellationToken, Task> handler in handlers)
        {
            try
            {
                await handler(session, evt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                host.ReportHandlerError($"Tier1.ScreenChanged({session.ConnectionId})", ex);
            }
        }
    }

    internal async Task DispatchHoverChangedAsync(ClientHoverStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        Func<IClientSession, ClientHoverStateSnapshot, CancellationToken, Task>[] handlers;
        lock (_gate)
        {
            handlers = [.. _hoverChangedHandlers];
        }

        foreach (Func<IClientSession, ClientHoverStateSnapshot, CancellationToken, Task> handler in handlers)
        {
            try
            {
                await handler(session, snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                host.ReportHandlerError($"Tier1.HoverChanged({session.ConnectionId})", ex);
            }
        }
    }

    internal void CloseSession()
    {
        ServerSlotButtonHandle[] buttons;
        ServerHotkeyHandle[] hotkeys;
        lock (_gate)
        {
            _closed = true;
            buttons = [.. _slotButtonHandles.Values];
            _slotButtonHandles.Clear();
            _slotClickHandlers.Clear();
            hotkeys = [.. _hotkeyHandles.Values];
            _hotkeyHandles.Clear();
            _hotkeyHandlers.Clear();
            _screenChangedHandlers.Clear();
            _hoverChangedHandlers.Clear();
        }

        foreach (ServerSlotButtonHandle button in buttons)
        {
            button.MarkRemovedLocally();
        }

        foreach (ServerHotkeyHandle hotkey in hotkeys)
        {
            hotkey.MarkUnregisteredLocally();
        }

        foreach (KeyValuePair<string, TaskCompletionSource<ClientScreenStateSnapshot>> pending in _pendingScreenQueries)
        {
            if (_pendingScreenQueries.TryRemove(pending.Key, out TaskCompletionSource<ClientScreenStateSnapshot>? tcs))
            {
                tcs.TrySetCanceled();
            }
        }

        foreach (KeyValuePair<string, TaskCompletionSource<ClientHoverStateSnapshot>> pending in _pendingHoverQueries)
        {
            if (_pendingHoverQueries.TryRemove(pending.Key, out TaskCompletionSource<ClientHoverStateSnapshot>? tcs))
            {
                tcs.TrySetCanceled();
            }
        }
    }

    private string NextRequestId(string prefix)
    {
        long seq = Interlocked.Increment(ref _nextRequestSeq);
        return $"{prefix}:{session.ConnectionId}:{seq}";
    }

    private static async Task<T> WaitForQueryAsync<T>(
        Task<T> responseTask,
        TimeSpan timeout,
        string operationName,
        CancellationToken cancellationToken)
    {
        if (responseTask.IsCompleted)
        {
            return await responseTask.ConfigureAwait(false);
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            return await responseTask.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"{operationName} timed out after {timeout.TotalMilliseconds:F0} ms.");
        }
    }

    private void SendSubscriptionToggleNonBlocking(string messageType, bool enabled)
    {
        Task sendTask = session.QueueOrSendSystemMessageAsync(
            messageType,
            new Tier1SetSubscriptionPayload(enabled),
            CancellationToken.None);

        if (sendTask.IsFaulted && sendTask.Exception is not null)
        {
            host.ReportHandlerError($"Tier1.{messageType}({session.ConnectionId})", sendTask.Exception.GetBaseException());
        }
        else if (!sendTask.IsCompleted)
        {
            _ = sendTask.ContinueWith(
                t => host.ReportHandlerError($"Tier1.{messageType}({session.ConnectionId})", t.Exception?.GetBaseException() ?? new InvalidOperationException("Subscription update failed.")),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
