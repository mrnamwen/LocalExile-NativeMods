using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace LocalExile.Client.AssetPipeline.Mesh;

public sealed record GlbMaterial(
    string Name,
    string AlphaMode,
    bool DoubleSided,
    int? BaseColorImageIndex);

public sealed record GlbImage(
    string MimeType,
    byte[] Data);

public sealed record GlbPrimitive(
    string Name,
    int MaterialIndex,
    Vector3[] Positions,
    Vector3[] Normals,
    Vector2[] Uvs,
    uint[] Indices);

public sealed record GlbScene(
    IReadOnlyList<GlbPrimitive> Primitives,
    IReadOnlyList<GlbMaterial> Materials,
    IReadOnlyList<GlbImage> Images);

/// <summary>
/// Pure-C# glTF 2.0 binary (<c>.glb</c>) parser supporting JSON + BIN chunks, hierarchical node transforms
/// (both <c>matrix</c> and <c>translation</c>/<c>rotation</c>/<c>scale</c>), meshes, materials, and embedded images.
/// </summary>
public static class GlbParser
{
    private const uint GlbMagic = 0x46546C67u; // "glTF"
    private const uint JsonChunkType = 0x4E4F534Au; // "JSON"
    private const uint BinChunkType = 0x004E4942u; // "BIN\0"

    public static GlbScene ParseFile(string glbPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(glbPath);
        return Parse(File.ReadAllBytes(glbPath));
    }

