using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using OozSharp;
using OozSharp.Exceptions;

namespace LocalExile.Client.AssetPipeline.Mesh;

public sealed record SpinFloatClipSpec(
    string Name,
    byte Fps,
    byte Playback,
    int TotalFrames,
    int Revolutions,
    float HoverHeight,
    float BobAmplitude,
    int BobCycles);

public sealed record AstValidationInfo(
    byte Version,
    int BoneCount,
    int AnimationCount,
    IReadOnlyList<string> AnimationNames);

/// <summary>
/// A single bone in a Path of Exile <c>.ast</c> skeleton hierarchy.
/// <see cref="BindPoseLocalMatrix"/> is the bone's local bind-pose transform in parent space
/// (DirectX / <see cref="Matrix4x4"/> row-vector layout <c>S * R * T</c>, verified against stock 3.29.3d <c>.ast</c> rigs).
/// </summary>
public sealed record AstBone(
    string Name,
    int ParentIndex,
    Matrix4x4 BindPoseLocalMatrix,
    byte Flags = 0);

public readonly record struct AstVec3Keyframe(float Frame, Vector3 Value);

public readonly record struct AstQuatKeyframe(float Frame, Quaternion Value);

public sealed record AstBoneTrack(
    int BoneIndex,
    IReadOnlyList<AstVec3Keyframe> ScaleKeys,
    IReadOnlyList<AstQuatKeyframe> RotationKeys,
    IReadOnlyList<AstVec3Keyframe> TranslationKeys,
    byte TrackKind = 0);

public sealed record AstAnimationClip(
    string Name,
    byte Fps,
    byte Playback,
    IReadOnlyList<AstBoneTrack> Tracks,
    float? DurationSeconds = null);

public sealed record AstSkeleton(
    byte Version,
    IReadOnlyList<AstBone> Bones,
    IReadOnlyList<AstAnimationClip> Animations,
    byte HeaderUnknown = 0,
    int LightCount = 0);

