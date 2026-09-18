namespace CYAC.Port.Render.Raster;

/// <summary>
/// The RETRO LOOK's quantiser: an 8×8 ordered (Bayer) threshold matrix in ABSOLUTE screen space, at
/// the host's own pixel pitch.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it quantises.</b>  A fragment carries two independent numbers: the fraction of the pixel's
/// AREA its geometry covers, and its TRANSLUCENCY alpha (the record's <c>popcount(selector)/8</c>
/// times the instance's opacity, times the radial profile for a soft disc).  The retro look
/// quantises the second one and leaves the first alone — so every EDGE in the frame stays analytic
/// while the translucency reads as a screen-space pattern of dots.  <c>alpha ≥ (v + ½)/64</c> lights
/// the pixel, everything else clears it.
/// </para>
/// <para>
/// <b>Why it reproduces the shipped selectors with no special case.</b>  The original's mask is an
/// 8-pixel screen-space pattern indexed by the pixel's own coordinates (<c>image@0x12839</c>:
/// <c>mov ax,[0x4CC] / test di,1 / xchg ah,al</c> picks the row's byte by row PARITY, then
/// <c>and al,7 / xlatb</c> against <c>80 40 20 10 08 04 02 01</c> at DGROUP <c>[0x2F2]</c> picks the
/// bit by column).  For the <c>0x5A</c> selector that is 0xAA on even rows and 0x55 on odd ones —
/// even columns of even rows, odd columns of odd rows.  This matrix at <c>alpha = ½</c> lights
/// exactly <c>v ≤ 31</c>, which is exactly that checkerboard; at <c>¼</c> it lights <c>v ≤ 15</c>,
/// one pixel in four, which is what a two-bits-per-nibble selector draws.  Nothing keys on
/// <see cref="Pipeline.DisplayPrimitive.Pattern"/> — the record's own alpha reproduces its own
/// pattern.
/// </para>
/// <para>
/// <b>At the HOST's pitch, not the original's.</b> A 320×200 cell makes the mask a 5-pixel block at
/// 1080p, which reads as an ugly coarse grid rather than a dither.  Indexing by the absolute host
/// pixel makes the period 2 host pixels at every resolution, which is the retro look at the
/// resolution the host actually has.
/// </para>
/// <para>
/// <b>Absolute coordinates</b> (<c>x &amp; 7</c>, <c>y &amp; 7</c> of the TARGET pixel, never of a
/// tile-relative one), so the pattern cannot move with the tile size or the thread count — the
/// invariance tests of hold under it.
/// </para>
/// </remarks>
internal static class OrderedDither
{
    /// <summary>
    /// The standard dispersed-dot 8×8 Bayer matrix, row-major (<c>[(y &amp; 7) * 8 + (x &amp; 7)]</c>).
    /// </summary>
    /// <remarks>
    /// The recursive construction
    /// <c>B(2n) = [[4·B(n), 4·B(n)+2], [4·B(n)+3, 4·B(n)+1]]</c> interleaved so that each level
    /// bisects the previous one's gaps, from <c>B(2) = [[0, 2], [3, 1]]</c>.  It is a permutation of
    /// 0…63 (<c>RetroDitherTests.TheBayerMatrixIsTheStandardConstruction</c> rebuilds it from the
    /// recursion and compares).
    /// </remarks>
    private static readonly byte[] Threshold =
    [
         0, 32,  8, 40,  2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44,  4, 36, 14, 46,  6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
         3, 35, 11, 43,  1, 33,  9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47,  7, 39, 13, 45,  5, 37,
        63, 31, 55, 23, 61, 29, 53, 21,
    ];

    /// <summary>The matrix's period in target pixels, per axis.</summary>
    public const int Period = 8;

    /// <summary>The matrix's own value at one absolute pixel: 0…63.</summary>
    /// <param name="x">The absolute target column.</param>
    /// <param name="y">The absolute target row.</param>
    public static int ThresholdAt(int x, int y) => Threshold[((y & 7) << 3) | (x & 7)];

    /// <summary>Quantises one fragment's translucency alpha to 0 or 1 at one absolute pixel.</summary>
    /// <param name="x">The absolute target column.</param>
    /// <param name="y">The absolute target row.</param>
    /// <param name="alpha">The fragment's translucency alpha, 0…1.</param>
    /// <returns><c>1.0</c> where the pattern lights the pixel, <c>0.0</c> where it clears it.</returns>
    /// <remarks>
    /// The comparison is written <c>64·alpha ≥ v + ½</c> rather than <c>alpha ≥ (v + ½)/64</c> so
    /// that the 64 thresholds are exact integers-plus-a-half and a coverage of exactly ½ or ¼ lands
    /// on the same side of every one of them at every resolution.
    /// </remarks>
    public static double Quantise(int x, int y, double alpha) =>
        alpha * 64.0 >= ThresholdAt(x, y) + 0.5 ? 1.0 : 0.0;
}
