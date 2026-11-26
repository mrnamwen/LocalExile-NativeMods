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
/// Specifies when a chained hook callback executes relative to the original target function.
/// </summary>
public enum HookPhase
{
    /// <summary>Executes before the original function; can inspect/modify arguments or set <see cref="HookContext.SkipOriginal"/>.</summary>
    Pre = 0,

    /// <summary>Executes after the original function; can inspect/modify the return value.</summary>
    Post = 1,
}

/// <summary>
/// Shared register/argument context passed to managed pre/post hook-chain callbacks (<c>nm_hook_context_t</c>).
/// </summary>
public sealed class HookContext
{
    /// <summary>Creates a new hook context for <paramref name="targetAddress"/>.</summary>
    public HookContext(nint targetAddress)
    {
        TargetAddress = targetAddress;
    }

    /// <summary>Positional integer/pointer arguments <c>0..7</c> (<c>RCX..R9</c> + stack).</summary>
    public ulong[] Args { get; } = new ulong[8];

    /// <summary>Raw 64-bit IEEE-754 bits of floating-point register arguments <c>XMM0..XMM3</c>.</summary>
    public ulong[] FloatArgs { get; } = new ulong[4];

    /// <summary>Integer/pointer return register (<c>RAX</c>), editable by pre-skip or post hooks.</summary>
    public ulong ReturnValue { get; set; }

    /// <summary>Raw 64-bit bits of floating-point return register (<c>XMM0</c>).</summary>
    public ulong ReturnFloatBits { get; set; }

    /// <summary>Floating-point return value interpreted as <see cref="double"/>.</summary>
    public double ReturnDouble
    {
        get => BitConverter.UInt64BitsToDouble(ReturnFloatBits);
        set => ReturnFloatBits = BitConverter.DoubleToUInt64Bits(value);
    }

    /// <summary>Floating-point return value interpreted as <see cref="float"/>.</summary>
    public float ReturnFloat
    {
        get => BitConverter.UInt32BitsToSingle(unchecked((uint)ReturnFloatBits));
        set => ReturnFloatBits = BitConverter.SingleToUInt32Bits(value);
    }

    /// <summary>The hooked native target address.</summary>
    public nint TargetAddress { get; }

    /// <summary>When set to <c>true</c> by a <see cref="HookPhase.Pre"/> callback, skips invoking the original function.</summary>
    public bool SkipOriginal { get; set; }

    /// <summary>Reads <c>XMM[index]</c> (<c>0..3</c>) as a <see cref="double"/>.</summary>
    public double GetDoubleArg(int index) => BitConverter.UInt64BitsToDouble(FloatArgs[index]);

    /// <summary>Writes a <see cref="double"/> into <c>XMM[index]</c> (<c>0..3</c>).</summary>
    public void SetDoubleArg(int index, double value) => FloatArgs[index] = BitConverter.DoubleToUInt64Bits(value);

    /// <summary>Reads <c>XMM[index]</c> (<c>0..3</c>) as a <see cref="float"/>.</summary>
    public float GetFloatArg(int index) => BitConverter.UInt32BitsToSingle(unchecked((uint)FloatArgs[index]));

    /// <summary>Writes a <see cref="float"/> into <c>XMM[index]</c> (<c>0..3</c>).</summary>
    public void SetFloatArg(int index, float value) => FloatArgs[index] = BitConverter.SingleToUInt32Bits(value);
}

/// <summary>
/// Represents a single Win64 native call argument (integer/pointer or <c>float</c>/<c>double</c> in <c>XMM0..XMM3</c>).
/// </summary>
/// <param name="RawBits">Raw 64-bit value or IEEE-754 bit pattern.</param>
/// <param name="IsFloat"><c>true</c> if passed in an <c>XMM</c> register when in slots <c>0..3</c>.</param>
public readonly record struct NativeCallArgument(ulong RawBits, bool IsFloat)
{
    /// <summary>Creates an integer/pointer argument.</summary>
    public static NativeCallArgument FromInt(nint value) => new(unchecked((ulong)value), false);

    /// <summary>Creates an unsigned 64-bit integer argument.</summary>
    public static NativeCallArgument FromUInt64(ulong value) => new(value, false);

    /// <summary>Creates a single-precision floating-point argument.</summary>
    public static NativeCallArgument FromFloat(float value) => new(BitConverter.SingleToUInt32Bits(value), true);

    /// <summary>Creates a double-precision floating-point argument.</summary>
    public static NativeCallArgument FromDouble(double value) => new(BitConverter.DoubleToUInt64Bits(value), true);

    /// <summary>Implicitly converts an integer/pointer value to a <see cref="NativeCallArgument"/>.</summary>
    public static implicit operator NativeCallArgument(nint value) => FromInt(value);
}

