# ServerBridge, Networking & Extender Registries

Wire bidirectional typed networking between LocalExile Gateway server mods and 3.29 client mods using **`LocalExile.Client.ServerBridge` (`1.2.0`)**, drive native client features from single-`.cs` Roslyn server mods with Tier 1 `IClientPhase1Session` / `IClientPhase2Session` / `IClientPhase4Session` (tooltips, stash affinity, rich UI / `IUiItemGrid`, 3D spatial audio, world VFX, and opt-in `u32` stacks), verify multi-mod `content-row-map.json` digests at handshake, register custom items/uniques/drop-pool/monster/NPC/buff overlays via LocalExile Part 2b & 2c, perform atomic Gateway item/affix transactions, and register custom GEAL built-ins, rulesets, opcodes, and raw hooks.

[← Documentation Home](../README.md) · [ServerBridge API Reference](../api/server-bridge.md) · [SDK Net, Protocol, Extend & Raw API Reference](../api/sdk-net-files-raw-extend.md)

---

## 1. Wire Transports: `HostRuleset` vs. `ChatFallback`

NativeMods negotiates its wire transport automatically via `ClientHandshake` / `ServerHandshake` (on live `NativeShimBridge`, `ClientHostRuntime.OnFrame` automatically sends `ClientHandshake` starting at frame `30` and retries every `2.0 s` wall-clock until `LastServerHandshake` arrives):

| Transport | Wire Framing | Max Payload | Vanilla Client Safety |
|---|---|---|---|
| **`HostRuleset`** *(Primary)* | S→C `0x02C9` / C→S `0x02CA`: `[u8 rulesetId = 0xA0][CompactUInt length][NMOD envelope]` (PoE big-endian `CompactUInt`: 1B `<=0x7F`, 2B `<=0x3FFF`, 3B `<=0xFFFF`) | `65,535` bytes | **100% safe** — stock unmodded 3.29 clients silently drop `0x02C9` for unknown ruleset IDs (never disconnect). |
| **`ChatFallback`** *(Fallback)* | S→C `##NM env <base64>` / C→S `/nm env <base64>` over chat (`0x0017` / `0x0015`) | Chat message limits | Requires zero custom packet hooks on the server. |

### Shared Typed Records (`Shared/MyMessages.cs`)

```csharp
public sealed record ToggleEffectRequest(bool Enabled, float Intensity);
public sealed record EffectStateSync(bool Enabled, float Intensity, string Status);
```

---

## 2. Gateway Server Side (`LocalExileGatewayHost` & `ClientBridgeHost`)

Use **`LocalExileGatewayHost.Create(...)`** to eliminate boilerplate sink/bus/`0x02CA`/`/nm` wiring and safely share `GatewayBridgeBus` across multiple dual-side mods on the same LocalExile server.

> **Qualification Rule:** Always qualify **`global::Client.For(player)`** when referencing LocalExile's core `Client` class in files that import `LocalExile.Client.ServerBridge` (which defines `LocalExile.Client.ServerBridge.Client`).

