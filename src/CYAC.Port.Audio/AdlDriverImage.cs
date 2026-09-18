namespace CYAC.Port.Audio;

/// <summary>
/// The bytes of <c>adldrive.drv</c> as the generator engine addresses them: index == the driver's
/// own DS offset.
/// </summary>
/// <remarks>
/// <para>
/// <b>The offset convention, proved rather than assumed.</b>  The engine loads the whole
/// <c>.DRV</c> asset at <c>buf_seg:0</c> and far-calls
/// <c>{buf_seg + ((entryOffset + 0xF) &gt;&gt; 4), 0}</c> — with <c>entryOffset = 0x0010</c> that is
/// <c>buf_seg + 1</c>, so the module's own DS/CS base sits 16 bytes into the buffer and
/// <c>guest drv_seg:X == asset byte X + 0x10</c> (the driver's own address convention;
/// <c>data/audio/drivers/adldrive.json</c> — "a near pointer inside the module is a FILE offset
/// minus 0x10").  The data tree ships the asset with that 16-byte EALIB header ALREADY stripped
/// (<c>codeBytes</c> 11,669 vs <c>declaredSize</c> 11,685), so <c>adldrive.code.bin[X]</c> is
/// exactly <c>drv:X</c> and no rebasing is needed anywhere in this assembly.
/// </para>
/// <para>
/// <see cref="Validate"/> proves it on the bytes instead of trusting the arithmetic: the 34-entry
/// descriptor table the driver's own code reaches with <c>add si,0x1568</c> must be at index
/// <see cref="DescriptorTable"/>, its first four state-block pointers must be the four the cmd-04
/// update stubs poke absolutely (<c>0x1967 / 0x1A27 / 0x1AE7 / 0x1C67</c>,
/// <c>asset:1b/adldrive.drv@0x1860/0x1873/0x1886/0x1911</c>), and the module banner
/// <c>"ADLIB - 4/5/91"</c> must sit just below the table.  Any other base moves all three.
/// </para>
/// </remarks>
public sealed class AdlDriverImage
{
    /// <summary>drv-space address of the 34-entry, 8-byte tone-descriptor table.</summary>
    /// <remarks><c>add si,0x1568</c> at <c>asset:1b/adldrive.drv@0x00C7</c> (drv 0x00B7).</remarks>
    public const int DescriptorTable = 0x1568;

    /// <summary>How many tone descriptors the table holds.</summary>
    /// <remarks>
    /// <c>cmp ax,0x22</c> at drv 0x0160 — cmd 00's closure bound (the generator space
    /// is closed BY CONSTRUCTION, ids ≥ 0x22 never reach the table).
    /// </remarks>
    public const int DescriptorCount = 0x22;

    /// <summary>Bytes per descriptor: <c>{state_block, chan_slot, pitch_ptr, vol_ptr}</c>.</summary>
    public const int DescriptorStride = 8;

    /// <summary>The four continuous state blocks the cmd-04 update stubs address absolutely.</summary>
    /// <remarks>
    /// Descriptors 0..3.  Verifying them pins the table base AND the little-endian word order.
    /// </remarks>
    private static readonly ushort[] ExpectedBlocks = [0x1967, 0x1A27, 0x1AE7, 0x1C67];

    private readonly byte[] _bytes;

    private AdlDriverImage(byte[] bytes) => _bytes = bytes;

    /// <summary>A private, mutable copy of the driver's data segment.</summary>
    /// <remarks>
    /// The generator model mutates this exactly as the guest driver mutates its own DS, so every
    /// engine gets its OWN copy and a second render from the same source bytes is identical.
    /// </remarks>
    public byte[] ToMutableCopy() => (byte[])_bytes.Clone();

    /// <summary>How many bytes the module carries.</summary>
    public int Length => _bytes.Length;

    /// <summary>Wraps driver bytes that are already in drv-space, validating the convention.</summary>
    /// <param name="bytes">The module's bytes, index 0 == <c>drv:0</c>.</param>
    /// <exception cref="InvalidDataException">The bytes are not <c>adldrive.drv</c> at drv 0.</exception>
    public static AdlDriverImage FromDriverSpace(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Validate(bytes);
        return new AdlDriverImage((byte[])bytes.Clone());
    }

    /// <summary>Reads <c>audio/drivers/adldrive.code.bin</c> from a data tree root.</summary>
    /// <param name="dataRoot">The transformed data tree's root directory.</param>
    /// <exception cref="FileNotFoundException">The module is not in the tree.</exception>
    /// <exception cref="InvalidDataException">Its bytes fail <see cref="Validate"/>.</exception>
    public static AdlDriverImage FromDataTree(string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        string path = Path.Combine(dataRoot, "audio", "drivers", "adldrive.code.bin");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"the AdLib driver module is not in the data tree: {path}. Build one with "
                    + "`cyac-transform sources data --verify`.",
                path);
        }

        return FromDriverSpace(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Proves the bytes are <c>adldrive.drv</c> mapped at drv 0 — the descriptor table where the
    /// driver's own code says it is, carrying the state blocks its own update stubs poke.
    /// </summary>
    /// <param name="bytes">Candidate driver bytes.</param>
    /// <exception cref="InvalidDataException">Any of the three checks fails.</exception>
    public static void Validate(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        int end = DescriptorTable + (DescriptorCount * DescriptorStride);
        if (bytes.Length < end)
        {
            throw new InvalidDataException(
                $"driver image is {bytes.Length} bytes — too short to hold the descriptor table at "
                    + $"drv 0x{DescriptorTable:X4}..0x{end:X4}");
        }

        for (int i = 0; i < ExpectedBlocks.Length; i++)
        {
            int at = DescriptorTable + (i * DescriptorStride);
            ushort block = (ushort)(bytes[at] | (bytes[at + 1] << 8));
            if (block != ExpectedBlocks[i])
            {
                throw new InvalidDataException(
                    $"driver image is not adldrive.drv at drv 0: descriptor {i} names state block "
                        + $"0x{block:X4}, but the cmd-04 update stub for tone {i} pokes "
                        + $"0x{ExpectedBlocks[i]:X4}. The 16-byte EALIB header is probably still on "
                        + "the front (drv:X == asset byte X+0x10).");
            }
        }

        if (IndexOf(bytes, "ADLIB"u8, 0x1540, 0x1560) < 0)
        {
            throw new InvalidDataException(
                "driver image carries no \"ADLIB\" build banner at drv 0x1540..0x1560");
        }
    }

    /// <summary>Whether <paramref name="bytes"/> pass <see cref="Validate"/>.</summary>
    /// <param name="bytes">Candidate driver bytes.</param>
    public static bool LooksLikeAdlDriver(byte[] bytes)
    {
        try
        {
            Validate(bytes);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle, int from, int to)
    {
        int last = Math.Min(to, haystack.Length - needle.Length);
        for (int i = from; i <= last; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }
}
