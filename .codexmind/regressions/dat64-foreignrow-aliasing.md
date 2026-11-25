# Regression: `.datc64` `foreignrow[]` Variable-Section Offset Aliasing Crash (`Game.patched.exe + 0x129838`)

## Symptom
When appending a brand-new row to `Data/Pet.datc64` via `CloneRow(0)` and serving the patched table through Route A `FileSystem` overlays on Path of Exile 3.29.3d (`build 328228`), the client boots to login and character select cleanly, then crashes on **town entry (`1_1_town`)** with `EXCEPTION_ACCESS_VIOLATION` reading `[0x0 + 0x28]` at `Game.patched.exe + 0x00129838` (`mov 0x28(%rdi), %rax`; callers `0x140913ef9` -> `0x140914232`), diagnosed via `WINEDEBUG=+seh` + disassembly.

## Root Cause
PoE 3.29's `.datc64` loader resolves `foreignrow[]` array elements **in-place inside the loaded variable-data section**, rewriting each 16-byte `{u64 row_index, u64 0}` pair into `{void* row_ptr, void* target_table_ptr}` as it iterates through every row of the table.

Because `Dat64TablePatcher.CloneRow(0)` copies the template row's fixed bytes verbatim, `Pet[516].__col7` (column offset `36`, `foreignrow[]` of `count=1`) inherited `Pet[0]`'s exact variable-section byte offset `0x3a` (`58`). Across all `516` stock rows of `Pet.datc64`, **zero** rows ever alias a `foreignrow[]` variable-section offset. When the loader processed row `0`, it resolved the entry at `var + 0x3a` in-place to `{row_ptr, table_ptr}`; when it later reached appended row `516`, it treated the already-resolved pointer pair at `var + 0x3a` as a raw `row_index`, failed bounds validation, and wrote `table_ptr = NULL`, causing `0x140129838` to dereference `[NULL + 0x28]` during world/cosmetics initialization.

Additionally, `MtxTypes.__col18` (`offset 137 / 0x89`, UTF-16LE string) is strictly unique across all `6,597 / 6,597` non-empty stock rows (`"Kiwi1"` in template row `5730`) and must also be overwritten with a unique short identifier (`"MaxwellPet"`).

## Fix & Invariant
Fixed in `src/LocalExile.Client.AssetPipeline/Dat64/MaxwellDatPatcher.cs` (`105ebcf`) and covered by unit tests in `tests/LocalExile.Client.AssetPipeline.Tests/Dat64TablePatcherTests.cs`:

1. **Never alias any `foreignrow[]` variable-section offset across rows in a patched `.datc64` table.** Whenever `CloneRow(templateRow)` is used on any table containing `foreignrow[]` columns (e.g., `Pet.__col7` at offset `36` and `Pet.Skills` at offset `68`), deep-copy the elements into freshly allocated variable-section offsets via `Dat64TablePatcher.SetForeignRowArray(...)`.
2. **Enforce unique string keys** on cloned `MtxTypes` rows (`MtxTypes.__col18` at offset `137`, verified unique across all `6,598 / 6,598` non-empty rows).
3. **Assert non-aliasing in verification:** `MaxwellDatPatcher.VerifyAppendedPetMtx` explicitly checks that `Pet[newRow].__col7` does not alias the template row's variable-section offset and throws on collision.
