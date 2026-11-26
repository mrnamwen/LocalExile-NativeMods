using System.Text.Json;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Engine;

namespace LocalExile.Client.Host.Services;

/// <summary>
/// Centralized mod keybind registry persisted to <c>Mods/Client/keybinds.json</c> with conflict detection.
/// </summary>
public sealed class KeybindRegistry : IKeybindRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class RegisteredKeybind
    {
        public required string Id { get; init; }
        public required string ModId { get; init; }
        public required string DisplayName { get; init; }
        public required KeyChord DefaultChord { get; init; }
        public KeyChord CurrentChord { get; set; }
        public required Action Callback { get; init; }
        public required bool AllowWhenTextInputFocused { get; init; }
        public required bool SuppressGameInput { get; init; }
        public required long Sequence { get; init; }

        public KeybindEntry ToSnapshot() => new(
            Id,
            ModId,
            DisplayName,
            DefaultChord,
            CurrentChord,
            AllowWhenTextInputFocused,
            SuppressGameInput);
    }

    private readonly Dictionary<string, RegisteredKeybind> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, KeyChord> _persistedOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<KeybindConflict> _conflicts = [];
    private readonly Lock _gate = new();
    private readonly Action<string, string, string>? _log;
    private long _nextSequence;

    /// <inheritdoc/>
    public string ConfigPath { get; }

    /// <inheritdoc/>
    public IReadOnlyList<KeybindEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _bindings.Values.OrderBy(b => b.Sequence).Select(b => b.ToSnapshot())];
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<KeybindConflict> Conflicts
    {
        get
        {
            lock (_gate)
            {
                return [.. _conflicts];
            }
        }
    }

    /// <inheritdoc/>
    public event Action<KeybindConflict>? ConflictDetected;

    /// <summary>Creates a new <see cref="KeybindRegistry"/> backed by <paramref name="configPath"/>.</summary>
    public KeybindRegistry(string? configPath = null, Action<string, string, string>? log = null)
    {
        ConfigPath = string.IsNullOrWhiteSpace(configPath)
            ? Path.Combine(AppContext.BaseDirectory, "Mods", "Client", "keybinds.json")
            : Path.GetFullPath(configPath);
        _log = log;
        Reload();
    }

    /// <inheritdoc/>
    public IDisposable Register(
        string id,
        string displayName,
        KeyChord defaultChord,
        Action onTriggered,
        bool allowWhenTextInputFocused = false,
        bool suppressGameInput = false) =>
        RegisterForMod("host", id, displayName, defaultChord, onTriggered, allowWhenTextInputFocused, suppressGameInput);

    internal IDisposable RegisterForMod(
        string modId,
        string id,
        string displayName,
        KeyChord defaultChord,
        Action onTriggered,
        bool allowWhenTextInputFocused = false,
        bool suppressGameInput = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(onTriggered);

        string trimmedId = id.Trim();
        List<KeybindConflict> newConflicts;
        lock (_gate)
        {
            if (_bindings.ContainsKey(trimmedId))
            {
                throw new InvalidOperationException($"Keybind '{trimmedId}' is already registered.");
            }

            KeyChord activeChord = _persistedOverrides.TryGetValue(trimmedId, out KeyChord overridden)
                ? overridden
                : defaultChord;

            var entry = new RegisteredKeybind
            {
                Id = trimmedId,
                ModId = modId,
                DisplayName = displayName,
                DefaultChord = defaultChord,
                CurrentChord = activeChord,
                Callback = onTriggered,
                AllowWhenTextInputFocused = allowWhenTextInputFocused,
                SuppressGameInput = suppressGameInput,
                Sequence = Interlocked.Increment(ref _nextSequence),
            };

            _bindings[trimmedId] = entry;
            newConflicts = RebuildConflictsLocked(trimmedId);
        }

        EmitConflicts(newConflicts);
        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                if (_bindings.Remove(trimmedId))
                {
                    RebuildConflictsLocked(null);
                }
            }
        });
    }

    /// <inheritdoc/>
    public KeyChord? GetBinding(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        lock (_gate)
        {
            if (_bindings.TryGetValue(id.Trim(), out RegisteredKeybind? entry))
            {
                return entry.CurrentChord;
            }

            if (_persistedOverrides.TryGetValue(id.Trim(), out KeyChord persisted))
            {
                return persisted;
            }

            return null;
        }
    }

    /// <inheritdoc/>
    public bool Rebind(string id, KeyChord newChord, out IReadOnlyList<KeybindConflict> conflicts)
    {
        conflicts = [];
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        string trimmedId = id.Trim();
        List<KeybindConflict> detected;
        lock (_gate)
        {
            if (!_bindings.TryGetValue(trimmedId, out RegisteredKeybind? entry))
            {
                return false;
            }

            entry.CurrentChord = newChord;
            if (newChord == entry.DefaultChord)
            {
                _persistedOverrides.Remove(trimmedId);
            }
            else
            {
                _persistedOverrides[trimmedId] = newChord;
            }

            detected = RebuildConflictsLocked(trimmedId);
        }

        Save();
        EmitConflicts(detected);
        conflicts = detected;
        return true;
    }

    /// <inheritdoc/>
    public bool ResetBinding(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        string trimmedId = id.Trim();
        List<KeybindConflict> detected;
        lock (_gate)
        {
            if (!_bindings.TryGetValue(trimmedId, out RegisteredKeybind? entry))
            {
                return false;
            }

            entry.CurrentChord = entry.DefaultChord;
            _persistedOverrides.Remove(trimmedId);
            detected = RebuildConflictsLocked(trimmedId);
        }

        Save();
        EmitConflicts(detected);
        return true;
    }

    /// <inheritdoc/>
    public void Save()
    {
        Dictionary<string, string> serialized;
        lock (_gate)
        {
            serialized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, KeyChord> kv in _persistedOverrides.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                serialized[kv.Key] = kv.Value.ToString();
            }
        }

        string? dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(serialized, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    /// <inheritdoc/>
    public void Reload()
    {
        var loaded = new Dictionary<string, KeyChord>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(ConfigPath))
        {
            try
            {
                string json = File.ReadAllText(ConfigPath);
                Dictionary<string, string>? raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
                if (raw is not null)
                {
                    foreach (KeyValuePair<string, string> kv in raw)
                    {
                        if (!string.IsNullOrWhiteSpace(kv.Key) && KeyChord.TryParse(kv.Value, out KeyChord chord))
                        {
                            loaded[kv.Key.Trim()] = chord;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _log?.Invoke("WARN", "keybinds", $"Failed to load '{ConfigPath}': {ex.Message}");
            }
        }

        List<KeybindConflict> detected;
        lock (_gate)
        {
            _persistedOverrides.Clear();
            foreach (KeyValuePair<string, KeyChord> kv in loaded)
            {
                _persistedOverrides[kv.Key] = kv.Value;
            }

            foreach (RegisteredKeybind entry in _bindings.Values)
            {
                entry.CurrentChord = _persistedOverrides.TryGetValue(entry.Id, out KeyChord overridden)
                    ? overridden
                    : entry.DefaultChord;
            }

            detected = RebuildConflictsLocked(null);
        }

        EmitConflicts(detected);
    }

    internal bool TryDispatchKeyDown(int virtualKey, KeyModifiers modifiers, bool isTextInputFocused)
    {
        var chord = KeyChord.FromKey(virtualKey, modifiers);
        return DispatchMatchingChord(chord, isTextInputFocused);
    }

    internal bool TryDispatchMouseDown(MouseButton button, KeyModifiers modifiers, bool isTextInputFocused)
    {
        if (button == MouseButton.None)
        {
            return false;
        }

        var chord = KeyChord.FromMouse(button, modifiers);
        return DispatchMatchingChord(chord, isTextInputFocused);
    }

    private bool DispatchMatchingChord(KeyChord chord, bool isTextInputFocused)
    {
        if (chord.IsEmpty)
        {
            return false;
        }

        RegisteredKeybind[] matches;
        lock (_gate)
        {
            matches = [.. _bindings.Values
                .Where(b => b.CurrentChord == chord && (!isTextInputFocused || b.AllowWhenTextInputFocused))
                .OrderBy(b => b.Sequence)];
        }

        bool suppress = false;
        foreach (RegisteredKeybind kb in matches)
        {
            try
            {
                kb.Callback();
                if (kb.SuppressGameInput)
                {
                    suppress = true;
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke("ERROR", kb.ModId, $"Keybind '{kb.Id}' callback failed: {ex.Message}");
            }
        }

        return suppress;
    }

    private List<KeybindConflict> RebuildConflictsLocked(string? focusId)
    {
        _conflicts.Clear();
        List<KeybindConflict> focusConflicts = [];
        RegisteredKeybind[] ordered = [.. _bindings.Values.OrderBy(b => b.Sequence)];

        for (int i = 0; i < ordered.Length; i++)
        {
            if (ordered[i].CurrentChord.IsEmpty)
            {
                continue;
            }

            for (int j = i + 1; j < ordered.Length; j++)
            {
                if (ordered[i].CurrentChord == ordered[j].CurrentChord)
                {
                    bool secondIsFocus = focusId is not null && string.Equals(ordered[j].Id, focusId, StringComparison.OrdinalIgnoreCase);
                    string primary = secondIsFocus ? ordered[j].Id : ordered[i].Id;
                    string other = secondIsFocus ? ordered[i].Id : ordered[j].Id;
                    var conflict = new KeybindConflict(
                        primary,
                        other,
                        ordered[i].CurrentChord,
                        $"Keybind conflict on '{ordered[i].CurrentChord}': '{primary}' conflicts with '{other}'.");
                    _conflicts.Add(conflict);
                    if (focusId is null ||
                        string.Equals(ordered[i].Id, focusId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ordered[j].Id, focusId, StringComparison.OrdinalIgnoreCase))
                    {
                        focusConflicts.Add(conflict);
                    }
                }
            }
        }

        return focusConflicts;
    }

    private void EmitConflicts(List<KeybindConflict> conflicts)
    {
        foreach (KeybindConflict conflict in conflicts)
        {
            _log?.Invoke("WARN", "keybinds", conflict.Message);
            ConflictDetected?.Invoke(conflict);
        }
    }
}

/// <summary>
/// Low-level suppressible keyboard/mouse input service implementing <see cref="IInputService"/>.
/// Tracks suppressed mouse buttons so suppressing a button-down event (for example <c>Ctrl+Shift+RMB</c>)
/// cleanly suppresses its matching button-up event as well.
/// </summary>
public sealed class InputService : IInputService, IDisposable
{
    private sealed record KeyHandlerEntry(long Id, int Priority, bool AllowWhenTextInputFocused, Action<KeyEventArgs> Handler);
    private sealed record MouseHandlerEntry(long Id, int Priority, bool AllowWhenTextInputFocused, Action<MouseEventArgs> Handler);

    private readonly INativeBridge _bridge;
    private readonly KeybindRegistry _keybinds;
    private readonly List<KeyHandlerEntry> _keyHandlers = [];
    private readonly List<MouseHandlerEntry> _mouseHandlers = [];
    private readonly HashSet<MouseButton> _suppressedMouseButtons = [];
    private readonly Lock _gate = new();
    private readonly IDisposable _keySub;
    private readonly IDisposable _mouseSub;
    private long _nextHandlerId;
    private KeyModifiers _activeModifiers;
    private (int X, int Y) _mousePosition;
    private bool _disposed;

    /// <summary>Creates a new <see cref="InputService"/>.</summary>
    public InputService(INativeBridge bridge, KeybindRegistry keybinds)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _keybinds = keybinds ?? throw new ArgumentNullException(nameof(keybinds));
        _keySub = _bridge.RegisterKeyCallback(HandleNativeKey);
        _mouseSub = _bridge.RegisterMouseCallback(HandleNativeMouse);
    }

    /// <inheritdoc/>
    public bool IsTextInputFocused => _bridge.IsTextInputFocused();

    /// <inheritdoc/>
    public KeyModifiers ActiveModifiers => _activeModifiers;

    /// <inheritdoc/>
    public (int X, int Y) MousePosition => _mousePosition;

    /// <inheritdoc/>
    public IKeybindRegistry Keybinds => _keybinds;

    /// <summary>Concrete <see cref="KeybindRegistry"/> instance.</summary>
    public KeybindRegistry KeybindRegistry => _keybinds;

    /// <inheritdoc/>
    public IDisposable OnKey(Action<KeyEventArgs> handler, int priority = 0, bool allowWhenTextInputFocused = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        long id = Interlocked.Increment(ref _nextHandlerId);
        var entry = new KeyHandlerEntry(id, priority, allowWhenTextInputFocused, handler);
        lock (_gate)
        {
            _keyHandlers.Add(entry);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _keyHandlers.RemoveAll(e => e.Id == id);
            }
        });
    }

    /// <inheritdoc/>
    public IDisposable OnMouse(Action<MouseEventArgs> handler, int priority = 0, bool allowWhenTextInputFocused = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        long id = Interlocked.Increment(ref _nextHandlerId);
        var entry = new MouseHandlerEntry(id, priority, allowWhenTextInputFocused, handler);
        lock (_gate)
        {
            _mouseHandlers.Add(entry);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _mouseHandlers.RemoveAll(e => e.Id == id);
            }
        });
    }

    private bool HandleNativeKey(KeyEventArgs args)
    {
        if (_disposed)
        {
            return false;
        }

        _activeModifiers = args.Modifiers;
        bool textFocused = args.IsTextInputFocused || _bridge.IsTextInputFocused();

        KeyHandlerEntry[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _keyHandlers.OrderBy(h => h.Priority).ThenBy(h => h.Id)];
        }

        foreach (KeyHandlerEntry entry in snapshot)
        {
            if (textFocused && !entry.AllowWhenTextInputFocused)
            {
                continue;
            }

            try
            {
                entry.Handler(args);
            }
            catch
            {
                // Fail-open on unhandled handler exception.
            }
        }

        if (!args.Handled && args.IsDown && !args.IsRepeat)
        {
            if (_keybinds.TryDispatchKeyDown(args.VirtualKey, args.Modifiers, textFocused))
            {
                args.Handled = true;
            }
        }

        return args.Handled;
    }

    private bool HandleNativeMouse(MouseEventArgs args)
    {
        if (_disposed)
        {
            return false;
        }

        _activeModifiers = args.Modifiers;
        _mousePosition = (args.X, args.Y);
        bool textFocused = args.IsTextInputFocused || _bridge.IsTextInputFocused();

        // If the matching button-down was suppressed (e.g. Ctrl+Shift+RMB), suppress button-up too
        // so PoE never executes OnMouseUp / OnRightClickDispatch (0x58/0x71/0x72).
        if (args.Kind == InputEventKind.Up && args.Button != MouseButton.None)
        {
            bool wasSuppressed;
            lock (_gate)
            {
                wasSuppressed = _suppressedMouseButtons.Remove(args.Button);
            }

            if (wasSuppressed)
            {
                args.Handled = true;
                return true;
            }
        }

        MouseHandlerEntry[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _mouseHandlers.OrderBy(h => h.Priority).ThenBy(h => h.Id)];
        }

        foreach (MouseHandlerEntry entry in snapshot)
        {
            if (textFocused && !entry.AllowWhenTextInputFocused)
            {
                continue;
            }

            try
            {
                entry.Handler(args);
            }
            catch
            {
                // Fail-open on unhandled handler exception.
            }
        }

        if (!args.Handled && args.Kind == InputEventKind.Down && args.Button != MouseButton.None)
        {
            if (_keybinds.TryDispatchMouseDown(args.Button, args.Modifiers, textFocused))
            {
                args.Handled = true;
            }
        }

        if (args.Handled && args.Kind == InputEventKind.Down && args.Button != MouseButton.None)
        {
            lock (_gate)
            {
                _suppressedMouseButtons.Add(args.Button);
            }
        }

        return args.Handled;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _keySub.Dispose();
        _mouseSub.Dispose();
        lock (_gate)
        {
            _keyHandlers.Clear();
            _mouseHandlers.Clear();
            _suppressedMouseButtons.Clear();
        }
    }
}
