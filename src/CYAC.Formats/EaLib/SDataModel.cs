using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CYAC.Formats.EaLib;

// ---------------------------------------------------------------------------
// `.S` / `.W` DATA-SECTION AUTHORING MODEL
//
// This is the semantic, JSON-serializable EDITABLE DOCUMENT of everything in a
// mission container EXCEPT the trailer x86 module (which is carried opaque here
// and owned by <see cref="WinRuleModel"/> / <see cref="WinRuleSynthesizer"/>).
// It sits on top of <see cref="SMissionDecoder"/> the way s_data_synth.py sits
// on top: the decoder is the byte grammar (a transcription of the engine's own
// tag interpreter `wld_or_s_asset_parser @image@0x096F2`), and this projects
// that stream into content the editor can author.
//
// SCHEMA PARITY IS A CONTRACT.  The JSON emitted here is EXACTLY the python
// tool's `json.dumps(extract(body))` shape — same keys, same order, same
// advisory `_`-prefixed annotations — so the 54 canonical models in deserialize
// here unchanged and a model authored here feeds `python3 --synth` unchanged.
// `--selftest- datasynth` proves both directions.
//
//   * an object = opener (kind) + ordered attributes + ONE position that closes
//     it; stream ORDER is semantic (rel_prev chains, slot re-registration is
//     last-writer-wins, a script sees the coordinates registered before it);
//   * attr 0x86's byte is the object's EXPLICIT actor/place SLOT index — the
//     thing win-rule victim indexes, AI-script place refs and pos-tag-6
//     references all point at.  [0xEE5A] is u16[13] => slots 0..12 are the
//     scarce authoring resource;
//   * coordinates are stored in WORLD UNITS: the stream i24 is value<<8
//     (read_3bytes @image@0x0A403), whose low byte is always 0.
// ---------------------------------------------------------------------------

/// <summary>Raised by the data-section model / synthesizer (python: ModelError).</summary>
public sealed class SDataException : Exception
{
    public SDataException(string msg) : base(msg) { }
}

/// <summary>
/// A NUL-terminated stream string.  Printable ASCII round-trips as text; any
/// other byte run is carried as hex (python `_text_or_hex`).
/// </summary>
public sealed class SDataText
{
    private SDataText(string? text, byte[]? raw) { Text = text; Raw = raw; }

    /// <summary>The ASCII text WITHOUT its NUL, or null when the run is hex-carried.</summary>
    public string? Text { get; }

    /// <summary>The full byte run INCLUDING the terminator, when not ASCII.</summary>
    public byte[]? Raw { get; }

    public static SDataText FromString(string s) => new(s, null);

    public static SDataText FromBytes(byte[] b)
    {
        // python `_text_or_hex`: ASCII + trailing NUL -> text, else hex
        if (b.Length > 0 && b[^1] == 0 && b[..^1].All(c => c >= 0x20 && c <= 0x7E))
            return new SDataText(Encoding.ASCII.GetString(b, 0, b.Length - 1), null);
        return new SDataText(null, b);
    }

    public byte[] ToBytes() =>
        Raw ?? Encoding.ASCII.GetBytes(Text!).Concat(new byte[] { 0 }).ToArray();

    /// <summary>What a human sees in the editor (hex runs shown as an escape).</summary>
    public string Display => Text ?? "0x" + Convert.ToHexString(Raw!);

    public JsonNode ToJson() =>
        Text is not null ? JsonValue.Create(Text)! : new JsonObject { ["hex"] = Convert.ToHexString(Raw!).ToLowerInvariant() };

    public static SDataText FromJson(JsonNode n) =>
        n is JsonObject o
            ? new SDataText(null, Convert.FromHexString((string)o["hex"]!))
            : new SDataText((string)n!, null);

    public SDataText Clone() => new(Text, Raw?.ToArray());
}

/// <summary>Section-1 site record: type + world-unit x/z (+ subtype for types 1..7).</summary>
public sealed class SDataSite
{
    public int Type { get; set; }
    public int X { get; set; }
    public int Z { get; set; }
    public int? Subtype { get; set; }

    public SDataSite Clone() => new() { Type = Type, X = X, Z = Z, Subtype = Subtype };
}

