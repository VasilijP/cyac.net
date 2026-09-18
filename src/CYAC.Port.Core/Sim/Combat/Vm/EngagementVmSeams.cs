using CYAC.Port.Core.Sim.Combat.Geometry;

namespace CYAC.Port.Core.Sim.Combat.Vm;

/// <summary>A VM seam this build does not implement — never a silent default.</summary>
/// <remarks>
/// The K5/C2/C3a/C3b convention: a verification catches this and counts the call as
/// UNCERTIFIABLE rather than reporting a pass.
/// </remarks>
public sealed class EngagementVmSeamException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Which seam, and who owns it.</param>
    public EngagementVmSeamException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    /// <param name="message">Which seam, and who owns it.</param>
    /// <param name="innerException">The cause.</param>
    public EngagementVmSeamException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with the default message.</summary>
    public EngagementVmSeamException()
        : base("an engagement-VM seam was reached that this build does not implement")
    {
    }
}

/// <summary>
/// The far calls the OPCODE HANDLERS make into other subsystems — the six utility opcodes that make
/// the bytecode a general scripting facility rather than a manoeuvre encoder.
/// </summary>
/// <remarks>
/// Every one of these is <b>cold</b> on the recorded dynamic call graph except
/// <see cref="PrintString"/>, which an earlier pass proved LIVE on the newer recordings.
/// Each is unit-tested from an authored synthetic program and carries a census counter so the day a
/// recording reaches one, the verification says so.
/// </remarks>
public interface IVmScriptEffects
{
    /// <summary>
    /// Opcode <c>0xD5 KILL_ACTOR</c> — <c>slot_4x19_clear_for_owner @image@0x0B582</c>
    /// (<c>lcall 0x108e,0xaca2</c> @<c>image@0x05517</c>), with <c>BX</c> = the actor table's word.
    /// </summary>
    /// <param name="actorRecordRef">The word at <c>[0xEE5A + 2·index]</c>.</param>
    void KillActor(ushort actorRecordRef);

    /// <summary>
    /// Opcode <c>0xD6 SPAWN_ACTOR</c> — <c>subsystem4x19_row_attach (ex-projectile_spawn) @image@0x0B467</c>
    /// (<c>lcall 0x108e,0xab87</c> @<c>image@0x05503</c>).
    /// </summary>
    /// <param name="request">The eight pushed words, named.</param>
    void SpawnActor(in VmSpawnActorRequest request);

    /// <summary>
    /// Opcode <c>0xDE FIRE_WEAPON</c>, first call —
    /// <c>deferred_effect_record_schedule @image@0x03A7C</c>
    /// (<c>lcall 0x108e,0x319c</c> @<c>image@0x0555F</c>).
    /// </summary>
    /// <param name="request">The nine pushed words, named.</param>
    void ScheduleDeferredEffect(in VmDeferredEffectRequest request);

    /// <summary>
    /// Opcode <c>0xDE</c>, second call — <c>sfx_object_impact_dispatch @image@0x29BA3</c>
    /// (<c>lcall 0x3981,0x393</c> @<c>image@0x05571</c>), <c>AL = 1</c> and a far pointer to the
    /// actor record's <c>+0x06</c>.
    /// </summary>
    /// <param name="actorRecordRef">The actor record's near offset.</param>
    void ImpactSound(ushort actorRecordRef);

    /// <summary>
    /// Opcode <c>0xDE</c>, the distance-gated arm —
    /// <c>subsystem5x06_per_frame_slot_fire @image@0x2DB69</c>
    /// (<c>lcall 0x3d85,0x319</c> @<c>image@0x055BE</c>) with the actor's two position pairs.
    /// </summary>
    /// <param name="x">The actor record's <c>+0x06</c> pair.</param>
    /// <param name="z">Its <c>+0x0E</c> pair.</param>
    void SlotFire(int x, int z);

    /// <summary>
    /// Opcode <c>0xDF PRINT_STRING</c> — <c>show_cockpit_text_string @image@0x0CC5B</c>
    /// (<c>lcall 0x108e,0xc37b</c> @<c>image@0x055E4</c>) with a far pointer to the assembled
    /// stack buffer.
    /// </summary>
    /// <param name="text">The NUL-terminated string the handler copied to <c>[bp-0x7E]</c>.</param>
    void PrintString(string text);

