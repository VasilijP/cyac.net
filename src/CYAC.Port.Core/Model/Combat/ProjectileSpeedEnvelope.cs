using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Model.Combat;

/// <summary>
/// What the motor flag — <c>pool_object[+3]</c> bit7 — should become after one envelope step.
/// </summary>
/// <remarks>
/// A three-state answer rather than a <c>bool</c> because the original really has three outcomes: a
/// class with no envelope (<see cref="WeaponClass.BoostFrames"/> == 0) jumps past both arms
/// (<c>image@0x027CD..0x027CF</c>) and touches neither the speed nor the flag.
/// </remarks>
public enum MotorState
{
    /// <summary>The class has no envelope; the original leaves the flag exactly as it was.</summary>
    Unchanged,

    /// <summary>Boosting — <c>or byte es:[bx+3],0x80</c> @<c>image@0x0282C</c>.</summary>
    Burning,

    /// <summary>Coasting — <c>and byte es:[bx+3],0x7f</c> @<c>image@0x02885</c>.</summary>
    Coasting,
}

/// <summary>
/// The result of one <see cref="ProjectileSpeedEnvelope.Step"/>: the projectile's next speed and what
/// its motor flag should become.
/// </summary>
/// <param name="SpeedQ8">The next value of <c>s_combat_spawn_record[+0x0C] speed_q8_i32</c>.</param>
/// <param name="Motor">What to do with the pool object's motor bit.</param>
public readonly record struct ProjectileSpeedStep(int SpeedQ8, MotorState Motor);

/// <summary>
/// The projectile speed envelope — the pure integer core of <c>combat_object_tick @0x026AB</c>
/// phase 3 (<c>image@0x027C7..0x02889</c>): boost toward a ceiling while the motor burns, then coast
/// down to a floor.
/// </summary>
/// <remarks>
/// <para>
/// INT-only: this is the integer half of a projectile's kinematics and it decides hits, so it is
/// part of the reproducible spine.  Every operation keeps the original's width and wrap semantics —
/// the 32-bit speed adds wrap, the frame comparisons are <b>unsigned</b> 16-bit.
/// </para>
/// <para>
/// Source of truth: the original's bytes at <c>image@0x027C7..0x02889</c> and the spawn-time
/// initialisation at <c>image@0x024C1..0x0250E</c>, as narrated.  the banner, not the body, is what
/// this transliterates.
/// </para>
/// <para>
/// <b>SHIPPED QUIRK, not modelled away:</b> <see cref="IsBoosting"/> compares two <c>u16</c> frame
/// numbers with an unsigned <c>jb</c> (<c>image@0x027D8</c>), and the boost deadline is formed by a
/// wrapping 16-bit add (<c>image@0x024FE</c>).  A boost window that straddles the frame counter's
/// wrap therefore ends immediately.  Reproduced deliberately; if it ever matters it belongs in the
/// quirk registry, not in a "fix" here.
/// </para>
/// </remarks>
public static class ProjectileSpeedEnvelope
{
    /// <summary>
    /// The speed a freshly spawned projectile starts with:
    /// <c>max(weapon.InitialSpeed &lt;&lt; 8, launcherSpeedQ8)</c>, a signed 32-bit maximum.
    /// </summary>
    /// <remarks>
    /// <c>image@0x024C1..0x024DE</c>: <c>mov ax,[si+0x14]; cdq; mov cl,8; shl_i32_by_cl</c> then the
    /// MSC signed 32-bit compare (<c>jg</c> on the high word, <c>jae</c> on the low) against the
    /// launcher's speed, storing the larger to <c>record[+0x0C]</c>.  A missile whose
    /// <see cref="WeaponClass.InitialSpeed"/> is 0 therefore leaves the rail at the launcher's speed.
    /// </remarks>
    public static int InitialSpeedQ8(WeaponClass weapon, int launcherSpeedQ8)
    {
        ArgumentNullException.ThrowIfNull(weapon);
        int classSpeedQ8 = unchecked(weapon.InitialSpeed << 8);
        return classSpeedQ8 >= launcherSpeedQ8 ? classSpeedQ8 : launcherSpeedQ8;
    }

    /// <summary>
    /// The frame the boost phase ends: <c>spawnFrame + weapon.BoostFrames</c>, wrapping at 16 bits
    /// (<c>image@0x024F9..0x02502</c> — <c>mov al,[si+0x1e]; sub ah,ah; add ax,[0xf0c8]</c>).
    /// </summary>
    public static ushort BoostEndFrame(WeaponClass weapon, ushort spawnFrame)
    {
        ArgumentNullException.ThrowIfNull(weapon);
        return unchecked((ushort)(spawnFrame + weapon.BoostFrames));
    }

    /// <summary>
    /// The frame the projectile expires: <c>spawnFrame + weapon.LifetimeFrames</c>, wrapping at 16
    /// bits (<c>image@0x02505..0x0250E</c>).
    /// </summary>
    public static ushort ExpireFrame(WeaponClass weapon, ushort spawnFrame)
    {
        ArgumentNullException.ThrowIfNull(weapon);
        return unchecked((ushort)(spawnFrame + weapon.LifetimeFrames));
    }

    /// <summary>
    /// Whether the projectile's display lifetime has elapsed — <c>expireFrame &lt;= frameCounter</c>,
    /// unsigned.
    /// </summary>
    /// <remarks>
    /// <c>image@0x026D1</c>: <c>cmp [si+0x14],dx; ja 0x2722</c> — the tick continues only while the
    /// expiry frame is strictly <i>above</i> the counter, so equality already expires the shot.
    /// </remarks>
    public static bool IsExpired(ushort expireFrame, ushort frameCounter) => expireFrame <= frameCounter;

