namespace CYAC.Port.Core.Model.World;

/// <summary>
/// ASSET CONDITIONING for a z-buffer: the DECAL LIFT.  Finds the shape records that are painted
/// markings on a larger surface — a MiG's red star on its fin, a Sabre's yellow band on its fuselage,
/// a Luftwaffe cross, a B-17's windows — and gives each a small outward LIFT so a depth test puts it
/// in front of the surface it decorates.
/// </summary>
/// <remarks>
/// <para>
/// The original never had this problem: <c>mesh_poly_tree_walk @image@0x1A8A8</c> is a PAINTER'S
/// algorithm, the tree lists a marking after the panel under it, and the later polygon simply
/// overwrites the earlier one.  The port's renderer keeps a depth buffer ("written
/// from scratch, never the 1991 projector"), and two coplanar polygons tie in depth — or, when the
/// authored marking sits a unit INSIDE a faceted fuselage (the Sabre's band: its quads lie up to 1.2
/// model units inside the facets they decorate), the surface wins outright.  So the star vanished and
/// only the top of the band showed.
/// </para>
/// <para>
/// The rule is GEOMETRIC, not order-based, because the painter's order is camera-dependent: a record
/// <c>D</c> is a decal on <c>B</c> when <c>B</c> is a polygon of at least <see cref="MinAreaRatio"/>
/// times <c>D</c>'s area, the two face the same way (|cos| ≥ <see cref="MinNormalAgreement"/>),
/// every vertex of <c>D</c> lies within <see cref="MaxDistanceModelUnits"/> of <c>B</c>'s plane and
/// <c>D</c>'s centroid projects INSIDE <c>B</c>.  The SMALLEST such <c>B</c> — the immediate parent — so a marking on a marking stacks, see
/// <see cref="Apply"/>.  The lift
/// is along <c>D</c>'s OWN outward normal (Newell's, in index order — the side a single-sided record
/// shows) by <c>epsilon − minDistance</c>, i.e. just enough to put <c>D</c>'s deepest vertex
/// <c>epsilon</c> above the base plane; a record already in front of its base is left alone.  The
/// offset is applied at draw time — per VERTEX (<see cref="MeshLod.VertexLifts"/>) when the decal's
/// vertices belong to decals only, so neighbouring markings that share an edge (the band's quads)
/// move together, and per RECORD (<see cref="MeshFace.LiftX"/>…) when a vertex is shared with the
/// base — so the INT vertex array, the shipped data, is untouched.
/// </para>
/// <para>
/// Measured on the shipped tree with the defaults: 103 records across the fleet (the census is) —
/// every star, cross and band, no wing panel (adjacent coplanar panels fail the centroid-inside
/// test).  The lift is applied in the renderer AFTER the gear pose, as a model-space constant, so a
/// record on an articulated leg is lifted too (the jets' articulation runs span the fin vertices).
/// </para>
/// </remarks>
public static class DecalConditioning
{
    /// <summary>
    /// The default lift above the base plane, in model units.  A third: with three stacking levels the
    /// Balkenkreuz stood 0.9 units off the wing; it is cut to a half or a third "to not stick out
    /// that much".  Depth precision is ample: 0.1 units at 3,000 units of range is still 3e-5 of 1/z,
    /// well above float's 6e-8.
    /// </summary>
    public const double DefaultEpsilonModelUnits = 0.1;

    /// <summary>A base must have at least this many times the decal's area.</summary>
    public const double MinAreaRatio = 2.0;

    /// <summary>|cos| between the two normals must be at least this.</summary>
    public const double MinNormalAgreement = 0.95;

    /// <summary>Every decal vertex must lie within this distance of the base plane.</summary>
    public const double MaxDistanceModelUnits = 2.0;

    /// <summary>One conditioned record: which record, its base, and the lift applied.</summary>
    /// <param name="Record">The decal's record index.</param>
    /// <param name="Base">The base's record index — the SMALLEST qualifying polygon enclosing the decal (its immediate parent).</param>
    /// <param name="MinDistance">The decal's deepest vertex relative to the base plane (negative = inside).</param>
    /// <param name="Lift">The lift applied along the decal's own normal at its deepest vertex, model units.</param>
    /// <param name="Level">
    /// The stacking level: 1 for a marking on a surface, 2 for a marking on a marking (the MiG's grey notch triangle
    /// on its red star), and so on.  A level-n decal is lifted above its parent's OWN lifted position, so the
    /// painter's stack the original relied on survives the depth test.
    /// </param>
    /// <param name="Parent">
    /// The DECAL this one is painted over (−1 when it lies directly on the surface): the record painted EARLIER in
    /// the same paint-tree leaf, coplanar with this one, whose polygon contains this one's centroid.  Paint order is
    /// the original's stacking order.
    /// </param>
    public readonly record struct Decal(int Record, int Base, double MinDistance, double Lift, int Level = 1, int Parent = -1);

