namespace CYAC.Port.Core.Model.World;

/// <summary>
/// The GROUND REFERENCE GRID — the small grey balls on the ground that give altitude and speed a
/// scale.  One <c>spheres</c> object, snapped to its own lattice around the camera, whose scale is
/// rewritten from the camera's altitude every frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is.</b> <c>data/meshes/spheres.json</c> LOD0 is a 9 × 9 PLANAR GRID: 81 vertices, <c>y
/// = 0</c> on every one, <c>x</c> and <c>z</c> each ∈ {−4096, −3072, …, +4096} — spacing 1,024 MODEL
/// units.  All 81 shape records are the same solid disc: <c>{opcode 3, colour 8, selector 0xFF,
/// radius 8}</c> (<c>g_spheres_dotcloud_circle_records [0x9BDC]</c> = <c>image@0x4593C</c>, 81 × 8
/// B), and palette index 8 is <c>(21,21,21)/63</c> → mid-grey.  The class's own emitter
/// <c>mesh_emit_spheres_dotcloud @image@0x1EE32</c> just draws all 81 of them (the LOD descriptor's
/// <c>+0x0D</c> bit 0 — <c>spheres</c> is the ONLY shipped LOD with it set — makes <c>+0x0A</c> a
/// NEAR code pointer, <c>image_addr(0x201D, 0xEC62) = image@0x1EE32</c>, instead of a paint-tree
/// root).
/// </para>
/// <para>
/// <b>Who instances it.</b> <c>scene_or_mission_state_reset @image@0x0C47F</c> — ONE object, once
/// per session: a zeroed 24-byte template with its class word set to <c>0x9B7E</c> (the
/// <c>spheres</c> registry slot, <c>data/exe/meshes/spheres.json</c> <c>slot.dgroup</c>; registry
/// entry 55 at <c>image@0x3579E</c>) handed to <c>pool_insert_with_bbox_or @image@0x153A1</c> with
/// <c>dst_slot = 0x8A</c> (<c>[0x96] g_scene_render_list_head</c>), the result stored in
/// <c>g_alloc_slot_C3FD_b [0x4718]</c>.  Not a lattice of objects and not a per-player follower: one
/// instance that gets MOVED.
/// </para>
/// <para>
/// <b>Who moves it.</b> <c>alloc_slot_b_camera_pos_snap_update @image@0x2DCF4</c>, once a frame from
/// <c>mission_state_machine @image@0x15CA</c> (and from <c>ui_film_review_screen
/// @image@0x32BC7</c>).  It (a) hides the grid above <see cref="HiddenAboveWorldUnits"/>, (b) picks
/// the class scale exponent from the camera's altitude (<see cref="ScaleShiftExponentFor"/>) and
/// WRITES it into the registry slot's own <c>+0x0C scale_shift_exp_i8</c> at DGROUP <c>[0x9B8A]</c>,
/// and (c) snaps the object to <see cref="Position"/>.
/// </para>
/// <para>
/// Runtime evidence: a captured frame of the original (MiG-21 external at 2,562 ft, so exponent
/// 2) contains exactly eight isolated palette-8 grey clusters on the green ground — three ≈6-pixel
/// discs and five 2-pixel dots at 640×400 — and nothing else that colour below the horizon.
/// </para>
/// <para>
/// INT-only content: every number here is the original's.
/// </para>
/// </remarks>
public static class GroundGrid
{
    /// <summary>The mesh the grid draws — registry slot 55.</summary>
    public const string MeshBasename = "spheres";

    /// <summary>How many balls the grid holds: 9 × 9.</summary>
    public const int BallCount = 81;

    /// <summary>The lattice spacing in MODEL units — the grid's own vertex pitch.</summary>
    public const int SpacingModelUnits = 1024;

    /// <summary>How many balls the 9 × 9 grid has along one side.</summary>
    public const int BallsPerSide = 9;

    /// <summary>The disc radius each ball is drawn with, in MODEL units (<c>record[+5]</c>).</summary>
    public const int BallRadiusModelUnits = 8;

