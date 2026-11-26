namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Active top-level screen state derived from PoE 3.29's <c>StateStack</c>.
/// </summary>
public enum PreGameScreen
{
    /// <summary>Screen state could not be determined or client is still booting.</summary>
    Unknown = 0,

    /// <summary>Login screen (<c>LoginState</c>).</summary>
    Login = 1,

    /// <summary>Character selection screen (<c>SelectCharacterState</c>).</summary>
    SelectCharacter = 2,

    /// <summary>Character creation screen (<c>CreateCharacterState</c> with league overlay closed).</summary>
    CreateCharacter = 3,

    /// <summary>League selection modal overlay (<c>CreateCharacterState</c> with visible <c>LeagueOverlay</c>).</summary>
    LeagueOverlay = 4,

    /// <summary>Active gameplay instance (<c>InGameState</c>).</summary>
    InGame = 5,
}

/// <summary>
/// Bit-flag enumeration of stock in-game UI panels tracked by <see cref="IScreensService"/>.
/// </summary>
[Flags]
public enum GamePanel
{
    /// <summary>No tracked panel open.</summary>
    None = 0,

    /// <summary>Primary in-game HUD (<c>InGameUI</c>).</summary>
    Hud = 1 << 0,

    /// <summary>Character inventory panel (<c>InventoryPanel</c>, Inventory tab active).</summary>
    Inventory = 1 << 1,

    /// <summary>Personal, guild, or trade stash panel (<c>StashPanel</c> / <c>GuildStashPanel</c>).</summary>
    Stash = 1 << 2,

    /// <summary>Map stash tab page (<c>MapStashPage</c>, active when a Map tab is selected).</summary>
    MapStash = 1 << 3,

    /// <summary>Blight stash tab page (<c>BlightStashPage</c>, active when a Blight tab is selected).</summary>
    BlightStash = 1 << 4,

    /// <summary>Passive skill tree panel (<c>PassiveShellPC</c>).</summary>
    PassiveTree = 1 << 5,

    /// <summary>NPC vendor buy/sell window (<c>VendorBuyWindow</c> / <c>VendorSellWindow</c>).</summary>
    Vendor = 1 << 6,

    /// <summary>Atlas passive skill tree panel (<c>AtlasPassiveShellPC</c>).</summary>
    AtlasPassiveTree = 1 << 7,

    /// <summary>NPC dialogue window (<c>NPCDialogueWindow</c>).</summary>
    NpcDialogue = 1 << 8,

    /// <summary>Player-to-player trade window (<c>PlayerTradeWindow</c>).</summary>
    PlayerTrade = 1 << 9,
}

/// <summary>
/// Stock Path of Exile 3.29 stash tab type codes (<c>StashTabInfo + 0x34</c>).
/// </summary>
public enum StashTabType
{
    /// <summary>Standard 12×12 stash tab.</summary>
    Normal = 0,

    /// <summary>Premium 12×12 stash tab.</summary>
    Premium = 1,

    /// <summary>Currency stash tab.</summary>
    Currency = 3,

    /// <summary>Unique collection stash tab.</summary>
    Unique = 4,

    /// <summary>Map stash tab.</summary>
    Map = 5,

    /// <summary>Divination card stash tab.</summary>
    Divination = 6,

    /// <summary>Quad 24×24 stash tab.</summary>
    Quad = 7,

    /// <summary>Essence stash tab.</summary>
    Essence = 8,

    /// <summary>Fragment stash tab.</summary>
    Fragment = 9,

    /// <summary>Alternate Essence stash tab code.</summary>
    EssenceAlt = 11,

    /// <summary>Delve stash tab.</summary>
    Delve = 12,

    /// <summary>Blight stash tab.</summary>
    Blight = 13,

    /// <summary>Ultimatum stash tab.</summary>
    Ultimatum = 14,

    /// <summary>Delirium stash tab.</summary>
    Delirium = 15,

