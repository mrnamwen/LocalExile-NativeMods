# Regression & Pitfall Log: 3.29.3c Data-Address Drift & Refuted Static Claims

Source: `10-verification.md` (overrides `08-deep-analysis.md` and `09-native-mods.md`).

## 1. 3.29.3c vs. 3.29.3d Address Drift

- **Rule:** Code RVAs (`.text` functions) match between 3.29.3c and 3.29.3d, **but data RVAs** (globals, vtables, static tables such as the ruleset creator array `0x14475dc70`, variant tag `0x1444a6930`, and control vtables) **do not transfer**. All `kind: "vtable"` entries MUST store `"expectedBytes": null` because `.rdata` vtables contain pointers relocated by `DYNAMIC_BASE` ASLR.
- **Mitigation & Current Counts (`0.3.0`):** `data/addresses/3.29.3d.json` holds **`240` symbols**, **`160` offsets**, **`18` constants** (`240/240` verified, `170/170` non-null `expectedBytes`). `data/addresses/3.29.3c.json` holds **`240` symbols** (**`153` `null`/`unverified_3_29_3c`**, **`87` verified/shared**). `scripts/generate-address-table.py --check` generates `address_table.cpp` with `0ull` / `NM_SYM_STATUS_UNVERIFIED` for `null` RVAs — never copy 3.29.3d data addresses blindly into 3.29.3c.

## 2. Refuted File Overlay "Route C" & Extension Trap

- **Refuted claim (`08` §1):** Loose files on disk override `Bundles2/_.index.bin`.
- **Actual behavior (`10` §2):** Precedence is strictly **index table → GGPK → disk (`<cwd>/<path>`)**. A loose file **never** overrides an indexed path.
- **Extension trap:** When an index is loaded, `exists` (`0x140e55340`) and `size` (`0x140e557f0`) immediately return "absent" **without probing disk** for `.ot`, `.otc`, `.ao`, `.aoc`, `.it`, `.itc`, and `.header` paths missing from the index.
- **Cached parsed assets:** While raw file bytes are not cached, parsed materials, meshes, textures, and UI are cached by path; live reload requires invoking the client's invalidator (`0x141010560`, wired in `0.2.0` as `nm_files_invalidate` / `IFileOverlayService.Invalidate`) or rebuilding UI windows.

## 3. Refuted Ruleset Wire ID, Creator Table, and Failure Mode

- **Refuted claim (`08` §3):** `0x2ca` is bidirectional, creator table is `0x144831770..ab8` (33 creators, 24-slot vtable), unknown ruleset ID triggers "Ruleset protocol violation."
- **Actual behavior (`10` §3):**
  - **S→C is `0x2c9`** (handled by filter `0x1415a6b50`); **`0x2ca` is C→S only**.
  - Creator array is a writable table at **`0x14475dc70`** (`0xaf` slots, **68** filled), objects have a **9-slot vtable** (slot 8 = `DeserialiseRulesetUpdate`).
  - Missing creator logs `Failed to create ruleset N` and continues; `0x2c9` for an absent ruleset ID is **silently dropped**. "Ruleset protocol violation" fires only when payload size exceeds `0xffff` bytes.

## 4. Refuted GEAL Built-in Count & `ExecuteGEAL`

- **Refuted claim (`08` §4):** 1,221 built-ins are callable on client, `ExecuteGEAL` is a run-text console entry, and there is no loop cap at all.
- **Actual behavior (`10` §4):**
  - Only **724** built-ins exist on the client (227 client + 499 shared); 497 server-only names fail to compile (`Function X does not exist`).
  - `ExecuteGEAL` is a skill class backed by `Data/ExecuteGEAL.dat` (167 rows), not an arbitrary eval API.
  - `While`/`DoWhile` stop at 1,000 iterations, though `Repeat(n)` and recursion remain uncapped.

## 5. Content Table, Encoding, `ModHost` & Packet Fatal Boundaries

