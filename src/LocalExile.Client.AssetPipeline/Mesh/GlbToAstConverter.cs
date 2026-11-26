using System.Numerics;

namespace LocalExile.Client.AssetPipeline.Mesh;

public sealed record GlbToAstOptions
{
    public int SkinIndex { get; init; }
    public byte DefaultFps { get; init; } = 30;
    public byte DefaultPlayback { get; init; } = (byte)'l';
    public float Scale { get; init; } = 1f;
    public Vector3 GltfCenterOffset { get; init; } = Vector3.Zero;
    public string FallbackRootBoneName { get; init; } = "root";
    public string FallbackClipName { get; init; } = "idle";
}

public sealed record GlbAstConversionResult(
    AstSkeleton Skeleton,
    byte[] AstBytes,
    byte[] AmdBytes,
    IReadOnlyList<int> GltfJointToPoeBoneMap);

/// <summary>
/// Converts glTF 2.0 <c>skins</c>, node hierarchies, and <c>animations</c> into a Path of Exile 3.29.3d
/// multi-bone hierarchical <c>.ast</c> v7 skeleton + BOM-less UTF-16LE CRLF <c>.amd</c> metadata file.
/// </summary>
public static class GlbToAstConverter
{
    /// <summary>
    /// Proper rigid rotation (<c>det = +1</c>) converting row-vectors from glTF Y-up <c>(x, y, z)</c>
    /// to PoE Z-down <c>(-z, x, -y)</c>: <c>v_poe = v_gltf * GltfToPoeBasis</c>.
    /// </summary>
    public static readonly Matrix4x4 GltfToPoeBasis = new(
        0f, 1f, 0f, 0f,
        0f, 0f, -1f, 0f,
        -1f, 0f, 0f, 0f,
        0f, 0f, 0f, 1f);

    /// <summary>
    /// Inverse (transpose) of <see cref="GltfToPoeBasis"/>: <c>v_gltf = v_poe * PoeToGltfBasis</c>.
    /// </summary>
    public static readonly Matrix4x4 PoeToGltfBasis = Matrix4x4.Transpose(GltfToPoeBasis);

    /// <summary>
    /// Converts a glTF local/world transform matrix (in <see cref="Matrix4x4"/> row-vector layout) into PoE's coordinate basis
    /// via similarity conjugation: <c>M_poe = C^-1 * M_gltf * C</c>.
    /// </summary>
    public static Matrix4x4 ConvertMatrixGltfToPoe(Matrix4x4 gltfMatrix) =>
        PoeToGltfBasis * gltfMatrix * GltfToPoeBasis;

    /// <summary>
    /// Converts a glTF unit quaternion to PoE's coordinate basis under proper rotation <see cref="GltfToPoeBasis"/> (<c>det = +1</c>).
    /// </summary>
    public static Quaternion ConvertQuaternionGltfToPoe(Quaternion q)
    {
        Quaternion norm = Quaternion.Normalize(q);
        Vector3 axis = GlbToPoeMeshConverter.GltfToPoeAxes(new Vector3(norm.X, norm.Y, norm.Z));
        return Quaternion.Normalize(new Quaternion(axis.X, axis.Y, axis.Z, norm.W));
    }

    /// <summary>
    /// Converts a glTF non-uniform scale vector <c>(sx, sy, sz)</c> into PoE axes <c>(sz, sx, sy)</c>
    /// matching <c>C^-1 * diag(sx, sy, sz) * C</c>.
    /// </summary>
    public static Vector3 ConvertScaleGltfToPoe(Vector3 s) => new(s.Z, s.X, s.Y);

