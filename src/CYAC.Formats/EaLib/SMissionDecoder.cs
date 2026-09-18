using System.Text;

namespace CYAC.Formats.EaLib;

// ---------------------------------------------------------------------------
// .S mission-module / .W campaign-world container (shared magic 0x06206319).
//
// C# PORT of — parser + BYTE-EXACT re-emitter. The grammar is a
// transcription of the game's own tag-byte interpreter `wld_or_s_asset_parser
// @image@0x096F2`; every consumption rule below carries the image@ citation of
// the instruction(s) that consume those stream bytes. Findings: (layout,
// censuses) and (the 5-slot module ABI).
//
// TOP-LEVEL LAYOUT (as consumed, in stream order)
//   +0x00  u16 magic_lo = 0x6319            image@0x09745 (cmp ax,0x6319)
//   +0x02  u16 magic_hi = 0x0620            image@0x0974D (cmp ax,0x620)
//   +0x04  u16 table_off  — read & DISCARDED by the tag parser (image@0x0975A);
//          it is the FILE offset of the trailer block's u16 size word (the
//          module/export-table pointer used by s_asset_briefing_text_extract
//          @image@0x0966B).  0x0000 in all 3 .W (no module).
//   +0x06  u16 sec1_count — sizes the 11-B/slot section-1 buffer (image@0x0975D,
//          x11 @image@0x0976F); NOT a loop bound (section 1 ends on 0xFF).
//   +0x08  SECTION 1 record stream, 0xFF-terminated   loop @image@0x097AE
//          SECTION 2 header: u16 name_count (image@0x09934) + u16 names_len
//            (image@0x0993B), name_count NUL-strings, mandatory 0xFF
//            (image@0x099B4)
//          SECTION 2 tag stream: directives + objects, 0xFF-terminated
//            (image@0x09A34)
//          TRAILER mini-stream (image@0x0A350..0x0A376):
//            0x00 -> u16 size N (image@0x0A310) + N raw bytes = the mission
//                    module (5 u16 export offsets relative to block start, then
//                    x86 code/data; far-heap copied to [0xFB8]/[0xFBA])
//            0xFF -> end of file
//            else -> byte ignored, keep reading (image@0x0A373)
//   (the game parser has NO EOF check; residue after the trailer 0xFF would be
//    silently ignored — measured: no shipped file has any)
//
// STREAM PRIMITIVES (helpers, seg 0x108E)
//   read_byte   @image@0x0A3EA
//   read_word   @image@0x0A3F6  (LE u16)
//   read_3bytes @image@0x0A403  -> i32 = (b0<<8)|(b1<<16)|(b2<<24), signed:
//                                 a 16.8 fixed-point world coordinate whose
//                                 fraction byte is always 0 ("i24" below).
// ---------------------------------------------------------------------------
public static class SMissionDecoder
{
    /// <summary>u16 0x6319, u16 0x0620 — image@0x09745 / image@0x0974D.</summary>
    public static ReadOnlySpan<byte> Magic => new byte[] { 0x19, 0x63, 0x20, 0x06 };

    /// <summary>
    /// The briefing-text destination buffer, in bytes.
    /// <c>s_asset_briefing_text_extract</c> image@0x0966B allocates it once —
    /// image@0x09671 <c>mov ax, 0x04B0</c> → far_heap_alloc_or_abort — then far-calls
    /// the module's slot-0 export (image@0x096DE) with that segment, and slot 0
    /// copies its briefing string into <c>result_seg:0</c>.  The engine performs NO
    /// bound check, so a module whose briefing text exceeds 1200 B writes past a
    /// far-heap block.  Every shipping briefing is well under it.
    /// </summary>
    public const int BriefingBufferBytes = 0x4B0;

    public sealed class ParseException : Exception
    {
        public ParseException(string msg) : base(msg) { }
    }

    // ---- operand kinds ---------------------------------------------------

    public enum OperandKind
    {
        None,      // tag only
        Byte,      // read_byte
        Word,      // read_word
        Word3,     // 3 x read_word (attr 0x99)
        Coords4,   // 4 x read_3bytes (directive 0x88)
        String,    // NUL-terminated (NUL included in Payload)
        Script,    // u16 N + N raw bytes (attr 0x89)
    }

