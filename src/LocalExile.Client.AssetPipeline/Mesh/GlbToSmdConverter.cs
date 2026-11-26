using System.Globalization;
using System.Numerics;
using System.Text;

namespace LocalExile.Client.AssetPipeline.Mesh;

public sealed record PoeSmdMeshOptions
{
    public string ArtBasePath { get; init; } = "Art/Models/Mods/Custom";
    public string MetadataBasePath { get; init; } = "Metadata/Models/Mods/Custom";
    public string ModelBaseName { get; init; } = "rig";
    public string ObjectBaseName { get; init; } = "CustomRig";
    public int SkinIndex { get; init; }
    public byte AnimationFps { get; init; } = 30;
    public byte DefaultPlayback { get; init; } = (byte)'l';
    /// <summary>
    /// Optional target longest horizontal span in PoE world units. When <see langword="null"/>, preserves glTF units.
    /// </summary>
    public float? TargetLongestHorizontalSpan { get; init; }
    public bool CenterBase { get; init; }
    public bool EmitAoWrapper { get; init; } = true;
    public bool DoubleSidedAlphaBackFaces { get; init; } = true;
}

public sealed record PoeSmdAssetBundle(
    IReadOnlyDictionary<string, byte[]> Files,
    PoeBounds Bounds,
    int VertexCount,
    int TriangleCount,
    int BoneCount,
    IReadOnlyList<string> ShapeNames,
    IReadOnlyList<string> AnimationNames,
    IReadOnlyList<string> MaterialPaths,
    IReadOnlyList<string> TextureAlbedoPaths,
    string FlatNormalTexturePath,
    IReadOnlyList<int?> TextureBaseColorImageIndices,
    IReadOnlyList<bool> TextureIsAlphaBlend,
    string SmdVirtualPath,
    string SmVirtualPath,
    string AstVirtualPath,
    string AmdVirtualPath,
    string? AoVirtualPath);

/// <summary>
/// Converts a rigged glTF 2.0 <c>.glb</c> scene (<c>JOINTS_0</c>, <c>WEIGHTS_0</c>, <c>skins</c>, <c>animations</c>)
/// into a complete native Path of Exile 3.29.3d skinned asset set:
/// <c>.smd</c> + <c>.smd.header</c>, multi-bone <c>.ast</c> (v7), BOM-less UTF-16LE CRLF <c>.sm</c>/<c>.mat</c>/<c>.amd</c>,
/// and an optional BOM UTF-16LE CRLF <c>.ao</c> wrapper.
/// </summary>
public static class GlbToSmdConverter
{
    public static PoeSmdAssetBundle Convert(GlbScene scene, PoeSmdMeshOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        options ??= new PoeSmdMeshOptions();

        if (scene.Primitives.Count == 0)
        {
            throw new InvalidDataException("GLB scene contains no primitives.");
        }

        string artBase = options.ArtBasePath.TrimEnd('/');
        string metaBase = options.MetadataBasePath.TrimEnd('/');
        string flatNormalPath = $"{artBase}/textures/{options.ModelBaseName}_flat_normal.dds";

        (Vector3 gltfCenter, float scale) = GlbToFmtConverter.ComputeTransform(
            scene.Primitives,
            options.TargetLongestHorizontalSpan,
            options.CenterBase);

        GlbAstConversionResult astResult = GlbToAstConverter.Convert(scene, new GlbToAstOptions
        {
            SkinIndex = options.SkinIndex,
            DefaultFps = options.AnimationFps,
            DefaultPlayback = options.DefaultPlayback,
            Scale = scale,
            GltfCenterOffset = gltfCenter,
        });

        IReadOnlyList<int> jointMap = astResult.GltfJointToPoeBoneMap;
        int boneCount = astResult.Skeleton.Bones.Count;

        var allVertices = new List<PoeVertex>();
        var allIndices = new List<uint>();
        var shapes = new List<PoeShapeRange>();
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
                var (b0, b1, b2, b3, w0, w1, w2, w3) = ResolveVertexSkin(prim.SkinInfluences, i, jointMap, boneCount);
                allVertices.Add(new PoeVertex(
                    poePositions[i],
                    poeNormals[i],
                    tangents[i],
                    handedness[i],
                    prim.Uvs[i],
                    b0, w0, b1, w1, b2, w2, b3, w3));
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
                    var (b0, b1, b2, b3, w0, w1, w2, w3) = ResolveVertexSkin(prim.SkinInfluences, i, jointMap, boneCount);
                    allVertices.Add(new PoeVertex(
                        poePositions[i],
                        -poeNormals[i],
                        tangents[i],
                        -handedness[i],
                        prim.Uvs[i],
                        b0, w0, b1, w1, b2, w2, b3, w3));
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
            uint emittedIndices = (uint)allIndices.Count - firstIndex;
            shapes.Add(new PoeShapeRange(shapeName, firstIndex, emittedIndices));

            string texSuffix = isAlphaBlend ? "rgba" : "dxt1";
            string formatLabel = isAlphaBlend ? "Uncompressed" : "DXT1";
            string albedoPath = $"{artBase}/textures/{options.ModelBaseName}_{cleanName}_{texSuffix}.dds";
            string matPath = $"{artBase}/textures/{options.ModelBaseName}_{cleanName}.mat";

            if (!files.ContainsKey(matPath))
            {
                files[matPath] = PoeMeshHelpers.BuildMatJson(albedoPath, flatNormalPath, formatLabel, isAlphaBlend);
            }

            albedoPaths.Add(albedoPath);
            materialPaths.Add(matPath);
            baseColorImageIndices.Add(mat.BaseColorImageIndex);
            isAlphaBlends.Add(isAlphaBlend);
        }

