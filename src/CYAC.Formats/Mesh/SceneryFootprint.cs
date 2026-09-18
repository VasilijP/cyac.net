namespace CYAC.Formats.Mesh;

// ---------------------------------------------------------------------------
// SCENERY FOOTPRINTS — the theater `.W` layer drawn the way the GAME's own map
// draws it.
//
// The in-game briefing map is a TOP-DOWN X/Z PLAN VIEW that draws scenery
// meshes as projected LINE SEGMENTS:
//   [0xE822]/[0xE82A] are the camera world X and Z i32 origins — a plan view,
//   projecting through gfx_viewport_world_to_screen @image@0x1EB74 (P320: 2D
//   ORTHOGRAPHIC — no perspective divide).
// That is why rivers and roads read as segment chains in-game, and it is what
// this file reproduces for the mission editor's map: each scenery class's mesh
// is decoded once, its edges are projected onto x/z (y dropped), and the
// resulting polyline set is cached per class and translated + rotated onto each
// `.W` placement.
//
// THREE FACTS THIS RESTS ON (all re-derivable from the bytes; `--selftest-map`
// stage M-G re-derives them on every run):
//
//  1. WHICH MESH.  class table image@0x34F90 = 46 x {u8 flag, u16 value}; flag 1
//  => value is a DGROUP-relative near pointer (DGROUP = image@0x3BD60) landing on
//  a MESH-REGISTRY DESCRIPTOR — the same 64-slot registry @image@0x35730 the
//  resource browser decodes.  desc[+0x22] = basename near-ptr, desc[+0x26] =
//  geometry segment, desc[+0x14 + 2*lod] = that LOD's face-descriptor near offset
//  (s_mesh_registry_slot). (This chain NAMES the classes;
//  The rest of it is walked to get the geometry.)
//
//  2. WHICH LOD — the COARSEST, not the densest.  A registry object holds up to
//     3 independent LODs and the engine draws exactly one
//     (mesh_visibility_lod_select @image@0x16BE8); index 0 is the FARTHEST /
//     coarsest.  For the two classes that dominate a theater the coarsest LOD
//     *is* a line:
//         river LOD0 = ONE op-1 edge (0,0,-1024) -> (0,0,1024)   [LOD1 = a quad]
//         road  LOD0 = ONE op-1 edge (0,0,-2048) -> (0,0,2048)   [LOD1 = a quad]
//         airport LOD0 = 11 op-1 edges = the runway outline
//     A map is the far view, so LOD0 is both the engine-faithful choice and the
//     one that produces the segment chains.  <see cref="FootprintDetail"/> can
//     ask for the densest instead; a class whose coarsest LOD yields no edges
//     falls back to the densest automatically.
//
//  3. SCALE — each class descriptor carries a per-class
//  SCALE-SHIFT EXPONENT at desc[+0x0C] (s_mesh_registry_slot
//  .scale_shift_exp_i8): mesh_leaf_cam_delta_normalize_and_rotate @image@0x16898
//  reads it as a signed byte (`mov al,[bx+0xC]; cbw` @image@0x168A2) and shifts
//  the camera→leaf delta right by it, i.e. the mesh renders at 2^exp × world
//  scale.  Shipped scenery: river=2 road=2 rural=2 rural2=2 airport=2 (×4);
//  city=3 mountain=3 mount2=3 urban=3 urban2=3 strip=3 (×8); revet/trees/hedge…=0
//  (1:1).  So a river quad is really 1024×8192 WORLD units and its LOD0 line 8192
//  — which is exactly why consecutive .W river placements (≤8192 apart) form
//  CONTINUOUS courses in-game, in both the map view and the 3D view.  The in-game
//  MAP view draws the same courses independently:
//  briefing_map_screen_obj_line_draw @image@0x1EA8D draws each river/road
//  placement as a line of half-length [0x2F8A+type*2] (river 0x1000=4096, road
//  0x2000=8192) = 2^exp × the LOD0 half-extent, colour [0x2F90+type*2] (river
//  0xFF01, road 0xFF07). g_lod_scale_factor [0x6C4] / g_lod_scale_table [0x4738]
//  still scale only the BBOX EXTENT for cull thresholds — that part of the old
//  note stands. Footprints in this file are therefore emitted PRE-SCALED to world
//  units (segments and bounds ×2^exp);
//  <see cref="SceneryFootprint.ScaleShiftExp"/> records the exponent.
//
// ROTATION.  A `.W` placement is position tag 1 (x/z only) PLUS, on most
// instances, attr 0x80 (`aux0_heading`, 1/8 degree, full circle 2880 —
// 659/746 GERMANY, 529/748 KOREA, 588/884 VIETNAM).  So the footprint is
// rotated by the instance's own heading using the SAME convention the map
// already draws mission-object heading stalks with: 0 = +z, increasing
// clockwise toward +x.
// ---------------------------------------------------------------------------

