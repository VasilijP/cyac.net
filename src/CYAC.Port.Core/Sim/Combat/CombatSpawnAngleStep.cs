using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// <c>combat_spawn_angle_step @image@0x02DB8</c> — one frame of a tracking projectile's steering:
/// pick the aim point, take the 3-D bearing to it, and slew both orientation words toward that
/// bearing at the weapon class's rate.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Two doors, BOTH inside <c>combat_object_tick</c>: the tracking phase's
/// <c>image@0x0272C</c> (before the eligibility check) and <c>image@0x027BE</c> (the untracked
/// level-flight path).  Both bodies come straight from the bytes.
/// </para>
/// <para>
/// The aim point is a COPY.  The original <c>rep movsw</c>s the first 24 bytes of the target's pool
/// object onto its own stack (<c>image@0x02E16</c>, <c>cx = 0x0C</c> words) and then edits the
/// copy's altitude — so the ground clamp never touches the real target.  Reproduced.
/// </para>
/// </remarks>
public static class CombatSpawnAngleStep
{
    /// <summary>
    /// The elevation a shot with no target aims at (<c>image@0x02DFA mov word [bp-0x14],0x2d0</c>).
    /// </summary>
    /// <remarks>
    /// MEASURED convention (<c>ProjectileKernelUnitTests</c>): the elevation word is
    /// <c>atan2(horizontal range, Δy)</c>, so <b>0 is level and 0x2D0 is STRAIGHT UP</b> — the
    /// no-target default makes an unguided shot climb away, it does not level it off.  The name is
    /// corrected here.
    /// </remarks>
    public const short NoTargetElevation = 0x02D0;

    /// <summary>
    /// Steers one spawn slot for one frame.
    /// </summary>
    /// <param name="context">The kernel context.</param>
    /// <param name="record">The spawn record — the original's single stack argument.</param>
    public static void Step(ProjectileKernelContext context, SpawnRecordRef record)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Census.AngleSteps++;

        CombatRegisters registers = context.Registers;
        WeaponClassView weapon = new WeaponClassView(context.StaticData, record.WeaponClassRef);
        CombatObjectView self = new CombatObjectView(context.Arena, record.PoolObjectRef);

        // image@0x02DD7..0x02DEA — the per-frame turn rate, scaled by dt and floored at 1.
        short rate = Fixed.MulDiv16SignedShr8(
            weapon.TurnRate, unchecked((short)registers.Word(0xF11C)));
        if (rate == 0)
        {
            rate = 1;
        }

        short headingTarget;
        short elevationTarget;

        if ((record.StatusFlags & 0x08) != 0)                   // image@0x02DEF test byte [si+0x1a],8
        {
            // image@0x02DF5..0x02DFF — no target: heading 0 and elevation 0x2D0 (straight up).
            context.Census.AngleStepsLevelFlight++;
            headingTarget = 0;
            elevationTarget = NoTargetElevation;
        }
        else
        {
            CombatObjectView target = new CombatObjectView(context.Arena, record.TargetRef);

            // image@0x02E09..0x02E18 — 12 words of the target object copied to the stack.
            ushort copyFlags = target.Flags;
            CombatPosition aim = target.Position;

            // image@0x02E19..0x02E8E — the GROUND CLAMP: if the target is below the weapon class's
            // minimum aim altitude AND we are already within its ground radius in 2-D, raise the
            // COPY's altitude to that minimum.  Both thresholds are the class byte-pair << 8.
            int minimumAltitude = unchecked(weapon.MinimumAimAltitude << 8);
            if (minimumAltitude > 0 && aim.Y < minimumAltitude)  // image@0x02E2D..0x02E41
            {
                int proximity = CombatGeometry.Proximity2d(self.Position, aim);   // image@0x02E62
                int groundRadius = unchecked(weapon.GroundClampRadius << 8);      // image@0x02E76
                if (groundRadius < proximity)                                     // image@0x02E7B..0x02E83
                {
                    aim = aim with { Y = minimumAltitude };                       // image@0x02E85
                }
            }

            // image@0x02E91..0x02EA7 — the bearing pair, through the far unpack thunk.
            CombatBearing bearing = CombatGeometry.BearingAndElevation(self.Position, aim);
            headingTarget = bearing.Heading;
            elevationTarget = bearing.Elevation;

            // The copied flag word is never read back; naming it keeps the copy's extent honest.
            _ = copyFlags;
        }

        // image@0x02EAC..0x02ED3 — both orientation words slew at the same rate.
        self.Heading = AngleStep.Toward(self.Heading, headingTarget, rate);
        self.Elevation = AngleStep.Toward(self.Elevation, elevationTarget, rate);
    }
}
