using CYAC.Port.Core.Sim.Combat.Geometry;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>A seam the per-node FSM reached that this build does not implement.</summary>
/// <remarks>
/// The K5/C2/C3a convention: "the callee did not run" is never a silent default.  A verification
/// catches this and counts the call as UNCERTIFIABLE rather than reporting a pass.
/// </remarks>
public sealed class EngagementNodeSeamException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Which seam, and who owns it.</param>
    public EngagementNodeSeamException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    /// <param name="message">Which seam, and who owns it.</param>
    /// <param name="innerException">The cause.</param>
    public EngagementNodeSeamException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with the default message.</summary>
    public EngagementNodeSeamException()
        : base("a per-node FSM seam was reached that this build does not implement")
    {
    }
}

/// <summary>
/// <c>engagement_script_interpreter @image@0x05154</c> as the per-node FSM calls it DIRECTLY —
/// three doors, all with the mode in <c>AL</c> and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from C2's <see cref="IEngagementVm"/>, which models <c>engagement_slot_fsm_advance
/// @image@0x04F64</c> (a FAR function taking <c>BX</c> = an engagement block and <c>AL</c> = a
/// mode).  The FSM never goes through <c>fsm_advance</c>: C0's door census (P4) lists
/// <c>image@0x0446E</c> and <c>image@0x04831</c> inside this function plus <c>image@0x087CB</c>
/// inside <c>engagement_close_range_proximity_kill_check</c> as three of the interpreter's six
/// doors.
/// </para>
/// <para>
/// Another part of the port owns the body (5,319 B, 52 RNG sites).  Verification feeds it from the P4 probe pair.
/// </para>
/// </remarks>
public interface IEngagementScriptInterpreter
{
    /// <summary>Runs one interpreter entry.</summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="arena">The pool arena.</param>
    /// <param name="mode">
    /// The original's <c>AL</c>: <c>0</c> from case A's fallback (<c>image@0x0446C</c>), the §F3
    /// shot class <c>2</c>/<c>0</c>/<c>1</c>/<c>8</c> (<c>image@0x04803..0x0482F</c>), and <c>5</c>
    /// from the close-range kill check (<c>image@0x087C9</c>).
    /// </param>
    void Run(CombatRegisters registers, PoolArena arena, byte mode);
}

/// <summary>
/// <c>enemy_target_acquisition_state_machine @image@0x07F7C</c> — the FSM's §F2 callee
/// (<c>image@0x047D6</c>), the ONE door it has image-wide.
/// </summary>
/// <remarks>
/// A seam so that a P2 verification can drive it from the machine's own P5 record while
/// <see cref="EnemyTargetAcquisition"/> is verified separately per P5 call — the two-level
/// structure described above.  <see cref="EnemyTargetAcquisition.Seam"/> is the real one
/// and is the runtime default.
/// </remarks>
public interface IAcquisitionStateMachine
{
    /// <summary>Runs one acquisition pass.  No arguments — the original takes none.</summary>
    /// <param name="context">The node context.</param>
    void Step(EngagementNodeContext context);
}

/// <summary>
/// <c>combat_vtable_slot_fn2_dispatch @image@0x08C72</c> — the <c>.S</c> mission module's
/// <c>on_slot_destroyed</c> hook (vtable <c>[0x0FB8]</c>, <c>+0x02</c>), which
/// <c>engagement_kill_tally_and_slot_dispatch</c> calls at <c>image@0x0874E</c>.
/// </summary>
/// <remarks>Another part of the port owns it (the WinRule-IR evaluator); verification feeds it from the P19 probe.</remarks>
public interface IEngagementNodeMissionHook
{
    /// <summary>Fires the hook.</summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="slotReference">
    /// The single stack word the tally pushes — <c>g_engagement_player_slot_nearptr [0xED56]</c>
    /// (<c>image@0x0874A</c>).
    /// </param>
    void OnSlotDestroyed(CombatRegisters registers, ushort slotReference);
}

/// <summary>
/// <c>combat_spawn_slot_alloc_and_film_record @image@0x02423</c> as the AI fires it
/// (<c>image@0x083CB</c>, the acquisition state machine's STATE 4).
/// </summary>
/// <remarks>
/// <para>
/// Report-only correction to the C3b brief: this function is <b>not</b> ported by C2 — C2 landed
/// <c>combat_object_tick</c>, the fire resolver and the depart path, and the only trace of
/// <c>0x02423</c> in <c>CYAC.Port.Core</c> is two doc comments.  It stays a seam here, which is what
/// the verification plan already assumed ("P10 … oracle-fed").  Another part of the port owns the body.
/// </para>
/// <para>
/// ABI at the AI door: <c>BX</c> = the weapon-class descriptor (<c>SI</c> in the caller),
/// <c>AX</c> = <c>[0xED56]</c>, <c>DX</c> = a near pointer to the caller's <c>[bp-0x12]</c> spawn
/// parameter block, and ONE pushed word — the fire flag from the skill roll
/// (<c>image@0x083C2</c>).  It answers in <c>AL</c>.
/// </para>
/// </remarks>
public interface ISpawnSlotAllocator
{
    /// <summary>Allocates a spawn slot and records the shot in the film.</summary>
    /// <param name="context">The node context.</param>
    /// <param name="request">The six stack words plus the three register arguments.</param>
    /// <returns>The original's <c>AL</c>: non-zero when a slot was allocated.</returns>
    bool Allocate(EngagementNodeContext context, in AiShotRequest request);
}