    /// <summary>
    /// Returns the LOD with every decal record given its lift, and the list of what was lifted.
    /// </summary>
    /// <param name="lod">The LOD as built from the documents.</param>
    /// <param name="epsilon">The lift above the base plane, model units; 0 or less = no conditioning.</param>
    /// <param name="found">Receives the decals found (also when <paramref name="epsilon"/> ≤ 0).</param>
    /// <remarks>
    /// <para>
    /// Two corrections, both found by an A/B against the original:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Decals stack.</b>  The MiG-15's star is THREE records on the fin: two red triangles
    /// (an upright and an inverted one, together a five-pointed star) and a small FIN-COLOURED
    /// triangle painted over them to cut the notch between the star's two lower legs (LOD2 records
    /// 48/49/50 and their mirrored twins 51/52/53).  Giving all three the same lift makes the notch
    /// tie with the red in depth and lose, so the red connects two vertices it should not.  The
    /// base is now the SMALLEST enclosing qualifying polygon (the immediate parent), records are
    /// classified largest first, and a decal whose parent is itself a lifted decal is lifted above
    /// the parent's lifted position (<see cref="Decal.Level"/>).</item>
    /// <item><b>A vertex is lifted by ITS OWN need.</b>  H20 lifted every node of a per-vertex decal by
    /// the record's DEEPEST need; the Sabre's band quads meet at the spine vertex (0, 6, −12), which
    /// already sits 0.49 units in FRONT of its facets while the quads' lower corners are 1.22 inside,
    /// so the spine was pushed ~1.8 units up out of the fuselage — "the yellow stripe sticks out at the
    /// top of the plane's back".  Each vertex now clears each of its records' base planes by exactly
    /// epsilon where it was behind them and is left alone where it already was in front.</item>
    /// </list>
    /// </remarks>
    public static MeshLod Apply(MeshLod lod, double epsilon, ICollection<Decal>? found = null)
    {
        ArgumentNullException.ThrowIfNull(lod);
        MeshFace[] faces = lod.Faces;
        int n = faces.Length;
        (double X, double Y, double Z)?[] normals = new (double X, double Y, double Z)?[n];
        double[] areas = new double[n];
        (double X, double Y, double Z)[] centroids = new (double X, double Y, double Z)[n];
        for (int i = 0; i < n; i++)
        {
            ref readonly MeshFace face = ref faces[i];
            if (face.Primitive != MeshPrimitive.Polygon || face.Indices.Length < 3)
            {
                continue;
            }

            ((double X, double Y, double Z) normal, double area) = Newell(lod, face.Indices);
            if (area <= 0.0)
            {
                continue;
            }

            normals[i] = normal;
            areas[i] = area;
            centroids[i] = Centroid(lod, face.Indices);
        }

        // Largest first, so a decal's parent — at least MinAreaRatio times larger — is classified,
        // and has its lift, before the decal itself is looked at.
        List<int> order = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            if (normals[i] is not null)
            {
                order.Add(i);
            }
        }

        order.Sort((p, q) => areas[q].CompareTo(areas[p]));

        int[] rootBase = new int[n];         // the SURFACE record the marking lies on, or -1
        Array.Fill(rootBase, -1);
        double[] rootLift = new double[n];      // epsilon above that surface at the deepest vertex
        double[] rootMin = new double[n];
        (double X, double Y, double Z)[] decalNormal = new (double X, double Y, double Z)[n];
        double[][] vertexDistance = new double[n][];   // each vertex's signed distance to the surface plane
        int[] level = new int[n];