/// <summary>One object attribute, named with the authoring vocabulary.</summary>
public sealed class SDataAttr
{
    public required string Attr { get; set; }

    /// <summary>byte / word operand.</summary>
    public int Value { get; set; }

    /// <summary>attr 0x99's three words.</summary>
    public int[]? Words { get; set; }

    /// <summary>attr 0x97's interned string.</summary>
    public SDataText? Text { get; set; }

    /// <summary>attr 0x89's authored AI script, carried as an OPAQUE unit.</summary>
    public byte[]? Script { get; set; }

    public byte Tag => SDataModel.AttrTag(Attr);
    public SMissionDecoder.OperandKind Kind => SMissionDecoder.AttrOperands[Tag];

    public SDataAttr Clone() => new()
    {
        Attr = Attr,
        Value = Value,
        Words = Words?.ToArray(),
        Text = Text?.Clone(),
        Script = Script?.ToArray(),
    };
}

/// <summary>The position that CLOSES an object (python `pos` sub-object).</summary>
public sealed class SDataPos
{
    public required string Pos { get; set; }

    /// <summary>tags 0/4/5/6: x,y,z world units.  tag 1: x,z.</summary>
    public int[] Coords { get; set; } = Array.Empty<int>();

    /// <summary>tag 2: the section-1 site TYPE (the engine picks a random unvisited site of it).</summary>
    public int SiteType { get; set; }

    /// <summary>tag 6: the earlier object's actor slot the offsets are relative to.</summary>
    public int Place { get; set; }

    /// <summary>tag 7: three actor-slot bytes (0xFF = unused).</summary>
    public int[]? Slots { get; set; }

    /// <summary>tag 3: the [3 eras][subtype_max] coordinate matrix (dormant).</summary>
    public List<int[]>? Rows { get; set; }

    public byte Tag => SDataModel.PosTag(Pos);

    public SDataPos Clone() => new()
    {
        Pos = Pos,
        Coords = Coords.ToArray(),
        SiteType = SiteType,
        Place = Place,
        Slots = Slots?.ToArray(),
        Rows = Rows?.Select(r => r.ToArray()).ToList(),
    };

    public static SDataPos Absolute(int x, int y, int z) =>
        new() { Pos = "absolute", Coords = new[] { x, y, z } };
    public static SDataPos RelPrev(int x, int y, int z) =>
        new() { Pos = "rel_prev", Coords = new[] { x, y, z } };
    public static SDataPos RelSpawn(int x, int y, int z) =>
        new() { Pos = "rel_spawn_origin", Coords = new[] { x, y, z } };
    public static SDataPos RelPlace(int place, int x, int y, int z) =>
        new() { Pos = "rel_place", Place = place, Coords = new[] { x, y, z } };
    public static SDataPos AtSite(int siteType) =>
        new() { Pos = "at_site", SiteType = siteType };
    public static SDataPos Ground(int x, int z) =>
        new() { Pos = "ground", Coords = new[] { x, z } };
    public static SDataPos TrackActors(int a, int b, int c) =>
        new() { Pos = "track_actors", Slots = new[] { a, b, c } };
}

/// <summary>A directive or an object — the stream is an ordered list of these.</summary>
public abstract class SDataItem
{
    public abstract SDataItem Clone();
}

/// <summary>Header-level directive (no object opens).</summary>
public sealed class SDataDirective : SDataItem
{
    public required string Directive { get; set; }
    public int Value { get; set; }

    /// <summary>directive 0x88's four world-extent coordinates.</summary>
    public int[]? Coords { get; set; }

    public byte Tag => SDataModel.DirectiveTag(Directive);
    public SMissionDecoder.OperandKind Kind => SMissionDecoder.DirectiveOperands[Tag];

    public override SDataItem Clone() =>
        new SDataDirective { Directive = Directive, Value = Value, Coords = Coords?.ToArray() };
}

/// <summary>One placed object: opener + ordered attributes + closing position.</summary>
public sealed class SDataObject : SDataItem
{
    /// <summary>"class" | "marker" | "named_mesh" | "nav_waypoint" | "ground_fx" | "prim_4d00".</summary>
    public required string Object { get; set; }

