namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// The mission-load-constant DGROUP data the projectile row reads that no trace window carries and
/// no port model yet owns: the weapon-class descriptor table at <c>0x1158</c>, the engagement class
/// prototypes at roughly <c>0x1812..0x24E8</c>, and the four-entry aircraft-type fire bonus at
/// <c>0x0DEC</c>.
/// </summary>
/// <remarks>
/// <para>
/// The kernel addresses all three the way the machine does — by NEAR OFFSET, because the pointers it
/// follows (<c>s_combat_spawn_record[+0x00]</c>, <c>s_engagement_state[+0x00]</c>) are literally
/// DGROUP offsets.  A typed model cannot stand in: <c>engagement_fire_authority_compute</c> reads
/// <c>prototype[+6 + weaponClass[+0]]</c> (<c>image@0x02AE3..0x02AEA</c>), an offset the weapon class
/// chooses at runtime.
/// </para>
/// <para>
/// LAW L1: this is a SEAM, never embedded data.  The port's runtime will build it from the
/// transformed open-format tree; a verification test builds it from the L1 image it locates by
/// walk-up, exactly as C1's <c>ImageEngagementPrototypes</c> does. <b>Transform ask (C2 → the
/// T-side):</b> the four bytes at DGROUP <c>0x0DEC</c> (<c>{0x00, 0x19, 0x40, 0x66}</c> in the
/// shipped image) are a tuning table with no home in the data tree yet.
/// </para>
/// <para>
/// C1's <see cref="IEngagementPrototypes"/> is a TYPED view of a subset of the same region.  The two
/// are deliberately not merged here (that would change a verified C1 interface); C2 proposes the
/// merge.
/// </para>
/// </remarks>
public interface ICombatStaticData
{
    /// <summary>One byte of the constant DGROUP region.</summary>
    /// <param name="dgroupOffset">The near offset, as the original's <c>DS:</c> operand.</param>
    byte Byte(int dgroupOffset);

    /// <summary>One little-endian word of the constant DGROUP region.</summary>
    /// <param name="dgroupOffset">The near offset.</param>
    ushort Word(int dgroupOffset);
}

/// <summary>
/// The thirteen-word argument frame <c>combat_object_tick</c> builds for
/// <c>world_grid_frustum_query_and_select @image@0x28540</c> (<c>image@0x0291D..0x02954</c>).
/// </summary>
/// <remarks>
/// Named in the order the original pushes them, which is the reverse of the callee's stack order. The four <c>push
/// ax</c>/<c>push cx</c> selectors carry REGISTER RESIDUE in their high bytes (<c>mov al,[bp-0x32]</c> after <c>mov
/// ax,0x100</c>, <c>mov cl,1</c> after <c>mov cl,8</c>), so only their LOW bytes are meaningful and only those are
/// modelled — a verification compares low bytes and says so. The two far pointers are given as their offset halves
/// plus the segment the caller used, because the port's arena is a single segment: <c>SelfPositionRef</c> is the
/// projectile's own position triple in the pool segment, and <c>LocalPositionCopy</c> is the six-word <c>SS:</c> copy
/// the tick makes of it at <c>image@0x028A1</c> before the flight step moves the original.
/// </remarks>
/// <param name="ExcludeRef">The projectile's own pool object — excluded from the search.</param>
/// <param name="LocalPositionCopy">The six-word position copy (12 bytes) taken before the flight step.</param>
/// <param name="SelfPositionRef">The pool near offset of the projectile's live position triple (object + 6).</param>
/// <param name="HalfRange">The search half-extent, <c>selectionWindow &lt;&lt; 8</c> as a signed 32-bit value.</param>
/// <param name="RoutingMask">Always <c>0x0100</c> at this site (<c>image@0x02942</c>).</param>
/// <param name="VisMode">The self-team byte — 1 when the shot's owner is the player (<c>image@0x02946</c>).</param>
/// <param name="RangeGate">Always 1 at this site (<c>image@0x0294A</c>).</param>
/// <param name="SubGate">The same byte as <see cref="VisMode"/> (<c>image@0x0294D</c>).</param>
/// <param name="LoopGate">
/// <c>neg(sbb(al, al))</c> after <c>cmp al,cl</c> — i.e. 0 when the self-team byte is 1 and 1
/// otherwise (<c>image@0x0294E..0x02952</c>).
/// </param>
public readonly record struct GridQueryRequest(
    ushort ExcludeRef,
    CombatPosition LocalPositionCopy,
    ushort SelfPositionRef,
    int HalfRange,
    ushort RoutingMask,
    byte VisMode,
    byte RangeGate,
    byte SubGate,
    byte LoopGate);

