namespace CYAC.Formats.Exe;

/// <summary>
/// What the 330-byte OPTLINK <c>/EXEPACK</c>-shape stub says about itself.
/// </summary>
/// <remarks>
/// Layout source:, re-derived from the stub bytes for this decoder.  Offsets are relative to <c>CS:0000</c>
/// of the stub — its entry point is <c>CS:0002</c>, so the trailer offsets quoted in the round-2b table are
/// two higher than the ones a disassembly listing started at the entry shows.
/// </remarks>
/// <param name="ImageOffset">The stub's offset in the layer-3 image.</param>
/// <param name="Length">The stub's length in bytes (what its self-relocator copies).</param>
/// <param name="UnpackedSp">Trailer <c>+0x13C</c>: the unpacked program's SP.</param>
/// <param name="UnpackedSs">Trailer <c>+0x13E</c>: SS, relative to the load segment.</param>
/// <param name="UnpackedIp">Trailer <c>+0x140</c>: the unpacked program's IP.</param>
/// <param name="UnpackedCs">Trailer <c>+0x142</c>: CS, relative to the load segment.</param>
/// <param name="PayloadParagraphs">Trailer <c>+0x144</c>: paragraphs of packed payload to slide up.</param>
/// <param name="FirstOpcode">Trailer <c>+0x146</c>: the first expander opcode, held in the trailer
/// rather than in the stream so the walker can start mid-loop.</param>
public sealed record ExepackStubInfo(
    int ImageOffset,
    int Length,
    ushort UnpackedSp,
    ushort UnpackedSs,
    ushort UnpackedIp,
    ushort UnpackedCs,
    ushort PayloadParagraphs,
    ushort FirstOpcode);

/// <summary>
/// Static decoder for layer 2 of <c>yeager.exe</c>: the SLR OPTLINK <c>/EXEPACK</c>-compatible
/// packer that RLE-compresses the initialised data and packs the relocation table into chains.
/// </summary>
/// <remarks>
/// <para>
/// The stub is EXEPACK-*shaped* but carries none of MS-LINK's diagnostic strings.  Its expander reads a stream of
/// 16-bit opcodes backwards out of a payload it first slides up out of the way, and writes forward from image offset
/// 0:
/// </para>
/// <list type="table">
/// <listheader><term>opcode</term><description>meaning</description></listheader>
/// <item><term><c>0x0000..0x7FFF</c></term><description>copy <c>n + 1</c> literal bytes.</description></item>
/// <item><term><c>0x8000..0x9FFF</c></term><description>repeat the next byte <c>n − 0x7FFF</c> times.</description></item>
/// <item><term><c>0xA000..0xDFFF</c></term><description>repeat the next word <c>n − 0x9FFF</c> times.</description></item>
/// <item><term><c>0xE000..0xFFFE</c></term><description>repeat the next 4 bytes <c>n + 1 − 0xE000</c> times.</description></item>
/// <item><term><c>0xFFFF</c></term><description>a relocation block; a block whose chain count is zero ends the stream.</description></item>
/// </list>
/// <para>
/// A relocation block names one segment and then <c>count</c> chains.  Each chain is an
/// <c>(offset, value)</c> pair: the word already stored at <c>offset</c> is the *next* offset in the
/// chain (<c>0xFFFF</c> ends it) and is overwritten with <c>value + loadSegment</c>.  That is the
/// packed form of the linker's relocation table, and walking it is what recovers the 3,568
/// relocation sites a reconstructed <c>.exe</c> needs.
/// </para>
/// </remarks>
public static class ExepackUnpacker
{
    /// <summary>The stub's length in bytes.</summary>
    public const int StubLength = 330;

    /// <summary>Offset of the trailer's first word (the unpacked SP) inside the stub.</summary>
    public const int TrailerOffset = 0x13C;

    private static readonly byte[] EntrySignature = [0x87, 0xC0, 0xFC, 0x8C, 0xDA, 0x83, 0xC2, 0x10];
    private static readonly byte[] ExitSignature = [0x2E, 0xFF, 0x2E, 0x40, 0x01];
    private static readonly byte[] PatcherSignature = [0x26, 0x87, 0x5D, 0xFF];

