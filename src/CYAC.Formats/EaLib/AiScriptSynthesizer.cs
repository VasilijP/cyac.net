using System.Text;

namespace CYAC.Formats.EaLib;

// ---------------------------------------------------------------------------
// EXTRACT / VALIDATE / SYNTH for the authored AI script.
//
// The proof standard:
//     Synth(Extract(payload)) == payload, byte for byte, for ALL 268 shipped
//     scripts, with ZERO per-script special cases.
// `--selftest-scriptsynth` stage S-A asserts exactly that against sources/2b.lib.
//
// WHERE THE RULES COME FROM.  Every refusal below is an engine fact with a
// citation, not editor taste:
//   * 712-B cap        — the parser stages the payload in [bp-0x2C8] (image@0x09D2D)
//   * terminal suspend — 268/268 shipped scripts end with 0xFF (image@0x0578E)
//   * slots 0..12      — [0xEE5A] is u16[13]
//   * refs at-or-before the owner — place coords are patched in as the file is
//     PARSED (image@0x094EF), so a forward reference reads what was registered
//     so far; own-slot refs are legal (registration precedes the patch at the
//     same object close — P16 ESCORT calibration)
//   * loop needs a yield first — 0xDD only rewinds the PC; without a
//     phase-setting (frame-yielding) op before it the interpreter spins forever
//     inside one frame
//   * refused opcodes — the patcher/VM width disagreement law
// ---------------------------------------------------------------------------

/// <summary>Extract / validate / synthesize authored AI scripts (python: ai_script_synth).</summary>
public static class AiScriptSynthesizer
{
    // =======================================================================
    // wire primitives
    // =======================================================================

    /// <summary>byte3 -> world units (i24): units = LE24(b0,b1,b2), signed.</summary>
    private static int UnitsFromWire(ReadOnlySpan<byte> b)
    {
        int v = b[0] | (b[1] << 8) | (b[2] << 16);
        return v >= 0x800000 ? v - 0x1000000 : v;
    }

    private static void UnitsToWire(int units, List<byte> into)
    {
        if (units < AiScriptOps.I24Min || units > AiScriptOps.I24Max)
            throw new AiScriptException($"coordinate {units} outside i24 world-unit range");
        int v = units & 0xFFFFFF;
        into.Add((byte)(v & 0xFF));
        into.Add((byte)((v >> 8) & 0xFF));
        into.Add((byte)((v >> 16) & 0xFF));
    }

    private static int U16(byte[] p, int off) => p[off] | (p[off + 1] << 8);

    // =======================================================================
    // extract: raw payload bytes -> step model
    // =======================================================================

