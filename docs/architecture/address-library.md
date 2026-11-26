# 3.29 Address Library Guide

The **Address Library** (`data/addresses/`) decouples the NativeMods script extender, managed host, and client mods from hardcoded binary addresses. Because Path of Exile 3.29 is compiled with `DYNAMIC_BASE` (ASLR), every runtime address is computed from a verified **RVA** (`runtimeAddress = loadedImageBase + rva`) and checked against expected instruction bytes before any detour is installed.

The repository ships **`426` symbols**, **`357` struct offsets**, and **`18` verified constants** across `3.29.3d` and `3.29.3c` (single source of truth in `data/addresses/{3.29.3d,3.29.3c}.json`, code-generated into `src/native-shim/src/address_table.cpp` and verified via `python3 scripts/generate-address-table.py --check`).

---

## 1. Supported Client Builds

Official unpatched SHA-256 digests matching LocalExile's `ClientBuilds.cs`:

| Game Version | Build Number | Executable SHA-256 (Unpatched) | Address File | Notes |
|---|---|---|---|---|
| **3.29.3d** | `328228` | `e9ba3d31c4d922e1826ef01d02fd22178a001b29e17264b68fc2446efd41f53d` | `data/addresses/3.29.3d.json` | Primary live- and static-verified build (`79,394,136` bytes, `ImageBase = 0x140000000`, `426/426` verified, `325` non-null `expectedBytes`; all `78` `kind: "vtable"` entries store `expectedBytes: null` for `DYNAMIC_BASE` ASLR safety). |
| **3.29.3c** | `327984` | `cc8aa38345be2a837575215d2ce142ebecf7b007345843265b335ebb8ac5964c` | `data/addresses/3.29.3c.json` | `87` verified/shared code RVAs (`code_shared_3_29_3c`); `339` unverified entries explicitly carry `rva: null` (`unverified_3_29_3c`, queued for Phase 5 batch port); `network.hnc_mask` verified at `0x2feaed0`. |
| **3.29.3c** (3.29.3.5 hotfix) | `328126` | `01a22facf36886b39bca74793c0d8c7295b1495f1625b4ebc7b23c2de32a70cf` | `data/addresses/3.29.3c.json` | Same cross-build rule as 327984. |

> **Critical 3.29.3c Rule:** `.text` code addresses match between 3.29.3c and 3.29.3d, **but `.data` / `.rdata` addresses (globals, vtables, IAT slots, static tables) drift between builds**. Never copy a 3.29.3d data RVA into `3.29.3c.json` without verifying it against the 3.29.3c executable.

---

## 2. JSON Schema (`data/addresses/address-library.schema.json`)

Every Address Library file conforms to `schemaVersion: 1` (`$id: https://localexile.dev/schemas/nativemods/address-library.schema.json`, `additionalProperties: false`):

### Top-Level Object

| Field | Type | Description |
|---|---|---|
| `schemaVersion` | `integer` (`const: 1`) | Schema format version. |
| `gameVersion` | `"3.29.3c" \| "3.29.3d"` | Target client version. |
| `imageBase` | `string` (`"0x140000000"`) | Nominal PE32+ image base (`va = imageBase + rva`). |
| `builds` | `array<ClientBuildRecord>` | Recognized executables (`buildNumber`, `label`, `executableSha256`, `executableSize`, optional `steamIndexSha256`, `source`). |
| `symbols` | `array<AddressSymbolRecord>` | Named functions, globals, vtables, import slots, and RIP-relative global references. |
| `offsets` | `array<StructOffsetRecord>` | Verified struct/object field offsets (`name`, `struct`, `offset`, `type`, `status`, optional `notes`, `source`). |
| `constants` | `AddressConstants` | Verified protocol, VM, bundle, and PE constants (§4). |

### `symbols[]` Entry Fields

