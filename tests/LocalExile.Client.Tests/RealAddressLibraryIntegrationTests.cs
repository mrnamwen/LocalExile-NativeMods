using LocalExile.Client.Host.Addresses;
using LocalExile.Client.Sdk.Addresses;
using Xunit;

namespace LocalExile.Client.Tests;

public sealed class RealAddressLibraryIntegrationTests
{
    private const string Sha3293dBuild328228 = "e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d";
    private const string Sha3293cBuild327984 = "cc8aa38345be2a837575215d2ce142ebecf7b007345843265b335ebb8ac5964c";
    private const string Sha3293cBuild328126 = "01a22facf36886b39bca74793c0d8c7295b1495f1625b4ebc7b23c2de32a70cf";

    [Fact]
    public void Real3293dJson_LoadsAll110SymbolsOffsetsAndConstants()
    {
        string addressesDir = FindAddressesDirectory();
        nint rebasedBase = unchecked((nint)0x7FF6_4000_0000L);
        JsonAddressLibrary lib = JsonAddressLibrary.LoadFromFile(Path.Combine(addressesDir, "3.29.3d.json"), runtimeImageBase: rebasedBase);

        Assert.Equal(ClientBuildId.Build3293d, lib.Build);
        Assert.Equal("3.29.3d", lib.BuildVersion);
        Assert.Equal(110, lib.Entries.Count);
        Assert.Equal(29, lib.Offsets.Count);
        Assert.Equal(18, lib.Constants.Count);
        Assert.True(lib.MatchesExecutableSha256(Sha3293dBuild328228));

        // Every symbol on 3.29.3d is verified and resolves cleanly with ASLR rebase.
        foreach (AddressEntry entry in lib.Entries)
        {
            Assert.True(entry.IsVerified, $"Expected 3.29.3d symbol '{entry.Name}' to be verified (status={entry.Status}).");
            Assert.True(lib.TryResolveRva(entry.Name, out ulong rva));
            Assert.Equal(entry.Rva!.Value, rva);
            Assert.Equal(unchecked((nint)((ulong)rebasedBase + rva)), lib.ResolveAddress(entry.Name));
        }

        // Spot-check key ground-truth RVAs & constants.
        Assert.True(lib.TryResolveRva("frame.state_stack_pass_a_hook", out ulong frameRva));
        Assert.Equal(0x1528FBFul, frameRva);
        Assert.True(lib.TryResolveRva("rulesets.creator_table", out ulong rulesetTableRva));
        Assert.Equal(0x475DC70ul, rulesetTableRva);
        Assert.Equal("0x2c9", lib.Constants["rulesetS2cOpcode"]);
        Assert.Equal("0x2ca", lib.Constants["rulesetC2sOpcode"]);
        Assert.Equal("65535", lib.Constants["rulesetMaxPayload"]);
        Assert.Equal("724", lib.Constants["gealClientBuiltinCount"]);
        Assert.Equal("0x2fc", lib.Constants["reservedOpcodeMin"]);
    }

    [Fact]
    public void Real3293cJson_LoadsAll110SymbolsAndBlocksAll23UnverifiedEntries()
    {
        string addressesDir = FindAddressesDirectory();
        JsonAddressLibrary lib = JsonAddressLibrary.LoadFromFile(Path.Combine(addressesDir, "3.29.3c.json"));

        Assert.Equal(ClientBuildId.Build3293c, lib.Build);
        Assert.Equal("3.29.3c", lib.BuildVersion);
        Assert.Equal(110, lib.Entries.Count);
        Assert.Equal(2, lib.KnownBuilds.Count);
        Assert.True(lib.MatchesExecutableSha256(Sha3293cBuild327984));
        Assert.True(lib.MatchesExecutableSha256(Sha3293cBuild328126));

        AddressEntry[] unverified = [.. lib.Entries.Where(e => string.Equals(e.Status, "unverified_3_29_3c", StringComparison.Ordinal))];
        Assert.Equal(23, unverified.Length);

        foreach (AddressEntry entry in unverified)
        {
            Assert.Null(entry.Rva);
            Assert.False(entry.IsVerified);
            Assert.False(lib.TryResolveRva(entry.Name, out _));
            Assert.False(lib.TryResolveAddress(entry.Name, out _));
            Assert.Throws<InvalidOperationException>(() => lib.ResolveAddress(entry.Name));
        }

        AddressEntry[] verified = [.. lib.Entries.Where(e => e.IsVerified)];
        Assert.Equal(87, verified.Length);
        foreach (AddressEntry entry in verified)
        {
            Assert.True(lib.TryResolveRva(entry.Name, out ulong rva));
            Assert.True(rva > 0);
        }
    }

    [Fact]
    public void RealAddressesDirectory_TryLoadForExecutableSha256_MatchesAll3OfficialBuilds()
    {
        string addressesDir = FindAddressesDirectory();

        JsonAddressLibrary? lib3d = JsonAddressLibrary.TryLoadForExecutableSha256(addressesDir, Sha3293dBuild328228);
        JsonAddressLibrary? lib3cOrig = JsonAddressLibrary.TryLoadForExecutableSha256(addressesDir, Sha3293cBuild327984);
        JsonAddressLibrary? lib3cHotfix = JsonAddressLibrary.TryLoadForExecutableSha256(addressesDir, Sha3293cBuild328126);
        JsonAddressLibrary? unknown = JsonAddressLibrary.TryLoadForExecutableSha256(addressesDir, "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        Assert.NotNull(lib3d);
        Assert.Equal(ClientBuildId.Build3293d, lib3d!.Build);

        Assert.NotNull(lib3cOrig);
        Assert.Equal(ClientBuildId.Build3293c, lib3cOrig!.Build);

        Assert.NotNull(lib3cHotfix);
        Assert.Equal(ClientBuildId.Build3293c, lib3cHotfix!.Build);

        Assert.Null(unknown);
    }

    private static string FindAddressesDirectory()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "data", "addresses");
            if (File.Exists(Path.Combine(candidate, "3.29.3d.json")))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate data/addresses/3.29.3d.json.");
    }
}
