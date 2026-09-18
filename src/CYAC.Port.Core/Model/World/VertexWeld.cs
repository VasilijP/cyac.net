namespace CYAC.Port.Core.Model.World;

/// <summary>
/// VERTEX WELD — the asset-conditioning pass that closes an UN-WELDED CORNER: two vertices of one LOD authored a
/// unit apart where the model meant one, so the faces on either side never meet and the seam shows as a hole, a
/// crack or a hairline.
/// </summary>
/// <remarks>
/// <para>
/// At ε = 1 the rule fires three times in the shipped fleet. The first is the P-51's canopy/spine corner on the
/// left, LOD2 <c>v4 (-4,7,-20)</c> beside <c>v28 (-4,8,-20)</c> where the mirrored right side uses the single
/// <c>v3 (4,8,-20)</c> (<c>P19_geometry_report.md</c> §2.4, §4.3 — verified against the raw <c>.PNT</c> bytes).
/// Its three consequences are the unfilled triangle behind the canopy, a 30-unit crack down the flank and a
/// hairline along the rear belly; The other two are the Yak-9's LOD2 pair <c>v52→v35</c> and <c>v51→v36</c>, a
/// mirror-symmetric pair of one-unit steps at the nose underside (x ±4, records 33–36), which a viewer
/// confirmed as wanted on.
/// </para>
/// <para>
/// <b>The rule is deliberately narrow</b> (the report's own definition of an un-welded corner):
/// two distinct vertices within ε whose pair NO record bridges, and which BOTH carry a boundary
/// edge (an edge used by exactly one polygon).  A pair a record already joins is a real edge of
/// the model; a pair with no open edge is a fold the model closes another way; neither is touched.
/// Vertices inside an articulation run (a gear leg, a canopy) are never welded, so the runs stay
/// what the transform published.  At ε = 1 the rule fires on the P-51 corner and on nothing else in
/// the six flyables, and on those three welds across every class (<c>VertexWeldTests</c> pins both).
/// </para>
/// <para>
/// <b>What a weld does.</b>  The lower index survives; every record that used the other index is
/// re-indexed to it (new index arrays — the shipped ones are never written); the survivor takes
/// the POSITION of whichever of the two more records used (for the P-51 that is the canopy's
/// <c>y = 8</c>, five records against two, which is also the right side's height, so the model comes
/// out symmetric).  The position lives in the REFINED vertex layer (<see cref="MeshLod.RefinedVertices"/>);
/// the INT array is never written, so the integrity tests and the ground/map readers of
/// <see cref="MeshLod.Vertex"/> see the shipped node.  The removed vertex stays in the array,
/// unreferenced.  Runs BEFORE sheet inflation and the decal lift, so both see closed geometry.
/// </para>
/// </remarks>
public static class VertexWeld
{
    /// <summary>The default ε, model units: it welds the P-51's corner and the Yak-9's nose pair, and nothing else in the fleet.</summary>
    public const double DefaultEpsilonModelUnits = 1.0;

    /// <summary>One weld the pass performed.</summary>
    /// <param name="Removed">The vertex no record references any more.</param>
    /// <param name="Survivor">The vertex the records now share.</param>
    /// <param name="Distance">How far apart the two were, model units.</param>
    /// <param name="MovedSurvivor">Whether the survivor took the removed vertex's position.</param>
    /// <param name="Records">The records re-indexed, ascending.</param>
    public readonly record struct Weld(int Removed, int Survivor, double Distance, bool MovedSurvivor, int[] Records);

    /// <summary>Welds every un-welded corner of one LOD within <paramref name="epsilon"/>.</summary>
    /// <param name="lod">The LOD, as built (after hidden records are emptied, before inflation).</param>
    /// <param name="epsilon">The distance, model units; 0 or less disables the pass.</param>
    /// <param name="found">Receives one entry per weld, or null.</param>
    /// <returns>The LOD, or the same instance when nothing qualified.</returns>
    public static MeshLod Apply(MeshLod lod, double epsilon, ICollection<Weld>? found = null)
    {
        ArgumentNullException.ThrowIfNull(lod);
        IReadOnlyList<Weld> welds = Find(lod, epsilon);
        if (welds.Count == 0)
        {
            return lod;
        }

        int n = lod.VertexCount;
        double[] refined = new double[n * 3];
        for (int i = 0; i < n; i++)
        {
            (double x, double y, double z) = lod.VertexPosition(i);
            refined[i * 3] = x;
            refined[(i * 3) + 1] = y;
            refined[(i * 3) + 2] = z;
        }

        MeshFace[] faces = (MeshFace[])lod.Faces.Clone();
        foreach (Weld weld in welds)
        {
            if (weld.MovedSurvivor)
            {
                refined[weld.Survivor * 3] = refined[weld.Removed * 3];
                refined[(weld.Survivor * 3) + 1] = refined[(weld.Removed * 3) + 1];
                refined[(weld.Survivor * 3) + 2] = refined[(weld.Removed * 3) + 2];
            }

            foreach (int record in weld.Records)
            {
                MeshFace face = faces[record];
                int[] indices = (int[])face.Indices.Clone();
                for (int i = 0; i < indices.Length; i++)
                {
                    if (indices[i] == weld.Removed)
                    {
                        indices[i] = weld.Survivor;
                    }
                }

                faces[record] = face with { Indices = indices };
            }

            found?.Add(weld);
        }

        // The PRIMARY constructor, never `with` (MeshLod.WithFaces' remarks).
        return new MeshLod(
            lod.Index,
            lod.Vertices,
            faces,
            lod.PaintLeafRecords,
            lod.PaintLeafArticulation,
            lod.VertexLifts,
            refined,
            lod.VertexSources,
            lod.Markings);
    }

