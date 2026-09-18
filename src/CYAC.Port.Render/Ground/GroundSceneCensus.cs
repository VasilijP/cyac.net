using System.Globalization;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Render.Ground;

/// <summary>
/// The EXTRACTION census: what a loaded theatre gives the analytic ground layer, and what it leaves
/// in the 3-D pass, and why.
/// </summary>
/// <remarks>
/// An instrument rather than a paragraph: <c>--ground-census</c> prints it for whatever theatre the
/// host has loaded, so a claim about the placed world can always be checked against the world.  It
/// is a pure function of that world and needs no frame.
/// </remarks>
public sealed class GroundSceneCensus
{
    private sealed record ClassRow(
        string Basename,
        byte Priority,
        int Lod,
        LineWidthClass LineClass)
    {
        public int Instances { get; set; }

        public int Polygons { get; set; }

        public int Capsules { get; set; }

        public SortedSet<int> Colors { get; } = [];

        public int LeftSolid { get; set; }

        public int LeftStippled { get; set; }

        public int LeftOffPlane { get; set; }

        public int LeftOtherPrimitive { get; set; }
    }

    private readonly List<string> _lines = [];

    /// <summary>The census, one line each.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Polygons the layer would draw for one frame of this theatre, all instances counted.</summary>
    public int TotalPolygons { get; private set; }

    /// <summary>Capsule segments it would draw.</summary>
    public int TotalCapsules { get; private set; }

    /// <summary>Records left in the 3-D pass, over the same instances.</summary>
    public int TotalLeftBehind { get; private set; }

    /// <summary>Censuses one theatre's static scene.</summary>
    /// <param name="scene">The placed world.</param>
    /// <param name="widths">The width model, so the report states the widths it would draw at.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scene"/> is null.</exception>
    public static GroundSceneCensus Of(WorldScene scene, LineWidthModel widths)
    {
        ArgumentNullException.ThrowIfNull(scene);
        GroundSceneCensus census = new GroundSceneCensus();
        census.Build(scene, widths);
        return census;
    }

    private void Build(WorldScene scene, LineWidthModel widths)
    {
        Dictionary<string, ClassRow> rows = new Dictionary<string, ClassRow>(StringComparer.Ordinal);
        int offPlaneInstances = 0;

        foreach (SceneInstance instance in scene.Statics)
        {
            MeshModel mesh = instance.Mesh;
            MeshLod lod = mesh.Densest;
            if (!rows.TryGetValue(mesh.Basename, out ClassRow? row))
            {
                row = new ClassRow(
                    mesh.Basename,
                    mesh.RenderLayerPriority,
                    lod.Index,
                    LineWidthModel.ClassOf(mesh.Basename));
                rows[mesh.Basename] = row;
            }

            row.Instances++;
            bool onPlane = Math.Abs(instance.Y) < 1e-9
                && Math.Abs(instance.PitchDegrees) < 1e-9
                && Math.Abs(instance.RollDegrees) < 1e-9;
            if (!onPlane)
            {
                offPlaneInstances++;
            }

            bool[]? flags = mesh.IsTrue3DObject ? null : GroundFaceTable.For(mesh).FlagsFor(lod.Index);
            for (int i = 0; i < lod.Faces.Length; i++)
            {
                MeshFace face = lod.Faces[i];
                bool promoted = onPlane && flags is not null && i < flags.Length && flags[i];
                if (promoted)
                {
                    row.Colors.Add(face.ColorIndex);
                    if (face.Primitive == MeshPrimitive.Line)
                    {
                        row.Capsules += Math.Max(0, face.Indices.Length - 1);
                    }
                    else
                    {
                        row.Polygons++;
                    }

                    continue;
                }

                if (face.Primitive is not (MeshPrimitive.Polygon or MeshPrimitive.Line))
                {
                    row.LeftOtherPrimitive++;
                }
                else if (!face.IsOpaque)
                {
                    row.LeftStippled++;
                }
                else if (mesh.IsTrue3DObject || !onPlane)
                {
                    row.LeftOffPlane++;
                }
                else
                {
                    row.LeftSolid++;
                }
            }
        }

        _lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"GROUND CENSUS — {scene.Name}: {scene.Statics.Count:N0} static instance(s), "
                + $"widths road {widths.RoadFeet:F0} / river {widths.RiverFeet:F0} / mark "
                + $"{widths.MarkFeet:F0} / line {widths.LineFeet:F2} ft, floor "
                + $"{widths.FloorHostPixels:F2} host px at 1920 wide"));
        _lines.Add(
            "   class      pri  lod  inst   poly  caps  width      colours            "
                + "left in the 3-D pass");

        foreach (ClassRow row in rows.Values.OrderByDescending(r => r.Polygons + r.Capsules).ThenBy(r => r.Basename, StringComparer.Ordinal))
        {
            TotalPolygons += row.Polygons;
            TotalCapsules += row.Capsules;
            int left = row.LeftSolid + row.LeftStippled + row.LeftOffPlane + row.LeftOtherPrimitive;
            TotalLeftBehind += left;

            string reason = left == 0
                ? "-"
                : string.Join(
                    " ",
                    new[]
                    {
                        row.LeftSolid > 0 ? $"{row.LeftSolid} raised" : null,
                        row.LeftStippled > 0 ? $"{row.LeftStippled} stippled" : null,
                        row.LeftOffPlane > 0 ? $"{row.LeftOffPlane} solid-class" : null,
                        row.LeftOtherPrimitive > 0 ? $"{row.LeftOtherPrimitive} disc/effect" : null,
                    }.Where(t => t is not null));

            string width = row.Capsules > 0
                ? $"{row.LineClass.ToString().ToLowerInvariant()} {widths.WidthFeetFor(row.Basename):F2} ft"
                : "-";

            _lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"   {row.Basename,-10} 0x{row.Priority:X2}  {row.Lod,3}  {row.Instances,4}  "
                    + $"{row.Polygons,5} {row.Capsules,5}  {width,-10} "
                    + $"{string.Join(",", row.Colors),-18} {reason}"));
        }

        string offPlaneNote = offPlaneInstances > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"; {offPlaneInstances:N0} instance(s) are off the plane")
            : string.Empty;
        _lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"   TOTAL      {TotalPolygons,29:N0} {TotalCapsules,5:N0}"
                + $"   — {TotalLeftBehind:N0} record(s) stay in the 3-D pass{offPlaneNote}"));
    }
}
