using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

// Wire format of a mission module's WIN-RULE IR, carried at
// <data>/missions/<name>.json -> module.winRules.model.
//
// The schema is the one the mission-system work's extractor emits (--extract-all, ported to C# as
// CYAC.Formats.EaLib.WinRuleModel): snake_case keys, texts as latin-1 strings with embedded
// control bytes, and ORDER-BEARING text maps.  These declarations are the RUNTIME's read side
// (the data-tree rule) — the port models a mission's rules from this document and never opens a
// `.S`.
//
// The IR is trustworthy because the project's synthesizer re-emits the ORIGINAL module bytes from
// it exactly; the document records that as `irReproducesTheModule`.

/// <summary>One comparison of a win-rule conjunction.</summary>
/// <remarks>
/// <c>cc</c> is the LITERAL x86 condition code of the jump-away branch as it appears in the shipping
/// code — the source's signed/unsigned spelling is unrecoverable from semantics, so the IR records
/// it rather than normalising it.
/// </remarks>
public sealed class WinRuleTestDto
{
    /// <summary>What is compared: <c>arg_w</c>, <c>arg_b</c>, <c>ctr</c> or <c>clock</c>.</summary>
    [JsonPropertyName("var")]
    public string? Var { get; init; }

    /// <summary>The x86 condition code of the jump-away branch, e.g. <c>jl</c>.</summary>
    [JsonPropertyName("cc")]
    public string? Cc { get; init; }

    /// <summary>The immediate it is compared against.</summary>
    [JsonPropertyName("imm")]
    public int Imm { get; init; }

    /// <summary>The byte-variable ordinal, when <see cref="Var"/> is <c>ctr</c>.</summary>
    [JsonPropertyName("counter")]
    public int? Counter { get; init; }
}

/// <summary>A win-predicate helper: <c>CF=1</c> means the predicate holds.</summary>
public sealed class WinRuleHelperDto
{
    /// <summary><c>stc_first</c> (tests jump away on FALSE) or <c>clc_first</c>.</summary>
    [JsonPropertyName("polarity")]
    public string? Polarity { get; init; }

    /// <summary>The conjunction, in order.</summary>
    [JsonPropertyName("tests")]
    public List<WinRuleTestDto>? Tests { get; init; }
}

/// <summary>One statement of the rule body IR, shared by every export.</summary>
public sealed class WinRuleStmtDto
{
    /// <summary><c>inc</c>, <c>copy</c>, <c>ax</c>, <c>ret</c>, <c>if</c>, <c>msg</c>, <c>dx0</c> or <c>activate</c>.</summary>
    [JsonPropertyName("op")]
    public string? Op { get; init; }

    /// <summary>The byte-variable ordinal, for <c>inc</c>.</summary>
    [JsonPropertyName("counter")]
    public int? Counter { get; init; }

    /// <summary>The text key, for <c>copy</c> and <c>msg</c>.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>The literal value, for <c>ax</c>.</summary>
    [JsonPropertyName("val")]
    public int? Val { get; init; }

    /// <summary>The helper index, for a helper-guarded <c>if</c>.</summary>
    [JsonPropertyName("helper")]
    public int? Helper { get; init; }

    /// <summary>The inline conjunction, for a test-guarded <c>if</c>.</summary>
    [JsonPropertyName("tests")]
    public List<WinRuleTestDto>? Tests { get; init; }

    /// <summary>The guarded body, for <c>if</c>.</summary>
    [JsonPropertyName("body")]
    public List<WinRuleStmtDto>? Body { get; init; }

    /// <summary>The actor slots, for <c>activate</c>.</summary>
    [JsonPropertyName("slots")]
    public List<int>? Slots { get; init; }
}

/// <summary>The shape of <c>check_win</c> (export 3).</summary>
public sealed class WinRuleFn3Dto
{
    /// <summary><c>template</c>, <c>never</c>, or one of the bespoke idioms.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The helper the templated win check calls, when it has one.</summary>
    [JsonPropertyName("helper")]
    public int? Helper { get; init; }

    /// <summary>Statements that run BEFORE the win-flag head (ACE's timed activation).</summary>
    [JsonPropertyName("pre")]
    public List<WinRuleStmtDto>? Pre { get; init; }

    /// <summary>The free-form tail after the head.</summary>
    [JsonPropertyName("extra")]
    public List<WinRuleStmtDto>? Extra { get; init; }
}

/// <summary>One item of the module's data-region layout, in source-declaration order.</summary>
public sealed class WinRuleDataItemDto
{
    /// <summary><c>byte</c> or <c>text</c>.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The text key, for a <c>text</c> item.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }
}

/// <summary>A mission module's whole win-rule IR.</summary>
public sealed class WinRuleModelDto
{
    /// <summary>The asset the model belongs to, e.g. <c>ABB.S</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Where the module's save/data region starts.</summary>
    [JsonPropertyName("save_off")]
    public int SaveOff { get; init; }

    /// <summary>How many byte variables the data region holds — kill counters and one-shot flags.</summary>
    [JsonPropertyName("n_counters")]
    public int NCounters { get; init; }

    /// <summary>Export 0's codegen style.</summary>
    [JsonPropertyName("fn0_style")]
    public string? Fn0Style { get; init; }

    /// <summary>Export 4's codegen style.</summary>
    [JsonPropertyName("fn4_style")]
    public string? Fn4Style { get; init; }

    /// <summary>The briefing string export 0 copies (latin-1, control bytes included).</summary>
    [JsonPropertyName("briefing")]
    public string? Briefing { get; init; }

    /// <summary>The debrief texts, in export 4's FIRST-USE order — the order is load-bearing.</summary>
    [JsonPropertyName("debrief_texts")]
    public Dictionary<string, string>? DebriefTexts { get; init; }

    /// <summary>The radio texts the rules can broadcast, in declaration order.</summary>
    [JsonPropertyName("radio_texts")]
    public Dictionary<string, string>? RadioTexts { get; init; }

    /// <summary>The win-predicate helpers.</summary>
    [JsonPropertyName("helpers")]
    public List<WinRuleHelperDto>? Helpers { get; init; }

    /// <summary>Export 1 — what happens when a tracked actor is destroyed.</summary>
    [JsonPropertyName("fn1")]
    public List<WinRuleStmtDto>? Fn1 { get; init; }

    /// <summary>Export 2 — the body the AI bytecode's <c>0xE0</c> opcode calls.</summary>
    [JsonPropertyName("fn2")]
    public List<WinRuleStmtDto>? Fn2 { get; init; }

    /// <summary>Export 3 — the per-frame objective predicate.</summary>
    [JsonPropertyName("fn3")]
    public WinRuleFn3Dto? Fn3 { get; init; }

    /// <summary>Export 4 — the debrief-text selector.</summary>
    [JsonPropertyName("fn4")]
    public List<WinRuleStmtDto>? Fn4 { get; init; }

    /// <summary>The data region's layout, including a declared-but-unreferenced variable.</summary>
    [JsonPropertyName("data_layout")]
    public List<WinRuleDataItemDto>? DataLayout { get; init; }
}
