using DualMod.HarvestGarden.Shared;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;

namespace DualMod.HarvestGarden.Client;

/// <summary>
/// Custom UI control registered via <see cref="IUiService.RegisterControl{T}"/> and referenced in
/// <c>Metadata/UI/Mods/Garden/Stockpile.ui</c> as <c>begin GrowthGauge</c>.
/// Smoothly interpolates visual growth progress on every client frame.
/// </summary>
public sealed class GrowthGauge : ICustomUiControl
{
    public string TypeName => GardenProtocol.GrowthGaugeControlType;
    public float CurrentProgress { get; private set; }
    public float TargetProgress { get; private set; }
    public int RipenPulses { get; private set; }

    public void SetTarget(float progress)
    {
        TargetProgress = Math.Clamp(progress, 0f, 1f);
    }

    public void PulseRipen(int stage)
    {
        RipenPulses++;
        TargetProgress = Math.Clamp(stage / 3f, 0f, 1f);
    }

    public void Advance(float deltaSeconds)
    {
        if (deltaSeconds <= 0f) return;
        float diff = TargetProgress - CurrentProgress;
        if (MathF.Abs(diff) < 0.001f)
        {
            CurrentProgress = TargetProgress;
            return;
        }
        CurrentProgress += diff * MathF.Min(1f, deltaSeconds * 6f);
    }
}

/// <summary>
/// Per-area client ruleset registered for the Sacred Grove mechanic (ruleset id <see cref="GardenProtocol.GardenRulesetId"/>).
/// </summary>
public sealed class GardenRuleset : IClientRuleset
{
    public uint RulesetId => GardenProtocol.GardenRulesetId;
    public string Name => "HarvestGardenRuleset";
    public int UpdatesReceived { get; private set; }

    public void OnRulesetUpdate(ReadOnlySpan<byte> payload)
    {
        UpdatesReceived++;
    }
}

/// <summary>
/// Client half of the Harvest Garden dual-side mod, running inside the 3.29 client via <c>LocalExile.Client.Host</c>.
/// Demonstrates:
/// - Custom UI control registration (<see cref="GrowthGauge"/>)
/// - Custom GEAL built-in registration (<c>GardenRipen</c>)
/// - Custom ruleset &amp; opcode registration
/// - Loading custom <c>.ui</c> layout and binding <c>Button</c>, <c>TextBox</c>, <c>CheckBox</c>, <c>Label</c>
/// - Typed network messaging over <c>mod.Net</c>
/// - Per-frame animation ticking via <c>game.Frame</c>
/// </summary>
public sealed class GardenClientMod : IClientMod
{
    private IClientContext? _context;
    private IUiWindow? _window;
    private readonly GrowthGauge _gauge = new();
    private int _frameCount;

    public IUiWindow? Window => _window;
    public GrowthGauge Gauge => _gauge;
    public int FrameCount => _frameCount;
    public GardenStateSnapshot? LastSnapshot { get; private set; }
    public int RipenCalls { get; private set; }

    public void Load(IClientContext context)
    {
        _context = context;
        var game = context.Game;

        // 1. Register custom UI control usable directly from Stockpile.ui
        context.TrackDisposable(game.Ui.RegisterControl<GrowthGauge>(GardenProtocol.GrowthGaugeControlType));

        // 2. Register custom GEAL built-in 'GardenRipen' invoked by Plot.ot
        context.TrackDisposable(game.GealBuiltins.Register(GardenProtocol.GealRipenBuiltin, (objectId, args) =>
        {
            int stage = args.Count > 0 ? args.Int(0) : 1;
            RipenCalls++;
            _gauge.PulseRipen(stage);
            context.Logger.Info($"GEAL GardenRipen triggered on object 0x{objectId:X} (stage={stage}).");
            return GealValue.FromInt(stage);
        }));

        // 3. Register per-area client ruleset and custom opcodes
        context.TrackDisposable(game.Rulesets.Register(new GardenRuleset()));
        context.TrackDisposable(game.Opcodes.Register(GardenProtocol.OpcodeSyncState, payload =>
        {
            context.Logger.Debug($"Received raw GardenSyncState opcode ({payload.Length} bytes).");
        }));

        // 4. Register content declarations (ItemBase + MiscObject Plot)
        game.Content.RegisterItemBase(new ItemBaseDeclaration(
            "Metadata/Items/Mods/Garden/WildSeed",
            "Metadata/Items/Currency/StackableCurrency",
            "StackableCurrency",
            "Wild Seed",
            "Art/2DItems/Mods/Garden/WildSeed.dds"));

        game.Content.RegisterWorldObject(new WorldObjectDeclaration(
            GardenProtocol.PlotObjectPath,
            "MiscObjects",
            "Harvest Garden Plot"));

        // 5. Subscribe to server-pushed garden messages
        context.TrackDisposable(context.Net.On<GardenOpenWindowCommand>(cmd =>
        {
            EnsureWindowLoaded();
            ApplySnapshot(cmd.InitialState);
        }));

        context.TrackDisposable(context.Net.On<GardenStateSnapshot>(ApplySnapshot));

        // 6. Per-frame interpolation on the game thread
        game.Frame += OnFrame;
    }

    public void Unload()
    {
        if (_context is not null)
        {
            _context.Game.Frame -= OnFrame;
        }
        _window?.Close();
        _window = null;
        _context = null;
    }

    private void OnFrame(float dt)
    {
        _frameCount++;
        _gauge.Advance(dt);
    }

    private void EnsureWindowLoaded()
    {
        if (_context is null || _window is not null)
            return;

        _window = _context.Game.Ui.Load(GardenProtocol.StockpileUiPath, "harvest_garden_stockpile");

        var seedBox = _window.Require<ITextBox>("seed_box");
        var autoCollect = _window.Require<ICheckBox>("auto_collect");

        _window.Require<IButton>("plant_button").Clicked += () =>
        {
            string seed = string.IsNullOrWhiteSpace(seedBox.Text) ? "Wild Thornfruit" : seedBox.Text;
            _context.Net.Send(new PlantSeedRequest(seed, autoCollect.IsChecked));
        };

        _window.Require<IButton>("harvest_button").Clicked += () =>
        {
            _context.Net.Send(new HarvestPlotRequest(-1));
        };

        _window.Require<IButton>("close_button").Clicked += () =>
        {
            _window.Visible = false;
        };
    }

    private void ApplySnapshot(GardenStateSnapshot snapshot)
    {
        LastSnapshot = snapshot;
        _gauge.SetTarget(snapshot.AverageGrowthProgress);

        EnsureWindowLoaded();
        if (_window is null) return;

        _window.Visible = true;
        _window.Require<ILabel>("lifeforce_label").Text =
            $"Condensed Lifeforce: {snapshot.Lifeforce} (Harvested: {snapshot.TotalHarvested})";
        _window.Require<ILabel>("status_label").Text = snapshot.StatusMessage;
    }
}