- **Appendable vs. Closed tables (`0.3.0`):** `DualModPackageValidator.AppendableRegistryTables` recognizes **18 live-verified appendable tables** (`BaseItemTypes`, `ItemVisualIdentity`, `CurrencyItems`, `MtxTypes`, `MtxTypeGameSpecific`, `Pet`, `MonsterVarieties`, `NPCs`, `Chests`, `Projectiles`, `MiscObjects`, `MiscAnimated`, `HideoutDoodads`, `ExecuteGEAL`, `ClientStrings`, `Mods`, `ModType`, `BuffDefinitions`).
- **Refuted claim (`10` §5 / `0.2.0` `NC017`):** "`Stats.datc64` is appendable and new rows are merely inert."
  - **Actual behavior (PR `#1615` + `re-stats-enum` `#48`):** **`Stats.datc64` is strictly CLOSED at `23,362` (`0x5b42`) rows.** Appending rows triggers `"Stats enum is out of sync"` (`0x1428eed00` / `0x143648bd0`) and exits at character select; moreover, row `23362` produces 1-based `StatId 0x5b43` which collides with compiled `StatId::INVALID` across 35+ inlined sites AND causes unchecked heap OOB writes/reads in `StatDescriptionList`, `StatValues`, `g_statInfoTable`/`g_statTopoOrder`/`g_statTopoInv`, and `StatCalc::m_hasStatBitset`. `"Stats"` is now enforced in `ClosedRegistryTables` (`NC016`). Use in-place non-ID column edits, Phase 3 dead-slot allocation (`30` verified `old_do_not_use_*`/`currently_unused_*` rows keeping stock `Id` + Route A `Metadata/StatDescriptions/*.txt` overlays; never row `464`), or unbounded high-ID `StatsComponent` (`0x1b3` varint) stats.
- **Other closed tables (`10` §5):** `ItemClasses` is bound to a compiled 103-entry enum (`0x142fe0b80`); `ActiveSkills` action classes are bound to 2,155 compiled C++ classes.
- **Special no-BOM text file (PR `#1615` / `#1201`):** While general `.txt` assets use UTF-16LE BOM, **`art/uiimages1.txt` requires no BOM** (case-insensitive exception enforced in `ModBundlePacker`).
- **`ModHost` auto-reload loop hazard (PR `#1615`):** Writing any file inside a server mod's own directory under `Mods/Binaries/<id>/` triggers LocalExile's `ModHost` file watcher reload loop. Server mods must store runtime files under `data/mods/` or via `IPlayerWorld.AccountData`/`CharacterData`. NativeMods' `Mods/Client/` runtime directory is outside `Mods/Binaries` and safe.
- **Loot filter path trap (PR `#1615`):** PoE 3.29 always enumerates `.filter` files from `Documents\My Games\Path of Exile`, ignoring `Client\config.json` subfolder redirection.
- **Unknown object type:** Throws rather than merely logging.
- **Unclaimed packet ID / escaped handler exception:** Always fatal (`Unable to deserialise packet with pid N` → `RaiseException(0xE06D7363)` → disconnect). Never send a reserved packet ID (`>= 0x2fc`) to a client without a registered handler group (`network.register_handler_group` `0x29a7710`, wired as `nm_register_opcode_handler_group` in `0.2.0`), and never let an exception escape a handler group.
- **M7 Tier 5 Protocol, Manifest & Registry Bounds:**
  - `OverlayAndChannels::register_opcode_handler_group` enforces `min_opcode >= 0x02FC`; `nm_send_raw_packet` & `send_live_raw_packet` enforce `1..65536` (`NM_ERR_INVALID_ARG` / `NM_ERR_PAYLOAD_TOO_LARGE`).
  - `ClientModManifest` separates `BundleSha256` (`bundleSha256`/`integrity.packageSha256`) from `PayloadHash` (`payloadHash`/`assemblyPayloadHash`/`assemblySha256`) so dual-side mods shipping `assembly` + `bundleSha256` aren't falsely rejected by DLL hash verification.
  - `ContentRegistry` rejects duplicate `(TableName, EntryKey)` / `MetadataPath` registrations.
  - `data/addresses/3.29.3{c,d}.json` `ui.control.flags` (`+0x1e8`) explicitly documents bit 13 (`0x2000` enabled, `Control::SetEnabled 0x18b0c70`).
