using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// A 3-D world position as the pool arena stores it: three signed 32-bit words at a pool object's
/// <c>+0x06</c> (X), <c>+0x0A</c> (Y, altitude) and <c>+0x0E</c> (Z).
/// </summary>
/// <param name="X">The object's <c>+0x06 pos_x_i32</c>.</param>
/// <param name="Y">The object's <c>+0x0A pos_y_i32</c> — altitude.</param>
/// <param name="Z">The object's <c>+0x0E pos_z_i32</c>.</param>
public readonly record struct CombatPosition(int X, int Y, int Z);

/// <summary>
/// The bearing pair <c>bearing_range_abs_pos_compute @image@0x1864E</c> writes through its two
/// out-pointers.
/// </summary>
/// <param name="Heading">The compass bearing, already corrected by <c>-0x2D0</c> and wrapped.</param>
/// <param name="Elevation">The elevation angle, uncorrected (level reads <c>0x2D0</c>).</param>
public readonly record struct CombatBearing(short Heading, short Elevation);

/// <summary>
/// The pure geometry leaves the PROJECTILE row (frame-ladder row 1) computes with: two distance
/// metrics, the 3-D bearing pair, the aiming-cone test and the displacement integrator.
/// </summary>
/// <remarks>
/// <para>
/// INT-only — every one of them feeds a fire/track decision.  No <c>double</c>: the 32-bit products
/// and the <c>&gt;&gt;8</c> / <c>&gt;&gt;2</c> normalisations are the original's own.
/// </para>
/// <para>
/// Source of truth: the original's bytes.
/// </para>
/// <para>
/// A note on <c>0x99</c>: Capstone's 16-bit output in this repo prints <c>CWD</c> as <c>cdq</c> and
/// <c>CBW</c> as <c>cwde</c>.  Every one of them here operates on <c>AX</c>/<c>DX</c> and is read as
/// the 16-bit form.
/// </para>
/// </remarks>
public static class CombatGeometry
{
    /// <summary>
    /// <c>combat_pos_proximity_2d @image@0x0B04C</c> — the 2-D Manhattan distance
    /// <c>|Δx| + |Δz|</c> between two positions, as a signed 32-bit value in <c>DX:AX</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ret 0x10</c>, eight stack words: the caller pushes the two objects' X then Z pairs.  The
    /// original negates with <c>neg ax / adc dx,0 / neg dx</c> (<c>image@0x0B05D</c>) — the standard
    /// 32-bit two's-complement negate; on <c>int.MinValue</c> that reproduces itself, exactly as C#'s
    /// unchecked negate does.
    /// </para>
    /// <para>
    /// The <b>Y axis is not in it</b> — this is a ground-plane range, which is why the tracking phase
    /// pairs it with a separate altitude gate.
    /// </para>
    /// </remarks>
    /// <param name="a">One position (the original's <c>[bp+0x0C..0x13]</c> pair).</param>
    /// <param name="b">The other (the original's <c>[bp+0x04..0x0B]</c> pair).</param>
    /// <returns>The summed absolute deltas, wrapping at 32 bits.</returns>
    public static int Proximity2d(CombatPosition a, CombatPosition b)
    {
        int dx = unchecked(a.X - b.X);                          // image@0x0B04F..0x0B058
        if (dx < 0)
        {
            dx = unchecked(-dx);                                // image@0x0B05D neg/adc/neg
        }

        int dz = unchecked(a.Z - b.Z);                          // image@0x0B068..0x0B071
        if (dz < 0)
        {
            dz = unchecked(-dz);                                // image@0x0B076
        }

        return unchecked(dz + dx);                              // image@0x0B07D add ax,cx / adc dx,bx
    }

    /// <summary>
    /// <c>abs3d_manhattan_dist @image@0x2439C</c> — <c>|Δx| + |Δy| + |Δz|</c> over three signed
    /// 32-bit axes, taken through two FAR pointers.
    /// </summary>
    /// <remarks>
    /// The original's pointers address the position triple directly (a pool object's <c>+0x06</c>),
    /// so its <c>+0</c> / <c>+4</c> / <c>+8</c> are X / Y / Z.  It accumulates X, then Z, then Y
    /// (<c>image@0x243AA</c>, <c>0x243C3</c>, <c>0x243E0</c>) and restores <c>DS</c> with a literal
    /// <c>mov cx,0x4bd6</c> on the way out (<c>image@0x24401</c>) because it entered with
    /// <c>lds</c> — a detail with no port meaning.
    /// </remarks>
    /// <param name="a">The first position.</param>
    /// <param name="b">The second.</param>
    /// <returns>The summed absolute deltas, wrapping at 32 bits.</returns>
    public static int ManhattanDistance3d(CombatPosition a, CombatPosition b)
    {
        int dx = unchecked(a.X - b.X);
        if (dx < 0)
        {
            dx = unchecked(-dx);
        }

        int dz = unchecked(a.Z - b.Z);
        if (dz < 0)
        {
            dz = unchecked(-dz);
        }

        int dy = unchecked(a.Y - b.Y);
        if (dy < 0)
        {
            dy = unchecked(-dy);
        }

        return unchecked(dy + dz + dx);                         // image@0x243F7..0x243FF
    }

