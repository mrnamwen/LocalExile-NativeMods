# NativeMods Client SDK & Server Bridge API Reference

This reference documents the public modding surface in **`LocalExile.Client.Sdk`** (`SdkVersion 0.1.0-poc`, `ProtocolVersion = 1`) and the Gateway-side **`LocalExile.Client.ServerBridge`** library.

Client mods reference **only** `LocalExile.Client.Sdk` — zero dependency on `LocalExile.Client.Host`.

---

## 1. Core Contracts (`LocalExile.Client.Sdk`)

### `SdkVersion`

| Member | Type | Value / Purpose |
|---|---|---|
| `Current` | `string` | `"0.1.0-poc"` |
| `ProtocolVersion` | `int` | `1` |
| `Parsed` | `Version` | `new Version(0, 1, 0)` |
| `IsCompatible(string? versionRange)` | `bool` | Evaluates `*`, `any`, exact versions, `^x.y.z`, `~x.y.z`, and comparators `>=`, `<=`, `>`, `<`, `=`. |

### `IClientMod`

```csharp
public interface IClientMod
{
    void Load(IClientContext context);
    void Unload() { }
}
```

- `Load(IClientContext context)` runs once on the game thread when loaded (and after every hot reload inside a fresh collectible `AssemblyLoadContext`).
- `Unload()` runs before the host disposes tracked resources and unloads the mod's load context.

### `ClientModManifest`

Parsed via `ClientModManifest.Parse(string json)` or `ClientModManifest.LoadFromFile(string path)` (supports both standalone flat manifests and LocalExile dual-side `mod.json` files with a nested `nativeClient` block):

| Property | Type | Default / Notes |
|---|---|---|
| `Id`, `Name`, `Version` | `string` | Required `Id`; `Name` defaults to `Id`, `Version` to `"0.0.0"`. |
| `Author`, `Description` | `string` | Optional metadata (`""`). |
| `SdkVersionRange` | `string` | Default `"*"`. |
| `Assembly`, `PayloadDir`, `PayloadHash` | `string?` | Client DLL filename, relative asset folder, optional SHA-256 digest. |
| `Depends`, `Replaces` | `IReadOnlyList<string>?` | Non-null helpers: `EffectiveDepends`, `EffectiveReplaces`. |
| `RawAccess`, `RawAccessJustification` | `bool` / `string?` | Gates `IClientContext.Raw` (`RawAccessDeniedException` if `false`). |
| `Enabled` | `bool` | Default `true`. |

### `IClientContext`, `IModLogger`, `IModScheduler`, `IGameServices`

| Member on `IClientContext` | Type | Purpose |
|---|---|---|
| `Manifest` | `ClientModManifest` | Parsed manifest for this mod. |
| `ModDirectory` | `string` | Root directory on disk (`Mods/Client/<id>/`). |
| `SdkVersion` | `string` | Active Client SDK version string. |
| `Logger` | `IModLogger` | `Debug(msg)`, `Info(msg)`, `Warn(msg)`, `Error(msg, Exception? = null)`. |
| `Scheduler` | `IModScheduler` | `IsOnGameThread`, `RunOnGameThread(Action)`, `RunOnGameThreadAsync(Action, ct)`, `RunOnGameThreadAsync<T>(Func<T>, ct)`. |
| `Addresses` | `IAddressLibrary` | Build-matched symbol resolver for 3.29.3c / 3.29.3d. |
| `Game` | `IGameServices` | Aggregate of all Engine services, Extend registries, and `event Action<float>? Frame`. |
| `Net` | `INetChannel` | Scoped bidirectional channel to the mod's server half. |
| `Raw` | `IRawMemory` | Low-level memory/hook surface (**throws `RawAccessDeniedException`** unless `Manifest.RawAccess == true`). |
| `TrackDisposable(IDisposable resource)` | `void` | Registers a handle disposed automatically on mod unload/reload. |

---

## 2. Address Library (`LocalExile.Client.Sdk.Addresses`)

