using System.Globalization;

namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Keyboard modifier bitmask captured during input dispatch.
/// </summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>No modifier keys held.</summary>
    None = 0,

    /// <summary>Shift key held.</summary>
    Shift = 1 << 0,

    /// <summary>Control key held.</summary>
    Control = 1 << 1,

    /// <summary>Alt / Menu key held.</summary>
    Alt = 1 << 2,
}

/// <summary>
/// Mouse button identifier for <see cref="MouseEventArgs"/> and <see cref="KeyChord"/>.
/// </summary>
public enum MouseButton
{
    /// <summary>No mouse button (for move or wheel events, or keyboard-only chords).</summary>
    None = 0,

    /// <summary>Primary left mouse button (LMB).</summary>
    Left = 1,

    /// <summary>Secondary right mouse button (RMB).</summary>
    Right = 2,

    /// <summary>Middle mouse button (MMB).</summary>
    Middle = 3,

    /// <summary>Extra mouse button 1 (XButton1 / Mouse4).</summary>
    X1 = 4,

    /// <summary>Extra mouse button 2 (XButton2 / Mouse5).</summary>
    X2 = 5,
}

/// <summary>
/// Kind of low-level mouse input event.
/// </summary>
public enum InputEventKind
{
    /// <summary>Button or key pressed down.</summary>
    Down = 0,

    /// <summary>Button or key released.</summary>
    Up = 1,

    /// <summary>Pointer moved.</summary>
    Move = 2,

    /// <summary>Mouse wheel scrolled.</summary>
    Wheel = 3,
}

/// <summary>
/// Mutable event arguments for a low-level keyboard event intercepted on the game thread.
/// Setting <see cref="Handled"/> to <c>true</c> suppresses the message from reaching Path of Exile.
/// </summary>
public sealed class KeyEventArgs
{
    /// <summary>Win32 virtual-key code (<c>VK_*</c>).</summary>
    public int VirtualKey { get; }

    /// <summary>Active modifier keys at the time of the event.</summary>
    public KeyModifiers Modifiers { get; }

    /// <summary><c>true</c> for key-down; <c>false</c> for key-up.</summary>
    public bool IsDown { get; }

    /// <summary><c>true</c> if this event is an OS auto-repeat key-down.</summary>
    public bool IsRepeat { get; }

    /// <summary><c>true</c> if a PoE <c>TextBox</c>, chat input, or search box currently has keyboard focus.</summary>
    public bool IsTextInputFocused { get; }

    /// <summary>Set to <c>true</c> to suppress this key event from reaching the game engine.</summary>
    public bool Handled { get; set; }

    /// <summary>Creates a new <see cref="KeyEventArgs"/>.</summary>
    public KeyEventArgs(int virtualKey, KeyModifiers modifiers, bool isDown, bool isRepeat = false, bool isTextInputFocused = false)
    {
        VirtualKey = virtualKey;
        Modifiers = modifiers;
        IsDown = isDown;
        IsRepeat = isRepeat;
        IsTextInputFocused = isTextInputFocused;
    }
}

/// <summary>
/// Mutable event arguments for a low-level mouse event intercepted on the game thread.
/// Setting <see cref="Handled"/> to <c>true</c> on a button-down event suppresses both the down event
/// and its matching button-up event from reaching Path of Exile.
/// </summary>
public sealed class MouseEventArgs
{
    /// <summary>Mouse button involved in the event, or <see cref="MouseButton.None"/> for move/wheel.</summary>
    public MouseButton Button { get; }

    /// <summary>Mouse event kind (Down, Up, Move, or Wheel).</summary>
    public InputEventKind Kind { get; }

    /// <summary>Client-space cursor X coordinate.</summary>
    public int X { get; }

    /// <summary>Client-space cursor Y coordinate.</summary>
    public int Y { get; }

    /// <summary>Mouse wheel delta (typically multiples of 120), or <c>0</c>.</summary>
    public int WheelDelta { get; }

    /// <summary>Active modifier keys at the time of the event.</summary>
    public KeyModifiers Modifiers { get; }

    /// <summary><c>true</c> if a PoE <c>TextBox</c>, chat input, or search box currently has keyboard focus.</summary>
    public bool IsTextInputFocused { get; }

    /// <summary>Set to <c>true</c> to suppress this mouse event from reaching the game engine.</summary>
    public bool Handled { get; set; }

