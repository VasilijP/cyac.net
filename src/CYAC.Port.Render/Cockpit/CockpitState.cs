namespace CYAC.Port.Render.Cockpit;

/// <summary>How the cockpit art is filtered when it is scaled to the window.</summary>
public enum CockpitFilter
{
    /// <summary>Whole-pixel nearest neighbour — the 1991 pixels, enlarged.</summary>
    Nearest = 0,

    /// <summary>Bilinear — the default: the art was drawn for a 1.2:1 CRT and reads better soft.</summary>
    Smooth = 1,
}

/// <summary>How the 320×200 design space is mapped onto the window.</summary>
public enum CockpitFit
{
    /// <summary>The panel fills the window on both axes (non-uniform).  The default.</summary>
    Stretch = 0,

    /// <summary>
    /// The panel keeps its 320:200 aspect and is pillar-boxed; the 3-D viewport still spans the whole
    /// window width, so the world is never letter-boxed.
    /// </summary>
    Uniform = 1,
}

/// <summary>The knobs the host gives the cockpit layer.</summary>
/// <param name="Enabled">
/// Whether the cockpit is drawn at all — the player's own Backspace toggle,
/// <c>g_cockpit_visible_flag [0xE471]</c>.
/// </param>
/// <param name="Filter">How the art is resampled.</param>
/// <param name="Fit">How the design space maps onto the window.</param>
/// <param name="Needles">how dial needles are drawn.</param>
/// <param name="Window">how the artificial horizon's window is cut.</param>
/// <param name="Hud">how the HUD's vector marks are stroked.</param>
/// <param name="HudHalfStroke">the refined HUD strokes' half-width in design pixels (<c>--hud-stroke</c> /
/// 2).</param>
/// <param name="NeedlePivotNudgeX">
/// Where in the dialinit pivot PIXEL the needle turns, in design pixels.  The original's
/// <c>dial_slot_needle_line_draw @image@0x0195C</c> pushes <c>slot[+0x08]/[+0x0A]</c> as a pixel ADDRESS and its line
/// filler paints that whole pixel, whose centre is <c>+0.5</c>; H19 drew the hub at the pixel's top-left CORNER, half
/// a design pixel up and left of the original's needle root (measured against
/// a captured frame of the original at 2×: the red root pixel IS the pivot pixel, the bitmap's white hub sits
/// to its right and below).  <c>--needle-nudge</c>.
/// </param>
/// <param name="NeedlePivotNudgeY">The same, along Y.</param>
/// <param name="HitMarkerAlphaCentre">
/// The canopy bullet-hole decals' opacity at their CENTRE (0…1).  The original blits the 48×38 decal opaque and never
/// clears it (<c>hud_damage_indicator_ring_draw @image@0x0CD75</c>), so two hits can wall off the forward view; the
/// port draws them halo-like instead: at least 50 % alpha, far edge almost transparent, centre almost opaque.  The opacity falls
/// linearly with the distance from the decal's centre to <see cref="HitMarkerAlphaEdge"/> at its corners.
/// <c>--hit-marker-alpha</c>.
/// </param>
/// <param name="HitMarkerAlphaEdge">The decals' opacity at their far edge (0…1).</param>
/// <param name="DesignatorLabels">
/// A PORT ADDITION — whether the IN-WORLD DESIGNATOR LABELS (the target's type and the chance-to-hit, under the box
/// on every qualifying aircraft) are drawn at all.  The original has no such switch; it is a port control, because at
/// a modern resolution a furball can carry a lot of text the 1991 screen never had room for.
/// </param>
/// <param name="DesignatorLabelScale">
/// Host pixels per FONT pixel for those labels.  1:1 in pixel terms is the default — scaling the
/// letters larger is suboptimal, because the port is not recreating 320×200 at 4K; 2
/// or 3 suit a 4K screen viewed from a distance.  The label's ANCHOR still comes from the design-space projection, so
/// it keeps riding its target.
/// </param>
/// <param name="OverlayWindowScale">
/// A PORT ADDITION — host pixels per DESIGN pixel for the four in-flight overlay windows.  A window's
/// frame is a frame, not a picture, so enlarging it to match the original reads as too thick.  0 is AUTO (a
/// quarter of the design scale, floored at 1 — the ESC menu's own rule, so 1 at 1080p and 2 at 4K); 1 is a strict
/// 1:1.  The window's PLACE still comes from the panel's own mapping, so it stays in its corner.
/// </param>
/// <param name="DesignatorOpacity">
/// How opaque those labels are, 0…1.  1 is solid, which is what the original draws.
/// </param>
public readonly record struct CockpitOptions(
    bool Enabled = true,
    CockpitFilter Filter = CockpitFilter.Smooth,
    CockpitFit Fit = CockpitFit.Stretch,
    NeedleStyle Needles = NeedleStyle.Tapered,
    InstrumentWindow Window = InstrumentWindow.Analytic,
    HudStyle Hud = HudStyle.Refined,
    double HudHalfStroke = 0.32,
    double NeedlePivotNudgeX = 0.5,
    double NeedlePivotNudgeY = 0.5,
    double HitMarkerAlphaCentre = 0.9,
    double HitMarkerAlphaEdge = 0.1,
    bool DesignatorLabels = true,
    int DesignatorLabelScale = 1,
    double DesignatorOpacity = 0.33,
    int OverlayWindowScale = 0);

