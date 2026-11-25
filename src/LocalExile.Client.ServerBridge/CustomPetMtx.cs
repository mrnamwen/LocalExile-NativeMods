using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace LocalExile.Client.ServerBridge;

/// <summary>
/// Declarative descriptor for a brand-new appended Pet MTX added by a NativeMods dual-side mod
/// across <c>MtxTypes.datc64</c>, <c>MtxTypeGameSpecific.datc64</c>, and <c>Pet.datc64</c>
/// (never replacing any existing stock pet row).
/// </summary>
public sealed record CustomPetMtxDefinition
{
    /// <summary><c>MicrotransactionSlot</c> ID for Pet Slot 1 (spawns on side +1).</summary>
    public const byte PetSlot1 = 18;

    /// <summary><c>MicrotransactionSlot</c> ID for Pet Slot 2 (spawns on side -1).</summary>
    public const byte PetSlot2 = 19;

    /// <summary>Stock 3.29.3d <c>MtxTypeGameSpecific.Category</c> value shared by all 516 stock pets.</summary>
    public const int PetCategory = 14;

    /// <summary>Verified 3.29.3d stock row count in <c>MtxTypes.datc64</c> (<c>0..8556</c>).</summary>
    public const int Stock3293dMtxTypesCount = 8557;

    /// <summary>Verified 3.29.3d stock row count in <c>MtxTypeGameSpecific.datc64</c> (<c>0..7848</c>).</summary>
    public const int Stock3293dMtxTypeGameSpecificCount = 7849;

    /// <summary>Verified 3.29.3d stock row count in <c>Pet.datc64</c> (<c>0..515</c>).</summary>
    public const int Stock3293dPetCount = 516;

    /// <summary>Default 8 deserialized base components inherited from <c>Metadata/Pet/Pet.ot</c>.</summary>
    public static readonly IReadOnlyList<string> BasePetComponents =
    [
        "Positioned", "Stats", "Pathfinding", "Buffs", "Life", "Animated", "Actor", "PetAi"
    ];

    /// <summary>Appended 0-based row index in <c>MtxTypes.datc64</c> (must be &gt;= stock count).</summary>
    public int MtxTypesRowIndex { get; init; }

    /// <summary>Appended 0-based row index in <c>MtxTypeGameSpecific.datc64</c> (must be &gt;= stock count).</summary>
    public int MtxTypeGameSpecificRowIndex { get; init; }

    /// <summary>Appended 0-based row index in <c>Pet.datc64</c> (must be &gt;= stock count).</summary>
    public int PetRowIndex { get; init; }

    /// <summary>Unique <c>MtxTypes.Id</c> path (for example <c>Metadata/Items/Pets/Mods/MaxwellPet</c>).</summary>
    public string MtxTypeId { get; init; } = string.Empty;

    /// <summary>Human-readable Pet MTX display name shown in the Cosmetics (<c>K</c>) and MTX (<c>N</c>) panels.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Tooltip description written to <c>MtxTypes.Description1</c>.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// <c>Pet.dat</c> <c>Id</c> (singular <c>Metadata/Pet/...</c>, no <c>.ot</c> suffix, matching
    /// LocalExile's <c>CosmeticEffects.PetTypes()</c> filter and <c>InstanceEntities.ObjectTypeHash</c>).
    /// </summary>
    public string PetMetadataPath { get; init; } = string.Empty;

    /// <summary>Virtual path to the 2D Cosmetics/MTX icon <c>.dds</c> file.</summary>
    public string IconDdsPath { get; init; } = string.Empty;

    /// <summary>Cosmetics tab slots allowed for this pet (defaults to Pet Slot 1 &amp; 2: <c>[18, 19]</c>).</summary>
    public IReadOnlyList<byte> AllowedSlots { get; init; } = [PetSlot1, PetSlot2];

    /// <summary>LocalExile <c>MtxCatalog</c> record ID (<c>MtxTypesRowIndex + 1</c>), sent in <c>0x01f4</c>/<c>0x0204</c>/<c>0x01f9</c>.</summary>
    public uint RecordId => checked((uint)MtxTypesRowIndex + 1u);

    /// <summary>Deterministic 32-bit MurmurHash2 of <see cref="MtxTypeId"/> (<c>MtxTypes.HASH32</c> == <c>MtxTypeGameSpecific.HASH32</c>).</summary>
    public uint MtxTypeHash => PoeContentHash.MurmurHash2(MtxTypeId);

