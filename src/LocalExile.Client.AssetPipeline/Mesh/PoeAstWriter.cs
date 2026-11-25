using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

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
/// Writes and validates Path of Exile 3.29.3d <c>.ast</c> version 7 skeletons (uncompressed inline bone tracks —
/// verified directly in <c>Game.exe</c> <c>0x142bf8090</c>, <c>0x142bf7a70</c>, <c>0x142bf7350</c> to bypass Oodle compression)
/// and matching <c>.amd</c> version 5 animation metadata files.
/// </summary>
public static class PoeAstWriter
{
    public const byte AstVersion = 7;

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

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // Header (8 bytes): version(1)=7, bone_count(1)=1, unk(1)=0, anim_count(4), light_count(1)=0
        bw.Write(AstVersion);
        bw.Write((byte)1);
        bw.Write((byte)0);
        bw.Write((uint)clips.Count);
        bw.Write((byte)0);

        // Bone 0 (version < 8: sibling(1), child(1), matrix[16](64), nameLen(1), name[nameLen]) -> 67 + nameLen bytes
        bw.Write((byte)0xFF); // sibling = none
        bw.Write((byte)0xFF); // child = none
        ReadOnlySpan<float> identity =
        [
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 0f, 0f, 1f,
        ];
        foreach (float m in identity)
        {
            bw.Write(m);
        }

        bw.Write((byte)boneNameBytes.Length);
        bw.Write(boneNameBytes);

