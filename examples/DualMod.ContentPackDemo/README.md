# DualMod.ContentPackDemo — Multi-Mod `.datpatch.json` Content Pack Showcase

This showcase proves that **two completely independent content mods** can touch the **exact same five Path of Exile 3.29.3d `.datc64` tables** (`BaseItemTypes`, `ItemVisualIdentity`, `CurrencyItems`, `Mods`, and the closed `Stats` dead-stat pool) and compose cleanly into a single conflict-free `.merged-cache/` via `DatPatchMerger` and `nativemods merge`.

## Included Mods

| Mod Folder | `modId` | Tables Touched | What It Adds / Modifies |
|---|---|---|---|
| `ModAlpha.AetherRelics/` | `aether-relics` | `ItemVisualIdentity`, `BaseItemTypes`, `CurrencyItems`, `Mods`, `Stats` (dead slot) | Appends custom currency **Aether Shard** (`$ref` wired across `ItemVisualIdentity` → `BaseItemTypes` → `CurrencyItems`), appends `AetherRelicUniqueMod1` referencing an allocated dead `Stats` slot (`aether_resonance_pct`), and generates UTF-16LE BOM+CRLF `Metadata/StatDescriptions/aether-relics_custom_stats.txt`. |
| `ModBeta.StackMastery/` | `stack-mastery` | `ItemVisualIdentity`, `BaseItemTypes`, `CurrencyItems`, `Mods`, `Stats` (dead slot) | Appends custom currency **Stack Catalyst** into the same tables, raises stock currency `StackSize` & `CurrencyTab_StackSize` in-place to **`65535`** (Wishlist #15 `u16` ceiling), appends `StackMasteryBoon1` referencing a second independent dead `Stats` slot (`stack_mastery_drop_qty_pct`), and generates `Metadata/StatDescriptions/stack-mastery_custom_stats.txt`. |

## Workflow

### 1. Validate both mods (including cross-mod conflict detection)

```bash
dotnet run --project src/LocalExile.Client.Cli -- validate \
  examples/DualMod.ContentPackDemo/ModAlpha.AetherRelics \
  --against examples/DualMod.ContentPackDemo/ModBeta.StackMastery
```

### 2. Merge both mods into a deterministic `.merged-cache/` + `content-row-map.json`

```bash
dotnet run --project src/LocalExile.Client.Cli -- merge \
  --stock /path/to/PoE/Client \
  --out ./Mods/Client/.merged-cache \
  examples/DualMod.ContentPackDemo/ModAlpha.AetherRelics \
  examples/DualMod.ContentPackDemo/ModBeta.StackMastery
```

The output directory contains:

- `Data/BaseItemTypes.datc64` (+2 appended rows, auto-computed unique `HASH32`, deep-copied `foreignrow[]` arrays)
- `Data/ItemVisualIdentity.datc64` (+2 appended rows, unique non-zero `HASH16`, zero array aliasing)
- `Data/CurrencyItems.datc64` (+2 appended rows + in-place `StackSize = 65535` update)
- `Data/Mods.datc64` (+2 appended rows wired to two distinct dead `Stats` rows)
- `Data/Stats.datc64` (strictly `23,362` stock rows preserved; stock `Stats[row].Id` & `HASH32` untouched for `LocalExile` `Stat.Checksum`)
- `Metadata/StatDescriptions/aether-relics_custom_stats.txt` (UTF-16LE BOM + CRLF)
- `Metadata/StatDescriptions/stack-mastery_custom_stats.txt` (UTF-16LE BOM + CRLF)
- `content-row-map.json` (deterministic row indices, hashes, and SHA-256 handshake `digest` independent of input ordering)
