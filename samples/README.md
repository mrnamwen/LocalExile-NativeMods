# Samples

Evidence behind [01-findings.md](../01-findings.md), produced read-only on 2026-11-18 from the 3.29.3d client.

| File | What |
|---|---|
| `ui-paths.txt` | every `.ui` and `metadata/ui/` path in the bundle index (124 `.ui`, 16 `.xml`) |
| `registered-controls.txt` | control types with a `UI::Controls::Detail::Register<T>` instantiation in the executable's RTTI |
| `client-rulesets.txt` | `Game::Rulesets::Client*` classes in the RTTI |
| `pex.py` | stdlib-only PE helper: `xref <string>…`, `callers <va>`, `fn <va>` (the path to `Client/Game.exe` is at the top) |

No game file is stored here. To read a stock `.ui` file, extract it from your own client:

    dotnet run --project scripts/re/ggdump -- Client <outDir> metadata/ui/ingamestate/confirmationwindow.ui
    iconv -f UTF-16 -t UTF-8 <outDir>/metadata_ui_ingamestate_confirmationwindow.ui