    /// <summary>Creates a new <see cref="MouseEventArgs"/>.</summary>
    public MouseEventArgs(
        MouseButton button,
        InputEventKind kind,
        int x,
        int y,
        KeyModifiers modifiers = KeyModifiers.None,
        int wheelDelta = 0,
        bool isTextInputFocused = false)
    {
        Button = button;
        Kind = kind;
        X = x;
        Y = y;
        Modifiers = modifiers;
        WheelDelta = wheelDelta;
        IsTextInputFocused = isTextInputFocused;
    }
}

/// <summary>
/// Represents a keyboard key or mouse button combined with optional <see cref="KeyModifiers"/>
/// (for example <c>Ctrl+Shift+RMB</c> or <c>Ctrl+F6</c>).
/// </summary>
/// <param name="VirtualKey">Win32 virtual-key code (0 when bound to a <see cref="MouseButton"/>).</param>
/// <param name="Modifiers">Required modifier keys.</param>
/// <param name="MouseButton">Mouse button trigger, or <see cref="MouseButton.None"/> for keyboard chords.</param>
public readonly record struct KeyChord(
    int VirtualKey,
    KeyModifiers Modifiers = KeyModifiers.None,
    MouseButton MouseButton = MouseButton.None)
{
    /// <summary>An unbound empty chord.</summary>
    public static KeyChord None => default;

    /// <summary>Returns <c>true</c> if this chord has neither a virtual key nor a mouse button.</summary>
    public bool IsEmpty => VirtualKey == 0 && MouseButton == MouseButton.None;

    /// <summary>Returns <c>true</c> if this chord triggers on a mouse button rather than a keyboard key.</summary>
    public bool IsMouseChord => MouseButton != MouseButton.None;

    /// <summary>Creates a keyboard chord from a Win32 virtual-key code and optional modifiers.</summary>
    public static KeyChord FromKey(int virtualKey, KeyModifiers modifiers = KeyModifiers.None) =>
        new(virtualKey, modifiers, MouseButton.None);

    /// <summary>Creates a mouse chord from a <see cref="MouseButton"/> and optional modifiers.</summary>
    public static KeyChord FromMouse(MouseButton button, KeyModifiers modifiers = KeyModifiers.None) =>
        new(0, modifiers, button);

    /// <summary>Formats this chord into canonical human-readable notation (for example <c>Ctrl+Shift+RMB</c> or <c>Ctrl+K</c>).</summary>
    public override string ToString()
    {
        if (IsEmpty)
        {
            return "None";
        }

        List<string> parts = [];
        if (Modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (MouseButton != MouseButton.None)
        {
            parts.Add(MouseButton switch
            {
                MouseButton.Left => "LMB",
                MouseButton.Right => "RMB",
                MouseButton.Middle => "MMB",
                MouseButton.X1 => "Mouse4",
                MouseButton.X2 => "Mouse5",
                _ => MouseButton.ToString(),
            });
        }
        else
        {
            parts.Add(FormatVirtualKey(VirtualKey));
        }

        return string.Join('+', parts);
    }

    /// <summary>Parses canonical keybind strings such as <c>Ctrl+Shift+RMB</c>, <c>Alt+F5</c>, or <c>VK_0x4B</c>.</summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (string.Equals(trimmed, "None", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] tokens = trimmed.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        KeyModifiers mods = KeyModifiers.None;
        int vk = 0;
        MouseButton mb = MouseButton.None;

        foreach (string token in tokens)
        {
            if (string.Equals(token, "Ctrl", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "Control", StringComparison.OrdinalIgnoreCase))
            {
                mods |= KeyModifiers.Control;
            }
            else if (string.Equals(token, "Shift", StringComparison.OrdinalIgnoreCase))
            {
                mods |= KeyModifiers.Shift;
            }
            else if (string.Equals(token, "Alt", StringComparison.OrdinalIgnoreCase))
            {
                mods |= KeyModifiers.Alt;
            }
            else if (TryParseMouseButton(token, out MouseButton parsedMouse))
            {
                if (vk != 0 || mb != MouseButton.None)
                {
                    return false;
                }

                mb = parsedMouse;
            }
            else if (TryParseVirtualKey(token, out int parsedVk))
            {
                if (vk != 0 || mb != MouseButton.None)
                {
                    return false;
                }

                vk = parsedVk;
            }
            else
            {
                return false;
            }
        }

        if (vk == 0 && mb == MouseButton.None)
        {
            return false;
        }

        chord = new KeyChord(vk, mods, mb);
        return true;
    }

    private static bool TryParseMouseButton(string token, out MouseButton button)
    {
        button = token.ToUpperInvariant() switch
        {
            "LMB" or "LEFTMOUSE" or "MOUSE1" => MouseButton.Left,
            "RMB" or "RIGHTMOUSE" or "MOUSE2" => MouseButton.Right,
            "MMB" or "MIDDLEMOUSE" or "MOUSE3" => MouseButton.Middle,
            "X1" or "XBUTTON1" or "MOUSE4" => MouseButton.X1,
            "X2" or "XBUTTON2" or "MOUSE5" => MouseButton.X2,
            _ => MouseButton.None,
        };
        return button != MouseButton.None;
    }

    private static bool TryParseVirtualKey(string token, out int vk)
    {
        vk = 0;
        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                vk = c;
                return true;
            }
        }

        if ((token.StartsWith('F') || token.StartsWith('f')) &&
            int.TryParse(token[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int fn) &&
            fn is >= 1 and <= 24)
        {
            vk = 0x70 + (fn - 1);
            return true;
        }

        if (token.StartsWith("VK_0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(token[5..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hexVk) &&
            hexVk is > 0 and <= 0xFF)
        {
            vk = hexVk;
            return true;
        }

        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rawHex) &&
            rawHex is > 0 and <= 0xFF)
        {
            vk = rawHex;
            return true;
        }

        vk = token.ToUpperInvariant() switch
        {
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "SPACE" => 0x20,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "END" => 0x23,
            "HOME" => 0x24,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            "BACKSPACE" => 0x08,
            _ => 0,
        };
        return vk != 0;
    }

    private static string FormatVirtualKey(int vk)
    {
        if (vk is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
        {
            return ((char)vk).ToString();
        }

        if (vk is >= 0x70 and <= 0x87)
        {
            return $"F{vk - 0x70 + 1}";
        }

        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Escape",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            _ => $"VK_0x{vk:X2}",
        };
    }
}