    /// <summary>
    /// Header-level "directive" tags — they loop straight back to the tag
    /// reader without opening an object (cascade image@0x099D0..0x09A3C).
    /// </summary>
    public static readonly IReadOnlyDictionary<byte, OperandKind> DirectiveOperands =
        new Dictionary<byte, OperandKind>
        {
            [0x81] = OperandKind.Byte,     // 1 byte, discarded          image@0x09A4F
            [0x82] = OperandKind.Byte,     // 1 byte, discarded          image@0x09A4F
            [0x83] = OperandKind.Byte,     // subtype_max -> [0xB54A]    image@0x09A55
            [0x84] = OperandKind.Byte,     // era filter -> [0xEE32]     image@0x09A68
            [0x88] = OperandKind.Coords4,  // world extents [0xF0E6..]   image@0x09A78
            [0x89] = OperandKind.None,     // [0xEDE1] = 0               image@0x09AA3
            [0x8A] = OperandKind.None,     // [0xF0E4] = 1               image@0x09AAB
            [0x95] = OperandKind.Byte,     // player aircraft -> [0xC31A] image@0x09ADD
            [0x9A] = OperandKind.Word,     // -> [0xF100]                image@0x09AB3
            [0x9D] = OperandKind.None,     // [0xF0E5] = 0               image@0x09ABC
            [0x9E] = OperandKind.Byte,     // ealib lookup -> [0xF1C6]   image@0x09AC4
        };

    /// <summary>Object-opener tags with special stream handling.</summary>
    public static readonly IReadOnlySet<byte> OpenerTags =
        new HashSet<byte> { 0x02, 0x03, 0x04, 0x05, 0x19 };

    /// <summary>Object attribute tags (inner loop image@0x09B58).</summary>
    public static readonly IReadOnlyDictionary<byte, OperandKind> AttrOperands =
        new Dictionary<byte, OperandKind>
        {
            [0x80] = OperandKind.Word,    // image@0x09B62
            [0x99] = OperandKind.Word3,   // image@0x09BB5..0x09BC3
            [0x84] = OperandKind.Byte,    // era compare vs [0xEE32]  image@0x09BD2
            [0x87] = OperandKind.None,    // deferred flag            image@0x09BEC
            [0x8F] = OperandKind.None,    // clear 0x40               image@0x09BFA
            [0x90] = OperandKind.None,    // priority class tag+0x70  image@0x09C0E
            [0x91] = OperandKind.None,
            [0x92] = OperandKind.None,
            [0x93] = OperandKind.None,
            [0x97] = OperandKind.String,  // interned name            image@0x09C20
            [0x94] = OperandKind.None,    // image@0x09C5A
            [0x9B] = OperandKind.None,    // image@0x09C68
            [0x9F] = OperandKind.None,    // image@0x09C76
            [0x96] = OperandKind.Byte,    // hp/skill                 image@0x09C84
            [0x98] = OperandKind.Word,    // spawn heading -> [bp-0x44] image@0x09C94
            [0x9C] = OperandKind.Word,    // image@0x09CA0
            [0x8A] = OperandKind.None,    // waypoint flag bits image@0x09CB0..0x09CE4
            [0x8B] = OperandKind.None,
            [0x8C] = OperandKind.None,
            [0x8D] = OperandKind.None,
            [0x8E] = OperandKind.None,
            [0x86] = OperandKind.Byte,    // image@0x09CF1
            [0x88] = OperandKind.Byte,    // image@0x09D03
            [0x89] = OperandKind.Script,  // AUTHORED AI SCRIPT       image@0x09D14
        };

    // ---- human-readable tag names (P3 §2 / §5; unnamed tags stay generic) --

    public static string DirectiveName(byte tag) => tag switch
    {
        0x81 => "0x81 unused-byte (discarded)",
        0x82 => "0x82 unused-byte (discarded)",
        0x83 => "0x83 subtype_max -> [0xB54A]",
        0x84 => "0x84 era filter -> [0xEE32]",
        0x88 => "0x88 world extents -> [0xF0E6..0xF0F5]",
        0x89 => "0x89 clear [0xEDE1]",
        0x8A => "0x8A set [0xF0E4]=1",
        0x95 => "0x95 player aircraft -> [0xC31A]",
        0x9A => "0x9A -> [0xF100] (initial altitude? hypothesis)",
        0x9D => "0x9D clear [0xF0E5]",
        0x9E => "0x9E opponent class -> ealib lookup -> [0xF1C6]",
        _ => $"0x{tag:X2} directive",
    };

    public static string AttrName(byte tag) => tag switch
    {
        0x80 => "0x80 scalar word",
        0x84 => "0x84 era filter (DORMANT in shipped data)",
        0x86 => "0x86 byte",
        0x87 => "0x87 deferred flag",
        0x88 => "0x88 byte",
        0x89 => "0x89 AI SCRIPT",
        0x8A => "0x8A waypoint flag",
        0x8B => "0x8B waypoint flag",
        0x8C => "0x8C waypoint flag (DORMANT)",
        0x8D => "0x8D waypoint flag",
        0x8E => "0x8E waypoint flag",
        0x8F => "0x8F clear flag 0x40",
        0x90 => "0x90 priority class 0",
        0x91 => "0x91 priority class 1",
        0x92 => "0x92 priority class 2",
        0x93 => "0x93 priority class 3",
        0x94 => "0x94 flag",
        0x96 => "0x96 hp/skill byte",
        0x97 => "0x97 interned name",
        0x98 => "0x98 spawn heading override",
        0x99 => "0x99 3 words",
        0x9B => "0x9B flag",
        0x9C => "0x9C word",
        0x9F => "0x9F flag",
        _ => $"0x{tag:X2} attr",
    };

