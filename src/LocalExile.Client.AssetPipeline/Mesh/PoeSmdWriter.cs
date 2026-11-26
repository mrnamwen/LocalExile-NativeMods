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
    byte Weight0 = 255,
    byte Bone1 = 0,
    byte Weight1 = 0,
    byte Bone2 = 0,
    byte Weight2 = 0,
    byte Bone3 = 0,
    byte Weight3 = 0);

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

public sealed record PoeSmdMesh(
    byte Version,
    ushort LodVersion,
    uint Flags,
    PoeBounds Bounds,
    IReadOnlyList<PoeVertex> Vertices,
    IReadOnlyList<uint> Indices,
    IReadOnlyList<PoeShapeRange> Shapes);

/// <summary>
/// Reads, writes, inspects, and round-trip verifies Path of Exile 3.29.3d <c>.smd</c>
/// (version 3 <c>DOLm</c> lver=4, 32-byte 4-bone skinned vertices) and matching <c>.smd.header</c> files,
/// verified byte-for-byte against stock 3.29.3d <c>.smd[.header]</c> assets.
/// </summary>
public static class PoeSmdWriter
{
    private const uint DolmMagic = 0x6D4C4F44u; // "DOLm"
    private const ushort LodVersion = 4;
    private const uint SmdV3Flags = 0x3Cu;       // normal(0x10) | tangent(0x20) | uv(0x08) | bones(0x04) (+ implicit pos 0x200 => 0x23C)
    private const uint FullVertexFlags = 0x23Cu;
    private const int VertexStride = 32;
    private const int MinimumHeaderSize = 280;

    /// <summary>
    /// Quantizes 4 floating-point bone weights to normalized <see cref="byte"/> weights summing to exactly 255
    /// using largest-remainder rounding. Falls back to <c>(255, 0, 0, 0)</c> if all weights are zero or negative.
    /// </summary>
    public static (byte W0, byte W1, byte W2, byte W3) NormalizeWeights(float w0, float w1, float w2, float w3)
    {
        Span<float> clamped = stackalloc float[4];
        clamped[0] = float.IsFinite(w0) && w0 > 0f ? w0 : 0f;
        clamped[1] = float.IsFinite(w1) && w1 > 0f ? w1 : 0f;
        clamped[2] = float.IsFinite(w2) && w2 > 0f ? w2 : 0f;
        clamped[3] = float.IsFinite(w3) && w3 > 0f ? w3 : 0f;

        float sum = clamped[0] + clamped[1] + clamped[2] + clamped[3];
        if (sum <= 1e-8f)
        {
            return (255, 0, 0, 0);
        }

        Span<int> floors = stackalloc int[4];
        Span<float> fracs = stackalloc float[4];
        int floorSum = 0;
        for (int i = 0; i < 4; i++)
        {
            float scaled = (clamped[i] / sum) * 255f;
            int f = Math.Clamp((int)MathF.Floor(scaled), 0, 255);
            floors[i] = f;
            fracs[i] = scaled - f;
            floorSum += f;
        }

        int remainder = 255 - floorSum;
        while (remainder > 0)
        {
            int best = 0;
            for (int i = 1; i < 4; i++)
            {
                if (fracs[i] > fracs[best])
                {
                    best = i;
                }
            }

            floors[best]++;
            fracs[best] = -1f;
            remainder--;
        }

        return ((byte)floors[0], (byte)floors[1], (byte)floors[2], (byte)floors[3]);
    }

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