        foreach (int i in order)
        {
            (double X, double Y, double Z) nd = normals[i]!.Value;
            int best = -1;
            double bestArea = 0.0, bestMin = 0.0, bestMax = 0.0;
            double[]? bestDistances = null;
            foreach (int j in order)
            {
                if (j == i || areas[j] < MinAreaRatio * areas[i])
                {
                    continue;
                }

                (double X, double Y, double Z) nb = normals[j]!.Value;
                double cos = (nd.X * nb.X) + (nd.Y * nb.Y) + (nd.Z * nb.Z);
                if (Math.Abs(cos) < MinNormalAgreement)
                {
                    continue;
                }

                // The base plane oriented like the decal's own normal.
                (double X, double Y, double Z) nOriented = cos < 0 ? (X: -nb.X, Y: -nb.Y, Z: -nb.Z) : nb;
                (double bx, double by, double bz) = lod.VertexPosition(faces[j].Indices[0]);
                double d = -((nOriented.X * bx) + (nOriented.Y * by) + (nOriented.Z * bz));
                double[] distances = new double[faces[i].Indices.Length];
                double min = double.MaxValue, max = double.MinValue;
                for (int k = 0; k < distances.Length; k++)
                {
                    (double x, double y, double z) = lod.VertexPosition(faces[i].Indices[k]);
                    double dist = (nOriented.X * x) + (nOriented.Y * y) + (nOriented.Z * z) + d;
                    distances[k] = dist;
                    min = Math.Min(min, dist);
                    max = Math.Max(max, dist);
                }

                if (Math.Max(Math.Abs(min), Math.Abs(max)) > MaxDistanceModelUnits)
                {
                    continue;
                }

                if (!Inside(lod, faces[j].Indices, centroids[i], nb))
                {
                    continue;
                }

                // The LARGEST enclosing polygon is the SURFACE the marking lies on (its root base);
                // stacking is decided by PAINT ORDER below, not by area: the Balkenkreuz's black
                // bars are less than twice smaller than the white bars under them and the area rule
                // missed them.
                if (areas[j] > bestArea)
                {
                    best = j;
                    bestArea = areas[j];
                    bestMin = min;
                    bestMax = max;
                    bestDistances = distances;
                }
            }

            if (best < 0)
            {
                continue;
            }

            // A record drawn from BOTH sides (not culled) has no "front": the viewer may be on either
            // side of a zero-thickness wing, so it must clear the surface plane by epsilon on WHICHEVER
            // side faces the eye — epsilon plus its whole offset range, applied toward the eye by the
            // renderer.  (The FW-190's bars sit 0.2..0.7 BELOW the wing plane: fine from below, hidden
            // from above — "= =" instead of the hollowed cross.) A single-sided record already epsilon
            // in front of its surface is left alone.
            double lift = faces[i].BackfaceCulled
                ? Math.Max(0.0, epsilon - bestMin)
                : epsilon + Math.Max(Math.Abs(bestMin), Math.Abs(bestMax));
            rootLift[i] = lift;
            rootBase[i] = best;
            rootMin[i] = bestMin;
            decalNormal[i] = nd;
            vertexDistance[i] = bestDistances!;
            level[i] = 1;
        }

        // ── stacking, in PAINT ORDER ────────────────────────────────────────────────────────
        // The original's tree paints a leaf's records in list order and a later record simply
        // covers an earlier one; a depth test needs the later one HIGHER.  A marking painted over
        // another marking — the MiG star's fin-coloured notch over its red triangles, the
        // Balkenkreuz's black bars over its white bars — is coplanar with it, lies within the base
        // tolerance of its plane, has its centroid inside it, and comes later in the SAME leaf.  Its
        // level is the parent's plus one and its lift clears the parent's lifted position by epsilon.
        int[] paintLeaf = new int[n];
        int[] paintPosition = new int[n];
        Array.Fill(paintLeaf, -1);
        foreach ((int leaf, int[] records) in lod.PaintLeaves)
        {
            for (int k = 0; k < records.Length; k++)
            {
                if ((uint)records[k] < (uint)n && paintLeaf[records[k]] < 0)
                {
                    paintLeaf[records[k]] = leaf;
                    paintPosition[records[k]] = k;
                }
            }
        }

