# Regression & Pitfall Log: Runtime, Proton, ASLR, and DeadlockDetector Hazards

Source: `10-verification.md` §6 and `11-client-sdk.md` §2.

## 1. No System .NET 10 Runtime Available Inside Client Process

- **Pitfall:** Early design (`09-native-mods.md`) assumed the user's machine already has a shared .NET 10 runtime usable by `LocalExile.ClientHost.dll`.
- **Why it breaks:**
  - On Linux, Path of Exile runs as a Windows x64 binary inside a Proton/Wine prefix (`data/wine-prefix`), which cannot load a Linux host's .NET runtime and has no Windows `hostfxr`/`coreclr` installed.
  - On Windows, LocalExile distributes self-contained releases, so users may have no shared .NET 10 desktop/runtime installation.
- **Mitigation:** Ship a private self-contained `win-x64` .NET 10 runtime (`hostfxr.dll`, `coreclr.dll`, managed host) alongside `LocalExile.ClientHost.dll` so both native Windows and Proton load identically.

## 2. `DeadlockDetector` (`0x140e3ed10`) Main-Thread Stall Kills

- **Hazard:** The 3.29 client runs a `DeadlockDetector` (`0x140e3ed10`) that throws if the main game thread stalls beyond its timeout threshold (exact timeout not yet recovered).
- **Mitigations:**
  - Never boot CoreCLR synchronously inside `DllMain` (loader-lock deadlock + startup stall) or block the game thread during runtime initialization.
  - Defer CoreCLR bootstrap to a background worker thread and trigger managed initialization safely on the first frame tick once ready.
  - Configure workstation concurrent GC to keep game-thread pause times minimal.

## 3. CoreCLR VEH vs. MSVC C++ Exceptions (`0xE06D7363`) Under Wine/Proton

- **Hazard:** Path of Exile throws MSVC C++ exceptions (`RaiseException(0xE06D7363)`) as normal control flow. CoreCLR registers a Vectored Exception Handler (VEH) that observes every first-chance exception, and GC thread suspension interacts with Wine threading.
- **Mitigations:**
  - Native shim exception walls catch `0xE06D7363` around all game calls and prevent managed exceptions from ever unwinding across native game frames.
  - Keep managed code execution on game threads confined strictly to bounded bridge callbacks.
  - Fallback plan if spike R1 fails under Proton: compile client mods as NativeAOT DLLs against the identical `LocalExile.Client.Sdk` contract (loses hot reload, preserves SDK).

## 4. `DYNAMIC_BASE` (ASLR), Empty State Stack, & `0.2.0` Threading Scope

- **ASLR (`DYNAMIC_BASE`):** The 3.29 PE32+ executable is relocatable. Never hardcode absolute `0x14xxxxxxx` addresses in the shim; store **RVAs** (`VA - 0x140000000`) and compute `moduleBase + rva` at runtime, verifying expected prologue bytes before installing any detour.
- **Empty State Stack:** The frame hook site (`0x141528fbf` in `StateStack::PassA`) is skipped while the state stack is empty (e.g., during early startup or transitions). The main-thread task queue drain requires a fallback trigger (such as a Win32 timer or secondary tick) so queued tasks cannot starve.
- **`0.2.0` / Tier 5 Game-Thread Scope, Lock & Queue Parity:** `NativeEntryPoint.InitializeForNativeHost` defers `AutoLoadClientMods` onto the main-thread queue (`EnqueueGameThread`) and runs inside `lock (Gate)` (preventing `Shutdown()` races), `EnqueueGameThread` returns `bool` and cleans `_pendingWork` on `NM_ERR_QUEUE_FULL`, `DrainGameThreadQueue(maxItems: 0)` returns `0` without draining or calling native drain on both `NativeShimBridge` and `SimulatedNativeBridge`, and `INativeBridge.IsOnGameThread` tracks `[ThreadStatic] s_inNativeGameFrame` across all native-to-managed game-thread callbacks.
- **`HookEngine` Leaf-Function, Mid-Insn Relocation & C++ Exception Unwind Safety:**
  - `compute_stolen_length` rejects `<14B` leaf/thunk targets terminating early (`ret`, `int3`, `ud2`, unconditional `jmp`) unless an earlier conditional branch skips past the terminator; `relocate_instructions` rejects mid-instruction internal jump targets.
  - `build_dispatcher_stub` registers DWARF `.eh_frame` CIE+FDE (`__register_frame`/`__deregister_frame` on SysV) and Win64 `RUNTIME_FUNCTION`+`UNWIND_INFO` (`RtlAddFunctionTable`/`RtlDeleteFunctionTable` on MinGW/Win64) + `ActiveCallGuard` + `nm_hook_invoke_original` CFI/SEH wrapper so C++ exceptions unwind cleanly through chained hooks and decrement `active_calls` without `std::terminate()` or retired-chain leaks.
