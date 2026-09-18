namespace CYAC.Port.Core.Primitives;

/// <summary>
/// The game's 32-bit square root: a normalize / bisect / rescale routine returning a 16.16
/// fixed-point result (approximately <c>sqrt(n)·2^16</c>).
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Source of truth: (<c>isqrt32 @image@0x18576</c>, 112 B, FAR, <c>RETF 4</c>), cross-read
/// against the original's bytes at <c>image@0x18576..0x185E2</c>.  Behaviourally verified by
/// <c>src/CYAC.Tools.SlrUnpacker/DiffTest.cs:384</c>.  Its four callers all take the square root of a
/// sum of squares: <c>mesh_proj_frustum_tangent_cache @image@0x165C8</c> (twice),
/// <c>bearing_range_abs_pos_compute @image@0x1864E</c>, <c>bearing_angle_3d_compute @image@0x1870C</c>.
/// </para>
/// <para>
/// The routine is DELIBERATELY approximate: it right-normalizes by whole 2-bit steps (losing the
/// discarded low bits), left-shifts by 12, takes a 16-bit floor square root, then rescales.  The
/// relative error is bounded by 0.768% and is worst at tiny inputs (<c>n = 3</c>).  Callers use it
/// for ranges and frustum tangents, where that is fine — do not "improve" it, the error is part of
/// the simulation.
/// </para>
/// </remarks>
public static class Isqrt32
{
    /// <summary>
    /// Exclusive upper bound of the routine's valid domain: <c>0x8000_0000</c>.
    /// </summary>
    /// <remarks>
    /// The normalization loop's exit test is <c>CMP DI,4 / JL</c> at <c>image@0x18587</c> — a SIGNED
    /// compare of the high word.  Once the high word's sign bit is set the loop reads it as negative
    /// and exits immediately, so nothing is normalized and the result is meaningless
    /// (<c>n = 0x8000_0000</c> returns 0).  This, not precision, is the routine's real wall.
    /// </remarks>
    public const uint MaxInput = 0x8000_0000u;

    /// <summary>
    /// The routine's worst-case relative error over its whole domain, as measured against
    /// <c>Math.Sqrt</c>: 0.768%, attained at <c>n = 3</c>.
    /// </summary>
    public const double MaxRelativeError = 0.00768;

    /// <summary>
    /// Computes <c>sqrt(n)</c> as a 16.16 fixed-point value: the high word is the integer part and
    /// the low word the fraction scaled by 2^16.  The original returns it in <c>DX:AX</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Known results from the decode: <c>1 → 0x0001_0000</c> (1.0), <c>4 → 0x0002_0000</c> (2.0),
    /// <c>2 → 0x0001_6800</c> (≈1.406 for √2 ≈ 1.414).
    /// </para>
    /// <para>
    /// CAUTION — the high word is NOT reliably <c>floor(sqrt(n))</c>, despite what and the difftest
    /// comment say.  It is exact only while no normalization happens (<c>n &lt; 2^18</c>); beyond
    /// that the truncating right-normalize makes it read one low (and, above 2^28, up to two low) for
    /// a large minority of inputs — about a third of the range 2^28..2^31.  Use
    /// <see cref="IntegerPart"/> for the original's value, and do not treat it as an exact integer
    /// square root.
    /// </para>
    /// </remarks>
    /// <param name="n">The radicand, 0 .. <see cref="MaxInput"/> − 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="n"/> is at or above <see cref="MaxInput"/> — see that field for why the
    /// original produces garbage there rather than a large root.
    /// </exception>
    public static uint Sqrt16x16(uint n)
    {
        if (n >= MaxInput)
        {
            throw new ArgumentOutOfRangeException(
                nameof(n),
                n,
                "isqrt32 (image@0x18587) exits its normalize loop on a SIGNED compare of the high "
                    + "word, so inputs at or above 0x80000000 skip normalization entirely and return "
                    + "a meaningless value (0x80000000 itself returns 0).");
        }

        // Phase 1 — right-normalize by 2-bit steps until the high word is below 4.
        // image@0x18581..0x18599.  Bounded at 7 steps because n < 2^31.
        uint value = n;
        int shift = 0;
        while (value >> 16 >= 4)
        {
            value >>= 2;
            shift++;
        }

        // Phase 2 — left-shift by 12 to pack precision into the top bits.  image@0x1859A..0x185A1.
        // value < 2^18 here, so this cannot overflow.
        value <<= 12;

        // Phase 3 — 16-bit binary bisection: keep each probe bit whose square still fits under the
        // normalized radicand.  image@0x185A3..0x185BE.
        //
        // The original compares the product's high word against the radicand's high word with a
        // SIGNED jg/jl and the low words with an unsigned ja.  Within the reachable domain the
        // accepted candidates stay below 0x8000, so their squares stay below 2^30 and the signed and
        // unsigned readings agree; the plain comparison below is therefore faithful.
        int result = 0;
        for (int probe = 0x8000; probe != 0; probe >>= 1)
        {
            int candidate = result + probe;
            uint square = (uint)(candidate * candidate);
            if (square <= value)
            {
                result = candidate;
            }
        }

        // Phase 4 — rescale by 2^(shift − 6).  image@0x185C0..0x185DD.
        //
        // `shift` never exceeds 7, so the left-shift path only ever runs once.  That matters: the
        // original's left-shift loop branches back to its own `NEG CX` (image@0x185D5 -> 0x185CF), so
        // any adjustment beyond a single bit would cycle forever.  The bug is real and unreachable.
        uint packed = (uint)result << 16;
        int adjust = 6 - shift;
        if (adjust > 0)
        {
            packed >>= adjust;
        }
        else if (adjust < 0)
        {
            packed <<= -adjust;
        }

        return packed;
    }

    /// <summary>
    /// The integer part of a packed 16.16 result — the original's <c>DX</c>.
    /// See <see cref="Sqrt16x16"/> for why this is only approximately <c>floor(sqrt(n))</c>.
    /// </summary>
    public static ushort IntegerPart(uint packed) => (ushort)(packed >> 16);

    /// <summary>The fractional part of a packed 16.16 result, scaled by 2^16 — the original's <c>AX</c>.</summary>
    public static ushort Fraction(uint packed) => (ushort)packed;
}
