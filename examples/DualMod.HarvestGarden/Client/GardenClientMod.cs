using System.Globalization;
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
    private IUiControl? _hostControl;

    public string ControlTypeName => GardenProtocol.GrowthGaugeControlType;
    public float CurrentProgress { get; private set; }
    public float TargetProgress { get; private set; }
    public int RipenPulses { get; private set; }

    public void OnInitialize(IUiControl hostControl)
    {
        _hostControl = hostControl;
        if (float.TryParse(hostControl.GetProperty("progress"), CultureInfo.InvariantCulture, out float initial))
        {
            TargetProgress = Math.Clamp(initial, 0f, 1f);
            CurrentProgress = TargetProgress;
        }
    }

    public void OnPropertyChanged(string propertyName, string value)
    {
        if (propertyName.Equals("progress", StringComparison.OrdinalIgnoreCase) &&
            float.TryParse(value, CultureInfo.InvariantCulture, out float parsed))
        {
            TargetProgress = Math.Clamp(parsed, 0f, 1f);
        }
        else if (propertyName.Equals("ripen_stage", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, CultureInfo.InvariantCulture, out int stage))
        {
            RipenPulses++;
            TargetProgress = Math.Clamp(stage / 3f, 0f, 1f);
        }
    }

    public void OnFrame(float deltaSeconds)
    {
        if (deltaSeconds <= 0f)
            return;

        float diff = TargetProgress - CurrentProgress;
        if (MathF.Abs(diff) < 0.001f)
        {
            CurrentProgress = TargetProgress;
        }
        else
        {
            CurrentProgress += diff * MathF.Min(1f, deltaSeconds * 6f);
        }

        _hostControl?.SetProperty("rendered_progress", CurrentProgress.ToString("0.000", CultureInfo.InvariantCulture));
    }

    public void OnDestroy()
    {
        _hostControl = null;
    }
}

/// <summary>
/// Per-area client ruleset registered for the Sacred Grove mechanic (ruleset id <see cref="GardenProtocol.GardenRulesetId"/>).
/// </summary>
public sealed class GardenRuleset : IClientRuleset
{
    public byte RulesetId => GardenProtocol.GardenRulesetId;
    public string Name => "HarvestGardenRuleset";
    public int UpdatesReceived { get; private set; }
    public int AreaEntries { get; private set; }

    public void OnAreaEntered(AreaEventArgs area) => AreaEntries++;

    public void OnRulesetUpdate(ReadOnlySpan<byte> payload) => UpdatesReceived++;

    public void OnFrame(float deltaSeconds) { }

    public void OnAreaLeft() { }
}

/// <summary>
/// Client half of the Harvest Garden dual-side mod, running inside the 3.29 client via <c>LocalExile.Client.Host</c>.
/// Demonstrates:
/// - Custom UI control registration (<see cref="GrowthGauge"/>)
/// - Custom GEAL built-in registration (<c>GardenRipen</c>)
/// - Custom ruleset (<see cref="GardenRuleset"/>) &amp; opcode handler registration
/// - Append-only content registry declarations (<c>WildSeed</c> + <c>Plot.ot</c>)
/// - Loading custom <c>.ui</c> layout and binding <c>Button</c>, <c>TextBox</c>, <c>CheckBox</c>, <c>Label</c>
/// - Typed network messaging over <c>context.Net</c>
/// - Per-frame animation ticking via <c>game.Frame</c>
/// </summary>
public sealed class GardenClientMod : IClientMod
{
    private IClientContext? _context;
    private IUiWindow? _window;
    private int _frameCount;
    private int _ripenCalls;
    private bool _unloading;

    public IUiWindow? Window => _window;
    public int FrameCount => _frameCount;
    public int RipenCalls => _ripenCalls;
    public GardenStateSnapshot? LastSnapshot { get; private set; }

