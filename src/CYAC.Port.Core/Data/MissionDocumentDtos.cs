using System.Text.Json;
using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

// Wire format of <data>/missions/<name>.json and <data>/world/<name>.json — one `.S` mission or
// `.W` theater catalog.  This is the RUNTIME's read side (the data-tree rule): the port models a
// mission from this document and never opens 2b.lib.
//
// The `data` section is the authoring model the mission-system work proved round-trip byte-exact for all
// 54 shipped containers (`cyac-s-data-model-v1`), plus two reversible supersets the transform adds:
// an `ai_script` attribute carries structured `script` steps instead of raw `hex`, and objects carry
// `_`-prefixed derived annotations.  Fields the transform needs but the runtime does not (the
// trailer's `module_hex`, an attribute's `hex`) are declared anyway so a reader can see the whole
// document; nothing here re-emits bytes.
//
// A few operands are POLYMORPHIC in the shipped grammar — a directive's `value` is a number for most
// tags and a four-element array for `world_extents`, and an attribute's `value` may be a number, a
// string or a three-word array.  They are declared as JsonElement and read through the helpers on
// each type rather than being flattened into a lie.

/// <summary>One site of a theater catalog's section-1 table.</summary>
public sealed class MissionSiteDto
{
    /// <summary>The site type: 1 generic, 2 landing zone, 6 engagement zone.</summary>
    [JsonPropertyName("type")]
    public int Type { get; init; }

    /// <summary>World X, in world units.</summary>
    [JsonPropertyName("x")]
    public int X { get; init; }

    /// <summary>World Z, in world units.</summary>
    [JsonPropertyName("z")]
    public int Z { get; init; }

    /// <summary>The subtype byte, present for types 1..7.</summary>
    [JsonPropertyName("subtype")]
    public int? Subtype { get; init; }

    /// <summary>What the type means, derived.</summary>
    [JsonPropertyName("_role")]
    public string? Role { get; init; }
}

/// <summary>One step of an AI script, as the transform's disassembler reads it.</summary>
public sealed class MissionAiStepDto
{
    /// <summary>The step's delay in engine ticks; <c>0xFFFE</c> is the "run once" sentinel.</summary>
    [JsonPropertyName("delay")]
    public int Delay { get; init; }

    /// <summary>The opcode's authoring name.</summary>
    [JsonPropertyName("op")]
    public string? Op { get; init; }

    /// <summary>The named place the step targets, when it takes one.</summary>
    [JsonPropertyName("place")]
    public int? Place { get; init; }

    /// <summary>The actor slot the step targets, when it takes one.</summary>
    [JsonPropertyName("actor")]
    public int? Actor { get; init; }

    /// <summary>The step's operand words, when it takes any.</summary>
    [JsonPropertyName("offset")]
    public List<int>? Offset { get; init; }

    /// <summary>A scalar operand, when the step takes one.</summary>
    [JsonPropertyName("value")]
    public int? Value { get; init; }

    /// <summary><c>orient_abs</c>'s three absolute world coordinates.</summary>
    [JsonPropertyName("target")]
    public List<int>? Target { get; init; }

    /// <summary><c>track_actor</c> / <c>phase_0d</c>'s three signed parameter words.</summary>
    [JsonPropertyName("params")]
    public List<int>? Params { get; init; }

    /// <summary><c>radio</c>'s NUL-terminated ASCII message.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary><c>set_script_flags</c>' flag word.</summary>
    [JsonPropertyName("flags")]
    public int? Flags { get; init; }

    /// <summary><c>clear_script_flags</c>' mask word.</summary>
    [JsonPropertyName("mask")]
    public int? Mask { get; init; }

    /// <summary><c>mission_event</c>'s event word.</summary>
    [JsonPropertyName("event")]
    public int? Event { get; init; }

    /// <summary><c>phase_0d</c>'s mode byte.</summary>
    [JsonPropertyName("mode")]
    public int? Mode { get; init; }

    /// <summary><c>spawn_actor</c>'s type word.</summary>
    [JsonPropertyName("type")]
    public int? Type { get; init; }

    /// <summary><c>spawn_actor</c>'s heading word, and <c>set_heading</c>'s.</summary>
    [JsonPropertyName("heading")]
    public int? Heading { get; init; }

    /// <summary><c>spawn_actor</c>'s duration word.</summary>
    [JsonPropertyName("duration")]
    public int? Duration { get; init; }
}

