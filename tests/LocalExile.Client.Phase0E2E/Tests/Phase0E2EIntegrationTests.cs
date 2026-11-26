using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DualMod.Phase0E2E.Server;
using DualMod.Phase0E2E.Shared;
using LocalExile.Client.AssetPipeline.Dat64;
using LocalExile.Client.Host;
using LocalExile.Client.Host.Addresses;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Host.Services;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Addresses;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using LocalExile.Client.ServerBridge;
using LocalExile.Client.Waypoint;
using Xunit;

namespace LocalExile.Client.Phase0E2E.Tests;

public sealed class Phase0E2EIntegrationTests
{
    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "NativeMods.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir) ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Could not locate NativeMods.slnx from AppContext.BaseDirectory.");
    }

    [Fact]
    public void Phase0UiLayouts_PassUiLayoutLinter_WithZeroErrors()
    {
        string root = FindRepoRoot();
        string uiDir = Path.Combine(root, "tests", "LocalExile.Client.Phase0E2E", "client", "Metadata", "UI", "Mods", "Phase0E2E");

        foreach (string file in new[] { "Phase0Probe.ui", "Phase0ChildCard.ui" })
        {
            string fullPath = Path.Combine(uiDir, file);
            Assert.True(File.Exists(fullPath), $"Missing UI file: {fullPath}");

            string text = File.ReadAllText(fullPath, Encoding.UTF8);
            var findings = UiLayoutLinter.Lint(text, fullPath);
            var errors = findings.Where(f => f.Severity == DiagnosticSeverity.Error).ToList();
            Assert.Empty(errors);
        }
    }

    [Fact]
    public void Phase0TextFixtures_And_ClientTextEncoding_HaveNoBomForSmMatAmd()
    {
        string root = FindRepoRoot();
        string uiDir = Path.Combine(root, "tests", "LocalExile.Client.Phase0E2E", "client", "Metadata", "UI", "Mods", "Phase0E2E");

        foreach (string ext in new[] { "sm", "mat", "amd" })
        {
            byte[] raw = File.ReadAllBytes(Path.Combine(uiDir, $"test_bom_free.{ext}"));
            Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF, $"UTF-8 BOM found in test_bom_free.{ext}");
            Assert.False(raw.Length >= 2 && ((raw[0] == 0xFF && raw[1] == 0xFE) || (raw[0] == 0xFE && raw[1] == 0xFF)), $"UTF-16 BOM found in test_bom_free.{ext}");
            Assert.True(raw.Length >= 4 && (raw.Length & 1) == 0 && raw[1] == 0x00, $"test_bom_free.{ext} must be BOM-free UTF-16LE");
            Assert.NotEmpty(Encoding.Unicode.GetString(raw));

            byte[] encoded = ClientTextEncoding.EncodeTextForPath($"Metadata/Test/sample.{ext}", "\uFEFFversion 2\nline2\n");
            Assert.False(encoded.Length >= 2 && encoded[0] == 0xFF && encoded[1] == 0xFE, $"Encoded .{ext} unexpectedly emitted UTF-16 BOM");
            Assert.True(encoded.Length >= 4 && (encoded.Length & 1) == 0 && encoded[1] == 0x00, $"Encoded .{ext} must be BOM-free UTF-16LE");
        }

        byte[] uiEncoded = ClientTextEncoding.EncodeTextForPath("Metadata/UI/Mods/Phase0E2E/Phase0Probe.ui", "// header\n<DEFINITIONS>\n");
        Assert.True(uiEncoded.Length >= 2 && uiEncoded[0] == 0xFF && uiEncoded[1] == 0xFE);
    }

    [Fact]
    public void Phase0Manifest_ParsesAndTargets020SdkRange()
    {
        string root = FindRepoRoot();
        string manifestPath = Path.Combine(root, "tests", "LocalExile.Client.Phase0E2E", "mod.json");
        var manifest = ClientModManifest.LoadFromFile(manifestPath);

        Assert.Equal("phase0-e2e", manifest.Id);
        Assert.True(manifest.Enabled);
        Assert.True(manifest.RawAccess);
        Assert.Equal("phase0-e2e.Client.dll", manifest.Assembly);
        Assert.Equal("client", manifest.PayloadDir);
        Assert.Equal(">=0.2.0 <2.0.0", manifest.SdkVersionRange);
    }

    [Fact]
    public void SdkAndAbi_AreBumpedTo020()
    {
        Assert.StartsWith("0.2.0", SdkVersion.Current, StringComparison.Ordinal);
        Assert.Equal(new Version(0, 2, 0), SdkVersion.Parsed);
        Assert.Equal(0x00000200u, NativeShimConstants.AbiVersion);
        Assert.True(SdkVersion.IsCompatible(">=0.2.0 <2.0.0"));
        Assert.False(SdkVersion.IsCompatible("=0.1.0-poc"));
    }

    [Fact]
    public void AddressLibrary_3293dIncludesHashToWstringView_And_3293cNullRvasDoNotThrow()
    {
        string root = FindRepoRoot();
        var lib3d = JsonAddressLibrary.LoadFromFile(Path.Combine(root, "data", "addresses", "3.29.3d.json"));
        Assert.True(lib3d.TryGetEntry("files.hash_to_wstring_view", out var entry));
        Assert.NotNull(entry);
        Assert.Equal(0x0e40af0ul, entry!.Rva);

        var lib3c = JsonAddressLibrary.LoadFromFile(Path.Combine(root, "data", "addresses", "3.29.3c.json"));
        Assert.NotNull(lib3c);
    }

    [Fact]
    public void SimulatedNativeBridge_SymbolsMatch3293dJson()
    {
        string root = FindRepoRoot();
        var lib3d = JsonAddressLibrary.LoadFromFile(Path.Combine(root, "data", "addresses", "3.29.3d.json"));
        using var sim = new SimulatedNativeBridge();

        foreach (string symbol in sim.GetRegisteredSymbolNames())
        {
            Assert.True(lib3d.TryGetEntry(symbol, out _), $"SimulatedNativeBridge symbol '{symbol}' missing from 3.29.3d.json");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(262143)]
    [InlineData(262144)]
    [InlineData(262145)]
    [InlineData(600 * 1024)]
    public void Nmb1Packer_And_FileOverlayService_RoundTripExactByteBoundaries(int payloadSize)
    {
        string tempDir = Path.Combine(AppContext.BaseDirectory, "phase0-nmb-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string inputDir = Path.Combine(tempDir, "in");
            string dataDir = Path.Combine(inputDir, "Data");
            Directory.CreateDirectory(dataDir);

            byte[] original = new byte[payloadSize];
            RandomNumberGenerator.Fill(original);
            File.WriteAllBytes(Path.Combine(dataDir, "probe.bin"), original);

            PackedBundleResult packed = ModBundlePacker.Pack("phase0-pack", "0.2.0", inputDir, tempDir);

            using var sim = new SimulatedNativeBridge();
            var overlay = new FileOverlayService(sim);
            using var reg = overlay.RegisterBundle(packed.BundlePath, packed.ManifestPath);

            byte[]? actual = overlay.ReadAllBytes("data/probe.bin");
            Assert.NotNull(actual);
            Assert.Equal(original, actual);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("Metadata/Items/Pets/Kiwi1")]
    [InlineData("Metadata/Pet/Mods/Maxwell/MaxwellPet")]
    [InlineData("Metadata/Items/Pets/Caf\u00e9_Pet")]
    public void PoeHashing_And_PoeContentHash_MatchAcrossAsciiAndNonAscii(string path)
    {
        uint h1 = PoeHashing.Hash32(path);
        uint h2 = PoeContentHash.MurmurHash2(path);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void DualModPackageValidator_IncludesNewAppendableTables()
    {
        foreach (string table in new[] { "ItemVisualIdentity", "CurrencyItems", "MtxTypes", "MtxTypeGameSpecific", "Pet" })
        {
            Assert.Contains(table, DualModPackageValidator.AppendableRegistryTables, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Managed020Apis_TextBoxCheckBoxClipboardLayoutRawNetAndStockFiles_WorkEndToEnd()
    {
        string root = FindRepoRoot();
        var lib = JsonAddressLibrary.LoadFromFile(Path.Combine(root, "data", "addresses", "3.29.3d.json"));
        using var sim = new SimulatedNativeBridge();
        using var runtime = new ClientHostRuntime(sim, new ClientHostOptions { AddressLibrary = lib });
        runtime.Start();

        var ui = runtime.Ui;
        var clipboard = runtime.Clipboard;
        var files = runtime.FileOverlayService;
        var opcodes = runtime.Opcodes;

        string uiRoot = Path.Combine(root, "tests", "LocalExile.Client.Phase0E2E", "client");
        using var dirReg = files.RegisterDirectory(uiRoot, priority: 100);

        using var win = ui.Load(Phase0Protocol.ProbeWindowUiPath, "probe");
        var textBox = win.Require<ITextBox>("probe_textbox");
        string? lastChanged = null;
        string? lastSubmitted = null;
        textBox.TextChanged += t => lastChanged = t;
        textBox.Submitted += t => lastSubmitted = t;
        textBox.Text = "Phase0_Test";
        textBox.Submit();
        Assert.Equal("Phase0_Test", textBox.Text);
        Assert.Equal("Phase0_Test", lastChanged);
        Assert.Equal("Phase0_Test", lastSubmitted);

        var checkBox = win.Require<ICheckBox>("probe_checkbox");
        bool? checkedVal = null;
        checkBox.CheckedChanged += v => checkedVal = v;
        checkBox.IsChecked = true;
        Assert.True(checkBox.IsChecked);
        Assert.True(checkedVal);

        clipboard.SetText(Phase0Protocol.ClipboardProbeText);
        Assert.Equal(Phase0Protocol.ClipboardProbeText, clipboard.GetText());

        var host = win.Require<IUiControl>("probe_slot_host");
        host.SetPosition(100f, 120f);
        host.SetSize(300f, 160f);
        Assert.Equal(100f, host.Rect.X);
        Assert.Equal(120f, host.Rect.Y);
        Assert.Equal(300f, host.Rect.Width);
        Assert.Equal(160f, host.Rect.Height);

        using var child = ui.CreateWindowInto(host, Phase0Protocol.ChildCardUiPath);
        Assert.Contains(child, host.Children);
        Assert.True(host.RemoveChild(child));
        child.Destroy();

        Assert.ThrowsAny<ArgumentException>(() => opcodes.RegisterHandler(0x02FB, _ => { }, "phase0"));
        using var opReg = opcodes.RegisterHandler(Phase0Protocol.RawProbeOpcodeLoopback, _ => { }, "phase0");
        Assert.True(opcodes.IsRegistered(Phase0Protocol.RawProbeOpcodeLoopback));

        files.InvalidateCachedAsset(Phase0Protocol.ProbeWindowUiPath);
    }

    [Fact]
    public async Task CollectibleAlc_Phase0ClientMod_LoadsAndUnloadsCleanly()
    {
        string root = FindRepoRoot();
        string phase0Dir = Path.Combine(root, "tests", "LocalExile.Client.Phase0E2E");
        string clientOutDir = Path.Combine(phase0Dir, "Client", "bin", "Release", "net10.0");
        string clientDll = Path.Combine(clientOutDir, "phase0-e2e.Client.dll");
        if (!File.Exists(clientDll))
        {
            clientOutDir = Path.Combine(phase0Dir, "Client", "bin", "Debug", "net10.0");
            clientDll = Path.Combine(clientOutDir, "phase0-e2e.Client.dll");
        }

        Assert.True(File.Exists(clientDll), $"Expected built phase0-e2e.Client.dll under {clientOutDir}");

        string stageModDir = Path.Combine(AppContext.BaseDirectory, "phase0-alc-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stageModDir);
        try
        {
            File.Copy(Path.Combine(phase0Dir, "mod.json"), Path.Combine(stageModDir, "mod.json"));
            File.Copy(clientDll, Path.Combine(stageModDir, "phase0-e2e.Client.dll"));

            using var sim = new SimulatedNativeBridge();
            using var runtime = new ClientHostRuntime(sim);
            runtime.Start();

            var loaded = runtime.Loader.LoadModFromDirectory(stageModDir);
            Assert.Equal("phase0-e2e", loaded.Manifest.Id);

            var unload = await runtime.Loader.UnloadModAsync("phase0-e2e");
            Assert.True(unload.WasLoaded);
            Assert.True(unload.UnloadedCleanly);
        }
        finally
        {
            if (Directory.Exists(stageModDir))
            {
                Directory.Delete(stageModDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ServerBridge020_AsyncHandlersClientReadyAndGatewayBus_RouteCleanly()
    {
        var sink = new RecordingSink();
        var bridge = new ClientBridgeHost(sink, new ClientBridgeOptions
        {
            HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
            DefaultChannel = Phase0Protocol.ChannelName
        });
        using var serverMod = new Phase0ServerMod(bridge);
        serverMod.Attach();

        string handshakeJson = JsonSerializer.Serialize(new ClientHandshake(
            ExtenderVersion: SdkVersion.Current,
            SdkVersion: SdkVersion.Current,
            Build: ClientBuildId.Build3293d,
            BuildVersion: "3.29.3d",
            ProtocolVersion: SdkVersion.ProtocolVersion,
            InstalledMods:
            [
                new InstalledClientModInfo(Phase0Protocol.ModId, "0.2.0"),
                new InstalledClientModInfo("maxwell", "0.2.0"),
                new InstalledClientModInfo("siphon", "0.2.0"),
            ],
            Capabilities: ["ruleset.0x2c9"]));

        await bridge.HandleIncomingEnvelopeAsync(
            "conn-1",
            new ModEnvelope(ClientBridgeHost.HostChannelId, nameof(ClientHandshake), handshakeJson),
            characterName: "Phase0Tester");

        string pingJson = JsonSerializer.Serialize(new Phase0Ping("tok-1", SdkVersion.Current, 0x00000200u, true));
        await bridge.HandleIncomingEnvelopeAsync(
            "conn-1",
            new ModEnvelope(Phase0Protocol.ModId, nameof(Phase0Ping), pingJson));

        Assert.Contains(sink.SentPackets, p => p.ConnectionId == "conn-1");
    }

    private sealed class RecordingSink : IGatewayPacketSink
    {
        public List<(string ConnectionId, ushort Opcode, byte[] Payload)> SentPackets { get; } = [];
        public List<(string ConnectionId, string Text)> SentChats { get; } = [];

        public Task SendPacketAsync(string connectionId, ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            SentPackets.Add((connectionId, opcode, payload.ToArray()));
            return Task.CompletedTask;
        }

        public Task SendChatReplyAsync(string connectionId, string text, CancellationToken cancellationToken = default)
        {
            SentChats.Add((connectionId, text));
            return Task.CompletedTask;
        }
    }
}
