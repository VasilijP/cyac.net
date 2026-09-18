namespace CYAC.Port.Render.Map;

/// <summary>
/// The map's colours and its 1991 marker geometry, each cited to the byte or to the pixel that
/// fixes it.
/// </summary>
/// <remarks>
/// <para>
/// The primary evidence for the whole look is one in-game map frame (KOREA).  Its seven colours
/// are exactly the seven this class names, and the player's cross in it is pixel-for-pixel the
/// geometry <c>briefing_map_mesh_and_symbol_draw @image@0x1E823</c> computes.
/// </para>
/// <para>
/// Indices, not RGB: the port renders in true colour but the COLOURS are game facts, so they are
/// carried as VGA palette indices and resolved through the shipped palette
/// (<c>data/palettes/palette.json</c>) exactly as the scene renderer does.
/// </para>
/// </remarks>
public static class MapLook
{
    /// <summary>
    /// The map's background: palette <b>254</b>, the same green the 3-D ground is painted in.
    /// </summary>
    /// <remarks>
    /// <c>briefing_map_mesh_and_symbol_draw</c> fills the viewport through the span-fill dispatcher
    /// <c>gfx_polygon_span_fill_dispatch_stub @image@0x10C6A</c> (<c>image@0x1E871</c>).  Which
    /// colour byte it uses is not decoded; the SCREENSHOT settles it — 60,560 of the frame's 64,000
    /// pixels are <c>#71B600</c>, the exact 6→8 widening of palette entry 254 <c>(28, 45, 0)</c>,
    /// which is <see cref="SceneColors.GroundPaletteIndex"/>.
    /// </remarks>
    public const int BackgroundColorIndex = 254;

    /// <summary>Roads: palette <b>7</b> (light grey).</summary>
    /// <remarks>
    /// <c>g_map_course_color_table [0x2F90]</c> entry for sub_type 0 is <c>0xFF07</c> (
    /// display-list-proven).  The screenshot's 1,606 <c>#AAAAAA</c> pixels are that entry.
    /// </remarks>
    public const int RoadColorIndex = 7;

    /// <summary>Rivers: palette <b>1</b> (blue).</summary>
    /// <remarks>
    /// The same table's sub_type-2 entry, <c>0xFF01</c>; the screenshot's 933 <c>#0000AA</c> pixels.
    /// </remarks>
    public const int RiverColorIndex = 1;

    /// <summary>A waypoint's 5×7 dot and its name: palette <b>15</b> (white).</summary>
    /// <remarks>
    /// <c>briefing_map_screen_label_draw_iter</c>: the filled rect takes <c>0xFF0F</c>
    /// (<c>image@0x1EA14</c>) and so does the name string (<c>image@0x1EA5E</c>).  Screenshot: the
    /// 5×7 white box at (132..136, 153..159) with "Home Base" beside it.
    /// </remarks>
    public const int WaypointColorIndex = 15;

    /// <summary>The digit inside the dot: palette <b>0</b> (black).</summary>
    /// <remarks>
    /// The glyph call at <c>image@0x1EA30</c> does not carry its colour in the decode; the
    /// screenshot's black pixels INSIDE the white box (133,155)/(133,158)/(135,158) settle it.
    /// </remarks>
    public const int WaypointDigitColorIndex = 0;

    /// <summary>The player's heading cross: palette <b>8</b> (dark grey).</summary>
    /// <remarks>
    /// <c>image@0x1E91A..0x1E924</c>: <c>cmp [0x15E],1 / sbb si,si / and si,0xFFF8 / add si,0xFF08</c>
    /// — sub_mode 0 gives <c>0xFF00</c> (index 0) and every other sub_mode gives <c>0xFF08</c> (index
    /// 8).  The shipped configuration is sub_mode 6 (<c>yeager.cfg@0x02</c>), so the game
    /// draws index 8 — which is exactly the <c>#555555</c> cross in the screenshot. The decoded file's
    /// comment is wrong twice: 0xFF00 is BLACK, and it is not the arm the shipped build takes.
    /// </remarks>
    public const int PlayerColorIndex = 8;

    /// <summary>The border scanline under the map: palette <b>0</b>.</summary>
    /// <remarks>
    /// <c>scene_frame_setup_or_redraw</c> draws <c>gfx_hline_draw(0xFF00, [0x166] − 1, 0x140, 0)</c>
    /// (<c>image@0x1E74E..0x1E75B</c>) — a black line right across the bottom row, which the
    /// screenshot has at y = 199.
    /// </remarks>
    public const int BorderColorIndex = 0;

