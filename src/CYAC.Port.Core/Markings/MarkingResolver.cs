using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Markings;

/// <summary>
/// VECTOR MARKINGS — turns a class's placements into the per-record frame table a LOD carries:
/// for every polygon record, every concrete frame (the placement, its mirror twin, its far
/// side) whose plane the record faces and whose picture box the record's projection touches.
/// </summary>
/// <remarks>
/// <para>
/// Geometry only: the rule reads the record's outward normal (Newell's, in index order), the
/// vertices projected into the frame, and the distance off the frame plane.  Records inside the
/// gear articulation's vertex runs are skipped (articulated parts carry no markings in the
/// first build — the frame would have to follow the pose).
/// </para>
/// <para>
/// The same function serves the library build and the browser's live editing
/// (<c>MeshModel.WithMarkings</c>), so what the editor shows is what the game draws.
/// </para>
/// </remarks>
public static class MarkingResolver
{
    /// <summary>The cosine of the side rule's 60°.</summary>
    public const double SideCosine = 0.5;

    /// <summary>One concrete frame a placement generates, before it meets any face.</summary>
    /// <param name="Placement">The placement it came from.</param>
    /// <param name="PlacementIndex">Its index in the set.</param>
    /// <param name="Origin">The frame origin.</param>
    /// <param name="U">The picture's +X axis.</param>
    /// <param name="V">The picture's +Y axis.</param>
    /// <param name="Normal"><c>U × V</c>, the side the picture faces.</param>
    /// <param name="Flip">Whether the picture's X is negated.</param>
    /// <param name="Far">Whether this is the far-side copy of a <see cref="PlacementSides.Both"/> placement.</param>
    /// <param name="Twin">Whether this is the mirror twin.</param>
    public readonly record struct ConcreteFrame(
        MarkingPlacement Placement,
        int PlacementIndex,
        (double X, double Y, double Z) Origin,
        (double X, double Y, double Z) U,
        (double X, double Y, double Z) V,
        (double X, double Y, double Z) Normal,
        bool Flip,
        bool Far,
        bool Twin);

    /// <summary>Every concrete frame of a set: the placements, their twins, their far sides.</summary>
    /// <param name="set">The set.</param>
    /// <param name="library">The pictures (for the symmetric / noMirror flags).</param>
    public static IReadOnlyList<ConcreteFrame> Frames(MarkingSet set, MarkingLibrary library)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(library);
        List<ConcreteFrame> frames = new List<ConcreteFrame>();
        for (int i = 0; i < set.Placements.Count; i++)
        {
            MarkingPlacement p = set.Placements[i].Orthonormalised();
            MarkingPicture? picture = library.TryGet(p.Picture);
            bool flippable = picture is { NoMirror: false, Symmetric: false };
            (double X, double Y, double Z) n = p.Normal;
            Add(frames, p, i, p.Origin, p.U, p.V, n, flip: false, far: false, twin: false, flippable);

            if (p.Mirror != PlacementMirror.None)
            {
                // Mirror across the model plane; negate U so the twin's viewer sees the picture
                // unmirrored (U × V stays outward on the twin's side).  An asymmetric picture that
                // is anchored to the airframe (a shark mouth, an arrow) then flips so it still
                // points at the nose; a code (noMirror) does not.
                (double X, double Y, double Z) mo = Mirror(p.Origin, p.Mirror);
                (double X, double Y, double Z) mu = Neg(Mirror(p.U, p.Mirror));
                (double X, double Y, double Z) mv = Mirror(p.V, p.Mirror);
                Add(frames, p, i, mo, mu, mv, Cross(mu, mv), flip: false, far: false, twin: true, flippable);
            }
        }