/// <summary>The structured form of an object's AI script.</summary>
public sealed class MissionAiScriptDto
{
    /// <summary>Document format tag: <c>cyac-ai-script-v1</c>.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>The steps, in order.</summary>
    [JsonPropertyName("steps")]
    public List<MissionAiStepDto>? Steps { get; init; }
}

/// <summary>The place and actor slots an AI script references.</summary>
public sealed class MissionScriptRefsDto
{
    /// <summary>The named places the script's patcher would rewrite.</summary>
    [JsonPropertyName("places")]
    public List<int>? Places { get; init; }

    /// <summary>The actor slots the script targets.</summary>
    [JsonPropertyName("actors")]
    public List<int>? Actors { get; init; }

    /// <summary>Why the script could not be walked, when it could not.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>One attribute of a placed object, in authored order.</summary>
public sealed class MissionAttrDto
{
    /// <summary>The attribute's authoring name, e.g. <c>actor_slot</c>.</summary>
    [JsonPropertyName("attr")]
    public string? Attr { get; init; }

    /// <summary>
    /// The operand: absent for a flag attribute, a number for most, a string for <c>pilot_name</c>,
    /// a three-element array for <c>aux_vector</c>.
    /// </summary>
    [JsonPropertyName("value")]
    public JsonElement? Value { get; init; }

    /// <summary>The raw AI-script bytes, when the transform could not structure them.</summary>
    [JsonPropertyName("hex")]
    public string? Hex { get; init; }

    /// <summary>The structured AI script, when it round-trips.</summary>
    [JsonPropertyName("script")]
    public MissionAiScriptDto? Script { get; init; }

    /// <summary>The slots the script references, derived.</summary>
    [JsonPropertyName("_refs")]
    public MissionScriptRefsDto? Refs { get; init; }

    /// <summary>
    /// The transform's own listing of the SHIPPED bytes, one line per instruction, each beginning
    /// <c>+OFFS</c> — a derived field, and an independent oracle for an assembler: the offsets are
    /// the ones the shipping payload has.
    /// </summary>
    [JsonPropertyName("_disasm")]
    public List<string>? Disassembly { get; init; }

    /// <summary>The operand as an integer, or <see langword="null"/> when it is not one.</summary>
    public int? AsInt =>
        Value is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out int n) ? n : null;

    /// <summary>The operand as a string, or <see langword="null"/> when it is not one.</summary>
    public string? AsText => Value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    /// <summary>The operand as a word list, or <see langword="null"/> when it is not an array.</summary>
    public IReadOnlyList<int>? AsWords
    {
        get
        {
            if (Value is not { ValueKind: JsonValueKind.Array } v)
            {
                return null;
            }

            List<int> words = new List<int>(v.GetArrayLength());
            foreach (JsonElement element in v.EnumerateArray())
            {
                words.Add(element.GetInt32());
            }

            return words;
        }
    }
}

/// <summary>Where an object is placed — the tag that closes it plus that tag's operands.</summary>
public sealed class MissionPosDto
{
    /// <summary>The placement's authoring name: <c>abs</c>, <c>ground</c>, <c>at_site</c>, ….</summary>
    [JsonPropertyName("pos")]
    public string? Pos { get; init; }

    /// <summary>Three coordinates, for the absolute and relative placements.</summary>
    [JsonPropertyName("xyz")]
    public List<int>? Xyz { get; init; }

    /// <summary>Two coordinates, for the ground-plane placement.</summary>
    [JsonPropertyName("xz")]
    public List<int>? Xz { get; init; }

    /// <summary>The site type to draw an anchor from, for <c>at_site</c>.</summary>
    [JsonPropertyName("site_type")]
    public int SiteType { get; init; }

    /// <summary>The actor slot the offset is relative to, for <c>rel_place</c>.</summary>
    [JsonPropertyName("place")]
    public int Place { get; init; }

    /// <summary>The actor slots tracked, for <c>track_actors</c>.</summary>
    [JsonPropertyName("slots")]
    public List<int>? Slots { get; init; }

    /// <summary>The era matrix's rows, for the era-matrix placement.</summary>
    [JsonPropertyName("rows")]
    public List<List<int>>? Rows { get; init; }
}

/// <summary>
/// One item of the container's ordered tag stream: a header-level DIRECTIVE or a placed OBJECT.
/// </summary>
/// <remarks>
/// The two are one JSON array because their ORDER is semantic — a relative placement chains
/// backwards and a script sees only what was registered before it — so a reader must not sort them
/// apart.  <see cref="Directive"/> is non-null for one, <see cref="Object"/> for the other.
/// </remarks>
public sealed class MissionStreamItemDto
{
    /// <summary>The directive's authoring name, for a directive item.</summary>
    [JsonPropertyName("directive")]
    public string? Directive { get; init; }

