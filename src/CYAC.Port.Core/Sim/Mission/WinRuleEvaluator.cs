using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Lifecycle;

namespace CYAC.Port.Core.Sim.Mission;

/// <summary>
/// THE MISSION'S RULES, RUNNING — an interpreter for the win-rule IR that IS the shipped <c>.S</c>
/// trailer module: its counters, its predicate helpers, its per-frame objective check, its radio calls
/// and its debrief selection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the semantics come from.</b>  Every operation here is the meaning of one fixed x86
/// encoding, and the authority for that meaning is the SYNTHESISER's rulebook
/// §2), which is proven by reconstruction: re-emitting these models reproduces the shipped modules
/// byte for byte for 42 template missions and the 4 covered bespoke ones
/// So "what does op <c>inc</c> mean" is not a reading of
/// prose — it is <c>2E FE 06 cw</c>, <c>inc byte cs:[counter]</c>, and nothing else.
/// </para>
/// <para>
/// <b>The ABI.</b> The five exports and their arguments are pinned by the four <c>les bx,[0xFB8]</c>
/// dispatch sites plus the briefing path (header, "THE 5-SLOT ABI"; The three the COMBAT frame calls are
/// <see cref="IMissionModule"/> and reach this type through the verified
/// <see cref="MissionModuleDispatch"/>; <see cref="GetBriefingText"/> (slot 0, from
/// <c>s_asset_briefing_text_extract @image@0x0966B</c>) and <see cref="GetDebriefText"/> (slot 4, from
/// <c>ui_post_death_message @image@0x25C67</c>) are UI-path calls and are therefore NOT on that
/// interface.
/// </para>
/// <para>
/// <b>Where the counters live.</b>  In the original they are bytes inside the module's OWN far-heap
/// segment — the <c>.S</c> block is copied verbatim to a fresh segment and addressed
/// <c>cs:</c>-relative with <c>DS = CS</c> (<c>image@0x0A322</c>: <c>mov [0xFB8],0</c> /
/// <c>mov [0xFBA],ax</c>).  That segment is not DGROUP, so the ported register file has no home
/// for it and <see cref="Counters"/> is an EVALUATOR-OWNED block, deliberately labelled.  Its
/// layout is the module's own (<see cref="ByteVariableOffsets"/>), so a counter's ordinal, its
/// module offset and its initial 0 all match the bytes.
/// </para>
/// <para>
/// <b>What it writes outside itself.</b>  Exactly what the module writes: the WIN flag byte through
/// the pointer the hook hands it (<c>[bp+0x12]</c>, i.e. <c>[0xB562]</c>), and — for the ACE idiom
/// only — bit 0 of <c>+0x3C</c> on the pool objects the actor-slot table names.  Nothing else.
/// </para>
/// </remarks>
public sealed class WinRuleEvaluator : IMissionModule
{
    private readonly WinRuleModelDto _model;
    private readonly byte[] _counters;
    private readonly int[] _counterOffsets;
    private readonly Dictionary<string, int> _radioTextOffsets = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _radioTextsByOffset = [];

    /// <summary>
    /// The far SEGMENT this evaluator hands back in <c>DX</c> for a radio call.
    /// </summary>
    /// <remarks>
    /// The original returns <c>DX = CS</c>, the module's own far-heap segment
    /// (<c>mov dx,cs</c> = <c>8C CA</c>), whose value is whatever <c>far_heap_alloc</c> gave the
    /// mission at load and is therefore unknowable to the port.  The port substitutes ONE synthetic
    /// non-zero constant so that (a) the dispatch's <c>text != 0</c> test at <c>image@0x08C4D</c>
    /// behaves, and (b) <see cref="ResolveText"/> can recognise its own pointers.  The OFFSET half
    /// is not synthetic: it is the module's own data-region offset, computed by
    /// <see cref="ByteVariableOffsets"/>'s layout walk, and is the exact <c>iw</c> of the module's
    /// <c>mov ax,text_off</c>.
    /// </remarks>
    public const ushort SyntheticModuleSegment = 0xC1AC;