    /// <summary>
    /// Full binary reader for PoE 3.29.3d version 3 <c>DOLm</c> <c>.smd</c> skinned meshes
    /// (supports lver 2/3/4, multi-LOD, flags <c>0x3C</c>/<c>0x23C</c> and extended vertex attributes, and 4-bone weights).
    /// </summary>
    public static PoeSmdMesh Read(ReadOnlySpan<byte> smdBytes)
    {
        if (smdBytes.Length < 45)
        {
            throw new InvalidDataException($"SMD too short ({smdBytes.Length} bytes).");
        }

        byte version = smdBytes[0];
        if (version != 3)
        {
            throw new InvalidDataException($"PoeSmdWriter.Read supports SMD v3 (DOLm); got v{version}. Use Inspect() for legacy v1-v2 headers.");
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
        if (lodShapes != shapeCount)
        {
            throw new InvalidDataException($"Unexpected DOLm header (lodShapes={lodShapes}, shapeCount={shapeCount}).");
        }

        int pos = 45;
        var levelCounts = new (int Tris, int Verts)[levels];
        for (int lv = 0; lv < levels; lv++)
        {
            EnsureAvailable(smdBytes, pos, 8, $"SMD DOLm level {lv} counts");
            int tris = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(pos, 4)));
            int verts = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(pos + 4, 4)));
            levelCounts[lv] = (tris, verts);
            pos += 8;
        }

        int stride = PoeFmtWriter.ComputeVertexStride(flags);
        var lod0Ranges = new (uint First, uint Count)[shapeCount];
        uint[] lod0Indices = [];
        PoeVertex[] lod0Vertices = [];

        for (int lv = 0; lv < levels; lv++)
        {
            (int triCount, int vertCount) = levelCounts[lv];
            EnsureAvailable(smdBytes, pos, shapeCount * 8, $"SMD DOLm level {lv} shape ranges");
            uint totalShapeIndices = 0;
            for (int i = 0; i < shapeCount; i++)
            {
                uint fi = BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(pos, 4));
                uint ic = BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(pos + 4, 4));
                if (lv == 0)
                {
                    lod0Ranges[i] = (fi, ic);
                }

                totalShapeIndices += ic;
                pos += 8;
            }

            uint expectedIndices = checked((uint)(triCount * 3));
            if (totalShapeIndices != expectedIndices)
            {
                throw new InvalidDataException($"Shape index counts sum to {totalShapeIndices}, expected {expectedIndices}.");
            }

            int idxWidth = vertCount > 0xFFFF ? 4 : 2;
            int idxBytes = checked((int)(expectedIndices * (uint)idxWidth));
            EnsureAvailable(smdBytes, pos, idxBytes, $"SMD DOLm level {lv} indices");

            if (lv == 0)
            {
                lod0Indices = new uint[expectedIndices];
                for (int i = 0; i < lod0Indices.Length; i++)
                {
                    uint idx = idxWidth == 2
                        ? BinaryPrimitives.ReadUInt16LittleEndian(smdBytes.Slice(pos + (i * 2), 2))
                        : BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(pos + (i * 4), 4));
                    if (idx >= (uint)vertCount)
                    {
                        throw new InvalidDataException($"SMD index {idx} out of range for {vertCount} vertices.");
                    }

                    lod0Indices[i] = idx;
                }
            }

            pos += idxBytes;

            int vertBytes = checked(vertCount * stride);
            EnsureAvailable(smdBytes, pos, vertBytes, $"SMD DOLm level {lv} vertices");
            if (lv == 0)
            {
                lod0Vertices = DecodeVertices(smdBytes.Slice(pos, vertBytes), vertCount, flags, stride);
            }

            pos += vertBytes;

            if (lodVer > 3)
            {
                EnsureAvailable(smdBytes, pos, 4, $"SMD DOLm level {lv} grid count");
                uint gridN = BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(pos, 4));
                int gridBytes = checked((int)(gridN * gridN * (uint)idxWidth));
                pos += 4;
                EnsureAvailable(smdBytes, pos, gridBytes, $"SMD DOLm level {lv} grid");
                pos += gridBytes;
            }
        }

        EnsureAvailable(smdBytes, pos, shapeCount * 4, "SMD shape name lengths");
        int[] nameLens = new int[shapeCount];
        for (int i = 0; i < shapeCount; i++)
        {
            nameLens[i] = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(pos, 4)));
            pos += 4;
        }

        var shapes = new List<PoeShapeRange>(shapeCount);
        for (int i = 0; i < shapeCount; i++)
        {
            EnsureAvailable(smdBytes, pos, nameLens[i], $"SMD shape {i} name");
            string name = Encoding.Unicode.GetString(smdBytes.Slice(pos, nameLens[i]));
            pos += nameLens[i];
            (uint fi, uint ic) = levels > 0 ? lod0Ranges[i] : (0u, 0u);
            shapes.Add(new PoeShapeRange(name, fi, ic));
        }

        if (smdBytes.Length - pos < 32)
        {
            throw new InvalidDataException($"SMD missing 32-byte trailer (only {smdBytes.Length - pos} trailing bytes).");
        }

        return new PoeSmdMesh(version, lodVer, flags, bounds, lod0Vertices, lod0Indices, shapes);
    }

    /// <summary>
    /// Inspects any PoE 3.29.3d <c>.smd</c> file (v3 <c>DOLm</c> as well as legacy v1/v2 headers).
    /// </summary>
    public static SmdValidationInfo Inspect(ReadOnlySpan<byte> smdBytes)
    {
        if (smdBytes.Length < 40)
        {
            throw new InvalidDataException($"SMD too short ({smdBytes.Length} bytes).");
        }

        byte version = smdBytes[0];
        if (version == 3)
        {
            PoeSmdMesh mesh = Read(smdBytes);
            return new SmdValidationInfo(mesh.Version, mesh.Vertices.Count, mesh.Indices.Count / 3, mesh.Shapes, mesh.Bounds);
        }

        if (version is 1 or 2)
        {
            int triCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(1, 4)));
            int vertCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(smdBytes.Slice(5, 4)));
            int shapeCount = BinaryPrimitives.ReadUInt16LittleEndian(smdBytes.Slice(10, 2));
            var bounds = new PoeBounds(
                BinaryPrimitives.ReadSingleLittleEndian(smdBytes.Slice(16, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(smdBytes.Slice(20, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(smdBytes.Slice(24, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(smdBytes.Slice(28, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(smdBytes.Slice(32, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(smdBytes.Slice(36, 4)));
            var shapes = new List<PoeShapeRange>(shapeCount);
            for (int i = 0; i < shapeCount; i++)
            {
                shapes.Add(new PoeShapeRange($"shape_{i}", 0u, 0u));
            }

            return new SmdValidationInfo(version, vertCount, triCount, shapes, bounds);
        }

        throw new InvalidDataException($"Unsupported SMD version {version}.");
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

        // Vertices (32 bytes each, signed snorm8 normals/tangents for v3, 4 bone indices + 4 normalized bone weights)
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
            vbuf[15] = unchecked((byte)(sbyte)-128); // matches stock 3.29.3d Rig_*.smd (0x80 on all vertices)

            vbuf[16] = EncodeSnorm8(v.Tangent.X);
            vbuf[17] = EncodeSnorm8(v.Tangent.Y);
            vbuf[18] = EncodeSnorm8(v.Tangent.Z);
            vbuf[19] = v.Handedness < 0f ? unchecked((byte)(sbyte)-127) : (byte)127;

            BinaryPrimitives.WriteUInt16LittleEndian(vbuf[20..22], BitConverter.HalfToUInt16Bits((Half)v.Uv.X));
            BinaryPrimitives.WriteUInt16LittleEndian(vbuf[22..24], BitConverter.HalfToUInt16Bits((Half)v.Uv.Y));

            vbuf[24] = v.Bone0;
            vbuf[25] = v.Bone1;
            vbuf[26] = v.Bone2;
            vbuf[27] = v.Bone3;

            vbuf[28] = v.Weight0;
            vbuf[29] = v.Weight1;
            vbuf[30] = v.Weight2;
            vbuf[31] = v.Weight3;

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
        PoeSmdMesh mesh = Read(smdBytes);
        int vertCount = mesh.Vertices.Count;
        int triCount = mesh.Indices.Count / 3;
        int shapeCount = mesh.Shapes.Count;

        // Cross-check .smd.header
        if (headerBytes.Length < 61)
        {
            throw new InvalidDataException($".smd.header too short ({headerBytes.Length} bytes).");
        }

        uint hdrVerts = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[0..4]);
        uint hdrIdxs = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[4..8]);
        uint hdrFlags = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[37..41]);
        ulong hdrShapes = BinaryPrimitives.ReadUInt64LittleEndian(headerBytes[53..61]);
        uint expectedFlags = mesh.Flags == 0 ? 0u : (mesh.Flags | 0x200u);
        if (hdrVerts != (uint)vertCount || hdrIdxs != (uint)(triCount * 3) || hdrFlags != expectedFlags || hdrShapes != (ulong)shapeCount)
        {
            throw new InvalidDataException(
                $".smd.header mismatch: verts={hdrVerts}/{vertCount}, idxs={hdrIdxs}/{triCount * 3}, flags=0x{hdrFlags:X}/0x{expectedFlags:X}, shapes={hdrShapes}/{shapeCount}.");
        }

        return new SmdValidationInfo(mesh.Version, vertCount, triCount, mesh.Shapes, mesh.Bounds);
    }

    private static PoeVertex[] DecodeVertices(ReadOnlySpan<byte> buf, int vertexCount, uint flags, int stride)
    {
        var result = new PoeVertex[vertexCount];
        if (vertexCount == 0 || stride < 12)
        {
            return result;
        }

        bool hasNormal = (flags & 0x10u) != 0;
        bool hasTangent = (flags & 0x20u) != 0;
        bool hasUv0 = (flags & 0x08u) != 0;
        bool hasBones = (flags & 0x04u) != 0;

        for (int i = 0; i < vertexCount; i++)
        {
            int o = i * stride;
            Vector3 pos = new(
                BinaryPrimitives.ReadSingleLittleEndian(buf.Slice(o, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(buf.Slice(o + 4, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(buf.Slice(o + 8, 4)));
            int cur = o + 12;

            Vector3 normal = new(0f, 0f, -1f);
            if (hasNormal)
            {
                normal = new Vector3(
                    DecodeSnorm8(buf[cur]),
                    DecodeSnorm8(buf[cur + 1]),
                    DecodeSnorm8(buf[cur + 2]));
                cur += 4;
            }

            Vector3 tangent = Vector3.UnitX;
            float handedness = 1f;
            if (hasTangent)
            {
                tangent = new Vector3(
                    DecodeSnorm8(buf[cur]),
                    DecodeSnorm8(buf[cur + 1]),
                    DecodeSnorm8(buf[cur + 2]));
                handedness = unchecked((sbyte)buf[cur + 3]) < 0 ? -1f : 1f;
                cur += 4;
            }

            Vector2 uv = Vector2.Zero;
            if (hasUv0)
            {
                uv = new Vector2(
                    (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(cur, 2))),
                    (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(cur + 2, 2))));
                cur += 4;
            }

            byte b0 = 0, b1 = 0, b2 = 0, b3 = 0;
            byte w0 = 255, w1 = 0, w2 = 0, w3 = 0;
            if (hasBones)
            {
                b0 = buf[cur];
                b1 = buf[cur + 1];
                b2 = buf[cur + 2];
                b3 = buf[cur + 3];
                w0 = buf[cur + 4];
                w1 = buf[cur + 5];
                w2 = buf[cur + 6];
                w3 = buf[cur + 7];
            }

            result[i] = new PoeVertex(pos, normal, tangent, handedness, uv, b0, w0, b1, w1, b2, w2, b3, w3);
        }

        return result;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> data, int offset, int needed, string context)
    {
        if (offset < 0 || needed < 0 || offset + needed > data.Length)
        {
            throw new InvalidDataException($"Truncated data in {context} (need {needed} bytes at {offset}, length={data.Length}).");
        }
    }

    private static byte EncodeSnorm8(float value)
    {
        int quantized = (int)MathF.Round(Math.Clamp(value, -1f, 1f) * 127f);
        return unchecked((byte)(sbyte)Math.Clamp(quantized, -127, 127));
    }

    private static float DecodeSnorm8(byte value)
    {
        sbyte s = unchecked((sbyte)value);
        return Math.Clamp(s / 127f, -1f, 1f);
    }
}
