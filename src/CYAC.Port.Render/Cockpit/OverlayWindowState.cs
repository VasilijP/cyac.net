using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.Mission;

namespace CYAC.Port.Render.Cockpit;

/// <summary>
/// What the host feeds the four in-flight overlay windows.
/// </summary>
/// <remarks>
/// <para>
/// One record for all four, because they share a chrome and a visibility byte
/// (<c>g_inflight_overlay_visibility [0xF1CB]</c>).  Another part of the port owns the frame, the title band and the
/// footer band; the CONTENT of each window is a slice of its own (W1 radar, W2 target, W3 map,
/// W4 envelope, W5 Yeager) and each adds the fields it needs here.
/// </para>
/// <para>
/// Every string is composed host-side, from the data tree and the live kernel — this assembly never
/// formats a shipped literal of its own.
/// </para>
/// </remarks>
/// <param name="Visible">
/// The live <c>[0xF1CB]</c> mask.  <see cref="CockpitOverlayFlags.None"/> draws nothing.
/// </param>
/// <param name="TargetPresent">
/// Whether there IS a target for the TARGET window — the original's second gate on bit 1, the
/// <c>lcall 0x108e:0x9c77</c> at <c>image@0x0EAB1</c> whose <c>AL</c> must be non-zero
/// (<c>target_in_range_view_check @image@0x0A557</c>).  With a lock but no target the window is not
/// drawn at all, which is what <c>cfg 0x02</c> measures before and after the target key
/// (<c>20_mig21_cfg02_notarget.png</c> has no window, <c>21_mig21_cfg02_target.png</c> has one).
/// </param>
/// <param name="TargetTitle">
/// The TARGET window's title tail — the target's type, drawn after <c>TARGET</c>
/// (a captured frame of the original: <c>TARGET  MiG-21MF</c>).
/// </param>
/// <param name="TargetPanel">
/// The TARGET window's CONTENTS: the manoeuvre, the lock state, the clock, the speed, the range and
/// the weapon-station ladder.  <see cref="TargetPanelState"/> carries them and
/// <see cref="OverlayWindowRenderer.RenderTargetContents"/> draws them, after the host has rendered
/// the silhouette into the same rectangle.
/// </param>
/// <param name="MapFooter">The MAP window's footer — its zoom, e.g. <c>x4</c>.</param>
/// <param name="MapPanel">
/// The MAP window's CONTENTS: the live scale shift and the contacts the shared pixel-obj engine
/// would plot inside the 64 × 48 face.  <see cref="OverlayWindowRenderer.RenderMapContents"/> draws
/// them.
/// </param>
/// <param name="EnvelopeFooter">
/// The ENVELOPE window's footer — the two live numbers under the plot, e.g. <c>1 G   2645 FT</c>.
/// </param>
/// <param name="EnvelopePanel">
/// The ENVELOPE window's CONTENTS: the flight-envelope curve the current load factor selects, the
/// aeroplane's own place on it, and the blink phase.
/// <see cref="OverlayWindowRenderer.RenderEnvelopeContents"/> draws them.
/// </param>
/// <param name="AdvisorPanel">
/// The YEAGER window's CONTENTS: whether Chuck is speaking at all, his two lines and which of the
/// three portraits goes with them. <see cref="OverlayWindowRenderer.RenderAdvisorContents"/> draws
/// them.
/// </param>
public readonly record struct OverlayWindowState(
    CockpitOverlayFlags Visible,
    bool TargetPresent = false,
    string TargetTitle = "",
    TargetPanelState TargetPanel = default,
    string MapFooter = "",
    string EnvelopeFooter = "",
    MapPanelState MapPanel = default,
    EnvelopePanelState EnvelopePanel = default,
    AdvisorPanelState AdvisorPanel = default)
{
    /// <summary>
    /// Whether any window will be drawn this frame.
    /// </summary>
    /// <remarks>
    /// Closed by W5 — the bit draws a 72 × 70 window at design column 4 while, and only while, a
    /// message is inside its display window: <c>g_advisor_last_dispatch_time [0xBCB8] &lt;=
    /// g_frame_count_scaled [0xF0D0] &lt; g_advisor_next_allow_time [0xBCF2]</c>
    /// (<c>image@0x0EEF0..0x0EEFD</c>).  That is <see cref="AdvisorPanelState.Speaking"/>.  The
    /// TARGET bit still needs a target.
    /// </remarks>
    public bool Any =>
        Visible.HasFlag(CockpitOverlayFlags.Map)
        || Visible.HasFlag(CockpitOverlayFlags.Envelope)
        || (Visible.HasFlag(CockpitOverlayFlags.Yeager) && AdvisorPanel.Speaking)
        || (Visible.HasFlag(CockpitOverlayFlags.Target) && TargetPresent);

    /// <summary>Whether the YEAGER window is actually on the band this frame.</summary>
    public bool AdvisorUp =>
        Visible.HasFlag(CockpitOverlayFlags.Yeager) && AdvisorPanel.Speaking;

    /// <summary>
    /// The mask every drawer downstream of the advisor sees: <see cref="Visible"/> minus the
    /// window a speaking advisor takes off the band
    /// (<see cref="OverlayWindowLayout.EffectiveVisible"/>).
    /// </summary>
    public CockpitOverlayFlags EffectiveVisible =>
        OverlayWindowLayout.EffectiveVisible(Visible, AdvisorPanel.Speaking);
}