    /// <summary>
    /// Converts a glTF scene's skin and animation clips into a PoE 3.29.3d multi-bone <c>.ast</c> v7 + <c>.amd</c>.
    /// Falls back cleanly to a 1-bone bind-pose rig if the scene has no skins.
    /// </summary>
    public static GlbAstConversionResult Convert(GlbScene scene, GlbToAstOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        options ??= new GlbToAstOptions();

        IReadOnlyList<GlbNode> nodes = scene.Nodes ?? [];
        IReadOnlyList<GlbSkin> skins = scene.Skins ?? [];
        IReadOnlyList<GlbAnimationClip> glbAnims = scene.Animations ?? [];
        byte fps = Math.Max((byte)1, options.DefaultFps);

        if (skins.Count == 0 || (uint)options.SkinIndex >= (uint)skins.Count || skins[options.SkinIndex].JointNodeIndices.Count == 0)
        {
            AstBone rootBone = new(options.FallbackRootBoneName, ParentIndex: -1, Matrix4x4.Identity);
            AstAnimationClip idleClip = BuildBindPoseClip(options.FallbackClipName, fps, options.DefaultPlayback, [rootBone]);
            AstSkeleton fallbackSkel = new(PoeAstWriter.AstVersion, [rootBone], [idleClip]);
            byte[] fallbackAst = PoeAstWriter.WriteV7(fallbackSkel);
            PoeAstWriter.ValidateV7(fallbackAst);
            byte[] fallbackAmd = PoeAstWriter.BuildAmd(fallbackSkel.Animations);
            return new GlbAstConversionResult(fallbackSkel, fallbackAst, fallbackAmd, [0]);
        }

        GlbSkin skin = skins[options.SkinIndex];
        int jointCount = skin.JointNodeIndices.Count;
        if (jointCount > 254)
        {
            throw new InvalidDataException($"GLB skin '{skin.Name}' has {jointCount} joints (PoE .ast maximum is 254).");
        }

        // Map glTF node index -> glTF joint index
        var nodeToJoint = new Dictionary<int, int>(jointCount);
        for (int j = 0; j < jointCount; j++)
        {
            nodeToJoint[skin.JointNodeIndices[j]] = j;
        }

        // Determine each joint's nearest ancestor joint (parent in joint space, or -1 for root joint)
        int[] jointParentJoint = new int[jointCount];
        Array.Fill(jointParentJoint, -1);
        for (int j = 0; j < jointCount; j++)
        {
            int nodeIdx = skin.JointNodeIndices[j];
            if ((uint)nodeIdx >= (uint)nodes.Count)
            {
                continue;
            }

            int pNode = nodes[nodeIdx].ParentIndex;
            int guard = 0;
            while (pNode >= 0 && guard++ <= nodes.Count)
            {
                if (nodeToJoint.TryGetValue(pNode, out int parentJoint))
                {
                    jointParentJoint[j] = parentJoint;
                    break;
                }

                pNode = nodes[pNode].ParentIndex;
            }
        }

        // Topologically order joints so roots come first (guaranteeing PoE bone 0 is a root!) and parents precede children.
        int[] poeBoneToGltfJoint = TopologicalOrderJoints(jointCount, jointParentJoint);
        int[] gltfJointToPoeBone = new int[jointCount];
        for (int b = 0; b < jointCount; b++)
        {
            gltfJointToPoeBone[poeBoneToGltfJoint[b]] = b;
        }

        // Compute world bind matrices in glTF mesh space (prefer Inverse(inverseBindMatrix), fallback to node.WorldMatrix).
        Matrix4x4[] worldBindGltf = new Matrix4x4[jointCount];
        for (int j = 0; j < jointCount; j++)
        {
            Matrix4x4 ibm = j < skin.InverseBindMatrices.Count ? skin.InverseBindMatrices[j] : Matrix4x4.Identity;
            if (Matrix4x4.Invert(ibm, out Matrix4x4 invIbm))
            {
                worldBindGltf[j] = invIbm;
            }
            else if ((uint)skin.JointNodeIndices[j] < (uint)nodes.Count)
            {
                worldBindGltf[j] = nodes[skin.JointNodeIndices[j]].WorldMatrix;
            }
            else
            {
                worldBindGltf[j] = Matrix4x4.Identity;
            }
        }

        // Build PoE AstBones with centered & scaled local bind matrices conjugated into PoE Z-down basis.
        var poeBones = new AstBone[jointCount];
        var poeBindTrs = new (Vector3 Scale, Quaternion Rotation, Vector3 Translation)[jointCount];
        var usedBoneNames = new HashSet<string>(StringComparer.Ordinal);
        var nodeToPoeBone = new Dictionary<int, int>(jointCount);

        for (int b = 0; b < jointCount; b++)
        {
            int j = poeBoneToGltfJoint[b];
            int nodeIdx = skin.JointNodeIndices[j];
            int parentJoint = jointParentJoint[j];
            int poeParent = parentJoint >= 0 ? gltfJointToPoeBone[parentJoint] : -1;

            nodeToPoeBone[nodeIdx] = b;

            string rawName = (uint)nodeIdx < (uint)nodes.Count ? nodes[nodeIdx].Name : $"bone_{b}";
            string boneName = MakeUniqueAsciiName(rawName, $"bone_{b}", usedBoneNames);

            Matrix4x4 localGltf;
            if (parentJoint >= 0)
            {
                Matrix4x4 parentWorldInv = Matrix4x4.Invert(worldBindGltf[parentJoint], out Matrix4x4 inv)
                    ? inv
                    : Matrix4x4.Identity;
                localGltf = worldBindGltf[j] * parentWorldInv;
            }
            else
            {
                localGltf = worldBindGltf[j];
                localGltf.M41 -= options.GltfCenterOffset.X;
                localGltf.M42 -= options.GltfCenterOffset.Y;
                localGltf.M43 -= options.GltfCenterOffset.Z;
            }

            // Apply uniform scale to local translation so bone positions match scaled mesh vertices identically.
            localGltf.M41 *= options.Scale;
            localGltf.M42 *= options.Scale;
            localGltf.M43 *= options.Scale;

            Matrix4x4 localPoe = ConvertMatrixGltfToPoe(localGltf);
            if (!Matrix4x4.Decompose(localPoe, out Vector3 ps, out Quaternion pr, out Vector3 pt))
            {
                ps = Vector3.One;
                pr = Quaternion.Identity;
                pt = new Vector3(localPoe.M41, localPoe.M42, localPoe.M43);
            }

            poeBindTrs[b] = (ps, Quaternion.Normalize(pr), pt);
            poeBones[b] = new AstBone(boneName, poeParent, localPoe);
        }

        // Convert glTF animations into PoE v7 multi-bone clips
        var astClips = new List<AstAnimationClip>();
        var usedClipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (GlbAnimationClip glbClip in glbAnims)
        {
            string clipName = MakeUniqueAsciiName(glbClip.Name, $"clip_{astClips.Count}", usedClipNames);
            float duration = MathF.Max(glbClip.DurationSeconds, 1f / fps);
            float endFrame = duration * fps;

            // Index channels by PoE bone index
            var scaleCh = new Dictionary<int, GlbAnimationChannel>();
            var rotCh = new Dictionary<int, GlbAnimationChannel>();
            var transCh = new Dictionary<int, GlbAnimationChannel>();

            foreach (GlbAnimationChannel ch in glbClip.Channels)
            {
                if (!nodeToPoeBone.TryGetValue(ch.TargetNodeIndex, out int poeBone))
                {
                    continue;
                }

                if (string.Equals(ch.Path, "scale", StringComparison.OrdinalIgnoreCase))
                {
                    scaleCh[poeBone] = ch;
                }
                else if (string.Equals(ch.Path, "rotation", StringComparison.OrdinalIgnoreCase))
                {
                    rotCh[poeBone] = ch;
                }
                else if (string.Equals(ch.Path, "translation", StringComparison.OrdinalIgnoreCase))
                {
                    transCh[poeBone] = ch;
                }
            }

            var tracks = new List<AstBoneTrack>(jointCount);
            for (int b = 0; b < jointCount; b++)
            {
                bool isRoot = poeBones[b].ParentIndex < 0;
                var (bindS, bindR, bindT) = poeBindTrs[b];

                AstVec3Keyframe[] scaleKeys = scaleCh.TryGetValue(b, out GlbAnimationChannel? sc)
                    ? ConvertScaleChannel(sc, fps)
                    : [new(0f, bindS), new(endFrame, bindS)];

                AstQuatKeyframe[] rotKeys = rotCh.TryGetValue(b, out GlbAnimationChannel? rc)
                    ? ConvertRotationChannel(rc, fps)
                    : [new(0f, bindR), new(endFrame, bindR)];

                AstVec3Keyframe[] transKeys = transCh.TryGetValue(b, out GlbAnimationChannel? tc)
                    ? ConvertTranslationChannel(tc, fps, isRoot ? options.GltfCenterOffset : Vector3.Zero, options.Scale)
                    : [new(0f, bindT), new(endFrame, bindT)];

                tracks.Add(new AstBoneTrack(b, scaleKeys, rotKeys, transKeys));
            }

            astClips.Add(new AstAnimationClip(clipName, fps, options.DefaultPlayback, tracks, duration));
        }

        if (astClips.Count == 0)
        {
            astClips.Add(BuildBindPoseClip(options.FallbackClipName, fps, options.DefaultPlayback, poeBones));
        }

        var skeleton = new AstSkeleton(PoeAstWriter.AstVersion, poeBones, astClips);
        byte[] astBytes = PoeAstWriter.WriteV7(skeleton);
        PoeAstWriter.ValidateV7(astBytes);
        byte[] amdBytes = PoeAstWriter.BuildAmd(astClips);

        return new GlbAstConversionResult(skeleton, astBytes, amdBytes, gltfJointToPoeBone);
    }

