using System.Collections.Concurrent;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Protocol;

namespace LocalExile.Client.ServerBridge;

/// <summary>
/// Declarative server-pushed stock item tooltip augmentation rule evaluated on the client when
/// <c>ItemPopup::BuildSections</c> builds or rebuilds a hovered item tooltip (including Alt-held rebuilds).
/// </summary>
/// <param name="Id">Unique rule identifier (re-registering the same id replaces the existing rule).</param>
/// <param name="Sections">Ordered tooltip sections appended when the rule matches.</param>
/// <param name="MetadataPrefix">Optional case-insensitive prefix filter on <see cref="HoveredItemInfo.MetadataPath"/>.</param>
/// <param name="BaseNameContains">Optional case-insensitive substring filter on <see cref="HoveredItemInfo.BaseName"/>.</param>
/// <param name="ItemClassId">Optional exact <c>ItemClasses</c> identifier match.</param>
/// <param name="MinRarity">Optional minimum item rarity tier.</param>
/// <param name="RequireAltHeld">Optional Alt-key filter (<c>true</c> = only when Alt held; <c>false</c> = only when Alt not held; <c>null</c> = always).</param>
/// <param name="Priority">Evaluation priority (lower values run first).</param>
public sealed record ServerItemTooltipRule(
    string Id,
    IReadOnlyList<TooltipSection> Sections,
    string? MetadataPrefix = null,
    string? BaseNameContains = null,
    uint? ItemClassId = null,
    ItemRarity? MinRarity = null,
    bool? RequireAltHeld = null,
    int Priority = 0);

/// <summary>
/// Declarative server-pushed Ctrl+Click stash affinity route rule evaluated on the client inside
/// <c>StashControl::GetAffinityTargets</c>.
/// </summary>
/// <param name="Id">Unique route identifier (re-registering the same id replaces the existing route).</param>
/// <param name="TargetTabIndex">Zero-based destination stash tab index sent in <c>C→S 0x00E1</c>.</param>
/// <param name="MetadataPrefix">Optional case-insensitive prefix filter on <see cref="HoveredItemInfo.MetadataPath"/>.</param>
/// <param name="BaseNameContains">Optional case-insensitive substring filter on <see cref="HoveredItemInfo.BaseName"/>.</param>
/// <param name="ItemClassId">Optional exact <c>ItemClasses</c> identifier match.</param>
/// <param name="StashKind">Optional target stash container filter (Personal or Guild).</param>
/// <param name="AffinityId">Optional stock affinity id byte carried alongside the route.</param>
/// <param name="ReplaceStockTargets">When <c>true</c>, replaces stock affinity targets rather than appending.</param>
/// <param name="Priority">Evaluation priority (lower values run first).</param>
public sealed record ServerStashAffinityRouteRule(
    string Id,
    ushort TargetTabIndex,
    string? MetadataPrefix = null,
    string? BaseNameContains = null,
    uint? ItemClassId = null,
    StashKind? StashKind = null,
    byte AffinityId = 0,
    bool ReplaceStockTargets = false,
    int Priority = 0);

/// <summary>
/// JSON-serializable item descriptor placed inside a client-side <see cref="IUiItemGrid"/> by a server mod.
/// </summary>
/// <param name="ItemId">Unique identifier for this item within the grid.</param>
/// <param name="Col">Zero-based top-left column coordinate.</param>
/// <param name="Row">Zero-based top-left row coordinate.</param>
/// <param name="Width">Horizontal footprint in grid cells (&gt;= 1).</param>
/// <param name="Height">Vertical footprint in grid cells (&gt;= 1).</param>
/// <param name="IconPath">Optional virtual texture path for the item's icon.</param>
/// <param name="StackSize">Displayed stack count (shown when &gt; 1).</param>
/// <param name="Rarity">Item rarity tier controlling border/tooltip styling.</param>
/// <param name="Title">Optional display title shown in hover tooltips.</param>
/// <param name="TooltipLines">Optional custom tooltip lines displayed on hover.</param>
public sealed record ServerGridItem(
    string ItemId,
    int Col,
    int Row,
    int Width = 1,
    int Height = 1,
    string? IconPath = null,
    int StackSize = 1,
    ItemRarity Rarity = ItemRarity.Normal,
    string? Title = null,
    IReadOnlyList<TooltipLine>? TooltipLines = null);

