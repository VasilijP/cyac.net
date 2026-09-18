using System.Text;
using System.Text.Json.Nodes;

namespace CYAC.Formats.EaLib;

// ---------------------------------------------------------------------------
// AUTHORED AI-SCRIPT MODEL (
// ported).
//
// Where <see cref="SDataModel"/> is the mission CONTAINER's authoring document,
// this is the authoring document for the one unit that container still carries
// opaque: the tag-0x89 engagement-VM payload (`{"attr":"ai_script","hex":…}`).
// A script is an ordered list of STEPS; each step is
//     [delay u16 LE] [opcode u8] [operands, opcode-specific]
// in the PRE-patch (authored) form — exactly what ships in the `.S` and what
// `ai_script_named_place_patch @image@0x094EF` consumes at mission load.
//
// SCHEMA PARITY IS A CONTRACT (the P15/P16 precedent).  The JSON emitted here
// is EXACTLY python's `json.dumps(extract(payload))` shape — `format`, `steps`,
// and per step `delay`, `op`, the folded `place`, then the op's own operand
// keys IN WIRE ORDER — so a model authored here feeds `python3 --synth`
// unchanged and vice versa. `--selftest-scriptsynth` proves both directions,
// and python stays the byte reference: any disagreement is a bug HERE by
// construction.
//
//
//  * PLACE REFS ARE FOLDED INTO THE OPCODE.  0xF0+k / 0xE3+k name actor slot k
//    (k = 0..12, the [0xEE5A] u16[13] budget); the load-time patcher rewrites
//    them to op 0x02 / 0x09 with the place's coordinates ADDED to the authored
//    offsets ([0xEE74 + 12k], patch @image@0x09588..0x09626).  So "orient to
//    place 3" is one opcode BYTE, not an operand — and the model keeps `place`
//    as a first-class field rather than making the author do arithmetic.
//
//  * THE 3-BYTE COORDINATE OPERAND IS LE24 WORLD UNITS, SIGNED.
//    value_i32 = (b0<<8)|(b1<<16)|(b2<<24); world units = value>>8 =
//    LE24(b0,b1,b2) — identical to the container's read_3bytes @image@0x0A403.
//    Three independent sites: VM reader @image@0x07694 (xchg dx,ax @0x076A2),
//    patcher add-helper @image@0x09478, compiler writer @image@0x0903B.
//
//  * AUTHORABILITY IS A LAW, NOT A CENSUS.  The patcher and the VM walk the
//    SAME bytes with DIFFERENT width tables; an opcode is authorable only when
//    both agree.  Unknown-to-the-patcher ops (incl. VM-VALID 0x07) leave `si`
//    ON the opcode byte (@image@0x095E3..0x095E8) and desync the walk; op 0x09
//    is the opposite mismatch (patcher width 0, VM width 6).  Both classes are
//    REFUSED here — see <see cref="AiScriptOps.Refused"/>.
// ---------------------------------------------------------------------------

/// <summary>Raised by the AI-script model / synthesizer (python: ScriptError).</summary>
public sealed class AiScriptException : Exception
{
    public AiScriptException(string msg) : base(msg) { }
}

/// <summary>
/// Operand storage classes.  <c>ActorRt</c>/<c>ActorLd</c>/<c>Place</c>/
/// <c>Event</c>/<c>Flags</c>/<c>Mask</c>/<c>Heading8</c> are u16/u8 aliases that
/// carry VALIDATION semantics (and, in the editor, a picker instead of a
/// number box).  <c>ActorLd</c> is the LOAD-PATCHED flavor (op 0x0A word0, fixed
/// up at parse time @image@0x09630) — it must resolve at-or-before its owner;
/// <c>ActorRt</c> is resolved at RUN time (0xD5/0xD6/0xDE) and only advises.
/// </summary>
public enum AiOperandKind
{
    U16, I16, U8, Units, Text,
    Place, ActorLd, ActorRt, Event, Flags, Mask, Heading8,
}

