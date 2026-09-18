using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Combat.Effects;

namespace CYAC.Port.Core.Sim.Combat.Lifecycle;

/// <summary>
/// <c>per_object_tick @image@0x2C66F</c> — the DESTRUCTION pool's five-state machine: the ejecting
/// pilot, his seat, the jettisoned canopy, and the crater each of them leaves.
/// </summary>
/// <remarks>
/// <para>
/// One of the five effect ticks of the mission frame (<c>image@0x00CD1</c>, the only caller
/// image-wide).  It walks the three <c>s_object_slot</c> records from <c>[0xBBD6]</c> down to
/// <c>[0xBB4C]</c> in <c>0x45</c> strides, skips the inactive ones (<c>cmp byte [si],0 / je</c>
/// @<c>image@0x2C963</c>) and, for each live one, pulls the 24-byte heads of its three ext-pool
/// objects onto the frame, runs the machine, and copies them back
/// (<c>rep movsw cx=0x0C</c> ×3 both ways, <c>image@0x2C968..0x2C94B</c>).
/// </para>
/// <para>
/// <b>What the three ext objects are</b> (<c>object_pool_init_3_slots @image@0x2C2B6</c>, H7 §0.2):
/// <c>ext_ptr_a [+0x04]</c> is an <c>eject1</c> — <b>the PILOT</b>, and the only one whose class is
/// swapped as the machine advances (<c>eject1 → eject2 → eject3 → eject4</c>, i.e. the parachute
/// opening); <c>ext_ptr_b [+0x06]</c> is a second <c>eject1</c> — <b>the SEAT</b>, switched on only
/// when the slot's <c>+0x02</c> kind flag is set; <c>ext_ptr_c [+0x08]</c> is a <c>canopy</c>.
/// </para>
/// <para>
/// <b>The five states</b> (<c>slot[+0x42]</c>, dispatched at <c>image@0x2C9BA</c>):
/// </para>
/// <list type="table">
///   <item><term>0 — ARMED</term><description>waits for the <c>+0x43</c> frame stamp
///   (<c>image@0x2C68C</c>), then goes to 1; when the kind flag is set it also lights the SEAT at
///   the pilot's own pose and seeds the seat's integrator from the pilot's with three edits
///   (<c>image@0x2C6FC</c>: <c>−0xA0</c> on its Y, target <c>0xED40</c> = −4,800, rate
///   <c>+0x640</c>).</description></item>
///   <item><term>1 — FALLING</term><description>while the pilot is above 15 position-hi-units
///   (≈3,840 world units) his elevation is walked toward <c>0x780</c>; below that, once his Y
///   velocity has gone negative and his elevation and roll have settled to 0, state 2
///   (<c>image@0x2C7DC</c>).</description></item>
///   <item><term>2 → 3 → 4</term><description>one frame each (<c>+0x43 = [0xF0C8] + 1</c>), each
///   swapping the pilot's class: <c>eject2</c> <c>[0x5578]</c>, <c>eject3</c> <c>[0x55C8]</c>,
///   <c>eject4</c> <c>[0x5618]</c>.  Entering 4 also rewrites the pilot's Y integrator to
///   <b>target −800, rate +800</b> (<c>image@0x2C83F</c>) — the CHUTE: he stops falling at 4,800
///   units a second and drifts down at 800.  Those are the "±800 terminal
///   velocities".</description></item>
/// </list>
/// <para>
/// <b>The ground.</b>  Every frame the pilot's Y is compared against <c>−classRecord[+0x38]</c>
/// (his class's <c>BoundsMinY</c>, i.e. its ground clearance) — above it he is integrated, at or
/// below it he is planted on it, the slot is marked for retirement, and unless he was already under
/// the chute (state 4) the impact fires a deferred effect, a <b>crater</b>
/// (<c>subsystem5x06_per_frame_slot_fire @image@0x2DB69</c>) and a sound.  The seat and the canopy
/// have a simpler end: each tumbles <c>0x28</c> BAM on all three Euler words per frame and, when its
/// Y goes NEGATIVE, switches itself off with a deferred effect at the spot.
/// </para>
/// </remarks>
public static class ObjectSlotTick
{
    /// <summary>The class record the pilot wears in state 2: <c>eject2</c> <c>[0x5578]</c>.</summary>
    /// <remarks><c>mov word [bp-0x32],0x5578</c> @<c>image@0x2C810</c>.</remarks>
    public const ushort Eject2ClassRecord = 0x5578;

