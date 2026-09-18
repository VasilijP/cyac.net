using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Markings;

/// <summary>
/// VECTOR MARKINGS — the one-off BOOTSTRAP that proposes a class's placements from the shipped geometry alone:
/// where the 1991 model painted a marking, a placement of the nation's insignia replaces it; where it painted
/// none (the P-51, P-47, B-29, F-105, B-52), RULE placements put a wing insignia at 60 % of each semi-span and a
/// fuselage insignia aft of the canopy.
/// </summary>
/// <remarks>
/// <para>
/// The output is the BASE a class's committed diff (<see cref="MarkingDiff"/>) is applied to. Its numbers are
/// derived from the original meshes, so it is regenerated wherever the data tree is, never committed; only the
/// edits made over it are. It runs on the SHIPPED LOD (no conditioning), so its record indices are the shipped
/// ones the <c>replaces</c> lists name.
/// </para>
/// <para>
/// Every placement carries a <see cref="MarkingPlacement.Key"/> derived from what produced it:
/// <c>shipped:&lt;records&gt;</c> for a decal cluster, <c>band:&lt;records&gt;</c> for a wrapped band and
/// <c>rule:&lt;name&gt;</c> for a rule placement. A key that repeats within a class gets <c>#2</c>, <c>#3</c>, …
/// in proposal order. Change <see cref="Generator"/> whenever the proposal changes on purpose.
/// </para>
/// </remarks>
public static class MarkingBootstrap
{
    /// <summary>The generator id a diff records for this bootstrap's output.</summary>
    public const string Generator = "MarkingBootstrap/1";

    /// <summary>Proposes a class's placements.</summary>
    /// <param name="plain">The class built with <see cref="MeshConditioning.None"/>.</param>
    /// <param name="nation">The nation (<see cref="MarkingNation"/>), or null for no insignia.</param>
    /// <param name="library">The pictures (for their boxes).</param>
    /// <returns>The set; may be empty.</returns>
    public static MarkingSet Propose(MeshModel plain, string? nation, MarkingLibrary library)
    {
        ArgumentNullException.ThrowIfNull(plain);
        ArgumentNullException.ThrowIfNull(library);
        string? insigniaName = MarkingNation.Insignia(nation, MarkingNation.IsPostwar(plain.Basename));
        List<MarkingPlacement> placements = new List<MarkingPlacement>();
        if (insigniaName is null || library.TryGet(insigniaName) is not { } insignia)
        {
            return new MarkingSet(plain.Basename, placements, nation);
        }

        int lodIndex = plain.DensestLodIndex;
        MeshLod lod = plain.LodByIndex(lodIndex);
        IReadOnlyList<int[]> clusters = ShippedClusters(lod);

        // A BAND that wraps the body (the Sabre's yellow tail band: eight quads of one colour
        // around the rear fuselage) is one `around` placement, not eight stars.
        HashSet<int> banded = new HashSet<int>();
        foreach ((MarkingPlacement Placement, int[] Records) band in WrappedBands(lod, clusters, plain.Gear))
        {
            placements.Add(band.Placement);
            banded.UnionWith(band.Records);
        }

        foreach (int[] cluster in clusters)
        {
            if (cluster.Any(banded.Contains))
            {
                continue;
            }

            // A wheel disc on a gear leg is a decal to the census but not a marking; and a cluster
            // smaller than a few model units (a window, a light) is not an insignia.
            if (plain.Gear is { } gear && gear.LodIndex == lodIndex && cluster.Any(r => InGearRun(lod, lod.Faces[r], gear)))
            {
                continue;
            }

            MarkingPlacement? placement = FromCluster(lod, cluster, insignia, lodIndex);
            if (placement is not null && placement.Size >= MinClusterSize)
            {
                placements.Add(placement);
            }
        }

        if (placements.Count == 0)
        {
            placements.AddRange(RulePlacements(lod, insignia, nation));
        }

        return new MarkingSet(plain.Basename, WithUniqueKeys(placements), nation);
    }

    private static List<MarkingPlacement> WithUniqueKeys(List<MarkingPlacement> placements)
    {
        Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
        List<MarkingPlacement> result = new List<MarkingPlacement>(placements.Count);
        foreach (MarkingPlacement p in placements)
        {
            string key = p.Key ?? throw new InvalidOperationException("a proposed placement has no key");
            int n = seen[key] = seen.GetValueOrDefault(key) + 1;
            result.Add(n == 1 ? p : p with { Key = $"{key}#{n}" });
        }

        return result;
    }