```csharp
using LocalExile.Client.ServerBridge;

using var gatewayHost = LocalExileGatewayHost.Create(modContext, builder => builder
    .WithBridgeOptions(new ClientBridgeOptions
    {
        ServerVersion = "LocalExile-3.29.3d",
        HostRulesetId = IRulesetRegistry.DefaultHostChannelRulesetId // 0xA0
    })
    .WithBus(attach: true, onDispatchError: (owner, ex) => modContext.Log($"[bus] {owner}: {ex.Message}"))
    .OnCosmeticChanged(async evt =>
    {
        if (!evt.IsSkillCosmetic)
            await ReconcileCosmeticAsync(evt);
    }));

ClientBridgeHost bridge = gatewayHost.Bridge; // DefaultChannel defaults to mod id

// Async C->S handler (channel-less or explicit channel; zero .GetAwaiter().GetResult()!):
IDisposable sub = bridge.On<ToggleEffectRequest>(async (session, req, ct) =>
{
    if (!session.HasClientMod("my-feature"))
        return;

    var sync = new EffectStateSync(req.Enabled, req.Intensity, "Updated!");
    await session.SendAsync(sync, cancellationToken: ct); // or TrySendAsync(...)
});

// Async ClientReady notification:
IDisposable readySub = bridge.OnClientReady(async (session, ct) =>
{
    if (session.HasClientMod("my-feature"))
        await session.TrySendAsync(new EffectStateSync(true, 1.0f, "Connected"), cancellationToken: ct);
});

// Graceful degradation to vanilla chat in a slash command handler:
gatewayHost.RegisterCommand("myfeature", "/myfeature", async (ctx, args) =>
{
    IClientSession session = bridge.For(ctx.ConnectionId);
    if (session.HasNativeHost && session.HasClientMod("my-feature"))
    {
        await session.SendAsync(new EffectStateSync(true, 1.0f, "Opened panel"));
        return;
    }

    await global::Client.For((Player)ctx.Player).SendChat("[MyFeature — Chat Fallback] Effect enabled.");
});
```

---

## 3. Server-Driven Native UI (`IClientUiSession`, `Window.Closed` & `BatchAsync`)

Server-only or dual-side mods can open and mutate native `.ui` windows directly from the server, coalescing multiple updates into a single ordered wire batch:

```csharp
IClientSession session = bridge.For(connectionId);
if (session.HasNativeHost)
{
    ServerUiWindowHandle win = await session.Ui.OpenAsync("Metadata/UI/Mods/MyServerMod/Panel.ui", "my_win");
    win.OnClosed(_ => { /* cleanup when player or session closes window */ });

    // Coalesce/flush multiple UI mutations in one ordered wire batch:
    await win.BatchAsync(async () =>
    {
        await win.SetTextAsync("status_label", "Driven straight from the server!");
        await win.SetVisibleAsync("status_label", true);
    });

    win.OnButtonClickAsync("apply_button", async (s, ct) =>
    {
        await win.SetTextAsync("status_label", "Server received button click!", ct);
        await s.Ui.PlayAudioAsync("Audio/Mods/MyServerMod/ding.ogg", cancellationToken: ct);
    });
}
```

---

## 4. Tier 1 Server-Only Parity (0.3.0, 0.4.0 and 1.2.0)

Single-`.cs` Roslyn server mods (with **zero custom client DLL**) can drive Phase 1, Phase 2, Phase 4, and Phase 5 client capabilities automatically through `ServerBridgeTier1Handler` (with ref-counted multi-mod subscriptions and automatic disconnect/dispose cleanup):

### Phase 1 (`session.Phase1` — `IClientPhase1Session`)

- `OnSlotButtonClickAsync` — register stock-slot buttons on `ScreenSlot.*`
- `ShowToastAsync` — display HUD toasts on `ScreenSlot.HudTopRight`
- `RegisterHotkeyAsync` / `OnHotkeyAsync` — register persistent client hotkeys
- `OnScreenChangedAsync` / `OnHoverChangedAsync` — subscribe to stock panel and hover state changes

### Phase 2 (`session.Phase2` — `IClientPhase2Session`)

```csharp
using LocalExile.Client.ServerBridge;

// Query live client world, visible ground loot, stats, area, item filter & camera:
IReadOnlyList<ClientWorldEntitySnapshot> loot =
    await session.Phase2.QueryVisibleGroundItemsAsync(maxDistance: 800f, cancellationToken: ct);
ClientEntityStatsSnapshot stats =
    await session.Phase2.QueryEntityStatsAsync(entityId: 0u, cancellationToken: ct);
AreaInfo? area =
    await session.Phase2.QueryAreaInfoAsync(cancellationToken: ct);
ItemFilterState filter =
    await session.Phase2.QueryItemFilterStateAsync(cancellationToken: ct);
CameraState camera =
    await session.Phase2.QueryCameraStateAsync(cancellationToken: ct);

// Opt-in ref-counted area & filter-reload push notifications:
IDisposable areaSub = await session.Phase2.OnAreaChangedAsync((s, ev) =>
    Console.WriteLine($"{s.ConnectionId} {ev.Kind}: {ev.AreaId}"));
IDisposable filterSub = await session.Phase2.OnFilterReloadedAsync((s, ev) =>
    Console.WriteLine($"{s.ConnectionId} reloaded {ev.State.FilterName}"));

// Owner-scoped ("server-bridge") zoom override — restored automatically on disconnect/dispose:
await session.Phase2.SetCameraZoomOverrideAsync(1.25f, cancellationToken: ct);
```

