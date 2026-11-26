using System.Numerics;
using System.Text;
using System.Text.Json;

namespace LocalExile.Client.AssetPipeline.Mesh;

public sealed record PoeFmtMeshOptions
{
    public string ArtBasePath { get; init; } = "Art/Models/Mods/Custom";
    public string MetadataBasePath { get; init; } = "Metadata/Props/Mods/Custom";
    public string ModelBaseName { get; init; } = "model";
    public string ObjectBaseName { get; init; } = "CustomProp";
    /// <summary>
    /// Optional target longest horizontal span in PoE world units. When <see langword="null"/>, preserves glTF units.
    /// </summary>
    public float? TargetLongestHorizontalSpan { get; init; }
    /// <summary>
    /// Centers the mesh horizontally at X/Y=0 with its base at Z=0 when true (default true when scaling, false otherwise unless set).
    /// </summary>
    public bool CenterBase { get; init; }
    public bool EmitAoWrapper { get; init; } = true;
    public bool DoubleSidedAlphaBackFaces { get; init; } = true;
}

public sealed record PoeFmtAssetBundle(
    IReadOnlyDictionary<string, byte[]> Files,
    PoeBounds Bounds,
    int VertexCount,
    int TriangleCount,
    IReadOnlyList<PoeFmtShape> Shapes,
    IReadOnlyList<string> MaterialPaths,
    IReadOnlyList<string> TextureAlbedoPaths,
    string FlatNormalTexturePath,
    IReadOnlyList<int?> TextureBaseColorImageIndices,
    IReadOnlyList<bool> TextureIsAlphaBlend,
    string FmtVirtualPath,
    string? AoVirtualPath);