/// <summary>
/// <c>world_grid_frustum_query_and_select @image@0x28540</c> — the spatial index query the
/// projectile's acquisition phase makes.
/// </summary>
/// <remarks>
/// The port's own implementation is C2b's job (the world grid, its two arenas and the frustum test).
/// Until it lands this is a seam, oracle-fed in verification from the P20 probe's EXIT record,
/// whose <c>regs.ax</c> IS the selected pool slot.
/// </remarks>
public interface ITargetAcquisition
{
    /// <summary>Runs the query.</summary>
    /// <param name="request">The original's thirteen-word frame.</param>
    /// <param name="selectionPoint">
    /// The six-word block the query fills through <c>OutPtrArg</c> — the caller's
    /// <c>ss:[bp-0x24]</c>, which the tick then hands to the damage resolver as the impact position.
    /// It reaches only output events on this row, so a verification seam may leave it default and
    /// say so; C2b's real implementation fills it.
    /// </param>
    /// <returns>The selected pool object's near offset; 0 = nothing, <c>0xFFFF</c> = the sentinel.</returns>
    ushort Query(in GridQueryRequest request, out CombatPosition selectionPoint);
}

/// <summary>
/// <c>engagement_slot_fsm_advance @image@0x04F64</c> — the engagement bytecode VM's mode entry.
/// </summary>
/// <remarks>
/// The damage resolver drives it twice: mode 4 on the NEW-ENGAGEMENT path (<c>image@0x0BF14</c>) and
/// mode 5 on the close-range kill path (<c>image@0x0C05C</c>).  An earlier pass measured exactly those two doors
/// and modes over the reference windows.  C4 implements it; verification feeds it from the P3 probe
/// pair.
/// </remarks>
public interface IEngagementVm
{
    /// <summary>Advances the VM for one engagement block.</summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="arena">The pool arena.</param>
    /// <param name="blockRef">The engagement block's pool near offset — the original's <c>BX</c>.</param>
    /// <param name="mode">The mode byte — the original's <c>AL</c>; 4 or 5 from this row.</param>
    void Advance(CombatRegisters registers, PoolArena arena, ushort blockRef, byte mode);
}

/// <summary>
/// <c>weapon_fire_combat_loop @image@0x0F748</c> — the PLAYER-damage roulette the resolver calls
/// when the victim is the player.
/// </summary>
/// <remarks>
/// Another part of the port owns it (K9's <c>AircraftDamage</c> already recomputes its master-write subset).  Verification
/// feeds it from the P16 probe pair — nine activations on the 173046 window.
/// </remarks>
public interface IPlayerDamage
{
    /// <summary>Applies <paramref name="damage"/> to the player.</summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="damage">The resolver's computed damage word (the original's single stack arg).</param>
    void Apply(CombatRegisters registers, short damage);
}

/// <summary>
/// The three engagement-lifecycle calls the resolver makes that belong to C5 and C6.
/// </summary>
public interface IEngagementLifecycle
{
    /// <summary>
    /// <c>engagement_player_contact_coalition_spawn @image@0x0BA20</c> (<c>image@0x0C092</c>) — the
    /// contact/coalition reaction to a hit.  P13 in the trace.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="arena">The pool arena.</param>
    /// <param name="victimRef">The victim's pool object.</param>
    /// <param name="attackerId">The shot's owner id — <c>spawn[+0x06]</c>.</param>
    void PlayerContactCoalitionSpawn(
        CombatRegisters registers, PoolArena arena, ushort victimRef, ushort attackerId);