- **`ClientBuildId`**: `Unknown = 0`, `Build3293c = 1`, `Build3293d = 2`.
- **`AddressEntry`**: `Name`, `Rva` (`ulong?`), `Category`, `Status`, `ExpectedBytes`, `Pattern`, `SourceDoc`, `Signature`, `Kind`, and `IsVerified` (true when `Rva.HasValue` and `Status` is `verified`, `live_verified`, `static_verified`, `doc_verified`, or `code_shared_3_29_3c`).
- **`IAddressLibrary`**:
  - Properties: `Build`, `BuildVersion`, `ExecutableSha256`, `ImageBase` (`nint`), `Entries`.
  - Methods: `TryGetEntry(symbolName, out entry)`, `TryResolveRva(symbolName, out rva)`, `TryResolveAddress(symbolName, out address)`, `ResolveAddress(symbolName)` (throws `KeyNotFoundException` if missing or `InvalidOperationException` if unverified on the active build).

See [Address Library](address-library.md) for the JSON schema and verified symbol table.

---

## 3. Engine Services (`LocalExile.Client.Sdk.Engine`)

All services below live on `IClientContext.Game` (`IGameServices`):

### UI (`IUiService`, `IUiWindow`, `IUiControl`, Typed Controls)

| Type | Key Members |
|---|---|
| `IUiService` | `OpenWindows`, `Load(virtualPath, windowId = null) -> IUiWindow`, `GetWindow(windowId)`, `CloseWindow(windowId)`, `ReloadWindow(windowId)`, `RegisterControl<TControl>(controlTypeName)`, `RegisterControl(controlTypeName, Func<ICustomUiControl>)` |
| `IUiWindow` | Inherits `IUiControl, IDisposable`; `SourcePath`, `IsOpen`, `event Action? Closed`, `Reload()`, `Close()` |
| `IUiControl` | `Id`, `ControlType`, `Path`, `Visible`, `Text`, `Parent`, `Children`, `Properties`, `GetProperty(name)`, `SetProperty(name, value)`, `Find(idPath)`, `Find<T>(idPath)`, `Require<T>(idPath)` |
| `IButton` | `Enabled`, `event Action? Clicked`, `Click()` |
| `ILabel` | `ForegroundColor` (`string?`) |
| `ITextBox` | `MaxLength`, `event Action<string>? TextChanged`, `event Action<string>? Submitted`, `Submit()` |
| `ICheckBox` | `IsChecked`, `event Action<bool>? CheckedChanged` |

### File Overlays (`IFileOverlayService` & `ClientTextEncoding`)

Resolution precedence is highest `priority` first (`VirtualFile` default `100` > `LooseDirectory` default `0` > `BundleOverlay` default `-10`), never mutating `Content.ggpk` or `Bundles2/`:

- `RegisterDirectory(string rootDirectory, int priority = 0) -> IDisposable`
- `RegisterVirtualFile(string virtualPath, ReadOnlyMemory<byte> rawBytes, int priority = 100) -> IDisposable`
- `RegisterVirtualTextFile(string virtualPath, string text, int priority = 100) -> IDisposable` — automatically encodes `.ui`, `.ot`, `.otc`, `.it`, `.itc`, `.ao`, `.aoc`, `.mat`, `.sm`, `.act`, `.epk`, `.ddt`, `.pet`, `.trl` as **UTF-16LE BOM (`0xFF 0xFE`) + CRLF**.
- `RegisterBundle(string bundlePath, string manifestPath, int priority = -10) -> IDisposable`
- `Exists(virtualPath)`, `ReadAllBytes(virtualPath)`, `ReadAllText(virtualPath)`, `Invalidate(virtualPath)`, `GetRegisteredOverlays()`
- `ClientTextEncoding` static helper: `NormalizeVirtualPath`, `RequiresUtf16LeBomCrlf`, `EncodeUtf16LeBomCrlf`, `DecodeText`.

### Scripts, Audio, Stats, World & Lifecycle

