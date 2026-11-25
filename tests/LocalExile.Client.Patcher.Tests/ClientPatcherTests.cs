using System.Security.Cryptography;
using LocalExile.Client.Patcher;
using Xunit;

namespace LocalExile.Client.Patcher.Tests;

public sealed class ClientPatcherTests
{
    private static AddressLibraryCatalog LoadRepoCatalog()
    {
        string? dir = AddressLibraryCatalog.LocateDefaultDirectory();
        Assert.NotNull(dir);
        return AddressLibraryCatalog.LoadFromDirectory(dir!);
    }

    [Fact]
    public void Check_RejectsUnknownSyntheticBinary_WithoutAllowSyntheticFlag()
    {
        byte[] pe = SyntheticPeBuilder.Build();
        var patcher = new ClientPatcher(LoadRepoCatalog());

        var check = patcher.CheckBytes(pe, allowSynthetic: false);
        Assert.True(check.IsValidPe32Plus);
        Assert.Equal(BuildMatchKind.Unknown, check.MatchKind);
        Assert.Equal(136, check.HeaderSlackBytes);
        Assert.Equal(3, check.ExistingImportCount);
        Assert.False(check.HasNativeModsImport);

        Assert.Throws<InvalidOperationException>(() =>
            patcher.PatchBytes(pe, new PatchOptions(AllowSynthetic: false)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Patch_Verify_Idempotent_Unpatch_RestoresByteForByte(bool includeAuthenticode, bool includeBoundImport)
    {
        byte[] original = SyntheticPeBuilder.Build(
            importedDlls: ["KERNEL32.dll", "VERSION.dll", "WINMM.dll", "USER32.dll"],
            includeAuthenticode: includeAuthenticode,
            includeBoundImport: includeBoundImport,
            headerSlackBytes: 136);

        string originalSha = Convert.ToHexStringLower(SHA256.HashData(original));
        var patcher = new ClientPatcher(LoadRepoCatalog());
        var opts = new PatchOptions(AllowSynthetic: true);

        // 1. Patch
        var (patched, patchRes) = patcher.PatchBytes(original, opts);
        Assert.True(patchRes.Patched);
        Assert.False(patchRes.AlreadyPatched);
        Assert.Equal(BuildMatchKind.SyntheticAllowed, patchRes.MatchKind);
        Assert.Equal(originalSha, patchRes.InputSha256);
        Assert.NotEqual(originalSha, patchRes.OutputSha256);
        Assert.Equal(5, patchRes.TotalImportCount);
        Assert.Equal(includeAuthenticode, patchRes.PreservedAuthenticodeForUnpatch);

        // 2. Verify patched PE structure, imports, directories, and checksum
        var verify = patcher.VerifyBytes(patched);
        Assert.True(verify.IsValid, verify.Message);
        Assert.True(verify.HasNativeModsImport);
        Assert.True(verify.HasUnpatchMetadata);
        Assert.Equal(5, verify.ImportCount);
        Assert.Equal(originalSha, verify.RecordedOriginalSha256);

        var parsedPatched = Pe32PlusImage.Parse(patched);
        Assert.Equal(4, parsedPatched.NumberOfSections);
        Assert.Equal(ClientPatcher.PatchSectionName, parsedPatched.Sections[^1].Name);
        Assert.Equal(0u, parsedPatched.SecurityDirectory.VirtualAddress);
        Assert.Equal(0u, parsedPatched.SecurityDirectory.Size);
        Assert.Equal(0u, parsedPatched.BoundImportDirectory.VirtualAddress);
        Assert.Equal(0u, parsedPatched.BoundImportDirectory.Size);

        var dllNames = parsedPatched.ReadImportDescriptors().Select(d => d.DllName).ToArray();
        Assert.Equal(
            ["KERNEL32.dll", "VERSION.dll", "WINMM.dll", "USER32.dll", ClientPatcher.DefaultHostDllName],
            dllNames);

        // 3. Idempotency: patching an already-patched image returns identical bytes
        var (patchedTwice, secondRes) = patcher.PatchBytes(patched, opts);
        Assert.False(secondRes.Patched);
        Assert.True(secondRes.AlreadyPatched);
        Assert.Equal(patched, patchedTwice);

        // 4. Unpatch: exact byte-for-byte restore (including Authenticode & BoundImport & CheckSum)
        var (restored, unpatchRes) = patcher.UnpatchBytes(patched);
        Assert.True(unpatchRes.Unpatched);
        Assert.True(unpatchRes.ExactByteRestoreVerified);
        Assert.Equal(originalSha, unpatchRes.RestoredSha256);
        Assert.Equal(original.Length, restored.Length);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void Patch_RejectsPeWithInsufficientHeaderSlack()
    {
        byte[] tightPe = SyntheticPeBuilder.Build(headerSlackBytes: 16);
        var patcher = new ClientPatcher();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            patcher.PatchBytes(tightPe, new PatchOptions(AllowSynthetic: true)));
        Assert.Contains("header slack", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsNonPeAndPe32X86Binaries()
    {
        Assert.Throws<InvalidDataException>(() => Pe32PlusImage.Parse([0x00, 0x01, 0x02]));

        byte[] pe = SyntheticPeBuilder.Build();
        // Flip machine to x86 (0x014C)
        int peOff = BitConverter.ToInt32(pe, 0x3C);
        pe[peOff + 4] = 0x4C;
        pe[peOff + 5] = 0x01;
        Assert.Throws<InvalidDataException>(() => Pe32PlusImage.Parse(pe));
    }

    [Fact]
    public void ProxyDll_Deploy_Verify_TamperDetect_AndRemove_LeavesExeUntouched()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "nm-proxy-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            string fakeProxySrc = Path.Combine(tempDir, "src_version.dll");
            string fakeHostSrc = Path.Combine(tempDir, "src_host.dll");
            string clientDir = Path.Combine(tempDir, "Client");
            File.WriteAllBytes(fakeProxySrc, "FAKE-VERSION-PROXY-DLL"u8.ToArray());
            File.WriteAllBytes(fakeHostSrc, "FAKE-CLIENTHOST-DLL"u8.ToArray());

            var patcher = new ClientPatcher();
            var manifest = patcher.DeployProxyDll(clientDir, fakeProxySrc, fakeHostSrc);
            Assert.Equal("version.dll", manifest.ProxyDllName);
            Assert.Equal(ClientPatcher.DefaultHostDllName, manifest.HostDllName);
            Assert.Contains("WINEDLLOVERRIDES=\"version=n,b\"", manifest.WineDllOverridesHint);

            var (ok, _, _) = patcher.VerifyProxyDll(clientDir);
            Assert.True(ok);

            // Tamper detection
            File.AppendAllText(Path.Combine(clientDir, "version.dll"), "TAMPER");
            var (okAfterTamper, tamperMsg, _) = patcher.VerifyProxyDll(clientDir);
            Assert.False(okAfterTamper);
            Assert.Contains("SHA-256 mismatch", tamperMsg);

            // Clean removal
            Assert.True(patcher.RemoveProxyDll(clientDir));
            Assert.False(File.Exists(Path.Combine(clientDir, "version.dll")));
            Assert.False(File.Exists(Path.Combine(clientDir, ClientPatcher.DefaultHostDllName)));
            Assert.False(File.Exists(Path.Combine(clientDir, ClientPatcher.ProxyManifestFileName)));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Cli_EndToEnd_CheckPatchVerifyUnpatchDumpAddresses()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "nm-cli-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            string exePath = Path.Combine(tempDir, "SyntheticGame.exe");
            string patchedPath = Path.Combine(tempDir, "SyntheticGame.patched.exe");
            string restoredPath = Path.Combine(tempDir, "SyntheticGame.restored.exe");

            byte[] original = SyntheticPeBuilder.Build(includeAuthenticode: true);
            File.WriteAllBytes(exePath, original);

            Assert.Equal(1, Program.Main(["check", exePath]));
            Assert.Equal(0, Program.Main(["check", exePath, "--allow-synthetic"]));
            Assert.Equal(0, Program.Main(["patch", exePath, "--output", patchedPath, "--allow-synthetic"]));
            Assert.Equal(0, Program.Main(["verify", patchedPath]));
            Assert.Equal(0, Program.Main(["unpatch", patchedPath, "--output", restoredPath]));
            Assert.Equal(original, File.ReadAllBytes(restoredPath));
            Assert.Equal(0, Program.Main(["dump-addresses", "--build", "3.29.3d", "--category", "frame"]));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