    /// <summary>State 3's: <c>eject3</c> <c>[0x55C8]</c> (<c>image@0x2C828</c>).</summary>
    public const ushort Eject3ClassRecord = 0x55C8;

    /// <summary>State 4's: <c>eject4</c> <c>[0x5618]</c> (<c>image@0x2C849</c>) — the open chute.</summary>
    public const ushort Eject4ClassRecord = 0x5618;

    /// <summary>The chute's terminal descent target: <c>0xFCE0</c> = <b>−800</b>.</summary>
    /// <remarks><c>mov word [si+0x12],0xfce0</c> @<c>image@0x2C83F</c>.</remarks>
    public const short ChuteDescentTarget = unchecked((short)0xFCE0);

    /// <summary>…and the rate it approaches it at: <c>0x0320</c> = <b>+800</b>.</summary>
    /// <remarks><c>mov word [si+0x14],0x320</c> @<c>image@0x2C844</c>.</remarks>
    public const short ChuteDescentRate = 0x0320;

    /// <summary>The SEAT's descent target when state 0 seeds it: <c>0xED40</c> = −4,800.</summary>
    /// <remarks><c>mov word [si+0x24],0xed40</c> @<c>image@0x2C706</c>.</remarks>
    public const short SeatDescentTarget = unchecked((short)0xED40);

    /// <summary>The elevation the pilot is walked toward while he is still high: <c>0x780</c>.</summary>
    /// <remarks><c>mov word [bp-2],0x780</c> @<c>image@0x2C7E2</c>.</remarks>
    public const short HighFallElevation = 0x0780;

    /// <summary>
    /// The altitude, as the position triple's HIGH word, above which state 1 only holds attitude:
    /// <b>15</b> (<c>cmp word [bp-0x26],0xf / jl</c> @<c>image@0x2C7DC</c>).
    /// </summary>
    public const short HighFallHiWord = 0x000F;

    /// <summary>The per-frame tumble the seat and the canopy take on all three Euler words: <c>0x28</c>.</summary>
    /// <remarks><c>mov ax,0x28 / lcall angle_delta_add_wrap_0xB40</c> ×3 @<c>image@0x2C869</c>.</remarks>
    public const short TumbleBam = 0x28;

    /// <summary>
    /// The attitude-settling rate: <c>muldiv16_signed_shr8(0x2D0, g_scene_frame_dt_scaled [0xF11C])</c>.
    /// </summary>
    /// <remarks><c>image@0x2C677</c> — 720 BAM (90°) a second, scaled by the frame's dt.</remarks>
    public const short AttitudeRateBase = 0x02D0;

    /// <summary>Runs one frame of the whole pool.</summary>
    /// <param name="context">The lifecycle context (registers, arena, class records, out-calls).</param>
    /// <param name="effects">The deferred-effect and crater pools the ground impacts fire.</param>
    /// <returns>How many slots were live this frame.</returns>
    public static int PerFrameTick(EngagementLifecycleContext context, IObjectSlotEffects effects)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(effects);

        CombatRegisters registers = context.Registers;
        short dt = unchecked((short)registers.Word(SmokePuffTable.FrameDt));
        short rate = Fixed.MulDiv16SignedShr8(AttitudeRateBase, dt);   // image@0x2C677

        int live = 0;
        for (int slot = ObjectSlotPool.LastSlot; slot >= ObjectSlotPool.FirstSlot;
             slot -= ObjectSlotPool.SlotBytes)
        {
            if (registers.Byte(slot + ObjectSlotPool.ActiveFlag) == 0)     // image@0x2C963
            {
                continue;
            }

            live++;
            TickSlot(context, effects, slot, rate, dt);
        }