### Phase 4 & 5 (`session.Phase4` — `IClientPhase4Session`, `1.2.0`)

Tier 1 server-only mods can push declarative stock tooltip rules (`tier1.tooltips`), stash affinity routes (`tier1.affinity`), rich dynamic controls & `IUiItemGrid` (`tier1.rich-ui`), 3D spatial audio & world VFX (`tier1.audio-ex`), compact 6-sockets, and opt-in `u32` stacks (`tier1.stacks-u32`) with zero client DLL:

```csharp
// 1. Declarative stock item tooltip augmentation rule & custom Ctrl+Click stash affinity route:
await session.Phase4.RegisterTooltipRuleAsync(new ServerItemTooltipRule(
    Id: "league.alt_tag",
    Sections: [TooltipSection.SingleLine("[Custom League Relic]", rgbaColor: 0x88CCFFFFu)],
    RequireAltHeld: true), ct);
await session.Phase4.RegisterAffinityRouteAsync(new ServerStashAffinityRouteRule(
    Id: "league.fragments", TargetTabIndex: 12, MetadataPrefix: "Metadata/Items/Mods/MyLeague/", ReplaceStockTargets: true), ct);

// 2. Dynamic IUiItemGrid & rich controls driven from the server:
await session.Phase4.CreateItemGridAsync("my_win", "body", "relic_grid", new UiItemGridOptions(6, 6, 48f, 2f), ct);
await session.Phase4.PlaceGridItemAsync("my_win", "relic_grid", new ServerGridItem("relic-1", Col: 0, Row: 0, Width: 2, Height: 2, Title: "Aether Relic"), ct);

// 3. 3D spatial audio, live entity VFX & opt-in u32 stack extension (> 65,535 stacks):
await session.Phase4.PlaySound3DAsync("Audio/Mods/MyLeague/hum.ogg", new WorldPosition3D(100f, 200f, 0f), cancellationToken: ct);
await session.Phase4.PlayWorldEffectAsync(entityId, "Metadata/Effects/Mods/MyLeague/Burst.ao", ct);
await session.Phase4.SetExtendedU32StacksEnabledAsync(enabled: true, formatMillions: true, cancellationToken: ct);
```

---

## 5. Atomic Gateway Item & Affix Transactions

`LocalExileItemAffixService` and `LocalExileGatewayTransactions` (used in `examples/DualMod.SiphonOrb/`) provide safe, deterministic inspection, stealing, storing (`ModSection.Enchant`), imbuing, granting, and custom-naming of items across both live Gateway inventories/stashes and unit-test `PortableItem` models:

