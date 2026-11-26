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

public readonly record struct GlbSkinInfluence(
    ushort Joint0,
    ushort Joint1,
    ushort Joint2,
    ushort Joint3,
    float Weight0,
    float Weight1,
    float Weight2,
    float Weight3);

public sealed record GlbNode(
    int Index,
    string Name,
    int ParentIndex,
    Vector3 Translation,
    Quaternion Rotation,
    Vector3 Scale,
    Matrix4x4 LocalMatrix,
    Matrix4x4 WorldMatrix,
    int? MeshIndex,
    int? SkinIndex,
    IReadOnlyList<int> Children);

public sealed record GlbSkin(
    string Name,
    IReadOnlyList<int> JointNodeIndices,
    IReadOnlyList<Matrix4x4> InverseBindMatrices,
    int? SkeletonRootNodeIndex);

public sealed record GlbAnimationChannel(
    int TargetNodeIndex,
    string Path,
    string Interpolation,
    IReadOnlyList<float> InputTimes,
    IReadOnlyList<float> OutputValues);

public sealed record GlbAnimationClip(
    string Name,
    float DurationSeconds,
    IReadOnlyList<GlbAnimationChannel> Channels);

public sealed record GlbPrimitive(
    string Name,
    int MaterialIndex,
    Vector3[] Positions,
    Vector3[] Normals,
    Vector2[] Uvs,
    uint[] Indices,
    GlbSkinInfluence[]? SkinInfluences = null,
    int? SkinIndex = null);

public sealed record GlbScene(
    IReadOnlyList<GlbPrimitive> Primitives,
    IReadOnlyList<GlbMaterial> Materials,
    IReadOnlyList<GlbImage> Images,
    IReadOnlyList<GlbNode>? Nodes = null,
    IReadOnlyList<GlbSkin>? Skins = null,
    IReadOnlyList<GlbAnimationClip>? Animations = null);

