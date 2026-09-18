using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Combat.Geometry;

/// <summary>
/// <c>gfx_2d_rotate_vec_around_pivot @image@0x15B31</c> — rotate a 2-D integer point about a pivot by a
/// 1/8°-unit angle through the engine's ×4 trig tables and a 32×32→32 <c>(A·B) &gt;&gt; 16</c> multiply.
/// </summary>
/// <remarks>
/// <para>
/// The custom-mission builder calls it twice: once per clause to step the formation origin 5,000 ft
/// along the clause-spacing heading (<c>image@0x28117</c>) and once per enemy to rotate its authored
/// formation offset into world space by the formation heading (<c>image@0x28293</c>).  Both push the
/// pivot pointer <c>DS:0x0680</c>, which is <c>{0, 0}</c> in the original's unpacked image
/// (<c>image@0x3C3E0</c>) — the rotations are about the origin.
/// </para>
/// <para>
/// <b>Why this is not <see cref="Vector2Rotate"/>.</b>  Both are the same mathematical CCW rotation and
/// both use the same bit-15 rounding, but they round at different points:
/// <c>angle_vec2_rotate_inplace @image@0x184DE</c> rounds EACH 16×16 product to a word and then adds,
/// while this routine keeps both products in 32 bits, adds, and rounds ONCE
/// (<c>sub ax,si / sbb dx,di</c> @<c>image@0x15B9F</c>, then
/// <c>test byte [bp-0x13],0x80 / inc [bp-0x12]</c> @<c>image@0x15BD9</c>).  They differ by ±1, so the
/// port carries both rather than aliasing one to the other.
/// </para>
/// <para>
/// Source of truth: the original's bytes <c>image@0x15B31..0x15C06</c> read. The
/// callee <c>image@0x187CA</c> is a sign-magnitude 32×32 multiply returning <c>(A·B) &gt;&gt; 16</c> (the
/// decode's PROPOSE <c>mul32_shr16</c>).
/// </para>
/// </remarks>
public static class Vector2RotateAroundPivot
{
    /// <summary>
    /// The pivot both custom-mission call sites pass: DGROUP <c>0x0680</c>, whose four bytes are zero
    /// in the original's unpacked image (<c>image@0x3C3E0</c>).
    /// </summary>
    public const int OriginPivotDgroupOffset = 0x0680;

    /// <summary>Rotates <paramref name="x"/>/<paramref name="y"/> about a pivot, in place.</summary>
    /// <param name="angleUnits">
    /// The rotation in 1/8° units; wrapped into <c>[0, 0xB40)</c> first
    /// (<c>angle_wrap_0_to_0xB40</c> @<c>image@0x15B3C</c>).
    /// </param>
    /// <param name="pivotX">The pivot's X — the original's <c>[bp+8][0]</c>.</param>
    /// <param name="pivotY">The pivot's Y — the original's <c>[bp+8][1]</c>.</param>
    /// <param name="x">The point's X, updated in place (<c>[bp+0xA][0]</c>).</param>
    /// <param name="y">The point's Y, updated in place (<c>[bp+0xA][1]</c>).</param>
    public static void RotateInPlace(
        short angleUnits, short pivotX, short pivotY, ref short x, ref short y)
    {
        Angle angle = Angle.Wrap(angleUnits);                                 // image@0x15B3C
        int cos4 = TrigTables.NavCosX4(angle);                              // image@0x15B44 angle_cos_x4
        int sin4 = TrigTables.NavSinX4(angle);                              // image@0x15B52 angle_sin_x4

        int deltaX = unchecked((short)(x - pivotX)) << 16;                  // image@0x15B65..0x15B6C
        int deltaY = unchecked((short)(y - pivotY)) << 16;                  // image@0x15B6F..0x15B7A

        // image@0x15B89 / 0x15B9A / 0x15B9F — NX32 = (sin4·dX)>>16 − (cos4·dY)>>16.
        int nx = unchecked(Mul32Shr16(sin4, deltaX) - Mul32Shr16(cos4, deltaY));

        // image@0x15BB5 / 0x15BCA / 0x15BCF — NY32 = (cos4·dX)>>16 + (sin4·dY)>>16.
        int ny = unchecked(Mul32Shr16(cos4, deltaX) + Mul32Shr16(sin4, deltaY));

        x = unchecked((short)(HighWordRounded(nx) + pivotX));               // image@0x15BD9..0x15BF6
        y = unchecked((short)(HighWordRounded(ny) + pivotY));               // image@0x15BE2..0x15BFE
    }

    /// <summary>
    /// The original's one-sided rounding: take the 32-bit value's HIGH word, plus one when the low
    /// word's bit 15 is set (<c>test byte [bp-0x13],0x80 / inc [bp-0x12]</c>,
    /// <c>image@0x15BD9..0x15BDF</c>).
    /// </summary>
    /// <param name="value">The unrounded 32-bit product sum.</param>
    /// <returns>The high word, as the original leaves it.</returns>
    public static short HighWordRounded(int value)
    {
        short high = unchecked((short)(value >> 16));
        return (value & 0x8000) != 0 ? unchecked((short)(high + 1)) : high;
    }

    /// <summary>
    /// <c>mul32_shr16 @image@0x187CA</c> — a SIGN-MAGNITUDE 32×32 multiply whose result is
    /// <c>(|a|·|b|) &gt;&gt; 16</c> with the sign re-applied (<c>not/neg</c> on each operand
    /// @<c>image@0x187D8</c>, the parity flag in <c>SI</c>).
    /// </summary>
    /// <remarks>
    /// Sign-magnitude, not an arithmetic shift: a negative product truncates TOWARD ZERO, which an
    /// <c>int</c> <c>&gt;&gt; 16</c> would not do.  The two call shapes here always have one operand
    /// of the form <c>delta &lt;&lt; 16</c>, so the shift is exact and the answer is
    /// <c>±(4·trig·delta)</c>.
    /// </remarks>
    /// <param name="a">The first factor.</param>
    /// <param name="b">The second.</param>
    /// <returns>The product, shifted right 16.</returns>
    public static int Mul32Shr16(int a, int b)
    {
        bool negative = (a < 0) ^ (b < 0);
        ulong magnitude = (ulong)Math.Abs((long)a) * (ulong)Math.Abs((long)b);
        int result = unchecked((int)(uint)(magnitude >> 16));
        return negative ? unchecked(-result) : result;
    }
}
