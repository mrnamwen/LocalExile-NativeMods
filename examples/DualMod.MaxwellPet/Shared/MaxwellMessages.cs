using System.Buffers.Binary;

namespace DualMod.MaxwellPet.Shared;

/// <summary>
/// Shared constants, appended 3.29.3d table coordinates, virtual asset paths, and custom opcodes
/// compiled into both the Server and Client halves of the Maxwell the Cat Pet MTX mod.
/// </summary>
public static class MaxwellProtocol
{
    public const string ModId = "maxwell";
    public const string ChannelName = "maxwell";

    // Virtual asset paths (under Mods/Maxwell/ so Waypoint namespace checks pass with zero warnings).
    public const string PanelUiPath = "Metadata/UI/Mods/Maxwell/MaxwellPanel.ui";
    public const string PanelWindowId = "maxwell_pet_panel";

    /// <summary>
    /// Pet.dat <c>Id</c> (singular <c>Metadata/Pet/</c>, no <c>.ot</c> suffix, matching all 516 stock pets
    /// and LocalExile's <c>CosmeticEffects.PetTypes()</c> filter).
    /// </summary>
    public const string PetMetadataPath = "Metadata/Pet/Mods/Maxwell/MaxwellPet";
    public const string PetOtPath = "Metadata/Pet/Mods/Maxwell/MaxwellPet.ot";
    public const string PetAoPath = "Metadata/Pet/Mods/Maxwell/MaxwellPet.ao";
    public const string PetIconPath = "Art/2DItems/Pets/Mods/Maxwell/MaxwellPet.dds";
    public const string MusicOggPath = "Audio/Mods/Maxwell/stock_market.ogg";

    // Appended 3.29.3d Pet MTX rows (NEVER replacing any stock pet row).
    // Stock 3.29.3d counts verified in LocalExile/data/client.db:
    //   MtxTypes = 8557 (0..8556), MtxTypeGameSpecific = 7849 (0..7848), Pet = 516 (0..515).
    public const string MtxTypeId = "Metadata/Items/Pets/Mods/MaxwellPet";
    public const string MtxDisplayName = "Maxwell the Cat Pet";
    public const string MtxDescription = "Summons your spinning Maxwell the Cat pet";

    public const int MtxTypesAppendedRow = 8557;
    public const uint MtxRecordId = 8558u; // MtxTypes row index + 1 (LocalExile MtxCatalog convention)
    public const uint MtxTypeHash = 0xA1EB592Eu; // MurmurHash2(MtxTypeId, seed=0) == 2716555566

    public const int MtxTypeGameSpecificAppendedRow = 7849;
    public const int MtxPetCategory = 14; // 516/516 stock pets use Category=14, __col4=[15], __col6=[18,19]

    public const int PetAppendedRow = 516;
    public const uint PetObjectTypeHash = 0x1F3AB11Au; // MurmurHash2(PetMetadataPath, seed=0) == 523940122
    public const ushort PetHash16 = 0xAE20; // ((PetObjectTypeHash >> 16) ^ (PetObjectTypeHash & 0xFFFF)) == 44576

    /// <summary>MicrotransactionSlot IDs for Pet Slot 1 and Pet Slot 2.</summary>
    public const byte PetSlot1 = 18;
    public const byte PetSlot2 = 19;

    // Patched .datc64 tables overlaid via nativeClient.replaces.
    public const string MtxTypesDatPath = "Data/MtxTypes.datc64";
    public const string MtxTypeGameSpecificDatPath = "Data/MtxTypeGameSpecific.datc64";
    public const string PetDatPath = "Data/Pet.datc64";

    public const string GealDanceBuiltin = "MaxwellDance";

    /// <summary>Host-channel custom ruleset ID for Maxwell pet client telemetry (0xA0=Host, 0xA1=HarvestGarden).</summary>
    public const byte MaxwellRulesetId = 0xA2;

