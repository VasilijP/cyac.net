using System.Text;

namespace CYAC.Formats.EaLib;

/// <summary>
/// A counter predicate: either one value set per counter (a conjunction) or, when no conjunction
/// describes it, the explicit table of counter combinations that satisfy it.
/// </summary>
public sealed class CounterPredicate
{
    internal CounterPredicate(IReadOnlyList<(long Counter, string Values)>? axes, IReadOnlyList<int[]>? truthTable)
    {
        Axes = axes;
        TruthTable = truthTable;
    }

    /// <summary>Per counter, in reading order, the value set that satisfies the predicate (e.g. <c>2+</c>, <c>0-1</c>, <c>never</c>).</summary>
    public IReadOnlyList<(long Counter, string Values)>? Axes { get; }

    /// <summary>The satisfying combinations, in ascending order, when the predicate is not a conjunction.</summary>
    public IReadOnlyList<int[]>? TruthTable { get; }

    /// <summary>The value set of one counter, or null (also for a truth table).</summary>
    /// <param name="counter">The counter's module offset.</param>
    public string? ValuesOf(long counter)
    {
        if (Axes is null)
        {
            return null;
        }

        foreach ((long c, string v) in Axes)
        {
            if (c == counter)
            {
                return v;
            }
        }

        return null;
    }

    /// <summary><c>fmt_pred</c>: <c>counter[+0xc] in [2+] AND …</c>, or <c>truth[(…), …]</c>.</summary>
    public override string ToString()
    {
        if (TruthTable is { } table)
        {
            return "truth" + ModuleRuleExtractor.ReprTuples(table);
        }

        return string.Join(" AND ", Axes!.Select(a => $"counter[+{PyText.Hex(a.Counter)}] in [{a.Values}]"));
    }

    internal string Repr()
    {
        if (TruthTable is { } table)
        {
            return "{'truth_table': " + ModuleRuleExtractor.ReprTuples(table) + "}";
        }

        return "{" + string.Join(", ", Axes!.Select(a => $"{PyText.Dec(a.Counter)}: {PyText.Repr(a.Values)}")) + "}";
    }
}

/// <summary>The in-flight win test of a template check_win: the helper it calls and what that helper tests.</summary>
/// <param name="Helper">The predicate helper's offset.</param>
/// <param name="Kind">Always <c>counters</c> (other helpers make the slot bespoke).</param>
/// <param name="Predicate">The helper's predicate over the counters.</param>
public sealed record CheckWinRule(long Helper, string Kind, CounterPredicate Predicate);

/// <summary>One outcome of get_debrief_text: the condition, the texts it copies and the value it returns.</summary>
/// <param name="Condition">The counter condition that leads here.</param>
/// <param name="Copies">The (source, length) copies, in order (either part may be unknown).</param>
/// <param name="Ax">The returned AX, when the path sets it.</param>
public sealed record DebriefRow(CounterPredicate Condition, IReadOnlyList<(long? Source, long? Length)> Copies, long? Ax);

/// <summary>The debrief decision table: the counters it reads and its rows, most successful first.</summary>
/// <param name="Counters">The counters get_debrief_text depends on, in reading order.</param>
/// <param name="Rows">The distinct outcomes.</param>
public sealed record DebriefTable(IReadOnlyList<long> Counters, IReadOnlyList<DebriefRow> Rows)
{
    internal string Repr()
    {
        StringBuilder sb = new StringBuilder("{'counters': [");
        sb.Append(string.Join(", ", Counters.Select(PyText.Dec))).Append("], 'rows': [");
        sb.Append(string.Join(", ", Rows.Select(r =>
            "(" + r.Condition.Repr() + ", [" +
            string.Join(", ", r.Copies.Select(c => "(" + PyText.Repr(c.Source) + ", " + PyText.Repr(c.Length) + ")")) +
            "], " + PyText.Repr(r.Ax) + ")")));
        return sb.Append("]}").ToString();
    }
}

