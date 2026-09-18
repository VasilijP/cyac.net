using System.Diagnostics.CodeAnalysis;

namespace CYAC.Formats.EaLib;

/// <summary>
/// Derives a mission's win-rule parameter catalog entry from its <c>.S</c> container: every editable
/// scalar of the trailer module with its offset, range and anchor bytes, each one proven by patching a
/// copy of the container and reading the rules back.
/// </summary>
/// <remarks>
/// <para>
/// Where the parameters come from:
/// </para>
/// <list type="bullet">
/// <item>on_event / script_hook: every <c>cmp … [bp + 6], imm</c> (argument bounds, range 0..12);</item>
/// <item>a template check_win: every counter compare of its predicate helper (range 0..100);</item>
/// <item>a template get_debrief_text: every counter or summed-counter compare of its body and of the
/// counter helpers it calls, apart from check_win's own helper;</item>
/// <item>a bespoke check_win or get_debrief_text (and the helpers it calls): clock compares, non-zero
/// counter compares, other compares against a register, an argument or a record field, and
/// <c>add ax/bx, imm</c> above 1.</item>
/// </list>
/// <para>
/// <see cref="WinRuleVocabulary"/> then names what it can.  Verification patches the value to the
/// neighbouring one, re-parses the container, re-analyses the module and requires the same instruction
/// with the new immediate; for a rule parameter it also requires the rule model to change the way the
/// parameter says ("semantic"), or to stay the same ("inert", no longer editable).
/// </para>
/// <para>
/// A container is refused, with the reason, where the Python tool would stop: no module block, a
/// module that does not disassemble, a rule model that cannot be computed.  One check is added: the
/// header's table offset must point at the module block, since every file offset is computed from it.
/// </para>
/// </remarks>
public static class WinRuleParamCollector
{
    /// <summary>The note every briefing copy carries.</summary>
    public const string BriefingNote = "(src,len) copy — string edits move the module layout; stage-2.1 territory";

    /// <summary>What an inert parameter's meaning gains.</summary>
    public const string InertNote = " [INERT: dead rule in shipped form — patching it has no semantic effect]";

    private static readonly string[] SlotNames = ["get_briefing_text", "on_event", "script_hook", "check_win", "get_debrief_text"];

    /// <summary>The outcome for one container.</summary>
    public sealed class Collection
    {
        internal Collection(WinRulesCatalog.Mission? mission, string reason)
        {
            Mission = mission;
            Reason = reason;
        }

        /// <summary>The catalog entry, when the container could be read.</summary>
        public WinRulesCatalog.Mission? Mission { get; }

        /// <summary>Why it could not be read; empty otherwise.</summary>
        public string Reason { get; }

        /// <summary>True when <see cref="Mission"/> is set.</summary>
        [MemberNotNullWhen(true, nameof(Mission))]
        public bool IsCollected => Mission is not null;
    }

    private enum Check
    {
        Event,
        Win,
        Debrief,
        Bespoke,
    }

    private sealed record Draft(WinRulesCatalog.Param P, Insn Insn, Check Check, long Target);

    /// <summary>Collects the entry of one parsed container, with <see cref="WinRuleVocabulary"/>'s words.  Never throws.</summary>
    /// <param name="file">The parsed <c>.S</c> container.</param>
    /// <param name="name">The archive member name (it selects the vocabulary).</param>
    public static Collection Collect(SMissionDecoder.SFile file, string name) =>
        Collect(file, name, WinRuleVocabulary.Annotations, WinRuleVocabulary.Idioms);