    /// <summary>Class-table id, for <c>Object == "class"</c> (0 PLAYER, 1 HOME-BASE-POS, 6..24 craft).</summary>
    public int ClassId { get; set; }

    /// <summary>opener 0x04's lead byte = the nav-waypoint slot index (0..2).</summary>
    public int NavSlot { get; set; }

    /// <summary>opener 0x03 / 0x04 label.</summary>
    public SDataText? Label { get; set; }

    public List<SDataAttr> Attrs { get; set; } = new();
    public required SDataPos Pos { get; set; }

    public override SDataItem Clone() => new SDataObject
    {
        Object = Object,
        ClassId = ClassId,
        NavSlot = NavSlot,
        Label = Label?.Clone(),
        Attrs = Attrs.Select(a => a.Clone()).ToList(),
        Pos = Pos.Clone(),
    };

    public SDataAttr? Attr(string name) => Attrs.FirstOrDefault(a => a.Attr == name);
    public bool HasAttr(string name) => Attr(name) is not null;

    // The model keeps the class id; a caller that shows a name brings its own lookup, read from
    // the originals: SDataModel.ToJsonNode takes one.
}

/// <summary>Trailer item: the opaque module block, or an ignored filler byte.</summary>
public sealed class SDataTrailerItem
{
    /// <summary>The module block payload (null for a skip byte).</summary>
    public byte[]? Module { get; set; }

    /// <summary>The ignored byte (python `skip`), when <see cref="Module"/> is null.</summary>
    public int Skip { get; set; }

    public SDataTrailerItem Clone() => new() { Module = Module?.ToArray(), Skip = Skip };
}

/// <summary>
/// The complete authoring model of one `.S` / `.W` container's DATA sections.
/// <see cref="SDataSynthesizer.Synth"/> derives every framing field (header
/// table_off, sec1_count, name-table counts, script length words) from content,
/// so the model holds mission CONTENT only.
/// </summary>
public sealed class SDataModel
{
    public const string FormatTag = "cyac-s-data-model-v1";

    public string Format { get; set; } = FormatTag;
    public string Name { get; set; } = "?";
    /// <summary>"S" (mission) or "W" (theater catalog).</summary>
    public string Kind { get; set; } = "S";
    public List<SDataSite> Sites { get; set; } = new();
    public List<SDataText> Names { get; set; } = new();
    public List<SDataItem> Stream { get; set; } = new();
    public List<SDataTrailerItem> Trailer { get; set; } = new();

    public IEnumerable<SDataObject> Objects => Stream.OfType<SDataObject>();
    public IEnumerable<SDataDirective> Directives => Stream.OfType<SDataDirective>();

    /// <summary>The opaque trailer module block, if the container has one.</summary>
    public byte[]? ModuleBlock =>
        Trailer.FirstOrDefault(t => t.Module is not null)?.Module;

    public SDataModel Clone() => new()
    {
        Format = Format,
        Name = Name,
        Kind = Kind,
        Sites = Sites.Select(s => s.Clone()).ToList(),
        Names = Names.Select(n => n.Clone()).ToList(),
        Stream = Stream.Select(i => i.Clone()).ToList(),
        Trailer = Trailer.Select(t => t.Clone()).ToList(),
    };

    // =======================================================================
    // vocabulary (names = the authoring vocabulary; tags are the byte truth)
    // =======================================================================

    /// <summary>Header-level directive tag -> authoring name (python DIRECTIVE_NAMES).</summary>
    public static readonly IReadOnlyDictionary<byte, string> DirectiveNames = new Dictionary<byte, string>
    {
        [0x81] = "unknown81",                  // byte, discarded    image@0x09A4F
        [0x82] = "unknown82",
        [0x83] = "subtype_max",                // -> [0xB54A]
        [0x84] = "era_filter",                 // -> [0xEE32]
        [0x88] = "world_extents",              // 4 x i24 -> [0xF0E6]
        [0x89] = "scene_flag_EDE1_clear",
        [0x8A] = "render_flag_F0E4_set",
        [0x95] = "player_aircraft",            // -> [0xC31A]
        [0x9A] = "mission_altitude",           // -> [0xF100] (cloud-deck datum)
        [0x9D] = "render_flag_F0E5_clear",
        [0x9E] = "opponent_class",             // -> [0xF1C6]
    };