    /// <summary>
    /// <c>combat_vtable_slot_fn2_dispatch @image@0x08C72</c> (<c>image@0x0C00B</c>) — the mission
    /// module's <c>on_slot_destroyed</c> hook, dispatched through <c>[0x0FB8]+0x02</c>.  P19.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="arena">The pool arena.</param>
    /// <param name="victimRef">The destroyed object's pool near offset.</param>
    void OnSlotDestroyed(CombatRegisters registers, PoolArena arena, ushort victimRef);

    /// <summary>
    /// <c>slot_find_by_ext_ptr_a @image@0x2C380</c> then, on a hit,
    /// <c>slot_deactivate_and_clear @image@0x2C3A3</c> (<c>image@0x0BEE0</c>/<c>0x0BEF5</c>) — the
    /// DECOY SHORT CIRCUIT: the struck object is looked up with
    /// <c>slot_find_by_ext_ptr_a @image@0x2C380</c> and, if found, deactivated with no damage dealt.
    /// </summary>
    /// <remarks>
    /// → <c>TryDestructionSlotShortCircuit</c>; it is NOT the countermeasure
    /// absorb — the real countermeasure effect is the spawn-record seduction <c>image@0x036B4</c> plus the
    /// scorer's decoy window, both in <c>Player/Countermeasures.cs</c>. Parent ruling — C6 §4.3 measured the
    /// lookup as <c>slot_find_by_ext_ptr_a @image@0x2C380</c> over the DESTRUCTION/DEBRIS
    /// <c>s_object_slot</c> pool <c>[0xBB4C]</c>, not a chaff/flare pool, so "Decoy" overstated the pool
    /// identity; the name now says exactly what the bytes test.
    /// </remarks>
    /// <param name="victimRef">The struck object's pool near offset.</param>
    /// <param name="guidedFlag">The resolver's <c>[bp-0x14]</c> byte, sign-extended by the original.</param>
    /// <returns><c>true</c> when a countermeasure slot was found and cleared.</returns>
    bool TryDestructionSlotShortCircuit(ushort victimRef, byte guidedFlag);
}

/// <summary>
/// The notifications the PROJECTILE row sends out of the simulation: film, effects, sound and
/// cockpit text.  None of them feeds a combat decision back in.
/// </summary>
/// <remarks>
/// Field class: every one of these writes only Fx/presentation state, EXCEPT
/// <see cref="ClearSubsystem4x19"/> and <see cref="ScheduleDeferredEffect"/>, which write the
/// <c>subsystem4x19_table [0xB84C]</c> and <c>deferred_effects_and_acq_flags [0xB520]</c> windows —
/// INT-adjacent tables that later rows read.  C2's verification masks and LISTS those two windows
///; modelling them is C5's/C6's.
/// </remarks>
public interface IProjectileEvents
{
    /// <summary>
    /// <c>deferred_effect_record_schedule @image@0x03A7C</c> — the expiry/impact visual effect.
    /// </summary>
    /// <remarks>
    /// Nine stack words at both sites, in push order: three selectors then the position as three
    /// <c>hi,lo</c> pairs.  The tick's expiry arm passes <c>(0, 1, 0)</c> and the projectile's own
    /// position (<c>image@0x026DE..0x02703</c>); the resolver's epilogue passes
    /// <c>(victim, lethalClass, killFinalized)</c> and the impact position
    /// (<c>image@0x0C09D..0x0C0C2</c>).
    /// </remarks>
    /// <param name="position">The effect's world position.</param>
    /// <param name="subject">The first selector word — the struck object, or 0.</param>
    /// <param name="kind">The second — 1 from the tick, the lethal-class flag from the resolver.</param>
    /// <param name="outcome">The third — 0 from the tick, the kill-finalized flag from the resolver.</param>
    void ScheduleDeferredEffect(CombatPosition position, ushort subject, byte kind, byte outcome);

    /// <summary>
    /// <c>sfx_object_impact_dispatch @image@0x29BA3</c> — the impact sound
    /// (<c>image@0x02715</c>, <c>image@0x0C0D6</c>).
    /// </summary>
    /// <param name="position">The far pointer the caller passes: the object's position triple.</param>
    /// <param name="kind">The <c>AL</c> byte — 1 from the tick, the lethal-class flag from the resolver.</param>
    void PlayImpactSound(CombatPosition position, byte kind);

