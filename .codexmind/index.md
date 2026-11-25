# NativeMods `.codexmind` Index

- `research/client-3.29-findings.md`: Distilled verified reverse-engineering findings for Path of Exile 3.29.3d / 3.29.3c (addresses, ABI, file overlay, message pump, rulesets, GEAL VM, content tables, PE layout, and runtime hazards).
- `research/r1-proton-results.md`: Empirical Spike R1 live Proton (`GE-Proton11-14`) results retiring Risk R1 (.NET 10 CoreCLR off-thread boot in 215–557 ms, both `version.dll` proxy and `.nmimp` import-patch modes, 900+ frame soaks, VEH `0xE06D7363` coexistence, `PassA` thread migration, and isolated `Xvfb` setup).
- `memory/architecture-decisions.md`: NativeMods PoC architecture decisions, dual-side mod model, Waypoint integration, C ABI & SDK design, and build/test setup.
- `regressions/ui-parser-and-layout-pitfalls.md`: Verified `.ui` parser crash conditions, layout infinite-spin traps, and mandatory linter/exception-wall mitigations from spike testing (`07-spike-results.md`).
- `regressions/runtime-proton-and-deadlock-hazards.md`: Known Win64/Proton CoreCLR hosting hazards, DeadlockDetector stalls, ASLR rebasing, and empty state-stack frame-hook gaps (`10-verification.md`, `11-client-sdk.md`).
- `regressions/build-drift-and-refuted-assumptions.md`: 3.29.3c vs. 3.29.3d data-address drift and every load-bearing claim from `08`/`09` refuted or qualified by `10-verification.md`.
- `regressions/collectible-alc-stj-pinning.md`: .NET 10 `System.Text.Json` `JsonSerializerOptionsUpdateHandler` pinning of collectible `AssemblyLoadContext`s and the `CollectibleJsonCodec` fix.