    /// <summary>
    /// Parse a shipping (PRE-patch) payload into the authoring model.  Throws
    /// <see cref="AiScriptException"/> for anything the author could not have
    /// written: a refused opcode, an opcode outside the alphabet, a truncated
    /// stream, an unterminated radio string.
    /// </summary>
    public static AiScriptModel Extract(byte[] p)
    {
        AiScriptModel model = new AiScriptModel();
        int i = 0;
        while (i < p.Length)
        {
            if (i + 3 > p.Length)
                throw new AiScriptException($"payload truncated at +0x{i:X}");
            int delay = U16(p, i);
            byte code = p[i + 2];
            i += 3;
            if (AiScriptOps.Refused.TryGetValue(code, out (string Vm, string Why) refused))
                throw new AiScriptException(
                    $"op 0x{code:X2} {refused.Vm} at +0x{i - 1:X} is not authorable: {refused.Why}");
            (AiScriptOp? op, int? place) = AiScriptOps.Resolve(code);
            if (op is null)
                throw new AiScriptException(
                    $"op 0x{code:X2} at +0x{i - 1:X} is outside the authorable alphabet");

            AiScriptStep step = new AiScriptStep { Delay = delay, Op = op.Name, Place = place };
            foreach (AiScriptField f in op.Fields)
            {
                if (f.Kind == AiOperandKind.Place) continue;      // folded into the opcode byte
                if (f.Kind == AiOperandKind.Text)
                {
                    int j = Array.IndexOf(p, (byte)0, i);
                    if (j < 0)
                        throw new AiScriptException(
                            $"op 0xDF string not NUL-terminated (payload end at +0x{p.Length:X})");
                    for (int k = i; k < j; k++)
                        if (p[k] < 0x20 || p[k] > 0x7E)
                            throw new AiScriptException($"op 0xDF string has non-ASCII bytes at +0x{i:X}");
                    step.Set(f.Key, Encoding.ASCII.GetString(p, i, j - i));
                    i = j + 1;
                    continue;
                }
                if (f.Kind == AiOperandKind.Units)
                {
                    int need = 3 * f.Count;
                    if (i + need > p.Length)
                        throw new AiScriptException(
                            $"payload truncated in op {op.Name} operands at +0x{i:X}");
                    int[] vals = new int[f.Count];
                    for (int k = 0; k < f.Count; k++) vals[k] = UnitsFromWire(p.AsSpan(i + 3 * k, 3));
                    step.Set(f.Key, vals);
                    i += need;
                    continue;
                }
                if (f.Kind == AiOperandKind.U8)
                {
                    if (i + 1 > p.Length)
                        throw new AiScriptException(
                            $"payload truncated in op {op.Name} operands at +0x{i:X}");
                    step.Set(f.Key, (int)p[i]);
                    i += 1;
                    continue;
                }
                // word kinds
                int needW = 2 * f.Count;
                if (i + needW > p.Length)
                    throw new AiScriptException($"payload truncated in op {op.Name} operands at +0x{i:X}");
                int[] words = new int[f.Count];
                for (int k = 0; k < f.Count; k++)
                {
                    int w = U16(p, i + 2 * k);
                    words[k] = f.IsSigned && w >= 0x8000 ? w - 0x10000 : w;
                }
                step.Set(f.Key, f.Count > 1 ? words : words[0]);
                i += needW;
            }
            model.Steps.Add(step);
        }
        return model;
    }

    // =======================================================================
    // validate: the encoded authoring rules (errors) + advisories (warnings)
    // =======================================================================