    /// <summary>
    /// Reads the stub's self-description out of the layer-3 image, or explains why there is none.
    /// </summary>
    /// <param name="memory">The layer-3 image.</param>
    /// <param name="entryCs">The layer-3 stub's recorded CS for its successor (absolute).</param>
    /// <param name="entryIp">The layer-3 stub's recorded IP for its successor.</param>
    /// <exception cref="InvalidDataException">The bytes there are not an EXEPACK-shape stub.</exception>
    internal static ExepackStubInfo ReadStub(StubMemory memory, ushort entryCs, ushort entryIp)
    {
        int stub = memory.SegmentBase(entryCs);
        if (entryIp != 2)
        {
            throw new InvalidDataException(
                $"layer 2: entry IP is 0x{entryIp:X4}, an EXEPACK-shape stub starts at CS:0002");
        }

        if (stub < 0 || stub + StubLength > memory.Bytes.Length)
        {
            throw new InvalidDataException(
                $"layer 2: entry segment 0x{entryCs:X4} is outside the unpacked image");
        }

        Span<byte> span = memory.Bytes.AsSpan(stub, StubLength);
        Require(span, entryIp, EntrySignature, "self-relocator preamble");
        Require(span, 0x7C, ExitSignature, "JMP FAR CS:[0x140] exit");
        Require(span, 0xAA, PatcherSignature, "XCHG ES:[DI-1],BX relocation patcher");

        ushort W(int offset) => memory.Read16(stub + offset);
        return new ExepackStubInfo(
            stub,
            StubLength,
            W(TrailerOffset),
            W(TrailerOffset + 2),
            W(TrailerOffset + 4),
            W(TrailerOffset + 6),
            W(TrailerOffset + 8),
            W(TrailerOffset + 10));
    }

    private static void Require(ReadOnlySpan<byte> stub, int offset, byte[] expected, string what)
    {
        if (!stub.Slice(offset, expected.Length).SequenceEqual(expected))
        {
            throw new InvalidDataException(
                $"layer 2: stub+0x{offset:X} is not the {what} " +
                $"(found {Convert.ToHexString(stub.Slice(offset, expected.Length))}, " +
                $"expected {Convert.ToHexString(expected)})");
        }
    }

    /// <summary>
    /// Runs layer 2 in place: slides the packed payload up, expands it over image offset 0, and
    /// applies every relocation chain.
    /// </summary>
    /// <param name="memory">The layer-3 image.</param>
    /// <param name="entryCs">The layer-3 stub's recorded CS for its successor (absolute).</param>
    /// <param name="entryIp">The layer-3 stub's recorded IP for its successor.</param>
    /// <param name="entrySs">The layer-3 stub's recorded SS for its successor (absolute).</param>
    /// <param name="entrySp">The layer-3 stub's recorded SP for its successor.</param>
    /// <param name="relocationSites">Receives the image offset of every patched word, in walk order.</param>
    /// <returns>The stub's trailer (read from the copy it made of itself) and how far it wrote.</returns>
    internal static (ExepackStubInfo Trailer, int OutputBytes) Expand(
        StubMemory memory,
        ushort entryCs,
        ushort entryIp,
        ushort entrySs,
        ushort entrySp,
        List<int> relocationSites)
    {
        Machine machine = new Machine(memory, entryCs, entryIp, entrySs, entrySp, relocationSites);
        ExepackStubInfo trailer = machine.Run();
        return (trailer, machine.OutputBytes);
    }

    /// <summary>The expander's working state, in the stub's own register names.</summary>
    private sealed class Machine
    {
        private readonly StubMemory _m;
        private readonly StubStack _stack;
        private readonly List<int> _relocations;
        private readonly ushort _loadSegment;
        private readonly ushort _entryCs;
        private readonly ushort _entrySs;

        private ushort _ax, _bx, _cx, _dx, _si, _di;
        private ushort _ds, _es;

        /// <summary>How far the expander wrote, as an image offset.</summary>
        internal int OutputBytes { get; private set; }

