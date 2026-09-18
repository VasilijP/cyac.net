using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace CYAC.Formats.EaLib;

/// <summary>
/// The inverse of <see cref="WinRuleSynthesizer"/>: reads a <c>.S</c> trailer module block back into
/// the <see cref="WinRuleModel"/> that synthesizes it, or says why it cannot.
/// </summary>
/// <remarks>
/// <para>
/// This is a rulebook decoder, not a disassembler.  It knows exactly the instruction encodings the
/// synthesizer emits (the encoding table below); any other byte sequence where code is expected means
/// the module is not modelled.  The procedure: sweep the five exported
/// functions and the helpers they call, tile the data region from the references the code makes into
/// it, then structure each function body into the statement IR.
/// </para>
/// <para>
/// A model is only ever returned when <see cref="WinRuleSynthesizer.Synth"/> gives the block back
/// byte for byte.  That gate is part of every public entry point, so the model is a faithful reading of
/// the bytes by construction, and anything the procedure misreads is refused instead of returned.
/// </para>
/// <para>
/// Where the Python tool refuses a shape the synthesizer can emit, this reader accepts it, so that a
/// module authored from a model can be read back: a win-flag head with nothing after it, actor slot 0,
/// a <c>ret</c> inside check_win's pre-body, unreferenced byte variables at the end of the data region,
/// an empty radio text at a byte variable's offset, a module with a briefing but no helpers, a debrief
/// function without copies, and a per-arm debrief function whose only copy is last while a branch
/// still reaches the epilogue.  On everything the Python tool accepts, the two agree.  (Shapes that
/// stay unreadable: helpers nothing calls, texts nothing copies and debrief texts listed out of
/// first-use order, whose bytes no reference in the code accounts for; and actor slots above 63,
/// whose one-byte displacement the CPU reads as negative.)
/// </para>
/// </remarks>
public static class WinRuleExtractor
{
    /// <summary>Why a module block is not modelled.  The first failing check wins.</summary>
    public enum Refusal
    {
        /// <summary>The block has no bytes.</summary>
        EmptyBlock,
        /// <summary>The block ends inside the export table, an instruction or a function.</summary>
        Truncated,
        /// <summary>An export offset points outside the code area of the block.</summary>
        BadExport,
        /// <summary>Bytes where code is expected that are none of the synthesizer's encodings.</summary>
        UnknownInstruction,
        /// <summary>A near call leaves the block.</summary>
        BadCallTarget,
        /// <summary>A function lacks the far frame (prologue, DS save slot, epilogue).</summary>
        BadFrame,
        /// <summary>The data region does not tile into save slot, byte variables and radio texts.</summary>
        BadDataRegion,
        /// <summary>Code reads or writes a byte that is not one of the data region's variables.</summary>
        UnknownVariable,
        /// <summary>A called helper is not a win predicate of either polarity.</summary>
        BadHelper,
        /// <summary>A branch goes backwards or leaves the block of code that encloses it.</summary>
        JumpOutOfRange,
        /// <summary>An instruction sequence that is not a statement of the IR.</summary>
        Unstructured,
        /// <summary>A copied text lies outside the block or breaks the debrief layout rule.</summary>
        BadTextPlacement,
        /// <summary>The recovered model fails validation or the synthesizer refuses it.</summary>
        InvalidModel,
        /// <summary>Re-synthesizing the recovered model does not give the block back.</summary>
        NotReproduced,
        /// <summary>An unexpected exception inside the reader.  Never expected; tests assert its absence.</summary>
        InternalError,
    }

    /// <summary>The outcome of reading one module block.</summary>
    public sealed class Extraction
    {
        private Extraction(WinRuleModel? model, Refusal? refusal, int offset, string detail)
        {
            Model = model;
            RefusedBecause = refusal;
            Offset = offset;
            Detail = detail;
        }

        /// <summary>The model, when the block is modelled; it re-synthesizes to the block exactly.</summary>
        public WinRuleModel? Model { get; }

        /// <summary>The first failing check, when the block is not modelled.</summary>
        public Refusal? RefusedBecause { get; }

        /// <summary>The block offset the failing check looked at (0 for block-level checks).</summary>
        public int Offset { get; }

        /// <summary>A human-readable account of the failing check.</summary>
        public string Detail { get; }

        /// <summary>True when <see cref="Model"/> is set.</summary>
        [MemberNotNullWhen(true, nameof(Model))]
        public bool IsModelled => Model is not null;

        /// <summary>One line: kind, offset and detail; empty when modelled.</summary>
        public string Reason => RefusedBecause is { } r ? $"{r} at +0x{Offset:X}: {Detail}" : string.Empty;

        internal static Extraction Modelled(WinRuleModel model) => new(model, null, 0, string.Empty);

        internal static Extraction Refused(Refusal refusal, int offset, string detail) =>
            new(null, refusal, offset, detail);
    }

    /// <summary>
    /// Reads <paramref name="block"/> (the trailer module payload, export table first).  Never throws.
    /// </summary>
    /// <param name="name">The model's name, e.g. the archive member name.</param>
    /// <param name="block">The module bytes.</param>
    public static Extraction Extract(string name, ReadOnlySpan<byte> block)
    {
        byte[] bytes = block.ToArray();
        WinRuleModel model;
        try
        {
            model = new Reader(name ?? "?", bytes).Read();
        }
        catch (NotModelled e)
        {
            return Extraction.Refused(e.Kind, e.Offset, e.Message);
        }
        catch (Exception e)
        {
            return Extraction.Refused(Refusal.InternalError, 0, $"{e.GetType().Name}: {e.Message}");
        }

        byte[] again;
        try
        {
            again = WinRuleSynthesizer.Synth(model);
        }
        catch (WinRuleSynthesizer.SynthException e)
        {
            return Extraction.Refused(Refusal.InvalidModel, 0, e.Message);
        }
        catch (Exception e)
        {
            return Extraction.Refused(Refusal.InternalError, 0, $"{e.GetType().Name}: {e.Message}");
        }

        int common = Math.Min(again.Length, bytes.Length);
        int first = 0;
        while (first < common && again[first] == bytes[first])
        {
            first++;
        }

        if (first < common || again.Length != bytes.Length)
        {
            return Extraction.Refused(Refusal.NotReproduced, first,
                $"the recovered model synthesizes {again.Length} B against {bytes.Length} B, " +
                $"first difference at +0x{first:X}");
        }

        return Extraction.Modelled(model);
    }