    public void Load(IClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var game = context.Game;

        // 1. Register custom UI control usable directly from Stockpile.ui
        context.TrackDisposable(game.Ui.RegisterControl<GrowthGauge>(GardenProtocol.GrowthGaugeControlType));

        // 2. Register custom GEAL built-in 'GardenRipen' invoked by Plot.ot
        context.TrackDisposable(game.GealBuiltins.Register(GardenProtocol.GealRipenBuiltin, (objectId, args) =>
        {
            int stage = args.Count > 0 ? args.Int(0) : 1;
            _ripenCalls++;
            DispatchOnGameThread(() =>
            {
                if (_window is { IsOpen: true } window && window.Find("growth_gauge") is { } gauge)
                {
                    gauge.SetProperty("ripen_stage", stage.ToString(CultureInfo.InvariantCulture));
                }
            });
            context.Logger.Info($"GEAL GardenRipen triggered on object 0x{objectId:X} (stage={stage}).");
            return GealValue.FromInt(stage);
        }));

        // 3. Register per-area client ruleset and custom opcode handler
        context.TrackDisposable(game.Rulesets.Register<GardenRuleset>());
        context.TrackDisposable(game.Opcodes.RegisterHandler(
            GardenProtocol.OpcodeSyncState,
            payload => context.Logger.Debug($"Received raw GardenSyncState opcode ({payload.Length} bytes)."),
            GardenProtocol.ModId));

        // 4. Register append-only content declarations (ItemBase + MiscObject Plot)
        context.TrackDisposable(game.Content.DeclareItemBase(new ItemBaseDeclaration(
            MetadataPath: "Metadata/Items/Mods/Garden/WildSeed",
            Name: "Wild Seed",
            InheritsFrom: "Metadata/Items/Currency/StackableCurrency",
            ItemClass: "StackableCurrency",
            ArtPath: "Art/2DItems/Mods/Garden/WildSeed.dds",
            StackSize: 20)));

        context.TrackDisposable(game.Content.DeclareWorldObject(new WorldObjectDeclaration(
            MetadataPath: GardenProtocol.PlotObjectPath,
            CarrierTable: "MiscObjects",
            ScriptPath: GardenProtocol.PlotObjectPath)));

        // 5. Subscribe to server-pushed garden messages (marshaling onto game thread via IModScheduler)
        context.TrackDisposable(context.Net.On<GardenOpenWindowCommand>(cmd => DispatchOnGameThread(() =>
        {
            EnsureWindowLoaded();
            ApplySnapshot(cmd.InitialState);
        })));

        context.TrackDisposable(context.Net.On<GardenStateSnapshot>(snapshot => DispatchOnGameThread(() => ApplySnapshot(snapshot))));

        // 6. Per-frame interpolation on the game thread
        game.Frame += OnFrame;
    }

    public void Unload()
    {
        _unloading = true;
        if (_context is not null)
        {
            _context.Game.Frame -= OnFrame;
        }

        if (_window is { } window)
        {
            window.Closed -= OnWindowClosed;
            window.Close();
        }

        _window = null;
        _context = null;
    }

    private void DispatchOnGameThread(Action action)
    {
        IClientContext? context = _context;
        if (_unloading || context is null)
            return;

        if (context.Scheduler.IsOnGameThread)
        {
            action();
            return;
        }

        context.Scheduler.RunOnGameThread(() =>
        {
            if (_unloading || _context is null)
                return;
            action();
        });
    }

    private void OnFrame(float dt)
    {
        _frameCount++;
        if (_window is { IsOpen: true } window && window.Find("growth_gauge") is { } gauge)
        {
            gauge.SetProperty("frame_ticks", _frameCount.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void EnsureWindowLoaded()
    {
        if (_unloading || _context is null || _window is { IsOpen: true })
            return;

        if (_window is { } stale)
        {
            stale.Closed -= OnWindowClosed;
            _window = null;
        }

        _window = _context.Game.Ui.Load(GardenProtocol.StockpileUiPath, "harvest_garden_stockpile");
        _window.Closed += OnWindowClosed;

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
            if (_window is { IsOpen: true } window)
                window.Visible = false;
        };
    }

    private void OnWindowClosed()
    {
        if (_window is { } closedWindow)
        {
            closedWindow.Closed -= OnWindowClosed;
        }

        _window = null;
    }

    private void ApplySnapshot(GardenStateSnapshot snapshot)
    {
        if (_unloading || _context is null)
            return;

        LastSnapshot = snapshot;
        EnsureWindowLoaded();
        if (_window is not { IsOpen: true } window)
            return;

        window.Visible = true;
        window.Require<ILabel>("lifeforce_label").Text =
            $"Condensed Lifeforce: {snapshot.Lifeforce} (Harvested: {snapshot.TotalHarvested})";
        window.Require<ILabel>("status_label").Text = snapshot.StatusMessage;

        if (window.Find("growth_gauge") is { } gauge)
        {
            gauge.SetProperty("progress", snapshot.AverageGrowthProgress.ToString("0.000", CultureInfo.InvariantCulture));
        }
    }
}
