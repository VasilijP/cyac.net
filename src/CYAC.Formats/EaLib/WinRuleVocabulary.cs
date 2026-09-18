using System.Globalization;
using System.Text;

namespace CYAC.Formats.EaLib;

/// <summary>
/// Our words for the parts of the bespoke mission modules that the rule model cannot name by itself:
/// a name and meaning for some of their parameters, and a one-line idiom per module.
/// </summary>
/// <remarks>
/// <para>
/// The texts carry no numbers from the missions.  Every mission value is a placeholder that is filled
/// from the module bytes when the parameters are collected:
/// </para>
/// <list type="bullet">
/// <item><c>{value}</c> is the parameter's own immediate;</item>
/// <item><c>{name}</c> is a <see cref="Reference"/> of the entry: the immediate or the displacement of
/// the instruction at a block offset, or the number of instructions of one shape in an export;</item>
/// <item><c>/N</c> divides exactly (an entry size or a word size, e.g. <c>{place/12}</c>), and a
/// remainder makes the placeholder fail;</item>
/// <item><c>:x</c> prints <c>0x</c> and upper-case hex digits, <c>:word</c> prints an English number
/// word (up to twenty); the default is decimal.</item>
/// </list>
/// <para>
/// The numbers left in the texts are structure: variable and counter offsets inside the module,
/// record-field offsets, global addresses, the size of a table entry, flag bits and block offsets.
/// </para>
/// <para>
/// An entry is keyed by module name and block offset, which says where, not what.  It applies only
/// when the instruction there still has the shape the entry was written for (the mnemonic and operand
/// shape of <see cref="RealModeInstruction.Shape"/>) and every placeholder resolves; otherwise the
/// module has changed under it and the generic wording stays.
/// </para>
/// </remarks>
public static class WinRuleVocabulary
{
    /// <summary>What a reference reads.</summary>
    public enum ReferenceKind
    {
        /// <summary>The immediate of the instruction at <see cref="Reference.At"/>.</summary>
        Immediate,
        /// <summary>The memory displacement of the instruction at <see cref="Reference.At"/>.</summary>
        Displacement,
        /// <summary>How many instructions of <see cref="Reference.Shape"/> export <see cref="Reference.At"/> contains.</summary>
        Count,
    }

    /// <summary>A number an entry's text needs, and where the module keeps it.</summary>
    /// <param name="Name">The placeholder name.</param>
    /// <param name="Kind">What is read.</param>
    /// <param name="At">The instruction's block offset, or the export slot for <see cref="ReferenceKind.Count"/>.</param>
    /// <param name="Shape">The instruction shape the reference expects (or counts).</param>
    public sealed record Reference(string Name, ReferenceKind Kind, int At, string Shape)
    {
        internal static Reference Imm(string name, int at, string shape) => new(name, ReferenceKind.Immediate, at, shape);

        internal static Reference Disp(string name, int at, string shape) => new(name, ReferenceKind.Displacement, at, shape);

        internal static Reference Count(string name, int slot, string shape) => new(name, ReferenceKind.Count, slot, shape);
    }

    /// <summary>A name and meaning for one parameter of one module.</summary>
    /// <param name="Mission">The module's archive name.</param>
    /// <param name="BlockOffset">The block offset of the parameter's immediate.</param>
    /// <param name="Shape">The shape the parameter's instruction must have.</param>
    /// <param name="Name">The parameter's name (a template).</param>
    /// <param name="Meaning">The parameter's meaning (a template).</param>
    /// <param name="Editable">False when the value is structural and must not be offered for editing.</param>
    /// <param name="References">The placeholders beyond <c>{value}</c>.</param>
    public sealed record Annotation(string Mission, int BlockOffset, string Shape, string Name, string Meaning,
                                    bool? Editable = null, params Reference[] References);

    /// <summary>A one-line description of a bespoke module.</summary>
    /// <param name="Mission">The module's archive name.</param>
    /// <param name="Text">The description (a template).</param>
    /// <param name="References">Its placeholders.</param>
    public sealed record Idiom(string Mission, string Text, params Reference[] References);