        PoeBounds bounds = PoeBounds.Compute([.. allVertices]);
        (byte[] smdBytes, byte[] smdHeaderBytes) = PoeSmdWriter.Write(allVertices, allIndices, shapes, bounds);
        PoeSmdWriter.Validate(smdBytes, smdHeaderBytes);

        string smdPath = $"{artBase}/{options.ModelBaseName}.smd";
        string smPath = $"{artBase}/{options.ModelBaseName}.sm";
        string astPath = $"{artBase}/{options.ModelBaseName}.ast";
        string amdPath = $"{artBase}/{options.ModelBaseName}.amd";

        files[smdPath] = smdBytes;
        files[smdPath + ".header"] = smdHeaderBytes;
        files[smPath] = BuildSm(smdPath, materialPaths, bounds);
        files[astPath] = astResult.AstBytes;
        files[amdPath] = astResult.AmdBytes;

        string? aoPath = null;
        if (options.EmitAoWrapper)
        {
            aoPath = $"{metaBase}/{options.ObjectBaseName}.ao";
            files[aoPath] = BuildSkinnedAo(amdPath, astPath, smPath);
        }

        return new PoeSmdAssetBundle(
            files,
            bounds,
            allVertices.Count,
            allIndices.Count / 3,
            boneCount,
            [.. shapes.Select(s => s.Name)],
            [.. astResult.Skeleton.Animations.Select(a => a.Name)],
            materialPaths,
            albedoPaths,
            flatNormalPath,
            baseColorImageIndices,
            isAlphaBlends,
            smdPath,
            smPath,
            astPath,
            amdPath,
            aoPath);
    }

    /// <summary>
    /// Builds a BOM-less UTF-16LE CRLF <c>.sm</c> version 6 skinned mesh manifest.
    /// </summary>
    public static byte[] BuildSm(string smdPath, IReadOnlyList<string> materialPaths, PoeBounds bounds)
    {
        ArgumentException.ThrowIfNullOrEmpty(smdPath);
        ArgumentNullException.ThrowIfNull(materialPaths);
        ArgumentNullException.ThrowIfNull(bounds);

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
        return PoeMeshHelpers.WriteUtf16LeNoBom(sb.ToString());
    }

    /// <summary>
    /// Builds a BOM UTF-16LE CRLF <c>.ao</c> wrapper referencing <c>.amd</c>, <c>.ast</c>, and <c>.sm</c>.
    /// </summary>
    public static byte[] BuildSkinnedAo(string amdPath, string astPath, string smPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(amdPath);
        ArgumentException.ThrowIfNullOrEmpty(astPath);
        ArgumentException.ThrowIfNullOrEmpty(smPath);

        string text =
            "version 3\r\n" +
            "extends \"Metadata/Parent\"\r\n\r\n" +
            $"AnimationController\r\n{{\r\n\tmetadata = \"{amdPath}\"\r\n}}\r\n\r\n" +
            "client\r\n{\r\n" +
            $"\tClientAnimationController\r\n\t{{\r\n\t\tskeleton = \"{astPath}\"\r\n\t}}\r\n\r\n" +
            $"\tSkinMesh\r\n\t{{\r\n\t\tskin = \"{smPath}\"\r\n\t}}\r\n" +
            "}\r\n";
        return PoeMeshHelpers.WriteUtf16LeWithBom(text);
    }

    private static (byte B0, byte B1, byte B2, byte B3, byte W0, byte W1, byte W2, byte W3) ResolveVertexSkin(
        GlbSkinInfluence[]? influences,
        int vertexIndex,
        IReadOnlyList<int> jointMap,
        int boneCount)
    {
        if (influences is null || (uint)vertexIndex >= (uint)influences.Length)
        {
            return (0, 0, 0, 0, 255, 0, 0, 0);
        }

        GlbSkinInfluence inf = influences[vertexIndex];
        byte b0 = MapJoint(inf.Joint0, jointMap, boneCount);
        byte b1 = MapJoint(inf.Joint1, jointMap, boneCount);
        byte b2 = MapJoint(inf.Joint2, jointMap, boneCount);
        byte b3 = MapJoint(inf.Joint3, jointMap, boneCount);

        (byte w0, byte w1, byte w2, byte w3) = PoeSmdWriter.NormalizeWeights(inf.Weight0, inf.Weight1, inf.Weight2, inf.Weight3);
        return (b0, b1, b2, b3, w0, w1, w2, w3);
    }

    private static byte MapJoint(ushort gltfJoint, IReadOnlyList<int> jointMap, int boneCount)
    {
        int mapped = gltfJoint < jointMap.Count ? jointMap[gltfJoint] : gltfJoint;
        if ((uint)mapped >= (uint)boneCount)
        {
            return 0;
        }

        return (byte)mapped;
    }
}