    /// <summary>PORT ADDITION — a friendly aircraft: palette <b>9</b> (light blue).</summary>
    /// <remarks>
    /// Not from the map: from the game's own IDENTIFICATION KEY.  The BOX VIEW colours an object by
    /// <c>WorldObjectFlags.Hostile</c> (flag word bit 10) at <c>image@0x33E6C</c>
    /// (<c>and al,0xFD / add ax,0xFF0C</c>): clear ⇒ <c>0xFF09</c> "Friendlies and bailed-out
    /// pilots", set ⇒ <c>0xFF0C</c> "Hostiles" (manual p.67).  The port's map plots aircraft, which
    /// the original's does not, so it borrows the colours the player already knows.
    /// </remarks>
    public const int FriendlyColorIndex = 9;

    /// <summary>PORT ADDITION — a hostile aircraft: palette <b>12</b> (light red).</summary>
    /// <remarks>The other arm of <c>image@0x33E6C</c>; see <see cref="FriendlyColorIndex"/>.</remarks>
    public const int HostileColorIndex = 12;

    /// <summary>PORT ADDITION — a crater or a wreck: palette <b>4</b> (dark red).</summary>
    /// <remarks>
    /// A port CHOICE, not a game fact: nothing in the image colours a wreck on a map, because
    /// nothing in the image puts one there.  Dark red reads as "was hostile, is dead" beside
    /// <see cref="HostileColorIndex"/>.
    /// </remarks>
    public const int WreckColorIndex = 4;

    /// <summary>PORT ADDITION — the rings, the scale bar and the north arrow: palette <b>0</b>.</summary>
    /// <remarks>Black on the green ground is the same contrast the original's border line uses.</remarks>
    public const int FurnitureColorIndex = 0;

    /// <summary>PORT ADDITION — the map's caption text: palette <b>15</b>.</summary>
    public const int CaptionColorIndex = 15;

    // ------------------------------------------------------------------ the 1991 marker geometry

    /// <summary>
    /// The player cross's source vector, before rotation: <c>(0, 30)</c>.
    /// </summary>
    /// <remarks>
    /// <c>image@0x1E882</c>: <c>mov [bp−8],0</c>; <c>image@0x1E887</c>: <c>mov [bp−6],0x1E</c>.  It is
    /// then rotated by the NEGATED camera heading (<c>mov cx,es:[bx+0x12] / neg cx</c>
    /// @<c>image@0x1E898</c>) through <c>angle_vec2_rotate_inplace @image@0x184DE</c>.
    /// </remarks>
    public const double CrossVectorLength = 30.0;

    /// <summary>
    /// The long arm's far end: <c>v / 4</c> (<c>sar 2</c>, <c>image@0x1E8D5..0x1E8DA</c>).
    /// </summary>
    public const double CrossTailFraction = 1.0 / 4.0;

    /// <summary>
    /// The long arm's near end: <c>−v / 16</c> (<c>sar 4</c> of the negated vector,
    /// <c>image@0x1E8A4..0x1E8B7</c>).
    /// </summary>
    public const double CrossNoseFraction = 1.0 / 16.0;

    /// <summary>
    /// The cross-bar's half length: <c>v / 8</c> (<c>sar 3</c>, <c>image@0x1E8BC..0x1E8D2</c>),
    /// perpendicular to the arm.
    /// </summary>
    public const double CrossBarFraction = 1.0 / 8.0;

    /// <summary>The waypoint dot: 5 columns wide.</summary>
    /// <remarks><c>gfx_filled_scanline_rect(y−1, x−1, 5, 7, 0xFF0F)</c> @<c>image@0x1EA14</c>.</remarks>
    public const int WaypointDotWidth = 5;

    /// <summary>The waypoint dot: 7 rows tall (the same call).</summary>
    public const int WaypointDotHeight = 7;

    /// <summary>
    /// How far right of the dot the name starts: 9 design pixels.
    /// </summary>
    /// <remarks>
    /// The fit test at <c>image@0x1EA55</c> is <c>x + 4·len + 9 &lt; [0xE62A]</c>, and the screenshot
    /// has the dot's projected column at 133 and "Home Base" starting at 142.
    /// </remarks>
    public const int WaypointLabelGap = 9;

    /// <summary>The colour a scenery class draws in on the map, or −1 to use the mesh's own.</summary>
    /// <param name="basename">The mesh's basename.</param>
    /// <returns>A palette index, or −1.</returns>
    /// <remarks>
    /// The two classes the ORIGINAL's map draws take the colours the original's map gives them — which
    /// are NOT the classes' 3-D colours but the map's own two-entry table <c>g_map_course_color_table
    /// [0x2F90]</c>.  Everything else is a port addition and takes the colour its own geometry
    /// carries, so a city looks like a city.
    /// </remarks>
    public static int CourseColorIndex(string basename) => basename switch
    {
        "road" => RoadColorIndex,
        "river" => RiverColorIndex,
        _ => -1,
    };
}
