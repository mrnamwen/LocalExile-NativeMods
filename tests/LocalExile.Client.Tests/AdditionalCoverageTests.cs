using LocalExile.Client.Host;
using LocalExile.Client.Host.Loading;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using Xunit;

namespace LocalExile.Client.Tests;

public sealed class AdditionalCoverageTests
{
    [Theory]
    [InlineData("*", true)]
    [InlineData("1.1.0", true)]
    [InlineData(">=1.0.0 <2.0.0", true)]
    [InlineData(">=1.1.0 <2.0.0", true)]
    [InlineData(">=0.2.0 <2.0.0", true)]
    [InlineData(">=0.3.0 <2.0.0", true)]
    [InlineData(">=0.4.0 <2.0.0", true)]
    [InlineData(">=0.1.0-poc <2.0.0", true)]
    [InlineData("^1.0.0", true)]
    [InlineData("^1.1.0", true)]
    [InlineData("~1.1.0", true)]
    [InlineData("=0.1.0-poc", false)]
    [InlineData(">=2.0.0", false)]
    [InlineData("^0.4.0", false)]
    [InlineData("<1.0.0", false)]
    [InlineData("~1.0.0", false)]
    public void SdkVersion_EvaluatesSemVerRangesAccurately(string range, bool expected)
    {
        Assert.Equal(expected, SdkVersion.IsCompatible(range));
    }

    [Fact]
    public void ClientModManifest_ParsesWaypointDualSideModJson()
    {
        string json = """
            {
              "id": "harvestgarden",
              "name": "Harvest Garden",
              "description": "Sacred Grove planting showcase.",
              "author": "Community",
              "version": "1.0.0",
              "compatibleClientVersions": ["0.5.2-beta"],
              "depends": ["Game"],
              "nativeClient": {
                "sdkVersionRange": ">=0.1.0-poc <0.2.0",
                "assembly": "harvestgarden.Client.dll",
                "payloadDir": "client",
                "replaces": ["Metadata/UI/ConfirmationWindow.ui"],
                "rawAccess": true,
                "rawAccessJustification": "Reads camera node pointer",
                "payloadHash": "deadbeef"
              }
            }
            """;

        ClientModManifest manifest = ClientModManifest.Parse(json);
        Assert.Equal("harvestgarden", manifest.Id);
        Assert.Equal("Harvest Garden", manifest.Name);
        Assert.Equal(">=0.1.0-poc <0.2.0", manifest.SdkVersionRange);
        Assert.Equal("harvestgarden.Client.dll", manifest.Assembly);
        Assert.Equal("client", manifest.PayloadDir);
        Assert.Single(manifest.EffectiveReplaces, "Metadata/UI/ConfirmationWindow.ui");
        Assert.Empty(manifest.EffectiveDepends);
        Assert.True(manifest.RawAccess);
        Assert.Equal("Reads camera node pointer", manifest.RawAccessJustification);
        Assert.Equal("deadbeef", manifest.PayloadHash);

        // Also verify bundleSha256 fallback & Waypoint-deployed Mods/Client/<id>/files/ fallback.
        string tempModDir = Path.Combine(Path.GetTempPath(), $"nm-wp-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempModDir, "files", "metadata", "ui", "mods", "wp"));
        try
        {
            File.WriteAllText(Path.Combine(tempModDir, "mod.json"), """
                {
                  "id": "wpmod",
                  "name": "Waypoint Deployed Mod",
                  "version": "1.0.0",
                  "nativeClient": {
                    "sdkVersionRange": ">=0.1.0-poc",
                    "payloadDir": "client",
                    "bundleSha256": "cafebabe01234567"
                  }
                }
                """);
            File.WriteAllBytes(
                Path.Combine(tempModDir, "files", "metadata", "ui", "mods", "wp", "test.ui"),
                ClientTextEncoding.EncodeUtf16LeBomCrlf("begin Frame\n id = wp_win;\nend"));

            using var bridge = new SimulatedNativeBridge();
            using var host = new ClientHostRuntime(bridge);
            LoadedClientMod loaded = host.Loader.LoadModFromDirectory(tempModDir);
            Assert.Equal("cafebabe01234567", loaded.Manifest.BundleSha256);
            Assert.Null(loaded.Manifest.PayloadHash);
            Assert.True(host.FileOverlayService.Exists("Metadata/UI/Mods/WP/Test.ui"));
        }
        finally
        {
            Directory.Delete(tempModDir, recursive: true);
        }
    }

    [Fact]
    public async Task Scheduler_MarshalsBackgroundWorkOntoGameThreadOnTickFrame()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

        int executedThreadId = -1;
        bool wasOnGameThread = false;
        Task<int> workTask = Task.Run(() => host.Scheduler.RunOnGameThreadAsync(() =>
        {
            executedThreadId = Environment.CurrentManagedThreadId;
            wasOnGameThread = host.Scheduler.IsOnGameThread;
            return 42;
        }));

        // Give the background task a moment to enqueue onto the main-thread queue.
        await Task.Delay(25);
        Assert.False(workTask.IsCompleted);

        int tickThreadId = Environment.CurrentManagedThreadId;
        host.TickFrame(0.016f);
        int result = await workTask;
        Assert.Equal(42, result);
        Assert.Equal(tickThreadId, executedThreadId);
        Assert.True(wasOnGameThread);
    }