/// <summary>
/// One AI shot as <c>enemy_target_acquisition_state_machine</c>'s STATE 4 hands it to
/// <c>combat_spawn_slot_alloc_and_film_record @image@0x02423</c>
/// (<c>image@0x0839B..0x083CB</c>).
/// </summary>
/// <param name="WeaponClassRef">The original's <c>BX</c> — the firing weapon class (<c>SI</c>).</param>
/// <param name="OwnerSlot">The original's <c>AX</c> — <c>[0xED56]</c>.</param>
/// <param name="Parameters">
/// The original's <c>DX</c>: a near pointer to the caller's twelve-byte block at <c>[bp-0x12]</c>,
/// which the world-grid query filled through its own out-pointer (<c>image@0x08352</c>) and
/// <c>weapon_fire_spawn_record_fill</c> then rewrote (<c>image@0x039BD</c>) — a position triple.
/// </param>
/// <param name="TargetRef">The FIRST pushed word — <c>g_acq_current_target [0xED6F]</c>.</param>
/// <param name="Elevation">The second — the guidance elevation from <c>[bp-0x14]</c>.</param>
/// <param name="Heading">The third — the guidance heading from <c>[bp-0x16]</c>.</param>
/// <param name="SpeedQ8">
/// The fourth and fifth — <c>[0xED79]:[0xED7B]</c>, or ZERO when the class prototype's
/// <c>+0x0C</c> has either of its low two bits set (<c>image@0x082CD</c>).
/// </param>
/// <param name="FireFlag">
/// The last pushed word: the skill roll's verdict, <c>rand8 &lt; [0xED71]</c>
/// (<c>image@0x083AD</c>).
/// </param>
public readonly record struct AiShotRequest(
    ushort WeaponClassRef,
    ushort OwnerSlot,
    CombatPosition Parameters,
    ushort TargetRef,
    short Elevation,
    short Heading,
    int SpeedQ8,
    bool FireFlag);

/// <summary>
/// <c>slot_alloc_and_activate @image@0x2C4F6</c> — the DESTRUCTION / DEBRIS pool's allocator, which
/// <c>enemy_spawn_with_angle_pos_init</c> reaches through <c>lcall 0x3c2b:0x246</c>
/// (<c>image@0x08AF9</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Seam, decided from the bytes</b>: 377 B / 148 instructions with SIX callees
/// of its own — <c>image@0x2C34F</c>, <c>image@0x2C447</c> ×2, <c>image@0x2C3D3</c> ×2, a far call
/// into segment <c>0x1000</c> (<c>image@0x001D0</c>) and a far call into the FILM segment
/// <c>0x401C</c> (<c>image@0x30AF8</c>).  It allocates one of the three <c>0x45</c>-byte
/// <c>s_object_slot</c> records at <c>[0xBB4C..0xBC1A]</c> (the extent re-derived from
/// <c>slot_find_by_ext_ptr_a @image@0x2C380</c>'s <c>0xBBD6 → 0xBB4C by −0x45</c> walk) and links a
/// fresh pool object.  That is a whole lifecycle subsystem — C5's — not a leaf.
/// </para>
/// <para>
/// ABI: FAR, <c>retf 0x0C</c>, six pushed words (<c>image@0x08ACE..0x08AF8</c>): the arc
/// accumulator's <c>[0xED79]/[0xED7B]</c> pair, a zero byte, the prototype's <c>+0x0C &amp; 0x40</c>
/// selector, <c>[0xED56]</c>, and the spawn-variant flag from the <c>rand8 &lt; 0x14</c> roll.
/// </para>
/// </remarks>
public interface IObjectSlotPool
{
    /// <summary>Allocates and activates a destruction/debris slot.</summary>
    /// <param name="context">The node context.</param>
    /// <param name="ownerSlot">The pushed <c>[0xED56]</c>.</param>
    /// <param name="arcAccumulator">The pushed <c>[0xED79]:[0xED7B]</c> pair.</param>
    /// <param name="prototypeSelector">The pushed <c>prototype[+0x0C] &amp; 0x40</c>.</param>
    /// <param name="variant">The pushed spawn-variant flag (the <c>rand8 &lt; 0x14</c> roll).</param>
    void AllocateAndActivate(
        EngagementNodeContext context,
        ushort ownerSlot,
        int arcAccumulator,
        byte prototypeSelector,
        bool variant);
}

/// <summary>
/// The acquisition state machine's TARGET-SELECTION cluster, which lives in
/// <c>combat_target_score_and_fire @image@0x07952</c> (766 B, C6's) and its two satellites.
/// </summary>
/// <remarks>
/// A seam because the cluster is C6's and because none of it is probed: C0's probe table has no
/// entry for <c>0x07952</c>, <c>0x07DBA</c> or <c>0x07DE6</c>, so a verification cannot oracle-feed
/// it either.  Every call is COUNTED, and the default implementation throws so an unfed call is
/// visible (<see cref="UnavailableTargetSelection"/>).
/// </remarks>
public interface ITargetSelection
{
    /// <summary>
    /// <c>combat_target_score_and_fire @image@0x07952</c> — pick a target for this engagement.
    /// </summary>
    /// <param name="context">The node context.</param>
    /// <param name="preferExisting">The original's <c>AL</c> (<c>[0xED65] == 1</c> at the state-0/1 door, and
    /// <c>prototype[+0x28] != 1 ? 1 : 0</c> at the state-2 re-scan door, <c>image@0x080CC</c>).</param>
    /// <param name="fireEnabled">The original's <c>DL</c> — 1 at the state-0/1 door, 0 at the state-2 one.</param>
    /// <param name="cooldown">
    /// The original's <c>BX</c> out-pointer (<c>lea bx,[bp-2]</c>): the routine writes a re-scan cooldown there,
    /// which the caller adds to <c>[0xF0D0]</c>.  It draws TWO random numbers into it (<c>image@0x079B3</c> /
    /// <c>image@0x07BE1</c>).
    /// </param>
    /// <returns>The original's <c>AX</c>: the selected pool object, or 0.</returns>
    ushort ScoreAndFire(
        EngagementNodeContext context, bool preferExisting, bool fireEnabled, out short cooldown);

    /// <summary>
    /// <c>combat_target_qualify_from_globals @image@0x07DBA</c> — the six-argument wrapper over the
    /// scoring cluster's qualifier (<c>image@0x08200</c> and <c>image@0x082F8</c>, both with
    /// <c>AL = 1</c>).
    /// </summary>
    /// <param name="context">The node context.</param>
    /// <param name="mode">The original's <c>AL</c>.</param>
    /// <returns>The original's <c>AL</c>.</returns>
    bool QualifyFromGlobals(EngagementNodeContext context, byte mode);

