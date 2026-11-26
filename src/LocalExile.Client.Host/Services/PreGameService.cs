using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Engine;

namespace LocalExile.Client.Host.Services;

/// <summary>
/// Pre-game Login-TCP character creation hook service (<c>C→S 0x12</c> / <c>S→C 0x13</c>).
/// </summary>
public sealed class PreGameService : IPreGameService, IDisposable
{
    private readonly IDisposable _creatingSub;
    private readonly IDisposable _createdSub;
    private readonly Action<string, string, string>? _log;
    private bool _disposed;

    /// <summary>Creates a new <see cref="PreGameService"/> wired to <paramref name="bridge"/>.</summary>
    public PreGameService(INativeBridge bridge, Action<string, string, string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _log = log;
        _creatingSub = bridge.RegisterCharacterCreatingCallback(HandleNativeCharacterCreating);
        _createdSub = bridge.RegisterCharacterCreatedCallback(HandleNativeCharacterCreated);
    }

    /// <inheritdoc/>
    public event Action<CharacterCreateContext>? CharacterCreating;

    /// <inheritdoc/>
    public event Action<CharacterCreatedEventArgs>? CharacterCreated;

    private string? HandleNativeCharacterCreating(string name, string league, string rawStartState)
    {
        if (_disposed)
        {
            return null;
        }

        Action<CharacterCreateContext>? handlers = CharacterCreating;
        if (handlers is null)
        {
            return null;
        }

        var ctx = new CharacterCreateContext(name, league, rawStartState);
        foreach (Delegate d in handlers.GetInvocationList())
        {
            try
            {
                ((Action<CharacterCreateContext>)d)(ctx);
            }
            catch (Exception ex)
            {
                _log?.Invoke("ERROR", "pregame", $"CharacterCreating handler failed: {ex.Message}");
            }
        }

        string formatted = ctx.FormatStartState();
        return string.Equals(formatted, rawStartState, StringComparison.Ordinal) ? null : formatted;
    }

    private void HandleNativeCharacterCreated(string name, string league, ushort resultCode)
    {
        if (_disposed)
        {
            return;
        }

        Action<CharacterCreatedEventArgs>? handlers = CharacterCreated;
        if (handlers is null)
        {
            return;
        }

        var args = new CharacterCreatedEventArgs(name, league, resultCode);
        foreach (Delegate d in handlers.GetInvocationList())
        {
            try
            {
                ((Action<CharacterCreatedEventArgs>)d)(args);
            }
            catch (Exception ex)
            {
                _log?.Invoke("ERROR", "pregame", $"CharacterCreated handler failed: {ex.Message}");
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _creatingSub.Dispose();
        _createdSub.Dispose();
    }
}
