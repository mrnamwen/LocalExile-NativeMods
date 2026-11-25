using System.Buffers.Binary;
using System.Text;
using LocalExile.Client.Patcher;

namespace LocalExile.Client.Patcher.Tests;

/// <summary>
/// Builds realistic synthetic PE32+ (AMD64) executables matching Path of Exile 3.29's
/// structural traits (ImageBase 0x140000000, DYNAMIC_BASE, multi-DLL import directory,
/// configurable header slack, optional EOF Authenticode SecurityDir and BoundImportDir).
/// </summary>
internal static class SyntheticPeBuilder
{
    public static byte[] Build(
        string[]? importedDlls = null,
        bool includeAuthenticode = false,
        bool includeBoundImport = false,
        int headerSlackBytes = 136,
        uint fileAlignment = 0x200,
        uint sectionAlignment = 0x1000)
    {
        importedDlls ??= ["KERNEL32.dll", "VERSION.dll", "USER32.dll"];

        const int sectionCount = 3; // .text, .rdata, .data
        const int optionalHeaderSize = 0xF0;
        const int sizeOfHeaders = 0x400;

        // Place PE header so SectionTableEnd = SizeOfHeaders - headerSlackBytes.
        int secTableSize = sectionCount * Pe32PlusImage.SectionHeaderSize;
        int secTableStart = sizeOfHeaders - headerSlackBytes - secTableSize;
        int peOffset = secTableStart - 24 - optionalHeaderSize;
        if (peOffset < 0x40)
            throw new ArgumentOutOfRangeException(nameof(headerSlackBytes), "Slack too large for 0x400 headers.");

        const uint textRva = 0x1000;
        const uint textRawOff = 0x400;
        const uint textRawSize = 0x400;

        const uint rdataRva = 0x2000;
        const uint rdataRawOff = 0x800;
        const uint rdataRawSize = 0x600;

        const uint dataRva = 0x3000;
        const uint dataRawOff = 0xE00;
        const uint dataRawSize = 0x200;

        const uint sizeOfImage = 0x4000;
        uint baseFileSize = dataRawOff + dataRawSize; // 0x1000

        byte[] authBlob = [];
        if (includeAuthenticode)
        {
            authBlob = new byte[0x280];
            // WIN_CERTIFICATE header: dwLength, wRevision (0x0200), wCertificateType (0x0002 PKCS7)
            BinaryPrimitives.WriteUInt32LittleEndian(authBlob.AsSpan(0, 4), (uint)authBlob.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(authBlob.AsSpan(4, 2), 0x0200);
            BinaryPrimitives.WriteUInt16LittleEndian(authBlob.AsSpan(6, 2), 0x0002);
            for (int i = 8; i < authBlob.Length; i++)
                authBlob[i] = (byte)((i * 37 + 0xA5) & 0xFF);
        }

        byte[] img = new byte[baseFileSize + authBlob.Length];

        // DOS header
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0, 2), Pe32PlusImage.DosMagic);
        BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(0x3C, 4), peOffset);

        // PE signature
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(peOffset, 4), Pe32PlusImage.PeSignature);

        // COFF FileHeader
        int fileHdr = peOffset + 4;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(fileHdr + 0, 2), Pe32PlusImage.MachineAmd64);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(fileHdr + 2, 2), sectionCount);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(fileHdr + 16, 2), optionalHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(fileHdr + 18, 2), 0x0022); // EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE

        // OptionalHeader64
        int opt = peOffset + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(opt + 0, 2), Pe32PlusImage.Pe32PlusMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(opt + 16, 4), textRva); // AddressOfEntryPoint
        BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(opt + 24, 8), 0x140000000UL);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(opt + 32, 4), sectionAlignment);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(opt + 36, 4), fileAlignment);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(opt + 56, 4), sizeOfImage);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(opt + 60, 4), sizeOfHeaders);
        BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(opt + 70, 2), 0x8160); // DYNAMIC_BASE | HIGH_ENTROPY_VA | NX_COMPAT | TSA
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(opt + 108, 4), 16); // NumberOfRvaAndSizes

        // Write .text stub bytes
        img[textRawOff] = 0xC3; // ret

        // Build import table inside .rdata
        int impDescOffset = (int)rdataRawOff;
        uint impDescSize = (uint)((importedDlls.Length + 1) * Pe32PlusImage.ImportDescriptorSize);
        int cursor = impDescOffset + (int)impDescSize;
        cursor = (cursor + 7) & ~7;

        for (int i = 0; i < importedDlls.Length; i++)
        {
            int descOff = impDescOffset + i * Pe32PlusImage.ImportDescriptorSize;
            int iltOff = cursor; cursor += 16;
            int iatOff = cursor; cursor += 16;
            int hnOff = cursor;
            byte[] fnBytes = Encoding.ASCII.GetBytes($"DummyImport{i}");
            BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(hnOff, 2), 0);
            Buffer.BlockCopy(fnBytes, 0, img, hnOff + 2, fnBytes.Length);
            cursor += ((2 + fnBytes.Length + 1) + 1) & ~1;

            int nameOff = cursor;
            byte[] dllBytes = Encoding.ASCII.GetBytes(importedDlls[i]);
            Buffer.BlockCopy(dllBytes, 0, img, nameOff, dllBytes.Length);
            cursor += dllBytes.Length + 1;
            cursor = (cursor + 7) & ~7;

            uint iltRva = rdataRva + (uint)(iltOff - rdataRawOff);
            uint iatRva = rdataRva + (uint)(iatOff - rdataRawOff);
            uint hnRva = rdataRva + (uint)(hnOff - rdataRawOff);
            uint nameRva = rdataRva + (uint)(nameOff - rdataRawOff);

            BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(iltOff, 8), hnRva);
            BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(iatOff, 8), hnRva);

            BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(descOff + 0, 4), iltRva);
            BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(descOff + 12, 4), nameRva);
            BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(descOff + 16, 4), iatRva);
        }

        // Data directories
        int dirs = opt + 112;
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(dirs + Pe32PlusImage.DirIndexImport * 8, 4), rdataRva);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(dirs + Pe32PlusImage.DirIndexImport * 8 + 4, 4), impDescSize);

        if (includeBoundImport)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(dirs + Pe32PlusImage.DirIndexBoundImport * 8, 4), 0x200);
            BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(dirs + Pe32PlusImage.DirIndexBoundImport * 8 + 4, 4), 0x40);
        }

        if (includeAuthenticode)
        {
            Buffer.BlockCopy(authBlob, 0, img, (int)baseFileSize, authBlob.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(dirs + Pe32PlusImage.DirIndexSecurity * 8, 4), baseFileSize);
            BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(dirs + Pe32PlusImage.DirIndexSecurity * 8 + 4, 4), (uint)authBlob.Length);
        }

        // Section headers
        WriteSec(img, secTableStart + 0 * 40, ".text", 0x350, textRva, textRawSize, textRawOff, 0x60000020);
        WriteSec(img, secTableStart + 1 * 40, ".rdata", 0x520, rdataRva, rdataRawSize, rdataRawOff, 0x40000040);
        WriteSec(img, secTableStart + 2 * 40, ".data", 0x100, dataRva, dataRawSize, dataRawOff, 0xC0000040);

        // Compute authentic initial PE checksum
        uint checksum = Pe32PlusImage.ComputePeChecksum(img, opt + 64);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(opt + 64, 4), checksum);
        return img;
    }

    private static void WriteSec(byte[] img, int off, string name, uint vs, uint va, uint rs, uint ro, uint ch)
    {
        byte[] nb = Encoding.ASCII.GetBytes(name);
        Buffer.BlockCopy(nb, 0, img, off, Math.Min(8, nb.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 8, 4), vs);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 12, 4), va);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 16, 4), rs);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 20, 4), ro);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(off + 36, 4), ch);
    }
}