        List<int> byPaint = new List<int>(order);
        byPaint.Sort((p, q) => paintLeaf[p] != paintLeaf[q] ? paintLeaf[p].CompareTo(paintLeaf[q]) : paintPosition[p].CompareTo(paintPosition[q]));
        int[] parentOf = new int[n];
        Array.Fill(parentOf, -1);
        double[] decalLift = new double[n];
        foreach (int i in byPaint)
        {
            if (rootBase[i] < 0)
            {
                continue;
            }

            decalLift[i] = rootLift[i];
            if (paintLeaf[i] < 0)
            {
                continue;
            }

            (double X, double Y, double Z) nd = decalNormal[i];
            int parent = -1;
            double parentMin = 0.0, parentMax = 0.0;
            foreach (int j in byPaint)
            {
                if (j == i || rootBase[j] < 0 || paintLeaf[j] != paintLeaf[i] || paintPosition[j] >= paintPosition[i])
                {
                    continue;
                }

                (double X, double Y, double Z) nb = decalNormal[j];
                double cos = (nd.X * nb.X) + (nd.Y * nb.Y) + (nd.Z * nb.Z);
                if (Math.Abs(cos) < MinNormalAgreement)
                {
                    continue;
                }

                (double X, double Y, double Z) nOriented = cos < 0 ? (X: -nb.X, Y: -nb.Y, Z: -nb.Z) : nb;
                (double bx, double by, double bz) = lod.VertexPosition(faces[j].Indices[0]);
                double d = -((nOriented.X * bx) + (nOriented.Y * by) + (nOriented.Z * bz));
                double min = double.MaxValue, max = double.MinValue;
                foreach (int v in faces[i].Indices)
                {
                    (double x, double y, double z) = lod.VertexPosition(v);
                    double dist = (nOriented.X * x) + (nOriented.Y * y) + (nOriented.Z * z) + d;
                    min = Math.Min(min, dist);
                    max = Math.Max(max, dist);
                }

                if (Math.Max(Math.Abs(min), Math.Abs(max)) > MaxDistanceModelUnits
                    || !Inside(lod, faces[j].Indices, centroids[i], nb))
                {
                    continue;
                }

                // The highest stack under this record; among equals the latest painted.
                if (parent < 0 || level[j] > level[parent] || (level[j] == level[parent] && paintPosition[j] > paintPosition[parent]))
                {
                    parent = j;
                    parentMin = min;
                    parentMax = max;
                }
            }

            if (parent >= 0)
            {
                parentOf[i] = parent;
                level[i] = level[parent] + 1;
                double aboveParent = faces[i].BackfaceCulled
                    ? Math.Max(0.0, epsilon - parentMin)
                    : epsilon + Math.Max(Math.Abs(parentMin), Math.Abs(parentMax));
                decalLift[i] = Math.Max(rootLift[i], decalLift[parent] + aboveParent);
            }
        }

        int[] decalOf = new int[n];
        Array.Fill(decalOf, -1);
        foreach (int i in order)
        {
            if (rootBase[i] < 0)
            {
                continue;
            }

            found?.Add(new Decal(i, rootBase[i], rootMin[i], decalLift[i], level[i], parentOf[i]));
            if (epsilon > 0.0 && decalLift[i] > 0.0)
            {
                decalOf[i] = rootBase[i];
            }
        }

        // ── the offsets ─────────────────────────────────────────────────────────────────────
        // Per VERTEX where every polygon using the vertex is a decal (the Sabre's band quads share
        // their corners with each other only): one offset per shared node, along the mean of the
        // decal normals there, long enough that the node clears EACH of its records' base planes by
        // that record's own need at that node — so neighbouring decals keep meeting at their shared
        // edge instead of opening a sliver, and a node already in front of its planes stays put.
        // Per RECORD where a decal shares a vertex with a non-decal polygon (a marking drawn with
        // the panel's own corner).
        int vertexCount = lod.VertexCount;
        bool[] usedByBase = new bool[vertexCount];
        for (int i = 0; i < n; i++)
        {
            if (decalOf[i] >= 0 || faces[i].Primitive != MeshPrimitive.Polygon)
            {
                continue;
            }

            foreach (int v in faces[i].Indices)
            {
                if ((uint)v < (uint)vertexCount)
                {
                    usedByBase[v] = true;
                }
            }
        }

        // A vertex where two decals DISAGREE on direction (a star's two mirrored sides share their
        // corners, one facing +X and one −X) cannot take one offset either: those records lift per
        // record, each along its own side.
        bool[] conflict = new bool[vertexCount];
        {
            (double X, double Y, double Z)[] probe = new (double X, double Y, double Z)[vertexCount];
            int[] uses = new int[vertexCount];
            for (int i = 0; i < n; i++)
            {
                if (decalOf[i] < 0)
                {
                    continue;
                }

                foreach (int v in faces[i].Indices)
                {
                    if ((uint)v < (uint)vertexCount)
                    {
                        probe[v] = (probe[v].X + decalNormal[i].X, probe[v].Y + decalNormal[i].Y, probe[v].Z + decalNormal[i].Z);
                        uses[v]++;
                    }
                }
            }

            for (int v = 0; v < vertexCount; v++)
            {
                double length = Math.Sqrt((probe[v].X * probe[v].X) + (probe[v].Y * probe[v].Y) + (probe[v].Z * probe[v].Z));
                // Opposite sides cancel (length → 0); two decals meeting at a crease do not
                // (two normals 90° apart still sum to 1.41 of 2).
                conflict[v] = uses[v] > 0 && length < 0.5 * uses[v];
            }
        }

