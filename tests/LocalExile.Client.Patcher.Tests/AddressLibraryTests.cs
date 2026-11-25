using LocalExile.Client.Patcher;
using Xunit;

namespace LocalExile.Client.Patcher.Tests;

public sealed class AddressLibraryTests
{
    [Fact]
    public void AddressLibrary_Loads3293dAnd3293c_WithExactVerifiedGroundTruth()
    {
        string? dir = AddressLibraryCatalog.LocateDefaultDirectory();
        Assert.NotNull(dir);
        var catalog = AddressLibraryCatalog.LoadFromDirectory(dir!);

        var doc3d = catalog.GetByGameVersion("3.29.3d");
        var doc3c = catalog.GetByGameVersion("3.29.3c");
        Assert.NotNull(doc3d);
        Assert.NotNull(doc3c);

        // Verified SHA-256 build hashes from LocalExile ClientBuilds.cs / Waypoint ClientLocator.cs
        Assert.Single(doc3d!.Builds);
        Assert.Equal("e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d", doc3d.Builds[0].ExecutableSha256);
        Assert.Equal(328228, doc3d.Builds[0].BuildNumber);

        Assert.Equal(2, doc3c!.Builds.Count);
        Assert.Contains(doc3c.Builds, b => b.BuildNumber == 327984 &&
            b.ExecutableSha256 == "cc8aa38345be2a837575215d2ce142ebecf7b007345843265b335ebb8ac5964c");
        Assert.Contains(doc3c.Builds, b => b.BuildNumber == 328126 &&
            b.ExecutableSha256 == "01a22facf36886b39bca74793c0d8c7295b1495f1625b4ebc7b23c2de32a70cf");

        // All required categories must be covered
        string[] requiredCategories =
        [
            "frame", "ui", "files", "geal", "rulesets", "network",
            "allocator", "runtime", "audio", "stats", "world",
        ];
        foreach (string cat in requiredCategories)
            Assert.Contains(doc3d.Symbols, s => s.Category == cat);

        // Key verified RVAs and expected bytes
        var frameHook = doc3d.Symbols.Single(s => s.Name == "frame.state_stack_pass_a_hook");
        Assert.Equal(0x1528fbfUL, frameHook.RvaValue);
        Assert.Equal("488b5d284883eb10", frameHook.ExpectedBytes);
        Assert.Equal("live_verified", frameHook.Status);

        var appGlobalRef = doc3d.Symbols.Single(s => s.Name == "network.app_global_ref");
        Assert.Equal(0x00f3b30UL, appGlobalRef.RvaValue);
        Assert.Equal("488b05????????488907488b05", appGlobalRef.ExpectedBytes);

        // Rulesets ground truth (10-verification.md §3)
        Assert.Equal("0x2c9", doc3d.Constants.RulesetS2cOpcode);
        Assert.Equal("0x2ca", doc3d.Constants.RulesetC2sOpcode);
        Assert.Equal(65535, doc3d.Constants.RulesetMaxPayload);
        Assert.Equal(175, doc3d.Constants.RulesetSlotCount);
        Assert.Equal(68, doc3d.Constants.RulesetFilledSlotCount);
        Assert.Equal(9, doc3d.Constants.RulesetVtableSlotCount);
        Assert.Equal(724, doc3d.Constants.GealClientBuiltinCount);
        Assert.Equal(1000, doc3d.Constants.GealWhileIterationCap);

        // Every symbol on 3.29.3d has consistent VA = ImageBase + RVA
        foreach (var s in doc3d.Symbols)
        {
            Assert.NotNull(s.RvaValue);
            Assert.NotNull(s.VaValue);
            Assert.Equal(doc3d.ImageBaseValue + s.RvaValue!.Value, s.VaValue!.Value);
        }

        // On 3.29.3c, unverified data/vtable/import_slot entries MUST be explicitly unverified_3_29_3c with null RVA
        var unverified3c = doc3c.Symbols.Where(s => s.Status == "unverified_3_29_3c").ToList();
        Assert.NotEmpty(unverified3c);
        foreach (var s in unverified3c)
        {
            Assert.Null(s.Rva);
            Assert.Null(s.Va);
            Assert.Null(s.ExpectedBytes);
            Assert.False(s.IsVerified);
        }

        // HncMask IS verified on both builds (LocalExile ClientBuilds.cs)
        var hnc3d = doc3d.Symbols.Single(s => s.Name == "network.hnc_mask");
        var hnc3c = doc3c.Symbols.Single(s => s.Name == "network.hnc_mask");
        Assert.Equal(0x2feaeb0UL, hnc3d.RvaValue);
        Assert.Equal(0x2feaed0UL, hnc3c.RvaValue);
    }
}