    private static AstAnimationClip BuildBindPoseClip(
        string clipName,
        byte fps,
        byte playback,
        AstBone[] bones)
    {
        float endFrame = fps; // 1.0 second default bind-pose clip
        var tracks = new List<AstBoneTrack>(bones.Length);
        for (int b = 0; b < bones.Length; b++)
        {
            if (!Matrix4x4.Decompose(bones[b].BindPoseLocalMatrix, out Vector3 s, out Quaternion r, out Vector3 t))
            {
                s = Vector3.One;
                r = Quaternion.Identity;
                t = new Vector3(bones[b].BindPoseLocalMatrix.M41, bones[b].BindPoseLocalMatrix.M42, bones[b].BindPoseLocalMatrix.M43);
            }

            r = Quaternion.Normalize(r);
            tracks.Add(new AstBoneTrack(
                b,
                [new(0f, s), new(endFrame, s)],
                [new(0f, r), new(endFrame, r)],
                [new(0f, t), new(endFrame, t)]));
        }

        return new AstAnimationClip(clipName, fps, playback, tracks, DurationSeconds: 1f);
    }

    private static AstVec3Keyframe[] ConvertScaleChannel(GlbAnimationChannel ch, byte fps)
    {
        int count = ch.InputTimes.Count;
        bool cubic = string.Equals(ch.Interpolation, "CUBICSPLINE", StringComparison.OrdinalIgnoreCase);
        int stride = cubic ? 9 : 3;
        int offset = cubic ? 3 : 0;

        var keys = new List<AstVec3Keyframe>(Math.Max(2, count));
        for (int i = 0; i < count; i++)
        {
            int vo = (i * stride) + offset;
            if (vo + 2 >= ch.OutputValues.Count)
            {
                break;
            }

            Vector3 gltfS = new(ch.OutputValues[vo], ch.OutputValues[vo + 1], ch.OutputValues[vo + 2]);
            keys.Add(new AstVec3Keyframe(ch.InputTimes[i] * fps, ConvertScaleGltfToPoe(gltfS)));
        }

        if (keys.Count == 1)
        {
            keys.Add(new AstVec3Keyframe(keys[0].Frame + 1f, keys[0].Value));
        }

        return keys.Count > 0 ? [.. keys] : [new(0f, Vector3.One), new(fps, Vector3.One)];
    }

