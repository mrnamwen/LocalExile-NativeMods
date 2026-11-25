using System.Globalization;
using System.Reflection;
using DualMod.MaxwellPet.Shared;
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;

namespace DualMod.MaxwellPet.Client;

/// <summary>
/// Per-area client ruleset registered for Maxwell pet client updates (ruleset id <see cref="MaxwellProtocol.MaxwellRulesetId"/>).
/// </summary>
public sealed class MaxwellRuleset : IClientRuleset
{
    public byte RulesetId => MaxwellProtocol.MaxwellRulesetId;
    public string Name => "MaxwellPetRuleset";
    public int UpdatesReceived { get; private set; }
    public int AreaEntries { get; private set; }

    public void OnAreaEntered(AreaEventArgs area) => AreaEntries++;

    public void OnRulesetUpdate(ReadOnlySpan<byte> payload) => UpdatesReceived++;

    public void OnFrame(float deltaSeconds) { }

    public void OnAreaLeft() { }
}

/// <summary>
/// Client half of the Maxwell the Cat Pet MTX dual-side mod, running inside the 3.29.3d client via
/// <c>LocalExile.Client.Host</c> inside a collectible <c>AssemblyLoadContext</c>.
/// Demonstrates:
/// - Appended brand-new Pet MTX content declarations (<c>MtxTypes</c>, <c>MtxTypeGameSpecific</c>, <c>Pet</c>)
/// - File/asset/dat64 overlay registration (<c>IFileOverlayService</c>)
/// - Native <c>MaxwellPanel.ui</c> control window (Summon/Dismiss, Spin Speed, Float Height, Stock Market Music Play/Mute/Volume)
/// - Looping <c>Audio/Mods/Maxwell/stock_market.ogg</c> playback via <c>IAudioService</c>
/// - Custom GEAL built-in <c>MaxwellDance</c>
/// - Smooth client-side spinning &amp; floating per-frame simulation (<c>IGameLifecycle.Frame</c>)
/// </summary>
public sealed class MaxwellClientMod : IClientMod
{
    private const float HoverFrequencyRadPerSec = 2.5f;
    private const float HoverAmplitude = 4.0f;
    private const float SpinStepDegPerSec = 30f;
    private const float FloatStep = 5f;
    private const float VolumeStep = 0.1f;

    private IClientContext? _context;
    private IUiWindow? _window;
    private MaxwellStateSync? _lastState;
    private int? _activeAudioHandle;
    private float _activeAudioVolume = -1f;
    private float _currentYawDegrees;
    private float _hoverPhaseRadians;
    private float _currentFloatOffset;
    private int _frameCount;
    private int _danceCalls;
    private bool _suppressUiEvents;

    public IUiWindow? Window => _window;
    public MaxwellStateSync? LastState => _lastState;
    public int? ActiveAudioHandle => _activeAudioHandle;
    public float CurrentYawDegrees => _currentYawDegrees;
    public float CurrentFloatOffset => _currentFloatOffset;
    public int FrameCount => _frameCount;
    public int DanceCalls => _danceCalls;

