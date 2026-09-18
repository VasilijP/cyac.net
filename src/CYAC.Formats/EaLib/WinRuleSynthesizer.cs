using System.Text;

namespace CYAC.Formats.EaLib;

// ---------------------------------------------------------------------------
// WIN-RULE MODULE SYNTHESIZER — C# port of's `synth`
// The codegen rulebook for the win-rule model.
//
// Semantic model (WinRuleModel) -> a complete `.S` trailer module block:
// export table, DS save slot, byte vars, radio texts, predicate helper(s),
// briefing / debrief strings and the five exported functions as freshly
// assembled 8086.  Proof standard = the Python one: for all 46 modeled
// missions Synth(model) reproduces the shipping module BYTE-EXACT
// (`--selftest-synth`, hard-fails below 46/46).
//
// THE CODEGEN RULEBOOK (mission-independent; no per-mission escape hatches)
//
//  LAYOUT, in emission order (block-relative):
//    +0x00..0x09   export table: 5 u16 fn offsets (fn0..fn4)
//    data region   the model's data_layout in SOURCE-DECLARATION order:
//                  the 2-byte DS save slot (00 00), zero-init byte vars
//                  (kill counters / one-shot radio flags) and radio texts.
//                  Template modules: save @+0xA, counters from +0xC.
//    helper(s)     the win-predicate helper(s), in model order
//    briefing      briefing bytes, verbatim
//    fn0 fn1 fn2 fn3
//    debrief texts tightly packed, in fn4 FIRST-USE order
//    fn4
//  EVEN-ALIGNMENT: every CODE entity starts at an even offset; a single 0x90
//  pad byte is inserted when the cursor is odd.  Strings are NOT aligned.
//  Every reference is BACKWARD, and instruction encodings are fixed-width, so
//  one sequential pass suffices.
//
//  FUNCTION FRAME (MSC far, 23 B):
//    prologue 55 8B EC | 56 57 | 2E 8C 1E <save> | 8C C8 | 8E D8
//    epilogue 2E 8E 1E <save> | 5F 5E 5D CB
//
//  INSTRUCTION SELECTION (the authoring compiler used exactly one encoding per
//  operation across all 51 shipping modules):
//    cmp word [bp+6],imm   83 7E 06 ib      (sign-extended imm8, imm <= 0x7F)
//    cmp word [bp+0xA],imm 83 7E 0A ib      (the mission clock)
//    cmp byte [bp+6],imm   80 7E 06 ib
//    cmp byte cs:[c],imm   2E 80 3E cw ib
//    inc byte cs:[c]       2E FE 06 cw
//    jcc / jmp             SHORT form only; call = E8 rel16
//    mov si/cx,imm16       BE iw / B9 iw ;  ax=1 -> B8 01 00 ;  ax=0 -> 33 C0
//    les di,[bp+6]         C4 7E 06 ;  mov di,[bp+6] / mov es,[bp+8] = fn0 twin
//    win flag              8B 5E 12 / 36 C6 07 01
//    msg (radio channel)   B8 iw (text off) / 8C CA (mov dx,cs)
//    activate (ACE)        8E 46 0C / 8B 5E 0E ; per slot k: 36 8B 77 2k +
//                          26 80 4C 3C 01
// ---------------------------------------------------------------------------
public static class WinRuleSynthesizer
{
    public static readonly IReadOnlyDictionary<string, byte> JccOpcodes = new Dictionary<string, byte>
    {
        ["jb"] = 0x72, ["jae"] = 0x73, ["je"] = 0x74, ["jne"] = 0x75, ["jbe"] = 0x76,
        ["ja"] = 0x77, ["jl"] = 0x7C, ["jge"] = 0x7D, ["jle"] = 0x7E, ["jg"] = 0x7F,
    };

    /// <summary>cc &lt;-&gt; the cc of the negated comparison (same signedness family).</summary>
    public static readonly IReadOnlyDictionary<string, string> InvertCc = new Dictionary<string, string>
    {
        ["jb"] = "jae", ["jae"] = "jb", ["je"] = "jne", ["jne"] = "je", ["jbe"] = "ja",
        ["ja"] = "jbe", ["jl"] = "jge", ["jge"] = "jl", ["jle"] = "jg", ["jg"] = "jle",
    };