/// <summary>One footprint edge, in WORLD units (mesh-local ×2^scale_shift_exp; x/z; y dropped).
/// (int, not short: city/mountain-class verts ×8 can exceed short range)</summary>
public readonly record struct FootprintSeg(int X0, int Z0, int X1, int Z1);

/// <summary>
/// THE HEADING CONVENTION — one place, because everything that
/// points (scenery footprints, aircraft heading stalks, shift-drag aiming) must
/// agree with the engine and with each other.
///
/// <para>attr 0x80 stores an angle in 1/8 degree, 2880 = a full circle, and the
/// engine NEGATES it before the trig lookup when it builds an object's rotation
/// matrix: <c>angle_mat3_pair_build @image@0x1BFA4</c> loads the heading and
/// immediately negates it — <c>mov ax,[bp+8]</c> @image@0x1BFAF,
/// <c>neg ax</c> @image@0x1BFB2 (the same treatment its bank and elevation
/// arguments get).  In world x/z that makes heading h point a body's local +z
/// at <c>(-sin h, cos h)</c>: h = 0 is +z, and the angle turns toward -x.</para>
///
/// <para><b>Independent confirmation from the shipped data.</b> Rendering the
/// three theaters' river/road footprints both ways is decisive: with the negation
/// the 507 river instances trace smooth continuous water courses with tributaries;
/// without it the same dashes scatter into noise.  Pilot 17's map drew heading
/// stalks with the un-negated convention (0 = +z turning toward +x) — that was an
/// assumption, and this replaces it.  the engine
/// negates; see above.</para>
/// </summary>
public static class HeadingConvention
{
    /// <summary>attr-0x80 units in a full circle (1/8 degree).</summary>
    public const int UnitsPerCircle = 2880;

    /// <summary>Degrees from attr-0x80 units.</summary>
    public static double Degrees(int eighthDegrees) => eighthDegrees / 8.0;

    /// <summary>The unit direction a heading (in DEGREES) points, in world x/z.</summary>
    public static (double X, double Z) Direction(double degrees)
    {
        double th = degrees * Math.PI / 180.0;
        return (-Math.Sin(th), Math.Cos(th));
    }

    /// <summary>The heading (degrees, 0..360) that points along a world x/z delta.</summary>
    public static double DegreesFromDirection(double dx, double dz)
    {
        double deg = Math.Atan2(-dx, dz) * 180.0 / Math.PI;
        return ((deg % 360) + 360) % 360;
    }
}

/// <summary>Which LOD a footprint is taken from.</summary>
public enum FootprintDetail
{
    /// <summary>LOD 0 — the farthest/coarsest LOD; what a map-scale view draws.</summary>
    Coarsest = 0,
    /// <summary>The densest populated LOD — the near view.</summary>
    Densest = 1,
}

/// <summary>The cached top-down edge footprint of one scenery class's mesh.</summary>
public sealed class SceneryFootprint
{
    public required int ClassId { get; init; }
    public required string Basename { get; init; }
    public required int DescriptorOffset { get; init; }

    /// <summary>Which LOD the segments came from (0..2).</summary>
    public required int Lod { get; init; }

    /// <summary>
    /// The class's scale-shift exponent, desc[+0x0C] (signed byte).
    /// The engine renders this mesh at 2^exp × world scale
    /// (mesh_leaf_cam_delta_normalize_and_rotate @image@0x16898: the camera→leaf
    /// delta is shifted right by it, `mov al,[bx+0xC]; cbw` @image@0x168A2).
    /// <see cref="Segments"/> and the bounds are already multiplied by
    /// <see cref="WorldScale"/>.
    /// </summary>
    public required int ScaleShiftExp { get; init; }