    /// <summary>
    /// Reads <paramref name="block"/>; true with the model when it is modelled, false with the reason
    /// (<see cref="Extraction.Reason"/>) otherwise.  Never throws.
    /// </summary>
    public static bool TryExtract(string name, ReadOnlySpan<byte> block,
                                  [NotNullWhen(true)] out WinRuleModel? model,
                                  [NotNullWhen(false)] out string? reason)
    {
        Extraction result = Extract(name, block);
        model = result.Model;
        reason = result.IsModelled ? null : result.Reason;
        return result.IsModelled;
    }

    // ---- the rulebook's instruction set ------------------------------------------------------

    private enum Op : byte
    {
        // frame
        PushBp, MovBpSp, PushSi, PushDi, SaveDs, MovAxCs, MovDsAx, RestoreDs, PopDi, PopSi, PopBp, Retf,
        // tests and branches
        CmpArgW, CmpClock, CmpArgB, CmpCtr, Jcc, Jmp, Call,
        // statements
        IncCtr, MovSi, MovCx, MovAx, MovDxCs, XorAx, XorDx, LesDi, MovDiArg, MovEsArg, RepMovsb,
        MovBxWinFlag, SetWinFlag, MovEsPool, MovBxActors, MovSiActor, OrActive,
        // helper tail
        Stc, Clc, Ret,
    }

    /// <summary>
    /// One decoded instruction.  <see cref="A"/>: the immediate, memory offset, displacement or
    /// branch target; <see cref="B"/>: the counter compare's immediate, or a jcc's opcode.
    /// </summary>
    private readonly record struct Insn(int Addr, int Size, Op Op, int A = 0, int B = 0)
    {
        public int End => Addr + Size;

        public bool IsCmp => Op is Op.CmpArgW or Op.CmpClock or Op.CmpArgB or Op.CmpCtr;

        public string Cc => CcNames[B - 0x70]!;
    }

    // The opcode bytes of each encoding; the operand bytes that follow are given by OperandBytes.
    private static readonly (byte[] Prefix, Op Op)[] Encodings =
    [
        ([0x55], Op.PushBp),
        ([0x8B, 0xEC], Op.MovBpSp),
        ([0x56], Op.PushSi),
        ([0x57], Op.PushDi),
        ([0x2E, 0x8C, 0x1E], Op.SaveDs),            // mov cs:[w],ds
        ([0x8C, 0xC8], Op.MovAxCs),
        ([0x8E, 0xD8], Op.MovDsAx),
        ([0x2E, 0x8E, 0x1E], Op.RestoreDs),         // mov ds,cs:[w]
        ([0x5F], Op.PopDi),
        ([0x5E], Op.PopSi),
        ([0x5D], Op.PopBp),
        ([0xCB], Op.Retf),
        ([0x83, 0x7E, 0x06], Op.CmpArgW),           // cmp word [bp+6],ib (sign-extended)
        ([0x83, 0x7E, 0x0A], Op.CmpClock),          // cmp word [bp+0xA],ib
        ([0x80, 0x7E, 0x06], Op.CmpArgB),           // cmp byte [bp+6],ib
        ([0x2E, 0x80, 0x3E], Op.CmpCtr),            // cmp byte cs:[w],ib
        ([0xEB], Op.Jmp),                           // jmp short
        ([0xE8], Op.Call),                          // call near rel16
        ([0x2E, 0xFE, 0x06], Op.IncCtr),            // inc byte cs:[w]
        ([0xBE], Op.MovSi),
        ([0xB9], Op.MovCx),
        ([0xB8], Op.MovAx),
        ([0x8C, 0xCA], Op.MovDxCs),
        ([0x33, 0xC0], Op.XorAx),
        ([0x33, 0xD2], Op.XorDx),
        ([0xC4, 0x7E, 0x06], Op.LesDi),             // les di,[bp+6]
        ([0x8B, 0x7E, 0x06], Op.MovDiArg),          // mov di,[bp+6]
        ([0x8E, 0x46, 0x08], Op.MovEsArg),          // mov es,[bp+8]
        ([0xF3, 0xA4], Op.RepMovsb),
        ([0x8B, 0x5E, 0x12], Op.MovBxWinFlag),      // mov bx,[bp+0x12]
        ([0x36, 0xC6, 0x07, 0x01], Op.SetWinFlag),  // mov byte ss:[bx],1
        ([0x8E, 0x46, 0x0C], Op.MovEsPool),         // mov es,[bp+0xC]
        ([0x8B, 0x5E, 0x0E], Op.MovBxActors),       // mov bx,[bp+0xE]
        ([0x36, 0x8B, 0x77], Op.MovSiActor),        // mov si,ss:[bx+db]
        ([0x26, 0x80, 0x4C, 0x3C, 0x01], Op.OrActive),  // or byte es:[si+0x3C],1
        ([0xF9], Op.Stc),
        ([0xF8], Op.Clc),
        ([0xC3], Op.Ret),
    ];

    private static int OperandBytes(Op op) => op switch
    {
        Op.CmpCtr => 3,                                                   // word address + imm8
        Op.SaveDs or Op.RestoreDs or Op.IncCtr or Op.Call
            or Op.MovSi or Op.MovCx or Op.MovAx => 2,                     // word
        Op.CmpArgW or Op.CmpClock or Op.CmpArgB or Op.MovSiActor or Op.Jmp => 1,
        _ => 0,
    };

