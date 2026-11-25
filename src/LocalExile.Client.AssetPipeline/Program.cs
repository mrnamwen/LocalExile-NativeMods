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
                "convert-dds" => RunConvertDds(opts),
                "convert-glb" => RunConvertGlb(opts),
                "convert-audio" => RunConvertAudio(opts),
                "patch-dat" => RunPatchDat(opts),
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
        byte[] bodyRaw = zipBodyTex ?? (scene.Images.Count > 0 ? scene.Images[0].Data : throw new InvalidDataException("Missing body texture."));
        byte[] whiskersRaw = zipWhiskersTex ?? (scene.Images.Count > 1 ? scene.Images[1].Data : throw new InvalidDataException("Missing whiskers texture."));

        RgbaImage bodyImg = RgbaImage.DecodeBytesWithImageMagick(bodyRaw, ".jpeg");
        RgbaImage whiskersImg = RgbaImage.DecodeBytesWithImageMagick(whiskersRaw, ".png");
        RgbaImage flatNormalImg = RgbaImage.CreateFlatNormalGlossAo(64, 64);
        RgbaImage iconImg = bodyImg.ResizeBilinear(156, 156);

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
            string matText = Encoding.Unicode.GetString(File.ReadAllBytes(matFile));
            using JsonDocument doc = JsonDocument.Parse(matText);
            if (doc.RootElement.GetProperty("version").GetInt32() != 4)
            {
                throw new InvalidDataException($"Unexpected .mat version in '{matFile}'.");
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

    private static void VerifyUtf16TextFile(string path, bool expectBom, string mustContain)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bool hasBom = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE;
        if (expectBom && !hasBom)
        {
            throw new InvalidDataException($"Expected UTF-16LE BOM in '{path}'.");
        }

        string text = Encoding.Unicode.GetString(bytes, hasBom ? 2 : 0, bytes.Length - (hasBom ? 2 : 0));
        if (!text.Contains(mustContain, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"File '{path}' does not contain expected token '{mustContain}'.");
        }
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
        Console.WriteLine("  patch-dat      --client <PoE Client or Dat dir> --out <outDir>");
        Console.WriteLine("  convert-glb    --in <model.glb> --out <outDir>");
        Console.WriteLine("  convert-dds    --in <image> --out <file.dds> [--format bc1|bc3|rgba8]");
        Console.WriteLine("  convert-audio  --in <audio> --out <file.ogg>");
    }
}
