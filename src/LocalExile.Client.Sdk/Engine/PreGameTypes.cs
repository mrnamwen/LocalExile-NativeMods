using System.Collections.ObjectModel;
using System.Text;

namespace LocalExile.Client.Sdk.Engine;

/// <summary>
/// Mutable context passed to <see cref="IPreGameService.CharacterCreating"/> when the client prepares
/// an outgoing <c>C→S 0x12</c> (<c>CreateCharacter</c>) packet on the Login TCP connection.
/// Mods attach structured key/value options formatted as <c>{BaseStartState}|nm:k=v&amp;...</c>
/// matching LocalExile <c>LoginServerSession.ParseStartState</c>.
/// </summary>
public sealed class CharacterCreateContext
{
    /// <summary>Delimiter separating <see cref="BaseStartState"/> from structured NativeMods options.</summary>
    public const string OptionDelimiter = "|nm:";

    /// <summary>Maximum length of the formatted option suffix (matches LocalExile <c>LoginServerSession.MaxOptionChars</c>).</summary>
    public const int MaxOptionChars = 1024;

    /// <summary>Maximum number of distinct key/value options permitted (matches LocalExile <c>LoginServerSession.MaxOptionCount</c>).</summary>
    public const int MaxOptionCount = 32;

    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Requested character name.</summary>
    public string Name { get; }

    /// <summary>Target league identifier.</summary>
    public string League { get; }

    /// <summary>Stock <c>CharacterStartStates.dat</c> base identifier (for example <c>DexDefault</c>).</summary>
    public string BaseStartState { get; }

    /// <summary>Read-only view of structured key/value options to append to <c>CreateCharacter.S78</c>.</summary>
    public IReadOnlyDictionary<string, string> Options { get; }

    /// <summary>Creates a new <see cref="CharacterCreateContext"/>, automatically splitting any existing <c>|nm:</c> suffix.</summary>
    public CharacterCreateContext(string name, string league, string rawStartState)
    {
        Name = name ?? string.Empty;
        League = league ?? string.Empty;
        if (TryParseStartState(rawStartState ?? string.Empty, out string baseState, out IReadOnlyDictionary<string, string> existing))
        {
            foreach (KeyValuePair<string, string> kv in existing)
            {
                if (_options.Count < MaxOptionCount)
                {
                    _options[kv.Key] = kv.Value;
                }
            }
        }

        BaseStartState = baseState;
        Options = new ReadOnlyDictionary<string, string>(_options);
    }

    /// <summary>
    /// Sets or updates a structured character-creation option with key/value validation and wire-length bounds enforcement.
    /// </summary>
    /// <param name="key">Option key (non-empty ASCII alphanumeric, <c>_</c>, <c>-</c>, or <c>.</c>).</param>
    /// <param name="value">Option value (no control characters).</param>
    public void SetOption(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        string trimmedKey = key.Trim();
        ValidateKey(trimmedKey);
        ValidateValue(value);

        bool isNew = !_options.ContainsKey(trimmedKey);
        if (isNew && _options.Count >= MaxOptionCount)
        {
            throw new InvalidOperationException($"Cannot add option '{trimmedKey}': exceeds maximum of {MaxOptionCount} character creation options.");
        }

        bool hadPrev = _options.TryGetValue(trimmedKey, out string? prevValue);
        _options[trimmedKey] = value;

        string tail = FormatOptionsTail(_options);
        if (tail.Length > MaxOptionChars)
        {
            if (hadPrev)
            {
                _options[trimmedKey] = prevValue!;
            }
            else
            {
                _options.Remove(trimmedKey);
            }

            throw new InvalidOperationException(
                $"Setting option '{trimmedKey}' would exceed the {MaxOptionChars}-character start-state option limit ({tail.Length} chars).");
        }
    }

    /// <summary>Removes a previously set option by key.</summary>
    public bool RemoveOption(string key) => !string.IsNullOrWhiteSpace(key) && _options.Remove(key.Trim());

    /// <summary>Tries to read a previously set option value.</summary>
    public bool TryGetOption(string key, out string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            value = null;
            return false;
        }

