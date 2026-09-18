using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Markings;

/// <summary>
/// VECTOR MARKINGS — one node of a picture's shape tree: a SIGNED DISTANCE FIELD in picture space
/// (negative inside), plus a conservative bounding box.  Every curve is analytic; nothing is
/// tessellated; nothing is sampled.
/// </summary>
/// <remarks>
/// The tree is immutable and pure, which is what lets the renderer evaluate it from several tile
/// workers at once and stay bit-identical at any thread count.  A node knows nothing about colour,
/// wear or placement — those are the <see cref="MarkingLayer"/> and the <see cref="MarkingPicture"/>.
/// </remarks>
public abstract class MarkingShape
{
    /// <summary>The signed distance from a picture-space point to the shape's boundary; negative inside.</summary>
    /// <param name="x">Picture-space X.</param>
    /// <param name="y">Picture-space Y.</param>
    public abstract double Distance(double x, double y);

    /// <summary>A box that contains the whole shape (conservative).</summary>
    public abstract SurfaceBounds Bounds { get; }

    /// <summary>Clamps to [0, 1].</summary>
    /// <param name="v">The value.</param>
    protected internal static double Saturate(double v) => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;
}

/// <summary>A disc of radius <see cref="R"/> about the origin.</summary>
/// <param name="R">The radius.</param>
public sealed class CircleShape(double R) : MarkingShape
{
    /// <summary>The radius.</summary>
    public double R { get; } = R;

    /// <inheritdoc />
    public override double Distance(double x, double y) => Math.Sqrt((x * x) + (y * y)) - R;

    /// <inheritdoc />
    public override SurfaceBounds Bounds { get; } = new(-R, -R, R, R);
}

/// <summary>An axis-aligned box about the origin with half extents.</summary>
/// <param name="HalfW">Half the width.</param>
/// <param name="HalfH">Half the height.</param>
public sealed class RectShape(double HalfW, double HalfH) : MarkingShape
{
    /// <summary>Half the width.</summary>
    public double HalfW { get; } = HalfW;

    /// <summary>Half the height.</summary>
    public double HalfH { get; } = HalfH;

    /// <inheritdoc />
    public override double Distance(double x, double y) => Box(x, y, HalfW, HalfH);

    /// <inheritdoc />
    public override SurfaceBounds Bounds { get; } = new(-HalfW, -HalfH, HalfW, HalfH);

    /// <summary>The exact box SDF: outside = Euclidean distance to the box, inside = −distance to the nearest side.</summary>
    /// <param name="x">X.</param>
    /// <param name="y">Y.</param>
    /// <param name="hw">Half width.</param>
    /// <param name="hh">Half height.</param>
    internal static double Box(double x, double y, double hw, double hh)
    {
        double qx = Math.Abs(x) - hw, qy = Math.Abs(y) - hh;
        double ox = Math.Max(qx, 0.0), oy = Math.Max(qy, 0.0);
        return Math.Sqrt((ox * ox) + (oy * oy)) + Math.Min(Math.Max(qx, qy), 0.0);
    }
}

/// <summary>A box with rounded corners.</summary>
/// <param name="HalfW">Half the width (including the corners).</param>
/// <param name="HalfH">Half the height.</param>
/// <param name="Radius">The corner radius.</param>
public sealed class RoundRectShape(double HalfW, double HalfH, double Radius) : MarkingShape
{
    private readonly double _r = Math.Max(0.0, Math.Min(Radius, Math.Min(HalfW, HalfH)));

    /// <inheritdoc />
    public override double Distance(double x, double y) => RectShape.Box(x, y, HalfW - _r, HalfH - _r) - _r;

    /// <inheritdoc />
    public override SurfaceBounds Bounds { get; } = new(-HalfW, -HalfH, HalfW, HalfH);
}

/// <summary>An annulus: a circle of radius <see cref="R"/> stroked <see cref="Width"/> wide.</summary>
/// <param name="R">The centreline radius.</param>
/// <param name="Width">The stroke width.</param>
public sealed class RingShape(double R, double Width) : MarkingShape
{
    /// <inheritdoc />
    public override double Distance(double x, double y) =>
        Math.Abs(Math.Sqrt((x * x) + (y * y)) - R) - (0.5 * Width);