        // Animations (version <= 7: 5-byte anim header + ASCII name + inline bone tracks, ZERO Oodle!)
        foreach (SpinFloatClipSpec clip in clips)
        {
            byte[] animNameBytes = Encoding.ASCII.GetBytes(clip.Name);
            if (animNameBytes.Length is 0 or > 255)
            {
                throw new ArgumentException($"Animation name '{clip.Name}' must be 1..255 ASCII bytes.", nameof(clips));
            }

            // 5-byte anim header (0x142bf7ac7): trackCount(1), unk(1), fps(1), playback(1), nameLen(1)
            bw.Write((byte)1); // 1 bone track (bone 0)
            bw.Write((byte)0);
            bw.Write(clip.Fps);
            bw.Write(clip.Playback);
            bw.Write((byte)animNameBytes.Length);
            bw.Write(animNameBytes);

            // Sample rotation every 30 degrees (12 steps per revolution) so slerp always follows exact 30° shortest arcs.
            int rotSteps = Math.Max(4, clip.Revolutions * 12);
            int transSteps = Math.Max(8, clip.BobCycles * 16);

            // Track header (version <= 9: 0x1d = 29 bytes, 0x142bf73d3):
            // kind(1)=0, boneIndex(4)=0, 6 x keyCount(4)
            bw.Write((byte)0);                  // kind = bone track
            bw.Write(0u);                       // bone index = 0
            bw.Write(2u);                       // scale key count
            bw.Write((uint)(rotSteps + 1));     // rotation key count
            bw.Write((uint)(transSteps + 1));   // translation key count
            bw.Write(0u);                       // extra1 count
            bw.Write(0u);                       // extra2 count
            bw.Write(0u);                       // extra3 count

            // Scale keys (16 bytes each: frame, sx, sy, sz)
            WriteVec3Key(bw, 0f, Vector3.One);
            WriteVec3Key(bw, clip.TotalFrames, Vector3.One);

            // Rotation keys (20 bytes each: frame, qx, qy, qz, qw) — spin around Z axis
            for (int i = 0; i <= rotSteps; i++)
            {
                float u = (float)i / rotSteps;
                float frame = u * clip.TotalFrames;
                float angle = u * clip.Revolutions * MathF.Tau;
                float half = angle * 0.5f;
                // Keep consecutive quaternions in the positive half-space along the arc;
                // slerp takes the shorter arc across each 30° step.
                WriteQuatKey(bw, frame, 0f, 0f, MathF.Sin(half), MathF.Cos(half));
            }

            // Translation keys (16 bytes each: frame, tx, ty, tz) — PoE +Z is DOWN, so negative Z floats upward
            for (int i = 0; i <= transSteps; i++)
            {
                float u = (float)i / transSteps;
                float frame = u * clip.TotalFrames;
                float z = -clip.HoverHeight - (clip.BobAmplitude * MathF.Sin(u * clip.BobCycles * MathF.Tau));
                WriteVec3Key(bw, frame, new Vector3(0f, 0f, z));
            }
        }

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Builds a matching <c>.amd</c> version 5 animation metadata file (UTF-16LE without BOM, matching stock <c>rig.amd</c>).
    /// </summary>
    public static byte[] BuildAmd(IReadOnlyList<SpinFloatClipSpec>? clips = null)
    {
        clips ??= DefaultSpinFloatClips;
        var sb = new StringBuilder();
        sb.Append("version 5\r\n");
        sb.Append(clips.Count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");

        foreach (SpinFloatClipSpec clip in clips)
        {
            string playbackWord = clip.Playback == (byte)'o' ? "once" : "loop";
            int durationMs = (int)MathF.Round((float)clip.TotalFrames / Math.Max((byte)1, clip.Fps) * 1000f);
            sb.Append('"').Append(clip.Name).Append("\"\r\n");
            sb.Append('\t').Append(playbackWord).Append("\r\n");
            sb.Append('\t').Append(durationMs.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("\t0\r\n");
            sb.Append("\t0\r\n");
            sb.Append("\t0\r\n");
            sb.Append("\r\n");
        }

        sb.Append("BoneGroups 0\r\n");
        return Encoding.Unicode.GetBytes(sb.ToString());
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

        byte version = astBytes[0];
        if (version != AstVersion)
        {
            throw new InvalidDataException($"Expected AST v{AstVersion}, got v{version}.");
        }

        int boneCount = astBytes[1];
        if (boneCount < 1)
        {
            throw new InvalidDataException("AST must have at least 1 bone.");
        }

        int animCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(astBytes.Slice(3, 4)));
        int lightCount = astBytes[7];
        int pos = 8;

        for (int b = 0; b < boneCount; b++)
        {
            if (pos + 0x43 > astBytes.Length)
            {
                throw new InvalidDataException($"AST truncated in bone {b}.");
            }

            byte nameLen = astBytes[pos + 0x42];
            pos += 0x43 + nameLen;
            if (pos > astBytes.Length)
            {
                throw new InvalidDataException($"AST truncated in bone {b} name.");
            }
        }

        for (int l = 0; l < lightCount; l++)
        {
            byte nameLen = astBytes[pos];
            pos += 0x38 + nameLen;
        }

        var animNames = new List<string>(animCount);
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
            animNames.Add(name);
            pos += nameLen;

            for (int t = 0; t < trackCount; t++)
            {
                if (pos + 29 > astBytes.Length)
                {
                    throw new InvalidDataException($"AST truncated in animation '{name}' track {t} header.");
                }

                uint boneIdx = BinaryPrimitives.ReadUInt32LittleEndian(astBytes.Slice(pos + 1, 4));
                if (boneIdx >= (uint)boneCount)
                {
                    throw new InvalidDataException($"Track bone index {boneIdx} out of range (boneCount={boneCount}).");
                }

                uint c0 = BinaryPrimitives.ReadUInt32LittleEndian(astBytes.Slice(pos + 5, 4));
                uint c1 = BinaryPrimitives.ReadUInt32LittleEndian(astBytes.Slice(pos + 9, 4));
                uint c2 = BinaryPrimitives.ReadUInt32LittleEndian(astBytes.Slice(pos + 13, 4));
                uint c3 = BinaryPrimitives.ReadUInt32LittleEndian(astBytes.Slice(pos + 17, 4));
                uint c4 = BinaryPrimitives.ReadUInt32LittleEndian(astBytes.Slice(pos + 21, 4));
                uint c5 = BinaryPrimitives.ReadUInt32LittleEndian(astBytes.Slice(pos + 25, 4));
                pos += 29;

                int trackDataBytes = checked((int)((c0 * 16u) + (c1 * 20u) + (c2 * 16u) + (c3 * 16u) + (c4 * 20u) + (c5 * 16u)));
                if (pos + trackDataBytes > astBytes.Length)
                {
                    throw new InvalidDataException($"AST truncated in animation '{name}' track {t} keyframes.");
                }

                pos += trackDataBytes;
            }
        }

        if (pos != astBytes.Length)
        {
            throw new InvalidDataException($"AST trailing bytes mismatch: consumed {pos} of {astBytes.Length} bytes.");
        }

        return new AstValidationInfo(version, boneCount, animCount, animNames);
    }

    private static void WriteVec3Key(BinaryWriter bw, float frame, Vector3 value)
    {
        bw.Write(frame);
        bw.Write(value.X);
        bw.Write(value.Y);
        bw.Write(value.Z);
    }

    private static void WriteQuatKey(BinaryWriter bw, float frame, float x, float y, float z, float w)
    {
        bw.Write(frame);
        bw.Write(x);
        bw.Write(y);
        bw.Write(z);
        bw.Write(w);
    }
}
