namespace CYAC.Port.Core.Model.World;

/// <summary>
/// The <c>sun</c> object — one white disc the engine keeps 100 world units directly above the view
/// anchor, for the whole session.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who instances it.</b> The sibling spawn of the ground grid, in <c>scene_or_mission_state_reset
/// @image@0x0C461</c>: a zeroed 24-byte template whose class word is set to <c>0x9F4E</c> — the
/// <c>sun</c> registry slot (<c>data/exe/meshes/sun.json</c> <c>slot.dgroup</c>, registry entry 57) —
/// handed to <c>pool_insert_with_bbox_or @image@0x153A1</c>, with the result stored in
/// <c>g_alloc_slot_C3FD_a [0x471C]</c>.  Exactly ONE object, once per session.
/// </para>
/// <para>
/// <b>Who moves it.</b> <c>alloc_slot_a_camera_pos_update @image@0x2DC9D</c>, once a frame, whose
/// whole body is thirteen instructions:
/// </para>
/// <code>
/// 2DC9D  ax:dx := [0xD88E]:[0xD890]      ; s_view_anchor X
/// 2DCA4  les bx,[0x471C]
/// 2DCA8  obj[+6]/[+8]  := X
/// 2DCB0  ax:dx := [0xD892]:[0xD894]      ; s_view_anchor Y
/// 2DCB7 add ax,0x6400 / adc dx,0; +0x6400 position units = +100 WORLD units
/// 2DCBD  obj[+0xA]/[+0xC] := Y + 100
/// 2DCC5  ax:dx := [0xD896]:[0xD898]      ; s_view_anchor Z
/// 2DCCC  obj[+0xE]/[+0x10] := Z
/// 2DCD4  retf
/// </code>
/// <para>
/// </para>
/// <para>
/// <b>What it looks like.</b> <c>sun</c>'s LOD0 is a SINGLE shape record — <c>{opcode 3 disc, colour
/// 15 (white), selector 0xFF (solid), radius 8, vertex 0}</c> over the one vertex <c>(0,0,0)</c> — at
/// <c>scaleShiftExponent 0</c> and <c>renderLayerPriority 0x05</c>, the LOWEST of any shipped class,
/// so everything paints over it.  At 100 world units it subtends <c>2·atan(8/100)</c> = <b>9.15°</b>,
/// which is a fat disc when you look up and nothing at all in level flight — which is why it took
/// until now to notice it was missing.
/// </para>
/// <para>
/// There is nothing left to get wrong here: the
/// position rule is thirteen instructions with no branches and the mesh is one record.  It follows
/// the CAMERA, not the aircraft, so in the external view it stays over the camera exactly as the
/// original's does — the anchor IS the camera.
/// </para>
/// </remarks>
public static class SunDisc
{
    /// <summary>The mesh the sun draws — registry slot 57, DGROUP <c>0x9F4E</c>.</summary>
    public const string MeshBasename = "sun";

    /// <summary>
    /// How far above the view anchor the sun sits, in world units: <b>100</b>.
    /// </summary>
    /// <remarks>
    /// <c>add ax,0x6400 / adc dx,0</c> @<c>image@0x2DCB7</c> on the anchor's Y <c>i32</c>, and a
    /// position unit is 1/256 world unit: <c>0x6400 / 256 = 100</c>.
    /// </remarks>
    public const int HeightAboveAnchorWorldUnits = 0x6400 >> 8;

    /// <summary>Where the sun object sits this frame, in world units.</summary>
    /// <param name="anchorXWorldUnits">The view anchor's X.</param>
    /// <param name="anchorYWorldUnits">The view anchor's Y.</param>
    /// <param name="anchorZWorldUnits">The view anchor's Z.</param>
    public static (double X, double Y, double Z) Position(
        double anchorXWorldUnits, double anchorYWorldUnits, double anchorZWorldUnits) =>
        (anchorXWorldUnits, anchorYWorldUnits + HeightAboveAnchorWorldUnits, anchorZWorldUnits);
}