    public sealed class SynthException : Exception
    {
        public SynthException(string msg) : base(msg) { }
    }

    /// <summary>Where the synthesizer placed each entity (diagnostics + the sanity sweep).</summary>
    public sealed class Layout
    {
        public int[] ExportOffsets { get; } = new int[5];
        public List<int> HelperOffsets { get; } = new();
        /// <summary>Code-entity spans (offset, length) — helpers and the 5 functions.</summary>
        public List<(int Off, int Len, string What)> CodeSpans { get; } = new();
        /// <summary>Every near-call target emitted (must land on a helper).</summary>
        public List<int> CallTargets { get; } = new();
        /// <summary>Text key -&gt; (offset, length) in the block.</summary>
        public Dictionary<string, (int Off, int Len)> Texts { get; } = new();
        public int SaveOff { get; set; }
        public List<int> ByteVarOffsets { get; } = new();
        public int FirstCodeOffset { get; set; }
        public int Size { get; set; }
    }

    /// <summary>Synthesize the module block for <paramref name="m"/>.</summary>
    public static byte[] Synth(WinRuleModel m) => SynthWithLayout(m).Block;

    public static (byte[] Block, Layout Layout) SynthWithLayout(WinRuleModel m)
    {
        List<string> errs = m.Validate();
        if (errs.Count > 0)
            throw new SynthException($"{m.Name}: invalid model — " + string.Join("; ", errs));

        Layout lay = new Layout();
        List<byte> outBuf = new List<byte>(new byte[10]);          // export-table placeholder
        Dictionary<string, (int Off, int Len)> texts = lay.Texts;

        // ---- data region (source-declaration order) ------------------------
        int saveAt = -1;
        foreach (WinRuleDataItem item in m.EffectiveLayout())
        {
            switch (item.Kind)
            {
                case "save":
                    saveAt = outBuf.Count;
                    outBuf.Add(0); outBuf.Add(0);            // DS save slot, zero-init
                    break;
                case "byte":
                    lay.ByteVarOffsets.Add(outBuf.Count);
                    outBuf.Add(0);                            // counter / one-shot flag
                    break;
                case "rtext":
                {
                    byte[] b = Latin1(m.RadioTexts[item.Key!]);
                    texts[item.Key!] = (outBuf.Count, b.Length);
                    outBuf.AddRange(b);
                    break;
                }
            }
        }
        if (saveAt != m.SaveOff)
            throw new SynthException($"{m.Name}: layout puts the save slot at +0x{saveAt:X}, " +
                                     $"model says +0x{m.SaveOff:X}");
        lay.SaveOff = saveAt;

        int CtrOff(int? idx)
        {
            if (idx is null || idx < 0 || idx >= lay.ByteVarOffsets.Count)
                throw new SynthException($"{m.Name}: byte var #{idx} does not exist");
            return lay.ByteVarOffsets[idx.Value];
        }

        void PadEven()
        {
            if ((outBuf.Count & 1) != 0) outBuf.Add(0x90);    // NOP alignment pad
        }

        (int Off, int Len) TextAt(string? key)
        {
            if (key is null || !texts.TryGetValue(key, out (int Off, int Len) t))
                throw new SynthException($"{m.Name}: text '{key}' referenced before it is placed");
            return t;
        }

        // ---- helpers -------------------------------------------------------
        foreach (WinRuleHelper h in m.Helpers)
        {
            PadEven();
            int at = outBuf.Count;
            lay.HelperOffsets.Add(at);
            byte[] code = SynthHelper(at, h, CtrOff);
            outBuf.AddRange(code);
            lay.CodeSpans.Add((at, code.Length, $"helper{lay.HelperOffsets.Count - 1}"));
        }

        // ---- briefing text --------------------------------------------------
        if (m.Briefing is not null)
        {
            byte[] b = Latin1(m.Briefing);
            texts["b"] = (outBuf.Count, b.Length);
            outBuf.AddRange(b);
        }

        EmitContext ctx = new EmitContext(m, lay, CtrOff, TextAt);

        void EmitFn(int slot, Action<Asm> body)
        {
            PadEven();
            int at = outBuf.Count;
            lay.ExportOffsets[slot] = at;
            Asm a = new Asm(at);
            a.Raw(Prologue(m.SaveOff));
            body(a);
            a.Label("END");
            a.Raw(Epilogue(m.SaveOff));
            byte[] code = a.Resolve();
            outBuf.AddRange(code);
            lay.CallTargets.AddRange(a.CallTargets);
            lay.CodeSpans.Add((at, code.Length, $"fn{slot}"));
        }

        // ---- fn0: the briefing copy -----------------------------------------
        EmitFn(0, a =>
        {
            if (m.Fn0Style is null) return;
            (int src, int len) = TextAt("b");
            EmitCopy(a, m.Fn0Style, src, len);
        });

        // ---- fn1 / fn2: the event rules --------------------------------------
        EmitFn(1, a => EmitBody(a, m.Fn1, null, ctx));
        EmitFn(2, a => EmitBody(a, m.Fn2, null, ctx));

        // ---- fn3: check_win ---------------------------------------------------
        EmitFn(3, a =>
        {
            EmitBody(a, m.Fn3.Pre, null, ctx);
            if (m.Fn3.Kind == "template")
            {
                a.Call(lay.HelperOffsets[m.Fn3.Helper!.Value]);
                a.Jcc("jae", "NOWIN");
                a.Raw(0x8B, 0x5E, 0x12);                      // mov bx,[bp+0x12]
                a.Raw(0x36, 0xC6, 0x07, 0x01);                // mov byte ss:[bx],1
                a.Label("NOWIN");
            }
            else if (m.Fn3.Kind != "never")
            {
                throw new SynthException($"{m.Name}: bad fn3 kind '{m.Fn3.Kind}'");
            }
            EmitBody(a, m.Fn3.Extra, null, ctx);
        });

        // ---- debrief texts (fn4 first-use order) -------------------------------
        foreach (KeyValuePair<string, string> kv in m.DebriefTexts.Items)
        {
            byte[] b = Latin1(kv.Value);
            texts[kv.Key] = (outBuf.Count, b.Length);
            outBuf.AddRange(b);
        }

        // ---- fn4: the debrief decision table -----------------------------------
        {
            PadEven();
            int at = outBuf.Count;
            lay.ExportOffsets[4] = at;
            Asm a = new Asm(at);
            a.Raw(Prologue(m.SaveOff));
            if (m.Fn4Style == "hoisted") a.Raw(0xC4, 0x7E, 0x06);   // les di,[bp+6]
            EmitBody(a, m.Fn4, m.Fn4Style, ctx);
            a.Label("END");
            if (m.Fn4Style == "shared_tail" && m.Fn4.Count > 0)
            {
                a.Raw(0xC4, 0x7E, 0x06);                             // les di,[bp+6]
                a.Raw(0xF3, 0xA4);                                   // rep movsb
                a.Label("EPI");
            }
            a.Raw(Epilogue(m.SaveOff));
            byte[] code = a.Resolve();
            outBuf.AddRange(code);
            lay.CallTargets.AddRange(a.CallTargets);
            lay.CodeSpans.Add((at, code.Length, "fn4"));
        }

        byte[] block = outBuf.ToArray();
        for (int i = 0; i < 5; i++)
        {
            block[i * 2] = (byte)(lay.ExportOffsets[i] & 0xFF);
            block[i * 2 + 1] = (byte)(lay.ExportOffsets[i] >> 8);
        }
        lay.FirstCodeOffset = lay.CodeSpans.Count > 0 ? lay.CodeSpans.Min(c => c.Off) : block.Length;
        lay.Size = block.Length;
        return (block, lay);
    }

