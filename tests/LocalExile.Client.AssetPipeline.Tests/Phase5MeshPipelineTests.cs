using System.Numerics;
using System.Text;
using System.Text.Json;
using LocalExile.Client.AssetPipeline.Dat64;
using LocalExile.Client.AssetPipeline.Mesh;
using Xunit;

namespace LocalExile.Client.AssetPipeline.Tests;

public sealed class Phase5MeshPipelineTests
{
    private const string StockClientDir = "/home/nam/Git/LocalExile/Client";

    [Fact]
    public void PoeFmtWriter_RoundTripsMultiShapeAndAttachmentsWithHeaderCrossCheck()
    {
        var vertices = new PoeFmtVertex[]
        {
            new(new(-10f, -5f, -20f), new(0f, 0f, -1f), new(1f, 0f, 0f), 1f, new(0f, 0f)),
            new(new(10f, -5f, -20f), new(0f, 0f, -1f), new(1f, 0f, 0f), 1f, new(1f, 0f)),
            new(new(0f, 15f, -10f), new(0f, 0f, -1f), new(1f, 0f, 0f), 1f, new(0.5f, 1f)),
            new(new(-4f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f), -1f, new(0.25f, 0.75f)),
            new(new(4f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f), -1f, new(0.75f, 0.75f)),
            new(new(0f, 8f, -5f), new(0f, 1f, 0f), new(0f, 0f, 1f), -1f, new(0.5f, 0.25f)),
        };
        uint[] indices = [0, 1, 2, 3, 4, 5];
        var shapes = new PoeFmtShape[]
        {
            new("BaseShape", "Art/Models/Mods/Prop/textures/stone.mat", 0, 3),
            new("TrimShape", "Art/Models/Mods/Prop/textures/gold.mat", 3, 3),
        };
        var attachments = new PoeFmtAttachment[]
        {
            new(1, "fx_tip", [new(0f, 0f, -25f), new(0f, 0f, -30f)]),
        };

        (byte[] fmtBytes, byte[] hdrBytes) = PoeFmtWriter.Write(vertices, indices, shapes, attachments: attachments);
        FmtValidationInfo info = PoeFmtWriter.Validate(fmtBytes, hdrBytes);
        Assert.Equal(PoeFmtWriter.FmtVersion, info.Version);
        Assert.Equal(PoeFmtWriter.DefaultLodVersion, info.LodVersion);
        Assert.Equal(PoeFmtWriter.DefaultVertexFlags, info.Flags);
        Assert.Equal(6, info.VertexCount);
        Assert.Equal(2, info.TriangleCount);
        Assert.Equal(2, info.Shapes.Count);
        Assert.Equal(1, info.AttachmentCount);

        PoeFmtMesh decoded = PoeFmtWriter.Read(fmtBytes);
        Assert.Equal(6, decoded.Vertices.Count);
        Assert.Equal(indices, decoded.Indices);
        Assert.Equal("BaseShape", decoded.Shapes[0].Name);
        Assert.Equal("Art/Models/Mods/Prop/textures/stone.mat", decoded.Shapes[0].MaterialPath);
        Assert.Equal("TrimShape", decoded.Shapes[1].Name);
        Assert.Equal("Art/Models/Mods/Prop/textures/gold.mat", decoded.Shapes[1].MaterialPath);
        Assert.Single(decoded.Attachments);
        Assert.Equal("fx_tip", decoded.Attachments[0].Name);
        Assert.Equal(2, decoded.Attachments[0].Points.Count);
        Assert.Equal(new Vector3(0f, 0f, -25f), decoded.Attachments[0].Points[0]);

        for (int i = 0; i < vertices.Length; i++)
        {
            Assert.Equal(vertices[i].Position, decoded.Vertices[i].Position);
            Assert.True(Vector3.Distance(vertices[i].Normal, decoded.Vertices[i].Normal) < 0.02f);
            Assert.True(Vector3.Distance(vertices[i].Tangent, decoded.Vertices[i].Tangent) < 0.02f);
            Assert.Equal(vertices[i].Handedness, decoded.Vertices[i].Handedness);
            Assert.True(Vector2.Distance(vertices[i].Uv, decoded.Vertices[i].Uv) < 0.01f);
        }

        // Truncated .fmt and corrupted .fmt.header must be rejected
        Assert.Throws<InvalidDataException>(() => PoeFmtWriter.Read(fmtBytes.AsSpan(0, fmtBytes.Length - 6)));
        byte[] badHdr = (byte[])hdrBytes.Clone();
        badHdr[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => PoeFmtWriter.Validate(fmtBytes, badHdr));
    }

    [Fact]
    public void PoeFmtWriter_SupportsWideU32IndicesOver65535Vertices()
    {
        const int vertexCount = 65540;
        var vertices = new PoeFmtVertex[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            vertices[i] = new PoeFmtVertex(new(i * 0.01f, 0f, 0f), new(0f, 0f, -1f), new(1f, 0f, 0f), 1f, new(0.5f, 0.5f));
        }

        uint[] indices = [0, 65535, 65539];
        var shapes = new PoeFmtShape[] { new("HugeShape", "Art/Models/Mods/Huge/mat.mat", 0, 3) };

        (byte[] fmtBytes, byte[] hdrBytes) = PoeFmtWriter.Write(vertices, indices, shapes);
        FmtValidationInfo info = PoeFmtWriter.Validate(fmtBytes, hdrBytes);
        Assert.Equal(vertexCount, info.VertexCount);
        Assert.Equal(1, info.TriangleCount);

        PoeFmtMesh decoded = PoeFmtWriter.Read(fmtBytes);
        Assert.Equal(65539u, decoded.Indices[2]);
    }