        return _options.TryGetValue(key.Trim(), out value);
    }

    /// <summary>
    /// Formats the final <c>CreateCharacter.S78</c> start-state string.
    /// Returns <see cref="BaseStartState"/> unchanged when no options are set, preserving 100% stock compatibility.
    /// </summary>
    public string FormatStartState()
    {
        if (_options.Count == 0)
        {
            return BaseStartState;
        }

        string tail = FormatOptionsTail(_options);
        if (tail.Length > MaxOptionChars)
        {
            throw new InvalidOperationException($"Formatted character creation options exceed {MaxOptionChars} characters.");
        }

        return string.Concat(BaseStartState, OptionDelimiter, tail);
    }

    /// <summary>
    /// Splits an optional <c>|nm:k=v&amp;...</c> suffix from a <c>CreateCharacter.S78</c> string,
    /// matching LocalExile <c>LoginServerSession.ParseStartState</c>.
    /// </summary>
    public static bool TryParseStartState(
        string? rawStartState,
        out string baseStartState,
        out IReadOnlyDictionary<string, string> options)
    {
        if (string.IsNullOrEmpty(rawStartState))
        {
            baseStartState = string.Empty;
            options = ReadOnlyDictionary<string, string>.Empty;
            return false;
        }

        int idx = rawStartState.IndexOf(OptionDelimiter, StringComparison.Ordinal);
        if (idx < 0)
        {
            baseStartState = rawStartState;
            options = ReadOnlyDictionary<string, string>.Empty;
            return false;
        }

        baseStartState = rawStartState[..idx];
        string tail = rawStartState[(idx + OptionDelimiter.Length)..];
        if (tail.Length == 0)
        {
            options = ReadOnlyDictionary<string, string>.Empty;
            return true;
        }

        if (tail.Length > MaxOptionChars)
        {
            tail = tail[..MaxOptionChars];
        }

        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in tail.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (parsed.Count >= MaxOptionCount)
            {
                break;
            }

            int eq = part.IndexOf('=');
            string rawKey = eq >= 0 ? part[..eq].Trim() : part;
            if (rawKey.Length == 0)
            {
                continue;
            }

            string rawVal = eq >= 0 ? part[(eq + 1)..].Trim() : string.Empty;
            string key = SafeUnescape(rawKey);
            if (key.Length == 0)
            {
                continue;
            }

            parsed[key] = SafeUnescape(rawVal);
        }

        options = parsed.Count == 0
            ? ReadOnlyDictionary<string, string>.Empty
            : new ReadOnlyDictionary<string, string>(parsed);
        return true;
    }

    private static string FormatOptionsTail(Dictionary<string, string> options)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (KeyValuePair<string, string> kv in options.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!first)
            {
                sb.Append('&');
            }

            first = false;
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kv.Value));
        }

        return sb.ToString();
    }

    private static void ValidateKey(string key)
    {
        if (key.Length is 0 or > 64)
        {
            throw new ArgumentException("Option key must be between 1 and 64 characters.", nameof(key));
        }

        foreach (char c in key)
        {
            bool valid = (c is >= 'a' and <= 'z')
                || (c is >= 'A' and <= 'Z')
                || (c is >= '0' and <= '9')
                || c is '_' or '-' or '.';
            if (!valid)
            {
                throw new ArgumentException($"Option key '{key}' contains invalid character '{c}'. Use ASCII letters, digits, '_', '-', or '.'.", nameof(key));
            }
        }
    }

    private static void ValidateValue(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                throw new ArgumentException("Option value must not contain control characters.", nameof(value));
            }
        }
    }

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}

/// <summary>
/// Event payload raised when the Login server replies with <c>S→C 0x13</c> (<c>CreateCharacterResult</c>).
/// </summary>
/// <param name="Name">Created character name (if available).</param>
/// <param name="League">Target league name (if available).</param>
/// <param name="ResultCode">Server result code (<c>0</c> indicates success).</param>
public sealed record CharacterCreatedEventArgs(string Name, string League, ushort ResultCode)
{
    /// <summary>Returns <c>true</c> when <see cref="ResultCode"/> is <c>0</c>.</summary>
    public bool Succeeded => ResultCode == 0;
}

/// <summary>
/// Pre-game Login-TCP character creation hooks (<c>C→S 0x12</c> / <c>S→C 0x13</c>).
/// </summary>
public interface IPreGameService
{
    /// <summary>
    /// Raised synchronously on the game thread right before <c>LoginClient::WriteCreateCharacter_0x12</c>
    /// serializes <c>CreateCharacter.S78</c>, allowing mods to attach structured options via <see cref="CharacterCreateContext.SetOption"/>.
    /// </summary>
    event Action<CharacterCreateContext>? CharacterCreating;

    /// <summary>
    /// Raised on the game thread when <c>CreateCharacterState::OnCreateResult</c> (<c>S→C 0x13</c>) completes.
    /// </summary>
    event Action<CharacterCreatedEventArgs>? CharacterCreated;
}