| Interface | Key Members |
|---|---|
| `IScriptService` | `InvokeFunction(uint objectId, string functionName, params GealValue[])`, `InvokeEvent(uint objectId, string eventName, params GealValue[])`, `ValidateScript(string gealSource) -> IReadOnlyList<GealScriptDiagnostic>`, `GetRegisteredBuiltins()`, `Register(name, Func<uint, GealArguments, GealValue>)`, `Register(name, Action<uint, GealArguments>)`. Types: `GealValue` (`Null`, `FromInt`, `FromFloat`, `FromBool`, `FromString`, `FromObjectId`), `GealArguments` (`Count`, `Int`, `Float`, `Bool`, `String`), `GealInvocationResult`. |
| `IAudioService` | `ActiveChannelCount` (client limit `0x200`), `PlayEvent(fmodEventPath, volume = 1.0f)`, `PlaySoundFile(virtualOggPath, volume = 1.0f)`, `StopChannel(channelHandle)`. |
| `IStatsService` | `GetStat(statKey, defaultValue = 0)`, `SetLocalStat(statKey, value)` (display-only, overwritten on next server sync), `OnStatChanged(statKey, Action<int>)`. |
| `IWorldService` | `CurrentAreaId`, `Objects` (`WorldObjectInfo`), `FindObject(uint objectId)`, `SpawnClientObject(metadataPath, x, y, z = 0f)`, `RemoveClientObject(uint objectId)`, `PlayEffect(uint targetObjectId, string effectPath)`. |
| `IGameLifecycle` | `FrameCount`, `IsConnected`, `CurrentAreaId`, events `Frame` (`Action<float>`), `AreaEntered` (`Action<AreaEventArgs>`), `AreaLeft` (`Action<string>`), `Connected` (`Action`), `Disconnected` (`Action<string?>`). |

---

## 4. Extend Registries (`LocalExile.Client.Sdk.Extend`)

| Registry on `IGameServices` | Interface & Key Members |
|---|---|
| `GealBuiltins` | `IGealBuiltinRegistry`: `RegisteredNames`, `Register(name, Func<uint, GealArguments, GealValue>)`, `Register(name, Action<uint, GealArguments>)`, `IsRegistered(name)`, `Invoke(name, uint objectId, params GealValue[])`. |
| `UiControls` | `IUiControlRegistry`: `RegisteredControlTypes`, `Register<TControl>(controlTypeName)`, `Register(controlTypeName, Func<ICustomUiControl>)`, `IsRegistered(controlTypeName)`, `CreateInstance(controlTypeName)`. `ICustomUiControl`: `ControlTypeName`, `OnInitialize(IUiControl)`, `OnPropertyChanged(name, value)`, `OnFrame(dt)`, `OnDestroy()`. |
| `Rulesets` | `IRulesetRegistry`: `MaxRulesetId = 0xAE`, `DefaultHostChannelRulesetId = 0xA0`, `RegisteredIds`, `Register<TRuleset>()`, `Register(byte rulesetId, Func<IClientRuleset>)`, `IsRegistered(byte)`, `TryDispatchUpdate(byte, ReadOnlySpan<byte>)`. `IClientRuleset`: `byte RulesetId`, `string Name`, `OnAreaEntered(AreaEventArgs)`, `OnRulesetUpdate(ReadOnlySpan<byte>)`, `OnFrame(dt)`, `OnAreaLeft()`. |
| `Opcodes` | `IOpcodeRegistry`: `ServerRulesetOpcode = 0x02C9`, `ClientRulesetOpcode = 0x02CA`, `FirstReservedOpcode = 0x02FC`, `RegisteredOpcodes`, `RegisterHandler(ushort opcode, Action<ReadOnlyMemory<byte>>, groupName = null)`, `IsRegistered(opcode)`, `TryDispatch(opcode, payload)`. |
| `Content` | `IContentRegistry`: `DatRows`, `ItemBases`, `WorldObjects`, `Overlays`, `DeclareDatRow(DatRowDeclaration)`, `DeclareItemBase(ItemBaseDeclaration)`, `DeclareWorldObject(WorldObjectDeclaration)`, `DeclareOverlay(ObjectOverlayDeclaration)`, `ComputeDeterministicId(modId, entryKey)` (32-bit FNV-1a). |