    /// <summary>
    /// The shared normalise-and-scale prologue of the three bearing routines: right-shift all
    /// components by 8 (arithmetic) until every one of them fits in 17 bits, then shift each right
    /// by 2.
    /// </summary>
    /// <remarks>
    /// The original's shift-by-8 is a byte-rotate + <c>CBW</c> idiom
    /// (<c>image@0x18692..0x186B1</c>), and the final <c>&gt;&gt;2</c> is two
    /// <c>SAR hi,1 / RCR lo,1</c> pairs (<c>image@0x186B3</c>).  Both are plain arithmetic shifts of
    /// the whole 32-bit value.  The loop test is "high word is 0 or -1" on each component, i.e.
    /// <c>-65536 &lt;= v &lt;= 65535</c>.
    /// </remarks>
    private static (short X, short Y, short Z) NormalizeDeltas(int dx, int dy, int dz)
    {
        static bool Fits(int v) => (v >> 16) is 0 or -1;        // image@0x18677 or dx,dx / cmp dx,-1

        while (!(Fits(dx) && Fits(dy) && Fits(dz)))
        {
            dx >>= 8;
            dy >>= 8;
            dz >>= 8;
        }

        return (unchecked((short)(dx >> 2)), unchecked((short)(dy >> 2)), unchecked((short)(dz >> 2)));
    }

    /// <summary>
    /// <c>bearing_angle_compute @image@0x185E6</c> — the 2-D compass bearing FROM
    /// <paramref name="from"/> TO <paramref name="to"/>, in 1/8° units.
    /// </summary>
    /// <remarks>
    /// <c>atan2_bam(dX, dZ)</c> then the East→North correction <c>-0x2D0</c> with a single
    /// <c>+0xB40</c> wrap on underflow (<c>image@0x18640..0x18645</c>).  Only X and Z participate.
    /// </remarks>
    /// <param name="from">The observer.</param>
    /// <param name="to">The observed.</param>
    /// <returns>The bearing, in <c>[0, 0xB40)</c>.</returns>
    public static short Bearing2d(CombatPosition from, CombatPosition to)
    {
        (short sx, _, short sz) = NormalizeDeltas(
            unchecked(to.X - from.X), 0, unchecked(to.Z - from.Z));

        short a = unchecked((short)Atan2Table.Atan2Bam(sx, sz).Units);   // image@0x1863B
        a = unchecked((short)(a - Angle.QuarterCircle));                 // image@0x18640 sub ax,0x2d0
        if (a < 0)                                                       // image@0x18643 jns
        {
            a = unchecked((short)(a + Angle.FullCircle));                // image@0x18645 add ax,0xb40
        }

        return a;
    }

    /// <summary>
    /// <c>bearing_angle_3d_compute @image@0x1870C</c> — the ELEVATION angle from
    /// <paramref name="from"/> to <paramref name="to"/>: <c>atan2(horizontal range, Δy)</c>, with no
    /// compass correction, so level reads <c>0x2D0</c>.
    /// </summary>
    /// <remarks>
    /// The horizontal range is <c>isqrt32(Δx² + Δz²)</c>'s INTEGER part
    /// (<c>lcall isqrt32</c> @<c>image@0x18799</c> then <c>mov ax,dx</c> — the high word of the
    /// 16.16 result), and it is fed as <c>atan2</c>'s ADJACENT with <c>Δy</c> as the OPPOSITE.
    /// </remarks>
    /// <param name="from">The observer.</param>
    /// <param name="to">The observed.</param>
    /// <returns>The elevation, in <c>[0, 0xB40)</c>.</returns>
    public static short Elevation3d(CombatPosition from, CombatPosition to)
    {
        (short sx, short sy, short sz) = NormalizeDeltas(
            unchecked(to.X - from.X), unchecked(to.Y - from.Y), unchecked(to.Z - from.Z));

        int squares = unchecked(sx * sx + sz * sz);             // image@0x18789..0x18795
        ushort range = Isqrt32.IntegerPart(Isqrt32.Sqrt16x16((uint)squares));
        return unchecked((short)Atan2Table.Atan2Bam(unchecked((short)range), sy).Units);
    }