/// <summary>One operand slot of an op, consumed IN WIRE ORDER after the opcode.</summary>
public sealed record AiScriptField(string Key, AiOperandKind Kind, int Count)
{
    public bool IsWord => Kind is AiOperandKind.U16 or AiOperandKind.I16 or AiOperandKind.ActorRt
                              or AiOperandKind.ActorLd or AiOperandKind.Event or AiOperandKind.Flags
                              or AiOperandKind.Mask or AiOperandKind.Heading8;
    public bool IsSigned => Kind == AiOperandKind.I16;

    /// <summary>"offset:units[3]" — the op-table rendering (python `--optable`).</summary>
    public string Describe() =>
        $"{Key}:{AiScriptOps.KindName(Kind)}" + (Count > 1 ? $"[{Count}]" : "");
}

/// <summary>
/// One authorable opcode.  <c>Commits</c> = the handler calls
/// <c>engagement_script_commit_timer @image@0x048EA</c>, so the step's delay
/// word is LIVE; every other op reads-and-discards it.  <c>Yields</c> = the
/// handler exits the per-frame interpreter loop (it set a phase), which is what
/// makes a <c>loop</c> after it safe rather than an infinite spin inside one
/// frame.
/// </summary>
public sealed record AiScriptOp(
    byte Code,
    string Name,
    IReadOnlyList<AiScriptField> Fields,
    string Semantics,
    string Citation,
    bool Commits,
    bool Yields,
    int Corpus)
{
    /// <summary>True for the two pseudo-op families whose opcode byte carries a place index.</summary>
    public bool IsPlaceOp => AiScriptOps.PlaceOps.ContainsKey(Code);

    /// <summary>Never used by any of the 268 shipped scripts (the "advanced" gate).</summary>
    public bool NeverShipped => Corpus == 0;

    public string OperandSummary =>
        Fields.Count == 0 ? "—" : string.Join(", ", Fields.Select(f => f.Describe()));

    /// <summary>"0xF0+k (k=0..12)" for the place families, "0xD8" otherwise.</summary>
    public string CodeText =>
        IsPlaceOp ? $"0x{Code:X2}+k (k=0..{AiScriptOps.PlaceOps[Code] - 1})" : $"0x{Code:X2}";
}

/// <summary>
/// The op table — the SINGLE source of truth for extract / synth / validate /
/// the editor's forms, transcribed from python's <c>OPS</c>/<c>REFUSED</c>.
/// </summary>
public static class AiScriptOps
{
    /// <summary>[0xEE5A] is u16[13]: places and actor slots are 0..12.</summary>
    public const int MaxSlot = 12;

    /// <summary>The parser stages a payload into a 712-B stack buffer [bp-0x2C8] (image@0x09D2D).</summary>
    public const int MaxLen = SDataModel.MaxScriptBytes;

    public const int I24Min = -0x800000, I24Max = 0x7FFFFF;

    /// <summary>commit_timer clamps a non-sentinel delay here (language doc §3).</summary>
    public const int DelayClamp = 0x1FFC;

    /// <summary>Delays written VERBATIM to [0xED62]: 0xFFFE "run now", 0xFFFF "hold".</summary>
    public const int DelayRunNow = 0xFFFE, DelayHold = 0xFFFF;

    /// <summary>The longest 0xDF string in the shipped corpus (the engine bound is unproven).</summary>
    public const int TextCorpusMax = 67;

    public const string Format = "cyac-ai-script-v1";

    /// <summary>Pseudo-op base -> how many place indexes it folds (k = 0..12).</summary>
    public static readonly IReadOnlyDictionary<byte, int> PlaceOps =
        new Dictionary<byte, int> { [0xE3] = 13, [0xF0] = 13 };

    private static AiScriptField F(string k, AiOperandKind kind, int count = 1) => new(k, kind, count);