        return frames;
    }

    private static void Add(
        List<ConcreteFrame> frames, MarkingPlacement p, int index,
        (double X, double Y, double Z) o, (double X, double Y, double Z) u, (double X, double Y, double Z) v,
        (double X, double Y, double Z) n, bool flip, bool far, bool twin, bool flippable)
    {
        frames.Add(new ConcreteFrame(p, index, o, u, v, n, flip || (twin && flippable), Far: far, Twin: twin));
        if (p.Sides == PlacementSides.Both)
        {
            // The far side: U negated so the far viewer reads it unmirrored; an airframe-anchored
            // asymmetric picture flips back so it keeps pointing the same way along the airframe.
            (double X, double Y, double Z) fu = Neg(u);
            frames.Add(new ConcreteFrame(p, index, o, fu, v, Cross(fu, v), (flip || (twin && flippable)) ^ flippable, Far: true, Twin: twin));
        }
    }

    /// <summary>Resolves a set onto one LOD: the record → frames table.</summary>
    /// <param name="lod">The LOD (after inflation and the lift — the faces the renderer draws).</param>
    /// <param name="set">The class's placements.</param>
    /// <param name="library">The pictures.</param>
    /// <param name="gear">The class's gear articulation, or null; its vertex runs are excluded.</param>
    /// <returns>The table; empty when nothing lands.</returns>
    public static IReadOnlyDictionary<int, SurfaceMarkingFrame[]> Resolve(
        MeshLod lod, MarkingSet set, MarkingLibrary library, GearArticulation? gear)
    {
        ArgumentNullException.ThrowIfNull(lod);
        IReadOnlyList<ConcreteFrame> frames = Frames(set, library);
        Dictionary<int, SurfaceMarkingFrame[]> result = new Dictionary<int, SurfaceMarkingFrame[]>();
        if (frames.Count == 0)
        {
            return result;
        }

        MarkingPicture?[] pictures = new MarkingPicture?[frames.Count];
        for (int f = 0; f < frames.Count; f++)
        {
            MarkingPlacement p = frames[f].Placement;
            pictures[f] = library.TryGet(p.Picture) is null ? null : library.Instance(p.Picture, p.Text, p.Color, p.Wear, p.BareMetal);
        }

        MeshFace[] faces = lod.Faces;
        List<SurfaceMarkingFrame> hits = new List<SurfaceMarkingFrame>();
        for (int r = 0; r < faces.Length; r++)
        {
            MeshFace face = faces[r];
            if (face.Primitive != MeshPrimitive.Polygon || face.Indices.Length < 3 || !lod.EmittedByPaintTree(r))
            {
                continue;
            }

            if (gear is not null && IsArticulated(lod, face, gear))
            {
                continue;
            }

            ((double X, double Y, double Z) normal, double area) = DecalConditioning.Newell(lod, face.Indices);
            if (!(area > 1e-9))
            {
                continue;
            }

            hits.Clear();
            for (int f = 0; f < frames.Count; f++)
            {
                if (pictures[f] is not { } picture)
                {
                    continue;
                }

                ConcreteFrame frame = frames[f];
                if (frame.Placement.Sides == PlacementSides.Around)
                {
                    // The face's own frame: U = the axis, V = n × U (so U × V = n, outward).
                    (double X, double Y, double Z) unit = MarkingPlacement.Unit(normal);
                    if (Math.Abs(Dot(unit, frame.U)) >= SideCosine)
                    {
                        continue;   // an end cap, not a side of the body
                    }

                    (double X, double Y, double Z) v = Cross(unit, frame.U);
                    ConcreteFrame around = frame with { V = v, Normal = Cross(frame.U, v) };
                    if (!Touches(lod, face, around, picture, axisOnly: true))
                    {
                        continue;
                    }

                    hits.Add(new SurfaceMarkingFrame(
                        around.Origin.X, around.Origin.Y, around.Origin.Z,
                        around.U.X, around.U.Y, around.U.Z,
                        around.V.X, around.V.Y, around.V.Z,
                        frame.Placement.Size, frame.Flip, picture));
                    continue;
                }

                double agree = Dot(normal, frame.Normal);
                bool sideOk = frame.Placement.Sides == PlacementSides.Through ? Math.Abs(agree) >= SideCosine : agree >= SideCosine;
                if (!sideOk)
                {
                    continue;
                }

                if (!Touches(lod, face, frame, picture, axisOnly: false))
                {
                    continue;
                }

                hits.Add(new SurfaceMarkingFrame(
                    frame.Origin.X, frame.Origin.Y, frame.Origin.Z,
                    frame.U.X, frame.U.Y, frame.U.Z,
                    frame.V.X, frame.V.Y, frame.V.Z,
                    frame.Placement.Size, frame.Flip, picture));
            }

            if (hits.Count > 0)
            {
                result[r] = [.. hits];
            }
        }

        return result;
    }

    /// <summary>
    /// Whether a record's projection into the frame overlaps the picture's box (grown by the wear
    /// band and one model unit) and the record lies within the frame's depth tolerance.
    /// </summary>
    private static bool Touches(MeshLod lod, in MeshFace face, in ConcreteFrame frame, MarkingPicture picture, bool axisOnly)
    {
        double size = frame.Placement.Size;
        SurfaceBounds b = picture.Bounds;
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        double minW = double.MaxValue;
        foreach (int i in face.Indices)
        {
            (double x, double y, double z) = lod.VertexPosition(i);
            double dx = x - frame.Origin.X, dy = y - frame.Origin.Y, dz = z - frame.Origin.Z;
            double u = (dx * frame.U.X) + (dy * frame.U.Y) + (dz * frame.U.Z);
            double v = (dx * frame.V.X) + (dy * frame.V.Y) + (dz * frame.V.Z);
            double w = Math.Abs((dx * frame.Normal.X) + (dy * frame.Normal.Y) + (dz * frame.Normal.Z));
            if (frame.Flip)
            {
                u = -u;
            }

            minU = Math.Min(minU, u);
            maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v);
            maxV = Math.Max(maxV, v);
            minW = Math.Min(minW, w);
        }

        double margin = 1.0;
        if (axisOnly)
        {
            // A wrapped band: the station along the axis must overlap, and EVERY vertex must lie
            // within the body's radius of the axis (`depth` for an `around` placement; default
            // 0.6 × size) — a wing root starts inside that radius but its tip does not.
            double radius = frame.Placement.Depth > 0.0 ? frame.Placement.Depth : 0.6 * size;
            foreach (int i in face.Indices)
            {
                (double x, double y, double z) = lod.VertexPosition(i);
                double dx = x - frame.Origin.X, dy = y - frame.Origin.Y, dz = z - frame.Origin.Z;
                double along = (dx * frame.U.X) + (dy * frame.U.Y) + (dz * frame.U.Z);
                double rx = dx - (along * frame.U.X), ry = dy - (along * frame.U.Y), rz = dz - (along * frame.U.Z);
                if (Math.Sqrt((rx * rx) + (ry * ry) + (rz * rz)) > radius)
                {
                    return false;
                }
            }

            return maxU >= (b.MinX * size) - margin && minU <= (b.MaxX * size) + margin;
        }

        double depth = frame.Placement.Depth > 0.0 ? frame.Placement.Depth : DefaultDepth(size);
        if (minW > depth)
        {
            return false;
        }

        return maxU >= (b.MinX * size) - margin && minU <= (b.MaxX * size) + margin
            && maxV >= (b.MinY * size) - margin && minV <= (b.MaxY * size) + margin;
    }

    /// <summary>
    /// The default depth tolerance: a third of the picture's size plus three model units — enough
    /// for an inflated skin (≤ 2.5 units off the shipped plane) and a faceted fuselage's curvature,
    /// not enough to reach the opposite side of a wing root or a fuselage.
    /// </summary>
    /// <param name="size">The placement's size.</param>
    public static double DefaultDepth(double size) => (size / 3.0) + 3.0;

    private static bool IsArticulated(MeshLod lod, in MeshFace face, GearArticulation gear)
    {
        if (gear.LodIndex != lod.Index)
        {
            return false;
        }

        foreach (int i in face.Indices)
        {
            int source = lod.SourceVertex(i);
            foreach (GearGroup group in gear.Groups)
            {
                if (source >= group.FirstVertex && source <= group.LastVertex)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static (double X, double Y, double Z) Mirror((double X, double Y, double Z) v, PlacementMirror m) =>
        m == PlacementMirror.X ? (-v.X, v.Y, v.Z) : m == PlacementMirror.Y ? (v.X, -v.Y, v.Z) : v;

    private static (double X, double Y, double Z) Neg((double X, double Y, double Z) v) => (-v.X, -v.Y, -v.Z);

    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        MarkingPlacement.Unit(((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X)));

    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
}