    [Fact]
    public void PoeAstWriter_RoundTripsMultiBoneHierarchyAndMultiClipKeyframes()
    {
        // Tree: root(0) -> spine(1) -> [leftArm(2) -> leftHand(4), rightArm(3)]
        var bones = new AstBone[]
        {
            new("root", -1, Matrix4x4.CreateTranslation(0f, 0f, -5f)),
            new("spine", 0, Matrix4x4.CreateTranslation(0f, 0f, -20f)),
            new("leftArm", 1, Matrix4x4.CreateRotationZ(0.25f) * Matrix4x4.CreateTranslation(-12f, 0f, -10f)),
            new("rightArm", 1, Matrix4x4.CreateRotationZ(-0.25f) * Matrix4x4.CreateTranslation(12f, 0f, -10f)),
            new("leftHand", 2, Matrix4x4.CreateTranslation(-8f, 0f, 0f)),
        };

        var idleClip = new AstAnimationClip(
            "idle",
            Fps: 30,
            Playback: (byte)'l',
            Tracks:
            [
                new AstBoneTrack(
                    0,
                    [new(0f, Vector3.One), new(30f, Vector3.One)],
                    [new(0f, Quaternion.Identity), new(30f, Quaternion.Identity)],
                    [new(0f, new(0f, 0f, -5f)), new(15f, new(0f, 0f, -8f)), new(30f, new(0f, 0f, -5f))]),
                new AstBoneTrack(
                    2,
                    [new(0f, Vector3.One), new(30f, Vector3.One)],
                    [new(0f, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0f)), new(30f, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f))],
                    [new(0f, new(-12f, 0f, -10f)), new(30f, new(-12f, 0f, -10f))]),
            ]);

        var attackClip = new AstAnimationClip(
            "attack",
            Fps: 60,
            Playback: (byte)'o',
            Tracks:
            [
                new AstBoneTrack(
                    3,
                    [new(0f, Vector3.One), new(20f, new(1.2f, 1.2f, 1.2f))],
                    [new(0f, Quaternion.Identity), new(20f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.0f))],
                    [new(0f, new(12f, 0f, -10f)), new(20f, new(14f, -3f, -12f))]),
            ]);

        byte[] astBytes = PoeAstWriter.WriteV7(bones, [idleClip, attackClip]);
        AstValidationInfo valInfo = PoeAstWriter.ValidateV7(astBytes);
        Assert.Equal(PoeAstWriter.AstVersion, valInfo.Version);
        Assert.Equal(5, valInfo.BoneCount);
        Assert.Equal(2, valInfo.AnimationCount);
        Assert.Equal(["idle", "attack"], valInfo.AnimationNames);

        AstSkeleton decoded = PoeAstWriter.Read(astBytes);
        Assert.Equal(5, decoded.Bones.Count);
        for (int i = 0; i < bones.Length; i++)
        {
            Assert.Equal(bones[i].Name, decoded.Bones[i].Name);
            Assert.Equal(bones[i].ParentIndex, decoded.Bones[i].ParentIndex);
            AssertMatricesClose(bones[i].BindPoseLocalMatrix, decoded.Bones[i].BindPoseLocalMatrix, 1e-5f);
        }

        // Verify inverse-bind round-trip helper
        Matrix4x4[] ibms = PoeAstWriter.ComputeInverseBindMatrices(decoded.Bones);
        AstBone[] rebuilt = PoeAstWriter.BuildBonesFromInverseBindMatrices(
            [.. bones.Select(b => b.Name)],
            [.. bones.Select(b => b.ParentIndex)],
            ibms);
        for (int i = 0; i < bones.Length; i++)
        {
            AssertMatricesClose(bones[i].BindPoseLocalMatrix, rebuilt[i].BindPoseLocalMatrix, 1e-4f);
        }

        // Verify animation tracks round-trip
        Assert.Equal(2, decoded.Animations.Count);
        Assert.Equal("idle", decoded.Animations[0].Name);
        Assert.Equal((byte)30, decoded.Animations[0].Fps);
        Assert.Equal((byte)'l', decoded.Animations[0].Playback);
        Assert.Equal(2, decoded.Animations[0].Tracks.Count);
        Assert.Equal(3, decoded.Animations[0].Tracks[0].TranslationKeys.Count);
        Assert.Equal(new Vector3(0f, 0f, -8f), decoded.Animations[0].Tracks[0].TranslationKeys[1].Value);

        Assert.Equal("attack", decoded.Animations[1].Name);
        Assert.Equal((byte)60, decoded.Animations[1].Fps);
        Assert.Equal((byte)'o', decoded.Animations[1].Playback);
        Assert.Single(decoded.Animations[1].Tracks);
        Assert.Equal(3, decoded.Animations[1].Tracks[0].BoneIndex);

        // Verify BOM-less UTF-16LE CRLF .amd generation
        byte[] amdBytes = PoeAstWriter.BuildAmd(decoded.Animations);
        AssertUtf16LeEncoding(amdBytes, expectBom: false, requireCrlf: true, label: "multi-bone .amd");
        string amdText = Encoding.Unicode.GetString(amdBytes);
        Assert.Contains("\"idle\"\r\n\tloop\r\n\t1000\r\n", amdText, StringComparison.Ordinal);
        Assert.Contains("\"attack\"\r\n\tonce\r\n\t333\r\n", amdText, StringComparison.Ordinal);

        // Invalid cycle or non-root bone 0 must throw
        AstBone[] badRoot = [new("child", 1, Matrix4x4.Identity), new("root", -1, Matrix4x4.Identity)];
        Assert.Throws<ArgumentException>(() => PoeAstWriter.WriteV7(badRoot, [idleClip]));
    }

    [Fact]
    public void PoeSmdWriter_RoundTripsFourBoneSkinnedWeightsAndWideIndices()
    {
        // Weight normalization invariants
        var w1 = PoeSmdWriter.NormalizeWeights(0.5f, 0.3f, 0.15f, 0.05f);
        Assert.Equal(255, w1.W0 + w1.W1 + w1.W2 + w1.W3);
        Assert.Equal((byte)128, w1.W0);
        Assert.Equal((byte)76, w1.W1);

        var wZero = PoeSmdWriter.NormalizeWeights(0f, -1f, float.NaN, 0f);
        Assert.Equal((255, 0, 0, 0), wZero);

        var vertices = new PoeVertex[]
        {
            new(new(0f, 0f, 0f), new(0f, 0f, -1f), new(1f, 0f, 0f), 1f, new(0f, 0f), Bone0: 0, Weight0: 128, Bone1: 1, Weight1: 64, Bone2: 2, Weight2: 40, Bone3: 3, Weight3: 23),
            new(new(10f, 0f, 0f), new(0f, 0f, -1f), new(1f, 0f, 0f), 1f, new(1f, 0f), Bone0: 1, Weight0: 200, Bone1: 2, Weight1: 55, Bone2: 0, Weight2: 0, Bone3: 0, Weight3: 0),
            new(new(5f, 10f, -5f), new(0f, 0f, -1f), new(1f, 0f, 0f), -1f, new(0.5f, 1f), Bone0: 3, Weight0: 255),
        };
        uint[] indices = [0, 1, 2];
        var shapes = new PoeShapeRange[] { new("BodyShape", 0, 3) };

        (byte[] smdBytes, byte[] hdrBytes) = PoeSmdWriter.Write(vertices, indices, shapes);
        SmdValidationInfo info = PoeSmdWriter.Validate(smdBytes, hdrBytes);
        Assert.Equal((byte)3, info.Version);
        Assert.Equal(3, info.VertexCount);
        Assert.Equal(1, info.TriangleCount);

        PoeSmdMesh decoded = PoeSmdWriter.Read(smdBytes);
        Assert.Equal(3, decoded.Vertices.Count);
        Assert.Equal((byte)0, decoded.Vertices[0].Bone0);
        Assert.Equal((byte)1, decoded.Vertices[0].Bone1);
        Assert.Equal((byte)2, decoded.Vertices[0].Bone2);
        Assert.Equal((byte)3, decoded.Vertices[0].Bone3);
        Assert.Equal((byte)128, decoded.Vertices[0].Weight0);
        Assert.Equal((byte)64, decoded.Vertices[0].Weight1);
        Assert.Equal((byte)40, decoded.Vertices[0].Weight2);
        Assert.Equal((byte)23, decoded.Vertices[0].Weight3);
        Assert.Equal(255, decoded.Vertices[0].Weight0 + decoded.Vertices[0].Weight1 + decoded.Vertices[0].Weight2 + decoded.Vertices[0].Weight3);
        Assert.Equal(-1f, decoded.Vertices[2].Handedness);
    }

    [Fact]
    public void GlbToFmtConverter_ConvertsStaticGlbToFmtMatAndBomAo()
    {
        byte[] glb = BuildSyntheticStaticGlb();
        GlbScene scene = GlbParser.Parse(glb);

        PoeFmtAssetBundle bundle = GlbToFmtConverter.Convert(scene, new PoeFmtMeshOptions
        {
            ArtBasePath = "Art/Models/Mods/Altar",
            MetadataBasePath = "Metadata/Props/Mods/Altar",
            ModelBaseName = "altar",
            ObjectBaseName = "CustomAltar",
            TargetLongestHorizontalSpan = 100f,
            CenterBase = true,
            EmitAoWrapper = true,
        });

        Assert.Equal("Art/Models/Mods/Altar/altar.fmt", bundle.FmtVirtualPath);
        Assert.Equal("Metadata/Props/Mods/Altar/CustomAltar.ao", bundle.AoVirtualPath);
        Assert.Equal(2, bundle.Shapes.Count);
        // Primitive 1 is double-sided BLEND -> backfaces emitted (3 verts + 6 verts = 9 verts, 1 tri + 2 tris = 3 tris)
        Assert.Equal(9, bundle.VertexCount);
        Assert.Equal(3, bundle.TriangleCount);

        byte[] fmtBytes = bundle.Files[bundle.FmtVirtualPath];
        byte[] hdrBytes = bundle.Files[bundle.FmtVirtualPath + ".header"];
        FmtValidationInfo fmtInfo = PoeFmtWriter.Validate(fmtBytes, hdrBytes);
        Assert.Equal(9, fmtInfo.VertexCount);
        Assert.Equal(3, fmtInfo.TriangleCount);

        // .mat files MUST be BOM-less UTF-16LE CRLF valid v4 JSON
        foreach (string matPath in bundle.MaterialPaths)
        {
            byte[] matBytes = bundle.Files[matPath];
            AssertUtf16LeEncoding(matBytes, expectBom: false, requireCrlf: true, label: matPath);
            string matJson = Encoding.Unicode.GetString(matBytes);
            using JsonDocument doc = JsonDocument.Parse(matJson);
            Assert.Equal(4, doc.RootElement.GetProperty("version").GetInt32());
        }

        // .ao file MUST be BOM UTF-16LE CRLF extending Metadata/FmtParent
        byte[] aoBytes = bundle.Files[bundle.AoVirtualPath!];
        AssertUtf16LeEncoding(aoBytes, expectBom: true, requireCrlf: true, label: bundle.AoVirtualPath!);
        string aoText = Encoding.Unicode.GetString(aoBytes, 2, aoBytes.Length - 2);
        Assert.Contains("extends \"Metadata/FmtParent\"", aoText, StringComparison.Ordinal);
        Assert.Contains("fixed_mesh = \"Art/Models/Mods/Altar/altar.fmt\"", aoText, StringComparison.Ordinal);
    }

    [Fact]
    public void GlbToSmdConverter_ConvertsRiggedMultiBoneAnimatedGlbWithExactEncodings()
    {
        // Verify coordinate basis conjugation math identity first: C^-1 * (S*R*T) * C == S_poe * R_poe * T_poe
        Vector3 testS = new(1.5f, 2.0f, 0.75f);
        Quaternion testR = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.4f, -0.3f, 0.7f));
        Vector3 testT = new(3.5f, 12.0f, -7.25f);
        Matrix4x4 gltfM = Matrix4x4.CreateScale(testS) * Matrix4x4.CreateFromQuaternion(testR) * Matrix4x4.CreateTranslation(testT);
        Matrix4x4 conjM = GlbToAstConverter.ConvertMatrixGltfToPoe(gltfM);
        Matrix4x4 directM =
            Matrix4x4.CreateScale(GlbToAstConverter.ConvertScaleGltfToPoe(testS)) *
            Matrix4x4.CreateFromQuaternion(GlbToAstConverter.ConvertQuaternionGltfToPoe(testR)) *
            Matrix4x4.CreateTranslation(GlbToPoeMeshConverter.GltfToPoeAxes(testT));
        AssertMatricesClose(conjM, directM, 1e-4f);

        // Build rigged 3-bone GLB with intentionally non-root-first joint list [arm, root, hand] to test topological remapping!
        byte[] riggedGlb = BuildSyntheticRiggedGlb(nonRootFirstJointOrder: true);
        GlbScene scene = GlbParser.Parse(riggedGlb);
        Assert.NotNull(scene.Skins);
        Assert.Single(scene.Skins);
        Assert.NotNull(scene.Animations);
        Assert.Equal(2, scene.Animations.Count);
        Assert.NotNull(scene.Primitives[0].SkinInfluences);

        PoeSmdAssetBundle bundle = GlbToSmdConverter.Convert(scene, new PoeSmdMeshOptions
        {
            ArtBasePath = "Art/Models/Mods/Golem",
            MetadataBasePath = "Metadata/Monsters/Mods/Golem",
            ModelBaseName = "golem",
            ObjectBaseName = "CustomGolem",
            AnimationFps = 30,
        });

        Assert.Equal(3, bundle.BoneCount);
        Assert.Equal(["idle", "slam"], bundle.AnimationNames);

        // Verify .smd + .smd.header and remapped multi-bone weights
        PoeSmdWriter.Validate(bundle.Files[bundle.SmdVirtualPath], bundle.Files[bundle.SmdVirtualPath + ".header"]);
        PoeSmdMesh smdMesh = PoeSmdWriter.Read(bundle.Files[bundle.SmdVirtualPath]);
        Assert.Equal(3, smdMesh.Vertices.Count);
        // Vertex 1 had glTF joints (0=arm, 1=root) weights (0.75, 0.25); after topo reorder root=0, arm=1, hand=2!
        PoeVertex v1 = smdMesh.Vertices[1];
        Assert.Equal((byte)1, v1.Bone0); // arm -> PoE bone 1
        Assert.Equal((byte)0, v1.Bone1); // root -> PoE bone 0
        Assert.Equal(255, v1.Weight0 + v1.Weight1 + v1.Weight2 + v1.Weight3);
        Assert.InRange(v1.Weight0, (byte)190, (byte)192);
        Assert.InRange(v1.Weight1, (byte)63, (byte)65);

        // Verify multi-bone .ast v7 round-trip
        byte[] astBytes = bundle.Files[bundle.AstVirtualPath];
        PoeAstWriter.ValidateV7(astBytes);
        AstSkeleton skel = PoeAstWriter.Read(astBytes);
        Assert.Equal(3, skel.Bones.Count);
        Assert.Equal("root_joint", skel.Bones[0].Name);
        Assert.Equal(-1, skel.Bones[0].ParentIndex);
        Assert.Equal("arm_joint", skel.Bones[1].Name);
        Assert.Equal(0, skel.Bones[1].ParentIndex);
        Assert.Equal("hand_joint", skel.Bones[2].Name);
        Assert.Equal(1, skel.Bones[2].ParentIndex);
        Assert.Equal(2, skel.Animations.Count);
        Assert.Equal(3, skel.Animations[0].Tracks.Count);
        Assert.Equal(3, skel.Animations[1].Tracks.Count);

        // Verify 12+3+1 text encoding rules: .sm, .mat, .amd = BOM-less UTF-16LE CRLF; .ao = BOM UTF-16LE CRLF
        AssertUtf16LeEncoding(bundle.Files[bundle.SmVirtualPath], expectBom: false, requireCrlf: true, label: bundle.SmVirtualPath);
        AssertUtf16LeEncoding(bundle.Files[bundle.AmdVirtualPath], expectBom: false, requireCrlf: true, label: bundle.AmdVirtualPath);
        foreach (string matPath in bundle.MaterialPaths)
        {
            AssertUtf16LeEncoding(bundle.Files[matPath], expectBom: false, requireCrlf: true, label: matPath);
        }

        Assert.NotNull(bundle.AoVirtualPath);
        AssertUtf16LeEncoding(bundle.Files[bundle.AoVirtualPath], expectBom: true, requireCrlf: true, label: bundle.AoVirtualPath);
    }

    [Fact]
    public void ProgramCli_ConvertGlbFmtSmdAndInspectMeshCommandsSucceed()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"localexile-p5-mesh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string staticGlbPath = Path.Combine(tempDir, "prop.glb");
            string riggedGlbPath = Path.Combine(tempDir, "rig.glb");
            File.WriteAllBytes(staticGlbPath, BuildSyntheticStaticGlb());
            File.WriteAllBytes(riggedGlbPath, BuildSyntheticRiggedGlb(nonRootFirstJointOrder: false));

            string fmtOut = Path.Combine(tempDir, "fmt_out");
            string smdOut = Path.Combine(tempDir, "smd_out");

            int rcFmt = Program.Main(["convert-glb-fmt", "--in", staticGlbPath, "--out", fmtOut, "--name", "pillar", "--span", "80"]);
            Assert.Equal(0, rcFmt);
            string fmtFile = Path.Combine(fmtOut, "Art", "Models", "Mods", "pillar", "pillar.fmt");
            Assert.True(File.Exists(fmtFile));
            Assert.True(File.Exists(fmtFile + ".header"));

            int rcSmd = Program.Main(["convert-glb-smd", "--in", riggedGlbPath, "--out", smdOut, "--name", "knight", "--fps", "30"]);
            Assert.Equal(0, rcSmd);
            string smdFile = Path.Combine(smdOut, "Art", "Models", "Mods", "knight", "knight.smd");
            string astFile = Path.Combine(smdOut, "Art", "Models", "Mods", "knight", "knight.ast");
            Assert.True(File.Exists(smdFile));
            Assert.True(File.Exists(smdFile + ".header"));
            Assert.True(File.Exists(astFile));

            Assert.Equal(0, Program.Main(["inspect-mesh", "--in", fmtFile]));
            Assert.Equal(0, Program.Main(["inspect-mesh", "--in", smdFile]));
            Assert.Equal(0, Program.Main(["inspect-mesh", "--in", astFile]));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void StockClient329_ParsesAndValidatesRealFmtAstAndSmdAssets()
    {
        string indexPath = Path.Combine(StockClientDir, "Bundles2", "_.index.bin");
        if (!File.Exists(indexPath))
        {
            // Gracefully skip in CI environments without local stock PoE 3.29.3d client
            return;
        }

        using Bundles2Reader bundles = Bundles2Reader.Open(StockClientDir);

        // 1. Real 3.29.3d .fmt v9 + .fmt.header (single-shape, multi-shape shared mat, multi-mat, and attachments!)
        string[] stockFmts =
        [
            "metadata/effects/spells/monsters_effects/league_chayula/graftsystem/active_xoph_02/mb/volcanobase05.fmt",
            "art/models/terrain/woods/doodads/featherpile06.fmt",
            "art/models/terrain/woods/doodads/azmerispiderwebs05_01.fmt",
            "art/particles/monster_particles/league_deepwater/kelpdreg/templarcrossbowbolt.fmt",
        ];

        foreach (string fmtPath in stockFmts)
        {
            Assert.True(bundles.TryReadFile(fmtPath, out byte[] fmtBytes), $"Missing stock FMT '{fmtPath}'.");
            Assert.True(bundles.TryReadFile(fmtPath + ".header", out byte[] hdrBytes), $"Missing stock FMT header '{fmtPath}.header'.");
            FmtValidationInfo info = PoeFmtWriter.Validate(fmtBytes, hdrBytes);
            Assert.Equal((byte)9, info.Version);
            Assert.True(info.VertexCount > 0);
            Assert.True(info.TriangleCount > 0);
            Assert.NotEmpty(info.Shapes);

            // Re-serialize decoded vertices/indices/shapes/attachments and verify our writer round-trips cleanly!
            PoeFmtMesh decoded = PoeFmtWriter.Read(fmtBytes);
            (byte[] reFmt, byte[] reHdr) = PoeFmtWriter.Write(decoded.Vertices, decoded.Indices, decoded.Shapes, decoded.Bounds, decoded.Attachments);
            FmtValidationInfo reInfo = PoeFmtWriter.Validate(reFmt, reHdr);
            Assert.Equal(decoded.Vertices.Count, reInfo.VertexCount);
            Assert.Equal(decoded.Indices.Count / 3, reInfo.TriangleCount);
            Assert.Equal(decoded.Shapes.Count, reInfo.Shapes.Count);
            Assert.Equal(decoded.Attachments.Count, reInfo.AttachmentCount);
        }

        // Also inspect legacy v6 .fmt
        if (bundles.TryReadFile("art/tools/vfxlib/ivan/test/cone_model/cone90.fmt", out byte[] cone90))
        {
            FmtValidationInfo legacy = PoeFmtWriter.Inspect(cone90);
            Assert.Equal((byte)6, legacy.Version);
            Assert.True(legacy.VertexCount > 0 && legacy.TriangleCount > 0);
        }

        // 2. Real 3.29.3d .ast multi-bone skeletons (v8, v9 with lights, v11, v12)
        // First verify pure-managed parsing (bone hierarchy, bind matrices, light count, animation headers)
        byte[] chestAst = bundles.ReadFile("art/templarchest03tutorial.ast");
        AstSkeleton chestMeta = PoeAstWriter.Read(chestAst);
        Assert.Equal((byte)8, chestMeta.Version);
        Assert.Equal(3, chestMeta.Bones.Count);
        Assert.Equal(["root", "Lid", "Latch"], chestMeta.Bones.Select(b => b.Name).ToArray());
        Assert.Equal([-1, 0, 1], chestMeta.Bones.Select(b => b.ParentIndex).ToArray());
        Assert.True(MathF.Abs(chestMeta.Bones[1].BindPoseLocalMatrix.M43 - (-38.676f)) < 0.01f);
        Assert.Single(chestMeta.Animations);
        Assert.Equal("open", chestMeta.Animations[0].Name);
        Assert.Equal((byte)24, chestMeta.Animations[0].Fps);

        byte[] attackAst = bundles.ReadFile("art/particles/monster_particles/league_hellscape/mob_pale_quasimodo/mbs/onacts/attack02.ast");
        AstSkeleton attackMeta = PoeAstWriter.Read(attackAst);
        Assert.Equal((byte)12, attackMeta.Version);
        Assert.Equal(26, attackMeta.Bones.Count);
        Assert.Single(attackMeta.Animations);
        Assert.Equal("animate", attackMeta.Animations[0].Name);

        byte[] catAst = bundles.ReadFile("art/models/pet/cat/rig.ast");
        AstSkeleton catSkel = PoeAstWriter.Read(catAst);
        Assert.Equal((byte)12, catSkel.Version);
        Assert.Equal(30, catSkel.Bones.Count);
        Assert.Equal(11, catSkel.Animations.Count);
        Assert.Contains(catSkel.Animations, a => a.Name == "idle");
        Assert.Contains(catSkel.Animations, a => a.Name == "run");

        byte[] ritualAst = bundles.ReadFile("art/particles/monster_particles/league_ritual/ritual_rune/mb/ritual_iiq/rig.ast");
        AstSkeleton ritualSkel = PoeAstWriter.Read(ritualAst);
        Assert.Equal((byte)9, ritualSkel.Version);
        Assert.Equal(8, ritualSkel.Bones.Count);
        Assert.Equal(1, ritualSkel.LightCount);
        Assert.Equal(3, ritualSkel.Animations.Count);

        // When LocalExile's ooz_worker.mjs is available, also verify full Oodle track decompression & v7 re-serialization!
        string oozWorker = Path.Combine(Path.GetDirectoryName(StockClientDir)!, "Tools", "ooz", "ooz_worker.mjs");
        if (File.Exists(oozWorker))
        {
            Func<ReadOnlyMemory<byte>, int, byte[]> oozDecompress = (comp, rawSize) => DecompressWithOozWorker(oozWorker, comp, rawSize);

            AstSkeleton chestFull = PoeAstWriter.Read(chestAst, oozDecompress);
            Assert.Equal(3, chestFull.Animations[0].Tracks.Count);
            Vector3 lidBindT = new(chestFull.Bones[1].BindPoseLocalMatrix.M41, chestFull.Bones[1].BindPoseLocalMatrix.M42, chestFull.Bones[1].BindPoseLocalMatrix.M43);
            Assert.True(Vector3.Distance(lidBindT, chestFull.Animations[0].Tracks[1].TranslationKeys[0].Value) < 1e-3f);

            AstSkeleton attackFull = PoeAstWriter.Read(attackAst, oozDecompress);
            Assert.Equal(26, attackFull.Animations[0].Tracks.Count);

            byte[] reAstV7 = PoeAstWriter.WriteV7(attackFull);
            AstValidationInfo reAstInfo = PoeAstWriter.ValidateV7(reAstV7);
            Assert.Equal(26, reAstInfo.BoneCount);
            Assert.Equal(["animate"], reAstInfo.AnimationNames);
            AstSkeleton reDecodedAttack = PoeAstWriter.Read(reAstV7);
            Assert.Equal(26, reDecodedAttack.Animations[0].Tracks.Count);
        }

        // 3. Real 3.29.3d .smd v3 + .smd.header (including multi-bone blended weights & multi-shape)
        string[] stockSmds =
        [
            "art/models/pet/cat/rig_8dd2fec6.smd",
            "art/particles/monster_particles/league_settlers/piratecoveboss/mb/wave/rig_d854e2fc.smd",
            "art/particles/monster_particles/league_settlers/piratecoveboss/mb/rig_79a40fd7.smd",
        ];

        bool foundBlendedMultiBoneVertex = false;
        foreach (string smdPath in stockSmds)
        {
            byte[] smdBytes = bundles.ReadFile(smdPath);
            byte[] hdrBytes = bundles.ReadFile(smdPath + ".header");
            SmdValidationInfo smdInfo = PoeSmdWriter.Validate(smdBytes, hdrBytes);
            Assert.Equal((byte)3, smdInfo.Version);
            Assert.True(smdInfo.VertexCount > 0);
            Assert.True(smdInfo.TriangleCount > 0);

            PoeSmdMesh decoded = PoeSmdWriter.Read(smdBytes);
            if (decoded.Vertices.Any(v => v.Weight1 > 0))
            {
                foundBlendedMultiBoneVertex = true;
            }

            // Re-serialize decoded stock SMD and verify full round-trip parity!
            (byte[] reSmd, byte[] reHdr) = PoeSmdWriter.Write(decoded.Vertices, decoded.Indices, decoded.Shapes, decoded.Bounds);
            SmdValidationInfo reInfo = PoeSmdWriter.Validate(reSmd, reHdr);
            Assert.Equal(decoded.Vertices.Count, reInfo.VertexCount);
            Assert.Equal(decoded.Indices.Count / 3, reInfo.TriangleCount);
            Assert.Equal(decoded.Shapes.Count, reInfo.Shapes.Count);
        }

        Assert.True(foundBlendedMultiBoneVertex, "Expected at least one multi-bone blended vertex in stock 3.29.3d SMDs.");
    }

    private static void AssertUtf16LeEncoding(byte[] bytes, bool expectBom, bool requireCrlf, string label)
    {
        Assert.True(bytes.Length >= 2, $"Expected non-empty UTF-16LE file '{label}'.");
        Assert.Equal(0, bytes.Length & 1);
        bool hasBom = bytes[0] == 0xFF && bytes[1] == 0xFE;
        Assert.Equal(expectBom, hasBom);
        Assert.False(bytes[0] == 0xFE && bytes[1] == 0xFF, $"Unexpected UTF-16BE BOM in '{label}'.");

        string text = Encoding.Unicode.GetString(bytes, hasBom ? 2 : 0, bytes.Length - (hasBom ? 2 : 0));
        Assert.DoesNotContain('\uFEFF', text);
        Assert.DoesNotContain('\0', text);
        if (requireCrlf)
        {
            Assert.Contains("\r\n", text, StringComparison.Ordinal);
            string withoutCrlf = text.Replace("\r\n", string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', withoutCrlf);
            Assert.DoesNotContain('\r', withoutCrlf);
        }
    }

    private static void AssertMatricesClose(Matrix4x4 expected, Matrix4x4 actual, float tolerance)
    {
        Assert.True(MathF.Abs(expected.M11 - actual.M11) <= tolerance, $"M11 {expected.M11} vs {actual.M11}");
        Assert.True(MathF.Abs(expected.M12 - actual.M12) <= tolerance, $"M12 {expected.M12} vs {actual.M12}");
        Assert.True(MathF.Abs(expected.M13 - actual.M13) <= tolerance, $"M13 {expected.M13} vs {actual.M13}");
        Assert.True(MathF.Abs(expected.M14 - actual.M14) <= tolerance, $"M14 {expected.M14} vs {actual.M14}");
        Assert.True(MathF.Abs(expected.M21 - actual.M21) <= tolerance, $"M21 {expected.M21} vs {actual.M21}");
        Assert.True(MathF.Abs(expected.M22 - actual.M22) <= tolerance, $"M22 {expected.M22} vs {actual.M22}");
        Assert.True(MathF.Abs(expected.M23 - actual.M23) <= tolerance, $"M23 {expected.M23} vs {actual.M23}");
        Assert.True(MathF.Abs(expected.M24 - actual.M24) <= tolerance, $"M24 {expected.M24} vs {actual.M24}");
        Assert.True(MathF.Abs(expected.M31 - actual.M31) <= tolerance, $"M31 {expected.M31} vs {actual.M31}");
        Assert.True(MathF.Abs(expected.M32 - actual.M32) <= tolerance, $"M32 {expected.M32} vs {actual.M32}");
        Assert.True(MathF.Abs(expected.M33 - actual.M33) <= tolerance, $"M33 {expected.M33} vs {actual.M33}");
        Assert.True(MathF.Abs(expected.M34 - actual.M34) <= tolerance, $"M34 {expected.M34} vs {actual.M34}");
        Assert.True(MathF.Abs(expected.M41 - actual.M41) <= tolerance, $"M41 {expected.M41} vs {actual.M41}");
        Assert.True(MathF.Abs(expected.M42 - actual.M42) <= tolerance, $"M42 {expected.M42} vs {actual.M42}");
        Assert.True(MathF.Abs(expected.M43 - actual.M43) <= tolerance, $"M43 {expected.M43} vs {actual.M43}");
        Assert.True(MathF.Abs(expected.M44 - actual.M44) <= tolerance, $"M44 {expected.M44} vs {actual.M44}");
    }

    private static byte[] BuildSyntheticStaticGlb()
    {
        using var bin = new MemoryStream();
        using var bw = new BinaryWriter(bin);

        // BV0: 6 uint16 indices (12 bytes)
        foreach (ushort idx in new ushort[] { 0, 1, 2, 0, 1, 2 }) bw.Write(idx);

        // BV1: 6 positions (72 bytes)
        float[] positions =
        [
            -2f, 0f, -2f, 2f, 0f, -2f, 0f, 4f, 2f,
            -1f, 1f, 0f, 1f, 1f, 0f, 0f, 2f, 1f,
        ];
        foreach (float f in positions) bw.Write(f);

        // BV2: 6 normals (72 bytes)
        float[] normals =
        [
            0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f,
            0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f,
        ];
        foreach (float f in normals) bw.Write(f);

        // BV3: 6 UVs (48 bytes)
        float[] uvs =
        [
            0f, 0f, 1f, 0f, 0.5f, 1f,
            0f, 0f, 1f, 0f, 0.5f, 1f,
        ];
        foreach (float f in uvs) bw.Write(f);
        bw.Flush();

        byte[] binBytes = Pad4(bin.ToArray(), 0x00);
        string json = $$"""
        {
          "asset": { "version": "2.0" },
          "scene": 0,
          "scenes": [{ "nodes": [0, 1] }],
          "nodes": [
            { "name": "stone_node", "mesh": 0 },
            { "name": "banner_node", "mesh": 1 }
          ],
          "materials": [
            { "name": "stone", "alphaMode": "OPAQUE" },
            { "name": "banner", "alphaMode": "BLEND", "doubleSided": true }
          ],
          "meshes": [
            { "name": "stone_mesh", "primitives": [{ "attributes": { "POSITION": 1, "NORMAL": 2, "TEXCOORD_0": 3 }, "indices": 0, "material": 0 }] },
            { "name": "banner_mesh", "primitives": [{ "attributes": { "POSITION": 5, "NORMAL": 6, "TEXCOORD_0": 7 }, "indices": 4, "material": 1 }] }
          ],
          "buffers": [{ "byteLength": {{binBytes.Length}} }],
          "bufferViews": [
            { "buffer": 0, "byteOffset": 0, "byteLength": 12 },
            { "buffer": 0, "byteOffset": 12, "byteLength": 72 },
            { "buffer": 0, "byteOffset": 84, "byteLength": 72 },
            { "buffer": 0, "byteOffset": 156, "byteLength": 48 }
          ],
          "accessors": [
            { "bufferView": 0, "byteOffset": 0, "componentType": 5123, "count": 3, "type": "SCALAR" },
            { "bufferView": 1, "byteOffset": 0, "componentType": 5126, "count": 3, "type": "VEC3" },
            { "bufferView": 2, "byteOffset": 0, "componentType": 5126, "count": 3, "type": "VEC3" },
            { "bufferView": 3, "byteOffset": 0, "componentType": 5126, "count": 3, "type": "VEC2" },
            { "bufferView": 0, "byteOffset": 6, "componentType": 5123, "count": 3, "type": "SCALAR" },
            { "bufferView": 1, "byteOffset": 36, "componentType": 5126, "count": 3, "type": "VEC3" },
            { "bufferView": 2, "byteOffset": 36, "componentType": 5126, "count": 3, "type": "VEC3" },
            { "bufferView": 3, "byteOffset": 24, "componentType": 5126, "count": 3, "type": "VEC2" }
          ]
        }
        """;

        return PackGlb(json, binBytes);
    }

    private static byte[] BuildSyntheticRiggedGlb(bool nonRootFirstJointOrder)
    {
        // Nodes:
        //   0: root_joint (T = (0, 0, 0), child = [1])
        //   1: arm_joint  (T = (0, 10, 0), child = [2])
        //   2: hand_joint (T = (5, 0, 0))
        //   3: mesh_node  (mesh = 0, skin = 0)
        // Joints order:
        //   normal:        [0, 1, 2]
        //   nonRootFirst:  [1, 0, 2] (arm=0, root=1, hand=2 -> tests topological joint remapping!)
        int[] jointNodes = nonRootFirstJointOrder ? [1, 0, 2] : [0, 1, 2];

        Matrix4x4 worldRoot = Matrix4x4.Identity;
        Matrix4x4 worldArm = Matrix4x4.CreateTranslation(0f, 10f, 0f);
        Matrix4x4 worldHand = Matrix4x4.CreateTranslation(5f, 10f, 0f);
        Matrix4x4[] worldByNode = [worldRoot, worldArm, worldHand];

        using var bin = new MemoryStream();
        using var bw = new BinaryWriter(bin);

        // BV0 (off 0, len 8): 3 uint16 indices + 2B pad
        int bv0Off = 0;
        bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)2); bw.Write((ushort)0);

        // BV1 (off 8, len 36): 3 positions
        int bv1Off = (int)bin.Position;
        foreach (float f in new[] { -5f, 0f, 0f, 5f, 10f, 0f, 0f, 15f, 2f }) bw.Write(f);

        // BV2 (off 44, len 36): 3 normals
        int bv2Off = (int)bin.Position;
        foreach (float f in new[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f }) bw.Write(f);

        // BV3 (off 80, len 24): 3 UVs
        int bv3Off = (int)bin.Position;
        foreach (float f in new[] { 0f, 0f, 1f, 0f, 0.5f, 1f }) bw.Write(f);

        // BV4 (off 104, len 12): 3 JOINTS_0 (UNSIGNED_BYTE VEC4)
        int bv4Off = (int)bin.Position;
        // v0 -> joint 0 (1.0); v1 -> joint 0 (0.75) + joint 1 (0.25); v2 -> joint 2 (1.0)
        bw.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 2, 0, 0, 0 });

        // BV5 (off 116, len 48): 3 WEIGHTS_0 (FLOAT VEC4)
        int bv5Off = (int)bin.Position;
        foreach (float f in new[]
        {
            1f, 0f, 0f, 0f,
            0.75f, 0.25f, 0f, 0f,
            1f, 0f, 0f, 0f,
        }) bw.Write(f);

        // BV6 (off 164, len 192): 3 inverseBindMatrices (MAT4) matching jointNodes order
        int bv6Off = (int)bin.Position;
        foreach (int jNode in jointNodes)
        {
            Matrix4x4.Invert(worldByNode[jNode], out Matrix4x4 ibm);
            WriteMatrix(bw, ibm);
        }

        // BV7 (off 356, len 8): anim times [0.0, 1.0]
        int bv7Off = (int)bin.Position;
        bw.Write(0f); bw.Write(1f);

        // BV8 (off 364, len 24): root translation keyframes [(0,0,0), (0,2,0)]
        int bv8Off = (int)bin.Position;
        foreach (float f in new[] { 0f, 0f, 0f, 0f, 2f, 0f }) bw.Write(f);

        // BV9 (off 388, len 32): arm rotation keyframes [identity, 90deg around +Y]
        int bv9Off = (int)bin.Position;
        Quaternion q90Y = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f);
        foreach (float f in new[] { 0f, 0f, 0f, 1f, q90Y.X, q90Y.Y, q90Y.Z, q90Y.W }) bw.Write(f);

        // BV10 (off 420, len 24): hand scale keyframes [(1,1,1), (1.5,1.5,1.5)]
        int bv10Off = (int)bin.Position;
        foreach (float f in new[] { 1f, 1f, 1f, 1.5f, 1.5f, 1.5f }) bw.Write(f);

        bw.Flush();
        byte[] binBytes = Pad4(bin.ToArray(), 0x00);

        string json = $$"""
        {
          "asset": { "version": "2.0" },
          "scene": 0,
          "scenes": [{ "nodes": [0, 3] }],
          "nodes": [
            { "name": "root_joint", "translation": [0, 0, 0], "children": [1] },
            { "name": "arm_joint", "translation": [0, 10, 0], "children": [2] },
            { "name": "hand_joint", "translation": [5, 0, 0] },
            { "name": "mesh_node", "mesh": 0, "skin": 0 }
          ],
          "skins": [
            {
              "name": "golem_skin",
              "skeleton": 0,
              "joints": [{{jointNodes[0]}}, {{jointNodes[1]}}, {{jointNodes[2]}}],
              "inverseBindMatrices": 6
            }
          ],
          "materials": [
            { "name": "golem_body", "alphaMode": "OPAQUE" }
          ],
          "meshes": [
            {
              "name": "golem_mesh",
              "primitives": [
                {
                  "attributes": { "POSITION": 1, "NORMAL": 2, "TEXCOORD_0": 3, "JOINTS_0": 4, "WEIGHTS_0": 5 },
                  "indices": 0,
                  "material": 0
                }
              ]
            }
          ],
          "animations": [
            {
              "name": "idle",
              "samplers": [
                { "input": 7, "output": 8, "interpolation": "LINEAR" }
              ],
              "channels": [
                { "sampler": 0, "target": { "node": 0, "path": "translation" } }
              ]
            },
            {
              "name": "slam",
              "samplers": [
                { "input": 7, "output": 9, "interpolation": "LINEAR" },
                { "input": 7, "output": 10, "interpolation": "LINEAR" }
              ],
              "channels": [
                { "sampler": 0, "target": { "node": 1, "path": "rotation" } },
                { "sampler": 1, "target": { "node": 2, "path": "scale" } }
              ]
            }
          ],
          "buffers": [{ "byteLength": {{binBytes.Length}} }],
          "bufferViews": [
            { "buffer": 0, "byteOffset": {{bv0Off}}, "byteLength": 6 },
            { "buffer": 0, "byteOffset": {{bv1Off}}, "byteLength": 36 },
            { "buffer": 0, "byteOffset": {{bv2Off}}, "byteLength": 36 },
            { "buffer": 0, "byteOffset": {{bv3Off}}, "byteLength": 24 },
            { "buffer": 0, "byteOffset": {{bv4Off}}, "byteLength": 12 },
            { "buffer": 0, "byteOffset": {{bv5Off}}, "byteLength": 48 },
            { "buffer": 0, "byteOffset": {{bv6Off}}, "byteLength": 192 },
            { "buffer": 0, "byteOffset": {{bv7Off}}, "byteLength": 8 },
            { "buffer": 0, "byteOffset": {{bv8Off}}, "byteLength": 24 },
            { "buffer": 0, "byteOffset": {{bv9Off}}, "byteLength": 32 },
            { "buffer": 0, "byteOffset": {{bv10Off}}, "byteLength": 24 }
          ],
          "accessors": [
            { "bufferView": 0, "componentType": 5123, "count": 3, "type": "SCALAR" },
            { "bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC3" },
            { "bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC3" },
            { "bufferView": 3, "componentType": 5126, "count": 3, "type": "VEC2" },
            { "bufferView": 4, "componentType": 5121, "count": 3, "type": "VEC4" },
            { "bufferView": 5, "componentType": 5126, "count": 3, "type": "VEC4" },
            { "bufferView": 6, "componentType": 5126, "count": 3, "type": "MAT4" },
            { "bufferView": 7, "componentType": 5126, "count": 2, "type": "SCALAR" },
            { "bufferView": 8, "componentType": 5126, "count": 2, "type": "VEC3" },
            { "bufferView": 9, "componentType": 5126, "count": 2, "type": "VEC4" },
            { "bufferView": 10, "componentType": 5126, "count": 2, "type": "VEC3" }
          ]
        }
        """;

        return PackGlb(json, binBytes);
    }

    private static void WriteMatrix(BinaryWriter bw, Matrix4x4 m)
    {
        bw.Write(m.M11); bw.Write(m.M12); bw.Write(m.M13); bw.Write(m.M14);
        bw.Write(m.M21); bw.Write(m.M22); bw.Write(m.M23); bw.Write(m.M24);
        bw.Write(m.M31); bw.Write(m.M32); bw.Write(m.M33); bw.Write(m.M34);
        bw.Write(m.M41); bw.Write(m.M42); bw.Write(m.M43); bw.Write(m.M44);
    }

    private static byte[] PackGlb(string json, byte[] binBytes)
    {
        byte[] jsonBytes = Pad4(Encoding.UTF8.GetBytes(json), 0x20);
        int totalLen = 12 + 8 + jsonBytes.Length + 8 + binBytes.Length;

        using var outMs = new MemoryStream(totalLen);
        using var obw = new BinaryWriter(outMs);
        obw.Write(0x46546C67u); // "glTF"
        obw.Write(2u);
        obw.Write((uint)totalLen);
        obw.Write((uint)jsonBytes.Length);
        obw.Write(0x4E4F534Au); // "JSON"
        obw.Write(jsonBytes);
        obw.Write((uint)binBytes.Length);
        obw.Write(0x004E4942u); // "BIN\0"
        obw.Write(binBytes);
        obw.Flush();
        return outMs.ToArray();
    }

    private static byte[] DecompressWithOozWorker(string oozWorkerPath, ReadOnlyMemory<byte> compressed, int rawSize)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(oozWorkerPath);

        using System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ooz_worker.mjs.");

        Span<byte> reqHdr = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(reqHdr[..4], (uint)rawSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(reqHdr[4..8], (uint)compressed.Length);
        proc.StandardInput.BaseStream.Write(reqHdr);
        proc.StandardInput.BaseStream.Write(compressed.Span);
        proc.StandardInput.BaseStream.Flush();
        proc.StandardInput.Close();

        byte[] respHdr = new byte[8];
        proc.StandardOutput.BaseStream.ReadExactly(respHdr);
        uint status = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(respHdr.AsSpan(0, 4));
        int outLen = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(respHdr.AsSpan(4, 4)));
        byte[] payload = new byte[outLen];
        proc.StandardOutput.BaseStream.ReadExactly(payload);
        proc.WaitForExit(5000);
        if (status != 0)
        {
            throw new InvalidDataException($"ooz_worker error: {Encoding.UTF8.GetString(payload)}");
        }

        return payload;
    }

    private static byte[] Pad4(byte[] input, byte padByte)
    {
        int rem = input.Length & 3;
        if (rem == 0) return input;
        byte[] padded = new byte[input.Length + (4 - rem)];
        Buffer.BlockCopy(input, 0, padded, 0, input.Length);
        for (int i = input.Length; i < padded.Length; i++) padded[i] = padByte;
        return padded;
    }
}