    /// <summary>Stash folder container.</summary>
    Folder = 16,

    /// <summary>Flask stash tab.</summary>
    Flask = 17,

    /// <summary>Gem stash tab.</summary>
    Gem = 18,

    /// <summary>Breach stash tab.</summary>
    Breach = 22,

    /// <summary>Unknown or unmapped stash tab type.</summary>
    Unknown = 255,
}

/// <summary>
/// Metadata describing the currently selected stash tab (including nested folder resolution and active map tier).
/// </summary>
/// <param name="Index">Server-side stash tab index (<c>StashTabView + 0x08</c>).</param>
/// <param name="Id">Unique server stash tab identifier (<c>StashTabInfo + 0x28</c>).</param>
/// <param name="Type">Stash tab layout type (<c>StashTabInfo + 0x34</c>).</param>
/// <param name="Name">Player-visible stash tab name.</param>
/// <param name="MapTier">Active map tier (1–17) when <paramref name="Type"/> is <see cref="StashTabType.Map"/>, or <c>null</c>.</param>
/// <param name="IsGuild"><c>true</c> if the active stash is a guild stash tab.</param>
/// <param name="IsRemoveOnly"><c>true</c> if the tab has the remove-only flag (<c>0x08</c>).</param>
/// <param name="Color">Packed RGB tab color value.</param>
/// <param name="ParentFolderIndex">Parent folder index if nested inside a stash folder, or <c>null</c>.</param>
public sealed record StashTabInfo(
    int Index,
    uint Id,
    StashTabType Type,
    string Name,
    int? MapTier = null,
    bool IsGuild = false,
    bool IsRemoveOnly = false,
    uint Color = 0,
    int? ParentFolderIndex = null);

/// <summary>
/// Managed conflict-free auto-flow attachment slots on stock in-game and pre-game screens.
/// </summary>
public enum ScreenSlot
{
    /// <summary>Horizontal toolbar strip above the 12×5 main inventory grid (auto-hides in Cosmetics tab).</summary>
    InventoryToolbar = 0,

    /// <summary>Top header bar of the stash panel.</summary>
    StashHeader = 1,

    /// <summary>Bottom footer bar of the stash panel (left of search box).</summary>
    StashFooter = 2,

    /// <summary>Toolbar strip attached to the active <c>MapStashPage</c>.</summary>
    MapTabBar = 3,

    /// <summary>Toolbar strip attached to the active <c>BlightStashPage</c>.</summary>
    BlightTabBar = 4,

    /// <summary>Header toolbar attached to the passive skill tree (<c>PassiveShellPC</c>).</summary>
    PassiveTreeHeader = 5,

    /// <summary>Top-right HUD overlay anchor (shifts left when right side panel or minimap is visible).</summary>
    HudTopRight = 6,

    /// <summary>Mode checkbox stack inside each <c>0xdc35</c> <c>LeagueCard</c> on <c>LeagueOverlay</c>, re-injected on every <c>LeagueOverlay::Fill</c>.</summary>
    LeagueCardModes = 7,

    /// <summary>Bottom bar on <c>CreateCharacterState</c> for character creation toggles/badges when <c>LeagueOverlay</c> is closed or skipped.</summary>
    CreateCharacterBar = 8,
}

/// <summary>
/// Event payload raised when a stock in-game panel opens or closes.
/// </summary>
/// <param name="Panel">The affected panel.</param>
/// <param name="IsOpen"><c>true</c> when the panel became visible; <c>false</c> when closed or torn down.</param>
/// <param name="PanelControl">Live wrapped <see cref="IUiControl"/> for the panel when open, or <c>null</c>.</param>
public sealed record ScreenChangedEventArgs(GamePanel Panel, bool IsOpen, IUiControl? PanelControl);

