namespace CYAC.Port.Core.Model.World;

/// <summary>
/// SHEET INFLATION — ASSET CONDITIONING for a depth-tested renderer: every zero-thickness "sheet" of a mesh
/// (a wing, a tailplane, a fin, a gear door) is turned into a closed solid with an airfoil section, and the
/// markings painted on it are split into a top and a bottom copy.  Knowledge, not data: the pass adds nodes
/// and records at load time from the shipped geometry; nothing generated is stored
/// ([[project_transform_first_no_inlined_resources]]).
/// </summary>
/// <remarks>
/// <para>
/// <b>What the 1991 models do.</b>  A flat part is authored as a PAIR of records over ONE vertex set,
/// wound in opposite directions: tag <c>0x10</c> (single-sided) one way and tag <c>0x18</c> the other
/// (<c>data/exe/meshes/p51.json</c> LOD2 records 48/50, 49/51, 64/65 …).  Horizontal pairs carry
/// two colours (camouflage above, light below); fins carry one.  The painter's order made that read
/// as a surface with two sides; a depth buffer sees two coplanar polygons that tie, so the wrong
/// colour wins from the wrong side and the markings — authored ONCE as tag <c>0x00</c>, never
/// culled (the FW-190's Balkenkreuz is four records per wing) — show through both faces.  Two
/// other authoring styles exist: LONE never-culled sheets (the F-4 has no twins at all; its wings
/// are single tag-<c>0x00</c> polygons) and parts that ALREADY have thickness (the Me 262 and B-52
/// wings at LOD2 carry separate upper and lower vertex rows) — the census tells them apart.
/// </para>
/// <para>
/// <b>The pass.</b>
/// <list type="number">
/// <item><b>Detect</b> twin pairs (same vertex set, reversed cyclic order) and lone never-culled
/// solid polygons above <see cref="SheetInflationOptions.LoneSheetMinArea"/> that are not a marking
/// on something larger; join them into SHEETS where they share an edge and lie in one plane.</item>
/// <item><b>Classify</b> each sheet by its normal and position: horizontal → <see cref="SheetKind.Wing"/>
/// (the largest, and any within half its area) or <see cref="SheetKind.Tail"/>; vertical →
/// <see cref="SheetKind.Fin"/>; a sheet inside an ARTICULATED paint-tree leaf → <see cref="SheetKind.Slab"/>
/// (a wheel, a door: constant thickness, no section).</item>
/// <item><b>Thickness field.</b>  Over the sheet plane, half-thickness = local chord × thickness
/// ratio (tapered root → tip) × the NACA four-digit section curve of the chordwise fraction
/// <c>u</c> (0 at the leading edge, which is +Z, the nose — <c>SceneDescription</c>: heading 0
/// points at +Z; the P-51's propeller disc sits at z = +36 and its fin at z ≈ −80).  The chord at a
/// spanwise station is read from the sheet OUTLINE, so decals and skins agree exactly.</item>
/// <item><b>Subdivide and offset.</b>  Every polygon in the sheet plane — the twins, the lone
/// sheets, the decals on them — is split at the chord <see cref="SheetInflationOptions.Stations"/>
/// and each vertex is pushed along the sheet normal by the field: the <c>0x10</c> record becomes the
/// top skin, its twin the bottom skin, a lone sheet gets a colour clone for its other side, and a
/// decal is cloned to both skins (or to its own side only when it is single-sided) with the
/// stacking lift on top.  Every result is single-sided.  Because thickness is a FIELD, adjacent
/// camouflage panels that share an edge move together and stay welded.  Walls close the outline
/// where it has height (the root, the tip); the leading and trailing edges close on their own.</item>
/// <item><b>Root nodes.</b>  A sheet vertex the fuselage also uses is never moved: the skins get
/// fresh copies, the body is untouched, the thick root sits inside it.</item>
/// </list>
/// </para>
/// <para>
/// <b>What survives.</b>  Paint-tree leaf membership (a generated record joins its source record's
/// leaf, so the gear callbacks hide it with the leg), the articulation vertex runs (a generated node
/// names its SOURCE node, <see cref="MeshLod.VertexSources"/>; <c>GearPose.Transform</c> tests the
/// source), the INT vertex array (untouched, for PoC mode and the integrity tests), the verified
/// kernels (they never read the mesh).
/// </para>
/// <para>
/// Fleet census (this pass's <see cref="Sheet"/> records, also): at LOD2
/// the P-51 has 15 twin pairs in 9 sheets, the FW-190 17 in 10, the F-86 14 in 9, the MiG-21 11 in
/// 10, the Me 262 2 in 1 (its tailplane), the F-4 0 (all lone).
/// </para>
/// </remarks>
public static class SheetInflation
{
    /// <summary>What a sheet is, which decides its thickness profile.</summary>
    public enum SheetKind
    {
        /// <summary>The largest horizontal sheets: the main wing halves.</summary>
        Wing = 0,

        /// <summary>The other horizontal sheets: tailplanes, canards, small strakes.</summary>
        Tail = 1,

        /// <summary>A vertical sheet: a fin, a rudder, a ventral strake.</summary>
        Fin = 2,

        /// <summary>A sheet inside an articulated leaf (a wheel, a door): a constant-thickness slab.</summary>
        Slab = 3,

        /// <summary>A sheet the pass leaves alone (a Z-facing disc-like polygon, or one it cannot frame).</summary>
        Skipped = 4,
    }

    /// <summary>One detected sheet and what was generated for it.</summary>
    /// <param name="Kind">Its classification.</param>
    /// <param name="Records">The source records that ARE the sheet (twins and lone members).</param>
    /// <param name="Decals">The source records found lying on it (markings).</param>
    /// <param name="Area">One side's area in square model units.</param>
    /// <param name="SpanUnits">Its extent along the span axis, model units.</param>
    /// <param name="ChordUnits">Its widest chord, model units.</param>
    /// <param name="MaxHalfThickness">The largest half-thickness the field reached, model units.</param>
    /// <param name="AddedRecords">How many records the sheet added to the LOD.</param>
    /// <param name="AddedVertices">How many nodes the sheet added to the LOD.</param>
    public readonly record struct Sheet(
        SheetKind Kind,
        int[] Records,
        int[] Decals,
        double Area,
        double SpanUnits,
        double ChordUnits,
        double MaxHalfThickness,
        int AddedRecords,
        int AddedVertices);