    /// <summary>The opener's authoring name, for an object item.</summary>
    [JsonPropertyName("object")]
    public string? Object { get; init; }

    /// <summary>A directive's operand: a number for most tags, a four-element array for extents.</summary>
    [JsonPropertyName("value")]
    public JsonElement? Value { get; init; }

    /// <summary>The class-table id, for a <c>class</c> object.</summary>
    [JsonPropertyName("class")]
    public int ClassId { get; init; }

    /// <summary>The class's name, derived.</summary>
    [JsonPropertyName("_class_name")]
    public string? ClassName { get; init; }

    /// <summary>The nav-slot index, for a <c>nav_waypoint</c>.</summary>
    [JsonPropertyName("nav_slot")]
    public int NavSlot { get; init; }

    /// <summary>The opener's label, for a named mesh or a nav waypoint.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>The attributes, in authored order.</summary>
    [JsonPropertyName("attrs")]
    public List<MissionAttrDto>? Attrs { get; init; }

    /// <summary>The placement that closes the object.</summary>
    [JsonPropertyName("pos")]
    public MissionPosDto? Pos { get; init; }

    /// <summary>True when this item is a header-level directive.</summary>
    public bool IsDirective => Directive is not null;

    /// <summary>A directive's scalar operand, or <see langword="null"/>.</summary>
    public int? DirectiveValue =>
        Value is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out int n) ? n : null;

    /// <summary>A directive's coordinate operand, or <see langword="null"/>.</summary>
    public IReadOnlyList<int>? DirectiveCoords
    {
        get
        {
            if (Value is not { ValueKind: JsonValueKind.Array } v)
            {
                return null;
            }

            List<int> coords = new List<int>(v.GetArrayLength());
            foreach (JsonElement element in v.EnumerateArray())
            {
                coords.Add(element.GetInt32());
            }

            return coords;
        }
    }
}

/// <summary>One item of the container's trailer.</summary>
public sealed class MissionTrailerItemDto
{
    /// <summary>The trailer module's bytes as hex, when this item carries it.</summary>
    [JsonPropertyName("module_hex")]
    public string? ModuleHex { get; init; }

    /// <summary>How many bytes this item skips, when it carries no module.</summary>
    [JsonPropertyName("skip")]
    public int Skip { get; init; }
}

/// <summary>The authoring model of one <c>.S</c>/<c>.W</c> container.</summary>
public sealed class MissionDataDto
{
    /// <summary>Document format tag: <c>cyac-s-data-model-v1</c>.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>The asset name, e.g. <c>ABB.S</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary><c>S</c> for a mission, <c>W</c> for a theater catalog.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The section-1 site table (theater catalogs only).</summary>
    [JsonPropertyName("sites")]
    public List<MissionSiteDto>? Sites { get; init; }

    /// <summary>The section-2 interned name table.</summary>
    [JsonPropertyName("names")]
    public List<string>? Names { get; init; }

    /// <summary>The ordered tag stream — directives and objects.</summary>
    [JsonPropertyName("stream")]
    public List<MissionStreamItemDto>? Stream { get; init; }

    /// <summary>The trailer, which for a mission carries the win-rule module.</summary>
    [JsonPropertyName("trailer")]
    public List<MissionTrailerItemDto>? Trailer { get; init; }
}

/// <summary>One export of a mission module's five-entry table.</summary>
public sealed class MissionExportDto
{
    /// <summary>The export's slot, 0..4.</summary>
    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    /// <summary>Its name in this project's vocabulary.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Its block-relative offset.</summary>
    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    /// <summary>The distance to the next export — an upper bound on its size, not its code size.</summary>
    [JsonPropertyName("extentUpperBound")]
    public int ExtentUpperBound { get; init; }

    /// <summary>True when the export starts with the MSC far prologue <c>55 8B EC</c>.</summary>
    [JsonPropertyName("hasCompilerPrologue")]
    public bool HasCompilerPrologue { get; init; }
}

/// <summary>The briefing text export 0 copies out of the module's own data.</summary>
public sealed class MissionBriefingDto
{
    /// <summary>Why it is read-only.</summary>
    [JsonPropertyName("_note")]
    public string? Note { get; init; }

    /// <summary>Where the copy reads from, block-relative.</summary>
    [JsonPropertyName("sourceOffset")]
    public int SourceOffset { get; init; }

    /// <summary>How many bytes it copies.</summary>
    [JsonPropertyName("bytes")]
    public int Bytes { get; init; }