    public static string OpenerName(byte tag) => tag switch
    {
        0x02 => "0x02 placement (no operand)",
        0x03 => "0x03 named mesh (label; kind 0xFFFD — DORMANT)",
        0x04 => "0x04 labelled site (byte + label; kind 0xFFFC)",
        0x05 => "0x05 kind 0xFFFB",
        0x19 => "0x19 prim class 0x4D00",
        _ => $"class 0x{tag:X2} (ealib lookup lcall 0x32AA:0x15B8)",
    };

    public static string PositionName(byte tag) => tag switch
    {
        0 => "pos 0: absolute x,y,z (3 x i24)",
        1 => "pos 1: ground plane x,z (2 x i24; y forced 0)",
        2 => "pos 2: .W site by section-1 record TYPE (1 byte)",
        3 => "pos 3: [3 eras][subtype_max] coord matrix (DORMANT)",
        4 => "pos 4: relative to origin [0xB556] (3 x i24)",
        5 => "pos 5: relative to mission spawn [0xEE34] (3 x i24, DORMANT)",
        6 => "pos 6: named-place slot + offset (byte + 3 x i24)",
        7 => "pos 7: compact x,y,z (3 bytes)",
        _ => $"pos {tag}: UNKNOWN",
    };

    // ---- model -----------------------------------------------------------

    /// <summary>
    /// Section-1 record: type byte + 2 x i24 coords (+ subtype byte for
    /// types 1..7).  Types 2 and 6 also call landing_zone_entry_add @0x090AE
    /// with flags 0x40 / 0x00 (image@0x098C9 / image@0x098A7).
    /// </summary>
    public sealed record Sec1Record(byte Type, int CoordA, int CoordB, byte? Subtype);

    public sealed record Directive(byte Tag, OperandKind Kind, int Scalar, int[]? Coords)
    {
        public string Describe() => Kind switch
        {
            OperandKind.None => DirectiveName(Tag),
            OperandKind.Coords4 => $"{DirectiveName(Tag)} = [{string.Join(", ", Coords!)}]",
            _ => $"{DirectiveName(Tag)} = {Scalar} (0x{Scalar:X})",
        };
    }

    public sealed record Attr(byte Tag, OperandKind Kind, int Scalar, int[]? Words, byte[]? Payload)
    {
        /// <summary>attr 0x97 / opener label text, NUL stripped.</summary>
        public string Text => Payload is null ? "" : CStr(Payload);

        public string Describe() => Kind switch
        {
            OperandKind.None => AttrName(Tag),
            OperandKind.Word3 => $"{AttrName(Tag)} = [{string.Join(", ", Words!)}]",
            OperandKind.String => $"{AttrName(Tag)} = \"{Text}\"",
            OperandKind.Script => $"{AttrName(Tag)} — {Payload!.Length} B",
            _ => $"{AttrName(Tag)} = {Scalar} (0x{Scalar:X})",
        };
    }

    /// <summary>
    /// The position tag CLOSES an object (cascade image@0x09D70..0x09F5D).
    /// Coords holds every i24 the tag consumed, in stream order; ByteArg holds
    /// the leading byte of tags 2 and 6; Compact holds tag 7's three bytes.
    /// </summary>
    public sealed record Position(byte Tag, byte ByteArg, int[] Coords, byte[]? Compact)
    {
        public string Describe()
        {
            string body = Tag switch
            {
                2 => $"type={ByteArg}",
                6 => $"place={ByteArg} + [{string.Join(", ", Coords)}]",
                7 => $"[{string.Join(", ", Compact!)}]",
                3 => $"{Coords.Length / 3} triples",
                _ => $"[{string.Join(", ", Coords)}]",
            };
            return $"{PositionName(Tag)}  {body}";
        }
    }

    public sealed record SObject(
        byte OpenerTag,
        byte? OpenerByte,
        byte[]? OpenerNameBytes,
        List<Attr> Attrs,
        Position Pos)
    {
        public string Label => OpenerNameBytes is null ? "" : CStr(OpenerNameBytes);

        /// <summary>attr 0x97 interned name, if present (pilot / nose art).</summary>
        public string? InternedName =>
            Attrs.FirstOrDefault(a => a.Tag == 0x97)?.Text;

        public IEnumerable<Attr> Scripts => Attrs.Where(a => a.Tag == 0x89);
    }

