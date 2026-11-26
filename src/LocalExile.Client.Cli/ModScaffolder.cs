using System.Globalization;
using System.Text;
using LocalExile.Client.Waypoint;

namespace LocalExile.Client.Cli;

/// <summary>
/// Supported <c>nativemods new</c> project templates.
/// </summary>
public enum ModTemplateKind
{
    Tier1,
    Dual,
    Content,
}

/// <summary>
/// Summary of a completed scaffold operation.
/// </summary>
public sealed record ScaffoldResult(
    ModTemplateKind Template,
    string ModId,
    string OutputDirectory,
    IReadOnlyList<string> CreatedFiles);

/// <summary>
/// Scaffolds clean, validator-passing NativeMods projects for Tier 1 (Roslyn server-only),
/// Tier 2 (dual-side client + server + <c>.ui</c>), and declarative <c>.datpatch.json</c> content mods.
/// </summary>
public static class ModScaffolder
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>SDK range compatible across 0.4.0 and 1.x releases.</summary>
    public const string DefaultSdkVersionRange = ">=0.4.0 <2.0.0";

    public static ScaffoldResult Scaffold(
        ModTemplateKind template,
        string rawModId,
        string outputDirectory,
        string? displayName = null,
        string? author = null,
        bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawModId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string canonicalId = NormalizeCanonicalId(rawModId);
        if (!DualModManifest.IsCanonicalCatalogId(canonicalId))
        {
            throw new ArgumentException(
                $"Invalid mod id '{rawModId}'. Mod IDs must start with a letter or digit and contain only letters, digits, '_', '.', or '-'.",
                nameof(rawModId));
        }

        string fullOutDir = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(fullOutDir) && !force && Directory.EnumerateFileSystemEntries(fullOutDir).Any())
        {
            throw new InvalidOperationException(
                $"Output directory '{fullOutDir}' already exists and is not empty. Pass --force to overwrite.");
        }

        Directory.CreateDirectory(fullOutDir);

        string pascal = ToPascalIdentifier(rawModId);
        string resolvedDisplayName = string.IsNullOrWhiteSpace(displayName) ? ToDisplayName(rawModId) : displayName.Trim();
        string resolvedAuthor = string.IsNullOrWhiteSpace(author) ? "LocalExile Community" : author.Trim();

        var created = new List<string>();
        switch (template)
        {
            case ModTemplateKind.Tier1:
                ScaffoldTier1(fullOutDir, canonicalId, pascal, resolvedDisplayName, resolvedAuthor, created);
                break;
            case ModTemplateKind.Dual:
                ScaffoldDual(fullOutDir, canonicalId, pascal, resolvedDisplayName, resolvedAuthor, created);
                break;
            case ModTemplateKind.Content:
                ScaffoldContent(fullOutDir, canonicalId, pascal, resolvedDisplayName, resolvedAuthor, created);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(template), template, "Unknown template kind.");
        }

        return new ScaffoldResult(template, canonicalId, fullOutDir, created);
    }

    public static string NormalizeCanonicalId(string rawModId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawModId);
        string trimmed = rawModId.Trim();
        var sb = new StringBuilder(trimmed.Length);
        char prev = '\0';

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(prev) || char.IsDigit(prev)))
                {
                    sb.Append('-');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsLower(c) || char.IsDigit(c) || c is '-' or '_' or '.')
            {
                sb.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                sb.Append('-');
            }

            prev = c;
        }

        return sb.ToString().Trim('-', '_', '.');
    }

    public static string ToPascalIdentifier(string rawModId)
    {
        var sb = new StringBuilder();
        bool capitalizeNext = true;
        foreach (char c in rawModId.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
                capitalizeNext = false;
            }
            else
            {
                capitalizeNext = true;
            }
        }

        if (sb.Length == 0 || char.IsDigit(sb[0]))
        {
            sb.Insert(0, "Mod");
        }

        return sb.ToString();
    }

    private static string ToDisplayName(string rawModId)
    {
        string normalized = NormalizeCanonicalId(rawModId);
        string[] parts = normalized.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return rawModId.Trim();
        }

        return string.Join(' ', parts.Select(p =>
            p.Length == 1
                ? p.ToUpperInvariant()
                : char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static void ScaffoldTier1(
        string outDir,
        string modId,
        string pascal,
        string displayName,
        string author,
        List<string> created)
    {
        string modJson = $$"""
        {
          "id": "{{modId}}",
          "name": "{{displayName}}",
          "description": "Tier 1 single-file Roslyn server mod powered by LocalExileGatewayHost and ServerBridge.",
          "author": "{{author}}",
          "version": "1.0.0",
          "compatibleClientVersions": [
            "0.5.2-beta"
          ],
          "kind": "script",
          "depends": [
            "Game"
          ],
          "assembly": "{{modId}}.dll",
          "enabled": true
        }
        """;

        string entryCs = $$"""
        #nullable enable
        // Tier 1 single-file Roslyn LocalExile Gateway IMod entry point for '{{modId}}'.
        // Drop this folder into LocalExile's Mods/ directory — requires zero client-side DLLs!

        using System;
        using System.Threading.Tasks;
        using GameServer.Modding;
        using LocalExile.Client.Sdk.Engine;
        using LocalExile.Client.Sdk.Extend;
        using LocalExile.Client.ServerBridge;

        public sealed class {{pascal}}Settings
        {
            public bool Enabled { get; set; } = true;
            public string GreetingMessage { get; set; } = "{{displayName}} ready!";
        }

        public sealed class {{pascal}}LocalExileEntry : IMod, IDisposable
        {
            private const string ChannelName = "{{modId}}";
            private const string ButtonId = "{{modId}}.action";
            private const string HotkeyId = "{{modId}}.trigger";

            private LocalExileGatewayHost? _gatewayHost;
            private {{pascal}}Settings _settings = new();

            public void Load(IModContext mod)
            {
                _settings = LoadSettings(mod);

                _gatewayHost = LocalExileGatewayHost.Create(mod, builder => builder
                    .WithBridgeOptions(new ClientBridgeOptions
                    {
                        ServerVersion = "LocalExile-3.29.3d",
                        HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                        DefaultChannel = ChannelName,
                    })
                    .WithBus(
                        attach: true,
                        onDispatchError: (owner, ex) => mod.Log($"[{{modId}}] bus '{owner}' failed: {ex.Message}"))
                    .OnClientReady(async ctx =>
                    {
                        if (!_settings.Enabled || _gatewayHost is null)
                        {
                            return;
                        }

                        IClientSession session = _gatewayHost.Bridge.For(ctx.ConnectionId);

                        await session.RegisterSlotButtonAsync(
                            ScreenSlot.HudTopRight,
                            ButtonId,
                            "{{displayName}}",
                            onClick: s => s.ShowToastAsync("{{displayName}}", _settings.GreetingMessage, ToastSeverity.Success),
                            options: new SlotAttachmentOptions(Order: 40, Width: 110f, Height: 26f, Tooltip: "Trigger {{displayName}} (Alt+K)."));

                        await session.RegisterHotkeyAsync(
                            HotkeyId,
                            "{{displayName}} Action",
                            KeyChord.FromKey('K', KeyModifiers.Alt),
                            onTriggered: (s, _, ct) => s.ShowToastAsync("{{displayName}}", _settings.GreetingMessage, ToastSeverity.Info, cancellationToken: ct),
                            allowWhenTextInputFocused: false,
                            suppressGameInput: true);
                    }));

                mod.Log("Loaded Tier 1 mod '{{modId}}' via LocalExileGatewayHost.");
            }

            public void Dispose()
            {
                _gatewayHost?.Dispose();
                _gatewayHost = null;
            }

            private static {{pascal}}Settings LoadSettings(IModContext mod)
            {
                try
                {
                    return mod.Settings<{{pascal}}Settings>();
                }
                catch
                {
                    return new {{pascal}}Settings();
                }
            }
        }
        """;

        string settingsJson = $$"""
        {
          "version": 1,
          "settings": {
            "enabled": {
              "type": "bool",
              "default": true,
              "label": "Enable {{displayName}}",
              "description": "Enables the {{displayName}} HUD button and hotkey."
            },
            "greetingMessage": {
              "type": "string",
              "default": "{{displayName}} ready!",
              "label": "Toast Message",
              "description": "Message displayed in the NativeMods HUD toast when triggered."
            }
          }
        }
        """;

        string readme = $$"""
        # {{displayName}} (`{{modId}}`)

        Tier 1 single-file Roslyn server mod for LocalExile + NativeMods `ServerBridge`.

        ## Files

        - `mod.json` — LocalExile mod manifest
        - `{{pascal}}LocalExileEntry.cs` — single-file `IMod` wired to `LocalExileGatewayHost` + Tier 1 HUD button, hotkey, and toast APIs
        - `settings.json` — Waypoint-compatible mod settings schema

        ## Validate

        ```bash
        nativemods validate .
        ```
        """;

        WriteTextFile(outDir, "mod.json", modJson, created);
        WriteTextFile(outDir, $"{pascal}LocalExileEntry.cs", entryCs, created);
        WriteTextFile(outDir, "settings.json", settingsJson, created);
        WriteTextFile(outDir, "README.md", readme, created);
    }

    private static void ScaffoldDual(
        string outDir,
        string modId,
        string pascal,
        string displayName,
        string author,
        List<string> created)
    {
        string uiVirtualPath = $"Metadata/UI/Mods/{modId}/Panel.ui";
        string panelWindowId = $"{modId.Replace('-', '_').Replace('.', '_')}_panel";

        string modJson = $$"""
        {
          "id": "{{modId}}",
          "name": "{{displayName}}",
          "description": "Tier 2 dual-side NativeMods mod with shared protocol contracts, client SDK module, native 3.29 .ui panel, and LocalExile server bridge.",
          "author": "{{author}}",
          "version": "1.0.0",
          "compatibleClientVersions": [
            "0.5.2-beta"
          ],
          "kind": "feature",
          "depends": [
            "Game"
          ],
          "assembly": "{{modId}}.dll",
          "enabled": true,
          "nativeClient": {
            "required": false,
            "sdkVersionRange": "{{DefaultSdkVersionRange}}",
            "supportedBuilds": [
              "3.29.3d",
              "3.29.3c"
            ],
            "assembly": "{{modId}}.Client.dll",
            "payloadDir": "client",
            "overlays": [
              "{{uiVirtualPath}}"
            ],
            "replaces": [],
            "rawAccess": false,
            "customOpcodes": [],
            "gealBuiltins": [],
            "customControls": [],
            "contentRegistries": [],
            "serverHalf": {
              "id": "{{modId}}",
              "minVersion": "1.0.0",
              "supportsGracefulFallback": true,
              "channelName": "{{modId}}"
            }
          }
        }
        """;

        string sharedCsproj = $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <RootNamespace>DualMod.{{pascal}}.Shared</RootNamespace>
            <AssemblyName>DualMod.{{pascal}}.Shared</AssemblyName>
          </PropertyGroup>
        </Project>
        """;

        string sharedMessages = $$"""
        namespace DualMod.{{pascal}}.Shared;

        /// <summary>
        /// Shared constants and typed RPC messages for <c>{{modId}}</c>.
        /// </summary>
        public static class {{pascal}}Protocol
        {
            public const string ModId = "{{modId}}";
            public const string ChannelName = "{{modId}}";
            public const string PanelUiPath = "{{uiVirtualPath}}";
            public const string PanelWindowId = "{{panelWindowId}}";
            public const string SlotButtonId = "{{modId}}.panel";
            public const string ToggleKeybindId = "{{modId}}.toggle_panel";
        }

        public sealed record {{pascal}}PingRequest(string Note);

        public sealed record {{pascal}}PingResponse(string Reply, int RequestCount);
        """;

        string clientCsproj = $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <RootNamespace>DualMod.{{pascal}}.Client</RootNamespace>
            <AssemblyName>{{modId}}.Client</AssemblyName>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="..\Shared\*.cs" Link="Shared\%(Filename)%(Extension)" />
          </ItemGroup>
          <ItemGroup>
            <!-- Point to your NativeMods checkout or NuGet package for LocalExile.Client.Sdk -->
            <ProjectReference Include="..\..\..\src\LocalExile.Client.Sdk\LocalExile.Client.Sdk.csproj" />
          </ItemGroup>
        </Project>
        """;

        string clientMod = $$"""
        using DualMod.{{pascal}}.Shared;
        using LocalExile.Client.Sdk;
        using LocalExile.Client.Sdk.Engine;

        namespace DualMod.{{pascal}}.Client;

        /// <summary>
        /// Client half of <c>{{modId}}</c> implementing <see cref="IClientMod"/>.
        /// </summary>
        public sealed class {{pascal}}ClientMod : IClientMod
        {
            private IClientContext? _context;
            private IUiWindow? _panel;
            private bool _unloading;

            public void Load(IClientContext context)
            {
                ArgumentNullException.ThrowIfNull(context);
                _context = context;
                _unloading = false;

                context.TrackDisposable(context.Game.Screens.RegisterSlotButton(
                    ScreenSlot.HudTopRight,
                    {{pascal}}Protocol.SlotButtonId,
                    "{{displayName}}",
                    TogglePanel,
                    new SlotAttachmentOptions(Order: 50, Width: 108f, Height: 26f, Tooltip: "Open {{displayName}} panel (Alt+P).")));

                context.TrackDisposable(context.Game.Input.Keybinds.Register(
                    {{pascal}}Protocol.ToggleKeybindId,
                    "Toggle {{displayName}} Panel",
                    KeyChord.FromKey('P', KeyModifiers.Alt),
                    () =>
                    {
                        if (!_unloading && _context is { Game.Input.IsTextInputFocused: false })
                        {
                            TogglePanel();
                        }
                    },
                    allowWhenTextInputFocused: false,
                    suppressGameInput: true));

                context.TrackDisposable(context.Net.On<{{pascal}}PingResponse>(response =>
                    DispatchOnGameThread(() =>
                    {
                        if (_unloading || _context is not { } ctx)
                        {
                            return;
                        }

                        if (_panel is { IsOpen: true } win && win.Find<ILabel>("status_label") is { } label)
                        {
                            label.Text = $"{response.Reply} (#{response.RequestCount})";
                        }

                        ctx.Game.Toasts.Show("{{displayName}}", response.Reply, ToastSeverity.Success);
                    })));

                context.Logger.Info("Loaded {{modId}} client module.");
            }

            public void Unload()
            {
                _unloading = true;
                ClosePanel();
                _context = null;
            }

            public void TogglePanel()
            {
                DispatchOnGameThread(() =>
                {
                    if (_unloading || _context is not { } ctx)
                    {
                        return;
                    }

                    if (_panel is { IsOpen: true })
                    {
                        ClosePanel();
                        return;
                    }

                    IUiWindow window = ctx.Game.Ui.Load({{pascal}}Protocol.PanelUiPath, {{pascal}}Protocol.PanelWindowId);
                    _panel = window;
                    window.Closed += () =>
                    {
                        if (ReferenceEquals(_panel, window))
                        {
                            _panel = null;
                        }
                    };

                    if (window.Find<IButton>("close_button") is { } closeBtn)
                    {
                        closeBtn.Clicked += ClosePanel;
                    }

                    if (window.Find<IButton>("ping_button") is { } pingBtn)
                    {
                        pingBtn.Clicked += () => ctx.Net.Send(new {{pascal}}PingRequest("hello from client"));
                    }
                });
            }

            private void ClosePanel()
            {
                IUiWindow? win = _panel;
                _panel = null;
                if (win is { IsOpen: true })
                {
                    win.Close();
                }
            }

            private void DispatchOnGameThread(Action action)
            {
                if (_context is not { } ctx)
                {
                    return;
                }

                if (ctx.Scheduler.IsOnGameThread)
                {
                    action();
                }
                else
                {
                    ctx.Scheduler.RunOnGameThread(action);
                }
            }
        }
        """;

        string serverCsproj = $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <RootNamespace>DualMod.{{pascal}}.Server</RootNamespace>
            <AssemblyName>{{modId}}</AssemblyName>
          </PropertyGroup>
          <ItemGroup>
            <Compile Remove="{{pascal}}LocalExileEntry.cs" />
            <None Include="{{pascal}}LocalExileEntry.cs" CopyToOutputDirectory="PreserveNewest" />
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="..\Shared\DualMod.{{pascal}}.Shared.csproj" />
            <ProjectReference Include="..\..\..\src\LocalExile.Client.ServerBridge\LocalExile.Client.ServerBridge.csproj" />
          </ItemGroup>
        </Project>
        """;

        string serverMod = $$"""
        using System.Threading;
        using DualMod.{{pascal}}.Shared;
        using LocalExile.Client.ServerBridge;

        namespace DualMod.{{pascal}}.Server;

        /// <summary>
        /// Server half of <c>{{modId}}</c> handling typed RPC requests over <see cref="ClientBridgeHost"/>.
        /// </summary>
        public sealed class {{pascal}}ServerMod : IDisposable
        {
            private readonly ClientBridgeHost _bridge;
            private IDisposable? _subscription;
            private int _count;

            public {{pascal}}ServerMod(ClientBridgeHost bridge)
            {
                ArgumentNullException.ThrowIfNull(bridge);
                _bridge = bridge;
            }

            public void Attach()
            {
                _subscription = _bridge.On<{{pascal}}PingRequest>({{pascal}}Protocol.ChannelName, async (session, request) =>
                {
                    int next = Interlocked.Increment(ref _count);
                    await session.SendAsync({{pascal}}Protocol.ChannelName, new {{pascal}}PingResponse($"Server received: {request.Note}", next));
                });
            }

            public void Dispose()
            {
                _subscription?.Dispose();
                _subscription = null;
            }
        }
        """;

        string serverEntry = $$"""
        #nullable enable
        // LocalExile Gateway IMod entry point for dual-side mod '{{modId}}'.

        using System;
        using DualMod.{{pascal}}.Server;
        using DualMod.{{pascal}}.Shared;
        using GameServer.Modding;
        using LocalExile.Client.Sdk.Extend;
        using LocalExile.Client.ServerBridge;

        public sealed class {{pascal}}LocalExileEntry : IMod, IDisposable
        {
            private LocalExileGatewayHost? _gatewayHost;
            private {{pascal}}ServerMod? _serverMod;

            public void Load(IModContext mod)
            {
                _gatewayHost = LocalExileGatewayHost.Create(mod, builder => builder
                    .WithBridgeOptions(new ClientBridgeOptions
                    {
                        ServerVersion = "LocalExile-3.29.3d",
                        HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId,
                        DefaultChannel = {{pascal}}Protocol.ChannelName,
                    })
                    .WithBus(
                        attach: true,
                        onDispatchError: (owner, ex) => mod.Log($"[{{modId}}] bus '{owner}' failed: {ex.Message}")));

                _serverMod = new {{pascal}}ServerMod(_gatewayHost.Bridge);
                _serverMod.Attach();
                mod.Log("Loaded dual-side mod '{{modId}}' via LocalExileGatewayHost.");
            }

            public void Dispose()
            {
                _serverMod?.Dispose();
                _serverMod = null;
                _gatewayHost?.Dispose();
                _gatewayHost = null;
            }
        }
        """;

        // Leading comment ensures <DEFINITIONS> never starts at byte 0 (preventing UI002).
        // Uses stock 3.29 controls with unique sibling IDs and zero UiLayoutLinter diagnostics.
        string panelUi = $$"""
        // {{displayName}} ({{modId}}) — Native 3.29.3d UI Panel.
        // Validated with UiLayoutLinter (0 diagnostics).

        <DEFINITIONS>

        const $w = 420;
        const $inner = 380;

        begin Frame
        	id = {{panelWindowId}};
        	aspect_mode = scaley;
        	size = $w, 240;
        	min_size = $w, 220;
        	auto_size = false, true;
        	float = centre;
        	y = 140;
        	visible = true;
        	mouse_enabled = true;
        	propagate_properties = false;

        	background_colour = argb( 236, 12, 14, 20 );
        	pixel_border_colour = rgb( 137, 100, 64 );

        	begin StackLayout
        		id = body;
        		auto_size = true, true;
        		margin = 20, 14, 20, 16;
        		float = centre;

        		begin Frame
        			id = title_bar;
        			size = $inner, 40;
        			margin = 0, 0, 0, 10;

        			begin Label
        				id = title_label;
        				location = 40, 0;
        				size = $inner - 80, 40;
        				auto_size = false;
        				text_align = MiddleCentre;
        				font = Fonts.LargeSC;
        				foreground_colour = rgb( 227, 194, 146 );
        				outline_colour = rgb( 0, 0, 0 );
        				text = "{{displayName}}";
        			end

        			begin BasicButton
        				id = close_button;
        				location = $inner - 36, 2;
        				size = 36, 36;
        				mouse_enabled = true;
        				background_colour = argb( 230, 62, 28, 28 );
        				pixel_border_colour = rgb( 170, 96, 86 );

        				begin Label
        					id = close_label;
        					location = 0, 0;
        					size = 36, 36;
        					auto_size = false;
        					text_align = MiddleCentre;
        					font = Fonts.SmallNormalSC;
        					foreground_colour = rgb( 245, 220, 215 );
        					mouse_enabled = false;
        					text = "X";
        				end
        			end
        		end

        		begin Label
        			id = status_label;
        			size = $inner, 48;
        			auto_size = false;
        			text_align = MiddleCentre;
        			font = Fonts.Normal;
        			foreground_colour = rgb( 212, 220, 232 );
        			text = "Connected to {{displayName}}.";
        		end

        		begin Frame
        			id = actions_row;
        			size = $inner, 44;
        			margin = 0, 10, 0, 0;

        			begin BasicButton
        				id = ping_button;
        				location = 90, 0;
        				size = 200, 42;
        				mouse_enabled = true;
        				background_colour = argb( 232, 38, 66, 52 );
        				pixel_border_colour = rgb( 116, 194, 154 );

        				begin Label
        					id = ping_label;
        					location = 0, 0;
        					size = 200, 42;
        					auto_size = false;
        					text_align = MiddleCentre;
        					font = Fonts.NormalSC;
        					foreground_colour = rgb( 232, 250, 240 );
        					mouse_enabled = false;
        					text = "Send Server Ping";
        				end
        			end
        		end
        	end
        end
        """;

        string settingsJson = $$"""
        {
          "version": 1,
          "settings": {
            "enabled": {
              "type": "bool",
              "default": true,
              "label": "Enable {{displayName}}",
              "description": "Enables {{displayName}} client and server features."
            }
          }
        }
        """;

        string readme = $$"""
        # {{displayName}} (`{{modId}}`)

        Tier 2 dual-side NativeMods mod targeting Path of Exile 3.29.3d (`sdkVersionRange: "{{DefaultSdkVersionRange}}"`).

        ## Layout

        - `Shared/` — shared protocol constants & RPC record contracts
        - `Client/` — `IClientMod` implementation against `LocalExile.Client.Sdk`
        - `Server/` — `ClientBridgeHost` server handler + `{{pascal}}LocalExileEntry.cs`
        - `client/{{uiVirtualPath}}` — native 3.29 `.ui` panel (0 `UiLayoutLinter` diagnostics)
        - `mod.json` / `settings.json` — dual-side Waypoint manifest & settings

        ## Validate

        ```bash
        nativemods validate .
        ```
        """;

        WriteTextFile(outDir, "mod.json", modJson, created);
        WriteTextFile(outDir, "settings.json", settingsJson, created);
        WriteTextFile(outDir, "README.md", readme, created);
        WriteTextFile(outDir, Path.Combine("Shared", $"DualMod.{pascal}.Shared.csproj"), sharedCsproj, created);
        WriteTextFile(outDir, Path.Combine("Shared", $"{pascal}Messages.cs"), sharedMessages, created);
        WriteTextFile(outDir, Path.Combine("Client", $"DualMod.{pascal}.Client.csproj"), clientCsproj, created);
        WriteTextFile(outDir, Path.Combine("Client", $"{pascal}ClientMod.cs"), clientMod, created);
        WriteTextFile(outDir, Path.Combine("Server", $"DualMod.{pascal}.Server.csproj"), serverCsproj, created);
        WriteTextFile(outDir, Path.Combine("Server", $"{pascal}ServerMod.cs"), serverMod, created);
        WriteTextFile(outDir, Path.Combine("Server", $"{pascal}LocalExileEntry.cs"), serverEntry, created);
        WriteTextFile(outDir, Path.Combine("client", "Metadata", "UI", "Mods", modId, "Panel.ui"), panelUi, created);
    }

    private static void ScaffoldContent(
        string outDir,
        string modId,
        string pascal,
        string displayName,
        string author,
        List<string> created)
    {
        string keyPrefix = modId.Replace('-', '_').Replace('.', '_');
        string itemMetaPath = $"Metadata/Items/Currency/Mods/{modId}/{pascal}Orb";
        string visualId = $"{pascal}OrbVisual";
        string modRowId = $"{pascal}CustomMod1";

        // payloadDir is "client/overlay" so declarative source patches live cleanly at client/patches/<id>.datpatch.json
        // without violating DualModPackageValidator's Mods/<id>/ runtime overlay namespace rule.
        string modJson = $$"""
        {
          "id": "{{modId}}",
          "name": "{{displayName}}",
          "description": "Declarative .datpatch.json content pack appending a custom currency, in-place stock stack-size tweak, and dead-stat Route A tooltip.",
          "author": "{{author}}",
          "version": "1.0.0",
          "compatibleClientVersions": [
            "0.5.2-beta"
          ],
          "kind": "feature",
          "depends": [
            "Game"
          ],
          "assembly": "{{modId}}.dll",
          "enabled": true,
          "nativeClient": {
            "required": true,
            "sdkVersionRange": "{{DefaultSdkVersionRange}}",
            "supportedBuilds": [
              "3.29.3d",
              "3.29.3c"
            ],
            "payloadDir": "client/overlay",
            "overlays": [],
            "replaces": [],
            "rawAccess": false,
            "customOpcodes": [],
            "gealBuiltins": [],
            "customControls": [],
            "contentRegistries": [
              {
                "table": "ItemVisualIdentity",
                "entryId": "{{visualId}}",
                "mode": "append"
              },
              {
                "table": "BaseItemTypes",
                "entryId": "{{itemMetaPath}}",
                "mode": "append"
              },
              {
                "table": "CurrencyItems",
                "entryId": "{{itemMetaPath}}",
                "mode": "append"
              },
              {
                "table": "Mods",
                "entryId": "{{modRowId}}",
                "mode": "append"
              }
            ]
          }
        }
        """;

        string datPatchJson = $$"""
        {
          "schemaVersion": 1,
          "modId": "{{modId}}",
          "allocateDeadStat": [
            {
              "key": "{{keyPrefix}}_power_pct",
              "isPublic": true,
              "isLocal": false,
              "isWeaponLocal": false,
              "semantics": 1,
              "description": {
                "variants": [
                  {
                    "condition": "1|#",
                    "text": "{0:+d}% increased {{displayName}} Power"
                  },
                  {
                    "condition": "#|-1",
                    "text": "{0:d}% reduced {{displayName}} Power",
                    "handlers": [
                      "negate 1"
                    ]
                  }
                ]
              }
            }
          ],
          "tables": {
            "ItemVisualIdentity": {
              "append": [
                {
                  "key": "{{keyPrefix}}_orb_visual",
                  "cloneFromRow": 0,
                  "fields": {
                    "Id": "{{visualId}}",
                    "DDSFile": "Art/2DItems/Currency/Mods/{{modId}}/{{pascal}}Orb.dds",
                    "AOFile": "Metadata/Items/Currency/CurrencyOrb.ao"
                  }
                }
              ]
            },
            "BaseItemTypes": {
              "append": [
                {
                  "key": "{{keyPrefix}}_orb",
                  "cloneFromId": "Metadata/Items/Currency/CurrencyRemoveMod",
                  "fields": {
                    "Id": "{{itemMetaPath}}",
                    "Name": "{{displayName}} Orb",
                    "ItemVisualIdentity": "$ref:ItemVisualIdentity:{{keyPrefix}}_orb_visual",
                    "Implicit_ModsKeys": [
                      "$ref:Mods:{{keyPrefix}}_custom_mod"
                    ]
                  }
                }
              ]
            },
            "CurrencyItems": {
              "append": [
                {
                  "key": "{{keyPrefix}}_orb_currency",
                  "cloneFromId": "Metadata/Items/Currency/CurrencyRemoveMod",
                  "fields": {
                    "BaseItemTypesKey": "$ref:BaseItemTypes:{{keyPrefix}}_orb",
                    "StackSize": 20,
                    "CurrencyTab_StackSize": 5000,
                    "Description": "Custom currency item added declaratively by {{displayName}}"
                  }
                }
              ],
              "modify": [
                {
                  "id": "Metadata/Items/Currency/CurrencyRemoveMod",
                  "fields": {
                    "StackSize": 5000,
                    "CurrencyTab_StackSize": 5000
                  }
                }
              ]
            },
            "Mods": {
              "append": [
                {
                  "key": "{{keyPrefix}}_custom_mod",
                  "cloneFromId": "Strength1",
                  "fields": {
                    "Id": "{{modRowId}}",
                    "Name": "of {{displayName}}",
                    "Level": 1,
                    "StatsKey1": "$ref:Stats:{{keyPrefix}}_power_pct",
                    "Stat1Min": 5,
                    "Stat1Max": 20
                  }
                }
              ]
            }
          }
        }
        """;

        string readme = string.Create(CultureInfo.InvariantCulture, $$"""
        # {{displayName}} (`{{modId}}`)

        Declarative `.datpatch.json` content pack for Path of Exile 3.29.3d.

        ## Files

        - `mod.json` — NativeMods content-pack manifest
        - `client/patches/{{modId}}.datpatch.json` — schema-driven `append` + `modify` + `allocateDeadStat` patch

        ## Validate & Merge

        ```bash
        nativemods validate .
        nativemods merge --stock /path/to/PoE/Client --out ./.merged-cache .
        ```
        """);

        WriteTextFile(outDir, "mod.json", modJson, created);
        WriteTextFile(outDir, Path.Combine("client", "patches", $"{modId}.datpatch.json"), datPatchJson, created);
        WriteTextFile(outDir, "README.md", readme, created);
    }

    private static void WriteTextFile(string rootDir, string relativePath, string content, List<string> created)
    {
        string fullPath = Path.Combine(rootDir, relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
        File.WriteAllText(fullPath, normalized, Utf8NoBom);
        created.Add(relativePath.Replace('\\', '/'));
    }
}