    /// <summary>
    /// Validate a model, optionally in MISSION CONTEXT.  <paramref name="mission"/>
    /// is the container model the script will live in and
    /// <paramref name="ownerIndex"/> the index (in <see cref="SDataModel.Stream"/>,
    /// directives included — python's <c>stream</c> index) of the object that
    /// owns it; with both supplied, slot references are checked against what the
    /// load-time patcher will actually have registered by then.
    /// </summary>
    public static (List<string> Errors, List<string> Warnings) Validate(
        AiScriptModel model, SDataModel? mission = null, int? ownerIndex = null)
    {
        List<string> errors = new List<string>();
        List<string> warnings = new List<string>();
        if (model.Format != AiScriptOps.Format)
            return (new List<string> { $"not a {AiScriptOps.Format} document" }, warnings);
        List<AiScriptStep> steps = model.Steps;
        if (steps.Count == 0)
            return (new List<string> { "script has no steps" }, warnings);

        // slot -> stream index of its FIRST registration (attr 0x86;
        // named_place_entry_register @image@0x08EE7 runs at object close)
        Dictionary<int, int> regFirst = new Dictionary<int, int>();
        if (mission is not null)
            for (int idx = 0; idx < mission.Stream.Count; idx++)
            {
                if (mission.Stream[idx] is not SDataObject o) continue;
                foreach (SDataAttr a in o.Attrs)
                    if (a.Attr == "actor_slot" && !regFirst.ContainsKey(a.Value))
                        regFirst[a.Value] = idx;
            }

        void CheckSlot(int n, int? val, string what, bool loadTime)
        {
            if (val is not { } v || v < 0 || v > AiScriptOps.MaxSlot)
            {
                errors.Add($"step {n}: {what} {(val?.ToString() ?? "None")} outside slot range " +
                           $"0..{AiScriptOps.MaxSlot} ([0xEE5A] is u16[13])");
                return;
            }
            if (mission is null) return;
            if (!regFirst.TryGetValue(v, out int first))
            {
                string msg = $"step {n}: {what} {v} is never registered by any object " +
                             "(attr actor_slot) in the mission";
                if (loadTime)
                    errors.Add(msg + " — load-time patch would read a stale/zero table entry");
                else warnings.Add(msg);
            }
            else if (loadTime && ownerIndex is { } owner && first > owner)
            {
                warnings.Add($"step {n}: {what} {v} is first registered by a LATER object " +
                             $"(stream idx {first} > owner {owner}) — the load-time patch reads " +
                             "the coords registered so far (stale)");
            }
        }

        bool seenYield = false;
        for (int n = 0; n < steps.Count; n++)
        {
            AiScriptStep s = steps[n];
            AiScriptOp? op = s.Spec;
            if (op is null)
            {
                errors.Add($"step {n}: unknown op '{s.Op}'");
                continue;
            }
            int d = s.Delay;
            if (d < 0 || d > 0xFFFF)
                errors.Add($"step {n}: delay {d} outside u16");
            else
            {
                if (d != AiScriptOps.DelayRunNow && d != AiScriptOps.DelayHold && d > AiScriptOps.DelayClamp)
                    warnings.Add($"step {n}: delay {d} > {AiScriptOps.DelayClamp} clamps at commit (doc §3)");
                if (d != 0 && !op.Commits)
                    warnings.Add($"step {n}: op {op.Name} ignores its delay word (no commit_timer " +
                                 $"call) — value {d} is dead data (shipped corpus always uses 0)");
            }
            if (op.IsPlaceOp) CheckSlot(n, s.Place, $"place ref ({op.Name})", true);

            foreach (AiScriptField f in op.Fields)
            {
                if (f.Kind == AiOperandKind.Place) continue;
                object? v = s.Get(f.Key);
                if (f.Kind == AiOperandKind.Text)
                {
                    if (v is not string text || text.Any(c => c < 0x20 || c > 0x7E))
                    {
                        errors.Add($"step {n}: radio text must be printable ASCII (0x20..0x7E), " +
                                   $"got {Describe(v)}");
                        continue;
                    }
                    if (text.Length == 0) warnings.Add($"step {n}: empty radio string");
                    if (text.Length > AiScriptOps.TextCorpusMax)
                        warnings.Add($"step {n}: radio text {text.Length} chars > shipped max " +
                                     $"{AiScriptOps.TextCorpusMax} (engine bound unproven)");
                    continue;
                }
                if (f.Kind == AiOperandKind.Units)
                {
                    if (v is not int[] arr || arr.Length != f.Count)
                    {
                        errors.Add($"step {n}: {f.Key} must be {f.Count} ints");
                        continue;
                    }
                    foreach (int x in arr)
                        if (x < AiScriptOps.I24Min || x > AiScriptOps.I24Max)
                            errors.Add($"step {n}: {f.Key} component {x} outside i24 world-unit range");
                    continue;
                }
                int[] vals;
                if (f.Count > 1)
                {
                    if (v is not int[] a || a.Length != f.Count)
                    {
                        errors.Add($"step {n}: {f.Key} must be a list of {f.Count} ints");
                        continue;
                    }
                    vals = a;
                }
                else
                {
                    if (v is not int one)
                    {
                        errors.Add($"step {n}: {f.Key} must be an int");
                        continue;
                    }
                    vals = new[] { one };
                }
                foreach (int x in vals)
                {
                    if (f.IsSigned)
                    {
                        if (x < -0x8000 || x > 0x7FFF) errors.Add($"step {n}: {f.Key} {x} outside i16");
                    }
                    else if (f.Kind == AiOperandKind.U8)
                    {
                        if (x < 0 || x > 0xFF) errors.Add($"step {n}: {f.Key} {x} outside u8");
                    }
                    else if (x < 0 || x > 0xFFFF) errors.Add($"step {n}: {f.Key} {x} outside u16");
                }
                switch (f.Kind)
                {
                    case AiOperandKind.ActorRt:
                        CheckSlot(n, vals[0], $"actor ref ({op.Name})", false);
                        break;
                    case AiOperandKind.ActorLd:
                        CheckSlot(n, vals[0], $"actor ref ({op.Name})", true);
                        break;
                    case AiOperandKind.Flags:
                    case AiOperandKind.Mask:
                        if (vals[0] > 0xFF)
                            warnings.Add($"step {n}: {f.Key} 0x{vals[0]:X4} high byte is ignored " +
                                         "(handler applies the low byte only)");
                        break;
                    case AiOperandKind.Heading8:
                        if (vals[0] >= 2880)
                            warnings.Add($"step {n}: heading {vals[0]} >= 2880 (1/8-degree BAM wraps)");
                        break;
                }
            }
            if (op.Yields) seenYield = true;
            if (op.Code == 0xDD && !seenYield)
                errors.Add($"step {n}: loop (RESET_PC) with no frame-yielding op before it — the " +
                           "interpreter would spin forever within one frame (yield ops: orient/" +
                           "heading/track/phase setters)");
            if (op.Code == 0xFD)
                warnings.Add($"step {n}: orient_prev_origin (0xFD) is dormant in all shipped data — " +
                             "engine path decoded but never flown");
        }
        if (steps[^1].Op != "suspend")
            errors.Add("script must end with a suspend step (0xFF; 268/268 shipped scripts do)");
        return (errors, warnings);
    }