    /// <summary>
    /// Whether a class gets the pass at all: AIRCRAFT (and vehicles) only.  Scenery is left alone — a
    /// road, a river, a runway strip IS a lone never-culled quad that must stay flat, and a hangar's or
    /// a bridge's flat faces are flat by design.
    /// </summary>
    /// <remarks>
    /// The test is on the class's own <c>s_mesh_registry_slot</c> fields as the transform publishes
    /// them (<c>data/exe/meshes/&lt;class&gt;.json</c> <c>slot.flags</c>, <c>slot.groundClearance</c>):
    /// <b>flags bit 2 (0x04) set AND ground clearance &gt; 0</b>.  Observed over all 66 classes: it
    /// selects exactly the 19 aeroplanes (<c>flags</c> 0x0C / 0x2C / 0x04, clearance 2560–12288) plus
    /// <c>truck</c> (0x0C, 7680), and rejects every shadow mesh (0x08), the ground decals (<c>strip</c>
    /// has 0x04 but clearance 0), the buildings (0x10 / 0x14), <c>bridge</c> (0x10, clearance 12288),
    /// the effects and the ejection parts (0x00).  What bit 2 MEANS in the engine is <b>(open
    /// hypothesis)</b> — this is an observed separation, not a decoded flag; the gear-rule classes are
    /// added regardless.  Its basename map covers the FLYABLE aircraft only — the P-47 dropped out —
    /// and it needs the vocabulary loaded, which synthetic-tree tests never do.
    /// </remarks>
    /// <param name="basename">The mesh basename, e.g. <c>p51</c>.</param>
    /// <param name="slotFlags">The slot's <c>flags</c> byte.</param>
    /// <param name="groundClearance">The slot's ground clearance.</param>
    public static bool AppliesTo(string? basename, int slotFlags, int groundClearance) =>
        (basename is not null && GearArticulation.Rules.ContainsKey(basename))
        || ((slotFlags & 0x04) != 0 && groundClearance > 0);

