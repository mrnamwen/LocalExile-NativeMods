using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace LocalExile.Client.AssetPipeline.Mesh;

public readonly record struct PoeFmtVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector3 Tangent,
    float Handedness,
    Vector2 Uv);

public sealed record PoeFmtShape(
    string Name,
    string MaterialPath,
    uint FirstIndex,
    uint IndexCount);

public sealed record PoeFmtAttachment(
    byte Flags,
    string Name,
    IReadOnlyList<Vector3> Points);

public sealed record PoeFmtMesh(
    byte Version,
    ushort LodVersion,
    uint Flags,
    PoeBounds Bounds,
    IReadOnlyList<PoeFmtVertex> Vertices,
    IReadOnlyList<uint> Indices,
    IReadOnlyList<PoeFmtShape> Shapes,
    IReadOnlyList<PoeFmtAttachment> Attachments);

public sealed record FmtValidationInfo(
    byte Version,
    ushort LodVersion,
    uint Flags,
    int VertexCount,
    int TriangleCount,
    IReadOnlyList<PoeFmtShape> Shapes,
    PoeBounds Bounds,
    int AttachmentCount);

/// <summary>
/// Reads, writes, inspects, and round-trip validates Path of Exile 3.29.3d static mesh <c>.fmt</c>
/// (version 9 <c>DOLm</c> lver=4, 24-byte vertices with flags <c>0x0238</c>) and matching <c>.fmt.header</c> files,
/// verified byte-for-byte against stock 3.29.3d <c>.fmt</c> and <c>.fmt.header</c> assets.
/// </summary>
public static class PoeFmtWriter
{
    public const byte FmtVersion = 9;
    public const uint DolmMagic = 0x6D4C4F44u; // "DOLm"
    public const ushort DefaultLodVersion = 4;
    public const uint DefaultVertexFlags = 0x00000238u; // pos(0x200) | tangent(0x20) | normal(0x10) | uv0(0x08)
    public const int DefaultVertexStride = 24;

    public static (byte[] FmtBytes, byte[] HeaderBytes) Write(
        IReadOnlyList<PoeFmtVertex> vertices,
        IReadOnlyList<uint> indices,
        IReadOnlyList<PoeFmtShape> shapes,
        PoeBounds? bounds = null,
        IReadOnlyList<PoeFmtAttachment>? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(shapes);

        if (vertices.Count == 0)
        {
            throw new ArgumentException("FMT must contain at least one vertex.", nameof(vertices));
        }

        if (indices.Count == 0 || indices.Count % 3 != 0)
        {
            throw new ArgumentException("FMT index count must be a positive multiple of 3.", nameof(indices));
        }

        if (shapes.Count == 0)
        {
            throw new ArgumentException("FMT must contain at least one shape.", nameof(shapes));
        }

        if (shapes.Count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(shapes), $"FMT shape count {shapes.Count} exceeds ushort.MaxValue.");
        }

        IReadOnlyList<PoeFmtAttachment> actualAttachments = attachments ?? [];
        int totalAttachmentPoints = 0;
        foreach (PoeFmtAttachment att in actualAttachments)
        {
            if (att.Points.Count > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(attachments), $"Attachment '{att.Name}' point count {att.Points.Count} exceeds 255.");
            }