    /// <summary>
    /// 2^<see cref="ScaleShiftExp"/>, exactly — <b>including negative exponents</b>.
    /// <para>
    /// / H6a — <b>four shipped classes ARE negative</b>: <c>eject1</c>… <c>eject4</c> carry
    /// <c>scaleShiftExponent = −2</c> and therefore draw at ×0.25. The engine's own shift is
    /// signed: <c>mov al,[bx+0x0C]; cbw</c> @<c>image@0x168A2</c> in
    /// <c>mesh_leaf_cam_delta_normalize_and_rotate</c>, whose <c>&lt; 0</c> arm
    /// @<c>image@0x1699B</c> LEFT-shifts the camera→leaf delta — i.e. a mesh unit is SMALLER
    /// than a world unit for a negative exponent. The old clamp silently drew those four classes
    /// 4× too big; nothing in the PoC path instances them, so this was a latent bug, not a live
    /// one.
    /// </para>
    /// </summary>
    public double WorldScale => ScaleShiftExp >= 0
        ? 1 << ScaleShiftExp
        : 1.0 / (1 << -ScaleShiftExp);

    /// <summary>vert_count of each LOD slot (0 = the slot is empty).</summary>
    public required int[] LodVertCounts { get; init; }

    /// <summary>The projected edges, mesh-local, de-duplicated, zero-length dropped.</summary>
    public required FootprintSeg[] Segments { get; init; }

    public required int VertexCount { get; init; }
    public required int RecordCount { get; init; }

    /// <summary>Records that contributed no edge: points/discs/effects (op 2/3/4) and out-of-range indices.</summary>
    public required int NonEdgeRecords { get; init; }

    public required int MinX { get; init; }
    public required int MinZ { get; init; }
    public required int MaxX { get; init; }
    public required int MaxZ { get; init; }

    /// <summary>Where the vertices came from: the class's <c>.PNT</c> file, or the in-image blob.</summary>
    public required string VertexSource { get; init; }

    public int SpanX => MaxX - MinX;
    public int SpanZ => MaxZ - MinZ;

    /// <summary>Half-diagonal of the local bounding box — the cull radius, in world units.</summary>
    public int Radius
    {
        get
        {
            int rx = Math.Max(Math.Abs(MinX), Math.Abs(MaxX));
            int rz = Math.Max(Math.Abs(MinZ), Math.Abs(MaxZ));
            return (int)Math.Ceiling(Math.Sqrt((double)rx * rx + (double)rz * rz));
        }
    }

    public bool IsEmpty => Segments.Length == 0;

    public override string ToString() =>
        $"{Basename} (class {ClassId}) LOD{Lod}: {Segments.Length} seg, {VertexCount} v, " +
        $"x[{MinX},{MaxX}] z[{MinZ},{MaxZ}]";
}

/// <summary>Decodes scenery-class mesh footprints straight out of the L1 image.</summary>
public static class SceneryFootprints
{
    /// <summary>image@ of the 46-record class table (3 B per record).</summary>
    public const int ClassTableOffset = 0x34F90;
    public const int ClassTableRecords = 46;
    public const int DGroupBase = MeshDecoder.DGroupBase;   // image@0x3BD60

    /// <summary>The class-id run the three shipping `.W` place scenery from.</summary>
    public const int FirstSceneryClass = 26;
    public const int LastSceneryClass = 45;

    /// <summary>
    /// class id -> mesh-registry DESCRIPTOR image@ for every flag-1 record in
    /// the class table.  (flag 2 = the arm where aircraft_class_table_lookup
    /// echoes AX back — not an opener, so it has no mesh; id 35 is the one hole
    /// in the 26..45 run.)
    /// </summary>
    public static Dictionary<int, int> ReadFlag1Descriptors(byte[] img)
    {
        Dictionary<int, int> map = new Dictionary<int, int>();
        for (int i = 0; i < ClassTableRecords; i++)
        {
            int rec = ClassTableOffset + i * 3;
            if (rec + 3 > img.Length) break;
            if (img[rec] != 1) continue;
            int desc = DGroupBase + (img[rec + 1] | (img[rec + 2] << 8));
            if (desc > 0 && desc + 0x28 <= img.Length) map[i] = desc;
        }
        return map;
    }