/// <summary>
/// What the YEAGER advisor window shows.
/// </summary>
/// <remarks>
/// The two lines are the far strings the message dispatch armed in
/// <c>[0xBCBA]/[0xBCBC]</c> and <c>[0xBCBE]/[0xBCC0]</c>, resolved host-side: where each code points
/// is <c>data/exe/tables/advisor.json</c>, the text is <c>data/exe/strings.json</c> — never retyped.
/// <see cref="DisplayType"/> is the tail's <c>[bp-2]</c>, which chooses the portrait.
/// </remarks>
/// <param name="Speaking">
/// Whether the message's display window is open — the drawer's own entry guard
/// (<c>image@0x0EEF0..0x0EEFD</c>).  False is the silent state every frame of the reference atlas
/// shows.
/// </param>
/// <param name="Line1">The UPPER line, or empty.</param>
/// <param name="Line2">The LOWER line, or empty when the message is a single line (only code 2).</param>
/// <param name="DisplayType">Which portrait the advisory carries.</param>
public readonly record struct AdvisorPanelState(
    bool Speaking,
    string Line1,
    string Line2,
    AdvisorDisplayType DisplayType)
{
    /// <summary>Chuck saying nothing — the state the whole reference atlas was captured in.</summary>
    public static AdvisorPanelState Silent =>
        new(false, string.Empty, string.Empty, AdvisorDisplayType.Standard);
}

/// <summary>
/// What the MAP window plots inside its face.
/// </summary>
/// <param name="ScaleShift">
/// <c>g_stipple_scale_shift [0x3234]</c>, the map's own zoom (9 at load, <c>image@0x00ABD</c>,
/// clamped 8..11 by the <c>,</c> / <c>.</c> keys).  One shift serves all three pixel-obj contexts, so
/// it is the same number <see cref="RadarState.ScaleShift"/> carries.
/// </param>
/// <param name="Contacts">
/// The frame's contacts — the SAME list the two scopes plot from
/// (<c>per_frame_object_pixel_setup</c> is one engine with three contexts), each carrying the map's
/// own colour class and altitude bit.  Null when there is no combat state.
/// </param>
public readonly record struct MapPanelState(
    int ScaleShift,
    IReadOnlyList<ScopeContact>? Contacts)
{
    /// <summary>A map with nothing to plot — still a face and an own-ship marker.</summary>
    public static MapPanelState None => new(MapWindow.DefaultScaleShift, null);
}

/// <summary>
/// What the ENVELOPE window plots inside its face.
/// </summary>
/// <remarks>
/// Everything the drawer at <c>image@0x0EB86</c> reads, in the port's own terms:
/// <see cref="EnvelopeWindow"/> holds the law that turns it into pixels.  <see cref="Curve"/> is the
/// aircraft's OWN <c>FlightModelEnvelopeRecord</c> — the port never re-derives the envelope, it
/// renders the one the integer kernel already flies (<c>data/aircraft/*.json</c>'s
/// <c>envelope</c>, itself <c>2b.lib/&lt;NAME&gt;.FME</c>).
/// </remarks>
/// <param name="Curve">
/// The record <c>fme_record_lookup_by_key(master, g_player_gload_int)</c> found, or
/// <see langword="null"/> when no curve matches the current load factor — in which case the original
/// draws <b>nothing at all</b> (<c>or dx,ax / jne</c>, <c>image@0x0EC29</c>) and the face keeps the
/// panel fill.
/// </param>
/// <param name="CornerX">
/// <c>g_cockpit_horizon_width_scale [0xF0BE]</c> = <c>master[+0x126]</c>: the largest airspeed among
/// the envelope's AUTHORED points, accumulated by <c>flight_envelope_load @image@0x2AA76</c>.
/// </param>
/// <param name="CornerY">
/// <c>g_cockpit_horizon_height_scale [0xF0C0]</c> = <c>master[+0x128]</c>: the same for altitude.
/// </param>
/// <param name="AirspeedFps">
/// <c>g_airspeed [0xEF99]</c> — the marker's x.
/// </param>
/// <param name="AltitudeQ8">
/// The player world object's <c>+0x0A</c> altitude in Q8 feet — the marker's y (shifted by 11).
/// </param>
/// <param name="RenderFrameCounter">
/// The scene render context's per-frame counter (<c>[0xC332]</c>); its parity is the marker's blink
/// phase — see <see cref="EnvelopeWindow.MarkerPaletteIndex"/>.
/// </param>
public readonly record struct EnvelopePanelState(
    EnvelopeCurve? Curve,
    int CornerX,
    int CornerY,
    int AirspeedFps,
    int AltitudeQ8,
    int RenderFrameCounter)
{
    /// <summary>An envelope window with no curve to draw — a grey face, exactly as the original.</summary>
    public static EnvelopePanelState None => new(null, 0, 0, 0, 0, 0);
}

/// <summary>What the overlay-window layer drew.</summary>
/// <param name="Windows">How many windows were framed.</param>
/// <param name="ChromePixels">Design pixels of chrome (frame plus shadow) painted.</param>
/// <param name="Texts">How many band strings were drawn.</param>
public readonly record struct OverlayWindowFrameStats(int Windows, int ChromePixels, int Texts);