    public sealed record TrailerItem(bool IsBlock, byte SkipByte, byte[]? Payload);

    /// <summary>
    /// The trailer 0x00-block: 5 u16 export offsets (relative to block start —
    /// the size word is already consumed, so block+2k = export slot k) followed
    /// by position-independent x86 code and module data.
    /// </summary>
    public sealed class ModuleBlock
    {
        public required byte[] Bytes { get; init; }
        public required int FileOffset { get; init; }     // file offset of block byte 0
        public required int[] ExportOffsets { get; init; } // 5 entries, block-relative

        /// <summary>P4 §2 slot names (the 5-slot ABI).</summary>
        public static readonly string[] SlotNames =
        {
            "get_briefing_text",   // 0 — s_asset_briefing_text_extract @image@0x0966B
            "on_slot_destroyed",   // 1 — wrapper @image@0x08C72
            "script_hook",         // 2 — wrapper @image@0x08CAB (AI opcode 0xE0)
            "check_win_condition", // 3 — combat_vtable_slot_dispatch @image@0x08BF0
            "get_debrief_text",    // 4 — ui_post_death_message @image@0x25BAF
        };

        /// <summary>
        /// Upper bound on a slot's extent: distance to the next-higher export
        /// offset (or the block end).  NOT the code size — module data (message
        /// / debrief strings) is interleaved between functions.
        /// </summary>
        public int ExtentTo(int slot)
        {
            int off = ExportOffsets[slot];
            int next = Bytes.Length;
            foreach (int o in ExportOffsets)
                if (o > off && o < next) next = o;
            return next - off;
        }

        /// <summary>True iff the slot starts with the MSC far prologue 55 8B EC.</summary>
        public bool HasMscPrologue(int slot)
        {
            int o = ExportOffsets[slot];
            return o + 3 <= Bytes.Length
                && Bytes[o] == 0x55 && Bytes[o + 1] == 0x8B && Bytes[o + 2] == 0xEC;
        }

        /// <summary>
        /// Independent (fixture-free) recovery of slot 0's briefing copy, the
        /// way func0 does it: `mov si,imm16; mov cx,imm16; les di,[bp+6]; rep
        /// movsb` (P4 §5.1 — 50/51 modules; FREE.S's slot 0 is a no-op). Scans
        /// the slot-0 body up to the `rep movsb` and returns the last `mov si`
        /// / `mov cx` immediates before it.
        /// </summary>
        public bool TryGetBriefingCopy(out int src, out int len)
        {
            src = len = 0;
            int start = ExportOffsets[0];
            int limit = Math.Min(Bytes.Length - 1, start + 64);
            int si = -1, cx = -1;
            for (int p = start; p < limit; p++)
            {
                if (Bytes[p] == 0xF3 && Bytes[p + 1] == 0xA4)   // rep movsb
                {
                    if (si < 0 || cx < 0) return false;
                    if (si + cx > Bytes.Length) return false;
                    src = si; len = cx;
                    return true;
                }
                if (Bytes[p] == 0xBE && p + 2 < Bytes.Length)    // mov si, imm16
                { si = Bytes[p + 1] | (Bytes[p + 2] << 8); p += 2; }
                else if (Bytes[p] == 0xB9 && p + 2 < Bytes.Length) // mov cx, imm16
                { cx = Bytes[p + 1] | (Bytes[p + 2] << 8); p += 2; }
                else if (Bytes[p] == 0xCB) return false;          // retf before movsb
            }
            return false;
        }

        /// <summary>Latin-1 text of a module (src,len) copy, e.g. the briefing.</summary>
        public string TextAt(int src, int len)
        {
            if (src < 0 || len < 0 || src + len > Bytes.Length) return "";
            return Encoding.Latin1.GetString(Bytes, src, len);
        }
    }

    public sealed class SFile
    {
        public required string Name { get; init; }
        public required byte[] Body { get; init; }
        public int Size => Body.Length;
        public required int TableOff { get; init; }     // header u16 @+4 (0 in .W)
        public required int Sec1Count { get; init; }    // header u16 @+6
        public required List<Sec1Record> Sec1Records { get; init; }
        public required int NameCount { get; init; }
        public required int NamesLen { get; init; }
        public required List<byte[]> Names { get; init; }   // NUL included
        public required List<object> Items { get; init; }   // Directive | SObject, stream order
        public required List<TrailerItem> Trailer { get; init; }
        public required Dictionary<string, int> Accounting { get; init; }
        public required List<string> Notes { get; init; }

        public bool IsWorld => TableOff == 0 && Module is null;

        public IEnumerable<SObject> Objects => Items.OfType<SObject>();
        public IEnumerable<Directive> Directives => Items.OfType<Directive>();
        public IEnumerable<Attr> AllScripts => Objects.SelectMany(o => o.Scripts);