    /// <inheritdoc />
    public override SurfaceBounds Bounds { get; } = new(-R - (0.5 * Width), -R - (0.5 * Width), R + (0.5 * Width), R + (0.5 * Width));
}

/// <summary>A simple polygon: the exact SDF (edge distance, sign by winding).</summary>
public sealed class PolygonShape : MarkingShape
{
    private readonly double[] _x;
    private readonly double[] _y;

    /// <summary>Creates the polygon.</summary>
    /// <param name="points">Its vertices, in order; at least three.</param>
    public PolygonShape(IReadOnlyList<(double X, double Y)> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3)
        {
            throw new ArgumentException("a polygon needs at least three points", nameof(points));
        }

        _x = new double[points.Count];
        _y = new double[points.Count];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < points.Count; i++)
        {
            (_x[i], _y[i]) = points[i];
            minX = Math.Min(minX, _x[i]);
            maxX = Math.Max(maxX, _x[i]);
            minY = Math.Min(minY, _y[i]);
            maxY = Math.Max(maxY, _y[i]);
        }

        Bounds = new SurfaceBounds(minX, minY, maxX, maxY);
    }

    /// <summary>The vertices.</summary>
    public IReadOnlyList<(double X, double Y)> Points =>
        Enumerable.Range(0, _x.Length).Select(i => (_x[i], _y[i])).ToArray();

    /// <inheritdoc />
    public override SurfaceBounds Bounds { get; }

    /// <inheritdoc />
    public override double Distance(double x, double y)
    {
        int n = _x.Length;
        double d = double.MaxValue;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double ex = _x[j] - _x[i], ey = _y[j] - _y[i];
            double wx = x - _x[i], wy = y - _y[i];
            double t = Saturate(((wx * ex) + (wy * ey)) / Math.Max(1e-30, (ex * ex) + (ey * ey)));
            double bx = wx - (ex * t), by = wy - (ey * t);
            d = Math.Min(d, (bx * bx) + (by * by));

            // Even-odd crossing test.
            if ((_y[i] > y) != (_y[j] > y))
            {
                double xAt = _x[i] + ((y - _y[i]) * ex / ey);
                if (x < xAt)
                {
                    inside = !inside;
                }
            }
        }

        double distance = Math.Sqrt(d);
        return inside ? -distance : distance;
    }
}

/// <summary>A regular star with <c>n</c> points, as a polygon.</summary>
public static class StarShape
{
    /// <summary>Builds the star's polygon.</summary>
    /// <param name="points">How many points (≥ 3).</param>
    /// <param name="outer">The tip radius.</param>
    /// <param name="inner">The valley radius.</param>
    /// <param name="rotationDegrees">Where the first tip points; 90 = straight up.</param>
    public static PolygonShape Create(int points, double outer, double inner, double rotationDegrees = 90.0)
    {
        if (points < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(points), "a star needs at least three points");
        }

        (double X, double Y)[] vertices = new (double X, double Y)[points * 2];
        double start = rotationDegrees * Math.PI / 180.0;
        for (int i = 0; i < points * 2; i++)
        {
            double r = (i & 1) == 0 ? outer : inner;
            double a = start + (i * Math.PI / points);
            vertices[i] = (r * Math.Cos(a), r * Math.Sin(a));
        }

        return new PolygonShape(vertices);
    }
}

/// <summary>The union of its children: the minimum distance.</summary>
public sealed class UnionShape : MarkingShape
{
    private readonly MarkingShape[] _children;

    /// <summary>Creates the union.</summary>
    /// <param name="children">At least one child.</param>
    public UnionShape(IReadOnlyList<MarkingShape> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Count == 0)
        {
            throw new ArgumentException("a union needs at least one child", nameof(children));
        }

