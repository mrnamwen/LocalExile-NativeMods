using LocalExile.Client.Host;
using LocalExile.Client.Host.Native;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;
using LocalExile.Client.Sdk.Protocol;
using Xunit;

namespace LocalExile.Client.Tests;

public sealed class ExtendRegistriesTests
{
    [Fact]
    public void GealBuiltins_RegisterInvokeAndValidateScriptDiagnostics()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

        int ripenedStages = 0;
        uint ripenedObj = 0;
        using IDisposable reg = host.Scripts.Register("GardenRipen", (objId, args) =>
        {
            ripenedObj = objId;
            ripenedStages = args.Int(0);
            return GealValue.FromInt(ripenedStages * 10);
        });

        Assert.Contains("GardenRipen", host.Scripts.GetRegisteredBuiltins());
        GealInvocationResult res = host.Scripts.InvokeFunction(0x42, "GardenRipen", GealValue.FromInt(3));
        Assert.True(res.Succeeded);
        Assert.Equal(0x42u, ripenedObj);
        Assert.Equal(3, ripenedStages);
        Assert.Equal(30, res.ReturnValue.IntValue);

        // Validate script catches server-only built-in, unbounded Repeat warning, and unbalanced braces.
        string badScript = """
            client {
                GrantItem("Currency");
                Repeat(5000, { PlayAnimation("grow"); });
                If(true, {
            """;
        IReadOnlyList<GealScriptDiagnostic> diags = host.Scripts.ValidateScript(badScript);
        Assert.Contains(diags, d => d.IsError && d.Code == "GEAL002");
        Assert.Contains(diags, d => !d.IsError && d.Code == "GEAL003");
        Assert.Contains(diags, d => d.IsError && d.Code == "GEAL004");