    /// <summary>
    /// <c>film_obj_departure_record @image@0x30C46</c> — the film DEPART record
    /// (<c>image@0x02635</c>).
    /// </summary>
    /// <param name="objectRef">The departing object's pool near offset.</param>
    void RecordDeparture(ushort objectRef);

    /// <summary>
    /// <c>slot_4x19_clear_for_owner @image@0x0B582</c> — drop the object's 4x19 rows
    /// (<c>image@0x0263D</c>, <c>image@0x0C3F1</c>).
    /// </summary>
    /// <param name="objectRef">The departing object's pool near offset.</param>
    void ClearSubsystem4x19(ushort objectRef);

    /// <summary>
    /// <c>subsystem5x06_per_frame_slot_fire @image@0x2DB69</c> — the NEAR-MISS visual the resolver
    /// fires when the victim reference is <c>-1</c> (<c>image@0x0BDA6</c>).
    /// </summary>
    /// <param name="z">The Z pair the resolver pushes FIRST (<c>image@0x0BD97</c>).</param>
    /// <param name="x">The X pair it pushes second (<c>image@0x0BD9F</c>).</param>
    void NearMissEffect(int z, int x);

    /// <summary>
    /// The player-hit feedback cluster: <c>sfx_play_tone16_random @image@0x29AF2</c> +
    /// <c>timer_dz_x_threshold_update @image@0x2D1E6</c> for a heavy hit, or
    /// <c>sfx_play_tone15_random @image@0x29ACA</c> / <c>sfx_play_tone14_fallback @image@0x29ABD</c>
    /// + <c>timer_blink_b_thresh_advance @image@0x2D212</c> for a light one
    /// (<c>image@0x0BEAA..0x0BEDB</c>).
    /// </summary>
    /// <param name="damage">The damage word — the original branches at <c>0x32</c>.</param>
    /// <param name="guided">The resolver's <c>[bp-0x14]</c> byte.</param>
    void PlayerHitFeedback(short damage, byte guided);

    /// <summary>
    /// <c>ai_advisor_message_dispatch @image@0x0EFF1</c> — the radio kill call
    /// (<c>image@0x0BFCA</c>).
    /// </summary>
    /// <param name="selector">The <c>AL</c> byte the resolver computes from the block's flags bit6.</param>
    void AdvisorMessage(byte selector);

    /// <summary>
    /// <c>engagement_cockpit_advisory_text_show @image@0x22161</c> — the "target damaged" advisory
    /// (<c>image@0x0BFAB</c>).
    /// </summary>
    /// <param name="attackerId">The shot's owner id.</param>
    /// <param name="victimRef">The victim's pool near offset.</param>
    void AdvisoryText(ushort attackerId, ushort victimRef);

    /// <summary>
    /// <c>engagement_cockpit_radio_text_emit @image@0x22269</c> — the kill radio call
    /// (<c>image@0x0BFD5</c>).
    /// </summary>
    /// <param name="victimRef">The victim's pool near offset.</param>
    void RadioKillCall(ushort victimRef);

    /// <summary>
    /// <c>subsystem4x19_row_attach (ex-projectile_spawn) @image@0x0B467</c> — the trailing smoke/fire a damaged aircraft starts
    /// carrying (<c>image@0x0BF92</c>).
    /// </summary>
    /// <remarks>
    /// MEASURED: despite its scanner name it does NOT touch the combat spawn table. It walks
    /// <c>subsystem4x19_table [0xB84C..0xB897]</c> backwards in <c>0x19</c>-byte strides
    /// (<c>image@0x0B476..0x0B49F</c>) — the visual trail/attachment table — so it is an output event like the rest.
    /// Another part of the port owns the body; a rename is proposed for the scanner.
    /// </remarks>
    /// <param name="ownerRef">The victim's engagement block <c>+0x02</c> owner reference.</param>
    /// <param name="variant">1 or 2, chosen by the low bit of a random draw (<c>image@0x0BF6C</c>).</param>
    void SpawnDamageSmoke(ushort ownerRef, byte variant);
}

/// <summary>Every projectile-row output event, discarded.</summary>
public sealed class NullProjectileEvents : IProjectileEvents
{
    /// <summary>The shared instance.</summary>
    public static NullProjectileEvents Instance { get; } = new();