        internal Machine(
            StubMemory memory, ushort entryCs, ushort entryIp, ushort entrySs, ushort entrySp,
            List<int> relocationSites)
        {
            _m = memory;
            _loadSegment = memory.LoadSegment;
            _entryCs = entryCs;
            _ = entryIp;
            _entrySs = entrySs;
            _relocations = relocationSites;
            _stack = new StubStack(memory, memory.SegmentBase(entrySs), entrySp);
        }

        private byte Al => (byte)_ax;

        private byte Ah => (byte)(_ax >> 8);

        private void SetAl(int v) => _ax = (ushort)((_ax & 0xFF00) | (v & 0xFF));

        private void SetAh(int v) => _ax = (ushort)((_ax & 0x00FF) | ((v & 0xFF) << 8));

        private int DsBase => _m.SegmentBase(_ds);

        private int EsBase => _m.SegmentBase(_es);

        internal ExepackStubInfo Run()
        {
            // stub+0x02..0x27 — DX = the image base segment; the trailer's SS and CS become
            // absolute; the stub copies itself to SS:0000 and continues there.
            _ds = (ushort)(_loadSegment - 0x10);
            _es = _ds;
            _dx = (ushort)(_ds + 0x10);
            _es = _entrySs;
            _ds = _entryCs;
            int stub = _m.SegmentBase(_entryCs);
            int work = _m.SegmentBase(_entrySs);
            _m.Write16(stub + TrailerOffset + 2, (ushort)(_m.Read16(stub + TrailerOffset + 2) + _dx));
            _m.Write16(stub + TrailerOffset + 6, (ushort)(_m.Read16(stub + TrailerOffset + 6) + _dx));
            _m.Move(work, stub, StubLength);
            _stack.Push(_es);
            _stack.Push(0x0028);
            _stack.Pop();
            _stack.Pop();

            // stub+0x24 sets BP = 1 as an odd/even mask for the unaligned store paths; this
            // decoder writes byte-wise, so the alignment split has no observable effect.

            ExepackStubInfo trailer = ReadStubTrailer(work);

            // stub+0x28..0x55 — slide the packed payload up out of the expansion's way.
            _bx = trailer.PayloadParagraphs;
            while (true)
            {
                _cx = 0x1000;
                if (_bx <= _cx)
                {
                    _cx = _bx;
                }

                _bx -= _cx;
                _ds = (ushort)(_ds - _cx);
                _es = (ushort)(_es - _cx);
                _m.Move(_m.SegmentBase(_es), _m.SegmentBase(_ds), _cx * 16);
                _di = 0xFFFE;
                if (_bx == 0)
                {
                    break;
                }
            }

            // stub+0x57..0x65 — read forward from the moved payload, write forward from offset 0.
            _ds = _es;
            _si = (ushort)(_di + 2);
            _es = _dx;
            _di = 0;
            _ax = trailer.FirstOpcode;

            Expand();
            OutputBytes = EsBase + _di;
            return trailer;
        }

        private ExepackStubInfo ReadStubTrailer(int work)
        {
            ushort W(int offset) => _m.Read16(work + offset);
            return new ExepackStubInfo(
                work, StubLength,
                W(TrailerOffset), W(TrailerOffset + 2), W(TrailerOffset + 4),
                W(TrailerOffset + 6), W(TrailerOffset + 8), W(TrailerOffset + 10));
        }

