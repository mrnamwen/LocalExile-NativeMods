using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Raw;

namespace LocalExile.Client.Host.Native;

/// <summary>
/// Abstraction bridging the managed <see cref="ClientHostRuntime"/> to either the native Win64 C++ shim
/// (<see cref="NativeShimBridge"/>) or the in-memory deterministic simulator (<see cref="SimulatedNativeBridge"/>).
/// </summary>
public interface INativeBridge : IDisposable
{
    /// <summary>Detected 3.29 client build identifier.</summary>
    ClientBuildId DetectedBuild { get; }

    /// <summary>Human-readable client build version string (for example <c>3.29.3d</c>).</summary>
    string BuildVersion { get; }

    /// <summary>SHA-256 of the running executable, if available.</summary>
    string? ExecutableSha256 { get; }

    /// <summary>Loaded module base address in process memory.</summary>
    nint ImageBase { get; }

    /// <summary>Whether the caller is executing on the game's main frame thread.</summary>
    bool IsOnGameThread { get; }

    /// <summary>Enumerates all symbols exposed by the underlying shim or address table.</summary>
    IReadOnlyCollection<AddressEntry> EnumerateSymbols();

    /// <summary>Resolves a symbol name to its RVA and runtime address.</summary>
    bool ResolveSymbol(string name, out ulong rva, out nint address);

    /// <summary>Verifies expected hex bytes (with optional <c>??</c> wildcards) at a runtime address.</summary>
    bool VerifyExpectedBytes(nint address, string expectedBytesHex);

    /// <summary>Installs a byte-verified inline detour at a named symbol.</summary>
    ulong InstallInlineHook(string symbolName, nint detourTarget, out nint trampoline);

    /// <summary>Installs a vtable slot hook.</summary>
    ulong InstallVtableHook(nint vtableAddress, uint slotIndex, nint detourTarget, out nint originalSlot);

    /// <summary>Installs a pre/post inline hook chain callback on a named verified symbol.</summary>
    ulong InstallInlineHookChain(string symbolName, HookPhase phase, int priority, Action<HookContext> callback) =>
        throw new NotSupportedException("Hook chains are not supported by this bridge.");

    /// <summary>Installs a pre/post inline hook chain callback at an explicit address.</summary>
    ulong InstallInlineHookChainAt(nint address, string? expectedBytesHex, HookPhase phase, int priority, Action<HookContext> callback) =>
        throw new NotSupportedException("Hook chains are not supported by this bridge.");

    /// <summary>Installs a pre/post vtable hook chain callback on a vtable slot.</summary>
    ulong InstallVtableHookChain(nint vtableAddress, uint slotIndex, HookPhase phase, int priority, Action<HookContext> callback) =>
        throw new NotSupportedException("Hook chains are not supported by this bridge.");

    /// <summary>Removes an installed hook by handle.</summary>
    bool RemoveHook(ulong hookHandle);

    /// <summary>Reads raw bytes from process (or simulated) memory.</summary>
    void ReadMemory(nint address, Span<byte> destination);

    /// <summary>Writes raw bytes to process (or simulated) memory.</summary>
    void WriteMemory(nint address, ReadOnlySpan<byte> data);

    /// <summary>Invokes a native symbol inside the SEH/C++ exception wall.</summary>
    nint InvokeNative(string symbolName, ReadOnlySpan<nint> args);

    /// <summary>Invokes a native address with up to 8 mixed integer/float arguments inside the SEH/C++ exception wall.</summary>
    NativeCallResult InvokeNativeEx(nint targetAddress, ReadOnlySpan<NativeCallArgument> args, bool returnIsFloat = false) =>
        throw new NotSupportedException("Extended native calls are not supported by this bridge.");

    /// <summary>Registers a per-frame callback executed on the game thread.</summary>
    IDisposable RegisterFrameCallback(Action<float> onFrame);

    /// <summary>Enqueues a callback to execute on the next game-thread drain. Returns <c>false</c> when the queue is full or disposed.</summary>
    bool EnqueueGameThread(Action action);

    /// <summary>Drains queued game-thread callbacks.</summary>
    int DrainGameThreadQueue(float deltaSeconds = 0f, int maxItems = 256);

    /// <summary>Registers a managed file overlay resolver callback.</summary>
    IDisposable RegisterFileOverlayResolver(Func<string, byte[]?> resolver);

    /// <summary>
    /// Updates the native shim's lock-free O(1) pre-filter with the exact normalized virtual paths
    /// currently served by the managed file overlay resolver so stock game files bypass P/Invoke.
    /// </summary>
    void SetFileOverlayKnownPaths(IReadOnlyCollection<string> normalizedPaths) { }

    /// <summary>Whether the bridge exposes a live instance-server connection token for reconnect/zone-change detection.</summary>
    bool SupportsConnectionToken => false;

    /// <summary>
    /// Returns a non-zero connection token identifying the current live instance-server TCP socket
    /// (<c>0</c> when disconnected / login / character select). Changes on every zone transition.
    /// </summary>
    ulong GetActiveConnectionToken() => 0;

    /// <summary>Reads a file from the underlying game archive (stock fallback when overlays miss).</summary>
    byte[]? ReadStockGameFile(string virtualPath);

    /// <summary>Notifies the engine's asset invalidator (<c>0x141010560</c>) that a virtual path changed.</summary>
    void InvalidateCachedAsset(string virtualPath);

    /// <summary>Registers a host ruleset receiver for S→C <c>0x2c9</c> updates.</summary>
    IDisposable RegisterRulesetReceiver(byte rulesetId, Action<ReadOnlyMemory<byte>> onPayload);

    /// <summary>Sends a C→S <c>0x2ca</c> ruleset packet.</summary>
    void SendRulesetPacket(byte rulesetId, ReadOnlySpan<byte> payload);

    /// <summary>Registers a chat channel interceptor for S→C <c>0x17</c> lines.</summary>
    IDisposable RegisterChatInterceptor(Func<string, string, bool> onIncomingChat);

    /// <summary>Sends a C→S <c>0x15</c> chat command line.</summary>
    void SendChatLine(string text);

    /// <summary>Registers a raw opcode handler group (<c>0x2fc+</c>).</summary>
    IDisposable RegisterRawOpcodeReceiver(ushort opcode, Action<ReadOnlyMemory<byte>> onPayload);

    /// <summary>Registers an inclusive raw opcode handler group (<c>0x2fc+</c>).</summary>
    IDisposable RegisterRawOpcodeGroup(ushort minOpcode, ushort maxOpcode, Action<ushort, ReadOnlyMemory<byte>> onPayload) =>
        NoopDisposable.Instance;

    /// <summary>Registers an inclusive stream-reading opcode handler group (<c>0x2fc+</c>).</summary>
    IDisposable RegisterRawOpcodeStreamHandler(ushort minOpcode, ushort maxOpcode, Func<ushort, INativePacketStream, ReadOnlyMemory<byte>, bool> onStream) =>
        NoopDisposable.Instance;

    /// <summary>Reads <paramref name="destination"/>.Length bytes from a native <c>BufferStreamIn</c> handle.</summary>
    bool StreamRead(nint streamHandle, Span<byte> destination) => false;

    /// <summary>Sends a raw opcode payload.</summary>
    void SendRawPacket(ushort opcode, ReadOnlySpan<byte> payload);

    /// <summary>Invokes a GEAL function or event on a world object.</summary>
    GealInvocationResult InvokeGeal(uint objectId, string entryName, bool isEvent, ReadOnlySpan<GealValue> args);

    /// <summary>Plays an audio event or <c>.ogg</c> file, returning a channel handle.</summary>
    int PlayAudio(string pathOrEvent, bool isFmodEvent, float volume);

    /// <summary>Plays an audio event or <c>.ogg</c> file with optional infinite looping, returning a channel handle.</summary>
    int PlayAudio(string pathOrEvent, bool isFmodEvent, float volume, bool loop) =>
        PlayAudio(pathOrEvent, isFmodEvent, volume);

    /// <summary>Updates linear volume (0.0 to 1.0) on an active audio channel.</summary>
    void SetAudioVolume(int channelHandle, float volume) { }

    /// <summary>Returns <c>true</c> if the audio channel is currently playing.</summary>
    bool IsAudioPlaying(int channelHandle) => false;

    /// <summary>Stops an audio channel.</summary>
    void StopAudio(int channelHandle);

    /// <summary>Returns the live native 3.29 UI root control pointer, or <c>0</c> if unavailable.</summary>
    nint GetNativeUiRoot() => nint.Zero;

    /// <summary>Creates and loads a live native 3.29 <c>Frame</c> window from a <c>.ui</c> virtual path (returns 0 when simulated or root not yet captured).</summary>
    nint CreateNativeUiWindow(string virtualPath) => nint.Zero;

    /// <summary>Creates and loads a live native 3.29 <c>Frame</c> window directly under <paramref name="parentControl"/>.</summary>
    nint CreateNativeUiWindowInto(nint parentControl, string virtualPath) => nint.Zero;

    /// <summary>Finds a child control by dot-separated id path inside a live native 3.29 UI control.</summary>
    nint FindNativeUiChild(nint parentControl, string dotPath) => nint.Zero;

    /// <summary>Sets visibility on a live native 3.29 UI control.</summary>
    void SetNativeUiVisible(nint control, bool visible) { }

    /// <summary>Sets text on a live native 3.29 Label (<c>isButton=false</c>) or Button (<c>isButton=true</c>).</summary>
    void SetNativeUiText(nint control, string text, bool isButton) { }

    /// <summary>Reads the absolute screen rectangle of a live native 3.29 UI control.</summary>
    bool TryGetNativeUiRect(nint control, out UiRect rect) { rect = default; return false; }

    /// <summary>Sets the relative position of a live native 3.29 UI control.</summary>
    void SetNativeUiPosition(nint control, float x, float y) { }

    /// <summary>Sets the size of a live native 3.29 UI control.</summary>
    void SetNativeUiSize(nint control, float width, float height) { }

    /// <summary>Sets the enabled state of a live native 3.29 UI control.</summary>
    void SetNativeUiEnabled(nint control, bool enabled) { }

    /// <summary>Reads the enabled state of a live native 3.29 UI control.</summary>
    bool TryGetNativeUiEnabled(nint control, out bool enabled) { enabled = false; return false; }

    /// <summary>Detaches <paramref name="childControl"/> from <paramref name="parentControl"/>.</summary>
    bool RemoveNativeUiChild(nint parentControl, nint childControl) => false;

    /// <summary>Destroys a live native 3.29 UI control.</summary>
    void DestroyNativeUiControl(nint control) { }

    /// <summary>Reads the current UTF-8 text of a live native 3.29 <c>TextBox</c>.</summary>
    string? GetNativeUiTextBoxText(nint textboxControl) => null;

    /// <summary>Sets the text of a live native 3.29 <c>TextBox</c>.</summary>
    void SetNativeUiTextBoxText(nint textboxControl, string text) { }

    /// <summary>Subscribes a managed callback to a live native 3.29 <c>TextBox</c> changed event.</summary>
    IDisposable SubscribeNativeUiTextBoxChanged(nint textboxControl, Action onChanged) => NoopDisposable.Instance;

    /// <summary>Subscribes a managed callback to a live native 3.29 <c>TextBox</c> submitted event.</summary>
    IDisposable SubscribeNativeUiTextBoxSubmitted(nint textboxControl, Action onSubmitted) => NoopDisposable.Instance;

    /// <summary>Reads the checked state of a live native 3.29 <c>CheckBox</c>.</summary>
    bool? GetNativeUiCheckBoxChecked(nint checkboxControl) => null;

    /// <summary>Sets the checked state of a live native 3.29 <c>CheckBox</c>.</summary>
    void SetNativeUiCheckBoxChecked(nint checkboxControl, bool isChecked) { }

    /// <summary>Subscribes a managed callback to a live native 3.29 <c>CheckBox</c> changed event.</summary>
    IDisposable SubscribeNativeUiCheckBoxChanged(nint checkboxControl, Action onChanged) => NoopDisposable.Instance;

    /// <summary>Subscribes a managed callback to a live native 3.29 Button's clicked event list.</summary>
    IDisposable SubscribeNativeUiClick(nint buttonControl, Action onClicked) => NoopDisposable.Instance;

    /// <summary>Reads live visibility of a native 3.29 UI control (including ancestor visibility).</summary>
    bool TryGetNativeUiVisible(nint control, out bool visible) { visible = false; return false; }

    /// <summary>Reads the native <c>id</c> string (<c>Control+0x1b8</c>) of a live 3.29 UI control.</summary>
    string? GetNativeUiId(nint control) => null;

    /// <summary>Reads the primary vtable address (<c>*(uint64_t*)control</c>) of a live 3.29 UI control.</summary>
    ulong GetNativeUiVtable(nint control) => 0;

    /// <summary>Reads the numeric tag (<c>Control+0x1d8</c>) of a live 3.29 UI control.</summary>
    uint GetNativeUiTag(nint control) => 0;

    /// <summary>Reads the parent pointer (<c>Control+0x1e0</c>) of a live 3.29 UI control.</summary>
    nint GetNativeUiParent(nint control) => nint.Zero;

    /// <summary>Enumerates direct native child pointers (<c>Control+0xb8</c>) of a live 3.29 UI control.</summary>
    IReadOnlyList<nint> GetNativeUiChildren(nint control) => [];

    /// <summary>Sets control width via native <c>Control::SetWidth</c>.</summary>
    void SetNativeUiWidth(nint control, float width) { }

    /// <summary>Sets control height via native <c>Control::SetHeight</c>.</summary>
    void SetNativeUiHeight(nint control, float height) { }

    /// <summary>Sets horizontal/vertical layout anchors and offsets on a native 3.29 UI control.</summary>
    void SetNativeUiAnchor(nint control, byte xAnchor, float xOffset, byte yAnchor, float yOffset) { }

    /// <summary>Enables or disables automatic width sizing on a native control.</summary>
    void SetNativeUiAutoWidth(nint control, bool enabled) { }

    /// <summary>Triggers a native <c>Layout::Update</c> recalculation on a control.</summary>
    void UpdateNativeUiLayout(nint control) { }

    /// <summary>Creates a pool-allocated native <c>CheckBox</c> child under <paramref name="parentControl"/>.</summary>
    nint CreateNativeUiCheckBoxChild(nint parentControl, string caption, bool initialChecked) => nint.Zero;

    /// <summary>Returns the current <see cref="PreGameScreen"/> derived from <c>StateStack</c>.</summary>
    PreGameScreen GetPreGameScreen() => PreGameScreen.Unknown;

    /// <summary>Returns the active <c>InGameUI*</c> pointer (changes across every zone transition).</summary>
    nint GetInGameUiHandle() => nint.Zero;

    /// <summary>Returns the bitmask of currently visible stock in-game panels.</summary>
    GamePanel GetActivePanels() => GamePanel.None;

    /// <summary>Returns the live native control handle for a stock panel (or <see cref="nint.Zero"/>).</summary>
    nint GetPanelControlHandle(GamePanel panel) => nint.Zero;

    /// <summary>Returns the preferred native parent control handle for a managed <see cref="ScreenSlot"/> (or <see cref="nint.Zero"/> if inactive).</summary>
    nint GetSlotParentHandle(ScreenSlot slot) => nint.Zero;

    /// <summary>Returns metadata for the currently selected stash tab, or <c>null</c>.</summary>
    StashTabInfo? GetActiveStashTab() => null;

    /// <summary>Returns whether the corner minimap (<c>InGameUI+0x6e0</c>) is currently visible.</summary>
    bool IsMinimapVisible() => false;

    /// <summary>Returns whether a right side panel (<c>InGameUI+0x610</c>) is currently visible.</summary>
    bool IsRightSidePanelOpen() => false;

    /// <summary>Registers a callback fired after <c>LeagueOverlay::Fill</c> rebuilds league cards.</summary>
    IDisposable RegisterLeagueOverlayFilledCallback(Action<nint, IReadOnlyList<LeagueCardInfo>> onFilled) => NoopDisposable.Instance;

    /// <summary>Returns whether a PoE text input control (<c>TextBox</c>, chat, search) currently has keyboard focus.</summary>
    bool IsTextInputFocused() => false;

    /// <summary>Registers a low-level suppressible keyboard hook callback (return <c>true</c> to suppress).</summary>
    IDisposable RegisterKeyCallback(Func<KeyEventArgs, bool> onKey) => NoopDisposable.Instance;

    /// <summary>Registers a low-level suppressible mouse hook callback (return <c>true</c> to suppress).</summary>
    IDisposable RegisterMouseCallback(Func<MouseEventArgs, bool> onMouse) => NoopDisposable.Instance;

    /// <summary>Returns the currently hovered native <c>UI::Control*</c> pointer, or <see cref="nint.Zero"/>.</summary>
    nint GetHoveredControlHandle() => nint.Zero;

    /// <summary>Returns a snapshot of the currently hovered inventory/stash item, or <c>null</c>.</summary>
    HoveredItemInfo? GetHoveredItem() => null;

    /// <summary>Returns a snapshot of the item currently held on the cursor or in currency use mode, or <c>null</c>.</summary>
    CursorItemInfo? GetCursorItem() => null;

    /// <summary>Registers a pre-hook on <c>C→S 0x12</c> (<c>WriteCreateCharacter_0x12</c>); returns modified start-state or <c>null</c> for unchanged.</summary>
    IDisposable RegisterCharacterCreatingCallback(Func<string, string, string, string?> onCreating) => NoopDisposable.Instance;

    /// <summary>Registers a callback fired on <c>S→C 0x13</c> (<c>CreateCharacterState::OnCreateResult</c>).</summary>
    IDisposable RegisterCharacterCreatedCallback(Action<string, string, ushort> onCreated) => NoopDisposable.Instance;

    /// <summary>Reads text from the system clipboard, or <c>null</c> if unavailable.</summary>
    string? GetClipboardText() => null;

    /// <summary>Writes text to the system clipboard.</summary>
    void SetClipboardText(string text) { }

    /// <summary>Reads a client stat.</summary>
    int ReadStat(string statKey, int defaultValue);

    /// <summary>Writes a display-only local client stat.</summary>
    void WriteLocalStat(string statKey, int value);

    /// <summary>Returns tracked world objects in the current area.</summary>
    IReadOnlyCollection<WorldObjectInfo> GetWorldObjects();

    /// <summary>Spawns a client-only world object.</summary>
    uint SpawnClientObject(string metadataPath, float x, float y, float z);

    /// <summary>Removes a client-only world object.</summary>
    bool RemoveClientObject(uint objectId);

    /// <summary>Plays an effect on a world object.</summary>
    void PlayEffect(uint targetObjectId, string effectPath);

    /// <summary>Returns the live local player entity snapshot, or <c>null</c> when outside an area.</summary>
    WorldEntitySnapshot? GetLocalPlayerEntity() => null;

    /// <summary>Enumerates live world entities matching <paramref name="query"/> (or all entities when <c>null</c>).</summary>
    IReadOnlyList<WorldEntitySnapshot> GetWorldEntities(WorldEntityQuery? query = null) => [];

    /// <summary>Looks up a single live world entity by its id, or <c>null</c> if absent.</summary>
    WorldEntitySnapshot? GetWorldEntity(uint entityId) => null;

    /// <summary>Reads a numeric stat on an entity (<c>entityId == 0</c> targets the local player).</summary>
    bool TryReadEntityStat(uint entityId, int statId, out int value)
    {
        value = 0;
        return false;
    }

    /// <summary>Sets a display-only local numeric stat on an entity (<c>entityId == 0</c> targets the local player).</summary>
    void SetLocalEntityStat(uint entityId, int statId, int value) { }

    /// <summary>Enumerates all numeric stats on an entity (<c>entityId == 0</c> targets the local player), including high-ID (<c>&gt;= 0x10000</c>) stats.</summary>
    IReadOnlyDictionary<int, int> GetAllEntityStats(uint entityId) => new Dictionary<int, int>();

    /// <summary>Reads the current live area metadata snapshot, or <c>null</c> when outside an area.</summary>
    AreaInfo? GetAreaInfo() => null;

    /// <summary>Registers native area entered/left lifecycle callbacks.</summary>
    IDisposable RegisterAreaLifecycleCallback(Action<AreaInfo> onAreaEntered, Action<string> onAreaLeft) => NoopDisposable.Instance;

    /// <summary>Reads the current active item-filter state snapshot.</summary>
    ItemFilterState GetItemFilterState() => ItemFilterState.Empty;

    /// <summary>Triggers a game-thread reload of the active item filter.</summary>
    bool ReloadItemFilter() => false;

    /// <summary>Reads cached filter visibility/style/label-rect for a ground world-item entity.</summary>
    ItemFilterResult GetWorldItemFilterResult(uint entityId) => ItemFilterResult.Unevaluated;

    /// <summary>Evaluates an item or world-item entity against the active item filter.</summary>
    ItemFilterResult EvaluateItemFilter(uint entityId) => ItemFilterResult.Unevaluated;

    /// <summary>Registers a callback fired when the client finishes loading/reloading/clearing an item filter.</summary>
    IDisposable RegisterItemFilterReloadCallback(Action<bool, string, ItemFilterState?> onReloaded) => NoopDisposable.Instance;

    /// <summary>Reads the current live in-game camera state snapshot.</summary>
    CameraState GetCameraState() => CameraState.Invalid;

    /// <summary>Projects a 3D world point to 2D screen pixel coordinates via the active camera.</summary>
    bool TryCameraWorldToScreen(float worldX, float worldY, float worldZ, out float screenX, out float screenY)
    {
        screenX = 0f;
        screenY = 0f;
        return false;
    }

    /// <summary>Sets the camera zoom override (<c>&lt; 0f</c> clears back to stock <c>-1.0f</c>).</summary>
    void SetCameraZoomOverride(float zoomOverride) { }

    // ── Phase 4 (ABI 0.5.0): Rich dynamic UI controls ──────────────────────────────────────────

