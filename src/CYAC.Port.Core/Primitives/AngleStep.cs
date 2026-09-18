namespace CYAC.Port.Core.Primitives;

/// <summary>
/// The rate-limited turn-toward leaf, <c>angle_step_towards @image@0x02EE3</c>.
/// </summary>
/// <remarks>
/// <para>
/// INT-only: it decides where a projectile and an enemy point, i.e. whether a shot hits, so it is
/// part of the reproducible spine.  All arithmetic is 16-bit and wraps.
/// </para>
/// <para>
/// Source of truth: the bytes at <c>image@0x02EE3..0x02F52</c>.  It lives in <c>Primitives/</c>
/// rather than <c>Sim/Combat/</c> because two callers need it:
/// <c>combat_spawn_angle_step @image@0x02DB8</c> drives both of a projectile's orientation words
/// through <see cref="Toward"/> every frame, and <c>engagement_slot_angle_update</c> needs the
/// same leaf.
/// </para>
/// </remarks>
public static class AngleStep
{
    /// <summary>
    /// The <c>lcall 0x201d:0x825a</c> the step makes (<c>angle_accumulate_wrap @image@0x1842A</c>),
    /// which the port already carries as <see cref="Angle.Add"/>.
    /// </summary>
    private static short Accumulate(short value, short delta) =>
        unchecked((short)new Angle(unchecked((ushort)value)).Add(delta).Units);

    /// <summary>
    /// <c>angle_step_towards @image@0x02EE3</c> — turn <paramref name="current"/> at most
    /// <paramref name="rate"/> units toward <paramref name="target"/>, the short way round.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Register ABI in the original: <c>AX</c> = current, <c>DX</c> = target, <c>BX</c> = rate;
    /// the answer comes back in <c>AX</c>.  The arms are deliberately asymmetric and reproduced
    /// as such:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Rate at or past a full circle</b> (<c>cmp bx,0xb40 / jl</c>
    ///     @<c>image@0x02EED</c>) snaps straight to the target.</item>
    ///   <item><b>Already there</b> (<c>cmp ax,dx / jge</c> then <c>jle 0x2F4B</c>
    ///     @<c>image@0x02F2B</c>) returns <paramref name="current"/> — the original reads back the
    ///     uninitialised local <c>[bp-8]</c>, which the prologue's <c>push ax</c>
    ///     (@<c>image@0x02EEB</c>) happens to have filled with <c>AX</c>.  A shipped
    ///     read-before-write whose value is well-defined only by the frame layout; kept, not
    ///     "fixed".</item>
    ///   <item><b>Within one step</b> snaps to the target — but the test is <c>jle</c> on the
    ///     ascending arm (@<c>image@0x02F03</c>, <c>diff &lt;= rate</c>) and <c>jl</c> on the
    ///     descending one (@<c>image@0x02F33</c>, <c>diff &lt; rate</c>).  An exactly-one-step
    ///     descending turn therefore steps rather than snaps.</item>
    ///   <item><b>The complement gate</b> <c>0xB40 - rate &lt;= diff</c> (@<c>image@0x02F0C</c> /
    ///     <c>0x02F3C</c>, both <c>jle</c>) also snaps: going the other way round would already be
    ///     within one step.</item>
    ///   <item><b>Direction</b> is chosen at the half circle: ascending takes <c>+rate</c> while
    ///     <c>diff &lt; 0x5A0</c> and <c>-rate</c> otherwise (@<c>image@0x02F0E</c>); descending is
    ///     the mirror (@<c>image@0x02F3E</c>).</item>
    /// </list>
    /// </remarks>
    /// <param name="current">The angle now, in 1/8° units.</param>
    /// <param name="target">The angle wanted.</param>
    /// <param name="rate">The maximum units to move this call.</param>
    /// <returns>The new angle.</returns>
    public static short Toward(short current, short target, short rate)
    {
        if (rate >= Angle.FullCircle)                           // image@0x02EED cmp bx,0xb40 / jl
        {
            return target;                                      // image@0x02EF3 mov ax,dx
        }

        if (current < target)                                   // image@0x02EF9 cmp ax,dx / jge
        {
            short diff = unchecked((short)(target - current));  // image@0x02EFF sub bx,ax

            if (diff <= rate)                                   // image@0x02F01 cmp bx,si / jle
            {
                return target;
            }

            if (unchecked((short)(Angle.FullCircle - rate)) <= diff)   // image@0x02F0A cmp ax,bx / jle
            {
                return target;
            }

            // image@0x02F0E cmp bx,0x5a0 / jge 0x2F20 (the NEGATED-rate arm)
            short delta = diff < Angle.HalfCircle ? rate : unchecked((short)-rate);
            return Accumulate(current, delta);
        }

        if (current <= target)                                  // image@0x02F29 cmp ax,dx / jle
        {
            // The `mov ax,[bp-8]` at image@0x02F4B, with [bp-8] still holding the prologue's AX.
            return current;
        }

        short back = unchecked((short)(current - target));      // image@0x02F2F sub bx,dx

        if (back < rate)                                        // image@0x02F31 cmp bx,si / jl
        {
            return target;
        }

        if (unchecked((short)(Angle.FullCircle - rate)) <= back)   // image@0x02F3A cmp ax,bx / jle
        {
            return target;
        }

        // image@0x02F3E cmp bx,0x5a0 / jl 0x2F20 (the NEGATED-rate arm)
        short step = back < Angle.HalfCircle ? unchecked((short)-rate) : rate;
        return Accumulate(current, step);
    }
}