    /// <summary>Attribute tag -> authoring name (python ATTR_NAMES).</summary>
    public static readonly IReadOnlyDictionary<byte, string> AttrNames = new Dictionary<byte, string>
    {
        [0x80] = "aux0_heading",     // word; spawn aux +0x12 (heading, 1/8 deg — hypothesis)
        [0x84] = "era_match",        // byte vs [0xEE32]
        [0x86] = "actor_slot",       // THE slot index ([0xEE5A]/[0xEE74]/[0xEF10])
        [0x87] = "target_table",     // flag -> g_active_target_table push
        [0x88] = "place_type",       // byte -> [0xEF10] slot type
        [0x89] = "ai_script",        // opaque script unit
        [0x8A] = "script_flag_01",
        [0x8B] = "script_flag_04",
        [0x8C] = "script_flag_08",   // dormant
        [0x8D] = "script_flag_10",
        [0x8E] = "script_flag_20",
        [0x8F] = "clear_objflag_40",
        [0x90] = "engage_class_0",
        [0x91] = "engage_class_1",
        [0x92] = "engage_class_2",
        [0x93] = "engage_class_3",
        [0x94] = "objflag_80",
        [0x96] = "skill",            // byte -> engagement slot +0x1D
        [0x97] = "pilot_name",       // interned string -> slot +0x1E
        [0x98] = "initial_speed",    // word (player -> [0xEE52]; others -> slot+0x25 <<8)
        [0x99] = "aux_vector",       // 3 words
        [0x9B] = "objflag_100",
        [0x9C] = "player_timer_seed",
        [0x9F] = "objflag_400",
    };

    public static readonly IReadOnlyDictionary<byte, string> PosNames = new Dictionary<byte, string>
    {
        [0] = "absolute", [1] = "ground", [2] = "at_site", [3] = "era_matrix",
        [4] = "rel_prev", [5] = "rel_spawn_origin", [6] = "rel_place", [7] = "track_actors",
    };

    // The model keeps ids.  The class table @image@0x34F90 holds 3-byte records {u8 flag, u16
    // value}: ids 6..24 are flag 0, a DGROUP pointer to the aircraft's stat block (engagement
    // prototype) whose +0x04 word points at its designation; ids 26..45 (no 35) are flag 1, a DGROUP
    // pointer to a mesh-registry descriptor whose +0x22 near pointer (in the geometry segment at
    // +0x26) names the scenery mesh (class 31 -> 0x8EF6 -> desc@image@0x44C56 -> its
    // basename); ids 0 and 1 are the player / home-base sentinels.  The transform reads the names
    // through that table (CYAC.Port.Transform OriginalNames); the tables as decoded before this
    // tables live on in the project's own tooling, where the mission editor's --selftest-map
    // still re-derives the scenery half from the image.

    /// <summary>
    /// Opener 0x19 hardcodes prim 0x4D00 — the SAME descriptor class 26 points
    /// at, so a <c>prim_4d00</c> object is an airport (P15 §opener census).
    /// </summary>
    public const string Prim4D00Name = "airport";

    /// <summary>The class id whose mesh descriptor <c>prim_4d00</c> shares (26 = airport).</summary>
    public const int Prim4D00ClassId = 26;

    // Needs the names; CYAC.Port.Transform OriginalNames.DescribeClass.

    /// <summary>
    /// The `.W` theater catalog an ERA loads.  <c>scenario_load_dispatch</c>
    /// @image@0x09305 reads the era selector <c>[0x2A0E]</c>
    /// (<c>mov bl,[0x2A0E]</c> @image@0x0932D), doubles it and indexes a
    /// 3-entry near-pointer table at DGROUP+0x0FAA (image@0x3CD0A)
    /// (<c>push [bx+0x0FAA]</c> @image@0x09335) whose targets are the ASCII
    /// names <c>"germany.w"</c> / <c>"korea.w"</c> / <c>"vietnam.w"</c>
    /// (image@0x3CCE2 / 0x3CCEC / 0x3CCF4).  A mission's era byte (+0x01 of its
    /// scenario record) is what <c>scenario_filter_by_era</c> @image@0x24B24
    /// matches against that selector, so era IS theater.
    /// </summary>
    public static readonly string[] TheaterAssetForEra = { "GERMANY.W", "KOREA.W", "VIETNAM.W" };

