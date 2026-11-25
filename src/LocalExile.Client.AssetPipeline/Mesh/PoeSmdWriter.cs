using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace LocalExile.Client.AssetPipeline.Mesh;

public readonly record struct PoeVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector3 Tangent,
    float Handedness,
    Vector2 Uv,
    byte Bone0 = 0,
    byte Weight0 = 255);

public sealed record PoeShapeRange(
    string Name,
    uint FirstIndex,
    uint IndexCount);

public sealed record PoeBounds(
    float MinX,
    float MaxX,
    float MinY,
    float MaxY,
    float MinZ,
    float MaxZ)
{
    public static PoeBounds Compute(ReadOnlySpan<PoeVertex> vertices)
    {
        if (vertices.IsEmpty)
        {
            return new PoeBounds(0, 0, 0, 0, 0, 0);
        }

        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;

        foreach (PoeVertex v in vertices)
        {
            if (v.Position.X < minX) minX = v.Position.X;
            if (v.Position.X > maxX) maxX = v.Position.X;
            if (v.Position.Y < minY) minY = v.Position.Y;
            if (v.Position.Y > maxY) maxY = v.Position.Y;
            if (v.Position.Z < minZ) minZ = v.Position.Z;
            if (v.Position.Z > maxZ) maxZ = v.Position.Z;
        }

        return new PoeBounds(minX, maxX, minY, maxY, minZ, maxZ);
    }
}

public sealed record SmdValidationInfo(
    byte Version,
    int VertexCount,
    int TriangleCount,
    IReadOnlyList<PoeShapeRange> Shapes,
    PoeBounds Bounds);

/// <summary>
/// Writes and round-trip verifies Path of Exile 3.29.3d <c>.smd</c> (version 3 <c>DOLm</c> lver=4, 32-byte skinned vertices)
/// and matching <c>.smd.header</c> files (verified byte-for-byte against stock <c>Art/Models/Pet/Cat/Rig_8dd2fec6.smd[.header]</c>).
/// </summary>
public static class PoeSmdWriter
{
    private const uint DolmMagic = 0x6D4C4F44u; // "DOLm"
    private const ushort LodVersion = 4;
    private const uint SmdV3Flags = 0x3Cu;       // normal(0x10) | tangent(0x20) | uv(0x08) | bones(0x04) (+ implicit pos 0x200 => 0x23C)
    private const uint FullVertexFlags = 0x23Cu;
    private const int VertexStride = 32;
    private const int MinimumHeaderSize = 280;

    public static (byte[] SmdBytes, byte[] HeaderBytes) Write(
        IReadOnlyList<PoeVertex> vertices,
        IReadOnlyList<uint> indices,
        IReadOnlyList<PoeShapeRange> shapes,
        PoeBounds? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(shapes);

        if (vertices.Count == 0)
        {
            throw new ArgumentException("SMD must contain at least one vertex.", nameof(vertices));
        }

        if (indices.Count == 0 || indices.Count % 3 != 0)
        {
            throw new ArgumentException("SMD index count must be a positive multiple of 3.", nameof(indices));
        }

        if (shapes.Count == 0)
        {
            throw new ArgumentException("SMD must contain at least one shape.", nameof(shapes));
        }

        PoeVertex[] vertexArr = vertices is PoeVertex[] arr ? arr : [.. vertices];
        PoeBounds actualBounds = bounds ?? PoeBounds.Compute(vertexArr);

        byte[] smdBytes = BuildSmdV3(vertexArr, indices, shapes, actualBounds);
        byte[] headerBytes = BuildSmdHeader(vertexArr.Length, indices.Count, shapes, actualBounds);
        return (smdBytes, headerBytes);
    }