    /// <summary>
    /// <c>bearing_range_abs_pos_compute @image@0x1864E</c> — heading AND elevation in one pass,
    /// which the original returns through two near out-pointers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reached through the far thunk <c>radar_bearing_range_farptr_unpack @image@0x15A62</c>, which
    /// unpacks two far pointers into the fourteen stack words this routine wants
    /// (<c>combat_spawn_angle_step</c>'s only call, <c>image@0x02EA7</c>).
    /// </para>
    /// <para>
    /// Structurally it is <see cref="Bearing2d"/> and <see cref="Elevation3d"/> sharing one
    /// normalisation, and the shared scaling is exactly why calling the two separately can differ:
    /// a large Δy would shift X and Z further here than <see cref="Bearing2d"/> alone would.  The
    /// port keeps the single-pass shape.
    /// </para>
    /// </remarks>
    /// <param name="from">The observer.</param>
    /// <param name="to">The observed.</param>
    /// <returns>The heading/elevation pair.</returns>
    public static CombatBearing BearingAndElevation(CombatPosition from, CombatPosition to)
    {
        (short sx, short sy, short sz) = NormalizeDeltas(
            unchecked(to.X - from.X), unchecked(to.Y - from.Y), unchecked(to.Z - from.Z));

        short heading = unchecked((short)Atan2Table.Atan2Bam(sx, sz).Units);   // image@0x186D0
        heading = unchecked((short)(heading - Angle.QuarterCircle));
        if (heading < 0)
        {
            heading = unchecked((short)(heading + Angle.FullCircle));
        }

        int squares = unchecked(sz * sz + sx * sx);             // image@0x186E3..0x186EF
        ushort range = Isqrt32.IntegerPart(Isqrt32.Sqrt16x16((uint)squares));
        short elevation = unchecked((short)Atan2Table.Atan2Bam(unchecked((short)range), sy).Units);

        return new CombatBearing(heading, elevation);
    }

    /// <summary>
    /// <c>combat_bearing_cone_check @image@0x0374A</c> — is <paramref name="target"/> inside
    /// <paramref name="shooter"/>'s aiming cone of half-angle <paramref name="coneUnits"/>?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two gates, elevation first then heading, each measured as the wrapped absolute difference
    /// between the shooter's own orientation word and the bearing to the target, folded at the half
    /// circle (<c>if (d &gt; 0x5A0) d = 0xB40 - d</c>, <c>image@0x037A6</c> / <c>0x0381E</c>) and
    /// compared <c>&gt;</c> against the cone (both <c>jle</c>-guarded).
    /// </para>
    /// <para>
    /// Between them sits a straight-up/straight-down ESCAPE: if the 3-D elevation bearing is within
    /// <c>0x50</c> (10°) of either <c>0x2D0</c> or <c>0x870</c> the routine returns TRUE without
    /// ever testing heading (<c>image@0x037BC..0x037DA</c>) — heading is meaningless there.
    /// </para>
    /// </remarks>
    /// <param name="shooter">The shooter's position.</param>
    /// <param name="shooterHeading">The shooter's <c>+0x12</c> orientation word.</param>
    /// <param name="shooterElevation">The shooter's <c>+0x14</c> orientation word.</param>
    /// <param name="target">The target's position.</param>
    /// <param name="coneUnits">The cone's half-angle — the weapon class's <c>+0x0E</c>.</param>
    /// <returns><c>true</c> when the target is inside the cone.</returns>
    public static bool BearingConeCheck(
        CombatPosition shooter,
        short shooterHeading,
        short shooterElevation,
        CombatPosition target,
        short coneUnits)
    {
        short bearing3d = Elevation3d(shooter, target);         // image@0x0378F

        short d = unchecked((short)(bearing3d - shooterElevation));   // image@0x03799 sub / 0x0379D neg
        d = unchecked((short)-d);
        if (d < 0)
        {
            d = unchecked((short)-d);                           // image@0x0379F cwd/xor/sub = abs16
        }

        if (d > Angle.HalfCircle)                               // image@0x037A6 cmp si,0x5a0 / jle
        {
            d = unchecked((short)(Angle.FullCircle - d));       // image@0x037AC
        }

        if (d > coneUnits)                                      // image@0x037B3 cmp si,[bp+4] / jle
        {
            return false;                                       // image@0x037B8 sub al,al
        }

        short up = unchecked((short)(bearing3d - Angle.QuarterCircle));   // image@0x037BF
        if (up < 0)
        {
            up = unchecked((short)-up);
        }

        if (up < 0x50)                                          // image@0x037C7 cmp ax,0x50 / jl
        {
            return true;
        }

        short down = unchecked((short)(bearing3d - Angle.ThreeQuarterCircle));   // image@0x037CF
        if (down < 0)
        {
            down = unchecked((short)-down);
        }

        if (down < 0x50)                                        // image@0x037D7 cmp ax,0x50 / jl
        {
            return true;
        }

        short bearing2d = Bearing2d(shooter, target);           // image@0x03807

        short h = unchecked((short)(shooterHeading - bearing2d));   // image@0x03811..0x03815
        if (h < 0)
        {
            h = unchecked((short)-h);                           // image@0x03817 abs16
        }

        if (h > Angle.HalfCircle)                               // image@0x0381E
        {
            h = unchecked((short)(Angle.FullCircle - h));
        }

        // image@0x0382B cmp si,[bp+4] / jg 0x37B8 (the FALSE arm)
        return h <= coneUnits;
    }