    public static readonly IReadOnlyList<AiScriptOp> All = new AiScriptOp[]
    {
        new(0x00, "idle", Array.Empty<AiScriptField>(),
            "phase=0 idle; [0xED80]=0; delay commits then execution continues",
            "image@0x055FC", true, false, 24),
        new(0x02, "orient_abs", new[] { F("target", AiOperandKind.Units, 3) },
            "phase=2 SET_ORIENTATION_TARGETS: fly/orient toward absolute world point (x,y,z units)",
            "image@0x0562C", true, true, 13),
        new(0x08, "phase8_auto", Array.Empty<AiScriptField>(),
            "phase=8: targets auto-derived from own pos [0xED4A/4C] + 0x3E800 (+1000 units) — " +
            "zoom/climb burst (label hypothesis)",
            "image@0x056A2", true, true, 8),
        new(0x0A, "track_actor",
            new[] { F("actor", AiOperandKind.ActorLd), F("params", AiOperandKind.I16, 3) },
            "phase=0xA actor-targeted: word0 = actor slot, LOAD-PATCHED to record nearptr " +
            "[0xEE5A+2k]+0x18 -> [0xED81]; params -> [0xED83/85/87] (track/pursue = name hypothesis)",
            "image@0x056F8 + patch image@0x09630", true, true, 32),
        new(0x0D, "phase_0d",
            new[] { F("mode", AiOperandKind.U8), F("params", AiOperandKind.I16, 3) },
            "phase=0xD: [0xED87]=mode byte (shipped {2,3}); [0xED88]=0; params -> [0xED81/83/85]",
            "image@0x05670", true, true, 5),
        new(0xD5, "kill_actor", new[] { F("actor", AiOperandKind.ActorRt) },
            "KILL_ACTOR: deactivate the 4x19 effect/projectile slot owned by record [0xEE5A+2k] " +
            "(lcall 0x108E:0xACA2 = slot_4x19_clear_for_owner)",
            "image@0x0550C", false, false, 2),
        new(0xD6, "spawn_actor",
            new[] { F("actor", AiOperandKind.ActorRt), F("type", AiOperandKind.U16),
                    F("heading", AiOperandKind.I16), F("duration", AiOperandKind.U16) },
            "SPAWN_ACTOR: alloc+init a 4x19 slot for record [0xEE5A+2k] via projectile_spawn " +
            "(lcall 0x108E:0xAB87); shipped (4,-1,8) — operand names from P174, partial",
            "image@0x054CE", false, false, 2),
        new(0xD7, "clear_script_flags", new[] { F("mask", AiOperandKind.Mask) },
            "[0xED78] &= ~(mask & 0xFF) — clear script-state flag bits (low byte only)",
            "image@0x054C2", false, false, 0),
        new(0xD8, "set_script_flags", new[] { F("flags", AiOperandKind.Flags) },
            "OR [0xED78],flags-lo; if flags==1 && [0xED6F]!=0 && slot active: conditional " +
            "engagement exit; shipped {1,4,16,32}",
            "image@0x0548E", false, false, 38),
        new(0xD9, "set_heading", new[] { F("heading", AiOperandKind.Heading8) },
            "[0xED4E] = heading (1/8-degree BAM; shipped 720=90°, 2160=270°)",
            "image@0x05484", false, false, 4),
        new(0xDA, "retarget", Array.Empty<AiScriptField>(),
            "RETARGET_SLOT: randomize target + deadline via FUN_709b",
            "image@0x0546E", false, false, 0),
        new(0xDB, "clear_counter", Array.Empty<AiScriptField>(),
            "[0xC390] = 0", "image@0x0544A", false, false, 36),
        new(0xDD, "loop", Array.Empty<AiScriptField>(),
            "RESET_PC: [0xED76]=0 — loop to script START (the only backward branch; target is " +
            "fixed by the ISA)", "image@0x05452", false, false, 77),
        new(0xDE, "fire_weapon", new[] { F("actor", AiOperandKind.ActorRt) },
            "FIRE_WEAPON: record [0xEE5A+2k]; if active (obj+2 bit0) fire (lcall 0x108E:0x319C) + " +
            "conditional kill-visual",
            "image@0x05520 (byte-verified [bx-0x11A6] = [0xEE5A])", false, false, 3),
        new(0xDF, "radio", new[] { F("text", AiOperandKind.Text) },
            "PRINT_STRING: NUL-terminated radio message (lcall 0x108E:0xC37B); shipped style: " +
            "Plane 'Pilot': \"message\"", "image@0x055CA", false, false, 33),
        new(0xE0, "mission_event", new[] { F("event", AiOperandKind.Event) },
            "CALL_SCRIPT_FUNCTION: calls the MISSION MODULE's own native func2 " +
            "on_secondary_event(event) via [0xFB8]; shipped events {0,1}",
            "image@0x05478 + image@0x08CBE", false, false, 13),
        new(0xE1, "set_engage_flag", Array.Empty<AiScriptField>(),
            "[0xED59] |= 0x20", "image@0x055F4", false, false, 10),
        new(0xE2, "clear_engage_flag", Array.Empty<AiScriptField>(),
            "[0xED59] &= ~0x20", "image@0x055EC", false, false, 0),
        // pseudo-ops: the opcode byte is base + place index
        new(0xE3, "heading_place",
            new[] { F("place", AiOperandKind.Place), F("offset", AiOperandKind.Units, 2) },
            "NAMED_PLACE_HEADING (raw 0xE3+place): patched at load to op 0x09 SET_HEADING_TARGETS " +
            "with (dx,dz) += place coords x,z from [0xEE74+12k]",
            "patch image@0x095F0..0x09626", true, true, 9),
        new(0xF0, "orient_place",
            new[] { F("place", AiOperandKind.Place), F("offset", AiOperandKind.Units, 3) },
            "NAMED_PLACE_ORIENT (raw 0xF0+place): patched at load to op 0x02 " +
            "SET_ORIENTATION_TARGETS with (dx,dy,dz) += place coords from [0xEE74+12k] — the " +
            "dominant authored idiom",
            "patch image@0x09588..0x095E0", true, true, 395),
        new(0xFD, "orient_prev_origin", new[] { F("offset", AiOperandKind.Units, 3) },
            "raw 0xFD: patched to op 0x02 with coords from [0xB556] (previous object's final " +
            "position at parse time) — DORMANT in shipped data",
            "patch image@0x09651..0x09655", true, true, 0),
        new(0xFE, "end_script", Array.Empty<AiScriptField>(),
            "RETURN/SCRIPT-END: AND [0xED3E],0xFE + cleanup lcall; never shipped",
            "image@0x0545A", false, false, 0),
        new(0xFF, "suspend", Array.Empty<AiScriptField>(),
            "SUSPEND: [0xED76]=-1 — yield; re-armed scripts restart from the top. MANDATORY " +
            "terminal step (268/268 shipped scripts end with it)",
            "image@0x0578E", false, false, 268),
    };