    private static AstQuatKeyframe[] ConvertRotationChannel(GlbAnimationChannel ch, byte fps)
    {
        int count = ch.InputTimes.Count;
        bool cubic = string.Equals(ch.Interpolation, "CUBICSPLINE", StringComparison.OrdinalIgnoreCase);
        int stride = cubic ? 12 : 4;
        int offset = cubic ? 4 : 0;

        var keys = new List<AstQuatKeyframe>(Math.Max(2, count));
        Quaternion? prev = null;
        for (int i = 0; i < count; i++)
        {
            int vo = (i * stride) + offset;
            if (vo + 3 >= ch.OutputValues.Count)
            {
                break;
            }

            Quaternion gltfQ = new(ch.OutputValues[vo], ch.OutputValues[vo + 1], ch.OutputValues[vo + 2], ch.OutputValues[vo + 3]);
            Quaternion poeQ = ConvertQuaternionGltfToPoe(gltfQ);
            // Enforce positive hemisphere continuity so PoE slerp always takes the shortest arc
            if (prev.HasValue && Quaternion.Dot(prev.Value, poeQ) < 0f)
            {
                poeQ = new Quaternion(-poeQ.X, -poeQ.Y, -poeQ.Z, -poeQ.W);
            }

            prev = poeQ;
            keys.Add(new AstQuatKeyframe(ch.InputTimes[i] * fps, poeQ));
        }

        if (keys.Count == 1)
        {
            keys.Add(new AstQuatKeyframe(keys[0].Frame + 1f, keys[0].Value));
        }

        return keys.Count > 0 ? [.. keys] : [new(0f, Quaternion.Identity), new(fps, Quaternion.Identity)];
    }

