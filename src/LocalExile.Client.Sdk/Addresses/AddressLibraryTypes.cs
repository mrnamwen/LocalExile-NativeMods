namespace LocalExile.Client.Sdk.Addresses;

/// <summary>
/// Identifies a supported Path of Exile 3.29 client executable build.
/// </summary>
public enum ClientBuildId
{
    /// <summary>An unrecognized or unsupported client executable build.</summary>
    Unknown = 0,

    /// <summary>Path of Exile 3.29.3c client build.</summary>
    Build3293c = 1,

    /// <summary>Path of Exile 3.29.3d client build (build 328228).</summary>
    Build3293d = 2,
}

/// <summary>
/// Describes a single named symbol entry in the 3.29 Address Library.
/// Because the client image has <c>DYNAMIC_BASE</c> (ASLR), addresses are stored as RVAs relative to the loaded image base.
/// </summary>
/// <param name="Name">Canonical symbol name (for example <c>frame.state_stack_pass_a_hook</c>, <c>ui.control_load</c>).</param>
/// <param name="Rva">Relative Virtual Address from the PE image base, or <c>null</c> when unverified on this build.</param>
/// <param name="Category">Logical subsystem category (for example <c>frame</c>, <c>ui</c>, <c>files</c>, <c>geal</c>, <c>rulesets</c>).</param>
/// <param name="Status">Verification status string (for example <c>live_verified</c>, <c>static_verified</c>, <c>doc_verified</c>, <c>code_shared_3_29_3c</c>, <c>verified</c>, or <c>unverified_3_29_3c</c>).</param>
/// <param name="ExpectedBytes">Optional lowercase hex string (may contain <c>??</c> wildcards) of verified bytes at the symbol RVA, or <c>null</c> if unpinned.</param>
/// <param name="Pattern">Optional wildcard byte pattern for RIP-relative global resolution.</param>
/// <param name="SourceDoc">Reference to the verification document or spike that established this entry.</param>
/// <param name="Signature">Optional human-readable C/Win64 function signature or layout description.</param>
/// <param name="Kind">Optional symbol kind (<c>code</c>, <c>data</c>, <c>vtable</c>, <c>import_slot</c>, <c>global_ref</c>).</param>
public sealed record AddressEntry(
    string Name,
    ulong? Rva,
    string Category,
    string Status = "verified",
    string? ExpectedBytes = null,
    string? Pattern = null,
    string? SourceDoc = null,
    string? Signature = null,
    string Kind = "code")
{
    /// <summary>
    /// Gets whether this symbol has a concrete RVA and is marked verified (or code-shared) for the active build.
    /// </summary>
    public bool IsVerified =>
        Rva.HasValue &&
        (string.Equals(Status, "verified", StringComparison.OrdinalIgnoreCase)
         || string.Equals(Status, "live_verified", StringComparison.OrdinalIgnoreCase)
         || string.Equals(Status, "static_verified", StringComparison.OrdinalIgnoreCase)
         || string.Equals(Status, "doc_verified", StringComparison.OrdinalIgnoreCase)
         || string.Equals(Status, "code_shared_3_29_3c", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Resolves named reverse-engineered engine symbols to verified RVAs and runtime pointers for the active 3.29 build.
/// </summary>
public interface IAddressLibrary
{
    /// <summary>The detected 3.29 client build identifier.</summary>
    ClientBuildId Build { get; }

    /// <summary>Human-readable client build version string (for example <c>3.29.3d</c> or <c>3.29.3c</c>).</summary>
    string BuildVersion { get; }

    /// <summary>Optional SHA-256 digest of the matched client executable.</summary>
    string? ExecutableSha256 { get; }

    /// <summary>The loaded base address of the client executable module in process memory.</summary>
    nint ImageBase { get; }

    /// <summary>All entries present in the loaded address database.</summary>
    IReadOnlyCollection<AddressEntry> Entries { get; }

    /// <summary>Attempts to retrieve metadata for a named symbol.</summary>
    /// <param name="symbolName">The canonical symbol name.</param>
    /// <param name="entry">The matching entry if found.</param>
    /// <returns><c>true</c> if an entry with <paramref name="symbolName"/> exists.</returns>
    bool TryGetEntry(string symbolName, out AddressEntry? entry);

    /// <summary>Attempts to resolve a verified RVA for a named symbol.</summary>
    /// <param name="symbolName">The canonical symbol name.</param>
    /// <param name="rva">The resolved RVA if the symbol exists and is verified.</param>
    /// <returns><c>true</c> if the symbol is verified and has an RVA; otherwise <c>false</c>.</returns>
    bool TryResolveRva(string symbolName, out ulong rva);

    /// <summary>Attempts to resolve a verified runtime pointer (<see cref="ImageBase"/> + RVA) for a named symbol.</summary>
    /// <param name="symbolName">The canonical symbol name.</param>
    /// <param name="address">The resolved runtime address.</param>
    /// <returns><c>true</c> if resolved; otherwise <c>false</c>.</returns>
    bool TryResolveAddress(string symbolName, out nint address);

    /// <summary>Resolves a verified runtime pointer for a named symbol, throwing if missing or unverified.</summary>
    /// <param name="symbolName">The canonical symbol name.</param>
    /// <returns>The runtime address (<see cref="ImageBase"/> + RVA).</returns>
    /// <exception cref="KeyNotFoundException">Thrown when <paramref name="symbolName"/> is not in the library.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the entry is unverified or lacks an RVA on the active build.</exception>
    nint ResolveAddress(string symbolName);
}