    /// <summary>The module block offset the export table ends at — data starts here.</summary>
    /// <remarks><c>+0x00..0x09</c> is the five-entry u16 export table (rulebook, LAYOUT item 1).</remarks>
    public const int DataRegionOffset = 0x0A;

    /// <summary>Builds an evaluator for one mission's rules.</summary>
    /// <param name="rules">The mission's win-rule view.</param>
    /// <param name="registers">
    /// The combat register file the win flag and the actor-slot table live in; null for a harness
    /// that only wants the rule arithmetic (the flag write is then recorded, not performed).
    /// </param>
    /// <param name="arena">
    /// The pool arena the ACE idiom's <c>activate</c> writes into; null leaves it recorded only.
    /// </param>
    /// <exception cref="NotSupportedException">
    /// The IR uses a statement, test variable or condition code this evaluator does not model.  The
    /// check runs at CONSTRUCTION, over every statement of every export and every helper, so a
    /// mission whose rules the port cannot run refuses to load instead of running them wrongly.
    /// </exception>
    public WinRuleEvaluator(
        MissionWinRules rules, CombatRegisters? registers = null, PoolArena? arena = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        Rules = rules;
        _model = rules.Source;
        Registers = registers;
        Arena = arena;
        _counters = new byte[Math.Max(0, _model.NCounters)];
        _counterOffsets = new int[_counters.Length];
        BuildDataLayout();
        Validate();
    }

    /// <summary>The mission's rules, as data.</summary>
    public MissionWinRules Rules { get; }

    /// <summary>
    /// The register file the WIN flag is written into and the actor-slot table is read from.
    /// </summary>
    /// <remarks>
    /// Settable because a VERIFICATION decodes a fresh register file for every recorded frame and
    /// must re-point the module at it; a running session sets it once, in the constructor.
    /// </remarks>
    public CombatRegisters? Registers { get; set; }

    /// <summary>The pool arena the ACE idiom's <c>activate</c> writes into.</summary>
    public PoolArena? Arena { get; set; }

    /// <summary>The mission's asset name, e.g. <c>"ABB.S"</c>.</summary>
    public string AssetName => Rules.AssetName;

    /// <summary>
    /// A module IS loaded — <c>[0x0FBA] != 0</c> for every mission, because
    /// <c>wld_or_s_asset_parser</c> installs the trailer block's far pointer at load
    /// (<c>image@0x0A322</c>).
    /// </summary>
    public bool IsInstalled => true;

    // ---------------------------------------------------------------- the module's own data region

    /// <summary>
    /// The module's byte variables — kill counters and one-shot radio flags — in ordinal order.
    /// </summary>
    /// <remarks>
    /// EVALUATOR-OWNED state (see the type remarks): the original keeps them in the module's own
    /// far-heap segment, which the verified DGROUP register file does not cover.
    /// </remarks>
    public IReadOnlyList<byte> Counters => _counters;

    /// <summary>
    /// Where each byte variable sits in the module block, in ordinal order — the <c>cw</c> of its
    /// own <c>inc byte cs:[cw]</c>.
    /// </summary>
    public IReadOnlyList<int> ByteVariableOffsets => _counterOffsets;

    /// <summary>
    /// The module block offset of each radio text, keyed as the IR keys it — the <c>iw</c> of the
    /// module's own <c>mov ax,text_off</c>.
    /// </summary>
    public IReadOnlyDictionary<string, int> RadioTextOffsets => _radioTextOffsets;