    /// <summary>
    /// Whether the motor is still burning — <c>boostEndFrame &gt;= frameCounter</c>, unsigned.
    /// </summary>
    /// <remarks>
    /// <c>image@0x027D5</c>: <c>cmp word [si+0x12],ax; jb 0x2833</c> — the coast arm is taken only
    /// when the deadline is strictly <i>below</i> the counter, so the boost arm still runs on the
    /// deadline frame itself.  (An earlier narrative says "while frame &lt; record[+0x12]"; the
    /// bytes say <c>&lt;=</c>, and bytes win — reported.)
    /// </remarks>
    public static bool IsBoosting(ushort boostEndFrame, ushort frameCounter) => boostEndFrame >= frameCounter;

    /// <summary>
    /// One envelope step: given the class, where the frame counter stands relative to the spawn's
    /// boost deadline, the current Q8 speed and this frame's scaled delta-t, produce the next Q8 speed
    /// and the motor flag's new state.
    /// </summary>
    /// <param name="weapon">The firing weapon's class — the spawn's <c>record[+0]</c>.</param>
    /// <param name="frameCounter">
    /// <c>g_master_frame_counter [0xF0C8]</c> as the tick sees it (<c>image@0x027D2</c>).
    /// </param>
    /// <param name="boostEndFrame">
    /// The spawn's <c>record[+0x12] boost_end_frame_u16</c>; see <see cref="BoostEndFrame"/>.
    /// </param>
    /// <param name="speedQ8">The spawn's current <c>record[+0x0C] speed_q8_i32</c>.</param>
    /// <param name="frameDelta">
    /// <c>g_scene_frame_dt_scaled [0xF11C]</c>.  The original multiplies it through
    /// <c>imul16_signed</c>, so it is consumed as a signed 16-bit value even though the schema types
    /// the global <c>u16</c>.
    /// </param>
    /// <returns>The next speed and what the motor bit should become.</returns>
    public static ProjectileSpeedStep Step(
        WeaponClass weapon,
        ushort frameCounter,
        ushort boostEndFrame,
        int speedQ8,
        short frameDelta)
    {
        ArgumentNullException.ThrowIfNull(weapon);

        // image@0x027C7..0x027CF — `mov bx,[si]; cmp byte [bx+0x1e],0; jne …; jmp 0x288a`.
        // No boost frames means the whole phase is skipped: constant speed, motor bit untouched.
        if (weapon.BoostFrames == 0)
        {
            return new ProjectileSpeedStep(speedQ8, MotorState.Unchanged);
        }

        // image@0x027D2..0x027D8 — `mov ax,[0xf0c8]; cmp [si+0x12],ax; jb 0x2833` (UNSIGNED).
        if (IsBoosting(boostEndFrame, frameCounter))
        {
            // image@0x027DA..0x02831 — the BOOST arm.
            // ceiling = (i32)class[+0x16] << 8   (`cdq; mov cl,8; shl_i32_by_cl`)
            int ceiling = unchecked(weapon.BoostSpeedMax << 8);

            // image@0x027EB..0x027F5 — MSC's signed 32-bit compare; accelerate only while below.
            if (speedQ8 < ceiling)
            {
                // image@0x027F9..0x02808 — `speed += imul16_signed(class[+0x1a], dt)`, 32-bit add.
                speedQ8 = unchecked(speedQ8 + Fixed.IMul16(weapon.BoostAcceleration, frameDelta));

                // image@0x0280B..0x02826 — clamp back down to the ceiling if the step overshot.
                if (speedQ8 > ceiling)
                {
                    speedQ8 = ceiling;
                }
            }

            // image@0x02829..0x0282C — `les bx,[bp-8]; or byte es:[bx+3],0x80`, on every boost path.
            return new ProjectileSpeedStep(speedQ8, MotorState.Burning);
        }

        // image@0x02833..0x02889 — the COAST arm, the exact mirror.
        // floor = (i32)class[+0x18] << 8
        int floor = unchecked(weapon.CoastSpeedMin << 8);

        // image@0x02844..0x0284E — decelerate only while above the floor.
        if (speedQ8 > floor)
        {
            // image@0x02852..0x02861 — `speed -= imul16_signed(class[+0x1c], dt)`, 32-bit subtract.
            speedQ8 = unchecked(speedQ8 - Fixed.IMul16(weapon.CoastDeceleration, frameDelta));

            // image@0x02864..0x0287F — clamp back up to the floor if the step undershot.
            if (speedQ8 < floor)
            {
                speedQ8 = floor;
            }
        }

        // image@0x02882..0x02885 — `les bx,[bp-8]; and byte es:[bx+3],0x7f`, on every coast path.
        return new ProjectileSpeedStep(speedQ8, MotorState.Coasting);
    }

    /// <summary>
    /// How far the projectile travels this tick along its heading/elevation:
    /// <c>(speedQ8 × frameDelta) &gt;&gt; 8</c>.
    /// </summary>
    /// <remarks>
    /// <c>image@0x028B2..0x028C5</c> — <c>mulu32</c> of the Q8 speed by the scaled delta-t, then
    /// <c>sar_i32_by_cl</c> with <c>cl = 8</c>; the result is handed to
    /// <c>angle_distance_xyz_accum @0x2084A</c> @<c>image@0x028CC</c>, which adds it to the pool
    /// object's own X/Y/Z.  The multiply is the unsigned 32×32 → low-32 routine, so it wraps; the
    /// shift that follows is arithmetic.
    /// </remarks>
    public static int DistanceThisTick(int speedQ8, short frameDelta) =>
        unchecked((int)((uint)speedQ8 * (uint)frameDelta)) >> 8;
}