    /// <summary>
    /// The altitude, in world units (= feet), at or above which the grid is switched off entirely:
    /// a round <b>30,000 ft</b>.
    /// </summary>
    /// <remarks>
    /// <c>image@0x2DCFC</c>: <c>cmp [0xD894],0x75 / jl on / jg off / cmp [0xD892],0x3000 / jb on</c>
    /// — i.e. off once the view anchor's Y reaches <c>0x0075_3000</c> position units, and a position
    /// unit is 1/256 world unit.
    /// </remarks>
    public const int HiddenAboveWorldUnits = 0x0075_3000 >> 8;

    /// <summary>
    /// The lowest <c>g_graphics_detail_level [0xF108]</c> that draws the grid.
    /// </summary>
    /// <remarks>
    /// <c>detail_level_pool_slot_b_thunk @image@0x0C0E2</c>:
    /// <c>cmp byte [0xF108],1 / jb → 0 / else → 1</c>, handed to
    /// <c>pool_slot_b_active_flag_set @image@0x2DCD5</c>.  So the Graphics menu's <i>Low Detail</i>
    /// turns the balls off and <i>Medium</i> / <i>High</i> turn them on
    /// (a captured frame of the original).
    /// </remarks>
    public const int MinimumDetailLevel = 1;

    /// <summary>
    /// The altitude thresholds, in world units, at which the grid's scale exponent steps up.
    /// </summary>
    /// <remarks>
    /// <c>image@0x2DD12..0x2DD42</c> compares <c>[0xD893]</c> — the view anchor's Y i32 read one
    /// byte in, i.e. <c>Y &gt;&gt; 8</c> = the altitude in world units — against 0x1F40 / 0xFA0 /
    /// 0x7D0 / 0x3E8 and yields 4 / 3 / 2 / 1, else 0.
    /// </remarks>
    public static ReadOnlySpan<int> AltitudeThresholdsWorldUnits => [0x03E8, 0x07D0, 0x0FA0, 0x1F40];

