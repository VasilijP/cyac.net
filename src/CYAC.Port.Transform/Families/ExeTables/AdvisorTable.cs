using System.Text.Json;
using CYAC.Port.Core.Data;

namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>
/// The in-flight advisor's line table (<c>exe/tables/advisor.json</c>): which strings each action code
/// shows, read by following the message dispatcher's own code.
/// </summary>
/// <remarks>
/// <para>
/// <c>ai_advisor_message_dispatch @image@0x0EFF1</c> range-checks the action code and jumps through a
/// CS-relative table (<c>cmp ax,imm / ja tail / shl ax,1 / xchg bx,ax / jmp cs:[bx+table]</c> at
/// <c>image@0x0F2D5</c>).  Each handler loads a far pointer into the line-1 pair
/// <c>[0xBCBA]/[0xBCBC]</c> and one into the line-2 pair <c>[0xBCBE]/[0xBCC0]</c>
/// (<c>mov word [addr],imm16</c>), then reaches the common tail at the range check's <c>ja</c> target.
/// </para>
/// <para>
/// The forward pass walks every path from each handler to the tail, following jumps and both sides of
/// every conditional branch, and records what each path leaves in the two pairs.  Three handler forms
/// occur and are read the same way:
/// </para>
/// <list type="bullet">
/// <item>a handler whose paths disagree on line 1 chooses between first lines; the distinct pointers,
/// in the order the walk meets them (fall-through first), are its <c>variants</c>;</item>
/// <item>a handler that zeroes both line-2 halves through <c>sub ax,ax / mov [addr],ax</c> has no second
/// line, and <c>line2ClearedAt</c> says where;</item>
/// <item>a handler that loads a far pointer into <c>cx:ax</c>, pushes both halves and makes a far call
/// passes a <c>banner</c> string to that call.</item>
/// </list>
/// <para>
/// A path that returns without reaching the tail (the dispatcher's refusal exit) shows nothing and is
/// not a message.  Any instruction outside the small set these handlers use stops the transform with
/// the code and the image offset: the walk never guesses.
/// </para>
/// <para>
/// Every pointer's segment must be the same one: its base is the string table's image offset, and each
/// pointer is recorded as its offset inside that table and the image offset it resolves to.  The text
/// stays in <c>exe/strings.json</c>.  The inverse re-encodes exactly the immediate words the handlers
/// load (offset and segment, for every pointer), which is what <c>--verify</c> diffs.
/// </para>
/// </remarks>
internal sealed class AdvisorExeTable : IExeTable
{
    /// <summary>The document's path in the data tree.</summary>
    public const string Path = AdvisorTableDto.DataPath;

    /// <summary><c>ai_advisor_message_dispatch</c>'s entry.</summary>
    public const int DispatcherImageOffset = 0x0EFF1;

    /// <summary>The dispatcher's range check, the first instruction of its switch.</summary>
    public const int SwitchImageOffset = 0x0F2D5;

    /// <summary>
    /// The segment the dispatcher runs in, as relocated for <see cref="ExeAddresses.LoadSegment"/>: its
    /// callers reach it as <c>lcall 0x108E:0xE711</c>, so the jump table's CS-relative words resolve
    /// against this segment's base.
    /// </summary>
    public const int CodeSegment = 0x108E;

    /// <summary><c>g_advisor_line1_ptr</c>'s offset half.</summary>
    public const int Line1OffsetGlobal = 0xBCBA;

    /// <summary><c>g_advisor_line1_ptr</c>'s segment half.</summary>
    public const int Line1SegmentGlobal = 0xBCBC;

    /// <summary><c>g_advisor_line2_ptr</c>'s offset half.</summary>
    public const int Line2OffsetGlobal = 0xBCBE;

    /// <summary><c>g_advisor_line2_ptr</c>'s segment half.</summary>
    public const int Line2SegmentGlobal = 0xBCC0;

    /// <summary>Where the immediate word sits in <c>mov word [addr],imm16</c> (<c>C7 06 addr imm</c>).</summary>
    public const int MemoryMoveOperand = 4;

    /// <summary>Where the immediate word sits in <c>mov reg16,imm16</c> (<c>B8+r imm</c>).</summary>
    public const int RegisterMoveOperand = 1;

