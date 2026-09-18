namespace CYAC.Formats.Exe;

/// <summary>
/// What the 822-byte SLR loader stub at the tail of a packed executable says about itself.
/// </summary>
/// <remarks>
/// Layout source:–§3, re-derived from the stub bytes for this decoder.  All offsets are relative to the stub's
/// own base, which is <c>e_cs:0000</c> of the packed <c>MZ</c> — note that the entry point is <c>e_cs:e_ip</c>
/// with <c>e_ip = 0</c>, so the stub base and the entry coincide for this layer.
/// </remarks>
/// <param name="ImageOffset">The stub's offset in the loaded image (i.e. <c>e_cs × 16</c>).</param>
/// <param name="FileOffset">The stub's offset in the packed file.</param>
/// <param name="Length">The stub's length in bytes, header and trailer included.</param>
/// <param name="Copyright">The trailer string at <c>stub+0x308</c> — the packer's fingerprint.</param>
/// <param name="CodeLengthCounts">
/// The 11 canonical-Huffman code-length counts at <c>stub+0x329</c>: entry <c>i</c> is how many
/// symbols have code length <c>i+1</c>.  They seed the distance tree when the stream asks for the
/// built-in table instead of shipping its own.
/// </param>
/// <param name="PayloadParagraphs">
/// <c>stub+0x334</c> — a copy of the packed <c>e_cs</c>, used as the paragraph count of compressed
/// payload sitting below the stub.
/// </param>
/// <param name="UnpackedSp">Inline-header <c>stub+0x07</c>: the SP the next layer starts on.</param>
/// <param name="UnpackedSs">Inline-header <c>stub+0x09</c>: SS, relative to the load segment.</param>
/// <param name="UnpackedIp">Inline-header <c>stub+0x0B</c>: the next layer's IP.</param>
/// <param name="UnpackedCs">Inline-header <c>stub+0x0D</c>: CS, relative to the load segment.</param>
/// <param name="LiteralTreeAlphabet">Seed at <c>stub+0x8D</c>: the literal/length tree's symbol count.</param>
/// <param name="DistanceTreeAlphabet">Seed at <c>stub+0x91</c>: the distance tree's symbol count.</param>
public sealed record SlrLzhStubInfo(
    int ImageOffset,
    int FileOffset,
    int Length,
    string Copyright,
    IReadOnlyList<byte> CodeLengthCounts,
    ushort PayloadParagraphs,
    ushort UnpackedSp,
    ushort UnpackedSs,
    ushort UnpackedIp,
    ushort UnpackedCs,
    ushort LiteralTreeAlphabet,
    ushort DistanceTreeAlphabet);