/// <summary>
/// Converts a parsed glTF 2.0 <c>.glb</c> static mesh scene into a native Path of Exile 3.29.3d static mesh asset set:
/// <c>.fmt</c> (v9 <c>DOLm</c> lver=4) + <c>.fmt.header</c>, BOM-less UTF-16LE CRLF <c>.mat</c> (v4 JSON) per shape/material,
/// and an optional BOM UTF-16LE CRLF <c>.ao</c> wrapper extending <c>Metadata/FmtParent</c>.
/// </summary>
public static class GlbToFmtConverter
{
    public static PoeFmtAssetBundle Convert(GlbScene scene, PoeFmtMeshOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        options ??= new PoeFmtMeshOptions();

        if (scene.Primitives.Count == 0)
        {
            throw new InvalidDataException("GLB scene contains no primitives.");
        }

        string artBase = options.ArtBasePath.TrimEnd('/');
        string metaBase = options.MetadataBasePath.TrimEnd('/');
        string flatNormalPath = $"{artBase}/textures/{options.ModelBaseName}_flat_normal.dds";

        (Vector3 gltfCenter, float scale) = ComputeTransform(scene.Primitives, options.TargetLongestHorizontalSpan, options.CenterBase);

        var allVertices = new List<PoeFmtVertex>();
        var allIndices = new List<uint>();
        var shapes = new List<PoeFmtShape>();
        var materialPaths = new List<string>();
        var albedoPaths = new List<string>();
        var baseColorImageIndices = new List<int?>();
        var isAlphaBlends = new List<bool>();
        var usedShapeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        for (int pIdx = 0; pIdx < scene.Primitives.Count; pIdx++)
        {
            GlbPrimitive prim = scene.Primitives[pIdx];
            GlbMaterial mat = (uint)prim.MaterialIndex < (uint)scene.Materials.Count
                ? scene.Materials[prim.MaterialIndex]
                : new GlbMaterial($"mat_{pIdx}", "OPAQUE", false, null);

            bool isAlphaBlend = string.Equals(mat.AlphaMode, "BLEND", StringComparison.OrdinalIgnoreCase);
            uint baseVertex = (uint)allVertices.Count;
            uint firstIndex = (uint)allIndices.Count;

            Vector3[] poePositions = new Vector3[prim.Positions.Length];
            Vector3[] poeNormals = new Vector3[prim.Normals.Length];
            for (int i = 0; i < prim.Positions.Length; i++)
            {
                Vector3 shifted = (prim.Positions[i] - gltfCenter) * scale;
                poePositions[i] = GlbToPoeMeshConverter.GltfToPoeAxes(shifted);
                Vector3 n = GlbToPoeMeshConverter.GltfToPoeAxes(prim.Normals[i]);
                poeNormals[i] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : new Vector3(0f, 0f, -1f);
            }

            (Vector3[] tangents, float[] handedness) = PoeMeshHelpers.ComputeTangents(poePositions, poeNormals, prim.Uvs, prim.Indices);

            for (int i = 0; i < poePositions.Length; i++)
            {
                allVertices.Add(new PoeFmtVertex(
                    poePositions[i],
                    poeNormals[i],
                    tangents[i],
                    handedness[i],
                    prim.Uvs[i]));
            }

            foreach (uint localIdx in prim.Indices)
            {
                allIndices.Add(baseVertex + localIdx);
            }

            if (options.DoubleSidedAlphaBackFaces && mat.DoubleSided && isAlphaBlend)
            {
                uint backBaseVertex = (uint)allVertices.Count;
                for (int i = 0; i < poePositions.Length; i++)
                {
                    allVertices.Add(new PoeFmtVertex(
                        poePositions[i],
                        -poeNormals[i],
                        tangents[i],
                        -handedness[i],
                        prim.Uvs[i]));
                }

                for (int i = 0; i + 2 < prim.Indices.Length; i += 3)
                {
                    allIndices.Add(backBaseVertex + prim.Indices[i]);
                    allIndices.Add(backBaseVertex + prim.Indices[i + 2]);
                    allIndices.Add(backBaseVertex + prim.Indices[i + 1]);
                }
            }

            string cleanName = PoeMeshHelpers.SanitizeIdentifier(mat.Name, $"shape{pIdx}");
            string shapeName = PoeMeshHelpers.MakeUniqueShapeName($"{cleanName}Shape", usedShapeNames);
            string texSuffix = isAlphaBlend ? "rgba" : "dxt1";
            string formatLabel = isAlphaBlend ? "Uncompressed" : "DXT1";
            string albedoPath = $"{artBase}/textures/{options.ModelBaseName}_{cleanName}_{texSuffix}.dds";
            string matPath = $"{artBase}/textures/{options.ModelBaseName}_{cleanName}.mat";

            uint emittedIndices = (uint)allIndices.Count - firstIndex;
            shapes.Add(new PoeFmtShape(shapeName, matPath, firstIndex, emittedIndices));

            if (!files.ContainsKey(matPath))
            {
                files[matPath] = PoeMeshHelpers.BuildMatJson(albedoPath, flatNormalPath, formatLabel, isAlphaBlend);
            }

            albedoPaths.Add(albedoPath);
            materialPaths.Add(matPath);
            baseColorImageIndices.Add(mat.BaseColorImageIndex);
            isAlphaBlends.Add(isAlphaBlend);
        }

        PoeBounds bounds = PoeFmtWriter.ComputeBounds([.. allVertices]);
        (byte[] fmtBytes, byte[] fmtHeaderBytes) = PoeFmtWriter.Write(allVertices, allIndices, shapes, bounds);
        PoeFmtWriter.Validate(fmtBytes, fmtHeaderBytes);

        string fmtPath = $"{artBase}/{options.ModelBaseName}.fmt";
        files[fmtPath] = fmtBytes;
        files[fmtPath + ".header"] = fmtHeaderBytes;

        string? aoPath = null;
        if (options.EmitAoWrapper)
        {
            aoPath = $"{metaBase}/{options.ObjectBaseName}.ao";
            files[aoPath] = BuildFixedMeshAo(fmtPath);
        }

        return new PoeFmtAssetBundle(
            files,
            bounds,
            allVertices.Count,
            allIndices.Count / 3,
            shapes,
            materialPaths,
            albedoPaths,
            flatNormalPath,
            baseColorImageIndices,
            isAlphaBlends,
            fmtPath,
            aoPath);
    }

