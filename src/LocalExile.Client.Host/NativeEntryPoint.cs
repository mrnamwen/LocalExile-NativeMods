using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LocalExile.Client.Host.Native;

namespace LocalExile.Client.Host;

/// <summary>
/// Managed entry point invoked by the native shim's CoreCLR bootstrapper (<c>nm_bootstrap_managed_host</c>).
/// Matches the contract documented in <c>nativemods_abi.h</c> and <c>coreclr_bootstrap.cpp</c>:
/// <c>int LocalExile.Client.Host.NativeEntryPoint.Initialize(IntPtr abiTablePtr)</c>.
/// </summary>
public static class NativeEntryPoint
{
    private static readonly Lock Gate = new();

    /// <summary>Gets the active managed runtime booted via <see cref="Initialize"/>, if any.</summary>
    public static ClientHostRuntime? ActiveRuntime { get; private set; }

    /// <summary>
    /// Native bootstrap entry point invoked by <c>LocalExile.ClientHost.dll</c>.
    /// </summary>
    /// <param name="abiTablePtr">Pointer to a valid <c>nm_abi_table_t</c> structure.</param>
    /// <returns><c>0</c> (<see cref="NmStatus.Ok"/>) on success, or a negative <see cref="NmStatus"/> error code.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Initialize(nint abiTablePtr)
    {
        return InitializeManaged(abiTablePtr);
    }

    /// <summary>
    /// Managed-callable entry point mirroring <see cref="Initialize"/> (used for testing and direct host activation).
    /// </summary>
    public static int InitializeManaged(nint abiTablePtr, ClientHostOptions? options = null)
    {
        if (abiTablePtr == nint.Zero)
        {
            return (int)NmStatus.ErrInvalidArg;
        }

        try
        {
            unsafe
            {
                NmAbiTable* table = (NmAbiTable*)abiTablePtr;
                if (table->StructSize < (uint)sizeof(NmAbiTable) || table->AbiVersion != NativeShimConstants.AbiVersion)
                {
                    return (int)NmStatus.ErrInvalidArg;
                }
            }

            lock (Gate)
            {
                ActiveRuntime?.Dispose();
                var bridge = new NativeShimBridge(abiTablePtr);
                var runtime = new ClientHostRuntime(bridge, options);
                runtime.Start();
                ActiveRuntime = runtime;
                return (int)NmStatus.Ok;
            }
        }
        catch
        {
            return (int)NmStatus.ErrRuntimeBootFailed;
        }
    }

    /// <summary>Shuts down the active managed host runtime if one was booted.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            ActiveRuntime?.Dispose();
            ActiveRuntime = null;
        }
    }
}