    public static readonly IReadOnlyDictionary<string, AiScriptOp> ByName =
        All.ToDictionary(o => o.Name, StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<byte, AiScriptOp> ByCode =
        All.ToDictionary(o => o.Code);

    /// <summary>VM opcodes deliberately NOT authorable, with interpreter-cited semantics.</summary>
    public static readonly IReadOnlyDictionary<byte, (string Vm, string Why)> Refused =
        new Dictionary<byte, (string, string)>
        {
            [0x01] = ("(invalid)", "VM-invalid -> abort stream @image@0x051C0"),
            [0x03] = ("(invalid)", "VM-invalid -> abort stream @image@0x051C0"),
            [0x04] = ("(invalid)", "VM-invalid -> abort stream @image@0x051C0"),
            [0x05] = ("(invalid)", "VM-invalid -> abort stream @image@0x051C0"),
            [0x06] = ("(invalid)", "VM-invalid -> abort stream @image@0x051C0"),
            [0x07] = ("SET_PHASE_07 (no operands; phase=7, [0xED80]=2 @image@0x05664)",
                      "UNKNOWN to the load-time patcher: cascade falls through without consuming " +
                      "the opcode (@image@0x095E3..0x095E8) — the patch walk desyncs. " +
                      "extended (0x07 was not in the original list)."),
            [0x09] = ("SET_HEADING_TARGETS (2 x byte3 @image@0x056CA)",
                      "WIDTH MISMATCH: patcher advances 0 operand bytes (@image@0x09523..0x09526) " +
                      "but the VM reads 6 — raw 0x09 walks cleanly through load yet desyncs the " +
                      "VM. Authored form is the 0xE3+k named-place op, which the patcher REWRITES " +
                      "to 0x09."),
            [0x0B] = ("SET_PHASE_0B (4 x u16 -> [0xED81/83/85/87] @image@0x05724)",
                      "unknown to the patcher -> desync"),
            [0x0C] = ("SET_PHASE_0C (1 x u16 -> [0xED81] @image@0x0560A)",
                      "unknown to the patcher -> desync"),
            [0xDC] = ("SET_FLIGHT_PARAMS (3 x u16; lcall 0x201D:0x825A @image@0x053DC)",
                      "unknown to the patcher -> desync"),
        };

    /// <summary>The op a raw opcode byte belongs to, plus the folded place index (or null).</summary>
    public static (AiScriptOp? Op, int? Place) Resolve(byte code)
    {
        foreach ((byte baseCode, int n) in PlaceOps)
            if (code >= baseCode && code < baseCode + n)
                return (ByCode[baseCode], code - baseCode);
        return (ByCode.TryGetValue(code, out AiScriptOp? op) ? op : null, null);
    }

    /// <summary>
    /// The RAW-form mnemonics prints — the preview pane speaks the
    /// disassembler's language, not the model's, so a listing in the editor
    /// and a listing from the reference tool read the same
    /// (`--selftest-scriptsynth` stage S-E diffs them line for line).
    /// </summary>
    private static readonly IReadOnlyDictionary<byte, string> RawMnemonics =
        new Dictionary<byte, string>
        {
            [0x00] = "SET_PHASE_IDLE", [0x02] = "SET_ORIENTATION_TARGETS",
            [0x08] = "SET_PHASE_DIVE", [0x0A] = "SET_PHASE_0A", [0x0D] = "SET_PHASE_0D",
            [0xD5] = "KILL_ACTOR", [0xD6] = "SPAWN_ACTOR", [0xD7] = "CLEAR_SCRIPT_FLAGS",
            [0xD8] = "SET_SCRIPT_FLAGS", [0xD9] = "SET_HEADING", [0xDA] = "RETARGET_SLOT",
            [0xDB] = "CLEAR_COUNTER", [0xDD] = "RESET_PC", [0xDE] = "FIRE_WEAPON",
            [0xDF] = "PRINT_STRING", [0xE0] = "CALL_SCRIPT_FUNCTION",
            [0xE1] = "SET_ENGAGE_FLAG", [0xE2] = "CLEAR_ENGAGE_FLAG",
            [0xE3] = "NAMED_PLACE_HEADING", [0xF0] = "NAMED_PLACE_ORIENT",
            [0xFD] = "NAMED_PLACE_ORIENT_PREV", [0xFE] = "EOF", [0xFF] = "SUSPEND",
        };

    /// <summary>The disassembler mnemonic for an op (place ops get their index suffix).</summary>
    public static string Mnemonic(AiScriptOp op, int? place)
    {
        string m = RawMnemonics.TryGetValue(op.Code, out string? s) ? s : $"UNKNOWN_{op.Code:X2}";
        return op.IsPlaceOp ? $"{m}_{place ?? 0}" : m;
    }

    /// <summary>python's kind spelling, for the op-table rendering and tooltips.</summary>
    public static string KindName(AiOperandKind k) => k switch
    {
        AiOperandKind.U16 => "u16",
        AiOperandKind.I16 => "i16",
        AiOperandKind.U8 => "u8",
        AiOperandKind.Units => "units",
        AiOperandKind.Text => "text",
        AiOperandKind.Place => "place",
        AiOperandKind.ActorLd => "actor_ld",
        AiOperandKind.ActorRt => "actor_rt",
        AiOperandKind.Event => "event",
        AiOperandKind.Flags => "flags",
        AiOperandKind.Mask => "mask",
        AiOperandKind.Heading8 => "heading8",
        _ => k.ToString().ToLowerInvariant(),
    };

    /// <summary>The markdown op table python's <c>--optable</c> generates (same rows).</summary>
    public static string OpTableMarkdown()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("| code | op (model name) | operands | delay live | yields | corpus | semantics | citation |");
        sb.AppendLine("|:--|:--|:--|:--:|:--:|--:|:--|:--|");
        foreach (AiScriptOp o in All)
            sb.AppendLine($"| {o.CodeText} | `{o.Name}` | {o.OperandSummary} | " +
                          $"{(o.Commits ? "yes" : "ignored")} | {(o.Yields ? "yes" : "—")} | " +
                          $"{o.Corpus} | {o.Semantics} | {o.Citation} |");
        sb.AppendLine();
        sb.AppendLine("**Refused (not authorable):**");
        sb.AppendLine();
        sb.AppendLine("| code | VM semantics | why refused |");
        sb.AppendLine("|:--|:--|:--|");
        foreach (byte code in Refused.Keys.OrderBy(c => c))
            sb.AppendLine($"| 0x{code:X2} | {Refused[code].Vm} | {Refused[code].Why} |");
        return sb.ToString();
    }
}