| Field | Type | Allowed Values / Format |
|---|---|---|
| `name` | `string` | `^[a-z0-9]+(\.[a-z0-9_]+)+$` (e.g., `frame.state_stack_pass_a_hook`, `ui.control_load`). |
| `category` | `string` | `frame`, `ui`, `files`, `geal`, `rulesets`, `network`, `allocator`, `runtime`, `audio`, `stats`, `world`, `components`, `skills`, `items`, `patch`. |
| `kind` | `string` | `code`, `data`, `vtable`, `import_slot`, `global_ref`. |
| `rva` | `string \| null` | Hex RVA relative to `0x140000000` (e.g., `"0x1528fbf"`), or `null` when unverified. |
| `va` | `string \| null` | Hex nominal VA (`"0x141528fbf"`), or `null` when unverified. |
| `expectedBytes` | `string \| null` | Hex byte sequence with optional `??` wildcards (`^([0-9a-fA-F]{2}\|\?\?)+$`), or `null` when not verified. |
| `status` | `string` | `live_verified`, `static_verified`, `doc_verified`, `code_shared_3_29_3c`, `unverified_3_29_3c`. |
| `callingConvention` | `string \| null` | `"win64"`, `"none"`, or `null`. |
| `signature` | `string \| null` | C/C++ prototype or type description. |
| `notes` | `string \| null` | Behavioral constraints, locks, or hazards. |
| `source` | `string` | Ground-truth document reference (e.g., `07-spike-results.md`, `10-verification.md §3`, `spikes/agent.js`). |

---

## 3. Core Verified Symbol Table (3.29.3d)

*(Run `dotnet run --project src/LocalExile.Client.Patcher -- dump-addresses --build 3.29.3d --addresses data/addresses` to inspect all 426 symbols and their full expected byte signatures.)*