    // ---- statement emission ------------------------------------------------

    private sealed class EmitContext
    {
        public EmitContext(WinRuleModel m, Layout lay, Func<int?, int> ctrOff,
                           Func<string?, (int Off, int Len)> textAt)
        { Model = m; Lay = lay; CtrOff = ctrOff; TextAt = textAt; }

        public WinRuleModel Model { get; }
        public Layout Lay { get; }
        public Func<int?, int> CtrOff { get; }
        public Func<string?, (int Off, int Len)> TextAt { get; }
    }

    private static void EmitBody(Asm a, List<WinRuleStmt> stmts, string? style, EmitContext ctx)
    {
        foreach (WinRuleStmt s in stmts)
        {
            switch (s.Op)
            {
                case "inc":
                {
                    int off = ctx.CtrOff(s.Counter);
                    a.Raw(0x2E, 0xFE, 0x06, (byte)off, (byte)(off >> 8));
                    break;
                }
                case "copy":
                {
                    (int src, int len) = ctx.TextAt(s.Text);
                    EmitCopy(a, style, src, len);
                    break;
                }
                case "ax":
                    if (s.Val == 1) a.Raw(0xB8, 0x01, 0x00);          // mov ax,1
                    else a.Raw(0x33, 0xC0);                            // xor ax,ax
                    break;
                case "dx0":
                    a.Raw(0x33, 0xD2);                                 // xor dx,dx
                    break;
                case "activate":
                    a.Raw(0x8E, 0x46, 0x0C);                           // mov es,[bp+0xc]
                    a.Raw(0x8B, 0x5E, 0x0E);                           // mov bx,[bp+0xe]
                    foreach (int k in s.Slots!)
                    {
                        a.Raw(0x36, 0x8B, 0x77, (byte)(2 * k));        // mov si,ss:[bx+2k]
                        a.Raw(0x26, 0x80, 0x4C, 0x3C, 0x01);           // or byte es:[si+0x3c],1
                    }
                    break;
                case "msg":
                {
                    (int src, _) = ctx.TextAt(s.Text);
                    a.Raw(0xB8, (byte)src, (byte)(src >> 8));          // mov ax,text_off
                    a.Raw(0x8C, 0xCA);                                 // mov dx,cs
                    break;
                }
                case "ret":
                    a.Jmp("END");
                    break;
                case "if":
                {
                    string after = a.NewLabel("IF");
                    if (s.Helper is not null)
                    {
                        a.Call(ctx.Lay.HelperOffsets[s.Helper.Value]);
                        a.Jcc("jae", after);
                    }
                    else
                    {
                        foreach (WinRuleTest t in s.Tests!) EmitTest(a, t, after, ctx.CtrOff);
                    }
                    EmitBody(a, s.Body ?? new List<WinRuleStmt>(), style, ctx);
                    a.Label(after);
                    break;
                }
                default:
                    throw new SynthException($"unsupported statement '{s.Op}'");
            }
        }
    }