    // -----------------------------------------------------------------------
    // The in-game MAP view's own river/road course tables.
    //
    // The map view does NOT draw scenery meshes for rivers/roads: the enqueuer
    // screen_buffer_iter_dispatch @image@0x1E766 copies EVERY pool object whose
    // descriptor near-ptr is 0x98E8 (road) or 0x985C (river) into a 10-B record
    // {sub_type (road 0 / river 2), heading>>4, pos_x i32, pos_z i32}
    // (runtime-proven: a 318-record buffer dumped from a KOREA session matched
    // all 149 river + 169 road .W placements EXACTLY, position and heading).  The drawer briefing_map_screen_obj_line_draw @image@0x1EA8D
    // then draws ONE line per record: center ± half-length rotated to (heading
    // + 90°), half-length from DGROUP+0x2F8A+sub_type*2 and colour from
    // DGROUP+0x2F90+sub_type*2. Those half-lengths (river 0x1000=4096, road
    // 0x2000=8192) are exactly 2^scale_shift_exp × the LOD0 half-extents, so
    // the pre-scaled LOD0 footprints this file emits ARE the in-game map lines.
    // -----------------------------------------------------------------------

    /// <summary>DGROUP offset of the map-course half-length table (u16[3], indexed by sub_type).</summary>
    public const int MapCourseHalfLenTableDGroup = 0x2F8A;
    /// <summary>DGROUP offset of the map-course colour table (u16[3], indexed by sub_type).</summary>
    public const int MapCourseColorTableDGroup = 0x2F90;

    /// <summary>The map enqueuer's sub_type for a scenery class (road 33 → 0, river 32 → 2; else null).</summary>
    public static int? MapCourseSubType(int classId) => classId switch { 33 => 0, 32 => 2, _ => null };

    /// <summary>Read a class's in-game map-course line (half-length in world units + raw colour word).</summary>
    public static (int HalfLength, int ColorWord)? ReadMapCourse(byte[] img, int classId)
    {
        if (MapCourseSubType(classId) is not { } t) return null;
        // sub_type indexes the tables as WORDS (byte offset = t*2): road 0 → +0,
        // river 2 → +4.  Runtime colour proof (P21 display-list capture): river
        // lines draw 0x01 = word [DGROUP+0x2F94] = 0xFF01, road lines 0x07 =
        // word [DGROUP+0x2F90] = 0xFF07.
        int hl = img[DGroupBase + MapCourseHalfLenTableDGroup + t * 2] |
                 (img[DGroupBase + MapCourseHalfLenTableDGroup + t * 2 + 1] << 8);
        int cw = img[DGroupBase + MapCourseColorTableDGroup + t * 2] |
                 (img[DGroupBase + MapCourseColorTableDGroup + t * 2 + 1] << 8);
        return (hl, cw);
    }

    /// <summary>
    /// Decode one class's footprint.  Returns null when the class has no flag-1
    /// descriptor or no populated LOD at all; a decoded-but-edgeless class comes
    /// back with <see cref="SceneryFootprint.IsEmpty"/> true so the caller can
    /// fall back to a glyph.
    /// </summary>
    /// <param name="img">The unpacked L1 image.</param>
    /// <param name="classId">The class id.</param>
    /// <param name="descOffset">The class's registry descriptor (image offset).</param>
    /// <param name="pnts">Where the class mesh's .PNT comes from (P4-G2: the caller's).</param>
    /// <param name="detail">Which end of the LOD ladder to draw.</param>
    public static SceneryFootprint? Build(byte[] img, int classId, int descOffset, PntLookup pnts,
                                          FootprintDetail detail = FootprintDetail.Coarsest)
    {
        MeshLodSet set = MeshDecoder.DecodeRegistrySlotLods(img, descOffset, null, pnts);
        int lod = detail == FootprintDetail.Coarsest ? set.LowestLod : set.HighestLod;
        if (lod < 0) return null;

        // Per-class world scale = 2^desc[+0x0C] (signed byte —
        // mesh_leaf_cam_delta_normalize_and_rotate @image@0x16898 / @0x168A2).
        int exp = (sbyte)img[descOffset + 0x0C];

        SceneryFootprint fp = FromLod(set, classId, descOffset, lod, exp);
        // A LOD that carries only points/discs (or whose indices are all out of
        // range) gives no picture — take the other end of the LOD ladder rather
        // than draw nothing.
        if (fp.IsEmpty)
        {
            int other = detail == FootprintDetail.Coarsest ? set.HighestLod : set.LowestLod;
            if (other >= 0 && other != lod)
            {
                SceneryFootprint alt = FromLod(set, classId, descOffset, other, exp);
                if (!alt.IsEmpty) return alt;
            }
        }
        return fp;
    }