    private static readonly Dictionary<string, byte> _dirByName =
        DirectiveNames.ToDictionary(kv => kv.Value, kv => kv.Key);
    private static readonly Dictionary<string, byte> _attrByName =
        AttrNames.ToDictionary(kv => kv.Value, kv => kv.Key);
    private static readonly Dictionary<string, byte> _posByName =
        PosNames.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static byte DirectiveTag(string name) =>
        _dirByName.TryGetValue(name, out byte t) ? t
            : throw new SDataException($"unknown directive '{name}'");

    public static byte AttrTag(string name) =>
        _attrByName.TryGetValue(name, out byte t) ? t
            : throw new SDataException($"unknown attr '{name}'");

    public static byte PosTag(string name) =>
        _posByName.TryGetValue(name, out byte t) ? t
            : throw new SDataException($"unknown position kind '{name}'");

    public static string AttrName(byte tag) => AttrNames[tag];

    // =======================================================================
    // engine-derived authoring limits (mission-independent)
    // =======================================================================

    /// <summary>[0xEE5A] is u16[13]: slots 0..12 (the scarce authoring resource).</summary>
    public const int MaxActorSlot = 12;
    public const int SlotBudget = 13;

    /// <summary>Shipped nav-slot range (g_nav_slot_record_array [0xB564], stride 0x2C).</summary>
    public const int MaxNavSlot = 2;

    /// <summary>The parser stages a script into a 712-B stack buffer [bp-0x2C8] (image@0x09D2D).</summary>
    public const int MaxScriptBytes = 0x2C8;

    /// <summary>Opcodes the load-time patcher @0x094EF cannot skip — it desyncs.</summary>
    public static readonly IReadOnlySet<byte> ScriptPatcherUnsafe = new HashSet<byte> { 0x0B, 0x0C, 0xDC };

    public const int WorldUnitMin = -0x800000, WorldUnitMax = 0x7FFFFF;

    /// <summary>
    /// AI-script raw-form operand widths, transcribed from the load-time patcher
    /// <c>ai_script_named_place_patch</c> @image@0x094EF.  -1 = NUL-terminated string.
    /// Encoding: <c>[delay u16][op][operands]</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<byte, int> ScriptRawWidths = BuildScriptWidths();

    private static Dictionary<byte, int> BuildScriptWidths()
    {
        Dictionary<byte, int> w = new Dictionary<byte, int>
        {
            [0x00] = 0, [0x02] = 9, [0x08] = 0, [0x09] = 0, [0x0A] = 8, [0x0D] = 7,
            [0xD5] = 2, [0xD6] = 8, [0xD7] = 2, [0xD8] = 2, [0xD9] = 2, [0xDA] = 0,
            [0xDB] = 0, [0xDD] = 0, [0xDE] = 2, [0xDF] = -1, [0xE0] = 2, [0xE1] = 0,
            [0xE2] = 0, [0xFE] = 0, [0xFF] = 0, [0xFD] = 9,
        };
        for (int k = 0xE3; k < 0xF0; k++) w[(byte)k] = 6;   // NAMED_PLACE_HEADING rel
        for (int k = 0xF0; k < 0xFD; k++) w[(byte)k] = 9;   // NAMED_PLACE_ORIENT rel
        return w;
    }

    /// <summary>One decoded script step (the opaque unit stays opaque; this only walks it).</summary>
    public readonly record struct ScriptStep(int Offset, int Delay, byte Op, byte[] Args);

