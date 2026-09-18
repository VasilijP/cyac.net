using System.Diagnostics;
using CYAC.Port.Render.Ground;
using CYAC.Port.Render.Raster;

namespace CYAC.Port.Render.Pipeline;

/// <summary>
/// One THREAD's private rendering state, and the loop that turns one tile's bins into pixels.
/// </summary>
/// <remarks>
/// <para>
/// A worker owns everything a tile needs and nothing a frame shares: its own
/// <see cref="FragmentRaster"/> (whose fragment pool is tile-local) and its capsule scratch.  The
/// only things it touches outside itself are the read-only display list, the
/// frame's depth buffer (whose elements belong to exactly one tile) and the target's pixels (ditto)
/// — so nothing needs a lock and nothing can race.
/// </para>
/// <para>
/// <b>What stays here</b> is what is NOT a rasterisation decision: which pass a primitive belongs
/// to, what its sort key is, and how a capsule's two endpoints become an outline.  The stamp map
/// went with the blend-stamp buffer; <see cref="CombineRule.Max"/> is resolved per pixel in the
/// fragment resolve.
/// </para>
/// </remarks>
internal sealed class TileWorker
{
    /// <summary>
    /// How many segments a capsule's end cap is drawn with — the same figure the GROUND-PLANE band
    /// builder polygonises its caps at (<see cref="GroundBand.CapSegments"/>), so a line looks the
    /// same whether it is widened in the plane or in screen space.
    /// </summary>
    private const int CapSegments = GroundBand.CapSegments;

    private readonly ScreenVertex[] _capsule = new ScreenVertex[(2 * CapSegments) + 2];
    private readonly FragmentRaster _raster = new();

    /// <summary>This worker's rasteriser.</summary>
    /// <remarks>
    /// There is ONE (<see cref="FragmentRaster"/>).
    /// </remarks>
    public FragmentRaster Raster => _raster;

    /// <summary>Pixels this worker's <see cref="PrimitiveKind.Halo"/> primitives blended.</summary>
    public long HaloPixels { get; private set; }

    /// <summary>
    /// <see cref="Stopwatch"/> ticks it spent on them.  A per-KIND figure — a kind is geometry, not
    /// a game concept.
    /// </summary>
    public long HaloTicks { get; private set; }

    /// <summary>Starts a frame on this worker.</summary>
    /// <param name="depth">The frame's shared depth buffer.</param>
    /// <param name="targetWidth">The target's width.</param>
    /// <param name="targetHeight">Its height.</param>
    /// <param name="style">the frame's look (the retro dither and the edge rule).</param>
    /// <param name="background">the frame's terminal function.</param>
    public void BeginFrame(
        float[] depth,
        int targetWidth,
        int targetHeight,
        in ResolveStyle style,
        BackgroundField background,
        InteriorMask? mask = null)
    {
        _raster.BeginFrame(depth, targetWidth, targetHeight, style, background, mask);
        HaloPixels = 0;
        HaloTicks = 0;
    }

    /// <summary>Draws one tile: its opaque bin, then its translucent bin.</summary>
    /// <param name="target">The pixels.</param>
    /// <param name="list">The frame's display list.</param>
    /// <param name="rect">The tile, in absolute target pixels.</param>
    /// <param name="opaque">Its opaque primitives, in submission order.</param>
    /// <param name="translucent">Its translucent primitives, in the frame's sort order.</param>
    /// <remarks>
    /// The two passes are R1's, restricted to one tile: opaque first in SUBMISSION order writing
    /// depth (which is what keeps a depth TIE between two coplanar decals resolving the same way on
    /// both sides of a tile boundary — H12 §4 point 2), then everything else in the frame's total
    /// sort order, depth-tested and writing no depth.
    /// </remarks>
    public void DrawTile(
        in PixelTarget target,
        DisplayList list,
        TileRect rect,
        ReadOnlySpan<int> opaque,
        ReadOnlySpan<int> translucent)
    {
        FragmentRaster raster = Raster;
        raster.BeginTile(rect.X, rect.Y, rect.Width, rect.Height);

        ReadOnlySpan<DisplayPrimitive> primitives = list.Primitives;
        for (int k = 0; k < opaque.Length; k++)
        {
            int i = opaque[k];
            long before = raster.PixelsWritten;
            DrawOne(target, list, i, in primitives[i], writeDepth: true);
            list.AddPixels(i, raster.PixelsWritten - before);
        }

        for (int k = 0; k < translucent.Length; k++)
        {
            int i = translucent[k];
            ref readonly DisplayPrimitive primitive = ref primitives[i];
            bool halo = primitive.Kind == PrimitiveKind.Halo;
            long tick = halo ? Stopwatch.GetTimestamp() : 0;
            long before = raster.PixelsWritten;
            DrawOne(target, list, i, in primitive, writeDepth: false);
            long written = raster.PixelsWritten - before;
            list.AddPixels(i, written);
            if (halo)
            {
                HaloPixels += written;
                HaloTicks += Stopwatch.GetTimestamp() - tick;
            }
        }

        raster.EndTile(target);
    }