    /// <summary>
    /// <c>target_sight_line_check @image@0x07DE6</c> — the state-2 candidate loop's line-of-sight
    /// test (<c>image@0x08154</c>).
    /// </summary>
    /// <param name="context">The node context.</param>
    /// <param name="targetRef">The pushed <c>[0xED6F]</c>.</param>
    /// <param name="weaponBlockRef">The pushed <c>SI + 0x28</c>.</param>
    /// <param name="ownerSlot">The pushed <c>[0xED56]</c>.</param>
    /// <returns>The original's <c>AL</c>.</returns>
    bool SightLine(
        EngagementNodeContext context, ushort targetRef, ushort weaponBlockRef, ushort ownerSlot);

    /// <summary>
    /// <c>weapon_guidance_angle_track @image@0x08440</c> — STATE 4's aim solution
    /// (<c>image@0x08270</c>), which writes the caller's <c>[bp-0x16]</c> and <c>[bp-0x14]</c>.
    /// </summary>
    /// <param name="context">The node context.</param>
    /// <param name="heading">The heading word the routine leaves in <c>[bp-0x16]</c>.</param>
    /// <param name="elevation">The elevation word it leaves in <c>[bp-0x14]</c>.</param>
    void GuidanceAngles(EngagementNodeContext context, out short heading, out short elevation);

    /// <summary>
    /// <c>weapon_fire_spawn_record_fill @image@0x0391C</c> — STATE 4's parameter-block fill
    /// (<c>image@0x0829A</c>).
    /// </summary>
    /// <param name="context">The node context.</param>
    /// <param name="parameters">
    /// The original's <c>DI</c> = the pushed <c>[bp+4]</c> — the caller's twelve-byte block at
    /// <c>[bp-0x12]</c>, which the routine overwrites with a position triple
    /// (<c>rep movsw cx=6</c> @<c>image@0x039BD</c>).
    /// </param>
    /// <param name="leadSource">
    /// The original's <c>AX</c> — <c>[0xED54] + 0x1A + 3·[0xED64]</c>, the three-byte per-slot lead
    /// descriptor inside the class prototype (<c>image@0x08285</c>).
    /// </param>
    /// <param name="targetRef">The original's <c>BX</c> — <c>[0xED56]</c>.</param>
    /// <param name="weaponClassRef">The original's <c>DX</c> — <c>SI</c>.</param>
    /// <param name="invertLead">The pushed word: <c>slotAmmo[i] &amp; 1</c> (<c>image@0x0827D</c>).</param>
    void FillSpawnRecord(
        EngagementNodeContext context,
        ref CombatPosition parameters,
        ushort leadSource,
        ushort targetRef,
        ushort weaponClassRef,
        bool invertLead);
}

/// <summary>The tripwire target-selection seam: every entry point throws, naming the owner.</summary>
public sealed class UnavailableTargetSelection : ITargetSelection
{
    /// <summary>The shared instance.</summary>
    public static UnavailableTargetSelection Instance { get; } = new();

    /// <inheritdoc/>
    public ushort ScoreAndFire(
        EngagementNodeContext context, bool preferExisting, bool fireEnabled, out short cooldown) =>
        throw Unavailable("combat_target_score_and_fire @image@0x07952", "C6");

    /// <inheritdoc/>
    public bool QualifyFromGlobals(EngagementNodeContext context, byte mode) =>
        throw Unavailable("combat_target_qualify_from_globals @image@0x07DBA", "C6");

    /// <inheritdoc/>
    public bool SightLine(
        EngagementNodeContext context, ushort targetRef, ushort weaponBlockRef, ushort ownerSlot) =>
        throw Unavailable("target_sight_line_check @image@0x07DE6", "C6");

    /// <inheritdoc/>
    public void GuidanceAngles(EngagementNodeContext context, out short heading, out short elevation) =>
        throw Unavailable("weapon_guidance_angle_track @image@0x08440", "C6");

    /// <inheritdoc/>
    public void FillSpawnRecord(
        EngagementNodeContext context,
        ref CombatPosition parameters,
        ushort leadSource,
        ushort targetRef,
        ushort weaponClassRef,
        bool invertLead) =>
        throw Unavailable("weapon_fire_spawn_record_fill @image@0x0391C", "C6");

    private static EngagementNodeSeamException Unavailable(string function, string owner) =>
        new($"{function} is a {owner} seam that this run did not wire up.");
}

/// <summary>
/// Everything the per-node FSM sends OUT of the simulation: sounds, advisories, film records and
/// the visual-effect allocators.  None of them feeds a value back into the kernel.
/// </summary>
/// <remarks>
/// Two of them DO write INT-adjacent tables the port models elsewhere and are listed here anyway
/// because the FSM does not own those tables: <see cref="AllocateSmokeSlot"/> writes
/// <c>subsystem4x19_table [0xB84C]</c> through <c>smoke_slot_alloc_and_fill @image@0x0B0FC</c>, and
/// <see cref="ClearSubsystem4x19"/> clears rows of the same table.  C2's
/// <see cref="IProjectileEvents"/> makes the same split for the same reason.
/// </remarks>
public interface IEngagementNodeEvents
{
    /// <summary>
    /// <c>smoke_slot_alloc_and_fill @image@0x0B0FC</c> — case D's trail emitter
    /// (<c>lcall 0x108e:0xa81c</c> @<c>image@0x0424F</c>, three pushed words: the kind <c>3</c> and
    /// the far pointer <c>DS:0xED42</c>, i.e. the fire position).
    /// </summary>
    /// <param name="position">The fire position <c>[0xED42..0xED4D]</c>.</param>
    /// <param name="kind">The pushed selector — always <c>3</c> at this site.</param>
    void AllocateSmokeSlot(CombatPosition position, ushort kind);

    /// <summary>
    /// <c>sfx_play_tone0d @image@0x29B82</c> — case 9's altitude-clamp cue
    /// (<c>lcall 0x3981:0x372</c> @<c>image@0x0465C</c>), fired only when the engagement is the
    /// player's currently-selected view.
    /// </summary>
    void PlayAltitudeClampTone();