/// <summary>
/// Static decoder for layer 3 of <c>yeager.exe</c>: the SLR Systems linker-internal LZH
/// (LZSS + two canonical-Huffman trees) whole-file compressor.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this replaces.</b> The project's original unpacker executes the packer stub on a
/// micro-emulator (<c>CYAC.Tools.SlrUnpacker</c> +).  A <c>CYAC.Port.*</c> consumer may not
/// reference the emulator, and "run these opaque bytes and see what happens" is exactly what the
/// transform principle forbids — so this is the algorithm itself, transcribed from the stub's
/// disassembly.  There is no instruction dispatch here: every step below is a named part of the
/// format.
/// </para>
/// <para>
/// <b>The format</b> (stub offsets, semantics re-derived):
/// </para>
/// <list type="bullet">
/// <item><description>
/// The stub copies itself to <c>e_ss:0000</c> and moves the whole compressed payload up so the
/// expansion can run downward over the space it came from (<c>stub+0x31</c>).
/// </description></item>
/// <item><description>
/// Two canonical-Huffman trees live in the stub's working segment: a 388-symbol literal/length tree
/// at <c>+0x340</c> and a 128-symbol distance tree at <c>+0x13E8</c>.  Each has a 256-entry
/// direct-lookup table over the next 8 bits (<c>+0x188</c> lengths, <c>+0x288</c> symbols) with two
/// node arrays (<c>+0x488</c> for a 1 bit, <c>+0xA98</c> for a 0 bit) for codes longer than 8.
/// </description></item>
/// <item><description>
/// The bit stream is MSB-first; the reader keeps a 16-bit window whose high byte is the lookahead
/// (<c>stub+0x1C2</c>).  Raw bytes (back-reference low bytes) are pulled from the same cursor.
/// </description></item>
/// <item><description>
/// Symbols 0–255 are literals; 256 is a 2-byte back-reference whose distance is one raw byte;
/// 257–383 are back-references of length <c>sym − 254</c> whose distance is
/// <c>distanceSymbol × 256 + rawByte</c>; 384 re-normalises the segment cursors; 385 rebuilds the
/// trees; 386 ends the stream.
/// </description></item>
/// <item><description>
/// A tree rebuild reads a nibble-packed run-length list of code lengths from the stream
/// (<c>stub+0x2E0</c>), or — for the distance tree only — the 11 counts baked into the stub trailer
/// (<c>stub+0x2B9</c>).  Table construction is a depth-first walk that assigns canonical codes in
/// symbol order (<c>stub+0x1FE</c>).
/// </description></item>
/// </list>
/// </remarks>
public static class SlrLzhDecoder
{
    /// <summary>Offset of the packer's copyright string inside the stub.</summary>
    public const int CopyrightOffset = 0x308;

    /// <summary>The copyright string a recognised SLR LZH stub carries.</summary>
    public const string CopyrightText = "Copyright (C) SLR Systems 1990-91";

    /// <summary>Offset of the 11-byte canonical code-length count table inside the stub.</summary>
    public const int CodeLengthCountOffset = 0x329;

    /// <summary>Number of code-length counts in the trailer table (maximum code length).</summary>
    public const int CodeLengthCountSize = 11;

    /// <summary>Offset of the payload paragraph count inside the stub.</summary>
    public const int PayloadParagraphOffset = 0x334;

    /// <summary>
    /// How many bytes the stub's self-relocator copies (412 words).  The last two are past the end
    /// of the shipping file — DOS zero-fills them, and nothing reads them, but the copy's length is
    /// what the stub's own <c>MOV CX,0x19C</c> says.
    /// </summary>
    public const int StubLength = 824;

    /// <summary>How many of the stub's bytes the file actually carries (through the trailer).</summary>
    public const int StubFileLength = 822;

    /// <summary>Offset of the literal/length tree's control block in the working segment.</summary>
    internal const int LiteralTreeBase = 0x340;

    /// <summary>Offset of the distance tree's control block in the working segment.</summary>
    internal const int DistanceTreeBase = 0x13E8;

    private static readonly byte[] EntrySignature = [0x87, 0xC0, 0xEB, 0x0B];