    public void Load(IClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var game = context.Game;

        // 1. Register optional external Maxwell generated asset overlay directory (dist/maxwell-assets or env var)
        string? externalAssetsDir = Environment.GetEnvironmentVariable("NATIVEMODS_MAXWELL_ASSETS_DIR");
        if (!string.IsNullOrWhiteSpace(externalAssetsDir) && Directory.Exists(externalAssetsDir))
        {
            context.TrackDisposable(game.Files.RegisterDirectory(externalAssetsDir, priority: 25));
        }

        // Ensure MaxwellPet.ot is always resolvable through IFileOverlayService even when running without loose files.
        if (!game.Files.Exists(MaxwellProtocol.PetOtPath))
        {
            context.TrackDisposable(game.Files.RegisterVirtualTextFile(
                MaxwellProtocol.PetOtPath,
                BuildFallbackPetOt(),
                priority: 50));
        }

        // 2. Register append-only content declarations for the brand-new Pet MTX (never replacing a stock pet)
        context.TrackDisposable(game.Content.DeclareDatRow(new DatRowDeclaration(
            TableName: "MtxTypes",
            EntryKey: MaxwellProtocol.MtxTypeId,
            Fields: new Dictionary<string, object?>
            {
                ["Id"] = MaxwellProtocol.MtxTypeId,
                ["HASH32"] = MaxwellProtocol.MtxTypeHash,
                ["Name"] = MaxwellProtocol.MtxDisplayName,
                ["Description1"] = MaxwellProtocol.MtxDescription
            })));

        context.TrackDisposable(game.Content.DeclareDatRow(new DatRowDeclaration(
            TableName: "MtxTypeGameSpecific",
            EntryKey: MaxwellProtocol.MtxTypeId,
            Fields: new Dictionary<string, object?>
            {
                ["Type"] = MaxwellProtocol.MtxTypesAppendedRow,
                ["HASH32"] = MaxwellProtocol.MtxTypeHash,
                ["DDSFile"] = MaxwellProtocol.PetIconPath,
                ["Category"] = MaxwellProtocol.MtxPetCategory
            })));

        context.TrackDisposable(game.Content.DeclareDatRow(new DatRowDeclaration(
            TableName: "Pet",
            EntryKey: MaxwellProtocol.PetMetadataPath,
            Fields: new Dictionary<string, object?>
            {
                ["Id"] = MaxwellProtocol.PetMetadataPath,
                ["MtxTypeGameSpecificKey"] = MaxwellProtocol.MtxTypeGameSpecificAppendedRow,
                ["HASH32"] = MaxwellProtocol.PetObjectTypeHash,
                ["HASH16"] = MaxwellProtocol.PetHash16
            })));

        context.TrackDisposable(game.Content.DeclareWorldObject(new WorldObjectDeclaration(
            MetadataPath: MaxwellProtocol.PetMetadataPath,
            CarrierTable: "Pet",
            ScriptPath: MaxwellProtocol.PetOtPath)));

        // 3. Register custom GEAL built-in 'MaxwellDance'
        context.TrackDisposable(game.GealBuiltins.Register(MaxwellProtocol.GealDanceBuiltin, (objectId, args) =>
        {
            int boost = args.Count > 0 ? Math.Clamp(args.Int(0), 1, 8) : 1;
            _danceCalls++;
            _currentYawDegrees = (_currentYawDegrees + 45f * boost) % 360f;
            if (_window is not null)
            {
                _window.SetProperty("maxwell_dance_calls", _danceCalls.ToString(CultureInfo.InvariantCulture));
                _window.SetProperty("maxwell_dance_object", $"0x{objectId:X}");
                RefreshTelemetryLabel();
            }

            if (objectId != 0)
            {
                game.World.PlayEffect(objectId, MaxwellProtocol.PetAoPath);
            }

            context.Logger.Info($"GEAL {MaxwellProtocol.GealDanceBuiltin} triggered on object 0x{objectId:X} (boost={boost}, total={_danceCalls}).");
            return GealValue.FromInt(_danceCalls);
        }));

        // 4. Register per-area custom ruleset and opcode handler
        context.TrackDisposable(game.Rulesets.Register<MaxwellRuleset>());
        context.TrackDisposable(game.Opcodes.RegisterHandler(
            MaxwellProtocol.OpcodeStateSync,
            payload => context.Logger.Debug($"Received raw MaxwellStateSync opcode ({payload.Length} bytes)."),
            MaxwellProtocol.ModId));

        // 5. Subscribe to server-pushed messages over context.Net
        context.TrackDisposable(context.Net.On<OpenMaxwellPanel>(cmd =>
        {
            EnsureWindowLoaded();
            ApplyState(cmd.InitialState, forceShowWindow: true);
        }));

        context.TrackDisposable(context.Net.On<MaxwellStateSync>(state =>
        {
            ApplyState(state, forceShowWindow: false);
        }));

        // 6. Per-frame spinning & floating tick on the game thread
        game.Frame += OnFrame;
    }