/// <summary>
/// The semantic rule model of one module (<c>extract_rules</c>): what the briefing copies, which event
/// arguments bump which counter, the win predicate, and the debrief decision table.  A function that
/// does not fit the model is listed in <see cref="Bespoke"/> with the reason.
/// </summary>
public sealed class ModuleRules
{
    internal ModuleRules()
    {
    }

    /// <summary>The functions that did not fit, as (<c>slotN</c>, reason), in slot order.</summary>
    public List<(string Slot, string Reason)> Bespoke { get; } = [];

    /// <summary>The briefing copy (source, length); null when slot 0 is empty or bespoke.</summary>
    public (long Source, long Length)? Briefing { get; internal set; }

    /// <summary>on_event: counter → the argument values (0..40) that bump it; null when bespoke.</summary>
    public IReadOnlyList<(long Counter, string Arguments)>? OnEvent { get; internal set; }

    /// <summary>script_hook, in the same form; null when bespoke.</summary>
    public IReadOnlyList<(long Counter, string Arguments)>? ScriptHook { get; internal set; }

    /// <summary>The template win test; null when check_win never wins or is bespoke.</summary>
    public CheckWinRule? CheckWin { get; internal set; }

    /// <summary>The debrief decision table; null when get_debrief_text is empty or bespoke.</summary>
    public DebriefTable? Debrief { get; internal set; }

    /// <summary>The bespoke slot names, sorted and distinct.</summary>
    public IReadOnlyList<string> BespokeSlots =>
        Bespoke.Select(b => b.Slot).Distinct().Order(StringComparer.Ordinal).ToList();

    internal IReadOnlyList<(long Counter, string Arguments)>? Event(int slot) => slot == 1 ? OnEvent : ScriptHook;
}

/// <summary>
/// The concrete-execution rule extractor, ported: event bodies are run for every argument 0..40,
/// predicate helpers over every counter combination 0..9, and the debrief function over every
/// combination 0..6; the outcomes are condensed back into value sets.
/// </summary>
/// <remarks>
/// <para>
/// The port is faithful, including the Python tool's choices: its value domains, its orderings, its
/// text matching (a <c>mov si</c> counts as a briefing source only when its operand prints in hex),
/// its signed comparisons for every conditional jump, and its step limits (200 steps for an event
/// body, 100 for a helper, 300 for the debrief).  Where the Python tool would stop with an unhandled
/// error, <see cref="TryExtract"/> returns false with that error's message.
/// </para>
/// <para>
/// One addition bounds the enumeration: a predicate over more than
/// <see cref="MaxCombinations"/> counter combinations is not enumerated and the slot is reported as
/// bespoke instead.  The shipped modules test at most three counters (1,000 combinations).
/// </para>
/// </remarks>
public static class ModuleRuleExtractor
{
    /// <summary>The largest counter-combination space the extractor enumerates.</summary>
    public const int MaxCombinations = 100_000;

    private static readonly string[] JccMnemonics = ["je", "jne", "jb", "jae", "jbe", "ja", "jl", "jge", "jle", "jg"];