    // 0x70..0x7F -> condition name; only the ten the synthesizer emits.
    private static readonly string?[] CcNames = BuildCcNames();

    private static string?[] BuildCcNames()
    {
        string?[] names = new string?[16];
        foreach ((string cc, byte opcode) in WinRuleSynthesizer.JccOpcodes)
        {
            names[opcode - 0x70] = cc;
        }

        return names;
    }

    private static Insn Decode(byte[] b, int at)
    {
        byte first = b[at];
        if (first is >= 0x70 and <= 0x7F && CcNames[first - 0x70] is not null)
        {
            Need(b, at, 2);
            return new Insn(at, 2, Op.Jcc, (at + 2 + (sbyte)b[at + 1]) & 0xFFFF, first);
        }

        bool cut = false;
        foreach ((byte[] prefix, Op op) in Encodings)
        {
            int avail = Math.Min(prefix.Length, b.Length - at);
            if (!b.AsSpan(at, avail).SequenceEqual(prefix.AsSpan(0, avail)))
            {
                continue;
            }

            if (avail < prefix.Length)
            {
                cut = true;
                continue;
            }

            int size = prefix.Length + OperandBytes(op);
            Need(b, at, size);
            return op switch
            {
                Op.SaveDs or Op.RestoreDs or Op.IncCtr => new Insn(at, size, op, U16(b, at + 3)),
                Op.CmpArgW or Op.CmpClock => new Insn(at, size, op, (sbyte)b[at + 3]),
                Op.CmpArgB or Op.MovSiActor => new Insn(at, size, op, b[at + 3]),
                Op.CmpCtr => new Insn(at, size, op, U16(b, at + 3), b[at + 5]),
                Op.Jmp => new Insn(at, size, op, (at + 2 + (sbyte)b[at + 1]) & 0xFFFF),
                Op.Call => new Insn(at, size, op, (at + 3 + (short)U16(b, at + 1)) & 0xFFFF),
                Op.MovSi or Op.MovCx or Op.MovAx => new Insn(at, size, op, U16(b, at + 1)),
                _ => new Insn(at, size, op),
            };
        }

        if (cut)
        {
            throw new NotModelled(Refusal.Truncated, at, "an instruction is cut off by the block end");
        }

        throw new NotModelled(Refusal.UnknownInstruction, at,
            $"bytes {Convert.ToHexString(b, at, Math.Min(6, b.Length - at))} are not a rulebook instruction");
    }

    private static void Need(byte[] b, int at, int size)
    {
        if (at + size > b.Length)
        {
            throw new NotModelled(Refusal.Truncated, at, "an instruction is cut off by the block end");
        }
    }

    private static int U16(byte[] b, int at) => b[at] | (b[at + 1] << 8);

    // ---- the reader ------------------------------------------------------------------------------

    private sealed class NotModelled(Refusal kind, int offset, string message) : Exception(message)
    {
        public Refusal Kind { get; } = kind;

        public int Offset { get; } = offset;
    }

    /// <summary>A swept function: its instructions from the entry to the terminating return.</summary>
    private sealed record Fn(int Off, int End, List<Insn> Insns, List<int> Calls);

    // Items at one offset tile in this order.  The Python tool sorts byte before rtext and so refuses
    // an empty radio text that shares its offset with a byte variable; that is the only difference.
    private enum DataKind { Rtext, Byte, Save }

    private sealed class Reader(string name, byte[] blk)
    {
        // The synthesizer's helpers reach their shared tail with short branches, so none is longer
        // than about 140 bytes; a longer sweep is not a helper, and capping it bounds the work a
        // hostile block can cause.
        private const int MaxHelperBytes = 0x100;

        private readonly WinRuleModel _m = new() { Name = name };
        private Fn[] _fns = [];
        private readonly SortedDictionary<int, Fn> _helpers = new();
        private readonly Dictionary<int, int> _helperIndex = new();
        private readonly Dictionary<int, int> _ctrMap = new();
        private readonly Dictionary<int, string> _rtextKey = new();

        public WinRuleModel Read()
        {
            if (blk.Length == 0)
            {
                throw new NotModelled(Refusal.EmptyBlock, 0, "the module block is empty");
            }

            if (blk.Length < 10)
            {
                throw new NotModelled(Refusal.Truncated, 0,
                    $"{blk.Length} B cannot hold the five-entry export table");
            }

            Analyze();
            BuildDataMap();

            int k = 0;
            foreach (int addr in _helpers.Keys)
            {
                _helperIndex[addr] = k++;
            }

            foreach (Fn h in _helpers.Values)
            {
                _m.Helpers.Add(ReadHelper(h));
            }

            ReadFn0();
            _m.Fn1 = ReadEventFn(1);
            _m.Fn2 = ReadEventFn(2);
            ReadFn3();
            ReadFn4();
            return _m;
        }

        // -- sweeps (analyze_module / linear_sweep) --------------------------------------------

        private void Analyze()
        {
            Fn[] fns = new Fn[5];
            SortedSet<int> pending = new SortedSet<int>();
            for (int slot = 0; slot < 5; slot++)
            {
                int off = U16(blk, slot * 2);
                if (off < 10 || off >= blk.Length)
                {
                    throw new NotModelled(Refusal.BadExport, slot * 2,
                        $"export {slot} points at +0x{off:X}, outside the code area (+0xA..+0x{blk.Length:X})");
                }

                fns[slot] = Sweep(off, $"export {slot}", int.MaxValue);
                pending.UnionWith(fns[slot].Calls);
            }

            _fns = fns;

            // Helpers are every call target, transitively, as in the tool; a helper that itself
            // calls is refused later by ReadHelper.
            while (pending.Count > 0)
            {
                int target = pending.Min;
                pending.Remove(target);
                if (_helpers.ContainsKey(target))
                {
                    continue;
                }

                if (target < 10 || target >= blk.Length)
                {
                    throw new NotModelled(Refusal.BadCallTarget, target,
                        $"a near call targets +0x{target:X}, outside the code area");
                }

                Fn h = Sweep(target, $"helper +0x{target:X}", MaxHelperBytes);
                _helpers[target] = h;
                foreach (int c in h.Calls)
                {
                    if (!_helpers.ContainsKey(c))
                    {
                        pending.Add(c);
                    }
                }
            }
        }