    /// <summary>
    /// <c>ai_advisor_message_dispatch @image@0x0EFF1</c> — the acquisition machine's radio call
    /// (<c>lcall 0x108e:0xe711</c> @<c>image@0x0825A</c>).
    /// </summary>
    /// <param name="selector">The <c>AL</c> byte: <c>4</c>, <c>7</c> or <c>8</c>.</param>
    void AdvisorMessage(byte selector);

    /// <summary>
    /// <c>engagement_intercept_briefing_format_and_show @image@0x220B9</c> — the "contact" banner a
    /// fresh acquisition raises (<c>lcall 0x31f4:0x179</c> @<c>image@0x08052</c>).
    /// </summary>
    /// <param name="targetRef">The target's pool near offset.</param>
    /// <param name="ownerSlot">The engagement's <c>[0xED56]</c>.</param>
    void ShowInterceptBriefing(ushort targetRef, ushort ownerSlot);

    /// <summary>
    /// <c>sfx_selected_object_audio_dispatch @image@0x29AA8</c> — the AI's own gun/launch sound
    /// (<c>lcall 0x3981:0x298</c> @<c>image@0x083D8</c>).
    /// </summary>
    /// <param name="weaponClassRef">The original's <c>AX</c> — the firing weapon class.</param>
    /// <param name="ownerSlot">The original's <c>BX</c> — <c>[0xED56]</c>.</param>
    void PlayWeaponFireSound(ushort weaponClassRef, ushort ownerSlot);

    /// <summary>
    /// <c>film_obj_departure_record @image@0x30C46</c> — the film DEPART record
    /// <c>engagement_slot_impact_and_depart</c>'s prologue writes
    /// (<c>lcall 0x401c:0xa86</c> @<c>image@0x08B8B</c>).
    /// </summary>
    /// <param name="slotReference">The departing engagement's <c>[0xED56]</c>.</param>
    void RecordDeparture(ushort slotReference);

    /// <summary>
    /// <c>slot_4x19_clear_for_owner @image@0x0B582</c> — drop the departing object's trail rows
    /// (<c>lcall 0x108e:0xaca2</c> @<c>image@0x08B94</c>).
    /// </summary>
    /// <param name="slotReference">The departing engagement's <c>[0xED56]</c>.</param>
    void ClearSubsystem4x19(ushort slotReference);

    /// <summary>
    /// <c>sfx_play_tone16_random @image@0x29AF2</c>'s door (corrected —
    /// <c>0x29AE2</c> is not a function entry; the callee of record is <c>0x3981:0x02E2</c> =
    /// <c>image@0x29AF2</c>) — the departure cue, fired only when the departing engagement is the
    /// player's selected view (<c>lcall 0x3981:0x2e2</c> @<c>image@0x08BA0</c>).
    /// </summary>
    void PlayDepartureTone();

    /// <summary>
    /// The impact/depart effect chain <c>engagement_slot_impact_and_depart @image@0x08BA6</c> runs
    /// after its bookkeeping: <c>lcall 0x3d85:0x319</c> (<c>image@0x08BB9</c>),
    /// <c>lcall 0x108e:0x319c</c> (<c>image@0x08BDD</c>) and <c>lcall 0x3981:0x393</c>
    /// (<c>image@0x08BE9</c>) — a ground-mark, a deferred effect and a sound.
    /// </summary>
    /// <param name="position">The fire position the three calls share.</param>
    void ImpactEffects(CombatPosition position);

    /// <summary>
    /// The spawn-init effect chain <c>enemy_spawn_with_angle_pos_init</c> runs on the
    /// <c>[0xED8C]</c> deferred branch (<c>image@0x08B41</c> and <c>image@0x08B4D</c>).
    /// </summary>
    /// <param name="position">The fire position.</param>
    void SpawnEffects(CombatPosition position);
}

/// <summary>Every per-node output event, discarded.</summary>
public sealed class NullEngagementNodeEvents : IEngagementNodeEvents
{
    /// <summary>The shared instance.</summary>
    public static NullEngagementNodeEvents Instance { get; } = new();

    /// <inheritdoc/>
    public void AllocateSmokeSlot(CombatPosition position, ushort kind)
    {
    }

    /// <inheritdoc/>
    public void PlayAltitudeClampTone()
    {
    }

    /// <inheritdoc/>
    public void AdvisorMessage(byte selector)
    {
    }

    /// <inheritdoc/>
    public void ShowInterceptBriefing(ushort targetRef, ushort ownerSlot)
    {
    }

    /// <inheritdoc/>
    public void PlayWeaponFireSound(ushort weaponClassRef, ushort ownerSlot)
    {
    }

    /// <inheritdoc/>
    public void RecordDeparture(ushort slotReference)
    {
    }

    /// <inheritdoc/>
    public void ClearSubsystem4x19(ushort slotReference)
    {
    }

    /// <inheritdoc/>
    public void PlayDepartureTone()
    {
    }

    /// <inheritdoc/>
    public void ImpactEffects(CombatPosition position)
    {
    }

    /// <inheritdoc/>
    public void SpawnEffects(CombatPosition position)
    {
    }
}

/// <summary>
/// Everything one <see cref="EngagementNodePass.Run"/> operates on: C3a's geometry context (the
/// register file, the arena, the constant tables and the geometry seams) plus the seven callees the
/// FSM makes that C3b does not own.
/// </summary>
/// <remarks>
/// C3a's <see cref="EngagementGeometryContext"/> is <c>sealed</c>, so this NESTS it rather than
/// deriving — the rule: extend or nest, never fork.  Every geometry call the FSM makes
/// passes <see cref="Geometry"/> straight through, so the two halves share one register file, one
/// arena and one census.
/// </remarks>
public sealed class EngagementNodeContext
{
    /// <summary>C3a's context — the shared register file, arena, static data and geometry seams.</summary>
    public required EngagementGeometryContext Geometry { get; init; }

    /// <summary>The Sim LFSR.  By law it advances <c>[0x07A8]</c> inside the register file.</summary>
    public required ICombatRandom Random { get; init; }

    /// <summary>The bytecode interpreter (C4).</summary>
    public IEngagementScriptInterpreter Interpreter { get; init; } =
        UnavailableScriptInterpreter.Instance;

