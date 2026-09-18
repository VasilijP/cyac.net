using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Flight;

namespace CYAC.Port.Core.Sim.Geometry;

/// <summary>
/// <c>angle_mat3_pair_build @image@0x1BFA4</c> — builds TWO paired Q14 rotation matrices from one
/// Euler triple in a single pass over six trig lookups.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  This is a RENDERER-SHARED leaf with four doors, none of them presentation-only:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>angle_3d_orientation_combine @image@0x1C203</c> and <c>@image@0x1C244</c> — the aim/camera
/// combiner's phases 4 and 6 (<see cref="OrientationCombine"/>).  Both read only
/// <see cref="Pair.Full"/>; <see cref="Pair.ElevationBank"/> is written and never read there.
/// </description></item>
/// <item><description>
/// <c>mesh_transform_precompute_b @image@0x16F56</c> — the per-mesh rotation cache, which keeps
/// <see cref="Pair.Full"/> in <c>g_per_mesh_mat3_buf [0xE842]</c> and
/// <see cref="Pair.ElevationBank"/> in <c>g_per_mesh_mat3_partial [0xE854]</c>.  That door is the
/// renderer's; it is named here so the port's own renderer can reuse this routine unchanged.
/// </description></item>
/// </list>
/// <para>
/// Source of truth: the bytes at <c>image@0x1BFA4..0x1C121</c> (382 B), entered at
/// <c>0x1BFA4</c>.  There are no conditional branches after the three
/// per-axis wrap tests, no division, and no rounding compensation anywhere.
/// </para>
/// <para>
/// <b>THE NAV-vs-MATH TRAP, settled by LCALL TARGET.</b> Each axis makes two calls, and the order
/// matters more than either name: FIRST <c>image@0x18394</c>, SECOND <c>image@0x18346</c>.
/// <c>image@0x1839B</c> is <c>add bx,0x2D0</c>, so <c>0x18394(θ) == 0x18346(θ + 90°)</c>. Both
/// descriptions are of the same bytes.  This file picks the MATH convention once and binds by
/// address: the FIRST call is <see cref="TrigTables.MathCos"/> (≡ <c>NavSin</c> ≡
/// <c>image@0x18394</c>) and the SECOND is <see cref="TrigTables.MathSin"/> (≡ <c>NavCos</c> ≡
/// <c>image@0x18346</c>).  A port that mixes the conventions loses 90°.
/// </para>
/// <para>
/// The reference formulae this file reproduces are exactly the ones the difftest registry proved
/// behaviourally against the original's bytes (<c>src/CYAC.Tools.SlrUnpacker/DiffTest.cs</c> #27,
/// <c>angle_mat3_pair_build</c>), and C8's own bit-exact corpus (§oracle 1) closes the ±4 Q14
/// tolerance that test had to carry.
/// </para>
/// </remarks>
public static class Mat3PairBuild
{
    /// <summary>The two matrices one call produces — the original's <c>out_a</c> and <c>out_b</c>.</summary>
    /// <param name="ElevationBank">
    /// <c>out_a</c>, the original's <c>DI</c> argument (<c>[bp+0xA]</c>): the PARTIAL matrix, built
    /// from the elevation and bank axes only — it does not mention the heading at all.  Word
    /// <c>[+0x0C]</c> is explicitly zeroed (<c>image@0x1C021</c>).
    /// </param>
    /// <param name="Full">
    /// <c>out_b</c>, the original's <c>SI</c> argument (<c>[bp+0xC]</c>): the FULL heading × elevation ×
    /// bank matrix.  Three of its words are stores SHARED with <paramref name="ElevationBank"/> —
    /// <c>[+2]</c> (<c>image@0x1C04D</c>), <c>[+8]</c> (<c>image@0x1C038</c>) and <c>[+0xE]</c>
    /// (<c>image@0x1BFF4</c>).
    /// </param>
    public readonly record struct Pair(Mat3Q14 ElevationBank, Mat3Q14 Full);