    /// <summary>
    /// Builds a BOM UTF-16LE CRLF <c>.ao</c> wrapper referencing a static <c>.fmt</c> mesh,
    /// matching stock 3.29.3d <c>Metadata/FmtParent</c> doodads.
    /// </summary>
    public static byte[] BuildFixedMeshAo(string fmtVirtualPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fmtVirtualPath);
        string text =
            "version 3\r\n" +
            "extends \"Metadata/FmtParent\"\r\n\r\n" +
            "client\r\n{\r\n" +
            "\tFixedMesh\r\n\t{\r\n" +
            $"\t\tfixed_mesh = \"{fmtVirtualPath}\"\r\n" +
            "\t}\r\n" +
            "}\r\n";
        return PoeMeshHelpers.WriteUtf16LeWithBom(text);
    }

    internal static (Vector3 Center, float Scale) ComputeTransform(
        IReadOnlyList<GlbPrimitive> primitives,
        float? targetLongestHorizontalSpan,
        bool centerBase)
    {
        if (!targetLongestHorizontalSpan.HasValue && !centerBase)
        {
            return (Vector3.Zero, 1f);
        }

        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        foreach (GlbPrimitive prim in primitives)
        {
            foreach (Vector3 p in prim.Positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        if (!float.IsFinite(min.X))
        {
            return (Vector3.Zero, 1f);
        }

        Vector3 center = centerBase || targetLongestHorizontalSpan.HasValue
            ? new Vector3((min.X + max.X) * 0.5f, min.Y, (min.Z + max.Z) * 0.5f)
            : Vector3.Zero;

        float scale = 1f;
        if (targetLongestHorizontalSpan.HasValue)
        {
            float maxHoriz = MathF.Max(max.X - min.X, max.Z - min.Z);
            if (maxHoriz > 1e-5f)
            {
                scale = targetLongestHorizontalSpan.Value / maxHoriz;
            }
        }

        return (center, scale);
    }
}

internal static class PoeMeshHelpers
{
    private static readonly Encoding Utf16LeWithBom = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
    private static readonly Encoding Utf16LeNoBom = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);

    public static byte[] WriteUtf16LeWithBom(string text)
    {
        byte[] preamble = Utf16LeWithBom.GetPreamble();
        byte[] body = Utf16LeWithBom.GetBytes(text);
        byte[] output = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, output, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, output, preamble.Length, body.Length);
        return output;
    }

    public static byte[] WriteUtf16LeNoBom(string text) => Utf16LeNoBom.GetBytes(text);

    public static byte[] BuildMatJson(string albedoPath, string normalPath, string albedoFormat, bool alphaBlend)
    {
        object dielectricGraph = new
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
                        new { path = normalPath, format = "Uncompressed", srgb = false },
                        new { value = "SamplerDynamicWrap" },
                    },
                },
                new
                {
                    name = "UseRoughness",
                    parameters = new object[] { new { value = true } },
                },
            },
        };

        object[] graphs = alphaBlend
            ?
            [
                new { parent = "Metadata/Effects/Graphs/General/ForceAlpha.fxgraph" },
                dielectricGraph,
            ]
            : [dielectricGraph];

        object defaultGraph = alphaBlend
            ? new { version = 3, shader_group = new[] { "Material" } }
            : new { version = 3, overriden_blend_mode = "Opaque" };

        var matObj = new
        {
            version = 4,
            textures = new object[]
            {
                new { filename = normalPath, format = "Uncompressed", count = 1 },
                new { filename = albedoPath, format = albedoFormat, count = 1 },
            },
            defaultgraph = defaultGraph,
            graphinstances = graphs,
        };

        string json = JsonSerializer.Serialize(matObj) + "\r\n";
        return WriteUtf16LeNoBom(json);
    }

    public static (Vector3[] Tangents, float[] Handedness) ComputeTangents(
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

    public static string SanitizeIdentifier(string name, string fallback)
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

    public static string SanitizeAsciiName(string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var sb = new StringBuilder(Math.Min(name.Length, 255));
        foreach (char c in name)
        {
            if (c is >= (char)0x21 and <= (char)0x7E)
            {
                sb.Append(c);
                if (sb.Length >= 255)
                {
                    break;
                }
            }
            else if (c is ' ' or '\t')
            {
                sb.Append('_');
                if (sb.Length >= 255)
                {
                    break;
                }
            }
        }

        return sb.Length > 0 ? sb.ToString() : fallback;
    }

    public static string MakeUniqueShapeName(string baseShapeName, HashSet<string> used)
    {
        if (used.Add(baseShapeName))
        {
            return baseShapeName;
        }

        for (int suffix = 1; ; suffix++)
        {
            string candidate = $"{baseShapeName}{suffix}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