/// <summary>
/// Event payload emitted when a player clicks a cell inside a subscribed client-side <see cref="IUiItemGrid"/>.
/// </summary>
/// <param name="WindowId">Identifier of the window hosting the grid.</param>
/// <param name="GridPath">Control path or id of the <see cref="IUiItemGrid"/>.</param>
/// <param name="Col">Zero-based clicked cell column.</param>
/// <param name="Row">Zero-based clicked cell row.</param>
/// <param name="ItemId">Identifier of the item occupying <c>(Col, Row)</c>, or <c>null</c> if empty.</param>
/// <param name="Button">Mouse button used for the click.</param>
/// <param name="Modifiers">Keyboard modifiers held during the click.</param>
public sealed record ClientGridCellClickedEvent(
    string WindowId,
    string GridPath,
    int Col,
    int Row,
    string? ItemId = null,
    MouseButton Button = MouseButton.Left,
    KeyModifiers Modifiers = KeyModifiers.None);

/// <summary>
/// Ergonomic handle representing a server-registered declarative item tooltip rule on a connected client.
/// </summary>
public sealed class ServerItemTooltipRuleHandle : IAsyncDisposable
{
    private readonly IClientPhase4Session _phase4;
    private int _removed;

    /// <summary>Unique rule identifier.</summary>
    public string Id { get; }

    /// <summary>Whether this rule is still active.</summary>
    public bool IsRegistered => Volatile.Read(ref _removed) == 0;

    internal ServerItemTooltipRuleHandle(IClientPhase4Session phase4, string id)
    {
        _phase4 = phase4;
        Id = id;
    }

    /// <summary>Removes this item tooltip rule from the client host.</summary>
    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _removed, 1) != 0)
        {
            return;
        }

        await _phase4.RemoveItemTooltipRuleAsync(Id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await RemoveAsync(CancellationToken.None).ConfigureAwait(false);

    internal void MarkRemovedLocally() => Interlocked.Exchange(ref _removed, 1);
}

/// <summary>
/// Ergonomic handle representing a server-registered Ctrl+Click stash affinity route rule on a connected client.
/// </summary>
public sealed class ServerStashAffinityRouteHandle : IAsyncDisposable
{
    private readonly IClientPhase4Session _phase4;
    private int _removed;

    /// <summary>Unique route identifier.</summary>
    public string Id { get; }

    /// <summary>Zero-based destination stash tab index.</summary>
    public ushort TargetTabIndex { get; }

    /// <summary>Whether this route is still active.</summary>
    public bool IsRegistered => Volatile.Read(ref _removed) == 0;

    internal ServerStashAffinityRouteHandle(IClientPhase4Session phase4, string id, ushort targetTabIndex)
    {
        _phase4 = phase4;
        Id = id;
        TargetTabIndex = targetTabIndex;
    }

    /// <summary>Removes this stash affinity route from the client host.</summary>
    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _removed, 1) != 0)
        {
            return;
        }

        await _phase4.RemoveStashAffinityRouteAsync(Id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await RemoveAsync(CancellationToken.None).ConfigureAwait(false);

    internal void MarkRemovedLocally() => Interlocked.Exchange(ref _removed, 1);
}

/// <summary>
/// Tier 1 <c>ServerBridge 1.2.0</c> Phase 4+5 session surface allowing single-<c>.cs</c> server mods
/// (with zero custom client DLL) to control custom &amp; stock item tooltips, Ctrl+Click stash affinity routes,
/// rich UI controls (<c>Picture</c>, <c>ComboBox</c>, <c>TabContainer</c>, <c>ItemGrid</c>), extended/3D audio
/// with channel handles, client-only world objects, and Phase 5 <c>u32</c> stacks / compact sockets toggles
/// — all automatically cleaned up on disconnect or disposal.
/// </summary>
public interface IClientPhase4Session
{
    // ---- Tooltips ----

    /// <summary>Displays a custom anchored tooltip overlay at <c>(screenX, screenY)</c> on the client.</summary>
    Task ShowTooltipAsync(
        TooltipContent content,
        float screenX,
        float screenY,
        CancellationToken cancellationToken = default);

    /// <summary>Hides the server-owned custom anchored tooltip overlay on the client.</summary>
    Task HideTooltipAsync(CancellationToken cancellationToken = default);

