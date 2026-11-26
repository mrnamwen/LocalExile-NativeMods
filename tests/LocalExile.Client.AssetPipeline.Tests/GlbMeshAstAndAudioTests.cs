using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LocalExile.Client.AssetPipeline.Audio;
using LocalExile.Client.AssetPipeline.Mesh;
using Xunit;

namespace LocalExile.Client.AssetPipeline.Tests;

public sealed class GlbMeshAstAndAudioTests
{
    [Fact]
    public void GlbToPoeMeshConverter_ConvertsSyntheticTwoMaterialGlbToValid329PetBundle()
    {
        byte[] glb = BuildSyntheticTwoPrimitiveGlb();
        GlbScene scene = GlbParser.Parse(glb);
        Assert.Equal(2, scene.Primitives.Count);
        Assert.Equal(2, scene.Materials.Count);

        PoePetAssetBundle bundle = GlbToPoeMeshConverter.Convert(scene, new PoePetMeshOptions());
        Assert.Equal(6, bundle.VertexCount);
        Assert.Equal(2, bundle.TriangleCount);
        Assert.Equal(["bodyShape", "whiskersShape"], bundle.ShapeNames);

        // Validate .smd + .smd.header round-trip
        byte[] smd = bundle.Files["Art/Models/Mods/Maxwell/maxwell.smd"];
        byte[] smdHeader = bundle.Files["Art/Models/Mods/Maxwell/maxwell.smd.header"];
        SmdValidationInfo smdInfo = PoeSmdWriter.Validate(smd, smdHeader);
        Assert.Equal((byte)3, smdInfo.Version);
        Assert.Equal(6, smdInfo.VertexCount);
        Assert.Equal(2, smdInfo.TriangleCount);
        Assert.Equal(2, smdInfo.Shapes.Count);
        Assert.True(smdHeader.Length >= 280);

        // Validate .ast v7 spinning+floating skeleton
        byte[] ast = bundle.Files["Art/Models/Mods/Maxwell/maxwell.ast"];
        AstValidationInfo astInfo = PoeAstWriter.ValidateV7(ast);
        Assert.Equal((byte)7, astInfo.Version);
        Assert.Equal(1, astInfo.BoneCount);
        Assert.Equal(["happy", "idle", "run"], astInfo.AnimationNames);

        // Validate .ot has UTF-16LE BOM and extends Metadata/Pet/Pet with NO extra components
        byte[] otBytes = bundle.Files["Metadata/Pet/Mods/Maxwell/MaxwellPet.ot"];
        Assert.Equal(0xFF, otBytes[0]);
        Assert.Equal(0xFE, otBytes[1]);
        string otText = Encoding.Unicode.GetString(otBytes, 2, otBytes.Length - 2);
        Assert.Contains("extends \"Metadata/Pet/Pet\"", otText, StringComparison.Ordinal);
        Assert.DoesNotContain("StateMachine", otText, StringComparison.Ordinal);

        // Validate .sm, .mat, and .amd are BOM-less UTF-16LE (no 0xFF 0xFE prefix)
        foreach (string noBomPath in new[]
        {
            "Art/Models/Mods/Maxwell/maxwell.sm",
            "Art/Models/Mods/Maxwell/maxwell.amd",
            "Art/Models/Mods/Maxwell/textures/maxwell_body.mat",
            "Art/Models/Mods/Maxwell/textures/maxwell_whiskers.mat",
        })
        {
            byte[] noBomBytes = bundle.Files[noBomPath];
            Assert.True(noBomBytes.Length >= 2, $"Expected non-empty UTF-16LE content in '{noBomPath}'.");
            Assert.Equal(0, noBomBytes.Length & 1);
            Assert.False(noBomBytes[0] == 0xFF && noBomBytes[1] == 0xFE, $"Unexpected UTF-16LE BOM in '{noBomPath}'.");
            Assert.False(noBomBytes[0] == 0xFE && noBomBytes[1] == 0xFF, $"Unexpected UTF-16BE BOM in '{noBomPath}'.");
            string decoded = Encoding.Unicode.GetString(noBomBytes);
            Assert.DoesNotContain('\uFEFF', decoded);
            Assert.DoesNotContain('\0', decoded);
        }

        // Validate .mat files parse as version 4 JSON, use valid PoE 3.29 format labels (never "RGBA8"), and whiskers includes ForceAlpha
        string bodyMat = Encoding.Unicode.GetString(bundle.Files["Art/Models/Mods/Maxwell/textures/maxwell_body.mat"]);
        Assert.DoesNotContain("\"RGBA8\"", bodyMat, StringComparison.Ordinal);
        using (JsonDocument doc = JsonDocument.Parse(bodyMat))
        {
            Assert.Equal(4, doc.RootElement.GetProperty("version").GetInt32());
            JsonElement textures = doc.RootElement.GetProperty("textures");
            Assert.Equal("Uncompressed", textures[0].GetProperty("format").GetString());
            Assert.Equal("DXT1", textures[1].GetProperty("format").GetString());
        }

        string whiskersMat = Encoding.Unicode.GetString(bundle.Files["Art/Models/Mods/Maxwell/textures/maxwell_whiskers.mat"]);
        Assert.Contains("ForceAlpha.fxgraph", whiskersMat, StringComparison.Ordinal);
        Assert.DoesNotContain("\"RGBA8\"", whiskersMat, StringComparison.Ordinal);
        Assert.Contains("\"format\":\"Uncompressed\"", whiskersMat, StringComparison.Ordinal);

        // Validate SMD vertex nw=0x80 (-128) and tw=+/-127 matching stock 3.29.3d Rig_*.smd
        int vertsOffset = 53 + (smdInfo.Shapes.Count * 8) + (smdInfo.TriangleCount * 3 * 2);
        for (int i = 0; i < smdInfo.VertexCount; i++)
        {
            int vo = vertsOffset + (i * 32);
            Assert.Equal((byte)0x80, smd[vo + 15]);
            sbyte tw = unchecked((sbyte)smd[vo + 19]);
            Assert.True(tw is 127 or -127, $"Expected tw in {{-127, 127}}, got {tw}");
        }
    }

