using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Render.Map;

/// <summary>
/// A scenery class's TOP-DOWN FOOTPRINT: its mesh's edges projected onto world x/z, in world units,
/// cached once per mesh.
/// </summary>
/// <remarks>
/// <para>
/// This is the same construction the mission editor's map panel uses
/// (<c>CYAC.Formats/Mesh/SceneryFootprint.cs</c>, iteration 1), rebuilt over the port's own
/// <see cref="MeshModel"/> so the renderer stays pure — no image reads, no <c>CYAC.Formats</c>
/// reference.
/// </para>
/// <para>
/// <b>Why the COARSEST LOD.</b>  A registry object holds up to three LODs and the engine draws one
/// (<c>mesh_visibility_lod_select @image@0x16BE8</c>, index 0 = farthest).  A map is the far view,
/// and at LOD 0 the two classes that make a theatre's picture ARE single edges: a river's is
/// <c>(0,0,±1024)</c> and a road's <c>(0,0,±2048)</c>, which multiplied by their class scale
/// (<c>2^desc[+0x0C]</c> = ×4) give the 8,192- and 16,384-unit lines that
/// <c>g_map_course_half_len_table [0x2F8A]</c> hands the original's own map drawer
/// (<c>briefing_map_screen_obj_line_draw @image@0x1EA8D</c>, half-lengths 0x1000 / 0x2000).
/// <b>So for rivers and roads this footprint IS the original's map line, from an independent table.</b>
/// Every other class is a port addition.
/// </para>
/// <para>
/// A class whose coarsest LOD carries no edges at all (points, discs) falls back to its densest, so
/// a glyph-less class still shows something rather than nothing — the same fallback
/// <c>SceneryFootprints.Build</c> makes.
/// </para>
/// </remarks>
internal sealed class MapFootprints
{
    /// <summary>One footprint edge in MODEL-scaled world units, with the colour its record carries.</summary>
    /// <param name="X0">First end, x.</param>
    /// <param name="Z0">First end, z.</param>
    /// <param name="X1">Second end, x.</param>
    /// <param name="Z1">Second end, z.</param>
    /// <param name="ColorIndex">The record's palette index.</param>
    internal readonly record struct Segment(
        double X0, double Z0, double X1, double Z1, byte ColorIndex);

    private readonly Dictionary<MeshModel, Segment[]> _cache = [];

    /// <summary>How many meshes have been decoded into the cache.</summary>
    public int CachedMeshes => _cache.Count;

    /// <summary>The footprint of one mesh, in world units around its own origin.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="worldScale">
    /// The instance's effective scale (<c>SceneInstance.EffectiveWorldScale</c>), i.e.
    /// <c>2^desc[+0x0C]</c> unless the instance overrides it.
    /// </param>
    /// <returns>Its edges; empty when the mesh has none at any LOD.</returns>
    public IReadOnlyList<Segment> Of(MeshModel mesh, double worldScale)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (_cache.TryGetValue(mesh, out Segment[]? cached))
        {
            return cached;
        }

        Segment[] segments = Build(mesh.Coarsest, worldScale);
        if (segments.Length == 0 && mesh.DensestLodIndex != mesh.Coarsest.Index)
        {
            segments = Build(mesh.Densest, worldScale);
        }

        _cache[mesh] = segments;
        return segments;
    }

    private static Segment[] Build(MeshLod lod, double worldScale)
    {
        List<Segment> segments = new List<Segment>();
        foreach (MeshFace face in lod.Faces)
        {
            switch (face.Primitive)
            {
                case MeshPrimitive.Line:
                    // A LINE record is a POLYLINE: consecutive indices are its segments
                    // (mesh_poly_emit_op1_line_edge @image@0x1B31A walks the index list in pairs).
                    for (int i = 0; i + 1 < face.Indices.Length; i++)
                    {
                        Add(segments, lod, face, face.Indices[i], face.Indices[i + 1], worldScale);
                    }

                    break;

                case MeshPrimitive.Polygon:
                    // A filled N-gon contributes its OUTLINE — a map is a plan, so a building is its
                    // footprint edge, not a blob.
                    for (int i = 0; i < face.Indices.Length; i++)
                    {
                        int next = (i + 1) % face.Indices.Length;
                        if (face.Indices.Length > 2 || i == 0)
                        {
                            Add(segments, lod, face, face.Indices[i], face.Indices[next], worldScale);
                        }
                    }

                    break;

                default:
                    // Points, discs and the opcode-4 effect callbacks have no top-down extent worth
                    // a line; the map draws a glyph for the OBJECT instead where it matters.
                    break;
            }
        }

        return [.. segments];
    }

    private static void Add(
        List<Segment> into, MeshLod lod, in MeshFace face, int a, int b, double worldScale)
    {
        if ((uint)a >= (uint)lod.VertexCount || (uint)b >= (uint)lod.VertexCount || a == b)
        {
            return;
        }

        (int ax, _, int az) = lod.Vertex(a);
        (int bx, _, int bz) = lod.Vertex(b);
        double x0 = ax * worldScale, z0 = az * worldScale;
        double x1 = bx * worldScale, z1 = bz * worldScale;
        if (x0 == x1 && z0 == z1)
        {
            return;   // a vertical edge seen from above is a point
        }

        into.Add(new Segment(x0, z0, x1, z1, face.ColorIndex));
    }
}