/// <summary>
/// One step: a delay word, an op, the folded place index (place ops only) and
/// the op's typed operands, kept in WIRE ORDER so JSON emission matches python's
/// insertion order exactly.  Values are <see cref="int"/>, <see cref="int"/>[]
/// or <see cref="string"/> — the three shapes the wire has.
/// </summary>
public sealed class AiScriptStep
{
    private readonly List<string> _keys = new();
    private readonly Dictionary<string, object> _vals = new(StringComparer.Ordinal);

    public int Delay { get; set; }

    /// <summary>The MODEL name ("orient_place"), not the raw opcode.</summary>
    public required string Op { get; set; }

    /// <summary>The place index folded into the opcode byte (0xF0+k / 0xE3+k), or null.</summary>
    public int? Place { get; set; }

    /// <summary>The op this step names, or null when the name is not in the table.</summary>
    public AiScriptOp? Spec => AiScriptOps.ByName.TryGetValue(Op, out AiScriptOp? o) ? o : null;

    public IReadOnlyList<string> Keys => _keys;

    public bool Has(string key) => _vals.ContainsKey(key);

    public object? Get(string key) => _vals.TryGetValue(key, out object? v) ? v : null;

    public void Set(string key, object value)
    {
        if (!_vals.ContainsKey(key)) _keys.Add(key);
        _vals[key] = value;
    }