        /// <summary>stub+0x81..0x139 — the opcode loop.</summary>
        private void Expand()
        {
            bool first = true;
            while (true)
            {
                if (first)
                {
                    // The trailer supplied the first opcode, so the loop is entered mid-way.
                    first = false;
                }
                else
                {
                    NormaliseSource();
                    if (_di >= 0x8000)
                    {
                        _di = (ushort)(_di - 0x8000);
                        _es = (ushort)(_es + 0x800);
                    }

                    _ax = ReadWord();
                }

                NormaliseSource();

                if (_ax < 0x8000)
                {
                    // stub+0x10C — a literal run of AX + 1 bytes.
                    (_cx, _ax) = (_ax, _cx);
                    _cx = (ushort)(_cx + 1);
                    int source = DsBase;
                    int destination = EsBase;
                    for (int i = _cx; i > 0; i--)
                    {
                        _m.Write8(destination + _di, _m.Read8(source + _si));
                        _si = (ushort)(_si + 1);
                        _di = (ushort)(_di + 1);
                    }

                    _cx = 0;
                }
                else if (_ax < 0xA000)
                {
                    // stub+0x121 — one byte repeated.
                    _ax = (ushort)(_ax - 0x7FFF);
                    (_cx, _ax) = (_ax, _cx);
                    SetAl(ReadByte());
                    SetAh(Al);
                    int destination = EsBase;
                    for (int i = _cx; i > 0; i--)
                    {
                        _m.Write8(destination + _di, Al);
                        _di = (ushort)(_di + 1);
                    }

                    _cx = 0;
                }
                else if (_ax < 0xE000)
                {
                    // stub+0x81 — one word repeated.  The stub has a separate unaligned path that
                    // writes the same little-endian byte pattern, so one loop covers both.
                    _ax = (ushort)(_ax - 0x9FFF);
                    (_cx, _ax) = (_ax, _cx);
                    _ax = ReadWord();
                    int destination = EsBase;
                    byte low = Al;
                    byte high = Ah;
                    for (int i = _cx; i > 0; i--)
                    {
                        _m.Write8(destination + _di, low);
                        _di = (ushort)(_di + 1);
                        _m.Write8(destination + _di, high);
                        _di = (ushort)(_di + 1);
                    }

                    _cx = 0;
                }
                else
                {
                    _ax = (ushort)(_ax + 1);
                    if (_ax == 0)
                    {
                        if (!ApplyRelocationBlock())
                        {
                            return;
                        }
                    }
                    else
                    {
                        // stub+0xD5 — four source bytes repeated: the two MOVSWs are followed by a
                        // SUB SI,4, so the same quad is re-read every iteration.
                        _ax = (ushort)(_ax - 0xE000);
                        (_cx, _ax) = (_ax, _cx);
                        int source = DsBase;
                        int destination = EsBase;
                        while (true)
                        {
                            for (int k = 0; k < 4; k++)
                            {
                                _m.Write8(destination + _di, _m.Read8(source + _si));
                                _si = (ushort)(_si + 1);
                                _di = (ushort)(_di + 1);
                            }

                            _si = (ushort)(_si - 4);
                            _cx = (ushort)(_cx - 1);
                            if (_cx == 0)
                            {
                                break;
                            }
                        }

                        _si = (ushort)(_si + 4);
                    }
                }
            }
        }

        /// <summary>stub+0x97..0xB6 — one relocation block; false when it is the terminator.</summary>
        private bool ApplyRelocationBlock()
        {
            _ax = ReadWord();
            (_cx, _ax) = (_ax, _cx);
            if (_cx == 0)
            {
                return false;
            }

            _stack.Push(_es);
            _stack.Push(_di);
            _ax = ReadWord();
            _ax = (ushort)(_ax + _dx);
            _es = _ax;
            while (true)
            {
                _ax = ReadWord();
                (_di, _ax) = (_ax, _di);
                _ax = ReadWord();
                _ax = (ushort)(_ax + _dx);
                _di = (ushort)(_di + 1);
                while (true)
                {
                    _bx = _ax;
                    int site = EsBase + (ushort)(_di - 1);
                    ushort next = _m.Read16(site);
                    _m.Write16(site, _bx);
                    _bx = next;
                    _relocations.Add(site);
                    _di = _bx;
                    _di = (ushort)(_di + 1);
                    if (_di == 0)
                    {
                        break;
                    }
                }

                _cx = (ushort)(_cx - 1);
                if (_cx == 0)
                {
                    break;
                }
            }

            _di = _stack.Pop();
            _es = _stack.Pop();
            return true;
        }

        /// <summary>stub+0xB7 / 0xC0 / 0xFF — re-base the source cursor inside its segment.</summary>
        private void NormaliseSource()
        {
            if (_si >= 0x8000)
            {
                _si = (ushort)(_si - 0x8000);
                _ds = (ushort)(_ds + 0x800);
            }
        }

        private ushort ReadWord()
        {
            ushort value = _m.Read16(DsBase + _si);
            _si = (ushort)(_si + 2);
            return value;
        }

        private byte ReadByte()
        {
            byte value = _m.Read8(DsBase + _si);
            _si = (ushort)(_si + 1);
            return value;
        }
    }
}