    /// <inheritdoc/>
    public void ScheduleDeferredEffect(CombatPosition position, ushort subject, byte kind, byte outcome)
    {
    }

    /// <inheritdoc/>
    public void PlayImpactSound(CombatPosition position, byte kind)
    {
    }

    /// <inheritdoc/>
    public void RecordDeparture(ushort objectRef)
    {
    }

    /// <inheritdoc/>
    public void ClearSubsystem4x19(ushort objectRef)
    {
    }

    /// <inheritdoc/>
    public void NearMissEffect(int z, int x)
    {
    }

    /// <inheritdoc/>
    public void PlayerHitFeedback(short damage, byte guided)
    {
    }

    /// <inheritdoc/>
    public void AdvisorMessage(byte selector)
    {
    }

    /// <inheritdoc/>
    public void AdvisoryText(ushort attackerId, ushort victimRef)
    {
    }

    /// <inheritdoc/>
    public void RadioKillCall(ushort victimRef)
    {
    }

    /// <inheritdoc/>
    public void SpawnDamageSmoke(ushort ownerRef, byte variant)
    {
    }
}

/// <summary>Why a damage resolution ended without applying damage.</summary>
public enum NoDamageReason
{
    /// <summary>The victim reference was 0 (<c>image@0x0BDB1</c>).</summary>
    NoVictim,

    /// <summary>The victim carries no engagement block (<c>image@0x0BDBA</c>).</summary>
    NoEngagementBlock,

    /// <summary>The victim's prototype has the <c>0xFF</c> never-engages sentinel (<c>image@0x0BDD8</c>).</summary>
    PrototypeSentinel,

    /// <summary>The rolled damage did not beat the armour (<c>image@0x0BE6B</c>).</summary>
    ArmourAbsorbed,

    /// <summary>The player was not firing this frame, or damage is suppressed (<c>image@0x0BE8D</c>).</summary>
    PlayerNotHittable,

    /// <summary>The shot was not firing this frame (<c>image@0x0BF2C</c>).</summary>
    NotFiringThisFrame,

    /// <summary>The victim's engagement block already had zero hit points (<c>image@0x0BF38</c>).</summary>
    AlreadyDead,
}

/// <summary>Which acquisition arm handed a shot to the damage resolver.</summary>
public enum FireRoute
{
    /// <summary>The query returned the <c>0xFFFF</c> sentinel (<c>image@0x02985</c>).</summary>
    Sentinel,

    /// <summary>The selected object carries no engagement block (<c>image@0x0298E</c>).</summary>
    NoEngagementBlock,

    /// <summary>The record's "firing this frame" bit was set (<c>image@0x029CF</c>).</summary>
    FiringThisFrame,

    /// <summary>The fall-through after the prototype's <c>+0x0C</c> bit4 gate (<c>image@0x02A05</c>).</summary>
    Fallthrough,
}

/// <summary>
/// An optional per-slot hook the driver calls around every <c>combat_object_tick</c>.
/// </summary>
/// <remarks>
/// Instrumentation only — the kernel never reads anything back through it.  It exists so a
/// verification can check the P0 probe's per-call oracle (the 27-byte spawn record at the tick's
/// entry and at its <c>ret</c>) instead of only the whole table at CS1 — verify per call, not just per frame.
/// </remarks>
public interface ICombatTickObserver
{
    /// <summary>Called with the record the driver is about to tick.</summary>
    /// <param name="record">The spawn record.</param>
    void BeforeTick(SpawnRecordRef record);

    /// <summary>Called with the same record after the tick returned.</summary>
    /// <param name="record">The spawn record.</param>
    void AfterTick(SpawnRecordRef record);

    /// <summary>
    /// Called at the top of every <c>engagement_slot_fire_handler</c> call with the three arguments
    /// that decide its behaviour, so a verification can check them against the P1 probe.
    /// </summary>
    /// <param name="record">The spawn record — <c>[bp+0x0E]</c>.</param>
    /// <param name="victimRef">The struck object — <c>[bp+0x0A]</c>.</param>
    /// <param name="firingThisFrame">The AL flag — <c>[bp+0x04]</c>.</param>
    void FireResolved(SpawnRecordRef record, ushort victimRef, bool firingThisFrame);
}