    private static SceneryFootprint FromLod(MeshLodSet set, int classId, int descOffset, int lod, int exp)
    {
        // / H6a — that clamp turned the four NEGATIVE-exponent classes (`eject1`..`eject4`,
        // exp = −2) into ×1 instead of ×0.25. The engine's exponent is signed (`mov
        // al,[bx+0x0C]; cbw` @image@0x168A2; the `< 0` arm @image@0x1699B left-shifts the
        // delta), so mesh→world for exp < 0 is an arithmetic RIGHT shift — which is also
        // what the 8086 `sar` in that path does, including its floor-toward −infinity
        // rounding. Integer footprints stay integer.
        static int ToWorld(int v, int e) => e >= 0 ? v << e : v >> -e;
        MeshRecord rec = set.Lods[lod]!;
        Vec3i[] verts = rec.Vertices;
        HashSet<(int, int)> seen = new HashSet<(int, int)>();
        List<FootprintSeg> segs = new List<FootprintSeg>();
        int nonEdge = 0;

        foreach (Polygon poly in rec.Polygons)
        {
            byte[] idx = poly.Indices;
            // opcode 0 = filled N-gon (closed), 1 = line/edge; 2/3/4 are
            // point / disc / special-effect records and have no footprint.
            bool closed = poly.PrimitiveOpcode == 0 && idx.Length > 2;
            if (poly.PrimitiveOpcode is not (0 or 1) || idx.Length < 2) { nonEdge++; continue; }
            bool contributed = false;
            int n = idx.Length;
            int edges = closed ? n : n - 1;
            for (int k = 0; k < edges; k++)
            {
                int a = idx[k], b = idx[(k + 1) % n];
                if (a == b || a >= verts.Length || b >= verts.Length) continue;
                Vec3i pa = verts[a];
                Vec3i pb = verts[b];
                if (pa.X == pb.X && pa.Z == pb.Z) continue;         // vertical edge -> a point from above
                (int, int) key = a < b ? (a, b) : (b, a);
                if (!seen.Add(key)) { contributed = true; continue; }
                segs.Add(new FootprintSeg(ToWorld(pa.X, exp), ToWorld(pa.Z, exp),
                                          ToWorld(pb.X, exp), ToWorld(pb.Z, exp)));
                contributed = true;
            }
            if (!contributed) nonEdge++;
        }

        int minX = 0, minZ = 0, maxX = 0, maxZ = 0;
        if (verts.Length > 0)
        {
            minX = minZ = int.MaxValue;
            maxX = maxZ = int.MinValue;
            foreach (Vec3i v in verts)
            {
                if (v.X < minX) minX = v.X;
                if (v.X > maxX) maxX = v.X;
                if (v.Z < minZ) minZ = v.Z;
                if (v.Z > maxZ) maxZ = v.Z;
            }
        }

        // Bounds in WORLD units too (× the class scale, 2^exp — signed).
        minX = ToWorld(minX, exp); maxX = ToWorld(maxX, exp);
        minZ = ToWorld(minZ, exp); maxZ = ToWorld(maxZ, exp);

        return new SceneryFootprint
        {
            ClassId = classId,
            Basename = set.Basename,
            DescriptorOffset = descOffset,
            Lod = lod,
            ScaleShiftExp = exp,
            LodVertCounts = rec.LodVertCounts.ToArray(),
            Segments = segs.ToArray(),
            VertexCount = verts.Length,
            RecordCount = rec.Polygons.Length,
            NonEdgeRecords = nonEdge,
            MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ,
            VertexSource = rec.PntSourceBasename is { } p ? $"{p}.PNT LOD{lod}" : "in-image blob",
        };
    }