    /// <summary>
    /// Opcode <c>0xE0 CALL_SCRIPT_FUNCTION</c> —
    /// <c>combat_vtable_slot_fn4_dispatch @image@0x08CAB</c>
    /// (<c>lcall 0x108e,0x83cb</c> @<c>image@0x0547C</c>), the <c>.S</c> mission module's own native
    /// code through the vtable at <c>[0x0FB8]</c>.
    /// </summary>
    /// <param name="functionIndex">The single pushed word — the operand.</param>
    void CallScriptFunction(ushort functionIndex);
}

/// <summary>
/// The non-opcode helpers the interpreter's OUTER arms call, which belong to other builders.
/// </summary>
public interface IVmSupport
{
    /// <summary>
    /// <c>world_extents_object_in_bounds @image@0x0B8C0</c> (<c>call 0xafe0</c>
    /// @<c>image@0x059F3</c>) with <c>BX</c> = <c>g_acq_current_target [0xED6F]</c>.
    /// </summary>
    /// <param name="registers">The register file (the four extent words <c>[0xF0E8..0xF0F4]</c>).</param>
    /// <param name="arena">The pool arena.</param>
    /// <param name="objectRef">The object's pool near offset.</param>
    /// <returns>
    /// The original's <c>AL</c>: 1 when the object is INSIDE the world extents.  The name in the C4 brief
    /// ("out of bounds") is inverted — <c>image@0x0B8ED</c> returns 1 on the all-tests-pass path.  The
    /// interpreter's <c>jne</c> @<c>image@0x059F8</c> therefore skips the waypoint re-engage for an
    /// IN-BOUNDS target.
    /// </returns>
    bool ObjectWithinWorldExtents(CombatRegisters registers, PoolArena arena, ushort objectRef);

    /// <summary>
    /// <c>radar_bearing_range_farptr_unpack @image@0x15A62</c> (<c>lcall 0x201d,0x5892</c>
    /// @<c>image@0x05AA5</c>) — the two out-words the "player is dead ahead and close" arm needs.
    /// </summary>
    /// <param name="registers">The register file (the routine reads <c>[0xED42]</c>).</param>
    /// <param name="arena">The pool arena.</param>
    /// <param name="targetRef">The pool near offset pushed as the source object.</param>
    /// <param name="bearing">The word written through the <c>BX</c> out-pointer (<c>[bp-2]</c>).</param>
    /// <param name="elevation">The word written through the <c>AX</c> out-pointer (<c>[bp-6]</c>).</param>
    void BearingAndElevationTo(
        CombatRegisters registers, PoolArena arena, ushort targetRef,
        out short bearing, out short elevation);
}

/// <summary>
/// Opcode <c>0xD6 SPAWN_ACTOR</c>'s eight pushed words, in the order
/// <c>image@0x054E6..0x05502</c> pushes them (so the LAST field is the callee's first stack word).
/// </summary>
/// <param name="Type">The low byte of the second operand (<c>mov al,[bp-0x24]</c>).</param>
/// <param name="ActiveFlag">The constant <c>1</c> (<c>mov al,1</c> @<c>image@0x054EA</c>).</param>
/// <param name="Heading">The third operand (<c>[bp-0x20]</c>).</param>
/// <param name="Zero">The <c>sub cx,cx</c> pad (<c>image@0x054F0</c>).</param>
/// <param name="Duration">The fourth operand (<c>[bp-0x26]</c>).</param>
/// <param name="ActorRecordRef">
/// <c>[0xEE5A + 2·slot]</c> — the record the first operand names (<c>image@0x054F6</c>).
/// </param>
/// <param name="ZeroA">The first <c>sub ax,ax</c> push.</param>
/// <param name="ZeroB">The second.</param>
public readonly record struct VmSpawnActorRequest(
    byte Type,
    byte ActiveFlag,
    ushort Heading,
    ushort Zero,
    ushort Duration,
    ushort ActorRecordRef,
    ushort ZeroA,
    ushort ZeroB);