- **`HookEngine` `> 8`-Arg Win64 ABI Limit & Multi-Chain `expectedBytes` Tail Verification (Phase 2 Live Proton `#85`/`#88`, `506561e`):**
  - **`> 8` Win64 Arguments Require Raw Typed `install_inline`:** `HookEngine`'s generic `install_inline_chain` dispatcher (`build_dispatcher_stub` + `nm_hook_invoke_original`) only forwards `8` arguments (`args[0..7]`). Hooking a function taking `> 8` Win64 arguments — such as 10-arg `world.client_world_init_area` (`0x141653ee0`, `rcx/rdx/r8/r9` + 6 stack args `arg4..arg9`) — via `install_inline_chain` drops `arg8`/`arg9`, feeding stack garbage and causing an immediate `STATUS_ACCESS_VIOLATION` (`0xC0000005`) on area entry. Always inspect `.pdata`/stack-arg count before choosing a chain hook, and use a typed raw `hooks.install_inline` detour forwarding all arguments (`a0..a9`) whenever a target takes `> 8` arguments.
  - **Multi-Chain `expectedBytes` Longer Than `stolen_len`:** When a second chain hook attaches to an already-chained target whose `original_bytes.size()` (`S`, e.g. `15 B` on `frame.ingame_state_leave` `0xd54160`) is shorter than `expectedBytes` (`N`, e.g. `16 B`), rejecting `N > S` falsely returns `NM_ERR_BYTE_MISMATCH (-6)`. Fixed in `HookEngine::get_or_create_inline_chain_locked` (`506561e`) to verify `[0 .. min(N, S)-1]` against `chain->original_bytes` and any remaining tail bytes `[S .. N-1]` directly against live unpatched memory at `target_address + S` (covered by `test_multichain_expected_bytes_longer_than_stolen` & `test_ten_arg_raw_inline_detour`, `28/28` C++20 shim tests).
- **Test-Suite Parallelism (Resolved in M7):** `tests/LocalExile.Client.Tests/AddressLibraryTests.cs` now carries `[Collection("NativeEntryPointSerial")]` alongside `Host020AndReliabilityTests`, eliminating concurrent `NativeEntryPoint.ActiveRuntime` test races.
- **Proxy DLL under Wine:** If falling back to a `version.dll` proxy instead of import-table patching, Wine ignores local `version.dll` unless `WINEDLLOVERRIDES=version=n,b` is set in the launch environment.

## 5. Phase 4 (`1.1.0` / C ABI `0.5.0`) Native ABI & Engine Registry Hazards

- **`GealBuiltinEntry` 16-Byte Alignment (`movaps` `#GP`, Live Proton `#144`, fixed `e05029d`):**
  - **Hazard:** `geal.builtin_register_entry` (`0x141de9780`) loads each 16-byte `{ const wchar_t* name; FactoryFn factory; }` entry via aligned SSE **`movaps xmm0, XMMWORD PTR [rdi]`** at `0x141de9858` and `0x141de9870`. Packing `GealBuiltinEntry` at offset `+0x08` inside an unaligned `RegCtx` struct (`{ uint64_t reg_addr; GealBuiltinEntry entry; }`) left `&entry` 8-byte aligned, causing `#GP` (`0xC0000005`, caught by SEH as `NM_ERR_GAME_EXCEPTION = -10`) on live `3.29.3d` when `harvest-garden` (`GardenRipen`) and `maxwell` (`MaxwellDance`) registered built-ins.
  - **Mitigation:** Mark `struct alignas(16) GealBuiltinEntry` (`sizeof == 16`, `alignof == 16`), place `entry` at offset `0` in `struct alignas(16) GealRegisterCtx` (verified with `static_assert`s), and apply `alignas(16)` to all game-bound structs passed into `geal.*`/`ui.*`/`components.*` (`ImageHandle16`, `GealCompiledArgSlot`, stack `MsvcWString` / `MsvcStdString` / `MsvcStdFunc64`). Regression-tested in `test_phase4_geal_builtin_register_alignment` (executes real `movaps (%0), %%xmm0`).
- **Exact 7-Slot MSVC `std::_Func_base` Vtable Order (`#139`, fixed `9378514`):**
  - **Hazard:** MSVC VS2019 `std::_Func_base` (`0x143031688` in `Game.exe` 3.29.3d) defines **7** virtual slots in exact order: `[0]=_Copy`, `[1]=_Move`, `[2]=_Do_call`, **`[3]=_Type`**, **`[4]=_Delete_this`**, **`[5]=_Get`**, **`[6]=~_Func_base(flags)`**. Putting `_Delete_this` at slot 3 or omitting slot 6 corrupts `std::function` teardown on `g_uiControlFactoryMap` erase or GEAL script unload.
  - **Mitigation:** All three shim functor vtables (`g_geal_void_impl_vtable[7]`, `g_geal_value_impl_vtable[7]`, `g_custom_ui_impl_vtable[7]`) strictly match the 7-slot VS2019 order, and live GEAL `_Func_impl` handles live permanently inside `LocalExile.ClientHost.dll` with an atomic `builtinId` indirection so previously compiled `.ot` scripts safely no-op after mod unload.
- **`rulesets.creator_table[0xA0]` Stock Collision (`#128`/`#132`):**
  - **Hazard:** On `3.29.3d`, `rulesets.creator_table[0xA0]` (`0x14475e170`) is **OCCUPIED** by a stock ruleset (static init `0x1417581aa` → creator `0x141763a10`, ctor `0x141765fe0`, size `0x30`, vtable `0x14348ba50`). Overwriting `creator_table[0xA0]` corrupts a stock game ruleset.
  - **Mitigation:** Keep `IRulesetRegistry.DefaultHostChannelRulesetId = 0xA0` strictly for the pre-dispatch `0x02C9` filter hook (preserving `ServerBridge` / `LocalExile` backward compatibility), refuse to overwrite any non-null `rulesets.creator_table[id]` slot (`NM_ERR_ALREADY_EXISTS = -16`), and expose `IRulesetRegistry.FirstFreeNativeRulesetId = 0xAE` (`NM_RULESET_FIRST_FREE_NATIVE_ID = 0xAEu`) + `IsNativeSlotFree(byte)` encoding the exact 3.29.3d census (`68` occupied / `107` free across `0x00..0xAE`).