| Symbol Name | Category | 3.29.3d VA | RVA | Kind | Status | Notes |
|---|---|---|---|---|---|---|
| `frame.state_stack_pass_a` | `frame` | `0x141528f40` | `0x1528f40` | `code` | `static_verified` | `StateStack::PassA` (vtable slot 2) |
| `frame.state_stack_pass_a_hook` | `frame` | `0x141528fbf` | `0x1528fbf` | `code` | `live_verified` | Bytes `488b5d284883eb10`; skipped when state stack is empty |
| `frame.ingame_tick` | `frame` | `0x140d54a00` | `0xd54a00` | `code` | `static_verified` | In-game tick tail (body `frame.ingame_update_body` at `0xd54c50`) |
| `ui.control_load` | `ui` | `0x1418b08d0` | `0x18b08d0` | `code` | `live_verified` | Wrap in SEH `0xE06D7363` exception wall |
| `ui.control_pool` | `ui` | `0x1418b6d20` | `0x18b6d20` | `code` | `live_verified` | Control allocator singleton |
| `ui.frame_create` | `ui` | `0x14014b0e0` | `0x14b0e0` | `code` | `live_verified` | `Frame* create(pool)` |
| `ui.add_child` / `ui.find_child` / `ui.set_visible` | `ui` | `0x1418b5b10` / `0x1418b6420` / `0x1418b0a30` | `0x18b5b10` / `0x18b6420` / `0x18b0a30` | `code` | `live_verified` | Core UI tree manipulation |
| `ui.event_list_get` / `ui.event_list_grow` | `ui` | `0x14019b2b0` / `0x140152430` | `0x19b2b0` / `0x152430` | `code` | `live_verified` | Control event handler lists (`button+0x2a0`) |
| `files.text_stream_open` | `files` | `0x1421d5b50` | `0x21d5b50` | `code` | `live_verified` | Route A text overlay hook (`.ui`, `.mat`) |
| `files.read_whole_file` | `files` | `0x140e564d0` | `0xe564d0` | `code` | `static_verified` | Consults bundle index at `fs+0xc900` |
| `files.hot_reload_invalidate_ext` | `files` | `0x141010560` | `0x1010560` | `code` | `static_verified` | Dormant hot-reload per-extension invalidator |
| `files.variant_tag_wstring` | `files` | `0x1444a6930` | `0x44a6930` | `data` | `static_verified` | 3.29.3d data only (`unverified_3_29_3c` on 3.29.3c) |
| `rulesets.creator_table` | `rulesets` | `0x14475dc70` | `0x475dc70` | `data` | `static_verified` | Writable array (`0xaf` slots: **68 occupied incl. `0xA0`, 107 free incl. `0xAE`**; 3.29.3d data only) |
| `rulesets.create` | `rulesets` | `0x1415a6800` | `0x15a6800` | `code` | `static_verified` | Instantiates ruleset by ID (`client+0xcfd8`) |
| `rulesets.s2c_0x2c9_filter` | `rulesets` | `0x1415a6b50` | `0x15a6b50` | `code` | `static_verified` | Routes S→C `0x2c9` to slot 8; silently drops unknown IDs |
| `rulesets.c2s_0x2ca_writer` / `rulesets.c2s_0x2ca_sender` | `rulesets` | `0x141fa7c10` / `0x1421a1960` | `0x1fa7c10` / `0x21a1960` | `code` | `static_verified` | C→S `0x2ca` writer & sender |
| `network.message_pump` / `network.register_handler_group` | `network` | `0x1429a77a0` / `0x1429a7710` | `0x29a77a0` / `0x29a7710` | `code` | `static_verified` | Reserved opcodes `>= 0x2fc` |
| `network.socket_write` / `network.socket_flush` | `network` | `0x140dfa000` / `0x140dfa160` | `0xdfa000` / `0xdfa160` | `code` | `live_verified` | Acquire SRW `socket+0x248` for write; never hold for flush |
| `network.chat_panel_listener` | `network` | `0x14017a270` | `0x17a270` | `code` | `live_verified` | S4 fallback intercepting `##NM ` S→C `0x17` lines |
| `network.app_global_ref` | `network` | `0x1400f3b30` | `0xf3b30` | `global_ref` | `live_verified` | Pattern `488b05????????488907488b05` resolving `network.app_global` |
| `geal.compiler_statements` / `geal.interpreter` | `geal` | `0x141df8210` / `0x141df0d70` | `0x1df8210` / `0x1df0d70` | `code` | `static_verified` | GEAL VM compiler & interpreter |
| `geal.run_script` / `geal.run_at_offset` | `geal` | `0x141df8040` / `0x141df8160` | `0x1df8040` / `0x1df8160` | `code` | `static_verified` | External GEAL execution entries |
| `geal.builtin_factory_table` / `geal.builtin_register_entry` | `geal` | `0x1445c50f8` / `0x141de9780` | `0x45c50f8` / `0x1de9780` | `data` / `code` | `live_verified` | 724 client-callable built-ins; **`GealBuiltinEntry` MUST be `alignas(16)` for `movaps` at `0x141de9858`** |
| `ui.control_factory_map_global` / `ui.parser_parse_property` | `ui` | `0x14475e990` / `0x141872270` | `0x475e990` / `0x1872270` | `data` / `code` | `live_verified` | Phase 4 live `IUiControlRegistry` map (47 stock controls) & custom `.ui` property hook |
| `ui.item_popup_build_sections` / `ui.stash_get_affinity_targets` / `items.compute_affinity_bitset` | `ui`/`items` | `0x1416c5740` / `0x140a83460` / `0x141589b20` | `0x16c5740` / `0xa83460` / `0x1589b20` | `code` | `live_verified` | Phase 4 `ITooltipService` & `IStashAffinityService` live `TargetChain` hooks (`C→S 0x00E1`) |
| `allocator.alloc` / `allocator.free` | `allocator` | `0x142c8cf60` / `0x142c8ca30` | `0x2c8cf60` / `0x2c8ca30` | `code` | `live_verified` / `static_verified` | Slab allocator with 16-byte prefix header |
| `runtime.wstring_assign` | `runtime` | `0x140144010` | `0x144010` | `code` | `live_verified` | MSVC `std::wstring::assign` |
| `runtime.cxx_throw_exception` / `runtime.deadlock_detector` | `runtime` | `0x142ce6a40` / `0x140e3ed10` | `0x2ce6a40` / `0xe3ed10` | `code` | `live_verified` / `static_verified` | `_CxxThrowException` (`0xE06D7363`) & main-thread watchdog |
| `audio.submit_request` / `stats.set_local` / `world.create_client_object` | `audio`/`stats`/`world` | `0x140ff86e0` / `0x1424d65c0` / `0x141ec47e0` | `0xff86e0` / `0x24d65c0` / `0x1ec47e0` | `code` | `static_verified` | Audio, display-only stats, client-only object creation |