    /// <summary>
    /// Reads the stub's self-description out of a packed file, or explains why it is not one.
    /// </summary>
    /// <param name="file">The packed executable.</param>
    /// <param name="header">Its parsed <c>MZ</c> header.</param>
    /// <exception cref="InvalidDataException">The file does not carry an SLR LZH stub.</exception>
    public static SlrLzhStubInfo ReadStub(ReadOnlySpan<byte> file, MzHeader header)
    {
        int imageOffset = header.Cs * 16;
        int fileOffset = header.HeaderBytes + imageOffset;
        if (header.Ip != 0)
        {
            throw new InvalidDataException(
                $"not an SLR LZH executable: entry IP is 0x{header.Ip:X4}, the stub starts at e_cs:0000");
        }

        if (fileOffset < 0 || fileOffset + StubFileLength > file.Length)
        {
            throw new InvalidDataException(
                $"not an SLR LZH executable: e_cs 0x{header.Cs:X4} puts the {StubFileLength}-byte " +
                $"stub at file offset 0x{fileOffset:X} but the file is only 0x{file.Length:X} bytes");
        }

        ReadOnlySpan<byte> stub = file.Slice(fileOffset, StubFileLength);
        if (!stub[..EntrySignature.Length].SequenceEqual(EntrySignature))
        {
            throw new InvalidDataException(
                "not an SLR LZH executable: the entry point does not begin with the stub's " +
                $"XCHG AX,AX / JMP +0x0B preamble (found {Convert.ToHexString(stub[..4])})");
        }

        string copyright = System.Text.Encoding.ASCII.GetString(
            stub.Slice(CopyrightOffset, CopyrightText.Length));
        if (copyright != CopyrightText)
        {
            throw new InvalidDataException(
                $"not an SLR LZH executable: stub+0x{CopyrightOffset:X} is \"{copyright}\", " +
                $"expected \"{CopyrightText}\"");
        }

        ushort paragraphs = Word(stub, PayloadParagraphOffset);
        if (paragraphs != header.Cs)
        {
            throw new InvalidDataException(
                $"not an SLR LZH executable: stub+0x{PayloadParagraphOffset:X} is 0x{paragraphs:X4} " +
                $"but must be a copy of e_cs (0x{header.Cs:X4})");
        }

        return new SlrLzhStubInfo(
            imageOffset,
            fileOffset,
            StubFileLength,
            copyright,
            stub.Slice(CodeLengthCountOffset, CodeLengthCountSize).ToArray(),
            paragraphs,
            Word(stub, 0x07),
            Word(stub, 0x09),
            Word(stub, 0x0B),
            Word(stub, 0x0D),
            Word(stub, 0x8D),
            Word(stub, 0x91));
    }