    /// <summary>
    /// <c>angle_distance_xyz_accum @image@0x2084A</c> — advance a position by
    /// <paramref name="distance"/> units along the direction
    /// (<paramref name="heading"/>, <paramref name="elevation"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The projectile flight step.  Two régimes, split at <c>distance &gt; 0x4000</c>
    /// (<c>image@0x02086C</c>, an UNSIGNED <c>ja</c> on the low word once the high word is zero):
    /// </para>
    /// <list type="bullet">
    ///   <item><b>PATH A</b> (small): the low word is rotated directly and the three components are
    ///     added as sign-extended 16-bit values.</item>
    ///   <item><b>PATH B</b> (large): the original reads <c>word [bp+7]</c> — the UNALIGNED word
    ///     straddling the 32-bit argument's bytes 1..2, i.e. <c>(distance &gt;&gt; 8) &amp;
    ///     0xFFFF</c> (<c>image@0x0208D2</c>) — rotates that, and shifts each component back left by
    ///     8 before accumulating.  The quirk is real and load-bearing: the rotation loses the low
    ///     8 bits, so a fast projectile's per-frame step is quantised to 256 world units.</item>
    /// </list>
    /// <para>
    /// A negative <paramref name="distance"/>, or an exactly zero one, is a no-op
    /// (<c>image@0x020850..0x020861</c>).  A zero <paramref name="elevation"/> skips the Y term
    /// entirely — the original never even zeroes the scratch (<c>image@0x020879 je</c>).
    /// </para>
    /// </remarks>
    /// <param name="position">The position to advance.</param>
    /// <param name="distance">The signed 32-bit step length.</param>
    /// <param name="elevation">The elevation orientation word.</param>
    /// <param name="heading">The heading orientation word.</param>
    /// <returns>The advanced position.</returns>
    public static CombatPosition AccumulateDistance3d(
        CombatPosition position, int distance, short elevation, short heading)
    {
        if (distance <= 0)                                      // image@0x020850..0x020861
        {
            return position;
        }

        int x = position.X;
        int y = position.Y;
        int z = position.Z;

        bool pathB = distance > 0x4000;                         // image@0x020864..0x020871

        // PATH A takes the value's LOW word; PATH B the unaligned word at byte offset 1.
        short seed = pathB
            ? unchecked((short)(distance >> 8))                 // image@0x0208D2 mov ax,[bp+7]
            : unchecked((short)distance);                       // image@0x020873 mov ax,[bp+6]

        if (elevation != 0)                                     // image@0x020879 / 0x0208D8
        {
            short px = seed;
            short py = 0;
            Vector2Rotate.RotateInPlace(elevation, 0, 0, ref px, ref py);
            int dy = py;                                        // cwd — sign-extended
            y = unchecked(y + (pathB ? dy << 8 : dy));
            seed = px;                                          // the rotated X feeds the heading step
        }

        short hx = 0;
        short hz = seed;
        Vector2Rotate.RotateInPlace(heading, 0, 0, ref hx, ref hz);

        int dx = hx;
        int dz = hz;
        x = unchecked(x + (pathB ? dx << 8 : dx));              // image@0x0208C5 / 0x020932
        z = unchecked(z + (pathB ? dz << 8 : dz));              // image@0x020947

        return new CombatPosition(x, y, z);
    }
}