    public void Unload()
    {
        if (_context is not null)
        {
            _context.Game.Frame -= OnFrame;
        }

        StopMusicIfPlaying();
        _window?.Close();
        _window = null;
        _lastState = null;
        _context = null;
    }

    private void OnFrame(float deltaSeconds)
    {
        if (deltaSeconds <= 0f)
            return;

        _frameCount++;

        var state = _lastState;
        if (state is { IsSummoned: true })
        {
            _currentYawDegrees = (_currentYawDegrees + state.SpinSpeedDegPerSec * deltaSeconds) % 360f;
            if (_currentYawDegrees < 0f)
                _currentYawDegrees += 360f;

            _hoverPhaseRadians = (_hoverPhaseRadians + HoverFrequencyRadPerSec * deltaSeconds) % (MathF.PI * 2f);
            _currentFloatOffset = MathF.Max(0f, state.FloatHeight + MathF.Sin(_hoverPhaseRadians) * HoverAmplitude);
        }
        else
        {
            _currentFloatOffset = 0f;
        }

        if (_window is not null)
        {
            _window.SetProperty("maxwell_yaw", _currentYawDegrees.ToString("0.0", CultureInfo.InvariantCulture));
            _window.SetProperty("maxwell_float", _currentFloatOffset.ToString("0.0", CultureInfo.InvariantCulture));
            _window.SetProperty("frame_ticks", _frameCount.ToString(CultureInfo.InvariantCulture));
            RefreshTelemetryLabel();
        }
    }

