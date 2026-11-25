namespace LocalExile.Client.Sdk.Raw;

/// <summary>
/// Specifies the native hook installation mechanism.
/// </summary>
public enum HookType
{
    /// <summary>Byte-verified inline detour trampoline at a function entry point.</summary>
    InlineDetour = 0,

    /// <summary>Virtual method table slot swap.</summary>
    VtableSlot = 1,
}

/// <summary>
/// Low-level raw memory, native call, and hook surface.
/// Only accessible to mods that explicitly declare <c>rawAccess: true</c> in their manifest.
/// </summary>
public interface IRawMemory
{
    /// <summary>Resolves a verified runtime address by canonical Address Library symbol name.</summary>
    /// <param name="symbolName">The canonical symbol name in the active 3.29 Address Library.</param>
    nint GetAddress(string symbolName);

    /// <summary>Reads an unmanaged value of type <typeparamref name="T"/> from <paramref name="address"/>.</summary>
    T Read<T>(nint address) where T : unmanaged;

    /// <summary>Writes an unmanaged value of type <typeparamref name="T"/> to <paramref name="address"/>.</summary>
    void Write<T>(nint address, T value) where T : unmanaged;

    /// <summary>Reads <paramref name="count"/> raw bytes starting at <paramref name="address"/>.</summary>
    byte[] ReadBytes(nint address, int count);

    /// <summary>Writes raw bytes starting at <paramref name="address"/>.</summary>
    void WriteBytes(nint address, ReadOnlySpan<byte> data);

    /// <summary>
    /// Installs a byte-verified native hook at <paramref name="symbolName"/>, returning a disposable handle
    /// that removes the hook when disposed (or when the owning mod unloads).
    /// </summary>
    /// <param name="symbolName">Canonical symbol name to hook.</param>
    /// <param name="detourTarget">Function pointer to the detour callback.</param>
    /// <param name="originalTrampoline">Receives the trampoline pointer for calling the original function.</param>
    /// <param name="hookType">Hook installation strategy.</param>
    IDisposable InstallHook(
        string symbolName,
        nint detourTarget,
        out nint originalTrampoline,
        HookType hookType = HookType.InlineDetour);

    /// <summary>
    /// Invokes a native Win64 function at <paramref name="symbolName"/> inside the shim's SEH/C++ exception wall.
    /// </summary>
    /// <param name="symbolName">Canonical symbol name to call.</param>
    /// <param name="args">Positional integer/pointer arguments.</param>
    /// <returns>The raw 64-bit return register value.</returns>
    nint Call(string symbolName, params nint[] args);
}
