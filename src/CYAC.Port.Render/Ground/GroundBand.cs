namespace CYAC.Port.Render.Ground;

/// <summary>
/// The GROUND-PLANE BAND builder: H15's "objects with dimensions instead of lines", promoted out of
/// the retired analytic ground layer and into the WHAT stage.
/// </summary>
/// <remarks>
/// <para>
/// A flat <c>y = 0</c> LINE record (a road, a city street, a runway centreline dash, an apron
/// outline) and a flat RIBBON quad (the three linear features that carry an authored width — H15
/// §0) are both drawn as a BAND about a centreline: a strip of a real world width, built IN THE
/// GROUND PLANE, with round caps for a line and square ends for a ribbon.
/// </para>
/// <para>
/// <b>Why it has to be built in the plane, and therefore above the display list.</b>  A screen-space
/// capsule — a strip perpendicular to the PROJECTED segment — is right for a tracer, which has no
/// preferred plane; it is wrong for a road, which is seen almost edge-on and whose width is
/// foreshortened by the grazing angle.  The two constructions differ by exactly that foreshortening,
/// and the difference is worth having: measuring the width the band SUBTENDS instead of the width
/// it HAS is what stops the roads fading out at range.  The band is therefore built from
/// three-dimensional points on the plane and handed to the pipeline as an ordinary polygon: the
/// HOW stage sees a polygon and needs to know nothing about roads.
/// </para>
/// <para>
/// A sub-pixel band is WIDENED to the on-screen floor at full brightness rather than dimmed by its
/// sub-pixel coverage (<see cref="ApplyScreenFloor"/>): the exact-coverage renderer is honest about
/// a thin bright feature and would draw a 48-foot road at 26,000 feet a fifth as bright, where the
/// 1991 renderer — painting every pixel whose centre the polygon touched — kept it crisp to the
/// horizon.  A missing element is a failure.
/// </para>
/// </remarks>
internal sealed class GroundBand
{
    /// <summary>
    /// The largest half-width the on-screen floor may ask for, as a fraction of the view depth:
    /// <b>5 %</b> (about 2.9° of the frame).
    /// </summary>
    /// <remarks>
    /// A numerical bound, not a look: the widening factor a grazing ground band needs diverges as
    /// the band approaches the horizon (where it subtends nothing), and without a bound a road at
    /// the horizon would be widened to hundreds of thousands of feet.  At 26,000 feet the bound is
    /// 1,300 feet of half-width and the road needs 211, so it never binds at any range a road is
    /// actually read at.
    /// </remarks>
    public const double MaxHalfWidthFraction = 0.05;

    /// <summary>Segments per round end cap.</summary>
    /// <remarks>
    /// Eight segments put the worst chord error at <c>1 − cos(π/16) = 1.9 %</c> of the half-width,
    /// which for a 30-foot road at any range where its cap is more than a pixel across is far below
    /// one pixel.  A RIBBON gets no caps at all: its ends are authored and abut the next segment.
    /// </remarks>
    public const int CapSegments = 8;

    /// <summary>The most vertices <see cref="Build"/> can produce.</summary>
    public const int MaxVertices = (2 * CapSegments) + 2;

    private double _focal;
    private double _centreX;
    private double _centreY;
    private double _near;
    private double _floorPixels;
    private Vec3 _planeNormal;

    /// <summary>The ground plane's normal (the world's up axis) expressed in VIEW space.</summary>
    public Vec3 PlaneNormal => _planeNormal;

    /// <summary>Sets the frame's constants.</summary>
    /// <param name="focal">The focal length in target pixels.</param>
    /// <param name="centreX">The viewport centre column.</param>
    /// <param name="centreY">The viewport centre row.</param>
    /// <param name="nearPlane">The near plane, world units.</param>
    /// <param name="planeNormal">The world up axis in VIEW space — the ground's normal.</param>
    /// <param name="floorPixels">The on-screen floor in TARGET pixels; 0 turns it off.</param>
    public void BeginFrame(
        double focal,
        double centreX,
        double centreY,
        double nearPlane,
        Vec3 planeNormal,
        double floorPixels)
    {
        _focal = focal;
        _centreX = centreX;
        _centreY = centreY;
        _near = nearPlane;
        _planeNormal = planeNormal;
        _floorPixels = floorPixels;
    }

