# DualMod.SkipCampaign — Opt-In Skip Campaign Showcase

Wave 3 Phase 1 (`0.3.0`) pre-game showcase demonstrating conflict-free **Pre-Game UI injection** (`ScreenSlot.LeagueCardModes` + `ScreenSlot.CreateCharacterBar`), **`C->S 0x12` (`CreateCharacter.S78`) option tagging** via `IPreGameService`, **Auth -> Gateway `GameEvents.CharacterCreated` handoff** with strict league/SSF/HC-death isolation, **idempotent first-enter `IPlayerWorld` progression**, and **HUD toast feedback** via `IToastService`.

## End-to-End Flow

1. **League Selection (`LeagueOverlay` / `0xdc35` cards):**
   - `SkipCampaignClientMod` registers a `"Skip Campaign"` mode checkbox in `ScreenSlot.LeagueCardModes`.
   - Whenever `LeagueOverlay::Fill` rebuilds league cards, NativeMods automatically re-injects the checkbox alongside Hardcore / SSF / Ruthless, equalizing widths and compacting spacing within the `500x905` card budget while preserving checked state per league.
2. **Create Character Screen (`ScreenSlot.CreateCharacterBar`):**
   - A synchronized mirror checkbox (`CreateCharacterSkipBar.ui`) mounts in `ScreenSlot.CreateCharacterBar` so players still see and toggle **Skip Campaign** when a Character Select league filter bypasses auto-opening `LeagueOverlay`.
3. **Character Creation (`C->S 0x12` / `S->C 0x13`):**
   - When the player clicks Create with Skip Campaign checked, `IPreGameService.CharacterCreating` attaches `skip_campaign=1` onto `CreateCharacter.S78` (for example `"DexDefault|nm:skip_campaign=1"`), preserving 100% compatibility with unmodified servers via `StartStatePrefixes`.
   - On `IPreGameService.CharacterCreated`:
     - **Success (`ResultCode == 0`):** resets the toggle so the next character defaults to normal campaign start.
     - **Failure (`ResultCode != 0`, e.g. name already taken):** preserves the toggle so the player does not have to re-check it.
4. **Server Auth -> Gateway Handoff (`GameEvents.CharacterCreated`):**
   - `LoginServerSession.ParseStartState` parses `|nm:skip_campaign=1` and forwards `character_created` over localhost `InstanceControl` to `GameServer.Gateway` **before** `S->C 0x15` / `0x13` and `InstanceDirectory.AssignStart`.
   - `SkipCampaignHandler` verifies `SkipCampaignSettings.Enabled` and (when `RequireLeagueCompletion` is true) checks `CharacterSaves.GetLeagueCharacters(account, league, includeDead: false)` for a living campaign-completed character in that exact league (strictly enforcing league, SSF, and Hardcore death isolation).
   - If eligible, it sets `StartingAreaId = "2_11_endgame_town"` (Karui Shores), `StartingLevel = 68`, and `SuppressStartKit = true` (zero Twilight Strand flash, zero starter-kit leakage) and queues a one-time post-entry grant.
5. **First In-Game Arrival (`IPlayerWorld` + `ServerBridge` HUD Toast):**
   - On first `PlayerEntered`, `SkipCampaignHandler.OnFirstEnterAsync` applies campaign quest flags via `SetQuestFlagsAsync` (clearing `UnearnedQuestFlags` so Act 5 & Act 10 Kitava `-60%` resistance penalties, Pantheon access, and `EndGameAvailable` take effect), grants Acts 1–10 + Karui Shores waypoints via `GrantWaypointsAsync`, sets refund points via `SetRefundPointsAsync`, optionally awards Normal/Cruel/Merciless Labyrinth ascendancy points via `AwardAscendancyAsync`, credits starting gold via `AddGoldAsync`, and marks the character finalized in both memory and `IPlayerWorld.CharacterData` (strictly idempotent across relogs and restarts).
   - `SkipCampaignServerMod` sends `SkipCampaignAppliedNotice` over `ServerBridge` (queued until `OnClientReady` if the client handshake finishes after `PlayerEntered`) and `SkipCampaignClientMod` displays a green HUD toast via `game.Toasts.Show(...)`.

## Project Layout

- `Shared/SkipCampaignContract.cs` — option key (`"skip_campaign"`), `SkipCampaignSettings`, default Kitava penalty flags & campaign waypoints, and `SkipCampaignAppliedNotice`.
- `Client/SkipCampaignClientMod.cs` — `IClientMod` targeting `LocalExile.Client.Sdk` `0.3.0`.
- `client/Metadata/UI/Mods/skip-campaign/CreateCharacterSkipBar.ui` — stock 3.29.3d `.ui` layout for `ScreenSlot.CreateCharacterBar`.
- `Server/SkipCampaignServerMod.cs` — testable `SkipCampaignHandler` + `SkipCampaignServerMod` (`ServerBridge` notice dispatch).
- `Server/SkipCampaignLocalExileEntry.cs` — LocalExile Gateway Roslyn `IMod` entry point wiring `GameEvents.CharacterCreated`, `CharacterSaves.GetLeagueCharacters`, `LocalExileGatewayHost`, and `IPlayerWorld`.