    /// <summary>
    /// Walk an authored script in RAW form, the way the load-time patcher does.
    /// Throws <see cref="SDataException"/> on desync / unknown / patcher-unsafe
    /// opcode — the same rejections python's <c>script_walk</c> makes.
    /// </summary>
    public static IEnumerable<ScriptStep> ScriptWalk(byte[] p)
    {
        int i = 0;
        while (i < p.Length)
        {
            if (i + 3 > p.Length) throw new SDataException($"script truncated at +0x{i:X}");
            int delay = p[i] | (p[i + 1] << 8);
            byte op = p[i + 2];
            i += 3;
            if (ScriptPatcherUnsafe.Contains(op))
                throw new SDataException(
                    $"script opcode 0x{op:X2} desyncs the load-time patcher @0x094EF at +0x{i - 1:X}");
            if (!ScriptRawWidths.TryGetValue(op, out int w))
                throw new SDataException(
                    $"script opcode 0x{op:X2} outside the patcher alphabet at +0x{i - 1:X}");
            if (w < 0)
            {
                int j = Array.IndexOf(p, (byte)0, i);
                if (j < 0) throw new SDataException($"script string at +0x{i:X} is unterminated");
                yield return new ScriptStep(i - 3, delay, op, p[i..(j + 1)]);
                i = j + 1;
            }
            else
            {
                if (i + w > p.Length) throw new SDataException($"script truncated at +0x{i:X}");
                yield return new ScriptStep(i - 3, delay, op, p[i..(i + w)]);
                i += w;
            }
        }
    }

    /// <summary>Place / actor slots a script references (patched at load time).</summary>
    public static (List<int> Places, List<int> Actors) ScriptRefs(byte[] p)
    {
        SortedSet<int> places = new SortedSet<int>();
        SortedSet<int> actors = new SortedSet<int>();
        foreach (ScriptStep s in ScriptWalk(p))
        {
            if (s.Op >= 0xE3 && s.Op <= 0xEF) places.Add(s.Op - 0xE3);
            else if (s.Op >= 0xF0 && s.Op <= 0xFC) places.Add(s.Op - 0xF0);
            else if (s.Op == 0x0A && s.Args.Length >= 2) actors.Add(s.Args[0] | (s.Args[1] << 8));
        }
        return (places.ToList(), actors.ToList());
    }

    /// <summary>Validate an authored script against the engine's parse/patch limits.</summary>
    public static string? ValidateScript(byte[] script)
    {
        if (script.Length > MaxScriptBytes)
            return $"ai_script {script.Length} B > {MaxScriptBytes} (parser staging buffer [bp-0x2C8])";
        try { foreach (ScriptStep _ in ScriptWalk(script)) { } }
        catch (SDataException ex) { return ex.Message; }
        return null;
    }

    // =======================================================================
    // JSON (exact schema parity)
    // =======================================================================

    /// <param name="className">
    /// The name to annotate a <c>class</c> object with (<c>_class_name</c>, right after <c>class</c>),
    /// or null for none.  The model carries only ids; the names come from the caller, which reads them
    /// from the originals.
    /// </param>
    public JsonObject ToJsonNode(Func<int, string?>? className = null)
    {
        JsonObject o = new JsonObject
        {
            ["format"] = Format,
            ["name"] = Name,
            ["kind"] = Kind,
        };
        JsonArray sites = new JsonArray();
        foreach (SDataSite s in Sites)
        {
            JsonObject js = new JsonObject { ["type"] = s.Type, ["x"] = s.X, ["z"] = s.Z };
            if (s.Subtype is not null) js["subtype"] = s.Subtype;
            sites.Add(js);
        }
        o["sites"] = sites;

        JsonArray names = new JsonArray();
        foreach (SDataText n in Names) names.Add(n.ToJson());
        o["names"] = names;

        JsonArray stream = new JsonArray();
        foreach (SDataItem item in Stream) stream.Add(ItemToJson(item, className));
        o["stream"] = stream;

        JsonArray trailer = new JsonArray();
        foreach (SDataTrailerItem t in Trailer)
            trailer.Add(t.Module is not null
                ? new JsonObject { ["module_hex"] = Convert.ToHexString(t.Module).ToLowerInvariant() }
                : new JsonObject { ["skip"] = t.Skip });
        o["trailer"] = trailer;
        return o;
    }

