using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Render.Ground;

/// <summary>
/// A flat quad read as a LINEAR ground feature: a centreline and an authored width.
/// </summary>
/// <param name="FirstA">First vertex of one short side.</param>
/// <param name="FirstB">Second vertex of that short side.</param>
/// <param name="SecondA">First vertex of the opposite short side.</param>
/// <param name="SecondB">Second vertex of the opposite short side.</param>
/// <param name="WidthWorldUnits">
/// The mean length of the two short sides in WORLD units — the feature's own authored width:
/// <c>road</c> 48 ft, <c>strip</c> 448 ft, <c>river</c> 1,024 ft.
/// </param>
/// <remarks>
/// <para>
/// Why the port needs to know: an exact-coverage renderer is HONEST about a thin bright feature — a 48-foot
/// road at 30,000 feet covers a fifth of a pixel and is drawn a fifth as bright, so it fades out, where the
/// 1991 renderer painted every pixel whose centre it touched at full colour and the road stayed a crisp line
/// to the horizon.  The port's rule settles it: <i>drawn width = max(physical width, on-screen
/// floor)</i>.  Applying that to a ribbon means WIDENING the quad in the ground plane until it meets the
/// floor, rather than dimming it — which is what <see cref="GroundBand"/> does (whose construction it
/// inherited when the layer folded into the pipeline), and only when the floor actually binds; at every closer
/// range the quad is drawn exactly as authored.
/// </para>
/// <para>
/// The centreline is the pair of short-side MIDPOINTS, so a ribbon widened at range keeps its
/// authored ends (square, not capped) and its authored direction.
/// </para>
/// </remarks>
public readonly record struct GroundRibbon(
    int FirstA, int FirstB, int SecondA, int SecondB, double WidthWorldUnits);

/// <summary>
/// Which of a class's shape records belong to the FLAT GROUND, worked out once per mesh.
/// </summary>
/// <remarks>
/// <para>
/// A record is promoted to the analytic ground layer when all four of these hold:
/// </para>
/// <list type="number">
/// <item>
/// its class is a GROUND DECAL — <c>MeshModel.IsTrue3DObject</c> is false, i.e. the registry slot's
/// <c>desc[+0]</c> render layer has no <c>0x80</c> bit.  That is the ORIGINAL's own division of the
/// world into decals and solids, so it needs no judgement of ours: <c>city 0x05</c>,
/// <c>airport</c>/<c>strip</c>/<c>revet 0x06</c>, <c>road 0x14</c>, <c>urban</c>/<c>rural 0x15</c>,
/// <c>crater 0x1E</c>, <c>river 0x28</c>, the aircraft shadows <c>0x32</c>.  It is what keeps
/// <c>sam</c>'s eighteen <c>y = 0</c> apron lines (a <c>0x80</c> solid) with their launcher;
/// </item>
/// <item>it is a POLYGON or a LINE (a disc is a screen-space circle, not a planar polygon; an
/// opcode-4 record is a callback);</item>
/// <item>it is SOLID — <c>record[+4] == 0xFF</c>.  A stippled ground record would need the original's
/// screen-space mask, which an exact-coverage fill cannot express, so it stays in the 3-D pass.  No
/// shipped ground-decal record is stippled;</item>
/// <item>every vertex it names has model <c>y == 0</c> exactly.  The vertices are INTEGERS, so this
/// is an exact test and needs no tolerance: <c>urban</c> LOD1's twenty records are two ground tiles
/// at <c>y = 0</c> and eighteen buildings at <c>y ∈ {8, 16}</c>, and the test separates them
/// cleanly.</item>
/// </list>
/// <para>
/// The INSTANCE must also be on the plane — <c>Y = 0</c>, no pitch, no roll — which every theatre
/// placement is (<c>WorldScene.FromTheater</c>: the <c>.W</c> stream places scenery with the
/// <c>ground</c> position tag, x and z only).  That is checked per instance by the renderer, not
/// here, because it is a property of the placement and not of the mesh.
/// </para>
/// </remarks>
public sealed class GroundFaceTable
{
    /// <summary>
    /// The aspect ratio above which a flat QUAD is a RIBBON — a linear ground feature with an
    /// authored width, rather than a field or a tile: <b>4 : 1</b>.
    /// </summary>
    /// <remarks>
    /// Measured, not chosen.  Over every flat quad in the shipped meshes exactly three exceed it,
    /// and they are exactly the three linear features the theatre draws: <c>road</c> LOD1 <b>48 ×
    /// 16,384</b> ft, <c>river</c> LOD1 <b>1,024 × 8,192</b> ft and <c>strip</c> <b>448 × 8,672</b>
    /// ft.  The next widest thing in the list is a <c>rural</c> field at 2: 1 and a <c>city</c>
    /// block at 2: 1, so the threshold has an order of magnitude of clearance either side (H15
    /// </remarks>
    public const double RibbonAspect = 4.0;