        /// <summary>
        /// Decodes forward from <paramref name="off"/>, past any return that an earlier branch jumps
        /// beyond, up to the first return nothing jumps past.
        /// </summary>
        private Fn Sweep(int off, string what, int maxBytes)
        {
            List<Insn> insns = new List<Insn>();
            List<int> calls = new List<int>();
            int addr = off;
            int reach = off;
            while (addr < blk.Length)
            {
                if (addr - off > maxBytes)
                {
                    throw new NotModelled(Refusal.BadHelper, off,
                        $"{what} runs longer than a short-branch helper can be");
                }

                Insn ins = Decode(blk, addr);
                insns.Add(ins);
                if (ins.Op is Op.Jcc or Op.Jmp)
                {
                    reach = Math.Max(reach, ins.A);
                }
                else if (ins.Op == Op.Call)
                {
                    calls.Add(ins.A);
                }

                if (ins.Op is Op.Ret or Op.Retf && ins.End > reach)
                {
                    return new Fn(off, ins.End, insns, calls);
                }

                addr = ins.End;
            }

            throw new NotModelled(Refusal.Truncated, off, $"{what} runs off the block end");
        }

        // -- frame (split_template) --------------------------------------------------------------

        private static readonly Op[] Prologue =
            [Op.PushBp, Op.MovBpSp, Op.PushSi, Op.PushDi, Op.SaveDs, Op.MovAxCs, Op.MovDsAx];

        private static readonly Op[] EpilogueTail = [Op.PopDi, Op.PopSi, Op.PopBp, Op.Retf];

        /// <summary>The body between the prologue and the epilogue, and the epilogue's address.</summary>
        private (List<Insn> Body, int BodyEnd) SplitFrame(int slot)
        {
            List<Insn> insns = _fns[slot].Insns;
            int off = _fns[slot].Off;
            if (insns.Count < Prologue.Length)
            {
                throw new NotModelled(Refusal.BadFrame, off, $"export {slot} is too short for the far frame");
            }

            for (int i = 0; i < Prologue.Length; i++)
            {
                if (insns[i].Op != Prologue[i])
                {
                    throw new NotModelled(Refusal.BadFrame, insns[i].Addr,
                        $"export {slot} lacks the far prologue (instruction {i})");
                }
            }

            int save = insns[4].A;
            if (save != _m.SaveOff)
            {
                throw new NotModelled(Refusal.BadFrame, insns[4].Addr,
                    $"export {slot} saves DS at +0x{save:X}, export 0 at +0x{_m.SaveOff:X}");
            }

            for (int k = Prologue.Length; k < insns.Count; k++)
            {
                if (insns[k].Op != Op.RestoreDs || insns[k].A != save)
                {
                    continue;
                }

                // The sweep ends at the first return nothing jumps past, so a body that stays inside
                // its function leaves exactly the epilogue's four closing instructions after it.
                bool tailOk = insns.Count == k + 1 + EpilogueTail.Length;
                for (int t = 0; tailOk && t < EpilogueTail.Length; t++)
                {
                    tailOk = insns[k + 1 + t].Op == EpilogueTail[t];
                }

                if (!tailOk)
                {
                    int epilogueEnd = insns[k].End + EpilogueTail.Length;
                    foreach (Insn ins in insns.Take(k))
                    {
                        if (ins.Op is Op.Jcc or Op.Jmp && ins.A >= epilogueEnd)
                        {
                            throw new NotModelled(Refusal.JumpOutOfRange, ins.Addr,
                                $"export {slot} branches to +0x{ins.A:X}, past its own end");
                        }
                    }

                    throw new NotModelled(Refusal.BadFrame, insns[k].Addr,
                        $"export {slot}: the DS restore is not followed by pop di/si/bp and retf alone");
                }

                return (insns.GetRange(Prologue.Length, k - Prologue.Length), insns[k].Addr);
            }

            throw new NotModelled(Refusal.BadFrame, off, $"export {slot} has no DS-restoring epilogue");
        }

        // -- data region (_build_data_map) -----------------------------------------------------