/// <summary>
/// Diagnostic emitted when two registered keybinds share the same active <see cref="KeyChord"/>.
/// </summary>
/// <param name="KeybindId">Identifier of the newly registered or rebound keybind.</param>
/// <param name="ConflictingKeybindId">Identifier of the already-registered keybind sharing the chord.</param>
/// <param name="Chord">The conflicting key or mouse chord.</param>
/// <param name="Message">Human-readable warning message.</param>
public sealed record KeybindConflict(string KeybindId, string ConflictingKeybindId, KeyChord Chord, string Message);

/// <summary>
/// Snapshot of a registered keybind entry managed by <see cref="IKeybindRegistry"/>.
/// </summary>
/// <param name="Id">Stable unique keybind identifier (for example <c>stashqol.bulk_use</c>).</param>
/// <param name="ModId">Owning mod identifier.</param>
/// <param name="DisplayName">Human-readable label shown in keybind UI/diagnostics.</param>
/// <param name="DefaultChord">Mod's default key or mouse chord.</param>
/// <param name="CurrentChord">Active user-configured chord (loaded from <c>keybinds.json</c> when customized).</param>
/// <param name="AllowWhenTextInputFocused">Whether this keybind fires even when a <c>TextBox</c>/chat input has focus.</param>
/// <param name="SuppressGameInput">Whether triggering this keybind sets <c>Handled = true</c> to block PoE's default action.</param>
public sealed record KeybindEntry(
    string Id,
    string ModId,
    string DisplayName,
    KeyChord DefaultChord,
    KeyChord CurrentChord,
    bool AllowWhenTextInputFocused = false,
    bool SuppressGameInput = false);

/// <summary>
/// Centralized mod keybind registry persisted to <c>Mods/Client/keybinds.json</c> with conflict detection.
/// </summary>
public interface IKeybindRegistry
{
    /// <summary>File path where user keybind overrides are persisted (typically <c>Mods/Client/keybinds.json</c>).</summary>
    string ConfigPath { get; }

    /// <summary>Snapshot of all currently registered keybinds.</summary>
    IReadOnlyList<KeybindEntry> Entries { get; }