        /// <summary>The first (and, in shipped data, only) trailer 0x00-block.</summary>
        public ModuleBlock? Module { get; set; }

        /// <summary>Rolled-up byte accounting.</summary>
        public Dictionary<string, int> Rollup()
        {
            Dictionary<string, int> roll = new Dictionary<string, int>();
            foreach ((string k, int v) in Accounting)
            {
                string c = Rollups.TryGetValue(k, out string? r) ? r : k;
                roll[c] = roll.TryGetValue(c, out int old) ? old + v : v;
            }
            return roll;
        }
    }

    /// <summary>Fine accounting class -> the 8 P3 §4 report classes.</summary>
    public static readonly IReadOnlyDictionary<string, string> Rollups =
        new Dictionary<string, string>
        {
            ["header"] = "header",
            ["sec1_records"] = "section1",
            ["sec1_term"] = "section1",
            ["sec2_header"] = "tag_stream",
            ["names"] = "strings",
            ["names_term"] = "tag_stream",
            ["tag_stream"] = "tag_stream",
            ["obj_string"] = "strings",
            ["ai_script"] = "ai_script",
            ["sec2_term"] = "tag_stream",
            ["trailer_frame"] = "tag_stream",
            ["export_table"] = "export_table",
            ["x86_code"] = "x86_code",
            ["unaccounted"] = "unaccounted",
        };

    // ---- reader with byte accounting (mirrors s_parse.Reader) -------------

    private sealed class Reader
    {
        private readonly byte[] _d;
        public int Pos;
        public readonly Dictionary<string, int> Acct = new();
        private int _mark;

        public Reader(byte[] data) { _d = data; }
        public int Length => _d.Length;

        private ReadOnlySpan<byte> Take(int n)
        {
            if (Pos + n > _d.Length)
                throw new ParseException($"stream overrun at 0x{Pos:X} (+{n})");
            Span<byte> s = _d.AsSpan(Pos, n);
            Pos += n;
            return s;
        }

        public byte U8() => Take(1)[0];

        public int U16() { ReadOnlySpan<byte> s = Take(2); return s[0] | (s[1] << 8); }

        /// <summary>read_3bytes @image@0x0A403: (b0&lt;&lt;8)|(b1&lt;&lt;16)|(b2&lt;&lt;24), signed.</summary>
        public int I24()
        {
            ReadOnlySpan<byte> s = Take(3);
            return unchecked((int)(((uint)s[0] << 8) | ((uint)s[1] << 16) | ((uint)s[2] << 24)));
        }

        /// <summary>NUL-terminated string, NUL INCLUDED (read_byte loop until AL==0).</summary>
        public byte[] CStrBytes()
        {
            int start = Pos;
            while (true)
            {
                if (Pos >= _d.Length)
                    throw new ParseException($"unterminated string at 0x{start:X}");
                if (_d[Pos] == 0) { Pos++; return _d[start..Pos]; }
                Pos++;
            }
        }

        public byte[] Raw(int n) => Take(n).ToArray();

        public void Mark() => _mark = Pos;

        public void Account(string cls)
        {
            Acct[cls] = (Acct.TryGetValue(cls, out int v) ? v : 0) + (Pos - _mark);
            _mark = Pos;
        }

        public void Add(string cls, int n) =>
            Acct[cls] = (Acct.TryGetValue(cls, out int v) ? v : 0) + n;

        public void SyncMark() => _mark = Pos;
    }

    // ---- parser ----------------------------------------------------------