        private void BuildDataMap()
        {
            List<Insn> fn0 = _fns[0].Insns;
            if (fn0.Count <= 4 || fn0[4].Op != Op.SaveDs)
            {
                throw new NotModelled(Refusal.BadFrame, _fns[0].Off,
                    "export 0 does not save DS in its fifth instruction");
            }

            int saveOff = fn0[4].A;
            _m.SaveOff = saveOff;

            int firstCode = _fns.Min(f => f.Off);
            foreach (int h in _helpers.Keys)
            {
                firstCode = Math.Min(firstCode, h);
            }

            // Code follows the data region unless the module has no helpers, in which case the
            // briefing text comes straight after it, unaligned.
            int dataEnd = firstCode;
            bool codeFollows = true;
            if (_helpers.Count == 0 && fn0.Count > 7 && fn0[7].Op == Op.MovSi
                && fn0[7].A >= 10 && fn0[7].A < firstCode)
            {
                dataEnd = fn0[7].A;
                codeFollows = false;
            }

            SortedSet<int> byteOffs = new SortedSet<int>();
            SortedSet<int> rtextOffs = new SortedSet<int>();
            foreach (Fn f in _fns.Concat(_helpers.Values))
            {
                Insn? prev = null;
                foreach (Insn ins in f.Insns)
                {
                    if (ins.Op is Op.IncCtr or Op.CmpCtr && ins.A >= 0x0A && ins.A < dataEnd)
                    {
                        byteOffs.Add(ins.A);
                    }

                    if (ins.Op == Op.MovDxCs && prev is { Op: Op.MovAx } ax)
                    {
                        // The end itself is allowed: an empty radio text declared last sits there.
                        if (ax.A < 0x0A || ax.A > dataEnd)
                        {
                            throw new NotModelled(Refusal.BadDataRegion, ax.Addr,
                                $"a radio message points at +0x{ax.A:X}, outside the data region " +
                                $"(+0xA..+0x{dataEnd:X})");
                        }

                        rtextOffs.Add(ax.A);
                    }

                    prev = ins;
                }
            }

            List<(int Off, DataKind Kind)> items = new List<(int Off, DataKind Kind)> { (saveOff, DataKind.Save) };
            items.AddRange(byteOffs.Select(o => (o, DataKind.Byte)));
            items.AddRange(rtextOffs.Select(o => (o, DataKind.Rtext)));
            items.Sort();

            List<(int Off, DataKind Kind, byte[]? Text)> layout = new List<(int Off, DataKind Kind, byte[]? Text)>();
            int pos = 0x0A;
            for (int k = 0; k < items.Count; k++)
            {
                (int off, DataKind kind) = items[k];
                if (off != pos)
                {
                    // A zero-filled hole is declared-but-unreferenced byte variables.
                    if (off > pos && AllZero(pos, off))
                    {
                        for (int p = pos; p < off; p++)
                        {
                            layout.Add((p, DataKind.Byte, null));
                        }

                        pos = off;
                    }
                    else
                    {
                        throw new NotModelled(Refusal.BadDataRegion, Math.Min(pos, off),
                            $"the data region has an unexplained span +0x{pos:X}..+0x{off:X}");
                    }
                }

                switch (kind)
                {
                    case DataKind.Save:
                        if (off + 2 > blk.Length || blk[off] != 0 || blk[off + 1] != 0)
                        {
                            throw new NotModelled(Refusal.BadDataRegion, off, "the DS save slot is not zero-initialised");
                        }

                        pos = off + 2;
                        layout.Add((off, kind, null));
                        break;

                    case DataKind.Byte:
                        if (blk[off] != 0)
                        {
                            throw new NotModelled(Refusal.BadDataRegion, off,
                                $"byte variable +0x{off:X} is not zero-initialised");
                        }

                        pos = off + 1;
                        layout.Add((off, kind, null));
                        break;

                    default:
                    {
                        // A radio text runs to the next item or to the end of the region, less the
                        // alignment pad the synthesizer adds back in front of odd-ending data.
                        int end = k + 1 < items.Count ? items[k + 1].Off : dataEnd;
                        int len = end - off;
                        if (end == dataEnd && codeFollows && len > 0 && blk[end - 1] == 0x90
                            && ((end - 1) & 1) != 0)
                        {
                            len--;
                        }

                        layout.Add((off, kind, blk.AsSpan(off, len).ToArray()));
                        pos = off + len;
                        if (pos != end && !(end == dataEnd && end - pos == 1))
                        {
                            throw new NotModelled(Refusal.BadDataRegion, pos, "a radio text does not tile");
                        }

                        break;
                    }
                }
            }

            if (pos < dataEnd)
            {
                // Trailing zeros are unreferenced byte variables too, ahead of an optional pad.
                int z = pos;
                while (z < dataEnd && blk[z] == 0)
                {
                    z++;
                }

                if (z == dataEnd || (codeFollows && z == dataEnd - 1 && blk[z] == 0x90))
                {
                    for (int p = pos; p < z; p++)
                    {
                        layout.Add((p, DataKind.Byte, null));
                    }

                    pos = z;
                }
            }

            if (pos != dataEnd && !(codeFollows && dataEnd - pos == 1 && blk[pos] == 0x90))
            {
                throw new NotModelled(Refusal.BadDataRegion, Math.Min(pos, dataEnd),
                    $"the data region does not reach the code (+0x{pos:X} vs +0x{dataEnd:X})");
            }

            List<int> byteVarOffs = new List<int>();
            foreach ((int off, DataKind kind, byte[]? text) in layout)
            {
                if (kind == DataKind.Byte)
                {
                    _ctrMap[off] = byteVarOffs.Count;
                    byteVarOffs.Add(off);
                }
                else if (kind == DataKind.Rtext)
                {
                    string key = $"r{_rtextKey.Count}";
                    _rtextKey[off] = key;
                    _m.RadioTexts[key] = Latin1(text!);
                }
            }

            _m.NCounters = byteVarOffs.Count;
            bool templateLayout = saveOff == 0x0A && _rtextKey.Count == 0
                                  && byteVarOffs.Select((o, i) => o == 0x0C + i).All(x => x);
            if (!templateLayout)
            {
                _m.DataLayout = layout.Select(item => item.Kind switch
                {
                    DataKind.Save => new WinRuleDataItem { Kind = "save" },
                    DataKind.Byte => new WinRuleDataItem { Kind = "byte" },
                    _ => new WinRuleDataItem { Kind = "rtext", Key = _rtextKey[item.Off] },
                }).ToList();
            }
        }