/// <summary>
/// Opcode <c>0xDE FIRE_WEAPON</c>'s nine pushed words (<c>image@0x05546..0x0555B</c>).
/// </summary>
/// <param name="ActorRecordRef">The actor record's near offset — pushed first and again as a flag.</param>
/// <param name="One">The constant <c>1</c> (<c>mov al,1</c> @<c>image@0x05547</c>).</param>
/// <param name="Field08">The actor record's <c>+0x08</c> word.</param>
/// <param name="Field06">Its <c>+0x06</c>.</param>
/// <param name="ZeroA">The first <c>sub cx,cx</c> push.</param>
/// <param name="ZeroB">The second.</param>
/// <param name="Field10">Its <c>+0x10</c>.</param>
/// <param name="Field0E">Its <c>+0x0E</c>.</param>
public readonly record struct VmDeferredEffectRequest(
    ushort ActorRecordRef,
    byte One,
    ushort Field08,
    ushort Field06,
    ushort ZeroA,
    ushort ZeroB,
    ushort Field10,
    ushort Field0E);

/// <summary>The tripwire <see cref="IVmScriptEffects"/>: every method throws.</summary>
public sealed class UnavailableVmScriptEffects : IVmScriptEffects
{
    /// <summary>The shared instance.</summary>
    public static UnavailableVmScriptEffects Instance { get; } = new();

    /// <inheritdoc/>
    public void KillActor(ushort actorRecordRef) => throw Reached("0xD5 KILL_ACTOR");

    /// <inheritdoc/>
    public void SpawnActor(in VmSpawnActorRequest request) => throw Reached("0xD6 SPAWN_ACTOR");

    /// <inheritdoc/>
    public void ScheduleDeferredEffect(in VmDeferredEffectRequest request) =>
        throw Reached("0xDE FIRE_WEAPON (deferred_effect_record_schedule)");

    /// <inheritdoc/>
    public void ImpactSound(ushort actorRecordRef) =>
        throw Reached("0xDE FIRE_WEAPON (sfx_object_impact_dispatch)");

    /// <inheritdoc/>
    public void SlotFire(int x, int z) =>
        throw Reached("0xDE FIRE_WEAPON (subsystem5x06_per_frame_slot_fire)");

    /// <inheritdoc/>
    public void PrintString(string text) => throw Reached("0xDF PRINT_STRING");

    /// <inheritdoc/>
    public void CallScriptFunction(ushort functionIndex) =>
        throw Reached("0xE0 CALL_SCRIPT_FUNCTION");

    private static EngagementVmSeamException Reached(string what) =>
        new($"the AI script executed {what}; this run did not wire the effect seam up.");
}

/// <summary>
/// The REAL <see cref="IVmSupport"/> — both members are small pure queries over state the port
/// already has, so neither has to stay a seam.
/// </summary>
public sealed class VmSupport : IVmSupport
{
    /// <summary>The shared instance.</summary>
    public static VmSupport Instance { get; } = new();

    /// <inheritdoc/>
    /// <remarks>
    /// <c>image@0x0B8C0</c>: four SIGNED word compares against
    /// <c>[0xF0E8]</c>/<c>[0xF0EC]</c> (the object's <c>+0x08</c>, the HIGH word of its Y) and
    /// <c>[0xF0F0]</c>/<c>[0xF0F4]</c> (its <c>+0x10</c>, the high word of Z).  X is never tested.
    /// </remarks>
    public bool ObjectWithinWorldExtents(
        CombatRegisters registers, PoolArena arena, ushort objectRef)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        short y = (short)arena.Word((ushort)(objectRef + 0x08));
        short z = (short)arena.Word((ushort)(objectRef + 0x10));
        return y >= (short)registers.Word(0xF0E8)                   // image@0x0B8CC jl
            && y <= (short)registers.Word(0xF0EC)                   // image@0x0B8D5 jg
            && z >= (short)registers.Word(0xF0F0)                   // image@0x0B8DE jl
            && z <= (short)registers.Word(0xF0F4);                  // image@0x0B8E7 jg
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>radar_bearing_range_farptr_unpack @image@0x15A62</c> is a pure ADAPTER: it unpacks a raw
    /// i32 XYZ block (<c>[bp+0xA]</c> — the interpreter passes <c>DS:0xED42</c>, the fire position)
    /// and an OBJECT BASE (<c>[bp+6]</c>, whose <c>+0x06</c> the adapter adds itself) into the six
    /// words <c>bearing_range_abs_pos_compute @image@0x1864E</c> takes, which C1/C3a already ported
    /// as <see cref="CombatGeometry.BearingAndElevation"/>.
    /// The two out-pointers are <c>BX</c> = heading and <c>AX</c> = elevation.
    /// </remarks>
    public void BearingAndElevationTo(
        CombatRegisters registers, PoolArena arena, ushort targetRef,
        out short bearing, out short elevation)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        CombatPosition from = new CombatPosition(
            unchecked((int)(registers.Word(0xED42) | ((uint)registers.Word(0xED44) << 16))),
            unchecked((int)(registers.Word(0xED46) | ((uint)registers.Word(0xED48) << 16))),
            unchecked((int)(registers.Word(0xED4A) | ((uint)registers.Word(0xED4C) << 16))));