    private void EnsureWindowLoaded()
    {
        if (_context is null || _window is not null)
            return;

        _window = _context.Game.Ui.Load(MaxwellProtocol.PanelUiPath, MaxwellProtocol.PanelWindowId);

        var spinBox = _window.Require<ITextBox>("spin_box");
        var floatBox = _window.Require<ITextBox>("float_box");
        var muteBox = _window.Require<ICheckBox>("music_mute_checkbox");

        _window.Require<IButton>("summon_button").Clicked += () =>
        {
            bool currentlySummoned = _lastState?.IsSummoned ?? false;
            _context.Net.Send(new ToggleMaxwellRequest(Summon: !currentlySummoned));
        };

        _window.Require<IButton>("spin_slower_button").Clicked += () =>
        {
            float next = Math.Clamp((_lastState?.SpinSpeedDegPerSec ?? MaxwellProtocol.DefaultSpinSpeedDegPerSec) - SpinStepDegPerSec,
                MaxwellProtocol.MinSpinSpeedDegPerSec, MaxwellProtocol.MaxSpinSpeedDegPerSec);
            _context.Net.Send(new ToggleMaxwellRequest(SpinSpeedDegPerSec: next));
        };

        _window.Require<IButton>("spin_faster_button").Clicked += () =>
        {
            float next = Math.Clamp((_lastState?.SpinSpeedDegPerSec ?? MaxwellProtocol.DefaultSpinSpeedDegPerSec) + SpinStepDegPerSec,
                MaxwellProtocol.MinSpinSpeedDegPerSec, MaxwellProtocol.MaxSpinSpeedDegPerSec);
            _context.Net.Send(new ToggleMaxwellRequest(SpinSpeedDegPerSec: next));
        };

        spinBox.Submitted += text =>
        {
            if (float.TryParse(text, CultureInfo.InvariantCulture, out float parsed))
            {
                _context.Net.Send(new ToggleMaxwellRequest(SpinSpeedDegPerSec: parsed));
            }
        };

        _window.Require<IButton>("float_lower_button").Clicked += () =>
        {
            float next = Math.Clamp((_lastState?.FloatHeight ?? MaxwellProtocol.DefaultFloatHeight) - FloatStep,
                MaxwellProtocol.MinFloatHeight, MaxwellProtocol.MaxFloatHeight);
            _context.Net.Send(new ToggleMaxwellRequest(FloatHeight: next));
        };

        _window.Require<IButton>("float_higher_button").Clicked += () =>
        {
            float next = Math.Clamp((_lastState?.FloatHeight ?? MaxwellProtocol.DefaultFloatHeight) + FloatStep,
                MaxwellProtocol.MinFloatHeight, MaxwellProtocol.MaxFloatHeight);
            _context.Net.Send(new ToggleMaxwellRequest(FloatHeight: next));
        };

        floatBox.Submitted += text =>
        {
            if (float.TryParse(text, CultureInfo.InvariantCulture, out float parsed))
            {
                _context.Net.Send(new ToggleMaxwellRequest(FloatHeight: parsed));
            }
        };

        _window.Require<IButton>("music_play_button").Clicked += () =>
        {
            bool playing = _lastState?.MusicPlaying ?? false;
            _context.Net.Send(new MaxwellAudioControl(Play: !playing));
        };

        muteBox.CheckedChanged += isMuted =>
        {
            if (_suppressUiEvents)
                return;

            _context.Net.Send(new MaxwellAudioControl(Muted: isMuted));
        };

        _window.Require<IButton>("vol_down_button").Clicked += () =>
        {
            float next = MathF.Round(Math.Clamp((_lastState?.MusicVolume ?? MaxwellProtocol.DefaultMusicVolume) - VolumeStep, 0f, 1f) * 100f) / 100f;
            _context.Net.Send(new MaxwellAudioControl(Volume: next));
        };

        _window.Require<IButton>("vol_up_button").Clicked += () =>
        {
            float next = MathF.Round(Math.Clamp((_lastState?.MusicVolume ?? MaxwellProtocol.DefaultMusicVolume) + VolumeStep, 0f, 1f) * 100f) / 100f;
            _context.Net.Send(new MaxwellAudioControl(Volume: next));
        };

        _window.Require<IButton>("dance_button").Clicked += () =>
        {
            uint targetId = _lastState?.PetEntityId ?? 0u;
            _context.Game.GealBuiltins.Invoke(MaxwellProtocol.GealDanceBuiltin, targetId, GealValue.FromInt(2));
        };

        _window.Require<IButton>("close_button").Clicked += () =>
        {
            _window.Visible = false;
        };
    }

    private void ApplyState(MaxwellStateSync state, bool forceShowWindow)
    {
        _lastState = state;
        SyncLoopingAudio(state);

        if (forceShowWindow || _window is not null)
        {
            EnsureWindowLoaded();
        }

        if (_window is null)
            return;

        if (forceShowWindow)
            _window.Visible = true;

        _suppressUiEvents = true;
        try
        {
            _window.Require<ILabel>("status_label").Text = state.StatusMessage;
            _window.Require<IButton>("summon_button").Text = state.IsSummoned ? "Dismiss Maxwell" : "Summon Maxwell";
            _window.Require<ITextBox>("spin_box").Text = state.SpinSpeedDegPerSec.ToString("0", CultureInfo.InvariantCulture);
            _window.Require<ITextBox>("float_box").Text = state.FloatHeight.ToString("0.#", CultureInfo.InvariantCulture);
            _window.Require<IButton>("music_play_button").Text = state.MusicPlaying ? "Stop Music" : "Play Music";
            _window.Require<ICheckBox>("music_mute_checkbox").IsChecked = state.MusicMuted;
            _window.Require<ILabel>("volume_label").Text =
                string.Create(CultureInfo.InvariantCulture, $"Volume: {(int)MathF.Round(state.MusicVolume * 100f)}%");
            RefreshTelemetryLabel();
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private void RefreshTelemetryLabel()
    {
        if (_window?.Find<ILabel>("telemetry_label") is not { } telemetry)
            return;

        var state = _lastState;
        float spin = state?.SpinSpeedDegPerSec ?? MaxwellProtocol.DefaultSpinSpeedDegPerSec;
        float height = state is { IsSummoned: true } ? _currentFloatOffset : state?.FloatHeight ?? MaxwellProtocol.DefaultFloatHeight;
        string music = _activeAudioHandle.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"Looping ({(int)MathF.Round((state?.MusicVolume ?? MaxwellProtocol.DefaultMusicVolume) * 100f)}%)")
            : state is { MusicMuted: true } ? "Muted" : "Stopped";

        telemetry.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"Yaw: {_currentYawDegrees:0.0} deg | Spin: {spin:0} deg/s | Float: {height:0.0} | Dances: {_danceCalls} | Music: {music}");
    }