    public static SFile Parse(byte[] data, string name = "?")
    {
        Reader r = new Reader(data);
        List<string> notes = new List<string>();

        // ---- header (8 bytes) --------------------------------------------
        r.Mark();
        if (data.Length < 8 || !data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new ParseException("bad magic (want 19 63 20 06)");
        r.Raw(4);
        int tableOff = r.U16();      // image@0x0975A — discarded by the tag parser
        int sec1Count = r.U16();     // image@0x0975D
        r.Account("header");

        // ---- section 1 ----------------------------------------------------
        List<Sec1Record> sec1 = new List<Sec1Record>();
        while (true)
        {
            r.Mark();
            byte t = r.U8();
            if (t == 0xFF) { r.Account("sec1_term"); break; }   // image@0x097C9
            if (t == 0x00 || t == 0x08)                          // image@0x097B9 / 0x097C7
            {
                int a = r.I24(), b = r.I24();
                sec1.Add(new Sec1Record(t, a, b, null));
            }
            else if (t >= 1 && t <= 7)                           // image@0x097BE..0x097C3
            {
                int a = r.I24(), b = r.I24();
                byte st = r.U8();                                // image@0x09886
                sec1.Add(new Sec1Record(t, a, b, st));
            }
            else
            {
                throw new ParseException(
                    $"section-1 record type 0x{t:X2} at 0x{r.Pos - 1:X} -> abort path @0x9752");
            }
            r.Account("sec1_records");
        }
        if (sec1.Count != sec1Count)
            notes.Add($"sec1_count {sec1Count} != actual record count {sec1.Count}");

        // ---- section 2 header + name table --------------------------------
        r.Mark();
        int nameCount = r.U16();     // image@0x09934
        int namesLen = r.U16();      // image@0x0993B
        r.Account("sec2_header");
        List<byte[]> names = new List<byte[]>();
        r.Mark();
        for (int i = 0; i < nameCount; i++) names.Add(r.CStrBytes());  // image@0x09983
        r.Account("names");
        if (names.Sum(n => n.Length) != namesLen)
            notes.Add($"names_len {namesLen} != actual names bytes {names.Sum(n => n.Length)}");
        r.Mark();
        if (r.U8() != 0xFF) throw new ParseException("missing 0xFF after name table"); // image@0x099B4
        r.Account("names_term");

        // ---- section 2 object / directive stream --------------------------
        List<object> items = new List<object>();
        int? subtypeMax = null;      // [0xB54A], set by directive 0x83
        while (true)
        {
            r.Mark();
            byte tag = r.U8();
            if (tag == 0xFF) { r.Account("sec2_term"); break; }         // image@0x09A34
            if (tag == 0x80)
                throw new ParseException("header tag 0x80 -> abort path @0x9752"); // image@0x099F4

            if (DirectiveOperands.TryGetValue(tag, out OperandKind dkind) && !OpenerTags.Contains(tag))
            {
                int scalar = 0; int[]? coords = null;
                switch (dkind)
                {
                    case OperandKind.Byte:
                        scalar = r.U8();
                        if (tag == 0x83) subtypeMax = scalar;           // image@0x09A5A
                        break;
                    case OperandKind.Word: scalar = r.U16(); break;
                    case OperandKind.Coords4:
                        coords = new int[4];
                        for (int i = 0; i < 4; i++) coords[i] = r.I24(); // image@0x09A78..0x09A9C
                        break;
                }
                r.Account("tag_stream");
                items.Add(new Directive(tag, dkind, scalar, coords));
                continue;
            }

            // ---- object opener ----
            byte? openerByte = null;
            byte[]? openerName = null;
            if (tag == 0x03)                                            // image@0x09AE3
            {
                r.Account("tag_stream"); r.Mark();
                openerName = r.CStrBytes();
                r.Account("obj_string");
            }
            else if (tag == 0x04)                                       // image@0x09B6B
            {
                openerByte = r.U8();
                r.Account("tag_stream"); r.Mark();
                openerName = r.CStrBytes();
                r.Account("obj_string");
            }
            else
            {
                // 0x02 / 0x05 / 0x19 / class-code default: no operand
                r.Account("tag_stream");
            }

            // ---- attribute loop (image@0x09B58) ----
            List<Attr> attrs = new List<Attr>();
            Position pos;
            while (true)
            {
                r.Mark();
                byte a = r.U8();
                if (AttrOperands.TryGetValue(a, out OperandKind akind))
                {
                    switch (akind)
                    {
                        case OperandKind.Byte:
                            attrs.Add(new Attr(a, akind, r.U8(), null, null));
                            r.Account("tag_stream");
                            continue;
                        case OperandKind.Word:
                            attrs.Add(new Attr(a, akind, r.U16(), null, null));
                            r.Account("tag_stream");
                            continue;
                        case OperandKind.Word3:
                        {
                            int[] w = new int[3];
                            for (int i = 0; i < 3; i++) w[i] = r.U16();
                            attrs.Add(new Attr(a, akind, 0, w, null));
                            r.Account("tag_stream");
                            continue;
                        }
                        case OperandKind.None:
                            attrs.Add(new Attr(a, akind, 0, null, null));
                            r.Account("tag_stream");
                            continue;
                        case OperandKind.String:
                            r.Account("tag_stream"); r.Mark();
                            attrs.Add(new Attr(a, akind, 0, null, r.CStrBytes()));
                            r.Account("obj_string");
                            continue;
                        case OperandKind.Script:
                        {
                            int n = r.U16();                            // image@0x09D14
                            r.Account("tag_stream");
                            r.Mark();
                            attrs.Add(new Attr(a, akind, 0, null, r.Raw(n))); // image@0x09D2D
                            r.Account("ai_script");
                            continue;
                        }
                    }
                }

                // ---- position tag closes the object ----
                switch (a)
                {
                    case 0:
                        pos = new Position(a, 0, Coords(r, 3), null);
                        break;
                    case 1:
                        // x then z; y forced 0 (image@0x09DA9 + shared z @0x09D8C)
                        pos = new Position(a, 0, Coords(r, 2), null);
                        break;
                    case 2:
                        pos = new Position(a, r.U8(), Array.Empty<int>(), null); // image@0x09F60
                        break;
                    case 3:
                    {
                        if (subtypeMax is null)
                            throw new ParseException(
                                "position tag 3 with no prior directive 0x83 (subtype_max unset)");
                        pos = new Position(a, 0, Coords(r, 3 * subtypeMax.Value * 3), null);
                        break;
                    }
                    case 4:
                    case 5:
                        pos = new Position(a, 0, Coords(r, 3), null);
                        break;
                    case 6:
                    {
                        byte slot = r.U8();
                        pos = new Position(a, slot, Coords(r, 3), null);
                        break;
                    }
                    case 7:
                        pos = new Position(a, 0, Array.Empty<int>(),
                                           new[] { r.U8(), r.U8(), r.U8() });
                        break;
                    default:
                        throw new ParseException(
                            $"unknown attr/position tag 0x{a:X2} at 0x{r.Pos - 1:X} -> abort @0x9752");
                }
                r.Account("tag_stream");
                break;
            }
            items.Add(new SObject(tag, openerByte, openerName, attrs, pos));
        }

        // ---- trailer (image@0x0A350..0x0A376) -----------------------------
        List<TrailerItem> trailer = new List<TrailerItem>();
        ModuleBlock? module = null;
        while (true)
        {
            r.Mark();
            byte t = r.U8();
            if (t == 0xFF) { r.Account("trailer_frame"); break; }
            if (t == 0x00)                                              // image@0x0A310
            {
                int n = r.U16();
                r.Account("trailer_frame");
                int blockDataOff = r.Pos;
                r.Mark();
                byte[] payload = r.Raw(n);
                if (n >= 10)
                {
                    // size word + 5 u16 offsets are the export table; the rest is
                    // x86 code + module data.
                    r.Add("export_table", 2 + 10);
                    r.Add("trailer_frame", -2);   // move the size word out of frame class
                    r.Add("x86_code", n - 10);
                    r.SyncMark();                 // consumed manually
                    if (tableOff != 0 && tableOff != blockDataOff - 2)
                        notes.Add($"header table_off 0x{tableOff:X} != block size-word offset " +
                                  $"0x{blockDataOff - 2:X}");
                    int[] offs = new int[5];
                    for (int i = 0; i < 5; i++) offs[i] = payload[i * 2] | (payload[i * 2 + 1] << 8);
                    for (int i = 0; i < 5; i++)
                    {
                        if (offs[i] + 3 > n)
                            notes.Add($"export fn{i} offset 0x{offs[i]:X} outside block (n=0x{n:X})");
                        else if (!(payload[offs[i]] == 0x55 && payload[offs[i] + 1] == 0x8B
                                   && payload[offs[i] + 2] == 0xEC))
                            notes.Add($"export fn{i} @block+0x{offs[i]:X} lacks MSC prologue 55 8B EC");
                    }
                    module ??= new ModuleBlock
                    {
                        Bytes = payload,
                        FileOffset = blockDataOff,
                        ExportOffsets = offs,
                    };
                }
                else
                {
                    r.Account("x86_code");
                    notes.Add($"trailer block too small for an export table (n={n})");
                }
                trailer.Add(new TrailerItem(true, 0, payload));
            }
            else
            {
                r.Account("trailer_frame");                             // image@0x0A373
                trailer.Add(new TrailerItem(false, t, null));
            }
        }

        // ---- residue ------------------------------------------------------
        if (r.Pos != data.Length)
        {
            r.Add("unaccounted", data.Length - r.Pos);
            notes.Add($"{data.Length - r.Pos} residue bytes after trailer 0xFF " +
                      "(game would ignore them)");
        }
        if (r.Acct.Values.Sum() != data.Length)
            throw new ParseException(
                $"byte accounting is not total ({r.Acct.Values.Sum()} != {data.Length})");

        return new SFile
        {
            Name = name,
            Body = data,
            TableOff = tableOff,
            Sec1Count = sec1Count,
            Sec1Records = sec1,
            NameCount = nameCount,
            NamesLen = namesLen,
            Names = names,
            Items = items,
            Trailer = trailer,
            Accounting = r.Acct,
            Notes = notes,
            Module = module,
        };
    }

    private static int[] Coords(Reader r, int n)
    {
        int[] c = new int[n];
        for (int i = 0; i < n; i++) c[i] = r.I24();
        return c;
    }

    // ---- re-emitter (inverse of Parse; byte-exact by construction) --------

    /// <summary>
    /// Inverse of read_3bytes: the low byte must be 0 (holds for all 54
    /// shipped files — the fraction byte of the 16.8 world coordinate).
    /// </summary>
    private static void EmitI24(List<byte> o, int v)
    {
        uint u = unchecked((uint)v);
        if ((u & 0xFF) != 0)
            throw new ParseException($"i24 value 0x{u:X8} has nonzero low byte — not representable");
        o.Add((byte)((u >> 8) & 0xFF));
        o.Add((byte)((u >> 16) & 0xFF));
        o.Add((byte)((u >> 24) & 0xFF));
    }

    private static void EmitU16(List<byte> o, int v)
    {
        o.Add((byte)(v & 0xFF));
        o.Add((byte)((v >> 8) & 0xFF));
    }

    public static byte[] ToBytes(SFile f)
    {
        List<byte> o = new List<byte>(f.Size);
        o.AddRange(Magic);
        EmitU16(o, f.TableOff);
        EmitU16(o, f.Sec1Count);

        foreach (Sec1Record rec in f.Sec1Records)
        {
            o.Add(rec.Type);
            EmitI24(o, rec.CoordA);
            EmitI24(o, rec.CoordB);
            if (rec.Type >= 1 && rec.Type <= 7) o.Add(rec.Subtype!.Value);
        }
        o.Add(0xFF);

        EmitU16(o, f.NameCount);
        EmitU16(o, f.NamesLen);
        foreach (byte[] n in f.Names) o.AddRange(n);
        o.Add(0xFF);

        foreach (object item in f.Items)
        {
            if (item is Directive d)
            {
                o.Add(d.Tag);
                switch (d.Kind)
                {
                    case OperandKind.Byte: o.Add((byte)d.Scalar); break;
                    case OperandKind.Word: EmitU16(o, d.Scalar); break;
                    case OperandKind.Coords4:
                        foreach (int v in d.Coords!) EmitI24(o, v);
                        break;
                }
                continue;
            }

            SObject obj = (SObject)item;
            o.Add(obj.OpenerTag);
            if (obj.OpenerTag == 0x04) o.Add(obj.OpenerByte!.Value);
            if (obj.OpenerNameBytes is not null) o.AddRange(obj.OpenerNameBytes);

            foreach (Attr a in obj.Attrs)
            {
                o.Add(a.Tag);
                switch (a.Kind)
                {
                    case OperandKind.Byte: o.Add((byte)a.Scalar); break;
                    case OperandKind.Word: EmitU16(o, a.Scalar); break;
                    case OperandKind.Word3:
                        foreach (int w in a.Words!) EmitU16(o, w);
                        break;
                    case OperandKind.String: o.AddRange(a.Payload!); break;
                    case OperandKind.Script:
                        EmitU16(o, a.Payload!.Length);
                        o.AddRange(a.Payload!);
                        break;
                }
            }

            Position p = obj.Pos;
            o.Add(p.Tag);
            switch (p.Tag)
            {
                case 2: o.Add(p.ByteArg); break;
                case 6:
                    o.Add(p.ByteArg);
                    foreach (int v in p.Coords) EmitI24(o, v);
                    break;
                case 7: o.AddRange(p.Compact!); break;
                default:
                    foreach (int v in p.Coords) EmitI24(o, v);
                    break;
            }
        }
        o.Add(0xFF);

        foreach (TrailerItem t in f.Trailer)
        {
            if (t.IsBlock)
            {
                o.Add(0x00);
                EmitU16(o, t.Payload!.Length);
                o.AddRange(t.Payload!);
            }
            else
            {
                o.Add(t.SkipByte);
            }
        }
        o.Add(0xFF);
        return o.ToArray();
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>Latin-1 text of a NUL-terminated byte run (NUL stripped).</summary>
    public static string CStr(byte[] b)
    {
        int n = b.Length;
        while (n > 0 && b[n - 1] == 0) n--;
        return Encoding.Latin1.GetString(b, 0, n);
    }

    /// <summary>
    /// Render an in-module text run for display: the engine's wrapped-text
    /// printer treats 0x01 as a paragraph break and 0x0C as an opening quote
    /// glyph; 0x00 terminates a string within the concatenated run.
    /// </summary>
    public static string RenderGameText(string raw)
    {
        StringBuilder sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (c == '\u0001') sb.Append('\n');
            else if (c == '\u000C') sb.Append('"');
            else if (c == '\0') sb.Append('\n');
            else sb.Append(c);
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Is this EALIB entry a .S or .W container?</summary>
    public static bool IsMissionAsset(string name) =>
        name.EndsWith(".S", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".W", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse every .S/.W entry of an archive (2b.lib), in archive order.</summary>
    public static List<SFile> ParseArchive(EaLibArchive archive)
    {
        List<SFile> outList = new List<SFile>();
        foreach (EaLibEntry e in archive.Entries)
        {
            if (!IsMissionAsset(e.Name)) continue;
            outList.Add(Parse(e.GetDecoded(), e.Name));
        }
        return outList;
    }
}