    /// <summary>The acquisition state machine — the real one by default.</summary>
    public IAcquisitionStateMachine Acquisition { get; init; } = EnemyTargetAcquisition.Seam;

    /// <summary>The target-selection cluster (C6).</summary>
    public ITargetSelection TargetSelection { get; init; } = UnavailableTargetSelection.Instance;

    /// <summary>The world-grid query (C2b) the acquisition machine's shared tail makes.</summary>
    public ITargetAcquisition? WorldGrid { get; init; }

    /// <summary>The spawn-slot allocator.</summary>
    public ISpawnSlotAllocator? SpawnAllocator { get; init; }

    /// <summary>The destruction/debris slot pool (C5).</summary>
    public IObjectSlotPool? ObjectSlots { get; init; }

    /// <summary>The <c>.S</c> mission module's <c>on_slot_destroyed</c> hook.</summary>
    public IEngagementNodeMissionHook? MissionHook { get; init; }

    /// <summary>The outbound notifications.</summary>
    public IEngagementNodeEvents Events { get; init; } = NullEngagementNodeEvents.Instance;

    /// <summary>The per-arm census this run fills.</summary>
    public EngagementNodeCensus Census { get; } = new();

    /// <summary>
    /// The PLAY-PROFILE cap on a node's sleep, in master-counter SECONDS; <c>-1</c> (the default) is
    /// the original's law.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The original re-files a node at <c>[0xF0C8] + [0xEDAC]</c> (<c>image@0x0489B</c>), and
    /// <c>[0xF0C8]</c> counts SECONDS; the manoeuvring arms ask for 2–4 (<c>image@0x041F1</c>,
    /// <c>0x04A20</c>, <c>0x0498A</c>), the idle arm for the whole remaining script delay
    /// (<c>image@0x041DE</c>).  An AI aircraft is only MOVED when its node runs, so a cruising or
    /// tracking bandit holds one position for those seconds and then jumps (measured: 101–102 held
    /// steps, 778–939 ft).
    /// </para>
    /// <para>
    /// <b>The choice:</b> cap the sleep rather than re-time the AI.  What the cap changes
    /// and what it does not, from the bytes: the SCRIPT runs only when its own absolute deadline
    /// <c>[0xED62]</c> is reached (<c>image@0x0519D</c>), the ACQUISITION machine only when its own
    /// quarter-second timer <c>[0xED6B]</c> is due (<c>image@0x07F9B</c>), and no arm of the
    /// per-node FSM outside those two draws a random number — so an extra pass integrates the
    /// manoeuvring engine (rate × dt, linear) and the fire position, and rolls no extra dice.  The
    /// only arm that does run on every pass regardless is the VM's mode-1 sentinel path (<c>[0xED59]
    /// &amp; 4</c> with <c>[0xED62] == 0xFFFE</c>, <c>image@0x04816</c>); the census counts it so a
    /// change there is visible.  Not byte-exact for AI positions between the original's wakes; the
    /// recorded replays run with the original law (<c>-1</c>).
    /// </para>
    /// <para>
    /// The unit is the second: <c>0</c> = every frame, <c>1</c> = at most one second.  There is no
    /// sub-second sleep in this scheduler; a finer motion cadence is the "commit-and-fly" option the
    /// ledger records as the later revamp.
    /// </para>
    /// </remarks>
    public int SleepCapSeconds { get; set; } = -1;

    /// <summary>The register file.</summary>
    public CombatRegisters Registers => Geometry.Registers;

    /// <summary>The pool arena.</summary>
    public PoolArena Arena => Geometry.Arena;

    /// <summary>The constant DGROUP regions.</summary>
    public ICombatStaticData StaticData => Geometry.StaticData;

    /// <summary>The named view of the register file C3a built.</summary>
    public EngagementAngleView View => Geometry.View;
}

/// <summary>The tripwire interpreter seam.</summary>
public sealed class UnavailableScriptInterpreter : IEngagementScriptInterpreter
{
    /// <summary>The shared instance.</summary>
    public static UnavailableScriptInterpreter Instance { get; } = new();

    /// <inheritdoc/>
    public void Run(CombatRegisters registers, PoolArena arena, byte mode) =>
        throw new EngagementNodeSeamException(
            $"engagement_script_interpreter @image@0x05154 was entered with AL = {mode}; it is C4's "
                + "5,319-byte bytecode VM and this run did not wire the seam up.");
}

/// <summary>
/// Which arms one per-node FSM pass reached.  Instrumentation only: the kernel never reads it back,
/// and the verification prints it so an arm no recording reaches is visible rather than
/// silently unproven.
/// </summary>
public sealed class EngagementNodeCensus
{
    /// <summary>How many passes ran.</summary>
    public long Passes { get; set; }

    /// <summary>The prologue's arc-parameter gate was OPEN — <c>prototype[+0x0D] &amp; 2</c> set.</summary>
    public long PrologueArcParams { get; set; }

    /// <summary>RÉGIME ARM (i): the prologue ELSE arm <c>image@0x041AC</c>.</summary>
    public long PrologueElse { get; set; }

    /// <summary>The <c>[0xEDE4]</c> latch saw phase <c>0x0C</c> (<c>image@0x041BF</c>).</summary>
    public long PhaseIsC { get; set; }

    /// <summary>Dispatches per phase 0..0x0D, plus index 14 for the out-of-range reset arm.</summary>
    public long[] Phase { get; } = new long[15];

    /// <summary>How many switch dispatches happened — case A can re-enter (<c>image@0x04471</c>).</summary>
    public long Dispatches { get; set; }

    /// <summary>Case 0: the countdown was still running (<c>image@0x041DB</c>).</summary>
    public long Case0Counting { get; set; }

    /// <summary>Case 0: the countdown had expired (<c>image@0x041E4</c>).</summary>
    public long Case0Expired { get; set; }

    /// <summary>Case 2: the far-range arc heading was chosen (<c>image@0x04274</c>).</summary>
    public long Case2FarArc { get; set; }

    /// <summary>Case 2: the near-range arc heading (<c>image@0x0427A</c>).</summary>
    public long Case2NearArc { get; set; }