    /// <summary>Sets one byte variable (a harness seeding a mid-mission state).</summary>
    /// <param name="ordinal">The variable's ordinal.</param>
    /// <param name="value">Its value.</param>
    public void SetCounter(int ordinal, byte value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, _counters.Length);
        _counters[ordinal] = value;
    }

    // ------------------------------------------------------------------------------- the census

    /// <summary>How many times the per-frame hook reached the module.</summary>
    public int CheckWinCalls { get; private set; }

    /// <summary>How many of those wrote the WIN flag.</summary>
    public int WinFlagWrites { get; private set; }

    /// <summary>Whether the mission has been won at least once (the port's own latch).</summary>
    /// <remarks>
    /// The original does NOT latch it: <c>combat_vtable_slot_dispatch</c> clears <c>[0xB562]</c>
    /// before every dispatch (<c>image@0x08C0F</c>) and reads it back after
    /// (<c>image@0x08C60</c>), so the byte is a per-call pulse and a template mission re-raises it
    /// on every fired frame for as long as its predicate holds.  A host that wants to say "the
    /// objective is complete" needs a latch of its own; this is it.
    /// </remarks>
    public bool HasWon { get; private set; }

    /// <summary>How many <c>on_slot_destroyed</c> dispatches reached the module.</summary>
    public int SlotDestroyedCalls { get; private set; }

    /// <summary>How many <c>on_secondary_event</c> (AI opcode <c>0xE0</c>) dispatches did.</summary>
    public int SecondaryEventCalls { get; private set; }

    /// <summary>How many radio calls the win check returned (a non-zero <c>DX:AX</c>).</summary>
    public int RadioCalls { get; private set; }

    /// <summary>How many actor slots the ACE idiom's <c>activate</c> has woken.</summary>
    public int ActorSlotsActivated { get; private set; }

    /// <summary>How many <c>activate</c> statements could not reach the pool (no arena wired).</summary>
    public int ActorActivationsUnserviced { get; private set; }

    // ------------------------------------------------------------------------------- the exports

    /// <summary>
    /// <c>vtable[+0x00] get_briefing_text</c> — the briefing the mission-select screen shows.
    /// </summary>
    /// <returns>The latin-1 text, or null when the module carries none (FREE.S).</returns>
    /// <remarks>
    /// A single <c>rep movsb</c> of one fixed <c>(src,len)</c> into the caller's <c>0x4B0</c>-byte
    /// buffer (<c>image@0x096DE</c>).  The two codegen styles (<c>les</c> vs <c>moves</c>) differ
    /// only in where <c>ES:DI</c> is loaded and have no semantic effect.
    /// </remarks>
    public string? GetBriefingText() => _model.Briefing;

    /// <summary>
    /// <c>vtable[+0x08] get_debrief_text</c> — the post-mission verdict.
    /// </summary>
    /// <returns>The selected text and the module's <c>AX</c>.</returns>
    /// <remarks>
    /// <para>
    /// Called once at mission end from <c>ui_post_death_message</c>
    /// (<c>lcall</c> @<c>image@0x25C71</c>, seven pushed words, <c>add sp,0xE</c>).  The return
    /// matters: <c>image@0x25C79..0x25C82</c> is <c>cmp di,1 / sbb al,al / and al,1 / add al,4 /
    /// mov [0xBC31],al</c>, so <b>AX ≥ 1 ⇒ mode 4 (mission accomplished) and AX = 0 ⇒ mode 5</b>,
    /// which is the index <c>ui_post_mission_stats_screen</c> then plays a Yeager voice line from
    /// (<c>image@0x26247</c>).  Mode 6 is the death path and is set before the module is asked.
    /// </para>
    /// <para>
    /// The first argument is the destination far pointer <c>[0x36C8]:0000</c>, so the port only has
    /// to say WHICH text the walk selected; the three fn4 copy styles (<c>hoisted</c>,
    /// <c>per_arm</c>, <c>shared_tail</c>) decide where the <c>rep movsb</c> sits and select the
    /// same text.
    /// </para>
    /// </remarks>
    public WinRuleDebrief GetDebriefText()
    {
        Frame frame = new Frame(Argument: 0, Clock: 0, WinFlagAddress: 0, PoolSegment: 0,
            ActorSlotTable: 0);
        Execute(Rules.DebriefSelector, ref frame);
        return new WinRuleDebrief(
            frame.SelectedTextKey,
            frame.SelectedTextKey is { } key ? TextOf(key) : null,
            frame.Ax);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <c>vtable[+0x06] check_win_condition</c>, the per-frame objective predicate.  The body is
    /// <c>pre</c> (ACE's timed actor activation) → the HEAD → <c>extra</c>, and the head for a
    /// <c>template</c> mission is exactly
    /// <c>call helper; jae NOWIN; mov bx,[bp+0x12]; mov byte ss:[bx],1</c> — the helper's CARRY
    /// flag is the predicate and the win flag is written through the pointer the hook pushed
    /// (rulebook, "fn3 KINDS").  Kind <c>never</c> (FREE.S) has no head at all.
    /// </para>
    /// <para>
    /// The return is <c>DX:AX</c>: the template tail sets both to 0
    /// (<c>xor ax,ax</c> / <c>xor dx,dx</c>), the radio idiom's <c>msg</c> sets
    /// <c>AX = text offset</c> and <c>DX = CS</c>, and the dispatch shows any non-zero result as
    /// cockpit text (<c>image@0x08C4D</c>).
    /// </para>
    /// </remarks>
    public uint CheckWinCondition(in MissionHookArgs args)
    {
        CheckWinCalls++;
        Frame frame = new Frame(
            Argument: args.PlayerObjectOffset,
            Clock: args.FrameCounter,
            WinFlagAddress: args.AdvisoryFlagAddress,
            PoolSegment: args.PoolSegment,
            ActorSlotTable: args.SlotNearPtrTable);

        WinRuleFn3Dto check = Rules.WinCondition;
        Execute(check.Pre ?? [], ref frame);

        if (!frame.Returned && check.Kind == "template")
        {
            int helper = check.Helper
                ?? throw new NotSupportedException(
                    $"{AssetName}: fn3 kind 'template' without a helper index");
            if (EvaluateHelper(helper, in frame))
            {
                WinFlagWrites++;
                HasWon = true;
                Registers?.SetByte(frame.WinFlagAddress, 1);        // image@0x08C25 / the module's
            }                                                        //   mov byte ss:[bx],1
        }

        Execute(check.Extra ?? [], ref frame);

        uint result = ((uint)frame.Dx << 16) | frame.Ax;
        if (result != 0)
        {
            RadioCalls++;
        }

        return result;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>vtable[+0x02] on_slot_destroyed(index)</c>: the 0-based index
    /// <c>slot_nearptr_table_lookup @image@0x08F22</c> returned, in <c>[bp+6]</c>.  A template body
    /// is one or two guarded <c>counter++</c> arms over that index — "a bandit of THIS group died"
    /// versus "one of ours did" (ESCORT.S counts both).
    /// </remarks>
    public void OnSlotDestroyed(ushort slotIndex)
    {
        SlotDestroyedCalls++;
        Frame frame = new Frame(slotIndex, 0, 0, 0, 0);
        Execute(Rules.OnSlotDestroyed, ref frame);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>vtable[+0x04] on_secondary_event(arg)</c> — the AI bytecode's <c>0xE0
    /// CALL_SCRIPT_FUNCTION</c>, whose word is passed straight through with no slot lookup
    /// (<c>image@0x08CB8</c>).  Its return is discarded by the wrapper's <c>retf 2</c>.
    /// </remarks>
    public void OnSecondaryEvent(ushort argument)
    {
        SecondaryEventCalls++;
        Frame frame = new Frame(argument, 0, 0, 0, 0);
        Execute(Rules.ScriptHook, ref frame);
    }

    // -------------------------------------------------------------------------------- radio texts

    /// <summary>
    /// The text a <c>DX:AX</c> from <see cref="CheckWinCondition"/> names, or null when the pointer
    /// is not one of this module's.
    /// </summary>
    /// <param name="farPointer">The module's returned far pointer.</param>
    /// <remarks>
    /// The original hands the pointer to <c>show_cockpit_text_string @image@0x0CC5B</c>, which
    /// copies at most <c>0x46</c> bytes into the HUD message strip and stamps a SIX-unit expiry
    /// (<see cref="Model.Cockpit.HudMessageTable.MessageTicks"/>).  The port resolves it back to the
    /// string the IR carries instead of copying bytes out of a far segment it does not have.
    /// </remarks>
    public string? ResolveText(uint farPointer)
    {
        if ((ushort)(farPointer >> 16) != SyntheticModuleSegment)
        {
            return null;
        }

        if (!_radioTextsByOffset.TryGetValue((int)(farPointer & 0xFFFF), out string? text))
        {
            return null;
        }

        // The module's texts carry their own terminating NUL, because the IR is the module's BYTES;
        // the original's copy stops there and so does this.
        int end = text.IndexOf('\0', StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    // ------------------------------------------------------------------------------ the machinery

    /// <summary>One export's frame: its arguments and the registers the IR can touch.</summary>
    private record struct Frame(
        ushort Argument,
        ushort Clock,
        ushort WinFlagAddress,
        ushort PoolSegment,
        ushort ActorSlotTable)
    {
        /// <summary>The return value the <c>ax</c> statement sets.</summary>
        public ushort Ax { get; set; }

        /// <summary>The high half of the <c>DX:AX</c> return.</summary>
        public ushort Dx { get; set; }

        /// <summary>Whether an explicit <c>return</c> (<c>jmp END</c>) has been taken.</summary>
        public bool Returned { get; set; }

        /// <summary>The text key the last executed <c>copy</c> selected.</summary>
        public string? SelectedTextKey { get; set; }
    }

    private void Execute(IReadOnlyList<WinRuleStmtDto> statements, ref Frame frame)
    {
        foreach (WinRuleStmtDto statement in statements)
        {
            if (frame.Returned)
            {
                return;
            }

            switch (statement.Op)
            {
                // `2E FE 06 cw` — inc byte cs:[counter], an 8-bit increment that wraps.
                case "inc":
                    int ordinal = Ordinal(statement.Counter);
                    _counters[ordinal] = unchecked((byte)(_counters[ordinal] + 1));
                    break;

                // `mov si,src / mov cx,len / … / rep movsb` — one text copy into ES:DI.
                case "copy":
                    frame.SelectedTextKey = statement.Text
                        ?? throw Malformed("a 'copy' statement without a text key");
                    break;

                // `B8 01 00` (ax=1) or `33 C0` (ax=0).
                case "ax":
                    frame.Ax = (ushort)(statement.Val ?? 0);
                    break;

                // `33 D2` — xor dx,dx.
                case "dx0":
                    frame.Dx = 0;
                    break;

                // `B8 iw / 8C CA` — mov ax,text_off ; mov dx,cs.  DX:AX becomes the radio channel.
                case "msg":
                    string key = statement.Text
                        ?? throw Malformed("a 'msg' statement without a text key");
                    frame.Ax = (ushort)RadioOffset(key);
                    frame.Dx = SyntheticModuleSegment;
                    break;

                // ACE's timed wake: es=[bp+0xC] (pool), bx=[bp+0xE] (the actor-slot table), then
                // per slot k `mov si,ss:[bx+2k]` + `or byte es:[si+0x3C],1`.
                case "activate":
                    Activate(statement.Slots ?? [], in frame);
                    break;

                // An explicit `return;` — the compiler's `jmp END`.
                case "ret":
                    frame.Returned = true;
                    return;

                case "if":
                    if (GuardHolds(statement, in frame))
                    {
                        Execute(statement.Body ?? [], ref frame);
                    }

                    break;

                default:
                    throw Unsupported(
                        $"statement op '{statement.Op}'",
                        " _emit_body");
            }
        }
    }

    /// <summary>Whether an <c>if</c>'s guard lets its body run.</summary>
    /// <remarks>
    /// Two forms.  A HELPER guard is <c>call helper; jae AFTER</c>, so the body runs iff the helper
    /// left <c>CF = 1</c>.  An inline conjunction emits each test's jump-away branch to the same
    /// AFTER label, so the body runs iff NO test's branch is taken — the tests are ANDed and each
    /// keeps its own literal condition code.
    /// </remarks>
    private bool GuardHolds(WinRuleStmtDto statement, in Frame frame)
    {
        if (statement.Helper is int helper)
        {
            return EvaluateHelper(helper, in frame);
        }

        foreach (WinRuleTestDto test in statement.Tests ?? [])
        {
            if (Evaluate(test, in frame).Taken(test.Cc ?? string.Empty))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Runs one predicate helper and returns its CARRY flag.</summary>
    /// <remarks>
    /// <c>stc_first</c>: every test's branch jumps away ON FALSE to a <c>clc; ret</c> tail, so the
    /// predicate is the CONJUNCTION of "the branch was not taken".  <c>clc_first</c>: the test's
    /// branch jumps ON TRUE to a <c>stc; ret</c> tail, so the predicate is the DISJUNCTION of "the
    /// branch was taken".  Both spellings exist for identical rules (ARGUMENT.S vs MIGCAP2.S, both
    /// <c>c ≥ 2</c>), which is why the polarity is recorded per mission rather than normalised
    /// (rulebook, "HELPER").
    /// </remarks>
    private bool EvaluateHelper(int index, in Frame caller)
    {
        IReadOnlyList<WinRuleHelperDto> helpers = Rules.Helpers;
        if (index < 0 || index >= helpers.Count)
        {
            throw Malformed($"helper index {index} is outside the declared {helpers.Count}");
        }

        // A helper is a NEAR function with no frame of its own (`_synth_helper` emits tests + a
        // stc/clc tail and nothing else), so a `[bp+6]` inside it would read the CALLER's argument.
        // No shipped helper does — every one tests counters — but the evaluator passes the caller's
        // frame rather than zeros, so the day one does, it reads what the bytes would.
        WinRuleHelperDto helper = helpers[index];
        Frame frame = caller;
        switch (helper.Polarity)
        {
            case "stc_first":
                foreach (WinRuleTestDto test in helper.Tests ?? [])
                {
                    if (Evaluate(test, in frame).Taken(test.Cc ?? string.Empty))
                    {
                        return false;
                    }
                }

                return true;

            case "clc_first":
                foreach (WinRuleTestDto test in helper.Tests ?? [])
                {
                    if (Evaluate(test, in frame).Taken(test.Cc ?? string.Empty))
                    {
                        return true;
                    }
                }

                return false;

            default:
                throw Unsupported(
                    $"helper polarity '{helper.Polarity}'", " _synth_helper");
        }
    }

    /// <summary>Computes one test's <c>cmp</c> flags.</summary>
    private WinRuleFlags Evaluate(WinRuleTestDto test, in Frame frame) => test.Var switch
    {
        // `83 7E 06 ib` — cmp word [bp+6], imm8 sign-extended: the export's first argument word.
        "arg_w" => WinRuleFlags.CompareWord(frame.Argument, test.Imm),

        // `80 7E 06 ib` — cmp byte [bp+6], imm: the same argument, char-typed in the original's C
        // (BLOW.S, INT2.S, RESCAP.S — a source fact the IR keeps).
        "arg_b" => WinRuleFlags.CompareByte(frame.Argument, test.Imm),

        // `2E 80 3E cw ib` — cmp byte cs:[counter], imm.
        "ctr" => WinRuleFlags.CompareByte(_counters[Ordinal(test.Counter)], test.Imm),

        // `83 7E 0A ib` — cmp word [bp+0xA], imm8 sign-extended.  For check_win that word is
        // g_master_frame_counter [0xF0C8], which advances once per 256 ticks of frame time
        // (SceneFrameTimer) — about once a second, so a `clock >= 20` test is "20 seconds in".
        "clock" => WinRuleFlags.CompareWord(frame.Clock, test.Imm),

        _ => throw Unsupported($"test variable '{test.Var}'", " _emit_test"),
    };

    /// <summary>The ACE idiom's actor wake.</summary>
    /// <remarks>
    /// <c>asset:2b/ACE.S@BLOCK+0x26F..0x2A2</c>: <c>mov es,[bp+0xC]</c> (the object pool's segment,
    /// <c>g_object_pool_segment [0x0094]</c>), <c>mov bx,[bp+0xE]</c> (the actor-slot near-pointer
    /// table <c>g_spawn_slot_nearptr_table [0xEE5A]</c>), then per slot <c>k</c>
    /// <c>mov si,ss:[bx+2k]</c> (<c>36 8B 77 db</c>; <c>SS = DS = DGROUP</c>) and
    /// <c>or byte es:[si+0x3C],1</c> (<c>26 80 4C 3C 01</c>).
    /// </remarks>
    private void Activate(IReadOnlyList<int> slots, in Frame frame)
    {
        if (Registers is not { } registers || Arena is not { } arena)
        {
            ActorActivationsUnserviced++;
            return;
        }

        foreach (int slot in slots)
        {
            ushort objectRef = registers.Word(frame.ActorSlotTable + (slot * 2));
            if (objectRef == 0 || !arena.Covers(objectRef, 0x3D))
            {
                continue;
            }

            ushort field = (ushort)(objectRef + 0x3C);
            arena.SetByte(field, (byte)(arena.Byte(field) | 1));
            ActorSlotsActivated++;
        }
    }

    private int Ordinal(int? counter)
    {
        int ordinal = counter
            ?? throw Malformed("a counter-addressed operation without an ordinal");
        if (ordinal < 0 || ordinal >= _counters.Length)
        {
            throw Malformed($"counter ordinal {ordinal} is outside the declared {_counters.Length}");
        }

        return ordinal;
    }

    private int RadioOffset(string key) =>
        _radioTextOffsets.TryGetValue(key, out int offset)
            ? offset
            : throw Malformed($"'msg' names radio text '{key}', which the data layout does not place");

    private string? TextOf(string key)
    {
        foreach ((string name, string text) in Rules.DebriefTexts)
        {
            if (string.Equals(name, key, StringComparison.Ordinal))
            {
                return text;
            }
        }

        foreach ((string name, string text) in Rules.RadioTexts)
        {
            if (string.Equals(name, key, StringComparison.Ordinal))
            {
                return text;
            }
        }

        return key == "b" ? _model.Briefing : null;
    }

    /// <summary>
    /// Walks the module's DATA REGION exactly as the synthesiser lays it out, so a byte variable's
    /// ordinal and a radio text's offset are the module's own.
    /// </summary>
    /// <remarks>
    /// synth</c>: the cursor starts at <see cref="DataRegionOffset"/> after the export table and
    /// walks <c>data_layout</c> in SOURCE-DECLARATION order — a <c>save</c> item takes two zero
    /// bytes, a <c>byte</c> item one, an <c>rtext</c> item its latin-1 length.  <c>data_layout</c>
    /// is null for a template mission, whose layout is the default <c>[save] + [byte] ×
    /// n_counters</c> and whose save slot is therefore always <c>+0x0A</c>; RAMROD's is at
    /// <c>+0x55</c> and STRAFE's at <c>+0x7A</c> because their source declares the texts first
    /// (findings §3, "Ordered data layout").
    /// </remarks>
    private void BuildDataLayout()
    {
        List<WinRuleDataItemDto>? layout = _model.DataLayout;
        int cursor = DataRegionOffset;
        int written = 0;
        int? saveAt = null;

        if (layout is null or { Count: 0 })
        {
            saveAt = cursor;
            cursor += 2;
            for (; written < _counters.Length; written++)
            {
                _counterOffsets[written] = cursor++;
            }
        }
        else
        {
            foreach (WinRuleDataItemDto item in layout)
            {
                switch (item.Kind)
                {
                    case "save":
                        saveAt = cursor;
                        cursor += 2;
                        break;

                    case "byte":
                        if (written >= _counters.Length)
                        {
                            throw Malformed(
                                "the data layout declares more byte variables than n_counters");
                        }

                        _counterOffsets[written++] = cursor++;
                        break;

                    case "rtext":
                        string key = item.Key
                            ?? throw Malformed("an 'rtext' data item without a key");
                        string text = TextOf(key)
                            ?? throw Malformed($"the data layout places undefined radio text '{key}'");
                        _radioTextOffsets[key] = cursor;
                        _radioTextsByOffset[cursor] = text;
                        cursor += Latin1Length(text);
                        break;

                    default:
                        throw Unsupported(
                            $"data item kind '{item.Kind}'", " synth");
                }
            }
        }

        if (written != _counters.Length)
        {
            throw Malformed(
                $"the data layout places {written} byte variable(s) but n_counters is {_counters.Length}");
        }

        if (saveAt != _model.SaveOff)
        {
            throw Malformed(
                $"the data layout puts the DS save slot at +0x{saveAt ?? -1:X}, the model says "
                    + $"+0x{_model.SaveOff:X}");
        }
    }

    /// <summary>The byte length of a latin-1 model string — one char, one byte.</summary>
    private static int Latin1Length(string text) => text.Length;

    /// <summary>
    /// Refuses, at construction, any IR this evaluator cannot run — the rule that an op
    /// whose meaning cannot be cited must throw at load rather than be guessed.
    /// </summary>
    private void Validate()
    {
        foreach (WinRuleHelperDto helper in Rules.Helpers)
        {
            if (helper.Polarity is not ("stc_first" or "clc_first"))
            {
                throw Unsupported(
                    $"helper polarity '{helper.Polarity}'", " _synth_helper");
            }

            ValidateTests(helper.Tests);
        }

        string kind = Rules.WinCondition.Kind ?? "never";
        if (kind is not ("template" or "never"))
        {
            throw Unsupported($"fn3 kind '{kind}'", " synth emit3");
        }

        if (kind == "template")
        {
            int helper = Rules.WinCondition.Helper
                ?? throw Malformed("fn3 kind 'template' without a helper index");
            if (helper < 0 || helper >= Rules.Helpers.Count)
            {
                throw Malformed(
                    $"fn3's helper index {helper} is outside the declared {Rules.Helpers.Count}");
            }
        }

        foreach (WinRuleStmtDto statement in Rules.AllStatements())
        {
            ValidateStatement(statement);
        }
    }

    private void ValidateStatement(WinRuleStmtDto statement)
    {
        switch (statement.Op)
        {
            case "inc":
                Ordinal(statement.Counter);
                break;

            case "copy":
                if (statement.Text is null)
                {
                    throw Malformed("a 'copy' statement without a text key");
                }

                break;

            case "msg":
                RadioOffset(statement.Text
                    ?? throw Malformed("a 'msg' statement without a text key"));
                break;

            case "ax":
            case "dx0":
            case "ret":
            case "activate":
                break;

            case "if":
                if (statement.Helper is int helper
                    && (helper < 0 || helper >= Rules.Helpers.Count))
                {
                    throw Malformed($"helper index {helper} is outside the declared {Rules.Helpers.Count}");
                }

                ValidateTests(statement.Tests);
                break;

            default:
                throw Unsupported(
                    $"statement op '{statement.Op}'", " _emit_body");
        }
    }

    private void ValidateTests(IReadOnlyList<WinRuleTestDto>? tests)
    {
        foreach (WinRuleTestDto test in tests ?? [])
        {
            if (test.Var is not ("arg_w" or "arg_b" or "ctr" or "clock"))
            {
                throw Unsupported(
                    $"test variable '{test.Var}'", " _emit_test");
            }

            if (test.Var == "ctr")
            {
                Ordinal(test.Counter);
            }

            if (test.Cc is not { } cc || !WinRuleFlags.KnownConditionCodes.Contains(cc))
            {
                throw Unsupported(
                    $"condition code '{test.Cc}'", " JCC_OPC");
            }
        }
    }

    private NotSupportedException Unsupported(string what, string authority) =>
        new($"{AssetName}: the win-rule IR uses {what}, which this evaluator does not model "
            + $"(the rulebook that would define it is {authority}).  The IR is literal on purpose: "
            + "an op whose meaning cannot be cited must not be guessed.");

    private InvalidDataException Malformed(string what) =>
        new($"{AssetName}: the win-rule IR is malformed — {what}.");
}

/// <summary>What <c>get_debrief_text</c> selected.</summary>
/// <param name="TextKey">The IR key of the text the walk copied, e.g. <c>"d0"</c>.</param>
/// <param name="Text">That text, or null when the walk copied none.</param>
/// <param name="Result">
/// The module's <c>AX</c>: <c>≥ 1</c> = mission accomplished (post-mission mode 4), 0 = not
/// (mode 5) — <c>image@0x25C79..0x25C82</c>.
/// </param>
public readonly record struct WinRuleDebrief(string? TextKey, string? Text, ushort Result)
{
    /// <summary>Whether the module scored the sortie a success.</summary>
    public bool Accomplished => Result >= 1;
}
