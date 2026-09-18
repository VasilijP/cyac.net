using System.Globalization;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Render.Ground;

/// <summary>
/// The LINE CENSUS: every <c>LINE</c> shape record in every mesh the port can draw, with the width
/// class it falls in and the width it will be drawn at.
/// </summary>
/// <remarks>
/// <para>
/// An INSTRUMENT rather than a paragraph, as for the ground extraction: <c>--line-census</c> prints
/// it for the whole mesh library, so a claim about the line classes can be checked against the
/// library, and a mesh that falls in the wrong class names itself instead of hiding.
/// </para>
/// <para>
/// A record's LENGTH is the sum of its segments' model lengths scaled by the class's own
/// <c>2^scaleShiftExponent</c>, i.e. in world units = FEET.  Its COLOUR is
/// <see cref="MeshFace.ColorIndex"/>, which the mesh library has already resolved to the
/// <c>.PNT</c> stream where the class has one (<c>pnt_sub_color_patcher @image@0x15C09</c>) and to
/// the executable record's own byte where it has not.
/// </para>
/// </remarks>
public sealed class LineSceneCensus
{
    private sealed record Row(string Basename, int Lod, bool Densest, LineWidthClass LineClass)
    {
        public int Records { get; set; }

        public int Segments { get; set; }

        public double ShortestFeet { get; set; } = double.MaxValue;

        public double LongestFeet { get; set; }

        public SortedSet<int> Colors { get; } = [];

        public int GearRecords { get; set; }
    }

    private readonly List<string> _lines = [];

    /// <summary>The census, one line each.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Line records over the whole library, every LOD.</summary>
    public int TotalRecords { get; private set; }

    /// <summary>Line records at a DENSEST LOD — what the port draws under <c>--lod max</c>.</summary>
    public int DensestRecords { get; private set; }

    /// <summary>Line records that belong to a landing-gear articulation leaf.</summary>
    public int GearRecords { get; private set; }

    /// <summary>Censuses a set of meshes.</summary>
    /// <param name="meshes">The meshes to walk — the whole library, in any order.</param>
    /// <param name="widths">The width model, so the census states the widths it would draw at.</param>
    /// <exception cref="ArgumentNullException"><paramref name="meshes"/> is null.</exception>
    public static LineSceneCensus Of(IEnumerable<MeshModel> meshes, LineWidthModel widths)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        LineSceneCensus census = new LineSceneCensus();
        census.Build(meshes, widths);
        return census;
    }

    private void Build(IEnumerable<MeshModel> meshes, LineWidthModel widths)
    {
        List<Row> rows = new List<Row>();

        foreach (MeshModel mesh in meshes.OrderBy(m => m.Basename, StringComparer.Ordinal))
        {
            LineWidthClass lineClass = LineWidthModel.ClassOf(mesh.Basename);
            foreach (MeshLod lod in mesh.Lods)
            {
                HashSet<int> gear = GearRecordsOf(mesh, lod);
                Row? row = null;
                for (int i = 0; i < lod.Faces.Length; i++)
                {
                    MeshFace face = lod.Faces[i];
                    if (face.Primitive != MeshPrimitive.Line || face.Indices.Length < 2)
                    {
                        continue;
                    }

                    row ??= new Row(
                        mesh.Basename, lod.Index, lod.Index == mesh.DensestLodIndex, lineClass);
                    row.Records++;
                    row.Colors.Add(face.ColorIndex);
                    if (gear.Contains(i))
                    {
                        row.GearRecords++;
                    }

                    for (int s = 0; s + 1 < face.Indices.Length; s++)
                    {
                        double feet = SegmentFeet(lod, mesh.WorldScale, face.Indices[s], face.Indices[s + 1]);
                        row.Segments++;
                        row.ShortestFeet = Math.Min(row.ShortestFeet, feet);
                        row.LongestFeet = Math.Max(row.LongestFeet, feet);
                    }
                }

                if (row is not null)
                {
                    rows.Add(row);
                }
            }
        }

        _lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"LINE CENSUS — {rows.Count} mesh/LOD pair(s) carry line records; widths rope "
                + $"{widths.RopeFeet:F2} / strut {widths.StrutFeet:F2} / line {widths.LineFeet:F2} / "
                + $"tracer {widths.TracerFeet:F2} / mark {widths.MarkFeet:F1} / road {widths.RoadFeet:F0} / "
                + $"river {widths.RiverFeet:F0} ft, floor {widths.FloorHostPixels:F2} host px at 1920 wide"));
        _lines.Add(
            "   mesh       lod  drawn  recs  segs  gear  class   width      colours          "
                + "length (ft)");

        foreach (Row row in rows)
        {
            TotalRecords += row.Records;
            GearRecords += row.GearRecords;
            if (row.Densest)
            {
                DensestRecords += row.Records;
            }

            _lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"   {row.Basename,-10} {row.Lod,3}  {(row.Densest ? "yes" : "no "),5}  "
                    + $"{row.Records,4}  {row.Segments,4}  {row.GearRecords,4}  "
                    + $"{row.LineClass.ToString().ToLowerInvariant(),-6}  "
                    + $"{widths.WidthFeetFor(row.Basename),6:F2} ft  "
                    + $"{string.Join(",", row.Colors),-16} {row.ShortestFeet,9:F1} .. {row.LongestFeet,9:F1}"));
        }

        _lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"   TOTAL {TotalRecords} line record(s); {DensestRecords} at a densest LOD "
                + $"(what --lod max draws); {GearRecords} in a landing-gear articulation leaf"));
    }

    /// <summary>The record indices a mesh's gear articulation leaves emit, for this LOD.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="lod">The LOD.</param>
    /// <remarks>
    /// The gear leaves are the paint-tree leaves that carry a 9-byte ARTICULATION BLOCK
    /// (<see cref="GearArticulation"/>, H3b); a line record inside one is a leg that swings with
    /// <c>g_gear_retract_angle_bam [0xEF96]</c>.
    /// </remarks>
    private static HashSet<int> GearRecordsOf(MeshModel mesh, MeshLod lod)
    {
        HashSet<int> records = new HashSet<int>();
        if (mesh.Gear is not { } gear || gear.LodIndex != lod.Index)
        {
            return records;
        }

        foreach (GearGroup group in gear.Groups)
        {
            if (lod.PaintLeaves.TryGetValue(group.LeafNodeImage, out int[]? indices))
            {
                foreach (int index in indices)
                {
                    records.Add(index);
                }
            }
        }

        return records;
    }

    private static double SegmentFeet(MeshLod lod, double scale, int a, int b)
    {
        (int ax, int ay, int az) = lod.Vertex(a);
        (int bx, int by, int bz) = lod.Vertex(b);
        double dx = (ax - bx) * scale;
        double dy = (ay - by) * scale;
        double dz = (az - bz) * scale;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}