        // Verify folded FNV-1a hash is deterministic and non-zero for camera_zoom.
        ushort hash = MessageCodec.ComputeGealFunctionHash("camera_zoom");
        Assert.Equal(hash, MessageCodec.ComputeGealFunctionHash("camera_zoom"));
        Assert.NotEqual(0, hash);
    }

    [Fact]
    public void CustomUiControl_InstantiatedFromUiLayoutReceivesLifecycleCallbacks()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

        GrowthGaugeControl? instance = null;
        using IDisposable reg = host.Ui.RegisterControl("GrowthGauge", () =>
        {
            instance = new GrowthGaugeControl();
            return instance;
        });

        using IDisposable overlay = host.FileOverlayService.RegisterVirtualTextFile("Metadata/UI/Mods/Garden/Gauge.ui", """
            begin Frame
                id = gauge_win;
                begin GrowthGauge
                    id = lifeforce_gauge;
                    progress = 0.25;
                end
            end
            """);

        IUiWindow win = host.Ui.Load("Metadata/UI/Mods/Garden/Gauge.ui");
        Assert.NotNull(instance);
        Assert.True(instance!.Initialized);

        IUiControl gauge = win.Require<IUiControl>("lifeforce_gauge");
        gauge.SetProperty("progress", "0.75");
        Assert.Equal("0.75", instance.LastProgress);

        host.TickFrame(0.05f);
        Assert.Equal(1, instance.FrameTicks);

        win.Close();
        Assert.True(instance.Destroyed);
    }

    [Fact]
    public void RulesetOpcodeAndContentRegistries_DispatchAndGenerateDeterministicIds()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

        // Ruleset registry (verified free slots in 0x00..0xAE; > 0xAE and occupied stock slots like 0xA0 rejected).
        TestGardenRuleset? ruleset = null;
        using IDisposable rsReg = host.Rulesets.Register(IRulesetRegistry.FirstFreeNativeRulesetId, () =>
        {
            ruleset = new TestGardenRuleset();
            return ruleset;
        });

        Assert.Throws<ArgumentOutOfRangeException>(() => host.Rulesets.Register(0xAF, () => new TestGardenRuleset()));
        Assert.Throws<InvalidOperationException>(() => host.Rulesets.Register(IRulesetRegistry.DefaultHostChannelRulesetId, () => new TestGardenRuleset()));

        host.NotifyAreaEntered("HarvestGrove", seed: 12345, activeRulesets: [IRulesetRegistry.FirstFreeNativeRulesetId]);
        Assert.Equal("HarvestGrove", ruleset!.LastArea);

        Assert.True(bridge.SimulateIncomingRulesetPacket(IRulesetRegistry.FirstFreeNativeRulesetId, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, ruleset.LastPayload);
        // Absent ruleset drops silently (returns false).
        Assert.False(bridge.SimulateIncomingRulesetPacket(0xAA, new byte[] { 1, 2 }));

        host.TickFrame(0.016f);
        Assert.Equal(1, ruleset.FrameCount);
        host.NotifyAreaLeft("HarvestGrove");
        Assert.True(ruleset.LeftArea);

        // Custom opcode registry (>= 0x02FC).
        byte[]? receivedOpcodePayload = null;
        using IDisposable opReg = host.Opcodes.RegisterHandler(IOpcodeRegistry.FirstReservedOpcode, data => receivedOpcodePayload = data.ToArray());
        Assert.True(bridge.SimulateIncomingRawPacket(IOpcodeRegistry.FirstReservedOpcode, new byte[] { 9, 8, 7 }));
        Assert.Equal(new byte[] { 9, 8, 7 }, receivedOpcodePayload);

        // Content registry + automatic .ot overlay + deterministic ID.
        using IDisposable itemReg = host.Content.DeclareItemBase(new ItemBaseDeclaration(
            "Metadata/Items/Mods/Garden/WildSeed", "Wild Seed", "Metadata/Items/Currency/StackableCurrency", "StackableCurrency", "Art/2DItems/WildSeed"));
        using IDisposable objReg = host.Content.DeclareWorldObject(new WorldObjectDeclaration(
            "Metadata/MiscellaneousObjects/Mods/Garden/Plot", "MiscObjects", OtOverlayContent: "version = 2\nclient { on_enter = { GardenRipen(1); } }"));
        using IDisposable datReg = host.Content.DeclareDatRow(new DatRowDeclaration(
            "MiscObjects", "garden_plot", new Dictionary<string, object?> { ["Id"] = "Metadata/MiscellaneousObjects/Mods/Garden/Plot" }));

        Assert.Single(host.Content.ItemBases);
        Assert.Single(host.Content.WorldObjects);
        Assert.Single(host.Content.DatRows);
        Assert.True(host.FileOverlayService.Exists("Metadata/MiscellaneousObjects/Mods/Garden/Plot.ot"));
        Assert.Equal(
            host.Content.ComputeDeterministicId("garden", "WildSeedT1"),
            host.Content.ComputeDeterministicId("GARDEN", "wildseedt1"));

        // Audio, Stats, World services.
        int ch = host.Audio.PlayEvent("event:/Harvest/PlotReady", 0.8f);
        Assert.Equal(1, host.Audio.ActiveChannelCount);
        host.Audio.StopChannel(ch);
        Assert.Equal(0, host.Audio.ActiveChannelCount);

        int observedStat = 0;
        using IDisposable statSub = host.Stats.OnStatChanged("garden_lifeforce", v => observedStat = v);
        host.Stats.SetLocalStat("garden_lifeforce", 250);
        Assert.Equal(250, observedStat);
        Assert.Equal(250, host.Stats.GetStat("garden_lifeforce"));

        uint objId = host.World.SpawnClientObject("Metadata/MiscellaneousObjects/Mods/Garden/Plot", 10f, 20f);
        Assert.NotNull(host.World.FindObject(objId));
        host.World.PlayEffect(objId, "Metadata/Effects/Harvest/Ripe");
        Assert.Contains((objId, "Metadata/Effects/Harvest/Ripe"), bridge.PlayedEffects);
        Assert.True(host.World.RemoveClientObject(objId));
    }

    private sealed class GrowthGaugeControl : ICustomUiControl
    {
        public string ControlTypeName => "GrowthGauge";
        public bool Initialized { get; private set; }
        public bool Destroyed { get; private set; }
        public int FrameTicks { get; private set; }
        public string? LastProgress { get; private set; }

        public void OnInitialize(IUiControl hostControl) => Initialized = true;
        public void OnPropertyChanged(string propertyName, string value)
        {
            if (propertyName == "progress") LastProgress = value;
        }
        public void OnFrame(float deltaSeconds) => FrameTicks++;
        public void OnDestroy() => Destroyed = true;
    }

    private sealed class TestGardenRuleset : IClientRuleset
    {
        public byte RulesetId => IRulesetRegistry.FirstFreeNativeRulesetId;
        public string Name => "TestGarden";
        public string? LastArea { get; private set; }
        public byte[]? LastPayload { get; private set; }
        public int FrameCount { get; private set; }
        public bool LeftArea { get; private set; }

        public void OnAreaEntered(AreaEventArgs area) => LastArea = area.AreaId;
        public void OnRulesetUpdate(ReadOnlySpan<byte> payload) => LastPayload = payload.ToArray();
        public void OnFrame(float deltaSeconds) => FrameCount++;
        public void OnAreaLeft() => LeftArea = true;
    }

    [Fact]
    public void ContentRegistry_RejectsDuplicateDatRowsItemBasesAndWorldObjects_AndAllowsReRegistrationAfterDispose()
    {
        using var bridge = new SimulatedNativeBridge();
        using var host = new ClientHostRuntime(bridge);

        IDisposable dat1 = host.Content.DeclareDatRow(new DatRowDeclaration(
            "MiscObjects", "garden_plot", new Dictionary<string, object?> { ["Id"] = "a" }));
        Assert.Throws<InvalidOperationException>(() => host.Content.DeclareDatRow(new DatRowDeclaration(
            "miscobjects", "GARDEN_PLOT", new Dictionary<string, object?> { ["Id"] = "b" })));
        dat1.Dispose();
        using IDisposable dat2 = host.Content.DeclareDatRow(new DatRowDeclaration(
            "MiscObjects", "garden_plot", new Dictionary<string, object?> { ["Id"] = "b" }));

        IDisposable item1 = host.Content.DeclareItemBase(new ItemBaseDeclaration(
            "Metadata/Items/Mods/Garden/WildSeed", "Wild Seed", "Metadata/Items/Currency/StackableCurrency", "StackableCurrency", "Art/2DItems/WildSeed"));
        Assert.Throws<InvalidOperationException>(() => host.Content.DeclareItemBase(new ItemBaseDeclaration(
            "metadata/items/mods/garden/wildseed", "Dup Seed", "Metadata/Items/Currency/StackableCurrency", "StackableCurrency", "Art/2DItems/WildSeed")));
        item1.Dispose();
        using IDisposable item2 = host.Content.DeclareItemBase(new ItemBaseDeclaration(
            "Metadata/Items/Mods/Garden/WildSeed", "Wild Seed 2", "Metadata/Items/Currency/StackableCurrency", "StackableCurrency", "Art/2DItems/WildSeed"));

        IDisposable obj1 = host.Content.DeclareWorldObject(new WorldObjectDeclaration(
            "Metadata/MiscellaneousObjects/Mods/Garden/Plot", "MiscObjects", OtOverlayContent: "version = 2\n"));
        Assert.Throws<InvalidOperationException>(() => host.Content.DeclareWorldObject(new WorldObjectDeclaration(
            "metadata/miscellaneousobjects/mods/garden/plot", "MiscObjects", OtOverlayContent: "version = 2\n")));
        obj1.Dispose();
        using IDisposable obj2 = host.Content.DeclareWorldObject(new WorldObjectDeclaration(
            "Metadata/MiscellaneousObjects/Mods/Garden/Plot", "MiscObjects", OtOverlayContent: "version = 2\n"));

        Assert.Single(host.Content.DatRows);
        Assert.Single(host.Content.ItemBases);
        Assert.Single(host.Content.WorldObjects);
    }
}