    /// <summary>Deterministic 32-bit MurmurHash2 of <see cref="PetMetadataPath"/> (used in <c>S-&gt;C 0x02bb</c> <c>CreateObject</c>).</summary>
    public uint PetObjectTypeHash => PoeContentHash.MurmurHash2(PetMetadataPath);

    /// <summary>Folded 16-bit hash written to <c>Pet.HASH16</c>.</summary>
    public ushort PetHash16 => PoeContentHash.FoldHash16(PetObjectTypeHash);

    /// <summary>Validates that this definition appends brand-new rows and follows 3.29.3d pet path invariants.</summary>
    public void Validate()
    {
        if (MtxTypesRowIndex < Stock3293dMtxTypesCount)
            throw new ArgumentOutOfRangeException(nameof(MtxTypesRowIndex), $"MtxTypesRowIndex ({MtxTypesRowIndex}) must be >= {Stock3293dMtxTypesCount} so stock MTX rows are never overwritten.");
        if (MtxTypeGameSpecificRowIndex < Stock3293dMtxTypeGameSpecificCount)
            throw new ArgumentOutOfRangeException(nameof(MtxTypeGameSpecificRowIndex), $"MtxTypeGameSpecificRowIndex ({MtxTypeGameSpecificRowIndex}) must be >= {Stock3293dMtxTypeGameSpecificCount} so stock rows are never overwritten.");
        if (PetRowIndex < Stock3293dPetCount)
            throw new ArgumentOutOfRangeException(nameof(PetRowIndex), $"PetRowIndex ({PetRowIndex}) must be >= {Stock3293dPetCount} so stock pets are never overwritten.");
        if (string.IsNullOrWhiteSpace(MtxTypeId))
            throw new ArgumentException("MtxTypeId is required.", nameof(MtxTypeId));
        if (string.IsNullOrWhiteSpace(DisplayName))
            throw new ArgumentException("DisplayName is required.", nameof(DisplayName));
        if (string.IsNullOrWhiteSpace(PetMetadataPath) || !PetMetadataPath.StartsWith("Metadata/Pet/", StringComparison.Ordinal))
            throw new ArgumentException("PetMetadataPath must start with singular 'Metadata/Pet/' to match PoE 3.29 Pet.dat and LocalExile CosmeticEffects.", nameof(PetMetadataPath));
        if (PetMetadataPath.EndsWith(".ot", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("PetMetadataPath must omit the '.ot' file extension (the client appends '.ot' automatically).", nameof(PetMetadataPath));
    }
}

/// <summary>Lightweight snapshot of a worn cosmetic slot (from LocalExile's <c>Game.WornCosmetics(player)</c>).</summary>
/// <param name="Slot">The <c>MicrotransactionSlot</c> ID (18 = Pet Slot 1, 19 = Pet Slot 2).</param>
/// <param name="RecordId">The equipped MTX record ID.</param>
/// <param name="MtxTypeHash">The equipped <c>MtxTypeGameSpecific</c> HASH32.</param>
public readonly record struct WornCosmeticSlot(byte Slot, uint RecordId, uint MtxTypeHash);

/// <summary>Lightweight snapshot of an active spawned pet entity (from LocalExile's <c>player.Pets</c>).</summary>
/// <param name="EntityId">World entity ID allocated for the pet <c>0x02bb</c> object.</param>
/// <param name="Side">Pet slot side (+1 for Pet Slot 18, -1 for Pet Slot 19).</param>
public readonly record struct SpawnedPetSnapshot(uint EntityId, int Side);

/// <summary>
/// Event payload raised by <see cref="LocalExilePetMtxRegistrar"/> when a player equips or unequips
/// a registered custom Pet MTX (for example directly via the in-game Cosmetics <c>K</c> panel).
/// </summary>
/// <param name="ConnectionId">Gateway connection identifier.</param>
/// <param name="CharacterName">Character name owning the pet.</param>
/// <param name="Pet">The registered custom Pet MTX definition.</param>
/// <param name="IsEquipped"><c>true</c> if the pet is now worn in a pet slot; <c>false</c> if unequipped or replaced.</param>
/// <param name="PetSlot">Cosmetics slot (<c>18</c> or <c>19</c>; last known slot on unequip).</param>
/// <param name="PetEntityId">Authoritative world entity ID spawned by LocalExile's <c>CosmeticsFeature.SpawnPets</c>, or <c>0</c> if unequipped/unspawned.</param>
public sealed record PetMtxEquipChangedEvent(
    string ConnectionId,
    string CharacterName,
    CustomPetMtxDefinition Pet,
    bool IsEquipped,
    byte PetSlot,
    uint PetEntityId);

/// <summary>
/// Reusable LocalExile Gateway helper that registers brand-new appended Pet MTX entries into LocalExile's
/// live <c>MtxCatalog</c> and <c>CosmeticEffects</c> catalogs (with clean unload removal), encodes <c>S-&gt;C 0x0204</c>
/// <c>RecordUpsert</c> grant packets, and detects Cosmetics (<c>K</c>) menu equip/unequip transitions in real time
/// so dual-side mods can sync client UI and looping audio without duplicating reflection or catalog glue.
/// </summary>
public sealed class LocalExilePetMtxRegistrar : IDisposable
{
    private readonly List<CustomPetMtxDefinition> _definitions = [];
    private readonly List<Action> _cleanupActions = [];
    private readonly ConcurrentDictionary<(string ConnectionId, uint MtxTypeHash), ObservedEquipState> _observed = new();
    private readonly Lock _gate = new();
    private bool _disposed;

    private readonly record struct ObservedEquipState(byte Slot, uint EntityId);

    /// <summary>All custom Pet MTX definitions currently registered with this registrar.</summary>
    public IReadOnlyList<CustomPetMtxDefinition> RegisteredPets
    {
        get { lock (_gate) { return [.. _definitions]; } }
    }

    /// <summary>
    /// Raised whenever a player transitions a registered custom Pet MTX between unequipped, equipped,
    /// switched pet slot, or respawned with a new entity ID in the Cosmetics (<c>K</c>) panel.
    /// </summary>
    public event Action<PetMtxEquipChangedEvent>? PetEquipChanged;

    /// <summary>
    /// Registers a brand-new <see cref="CustomPetMtxDefinition"/> with this registrar so equip tracking
    /// and live catalog registration apply to it.
    /// </summary>
    public IDisposable Register(CustomPetMtxDefinition pet)
    {
        ArgumentNullException.ThrowIfNull(pet);
        pet.Validate();

        lock (_gate)
        {
            if (!_definitions.Any(d => d.MtxTypeHash == pet.MtxTypeHash))
                _definitions.Add(pet);
        }

        return new ActionDisposable(() =>
        {
            lock (_gate)
            {
                _definitions.RemoveAll(d => d.MtxTypeHash == pet.MtxTypeHash);
            }
        });
    }

    /// <summary>
    /// Appends all registered custom Pet MTX definitions into LocalExile's live <c>Game.Mtx</c> (<c>MtxCatalog</c>)
    /// and <c>Game.MtxEffects</c> (<c>CosmeticEffects</c>) instances without modifying LocalExile core.
    /// Safe to call repeatedly (idempotent); entries are removed automatically on <see cref="Dispose"/>.
    /// </summary>
    /// <param name="mtxCatalog">LocalExile's <c>Game.Mtx</c> instance (or <c>null</c> if not initialized yet).</param>
    /// <param name="cosmeticEffects">LocalExile's <c>Game.MtxEffects</c> instance (or <c>null</c> if not initialized yet).</param>
    /// <param name="log">Optional diagnostic logger callback.</param>
    /// <returns>Number of newly inserted Pet MTX definitions across the live catalogs.</returns>
    public int ApplyToLiveCatalogs(object? mtxCatalog, object? cosmeticEffects, Action<string>? log = null)
    {
        if (mtxCatalog is null || cosmeticEffects is null)
            return 0;

        CustomPetMtxDefinition[] snapshot = RegisteredPets.ToArray();
        int applied = 0;

        foreach (CustomPetMtxDefinition pet in snapshot)
        {
            if (TryApplySinglePetToLiveCatalogs(pet, mtxCatalog, cosmeticEffects, log))
                applied++;
        }

        return applied;
    }

    /// <summary>
    /// Encodes the exact 3.29.3d <c>S-&gt;C 0x0204</c> (<c>InstanceMicrotransactions.RecordUpsert</c>) wire frame
    /// granting ownership of <paramref name="pet"/> to a connected client so it appears immediately in the
    /// Cosmetics (<c>K</c>) and Microtransactions (<c>N</c>) panels.
    /// </summary>
    /// <param name="pet">The custom Pet MTX definition to grant.</param>
    /// <param name="justReceived">Sets the trailing <c>justReceived</c> byte (defaults to <c>true</c>).</param>
    /// <returns>Wire-ready packet bytes beginning with big-endian opcode <c>0x0204</c>.</returns>
    public static byte[] EncodeRecordUpsertPacket(CustomPetMtxDefinition pet, bool justReceived = true)
    {
        ArgumentNullException.ThrowIfNull(pet);

        // Matches LocalExile InstanceMicrotransactions.RecordUpsert (0x0204) & LoginWriter.WStr verbatim:
        // u16 BE 0x0204 | u8 isStore(0) | u8 isGuild(0)
        // | Record: u8 Flags8(0) | u32 BE MtxTypeHash | u32 BE RecordId | u64 LE Qword(0)
        //           | WStr Name("") [u16 BE 0] | WStr Desc("") [u16 BE 0] | u8 Flags9(0) | u8 0 | u8 0
        // | u8 justReceived
        byte[] packet = new byte[29];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), 0x0204);
        packet[2] = 0; // isStore
        packet[3] = 0; // isGuild
        packet[4] = 0; // Flags8
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(5, 4), pet.MtxTypeHash);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(9, 4), pet.RecordId);
        // bytes 13..20: Qword = 0
        // bytes 21..22: Name WStr("") = u16 BE 0 (client resolves name from MtxTypes.datc64)
        // bytes 23..24: Description WStr("") = u16 BE 0
        // bytes 25..27: Flags9 = 0, no variation list (0), no variant (0)
        packet[28] = justReceived ? (byte)1 : (byte)0;
        return packet;
    }

    /// <summary>
    /// Compares a player's current worn cosmetic slots and spawned pet entities against the last observed state
    /// for <paramref name="connectionId"/> and raises <see cref="PetEquipChanged"/> for any equip, unequip,
    /// slot change, or entity respawn transition.
    /// </summary>
    /// <param name="connectionId">Unique connection identifier.</param>
    /// <param name="characterName">Active character name.</param>
    /// <param name="wornSlots">Current worn cosmetic slots (e.g. from <c>Game.WornCosmetics(player)</c>).</param>
    /// <param name="spawnedPets">Optional currently spawned pet entities (e.g. from <c>player.Pets</c>).</param>
    /// <returns>All equip transition events raised during this observation.</returns>
    public IReadOnlyList<PetMtxEquipChangedEvent> ObserveWornCosmetics(
        string connectionId,
        string characterName,
        IReadOnlyList<WornCosmeticSlot> wornSlots,
        IReadOnlyList<SpawnedPetSnapshot>? spawnedPets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        ArgumentNullException.ThrowIfNull(wornSlots);

        CustomPetMtxDefinition[] defs = RegisteredPets.ToArray();
        if (defs.Length == 0)
            return [];

        var events = new List<PetMtxEquipChangedEvent>();

        foreach (CustomPetMtxDefinition pet in defs)
        {
            WornCosmeticSlot? matchingSlot = null;
            foreach (WornCosmeticSlot slot in wornSlots)
            {
                if ((slot.Slot == CustomPetMtxDefinition.PetSlot1 || slot.Slot == CustomPetMtxDefinition.PetSlot2) &&
                    (slot.MtxTypeHash == pet.MtxTypeHash || slot.RecordId == pet.RecordId))
                {
                    matchingSlot = slot;
                    break;
                }
            }

            var key = (connectionId, pet.MtxTypeHash);
            bool hadPrevious = _observed.TryGetValue(key, out ObservedEquipState previous);

            if (matchingSlot is { } worn)
            {
                int expectedSide = worn.Slot == CustomPetMtxDefinition.PetSlot1 ? 1 : -1;
                uint entityId = 0;
                if (spawnedPets is not null)
                {
                    foreach (SpawnedPetSnapshot spawned in spawnedPets)
                    {
                        if (spawned.Side == expectedSide)
                        {
                            entityId = spawned.EntityId;
                            break;
                        }
                    }
                }

                var current = new ObservedEquipState(worn.Slot, entityId);
                bool newlyEquipped = !hadPrevious;
                bool slotChanged = hadPrevious && previous.Slot != worn.Slot;
                bool respawnedWithNewEntity = hadPrevious && entityId != 0 && previous.EntityId != entityId;

                if (newlyEquipped || slotChanged || respawnedWithNewEntity)
                {
                    _observed[key] = current;
                    var evt = new PetMtxEquipChangedEvent(connectionId, characterName, pet, IsEquipped: true, worn.Slot, entityId);
                    events.Add(evt);
                    PetEquipChanged?.Invoke(evt);
                }
                else if (hadPrevious && previous != current)
                {
                    // The pet entity was despawned (for example via /maxwell dismiss) while the Cosmetics (K)
                    // slot itself did not change; track the cleared entity ID without firing a false re-equip.
                    _observed[key] = current;
                }
            }
            else if (hadPrevious)
            {
                _observed.TryRemove(key, out _);
                var evt = new PetMtxEquipChangedEvent(connectionId, characterName, pet, IsEquipped: false, previous.Slot, PetEntityId: 0);
                events.Add(evt);
                PetEquipChanged?.Invoke(evt);
            }
        }

        return events;
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="connectionId"/> is currently observed as wearing the custom Pet MTX
    /// identified by <paramref name="mtxTypeHash"/> in Cosmetics Pet Slot 1 or 2.
    /// </summary>
    public bool IsEquipped(string connectionId, uint mtxTypeHash) =>
        !string.IsNullOrWhiteSpace(connectionId) && _observed.ContainsKey((connectionId, mtxTypeHash));

    /// <summary>
    /// Reflection-assisted convenience overload for LocalExile Gateway <c>IMod</c> scripts: reads
    /// <c>Game.WornCosmetics(player)</c> (<c>IEnumerable&lt;InstanceMicrotransactions.Equipped&gt;</c>) and
    /// <c>player.Pets</c> (<c>Dictionary&lt;uint, SpawnedPet&gt;</c>) without requiring compile-time coupling
    /// in <c>LocalExile.Client.ServerBridge</c>.
    /// </summary>
    public IReadOnlyList<PetMtxEquipChangedEvent> ObserveLiveGatewayPlayer(
        string connectionId,
        string characterName,
        IEnumerable? wornCosmetics,
        IDictionary? playerPets)
    {
        var worn = new List<WornCosmeticSlot>();
        if (wornCosmetics is not null)
        {
            foreach (object? item in wornCosmetics)
            {
                if (item is null)
                    continue;

                Type t = item.GetType();
                if (t.GetProperty("Slot")?.GetValue(item) is byte slot &&
                    t.GetProperty("Id")?.GetValue(item) is uint id &&
                    t.GetProperty("MtxTypeHash")?.GetValue(item) is uint hash)
                {
                    worn.Add(new WornCosmeticSlot(slot, id, hash));
                }
            }
        }

        var pets = new List<SpawnedPetSnapshot>();
        if (playerPets is not null)
        {
            lock (playerPets)
            {
                foreach (DictionaryEntry entry in playerPets)
                {
                    if (entry.Key is uint entityId && entry.Value is { } petObj)
                    {
                        Type pt = petObj.GetType();
                        object? sideVal = pt.GetProperty("Side")?.GetValue(petObj) ?? pt.GetField("Side")?.GetValue(petObj);
                        if (sideVal is int side)
                        {
                            pets.Add(new SpawnedPetSnapshot(entityId, side));
                        }
                    }
                }
            }
        }

        return ObserveWornCosmetics(connectionId, characterName, worn, pets);
    }

    /// <summary>Clears cached equip observation state when a player disconnects or leaves the area.</summary>
    public void ClearConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return;

        foreach (var key in _observed.Keys)
        {
            if (string.Equals(key.ConnectionId, connectionId, StringComparison.Ordinal))
                _observed.TryRemove(key, out _);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Action[] cleanups;
        lock (_gate)
        {
            cleanups = [.. _cleanupActions];
            _cleanupActions.Clear();
            _definitions.Clear();
        }

        foreach (Action cleanup in cleanups)
        {
            try
            {
                cleanup();
            }
            catch
            {
                // Best-effort catalog cleanup on mod unload.
            }
        }

        _observed.Clear();
    }

    private bool TryApplySinglePetToLiveCatalogs(
        CustomPetMtxDefinition pet,
        object mtxCatalog,
        object cosmeticEffects,
        Action<string>? log)
    {
        bool changed = false;

        // 1. Append record to MtxCatalog._owned (IList) and MtxCatalog._byId (IDictionary<uint, Record>)
        Type mtxType = mtxCatalog.GetType();
        FieldInfo? ownedField = mtxType.GetField("_owned", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? byIdField = mtxType.GetField("_byId", BindingFlags.Instance | BindingFlags.NonPublic);

        if (ownedField?.GetValue(mtxCatalog) is IList ownedList &&
            byIdField?.GetValue(mtxCatalog) is IDictionary byIdDict &&
            !byIdDict.Contains(pet.RecordId))
        {
            Type? recordType = ownedField.FieldType.IsGenericType ? ownedField.FieldType.GetGenericArguments()[0] : null;
            object? recordInstance = CreateMtxRecord(recordType, pet.MtxTypeHash, pet.RecordId);
            if (recordInstance is not null)
            {
                ownedList.Add(recordInstance);
                byIdDict[pet.RecordId] = recordInstance;
                changed = true;

                lock (_gate)
                {
                    _cleanupActions.Add(() =>
                    {
                        ownedList.Remove(recordInstance);
                        byIdDict.Remove(pet.RecordId);
                    });
                }
            }
        }

        // 2. Register Pet effect in CosmeticEffects._data.Item1 (Dictionary<uint, CosmeticEffects.Effect>)
        Type fxType = cosmeticEffects.GetType();
        FieldInfo? dataField = fxType.GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic);
        object? tupleObj = dataField?.GetValue(cosmeticEffects);
        if (tupleObj is not null)
        {
            FieldInfo? item1Field = tupleObj.GetType().GetField("Item1");
            if (item1Field?.GetValue(tupleObj) is IDictionary effectsDict && !effectsDict.Contains(pet.MtxTypeHash))
            {
                Type? effectType = item1Field.FieldType.IsGenericType ? item1Field.FieldType.GetGenericArguments()[1] : null;
                object? effectInstance = CreatePetEffect(effectType, pet);
                if (effectInstance is not null)
                {
                    effectsDict[pet.MtxTypeHash] = effectInstance;
                    changed = true;

                    lock (_gate)
                    {
                        _cleanupActions.Add(() => effectsDict.Remove(pet.MtxTypeHash));
                    }
                }
            }
        }

        if (changed)
        {
            log?.Invoke($"[ServerBridge] Registered brand-new Pet MTX '{pet.DisplayName}' (RecordId={pet.RecordId}, HASH32=0x{pet.MtxTypeHash:X8}, Pet='{pet.PetMetadataPath}').");
        }

        return changed;
    }

    private static object? CreateMtxRecord(Type? recordType, uint mtxTypeHash, uint recordId)
    {
        if (recordType is null)
            return null;

        // InstanceMicrotransactions.Record(uint MtxTypeHash, uint Id, string Name = "", string Description = "", ulong Qword = 0, byte Flags8 = 0, byte Flags9 = 0)
        foreach (ConstructorInfo ctor in recordType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            ParameterInfo[] parameters = ctor.GetParameters();
            if (parameters.Length >= 2 &&
                parameters[0].ParameterType == typeof(uint) &&
                parameters[1].ParameterType == typeof(uint))
            {
                object?[] args = new object?[parameters.Length];
                args[0] = mtxTypeHash;
                args[1] = recordId;
                for (int i = 2; i < parameters.Length; i++)
                {
                    args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : GetDefaultValue(parameters[i].ParameterType);
                }

                return ctor.Invoke(args);
            }
        }

        return null;
    }

    private static object? CreatePetEffect(Type? effectType, CustomPetMtxDefinition pet)
    {
        if (effectType is null)
            return null;

        // CosmeticEffects.Effect(ushort Visual, ushort WeaponEffect, IReadOnlyList<(uint Stat, int Value)> Stats, int Pet = 0)
        object? effect = null;
        foreach (ConstructorInfo ctor in effectType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            ParameterInfo[] parameters = ctor.GetParameters();
            if (parameters.Length == 4 &&
                parameters[0].ParameterType == typeof(ushort) &&
                parameters[1].ParameterType == typeof(ushort) &&
                parameters[3].ParameterType == typeof(int))
            {
                Array emptyStats = Array.CreateInstance(typeof(ValueTuple<uint, int>), 0);
                effect = ctor.Invoke([(ushort)0, (ushort)0, emptyStats, pet.PetRowIndex + 1]);
                break;
            }
        }

        if (effect is null)
            return null;

        SetInitProperty(effectType, effect, "PetType", pet.PetMetadataPath);
        SetInitProperty(effectType, effect, "PetComponents", CustomPetMtxDefinition.BasePetComponents);
        SetInitProperty(effectType, effect, "PetStates", null);
        return effect;
    }

    private static void SetInitProperty(Type type, object instance, string propertyName, object? value)
    {
        PropertyInfo? prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        MethodInfo? setter = prop?.GetSetMethod(nonPublic: true);
        setter?.Invoke(instance, [value]);
    }

    private static object? GetDefaultValue(Type type) =>
        type.IsValueType ? Activator.CreateInstance(type) : null;

    private sealed class ActionDisposable(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;
        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
