namespace CYAC.Formats.Exe;

/// <summary>
/// The DOS program-image address space the two packer layers work in: a flat byte array whose
/// index 0 is <c>loadSeg:0000</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both packer stubs are *self-modifying in memory*: they copy themselves out of the way, move the
/// compressed payload upward and then expand downward over the space they came from, and both leave
/// working tables and stack frames behind.  All of that lands inside the 320 KB the project's reference
/// image captures, so a decoder that only produced the decompressed stream would not reproduce it.
/// Modelling one flat memory instead — with the moves, the tables and the stack written where the
/// stubs write them — is what makes the static unpack byte-exact.
/// </para>
/// <para>
/// Segment arithmetic is therefore kept explicit but never wraps: a segment register is stored as
/// its 16-bit value and turned into an index by <see cref="SegmentBase"/>.  The stubs slide
/// <c>ES</c>/<c>DS</c> and their offsets in opposite directions precisely so the *physical* address
/// is preserved, which in this flat model is the identity.
/// </para>
/// </remarks>
internal sealed class StubMemory
{
    private readonly byte[] _bytes;

    /// <summary>Creates the address space.</summary>
    /// <param name="size">Number of bytes to model, starting at <c>loadSeg:0000</c>.</param>
    /// <param name="loadSegment">The segment index 0 corresponds to.</param>
    internal StubMemory(int size, ushort loadSegment)
    {
        _bytes = new byte[size];
        LoadSegment = loadSegment;
    }

    /// <summary>The segment that byte 0 of <see cref="Bytes"/> lives at.</summary>
    internal ushort LoadSegment { get; }

    /// <summary>The modelled bytes.</summary>
    internal byte[] Bytes => _bytes;

    /// <summary>The index at which a segment's offset 0 lives.</summary>
    /// <param name="segment">A segment-register value.</param>
    internal int SegmentBase(ushort segment) => (segment - LoadSegment) * 16;

    /// <summary>Reads one byte.</summary>
    /// <param name="index">Index into the image.</param>
    internal byte Read8(int index) => _bytes[index];

    /// <summary>Writes one byte.</summary>
    /// <param name="index">Index into the image.</param>
    /// <param name="value">The byte.</param>
    internal void Write8(int index, byte value) => _bytes[index] = value;

    /// <summary>Reads one little-endian word.</summary>
    /// <param name="index">Index into the image.</param>
    internal ushort Read16(int index) => (ushort)(_bytes[index] | (_bytes[index + 1] << 8));

    /// <summary>Writes one little-endian word.</summary>
    /// <param name="index">Index into the image.</param>
    /// <param name="value">The word.</param>
    internal void Write16(int index, ushort value)
    {
        _bytes[index] = (byte)value;
        _bytes[index + 1] = (byte)(value >> 8);
    }

    /// <summary>Copies a block, tolerating overlap in either direction.</summary>
    /// <param name="destination">Destination index.</param>
    /// <param name="source">Source index.</param>
    /// <param name="length">Bytes to copy.</param>
    internal void Move(int destination, int source, int length) =>
        Array.Copy(_bytes, source, _bytes, destination, length);
}

/// <summary>
/// The stub's own stack, modelled because its residue survives into the original's unpacked image.
/// </summary>
/// <remarks>
/// The SLR stub runs on the stack DOS hands it from the packed <c>MZ</c> header
/// (<c>SS:SP = e_ss:e_sp</c>), and the deepest frames of its recursive canonical-Huffman table
/// builder reach ~118 bytes below the top.  Those bytes are inside the 320 KB image the project
/// treats as the reference, so the words the stub pushes are part of the artefact: reproducing them is a
/// requirement, not a nicety (see <c>YeagerExeUnpacker</c> remarks).
/// </remarks>
internal sealed class StubStack
{
    private readonly StubMemory _memory;
    private readonly int _base;

    /// <summary>Creates a stack.</summary>
    /// <param name="memory">The address space to write into.</param>
    /// <param name="stackBase">The index of <c>SS:0000</c>.</param>
    /// <param name="sp">The initial SP.</param>
    internal StubStack(StubMemory memory, int stackBase, ushort sp)
    {
        _memory = memory;
        _base = stackBase;
        Sp = sp;
    }

    /// <summary>The current stack pointer.</summary>
    internal ushort Sp { get; private set; }

    /// <summary>Pushes a word, writing it into the image where the CPU would.</summary>
    /// <param name="value">The word to push.</param>
    internal void Push(ushort value)
    {
        Sp = (ushort)(Sp - 2);
        _memory.Write16(_base + Sp, value);
    }

    /// <summary>Pops a word.</summary>
    internal ushort Pop()
    {
        ushort value = _memory.Read16(_base + Sp);
        Sp = (ushort)(Sp + 2);
        return value;
    }
}
