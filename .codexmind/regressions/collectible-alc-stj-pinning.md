# Regression & Pitfall Log: .NET 10 `System.Text.Json` Collectible `AssemblyLoadContext` Pinning

Discovered during end-to-end hot-reload testing of `DualMod.HarvestGarden` (`UnloadModAsync().UnloadedCleanly` returned `false` whenever a client mod called `context.Net.Send<T>` or `context.Net.On<T>` with mod-defined message records).

## Symptom

- A client mod loaded into a collectible `AssemblyLoadContext(isCollectible: true)` unloads cleanly **until** it serializes or deserializes a type defined in its own assembly through `System.Text.Json.JsonSerializer`.
- Once touched by `JsonSerializer`, `WeakReference.IsAlive` remains `true` permanently after `AssemblyLoadContext.Unload()` and full GC passes, blocking hot reload reclamation.

## Root Cause (Verified on .NET 10)

- `System.Text.Json.JsonSerializerOptions` caches strong `JsonTypeInfo` references to every `Type` serialized or deserialized through it.
- Even allocating a fresh `new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }` still registers strong type metadata inside .NET 10's internal global `JsonSerializerOptionsUpdateHandler` tracking cache, pinning the collectible `AssemblyLoadContext` in memory.

## Fix Implemented

1. **Zero-Static-Cache Collectible Codec (`src/LocalExile.Client.Sdk/Protocol/CollectibleJsonCodec.cs`):**
   - Built directly on `Utf8JsonWriter` and `JsonDocument` (camelCase, case-insensitive, supporting records/primary constructors, POCOs, primitives, enums, nullables, arrays, lists/`IReadOnlyList<T>`, and dictionaries) with **zero** static or global type-metadata caches.
   - `MessageCodec.CreateTypedEnvelope<T>` and `MessageCodec.DeserializePayload<T>` inspect whether `T` (or its generic/element types) lives in a collectible `AssemblyLoadContext` and automatically route those types through `CollectibleJsonCodec`.
2. **Per-ALC `ConditionalWeakTable` Options (`MessageCodec.GetJsonOptionsForType(Type)`):**
   - Exposes isolated per-ALC `JsonSerializerOptions` via `ConditionalWeakTable<AssemblyLoadContext, JsonSerializerOptions>`.
3. **Defense-in-Depth Cache Clear (`ClientModLoader.DetachAndInitiateUnload`):**
   - Invokes `TryClearSystemTextJsonMetadataCache()` during unload so even third-party client mods that call `JsonSerializer` directly do not leak their `AssemblyLoadContext`.

## Verification

- Covered by `ModLoaderAndIsolationTests.CollectibleAlc_TypedNetSendAndReceiveWithModDefinedMessage_UnloadsCleanly` (real `PersistedAssemblyBuilder` DLL with mod-defined `ModPing` record, proving `UnloadedCleanly == true`) and `HarvestGardenEndToEndTests`.
