using System.Buffers.Binary;

namespace CYAC.Formats.Cockpit;

/// <summary>
/// Reads a cockpit compositor pack: its region map always, and — for the two 8-bit modes — the
/// cockpit bitmap its straight-line paint program writes.
/// </summary>
/// <remarks>
/// <para>
/// Source of truth: and its Python reference, which this is a port of.  The pack is machine code
/// (the code-is-not-data rule), so "decoding" it means recovering the PARAMETERS a generator needs to
/// re-emit it, not converting the code into data.
/// </para>
/// <para>
/// <b>Why this is not a disassembler.</b>  The pack is one linear program with no
/// jumps, calls, conditionals or dispatch, written from five paint idioms.  So the simulator matches
/// exactly the instruction encodings the generator emits and REFUSES anything else
/// (<see cref="InvalidDataException"/> naming the offset and the bytes).  A pack that is not the
/// shape is a measured finding, not something to guess at.
/// </para>
/// </remarks>
public static class CockpitPackParser
{
    /// <summary>Screen width in pixels — every mode paints a 320×200 screen.</summary>
    public const int ScreenWidth = 320;

    /// <summary>Screen height in pixels.</summary>
    public const int ScreenHeight = 200;

    /// <summary>Bytes per row in one Mode-X plane: 320 / 4.</summary>
    public const int ModeXPlanePitch = ScreenWidth / 4;

    /// <summary>The element list's terminator byte.</summary>
    public const byte ElementListTerminator = 0xFF;

    /// <summary>Recognises the mode from a pack's asset name (<c>51_VGA.BIN</c> → VGA).</summary>
    /// <param name="assetName">The EALIB member name.</param>
    /// <param name="mode">The mode, when the name carries one.</param>
    public static bool TryModeOf(string assetName, out CockpitPackMode mode)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        string upper = assetName.ToUpperInvariant();
        foreach (CockpitPackMode candidate in Enum.GetValues<CockpitPackMode>())
        {
            if (upper.Contains($"_{candidate.ToString().ToUpperInvariant()}.", StringComparison.Ordinal))
            {
                mode = candidate;
                return true;
            }
        }