/// <summary>
/// Pure-C# glTF 2.0 binary (<c>.glb</c>) parser supporting JSON + BIN chunks, hierarchical node transforms
/// (both <c>matrix</c> and <c>translation</c>/<c>rotation</c>/<c>scale</c>), meshes, materials, embedded images,
/// skeletal <c>skins</c> (<c>JOINTS_0</c>, <c>WEIGHTS_0</c>, <c>inverseBindMatrices</c>), and keyframe <c>animations</c>.
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
        var nodes = ParseNodes(root);
        var skins = ParseSkins(root, accessors, bufferViews, binChunk);
        var animations = ParseAnimations(root, accessors, bufferViews, binChunk);

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
                    int? skinIdx = node.TryGetProperty("skin", out JsonElement skinIdxEl)
                        ? skinIdxEl.GetInt32()
                        : null;
                    JsonElement meshEl = meshesEl[meshIdx];
                    string meshName = meshEl.TryGetProperty("name", out JsonElement nEl)
                        ? nEl.GetString() ?? $"mesh_{meshIdx}"
                        : $"mesh_{meshIdx}";

                    // Per glTF 2.0 spec 3.7.3.1: for skinned meshes, the mesh node's own transform is ignored
                    // (vertices are already in mesh space bound to joint transforms via inverseBindMatrices).
                    Matrix4x4 world = skinIdx.HasValue ? Matrix4x4.Identity : nodes[nodeIdx].WorldMatrix;
                    Matrix4x4 normalMatrix = skinIdx.HasValue ? Matrix4x4.Identity : ComputeNormalMatrix(world);

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
                            positions[i] = skinIdx.HasValue ? rawPos[i] : TransformColumnMajor(world, rawPos[i]);
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
                                Vector3 n = skinIdx.HasValue ? rawNorm[i] : TransformDirectionColumnMajor(normalMatrix, rawNorm[i]);
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

                        GlbSkinInfluence[]? skinInfluences = null;
                        if (attrs.TryGetProperty("JOINTS_0", out JsonElement jointsAccEl) &&
                            attrs.TryGetProperty("WEIGHTS_0", out JsonElement weightsAccEl))
                        {
                            (ushort, ushort, ushort, ushort)[] joints = ReadUShort4Accessor(accessors[jointsAccEl.GetInt32()], bufferViews, binChunk);
                            (float, float, float, float)[] weights = ReadVec4NormalizedAccessor(accessors[weightsAccEl.GetInt32()], bufferViews, binChunk);
                            int count = Math.Min(positions.Length, Math.Min(joints.Length, weights.Length));
                            skinInfluences = new GlbSkinInfluence[positions.Length];
                            for (int i = 0; i < count; i++)
                            {
                                skinInfluences[i] = new GlbSkinInfluence(
                                    joints[i].Item1,
                                    joints[i].Item2,
                                    joints[i].Item3,
                                    joints[i].Item4,
                                    weights[i].Item1,
                                    weights[i].Item2,
                                    weights[i].Item3,
                                    weights[i].Item4);
                            }

                            for (int i = count; i < positions.Length; i++)
                            {
                                skinInfluences[i] = new GlbSkinInfluence(0, 0, 0, 0, 1f, 0f, 0f, 0f);
                            }
                        }

                        primitives.Add(new GlbPrimitive(primName, matIdx, positions, normals, uvs, indices, skinInfluences, skinIdx));
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

        return new GlbScene(primitives, materials, images, nodes, skins, animations);
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

    private static GlbNode[] ParseNodes(JsonElement root)
    {
        if (!root.TryGetProperty("nodes", out JsonElement nodesEl))
        {
            return [];
        }

        int count = nodesEl.GetArrayLength();
        string[] names = new string[count];
        Vector3[] translations = new Vector3[count];
        Quaternion[] rotations = new Quaternion[count];
        Vector3[] scales = new Vector3[count];
        Matrix4x4[] locals = new Matrix4x4[count];
        int?[] meshIndices = new int?[count];
        int?[] skinIndices = new int?[count];
        List<int>[] children = new List<int>[count];
        int[] parents = new int[count];
        Array.Fill(parents, -1);

        for (int i = 0; i < count; i++)
        {
            JsonElement n = nodesEl[i];
            names[i] = n.TryGetProperty("name", out JsonElement nameEl)
                ? nameEl.GetString() ?? $"node_{i}"
                : $"node_{i}";
            meshIndices[i] = n.TryGetProperty("mesh", out JsonElement mEl) ? mEl.GetInt32() : null;
            skinIndices[i] = n.TryGetProperty("skin", out JsonElement sEl) ? sEl.GetInt32() : null;

            (translations[i], rotations[i], scales[i], locals[i]) = ReadNodeLocalTransform(n);

            children[i] = [];
            if (n.TryGetProperty("children", out JsonElement ch))
            {
                foreach (JsonElement c in ch.EnumerateArray())
                {
                    int ci = c.GetInt32();
                    children[i].Add(ci);
                    parents[ci] = i;
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
            Matrix4x4 world = MultiplyColumnMajor(parentWorld, locals[idx]);
            worlds[idx] = world;
            foreach (int child in children[idx])
            {
                Walk(child, world);
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (parents[i] < 0)
            {
                Walk(i, Matrix4x4.Identity);
            }
        }

        var result = new GlbNode[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = new GlbNode(
                i,
                names[i],
                parents[i],
                translations[i],
                rotations[i],
                scales[i],
                locals[i],
                worlds[i],
                meshIndices[i],
                skinIndices[i],
                children[i]);
        }

        return result;
    }

    private static GlbSkin[] ParseSkins(
        JsonElement root,
        AccessorInfo[] accessors,
        BufferViewInfo[] bufferViews,
        ReadOnlySpan<byte> binChunk)
    {
        if (!root.TryGetProperty("skins", out JsonElement skinsEl))
        {
            return [];
        }

        var list = new List<GlbSkin>();
        int idx = 0;
        foreach (JsonElement skinEl in skinsEl.EnumerateArray())
        {
            string name = skinEl.TryGetProperty("name", out JsonElement nEl)
                ? nEl.GetString() ?? $"skin_{idx}"
                : $"skin_{idx}";
            int? skeletonRoot = skinEl.TryGetProperty("skeleton", out JsonElement skEl)
                ? skEl.GetInt32()
                : null;

            var joints = new List<int>();
            foreach (JsonElement jEl in skinEl.GetProperty("joints").EnumerateArray())
            {
                joints.Add(jEl.GetInt32());
            }

            Matrix4x4[] ibm;
            if (skinEl.TryGetProperty("inverseBindMatrices", out JsonElement ibmAccEl))
            {
                ibm = ReadMat4Accessor(accessors[ibmAccEl.GetInt32()], bufferViews, binChunk);
            }
            else
            {
                ibm = new Matrix4x4[joints.Count];
                Array.Fill(ibm, Matrix4x4.Identity);
            }

            list.Add(new GlbSkin(name, joints, ibm, skeletonRoot));
            idx++;
        }

        return [.. list];
    }

    private static GlbAnimationClip[] ParseAnimations(
        JsonElement root,
        AccessorInfo[] accessors,
        BufferViewInfo[] bufferViews,
        ReadOnlySpan<byte> binChunk)
    {
        if (!root.TryGetProperty("animations", out JsonElement animsEl))
        {
            return [];
        }

        var list = new List<GlbAnimationClip>();
        int idx = 0;
        foreach (JsonElement animEl in animsEl.EnumerateArray())
        {
            string name = animEl.TryGetProperty("name", out JsonElement nEl)
                ? nEl.GetString() ?? $"clip_{idx}"
                : $"clip_{idx}";

            var samplers = new List<(int InputAcc, int OutputAcc, string Interpolation)>();
            if (animEl.TryGetProperty("samplers", out JsonElement samplersEl))
            {
                foreach (JsonElement sEl in samplersEl.EnumerateArray())
                {
                    int inputAcc = sEl.GetProperty("input").GetInt32();
                    int outputAcc = sEl.GetProperty("output").GetInt32();
                    string interp = sEl.TryGetProperty("interpolation", out JsonElement iEl)
                        ? iEl.GetString() ?? "LINEAR"
                        : "LINEAR";
                    samplers.Add((inputAcc, outputAcc, interp));
                }
            }

            var channels = new List<GlbAnimationChannel>();
            float maxTime = 0f;
            if (animEl.TryGetProperty("channels", out JsonElement channelsEl))
            {
                foreach (JsonElement chEl in channelsEl.EnumerateArray())
                {
                    int samplerIdx = chEl.GetProperty("sampler").GetInt32();
                    if ((uint)samplerIdx >= (uint)samplers.Count)
                    {
                        continue;
                    }

                    JsonElement targetEl = chEl.GetProperty("target");
                    if (!targetEl.TryGetProperty("node", out JsonElement nodeEl))
                    {
                        continue;
                    }

                    int targetNode = nodeEl.GetInt32();
                    string path = targetEl.GetProperty("path").GetString() ?? string.Empty;
                    var (inputAcc, outputAcc, interp) = samplers[samplerIdx];

                    float[] times = ReadFloatArrayAccessor(accessors[inputAcc], bufferViews, binChunk);
                    float[] values = ReadFloatArrayAccessor(accessors[outputAcc], bufferViews, binChunk);
                    if (times.Length > 0)
                    {
                        maxTime = MathF.Max(maxTime, times[^1]);
                    }

                    channels.Add(new GlbAnimationChannel(targetNode, path, interp, times, values));
                }
            }

            list.Add(new GlbAnimationClip(name, maxTime, channels));
            idx++;
        }

        return [.. list];
    }

    private static (Vector3 Translation, Quaternion Rotation, Vector3 Scale, Matrix4x4 LocalMatrix) ReadNodeLocalTransform(JsonElement node)
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
            // This matches System.Numerics row-vector layout (S * R * T) identically!
            var local = new Matrix4x4(
                m[0], m[1], m[2], m[3],
                m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11],
                m[12], m[13], m[14], m[15]);

            if (!Matrix4x4.Decompose(local, out Vector3 s, out Quaternion r, out Vector3 t))
            {
                s = Vector3.One;
                r = Quaternion.Identity;
                t = new Vector3(local.M41, local.M42, local.M43);
            }

            return (t, Quaternion.Normalize(r), s, local);
        }

        Vector3 trans = Vector3.Zero;
        if (node.TryGetProperty("translation", out JsonElement tEl))
        {
            trans = new Vector3((float)tEl[0].GetDouble(), (float)tEl[1].GetDouble(), (float)tEl[2].GetDouble());
        }

        Quaternion rot = Quaternion.Identity;
        if (node.TryGetProperty("rotation", out JsonElement rEl))
        {
            rot = Quaternion.Normalize(new Quaternion(
                (float)rEl[0].GetDouble(),
                (float)rEl[1].GetDouble(),
                (float)rEl[2].GetDouble(),
                (float)rEl[3].GetDouble()));
        }

        Vector3 scale = Vector3.One;
        if (node.TryGetProperty("scale", out JsonElement sEl))
        {
            scale = new Vector3((float)sEl[0].GetDouble(), (float)sEl[1].GetDouble(), (float)sEl[2].GetDouble());
        }

        // System.Numerics row-vector S * R * T has its memory layout identical to glTF column-major T * R * S!
        Matrix4x4 mat = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(trans);
        return (trans, rot, scale, mat);
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

    private static (ushort, ushort, ushort, ushort)[] ReadUShort4Accessor(AccessorInfo acc, BufferViewInfo[] bvs, ReadOnlySpan<byte> bin)
    {
        BufferViewInfo bv = bvs[acc.BufferView];
        int elemSize = acc.ComponentType switch
        {
            5121 => 4, // UNSIGNED_BYTE
            5123 => 8, // UNSIGNED_SHORT
            _ => throw new InvalidDataException($"Unsupported JOINTS_0 componentType {acc.ComponentType}."),
        };
        int stride = bv.ByteStride > 0 ? bv.ByteStride : elemSize;
        int start = bv.ByteOffset + acc.ByteOffset;
        var result = new (ushort, ushort, ushort, ushort)[acc.Count];

        for (int i = 0; i < acc.Count; i++)
        {
            int o = start + (i * stride);
            result[i] = acc.ComponentType switch
            {
                5121 => (bin[o], bin[o + 1], bin[o + 2], bin[o + 3]),
                5123 => (
                    BinaryPrimitives.ReadUInt16LittleEndian(bin.Slice(o, 2)),
                    BinaryPrimitives.ReadUInt16LittleEndian(bin.Slice(o + 2, 2)),
                    BinaryPrimitives.ReadUInt16LittleEndian(bin.Slice(o + 4, 2)),
                    BinaryPrimitives.ReadUInt16LittleEndian(bin.Slice(o + 6, 2))),
                _ => (0, 0, 0, 0),
            };
        }

        return result;
    }

    private static (float, float, float, float)[] ReadVec4NormalizedAccessor(AccessorInfo acc, BufferViewInfo[] bvs, ReadOnlySpan<byte> bin)
    {
        BufferViewInfo bv = bvs[acc.BufferView];
        int elemSize = acc.ComponentType switch
        {
            5126 => 16, // FLOAT
            5123 => 8,  // UNSIGNED_SHORT
            5121 => 4,  // UNSIGNED_BYTE
            _ => throw new InvalidDataException($"Unsupported WEIGHTS_0 componentType {acc.ComponentType}."),
        };
        int stride = bv.ByteStride > 0 ? bv.ByteStride : elemSize;
        int start = bv.ByteOffset + acc.ByteOffset;
        var result = new (float, float, float, float)[acc.Count];

        for (int i = 0; i < acc.Count; i++)
        {
            int o = start + (i * stride);
            result[i] = acc.ComponentType switch
            {
                5126 => (
                    BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 4, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 8, 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 12, 4))),
                5123 => (
                    BinaryPrimitives.ReadUInt16LittleEndian(bin.Slice(o, 2)) / 65535f,
                    BinaryPrimitives.ReadUInt16LittleEndian(bin.Slice(o + 2, 2)) / 65535f,
                    BinaryPrimitives.ReadUInt16LittleEndian(bin.Slice(o + 4, 2)) / 65535f,
                    BinaryPrimitives.ReadUInt16LittleEndian(bin.Slice(o + 6, 2)) / 65535f),
                5121 => (bin[o] / 255f, bin[o + 1] / 255f, bin[o + 2] / 255f, bin[o + 3] / 255f),
                _ => (1f, 0f, 0f, 0f),
            };
        }

        return result;
    }

    private static Matrix4x4[] ReadMat4Accessor(AccessorInfo acc, BufferViewInfo[] bvs, ReadOnlySpan<byte> bin)
    {
        BufferViewInfo bv = bvs[acc.BufferView];
        int stride = bv.ByteStride > 0 ? bv.ByteStride : 64;
        int start = bv.ByteOffset + acc.ByteOffset;
        var result = new Matrix4x4[acc.Count];
        for (int i = 0; i < acc.Count; i++)
        {
            int o = start + (i * stride);
            result[i] = new Matrix4x4(
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 0, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 4, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 8, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 12, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 16, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 20, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 24, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 28, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 32, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 36, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 40, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 44, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 48, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 52, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 56, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(o + 60, 4)));
        }

        return result;
    }

    private static float[] ReadFloatArrayAccessor(AccessorInfo acc, BufferViewInfo[] bvs, ReadOnlySpan<byte> bin)
    {
        int comps = acc.Type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            "MAT4" => 16,
            _ => throw new InvalidDataException($"Unsupported animation accessor type '{acc.Type}'."),
        };

        int compBytes = acc.ComponentType switch
        {
            5126 => 4, // FLOAT
            5120 => 1, // BYTE
            5121 => 1, // UNSIGNED_BYTE
            5122 => 2, // SHORT
            5123 => 2, // UNSIGNED_SHORT
            _ => throw new InvalidDataException($"Unsupported animation componentType {acc.ComponentType}."),
        };

        BufferViewInfo bv = bvs[acc.BufferView];
        int stride = bv.ByteStride > 0 ? bv.ByteStride : comps * compBytes;
        int start = bv.ByteOffset + acc.ByteOffset;
        float[] result = new float[acc.Count * comps];

        for (int i = 0; i < acc.Count; i++)
        {
            int o = start + (i * stride);
            for (int c = 0; c < comps; c++)
            {
                int co = o + (c * compBytes);
                result[(i * comps) + c] = acc.ComponentType switch
                {
                    5126 => BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(co, 4)),
                    5120 => Math.Max(unchecked((sbyte)bin[co]) / 127f, -1f),
                    5121 => bin[co] / 255f,
                    5122 => Math.Max(BinaryPrimitives.ReadInt16LittleEndian(bin.Slice(co, 2)) / 32767f, -1f),
                    5123 => BinaryPrimitives.ReadUInt16LittleEndian(bin.Slice(co, 2)) / 65535f,
                    _ => 0f,
                };
            }
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