    /// <summary>How long the switch is: <c>cmp</c> (3), <c>ja</c> (2), <c>shl</c> (2), <c>xchg</c> (1), <c>jmp</c> (5).</summary>
    private const int SwitchLength = 13;

    /// <summary>The longest path the walk follows before it calls the handler malformed.</summary>
    private const int MaxSteps = 256;

    /// <summary>The most paths one handler may have before the walk calls it malformed.</summary>
    private const int MaxPaths = 64;

    /// <inheritdoc/>
    public string TreePath => Path;

    /// <inheritdoc/>
    public string Description =>
        "the in-flight advisor's line table: for each action code, where its handler points the two "
            + "text lines (and any first-line choice or banner), as offsets into the advisory string table";

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        (int count, int tail, int jumpTable) = ReadSwitch(image);
        int codeBase = (CodeSegment - ExeAddresses.LoadSegment) * 16;
        byte[] bytes = image.ToArray();

        List<(int Code, int Handler, HandlerReading Reading)> walked = new List<(int Code, int Handler, HandlerReading Reading)>(count);
        for (int code = 0; code < count; code++)
        {
            int handler = codeBase + Word(image, jumpTable + (code * 2));
            if (handler < DispatcherImageOffset || handler >= SwitchImageOffset)
            {
                throw Malformed(
                    code, jumpTable + (code * 2),
                    $"the jump table sends it to image@0x{handler:X5}, outside the dispatcher's handlers "
                        + $"(image@0x{DispatcherImageOffset:X5}..0x{SwitchImageOffset:X5})");
            }

            walked.Add((code, handler, HandlerWalker.Read(bytes, code, handler, tail)));
        }

        // One string table: every pointer the handlers load must share its segment.
        List<int> segments = walked
            .SelectMany(w => w.Reading.Pointers())
            .Select(p => p.Segment)
            .Distinct()
            .ToList();
        if (segments.Count != 1)
        {
            throw new InvalidDataException(
                "the advisor handlers load pointers into "
                    + $"{segments.Count} different segments ({string.Join(", ", segments.Select(s => $"0x{s:X4}"))}); "
                    + "exactly one string table was expected");
        }

        int tableBase = ExeAddresses.Resolve(0, (ushort)segments[0]);
        List<AdvisorCodeDto> codes = new List<AdvisorCodeDto>(count);
        int pointers = 0;
        int variants = 0;
        int banners = 0;
        foreach ((int code, int handler, HandlerReading reading) in walked)
        {
            foreach (FarPointer pointer in reading.Pointers())
            {
                RequireString(image, code, pointer, tableBase);
            }

            pointers += reading.Pointers().Count();
            variants += reading.FirstLines.Count > 1 ? reading.FirstLines.Count : 0;
            banners += reading.Banner is null ? 0 : 1;
            codes.Add(new AdvisorCodeDto
            {
                Code = code,
                Handler = PortHex.Format(handler, 5),
                Line1 = reading.FirstLines.Count == 1 ? Dto(reading.FirstLines[0], tableBase) : null,
                Variants = reading.FirstLines.Count > 1
                    ? [.. reading.FirstLines.Select(p => Dto(p, tableBase))]
                    : null,
                Line2 = reading.SecondLine is { } second ? Dto(second, tableBase) : null,
                Line2ClearedAt = reading.SecondLineClearedAt is { } cleared ? PortHex.Format(cleared, 5) : null,
                Banner = reading.Banner is { } banner ? Dto(banner, tableBase) : null,
            });
        }

