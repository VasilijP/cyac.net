namespace CYAC.Port.Render.Map;

/// <summary>
/// The F9 map's world→screen transform: a 2-D ORTHOGRAPHIC top-down plan view, north up.
/// </summary>
/// <remarks>
/// <para>
/// The original's own projection is <c>gfx_viewport_world_to_screen @image@0x1EB74</c> (P320:
/// "2D orthographic, no perspective divide"), and it is the map's ONLY projector — the briefing-map
/// cluster's four call sites are its whole world:
/// <c>briefing_map_mesh_and_symbol_draw @image@0x1E823</c> (the player's heading cross),
/// <c>briefing_map_screen_label_draw_iter @image@0x1E955</c> (the waypoint dots) and
/// <c>briefing_map_screen_obj_line_draw @image@0x1EA8D</c> twice (a course line's two ends).
/// </para>
/// <para><b>The law, byte for byte.</b> With <c>hi(v)</c> the HIGH word of an <c>i32</c> world coordinate
/// — the arena keeps <c>world &lt;&lt; 8</c>, so one hi unit is <b>256 world units</b> (
/// "the 2D map works in world»16 units"):
/// </para>
/// <code>
///   sx = [0xE634] + ((hi(x) − [0xE824]) · 4 · 125/100) &gt;&gt; (10 − zoomShift)   image@0x1EB79..0x1EBF5
///   sy = [0xE636] + (([0xE82C] − hi(z)) · 4 · 125/100) &gt;&gt; (10 − zoomShift)   image@0x1EB84..0x1EC00
/// </code>
/// <para>
/// The Z term is subtracted the other way round (<c>mov di,[0xE82C] / sub di,[bp+0x0C]</c>
/// @<c>image@0x1EB84</c>) — that is the NORTH-UP inversion, and it is why the map has no camera
/// heading in it at all.  <c>zoomShift [0xE836]</c> is <c>clamp(g_map_zoom_level [0xD8A0] − 2, 6, 9)</c>
/// (<c>scene_frame_setup_or_redraw @image@0x1E480..0x1E4A1</c>).  So the scale is
/// <c>5 / (256 · 2^(10 − zoomShift))</c> pixels per world unit on the original's 320-pixel screen —
/// <see cref="WorldUnitsPerDesignPixel"/>.
/// </para>
/// <para>
/// The port keeps the SPAN and drops the pixel grid: at zoom level <c>N</c> the window shows the same
/// world rectangle the 1991 screen showed, so a 1920-wide window is the same map at six times the sampling
/// (<see cref="ForZoomLevel"/>).  Geometry is <c>double</c> (the anti-shimmer law).
/// </para>
/// </remarks>
/// <param name="OriginX">The world X the map is centred on.</param>
/// <param name="OriginZ">The world Z the map is centred on.</param>
/// <param name="PixelsPerWorldUnit">The scale; positive.</param>
/// <param name="CentreX">The screen column the origin lands on.</param>
/// <param name="CentreY">The screen row the origin lands on.</param>
public readonly record struct MapProjection(
    double OriginX, double OriginZ, double PixelsPerWorldUnit, double CentreX, double CentreY)
{
    /// <summary>The design screen the 1991 scale ladder is quoted on: 320 columns.</summary>
    /// <remarks><c>g_screen_width_pixels_cfg [0x0164]</c>, read by
    /// <c>scene_frame_setup_or_redraw @image@0x1E44C</c>.</remarks>
    public const int DesignWidth = 320;

    /// <summary>The lowest zoom level the original's keys reach: 7 (<c>"ZOOM:1"</c>).</summary>
    /// <remarks><c>gauge_zoom_out_keyhandler @image@0x0D8C4</c> decrements to 7.</remarks>
    public const int MinZoomLevel = 7;

    /// <summary>The highest: 12 (<c>"ZOOM:32"</c>) — <c>gauge_zoom_in_keyhandler @image@0x0D8B5</c>.</summary>
    public const int MaxZoomLevel = 12;

    /// <summary>
    /// The zoom SHIFT the projection actually uses: <c>clamp(level − 2, 6, 9)</c>.
    /// </summary>
    /// <param name="zoomLevel">The map zoom level <c>g_map_zoom_level [0xD8A0]</c>, 7..12.</param>
    /// <remarks>
    /// <c>scene_frame_setup_or_redraw</c>: <c>ax = [0xD8A0] − 2</c> (<c>image@0x1E480</c>), <c>cmp
    /// ax,6 / jge</c> (<c>image@0x1E48A</c>), <c>cmp [0xE836],9 / jle</c> (<c>image@0x1E495</c>).
    /// The clamp is NARROWER than the key range: levels 7 and 8 both give shift 6 and levels 11 and
    /// 12 both give shift 9, so the six <c>"ZOOM:n"</c> steps the HUD prints are only FOUR distinct
    /// map scales.  That is the shipped behaviour, reproduced here.
    /// </remarks>
    public static int ZoomShift(int zoomLevel) => Math.Clamp(zoomLevel - 2, 6, 9);

    /// <summary>World units per pixel of the original's 320-column screen, at a zoom level.</summary>
    /// <param name="zoomLevel">7..12.</param>
    /// <remarks>
    /// <c>256 · 2^(10 − shift) / 5</c> — the inverse of the <c>×4 ×125/100 &gt;&gt; (10 − shift)</c>
    /// chain at <c>image@0x1EB80..0x1EBF3</c>, with the 256 that turns a hi unit into world units.
    /// 819.2 at shift 6 … 102.4 at shift 9.
    /// </remarks>
    public static double WorldUnitsPerDesignPixel(int zoomLevel) =>
        256.0 * (1 << (10 - ZoomShift(zoomLevel))) / 5.0;

    /// <summary>Builds the projection for one of the original's zoom levels.</summary>
    /// <param name="originX">World X at the centre.</param>
    /// <param name="originZ">World Z at the centre.</param>
    /// <param name="zoomLevel">7..12.</param>
    /// <param name="width">The target's width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <remarks>
    /// The window shows the world rectangle the 320-column screen showed, whatever its size: the
    /// scale is multiplied by <c>width / 320</c>.  Representing, not reproducing — the same choice
    /// the cockpit layer makes with its design space (<c>CockpitScale</c>).
    /// </remarks>
    public static MapProjection ForZoomLevel(
        double originX, double originZ, int zoomLevel, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        double scale = width / (double)DesignWidth / WorldUnitsPerDesignPixel(zoomLevel);
        return new MapProjection(originX, originZ, scale, width / 2.0, height / 2.0);
    }

    /// <summary>
    /// PORT ADDITION — the FIT: a scale that frames a world rectangle in the target.
    /// </summary>
    /// <param name="minX">The rectangle's west edge, world units.</param>
    /// <param name="minZ">Its south edge.</param>
    /// <param name="maxX">Its east edge.</param>
    /// <param name="maxZ">Its north edge.</param>
    /// <param name="width">The target's width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="marginPixels">Pixels to keep clear on every side.</param>
    /// <remarks>
    /// The original has no "fit": its map is always the player's own position at one of six zoom
    /// levels, clamped to the scene's bounds (<c>image@0x1E55D..0x1E721</c>).  Framing the mission is
    /// the mission EDITOR's idea (<c>MapCanvas.FitToContent</c>), and it is what a player opening the
    /// map first wants to see.  Labelled deviation D2.
    /// </remarks>
    public static MapProjection Fit(
        double minX, double minZ, double maxX, double maxZ, int width, int height,
        double marginPixels = 24.0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        double spanX = Math.Max(maxX - minX, 1.0);
        double spanZ = Math.Max(maxZ - minZ, 1.0);
        double usableX = Math.Max(width - (2.0 * marginPixels), 1.0);
        double usableY = Math.Max(height - (2.0 * marginPixels), 1.0);

        // ONE scale for both axes: a map with different x and z scales is not a map.
        double scale = Math.Min(usableX / spanX, usableY / spanZ);
        return new MapProjection(
            (minX + maxX) / 2.0, (minZ + maxZ) / 2.0, scale, width / 2.0, height / 2.0);
    }

    /// <summary>World units per screen pixel — the scale bar's quantity.</summary>
    public double WorldUnitsPerPixel =>
        PixelsPerWorldUnit > 0.0 ? 1.0 / PixelsPerWorldUnit : double.PositiveInfinity;

    /// <summary>Projects a world point.</summary>
    /// <param name="x">World X.</param>
    /// <param name="z">World Z.</param>
    /// <returns>The screen point, in pixels; Y grows DOWN and north is up.</returns>
    public (double X, double Y) ToScreen(double x, double z) => (
        CentreX + ((x - OriginX) * PixelsPerWorldUnit),
        CentreY - ((z - OriginZ) * PixelsPerWorldUnit));   // image@0x1EB88 — north up

    /// <summary>The world point under a screen pixel — the inverse of <see cref="ToScreen"/>.</summary>
    /// <param name="screenX">The column.</param>
    /// <param name="screenY">The row.</param>
    public (double X, double Z) ToWorld(double screenX, double screenY) => (
        OriginX + ((screenX - CentreX) * WorldUnitsPerPixel),
        OriginZ - ((screenY - CentreY) * WorldUnitsPerPixel));

    /// <summary>The same projection re-centred on another world point.</summary>
    /// <param name="x">World X.</param>
    /// <param name="z">World Z.</param>
    public MapProjection CentredOn(double x, double z) => this with { OriginX = x, OriginZ = z };
}
