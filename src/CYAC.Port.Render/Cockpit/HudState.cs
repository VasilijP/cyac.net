using CYAC.Port.Core.Model.Cockpit;

namespace CYAC.Port.Render.Cockpit;

/// <summary>
/// One marker the HUD's <c>0x400</c> arm draws over the world, already projected into the game's 320×200
/// design space by the host.
/// </summary>
/// <param name="X">Its design column.</param>
/// <param name="Y">Its design row.</param>
/// <param name="Locked">
/// For the target box: whether the lock is confirmed, which is what adds the diamond
/// (<c>image@0x0CA72</c> sets <c>g_target_lock_phase [0xF1B7] = 2</c> first).
/// </param>
/// <param name="LeadDots">
/// For the pipper: how many of the four lead dots are lit, 0..4 — the original's
/// <c>reach &gt; dist</c>, <c>·3/4</c>, <c>·1/2</c>, <c>·1/4</c> ladder (<c>image@0x0D24D..0x0D2B6</c>).
/// </param>
public readonly record struct HudMarker(double X, double Y, bool Locked, int LeadDots);

/// <summary>
/// One IN-WORLD TARGET DESIGNATOR label: an engagement object's type and the player's chance of
/// hitting it, drawn under its projected screen position.
/// </summary>
/// <remarks>
/// <para>
/// <c>hud_engagement_label_draw @image@0x0CDB4</c> walks the engagement list and draws two rows for
/// every object that passes its four guards, at <c>screen_y + 9</c> and <c>screen_y + 15</c>
/// (<c>image@0x0CE74</c> / <c>image@0x0CE9A</c>), each left-aligned from
/// <c>screen_x − (2·len + 1)</c> by its private helper at <c>image@0x0CEE8</c>.
/// </para>
/// <para>
/// It is INDEPENDENT of the target selection: the label is drawn before <c>Enter</c> is ever pressed
/// (a captured frame of the original shows <c>MiG-21MF</c> and
/// <c>81%</c> with no box and no window).  Only the yellow BOX (<see cref="HudState.TargetMarker"/>,
/// <c>hud_target_screen_marker_draw @image@0x1EC10</c>) needs the key.
/// </para>
/// </remarks>
/// <param name="X">The object's projected design column.</param>
/// <param name="Y">Its projected design row.</param>
/// <param name="TypeName">Its type, from the engagement prototype's <c>+0x04</c> name pointer.</param>
/// <param name="HitPercent">The chance-to-hit, 0..100, or −1 to draw no percentage row.</param>
/// <param name="EngagingPlayer">
/// Whether the object is engaging the PLAYER — <c>block[+0x1B] == g_alt_object_farptr_off [0x00C0]</c>
/// (<c>image@0x0CE32</c>), which colours the label palette 12 instead of palette 0.
/// </param>
/// <param name="Selected">
/// Whether this is the object the target keys SELECTED, which gets the yellow
/// <see cref="HudRenderer.DesignatorBoxSize"/> box — <c>hud_target_screen_marker_draw
/// @image@0x1EC10</c>, the <c>0x100</c> widget arm's own marker.
/// </param>
public readonly record struct HudDesignator(
    double X, double Y, string TypeName, int HitPercent, bool EngagingPlayer, bool Selected = false);