    private static string Describe(object? v) => v switch
    {
        null => "None",
        string s => $"'{s}'",
        int[] a => "[" + string.Join(", ", a) + "]",
        _ => v.ToString() ?? "?",
    };

    // =======================================================================
    // synth: step model -> raw payload bytes
    // =======================================================================

    /// <summary>
    /// Emit the authored (PRE-patch) payload.  Refuses on any validation ERROR;
    /// <paramref name="warnings"/>, when supplied, collects the advisories.
    /// </summary>
    public static byte[] Synth(AiScriptModel model, List<string>? warnings = null,
                              SDataModel? mission = null, int? ownerIndex = null)
    {
        (List<string> errors, List<string> warns) = Validate(model, mission, ownerIndex);
        if (errors.Count > 0) throw new AiScriptException(string.Join("; ", errors));
        warnings?.AddRange(warns);

        List<byte> outBytes = new List<byte>();
        foreach (AiScriptStep s in model.Steps)
        {
            AiScriptOp op = AiScriptOps.ByName[s.Op];
            outBytes.Add((byte)(s.Delay & 0xFF));
            outBytes.Add((byte)((s.Delay >> 8) & 0xFF));
            int code = op.Code + (op.IsPlaceOp ? s.Place ?? 0 : 0);
            outBytes.Add((byte)code);
            foreach (AiScriptField f in op.Fields)
            {
                if (f.Kind == AiOperandKind.Place) continue;
                switch (f.Kind)
                {
                    case AiOperandKind.Text:
                        outBytes.AddRange(Encoding.ASCII.GetBytes(s.Text(f.Key)));
                        outBytes.Add(0);
                        break;
                    case AiOperandKind.Units:
                        foreach (int x in s.Ints(f.Key)) UnitsToWire(x, outBytes);
                        break;
                    case AiOperandKind.U8:
                        outBytes.Add((byte)(s.Int(f.Key) & 0xFF));
                        break;
                    default:
                        foreach (int x in f.Count > 1 ? s.Ints(f.Key) : new[] { s.Int(f.Key) })
                        {
                            int w = x & 0xFFFF;
                            outBytes.Add((byte)(w & 0xFF));
                            outBytes.Add((byte)((w >> 8) & 0xFF));
                        }
                        break;
                }
            }
        }
        if (outBytes.Count > AiScriptOps.MaxLen)
            throw new AiScriptException($"script {outBytes.Count} B > {AiScriptOps.MaxLen} " +
                                        "(parser staging buffer [bp-0x2C8])");
        byte[] result = outBytes.ToArray();
        // container-grammar cross-check: the emitted stream must ALSO walk
        // cleanly under the container's own patcher alphabet (SDataModel.
        // ScriptWalk) — two independently transcribed width tables agreeing.
        try { foreach (SDataModel.ScriptStep _ in SDataModel.ScriptWalk(result)) { } }
        catch (SDataException ex) { throw new AiScriptException(ex.Message); }
        return result;
    }

    // =======================================================================
    // listings
    // =======================================================================