/// <summary>
/// How a dial needle is drawn.  The original draws every needle as a ONE-PIXEL line from the slot's
/// pivot to its tip (<c>dial_slot_needle_line_draw @image@0x0195C</c>); at host resolution that line
/// is a hair, so the port can REPRESENT it as a needle.
/// </summary>
public enum NeedleStyle
{
    /// <summary>A constant-width anti-aliased line (the H10a look).</summary>
    Line = 0,

    /// <summary>A tapered needle — wide at the hub, a point at the tip — over a hub disc.</summary>
    Tapered = 1,
}

/// <summary>
/// How the artificial horizon's round window is cut.  The aircraft's <c>_horiz</c> mask is a 320×200
/// bitmap, so its circle is a staircase at host resolution; the analytic window fits a circle to the
/// mask's clear pixels and anti-aliases its rim.
/// </summary>
public enum InstrumentWindow
{
    /// <summary>The 1991 mask bitmap, pixel for pixel.</summary>
    Bitmap = 0,

    /// <summary>A circle fitted to the mask, anti-aliased at window resolution (falls back to the bitmap when the mask is not round).</summary>
    Analytic = 1,
}

/// <summary>
/// How the HUD's vector marks (pipper, lead dots, waterline, target box, lock diamond) are drawn.
/// The GEOMETRY is the original's in both styles (<c>hud_target_lock_logic @image@0x0D0A0</c>,
/// <c>hud_per_frame_draw @image@0x0C9FD</c>); only the stroke changes.
/// </summary>
public enum HudStyle
{
    /// <summary>The 1991 pixel runs, scaled (the H10b look).</summary>
    Classic = 0,

    /// <summary>Anti-aliased strokes at window resolution: a ring pipper with round lead dots, capsule waterline bars, a stroked box and diamond.</summary>
    Refined = 1,
}

// An earlier placeholder plotted every engagement over the full 360° by bearing
// and slant range, and coloured the locked one.  The original's scopes are a top-down ground-plane
// plot with the own ship at the region's pivot and NO IFF; the type that carries it is ScopeContact in
// RadarScope.cs, whose fields are the projector's own quantities.