        private bool AllZero(int from, int to)
        {
            if (to > blk.Length)
            {
                return false;
            }

            for (int p = from; p < to; p++)
            {
                if (blk[p] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        // -- helpers (_extract_helper) ---------------------------------------------------------

        private WinRuleHelper ReadHelper(Fn h)
        {
            List<Insn> insns = h.Insns;
            int k = insns.FindIndex(i => i.Op is Op.Stc or Op.Clc);
            if (k < 0)
            {
                throw new NotModelled(Refusal.BadHelper, h.Off, "the helper never sets the carry flag");
            }

            Op[] tail = insns.Skip(k).Select(i => i.Op).ToArray();
            string polarity;
            if (tail.SequenceEqual([Op.Stc, Op.Ret, Op.Clc, Op.Ret]))
            {
                polarity = "stc_first";
            }
            else if (tail.SequenceEqual([Op.Clc, Op.Ret, Op.Stc, Op.Ret]))
            {
                polarity = "clc_first";
            }
            else
            {
                throw new NotModelled(Refusal.BadHelper, insns[k].Addr,
                    "the helper does not end in stc/ret/clc/ret or clc/ret/stc/ret");
            }

            int alternate = insns[k + 2].Addr;
            WinRuleHelper helper = new WinRuleHelper { Polarity = polarity };
            for (int i = 0; i < k; i += 2)
            {
                Insn cmp = insns[i];
                Insn jcc = insns[i + 1];
                if (!cmp.IsCmp || jcc.Op != Op.Jcc)
                {
                    throw new NotModelled(Refusal.BadHelper, cmp.Addr, "the helper head is not (cmp, jcc)*");
                }

                if (jcc.A != alternate)
                {
                    throw new NotModelled(Refusal.BadHelper, jcc.Addr,
                        "a helper test does not branch to the alternate return");
                }

                helper.Tests.Add(ReadTest(cmp, jcc));
            }

            return helper;
        }

        private WinRuleTest ReadTest(Insn cmp, Insn jcc)
        {
            WinRuleTest test = new WinRuleTest { Cc = jcc.Cc };
            switch (cmp.Op)
            {
                case Op.CmpArgW:
                    test.Var = "arg_w";
                    test.Imm = cmp.A;
                    break;
                case Op.CmpClock:
                    test.Var = "clock";
                    test.Imm = cmp.A;
                    break;
                case Op.CmpArgB:
                    test.Var = "arg_b";
                    test.Imm = cmp.A;
                    break;
                default:
                    test.Var = "ctr";
                    test.Counter = Counter(cmp);
                    test.Imm = cmp.B;
                    break;
            }

            return test;
        }

        private int Counter(Insn ins)
        {
            if (!_ctrMap.TryGetValue(ins.A, out int ordinal))
            {
                throw new NotModelled(Refusal.UnknownVariable, ins.Addr,
                    $"cs:[0x{ins.A:X}] is not a byte variable of the data region");
            }

            return ordinal;
        }

        // -- fn0: the briefing copy ------------------------------------------------------------

        private void ReadFn0()
        {
            (List<Insn> body, int bodyEnd) = SplitFrame(0);
            if (body.Count == 0)
            {
                _m.Fn0Style = null;
                _m.Briefing = null;
                return;
            }

            string style = body.Any(i => i.Op == Op.LesDi) ? "les" : "moves";
            (int Src, int Len)? copied = null;
            BodyParser parser = new BodyParser(this, body, bodyEnd, style, (src, len, at) =>
            {
                copied = (src, len);
                return "b";
            });
            int end = parser.ParseCopy(body[0].Addr, out _);
            if (end != bodyEnd)
            {
                throw new NotModelled(Refusal.Unstructured, end,
                    "get_briefing_text does more than copy the briefing");
            }

            (int s, int l) = copied!.Value;
            _m.Fn0Style = style;
            _m.Briefing = Latin1(TextBytes(s, l, "briefing"));
        }

        // -- fn1 / fn2: the event rules --------------------------------------------------------

        private List<WinRuleStmt> ReadEventFn(int slot)
        {
            (List<Insn> body, int bodyEnd) = SplitFrame(slot);
            if (body.Count == 0)
            {
                return [];
            }

            return new BodyParser(this, body, bodyEnd, null, NoText).Parse(body[0].Addr, bodyEnd);
        }

        private static string NoText(int src, int len, int at) =>
            throw new NotModelled(Refusal.Unstructured, at, "a text copy outside the briefing and debrief exports");

        // -- fn3: check_win --------------------------------------------------------------------

        private void ReadFn3()
        {
            (List<Insn> body, int bodyEnd) = SplitFrame(3);

            // The win-flag head: call helper; jae L; mov bx,[bp+0x12]; mov byte ss:[bx],1; L:
            int headAt = -1;
            for (int i = 0; i + 3 < body.Count; i++)
            {
                if (body[i].Op == Op.Call && body[i + 1].Op == Op.Jcc && body[i + 1].Cc == "jae"
                    && body[i + 2].Op == Op.MovBxWinFlag && body[i + 3].Op == Op.SetWinFlag
                    && body[i + 1].A == body[i + 3].End)
                {
                    headAt = i;
                    break;
                }
            }

            List<Insn> pre;
            List<Insn> extra;
            if (headAt >= 0)
            {
                _m.Fn3 = new WinRuleFn3 { Kind = "template", Helper = _helperIndex[body[headAt].A] };
                pre = body.GetRange(0, headAt);
                extra = body.GetRange(headAt + 4, body.Count - headAt - 4);
            }
            else
            {
                _m.Fn3 = new WinRuleFn3 { Kind = "never" };
                pre = [];
                extra = body;
            }

            // A `ret` anywhere in check_win jumps to the epilogue, the pre-body's included.
            _m.Fn3.Pre = pre.Count == 0
                ? []
                : new BodyParser(this, pre, bodyEnd, null, NoText).Parse(pre[0].Addr, pre[^1].End);
            _m.Fn3.Extra = extra.Count == 0
                ? []
                : new BodyParser(this, extra, bodyEnd, null, NoText).Parse(extra[0].Addr, extra[^1].End);
        }

        // -- fn4: the debrief decision table ---------------------------------------------------

        private void ReadFn4()
        {
            (List<Insn> all, int bodyEnd) = SplitFrame(4);
            string? style;
            List<Insn> body;
            int stop;
            if (all.Count == 0)
            {
                style = null;
                body = all;
                stop = bodyEnd;
            }
            else
            {
                List<int> les = new List<int>();
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].Op == Op.LesDi)
                    {
                        les.Add(i);
                    }
                }

                if (les.Count == 0)
                {
                    // No copy at all: only the per-arm style emits no destination load.
                    style = "per_arm";
                    body = all;
                    stop = bodyEnd;
                }
                else if (les[0] == 0)
                {
                    style = "hoisted";
                    body = all.GetRange(1, all.Count - 1);
                    stop = bodyEnd;
                }
                else if (les.Count == 1 && les[0] == all.Count - 2 && all[^1].Op == Op.RepMovsb
                         && !all.Any(i => i.Op is Op.Jcc or Op.Jmp && i.A == bodyEnd))
                {
                    // One shared `les di; rep movsb` tail; `ret` jumps to it.  A per-arm body whose
                    // only copy comes last looks the same, but then some branch may reach past the
                    // copy to the epilogue, which no shared-tail body does.
                    style = "shared_tail";
                    body = all.GetRange(0, all.Count - 2);
                    stop = all[^2].Addr;
                }
                else
                {
                    foreach (int i in les)
                    {
                        if (all[i - 1].Op != Op.MovCx)
                        {
                            throw new NotModelled(Refusal.Unstructured, all[i].Addr,
                                "get_debrief_text loads its destination in an unrecognised place");
                        }
                    }

                    style = "per_arm";
                    body = all;
                    stop = bodyEnd;
                }
            }

            _m.Fn4Style = style;
            if (body.Count == 0)
            {
                return;
            }

            Dictionary<(int, int), string> registry = new Dictionary<(int, int), string>();
            List<(int Src, int Len, int At)> order = new List<(int Src, int Len, int At)>();
            BodyParser parser = new BodyParser(this, body, stop, style, (src, len, at) =>
            {
                if (!registry.TryGetValue((src, len), out string? key))
                {
                    key = $"d{registry.Count}";
                    registry[(src, len)] = key;
                    order.Add((src, len, at));
                }

                return key;
            });
            _m.Fn4 = parser.Parse(body[0].Addr, stop);

            // Debrief texts sit right after check_win, tightly packed in first-use order.
            int expect = _fns[3].End;
            foreach ((int src, int len, int at) in order)
            {
                if (src != expect)
                {
                    throw new NotModelled(Refusal.BadTextPlacement, at,
                        $"a debrief text sits at +0x{src:X}; the layout rule puts it at +0x{expect:X}");
                }

                _m.DebriefTexts[registry[(src, len)]] = Latin1(TextBytes(src, len, "debrief text"));
                expect = src + len;
            }
        }