    private void SyncLoopingAudio(MaxwellStateSync state)
    {
        if (_context is null)
            return;

        bool shouldPlay = state.IsSummoned && state.MusicPlaying && !state.MusicMuted && state.MusicVolume > 0.001f;
        if (!shouldPlay)
        {
            StopMusicIfPlaying();
            return;
        }

        var audio = _context.Game.Audio;
        if (_activeAudioHandle is int existingHandle)
        {
            if (MathF.Abs(_activeAudioVolume - state.MusicVolume) > 0.005f)
            {
                if (!TrySetChannelVolume(audio, existingHandle, state.MusicVolume))
                {
                    audio.StopChannel(existingHandle);
                    _activeAudioHandle = PlayLoopingSoundFile(audio, MaxwellProtocol.MusicOggPath, state.MusicVolume);
                }

                _activeAudioVolume = state.MusicVolume;
            }

            return;
        }

        _activeAudioHandle = PlayLoopingSoundFile(audio, MaxwellProtocol.MusicOggPath, state.MusicVolume);
        _activeAudioVolume = state.MusicVolume;
        _window?.SetProperty("maxwell_audio_path", MaxwellProtocol.MusicOggPath);
        _context.Logger.Info($"Started looping Maxwell audio '{MaxwellProtocol.MusicOggPath}' (handle={_activeAudioHandle}, volume={state.MusicVolume:0.00}).");
    }

    private void StopMusicIfPlaying()
    {
        if (_activeAudioHandle is int handle && _context is not null)
        {
            _context.Game.Audio.StopChannel(handle);
            _context.Logger.Info($"Stopped Maxwell audio channel {handle}.");
        }

        _activeAudioHandle = null;
        _activeAudioVolume = -1f;
        _window?.SetProperty("maxwell_audio_path", string.Empty);
    }

    /// <summary>
    /// Invokes <c>IAudioService.PlaySoundFile(string, float, bool)</c> with <c>loop: true</c> when provided
    /// by the live 3.29.3d FMOD host, falling back cleanly to 2-arg <c>PlaySoundFile(string, float)</c>.
    /// </summary>
    private static int PlayLoopingSoundFile(IAudioService audio, string virtualOggPath, float volume)
    {
        MethodInfo? loopOverload = typeof(IAudioService).GetMethod(
            nameof(IAudioService.PlaySoundFile),
            [typeof(string), typeof(float), typeof(bool)]);

        if (loopOverload is not null)
        {
            object? result = loopOverload.Invoke(audio, [virtualOggPath, volume, true]);
            if (result is int handle)
                return handle;
        }

        return audio.PlaySoundFile(virtualOggPath, volume);
    }

    private static bool TrySetChannelVolume(IAudioService audio, int channelHandle, float volume)
    {
        MethodInfo? setVolume = typeof(IAudioService).GetMethod(
            "SetChannelVolume",
            [typeof(int), typeof(float)]);

        if (setVolume is null)
            return false;

        setVolume.Invoke(audio, [channelHandle, volume]);
        return true;
    }

    private static string BuildFallbackPetOt() =>
        """
        version 2
        extends "Metadata/Pet/Pet"

        Animated
        {
        	animated_object = "Metadata/Pet/Mods/Maxwell/MaxwellPet.ao"
        }
        """;
}