    private static ushort Word(ReadOnlySpan<byte> bytes, int offset) =>
        (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    /// <summary>
    /// Runs layer 3 in place: patches the inline header, relocates the stub and payload, and expands
    /// the compressed stream over the bottom of the image.
    /// </summary>
    /// <param name="memory">The program image, already holding the packed file's body at index 0.</param>
    /// <param name="header">The packed file's <c>MZ</c> header.</param>
    /// <param name="stack">The stack DOS would have handed the stub.</param>
    /// <returns>The number of bytes the layer emitted.</returns>
    internal static int Expand(StubMemory memory, MzHeader header, StubStack stack) =>
        new Machine(memory, header, stack).Run();

    /// <summary>
    /// The decoder's working state.  Field names are the stub's own register names because every
    /// step below is a transcription of a named part of the algorithm; keeping the names makes the
    /// stub offsets in the comments checkable.
    /// </summary>
    private sealed class Machine
    {
        private readonly StubMemory _m;
        private readonly StubStack _stack;
        private readonly ushort _loadSegment;
        private readonly int _work;         // image index of the stub's working segment (e_ss:0000)
        private readonly int _stubImage;    // image index of the stub in its original position

        private ushort _ax, _bx, _cx, _dx, _si, _bp;
        private int _di;
        private ushort _ds, _es;

        internal Machine(StubMemory memory, MzHeader header, StubStack stack)
        {
            _m = memory;
            _stack = stack;
            _loadSegment = memory.LoadSegment;
            _work = header.Ss * 16;
            _stubImage = header.Cs * 16;
            Header = header;
        }

        private MzHeader Header { get; }

        private byte Al => (byte)_ax;

        private byte Ah => (byte)(_ax >> 8);

        private byte Cl => (byte)_cx;

        private byte Ch => (byte)(_cx >> 8);

        private void SetAl(int v) => _ax = (ushort)((_ax & 0xFF00) | (v & 0xFF));

        private void SetAh(int v) => _ax = (ushort)((_ax & 0x00FF) | ((v & 0xFF) << 8));

        private void SetCl(int v) => _cx = (ushort)((_cx & 0xFF00) | (v & 0xFF));

        private void SetCh(int v) => _cx = (ushort)((_cx & 0x00FF) | ((v & 0xFF) << 8));

        private int DsBase => _m.SegmentBase(_ds);

        private int EsBase => _m.SegmentBase(_es);

        /// <summary>Pulls the next raw byte from the compressed stream (<c>MOV reg,[BX] / INC BX</c>).</summary>
        private byte NextStreamByte()
        {
            byte value = _m.Read8(DsBase + _bx);
            _bx = (ushort)(_bx + 1);
            return value;
        }

        internal int Run()
        {
            // stub+0x10..0x21 — DX = PSP + 0x10 = the program image's base segment; the inline
            // header's SS and CS become absolute.
            _dx = _loadSegment;
            _stack.Push(_dx);                                       // stub+0x15
            _es = (ushort)(_loadSegment + Header.Ss);
            _ds = (ushort)(_loadSegment + Header.Cs);
            _m.Write16(_stubImage + 0x09, (ushort)(_m.Read16(_stubImage + 0x09) + _dx));
            _m.Write16(_stubImage + 0x0D, (ushort)(_m.Read16(_stubImage + 0x0D) + _dx));

            // stub+0x22..0x30 — the stub copies itself to e_ss:0000 and continues there.
            _m.Move(_work, _stubImage, StubLength);
            _stack.Push(_es);
            _stack.Push(0x0031);
            _stack.Pop();
            _stack.Pop();

            MovePayload();

            // stub+0x60..0x79 — remember the stream segment, seed both trees' alphabet sizes.
            _stack.Push(_es);
            _bx = (ushort)(_di + 2);
            _stack.Push((ushort)(_loadSegment + Header.Ss));
            _es = _stack.Pop();
            _stack.Push((ushort)(_loadSegment + Header.Ss));
            _ds = _stack.Pop();
            _si = 0x8D;
            _m.Move(_work + LiteralTreeBase, _work + _si, 4);
            _si = 0x91;
            _m.Move(_work + DistanceTreeBase, _work + _si, 4);
            _si = 0x95;
            _ds = _stack.Pop();
            _di = 0;
            _es = _dx;                                              // stub+0x7B..0x7F
            _bp = LiteralTreeBase;

            // stub+0x81..0x87 — prime the bit window with two stream bytes.
            SetAh(0);
            _dx = (ushort)(NextStreamByte() << 8);
            _dx |= NextStreamByte();
            _cx = 8;

            // stub+0x8A — the stream always opens with a tree-definition record.
            _stack.Push(0x011C);
            RebuildTrees();
            _stack.Pop();

            MainLoop();

            // stub+0x150..0x15A — the exit path calls the (stubbed-out) character printer twice,
            // which also runs the stream-pointer normalisation, then restores the caller's DX.
            SetAl(0x0D);
            _stack.Push(0x0155);
            NormaliseStreamCursor();
            _stack.Pop();
            SetAl(0x0A);
            _stack.Push(0x015A);
            NormaliseStreamCursor();
            _stack.Pop();
            _dx = _stack.Pop();

            return EsBase + _di;
        }

        /// <summary>
        /// stub+0x31..0x5F — slides the whole compressed payload up to just below the stub's copy,
        /// in descending 64 KB chunks, so the expansion can run downward from image offset 0.
        /// </summary>
        private void MovePayload()
        {
            _bx = _m.Read16(_work + PayloadParagraphOffset);
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
                _si = 0xFFFE;
                if (_bx == 0)
                {
                    return;
                }
            }
        }

        /// <summary>stub+0x96..0x122 — the LZ77 symbol loop.</summary>
        private void MainLoop()
        {
            while (true)
            {
                // stub+0x96..0xA8 — decode one literal/length symbol.
                SetAh(0);
                SetAl(_dx >> 8);
                (_ax, _si) = (_si, _ax);
                SetAl(_m.Read8(_work + _bp + 0x188 + _si));
                _si = (ushort)(_si * 2);
                _si = _m.Read16(_work + _bp + 0x288 + _si);
                _stack.Push(0x00A8);
                ConsumeBits();
                _stack.Pop();
                (_ax, _si) = (_si, _ax);

                if (_ax >= _m.Read16(_work + LiteralTreeBase))
                {
                    // stub+0xFC — the 8-bit table gave a node index, not a symbol: walk the tree.
                    (_ax, _si) = (_si, _ax);
                    _stack.Push(0x0100);
                    WalkTree(_bp);
                    _stack.Pop();
                    (_ax, _si) = (_si, _ax);
                }

                if (Ah == 0)
                {
                    // stub+0x95 — a literal byte.
                    _m.Write8(EsBase + _di, Al);
                    _di = (ushort)(_di + 1);
                    continue;
                }

                if (Al >= 0x80)
                {
                    // stub+0xB2..0x11C — the three control symbols.
                    if (Al == 0x80)
                    {
                        _stack.Push(0x0122);
                        NormaliseCursors();
                        _stack.Pop();
                    }
                    else if (Al == 0x82)
                    {
                        return;
                    }
                    else
                    {
                        _stack.Push(0x011C);
                        RebuildTrees();
                        _stack.Pop();
                    }

                    continue;
                }

                if (Al == 0)
                {
                    // stub+0x103 — a 2-byte back-reference; the distance is one raw stream byte.
                    SetAl(NextStreamByte());
                    SetAh(0);
                    _si = (ushort)(_di - 1 - _ax);
                    int window = EsBase;
                    _m.Write8(window + _di, _m.Read8(window + _si));
                    _di = (ushort)(_di + 1);
                    _si = (ushort)(_si + 1);
                    _m.Write8(window + _di, _m.Read8(window + _si));
                    _di = (ushort)(_di + 1);
                    _si = (ushort)(_si + 1);
                    continue;
                }

                // stub+0xBA..0xF0 — a back-reference of length AL+2; the distance's high byte comes
                // from the distance tree, its low byte straight from the stream.
                SetAh(0);
                _stack.Push(_ax);
                SetAl(_dx >> 8);
                (_ax, _si) = (_si, _ax);
                SetAl(_m.Read8(_work + _bp + 0x1230 + _si));
                _si = (ushort)(_si * 2);
                _si = _m.Read16(_work + _bp + 0x1330 + _si);
                _stack.Push(0x00CD);
                ConsumeBits();
                _stack.Pop();
                if (_si >= _m.Read16(_work + DistanceTreeBase))
                {
                    _stack.Push(_bp);
                    _bp = DistanceTreeBase;
                    _stack.Push(0x00F9);
                    WalkTree(_bp);
                    _stack.Pop();
                    _bp = _stack.Pop();
                }

                (_ax, _si) = (_si, _ax);
                _ax = (ushort)(((_ax & 0xFF) << 8) | ((_ax >> 8) & 0xFF));
                _si = _ax;
                SetAl(NextStreamByte());
                SetAh(0);
                _si = (ushort)(_si + _ax);
                _si = (ushort)(-_si);
                _si = (ushort)(_si + _di);
                _si = (ushort)(_si - 1);
                _ax = _stack.Pop();
                _ax = (ushort)(_ax + 2);
                (_cx, _ax) = (_ax, _cx);
                _stack.Push(_ds);
                _stack.Push(_es);
                _ds = _stack.Pop();
                int output = EsBase;
                for (int i = _cx; i > 0; i--)
                {
                    // The LZ77 copy must be byte-by-byte and forward: source and destination overlap
                    // whenever the distance is shorter than the length (run expansion).
                    _m.Write8(output + _di, _m.Read8(output + _si));
                    _si = (ushort)(_si + 1);
                    _di = (ushort)(_di + 1);
                }

                _cx = 0;
                _ds = _stack.Pop();
                (_cx, _ax) = (_ax, _cx);
            }
        }

        /// <summary>stub+0x1C2 — drops <c>AL</c> bits from the window, refilling the low byte.</summary>
        private void ConsumeBits()
        {
            int n = Al;
            if (Cl >= n)
            {
                _dx = (ushort)(_dx << n);
                SetCl(Cl - n);
                if (Cl == 0)
                {
                    _dx = (ushort)((_dx & 0xFF00) | NextStreamByte());
                    SetCl(8);
                }
            }
            else
            {
                int held = Cl;
                _dx = (ushort)(_dx << held);
                _dx = (ushort)((_dx & 0xFF00) | NextStreamByte());
                SetAl(n - held);
                SetCl(Al);
                _dx = (ushort)(_dx << Cl);
                SetCl(8 - Al);
            }
        }

        /// <summary>stub+0x19C — reads a single bit into <c>AX</c>.</summary>
        private void ReadBit()
        {
            SetAl(1);
            _stack.Push(_si);
            _stack.Push(_ax);
            ushort bit = (ushort)(_dx >> 15);
            _ax = _stack.Pop();
            _si = bit;
            _stack.Push(_si);
            _stack.Push(0x01B3);
            ConsumeBits();
            _stack.Pop();
            _ax = _stack.Pop();
            _si = _stack.Pop();
        }

        /// <summary>
        /// stub+0x178 — walks the tree bit by bit from the node index in <c>SI</c> until it lands on
        /// a symbol (any value below the tree's alphabet size).
        /// </summary>
        /// <param name="treeBase">The tree's control-block offset in the working segment.</param>
        private void WalkTree(int treeBase)
        {
            while (true)
            {
                _si = (ushort)(_si * 2);
                int bit = (_dx >> 15) & 1;
                _dx = (ushort)(_dx << 1);
                _cx = (ushort)(_cx - 1);
                if (_cx == 0)
                {
                    _dx = (ushort)((_dx & 0xFF00) | NextStreamByte());
                    _cx = (ushort)((_cx & 0xFF00) | 8);
                }

                _si = _m.Read16(_work + treeBase + (bit != 0 ? 0x488 : 0xA98) + _si);
                if (_m.Read16(_work + treeBase) > _si)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// stub+0x1FE — the canonical-code assignment walk.  It descends the code tree in code
        /// order; at each depth it takes the next symbol whose declared code length equals that
        /// depth, filling the 8-bit lookup table for short codes and building explicit nodes
        /// otherwise.  Returns the leaf's symbol or the internal node's index in <c>AX</c>.
        /// </summary>
        /// <param name="treeBase">The tree's control-block offset in the working segment.</param>
        private void AssignCodes(int treeBase)
        {
            int control = _work + treeBase;
            ushort alphabet = _m.Read16(control);
            if (Ch == Cl)
            {
                _dx = (ushort)(_dx + 1);
                _ax = (ushort)(alphabet - _dx);
                if (alphabet > _dx)
                {
                    (_cx, _ax) = (_ax, _cx);
                    _di = control + 4 + _dx;
                    byte wanted = Al;
                    bool found = false;
                    while (_cx != 0)
                    {
                        byte length = _m.Read8(_di);
                        _di++;
                        _cx--;
                        if (length == wanted)
                        {
                            found = true;
                            break;
                        }
                    }

                    (_cx, _ax) = (_ax, _cx);
                    if (found)
                    {
                        _dx = (ushort)(_di - 5 - control);
                        if (Ch <= 8)
                        {
                            // A code of 8 bits or fewer resolves in one table lookup: give every
                            // 8-bit prefix that starts with this code the symbol and its length.
                            SetAl(8 - Ch);
                            (_cx, _ax) = (_ax, _cx);
                            SetCh(1);
                            SetCh(1 << Cl);
                            _di = _si;
                            SetCl(8);
                            _di >>= 8;
                            _stack.Push(_ax);
                            _stack.Push((ushort)_di);
                            int lengths = control + 0x188 + _di;
                            SetCl(Ch);
                            SetCh(0);
                            _stack.Push(_cx);
                            SetAl(Ah);
                            for (int i = 0; i < _cx; i++)
                            {
                                _m.Write8(lengths + i, Al);
                            }

                            _di = lengths + _cx;
                            _cx = 0;
                            _cx = _stack.Pop();
                            _di = _stack.Pop();
                            _di = (ushort)(_di * 2);
                            int symbols = control + 0x288 + _di;
                            _ax = _dx;
                            for (int i = 0; i < _cx; i++)
                            {
                                _m.Write16(symbols + (i * 2), _ax);
                            }

                            _di = symbols + (_cx * 2);
                            _cx = 0;
                            _cx = _stack.Pop();
                        }

                        _ax = _dx;
                        return;
                    }
                }

                // stub+0x24D — no symbol left at this length: start looking for the next one.
                _dx = 0xFFFF;
                SetCh(Ch + 1);
            }

            // stub+0x252 — an internal node: build both children, remember their indices.
            _stack.Push(_bx);
            _bx = (ushort)(_bx + 1);
            _cx = (ushort)(_cx + 1);
            _stack.Push(0x0258);
            AssignCodes(treeBase);
            _stack.Pop();
            _di = _stack.Pop();
            _di = (ushort)(_di * 2);
            _m.Write16(control + 0xA98 + _di, _ax);
            _stack.Push(_si);
            _ax = 0x8000;
            _cx = (ushort)(_cx - 1);
            _ax = (ushort)(_ax >> Cl);
            _cx = (ushort)(_cx + 1);
            _si ^= _ax;
            _stack.Push((ushort)_di);
            _stack.Push(0x026D);
            AssignCodes(treeBase);
            _stack.Pop();
            _di = _stack.Pop();
            _si = _stack.Pop();
            _m.Write16(control + 0x488 + _di, _ax);
            _cx = (ushort)(_cx - 1);
            _di >>= 1;
            if (Cl == 8)
            {
                // An internal node at depth 8 owns a whole 8-bit prefix: park its index in the
                // lookup table so the decoder knows to fall through to the bit-by-bit walk.
                _ax = _si;
                SetAl(Ah);
                SetAh(0);
                (_ax, _si) = (_si, _ax);
                _m.Write8(control + 0x188 + _si, Cl);
                _si = (ushort)(_si * 2);
                _m.Write16(control + 0x288 + _si, (ushort)_di);
                (_ax, _si) = (_si, _ax);
            }

            // stub+0x28D — XCHG DI,AX returns the node index in AX.
            int previousDi = _di;
            _di = _ax;
            _ax = (ushort)previousDi;
        }

        /// <summary>stub+0x1E5 — (re)builds one tree's lookup tables from its code-length array.</summary>
        /// <param name="treeBase">The tree's control-block offset in the working segment.</param>
        private void BuildTables(int treeBase)
        {
            _stack.Push(_ds);
            _stack.Push(_si);
            _stack.Push(_dx);
            _stack.Push(_cx);
            _stack.Push(_bx);
            _bx = _m.Read16(_work + treeBase);
            _cx = 0x0100;
            _dx = 0xFFFF;
            _si = 0;
            _stack.Push(0x01F8);
            AssignCodes(treeBase);
            _stack.Pop();
            _bx = _stack.Pop();
            _cx = _stack.Pop();
            _dx = _stack.Pop();
            _si = _stack.Pop();
            _ds = _stack.Pop();
        }

        /// <summary>
        /// stub+0x2E0 — reads a tree's code lengths from the stream as a nibble-packed run-length
        /// list: one count byte, then per group a byte of <c>(repeats &lt;&lt; 4) | (length − 1)</c>.
        /// </summary>
        /// <param name="treeBase">The tree's control-block offset in the working segment.</param>
        private void ReadTreeDefinition(int treeBase)
        {
            _stack.Push(_cx);
            _di = _work + treeBase + 4;
            SetAl(NextStreamByte());
            SetAh(0);
            _si = _ax;
            _si = (ushort)(_si + 1);
            SetCh(0);
            while (true)
            {
                SetAl(NextStreamByte());
                SetAh(0);
                SetAh(Al);
                SetCl(4);
                SetAh(Ah >> 4);
                SetCl(Ah);
                SetAl(Al & 0x0F);
                _ax = (ushort)(_ax + 1);
                _m.Write8(_di, Al);
                _di++;
                for (int i = Cl; i > 0; i--)
                {
                    _m.Write8(_di, Al);
                    _di++;
                }

                SetCl(0);
                _si = (ushort)(_si - 1);
                if (_si == 0)
                {
                    break;
                }
            }

            _cx = _stack.Pop();
            BuildTables(treeBase);
        }

        /// <summary>
        /// stub+0x28F — the tree-definition record: one flag bit per tree, plus a third bit that
        /// selects the distance tree baked into the stub trailer.
        /// </summary>
        private void RebuildTrees()
        {
            _stack.Push(_es);
            _stack.Push((ushort)_di);
            _stack.Push((ushort)(_loadSegment + Header.Ss));
            _es = _stack.Pop();
            _bp = LiteralTreeBase;
            _stack.Push(0x0296);
            ReadBit();
            _stack.Pop();
            if (_ax != 0)
            {
                _stack.Push(0x029D);
                ReadTreeDefinition(LiteralTreeBase);
                _stack.Pop();
            }

            _bp = DistanceTreeBase;
            _stack.Push(0x02A3);
            ReadBit();
            _stack.Pop();
            if (_ax != 0)
            {
                _stack.Push(0x02AA);
                ReadTreeDefinition(DistanceTreeBase);
                _stack.Pop();
            }
            else
            {
                _stack.Push(0x02B5);
                ReadBit();
                _stack.Pop();
                if (_ax != 0)
                {
                    ReadTrailerDistanceTree();
                }
            }

            _di = _stack.Pop();
            _es = _stack.Pop();
            SetCh(0);
            _bp = LiteralTreeBase;
        }

        /// <summary>
        /// stub+0x2B9 — expands the 11 code-length counts in the stub trailer into the distance
        /// tree's code-length array: <c>counts[i]</c> symbols get length <c>i + 1</c>.
        /// </summary>
        private void ReadTrailerDistanceTree()
        {
            _stack.Push(_ds);
            _stack.Push(_si);
            _stack.Push(_dx);
            _stack.Push(_cx);
            _stack.Push(_bx);
            _stack.Push((ushort)(_loadSegment + Header.Ss));
            _ds = _stack.Pop();
            _si = CodeLengthCountOffset;
            _di = _work + _bp + 4;
            SetAl(1);
            _dx = CodeLengthCountSize;
            SetCh(0);
            while (true)
            {
                SetCl(_m.Read8(_work + _si));
                _si = (ushort)(_si + 1);
                for (int i = Cl; i > 0; i--)
                {
                    _m.Write8(_di, Al);
                    _di++;
                }

                SetCl(0);
                _ax = (ushort)(_ax + 1);
                _dx = (ushort)(_dx - 1);
                if (_dx == 0)
                {
                    break;
                }
            }

            _stack.Push(0x02D9);
            BuildTables(_bp);
            _stack.Pop();
            _bx = _stack.Pop();
            _cx = _stack.Pop();
            _dx = _stack.Pop();
            _si = _stack.Pop();
            _ds = _stack.Pop();
        }

        /// <summary>
        /// stub+0x125 — control symbol 384: re-bases the output cursor when it approaches the end of
        /// its 64 KB segment (and the stream cursor with it).  Both moves preserve the physical
        /// address, so in a flat image they change nothing but the register split.
        /// </summary>
        private void NormaliseCursors()
        {
            if (_di >= 0xC000)
            {
                _di = (ushort)(_di - 0x4000);
                _es = (ushort)(_es + 0x400);
            }

            NormaliseStreamCursor();
        }

        /// <summary>
        /// stub+0x138..0x14F — the character printer whose <c>INT 21h</c> the shipped build has
        /// patched out (the two bytes are <c>XCHG AX,AX</c>); what survives is the stream cursor's
        /// 32 KB re-base, which is why the exit path calls it too.
        /// </summary>
        private void NormaliseStreamCursor()
        {
            _stack.Push(_dx);
            _dx = _stack.Pop();
            if (_bx >= 0x8000)
            {
                _ds = (ushort)(_ds + 0x800);
                _bx = (ushort)(_bx - 0x8000);
            }
        }
    }
}
