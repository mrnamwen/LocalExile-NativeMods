# NativeMods Modding Reference (Humans & AI Agents)

A complete, copy-paste-ready guide to building, testing, packaging, and verifying **Path of Exile 3.29** mods for [LocalExile](https://github.com/LocalExile/LocalExile) using **NativeMods** (`SdkVersion 0.1.0-poc`, C ABI `0.1.0`).

> **For AI Coding Agents — Fast Orientation**
> - **Frozen target builds:** Path of Exile **3.29.3c** and **3.29.3d** only (Win64 / Proton).
> - **Never modify stock client files:** Never write to `Client/Game.exe`, `Client/Content.ggpk`, or `Client/Bundles2/`. All mod assets ship as loose overlays (`NATIVEMODS_OVERLAY_DIR` / `Mods/Client/<id>/files/`) or stored-block `NMB1` bundles.
> - **Reference implementations in this repo:**
>   - `examples/DualMod.HarvestGarden/` — Dual-side mod with custom UI control (`GrowthGauge`), GEAL built-in (`GardenRipen`), custom ruleset (`0xA1`), and graceful vanilla chat fallback.
>   - `examples/DualMod.MaxwellPet/` — Brand-new Pet MTX mod (appended `.datc64` rows, native Cosmetics `K` menu integration, `.ui` panel, looping `.ogg` audio, `.glb`/`.dds`/`.ast` pipeline via `scripts/build-maxwell-assets.sh`).
>   - `examples/DualMod.SiphonOrb/` — Brand-new dual-stage Custom Currency mod (**Orb of Siphoning** + **Charged Orb of Siphoning**): appended `BaseItemTypes`/`ItemVisualIdentity`/`CurrencyItems` `.datc64` rows, flood-fill alpha-cutout `78x78` RGBA8 currency icons, Recombinator-style `SiphonPicker.ui`, persistent `ModSection.Enchant` stored affix on the Charged Orb, and `C->S 0x0071`/`0x0072` currency routing via `scripts/build-siphon-assets.sh`.
> - **Key libraries:**
>   - Client mods compile against **`LocalExile.Client.Sdk`** *only* (never `LocalExile.Client.Host`).
>   - Server mods use **`LocalExile.Client.ServerBridge`** (networking, server-driven UI, reusable Pet MTX registration, custom currency registration, and item affix inspection/manipulation).
>   - Asset conversion & `.datc64` row appending use **`LocalExile.Client.AssetPipeline`**.
>   - Validation, `.ui` linting, `NMB1` bundling, and installation use **`LocalExile.Client.Waypoint`**.
> - **Build & test gate:** `dotnet build NativeMods.slnx -c Release` and `dotnet test NativeMods.slnx -c Release --no-build` (`TreatWarningsAsErrors=true`, `net10.0`).

---

## Table of Contents

1. [Quick-Start Mod Skeletons](#1-quick-start-mod-skeletons)
   - [1.1 Server-Only Mod (With Optional Server-Driven Native UI)](#11-server-only-mod-with-optional-server-driven-native-ui)
   - [1.2 Client-Only Mod](#12-client-only-mod)
   - [1.3 Dual-Side Waypoint Mod (Recommended)](#13-dual-side-waypoint-mod-recommended)
2. [Author & AI Agent Ground Rules / Invariants](#2-author--ai-agent-ground-rules--invariants)
3. [Complete Modding API Cookbook](#3-complete-modding-api-cookbook)
   - [3.1 Native `.ui` Windows & Typed Controls](#31-native-ui-windows--typed-controls)
   - [3.2 Virtual & Loose File Overlays](#32-virtual--loose-file-overlays)
   - [3.3 Looping Custom `.ogg` Audio & FMOD Events](#33-looping-custom-ogg-audio--fmod-events)
   - [3.4 Typed Server ↔ Client Networking & Graceful Fallback](#34-typed-server--client-networking--graceful-fallback)
   - [3.5 GEAL Built-Ins, Custom Rulesets & Custom Opcodes](#35-geal-built-ins-custom-rulesets--custom-opcodes)
   - [3.6 Adding Brand-New In-Game Content (NOT Replacers)](#36-adding-brand-new-in-game-content-not-replacers)
   - [3.7 Using `Raw` Memory & Native Hooks Safely](#37-using-raw-memory--native-hooks-safely)
4. [Testing, Cross-Compilation & Live Proton Verification](#4-testing-cross-compilation--live-proton-verification)
   - [4.1 Fast Deterministic Unit & E2E Testing](#41-fast-deterministic-unit--e2e-testing)
   - [4.2 Cross-Compiling Win64 DLLs & Wine Smoke Test](#42-cross-compiling-win64-dlls--wine-smoke-test)
   - [4.3 Live Testing Against Real 3.29.3d Under Proton (`Xvfb`)](#43-live-testing-against-real-3293d-under-proton-xvfb)
5. [Diagnostics & Troubleshooting Cheat Sheet](#5-diagnostics--troubleshooting-cheat-sheet)

---

## 1. Quick-Start Mod Skeletons

All `.NET` projects in NativeMods target `net10.0` with `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.

### 1.1 Server-Only Mod (With Optional Server-Driven Native UI)

A standard LocalExile server mod requires **no `nativeClient` block** in `mod.json` if it only uses server-side `GameEvents`. If you want to open native `.ui` windows on NativeMods-equipped clients *without* writing a client C# DLL, add a data-only `nativeClient` block containing your `.ui` overlay and drive it from the server via `session.Ui` (`IClientUiSession`).

```text
my-server-mod/
├─ mod.json
├─ settings.json                  # Optional; preserved across Waypoint upgrades
├─ MyServerMod.csproj             # Produces my-server-mod.dll
├─ MyServerMod.cs                 # Authoritative logic + ClientBridgeHost
└─ client/                        # Optional: data-only .ui overlays (no client DLL)
   └─ Metadata/UI/Mods/MyServerMod/Panel.ui
```

**`mod.json` (pure server-only — backward-compatible with stock Waypoint):**
```json
{
  "id": "my-server-mod",
  "name": "My Server Mod",
  "description": "Adds a custom slash command and optional native panel.",
  "author": "YourName",
  "version": "1.0.0",
  "compatibleClientVersions": ["0.5.2-beta"],
  "kind": "feature",
  "depends": ["Game"],
  "assembly": "my-server-mod.dll",
  "enabled": true
}
```

**`MyServerMod.csproj`:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>my-server-mod</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\LocalExile.Client.ServerBridge\LocalExile.Client.ServerBridge.csproj" />
  </ItemGroup>
</Project>
```

---

### 1.2 Client-Only Mod

Use a client-only mod for local HUD overlays, visual tweaks, custom audio feedback, or client-side `.ui` tools that need no server half. Deployed into `<LocalExileRoot>/Mods/Client/<id>/`.

```text
my-hud/
├─ mod.json
├─ MyHud.Client.csproj            # Produces my-hud.Client.dll
├─ MyHudClientMod.cs              # Implements IClientMod
└─ client/
   └─ Metadata/UI/Mods/Hud/Overlay.ui
```

> **Namespace Tip:** `DualModPackageValidator.IsModNamespacedPath` allows `Mods/<id>/` **or** `Mods/<last-hyphen-segment>/`. For `id = "my-hud"`, both `Mods/my-hud/` and `Mods/Hud/` pass validation with zero warnings.

**`mod.json` (client-only):**
```json
{
  "id": "my-hud",
  "name": "My HUD Overlay",
  "description": "Displays a local client-only status panel.",
  "author": "YourName",
  "version": "1.0.0",
  "compatibleClientVersions": ["0.5.2-beta"],
  "kind": "feature",
  "depends": [],
  "assembly": "my-hud.dll",
  "enabled": true,
  "nativeClient": {
    "required": false,
    "sdkVersionRange": ">=0.1.0-poc <2.0.0",
    "supportedBuilds": ["3.29.3d", "3.29.3c"],
    "assembly": "my-hud.Client.dll",
    "payloadDir": "client",
    "overlays": [
      "Metadata/UI/Mods/Hud/Overlay.ui"
    ],
    "replaces": [],
    "rawAccess": false
  }
}
```

**`MyHud.Client.csproj` (references ONLY `LocalExile.Client.Sdk`):**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>my-hud.Client</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\LocalExile.Client.Sdk\LocalExile.Client.Sdk.csproj" />
  </ItemGroup>
</Project>
```

**`MyHudClientMod.cs`:**
```csharp
using LocalExile.Client.Sdk;
using LocalExile.Client.Sdk.Engine;

namespace MyHud.Client;

public sealed class MyHudClientMod : IClientMod
{
    private IClientContext? _context;
    private IUiWindow? _window;

    public void Load(IClientContext context)
    {
        _context = context;
        _window = context.Game.Ui.Load("Metadata/UI/Mods/Hud/Overlay.ui", "my_hud_window");
        context.TrackDisposable(_window);
        context.Game.Frame += OnFrame;
        context.Logger.Info("MyHudClientMod loaded.");
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

    private void OnFrame(float deltaSeconds)
    {
        // Runs on the game thread; never assert a single fixed OS TID.
    }
}
```

---

### 1.3 Dual-Side Waypoint Mod (Recommended)

This is the canonical layout used by `examples/DualMod.HarvestGarden/` and `examples/DualMod.MaxwellPet/`.

```text
DualMod.MyFeature/
├─ mod.json                       # Unified LocalExile + nativeClient Waypoint manifest
├─ settings.json                  # User config template (preserved on Waypoint upgrade)
├─ Shared/
│  ├─ DualMod.MyFeature.Shared.csproj
│  └─ MyFeatureMessages.cs        # Protocol constants + typed records
├─ Server/
│  ├─ DualMod.MyFeature.Server.csproj    # Produces my-feature.dll (ServerBridge)
│  ├─ MyFeatureServerMod.cs              # Authoritative server logic + vanilla fallback
│  └─ MyFeatureLocalExileEntry.cs        # Optional Gateway ModHost Roslyn IMod entry point
├─ Client/
│  ├─ DualMod.MyFeature.Client.csproj    # Produces my-feature.Client.dll (Client.Sdk ONLY)
│  └─ MyFeatureClientMod.cs              # Implements IClientMod
└─ client/
   ├─ Metadata/UI/Mods/Feature/Panel.ui
   └─ Metadata/MiscellaneousObjects/Mods/Feature/Thing.ot
```

**Key `.csproj` rule for collectible ALC hot reload (`Client/DualMod.MyFeature.Client.csproj`):**
Compile `Shared/*.cs` directly into `<id>.Client.dll` instead of referencing a separate `Shared.dll`, so `ClientModLoader` can shadow-copy and unload your entire client mod as a single self-contained assembly inside one collectible `AssemblyLoadContext`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>DualMod.MyFeature.Client</RootNamespace>
    <AssemblyName>my-feature.Client</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="..\Shared\*.cs" Link="Shared\%(Filename)%(Extension)" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\src\LocalExile.Client.Sdk\LocalExile.Client.Sdk.csproj" />
  </ItemGroup>
</Project>
```

**Key `.csproj` rule for LocalExile Gateway Roslyn entry points (`Server/DualMod.MyFeature.Server.csproj`):**
LocalExile's `ModHost` compiles `.cs` files placed in `Mods/Binaries/<id>/` against the live Gateway assemblies (`GameServer.Modding`, `GameServer.Core`, `GameServer.Gateway`, `game.dll`) while also loading sibling DLLs. Keep your testable server logic in `MyFeatureServerMod.cs` and ship `MyFeatureLocalExileEntry.cs` as content copied to output:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>DualMod.MyFeature.Server</RootNamespace>
    <AssemblyName>my-feature</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Remove="MyFeatureLocalExileEntry.cs" />
    <None Include="MyFeatureLocalExileEntry.cs" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Shared\DualMod.MyFeature.Shared.csproj" />
    <ProjectReference Include="..\..\..\src\LocalExile.Client.ServerBridge\LocalExile.Client.ServerBridge.csproj" />
  </ItemGroup>
</Project>
```

**Complete dual-side `mod.json` template:**
```json
{
  "id": "my-feature",
  "name": "My Feature",
  "description": "Short one-sentence description of what this mod adds.",
  "author": "YourName",
  "version": "1.0.0",
  "compatibleClientVersions": ["0.5.2-beta"],
  "kind": "feature",
  "depends": ["Game"],
  "assembly": "my-feature.dll",
  "enabled": true,
  "nativeClient": {
    "required": false,
    "sdkVersionRange": ">=0.1.0-poc <2.0.0",
    "supportedBuilds": ["3.29.3d", "3.29.3c"],
    "assembly": "my-feature.Client.dll",
    "payloadDir": "client",
    "overlays": [
      "Metadata/UI/Mods/Feature/Panel.ui",
      "Metadata/MiscellaneousObjects/Mods/Feature/Thing.ot"
    ],
    "replaces": [],
    "rawAccess": false,
    "customOpcodes": [
      { "id": 1201, "name": "MyFeatureAction", "direction": "ClientToServer" },
      { "id": 1202, "name": "MyFeatureSync", "direction": "ServerToClient" }
    ],
    "gealBuiltins": ["MyFeaturePulse"],
    "customControls": [],
    "contentRegistries": [
      { "table": "MiscObjects", "entryId": "Metadata/MiscellaneousObjects/Mods/Feature/Thing", "mode": "append" }
    ],
    "serverHalf": {
      "id": "my-feature",
      "minVersion": "1.0.0",
      "supportsGracefulFallback": true,
      "channelName": "my-feature"
    }
  }
}
```

---

## 2. Author & AI Agent Ground Rules / Invariants

Violating any of these verified 3.29.3c/d invariants causes hard crashes, infinite layout loops, validator rejections, or memory leaks. Treat them as mandatory pre-flight checks.

### 1. Frozen 3.29.3c & 3.29.3d Target Only
- `nativeClient.supportedBuilds` must only contain `"3.29.3d"` and/or `"3.29.3c"` (any other build fails validation with `NC004`).
- Under Proton/Wine and Windows ASLR, `Game.patched.exe` relocates (empirically observed at `0x6FFFF8A30000` instead of `0x140000000`). **Never hardcode absolute addresses** — always resolve symbols via `context.Addresses` (`IAddressLibrary`), which rebases verified RVAs against `ImageBase`.

### 2. Zero Edits to Stock `Client/` Files
- Never modify `Client/Game.exe`, `Client/Content.ggpk`, or `Client/Bundles2/`.
- Never commit copyrighted game files, `.datc64`, `.dds`, `.smd`, `.glb`, or audio media into git; generate local payloads into gitignored `dist/` via build scripts (like `scripts/build-maxwell-assets.sh`).
- Server mods must never require edits to LocalExile core (`GameServer.*`); use public `GameEvents`, `Game.*`, and `LocalExile.Client.ServerBridge`.

### 3. `Mods/<id>/` Namespace Rules vs. `replaces`
- Every new overlay asset in `payloadDir` (`client/`) **must** live under a `Mods/<namespace>/` segment (case-insensitive), where `<namespace>` matches either:
  - the full `mod.json` `id` (e.g., `id: "maxwell"` → `Mods/Maxwell/`), **or**
  - the last hyphen-delimited segment of `id` (e.g., `id: "harvest-garden"` → `Mods/Garden/`).
- **Watch out for hyphenated names!** If `id = "maxwell-pet"`, `Mods/Maxwell/` fails `DualModPackageValidator` with `PAY003` because the last hyphen segment is `"pet"`, not `"maxwell"`. Choose `id: "maxwell"` or `id: "pet-maxwell"` instead.
- Any file outside `Mods/<namespace>/` (such as patched `Data/MtxTypes.datc64`, `Data/MtxTypeGameSpecific.datc64`, `Data/Pet.datc64`, or a deliberate stock override) **must** be explicitly declared in `nativeClient.replaces` so Waypoint audits it and detects cross-mod `CNF001` conflicts.

### 4. Exact Client Text Encodings (UTF-16LE BOM CRLF vs. No BOM)
Path of Exile 3.29 silently fails or throws C++ parse exceptions on wrong encodings:

| File Extensions | Required Encoding | Helper |
|---|---|---|
| `.ui`, `.ot`, `.otc`, `.it`, `.itc`, `.ao`, `.aoc`, `.act`, `.epk`, `.ddt`, `.pet`, `.trl` | **UTF-16LE with BOM (`0xFF 0xFE`) + CRLF (`\r\n`)** | `ClientTextEncoding.EncodeUtf16LeBomCrlf`, `IFileOverlayService.RegisterVirtualTextFile`, `ModBundlePacker.EncodeClientText` |
| `.sm` (v6), `.mat` (v4 JSON) | **UTF-16LE without BOM** | `GlbToPoeMeshConverter` (`new UnicodeEncoding(false, byteOrderMark: false)`) |
| `mod.json`, `settings.json`, `manifest.json` | UTF-8 | Standard JSON |

### 5. `.ot` Inheritance & Serialized Component Contract (CRITICAL for Pets & World Objects)
- When authoring a brand-new Pet MTX `.ot` file:
  - Use **singular** `Metadata/Pet/...` (e.g., `Metadata/Pet/Mods/Maxwell/MaxwellPet.ot`, table `Pet.Id = "Metadata/Pet/Mods/Maxwell/MaxwellPet"` with **no `.ot` suffix**). All 516 stock pets use singular `Metadata/Pet/`, and LocalExile's `CosmeticEffects.PetTypes()` filters on `type.StartsWith("Metadata/Pet/", StringComparison.Ordinal)`.
  - Extend `"Metadata/Pet/Pet"` and **DO NOT add extra serialized components** (no `StateMachine`, etc.). Adding a serialized component breaks the exact `PetObject.BaseComponents` wire layout (`Positioned, Stats, Pathfinding, Buffs, Life, Animated, Actor, PetAi`) and causes buffer underflow / client disconnect on `S→C 0x02bb`.
  - Override only visual/non-serialized blocks (like `Animated { animated_object = "..." }` and `client { Functions { ... } }`).

### 6. `.ui` Parser Pitfalls & `UiLayoutLinter` Rules
Always lint every `.ui` file with `UiLayoutLinter.Lint(...)` before testing in-game:
- **`UI002` (Fatal parse error):** Never start a `.ui` file on byte 0 with `<DEFINITIONS>`. Always place a leading `//` comment or blank line before `<DEFINITIONS>`.
- **`UI006` (Unknown control):** Only use the **47 verified 3.29.3d stock controls** (`Frame`, `StackLayout`, `Label`, `Button`, `TextBox`, `CheckBox`, `Picture`, etc.) or custom control names declared in `nativeClient.customControls` and registered via `IUiService.RegisterControl`. Note: for windows loaded directly by the native 3.29.3d `ui.control_load` parser in live Proton, stick to 100% stock controls.
- **`UI008` (100% CPU infinite layout hang!):** **NEVER set `margin` on a `Picture` directly inside a `StackLayout`.** Insert an empty spacer `begin Frame` with `size = 10, 12;` immediately after the `Picture` instead.
- **`UI010` / `UI013` (Client exits with `error: ":" expected`):** Never invent unknown properties on stock controls.
- **`UI016` (Uncaught MSVC `0xE06D7363` crash):** **Every sibling control under the same parent container MUST have a unique `id`.**

### 7. Collectible `AssemblyLoadContext` & `CollectibleJsonCodec` Rules
- `ClientModLoader` shadow-copies `<id>.Client.dll` into a collectible `AssemblyLoadContext(isCollectible: true)` for zero-restart hot reload.
- **Never pin your mod's ALC:**
  - Always register every subscription/overlay/registry handle with `context.TrackDisposable(...)` and unsubscribe `context.Game.Frame -= OnFrame` inside `Unload()`.
  - Never use raw `System.Text.Json.JsonSerializer.Serialize<T>` / `Deserialize<T>` directly with default static `JsonSerializerOptions` on mod-defined types — on .NET 10, `JsonSerializerOptionsUpdateHandler` caches strong type metadata that permanently pins collectible ALCs (`UnloadedCleanly == false`).
  - Instead, pass typed records through `context.Net.Send<T>` / `context.Net.On<T>` (which automatically route collectible-ALC types through **`CollectibleJsonCodec`** in `LocalExile.Client.Sdk.Protocol`) or call `CollectibleJsonCodec` / `MessageCodec.GetJsonOptionsForType(typeof(T))` directly.

### 8. Threading, `StateStack::PassA` OS TID Migration & Exception Wall
- All UI (`IUiService`), stats (`IStatsService`), world (`IWorldService`), and native calls must run on the game thread (`IGameServices.Frame` or `context.Scheduler.RunOnGameThread(...)`).
- **OS TID Migration Caveat (Empirically Proven in R1):** Live 3.29.3d Proton runs proved that `StateStack::PassA` (`0x1528f40`) normally runs on one OS thread (`tid=352`) but migrates to other OS threads (`tid=468`, `tid=472`) across heartbeats/Job2 transitions. **Never cache `GetCurrentThreadId()` or use `[ThreadStatic]` assuming a single immutable game thread!** Always check `context.Scheduler.IsOnGameThread` or enqueue via `context.Scheduler.RunOnGameThread(...)`.
- Native calls cross `nm_call_guarded`, catching Win64 SEH and MSVC C++ exceptions (`0xE06D7363`). `ClientModLoader` also isolates managed exceptions; a mod that throws `MaxModFaults` (default `3`) times on `Frame` is automatically disabled without crashing the client or other mods.

### 9. Security & Trust Boundaries
- **Servers NEVER push executable code to clients.** Client assemblies are installed explicitly by the player via Waypoint and verified by SHA-256 (`assemblySha256` / `bundleSha256` / `integrity`).
- Package limits enforced by `DualModPackageValidator`: `MaxManifestBytes = 128 KB`, `MaxPackageBytes = 64 MB`, `MaxPackageFiles = 2000`, zero symlinks/reparse points, zip-slip rejection.
- `rawAccess: true` requires a non-empty `rawAccessJustification` (`NC008`) and is blocked by `WaypointModInstaller` unless `InstallOptions.AllowRawAccess == true`.

---

## 3. Complete Modding API Cookbook

Every type and method signature below matches the compiled source in `src/`.

### 3.1 Native `.ui` Windows & Typed Controls

#### Authoring a Valid 3.29.3d `.ui` File
```text
// Leading comment required before <DEFINITIONS> (avoids UI002 parser error)
<DEFINITIONS>

const $w = 720;

begin Frame
	id = my_panel_window;
	aspect_mode = scaley;
	size = $w, 420;
	float = centre;
	y = 240;
	visible = true;
	mouse_enabled = true;
	propagate_properties = false;
	background = "Art/2DArt/UIImages/Common/Background1", tile;

	begin StackLayout
		id = body;
		auto_size = true, true;
		margin = 24, 20, 24, 20;
		float = centre;

		begin Label
			id = status_label;
			font = Fonts.NormalSC;
			foreground_colour = Colours.White;
			text = "Ready";
		end

		begin Picture
			id = header_divider;
			float = centre;
			source = "Art/2DArt/UIImages/InGame/Divider";
			size = 520, 8;
		end

		// Use a spacer Frame instead of margin on Picture inside StackLayout (avoids UI008 hang!)
		begin Frame
			id = divider_spacer;
			size = 10, 12;
		end

		begin TextBox
			id = amount_box;
			size = 240, 44;
			font = Fonts[ "TextBox" ].Normal;
			caret_colour = Colours.White;
			text = "10";
		end

		begin CheckBox
			id = auto_check;
			size = 420, 40;
			font = Fonts.NormalSC;
			text_side = right;
			text = "Enable Auto Mode";
		end

		begin Button
			id = apply_button;
			size = 220, 44;
			text = "Apply";
		end
	end
end
```

#### Loading & Binding Controls in C# (`LocalExile.Client.Sdk.Engine`)
```csharp
IUiWindow window = context.Game.Ui.Load("Metadata/UI/Mods/Feature/Panel.ui", "my_panel");
context.TrackDisposable(window);

var statusLabel = window.Require<ILabel>("status_label");
var amountBox   = window.Require<ITextBox>("amount_box");
var autoCheck   = window.Require<ICheckBox>("auto_check");
var applyButton = window.Require<IButton>("apply_button");

applyButton.Clicked += () =>
{
    statusLabel.Text = $"Applied: {amountBox.Text} (auto={autoCheck.IsChecked})";
    context.Net.Send(new ApplyRequest(amountBox.Text, autoCheck.IsChecked));
};

amountBox.Submitted += text => statusLabel.Text = $"Submitted: {text}";
autoCheck.CheckedChanged += isChecked => statusLabel.SetProperty("auto_enabled", isChecked.ToString());
```

> **Verified 3.29.3d Dynamic UI Rules (`Button.Text`, `Label.Text`, Row Visibility & Reuse):**
> - **Mixed-case vs. SmallCaps:** `Button::SetText` (`+0x3f8` slot 2) updates the button's internal child `Label` (`+0x408`) and lowercases ASCII for PoE's centered SmallCaps button font. For mixed-case, left-aligned dynamic rows (such as Recombinator-style affix pickers), pair a `Button` (`Select` / `Selected`) with an explicit child `Label` (`Fonts.Normal`, `text_align = MiddleLeft`) inside a named row `Frame`.
> - **Dynamic row visibility:** Setting `rowFrame.Visible = false` invokes `Control::SetVisible` (`0x1418b0a30`), which hides the entire row, clears hover/focus/capture on descendants, collapses the row inside `StackLayout`, and blocks clicks on hidden child buttons.
> - **Explicit container IDs & clean reopen:** Give every ancestor `Frame`/`StackLayout` an explicit unique `id`. Closing and reopening a `.ui` window reuses the native window handle cleanly (resyncing initial visibility/text and replacing per-button click slots with zero duplicate-ID crashes and zero leaked handlers).

#### Registering a Custom UI Control (`ICustomUiControl`)
Declare the control name in `mod.json` (`"customControls": ["GrowthGauge"]`), then register it before calling `Ui.Load(...)`:

```csharp
public sealed class GrowthGauge : ICustomUiControl
{
    private IUiControl? _host;
    private float _target, _rendered;

    public string ControlTypeName => "GrowthGauge";

    public void OnInitialize(IUiControl hostControl) => _host = hostControl;

    public void OnPropertyChanged(string propertyName, string value)
    {
        if (propertyName == "progress" && float.TryParse(value, out float p))
            _target = Math.Clamp(p, 0f, 1f);
    }

    public void OnFrame(float deltaSeconds)
    {
        _rendered += (_target - _rendered) * Math.Min(1f, deltaSeconds * 6f);
        _host?.SetProperty("rendered_progress", _rendered.ToString("0.00"));
    }

    public void OnDestroy() => _host = null;
}

// Inside IClientMod.Load(IClientContext context):
context.TrackDisposable(context.Game.Ui.RegisterControl<GrowthGauge>("GrowthGauge"));
```

---

### 3.2 Virtual & Loose File Overlays

NativeMods provides **Route A universal live file overlay hooks** in `LocalExile.ClientHost.dll` armed at DLL init (`< 2 ms`, *before* CoreCLR boots and *before* PoE reads `Data/*.datc64`):
- Hooked 3.29.3d functions: `files.exists_query` (`0x0e55340`), `files.size_query` (`0x0e557f0`), `files.read_variant_2` (`0x0e55aa0`, partial/range binary reads), `files.read_whole_file` (`0x0e564d0`, `.ui`/`.ot`/`.ao`/`.header`/renderer), and `files.read_variant_5` (`0x0e56af0`).
- Buffers are allocated from PoE's own slab allocator (`0x2c8cf60`) with 16-byte header + atomic `g_allocatedBytes` (`0x45d36a8`) accounting, eliminating extension traps for `.ot`, `.ao`, `.dds.header`, `.smd`, `.ast`, and `.datc64`.

**Overlay Resolution Precedence (highest wins):**
1. Managed `IFileOverlayService` overlays: `VirtualFile` (default priority `100`) > `LooseDirectory` (`0`) > `BundleOverlay` (`-10`).
2. `NATIVEMODS_OVERLAY_DIR` environment variable (semicolon-separated directories, e.g. `dist/maxwell-assets` — works from millisecond 0 before CoreCLR boots, required for `.datc64` table overrides read at startup).
3. Auto-discovered `<NATIVEMODS_MODS_DIR>/<mod>/{client,files}` (or `<dll_dir>\Mods\Client\<mod>\{client,files}`).

Enable `NATIVEMODS_TRACE_FILES=1` when debugging live Proton runs to log overlay lookups/misses for `mods/` and `.dat` paths in `nativemods-shim.log`. Other diagnostic shim environment variables include `NATIVEMODS_HOST_DIR` (explicit self-contained `.NET 10` host directory) and `NATIVEMODS_DISABLE_AUTO_INIT=1` (skip automatic build detection in unit harnesses).

#### Managed Overlay API (`IFileOverlayService`)
```csharp
IFileOverlayService files = context.Game.Files;

// 1. Register a loose directory (higher priority overrides lower)
context.TrackDisposable(files.RegisterDirectory(Path.Combine(context.ModDirectory, "client"), priority: 10));

// 2. Register an in-memory text file (automatically UTF-16LE BOM + CRLF for .ui/.ot/.ao/.act/etc.)
context.TrackDisposable(files.RegisterVirtualTextFile(
    "Metadata/Pet/Mods/MyMod/MyPet.ot",
    "version 2\nextends \"Metadata/Pet/Pet\"\n",
    priority: 100));

// 3. Register raw binary bytes (.dds, .ogg, .datc64)
context.TrackDisposable(files.RegisterVirtualFile("Audio/Mods/MyMod/theme.ogg", oggBytes, priority: 100));

// 4. Register a packed NMB1 stored-block release bundle
context.TrackDisposable(files.RegisterBundle(bundlePath, manifestPath, priority: -10));

// 5. Inspect & hot-invalidate
if (files.Exists("Metadata/UI/Mods/MyMod/Panel.ui"))
{
    string? uiSource = files.ReadAllText("Metadata/UI/Mods/MyMod/Panel.ui");
    files.Invalidate("Metadata/UI/Mods/MyMod/Panel.ui");
}
```

---

### 3.3 Looping Custom `.ogg` Audio & FMOD Events

`IAudioService` loads Path of Exile's own `fmod.dll` **2.03** dynamically at runtime (zero static import dependency) and plays `.ogg` Vorbis files directly from overlay memory (`FMOD_OPENMEMORY | FMOD_CREATESAMPLE | FMOD_LOOP_NORMAL`), automatically falling back to `FMOD_OUTPUTTYPE_NOSOUND` on headless machines without an audio sink.

```csharp
IAudioService audio = context.Game.Audio;

// Start looping a custom 48 kHz stereo Vorbis .ogg from the file overlay
int handle = audio.PlaySoundFile("Audio/Mods/Maxwell/stock_market.ogg", volume: 0.75f, loop: true);

// Adjust volume dynamically without restarting playback
audio.SetChannelVolume(handle, 0.40f);

// Query playback state
bool playing = audio.IsChannelPlaying(handle);

// Stop cleanly (always stop in IClientMod.Unload!)
audio.StopChannel(handle);

// Or trigger a stock/custom FMOD Studio event via PoE's AudioManager
int sfx = audio.PlayEvent("event:/UI/ButtonClick", volume: 1.0f);
```

> **Audio Format Rule:** Transcode `.opus`, `.mp3`, or `.wav` sources to **48 kHz stereo Ogg Vorbis** using `LocalExile.Client.AssetPipeline` (`AudioTranscoder.TranscodeToOggVorbis` or `convert-audio` CLI) so FMOD 2.03 decodes them reliably. Active channel limit in 3.29 is `0x200` (`512`).

---

### 3.4 Typed Server ↔ Client Networking & Graceful Fallback

NativeMods supports two wire transports automatically negotiated by `ClientHandshake` / `ServerHandshake` (on live `NativeShimBridge`, `ClientHostRuntime.OnFrame` automatically sends `ClientHandshake` starting at frame `30` and retries every `2.0 s` wall-clock until `LastServerHandshake` arrives):
- **`HostRuleset` (Primary):** S→C `0x02C9` / C→S `0x02CA` carrying `[u8 rulesetId = 0xA0][CompactUInt payloadLength][NMOD envelope]` (PoE big-endian `CompactUInt`: 1B `<=0x7F`, 2B `<=0x3FFF`, 3B `<=0xFFFF`, matching `Game.exe`, `live_engine_hooks.cpp`, and LocalExile `InstanceRulesets.cs`; max `65535` bytes). **Stock unmodded clients silently drop `0x02C9` for unknown ruleset IDs**, so mixed servers never disconnect vanilla players.
- **`ChatFallback` (Fallback):** S→C `##NM env <base64>` / C→S `/nm env <base64>` over chat (`0x0017` / `0x0015`), requiring zero custom packet hooks on the server.

#### Shared Typed Records (`Shared/MyMessages.cs`)
```csharp
public sealed record ToggleEffectRequest(bool Enabled, float Intensity);
public sealed record EffectStateSync(bool Enabled, float Intensity, string Status);
```

#### Server Side (`LocalExile.Client.ServerBridge`)
```csharp
// Configure once on mod load (with your IGatewayPacketSink adapter):
var bridge = new ClientBridgeHost(gatewaySink, new ClientBridgeOptions
{
    ServerVersion = "LocalExile-3.29.3d",
    HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId // 0xA0
});

// Subscribe to typed C->S messages on your mod's channel (Action<IClientSession, T>; avoid async void):
IDisposable sub = bridge.On<ToggleEffectRequest>("my-feature", (session, req) =>
{
    if (!session.HasClientMod("my-feature"))
        return;

    var sync = new EffectStateSync(req.Enabled, req.Intensity, "Updated!");
    _ = session.SendAsync("my-feature", sync);
});

// Graceful degradation in a slash command handler:
public async Task<string> HandleCommandAsync(string connectionId, string[] args)
{
    IClientSession session = bridge.For(connectionId);
    if (session.HasNativeHost && session.HasClientMod("my-feature"))
    {
        await session.SendAsync("my-feature", new EffectStateSync(true, 1.0f, "Opened panel"));
        return string.Empty; // Client handles UI
    }

    // Stock 3.29 client without NativeMods -> clean chat-only fallback!
    return "[MyFeature — Chat Fallback] Effect enabled (intensity 1.0).";
}
```

#### Server-Driven UI (Zero Client DLL Required)
```csharp
IClientSession session = bridge.For(connectionId);
if (session.HasNativeHost)
{
    ServerUiWindowHandle win = await session.Ui.OpenAsync("Metadata/UI/Mods/MyServerMod/Panel.ui", "my_win");
    await win.SetTextAsync("status_label", "Driven straight from the server!");
    win.OnButtonClick("apply_button", s =>
    {
        _ = win.SetTextAsync("status_label", "Server received button click!");
        _ = s.Ui.PlayAudioAsync("Audio/Mods/MyServerMod/ding.ogg");
    });
}
```

#### Client Side (`IClientContext.Net`)
```csharp
// Subscribe to S->C typed messages (tracked for automatic disposal on unload):
context.TrackDisposable(context.Net.On<EffectStateSync>(sync =>
{
    statusLabel.Text = sync.Status;
}));

// Send typed C->S message (uses CollectibleJsonCodec automatically):
context.Net.Send(new ToggleEffectRequest(Enabled: true, Intensity: 0.85f));
```

---

### 3.5 GEAL Built-Ins, Custom Rulesets & Custom Opcodes

All registries live on `context.Game` (`IGameServices`) and return `IDisposable` handles that **must** be passed to `context.TrackDisposable(...)`.

#### Registering a Custom GEAL Built-In (`IGealBuiltinRegistry`)
Declare in `mod.json` under `"gealBuiltins": ["MaxwellDance"]`:
```csharp
context.TrackDisposable(context.Game.GealBuiltins.Register("MaxwellDance", (uint objectId, GealArguments args) =>
{
    int boost = args.Count > 0 ? Math.Clamp(args.Int(0), 1, 8) : 1;
    context.Game.World.PlayEffect(objectId, "Metadata/Pet/Mods/Maxwell/MaxwellPet.ao");
    return GealValue.FromInt(boost);
}));
```
Callable from `.ot` client scripts (`client { Functions { on_dance = { MaxwellDance( 2 ); } } }`) or C# (`context.Game.GealBuiltins.Invoke("MaxwellDance", objId, GealValue.FromInt(2))`). Validate `.ot` GEAL scripts statically with `context.Game.Scripts.ValidateScript(source)`.

#### Registering a Custom Per-Area Ruleset (`IRulesetRegistry`)
Slot IDs `0x00..0xAE` (`IRulesetRegistry.MaxRulesetId = 0xAE`). Reserve `0xA0` for the NativeMods host channel (`DefaultHostChannelRulesetId`); example mods use `0xA1` (HarvestGarden) and `0xA2` (MaxwellPet):
```csharp
public sealed class MyAreaRuleset : IClientRuleset
{
    public byte RulesetId => 0xA3;
    public string Name => "MyAreaRuleset";
    public void OnAreaEntered(AreaEventArgs area) { /* init per-area state */ }
    public void OnRulesetUpdate(ReadOnlySpan<byte> payload) { /* decode S->C 0x2c9 body */ }
    public void OnFrame(float deltaSeconds) { }
    public void OnAreaLeft() { }
}

context.TrackDisposable(context.Game.Rulesets.Register<MyAreaRuleset>());
```

#### Registering a Custom Opcode Handler (`IOpcodeRegistry`)
Stock 3.29.3d opcodes end at `0x02FB`, so custom opcodes start at `IOpcodeRegistry.FirstReservedOpcode` (`0x02FC`). Declare them in `mod.json` `nativeClient.customOpcodes` so Waypoint prevents cross-mod `CNF003` collisions:
```csharp
context.TrackDisposable(context.Game.Opcodes.RegisterHandler(
    opcode: 1202,
    handler: payload => context.Logger.Debug($"Received {payload.Length} raw bytes"),
    groupName: "my-feature"));
```

---

### 3.6 Adding Brand-New In-Game Content (NOT Replacers)

NativeMods is built to **add brand-new items, world objects, and Pet MTXs** alongside stock content — never overwriting or sacrificing an existing stock pet or item.

#### Appendable vs. Closed `.datc64` Tables
- **Appendable registry tables (`DualModPackageValidator.AppendableRegistryTables`):** `BaseItemTypes`, `MonsterVarieties`, `NPCs`, `Chests`, `Projectiles`, `MiscObjects`, `MiscAnimated`, `HideoutDoodads`, `ExecuteGEAL`, `ClientStrings`.
- **Appendable Pet MTX tables (verified byte-for-byte on 3.29.3d):** `Data/MtxTypes.datc64`, `Data/MtxTypeGameSpecific.datc64`, `Data/Pet.datc64`.
- **Closed tables (`DualModPackageValidator.ClosedRegistryTables`):** `ItemClasses` (bound to compiled C++ enums — fails validation with `NC016`). `Stats` emits warning `NC017`.

#### Step 1: Computing PoE 3.29 `HASH32` & `HASH16`
Path of Exile 3.29 computes `MtxTypes.HASH32`, `MtxTypeGameSpecific.HASH32`, `Pet.HASH32`, and `S→C 0x02bb` `ObjectTypeHash` using **32-bit MurmurHash2 (seed `0`) over narrow UTF-8/ASCII bytes** (`Game.exe 0x140df6e10`), and `Pet.HASH16` via XOR fold `((hash32 >> 16) ^ (hash32 & 0xFFFF))`:

```csharp
// Server-side (LocalExile.Client.ServerBridge):
using LocalExile.Client.ServerBridge;

uint mtxHash32   = PoeContentHash.MurmurHash2("Metadata/Items/Pets/Mods/MaxwellPet");   // 0xA1EB592E (2716555566)
uint petHash32   = PoeContentHash.MurmurHash2("Metadata/Pet/Mods/Maxwell/MaxwellPet");  // 0x1F3AB11A (523940122)
ushort petHash16 = PoeContentHash.FoldHash16(petHash32);                                 // 0xAE20     (44576)

// Asset-pipeline side (LocalExile.Client.AssetPipeline.Dat64):
// PoeHashing.Hash32(...) and PoeHashing.FoldHash16(...) produce identical values.
```

#### Step 2: Appending Rows to `.datc64` Tables (`Dat64TablePatcher` & `MaxwellDatPatcher`)
A brand-new Pet MTX requires appending **1 row** to each of 3 tables (**no** `BaseItemTypes` or `ItemisedVisualEffect` rows needed):

| Table (3.29.3d) | Row Stride | Stock Rows | Appended Row (Example: Maxwell) | Critical Columns |
|---|---|---|---|---|
| `Data/MtxTypes.datc64` | `180 B` | `8557` (`0..8556`) | **`8557`** (`Record.Id = 8558`) | `Id` (`@0`), `HASH32` (`@8` = `0xA1EB592E`), `Name` (`@12`), `Description1` (`@28`); clone scalar flags from `Metadata/Items/Pets/Kiwi1` (row `5730`) |
| `Data/MtxTypeGameSpecific.datc64` | `68 B` | `7849` (`0..7848`) | **`7849`** | `Type` (`@0` foreignrow → `8557`), `HASH32` (`@16` = **must equal `MtxTypes.HASH32` or client crashes!**), `DDSFile` (`@20`), `Category` (`@28` = `14`), `__col4` (`@32` = `[15]`), `__col5` (`@48` = `0`), `__col6` (`@52` = `[18, 19]` Pet Slot 1 & 2) |
| `Data/Pet.datc64` | `138 B` | `516` (`0..515`) | **`516`** | `Id` (`@0` = singular `"Metadata/Pet/Mods/..."`, no `.ot`), `MtxTypeGameSpecificKey` (`@8` foreignrow → `7849`), `HASH32` (`@24`), `HASH16` (`@28`); clone scalar flags from `Metadata/Pet/Kiwi/Basic` (row `0`) |

**Using `MaxwellDatPatcher.AppendPetMtx` (turnkey for any custom Pet MTX):**
```csharp
using LocalExile.Client.AssetPipeline.Dat64;

using Bundles2Reader bundles = Bundles2Reader.Open("/path/to/LocalExile/Client");
byte[] stockMtxTypes = bundles.ReadFile("Data/MtxTypes.datc64");
byte[] stockMtxGs    = bundles.ReadFile("Data/MtxTypeGameSpecific.datc64");
byte[] stockPet      = bundles.ReadFile("Data/Pet.datc64");

var spec = new PetMtxPatchSpec
{
    MtxTypeId   = "Metadata/Items/Pets/Mods/MyCustomPet",
    DisplayName = "My Custom Pet",
    Description = "Summons your brand-new custom pet.",
    PetId       = "Metadata/Pet/Mods/MyMod/MyCustomPet",
    IconDdsPath = "Art/2DItems/Pets/Mods/MyMod/MyCustomPet.dds",
};

// AppendPetMtx clones template rows, writes hashes/foreign rows, and internally runs VerifyAppendedPetMtx(...)
PatchedPetMtxResult patched = MaxwellDatPatcher.AppendPetMtx(stockMtxTypes, stockMtxGs, stockPet, spec);

// Write to your overlay dir (served via NATIVEMODS_OVERLAY_DIR and declared in mod.json replaces[])
File.WriteAllBytes(Path.Combine(outDir, "Data/MtxTypes.datc64"), patched.MtxTypesBytes);
File.WriteAllBytes(Path.Combine(outDir, "Data/MtxTypeGameSpecific.datc64"), patched.MtxTypeGameSpecificBytes);
File.WriteAllBytes(Path.Combine(outDir, "Data/Pet.datc64"), patched.PetBytes);
```

**Using generic `Dat64TablePatcher` for any other appendable `.datc64` table:**
`Dat64TablePatcher` preserves every stock row and variable-section offset byte-for-byte, writes 4-byte double-NUL (`00 00 00 00`) UTF-16LE strings, and round-trip verifies the output binary:
```csharp
Dat64Table table = Dat64Table.Parse(stockTableBytes);
var patcher = new Dat64TablePatcher(table);

byte[] row = patcher.CloneRow(templateRowIndex: 0);
patcher.SetString(row, columnOffset: 0, "Metadata/MiscellaneousObjects/Mods/MyMod/Thing");
Dat64TablePatcher.WriteUInt32(row, columnOffset: 8, PoeHashing.Hash32("Metadata/MiscellaneousObjects/Mods/MyMod/Thing"));
Dat64TablePatcher.WriteForeignRow(row, columnOffset: 16, targetRowIndex: 42UL);
patcher.SetInt32Array(row, columnOffset: 32, [18, 19]);

int newRowIndex = patcher.AppendRow(row);
Dat64Table verified = patcher.BuildAndVerify();
byte[] outputBytes = verified.Serialize();
```

#### Step 3: Registering the Brand-New Pet MTX on the LocalExile Server (Cosmetics `K` Menu)
Use **`CustomPetMtxDefinition`** and **`LocalExilePetMtxRegistrar`** in **`LocalExile.Client.ServerBridge`** so any mod can add a brand-new Pet MTX to LocalExile's live `Game.Mtx` (`MtxCatalog`) & `Game.MtxEffects` (`CosmeticEffects`), grant ownership via the exact 29-byte `S→C 0x0204` (`RecordUpsert`) wire packet, and react to live **Cosmetics (`K`) menu** Pet Slot 1 (`18`) / Pet Slot 2 (`19`) equip & unequip transitions — with **zero edits to LocalExile core** and zero duplicated reflection glue:

```csharp
using LocalExile.Client.ServerBridge;

// 1. Define your appended brand-new Pet MTX (validates append-only row indices >= 8557/7849/516,
//    singular "Metadata/Pet/" prefix, no ".ot" suffix, and computes RecordId/Hashes automatically):
var myPet = new CustomPetMtxDefinition
{
    MtxTypesRowIndex            = CustomPetMtxDefinition.Stock3293dMtxTypesCount,            // 8557 -> RecordId = 8558
    MtxTypeGameSpecificRowIndex = CustomPetMtxDefinition.Stock3293dMtxTypeGameSpecificCount, // 7849
    PetRowIndex                 = CustomPetMtxDefinition.Stock3293dPetCount,                 // 516
    MtxTypeId                   = "Metadata/Items/Pets/Mods/MaxwellPet",
    DisplayName                 = "Maxwell the Cat Pet",
    Description                 = "Summons your spinning Maxwell the Cat pet",
    PetMetadataPath             = "Metadata/Pet/Mods/Maxwell/MaxwellPet",
    IconDdsPath                 = "Art/2DItems/Pets/Mods/Maxwell/MaxwellPet.dds",
    AllowedSlots                = [CustomPetMtxDefinition.PetSlot1, CustomPetMtxDefinition.PetSlot2], // [18, 19]
};

// 2. Register with LocalExilePetMtxRegistrar and subscribe to Cosmetics (K) menu equip/unequip events:
using var registrar = new LocalExilePetMtxRegistrar();
IDisposable regHandle = registrar.Register(myPet);

registrar.PetEquipChanged += (PetMtxEquipChangedEvent ev) =>
{
    // Triggered when the player equips, swaps slot, or unequips your pet in the Cosmetics (K) menu!
    // Push your typed Server->Client sync message (e.g., start/stop looping audio & update UI panel).
    _ = bridge.SendAsync(ev.ConnectionId, "maxwell", new MaxwellStateSync(
        ev.CharacterName, ev.IsEquipped, ev.PetEntityId, ev.PetSlot, ...));
};

// 3. Inside your LocalExile Gateway IMod entry point (see examples/DualMod.MaxwellPet/Server/MaxwellLocalExileEntry.cs):
//    - On GameEvents.PlayerEntered:
registrar.ApplyToLiveCatalogs(Game.Mtx, Game.MtxEffects, mod.Log); // Idempotent; cleanly removed on registrar.Dispose()
byte[] grantPacket = LocalExilePetMtxRegistrar.EncodeRecordUpsertPacket(myPet, justReceived: true); // 29-byte S->C 0x0204
await Program.Send(player, grantPacket, "0x0204 grant custom Pet MTX");
registrar.ObserveLiveGatewayPlayer(connId, character, Game.WornCosmetics(player), player.Pets);

//    - On GameEvents.AreaTick (~4 Hz): detects Cosmetics (K) menu Pet Slot 1/2 equip & unequip in real time!
registrar.ObserveLiveGatewayPlayer(connId, character, Game.WornCosmetics(player), player.Pets);

//    - On GameEvents.PlayerLeaving (when player.Closed):
registrar.ClearConnection(connId);
```

> **Note for Unit Tests:** `registrar.ObserveWornCosmetics(connectionId, characterName, wornSlots, spawnedPets)` lets you test the entire Cosmetics `K` equip / slot-swap / unequip → client audio & UI sync lifecycle deterministically in xUnit without referencing live Gateway DLLs. See `tests/LocalExile.Client.MaxwellPet.Tests/PetMtxRegistrarTests.cs`.

#### Step 4: Converting `.glb` 3D Models, `.png`/`.jpeg` Textures & `.opus` Audio (`LocalExile.Client.AssetPipeline`)

**CLI commands (`dotnet run --project src/LocalExile.Client.AssetPipeline -- ...`):**

```bash
# Convert a glTF 2.0 .glb model into native 3.29.3d .smd + .smd.header + .sm + .ast v7 + .amd + .mat + .ot/.ao/.act
dotnet run --project src/LocalExile.Client.AssetPipeline -c Release -- \
  convert-glb --in /path/to/model.glb --out dist/my-assets

# Convert PNG/JPEG/TGA image to PoE DX10 .dds + exact 3.29.3d .dds.header (bc1 | bc3 | rgba8)
dotnet run --project src/LocalExile.Client.AssetPipeline -c Release -- \
  convert-dds --in /path/to/icon.png --out dist/my-assets/Art/2DItems/Pets/Mods/MyMod/MyPet.dds --format rgba8

# Transcode .opus/.mp3/.wav to 48 kHz stereo Vorbis .ogg (validated OggS + \x01vorbis header)
dotnet run --project src/LocalExile.Client.AssetPipeline -c Release -- \
  convert-audio --in /path/to/theme.opus --out dist/my-assets/Audio/Mods/MyMod/theme.ogg

# Append Pet MTX rows to stock .datc64 tables extracted directly from read-only PoE Client/Bundles2/
dotnet run --project src/LocalExile.Client.AssetPipeline -c Release -- \
  patch-dat --client /home/nam/Git/LocalExile/Client --out dist/my-assets

# Complete turnkey Maxwell build & verification script (outputs 22 verified assets into dist/maxwell-assets/)
./scripts/build-maxwell-assets.sh
```

**C# Library API (`LocalExile.Client.AssetPipeline`):**
```csharp
using LocalExile.Client.AssetPipeline.Audio;
using LocalExile.Client.AssetPipeline.Dds;
using LocalExile.Client.AssetPipeline.Mesh;

// 1. glTF 2.0 .glb -> PoE 3.29.3d Pet asset bundle (rigid Y-up -> PoE Z-down rotation, 1-bone v7 uncompressed .ast!)
GlbScene scene = GlbParser.ParseFile("model.glb");
PoePetAssetBundle meshBundle = GlbToPoeMeshConverter.Convert(scene, new PoePetMeshOptions
{
    ArtBasePath      = "Art/Models/Mods/MyMod",
    MetadataBasePath = "Metadata/Pet/Mods/MyMod",
    ObjectBaseName   = "MyPet",
    ModelBaseName    = "mypet",
    TargetLongestHorizontalSpan = 84f,
});
foreach (var (virtualPath, bytes) in meshBundle.Files)
{
    string full = Path.Combine(outDir, virtualPath.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllBytes(full, bytes);
}

// 2. Texture -> DX10 .dds + PoE 3.29.3d .dds.header pair
//    PoeDdsFormat.Bc1   = DXGI 71 / PoE fmt 205 (opaque albedo)
//    PoeDdsFormat.Bc3   = DXGI 77 / PoE fmt 207 (compressed alpha)
//    PoeDdsFormat.Rgba8 = DXGI 28 / PoE fmt 21  (2D icons 156x156 & lossless alpha)
DdsOutput iconDds = DdsTextureConverter.ConvertImageFile("icon.png", PoeDdsFormat.Rgba8, resizeWidth: 156, resizeHeight: 156);
DdsTextureConverter.WritePair(Path.Combine(outDir, "Art/2DItems/Pets/Mods/MyMod/MyPet.dds"), iconDds);

// 3. Audio -> 48 kHz stereo Vorbis .ogg
OggVorbisInfo oggInfo = AudioTranscoder.TranscodeToOggVorbis("theme.opus", Path.Combine(outDir, "Audio/Mods/MyMod/theme.ogg"));
```

#### Step 5: Brand-New Custom Currencies & Item Affix Manipulation (`CurrencyDatPatcher`, `LocalExileCurrencyRegistrar`, `LocalExileItemAffixService`)

A brand-new Currency item appends rows across three 3.29.3d `.datc64` tables (zero overlap with Pet MTX tables — both overlays can be passed semicolon-separated via `NATIVEMODS_OVERLAY_DIR`):

| Table (3.29.3d) | Row Stride | Stock Rows | Example Appended Rows (`DualMod.SiphonOrb`) | Critical Columns & Invariants |
|---|---|---|---|---|
| `Data/BaseItemTypes.datc64` | `374 B` | `5461` (`0..5460`) | **`5461`** (`SiphonOrbEmpty`), **`5462`** (`SiphonOrbCharged`) | Unique `Id` (`@0`), `HASH32` (`@140`, MurmurHash2 seed `0`), `ItemClassesKey` (`@8` = `30` `StackableCurrency`), `ItemVisualIdentity` (`@124`). Deep-copy/clear all 11 `foreignrow[]` array columns (zero aliasing!). |
| `Data/ItemVisualIdentity.datc64` | `604 B` | `15516` (`0..15515`) | **`15516`** (`0xD501`), **`15517`** (`0xD502`) | Unique `Id` (`@0`), **`HASH16` (`@40`)** indexed directly by `Base::Read` (`0x1421ed3ea`), `DDSFile` (`@8`), `AOFile` (`@16`), `InventorySoundEffect` (`@24` = `33`). Clear all 16 array columns. |
| `Data/CurrencyItems.datc64` | `205 B` | `1962` (`0..1961`) | **`1962`** (stack `20`), **`1963`** (stack `1`) | Unique `BaseItemTypesKey` (`@0`), `StackSize` (`@16`), `CurrencyUseType` (`@20` = `2` right-click → left-click item), `Action` (`@24` = `"remove_random_mod"` for native cursor-apply onto Magic/Rare gear emitting `C→S 0x0071`/`0x0072`). |

**1. Asset & `.datc64` Pipeline (`LocalExile.Client.AssetPipeline.Dat64.CurrencyDatPatcher`):**
```csharp
using LocalExile.Client.AssetPipeline.Dat64;
using LocalExile.Client.AssetPipeline.Dds;

// Append one or more custom currencies (deep-copies/clears all foreignrow[] arrays & verifies uniqueness):
var specs = CurrencyDatPatcher.CreateSiphonOrbSpecs(); // or construct your own CurrencyItemPatchSpec[]
PatchedCurrencyResult patched = CurrencyDatPatcher.AppendCurrencies(
    stockBaseItemTypes, stockItemVisualIdentity, stockCurrencyItems, specs);

// Generate UTF-16LE BOM .it metadata (SupportsStoredMods=true adds LocalStats {} + Mods {} like Charged Compass):
byte[] itBytes = CurrencyDatPatcher.BuildItemMetadataItBytes(specs[0]);

// Convert 512x512 black-background PNG -> 78x78 flood-fill alpha-cutout RGBA8 DX10 .dds + .dds.header:
DdsOutput icon = DdsTextureConverter.ConvertCurrencyIconFile("siphon_orb_empty.png", targetWidth: 78, targetHeight: 78);
DdsTextureConverter.WritePair(Path.Combine(outDir, "Art/2DItems/Currency/Mods/Siphon/SiphonOrbEmpty.dds"), icon);
```
```bash
# Turnkey Orb of Siphoning asset build & verification (outputs dist/siphon-assets/):
./scripts/build-siphon-assets.sh

# General-purpose JSON spec CLI (supports --base-overlay to layer multiple currency mods):
dotnet run --project src/LocalExile.Client.AssetPipeline -c Release -- \
  build-currency --spec my-currencies.json --client /home/nam/Git/LocalExile/Client --out dist/my-currency-assets
```

**2. Server Registration & Right-Click Currency Routing (`LocalExileCurrencyRegistrar` in `LocalExile.Client.ServerBridge`):**
```csharp
using LocalExile.Client.ServerBridge;

var emptyOrb = new CustomCurrencyItemDefinition
{
    BaseItemTypesRowIndex       = CustomCurrencyItemDefinition.Stock3293dBaseItemTypesCount,       // 5461
    ItemVisualIdentityRowIndex  = CustomCurrencyItemDefinition.Stock3293dItemVisualIdentityCount,  // 15516
    CurrencyItemsRowIndex       = CustomCurrencyItemDefinition.Stock3293dCurrencyItemsCount,       // 1962
    BaseItemTypeId              = "Metadata/Items/Currency/Mods/Siphon/SiphonOrbEmpty",
    DisplayName                 = "Orb of Siphoning",
    VisualIdentityId            = "SiphonOrbEmpty",
    VisualIdentityHash16        = 0xD501,
    IconDdsPath                 = "Art/2DItems/Currency/Mods/Siphon/SiphonOrbEmpty.dds",
    MaxStackSize                = 20,
    CurrencyUseType             = CustomCurrencyItemDefinition.CurrencyUseTypeApplyToItem,         // 2
    CurrencyAction              = CustomCurrencyItemDefinition.DefaultMagicOrRareTargetAction,     // "remove_random_mod"
    SupportsStoredMods          = false, // true on Charged Orb -> ["Base","Usable","Stack","LocalStats","Mods"]
};

using var currencyRegistrar = new LocalExileCurrencyRegistrar();
currencyRegistrar.Register(emptyOrb, async (CurrencyUseContext ctx) =>
{
    // Invoked on C->S 0x0071 (UseItemOn) / 0x0072 (UseItemTypeOn) ONLY for registered custom TypeHashes!
    // Stock currencies return false and pass straight through to stock LocalExile crafting.
    return await HandleSiphonUseAsync(ctx);
});

// Inside Gateway IMod.Load (before listener.Start(), cleanly removed on Dispose()):
currencyRegistrar.ApplyToLiveCatalog(Program.Items, mod.Log);
```

**3. Inspecting, Stealing, Storing & Imbuing Item Affixes (`LocalExileItemAffixService`):**
```csharp
using LocalExile.Client.ServerBridge;

var affixService = new LocalExileItemAffixService();

// Inspect explicit affixes on a live Gateway Item (formats exact PoE stat_descriptions.txt text):
ItemAffixView view = affixService.InspectGatewayItem(inventoryId, itemId, liveItem, itemCatalog, statDescriptions);
// Or inspect/test deterministically using PortableItem:
ItemAffixView portableView = LocalExileItemAffixService.Inspect(portableItem);

// 1. Steal a chosen affix (validates corrupted/mirrored/unidentified/fractured/0-mod guards):
AffixStealResult steal = LocalExileItemAffixService.StealAffix(sourceItem, affixIndex: 0);
// 2. Embed the stolen affix persistently on a Charged Currency item (ModSection.Enchant, Rarity=Normal, Stack=1):
PortableItem chargedOrb = LocalExileItemAffixService.CreateChargedCurrencyItem(chargedOrbDef, steal.StoredAffix!);
// 3. Later, read the stored affix back and replace a chosen affix on a target item (enforces Prefix/Suffix caps & mod groups):
StoredAffixPayload? stored = LocalExileItemAffixService.ReadStoredAffix(chargedOrb);
AffixReplaceResult imbue = LocalExileItemAffixService.ReplaceAffix(targetItem, targetAffixIndex: 1, stored!);
```

---

### 3.7 Using `Raw` Memory & Native Hooks Safely

Most mods never need `IRawMemory`. If you do need direct symbol reads, native calls, or hooks:

1. **Declare intent in `mod.json`** (otherwise accessing `context.Raw` throws `RawAccessDeniedException` and Waypoint rejects the package with `NC008`):
   ```json
   "nativeClient": {
     "rawAccess": true,
     "rawAccessJustification": "Reads active camera matrix for 3D world-to-screen overlay projection."
   }
   ```
2. **Resolve via `IAddressLibrary` — never hardcode offsets:**
   ```csharp
   // Verify symbol presence on the running 3.29 build (3.29.3c or 3.29.3d)
   if (context.Addresses.TryResolveAddress("frame.state_stack_pass_a", out nint addr))
   {
       // All reads/writes/hooks run on the game thread
       byte[] prologue = context.Raw.ReadBytes(addr, 16);
   }

   // Native call executed inside nm_call_guarded (0xE06D7363 SEH/C++ exception wall):
   nint result = context.Raw.Call("audio.get_holder");

   // Byte-verified inline detour or vtable hook (disposed automatically on mod unload!):
   IDisposable hook = context.Raw.InstallHook(
       symbolName: "some.verified_symbol",
       detourTarget: myUnmanagedCallersOnlyFnPtr,
       out nint originalTrampoline,
       hookType: HookType.InlineDetour);
   context.TrackDisposable(hook);
   ```
3. See [`docs/address-library.md`](address-library.md) for the full list of verified 3.29.3d / 3.29.3c symbols and the workflow for adding new verified entries.

---

## 4. Testing, Cross-Compilation & Live Proton Verification

### 4.1 Fast Deterministic Unit & E2E Testing

You can test 95% of your dual-side mod in milliseconds on Linux or Windows with **zero game binary** using `SimulatedNativeBridge`, `ClientHostRuntime`, `ClientBridgeHost`, `DualModPackageValidator`, `UiLayoutLinter`, and `WaypointModInstaller`:

```csharp
// 1. Validate manifest, namespace rules, .ui layouts, and cross-mod conflicts
ValidationReport report = DualModPackageValidator.ValidatePackageDirectory(packageDir, SemVer.Parse(SdkVersion.Current));
Assert.True(report.IsValid, string.Join('\n', report.Errors));

// 2. Lint individual .ui files directly
IReadOnlyList<ValidationDiagnostic> uiDiags = UiLayoutLinter.Lint(
    File.ReadAllText(uiPath), uiPath, customControls: ["GrowthGauge"]);
Assert.DoesNotContain(uiDiags, d => d.Severity == DiagnosticSeverity.Error);

// 3. Install into a temp LocalExile root with WaypointModInstaller
var installer = new WaypointModInstaller();
InstallAuditReport audit = installer.Install(packageDir, tempLocalExileRoot, new InstallOptions { PackClientBundle = true });
Assert.True(audit.Succeeded);

// 4. Boot ClientHostRuntime over SimulatedNativeBridge & verify collectible ALC unload
var bridge = new SimulatedNativeBridge(ClientBuildId.Build3293d);
using var host = new ClientHostRuntime(bridge, new ClientHostOptions
{
    TransportKind = NetTransportKind.HostRuleset,
    ShadowCopyRoot = Path.Combine(tempRoot, "shadow"),
});
host.Start();
host.Loader.LoadModFromDirectory(Path.Combine(tempLocalExileRoot, "Mods", "Client", "my-feature"));
host.TickFrame(1f / 60f);

// Verify zero ALC pinning (catches System.Text.Json leaks & forgotten event unsubscriptions!)
ModUnloadResult unload = await host.Loader.UnloadModAsync("my-feature");
Assert.True(unload.UnloadedCleanly);
```

**Run the full managed & native host-side test suites:**
```bash
# .NET 10 solution build & all xUnit suites
dotnet build NativeMods.slnx -c Release
dotnet test NativeMods.slnx -c Release --no-build

# C++20 native shim unit tests (Linux, -Wall -Wextra -Wpedantic -Werror)
cmake -S src/native-shim -B build/native-shim -DCMAKE_BUILD_TYPE=Release
cmake --build build/native-shim
ctest --test-dir build/native-shim --output-on-failure
```

### 4.2 Cross-Compiling Win64 DLLs & Wine Smoke Test

Build the real Win64 `LocalExile.ClientHost.dll`, `version.dll` proxy, `r1_wine_smoke.exe`, and self-contained `win-x64` `.NET 10` host bundle into `dist/win-x64/`:

```bash
./scripts/build-win64.sh
```

`scripts/build-win64.sh` automatically:
1. Cross-compiles `src/native-shim` via MinGW-w64 (`x86_64-w64-mingw32-g++`).
2. Publishes self-contained `win-x64` `LocalExile.Client.Host` (`hostfxr.dll`, `coreclr.dll`, `clrjit.dll`, etc.).
3. Stages `addresses/{3.29.3d,3.29.3c}.json` and example client mods.
4. Audits PE imports via `x86_64-w64-mingw32-objdump -p`, failing if any non-system MinGW runtime DLL (`libgcc`, `libstdc++`, `libwinpthread`) leaks in.

Run fast Wine smoke verification before touching Proton:
```bash
wine build/native-shim-win64/nativemods_shim_tests.exe
wine dist/win-x64/r1_wine_smoke.exe
```

### 4.3 Live Testing Against Real 3.29.3d Under Proton (`Xvfb`)

All live client runners enforce strict safety guarantees:
- **Isolated `Xvfb` virtual screen** (`1280x720x24`, unsetting `WAYLAND_DISPLAY`, `HYPRLAND_INSTANCE_SIGNATURE`, `XDG_CURRENT_DESKTOP`, `XDG_SESSION_TYPE`, `PROTON_ENABLE_WAYLAND`) with pre/mid/post `hyprctl clients -j` zero-leak checks.
- **Strictly launches via LocalExile's `scripts/dev/live.ps1`** (`--patching-server 127.0.0.1:PORT`) — never connects to official GGG servers.
- **Zero-mutation guarantee on `Client/`:** Records `Game.exe` and `Game.patched.exe` SHA-256 before launch, restores everything in a `trap EXIT` handler, and verifies exact SHA-256 restoration on exit.
- **Isolated PipeWire null sink (`module-null-sink`):** Allows live FMOD 2.03 audio verification (real 48 kHz stereo PCM energy) without playing loud audio on your desktop speakers.

#### 1. CoreCLR & Frame-Hook Soak (`scripts/run-r1-proton.sh`)
```bash
# Proxy mode (version.dll + WINEDLLOVERRIDES="version=n,b")
./scripts/run-r1-proton.sh --mode proxy --soak-seconds 45

# PE32+ .nmimp import-patch mode (zero proxy DLL, zero WINEDLLOVERRIDES)
./scripts/run-r1-proton.sh --mode import-patch --soak-seconds 35
```

#### 2. Full End-to-End Dual-Side Mod Verification (Cosmetics `K` Menu, `.ui`, Audio, Currencies)
For interactive desktop testing on Hyprland/Wayland/X11 (preserving native display and audio, with automatic `characters.db` snapshot & clean teardown):
```bash
# Launch both Maxwell Pet + Orb of Siphoning together (semicolon-separated NATIVEMODS_OVERLAY_DIR, zero table overlap):
./scripts/launch-desktop.sh --mod all

# Or test a single mod in isolation (pass --dry-run to validate paths/plan without launching):
./scripts/launch-desktop.sh --mod siphon
./scripts/launch-desktop.sh --mod maxwell
```

For headless automated verification of a content + UI + audio dual-side mod (such as `examples/DualMod.MaxwellPet/` via `scripts/run-maxwell-proton.sh`):
1. Build Win64 shim + host (`./scripts/build-win64.sh`) and generate your local asset overlay (`./scripts/build-maxwell-assets.sh` → `dist/maxwell-assets/`).
2. Stage the server mod in a temporary `Game_MODS_FOLDER` directory under `<LocalExileRoot>/data/` (zero edits to `<LocalExileRoot>/Mods/`) and pass `NATIVEMODS_OVERLAY_DIR=<staged-assets>` + `NATIVEMODS_MODS_DIR=<staged-client-mods>`.
3. Launch inside isolated `Xvfb` (`1280x720x24`) + a PipeWire `module-null-sink` (`PULSE_SINK=<null>`), verify `nativemods-shim.log` markers, open the native **Cosmetics (`K`) menu** to confirm the brand-new Pet MTX appears alongside stock pets and spawns when equipped, exercise `/maxwell` + `MaxwellPanel.ui`, capture screenshots via `import -window root`, and verify non-silent PCM energy on the null-sink monitor via `parec`.

#### Expected `nativemods-shim.log` Live Markers
When verifying a live Proton run, check `nativemods-shim.log` for:
```text
[OVERLAY] Armed live FileSystem hooks (exists=OK size=OK read_range=OK read_whole=OK read_v5=OK hash_lookup=OK)
[OVERLAY] Served 'data/pet.datc64' (... bytes via ...)
[UI] Armed Control::Load root observer (OK)
[NET] Armed live channel hooks (chat_rx=OK ruleset_0x2c9=OK)
[MANAGED] [INFO] [host] NativeMods managed host started (SDK 0.1.0-poc, build 3.29.3d, transport HostRuleset).
[AUDIO] FMOD 2.03 initialized (output=default|nosound, sys=0x...)
[AUDIO] Playing OGG 'Audio/Mods/Maxwell/stock_market.ogg' (bytes=..., loop=1, volume=..., handle=..., output=default|nosound, fmod_rc=0)
[UI] Captured native UI root 0x...
[UI] Loaded native window 'Metadata/UI/Mods/Maxwell/MaxwellPanel.ui' (root=0x..., control=0x...)
```

---

## 5. Diagnostics & Troubleshooting Cheat Sheet

| Symptom / Code | Root Cause | Fix |
|---|---|---|
| `UI002` / client crashes loading `.ui` | File starts with `<DEFINITIONS>` at byte 0 | Add a `//` comment or blank line before `<DEFINITIONS>` |
| `UI008` / client hangs at 100% CPU opening window | `Picture` inside `StackLayout` has `margin` | Remove `margin` from `Picture`; insert a spacer `begin Frame` (`size = 10, 12;`) |
| `UI016` / client closes with `0xE06D7363` | Duplicate sibling `id` under the same parent container | Give every sibling control a unique `id` |
| `UI010` / `UI013` (`error: ":" expected`) | Unknown property on a stock 3.29.3d control | Run `UiLayoutLinter.Lint(...)` and use only known properties |
| `PAY003` in `DualModPackageValidator` | Asset path is outside `Mods/<id>/` (or `Mods/<last-hyphen-segment>/`) and not listed in `replaces` | Move asset under `Mods/<namespace>/` or explicitly list stock override in `nativeClient.replaces` |
| `NC008` / `RawAccessDeniedException` | Mod uses `context.Raw` without `rawAccess: true` + `rawAccessJustification` | Add both fields to `mod.json` (and pass `InstallOptions.AllowRawAccess = true` on install) |
| `NC016` | Attempted to append to a closed `.datc64` table (`ItemClasses`) | Ride an existing `ItemClasses` value and append to `BaseItemTypes` instead |
| `ModUnloadResult.UnloadedCleanly == false` | Collectible ALC pinned by static cache or leaked delegate | Use `context.Net.Send`/`CollectibleJsonCodec` (never static `JsonSerializerOptions`), call `context.TrackDisposable(...)`, and unsubscribe `Game.Frame` in `Unload()` |
| Client disconnects / buffer underflow on `0x02bb` pet spawn | `.ot` file added an extra serialized component (e.g., `StateMachine`) | Extend `"Metadata/Pet/Pet"` with **zero extra serialized components** |
| Client crashes opening Cosmetics (`K`) tab | Owned `MtxTypes.HASH32` missing or mismatched in `MtxTypeGameSpecific.datc64` | Ensure `MtxTypeGameSpecific.HASH32 == MtxTypes.HASH32 == MurmurHash2(MtxTypes.Id, 0)` and both tables are overlaid before startup via `NATIVEMODS_OVERLAY_DIR` |
| Pet missing from Cosmetics (`K`) tab on server | `Pet.Id` used plural `Metadata/Pets/...` or included `.ot` | Use singular `Metadata/Pet/Mods/<Name>/<Pet>` (no `.ot` suffix) so `CosmeticEffects.PetTypes()` recognizes it |
| `Game.patched.exe` fails `LocalExile.Client.Patcher check` | Expected only stock `Game.exe` SHA-256 | Update to current `ClientPatcher` which recognizes `LocalExileKeyPatched329` via 6 verified code anchors (or pass `--allow-synthetic` for synthetic test binaries) |
| Client crashes loading appended `.datc64` row (`+0x129838`) | Cloned `.datc64` row aliased a stock `foreignrow[]` variable-section offset | Deep-copy or clear every `foreignrow[]` array column to a fresh variable-section offset (handled automatically by `MaxwellDatPatcher` / `CurrencyDatPatcher`) |
| Custom currency icon invisible or wrong texture in inventory | `ItemVisualIdentity.HASH16` collided or didn't match server `BaseType` | Assign a unique non-zero `HASH16` (indexed directly by `Base::Read` at `0x1421ed3ea`, not recomputed from `Id`) and pair `78x78` RGBA8 `.dds` + `.dds.header` |
| Proton cannot see `/tmp/...` staged files | `umu-run`'s `steamrt4` (`pressure-vessel`) container does not mount host `/tmp` | Stage temporary test artifacts under `<LocalExileRoot>/data/` (cleaned up in `trap EXIT`) |