    private static void EmitTest(Asm a, WinRuleTest t, string failLabel, Func<int?, int> ctrOff)
    {
        switch (t.Var)
        {
            case "arg_w":
                Require(t.Imm is >= 0 and <= 0x7F, "the sign-extended imm8 cmp form needs imm <= 0x7F");
                a.Raw(0x83, 0x7E, 0x06, (byte)t.Imm);
                break;
            case "clock":
                Require(t.Imm is >= 0 and <= 0x7F, "the sign-extended imm8 cmp form needs imm <= 0x7F");
                a.Raw(0x83, 0x7E, 0x0A, (byte)t.Imm);
                break;
            case "arg_b":
                a.Raw(0x80, 0x7E, 0x06, (byte)t.Imm);
                break;
            case "ctr":
            {
                int off = ctrOff(t.Counter);
                a.Raw(0x2E, 0x80, 0x3E, (byte)off, (byte)(off >> 8), (byte)t.Imm);
                break;
            }
            default:
                throw new SynthException($"bad test var '{t.Var}'");
        }
        a.Jcc(t.Cc, failLabel);
    }

    private static void EmitCopy(Asm a, string? style, int src, int length)
    {
        a.Raw(0xBE, (byte)src, (byte)(src >> 8));                      // mov si,imm16
        if (style == "moves")                                          // the fn0 codegen twin
        {
            a.Raw(0x8B, 0x7E, 0x06);                                   // mov di,[bp+6]
            a.Raw(0x8E, 0x46, 0x08);                                   // mov es,[bp+8]
        }
        a.Raw(0xB9, (byte)length, (byte)(length >> 8));                // mov cx,imm16
        if (style is "les" or "per_arm") a.Raw(0xC4, 0x7E, 0x06);      // les di,[bp+6]
        if (style is "les" or "moves" or "per_arm" or "hoisted") a.Raw(0xF3, 0xA4);   // rep movsb
        // style "shared_tail": si/cx only — the shared tail does the copy
    }