/// <summary>
/// Everything the cockpit layer reads about one frame.  A value type of integers built by the host after
/// the last simulation step: the renderer can no more write into the simulation than it can see it.
/// </summary>
/// <param name="ViewId">
/// The current view's id in the original's own numbering.  The panel is painted for view 0 alone —
/// <c>[0xE46F]</c>, bit 0 of the per-view flag byte <c>[0x2BC0 + view]</c>, is set on the forward
/// cockpit view and no other (<c>image@0x010B4</c>).
/// </param>
/// <param name="Crashed">
/// <c>[0xC32F] != 0</c> — the augured-in flag, which also suppresses the panel
/// (<c>image@0x010BB</c>).
/// </param>
/// <param name="StatusFlags">
/// <c>master[+0x124]</c> as the cockpit regions see it through <c>g_input_state_bitfield [0xF0BC]</c>:
/// bit 0 afterburner, bit 1 flaps, bit 2 gear, bit 3 brake (the four region state functions that read
/// exactly those bits).
/// </param>
/// <param name="PitchBam">The player's pitch, ⅛-degree BAM — the artificial horizon's ladder.</param>
/// <param name="RollBam">The player's roll, ⅛-degree BAM.</param>
/// <param name="HeadingBam">The player's compass heading, ⅛-degree BAM.</param>
/// <param name="AltitudeFeet">Altitude in whole feet — the altimeter's value before the modulo.</param>
/// <param name="VerticalSpeedMidWord"><c>g_vertical_speed_mid_i16 [0xF1C1]</c>, the VSI's value.</param>
/// <param name="AirspeedFps">The player object's TAS at <c>+0x26</c>, the airspeed dial's value.</param>
/// <param name="FuelMidWord">The fuel level's mid word <c>[0xF059]</c>, the fuel gauge's value.</param>
/// <param name="ThrottlePercent">
/// Slot 5's value: <c>[0xF035]</c> (<c>image@0x020F6</c>), which the HUD prints as <c>"THR: %3d%%"</c>
/// (<c>image@0x0C728</c>, H10b F10) — the THROTTLE PERCENT, 0..100, not an engagement flag.  The port fed
/// the combat register <c>[0xF035]</c>, which nothing in the port writes, so the gauge never moved; the
/// aircraft's own throttle target <c>master[+0xA0] &gt;&gt; 8</c> is the quantity the original's HUD
/// prints from the same word.
/// </param>
/// <param name="CompassBam">
/// Slot 3's FIRST needle.  Every aircraft but the F-86 is fed its own heading (<c>image@0x0208F</c>); the
/// F-86 is fed the ABSOLUTE bearing to the current nav slot (<c>image@0x0206C..0x02077</c>,
/// <c>object_bearing_to_slot_compute(AL = 0)</c> then <c>angle_wrap_0_to_0xB40</c>), with the heading on
/// its SECOND needle.
/// </param>
/// <param name="BearingPointerBam">
/// Slot 4 — the RELATIVE bearing to the current nav slot, <c>(−1440, +1440]</c>
/// (<c>image@0x020A0..0x020AF</c>, <c>object_bearing_to_slot_compute(AL = 1)</c>).
/// </param>
/// <param name="PercentMeterA"><c>g_engagement_pct_meter_a [0xF1D8]</c>.</param>
/// <param name="PercentMeterB"><c>g_engagement_pct_meter_b [0xF1D9]</c>.</param>
/// <param name="PercentMeterC"><c>g_engagement_pct_meter_c [0xF1DA]</c>.</param>
/// <param name="WeaponRounds">
/// The ROUNDS left in the selected weapon slot — <c>g_hud_weapon_slot_ammo [0xED2C + 2·slot]</c>,
/// which region 6's state function reads as <c>[bx − 0x12D4]</c>.  −1 draws no readout.
/// </param>
/// <param name="WeaponName">The selected weapon's name, for the non-F-86 <c>"%4s %4d"</c> format.</param>
/// <param name="ChaffCount"><c>[0xED32]</c>.</param>
/// <param name="FlareCount"><c>[0xED33]</c>.</param>
/// <param name="Radar">
/// The RADAR's live state: the <b>R</b> switch, the CRT sweep clock and the contacts the monitor and the
/// RWR plot.  <see cref="RadarState.None"/> for an aircraft without either instrument.
/// </param>
public readonly record struct CockpitState(
    int ViewId,
    bool Crashed,
    byte StatusFlags,
    int PitchBam,
    int RollBam,
    int HeadingBam,
    int AltitudeFeet,
    int VerticalSpeedMidWord,
    int AirspeedFps,
    int FuelMidWord,
    int ThrottlePercent,
    int CompassBam,
    int BearingPointerBam,
    int PercentMeterA,
    int PercentMeterB,
    int PercentMeterC,
    int WeaponRounds,
    string? WeaponName,
    int ChaffCount,
    int FlareCount,
    RadarState Radar)
{
    /// <summary>Bit 0 of the status word — the afterburner (region 9).</summary>
    public bool Afterburner => (StatusFlags & 0x01) != 0;

    /// <summary>Bit 1 — the flaps (region 3).</summary>
    public bool FlapsDown => (StatusFlags & 0x02) != 0;

    /// <summary>Bit 2 — the landing gear (regions 3 and 4).</summary>
    public bool GearDown => (StatusFlags & 0x04) != 0;

    /// <summary>Bit 3 — the wheel brake (region 5).</summary>
    public bool BrakeOn => (StatusFlags & 0x08) != 0;

    /// <summary>
    /// The value one dial slot is fed, by the slot's role — <c>cockpit_dial_state_compute_all
    /// @image@0x01FCF</c> phases 3..13.
    /// </summary>
    /// <param name="slot">The slot index 0..9.</param>
    public int DialValue(int slot) => slot switch
    {
        // image@0x01FED — the altimeter needle shows the REMAINDER of a divide by 1,000, and the
        // thousands digit is the slot's separate numeric text.
        0 => ((AltitudeFeet % 1000) + 1000) % 1000,
        1 => VerticalSpeedMidWord,
        2 => AirspeedFps,
        3 => CompassBam,   // the F-86 feeds its first needle the BEARING instead — see DialSecondary
        4 => BearingPointerBam,
        5 => ThrottlePercent,
        6 => FuelMidWord,
        7 => PercentMeterA,
        8 => PercentMeterB,
        9 => PercentMeterC,
        _ => 0,
    };

    /// <summary>
    /// The SECOND needle's value on a slot that has two — <c>dial_slot_value_compute</c>'s
    /// <c>[bp+4]</c>.
    /// </summary>
    /// <param name="slot">The slot index 0..9.</param>
    /// <remarks>
    /// Only one shipped slot has two needles: the F-86's slot 3 (<c>keyframeCount = 2</c>,
    /// <c>kindWord 0x0C04</c>).  <c>cockpit_dial_state_compute_all</c>'s phase 6
    /// (<c>image@0x02065..0x02096</c>) passes the F-86 <c>(slot, absolute bearing, heading)</c> and
    /// everyone else <c>(slot, heading, 0)</c>, so the second needle is the aircraft's HEADING while
    /// the first points at the bearing.  Every other slot is called with a zero secondary.
    /// </remarks>
    public int DialSecondary(int slot) => slot == 3 ? HeadingBam : 0;

    /// <summary>The altimeter's thousands digit, which the original draws as text beside the needle.</summary>
    public int AltitudeThousands => AltitudeFeet / 1000;
}