    private static AstVec3Keyframe[] ConvertTranslationChannel(
        GlbAnimationChannel ch,
        byte fps,
        Vector3 gltfCenterOffset,
        float scale)
    {
        int count = ch.InputTimes.Count;
        bool cubic = string.Equals(ch.Interpolation, "CUBICSPLINE", StringComparison.OrdinalIgnoreCase);
        int stride = cubic ? 9 : 3;
        int offset = cubic ? 3 : 0;

        var keys = new List<AstVec3Keyframe>(Math.Max(2, count));
        for (int i = 0; i < count; i++)
        {
            int vo = (i * stride) + offset;
            if (vo + 2 >= ch.OutputValues.Count)
            {
                break;
            }

            Vector3 gltfT = new(ch.OutputValues[vo], ch.OutputValues[vo + 1], ch.OutputValues[vo + 2]);
            Vector3 poeT = GlbToPoeMeshConverter.GltfToPoeAxes((gltfT - gltfCenterOffset) * scale);
            keys.Add(new AstVec3Keyframe(ch.InputTimes[i] * fps, poeT));
        }

        if (keys.Count == 1)
        {
            keys.Add(new AstVec3Keyframe(keys[0].Frame + 1f, keys[0].Value));
        }

        return keys.Count > 0 ? [.. keys] : [new(0f, Vector3.Zero), new(fps, Vector3.Zero)];
    }

    private static int[] TopologicalOrderJoints(int jointCount, int[] parentJoint)
    {
        var order = new List<int>(jointCount);
        bool[] placed = new bool[jointCount];

        // First place roots in original joint order (preserving joint 0 == bone 0 when joint 0 is already a root)
        var queue = new Queue<int>();
        for (int j = 0; j < jointCount; j++)
        {
            if (parentJoint[j] < 0)
            {
                placed[j] = true;
                order.Add(j);
                queue.Enqueue(j);
            }
        }

        // BFS so parents always precede children
        while (queue.Count > 0)
        {
            int parent = queue.Dequeue();
            for (int j = 0; j < jointCount; j++)
            {
                if (!placed[j] && parentJoint[j] == parent)
                {
                    placed[j] = true;
                    order.Add(j);
                    queue.Enqueue(j);
                }
            }
        }

        // Append any residual nodes (defensive against malformed cyclic glTF)
        for (int j = 0; j < jointCount; j++)
        {
            if (!placed[j])
            {
                order.Add(j);
            }
        }

        return [.. order];
    }

    private static string MakeUniqueAsciiName(string rawName, string fallback, HashSet<string> used)
    {
        string baseName = PoeMeshHelpers.SanitizeAsciiName(rawName, fallback);
        if (used.Add(baseName))
        {
            return baseName;
        }

        for (int suffix = 1; ; suffix++)
        {
            string candidate = $"{baseName}_{suffix}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