    public void Remove(string key)
    {
        _vals.Remove(key);
        _keys.Remove(key);
    }

    /// <summary>Drop every operand the current op does not name (used when the op changes).</summary>
    public void KeepOnly(IEnumerable<string> keys)
    {
        HashSet<string> keep = keys.ToHashSet(StringComparer.Ordinal);
        foreach (string k in _keys.Where(k => !keep.Contains(k)).ToList()) Remove(k);
    }

    public int Int(string key) => Get(key) is int i ? i : 0;
    public int[] Ints(string key) => Get(key) as int[] ?? Array.Empty<int>();
    public string Text(string key) => Get(key) as string ?? "";

    public AiScriptStep Clone()
    {
        AiScriptStep s = new AiScriptStep { Delay = Delay, Op = Op, Place = Place };
        foreach (string k in _keys)
            s.Set(k, _vals[k] switch
            {
                int[] a => a.ToArray(),
                var v => v,
            });
        return s;
    }

    /// <summary>
    /// A fresh step for <paramref name="op"/> with every operand at its neutral
    /// value, and the delay at the op's natural default: <c>0xFFFE</c> ("run
    /// now") on a committing op — the shipped opening idiom — and 0 elsewhere,
    /// because a non-committing op's delay word is dead data.
    /// </summary>
    public static AiScriptStep Fresh(AiScriptOp op, int? place = null)
    {
        AiScriptStep s = new AiScriptStep
        {
            Op = op.Name,
            Delay = op.Commits ? AiScriptOps.DelayRunNow : 0,
            Place = op.IsPlaceOp ? place ?? 0 : null,
        };
        foreach (AiScriptField f in op.Fields)
        {
            if (f.Kind == AiOperandKind.Place) continue;
            s.Set(f.Key, f.Kind switch
            {
                AiOperandKind.Text => "",
                AiOperandKind.Units => new int[f.Count],
                _ => f.Count > 1 ? new int[f.Count] : 0,
            });
        }
        return s;
    }