    /// <summary>The text, rendered for display.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>One named, editable immediate inside a mission module's code.</summary>
public sealed class MissionWinRuleParamDto
{
    /// <summary>A stable id, e.g. <c>win_thr0</c>.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>What the parameter is, e.g. <c>win_threshold</c>.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>A human-readable label.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The value LIVE from the module's bytes.</summary>
    [JsonPropertyName("value")]
    public int Value { get; init; }

    /// <summary>The lowest value the encoding accepts.</summary>
    [JsonPropertyName("min")]
    public int Min { get; init; }

    /// <summary>The highest value the encoding accepts.</summary>
    [JsonPropertyName("max")]
    public int Max { get; init; }

    /// <summary>Whether an edit here is written back into the module on export.</summary>
    [JsonPropertyName("editable")]
    public bool Editable { get; init; }

    /// <summary>How the immediate is encoded, e.g. <c>imm8</c>.</summary>
    [JsonPropertyName("encoding")]
    public string? Encoding { get; init; }

    /// <summary>The immediate's offset inside the module block.</summary>
    [JsonPropertyName("moduleOffset")]
    public int ModuleOffset { get; init; }

    /// <summary>Whether the anchor bytes were found where the catalog says.</summary>
    [JsonPropertyName("_anchorMatched")]
    public bool AnchorMatched { get; init; }
}

/// <summary>A mission's rules, as far as the project has decoded them.</summary>
public sealed class MissionWinRulesDto
{
    /// <summary><c>template</c> or the name of a bespoke shape.</summary>
    [JsonPropertyName("class")]
    public string? Class { get; init; }

    /// <summary>Why there is nothing here, when the mission is not catalogued.</summary>
    [JsonPropertyName("_note")]
    public string? Note { get; init; }

    /// <summary>The rules in one line each, for a reader.</summary>
    [JsonPropertyName("_ruleLines")]
    public List<string>? RuleLines { get; init; }

    /// <summary>The named editable immediates.</summary>
    [JsonPropertyName("params")]
    public List<MissionWinRuleParamDto>? Params { get; init; }

    /// <summary>
    /// The canonical statement IR, present for the 46 modelled missions.  It is trustworthy because
    /// the project's synthesizer re-emits the ORIGINAL module bytes from it —
    /// <see cref="IrReproducesTheModule"/> records whether that held for this module.
    /// </summary>
    [JsonPropertyName("model")]
    public WinRuleModelDto? Model { get; init; }

    /// <summary>Whether re-synthesising <see cref="Model"/> reproduces the shipped module exactly.</summary>
    [JsonPropertyName("irReproducesTheModule")]
    public bool IrReproducesTheModule { get; init; }
}

/// <summary>The <c>.S</c> trailer's x86 module, as data.</summary>
public sealed class MissionModuleDto
{
    /// <summary>False for the three theater catalogs, which have no module.</summary>
    [JsonPropertyName("present")]
    public bool Present { get; init; }

    /// <summary>How the module is carried and what is editable about it.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>The module block's size in bytes.</summary>
    [JsonPropertyName("codeBytes")]
    public int CodeBytes { get; init; }

    /// <summary>Where the block starts in the container file.</summary>
    [JsonPropertyName("blockFileOffset")]
    public int BlockFileOffset { get; init; }

    /// <summary>The five exports.</summary>
    [JsonPropertyName("exports")]
    public List<MissionExportDto>? Exports { get; init; }

    /// <summary>The briefing, when the module has the copy pattern.</summary>
    [JsonPropertyName("briefing")]
    public MissionBriefingDto? Briefing { get; init; }

    /// <summary>The rules.</summary>
    [JsonPropertyName("winRules")]
    public MissionWinRulesDto? WinRules { get; init; }
}

/// <summary>Wire format of <c>missions/&lt;name&gt;.json</c> and <c>world/&lt;name&gt;.json</c>.</summary>
public sealed class MissionDocumentDto
{
    /// <summary>Document format tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is and how it is edited.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>Where the bytes came from, e.g. <c>2b.lib/ABB.S</c>.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>The asset name, e.g. <c>ABB.S</c>.</summary>
    [JsonPropertyName("assetName")]
    public string? AssetName { get; init; }

    /// <summary><c>mission</c> or <c>theater</c>.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The container's authored content.</summary>
    [JsonPropertyName("data")]
    public MissionDataDto? Data { get; init; }

    /// <summary>The trailer module, for a mission.</summary>
    [JsonPropertyName("module")]
    public MissionModuleDto? Module { get; init; }
}