---

## 5. Networking & Wire Protocol (`LocalExile.Client.Sdk.Protocol`)

### `INetChannel`

- `ChannelId`, `Transport` (`NetTransportInfo`: `Kind` [`HostRuleset`, `ReservedHandlerGroup`, `ChatFallback`, `Loopback`], `IncomingOpcode`, `OutgoingOpcode`, `RulesetId`, `MaxPayloadBytes`, `IsConnected`).
- `Send<T>(T message, string? messageType = null)`, `On<T>(Action<T> handler, string? messageType = null) -> IDisposable`
- `SendRaw(ushort opcode, ReadOnlySpan<byte> payload)`, `OnRaw(ushort opcode, Action<ReadOnlyMemory<byte>> handler) -> IDisposable`

### `MessageCodec` & Protocol Records

- **Records:** `ModEnvelope`, `InstalledClientModInfo`, `ClientHandshake`, `ServerHandshake`, `ServerUiCommand` (`ServerUiCommandKind`: `OpenWindow`, `CloseWindow`, `ReloadWindow`, `SetText`, `SetVisible`, `SetProperty`, `SubscribeEvent`, `PlayAudio`, `PlayEffect`), `ClientUiEventNotification`.
- **Constants:** `SystemChannel = "__host"`, `EnvelopeMagic = 0x4E4D4F44` (`"NMOD"`), `ServerChatPrefix = "##NM "`, `ClientChatCommandPrefix = "/nm "`, `MaxRulesetPayloadBytes = 0xFFFF`.
- **Binary envelope frame:** `[u32 magic 'NMOD'][u16 protocolVersion][u16 channelLen][utf8 channel][u16 typeLen][utf8 type][u32 payloadLen][utf8 payloadJson][i64 sequence]` (big-endian).
- **Ruleset body framing (`HostRuleset`):** `[u8 rulesetId][u16 BE payloadLength][payload]` (carried under S→C `0x02C9` / C→S `0x02CA`; unmodded clients silently drop `0x02C9` for unknown ruleset IDs).
- **Chat fallback framing (`ChatFallback`):** S→C `##NM env <base64>` (plus legacy Spike S4 `##NM open` / `##NM set` compatibility) and C→S `/nm env <base64>`.
- **GEAL `0x285` hash helper:** `MessageCodec.ComputeGealFunctionHash(string functionName)` (folded 16-bit FNV-1a).
- **Collectible-ALC JSON serialization (`CollectibleJsonCodec` & `MessageCodec.GetJsonOptionsForType`):** On .NET 10, `System.Text.Json`'s internal `JsonSerializerOptionsUpdateHandler` caches strong type metadata that pins collectible `AssemblyLoadContext`s. `MessageCodec.CreateTypedEnvelope<T>` and `DeserializePayload<T>` automatically route types living in a collectible ALC through `CollectibleJsonCodec` (zero-static-cache `Utf8JsonWriter` + `JsonDocument` supporting records, POCOs, primitives, enums, nullables, arrays, lists, and dictionaries), and expose isolated per-ALC options via `MessageCodec.GetJsonOptionsForType(Type)`.

---

## 6. Raw Memory Access (`LocalExile.Client.Sdk.Raw`)

`IRawMemory` (gated by `Manifest.RawAccess == true`, otherwise `IClientContext.Raw` throws `RawAccessDeniedException`):

- `GetAddress(string symbolName) -> nint`
- `Read<T>(nint address)` / `Write<T>(nint address, T value)` (`where T : unmanaged`)
- `ReadBytes(nint address, int count)` / `WriteBytes(nint address, ReadOnlySpan<byte> data)`
- `InstallHook(string symbolName, nint detourTarget, out nint originalTrampoline, HookType hookType = HookType.InlineDetour) -> IDisposable` (`HookType.InlineDetour`, `HookType.VtableSlot`)
- `Call(string symbolName, params nint[] args) -> nint` (executed inside the native SEH/C++ exception wall)

---

