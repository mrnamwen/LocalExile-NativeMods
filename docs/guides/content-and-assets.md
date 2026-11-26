# Brand-New Content, Assets, Overlays, Audio & Packaging

Add **brand-new** 3D models, textures, looping `.ogg` audio, Pet MTXs (appearing natively in the Cosmetics `K` menu), and Custom Currencies to Path of Exile 3.29 without replacing stock content or mutating `Client/` files.

[← Documentation Home](../README.md) · [AssetPipeline API Reference](../api/asset-pipeline.md) · [Waypoint API Reference](../api/waypoint.md) · [Capabilities & Limits](capabilities-and-limits.md)

---

## 1. Route A Universal File Overlays (`IFileOverlayService`)

NativeMods arms **Route A universal live file overlay hooks** in `LocalExile.ClientHost.dll` at DLL init (`< 2 ms`, *before* CoreCLR boots and *before* PoE reads `Data/*.datc64`):
- Hooked 3.29.3d functions: `files.exists_query` (`0x0e55340`), `files.size_query` (`0x0e557f0`), `files.read_variant_2` (`0x0e55aa0`), `files.read_whole_file` (`0x0e564d0`), and `files.read_variant_5` (`0x0e56af0`).
- Buffers are allocated from PoE's own slab allocator (`0x2c8cf60`) with 16-byte header + atomic `g_allocatedBytes` (`0x45d36a8`) accounting, bypassing PoE's extension trap for `.ot`, `.ao`, `.dds.header`, `.smd`, `.ast`, and `.datc64`.

### Overlay Precedence (Highest Wins)

1. Managed `IFileOverlayService`: `VirtualFile` (default priority `100`) > `LooseDirectory` (`0`) > `BundleOverlay` (`-10`).
2. `NATIVEMODS_OVERLAY_DIR` environment variable (semicolon-separated directories, e.g., `dist/maxwell-assets;dist/siphon-assets` — active from millisecond 0 before CoreCLR boots, required for startup `.datc64` tables).
3. Auto-discovered `<NATIVEMODS_MODS_DIR>/<mod>/{client,files}` (or `<dll_dir>\Mods\Client\<mod>\{client,files}`).

> **Diagnostic Env Vars:** Set `NATIVEMODS_TRACE_FILES=1` to trace `mods/` and `.dat` lookups in `nativemods-shim.log`, `NATIVEMODS_HOST_DIR` for an explicit self-contained `.NET 10` host directory, or `NATIVEMODS_DISABLE_AUTO_INIT=1` in unit harnesses.

```csharp
IFileOverlayService files = context.Game.Files;

// 1. Register a loose directory
context.TrackDisposable(files.RegisterDirectory(Path.Combine(context.ModDirectory, "client"), priority: 10));

// 2. Register virtual text (auto-selects 12 BOM+CRLF vs. 3 No-BOM + art/uiimages1.txt via EncodeTextForPath)
context.TrackDisposable(files.RegisterVirtualTextFile(
    "Metadata/Pet/Mods/MyMod/MyPet.ot",
    "version 2\nextends \"Metadata/Pet/Pet\"\n",
    priority: 100));

// 3. Register raw binary bytes (.dds, .ogg, .datc64)
context.TrackDisposable(files.RegisterVirtualFile("Audio/Mods/MyMod/theme.ogg", oggBytes, priority: 100));

// 4. Register a packed NMB1 stored-block bundle ([u32 chunkLen][0xCC 0x06][payload])
context.TrackDisposable(files.RegisterBundle(bundlePath, manifestPath, priority: -10));

// 5. Inspect, read un-overlaid stock bytes, and hot-invalidate parsed cache (0x1010560)
if (files.Exists("Metadata/UI/Mods/MyMod/Panel.ui"))
{
    string? uiSource = files.ReadAllText("Metadata/UI/Mods/MyMod/Panel.ui");
    byte[]? stockBytes = files.ReadStockGameFile("Metadata/Items/Item.it");
    files.InvalidateCachedAsset("Art/Textures/Mods/MyMod/icon.dds");
}
```

---

## 2. 3D Meshes (`.glb` → `.smd`/`.sm`/`.mat`/`.ast`/`.amd`), `.dds` Textures & FMOD `.ogg` Audio

### CLI Commands (`LocalExile.Client.AssetPipeline`)