        mode = default;
        return false;
    }

    /// <summary>Whether a mode's paint program is one this build can simulate and regenerate.</summary>
    /// <param name="mode">The mode to test.</param>
    public static bool IsGenerated(CockpitPackMode mode) =>
        mode is CockpitPackMode.Vga or CockpitPackMode.Mcga;

    /// <summary>Reads a pack's region map.</summary>
    /// <param name="pack">The decompressed asset body.</param>
    /// <param name="mode">The mode its name declares.</param>
    /// <exception cref="InvalidDataException">The header does not describe a pack of that mode.</exception>
    public static CockpitPackLayout ReadLayout(ReadOnlySpan<byte> pack, CockpitPackMode mode)
    {
        if (pack.Length < 8)
        {
            throw new InvalidDataException($"a cockpit pack is at least 8 B; this one is {pack.Length}");
        }

        int paintEntry = BinaryPrimitives.ReadUInt16LittleEndian(pack);
        int elementList = BinaryPrimitives.ReadUInt16LittleEndian(pack[2..]);
        if (elementList <= 0 || elementList >= pack.Length)
        {
            throw new InvalidDataException(
                $"header word 1 (element-list offset) is 0x{elementList:X4}, outside the {pack.Length} B pack");
        }

        byte[] elements = ReadElementList(pack, elementList);

        if (mode != CockpitPackMode.Vga)
        {
            if (paintEntry <= 0 || paintEntry >= pack.Length)
            {
                throw new InvalidDataException(
                    $"header word 0 (paint entry) is 0x{paintEntry:X4}, outside the {pack.Length} B pack");
            }

            return new CockpitPackLayout
            {
                Mode = mode,
                Length = pack.Length,
                HeaderLength = 4,
                PaintEntry = paintEntry,
                ElementListOffset = elementList,
                DataStart = 4,
                DataEnd = elementList,
                CodeStart = paintEntry,
                CodeEnd = pack.Length,
                Elements = elements,
            };
        }

        if (pack.Length < 12)
        {
            throw new InvalidDataException($"a VGA pack has a 12-byte header; this one is {pack.Length} B");
        }

        int initEntry = BinaryPrimitives.ReadUInt16LittleEndian(pack[4..]);
        int unusedEntry = BinaryPrimitives.ReadUInt16LittleEndian(pack[6..]);
        int dataPointerOffset = BinaryPrimitives.ReadUInt16LittleEndian(pack[8..]);
        int dataPointerSegment = BinaryPrimitives.ReadUInt16LittleEndian(pack[10..]);
        int dataStart = FindSelfPatchedDataOffset(pack, initEntry);

        return new CockpitPackLayout
        {
            Mode = mode,
            Length = pack.Length,
            HeaderLength = 12,
            PaintEntry = paintEntry,
            ElementListOffset = elementList,
            InitEntry = initEntry,
            UnusedEntry = unusedEntry,
            DataPointerOffset = dataPointerOffset,
            DataPointerSegment = dataPointerSegment,
            DataStart = dataStart,
            DataEnd = elementList,
            CodeStart = 12,
            CodeEnd = dataStart,
            Elements = elements,
        };
    }

    /// <summary>
    /// Runs the pack's paint program and returns the cockpit bitmap it writes.
    /// </summary>
    /// <param name="pack">The decompressed asset body.</param>
    /// <param name="layout">Its region map, from <see cref="ReadLayout"/>.</param>
    /// <exception cref="InvalidDataException">
    /// The mode is one whose program this build does not model, or the code region contains an
    /// instruction encoding outside the generator's vocabulary.
    /// </exception>
    public static CockpitPaintProgram Simulate(ReadOnlySpan<byte> pack, CockpitPackLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!IsGenerated(layout.Mode))
        {
            throw new InvalidDataException(
                $"{layout.Mode} packs are a different program family and this build " +
                "does not simulate them; their regions are censused instead");
        }

        byte[] canvas = new byte[ScreenWidth * ScreenHeight];
        bool[] painted = new bool[ScreenWidth * ScreenHeight];
        // Mode-X selects a plane with an OUT to the sequencer; nothing before the first such OUT can
        // paint, which is also how the two VGA prologue stubs are skipped without decoding them as
        // paint.  Linear modes have a single implicit plane.
        int plane = layout.Mode == CockpitPackMode.Vga ? -1 : 0;
        int destination = 0;
        int count = 0;
        int source = layout.DataStart;
        int port = -1;
        int copyBytes = 0;
        int literals = 0;

        int at = layout.CodeStart;
        while (at < layout.CodeEnd)
        {
            PackInstruction instruction = Decode(pack, at, layout.CodeEnd);
            at += instruction.Length;
            switch (instruction.Kind)
            {
                case PackOp.SetDx:
                    port = instruction.Immediate;
                    break;

                case PackOp.OutDxAx:
                    if (port == SequencerPort && layout.Mode == CockpitPackMode.Vga)
                    {
                        plane++;
                    }

                    break;

                case PackOp.SetDi:
                    destination = instruction.Immediate;
                    break;

                case PackOp.SetCx:
                    count = instruction.Immediate;
                    break;

                case PackOp.SetSi:
                    source = instruction.Immediate;
                    break;

                case PackOp.MoveString:
                {
                    if (plane < 0)
                    {
                        break;
                    }

                    int moved = instruction.Repeat ? count * instruction.Width : instruction.Width;
                    for (int i = 0; i < moved; i++)
                    {
                        Paint(canvas, painted, layout.Mode, plane, destination + i, ReadData(pack, source + i));
                    }

                    destination += moved;
                    source += moved;
                    copyBytes += moved;
                    break;
                }

                case PackOp.StoreImmediate8:
                    if (plane >= 0)
                    {
                        Paint(canvas, painted, layout.Mode, plane, instruction.Address, (byte)instruction.Immediate);
                        literals++;
                    }

                    break;

                case PackOp.StoreImmediate16:
                    if (plane >= 0)
                    {
                        Paint(canvas, painted, layout.Mode, plane, instruction.Address, (byte)(instruction.Immediate & 0xFF));
                        Paint(canvas, painted, layout.Mode, plane, instruction.Address + 1, (byte)(instruction.Immediate >> 8));
                        literals += 2;
                    }

                    break;

                default:
                    break;
            }
        }

        return new CockpitPaintProgram(RunsOf(canvas, painted), copyBytes, literals, source);
    }

    /// <summary>Splits a painted canvas into the run list the generator re-emits from.</summary>
    /// <param name="canvas">320×200 palette indices.</param>
    /// <param name="painted">Which of them are opaque.</param>
    public static IReadOnlyList<CockpitPaintRun> RunsOf(ReadOnlySpan<byte> canvas, ReadOnlySpan<bool> painted)
    {
        List<CockpitPaintRun> runs = new List<CockpitPaintRun>();
        for (int y = 0; y < ScreenHeight; y++)
        {
            int x = 0;
            while (x < ScreenWidth)
            {
                if (!painted[(y * ScreenWidth) + x])
                {
                    x++;
                    continue;
                }

                int start = x;
                while (x < ScreenWidth && painted[(y * ScreenWidth) + x])
                {
                    x++;
                }

                runs.Add(new CockpitPaintRun(y, start, canvas.Slice((y * ScreenWidth) + start, x - start).ToArray()));
            }
        }

        return runs;
    }

    private const int SequencerPort = 0x3C4;

    private static byte ReadData(ReadOnlySpan<byte> pack, int offset)
    {
        if (offset < 0 || offset >= pack.Length)
        {
            throw new InvalidDataException(
                $"the paint program's data cursor ran to 0x{offset:X}, outside the {pack.Length} B pack");
        }

        return pack[offset];
    }

    private static void Paint(
        byte[] canvas, bool[] painted, CockpitPackMode mode, int plane, int offset, byte value)
    {
        int x;
        int y;
        if (mode == CockpitPackMode.Vga)
        {
            x = ((offset % ModeXPlanePitch) * 4) + plane;
            y = offset / ModeXPlanePitch;
        }
        else
        {
            x = offset % ScreenWidth;
            y = offset / ScreenWidth;
        }

        if (y < 0 || y >= ScreenHeight || x < 0 || x >= ScreenWidth)
        {
            throw new InvalidDataException(
                $"the paint program writes VRAM offset 0x{offset:X} (plane {plane}), which is outside " +
                $"the {ScreenWidth}x{ScreenHeight} screen");
        }

        canvas[(y * ScreenWidth) + x] = value;
        painted[(y * ScreenWidth) + x] = true;
    }

    private static byte[] ReadElementList(ReadOnlySpan<byte> pack, int elementList)
    {
        int end = elementList;
        while (end < pack.Length && pack[end] != ElementListTerminator)
        {
            end++;
        }

        if (end >= pack.Length)
        {
            throw new InvalidDataException(
                $"the element list at 0x{elementList:X4} is not 0xFF-terminated before the end of the pack");
        }

        return pack[elementList..end].ToArray();
    }

    private static int FindSelfPatchedDataOffset(ReadOnlySpan<byte> pack, int initEntry)
    {
        // The init stub's only real job is `mov word cs:[8], <data offset>` — the self-patch that
        // fills the header's far pointer in.  Its immediate IS the data offset.
        ReadOnlySpan<byte> pattern = [0x2E, 0xC7, 0x06, 0x08, 0x00];
        int from = Math.Max(0, initEntry);
        int to = Math.Min(pack.Length - pattern.Length - 2, from + 0x40);
        for (int at = from; at <= to; at++)
        {
            if (pack.Slice(at, pattern.Length).SequenceEqual(pattern))
            {
                return BinaryPrimitives.ReadUInt16LittleEndian(pack[(at + pattern.Length)..]);
            }
        }

        throw new InvalidDataException(
            $"no `mov word cs:[8], imm16` self-patch in the init stub at 0x{initEntry:X4}; this is not " +
            "a VGA cockpit pack of the shipping shape");
    }

    private enum PackOp
    {
        Ignored,
        SetDx,
        SetDi,
        SetSi,
        SetCx,
        OutDxAx,
        MoveString,
        StoreImmediate8,
        StoreImmediate16,
    }

    private readonly record struct PackInstruction(
        PackOp Kind, int Length, int Immediate = 0, int Address = 0, int Width = 1, bool Repeat = false);

    // The generator's whole instruction vocabulary (`token`).  Any other encoding throws — see the
    // type remarks.
    private static PackInstruction Decode(ReadOnlySpan<byte> pack, int at, int end)
    {
        byte op = pack[at];
        switch (op)
        {
            case 0x55: // push bp
            case 0x53: // push bx
            case 0x51: // push cx
            case 0x52: // push dx
            case 0x56: // push si
            case 0x57: // push di
            case 0x1E: // push ds
            case 0x06: // push es
            case 0x07: // pop es
            case 0x1F: // pop ds
            case 0x5F: // pop di
            case 0x5E: // pop si
            case 0x5A: // pop dx
            case 0x59: // pop cx
            case 0x5B: // pop bx
            case 0x5D: // pop bp
            case 0xCB: // retf
                return new PackInstruction(PackOp.Ignored, 1);

            case 0xA4: // movsb
                return new PackInstruction(PackOp.MoveString, 1, Width: 1);
            case 0xA5: // movsw
                return new PackInstruction(PackOp.MoveString, 1, Width: 2);

            case 0xEF: // out dx, ax
                return new PackInstruction(PackOp.OutDxAx, 1);

            case 0xF3 when At(pack, at + 1, end) == 0xA4: // rep movsb
                return new PackInstruction(PackOp.MoveString, 2, Width: 1, Repeat: true);
            case 0xF3 when At(pack, at + 1, end) == 0xA5: // rep movsw
                return new PackInstruction(PackOp.MoveString, 2, Width: 2, Repeat: true);

            case 0xB8: // mov ax, imm16
                return new PackInstruction(PackOp.Ignored, 3, Word(pack, at + 1, end));
            case 0xB9: // mov cx, imm16
                return new PackInstruction(PackOp.SetCx, 3, Word(pack, at + 1, end));
            case 0xBA: // mov dx, imm16
                return new PackInstruction(PackOp.SetDx, 3, Word(pack, at + 1, end));
            case 0xBE: // mov si, imm16
                return new PackInstruction(PackOp.SetSi, 3, Word(pack, at + 1, end));
            case 0xBF: // mov di, imm16
                return new PackInstruction(PackOp.SetDi, 3, Word(pack, at + 1, end));

            case 0x33 when At(pack, at + 1, end) == 0xFF: // xor di, di
                return new PackInstruction(PackOp.SetDi, 2, 0);

            case 0x8B when At(pack, at + 1, end) == 0xEC: // mov bp, sp
                return new PackInstruction(PackOp.Ignored, 2);
            case 0x8B when At(pack, at + 1, end) == 0x46: // mov ax, [bp+disp8]
                return new PackInstruction(PackOp.Ignored, 3);
            case 0x8C when At(pack, at + 1, end) == 0xC8: // mov ax, cs
                return new PackInstruction(PackOp.Ignored, 2);
            case 0x8E when At(pack, at + 1, end) is 0xC0 or 0xD8: // mov es,ax / mov ds,ax
                return new PackInstruction(PackOp.Ignored, 2);

            case 0x2E when At(pack, at + 1, end) == 0xC5 && At(pack, at + 2, end) == 0x36:
                return new PackInstruction(PackOp.Ignored, 5);      // lds si, cs:[imm16]
            case 0x2E when At(pack, at + 1, end) == 0x8C && At(pack, at + 2, end) == 0x0E:
                return new PackInstruction(PackOp.Ignored, 5);      // mov cs:[imm16], cs
            case 0x2E when At(pack, at + 1, end) == 0xC7 && At(pack, at + 2, end) == 0x06:
                return new PackInstruction(PackOp.Ignored, 7);      // mov word cs:[imm16], imm16

            case 0x26 when At(pack, at + 1, end) == 0xC6 && At(pack, at + 2, end) == 0x06:
                return new PackInstruction(
                    PackOp.StoreImmediate8, 6, At(pack, at + 5, end), Word(pack, at + 3, end));
            case 0x26 when At(pack, at + 1, end) == 0xC7 && At(pack, at + 2, end) == 0x06:
                return new PackInstruction(
                    PackOp.StoreImmediate16, 7, Word(pack, at + 5, end), Word(pack, at + 3, end));

            default:
                throw new InvalidDataException(
                    $"unmodelled instruction at pack offset 0x{at:X4}: " +
                    $"{Convert.ToHexString(pack[at..Math.Min(end, at + 6)])} — the generator's " +
                    "vocabulary is the five paint idioms plus the two stubs");
        }
    }

    private static byte At(ReadOnlySpan<byte> pack, int at, int end) =>
        at < end && at < pack.Length ? pack[at] : (byte)0;

    private static int Word(ReadOnlySpan<byte> pack, int at, int end)
    {
        if (at + 2 > Math.Min(end, pack.Length))
        {
            throw new InvalidDataException($"an instruction's immediate runs past the code region at 0x{at:X4}");
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(pack[at..]);
    }
}
