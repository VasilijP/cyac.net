using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CYAC.Formats.EaLib;

// ---------------------------------------------------------------------------
// WIN-RULE MODULE MODEL — the semantic model.
//
// The model is the EDITABLE DOCUMENT of a `.S` mission module's win-rule code:
// it records everything mission-specific (data layout, texts, counters,
// predicate structure, statement IR) and NOTHING position-dependent — every
// offset in the emitted module falls out of the codegen rulebook
// (<see cref="WinRuleSynthesizer"/>).
//
// The JSON schema is EXACTLY the Python tool's `asdict` output, so the models
// produced by `python3 --extract-all <dir>` deserialize here unchanged (and vice
// versa); <see cref="WinRuleExtractor"/> derives the same models from the module
// bytes.  Two implementation notes that the schema depends on:
//
//   * TEXTS ARE BYTES.  Briefing / debrief / radio strings are latin-1 with
//     EMBEDDED control bytes (0x01 paragraph, 0x0C open-quote) and embedded /
//     trailing 0x00 terminators; they are carried as .NET strings whose chars
//     are all <= 0xFF and converted with <see cref="Encoding.Latin1"/>.  Never
//     round-trip them through UTF-8.
//   * TEXT-MAP ORDER IS LOAD-BEARING.  `debrief_texts` is emitted in fn4
//     FIRST-USE order and `data_layout` in source-declaration order, so the
//     maps must preserve insertion order — hence <see cref="OrderedTextMap"/>
//     rather than Dictionary&lt;,&gt;.
//
// A declared-but-unreferenced byte var (DOWN.S's dead counter at +0xC) is a
// first-class layout item: it must survive a load/store round-trip or the
// module stops being byte-exact.
// ---------------------------------------------------------------------------

/// <summary>
/// One comparison of a conjunction.  <see cref="Cc"/> is the LITERAL x86
/// condition code of the jump-away branch as it appears in the shipping code
/// (the source's signed/unsigned spelling is unrecoverable from semantics, so
/// the model records it); <see cref="Var"/> is
/// <c>arg_w</c> | <c>arg_b</c> | <c>ctr</c> | <c>clock</c>.
/// </summary>
public sealed class WinRuleTest
{
    [JsonPropertyName("var")] public string Var { get; set; } = "arg_w";
    [JsonPropertyName("cc")] public string Cc { get; set; } = "jl";
    [JsonPropertyName("imm")] public int Imm { get; set; }
    /// <summary>Byte-var ordinal, for <c>var == "ctr"</c>.</summary>
    [JsonPropertyName("counter")] public int? Counter { get; set; }

    public WinRuleTest Clone() => new() { Var = Var, Cc = Cc, Imm = Imm, Counter = Counter };
}

/// <summary>Win-predicate helper: CF=1 &lt;=&gt; predicate true.</summary>
public sealed class WinRuleHelper
{
    /// <summary>"stc_first" (tests jump away on FALSE) | "clc_first" (single test jumps away on TRUE).</summary>
    [JsonPropertyName("polarity")] public string Polarity { get; set; } = "stc_first";
    [JsonPropertyName("tests")] public List<WinRuleTest> Tests { get; set; } = new();

    public WinRuleHelper Clone() => new()
    {
        Polarity = Polarity,
        Tests = Tests.Select(t => t.Clone()).ToList(),
    };
}

/// <summary>
/// One statement of the body IR (shared by fn1 / fn2 / fn3.pre / fn3.extra /
/// fn4).  <c>op</c> = inc | copy | ax | ret | if | msg | dx0 | activate.
/// </summary>
public sealed class WinRuleStmt
{
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("counter")] public int? Counter { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("val")] public int? Val { get; set; }
    [JsonPropertyName("helper")] public int? Helper { get; set; }
    [JsonPropertyName("tests")] public List<WinRuleTest>? Tests { get; set; }
    [JsonPropertyName("body")] public List<WinRuleStmt>? Body { get; set; }
    [JsonPropertyName("slots")] public List<int>? Slots { get; set; }

    public static WinRuleStmt Inc(int counter) => new() { Op = "inc", Counter = counter };
    public static WinRuleStmt Copy(string key) => new() { Op = "copy", Text = key };
    public static WinRuleStmt Msg(string key) => new() { Op = "msg", Text = key };
    public static WinRuleStmt Ax(int val) => new() { Op = "ax", Val = val };
    public static WinRuleStmt Dx0() => new() { Op = "dx0" };
    public static WinRuleStmt Ret() => new() { Op = "ret" };

    public static WinRuleStmt If(IEnumerable<WinRuleTest> tests, IEnumerable<WinRuleStmt> body) =>
        new() { Op = "if", Tests = tests.ToList(), Body = body.ToList() };

    public static WinRuleStmt IfHelper(int helper, IEnumerable<WinRuleStmt> body) =>
        new() { Op = "if", Helper = helper, Body = body.ToList() };

    public WinRuleStmt Clone() => new()
    {
        Op = Op,
        Counter = Counter,
        Text = Text,
        Val = Val,
        Helper = Helper,
        Tests = Tests?.Select(t => t.Clone()).ToList(),
        Body = Body?.Select(s => s.Clone()).ToList(),
        Slots = Slots is null ? null : new List<int>(Slots),
    };
}