    /// <summary>
    /// <c>+OFFS  [delay=…]  MNEMONIC  operands  ; comment</c>.  The preview pane
    /// renders this, so what an author sees while editing is the same reading
    /// the reference disassembler gives of the bytes that will ship.
    /// </summary>
    public static string Disasm(AiScriptModel model, bool withComments = true)
    {
        StringBuilder sb = new StringBuilder();
        int off = 0;
        foreach (AiScriptStep s in model.Steps)
        {
            AiScriptOp? op = s.Spec;
            if (op is null)
            {
                sb.AppendLine($"+{off:X4}  [delay={s.Delay}]  UNKNOWN_OP '{s.Op}'");
                continue;
            }
            string delay = s.Delay switch
            {
                AiScriptOps.DelayRunNow => "0xFFFE(sentinel)",
                AiScriptOps.DelayHold => "0xFFFF(sentinel)",
                0 => "0",
                _ => $"0x{s.Delay:X4}",
            };
            sb.Append($"+{off:X4}  [delay={delay}]  {AiScriptOps.Mnemonic(op, s.Place),-24}");
            sb.Append(OperandText(s, op));
            if (withComments) sb.Append($"  ; {op.Citation}");
            sb.AppendLine();
            off += StepLength(s, op);
        }
        if (model.Steps.Count > 0)
            sb.Append($"; Total: {model.Steps.Count} instruction(s), {off} byte(s)");
        return sb.ToString();
    }

    /// <summary>The operand rendering used by both the listing and the step-row summary.</summary>
    public static string OperandText(AiScriptStep step, AiScriptOp op)
    {
        List<string> parts = new List<string>();
        foreach (AiScriptField f in op.Fields)
        {
            switch (f.Kind)
            {
                case AiOperandKind.Place:
                    break;                                     // in the mnemonic suffix
                case AiOperandKind.Text:
                    parts.Add($"str='{step.Text(f.Key)}'");
                    break;
                case AiOperandKind.Units:
                {
                    string[] names = f.Count == 2
                        ? new[] { "dx_units", "dz_units" }
                        : new[] { "dx_units", "dy_units", "dz_units" };
                    int[] vals = step.Ints(f.Key);
                    for (int k = 0; k < f.Count; k++)
                        parts.Add($"{(k < names.Length ? names[k] : f.Key + k)}=" +
                                  $"{(k < vals.Length ? vals[k] : 0)}");
                    break;
                }
                default:
                    if (f.Count > 1)
                    {
                        int[] vals = step.Ints(f.Key);
                        for (int k = 0; k < f.Count; k++)
                            parts.Add($"{f.Key}{k}={(k < vals.Length ? vals[k] : 0)}");
                    }
                    else parts.Add($"{f.Key}={step.Int(f.Key)}");
                    break;
            }
        }
        return string.Join("  ", parts);
    }

    /// <summary>Wire length of one step (3-byte header + operands).</summary>
    public static int StepLength(AiScriptStep step, AiScriptOp op)
    {
        int n = 3;
        foreach (AiScriptField f in op.Fields)
            n += f.Kind switch
            {
                AiOperandKind.Place => 0,
                AiOperandKind.Text => Encoding.ASCII.GetByteCount(step.Text(f.Key)) + 1,
                AiOperandKind.Units => 3 * f.Count,
                AiOperandKind.U8 => 1,
                _ => 2 * f.Count,
            };
        return n;
    }

    // =======================================================================
    // mission plumbing (containers carry scripts as attr 0x89)
    // =======================================================================

    /// <summary>Every ai_script attr in a mission: (stream index, the payload).</summary>
    public static IEnumerable<(int StreamIndex, byte[] Payload)> IterMissionScripts(SDataModel m)
    {
        for (int i = 0; i < m.Stream.Count; i++)
        {
            if (m.Stream[i] is not SDataObject o) continue;
            foreach (SDataAttr a in o.Attrs)
                if (a.Attr == "ai_script" && a.Script is { } s)
                    yield return (i, s);
        }
    }

    /// <summary>
    /// Replace-or-add the ai_script attr of <c>stream[streamIndex]</c> with the
    /// synth of <paramref name="script"/>, validated IN MISSION CONTEXT — the
    /// python <c>attach_script</c>, and the one write path for authored scripts.
    /// </summary>
    public static void AttachScript(SDataModel mission, int streamIndex, AiScriptModel script,
                                    List<string>? warnings = null)
    {
        byte[] payload = Synth(script, warnings, mission, streamIndex);
        if (mission.Stream[streamIndex] is not SDataObject obj)
            throw new AiScriptException($"stream[{streamIndex}] is not an object");
        SDataAttr? hit = obj.Attrs.FirstOrDefault(a => a.Attr == "ai_script");
        if (hit is not null) hit.Script = payload;
        else obj.Attrs.Add(new SDataAttr { Attr = "ai_script", Script = payload });
    }
}
