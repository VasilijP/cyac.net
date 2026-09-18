using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Effects;
using CYAC.Port.Core.Sim.Combat.Lifecycle;
using CYAC.Port.Core.Sim.Combat.Vm;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// THE EFFECTS that nothing draws yet: the five per-frame effect ticks of
/// <c>mission_state_machine</c>'s CS1 → CS2 span (<c>image@0x00CCC..0x00CE0</c>), and every combat
/// out-call that feeds them.
/// </summary>
/// <remarks>
/// <para>
/// Counting these calls and throwing the rest away is why firing at a bandit produces nothing
/// visible.  Three of the five ticks are ported here from the bytes and the fourth and fifth are
/// named with what they cost (see <see cref="Census"/>' remarks).
/// </para>
/// <para>
/// The chain that makes smoke appear is worth stating once: the combat kernel's damage path calls
/// <see cref="SpawnDamageSmoke"/> → <see cref="EffectEmitterTable.Attach"/> puts a repeating EMITTER
/// row on the victim → <see cref="EffectEmitterTable.PerFrameAdvance"/> lights one
/// <see cref="SmokePuffTable"/> puff at the victim's CURRENT position every interval →
/// <see cref="SmokePuffTable.PerFrameStep"/> makes each puff rise, spread, tumble and die.  A puff is
/// an ordinary pool object with its ACTIVE bit set, so the renderer draws it with no new code at all.
/// </para>
/// </remarks>
public sealed class SessionEffects
    : IProjectileEvents, IEngagementNodeEvents, IVmScriptEffects, IObjectSlotEffects
{
    private readonly CombatRegisters _registers;
    private readonly PoolArena _arena;
    private readonly List<string> _messages = [];
    private readonly EngagementLifecycleContext? _lifecycle;
    private readonly ICombatRandom? _random;
    private readonly CountermeasureTable? _countermeasures;

    /// <summary>Wires the effect subsystems over one live combat state.</summary>
    /// <param name="registers">The DGROUP register file the three tables live in.</param>
    /// <param name="arena">The pool the effect objects live in.</param>
    /// <param name="lifecycle">
    /// The lifecycle context <c>per_object_tick @image@0x2C66F</c> needs (it reads the destruction
    /// slots out of the register file and the class records out of the constant DGROUP surface).
    /// Null leaves the fourth effect tick counted, as an earlier pass left it.
    /// </param>
    /// <param name="random">
    /// The PRNG a crater's random heading is drawn from (<c>image@0x2DBFB</c>, detail ≥ 2).
    /// </param>
    /// <param name="countermeasures">
    /// The five chaff/flare cloud slots the despawn arm walks.  Null leaves the whole
    /// countermeasure tick counted, as an earlier pass left it.
    /// </param>
    public SessionEffects(
        CombatRegisters registers,
        PoolArena arena,
        EngagementLifecycleContext? lifecycle = null,
        ICombatRandom? random = null,
        CountermeasureTable? countermeasures = null)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);
        _registers = registers;
        _arena = arena;
        _lifecycle = lifecycle;
        _random = random;
        _countermeasures = countermeasures;
    }

    /// <summary>The running census of everything the effect subsystems did.</summary>
    public EffectCensus Census { get; } = new();

    /// <summary>
    /// <c>g_frame_time_accum [0xF0D2:0xF0D4]</c> as one unsigned 32-bit value — the clock every
    /// effect deadline is stamped against.
    /// </summary>
    /// <returns>The accumulator.</returns>
    public uint FrameTimeAccum() =>
        unchecked((uint)(_registers.Word(0xF0D2) | (_registers.Word(0xF0D4) << 16)));

    /// <summary>
    /// Where the sounds go.  Null leaves every site counted, as the kernel leaves them.
    /// </summary>
    /// <remarks>
    /// The eight <c>Census.Sounds++</c> sites below now each carry the trampoline the original calls
    /// and its fixed tone id; <see cref="IAudioEvents"/> carries the citations.  Setting this cannot
    /// change a single simulation decision — every call is one-way and nothing reads a result.
    /// </remarks>
    public IAudioEvents? Audio { get; set; }

    /// <summary>The cockpit-text lines the mission's scripts printed, in order.</summary>
    public IReadOnlyList<string> Messages => _messages;

    /// <summary>
    /// CS1 → CS2 — the five effect ticks, in the original's order
    /// (<c>image@0x00CCC</c>, <c>0x00CD1</c>, <c>0x00CD6</c>, <c>0x00CDB</c>, <c>0x00CE0</c>).
    /// </summary>
    public void RunTicks()
    {
        Census.Frames++;
        Census.DeferredEffectsFired += DeferredEffectPool.PerFrameTick(_registers, _arena);

        // per_object_tick @image@0x2C66F.  The producer was there all along (engagement phase 6,
        // EnemySpawnAngleInit), and what was missing was the pool's own objects and this tick.
        // Both landed; the counter now counts the frames it ran with no live slot.
        if (_lifecycle is not null)
        {
            int live = ObjectSlotTick.PerFrameTick(_lifecycle, this);
            Census.ObjectSlotsLive = live;
            Census.ObjectSlotSlotFrames += live;
        }
        else
        {
            Census.ObjectSlotTicksSkipped++;
        }

        Census.PuffsEmitted += EffectEmitterTable.PerFrameAdvance(_registers, _arena);

        // H16 seam 2 — countermeasure drift @image@0x0AD24.  Its DESPAWN arm is now ported
        // (CountermeasurePool.Expire, image@0x0AD4C..0x0AD76) so the five slots recycle; the
        // ballistics half (random attitude, gravity, friction, integration) is still only counted,
        // which is what this counter has always named.
        if (_countermeasures is not null)
        {
            Census.CountermeasureCloudsExpired +=
                CountermeasurePool.Expire(_countermeasures, _arena, FrameTimeAccum());
        }

        Census.CountermeasureDriftsSkipped++;
        Census.LivePuffs = SmokePuffTable.PerFrameStep(_registers, _arena);
    }

    // ------------------------------------------------------------------- IProjectileEvents

    /// <inheritdoc/>
    public void ScheduleDeferredEffect(CombatPosition position, ushort subject, byte kind, byte outcome)
    {
        if (DeferredEffectPool.Schedule(_registers, _arena, position, kind, outcome, subject) >= 0)
        {
            Census.DeferredEffectsScheduled++;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>sfx_object_impact_dispatch @image@0x29BA3</c>.  The <c>AL</c> byte is 1 at the tick's
    /// site (<c>image@0x02713</c>) and the resolver's lethal-class flag at its own (<c>mov
    /// al,[bp-0x14]</c> @<c>image@0x0C0D3</c>), so it passes straight through.
    /// </remarks>
    public void PlayImpactSound(CombatPosition position, byte kind)
    {
        Census.Sounds++;
        Audio?.ObjectImpact(position, kind != 0);
    }

    /// <inheritdoc/>
    public void RecordDeparture(ushort objectRef) => Census.FilmRecords++;

    /// <inheritdoc/>
    public void ClearSubsystem4x19(ushort objectRef) =>
        Census.EmitterRowsCleared += EffectEmitterTable.ClearForOwner(_registers, objectRef);

    /// <inheritdoc/>
    /// <remarks>
    /// <c>image@0x0BE8C</c> — a shell that misses but passes close lights a puff at the miss point.
    /// The two arguments really are pushed Z-then-X; the Y comes from the emitter's own floor.
    /// </remarks>
    public void NearMissEffect(int z, int x)
    {
        Census.NearMisses++;
        SmokePuffTable.Allocate(
            _registers,
            _arena,
            new CombatPosition(x, EffectEmitterTable.FixedPointY, z),
            0);
    }

    /// <inheritdoc/>
    public void PlayerHitFeedback(short damage, byte guided) => Census.PlayerHits++;

    /// <summary>
    /// The host's advisor listener, told every action code the combat kernels raise.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="SmokeTrail.PresentedPosition"/>: an optional presentation-side
    /// delegate, null by default, that the kernel itself never reads.  Before it existed the five
    /// <c>AdvisorMessage</c> sites (a kill, <c>image@0x0BFCA</c>; the abort deadline,
    /// <c>image@0x0FE37</c>; a shot with no lock, <c>image@0x034AD</c>; a bandit or an inbound
    /// missile, <c>image@0x0825A</c>) only incremented a counter, so the port could show Chuck's
    /// window but never had a message to put in it.  Nothing downstream of this call can write
    /// simulation state, and the recorded replays are unaffected — the whole advisory path in the
    /// original is text.
    /// </remarks>
    public Action<byte>? AdvisorObserver { get; set; }

    /// <inheritdoc/>
    public void AdvisorMessage(byte selector)
    {
        Census.Advisories++;
        AdvisorObserver?.Invoke(selector);
    }

    /// <inheritdoc/>
    public void AdvisoryText(ushort attackerId, ushort victimRef) => Census.Advisories++;

    /// <inheritdoc/>
    public void RadioKillCall(ushort victimRef) => Census.RadioCalls++;

    /// <inheritdoc/>
    /// <remarks>
    /// THE DAMAGE SMOKE.  <c>weapon_fire_combat_loop</c> attaches it with <c>owner = victim</c>,
    /// <c>type = 1</c> and the variant the random draw picked (<c>image@0x0BF6C..0x0BF92</c>), so
    /// the trail follows the damaged aeroplane until its pool object goes inactive.
    /// </remarks>
    public void SpawnDamageSmoke(ushort ownerRef, byte variant)
    {
        if (EffectEmitterTable.Attach(
                _registers,
                _arena,
                default,
                ownerRef,
                interval: 0,
                firstDelay: 0,
                lifetime: 0,
                classifier: variant,
                kind: 1) >= 0)
        {
            Census.DamageSmokeRows++;
        }
    }

    // --------------------------------------------------------------- IEngagementNodeEvents (C3b)

    /// <inheritdoc/>
    public void AllocateSmokeSlot(CombatPosition position, ushort kind)
    {
        if (SmokePuffTable.Allocate(_registers, _arena, position, (byte)kind) >= 0)
        {
            Census.PuffsEmitted++;
        }
    }

    /// <inheritdoc/>
    /// <remarks><c>sfx_play_tone0d @image@0x29B82</c> pushes tone <c>0x0D</c>.</remarks>
    public void PlayAltitudeClampTone()
    {
        Census.Sounds++;
        Audio?.PlayTone(0x0D);
    }

    /// <inheritdoc/>
    public void ShowInterceptBriefing(ushort targetRef, ushort ownerSlot) => Census.Briefings++;

    /// <inheritdoc/>
    /// <remarks>
    /// <c>sfx_selected_object_audio_dispatch @image@0x29AA8</c>, which is a two-gate stub: it
    /// forwards to <c>sfx_weapon_type_tone_dispatch @image@0x29A79</c> only when the firing
    /// engagement is the player's SELECTED object (<c>cmp bx,[0xBE]</c> @<c>image@0x29AA8</c>) and
    /// view-flag bit 2 is set (<c>test byte [0xf12a],4</c> @<c>image@0x29AAE</c>).  The port applies
    /// the selected-object gate, which is state it has; the view-flag bit is the host's and is
    /// <c>(open)</c> here.  The forwarded routine then splits on the weapon record's own <c>+0x24
    /// &amp; 0x10</c> and passes its <c>+0x2B</c> byte as the gun volume.
    /// </remarks>
    public void PlayWeaponFireSound(ushort weaponClassRef, ushort ownerSlot)
    {
        Census.Sounds++;
        if (Audio is null)
        {
            return;
        }

        // The weapon CLASS record is const DGROUP (the 20-record table at 0x1158), not
        // register-file state: it is read through the static-data surface, and only through the
        // register file when a window happens to mirror it.  An earlier pass read the registers alone, which
        // was invisible because the selected-object gate returned before the read; with every
        // shooter reaching this point, a descriptor outside the windows threw.
        byte flags = WeaponClassByte(weaponClassRef + 0x24);
        byte level = WeaponClassByte(weaponClassRef + 0x2B);
        bool airToGround = (flags & 0x10) != 0;
        int volume = level;

        // The shooter's own position, so the sound can be PLACED.  The original never asks for it:
        // it plays the selected object's gun flat and everyone else's not at all.  The audio side
        // decides which of the two it wants (its positional pool, or the original's [0xBE] gate) —
        // nothing here changes, and nothing here reads a result.
        CombatPosition position = _arena.Covers(ownerSlot, 0x18)
            ? new CombatObjectView(_arena, ownerSlot).Position
            : default;
        Audio.ObjectWeaponFire(
            ownerSlot,
            position,
            ownerSlot == _registers.Word(HudSelectedObjectKey),
            airToGround,
            volume);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>lcall 0x3981:0x2e2</c> @<c>image@0x08BA0</c> resolves to <c>image@0x29AF2</c> =
    /// <c>sfx_play_tone16_random</c>, i.e. impact variant B with its own pitch/volume draw.
    /// </remarks>
    public void PlayDepartureTone()
    {
        Census.Sounds++;
        Audio?.PlayRandomizedImpactTone(variantB: true);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>engagement_slot_impact_and_depart @image@0x08BA6</c>'s three-call tail: a ground mark, a
    /// deferred effect and a sound.  The deferred effect is the one that shows.
    /// </remarks>
    public void ImpactEffects(CombatPosition position)
    {
        Census.Impacts++;

        // A GROUND MARK first (subsystem5x06_per_frame_slot_fire @image@0x08BB9, four arguments =
        // the fire position's X and Z as two i32), then the deferred effect, then a sound.  The
        // crater is what makes a kill leave something behind.
        SpawnCrater(position.X, position.Z);

        // …and the deferred effect's two selector bytes are 1 and 1 (`mov al,1 / push ax / push ax`
        // @image@0x08BC5), not 0 and 1: +0x0C = 1 puts the explosion on the BITMAP / burst fork of
        // deferred_effect_render (image@0x03E30) and +0x0D = 1 makes the record hand over to a
        // KIND-3 emitter when it fires — the SMOKE COLUMN over the wreck. H5a's reading dropped the
        // fork byte, so a crash site drew the small disc and no smoke followed.
        ScheduleDeferredEffect(position, 0, kind: 1, outcome: 1);
        Census.Sounds++;                                                 // image@0x08BE9
        // `mov al,1` @image@0x08BE7 then lcall 0x3981:0x393 = sfx_object_impact_dispatch.
        Audio?.ObjectImpact(position, loud: true);
    }

    /// <inheritdoc/>
    public void SpawnEffects(CombatPosition position)
    {
        // image@0x08B1F..0x08B4D: the SPAWN branch's selectors are +0x0C = 1 (the burst fork)
        // and +0x0D = 0 (no emitter follows), the mirror image of the impact's.
        Census.Impacts++;
        ScheduleDeferredEffect(position, 0, kind: 1, outcome: 0);
        Census.Sounds++;                                                 // image@0x08B4D
        // `mov al,1` @image@0x08B4B; the same impact dispatch, on the fire position.
        Audio?.ObjectImpact(position, loud: true);
    }

    // ------------------------------------------------------------------ IObjectSlotEffects

    /// <inheritdoc/>
    public void ScheduleDeferredEffect(
        CombatPosition position, byte renderFork, byte emitterType, ushort subject) =>
        ScheduleDeferredEffect(position, subject, renderFork, emitterType);

    /// <inheritdoc/>
    /// <remarks>
    /// <c>subsystem5x06_per_frame_slot_fire @image@0x2DB69</c> — the five-slot <c>crater</c> pool.
    /// </remarks>
    public void SpawnCrater(int x, int z)
    {
        if (CraterPool.Fire(_registers, _arena, x, z, _random) >= 0)
        {
            Census.Craters++;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The ejection pool's ground impact takes the SOFT arm: <c>sub al,al</c>
    /// @<c>image@0x2C7C7</c> before <c>lcall 0x3981:0x393</c>, so <c>AL == 0</c> and the dispatch
    /// plays the 0x1C thud rather than the 0x15/0x16 weapon impact.
    /// </remarks>
    public void PlayImpactSound(CombatPosition position)
    {
        Census.Sounds++;
        Audio?.ObjectImpact(position, loud: false);
    }

    // -------------------------------------------------------------------- IVmScriptEffects (C4)

    /// <inheritdoc/>
    /// <remarks><c>0xD5 KILL_ACTOR</c> → <c>slot_4x19_clear_for_owner @image@0x0B582</c>.</remarks>
    public void KillActor(ushort actorRecordRef)
    {
        Census.ActorKills++;
        Census.EmitterRowsCleared += EffectEmitterTable.ClearForOwner(_registers, actorRecordRef);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>0xD6 SPAWN_ACTOR</c> → <c>subsystem4x19_row_attach</c> with the eight words
    /// <c>image@0x054E6..0x05502</c> pushes: <c>Type</c> is the puff kind, <c>ActiveFlag</c> the
    /// classifier, <c>Heading</c> the row's lifetime, <c>Duration</c> its interval and
    /// <c>ActorRecordRef</c> its owner — the mapping is positional and exact
    /// (<c>VmSpawnActorRequest</c>'s own field docs name each push).
    /// </remarks>
    public void SpawnActor(in VmSpawnActorRequest request)
    {
        Census.ActorSpawns++;
        if (EffectEmitterTable.Attach(
                _registers,
                _arena,
                default,
                request.ActorRecordRef,
                interval: request.Duration,
                firstDelay: request.Zero,
                lifetime: request.Heading,
                classifier: request.ActiveFlag,
                kind: request.Type) >= 0)
        {
            Census.ScriptEffectRows++;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>0xDE FIRE_WEAPON</c>'s deferred-effect call.  The two position pairs are unambiguous — the
    /// actor record's own <c>+0x06/+0x08</c> X and <c>+0x0E/+0x10</c> Z — and the effect fires at the
    /// pool's own Y floor.  <b>(open)</b>: which of the nine pushed words is the type byte and which
    /// the parameter is NOT re-derived here, so the port passes the constant <c>One</c> as the
    /// parameter and a non-zero type (so an emitter really follows the flash).  A P-probe on
    /// <c>image@0x05546</c> would settle it.
    /// </remarks>
    public void ScheduleDeferredEffect(in VmDeferredEffectRequest request)
    {
        Census.ScriptDeferredEffects++;
        int x = request.Field06 | (request.Field08 << 16);
        int z = request.Field0E | (request.Field10 << 16);
        ScheduleDeferredEffect(
            new CombatPosition(x, 0, z), request.ActorRecordRef, request.One, outcome: 1);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>0xDE FIRE_WEAPON</c>'s tail: <c>mov al,1</c> @<c>image@0x0556F</c> then <c>lcall
    /// 0x3981:0x393</c>, with the far pointer built from the actor record's own <c>+0x06</c>
    /// position triple (<c>add ax,6</c> @<c>image@0x0556A</c>) — which is exactly the near offset
    /// this seam receives.
    /// </remarks>
    public void ImpactSound(ushort actorRecordRef)
    {
        Census.Sounds++;
        if (Audio is null)
        {
            return;
        }

        Audio.ObjectImpact(
            new CombatPosition(
                Int32At(actorRecordRef),
                Int32At(unchecked((ushort)(actorRecordRef + 0x04))),
                Int32At(unchecked((ushort)(actorRecordRef + 0x08)))),
            loud: true);
    }

    /// <summary><c>g_hud_selected_object_key [0xBE]</c> — the object the player has selected.</summary>
    private const int HudSelectedObjectKey = 0xBE;

    /// <summary>
    /// One byte of a weapon CLASS record, from the const DGROUP surface when there is one.
    /// </summary>
    /// <param name="offset">The record's DGROUP near offset plus the field's.</param>
    /// <returns>The byte, or 0 when neither surface covers it.</returns>
    private byte WeaponClassByte(int offset) =>
        _lifecycle is { } lifecycle
            ? lifecycle.StaticData.Byte(offset)
            : _registers.Covers(offset, 1) ? _registers.Byte(offset) : (byte)0;

    /// <summary>Reads an <c>i32</c> out of the pool arena, the way the position triples are stored.</summary>
    /// <param name="offset">The arena near offset.</param>
    private int Int32At(ushort offset) =>
        unchecked(_arena.Word(offset) | (_arena.Word(unchecked((ushort)(offset + 2))) << 16));

    /// <inheritdoc/>
    /// <remarks>
    /// <c>subsystem5x06_per_frame_slot_fire @image@0x2DB69</c> — a ground-fire flash at a point.
    /// The port lights the same deferred-effect flash the impact path uses.
    /// </remarks>
    public void SlotFire(int x, int z)
    {
        Census.SlotFires++;
        ScheduleDeferredEffect(new CombatPosition(x, 0, z), 0, kind: 0, outcome: 1);
    }

    /// <inheritdoc/>
    public void PrintString(string text)
    {
        Census.Messages++;
        if (_messages.Count < 64)
        {
            _messages.Add(text);
        }
    }

    /// <summary>
    /// Where the AI script's <c>0xE0</c> hook goes: the mission module's <c>on_secondary_event</c>.
    /// </summary>
    /// <remarks>
    /// The original's own route is <c>engagement_script_interpreter</c>'s <c>0xE0</c> arm
    /// (<c>image@0x0547C</c>) → <c>combat_vtable_slot_fn4_dispatch @image@0x08CAB</c> → the module's
    /// export 2, with the word passed straight through and no slot-table lookup.  A session sets
    /// this once, after its kernel context exists; a verification harness leaves it null and the
    /// call is counted only.
    /// </remarks>
    public EngagementLifecycleContext? ModuleDispatch { get; set; }

    /// <inheritdoc/>
    public void CallScriptFunction(ushort functionIndex)
    {
        Census.ScriptFunctionCalls++;
        if (ModuleDispatch is { } lifecycle)
        {
            MissionModuleDispatch.OnSecondaryEvent(lifecycle, functionIndex);   // image@0x08CAB
        }
    }
}

/// <summary>What the effect subsystems did, and what is still missing.</summary>
/// <remarks>
/// <para>
/// <b>The two ticks that are still counted, not run</b>, and exactly what each costs:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>per_object_tick @image@0x2C66F</c> (<see cref="ObjectSlotTicksSkipped"/>) — the 3-slot
///     <c>object_slot_table [0xBB4C]</c> destruction/debris pool with its five-state machine and its
///     three <c>vec3</c> integrators.  Cost: after a kill the wreck's debris and the ejection seat do
///     not move.  Nothing PRODUCES a slot in the port yet either
///     (<c>object_pool_init_3_slots @image@0x2C2B6</c> is not run), so the tick would be a no-op
///     today — which is why it is the honest thing to name rather than half-port.
///   </description></item>
///   <item><description>
///     <c>countermeasure_cloud_per_frame_drift @image@0x0AD24</c>
///     (<see cref="CountermeasureDriftsSkipped"/>) — the 5-slot chaff/flare cloud pool's ballistics.
///     Cost: a dispensed decoy has no drifting cloud object, so it cannot be seen and cannot itself
///     be seduced onto; the DECOY short-circuit that makes the tactic work is ported and unaffected.
///     Its spawner <c>countermeasure_cloud_spawn @image@0x0ABC3</c> is not ported either, so again
///     the tick would have nothing to walk.
///   </description></item>
/// </list>
/// </remarks>
public sealed class EffectCensus
{
    /// <summary>Frames that ran the five ticks.</summary>
    public long Frames { get; internal set; }

    /// <summary>Emitter rows attached by the DAMAGE path.</summary>
    public long DamageSmokeRows { get; internal set; }

    /// <summary>Emitter rows attached by an AI script's <c>0xD6</c>.</summary>
    public long ScriptEffectRows { get; internal set; }

    /// <summary>Emitter rows cleared (a departure, a kill, a script <c>0xD5</c>).</summary>
    public long EmitterRowsCleared { get; internal set; }

    /// <summary>Smoke puffs lit — by an emitter row, a near miss or the node FSM.</summary>
    public long PuffsEmitted { get; internal set; }

    /// <summary>Puffs alive after the last tick.</summary>
    public int LivePuffs { get; internal set; }

    /// <summary>Deferred-effect flashes scheduled.</summary>
    public long DeferredEffectsScheduled { get; internal set; }

    /// <summary>…and fired by the tick.</summary>
    public long DeferredEffectsFired { get; internal set; }

    /// <summary>Near-miss puffs.</summary>
    public long NearMisses { get; internal set; }

    /// <summary>Impact-effect calls.</summary>
    public long Impacts { get; internal set; }

    /// <summary>Hits the player took.</summary>
    public long PlayerHits { get; internal set; }

    /// <summary>Sounds the port has no audio path for yet.</summary>
    public long Sounds { get; internal set; }

    /// <summary>Advisory / radio calls.</summary>
    public long Advisories { get; internal set; }

    /// <summary>Kill radio calls.</summary>
    public long RadioCalls { get; internal set; }

    /// <summary>Intercept briefings.</summary>
    public long Briefings { get; internal set; }

    /// <summary>Film records the port does not write.</summary>
    public long FilmRecords { get; internal set; }

    /// <summary>Script <c>0xD5 KILL_ACTOR</c> calls.</summary>
    public long ActorKills { get; internal set; }

    /// <summary>Script <c>0xD6 SPAWN_ACTOR</c> calls.</summary>
    public long ActorSpawns { get; internal set; }

    /// <summary>Script <c>0xDE FIRE_WEAPON</c> deferred effects.</summary>
    public long ScriptDeferredEffects { get; internal set; }

    /// <summary>Script slot fires.</summary>
    public long SlotFires { get; internal set; }

    /// <summary>Cockpit text lines.</summary>
    public long Messages { get; internal set; }

    /// <summary>Script <c>.S</c>-module callbacks.</summary>
    public long ScriptFunctionCalls { get; internal set; }

    /// <summary>Frames on which <c>per_object_tick</c> was counted, not run.</summary>
    public long ObjectSlotTicksSkipped { get; internal set; }

    /// <summary>Frames on which the countermeasure-cloud BALLISTICS were counted, not run.</summary>
    /// <remarks>
    /// H16 narrowed what this names: the tick's DESPAWN arm is ported now
    /// (<see cref="Combat.Effects.CountermeasurePool.Expire"/>), so what is still skipped is the
    /// random attitude, the gravity, the friction and the position integration.
    /// </remarks>
    public long CountermeasureDriftsSkipped { get; internal set; }

    /// <summary>How many chaff/flare clouds reached their deadline or the ground.</summary>
    public long CountermeasureCloudsExpired { get; internal set; }

    /// <summary>How many chaff/flare clouds were dispensed.</summary>
    public long CountermeasureCloudsSpawned { get; internal set; }

    /// <summary>How many destruction slots were live on the LAST frame (0…3).</summary>
    public int ObjectSlotsLive { get; internal set; }

    /// <summary>slot-frames run: the running sum of <see cref="ObjectSlotsLive"/>.</summary>
    public long ObjectSlotSlotFrames { get; internal set; }

    /// <summary>Craters planted (<c>subsystem5x06_per_frame_slot_fire</c>).</summary>
    public long Craters { get; internal set; }
}