    /// <summary>Case 2: the evasion setup latched <c>[0xED59]</c> bit2 and the arm exited early.</summary>
    public long Case2EarlyExit { get; set; }

    /// <summary>Case 2: the octant window accepted and <c>[0xED59]</c> bit2 was set.</summary>
    public long Case2OctantHit { get; set; }

    /// <summary>Case 2: the octant window rejected.</summary>
    public long Case2OctantMiss { get; set; }

    /// <summary>Case D: the smoke emitter fired (<c>image@0x0424F</c>).</summary>
    public long CaseDSmoke { get; set; }

    /// <summary>Case 6: the spawn init reported "no object left" and the arm departed the slot.</summary>
    public long Case6Depart { get; set; }

    /// <summary>Case 6: <c>[0xED3E]</c> bit0 was clear, so the arm fell straight into §F1.</summary>
    public long Case6NotArmed { get; set; }

    /// <summary>Case 6: the altitude test kept the engagement alive.</summary>
    public long Case6Alive { get; set; }

    /// <summary>Case 7: the <c>0x7D0</c> range gate latched <c>[0xED59]</c> bit2.</summary>
    public long Case7RangeLatch { get; set; }

    /// <summary>Case 8: the elevation nudge arm ran (<c>image@0x0452D</c>).</summary>
    public long Case8Nudge { get; set; }

    /// <summary>Case 9: the "above the target" branch (<c>image@0x0456C</c>).</summary>
    public long Case9Above { get; set; }

    /// <summary>Case 9: the "climb to the ceiling" branch (<c>image@0x045B0</c>).</summary>
    public long Case9Climb { get; set; }

    /// <summary>Case 9: the "level off" branch (<c>image@0x045C2</c>).</summary>
    public long Case9Level { get; set; }

    /// <summary>Case 9: the altitude floor was applied and the cue fired.</summary>
    public long Case9FloorCue { get; set; }

    /// <summary>Case A: the interpreter fallback (<c>image@0x04466</c>) re-entered the switch.</summary>
    public long CaseAInterpreter { get; set; }

    /// <summary>Case A: the pose-copy arm (<c>image@0x04474</c>).</summary>
    public long CaseAPose { get; set; }

    /// <summary>Case B: the only arm — the AL=1 slot-angle update.</summary>
    public long CaseBAngleUpdate { get; set; }

    /// <summary>The PC chain's <c>DX &lt; 2</c> bias arm (<c>image@0x04309</c>).</summary>
    public long PcBiasArm { get; set; }

    /// <summary>Its Q8.8 accumulate arm (<c>image@0x04326</c>).</summary>
    public long PcAccumulateArm { get; set; }

    /// <summary>The PC chain's <c>0xA00</c> altitude clamp (<c>image@0x0436F</c>).</summary>
    public long PcAltitudeClamp { get; set; }

    /// <summary>The PC chain's <c>[0xB538]</c> mid-arc rate arm (<c>image@0x043BC</c>).</summary>
    public long PcMidArcRate { get; set; }

    /// <summary>Its <c>[0xB53A]</c> fixed-rate arm (<c>image@0x043CF</c>).</summary>
    public long PcFixedRate { get; set; }

    /// <summary>Its plain arc-heading rate arm (<c>image@0x043D6</c>).</summary>
    public long PcHeadingRate { get; set; }

    /// <summary>The PC chain's early exit into §F1 (<c>image@0x043EC</c>).</summary>
    public long PcEarlyExit { get; set; }

    /// <summary>The PC chain's close-range latch (<c>image@0x043F8</c>).</summary>
    public long PcCloseRange { get; set; }

    /// <summary>The reset arm (<c>image@0x04736</c>).</summary>
    public long ResetArm { get; set; }

    /// <summary>§F1's Z clamp (<c>image@0x04775</c>).</summary>
    public long ClampZ { get; set; }

    /// <summary>§F1's arc-accumulator clamp (<c>image@0x04784</c>).</summary>
    public long ClampArc { get; set; }

    /// <summary>§F1's altitude cap (<c>image@0x047A2</c>).</summary>
    public long AltitudeCap { get; set; }

    /// <summary>§F1's attribute-table bit4 skip (<c>image@0x04797</c>).</summary>
    public long AltitudeCapSkipped { get; set; }

    /// <summary>The close-range proximity kill check fired the VM (<c>image@0x087CB</c>).</summary>
    public long CloseRangeKill { get; set; }

    /// <summary>§F2 ran the acquisition state machine (<c>image@0x047D6</c>).</summary>
    public long AcqFsmRan { get; set; }

    /// <summary>RÉGIME ARM (ii): §F2's skip-acq else arm (<c>image@0x047DC</c>).</summary>
    public long SkipAcqArm { get; set; }

    /// <summary>The skip-acq arm's <c>&gt;= 2</c> path, which clears the acquisition (<c>image@0x047E8</c>).</summary>
    public long SkipAcqCleared { get; set; }

    /// <summary>§F3's four shot classes 2 / 0 / 1 / 8, then "no interpreter call".</summary>
    public long[] ShotClass { get; } = new long[5];

    /// <summary>§F4's low-altitude spawn gate passed all five conjuncts (<c>image@0x04863</c>).</summary>
    public long LowAltitudeSpawn { get; set; }

    /// <summary>§F5 took the schedule-adjust arm (<c>image@0x04898</c>).</summary>
    public long ScheduleAdjusted { get; set; }

    /// <summary>re-files whose sleep the play-profile cap shortened
    /// (<see cref="EngagementNodeContext.SleepCapSeconds"/>).</summary>
    public long SleepCapped { get; set; }

    /// <summary>§F5 clamped the next schedule at <c>0x1FFC</c> (<c>image@0x048AC</c>).</summary>
    public long ScheduleClamped { get; set; }

    /// <summary>Acquisition machine: the <c>[0xED64] == 0xFF</c> early return (<c>image@0x07F8B</c>).</summary>
    public long AcqIdleReturn { get; set; }

    /// <summary>Acquisition machine: the frame gate held it off (<c>image@0x07FA1</c>).</summary>
    public long AcqFrameGate { get; set; }