    /// <summary>Builds one band's outline, in VIEW space, on the ground plane.</summary>
    /// <param name="a">The centreline's first end, view space, on the plane.</param>
    /// <param name="b">Its second end.</param>
    /// <param name="halfWidth">Half the band's physical width, world units.</param>
    /// <param name="roundCaps">Round caps (a line) or square ends (a ribbon).</param>
    /// <param name="output">Where to write the outline; at least <see cref="MaxVertices"/> long.</param>
    /// <returns>How many vertices were written; 0 when the segment is degenerate.</returns>
    public int Build(Vec3 a, Vec3 b, double halfWidth, bool roundCaps, Span<Vec3> output)
    {
        Vec3 along = b - a;
        double length = along.Length;
        if (!(length > 1e-6))
        {
            return 0;
        }

        Vec3 unit = along * (1.0 / length);
        Vec3 across = _planeNormal.Cross(unit);
        double acrossLength = across.Length;
        if (!(acrossLength > 1e-9))
        {
            return 0;
        }

        across *= 1.0 / acrossLength;

        double half = Math.Max(halfWidth, 0.0);
        double halfA = Math.Max(ApplyScreenFloor(a, across, half), 1e-4);
        double halfB = Math.Max(ApplyScreenFloor(b, across, half), 1e-4);

        int n = 0;
        output[n++] = a + (across * halfA);
        output[n++] = b + (across * halfB);
        if (roundCaps)
        {
            for (int i = 1; i < CapSegments; i++)
            {
                double theta = i * Math.PI / CapSegments;
                output[n++] = b + (across * (Math.Cos(theta) * halfB)) + (unit * (Math.Sin(theta) * halfB));
            }
        }

        output[n++] = b - (across * halfB);
        output[n++] = a - (across * halfA);
        if (roundCaps)
        {
            for (int i = 1; i < CapSegments; i++)
            {
                double theta = i * Math.PI / CapSegments;
                output[n++] = a - (across * (Math.Cos(theta) * halfA)) - (unit * (Math.Sin(theta) * halfA));
            }
        }

        return n;
    }

    /// <summary>
    /// The ON-SCREEN FLOOR, applied where it actually means something: to the width the band
    /// SUBTENDS, not to the width it has.
    /// </summary>
    /// <param name="point">A point of the centreline, view space.</param>
    /// <param name="across">The unit perpendicular in the ground plane.</param>
    /// <param name="half">Half the band's physical width, world units.</param>
    /// <returns>The half-width to draw at.</returns>
    /// <remarks>
    /// <para>
    /// The first version of this measured <c>floor · z / focal</c> — the width a plane FACING the
    /// camera needs — and it was wrong for the ground, which is seen almost edge-on: a 48-foot road
    /// at 26,000 feet and 6.5° of depression subtends <c>48 · sin 6.5° · f / z = 0.17</c> pixels, not
    /// 1.4, so the roads still faded out.  Measuring the projected separation of the band's own two
    /// edges gets the foreshortening for free and needs no trigonometry.
    /// </para>
    /// <para>
    /// <see cref="MaxHalfWidthFraction"/> bounds the widening, because the factor diverges at the
    /// horizon itself — a ground feature exactly on the horizon subtends nothing at all.
    /// </para>
    /// </remarks>
    private double ApplyScreenFloor(Vec3 point, Vec3 across, double half)
    {
        if (_floorPixels <= 0.0 || point.Z < _near)
        {
            return half;
        }

        // A small PROBE offset, not the band's own half-width: the width may be zero (a line whose
        // class is tuned to 0 feet) and the measurement still has to work.  Projection is linear in
        // a small offset, so the probe's projected separation gives pixels-per-world-unit exactly.
        double probe = 1e-3 * Math.Max(point.Z, _near);
        Vec3 plus = point + (across * probe);
        Vec3 minus = point - (across * probe);
        double ceiling = MaxHalfWidthFraction * point.Z;
        if (plus.Z < _near || minus.Z < _near)
        {
            return half;
        }

        (double px, double py) = Project(plus);
        (double qx, double qy) = Project(minus);
        double dx = px - qx, dy = py - qy;
        double separation = Math.Sqrt((dx * dx) + (dy * dy));
        if (!(separation > 1e-12))
        {
            return Math.Max(half, ceiling);
        }

        double pixelsPerWorldUnit = separation / (2.0 * probe);
        double needed = 0.5 * _floorPixels / pixelsPerWorldUnit;
        return needed <= half ? half : Math.Min(needed, Math.Max(half, ceiling));
    }

    /// <summary>Projects one view-space point into target pixels.</summary>
    private (double X, double Y) Project(Vec3 v)
    {
        double invZ = 1.0 / v.Z;
        return (_centreX + (v.X * _focal * invZ), _centreY - (v.Y * _focal * invZ));
    }
}