    public JsonObject ToJson()
    {
        JsonObject o = new JsonObject { ["delay"] = Delay, ["op"] = Op };
        if (Place is { } p) o["place"] = p;
        // wire order when the op is known; insertion order otherwise (an unknown
        // op is a validation ERROR, but it must still serialize for the report)
        List<string> order = Spec is { } sp
            ? sp.Fields.Where(f => f.Kind != AiOperandKind.Place).Select(f => f.Key)
                .Concat(_keys).Distinct(StringComparer.Ordinal).ToList()
            : _keys.ToList();
        foreach (string k in order)
        {
            if (!_vals.TryGetValue(k, out object? v)) continue;
            o[k] = v switch
            {
                int i => JsonValue.Create(i),
                int[] a => new JsonArray(a.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                string s => JsonValue.Create(s),
                _ => JsonValue.Create(v.ToString()),
            };
        }
        return o;
    }

    public static AiScriptStep FromJson(JsonObject j)
    {
        AiScriptStep s = new AiScriptStep
        {
            Op = j["op"]?.GetValue<string>() ?? throw new AiScriptException("step has no op"),
            Delay = j["delay"] is { } d ? d.GetValue<int>() : 0,
        };
        foreach (KeyValuePair<string, JsonNode?> kv in j)
        {
            if (kv.Key is "delay" or "op") continue;
            if (kv.Key == "place") { s.Place = kv.Value!.GetValue<int>(); continue; }
            s.Set(kv.Key, kv.Value switch
            {
                JsonArray a => a.Select(x => x!.GetValue<int>()).ToArray(),
                JsonValue v when v.TryGetValue<int>(out int i) => i,
                JsonValue v when v.TryGetValue<string>(out string? t) => t,
                _ => throw new AiScriptException($"step operand {kv.Key} has an unmodelable JSON shape"),
            });
        }
        return s;
    }
}

/// <summary>An authored script: the ordered step list, JSON-serializable.</summary>
public sealed class AiScriptModel
{
    public string Format { get; set; } = AiScriptOps.Format;
    public List<AiScriptStep> Steps { get; set; } = new();

    public static AiScriptModel Empty() => new()
    {
        Steps = { AiScriptStep.Fresh(AiScriptOps.ByName["suspend"]) },
    };

    public AiScriptModel Clone() => new()
    {
        Format = Format,
        Steps = Steps.Select(s => s.Clone()).ToList(),
    };

    public JsonObject ToJsonNode()
    {
        JsonArray steps = new JsonArray();
        foreach (AiScriptStep s in Steps) steps.Add(s.ToJson());
        return new JsonObject { ["format"] = Format, ["steps"] = steps };
    }

    public string ToJson() => ToJsonNode().ToJsonString(SDataModel.JsonOptions);

    public string CanonicalJson() => SDataModel.Canonicalize(ToJsonNode());

    public static AiScriptModel FromJsonNode(JsonObject j)
    {
        AiScriptModel m = new AiScriptModel { Format = j["format"]?.GetValue<string>() ?? "" };
        if (j["steps"] is JsonArray arr)
            foreach (JsonNode? s in arr) m.Steps.Add(AiScriptStep.FromJson((JsonObject)s!));
        return m;
    }

    public static AiScriptModel FromJson(string json) =>
        FromJsonNode(JsonNode.Parse(json) as JsonObject
                     ?? throw new AiScriptException("script model JSON is not an object"));
}