    [Fact]
    public void GlbToPoeMeshConverter_EmitsBackFacesForDoubleSidedAlphaBlendCards()
    {
        var scene = new GlbScene(
            Primitives:
            [
                new GlbPrimitive(
                    "whiskers",
                    MaterialIndex: 0,
                    Positions: [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
                    Normals: [new(0, 0, 1), new(0, 0, 1), new(0, 0, 1)],
                    Uvs: [new(0, 0), new(1, 0), new(0, 1)],
                    Indices: [0, 1, 2]),
            ],
            Materials: [new GlbMaterial("whiskers", "BLEND", DoubleSided: true, BaseColorImageIndex: 0)],
            Images: [new GlbImage("image/png", [1, 2, 3, 4])]);

        PoePetAssetBundle bundle = GlbToPoeMeshConverter.Convert(scene, new PoePetMeshOptions());
        Assert.Equal(6, bundle.VertexCount);
        Assert.Equal(2, bundle.TriangleCount);
        Assert.Equal([0], bundle.TextureBaseColorImageIndices);
        Assert.Equal([true], bundle.TextureIsAlphaBlend);
    }

    [Fact]
    public void ResolveGlbMaterialImage_PrefersGlbEmbeddedImageOverLooseZipFallback()
    {
        byte[] glbBody = [0xFF, 0xD8, 0x01, 0x02];
        byte[] glbWhiskers = [0x89, 0x50, 0x4E, 0x47];
        byte[] invertedZipBody = [0xAA, 0xBB, 0xCC, 0xDD];

        var scene = new GlbScene(
            Primitives:
            [
                new GlbPrimitive("body", 0, [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [new(0, 0, 1), new(0, 0, 1), new(0, 0, 1)], [new(0, 0), new(1, 0), new(0, 1)], [0, 1, 2]),
                new GlbPrimitive("whiskers", 1, [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [new(0, 0, 1), new(0, 0, 1), new(0, 0, 1)], [new(0, 0), new(1, 0), new(0, 1)], [0, 1, 2]),
            ],
            Materials:
            [
                new GlbMaterial("dingus", "OPAQUE", DoubleSided: true, BaseColorImageIndex: 0),
                new GlbMaterial("whiskers", "BLEND", DoubleSided: true, BaseColorImageIndex: 1),
            ],
            Images:
            [
                new GlbImage("image/jpeg", glbBody),
                new GlbImage("image/png", glbWhiskers),
            ]);

        PoePetAssetBundle bundle = GlbToPoeMeshConverter.Convert(scene, new PoePetMeshOptions());

        // Even when a loose zip texture is provided, 3D mesh albedo MUST use the GLB's own embedded image so it matches TEXCOORD_0!
        (byte[] resolvedBody, string bodyExt) = Program.ResolveGlbMaterialImage(scene, bundle, 0, invertedZipBody, ".jpeg", "body");
        Assert.Same(glbBody, resolvedBody);
        Assert.Equal(".jpeg", bodyExt);

        (byte[] resolvedWhiskers, string whiskersExt) = Program.ResolveGlbMaterialImage(scene, bundle, 1, invertedZipBody, ".png", "whiskers");
        Assert.Same(glbWhiskers, resolvedWhiskers);
        Assert.Equal(".png", whiskersExt);

        // Fallback to zip texture ONLY when GLB has no embedded image
        var emptyImgScene = scene with { Images = [] };
        (byte[] fallback, string fallbackExt) = Program.ResolveGlbMaterialImage(emptyImgScene, bundle, 0, invertedZipBody, ".jpeg", "body");
        Assert.Same(invertedZipBody, fallback);
        Assert.Equal(".jpeg", fallbackExt);
    }

    [Fact]
    public void PoeAstWriter_RejectsTruncatedOrInvalidAst()
    {
        byte[] ast = PoeAstWriter.BuildSpinFloatAst();
        AstValidationInfo info = PoeAstWriter.ValidateV7(ast);
        Assert.Equal(3, info.AnimationCount);

        byte[] truncated = ast[..^8];
        Assert.Throws<InvalidDataException>(() => PoeAstWriter.ValidateV7(truncated));
    }

    [Fact]
    public void AudioTranscoder_ValidatesSyntheticOggVorbisHeaderAndRejectsGarbage()
    {
        byte[] syntheticOgg = BuildMinimalOggVorbisPage(channels: 2, sampleRate: 48000);
        OggVorbisInfo info = AudioTranscoder.ValidateOggVorbis(syntheticOgg);
        Assert.Equal(2, info.Channels);
        Assert.Equal(48000, info.SampleRate);

        byte[] invalid = new byte[64];
        Assert.Throws<InvalidDataException>(() => AudioTranscoder.ValidateOggVorbis(invalid));
    }

    private static byte[] BuildMinimalOggVorbisPage(int channels, int sampleRate)
    {
        byte[] ogg = new byte[64];
        ogg[0] = (byte)'O';
        ogg[1] = (byte)'g';
        ogg[2] = (byte)'g';
        ogg[3] = (byte)'S';
        ogg[4] = 0;    // version
        ogg[5] = 0x02; // BOS
        ogg[26] = 1;   // 1 segment
        ogg[27] = 30;  // 30 bytes payload

        int p = 28;
        ogg[p + 0] = 0x01;
        Encoding.ASCII.GetBytes("vorbis").CopyTo(ogg, p + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(ogg.AsSpan(p + 7, 4), 0u);
        ogg[p + 11] = (byte)channels;
        BinaryPrimitives.WriteUInt32LittleEndian(ogg.AsSpan(p + 12, 4), (uint)sampleRate);
        return ogg;
    }

    private static byte[] BuildSyntheticTwoPrimitiveGlb()
    {
        // Build a minimal valid glTF 2.0 binary (.glb) with 2 meshes (opaque body + alpha whiskers) and a node transform.
        using var bin = new MemoryStream();
        using var bw = new BinaryWriter(bin);

        // BufferView 0: 6 uint16 indices (12 bytes)
        foreach (ushort idx in new ushort[] { 0, 1, 2, 0, 1, 2 })
        {
            bw.Write(idx);
        }

        // BufferView 1: 6 positions * 12 bytes = 72 bytes
        float[] positions =
        [
            -1f, 0f, -1f,  1f, 0f, -1f,  0f, 2f, 1f,
            -0.5f, 1f, 0f, 0.5f, 1f, 0f, 0f, 1.5f, 0.5f,
        ];
        foreach (float f in positions) bw.Write(f);

        // BufferView 2: 6 normals * 12 bytes = 72 bytes
        float[] normals =
        [
            0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f,
            0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f,
        ];
        foreach (float f in normals) bw.Write(f);

        // BufferView 3: 6 UVs * 8 bytes = 48 bytes
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
          "scenes": [{ "nodes": [0] }],
          "nodes": [
            { "name": "root", "scale": [2.0, 2.0, 2.0], "children": [1, 2] },
            { "name": "body_node", "mesh": 0 },
            { "name": "whiskers_node", "mesh": 1 }
          ],
          "materials": [
            { "name": "body", "alphaMode": "OPAQUE" },
            { "name": "whiskers", "alphaMode": "BLEND" }
          ],
          "meshes": [
            { "name": "body_mesh", "primitives": [{ "attributes": { "POSITION": 1, "NORMAL": 2, "TEXCOORD_0": 3 }, "indices": 0, "material": 0 }] },
            { "name": "whiskers_mesh", "primitives": [{ "attributes": { "POSITION": 5, "NORMAL": 6, "TEXCOORD_0": 7 }, "indices": 4, "material": 1 }] }
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