    private static byte[] SynthHelper(int org, WinRuleHelper h, Func<int?, int> ctrOff)
    {
        Asm a = new Asm(org);
        if (h.Polarity == "stc_first")
        {
            foreach (WinRuleTest t in h.Tests) EmitTest(a, t, "FAIL", ctrOff);
            a.Raw(0xF9, 0xC3);                 // stc; ret
            a.Label("FAIL");
            a.Raw(0xF8, 0xC3);                 // clc; ret
        }
        else if (h.Polarity == "clc_first")
        {
            foreach (WinRuleTest t in h.Tests) EmitTest(a, t, "WIN", ctrOff);
            a.Raw(0xF8, 0xC3);                 // clc; ret
            a.Label("WIN");
            a.Raw(0xF9, 0xC3);                 // stc; ret
        }
        else throw new SynthException($"bad helper polarity '{h.Polarity}'");
        return a.Resolve();
    }

    private static byte[] Prologue(int save) => new byte[]
    {
        0x55, 0x8B, 0xEC,                      // push bp; mov bp,sp
        0x56, 0x57,                            // push si; push di
        0x2E, 0x8C, 0x1E, (byte)save, (byte)(save >> 8),   // mov cs:[save],ds
        0x8C, 0xC8,                            // mov ax,cs
        0x8E, 0xD8,                            // mov ds,ax
    };

    private static byte[] Epilogue(int save) => new byte[]
    {
        0x2E, 0x8E, 0x1E, (byte)save, (byte)(save >> 8),   // mov ds,cs:[save]
        0x5F, 0x5E, 0x5D, 0xCB,                // pop di; pop si; pop bp; retf
    };

    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    private static void Require(bool cond, string msg)
    {
        if (!cond) throw new SynthException(msg);
    }

    // ---- the tiny fixed-encoding 8086 emitter -------------------------------

    /// <summary>
    /// Label-based emitter with short-branch fixups.  <see cref="Org"/> is the
    /// BLOCK-RELATIVE address of byte 0, because every module reference (call
    /// displacement, text pointer) is block-relative — the module is fully
    /// position-independent (P4: 255/255 fns, zero far calls).
    /// </summary>
    private sealed class Asm
    {
        private readonly List<byte> _buf = new();
        private readonly Dictionary<string, int> _labels = new();
        private readonly List<(int BufOff, string Label)> _fix = new();
        private int _uid;

        public Asm(int org) { Org = org; }

        public int Org { get; }
        public int Here => Org + _buf.Count;
        public List<int> CallTargets { get; } = new();

        public string NewLabel(string prefix) => $"{prefix}{++_uid}";

        public void Label(string name) => _labels[name] = Here;

        public void Raw(params byte[] b) => _buf.AddRange(b);

        public void Jcc(string cc, string target)
        {
            if (!JccOpcodes.TryGetValue(cc, out byte op))
                throw new SynthException($"unknown condition code '{cc}'");
            _buf.Add(op);
            _fix.Add((_buf.Count, target));
            _buf.Add(0);
        }

        public void Jmp(string target)
        {
            _buf.Add(0xEB);
            _fix.Add((_buf.Count, target));
            _buf.Add(0);
        }