    private const string ClockTest = "cmp m2[:bp++d] imm2";
    private const string CounterTest = "cmp m1[cs:D] imm1";
    private const string SumTest = "cmp al imm1";
    private const string GroundTest = "cmp m2[es:si++d] imm2";
    private const string DeckTest = "cmp m1[es:si++d] imm1";
    private const string RadiusTest = "cmp ax imm2";
    private const string PlaceSelect = "add bx imm2";
    private const string ActorSlot = "mov si m2[ss:bx++d]";
    private const string Activation = "or m1[es:si++d] imm1";
    private const string MessageReturn = "mov dx cs";

    private const string OnGround = "On-ground test (== {value})";
    private const string OnDeck = "On-deck altitude limit";
    private const string OnDeckMeaning = "player byte[+0xB] <= N counts as 'on the deck' (shipped {value:x})";
    private const string Radius = "Landing radius";
    private const string FirstCall = "Radio call 1 time";
    private const string SecondCall = "Radio call 2 time";
    private const string FirstMessage = "clock tick of the first one-shot radio message (shipped {value})";
    private const string SecondMessage = "clock tick of the second one-shot radio message (shipped {value})";
    private const string TimedMessages = "{calls:word} timed one-shot radio messages (clock {first} / {second})";

    /// <summary>The parameter entries.</summary>
    public static IReadOnlyList<Annotation> Annotations { get; } =
    [
        // Timed activation: check_win wakes a run of actors once the clock passes a tick.
        new("ACE.S", 0x25F, ClockTest, "Ace-activation time",
            "mission clock tick at which the {aces:word} aces (actor slots {first/2}..{last/2}) are woken " +
            "(`or byte es:[actor+0x3C],1`); shipped {value}",
            null,
            Reference.Count("aces", 3, Activation),
            Reference.Disp("first", 0x275, ActorSlot),
            Reference.Disp("last", 0x299, ActorSlot)),

        // Land-at-place: the predicate helper tests the player record against a named place.
        new("ALONE.S", 0x1F, PlaceSelect, "Landing place selector",
            "byte offset into g_named_place_coord_table [0xEE74] (12 B/entry; {value:x} = place {value/12}) " +
            "— must stay a multiple of 12, not offered as a numeric knob",
            false),
        new("ALONE.S", 0x24, GroundTest, OnGround,
            "player word[+0xC] <= {value} means 'on the ground' — structural, not a knob", false),
        new("ALONE.S", 0x2B, DeckTest, OnDeck, OnDeckMeaning),
        new("ALONE.S", 0x49, RadiusTest, Radius,
            "win requires Manhattan |dx|+|dz| <= N of the landing place, coord-hi units (shipped {value:x})"),
        new("GAUNTLET.S", 0x26, GroundTest, OnGround,
            "player word[+0xC] <= {value} means 'on the ground' — structural", false),
        new("GAUNTLET.S", 0x2D, DeckTest, OnDeck, OnDeckMeaning),
        new("GAUNTLET.S", 0x4B, RadiusTest, Radius,
            "win requires Manhattan |dx|+|dz| <= N of place {place/12} (shipped {value:x})",
            null, Reference.Imm("place", 0x1F, PlaceSelect)),
        new("MOOLAH.S", 0x88, GroundTest, OnGround,
            "player word[+0xC] <= {value} means 'on the ground' — structural", false),
        new("MOOLAH.S", 0x8F, DeckTest, OnDeck, OnDeckMeaning),
        new("MOOLAH.S", 0xAD, RadiusTest, Radius,
            "win requires Manhattan |dx|+|dz| <= N of place {place/12} (shipped {value:x})",
            null, Reference.Imm("place", 0x81, PlaceSelect)),
        new("MOOLAH.S", 0x2B1, ClockTest, FirstCall, FirstMessage),
        new("MOOLAH.S", 0x2CB, ClockTest, SecondCall, SecondMessage),

        // Event-armed delayed radio: an actor field arms a deadline clock+N.
        new("BOLO.S", 0x1FB, "cmp m2[cs:D] imm2", "Deadline sentinel ({value} = unarmed)",
            "cs:[0x40] == {value} means the radio deadline is not yet armed — structural sentinel, not a knob",
            false),
        new("BOLO.S", 0x20A, "add bx imm2", "Actor record field offset",
            "+{value:x} into the actor slot record — ABI offset, not a knob", false),
        new("BOLO.S", 0x20F, "cmp m2[es:bx++d] imm2", "Trigger test (!= {value})",
            "actor {actor/2} word[+0x33] != {value} arms the deadline — structural", false,
            Reference.Disp("actor", 0x204, "mov bx m2[ss:bx++d]")),
        new("BOLO.S", 0x216, "add ax imm2", "Radio deadline delay",
            "the delayed radio message fires clock+N after actor {actor/2}'s trigger field goes nonzero " +
            "(shipped {value:x} = {value})",
            null, Reference.Disp("actor", 0x204, "mov bx m2[ss:bx++d]")),

        // Timed one-shot radio messages beside a kill threshold.
        new("RAMROD.S", 0x2B9, ClockTest, FirstCall, FirstMessage),
        new("RAMROD.S", 0x2D3, ClockTest, SecondCall, SecondMessage),
        new("RAMROD.S", 0x5F, CounterTest, "Win threshold (counter +0x58)",
            "check_win in-flight predicate: counter[+0x58] >= N — TWIN of the debrief threshold @blk+0x5d9 " +
            "(TRUCK-style split: patch both to keep the advisor and the scoring consistent)"),
        new("RAMROD.S", 0x5D9, CounterTest, "Debrief threshold (counter +0x58)",
            "get_debrief_text scoring threshold — TWIN of the in-flight win threshold @blk+0x5f (patch both)"),
        new("STRAFE.S", 0x2E1, ClockTest, FirstCall, FirstMessage),
        new("STRAFE.S", 0x2FB, ClockTest, SecondCall, SecondMessage),

        // Checkride monitor: clock windows with altitude gates, and a summed-counter win.
        new("INSTR.S", 0x448, ClockTest, FirstCall,
            "clock tick of the first instructor radio call (shipped {value})"),
        new("INSTR.S", 0x462, ClockTest, "Radio call 2 window start",
            "instructor call 2 fires in clock window [start,end] when the altitude gate holds " +
            "(shipped {value}..{end})",
            null, Reference.Imm("end", 0x465, ClockTest)),
        new("INSTR.S", 0x468, ClockTest, "Radio call 2 window end",
            "end of the radio-call-2 clock window (shipped {value})"),
        new("INSTR.S", 0x472, "cmp m2[es:bx++d] imm2", "Radio call 2 altitude gate",
            "call 2 fires only while player word[+0xC] >= N (shipped {value:x} — the 'climb' instruction gate)"),
        new("INSTR.S", 0x48C, ClockTest, "Radio call 3 window start",
            "instructor call 3 clock window start (shipped {value}..{end})",
            null, Reference.Imm("end", 0x48F, ClockTest)),
        new("INSTR.S", 0x492, ClockTest, "Radio call 3 window end",
            "end of the radio-call-3 clock window (shipped {value})"),
        new("INSTR.S", 0x49C, "cmp m2[es:bx++d] imm2", "Radio call 3 altitude gate",
            "call 3 fires only while player word[+0xC] >= N (shipped {value:x})"),
        new("INSTR.S", 0xC6, SumTest, "Win threshold (summed counters)",
            "check_win: c[+0xc]+c[+0xd] >= N (helper @+0xbc sums both counters) — TWIN of the slot-4 sum " +
            "threshold @blk+0x6de (patch both to keep advisor and scoring consistent)"),
        new("INSTR.S", 0x6DE, SumTest, "Debrief threshold (summed counters)",
            "get_debrief_text: c[+0xc]+c[+0xd] >= N — TWIN of the in-flight sum threshold @blk+0xc6 (patch both)"),
    ];