        CombatPosition to = new CombatPosition(
            unchecked((int)(arena.Word((ushort)(targetRef + 0x06))
                | ((uint)arena.Word((ushort)(targetRef + 0x08)) << 16))),
            unchecked((int)(arena.Word((ushort)(targetRef + 0x0A))
                | ((uint)arena.Word((ushort)(targetRef + 0x0C)) << 16))),
            unchecked((int)(arena.Word((ushort)(targetRef + 0x0E))
                | ((uint)arena.Word((ushort)(targetRef + 0x10)) << 16))));

        CombatBearing result = CombatGeometry.BearingAndElevation(from, to);
        bearing = result.Heading;
        elevation = result.Elevation;
    }
}

/// <summary>The tripwire <see cref="IVmSupport"/>.</summary>
public sealed class UnavailableVmSupport : IVmSupport
{
    /// <summary>The shared instance.</summary>
    public static UnavailableVmSupport Instance { get; } = new();

    /// <inheritdoc/>
    public bool ObjectWithinWorldExtents(
        CombatRegisters registers, PoolArena arena, ushort objectRef) =>
        throw new EngagementVmSeamException(
            "world_extents_object_in_bounds @image@0x0B8C0 was reached; wire VmSupport up.");

    /// <inheritdoc/>
    public void BearingAndElevationTo(
        CombatRegisters registers, PoolArena arena, ushort targetRef,
        out short bearing, out short elevation) =>
        throw new EngagementVmSeamException(
            "radar_bearing_range_farptr_unpack @image@0x15A62 was reached; wire VmSupport up.");
}

/// <summary>
/// Everything one interpreter entry operates on: C3a's geometry context (the register file, the
/// arena, the constant tables, the geometry seams) plus the script heap, the RNG, the prototypes,
/// the two VM seams and the outbound events.
/// </summary>
/// <remarks>
/// NESTS <see cref="EngagementGeometryContext"/> rather than deriving — the same choice an earlier pass made,
/// for the same reason: one register file, one arena, one census across the whole cluster.
/// </remarks>
public sealed class EngagementVmContext
{
    /// <summary>C3a's context — the shared register file, arena, static data and geometry seams.</summary>
    public required EngagementGeometryContext Geometry { get; init; }

    /// <summary>The far heap the program buffers live in.</summary>
    public required IAiScriptHeap Heap { get; init; }

    /// <summary>The Sim LFSR.  By law it advances <c>[0x07A8]</c> inside the register file.</summary>
    public required ICombatRandom Random { get; init; }

    /// <summary>The engagement class prototypes.</summary>
    public required IEngagementPrototypes Prototypes { get; init; }

    /// <summary>The opcode-driven effect calls.</summary>
    public IVmScriptEffects Effects { get; init; } = UnavailableVmScriptEffects.Instance;

    /// <summary>The non-opcode helpers other builders own.</summary>
    public IVmSupport Support { get; init; } = VmSupport.Instance;

    /// <summary>The outbound notifications <c>target_departure_cleanup</c> raises.</summary>
    public ICombatEvents Events { get; init; } = NullCombatEvents.Instance;