/// <summary>
/// Everything one <see cref="CombatSpawnDriver.Step"/> needs: the mutable combat state, the constant
/// tables, and the five callees the projectile row makes that C2 does not own.
/// </summary>
public sealed class ProjectileKernelContext
{
    /// <summary>The DGROUP combat register file — the spawn table and every global this row touches.</summary>
    public required CombatRegisters Registers { get; init; }

    /// <summary>The pool arena — every world object and engagement block.</summary>
    public required PoolArena Arena { get; init; }

    /// <summary>The constant DGROUP tables (weapon classes, prototypes, the fire bonus).</summary>
    public required ICombatStaticData StaticData { get; init; }

    /// <summary>The Sim LFSR — by law it advances <c>[0x07A8]</c> inside <see cref="Registers"/>.</summary>
    public required ICombatRandom Random { get; init; }

    /// <summary>The world-grid query (C2b).</summary>
    public required ITargetAcquisition Acquisition { get; init; }

    /// <summary>The engagement bytecode VM (C4).</summary>
    public required IEngagementVm Vm { get; init; }

    /// <summary>The player-damage roulette (C6).</summary>
    public required IPlayerDamage PlayerDamage { get; init; }

    /// <summary>The lifecycle calls.</summary>
    public required IEngagementLifecycle Lifecycle { get; init; }

    /// <summary>The outbound notifications.</summary>
    public required IProjectileEvents Events { get; init; }

    /// <summary>The per-slot verification hook; null in a normal run.</summary>
    public ICombatTickObserver? Observer { get; init; }

    /// <summary>The per-step arm census — always present, never a decision input.</summary>
    public ProjectileKernelCensus Census { get; } = new();
}

/// <summary>
/// Which arms one <see cref="CombatSpawnDriver.Step"/> reached.  Instrumentation only: the kernel
/// never reads it back, and the verification prints it so an arm no recording reaches is
/// visible rather than silently unproven.
/// </summary>
public sealed class ProjectileKernelCensus
{
    /// <summary>Slots visited by <c>combat_spawn_slot_count</c>'s backwards walk.</summary>
    public int SlotsVisited { get; internal set; }

    /// <summary>Slots that were active and therefore ticked.</summary>
    public int SlotsTicked { get; internal set; }

    /// <summary>Ticks that took the expiry arm (<c>image@0x026D6</c>).</summary>
    public int Expired { get; internal set; }

    /// <summary>Expiries whose class had bit0 set and fired the two-call effect departure.</summary>
    public int ExpiredWithEffect { get; internal set; }

    /// <summary>Ticks that entered the engagement-tracking phase (<c>image@0x0272B</c>).</summary>
    public int Tracking { get; internal set; }

    /// <summary>Tracking passes that ran the fire-eligibility check.</summary>
    public int EligibilityChecks { get; internal set; }

    /// <summary>Eligibility checks that reached the altitude-window tick.</summary>
    public int AltitudeWindowTicks { get; internal set; }

    /// <summary>Altitude-window ticks that got past all three gates and drew.</summary>
    public int AltitudeWindowDraws { get; internal set; }

    /// <summary>Tracking passes that reached <c>combat_engagement_state_update</c>.</summary>
    public int TrackingStateUpdates { get; internal set; }

    /// <summary>Calls into <c>combat_engagement_state_update</c> through BOTH of its doors.</summary>
    public int StateUpdateCalls { get; internal set; }

    /// <summary>Ticks that ran the angle step (<c>image@0x027BE</c> or the tracking-phase one).</summary>
    public int AngleSteps { get; internal set; }

    /// <summary>Angle steps that had no target and used the level-flight defaults.</summary>
    public int AngleStepsLevelFlight { get; internal set; }

    /// <summary>Ticks that ran the speed envelope's BOOST arm.</summary>
    public int EnvelopeBoost { get; internal set; }

    /// <summary>Ticks that ran the speed envelope's COAST arm.</summary>
    public int EnvelopeCoast { get; internal set; }

    /// <summary>Ticks whose class had no envelope at all.</summary>
    public int EnvelopeSkipped { get; internal set; }