    /// <summary>
    /// Extracts the rule model of an analysed module.  Returns false with the error message where the
    /// Python tool would fail outright.  Never throws.
    /// </summary>
    /// <param name="analysis">The analysed module.</param>
    /// <param name="rules">The rule model.</param>
    /// <param name="error">The failure, when it fails.</param>
    public static bool TryExtract(ModuleAnalysis analysis, out ModuleRules? rules, out string? error)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        try
        {
            rules = Extract(analysis);
            error = null;
            return true;
        }
        catch (PyError e)
        {
            rules = null;
            error = e.Message;
            return false;
        }
        catch (Exception e)
        {
            rules = null;
            error = $"internal error: {e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    /// <summary><c>condense</c>: <c>[2,3,4,7]</c> → <c>2-4,7</c>; a run reaching <paramref name="top"/> prints as <c>N+</c>.</summary>
    /// <param name="values">The values, in the order they were found.</param>
    /// <param name="top">The domain's last value.</param>
    public static string Condense(IReadOnlyList<long> values, long top = 40)
    {
        List<(long A, long B)> runs = new List<(long A, long B)>();
        long s = 0, p = 0;
        bool open = false;
        foreach (long v in values)
        {
            if (!open)
            {
                s = p = v;
                open = true;
            }
            else if (v == p + 1)
            {
                p = v;
            }
            else
            {
                runs.Add((s, p));
                s = p = v;
            }
        }

        if (!open)
        {
            throw new PyError(PyErrorKind.NameError, "cannot access local variable 'p' where it is not associated with a value");
        }

        runs.Add((s, p));
        return string.Join(",", runs.Select(r =>
            r.A == r.B ? PyText.Dec(r.A) : r.B == top ? $"{PyText.Dec(r.A)}+" : $"{PyText.Dec(r.A)}-{PyText.Dec(r.B)}"));
    }

    /// <summary><c>fmt_pred</c>, with <c>(bespoke)</c> for no predicate.</summary>
    /// <param name="predicate">The predicate.</param>
    public static string FormatPredicate(CounterPredicate? predicate) => predicate?.ToString() ?? "(bespoke)";

    internal static ModuleRules Extract(ModuleAnalysis r)
    {
        ModuleRules m = new ModuleRules();
        IReadOnlyList<ModuleFunction> fns = r.Exports;

        // slot 0: the briefing copy
        try
        {
            ArraySegment<Insn> body = ModuleAnalysis.SplitTemplate(fns[0].Code);
            long si = FirstImmediate(body, "si, 0x");
            long cx = FirstImmediate(body, "cx, 0x");
            if (!body.Any(i => i.M == "rep movsb"))
            {
                throw PyError.Assertion();
            }

            m.Briefing = (si, cx);
        }
        catch (PyError e) when (e.Kind is PyErrorKind.StopIteration or PyErrorKind.AssertionError or PyErrorKind.ValueError)
        {
            try
            {
                if (ModuleAnalysis.SplitTemplate(fns[0].Code).Count != 0)
                {
                    throw PyError.Value(e.Message);
                }

                m.Briefing = null;          // an empty get_briefing_text
            }
            catch (PyError inner) when (inner.Kind == PyErrorKind.ValueError)
            {
                m.Bespoke.Add(("slot0", e.Message));
            }
        }

        // slots 1 and 2: event rules by concrete execution
        foreach (int slot in (int[])[1, 2])
        {
            IReadOnlyList<(long, string)>? rules;
            try
            {
                rules = RulesFromEventFunction(fns[slot]);
            }
            catch (PyError e) when (e.Kind == PyErrorKind.ValueError)
            {
                rules = null;
                m.Bespoke.Add(($"slot{slot}", e.Message));
            }

            if (slot == 1)
            {
                m.OnEvent = rules;
            }
            else
            {
                m.ScriptHook = rules;
            }
        }

        // slot 3: call helper; jae skip; mov bx,[bp+0x12]; mov byte ss:[bx],1; xor ax,ax; xor dx,dx
        try
        {
            ArraySegment<Insn> body = ModuleAnalysis.SplitTemplate(fns[3].Code);
            string[] pat = body.Select(i => i.I.TrimmedText).ToArray();
            if (body.Count == 0 || pat is ["xor ax, ax", "xor dx, dx"])
            {
                m.CheckWin = null;          // never wins, no message
            }
            else if (body.Count == 6 && body[0].M == "call"
                     && pat[1].StartsWith("jae", StringComparison.Ordinal)
                     && pat[2] == "mov bx, word ptr [bp + 0x12]"
                     && pat[3] == "mov byte ptr ss:[bx], 1"
                     && pat[4] == "xor ax, ax" && pat[5] == "xor dx, dx")
            {
                long helper = body[0].Target;
                if (!r.Helpers.TryGetValue(helper, out ModuleFunction? h))
                {
                    throw PyError.Key(helper);
                }

                (string kind, CounterPredicate? detail) = HelperPredicate(h);
                if (kind == "bespoke")
                {
                    throw PyError.Value($"bespoke predicate helper +{PyText.Hex(helper)}");
                }

                m.CheckWin = new CheckWinRule(helper, kind, detail!);
            }
            else
            {
                throw PyError.Value($"non-template body ({body.Count} insns)");
            }
        }
        catch (PyError e) when (e.Kind == PyErrorKind.ValueError)
        {
            m.CheckWin = null;
            m.Bespoke.Add(("slot3", e.Message));
        }

        // slot 4: every counter combination through the debrief function
        try
        {
            m.Debrief = DebriefDecisionTable(fns[4], r);
        }
        catch (PyError e) when (e.Kind is PyErrorKind.StopIteration or PyErrorKind.KeyError or PyErrorKind.ValueError)
        {
            m.Debrief = null;
            m.Bespoke.Add(("slot4", e.Message));
        }

        return m;
    }

    /// <summary>The first <c>mov</c> whose operands start with <paramref name="head"/>, its value read as hex.</summary>
    private static long FirstImmediate(IEnumerable<Insn> body, string head)
    {
        foreach (Insn i in body)
        {
            if (i.M == "mov" && i.O.StartsWith(head, StringComparison.Ordinal))
            {
                return PyText.Int(i.O[4..], 16);
            }
        }

        throw PyError.StopIteration();
    }

    // ---- slots 1/2 ---------------------------------------------------------------------------

    /// <summary><c>rules_from_event_fn</c>: counter → the arguments 0..40 that bump it (<c>always</c> for all 41).</summary>
    internal static IReadOnlyList<(long Counter, string Arguments)> RulesFromEventFunction(ModuleFunction fn)
    {
        ArraySegment<Insn> body = ModuleAnalysis.SplitTemplate(fn.Code);
        if (body.Count == 0)
        {
            return [];
        }

        Dictionary<int, Insn> byAddress = ModuleFunction.Index(body);
        List<long> order = new List<long>();
        Dictionary<long, List<long>> table = new Dictionary<long, List<long>>();
        List<long> incs = new List<long>();
        for (int arg = 0; arg <= 40; arg++)
        {
            incs.Clear();
            ExecEventBody(body, byAddress, arg, incs);
            foreach (long off in incs)
            {
                if (!table.TryGetValue(off, out List<long>? args))
                {
                    table[off] = args = [];
                    order.Add(off);
                }

                args.Add(arg);
            }
        }

        return order.Select(off => (off, table[off].Count == 41 ? "always" : Condense(table[off]))).ToList();
    }

    /// <summary><c>exec_event_body</c>: the counters one run bumps, in order.</summary>
    private static void ExecEventBody(ArraySegment<Insn> body, Dictionary<int, Insn> byAddress, long arg, List<long> incs)
    {
        long end = body[^1].Address + body[^1].Size;
        long addr = body[0].Address;
        long? cmpVal = null;
        int steps = 0;
        while (addr < end)
        {
            if (!byAddress.TryGetValue((int)addr, out Insn? ins) || addr != (int)addr)
            {
                throw PyError.Value($"jump into mid-instruction +{PyText.Hex(addr)}");
            }

            if (++steps > 200)
            {
                throw PyError.Value("loop in event body");
            }

            string m = ins.M;
            if (m == "cmp")
            {
                if (!(ins.O.StartsWith("word ptr [bp + 6], ", StringComparison.Ordinal)
                      || ins.O.StartsWith("byte ptr [bp + 6], ", StringComparison.Ordinal)))
                {
                    throw PyError.Value($"unsupported cmp: {ins.O}");
                }

                cmpVal = ins.LastImmediate;
                addr += ins.Size;
            }
            else if (IsJcc(m))
            {
                if (cmpVal is not { } rhs)
                {
                    throw PyError.Value("jcc without cmp");
                }

                addr = Taken(m, arg, rhs) ? ins.Target : addr + ins.Size;
            }
            else if (m == "inc")
            {
                if (!ins.O.Contains("ptr cs:[", StringComparison.Ordinal))
                {
                    throw PyError.Value($"unsupported inc: {ins.O}");
                }

                incs.Add(ins.IncOffset);
                addr += ins.Size;
            }
            else if (m == "jmp")
            {
                addr = ins.Target;
            }
            else
            {
                throw PyError.Value($"unsupported insn in event body: {m} {ins.O}");
            }
        }
    }

    // ---- slot 3 ------------------------------------------------------------------------------

    /// <summary><c>helper_counters</c>: the cs: variables a pure counter-predicate helper compares, or null.</summary>
    internal static List<long>? HelperCounters(ModuleFunction h)
    {
        foreach (Insn i in h.Code)
        {
            if (!(i.M is "cmp" or "stc" or "clc" or "ret" or "jmp" || IsJcc(i.M)))
            {
                return null;
            }
        }

        List<long> reads = new List<long>();
        foreach (Insn i in h.Code)
        {
            if (i.M == "cmp")
            {
                if (!i.O.Contains("ptr cs:[", StringComparison.Ordinal))
                {
                    return null;
                }

                long off = i.CsOffset;
                if (!reads.Contains(off))
                {
                    reads.Add(off);
                }
            }
        }

        return reads;
    }

    /// <summary><c>run_counter_helper</c>: the carry flag the helper returns under <paramref name="env"/>.</summary>
    private static bool RunCounterHelper(ModuleFunction h, Env env)
    {
        long addr = h.Code[0].Address;
        (long L, long R)? cmpPair = null;
        for (int step = 0; step < 100; step++)
        {
            if (addr != (int)addr || !h.ByAddress.TryGetValue((int)addr, out Insn? ins))
            {
                throw PyError.Key(addr);
            }

            string m = ins.M;
            if (m == "cmp")
            {
                long l = env.Get(ins.CsOffset);
                cmpPair = (l, ins.LastImmediate);
                addr += ins.Size;
            }
            else if (IsJcc(m))
            {
                if (cmpPair is not { } pair)
                {
                    throw PyError.Type("__main__.<lambda>() argument after * must be an iterable, not NoneType");
                }

                addr = Taken(m, pair.L, pair.R) ? ins.Target : addr + ins.Size;
            }
            else if (m == "stc")
            {
                return true;
            }
            else if (m == "clc")
            {
                return false;
            }
            else if (m == "jmp")
            {
                addr = ins.Target;
            }
            else
            {
                throw PyError.Value($"helper insn {m}");
            }
        }

        throw PyError.Value("runaway helper");
    }

    /// <summary><c>helper_predicate</c>: the helper's predicate over counter values 0..9.</summary>
    private static (string Kind, CounterPredicate? Detail) HelperPredicate(ModuleFunction h)
    {
        List<long>? reads = HelperCounters(h);
        if (reads is null || reads.Count == 0)
        {
            return ("bespoke", null);
        }

        const int Dom = 10;
        Env env = new Env(reads);
        List<int> truth = new List<int>();
        foreach (int code in Combinations(Dom, reads.Count))
        {
            env.Load(code, Dom);
            if (RunCounterHelper(h, env))
            {
                truth.Add(code);
            }
        }

        return ("counters", FitAxes(reads, truth, Dom));
    }

    /// <summary>
    /// <c>fit_axes</c>: the per-counter value sets when their conjunction is exactly the satisfying set,
    /// the satisfying set itself otherwise.  Combinations are encoded base <paramref name="dom"/>,
    /// first counter most significant, so numeric order is the Python tuple order.
    /// </summary>
    private static CounterPredicate FitAxes(IReadOnlyList<long> reads, IReadOnlyCollection<int> truth, int dom)
    {
        int n = reads.Count;
        List<(long, string)> axes = new List<(long, string)>(n);
        bool[][] member = new bool[n][];
        for (int k = 0; k < n; k++)
        {
            SortedSet<long> values = new SortedSet<long>();
            foreach (int code in truth)
            {
                values.Add(Digit(code, k, n, dom));
            }

            string spec = values.Count == 0 ? "never" : Condense(values.ToList(), dom - 1);
            axes.Add((reads[k], spec));
            member[k] = new bool[dom];
            for (int v = 0; v < dom; v++)
            {
                member[k][v] = InSet(spec, v);
            }
        }

        HashSet<int> truthSet = truth as HashSet<int> ?? [.. truth];
        int total = Pow(dom, n);
        int conj = 0;
        bool equal = true;
        for (int code = 0; code < total && equal; code++)
        {
            bool all = true;
            for (int k = 0; k < n && all; k++)
            {
                all = member[k][Digit(code, k, n, dom)];
            }

            if (all)
            {
                conj++;
                equal = truthSet.Contains(code);
            }
        }

        if (equal && conj == truthSet.Count)
        {
            return new CounterPredicate(axes, null);
        }

        List<int[]> table = truthSet.Order().Select(code =>
        {
            int[] tuple = new int[n];
            for (int k = 0; k < n; k++)
            {
                tuple[k] = Digit(code, k, n, dom);
            }

            return tuple;
        }).ToList();
        return new CounterPredicate(null, table);
    }

    /// <summary><c>fit_axes</c>'s <c>in_set</c>.</summary>
    private static bool InSet(string spec, long v)
    {
        if (spec == "never")
        {
            return false;
        }

        foreach (string part in spec.Split(','))
        {
            if (part.EndsWith('+') && v >= PyText.Int(part[..^1], 10))
            {
                return true;
            }

            if (part.Contains('-'))
            {
                string[] ab = part.Split('-');
                if (ab.Length != 2)
                {
                    throw PyError.Value($"too many values to unpack (expected 2, got {ab.Length})");
                }

                if (PyText.Int(ab[0], 10) <= v && v <= PyText.Int(ab[1], 10))
                {
                    return true;
                }
            }

            if (part.Length > 0 && part.All(char.IsAsciiDigit) && v == PyText.Int(part, 10))
            {
                return true;
            }
        }

        return false;
    }

    // ---- slot 4 ------------------------------------------------------------------------------

    /// <summary><c>body_counters</c>: every cs: variable the debrief body (and its helpers) reads.</summary>
    private static List<long> BodyCounters(ArraySegment<Insn> body, ModuleAnalysis r)
    {
        List<long> offs = new List<long>();
        foreach (Insn i in body)
        {
            if (i.M is "cmp" or "mov" or "add" && i.O.Contains("ptr cs:[", StringComparison.Ordinal)
                && !i.O.StartsWith("word ptr cs:[", StringComparison.Ordinal)
                && !i.O.StartsWith("ds, ", StringComparison.Ordinal))
            {
                AddOnce(offs, i.CsOffset);
            }
            else if (i.M == "cmp" && i.O.StartsWith("word ptr cs:[", StringComparison.Ordinal))
            {
                AddOnce(offs, i.CsOffset);
            }
            else if (i.M == "call")
            {
                long target = i.Target;
                if (!r.Helpers.TryGetValue(target, out ModuleFunction? h))
                {
                    throw PyError.Key(target);
                }

                List<long> sub = HelperCounters(h) ?? throw PyError.Value($"bespoke helper +{PyText.Hex(i.Target)}");
                foreach (long o in sub)
                {
                    AddOnce(offs, o);
                }
            }
        }

        return offs;
    }

    /// <summary><c>debrief_decision_table</c>; null for an empty get_debrief_text.</summary>
    private static DebriefTable? DebriefDecisionTable(ModuleFunction fn, ModuleAnalysis r)
    {
        ArraySegment<Insn> body = ModuleAnalysis.SplitTemplate(fn.Code);
        if (body.Count == 0)
        {
            return null;
        }

        List<long> offs = BodyCounters(body, r);
        const int Dom = 7;
        Env env = new Env(offs);
        List<string> keys = new List<string>();
        Dictionary<string, (List<(long?, long?)> Copies, long? Ax, HashSet<int> Combos)> outcomes = new Dictionary<string, (List<(long?, long?)> Copies, long? Ax, HashSet<int> Combos)>();
        foreach (int code in Combinations(Dom, offs.Count))
        {
            env.Load(code, Dom);
            (List<(long?, long?)> copies, long? ax) = RunTextPicker(fn, r, env);
            string key = string.Join(";", copies.Select(c => PyText.Repr(c.Item1) + "," + PyText.Repr(c.Item2))) + "|" + PyText.Repr(ax);
            if (!outcomes.TryGetValue(key, out (List<(long?, long?)> Copies, long? Ax, HashSet<int> Combos) outcome))
            {
                outcomes[key] = outcome = (copies, ax, []);
                keys.Add(key);
            }

            outcome.Combos.Add(code);
        }

        // Most successful return value first; ties keep the order of first appearance.
        List<DebriefRow> rows = keys
            .Select((k, index) => (Outcome: outcomes[k], Index: index))
            .OrderBy(x => -(x.Outcome.Ax is { } a && a != 0 ? a : 0))
            .ThenBy(x => x.Index)
            .Select(x => new DebriefRow(FitAxes(offs, x.Outcome.Combos, Dom), x.Outcome.Copies, x.Outcome.Ax))
            .ToList();
        return new DebriefTable(offs, rows);
    }

    /// <summary><c>run_text_picker</c>: the copies and AX of one run under <paramref name="env"/>.</summary>
    private static (List<(long?, long?)> Copies, long? Ax) RunTextPicker(ModuleFunction fn, ModuleAnalysis r, Env env)
    {
        Insn[] code = fn.Code;
        if (code.Length < 8)
        {
            throw PyError.Index();
        }

        long addr = code[7].Address;
        long? si = null, cx = null, ax = null, al = null;
        (long? L, long R)? cmpPair = null;
        bool? cf = null;
        List<(long?, long?)> copies = new List<(long?, long?)>();
        for (int step = 0; step < 300; step++)
        {
            if (addr != (int)addr || !fn.ByAddress.TryGetValue((int)addr, out Insn? ins))
            {
                throw PyError.Value($"fell off at +{PyText.Hex(addr)}");
            }

            string m = ins.M, o = ins.O;
            if (m == "mov" && o.StartsWith("ds, word ptr cs:[", StringComparison.Ordinal))
            {
                return (copies, ax);
            }
            else if (m == "mov" && o.StartsWith("si, ", StringComparison.Ordinal))
            {
                si = PyText.Int(o[4..], 0);
            }
            else if (m == "mov" && o.StartsWith("cx, ", StringComparison.Ordinal))
            {
                cx = PyText.Int(o[4..], 0);
            }
            else if (m == "mov" && o.StartsWith("ax, ", StringComparison.Ordinal) && !o.Contains("cs", StringComparison.Ordinal))
            {
                ax = PyText.Int(o[4..], 0);
            }
            else if (m == "mov" && o.StartsWith("al, byte ptr cs:[", StringComparison.Ordinal))
            {
                al = env.Get(ins.CsOffset);
            }
            else if (m == "add" && o.StartsWith("al, byte ptr cs:[", StringComparison.Ordinal))
            {
                long add = env.Get(ins.CsOffset);
                al = al is { } a
                    ? a + add
                    : throw PyError.Type("unsupported operand type(s) for +=: 'NoneType' and 'int'");
            }
            else if (m == "xor" && o == "ax, ax")
            {
                ax = 0;
            }
            else if (m == "cmp" && (o.StartsWith("byte ptr cs:[", StringComparison.Ordinal)
                                    || o.StartsWith("word ptr cs:[", StringComparison.Ordinal)))
            {
                long l = env.Get(ins.CsOffset);
                cmpPair = (l, ins.LastImmediate);
                cf = null;
            }
            else if (m == "cmp" && o.StartsWith("al, ", StringComparison.Ordinal))
            {
                cmpPair = (al, ins.LastImmediate);
                cf = null;
            }
            else if (m == "call")
            {
                long target = ins.Target;
                if (!r.Helpers.TryGetValue(target, out ModuleFunction? h))
                {
                    throw PyError.Key(target);
                }

                cf = RunCounterHelper(h, env);
                cmpPair = null;
            }
            else if (IsJcc(m))
            {
                bool taken;
                if (cf is { } carry && m is "jae" or "jb")
                {
                    taken = m == "jae" ? !carry : carry;
                }
                else if (cmpPair is { } pair)
                {
                    taken = pair.L is { } left ? Taken(m, left, pair.R) : TakenOnNone(m);
                }
                else
                {
                    throw PyError.Value("jcc with no flags");
                }

                addr = taken ? ins.Target : addr + ins.Size;
                continue;
            }
            else if (m == "rep movsb")
            {
                copies.Add((si, cx));
            }
            else if (m == "jmp")
            {
                addr = ins.Target;
                continue;
            }
            else if (m is not ("les" or "push" or "pop"))
            {
                throw PyError.Value($"unsupported in picker: {m} {o}");
            }

            addr += ins.Size;
        }

        throw PyError.Value("runaway picker");
    }

    // ---- shared ------------------------------------------------------------------------------

    private static bool IsJcc(string m) => Array.IndexOf(JccMnemonics, m) >= 0;

    /// <summary>The Python tool's JCC table: every condition compares the operands as plain integers.</summary>
    private static bool Taken(string m, long l, long r) => m switch
    {
        "je" => l == r,
        "jne" => l != r,
        "jb" or "jl" => l < r,
        "jae" or "jge" => l >= r,
        "jbe" or "jle" => l <= r,
        _ => l > r,
    };

    /// <summary>The same table with a missing left operand: equality tests work, orderings fail.</summary>
    private static bool TakenOnNone(string m) => m switch
    {
        "je" => false,
        "jne" => true,
        "jb" or "jl" => throw PyError.Type("'<' not supported between instances of 'NoneType' and 'int'"),
        "jae" or "jge" => throw PyError.Type("'>=' not supported between instances of 'NoneType' and 'int'"),
        "jbe" or "jle" => throw PyError.Type("'<=' not supported between instances of 'NoneType' and 'int'"),
        _ => throw PyError.Type("'>' not supported between instances of 'NoneType' and 'int'"),
    };

    private static void AddOnce(List<long> list, long v)
    {
        if (!list.Contains(v))
        {
            list.Add(v);
        }
    }

    private static int Pow(int b, int e)
    {
        long p = 1;
        for (int i = 0; i < e; i++)
        {
            p *= b;
            if (p > MaxCombinations)
            {
                throw PyError.Value($"{e} counters exceed the {MaxCombinations}-combination limit");
            }
        }

        return (int)p;
    }

    private static IEnumerable<int> Combinations(int dom, int n)
    {
        int total = Pow(dom, n);
        for (int code = 0; code < total; code++)
        {
            yield return code;
        }
    }

    private static int Digit(int code, int k, int n, int dom)
    {
        for (int j = n - 1; j > k; j--)
        {
            code /= dom;
        }

        return code % dom;
    }

    internal static string ReprTuples(IReadOnlyList<int[]> tuples) =>
        "[" + string.Join(", ", tuples.Select(t =>
            t.Length == 1 ? $"({t[0]},)" : "(" + string.Join(", ", t) + ")")) + "]";

    /// <summary>A counter environment: the value of each counter the enumeration varies.</summary>
    private sealed class Env(IReadOnlyList<long> counters)
    {
        private readonly long[] _counters = [.. counters];
        private readonly int[] _values = new int[counters.Count];

        public void Load(int code, int dom)
        {
            for (int k = _values.Length - 1; k >= 0; k--)
            {
                _values[k] = code % dom;
                code /= dom;
            }
        }

        public long Get(long counter)
        {
            int k = Array.IndexOf(_counters, counter);
            return k >= 0 ? _values[k] : throw PyError.Key(counter);
        }
    }
}