    /// <summary>The per-arm census this run fills.</summary>
    public EngagementVmCensus Census { get; } = new();

    /// <summary>The register file.</summary>
    public CombatRegisters Registers => Geometry.Registers;

    /// <summary>The pool arena.</summary>
    public PoolArena Arena => Geometry.Arena;

    /// <summary>The constant DGROUP regions.</summary>
    public ICombatStaticData StaticData => Geometry.StaticData;

    /// <summary>C3a's named view of the register file.</summary>
    public EngagementAngleView View => Geometry.View;
}

/// <summary>
/// Which arms one <see cref="EngagementVm"/> entry reached.  Instrumentation only: the kernel never
/// reads it back, and the verification prints it so an arm no recording reaches is visible
/// rather than silently unproven.
/// </summary>
public sealed class EngagementVmCensus
{
    /// <summary>How many interpreter entries ran.</summary>
    public long Entries { get; set; }

    /// <summary>Entries per mode <c>AL</c> — index 0..7 plus 8 for "anything else".</summary>
    public long[] Mode { get; } = new long[9];

    /// <summary>The <c>[0xED61] == 6</c> guard fired and the entry did nothing (<c>image@0x05165</c>).</summary>
    public long Phase6Guard { get; set; }

    /// <summary>How many opcodes were fetched and dispatched (<c>image@0x05400</c>).</summary>
    public long OpcodesExecuted { get; set; }

    /// <summary>Executions per opcode byte.</summary>
    public long[] Opcode { get; } = new long[256];

    /// <summary>Dispatches that landed on the INVALID arm <c>image@0x051C0</c>.</summary>
    public long InvalidOpcodes { get; set; }

    /// <summary>How many LFSR draws the entry made (its own count, for the RNG proof).</summary>
    public long RandomDraws { get; set; }

    /// <summary>Calls to <c>ensure_script_loaded</c> that ALLOCATED a new 50-byte block.</summary>
    public long ProgramsAllocated { get; set; }

    /// <summary>Bytes the JIT emitters wrote.</summary>
    public long EmittedBytes { get; set; }

    /// <summary>Complete <c>0x0B</c> instructions <c>ai_script_emit_op0B_settargets</c> wrote.</summary>
    public long EmittedSetTargets { get; set; }

    /// <summary>Fetches whose PC fell outside the heap block (see <see cref="ScriptReadsOutsideBlock"/>).</summary>
    public long ScriptReadsOutsideBlock { get; set; }

    /// <summary>The zone-program copy-in at <c>image@0x0587D</c> fired.</summary>
    public long ZoneProgramCopyIns { get; set; }

    /// <summary>The zone scan ran but found NO zone (<c>image@0x05859</c>).</summary>
    public long ZoneScanEmpty { get; set; }

    /// <summary>The mode-5 close-range kill arm ran (<c>image@0x053B6</c>).</summary>
    public long Mode5Kill { get; set; }

    /// <summary>The mode-4 new-engagement arm ran (<c>image@0x052D0</c>).</summary>
    public long Mode4NewEngagement { get; set; }

    /// <summary>The mode-7 prng arm ran (<c>image@0x051CA</c>).</summary>
    public long Mode7Prng { get; set; }

    /// <summary>The mode-2 timer-guard/release arm ran (<c>image@0x052B2</c>).</summary>
    public long Mode2TimerGuard { get; set; }

    /// <summary>The autonomous phase→7 transition at <c>image@0x058AC</c>.</summary>
    public long AutoPhase7Short { get; set; }

    /// <summary>The autonomous phase→2 re-engage from the <c>[0xEDB6]</c> waypoint (<c>image@0x059FA</c>).</summary>
    public long AutoPhase2Waypoint { get; set; }

    /// <summary>The autonomous phase→0xB at <c>image@0x05BCB</c>.</summary>
    public long AutoPhaseBFirst { get; set; }

    /// <summary>The autonomous phase→0xB at <c>image@0x05C2B</c>.</summary>
    public long AutoPhaseBSecond { get; set; }

    /// <summary>The outcome resolver's evasion arm armed <c>[0xED81]</c> bit1 (<c>image@0x088B0</c>).</summary>
    public long EvasionArmed { get; set; }

