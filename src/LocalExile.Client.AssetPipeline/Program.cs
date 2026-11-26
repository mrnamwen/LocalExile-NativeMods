using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LocalExile.Client.AssetPipeline.Audio;
using LocalExile.Client.AssetPipeline.Dat64;
using LocalExile.Client.AssetPipeline.Dds;
using LocalExile.Client.AssetPipeline.Mesh;

namespace LocalExile.Client.AssetPipeline;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            string cmd = args[0].ToLowerInvariant();
            Dictionary<string, string> opts = ParseOptions(args[1..]);

            return cmd switch
            {
                "build-maxwell" => RunBuildMaxwell(opts),
                "verify-maxwell" => RunVerifyMaxwell(opts),
                "build-siphon" => RunBuildSiphon(opts),
                "verify-siphon" => RunVerifySiphon(opts),
                "build-currency" => RunBuildCurrency(opts),
                "convert-dds" => RunConvertDds(opts),
                "convert-glb" => RunConvertGlb(opts),
                "convert-audio" => RunConvertAudio(opts),
                "patch-dat" => RunPatchDat(opts),
                "merge-dat-patches" => RunMergeDatPatches(opts),
                _ => UnknownCommand(cmd),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 2;
        }
    }

    private static int RunBuildMaxwell(Dictionary<string, string> opts)
    {
        string zipPath = RequireOption(opts, "--zip");
        string audioPath = RequireOption(opts, "--audio");
        string clientDir = RequireOption(opts, "--client");
        string outDir = RequireOption(opts, "--out");

        if (!File.Exists(zipPath)) throw new FileNotFoundException($"Maxwell zip not found: '{zipPath}'.", zipPath);
        if (!File.Exists(audioPath)) throw new FileNotFoundException($"Maxwell audio not found: '{audioPath}'.", audioPath);

        Directory.CreateDirectory(outDir);

        // 1. Read GLB + embedded textures from zip
        byte[] glbBytes;
        byte[]? zipBodyTex = null;
        byte[]? zipWhiskersTex = null;
        using (ZipArchive archive = ZipFile.OpenRead(zipPath))
        {
            ZipArchiveEntry glbEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"No .glb entry found in '{zipPath}'.");
            glbBytes = ReadEntryBytes(glbEntry);

            ZipArchiveEntry? tex0 = archive.GetEntry("textures/gltf_embedded_0.jpeg");
            ZipArchiveEntry? tex1 = archive.GetEntry("textures/gltf_embedded_1.png");
            if (tex0 is not null) zipBodyTex = ReadEntryBytes(tex0);
            if (tex1 is not null) zipWhiskersTex = ReadEntryBytes(tex1);
        }

        GlbScene scene = GlbParser.Parse(glbBytes);
        PoePetAssetBundle bundle = GlbToPoeMeshConverter.Convert(scene, new PoePetMeshOptions());

        foreach (var (virtualPath, data) in bundle.Files)
        {
            WriteVirtualFile(outDir, virtualPath, data);
        }

        // 2. Convert textures to PoE 3.29 .dds + .dds.header
        // CRITICAL: Always prefer the GLB's own embedded images for 3D mesh albedo textures!
        // Sketchfab zip archives often include separate loose textures/gltf_embedded_* files that are
        // vertically inverted relative to the GLB's embedded images & TEXCOORD_0 UVs.
        (byte[] bodyRaw, string bodyExt) = ResolveGlbMaterialImage(scene, bundle, 0, zipBodyTex, ".jpeg", "body");
        (byte[] whiskersRaw, string whiskersExt) = ResolveGlbMaterialImage(scene, bundle, 1, zipWhiskersTex, ".png", "whiskers");

        RgbaImage bodyImg = RgbaImage.DecodeBytesWithImageMagick(bodyRaw, bodyExt);
        RgbaImage whiskersImg = RgbaImage.DecodeBytesWithImageMagick(whiskersRaw, whiskersExt);
        RgbaImage flatNormalImg = RgbaImage.CreateFlatNormalGlossAo(64, 64);

        // For the 2D Cosmetics Pet Slot icon, prefer the upright portrait texture from the zip if present.
        RgbaImage iconSourceImg = zipBodyTex is not null
            ? RgbaImage.DecodeBytesWithImageMagick(zipBodyTex, ".jpeg")
            : bodyImg;
        RgbaImage iconImg = iconSourceImg.ResizeBilinear(156, 156);

        DdsOutput bodyDds = DdsTextureConverter.ConvertFromRgba(bodyImg, PoeDdsFormat.Bc1);
        DdsOutput whiskersDds = DdsTextureConverter.ConvertFromRgba(whiskersImg, PoeDdsFormat.Rgba8);
        DdsOutput normalDds = DdsTextureConverter.ConvertFromRgba(flatNormalImg, PoeDdsFormat.Rgba8);
        DdsOutput iconDds = DdsTextureConverter.ConvertFromRgba(iconImg, PoeDdsFormat.Rgba8);

        DdsTextureConverter.WritePair(ResolveVirtualPath(outDir, bundle.TextureAlbedoPaths[0]), bodyDds);
        if (bundle.TextureAlbedoPaths.Count > 1)
        {
            DdsTextureConverter.WritePair(ResolveVirtualPath(outDir, bundle.TextureAlbedoPaths[1]), whiskersDds);
        }

        DdsTextureConverter.WritePair(ResolveVirtualPath(outDir, bundle.FlatNormalTexturePath), normalDds);

        var datSpec = new PetMtxPatchSpec();
        DdsTextureConverter.WritePair(ResolveVirtualPath(outDir, datSpec.IconDdsPath), iconDds);

        // 3. Transcode looping audio (.opus -> 48kHz stereo Vorbis .ogg)
        const string audioVirtualPath = "Audio/Mods/Maxwell/stock_market.ogg";
        OggVorbisInfo oggInfo = AudioTranscoder.TranscodeToOggVorbis(audioPath, ResolveVirtualPath(outDir, audioVirtualPath));

        // 4. Extract baseline stock .datc64 tables (read-only from Client/Bundles2/ or pre-extracted dat dir) and append Maxwell rows
        (byte[] stockMtxTypes, byte[] stockMtxGs, byte[] stockPet) = LoadStockDatTables(clientDir);
        PatchedPetMtxResult datResult = MaxwellDatPatcher.AppendPetMtx(stockMtxTypes, stockMtxGs, stockPet, datSpec);

        WriteVirtualFile(outDir, "Data/MtxTypes.datc64", datResult.MtxTypesBytes);
        WriteVirtualFile(outDir, "Data/MtxTypeGameSpecific.datc64", datResult.MtxTypeGameSpecificBytes);
        WriteVirtualFile(outDir, "Data/Pet.datc64", datResult.PetBytes);

        // 5. Full round-trip verification of the entire output tree
        VerifyMaxwellDirectory(outDir, datSpec);

        Console.WriteLine($"Built & verified Maxwell 3.29.3d asset overlay at '{outDir}':");
        Console.WriteLine($"  Mesh: {bundle.VertexCount} verts, {bundle.TriangleCount} tris, shapes=[{string.Join(", ", bundle.ShapeNames)}]");
        Console.WriteLine($"  Textures: body={bodyDds.Width}x{bodyDds.Height} BC1 ({bodyDds.MipCount} mips), whiskers={whiskersDds.Width}x{whiskersDds.Height} RGBA8 ({whiskersDds.MipCount} mips), icon={iconDds.Width}x{iconDds.Height} RGBA8");
        Console.WriteLine($"  Audio: {audioVirtualPath} ({oggInfo.SampleRate}Hz, {oggInfo.Channels}ch Vorbis, {oggInfo.FileSizeBytes} bytes)");
        Console.WriteLine($"  Dat64: MtxTypes[{datResult.MtxTypesRowIndex}] HASH32=0x{datResult.MtxHash32:X8}, MtxTypeGS[{datResult.MtxTypeGameSpecificRowIndex}], Pet[{datResult.PetRowIndex}] HASH32=0x{datResult.PetHash32:X8} HASH16=0x{datResult.PetHash16:X4}");
        return 0;
    }

    private static int RunVerifyMaxwell(Dictionary<string, string> opts)
    {
        string dir = RequireOption(opts, "--dir");
        VerifyMaxwellDirectory(dir, new PetMtxPatchSpec());
        Console.WriteLine($"Verified all Maxwell 3.29.3d assets in '{dir}' cleanly.");
        return 0;
    }

    public static void VerifyMaxwellDirectory(string rootDir, PetMtxPatchSpec spec)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDir);
        ArgumentNullException.ThrowIfNull(spec);

        // 1. Verify .datc64 tables
        Dat64Table mtxTypes = Dat64Table.Load(ResolveVirtualPath(rootDir, "Data/MtxTypes.datc64"));
        Dat64Table mtxGs = Dat64Table.Load(ResolveVirtualPath(rootDir, "Data/MtxTypeGameSpecific.datc64"));
        Dat64Table pet = Dat64Table.Load(ResolveVirtualPath(rootDir, "Data/Pet.datc64"));

        int mtxRow = mtxTypes.FindRowByString(MaxwellDatPatcher.MtxTypesIdOffset, spec.MtxTypeId);
        int petRow = pet.FindRowByString(MaxwellDatPatcher.PetIdOffset, spec.PetId);
        if (mtxRow < 0 || petRow < 0)
        {
            throw new InvalidDataException($"Appended Maxwell rows missing (MtxTypes row={mtxRow}, Pet row={petRow}).");
        }

        ulong gsRow = pet.ReadForeignRow(petRow, MaxwellDatPatcher.PetMtxGsKeyOffset)
            ?? throw new InvalidDataException("Pet.MtxTypeGameSpecificKey is null.");
        MaxwellDatPatcher.VerifyAppendedPetMtx(mtxTypes, mtxGs, pet, spec, mtxRow, checked((int)gsRow), petRow);

        // 2. Verify mesh & animation binaries
        byte[] smd = File.ReadAllBytes(ResolveVirtualPath(rootDir, "Art/Models/Mods/Maxwell/maxwell.smd"));
        byte[] smdHdr = File.ReadAllBytes(ResolveVirtualPath(rootDir, "Art/Models/Mods/Maxwell/maxwell.smd.header"));
        PoeSmdWriter.Validate(smd, smdHdr);

        byte[] ast = File.ReadAllBytes(ResolveVirtualPath(rootDir, "Art/Models/Mods/Maxwell/maxwell.ast"));
        AstValidationInfo astInfo = PoeAstWriter.ValidateV7(ast);
        if (!astInfo.AnimationNames.Contains("idle") || !astInfo.AnimationNames.Contains("run") || !astInfo.AnimationNames.Contains("happy"))
        {
            throw new InvalidDataException("AST missing required pet animations (idle, run, happy).");
        }

        // 3. Verify text metadata files (.ot, .ao, .act, .sm, .amd, .mat)
        VerifyUtf16TextFile(ResolveVirtualPath(rootDir, "Metadata/Pet/Mods/Maxwell/MaxwellPet.ot"), expectBom: true, mustContain: "extends \"Metadata/Pet/Pet\"");
        VerifyUtf16TextFile(ResolveVirtualPath(rootDir, "Metadata/Pet/Mods/Maxwell/MaxwellPet.ao"), expectBom: true, mustContain: "Art/Models/Mods/Maxwell/maxwell.ast");
        VerifyUtf16TextFile(ResolveVirtualPath(rootDir, "Metadata/Pet/Mods/Maxwell/MaxwellPet.act"), expectBom: true, mustContain: "Idle = \"idle\"");
        VerifyUtf16TextFile(ResolveVirtualPath(rootDir, "Art/Models/Mods/Maxwell/maxwell.sm"), expectBom: false, mustContain: "SkinnedMeshData \"Art/Models/Mods/Maxwell/maxwell.smd\"");
        VerifyUtf16TextFile(ResolveVirtualPath(rootDir, "Art/Models/Mods/Maxwell/maxwell.amd"), expectBom: false, mustContain: "\"idle\"");

        foreach (string matFile in Directory.GetFiles(ResolveVirtualPath(rootDir, "Art/Models/Mods/Maxwell/textures"), "*.mat"))
        {
            byte[] matBytes = File.ReadAllBytes(matFile);
            if (matBytes.Length >= 2 && matBytes[0] == 0xFF && matBytes[1] == 0xFE)
            {
                throw new InvalidDataException($"Unexpected UTF-16LE BOM in '{matFile}'.");
            }

            string matText = Encoding.Unicode.GetString(matBytes);
            using JsonDocument doc = JsonDocument.Parse(matText);
            if (doc.RootElement.GetProperty("version").GetInt32() != 4)
            {
                throw new InvalidDataException($"Unexpected .mat version in '{matFile}'.");
            }

            foreach (JsonElement texEl in doc.RootElement.GetProperty("textures").EnumerateArray())
            {
                string fmt = texEl.GetProperty("format").GetString() ?? string.Empty;
                if (fmt is not ("DXT1" or "BC1" or "DXT5" or "BC7" or "Uncompressed"))
                {
                    throw new InvalidDataException($"Invalid PoE 3.29 .mat texture format '{fmt}' in '{matFile}'.");
                }

                string filename = texEl.GetProperty("filename").GetString() ?? string.Empty;
                string fullTexPath = ResolveVirtualPath(rootDir, filename);
                if (!File.Exists(fullTexPath) || !File.Exists(fullTexPath + ".header"))
                {
                    throw new FileNotFoundException($"Material '{matFile}' references missing texture '{filename}' (or its .header).", fullTexPath);
                }
            }
        }

        // 4. Verify all .dds + .dds.header pairs
        foreach (string ddsPath in Directory.GetFiles(rootDir, "*.dds", SearchOption.AllDirectories))
        {
            string hdrPath = ddsPath + ".header";
            if (!File.Exists(hdrPath))
            {
                throw new FileNotFoundException($"Missing streaming header '{hdrPath}'.", hdrPath);
            }

            DdsTextureConverter.Validate(File.ReadAllBytes(ddsPath), File.ReadAllBytes(hdrPath));
        }

        // 5. Verify looping audio .ogg
        byte[] ogg = File.ReadAllBytes(ResolveVirtualPath(rootDir, "Audio/Mods/Maxwell/stock_market.ogg"));
        OggVorbisInfo oggInfo = AudioTranscoder.ValidateOggVorbis(ogg);
        if (oggInfo.Channels != 2 || (oggInfo.SampleRate != 48000 && oggInfo.SampleRate != 44100))
        {
            throw new InvalidDataException($"Unexpected OGG format: {oggInfo.SampleRate}Hz {oggInfo.Channels}ch.");
        }
    }

    private static int RunBuildSiphon(Dictionary<string, string> opts)
    {
        string emptyIconPath = RequireOption(opts, "--empty-icon");
        string chargedIconPath = RequireOption(opts, "--charged-icon");
        string clientDir = RequireOption(opts, "--client");
        string outDir = RequireOption(opts, "--out");
        string? baseOverlayDir = opts.GetValueOrDefault("--base-overlay");
        int iconSize = opts.TryGetValue("--icon-size", out string? sizeStr)
            ? int.Parse(sizeStr, System.Globalization.CultureInfo.InvariantCulture)
            : 78;

        if (!File.Exists(emptyIconPath)) throw new FileNotFoundException($"Empty Siphon Orb icon not found: '{emptyIconPath}'.", emptyIconPath);
        if (!File.Exists(chargedIconPath)) throw new FileNotFoundException($"Charged Siphon Orb icon not found: '{chargedIconPath}'.", chargedIconPath);

        IReadOnlyList<CurrencyItemPatchSpec> specs = CurrencyDatPatcher.CreateSiphonOrbSpecs(emptyIconPath, chargedIconPath);
        PatchedCurrencyResult result = BuildCurrencyOverlay(specs, clientDir, outDir, baseOverlayDir, iconSize);
        VerifyCurrencyDirectory(outDir, specs);

        Console.WriteLine($"Built & verified Orb of Siphoning 3.29.3d asset overlay at '{outDir}':");
        foreach (PatchedCurrencyEntry e in result.Entries)
        {
            Console.WriteLine(
                $"  {e.Spec.DisplayName}: BIT[{e.BaseItemTypesRowIndex}] HASH32=0x{e.BaseItemHash32:X8}, IVI[{e.ItemVisualIdentityRowIndex}] HASH16=0x{e.VisualIdentityHash16:X4}, CUR[{e.CurrencyItemsRowIndex}] Stack={e.Spec.StackSize}, It={e.ItVirtualPath}, Icon={e.Spec.IconDdsPath} ({iconSize}x{iconSize} RGBA8)");
        }

        return 0;
    }

    private static int RunVerifySiphon(Dictionary<string, string> opts)
    {
        string dir = RequireOption(opts, "--dir");
        IReadOnlyList<CurrencyItemPatchSpec> specs = CurrencyDatPatcher.CreateSiphonOrbSpecs();
        VerifyCurrencyDirectory(dir, specs);
        Console.WriteLine($"Verified all Orb of Siphoning 3.29.3d assets in '{dir}' cleanly.");
        return 0;
    }

    private static int RunBuildCurrency(Dictionary<string, string> opts)
    {
        string specPath = RequireOption(opts, "--spec");
        string clientDir = RequireOption(opts, "--client");
        string outDir = RequireOption(opts, "--out");
        string? baseOverlayDir = opts.GetValueOrDefault("--base-overlay");
        int iconSize = opts.TryGetValue("--icon-size", out string? sizeStr)
            ? int.Parse(sizeStr, System.Globalization.CultureInfo.InvariantCulture)
            : 78;

        if (!File.Exists(specPath)) throw new FileNotFoundException($"Currency spec JSON not found: '{specPath}'.", specPath);

        string json = File.ReadAllText(specPath);
        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        IReadOnlyList<CurrencyItemPatchSpec> specs;
        using (JsonDocument doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                specs = JsonSerializer.Deserialize<List<CurrencyItemPatchSpec>>(json, jsonOpts)
                    ?? throw new InvalidDataException("Currency spec array deserialized to null.");
            }
            else
            {
                CurrencyItemPatchSpec single = JsonSerializer.Deserialize<CurrencyItemPatchSpec>(json, jsonOpts)
                    ?? throw new InvalidDataException("Currency spec object deserialized to null.");
                specs = [single];
            }
        }

        string specDir = Path.GetDirectoryName(Path.GetFullPath(specPath)) ?? Environment.CurrentDirectory;
        var resolvedSpecs = specs.Select(s =>
            string.IsNullOrWhiteSpace(s.SourceIconPath) || Path.IsPathRooted(s.SourceIconPath)
                ? s
                : s with { SourceIconPath = Path.GetFullPath(Path.Combine(specDir, s.SourceIconPath)) }).ToList();

        PatchedCurrencyResult result = BuildCurrencyOverlay(resolvedSpecs, clientDir, outDir, baseOverlayDir, iconSize);
        VerifyCurrencyDirectory(outDir, resolvedSpecs, requireIcons: resolvedSpecs.All(s => !string.IsNullOrWhiteSpace(s.SourceIconPath)));

        Console.WriteLine($"Built & verified {result.Entries.Count} custom currency item(s) into '{outDir}':");
        foreach (PatchedCurrencyEntry e in result.Entries)
        {
            Console.WriteLine($"  {e.Spec.DisplayName}: BIT[{e.BaseItemTypesRowIndex}] HASH32=0x{e.BaseItemHash32:X8}, IVI[{e.ItemVisualIdentityRowIndex}] HASH16=0x{e.VisualIdentityHash16:X4}, CUR[{e.CurrencyItemsRowIndex}]");
        }

        return 0;
    }

    public static PatchedCurrencyResult BuildCurrencyOverlay(
        IReadOnlyList<CurrencyItemPatchSpec> specs,
        string clientDir,
        string outDir,
        string? baseOverlayDir = null,
        int iconSize = 78)
    {
        ArgumentNullException.ThrowIfNull(specs);
        Directory.CreateDirectory(outDir);

        (byte[] stockBit, byte[] stockIvi, byte[] stockCur) = LoadCurrencyDatTables(clientDir, baseOverlayDir);
        PatchedCurrencyResult result = CurrencyDatPatcher.AppendCurrencies(stockBit, stockIvi, stockCur, specs);

        WriteVirtualFile(outDir, "Data/BaseItemTypes.datc64", result.BaseItemTypesBytes);
        WriteVirtualFile(outDir, "Data/ItemVisualIdentity.datc64", result.ItemVisualIdentityBytes);
        WriteVirtualFile(outDir, "Data/CurrencyItems.datc64", result.CurrencyItemsBytes);

        foreach (PatchedCurrencyEntry entry in result.Entries)
        {
            WriteVirtualFile(outDir, entry.ItVirtualPath, entry.ItFileBytes);

            if (!string.IsNullOrWhiteSpace(entry.Spec.SourceIconPath))
            {
                DdsOutput iconDds = DdsTextureConverter.ConvertCurrencyIconFile(
                    entry.Spec.SourceIconPath,
                    targetWidth: iconSize,
                    targetHeight: iconSize,
                    format: PoeDdsFormat.Rgba8,
                    removeBlackBackground: true);
                DdsTextureConverter.Validate(iconDds.DdsBytes, iconDds.HeaderBytes);
                DdsTextureConverter.WritePair(ResolveVirtualPath(outDir, entry.Spec.IconDdsPath), iconDds);
            }
        }

        return result;
    }

    public static void VerifyCurrencyDirectory(
        string rootDir,
        IReadOnlyList<CurrencyItemPatchSpec> specs,
        bool requireIcons = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDir);
        ArgumentNullException.ThrowIfNull(specs);

        Dat64Table bit = Dat64Table.Load(ResolveVirtualPath(rootDir, "Data/BaseItemTypes.datc64"));
        Dat64Table ivi = Dat64Table.Load(ResolveVirtualPath(rootDir, "Data/ItemVisualIdentity.datc64"));
        Dat64Table cur = Dat64Table.Load(ResolveVirtualPath(rootDir, "Data/CurrencyItems.datc64"));

        var entries = new List<PatchedCurrencyEntry>(specs.Count);
        foreach (CurrencyItemPatchSpec spec in specs)
        {
            int bitRow = bit.FindRowByString(CurrencyDatPatcher.BitIdOffset, spec.BaseItemTypeId);
            int iviRow = ivi.FindRowByString(CurrencyDatPatcher.IviIdOffset, spec.VisualIdentityId);
            if (bitRow < 0 || iviRow < 0)
            {
                throw new InvalidDataException(
                    $"Appended currency rows missing for '{spec.BaseItemTypeId}' (BIT row={bitRow}, IVI row={iviRow}).");
            }

            int curRow = -1;
            for (int r = 0; r < cur.RowCount; r++)
            {
                if (cur.ReadForeignRow(r, CurrencyDatPatcher.CurBaseItemTypesKeyOffset) == (ulong)bitRow)
                {
                    curRow = r;
                    break;
                }
            }

            if (curRow < 0)
            {
                throw new InvalidDataException($"Appended CurrencyItems row missing for '{spec.BaseItemTypeId}' (BIT row={bitRow}).");
            }

            ushort hash16 = ivi.ReadUInt16(iviRow, CurrencyDatPatcher.IviHash16Offset);
            if (spec.VisualIdentityHash16.HasValue && hash16 != spec.VisualIdentityHash16.Value)
            {
                throw new InvalidDataException(
                    $"ItemVisualIdentity[{iviRow}].HASH16 mismatch for '{spec.VisualIdentityId}': expected 0x{spec.VisualIdentityHash16.Value:X4}, got 0x{hash16:X4}.");
            }

            string itFullPath = ResolveVirtualPath(rootDir, spec.ItVirtualPath);
            VerifyUtf16TextFile(itFullPath, expectBom: true, mustContain: $"extends \"{spec.InheritsFrom}\"");
            if (spec.SupportsStoredMods)
            {
                VerifyUtf16TextFile(itFullPath, expectBom: true, mustContain: "LocalStats");
                VerifyUtf16TextFile(itFullPath, expectBom: true, mustContain: spec.StatDescriptionList);
            }

            if (requireIcons)
            {
                string ddsFullPath = ResolveVirtualPath(rootDir, spec.IconDdsPath);
                string hdrFullPath = ddsFullPath + ".header";
                if (!File.Exists(ddsFullPath) || !File.Exists(hdrFullPath))
                {
                    throw new FileNotFoundException($"Missing currency icon DDS or .header for '{spec.BaseItemTypeId}' at '{ddsFullPath}'.", ddsFullPath);
                }

                DdsValidationInfo info = DdsTextureConverter.Validate(File.ReadAllBytes(ddsFullPath), File.ReadAllBytes(hdrFullPath));
                if (info.DxgiFormat != 28u || info.PoeHeaderFormat != 21u)
                {
                    throw new InvalidDataException(
                        $"Currency icon '{spec.IconDdsPath}' expected RGBA8 (DXGI 28 / PoE 21), got DXGI {info.DxgiFormat} / PoE {info.PoeHeaderFormat}.");
                }
            }

            entries.Add(new PatchedCurrencyEntry(
                spec,
                bitRow,
                iviRow,
                curRow,
                spec.BaseItemHash32,
                hash16,
                spec.ItVirtualPath,
                File.ReadAllBytes(itFullPath)));
        }

        CurrencyDatPatcher.VerifyAppendedCurrencies(bit, ivi, cur, entries);
    }

    private static int RunConvertDds(Dictionary<string, string> opts)
    {
        string input = RequireOption(opts, "--in");
        string output = RequireOption(opts, "--out");
        string fmtStr = opts.GetValueOrDefault("--format", "bc1").ToLowerInvariant();
        PoeDdsFormat fmt = fmtStr switch
        {
            "bc1" or "dxt1" => PoeDdsFormat.Bc1,
            "bc3" or "dxt5" => PoeDdsFormat.Bc3,
            "rgba8" or "rgba" => PoeDdsFormat.Rgba8,
            _ => throw new ArgumentException($"Unsupported DDS format '{fmtStr}' (use bc1|bc3|rgba8)."),
        };

        DdsOutput dds = DdsTextureConverter.ConvertImageFile(input, fmt);
        DdsTextureConverter.Validate(dds.DdsBytes, dds.HeaderBytes);
        DdsTextureConverter.WritePair(output, dds);
        Console.WriteLine($"Wrote {output} + .header ({dds.Width}x{dds.Height}, {dds.MipCount} mips, {fmt}).");
        return 0;
    }

    private static int RunConvertGlb(Dictionary<string, string> opts)
    {
        string input = RequireOption(opts, "--in");
        string outDir = RequireOption(opts, "--out");
        GlbScene scene = GlbParser.ParseFile(input);
        PoePetAssetBundle bundle = GlbToPoeMeshConverter.Convert(scene);
        foreach (var (vp, data) in bundle.Files)
        {
            WriteVirtualFile(outDir, vp, data);
        }

        Console.WriteLine($"Converted '{input}' -> {bundle.Files.Count} files in '{outDir}' ({bundle.VertexCount} verts, {bundle.TriangleCount} tris).");
        return 0;
    }

    private static int RunConvertAudio(Dictionary<string, string> opts)
    {
        string input = RequireOption(opts, "--in");
        string output = RequireOption(opts, "--out");
        OggVorbisInfo info = AudioTranscoder.TranscodeToOggVorbis(input, output);
        Console.WriteLine($"Transcoded '{input}' -> '{output}' ({info.SampleRate}Hz, {info.Channels}ch Vorbis, {info.FileSizeBytes} bytes).");
        return 0;
    }

    private static int RunPatchDat(Dictionary<string, string> opts)
    {
        string clientDir = RequireOption(opts, "--client");
        string outDir = RequireOption(opts, "--out");
        (byte[] mtx, byte[] gs, byte[] pet) = LoadStockDatTables(clientDir);
        PatchedPetMtxResult res = MaxwellDatPatcher.AppendPetMtx(mtx, gs, pet);
        WriteVirtualFile(outDir, "Data/MtxTypes.datc64", res.MtxTypesBytes);
        WriteVirtualFile(outDir, "Data/MtxTypeGameSpecific.datc64", res.MtxTypeGameSpecificBytes);
        WriteVirtualFile(outDir, "Data/Pet.datc64", res.PetBytes);
        Console.WriteLine($"Patched 3 .datc64 tables into '{outDir}/Data' (Pet row {res.PetRowIndex}).");
        return 0;
    }

    private static int RunMergeDatPatches(Dictionary<string, string> opts)
    {
        string clientDir = RequireOption(opts, "--client");
        string patchesArg = RequireOption(opts, "--patches");
        string outDir = RequireOption(opts, "--out");
        string? schemaPath = opts.GetValueOrDefault("--schema");

        List<string> patchFiles = ResolvePatchFiles(patchesArg);
        if (patchFiles.Count == 0)
        {
            throw new InvalidOperationException($"No .datpatch.json files found in '{patchesArg}'.");
        }

        List<DatPatchDocument> docs = patchFiles.Select(DatPatchDocument.LoadFromFile).ToList();
        Dat64SchemaRegistry schema = string.IsNullOrWhiteSpace(schemaPath)
            ? Dat64SchemaRegistry.LoadDefault329()
            : Dat64SchemaRegistry.LoadFromFile(schemaPath);

        using var source = new DirectoryDat64Source(clientDir);
        DatPatchMergeResult result = DatPatchMerger.Merge(source, docs, schema, outDir);

        Console.WriteLine($"Merged {docs.Count} mod patch(es) into '{outDir}' (digest={result.RowMap.Digest}):");
        foreach (var (tableName, tbl) in result.RowMap.Tables)
        {
            Console.WriteLine($"  Data/{tableName}.datc64: {tbl.StockRowCount} -> {tbl.MergedRowCount} rows ({tbl.Entries.Count} mapped entries, sha256={tbl.TableSha256[..12]}...)");
        }

        foreach (GeneratedOverlayEntry ov in result.RowMap.GeneratedOverlays)
        {
            Console.WriteLine($"  Overlay {ov.VirtualPath} (sha256={ov.Sha256[..12]}...)");
        }

        return 0;
    }

    private static List<string> ResolvePatchFiles(string patchesArg)
    {
        var files = new List<string>();
        foreach (string token in patchesArg.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Directory.Exists(token))
            {
                files.AddRange(Directory.EnumerateFiles(token, "*.datpatch.json", SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            }
            else if (File.Exists(token))
            {
                files.Add(token);
            }
            else
            {
                throw new FileNotFoundException($"Patch file or directory not found: '{token}'.", token);
            }
        }

        return files;
    }

    private static (byte[] MtxTypes, byte[] MtxTypeGameSpecific, byte[] Pet) LoadStockDatTables(string clientOrDatDir)
    {
        // Support either a loose baseline folder containing Data/*.datc64 (or *.datc64 directly) OR a PoE Steam Client folder with Bundles2/_.index.bin!
        string looseMtx = Path.Combine(clientOrDatDir, "Data", "MtxTypes.datc64");
        string looseGs = Path.Combine(clientOrDatDir, "Data", "MtxTypeGameSpecific.datc64");
        string loosePet = Path.Combine(clientOrDatDir, "Data", "Pet.datc64");
        if (File.Exists(looseMtx) && File.Exists(looseGs) && File.Exists(loosePet))
        {
            return (File.ReadAllBytes(looseMtx), File.ReadAllBytes(looseGs), File.ReadAllBytes(loosePet));
        }

        string directMtx = Path.Combine(clientOrDatDir, "MtxTypes.datc64");
        string directGs = Path.Combine(clientOrDatDir, "MtxTypeGameSpecific.datc64");
        string directPet = Path.Combine(clientOrDatDir, "Pet.datc64");
        if (File.Exists(directMtx) && File.Exists(directGs) && File.Exists(directPet))
        {
            return (File.ReadAllBytes(directMtx), File.ReadAllBytes(directGs), File.ReadAllBytes(directPet));
        }

        using Bundles2Reader bundles = Bundles2Reader.Open(clientOrDatDir);
        return (
            bundles.ReadFile("Data/MtxTypes.datc64"),
            bundles.ReadFile("Data/MtxTypeGameSpecific.datc64"),
            bundles.ReadFile("Data/Pet.datc64"));
    }

    private static (byte[] BaseItemTypes, byte[] ItemVisualIdentity, byte[] CurrencyItems) LoadCurrencyDatTables(
        string clientOrDatDir,
        string? baseOverlayDir = null)
    {
        // Prefer layering onto an existing patched overlay when provided (and when all 3 currency tables are present there).
        if (!string.IsNullOrWhiteSpace(baseOverlayDir) && TryLoadLooseCurrencyDatTables(baseOverlayDir, out var layered))
        {
            return layered;
        }

        if (TryLoadLooseCurrencyDatTables(clientOrDatDir, out var loose))
        {
            return loose;
        }

        using Bundles2Reader bundles = Bundles2Reader.Open(clientOrDatDir);
        return (
            bundles.ReadFile("Data/BaseItemTypes.datc64"),
            bundles.ReadFile("Data/ItemVisualIdentity.datc64"),
            bundles.ReadFile("Data/CurrencyItems.datc64"));
    }

    private static bool TryLoadLooseCurrencyDatTables(
        string dir,
        out (byte[] BaseItemTypes, byte[] ItemVisualIdentity, byte[] CurrencyItems) tables)
    {
        string[] subdirs = [Path.Combine(dir, "Data"), dir];
        foreach (string sub in subdirs)
        {
            string bit = Path.Combine(sub, "BaseItemTypes.datc64");
            string ivi = Path.Combine(sub, "ItemVisualIdentity.datc64");
            string cur = Path.Combine(sub, "CurrencyItems.datc64");
            if (File.Exists(bit) && File.Exists(ivi) && File.Exists(cur))
            {
                tables = (File.ReadAllBytes(bit), File.ReadAllBytes(ivi), File.ReadAllBytes(cur));
                return true;
            }
        }

        tables = default;
        return false;
    }

    private static void VerifyUtf16TextFile(string path, bool expectBom, string mustContain)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bool hasBom = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE;
        if (expectBom && !hasBom)
        {
            throw new InvalidDataException($"Expected UTF-16LE BOM in '{path}'.");
        }

        if (!expectBom && hasBom)
        {
            throw new InvalidDataException($"Unexpected UTF-16LE BOM in '{path}'.");
        }

        string text = Encoding.Unicode.GetString(bytes, hasBom ? 2 : 0, bytes.Length - (hasBom ? 2 : 0));
        if (!text.Contains(mustContain, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"File '{path}' does not contain expected token '{mustContain}'.");
        }
    }

    public static (byte[] Data, string FileExtension) ResolveGlbMaterialImage(
        GlbScene scene,
        PoePetAssetBundle bundle,
        int materialSlot,
        byte[]? fallbackZipBytes,
        string fallbackExtension,
        string label)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(bundle);

        int? mappedIdx = materialSlot < bundle.TextureBaseColorImageIndices.Count
            ? bundle.TextureBaseColorImageIndices[materialSlot]
            : null;
        int imgIdx = mappedIdx ?? materialSlot;

        if ((uint)imgIdx < (uint)scene.Images.Count && scene.Images[imgIdx].Data.Length > 0)
        {
            GlbImage emb = scene.Images[imgIdx];
            string ext = emb.MimeType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" or "image/jpg" => ".jpeg",
                "image/webp" => ".webp",
                _ => fallbackExtension,
            };
            return (emb.Data, ext);
        }

        if (fallbackZipBytes is { Length: > 0 })
        {
            return (fallbackZipBytes, fallbackExtension);
        }

        throw new InvalidDataException($"Missing {label} texture (neither GLB embedded image #{imgIdx} nor zip fallback found).");
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using Stream s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string ResolveVirtualPath(string rootDir, string virtualPath) =>
        Path.Combine(rootDir, virtualPath.Replace('/', Path.DirectorySeparatorChar));

    private static void WriteVirtualFile(string rootDir, string virtualPath, byte[] data)
    {
        string full = ResolveVirtualPath(rootDir, virtualPath);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(full, data);
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                dict[args[i]] = args[i + 1];
                i++;
            }
        }

        return dict;
    }

    private static string RequireOption(Dictionary<string, string> opts, string key) =>
        opts.TryGetValue(key, out string? val) && !string.IsNullOrWhiteSpace(val)
            ? val
            : throw new ArgumentException($"Missing required option '{key}'.");

    private static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"Unknown command '{cmd}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("LocalExile.Client.AssetPipeline — Path of Exile 3.29 Asset & .datc64 Pipeline");
        Console.WriteLine("Commands:");
        Console.WriteLine("  build-maxwell  --zip <maxwell.zip> --audio <stock_market.opus> --client <PoE Client or Dat dir> --out <outDir>");
        Console.WriteLine("  verify-maxwell --dir <outDir>");
        Console.WriteLine("  build-siphon   --empty-icon <empty.png> --charged-icon <charged.png> --client <PoE Client or Dat dir> --out <outDir> [--base-overlay <dir>] [--icon-size 78]");
        Console.WriteLine("  verify-siphon  --dir <outDir>");
        Console.WriteLine("  build-currency --spec <currency-specs.json> --client <PoE Client or Dat dir> --out <outDir> [--base-overlay <dir>] [--icon-size 78]");
        Console.WriteLine("  patch-dat          --client <PoE Client or Dat dir> --out <outDir>");
        Console.WriteLine("  merge-dat-patches  --client <PoE Client or Dat dir> --patches <dir-or-files> --out <outDir> [--schema <schema.min.json>]");
        Console.WriteLine("  convert-glb    --in <model.glb> --out <outDir>");
        Console.WriteLine("  convert-dds    --in <image> --out <file.dds> [--format bc1|bc3|rgba8]");
        Console.WriteLine("  convert-audio  --in <audio> --out <file.ogg>");
    }
}