            totalAttachmentPoints += att.Points.Count;
        }

        if (actualAttachments.Count > byte.MaxValue || totalAttachmentPoints > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(attachments), "FMT attachment count and total point count must each fit in a byte.");
        }

        PoeFmtVertex[] vertexArr = vertices is PoeFmtVertex[] arr ? arr : [.. vertices];
        PoeBounds actualBounds = bounds ?? ComputeBounds(vertexArr);

        uint totalShapeIndices = 0;
        foreach (PoeFmtShape s in shapes)
        {
            if ((ulong)s.FirstIndex + s.IndexCount > (ulong)indices.Count)
            {
                throw new ArgumentException($"Shape '{s.Name}' range [{s.FirstIndex}..{s.FirstIndex + s.IndexCount}) exceeds index count {indices.Count}.", nameof(shapes));
            }

            totalShapeIndices += s.IndexCount;
        }

        if (totalShapeIndices != (uint)indices.Count)
        {
            throw new ArgumentException($"Shape index counts sum to {totalShapeIndices}, expected {indices.Count}.", nameof(shapes));
        }

        byte[] fmtBytes = BuildFmtV9(vertexArr, indices, shapes, actualBounds, actualAttachments, (byte)totalAttachmentPoints);
        byte[] headerBytes = BuildFmtHeader(vertexArr.Length, indices.Count, shapes, actualBounds, actualAttachments);
        return (fmtBytes, headerBytes);
    }

    public static PoeBounds ComputeBounds(ReadOnlySpan<PoeFmtVertex> vertices)
    {
        if (vertices.IsEmpty)
        {
            return new PoeBounds(0, 0, 0, 0, 0, 0);
        }

        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;

        foreach (PoeFmtVertex v in vertices)
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

    /// <summary>
    /// Full binary reader for PoE 3.29.3d version 9 <c>DOLm</c> <c>.fmt</c> static meshes.
    /// Reads LOD 0 vertices/indices, shape/material submeshes, optional attachment entries, and bounding box.
    /// </summary>
    public static PoeFmtMesh Read(ReadOnlySpan<byte> fmtBytes)
    {
        if (fmtBytes.Length < 44)
        {
            throw new InvalidDataException($"FMT file too short ({fmtBytes.Length} bytes).");
        }

        byte version = fmtBytes[0];
        if (version != FmtVersion)
        {
            throw new InvalidDataException($"PoeFmtWriter.Read supports FMT v{FmtVersion} (DOLm); got v{version}. Use Inspect() for legacy v6-v8 headers.");
        }

        ushort shapeCount = BinaryPrimitives.ReadUInt16LittleEndian(fmtBytes.Slice(1, 2));
        byte d0Count = fmtBytes[3];
        byte d0TotalPoints = fmtBytes[4];

        var bounds = new PoeBounds(
            BinaryPrimitives.ReadSingleLittleEndian(fmtBytes.Slice(7, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(fmtBytes.Slice(11, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(fmtBytes.Slice(15, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(fmtBytes.Slice(19, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(fmtBytes.Slice(23, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(fmtBytes.Slice(27, 4)));

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(fmtBytes.Slice(31, 4));
        if (magic != DolmMagic)
        {
            throw new InvalidDataException($"Missing 'DOLm' magic in FMT v9 (got 0x{magic:X8}).");
        }

        ushort lodVer = BinaryPrimitives.ReadUInt16LittleEndian(fmtBytes.Slice(35, 2));
        byte levels = fmtBytes[37];
        ushort lodShapes = BinaryPrimitives.ReadUInt16LittleEndian(fmtBytes.Slice(38, 2));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(fmtBytes.Slice(40, 4));

        if (lodShapes != shapeCount)
        {
            throw new InvalidDataException($"FMT DOLm shape count ({lodShapes}) does not match header shape count ({shapeCount}).");
        }

        int pos = 44;
        var levelCounts = new (uint Tris, uint Verts)[levels];
        for (int lv = 0; lv < levels; lv++)
        {
            EnsureAvailable(fmtBytes, pos, 8, $"FMT DOLm level {lv} counts");
            uint tris = BinaryPrimitives.ReadUInt32LittleEndian(fmtBytes.Slice(pos, 4));
            uint verts = BinaryPrimitives.ReadUInt32LittleEndian(fmtBytes.Slice(pos + 4, 4));
            levelCounts[lv] = (tris, verts);
            pos += 8;
        }

        int stride = ComputeVertexStride(flags);
        var lod0Ranges = new (uint First, uint Count)[shapeCount];
        uint[] lod0Indices = [];
        PoeFmtVertex[] lod0Vertices = [];

        for (int lv = 0; lv < levels; lv++)
        {
            (uint tris, uint verts) = levelCounts[lv];
            EnsureAvailable(fmtBytes, pos, lodShapes * 8, $"FMT DOLm level {lv} shape ranges");
            uint rangeSum = 0;
            for (int s = 0; s < lodShapes; s++)
            {
                uint fi = BinaryPrimitives.ReadUInt32LittleEndian(fmtBytes.Slice(pos, 4));
                uint ic = BinaryPrimitives.ReadUInt32LittleEndian(fmtBytes.Slice(pos + 4, 4));
                if (lv == 0)
                {
                    lod0Ranges[s] = (fi, ic);
                }

                rangeSum += ic;
                pos += 8;
            }

            uint expectedIndices = checked(tris * 3u);
            if (rangeSum != expectedIndices)
            {
                throw new InvalidDataException($"FMT level {lv} shape index sum ({rangeSum}) != 3 * triangles ({expectedIndices}).");
            }

            int idxWidth = verts > 0xFFFFu ? 4 : 2;
            int idxBytes = checked((int)(expectedIndices * (uint)idxWidth));
            EnsureAvailable(fmtBytes, pos, idxBytes, $"FMT DOLm level {lv} indices");

            if (lv == 0)
            {
                lod0Indices = new uint[expectedIndices];
                for (int i = 0; i < lod0Indices.Length; i++)
                {
                    uint idx = idxWidth == 2
                        ? BinaryPrimitives.ReadUInt16LittleEndian(fmtBytes.Slice(pos + (i * 2), 2))
                        : BinaryPrimitives.ReadUInt32LittleEndian(fmtBytes.Slice(pos + (i * 4), 4));
                    if (idx >= verts)
                    {
                        throw new InvalidDataException($"FMT index {idx} out of range for {verts} vertices.");
                    }

                    lod0Indices[i] = idx;
                }
            }

            pos += idxBytes;

            int vertBytes = checked((int)(verts * (uint)stride));
            EnsureAvailable(fmtBytes, pos, vertBytes, $"FMT DOLm level {lv} vertices");

            if (lv == 0)
            {
                lod0Vertices = DecodeVertices(fmtBytes.Slice(pos, vertBytes), checked((int)verts), flags, stride);
            }

            pos += vertBytes;

            if (lodVer > 3)
            {
                EnsureAvailable(fmtBytes, pos, 4, $"FMT DOLm level {lv} lookup grid count");
                uint gridN = BinaryPrimitives.ReadUInt32LittleEndian(fmtBytes.Slice(pos, 4));
                int gridBytes = checked((int)(gridN * gridN * (uint)idxWidth));
                pos += 4;
                EnsureAvailable(fmtBytes, pos, gridBytes, $"FMT DOLm level {lv} lookup grid");
                pos += gridBytes;
            }
        }

        // Shape string offsets: shapeCount * (u32 nameCharOffset, u32 matCharOffset)
        EnsureAvailable(fmtBytes, pos, shapeCount * 8, "FMT shape string offsets");
        var shapeOffsets = new (uint NameOff, uint MatOff)[shapeCount];
        for (int s = 0; s < shapeCount; s++)
        {
            uint nameOff = BinaryPrimitives.ReadUInt32LittleEndian(fmtBytes.Slice(pos, 4));
            uint matOff = BinaryPrimitives.ReadUInt32LittleEndian(fmtBytes.Slice(pos + 4, 4));
            shapeOffsets[s] = (nameOff, matOff);
            pos += 8;
        }

        // Optional attachment (d0) records: d0Count * (byte flags, byte ptCount, u32 nameCharOffset) + d0TotalPoints * 12B float3
        EnsureAvailable(fmtBytes, pos, d0Count * 6, "FMT attachment headers");
        var d0Headers = new (byte Flags, byte PointCount, uint NameOff)[d0Count];
        int countedPoints = 0;
        for (int i = 0; i < d0Count; i++)
        {
            byte attFlags = fmtBytes[pos];
            byte ptCount = fmtBytes[pos + 1];
            uint nameOff = BinaryPrimitives.ReadUInt32LittleEndian(fmtBytes.Slice(pos + 2, 4));
            d0Headers[i] = (attFlags, ptCount, nameOff);
            countedPoints += ptCount;
            pos += 6;
        }

        if (countedPoints != d0TotalPoints)
        {
            throw new InvalidDataException($"FMT attachment point sum ({countedPoints}) != header d0 point count ({d0TotalPoints}).");
        }

        EnsureAvailable(fmtBytes, pos, d0TotalPoints * 12, "FMT attachment points");
        var d0PointLists = new Vector3[d0Count][];
        for (int i = 0; i < d0Count; i++)
        {
            int pc = d0Headers[i].PointCount;
            var pts = new Vector3[pc];
            for (int p = 0; p < pc; p++)
            {
                pts[p] = new Vector3(
                    BinaryPrimitives.ReadSingleLittleEndian(fmtBytes.Slice(pos, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(fmtBytes.Slice(pos + 4, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(fmtBytes.Slice(pos + 8, 4)));
                pos += 12;
            }

            d0PointLists[i] = pts;
        }

        // UTF-16LE NUL-terminated string table: u32 charCount + charCount * 2 bytes
        EnsureAvailable(fmtBytes, pos, 4, "FMT string table length");
        int stringChars = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(fmtBytes.Slice(pos, 4)));
        pos += 4;
        int stringBytes = checked(stringChars * 2);
        EnsureAvailable(fmtBytes, pos, stringBytes, "FMT string table payload");
        string stringTable = Encoding.Unicode.GetString(fmtBytes.Slice(pos, stringBytes));
        pos += stringBytes;

        if (pos != fmtBytes.Length)
        {
            throw new InvalidDataException($"FMT trailing bytes mismatch: consumed {pos} of {fmtBytes.Length} bytes.");
        }

        var shapes = new List<PoeFmtShape>(shapeCount);
        for (int s = 0; s < shapeCount; s++)
        {
            string shapeName = ReadNullTerminatedString(stringTable, checked((int)shapeOffsets[s].NameOff));
            string matPath = ReadNullTerminatedString(stringTable, checked((int)shapeOffsets[s].MatOff));
            (uint fi, uint ic) = levels > 0 ? lod0Ranges[s] : (0u, 0u);
            shapes.Add(new PoeFmtShape(shapeName, matPath, fi, ic));
        }

        var attachments = new List<PoeFmtAttachment>(d0Count);
        for (int i = 0; i < d0Count; i++)
        {
            string attName = ReadNullTerminatedString(stringTable, checked((int)d0Headers[i].NameOff));
            attachments.Add(new PoeFmtAttachment(d0Headers[i].Flags, attName, d0PointLists[i]));
        }

        return new PoeFmtMesh(
            version,
            lodVer,
            flags,
            bounds,
            lod0Vertices,
            lod0Indices,
            shapes,
            attachments);
    }

    /// <summary>
    /// Inspects any PoE 3.29.3d <c>.fmt</c> file (both v9 <c>DOLm</c> and legacy v6-v8 static meshes).
    /// </summary>
    public static FmtValidationInfo Inspect(ReadOnlySpan<byte> fmtBytes)
    {
        if (fmtBytes.Length < 39)
        {
            throw new InvalidDataException($"FMT file too short ({fmtBytes.Length} bytes).");
        }

        byte version = fmtBytes[0];
        if (version == FmtVersion)
        {
            PoeFmtMesh mesh = Read(fmtBytes);
            return new FmtValidationInfo(
                mesh.Version,
                mesh.LodVersion,
                mesh.Flags,
                mesh.Vertices.Count,
                mesh.Indices.Count / 3,
                mesh.Shapes,
                mesh.Bounds,
                mesh.Attachments.Count);
        }

        if (version is >= 6 and <= 8)
        {
            int tris = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(fmtBytes.Slice(1, 4)));
            int verts = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(fmtBytes.Slice(5, 4)));
            int shapeCount = BinaryPrimitives.ReadUInt16LittleEndian(fmtBytes.Slice(9, 2));
            byte d0Count = fmtBytes[11];
            var bounds = new PoeBounds(
                BinaryPrimitives.ReadSingleLittleEndian(fmtBytes.Slice(15, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(fmtBytes.Slice(19, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(fmtBytes.Slice(23, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(fmtBytes.Slice(27, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(fmtBytes.Slice(31, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(fmtBytes.Slice(35, 4)));

            var shapes = new List<PoeFmtShape>(shapeCount);
            for (int i = 0; i < shapeCount; i++)
            {
                shapes.Add(new PoeFmtShape($"shape_{i}", string.Empty, 0u, 0u));
            }

            return new FmtValidationInfo(version, 0, DefaultVertexFlags, verts, tris, shapes, bounds, d0Count);
        }

        throw new InvalidDataException($"Unsupported FMT version {version}.");
    }

    /// <summary>
    /// Round-trip parses and cross-checks a <c>.fmt</c> + <c>.fmt.header</c> pair against PoE 3.29.3d invariants.
    /// </summary>
    public static FmtValidationInfo Validate(ReadOnlySpan<byte> fmtBytes, ReadOnlySpan<byte> headerBytes)
    {
        PoeFmtMesh mesh = Read(fmtBytes);
        ValidateHeader(mesh, headerBytes);

        return new FmtValidationInfo(
            mesh.Version,
            mesh.LodVersion,
            mesh.Flags,
            mesh.Vertices.Count,
            mesh.Indices.Count / 3,
            mesh.Shapes,
            mesh.Bounds,
            mesh.Attachments.Count);
    }

    private static void ValidateHeader(PoeFmtMesh mesh, ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length < 77)
        {
            throw new InvalidDataException($".fmt.header too short ({headerBytes.Length} bytes).");
        }

        uint hdrVerts = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[0..4]);
        uint hdrIdxs = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[4..8]);
        byte hdrLodCount = headerBytes[36];
        uint hdrFlags = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.Slice(37, 4));
        uint hdrIdxStride = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.Slice(49, 4));
        ulong hdrD0Count = BinaryPrimitives.ReadUInt64LittleEndian(headerBytes.Slice(53, 8));

        uint expectedFlags = mesh.Flags | 0x200u;
        uint expectedIdxStride = mesh.Vertices.Count == 0 ? 0u : (mesh.Vertices.Count > 0xFFFF ? 4u : 2u);
        if (hdrVerts != (uint)mesh.Vertices.Count ||
            hdrIdxs != (uint)mesh.Indices.Count ||
            hdrLodCount == 0 ||
            hdrFlags != expectedFlags ||
            hdrIdxStride != expectedIdxStride ||
            hdrD0Count != (ulong)mesh.Attachments.Count)
        {
            throw new InvalidDataException(
                $".fmt.header mismatch: verts={hdrVerts}/{mesh.Vertices.Count}, idxs={hdrIdxs}/{mesh.Indices.Count}, " +
                $"flags=0x{hdrFlags:X}/0x{expectedFlags:X}, idxStride={hdrIdxStride}/{expectedIdxStride}, d0={hdrD0Count}/{mesh.Attachments.Count}.");
        }

        int pos = 61;
        for (ulong i = 0; i < hdrD0Count; i++)
        {
            EnsureAvailable(headerBytes, pos, 8, ".fmt.header attachment name length");
            int nameLen = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(headerBytes.Slice(pos, 8)));
            pos += 8;
            EnsureAvailable(headerBytes, pos, nameLen + 8, ".fmt.header attachment name & point count");
            pos += nameLen;
            int ptCount = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(headerBytes.Slice(pos, 8)));
            pos += 8;
            int ptBytes = checked(ptCount * 12);
            EnsureAvailable(headerBytes, pos, ptBytes, ".fmt.header attachment points");
            pos += ptBytes;
        }

        // 8 zero bytes + u64 drawEntryCount
        EnsureAvailable(headerBytes, pos, 16, ".fmt.header draw entry count");
        pos += 8;
        ulong drawEntries = BinaryPrimitives.ReadUInt64LittleEndian(headerBytes.Slice(pos, 8));
        pos += 8;

        uint drawIndexSum = 0;
        for (ulong i = 0; i < drawEntries; i++)
        {
            EnsureAvailable(headerBytes, pos, 16, ".fmt.header draw entry range & mat length");
            uint idxCount = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.Slice(pos, 4));
            uint firstIdx = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.Slice(pos + 4, 4));
            if ((ulong)firstIdx + idxCount > hdrIdxs)
            {
                throw new InvalidDataException($".fmt.header draw entry {i} range [{firstIdx}..{firstIdx + idxCount}) exceeds {hdrIdxs}.");
            }

            drawIndexSum += idxCount;
            pos += 8;

            int matLen = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(headerBytes.Slice(pos, 8)));
            pos += 8;
            EnsureAvailable(headerBytes, pos, matLen + 8, ".fmt.header material path & shape name length");
            pos += matLen;

            int shapeLen = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(headerBytes.Slice(pos, 8)));
            pos += 8;
            EnsureAvailable(headerBytes, pos, shapeLen, ".fmt.header shape name");
            pos += shapeLen;
        }

        if (drawEntries > 0 && drawIndexSum != hdrIdxs)
        {
            throw new InvalidDataException($".fmt.header draw entry index sum ({drawIndexSum}) != total indices ({hdrIdxs}).");
        }

        if (pos != headerBytes.Length)
        {
            throw new InvalidDataException($".fmt.header trailing bytes mismatch: consumed {pos} of {headerBytes.Length} bytes.");
        }
    }

    private static byte[] BuildFmtV9(
        PoeFmtVertex[] vertices,
        IReadOnlyList<uint> indices,
        IReadOnlyList<PoeFmtShape> shapes,
        PoeBounds bounds,
        IReadOnlyList<PoeFmtAttachment> attachments,
        byte totalAttachmentPoints)
    {
        // Build deduplicated NUL-terminated UTF-16LE string table (char offsets match stock 3.29.3d .fmt)
        var stringOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        var stringBuilder = new StringBuilder();

        uint InternString(string value)
        {
            if (stringOffsets.TryGetValue(value, out uint existing))
            {
                return existing;
            }

            uint offset = (uint)stringBuilder.Length;
            stringOffsets[value] = offset;
            stringBuilder.Append(value);
            stringBuilder.Append('\0');
            return offset;
        }

        var shapeOffsets = new (uint NameOff, uint MatOff)[shapes.Count];
        for (int i = 0; i < shapes.Count; i++)
        {
            uint nameOff = InternString(shapes[i].Name);
            uint matOff = InternString(shapes[i].MaterialPath);
            shapeOffsets[i] = (nameOff, matOff);
        }

        var attOffsets = new uint[attachments.Count];
        for (int i = 0; i < attachments.Count; i++)
        {
            attOffsets[i] = InternString(attachments[i].Name);
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // Header (31 bytes): version(1)=9, shapeCount(2), d0Count(1), d0TotalPoints(1), unk(2)=0, bounds[6](24)
        bw.Write(FmtVersion);
        bw.Write((ushort)shapes.Count);
        bw.Write((byte)attachments.Count);
        bw.Write(totalAttachmentPoints);
        bw.Write((ushort)0);
        bw.Write(bounds.MinX);
        bw.Write(bounds.MaxX);
        bw.Write(bounds.MinY);
        bw.Write(bounds.MaxY);
        bw.Write(bounds.MinZ);
        bw.Write(bounds.MaxZ);

        // DOLm block (13 bytes header): magic(4), lodVer(2)=4, levels(1)=1, lodShapes(2), flags(4)=0x0238
        bw.Write(DolmMagic);
        bw.Write(DefaultLodVersion);
        bw.Write((byte)1);
        bw.Write((ushort)shapes.Count);
        bw.Write(DefaultVertexFlags);

        // Level 0 counts: triangles, vertices
        bw.Write((uint)(indices.Count / 3));
        bw.Write((uint)vertices.Length);

        // Level 0 shape ranges: (firstIndex, indexCount)
        foreach (PoeFmtShape s in shapes)
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

        // Vertices (24 bytes each: pos float3, normal snorm8x4 w=-128, tangent snorm8x4 w=+/-127, uv0 half2)
        Span<byte> vbuf = stackalloc byte[DefaultVertexStride];
        foreach (PoeFmtVertex v in vertices)
        {
            vbuf.Clear();
            BinaryPrimitives.WriteSingleLittleEndian(vbuf[0..4], v.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(vbuf[4..8], v.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(vbuf[8..12], v.Position.Z);

            vbuf[12] = EncodeSnorm8(v.Normal.X);
            vbuf[13] = EncodeSnorm8(v.Normal.Y);
            vbuf[14] = EncodeSnorm8(v.Normal.Z);
            vbuf[15] = unchecked((byte)(sbyte)-128);

            vbuf[16] = EncodeSnorm8(v.Tangent.X);
            vbuf[17] = EncodeSnorm8(v.Tangent.Y);
            vbuf[18] = EncodeSnorm8(v.Tangent.Z);
            vbuf[19] = v.Handedness < 0f ? unchecked((byte)(sbyte)-127) : (byte)127;

            BinaryPrimitives.WriteUInt16LittleEndian(vbuf[20..22], BitConverter.HalfToUInt16Bits((Half)v.Uv.X));
            BinaryPrimitives.WriteUInt16LittleEndian(vbuf[22..24], BitConverter.HalfToUInt16Bits((Half)v.Uv.Y));
            bw.Write(vbuf);
        }

        // lver > 3 lookup grid count = 0
        bw.Write(0u);

        // Shape string offsets
        for (int i = 0; i < shapeOffsets.Length; i++)
        {
            bw.Write(shapeOffsets[i].NameOff);
            bw.Write(shapeOffsets[i].MatOff);
        }

        // Attachment (d0) headers + float3 point array
        for (int i = 0; i < attachments.Count; i++)
        {
            bw.Write(attachments[i].Flags);
            bw.Write((byte)attachments[i].Points.Count);
            bw.Write(attOffsets[i]);
        }

        foreach (PoeFmtAttachment att in attachments)
        {
            foreach (Vector3 pt in att.Points)
            {
                bw.Write(pt.X);
                bw.Write(pt.Y);
                bw.Write(pt.Z);
            }
        }

        // UTF-16LE string table: u32 charCount + UTF-16LE bytes
        string table = stringBuilder.ToString();
        bw.Write((uint)table.Length);
        bw.Write(Encoding.Unicode.GetBytes(table));

        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildFmtHeader(
        int vertexCount,
        int indexCount,
        IReadOnlyList<PoeFmtShape> shapes,
        PoeBounds bounds,
        IReadOnlyList<PoeFmtAttachment> attachments)
    {
        using var ms = new MemoryStream(256);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // Verified against stock 3.29.3d volcanobase05.fmt.header, azmerispiderwebs05_01.fmt.header, templarcrossbowbolt.fmt.header
        bw.Write((uint)vertexCount);
        bw.Write((uint)indexCount);
        bw.Write(0u);

        // .fmt.header bounds order: minX, minY, minZ, maxX, maxY, maxZ
        bw.Write(bounds.MinX);
        bw.Write(bounds.MinY);
        bw.Write(bounds.MinZ);
        bw.Write(bounds.MaxX);
        bw.Write(bounds.MaxY);
        bw.Write(bounds.MaxZ);

        bw.Write((byte)1); // LOD count
        bw.Write(DefaultVertexFlags); // 0x238
        bw.Write(0UL); // 8 zero bytes
        bw.Write(vertexCount > 0xFFFF ? 4u : 2u); // index stride

        // Attachment entries
        bw.Write((ulong)attachments.Count);
        foreach (PoeFmtAttachment att in attachments)
        {
            byte[] asciiName = Encoding.ASCII.GetBytes(att.Name);
            bw.Write((ulong)asciiName.Length);
            bw.Write(asciiName);
            bw.Write((ulong)att.Points.Count);
            foreach (Vector3 pt in att.Points)
            {
                bw.Write(pt.X);
                bw.Write(pt.Y);
                bw.Write(pt.Z);
            }
        }

        // 8 zero bytes + draw/shape entry table
        bw.Write(0UL);
        bw.Write((ulong)shapes.Count);
        foreach (PoeFmtShape s in shapes)
        {
            byte[] asciiMat = Encoding.ASCII.GetBytes(s.MaterialPath);
            byte[] asciiName = Encoding.ASCII.GetBytes(s.Name);

            // Note: .fmt.header stores (indexCount, firstIndex) in that order (verified on multi-shape stock .fmt.header!)
            bw.Write(s.IndexCount);
            bw.Write(s.FirstIndex);
            bw.Write((ulong)asciiMat.Length);
            bw.Write(asciiMat);
            bw.Write((ulong)asciiName.Length);
            bw.Write(asciiName);
        }

        bw.Flush();
        return ms.ToArray();
    }

    internal static int ComputeVertexStride(uint flags)
    {
        if (flags == 0)
        {
            return 0;
        }

        int stride = 12; // float3 position (implicit or 0x200)
        if ((flags & 0x10u) != 0) stride += 4; // normal snorm8x4
        if ((flags & 0x20u) != 0) stride += 4; // tangent snorm8x4
        if ((flags & 0x08u) != 0) stride += 4; // uv0 half2
        if ((flags & 0x04u) != 0) stride += 8; // bone indices + weights
        if ((flags & 0x02u) != 0) stride += 4; // vertex color rgba8
        if ((flags & 0x01u) != 0) stride += 4; // uv1 half2
        if ((flags & 0x40u) != 0) stride += 4; // extra attribute
        if ((flags & 0x80u) != 0) stride += 4; // extra attribute
        return stride;
    }

    private static PoeFmtVertex[] DecodeVertices(ReadOnlySpan<byte> buf, int vertexCount, uint flags, int stride)
    {
        var result = new PoeFmtVertex[vertexCount];
        if (vertexCount == 0 || stride < 12)
        {
            return result;
        }

        bool hasNormal = (flags & 0x10u) != 0;
        bool hasTangent = (flags & 0x20u) != 0;
        bool hasUv0 = (flags & 0x08u) != 0;

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
            }

            result[i] = new PoeFmtVertex(pos, normal, tangent, handedness, uv);
        }

        return result;
    }

    private static string ReadNullTerminatedString(string table, int charOffset)
    {
        if ((uint)charOffset >= (uint)table.Length)
        {
            throw new InvalidDataException($"FMT string table offset {charOffset} out of bounds (len={table.Length}).");
        }

        int nul = table.IndexOf('\0', charOffset);
        return nul < 0 ? table[charOffset..] : table[charOffset..nul];
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