---

## 4. Verified Protocol & Engine Constants (`constants{}`)

| Constant | Value | Meaning |
|---|---|---|
| `rulesetS2cOpcode` / `rulesetC2sOpcode` | `"0x2c9"` / `"0x2ca"` | S→C and C→S ruleset opcodes |
| `rulesetMaxPayload` | `65535` (`0xffff`) | Max ruleset update payload before "Ruleset protocol violation" |
| `rulesetSlotCount` / `rulesetFilledSlotCount` | `175` (`0xaf`) / `68` | Total / occupied slots in `rulesets.creator_table` |
| `rulesetVtableSlotCount` / `rulesetBaseSize` | `9` / `24` (`0x18`) | Ruleset base vtable slot count and base object size |
| `reservedOpcodeMin` | `"0x2fc"` | First free opcode above the 3.29.3d dispatcher limit (`0x2fb`) |
| `chatS2cOpcode` / `chatC2sOpcode` | `"0x17"` / `"0x15"` | Chat fallback opcodes (`chatFallbackPrefix`: `"##NM "`) |
| `gealClientBuiltinCount` / `gealWhileIterationCap` | `724` / `1000` | Verified client-callable GEAL built-ins and `While` cap |
| `bundleHashSeed` / `bundleBlockSize` | `"0x1337b33f"` / `262144` (`0x40000`) | Murmur64 path hash seed and bundle chunk size |
| `oodleStoredBlockMagic` | `"cc06"` | Uncompressed Oodle stored-block header bytes |
| `msvcCxxExceptionCode` | `"0xe06d7363"` | MSVC C++ SEH exception code |
| `peHeaderSlackBytes` | `136` | Slack bytes after section table in `Game.exe` |

---

## 5. Expected-Byte Safety Checks

Before installing any inline detour, the native shim calls `nm_verify_bytes(runtimeAddr, expectedHex)` (and `AddressLibraryCatalog.MatchByVerifiedCodeSignatures` checks 6 core anchors to identify LocalExile key-patched `Game.patched.exe` builds):

1. Every fixed hex byte pair is compared against live memory at `imageBase + rva`.
2. `??` wildcards skip relocatable RIP-relative displacements (e.g., `network.app_global_ref`: `488b05????????488907488b05`).
3. If any fixed byte mismatches (`NM_ERR_BYTE_MISMATCH`) — or if `rva` is `null` / `NM_SYM_STATUS_UNVERIFIED` (`NM_ERR_SYMBOL_UNVERIFIED`) — hook installation fails cleanly without modifying game memory.

---

## 6. Workflow for Adding New Reverse-Engineered Symbols

1. **Derive on 3.29.3d** via Ghidra / `objdump` / live validation; compute `rva = va - 0x140000000`.
2. **Extract real prologue bytes** directly from an unpatched 3.29.3d executable (using `??` for relocatable RIP-relative offsets). Never fabricate bytes.
3. **Add the entry to `data/addresses/3.29.3d.json`** matching `address-library.schema.json` with accurate `category`, `kind`, `callingConvention`, `signature`, `status`, and `source`.
4. **Update `data/addresses/3.29.3c.json`**:
   - For `kind: "code"` verified to match 3.29.3c, record the shared RVA with `status: "code_shared_3_29_3c"`.
   - For `kind: "data"`, `"vtable"`, or `"import_slot"` not yet verified on 3.29.3c, set `"rva": null`, `"va": null`, `"expectedBytes": null`, and `"status": "unverified_3_29_3c"`.
5. **Validate against schema and tests:**
   ```bash
   dotnet test tests/LocalExile.Client.Patcher.Tests
   dotnet test tests/LocalExile.Client.Tests
   ```
6. **Never commit any game binary or extracted GGG asset** to the repository.
