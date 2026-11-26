namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Controls Phase 5 (<c>1.2.0</c>) extended 32-bit stack size deserialization (<c>Stack::Deserialise</c> opt-in
/// <c>0xFFFF + u32</c> wire sentinel), optional millions (<c>1M</c>/<c>1.2M</c>) stack-count UI formatting,
/// and sideband per-entity stack size overrides (<c>Stack + 0x18</c>).
/// All mod-owned toggles and overrides are tracked in <c>ScopedGameServices</c> and automatically disabled on mod unload or server disconnect.
/// </summary>
public interface IStackExtensionService
{
    /// <summary>
    /// Gets whether opt-in 32-bit (<c>u32</c>) stack deserialization (<c>0xFFFF</c> sentinel + LE <c>u32</c>) is currently enabled.
    /// Defaults to <c>false</c> for 100% vanilla wire compatibility and resets automatically on mod unload or disconnect.
    /// </summary>
    bool IsExtendedU32StacksEnabled { get; }

    /// <summary>
    /// Enables or disables opt-in 32-bit (<c>u32</c>) stack deserialization for this mod/session.
    /// Automatically disabled when the owning mod unloads or the client disconnects.
    /// </summary>
    /// <param name="enabled"><c>true</c> to opt into extended <c>u32</c> stack decoding; <c>false</c> to release this mod's opt-in.</param>
    void SetExtendedU32StacksEnabled(bool enabled);

    /// <summary>
    /// Gets whether extended stack-count UI formatting (<c>&gt;= 1,000,000</c> rendered as <c>"1M"</c>/<c>"1.2M"</c> instead of <c>"1000k"</c>) is enabled.
    /// </summary>
    bool IsFormatMillionsEnabled { get; }

    /// <summary>
    /// Enables or disables extended millions (<c>"1M"</c>/<c>"1.2M"</c>) formatting on <c>ui.format_stack_count</c>.
    /// Automatically disabled when the owning mod unloads or the client disconnects.
    /// </summary>
    /// <param name="enabled"><c>true</c> to format counts <c>&gt;= 1,000,000</c> with an <c>"M"</c> suffix; <c>false</c> to release this mod's opt-in.</param>
    void SetFormatMillionsEnabled(bool enabled);

    /// <summary>
    /// Applies a sideband 32-bit stack size override directly onto a live item entity's <c>Stack + 0x18</c> field
    /// and refreshes its inventory view (fallback path for server mods that cannot modify <c>0x00BF</c> serialization).
    /// </summary>
    /// <param name="entityId">Target item entity identifier.</param>
    /// <param name="stackSize">New 32-bit stack size value.</param>
    /// <returns><c>true</c> if the override was applied; otherwise <c>false</c>.</returns>
    bool SetEntityStackOverride(uint entityId, uint stackSize);
}