        return live;
    }

    /// <summary>One live slot's frame.</summary>
    /// <param name="context">The lifecycle context.</param>
    /// <param name="effects">The effect pools.</param>
    /// <param name="slot">The slot's DGROUP offset.</param>
    /// <param name="rate">The attitude-settling rate.</param>
    /// <param name="dt">The frame's scaled dt.</param>
    private static void TickSlot(
        EngagementLifecycleContext context,
        IObjectSlotEffects effects,
        int slot,
        short rate,
        short dt)
    {
        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;

        ushort pilotRef = registers.Word(slot + ObjectSlotPool.ExtPointerA);
        ushort seatRef = registers.Word(slot + ObjectSlotPool.ExtPointerB);
        ushort canopyRef = registers.Word(slot + ObjectSlotPool.ExtPointerC);
        if (!arena.Covers(pilotRef, 0x18) || !arena.Covers(seatRef, 0x18)
            || !arena.Covers(canopyRef, 0x18))
        {
            return;
        }

        // image@0x2C968..0x2C9AF — the three 24-byte heads onto the frame.
        ExtObject pilot = ExtObject.Read(arena, pilotRef);
        ExtObject seat = ExtObject.Read(arena, seatRef);
        ExtObject canopy = ExtObject.Read(arena, canopyRef);

        short elevationTarget = 0;                                  // image@0x2C9B1
        bool retire = false;                                        // image@0x2C9B6 [bp-0x4E]

        switch (registers.Byte(slot + StateByte))                   // image@0x2C9BA
        {
            case 0:
                StateArmed(context, slot, ref pilot, ref seat);
                break;
            case 1:
                StateFalling(registers, slot, ref pilot, ref elevationTarget);
                break;
            case 2:
                StateTimed(registers, slot, 3, Eject3ClassRecord, ref pilot);   // image@0x2C818
                break;
            case 3:
                StateChute(registers, slot, ref pilot);                      // image@0x2C830
                break;
            default:
                break;                                                       // 4 → straight on
        }

        // ── the PILOT's integrator and his ground test (image@0x2C70B..0x2C851) ────────────────
        int ground = -ClassGroundY(context, pilot.ClassRef);
        if (ground < pilot.Y)                                        // image@0x2C71B..0x2C725
        {
            Vec3StepTowardIterate(registers, slot + PilotIntegrator, dt);     // image@0x2C72A
            Vec3AccumulateScaled(registers, slot + PilotIntegrator, ref pilot, dt);  // image@0x2C733
            if (registers.Byte(slot + StateByte) != 0)               // image@0x2C736
            {
                pilot.Elevation = AngleStep.Toward(pilot.Elevation, elevationTarget, rate);
            }

            pilot.Roll = AngleStep.Toward(pilot.Roll, 0, rate);      // image@0x2C74D
        }

        if (ground >= pilot.Y)                                       // image@0x2C76D..0x2C77C
        {
            pilot.Y = ground;                                        // image@0x2C77F
            retire = true;                                           // image@0x2C785
            if (registers.Byte(slot + StateByte) != 4)               // image@0x2C789
            {
                // image@0x2C792..0x2C7CE — the impact: a deferred effect on the DISC fork
                // (+0x0C = 0, +0x0D = 0: no smoke follows), a CRATER, and a sound.
                effects.ScheduleDeferredEffect(pilot.Position, 0, 0, 0);
                effects.SpawnCrater(pilot.Position.X, pilot.Position.Z);
                effects.PlayImpactSound(pilot.Position);
            }

            if (registers.Byte(slot + ObjectSlotPool.PlayerOwnedFlag) != 0)
            {
                // image@0x2C7D4 / 0x2C9E5 — the player's own wreck hit the ground: trip the
                // destroyed flag and end the session NOW.
                if (registers.Byte(slot + StateByte) != 4)
                {
                    registers.SetByte(LifecycleOffsets.PlayerSlotDestroyedFlag, 1);
                }

                registers.SetWord(SessionEndDeadlineFrame, 0);
                Write(arena, pilotRef, seatRef, canopyRef, pilot, seat, canopy);
                if (retire)
                {
                    ObjectSlotPool.ExtPoolSlotDeactivate(context, (ushort)slot);
                }

                return;
            }
        }

        // ── the CANOPY (image@0x2C851) and the SEAT (image@0x2C8AF) ───────────────────────────
        Debris(registers, effects, slot + CanopyIntegrator, ref canopy, dt);
        Debris(registers, effects, slot + SeatIntegrator, ref seat, dt);

        Write(arena, pilotRef, seatRef, canopyRef, pilot, seat, canopy);
        if (retire)                                                  // image@0x2C94C
        {
            ObjectSlotPool.ExtPoolSlotDeactivate(context, (ushort)slot);
        }
    }

    /// <summary>State 0 — wait for the arm frame, then light the seat and seed its integrator.</summary>
    /// <param name="context">The lifecycle context.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="pilot">The pilot's frame copy.</param>
    /// <param name="seat">The seat's frame copy.</param>
    private static void StateArmed(
        EngagementLifecycleContext context, int slot, ref ExtObject pilot, ref ExtObject seat)
    {
        CombatRegisters registers = context.Registers;
        if (registers.Word(slot + StampWord) > registers.MasterFrameCounter)   // image@0x2C68F ja
        {
            return;
        }

        if (registers.Byte(slot + ObjectSlotPool.StageFlag) == 0)              // image@0x2C694
        {
            return;
        }

        registers.SetByte(slot + StateByte, 1);                                // image@0x2C69A
        if (registers.Byte(slot + ObjectSlotPool.KindFlag) == 0)               // image@0x2C69E
        {
            return;
        }

        // image@0x2C6A4..0x2C6DB — the SEAT wakes up at the pilot's exact pose.
        seat.Flags |= 1;
        seat.SetPosition(pilot.Position);
        seat.Heading = pilot.Heading;
        seat.Elevation = pilot.Elevation;
        seat.Roll = pilot.Roll;

        // image@0x2C6DE..0x2C6EE — 18 bytes: the pilot's integrator becomes the seat's…
        for (int i = 0; i < IntegratorBytes; i += 2)
        {
            registers.SetWord(slot + SeatIntegrator + i, registers.Word(slot + PilotIntegrator + i));
        }

        // …with four edits (image@0x2C6F1..0x2C70A).
        registers.SetWord(slot + SeatIntegrator + 0x02, registers.Word(slot + SeatIntegrator));
        registers.SetWord(slot + SeatIntegrator + 0x0E, registers.Word(slot + SeatIntegrator + 0x0C));
        registers.SetWord(
            slot + SeatIntegrator + 0x06,
            unchecked((ushort)(registers.Word(slot + SeatIntegrator + 0x06) - 0xA0)));
        registers.SetWord(
            slot + SeatIntegrator + 0x0A,
            unchecked((ushort)(registers.Word(slot + SeatIntegrator + 0x0A) + 0x640)));
        registers.SetWord(slot + SeatIntegrator + 0x08, unchecked((ushort)SeatDescentTarget));
    }

    /// <summary>State 1 — hold attitude while high, then hand over to the chute sequence.</summary>
    /// <param name="registers">The register file.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="pilot">The pilot's frame copy.</param>
    /// <param name="elevationTarget">The elevation the common integrator walks him toward.</param>
    private static void StateFalling(
        CombatRegisters registers, int slot, ref ExtObject pilot, ref short elevationTarget)
    {
        if (unchecked((short)(pilot.Y >> 16)) >= HighFallHiWord)               // image@0x2C7DC
        {
            elevationTarget = HighFallElevation;
            return;
        }

        if (unchecked((short)registers.Word(slot + PilotIntegrator + 0x06)) >= 0)  // image@0x2C7EA
        {
            return;
        }

        if (pilot.Elevation != 0 || pilot.Roll != 0)                           // image@0x2C7F3/0x2C7F9
        {
            return;
        }

        if (registers.Byte(slot + ObjectSlotPool.StageFlag) == 0)              // image@0x2C7FF
        {
            return;
        }

        // image@0x2C805..0x2C810 — state 2 next frame, and the pilot puts on eject2.
        registers.SetByte(slot + StateByte, 2);
        registers.SetWord(
            slot + StampWord, unchecked((ushort)(registers.MasterFrameCounter + 1)));
        pilot.ClassRef = Eject2ClassRecord;
    }

    /// <summary>State 2's body: one frame later, state 3 and the next class.</summary>
    /// <param name="registers">The register file.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="state">The state to enter.</param>
    /// <param name="classRecord">The class the pilot puts on.</param>
    /// <param name="pilot">The pilot's frame copy.</param>
    private static void StateTimed(
        CombatRegisters registers, int slot, byte state, ushort classRecord, ref ExtObject pilot)
    {
        if (registers.Word(slot + StampWord) > registers.MasterFrameCounter)   // image@0x2C81B ja
        {
            return;
        }

        registers.SetByte(slot + StateByte, state);
        registers.SetWord(
            slot + StampWord, unchecked((ushort)(registers.MasterFrameCounter + 1)));
        pilot.ClassRef = classRecord;
    }

    /// <summary>State 3 — the chute opens: <c>eject4</c>, and the ±800 terminal descent.</summary>
    /// <param name="registers">The register file.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="pilot">The pilot's frame copy.</param>
    private static void StateChute(CombatRegisters registers, int slot, ref ExtObject pilot)
    {
        if (registers.Word(slot + StampWord) > registers.MasterFrameCounter)   // image@0x2C833 jbe
        {
            return;
        }

        registers.SetByte(slot + StateByte, 4);                                // image@0x2C83B
        registers.SetWord(slot + PilotIntegrator + 0x08, unchecked((ushort)ChuteDescentTarget));
        registers.SetWord(slot + PilotIntegrator + 0x0A, unchecked((ushort)ChuteDescentRate));
        pilot.ClassRef = Eject4ClassRecord;
    }

    /// <summary>
    /// The seat's and the canopy's shared body (<c>image@0x2C851</c> / <c>image@0x2C8AF</c>): while
    /// the object is active, integrate it, tumble it <c>0x28</c> BAM on every axis, and switch it
    /// off with a deferred effect the frame its Y goes negative.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="effects">The effect pools.</param>
    /// <param name="integrator">The integrator's DGROUP offset.</param>
    /// <param name="part">The object's frame copy.</param>
    /// <param name="dt">The frame's scaled dt.</param>
    private static void Debris(
        CombatRegisters registers,
        IObjectSlotEffects effects,
        int integrator,
        ref ExtObject part,
        short dt)
    {
        if ((part.Flags & 1) == 0)
        {
            return;
        }

        Vec3StepTowardIterate(registers, integrator, dt);
        Vec3AccumulateScaled(registers, integrator, ref part, dt);
        part.Heading = Tumble(part.Heading);
        part.Elevation = Tumble(part.Elevation);
        part.Roll = Tumble(part.Roll);

        if (unchecked((short)(part.Y >> 16)) < 0)                    // image@0x2C887 / 0x2C8E5
        {
            part.Flags &= 0xFFFE;
            effects.ScheduleDeferredEffect(part.Position, 0, 0, 0);
        }
    }

    /// <summary><c>angle_delta_add_wrap_0xB40 @image@0x1842A</c> with a constant <c>0x28</c>.</summary>
    /// <param name="angle">The Euler word.</param>
    /// <returns>The tumbled angle.</returns>
    private static short Tumble(short angle) =>
        unchecked((short)new Angle(unchecked((ushort)angle)).Add(TumbleBam).Units);

    /// <summary>
    /// <c>vec3_step_toward_iterate @image@0x2CB7E</c> — three
    /// <see cref="ComponentStepToward"/> passes at <c>+0</c>, <c>+6</c> and <c>+0x0C</c>.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="triple">The integrator's DGROUP offset.</param>
    /// <param name="dt">The frame's scaled dt.</param>
    private static void Vec3StepTowardIterate(CombatRegisters registers, int triple, short dt)
    {
        ComponentStepToward(registers, triple, dt);
        ComponentStepToward(registers, triple + 6, dt);
        ComponentStepToward(registers, triple + 0x0C, dt);
    }

    /// <summary>
    /// <c>component_step_toward @image@0x2CB9B</c> — one clamped step-toward on
    /// <c>{current +0, target +2, rate +4}</c>.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="component">The component's DGROUP offset.</param>
    /// <param name="dt">The frame's scaled dt.</param>
    /// <remarks>
    /// The step is <c>muldiv16_signed(rate, dt, 0x100)</c> with a FLOOR OF ONE (<c>or ax,ax /
    /// jne</c> then <c>mov cx,1</c> @<c>image@0x2CBB3</c>), so a component with a rate too small
    /// to move at this frame rate still creeps one unit — which is what keeps the chute's
    /// descent from stalling at a very small dt.
    /// </remarks>
    private static void ComponentStepToward(CombatRegisters registers, int component, short dt)
    {
        short current = unchecked((short)registers.Word(component));
        short target = unchecked((short)registers.Word(component + 2));
        short rate = unchecked((short)registers.Word(component + 4));

        short delta = unchecked((short)(target - current));          // image@0x2CBA2
        short step = Fixed.MulDiv16Signed(rate, dt, 0x100);          // image@0x2CBAE
        short amount = step == 0 ? (short)1 : step;                  // image@0x2CBB7

        short magnitude = unchecked((short)(delta < 0 ? -delta : delta));
        if (magnitude >= amount)                                     // image@0x2CBC1 jge
        {
            registers.SetWord(
                component,
                unchecked((ushort)(delta < 0 ? current - amount : current + amount)));
            return;
        }

        registers.SetWord(component, unchecked((ushort)target));     // image@0x2CBC5 the snap
    }

    /// <summary>
    /// <c>vec3_accumulate_scaled @image@0x2CBDD</c> — add each component's CURRENT, scaled by
    /// <c>dt/16</c> and sign-extended, into the object's <c>i32</c> position triple.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="triple">The integrator's DGROUP offset.</param>
    /// <param name="target">The object whose position is accumulated.</param>
    /// <param name="dt">The frame's scaled dt.</param>
    private static void Vec3AccumulateScaled(
        CombatRegisters registers, int triple, ref ExtObject target, short dt)
    {
        target.X = unchecked(target.X + Fixed.MulDiv16Signed(
            unchecked((short)registers.Word(triple)), dt, 0x10));
        target.Y = unchecked(target.Y + Fixed.MulDiv16Signed(
            unchecked((short)registers.Word(triple + 6)), dt, 0x10));
        target.Z = unchecked(target.Z + Fixed.MulDiv16Signed(
            unchecked((short)registers.Word(triple + 0x0C)), dt, 0x10));
    }

    /// <summary>
    /// <c>−classRecord[+0x38..0x3B]</c> — the Y a member of this class rests at, in position units.
    /// </summary>
    /// <param name="context">The lifecycle context (its constant DGROUP surface).</param>
    /// <param name="classRef">The class record's DGROUP near pointer.</param>
    /// <returns>The class's AABB minimum Y as a signed 32-bit value.</returns>
    private static int ClassGroundY(EngagementLifecycleContext context, ushort classRef) =>
        unchecked((int)((uint)context.StaticData.Word(classRef + ClassBoundsMinY)
            | ((uint)context.StaticData.Word(classRef + ClassBoundsMinY + 2) << 16)));

    private static void Write(
        PoolArena arena,
        ushort pilotRef,
        ushort seatRef,
        ushort canopyRef,
        in ExtObject pilot,
        in ExtObject seat,
        in ExtObject canopy)
    {
        seat.Write(arena, seatRef);                                  // image@0x2C90D
        pilot.Write(arena, pilotRef);                                // image@0x2C922
        canopy.Write(arena, canopyRef);                              // image@0x2C937
    }

    /// <summary><c>slot[+0x42]</c> — the state byte.</summary>
    public const int StateByte = 0x42;

    /// <summary><c>slot[+0x43]</c> — the UNALIGNED frame stamp the timed states wait on.</summary>
    public const int StampWord = 0x43;

    /// <summary><c>slot[+0x0A]</c> — the PILOT's 18-byte three-component integrator.</summary>
    public const int PilotIntegrator = 0x0A;

    /// <summary><c>slot[+0x1C]</c> — the SEAT's.</summary>
    public const int SeatIntegrator = 0x1C;

    /// <summary><c>slot[+0x2E]</c> — the CANOPY's.</summary>
    public const int CanopyIntegrator = 0x2E;

    /// <summary>One integrator's length: three components of six bytes.</summary>
    public const int IntegratorBytes = 0x12;

    /// <summary><c>s_mesh_registry_slot +0x38</c> — the class's AABB minimum Y, an <c>i32</c>.</summary>
    /// <remarks>
    /// <c>mov ax,[bx+0x38] / mov dx,[bx+0x3a] / neg…</c> @<c>image@0x2C70E</c>; its negation is the
    /// class's ground clearance (<c>+0x2C</c>, <c>ClassRecord.GroundClearance</c>) — <c>eject1</c>
    /// 4,736 position units, <c>canopy</c> 512.
    /// </remarks>
    public const int ClassBoundsMinY = 0x38;

    /// <summary><c>g_session_end_deadline_frame [0xC390]</c>, written 0 = "end now".</summary>
    public const int SessionEndDeadlineFrame = 0xC390;

    /// <summary>The 24-byte head of one pool object, on the frame — the original's own copy.</summary>
    /// <remarks>
    /// <c>rep movsw cx=0x0C</c> in and out (<c>image@0x2C97D</c> / <c>image@0x2C91F</c>): class
    /// <c>+0</c>, flags <c>+2</c>, sibling link <c>+4</c>, three <c>i32</c> at <c>+6</c>, and the
    /// Euler triple at <c>+0x12</c>.  The link is carried untouched so the write-back cannot break
    /// the render list.
    /// </remarks>
    private struct ExtObject
    {
        public ushort ClassRef;
        public ushort Flags;
        public ushort Link;
        public int X;
        public int Y;
        public int Z;
        public short Heading;
        public short Elevation;
        public short Roll;

        public readonly CombatPosition Position => new(X, Y, Z);

        public static ExtObject Read(PoolArena arena, ushort objectRef) => new()
        {
            ClassRef = arena.Word(objectRef),
            Flags = arena.Word((ushort)(objectRef + 0x02)),
            Link = arena.Word((ushort)(objectRef + 0x04)),
            X = ReadInt32(arena, (ushort)(objectRef + 0x06)),
            Y = ReadInt32(arena, (ushort)(objectRef + 0x0A)),
            Z = ReadInt32(arena, (ushort)(objectRef + 0x0E)),
            Heading = unchecked((short)arena.Word((ushort)(objectRef + 0x12))),
            Elevation = unchecked((short)arena.Word((ushort)(objectRef + 0x14))),
            Roll = unchecked((short)arena.Word((ushort)(objectRef + 0x16))),
        };

        public readonly void Write(PoolArena arena, ushort objectRef)
        {
            arena.SetWord(objectRef, ClassRef);
            arena.SetWord((ushort)(objectRef + 0x02), Flags);
            arena.SetWord((ushort)(objectRef + 0x04), Link);
            WriteInt32(arena, (ushort)(objectRef + 0x06), X);
            WriteInt32(arena, (ushort)(objectRef + 0x0A), Y);
            WriteInt32(arena, (ushort)(objectRef + 0x0E), Z);
            arena.SetWord((ushort)(objectRef + 0x12), unchecked((ushort)Heading));
            arena.SetWord((ushort)(objectRef + 0x14), unchecked((ushort)Elevation));
            arena.SetWord((ushort)(objectRef + 0x16), unchecked((ushort)Roll));
        }

        public void SetPosition(CombatPosition position)
        {
            X = position.X;
            Y = position.Y;
            Z = position.Z;
        }

        private static int ReadInt32(PoolArena arena, ushort at) =>
            unchecked((int)((uint)arena.Word(at) | ((uint)arena.Word((ushort)(at + 2)) << 16)));

        private static void WriteInt32(PoolArena arena, ushort at, int value)
        {
            arena.SetWord(at, unchecked((ushort)value));
            arena.SetWord((ushort)(at + 2), unchecked((ushort)(value >> 16)));
        }
    }
}