/// <summary>
/// check_win's shape: an optional PRE body (ACE's timed actor activation runs
/// BEFORE the win-flag head), the head itself
/// (<c>call helper; jae L; mov bx,[bp+0x12]; mov ss:[bx],1</c>) when
/// <c>kind == "template"</c>, then a free-form tail (template missions:
/// exactly <c>[ax(0), dx0]</c>; the radio idiom appends guarded clauses whose
/// DX:AX return doubles as the radio channel).
/// </summary>
public sealed class WinRuleFn3
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "never";
    [JsonPropertyName("helper")] public int? Helper { get; set; }
    [JsonPropertyName("pre")] public List<WinRuleStmt> Pre { get; set; } = new();
    [JsonPropertyName("extra")] public List<WinRuleStmt> Extra { get; set; } = new();

    public WinRuleFn3 Clone() => new()
    {
        Kind = Kind,
        Helper = Helper,
        Pre = Pre.Select(s => s.Clone()).ToList(),
        Extra = Extra.Select(s => s.Clone()).ToList(),
    };
}

/// <summary>One item of the ordered module DATA region (declaration order).</summary>
public sealed class WinRuleDataItem
{
    /// <summary>"save" (the 2-byte DS save slot) | "byte" (counter / one-shot flag) | "rtext".</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "byte";
    /// <summary>Radio-text key, for <c>kind == "rtext"</c>.</summary>
    [JsonPropertyName("key")] public string? Key { get; set; }

    public WinRuleDataItem Clone() => new() { Kind = Kind, Key = Key };
}

/// <summary>
/// An insertion-ORDERED string map (JSON object).  Order is load-bearing:
/// `debrief_texts` is emitted in fn4 first-use order.
/// </summary>
[JsonConverter(typeof(OrderedTextMapConverter))]
public sealed class OrderedTextMap
{
    private readonly List<KeyValuePair<string, string>> _items = new();

    public int Count => _items.Count;
    public IReadOnlyList<KeyValuePair<string, string>> Items => _items;
    public IEnumerable<string> Keys => _items.Select(kv => kv.Key);

    public bool ContainsKey(string key) => IndexOf(key) >= 0;

    public string this[string key]
    {
        get
        {
            int i = IndexOf(key);
            if (i < 0) throw new KeyNotFoundException($"text key '{key}' not in the model");
            return _items[i].Value;
        }
        set
        {
            int i = IndexOf(key);
            if (i < 0) _items.Add(new(key, value));
            else _items[i] = new(key, value);
        }
    }

    public bool TryGetValue(string key, out string value)
    {
        int i = IndexOf(key);
        value = i < 0 ? "" : _items[i].Value;
        return i >= 0;
    }

    public void Add(string key, string value)
    {
        if (ContainsKey(key)) throw new ArgumentException($"duplicate text key '{key}'");
        _items.Add(new(key, value));
    }

    public bool Remove(string key)
    {
        int i = IndexOf(key);
        if (i < 0) return false;
        _items.RemoveAt(i);
        return true;
    }

    /// <summary>Reorder to <paramref name="keys"/> (must be a permutation of the current keys).</summary>
    public void Reorder(IEnumerable<string> keys)
    {
        List<string> want = keys.ToList();
        if (want.Count != _items.Count || want.Distinct().Count() != want.Count
            || want.Any(k => !ContainsKey(k)))
            throw new ArgumentException("Reorder needs a permutation of the existing keys");
        List<KeyValuePair<string, string>> copy = want.Select(k => new KeyValuePair<string, string>(k, this[k])).ToList();
        _items.Clear();
        _items.AddRange(copy);
    }