    /// <summary>Ticks that reached the target-acquisition grid query.</summary>
    public int GridQueries { get; internal set; }

    /// <summary>Grid queries that returned a slot.</summary>
    public int Acquired { get; internal set; }

    /// <summary>Acquisitions that were already the current target (the early exit).</summary>
    public int SameTarget { get; internal set; }

    /// <summary>Acquisitions that ran the two-thunk team-tag XOR avoidance test.</summary>
    public int TeamTagTests { get; internal set; }

    /// <summary>Acquisitions the team-tag test rejected.</summary>
    public int TeamTagRejects { get; internal set; }

    /// <summary>Ticks where the acquisition phase saw the <c>0xFFFF</c> sentinel.</summary>
    public int SentinelTargets { get; internal set; }

    /// <summary>Ticks whose owner is the player (<c>[si+6] == [0x00C0]</c>).</summary>
    public int SelfTeam { get; internal set; }

    /// <summary>Ticks that took the <c>[0xE46D]</c> cheat acquisition bias.</summary>
    public int CheatBias { get; internal set; }

    /// <summary>Ticks whose selection window was open (<c>[si+0x10] &lt;= [0xF0C8]</c>).</summary>
    public int SelectionWindowOpen { get; internal set; }

    /// <summary>Calls into the damage resolver.</summary>
    public int FireHandlerCalls { get; internal set; }

    /// <summary>Resolver calls that took the NEAR-MISS arm (victim <c>-1</c>).</summary>
    public int NearMiss { get; internal set; }

    /// <summary>Resolver calls that fell through to the epilogue without resolving damage.</summary>
    public int NoDamage { get; internal set; }

    /// <summary>Per-reason breakdown of <see cref="NoDamage"/>, indexed by <see cref="NoDamageReason"/>.</summary>
    public int[] NoDamageReasons { get; } = new int[Enum.GetValues<NoDamageReason>().Length];

    /// <summary>Per-arm breakdown of the acquisition phase's four routes into the resolver.</summary>
    public int[] FireRoutes { get; } = new int[Enum.GetValues<FireRoute>().Length];

    /// <summary>Resolver calls that consumed a countermeasure decoy.</summary>
    public int CountermeasureHits { get; internal set; }

    /// <summary>Resolver calls that damaged the PLAYER.</summary>
    public int PlayerHits { get; internal set; }

    /// <summary>Resolver calls that damaged an enemy and it survived.</summary>
    public int EnemyHits { get; internal set; }

    /// <summary>Enemy hits that pushed the victim below half hit points and started smoke.</summary>
    public int SmokeStarts { get; internal set; }

    /// <summary>Resolver calls that KILLED the victim.</summary>
    public int Kills { get; internal set; }

    /// <summary>Kills that ran the close-range check and its VM mode 5.</summary>
    public int CloseRangeKills { get; internal set; }

    /// <summary>Kills that ran <c>engagement_kill_finalize</c>.</summary>
    public int KillFinalizes { get; internal set; }

    /// <summary>Kills the player scored that incremented <c>[0xF102]</c> or <c>[0xF106]</c>.</summary>
    public int PlayerKillsTallied { get; internal set; }

    /// <summary>Calls into <c>combat_spawn_slot_depart</c>.</summary>
    public int Departures { get; internal set; }

    /// <summary>VM (<see cref="IEngagementVm"/>) advances requested.</summary>
    public int VmAdvances { get; internal set; }

    /// <summary>Countermeasure-lookup calls made.</summary>
    public int CountermeasureLookups { get; internal set; }

    /// <summary>Player-damage seam calls made.</summary>
    public int PlayerDamageCalls { get; internal set; }

    /// <summary>Coalition-spawn seam calls made.</summary>
    public int CoalitionSpawnCalls { get; internal set; }

    /// <summary>Mission <c>on_slot_destroyed</c> seam calls made.</summary>
    public int MissionHookCalls { get; internal set; }

