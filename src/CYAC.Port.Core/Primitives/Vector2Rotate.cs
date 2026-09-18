namespace CYAC.Port.Core.Primitives;

/// <summary>
/// <c>angle_vec2_rotate_inplace @image@0x184DE</c> — rotate a 2-D integer point about a pivot by a
/// 1/8°-unit angle, using the game's own doubled sine/cosine table entries and its own rounding.
/// </summary>
/// <remarks>
/// <para>
/// INT-only: it is the inner step of <c>angle_distance_xyz_accum</c>, which is how every
/// projectile's position advances, so it decides hits.
/// </para>
/// <para>
/// Source of truth: the bytes at <c>image@0x184DE..0x18575</c>.  The two table lookups are <c>angle_cos_table_lookup @image@0x18346</c> and
/// <c>angle_sin_table_lookup @image@0x18394</c>, already ported as <see cref="TrigTables.NavCos"/> /
/// <see cref="TrigTables.NavSin"/>.
/// </para>
/// </remarks>
public static class Vector2Rotate
{
    /// <summary>
    /// The original's rounding of a 16×16 signed product: <c>imul</c> into <c>DX:AX</c>, one
    /// <c>SHL AX,1 / RCL DX,1</c>, then <c>or ax,ax / jns / inc dx</c> — i.e. keep the HIGH word of
    /// the doubled product and round up when the discarded low half has its sign bit set.
    /// </summary>
    /// <remarks>
    /// <c>image@0x18527..0x18531</c> and its three siblings.  Written out because the "+1" is not a
    /// symmetric round-to-nearest: it triggers on the low word's bit15, i.e. on
    /// <c>(product·2) &amp; 0x8000</c>, for negative products too.
    /// </remarks>
    /// <param name="a">The first signed factor.</param>
    /// <param name="b">The second signed factor.</param>
    /// <returns>The rounded high word, as the original leaves it in <c>DX</c>.</returns>
    public static short RoundedProductHigh(short a, short b)
    {
        int doubled = unchecked(a * b * 2);
        short high = unchecked((short)(doubled >> 16));
        short low = unchecked((short)doubled);
        return low < 0 ? unchecked((short)(high + 1)) : high;
    }

    /// <summary>
    /// Rotate <c>(x, y)</c> about <c>(pivotX, pivotY)</c> by <paramref name="angleUnits"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The original takes three stack words — the angle, a near pointer to the PIVOT pair and a near
    /// pointer to the point pair it updates in place — and normalises the angle with ONE add and ONE
    /// subtract of a full circle (<c>image@0x184E6..0x184F6</c>), not a loop: an angle further out
    /// than one circle stays out.  Reproduced.
    /// </para>
    /// <para>
    /// The scale factors are <c>2·cos</c> and <c>2·sin</c> (<c>shl ax,1</c> after each lookup,
    /// <c>image@0x184FF</c> / <c>0x1850A</c>), and the two scratch words the original stages the
    /// deltas in are <c>[0x077A]</c> / <c>[0x077C]</c> — DGROUP scratch no trace window carries and
    /// nothing else reads.
    /// </para>
    /// </remarks>
    /// <param name="angleUnits">The rotation, in 1/8° units; may be negative or slightly over a circle.</param>
    /// <param name="pivotX">The pivot's X — the original's <c>[si]</c>.</param>
    /// <param name="pivotY">The pivot's Y — the original's <c>[si+2]</c>.</param>
    /// <param name="x">The point's X — the original's <c>[bx]</c>, updated in place.</param>
    /// <param name="y">The point's Y — the original's <c>[bx+2]</c>, updated in place.</param>
    public static void RotateInPlace(
        short angleUnits, short pivotX, short pivotY, ref short x, ref short y)
    {
        short a = angleUnits;

        if (a < 0)                                              // image@0x184E6 or si,si / jns
        {
            a = unchecked((short)(a + Angle.FullCircle));       // image@0x184EA add si,0xb40
        }

        if (a >= Angle.FullCircle)                              // image@0x184EE cmp si,0xb40 / jl
        {
            a = unchecked((short)(a - Angle.FullCircle));       // image@0x184F4 sub si,0xb40
        }

        short cos2 = unchecked((short)(TrigTables.NavCos(Angle.FromUnits(a)) * 2));   // image@0x184FF
        short sin2 = unchecked((short)(TrigTables.NavSin(Angle.FromUnits(a)) * 2));   // image@0x1850A

        short dx = unchecked((short)(x - pivotX));              // image@0x18516 → [0x077A]
        short dy = unchecked((short)(y - pivotY));              // image@0x1851E → [0x077C]

        // image@0x18524..0x18547: X' = pivotX + round(dx·2sin) − round(2cos·dy)
        short nx = unchecked((short)(RoundedProductHigh(dx, sin2) - RoundedProductHigh(cos2, dy)));
        nx = unchecked((short)(nx + pivotX));

        // image@0x18549..0x1856D: Y' = pivotY + round(2cos·dx) + round(dy·2sin)
        short ny = unchecked((short)(RoundedProductHigh(cos2, dx) + RoundedProductHigh(dy, sin2)));
        ny = unchecked((short)(ny + pivotY));

        x = nx;
        y = ny;
    }
}