/// <summary>
/// Captures both integer (<c>RAX</c>) and floating-point (<c>XMM0</c>) return values from a guarded native call.
/// </summary>
/// <param name="RawInt">Raw <c>RAX</c> 64-bit result.</param>
/// <param name="RawFloatBits">Raw <c>XMM0</c> 64-bit IEEE-754 result.</param>
public readonly record struct NativeCallResult(ulong RawInt, ulong RawFloatBits)
{
    /// <summary>Interprets <see cref="RawInt"/> as a native pointer-sized integer.</summary>
    public nint AsIntPtr => unchecked((nint)RawInt);

    /// <summary>Interprets <see cref="RawInt"/> as a signed 64-bit integer.</summary>
    public long AsInt64 => unchecked((long)RawInt);

    /// <summary>Interprets <see cref="RawFloatBits"/> as a <see cref="double"/>.</summary>
    public double AsDouble => BitConverter.UInt64BitsToDouble(RawFloatBits);

    /// <summary>Interprets the low 32 bits of <see cref="RawFloatBits"/> as a <see cref="float"/>.</summary>
    public float AsFloat => BitConverter.UInt32BitsToSingle(unchecked((uint)RawFloatBits));
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
    /// Installs a native hook at <paramref name="symbolName"/> using an explicit <paramref name="vtableSlotIndex"/> when <paramref name="hookType"/> is <see cref="HookType.VtableSlot"/>.
    /// </summary>
    IDisposable InstallHook(
        string symbolName,
        nint detourTarget,
        out nint originalTrampoline,
        HookType hookType,
        uint vtableSlotIndex);

    /// <summary>Installs a raw detour on a vtable slot by vtable address and <paramref name="slotIndex"/>.</summary>
    IDisposable InstallVtableHook(
        nint vtableAddress,
        uint slotIndex,
        nint detourTarget,
        out nint originalSlot);

    /// <summary>Installs a shared multi-mod pre/post hook-chain callback on a verified symbol.</summary>
    IDisposable InstallHookChain(
        string symbolName,
        HookPhase phase,
        Action<HookContext> callback,
        int priority = 0);

    /// <summary>Installs a shared multi-mod pre/post hook-chain callback at an explicit address.</summary>
    IDisposable InstallHookChain(
        nint address,
        string? expectedBytesHex,
        HookPhase phase,
        Action<HookContext> callback,
        int priority = 0);

    /// <summary>Installs a shared multi-mod pre/post hook-chain callback on a vtable slot.</summary>
    IDisposable InstallVtableHookChain(
        nint vtableAddress,
        uint slotIndex,
        HookPhase phase,
        Action<HookContext> callback,
        int priority = 0);

    /// <summary>
    /// Invokes a native Win64 function at <paramref name="symbolName"/> inside the shim's SEH/C++ exception wall.
    /// </summary>
    /// <param name="symbolName">Canonical symbol name to call.</param>
    /// <param name="args">Positional integer/pointer arguments (up to 8).</param>
    /// <returns>The raw 64-bit return register value.</returns>
    nint Call(string symbolName, params nint[] args);

    /// <summary>
    /// Invokes a native Win64 function at <paramref name="symbolName"/> with up to 8 mixed integer/float arguments inside the SEH/C++ exception wall.
    /// </summary>
    NativeCallResult CallEx(string symbolName, ReadOnlySpan<NativeCallArgument> args, bool returnIsFloat = false);

    /// <summary>
    /// Invokes a native Win64 function at <paramref name="targetAddress"/> with up to 8 mixed integer/float arguments inside the SEH/C++ exception wall.
    /// </summary>
    NativeCallResult CallEx(nint targetAddress, ReadOnlySpan<NativeCallArgument> args, bool returnIsFloat = false);
}
