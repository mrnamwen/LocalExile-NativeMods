using System.IO.Compression;
using System.Text;
using LocalExile.Client.Waypoint;
using Xunit;

namespace LocalExile.Client.Waypoint.Tests;

public sealed class WaypointInstallerAndExtenderTests
{
    private sealed class FakePePatcherBridge : IPePatcherBridge
    {
        public bool IsPatched { get; private set; }
        public string? PatchedHostDll { get; private set; }
        public byte[]? OriginalBytes { get; private set; }
        public bool FailNextPatch { get; set; }

        public Task<PePatchResult> CheckAsync(string patchedExePath, CancellationToken ct = default)
        {
            if (!File.Exists(patchedExePath))
                return Task.FromResult(new PePatchResult(false, "Executable missing."));
            return Task.FromResult(new PePatchResult(true, "Valid 3.29.3d executable.", IsPatched, "3.29.3d",
                DualModPackageValidator.ComputeFileSha256(patchedExePath)));
        }

        public Task<PePatchResult> PatchAsync(string patchedExePath, string hostDllName, CancellationToken ct = default)
        {
            if (FailNextPatch)
                return Task.FromResult(new PePatchResult(false, "Simulated patch failure."));
            OriginalBytes ??= File.ReadAllBytes(patchedExePath);
            byte[] patched = [.. OriginalBytes, .. Encoding.ASCII.GetBytes($"\0IMPORT:{hostDllName}\0")];
            File.WriteAllBytes(patchedExePath, patched);
            IsPatched = true;
            PatchedHostDll = hostDllName;
            return Task.FromResult(new PePatchResult(true, "Patched successfully.", true, "3.29.3d",
                DualModPackageValidator.ComputeFileSha256(patchedExePath)));
        }

        public Task<PePatchResult> UnpatchAsync(string patchedExePath, CancellationToken ct = default)
        {
            if (OriginalBytes is not null)
                File.WriteAllBytes(patchedExePath, OriginalBytes);
            IsPatched = false;
            PatchedHostDll = null;
            return Task.FromResult(new PePatchResult(true, "Restored original bytes.", false, "3.29.3d",
                DualModPackageValidator.ComputeFileSha256(patchedExePath)));
        }

        public Task<PePatchResult> VerifyAsync(string patchedExePath, CancellationToken ct = default) =>
            Task.FromResult(new PePatchResult(true, IsPatched ? "Import entry verified." : "Not patched.", IsPatched, "3.29.3d"));
    }