/// <summary>
/// Event payload raised when the client transitions between pre-game and in-game screens.
/// </summary>
/// <param name="Previous">The previous screen state.</param>
/// <param name="Current">The newly active screen state.</param>
public sealed record PreGameScreenChangedEventArgs(PreGameScreen Previous, PreGameScreen Current);

/// <summary>
/// Event payload raised when the active stash tab or map sub-tab tier changes.
/// </summary>
/// <param name="Previous">Previously active stash tab metadata, or <c>null</c>.</param>
/// <param name="Current">Newly active stash tab metadata, or <c>null</c> when stash closes.</param>
public sealed record ActiveStashTabChangedEventArgs(StashTabInfo? Previous, StashTabInfo? Current);

/// <summary>
/// Snapshot describing a single main <c>0xdc35</c> <c>LeagueCard</c> built during <c>LeagueOverlay::Fill</c>.
/// </summary>
/// <param name="CardIndex">Zero-based index of this card in <c>LeagueOverlay</c>.</param>
/// <param name="LeagueId">Base league name/identifier shown on this card.</param>
/// <param name="CardHandle">Native <c>LeagueCard*</c> pointer.</param>
/// <param name="ModeStackHandle">Native mode <c>StackLayout*</c> pointer holding HC/SSF/Ruthless and mod checkboxes.</param>
/// <param name="StockCheckboxCount">Number of stock mode checkboxes (0–3) present on this card.</param>
/// <param name="DescBottomY">Bottom Y coordinate (<c>desc.rel_y + desc.height</c>) of the card description label inside the <c>500×905</c> card.</param>
/// <param name="ModeStackY">Initial Y coordinate of the mode <c>StackLayout</c> inside the card.</param>
/// <param name="MaxStockCheckboxWidth">Maximum natural width of the stock checkboxes on this card.</param>
public sealed record LeagueCardInfo(
    int CardIndex,
    string LeagueId,
    nint CardHandle,
    nint ModeStackHandle,
    int StockCheckboxCount,
    float DescBottomY = 580f,
    float ModeStackY = 628f,
    float MaxStockCheckboxWidth = 220f);

/// <summary>
/// Event payload raised after <c>LeagueOverlay::Fill</c> rebuilds league cards and managed <see cref="ScreenSlot.LeagueCardModes"/> checkboxes have been re-injected.
/// </summary>
/// <param name="OverlayHandle">Native <c>LeagueOverlay*</c> pointer.</param>
/// <param name="Cards">Snapshot of all <c>0xdc35</c> cards built by this fill pass.</param>
public sealed record LeagueOverlayFilledEventArgs(nint OverlayHandle, IReadOnlyList<LeagueCardInfo> Cards);

/// <summary>
/// Layout options controlling ordering, sizing, and spacing of an attachment inside a managed <see cref="ScreenSlot"/>.
/// </summary>
/// <param name="Order">Sort priority (lower values appear earlier in flow order).</param>
/// <param name="Width">Preferred width in UI virtual units (<c>0</c> uses control's natural width or slot default).</param>
/// <param name="Height">Preferred height in UI virtual units (<c>0</c> uses control's natural height or slot default).</param>
/// <param name="Spacing">Extra trailing gap in virtual units between this item and the next sibling.</param>
/// <param name="Tooltip">Optional tooltip text.</param>
public sealed record SlotAttachmentOptions(
    int Order = 0,
    float Width = 0f,
    float Height = 0f,
    float Spacing = 6f,
    string? Tooltip = null);

/// <summary>
/// Handle representing a mod control attached to a managed <see cref="ScreenSlot"/> or raw native control.
/// Disposing detaches and destroys the attachment and reflows remaining siblings.
/// </summary>
public interface ISlotAttachment : IDisposable
{
    /// <summary>Target slot (or <see cref="ScreenSlot.HudTopRight"/> for raw control attachments).</summary>
    ScreenSlot Slot { get; }

    /// <summary>Unique identifier of this attachment within its slot.</summary>
    string Id { get; }