        // The split is TRANSITIVE: a record that must lift per record blocks its vertices, and any
        // decal touching a blocked vertex lifts per record too — otherwise a vertex on the seam
        // between the two kinds would be lifted twice (once with its record, once as a node).
        // A STACKED decal (level ≥ 2) lifts per record as well: its nodes may be shared with the
        // decal under it, which needs a different height at the same node.
        bool[] perRecord = new bool[n];
        bool[] blocked = new bool[vertexCount];
        for (int i = 0; i < n; i++)
        {
            if (decalOf[i] < 0)
            {
                continue;
            }

            if (level[i] > 1)
            {
                perRecord[i] = true;
            }

            // A record drawn from BOTH sides (not back-face culled: the Balkenkreuz bars, tag 0x00,
            // on a zero-thickness wing) must lift TOWARD THE EYE, which only the renderer knows per
            // frame and only applies on the per-record path (SceneRenderer flips the record offset on
            // a back-facing view; per-vertex offsets are baked in model space). Without this the
            // FW-190's white vertical bar went under the wing and the top view showed "= =" instead
            // of the hollowed cross.
            if (!faces[i].BackfaceCulled)
            {
                perRecord[i] = true;
            }

            foreach (int v in faces[i].Indices)
            {
                if ((uint)v >= (uint)vertexCount || usedByBase[v] || conflict[v])
                {
                    perRecord[i] = true;
                }
            }
        }

        for (bool changed = true; changed;)
        {
            changed = false;
            for (int i = 0; i < n; i++)
            {
                if (!perRecord[i])
                {
                    continue;
                }

                foreach (int v in faces[i].Indices)
                {
                    if ((uint)v < (uint)vertexCount)
                    {
                        blocked[v] = true;
                    }
                }
            }

            for (int i = 0; i < n; i++)
            {
                if (decalOf[i] < 0 || perRecord[i])
                {
                    continue;
                }

                foreach (int v in faces[i].Indices)
                {
                    if ((uint)v < (uint)vertexCount && blocked[v])
                    {
                        perRecord[i] = true;
                        changed = true;
                        break;
                    }
                }
            }
        }

        MeshFace[]? lifted = null;
        double[]? vertexLifts = null;
        (double X, double Y, double Z)[] sum = new (double X, double Y, double Z)[vertexCount];
        List<(double Need, (double X, double Y, double Z) Normal)>?[] needs = new List<(double Need, (double X, double Y, double Z) Normal)>?[vertexCount];
        for (int i = 0; i < n; i++)
        {
            if (decalOf[i] < 0)
            {
                continue;
            }

            if (perRecord[i])
            {
                lifted ??= (MeshFace[])faces.Clone();
                (double X, double Y, double Z) nd = decalNormal[i];
                lifted[i] = faces[i] with { LiftX = nd.X * decalLift[i], LiftY = nd.Y * decalLift[i], LiftZ = nd.Z * decalLift[i] };
                continue;
            }

            int[] indices = faces[i].Indices;
            for (int k = 0; k < indices.Length; k++)
            {
                int v = indices[k];
                // This node's OWN need for this record: epsilon above the parent plane (plus the
                // parent's lift), or nothing where it already is.
                double need = Math.Max(0.0, epsilon - vertexDistance[i][k]);   // level 1 only: stacked records lift per record
                sum[v] = (sum[v].X + decalNormal[i].X, sum[v].Y + decalNormal[i].Y, sum[v].Z + decalNormal[i].Z);
                (needs[v] ??= new List<(double, (double, double, double))>(2)).Add((need, decalNormal[i]));
            }
        }