```bash
# 1. glTF 2.0 .glb -> native 3.29.3d .smd + .smd.header + .sm + .ast v7 + .amd + .mat + .ot/.ao/.act
dotnet run --project src/LocalExile.Client.AssetPipeline -c Release -- \
  convert-glb --in /path/to/model.glb --out dist/my-assets

# 2. PNG/JPEG/TGA -> PoE DX10 .dds + exact 3.29.3d .dds.header (bc1 | bc3 | rgba8)
dotnet run --project src/LocalExile.Client.AssetPipeline -c Release -- \
  convert-dds --in /path/to/icon.png --out dist/my-assets/Art/2DItems/Pets/Mods/MyMod/MyPet.dds --format rgba8

# 3. .opus/.mp3/.wav -> 48 kHz stereo Vorbis .ogg (validated OggS + \x01vorbis header)
dotnet run --project src/LocalExile.Client.AssetPipeline -c Release -- \
  convert-audio --in /path/to/theme.opus --out dist/my-assets/Audio/Mods/MyMod/theme.ogg

# 4. Append Pet MTX rows to stock .datc64 extracted read-only from Client/Bundles2/
dotnet run --project src/LocalExile.Client.AssetPipeline -c Release -- \
  patch-dat --client /home/nam/Git/LocalExile/Client --out dist/my-assets

# 5. Append Custom Currency rows from a JSON spec (--base-overlay layers multiple currency mods)
dotnet run --project src/LocalExile.Client.AssetPipeline -c Release -- \
  build-currency --spec my-currencies.json --client /home/nam/Git/LocalExile/Client --out dist/my-currency-assets

# Turnkey showcase scripts:
./scripts/build-maxwell-assets.sh
./scripts/build-siphon-assets.sh
```

### C# Library API (`LocalExile.Client.AssetPipeline`)

```csharp
using LocalExile.Client.AssetPipeline.Audio;
using LocalExile.Client.AssetPipeline.Dds;
using LocalExile.Client.AssetPipeline.Mesh;

// 1. 3D mesh bundle (rigid Y-up -> PoE Z-down rotation, 1-bone v7 uncompressed .ast, no-BOM .sm/.mat/.amd)
GlbScene scene = GlbParser.ParseFile("model.glb");
PoePetAssetBundle meshBundle = GlbToPoeMeshConverter.Convert(scene, new PoePetMeshOptions
{
    ArtBasePath      = "Art/Models/Mods/MyMod",
    MetadataBasePath = "Metadata/Pet/Mods/MyMod",
    ObjectBaseName   = "MyPet",
    ModelBaseName    = "mypet",
    TargetLongestHorizontalSpan = 84f,
});

// 2. Texture -> DX10 .dds + .dds.header pair
//    PoeDdsFormat.Bc1   = DXGI 71 / PoE fmt 205 (opaque albedo)
//    PoeDdsFormat.Bc3   = DXGI 77 / PoE fmt 207 (compressed alpha)
//    PoeDdsFormat.Rgba8 = DXGI 28 / PoE fmt 21  (2D icons 156x156 pets, 78x78 currencies)
DdsOutput iconDds = DdsTextureConverter.ConvertImageFile("icon.png", PoeDdsFormat.Rgba8, resizeWidth: 156, resizeHeight: 156);
DdsTextureConverter.WritePair(Path.Combine(outDir, "Art/2DItems/Pets/Mods/MyMod/MyPet.dds"), iconDds);

//    512x512 black-background PNG -> 78x78 flood-fill alpha-cutout RGBA8 currency icon:
DdsOutput orbIcon = DdsTextureConverter.ConvertCurrencyIconFile("siphon_orb_empty.png", targetWidth: 78, targetHeight: 78);
DdsTextureConverter.WritePair(Path.Combine(outDir, "Art/2DItems/Currency/Mods/Siphon/SiphonOrbEmpty.dds"), orbIcon);

// 3. Audio -> 48 kHz stereo Vorbis .ogg
OggVorbisInfo oggInfo = AudioTranscoder.TranscodeToOggVorbis("theme.opus", Path.Combine(outDir, "Audio/Mods/MyMod/theme.ogg"));
```

### Looping Custom `.ogg` Audio & FMOD 2.03 Events (`IAudioService`)

`IAudioService` dynamically loads PoE's `fmod.dll` **2.03** (automatically falling back to `FMOD_OUTPUTTYPE_NOSOUND` on headless machines; active channel limit `0x200` = `512`):

```csharp
IAudioService audio = context.Game.Audio;

int handle = audio.PlaySoundFile("Audio/Mods/Maxwell/stock_market.ogg", volume: 0.75f, loop: true);
audio.SetChannelVolume(handle, 0.40f);
bool playing = audio.IsChannelPlaying(handle);
audio.StopChannel(handle); // Always stop in IClientMod.Unload()!

int sfx = audio.PlayEvent("event:/UI/ButtonClick", volume: 1.0f);
```

---

## 3. `.datc64` Hashing, Generic Row Appending & `foreignrow[]` Deep-Copy Rule

Path of Exile 3.29 computes `HASH32` (and `S→C 0x02bb` `ObjectTypeHash`) via **32-bit MurmurHash2 (seed `0`) over narrow UTF-8/ASCII bytes** (`Game.exe 0x140df6e10`), and `Pet.HASH16` via XOR fold `((hash32 >> 16) ^ (hash32 & 0xFFFF))`:

```csharp
// Identical results from PoeContentHash (ServerBridge) and PoeHashing (AssetPipeline.Dat64):
uint mtxHash32   = PoeContentHash.MurmurHash2("Metadata/Items/Pets/Mods/MaxwellPet");   // 0xA1EB592E
uint petHash32   = PoeContentHash.MurmurHash2("Metadata/Pet/Mods/Maxwell/MaxwellPet");  // 0x1F3AB11A
ushort petHash16 = PoeContentHash.FoldHash16(petHash32);                                 // 0xAE20
```

### Generic `Dat64TablePatcher` (18 Verified Appendable Tables)

See [Capabilities & Limits](capabilities-and-limits.md#what-you-can-do-vs-what-you-cannot-do) for the 18 appendable tables vs. closed `ItemClasses` & `Stats`.

> **Critical `foreignrow[]` Deep-Copy Rule:** PoE resolves `foreignrow[]` entries in-place inside the variable-data section. **Never** leave a cloned row pointing at the template row's `foreignrow[]` variable-section offset (causes a `+0x129838` crash on town entry). Always deep-copy or clear every `foreignrow[]` array column via `SetForeignRowArray(...)`.

```csharp
using LocalExile.Client.AssetPipeline.Dat64;

Dat64Table table = Dat64Table.Parse(stockTableBytes);
var patcher = new Dat64TablePatcher(table);

byte[] row = patcher.CloneRow(templateRowIndex: 0);
patcher.SetString(row, columnOffset: 0, "Metadata/MiscellaneousObjects/Mods/MyMod/Thing");
Dat64TablePatcher.WriteUInt32(row, columnOffset: 8, PoeHashing.Hash32("Metadata/MiscellaneousObjects/Mods/MyMod/Thing"));
Dat64TablePatcher.WriteForeignRow(row, columnOffset: 16, targetRowIndex: 42UL);
patcher.SetInt32Array(row, columnOffset: 32, [18, 19]);

int newRowIndex = patcher.AppendRow(row);
byte[] outputBytes = patcher.BuildAndVerify().Serialize();
```

---

## 4. Brand-New Pet MTXs (Cosmetics `K` Menu Integration)

Appends **1 row** to each of 3 tables (`MtxTypes` `8557`, `MtxTypeGameSpecific` `7849`, `Pet` `516`) with zero `BaseItemTypes` edits:

| Table (3.29.3d) | Stride | Stock Rows | Critical Columns |
|---|---:|---:|---|
| `Data/MtxTypes.datc64` | `180 B` | `8557` | `Id` `@0`, `HASH32` `@8`, `Name` `@12`, `Description1` `@28`, unique `__col18` `@137` (clone `Kiwi1` row `5730`) |
| `Data/MtxTypeGameSpecific.datc64` | `68 B` | `7849` | `Type` `@0` → new `MtxTypes` row, **`HASH32` `@16` == `MtxTypes.HASH32`**, `DDSFile` `@20`, `Category` `@28` = `14`, `__col4` `@32` = `[15]`, `__col6` `@52` = `[18, 19]` (Pet Slots 1 & 2) |
| `Data/Pet.datc64` | `138 B` | `516` | `Id` `@0` = **singular** `Metadata/Pet/Mods/...` (no `.ot`), `MtxTypeGameSpecificKey` `@8`, `HASH32` `@24`, `HASH16` `@28`; deep-copy `__col7` `@36` & `Skills` `@68` |

```csharp
// Asset pipeline (reads stock .datc64 read-only from Client/Bundles2/ & verifies non-aliasing):
using Bundles2Reader bundles = Bundles2Reader.Open("/path/to/LocalExile/Client");
PatchedPetMtxResult patched = MaxwellDatPatcher.AppendPetMtx(
    bundles.ReadFile("Data/MtxTypes.datc64"),
    bundles.ReadFile("Data/MtxTypeGameSpecific.datc64"),
    bundles.ReadFile("Data/Pet.datc64"),
    new PetMtxPatchSpec
    {
        MtxTypeId   = "Metadata/Items/Pets/Mods/MyCustomPet",
        DisplayName = "My Custom Pet",
        Description = "Summons your brand-new custom pet.",
        PetId       = "Metadata/Pet/Mods/MyMod/MyCustomPet",
        IconDdsPath = "Art/2DItems/Pets/Mods/MyMod/MyCustomPet.dds",
    });

// Server-side registration (Cosmetics K menu + 29-byte S->C 0x0204 grant + OnCosmeticChanged):
var myPet = new CustomPetMtxDefinition
{
    MtxTypesRowIndex            = CustomPetMtxDefinition.Stock3293dMtxTypesCount,            // 8557
    MtxTypeGameSpecificRowIndex = CustomPetMtxDefinition.Stock3293dMtxTypeGameSpecificCount, // 7849
    PetRowIndex                 = CustomPetMtxDefinition.Stock3293dPetCount,                 // 516
    MtxTypeId                   = "Metadata/Items/Pets/Mods/MyCustomPet",
    DisplayName                 = "My Custom Pet",
    Description                 = "Summons your brand-new custom pet.",
    PetMetadataPath             = "Metadata/Pet/Mods/MyMod/MyCustomPet",
    IconDdsPath                 = "Art/2DItems/Pets/Mods/MyMod/MyCustomPet.dds",
    AllowedSlots                = [CustomPetMtxDefinition.PetSlot1, CustomPetMtxDefinition.PetSlot2],
};
using var registrar = new LocalExilePetMtxRegistrar();
registrar.Register(myPet);
registrar.ApplyToLiveCatalogs(Game.Mtx, Game.MtxEffects, mod.Log);
byte[] grantPacket = LocalExilePetMtxRegistrar.EncodeRecordUpsertPacket(myPet, justReceived: true);
```

---

## 5. Brand-New Custom Currencies (`CurrencyDatPatcher` & `LocalExileCurrencyRegistrar`)

Appends rows across 3 tables (zero overlap with Pet MTX tables — both overlays compose cleanly via semicolon-separated `NATIVEMODS_OVERLAY_DIR`):

| Table (3.29.3d) | Stride | Stock Rows | Critical Columns & Invariants |
|---|---:|---:|---|
| `Data/BaseItemTypes.datc64` | `374 B` | `5461` | Unique `Id` `@0`, `HASH32` `@140`, `ItemClassesKey` `@8` = `30` (`StackableCurrency`), `ItemVisualIdentity` `@124`. Deep-copy/clear all 11 `foreignrow[]` array columns. |
| `Data/ItemVisualIdentity.datc64` | `604 B` | `15516` | Unique `Id` `@0`, unique non-zero **`HASH16` `@40`** (indexed directly by `Base::Read` `0x1421ed3ea`), `DDSFile` `@8`, `AOFile` `@16`, `InventorySoundEffect` `@24` = `33`. Clear all 16 array columns. |
| `Data/CurrencyItems.datc64` | `205 B` | `1962` | Unique `BaseItemTypesKey` `@0`, `StackSize` `@16`, `CurrencyUseType` `@20` = `2` (right-click → left-click item), `Action` `@24` = `"remove_random_mod"` (emits `C→S 0x0071`/`0x0072`). |

```csharp
// 1. Patch .datc64 + generate UTF-16LE BOM .it metadata (SupportsStoredMods=true adds LocalStats {} + Mods {}):
PatchedCurrencyResult patched = CurrencyDatPatcher.AppendCurrencies(
    stockBaseItemTypes, stockItemVisualIdentity, stockCurrencyItems, specs);
byte[] itBytes = CurrencyDatPatcher.BuildItemMetadataItBytes(specs[0]);

// 2. Server-side registration & right-click routing (C->S 0x0071 UseItemOn / 0x0072 UseItemTypeOn):
using var currencyRegistrar = new LocalExileCurrencyRegistrar();
currencyRegistrar.Register(emptyOrbDef, async (CurrencyUseContext ctx) => await HandleSiphonUseAsync(ctx));
currencyRegistrar.ApplyToLiveCatalog(Program.Items, mod.Log); // Cleanly removed on Dispose()
```

---

## 6. Validation, Bundling (`NMB1`) & Waypoint Packaging

Before shipping any mod, run `DualModPackageValidator`, `UiLayoutLinter`, and `ModBundlePacker` (stored-block `NMB1`: `[u32 chunkLen][0xCC 0x06][payload]`):

```csharp
using LocalExile.Client.Sdk;
using LocalExile.Client.Waypoint;

ValidationReport report = DualModPackageValidator.ValidatePackageDirectory(packageDir, SemVer.Parse(SdkVersion.Current));
if (!report.IsValid)
    throw new InvalidOperationException(string.Join('\n', report.Errors));

var installer = new WaypointModInstaller();
InstallAuditReport audit = installer.Install(packageDir, localExileRoot, new InstallOptions { PackClientBundle = true });
```

See [Waypoint Integration](../architecture/waypoint-integration.md) for the complete `mod.json` schema, security limits, and installer ledger format.

---

## Related API Reference

- [AssetPipeline API Reference](../api/asset-pipeline.md)
- [Waypoint API Reference](../api/waypoint.md)
- [`LocalExilePetMtxRegistrar` & `LocalExileCurrencyRegistrar`](../api/server-bridge.md)
- [`IFileOverlayService` & `ClientTextEncoding`](../api/sdk-net-files-raw-extend.md)