    /// <summary>Dynamically creates a slab-pooled native UI control of <paramref name="kind"/> under <paramref name="parentControl"/>.</summary>
    nint CreateNativeUiControl(nint parentControl, UiControlKind kind, string? id = null) => nint.Zero;

    /// <summary>Loads and binds a virtual texture path onto a native <c>Picture</c> control.</summary>
    void SetNativeUiPictureTexture(nint pictureControl, string texturePath, bool resizeToImage = false) { }

    /// <summary>Reads vertical scroll offset and maximum scroll offset of a native <c>ScrollableFrame</c>.</summary>
    bool TryGetNativeUiScrollState(nint scrollableControl, out float offset, out float maxOffset)
    {
        offset = 0f;
        maxOffset = 0f;
        return false;
    }

    /// <summary>Sets the vertical scroll offset of a native <c>ScrollableFrame</c>.</summary>
    void SetNativeUiScrollOffset(nint scrollableControl, float offset) { }

    /// <summary>Sets the vertical scroll range of a native <c>ScrollableFrame</c>.</summary>
    void SetNativeUiScrollRange(nint scrollableControl, float minOffset, float maxOffset) { }

    /// <summary>Recomputes native content bounds of a <c>ScrollableFrame</c>.</summary>
    void UpdateNativeUiScrollContentBounds(nint scrollableControl) { }

    /// <summary>Appends a tab to a native <c>TabContainer</c>, returning the tab's content container pointer.</summary>
    nint AddNativeUiTab(nint tabContainerControl, string title, string? iconPath = null, bool hidden = false) => nint.Zero;

    /// <summary>Selects a tab by zero-based index on a native <c>TabContainer</c>.</summary>
    void SelectNativeUiTab(nint tabContainerControl, int index) { }

    /// <summary>Clears all tabs from a native <c>TabContainer</c>.</summary>
    void ClearNativeUiTabs(nint tabContainerControl) { }

    /// <summary>Reads tab count and active tab index from a native <c>TabContainer</c>.</summary>
    bool TryGetNativeUiTabState(nint tabContainerControl, out int count, out int activeIndex)
    {
        count = 0;
        activeIndex = -1;
        return false;
    }

    /// <summary>Subscribes to active tab changes on a native <c>TabContainer</c>.</summary>
    IDisposable SubscribeNativeUiTabChanged(nint tabContainerControl, Action<int> onChanged) => NoopDisposable.Instance;

    /// <summary>Appends an item to a native <c>ComboBox</c>, returning its zero-based index (or <c>-1</c> on failure).</summary>
    int AddNativeUiComboBoxItem(nint comboBoxControl, string text, bool enabled = true, uint tag = 0) => -1;

    /// <summary>Clears all items from a native <c>ComboBox</c>.</summary>
    void ClearNativeUiComboBoxItems(nint comboBoxControl) { }

    /// <summary>Sets the selected index of a native <c>ComboBox</c>.</summary>
    bool SetNativeUiComboBoxSelectedIndex(nint comboBoxControl, int index) => false;

    /// <summary>Reads item count, selected index, and selected text from a native <c>ComboBox</c>.</summary>
    bool TryGetNativeUiComboBoxState(nint comboBoxControl, out int count, out int selectedIndex, out string? selectedText)
    {
        count = 0;
        selectedIndex = -1;
        selectedText = null;
        return false;
    }

    /// <summary>Subscribes to selection changes on a native <c>ComboBox</c>.</summary>
    IDisposable SubscribeNativeUiComboBoxSelectionChanged(nint comboBoxControl, Action<int> onChanged) => NoopDisposable.Instance;

    /// <summary>Inserts a child control row into a native <c>ControlListBox</c> at <paramref name="index"/> with optional <paramref name="userData"/>.</summary>
    bool InsertNativeUiListBoxItem(nint listBoxControl, int index, nint childControl, ulong userData = 0) => false;

    /// <summary>Clears all items from a native <c>ControlListBox</c>.</summary>
    void ClearNativeUiListBoxItems(nint listBoxControl) { }

    /// <summary>Sets the selected index on a native <c>ControlListBox</c>.</summary>
    bool SetNativeUiListBoxSelectedIndex(nint listBoxControl, int index) => false;

    /// <summary>Clears the active selection on a native <c>ControlListBox</c>.</summary>
    void ClearNativeUiListBoxSelection(nint listBoxControl) { }

    /// <summary>Reads item count and selected index from a native <c>ControlListBox</c>.</summary>
    bool TryGetNativeUiListBoxState(nint listBoxControl, out int count, out int selectedIndex)
    {
        count = 0;
        selectedIndex = -1;
        return false;
    }

    /// <summary>Subscribes to selection changes on a native <c>ControlListBox</c>.</summary>
    IDisposable SubscribeNativeUiListBoxSelectionChanged(nint listBoxControl, Action<int> onChanged) => NoopDisposable.Instance;

    // ── Phase 4 (ABI 0.5.0): Stock item tooltip augmentation ───────────────────────────────────

    /// <summary>Registers the host callback invoked on <c>ui.item_popup_build_sections</c> to augment stock item tooltips.</summary>
    IDisposable RegisterItemTooltipBuildCallback(Func<ItemTooltipContext, IReadOnlyList<TooltipSection>> callback) => NoopDisposable.Instance;

    // ── Phase 4 (ABI 0.5.0): Custom stash affinity routing & bitset hooks ──────────────────────

    /// <summary>Registers the host callback invoked on <c>ui.stash_get_affinity_targets</c> (<c>0xa83460</c>) for Ctrl+Click routing.</summary>
    IDisposable RegisterStashAffinityRouteCallback(Func<StashAffinityItemContext, IReadOnlyList<StashAffinityRoute>> callback) => NoopDisposable.Instance;

    /// <summary>Registers the host callback invoked on <c>items.compute_affinity_bitset</c> (<c>0x1589b20</c>) for stock affinity bits.</summary>
    IDisposable RegisterStashAffinityBitsetCallback(Func<StashAffinityItemContext, uint> callback) => NoopDisposable.Instance;

    // ── Phase 4 (ABI 0.5.0): Live native Extend registries ─────────────────────────────────────

    /// <summary>Registers a custom GEAL built-in directly in <c>geal.builtin_factory_table</c>.</summary>
    IDisposable RegisterNativeGealBuiltin(string name, bool hasReturnValue, Func<uint, GealArguments, GealValue> handler) => NoopDisposable.Instance;

    /// <summary>Whether the underlying bridge drives native <c>ClientRuleset</c> vtable lifecycle callbacks directly from the engine.</summary>
    bool DrivesNativeRulesetLifecycle => false;

    /// <summary>Registers a native ruleset proxy into an unoccupied slot of <c>rulesets.creator_table</c>.</summary>
    IDisposable RegisterNativeRuleset(
        byte rulesetId,
        Action<AreaEventArgs> onAreaEntered,
        Action<ReadOnlyMemory<byte>> onRulesetUpdate,
        Action<float> onFrame,
        Action onAreaLeft) => NoopDisposable.Instance;

    /// <summary>Registers a custom UI control kind directly in <c>g_uiControlFactoryMap</c>.</summary>
    IDisposable RegisterNativeCustomUiControlKind(
        string controlTypeName,
        Action<nint> onCreated,
        Action<nint, string, string> onPropertyChanged,
        Action<nint> onDestroyed) => NoopDisposable.Instance;
}

/// <summary>
/// Native bridge bound to <c>LocalExile.ClientHost.dll</c> via the <see cref="NmAbiTable"/> function-pointer table
/// defined in <c>src/native-shim/include/nativemods_abi.h</c>.
/// </summary>
public sealed unsafe class NativeShimBridge : INativeBridge
{
    private enum CallbackKind : byte
    {
        Frame = 0,
        FileOverlay = 1,
        Ruleset = 2,
        Chat = 3,
        UiClick = 4,
        TextBoxChanged = 5,
        TextBoxSubmitted = 6,
        CheckBoxChanged = 7,
        OpcodeRx = 8,
        HookChain = 9,
        UiTabChanged = 10,
        UiComboBoxChanged = 11,
        UiListBoxChanged = 12,
        GealBuiltin = 13,
        NativeRuleset = 14,
        CustomUiControl = 15,
    }

    private sealed class UiIntEventWrapper(NativeShimBridge bridge, Action<int> callback)
    {
        public readonly NativeShimBridge Bridge = bridge;
        public readonly Action<int> Callback = callback;
    }

    private sealed class GealBuiltinWrapper(NativeShimBridge bridge, bool hasReturn, Func<uint, GealArguments, GealValue> handler)
    {
        public readonly NativeShimBridge Bridge = bridge;
        public readonly bool HasReturn = hasReturn;
        public readonly Func<uint, GealArguments, GealValue> Handler = handler;
    }

    private sealed class NativeRulesetWrapper(
        NativeShimBridge bridge,
        Action<AreaEventArgs> onAreaEntered,
        Action<ReadOnlyMemory<byte>> onRulesetUpdate,
        Action<float> onFrame,
        Action onAreaLeft)
    {
        public readonly NativeShimBridge Bridge = bridge;
        public readonly Action<AreaEventArgs> OnAreaEntered = onAreaEntered;
        public readonly Action<ReadOnlyMemory<byte>> OnRulesetUpdate = onRulesetUpdate;
        public readonly Action<float> OnFrame = onFrame;
        public readonly Action OnAreaLeft = onAreaLeft;
    }

    private sealed class CustomUiControlWrapper(
        NativeShimBridge bridge,
        Action<nint> onCreated,
        Action<nint, string, string> onPropertyChanged,
        Action<nint> onDestroyed)
    {
        public readonly NativeShimBridge Bridge = bridge;
        public readonly Action<nint> OnCreated = onCreated;
        public readonly Action<nint, string, string> OnPropertyChanged = onPropertyChanged;
        public readonly Action<nint> OnDestroyed = onDestroyed;
    }

    private sealed class UiEventSlot(NativeShimBridge bridge, CallbackKind kind)
    {
        public readonly NativeShimBridge Bridge = bridge;
        public readonly CallbackKind Kind = kind;
        public Action? Handler;
        public bool NativeSubscribed;
        public ulong ActiveToken;
    }

    private sealed class FrameCallbackWrapper(NativeShimBridge bridge, Action<float> callback)
    {
        public readonly NativeShimBridge Bridge = bridge;
        public readonly Action<float> Callback = callback;
    }

    private sealed class PendingWorkItem(NativeShimBridge bridge, Action work)
    {
        public readonly NativeShimBridge Bridge = bridge;
        public readonly Action Work = work;
    }

    private sealed class RulesetCallbackWrapper(NativeShimBridge bridge, Action<ReadOnlyMemory<byte>> callback)
    {
        public readonly NativeShimBridge Bridge = bridge;
        public readonly Action<ReadOnlyMemory<byte>> Callback = callback;
    }

    private sealed class ChatCallbackWrapper(NativeShimBridge bridge, Func<string, string, bool> callback)
    {
        public readonly NativeShimBridge Bridge = bridge;
        public readonly Func<string, string, bool> Callback = callback;
    }

    private sealed class HookChainWrapper(Action<HookContext> callback)
    {
        public readonly Action<HookContext> Callback = callback;
    }

    private sealed class OpcodeHandlerWrapper
    {
        public readonly NativeShimBridge Bridge;
        public readonly Action<ushort, ReadOnlyMemory<byte>>? ByteHandler;
        public readonly Func<ushort, INativePacketStream, ReadOnlyMemory<byte>, bool>? StreamHandler;

        public OpcodeHandlerWrapper(NativeShimBridge bridge, Action<ushort, ReadOnlyMemory<byte>> byteHandler)
        {
            Bridge = bridge;
            ByteHandler = byteHandler;
        }

        public OpcodeHandlerWrapper(NativeShimBridge bridge, Func<ushort, INativePacketStream, ReadOnlyMemory<byte>, bool> streamHandler)
        {
            Bridge = bridge;
            StreamHandler = streamHandler;
        }
    }

    private sealed unsafe class NativePacketStream(NativeShimBridge bridge, nint streamHandle) : INativePacketStream
    {
        public bool TryRead(Span<byte> destination) => bridge.StreamRead(streamHandle, destination);

        public byte[] ReadBytes(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count == 0)
            {
                return [];
            }

            byte[] buf = new byte[count];
            if (!TryRead(buf))
            {
                throw new EndOfStreamException($"Failed to read {count} byte(s) from native packet stream.");
            }

            return buf;
        }