    /// <summary>Registers (or replaces) a declarative stock item tooltip augmentation rule on the client.</summary>
    Task<ServerItemTooltipRuleHandle> RegisterItemTooltipRuleAsync(
        ServerItemTooltipRule rule,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a previously registered declarative item tooltip rule from the client.</summary>
    Task RemoveItemTooltipRuleAsync(
        string ruleId,
        CancellationToken cancellationToken = default);

    // ---- Stash Affinity ----

    /// <summary>Registers (or replaces) a declarative Ctrl+Click stash affinity route rule on the client.</summary>
    Task<ServerStashAffinityRouteHandle> RegisterStashAffinityRouteAsync(
        ServerStashAffinityRouteRule rule,
        CancellationToken cancellationToken = default);

    /// <summary>Convenience overload registering a declarative Ctrl+Click stash affinity route rule on the client.</summary>
    Task<ServerStashAffinityRouteHandle> RegisterStashAffinityRouteAsync(
        string id,
        ushort targetTabIndex,
        string? metadataPrefix = null,
        uint? itemClassId = null,
        StashKind? stashKind = null,
        bool replaceStockTargets = false,
        byte affinityId = 0,
        int priority = 0,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a previously registered stash affinity route rule from the client.</summary>
    Task RemoveStashAffinityRouteAsync(
        string routeId,
        CancellationToken cancellationToken = default);

    // ---- Rich UI / ItemGrid ----

    /// <summary>Sets the virtual texture path on an <see cref="IPicture"/> control inside <paramref name="windowId"/>.</summary>
    Task SetPictureTextureAsync(
        string windowId,
        string controlPath,
        string texturePath,
        bool resizeToImage = false,
        CancellationToken cancellationToken = default);

    /// <summary>Sets the selected item index on an <see cref="IComboBox"/> control inside <paramref name="windowId"/>.</summary>
    Task SetComboBoxSelectedAsync(
        string windowId,
        string controlPath,
        int selectedIndex,
        CancellationToken cancellationToken = default);

    /// <summary>Selects a tab by zero-based index on an <see cref="ITabContainer"/> control inside <paramref name="windowId"/>.</summary>
    Task SelectTabAsync(
        string windowId,
        string controlPath,
        int tabIndex,
        CancellationToken cancellationToken = default);

    /// <summary>Dynamically creates a slab-safe composite <see cref="IUiItemGrid"/> inside a container control in <paramref name="windowId"/>.</summary>
    Task CreateItemGridAsync(
        string windowId,
        string parentControlPath,
        string gridId,
        UiItemGridOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Places (or replaces by <see cref="ServerGridItem.ItemId"/>) an item inside an <see cref="IUiItemGrid"/>.</summary>
    Task SetGridItemAsync(
        string windowId,
        string gridPath,
        ServerGridItem item,
        CancellationToken cancellationToken = default);

    /// <summary>Removes an item by <paramref name="itemId"/> from an <see cref="IUiItemGrid"/>.</summary>
    Task RemoveGridItemAsync(
        string windowId,
        string gridPath,
        string itemId,
        CancellationToken cancellationToken = default);

    /// <summary>Clears all items from an <see cref="IUiItemGrid"/>.</summary>
    Task ClearGridAsync(
        string windowId,
        string gridPath,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes synchronously to cell-click callbacks from an <see cref="IUiItemGrid"/>.</summary>
    Task<IDisposable> OnGridCellClickAsync(
        string windowId,
        string gridPath,
        Action<IClientSession, ClientGridCellClickedEvent> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously to cell-click callbacks from an <see cref="IUiItemGrid"/>.</summary>
    Task<IDisposable> OnGridCellClickAsync(
        string windowId,
        string gridPath,
        Func<IClientSession, ClientGridCellClickedEvent, Task> handler,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes asynchronously with cancellation support to cell-click callbacks from an <see cref="IUiItemGrid"/>.</summary>
    Task<IDisposable> OnGridCellClickAsync(
        string windowId,
        string gridPath,
        Func<IClientSession, ClientGridCellClickedEvent, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default);

    // ---- Audio, World & Phase 5 Toggles ----

    /// <summary>Plays an FMOD event or <c>.ogg</c> sound with explicit volume and loop flag, returning its <see cref="AudioChannelHandle"/>.</summary>
    Task<AudioChannelHandle> PlayAudioExAsync(
        string pathOrEvent,
        float volume = 1.0f,
        bool loop = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stops an active server-started audio channel.</summary>
    Task StopAudioAsync(
        AudioChannelHandle channel,
        CancellationToken cancellationToken = default);

    /// <summary>Plays a 3D world-positioned sound with live camera-distance attenuation, returning its <see cref="AudioChannelHandle"/>.</summary>
    Task<AudioChannelHandle> PlaySoundAtPositionAsync(
        string pathOrEvent,
        float x,
        float y,
        float z,
        float volume = 1.0f,
        float maxDistance = 1200f,
        bool loop = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>Updates the 3D world coordinate and attenuation radius of an active audio channel.</summary>
    Task SetAudioPositionAsync(
        AudioChannelHandle channel,
        float x,
        float y,
        float z,
        float maxDistance = 1200f,
        CancellationToken cancellationToken = default);

    /// <summary>Spawns a client-only world object with optional yaw rotation and uniform scale, returning its client object id.</summary>
    Task<uint> SpawnClientObjectAsync(
        string metadataPath,
        float x,
        float y,
        float z = 0f,
        float rotationRadians = 0f,
        float scale = 1.0f,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a previously spawned client-only world object.</summary>
    Task RemoveClientObjectAsync(
        uint objectId,
        CancellationToken cancellationToken = default);

    /// <summary>Enables or disables opt-in 32-bit stack deserialization (and optionally millions stack formatting) on the client.</summary>
    Task SetExtendedU32StacksAsync(
        bool enabled,
        bool? formatMillions = null,
        CancellationToken cancellationToken = default);

    /// <summary>Applies a sideband 32-bit stack size override onto a live item entity on the client.</summary>
    Task SetEntityStackOverrideAsync(
        uint entityId,
        uint stackSize,
        CancellationToken cancellationToken = default);

    /// <summary>Enables or disables 6-socket compact icon layout scaling on small items on the client.</summary>
    Task SetCompactSocketsAsync(
        bool enabled,
        CancellationToken cancellationToken = default);
}

// -----------------------------------------------------------------------------
// Internal Wire DTOs (JSON schema shared with LocalExile.Client.Host.Services.ServerBridgeTier1Handler)
// -----------------------------------------------------------------------------

internal sealed record Tier1ShowTooltipPayload(TooltipContent Content, float ScreenX, float ScreenY);

internal sealed record Tier1HideTooltipPayload();

internal sealed record Tier1RemoveRulePayload(string Id);

internal sealed record Tier1SetPictureTexturePayload(string WindowId, string ControlPath, string TexturePath, bool ResizeToImage = false);

internal sealed record Tier1SetComboBoxSelectedPayload(string WindowId, string ControlPath, int SelectedIndex);

internal sealed record Tier1SelectTabPayload(string WindowId, string ControlPath, int TabIndex);

internal sealed record Tier1CreateItemGridPayload(string WindowId, string ParentControlPath, string GridId, UiItemGridOptions Options);

internal sealed record Tier1SetGridItemPayload(string WindowId, string GridPath, ServerGridItem Item);

internal sealed record Tier1RemoveGridItemPayload(string WindowId, string GridPath, string ItemId);

internal sealed record Tier1ClearGridPayload(string WindowId, string GridPath);

internal sealed record Tier1SetGridClickSubscriptionPayload(string WindowId, string GridPath, bool Enabled);

internal sealed record Tier1PlayAudioExPayload(string RequestId, string PathOrEvent, float Volume = 1.0f, bool Loop = false);

internal sealed record Tier1PlaySoundAtPositionPayload(
    string RequestId,
    string PathOrEvent,
    float X,
    float Y,
    float Z,
    float Volume = 1.0f,
    float MaxDistance = 1200f,
    bool Loop = false);

internal sealed record Tier1PlayAudioExResponsePayload(string RequestId, int ChannelHandle);

internal sealed record Tier1StopAudioPayload(int ChannelHandle);

internal sealed record Tier1SetAudioPositionPayload(int ChannelHandle, float X, float Y, float Z, float MaxDistance = 1200f);

internal sealed record Tier1SpawnClientObjectPayload(
    string RequestId,
    string MetadataPath,
    float X,
    float Y,
    float Z = 0f,
    float RotationRadians = 0f,
    float Scale = 1.0f);

internal sealed record Tier1SpawnClientObjectResponsePayload(string RequestId, uint ObjectId);

internal sealed record Tier1RemoveClientObjectPayload(uint ObjectId);

internal sealed record Tier1SetExtendedU32StacksPayload(bool Enabled, bool? FormatMillions = null);

internal sealed record Tier1SetEntityStackOverridePayload(uint EntityId, uint StackSize);

internal sealed record Tier1SetCompactSocketsPayload(bool Enabled);

// -----------------------------------------------------------------------------
// Session Implementation
// -----------------------------------------------------------------------------

internal sealed class ClientPhase4Session(ClientSession session, ClientBridgeHost host) : IClientPhase4Session
{
    private static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(5);

    private sealed class GridKeyComparer : IEqualityComparer<(string WindowId, string GridPath)>
    {
        public static readonly GridKeyComparer Instance = new();

        public bool Equals((string WindowId, string GridPath) x, (string WindowId, string GridPath) y) =>
            string.Equals(x.WindowId, y.WindowId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.GridPath, y.GridPath, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string WindowId, string GridPath) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.WindowId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.GridPath));
    }

    private readonly string _sessionNonce = Guid.NewGuid().ToString("N")[..8];
    private readonly Dictionary<string, ServerItemTooltipRuleHandle> _tooltipRuleHandles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServerStashAffinityRouteHandle> _affinityRouteHandles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string WindowId, string GridPath), List<Func<IClientSession, ClientGridCellClickedEvent, CancellationToken, Task>>> _gridClickHandlers = new(GridKeyComparer.Instance);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AudioChannelHandle>> _pendingAudioQueries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<uint>> _pendingSpawnObjectQueries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private long _nextRequestSeq;
    private bool _closed;

    // ---- Tooltips ----

    public Task ShowTooltipAsync(
        TooltipContent content,
        float screenX,
        float screenY,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.ShowTooltip,
            new Tier1ShowTooltipPayload(content, screenX, screenY),
            cancellationToken);
    }

    public Task HideTooltipAsync(CancellationToken cancellationToken = default) =>
        session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.HideTooltip,
            new Tier1HideTooltipPayload(),
            cancellationToken);

    public async Task<ServerItemTooltipRuleHandle> RegisterItemTooltipRuleAsync(
        ServerItemTooltipRule rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Id);

        string trimmedId = rule.Id.Trim();
        ServerItemTooltipRule normalized = rule with { Id = trimmedId };
        var handle = new ServerItemTooltipRuleHandle(this, trimmedId);

        ServerItemTooltipRuleHandle? replaced = null;
        bool isClosed;
        lock (_gate)
        {
            isClosed = _closed || session.IsClosed;
            if (!isClosed)
            {
                _tooltipRuleHandles.TryGetValue(trimmedId, out replaced);
                _tooltipRuleHandles[trimmedId] = handle;
            }
        }

        if (isClosed)
        {
            handle.MarkRemovedLocally();
            return handle;
        }

        replaced?.MarkRemovedLocally();
        await session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.RegisterItemTooltipRule,
            normalized,
            cancellationToken).ConfigureAwait(false);
        return handle;
    }

    public Task RemoveItemTooltipRuleAsync(
        string ruleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        string trimmedId = ruleId.Trim();
        ServerItemTooltipRuleHandle? existing;
        lock (_gate)
        {
            _tooltipRuleHandles.Remove(trimmedId, out existing);
        }

        existing?.MarkRemovedLocally();
        return session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.RemoveItemTooltipRule,
            new Tier1RemoveRulePayload(trimmedId),
            cancellationToken);
    }

    // ---- Stash Affinity ----

    public async Task<ServerStashAffinityRouteHandle> RegisterStashAffinityRouteAsync(
        ServerStashAffinityRouteRule rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Id);

        string trimmedId = rule.Id.Trim();
        ServerStashAffinityRouteRule normalized = rule with { Id = trimmedId };
        var handle = new ServerStashAffinityRouteHandle(this, trimmedId, normalized.TargetTabIndex);

        ServerStashAffinityRouteHandle? replaced = null;
        bool isClosed;
        lock (_gate)
        {
            isClosed = _closed || session.IsClosed;
            if (!isClosed)
            {
                _affinityRouteHandles.TryGetValue(trimmedId, out replaced);
                _affinityRouteHandles[trimmedId] = handle;
            }
        }

        if (isClosed)
        {
            handle.MarkRemovedLocally();
            return handle;
        }

        replaced?.MarkRemovedLocally();
        await session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.RegisterStashAffinityRoute,
            normalized,
            cancellationToken).ConfigureAwait(false);
        return handle;
    }

    public Task<ServerStashAffinityRouteHandle> RegisterStashAffinityRouteAsync(
        string id,
        ushort targetTabIndex,
        string? metadataPrefix = null,
        uint? itemClassId = null,
        StashKind? stashKind = null,
        bool replaceStockTargets = false,
        byte affinityId = 0,
        int priority = 0,
        CancellationToken cancellationToken = default) =>
        RegisterStashAffinityRouteAsync(
            new ServerStashAffinityRouteRule(
                Id: id,
                TargetTabIndex: targetTabIndex,
                MetadataPrefix: metadataPrefix,
                ItemClassId: itemClassId,
                StashKind: stashKind,
                AffinityId: affinityId,
                ReplaceStockTargets: replaceStockTargets,
                Priority: priority),
            cancellationToken);

    public Task RemoveStashAffinityRouteAsync(
        string routeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        string trimmedId = routeId.Trim();
        ServerStashAffinityRouteHandle? existing;
        lock (_gate)
        {
            _affinityRouteHandles.Remove(trimmedId, out existing);
        }

        existing?.MarkRemovedLocally();
        return session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.RemoveStashAffinityRoute,
            new Tier1RemoveRulePayload(trimmedId),
            cancellationToken);
    }

    // ---- Rich UI / ItemGrid ----

    public Task SetPictureTextureAsync(
        string windowId,
        string controlPath,
        string texturePath,
        bool resizeToImage = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(texturePath);
        return session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.SetPictureTexture,
            new Tier1SetPictureTexturePayload(windowId.Trim(), controlPath.Trim(), texturePath.Trim(), resizeToImage),
            cancellationToken);
    }

    public Task SetComboBoxSelectedAsync(
        string windowId,
        string controlPath,
        int selectedIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        return session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.SetComboBoxSelected,
            new Tier1SetComboBoxSelectedPayload(windowId.Trim(), controlPath.Trim(), selectedIndex),
            cancellationToken);
    }

    public Task SelectTabAsync(
        string windowId,
        string controlPath,
        int tabIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlPath);
        return session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.SelectTab,
            new Tier1SelectTabPayload(windowId.Trim(), controlPath.Trim(), tabIndex),
            cancellationToken);
    }

    public Task CreateItemGridAsync(
        string windowId,
        string parentControlPath,
        string gridId,
        UiItemGridOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gridId);
        ArgumentNullException.ThrowIfNull(options);
        return session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.CreateItemGrid,
            new Tier1CreateItemGridPayload(windowId.Trim(), parentControlPath?.Trim() ?? string.Empty, gridId.Trim(), options),
            cancellationToken);
    }

    public Task SetGridItemAsync(
        string windowId,
        string gridPath,
        ServerGridItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gridPath);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.ItemId);
        return session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.SetGridItem,
            new Tier1SetGridItemPayload(windowId.Trim(), gridPath.Trim(), item),
            cancellationToken);
    }

    public Task RemoveGridItemAsync(
        string windowId,
        string gridPath,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gridPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        return session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.RemoveGridItem,
            new Tier1RemoveGridItemPayload(windowId.Trim(), gridPath.Trim(), itemId.Trim()),
            cancellationToken);
    }

    public Task ClearGridAsync(
        string windowId,
        string gridPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gridPath);
        return session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.ClearGrid,
            new Tier1ClearGridPayload(windowId.Trim(), gridPath.Trim()),
            cancellationToken);
    }

    public Task<IDisposable> OnGridCellClickAsync(
        string windowId,
        string gridPath,
        Action<IClientSession, ClientGridCellClickedEvent> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnGridCellClickAsync(
            windowId,
            gridPath,
            (s, evt, _) =>
            {
                handler(s, evt);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    public Task<IDisposable> OnGridCellClickAsync(
        string windowId,
        string gridPath,
        Func<IClientSession, ClientGridCellClickedEvent, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnGridCellClickAsync(windowId, gridPath, (s, evt, _) => handler(s, evt), cancellationToken);
    }

    public async Task<IDisposable> OnGridCellClickAsync(
        string windowId,
        string gridPath,
        Func<IClientSession, ClientGridCellClickedEvent, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gridPath);
        ArgumentNullException.ThrowIfNull(handler);

        var key = (windowId.Trim(), gridPath.Trim());
        bool shouldEnable;
        lock (_gate)
        {
            if (_closed || session.IsClosed)
            {
                return ClientBridgeHost.NoOpDisposable.Instance;
            }

            if (!_gridClickHandlers.TryGetValue(key, out List<Func<IClientSession, ClientGridCellClickedEvent, CancellationToken, Task>>? list))
            {
                list = [];
                _gridClickHandlers[key] = list;
                shouldEnable = true;
            }
            else
            {
                shouldEnable = list.Count == 0;
            }

            list.Add(handler);
        }

        if (shouldEnable)
        {
            try
            {
                await session.QueueOrSendSystemMessageAsync(
                    Tier1MessageTypes.SetGridClickSubscription,
                    new Tier1SetGridClickSubscriptionPayload(key.Item1, key.Item2, Enabled: true),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (_gate)
                {
                    if (_gridClickHandlers.TryGetValue(key, out var list))
                    {
                        list.Remove(handler);
                        if (list.Count == 0)
                        {
                            _gridClickHandlers.Remove(key);
                        }
                    }
                }

                throw;
            }
        }

        return new ClientBridgeHost.Subscription(() =>
        {
            bool shouldDisable = false;
            lock (_gate)
            {
                if (_gridClickHandlers.TryGetValue(key, out var list) && list.Remove(handler) && list.Count == 0)
                {
                    _gridClickHandlers.Remove(key);
                    shouldDisable = !_closed && !session.IsClosed;
                }
            }

            if (shouldDisable)
            {
                SendGridClickToggleNonBlocking(key.Item1, key.Item2, enabled: false);
            }
        });
    }

    // ---- Audio, World & Phase 5 Toggles ----

    public Task<AudioChannelHandle> PlayAudioExAsync(
        string pathOrEvent,
        float volume = 1.0f,
        bool loop = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrEvent);
        float clampedVol = Math.Clamp(float.IsNaN(volume) ? 0f : volume, 0f, 1f);
        return ExecuteQueryAsync(
            _pendingAudioQueries,
            "aud",
            Tier1MessageTypes.PlayAudioEx,
            requestId => new Tier1PlayAudioExPayload(requestId, pathOrEvent.Trim(), clampedVol, loop),
            nameof(PlayAudioExAsync),
            timeout,
            cancellationToken);
    }

    public Task StopAudioAsync(
        AudioChannelHandle channel,
        CancellationToken cancellationToken = default) =>
        session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.StopAudio,
            new Tier1StopAudioPayload(channel.Value),
            cancellationToken);

    public Task<AudioChannelHandle> PlaySoundAtPositionAsync(
        string pathOrEvent,
        float x,
        float y,
        float z,
        float volume = 1.0f,
        float maxDistance = 1200f,
        bool loop = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrEvent);
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Spatial audio coordinates must be finite numbers.");
        }

        if (!float.IsFinite(maxDistance) || maxDistance <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistance), "Spatial audio maxDistance must be a finite positive number.");
        }

        float clampedVol = Math.Clamp(float.IsNaN(volume) ? 0f : volume, 0f, 1f);
        return ExecuteQueryAsync(
            _pendingAudioQueries,
            "a3d",
            Tier1MessageTypes.PlaySoundAtPosition,
            requestId => new Tier1PlaySoundAtPositionPayload(requestId, pathOrEvent.Trim(), x, y, z, clampedVol, maxDistance, loop),
            nameof(PlaySoundAtPositionAsync),
            timeout,
            cancellationToken);
    }

    public Task SetAudioPositionAsync(
        AudioChannelHandle channel,
        float x,
        float y,
        float z,
        float maxDistance = 1200f,
        CancellationToken cancellationToken = default)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Spatial audio coordinates must be finite numbers.");
        }

        if (!float.IsFinite(maxDistance) || maxDistance <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistance), "Spatial audio maxDistance must be a finite positive number.");
        }

        return session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.SetAudioPosition,
            new Tier1SetAudioPositionPayload(channel.Value, x, y, z, maxDistance),
            cancellationToken);
    }

    public Task<uint> SpawnClientObjectAsync(
        string metadataPath,
        float x,
        float y,
        float z = 0f,
        float rotationRadians = 0f,
        float scale = 1.0f,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataPath);
        if (!float.IsFinite(rotationRadians))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationRadians), "Rotation must be a finite number.");
        }

        if (!float.IsFinite(scale) || scale <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be a finite positive number.");
        }

        return ExecuteQueryAsync(
            _pendingSpawnObjectQueries,
            "obj",
            Tier1MessageTypes.SpawnClientObject,
            requestId => new Tier1SpawnClientObjectPayload(requestId, metadataPath.Trim(), x, y, z, rotationRadians, scale),
            nameof(SpawnClientObjectAsync),
            timeout,
            cancellationToken);
    }

    public Task RemoveClientObjectAsync(
        uint objectId,
        CancellationToken cancellationToken = default) =>
        session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.RemoveClientObject,
            new Tier1RemoveClientObjectPayload(objectId),
            cancellationToken);

    public Task SetExtendedU32StacksAsync(
        bool enabled,
        bool? formatMillions = null,
        CancellationToken cancellationToken = default) =>
        session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.SetExtendedU32Stacks,
            new Tier1SetExtendedU32StacksPayload(enabled, formatMillions),
            cancellationToken);

    public Task SetEntityStackOverrideAsync(
        uint entityId,
        uint stackSize,
        CancellationToken cancellationToken = default) =>
        session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.SetEntityStackOverride,
            new Tier1SetEntityStackOverridePayload(entityId, stackSize),
            cancellationToken);

    public Task SetCompactSocketsAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.SetCompactSockets,
            new Tier1SetCompactSocketsPayload(enabled),
            cancellationToken);

    // ---- Incoming Response & Event Dispatch ----

    internal void HandlePlayAudioExResponse(Tier1PlayAudioExResponsePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.RequestId) &&
            _pendingAudioQueries.TryRemove(payload.RequestId, out TaskCompletionSource<AudioChannelHandle>? tcs))
        {
            tcs.TrySetResult(new AudioChannelHandle(payload.ChannelHandle));
        }
    }

    internal void HandleSpawnClientObjectResponse(Tier1SpawnClientObjectResponsePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.RequestId) &&
            _pendingSpawnObjectQueries.TryRemove(payload.RequestId, out TaskCompletionSource<uint>? tcs))
        {
            tcs.TrySetResult(payload.ObjectId);
        }
    }

    internal async Task DispatchGridCellClickAsync(ClientGridCellClickedEvent evt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(evt.WindowId) || string.IsNullOrWhiteSpace(evt.GridPath))
        {
            return;
        }

        var key = (evt.WindowId.Trim(), evt.GridPath.Trim());
        Func<IClientSession, ClientGridCellClickedEvent, CancellationToken, Task>[] handlers;
        lock (_gate)
        {
            handlers = _gridClickHandlers.TryGetValue(key, out var list) ? [.. list] : [];
        }

        foreach (var handler in handlers)
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
                host.ReportHandlerError($"Tier1.GridCellClick({session.ConnectionId}/{key.Item1}/{key.Item2})", ex);
            }
        }
    }

    internal void CloseSession()
    {
        ServerItemTooltipRuleHandle[] tooltipHandles;
        ServerStashAffinityRouteHandle[] affinityHandles;
        lock (_gate)
        {
            _closed = true;
            tooltipHandles = [.. _tooltipRuleHandles.Values];
            _tooltipRuleHandles.Clear();
            affinityHandles = [.. _affinityRouteHandles.Values];
            _affinityRouteHandles.Clear();
            _gridClickHandlers.Clear();
        }

        foreach (ServerItemTooltipRuleHandle h in tooltipHandles)
        {
            h.MarkRemovedLocally();
        }

        foreach (ServerStashAffinityRouteHandle h in affinityHandles)
        {
            h.MarkRemovedLocally();
        }

        CancelPending(_pendingAudioQueries);
        CancelPending(_pendingSpawnObjectQueries);
    }

    // ---- Helpers ----

    private async Task<TResult> ExecuteQueryAsync<TPayload, TResult>(
        ConcurrentDictionary<string, TaskCompletionSource<TResult>> pendingMap,
        string prefix,
        string messageType,
        Func<string, TPayload> payloadFactory,
        string operationName,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        if (_closed || session.IsClosed)
        {
            throw new OperationCanceledException($"Client session '{session.ConnectionId}' is closed.");
        }

        string requestId = $"{prefix}-{_sessionNonce}-{Interlocked.Increment(ref _nextRequestSeq)}";
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingMap[requestId] = tcs;

        try
        {
            await session.SendDirectAsync(MessageCodec.SystemChannel, payloadFactory(requestId), messageType, cancellationToken).ConfigureAwait(false);
            return await AwaitWithTimeoutAsync(tcs.Task, timeout ?? DefaultQueryTimeout, operationName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pendingMap.TryRemove(requestId, out _);
        }
    }

    private static async Task<T> AwaitWithTimeoutAsync<T>(
        Task<T> task,
        TimeSpan timeout,
        string operationName,
        CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            return await task.ConfigureAwait(false);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        Task delayTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);
        Task completed = await Task.WhenAny(task, delayTask).ConfigureAwait(false);
        if (ReferenceEquals(completed, task))
        {
            return await task.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException($"Timed out waiting for client response to {operationName} after {timeout.TotalMilliseconds:F0} ms.");
    }

    private static void CancelPending<T>(ConcurrentDictionary<string, TaskCompletionSource<T>> map)
    {
        foreach (string key in map.Keys.ToArray())
        {
            if (map.TryRemove(key, out TaskCompletionSource<T>? tcs))
            {
                tcs.TrySetCanceled();
            }
        }
    }

    private void SendGridClickToggleNonBlocking(string windowId, string gridPath, bool enabled)
    {
        Task sendTask = session.QueueOrSendSystemMessageAsync(
            Tier1MessageTypes.SetGridClickSubscription,
            new Tier1SetGridClickSubscriptionPayload(windowId, gridPath, enabled),
            CancellationToken.None);

        if (sendTask.IsFaulted && sendTask.Exception is not null)
        {
            host.ReportHandlerError($"Tier1.{Tier1MessageTypes.SetGridClickSubscription}({session.ConnectionId})", sendTask.Exception.GetBaseException());
        }
        else if (!sendTask.IsCompleted)
        {
            _ = sendTask.ContinueWith(
                t => host.ReportHandlerError($"Tier1.{Tier1MessageTypes.SetGridClickSubscription}({session.ConnectionId})", t.Exception?.GetBaseException() ?? new InvalidOperationException("Failed to toggle grid click subscription.")),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