    /// <summary>Acquisition machine: the eligibility check dropped the current target.</summary>
    public long AcqTargetDropped { get; set; }

    /// <summary>Acquisition machine dispatches per state 0..4, plus index 5 for "out of range".</summary>
    public long[] AcqState { get; } = new long[6];

    /// <summary>Acquisition machine: a fresh target was selected (<c>image@0x08004</c>).</summary>
    public long AcqTargetSelected { get; set; }

    /// <summary>Acquisition machine: the selector found nothing (<c>image@0x0805E</c>).</summary>
    public long AcqNoTarget { get; set; }

    /// <summary>Acquisition machine: state 2's candidate loop picked a slot on the score-3 path.</summary>
    public long AcqSlotPerfect { get; set; }

    /// <summary>Acquisition machine: it picked a slot on the best-score path.</summary>
    public long AcqSlotScored { get; set; }

    /// <summary>Acquisition machine: the candidate loop rejected the random filter (<c>image@0x081CB</c>).</summary>
    public long AcqRandomFilterRejected { get; set; }

    /// <summary>Acquisition machine: state 4 fired a shot (<c>image@0x083CB</c> returned true).</summary>
    public long AcqShotFired { get; set; }

    /// <summary>Acquisition machine: the skill roll said MISS (<c>image@0x083AD</c>).</summary>
    public long AcqSkillMiss { get; set; }

    /// <summary>Evasion setup: the proximity/Manhattan early arm (<c>image@0x04979</c>).</summary>
    public long EvasionProximityArm { get; set; }

    /// <summary>Evasion setup: the "hold altitude" arm inside it (<c>image@0x04980</c>).</summary>
    public long EvasionHoldAltitude { get; set; }

    /// <summary>Evasion setup: the altitude delta was inside <c>0xA00</c>, so elevation went to 0.</summary>
    public long EvasionZeroElevation { get; set; }

    /// <summary>Evasion setup: the 3-D elevation arm (<c>image@0x049EE</c>).</summary>
    public long EvasionElevationArm { get; set; }

    /// <summary>Spawn init: the "coded angles" arm (<c>image@0x089CD</c>).</summary>
    public long SpawnCodedAngles { get; set; }

    /// <summary>Spawn init: the "default sweep" arm (<c>image@0x08A22</c>).</summary>
    public long SpawnDefaultSweep { get; set; }

    /// <summary>Spawn init: the time gate opened and a slot was allocated (<c>image@0x08AF9</c>).</summary>
    public long SpawnAllocated { get; set; }

    /// <summary>Spawn init: the second-phase deferred effect fired (<c>image@0x08B1C</c>).</summary>
    public long SpawnDeferredEffect { get; set; }

    /// <summary>Kill tally: the enemy counter <c>[0xF106]</c> was bumped (<c>image@0x0873F</c>).</summary>
    public long KillTallyEnemy { get; set; }

    /// <summary>Kill tally: the friendly counter <c>[0xF102]</c> was bumped (<c>image@0x08746</c>).</summary>
    public long KillTallyFriendly { get; set; }

    /// <summary>Adds another census into this one.</summary>
    /// <param name="other">The census to fold in.</param>
    public void Add(EngagementNodeCensus other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Passes += other.Passes;
        PrologueArcParams += other.PrologueArcParams;
        PrologueElse += other.PrologueElse;
        PhaseIsC += other.PhaseIsC;
        for (int i = 0; i < Phase.Length; i++)
        {
            Phase[i] += other.Phase[i];
        }

        Dispatches += other.Dispatches;
        Case0Counting += other.Case0Counting;
        Case0Expired += other.Case0Expired;
        Case2FarArc += other.Case2FarArc;
        Case2NearArc += other.Case2NearArc;
        Case2EarlyExit += other.Case2EarlyExit;
        Case2OctantHit += other.Case2OctantHit;
        Case2OctantMiss += other.Case2OctantMiss;
        CaseDSmoke += other.CaseDSmoke;
        Case6Depart += other.Case6Depart;
        Case6NotArmed += other.Case6NotArmed;
        Case6Alive += other.Case6Alive;
        Case7RangeLatch += other.Case7RangeLatch;
        Case8Nudge += other.Case8Nudge;
        Case9Above += other.Case9Above;
        Case9Climb += other.Case9Climb;
        Case9Level += other.Case9Level;
        Case9FloorCue += other.Case9FloorCue;
        CaseAInterpreter += other.CaseAInterpreter;
        CaseAPose += other.CaseAPose;
        CaseBAngleUpdate += other.CaseBAngleUpdate;
        PcBiasArm += other.PcBiasArm;
        PcAccumulateArm += other.PcAccumulateArm;
        PcAltitudeClamp += other.PcAltitudeClamp;
        PcMidArcRate += other.PcMidArcRate;
        PcFixedRate += other.PcFixedRate;
        PcHeadingRate += other.PcHeadingRate;
        PcEarlyExit += other.PcEarlyExit;
        PcCloseRange += other.PcCloseRange;
        ResetArm += other.ResetArm;
        ClampZ += other.ClampZ;
        ClampArc += other.ClampArc;
        AltitudeCap += other.AltitudeCap;
        AltitudeCapSkipped += other.AltitudeCapSkipped;
        CloseRangeKill += other.CloseRangeKill;
        AcqFsmRan += other.AcqFsmRan;
        SkipAcqArm += other.SkipAcqArm;
        SkipAcqCleared += other.SkipAcqCleared;
        for (int i = 0; i < ShotClass.Length; i++)
        {
            ShotClass[i] += other.ShotClass[i];
        }

        LowAltitudeSpawn += other.LowAltitudeSpawn;
        ScheduleAdjusted += other.ScheduleAdjusted;
        SleepCapped += other.SleepCapped;
        ScheduleClamped += other.ScheduleClamped;
        AcqIdleReturn += other.AcqIdleReturn;
        AcqFrameGate += other.AcqFrameGate;
        AcqTargetDropped += other.AcqTargetDropped;
        for (int i = 0; i < AcqState.Length; i++)
        {
            AcqState[i] += other.AcqState[i];
        }

        AcqTargetSelected += other.AcqTargetSelected;
        AcqNoTarget += other.AcqNoTarget;
        AcqSlotPerfect += other.AcqSlotPerfect;
        AcqSlotScored += other.AcqSlotScored;
        AcqRandomFilterRejected += other.AcqRandomFilterRejected;
        AcqShotFired += other.AcqShotFired;
        AcqSkillMiss += other.AcqSkillMiss;
        EvasionProximityArm += other.EvasionProximityArm;
        EvasionHoldAltitude += other.EvasionHoldAltitude;
        EvasionZeroElevation += other.EvasionZeroElevation;
        EvasionElevationArm += other.EvasionElevationArm;
        SpawnCodedAngles += other.SpawnCodedAngles;
        SpawnDefaultSweep += other.SpawnDefaultSweep;
        SpawnAllocated += other.SpawnAllocated;
        SpawnDeferredEffect += other.SpawnDeferredEffect;
        KillTallyEnemy += other.KillTallyEnemy;
        KillTallyFriendly += other.KillTallyFriendly;
    }

