using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// A mission's rules as data: the semantic model of its <c>.S</c> trailer module — what counts as a
/// kill, what the win predicate tests, what the briefing and debrief say.
/// </summary>
/// <remarks>
/// <para>
/// The original ships these rules as native x86.  The port lifts them into an IR that the
/// project's synthesizer re-emits BYTE-EXACT for 42 template modules plus the 4 covered bespoke ones
/// (ACE / PIRATE / RAMROD / STRAFE).  Byte-exact re-emission is what makes the IR trustworthy: it is
/// not a paraphrase of the code, it reproduces it — and each mission document records whether that
/// held for its own module (<see cref="IrReproducesTheModule"/>).
/// </para>
/// <para>
/// The IR is carried by
/// <c>&lt;data&gt;/missions/&lt;name&gt;.json</c> under <c>module.winRules.model</c>, so the runtime
/// reads it from the tree and a modder can see and edit it.  A host installs the set once with
/// <see cref="Load"/>, the way it installs the trig and class-record tables.  Since the transform
/// reads each model out of the mission's own module bytes rather than from a decoded copy.
/// </para>
/// <para>
/// <c>Sim/Mission/WinRuleEvaluator</c> INTERPRETS this view and the verified
/// <c>MissionModuleDispatch</c> calls it on a live sortie.  The caution held: the evaluator computes
/// real 8086 <c>cmp</c> flags and asks each test's own recorded condition code, and an op it cannot
/// cite a meaning for throws at LOAD.  This type still only READS — it is the document, and the
/// evaluator is the machine.
/// </para>
/// <para>
/// 5 of the 51 shipped missions have no model: ALONE, BOLO, GAUNTLET, INSTR and MOOLAH, whose modules
/// fall outside the covered shapes.  <see cref="ForMission(string)"/> returns <see langword="null"/>
/// for them rather than an empty model.
/// </para>
/// </remarks>
public sealed class MissionWinRules
{
    private static readonly Dictionary<string, MissionWinRules> Registry =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly WinRuleModelDto _model;

    private MissionWinRules(string assetName, MissionWinRulesDto section, WinRuleModelDto model)
    {
        AssetName = assetName;
        Section = section;
        _model = model;
    }

    /// <summary>The number of shipped missions with a canonical model: 46.</summary>
    public const int ModelledMissionCount = 46;

    /// <summary>
    /// Installs the rules of every mission whose document carries a model.
    /// </summary>
    /// <param name="documents">Asset name → the mission document's <c>module</c> section.</param>
    public static void Load(IReadOnlyDictionary<string, MissionModuleDto> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        Registry.Clear();
        foreach ((string assetName, MissionModuleDto module) in documents)
        {
            if (FromModule(assetName, module) is { } rules)
            {
                Registry[assetName] = rules;
            }
        }
    }

    /// <summary>Forgets the installed rule set (tests).</summary>
    public static void Unload() => Registry.Clear();

    /// <summary>The asset names that have a canonical model, e.g. <c>"ABB.S"</c>.</summary>
    public static IReadOnlyCollection<string> ModelledAssetNames => Registry.Keys;