        AdvisorTableDto dto = new AdvisorTableDto
        {
            Format = AdvisorTableDto.FormatTag,
            About =
                "The in-flight advisor's line table, read out of ai_advisor_message_dispatch @image@0x0EFF1. "
                + "The switch at `switch` range-checks the action code and jumps through the CS-relative "
                + "`jumpTable`; each code's `handler` loads a far pointer into the line-1 pair "
                + "[0xBCBA]/[0xBCBC] and one into the line-2 pair [0xBCBE]/[0xBCC0], then reaches the "
                + "common `tail`. Every pointer shares one segment, whose base is `tableImageBase`; each is "
                + "recorded as its `offset` inside that table, the `image` offset it resolves to, and the "
                + "instructions that load its offset and segment halves (`offsetAt`, `segmentAt`: "
                + "mov word [addr],imm16, operand at +4; for a pushed pointer mov reg16,imm16, operand at "
                + "+1). `variants` replaces `line1` where a handler chooses its first line on run-time "
                + "state, in the order its branches appear; `line2ClearedAt` replaces `line2` where a "
                + "handler zeroes the pair (no second line); `banner` is a pointer a handler also pushes "
                + "to the far call at `pushedTo`. The text is not here: join `image` with "
                + "exe/strings.json. Editing `offset` (and `image` with it) re-points a line; "
                + "--verify re-encodes exactly those immediate words.",
            Dispatcher = PortHex.Format(DispatcherImageOffset, 5),
            Switch = PortHex.Format(SwitchImageOffset, 5),
            JumpTable = PortHex.Format(jumpTable, 5),
            Tail = PortHex.Format(tail, 5),
            TableImageBase = PortHex.Format(tableBase, 5),
            Codes = codes,
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.AdvisorTableDto),
            0,
            $"advisor: {codes.Count} codes, {pointers} string pointers ({variants} first-line variants, "
                + $"{banners} banner)");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        AdvisorTableDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.AdvisorTableDto)
                              ?? throw new InvalidDataException($"{Path} is empty");
        AdvisorTableRules.Validate(dto);

        int tableBase = PortHex.Parse(dto.TableImageBase);
        int segment = ExeAddresses.LoadSegment + (tableBase / 16);
        if (segment > ushort.MaxValue)
        {
            throw new InvalidDataException(
                $"{Path}: tableImageBase {dto.TableImageBase} lies past the last addressable segment");
        }

        List<ExeSlice> slices = new List<ExeSlice>();
        foreach (AdvisorCodeDto code in dto.Codes!)
        {
            string name = $"advisor code {code.Code}";
            if (code.Line1 is { } line1)
            {
                Encode(slices, $"{name} line 1", line1, segment, MemoryMoveOperand);
            }

            List<AdvisorPointerDto> variants = code.Variants ?? [];
            for (int v = 0; v < variants.Count; v++)
            {
                Encode(slices, $"{name} variant {v}", variants[v], segment, MemoryMoveOperand);
            }

            if (code.Line2 is { } line2)
            {
                Encode(slices, $"{name} line 2", line2, segment, MemoryMoveOperand);
            }

            if (code.Banner is { } banner)
            {
                Encode(slices, $"{name} banner", banner, segment, RegisterMoveOperand);
            }
        }

        return slices;
    }

    private static void Encode(List<ExeSlice> slices, string name, AdvisorPointerDto pointer, int segment, int operand)
    {
        int offset = PortHex.Parse(pointer.Offset);
        slices.Add(new ExeSlice($"{name} offset", PortHex.Parse(pointer.OffsetAt) + operand, WordBytes(offset)));
        slices.Add(new ExeSlice($"{name} segment", PortHex.Parse(pointer.SegmentAt) + operand, WordBytes(segment)));
    }

    private static byte[] WordBytes(int value) => [(byte)value, (byte)(value >> 8)];

    private static AdvisorPointerDto Dto(FarPointer pointer, int tableBase) => new()
    {
        Offset = PortHex.Format(pointer.Offset, 3),
        Image = PortHex.Format(tableBase + pointer.Offset, 5),
        OffsetAt = PortHex.Format(pointer.OffsetAt, 5),
        SegmentAt = PortHex.Format(pointer.SegmentAt, 5),
        PushedTo = pointer.PushedTo is { } call ? PortHex.Format(call, 5) : null,
    };

    /// <summary>
    /// Reads the switch: <c>cmp ax,imm16 / ja rel8 / shl ax,1 / xchg bx,ax / jmp word cs:[bx+disp16]</c>.
    /// </summary>
    private static (int Count, int Tail, int JumpTable) ReadSwitch(ReadOnlySpan<byte> image)
    {
        int at = SwitchImageOffset;
        if (at + SwitchLength > image.Length
            || image[at] != 0x3D
            || image[at + 3] != 0x77
            || image[at + 5] != 0xD1 || image[at + 6] != 0xE0
            || image[at + 7] != 0x93
            || image[at + 8] != 0x2E || image[at + 9] != 0xFF || image[at + 10] != 0xA7)
        {
            throw new InvalidDataException(
                $"the advisor dispatcher's switch at image@0x{at:X5} is not "
                    + "cmp ax,imm16 / ja / shl ax,1 / xchg bx,ax / jmp cs:[bx+disp16]: "
                    + (at < image.Length ? Convert.ToHexString(image[at..Math.Min(at + SwitchLength, image.Length)]) : "(past the image)"));
        }

        int count = Word(image, at + 1) + 1;
        int tail = at + 5 + (sbyte)image[at + 4];
        int codeBase = (CodeSegment - ExeAddresses.LoadSegment) * 16;
        int jumpTable = codeBase + Word(image, at + 11);
        if (jumpTable != at + SwitchLength)
        {
            throw new InvalidDataException(
                $"the advisor dispatcher's jump table resolves to image@0x{jumpTable:X5}, not to the bytes "
                    + $"right after its switch (image@0x{at + SwitchLength:X5}); the code segment is not 0x{CodeSegment:X4}");
        }

        if (jumpTable + (count * 2) > image.Length)
        {
            throw new InvalidDataException(
                $"the advisor dispatcher's jump table of {count} entries runs past the image");
        }

        return (count, tail, jumpTable);
    }

    private static void RequireString(ReadOnlySpan<byte> image, int code, FarPointer pointer, int tableBase)
    {
        int at = tableBase + pointer.Offset;
        int end = Math.Max(at, 0);
        while (end < image.Length && image[end] is >= 0x20 and < 0x7F)
        {
            end++;
        }

        if (at < 0 || end == at || end >= image.Length || image[end] != 0)
        {
            throw Malformed(
                code, pointer.OffsetAt,
                $"its pointer resolves to image@0x{at:X5}, which is not a NUL-terminated printable string");
        }
    }

    private static int Word(ReadOnlySpan<byte> image, int at) => image[at] | (image[at + 1] << 8);

    private static InvalidDataException Malformed(int code, int at, string problem) =>
        new($"advisor code {code}: {problem} (image@0x{at:X5})");

    /// <summary>A far pointer a handler loads from two immediate words.</summary>
    private sealed record FarPointer(int Offset, int Segment, int OffsetAt, int SegmentAt, int? PushedTo = null);

    /// <summary>What every path through one handler agrees on.</summary>
    private sealed record HandlerReading(
        IReadOnlyList<FarPointer> FirstLines, FarPointer? SecondLine, int? SecondLineClearedAt, FarPointer? Banner)
    {
        public IEnumerable<FarPointer> Pointers()
        {
            foreach (FarPointer line in FirstLines)
            {
                yield return line;
            }

            if (SecondLine is not null)
            {
                yield return SecondLine;
            }

            if (Banner is not null)
            {
                yield return Banner;
            }
        }
    }

    /// <summary>Where a value on a path came from.</summary>
    private enum Origin
    {
        /// <summary>Nothing the walk can name.</summary>
        Unknown,

        /// <summary>The operand of <c>mov word [addr],imm16</c>.</summary>
        MemoryImmediate,

        /// <summary>The operand of <c>mov reg16,imm16</c>.</summary>
        RegisterImmediate,

        /// <summary><c>sub ax,ax</c>.</summary>
        Cleared,
    }

    /// <summary>A register or memory value on one path, and the instruction that produced it.</summary>
    private readonly record struct Value(int? Known, int At, Origin Origin)
    {
        public static Value Unknown(int at) => new(null, at, Origin.Unknown);
    }

    /// <summary>
    /// Follows every path of one handler over the instruction set the advisor handlers use.
    /// </summary>
    private sealed class HandlerWalker
    {
        private readonly byte[] _image;
        private readonly int _code;
        private readonly int _tail;
        private readonly List<PathEnd> _speaking = [];
        private int _paths;

        private HandlerWalker(byte[] image, int code, int tail)
        {
            _image = image;
            _code = code;
            _tail = tail;
        }

        public static HandlerReading Read(byte[] image, int code, int handler, int tail)
        {
            HandlerWalker walker = new HandlerWalker(image, code, tail);
            walker.Walk(handler, new PathState());
            if (walker._speaking.Count == 0)
            {
                throw Malformed(code, handler, "no path through the handler reaches the dispatcher's tail");
            }

            return walker.Summarise(handler);
        }

        private void Walk(int pc, PathState state)
        {
            int steps = 0;
            while (true)
            {
                if (pc == _tail)
                {
                    _speaking.Add(new PathEnd(state.Writes, state.Banners));
                    return;
                }

                if (!state.Visited.Add(pc))
                {
                    throw Malformed(_code, pc, "a path through the handler loops");
                }

                if (++steps > MaxSteps)
                {
                    throw Malformed(_code, pc, $"a path through the handler is longer than {MaxSteps} instructions");
                }

                if (pc < 0 || pc + 1 > _image.Length)
                {
                    throw Malformed(_code, pc, "a path through the handler leaves the image");
                }

                byte op = _image[pc];
                switch (op)
                {
                    case 0xC7 when Byte(pc + 1) == 0x06:
                        // mov word [addr],imm16
                        state.Writes[Word(pc + 2)] = new Value(Word(pc + 4), pc, Origin.MemoryImmediate);
                        pc += 6;
                        break;
                    case 0xC6 when Byte(pc + 1) == 0x46:
                        // mov byte [bp+disp8],imm8: the handler's local display type
                        pc += 4;
                        break;
                    case 0x2B when Byte(pc + 1) == 0xC0:
                        // sub ax,ax
                        state.Ax = new Value(0, pc, Origin.Cleared);
                        pc += 2;
                        break;
                    case 0x2A when Byte(pc + 1) == 0xC0:
                        // sub al,al: the refusal's return value; ah stays unknown
                        state.Ax = Value.Unknown(pc);
                        pc += 2;
                        break;
                    case 0xA3:
                        // mov [addr],ax
                        state.Writes[Word(pc + 1)] = state.Ax;
                        pc += 3;
                        break;
                    case 0xA1:
                        // mov ax,[addr]
                        state.Ax = Value.Unknown(pc);
                        pc += 3;
                        break;
                    case 0xB8:
                        state.Ax = new Value(Word(pc + 1), pc, Origin.RegisterImmediate);
                        pc += 3;
                        break;
                    case 0xB9:
                        state.Cx = new Value(Word(pc + 1), pc, Origin.RegisterImmediate);
                        pc += 3;
                        break;
                    case 0x50:
                        state.Stack.Add(state.Ax);
                        pc += 1;
                        break;
                    case 0x51:
                        state.Stack.Add(state.Cx);
                        pc += 1;
                        break;
                    case 0x9A:
                        FarCall(pc, state);
                        pc += 5;
                        break;
                    case 0x83 when Byte(pc + 1) == 0x3E:
                        // cmp word [addr],imm8
                        pc += 5;
                        break;
                    case 0xF6 when Byte(pc + 1) == 0x06:
                        // test byte [addr],imm8
                        pc += 5;
                        break;
                    case 0x3D:
                        // cmp ax,imm16
                        pc += 3;
                        break;
                    case 0xE9:
                        pc = pc + 3 + (short)Word(pc + 1);
                        break;
                    case 0xEB:
                        pc = pc + 2 + (sbyte)Byte(pc + 1);
                        break;
                    case >= 0x70 and <= 0x7F:
                        if (++_paths > MaxPaths)
                        {
                            throw Malformed(_code, pc, $"the handler has more than {MaxPaths} paths");
                        }

                        // Fall-through first, so a choice's variants come out in the order they appear.
                        Walk(pc + 2, state.Fork());
                        pc = pc + 2 + (sbyte)Byte(pc + 1);
                        break;
                    case 0x8B when Byte(pc + 1) == 0xE5:
                        // mov sp,bp
                        pc += 2;
                        break;
                    case 0x5D:
                        // pop bp
                        pc += 1;
                        break;
                    case 0xCB:
                        // retf before the tail: the dispatcher refused, and nothing is shown
                        return;
                    default:
                        throw Malformed(
                            _code, pc,
                            $"unexpected instruction bytes {Convert.ToHexString(_image, pc, Math.Min(6, _image.Length - pc))}");
                }
            }
        }

        private void FarCall(int pc, PathState state)
        {
            int target = ExeAddresses.Resolve((ushort)Word(pc + 1), (ushort)Word(pc + 3));
            List<Value> stack = state.Stack;
            if (stack.Count == 0)
            {
                // A call with no pushed arguments (the random gate): it only clobbers the scratch registers.
            }
            else if (stack.Count == 2
                && stack[0] is { Known: { } segment, Origin: Origin.RegisterImmediate } segmentValue
                && stack[1] is { Known: { } offset, Origin: Origin.RegisterImmediate } offsetValue)
            {
                // push cx (segment) / push ax (offset): a far pointer argument.
                state.Banners.Add(new FarPointer(offset, segment, offsetValue.At, segmentValue.At, target));
            }
            else
            {
                throw Malformed(
                    _code, pc,
                    $"a far call receives {stack.Count} pushed word(s) that are not one far pointer loaded from immediates");
            }

            stack.Clear();
            state.Ax = Value.Unknown(pc);
            state.Cx = Value.Unknown(pc);
        }

        private HandlerReading Summarise(int handler)
        {
            List<FarPointer> firstLines = new List<FarPointer>();
            FarPointer? secondLine = null;
            int? clearedAt = null;
            FarPointer? banner = null;
            for (int p = 0; p < _speaking.Count; p++)
            {
                PathEnd end = _speaking[p];
                FarPointer first = Pointer(end, Line1OffsetGlobal, Line1SegmentGlobal, handler, "line 1")
                                   ?? throw Malformed(_code, handler, "a path reaches the tail with line 1 cleared");
                if (!firstLines.Any(f => f.OffsetAt == first.OffsetAt))
                {
                    firstLines.Add(first);
                }

                FarPointer? second = Pointer(end, Line2OffsetGlobal, Line2SegmentGlobal, handler, "line 2");
                int? cleared = second is null ? ClearedAt(end) : null;
                if (p == 0)
                {
                    secondLine = second;
                    clearedAt = cleared;
                }
                else if (second?.OffsetAt != secondLine?.OffsetAt || cleared != clearedAt)
                {
                    throw Malformed(_code, handler, "its paths disagree on line 2");
                }

                if (end.Banners.Count > 1)
                {
                    throw Malformed(_code, handler, "a path pushes more than one string to far calls");
                }

                if (end.Banners.Count == 1)
                {
                    FarPointer pushed = end.Banners[0];
                    if (banner is not null && banner.OffsetAt != pushed.OffsetAt)
                    {
                        throw Malformed(_code, handler, "its paths push different strings to far calls");
                    }

                    banner = pushed;
                }
            }

            return new HandlerReading(firstLines, secondLine, clearedAt, banner);
        }

        /// <summary>
        /// The far pointer a path leaves in a pair, or null when it cleared both halves; anything else
        /// is not a handler shape this reader knows.
        /// </summary>
        private FarPointer? Pointer(PathEnd end, int offsetGlobal, int segmentGlobal, int handler, string line)
        {
            if (!end.Writes.TryGetValue(offsetGlobal, out Value offset)
                || !end.Writes.TryGetValue(segmentGlobal, out Value segment))
            {
                throw Malformed(_code, handler, $"a path reaches the tail without loading {line}");
            }

            if (offset is { Origin: Origin.MemoryImmediate, Known: { } o }
                && segment is { Origin: Origin.MemoryImmediate, Known: { } s })
            {
                return new FarPointer(o, s, offset.At, segment.At);
            }

            if (offset is { Origin: Origin.Cleared, Known: 0 } && segment is { Origin: Origin.Cleared, Known: 0 }
                && offset.At == segment.At)
            {
                return null;
            }

            throw Malformed(
                _code, handler,
                $"a path loads {line} from something other than two mov-immediates or one cleared register");
        }

        private static int? ClearedAt(PathEnd end) => end.Writes[Line2OffsetGlobal].At;

        private byte Byte(int at) =>
            at < _image.Length ? _image[at] : throw Malformed(_code, at, "a path through the handler leaves the image");

        private int Word(int at) => Byte(at) | (Byte(at + 1) << 8);

        /// <summary>What one path left behind when it reached the tail.</summary>
        private sealed record PathEnd(IReadOnlyDictionary<int, Value> Writes, IReadOnlyList<FarPointer> Banners);

        private sealed class PathState
        {
            public Dictionary<int, Value> Writes { get; private init; } = [];

            public List<FarPointer> Banners { get; private init; } = [];

            public List<Value> Stack { get; private init; } = [];

            public HashSet<int> Visited { get; private init; } = [];

            public Value Ax { get; set; } = Value.Unknown(-1);

            public Value Cx { get; set; } = Value.Unknown(-1);

            public PathState Fork() => new()
            {
                Writes = new Dictionary<int, Value>(Writes),
                Banners = [.. Banners],
                Stack = [.. Stack],
                Visited = [.. Visited],
                Ax = Ax,
                Cx = Cx,
            };
        }
    }
}