        _children = [.. children];
        SurfaceBounds bounds = _children[0].Bounds;
        for (int i = 1; i < _children.Length; i++)
        {
            bounds = bounds.Union(_children[i].Bounds);
        }

        Bounds = bounds;
    }

    /// <summary>The children.</summary>
    public IReadOnlyList<MarkingShape> Children => _children;

    /// <inheritdoc />
    public override SurfaceBounds Bounds { get; }

    /// <inheritdoc />
    public override double Distance(double x, double y)
    {
        double d = double.MaxValue;
        foreach (MarkingShape child in _children)
        {
            d = Math.Min(d, child.Distance(x, y));
        }

        return d;
    }
}

/// <summary>The first child minus the rest: <c>max(a, −b)</c>.</summary>
public sealed class SubtractShape : MarkingShape
{
    private readonly MarkingShape _a;
    private readonly MarkingShape[] _b;

    /// <summary>Creates the difference.</summary>
    /// <param name="a">What is kept.</param>
    /// <param name="b">What is cut away.</param>
    public SubtractShape(MarkingShape a, IReadOnlyList<MarkingShape> b)
    {
        _a = a ?? throw new ArgumentNullException(nameof(a));
        _b = [.. b ?? throw new ArgumentNullException(nameof(b))];
    }

    /// <inheritdoc />
    public override SurfaceBounds Bounds => _a.Bounds;

    /// <inheritdoc />
    public override double Distance(double x, double y)
    {
        double d = _a.Distance(x, y);
        foreach (MarkingShape cut in _b)
        {
            d = Math.Max(d, -cut.Distance(x, y));
        }

        return d;
    }
}

/// <summary>The intersection of its children: the maximum distance.</summary>
public sealed class IntersectShape : MarkingShape
{
    private readonly MarkingShape[] _children;

    /// <summary>Creates the intersection.</summary>
    /// <param name="children">At least one child.</param>
    public IntersectShape(IReadOnlyList<MarkingShape> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Count == 0)
        {
            throw new ArgumentException("an intersection needs at least one child", nameof(children));
        }

        _children = [.. children];
        SurfaceBounds b = _children[0].Bounds;
        for (int i = 1; i < _children.Length; i++)
        {
            SurfaceBounds o = _children[i].Bounds;
            b = new SurfaceBounds(Math.Max(b.MinX, o.MinX), Math.Max(b.MinY, o.MinY), Math.Min(b.MaxX, o.MaxX), Math.Min(b.MaxY, o.MaxY));
        }

        Bounds = b;
    }

    /// <inheritdoc />
    public override SurfaceBounds Bounds { get; }

    /// <inheritdoc />
    public override double Distance(double x, double y)
    {
        double d = double.MinValue;
        foreach (MarkingShape child in _children)
        {
            d = Math.Max(d, child.Distance(x, y));
        }

        return d;
    }
}

/// <summary>The child's boundary stroked <see cref="Width"/> wide: <c>|d| − width/2</c>.</summary>
/// <param name="Child">The stroked shape.</param>
/// <param name="Width">The stroke width.</param>
public sealed class OutlineShape(MarkingShape Child, double Width) : MarkingShape
{
    /// <inheritdoc />
    public override double Distance(double x, double y) => Math.Abs(Child.Distance(x, y)) - (0.5 * Width);

    /// <inheritdoc />
    public override SurfaceBounds Bounds { get; } = Child.Bounds.Grow(0.5 * Width);
}

/// <summary>The child grown (or shrunk) by an offset: <c>d − offset</c>.</summary>
/// <param name="Child">The shape.</param>
/// <param name="Offset">How far the boundary moves outward (negative shrinks).</param>
public sealed class OffsetShape(MarkingShape Child, double Offset) : MarkingShape
{
    /// <inheritdoc />
    public override double Distance(double x, double y) => Child.Distance(x, y) - Offset;

    /// <inheritdoc />
    public override SurfaceBounds Bounds { get; } = Child.Bounds.Grow(Math.Max(0.0, Offset));
}

