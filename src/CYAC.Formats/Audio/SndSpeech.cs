namespace CYAC.Formats.Audio;

/// <summary>
/// A <c>.SND</c> speech clip: an 8-byte BIG-ENDIAN header followed by the sample stream.
/// </summary>
/// <remarks>
/// <para>
/// Source of truth is the CONSUMER, not the assets: <c>BLASTER.SP</c>'s <c>play</c> export reads the
/// header word by word out of the buffer the engine hands it — <c>asset:1b/BLASTER.SP@0x01BE</c>
/// <c>lodsw/xchg → cs:[0x24]</c> (mode, byte-swapped), <c>@0x01DA</c> length HIGH word,
/// <c>@0x01E1</c> length LOW word, <c>@0x01E8</c> rate, and <c>@0x01ED</c> <c>mov cs:[0x12],si</c> =
/// "the PCM starts at arg+8" (the layout itself is decoded below). Each <c>lodsw</c> + <c>xchg al,ah</c>
/// pair is a big-endian 16-bit read, so the u32 length is assembled from two separate big-endian
/// halves — high word first.
/// </para>
/// <para>
/// <b>Mode</b> selects the device play routine: mode 0 is unsigned 8-bit PCM; mode 1 re-points the
/// module's vtable at the hardware-ADPCM descriptor (<c>@0x0209</c>). All 25 shipped clips are mode 0
/// at 7,168 Hz (measured). A mode-1 payload has never been seen and this type does not
/// pretend to decode one — it carries the samples verbatim and says what the mode was.
/// </para>
/// </remarks>
public sealed class SndSpeech
{
    /// <summary>Bytes of header before the sample stream: 8.</summary>
    public const int HeaderBytes = 8;

    /// <summary>Mode 0 — unsigned 8-bit PCM, the only mode the shipped clips use.</summary>
    public const int ModePcm8 = 0;

    /// <summary>Mode 1 — the module's hardware-ADPCM vtable (never shipped; not decoded here).</summary>
    public const int ModeAdpcm = 1;

    /// <summary>The sample rate all 25 shipped clips carry: 7,168 Hz.</summary>
    public const int ShippedSampleRate = 7168;

    private SndSpeech(int mode, long declaredLength, int sampleRate, byte[] samples, byte[] trailing)
    {
        Mode = mode;
        DeclaredLength = declaredLength;
        SampleRate = sampleRate;
        Samples = samples;
        Trailing = trailing;
    }

    /// <summary>Header <c>+0x00</c> (BE u16) — the device play mode.</summary>
    public int Mode { get; }

    /// <summary>Header <c>+0x02</c> (BE u32, high word first) — the sample count the module schedules.</summary>
    public long DeclaredLength { get; }

    /// <summary>Header <c>+0x06</c> (BE u16) — the sample rate in Hz.</summary>
    public int SampleRate { get; }

    /// <summary>The sample stream, <see cref="DeclaredLength"/> bytes starting at <c>+0x08</c>.</summary>
    public byte[] Samples { get; }

    /// <summary>
    /// Bytes past the declared length — none in any shipped clip, carried so a short or padded file
    /// still round-trips.
    /// </summary>
    public byte[] Trailing { get; }

    /// <summary>True when the payload is the plain unsigned-8-bit PCM a WAV can carry losslessly.</summary>
    public bool IsPcm8 => Mode == ModePcm8;

    /// <summary>Parses a <c>.SND</c> body (already decompressed).</summary>
    /// <param name="body">The asset bytes, starting at the header.</param>
    /// <exception cref="InvalidDataException">The body is shorter than the header or than it declares.</exception>
    public static SndSpeech Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length < HeaderBytes)
        {
            throw new InvalidDataException(
                $".SND is {body.Length} bytes, shorter than its {HeaderBytes}-byte header");
        }

        int mode = (body[0] << 8) | body[1];
        long length = ((long)((body[2] << 8) | body[3]) << 16) | (uint)((body[4] << 8) | body[5]);
        int rate = (body[6] << 8) | body[7];

        long available = body.Length - HeaderBytes;
        if (length > available)
        {
            throw new InvalidDataException(
                $".SND header declares {length} sample bytes but only {available} follow the header");
        }

        byte[] samples = body.Slice(HeaderBytes, (int)length).ToArray();
        byte[] trailing = body[(HeaderBytes + (int)length)..].ToArray();
        return new SndSpeech(mode, length, rate, samples, trailing);
    }

    /// <summary>Builds a clip from its parts, for the inverse direction.</summary>
    /// <param name="mode">The device play mode.</param>
    /// <param name="sampleRate">The sample rate in Hz.</param>
    /// <param name="samples">The sample stream.</param>
    /// <param name="trailing">Bytes past the declared length, usually empty.</param>
    /// <param name="declaredLength">
    /// The length to write into the header; defaults to <paramref name="samples"/>'s length, which is
    /// what every shipped clip carries.
    /// </param>
    public static SndSpeech FromSamples(
        int mode, int sampleRate, byte[] samples, byte[]? trailing = null, long? declaredLength = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return new SndSpeech(
            mode, declaredLength ?? samples.Length, sampleRate, samples, trailing ?? []);
    }

    /// <summary>Re-emits the asset bytes exactly.</summary>
    public byte[] ToBytes()
    {
        byte[] bytes = new byte[HeaderBytes + Samples.Length + Trailing.Length];
        bytes[0] = (byte)(Mode >> 8);
        bytes[1] = (byte)Mode;
        bytes[2] = (byte)(DeclaredLength >> 24);
        bytes[3] = (byte)(DeclaredLength >> 16);
        bytes[4] = (byte)(DeclaredLength >> 8);
        bytes[5] = (byte)DeclaredLength;
        bytes[6] = (byte)(SampleRate >> 8);
        bytes[7] = (byte)SampleRate;
        Samples.CopyTo(bytes, HeaderBytes);
        Trailing.CopyTo(bytes, HeaderBytes + Samples.Length);
        return bytes;
    }
}
