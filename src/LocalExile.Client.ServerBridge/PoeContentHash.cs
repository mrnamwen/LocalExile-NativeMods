using System.Buffers.Binary;

namespace LocalExile.Client.ServerBridge;

/// <summary>
/// Deterministic content hashing utilities matching Path of Exile 3.29's native client and LocalExile server
/// (<c>InstanceEntities.ObjectTypeHash</c> at <c>0x140df6e10</c>, <c>MtxTypes.HASH32</c>, <c>BaseItemTypes.HASH32</c>,
/// and folded 16-bit dat <c>HASH16</c> keys).
/// </summary>
public static class PoeContentHash
{
    private const uint Murmur2Multiplier = 0x5BD1E995u;

    /// <summary>
    /// Computes Path of Exile 3.29's 32-bit MurmurHash2 (seed <c>0</c>) over <paramref name="metadataPath"/>
    /// with each UTF-16 code unit narrowed to its low byte (matching <c>0x14247b370</c> -&gt; <c>0x140df6e10</c>).
    /// </summary>
    /// <param name="metadataPath">Virtual metadata path (for example <c>Metadata/Items/Pets/Mods/MaxwellPet</c>).</param>
    /// <returns>The 32-bit unsigned hash matching PoE 3.29's <c>HASH32</c> / <c>ObjectTypeHash</c>.</returns>
    public static uint MurmurHash2(string metadataPath)
    {
        ArgumentNullException.ThrowIfNull(metadataPath);

        if (metadataPath.Length <= 256)
        {
            Span<byte> stackBuffer = stackalloc byte[metadataPath.Length];
            for (int i = 0; i < metadataPath.Length; i++)
            {
                stackBuffer[i] = (byte)metadataPath[i];
            }

            return MurmurHash2(stackBuffer);
        }

        byte[] heapBuffer = new byte[metadataPath.Length];
        for (int i = 0; i < metadataPath.Length; i++)
        {
            heapBuffer[i] = (byte)metadataPath[i];
        }

        return MurmurHash2(heapBuffer);
    }

    /// <summary>
    /// Computes Path of Exile 3.29's 32-bit MurmurHash2 (seed <c>0</c>) over raw <paramref name="bytes"/>.
    /// </summary>
    /// <param name="bytes">Input byte span.</param>
    /// <returns>The 32-bit unsigned MurmurHash2 digest.</returns>
    public static uint MurmurHash2(ReadOnlySpan<byte> bytes)
    {
        uint h = (uint)bytes.Length;
        int offset = 0;
        while (bytes.Length - offset >= 4)
        {
            uint k = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4)) * Murmur2Multiplier;
            k ^= k >> 24;
            h = (h * Murmur2Multiplier) ^ (k * Murmur2Multiplier);
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
                h = (h ^ bytes[offset]) * Murmur2Multiplier;
                break;
        }

        h = (h ^ (h >> 13)) * Murmur2Multiplier;
        return h ^ (h >> 15);
    }

    /// <summary>
    /// Folds a 32-bit PoE dat <c>HASH32</c> value into its 16-bit <c>HASH16</c> representation
    /// (<c>(hash32 &gt;&gt; 16) ^ (hash32 &amp; 0xFFFF)</c>), matching stock 3.29.3d <c>Pet.dat</c> rows.
    /// </summary>
    /// <param name="hash32">32-bit hash value.</param>
    /// <returns>Folded 16-bit hash value.</returns>
    public static ushort FoldHash16(uint hash32) =>
        (ushort)(((hash32 >> 16) ^ (hash32 & 0xFFFFu)) & 0xFFFFu);
}
