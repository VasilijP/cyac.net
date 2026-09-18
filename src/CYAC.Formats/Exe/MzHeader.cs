namespace CYAC.Formats.Exe;

/// <summary>
/// The 28 fixed bytes at the head of a DOS <c>MZ</c> executable.
/// </summary>
/// <remarks>
/// <para>
/// Source of truth: <c>platform</c> — the DOS EXE header layout (identical in every DOS reference;
/// the field names are the historical <c>e_*</c> ones).  Only the fields the unpacker needs are
/// modelled; <c>e_res</c> and everything past <c>e_ovno</c> are not part of the fixed header.
/// </para>
/// <para>
/// The header of <c>sources/yeager.exe</c> is 32 bytes (<see cref="HeaderParagraphs"/> = 2) and carries
/// no relocations — the packer moved them inside the compressed payload.
/// </para>
/// </remarks>
/// <param name="BytesOnLastPage"><c>e_cblp</c> — bytes used on the last 512-byte page (0 = full).</param>
/// <param name="Pages"><c>e_cp</c> — 512-byte pages in the file, rounding up.</param>
/// <param name="RelocationCount"><c>e_crlc</c> — number of entries in the relocation table.</param>
/// <param name="HeaderParagraphs"><c>e_cparhdr</c> — header size in 16-byte paragraphs.</param>
/// <param name="MinAlloc"><c>e_minalloc</c> — paragraphs of extra memory the image needs.</param>
/// <param name="MaxAlloc"><c>e_maxalloc</c> — paragraphs of extra memory the image would like.</param>
/// <param name="Ss"><c>e_ss</c> — initial SS, relative to the load segment.</param>
/// <param name="Sp"><c>e_sp</c> — initial SP.</param>
/// <param name="Checksum"><c>e_csum</c> — word checksum; zero in practice.</param>
/// <param name="Ip"><c>e_ip</c> — initial IP.</param>
/// <param name="Cs"><c>e_cs</c> — initial CS, relative to the load segment.</param>
/// <param name="RelocationTableOffset"><c>e_lfarlc</c> — file offset of the relocation table.</param>
/// <param name="OverlayNumber"><c>e_ovno</c> — overlay number; 0 is the root program.</param>
public readonly record struct MzHeader(
    ushort BytesOnLastPage,
    ushort Pages,
    ushort RelocationCount,
    ushort HeaderParagraphs,
    ushort MinAlloc,
    ushort MaxAlloc,
    ushort Ss,
    ushort Sp,
    ushort Checksum,
    ushort Ip,
    ushort Cs,
    ushort RelocationTableOffset,
    ushort OverlayNumber)
{
    /// <summary>The <c>MZ</c> signature word, little-endian.</summary>
    public const ushort Signature = 0x5A4D;

    /// <summary>Size of the fixed part of the header, before the relocation table.</summary>
    public const int FixedSize = 0x1C;

    /// <summary>The file offset at which a reconstructed header puts its relocation table.</summary>
    public const int DefaultRelocationTableOffset = 0x1E;

    /// <summary>The header's own size in bytes (<see cref="HeaderParagraphs"/> × 16).</summary>
    public int HeaderBytes => HeaderParagraphs * 16;

    /// <summary>
    /// Reads the fixed header from the start of a DOS executable.
    /// </summary>
    /// <param name="file">The whole file, or at least its first <see cref="FixedSize"/> bytes.</param>
    /// <param name="header">The parsed header when the signature matched.</param>
    /// <returns><see langword="true"/> when <paramref name="file"/> starts with a readable MZ header.</returns>
    public static bool TryRead(ReadOnlySpan<byte> file, out MzHeader header)
    {
        header = default;
        if (file.Length < FixedSize)
        {
            return false;
        }

        if (Word(file, 0x00) != Signature)
        {
            return false;
        }

        header = new MzHeader(
            Word(file, 0x02), Word(file, 0x04), Word(file, 0x06), Word(file, 0x08),
            Word(file, 0x0A), Word(file, 0x0C), Word(file, 0x0E), Word(file, 0x10),
            Word(file, 0x12), Word(file, 0x14), Word(file, 0x16), Word(file, 0x18),
            Word(file, 0x1A));
        return header.HeaderParagraphs != 0;
    }

    /// <summary>
    /// Writes this header into the first <see cref="FixedSize"/> bytes of a buffer.
    /// </summary>
    /// <param name="destination">The buffer to write into; must be at least <see cref="FixedSize"/> long.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < FixedSize)
        {
            throw new ArgumentException($"an MZ header needs {FixedSize} bytes", nameof(destination));
        }

        Write(destination, 0x00, Signature);
        Write(destination, 0x02, BytesOnLastPage);
        Write(destination, 0x04, Pages);
        Write(destination, 0x06, RelocationCount);
        Write(destination, 0x08, HeaderParagraphs);
        Write(destination, 0x0A, MinAlloc);
        Write(destination, 0x0C, MaxAlloc);
        Write(destination, 0x0E, Ss);
        Write(destination, 0x10, Sp);
        Write(destination, 0x12, Checksum);
        Write(destination, 0x14, Ip);
        Write(destination, 0x16, Cs);
        Write(destination, 0x18, RelocationTableOffset);
        Write(destination, 0x1A, OverlayNumber);
    }

    private static ushort Word(ReadOnlySpan<byte> bytes, int offset) =>
        (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static void Write(Span<byte> bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }
}