    private readonly Dictionary<int, bool[]> _byLod = [];
    private readonly Dictionary<int, GroundRibbon?[]> _ribbonsByLod = [];

    private GroundFaceTable(MeshModel mesh)
    {
        Mesh = mesh;
        LineClass = LineWidthModel.ClassOf(mesh.Basename);
        if (mesh.IsTrue3DObject)
        {
            return;
        }

        foreach (MeshLod lod in mesh.Lods)
        {
            bool[] flags = new bool[lod.Faces.Length];
            GroundRibbon?[] ribbons = new GroundRibbon?[lod.Faces.Length];
            bool any = false;
            for (int i = 0; i < lod.Faces.Length; i++)
            {
                // A record the paint tree never emits is not ground either.
                if (!lod.EmittedByPaintTree(i) || !IsFlat(lod, lod.Faces[i]))
                {
                    continue;
                }

                flags[i] = true;
                any = true;
                if (lod.Faces[i].Primitive == MeshPrimitive.Line)
                {
                    LineRecords++;
                }
                else
                {
                    PolygonRecords++;
                    ribbons[i] = RibbonOf(lod, lod.Faces[i], mesh.WorldScale);
                    if (ribbons[i] is not null)
                    {
                        RibbonRecords++;
                    }
                }
            }

            if (any)
            {
                _byLod[lod.Index] = flags;
                _ribbonsByLod[lod.Index] = ribbons;
            }
        }
    }

    private static readonly Dictionary<MeshModel, GroundFaceTable> Cache = [];

    /// <summary>The class this table describes.</summary>
    public MeshModel Mesh { get; }

    /// <summary>Which width class this class's LINE records are drawn at.</summary>
    public LineWidthClass LineClass { get; }

    /// <summary>How many flat POLYGON records the class has, over all its levels of detail.</summary>
    public int PolygonRecords { get; }

    /// <summary>How many flat LINE records it has, over all its levels of detail.</summary>
    public int LineRecords { get; }

    /// <summary>How many of the flat polygons are RIBBONS — linear features with an authored width.</summary>
    public int RibbonRecords { get; }

    /// <summary>Whether any level of detail has a flat record at all.</summary>
    public bool HasFlatRecords => _byLod.Count > 0;

    /// <summary>The promotion flags of one level of detail, or null when it has none.</summary>
    /// <param name="lodIndex">The LOD's own index.</param>
    public bool[]? FlagsFor(int lodIndex) => _byLod.GetValueOrDefault(lodIndex);

    /// <summary>The table for a mesh, built once and cached.</summary>
    /// <param name="mesh">The class.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is null.</exception>
    /// <remarks>
    /// The cache is keyed on the mesh INSTANCE.  A <see cref="MeshLibrary"/> hands out one
    /// <see cref="MeshModel"/> per class for the life of the process, so the table is built once per
    /// class per run.  Guarded because a host may render from more than one thread over its life
    /// (mode-13hx's rasteriser thread, a headless run's main thread).
    /// </remarks>
    public static GroundFaceTable For(MeshModel mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        lock (Cache)
        {
            if (!Cache.TryGetValue(mesh, out GroundFaceTable? table))
            {
                table = new GroundFaceTable(mesh);
                Cache[mesh] = table;
            }

            return table;
        }
    }