    [Fact]
    public void LegacySpikeS4ChatLines_OpenAndUpdateWindowViaChatFallback()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge, new ClientHostOptions { TransportKind = NetTransportKind.ChatFallback });

        // Load the REAL spikes/overlay-src/metadata/ui/mods/spike/garden.ui from the repo!
        string repoRoot = FindRepoRoot();
        string spikeGardenUi = Path.Combine(repoRoot, "spikes", "overlay-src", "metadata", "ui", "mods", "spike", "garden.ui");
        Assert.True(File.Exists(spikeGardenUi), $"Expected spike UI at {spikeGardenUi}");

        host.FileOverlayService.RegisterVirtualTextFile("Metadata/UI/Mods/Spike/Garden.ui", File.ReadAllText(spikeGardenUi));

        // Plain S4 chat commands must be intercepted (return true) and drive the UI!
        Assert.True(bridge.SimulateIncomingChat("Server", "##NM open Metadata/UI/Mods/Spike/Garden.ui"));
        Assert.True(bridge.SimulateIncomingChat("Server", "##NM set body.status Welcome from spike S4!"));
        Assert.False(bridge.SimulateIncomingChat("Player", "Normal chat message should not be intercepted"));

        IUiWindow? win = host.Ui.GetWindow("Garden");
        Assert.NotNull(win);
        Assert.Equal("Welcome from spike S4!", win!.Require<ILabel>("body.status").Text);
        Assert.Equal("Plant", win.Require<IButton>("body.buttons.plant").Text);
        Assert.Equal("Harvest", win.Require<IButton>("body.buttons.harvest").Text);
        Assert.Equal("Close", win.Require<IButton>("body.buttons.close").Text);
    }

    [Fact]
    public async Task ModUnload_AutoDisposesAllScopedRegistrationsWithoutManualTracking()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

        host.Loader.LoadInProcessMod(new ClientModManifest("auto-cleanup", "Auto Cleanup", "1.0.0"), new AutoTrackedMod());

        Assert.True(host.GealBuiltins.IsRegistered("TempBuiltin"));
        Assert.True(host.UiControls.IsRegistered("TempControl"));
        Assert.True(host.Opcodes.IsRegistered(IOpcodeRegistry.FirstReservedOpcode));
        Assert.True(host.FileOverlayService.Exists("Metadata/UI/Mods/Temp/Auto.ui"));

        ModUnloadResult result = await host.Loader.UnloadModAsync("auto-cleanup");
        Assert.True(result.WasLoaded);
        Assert.True(result.UnloadedCleanly);

        // Every registration made through ScopedGameServices must be automatically torn down on unload!
        Assert.False(host.GealBuiltins.IsRegistered("TempBuiltin"));
        Assert.False(host.UiControls.IsRegistered("TempControl"));
        Assert.False(host.Opcodes.IsRegistered(IOpcodeRegistry.FirstReservedOpcode));
        Assert.False(host.FileOverlayService.Exists("Metadata/UI/Mods/Temp/Auto.ui"));
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "NativeMods.slnx")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate NativeMods.slnx root.");
    }

    private sealed class AutoTrackedMod : IClientMod
    {
        public void Load(IClientContext context)
        {
            // Intentionally discard all IDisposable handles — host must auto-track and clean them up on unload!
            _ = context.Game.Scripts.Register("TempBuiltin", (_, _) => GealValue.FromBool(true));
            _ = context.Game.Ui.RegisterControl("TempControl", () => new DummyControl());
            _ = context.Game.Opcodes.RegisterHandler(IOpcodeRegistry.FirstReservedOpcode, _ => { });
            _ = context.Game.Files.RegisterVirtualTextFile("Metadata/UI/Mods/Temp/Auto.ui", "begin Frame\n id = auto;\nend");
        }
    }

    private sealed class DummyControl : ICustomUiControl
    {
        public string ControlTypeName => "TempControl";
        public void OnInitialize(IUiControl hostControl) { }
        public void OnPropertyChanged(string propertyName, string value) { }
        public void OnFrame(float deltaSeconds) { }
        public void OnDestroy() { }
    }
}