## 7. Server Bridge (`LocalExile.Client.ServerBridge`)

Gateway-side library for LocalExile server mods (`Mods/Binaries/<id>/<id>.dll`):

| Type | Key Members |
|---|---|
| `IGatewayPacketSink` | `SendPacketAsync(connectionId, opcode, payload, ct)`, `SendChatReplyAsync(connectionId, text, ct)`. |
| `ClientBridgeOptions` | `ServerVersion` (`"0.5.2-beta"`), `BridgeVersion` (`SdkVersion.Current`), `HostRulesetId` (`0xA0`), `DefaultTransport` (`HostRuleset`), `RequiredMods`, `ServerCapabilities`. |
| `ClientBridgeHost` | `For(connId)` / `GetOrCreateSession(connId)`, `RemoveSession(connId)`, `On<T>(channel, Action<IClientSession, T>, messageType?)`, `SendAsync<T>(connId, channel, message, ...)`, `HandleIncomingPacketAsync(connId, opcode, payload, ct)`, `HandleIncomingChatCommandAsync(connId, commandLine, ct)`, events `ClientReady`, `MessageReceived`. |
| `Client` (static facade) | `DefaultHost`, `Initialize(ClientBridgeHost)`, `Reset()`, `For(connectionId) -> IClientSession`. |
| `IClientSession` | `ConnectionId`, `HasNativeHost`, `Handshake`, `ActiveTransport`, `ClientBuild`, `ExtenderVersion`, `ClientSdkVersion`, `InstalledMods`, `Ui` (`IClientUiSession`), `HasClientMod(modId, expectedPayloadHash?)`, `SendAsync<T>(channel, message, ...)`, `On<T>(channel, Action<IClientSession, T>, messageType?)`. |
| `IClientUiSession` & `ServerUiWindowHandle` | `OpenAsync(virtualPath, windowId?)`, `CloseAsync`, `ReloadAsync`, `SetTextAsync`, `SetVisibleAsync`, `SetPropertyAsync`, `OnButtonClick`, `OnControlEvent`, `PlayAudioAsync`, `PlayEffectAsync`. |

---

## 8. Threading, Hot Reload, and Exception-Wall Contracts

1. **Main-Thread Rule:** UI (`IUiService`), stats (`IStatsService`), world objects (`IWorldService`), and native calls must run on the main game thread (`IGameServices.Frame` or `context.Scheduler.RunOnGameThread(...)`).
2. **Native Exception Wall:** Every managed-to-native call crosses `nm_call_guarded` (`NM_MSVC_CXX_EXCEPTION_CODE = 0xE06D7363`), preventing `.ui` parse errors or missing-file throws from crashing the client.
3. **Managed Fault Isolation:** `ClientModLoader` wraps every mod `Load`, `Frame` tick, UI event, and network handler in a fault-isolating wall; a mod that repeatedly throws on `Frame` is disabled automatically while healthy mods keep running.
4. **Collectible Hot Reload:** `ClientModLoader` shadow-copies each mod DLL (and PDB if present) into a private temp copy before loading it into an `AssemblyLoadContext(isCollectible: true)`, watches for updates via debounced `FileSystemWatcher` / explicit `ReloadModAsync`, disposes tracked registrations, clears `System.Text.Json` metadata caches as defense-in-depth, unloads the context, and verifies GC reclamation via `WeakReference` (`UnloadedCleanly == true`). Also automatically falls back to Waypoint's `files/` overlay directory when `manifest.PayloadDir` (e.g., `"client"`) is absent on disk.

---

## 9. Versioning & Compatibility

- Because Path of Exile 3.29.3c and 3.29.3d are frozen binaries, SDK APIs never break due to upstream game patches.
- Mods declare `sdkVersionRange` in `mod.json`, enforced by both `DualModPackageValidator` (Waypoint) and `ClientModLoader` (`SdkVersion.IsCompatible`).
- Breaking changes to the native C ABI increment `NM_ABI_VERSION` (`0x00000100` for `0.1.0`), verified by `NativeEntryPoint.Initialize`.