        // -- shared -----------------------------------------------------------------------------

        private byte[] TextBytes(int src, int len, string what)
        {
            if (src < 10 || src + len > blk.Length)
            {
                throw new NotModelled(Refusal.BadTextPlacement, src,
                    $"the {what} (+0x{src:X}, {len} B) is not inside the block");
            }

            return blk.AsSpan(src, len).ToArray();
        }

        private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

        /// <summary>Linear instructions to statement IR (the tool's <c>_BodyParser</c>).</summary>
        private sealed class BodyParser
        {
            private readonly Reader _r;
            private readonly List<Insn> _insns;
            private readonly Dictionary<int, int> _at = new();
            private readonly int _end;
            private readonly string? _style;
            private readonly Func<int, int, int, string> _textKey;

            /// <param name="reader">The module being read.</param>
            /// <param name="insns">The instructions of this body, contiguous.</param>
            /// <param name="end">Where <c>ret</c> jumps: the epilogue, or the shared copy tail.</param>
            /// <param name="style">The copy style of this function (null: no destination handling).</param>
            /// <param name="textKey">
            /// Names the text a copy refers to (source, length, instruction address); refuses where
            /// the function may not copy.
            /// </param>
            public BodyParser(Reader reader, List<Insn> insns, int end, string? style,
                              Func<int, int, int, string> textKey)
            {
                _r = reader;
                _insns = insns;
                _end = end;
                _style = style;
                _textKey = textKey;
                for (int i = 0; i < insns.Count; i++)
                {
                    _at[insns[i].Addr] = i;
                }
            }

            private Insn? At(int addr) => _at.TryGetValue(addr, out int i) ? _insns[i] : null;

