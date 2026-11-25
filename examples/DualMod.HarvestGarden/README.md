# Harvest Garden (Dual-Side NativeMods PoC)

A complete dual-side LocalExile mod showcasing the 3.29 NativeMods architecture from `06-roadmap.md` and `11-client-sdk.md`:

- **Server half** (`Server/GardenServerMod.cs`): Authoritative seed inventory, plot growth stages, lifeforce ledger, `/garden` chat command, graceful text fallback when a player lacks the NativeMods extender, and rich UI/state sync over `LocalExile.Client.ServerBridge` when connected to a NativeMods client.
- **Client half** (`Client/GardenClientMod.cs`): Implements `IClientMod` against `LocalExile.Client.Sdk`:
  - Loads custom `Metadata/UI/Mods/Garden/Stockpile.ui`
  - Binds `Button`, `TextBox`, `CheckBox`, and `Label` controls and syncs over `mod.Net`
  - Registers custom UI control `GrowthGauge` (`Ui.RegisterControl`)
  - Registers custom GEAL built-in `GardenRipen` (`Scripts.Register`)
  - Registers custom per-area ruleset `GardenRuleset` and custom opcodes (`0x03E9`–`0x03EC`)
  - Ticks smooth client-side growth interpolation per frame (`game.Frame`)
- **Shared contract** (`Shared/GardenMessages.cs`): Typed message records and opcode IDs compiled into both halves.
- **Client payload** (`client/`):
  - `Metadata/UI/Mods/Garden/Stockpile.ui` (linted 3.29 `.ui` layout)
  - `Metadata/MiscellaneousObjects/Mods/Garden/Plot.ot` (custom object type with `client { }` GEAL calling `GardenRipen`)

## Commands

- `/garden` — Opens the Seed Stockpile UI (or prints garden status in chat when running without NativeMods).
- `/garden plant <seed>` — Plants a seed in the next open plot.
- `/garden harvest` — Harvests all ripe plots and collects lifeforce.