    /// <summary>The outcome resolver took the LOCKED-ON arm (<c>image@0x08914</c>).</summary>
    public long OutcomeLockedOn { get; set; }

    /// <summary>The outcome resolver took the post-hit evasion arm (<c>image@0x0894A</c>).</summary>
    public long OutcomePostHitEvasion { get; set; }

    /// <summary>Unconditional <c>subsystem4x19_row_attach</c> calls the outcome resolver made.</summary>
    public long OutcomeSpawns { get; set; }

    /// <summary>Behaviour-compiler arms entered, indexed by the <c>[0xED80]</c> value they stamp.</summary>
    public long[] CompilerArm { get; } = new long[0x20];

    /// <summary>
    /// When set, <see cref="Trail"/> records the image address of every labelled block the run
    /// entered — the diagnostic that turns "the port took a different arm" into "the port took
    /// <c>image@0xNNNNN</c> where the machine took <c>image@0xMMMMM</c>".  Off by default.
    /// </summary>
    public bool TrailEnabled { get; set; }

    /// <summary>The arm path of the last run, in order, when <see cref="TrailEnabled"/>.</summary>
    public List<int> Trail { get; } = [];

    /// <summary>Records one labelled block's image address.</summary>
    /// <param name="imageAddress">The block's address.</param>
    public void Visit(int imageAddress)
    {
        if (TrailEnabled && Trail.Count < 4096)
        {
            Trail.Add(imageAddress);
        }
    }

    /// <summary>Records a compiler arm by its <c>[0xED80]</c> stamp.</summary>
    /// <param name="behaviour">The value written to <c>g_engagement_new_engagement_flag</c>.</param>
    public void Compiler(int behaviour)
    {
        if (behaviour >= 0 && behaviour < CompilerArm.Length)
        {
            CompilerArm[behaviour]++;
        }
    }

    /// <summary>Folds another census into this one.</summary>
    /// <param name="other">The census to add.</param>
    public void Add(EngagementVmCensus other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Entries += other.Entries;
        Phase6Guard += other.Phase6Guard;
        OpcodesExecuted += other.OpcodesExecuted;
        InvalidOpcodes += other.InvalidOpcodes;
        RandomDraws += other.RandomDraws;
        ProgramsAllocated += other.ProgramsAllocated;
        EmittedBytes += other.EmittedBytes;
        EmittedSetTargets += other.EmittedSetTargets;
        ScriptReadsOutsideBlock += other.ScriptReadsOutsideBlock;
        ZoneProgramCopyIns += other.ZoneProgramCopyIns;
        ZoneScanEmpty += other.ZoneScanEmpty;
        Mode5Kill += other.Mode5Kill;
        Mode4NewEngagement += other.Mode4NewEngagement;
        Mode7Prng += other.Mode7Prng;
        Mode2TimerGuard += other.Mode2TimerGuard;
        AutoPhase7Short += other.AutoPhase7Short;
        AutoPhase2Waypoint += other.AutoPhase2Waypoint;
        AutoPhaseBFirst += other.AutoPhaseBFirst;
        AutoPhaseBSecond += other.AutoPhaseBSecond;
        EvasionArmed += other.EvasionArmed;
        OutcomeLockedOn += other.OutcomeLockedOn;
        OutcomePostHitEvasion += other.OutcomePostHitEvasion;
        OutcomeSpawns += other.OutcomeSpawns;

        for (int i = 0; i < Mode.Length; i++)
        {
            Mode[i] += other.Mode[i];
        }

        for (int i = 0; i < Opcode.Length; i++)
        {
            Opcode[i] += other.Opcode[i];
        }

        for (int i = 0; i < CompilerArm.Length; i++)
        {
            CompilerArm[i] += other.CompilerArm[i];
        }
    }

    /// <summary>A one-line summary for a verification's output.</summary>
    public override string ToString() =>
        $"VM: {Entries} entries, {OpcodesExecuted} opcodes, {RandomDraws} draws, "
            + $"{EmittedBytes} B emitted ({EmittedSetTargets} SET_TARGETS), "
            + $"{ZoneProgramCopyIns} zone copy-ins, {InvalidOpcodes} invalid";
}