    /// <summary>Ordering priority inside the slot.</summary>
    int Order { get; }

    /// <summary>Whether the attachment is currently mounted to a live parent control.</summary>
    bool IsAttached { get; }

    /// <summary>The mounted managed/native control handle, or <c>null</c> while its target screen is not active.</summary>
    IUiControl? Control { get; }

    /// <summary>The computed layout rectangle assigned by the slot auto-flow engine.</summary>
    UiRect ComputedRect { get; }
}

/// <summary>
/// Options for registering a mod mode checkbox inside <see cref="ScreenSlot.LeagueCardModes"/>.
/// </summary>
/// <param name="Id">Unique option identifier (for example <c>skip_campaign</c>).</param>
/// <param name="Caption">Checkbox caption displayed alongside HC/SSF/Ruthless.</param>
/// <param name="DefaultChecked">Initial checked state when a league card is first shown.</param>
/// <param name="Order">Sort priority relative to other mod mode checkboxes.</param>
/// <param name="Tooltip">Optional hover tooltip text.</param>
/// <param name="NaturalWidth">Estimated natural caption+box width in virtual units (used for width equalization when simulated).</param>
/// <param name="LeagueFilter">Optional predicate selecting which league IDs display this checkbox (<c>null</c> = all leagues).</param>
public sealed record LeagueModeCheckboxOptions(
    string Id,
    string Caption,
    bool DefaultChecked = false,
    int Order = 0,
    string? Tooltip = null,
    float NaturalWidth = 220f,
    Func<string, bool>? LeagueFilter = null);

/// <summary>
/// Handle for a mod mode checkbox registered into <see cref="ScreenSlot.LeagueCardModes"/>.
/// Persists per-league checked state across <c>LeagueOverlay::Fill</c> card rebuilds.
/// </summary>
public interface ILeagueModeRegistration : IDisposable
{
    /// <summary>Unique registration identifier.</summary>
    string Id { get; }

    /// <summary>Checkbox caption text.</summary>
    string Caption { get; }

    /// <summary>Gets whether the mode checkbox is checked for the most recently toggled or default league.</summary>
    bool IsChecked { get; }

    /// <summary>Gets whether this mode checkbox is checked for <paramref name="leagueId"/>.</summary>
    bool GetChecked(string leagueId);

    /// <summary>Sets the checked state for <paramref name="leagueId"/> and updates any live injected checkbox control.</summary>
    void SetChecked(string leagueId, bool isChecked);

    /// <summary>Raised on the game thread when the player toggles this checkbox on a league card, passing <c>(leagueId, isChecked)</c>.</summary>
    event Action<string, bool>? CheckedChanged;
}

/// <summary>
/// Tracks in-game and pre-game screens, active stash tabs, and conflict-free managed auto-flow toolbar slots
/// with automatic re-attachment across area transitions and <c>LeagueOverlay::Fill</c> rebuilds.
/// </summary>
public interface IScreensService
{
    /// <summary>Current pre-game or in-game screen state.</summary>
    PreGameScreen CurrentPreGameScreen { get; }

    /// <summary>Returns <c>true</c> when <see cref="CurrentPreGameScreen"/> is <see cref="PreGameScreen.InGame"/>.</summary>
    bool IsInGame => CurrentPreGameScreen == PreGameScreen.InGame;

    /// <summary>Bitmask of all currently visible in-game panels.</summary>
    GamePanel ActivePanels { get; }

    /// <summary>Returns <c>true</c> if the specified panel is currently visible.</summary>
    bool IsPanelOpen(GamePanel panel);

    /// <summary>Returns the live wrapped <see cref="IUiControl"/> for a currently open panel, or <c>null</c>.</summary>
    IUiControl? GetPanelControl(GamePanel panel);

    /// <summary>Metadata for the currently active stash tab, or <c>null</c> when stash is closed.</summary>
    StashTabInfo? ActiveStashTab { get; }

