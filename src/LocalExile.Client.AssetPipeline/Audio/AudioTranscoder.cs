using System.Buffers.Binary;
using System.Diagnostics;

namespace LocalExile.Client.AssetPipeline.Audio;

public sealed record OggVorbisInfo(int Channels, int SampleRate, int FileSizeBytes);

/// <summary>
/// Transcodes custom audio (e.g. <c>.opus</c>, <c>.mp3</c>, <c>.wav</c>) into PoE 3.29 / FMOD-compatible
/// stereo Vorbis <c>.ogg</c> via <c>ffmpeg</c> and validates the resulting Ogg Vorbis bitstream header.
/// </summary>
public static class AudioTranscoder
{
    public const int DefaultSampleRate = 48000;
    public const int DefaultChannels = 2;

    public static OggVorbisInfo TranscodeToOggVorbis(
        string inputPath,
        string outputOggPath,
        int sampleRate = DefaultSampleRate,
        int channels = DefaultChannels,
        int quality = 5)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputOggPath);

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Source audio file not found: '{inputPath}'.", inputPath);
        }

        string? outDir = Path.GetDirectoryName(outputOggPath);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add(channels.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("libvorbis");
        psi.ArgumentList.Add("-q:a");
        psi.ArgumentList.Add(quality.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(outputOggPath);

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch ffmpeg.");
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {stderr}");
        }

        byte[] oggBytes = File.ReadAllBytes(outputOggPath);
        return ValidateOggVorbis(oggBytes);
    }

    /// <summary>
    /// Validates an Ogg Vorbis bitstream: verifies <c>"OggS"</c> page capture, BOS flag,
    /// <c>\x01vorbis</c> identification header, channel count, and sample rate.
    /// </summary>
    public static OggVorbisInfo ValidateOggVorbis(ReadOnlySpan<byte> oggBytes)
    {
        if (oggBytes.Length < 58)
        {
            throw new InvalidDataException($"Ogg Vorbis file too short ({oggBytes.Length} bytes).");
        }

        if (oggBytes[0] != (byte)'O' || oggBytes[1] != (byte)'g' || oggBytes[2] != (byte)'g' || oggBytes[3] != (byte)'S')
        {
            throw new InvalidDataException("Missing 'OggS' capture pattern.");
        }

        if (oggBytes[4] != 0)
        {
            throw new InvalidDataException($"Unsupported Ogg stream structure version {oggBytes[4]}.");
        }

        byte headerType = oggBytes[5];
        if ((headerType & 0x02) == 0)
        {
            throw new InvalidDataException("First Ogg page is missing the BOS (beginning-of-stream) flag.");
        }

        int segmentCount = oggBytes[26];
        int payloadOffset = 27 + segmentCount;
        if (payloadOffset + 30 > oggBytes.Length)
        {
            throw new InvalidDataException("Ogg page header truncated before Vorbis identification header.");
        }

        ReadOnlySpan<byte> ident = oggBytes.Slice(payloadOffset, 30);
        if (ident[0] != 0x01 ||
            ident[1] != (byte)'v' || ident[2] != (byte)'o' || ident[3] != (byte)'r' ||
            ident[4] != (byte)'b' || ident[5] != (byte)'i' || ident[6] != (byte)'s')
        {
            throw new InvalidDataException("Ogg stream is not Vorbis (missing \\x01vorbis identification header).");
        }

        uint vorbisVersion = BinaryPrimitives.ReadUInt32LittleEndian(ident.Slice(7, 4));
        if (vorbisVersion != 0)
        {
            throw new InvalidDataException($"Unsupported Vorbis version {vorbisVersion}.");
        }

        int channels = ident[11];
        int sampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(ident.Slice(12, 4)));
        if (channels <= 0 || sampleRate <= 0)
        {
            throw new InvalidDataException($"Invalid Vorbis audio parameters (channels={channels}, sampleRate={sampleRate}).");
        }

        return new OggVorbisInfo(channels, sampleRate, oggBytes.Length);
    }
}