    /// <summary>The census as report lines, one arm per entry, zero-valued arms included.</summary>
    /// <returns>Name/count pairs in report order.</returns>
    public IEnumerable<(string Arm, long Count)> Lines()
    {
        yield return ("passes", Passes);
        yield return ("prologue arc-params (gate open)", PrologueArcParams);
        yield return ("prologue ELSE arm (regime i)", PrologueElse);
        yield return ("[0xEDE4] latch: phase == 0xC", PhaseIsC);
        for (int i = 0; i < 14; i++)
        {
            yield return ($"phase 0x{i:X}", Phase[i]);
        }

        yield return ("phase > 0xD (reset via bound test)", Phase[14]);
        yield return ("switch dispatches", Dispatches);
        yield return ("case 0 counting", Case0Counting);
        yield return ("case 0 expired", Case0Expired);
        yield return ("case 2 far arc", Case2FarArc);
        yield return ("case 2 near arc", Case2NearArc);
        yield return ("case 2 early exit", Case2EarlyExit);
        yield return ("case 2 octant HIT", Case2OctantHit);
        yield return ("case 2 octant MISS", Case2OctantMiss);
        yield return ("case D smoke emitted", CaseDSmoke);
        yield return ("case 6 not armed", Case6NotArmed);
        yield return ("case 6 alive", Case6Alive);
        yield return ("case 6 depart", Case6Depart);
        yield return ("case 7 range latch", Case7RangeLatch);
        yield return ("case 8 elevation nudge", Case8Nudge);
        yield return ("case 9 above", Case9Above);
        yield return ("case 9 climb", Case9Climb);
        yield return ("case 9 level", Case9Level);
        yield return ("case 9 floor cue", Case9FloorCue);
        yield return ("case A interpreter fallback", CaseAInterpreter);
        yield return ("case A pose copy", CaseAPose);
        yield return ("case B angle update", CaseBAngleUpdate);
        yield return ("PC bias arm", PcBiasArm);
        yield return ("PC accumulate arm", PcAccumulateArm);
        yield return ("PC 0xA00 altitude clamp", PcAltitudeClamp);
        yield return ("PC mid-arc rate", PcMidArcRate);
        yield return ("PC fixed rate 0xA628", PcFixedRate);
        yield return ("PC heading rate", PcHeadingRate);
        yield return ("PC early exit", PcEarlyExit);
        yield return ("PC close-range latch", PcCloseRange);
        yield return ("reset arm", ResetArm);
        yield return ("F1 Z clamp", ClampZ);
        yield return ("F1 arc clamp", ClampArc);
        yield return ("F1 altitude cap", AltitudeCap);
        yield return ("F1 altitude cap SKIPPED (attr bit4)", AltitudeCapSkipped);
        yield return ("close-range kill", CloseRangeKill);
        yield return ("F2 acq FSM ran", AcqFsmRan);
        yield return ("F2 skip-acq arm (regime ii)", SkipAcqArm);
        yield return ("F2 skip-acq cleared", SkipAcqCleared);
        yield return ("F3 shot class 2", ShotClass[0]);
        yield return ("F3 shot class 0", ShotClass[1]);
        yield return ("F3 shot class 1", ShotClass[2]);
        yield return ("F3 shot class 8", ShotClass[3]);
        yield return ("F3 no interpreter call", ShotClass[4]);
        yield return ("F4 low-altitude spawn", LowAltitudeSpawn);
        yield return ("F5 schedule adjusted", ScheduleAdjusted);
        yield return ("F5 sleep capped (play profile)", SleepCapped);
        yield return ("F5 schedule clamped", ScheduleClamped);
        yield return ("acq idle return", AcqIdleReturn);
        yield return ("acq frame gate", AcqFrameGate);
        yield return ("acq target dropped", AcqTargetDropped);
        for (int i = 0; i < 5; i++)
        {
            yield return ($"acq state {i}", AcqState[i]);
        }

        yield return ("acq state out of range", AcqState[5]);
        yield return ("acq target selected", AcqTargetSelected);
        yield return ("acq no target", AcqNoTarget);
        yield return ("acq slot PERFECT (score 3)", AcqSlotPerfect);
        yield return ("acq slot scored", AcqSlotScored);
        yield return ("acq random filter rejected", AcqRandomFilterRejected);
        yield return ("acq shot fired", AcqShotFired);
        yield return ("acq skill roll MISS", AcqSkillMiss);
        yield return ("evasion proximity arm", EvasionProximityArm);
        yield return ("evasion hold altitude", EvasionHoldAltitude);
        yield return ("evasion zero elevation", EvasionZeroElevation);
        yield return ("evasion 3-D elevation arm", EvasionElevationArm);
        yield return ("spawn coded angles", SpawnCodedAngles);
        yield return ("spawn default sweep", SpawnDefaultSweep);
        yield return ("spawn allocated", SpawnAllocated);
        yield return ("spawn deferred effect", SpawnDeferredEffect);
        yield return ("kill tally ENEMY [0xF106]", KillTallyEnemy);
        yield return ("kill tally FRIENDLY [0xF102]", KillTallyFriendly);
    }
}