    /// <summary>Snapshot of the active <c>0xdc35</c> league cards from the most recent <c>LeagueOverlay::Fill</c>.</summary>
    IReadOnlyList<LeagueCardInfo> ActiveLeagueCards { get; }

    /// <summary>
    /// Registers a <c>.ui</c> layout into a managed conflict-free auto-flow <see cref="ScreenSlot"/>.
    /// Automatically mounts when the target screen opens and re-attaches after zone transitions.
    /// </summary>
    /// <param name="slot">Target screen slot.</param>
    /// <param name="id">Unique attachment identifier.</param>
    /// <param name="virtualUiPath">Virtual path to the <c>.ui</c> layout file.</param>
    /// <param name="options">Optional ordering, sizing, and spacing overrides.</param>
    ISlotAttachment RegisterSlot(ScreenSlot slot, string id, string virtualUiPath, SlotAttachmentOptions? options = null);

    /// <summary>
    /// Registers a convenience push button into a managed conflict-free auto-flow <see cref="ScreenSlot"/>.
    /// </summary>
    /// <param name="slot">Target screen slot.</param>
    /// <param name="id">Unique button identifier.</param>
    /// <param name="text">Button caption text.</param>
    /// <param name="onClick">Game-thread callback invoked when clicked.</param>
    /// <param name="options">Optional ordering, sizing, and spacing overrides.</param>
    ISlotAttachment RegisterSlotButton(ScreenSlot slot, string id, string text, Action onClick, SlotAttachmentOptions? options = null);

    /// <summary>
    /// Registers a mode checkbox into <see cref="ScreenSlot.LeagueCardModes"/> that is automatically re-injected
    /// into every <c>0xdc35</c> card's mode <c>StackLayout</c> on <c>LeagueOverlay::Fill</c>, equalizing widths
    /// with HC/SSF/Ruthless and compacting height/spacing within the <c>500×905</c> card budget.
    /// </summary>
    /// <param name="options">Checkbox caption, default state, ordering, and optional league filter.</param>
    /// <param name="onChanged">Optional callback invoked with <c>(leagueId, isChecked)</c> when toggled.</param>
    ILeagueModeRegistration RegisterLeagueCardMode(LeagueModeCheckboxOptions options, Action<string, bool>? onChanged = null);

    /// <summary>
    /// Low-level escape hatch attaching a <c>.ui</c> layout directly under an arbitrary native <c>UI::Control*</c> handle.
    /// </summary>
    /// <param name="parentHandle">Native parent <c>UI::Control*</c> handle.</param>
    /// <param name="virtualUiPath">Virtual path to the <c>.ui</c> layout file.</param>
    /// <param name="id">Optional attachment identifier.</param>
    /// <param name="x">Relative X offset inside parent.</param>
    /// <param name="y">Relative Y offset inside parent.</param>
    ISlotAttachment AttachToControl(nint parentHandle, string virtualUiPath, string? id = null, float x = 0f, float y = 0f);

    /// <summary>Returns all registered attachments currently ordered in <paramref name="slot"/>.</summary>
    IReadOnlyList<ISlotAttachment> GetSlotAttachments(ScreenSlot slot);

    /// <summary>Raised on the game thread when an in-game panel opens or closes.</summary>
    event Action<ScreenChangedEventArgs>? ScreenChanged;

    /// <summary>Raised on the game thread when the client transitions between pre-game and in-game screens.</summary>
    event Action<PreGameScreenChangedEventArgs>? PreGameScreenChanged;

    /// <summary>Raised on the game thread when the active stash tab or map sub-tab changes.</summary>
    event Action<ActiveStashTabChangedEventArgs>? ActiveStashTabChanged;

    /// <summary>Raised on the game thread after <c>LeagueOverlay::Fill</c> rebuilds cards and re-injects mod mode checkboxes.</summary>
    event Action<LeagueOverlayFilledEventArgs>? LeagueOverlayFilled;
}