/// <summary>
/// Reads, writes, inspects, and validates Path of Exile 3.29.3d <c>.ast</c> skeletons (multi-bone hierarchical v7
/// uncompressed inline bone tracks — verified directly in <c>Game.exe</c> <c>0x142bf8090</c>, <c>0x142bf7a70</c>,
/// <c>0x142bf7350</c> to bypass Oodle compression — plus stock v8..v12 binary reader/inspector)
/// and matching BOM-less UTF-16LE CRLF <c>.amd</c> version 5 animation metadata files.
/// </summary>
public static class PoeAstWriter
{
    public const byte AstVersion = 7;
    private static readonly Encoding Utf16LeNoBom = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);

    /// <summary>
    /// Default Maxwell spinning + floating clips (<c>happy</c>, <c>idle</c>, <c>run</c>).
    /// Note: PoE game axes use +Z DOWN, so negative Z translation lifts the pet above the ground.
    /// </summary>
    public static IReadOnlyList<SpinFloatClipSpec> DefaultSpinFloatClips { get; } =
    [
        new("happy", Fps: 30, Playback: (byte)'o', TotalFrames: 45, Revolutions: 2, HoverHeight: 34f, BobAmplitude: 14f, BobCycles: 1),
        new("idle", Fps: 30, Playback: (byte)'l', TotalFrames: 90, Revolutions: 1, HoverHeight: 28f, BobAmplitude: 10f, BobCycles: 1),
        new("run", Fps: 30, Playback: (byte)'l', TotalFrames: 60, Revolutions: 1, HoverHeight: 32f, BobAmplitude: 12f, BobCycles: 1),
    ];

    /// <summary>
    /// Builds a 1-bone (<c>"root"</c>) <c>.ast</c> v7 binary skeleton with smooth 360° Z-spin + sinusoidal floating hover keyframes.
    /// Preserved 100% backward-compatible with the Maxwell Pet pipeline.
    /// </summary>
    public static byte[] BuildSpinFloatAst(IReadOnlyList<SpinFloatClipSpec>? clips = null, string rootBoneName = "root")
    {
        clips ??= DefaultSpinFloatClips;
        if (clips.Count == 0)
        {
            throw new ArgumentException("AST must contain at least one animation clip.", nameof(clips));
        }

        byte[] boneNameBytes = Encoding.ASCII.GetBytes(rootBoneName);
        if (boneNameBytes.Length is 0 or > 255)
        {
            throw new ArgumentException("Root bone name must be 1..255 ASCII bytes.", nameof(rootBoneName));
        }

        AstBone[] bones = [new AstBone(rootBoneName, ParentIndex: -1, Matrix4x4.Identity)];
        var astClips = new List<AstAnimationClip>(clips.Count);

        foreach (SpinFloatClipSpec clip in clips)
        {
            int rotSteps = Math.Max(4, clip.Revolutions * 12);
            int transSteps = Math.Max(8, clip.BobCycles * 16);

            var scaleKeys = new AstVec3Keyframe[]
            {
                new(0f, Vector3.One),
                new(clip.TotalFrames, Vector3.One),
            };

            var rotKeys = new AstQuatKeyframe[rotSteps + 1];
            for (int i = 0; i <= rotSteps; i++)
            {
                float u = (float)i / rotSteps;
                float frame = u * clip.TotalFrames;
                float angle = u * clip.Revolutions * MathF.Tau;
                float half = angle * 0.5f;
                rotKeys[i] = new AstQuatKeyframe(frame, new Quaternion(0f, 0f, MathF.Sin(half), MathF.Cos(half)));
            }

            var transKeys = new AstVec3Keyframe[transSteps + 1];
            for (int i = 0; i <= transSteps; i++)
            {
                float u = (float)i / transSteps;
                float frame = u * clip.TotalFrames;
                float z = -clip.HoverHeight - (clip.BobAmplitude * MathF.Sin(u * clip.BobCycles * MathF.Tau));
                transKeys[i] = new AstVec3Keyframe(frame, new Vector3(0f, 0f, z));
            }

            astClips.Add(new AstAnimationClip(
                clip.Name,
                clip.Fps,
                clip.Playback,
                [new AstBoneTrack(0, scaleKeys, rotKeys, transKeys)],
                DurationSeconds: (float)clip.TotalFrames / Math.Max((byte)1, clip.Fps)));
        }

        return WriteV7(bones, astClips);
    }

    /// <summary>
    /// Writes a multi-bone hierarchical <c>.ast</c> version 7 skeleton with arbitrary bone tree and multi-clip keyframe tracks.
    /// </summary>
    public static byte[] WriteV7(AstSkeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        return WriteV7(skeleton.Bones, skeleton.Animations);
    }

    /// <summary>
    /// Writes a multi-bone hierarchical <c>.ast</c> version 7 skeleton with arbitrary bone tree and multi-clip keyframe tracks.
    /// </summary>
    public static byte[] WriteV7(IReadOnlyList<AstBone> bones, IReadOnlyList<AstAnimationClip> animations)
    {
        ArgumentNullException.ThrowIfNull(bones);
        ArgumentNullException.ThrowIfNull(animations);

        if (bones.Count is 0 or > 254)
        {
            throw new ArgumentException($"AST bone count must be 1..254 (got {bones.Count}).", nameof(bones));
        }

        if (animations.Count == 0)
        {
            throw new ArgumentException("AST must contain at least one animation clip.", nameof(animations));
        }

        // Convert ParentIndex forest into PoE's Left-Child Right-Sibling (LCRS) byte pointers (0xFF = none).
        (byte[] siblings, byte[] children) = BuildLcrsPointers(bones);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // Header (8 bytes): version(1)=7, bone_count(1), unk(1)=0, anim_count(4), light_count(1)=0
        bw.Write(AstVersion);
        bw.Write((byte)bones.Count);
        bw.Write((byte)0);
        bw.Write((uint)animations.Count);
        bw.Write((byte)0);

        // Bones (v7: sibling(1), child(1), matrix[16](64), nameLen(1), name[nameLen]) -> 67 + nameLen bytes each
        for (int b = 0; b < bones.Count; b++)
        {
            AstBone bone = bones[b];
            byte[] boneNameBytes = Encoding.ASCII.GetBytes(bone.Name);
            if (boneNameBytes.Length is 0 or > 255)
            {
                throw new ArgumentException($"Bone[{b}] name '{bone.Name}' must be 1..255 ASCII bytes.", nameof(bones));
            }

            bw.Write(siblings[b]);
            bw.Write(children[b]);
            WriteMatrix4x4(bw, bone.BindPoseLocalMatrix);
            bw.Write((byte)boneNameBytes.Length);
            bw.Write(boneNameBytes);
        }

        // Animations (v7: 5-byte anim header + ASCII name + inline bone tracks, ZERO Oodle!)
        foreach (AstAnimationClip clip in animations)
        {
            byte[] animNameBytes = Encoding.ASCII.GetBytes(clip.Name);
            if (animNameBytes.Length is 0 or > 255)
            {
                throw new ArgumentException($"Animation name '{clip.Name}' must be 1..255 ASCII bytes.", nameof(animations));
            }

            if (clip.Fps == 0)
            {
                throw new ArgumentException($"Animation '{clip.Name}' FPS must be > 0.", nameof(animations));
            }

            if (clip.Playback is not ((byte)'l' or (byte)'n' or (byte)'o'))
            {
                throw new ArgumentException($"Animation '{clip.Name}' playback must be 'l', 'o', or 'n' (got 0x{clip.Playback:X2}).", nameof(animations));
            }

            if (clip.Tracks.Count > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(animations), $"Animation '{clip.Name}' track count {clip.Tracks.Count} exceeds 255.");
            }

            // 5-byte anim header (0x142bf7ac7): trackCount(1), unk(1), fps(1), playback(1), nameLen(1)
            bw.Write((byte)clip.Tracks.Count);
            bw.Write((byte)0);
            bw.Write(clip.Fps);
            bw.Write(clip.Playback);
            bw.Write((byte)animNameBytes.Length);
            bw.Write(animNameBytes);

            foreach (AstBoneTrack track in clip.Tracks)
            {
                if ((uint)track.BoneIndex >= (uint)bones.Count)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(animations),
                        $"Animation '{clip.Name}' track bone index {track.BoneIndex} out of range [0..{bones.Count - 1}].");
                }

                // Track header (version <= 9: 29 bytes, 0x142bf73d3):
                // kind(1), boneIndex(4), 6 x keyCount(4)
                bw.Write(track.TrackKind);
                bw.Write((uint)track.BoneIndex);
                bw.Write((uint)track.ScaleKeys.Count);
                bw.Write((uint)track.RotationKeys.Count);
                bw.Write((uint)track.TranslationKeys.Count);
                bw.Write(0u);
                bw.Write(0u);
                bw.Write(0u);

                foreach (AstVec3Keyframe sk in track.ScaleKeys)
                {
                    WriteVec3Key(bw, sk.Frame, sk.Value);
                }

                foreach (AstQuatKeyframe rk in track.RotationKeys)
                {
                    WriteQuatKey(bw, rk.Frame, rk.Value.X, rk.Value.Y, rk.Value.Z, rk.Value.W);
                }

                foreach (AstVec3Keyframe tk in track.TranslationKeys)
                {
                    WriteVec3Key(bw, tk.Frame, tk.Value);
                }
            }
        }

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Computes world-space bind matrices (<c>World[i] = Local[i] * World[parent]</c>) from a bone hierarchy.
    /// </summary>
    public static Matrix4x4[] ComputeWorldBindMatrices(IReadOnlyList<AstBone> bones)
    {
        ArgumentNullException.ThrowIfNull(bones);
        Matrix4x4[] world = new Matrix4x4[bones.Count];
        bool[] computed = new bool[bones.Count];

        Matrix4x4 Resolve(int index, int depth)
        {
            if (depth > bones.Count)
            {
                throw new InvalidDataException($"Cycle detected in AST bone hierarchy at bone {index}.");
            }

            if (computed[index])
            {
                return world[index];
            }

            AstBone bone = bones[index];
            Matrix4x4 m = bone.ParentIndex >= 0
                ? bone.BindPoseLocalMatrix * Resolve(bone.ParentIndex, depth + 1)
                : bone.BindPoseLocalMatrix;
            world[index] = m;
            computed[index] = true;
            return m;
        }

        for (int i = 0; i < bones.Count; i++)
        {
            Resolve(i, 0);
        }

        return world;
    }

    /// <summary>
    /// Computes inverse bind matrices (<c>Inverse(WorldBind[i])</c>) for skinning verification.
    /// </summary>
    public static Matrix4x4[] ComputeInverseBindMatrices(IReadOnlyList<AstBone> bones)
    {
        Matrix4x4[] worlds = ComputeWorldBindMatrices(bones);
        Matrix4x4[] inv = new Matrix4x4[worlds.Length];
        for (int i = 0; i < worlds.Length; i++)
        {
            inv[i] = Matrix4x4.Invert(worlds[i], out Matrix4x4 m) ? m : Matrix4x4.Identity;
        }

        return inv;
    }

    /// <summary>
    /// Constructs <see cref="AstBone"/> local bind-pose matrices from inverse bind matrices and parent indices:
    /// <c>World[i] = Inverse(InvBind[i])</c>, <c>Local[i] = World[i] * InvBind[parent]</c>.
    /// </summary>
    public static AstBone[] BuildBonesFromInverseBindMatrices(
        IReadOnlyList<string> names,
        IReadOnlyList<int> parentIndices,
        IReadOnlyList<Matrix4x4> inverseBindMatrices)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(parentIndices);
        ArgumentNullException.ThrowIfNull(inverseBindMatrices);
        if (names.Count != parentIndices.Count || names.Count != inverseBindMatrices.Count)
        {
            throw new ArgumentException("Bone names, parent indices, and inverse bind matrices must have equal length.");
        }

        var worldBind = new Matrix4x4[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            worldBind[i] = Matrix4x4.Invert(inverseBindMatrices[i], out Matrix4x4 w) ? w : Matrix4x4.Identity;
        }

        var bones = new AstBone[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            int p = parentIndices[i];
            Matrix4x4 local = p >= 0 ? worldBind[i] * inverseBindMatrices[p] : worldBind[i];
            bones[i] = new AstBone(names[i], p, local);
        }

        return bones;
    }

    /// <summary>
    /// Builds a matching <c>.amd</c> version 5 animation metadata file (BOM-less UTF-16LE CRLF, matching stock <c>rig.amd</c>).
    /// </summary>
    public static byte[] BuildAmd(IReadOnlyList<SpinFloatClipSpec>? clips = null)
    {
        clips ??= DefaultSpinFloatClips;
        var entries = clips.Select(c => (
            c.Name,
            c.Playback,
            DurationMs: (int)MathF.Round((float)c.TotalFrames / Math.Max((byte)1, c.Fps) * 1000f))).ToList();
        return BuildAmdCore(entries);
    }

    /// <summary>
    /// Builds a matching <c>.amd</c> version 5 animation metadata file (BOM-less UTF-16LE CRLF) from multi-bone <see cref="AstAnimationClip"/> definitions.
    /// </summary>
    public static byte[] BuildAmd(IReadOnlyList<AstAnimationClip> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var entries = new List<(string Name, byte Playback, int DurationMs)>(clips.Count);
        foreach (AstAnimationClip clip in clips)
        {
            float durationSec;
            if (clip.DurationSeconds.HasValue && clip.DurationSeconds.Value > 0f)
            {
                durationSec = clip.DurationSeconds.Value;
            }
            else
            {
                float maxFrame = 0f;
                foreach (AstBoneTrack t in clip.Tracks)
                {
                    if (t.ScaleKeys.Count > 0) maxFrame = MathF.Max(maxFrame, t.ScaleKeys[^1].Frame);
                    if (t.RotationKeys.Count > 0) maxFrame = MathF.Max(maxFrame, t.RotationKeys[^1].Frame);
                    if (t.TranslationKeys.Count > 0) maxFrame = MathF.Max(maxFrame, t.TranslationKeys[^1].Frame);
                }

                durationSec = maxFrame / Math.Max((byte)1, clip.Fps);
            }

            int durationMs = Math.Max(1, (int)MathF.Round(durationSec * 1000f));
            entries.Add((clip.Name, clip.Playback, durationMs));
        }

        return BuildAmdCore(entries);
    }

    /// <summary>
    /// Round-trip parses and validates a version 7 <c>.ast</c> file against <c>Game.exe</c>'s exact byte consumption rules.
    /// </summary>
    public static AstValidationInfo ValidateV7(ReadOnlySpan<byte> astBytes)
    {
        if (astBytes.Length < 8)
        {
            throw new InvalidDataException($"AST file too short ({astBytes.Length} bytes).");
        }

        if (astBytes[0] != AstVersion)
        {
            throw new InvalidDataException($"Expected AST v{AstVersion}, got v{astBytes[0]}.");
        }

        AstSkeleton skeleton = Read(astBytes);
        return new AstValidationInfo(
            skeleton.Version,
            skeleton.Bones.Count,
            skeleton.Animations.Count,
            [.. skeleton.Animations.Select(a => a.Name)]);
    }

    /// <summary>
    /// Full binary reader for PoE 3.29.3d <c>.ast</c> skeletons.
    /// Supports v7 (inline uncompressed tracks, exact round-trip) as well as stock v8..v12
    /// (extended bone flags, Oodle-bundled animation track blobs decompressed via Kraken or an optional custom decompressor).
    /// </summary>
    public static AstSkeleton Read(
        ReadOnlySpan<byte> astBytes,
        Func<ReadOnlyMemory<byte>, int, byte[]>? oodleDecompressor = null)
    {
        if (astBytes.Length < 8)
        {
            throw new InvalidDataException($"AST file too short ({astBytes.Length} bytes).");
        }

        byte version = astBytes[0];
        if (version is < 7 or > 12)
        {
            throw new InvalidDataException($"Unsupported AST version v{version} (supported: v7..v12).");
        }

        int boneCount = astBytes[1];
        byte headerUnk = astBytes[2];
        if (boneCount < 1)
        {
            throw new InvalidDataException("AST must have at least 1 bone.");
        }

        int animCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(astBytes.Slice(3, 4)));
        int lightCount = astBytes[7];
        int pos = 8;

        byte[] siblings = new byte[boneCount];
        byte[] children = new byte[boneCount];
        Matrix4x4[] matrices = new Matrix4x4[boneCount];
        string[] boneNames = new string[boneCount];
        byte[] boneFlags = new byte[boneCount];

        int boneHeaderSize = version >= 8 ? 68 : 67;
        for (int b = 0; b < boneCount; b++)
        {
            if (pos + boneHeaderSize > astBytes.Length)
            {
                throw new InvalidDataException($"AST truncated in bone {b}.");
            }

            siblings[b] = astBytes[pos];
            children[b] = astBytes[pos + 1];
            matrices[b] = ReadMatrix4x4(astBytes.Slice(pos + 2, 64));
            byte nameLen = astBytes[pos + 66];
            if (version >= 8)
            {
                boneFlags[b] = astBytes[pos + 67];
            }

            pos += boneHeaderSize;
            if (pos + nameLen > astBytes.Length)
            {
                throw new InvalidDataException($"AST truncated in bone {b} name.");
            }

            boneNames[b] = Encoding.ASCII.GetString(astBytes.Slice(pos, nameLen));
            pos += nameLen;
        }

        int[] parentIndices = ReconstructParentIndices(siblings, children);
        var bones = new AstBone[boneCount];
        for (int b = 0; b < boneCount; b++)
        {
            bones[b] = new AstBone(boneNames[b], parentIndices[b], matrices[b], boneFlags[b]);
        }

        for (int l = 0; l < lightCount; l++)
        {
            if (pos >= astBytes.Length)
            {
                throw new InvalidDataException($"AST truncated in light {l}.");
            }

            byte nameLen = astBytes[pos];
            pos += 0x38 + nameLen;
            if (pos > astBytes.Length)
            {
                throw new InvalidDataException($"AST truncated in light {l} payload.");
            }
        }

        var animations = new List<AstAnimationClip>(animCount);
        if (version == 7)
        {
            for (int a = 0; a < animCount; a++)
            {
                if (pos + 5 > astBytes.Length)
                {
                    throw new InvalidDataException($"AST truncated in animation {a} header.");
                }

                int trackCount = astBytes[pos];
                byte fps = astBytes[pos + 2];
                byte playback = astBytes[pos + 3];
                int nameLen = astBytes[pos + 4];
                pos += 5;

                if (fps == 0 || (playback != (byte)'l' && playback != (byte)'n' && playback != (byte)'o'))
                {
                    throw new InvalidDataException($"Invalid animation {a} header (fps={fps}, playback=0x{playback:X2}).");
                }

                if (pos + nameLen > astBytes.Length)
                {
                    throw new InvalidDataException($"AST truncated in animation {a} name.");
                }

                string name = Encoding.ASCII.GetString(astBytes.Slice(pos, nameLen));
                pos += nameLen;

                List<AstBoneTrack> tracks = ReadTracks(astBytes, ref pos, trackCount, boneCount, version, name);
                animations.Add(new AstAnimationClip(name, fps, playback, tracks));
            }

            if (pos != astBytes.Length)
            {
                throw new InvalidDataException($"AST trailing bytes mismatch: consumed {pos} of {astBytes.Length} bytes.");
            }
        }
        else
        {
            // v8..v12: animation headers precede a single Oodle-compressed PoE bundle containing all track blobs.
            var animMetas = new List<(string Name, int TrackCount, byte Fps, byte Playback, int BlobOffset, int BlobSize)>(animCount);
            for (int a = 0; a < animCount; a++)
            {
                int trackCount;
                byte fps;
                byte playback;
                int nameLen;
                int blobOffset;
                int blobSize;

                if (version <= 9)
                {
                    if (pos + 13 > astBytes.Length)
                    {
                        throw new InvalidDataException($"AST v{version} truncated in animation {a} header.");
                    }

                    trackCount = astBytes[pos];
                    fps = astBytes[pos + 2];
                    playback = astBytes[pos + 3];
                    nameLen = astBytes[pos + 4];
                    blobOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(astBytes.Slice(pos + 5, 4)));
                    blobSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(astBytes.Slice(pos + 9, 4)));
                    pos += 13;
                }
                else
                {
                    if (pos + 15 > astBytes.Length)
                    {
                        throw new InvalidDataException($"AST v{version} truncated in animation {a} header.");
                    }

                    trackCount = astBytes[pos];
                    fps = astBytes[pos + 2];
                    playback = astBytes[pos + 3];
                    nameLen = BinaryPrimitives.ReadUInt16LittleEndian(astBytes.Slice(pos + 5, 2));
                    blobOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(astBytes.Slice(pos + 7, 4)));
                    blobSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(astBytes.Slice(pos + 11, 4)));
                    pos += 15;
                }

                if (pos + nameLen > astBytes.Length)
                {
                    throw new InvalidDataException($"AST v{version} truncated in animation {a} name.");
                }

                string name = Encoding.ASCII.GetString(astBytes.Slice(pos, nameLen));
                pos += nameLen;
                animMetas.Add((name, trackCount, fps, playback, blobOffset, blobSize));
            }

            byte[]? decompressedBlob = TryDecompressAstBundle(astBytes[pos..], oodleDecompressor);
            foreach (var meta in animMetas)
            {
                List<AstBoneTrack> tracks = [];
                if (decompressedBlob is not null &&
                    meta.BlobOffset >= 0 &&
                    meta.BlobSize >= 0 &&
                    meta.BlobOffset + meta.BlobSize <= decompressedBlob.Length)
                {
                    int localPos = 0;
                    ReadOnlySpan<byte> slice = decompressedBlob.AsSpan(meta.BlobOffset, meta.BlobSize);
                    try
                    {
                        tracks = ReadTracks(slice, ref localPos, meta.TrackCount, boneCount, version, meta.Name);
                    }
                    catch (InvalidDataException)
                    {
                        tracks = [];
                    }
                }

                animations.Add(new AstAnimationClip(meta.Name, meta.Fps, meta.Playback, tracks));
            }
        }

        return new AstSkeleton(version, bones, animations, headerUnk, lightCount);
    }

    /// <summary>
    /// Inspects any PoE 3.29.3d <c>.ast</c> file (v7..v12) and returns summary metadata.
    /// </summary>
    public static AstValidationInfo Inspect(ReadOnlySpan<byte> astBytes)
    {
        AstSkeleton skeleton = Read(astBytes);
        return new AstValidationInfo(
            skeleton.Version,
            skeleton.Bones.Count,
            skeleton.Animations.Count,
            [.. skeleton.Animations.Select(a => a.Name)]);
    }

    private static List<AstBoneTrack> ReadTracks(
        ReadOnlySpan<byte> data,
        ref int pos,
        int trackCount,
        int boneCount,
        byte version,
        string animName)
    {
        int headerBytes = version <= 9 ? 29 : 33;
        var tracks = new List<AstBoneTrack>(trackCount);

        for (int t = 0; t < trackCount; t++)
        {
            if (pos + headerBytes > data.Length)
            {
                throw new InvalidDataException($"AST truncated in animation '{animName}' track {t} header.");
            }

            byte kind = data[pos];
            uint boneIdx = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 1, 4));
            if (boneIdx >= (uint)boneCount)
            {
                throw new InvalidDataException($"Track bone index {boneIdx} out of range (boneCount={boneCount}).");
            }

            uint c0 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 5, 4));
            uint c1 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 9, 4));
            uint c2 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 13, 4));
            uint c3 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 17, 4));
            uint c4 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 21, 4));
            uint c5 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 25, 4));
            // Note: v10+ track headers include an extra 4-byte field at offset +29 (track flags, not a keyframe array count).
            pos += headerBytes;

            int trackDataBytes = checked((int)((c0 * 16u) + (c1 * 20u) + (c2 * 16u) + (c3 * 16u) + (c4 * 20u) + (c5 * 16u)));
            if (pos + trackDataBytes > data.Length)
            {
                throw new InvalidDataException($"AST truncated in animation '{animName}' track {t} keyframes.");
            }

            var scaleKeys = new AstVec3Keyframe[c0];
            for (int i = 0; i < scaleKeys.Length; i++)
            {
                scaleKeys[i] = ReadVec3Key(data.Slice(pos, 16));
                pos += 16;
            }

            var rotKeys = new AstQuatKeyframe[c1];
            for (int i = 0; i < rotKeys.Length; i++)
            {
                rotKeys[i] = ReadQuatKey(data.Slice(pos, 20));
                pos += 20;
            }

            var transKeys = new AstVec3Keyframe[c2];
            for (int i = 0; i < transKeys.Length; i++)
            {
                transKeys[i] = ReadVec3Key(data.Slice(pos, 16));
                pos += 16;
            }

            pos += checked((int)((c3 * 16u) + (c4 * 20u) + (c5 * 16u)));
            tracks.Add(new AstBoneTrack(checked((int)boneIdx), scaleKeys, rotKeys, transKeys, kind));
        }

        return tracks;
    }

    private static byte[]? TryDecompressAstBundle(
        ReadOnlySpan<byte> bundle,
        Func<ReadOnlyMemory<byte>, int, byte[]>? oodleDecompressor)
    {
        if (bundle.Length < 64)
        {
            return null;
        }

        try
        {
            int decompressedSize = BinaryPrimitives.ReadInt32LittleEndian(bundle[..4]);
            int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(bundle.Slice(36, 4));
            int granularity = BinaryPrimitives.ReadInt32LittleEndian(bundle.Slice(40, 4));
            if (decompressedSize <= 0 || chunkCount <= 0 || granularity <= 0 || 60 + (chunkCount * 4) > bundle.Length)
            {
                return null;
            }

            byte[] output = new byte[decompressedSize];
            int chunkBegin = 60 + (chunkCount * 4);
            int decompOffset = 0;
            Kraken? kraken = oodleDecompressor is null ? new Kraken() : null;

            for (int idx = 0; idx < chunkCount; idx++)
            {
                int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bundle.Slice(60 + (idx * 4), 4));
                int decompChunkSize = idx == chunkCount - 1 ? decompressedSize - decompOffset : granularity;
                if (chunkSize < 0 || chunkBegin + chunkSize > bundle.Length || decompChunkSize < 0 || decompOffset + decompChunkSize > output.Length)
                {
                    return null;
                }

                ReadOnlySpan<byte> comp = bundle.Slice(chunkBegin, chunkSize);
                if (chunkSize == decompChunkSize)
                {
                    comp.CopyTo(output.AsSpan(decompOffset, decompChunkSize));
                }
                else if (oodleDecompressor is not null)
                {
                    byte[] raw = oodleDecompressor(comp.ToArray(), decompChunkSize);
                    raw.AsSpan(0, decompChunkSize).CopyTo(output.AsSpan(decompOffset, decompChunkSize));
                }
                else
                {
                    ReadOnlyMemory<byte> raw = kraken!.Decompress(comp, decompChunkSize);
                    raw.Span[..decompChunkSize].CopyTo(output.AsSpan(decompOffset, decompChunkSize));
                }

                decompOffset += decompChunkSize;
                chunkBegin += chunkSize;
            }

            return output;
        }
        catch (Exception ex) when (ex is DecoderException or ArgumentException or IndexOutOfRangeException or InvalidDataException or NotImplementedException or NotSupportedException)
        {
            return null;
        }
    }

    private static (byte[] Siblings, byte[] Children) BuildLcrsPointers(IReadOnlyList<AstBone> bones)
    {
        int n = bones.Count;
        byte[] siblings = new byte[n];
        byte[] children = new byte[n];
        Array.Fill(siblings, (byte)0xFF);
        Array.Fill(children, (byte)0xFF);

        var roots = new List<int>();
        var childLists = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            childLists[i] = [];
        }

        for (int i = 0; i < n; i++)
        {
            int parent = bones[i].ParentIndex;
            if (parent < 0)
            {
                roots.Add(i);
            }
            else if (parent >= n || parent == i)
            {
                throw new ArgumentException($"Bone[{i}] '{bones[i].Name}' has invalid parent index {parent}.", nameof(bones));
            }
            else
            {
                childLists[parent].Add(i);
            }
        }

        if (roots.Count == 0)
        {
            throw new ArgumentException("AST skeleton must have at least one root bone (ParentIndex == -1).", nameof(bones));
        }

        if (roots[0] != 0)
        {
            throw new ArgumentException("AST bone 0 must be a root bone (ParentIndex == -1).", nameof(bones));
        }

        for (int i = 0; i + 1 < roots.Count; i++)
        {
            siblings[roots[i]] = (byte)roots[i + 1];
        }

        for (int p = 0; p < n; p++)
        {
            List<int> ch = childLists[p];
            if (ch.Count == 0)
            {
                continue;
            }

            children[p] = (byte)ch[0];
            for (int i = 0; i + 1 < ch.Count; i++)
            {
                siblings[ch[i]] = (byte)ch[i + 1];
            }
        }

        // Verify no disconnected cycle exists (every bone must be reachable from bone 0).
        int[] verifiedParents = ReconstructParentIndices(siblings, children);
        for (int i = 0; i < n; i++)
        {
            if (verifiedParents[i] != bones[i].ParentIndex)
            {
                throw new ArgumentException($"AST bone hierarchy contains a cycle or unreachable bone at index {i}.", nameof(bones));
            }
        }

        return (siblings, children);
    }

    private static int[] ReconstructParentIndices(byte[] siblings, byte[] children)
    {
        int n = siblings.Length;
        int[] parents = new int[n];
        bool[] visited = new bool[n];
        Array.Fill(parents, -1);

        void Walk(int idx, int parent)
        {
            while (idx != 0xFF)
            {
                if (idx >= n || visited[idx])
                {
                    break;
                }

                visited[idx] = true;
                parents[idx] = parent;
                if (children[idx] != 0xFF)
                {
                    Walk(children[idx], idx);
                }

                idx = siblings[idx];
            }
        }

        Walk(0, -1);
        return parents;
    }

    private static byte[] BuildAmdCore(List<(string Name, byte Playback, int DurationMs)> clips)
    {
        var sb = new StringBuilder();
        sb.Append("version 5\r\n");
        sb.Append(clips.Count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");

        foreach (var clip in clips)
        {
            string playbackWord = clip.Playback == (byte)'o' ? "once" : "loop";
            sb.Append('"').Append(clip.Name).Append("\"\r\n");
            sb.Append('\t').Append(playbackWord).Append("\r\n");
            sb.Append('\t').Append(clip.DurationMs.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("\t0\r\n");
            sb.Append("\t0\r\n");
            sb.Append("\t0\r\n");
            sb.Append("\r\n");
        }

        sb.Append("BoneGroups 0\r\n");
        return Utf16LeNoBom.GetBytes(sb.ToString());
    }

    private static void WriteMatrix4x4(BinaryWriter bw, Matrix4x4 m)
    {
        bw.Write(m.M11); bw.Write(m.M12); bw.Write(m.M13); bw.Write(m.M14);
        bw.Write(m.M21); bw.Write(m.M22); bw.Write(m.M23); bw.Write(m.M24);
        bw.Write(m.M31); bw.Write(m.M32); bw.Write(m.M33); bw.Write(m.M34);
        bw.Write(m.M41); bw.Write(m.M42); bw.Write(m.M43); bw.Write(m.M44);
    }

    private static Matrix4x4 ReadMatrix4x4(ReadOnlySpan<byte> buf) => new(
        BinaryPrimitives.ReadSingleLittleEndian(buf[0..4]),
        BinaryPrimitives.ReadSingleLittleEndian(buf[4..8]),
        BinaryPrimitives.ReadSingleLittleEndian(buf[8..12]),
        BinaryPrimitives.ReadSingleLittleEndian(buf[12..16]),
        BinaryPrimitives.ReadSingleLittleEndian(buf[16..20]),
        BinaryPrimitives.ReadSingleLittleEndian(buf[20..24]),
        BinaryPrimitives.ReadSingleLittleEndian(buf[24..28]),
        BinaryPrimitives.ReadSingleLittleEndian(buf[28..32]),
        BinaryPrimitives.ReadSingleLittleEndian(buf[32..36]),
        BinaryPrimitives.ReadSingleLittleEndian(buf[36..40]),
        BinaryPrimitives.ReadSingleLittleEndian(buf[40..44]),
        BinaryPrimitives.ReadSingleLittleEndian(buf[44..48]),
        BinaryPrimitives.ReadSingleLittleEndian(buf[48..52]),
        BinaryPrimitives.ReadSingleLittleEndian(buf[52..56]),
        BinaryPrimitives.ReadSingleLittleEndian(buf[56..60]),
        BinaryPrimitives.ReadSingleLittleEndian(buf[60..64]));

    private static void WriteVec3Key(BinaryWriter bw, float frame, Vector3 value)
    {
        bw.Write(frame);
        bw.Write(value.X);
        bw.Write(value.Y);
        bw.Write(value.Z);
    }

    private static AstVec3Keyframe ReadVec3Key(ReadOnlySpan<byte> buf) => new(
        BinaryPrimitives.ReadSingleLittleEndian(buf[0..4]),
        new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(buf[4..8]),
            BinaryPrimitives.ReadSingleLittleEndian(buf[8..12]),
            BinaryPrimitives.ReadSingleLittleEndian(buf[12..16])));

    private static void WriteQuatKey(BinaryWriter bw, float frame, float x, float y, float z, float w)
    {
        bw.Write(frame);
        bw.Write(x);
        bw.Write(y);
        bw.Write(z);
        bw.Write(w);
    }

    private static AstQuatKeyframe ReadQuatKey(ReadOnlySpan<byte> buf) => new(
        BinaryPrimitives.ReadSingleLittleEndian(buf[0..4]),
        new Quaternion(
            BinaryPrimitives.ReadSingleLittleEndian(buf[4..8]),
            BinaryPrimitives.ReadSingleLittleEndian(buf[8..12]),
            BinaryPrimitives.ReadSingleLittleEndian(buf[12..16]),
            BinaryPrimitives.ReadSingleLittleEndian(buf[16..20])));
}