    private static JsonNode ItemToJson(SDataItem item, Func<int, string?>? className)
    {
        if (item is SDataDirective d)
        {
            JsonObject jd = new JsonObject { ["directive"] = d.Directive };
            if (d.Kind == SMissionDecoder.OperandKind.Coords4)
                jd["value"] = new JsonArray(d.Coords!.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
            else if (d.Kind != SMissionDecoder.OperandKind.None)
                jd["value"] = d.Value;
            return jd;
        }

        SDataObject ob = (SDataObject)item;
        JsonObject jo = new JsonObject { ["object"] = ob.Object };
        switch (ob.Object)
        {
            case "named_mesh":
                jo["label"] = ob.Label!.ToJson();
                break;
            case "nav_waypoint":
                jo["nav_slot"] = ob.NavSlot;
                jo["label"] = ob.Label!.ToJson();
                break;
            case "class":
                jo["class"] = ob.ClassId;
                if (className?.Invoke(ob.ClassId) is { } cn) jo["_class_name"] = cn;
                break;
        }

        JsonArray attrs = new JsonArray();
        foreach (SDataAttr a in ob.Attrs)
        {
            JsonObject ja = new JsonObject { ["attr"] = a.Attr };
            if (a.Attr == "ai_script")
            {
                ja["hex"] = Convert.ToHexString(a.Script!).ToLowerInvariant();
                JsonObject refs;
                try
                {
                    (List<int> places, List<int> actors) = ScriptRefs(a.Script!);
                    refs = new JsonObject
                    {
                        ["places"] = new JsonArray(places.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                        ["actors"] = new JsonArray(actors.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                    };
                }
                catch (SDataException ex) { refs = new JsonObject { ["error"] = ex.Message }; }
                ja["_refs"] = refs;
            }
            else
            {
                switch (a.Kind)
                {
                    case SMissionDecoder.OperandKind.None: break;
                    case SMissionDecoder.OperandKind.String: ja["value"] = a.Text!.ToJson(); break;
                    case SMissionDecoder.OperandKind.Word3:
                        ja["value"] = new JsonArray(a.Words!.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
                        break;
                    default: ja["value"] = a.Value; break;
                }
            }
            attrs.Add(ja);
        }
        jo["attrs"] = attrs;

        SDataPos p = ob.Pos;
        JsonObject jp = new JsonObject { ["pos"] = p.Pos };
        switch (p.Tag)
        {
            case 0: case 4: case 5:
                jp["xyz"] = Coords(p.Coords); break;
            case 1:
                jp["xz"] = Coords(p.Coords); break;
            case 2:
                jp["site_type"] = p.SiteType; break;
            case 3:
                jp["rows"] = new JsonArray(p.Rows!.Select(r => (JsonNode)Coords(r)).ToArray()); break;
            case 6:
                jp["place"] = p.Place;
                jp["xyz"] = Coords(p.Coords);
                break;
            case 7:
                jp["slots"] = Coords(p.Slots!); break;
        }
        jo["pos"] = jp;
        return jo;

        static JsonArray Coords(int[] v) => new(v.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
    }

    public static SDataModel FromJsonNode(JsonObject o)
    {
        string fmt = (string?)o["format"] ?? "";
        if (fmt != FormatTag) throw new SDataException($"not a {FormatTag} document (got '{fmt}')");
        SDataModel m = new SDataModel
        {
            Format = fmt,
            Name = (string?)o["name"] ?? "?",
            Kind = (string?)o["kind"] ?? "S",
        };
        foreach (JsonNode? s in (JsonArray)o["sites"]!)
        {
            JsonObject js = (JsonObject)s!;
            m.Sites.Add(new SDataSite
            {
                Type = (int)js["type"]!,
                X = (int)js["x"]!,
                Z = (int)js["z"]!,
                Subtype = js.ContainsKey("subtype") ? (int)js["subtype"]! : null,
            });
        }
        foreach (JsonNode? n in (JsonArray)o["names"]!) m.Names.Add(SDataText.FromJson(n!));
        foreach (JsonNode? it in (JsonArray)o["stream"]!) m.Stream.Add(ItemFromJson((JsonObject)it!));
        foreach (JsonNode? t in (JsonArray)o["trailer"]!)
        {
            JsonObject jt = (JsonObject)t!;
            m.Trailer.Add(jt.ContainsKey("module_hex")
                ? new SDataTrailerItem { Module = Convert.FromHexString((string)jt["module_hex"]!) }
                : new SDataTrailerItem { Skip = (int)jt["skip"]! });
        }
        return m;
    }

    private static SDataItem ItemFromJson(JsonObject j)
    {
        if (j.ContainsKey("directive"))
        {
            SDataDirective d = new SDataDirective { Directive = (string)j["directive"]! };
            if (j["value"] is JsonArray arr) d.Coords = arr.Select(v => (int)v!).ToArray();
            else if (j["value"] is not null) d.Value = (int)j["value"]!;
            return d;
        }

        JsonObject jp = (JsonObject)j["pos"]!;
        SDataPos pos = new SDataPos { Pos = (string)jp["pos"]! };
        switch (pos.Tag)
        {
            case 0: case 4: case 5:
                pos.Coords = ((JsonArray)jp["xyz"]!).Select(v => (int)v!).ToArray(); break;
            case 1:
                pos.Coords = ((JsonArray)jp["xz"]!).Select(v => (int)v!).ToArray(); break;
            case 2:
                pos.SiteType = (int)jp["site_type"]!; break;
            case 3:
                pos.Rows = ((JsonArray)jp["rows"]!)
                    .Select(r => ((JsonArray)r!).Select(v => (int)v!).ToArray()).ToList();
                break;
            case 6:
                pos.Place = (int)jp["place"]!;
                pos.Coords = ((JsonArray)jp["xyz"]!).Select(v => (int)v!).ToArray();
                break;
            case 7:
                pos.Slots = ((JsonArray)jp["slots"]!).Select(v => (int)v!).ToArray(); break;
        }

        SDataObject o = new SDataObject { Object = (string)j["object"]!, Pos = pos };
        if (j.ContainsKey("class")) o.ClassId = (int)j["class"]!;
        if (j.ContainsKey("nav_slot")) o.NavSlot = (int)j["nav_slot"]!;
        if (j["label"] is JsonNode lbl) o.Label = SDataText.FromJson(lbl);
        foreach (JsonNode? a in (JsonArray)j["attrs"]!)
        {
            JsonObject ja = (JsonObject)a!;
            SDataAttr attr = new SDataAttr { Attr = (string)ja["attr"]! };
            if (attr.Attr == "ai_script") attr.Script = Convert.FromHexString((string)ja["hex"]!);
            else
                switch (attr.Kind)
                {
                    case SMissionDecoder.OperandKind.None: break;
                    case SMissionDecoder.OperandKind.String: attr.Text = SDataText.FromJson(ja["value"]!); break;
                    case SMissionDecoder.OperandKind.Word3:
                        attr.Words = ((JsonArray)ja["value"]!).Select(v => (int)v!).ToArray(); break;
                    default: attr.Value = (int)ja["value"]!; break;
                }
            o.Attrs.Add(attr);
        }
        return o;
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson(Func<int, string?>? className = null) => ToJsonNode(className).ToJsonString(JsonOptions);

    public static SDataModel FromJson(string json) =>
        FromJsonNode(JsonNode.Parse(json) as JsonObject
                     ?? throw new SDataException("model JSON is not an object"));

    /// <summary>
    /// Normalized rendering used for MODEL EQUALITY: every <c>_</c>-prefixed key
    /// (the advisory <c>_class_name</c> / <c>_refs</c> annotations both
    /// implementations recompute) is dropped, everything else — including list
    /// ORDER, which is semantic — is preserved.
    /// </summary>
    public string CanonicalJson() => Canonicalize(ToJsonNode());

    public static string Canonicalize(JsonNode node)
    {
        JsonNode copy = JsonNode.Parse(node.ToJsonString())!;
        Strip(copy);
        return copy.ToJsonString(JsonOptions);

        static void Strip(JsonNode n)
        {
            if (n is JsonObject o)
            {
                foreach (string key in o.Where(kv => kv.Key.StartsWith('_')).Select(kv => kv.Key).ToList())
                    o.Remove(key);
                foreach (KeyValuePair<string, JsonNode?> kv in o.ToList()) if (kv.Value is not null) Strip(kv.Value);
            }
            else if (n is JsonArray a)
            {
                foreach (JsonNode? item in a) if (item is not null) Strip(item);
            }
        }
    }
}