    /// <summary>
    /// Runs the whole routine.  The three angles are RAW 16-bit words, exactly as the original's
    /// stack slots hold them: each is negated and conditionally wrapped before its lookups.
    /// </summary>
    /// <param name="bank">The original's <c>[bp+4]</c>, processed THIRD (<c>image@0x1BFFA</c>).</param>
    /// <param name="elevation">Its <c>[bp+6]</c>, processed SECOND (<c>image@0x1BFCF</c>).</param>
    /// <param name="heading">Its <c>[bp+8]</c>, processed FIRST (<c>image@0x1BFAF</c>).</param>
    /// <returns>The pair of matrices the original writes through its two near pointers.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An angle whose negate-and-wrap lands outside <c>[0, 0xB40]</c> — see
    /// <see cref="NegateAndWrap"/>.
    /// </exception>
    public static Pair Build(short bank, short elevation, short heading)
    {
        // image@0x1BFAF..0x1BFF9 / 0x1BFCF..0x1BFF9 / 0x1BFFA..0x1C020 — three identical per-axis
        // blocks: NEG + conditional +0xB40, then the two lookups by LCALL TARGET.  The scratch words
        // the original parks them in ([0xCEC]..[0xCF8]) are function-private and outside every
        // combat-trace DGROUP window, so the port keeps them in locals.
        Angle h = new Angle((ushort)NegateAndWrap(heading, nameof(heading)));
        Angle e = new Angle((ushort)NegateAndWrap(elevation, nameof(elevation)));
        Angle b = new Angle((ushort)NegateAndWrap(bank, nameof(bank)));

        short cosH = TrigTables.MathCos(h);   // [0xCEC], LCALL image@0x18394   (image@0x1BFBC)
        short sinH = TrigTables.MathSin(h);   // [0xCEE], LCALL image@0x18346   (image@0x1BFC7)
        short cosE = TrigTables.MathCos(e);   // [0xCF0]                        (image@0x1BFDC)
        short sinE = TrigTables.MathSin(e);   // [0xCF2]                        (image@0x1BFEA)
        short cosB = TrigTables.MathCos(b);   // [0xCF4]                        (image@0x1C007)
        short sinB = TrigTables.MathSin(b);   // [0xCF6]                        (image@0x1C014)

        // The twelve Q14 products.  Each is IMUL 16x16 -> 32 followed by `shl ax,1 / rcl dx,1` TWICE
        // and a store of DX only — a straight << 2 of the 32-bit product with NO round-to-nearest
        // (this function has no `or ax,ax ; jns ; inc dx` anywhere).  Taking the
        // high word of (p << 2) is bit-for-bit `(short)(p >> 14)`; see Q14 below.
        short ceSb = Q14(cosE * sinB);        // image@0x1C03B — out_a[1] == out_b[1]  (SHARED)
        short seSb = Q14(sinE * sinB);        // image@0x1C0A4 — out_a[2], and the BP carry
        short ceCb = Q14(cosE * cosB);        // image@0x1C026 — out_a[4] == out_b[4]  (SHARED)
        short seCb = Q14(sinE * cosB);        // image@0x1C050 — out_a[5], and the BP carry
        short shCe = Q14(sinH * cosE);        // image@0x1C0F7 — out_b[6]
        short chCe = Q14(cosH * cosE);        // image@0x1C109 — out_b[8]

        Mat3Q14 partial = new Mat3Q14();
        partial[0] = unchecked((ushort)cosB);                  // image@0x1C00F
        partial[1] = unchecked((ushort)ceSb);                  // image@0x1C04D
        partial[2] = unchecked((ushort)seSb);                  // image@0x1C0B3
        partial[3] = unchecked((ushort)-sinB);                 // image@0x1C01E (NEG then store)
        partial[4] = unchecked((ushort)ceCb);                  // image@0x1C035
        partial[5] = unchecked((ushort)seCb);                  // image@0x1C05F
        partial[6] = 0;                                        // image@0x1C021 (mov word [di+0xC],0)
        partial[7] = unchecked((ushort)-sinE);                 // image@0x1BFF7
        partial[8] = unchecked((ushort)cosE);                  // image@0x1BFE4

        // The four cross terms accumulate in 32 bits BEFORE the << 2 (ADD/ADC or SUB/SBB on the raw
        // IMUL products), so the port sums ints and calls Q14 once — same bits, wrapping mod 2^32.
        Mat3Q14 full = new Mat3Q14();
        full[0] = unchecked((ushort)Q14((seSb * sinH) + (cosH * cosB)));   // image@0x1C0B8..0x1C0D5
        full[1] = unchecked((ushort)ceSb);                                 // image@0x1C04D (SHARED)
        full[2] = unchecked((ushort)Q14((seSb * cosH) - (sinH * cosB)));   // image@0x1C0D7..0x1C0F4
        full[3] = unchecked((ushort)Q14((seCb * sinH) - (cosH * sinB)));   // image@0x1C064..0x1C081
        full[4] = unchecked((ushort)ceCb);                                 // image@0x1C035 (SHARED)
        full[5] = unchecked((ushort)Q14((seCb * cosH) + (sinH * sinB)));   // image@0x1C084..0x1C0A1
        full[6] = unchecked((ushort)shCe);                                 // image@0x1C106
        full[7] = unchecked((ushort)-sinE);                                // image@0x1BFF4 (SHARED)
        full[8] = unchecked((ushort)chCe);                                 // image@0x1C118

        return new Pair(partial, full);
    }