/// <summary>
/// Everything the HUD overlay reads about one frame.  Integers and strings built by the host after the last
/// simulation step; the renderer never reaches into the simulation.
/// </summary>
/// <param name="Suppressed">
/// <c>g_input_mode_byte [0xC32F] != 0</c> — a menu or dialog is up and <c>hud_per_frame_draw</c>
/// returns at once (<c>image@0x0C5AF</c>).
/// </param>
/// <param name="ForwardView">
/// <c>g_view_mode_bit0_flag [0xE46F]</c> — whether this is the forward view.  It selects the mask
/// (<see cref="HudMask"/>) and gates the message strip's companion, the hit-marker pass.
/// </param>
/// <param name="FlightInfoVisible"><c>g_flight_info_visible [0xB0]</c>, the Ctrl-F toggle.</param>
/// <param name="CockpitDrawn">
/// <c>[0xE472]</c> — whether the panel is painted this frame, which is what chooses the per-aircraft
/// HUD layout block over the full-screen default (<c>image@0x0C5B9</c>).
/// </param>
/// <param name="AltitudeFeet">The altitude the readout prints, clamped at zero upstream.</param>
/// <param name="AirspeedFps">The player object's TAS at <c>+0x26</c>, in feet per second.</param>
/// <param name="GLoadQ8"><c>g_player_gload_q8 [0xF06E]</c>.</param>
/// <param name="VerticalSpeed"><c>g_vertical_speed_i32 [0xF1C0]</c>.</param>
/// <param name="ThrottlePercent"><c>[0xF035]</c>, printed as <c>"%3d%%"</c> after THR/AFT.</param>
/// <param name="HeadingBam">The player object's <c>+0x12</c> heading, ⅛-degree BAM.</param>
/// <param name="StatusFlags"><c>g_input_state_bitfield [0xF0BC]</c>: bit 0 AB, 1 flaps, 2 gear, 3 brake.</param>
/// <param name="LandingReady">
/// Whether the VSI line gets its three-star landing cue — the aircraft is below the altitude ceiling
/// the arm tests, its gear is down and <c>crash_conditions_valid</c> says a touchdown would be
/// survivable (<c>image@0x0C76C..0x0C78A</c>).
/// </param>
/// <param name="ZoomLevel"><c>g_camera_zoom_level [0xD8A0]</c>, 7..12.</param>
/// <param name="TimeCompressionShift"><c>g_scene_tick_shift [0xF104]</c>, 0/1/2.</param>
/// <param name="WeaponName">The selected weapon's name, or null for none.</param>
/// <param name="WeaponRounds">Its rounds, or −1 for none.</param>
/// <param name="HitPercent">The chance-to-hit suffix's value, or −1 when there is no target.</param>
/// <param name="Message">The message strip's text, or null when nothing is up.</param>
/// <param name="MessageX">
/// Its design column, or −1 to centre it the way the built-in stall messages are centred:
/// <c>x = (0x50 − len)·2</c> (<c>image@0x0CCE0</c>).
/// </param>
/// <param name="TargetMarker">
/// The guided-weapon marker: a 15 × 15 box, plus a diamond when the lock is confirmed.  Null when the
/// selected weapon is a gun or nothing is being tracked.
/// </param>
/// <param name="Pipper">The gun pipper, or null when the selected weapon is guided.</param>
/// <param name="HitMarkers">Where the bullet-hole decals sit, at most two.</param>
/// <param name="Designators">
/// The IN-WORLD TARGET DESIGNATOR labels: one per qualifying engagement object (<see cref="HudDesignator"/>).
/// Null or empty draws none.
/// </param>
/// <param name="OverlayCoversTopLeft">
/// Whether an in-flight overlay window holds the first slot, which suppresses the HUD's top-left block rather
/// than overdrawing it (<see cref="HudMask.WithoutCoveredBlocks"/>).
/// </param>
/// <param name="OverlayCoversTopRight">
/// Whether the TARGET window is up, which suppresses the top-right block the same way.
/// </param>
public readonly record struct HudState(
    bool Suppressed,
    bool ForwardView,
    bool FlightInfoVisible,
    bool CockpitDrawn,
    int AltitudeFeet,
    int AirspeedFps,
    int GLoadQ8,
    int VerticalSpeed,
    int ThrottlePercent,
    int HeadingBam,
    byte StatusFlags,
    bool LandingReady,
    int ZoomLevel,
    int TimeCompressionShift,
    string? WeaponName,
    int WeaponRounds,
    int HitPercent,
    string? Message,
    int MessageX,
    HudMarker? TargetMarker,
    HudMarker? Pipper,
    IReadOnlyList<PanelPoint>? HitMarkers,
    IReadOnlyList<HudDesignator>? Designators = null,
    bool OverlayCoversTopLeft = false,
    bool OverlayCoversTopRight = false)
{
    /// <summary>Bit 0 of the status word — the afterburner, which relabels THR as AFT.</summary>
    public bool Afterburner => (StatusFlags & 0x01) != 0;

    /// <summary>Bit 1 — the flaps.</summary>
    public bool FlapsDown => (StatusFlags & 0x02) != 0;

    /// <summary>Bit 2 — the landing gear.</summary>
    public bool GearDown => (StatusFlags & 0x04) != 0;

    /// <summary>Bit 3 — the wheel brake.</summary>
    public bool BrakeOn => (StatusFlags & 0x08) != 0;

    /// <summary>Which widgets this frame draws.</summary>
    public HudWidget Widgets => HudMask.WithoutCoveredBlocks(
        HudMask.For(ForwardView, FlightInfoVisible), OverlayCoversTopLeft, OverlayCoversTopRight);
}

/// <summary>
/// A masked HUD decal: the <c>bullet.pic</c> / <c>bulletm.msk</c> pair the damage pass stamps on the
/// canopy.
/// </summary>
/// <param name="Width">Its width in design pixels — 48 for the shipped decal.</param>
/// <param name="Height">Its height — 38.</param>
/// <param name="Indices">Its palette indices, row major.</param>
/// <param name="Opaque">Its mask: true where the decal paints.</param>
/// <remarks>
/// <c>hud_damage_indicator_ring_draw @image@0x0CD75</c> blits it through <c>gfx_masked_blit
/// @image@0x1D162</c> at <c>0x30 × 0x26</c> from the descriptor <c>[0x31C6]</c>, which
/// <c>image@0x0CD03..0x0CD22</c> fills with the assets named at DGROUP <c>[0x31AE] = "bullet.pic"</c>
/// and <c>[0x31B9] = "bulletm.msk"</c>.
/// </remarks>
public sealed record HudDecal(int Width, int Height, byte[] Indices, bool[] Opaque)
{
    /// <summary>A palette index inside the decal, or −1 where the mask is clear.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    public int At(int x, int y) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height && Opaque[(y * Width) + x]
            ? Indices[(y * Width) + x]
            : -1;
}
