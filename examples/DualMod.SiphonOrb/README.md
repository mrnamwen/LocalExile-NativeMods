# DualMod.SiphonOrb — Orb of Siphoning

Dual-side NativeMods example demonstrating reusable **custom currency items**, **persistent mod-bearing currency tooltips**, **live affix stealing & imbuing**, and a **Recombinator-style native 3.29.3d modifier picker UI** without modifying LocalExile core.

## Gameplay Flow

1. Run `/siphon give` (grants 5 empty **Orbs of Siphoning**) or use orbs already in inventory/stash.
2. **Right-click Orb of Siphoning -> Left-click** any Magic or Rare gear item (`C->S 0x0071`).
3. The Recombinator-style **`SiphonPicker.ui`** modal opens natively listing every explicit affix on the item with exact formatted PoE stat descriptions.
4. Select an affix and click **Confirm Siphon** — the server removes that affix from the source item, consumes 1 Empty Orb, and creates a persistent **Charged Orb of Siphoning** (`stack_size = 1`, `Mods` component with `ModSection.Enchant`) showing the stolen modifier right on its native PoE hover tooltip and surviving relog/restart in `characters.db`.
5. **Right-click Charged Orb of Siphoning -> Left-click** a second Magic or Rare item -> **`SiphonPicker.ui`** opens in **Imbue** mode displaying the stored modifier above the destination item's replaceable affixes (automatically locking incompatible slots for fractured mods, full Prefix/Suffix caps, or duplicate mod groups). Select a modifier and click **Confirm Imbue** to replace it with the exact rolled values and consume the Charged Orb.

Vanilla 3.29 clients without the NativeMods client extender automatically fall back to numbered chat prompts (`/siphon pick <1..N>` and `/siphon cancel`).

## Appended 3.29.3d Currency Table Coordinates

Never overwrites any stock Path of Exile currency row:

| Table | Empty Orb (`Orb of Siphoning`) | Charged Orb (`Charged Orb of Siphoning`) |
|---|---|---|
| `BaseItemTypes` (`5461` stock) | Row `5461` (`HASH32 = 0x2B06B9D8`) | Row `5462` (`HASH32 = 0xBD658699`) |
| `ItemVisualIdentity` (`15516` stock) | Row `15516` (`HASH16 = 0xD501`) | Row `15517` (`HASH16 = 0xD502`) |
| `CurrencyItems` (`1962` stock) | Row `1962` (`StackSize = 20`, `UseType = 2`, `Action = remove_random_mod`) | Row `1963` (`StackSize = 1`, `UseType = 2`, `Action = remove_random_mod`) |
| Serialized Components | `Base, Usable, Stack` | `Base, Usable, Stack, LocalStats, Mods` |

## Reusable APIs (`LocalExile.Client.ServerBridge`)

- `CustomCurrencyItemDefinition` & `LocalExileCurrencyRegistrar`: appends custom currencies into LocalExile's live `ItemCatalog` on `Load` (cleanly removing on `Dispose`) and routes `C->S 0x0071`/`0x0072` strictly for registered custom `TypeHash`es without touching stock currencies.
- `LocalExileItemAffixService`: inspects explicit affixes with formatted PoE `stat_descriptions.txt` lines, validates Prefix/Suffix/group/fracture rules, steals affixes, embeds them onto mod-bearing currencies, and replaces target affixes while preserving exact rolled values.

## Natural World Currency Drops & Declarative `.datpatch.json`

- **Natural Drop-Pool Integration (`SiphonLocalExileEntry.cs` + `settings.json`):** On mod load, `SiphonLocalExileEntry` registers both orbs into LocalExile's live `ItemCatalog` via `ItemCatalog.RegisterOverlay` and registers Empty Orbs of Siphoning into `DropDataProvider.Default.RegisterOverlay` (`DropPoolCategory.Currency`) with zero SQLite mutation and zero reflection. Drop weight (`dropWeight`, default `25`), level gates (`minAreaLevel = 45`, `maxAreaLevel = 0`), drop sources (`MonsterKill`, `Container`, `Strongbox`, `BossBonus`, `MapCurrency`), and `monsterRarity` filters are all configurable in `settings.json`, preserved across `/reload`, and automatically removed on mod unload via `GameEventOwners`.
- **Composable `.datpatch.json` (`client/patches/siphon-orb.datpatch.json`):** Declares the `ItemVisualIdentity`, `BaseItemTypes`, and `CurrencyItems` appends in `DatPatchDocument` v1 format so `DualMod.SiphonOrb` can merge cleanly alongside other currency/content mods via `DatPatchMerger`.