    /// <summary>Snapshot of all currently active chord conflicts across registered keybinds.</summary>
    IReadOnlyList<KeybindConflict> Conflicts { get; }

    /// <summary>
    /// Registers a named keybind with a default <see cref="KeyChord"/> and callback.
    /// Automatically applies any persisted override from <see cref="ConfigPath"/> and detects conflicts.
    /// </summary>
    /// <param name="id">Unique keybind identifier.</param>
    /// <param name="displayName">Human-readable label.</param>
    /// <param name="defaultChord">Default key or mouse chord.</param>
    /// <param name="onTriggered">Callback executed on the game thread when the chord fires.</param>
    /// <param name="allowWhenTextInputFocused">Whether to fire while a text input control is focused.</param>
    /// <param name="suppressGameInput">Whether to suppress the underlying key/mouse event from reaching PoE.</param>
    IDisposable Register(
        string id,
        string displayName,
        KeyChord defaultChord,
        Action onTriggered,
        bool allowWhenTextInputFocused = false,
        bool suppressGameInput = false);

    /// <summary>Returns the active <see cref="KeyChord"/> for <paramref name="id"/>, or <c>null</c> if unknown.</summary>
    KeyChord? GetBinding(string id);

    /// <summary>Rebinds <paramref name="id"/> to <paramref name="newChord"/>, persists to disk, and reports any conflicts.</summary>
    bool Rebind(string id, KeyChord newChord, out IReadOnlyList<KeybindConflict> conflicts);

    /// <summary>Resets <paramref name="id"/> back to its mod-declared default chord and persists to disk.</summary>
    bool ResetBinding(string id);

    /// <summary>Persists current custom overrides to <see cref="ConfigPath"/>.</summary>
    void Save();

    /// <summary>Reloads user overrides from <see cref="ConfigPath"/> and refreshes active conflicts.</summary>
    void Reload();

    /// <summary>Raised whenever a registration or rebind produces a chord conflict.</summary>
    event Action<KeybindConflict>? ConflictDetected;
}

/// <summary>
/// Low-level suppressible keyboard/mouse input interception service, text-input focus query,
/// and centralized <see cref="IKeybindRegistry"/>.
/// </summary>
public interface IInputService
{
    /// <summary>
    /// Returns <c>true</c> if the currently focused PoE UI control is a text input (<c>TextBox</c>, chat box, stash search, etc.).
    /// Hotkeys and key handlers skip invocation while this is <c>true</c> unless explicitly opted in.
    /// </summary>
    bool IsTextInputFocused { get; }

    /// <summary>Currently held modifier keys.</summary>
    KeyModifiers ActiveModifiers { get; }

    /// <summary>Latest observed cursor position in client coordinates.</summary>
    (int X, int Y) MousePosition { get; }

    /// <summary>Centralized keybind registry persisted to <c>Mods/Client/keybinds.json</c>.</summary>
    IKeybindRegistry Keybinds { get; }

    /// <summary>
    /// Subscribes a low-level keyboard handler executed on the game thread before PoE handles the message.
    /// Lower <paramref name="priority"/> values execute first.
    /// </summary>
    /// <param name="handler">Callback receiving <see cref="KeyEventArgs"/> (set <c>Handled = true</c> to suppress).</param>
    /// <param name="priority">Execution priority (lower runs earlier).</param>
    /// <param name="allowWhenTextInputFocused">If <c>false</c> (default), skipped while <see cref="IsTextInputFocused"/> is <c>true</c>.</param>
    IDisposable OnKey(Action<KeyEventArgs> handler, int priority = 0, bool allowWhenTextInputFocused = false);

    /// <summary>
    /// Subscribes a low-level mouse handler executed on the game thread before PoE handles the message.
    /// Setting <c>Handled = true</c> on a button-down event (for example <c>Ctrl+Shift+RMB</c>) suppresses both down and matching up.
    /// </summary>
    /// <param name="handler">Callback receiving <see cref="MouseEventArgs"/> (set <c>Handled = true</c> to suppress).</param>
    /// <param name="priority">Execution priority (lower runs earlier).</param>
    /// <param name="allowWhenTextInputFocused">If <c>false</c> (default), skipped while <see cref="IsTextInputFocused"/> is <c>true</c>.</param>
    IDisposable OnMouse(Action<MouseEventArgs> handler, int priority = 0, bool allowWhenTextInputFocused = false);
}