    /// <summary>A fresh key "<paramref name="prefix"/>N" not yet in the map.</summary>
    public string NewKey(string prefix)
    {
        for (int i = 0; ; i++)
            if (!ContainsKey(prefix + i)) return prefix + i;
    }

    public OrderedTextMap Clone()
    {
        OrderedTextMap m = new OrderedTextMap();
        foreach (KeyValuePair<string, string> kv in _items) m._items.Add(kv);
        return m;
    }

    private int IndexOf(string key) => _items.FindIndex(kv => kv.Key == key);
}

internal sealed class OrderedTextMapConverter : JsonConverter<OrderedTextMap>
{
    public override OrderedTextMap Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
    {
        OrderedTextMap map = new OrderedTextMap();
        if (reader.TokenType == JsonTokenType.Null) return map;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("expected a JSON object for a text map");
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return map;
            string key = reader.GetString()!;
            reader.Read();
            map.Add(key, reader.GetString() ?? "");
        }
        throw new JsonException("unterminated text map");
    }

    public override void Write(Utf8JsonWriter w, OrderedTextMap value, JsonSerializerOptions o)
    {
        w.WriteStartObject();
        foreach (KeyValuePair<string, string> kv in value.Items) w.WriteString(kv.Key, kv.Value);
        w.WriteEndObject();
    }
}