    /// <summary>Collects the entry of one parsed container with the given words.  Never throws.</summary>
    /// <param name="file">The parsed <c>.S</c> container.</param>
    /// <param name="name">The archive member name (it selects the vocabulary).</param>
    /// <param name="annotations">The parameter names and meanings to apply.</param>
    /// <param name="idioms">The module idioms to apply.</param>
    public static Collection Collect(SMissionDecoder.SFile file, string name,
                                     IReadOnlyList<WinRuleVocabulary.Annotation> annotations,
                                     IReadOnlyList<WinRuleVocabulary.Idiom> idioms)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(idioms);
        name ??= "?";
        try
        {
            return new Collection(Build(file, name, annotations, idioms), string.Empty);
        }
        catch (PyError e)
        {
            return new Collection(null, $"{e.Kind}: {e.Message}");
        }
        catch (Exception e)
        {
            return new Collection(null, $"internal error: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// <c>imm_encoding</c>: the width and block offset of an instruction's trailing immediate, for the
    /// opcodes whose immediate is a patchable scalar (the ALU compares/arithmetic and <c>mov reg, imm</c>).
    /// How the immediate reads as a number is <see cref="WinRulesCatalog.FormOf"/>.
    /// </summary>
    /// <param name="ins">The instruction.</param>
    public static (int Size, int Offset)? ImmediateEncoding(RealModeInstruction ins)
    {
        ArgumentNullException.ThrowIfNull(ins);
        byte[] b = ins.Bytes;
        int i = WinRulesCatalog.SkipPrefixes(b);

        if (i >= b.Length)
        {
            return null;
        }

        byte op = b[i];
        int size;
        if (op is 0x80 or 0x83 or 0x3C or 0x2C or 0x04 or 0x24 or 0x0C or 0x34 or >= 0xB0 and <= 0xB7)
        {
            size = 1;
        }
        else if (op is 0x81 or 0x3D or 0x2D or 0x05 or 0x25 or 0x0D or 0x35 or >= 0xB8 and <= 0xBF)
        {
            size = 2;
        }
        else
        {
            return null;
        }

        return (size, ins.Address + ins.Size - size);
    }

    private static WinRulesCatalog.Mission Build(SMissionDecoder.SFile file, string name,
                                                 IReadOnlyList<WinRuleVocabulary.Annotation> annotations,
                                                 IReadOnlyList<WinRuleVocabulary.Idiom> idioms)
    {
        SMissionDecoder.TrailerItem trailer = file.Trailer.FirstOrDefault(t => t.IsBlock)
                                              ?? throw PyError.StopIteration();
        byte[] block = trailer.Payload!;
        int[] offs = ModuleAnalysis.ReadExports(block)
            ?? throw new PyError(PyErrorKind.StructError, "unpack requires a buffer of 10 bytes");
        int tableOff = file.TableOff;
        if (file.Module is not { } module || !module.Bytes.AsSpan().SequenceEqual(block) || module.FileOffset != tableOff + 2)
        {
            throw PyError.Value($"the header's table offset 0x{tableOff:X} does not point at the module block");
        }

        ModuleAnalysis analysis = ModuleAnalysis.Analyze(block, offs);
        ModuleRules rules = ModuleRuleExtractor.Extract(analysis);
        IReadOnlyList<string> besp = rules.BespokeSlots;
        List<Draft> drafts = CollectParams(name, analysis, rules, annotations);

        WinRulesCatalog.Mission mission = new WinRulesCatalog.Mission
        {
            Class = besp.Count > 0 ? "bespoke" : "template",
            BespokeSlots = [.. besp],
            TableOff = tableOff,
            BlockFileOff = tableOff + 2,
            BlockSize = block.Length,
        };

        foreach (Draft d in drafts)
        {
            WinRulesCatalog.Param p = d.P;
            p.FileOffset = tableOff + 2 + p.BlockOffset;
            string v = p.Editable ? Verify(file.Body, tableOff, offs, d, rules) : "skipped";
            p.Verified = v;
            if (v == "inert")
            {
                p.Editable = false;
                p.Meaning += InertNote;
            }
            else if (v.StartsWith("FAILED", StringComparison.Ordinal))
            {
                p.Editable = false;
            }

            mission.Params.Add(p);
        }

        if (WinRuleVocabulary.IdiomFor(idioms, name) is { } idiom
            && WinRuleVocabulary.Resolve(idiom.References, analysis) is { } values
            && WinRuleVocabulary.Render(idiom.Text, k => values.TryGetValue(k, out long x) ? x : (long?)null) is { } text)
        {
            mission.Idiom = text;
        }

        if (rules.Briefing is { } b)
        {
            mission.Briefing = new WinRulesCatalog.BriefingCopy
            {
                Src = (int)b.Source,
                Len = (int)b.Length,
                Editable = false,
                Note = BriefingNote,
            };
        }

        mission.RuleLines = RuleLines(rules);
        return mission;
    }

    /// <summary>The author-readable rule lines (the wording of the Python tool's
    /// <c>--rules</c>).</summary>
    private static List<string> RuleLines(ModuleRules m)
    {
        List<string> lines = new List<string>();
        foreach (int slot in (int[])[1, 2])
        {
            if (m.Event(slot) is { Count: > 0 } map)
            {
                foreach ((long off, string spec) in map.OrderBy(e => e.Counter))
                {
                    lines.Add($"{SlotNames[slot]}: arg in [{spec}] → counter[+{PyText.Hex(off)}]++");
                }
            }
        }

        if (m.CheckWin is { } cw)
        {
            lines.Add($"check_win: {ModuleRuleExtractor.FormatPredicate(cw.Predicate)} → win");
        }

        if (m.Debrief is { } d)
        {
            foreach (DebriefRow row in d.Rows)
            {
                lines.Add($"debrief[ret={PyText.Repr(row.Ax)}]: {ModuleRuleExtractor.FormatPredicate(row.Condition)}");
            }
        }

        return lines;
    }

    // ---- collect_params ----------------------------------------------------------------------

    private static List<Draft> CollectParams(string name, ModuleAnalysis r, ModuleRules m,
                                             IReadOnlyList<WinRuleVocabulary.Annotation> annotations)
    {
        List<Draft> drafts = new List<Draft>();
        HashSet<int> seen = new HashSet<int>();
        IReadOnlyList<string> besp = m.BespokeSlots;
        CheckWinRule? cw = m.CheckWin;
        long? fn3Helper = cw?.Helper;
        IReadOnlyList<ModuleFunction> fns = r.Exports;

        void Mk(Insn ins, string pid, string kind, string title, string meaning, int vmin, int vmax,
                Check check, long target = 0, bool editable = true, long? counter = null, bool shared = false)
        {
            if (ImmediateEncoding(ins.I) is not { } encoding)
            {
                return;
            }

            (int size, int ioff) = encoding;
            int val = ImmediateValue(ins.I, size);

            // The Python tool caps every byte immediate at 0x7F.  That is exact for the sign-extended
            // 0x83 group and conservative for the byte operations, whose immediate holds 0..255
            // (WinRulesCatalog.FormOf); widening their cap would change the proven range of four
            // shipped parameters (ALONE besp_cmp2, GAUNTLET besp_cmp1, INSTR besp_cmp7, MOOLAH
            // besp_cmp3: 127 -> 255), so it waits for a ruling.
            if (size == 1 && vmax > 0x7F)
            {
                vmax = 0x7F;
            }

            if (!seen.Add(ioff))
            {
                return;
            }

            drafts.Add(new Draft(new WinRulesCatalog.Param
            {
                Id = pid,
                Kind = kind,
                Name = title,
                Meaning = meaning,
                Value = val,
                Encoding = size == 1 ? "imm8" : "imm16",
                BlockOffset = ioff,
                Insn = ins.I.Text,
                InsnBlockOffset = ins.Address,
                ContextBytes = Convert.ToHexStringLower(ins.I.Bytes, 0, ins.Size - size),
                Min = vmin,
                Max = vmax,
                Editable = editable,
                Counter = counter is { } c ? (int)c : null,
                SharedWithDebrief = shared,
            }, ins, check, target));
        }

        // slots 1/2: argument bounds of the event rules
        foreach (int slot in (int[])[1, 2])
        {
            if (m.Event(slot) is not { } specMap)
            {
                continue;
            }

            string key = SlotNames[slot];
            string ruleStr = string.Join("; ", specMap.OrderBy(e => e.Counter)
                .Select(e => $"arg in [{e.Arguments}] -> counter[+{PyText.Hex(e.Counter)}]++"));
            int k = 0;
            foreach (Insn ins in fns[slot].Code)
            {
                if (ins.M == "cmp" && ins.O.Contains("[bp + 6]", StringComparison.Ordinal))
                {
                    string arg = slot == 1 ? "victim slot index" : "script event id";
                    Mk(ins, $"slot{slot}_bound{k}", $"{key}_bound", $"{key} bound #{k}",
                       $"{arg} comparison bound (current rules: {ruleStr})", 0, 12, Check.Event, slot);
                    k++;
                }
            }
        }

        // slot 3: the template win predicate's thresholds
        if (cw is not null)
        {
            bool shared = fns[4].Calls.Contains(cw.Helper);
            ModuleFunction h = r.Helpers[cw.Helper];
            int k = 0;
            foreach (Insn ins in h.Code)
            {
                if (ins.M == "cmp" && ins.O.Contains("ptr cs:[", StringComparison.Ordinal))
                {
                    long off = ins.CsOffset;
                    string? spec = cw.Predicate.ValuesOf(off);
                    string meaning = !string.IsNullOrEmpty(spec)
                        ? $"check_win: axis counter[+{PyText.Hex(off)}] in [{spec}]"
                        : $"check_win predicate comparison on counter[+{PyText.Hex(off)}]";
                    Mk(ins, $"win_thr{k}", "win_threshold", $"Win threshold (counter +{PyText.Hex(off)})",
                       meaning, 0, 100, Check.Win, off, counter: off, shared: shared);
                    k++;
                }
            }
        }

        // slot 4: debrief thresholds (a helper check_win also calls is the win predicate)
        if (m.Debrief is not null && !besp.Contains("slot4"))
        {
            IEnumerable<Insn> body;
            try
            {
                body = ModuleAnalysis.SplitTemplate(fns[4].Code);
            }
            catch (PyError e) when (e.Kind == PyErrorKind.ValueError)
            {
                body = [];
            }

            List<(Insn Ins, bool IsWin)> cands = body.Select(i => (Ins: i, IsWin: false)).ToList();
            foreach (long c in fns[4].Calls.Distinct().Order())
            {
                if (c == fn3Helper)
                {
                    continue;
                }

                if (r.Helpers.TryGetValue(c, out ModuleFunction? hh) && ModuleRuleExtractor.HelperCounters(hh) is { Count: > 0 })
                {
                    bool isWin = fns[3].Calls.Contains(c);
                    cands.AddRange(hh.Code.Select(i => (i, isWin)));
                }
            }

            int k = 0;
            foreach ((Insn ins, bool isWin) in cands)
            {
                if (ins.M != "cmp")
                {
                    continue;
                }

                string o = ins.O;
                if (o.StartsWith("byte ptr cs:[", StringComparison.Ordinal) || o.StartsWith("word ptr cs:[", StringComparison.Ordinal))
                {
                    long off = ins.CsOffset;
                    string hex = PyText.Hex(off);
                    if (isWin)
                    {
                        Mk(ins, $"win_thr{k}", "win_threshold", $"Win threshold (counter +{hex})",
                           $"win predicate on counter[+{hex}] — the helper is shared by check_win (bespoke slot 3) " +
                           "and get_debrief_text, so one patch drives both",
                           0, 100, Check.Debrief, counter: off, shared: true);
                    }
                    else
                    {
                        Mk(ins, $"debrief_thr{k}", "debrief_threshold", $"Debrief threshold (counter +{hex})",
                           $"get_debrief_text: comparison on counter[+{hex}] — selects the debrief row/score, " +
                           "NOT the in-flight win",
                           0, 100, Check.Debrief, counter: off);
                    }

                    k++;
                }
                else if (o.StartsWith("al, ", StringComparison.Ordinal) && ImmediateEncoding(ins.I) is not null)
                {
                    Mk(ins, $"debrief_thr{k}", "debrief_threshold", "Debrief threshold (summed counters)",
                       "get_debrief_text: comparison on a counter sum in AL", 0, 100, Check.Debrief);
                    k++;
                }
            }
        }

        // bespoke slots: a generic scalar scan
        List<ModuleFunction> scan = new List<ModuleFunction>();
        foreach (int slot in (int[])[3, 4])
        {
            if (besp.Contains($"slot{slot}"))
            {
                scan.Add(fns[slot]);
                foreach (long c in fns[slot].Calls)
                {
                    if (r.Helpers.TryGetValue(c, out ModuleFunction? hh))
                    {
                        scan.Add(hh);
                    }
                }
            }
        }

        int n = 0;
        foreach (ModuleFunction f in scan)
        {
            foreach (Insn ins in f.Code)
            {
                if (ImmediateEncoding(ins.I) is null)
                {
                    continue;
                }

                string o = ins.O;
                long imm = ins.LastImmediate;
                string owner = FindOwner(r, ins.Address);
                string quoted = $"bespoke {owner}: `{ins.M} {o}` — see idiom notes";
                if (ins.M == "cmp" && o.Contains("[bp + 0xa]", StringComparison.Ordinal))
                {
                    Mk(ins, $"clock_thr{n}", "clock_threshold", "Mission-clock threshold",
                       $"bespoke {owner}: fires when mission clock [0xF0C8] reaches {PyText.Dec(imm)} " +
                       "(dispatch granularity 4 frames)",
                       0, 30000, Check.Bespoke);
                    n++;
                }
                else if (ins.M == "cmp" && o.Contains("ptr cs:[", StringComparison.Ordinal))
                {
                    long off = ins.CsOffset;
                    if (imm == 0)
                    {
                        continue;       // a one-shot flag test or a ==0 guard, not a knob
                    }

                    Mk(ins, $"besp_thr{n}", "win_threshold", $"Threshold (counter +{PyText.Hex(off)})",
                       $"bespoke {owner}: comparison on module byte cs:[+{PyText.Hex(off)}] " +
                       "(kill counter per the slot-1 rules)",
                       0, 100, Check.Bespoke, counter: off);
                    n++;
                }
                else if (ins.M == "cmp" && (o.StartsWith("ax, ", StringComparison.Ordinal)
                                            || o.StartsWith("al, ", StringComparison.Ordinal)
                                            || o.Contains("es:[", StringComparison.Ordinal)
                                            || o.Contains("ss:[", StringComparison.Ordinal)
                                            || o.Contains("[bp", StringComparison.Ordinal)))
                {
                    Mk(ins, $"besp_cmp{n}", "bespoke_scalar", $"Bespoke comparison @+{PyText.Hex(ins.Address)}",
                       quoted, 0, 0x7FFF, Check.Bespoke);
                    n++;
                }
                else if (ins.M == "add" && (o.StartsWith("ax, ", StringComparison.Ordinal)
                                            || o.StartsWith("bx, ", StringComparison.Ordinal)) && imm > 1)
                {
                    Mk(ins, $"besp_add{n}", "bespoke_scalar", $"Bespoke offset/delay @+{PyText.Hex(ins.Address)}",
                       quoted, 0, 0x7FFF, Check.Bespoke);
                    n++;
                }
            }
        }

        // our words replace the generic wording where the code still has the expected shape
        foreach (Draft d in drafts)
        {
            WinRulesCatalog.Param p = d.P;
            if (WinRuleVocabulary.AnnotationFor(annotations, name, p.BlockOffset) is not { } ann || d.Insn.I.Shape != ann.Shape
                || WinRuleVocabulary.Resolve(ann.References, r) is not { } values)
            {
                continue;
            }

            long? Lookup(string key) => key == "value" ? p.Value : values.TryGetValue(key, out long x) ? x : (long?)null;
            if (WinRuleVocabulary.Render(ann.Name, Lookup) is not { } title
                || WinRuleVocabulary.Render(ann.Meaning, Lookup) is not { } meaning)
            {
                continue;
            }

            p.Name = title;
            p.Meaning = meaning;
            if (ann.Editable is { } editable)
            {
                p.Editable = editable;
            }
        }

        return drafts;
    }

    /// <summary>The value of an instruction's trailing immediate of <paramref name="size"/> bytes, read by its opcode.</summary>
    private static int ImmediateValue(RealModeInstruction ins, int size)
    {
        Span<byte> bytes = ins.Bytes.AsSpan(0, ins.Size);
        return WinRulesCatalog.ReadImmediate(
            bytes[^size..], WinRulesCatalog.FormOf(bytes[..^size], size));
    }

    /// <summary><c>_find_owner</c>: the label of the first swept function whose extent holds the offset.</summary>
    private static string FindOwner(ModuleAnalysis r, int blockOffset)
    {
        foreach (ModuleFunction f in r.Exports.Concat(r.HelpersInDiscoveryOrder))
        {
            if (f.Offset <= blockOffset && blockOffset < f.End)
            {
                return f.Label;
            }
        }

        return "?";
    }

    // ---- verify_param ------------------------------------------------------------------------

    private static string Verify(byte[] body, int tableOff, int[] offs, Draft d, ModuleRules origM)
    {
        WinRulesCatalog.Param p = d.P;
        int encSize = p.Encoding == "imm8" ? 1 : 2;
        long tv = p.Value + 1 <= p.Max ? p.Value + 1 : p.Value - 1;
        if (tv < p.Min || tv == p.Value)
        {
            return "FAILED: no distinct test value in range";
        }

        int fileOff = tableOff + 2 + p.BlockOffset;
        if (fileOff < 0 || fileOff + encSize > body.Length)
        {
            throw PyError.Index();
        }

        byte[] patched = (byte[])body.Clone();
        for (int i = 0; i < encSize; i++)
        {
            patched[fileOff + i] = (byte)((tv >> (8 * i)) & 0xFF);
        }

        SMissionDecoder.SFile m2;
        try
        {
            m2 = SMissionDecoder.Parse(patched, "?");
        }
        catch (Exception e)
        {
            return $"FAILED: patched body no longer parses ({e.Message})";
        }

        SMissionDecoder.TrailerItem trailer = m2.Trailer.FirstOrDefault(t => t.IsBlock) ?? throw PyError.StopIteration();
        byte[] blk2 = trailer.Payload!;
        int[] offs2 = ModuleAnalysis.ReadExports(blk2)
            ?? throw new PyError(PyErrorKind.StructError, "unpack requires a buffer of 10 bytes");
        if (!offs2.AsSpan().SequenceEqual(offs))
        {
            return "FAILED: export table changed";
        }

        foreach (int o in offs2)
        {
            if (!(o + 3 <= blk2.Length && blk2[o] == 0x55 && blk2[o + 1] == 0x8B && blk2[o + 2] == 0xEC))
            {
                return $"FAILED: prologue destroyed at +{PyText.Hex(o)}";
            }
        }

        ModuleAnalysis r2;
        try
        {
            r2 = ModuleAnalysis.Analyze(blk2, offs2);
        }
        catch (PyError e)
        {
            return $"FAILED: patched module no longer disassembles ({e.Message})";
        }

        // read-back: the same instruction now carries the new immediate
        int ioff = p.InsnBlockOffset;
        RealModeInstruction? ins2 = ioff >= 0 && ioff < blk2.Length
            ? RealModeDisassembler.Decode(blk2.AsSpan(ioff, Math.Min(16, blk2.Length - ioff)), ioff)
            : null;
        string context = p.ContextBytes;
        if (ins2 is null
            || Convert.ToHexStringLower(ins2.Bytes, 0, Math.Min(ins2.Size, context.Length / 2)) != context)
        {
            return "FAILED: instruction context changed under patch";
        }

        long back = ins2.Size == d.Insn.Size ? ImmediateValue(ins2, encSize) : long.MinValue;
        if (back != tv)
        {
            return $"FAILED: read-back imm {PyText.Dec(back)} != {PyText.Dec(tv)}";
        }

        if (d.Check == Check.Bespoke)
        {
            return "syntactic";
        }

        ModuleRules m2r;
        try
        {
            m2r = ModuleRuleExtractor.Extract(r2);
        }
        catch (PyError e)
        {
            return $"FAILED: rule re-extraction crashed ({e.Message})";
        }

        if (!m2r.BespokeSlots.SequenceEqual(origM.BespokeSlots))
        {
            return "FAILED: patch changed the mission's bespoke classification";
        }

        // A bound N can surface as N (>=-style), N-1 (a jb-closed range) or N+1 (a jne axis).
        static bool Visible(long v, HashSet<string> tokens) =>
            tokens.Contains(PyText.Dec(v - 1)) || tokens.Contains(PyText.Dec(v)) || tokens.Contains(PyText.Dec(v + 1));

        switch (d.Check)
        {
            case Check.Event:
            {
                IReadOnlyList<(long Counter, string Arguments)> old = origM.Event((int)d.Target) ?? [];
                IReadOnlyList<(long Counter, string Arguments)> neu = m2r.Event((int)d.Target) ?? [];
                if (SameRules(old, neu))
                {
                    return "inert";     // the immediate is real but the shipped rule structure ignores it
                }

                HashSet<string> tokens = new HashSet<string>(StringComparer.Ordinal);
                foreach ((long _, string spec) in neu)
                {
                    tokens.UnionWith(SpecTokens(spec));
                }

                if (!Visible(tv, tokens) && neu.All(e => e.Arguments != "always"))
                {
                    string repr = "{" + string.Join(", ", neu.Select(e => $"{PyText.Dec(e.Counter)}: {PyText.Repr(e.Arguments)}")) + "}";
                    return $"FAILED: new bound {PyText.Dec(tv)} not visible in re-extracted rules {repr}";
                }

                return "semantic";
            }

            case Check.Win:
            {
                string oldp = origM.CheckWin is { } a ? ModuleRuleExtractor.FormatPredicate(a.Predicate) : "";
                string newp = m2r.CheckWin is { } b ? ModuleRuleExtractor.FormatPredicate(b.Predicate) : "";
                if (oldp == newp)
                {
                    return "inert";
                }

                CounterPredicate det = m2r.CheckWin?.Predicate
                                       ?? throw PyError.Type("'NoneType' object is not subscriptable");
                if (det.ValuesOf(d.Target) is { } axis && !Visible(tv, SpecTokens(axis)))
                {
                    return $"FAILED: threshold {PyText.Dec(tv)} not visible in axis spec {axis}";
                }

                return "semantic";
            }

            default:
            {
                string old = origM.Debrief?.Repr() ?? "None";
                string neu = m2r.Debrief?.Repr() ?? "None";
                return old == neu ? "FAILED: debrief table unchanged under patch" : "semantic";
            }
        }
    }

    /// <summary>Dictionary equality of two rule maps (order does not matter).</summary>
    private static bool SameRules(IReadOnlyList<(long Counter, string Arguments)> a, IReadOnlyList<(long Counter, string Arguments)> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        Dictionary<long, string> map = a.ToDictionary(e => e.Counter, e => e.Arguments);
        return b.All(e => map.TryGetValue(e.Counter, out string? v) && v == e.Arguments);
    }

    /// <summary><c>_spec_tokens</c>: the non-empty pieces of a value set split at <c>,</c>, <c>+</c> and <c>-</c>.</summary>
    private static HashSet<string> SpecTokens(string spec) =>
        new(spec.Split([',', '+', '-'], StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
}