        for (int v = 0; v < vertexCount; v++)
        {
            if (needs[v] is not { } list)
            {
                continue;
            }

            double maxNeed = 0.0;
            foreach ((double need, (double X, double Y, double Z) _) in list)
            {
                maxNeed = Math.Max(maxNeed, need);
            }

            if (maxNeed <= 0.0)
            {
                continue;
            }

            double length = Math.Sqrt((sum[v].X * sum[v].X) + (sum[v].Y * sum[v].Y) + (sum[v].Z * sum[v].Z));
            if (length <= 1e-9)
            {
                continue;
            }

            (double X, double Y, double Z) dir = (X: sum[v].X / length, Y: sum[v].Y / length, Z: sum[v].Z / length);

            // Along the MEAN normal, long enough that the projection onto EACH contributing normal
            // is still that record's need at this node (the mean of two normals at a crease is
            // shorter along either), capped at three times the largest need.
            double magnitude = 0.0;
            foreach ((double need, (double X, double Y, double Z) normal) in list)
            {
                if (need <= 0.0)
                {
                    continue;
                }

                double along = Math.Max(0.3, (normal.X * dir.X) + (normal.Y * dir.Y) + (normal.Z * dir.Z));
                magnitude = Math.Max(magnitude, need / along);
            }

            magnitude = Math.Min(magnitude, 3.0 * maxNeed);
            vertexLifts ??= new double[vertexCount * 3];
            vertexLifts[v * 3] = dir.X * magnitude;
            vertexLifts[(v * 3) + 1] = dir.Y * magnitude;
            vertexLifts[(v * 3) + 2] = dir.Z * magnitude;
        }

        return lifted is null && vertexLifts is null
            ? lod
            : new MeshLod(
                lod.Index, lod.Vertices, lifted ?? faces, lod.PaintLeafRecords, lod.PaintLeafArticulation, vertexLifts,
                lod.RefinedVertices, lod.VertexSources);   // the primary constructor: `with` would keep the cached per-face tables
    }

    /// <summary>Newell's outward normal (unit) and the polygon's area, in model units.</summary>
    /// <param name="lod">The LOD.</param>
    /// <param name="indices">The polygon's vertex indices.</param>
    public static ((double X, double Y, double Z) Normal, double Area) Newell(MeshLod lod, int[] indices)
    {
        ArgumentNullException.ThrowIfNull(lod);
        ArgumentNullException.ThrowIfNull(indices);
        double nx = 0, ny = 0, nz = 0;
        for (int k = 0; k < indices.Length; k++)
        {
            (double px, double py, double pz) = lod.VertexPosition(indices[k]);
            (double qx, double qy, double qz) = lod.VertexPosition(indices[(k + 1) % indices.Length]);
            nx += (py - qy) * (pz + qz);
            ny += (pz - qz) * (px + qx);
            nz += (px - qx) * (py + qy);
        }

        double length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        return length <= 0.0 ? ((0, 0, 0), 0.0) : ((nx / length, ny / length, nz / length), length / 2.0);
    }

    private static (double X, double Y, double Z) Centroid(MeshLod lod, int[] indices)
    {
        double x = 0, y = 0, z = 0;
        foreach (int v in indices)
        {
            (double vx, double vy, double vz) = lod.VertexPosition(v);
            x += vx;
            y += vy;
            z += vz;
        }

        return (x / indices.Length, y / indices.Length, z / indices.Length);
    }

    /// <summary>Even-odd point-in-polygon on the plane's two dominant axes.</summary>
    private static bool Inside(MeshLod lod, int[] polygon, (double X, double Y, double Z) p, (double X, double Y, double Z) normal)
    {
        int axis = Math.Abs(normal.X) >= Math.Abs(normal.Y) && Math.Abs(normal.X) >= Math.Abs(normal.Z)
            ? 0
            : (Math.Abs(normal.Y) >= Math.Abs(normal.Z) ? 1 : 2);
        int u = axis == 0 ? 1 : 0;
        int v = axis == 2 ? 1 : 2;
        double px = Component(p, u), py = Component(p, v);
        bool inside = false;
        for (int i = 0; i < polygon.Length; i++)
        {
            (double X, double Y, double Z) a = lod.VertexPosition(polygon[i]);
            (double X, double Y, double Z) b = lod.VertexPosition(polygon[(i + 1) % polygon.Length]);
            double ax = Component(a, u), ay = Component(a, v), bx = Component(b, u), by = Component(b, v);
            if ((ay > py) != (by > py))
            {
                double xi = ax + ((py - ay) * (bx - ax) / (by - ay));
                if (px < xi)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    private static double Component((double X, double Y, double Z) p, int k) => k == 0 ? p.X : (k == 1 ? p.Y : p.Z);

    private static double Component((int X, int Y, int Z) p, int k) => k == 0 ? p.X : (k == 1 ? p.Y : p.Z);
}