    /// <summary>
    /// Shipped decals of one colour whose Z ranges overlap and whose outward normals spread over
    /// more than a half turn about the body axis: a band wrapped around the fuselage.
    /// </summary>
    /// <param name="lod">The shipped LOD.</param>
    /// <param name="clusters">The decal clusters (see <see cref="ShippedClusters"/>).</param>
    public static IReadOnlyList<(MarkingPlacement Placement, int[] Records)> WrappedBands(MeshLod lod, IReadOnlyList<int[]> clusters, GearArticulation? gear = null)
    {
        ArgumentNullException.ThrowIfNull(lod);
        ArgumentNullException.ThrowIfNull(clusters);
        List<(MarkingPlacement, int[])> result = new List<(MarkingPlacement, int[])>();
        List<int> records = clusters.SelectMany(c => c).Distinct().ToList();
        {
            // Candidates: sideways-facing decals (|n_z| small) — a band's segments run along the
            // body — that are not on a gear leg (a wheel is a sideways disc too).  NOT grouped by
            // colour: the 1991 artist shaded the Sabre's yellow band per facet (palette 67–70).
            List<int> side = records.Where(r =>
                Math.Abs(Unit(DecalConditioning.Newell(lod, lod.Faces[r].Indices).Normal).Z) < 0.5
                && !(gear is not null && gear.LodIndex == lod.Index && InGearRun(lod, lod.Faces[r], gear))).ToList();
            if (side.Count < 4)
            {
                return result;
            }

            // Chain by SHARED VERTICES and overlapping Z range: the segments of one band touch.
            Dictionary<int, (double Min, double Max)> ranges = side.ToDictionary(r => r, r => ZRange(lod, lod.Faces[r].Indices));
            Dictionary<int, int> parent = side.ToDictionary(r => r, r => r);
            int Find(int r) => parent[r] == r ? r : parent[r] = Find(parent[r]);
            foreach (int a in side)
            {
                foreach (int b in side)
                {
                    if (a < b && ranges[a].Min <= ranges[b].Max + 0.5 && ranges[b].Min <= ranges[a].Max + 0.5
                        && lod.Faces[a].Indices.Intersect(lod.Faces[b].Indices).Any())
                    {
                        parent[Find(a)] = Find(b);
                    }
                }
            }

            foreach (int[] chain in side.GroupBy(Find).Select(g => g.OrderBy(r => r).ToArray()))
            {
                if (chain.Length < 4)
                {
                    continue;
                }

                // The normals must go around: the spread of their angle about Z exceeds 180°.
                List<double> angles = chain.Select(r =>
                {
                    (double X, double Y, double Z) n = Unit(DecalConditioning.Newell(lod, lod.Faces[r].Indices).Normal);
                    return Math.Atan2(n.Y, n.X);
                }).OrderBy(a => a).ToList();
                double maxGap = 0;
                for (int i = 0; i < angles.Count; i++)
                {
                    double next = i + 1 < angles.Count ? angles[i + 1] : angles[0] + (2 * Math.PI);
                    maxGap = Math.Max(maxGap, next - angles[i]);
                }

                // Around means MANY facet directions (the Sabre's band has eight); a fin star on
                // both skins has two opposite ones and is not a band.
                int bins = angles.Select(a => (int)Math.Round(a * 12.0 / Math.PI)).Distinct().Count();
                if (bins < 4 || (2 * Math.PI) - maxGap < 150.0 * Math.PI / 180.0)
                {
                    continue;
                }

                double zMin = chain.Min(r => ranges[r].Min), zMax = chain.Max(r => ranges[r].Max);
                List<(double X, double Y, double Z)> vertices = chain.SelectMany(r => lod.Faces[r].Indices).Distinct().Select(lod.VertexPosition).ToList();
                double y = vertices.Average(v => v.Y);
                // The body's radius at the band: the farthest shipped vertex from the axis, with room.
                double radius = 1.25 * vertices.Max(v => Math.Sqrt((v.X * v.X) + ((v.Y - y) * (v.Y - y))));
                result.Add((new MarkingPlacement(
                    "sabre-band",
                    (0.0, y, 0.5 * (zMin + zMax)),
                    (0.0, 0.0, 1.0),
                    (0.0, 1.0, 0.0),
                    Math.Max(1.0, zMax - zMin),
                    Sides: PlacementSides.Around,
                    Depth: radius,
                    Replaces: new Dictionary<int, int[]> { [lod.Index] = chain },
                    Note: $"bootstrap: wrapped band, shipped records {string.Join(",", chain)}")
                {
                    Key = "band:" + string.Join(",", chain),
                }, chain));
            }
        }

        return result;
    }

