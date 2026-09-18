using System.Buffers.Binary;
using System.Text;

namespace CYAC.Formats.Audio;

/// <summary>
/// A minimal, dependency-free RIFF/WAVE reader and writer for unsigned 8-bit mono PCM — the open
/// form the data tree carries a <c>.SND</c> speech clip in.
/// </summary>
/// <remarks>
/// <para>
/// Format facts are `platform` (Microsoft/IBM Multimedia Programming Interface, RIFF WAVE): a
/// <c>RIFF</c> chunk of type <c>WAVE</c> containing a 16-byte <c>fmt </c> chunk
/// (<c>wFormatTag = 1</c> = PCM, channels, sample rate, byte rate, block align, bits per sample) and
/// a <c>data</c> chunk of raw samples. 8-bit PCM samples are UNSIGNED (0x80 = silence), which is
/// exactly what the <c>.SND</c> stream holds, so the payload is copied byte for byte in both
/// directions. Every chunk body is padded to an even length; the pad byte is not part of the data.
/// </para>
/// <para>This is deliberately not a general WAV library: it reads what it writes, and refuses the rest
/// by name so a hand-edited file that would silently lose samples fails loudly instead.</para>
/// </remarks>
public static class WavPcm8
{
    /// <summary>PCM format tag: 1.</summary>
    public const ushort FormatPcm = 1;

    /// <summary>Bits per sample this codec handles: 8.</summary>
    public const int BitsPerSample = 8;

    /// <summary>Channels this codec handles: 1 (mono).</summary>
    public const int Channels = 1;

    /// <summary>Encodes unsigned 8-bit mono PCM as a RIFF/WAVE file.</summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="samples">The unsigned 8-bit samples.</param>
    public static byte[] Encode(int sampleRate, ReadOnlySpan<byte> samples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        int dataPad = samples.Length & 1;
        int riffSize = 4                      // "WAVE"
            + 8 + 16                          // "fmt " chunk
            + 8 + samples.Length + dataPad;   // "data" chunk
        byte[] bytes = new byte[8 + riffSize];
        Span<byte> span = bytes.AsSpan();

        Ascii("RIFF").CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)riffSize);
        Ascii("WAVE").CopyTo(span[8..]);

        Ascii("fmt ").CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(sampleRate * Channels * (BitsPerSample / 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], Channels * (BitsPerSample / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], BitsPerSample);

        Ascii("data").CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)samples.Length);
        samples.CopyTo(span[44..]);
        return bytes;
    }

    /// <summary>Reads back a file this codec wrote (or any equivalent PCM-8 mono WAV).</summary>
    /// <param name="wav">The file bytes.</param>
    /// <param name="sampleRate">The sample rate the file declares.</param>
    /// <returns>The unsigned 8-bit samples.</returns>
    /// <exception cref="InvalidDataException">The file is not an 8-bit mono PCM WAVE.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> wav, out int sampleRate)
    {
        if (wav.Length < 12 || !Matches(wav, "RIFF") || !Matches(wav[8..], "WAVE"))
        {
            throw new InvalidDataException("not a RIFF/WAVE file");
        }

        int position = 12;
        bool haveFormat = false;
        sampleRate = 0;
        byte[]? data = null;

        while (position + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav.Slice(position, 4));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(wav[(position + 4)..]);
            int body = position + 8;
            if (body + size > wav.Length)
            {
                throw new InvalidDataException(
                    $"WAVE chunk \"{id}\" declares {size} bytes but only {wav.Length - body} remain");
            }

            if (id == "fmt ")
            {
                if (size < 16)
                {
                    throw new InvalidDataException($"WAVE \"fmt \" chunk is {size} bytes, expected at least 16");
                }

                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(wav[body..]);
                ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(wav[(body + 2)..]);
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav[(body + 4)..]);
                ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(wav[(body + 14)..]);
                if (tag != FormatPcm || channels != Channels || bits != BitsPerSample)
                {
                    throw new InvalidDataException(
                        $"WAVE is format {tag}, {channels} channel(s), {bits} bit(s); this codec reads " +
                        $"{FormatPcm}/{Channels}/{BitsPerSample} (unsigned 8-bit mono PCM) only");
                }

                haveFormat = true;
            }
            else if (id == "data")
            {
                data = wav.Slice(body, (int)size).ToArray();
            }

            position = body + (int)size + ((int)size & 1);
        }

        if (!haveFormat)
        {
            throw new InvalidDataException("WAVE has no \"fmt \" chunk");
        }

        return data ?? throw new InvalidDataException("WAVE has no \"data\" chunk");
    }

    private static ReadOnlySpan<byte> Ascii(string fourCc) => Encoding.ASCII.GetBytes(fourCc);

    private static bool Matches(ReadOnlySpan<byte> bytes, string fourCc) =>
        bytes.Length >= 4 && Encoding.ASCII.GetString(bytes[..4]) == fourCc;
}
