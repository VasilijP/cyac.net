using System.Runtime.CompilerServices;
using CYAC.Port.Core.Model.Cockpit;

namespace CYAC.Port.Render.Cockpit;

/// <summary>
/// A circle fitted to an instrument mask's CLEAR region, in design coordinates.
/// </summary>
/// <param name="X">Centre X (design pixels, absolute).</param>
/// <param name="Y">Centre Y (design pixels, absolute).</param>
/// <param name="Radius">Radius, design pixels — to the pixel-edge boundary, so a 1991 pixel that was clear is inside.</param>
/// <remarks>
/// <para>
/// The aircraft's <c>_horiz</c> mask (<c>data/images/masks/&lt;nn&gt;_horiz.png</c>) is what
/// <c>gfx_masked_blit @image@0x1D162</c> puts back over the artificial horizon: its SET bits are the
/// bezel, its CLEAR bits the window.  At 320×200 the window is a small circle drawn on a pixel grid;
/// scaled six times it is a staircase.  This type reads the clear pixels once, takes their centroid
/// (unused for the fit itself), takes the clear region's SILHOUETTE — per row the leftmost and
/// rightmost clear pixel, per column the topmost and bottommost — fits a least-squares circle to it,
/// refits over the points within a pixel of that circle (which drops a DOME window's flat cut — the
/// P-51's and FW-190's), and accepts the result only when it is round — the relative residual under
/// <see cref="MaxRelativeSpread"/> — so a mask that is not a circle keeps its bitmap.  The renderer
/// keeps the bitmap's cut where it lies well inside the circle, so a dome stays a dome and the
/// aircraft reference post stays a post.
/// </para>
/// <para>
/// Fits are cached per mask instance, so the per-frame cost is a dictionary lookup.
/// </para>
/// </remarks>
public readonly record struct InstrumentCircle(double X, double Y, double Radius)
{
    /// <summary>The largest boundary-distance spread (standard deviation / mean) accepted as "round".</summary>
    public const double MaxRelativeSpread = 0.06;

    private static readonly ConditionalWeakTable<CockpitMask, StrongBox<InstrumentCircle?>> Cache = new();

    /// <summary>
    /// The circle fitted to the mask's clear region inside <paramref name="rect"/>, or
    /// <see langword="null"/> when the region is not round enough.
    /// </summary>
    /// <param name="mask">The instrument mask (region-relative coordinates).</param>
    /// <param name="rect">The region rectangle the mask sits in (absolute design coordinates).</param>
    public static InstrumentCircle? Fit(CockpitMask mask, PanelRect rect)
    {
        ArgumentNullException.ThrowIfNull(mask);
        StrongBox<InstrumentCircle?> box = Cache.GetValue(mask, m => new StrongBox<InstrumentCircle?>(Compute(m, rect)));
        return box.Value;
    }

    private static InstrumentCircle? Compute(CockpitMask mask, PanelRect rect)
    {
        int w = Math.Min(mask.Width, rect.Width);
        int h = Math.Min(mask.Height, rect.Height);
        long count = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                count += mask.At(x, y) ? 0 : 1;
            }
        }

        if (count < 12)
        {
            return null;
        }

        // The clear region's SILHOUETTE: per row its leftmost and rightmost clear pixel, per column
        // its topmost and bottommost.  A set island inside the window (the aircraft reference post
        // the P-51 and FW-190 masks carry in the middle, joined to the bezel at the bottom) never
        // reaches the silhouette, so it cannot pull the fit.
        HashSet<(double X, double Y)> boundary = new HashSet<(double X, double Y)>();
        for (int y = 0; y < h; y++)
        {
            int first = -1, last = -1;
            for (int x = 0; x < w; x++)
            {
                if (!mask.At(x, y))
                {
                    if (first < 0)
                    {
                        first = x;
                    }

                    last = x;
                }
            }

            if (first >= 0)
            {
                boundary.Add((first + 0.5, y + 0.5));
                boundary.Add((last + 0.5, y + 0.5));
            }
        }

        for (int x = 0; x < w; x++)
        {
            int first = -1, last = -1;
            for (int y = 0; y < h; y++)
            {
                if (!mask.At(x, y))
                {
                    if (first < 0)
                    {
                        first = y;
                    }

                    last = y;
                }
            }

            if (first >= 0)
            {
                boundary.Add((x + 0.5, first + 0.5));
                boundary.Add((x + 0.5, last + 0.5));
            }
        }

        if (boundary.Count < 8)
        {
            return null;
        }

        // Two of the six shipped windows (P-51, FW-190) are DOMES — a circle with a flat cut and a
        // post — so the fit is done twice: a least-squares circle (Kåsa) over the whole silhouette,
        // then again over the points within a pixel of it, which drops the cut.  The bitmap still
        // cuts the flat edge and the post in the renderer; only the rim is analytic.
        List<(double X, double Y)> points = boundary.ToList();
        if (!Kasa(points, out double fx, out double fy, out double radius))
        {
            return null;
        }

        List<(double X, double Y)> kept = points.Where(b => Math.Abs(Math.Sqrt(((b.X - fx) * (b.X - fx)) + ((b.Y - fy) * (b.Y - fy))) - radius) <= 1.0).ToList();
        if (kept.Count < 8 || kept.Count < points.Count / 2 || !Kasa(kept, out fx, out fy, out radius))
        {
            return null;
        }

        List<double> residual = kept.Select(b => Math.Sqrt(((b.X - fx) * (b.X - fx)) + ((b.Y - fy) * (b.Y - fy))) - radius).ToList();
        double spread = Math.Sqrt(residual.Sum(r => r * r) / residual.Count) / Math.Max(radius, 1e-9);
        if (spread > MaxRelativeSpread || radius < 2.0)
        {
            return null;
        }

        // The boundary pixels' CENTRES lie half a pixel inside the true edge.
        return new InstrumentCircle(rect.X + fx, rect.Y + fy, radius + 0.5);
    }

    /// <summary>Kåsa's algebraic least-squares circle: minimises Σ (x²+y²+Dx+Ey+F)².</summary>
    private static bool Kasa(List<(double X, double Y)> pts, out double cx, out double cy, out double r)
    {
        double n = pts.Count;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0, sz = 0;
        foreach ((double x, double y) in pts)
        {
            double z = (x * x) + (y * y);
            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y; sxz += x * z; syz += y * z; sz += z;
        }

        // Normal equations for (D, E, F): [sxx sxy sx; sxy syy sy; sx sy n] · v = −[sxz; syz; sz].
        double a11 = sxx, a12 = sxy, a13 = sx, a22 = syy, a23 = sy, a33 = n;
        double b1 = -sxz, b2 = -syz, b3 = -sz;
        double det = (a11 * ((a22 * a33) - (a23 * a23))) - (a12 * ((a12 * a33) - (a23 * a13))) + (a13 * ((a12 * a23) - (a22 * a13)));
        cx = cy = r = 0;
        if (Math.Abs(det) < 1e-9)
        {
            return false;
        }

        double d = ((b1 * ((a22 * a33) - (a23 * a23))) - (a12 * ((b2 * a33) - (a23 * b3))) + (a13 * ((b2 * a23) - (a22 * b3)))) / det;
        double e = ((a11 * ((b2 * a33) - (a23 * b3))) - (b1 * ((a12 * a33) - (a23 * a13))) + (a13 * ((a12 * b3) - (b2 * a13)))) / det;
        double f = ((a11 * ((a22 * b3) - (b2 * a23))) - (a12 * ((a12 * b3) - (b2 * a13))) + (b1 * ((a12 * a23) - (a22 * a13)))) / det;
        cx = -d / 2.0;
        cy = -e / 2.0;
        double rr = (cx * cx) + (cy * cy) - f;
        if (rr <= 0)
        {
            return false;
        }

        r = Math.Sqrt(rr);
        return true;
    }
}