```csharp
using LocalExile.Client.ServerBridge;

var affixService = new LocalExileItemAffixService();

// Inspect explicit affixes (formats exact PoE stat_descriptions.txt text):
ItemAffixView view = affixService.InspectGatewayItem(inventoryId, itemId, liveItem, itemCatalog, statDescriptions);
ItemAffixView portableView = LocalExileItemAffixService.Inspect(portableItem);

// Deterministic portable steal / charge / imbue pipeline:
AffixStealResult steal = LocalExileItemAffixService.StealAffix(sourceItem, affixIndex: 0);
PortableItem chargedOrb = LocalExileItemAffixService.CreateChargedCurrencyItem(chargedOrbDef, steal.StoredAffix!);
StoredAffixPayload? stored = LocalExileItemAffixService.ReadStoredAffix(chargedOrb);
AffixReplaceResult imbue = LocalExileItemAffixService.ReplaceAffix(targetItem, targetAffixIndex: 1, stored!);

// Live Gateway atomic transactions & Item.CustomName:
GatewayGrantResult grant = await LocalExileGatewayTransactions.GrantItemsAsync(player, itemCatalog, items, emptyOrb, count: 5);
GatewayItemLocation? loc = LocalExileGatewayTransactions.LocateGatewayItem(items, stash, invId, itemId);
if (loc is { IsWorn: false })
{
    GatewayStealResult gwSteal = await affixService.StealAffixOnGatewayItem(
        player, itemCatalog, items, stash, currencyInvId, currencyItemId, invId, itemId,
        affixIndex: 0, chargedOrbDef, chargedCustomName: null);
    GatewayReplaceResult gwReplace = await affixService.ReplaceAffixOnGatewayItem(
        player, itemCatalog, items, stash, chargedInvId, gwSteal.CreatedChargedItemId, invId, destItemId, replaceIndex: 0);
}
object namedItem = LocalExileItemAffixService.WithGatewayItemCustomName(liveItem, "My Relic");
string? customName = LocalExileItemAffixService.GetGatewayItemCustomName(namedItem);
```

---

## 6. Content-Row-Map Handshake Digest and LocalExile Part 2b Overlays (`1.0.0`)

### `content-row-map.json` Handshake Verification (`ServerContentRowMap`)

When multiple content mods share `.datc64` tables via `DatPatchMerger` (`Mods/Client/.merged-cache/`), bind the merged cache on your server mod so row indices, `HASH32`/`HASH16` IDs, and dead `Stats` stock IDs stay synchronized between client and server:

```csharp
using LocalExile.Client.ServerBridge;

// 1. Load Mods/Client/.merged-cache/content-row-map.json and bind its SHA-256 digest onto ClientBridgeHost:
ServerContentRowMap? rowMap = ServerContentRowMap.TryLoadAndBind(bridge, Path.Combine(localExileRoot, "Mods", "Client", ".merged-cache"));

// 2. React if a connected client has a stale or mismatched .merged-cache/:
bridge.ContentRowMapDigestMismatch += (session, expectedDigest, actualDigest) =>
{
    modContext.Log($"[warn] {session.CharacterName} content-row-map mismatch: expected={expectedDigest}, actual={actualDigest}");
};

// 3. Resolve symbolic keys deterministically on the server:
if (rowMap is not null)
{
    int baseRow = rowMap.RequireRowIndex("BaseItemTypes", "aether_shard");
    uint hash32 = rowMap.RequireHash32("BaseItemTypes", "aether_shard");
    ushort hash16 = rowMap.RequireHash16("ItemVisualIdentity", "aether_shard_visual");
    string deadStatStockId = rowMap.RequireStockId("Stats", "aether_resonance_pct");
}
```

### LocalExile Part 2b & 2c Runtime Catalog, Drop-Pool & Custom-League APIs

LocalExile Core Hooks Part 2b (`dac865f6d`) and Part 2c (`c0392f414` on `feat/nativemods-phase0-core-hooks`) expose lock-free, zero-SQLite-mutation runtime APIs for custom content and full custom league mechanics (all auto-cleaned via `GameEventOwners` and preserved across `/reload`):