            public List<WinRuleStmt> Parse(int start, int stop)
            {
                List<WinRuleStmt> stmts = new List<WinRuleStmt>();
                int addr = start;
                while (addr < stop)
                {
                    if (At(addr) is not { } ins)
                    {
                        throw new NotModelled(Refusal.Unstructured, addr,
                            "control flow lands between instructions or outside the function");
                    }

                    switch (ins.Op)
                    {
                        case Op.CmpArgW or Op.CmpClock or Op.CmpArgB or Op.CmpCtr:
                        {
                            (List<WinRuleTest> tests, int target, int next, int jccAt) = GatherTests(addr);
                            CheckTarget(target, next, stop, jccAt);
                            List<WinRuleStmt> body = Parse(next, target);
                            stmts.Add(WinRuleStmt.If(tests, body));
                            addr = target;
                            break;
                        }

                        case Op.Call:
                        {
                            if (At(ins.End) is not { Op: Op.Jcc } jae || jae.Cc != "jae")
                            {
                                throw new NotModelled(Refusal.Unstructured, ins.End,
                                    "a helper call is not followed by jae");
                            }

                            if (!_r._helperIndex.TryGetValue(ins.A, out int helper))
                            {
                                throw new NotModelled(Refusal.BadHelper, ins.Addr,
                                    $"a call to +0x{ins.A:X}, which is not a helper");
                            }

                            CheckTarget(jae.A, jae.End, stop, jae.Addr);
                            List<WinRuleStmt> body = Parse(jae.End, jae.A);
                            stmts.Add(WinRuleStmt.IfHelper(helper, body));
                            addr = jae.A;
                            break;
                        }

                        case Op.IncCtr:
                            stmts.Add(WinRuleStmt.Inc(_r.Counter(ins)));
                            addr = ins.End;
                            break;

                        case Op.MovSi:
                            addr = ParseCopy(addr, out WinRuleStmt copy);
                            stmts.Add(copy);
                            break;

                        case Op.MovAx:
                            if (At(ins.End) is { Op: Op.MovDxCs } dx)
                            {
                                if (!_r._rtextKey.TryGetValue(ins.A, out string? key))
                                {
                                    throw new NotModelled(Refusal.Unstructured, ins.Addr,
                                        $"a radio message at +0x{ins.A:X} is not a radio text of the data region");
                                }

                                stmts.Add(WinRuleStmt.Msg(key));
                                addr = dx.End;
                            }
                            else if (ins.A == 1)
                            {
                                stmts.Add(WinRuleStmt.Ax(1));
                                addr = ins.End;
                            }
                            else
                            {
                                throw new NotModelled(Refusal.Unstructured, ins.Addr,
                                    $"mov ax,0x{ins.A:X} is neither a return value nor a radio message");
                            }

                            break;

                        case Op.XorAx:
                            stmts.Add(WinRuleStmt.Ax(0));
                            addr = ins.End;
                            break;

                        case Op.XorDx:
                            stmts.Add(WinRuleStmt.Dx0());
                            addr = ins.End;
                            break;

                        case Op.MovEsPool:
                            addr = ParseActivate(ins, out WinRuleStmt activate);
                            stmts.Add(activate);
                            break;

                        case Op.Jmp:
                            if (ins.A != _end)
                            {
                                throw new NotModelled(Refusal.Unstructured, ins.Addr,
                                    $"jmp to +0x{ins.A:X} is not a return (+0x{_end:X})");
                            }

                            stmts.Add(WinRuleStmt.Ret());
                            addr = ins.End;
                            break;

                        default:
                            throw new NotModelled(Refusal.Unstructured, ins.Addr,
                                $"{ins.Op} does not start a statement");
                    }
                }

                return stmts;
            }

            private static void CheckTarget(int target, int from, int stop, int at)
            {
                // Forward only (the synthesizer's labels all follow their branches), and within the
                // enclosing body; this also bounds the nesting depth by the short-branch reach.
                if (target < from || target > stop)
                {
                    throw new NotModelled(Refusal.JumpOutOfRange, at,
                        $"a branch to +0x{target:X} leaves its block (+0x{from:X}..+0x{stop:X})");
                }
            }

            /// <summary>Consecutive (cmp, jcc) pairs sharing one fail target form one <c>if</c>.</summary>
            private (List<WinRuleTest> Tests, int Target, int Next, int FirstJcc) GatherTests(int addr)
            {
                List<WinRuleTest> tests = new List<WinRuleTest>();
                int? target = null;
                int firstJcc = addr;
                while (At(addr) is { IsCmp: true } cmp)
                {
                    if (At(cmp.End) is not { Op: Op.Jcc } jcc)
                    {
                        throw new NotModelled(Refusal.Unstructured, cmp.End, "a compare is not followed by a jcc");
                    }

                    if (target is null)
                    {
                        target = jcc.A;
                        firstJcc = jcc.Addr;
                    }
                    else if (jcc.A != target)
                    {
                        break;      // a different fail target opens a nested if
                    }

                    tests.Add(_r.ReadTest(cmp, jcc));
                    addr = jcc.End;
                }

                return (tests, target!.Value, addr, firstJcc);
            }

            private Insn Expect(int addr, Op op, string what)
            {
                if (At(addr) is { } ins && ins.Op == op)
                {
                    return ins;
                }

                throw new NotModelled(Refusal.Unstructured, addr, $"expected {what}");
            }

            /// <summary>One text copy in this function's style; returns the address after it.</summary>
            public int ParseCopy(int addr, out WinRuleStmt stmt)
            {
                int at = addr;
                Insn si = Expect(addr, Op.MovSi, "mov si,text");
                addr = si.End;
                if (_style == "moves")
                {
                    addr = Expect(addr, Op.MovDiArg, "mov di,[bp+6]").End;
                    addr = Expect(addr, Op.MovEsArg, "mov es,[bp+8]").End;
                }

                Insn cx = Expect(addr, Op.MovCx, "mov cx,length");
                addr = cx.End;
                if (_style is "les" or "per_arm")
                {
                    addr = Expect(addr, Op.LesDi, "les di,[bp+6]").End;
                }

                if (_style is "les" or "moves" or "per_arm" or "hoisted")
                {
                    addr = Expect(addr, Op.RepMovsb, "rep movsb").End;
                }

                stmt = WinRuleStmt.Copy(_textKey(si.A, cx.A, at));
                return addr;
            }

            /// <summary>es=[bp+0xC]; bx=[bp+0xE]; then per slot k: si=ss:[bx+2k]; or es:[si+0x3C],1.</summary>
            private int ParseActivate(Insn head, out WinRuleStmt stmt)
            {
                int addr = Expect(head.End, Op.MovBxActors, "mov bx,[bp+0xE]").End;
                List<int> slots = new List<int>();
                while (At(addr) is { Op: Op.MovSiActor } si)
                {
                    // The displacement is signed on the CPU; only 0..0x7F reads as a slot index.
                    if ((si.A & 1) != 0 || si.A > 0x7F)
                    {
                        throw new NotModelled(Refusal.Unstructured, si.Addr,
                            $"actor-table displacement 0x{si.A:X} is not an even non-negative index");
                    }

                    addr = Expect(si.End, Op.OrActive, "or byte es:[si+0x3C],1").End;
                    slots.Add(si.A / 2);
                }

                if (slots.Count == 0)
                {
                    throw new NotModelled(Refusal.Unstructured, head.Addr, "an actor activation with no slots");
                }

                stmt = new WinRuleStmt { Op = "activate", Slots = slots };
                return addr;
            }
        }
    }
}