    /// <summary>True when a mission's module has a canonical model.</summary>
    /// <param name="assetName">An asset name such as <c>"ABB.S"</c> (case-insensitive).</param>
    public static bool IsModelled(string assetName)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        return Registry.ContainsKey(assetName);
    }

    /// <summary>
    /// The rules of one mission, or <see langword="null"/> when its module is not modelled.
    /// </summary>
    /// <param name="assetName">An asset name such as <c>"ABB.S"</c> (case-insensitive).</param>
    public static MissionWinRules? ForMission(string assetName)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        return Registry.TryGetValue(assetName, out MissionWinRules? rules) ? rules : null;
    }

    /// <summary>Builds the rules of one mission from its module section, or null when unmodelled.</summary>
    /// <param name="assetName">The mission's asset name.</param>
    /// <param name="module">The document's <c>module</c> section.</param>
    /// <remarks>
    /// Public so a test can build a rules view over a HAND-WRITTEN IR model, which is how the evaluator's
    /// per-op semantics are asserted one op at a time without hunting for a shipped mission that happens to
    /// use that op alone.
    /// </remarks>
    public static MissionWinRules? FromModule(string assetName, MissionModuleDto? module) =>
        module?.WinRules is { Model: { } model } section
            ? new MissionWinRules(assetName, section, model)
            : null;

    /// <summary>The mission asset these rules belong to.</summary>
    public string AssetName { get; }

    /// <summary>The document section behind this view.</summary>
    public MissionWinRulesDto Section { get; }

    /// <summary>The IR itself, for callers that need a field this view omits.</summary>
    public WinRuleModelDto Source => _model;

    /// <summary>
    /// Whether re-synthesising <see cref="Source"/> reproduces the shipped module byte for byte —
    /// the transform measured it per mission when it wrote the document.
    /// </summary>
    public bool IrReproducesTheModule => Section.IrReproducesTheModule;

    /// <summary>
    /// The shape of the win check: <c>"template"</c> (call a helper, set the win flag),
    /// <c>"never"</c>, or one of the bespoke idioms.
    /// </summary>
    public string WinConditionKind => _model.Fn3?.Kind ?? "never";

    /// <summary>The helper index the templated win check calls, when it has one.</summary>
    public int? WinConditionHelper => _model.Fn3?.Helper;

    /// <summary>
    /// The briefing string export 0 copies (latin-1, control bytes included: 0x01 is a paragraph break
    /// and 0x0C an opening quote).  <see langword="null"/> when the module has no briefing.
    /// </summary>
    public string? Briefing => _model.Briefing;

    /// <summary>The debrief texts, in the order export 4 first uses them — the order is load-bearing.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> DebriefTexts =>
        [.. _model.DebriefTexts ?? []];

    /// <summary>The radio texts the rules can broadcast, in declaration order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> RadioTexts =>
        [.. _model.RadioTexts ?? []];

    /// <summary>How many byte variables the module's data region holds — kill counters and one-shot flags.</summary>
    public int CounterCount => _model.NCounters;

    /// <summary>The win-predicate helpers (CF=1 means the predicate holds).</summary>
    public IReadOnlyList<WinRuleHelperDto> Helpers => _model.Helpers ?? [];

    /// <summary>Export 1 — what happens when a tracked actor is destroyed.</summary>
    public IReadOnlyList<WinRuleStmtDto> OnSlotDestroyed => _model.Fn1 ?? [];

    /// <summary>Export 2 — the body the AI bytecode's <c>0xE0</c> opcode calls.</summary>
    public IReadOnlyList<WinRuleStmtDto> ScriptHook => _model.Fn2 ?? [];

    /// <summary>Export 3 — the per-frame objective predicate.</summary>
    public WinRuleFn3Dto WinCondition => _model.Fn3 ?? new WinRuleFn3Dto();

    /// <summary>Export 4 — the debrief-text selector.</summary>
    public IReadOnlyList<WinRuleStmtDto> DebriefSelector => _model.Fn4 ?? [];

    /// <summary>The named editable immediates the transform found in the module's code.</summary>
    public IReadOnlyList<MissionWinRuleParamDto> Parameters => Section.Params ?? [];

    /// <summary>
    /// The IR's own consistency check: counters in range, text keys defined, helper indices valid.
    /// Empty means clean.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = new List<string>();

        foreach (int counter in ReferencedCounters())
        {
            if (counter < 0 || counter >= CounterCount)
            {
                problems.Add($"counter {counter} is outside the declared {CounterCount}");
            }
        }

        foreach (int helper in ReferencedHelpers())
        {
            if (helper < 0 || helper >= Helpers.Count)
            {
                problems.Add($"helper {helper} is outside the declared {Helpers.Count}");
            }
        }

        foreach (WinRuleStmtDto statement in AllStatements())
        {
            if (statement.Op is "copy" or "msg" && statement.Text is { } key)
            {
                bool defined = (_model.DebriefTexts?.ContainsKey(key) ?? false)
                    || (_model.RadioTexts?.ContainsKey(key) ?? false);
                if (!defined)
                {
                    problems.Add($"statement '{statement.Op}' names undefined text '{key}'");
                }
            }
        }

        return problems;
    }

    /// <summary>Every byte-variable ordinal the rules touch, ascending — incremented or tested.</summary>
    public IReadOnlyList<int> ReferencedCounters()
    {
        SortedSet<int> seen = new SortedSet<int>();
        foreach (WinRuleStmtDto statement in AllStatements())
        {
            if (statement.Op == "inc" && statement.Counter is int counter)
            {
                seen.Add(counter);
            }

            AddTested(seen, statement.Tests);
        }

        foreach (WinRuleHelperDto helper in Helpers)
        {
            AddTested(seen, helper.Tests);
        }

        return [.. seen];
    }

    /// <summary>Every helper index the rules call, ascending (the win check's own included).</summary>
    public IReadOnlyList<int> ReferencedHelpers()
    {
        SortedSet<int> seen = new SortedSet<int>();
        if (WinConditionHelper is int winHelper)
        {
            seen.Add(winHelper);
        }

        foreach (WinRuleStmtDto statement in AllStatements())
        {
            if (statement.Op == "if" && statement.Helper is int helper)
            {
                seen.Add(helper);
            }
        }

        return [.. seen];
    }

    /// <summary>The actor slots the rules activate on a timer (the ACE idiom's <c>activate</c>).</summary>
    public IReadOnlyList<int> ActivatedActorSlots()
    {
        SortedSet<int> seen = new SortedSet<int>();
        foreach (WinRuleStmtDto statement in AllStatements())
        {
            if (statement.Op == "activate" && statement.Slots is { } slots)
            {
                foreach (int slot in slots)
                {
                    seen.Add(slot);
                }
            }
        }

        return [.. seen];
    }

    /// <summary>Every statement of every export, guarded bodies included, in document order.</summary>
    public IEnumerable<WinRuleStmtDto> AllStatements()
    {
        foreach (WinRuleStmtDto statement in Walk(OnSlotDestroyed))
        {
            yield return statement;
        }

        foreach (WinRuleStmtDto statement in Walk(ScriptHook))
        {
            yield return statement;
        }

        foreach (WinRuleStmtDto statement in Walk(WinCondition.Pre ?? []))
        {
            yield return statement;
        }

        foreach (WinRuleStmtDto statement in Walk(WinCondition.Extra ?? []))
        {
            yield return statement;
        }

        foreach (WinRuleStmtDto statement in Walk(DebriefSelector))
        {
            yield return statement;
        }
    }

    private static IEnumerable<WinRuleStmtDto> Walk(IReadOnlyList<WinRuleStmtDto> statements)
    {
        foreach (WinRuleStmtDto statement in statements)
        {
            yield return statement;
            foreach (WinRuleStmtDto nested in Walk(statement.Body ?? []))
            {
                yield return nested;
            }
        }
    }

    private static void AddTested(SortedSet<int> into, IReadOnlyList<WinRuleTestDto>? tests)
    {
        foreach (WinRuleTestDto test in tests ?? [])
        {
            if (test.Var == "ctr" && test.Counter is int counter)
            {
                into.Add(counter);
            }
        }
    }
}