    [Fact]
    public void Installer_FullInstallUpgradeUninstallLifecycle_EncodesOverlaysAndNeverTouchesStockClient()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "nm-install-test-" + Guid.NewGuid().ToString("N"));
        string serverRoot = Path.Combine(workspace, "LocalExile");
        string pkgV1 = Path.Combine(workspace, "pkg-v1");
        string pkgV2 = Path.Combine(workspace, "pkg-v2");

        try
        {
            // Seed realistic LocalExile directory with stock Client/ files that MUST NEVER be modified
            string clientDir = Path.Combine(serverRoot, "Client");
            Directory.CreateDirectory(Path.Combine(clientDir, "Bundles2"));
            byte[] stockExeBytes = Encoding.UTF8.GetBytes("STOCK_GAME_EXE_3_29_3D");
            byte[] stockIndexBytes = Encoding.UTF8.GetBytes("STOCK_INDEX_BIN_BYTES");
            File.WriteAllBytes(Path.Combine(clientDir, "Game.exe"), stockExeBytes);
            File.WriteAllBytes(Path.Combine(clientDir, "Bundles2", "_.index.bin"), stockIndexBytes);

            // Build v1 dual-side mod package from real examples/DualMod.HarvestGarden
            CopyDirectory(Path.Combine(FindRepoRoot(), "examples", "DualMod.HarvestGarden"), pkgV1);
            File.WriteAllBytes(Path.Combine(pkgV1, "harvest-garden.dll"), "FAKE_SERVER_DLL_V1"u8.ToArray());
            File.WriteAllBytes(Path.Combine(pkgV1, "harvest-garden.Client.dll"), "FAKE_CLIENT_DLL_V1"u8.ToArray());

            var installer = new WaypointModInstaller();
            var options = new InstallOptions
            {
                CurrentSdkVersion = SemVer.Parse("0.1.0-poc"),
                PackClientBundle = true,
                DeployLooseOverlays = true
            };

            // 1. Install v1
            var reportV1 = installer.Install(pkgV1, serverRoot, options);
            Assert.True(reportV1.Succeeded, reportV1.ToHumanReadableSummary());
            Assert.False(reportV1.IsUpgrade);
            Assert.Equal("harvest-garden", reportV1.ModId);
            Assert.Equal("1.0.0", reportV1.Version);
            Assert.False(reportV1.RawAccessRequested);
            Assert.NotNull(reportV1.BundleFile);
            Assert.Contains("NativeMods Install Audit", reportV1.ToHumanReadableSummary());

            // Verify server deployment & mod-state.txt
            string serverModDir = Path.Combine(serverRoot, "Mods", "Binaries", "harvest-garden");
            Assert.True(File.Exists(Path.Combine(serverModDir, "mod.json")));
            Assert.True(File.Exists(Path.Combine(serverModDir, "harvest-garden.dll")));
            Assert.False(File.Exists(Path.Combine(serverModDir, "harvest-garden.Client.dll"))); // client DLL stays out of server dir
            var choices = WaypointModInstaller.ReadModStateChoices(Path.Combine(serverRoot, "Mods", "Binaries"));
            Assert.True(choices["harvest-garden"]);

            // Verify client deployment: UTF-16LE BOM CRLF loose overlay + deterministic verified bundle
            string clientModDir = Path.Combine(serverRoot, "Mods", "Client", "harvest-garden");
            Assert.True(File.Exists(Path.Combine(clientModDir, "harvest-garden.Client.dll")));
            string deployedUi = Path.Combine(clientModDir, "files", "metadata", "ui", "mods", "garden", "stockpile.ui");
            Assert.True(File.Exists(deployedUi));
            byte[] uiBytes = File.ReadAllBytes(deployedUi);
            Assert.Equal(0xFF, uiBytes[0]);
            Assert.Equal(0xFE, uiBytes[1]);
            string uiDecoded = Encoding.Unicode.GetString(uiBytes, 2, uiBytes.Length - 2);
            Assert.Contains("\r\n", uiDecoded);
            Assert.Contains("garden_stockpile_window", uiDecoded);

            string deployedBundle = Path.Combine(clientModDir, reportV1.BundleFile!);
            string deployedManifest = Path.Combine(clientModDir, "manifest.json");
            Assert.True(ModBundlePacker.Verify(deployedBundle, deployedManifest).IsValid);

            // Verify ledger recorded the mod
            var ledgerAfterV1 = installer.ReadLedger(serverRoot);
            Assert.Single(ledgerAfterV1.Mods);
            Assert.Equal("1.0.0", ledgerAfterV1.Mods[0].Version);

            // 2. Upgrade to v1.1.0 with an additional stock replace and verify stale files are cleaned
            CopyDirectory(pkgV1, pkgV2);
            var v2Manifest = DualModManifest.LoadFromFile(Path.Combine(pkgV2, "mod.json")) with { Version = "1.1.0" };
            File.WriteAllText(Path.Combine(pkgV2, "mod.json"), v2Manifest.ToJson());
            File.WriteAllBytes(Path.Combine(pkgV2, "harvest-garden.dll"), "FAKE_SERVER_DLL_V2"u8.ToArray());

            var reportV2 = installer.Install(pkgV2, serverRoot, options);
            Assert.True(reportV2.Succeeded, reportV2.ToHumanReadableSummary());
            Assert.True(reportV2.IsUpgrade);
            Assert.Equal("1.0.0", reportV2.PreviousVersion);
            Assert.Equal("1.1.0", reportV2.Version);
            Assert.Equal("FAKE_SERVER_DLL_V2", File.ReadAllText(Path.Combine(serverModDir, "harvest-garden.dll")));

            // 3. Install conflicting second mod -> must be blocked and reported in InstallAuditReport
            string conflictingPkg = Path.Combine(workspace, "pkg-conflict");
            Directory.CreateDirectory(conflictingPkg);
            var conflictManifest = new DualModManifest
            {
                Id = "rival-garden",
                Name = "Rival Garden",
                Version = "1.0.0",
                CompatibleClientVersions = ["0.5.2-beta"],
                NativeClient = new NativeClientManifest
                {
                    SdkVersionRange = ">=0.1.0-poc <2.0.0",
                    CustomOpcodes = [new(1001, "RivalPlant")],
                    GealBuiltins = ["GardenRipen"]
                }
            };
            File.WriteAllText(Path.Combine(conflictingPkg, "mod.json"), conflictManifest.ToJson());

            var conflictReport = installer.Install(conflictingPkg, serverRoot, options);
            Assert.False(conflictReport.Succeeded);
            Assert.Equal(2, conflictReport.Conflicts.Count); // Opcode 1001 + GEAL GardenRipen
            Assert.Contains("Cross-Mod Conflicts (2)", conflictReport.ToHumanReadableSummary());

            // 4. Uninstall cleanly
            var uninstall = installer.Uninstall("harvest-garden", serverRoot);
            Assert.True(uninstall.Succeeded);
            Assert.True(uninstall.RemovedServerDir);
            Assert.True(uninstall.RemovedClientDir);
            Assert.False(Directory.Exists(serverModDir));
            Assert.False(Directory.Exists(clientModDir));
            Assert.Empty(installer.ReadLedger(serverRoot).Mods);
            Assert.False(WaypointModInstaller.ReadModStateChoices(Path.Combine(serverRoot, "Mods", "Binaries")).ContainsKey("harvest-garden"));

            // Stock client files MUST remain byte-for-byte untouched!
            Assert.Equal(stockExeBytes, File.ReadAllBytes(Path.Combine(clientDir, "Game.exe")));
            Assert.Equal(stockIndexBytes, File.ReadAllBytes(Path.Combine(clientDir, "Bundles2", "_.index.bin")));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Installer_InstallsFromZipAndBlocksUnapprovedRawAccess()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "nm-zip-test-" + Guid.NewGuid().ToString("N"));
        string serverRoot = Path.Combine(workspace, "LocalExile");
        string pkgDir = Path.Combine(workspace, "raw-mod");
        string zipPath = Path.Combine(workspace, "raw-mod-1.0.0.zip");
        Directory.CreateDirectory(pkgDir);

        try
        {
            var manifest = new DualModManifest
            {
                Id = "raw-mod",
                Name = "Raw Diagnostics",
                Version = "1.0.0",
                CompatibleClientVersions = ["0.5.2-beta"],
                NativeClient = new NativeClientManifest
                {
                    SdkVersionRange = ">=0.1.0-poc <2.0.0",
                    RawAccess = true,
                    RawAccessJustification = "Reads diagnostic frame timing struct for profiler overlay."
                }
            };
            File.WriteAllText(Path.Combine(pkgDir, "mod.json"), manifest.ToJson());
            File.WriteAllBytes(Path.Combine(pkgDir, "raw-mod.dll"), "SERVER_DLL"u8.ToArray());

            ZipFile.CreateFromDirectory(pkgDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);

            var installer = new WaypointModInstaller();

            // Blocked when AllowRawAccess = false
            var blocked = installer.InstallFromZip(zipPath, serverRoot, new InstallOptions
            {
                CurrentSdkVersion = SemVer.Parse("0.1.0-poc"),
                AllowRawAccess = false
            });
            Assert.False(blocked.Succeeded);
            Assert.True(blocked.RawAccessRequested);
            Assert.Contains("Raw Memory/Hook Access: YES", blocked.ToHumanReadableSummary());

            // Succeeds once player explicitly approves RawAccess
            var approved = installer.InstallFromZip(zipPath, serverRoot, new InstallOptions
            {
                CurrentSdkVersion = SemVer.Parse("0.1.0-poc"),
                AllowRawAccess = true
            });
            Assert.True(approved.Succeeded, approved.ToHumanReadableSummary());
            Assert.True(File.Exists(Path.Combine(serverRoot, "Mods", "Binaries", "raw-mod", "raw-mod.dll")));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Installer_RejectsZipSlipTraversalAndAuditsDeclaredStockReplaces()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "nm-sec-test-" + Guid.NewGuid().ToString("N"));
        string serverRoot = Path.Combine(workspace, "LocalExile");
        string badZip = Path.Combine(workspace, "evil.zip");
        string replacePkg = Path.Combine(workspace, "replace-pkg");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(replacePkg);

        try
        {
            // 1. Zip-slip traversal archive must throw InvalidDataException before extracting
            using (var archive = ZipFile.Open(badZip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../../escape.dll");
                using var w = new StreamWriter(entry.Open());
                w.Write("evil");
            }

            var installer = new WaypointModInstaller();
            Assert.Throws<InvalidDataException>(() => installer.InstallFromZip(badZip, serverRoot));

            // 2. Declared stock replace appears explicitly in InstallAuditReport
            var manifest = new DualModManifest
            {
                Id = "escape-theme",
                Name = "Escape Theme",
                Version = "1.0.0",
                CompatibleClientVersions = ["0.5.2-beta"],
                NativeClient = new NativeClientManifest
                {
                    SdkVersionRange = ">=0.1.0-poc <2.0.0",
                    PayloadDir = "client",
                    Replaces = ["Metadata/UI/InGameState/EscapeMenu.ui"]
                }
            };
            File.WriteAllText(Path.Combine(replacePkg, "mod.json"), manifest.ToJson());
            string overrideUi = Path.Combine(replacePkg, "client", "Metadata", "UI", "InGameState", "EscapeMenu.ui");
            Directory.CreateDirectory(Path.GetDirectoryName(overrideUi)!);
            File.WriteAllText(overrideUi, "// theme\n<DEFINITIONS>\nbegin Frame\n\tid = esc_override;\nend\n");

            var report = installer.Install(replacePkg, serverRoot, new InstallOptions
            {
                CurrentSdkVersion = SemVer.Parse("0.1.0-poc")
            });
            Assert.True(report.Succeeded, report.ToHumanReadableSummary());
            Assert.Contains("Metadata/UI/InGameState/EscapeMenu.ui", report.ReplacedStockPaths);
            Assert.Contains("[OVERRIDE] Metadata/UI/InGameState/EscapeMenu.ui", report.ToHumanReadableSummary());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ExtenderManager_InstallStatusAndRemove_WorksInBothImportTableAndProxyModes()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "nm-extender-test-" + Guid.NewGuid().ToString("N"));
        string serverRoot = Path.Combine(workspace, "LocalExile");
        string bundleSrc = Path.Combine(workspace, "extender-dist");
        string hostSrc = Path.Combine(bundleSrc, "host");
        string addrSrc = Path.Combine(bundleSrc, "addresses");

        Directory.CreateDirectory(Path.Combine(serverRoot, "Client"));
        Directory.CreateDirectory(hostSrc);
        Directory.CreateDirectory(addrSrc);

        try
        {
            byte[] stockGameExe = "STOCK_GAME_EXE"u8.ToArray();
            byte[] patchedGameExe = "LOCALEXILE_PATCHED_EXE"u8.ToArray();
            File.WriteAllBytes(Path.Combine(serverRoot, "Client", "Game.exe"), stockGameExe);
            File.WriteAllBytes(Path.Combine(serverRoot, "Client", "Game.patched.exe"), patchedGameExe);

            string shimSrc = Path.Combine(bundleSrc, "LocalExile.ClientHost.dll");
            string proxySrc = Path.Combine(bundleSrc, "version.dll");
            File.WriteAllBytes(shimSrc, "NATIVE_SHIM_DLL"u8.ToArray());
            File.WriteAllBytes(proxySrc, "PROXY_VERSION_DLL"u8.ToArray());
            File.WriteAllBytes(Path.Combine(hostSrc, "LocalExile.Client.Host.dll"), "MANAGED_HOST"u8.ToArray());
            File.WriteAllText(Path.Combine(addrSrc, "3.29.3d.json"), "{}");

            var patcher = new FakePePatcherBridge();
            var manager = new WaypointExtenderManager(patcher);

            // Initial status: not installed
            var initial = await manager.GetStatusAsync(serverRoot);
            Assert.False(initial.IsInstalled);
            Assert.True(initial.StockClientPresent);
            Assert.True(initial.PatchedClientPresent);

            // Install in primary ImportTable mode
            var install = await manager.InstallAsync(serverRoot, new ExtenderInstallOptions(
                ExtenderVersion: "0.1.0-poc",
                NativeShimDllSourcePath: shimSrc,
                ManagedHostBundleDir: hostSrc,
                Mode: ExtenderInstallMode.ImportTable,
                AddressLibraryDir: addrSrc));

            Assert.True(install.Succeeded, install.Message);
            Assert.True(install.Status.IsInstalled);
            Assert.True(install.Status.IsHealthy);
            Assert.True(install.Status.PeImportVerified);
            Assert.Equal("3.29.3d", install.Status.DetectedBuild);
            Assert.Equal("LocalExile.ClientHost.dll", patcher.PatchedHostDll);
            Assert.True(File.Exists(Path.Combine(serverRoot, "Client", "nativemods-host", "addresses", "3.29.3d.json")));

            // Remove restores Game.patched.exe byte-for-byte and removes shim + host bundle
            var removed = await manager.RemoveAsync(serverRoot);
            Assert.True(removed.Succeeded, removed.Message);
            Assert.False(removed.Status.IsInstalled);
            Assert.False(removed.Status.PeImportVerified);
            Assert.Equal(patchedGameExe, File.ReadAllBytes(Path.Combine(serverRoot, "Client", "Game.patched.exe")));
            Assert.Equal(stockGameExe, File.ReadAllBytes(Path.Combine(serverRoot, "Client", "Game.exe")));

            // Install in ProxyVersionDll fallback mode
            var proxyInstall = await manager.InstallAsync(serverRoot, new ExtenderInstallOptions(
                ExtenderVersion: "0.1.0-poc",
                NativeShimDllSourcePath: shimSrc,
                ManagedHostBundleDir: hostSrc,
                Mode: ExtenderInstallMode.ProxyVersionDll,
                ProxyVersionDllSourcePath: proxySrc));

            Assert.True(proxyInstall.Succeeded, proxyInstall.Message);
            Assert.True(proxyInstall.Status.IsHealthy);
            Assert.True(proxyInstall.Status.ProxyDllPresent);
            Assert.Equal(ExtenderInstallMode.ProxyVersionDll, proxyInstall.Status.Mode);

            // Remove proxy mode cleanly
            var proxyRemoved = await manager.RemoveAsync(serverRoot);
            Assert.True(proxyRemoved.Succeeded);
            Assert.False(File.Exists(Path.Combine(serverRoot, "Client", "version.dll")));
            Assert.Equal(stockGameExe, File.ReadAllBytes(Path.Combine(serverRoot, "Client", "Game.exe")));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "02-architecture.md")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Could not locate NativeMods repository root.");
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, file);
            if (rel.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
                continue;
            string target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
