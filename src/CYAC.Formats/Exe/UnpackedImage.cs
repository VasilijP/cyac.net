namespace CYAC.Formats.Exe;

/// <summary>
/// One entry of a DOS relocation table: the segment:offset of a word holding a segment value the
/// loader must adjust.
/// </summary>
/// <param name="Segment">The entry's segment, relative to the load segment.</param>
/// <param name="Offset">The entry's offset inside that segment.</param>
public readonly record struct MzRelocation(ushort Segment, ushort Offset)
{
    /// <summary>The byte offset into the program image this entry addresses.</summary>
    public int ImageOffset => (Segment * 16) + Offset;
}

/// <summary>
/// The result of statically unpacking a packed DOS executable: the program image as DOS would have
/// it in memory, plus everything needed to write it back out as a loadable <c>.exe</c>.
/// </summary>
/// <remarks>
/// <para>
/// The image is the memory snapshot at <see cref="LoadSegment"/>, which means every word a
/// relocation entry points at holds an <b>absolute</b> segment for that load address.  Ask for a
/// different base with <see cref="ImageAtLoadSeg"/>, or for the linker's pre-relocation form with
/// <see cref="ToReconstructedExe"/> — the DOS loader adds the real load segment at load time.
/// </para>
/// </remarks>
public sealed class UnpackedImage
{
    private readonly byte[] _image;
    private readonly int[] _relocationOffsets;
    private readonly ushort _minAlloc;
    private readonly ushort _maxAlloc;

    internal UnpackedImage(
        byte[] image,
        int[] relocationOffsets,
        ushort loadSegment,
        ushort entryCs,
        ushort entryIp,
        ushort stackSs,
        ushort stackSp,
        ushort minAlloc,
        ushort maxAlloc,
        ExeUnpackReport report)
    {
        _image = image;
        _relocationOffsets = relocationOffsets;
        _minAlloc = minAlloc;
        _maxAlloc = maxAlloc;
        LoadSegment = loadSegment;
        EntryCs = entryCs;
        EntryIp = entryIp;
        StackSs = stackSs;
        StackSp = stackSp;
        Report = report;
        Relocations =
        [
            .. relocationOffsets.Select(o => o < 0x10000
                ? new MzRelocation(0, (ushort)o)
                : new MzRelocation((ushort)(o >> 4), (ushort)(o & 0xF))),
        ];
    }

    /// <summary>The load segment the image's relocated words are resolved for.</summary>
    public ushort LoadSegment { get; }

    /// <summary>The image's length in bytes.</summary>
    public int Length => _image.Length;

    /// <summary>The unpacked program's CS, relative to the load segment.</summary>
    public ushort EntryCs { get; }

    /// <summary>The unpacked program's IP.</summary>
    public ushort EntryIp { get; }

    /// <summary>The unpacked program's SS, relative to the load segment.</summary>
    public ushort StackSs { get; }

    /// <summary>The unpacked program's SP.</summary>
    public ushort StackSp { get; }

    /// <summary>Every relocation site the packer's chains named, in ascending image order.</summary>
    public IReadOnlyList<MzRelocation> Relocations { get; }

    /// <summary>The same sites as plain image offsets.</summary>
    public IReadOnlyList<int> RelocationImageOffsets => _relocationOffsets;

    /// <summary>What the unpack found — the knowledge half of the transform.</summary>
    public ExeUnpackReport Report { get; }

    /// <summary>
    /// The program image as it would be in memory at a given load segment.
    /// </summary>
    /// <param name="loadSegment">The base to resolve relocations for.</param>
    /// <returns>A fresh copy; the caller may modify it freely.</returns>
    public byte[] ImageAtLoadSeg(ushort loadSegment)
    {
        byte[] copy = (byte[])_image.Clone();
        int delta = loadSegment - LoadSegment;
        if (delta == 0)
        {
            return copy;
        }

        foreach (int offset in _relocationOffsets)
        {
            ushort word = (ushort)(copy[offset] | (copy[offset + 1] << 8));
            word = (ushort)(word + delta);
            copy[offset] = (byte)word;
            copy[offset + 1] = (byte)(word >> 8);
        }

        return copy;
    }

    /// <summary>
    /// Writes the image back out as a plain, loadable <c>MZ</c> executable with a real relocation
    /// table — the form DOS (or DOSBox, or Ghidra) can load at any segment.
    /// </summary>
    /// <remarks>
    /// The header is grown to hold the whole relocation table and rounded up to a 256-byte boundary;
    /// each relocated word is written in its pre-relocation form (the stored value minus
    /// <see cref="LoadSegment"/>) so the loader's own fix-up produces the right absolute segment.
    /// </remarks>
    public byte[] ToReconstructedExe()
    {
        int count = _relocationOffsets.Length;
        int needed = 0x40 + (count * 4);
        int headerBytes = (needed + 0xFF) & ~0xFF;
        int total = headerBytes + _image.Length;

        byte[] exe = new byte[total];
        MzHeader header = new MzHeader(
            (ushort)(total & 0x1FF),
            (ushort)((total + 511) >> 9),
            (ushort)count,
            (ushort)(headerBytes / 16),
            _minAlloc,
            _maxAlloc,
            StackSs,
            StackSp,
            0,
            EntryIp,
            EntryCs,
            MzHeader.DefaultRelocationTableOffset,
            0);
        header.WriteTo(exe);

        int table = MzHeader.DefaultRelocationTableOffset;
        foreach (MzRelocation relocation in Relocations)
        {
            exe[table] = (byte)relocation.Offset;
            exe[table + 1] = (byte)(relocation.Offset >> 8);
            exe[table + 2] = (byte)relocation.Segment;
            exe[table + 3] = (byte)(relocation.Segment >> 8);
            table += 4;
        }

        _image.CopyTo(exe, headerBytes);
        foreach (int offset in _relocationOffsets)
        {
            int at = headerBytes + offset;
            ushort word = (ushort)(exe[at] | (exe[at + 1] << 8));
            word = (ushort)(word - LoadSegment);
            exe[at] = (byte)word;
            exe[at + 1] = (byte)(word >> 8);
        }

        return exe;
    }
}