        public T Read<T>() where T : unmanaged
        {
            Span<byte> buf = stackalloc byte[Unsafe.SizeOf<T>()];
            if (!TryRead(buf))
            {
                throw new EndOfStreamException($"Failed to read {typeof(T).Name} ({buf.Length} bytes) from native packet stream.");
            }

            return MemoryMarshal.Read<T>(buf);
        }
    }

    private readonly struct GameThreadScope(bool prev) : IDisposable
    {
        public void Dispose() => s_inNativeGameFrame = prev;
    }

    private const int MaxMockPendingWork = 4096;
    private const ulong SyntheticStreamMagic = 0x4E4D53545245414Dul;

    [ThreadStatic]
    private static bool s_inNativeGameFrame;

    private readonly NmAbiTable* _table;
    private int _gameThreadId;
    private readonly ConcurrentQueue<Action> _pendingWork = new();
    private int _pendingWorkCount;
    private readonly ConcurrentDictionary<nint, GCHandle> _pendingWorkHandles = new();
    private readonly ConcurrentDictionary<(CallbackKind Kind, ulong Token), GCHandle> _pinnedCallbacks = new();
    private readonly ConcurrentDictionary<(CallbackKind Kind, nint Control), UiEventSlot> _uiEventSlots = new();
    private readonly ConcurrentDictionary<string, int> _localStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, WorldObjectInfo> _clientObjects = new();
    private readonly ConcurrentDictionary<uint, ulong> _nativeClientObjectHandles = new();
    private readonly ConcurrentDictionary<ushort, List<Action<ReadOnlyMemory<byte>>>> _rawReceivers = new();
    private readonly Lock _phase1Gate = new();
    private readonly List<Action<nint, IReadOnlyList<LeagueCardInfo>>> _leagueFillCallbacks = [];
    private readonly List<Func<KeyEventArgs, bool>> _keyCallbacks = [];
    private readonly List<Func<MouseEventArgs, bool>> _mouseCallbacks = [];
    private readonly List<Func<string, string, string, string?>> _charCreatingCallbacks = [];
    private readonly List<Action<string, string, ushort>> _charCreatedCallbacks = [];
    private readonly List<(Action<AreaInfo> OnEntered, Action<string> OnLeft)> _areaLifecycleCallbacks = [];
    private readonly List<Action<bool, string, ItemFilterState?>> _filterReloadCallbacks = [];
    private readonly List<Func<ItemTooltipContext, IReadOnlyList<TooltipSection>>> _tooltipAugmentCallbacks = [];
    private readonly List<Func<StashAffinityItemContext, IReadOnlyList<StashAffinityRoute>>> _affinityRouteCallbacks = [];
    private readonly List<Func<StashAffinityItemContext, uint>> _affinityBitsetCallbacks = [];
    private GCHandle _selfCallbackHandle;
    private bool _screensNativeHooked;
    private bool _inputNativeHooked;
    private bool _preGameNativeHooked;
    private bool _areaNativeHooked;
    private bool _filterNativeHooked;
    private bool _tooltipNativeHooked;
    private bool _affinityNativeHooked;
    private int _nextAudioChannel = 1;
    private uint _nextClientObjectId = 0x8000_0001u;
    private ulong _managedFileOverlayToken;
    private bool _disposed;

    internal int PendingWorkHandleCount => _pendingWorkHandles.Count;

    /// <inheritdoc/>
    public ClientBuildId DetectedBuild { get; }

    /// <inheritdoc/>
    public string BuildVersion { get; }

    /// <inheritdoc/>
    public string? ExecutableSha256 { get; }

    /// <inheritdoc/>
    public nint ImageBase { get; }

    /// <inheritdoc/>
    public bool IsOnGameThread =>
        s_inNativeGameFrame ||
        (Volatile.Read(ref _gameThreadId) is int tid && tid != 0 && Environment.CurrentManagedThreadId == tid);

    /// <summary>Creates a new bridge wrapping a validated <c>nm_abi_table_t*</c> pointer.</summary>
    public NativeShimBridge(nint abiTablePtr)
    {
        if (abiTablePtr == nint.Zero)
        {
            throw new ArgumentNullException(nameof(abiTablePtr));
        }

        _table = (NmAbiTable*)abiTablePtr;
        if (_table->StructSize < (uint)sizeof(NmAbiTable) || _table->AbiVersion != NativeShimConstants.AbiVersion)
        {
            throw new InvalidOperationException($"Unsupported native shim ABI version 0x{_table->AbiVersion:X8} (expected 0x{NativeShimConstants.AbiVersion:X8}).");
        }

        NmHostInfo info = default;
        info.StructSize = (uint)sizeof(NmHostInfo);
        if (_table->GetHostInfo is not null && _table->GetHostInfo(&info) == NmStatus.Ok)
        {
            DetectedBuild = info.ToClientBuildId();
            BuildVersion = string.IsNullOrWhiteSpace(info.GetBuildVersion()) ? "unknown" : info.GetBuildVersion();
            string sha = info.GetExeSha256();
            ExecutableSha256 = string.IsNullOrWhiteSpace(sha) ? null : sha;
            ImageBase = checked((nint)info.ImageBase);
        }
        else
        {
            DetectedBuild = ClientBuildId.Unknown;
            BuildVersion = "unknown";
            ImageBase = unchecked((nint)NativeShimConstants.NominalImageBase);
        }
    }

    private GameThreadScope EnterNativeGameThreadScope(bool captureGameThreadId)
    {
        if (captureGameThreadId && Volatile.Read(ref _gameThreadId) == 0)
        {
            Interlocked.CompareExchange(ref _gameThreadId, Environment.CurrentManagedThreadId, 0);
        }

        bool prev = s_inNativeGameFrame;
        s_inNativeGameFrame = true;
        return new GameThreadScope(prev);
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<AddressEntry> EnumerateSymbols()
    {
        var list = new List<AddressEntry>();
        if (_table->GetHostInfo is null || _table->GetSymbolAt is null)
        {
            return list;
        }

        NmHostInfo info = default;
        info.StructSize = (uint)sizeof(NmHostInfo);
        if (_table->GetHostInfo(&info) != NmStatus.Ok)
        {
            return list;
        }

        for (uint i = 0; i < info.SymbolCount; i++)
        {
            NmSymbolInfo sym = default;
            if (_table->GetSymbolAt(i, &sym) == NmStatus.Ok)
            {
                list.Add(sym.ToAddressEntry());
            }
        }

        return list;
    }

    /// <inheritdoc/>
    public bool ResolveSymbol(string name, out ulong rva, out nint address)
    {
        rva = 0;
        address = nint.Zero;
        if (_table->LookupSymbol is null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        byte[] utf8 = ToNullTerminatedUtf8(name);
        fixed (byte* pName = utf8)
        {
            NmSymbolInfo sym = default;
            NmStatus status = _table->LookupSymbol(pName, &sym);
            if (status != NmStatus.Ok || sym.Status == NmSymbolStatus.Unverified)
            {
                return false;
            }

            rva = sym.Rva;
            address = checked((nint)sym.RuntimeAddress);
            return true;
        }
    }

    /// <inheritdoc/>
    public bool VerifyExpectedBytes(nint address, string expectedBytesHex)
    {
        if (_table->VerifyBytes is null || string.IsNullOrWhiteSpace(expectedBytesHex))
        {
            return false;
        }

        byte[] utf8 = ToNullTerminatedUtf8(expectedBytesHex);
        fixed (byte* pHex = utf8)
        {
            return _table->VerifyBytes((ulong)address, pHex) == NmStatus.Ok;
        }
    }

    /// <inheritdoc/>
    public ulong InstallInlineHook(string symbolName, nint detourTarget, out nint trampoline)
    {
        trampoline = nint.Zero;
        if (_table->InstallInlineHook is null)
        {
            throw new NotSupportedException("Native shim does not expose install_inline_hook.");
        }

        byte[] utf8 = ToNullTerminatedUtf8(symbolName);
        fixed (byte* pName = utf8)
        {
            void* tramp = null;
            ulong handle = 0;
            NmStatus status = _table->InstallInlineHook(pName, (void*)detourTarget, &tramp, &handle);
            EnsureOk(status, $"InstallInlineHook({symbolName})");
            trampoline = (nint)tramp;
            return handle;
        }
    }

    /// <inheritdoc/>
    public ulong InstallVtableHook(nint vtableAddress, uint slotIndex, nint detourTarget, out nint originalSlot)
    {
        originalSlot = nint.Zero;
        if (_table->InstallVtableHook is null)
        {
            throw new NotSupportedException("Native shim does not expose install_vtable_hook.");
        }

        void* orig = null;
        ulong handle = 0;
        NmStatus status = _table->InstallVtableHook((void*)vtableAddress, slotIndex, (void*)detourTarget, &orig, &handle);
        EnsureOk(status, $"InstallVtableHook(slot={slotIndex})");
        originalSlot = (nint)orig;
        return handle;
    }

    /// <inheritdoc/>
    public ulong InstallInlineHookChain(string symbolName, HookPhase phase, int priority, Action<HookContext> callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolName);
        ArgumentNullException.ThrowIfNull(callback);
        if (_table->InstallInlineChainHookSym is null)
        {
            throw new NotSupportedException("Native shim does not expose install_inline_chain_hook_sym.");
        }

        var wrapper = new HookChainWrapper(callback);
        GCHandle gcHandle = GCHandle.Alloc(wrapper);
        byte[] utf8 = ToNullTerminatedUtf8(symbolName);
        ulong handle = 0;
        fixed (byte* pName = utf8)
        {
            NmStatus status = _table->InstallInlineChainHookSym(
                pName,
                (int)phase,
                priority,
                &OnNativeHookChain,
                (void*)GCHandle.ToIntPtr(gcHandle),
                &handle);
            if (status != NmStatus.Ok)
            {
                gcHandle.Free();
                EnsureOk(status, $"InstallInlineHookChain({symbolName})");
            }
        }

        _pinnedCallbacks[(CallbackKind.HookChain, handle)] = gcHandle;
        return handle;
    }

    /// <inheritdoc/>
    public ulong InstallInlineHookChainAt(nint address, string? expectedBytesHex, HookPhase phase, int priority, Action<HookContext> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (address == nint.Zero)
        {
            throw new ArgumentException("Cannot install hook chain at null address.", nameof(address));
        }

        if (_table->InstallInlineChainHook is null)
        {
            throw new NotSupportedException("Native shim does not expose install_inline_chain_hook.");
        }

        var wrapper = new HookChainWrapper(callback);
        GCHandle gcHandle = GCHandle.Alloc(wrapper);
        byte[]? hexUtf8 = string.IsNullOrWhiteSpace(expectedBytesHex) ? null : ToNullTerminatedUtf8(expectedBytesHex);
        ulong handle = 0;
        fixed (byte* pHex = hexUtf8)
        {
            NmStatus status = _table->InstallInlineChainHook(
                unchecked((ulong)address),
                pHex,
                (int)phase,
                priority,
                &OnNativeHookChain,
                (void*)GCHandle.ToIntPtr(gcHandle),
                &handle);
            if (status != NmStatus.Ok)
            {
                gcHandle.Free();
                EnsureOk(status, $"InstallInlineHookChainAt(0x{(ulong)address:X})");
            }
        }

        _pinnedCallbacks[(CallbackKind.HookChain, handle)] = gcHandle;
        return handle;
    }

    /// <inheritdoc/>
    public ulong InstallVtableHookChain(nint vtableAddress, uint slotIndex, HookPhase phase, int priority, Action<HookContext> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (vtableAddress == nint.Zero)
        {
            throw new ArgumentException("Cannot install vtable hook chain at null vtable.", nameof(vtableAddress));
        }

        if (_table->InstallVtableChainHook is null)
        {
            throw new NotSupportedException("Native shim does not expose install_vtable_chain_hook.");
        }

        var wrapper = new HookChainWrapper(callback);
        GCHandle gcHandle = GCHandle.Alloc(wrapper);
        ulong handle = 0;
        NmStatus status = _table->InstallVtableChainHook(
            (void*)vtableAddress,
            slotIndex,
            (int)phase,
            priority,
            &OnNativeHookChain,
            (void*)GCHandle.ToIntPtr(gcHandle),
            &handle);
        if (status != NmStatus.Ok)
        {
            gcHandle.Free();
            EnsureOk(status, $"InstallVtableHookChain(slot={slotIndex})");
        }

        _pinnedCallbacks[(CallbackKind.HookChain, handle)] = gcHandle;
        return handle;
    }

    /// <inheritdoc/>
    public bool RemoveHook(ulong hookHandle)
    {
        if (_table->RemoveHook is null || hookHandle == 0)
        {
            return false;
        }

        bool removed = _table->RemoveHook(hookHandle) == NmStatus.Ok;
        if (_pinnedCallbacks.TryRemove((CallbackKind.HookChain, hookHandle), out GCHandle h) && h.IsAllocated)
        {
            h.Free();
        }

        return removed;
    }

    /// <inheritdoc/>
    public void ReadMemory(nint address, Span<byte> destination)
    {
        if (address == nint.Zero)
        {
            throw new ArgumentException("Cannot read from null address.", nameof(address));
        }

        new ReadOnlySpan<byte>((void*)address, destination.Length).CopyTo(destination);
    }

    /// <inheritdoc/>
    public void WriteMemory(nint address, ReadOnlySpan<byte> data)
    {
        if (address == nint.Zero)
        {
            throw new ArgumentException("Cannot write to null address.", nameof(address));
        }

        data.CopyTo(new Span<byte>((void*)address, data.Length));
    }

    /// <inheritdoc/>
    public nint InvokeNative(string symbolName, ReadOnlySpan<nint> args)
    {
        if (!ResolveSymbol(symbolName, out _, out nint address))
        {
            throw new KeyNotFoundException($"Verified symbol '{symbolName}' was not found in the native shim.");
        }

        if (args.Length > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Native calls support at most 8 arguments.");
        }

        if (args.Length > 4 || _table->CallGuarded is null)
        {
            if (_table->CallNativeGuarded is null)
            {
                throw new NotSupportedException("Native shim does not expose call_native_guarded.");
            }

            Span<NativeCallArgument> callArgs = stackalloc NativeCallArgument[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                callArgs[i] = NativeCallArgument.FromInt(args[i]);
            }

            return InvokeNativeEx(address, callArgs, returnIsFloat: false).AsIntPtr;
        }

        GuardedInvocationContext ctx = new()
        {
            TargetAddress = address,
            ArgCount = args.Length,
            Arg0 = args.Length > 0 ? args[0] : 0,
            Arg1 = args.Length > 1 ? args[1] : 0,
            Arg2 = args.Length > 2 ? args[2] : 0,
            Arg3 = args.Length > 3 ? args[3] : 0,
        };

        long result = 0;
        NmExceptionInfo exInfo = default;
        NmStatus status = _table->CallGuarded(&InvokeGuardedTrampoline, &ctx, &result, &exInfo);
        if (status == NmStatus.ErrGameException)
        {
            throw new InvalidOperationException(
                $"Native call '{symbolName}' threw native exception 0x{exInfo.ExceptionCode:X8} at 0x{exInfo.ExceptionAddress:X}: {exInfo.GetMessage()}");
        }

        EnsureOk(status, $"InvokeNative({symbolName})");
        return (nint)result;
    }

    /// <inheritdoc/>
    public NativeCallResult InvokeNativeEx(nint targetAddress, ReadOnlySpan<NativeCallArgument> args, bool returnIsFloat = false)
    {
        if (targetAddress == nint.Zero)
        {
            throw new ArgumentException("Cannot invoke null target address.", nameof(targetAddress));
        }

        if (args.Length > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Win64 guarded native calls support at most 8 arguments.");
        }

        if (_table->CallNativeGuarded is null)
        {
            throw new NotSupportedException("Native shim does not expose call_native_guarded.");
        }

        NmNativeCallDesc desc = default;
        desc.TargetAddress = unchecked((ulong)targetAddress);
        desc.ArgCount = (uint)args.Length;
        desc.ReturnIsFloat = returnIsFloat ? (byte)1 : (byte)0;

        uint floatMask = 0;
        for (int i = 0; i < args.Length; i++)
        {
            desc.Args[i] = args[i].RawBits;
            if (args[i].IsFloat)
            {
                floatMask |= 1u << i;
            }
        }

        desc.FloatArgMask = floatMask;

        NmExceptionInfo exInfo = default;
        NmStatus status = _table->CallNativeGuarded(&desc, &exInfo);
        if (status == NmStatus.ErrGameException)
        {
            throw new InvalidOperationException(
                $"Native call 0x{(ulong)targetAddress:X} threw native exception 0x{exInfo.ExceptionCode:X8} at 0x{exInfo.ExceptionAddress:X}: {exInfo.GetMessage()}");
        }

        EnsureOk(status, $"InvokeNativeEx(0x{(ulong)targetAddress:X})");
        return new NativeCallResult(desc.OutInt, desc.OutFloatBits);
    }

    /// <inheritdoc/>
    public IDisposable RegisterFrameCallback(Action<float> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        if (_table->RegisterFrameCallback is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle handle = GCHandle.Alloc(new FrameCallbackWrapper(this, onFrame));
        ulong token = 0;
        NmStatus status = _table->RegisterFrameCallback(&OnNativeFrame, (void*)GCHandle.ToIntPtr(handle), &token);
        if (status != NmStatus.Ok)
        {
            handle.Free();
            EnsureOk(status, "RegisterFrameCallback");
        }

        var key = (CallbackKind.Frame, token);
        _pinnedCallbacks[key] = handle;
        return new ActionDisposable(() =>
        {
            if (_table->UnregisterFrameCallback is not null)
            {
                _table->UnregisterFrameCallback(key.token);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    /// <inheritdoc/>
    public bool EnqueueGameThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Volatile.Read(ref _disposed))
        {
            return false;
        }

        if (_table->EnqueueMainThread is not null)
        {
            var item = new PendingWorkItem(this, action);
            GCHandle handle = GCHandle.Alloc(item);
            nint ptr = GCHandle.ToIntPtr(handle);
            _pendingWorkHandles[ptr] = handle;

            NmStatus status = _table->EnqueueMainThread(&OnNativeMainThreadDrain, (void*)ptr);
            if (status != NmStatus.Ok)
            {
                if (_pendingWorkHandles.TryRemove(ptr, out GCHandle removed) && removed.IsAllocated)
                {
                    removed.Free();
                }

                if (status == NmStatus.ErrQueueFull)
                {
                    return false;
                }

                EnsureOk(status, "EnqueueMainThread");
            }

            return true;
        }

        while (true)
        {
            int cur = Volatile.Read(ref _pendingWorkCount);
            if (cur >= MaxMockPendingWork)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _pendingWorkCount, cur + 1, cur) == cur)
            {
                break;
            }
        }

        _pendingWork.Enqueue(action);
        return true;
    }

    /// <inheritdoc/>
    public int DrainGameThreadQueue(float deltaSeconds = 0f, int maxItems = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxItems);
        if (maxItems == 0)
        {
            return 0;
        }

        int drained = 0;
        bool prev = s_inNativeGameFrame;
        s_inNativeGameFrame = true;
        try
        {
            while (drained < maxItems && _pendingWork.TryDequeue(out Action? work))
            {
                Interlocked.Decrement(ref _pendingWorkCount);
                work();
                drained++;
            }
        }
        finally
        {
            s_inNativeGameFrame = prev;
        }

        if (_table->DrainMainThreadQueue is not null)
        {
            _table->DrainMainThreadQueue(deltaSeconds, (uint)Math.Max(1, maxItems));
        }

        return drained;
    }

    /// <inheritdoc/>
    public IDisposable RegisterFileOverlayResolver(Func<string, byte[]?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (_table->RegisterFileOverlay is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle handle = GCHandle.Alloc(resolver);
        ulong token = 0;
        NmStatus status = _table->RegisterFileOverlay(&OnNativeFileOverlay, &OnNativeFileOverlayFree, (void*)GCHandle.ToIntPtr(handle), &token);
        if (status != NmStatus.Ok)
        {
            handle.Free();
            EnsureOk(status, "RegisterFileOverlay");
        }

        var key = (CallbackKind.FileOverlay, token);
        _pinnedCallbacks[key] = handle;
        Volatile.Write(ref _managedFileOverlayToken, token);
        return new ActionDisposable(() =>
        {
            if (Volatile.Read(ref _managedFileOverlayToken) == key.token)
            {
                Volatile.Write(ref _managedFileOverlayToken, 0);
            }

            if (_table->UnregisterFileOverlay is not null)
            {
                _table->UnregisterFileOverlay(key.token);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    /// <inheritdoc/>
    public void SetFileOverlayKnownPaths(IReadOnlyCollection<string> normalizedPaths)
    {
        ArgumentNullException.ThrowIfNull(normalizedPaths);
        ulong token = Volatile.Read(ref _managedFileOverlayToken);
        if (token == 0 || _table->SetFileOverlayKnownPaths is null)
        {
            return;
        }

        int count = normalizedPaths.Count;
        if (count == 0)
        {
            _table->SetFileOverlayKnownPaths(token, null, 0);
            return;
        }

        byte** ptrs = (byte**)NativeMemory.Alloc((nuint)count, (nuint)sizeof(byte*));
        int allocated = 0;
        try
        {
            foreach (string path in normalizedPaths)
            {
                int byteLen = Encoding.UTF8.GetByteCount(path);
                byte* buf = (byte*)NativeMemory.Alloc((nuint)(byteLen + 1));
                Encoding.UTF8.GetBytes(path, new Span<byte>(buf, byteLen));
                buf[byteLen] = 0;
                ptrs[allocated++] = buf;
            }

            _table->SetFileOverlayKnownPaths(token, ptrs, (nuint)allocated);
        }
        finally
        {
            for (int i = 0; i < allocated; i++)
            {
                NativeMemory.Free(ptrs[i]);
            }

            NativeMemory.Free(ptrs);
        }
    }

    /// <inheritdoc/>
    public bool SupportsConnectionToken => _table->GetActiveConnectionToken is not null;

    /// <inheritdoc/>
    public ulong GetActiveConnectionToken()
    {
        if (_table->GetActiveConnectionToken is null)
        {
            return 0;
        }

        ulong token = 0;
        return _table->GetActiveConnectionToken(&token) == NmStatus.Ok ? token : 0;
    }

    /// <inheritdoc/>
    public byte[]? ReadStockGameFile(string virtualPath)
    {
        string normalized = NormalizeAndValidateVirtualPath(virtualPath);
        if (_table->FilesReadStock is null)
        {
            return null;
        }

        byte[] utf8 = ToNullTerminatedUtf8(normalized);
        byte* outData = null;
        nuint outSize = 0;
        try
        {
            fixed (byte* pPath = utf8)
            {
                NmStatus status = _table->FilesReadStock(pPath, &outData, &outSize);
                if (status != NmStatus.Ok || outData is null)
                {
                    return null;
                }
            }

            int len = checked((int)outSize);
            byte[] copy = new byte[len];
            if (len > 0)
            {
                Marshal.Copy((nint)outData, copy, 0, len);
            }

            return copy;
        }
        finally
        {
            if (outData is not null && _table->FreeFileOverlayBuffer is not null)
            {
                _table->FreeFileOverlayBuffer(outData);
            }
        }
    }

    /// <inheritdoc/>
    public void InvalidateCachedAsset(string virtualPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        if (_table->FilesInvalidate is null)
        {
            return;
        }

        string normalized = ClientTextEncoding.NormalizeVirtualPath(virtualPath);
        byte[] utf8 = ToNullTerminatedUtf8(normalized);
        fixed (byte* pPath = utf8)
        {
            _table->FilesInvalidate(pPath);
        }
    }

    /// <inheritdoc/>
    public IDisposable RegisterRulesetReceiver(byte rulesetId, Action<ReadOnlyMemory<byte>> onPayload)
    {
        ArgumentNullException.ThrowIfNull(onPayload);
        if (_table->RegisterRulesetHandler is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle handle = GCHandle.Alloc(new RulesetCallbackWrapper(this, onPayload));
        ulong token = 0;
        NmStatus status = _table->RegisterRulesetHandler(rulesetId, &OnNativeRulesetRx, (void*)GCHandle.ToIntPtr(handle), &token);
        if (status != NmStatus.Ok)
        {
            handle.Free();
            EnsureOk(status, "RegisterRulesetReceiver");
        }

        var key = (CallbackKind.Ruleset, token);
        _pinnedCallbacks[key] = handle;
        return new ActionDisposable(() =>
        {
            if (_table->UnregisterRulesetHandler is not null)
            {
                _table->UnregisterRulesetHandler(key.token);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    /// <inheritdoc/>
    public void SendRulesetPacket(byte rulesetId, ReadOnlySpan<byte> payload)
    {
        if (_table->SendRulesetPacket is null)
        {
            throw new NotSupportedException("Native shim does not expose send_ruleset_packet.");
        }

        fixed (byte* pData = payload)
        {
            NmStatus status = _table->SendRulesetPacket(rulesetId, pData, (uint)payload.Length);
            EnsureOk(status, "SendRulesetPacket");
        }
    }

    /// <inheritdoc/>
    public IDisposable RegisterChatInterceptor(Func<string, string, bool> onIncomingChat)
    {
        ArgumentNullException.ThrowIfNull(onIncomingChat);
        if (_table->RegisterChatChannelHandler is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle handle = GCHandle.Alloc(new ChatCallbackWrapper(this, onIncomingChat));
        ulong token = 0;
        NmStatus status = _table->RegisterChatChannelHandler(&OnNativeChatRx, (void*)GCHandle.ToIntPtr(handle), &token);
        if (status != NmStatus.Ok)
        {
            handle.Free();
            EnsureOk(status, "RegisterChatInterceptor");
        }

        var key = (CallbackKind.Chat, token);
        _pinnedCallbacks[key] = handle;
        return new ActionDisposable(() =>
        {
            if (_table->UnregisterChatChannelHandler is not null)
            {
                _table->UnregisterChatChannelHandler(key.token);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    /// <inheritdoc/>
    public void SendChatLine(string text)
    {
        if (_table->SendChatLine is null)
        {
            throw new NotSupportedException("Native shim does not expose send_chat_line.");
        }

        byte[] utf8 = ToNullTerminatedUtf8(text);
        fixed (byte* pText = utf8)
        {
            NmStatus status = _table->SendChatLine(pText);
            EnsureOk(status, "SendChatLine");
        }
    }

    /// <inheritdoc/>
    public IDisposable RegisterRawOpcodeReceiver(ushort opcode, Action<ReadOnlyMemory<byte>> onPayload)
    {
        ArgumentNullException.ThrowIfNull(onPayload);
        if (_table->RegisterOpcodeHandlerGroup is not null)
        {
            return RegisterRawOpcodeGroup(opcode, opcode, (_, payload) => onPayload(payload));
        }

        List<Action<ReadOnlyMemory<byte>>> list = _rawReceivers.GetOrAdd(opcode, _ => []);
        lock (list)
        {
            list.Add(onPayload);
        }

        return new ActionDisposable(() =>
        {
            lock (list)
            {
                list.Remove(onPayload);
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable RegisterRawOpcodeGroup(ushort minOpcode, ushort maxOpcode, Action<ushort, ReadOnlyMemory<byte>> onPayload)
    {
        ArgumentNullException.ThrowIfNull(onPayload);
        if (_table->RegisterOpcodeHandlerGroup is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle handle = GCHandle.Alloc(new OpcodeHandlerWrapper(this, onPayload));
        ulong token = 0;
        NmStatus status = _table->RegisterOpcodeHandlerGroup(minOpcode, maxOpcode, &OnNativeOpcodeRx, (void*)GCHandle.ToIntPtr(handle), &token);
        if (status != NmStatus.Ok)
        {
            handle.Free();
            EnsureOk(status, $"RegisterRawOpcodeGroup(0x{minOpcode:X4}..0x{maxOpcode:X4})");
        }

        var key = (CallbackKind.OpcodeRx, token);
        _pinnedCallbacks[key] = handle;
        return new ActionDisposable(() =>
        {
            if (_table->UnregisterOpcodeHandlerGroup is not null)
            {
                _table->UnregisterOpcodeHandlerGroup(key.token);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable RegisterRawOpcodeStreamHandler(ushort minOpcode, ushort maxOpcode, Func<ushort, INativePacketStream, ReadOnlyMemory<byte>, bool> onStream)
    {
        ArgumentNullException.ThrowIfNull(onStream);
        if (_table->RegisterOpcodeHandlerGroup is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle handle = GCHandle.Alloc(new OpcodeHandlerWrapper(this, onStream));
        ulong token = 0;
        NmStatus status = _table->RegisterOpcodeHandlerGroup(minOpcode, maxOpcode, &OnNativeOpcodeRx, (void*)GCHandle.ToIntPtr(handle), &token);
        if (status != NmStatus.Ok)
        {
            handle.Free();
            EnsureOk(status, $"RegisterRawOpcodeStreamHandler(0x{minOpcode:X4}..0x{maxOpcode:X4})");
        }

        var key = (CallbackKind.OpcodeRx, token);
        _pinnedCallbacks[key] = handle;
        return new ActionDisposable(() =>
        {
            if (_table->UnregisterOpcodeHandlerGroup is not null)
            {
                _table->UnregisterOpcodeHandlerGroup(key.token);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    /// <inheritdoc/>
    public bool StreamRead(nint streamHandle, Span<byte> destination)
    {
        if (streamHandle == nint.Zero || _table->StreamRead is null)
        {
            return false;
        }

        if (destination.IsEmpty)
        {
            return true;
        }

        fixed (byte* pDst = destination)
        {
            return _table->StreamRead((void*)streamHandle, pDst, (ulong)destination.Length) == NmStatus.Ok;
        }
    }

    /// <inheritdoc/>
    public void SendRawPacket(ushort opcode, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, "Raw packet payload exceeds 65536 bytes.");
        }

        if (_table->SendRawPacket is null)
        {
            return;
        }

        fixed (byte* pPayload = payload)
        {
            NmStatus status = _table->SendRawPacket(opcode, pPayload, (uint)payload.Length);
            EnsureOk(status, $"SendRawPacket(0x{opcode:X4})");
        }
    }

    /// <inheritdoc/>
    public GealInvocationResult InvokeGeal(uint objectId, string entryName, bool isEvent, ReadOnlySpan<GealValue> args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
        if (_table->GealInvokeFunction is null)
        {
            return new(false, GealValue.Null, "Native shim does not expose geal_invoke_function.");
        }

        byte[] fnUtf8 = ToNullTerminatedUtf8(entryName.Trim());
        byte[] argsJsonUtf8 = ToNullTerminatedUtf8(SerializeGealArgumentsJson(args));
        byte[] outBuf = new byte[1024];
        nuint outLen = 0;

        NmStatus status;
        fixed (byte* pFn = fnUtf8)
        fixed (byte* pArgs = argsJsonUtf8)
        fixed (byte* pOut = outBuf)
        {
            status = _table->GealInvokeFunction(objectId, pFn, pArgs, pOut, (nuint)outBuf.Length, &outLen);
        }

        if (status == NmStatus.ErrBufferTooSmall && outLen > 0 && outLen <= 1024u * 1024u)
        {
            outBuf = new byte[(int)outLen + 1];
            fixed (byte* pFn = fnUtf8)
            fixed (byte* pArgs = argsJsonUtf8)
            fixed (byte* pOut = outBuf)
            {
                status = _table->GealInvokeFunction(objectId, pFn, pArgs, pOut, (nuint)outBuf.Length, &outLen);
            }
        }

        if (status != NmStatus.Ok)
        {
            return new GealInvocationResult(false, GealValue.Null, $"geal_invoke_function('{entryName}') failed with status {status}.");
        }

        if (outLen == 0)
        {
            return new GealInvocationResult(true, GealValue.Null);
        }

        int len = (int)Math.Min(outLen, (nuint)outBuf.Length);
        string resultJson = Encoding.UTF8.GetString(outBuf, 0, len).TrimEnd('\0');
        return new GealInvocationResult(true, DeserializeGealValueJson(resultJson));
    }

    /// <inheritdoc/>
    public int PlayAudio(string pathOrEvent, bool isFmodEvent, float volume) =>
        PlayAudio(pathOrEvent, isFmodEvent, volume, loop: false);

    /// <inheritdoc/>
    public int PlayAudio(string pathOrEvent, bool isFmodEvent, float volume, bool loop)
    {
        byte[] utf8 = ToNullTerminatedUtf8(pathOrEvent);
        fixed (byte* pPath = utf8)
        {
            int handle = 0;
            if (isFmodEvent && _table->AudioPlayEvent is not null)
            {
                if (_table->AudioPlayEvent(pPath, volume, &handle) == NmStatus.Ok && handle > 0)
                {
                    return handle;
                }
            }
            else if (!isFmodEvent && _table->AudioPlayOgg is not null)
            {
                if (_table->AudioPlayOgg(pPath, volume, loop ? 1 : 0, &handle) == NmStatus.Ok && handle > 0)
                {
                    return handle;
                }
            }
        }

        return Interlocked.Increment(ref _nextAudioChannel);
    }

    /// <inheritdoc/>
    public void SetAudioVolume(int channelHandle, float volume)
    {
        if (_table->AudioSetVolume is not null && channelHandle > 0)
        {
            _table->AudioSetVolume(channelHandle, Math.Clamp(volume, 0f, 1f));
        }
    }

    /// <inheritdoc/>
    public bool IsAudioPlaying(int channelHandle)
    {
        if (_table->AudioIsPlaying is not null && channelHandle > 0)
        {
            int playing = 0;
            if (_table->AudioIsPlaying(channelHandle, &playing) == NmStatus.Ok)
            {
                return playing != 0;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public void StopAudio(int channelHandle)
    {
        if (_table->AudioStop is not null && channelHandle > 0)
        {
            _table->AudioStop(channelHandle);
        }
    }

    /// <inheritdoc/>
    public nint GetNativeUiRoot()
    {
        if (_table->UiGetActiveRoot is not null)
        {
            void* activeRoot = null;
            if (_table->UiGetActiveRoot(&activeRoot) == NmStatus.Ok && activeRoot is not null)
            {
                return (nint)activeRoot;
            }
        }

        if (_table->UiGetRoot is null)
        {
            return nint.Zero;
        }

        void* root = null;
        return _table->UiGetRoot(&root) == NmStatus.Ok ? (nint)root : nint.Zero;
    }

    /// <inheritdoc/>
    public nint CreateNativeUiWindow(string virtualPath)
    {
        if (_table->UiCreateWindow is null || string.IsNullOrWhiteSpace(virtualPath))
        {
            return nint.Zero;
        }

        byte[] utf8 = ToNullTerminatedUtf8(virtualPath);
        fixed (byte* pPath = utf8)
        {
            void* ctrl = null;
            return _table->UiCreateWindow(pPath, &ctrl) == NmStatus.Ok ? (nint)ctrl : nint.Zero;
        }
    }

    /// <inheritdoc/>
    public nint CreateNativeUiWindowInto(nint parentControl, string virtualPath)
    {
        if (_table->UiCreateWindowInto is null || parentControl == nint.Zero || string.IsNullOrWhiteSpace(virtualPath))
        {
            return nint.Zero;
        }

        byte[] utf8 = ToNullTerminatedUtf8(virtualPath);
        fixed (byte* pPath = utf8)
        {
            void* ctrl = null;
            return _table->UiCreateWindowInto((void*)parentControl, pPath, &ctrl) == NmStatus.Ok ? (nint)ctrl : nint.Zero;
        }
    }

    /// <inheritdoc/>
    public nint FindNativeUiChild(nint parentControl, string dotPath)
    {
        if (_table->UiFindChild is null || parentControl == nint.Zero || string.IsNullOrWhiteSpace(dotPath))
        {
            return nint.Zero;
        }

        byte[] utf8 = ToNullTerminatedUtf8(dotPath);
        fixed (byte* pPath = utf8)
        {
            void* child = null;
            return _table->UiFindChild((void*)parentControl, pPath, &child) == NmStatus.Ok ? (nint)child : nint.Zero;
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiVisible(nint control, bool visible)
    {
        if (_table->UiSetVisible is not null && control != nint.Zero)
        {
            _table->UiSetVisible((void*)control, visible ? 1 : 0);
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiText(nint control, string text, bool isButton)
    {
        if (_table->UiSetText is not null && control != nint.Zero)
        {
            byte[] utf8 = ToNullTerminatedUtf8(text ?? string.Empty);
            fixed (byte* pText = utf8)
            {
                _table->UiSetText((void*)control, pText, isButton ? 1 : 0);
            }
        }
    }

    /// <inheritdoc/>
    public bool TryGetNativeUiRect(nint control, out UiRect rect)
    {
        rect = default;
        if (_table->UiGetRect is null || control == nint.Zero)
        {
            return false;
        }

        float x = 0f, y = 0f, w = 0f, h = 0f;
        if (_table->UiGetRect((void*)control, &x, &y, &w, &h) != NmStatus.Ok)
        {
            return false;
        }

        rect = new UiRect(x, y, w, h);
        return true;
    }

    /// <inheritdoc/>
    public void SetNativeUiPosition(nint control, float x, float y)
    {
        if (_table->UiSetPosition is not null && control != nint.Zero)
        {
            _table->UiSetPosition((void*)control, x, y);
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiSize(nint control, float width, float height)
    {
        if (_table->UiSetSize is not null && control != nint.Zero)
        {
            _table->UiSetSize((void*)control, width, height);
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiEnabled(nint control, bool enabled)
    {
        if (_table->UiSetEnabled is not null && control != nint.Zero)
        {
            _table->UiSetEnabled((void*)control, enabled ? 1 : 0);
        }
    }

    /// <inheritdoc/>
    public bool TryGetNativeUiEnabled(nint control, out bool enabled)
    {
        enabled = false;
        if (_table->UiGetEnabled is null || control == nint.Zero)
        {
            return false;
        }

        int raw = 0;
        if (_table->UiGetEnabled((void*)control, &raw) != NmStatus.Ok)
        {
            return false;
        }

        enabled = raw != 0;
        return true;
    }

    /// <inheritdoc/>
    public bool RemoveNativeUiChild(nint parentControl, nint childControl)
    {
        if (_table->UiRemoveChild is null || parentControl == nint.Zero || childControl == nint.Zero)
        {
            return false;
        }

        return _table->UiRemoveChild((void*)parentControl, (void*)childControl) == NmStatus.Ok;
    }

    /// <inheritdoc/>
    public void DestroyNativeUiControl(nint control)
    {
        if (control == nint.Zero)
        {
            return;
        }

        CleanupUiSlotsForControl(control);
        if (_table->UiDestroyControl is not null)
        {
            _table->UiDestroyControl((void*)control);
        }
    }

    /// <inheritdoc/>
    public string? GetNativeUiTextBoxText(nint textboxControl)
    {
        if (_table->UiTextboxGetText is null || textboxControl == nint.Zero)
        {
            return null;
        }

        nuint length = 0;
        NmStatus st = _table->UiTextboxGetText((void*)textboxControl, null, 0, &length);
        if (st != NmStatus.Ok)
        {
            return null;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            int cap = checked((int)length + 1);
            byte[] buf = new byte[cap];
            fixed (byte* pBuf = buf)
            {
                nuint actual = 0;
                st = _table->UiTextboxGetText((void*)textboxControl, pBuf, (nuint)cap, &actual);
                if (st == NmStatus.Ok)
                {
                    return Encoding.UTF8.GetString(buf, 0, checked((int)actual));
                }

                if (st != NmStatus.ErrBufferTooSmall || actual == 0)
                {
                    return null;
                }

                length = actual;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public void SetNativeUiTextBoxText(nint textboxControl, string text)
    {
        if (_table->UiTextboxSetText is null || textboxControl == nint.Zero)
        {
            return;
        }

        byte[] utf8 = ToNullTerminatedUtf8(text ?? string.Empty);
        fixed (byte* pText = utf8)
        {
            _table->UiTextboxSetText((void*)textboxControl, pText);
        }
    }

    /// <inheritdoc/>
    public IDisposable SubscribeNativeUiTextBoxChanged(nint textboxControl, Action onChanged) =>
        SubscribeNativeUiEvent(
            CallbackKind.TextBoxChanged,
            textboxControl,
            onChanged,
            _table->UiTextboxSubscribeChanged);

    /// <inheritdoc/>
    public IDisposable SubscribeNativeUiTextBoxSubmitted(nint textboxControl, Action onSubmitted) =>
        SubscribeNativeUiEvent(
            CallbackKind.TextBoxSubmitted,
            textboxControl,
            onSubmitted,
            _table->UiTextboxSubscribeSubmitted);

    /// <inheritdoc/>
    public bool? GetNativeUiCheckBoxChecked(nint checkboxControl)
    {
        if (_table->UiCheckboxGetChecked is null || checkboxControl == nint.Zero)
        {
            return null;
        }

        int value = 0;
        return _table->UiCheckboxGetChecked((void*)checkboxControl, &value) == NmStatus.Ok ? value != 0 : null;
    }

    /// <inheritdoc/>
    public void SetNativeUiCheckBoxChecked(nint checkboxControl, bool isChecked)
    {
        if (_table->UiCheckboxSetChecked is not null && checkboxControl != nint.Zero)
        {
            _table->UiCheckboxSetChecked((void*)checkboxControl, isChecked ? 1 : 0);
        }
    }

    /// <inheritdoc/>
    public IDisposable SubscribeNativeUiCheckBoxChanged(nint checkboxControl, Action onChanged) =>
        SubscribeNativeUiEvent(
            CallbackKind.CheckBoxChanged,
            checkboxControl,
            onChanged,
            _table->UiCheckboxSubscribeChanged);

    /// <inheritdoc/>
    public IDisposable SubscribeNativeUiClick(nint buttonControl, Action onClicked) =>
        SubscribeNativeUiEvent(
            CallbackKind.UiClick,
            buttonControl,
            onClicked,
            _table->UiSubscribeClick);

    /// <inheritdoc/>
    public bool TryGetNativeUiVisible(nint control, out bool visible)
    {
        visible = false;
        if (_table->UiIsVisible is null || control == nint.Zero)
        {
            return false;
        }

        int raw = 0;
        if (_table->UiIsVisible((void*)control, &raw) != NmStatus.Ok)
        {
            return false;
        }

        visible = raw != 0;
        return true;
    }

    /// <inheritdoc/>
    public string? GetNativeUiId(nint control)
    {
        if (_table->UiGetControlId is null || control == nint.Zero)
        {
            return null;
        }

        nuint length = 0;
        NmStatus st = _table->UiGetControlId((void*)control, null, 0, &length);
        if (st != NmStatus.Ok)
        {
            return null;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            int cap = checked((int)length + 1);
            byte[] buf = new byte[cap];
            fixed (byte* pBuf = buf)
            {
                nuint actual = 0;
                st = _table->UiGetControlId((void*)control, pBuf, (nuint)cap, &actual);
                if (st == NmStatus.Ok)
                {
                    return Encoding.UTF8.GetString(buf, 0, checked((int)actual));
                }

                if (st != NmStatus.ErrBufferTooSmall || actual == 0)
                {
                    return null;
                }

                length = actual;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public ulong GetNativeUiVtable(nint control)
    {
        if (_table->UiGetVtable is null || control == nint.Zero)
        {
            return 0;
        }

        ulong vt = 0;
        return _table->UiGetVtable((void*)control, &vt) == NmStatus.Ok ? vt : 0;
    }

    /// <inheritdoc/>
    public uint GetNativeUiTag(nint control)
    {
        if (_table->UiGetControlTag is null || control == nint.Zero)
        {
            return 0;
        }

        ushort tag = 0;
        return _table->UiGetControlTag((void*)control, &tag) == NmStatus.Ok ? tag : 0u;
    }

    /// <inheritdoc/>
    public nint GetNativeUiParent(nint control)
    {
        if (_table->UiGetControlParent is null || control == nint.Zero)
        {
            return nint.Zero;
        }

        void* parent = null;
        return _table->UiGetControlParent((void*)control, &parent) == NmStatus.Ok ? (nint)parent : nint.Zero;
    }

    /// <inheritdoc/>
    public IReadOnlyList<nint> GetNativeUiChildren(nint control)
    {
        if (_table->UiGetChildCount is null || _table->UiGetChildAt is null || control == nint.Zero)
        {
            return [];
        }

        uint count = 0;
        if (_table->UiGetChildCount((void*)control, &count) != NmStatus.Ok || count == 0)
        {
            return [];
        }

        int capped = (int)Math.Min(count, 4096u);
        var result = new List<nint>(capped);
        for (uint i = 0; i < (uint)capped; i++)
        {
            void* child = null;
            if (_table->UiGetChildAt((void*)control, i, &child) == NmStatus.Ok && child is not null)
            {
                result.Add((nint)child);
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public void SetNativeUiWidth(nint control, float width)
    {
        if (_table->UiSetWidth is not null && control != nint.Zero)
        {
            _table->UiSetWidth((void*)control, width);
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiHeight(nint control, float height)
    {
        if (_table->UiSetHeight is not null && control != nint.Zero)
        {
            _table->UiSetHeight((void*)control, height);
        }
    }

    /// <inheritdoc/>
    public void SetNativeUiAnchor(nint control, byte xAnchor, float xOffset, byte yAnchor, float yOffset)
    {
        if (_table->UiSetAnchor is not null && control != nint.Zero)
        {
            _table->UiSetAnchor((void*)control, xAnchor, xOffset, yAnchor, yOffset);
        }
    }

    /// <inheritdoc/>
    public void UpdateNativeUiLayout(nint control)
    {
        if (_table->UiLayoutUpdate is not null && control != nint.Zero)
        {
            _table->UiLayoutUpdate((void*)control);
        }
    }

    /// <inheritdoc/>
    public nint CreateNativeUiCheckBoxChild(nint parentControl, string caption, bool initialChecked)
    {
        if (_table->UiCreateCheckBoxChild is null || parentControl == nint.Zero)
        {
            return nint.Zero;
        }

        byte[] utf8 = ToNullTerminatedUtf8(caption ?? string.Empty);
        fixed (byte* pCaption = utf8)
        {
            void* cb = null;
            if (_table->UiCreateCheckBoxChild((void*)parentControl, pCaption, &cb) != NmStatus.Ok || cb is null)
            {
                return nint.Zero;
            }

            if (initialChecked && _table->UiCheckboxSetChecked is not null)
            {
                _table->UiCheckboxSetChecked(cb, 1);
            }

            return (nint)cb;
        }
    }

    private bool TryGetNativeScreensState(out NmScreensState state)
    {
        state = default;
        if (_table->ScreensGetState is null)
        {
            return false;
        }

        NmScreensState local = default;
        local.StructSize = (uint)sizeof(NmScreensState);
        if (_table->ScreensGetState(&local) != NmStatus.Ok)
        {
            return false;
        }

        state = local;
        return true;
    }

    /// <inheritdoc/>
    public PreGameScreen GetPreGameScreen()
    {
        if (!TryGetNativeScreensState(out NmScreensState state))
        {
            return PreGameScreen.Unknown;
        }

        return (NmPreGameScreen)state.Screen switch
        {
            NmPreGameScreen.Login => PreGameScreen.Login,
            NmPreGameScreen.SelectCharacter => PreGameScreen.SelectCharacter,
            NmPreGameScreen.CreateCharacter => PreGameScreen.CreateCharacter,
            NmPreGameScreen.LeagueOverlay => PreGameScreen.LeagueOverlay,
            NmPreGameScreen.InGame => PreGameScreen.InGame,
            _ => PreGameScreen.Unknown,
        };
    }

    /// <inheritdoc/>
    public nint GetInGameUiHandle() =>
        TryGetNativeScreensState(out NmScreensState state) ? (nint)state.HudRoot : nint.Zero;

    /// <inheritdoc/>
    public GamePanel GetActivePanels()
    {
        if (!TryGetNativeScreensState(out NmScreensState state))
        {
            return GamePanel.None;
        }

        var flags = (NmPanelFlags)state.VisiblePanels;
        GamePanel result = GamePanel.None;
        if (flags.HasFlag(NmPanelFlags.Hud)) result |= GamePanel.Hud;
        if (flags.HasFlag(NmPanelFlags.Inventory)) result |= GamePanel.Inventory;
        if (flags.HasFlag(NmPanelFlags.Stash) || flags.HasFlag(NmPanelFlags.GuildStash) || flags.HasFlag(NmPanelFlags.TradeStash)) result |= GamePanel.Stash;
        if (flags.HasFlag(NmPanelFlags.MapStash)) result |= GamePanel.MapStash;
        if (flags.HasFlag(NmPanelFlags.BlightStash)) result |= GamePanel.BlightStash;
        if (flags.HasFlag(NmPanelFlags.PassiveTree)) result |= GamePanel.PassiveTree;
        if (flags.HasFlag(NmPanelFlags.AtlasTree)) result |= GamePanel.AtlasPassiveTree;
        if (flags.HasFlag(NmPanelFlags.VendorBuy) || flags.HasFlag(NmPanelFlags.VendorSell)) result |= GamePanel.Vendor;
        if (flags.HasFlag(NmPanelFlags.NpcDialogue)) result |= GamePanel.NpcDialogue;
        if (flags.HasFlag(NmPanelFlags.PlayerTrade)) result |= GamePanel.PlayerTrade;
        return result;
    }

    /// <inheritdoc/>
    public nint GetPanelControlHandle(GamePanel panel)
    {
        if (!TryGetNativeScreensState(out NmScreensState state))
        {
            return nint.Zero;
        }

        return panel switch
        {
            GamePanel.Hud => (nint)state.HudRoot,
            GamePanel.Inventory => (nint)state.InventoryPanel,
            GamePanel.Stash => (nint)state.StashPanel,
            GamePanel.MapStash => (nint)state.MapStashPage,
            GamePanel.BlightStash => (nint)state.BlightStashPage,
            GamePanel.PassiveTree => (nint)state.PassivePanel,
            GamePanel.Vendor => (nint)state.VendorPanel,
            _ => nint.Zero,
        };
    }

    /// <inheritdoc/>
    public nint GetSlotParentHandle(ScreenSlot slot)
    {
        if (!TryGetNativeScreensState(out NmScreensState state))
        {
            return nint.Zero;
        }

        var flags = (NmPanelFlags)state.VisiblePanels;
        return slot switch
        {
            ScreenSlot.InventoryToolbar => flags.HasFlag(NmPanelFlags.Inventory)
                ? (state.InventoryContent is not null ? (nint)state.InventoryContent : (nint)state.InventoryPanel)
                : nint.Zero,
            ScreenSlot.StashHeader or ScreenSlot.StashFooter =>
                (flags.HasFlag(NmPanelFlags.Stash) || flags.HasFlag(NmPanelFlags.GuildStash) || flags.HasFlag(NmPanelFlags.TradeStash))
                    ? (nint)state.StashPanel
                    : nint.Zero,
            ScreenSlot.MapTabBar => flags.HasFlag(NmPanelFlags.MapStash) ? (nint)state.MapStashPage : nint.Zero,
            ScreenSlot.BlightTabBar => flags.HasFlag(NmPanelFlags.BlightStash) ? (nint)state.BlightStashPage : nint.Zero,
            ScreenSlot.PassiveTreeHeader => flags.HasFlag(NmPanelFlags.PassiveTree)
                ? (state.PassiveTitleBar is not null ? (nint)state.PassiveTitleBar : (nint)state.PassivePanel)
                : nint.Zero,
            ScreenSlot.HudTopRight => (nint)state.HudRoot,
            ScreenSlot.CreateCharacterBar => (NmPreGameScreen)state.Screen == NmPreGameScreen.CreateCharacter
                ? (nint)state.CreateCharFrame
                : nint.Zero,
            _ => nint.Zero,
        };
    }

    /// <inheritdoc/>
    public StashTabInfo? GetActiveStashTab()
    {
        if (!TryGetNativeScreensState(out NmScreensState state) || state.ActiveStashTabIndex < 0)
        {
            return null;
        }

        StashTabType type = Enum.IsDefined(typeof(StashTabType), (int)state.ActiveStashTabType)
            ? (StashTabType)(int)state.ActiveStashTabType
            : StashTabType.Unknown;
        int? mapTier = type == StashTabType.Map && state.ActiveMapTier >= 0 ? state.ActiveMapTier : null;
        return new StashTabInfo(
            Index: state.ActiveStashTabIndex,
            Id: state.ActiveStashTabId,
            Type: type,
            Name: state.GetActiveStashTabName(),
            MapTier: mapTier,
            IsGuild: state.StashMode == 1u);
    }

    /// <inheritdoc/>
    public bool IsMinimapVisible() =>
        TryGetNativeScreensState(out NmScreensState state) && ((NmPanelFlags)state.VisiblePanels).HasFlag(NmPanelFlags.Minimap);

    /// <inheritdoc/>
    public bool IsRightSidePanelOpen() =>
        TryGetNativeScreensState(out NmScreensState state) && ((NmPanelFlags)state.VisiblePanels).HasFlag(NmPanelFlags.RightSide);

    /// <inheritdoc/>
    public IDisposable RegisterLeagueOverlayFilledCallback(Action<nint, IReadOnlyList<LeagueCardInfo>> onFilled)
    {
        ArgumentNullException.ThrowIfNull(onFilled);
        lock (_phase1Gate)
        {
            _leagueFillCallbacks.Add(onFilled);
            if (!_screensNativeHooked && _table->ScreensSetCallback is not null)
            {
                void* ud = EnsureSelfCallbackUserDataLocked();
                if (_table->ScreensSetCallback(&OnNativeScreenEvent, ud) == NmStatus.Ok)
                {
                    _screensNativeHooked = true;
                }
            }
        }

        return new ActionDisposable(() =>
        {
            lock (_phase1Gate)
            {
                _leagueFillCallbacks.Remove(onFilled);
                if (_leagueFillCallbacks.Count == 0 && _screensNativeHooked && _table->ScreensSetCallback is not null)
                {
                    _table->ScreensSetCallback(null, null);
                    _screensNativeHooked = false;
                }
            }
        });
    }

    /// <inheritdoc/>
    public bool IsTextInputFocused()
    {
        if (_table->InputIsTextFocused is null)
        {
            return false;
        }

        int focused = 0;
        return _table->InputIsTextFocused(&focused) == NmStatus.Ok && focused != 0;
    }

    /// <inheritdoc/>
    public IDisposable RegisterKeyCallback(Func<KeyEventArgs, bool> onKey)
    {
        ArgumentNullException.ThrowIfNull(onKey);
        lock (_phase1Gate)
        {
            _keyCallbacks.Add(onKey);
            EnsureInputCallbackHookedLocked();
        }

        return new ActionDisposable(() =>
        {
            lock (_phase1Gate)
            {
                _keyCallbacks.Remove(onKey);
                ReleaseInputCallbackIfUnusedLocked();
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable RegisterMouseCallback(Func<MouseEventArgs, bool> onMouse)
    {
        ArgumentNullException.ThrowIfNull(onMouse);
        lock (_phase1Gate)
        {
            _mouseCallbacks.Add(onMouse);
            EnsureInputCallbackHookedLocked();
        }

        return new ActionDisposable(() =>
        {
            lock (_phase1Gate)
            {
                _mouseCallbacks.Remove(onMouse);
                ReleaseInputCallbackIfUnusedLocked();
            }
        });
    }

    private bool TryGetNativeHoverState(out NmHoverState state)
    {
        state = default;
        if (_table->HoverGetState is null)
        {
            return false;
        }

        NmHoverState local = default;
        local.StructSize = (uint)sizeof(NmHoverState);
        if (_table->HoverGetState(&local) != NmStatus.Ok)
        {
            return false;
        }

        state = local;
        return true;
    }

    /// <inheritdoc/>
    public nint GetHoveredControlHandle() =>
        TryGetNativeHoverState(out NmHoverState state) ? (nint)state.HoveredControl : nint.Zero;

    /// <inheritdoc/>
    public HoveredItemInfo? GetHoveredItem()
    {
        if (!TryGetNativeHoverState(out NmHoverState state) || state.HoveredItem.Present == 0)
        {
            return null;
        }

        NmItemSnapshot snap = state.HoveredItem;
        return new HoveredItemInfo(
            EntityHandle: (nint)snap.ItemEntity,
            ControlHandle: (nint)snap.ItemView,
            ItemId: snap.ItemId,
            InventoryId: snap.InventoryId,
            GridX: snap.GridX,
            GridY: snap.GridY,
            Width: snap.GridW,
            Height: snap.GridH,
            MetadataPath: snap.GetMetadataPath(),
            BaseName: snap.GetBaseName(),
            StackSize: snap.StackSize > 0 ? snap.StackSize : 1u,
            MaxStackSize: snap.MaxStackSize > 0 ? snap.MaxStackSize : 1u,
            Rarity: MapItemRarity(snap.Rarity),
            IsIdentified: snap.Identified != 0,
            IsCorrupted: snap.Corrupted != 0,
            ItemLevel: snap.ItemLevel,
            ItemClassId: snap.ItemClassId);
    }

    /// <inheritdoc/>
    public CursorItemInfo? GetCursorItem()
    {
        if (!TryGetNativeHoverState(out NmHoverState state))
        {
            return null;
        }

        CursorMode mode = (NmCursorMode)state.CursorMode switch
        {
            NmCursorMode.Holding => CursorMode.HoldingItem,
            NmCursorMode.UseMode => CursorMode.UsingItem,
            _ => CursorMode.Empty,
        };
        if (mode == CursorMode.Empty && state.CursorItem.Present == 0)
        {
            return null;
        }

        NmItemSnapshot snap = state.CursorItem;
        uint sourceInvId = mode == CursorMode.UsingItem && state.UseSourceInvId != 0 ? state.UseSourceInvId : snap.InventoryId;
        int sourceX = mode == CursorMode.UsingItem ? state.UseCellX : snap.GridX;
        int sourceY = mode == CursorMode.UsingItem ? state.UseCellY : snap.GridY;
        return new CursorItemInfo(
            Mode: mode == CursorMode.Empty ? CursorMode.HoldingItem : mode,
            EntityHandle: (nint)snap.ItemEntity,
            ItemId: snap.ItemId,
            SourceInventoryId: sourceInvId,
            SourceGridX: sourceX,
            SourceGridY: sourceY,
            Width: snap.GridW,
            Height: snap.GridH,
            MetadataPath: snap.GetMetadataPath(),
            BaseName: snap.GetBaseName(),
            StackSize: snap.StackSize > 0 ? snap.StackSize : 1u,
            MaxStackSize: snap.MaxStackSize > 0 ? snap.MaxStackSize : 1u,
            Rarity: MapItemRarity(snap.Rarity),
            IsIdentified: snap.Identified != 0,
            IsCorrupted: snap.Corrupted != 0);
    }

    /// <inheritdoc/>
    public IDisposable RegisterCharacterCreatingCallback(Func<string, string, string, string?> onCreating)
    {
        ArgumentNullException.ThrowIfNull(onCreating);
        lock (_phase1Gate)
        {
            _charCreatingCallbacks.Add(onCreating);
            EnsurePreGameCallbacksHookedLocked();
        }

        return new ActionDisposable(() =>
        {
            lock (_phase1Gate)
            {
                _charCreatingCallbacks.Remove(onCreating);
                ReleasePreGameCallbacksIfUnusedLocked();
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable RegisterCharacterCreatedCallback(Action<string, string, ushort> onCreated)
    {
        ArgumentNullException.ThrowIfNull(onCreated);
        lock (_phase1Gate)
        {
            _charCreatedCallbacks.Add(onCreated);
            EnsurePreGameCallbacksHookedLocked();
        }

        return new ActionDisposable(() =>
        {
            lock (_phase1Gate)
            {
                _charCreatedCallbacks.Remove(onCreated);
                ReleasePreGameCallbacksIfUnusedLocked();
            }
        });
    }

    private void* EnsureSelfCallbackUserDataLocked()
    {
        if (!_selfCallbackHandle.IsAllocated)
        {
            _selfCallbackHandle = GCHandle.Alloc(this);
        }

        return (void*)GCHandle.ToIntPtr(_selfCallbackHandle);
    }

    private void EnsureInputCallbackHookedLocked()
    {
        if (!_inputNativeHooked && _table->InputSetCallback is not null)
        {
            void* ud = EnsureSelfCallbackUserDataLocked();
            if (_table->InputSetCallback(&OnNativeInput, ud) == NmStatus.Ok)
            {
                _inputNativeHooked = true;
            }
        }
    }

    private void ReleaseInputCallbackIfUnusedLocked()
    {
        if (_keyCallbacks.Count == 0 && _mouseCallbacks.Count == 0 && _inputNativeHooked && _table->InputSetCallback is not null)
        {
            _table->InputSetCallback(null, null);
            _inputNativeHooked = false;
        }
    }

    private void EnsurePreGameCallbacksHookedLocked()
    {
        if (!_preGameNativeHooked && _table->PreGameSetCallbacks is not null)
        {
            void* ud = EnsureSelfCallbackUserDataLocked();
            if (_table->PreGameSetCallbacks(&OnNativePreGameCreating, &OnNativePreGameCreated, ud) == NmStatus.Ok)
            {
                _preGameNativeHooked = true;
            }
        }
    }

    private void ReleasePreGameCallbacksIfUnusedLocked()
    {
        if (_charCreatingCallbacks.Count == 0 && _charCreatedCallbacks.Count == 0 && _preGameNativeHooked && _table->PreGameSetCallbacks is not null)
        {
            _table->PreGameSetCallbacks(null, null, null);
            _preGameNativeHooked = false;
        }
    }

    private static ItemRarity MapItemRarity(uint raw) => raw switch
    {
        0u => ItemRarity.Normal,
        1u => ItemRarity.Magic,
        2u => ItemRarity.Rare,
        3u => ItemRarity.Unique,
        _ => ItemRarity.Unknown,
    };

    private List<LeagueCardInfo> CollectActiveLeagueCards(nint overlayHandle)
    {
        var cards = new List<LeagueCardInfo>();
        if (!TryGetNativeScreensState(out NmScreensState state) || state.LeagueCardsFlow is null)
        {
            return cards;
        }

        nint flowHandle = (nint)state.LeagueCardsFlow;
        IReadOnlyList<nint> flowChildren = GetNativeUiChildren(flowHandle);
        ResolveSymbol("ui.vtable_stack_layout", out _, out nint stackLayoutVt);
        ulong stackLayoutVtU64 = unchecked((ulong)stackLayoutVt);

        int cardIndex = 0;
        foreach (nint child in flowChildren)
        {
            if (GetNativeUiTag(child) != NativeShimConstants.TagLeagueCard)
            {
                continue;
            }

            IReadOnlyList<nint> cardChildren = GetNativeUiChildren(child);
            nint modeStack = nint.Zero;
            float descBottomY = 580f;
            float modeStackY = 628f;
            int stockCount = 0;
            float maxStockW = 0f;

            if (cardChildren.Count >= 4 && TryGetNativeUiRect(cardChildren[3], out UiRect descRect))
            {
                descBottomY = descRect.Y + descRect.Height;
            }

            foreach (nint cardChild in cardChildren)
            {
                if (stackLayoutVtU64 != 0 && GetNativeUiVtable(cardChild) == stackLayoutVtU64)
                {
                    modeStack = cardChild;
                    break;
                }
            }

            if (modeStack == nint.Zero && cardChildren.Count >= 5)
            {
                modeStack = cardChildren[4];
            }

            if (modeStack != nint.Zero)
            {
                if (TryGetNativeUiRect(modeStack, out UiRect stackRect) && stackRect.Y > 0f)
                {
                    modeStackY = stackRect.Y;
                }

                IReadOnlyList<nint> stockCheckBoxes = GetNativeUiChildren(modeStack);
                stockCount = stockCheckBoxes.Count;
                foreach (nint cb in stockCheckBoxes)
                {
                    if (TryGetNativeUiRect(cb, out UiRect cbRect) && cbRect.Width > maxStockW)
                    {
                        maxStockW = cbRect.Width;
                    }
                }
            }

            string leagueId = GetNativeUiId(child) is { Length: > 0 } id ? id : $"league_{cardIndex}";
            cards.Add(new LeagueCardInfo(
                CardIndex: cardIndex,
                LeagueId: leagueId,
                CardHandle: child,
                ModeStackHandle: modeStack,
                StockCheckboxCount: stockCount,
                DescBottomY: descBottomY,
                ModeStackY: modeStackY,
                MaxStockCheckboxWidth: maxStockW > 0f ? maxStockW : 220f));
            cardIndex++;
        }

        return cards;
    }

    private IDisposable SubscribeNativeUiEvent(
        CallbackKind kind,
        nint control,
        Action callback,
        delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, void*, void>, void*, ulong*, NmStatus> subscribeFn)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (subscribeFn is null || control == nint.Zero)
        {
            return NoopDisposable.Instance;
        }

        UiEventSlot slot = _uiEventSlots.GetOrAdd((kind, control), k => new UiEventSlot(this, k.Kind));
        lock (slot)
        {
            Volatile.Write(ref slot.Handler, callback);
            if (!slot.NativeSubscribed)
            {
                GCHandle handle = GCHandle.Alloc(slot);
                ulong token = 0;
                NmStatus st = subscribeFn((void*)control, &OnNativeUiEvent, (void*)GCHandle.ToIntPtr(handle), &token);
                if (st != NmStatus.Ok)
                {
                    handle.Free();
                    Interlocked.CompareExchange(ref slot.Handler, null, callback);
                    return NoopDisposable.Instance;
                }

                slot.NativeSubscribed = true;
                slot.ActiveToken = token;
                _pinnedCallbacks[(kind, token)] = handle;
            }
        }

        return new ActionDisposable(() =>
        {
            lock (slot)
            {
                Interlocked.CompareExchange(ref slot.Handler, null, callback);
                if (Volatile.Read(ref slot.Handler) is null && slot.ActiveToken != 0)
                {
                    ulong token = slot.ActiveToken;
                    slot.ActiveToken = 0;
                    slot.NativeSubscribed = false;
                    if (_table->UiUnsubscribe is not null)
                    {
                        _table->UiUnsubscribe(token);
                    }

                    if (_pinnedCallbacks.TryRemove((kind, token), out GCHandle h) && h.IsAllocated)
                    {
                        h.Free();
                    }
                }
            }
        });
    }

    private void CleanupUiSlotsForControl(nint control)
    {
        foreach (CallbackKind kind in new[] { CallbackKind.UiClick, CallbackKind.TextBoxChanged, CallbackKind.TextBoxSubmitted, CallbackKind.CheckBoxChanged })
        {
            if (_uiEventSlots.TryRemove((kind, control), out UiEventSlot? slot))
            {
                lock (slot)
                {
                    Volatile.Write(ref slot.Handler, null);
                    if (slot.ActiveToken != 0)
                    {
                        ulong token = slot.ActiveToken;
                        slot.ActiveToken = 0;
                        slot.NativeSubscribed = false;
                        if (_table->UiUnsubscribe is not null)
                        {
                            _table->UiUnsubscribe(token);
                        }

                        if (_pinnedCallbacks.TryRemove((kind, token), out GCHandle h) && h.IsAllocated)
                        {
                            h.Free();
                        }
                    }
                }
            }
        }
    }

    /// <inheritdoc/>
    public string? GetClipboardText()
    {
        if (_table->ClipboardGetText is null)
        {
            return null;
        }

        nuint length = 0;
        NmStatus st = _table->ClipboardGetText(null, 0, &length);
        if (st != NmStatus.Ok)
        {
            return null;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            int cap = checked((int)length + 1);
            byte[] buf = new byte[cap];
            fixed (byte* pBuf = buf)
            {
                nuint actual = 0;
                st = _table->ClipboardGetText(pBuf, (nuint)cap, &actual);
                if (st == NmStatus.Ok)
                {
                    return Encoding.UTF8.GetString(buf, 0, checked((int)actual));
                }

                if (st != NmStatus.ErrBufferTooSmall || actual == 0)
                {
                    return null;
                }

                length = actual;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public void SetClipboardText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_table->ClipboardSetText is null)
        {
            return;
        }

        byte[] utf8 = ToNullTerminatedUtf8(text);
        fixed (byte* pText = utf8)
        {
            NmStatus status = _table->ClipboardSetText(pText);
            EnsureOk(status, "ClipboardSetText");
        }
    }

    /// <inheritdoc/>
    public int ReadStat(string statKey, int defaultValue) =>
        _localStats.TryGetValue(statKey, out int val) ? val : defaultValue;

    /// <inheritdoc/>
    public void WriteLocalStat(string statKey, int value) => _localStats[statKey] = value;

    /// <inheritdoc/>
    public IReadOnlyCollection<WorldObjectInfo> GetWorldObjects() => _clientObjects.Values.ToArray();

    /// <inheritdoc/>
    public uint SpawnClientObject(string metadataPath, float x, float y, float z)
    {
        if (_table->WorldCreateClientObject is not null)
        {
            byte[] utf8 = ToNullTerminatedUtf8(metadataPath);
            ulong nativeHandle = 0;
            fixed (byte* pMeta = utf8)
            {
                NmStatus status = _table->WorldCreateClientObject(pMeta, x, y, z, &nativeHandle);
                EnsureOk(status, $"WorldCreateClientObject({metadataPath})");
            }

            uint nativeId = unchecked((uint)nativeHandle);
            if (nativeId == 0 || _nativeClientObjectHandles.ContainsKey(nativeId))
            {
                nativeId = Interlocked.Increment(ref _nextClientObjectId);
            }

            _nativeClientObjectHandles[nativeId] = nativeHandle;
            _clientObjects[nativeId] = new WorldObjectInfo(nativeId, metadataPath, x, y, z, true, new Dictionary<string, string>());
            return nativeId;
        }

        uint id = Interlocked.Increment(ref _nextClientObjectId);
        _clientObjects[id] = new WorldObjectInfo(id, metadataPath, x, y, z, true, new Dictionary<string, string>());
        return id;
    }

    /// <inheritdoc/>
    public bool RemoveClientObject(uint objectId)
    {
        if (_nativeClientObjectHandles.TryRemove(objectId, out ulong nativeHandle) && _table->WorldRemoveClientObject is not null)
        {
            _clientObjects.TryRemove(objectId, out _);
            return _table->WorldRemoveClientObject(nativeHandle) == NmStatus.Ok;
        }

        return _clientObjects.TryRemove(objectId, out _);
    }

    /// <inheritdoc/>
    public void PlayEffect(uint targetObjectId, string effectPath) { }

    // --- Phase 2 (ABI 0.4.0, Slots 89..106) ---

    /// <inheritdoc/>
    public WorldEntitySnapshot? GetLocalPlayerEntity()
    {
        if (_table->WorldGetLocalPlayer is null)
        {
            return null;
        }

        NmWorldEntitySnapshot snap = default;
        snap.StructSize = (uint)sizeof(NmWorldEntitySnapshot);
        if (_table->WorldGetLocalPlayer(&snap) != NmStatus.Ok || snap.EntityId == 0)
        {
            return null;
        }

        return MapWorldEntitySnapshot(in snap);
    }

    /// <inheritdoc/>
    public IReadOnlyList<WorldEntitySnapshot> GetWorldEntities(WorldEntityQuery? query = null)
    {
        if (_table->WorldListEntities is null)
        {
            return [];
        }

        NmWorldEntityQuery nativeQuery = default;
        NmWorldEntityQuery* queryPtr = null;
        if (query is not null)
        {
            nativeQuery = BuildNativeWorldQuery(query);
            queryPtr = &nativeQuery;
        }

        int capacity = query?.MaxResults is int maxReq and > 0
            ? Math.Min(maxReq, 65536)
            : 256;

        NmWorldEntitySnapshot[] buffer = new NmWorldEntitySnapshot[capacity];
        uint count = 0;
        uint total = 0;
        NmStatus status;
        fixed (NmWorldEntitySnapshot* pBuf = buffer)
        {
            status = _table->WorldListEntities(queryPtr, pBuf, (uint)buffer.Length, &count, &total);
        }

        if ((status == NmStatus.ErrBufferTooSmall || (status == NmStatus.Ok && total > count && total > (uint)buffer.Length)) &&
            total > 0 &&
            (query?.MaxResults is null || count < (uint)Math.Min(query.MaxResults.Value, 65536)))
        {
            int grownCap = (int)Math.Min(total, (uint)(query?.MaxResults is int mr and > 0 ? Math.Min(mr, 65536) : 65536));
            if (grownCap > buffer.Length)
            {
                buffer = new NmWorldEntitySnapshot[grownCap];
                count = 0;
                fixed (NmWorldEntitySnapshot* pBuf = buffer)
                {
                    status = _table->WorldListEntities(queryPtr, pBuf, (uint)buffer.Length, &count, &total);
                }
            }
        }

        if (status != NmStatus.Ok || count == 0)
        {
            return [];
        }

        int finalCount = (int)Math.Min(count, (uint)buffer.Length);
        var list = new List<WorldEntitySnapshot>(finalCount);
        for (int i = 0; i < finalCount; i++)
        {
            list.Add(MapWorldEntitySnapshot(in buffer[i]));
        }

        return list;
    }

    /// <inheritdoc/>
    public WorldEntitySnapshot? GetWorldEntity(uint entityId)
    {
        if (_table->WorldGetEntity is null)
        {
            return null;
        }

        NmWorldEntitySnapshot snap = default;
        snap.StructSize = (uint)sizeof(NmWorldEntitySnapshot);
        if (_table->WorldGetEntity(entityId, &snap) != NmStatus.Ok || snap.EntityId == 0)
        {
            return null;
        }

        return MapWorldEntitySnapshot(in snap);
    }

    /// <inheritdoc/>
    public bool TryReadEntityStat(uint entityId, int statId, out int value)
    {
        if (_table->StatsReadEntity is null)
        {
            value = 0;
            return false;
        }

        int v = 0;
        if (_table->StatsReadEntity(entityId, statId, &v) == NmStatus.Ok)
        {
            value = v;
            return true;
        }

        value = 0;
        return false;
    }

    /// <inheritdoc/>
    public void SetLocalEntityStat(uint entityId, int statId, int value)
    {
        if (_table->StatsSetLocalEntity is not null)
        {
            _table->StatsSetLocalEntity(entityId, statId, value);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<int, int> GetAllEntityStats(uint entityId)
    {
        var result = new Dictionary<int, int>();
        if (_table->StatsListEntity is null)
        {
            return result;
        }

        NmStatEntry[] buffer = new NmStatEntry[256];
        uint count = 0;
        uint total = 0;
        NmStatus status;
        fixed (NmStatEntry* pBuf = buffer)
        {
            status = _table->StatsListEntity(entityId, pBuf, (uint)buffer.Length, &count, &total);
        }

        if ((status == NmStatus.ErrBufferTooSmall || (status == NmStatus.Ok && total > count && total > (uint)buffer.Length)) && total > 0)
        {
            int grownCap = (int)Math.Min(total, 65536u);
            if (grownCap > buffer.Length)
            {
                buffer = new NmStatEntry[grownCap];
                count = 0;
                fixed (NmStatEntry* pBuf = buffer)
                {
                    status = _table->StatsListEntity(entityId, pBuf, (uint)buffer.Length, &count, &total);
                }
            }
        }

        if (status != NmStatus.Ok || count == 0)
        {
            return result;
        }

        int finalCount = (int)Math.Min(count, (uint)buffer.Length);
        for (int i = 0; i < finalCount; i++)
        {
            result[buffer[i].StatId] = buffer[i].Value;
        }

        return result;
    }

    /// <inheritdoc/>
    public AreaInfo? GetAreaInfo()
    {
        if (_table->LifecycleGetAreaInfo is null)
        {
            return null;
        }

        NmAreaInfo info = default;
        info.StructSize = (uint)sizeof(NmAreaInfo);
        if (_table->LifecycleGetAreaInfo(&info) != NmStatus.Ok || info.InGame == 0)
        {
            return null;
        }

        return MapAreaInfo(in info);
    }

    /// <inheritdoc/>
    public IDisposable RegisterAreaLifecycleCallback(Action<AreaInfo> onAreaEntered, Action<string> onAreaLeft)
    {
        ArgumentNullException.ThrowIfNull(onAreaEntered);
        ArgumentNullException.ThrowIfNull(onAreaLeft);
        var entry = (onAreaEntered, onAreaLeft);
        lock (_phase1Gate)
        {
            _areaLifecycleCallbacks.Add(entry);
            if (!_areaNativeHooked && _table->LifecycleSetAreaCallback is not null)
            {
                void* ud = EnsureSelfCallbackUserDataLocked();
                if (_table->LifecycleSetAreaCallback(&OnNativeAreaEvent, ud) == NmStatus.Ok)
                {
                    _areaNativeHooked = true;
                }
            }
        }

        return new ActionDisposable(() =>
        {
            lock (_phase1Gate)
            {
                _areaLifecycleCallbacks.Remove(entry);
                if (_areaLifecycleCallbacks.Count == 0 && _areaNativeHooked && _table->LifecycleSetAreaCallback is not null)
                {
                    _table->LifecycleSetAreaCallback(null, null);
                    _areaNativeHooked = false;
                }
            }
        });
    }

    /// <inheritdoc/>
    public ItemFilterState GetItemFilterState()
    {
        if (_table->FilterGetState is null)
        {
            return ItemFilterState.Empty;
        }

        NmItemFilterState state = default;
        state.StructSize = (uint)sizeof(NmItemFilterState);
        if (_table->FilterGetState(&state) != NmStatus.Ok)
        {
            return ItemFilterState.Empty;
        }

        return MapItemFilterState(in state);
    }

    /// <inheritdoc/>
    public bool ReloadItemFilter() =>
        _table->FilterReload is not null && _table->FilterReload() == NmStatus.Ok;

    /// <inheritdoc/>
    public ItemFilterResult GetWorldItemFilterResult(uint entityId)
    {
        if (_table->FilterGetWorldItemResult is null)
        {
            return ItemFilterResult.Unevaluated;
        }

        NmItemFilterResult res = default;
        res.StructSize = (uint)sizeof(NmItemFilterResult);
        if (_table->FilterGetWorldItemResult(entityId, &res) != NmStatus.Ok)
        {
            return ItemFilterResult.Unevaluated;
        }

        return MapItemFilterResult(in res);
    }

    /// <inheritdoc/>
    public ItemFilterResult EvaluateItemFilter(uint entityId)
    {
        if (_table->FilterEvaluateEntity is null)
        {
            return ItemFilterResult.Unevaluated;
        }

        NmItemFilterResult res = default;
        res.StructSize = (uint)sizeof(NmItemFilterResult);
        if (_table->FilterEvaluateEntity(entityId, &res) != NmStatus.Ok)
        {
            return ItemFilterResult.Unevaluated;
        }

        return MapItemFilterResult(in res);
    }

    /// <inheritdoc/>
    public IDisposable RegisterItemFilterReloadCallback(Action<bool, string, ItemFilterState?> onReloaded)
    {
        ArgumentNullException.ThrowIfNull(onReloaded);
        lock (_phase1Gate)
        {
            _filterReloadCallbacks.Add(onReloaded);
            if (!_filterNativeHooked && _table->FilterSetReloadCallback is not null)
            {
                void* ud = EnsureSelfCallbackUserDataLocked();
                if (_table->FilterSetReloadCallback(&OnNativeFilterReloaded, ud) == NmStatus.Ok)
                {
                    _filterNativeHooked = true;
                }
            }
        }

        return new ActionDisposable(() =>
        {
            lock (_phase1Gate)
            {
                _filterReloadCallbacks.Remove(onReloaded);
                if (_filterReloadCallbacks.Count == 0 && _filterNativeHooked && _table->FilterSetReloadCallback is not null)
                {
                    _table->FilterSetReloadCallback(null, null);
                    _filterNativeHooked = false;
                }
            }
        });
    }

    /// <inheritdoc/>
    public CameraState GetCameraState()
    {
        if (_table->CameraGetState is null)
        {
            return CameraState.Invalid;
        }

        NmCameraState cam = default;
        cam.StructSize = (uint)sizeof(NmCameraState);
        if (_table->CameraGetState(&cam) != NmStatus.Ok || cam.Valid == 0)
        {
            return CameraState.Invalid;
        }

        float[] matrix = new float[16];
        for (int i = 0; i < 16; i++)
        {
            matrix[i] = cam.ViewProj[i];
        }

        return new CameraState(
            IsValid: true,
            Position: new WorldPosition3D(cam.PosX, cam.PosY, cam.PosZ),
            Target: new WorldPosition3D(cam.TargetX, cam.TargetY, cam.TargetZ),
            Zoom: cam.Zoom,
            ZoomOverride: cam.ZoomOverride,
            ZoomTarget: cam.ZoomTarget,
            Fov: cam.Fov,
            ViewportWidth: cam.ViewportW,
            ViewportHeight: cam.ViewportH,
            ViewProjectionMatrix: matrix);
    }

    /// <inheritdoc/>
    public bool TryCameraWorldToScreen(float worldX, float worldY, float worldZ, out float screenX, out float screenY)
    {
        if (_table->CameraWorldToScreen is not null)
        {
            float sx = 0f;
            float sy = 0f;
            int inFront = 0;
            if (_table->CameraWorldToScreen(worldX, worldY, worldZ, &sx, &sy, &inFront) == NmStatus.Ok && inFront != 0)
            {
                screenX = sx;
                screenY = sy;
                return true;
            }

            screenX = 0f;
            screenY = 0f;
            return false;
        }

        return GetCameraState().TryProjectWorldToScreen(worldX, worldY, worldZ, out screenX, out screenY);
    }

    /// <inheritdoc/>
    public void SetCameraZoomOverride(float zoomOverride)
    {
        if (_table->CameraSetZoomOverride is null)
        {
            return;
        }

        float normalized = float.IsNaN(zoomOverride) || float.IsInfinity(zoomOverride) || zoomOverride < 0f
            ? -1.0f
            : zoomOverride;
        _table->CameraSetZoomOverride(normalized);
    }

    // ── Phase 4 (ABI 0.5.0): Rich Dynamic UI Controls ──────────────────────────────────────────

    /// <inheritdoc/>
    public nint CreateNativeUiControl(nint parentControl, UiControlKind kind, string? id = null)
    {
        if (parentControl == nint.Zero || _table->UiCreateControl is null)
        {
            return nint.Zero;
        }

        byte[]? idUtf8 = string.IsNullOrWhiteSpace(id) ? null : ToNullTerminatedUtf8(id.Trim());
        void* outCtrl = null;
        fixed (byte* pId = idUtf8)
        {
            if (_table->UiCreateControl((int)kind, (void*)parentControl, pId, &outCtrl) == NmStatus.Ok)
            {
                return (nint)outCtrl;
            }
        }

        return nint.Zero;
    }

    /// <inheritdoc/>
    public void SetNativeUiPictureTexture(nint pictureControl, string texturePath, bool resizeToImage = false)
    {
        if (pictureControl == nint.Zero || string.IsNullOrWhiteSpace(texturePath) || _table->UiPictureSetTexture is null)
        {
            return;
        }

        byte[] utf8 = ToNullTerminatedUtf8(texturePath.Trim());
        fixed (byte* pPath = utf8)
        {
            _table->UiPictureSetTexture((void*)pictureControl, pPath);
        }
    }

    /// <inheritdoc/>
    public bool TryGetNativeUiScrollState(nint scrollableControl, out float offset, out float maxOffset)
    {
        offset = 0f;
        maxOffset = 0f;
        if (scrollableControl == nint.Zero || _table->UiScrollableGetScroll is null)
        {
            return false;
        }

        float off = 0f;
        float max = 0f;
        if (_table->UiScrollableGetScroll((void*)scrollableControl, &off, &max) != NmStatus.Ok)
        {
            return false;
        }

        offset = off;
        maxOffset = max;
        return true;
    }

    /// <inheritdoc/>
    public void SetNativeUiScrollOffset(nint scrollableControl, float offset)
    {
        if (scrollableControl == nint.Zero || _table->UiScrollableSetScroll is null)
        {
            return;
        }

        _table->UiScrollableSetScroll((void*)scrollableControl, offset);
    }

    /// <inheritdoc/>
    public nint AddNativeUiTab(nint tabContainerControl, string title, string? iconPath = null, bool hidden = false)
    {
        if (tabContainerControl == nint.Zero || string.IsNullOrWhiteSpace(title) || _table->UiTabContainerAddTab is null)
        {
            return nint.Zero;
        }

        byte[] utf8 = ToNullTerminatedUtf8(title);
        void* outPage = null;
        fixed (byte* pLabel = utf8)
        {
            if (_table->UiTabContainerAddTab((void*)tabContainerControl, pLabel, 0u, hidden ? 1 : 0, &outPage) == NmStatus.Ok)
            {
                return (nint)outPage;
            }
        }

        return nint.Zero;
    }

    /// <inheritdoc/>
    public void SelectNativeUiTab(nint tabContainerControl, int index)
    {
        if (tabContainerControl == nint.Zero || _table->UiTabContainerSelect is null)
        {
            return;
        }

        _table->UiTabContainerSelect((void*)tabContainerControl, index);
    }

    /// <inheritdoc/>
    public bool TryGetNativeUiTabState(nint tabContainerControl, out int count, out int activeIndex)
    {
        count = 0;
        activeIndex = -1;
        if (tabContainerControl == nint.Zero || _table->UiTabContainerGetCount is null || _table->UiTabContainerGetActive is null)
        {
            return false;
        }

        uint c = 0;
        int active = -1;
        if (_table->UiTabContainerGetCount((void*)tabContainerControl, &c) != NmStatus.Ok ||
            _table->UiTabContainerGetActive((void*)tabContainerControl, &active) != NmStatus.Ok)
        {
            return false;
        }

        count = checked((int)c);
        activeIndex = active;
        return true;
    }

    /// <inheritdoc/>
    public IDisposable SubscribeNativeUiTabChanged(nint tabContainerControl, Action<int> onChanged) =>
        SubscribeNativeUiIntEvent(CallbackKind.UiTabChanged, tabContainerControl, _table->UiTabContainerSubscribeChanged, onChanged);

    /// <inheritdoc/>
    public int AddNativeUiComboBoxItem(nint comboBoxControl, string text, bool enabled = true, uint tag = 0)
    {
        if (comboBoxControl == nint.Zero || _table->UiComboBoxAddItem is null)
        {
            return -1;
        }

        byte[] utf8 = ToNullTerminatedUtf8(text ?? string.Empty);
        uint outIdx = 0;
        fixed (byte* pText = utf8)
        {
            if (_table->UiComboBoxAddItem((void*)comboBoxControl, pText, enabled ? 1 : 0, tag, &outIdx) == NmStatus.Ok)
            {
                return checked((int)outIdx);
            }
        }

        return -1;
    }

    /// <inheritdoc/>
    public void ClearNativeUiComboBoxItems(nint comboBoxControl)
    {
        if (comboBoxControl != nint.Zero && _table->UiComboBoxClear is not null)
        {
            _table->UiComboBoxClear((void*)comboBoxControl);
        }
    }

    /// <inheritdoc/>
    public bool SetNativeUiComboBoxSelectedIndex(nint comboBoxControl, int index) =>
        comboBoxControl != nint.Zero && _table->UiComboBoxSetSelected is not null &&
        _table->UiComboBoxSetSelected((void*)comboBoxControl, index) == NmStatus.Ok;

    /// <inheritdoc/>
    public bool TryGetNativeUiComboBoxState(nint comboBoxControl, out int count, out int selectedIndex, out string? selectedText)
    {
        count = 0;
        selectedIndex = -1;
        selectedText = null;
        if (comboBoxControl == nint.Zero || _table->UiComboBoxGetCount is null || _table->UiComboBoxGetSelected is null)
        {
            return false;
        }

        uint c = 0;
        int sel = -1;
        if (_table->UiComboBoxGetCount((void*)comboBoxControl, &c) != NmStatus.Ok ||
            _table->UiComboBoxGetSelected((void*)comboBoxControl, &sel) != NmStatus.Ok)
        {
            return false;
        }

        count = checked((int)c);
        selectedIndex = sel;
        return true;
    }

    /// <inheritdoc/>
    public IDisposable SubscribeNativeUiComboBoxSelectionChanged(nint comboBoxControl, Action<int> onChanged) =>
        SubscribeNativeUiIntEvent(CallbackKind.UiComboBoxChanged, comboBoxControl, _table->UiComboBoxSubscribeChanged, onChanged);

    /// <inheritdoc/>
    public bool InsertNativeUiListBoxItem(nint listBoxControl, int index, nint childControl, ulong userData = 0)
    {
        if (listBoxControl == nint.Zero || childControl == nint.Zero || index < 0 || _table->UiListBoxInsertItem is null)
        {
            return false;
        }

        return _table->UiListBoxInsertItem((void*)listBoxControl, (uint)index, (void*)childControl, userData) == NmStatus.Ok;
    }

    /// <inheritdoc/>
    public void ClearNativeUiListBoxItems(nint listBoxControl)
    {
        if (listBoxControl != nint.Zero && _table->UiListBoxClear is not null)
        {
            _table->UiListBoxClear((void*)listBoxControl);
        }
    }

    /// <inheritdoc/>
    public bool SetNativeUiListBoxSelectedIndex(nint listBoxControl, int index) =>
        listBoxControl != nint.Zero && _table->UiListBoxSetSelected is not null &&
        _table->UiListBoxSetSelected((void*)listBoxControl, index) == NmStatus.Ok;

    /// <inheritdoc/>
    public void ClearNativeUiListBoxSelection(nint listBoxControl)
    {
        if (listBoxControl != nint.Zero && _table->UiListBoxSetSelected is not null)
        {
            _table->UiListBoxSetSelected((void*)listBoxControl, -1);
        }
    }

    /// <inheritdoc/>
    public bool TryGetNativeUiListBoxState(nint listBoxControl, out int count, out int selectedIndex)
    {
        count = 0;
        selectedIndex = -1;
        if (listBoxControl == nint.Zero || _table->UiListBoxGetCount is null || _table->UiListBoxGetSelected is null)
        {
            return false;
        }

        uint c = 0;
        int sel = -1;
        if (_table->UiListBoxGetCount((void*)listBoxControl, &c) != NmStatus.Ok ||
            _table->UiListBoxGetSelected((void*)listBoxControl, &sel) != NmStatus.Ok)
        {
            return false;
        }

        count = checked((int)c);
        selectedIndex = sel;
        return true;
    }

    /// <inheritdoc/>
    public IDisposable SubscribeNativeUiListBoxSelectionChanged(nint listBoxControl, Action<int> onChanged) =>
        SubscribeNativeUiIntEvent(CallbackKind.UiListBoxChanged, listBoxControl, _table->UiListBoxSubscribeChanged, onChanged);

    private IDisposable SubscribeNativeUiIntEvent(
        CallbackKind kind,
        nint control,
        delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, int, void*, void>, void*, ulong*, NmStatus> subscribeFn,
        Action<int> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        if (control == nint.Zero || subscribeFn is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle handle = GCHandle.Alloc(new UiIntEventWrapper(this, onChanged));
        ulong token = 0;
        NmStatus status = subscribeFn((void*)control, &OnNativeUiIntEvent, (void*)GCHandle.ToIntPtr(handle), &token);
        if (status != NmStatus.Ok)
        {
            handle.Free();
            return NoopDisposable.Instance;
        }

        var key = (kind, token);
        _pinnedCallbacks[key] = handle;
        ulong capturedToken = token;
        return new ActionDisposable(() =>
        {
            if (_table->UiUnsubscribe is not null)
            {
                _table->UiUnsubscribe(capturedToken);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    // ── Phase 4 (ABI 0.5.0): Stock Item Tooltip Augmentation ───────────────────────────────────

    /// <inheritdoc/>
    public IDisposable RegisterItemTooltipBuildCallback(Func<ItemTooltipContext, IReadOnlyList<TooltipSection>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_phase1Gate)
        {
            _tooltipAugmentCallbacks.Add(callback);
            if (!_tooltipNativeHooked && _table->TooltipSetItemAugmentCallback is not null)
            {
                void* ud = EnsureSelfCallbackUserDataLocked();
                if (_table->TooltipSetItemAugmentCallback(&OnNativeTooltipAugment, ud) == NmStatus.Ok)
                {
                    _tooltipNativeHooked = true;
                }
            }
        }

        return new ActionDisposable(() =>
        {
            lock (_phase1Gate)
            {
                _tooltipAugmentCallbacks.Remove(callback);
                if (_tooltipAugmentCallbacks.Count == 0 && _tooltipNativeHooked && _table->TooltipSetItemAugmentCallback is not null)
                {
                    _table->TooltipSetItemAugmentCallback(null, null);
                    _tooltipNativeHooked = false;
                }
            }
        });
    }

    // ── Phase 4 (ABI 0.5.0): Stash Affinity Routing & Bitset ───────────────────────────────────

    /// <inheritdoc/>
    public IDisposable RegisterStashAffinityRouteCallback(Func<StashAffinityItemContext, IReadOnlyList<StashAffinityRoute>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_phase1Gate)
        {
            _affinityRouteCallbacks.Add(callback);
            EnsureAffinityCallbackHookedLocked();
        }

        return new ActionDisposable(() =>
        {
            lock (_phase1Gate)
            {
                _affinityRouteCallbacks.Remove(callback);
                ReleaseAffinityCallbackIfUnusedLocked();
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable RegisterStashAffinityBitsetCallback(Func<StashAffinityItemContext, uint> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_phase1Gate)
        {
            _affinityBitsetCallbacks.Add(callback);
            EnsureAffinityCallbackHookedLocked();
        }

        return new ActionDisposable(() =>
        {
            lock (_phase1Gate)
            {
                _affinityBitsetCallbacks.Remove(callback);
                ReleaseAffinityCallbackIfUnusedLocked();
            }
        });
    }

    private void EnsureAffinityCallbackHookedLocked()
    {
        if (!_affinityNativeHooked && _table->AffinitySetRouterCallback is not null)
        {
            void* ud = EnsureSelfCallbackUserDataLocked();
            if (_table->AffinitySetRouterCallback(&OnNativeAffinityRoute, ud) == NmStatus.Ok)
            {
                _affinityNativeHooked = true;
            }
        }
    }

    private void ReleaseAffinityCallbackIfUnusedLocked()
    {
        if (_affinityRouteCallbacks.Count == 0 && _affinityBitsetCallbacks.Count == 0 &&
            _affinityNativeHooked && _table->AffinitySetRouterCallback is not null)
        {
            _table->AffinitySetRouterCallback(null, null);
            _affinityNativeHooked = false;
        }
    }

    // ── Phase 4 (ABI 0.5.0): Live Native Extend Registries ─────────────────────────────────────

    /// <inheritdoc/>
    public IDisposable RegisterNativeGealBuiltin(string name, bool hasReturnValue, Func<uint, GealArguments, GealValue> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (_table->GealRegisterBuiltin is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle gcHandle = GCHandle.Alloc(new GealBuiltinWrapper(this, hasReturnValue, handler));
        byte[] nameUtf8 = ToNullTerminatedUtf8(name.Trim());
        ulong nativeHandle = 0;
        NmStatus status;
        fixed (byte* pName = nameUtf8)
        {
            status = _table->GealRegisterBuiltin(
                pName,
                0u,
                8u,
                hasReturnValue ? 1 : 0,
                &OnNativeGealBuiltin,
                (void*)GCHandle.ToIntPtr(gcHandle),
                &nativeHandle);
        }

        if (status != NmStatus.Ok)
        {
            gcHandle.Free();
            EnsureOk(status, $"GealRegisterBuiltin({name})");
        }

        var key = (CallbackKind.GealBuiltin, nativeHandle);
        _pinnedCallbacks[key] = gcHandle;
        ulong capturedHandle = nativeHandle;
        return new ActionDisposable(() =>
        {
            if (_table->GealUnregisterBuiltin is not null)
            {
                _table->GealUnregisterBuiltin(capturedHandle);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    /// <inheritdoc/>
    public bool DrivesNativeRulesetLifecycle => _table->RulesetRegisterNative is not null;

    /// <inheritdoc/>
    public IDisposable RegisterNativeRuleset(
        byte rulesetId,
        Action<AreaEventArgs> onAreaEntered,
        Action<ReadOnlyMemory<byte>> onRulesetUpdate,
        Action<float> onFrame,
        Action onAreaLeft)
    {
        ArgumentNullException.ThrowIfNull(onAreaEntered);
        ArgumentNullException.ThrowIfNull(onRulesetUpdate);
        ArgumentNullException.ThrowIfNull(onFrame);
        ArgumentNullException.ThrowIfNull(onAreaLeft);

        if (_table->RulesetRegisterNative is null)
        {
            return RegisterRulesetReceiver(rulesetId, onRulesetUpdate);
        }

        GCHandle gcHandle = GCHandle.Alloc(new NativeRulesetWrapper(this, onAreaEntered, onRulesetUpdate, onFrame, onAreaLeft));
        NmNativeRulesetCallbacks cbs = default;
        cbs.StructSize = (uint)sizeof(NmNativeRulesetCallbacks);
        cbs.OnAreaEntered = &OnNativeRulesetAreaEntered;
        cbs.OnFrame = &OnNativeRulesetFrame;
        cbs.OnRulesetUpdate = &OnNativeRulesetUpdate;
        cbs.OnAreaLeft = &OnNativeRulesetAreaLeft;

        ulong nativeHandle = 0;
        NmStatus status = _table->RulesetRegisterNative(rulesetId, &cbs, (void*)GCHandle.ToIntPtr(gcHandle), &nativeHandle);
        if (status != NmStatus.Ok)
        {
            gcHandle.Free();
            EnsureOk(status, $"RulesetRegisterNative(0x{rulesetId:X2})");
        }

        var key = (CallbackKind.NativeRuleset, nativeHandle);
        _pinnedCallbacks[key] = gcHandle;
        ulong capturedHandle = nativeHandle;
        return new ActionDisposable(() =>
        {
            if (_table->RulesetUnregisterNative is not null)
            {
                _table->RulesetUnregisterNative(capturedHandle);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable RegisterNativeCustomUiControlKind(
        string controlTypeName,
        Action<nint> onCreated,
        Action<nint, string, string> onPropertyChanged,
        Action<nint> onDestroyed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlTypeName);
        ArgumentNullException.ThrowIfNull(onCreated);
        ArgumentNullException.ThrowIfNull(onPropertyChanged);
        ArgumentNullException.ThrowIfNull(onDestroyed);

        if (_table->UiRegisterCustomControlKind is null)
        {
            return NoopDisposable.Instance;
        }

        GCHandle gcHandle = GCHandle.Alloc(new CustomUiControlWrapper(this, onCreated, onPropertyChanged, onDestroyed));
        NmCustomUiControlCallbacks cbs = default;
        cbs.StructSize = (uint)sizeof(NmCustomUiControlCallbacks);
        cbs.OnCreated = &OnNativeCustomUiCreated;
        cbs.OnProperty = &OnNativeCustomUiProperty;
        cbs.OnDestroyed = &OnNativeCustomUiDestroyed;

        byte[] kindUtf8 = ToNullTerminatedUtf8(controlTypeName.Trim());
        ulong nativeHandle = 0;
        NmStatus status;
        fixed (byte* pKind = kindUtf8)
        {
            status = _table->UiRegisterCustomControlKind(pKind, &cbs, (void*)GCHandle.ToIntPtr(gcHandle), &nativeHandle);
        }

        if (status != NmStatus.Ok)
        {
            gcHandle.Free();
            EnsureOk(status, $"UiRegisterCustomControlKind({controlTypeName})");
        }

        var key = (CallbackKind.CustomUiControl, nativeHandle);
        _pinnedCallbacks[key] = gcHandle;
        ulong capturedHandle = nativeHandle;
        return new ActionDisposable(() =>
        {
            if (_table->UiUnregisterCustomControlKind is not null)
            {
                _table->UiUnregisterCustomControlKind(capturedHandle);
            }

            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        });
    }

    private static HoveredItemInfo MapItemSnapshotToHoveredItem(in NmItemSnapshot item) =>
        new(
            EntityHandle: (nint)item.ItemEntity,
            ControlHandle: (nint)item.ItemView,
            ItemId: item.ItemId,
            InventoryId: item.InventoryId,
            GridX: item.GridX,
            GridY: item.GridY,
            Width: item.GridW,
            Height: item.GridH,
            MetadataPath: item.GetMetadataPath(),
            BaseName: item.GetBaseName(),
            StackSize: item.StackSize > 0 ? item.StackSize : 1u,
            MaxStackSize: item.MaxStackSize > 0 ? item.MaxStackSize : 1u,
            Rarity: MapItemRarity(item.Rarity),
            IsIdentified: item.Identified != 0,
            IsCorrupted: item.Corrupted != 0,
            ItemLevel: item.ItemLevel,
            ItemClassId: item.ItemClassId);

    private static string SerializeGealArgumentsJson(ReadOnlySpan<GealValue> args)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (GealValue val in args)
            {
                WriteGealValueJson(writer, val);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string SerializeGealValueJson(GealValue val)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            WriteGealValueJson(writer, val);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteGealValueJson(Utf8JsonWriter writer, GealValue val)
    {
        switch (val.Kind)
        {
            case GealValueKind.Integer:
                writer.WriteNumberValue(val.IntValue);
                break;
            case GealValueKind.Float:
                writer.WriteNumberValue(val.FloatValue);
                break;
            case GealValueKind.Bool:
                writer.WriteBooleanValue(val.BoolValue);
                break;
            case GealValueKind.String:
                writer.WriteStringValue(val.StringValue ?? string.Empty);
                break;
            case GealValueKind.ObjectId:
                writer.WriteNumberValue(val.ObjectIdValue);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static GealValue[] DeserializeGealArgumentsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [ParseJsonElementToGealValue(doc.RootElement)];
            }

            var list = new List<GealValue>(doc.RootElement.GetArrayLength());
            foreach (JsonElement elem in doc.RootElement.EnumerateArray())
            {
                list.Add(ParseJsonElementToGealValue(elem));
            }

            return [.. list];
        }
        catch
        {
            return [];
        }
    }

    private static GealValue DeserializeGealValueJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return GealValue.Null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return ParseJsonElementToGealValue(doc.RootElement);
        }
        catch
        {
            return GealValue.FromString(json);
        }
    }

    private static GealValue ParseJsonElementToGealValue(JsonElement elem) => elem.ValueKind switch
    {
        JsonValueKind.Number when elem.TryGetInt64(out long l) => GealValue.FromInt(l),
        JsonValueKind.Number => GealValue.FromFloat(elem.GetSingle()),
        JsonValueKind.True => GealValue.FromBool(true),
        JsonValueKind.False => GealValue.FromBool(false),
        JsonValueKind.String => GealValue.FromString(elem.GetString() ?? string.Empty),
        _ => GealValue.Null,
    };

    private static NmWorldEntityQuery BuildNativeWorldQuery(WorldEntityQuery query)
    {
        NmWorldEntityQuery q = default;
        q.StructSize = (uint)sizeof(NmWorldEntityQuery);
        q.RequiredKinds = (uint)query.RequiredKind & 0x0FFFu;
        q.ExcludedKinds = (uint)query.ExcludedKind & 0x0FFFu;
        q.MaxResults = query.MaxResults is int mr and > 0 ? (uint)mr : 0u;
        if (query.Origin is { } origin)
        {
            q.OriginX = origin.X;
            q.OriginY = origin.Y;
            q.UseOrigin = 1;
        }

        q.MaxDistance = query.MaxDistance is float md and > 0f ? md : 0f;
        q.FilterVisibleOnly = query.FilterVisibleOnly ? (byte)1 : (byte)0;
        q.IncludeWorldItem = 1;
        if (!string.IsNullOrEmpty(query.MetadataPrefix))
        {
            byte[] prefixUtf8 = Encoding.UTF8.GetBytes(query.MetadataPrefix);
            int copyLen = Math.Min(prefixUtf8.Length, 127);
            for (int i = 0; i < copyLen; i++)
            {
                q.MetadataPrefix[i] = prefixUtf8[i];
            }

            q.MetadataPrefix[copyLen] = 0;
        }

        return q;
    }

    private static WorldEntitySnapshot MapWorldEntitySnapshot(in NmWorldEntitySnapshot snap)
    {
        WorldEntityKind kind = (WorldEntityKind)(snap.KindFlags & 0x0FFFu);
        if (snap.IsAlive != 0)
        {
            kind |= WorldEntityKind.Alive;
        }

        if (snap.IsTargetable != 0)
        {
            kind |= WorldEntityKind.Targetable;
        }

        if (snap.IsOpened != 0)
        {
            kind |= WorldEntityKind.Opened;
        }

        if (snap.EntityId >= 0x4000_0000u)
        {
            kind |= WorldEntityKind.ClientOnly;
        }

        HoveredItemInfo? worldItem = null;
        if (snap.WorldItem.Present != 0)
        {
            NmItemSnapshot item = snap.WorldItem;
            worldItem = new HoveredItemInfo(
                EntityHandle: (nint)item.ItemEntity,
                ControlHandle: (nint)item.ItemView,
                ItemId: item.ItemId,
                InventoryId: item.InventoryId,
                GridX: item.GridX,
                GridY: item.GridY,
                Width: item.GridW,
                Height: item.GridH,
                MetadataPath: item.GetMetadataPath(),
                BaseName: item.GetBaseName(),
                StackSize: item.StackSize,
                MaxStackSize: item.MaxStackSize,
                Rarity: MapItemRarity(item.Rarity),
                IsIdentified: item.Identified != 0,
                IsCorrupted: item.Corrupted != 0,
                ItemLevel: item.ItemLevel,
                ItemClassId: item.ItemClassId);
        }

        return new WorldEntitySnapshot(
            EntityId: snap.EntityId,
            EntityHandle: (nint)snap.EntityPtr,
            Kind: kind,
            MetadataPath: snap.GetMetadataPath(),
            GridX: snap.GridX,
            GridY: snap.GridY,
            Position: new WorldPosition3D(snap.WorldX, snap.WorldY, snap.WorldZ),
            Hp: snap.Hp,
            MaxHp: snap.MaxHp,
            Rarity: MapItemRarity(snap.Rarity),
            IsFilterVisible: snap.FilterVisible != 0,
            WorldItem: worldItem);
    }

    private static AreaInfo MapAreaInfo(in NmAreaInfo info)
    {
        int count = (int)Math.Min(info.RulesetCount, (uint)NativeShimConstants.AreaMaxRulesets);
        uint[] rulesets = count > 0 ? new uint[count] : [];
        for (int i = 0; i < count; i++)
        {
            rulesets[i] = info.RulesetIds[i];
        }

        return new AreaInfo(
            AreaId: info.GetAreaId(),
            AreaName: info.GetAreaName(),
            Act: info.Act,
            AreaLevel: info.AreaLevel,
            EffectiveLevel: info.EffectiveLevel,
            Hash16: info.Hash16,
            IsTown: info.IsTown != 0,
            IsHideout: info.IsHideout != 0,
            IsGuildHideout: info.IsGuildHideout != 0,
            ActiveRulesetIds: rulesets,
            AreaSeed: info.AreaSeed);
    }

    private static ItemFilterState MapItemFilterState(in NmItemFilterState state) =>
        new(
            FilterName: state.GetFilterName(),
            FilterFolder: state.GetFilterFolder(),
            FilterPath: state.GetFilterPath(),
            IsLoaded: state.Loaded != 0,
            HideFilteredGroundItems: state.HideFilteredGroundItems != 0,
            AltHighlightActive: state.AltHighlightActive != 0);

    private static ItemFilterResult MapItemFilterResult(in NmItemFilterResult res) =>
        new(
            Evaluated: res.Evaluated != 0,
            IsVisible: res.IsVisible != 0,
            FontSize: unchecked((int)res.FontSize),
            TextRgba: res.TextRgba,
            BorderRgba: res.BorderRgba,
            BackgroundRgba: res.BgRgba,
            AlertSoundId: res.AlertSoundId,
            MinimapIconShape: res.MinimapIconShape,
            MinimapIconColor: res.MinimapIconColor,
            MinimapIconSize: res.MinimapIconSize,
            BeamColor: res.BeamColor,
            BeamTemporary: res.BeamTemporary != 0,
            LabelRect: res.HasLabelRect != 0 ? new UiRect(res.LabelX, res.LabelY, res.LabelW, res.LabelH) : null);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        Volatile.Write(ref _disposed, true);
        lock (_phase1Gate)
        {
            if (_screensNativeHooked && _table->ScreensSetCallback is not null)
            {
                _table->ScreensSetCallback(null, null);
                _screensNativeHooked = false;
            }

            if (_inputNativeHooked && _table->InputSetCallback is not null)
            {
                _table->InputSetCallback(null, null);
                _inputNativeHooked = false;
            }

            if (_preGameNativeHooked && _table->PreGameSetCallbacks is not null)
            {
                _table->PreGameSetCallbacks(null, null, null);
                _preGameNativeHooked = false;
            }

            if (_areaNativeHooked && _table->LifecycleSetAreaCallback is not null)
            {
                _table->LifecycleSetAreaCallback(null, null);
                _areaNativeHooked = false;
            }

            if (_filterNativeHooked && _table->FilterSetReloadCallback is not null)
            {
                _table->FilterSetReloadCallback(null, null);
                _filterNativeHooked = false;
            }

            if (_tooltipNativeHooked && _table->TooltipSetItemAugmentCallback is not null)
            {
                _table->TooltipSetItemAugmentCallback(null, null);
                _tooltipNativeHooked = false;
            }

            if (_affinityNativeHooked && _table->AffinitySetRouterCallback is not null)
            {
                _table->AffinitySetRouterCallback(null, null);
                _affinityNativeHooked = false;
            }

            _leagueFillCallbacks.Clear();
            _keyCallbacks.Clear();
            _mouseCallbacks.Clear();
            _charCreatingCallbacks.Clear();
            _charCreatedCallbacks.Clear();
            _areaLifecycleCallbacks.Clear();
            _filterReloadCallbacks.Clear();
            _tooltipAugmentCallbacks.Clear();
            _affinityRouteCallbacks.Clear();
            _affinityBitsetCallbacks.Clear();

            if (_selfCallbackHandle.IsAllocated)
            {
                _selfCallbackHandle.Free();
            }
        }

        foreach (UiEventSlot slot in _uiEventSlots.Values)
        {
            Volatile.Write(ref slot.Handler, null);
            slot.ActiveToken = 0;
            slot.NativeSubscribed = false;
        }

        _uiEventSlots.Clear();

        foreach (nint ptr in _pendingWorkHandles.Keys.ToArray())
        {
            if (_pendingWorkHandles.TryRemove(ptr, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        }

        while (_pendingWork.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingWorkCount);
        }

        foreach ((CallbackKind Kind, ulong Token) key in _pinnedCallbacks.Keys.ToArray())
        {
            if (_pinnedCallbacks.TryRemove(key, out GCHandle h) && h.IsAllocated)
            {
                h.Free();
            }
        }
    }

    private struct GuardedInvocationContext
    {
        public nint TargetAddress;
        public int ArgCount;
        public nint Arg0;
        public nint Arg1;
        public nint Arg2;
        public nint Arg3;
    }

    private struct StreamRemainingProbeContext
    {
        public void* StreamHandle;
        public ulong Remaining;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long InvokeGuardedTrampoline(void* context)
    {
        GuardedInvocationContext* ctx = (GuardedInvocationContext*)context;
        return ctx->ArgCount switch
        {
            0 => ((delegate* unmanaged[Cdecl]<nint>)ctx->TargetAddress)(),
            1 => ((delegate* unmanaged[Cdecl]<nint, nint>)ctx->TargetAddress)(ctx->Arg0),
            2 => ((delegate* unmanaged[Cdecl]<nint, nint, nint>)ctx->TargetAddress)(ctx->Arg0, ctx->Arg1),
            3 => ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)ctx->TargetAddress)(ctx->Arg0, ctx->Arg1, ctx->Arg2),
            _ => ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)ctx->TargetAddress)(ctx->Arg0, ctx->Arg1, ctx->Arg2, ctx->Arg3),
        };
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long ProbeStreamRemainingTrampoline(void* context) => ProbeStreamRemainingCore(context);

    private static long ProbeStreamRemainingCore(void* context)
    {
        StreamRemainingProbeContext* ctx = (StreamRemainingProbeContext*)context;
        byte* stream = (byte*)ctx->StreamHandle;
        ulong magic = *(ulong*)stream;
        if (magic == SyntheticStreamMagic)
        {
            ulong size = *(ulong*)(stream + 16);
            ulong cursor = *(ulong*)(stream + 24);
            ctx->Remaining = size >= cursor ? size - cursor : 0;
            return 0;
        }

        ulong total = *(ulong*)(stream + 0x270);
        ulong cur = *(ulong*)(stream + 0x278);
        ctx->Remaining = total >= cur ? total - cur : 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeFrame(float deltaSeconds, void* userData)
    {
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is FrameCallbackWrapper wrapper)
            {
                using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                wrapper.Callback(deltaSeconds);
            }
        }
        catch
        {
            // Never let managed exceptions unwind across native game frames!
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeUiEvent(void* control, void* userData)
    {
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is UiEventSlot slot)
            {
                using var _ = slot.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                Volatile.Read(ref slot.Handler)?.Invoke();
            }
        }
        catch
        {
            // Never unwind managed exceptions into native UI event dispatch.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeMainThreadDrain(void* userData)
    {
        nint ptr = (nint)userData;
        if (ptr == nint.Zero)
        {
            return;
        }

        GCHandle handle = GCHandle.FromIntPtr(ptr);
        PendingWorkItem? item = handle.Target as PendingWorkItem;
        if (item is not null)
        {
            if (item.Bridge._pendingWorkHandles.TryRemove(ptr, out GCHandle removed) && removed.IsAllocated)
            {
                removed.Free();
            }
        }
        else if (handle.IsAllocated)
        {
            handle.Free();
        }

        if (item is null || Volatile.Read(ref item.Bridge._disposed))
        {
            return;
        }

        try
        {
            using var _ = item.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
            item.Work();
        }
        catch
        {
            // Exception wall across native boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus OnNativeFileOverlay(byte* virtualPathUtf8, byte** outData, nuint* outSize, void* userData)
    {
        try
        {
            if (virtualPathUtf8 is null || outData is null || outSize is null)
            {
                return NmStatus.ErrInvalidArg;
            }

            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is not Func<string, byte[]?> resolver)
            {
                return NmStatus.ErrInvalidArg;
            }

            string? path = Marshal.PtrToStringUTF8((nint)virtualPathUtf8);
            if (string.IsNullOrWhiteSpace(path))
            {
                return NmStatus.ErrInvalidArg;
            }

            byte[]? bytes = resolver(path);
            if (bytes is null)
            {
                *outData = null;
                *outSize = 0;
                return NmStatus.ErrSymbolNotFound;
            }

            byte* nativeBuf = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, bytes.Length));
            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, (nint)nativeBuf, bytes.Length);
            }

            *outData = nativeBuf;
            *outSize = (nuint)bytes.Length;
            return NmStatus.Ok;
        }
        catch
        {
            return NmStatus.ErrGameException;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeFileOverlayFree(byte* data, void* userData)
    {
        if (data is not null)
        {
            NativeMemory.Free(data);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeRulesetRx(uint rulesetId, byte* payload, uint length, void* userData)
    {
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is RulesetCallbackWrapper wrapper)
            {
                using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                byte[] copy = new byte[length];
                if (length > 0 && payload is not null)
                {
                    Marshal.Copy((nint)payload, copy, 0, (int)length);
                }

                wrapper.Callback(copy);
            }
        }
        catch
        {
            // Never unwind managed exceptions into native network pump.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnNativeChatRx(byte* senderUtf8, byte* textUtf8, void* userData)
    {
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is ChatCallbackWrapper wrapper)
            {
                using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                string sender = senderUtf8 is not null ? Marshal.PtrToStringUTF8((nint)senderUtf8) ?? string.Empty : string.Empty;
                string text = textUtf8 is not null ? Marshal.PtrToStringUTF8((nint)textUtf8) ?? string.Empty : string.Empty;
                return wrapper.Callback(sender, text) ? 1 : 0;
            }
        }
        catch
        {
            // Exception wall: fail-open (do not suppress stock chat on managed exception).
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnNativeOpcodeRx(ushort opcode, void* streamHandle, byte* payload, uint length, void* userData)
    {
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is not OpcodeHandlerWrapper wrapper)
            {
                return 0;
            }

            using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);

            if (wrapper.StreamHandler is not null && streamHandle is not null)
            {
                byte[] preloaded = [];
                if (payload is not null && length > 0)
                {
                    preloaded = new byte[length];
                    Marshal.Copy((nint)payload, preloaded, 0, (int)length);
                }

                var stream = new NativePacketStream(wrapper.Bridge, (nint)streamHandle);
                return wrapper.StreamHandler(opcode, stream, preloaded) ? 1 : 0;
            }

            if (wrapper.ByteHandler is not null)
            {
                byte[] bytes;
                if (payload is not null)
                {
                    bytes = new byte[length];
                    if (length > 0)
                    {
                        Marshal.Copy((nint)payload, bytes, 0, (int)length);
                    }
                }
                else if (streamHandle is not null && wrapper.Bridge._table->StreamRead is not null)
                {
                    ulong remaining = ReadRemainingStreamBytes(wrapper.Bridge, streamHandle);
                    if (remaining > 1048576ul)
                    {
                        return 0;
                    }

                    bytes = new byte[remaining];
                    if (remaining > 0 && !wrapper.Bridge.StreamRead((nint)streamHandle, bytes))
                    {
                        return 0;
                    }
                }
                else
                {
                    bytes = [];
                }

                wrapper.ByteHandler(opcode, bytes);
                return 1;
            }
        }
        catch
        {
            // Exception wall across native MessagePump.
        }

        return 0;
    }

    private static ulong ReadRemainingStreamBytes(NativeShimBridge bridge, void* streamHandle)
    {
        StreamRemainingProbeContext probe = new() { StreamHandle = streamHandle, Remaining = 0 };
        if (bridge._table->CallGuarded is not null)
        {
            long rc = 0;
            NmExceptionInfo ex = default;
            if (bridge._table->CallGuarded(&ProbeStreamRemainingTrampoline, &probe, &rc, &ex) == NmStatus.Ok)
            {
                return probe.Remaining;
            }

            return 0;
        }

        ProbeStreamRemainingCore(&probe);
        return probe.Remaining;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeHookChain(NmHookContext* nativeCtx, void* userData)
    {
        if (nativeCtx is null)
        {
            return;
        }

        bool prev = s_inNativeGameFrame;
        s_inNativeGameFrame = true;
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is not HookChainWrapper wrapper)
            {
                return;
            }

            var managedCtx = new HookContext(unchecked((nint)nativeCtx->TargetAddress))
            {
                ReturnValue = nativeCtx->ReturnValue,
                ReturnFloatBits = nativeCtx->ReturnFloat,
                SkipOriginal = nativeCtx->SkipOriginal != 0,
            };
            for (int i = 0; i < 8; i++)
            {
                managedCtx.Args[i] = nativeCtx->Args[i];
            }

            for (int i = 0; i < 4; i++)
            {
                managedCtx.FloatArgs[i] = nativeCtx->FloatArgs[i];
            }

            wrapper.Callback(managedCtx);

            for (int i = 0; i < 8; i++)
            {
                nativeCtx->Args[i] = managedCtx.Args[i];
            }

            for (int i = 0; i < 4; i++)
            {
                nativeCtx->FloatArgs[i] = managedCtx.FloatArgs[i];
            }

            nativeCtx->ReturnValue = managedCtx.ReturnValue;
            nativeCtx->ReturnFloat = managedCtx.ReturnFloatBits;
            nativeCtx->SkipOriginal = managedCtx.SkipOriginal ? (byte)1 : (byte)0;
        }
        catch
        {
            // Exception wall across native hook dispatcher.
        }
        finally
        {
            s_inNativeGameFrame = prev;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeScreenEvent(int eventKind, void* primaryHandle, ulong param0, void* userData)
    {
        try
        {
            if (userData is null)
            {
                return;
            }

            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is not NativeShimBridge bridge || Volatile.Read(ref bridge._disposed))
            {
                return;
            }

            using var _ = bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
            if ((NmScreenEventKind)eventKind == NmScreenEventKind.LeagueOverlayFilled)
            {
                Action<nint, IReadOnlyList<LeagueCardInfo>>[] callbacks;
                lock (bridge._phase1Gate)
                {
                    callbacks = [.. bridge._leagueFillCallbacks];
                }

                if (callbacks.Length > 0)
                {
                    List<LeagueCardInfo> cards = bridge.CollectActiveLeagueCards((nint)primaryHandle);
                    foreach (Action<nint, IReadOnlyList<LeagueCardInfo>> cb in callbacks)
                    {
                        cb((nint)primaryHandle, cards);
                    }
                }
            }
        }
        catch
        {
            // Never let managed exceptions unwind across native screen hooks.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnNativeInput(NmInputEvent* ev, void* userData)
    {
        try
        {
            if (ev is null || userData is null)
            {
                return 0;
            }

            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is not NativeShimBridge bridge || Volatile.Read(ref bridge._disposed))
            {
                return 0;
            }

            using var _ = bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
            var mods = (KeyModifiers)(ev->Modifiers & 0x7u);
            bool textFocused = ev->IsTextInputFocused != 0;
            var kind = (NmInputKind)ev->Kind;

            if (kind is NmInputKind.KeyDown or NmInputKind.KeyUp)
            {
                Func<KeyEventArgs, bool>[] handlers;
                lock (bridge._phase1Gate)
                {
                    handlers = [.. bridge._keyCallbacks];
                }

                if (handlers.Length == 0)
                {
                    return 0;
                }

                var args = new KeyEventArgs(
                    unchecked((int)ev->VirtualKey),
                    mods,
                    isDown: kind == NmInputKind.KeyDown,
                    isRepeat: ev->IsRepeat != 0,
                    isTextInputFocused: textFocused);
                bool suppressed = false;
                foreach (Func<KeyEventArgs, bool> h in handlers)
                {
                    if (h(args) || args.Handled)
                    {
                        suppressed = true;
                    }
                }

                return suppressed ? 1 : 0;
            }

            if (kind is NmInputKind.MouseDown or NmInputKind.MouseUp or NmInputKind.MouseWheel or NmInputKind.MouseMove)
            {
                Func<MouseEventArgs, bool>[] handlers;
                lock (bridge._phase1Gate)
                {
                    handlers = [.. bridge._mouseCallbacks];
                }

                if (handlers.Length == 0)
                {
                    return 0;
                }

                InputEventKind managedKind = kind switch
                {
                    NmInputKind.MouseDown => InputEventKind.Down,
                    NmInputKind.MouseUp => InputEventKind.Up,
                    NmInputKind.MouseWheel => InputEventKind.Wheel,
                    _ => InputEventKind.Move,
                };
                MouseButton button = (NmMouseButton)ev->MouseButton switch
                {
                    NmMouseButton.Left => MouseButton.Left,
                    NmMouseButton.Right => MouseButton.Right,
                    NmMouseButton.Middle => MouseButton.Middle,
                    NmMouseButton.X1 => MouseButton.X1,
                    NmMouseButton.X2 => MouseButton.X2,
                    _ => MouseButton.None,
                };

                var args = new MouseEventArgs(button, managedKind, ev->MouseX, ev->MouseY, mods, ev->WheelDelta, textFocused);
                bool suppressed = false;
                foreach (Func<MouseEventArgs, bool> h in handlers)
                {
                    if (h(args) || args.Handled)
                    {
                        suppressed = true;
                    }
                }

                return suppressed ? 1 : 0;
            }
        }
        catch
        {
            // Fail-open across Win32 WndProc / StateStack::OnWindowMessage boundary.
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnNativePreGameCreating(
        byte* nameUtf8,
        byte* leagueUtf8,
        byte* startStateUtf8,
        byte* outStartStateUtf8,
        nuint outCapacity,
        void* userData)
    {
        try
        {
            if (userData is null || outStartStateUtf8 is null || outCapacity == 0)
            {
                return 0;
            }

            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is not NativeShimBridge bridge || Volatile.Read(ref bridge._disposed))
            {
                return 0;
            }

            Func<string, string, string, string?>[] handlers;
            lock (bridge._phase1Gate)
            {
                handlers = [.. bridge._charCreatingCallbacks];
            }

            if (handlers.Length == 0)
            {
                return 0;
            }

            using var _ = bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
            string name = nameUtf8 is not null ? Marshal.PtrToStringUTF8((nint)nameUtf8) ?? string.Empty : string.Empty;
            string league = leagueUtf8 is not null ? Marshal.PtrToStringUTF8((nint)leagueUtf8) ?? string.Empty : string.Empty;
            string currentStartState = startStateUtf8 is not null ? Marshal.PtrToStringUTF8((nint)startStateUtf8) ?? string.Empty : string.Empty;
            string originalStartState = currentStartState;

            foreach (Func<string, string, string, string?> h in handlers)
            {
                string? updated = h(name, league, currentStartState);
                if (!string.IsNullOrEmpty(updated))
                {
                    currentStartState = updated;
                }
            }

            if (string.Equals(currentStartState, originalStartState, StringComparison.Ordinal))
            {
                return 0;
            }

            byte[] utf8 = Encoding.UTF8.GetBytes(currentStartState);
            if ((nuint)(utf8.Length + 1) > outCapacity)
            {
                return 0;
            }

            utf8.CopyTo(new Span<byte>(outStartStateUtf8, utf8.Length));
            outStartStateUtf8[utf8.Length] = 0;
            return 1;
        }
        catch
        {
            // Never let managed exceptions unwind across LoginClient::WriteCreateCharacter_0x12.
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativePreGameCreated(byte* nameUtf8, byte* leagueUtf8, ushort resultCode, void* userData)
    {
        try
        {
            if (userData is null)
            {
                return;
            }

            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is not NativeShimBridge bridge || Volatile.Read(ref bridge._disposed))
            {
                return;
            }

            Action<string, string, ushort>[] handlers;
            lock (bridge._phase1Gate)
            {
                handlers = [.. bridge._charCreatedCallbacks];
            }

            if (handlers.Length == 0)
            {
                return;
            }

            using var _ = bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
            string name = nameUtf8 is not null ? Marshal.PtrToStringUTF8((nint)nameUtf8) ?? string.Empty : string.Empty;
            string league = leagueUtf8 is not null ? Marshal.PtrToStringUTF8((nint)leagueUtf8) ?? string.Empty : string.Empty;
            foreach (Action<string, string, ushort> h in handlers)
            {
                h(name, league, resultCode);
            }
        }
        catch
        {
            // Never let managed exceptions unwind across CreateCharacterState::OnCreateResult.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeAreaEvent(int eventKind, NmAreaInfo* areaInfo, void* userData)
    {
        try
        {
            if (userData is null)
            {
                return;
            }

            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is not NativeShimBridge bridge || Volatile.Read(ref bridge._disposed))
            {
                return;
            }

            (Action<AreaInfo> OnEntered, Action<string> OnLeft)[] callbacks;
            lock (bridge._phase1Gate)
            {
                callbacks = [.. bridge._areaLifecycleCallbacks];
            }

            if (callbacks.Length == 0)
            {
                return;
            }

            using var _ = bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
            var kind = (NmAreaEventKind)eventKind;
            if (kind == NmAreaEventKind.Entered && areaInfo is not null)
            {
                AreaInfo managedArea = MapAreaInfo(in *areaInfo);
                foreach (var cb in callbacks)
                {
                    cb.OnEntered(managedArea);
                }
            }
            else if (kind == NmAreaEventKind.Left)
            {
                string areaId = areaInfo is not null ? areaInfo->GetAreaId() : string.Empty;
                foreach (var cb in callbacks)
                {
                    cb.OnLeft(areaId);
                }
            }
        }
        catch
        {
            // Never let managed exceptions unwind across native area lifecycle hooks.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeFilterReloaded(int succeeded, NmItemFilterState* filterState, void* userData)
    {
        try
        {
            if (userData is null)
            {
                return;
            }

            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is not NativeShimBridge bridge || Volatile.Read(ref bridge._disposed))
            {
                return;
            }

            Action<bool, string, ItemFilterState?>[] callbacks;
            lock (bridge._phase1Gate)
            {
                callbacks = [.. bridge._filterReloadCallbacks];
            }

            if (callbacks.Length == 0)
            {
                return;
            }

            using var _ = bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
            ItemFilterState? managedState = filterState is not null ? MapItemFilterState(in *filterState) : null;
            string message = succeeded != 0 ? "Item Filter loaded successfully." : "Item Filter failed to load.";
            foreach (var cb in callbacks)
            {
                cb(succeeded != 0, message, managedState);
            }
        }
        catch
        {
            // Never let managed exceptions unwind across ItemFilterManager::NotifyLoaded.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeUiIntEvent(void* control, int value, void* userData)
    {
        try
        {
            if (userData is null)
            {
                return;
            }

            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is UiIntEventWrapper wrapper && !Volatile.Read(ref wrapper.Bridge._disposed))
            {
                using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                wrapper.Callback(value);
            }
        }
        catch
        {
            // Exception wall across native UI rich control events.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeTooltipAugment(void* popupControl, NmItemSnapshot* item, int altHeld, void* userData)
    {
        try
        {
            if (userData is null || item is null || item->Present == 0)
            {
                return;
            }

            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is not NativeShimBridge bridge || Volatile.Read(ref bridge._disposed))
            {
                return;
            }

            Func<ItemTooltipContext, IReadOnlyList<TooltipSection>>[] callbacks;
            lock (bridge._phase1Gate)
            {
                callbacks = [.. bridge._tooltipAugmentCallbacks];
            }

            if (callbacks.Length == 0 || bridge._table->TooltipAddItemSection is null)
            {
                return;
            }

            using var _ = bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
            HoveredItemInfo hovered = MapItemSnapshotToHoveredItem(in *item);
            var ctx = new ItemTooltipContext(hovered, altHeld != 0, (nint)popupControl);

            foreach (Func<ItemTooltipContext, IReadOnlyList<TooltipSection>> cb in callbacks)
            {
                IReadOnlyList<TooltipSection>? sections;
                try
                {
                    sections = cb(ctx);
                }
                catch
                {
                    continue;
                }

                if (sections is null)
                {
                    continue;
                }

                foreach (TooltipSection section in sections)
                {
                    if (section?.Lines is null || section.Lines.Count == 0)
                    {
                        continue;
                    }

                    for (int i = 0; i < section.Lines.Count; i++)
                    {
                        TooltipLine line = section.Lines[i];
                        if (string.IsNullOrEmpty(line.Text))
                        {
                            continue;
                        }

                        byte[] lineUtf8 = ToNullTerminatedUtf8(line.Text);
                        int addSep = (i == 0 && section.PrependSeparator) ? 1 : 0;
                        fixed (byte* pLine = lineUtf8)
                        {
                            bridge._table->TooltipAddItemSection(popupControl, pLine, line.RgbaColor, addSep);
                        }
                    }
                }
            }
        }
        catch
        {
            // Never let managed exceptions unwind across ItemPopup::BuildSections.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnNativeAffinityRoute(NmItemSnapshot* item, int stashKind, NmAffinityRouteResult* outResult, void* userData)
    {
        try
        {
            if (userData is null || item is null || item->Present == 0 || outResult is null)
            {
                return 0;
            }

            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is not NativeShimBridge bridge || Volatile.Read(ref bridge._disposed))
            {
                return 0;
            }

            using var _ = bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
            HoveredItemInfo hovered = MapItemSnapshotToHoveredItem(in *item);
            StashKind kind = stashKind == 1 ? StashKind.Guild : StashKind.Personal;
            var ctx = new StashAffinityItemContext(hovered, kind);

            if (stashKind < 0)
            {
                Func<StashAffinityItemContext, uint>[] bitsetCallbacks;
                lock (bridge._phase1Gate)
                {
                    bitsetCallbacks = [.. bridge._affinityBitsetCallbacks];
                }

                uint mask = 0u;
                foreach (Func<StashAffinityItemContext, uint> cb in bitsetCallbacks)
                {
                    try { mask |= cb(ctx); }
                    catch { }
                }

                if (mask == 0u)
                {
                    return 0;
                }

                outResult->StructSize = (uint)sizeof(NmAffinityRouteResult);
                outResult->Action = (int)NmAffinityRouteAction.Stock;
                outResult->TargetCount = 0;
                outResult->StockAffinityBitmask = mask & 0x003FFFFFu;
                return 1;
            }

            Func<StashAffinityItemContext, IReadOnlyList<StashAffinityRoute>>[] routeCallbacks;
            lock (bridge._phase1Gate)
            {
                routeCallbacks = [.. bridge._affinityRouteCallbacks];
            }

            if (routeCallbacks.Length == 0)
            {
                return 0;
            }

            var routes = new List<StashAffinityRoute>();
            bool replaceStock = false;
            foreach (Func<StashAffinityItemContext, IReadOnlyList<StashAffinityRoute>> cb in routeCallbacks)
            {
                try
                {
                    IReadOnlyList<StashAffinityRoute>? matched = cb(ctx);
                    if (matched is not null)
                    {
                        foreach (StashAffinityRoute r in matched)
                        {
                            if (r is not null)
                            {
                                routes.Add(r);
                                if (r.ReplaceStockTargets)
                                {
                                    replaceStock = true;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            if (routes.Count == 0)
            {
                return 0;
            }

            int count = Math.Min(routes.Count, NativeShimConstants.AffinityMaxTargets);
            outResult->StructSize = (uint)sizeof(NmAffinityRouteResult);
            outResult->Action = (int)(replaceStock ? NmAffinityRouteAction.Override : NmAffinityRouteAction.Append);
            outResult->TargetCount = (uint)count;
            outResult->StockAffinityBitmask = 0u;
            for (int i = 0; i < count; i++)
            {
                outResult->SetTarget(i, new NmAffinityTarget
                {
                    TabIndex = routes[i].TabIndex,
                    AffinityId = routes[i].AffinityId,
                    Reserved = 0,
                });
            }

            return 1;
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NmStatus OnNativeGealBuiltin(
        uint entityId,
        byte* argsJsonUtf8,
        byte* outResultUtf8,
        nuint outCapacity,
        nuint* outLen,
        void* userData)
    {
        try
        {
            if (userData is null)
            {
                return NmStatus.ErrInvalidArg;
            }

            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is not GealBuiltinWrapper wrapper || Volatile.Read(ref wrapper.Bridge._disposed))
            {
                return NmStatus.ErrNotFound;
            }

            using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
            string? argsJson = argsJsonUtf8 is not null ? Marshal.PtrToStringUTF8((nint)argsJsonUtf8) : null;
            GealValue[] parsedArgs = DeserializeGealArgumentsJson(argsJson);
            GealValue result = wrapper.Handler(entityId, new GealArguments(parsedArgs));

            if (!wrapper.HasReturn)
            {
                if (outLen is not null)
                {
                    *outLen = 0;
                }

                return NmStatus.Ok;
            }

            byte[] encoded = Encoding.UTF8.GetBytes(SerializeGealValueJson(result));
            if (outLen is not null)
            {
                *outLen = (nuint)encoded.Length;
            }

            if (outResultUtf8 is null || outCapacity < (nuint)(encoded.Length + 1))
            {
                return NmStatus.ErrBufferTooSmall;
            }

            if (encoded.Length > 0)
            {
                encoded.CopyTo(new Span<byte>(outResultUtf8, encoded.Length));
            }

            outResultUtf8[encoded.Length] = 0;
            return NmStatus.Ok;
        }
        catch
        {
            return NmStatus.ErrGameException;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeRulesetAreaEntered(uint rulesetId, uint localPlayerEntityId, void* userData)
    {
        try
        {
            if (userData is null) return;
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is NativeRulesetWrapper wrapper && !Volatile.Read(ref wrapper.Bridge._disposed))
            {
                using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                AreaInfo? area = wrapper.Bridge.GetAreaInfo();
                string areaId = area?.AreaId ?? string.Empty;
                uint seed = area?.AreaSeed ?? 0u;
                byte[] active = area is not null
                    ? [.. area.ActiveRulesetIds.Where(id => id <= byte.MaxValue).Select(id => (byte)id)]
                    : [(byte)rulesetId];
                wrapper.OnAreaEntered(new AreaEventArgs(areaId, seed, active) { Area = area });
            }
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeRulesetFrame(uint rulesetId, float deltaSeconds, void* userData)
    {
        try
        {
            if (userData is null) return;
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is NativeRulesetWrapper wrapper && !Volatile.Read(ref wrapper.Bridge._disposed))
            {
                using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                wrapper.OnFrame(deltaSeconds);
            }
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeRulesetUpdate(uint rulesetId, byte* payload, uint length, void* userData)
    {
        try
        {
            if (userData is null) return;
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is NativeRulesetWrapper wrapper && !Volatile.Read(ref wrapper.Bridge._disposed))
            {
                using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                byte[] copy = new byte[length];
                if (length > 0 && payload is not null)
                {
                    Marshal.Copy((nint)payload, copy, 0, (int)length);
                }

                wrapper.OnRulesetUpdate(copy);
            }
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeRulesetAreaLeft(uint rulesetId, void* userData)
    {
        try
        {
            if (userData is null) return;
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is NativeRulesetWrapper wrapper && !Volatile.Read(ref wrapper.Bridge._disposed))
            {
                using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                wrapper.OnAreaLeft();
            }
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeCustomUiCreated(byte* kindNameUtf8, void* control, void* userData)
    {
        try
        {
            if (userData is null || control is null) return;
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is CustomUiControlWrapper wrapper && !Volatile.Read(ref wrapper.Bridge._disposed))
            {
                using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                wrapper.OnCreated((nint)control);
            }
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeCustomUiProperty(void* control, byte* keyUtf8, byte* valueUtf8, void* userData)
    {
        try
        {
            if (userData is null || control is null) return;
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is CustomUiControlWrapper wrapper && !Volatile.Read(ref wrapper.Bridge._disposed))
            {
                using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                string key = keyUtf8 is not null ? Marshal.PtrToStringUTF8((nint)keyUtf8) ?? string.Empty : string.Empty;
                string value = valueUtf8 is not null ? Marshal.PtrToStringUTF8((nint)valueUtf8) ?? string.Empty : string.Empty;
                wrapper.OnPropertyChanged((nint)control, key, value);
            }
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeCustomUiDestroyed(void* control, void* userData)
    {
        try
        {
            if (userData is null || control is null) return;
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is CustomUiControlWrapper wrapper && !Volatile.Read(ref wrapper.Bridge._disposed))
            {
                using var _ = wrapper.Bridge.EnterNativeGameThreadScope(captureGameThreadId: true);
                wrapper.OnDestroyed((nint)control);
            }
        }
        catch { }
    }

    internal static string NormalizeAndValidateVirtualPath(string virtualPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        string[] rawSegments = virtualPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in rawSegments)
        {
            if (seg == "..")
            {
                throw new ArgumentException($"Virtual path '{virtualPath}' contains forbidden '..' traversal segment.", nameof(virtualPath));
            }
        }

        return ClientTextEncoding.NormalizeVirtualPath(virtualPath);
    }

    private static byte[] ToNullTerminatedUtf8(string value)
    {
        int len = Encoding.UTF8.GetByteCount(value);
        byte[] buf = new byte[len + 1];
        Encoding.UTF8.GetBytes(value, 0, value.Length, buf, 0);
        buf[len] = 0;
        return buf;
    }

    private static void EnsureOk(NmStatus status, string operation)
    {
        if (status != NmStatus.Ok)
        {
            throw new InvalidOperationException($"Native shim call {operation} failed with status {status} ({(int)status}).");
        }
    }
}

internal sealed class NoopDisposable : IDisposable
{
    public static readonly NoopDisposable Instance = new();
    public void Dispose() { }
}

internal sealed class ActionDisposable(Action onDispose) : IDisposable
{
    private Action? _onDispose = onDispose;

    public void Dispose()
    {
        Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