    /// <summary>
    /// The per-axis <c>NEG</c> + single conditional <c>+0xB40</c> idiom
    /// (<c>image@0x1BFB2..0x1BFB8</c>, repeated verbatim at <c>0x1BFD2</c> and <c>0x1BFFD</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>NEG</c> is 16-bit, so <c>0x8000</c> negates to itself and stays SIGNED-negative; the
    /// wrap is a SINGLE conditional add, not a loop.  The result therefore lands in
    /// <c>[0, 0xB40]</c> only for inputs in <c>[-0xB40, 0xB40]</c>.  Outside that the original
    /// indexes past its 721-entry far-segment table (<c>image@0x1834D</c> onwards) and returns
    /// whatever byte pair follows it in segment <c>0x4438</c> — undefined, un-portable, and not
    /// reproducible without shipping original bytes (the no-original-data rule).  The port refuses
    /// instead of inventing a value; no observed caller reaches it (all four doors of
    /// <see cref="OrientationCombine"/> and the per-mesh cache pass canonical BAMs).
    /// </para>
    /// <para>
    /// <c>0xB40</c> IS reachable — from <c>angle == -0xB40</c>, whose <c>NEG</c> gives <c>+0xB40</c>
    /// and skips the add — and the original handles it CORRECTLY rather than by accident: at index
    /// <c>0xB40</c> the direct fold takes its fourth arm (<c>image@0x18383</c>,
    /// <c>-table[0xB40 - 0xB40] = -table[0] = 0</c>) and the shifted fold reduces
    /// <c>0xB40 + 0x2D0</c> to <c>0x2D0</c> and takes its second arm
    /// (<c>table[0x5A0 - 0x2D0] = table[0x2D0]</c>) — the same two values as index 0.  So the port
    /// folds <c>0xB40</c> to 0, which is an identity, not an approximation.
    /// </para>
    /// </remarks>
    /// <param name="angle">The raw stack word.</param>
    /// <param name="name">The parameter name, for the diagnostic.</param>
    /// <returns>The table index, in <c>[0, 0xB40)</c>.</returns>
    internal static int NegateAndWrap(short angle, string name)
    {
        int negated = unchecked((short)-angle);         // image@0x1BFB2 — 16-bit NEG
        if (negated < 0)
        {
            negated += Angle.FullCircle;                // image@0x1BFB6 — ONE conditional add
        }

        if (negated == Angle.FullCircle)
        {
            return 0;                                   // proven identity, see the remarks
        }

        if ((uint)negated >= Angle.FullCircle)
        {
            throw new ArgumentOutOfRangeException(
                name,
                angle,
                $"angle_mat3_pair_build (image@0x1BFA4) negates and wraps ONCE, so only "
                    + $"[-0x{Angle.FullCircle:X}, 0x{Angle.FullCircle:X}] has a defined answer; "
                    + $"0x{(ushort)angle:X4} lands at 0x{negated:X} and the original would index "
                    + "past its 721-entry trig table in far segment 0x4438.");
        }

        return negated;
    }

    /// <summary>
    /// <c>shl ax,1 / rcl dx,1</c> twice, then keep <c>DX</c> — the high word of the 32-bit product
    /// shifted left by two, i.e. a Q14 × Q14 → Q14 reduction with truncation toward minus infinity.
    /// </summary>
    /// <remarks>
    /// Taking bits <c>[31:16]</c> of <c>(p &lt;&lt; 2)</c> selects bits <c>[29:14]</c> of <c>p</c>,
    /// which is exactly what <c>(short)(p &gt;&gt; 14)</c> selects — so the two are bit-identical
    /// including the wrap of the shift itself.  Nothing here rounds.
    /// </remarks>
    /// <param name="product">The 32-bit accumulator, already wrapped mod 2^32 by any ADD/ADC.</param>
    /// <returns>The stored word.</returns>
    internal static short Q14(int product) => unchecked((short)(product >> 14));
}