    /// <summary>Two polygons are twins when they share a vertex set and wind opposite ways.</summary>
    public static bool AreReversedTwins(int[] a, int[] b)
    {
        if (a.Length != b.Length || a.Length < 3)
        {
            return false;
        }

        int n = a.Length;
        for (int shift = 0; shift < n; shift++)
        {
            bool match = true;
            for (int k = 0; k < n; k++)
            {
                if (a[k] != b[(shift - k + (2 * n)) % n])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The NACA four-digit section's half-thickness per unit of (chord × thickness ratio), with
    /// the trailing edge closed: 0 at u = 0 and u = 1, ≈ 0.5 at u = 0.3.
    /// </summary>
    /// <param name="u">The chordwise fraction, 0 at the leading edge.</param>
    public static double SectionHalfThickness(double u)
    {
        u = Math.Clamp(u, 0.0, 1.0);
        double open = 5.0 * ((0.2969 * Math.Sqrt(u)) - (0.1260 * u) - (0.3516 * u * u) + (0.2843 * u * u * u) - (0.1015 * u * u * u * u));
        const double AtTrailingEdge = 5.0 * (0.2969 - 0.1260 - 0.3516 + 0.2843 - 0.1015);
        return Math.Max(0.0, open - (u * AtTrailingEdge));
    }

    /// <summary>
    /// Returns the LOD with every sheet inflated, and the census of what was found.
    /// </summary>
    /// <param name="lod">The LOD as built from the documents (before the decal lift).</param>
    /// <param name="options">The thickness knobs; <see cref="SheetInflationOptions.Enabled"/> false returns the LOD unchanged.</param>
    /// <param name="found">Receives one record per sheet (also when disabled — the census is free).</param>
    public static MeshLod Apply(MeshLod lod, in SheetInflationOptions options, ICollection<Sheet>? found = null)
    {
        ArgumentNullException.ThrowIfNull(lod);
        Pass pass = new Pass(lod, options);
        pass.Detect();
        if (!options.Enabled || pass.Sheets.Count == 0)
        {
            pass.Report(found, generated: false);
            return lod;
        }

        MeshLod result = pass.Generate();
        pass.Report(found, generated: true);
        return result;
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // The pass.  Vertices are doubles throughout; the INT array is never written.
    // ────────────────────────────────────────────────────────────────────────────────────────

    private readonly record struct V3(double X, double Y, double Z)
    {
        public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator *(V3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public double Dot(V3 b) => (X * b.X) + (Y * b.Y) + (Z * b.Z);
        public V3 Cross(V3 b) => new((Y * b.Z) - (Z * b.Y), (Z * b.X) - (X * b.Z), (X * b.Y) - (Y * b.X));
        public double Length => Math.Sqrt(Dot(this));
        public V3 Unit() { double l = Length; return l > 1e-12 ? this * (1.0 / l) : this; }
    }

    private sealed class SheetInfo
    {
        public required List<int> Members { get; init; }
        public List<int> Decals { get; } = [];
        public SheetKind Kind { get; set; }
        public V3 Normal { get; set; }
        public double PlaneD { get; set; }
        public V3 Span { get; set; }
        public V3 Chord { get; set; }
        public double VMin { get; set; }
        public double VMax { get; set; }
        public double RootV { get; set; }
        public double Area { get; set; }
        public double WidestChord { get; set; }
        public double SlabHalf { get; set; }
        public double MaxHalf { get; set; }
        public int AddedRecords { get; set; }
        public int AddedVertices { get; set; }
        public HashSet<int> VertexSet { get; } = [];
    }

    private readonly record struct PolyVertex(V3 Position, double U, int Source);

    private sealed class Pass
    {
        private const double CoplanarCos = 0.98;
        private const double CoplanarDistance = 0.5;
        private const double DecalDistance = 2.0;
        private const double WallMinHeight = 0.05;
        private const double CapBand = 0.12;

        private readonly MeshLod _lod;
        private readonly SheetInflationOptions _options;
        private readonly MeshFace[] _faces;
        private readonly int _shipped;
        private readonly V3?[] _normal;
        private readonly double[] _area;
        private readonly V3[] _centroid;
        private readonly int[] _twin;
        private readonly int[] _sheetOf;
        private readonly int[] _leafOf;
        private readonly Dictionary<int, int[]> _leaves;

        public List<SheetInfo> Sheets { get; } = [];

        public Pass(MeshLod lod, SheetInflationOptions options)
        {
            _lod = lod;
            _options = options;
            _faces = lod.Faces;
            _shipped = lod.VertexCount;
            int n = _faces.Length;
            _normal = new V3?[n];
            _area = new double[n];
            _centroid = new V3[n];
            _twin = new int[n];
            _sheetOf = new int[n];
            _leafOf = new int[n];
            Array.Fill(_twin, -1);
            Array.Fill(_sheetOf, -1);
            Array.Fill(_leafOf, -1);
            _leaves = new Dictionary<int, int[]>(lod.PaintLeaves);
            foreach ((int leaf, int[] records) in _leaves)
            {
                foreach (int r in records)
                {
                    if ((uint)r < (uint)n)
                    {
                        _leafOf[r] = leaf;
                    }
                }
            }

            for (int i = 0; i < n; i++)
            {
                ref readonly MeshFace face = ref _faces[i];
                if (face.Primitive != MeshPrimitive.Polygon || face.Indices.Length < 3 || !InRange(face.Indices))
                {
                    continue;
                }

                // A paint-tree ORPHAN (a split-plane polygon no leaf emits; 35 fleet-wide) is never
                // drawn, so it is neither a sheet nor a marking: generating from it would only
                // breed more orphans.
                if (!lod.EmittedByPaintTree(i))
                {
                    continue;
                }

                (V3 normal, double area) = Newell(face.Indices);
                if (area <= 1e-9)
                {
                    continue;
                }

                _normal[i] = normal;
                _area[i] = area;
                _centroid[i] = Centroid(face.Indices);
            }
        }

        private V3 P(int index)
        {
            (double x, double y, double z) = _lod.VertexPosition(index);
            return new V3(x, y, z);
        }

        private bool InRange(int[] indices)
        {
            foreach (int i in indices)
            {
                if ((uint)i >= (uint)_shipped)
                {
                    return false;
                }
            }

            return true;
        }

        private (V3 Normal, double Area) Newell(int[] indices)
        {
            double nx = 0, ny = 0, nz = 0;
            for (int k = 0; k < indices.Length; k++)
            {
                V3 a = P(indices[k]);
                V3 b = P(indices[(k + 1) % indices.Length]);
                nx += (a.Y - b.Y) * (a.Z + b.Z);
                ny += (a.Z - b.Z) * (a.X + b.X);
                nz += (a.X - b.X) * (a.Y + b.Y);
            }

            V3 n = new V3(nx, ny, nz);
            double length = n.Length;
            return length < 1e-12 ? (n, 0.0) : (n * (1.0 / length), length / 2.0);
        }

        private V3 Centroid(int[] indices)
        {
            V3 sum = new V3(0, 0, 0);
            foreach (int i in indices)
            {
                sum += P(i);
            }

            return sum * (1.0 / indices.Length);
        }

        private static bool Inside(ReadOnlySpan<V3> polygon, V3 point, V3 normal)
        {
            // Every edge's cross product with the point must agree with the polygon normal.
            for (int i = 0; i < polygon.Length; i++)
            {
                V3 a = polygon[i];
                V3 b = polygon[(i + 1) % polygon.Length];
                V3 cross = (b - a).Cross(point - a);
                if (cross.Dot(normal) < -1e-9)
                {
                    return false;
                }
            }

            return true;
        }

        private V3[] Polygon(int record)
        {
            int[] indices = _faces[record].Indices;
            V3[] pts = new V3[indices.Length];
            for (int k = 0; k < pts.Length; k++)
            {
                pts[k] = P(indices[k]);
            }

            return pts;
        }

        // ── 1. detect ─────────────────────────────────────────────────────────────────────────

        public void Detect()
        {
            int n = _faces.Length;


            // Twins: same vertex set, reversed order.
            Dictionary<string, List<int>> bySet = new Dictionary<string, List<int>>();
            for (int i = 0; i < n; i++)
            {
                if (_normal[i] is null)
                {
                    continue;
                }

                int[] sorted = (int[])_faces[i].Indices.Clone();
                Array.Sort(sorted);
                string key = string.Join(',', sorted);
                if (!bySet.TryGetValue(key, out List<int>? list))
                {
                    bySet[key] = list = [];
                }

                list.Add(i);
            }

            foreach (List<int> list in bySet.Values)
            {
                for (int a = 0; a < list.Count; a++)
                {
                    if (_twin[list[a]] >= 0)
                    {
                        continue;
                    }

                    for (int b = a + 1; b < list.Count; b++)
                    {
                        if (_twin[list[b]] < 0 && AreReversedTwins(_faces[list[a]].Indices, _faces[list[b]].Indices))
                        {
                            _twin[list[a]] = list[b];
                            _twin[list[b]] = list[a];
                            break;
                        }
                    }
                }
            }

            // Members: every twin, plus lone never-culled solid polygons that are not markings.
            List<int> members = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (_normal[i] is null)
                {
                    continue;
                }

                if (_twin[i] >= 0)
                {
                    members.Add(i);
                    continue;
                }

                ref readonly MeshFace face = ref _faces[i];
                bool loneTag = face.Tag == 0x00 || face.Tag == MeshFace.DoubleSidedTag;
                if (!loneTag || !face.IsOpaque || _area[i] < _options.LoneSheetMinArea || IsMarking(i))
                {
                    continue;
                }

                members.Add(i);
            }

            // Sheets: connected components over shared edges in one plane.
            Dictionary<int, int> parent = new Dictionary<int, int>();
            foreach (int m in members)
            {
                parent[m] = m;
            }

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }

                return x;
            }

            for (int a = 0; a < members.Count; a++)
            {
                int ia = members[a];
                V3 na = _normal[ia]!.Value;
                for (int b = a + 1; b < members.Count; b++)
                {
                    int ib = members[b];
                    if (SharedVertices(_faces[ia].Indices, _faces[ib].Indices) < 2)
                    {
                        continue;
                    }

                    V3 nb = _normal[ib]!.Value;
                    if (Math.Abs(na.Dot(nb)) < CoplanarCos)
                    {
                        continue;
                    }

                    double d = Math.Abs(na.Dot(_centroid[ib] - _centroid[ia]));
                    if (d > CoplanarDistance)
                    {
                        continue;
                    }

                    parent[Find(ia)] = Find(ib);
                }
            }

            Dictionary<int, List<int>> groups = new Dictionary<int, List<int>>();
            foreach (int m in members)
            {
                int root = Find(m);
                if (!groups.TryGetValue(root, out List<int>? list))
                {
                    groups[root] = list = [];
                }

                list.Add(m);
            }

            foreach (List<int> list in groups.Values.OrderBy(l => l.Min()))
            {
                list.Sort();
                SheetInfo sheet = new SheetInfo { Members = list };
                int largest = list.MaxBy(r => _area[r]);
                sheet.Normal = _normal[largest]!.Value;
                sheet.PlaneD = sheet.Normal.Dot(_centroid[largest]);
                foreach (int r in list)
                {
                    foreach (int v in _faces[r].Indices)
                    {
                        sheet.VertexSet.Add(v);
                    }
                }

                // One side's area: the twins count once.
                double area = 0;
                foreach (int r in list)
                {
                    if (_twin[r] < 0 || _twin[r] > r)
                    {
                        area += _area[r];
                    }
                }

                sheet.Area = area;
                foreach (int r in list)
                {
                    _sheetOf[r] = Sheets.Count;
                }

                Sheets.Add(sheet);
            }

            // Decals on sheets: any other polygon lying in the plane whose centroid a member contains.
            for (int i = 0; i < n; i++)
            {
                if (_normal[i] is null || _sheetOf[i] >= 0)
                {
                    continue;
                }

                for (int s = 0; s < Sheets.Count; s++)
                {
                    SheetInfo sheet = Sheets[s];
                    if (Math.Abs(_normal[i]!.Value.Dot(sheet.Normal)) < CoplanarCos || !WithinPlane(i, sheet, DecalDistance))
                    {
                        continue;
                    }

                    V3 c = ProjectToPlane(_centroid[i], sheet);
                    bool inside = false;
                    foreach (int m in sheet.Members)
                    {
                        if (_area[m] >= 1.5 * _area[i] && Inside(Polygon(m), c, _normal[m]!.Value))
                        {
                            inside = true;
                            break;
                        }
                    }

                    if (inside)
                    {
                        sheet.Decals.Add(i);
                        break;
                    }
                }
            }

            Classify();
        }

        private bool IsMarking(int i)
        {
            V3 ni = _normal[i]!.Value;
            for (int j = 0; j < _faces.Length; j++)
            {
                if (j == i || _normal[j] is null || _area[j] < 2.0 * _area[i])
                {
                    continue;
                }

                V3 nj = _normal[j]!.Value;
                if (Math.Abs(ni.Dot(nj)) < CoplanarCos)
                {
                    continue;
                }

                double d = Math.Abs(nj.Dot(_centroid[i] - _centroid[j]));
                if (d <= DecalDistance && Inside(Polygon(j), _centroid[i] - (nj * nj.Dot(_centroid[i] - _centroid[j])), nj))
                {
                    return true;
                }
            }

            return false;
        }

        private bool WithinPlane(int record, SheetInfo sheet, double tolerance)
        {
            foreach (int v in _faces[record].Indices)
            {
                if (Math.Abs(sheet.Normal.Dot(P(v)) - sheet.PlaneD) > tolerance)
                {
                    return false;
                }
            }

            return true;
        }

        private static V3 ProjectToPlane(V3 p, SheetInfo sheet) =>
            p - (sheet.Normal * (sheet.Normal.Dot(p) - sheet.PlaneD));

        private static int SharedVertices(int[] a, int[] b)
        {
            int shared = 0;
            foreach (int x in a)
            {
                if (Array.IndexOf(b, x) >= 0)
                {
                    shared++;
                }
            }

            return shared;
        }

        private void Classify()
        {
            double largestHorizontal = 0;
            foreach (SheetInfo sheet in Sheets)
            {
                if (Math.Abs(sheet.Normal.Y) >= Math.Max(Math.Abs(sheet.Normal.X), Math.Abs(sheet.Normal.Z)))
                {
                    largestHorizontal = Math.Max(largestHorizontal, sheet.Area);
                }
            }

            foreach (SheetInfo sheet in Sheets)
            {
                V3 n = sheet.Normal;
                bool articulated = false;
                foreach (int r in sheet.Members)
                {
                    if (_leafOf[r] >= 0 && _lod.PaintArticulation.ContainsKey(_leafOf[r]))
                    {
                        articulated = true;
                        break;
                    }
                }

                double ax = Math.Abs(n.X), ay = Math.Abs(n.Y), az = Math.Abs(n.Z);
                V3 nose = new(0, 0, 1);
                if (articulated)
                {
                    sheet.Kind = SheetKind.Slab;
                }
                else if (ay >= ax && ay >= az)
                {
                    sheet.Kind = sheet.Area >= 0.5 * largestHorizontal ? SheetKind.Wing : SheetKind.Tail;
                }
                else if (ax >= az)
                {
                    sheet.Kind = SheetKind.Fin;
                }
                else
                {
                    sheet.Kind = SheetKind.Skipped;
                    continue;
                }

                // The chord axis is +Z (the nose) projected into the plane; the span axis is in-plane
                // and perpendicular.  A Z-facing slab uses X instead.
                V3 chordSeed = az > Math.Max(ax, ay) ? new V3(1, 0, 0) : nose;
                V3 chord = (chordSeed - (n * n.Dot(chordSeed))).Unit();
                if (chord.Length < 1e-6)
                {
                    sheet.Kind = SheetKind.Skipped;
                    continue;
                }

                V3 span = n.Cross(chord).Unit();
                sheet.Chord = chord;
                sheet.Span = span;

                double vmin = double.MaxValue, vmax = double.MinValue;
                double cmin = double.MaxValue, cmax = double.MinValue;
                foreach (int v in sheet.VertexSet)
                {
                    V3 p = P(v);
                    double sv = span.Dot(p), cv = chord.Dot(p);
                    vmin = Math.Min(vmin, sv);
                    vmax = Math.Max(vmax, sv);
                    cmin = Math.Min(cmin, cv);
                    cmax = Math.Max(cmax, cv);
                }

                sheet.VMin = vmin;
                sheet.VMax = vmax;
                sheet.WidestChord = 0;
                foreach (int v in sheet.VertexSet)
                {
                    (double lo, double hi) = ChordAt(sheet, span.Dot(P(v)));
                    sheet.WidestChord = Math.Max(sheet.WidestChord, hi - lo);
                }

                // The root is where the fuselage is: the station nearest the centreline along the
                // span axis (|x| = 0 for a wing, |y| = 0 for a fin — a
                // VENTRAL fin, the Me 163's skid fin, hangs below the fuselage: its root is its upper
                // end).  A sheet that STRADDLES the centreline — the MiG-15's tailplane is ONE
                // sheet from x = −24 to +24 — has its root in the MIDDLE and a tip at each end; the
                // human saw one half thick at both ends when the root sat at one tip.
                sheet.RootV = vmin <= 0.0 && vmax >= 0.0 ? 0.0 : (Math.Abs(vmin) <= Math.Abs(vmax) ? vmin : vmax);

                double spanExtent = Math.Max(1e-6, vmax - vmin);
                double chordExtent = Math.Max(1e-6, cmax - cmin);
                sheet.SlabHalf = _options.SlabHalfThicknessFraction * Math.Min(spanExtent, chordExtent);
            }
        }

        /// <summary>
        /// The chord interval of the sheet outline at a spanwise station, read from the outline
        /// edges that are NOT caps.  A cap edge — a wing root, a fin's foot, a tip cap — has both
        /// ends at the same span extreme and is ignored: a station vertex created ON such an edge
        /// would otherwise find itself on the outline and read as u = 0 or 1 (the P-51's fin came
        /// out 0.22 units thick, the P-47's fin skins coincided).  A slope rule threw away the
        /// P-51's ~70°-swept fin fillet, its actual leading edge. Inside a cap, where a station
        /// crosses fewer than two such edges, the leading and trailing edges are EXTRAPOLATED
        /// linearly from two inner samples.
        /// </summary>
        private (double Lo, double Hi) ChordAt(SheetInfo sheet, double v)
        {
            double span = Math.Max(1e-9, sheet.VMax - sheet.VMin);
            double requested = Math.Clamp(v, sheet.VMin, sheet.VMax);
            if (Sample(sheet, requested, out double lo, out double hi))
            {
                return (lo, hi);
            }

            double middle = 0.5 * (sheet.VMin + sheet.VMax);
            double step = 0.01 * span * (requested < middle ? 1.0 : -1.0);
            double v1 = requested;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                v1 += step;
                if ((step > 0 && v1 > middle) || (step < 0 && v1 < middle))
                {
                    break;
                }

                if (Sample(sheet, v1, out double lo1, out double hi1))
                {
                    double v2 = v1 + (step * 4.0);
                    if (Sample(sheet, v2, out double lo2, out double hi2) && Math.Abs(v2 - v1) > 1e-9)
                    {
                        double f = (requested - v1) / (v1 - v2);
                        double eLo = lo1 + ((lo1 - lo2) * f);
                        double eHi = hi1 + ((hi1 - hi2) * f);
                        if (eHi - eLo > 1e-6)
                        {
                            return (eLo, eHi);
                        }
                    }

                    return (lo1, hi1);
                }
            }

            // Fall back to the sheet's full chord range.
            double flo = double.MaxValue, fhi = double.MinValue;
            foreach (int v2 in sheet.VertexSet)
            {
                double c = sheet.Chord.Dot(P(v2));
                flo = Math.Min(flo, c);
                fhi = Math.Max(fhi, c);
            }

            return (flo, fhi);
        }

        /// <summary>The chord interval at one station from the non-cap outline edges; false inside a cap.</summary>
        private bool Sample(SheetInfo sheet, double station, out double lo, out double hi)
        {
            double span = Math.Max(1e-9, sheet.VMax - sheet.VMin);
            double band = CapBand * span;
            lo = double.MaxValue;
            hi = double.MinValue;
            int crossings = 0;
            foreach (int m in sheet.Members)
            {
                int[] indices = _faces[m].Indices;
                for (int k = 0; k < indices.Length; k++)
                {
                    V3 a = P(indices[k]);
                    V3 b = P(indices[(k + 1) % indices.Length]);
                    double sa = sheet.Span.Dot(a), sb = sheet.Span.Dot(b);
                    double ca = sheet.Chord.Dot(a), cb = sheet.Chord.Dot(b);
                    if (Math.Abs(sb - sa) < 1e-9)
                    {
                        continue;   // lies along a station: never a crossing
                    }

                    // A cap edge has BOTH ends at the same span extreme (a root, a foot, a tip
                    // cap).  A swept leading edge — the P-51's fin fillet runs at ~70° — does not.
                    bool atMin = sa - sheet.VMin <= band && sb - sheet.VMin <= band;
                    bool atMax = sheet.VMax - sa <= band && sheet.VMax - sb <= band;
                    if (atMin || atMax)
                    {
                        continue;
                    }

                    double va = sa - station, vb = sb - station;
                    if ((va <= 0 && vb >= 0) || (va >= 0 && vb <= 0))
                    {
                        double t = va / (va - vb);
                        double c = ca + ((cb - ca) * t);
                        lo = Math.Min(lo, c);
                        hi = Math.Max(hi, c);
                        crossings++;
                    }
                }
            }

            return crossings >= 2 && hi - lo > 1e-9;
        }

        private double HalfThickness(SheetInfo sheet, V3 planePoint)
        {
            if (sheet.Kind == SheetKind.Slab)
            {
                return sheet.SlabHalf;
            }

            double v = sheet.Span.Dot(planePoint);
            (double lo, double hi) = ChordAt(sheet, v);
            double chord = hi - lo;
            if (chord <= 1e-9)
            {
                return 0.0;
            }

            double u = (hi - sheet.Chord.Dot(planePoint)) / chord;   // 0 at the leading (+Z) edge
            double reach = Math.Max(Math.Abs(sheet.VMax - sheet.RootV), Math.Abs(sheet.VMin - sheet.RootV));
            double t = Math.Abs(v - sheet.RootV) / Math.Max(1e-9, reach);   // 0 at the root, 1 at the farther tip
            (double root, double tip) = sheet.Kind switch
            {
                SheetKind.Wing => (_options.WingRootThicknessRatio, _options.WingTipThicknessRatio),
                SheetKind.Tail => (_options.TailRootThicknessRatio, _options.TailTipThicknessRatio),
                _ => (_options.FinRootThicknessRatio, _options.FinTipThicknessRatio),
            };
            double ratio = root + ((tip - root) * Math.Clamp(t, 0.0, 1.0));
            return chord * ratio * SectionHalfThickness(u);
        }

        private double ChordFraction(SheetInfo sheet, V3 planePoint)
        {
            (double lo, double hi) = ChordAt(sheet, sheet.Span.Dot(planePoint));
            double chord = hi - lo;
            return chord <= 1e-9 ? 0.0 : Math.Clamp((hi - sheet.Chord.Dot(planePoint)) / chord, 0.0, 1.0);
        }

        // ── 2. generate ───────────────────────────────────────────────────────────────────────

        private readonly List<double> _refined = [];
        private readonly List<int> _sources = [];
        private readonly Dictionary<(long, long, long), int> _dedup = [];
        private readonly List<MeshFace> _out = [];
        private readonly Dictionary<int, List<int>> _leafAdditions = [];

        public MeshLod Generate()
        {
            for (int i = 0; i < _shipped; i++)
            {
                (double x, double y, double z) = _lod.VertexPosition(i);
                _refined.Add(x);
                _refined.Add(y);
                _refined.Add(z);
                _sources.Add(i);
            }

            _out.AddRange(_faces);
            bool[] replaced = new bool[_faces.Length];

            for (int s = 0; s < Sheets.Count; s++)
            {
                SheetInfo sheet = Sheets[s];
                if (sheet.Kind == SheetKind.Skipped)
                {
                    continue;
                }

                int recordsBefore = _out.Count, verticesBefore = _sources.Count;
                HashSet<int> replacedByThisSheet = new HashSet<int>();

                // Skins: per plane point, the top and bottom copies (for the walls).
                Dictionary<(long, long, long), int> topOf = new Dictionary<(long, long, long), int>();
                Dictionary<(long, long, long), int> bottomOf = new Dictionary<(long, long, long), int>();
                Dictionary<(int, int), (int Count, byte Color, int Leaf)> topEdges = new Dictionary<(int, int), (int Count, byte Color, int Leaf)>();
                Dictionary<((long, long, long), (long, long, long)), byte> bottomColors = new Dictionary<((long, long, long), (long, long, long)), byte>();

                foreach (int r in sheet.Members)
                {
                    double side = Math.Sign(_normal[r]!.Value.Dot(sheet.Normal));
                    if (side == 0)
                    {
                        continue;
                    }

                    bool lone = _twin[r] < 0;
                    EmitSkin(sheet, r, side, lift: 0.0, replaced, replacedByThisSheet, side > 0 ? topOf : bottomOf, side > 0 ? topEdges : null, side > 0 ? null : bottomColors);
                    if (lone)
                    {
                        EmitSkin(sheet, r, -side, lift: 0.0, replaced, replacedByThisSheet, side > 0 ? bottomOf : topOf, side > 0 ? null : topEdges, side > 0 ? bottomColors : null);
                    }
                }

                // Decals: cloned to both skins, or to their own side only when single-sided.
                Dictionary<int, int> level = new Dictionary<int, int>();
                foreach (int d in sheet.Decals.OrderBy(x => x))
                {
                    int lvl = 1;
                    foreach ((int other, int otherLevel) in level)
                    {
                        if (_area[other] >= _area[d] && Inside(Polygon(other), ProjectToPlane(_centroid[d], sheet), _normal[other]!.Value))
                        {
                            lvl = Math.Max(lvl, otherLevel + 1);
                        }
                    }

                    level[d] = lvl;
                    double lift = _options.DecalLiftModelUnits * lvl;
                    double own = Math.Sign(_normal[d]!.Value.Dot(sheet.Normal));
                    if (own == 0)
                    {
                        own = 1;
                    }

                    if (_faces[d].BackfaceCulled)
                    {
                        EmitSkin(sheet, d, own, lift, replaced, replacedByThisSheet, null, null);
                    }
                    else
                    {
                        EmitSkin(sheet, d, own, lift, replaced, replacedByThisSheet, null, null);
                        EmitSkin(sheet, d, -own, lift, replaced, replacedByThisSheet, null, null);
                    }
                }

                EmitWalls(sheet, topEdges, topOf, bottomOf, bottomColors);

                sheet.AddedRecords = _out.Count - recordsBefore;
                sheet.AddedVertices = _sources.Count - verticesBefore;
                if (sheet.AddedRecords == 0 && replacedByThisSheet.Count == 0)
                {
                    // A sheet POINTED AT BOTH ENDS (the Me 163's dorsal fairing triangle at LOD2)
                    // has no cap edge to carry thickness: every piece is a zero-section sliver and
                    // nothing survives.  It stays exactly as authored and is reported so.
                    sheet.Kind = SheetKind.Skipped;
                }
            }

            Dictionary<int, int[]> leaves = new Dictionary<int, int[]>(_leaves);
            foreach ((int leaf, List<int> added) in _leafAdditions)
            {
                int[] existing = leaves.TryGetValue(leaf, out int[]? e) ? e : [];
                leaves[leaf] = [.. existing, .. added];
            }

            // The PRIMARY constructor, never `with`: a record's `with` clones the cached
            // paint-tree emission table, orphan count and bounding radius, all sized for the OLD
            // face array — the hangar crashed on the first inflated record past it.
            return new MeshLod(
                _lod.Index,
                _lod.Vertices,
                [.. _out],
                leaves,
                _lod.PaintLeafArticulation,
                _lod.VertexLifts,
                [.. _refined],
                [.. _sources]);
        }

        private int AddVertex(V3 p, int source)
        {
            (long, long, long) key = Key(p);
            if (_dedup.TryGetValue(key, out int index))
            {
                return index;
            }

            index = _sources.Count;
            _refined.Add(p.X);
            _refined.Add(p.Y);
            _refined.Add(p.Z);
            _sources.Add(source);
            _dedup[key] = index;
            return index;
        }

        private static (long, long, long) Key(V3 p) =>
            ((long)Math.Round(p.X * 4096.0), (long)Math.Round(p.Y * 4096.0), (long)Math.Round(p.Z * 4096.0));

        private void EmitRecord(int sourceRecord, MeshFace face, bool[] replaced, HashSet<int> replacedByThisSheet)
        {
            if (!replaced[sourceRecord])
            {
                _out[sourceRecord] = face;
                replaced[sourceRecord] = true;
                replacedByThisSheet.Add(sourceRecord);
                return;
            }

            int index = _out.Count;
            _out.Add(face);
            int leaf = _leafOf[sourceRecord];
            if (leaf >= 0)
            {
                if (!_leafAdditions.TryGetValue(leaf, out List<int>? list))
                {
                    _leafAdditions[leaf] = list = [];
                }

                list.Add(index);
            }
        }

        private void EmitSkin(
            SheetInfo sheet, int record, double side, double lift, bool[] replaced, HashSet<int> replacedByThisSheet,
            Dictionary<(long, long, long), int>? sideMap, Dictionary<(int, int), (int Count, byte Color, int Leaf)>? edges,
            Dictionary<((long, long, long), (long, long, long)), byte>? bottomEdgeColors = null)
        {
            ref readonly MeshFace face = ref _faces[record];
            int[] indices = face.Indices;
            List<PolyVertex> poly = new List<PolyVertex>(indices.Length);
            bool ownSide = Math.Sign(_normal[record]!.Value.Dot(sheet.Normal)) == side;
            for (int k = 0; k < indices.Length; k++)
            {
                int vi = ownSide ? indices[k] : indices[indices.Length - 1 - k];
                V3 p = ProjectToPlane(P(vi), sheet);
                poly.Add(new PolyVertex(p, ChordFraction(sheet, p), _lod.SourceVertex(vi)));
            }

            List<List<PolyVertex>> pieces = sheet.Kind == SheetKind.Slab ? [poly] : Subdivide(poly);
            foreach (List<PolyVertex> piece in pieces)
            {
                if (piece.Count < 3)
                {
                    continue;
                }

                // A piece whose section is zero at EVERY vertex is a sliver lying along the
                // leading or trailing edge (an outline corner where two edge segments meet, the
                // Me 163's skid fin): both skins would coincide there.  It is dropped — at zero
                // thickness the two skins meet anyway, so nothing is missing.
                double[] halves = new double[piece.Count];
                double deepest = 0.0;
                for (int k = 0; k < piece.Count; k++)
                {
                    halves[k] = HalfThickness(sheet, piece[k].Position);
                    deepest = Math.Max(deepest, halves[k]);
                }

                if (lift == 0.0 && deepest < 1e-6)
                {
                    continue;
                }

                int[] outIndices = new int[piece.Count];
                bool degenerate = false;
                for (int k = 0; k < piece.Count; k++)
                {
                    PolyVertex pv = piece[k];
                    double half = halves[k];
                    sheet.MaxHalf = Math.Max(sheet.MaxHalf, half);
                    V3 pos = pv.Position + (sheet.Normal * (side * (half + lift)));
                    outIndices[k] = AddVertex(pos, pv.Source);
                    if (sideMap is not null)
                    {
                        sideMap.TryAdd(Key(pv.Position), outIndices[k]);
                    }
                }

                for (int k = 0; k < outIndices.Length && !degenerate; k++)
                {
                    degenerate = outIndices[k] == outIndices[(k + 1) % outIndices.Length];
                }

                if (degenerate)
                {
                    outIndices = [.. outIndices.Distinct()];
                    if (outIndices.Length < 3)
                    {
                        continue;
                    }
                }

                MeshFace emitted = face with
                {
                    Indices = outIndices,
                    Tag = MeshFace.SingleSidedTag,
                    LiftX = 0.0,
                    LiftY = 0.0,
                    LiftZ = 0.0,
                };
                EmitRecord(record, emitted, replaced, replacedByThisSheet);

                if (bottomEdgeColors is not null)
                {
                    for (int k = 0; k < piece.Count; k++)
                    {
                        (long, long, long) ka = Key(piece[k].Position);
                        (long, long, long) kb = Key(piece[(k + 1) % piece.Count].Position);
                        bottomEdgeColors[ka.CompareTo(kb) <= 0 ? (ka, kb) : (kb, ka)] = face.ColorIndex;
                    }
                }

                if (edges is not null)
                {
                    // Boundary census keyed by the (welded) top-skin vertex indices: an edge two
                    // pieces share is counted twice, an outline edge once.
                    for (int k = 0; k < outIndices.Length; k++)
                    {
                        int ia = outIndices[k], ib = outIndices[(k + 1) % outIndices.Length];
                        (int, int) edgeKey = ia <= ib ? (ia, ib) : (ib, ia);
                        edges[edgeKey] = edges.TryGetValue(edgeKey, out (int Count, byte Color, int Leaf) cur)
                            ? (cur.Count + 1, cur.Color, cur.Leaf)
                            : (1, face.ColorIndex, _leafOf[record]);
                        _edgeEnds[edgeKey] = (piece[k % piece.Count].Position, piece[(k + 1) % piece.Count].Position);
                    }
                }
            }
        }

        private readonly Dictionary<(int, int), (V3 A, V3 B)> _edgeEnds = [];

        private List<List<PolyVertex>> Subdivide(List<PolyVertex> poly)
        {
            double[]? stations = _options.Stations;
            List<List<PolyVertex>> pieces = new List<List<PolyVertex>>();
            if (stations is null || stations.Length < 2)
            {
                pieces.Add(poly);
                return pieces;
            }

            for (int k = 0; k + 1 < stations.Length; k++)
            {
                double lo = stations[k], hi = stations[k + 1];
                List<PolyVertex> piece = Clip(Clip(poly, lo, keepAbove: true), hi, keepAbove: false);
                if (piece.Count >= 3)
                {
                    pieces.Add(piece);
                }
            }

            return pieces;
        }

        private static List<PolyVertex> Clip(List<PolyVertex> poly, double threshold, bool keepAbove)
        {
            List<PolyVertex> result = new List<PolyVertex>(poly.Count + 2);
            for (int k = 0; k < poly.Count; k++)
            {
                PolyVertex a = poly[k];
                PolyVertex b = poly[(k + 1) % poly.Count];
                bool aIn = keepAbove ? a.U >= threshold - 1e-9 : a.U <= threshold + 1e-9;
                bool bIn = keepAbove ? b.U >= threshold - 1e-9 : b.U <= threshold + 1e-9;
                if (aIn)
                {
                    result.Add(a);
                }

                if (aIn != bIn && Math.Abs(b.U - a.U) > 1e-12)
                {
                    double t = (threshold - a.U) / (b.U - a.U);
                    result.Add(new PolyVertex(a.Position + ((b.Position - a.Position) * t), threshold, a.Source));
                }
            }

            return result;
        }

        private void EmitWalls(
            SheetInfo sheet,
            Dictionary<(int, int), (int Count, byte Color, int Leaf)> topEdges,
            Dictionary<(long, long, long), int> topOf,
            Dictionary<(long, long, long), int> bottomOf,
            Dictionary<((long, long, long), (long, long, long)), byte> bottomColors)
        {
            V3 sheetCentre = new V3(0, 0, 0);
            foreach (int v in sheet.VertexSet)
            {
                sheetCentre += P(v);
            }

            sheetCentre = ProjectToPlane(sheetCentre * (1.0 / Math.Max(1, sheet.VertexSet.Count)), sheet);

            foreach (((int, int) edgeKey, (int Count, byte Color, int Leaf) info) in topEdges)
            {
                if (info.Count != 1 || !_edgeEnds.TryGetValue(edgeKey, out (V3 A, V3 B) ends))
                {
                    continue;
                }

                (long, long, long) ka = Key(ends.A);
                (long, long, long) kb = Key(ends.B);
                if (!topOf.TryGetValue(ka, out int ta) || !topOf.TryGetValue(kb, out int tb)
                    || !bottomOf.TryGetValue(ka, out int ba) || !bottomOf.TryGetValue(kb, out int bb))
                {
                    continue;
                }

                double heightA = (Pos(ta) - Pos(ba)).Length;
                double heightB = (Pos(tb) - Pos(bb)).Length;
                if (heightA < WallMinHeight && heightB < WallMinHeight)
                {
                    continue;
                }

                // Outward: away from the sheet centre in the plane.
                V3 mid = (ends.A + ends.B) * 0.5;
                V3 outward = (mid - sheetCentre) - (sheet.Normal * sheet.Normal.Dot(mid - sheetCentre));

                // Two quads split at mid-thickness: the upper half in the top skin's colour, the
                // lower half in the bottom skin's, so a wing tip seen from below is underside-
                // coloured (one brown quad showed as brown tips under the FW-190's white wings).
                byte bottomColor = bottomColors.TryGetValue(ka.CompareTo(kb) <= 0 ? (ka, kb) : (kb, ka), out byte c) ? c : info.Color;
                int ma = AddVertex((Pos(ta) + Pos(ba)) * 0.5, _sources[ta]);
                int mb = AddVertex((Pos(tb) + Pos(bb)) * 0.5, _sources[tb]);
                EmitWall([ta, tb, mb, ma], info.Color, info.Leaf, outward);
                EmitWall([ma, mb, bb, ba], bottomColor, info.Leaf, outward);
            }
        }

        private void EmitWall(List<int> quad, byte color, int leaf, V3 outward)
        {
            List<int> distinct = quad.Distinct().ToList();
            if (distinct.Count < 3)
            {
                return;
            }

            V3 n = NewellOf(distinct);
            if (n.Length < 1e-9)
            {
                return;
            }

            if (n.Dot(outward) < 0)
            {
                distinct.Reverse();
            }

            MeshFace wall = new MeshFace(MeshPrimitive.Polygon, [.. distinct], color, MeshFace.SingleSidedTag, 0);
            int index = _out.Count;
            _out.Add(wall);
            if (leaf >= 0)
            {
                if (!_leafAdditions.TryGetValue(leaf, out List<int>? list))
                {
                    _leafAdditions[leaf] = list = [];
                }

                list.Add(index);
            }
        }

        private V3 Pos(int index) => new(_refined[index * 3], _refined[(index * 3) + 1], _refined[(index * 3) + 2]);

        private V3 NewellOf(List<int> indices)
        {
            double nx = 0, ny = 0, nz = 0;
            for (int k = 0; k < indices.Count; k++)
            {
                V3 a = Pos(indices[k]);
                V3 b = Pos(indices[(k + 1) % indices.Count]);
                nx += (a.Y - b.Y) * (a.Z + b.Z);
                ny += (a.Z - b.Z) * (a.X + b.X);
                nz += (a.X - b.X) * (a.Y + b.Y);
            }

            return new V3(nx, ny, nz);
        }

        // ── 3. report ─────────────────────────────────────────────────────────────────────────

        public void Report(ICollection<Sheet>? found, bool generated)
        {
            if (found is null)
            {
                return;
            }

            foreach (SheetInfo sheet in Sheets)
            {
                found.Add(new Sheet(
                    sheet.Kind,
                    [.. sheet.Members],
                    [.. sheet.Decals],
                    sheet.Area,
                    sheet.VMax - sheet.VMin,
                    sheet.WidestChord,
                    generated ? sheet.MaxHalf : 0.0,
                    generated ? sheet.AddedRecords : 0,
                    generated ? sheet.AddedVertices : 0));
            }
        }
    }
}