    public static GlbScene Parse(ReadOnlySpan<byte> glbBytes)
    {
        if (glbBytes.Length < 20)
        {
            throw new InvalidDataException($"GLB file too short ({glbBytes.Length} bytes).");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes[..4]);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(4, 4));
        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(8, 4));

        if (magic != GlbMagic)
        {
            throw new InvalidDataException($"Invalid GLB magic 0x{magic:X8} (expected 'glTF').");
        }

        if (version != 2)
        {
            throw new InvalidDataException($"Unsupported glTF version {version} (expected 2).");
        }

        if (declaredLength > (uint)glbBytes.Length)
        {
            throw new InvalidDataException($"GLB declared length ({declaredLength}) exceeds actual length ({glbBytes.Length}).");
        }

        ReadOnlySpan<byte> jsonChunk = default;
        ReadOnlySpan<byte> binChunk = default;
        int pos = 12;
        while (pos + 8 <= declaredLength)
        {
            int chunkLen = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(pos, 4)));
            uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.Slice(pos + 4, 4));
            pos += 8;
            if (pos + chunkLen > declaredLength)
            {
                throw new InvalidDataException($"GLB chunk (0x{chunkType:X8}, {chunkLen} bytes) exceeds file bounds.");
            }

            if (chunkType == JsonChunkType)
            {
                jsonChunk = glbBytes.Slice(pos, chunkLen);
            }
            else if (chunkType == BinChunkType)
            {
                binChunk = glbBytes.Slice(pos, chunkLen);
            }

            pos += chunkLen;
        }

        if (jsonChunk.IsEmpty)
        {
            throw new InvalidDataException("GLB is missing the required JSON chunk.");
        }

        using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(jsonChunk));
        JsonElement root = doc.RootElement;

        var bufferViews = ParseBufferViews(root);
        var accessors = ParseAccessors(root);
        var images = ParseImages(root, bufferViews, binChunk);
        var textureSources = ParseTextureSources(root);
        var materials = ParseMaterials(root, textureSources);

        Matrix4x4[] worldTransforms = ComputeNodeWorldTransforms(root);
        var primitives = new List<GlbPrimitive>();

        if (root.TryGetProperty("nodes", out JsonElement nodesEl) &&
            root.TryGetProperty("meshes", out JsonElement meshesEl))
        {
            int nodeIdx = 0;
            foreach (JsonElement node in nodesEl.EnumerateArray())
            {
                if (node.TryGetProperty("mesh", out JsonElement meshIdxEl))
                {
                    int meshIdx = meshIdxEl.GetInt32();
                    JsonElement meshEl = meshesEl[meshIdx];
                    string meshName = meshEl.TryGetProperty("name", out JsonElement nEl)
                        ? nEl.GetString() ?? $"mesh_{meshIdx}"
                        : $"mesh_{meshIdx}";
                    Matrix4x4 world = worldTransforms[nodeIdx];
                    Matrix4x4 normalMatrix = ComputeNormalMatrix(world);

                    int primIdx = 0;
                    foreach (JsonElement primEl in meshEl.GetProperty("primitives").EnumerateArray())
                    {
                        string primName = primIdx == 0 ? meshName : $"{meshName}_{primIdx}";
                        int matIdx = primEl.TryGetProperty("material", out JsonElement mEl) ? mEl.GetInt32() : 0;
                        JsonElement attrs = primEl.GetProperty("attributes");

                        Vector3[] rawPos = ReadVec3Accessor(accessors[attrs.GetProperty("POSITION").GetInt32()], bufferViews, binChunk);
                        Vector3[] positions = new Vector3[rawPos.Length];
                        for (int i = 0; i < rawPos.Length; i++)
                        {
                            positions[i] = TransformColumnMajor(world, rawPos[i]);
                        }

                        uint[] indices;
                        if (primEl.TryGetProperty("indices", out JsonElement idxAccEl))
                        {
                            indices = ReadScalarIndices(accessors[idxAccEl.GetInt32()], bufferViews, binChunk);
                        }
                        else
                        {
                            indices = new uint[positions.Length];
                            for (uint i = 0; i < (uint)positions.Length; i++)
                            {
                                indices[i] = i;
                            }
                        }

                        Vector3[] normals;
                        if (attrs.TryGetProperty("NORMAL", out JsonElement normAccEl))
                        {
                            Vector3[] rawNorm = ReadVec3Accessor(accessors[normAccEl.GetInt32()], bufferViews, binChunk);
                            normals = new Vector3[rawNorm.Length];
                            for (int i = 0; i < rawNorm.Length; i++)
                            {
                                Vector3 n = TransformDirectionColumnMajor(normalMatrix, rawNorm[i]);
                                normals[i] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
                            }
                        }
                        else
                        {
                            normals = ComputeVertexNormals(positions, indices);
                        }

                        Vector2[] uvs;
                        if (attrs.TryGetProperty("TEXCOORD_0", out JsonElement uvAccEl))
                        {
                            uvs = ReadVec2Accessor(accessors[uvAccEl.GetInt32()], bufferViews, binChunk);
                        }
                        else
                        {
                            uvs = new Vector2[positions.Length];
                        }

                        primitives.Add(new GlbPrimitive(primName, matIdx, positions, normals, uvs, indices));
                        primIdx++;
                    }
                }

                nodeIdx++;
            }
        }

        if (primitives.Count == 0)
        {
            throw new InvalidDataException("GLB contains no mesh primitives.");
        }

        return new GlbScene(primitives, materials, images);
    }

    private sealed record BufferViewInfo(int ByteOffset, int ByteLength, int ByteStride);
    private sealed record AccessorInfo(int BufferView, int ByteOffset, int ComponentType, int Count, string Type, bool Normalized);

    private static BufferViewInfo[] ParseBufferViews(JsonElement root)
    {
        if (!root.TryGetProperty("bufferViews", out JsonElement el))
        {
            return [];
        }

        var list = new List<BufferViewInfo>();
        foreach (JsonElement bv in el.EnumerateArray())
        {
            int byteOffset = bv.TryGetProperty("byteOffset", out JsonElement bo) ? bo.GetInt32() : 0;
            int byteLength = bv.GetProperty("byteLength").GetInt32();
            int byteStride = bv.TryGetProperty("byteStride", out JsonElement bs) ? bs.GetInt32() : 0;
            list.Add(new BufferViewInfo(byteOffset, byteLength, byteStride));
        }

        return [.. list];
    }

    private static AccessorInfo[] ParseAccessors(JsonElement root)
    {
        if (!root.TryGetProperty("accessors", out JsonElement el))
        {
            return [];
        }

        var list = new List<AccessorInfo>();
        foreach (JsonElement a in el.EnumerateArray())
        {
            int bv = a.GetProperty("bufferView").GetInt32();
            int byteOffset = a.TryGetProperty("byteOffset", out JsonElement bo) ? bo.GetInt32() : 0;
            int componentType = a.GetProperty("componentType").GetInt32();
            int count = a.GetProperty("count").GetInt32();
            string type = a.GetProperty("type").GetString() ?? "SCALAR";
            bool normalized = a.TryGetProperty("normalized", out JsonElement norm) && norm.GetBoolean();
            list.Add(new AccessorInfo(bv, byteOffset, componentType, count, type, normalized));
        }

        return [.. list];
    }

    private static GlbImage[] ParseImages(JsonElement root, BufferViewInfo[] bufferViews, ReadOnlySpan<byte> binChunk)
    {
        if (!root.TryGetProperty("images", out JsonElement el))
        {
            return [];
        }

        var list = new List<GlbImage>();
        foreach (JsonElement img in el.EnumerateArray())
        {
            string mime = img.TryGetProperty("mimeType", out JsonElement m) ? m.GetString() ?? "application/octet-stream" : "application/octet-stream";
            if (img.TryGetProperty("bufferView", out JsonElement bvEl))
            {
                BufferViewInfo bv = bufferViews[bvEl.GetInt32()];
                byte[] data = binChunk.Slice(bv.ByteOffset, bv.ByteLength).ToArray();
                list.Add(new GlbImage(mime, data));
            }
            else
            {
                list.Add(new GlbImage(mime, []));
            }
        }

        return [.. list];
    }

    private static int[] ParseTextureSources(JsonElement root)
    {
        if (!root.TryGetProperty("textures", out JsonElement el))
        {
            return [];
        }

        var list = new List<int>();
        foreach (JsonElement tex in el.EnumerateArray())
        {
            list.Add(tex.TryGetProperty("source", out JsonElement s) ? s.GetInt32() : -1);
        }

        return [.. list];
    }

    private static GlbMaterial[] ParseMaterials(JsonElement root, int[] textureSources)
    {
        if (!root.TryGetProperty("materials", out JsonElement el))
        {
            return [new GlbMaterial("default", "OPAQUE", false, null)];
        }

        var list = new List<GlbMaterial>();
        int idx = 0;
        foreach (JsonElement mat in el.EnumerateArray())
        {
            string name = mat.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? $"mat_{idx}" : $"mat_{idx}";
            string alphaMode = mat.TryGetProperty("alphaMode", out JsonElement am) ? am.GetString() ?? "OPAQUE" : "OPAQUE";
            bool doubleSided = mat.TryGetProperty("doubleSided", out JsonElement ds) && ds.GetBoolean();
            int? baseColorImage = null;
            if (mat.TryGetProperty("pbrMetallicRoughness", out JsonElement pbr) &&
                pbr.TryGetProperty("baseColorTexture", out JsonElement bct) &&
                bct.TryGetProperty("index", out JsonElement ti))
            {
                int texIdx = ti.GetInt32();
                if ((uint)texIdx < (uint)textureSources.Length && textureSources[texIdx] >= 0)
                {
                    baseColorImage = textureSources[texIdx];
                }
            }

            list.Add(new GlbMaterial(name, alphaMode, doubleSided, baseColorImage));
            idx++;
        }

        return list.Count > 0 ? [.. list] : [new GlbMaterial("default", "OPAQUE", false, null)];
    }

    private static Matrix4x4[] ComputeNodeWorldTransforms(JsonElement root)
    {
        if (!root.TryGetProperty("nodes", out JsonElement nodesEl))
        {
            return [];
        }

        int count = nodesEl.GetArrayLength();
        Matrix4x4[] locals = new Matrix4x4[count];
        List<int>[] children = new List<int>[count];
        bool[] hasParent = new bool[count];

        for (int i = 0; i < count; i++)
        {
            JsonElement n = nodesEl[i];
            locals[i] = ReadNodeLocalMatrix(n);
            children[i] = [];
            if (n.TryGetProperty("children", out JsonElement ch))
            {
                foreach (JsonElement c in ch.EnumerateArray())
                {
                    int ci = c.GetInt32();
                    children[i].Add(ci);
                    hasParent[ci] = true;
                }
            }
        }

        Matrix4x4[] worlds = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            worlds[i] = Matrix4x4.Identity;
        }

        void Walk(int idx, Matrix4x4 parentWorld)
        {
            // glTF column-major composition: world = parent * local.
            // Because we store glTF column-major entries directly into Matrix4x4 fields,
            // MultiplyColumnMajor(parentWorld, locals[idx]) matches glTF exactly.
            Matrix4x4 world = MultiplyColumnMajor(parentWorld, locals[idx]);
            worlds[idx] = world;
            foreach (int child in children[idx])
            {
                Walk(child, world);
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (!hasParent[i])
            {
                Walk(i, Matrix4x4.Identity);
            }
        }

        return worlds;
    }

    private static Matrix4x4 ReadNodeLocalMatrix(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out JsonElement matEl))
        {
            float[] m = new float[16];
            int i = 0;
            foreach (JsonElement v in matEl.EnumerateArray())
            {
                m[i++] = (float)v.GetDouble();
            }

            // Store column-major entries directly: M11..M14 = col0, M21..M24 = col1, M31..M34 = col2, M41..M44 = col3.
            return new Matrix4x4(
                m[0], m[1], m[2], m[3],
                m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11],
                m[12], m[13], m[14], m[15]);
        }

        Vector3 t = Vector3.Zero;
        if (node.TryGetProperty("translation", out JsonElement tEl))
        {
            t = new Vector3((float)tEl[0].GetDouble(), (float)tEl[1].GetDouble(), (float)tEl[2].GetDouble());
        }

        Quaternion r = Quaternion.Identity;
        if (node.TryGetProperty("rotation", out JsonElement rEl))
        {
            r = Quaternion.Normalize(new Quaternion(
                (float)rEl[0].GetDouble(),
                (float)rEl[1].GetDouble(),
                (float)rEl[2].GetDouble(),
                (float)rEl[3].GetDouble()));
        }

        Vector3 s = Vector3.One;
        if (node.TryGetProperty("scale", out JsonElement sEl))
        {
            s = new Vector3((float)sEl[0].GetDouble(), (float)sEl[1].GetDouble(), (float)sEl[2].GetDouble());
        }

        // System.Numerics row-vector S * R * T has its memory layout identical to glTF column-major T * R * S!
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
    }

    private static Matrix4x4 MultiplyColumnMajor(Matrix4x4 a, Matrix4x4 b) =>
        // In column-major storage (each row of Matrix4x4 struct is a column of the math matrix),
        // math product A * B corresponds to System.Numerics row-major product b * a.
        b * a;

    private static Vector3 TransformColumnMajor(Matrix4x4 m, Vector3 v) =>
        // Column 0 is (M11,M12,M13), Col 1 is (M21,M22,M23), Col 2 is (M31,M32,M33), Col 3 is (M41,M42,M43).
        new(
            (m.M11 * v.X) + (m.M21 * v.Y) + (m.M31 * v.Z) + m.M41,
            (m.M12 * v.X) + (m.M22 * v.Y) + (m.M32 * v.Z) + m.M42,
            (m.M13 * v.X) + (m.M23 * v.Y) + (m.M33 * v.Z) + m.M43);

    private static Vector3 TransformDirectionColumnMajor(Matrix4x4 m, Vector3 v) =>
        new(
            (m.M11 * v.X) + (m.M21 * v.Y) + (m.M31 * v.Z),
            (m.M12 * v.X) + (m.M22 * v.Y) + (m.M32 * v.Z),
            (m.M13 * v.X) + (m.M23 * v.Y) + (m.M33 * v.Z));

    private static Matrix4x4 ComputeNormalMatrix(Matrix4x4 world)
    {
        var upper = new Matrix4x4(
            world.M11, world.M12, world.M13, 0,
            world.M21, world.M22, world.M23, 0,
            world.M31, world.M32, world.M33, 0,
            0, 0, 0, 1);
        return Matrix4x4.Invert(upper, out Matrix4x4 inv) ? Matrix4x4.Transpose(inv) : Matrix4x4.Identity;
    }

    private static Vector3[] ReadVec3Accessor(AccessorInfo acc, BufferViewInfo[] bvs, ReadOnlySpan<byte> bin)
    {
        BufferViewInfo bv = bvs[acc.BufferView];
        int stride = bv.ByteStride > 0 ? bv.ByteStride : 12;
        int start = bv.ByteOffset + acc.ByteOffset;
        Vector3[] result = new Vector3[acc.Count];
        for (int i = 0; i < acc.Count; i++)
        {
            int o = start + (i * stride);
            result[i] = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 4, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 8, 4)));
        }

        return result;
    }

    private static Vector2[] ReadVec2Accessor(AccessorInfo acc, BufferViewInfo[] bvs, ReadOnlySpan<byte> bin)
    {
        BufferViewInfo bv = bvs[acc.BufferView];
        int elemSize = acc.ComponentType switch
        {
            5126 => 8, // FLOAT
            5123 => 4, // UNSIGNED_SHORT
            5121 => 2, // UNSIGNED_BYTE
            _ => throw new InvalidDataException($"Unsupported TEXCOORD_0 componentType {acc.ComponentType}."),
        };
        int stride = bv.ByteStride > 0 ? bv.ByteStride : elemSize;
        int start = bv.ByteOffset + acc.ByteOffset;
        Vector2[] result = new Vector2[acc.Count];

        for (int i = 0; i < acc.Count; i++)
        {
            int o = start + (i * stride);
            result[i] = acc.ComponentType switch
            {
                5126 => new Vector2(
                    BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 4, 4))),
                5123 => new Vector2(
                    BinaryPrimitives.ReadUInt16LittleEndian(bin.Slice(o, 2)) / 65535f,
                    BinaryPrimitives.ReadUInt16LittleEndian(bin.Slice(o + 2, 2)) / 65535f),
                5121 => new Vector2(bin[o] / 255f, bin[o + 1] / 255f),
                _ => Vector2.Zero,
            };
        }

        return result;
    }

    private static uint[] ReadScalarIndices(AccessorInfo acc, BufferViewInfo[] bvs, ReadOnlySpan<byte> bin)
    {
        BufferViewInfo bv = bvs[acc.BufferView];
        int elemSize = acc.ComponentType switch
        {
            5121 => 1, // UNSIGNED_BYTE
            5123 => 2, // UNSIGNED_SHORT
            5125 => 4, // UNSIGNED_INT
            _ => throw new InvalidDataException($"Unsupported index componentType {acc.ComponentType}."),
        };
        int stride = bv.ByteStride > 0 ? bv.ByteStride : elemSize;
        int start = bv.ByteOffset + acc.ByteOffset;
        uint[] result = new uint[acc.Count];

        for (int i = 0; i < acc.Count; i++)
        {
            int o = start + (i * stride);
            result[i] = acc.ComponentType switch
            {
                5121 => bin[o],
                5123 => BinaryPrimitives.ReadUInt16LittleEndian(bin.Slice(o, 2)),
                5125 => BinaryPrimitives.ReadUInt32LittleEndian(bin.Slice(o, 4)),
                _ => 0u,
            };
        }

        return result;
    }

    private static Vector3[] ComputeVertexNormals(Vector3[] positions, uint[] indices)
    {
        Vector3[] accum = new Vector3[positions.Length];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            uint i0 = indices[i];
            uint i1 = indices[i + 1];
            uint i2 = indices[i + 2];
            Vector3 e1 = positions[i1] - positions[i0];
            Vector3 e2 = positions[i2] - positions[i0];
            Vector3 face = Vector3.Cross(e1, e2);
            accum[i0] += face;
            accum[i1] += face;
            accum[i2] += face;
        }

        for (int i = 0; i < accum.Length; i++)
        {
            accum[i] = accum[i].LengthSquared() > 1e-12f ? Vector3.Normalize(accum[i]) : Vector3.UnitY;
        }

        return accum;
    }
}