    /// <summary>Custom opcode IDs matching <c>mod.json</c> <c>nativeClient.customOpcodes</c>.</summary>
    public const ushort OpcodeTogglePet = 1101;
    public const ushort OpcodeAudioControl = 1102;
    public const ushort OpcodeStateSync = 1103;
    public const ushort OpcodeOpenPanel = 1104;

    // Tuning bounds shared by server validation and client UI.
    public const float DefaultSpinSpeedDegPerSec = 90f;
    public const float MinSpinSpeedDegPerSec = 0f;
    public const float MaxSpinSpeedDegPerSec = 720f;

    public const float DefaultFloatHeight = 14f;
    public const float MinFloatHeight = 0f;
    public const float MaxFloatHeight = 60f;

    public const float DefaultMusicVolume = 0.75f;

    /// <summary>
    /// Computes Path of Exile 3.29's 32-bit MurmurHash2 (seed 0) over narrow ASCII/UTF-16-low-byte metadata paths,
    /// matching <c>GameServer.Core.Protocol.Instance.InstanceEntities.ObjectTypeHash</c> (<c>0x140df6e10</c>).
    /// </summary>
    public static uint ComputeObjectTypeHash(string metadataPath)
    {
        ArgumentNullException.ThrowIfNull(metadataPath);
        byte[] bytes = new byte[metadataPath.Length];
        for (int i = 0; i < metadataPath.Length; i++)
        {
            bytes[i] = (byte)metadataPath[i];
        }

        const uint m = 0x5BD1E995u;
        uint h = (uint)bytes.Length;
        int offset = 0;
        while (bytes.Length - offset >= 4)
        {
            uint k = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)) * m;
            k ^= k >> 24;
            h = (h * m) ^ (k * m);
            offset += 4;
        }

        switch (bytes.Length - offset)
        {
            case 3:
                h ^= (uint)bytes[offset + 2] << 16;
                goto case 2;
            case 2:
                h ^= (uint)bytes[offset + 1] << 8;
                goto case 1;
            case 1:
                h = (h ^ bytes[offset]) * m;
                break;
        }

        h = (h ^ (h >> 13)) * m;
        return h ^ (h >> 15);
    }

    /// <summary>Folds a 32-bit dat HASH32 into PoE's 16-bit HASH16 (<c>(h &gt;&gt; 16) ^ (h &amp; 0xFFFF)</c>).</summary>
    public static ushort FoldHash16(uint hash32) => (ushort)(((hash32 >> 16) ^ (hash32 & 0xFFFFu)) & 0xFFFFu);
}

/// <summary>
/// Client -&gt; Server: summons, dismisses, or updates spin/float parameters for the player's Maxwell pet.
/// When <paramref name="Summon"/> is <c>null</c> and no parameter override is specified, toggles summon state.
/// </summary>
public sealed record ToggleMaxwellRequest(
    bool? Summon = null,
    float? SpinSpeedDegPerSec = null,
    float? FloatHeight = null,
    byte PetSlot = MaxwellProtocol.PetSlot1);

/// <summary>
/// Server -&gt; Client: authoritative state of the player's Maxwell the Cat Pet MTX, including entity ID,
/// equipped cosmetic slot, spin/float tuning, and looping audio playback state.
/// </summary>
public sealed record MaxwellStateSync(
    string CharacterName,
    bool IsSummoned,
    uint PetEntityId,
    byte PetSlot,
    float SpinSpeedDegPerSec,
    float FloatHeight,
    bool MusicPlaying,
    bool MusicMuted,
    float MusicVolume,
    int DanceCount,
    string StatusMessage);

/// <summary>
/// Client -&gt; Server: controls looping playback, mute toggle, and volume for <c>Audio/Mods/Maxwell/stock_market.ogg</c>.
/// </summary>
public sealed record MaxwellAudioControl(
    bool? Play = null,
    bool? Muted = null,
    float? Volume = null);

/// <summary>
/// Server -&gt; Client: instructs the NativeMods client mod to open/focus <c>MaxwellPanel.ui</c> with current state.
/// </summary>
public sealed record OpenMaxwellPanel(
    string CharacterName,
    MaxwellStateSync InitialState);