/// <summary>
/// The child translated, rotated and uniformly scaled: the point is inverse-mapped and the distance
/// scaled back by the factor, so the field stays a true distance.
/// </summary>
public sealed class TransformShape : MarkingShape
{
    private readonly MarkingShape _child;
    private readonly double _tx, _ty, _cos, _sin, _scale, _inv;

    /// <summary>Creates the transform.</summary>
    /// <param name="child">The shape.</param>
    /// <param name="translateX">Where the child's origin lands, X.</param>
    /// <param name="translateY">Y.</param>
    /// <param name="rotationDegrees">Counter-clockwise rotation.</param>
    /// <param name="scale">Uniform scale (&gt; 0).</param>
    public TransformShape(MarkingShape child, double translateX, double translateY, double rotationDegrees, double scale)
    {
        _child = child ?? throw new ArgumentNullException(nameof(child));
        if (!(scale > 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "a transform's scale must be positive");
        }

        _tx = translateX;
        _ty = translateY;
        double a = rotationDegrees * Math.PI / 180.0;
        _cos = Math.Cos(a);
        _sin = Math.Sin(a);
        _scale = scale;
        _inv = 1.0 / scale;

        SurfaceBounds b = child.Bounds;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach ((double cx, double cy) in new[] { (b.MinX, b.MinY), (b.MaxX, b.MinY), (b.MaxX, b.MaxY), (b.MinX, b.MaxY) })
        {
            double px = (((cx * _cos) - (cy * _sin)) * scale) + translateX;
            double py = (((cx * _sin) + (cy * _cos)) * scale) + translateY;
            minX = Math.Min(minX, px);
            maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py);
            maxY = Math.Max(maxY, py);
        }

        Bounds = new SurfaceBounds(minX, minY, maxX, maxY);
    }

    /// <inheritdoc />
    public override SurfaceBounds Bounds { get; }

    /// <inheritdoc />
    public override double Distance(double x, double y)
    {
        double dx = (x - _tx) * _inv, dy = (y - _ty) * _inv;
        double lx = (dx * _cos) + (dy * _sin);
        double ly = (-dx * _sin) + (dy * _cos);
        return _child.Distance(lx, ly) * _scale;
    }
}

/// <summary>A stroked polyline: the union of its segments' capsules — the font's primitive.</summary>
public sealed class StrokeShape : MarkingShape
{
    private readonly double[] _x, _y;
    private readonly double _half;

    /// <summary>Creates the stroke.</summary>
    /// <param name="points">The polyline; one point is a dot.</param>
    /// <param name="width">The stroke width.</param>
    public StrokeShape(IReadOnlyList<(double X, double Y)> points, double width)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ArgumentException("a stroke needs at least one point", nameof(points));
        }

        _x = new double[points.Count];
        _y = new double[points.Count];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < points.Count; i++)
        {
            (_x[i], _y[i]) = points[i];
            minX = Math.Min(minX, _x[i]);
            maxX = Math.Max(maxX, _x[i]);
            minY = Math.Min(minY, _y[i]);
            maxY = Math.Max(maxY, _y[i]);
        }

        _half = 0.5 * width;
        Bounds = new SurfaceBounds(minX - _half, minY - _half, maxX + _half, maxY + _half);
    }

    /// <inheritdoc />
    public override SurfaceBounds Bounds { get; }

    /// <inheritdoc />
    public override double Distance(double x, double y)
    {
        double d = double.MaxValue;
        if (_x.Length == 1)
        {
            double px = x - _x[0], py = y - _y[0];
            return Math.Sqrt((px * px) + (py * py)) - _half;
        }

        for (int i = 0; i + 1 < _x.Length; i++)
        {
            double ex = _x[i + 1] - _x[i], ey = _y[i + 1] - _y[i];
            double wx = x - _x[i], wy = y - _y[i];
            double t = Saturate(((wx * ex) + (wy * ey)) / Math.Max(1e-30, (ex * ex) + (ey * ey)));
            double bx = wx - (ex * t), by = wy - (ey * t);
            d = Math.Min(d, (bx * bx) + (by * by));
        }

        return Math.Sqrt(d) - _half;
    }
}