    /// <summary>The module idioms.</summary>
    public static IReadOnlyList<Idiom> Idioms { get; } =
    [
        new("ACE.S",
            "timed actor activation: at clock >= {clock}, wake actor slots {first/2}..{last/2} " +
            "(or byte es:[actor+0x3C],1); win = counter[+0xd] >= {threshold} (all {aces:word} aces)",
            Reference.Imm("clock", 0x25C, ClockTest),
            Reference.Disp("first", 0x275, ActorSlot),
            Reference.Disp("last", 0x299, ActorSlot),
            Reference.Imm("threshold", 0x0E, CounterTest),
            Reference.Count("aces", 3, Activation)),
        new("ALONE.S",
            "land-at-place: win when on deck (player word[+0xC] <= {ground}, byte[+0xB] <= {deck:x}) " +
            "within Manhattan radius {radius:x} of place {place/12}",
            Reference.Imm("ground", 0x20, GroundTest),
            Reference.Imm("deck", 0x27, DeckTest),
            Reference.Imm("radius", 0x48, RadiusTest),
            Reference.Imm("place", 0x1D, PlaceSelect)),
        new("GAUNTLET.S",
            "land-at-place: same predicate as ALONE at place {place/12}",
            Reference.Imm("place", 0x1F, PlaceSelect)),
        new("MOOLAH.S",
            "land-at-place win (place {place/12}) + " + TimedMessages,
            Reference.Imm("place", 0x81, PlaceSelect),
            Reference.Count("calls", 3, MessageReturn),
            Reference.Imm("first", 0x2AE, ClockTest),
            Reference.Imm("second", 0x2C8, ClockTest)),
        new("BOLO.S",
            "kill threshold + event-armed delayed radio: when actor {actor/2} word[+0x33] != {trigger}, " +
            "arm deadline clock+{delay:x}, fire message once",
            Reference.Disp("actor", 0x204, "mov bx m2[ss:bx++d]"),
            Reference.Imm("trigger", 0x20B, "cmp m2[es:bx++d] imm2"),
            Reference.Imm("delay", 0x215, "add ax imm2")),
        new("PIRATE.S",
            "kill threshold (counter[+0xc] >= {threshold}) + first-kill radio message " +
            "(fires when counter[+0xd] > {kills})",
            Reference.Imm("threshold", 0x4E, CounterTest),
            Reference.Imm("kills", 0x28E, CounterTest)),
        new("RAMROD.S",
            "kill threshold (counter[+0x58] >= {threshold}) + " + TimedMessages,
            Reference.Imm("threshold", 0x5A, CounterTest),
            Reference.Count("calls", 3, MessageReturn),
            Reference.Imm("first", 0x2B6, ClockTest),
            Reference.Imm("second", 0x2D0, ClockTest)),
        new("STRAFE.S",
            "kill threshold (counter[+0x7c] >= {threshold}) + " + TimedMessages,
            Reference.Imm("threshold", 0x7E, CounterTest),
            Reference.Count("calls", 3, MessageReturn),
            Reference.Imm("first", 0x2DE, ClockTest),
            Reference.Imm("second", 0x2F8, ClockTest)),
        new("INSTR.S",
            "checkride monitor: one-shot formation checks + {calls:word} timed radio calls with altitude " +
            "gates; win = c[+0xc]+c[+0xd] >= {threshold}",
            Reference.Count("calls", 3, MessageReturn),
            Reference.Imm("threshold", 0xC5, SumTest)),
    ];