/// <summary>
/// The complete semantic model of one `.S` trailer module.  Byte-exactness of
/// <see cref="WinRuleSynthesizer.Synth"/> comes from the layout + codegen rules
/// alone: this model holds NO offsets and NO code bytes.
/// </summary>
public sealed class WinRuleModel
{
    [JsonPropertyName("name")] public string Name { get; set; } = "?";
    /// <summary>Block offset of the 2-byte DS save slot (NOT always +0xA — RAMROD +0x55, STRAFE +0x7A).</summary>
    [JsonPropertyName("save_off")] public int SaveOff { get; set; } = 0x0A;
    /// <summary>Number of byte vars (kill counters AND one-shot radio flags) in the data region.</summary>
    [JsonPropertyName("n_counters")] public int NCounters { get; set; }
    /// <summary>"les" | "moves" | null (no briefing — FREE.S).</summary>
    [JsonPropertyName("fn0_style")] public string? Fn0Style { get; set; }
    /// <summary>"hoisted" | "per_arm" | "shared_tail" | null (no debrief copies).</summary>
    [JsonPropertyName("fn4_style")] public string? Fn4Style { get; set; }
    /// <summary>Briefing byte string (latin-1, control bytes included), or null.</summary>
    [JsonPropertyName("briefing")] public string? Briefing { get; set; }
    [JsonPropertyName("debrief_texts")] public OrderedTextMap DebriefTexts { get; set; } = new();
    [JsonPropertyName("helpers")] public List<WinRuleHelper> Helpers { get; set; } = new();
    [JsonPropertyName("fn1")] public List<WinRuleStmt> Fn1 { get; set; } = new();
    [JsonPropertyName("fn2")] public List<WinRuleStmt> Fn2 { get; set; } = new();
    [JsonPropertyName("fn3")] public WinRuleFn3 Fn3 { get; set; } = new();
    [JsonPropertyName("fn4")] public List<WinRuleStmt> Fn4 { get; set; } = new();
    /// <summary>
    /// Explicit data-region layout in source-declaration order.  null = the
    /// template default <c>[save, byte × n_counters]</c> (save at +0xA).
    /// </summary>
    [JsonPropertyName("data_layout")] public List<WinRuleDataItem>? DataLayout { get; set; }
    [JsonPropertyName("radio_texts")] public OrderedTextMap RadioTexts { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        // latin-1 payload bytes must survive; the writer is only used for
        // diagnostics / fixtures, but keep it lossless anyway.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static WinRuleModel FromJson(string json) =>
        JsonSerializer.Deserialize<WinRuleModel>(json, JsonOptions)
        ?? throw new InvalidDataException("empty win-rule model JSON");

    public static WinRuleModel FromJson(Stream json) =>
        JsonSerializer.Deserialize<WinRuleModel>(json, JsonOptions)
        ?? throw new InvalidDataException("empty win-rule model JSON");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public WinRuleModel Clone() => new()
    {
        Name = Name,
        SaveOff = SaveOff,
        NCounters = NCounters,
        Fn0Style = Fn0Style,
        Fn4Style = Fn4Style,
        Briefing = Briefing,
        DebriefTexts = DebriefTexts.Clone(),
        Helpers = Helpers.Select(h => h.Clone()).ToList(),
        Fn1 = Fn1.Select(s => s.Clone()).ToList(),
        Fn2 = Fn2.Select(s => s.Clone()).ToList(),
        Fn3 = Fn3.Clone(),
        Fn4 = Fn4.Select(s => s.Clone()).ToList(),
        DataLayout = DataLayout?.Select(d => d.Clone()).ToList(),
        RadioTexts = RadioTexts.Clone(),
    };

    /// <summary>
    /// The effective data layout (the template default materialized when
    /// <see cref="DataLayout"/> is null).
    /// </summary>
    public List<WinRuleDataItem> EffectiveLayout()
    {
        if (DataLayout is not null) return DataLayout;
        List<WinRuleDataItem> l = new List<WinRuleDataItem> { new() { Kind = "save" } };
        for (int i = 0; i < NCounters; i++) l.Add(new WinRuleDataItem { Kind = "byte" });
        return l;
    }

    /// <summary>Every statement of the module, depth-first (for consistency checks).</summary>
    public IEnumerable<WinRuleStmt> AllStatements()
    {
        IEnumerable<WinRuleStmt> Walk(IEnumerable<WinRuleStmt> list)
        {
            foreach (WinRuleStmt s in list)
            {
                yield return s;
                if (s.Body is not null)
                    foreach (WinRuleStmt x in Walk(s.Body)) yield return x;
            }
        }
        foreach (WinRuleStmt s in Walk(Fn1)) yield return s;
        foreach (WinRuleStmt s in Walk(Fn2)) yield return s;
        foreach (WinRuleStmt s in Walk(Fn3.Pre)) yield return s;
        foreach (WinRuleStmt s in Walk(Fn3.Extra)) yield return s;
        foreach (WinRuleStmt s in Walk(Fn4)) yield return s;
    }

    /// <summary>
    /// Model-level consistency (cheap, pre-synthesis): counters in range, text
    /// keys defined, helper indices valid, layout agrees with save_off /
    /// n_counters, texts encodable as latin-1.  Returns the problems found.
    /// </summary>
    public List<string> Validate()
    {
        List<string> errs = new List<string>();
        List<WinRuleDataItem> layout = EffectiveLayout();
        int saveAt = -1, bytes = 0, pos = 10;
        foreach (WinRuleDataItem item in layout)
        {
            switch (item.Kind)
            {
                case "save":
                    if (saveAt >= 0) errs.Add("data layout declares two DS save slots");
                    saveAt = pos; pos += 2; break;
                case "byte":
                    bytes++; pos += 1; break;
                case "rtext":
                    if (item.Key is null || !RadioTexts.ContainsKey(item.Key))
                        errs.Add($"data layout references undefined radio text '{item.Key}'");
                    else pos += Latin1Len(RadioTexts[item.Key]);
                    break;
                default:
                    errs.Add($"unknown data item kind '{item.Kind}'"); break;
            }
        }
        if (saveAt < 0) errs.Add("data layout lacks the DS save slot");
        else if (saveAt != SaveOff) errs.Add($"save slot is at +0x{saveAt:X} but save_off says +0x{SaveOff:X}");
        if (bytes != NCounters) errs.Add($"data layout has {bytes} byte vars but n_counters = {NCounters}");

        foreach (WinRuleHelper h in Helpers)
        {
            if (h.Polarity is not ("stc_first" or "clc_first"))
                errs.Add($"bad helper polarity '{h.Polarity}'");
            if (h.Tests.Count == 0) errs.Add("a win-predicate helper has no tests");
            foreach (WinRuleTest t in h.Tests) CheckTest(t, errs);
        }

        foreach (WinRuleStmt s in AllStatements())
        {
            switch (s.Op)
            {
                case "inc":
                    if (s.Counter is null || s.Counter < 0 || s.Counter >= NCounters)
                        errs.Add($"inc of byte var #{s.Counter} — only 0..{NCounters - 1} exist");
                    break;
                case "copy":
                    if (s.Text is null || !DebriefTexts.ContainsKey(s.Text))
                        errs.Add($"copy of undefined text '{s.Text}'");
                    break;
                case "msg":
                    if (s.Text is null || !RadioTexts.ContainsKey(s.Text))
                        errs.Add($"msg references undefined radio text '{s.Text}'");
                    else if (DataLayout is null || !DataLayout.Any(d => d.Kind == "rtext" && d.Key == s.Text))
                        errs.Add($"radio text '{s.Text}' is not placed in the data layout");
                    break;
                case "ax":
                    if (s.Val is not (0 or 1)) errs.Add($"ax value {s.Val} — only 0 and 1 are encodable");
                    break;
                case "if":
                    if (s.Helper is not null && (s.Helper < 0 || s.Helper >= Helpers.Count))
                        errs.Add($"if calls helper #{s.Helper} — only {Helpers.Count} helper(s) exist");
                    if (s.Helper is null && (s.Tests is null || s.Tests.Count == 0))
                        errs.Add("if with neither a helper nor tests");
                    foreach (WinRuleTest t in s.Tests ?? new List<WinRuleTest>()) CheckTest(t, errs);
                    break;
                case "activate":
                    if (s.Slots is null || s.Slots.Count == 0) errs.Add("activate with no actor slots");
                    break;
                case "ret":
                case "dx0":
                    break;
                default:
                    errs.Add($"unknown statement op '{s.Op}'");
                    break;
            }
        }

        // fn0 needs a briefing to copy, and vice versa
        if (Fn0Style is null && Briefing is not null)
            errs.Add("a briefing text is defined but fn0 has no copy style");
        if (Fn0Style is not null && Briefing is null)
            errs.Add($"fn0 style '{Fn0Style}' needs a briefing text");
        if (Fn3.Kind == "template" && (Fn3.Helper is null || Fn3.Helper >= Helpers.Count))
            errs.Add("check_win is 'template' but names no valid win-predicate helper");
        if (Fn4.Count > 0 && Fn4Style is null)
            errs.Add("fn4 has statements but no copy style");

        foreach ((string label, string text) in AllTexts())
            for (int i = 0; i < text.Length; i++)
                if (text[i] > 0xFF)
                    errs.Add($"{label}: character U+{(int)text[i]:X4} at {i} is not latin-1 encodable");
        return errs;
    }

    private void CheckTest(WinRuleTest t, List<string> errs)
    {
        switch (t.Var)
        {
            case "arg_w":
            case "clock":
                if (t.Imm is < 0 or > 0x7F)
                    errs.Add($"{t.Var} compare against {t.Imm}: the sign-extended imm8 form needs 0..127");
                break;
            case "arg_b":
                if (t.Imm is < 0 or > 0xFF) errs.Add($"arg_b compare against {t.Imm} is not a byte");
                break;
            case "ctr":
                if (t.Counter is null || t.Counter < 0 || t.Counter >= NCounters)
                    errs.Add($"test on byte var #{t.Counter} — only 0..{NCounters - 1} exist");
                if (t.Imm is < 0 or > 0xFF) errs.Add($"counter compare against {t.Imm} is not a byte");
                break;
            default:
                errs.Add($"unknown test var '{t.Var}'");
                break;
        }
        if (!WinRuleSynthesizer.JccOpcodes.ContainsKey(t.Cc))
            errs.Add($"unknown condition code '{t.Cc}'");
    }

    private IEnumerable<(string, string)> AllTexts()
    {
        if (Briefing is not null) yield return ("briefing", Briefing);
        foreach (KeyValuePair<string, string> kv in DebriefTexts.Items) yield return ($"debrief text {kv.Key}", kv.Value);
        foreach (KeyValuePair<string, string> kv in RadioTexts.Items) yield return ($"radio text {kv.Key}", kv.Value);
    }

    public static int Latin1Len(string s) => Encoding.Latin1.GetByteCount(s);

    /// <summary>
    /// A normalized JSON rendering used for MODEL EQUALITY (the pilot-13
    /// oracle compares a C#-edited model against the model the Python
    /// extractor recovers from the emitted bytes).  Null-valued fields are
    /// dropped so that the two writers' null padding cannot matter, while
    /// text-map and layout ORDER is preserved because it is semantic.
    /// </summary>
    public string CanonicalJson()
    {
        JsonNode node = JsonNode.Parse(JsonSerializer.Serialize(this, JsonOptions))!;
        StripNulls(node);
        return node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static void StripNulls(JsonNode node)
    {
        if (node is JsonObject o)
        {
            foreach (string key in o.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
                o.Remove(key);
            foreach (KeyValuePair<string, JsonNode?> kv in o.ToList()) StripNulls(kv.Value!);
        }
        else if (node is JsonArray a)
        {
            foreach (JsonNode? item in a) if (item is not null) StripNulls(item);
        }
    }
}