/// <summary>
/// The out-calls <see cref="ObjectSlotTick"/> makes when something it animates hits the ground.
/// </summary>
/// <remarks>
/// Three, all from <c>image@0x2C792..0x2C7CE</c>: a deferred effect
/// (<c>deferred_effect_record_schedule @image@0x03A7C</c>), a CRATER
/// (<c>subsystem5x06_per_frame_slot_fire @image@0x2DB69</c>) and an impact sound
/// (<c>lcall 0x3981:0x393</c> @<c>image@0x2C7C9</c>).  A seam so that
/// <c>Sim/Combat/Lifecycle</c> keeps no dependency on the session's effect wiring.
/// </remarks>
public interface IObjectSlotEffects
{
    /// <summary>
    /// <c>deferred_effect_record_schedule</c> — light a flash at a point.
    /// </summary>
    /// <param name="position">Where.</param>
    /// <param name="renderFork">The record's <c>+0x0C</c> byte (the render fork).</param>
    /// <param name="emitterType">Its <c>+0x0D</c> byte (0 = flash only, no smoke follows).</param>
    /// <param name="subject">The object the caller is describing, or 0.</param>
    void ScheduleDeferredEffect(CombatPosition position, byte renderFork, byte emitterType, ushort subject);

    /// <summary>
    /// <c>subsystem5x06_per_frame_slot_fire</c> — put a <c>crater</c> on the ground at an X/Z.
    /// </summary>
    /// <param name="x">World X, in position units.</param>
    /// <param name="z">World Z.</param>
    void SpawnCrater(int x, int z);

    /// <summary>The impact cue (<c>lcall 0x3981:0x393</c>).</summary>
    /// <param name="position">Where it happened.</param>
    void PlayImpactSound(CombatPosition position);
}