    /// <summary>Every flag-1 class in the scenery run, decoded once.</summary>
    /// <param name="img">The unpacked L1 image.</param>
    /// <param name="pnts">Where the class meshes' .PNTs come from.</param>
    /// <param name="detail">Which end of the LOD ladder to draw.</param>
    /// <param name="firstClass">The first class id to decode.</param>
    /// <param name="lastClass">The last class id to decode.</param>
    public static Dictionary<int, SceneryFootprint> BuildAll(
        byte[] img, PntLookup pnts, FootprintDetail detail = FootprintDetail.Coarsest,
        int firstClass = FirstSceneryClass, int lastClass = LastSceneryClass)
    {
        Dictionary<int, SceneryFootprint> result = new Dictionary<int, SceneryFootprint>();
        foreach ((int cid, int desc) in ReadFlag1Descriptors(img))
        {
            if (cid < firstClass || cid > lastClass) continue;
            if (Build(img, cid, desc, pnts, detail) is { } fp) result[cid] = fp;
        }
        return result;
    }

    // The image is the caller's; the development tools keep the walk-up that found it.
}

/// <summary>
/// A footprint catalog: one decode per class, shared by every map and every
/// mission that holds it.  Placement is a translate + rotate of the cached
/// mesh-local segments, which is what keeps a ~900-instance theater cheap.
/// </summary>
/// <remarks>
/// A tool that wants one catalog per process builds it with <see cref="FromImage"/> from inputs it
/// found itself.
/// </remarks>
public sealed class SceneryFootprintCatalog
{
    private readonly Dictionary<int, SceneryFootprint> _byClass;

    private SceneryFootprintCatalog(Dictionary<int, SceneryFootprint> byClass, string source)
    {
        _byClass = byClass;
        Source = source;
    }

    /// <summary>Where the geometry came from (for the status line), e.g. the L1 image path.</summary>
    public string Source { get; }

    public IReadOnlyDictionary<int, SceneryFootprint> ByClass => _byClass;

    public SceneryFootprint? For(int classId) =>
        _byClass.TryGetValue(classId, out SceneryFootprint? f) && !f.IsEmpty ? f : null;

    public int TotalSegments => _byClass.Values.Sum(f => f.Segments.Length);

    /// <summary>Build a catalog from an explicit image and .PNT source.</summary>
    /// <param name="img">The unpacked L1 image.</param>
    /// <param name="pnts">Where the class meshes' .PNTs come from.</param>
    /// <param name="detail">Which end of the LOD ladder to draw.</param>
    /// <param name="source">Where the geometry came from, for a status line.</param>
    public static SceneryFootprintCatalog FromImage(
        byte[] img, PntLookup pnts, FootprintDetail detail = FootprintDetail.Coarsest,
        string source = "(explicit)") =>
        new(SceneryFootprints.BuildAll(img, pnts, detail), source);

    // ---- placement ---------------------------------------------------------

    /// <summary>
    /// cos/sin for a placement heading in attr-0x80 units, in the engine's
    /// convention (<see cref="HeadingConvention"/>: the angle is NEGATED, so
    /// local +z lands on (-sin h, cos h)).
    /// </summary>
    public static (double Cos, double Sin) Rotation(int? headingEighthDegrees)
    {
        if (headingEighthDegrees is not { } h || h == 0) return (1.0, 0.0);
        double th = -HeadingConvention.Degrees(h) * Math.PI / 180.0;
        return (Math.Cos(th), Math.Sin(th));
    }

    /// <summary>
    /// Place one mesh-local point at a world position under a heading:
    /// <c>world = origin + R(heading) * local</c>, R taking local +z onto the
    /// heading direction (sin, cos).
    /// </summary>
    public static (double X, double Z) Place(double localX, double localZ,
                                             int worldX, int worldZ, double cos, double sin) =>
        (worldX + localX * cos + localZ * sin,
         worldZ - localX * sin + localZ * cos);
}