    private void DrawOne(
        in PixelTarget target,
        DisplayList list,
        int index,
        in DisplayPrimitive primitive,
        bool writeDepth)
    {
        ReadOnlySpan<ScreenVertex> vertices = list.VerticesOf(in primitive);

        // Where the primitive stands in the frame's order.  A direct rasteriser ignores it; a
        // fragment rasteriser sorts every fragment it emits by it.
        Raster.BeginPrimitive(
            new PrimitiveKey(
                primitive.Layer,
                primitive.Priority,
                primitive.Group,
                primitive.Combine,
                primitive.SubmissionIndex));

        Paint paint = new Paint(
            primitive.Color,
            primitive.Coverage,
            primitive.Profile == CoverageProfile.Radial);

        switch (primitive.Kind)
        {
            case PrimitiveKind.Polygon:
                if (primitive.Shader >= 0)
                {
                    // VECTOR MARKINGS — the skin's colour is computed per pixel from the entry's
                    // marking planes; everything else about the fill is the flat path's.
                    ref readonly SurfaceShaderEntry entry = ref list.ShaderAt(primitive.Shader);
                    Raster.FillPolygon(
                        target, vertices, primitive.Depth, paint, writeDepth,
                        new MarkedShade(
                            entry.Skin, primitive.Color, primitive.Depth, list.Planes,
                            entry.PlaneStart, entry.PlaneCount, target.Order));
                }
                else
                {
                    Raster.FillPolygon(target, vertices, primitive.Depth, paint, writeDepth);
                }

                break;

            case PrimitiveKind.Disc:
                Raster.FillDisc(target, vertices[0], primitive.ScalarA, paint, writeDepth);
                break;

            case PrimitiveKind.Point:
                Raster.DrawPoint(target, vertices[0], paint, writeDepth);
                break;

            case PrimitiveKind.Capsule:
                DrawCapsule(target, in primitive, vertices, paint, writeDepth);
                break;

            case PrimitiveKind.Sprite:
                if (list.SpriteAt(index) is { } sprite)
                {
                    Raster.DrawSprite(
                        target,
                        sprite,
                        vertices[0],
                        primitive.ScalarA,
                        primitive.ScalarB,
                        primitive.Coverage,
                        list.SpritePalette,
                        writeDepth);
                }

                break;

            case PrimitiveKind.Halo:
                Raster.BlendHaloSegment(
                    target,
                    vertices[0],
                    vertices[1],
                    primitive.ScalarA,
                    primitive.ScalarB,
                    primitive.Color,
                    list.HaloProfile);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Rasterises a <see cref="PrimitiveKind.Capsule"/>: a screen-space trapezoid with a
    /// polygonised half-disc at each end.
    /// </summary>
    /// <param name="target">The pixels.</param>
    /// <param name="primitive">The capsule.</param>
    /// <param name="vertices">Its two projected endpoints.</param>
    /// <param name="paint">Its resolved colour and coverage.</param>
    /// <param name="writeDepth">Whether it may stamp the depth buffer.</param>
    /// <remarks>
    /// The half-widths are the WHAT stage's (a physical width projected, floored on screen — port
    /// turning them into an outline is pure geometry and belongs here.  Because
    /// <c>1/z</c> is exactly affine in screen space along the projected segment, the outline gets a
    /// real depth plane rather than one depth and cannot z-fight along its own length.  The outline
    /// is built in ABSOLUTE target coordinates and clipped by the raster, so a capsule that
    /// straddles a tile boundary is the same shape on both sides.
    /// </remarks>
    private void DrawCapsule(
        in PixelTarget target,
        in DisplayPrimitive primitive,
        ReadOnlySpan<ScreenVertex> vertices,
        Paint paint,
        bool writeDepth)
    {
        ScreenVertex pa = vertices[0];
        ScreenVertex pb = vertices[1];
        double halfA = primitive.ScalarA;
        double halfB = primitive.ScalarB;

        double dx = pb.X - pa.X;
        double dy = pb.Y - pa.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (!(length > 1e-6) || !double.IsFinite(halfA) || !double.IsFinite(halfB))
        {
            Raster.FillDisc(target, pa, Math.Max(halfA, halfB), paint, writeDepth);
            return;
        }

        double ux = dx / length, uy = dy / length;
        double px = -uy, py = ux;

        int n = 0;
        _capsule[n++] = pa with { X = pa.X + (px * halfA), Y = pa.Y + (py * halfA) };
        _capsule[n++] = pb with { X = pb.X + (px * halfB), Y = pb.Y + (py * halfB) };
        for (int i = 1; i < CapSegments; i++)
        {
            double theta = i * Math.PI / CapSegments;
            double c = Math.Cos(theta) * halfB, s = Math.Sin(theta) * halfB;
            _capsule[n++] = pb with { X = pb.X + (px * c) + (ux * s), Y = pb.Y + (py * c) + (uy * s) };
        }

        _capsule[n++] = pb with { X = pb.X - (px * halfB), Y = pb.Y - (py * halfB) };
        _capsule[n++] = pa with { X = pa.X - (px * halfA), Y = pa.Y - (py * halfA) };
        for (int i = 1; i < CapSegments; i++)
        {
            double theta = i * Math.PI / CapSegments;
            double c = Math.Cos(theta) * halfA, s = Math.Sin(theta) * halfA;
            _capsule[n++] = pa with { X = pa.X - (px * c) - (ux * s), Y = pa.Y - (py * c) - (uy * s) };
        }

        double slope = (pb.InvZ - pa.InvZ) / length;
        double planeA = slope * ux;
        double planeB = slope * uy;
        double planeC = pa.InvZ - (planeA * pa.X) - (planeB * pa.Y);
        DepthPlane depth = new DepthPlane(planeA, planeB, planeC + (0.5 * (planeA + planeB)));
        Raster.FillPolygon(target, _capsule.AsSpan(0, n), depth, paint, writeDepth);
    }
}