    /// <summary>
    /// The ribbon description of a flat polygon record, when it has one.
    /// </summary>
    /// <param name="lodIndex">The LOD's own index.</param>
    /// <param name="record">The record's index in that LOD.</param>
    public GroundRibbon? RibbonFor(int lodIndex, int record) =>
        _ribbonsByLod.TryGetValue(lodIndex, out GroundRibbon?[]? ribbons) && (uint)record < (uint)ribbons.Length
            ? ribbons[record]
            : null;

    /// <summary>Whether a record of a given LOD is drawn by the analytic ground layer.</summary>
    /// <param name="lodIndex">The LOD's own index.</param>
    /// <param name="record">The record's index in that LOD.</param>
    public bool IsFlat(int lodIndex, int record) =>
        _byLod.TryGetValue(lodIndex, out bool[]? flags)
        && (uint)record < (uint)flags.Length
        && flags[record];

    /// <summary>
    /// Reads a flat quad as a RIBBON — the two short sides and the width between the long ones.
    /// </summary>
    /// <param name="lod">The level of detail the record belongs to.</param>
    /// <param name="face">The record.</param>
    /// <param name="worldScale">The class's <c>2^scaleShiftExponent</c>.</param>
    /// <returns>The ribbon, or null when the quad is not one.</returns>
    private static GroundRibbon? RibbonOf(MeshLod lod, in MeshFace face, double worldScale)
    {
        int[] indices = face.Indices;
        if (face.Primitive != MeshPrimitive.Polygon || indices.Length != 4)
        {
            return null;
        }

        Span<double> sides = stackalloc double[4];
        for (int i = 0; i < 4; i++)
        {
            (int ax, _, int az) = lod.Vertex(indices[i]);
            (int bx, _, int bz) = lod.Vertex(indices[(i + 1) % 4]);
            double dx = (double)bx - ax, dz = (double)bz - az;
            sides[i] = Math.Sqrt((dx * dx) + (dz * dz));
        }

        double pairA = 0.5 * (sides[0] + sides[2]);      // sides (0,1) and (2,3)
        double pairB = 0.5 * (sides[1] + sides[3]);      // sides (1,2) and (3,0)
        if (pairA <= 0 || pairB <= 0)
        {
            return null;
        }

        // Only a true RECTANGLE may be rebuilt from a centreline and a width; anything else would
        // change shape the moment the floor did not bind.  All three shipped ribbons are rectangles.
        const double Square = 1e-6;
        if (Math.Abs(sides[0] - sides[2]) > Square * pairA
            || Math.Abs(sides[1] - sides[3]) > Square * pairB)
        {
            return null;
        }

        if (pairA * RibbonAspect < pairB)
        {
            return new GroundRibbon(
                indices[0], indices[1], indices[2], indices[3], pairA * worldScale);
        }

        if (pairB * RibbonAspect < pairA)
        {
            return new GroundRibbon(
                indices[1], indices[2], indices[3], indices[0], pairB * worldScale);
        }

        return null;
    }

    private static bool IsFlat(MeshLod lod, in MeshFace face)
    {
        if (!face.IsOpaque)
        {
            return false;
        }

        int minimum = face.Primitive switch
        {
            MeshPrimitive.Polygon => 3,
            MeshPrimitive.Line => 2,
            _ => int.MaxValue,
        };

        if (face.Indices.Length < minimum)
        {
            return false;
        }

        int vertices = lod.VertexCount;
        foreach (int index in face.Indices)
        {
            if ((uint)index >= (uint)vertices || lod.Vertex(index).Y != 0)   // Vertex: a port-added node (SheetInflation) lives past the INT array
            {
                return false;
            }
        }

        return true;
    }
}