    private static (double Min, double Max) ZRange(MeshLod lod, int[] indices)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (int i in indices)
        {
            double z = lod.VertexPosition(i).Z;
            min = Math.Min(min, z);
            max = Math.Max(max, z);
        }

        return (min, max);
    }

    /// <summary>The smallest placement size (model units per picture unit) a shipped cluster may propose.</summary>
    public const double MinClusterSize = 4.0;

    private static bool InGearRun(MeshLod lod, in MeshFace face, GearArticulation gear)
    {
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

    /// <summary>
    /// The shipped markings, clustered: the records the decal census finds (a polygon lying on a
    /// larger coplanar one), grouped when coplanar and adjacent.
    /// </summary>
    /// <param name="lod">The shipped LOD.</param>
    public static IReadOnlyList<int[]> ShippedClusters(MeshLod lod)
    {
        ArgumentNullException.ThrowIfNull(lod);
        List<DecalConditioning.Decal> found = new List<DecalConditioning.Decal>();
        DecalConditioning.Apply(lod, 0.0, found);   // census only, no lift
        List<int> records = found.Select(d => d.Record).Distinct().OrderBy(r => r).ToList();
        Dictionary<int, (double X, double Y, double Z)> normals = records.ToDictionary(r => r, r => Unit(DecalConditioning.Newell(lod, lod.Faces[r].Indices).Normal));
        Dictionary<int, (double X, double Y, double Z)> centroids = records.ToDictionary(r => r, r => Centroid(lod, lod.Faces[r].Indices));
        Dictionary<int, double> extents = records.ToDictionary(r => r, r => Extent(lod, lod.Faces[r].Indices));

        // Union-find over pairs that are coplanar (|cos| ≥ 0.95, within 1.5 units of each other's
        // plane) and near (centroids closer than the sum of their extents).
        Dictionary<int, int> parent = records.ToDictionary(r => r, r => r);
        int Find(int r) => parent[r] == r ? r : parent[r] = Find(parent[r]);
        for (int i = 0; i < records.Count; i++)
        {
            for (int j = i + 1; j < records.Count; j++)
            {
                int a = records[i], b = records[j];
                (double X, double Y, double Z) na = normals[a];
                (double X, double Y, double Z) nb = normals[b];
                if (Math.Abs(Dot(na, nb)) < 0.95)
                {
                    continue;
                }

                (double X, double Y, double Z) ca = centroids[a];
                (double X, double Y, double Z) cb = centroids[b];
                (double, double, double) d = (cb.X - ca.X, cb.Y - ca.Y, cb.Z - ca.Z);
                if (Math.Abs(Dot(d, na)) > 1.5)
                {
                    continue;
                }

                double dist = Math.Sqrt(Dot(d, d));
                if (dist > extents[a] + extents[b])
                {
                    continue;
                }

                parent[Find(a)] = Find(b);
            }
        }

        return records.GroupBy(Find).Select(g => g.OrderBy(r => r).ToArray()).OrderBy(c => c[0]).ToList();
    }

    private static MarkingPlacement? FromCluster(MeshLod lod, int[] cluster, MarkingPicture insignia, int lodIndex)
    {
        // The frame: origin at the cluster's vertex centroid, normal = the largest record's, U =
        // the model's +Z (nose) projected into the plane (+X when the plane faces along Z).
        List<(double X, double Y, double Z)> vertices = cluster.SelectMany(r => lod.Faces[r].Indices).Distinct().Select(lod.VertexPosition).ToList();
        if (vertices.Count < 3)
        {
            return null;
        }

        int largest = cluster.MaxBy(r => DecalConditioning.Newell(lod, lod.Faces[r].Indices).Area);
        (double X, double Y, double Z) normal = Unit(DecalConditioning.Newell(lod, lod.Faces[largest].Indices).Normal);
        (double, double, double) origin = (vertices.Average(v => v.X), vertices.Average(v => v.Y), vertices.Average(v => v.Z));
        (double X, double Y, double Z) u = ProjectInto(Math.Abs(normal.Z) > 0.9 ? (1.0, 0.0, 0.0) : (0.0, 0.0, 1.0), normal);
        (double X, double Y, double Z) v = Cross(normal, u);

        // A fuselage-side marking should read nose-forward for its viewer: U × V must be the
        // OUTWARD normal, and the picture's +X the viewer's right.  With U = +Z on the LEFT side
        // (normal −X) that holds; on the right side (normal +X) U must be −Z.
        if (Dot(Cross(u, v), normal) < 0)
        {
            u = Neg(u);
            v = Cross(normal, u);
        }

        double extentU = 0, extentV = 0;
        foreach ((double x, double y, double z) in vertices)
        {
            double dx = x - origin.Item1, dy = y - origin.Item2, dz = z - origin.Item3;
            extentU = Math.Max(extentU, Math.Abs((dx * u.X) + (dy * u.Y) + (dz * u.Z)));
            extentV = Math.Max(extentV, Math.Abs((dx * v.X) + (dy * v.Y) + (dz * v.Z)));
        }

        // Fit the picture's own box to the cluster's extent: a star fills the cluster's height, a
        // cross its width.
        SurfaceBounds b = insignia.ShapeBounds;
        double size = Math.Min((2.0 * extentU) / Math.Max(1e-6, b.Width), (2.0 * extentV) / Math.Max(1e-6, b.Height));
        if (!(size > 0.5))
        {
            return null;
        }

        // A cluster with records facing BOTH ways (a fin star painted on each side) is two-sided.
        bool twoSided = cluster.Any(r => Dot(Unit(DecalConditioning.Newell(lod, lod.Faces[r].Indices).Normal), normal) < -0.5);
        return new MarkingPlacement(
            insignia.Name, origin, u, v, size,
            Sides: Math.Abs(normal.Y) > 0.7 ? PlacementSides.Through : twoSided ? PlacementSides.Both : PlacementSides.Top,
            Mirror: PlacementMirror.None,
            Replaces: new Dictionary<int, int[]> { [lodIndex] = cluster },
            Note: $"bootstrap: shipped records {string.Join(",", cluster)}")
        {
            Key = "shipped:" + string.Join(",", cluster),
        };
    }

    /// <summary>
    /// Rule placements for a class with no shipped markings: the wing insignia at 60 % of each
    /// semi-span (upper left, lower right — the USAAF convention; both surfaces for the others),
    /// and a fuselage insignia on each side aft of the wing.
    /// </summary>
    /// <param name="lod">The shipped LOD.</param>
    /// <param name="insignia">The picture.</param>
    /// <param name="nation">The nation.</param>
    public static IReadOnlyList<MarkingPlacement> RulePlacements(MeshLod lod, MarkingPicture insignia, string? nation)
    {
        ArgumentNullException.ThrowIfNull(lod);
        ArgumentNullException.ThrowIfNull(insignia);
        List<MarkingPlacement> result = new List<MarkingPlacement>();
        MeshFace[] faces = lod.Faces;

        // The wing: the horizontal polygons (|n_y| > 0.8) — their vertices' X extent is the span.
        HashSet<int> wingVertices = new HashSet<int>();
        List<int> wingFaces = new List<int>();
        for (int r = 0; r < faces.Length; r++)
        {
            MeshFace f = faces[r];
            if (f.Primitive != MeshPrimitive.Polygon || f.Indices.Length < 3)
            {
                continue;
            }

            ((double X, double Y, double Z) n, double area) = DecalConditioning.Newell(lod, f.Indices);
            (double X, double Y, double Z) un = Unit(n);
            if (Math.Abs(un.Y) > 0.8 && area > 40.0)
            {
                wingFaces.Add(r);
                foreach (int i in f.Indices)
                {
                    wingVertices.Add(i);
                }
            }
        }

        if (wingVertices.Count >= 3)
        {
            List<(double X, double Y, double Z)> pts = wingVertices.Select(lod.VertexPosition).ToList();
            double halfSpan = pts.Max(p => Math.Abs(p.X));
            double station = 0.6 * halfSpan;
            foreach (double sign in new[] { -1.0, 1.0 })
            {
                // The chord at the station: where the wing polygons' edges cross x = station.
                double x = sign * station;
                double zMin = double.MaxValue, zMax = double.MinValue, ySum = 0;
                int crossings = 0;
                foreach (int r in wingFaces)
                {
                    int[] idx = faces[r].Indices;
                    for (int k = 0; k < idx.Length; k++)
                    {
                        (double X, double Y, double Z) a = lod.VertexPosition(idx[k]);
                        (double X, double Y, double Z) b = lod.VertexPosition(idx[(k + 1) % idx.Length]);
                        if ((a.X <= x) == (b.X <= x) || Math.Abs(b.X - a.X) < 1e-9)
                        {
                            continue;
                        }

                        double t = (x - a.X) / (b.X - a.X);
                        double z = a.Z + ((b.Z - a.Z) * t);
                        zMin = Math.Min(zMin, z);
                        zMax = Math.Max(zMax, z);
                        ySum += a.Y + ((b.Y - a.Y) * t);
                        crossings++;
                    }
                }

                if (crossings < 2)
                {
                    continue;
                }

                double y = ySum / crossings;
                double chord = zMax - zMin;
                double size = 0.55 * chord / Math.Max(1e-6, insignia.ShapeBounds.Height);
                (double, double y, double) origin = (sign * station, y, 0.5 * (zMin + zMax));
                // Upper surface on the left wing, lower on the right (USAAF); both for the others.
                bool upper = nation != "usa" || sign < 0;
                bool lower = nation != "usa" || sign > 0;
                // The picture's +Y runs toward the nose on both surfaces.  From ABOVE (facing −Y,
                // nose up the screen) the viewer's right is −X, so U = −X and U × V = +Y, the
                // upper skin's normal; from BELOW it is +X and U × V = −Y.
                if (upper)
                {
                    result.Add(new MarkingPlacement(insignia.Name, origin, (-1.0, 0.0, 0.0), (0.0, 0.0, 1.0), size,
                        Sides: PlacementSides.Top, Note: "rule: wing upper") { Key = "rule:wing upper" });
                }

                if (lower)
                {
                    result.Add(new MarkingPlacement(insignia.Name, origin, (1.0, 0.0, 0.0), (0.0, 0.0, 1.0), size,
                        Sides: PlacementSides.Top, Note: "rule: wing lower") { Key = "rule:wing lower" });
                }
            }
        }

        // The fuselage: the largest side-facing polygon (|n_x| > 0.8) aft of the wing's centre.
        double wingZ = wingVertices.Count > 0 ? wingVertices.Select(i => lod.VertexPosition(i).Z).Average() : 0.0;
        int best = -1;
        double bestArea = 0;
        for (int r = 0; r < faces.Length; r++)
        {
            MeshFace f = faces[r];
            if (f.Primitive != MeshPrimitive.Polygon || f.Indices.Length < 3)
            {
                continue;
            }

            ((double X, double Y, double Z) n, double area) = DecalConditioning.Newell(lod, f.Indices);
            (double X, double Y, double Z) un = Unit(n);
            (double X, double Y, double Z) c = Centroid(lod, f.Indices);
            if (un.X < -0.8 && c.X < -1.5 && c.Z < wingZ - 2.0 && area > bestArea)
            {
                best = r;
                bestArea = area;
            }
        }

        if (best >= 0)
        {
            (double X, double Y, double Z) c = Centroid(lod, faces[best].Indices);
            double height = 0;
            foreach (int i in faces[best].Indices)
            {
                height = Math.Max(height, Math.Abs(lod.VertexPosition(i).Y - c.Y));
            }

            double size = 1.6 * height / Math.Max(1e-6, insignia.ShapeBounds.Height);
            result.Add(new MarkingPlacement(insignia.Name, c, (0.0, 0.0, 1.0), (0.0, 1.0, 0.0), size,
                Sides: PlacementSides.Top, Mirror: PlacementMirror.X, Note: "rule: fuselage side") { Key = "rule:fuselage side" });
        }

        return result;
    }

    private static (double X, double Y, double Z) Centroid(MeshLod lod, int[] indices)
    {
        double x = 0, y = 0, z = 0;
        foreach (int i in indices)
        {
            (double px, double py, double pz) = lod.VertexPosition(i);
            x += px;
            y += py;
            z += pz;
        }

        return (x / indices.Length, y / indices.Length, z / indices.Length);
    }

    private static double Extent(MeshLod lod, int[] indices)
    {
        (double X, double Y, double Z) c = Centroid(lod, indices);
        double e = 0;
        foreach (int i in indices)
        {
            (double x, double y, double z) = lod.VertexPosition(i);
            e = Math.Max(e, Math.Sqrt(((x - c.X) * (x - c.X)) + ((y - c.Y) * (y - c.Y)) + ((z - c.Z) * (z - c.Z))));
        }

        return e;
    }

    private static (double X, double Y, double Z) ProjectInto((double X, double Y, double Z) v, (double X, double Y, double Z) n)
    {
        double d = Dot(v, n);
        return Unit((v.X - (d * n.X), v.Y - (d * n.Y), v.Z - (d * n.Z)));
    }

    private static (double X, double Y, double Z) Unit((double X, double Y, double Z) v) => MarkingPlacement.Unit(v);

    private static (double X, double Y, double Z) Neg((double X, double Y, double Z) v) => (-v.X, -v.Y, -v.Z);

    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Unit(((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X)));

    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
}