- **Part 2b Content & Drops:** `ItemCatalog.RegisterOverlay`, `UniqueCatalog.RegisterOverlay`, `ModCatalog`, `GemSources`, and `DropDataProvider.Default.RegisterOverlay` / `DropPoolOverlays` (lock-free lazy `ItemDrops` rebuild across monster kills, containers, strongboxes, boss bonuses, map currency mods, and `HardModeDrops`).
- **Part 2c Custom-League Mechanics (`c0392f414`):** Area lifecycle events (`GameEvents.AreaInit`, `AreaClosing`, `MonsterKilled`, `PlayerDied`, `ObjectUse`, `MapDeviceActivating`), custom interactable world objects & monster/NPC/buff catalog overlays (`ObjectTypes`, `MonsterCatalog`, `NpcCatalog`, `BuffRows`), custom portals & sub-area / boss-arena instances (`Portals.cs`), custom vendor recipes (`Rules.VendorRecipes.cs`), and league-scoped persistence (`LeagueData` alongside `AccountData`/`CharacterData`) + `IPlayerWorld` encounter facades.
- **Direct Gateway APIs (Roslyn `IMod` entry point, e.g., `examples/DualMod.SiphonOrb/Server/SiphonLocalExileEntry.cs`):** Call `ItemCatalog.RegisterOverlay(...)`, `DropDataProvider.Default.RegisterOverlay(...)`, and Part 2c `GameEvents` / `IPlayerWorld` helpers directly under `mod.Manifest.Id`.
- **Zero-hard-dependency `ServerBridge` reflection helpers (`LocalExileGatewayHost`):** For compiled server DLLs referencing only `LocalExile.Client.ServerBridge`:

```csharp
if (gatewayHost.SupportsCatalogRegistration)
{
    gatewayHost.RegisterCustomBaseItem(new GatewayCustomBaseItemRegistration(
        Id: "Metadata/Items/Currency/Mods/MyMod/AetherShard",
        Name: "Aether Shard",
        ItemClass: "StackableCurrency",
        Row: -1, // Synthetic row allocation (default)
        Width: 1,
        Height: 1,
        MaxStack: 20,
        VisualIdentityId: "AetherShardVisual",
        VisualIdentityHash16: hash16,
        Components: ["Base", "Stack"]));

    gatewayHost.RegisterDropPoolEntry(new GatewayDropPoolEntryRegistration(
        Category: "Currency",
        BaseItemId: "Metadata/Items/Currency/Mods/MyMod/AetherShard",
        Weight: 25.0,
        MinAreaLevel: 45,
        MaxAreaLevel: 0));
}
```

---

## 7. Client-Side Networking, Chat, GEAL, Rulesets, Opcodes & `IRawMemory`

### Typed Client Messaging & Chat Interception (`INetChannel` / `IChatService`)

```csharp
// Subscribe to S->C typed messages (marshal UI mutations onto game thread):
context.TrackDisposable(context.Net.On<EffectStateSync>(sync =>
{
    if (context.Scheduler.IsOnGameThread)
        statusLabel.Text = sync.Status;
    else
        context.Scheduler.RunOnGameThread(() => statusLabel.Text = sync.Status);
}));

// Send typed C->S message (routes collectible-ALC types through CollectibleJsonCodec automatically):
context.Net.Send(new ToggleEffectRequest(Enabled: true, Intensity: 0.85f));

// Raw packet send/receive & S->C chat interception (return true to suppress):
context.TrackDisposable(context.Net.OnRaw(1202, bytes => context.Logger.Debug($"Raw {bytes.Length} B")));
context.Net.SendRaw(1202, [0x01, 0x02]);
context.TrackDisposable(context.Game.Chat.RegisterInterceptor((sender, text) =>
    text.Contains("[SUPPRESS]", StringComparison.Ordinal)));
```

### Custom GEAL Built-Ins, Per-Area Rulesets & Custom Opcodes (Live Native `1.1.0`)

All registries live on `context.Game` and return `IDisposable` handles that **must** be passed to `context.TrackDisposable(...)`. In **`1.1.0`** (C ABI `0.5.0`), `IGealBuiltinRegistry`, `IRulesetRegistry`, and `IUiControlRegistry` are live-wired directly into PoE `3.29.3d`'s native engine tables:

```csharp
// 1. Live Native GEAL Built-In (registers directly into geal.builtin_factory_table via 0x141de9780;
//    declare in mod.json "gealBuiltins": ["IdolBoardCount"])
context.TrackDisposable(context.Game.GealBuiltins.Register("IdolBoardCount", (uint objectId, GealArguments args) =>
{
    int boost = args.Count > 0 ? Math.Clamp(args.Int(0), 1, 8) : 1;
    context.Game.World.PlayEffect(objectId, "Metadata/Pet/Mods/Maxwell/MaxwellPet.ao");
    return GealValue.FromInt(boost);
}));
// Validate .ot GEAL scripts statically: context.Game.Scripts.ValidateScript(source)
// Invoke entity Functions{} entry live: context.Game.Scripts.InvokeFunction(entityId, "OnPulse", ...)

// 2. Live Native Per-Area Ruleset (registers into rulesets.creator_table[0x00..0xAE] via RegisterNative;
//    IMPORTANT: 0xA0 is occupied by a stock ruleset on 3.29.3d and reserved solely for the 0x02C9 filter-hook host transport!
//    Always pick a verified free slot such as IRulesetRegistry.FirstFreeNativeRulesetId (0xAE) or check IsNativeSlotFree(id))
context.TrackDisposable(context.Game.Rulesets.RegisterNative<MyAreaRuleset>());

// 3. Custom Opcode Handler (stock 3.29.3d opcodes end at 0x02FB; custom >= FirstReservedOpcode 0x02FC)
context.TrackDisposable(context.Game.Opcodes.RegisterHandler(
    opcode: 1202,
    handler: payload => context.Logger.Debug($"Received {payload.Length} raw bytes"),
    groupName: "my-feature"));

context.TrackDisposable(context.Game.Opcodes.RegisterStreamHandler(
    minOpcode: 1200,
    maxOpcode: 1209,
    handler: (opcode, stream) =>
    {
        if (stream.TryRead(out uint value))
            context.Logger.Debug($"Opcode 0x{opcode:X4} val={value}");
    },
    groupName: "my-feature"));
```

### Using `IRawMemory` & Native Hooks Safely

Most mods never need `IRawMemory`. If you do:
1. Declare `"rawAccess": true` and a non-empty `"rawAccessJustification"` in `mod.json` (otherwise `context.Raw` throws `RawAccessDeniedException` and Waypoint rejects with `NC008`).
2. Always resolve via `context.Addresses` (`IAddressLibrary`) — never hardcode absolute addresses.
3. Respect the **`HookEngine` `<= 8`-arg chain vs. `> 8`-arg raw detour** rule (see [Capabilities & Limits](capabilities-and-limits.md#8-hookengine-8-arg-chain-limit-vs-raw-detours)).

```csharp
if (context.Addresses.TryResolveAddress("frame.state_stack_pass_a", out nint addr))
{
    byte[] prologue = context.Raw.ReadBytes(addr, 16);
}

// Native calls run inside nm_call_guarded (0xE06D7363 SEH/C++ exception wall):
nint result = context.Raw.Call("audio.get_holder");
NativeCallResult callEx = context.Raw.CallEx(addr, [NativeCallArgument.FromPointer(ptr), NativeCallArgument.FromFloat(1.5f)]);

// Shared multi-mod Pre/Post hook chain (<= 8 Win64 args) & vtable hook:
context.TrackDisposable(context.Raw.InstallHookChain(
    symbolName: "some.verified_symbol",
    phase: HookPhase.Pre,
    callback: ctx => { /* inspect ctx.GetArg(0); optionally ctx.SkipOriginal = true */ },
    priority: 0));
context.TrackDisposable(context.Raw.InstallVtableHook(vtableAddr, vtableSlotIndex: 8, detourTarget: myFnPtr, out nint origSlot));
```

---

## Related API Reference

- [ServerBridge API Reference](../api/server-bridge.md)
- [SDK Networking, Overlays, Protocol, Extend & Raw API Reference](../api/sdk-net-files-raw-extend.md)
- [Address Library Guide](../architecture/address-library.md)