    private static readonly string[] Words =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
    ];

    private static readonly string[] MoreWords =
    [
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen",
        "nineteen", "twenty",
    ];

    /// <summary>The entry for one parameter of one module, or null.</summary>
    /// <param name="annotations">The entries to search.</param>
    /// <param name="mission">The module's archive name (any case).</param>
    /// <param name="blockOffset">The block offset of the parameter's immediate.</param>
    public static Annotation? AnnotationFor(IEnumerable<Annotation> annotations, string mission, int blockOffset) =>
        annotations.FirstOrDefault(a => a.BlockOffset == blockOffset
                                        && string.Equals(a.Mission, mission, StringComparison.OrdinalIgnoreCase));

    /// <summary>The idiom of a module, or null.</summary>
    /// <param name="idioms">The idioms to search.</param>
    /// <param name="mission">The module's archive name (any case).</param>
    public static Idiom? IdiomFor(IEnumerable<Idiom> idioms, string mission) =>
        idioms.FirstOrDefault(i => string.Equals(i.Mission, mission, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The placeholder names a template uses (<c>{name}</c>, <c>{name/N}</c>, <c>{name:f}</c> all give
    /// <c>name</c>), in order of appearance.
    /// </summary>
    /// <param name="template">The text with placeholders.</param>
    public static IEnumerable<string> PlaceholderNames(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        int i = 0;
        while ((i = template.IndexOf('{', i)) >= 0)
        {
            int close = template.IndexOf('}', i + 1);
            if (close < 0)
            {
                yield break;
            }

            string spec = template[(i + 1)..close];
            int cut = spec.IndexOfAny(['/', ':']);
            yield return cut >= 0 ? spec[..cut] : spec;
            i = close + 1;
        }
    }

    /// <summary>
    /// Fills a template.  Returns null when a placeholder does not resolve: an unknown name, a value
    /// the lookup cannot supply, or an inexact division.
    /// </summary>
    /// <param name="template">The text with placeholders.</param>
    /// <param name="lookup">The value of a placeholder name, or null when it has none.</param>
    public static string? Render(string template, Func<string, long?> lookup)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(lookup);
        StringBuilder sb = new StringBuilder(template.Length);
        int i = 0;
        while (i < template.Length)
        {
            int open = template.IndexOf('{', i);
            if (open < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }

            int close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                return null;
            }

            sb.Append(template, i, open - i);
            if (Placeholder(template[(open + 1)..close], lookup) is not { } text)
            {
                return null;
            }

            sb.Append(text);
            i = close + 1;
        }

        return sb.ToString();
    }

    /// <summary>English words up to twenty, digits beyond.</summary>
    /// <param name="n">The number.</param>
    public static string NumberWord(long n) => n switch
    {
        >= 0 and <= 10 => Words[n],
        > 10 and <= 20 => MoreWords[n - 11],
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    private static string? Placeholder(string spec, Func<string, long?> lookup)
    {
        string format = string.Empty;
        int colon = spec.IndexOf(':');
        if (colon >= 0)
        {
            format = spec[(colon + 1)..];
            spec = spec[..colon];
        }

        long divisor = 1;
        int slash = spec.IndexOf('/');
        if (slash >= 0)
        {
            if (!long.TryParse(spec[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out divisor)
                || divisor <= 0)
            {
                return null;
            }

            spec = spec[..slash];
        }

        if (lookup(spec) is not { } value || value % divisor != 0)
        {
            return null;
        }

        value /= divisor;
        return format switch
        {
            "" => value.ToString(CultureInfo.InvariantCulture),
            "x" => (value < 0 ? "-0x" : "0x") + Math.Abs(value).ToString("X", CultureInfo.InvariantCulture),
            "word" => NumberWord(value),
            _ => null,
        };
    }

    /// <summary>
    /// The value of each reference in an analysed module, or null when one of them does not resolve
    /// (no analysed instruction at the offset, a different shape, no such operand).
    /// </summary>
    internal static Dictionary<string, long>? Resolve(IReadOnlyList<Reference> references, ModuleAnalysis analysis)
    {
        Dictionary<string, long> values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (Reference r in references)
        {
            long? v = r.Kind switch
            {
                ReferenceKind.Count => r.At is >= 0 and < 5
                    ? analysis.Exports[r.At].Instructions.Count(i => i.Shape == r.Shape)
                    : null,
                _ => Operand(r, analysis),
            };
            if (v is not { } value)
            {
                return null;
            }

            values[r.Name] = value;
        }

        return values;
    }

    private static long? Operand(Reference r, ModuleAnalysis analysis)
    {
        Insn? ins = FindInstruction(analysis, r.At);
        if (ins is null || ins.I.Shape != r.Shape)
        {
            return null;
        }

        if (r.Kind == ReferenceKind.Displacement)
        {
            foreach (RealModeOperand op in ins.I.Operands)
            {
                if (op.Kind == RealModeOperandKind.Memory)
                {
                    return op.Displacement;
                }
            }

            return null;
        }

        try
        {
            return ins.LastImmediate;
        }
        catch (PyError)
        {
            return null;
        }
    }

    internal static Insn? FindInstruction(ModuleAnalysis analysis, int address)
    {
        foreach (ModuleFunction f in analysis.Exports.Concat(analysis.HelpersInDiscoveryOrder))
        {
            if (f.ByAddress.TryGetValue(address, out Insn? ins))
            {
                return ins;
            }
        }

        return null;
    }
}