    private static byte[] BuildSmdV3(
        PoeVertex[] vertices,
        IReadOnlyList<uint> indices,
        IReadOnlyList<PoeShapeRange> shapes,
        PoeBounds bounds)
    {
        byte[][] utf16Names = new byte[shapes.Count][];
        uint totalNameBytes = 0;
        for (int i = 0; i < shapes.Count; i++)
        {
            utf16Names[i] = Encoding.Unicode.GetBytes(shapes[i].Name);
            totalNameBytes += (uint)utf16Names[i].Length;
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // Header (32 bytes): version(1), const 4(1), shape_count(2), total_name_bytes(4), bounds[6](24)
        bw.Write((byte)3);
        bw.Write((byte)4);
        bw.Write((ushort)shapes.Count);
        bw.Write(totalNameBytes);
        bw.Write(bounds.MinX);
        bw.Write(bounds.MaxX);
        bw.Write(bounds.MinY);
        bw.Write(bounds.MaxY);
        bw.Write(bounds.MinZ);
        bw.Write(bounds.MaxZ);

        // DOLm LOD header (13 bytes): magic(4), lod_version(2), levels(1), lod_shapes(2), flags(4)
        bw.Write(DolmMagic);
        bw.Write(LodVersion);
        bw.Write((byte)1);
        bw.Write((ushort)shapes.Count);
        bw.Write(SmdV3Flags);

        // Level 0 counts: triangles, vertices
        bw.Write((uint)(indices.Count / 3));
        bw.Write((uint)vertices.Length);

        // Level 0 shape ranges: (first_index, index_count)
        foreach (PoeShapeRange s in shapes)
        {
            bw.Write(s.FirstIndex);
            bw.Write(s.IndexCount);
        }

        // Indices (u16 when vertexCount <= 65535, else u32)
        bool wide = vertices.Length > 0xFFFF;
        foreach (uint idx in indices)
        {
            if (idx >= (uint)vertices.Length)
            {
                throw new InvalidDataException($"Index {idx} out of bounds for {vertices.Length} vertices.");
            }

            if (wide)
            {
                bw.Write(idx);
            }
            else
            {
                bw.Write((ushort)idx);
            }
        }

        // Vertices (32 bytes each, signed snorm8 normals/tangents for v3)
        Span<byte> vbuf = stackalloc byte[VertexStride];
        foreach (PoeVertex v in vertices)
        {
            vbuf.Clear();
            BinaryPrimitives.WriteSingleLittleEndian(vbuf[0..4], v.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(vbuf[4..8], v.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(vbuf[8..12], v.Position.Z);

            vbuf[12] = EncodeSnorm8(v.Normal.X);
            vbuf[13] = EncodeSnorm8(v.Normal.Y);
            vbuf[14] = EncodeSnorm8(v.Normal.Z);
            vbuf[15] = 0;

            vbuf[16] = EncodeSnorm8(v.Tangent.X);
            vbuf[17] = EncodeSnorm8(v.Tangent.Y);
            vbuf[18] = EncodeSnorm8(v.Tangent.Z);
            vbuf[19] = v.Handedness < 0f ? unchecked((byte)(sbyte)-128) : (byte)127;

            BinaryPrimitives.WriteUInt16LittleEndian(vbuf[20..22], BitConverter.HalfToUInt16Bits((Half)v.Uv.X));
            BinaryPrimitives.WriteUInt16LittleEndian(vbuf[22..24], BitConverter.HalfToUInt16Bits((Half)v.Uv.Y));

            vbuf[24] = v.Bone0;
            vbuf[25] = 0;
            vbuf[26] = 0;
            vbuf[27] = 0;

            vbuf[28] = v.Weight0;
            vbuf[29] = 0;
            vbuf[30] = 0;
            vbuf[31] = 0;

            bw.Write(vbuf);
        }

        // lver > 3 lookup grid count = 0
        bw.Write(0u);

        // Shape name UTF-16LE byte lengths, then UTF-16LE name bytes back-to-back
        foreach (byte[] nb in utf16Names)
        {
            bw.Write((uint)nb.Length);
        }

        foreach (byte[] nb in utf16Names)
        {
            bw.Write(nb);
        }

        // Standard 32-byte trailer matching stock Rig_8dd2fec6.smd (u32 4 + 28 zero bytes)
        bw.Write(4u);
        Span<byte> zeros = stackalloc byte[28];
        zeros.Clear();
        bw.Write(zeros);

        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildSmdHeader(
        int vertexCount,
        int indexCount,
        IReadOnlyList<PoeShapeRange> shapes,
        PoeBounds bounds)
    {
        using var ms = new MemoryStream(MinimumHeaderSize);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // Verified against Art/Models/Pet/Cat/Rig_8dd2fec6.smd.header (280 bytes)
        bw.Write((uint)vertexCount);
        bw.Write((uint)indexCount);
        bw.Write(0u);

        // .smd.header bounds order: minX, minY, minZ, maxX, maxY, maxZ
        bw.Write(bounds.MinX);
        bw.Write(bounds.MinY);
        bw.Write(bounds.MinZ);
        bw.Write(bounds.MaxX);
        bw.Write(bounds.MaxY);
        bw.Write(bounds.MaxZ);

        bw.Write((byte)1); // LOD count
        bw.Write(FullVertexFlags); // 0x23C
        bw.Write(0UL); // 8 zero bytes
        bw.Write(vertexCount > 0xFFFF ? 4u : 2u); // index stride
        bw.Write((ulong)shapes.Count);

        foreach (PoeShapeRange s in shapes)
        {
            byte[] asciiName = Encoding.ASCII.GetBytes(s.Name);
            bw.Write(s.FirstIndex);
            bw.Write(s.IndexCount);
            bw.Write((ulong)asciiName.Length);
            bw.Write(asciiName);
        }

        // Trailer marker bytes matching Rig_8dd2fec6.smd.header (0x04, 0x01) + zero padding to >= 280 bytes
        bw.Write((byte)4);
        bw.Write((byte)1);
        bw.Flush();

        if (ms.Length < MinimumHeaderSize)
        {
            ms.Write(new byte[MinimumHeaderSize - (int)ms.Length]);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Round-trip parses and verifies a <c>.smd</c> + <c>.smd.header</c> pair.
    /// </summary>
    public static SmdValidationInfo Validate(ReadOnlySpan<byte> smdBytes, ReadOnlySpan<byte> headerBytes)
    {
        if (smdBytes.Length < 53)
        {
            throw new InvalidDataException($"SMD too short ({smdBytes.Length} bytes).");
        }

        byte version = smdBytes[0];
        if (version != 3)
        {
            throw new InvalidDataException($"Expected SMD v3, got v{version}.");
        }

        int shapeCount = BinaryPrimitives.ReadUInt16LittleEndian(smdBytes.Slice(2, 2));
        var bounds = new PoeBounds(
            BinaryPrimitives.ReadSingleLittleEndian(smdBytes.Slice(8, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(smdBytes.Slice(12, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(smdBytes.Slice(16, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(smdBytes.Slice(20, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(smdBytes.Slice(24, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(smdBytes.Slice(28, 4)));

        if (BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(32, 4)) != DolmMagic)
        {
            throw new InvalidDataException("Missing 'DOLm' magic in SMD v3.");
        }

        ushort lodVer = BinaryPrimitives.ReadUInt16LittleEndian(smdBytes.Slice(36, 2));
        byte levels = smdBytes[38];
        ushort lodShapes = BinaryPrimitives.ReadUInt16LittleEndian(smdBytes.Slice(39, 2));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(41, 4));
        if (levels != 1 || lodShapes != shapeCount || (flags & 0x3C) != 0x3C)
        {
            throw new InvalidDataException($"Unexpected DOLm header (levels={levels}, lodShapes={lodShapes}, flags=0x{flags:X}).");
        }

        int pos = 45;
        int triCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(pos, 4)));
        int vertCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(pos + 4, 4)));
        pos += 8;

        var ranges = new (uint First, uint Count)[shapeCount];
        uint totalShapeIndices = 0;
        for (int i = 0; i < shapeCount; i++)
        {
            uint fi = BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(pos, 4));
            uint ic = BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(pos + 4, 4));
            ranges[i] = (fi, ic);
            totalShapeIndices += ic;
            pos += 8;
        }

        if (totalShapeIndices != (uint)(triCount * 3))
        {
            throw new InvalidDataException($"Shape index counts sum to {totalShapeIndices}, expected {triCount * 3}.");
        }

        int idxWidth = vertCount > 0xFFFF ? 4 : 2;
        for (int i = 0; i < triCount * 3; i++)
        {
            uint idx = idxWidth == 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(smdBytes.Slice(pos + (i * 2), 2))
                : BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(pos + (i * 4), 4));
            if (idx >= (uint)vertCount)
            {
                throw new InvalidDataException($"SMD index {idx} out of range for {vertCount} vertices.");
            }
        }

        pos += triCount * 3 * idxWidth;
        pos += vertCount * VertexStride;

        if (lodVer > 3)
        {
            uint gridN = BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(pos, 4));
            pos += 4 + checked((int)(gridN * gridN * (uint)idxWidth));
        }

        int[] nameLens = new int[shapeCount];
        for (int i = 0; i < shapeCount; i++)
        {
            nameLens[i] = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(pos, 4)));
            pos += 4;
        }

        var shapes = new List<PoeShapeRange>(shapeCount);
        for (int i = 0; i < shapeCount; i++)
        {
            string name = Encoding.Unicode.GetString(smdBytes.Slice(pos, nameLens[i]));
            pos += nameLens[i];
            shapes.Add(new PoeShapeRange(name, ranges[i].First, ranges[i].Count));
        }

        if (smdBytes.Length - pos < 32)
        {
            throw new InvalidDataException($"SMD missing 32-byte trailer (only {smdBytes.Length - pos} trailing bytes).");
        }

        // Cross-check .smd.header
        if (headerBytes.Length < 61)
        {
            throw new InvalidDataException($".smd.header too short ({headerBytes.Length} bytes).");
        }

        uint hdrVerts = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[0..4]);
        uint hdrIdxs = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[4..8]);
        uint hdrFlags = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[37..41]);
        ulong hdrShapes = BinaryPrimitives.ReadUInt64LittleEndian(headerBytes[53..61]);
        if (hdrVerts != (uint)vertCount || hdrIdxs != (uint)(triCount * 3) || hdrFlags != FullVertexFlags || hdrShapes != (ulong)shapeCount)
        {
            throw new InvalidDataException(
                $".smd.header mismatch: verts={hdrVerts}/{vertCount}, idxs={hdrIdxs}/{triCount * 3}, flags=0x{hdrFlags:X}, shapes={hdrShapes}/{shapeCount}.");
        }

        return new SmdValidationInfo(version, vertCount, triCount, shapes, bounds);
    }

    private static byte EncodeSnorm8(float value)
    {
        int quantized = (int)MathF.Round(Math.Clamp(value, -1f, 1f) * 127f);
        return unchecked((byte)(sbyte)Math.Clamp(quantized, -127, 127));
    }
}