    /// <summary>The un-welded corners of a LOD within <paramref name="epsilon"/>, without applying them.</summary>
    /// <param name="lod">The LOD.</param>
    /// <param name="epsilon">The distance, model units; 0 or less finds nothing.</param>
    public static IReadOnlyList<Weld> Find(MeshLod lod, double epsilon)
    {
        ArgumentNullException.ThrowIfNull(lod);
        List<Weld> welds = new List<Weld>();
        if (!(epsilon > 0.0))
        {
            return welds;
        }

        int n = lod.VertexCount;
        MeshFace[] faces = lod.Faces;

        // Edge census over the polygons, use counts, and which faces touch which vertex.
        Dictionary<(int, int), int> edgeUses = new Dictionary<(int, int), int>();
        int[] uses = new int[n];
        List<int>[] facesOf = new List<int>[n];
        for (int f = 0; f < faces.Length; f++)
        {
            MeshFace face = faces[f];
            if (face.Primitive != MeshPrimitive.Polygon || face.Indices.Length < 3)
            {
                continue;
            }

            int[] indices = face.Indices;
            for (int i = 0; i < indices.Length; i++)
            {
                int a = indices[i], b = indices[(i + 1) % indices.Length];
                if ((uint)a >= (uint)n || (uint)b >= (uint)n || a == b)
                {
                    continue;
                }

                (int, int) key = a < b ? (a, b) : (b, a);
                edgeUses[key] = edgeUses.TryGetValue(key, out int c) ? c + 1 : 1;
            }

            foreach (int v in indices.Distinct())
            {
                if ((uint)v < (uint)n)
                {
                    uses[v]++;
                    (facesOf[v] ??= []).Add(f);
                }
            }
        }

        bool[] boundary = new bool[n];
        foreach (((int a, int b), int count) in edgeUses)
        {
            if (count == 1)
            {
                boundary[a] = true;
                boundary[b] = true;
            }
        }

        // Articulation runs are off limits: the transform published them by index.
        bool[] protectedVertex = new bool[n];
        foreach (MeshArticulationBlock block in lod.PaintArticulation.Values)
        {
            for (int v = Math.Max(0, block.FirstVertex); v <= block.LastVertex && v < n; v++)
            {
                protectedVertex[v] = true;
            }

            if ((uint)block.PivotVertex < (uint)n)
            {
                protectedVertex[block.PivotVertex] = true;
            }
        }

        List<int> candidates = new List<int>();
        for (int v = 0; v < n; v++)
        {
            if (boundary[v] && !protectedVertex[v])
            {
                candidates.Add(v);
            }
        }

        bool[] taken = new bool[n];
        double epsilonSquared = epsilon * epsilon;
        for (int i = 0; i < candidates.Count; i++)
        {
            int a = candidates[i];
            if (taken[a])
            {
                continue;
            }

            (double ax, double ay, double az) = lod.VertexPosition(a);
            for (int j = i + 1; j < candidates.Count; j++)
            {
                int b = candidates[j];
                if (taken[b] || taken[a])
                {
                    continue;
                }

                (double bx, double by, double bz) = lod.VertexPosition(b);
                double dx = ax - bx, dy = ay - by, dz = az - bz;
                double distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);
                if (distanceSquared > epsilonSquared)
                {
                    continue;
                }

                (int a, int b) key = (a, b);
                bool bridged = edgeUses.ContainsKey(key) || facesOf[a]!.Any(f => facesOf[b]!.Contains(f));
                if (bridged)
                {
                    continue;
                }

                taken[a] = true;
                taken[b] = true;
                welds.Add(new Weld(
                    Removed: b,
                    Survivor: a,
                    Distance: Math.Sqrt(distanceSquared),
                    MovedSurvivor: uses[b] > uses[a],
                    Records: [.. facesOf[b]!.OrderBy(f => f)]));
                break;
            }
        }

        return welds;
    }
}