    /// <summary>Accumulates <paramref name="other"/> into this census.</summary>
    /// <param name="other">The census to fold in.</param>
    public void Add(ProjectileKernelCensus other)
    {
        ArgumentNullException.ThrowIfNull(other);
        SlotsVisited += other.SlotsVisited;
        SlotsTicked += other.SlotsTicked;
        Expired += other.Expired;
        ExpiredWithEffect += other.ExpiredWithEffect;
        Tracking += other.Tracking;
        EligibilityChecks += other.EligibilityChecks;
        AltitudeWindowTicks += other.AltitudeWindowTicks;
        AltitudeWindowDraws += other.AltitudeWindowDraws;
        TrackingStateUpdates += other.TrackingStateUpdates;
        StateUpdateCalls += other.StateUpdateCalls;
        AngleSteps += other.AngleSteps;
        AngleStepsLevelFlight += other.AngleStepsLevelFlight;
        EnvelopeBoost += other.EnvelopeBoost;
        EnvelopeCoast += other.EnvelopeCoast;
        EnvelopeSkipped += other.EnvelopeSkipped;
        GridQueries += other.GridQueries;
        Acquired += other.Acquired;
        SameTarget += other.SameTarget;
        TeamTagTests += other.TeamTagTests;
        TeamTagRejects += other.TeamTagRejects;
        SentinelTargets += other.SentinelTargets;
        SelfTeam += other.SelfTeam;
        CheatBias += other.CheatBias;
        SelectionWindowOpen += other.SelectionWindowOpen;
        FireHandlerCalls += other.FireHandlerCalls;
        NearMiss += other.NearMiss;
        NoDamage += other.NoDamage;
        for (int i = 0; i < NoDamageReasons.Length; i++)
        {
            NoDamageReasons[i] += other.NoDamageReasons[i];
        }

        for (int i = 0; i < FireRoutes.Length; i++)
        {
            FireRoutes[i] += other.FireRoutes[i];
        }

        CountermeasureHits += other.CountermeasureHits;
        PlayerHits += other.PlayerHits;
        EnemyHits += other.EnemyHits;
        SmokeStarts += other.SmokeStarts;
        Kills += other.Kills;
        CloseRangeKills += other.CloseRangeKills;
        KillFinalizes += other.KillFinalizes;
        PlayerKillsTallied += other.PlayerKillsTallied;
        Departures += other.Departures;
        VmAdvances += other.VmAdvances;
        CountermeasureLookups += other.CountermeasureLookups;
        PlayerDamageCalls += other.PlayerDamageCalls;
        CoalitionSpawnCalls += other.CoalitionSpawnCalls;
        MissionHookCalls += other.MissionHookCalls;
    }

    /// <summary>A one-line rendering for a verification report.</summary>
    /// <returns>The census as text.</returns>
    public override string ToString() =>
        $"slots {SlotsVisited}/{SlotsTicked} ticked  expire {Expired}(fx {ExpiredWithEffect})  "
        + $"track {Tracking}  stateupd {StateUpdateCalls}  elig {EligibilityChecks}  altwin {AltitudeWindowTicks}(draw {AltitudeWindowDraws})  "
        + $"angstep {AngleSteps}(level {AngleStepsLevelFlight})  env b/c/skip {EnvelopeBoost}/{EnvelopeCoast}/{EnvelopeSkipped}  "
        + $"grid {GridQueries}(hit {Acquired}, same {SameTarget}, sentinel {SentinelTargets})  "
        + $"teamtag {TeamTagTests}(reject {TeamTagRejects})  selfteam {SelfTeam}  cheat {CheatBias}  "
        + $"win {SelectionWindowOpen}  fire {FireHandlerCalls}(miss {NearMiss}, none {NoDamage}, cm {CountermeasureHits}, "
        + $"player {PlayerHits}, enemy {EnemyHits}, smoke {SmokeStarts}, kill {Kills}, close {CloseRangeKills}, "
        + $"final {KillFinalizes}, tally {PlayerKillsTallied})  depart {Departures}  vm {VmAdvances}\n"
        + "    nodamage: " + string.Join(
            " ",
            Enum.GetValues<NoDamageReason>().Select(r => $"{r}={NoDamageReasons[(int)r]}"))
        + "\n    fireroute: " + string.Join(
            " ", Enum.GetValues<FireRoute>().Select(r => $"{r}={FireRoutes[(int)r]}"));
}