        public void Call(int absTarget)
        {
            _buf.Add(0xE8);
            int rel = absTarget - (Here + 2);
            _buf.Add((byte)(rel & 0xFF));
            _buf.Add((byte)((rel >> 8) & 0xFF));
            CallTargets.Add(absTarget);
        }

        public byte[] Resolve()
        {
            foreach ((int off, string label) in _fix)
            {
                if (!_labels.TryGetValue(label, out int tgt))
                    throw new SynthException($"unresolved label '{label}'");
                int rel = tgt - (Org + off + 1);
                if (rel is < -128 or > 127)
                    throw new SynthException(
                        $"short branch to '{label}' out of range ({rel}) — the module's " +
                        "code grew past what the 8086 short-jump codegen can address");
                _buf[off] = (byte)(rel & 0xFF);
            }
            return _buf.ToArray();
        }
    }

    // ---- sanity sweep --------------------------------------------------------

    /// <summary>
    /// Structural sweep over a synthesized (or shipping) module block — the
    /// byte-pattern half of <c>s_module_decode.py</c>'s verifier, which is what
    /// can be checked without a disassembler: export offsets in range, past the
    /// export table and EVEN; MSC far prologue at every export; the block big
    /// enough; and, when a <paramref name="layout"/> is supplied, every code
    /// entity terminated by ret/retf and every near-call target landing exactly
    /// on a helper entry.  Returns the violations (empty = clean).
    ///
    /// <para>(The PIC property the Python sweep checks with capstone — no far
    /// calls, no out-of-block call targets — is structural here: the emitter
    /// has no far-call encoding at all, and call targets are validated against
    /// the helper table rather than guessed from a byte scan, which would
    /// false-positive on 0x9A bytes inside immediates.)</para>
    /// </summary>
    public static List<string> Sanity(byte[] block, Layout? layout = null)
    {
        List<string> errs = new List<string>();
        if (block.Length < 10 + 5) { errs.Add($"block is only {block.Length} B"); return errs; }
        int[] offs = new int[5];
        for (int i = 0; i < 5; i++) offs[i] = block[i * 2] | (block[i * 2 + 1] << 8);
        for (int k = 0; k < 5; k++)
        {
            int o = offs[k];
            if (o < 10 || o + 3 > block.Length)
            { errs.Add($"fn{k} export offset +0x{o:X} outside the block"); continue; }
            if ((o & 1) != 0) errs.Add($"fn{k} @+0x{o:X} is not even-aligned");
            if (!(block[o] == 0x55 && block[o + 1] == 0x8B && block[o + 2] == 0xEC))
                errs.Add($"fn{k} @+0x{o:X} lacks the MSC far prologue 55 8B EC");
        }
        if (layout is not null)
        {
            for (int k = 0; k < 5; k++)
                if (layout.ExportOffsets[k] != offs[k])
                    errs.Add($"fn{k}: export table says +0x{offs[k]:X}, layout placed it at " +
                             $"+0x{layout.ExportOffsets[k]:X}");
            foreach ((int off, int len, string what) in layout.CodeSpans)
            {
                if (off + len > block.Length) { errs.Add($"{what} runs past the block end"); continue; }
                if ((off & 1) != 0) errs.Add($"{what} @+0x{off:X} is not even-aligned");
                bool nearRet = len >= 2 && block[off + len - 1] == 0xC3;         // helper: ret
                bool farRet = len >= 4 && block[off + len - 1] == 0xCB
                                       && block[off + len - 4] == 0x5F;          // pop di; pop si; pop bp; retf
                if (!nearRet && !farRet) errs.Add($"{what} does not end in ret/retf");
            }
            foreach (int t in layout.CallTargets)
                if (!layout.HelperOffsets.Contains(t))
                    errs.Add($"near call to +0x{t:X} is not a helper entry");
            if (layout.Size != block.Length)
                errs.Add($"layout size {layout.Size} != block length {block.Length}");
        }
        return errs;
    }
}