    /// <summary>
    /// The scale exponent the original installs for a given camera altitude: 0…4.
    /// </summary>
    /// <param name="altitudeWorldUnits">The view anchor's Y in world units (= feet).</param>
    /// <remarks>
    /// The shipped static <c>scaleShiftExponent</c> of <c>spheres</c> is 5, and the runtime NEVER
    /// uses it: <c>image@0x2DD44</c> (<c>mov [0x9B8A],cl</c>) writes this value into the registry
    /// slot's own <c>+0x0C</c> every frame (slot DGROUP <c>0x9B7E</c> + <c>0x0C</c> = <c>0x9B8A</c>).
    /// A model unit is <c>2^exp</c> world units, so the ball spacing runs 1,024 → 16,384 ft and the
    /// ball radius 8 → 128 ft: the lattice keeps a roughly CONSTANT apparent size as the camera
    /// climbs, which is what makes it a height cue.
    /// </remarks>
    public static int ScaleShiftExponentFor(int altitudeWorldUnits)
    {
        ReadOnlySpan<int> thresholds = AltitudeThresholdsWorldUnits;
        for (int i = thresholds.Length - 1; i >= 0; i--)
        {
            if (altitudeWorldUnits >= thresholds[i])
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>The lattice cell, in WORLD units, at a given scale exponent.</summary>
    /// <param name="scaleShiftExponent">0…4.</param>
    /// <remarks>
    /// <c>SpacingModelUnits &lt;&lt; exp</c> — and the snap mask <c>0xFFFC_0000 &lt;&lt; exp</c>
    /// (<c>image@0x2DD62</c>) clears exactly the bits below <c>cell &lt;&lt; 8</c> position units, so
    /// the mask period and the mesh's own vertex pitch are the same number.  That is what keeps a
    /// ball at a fixed world position while the 9 × 9 patch follows the camera.
    /// </remarks>
    public static int CellWorldUnits(int scaleShiftExponent) =>
        SpacingModelUnits << scaleShiftExponent;

    /// <summary>Whether the grid is drawn at all this frame.</summary>
    /// <param name="altitudeWorldUnits">The camera's altitude, world units.</param>
    /// <param name="detailLevel"><c>g_graphics_detail_level [0xF108]</c>, 0/1/2.</param>
    public static bool IsVisible(int altitudeWorldUnits, int detailLevel) =>
        detailLevel >= MinimumDetailLevel && altitudeWorldUnits < HiddenAboveWorldUnits;

    /// <summary>
    /// Where the grid object sits this frame, in WORLD units — the camera snapped to the lattice.
    /// </summary>
    /// <param name="anchorXWorldUnits">The view anchor's X.</param>
    /// <param name="anchorZWorldUnits">The view anchor's Z.</param>
    /// <param name="scaleShiftExponent">The exponent from <see cref="ScaleShiftExponentFor"/>.</param>
    /// <returns>The object's world X, Y (always 0 — it lies on the ground plane) and Z.</returns>
    /// <remarks>
    /// <c>image@0x2DD4D..0x2DDAE</c>, in position units:
    /// <c>X = (anchorX + HALF) &amp; MASK</c>, <c>Y = 0</c>, <c>Z = (anchorZ + HALF) &amp; MASK</c>
    /// with <c>HALF = 0x0008_0000 &lt;&lt; exp</c> and <c>MASK = 0xFFFC_0000 &lt;&lt; exp</c>
    /// (both built by <c>shl_i32_by_cl @image@0x0020A</c>).  <c>HALF</c> is exactly TWO cells, so it
    /// is a pure +2-cell translation on top of the snap — not a round-to-nearest half cell.  That is
    /// what the bytes say; the port copies it rather than "fixing" it, because the offset is
    /// visible (the patch is centred two cells to +X/+Z of the camera).
    /// The whole computation is done here in world units: the mask clears at least eight low
    /// position-unit bits, so shifting out the sub-world-unit byte first changes nothing.
    /// </remarks>
    public static (int X, int Y, int Z) Position(
        int anchorXWorldUnits, int anchorZWorldUnits, int scaleShiftExponent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(scaleShiftExponent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scaleShiftExponent, 13);
        int cell = CellWorldUnits(scaleShiftExponent);
        int half = 2 * cell;
        int mask = ~(cell - 1);
        return ((anchorXWorldUnits + half) & mask, 0, (anchorZWorldUnits + half) & mask);
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────
    // The FIXED lattice — the port's default
    // ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pitch of the port's FIXED ground lattice, in world units: <b>1,024</b>.
    /// </summary>
    /// <remarks>
    /// It is the original's own BAND-0 cell — <see cref="CellWorldUnits"/> at exponent 0, i.e. what
    /// the game itself uses below 1,000 ft (<c>image@0x2DD42</c>).  The port keeps that one pitch at
    /// every altitude instead of stepping it, so a ball never moves and never changes size: the
    /// balls at FIXED world positions and a FIXED size, rendered naturally.
    /// </remarks>
    public const int FixedPitchWorldUnits = SpacingModelUnits;

    /// <summary>The radius every fixed-lattice ball is drawn at, in world units: <b>8</b>.</summary>
    /// <remarks>
    /// The original's band-0 radius: the record's own 8 model units at exponent 0
    /// (<c>g_spheres_dotcloud_circle_records [0x9BDC]</c>).  A 16-ft ball every 1,024 ft, which is
    /// what makes it read as a texture on the ground rather than as a set of objects.
    /// </remarks>
    public const int FixedBallRadiusWorldUnits = BallRadiusModelUnits;

    /// <summary>
    /// The world period of ONE <c>spheres</c> instance on the fixed lattice: 9 × 1,024 = 9,216.
    /// </summary>
    /// <remarks>
    /// The mesh spans −4,096…+4,096 at exponent 0, so instances placed every 9,216 world units butt
    /// up exactly: the last ball of one and the first of the next are one pitch apart.  Tiling the
    /// SHIPPED mesh rather than emitting single balls is what keeps the cost at one instance per 81
    /// balls.
    /// </remarks>
    public const int FixedTilePeriodWorldUnits = BallsPerSide * FixedPitchWorldUnits;

    /// <summary>
    /// How many whole tile periods of fixed lattice are drawn around the camera per axis: <b>3</b>.
    /// </summary>
    /// <remarks>
    /// 3 periods = 27,648 world units, and a ball of radius 8 is already sub-pixel at 1920×1080
    /// beyond 12,288 units (<c>2·8·focal/d &lt; 1</c> with the original's own derived focal length),
    /// so the outermost ring is invisible before the tiling ends — which is the property that lets
    /// <see cref="FixedFadeAt"/> take a ball out at zero opacity.  49 instances, 3,969 balls.
    /// </remarks>
    public const int DefaultFixedTiles = 3;

    /// <summary>How many <c>spheres</c> instances a fixed lattice of this radius draws.</summary>
    /// <param name="tiles">The tile radius.</param>
    public static int FixedTileCount(int tiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tiles);
        int side = (2 * tiles) + 1;
        return side * side;
    }

    /// <summary>
    /// Where one tile of the FIXED lattice sits, in world units.
    /// </summary>
    /// <param name="tileX">The tile's X index relative to the camera's own tile, −tiles…+tiles.</param>
    /// <param name="tileZ">Its Z index.</param>
    /// <param name="anchorXWorldUnits">The camera's X.</param>
    /// <param name="anchorZWorldUnits">The camera's Z.</param>
    /// <returns>The instance's world X, Y (0) and Z.</returns>
    /// <remarks>
    /// The position is a whole multiple of <see cref="FixedTilePeriodWorldUnits"/> measured from the
    /// WORLD origin — the camera only chooses WHICH tiles are drawn, never where they are.  So every
    /// ball has one world position for the whole sortie and the lattice cannot slide, jump or re-scale
    /// under the aircraft, which is the whole point of the mode.  (The original instead re-snaps its
    /// single object every frame and rewrites its scale exponent per altitude band —
    /// <see cref="Position"/> and <see cref="ScaleShiftExponentFor"/>, kept as <c>--ground-balls
    /// classic</c>.)
    /// </remarks>
    public static (int X, int Y, int Z) FixedTilePosition(
        int tileX, int tileZ, int anchorXWorldUnits, int anchorZWorldUnits)
    {
        int period = FixedTilePeriodWorldUnits;
        int homeX = (int)Math.Round(anchorXWorldUnits / (double)period) * period;
        int homeZ = (int)Math.Round(anchorZWorldUnits / (double)period) * period;
        return (homeX + (tileX * period), 0, homeZ + (tileZ * period));
    }

    /// <summary>
    /// The opacity a fixed-lattice tile is drawn at, so one entering or leaving the set does so at
    /// zero.
    /// </summary>
    /// <param name="deltaX">Tile X minus camera X, world units.</param>
    /// <param name="deltaZ">Tile Z minus camera Z, world units.</param>
    /// <param name="tiles">The tile radius in force.</param>
    /// <remarks>
    /// The same Chebyshev ramp the tiled cloud deck uses (<see cref="CloudDeck.FadeAt"/>): a tile can
    /// only appear or vanish when its per-axis distance crosses <c>(tiles + ½) × period</c>, so the
    /// fade reaches 0 exactly there.  Presentation only.
    /// </remarks>
    public static double FixedFadeAt(double deltaX, double deltaZ, int tiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tiles);
        double edge = (tiles + 0.5) * FixedTilePeriodWorldUnits;
        double band = FixedTilePeriodWorldUnits / 2.0;
        double chebyshev = Math.Max(Math.Abs(deltaX), Math.Abs(deltaZ));
        if (chebyshev <= edge - band)
        {
            return 1.0;
        }

        return chebyshev >= edge ? 0.0 : (edge - chebyshev) / band;
    }
}
