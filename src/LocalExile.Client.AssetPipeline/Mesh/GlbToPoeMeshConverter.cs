using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace LocalExile.Client.AssetPipeline.Mesh;

public sealed record PoePetMeshOptions
{
    public string ArtBasePath { get; init; } = "Art/Models/Mods/Maxwell";
    public string MetadataBasePath { get; init; } = "Metadata/Pet/Mods/Maxwell";
    public string ObjectBaseName { get; init; } = "MaxwellPet";
    public string ModelBaseName { get; init; } = "maxwell";
    public float TargetLongestHorizontalSpan { get; init; } = 84f;
    public int PositionedScale { get; init; } = 100;
    public IReadOnlyList<SpinFloatClipSpec>? Clips { get; init; }
}

public sealed record PoePetAssetBundle(
    IReadOnlyDictionary<string, byte[]> Files,
    PoeBounds Bounds,
    int VertexCount,
    int TriangleCount,
    IReadOnlyList<string> ShapeNames,
    IReadOnlyList<string> MaterialPaths,
    IReadOnlyList<string> TextureAlbedoPaths,
    string FlatNormalTexturePath);

/// <summary>
/// Converts a parsed glTF 2.0 <c>.glb</c> scene into a complete, native Path of Exile 3.29.3d Pet asset set:
/// <c>.smd</c> + <c>.smd.header</c>, <c>.sm</c>, <c>.ast</c> (v7 spinning+floating skeleton), <c>.amd</c>,
/// <c>.mat</c> per shape, and <c>.ot</c>/<c>.ao</c>/<c>.act</c> (extending <c>Metadata/Pet/Pet</c> with zero extra serialized components).
/// </summary>
public static class GlbToPoeMeshConverter
{
    private static readonly Encoding Utf16LeWithBom = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
    private static readonly Encoding Utf16LeNoBom = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);

    public static PoePetAssetBundle Convert(GlbScene scene, PoePetMeshOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        options ??= new PoePetMeshOptions();

        // Compute global glTF bounds so multi-primitive models (body + whiskers) stay aligned when centered & scaled.
        Vector3 gltfMin = new(float.PositiveInfinity);
        Vector3 gltfMax = new(float.NegativeInfinity);
        foreach (GlbPrimitive prim in scene.Primitives)
        {
            foreach (Vector3 p in prim.Positions)
            {
                gltfMin = Vector3.Min(gltfMin, p);
                gltfMax = Vector3.Max(gltfMax, p);
            }
        }

        Vector3 gltfCenter = new((gltfMin.X + gltfMax.X) * 0.5f, gltfMin.Y, (gltfMin.Z + gltfMax.Z) * 0.5f);
        float maxHorizSpan = MathF.Max(gltfMax.X - gltfMin.X, gltfMax.Z - gltfMin.Z);
        float scale = maxHorizSpan > 1e-5f ? options.TargetLongestHorizontalSpan / maxHorizSpan : 1f;

        var allVertices = new List<PoeVertex>();
        var allIndices = new List<uint>();
        var shapes = new List<PoeShapeRange>();
        var materialPaths = new List<string>();
        var albedoPaths = new List<string>();
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        string flatNormalPath = $"{options.ArtBasePath}/textures/{options.ModelBaseName}_flat_normal.dds";

        for (int pIdx = 0; pIdx < scene.Primitives.Count; pIdx++)
        {
            GlbPrimitive prim = scene.Primitives[pIdx];
            GlbMaterial mat = (uint)prim.MaterialIndex < (uint)scene.Materials.Count
                ? scene.Materials[prim.MaterialIndex]
                : new GlbMaterial($"mat_{pIdx}", "OPAQUE", false, null);

            uint baseVertex = (uint)allVertices.Count;
            uint firstIndex = (uint)allIndices.Count;

            // Rigid rotation (det = +1) from glTF Y-up (x, y, z) -> PoE Z-down (-z, x, -y):
            // preserves triangle winding, normals, and tangent handedness with zero mirroring!
            Vector3[] poePositions = new Vector3[prim.Positions.Length];
            Vector3[] poeNormals = new Vector3[prim.Normals.Length];
            for (int i = 0; i < prim.Positions.Length; i++)
            {
                Vector3 shifted = (prim.Positions[i] - gltfCenter) * scale;
                poePositions[i] = GltfToPoeAxes(shifted);
                Vector3 n = GltfToPoeAxes(prim.Normals[i]);
                poeNormals[i] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : new Vector3(0f, 0f, -1f);
            }

            (Vector3[] tangents, float[] handedness) = ComputeTangents(poePositions, poeNormals, prim.Uvs, prim.Indices);

            for (int i = 0; i < poePositions.Length; i++)
            {
                allVertices.Add(new PoeVertex(
                    poePositions[i],
                    poeNormals[i],
                    tangents[i],
                    handedness[i],
                    prim.Uvs[i],
                    Bone0: 0,
                    Weight0: 255));
            }

            foreach (uint localIdx in prim.Indices)
            {
                allIndices.Add(baseVertex + localIdx);
            }

            string cleanName = SanitizeIdentifier(mat.Name, $"shape{pIdx}");
            string shapeName = $"{cleanName}Shape";
            shapes.Add(new PoeShapeRange(shapeName, firstIndex, (uint)prim.Indices.Length));

            bool isAlphaBlend = string.Equals(mat.AlphaMode, "BLEND", StringComparison.OrdinalIgnoreCase);
            string texSuffix = isAlphaBlend ? "rgba" : "dxt1";
            string formatLabel = isAlphaBlend ? "RGBA8" : "DXT1";
            string albedoPath = $"{options.ArtBasePath}/textures/{options.ModelBaseName}_{cleanName}_{texSuffix}.dds";
            string matPath = $"{options.ArtBasePath}/textures/{options.ModelBaseName}_{cleanName}.mat";

            albedoPaths.Add(albedoPath);
            materialPaths.Add(matPath);
            files[matPath] = BuildMatJson(albedoPath, flatNormalPath, formatLabel, isAlphaBlend);
        }

        PoeBounds bounds = PoeBounds.Compute([.. allVertices]);
        (byte[] smdBytes, byte[] smdHeaderBytes) = PoeSmdWriter.Write(allVertices, allIndices, shapes, bounds);
        PoeSmdWriter.Validate(smdBytes, smdHeaderBytes);

        byte[] astBytes = PoeAstWriter.BuildSpinFloatAst(options.Clips);
        PoeAstWriter.ValidateV7(astBytes);
        byte[] amdBytes = PoeAstWriter.BuildAmd(options.Clips);

        string smdPath = $"{options.ArtBasePath}/{options.ModelBaseName}.smd";
        string smPath = $"{options.ArtBasePath}/{options.ModelBaseName}.sm";
        string astPath = $"{options.ArtBasePath}/{options.ModelBaseName}.ast";
        string amdPath = $"{options.ArtBasePath}/{options.ModelBaseName}.amd";

        string otPath = $"{options.MetadataBasePath}/{options.ObjectBaseName}.ot";
        string aoPath = $"{options.MetadataBasePath}/{options.ObjectBaseName}.ao";
        string actPath = $"{options.MetadataBasePath}/{options.ObjectBaseName}.act";

        files[smdPath] = smdBytes;
        files[smdPath + ".header"] = smdHeaderBytes;
        files[smPath] = BuildSm(smdPath, materialPaths, bounds);
        files[astPath] = astBytes;
        files[amdPath] = amdBytes;

        files[otPath] = BuildPetOt(aoPath, actPath, options.PositionedScale);
        files[aoPath] = BuildPetAo(amdPath, astPath, smPath);
        files[actPath] = BuildPetAct(aoPath);

        return new PoePetAssetBundle(
            files,
            bounds,
            allVertices.Count,
            allIndices.Count / 3,
            [.. shapes.Select(s => s.Name)],
            materialPaths,
            albedoPaths,
            flatNormalPath);
    }

    /// <summary>
    /// Proper rigid rotation (<c>det = +1</c>) from glTF Y-up <c>(x, y, z)</c> to PoE Z-down <c>(-z, x, -y)</c>.
    /// </summary>
    public static Vector3 GltfToPoeAxes(Vector3 v) => new(-v.Z, v.X, -v.Y);

    private static byte[] BuildSm(string smdPath, List<string> materialPaths, PoeBounds bounds)
    {
        var sb = new StringBuilder();
        sb.Append("version 6\r\n");
        sb.Append("SkinnedMeshData \"").Append(smdPath).Append("\"\r\n");
        sb.Append("Materials ").Append(materialPaths.Count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        foreach (string mat in materialPaths)
        {
            sb.Append("\t\"").Append(mat).Append("\" 1\r\n");
        }

        static float CleanZero(float v) => v == 0f ? 0f : v;
        sb.Append(FormattableString.Invariant(
            $"BoundingBox {CleanZero(bounds.MinX):0.####} {CleanZero(bounds.MinY):0.####} {CleanZero(bounds.MinZ):0.####} {CleanZero(bounds.MaxX):0.####} {CleanZero(bounds.MaxY):0.####} {CleanZero(bounds.MaxZ):0.####}\r\n"));
        sb.Append("BoneGroups 0\r\n");
        return Utf16LeNoBom.GetBytes(sb.ToString());
    }

    private static byte[] BuildMatJson(string albedoPath, string normalPath, string albedoFormat, bool alphaBlend)
    {
        object[] graphs = alphaBlend
            ?
            [
                new { parent = "Metadata/Effects/Graphs/General/ForceAlpha.fxgraph" },
                new
                {
                    parent = "Metadata/Materials/DielectricSpecGloss.fxgraph",
                    custom_parameters = new object[]
                    {
                        new
                        {
                            name = "AlbedoTransparency_TEX",
                            parameters = new object[]
                            {
                                new { path = albedoPath, srgb = true },
                                new { value = "SamplerDynamicWrap" },
                            },
                        },
                        new
                        {
                            name = "NormalGlossAO_TEX",
                            parameters = new object[]
                            {
                                new { path = normalPath, srgb = false },
                                new { value = "SamplerDynamicWrap" },
                            },
                        },
                        new
                        {
                            name = "UseRoughness",
                            parameters = new object[] { new { value = true } },
                        },
                    },
                },
            ]
            :
            [
                new
                {
                    parent = "Metadata/Materials/DielectricSpecGloss.fxgraph",
                    custom_parameters = new object[]
                    {
                        new
                        {
                            name = "AlbedoTransparency_TEX",
                            parameters = new object[]
                            {
                                new { path = albedoPath, format = albedoFormat, srgb = true },
                                new { value = "SamplerDynamicWrap" },
                            },
                        },
                        new
                        {
                            name = "NormalGlossAO_TEX",
                            parameters = new object[]
                            {
                                new { path = normalPath, srgb = false },
                                new { value = "SamplerDynamicWrap" },
                            },
                        },
                        new
                        {
                            name = "UseRoughness",
                            parameters = new object[] { new { value = true } },
                        },
                    },
                },
            ];

        object defaultGraph = alphaBlend
            ? new { version = 3, shader_group = new[] { "Material" } }
            : new { version = 3, overriden_blend_mode = "Opaque" };

        var matObj = new
        {
            version = 4,
            textures = new object[]
            {
                new { filename = normalPath, format = "RGBA8", count = 1 },
                new { filename = albedoPath, format = albedoFormat, count = 1 },
            },
            defaultgraph = defaultGraph,
            graphinstances = graphs,
        };

        string json = JsonSerializer.Serialize(matObj);
        return Utf16LeNoBom.GetBytes(json);
    }

    private static byte[] BuildPetOt(string aoPath, string actPath, int scale)
    {
        // Strictly extends "Metadata/Pet/Pet" with ZERO extra serialized components so 0x02bb PetObject deserialization matches exactly.
        string text =
            $"version 3\r\n" +
            $"extends \"Metadata/Pet/Pet\"\r\n\r\n" +
            $"Positioned\r\n{{\r\n\tscale = {scale.ToString(CultureInfo.InvariantCulture)}\r\n}}\r\n\r\n" +
            $"Animated\r\n{{\r\n\tanimated_object = \"{aoPath}\"\r\n}}\r\n\r\n" +
            $"Actor\r\n{{\r\n\tactor = \"{actPath}\"\r\n}}\r\n";
        return WriteUtf16LeWithBom(text);
    }

    private static byte[] BuildPetAo(string amdPath, string astPath, string smPath)
    {
        string text =
            $"version 3\r\n" +
            $"extends \"Metadata/Parent\"\r\n\r\n" +
            $"AnimationController\r\n{{\r\n\tmetadata = \"{amdPath}\"\r\n}}\r\n\r\n" +
            $"client\r\n{{\r\n" +
            $"\tClientAnimationController\r\n\t{{\r\n\t\tskeleton = \"{astPath}\"\r\n\t}}\r\n\r\n" +
            $"\tSkinMesh\r\n\t{{\r\n\t\tskin = \"{smPath}\"\r\n\t}}\r\n" +
            $"}}\r\n";
        return WriteUtf16LeWithBom(text);
    }

    private static byte[] BuildPetAct(string aoPath)
    {
        string text =
            $"animated_object \"{aoPath}\"\r\n" +
            $"{{\r\n" +
            $"\tIdle = \"idle\";\r\n" +
            $"\tRun = \"run\";\r\n" +
            $"\tEmerge = \"happy\" effect_detached \"Metadata/Effects/Microtransactions/pets/summon/summon.ao\" \"release\";\r\n" +
            $"}}\r\n" +
            $"movement_speed 375\r\n" +
            $"action_set minion\r\n";
        return WriteUtf16LeWithBom(text);
    }

    private static byte[] WriteUtf16LeWithBom(string text)
    {
        byte[] preamble = Utf16LeWithBom.GetPreamble();
        byte[] body = Utf16LeWithBom.GetBytes(text);
        byte[] output = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, output, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, output, preamble.Length, body.Length);
        return output;
    }

    private static (Vector3[] Tangents, float[] Handedness) ComputeTangents(
        Vector3[] positions,
        Vector3[] normals,
        Vector2[] uvs,
        uint[] indices)
    {
        int n = positions.Length;
        Vector3[] tan1 = new Vector3[n];
        Vector3[] tan2 = new Vector3[n];

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            uint i0 = indices[i];
            uint i1 = indices[i + 1];
            uint i2 = indices[i + 2];

            Vector3 e1 = positions[i1] - positions[i0];
            Vector3 e2 = positions[i2] - positions[i0];
            Vector2 d1 = uvs[i1] - uvs[i0];
            Vector2 d2 = uvs[i2] - uvs[i0];

            float det = (d1.X * d2.Y) - (d2.X * d1.Y);
            if (MathF.Abs(det) < 1e-8f)
            {
                continue;
            }

            float invDet = 1f / det;
            Vector3 sdir = ((e1 * d2.Y) - (e2 * d1.Y)) * invDet;
            Vector3 tdir = ((e2 * d1.X) - (e1 * d2.X)) * invDet;

            tan1[i0] += sdir;
            tan1[i1] += sdir;
            tan1[i2] += sdir;
            tan2[i0] += tdir;
            tan2[i1] += tdir;
            tan2[i2] += tdir;
        }

        Vector3[] tangents = new Vector3[n];
        float[] handedness = new float[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 norm = normals[i];
            Vector3 t = tan1[i];
            Vector3 ortho = t - (norm * Vector3.Dot(norm, t));
            if (ortho.LengthSquared() > 1e-8f)
            {
                tangents[i] = Vector3.Normalize(ortho);
                handedness[i] = Vector3.Dot(Vector3.Cross(norm, tangents[i]), tan2[i]) < 0f ? -1f : 1f;
            }
            else
            {
                Vector3 up = MathF.Abs(norm.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
                tangents[i] = Vector3.Normalize(Vector3.Cross(up, norm));
                handedness[i] = 1f;
            }
        }

        return (tangents, handedness);
    }

    private static string SanitizeIdentifier(string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.Length > 0 ? sb.ToString() : fallback;
    }
}
