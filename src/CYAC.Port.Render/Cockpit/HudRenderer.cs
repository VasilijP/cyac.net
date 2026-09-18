using CYAC.Port.Core.Model.Cockpit;

namespace CYAC.Port.Render.Cockpit;

/// <summary>What one <see cref="HudRenderer.Render"/> drew — for tests and telemetry.</summary>
/// <param name="Mask">The widget mask the frame ran with.</param>
/// <param name="TextWidgets">How many text widgets printed something.</param>
/// <param name="MarkerPixels">Design pixels of marker geometry (waterline, box, diamond, pipper).</param>
/// <param name="DecalPixels">Design pixels of bullet-hole decal painted.</param>
/// <param name="MessageDrawn">Whether the message strip printed a line.</param>
public readonly record struct HudFrameStats(
    HudWidget Mask,
    int TextWidgets,
    int MarkerPixels,
    int DecalPixels,
    bool MessageDrawn);

/// <summary>
/// The HUD OVERLAY: <c>hud_per_frame_draw @image@0x0C5A7</c>'s eleven widgets, the message strip, the
/// canopy hit markers and the gunsight, drawn over everything else.
/// </summary>
/// <remarks>
/// <para>
/// The original draws this last of all, once per frame, from <c>image@0x01708</c>.  Its layout comes
/// from the 38-byte block it copies into <c>[0xF180..0xF1A5]</c> — the aircraft's own when the panel
/// is painted this frame, the full-screen default <c>[0x3078]</c> otherwise — and every anchor is a
/// coordinate in the same 320×200 design space the cockpit layer uses, so the whole overlay scales
/// with <see cref="CockpitScale"/> and no coordinate is re-authored.
/// </para>
/// <para>
/// <b>Colour.</b>  One constant: the VGA branch of the HUD colour init (<c>image@0x0C56D..0x0C57E</c>)
/// writes <c>[0xF1A6] = 0x0E</c>, <c>[0xF1AA] = 0xFF0E</c>, <c>[0xF1A8] = 0xFFFF</c>, so
/// <c>g_text_color = 0xFF0E</c> and <c>[0xE810] = 0</c> — palette 14 ink, no background fill, for
/// every widget and for the marker geometry alike.  (The <c>[0x015E] == 0</c> branch's white-on-fill
/// is the CGA one; the port is VGA-only.)
/// </para>
/// <para>
/// <b>Represent, don't reproduce.</b>  The text is the game's own 4×6 glyph strip drawn crisply at
/// host resolution; the waterline bars, the target box and the pipper are the original's own
/// one-design-pixel runs, so they stay hairline-thin relative to the screen instead of turning into
/// six-pixel slabs; only the lock diamond's diagonals are anti-aliased, because a 1991 Bresenham
/// diagonal enlarged six times is a staircase.
/// </para>
/// </remarks>
public sealed class HudRenderer
{
    /// <summary>
    /// The palette index every HUD element is drawn in — <c>g_text_color</c>'s and
    /// <c>g_hud_lock_box_color [0xF1AA]</c>'s shared low byte, <c>0x0E</c>.
    /// </summary>
    /// <remarks>
    /// <c>hud_lock_color_step_advance @image@0x0C58E</c> can walk it around the 16-entry ramp, but it
    /// is a KEY handler (the ladder arm at <c>image@0x012DC</c>, gated on <c>[0xC31C]</c>), not a
    /// per-frame animation; the port ships the default and does not bind the key <b>(open)</b>.
    /// </remarks>
    public const int InkPaletteIndex = 0x0E;

    /// <summary>The glyph advance of the <c>4x6</c> font, in design pixels.</summary>
    /// <remarks>
    /// <c>glyph_blit_dispatch_2 @image@0x1F1BC</c> centres a string on <c>2·len</c> and
    /// <c>image@0x1F4EC</c> sizes its background on <c>4·len</c> — four columns a character.
    /// </remarks>
    public const int GlyphAdvance = 4;

    /// <summary>Draws the overlay over a finished frame.</summary>
    /// <param name="target">The frame.</param>
    /// <param name="art">The aircraft's cockpit art — for its layout blocks, viewport and palette.</param>
    /// <param name="font">The game's own glyph strip.</param>
    /// <param name="state">The frame's HUD state.</param>
    /// <param name="options">The cockpit knobs, for the design-space mapping.</param>
    /// <param name="decal">The bullet-hole decal, or null when the tree does not carry it.</param>
    /// <returns>What was drawn.</returns>
    public HudFrameStats Render(
        PixelTarget target,
        CockpitArt art,
        CockpitFont font,
        in HudState state,
        in CockpitOptions options,
        HudDecal? decal)
    {
        ArgumentNullException.ThrowIfNull(art);
        ArgumentNullException.ThrowIfNull(font);

        // image@0x0C5AF — a menu or dialog is up: the whole function returns.
        if (state.Suppressed)
        {
            return new HudFrameStats(HudWidget.None, 0, 0, 0, false);
        }

        CockpitScale scale = CockpitScale.For(
            target.Width, target.Height, art.Viewport, options.Fit, state.CockpitDrawn);
        HudLayout layout = state.CockpitDrawn
            ? art.Layout.HudFor(art.AircraftIndex)
            : art.Layout.HudDefault;
        Rgb24 ink = art.Palette.Count > InkPaletteIndex
            ? art.Palette[InkPaletteIndex]
            : new Rgb24(255, 255, 85);

        // The clip rectangle the whole frame shares — the aircraft's viewport when the panel is
        // painted, the whole screen when it is not (image@0x015F3 / image@0x01641).  Its CENTRE is
        // what freecam_screen_project leaves in [0xF17A]/[0xF17C] on the zero-delta path, and
        // gfx_viewport_clip_rect_setup @image@0x11876 computes it as (min + max) >> 1 with
        // max = origin + extent − 1.
        PanelRect viewport = state.CockpitDrawn
            ? art.Viewport
            : new PanelRect(0, 0, CockpitLayout.DesignWidth, CockpitLayout.DesignHeight - 1);
        int centreX = (viewport.X + viewport.Right - 1) >> 1;
        int centreY = (viewport.Y + viewport.Bottom - 1) >> 1;

        int texts = 0;
        int markerPixels = 0;
        int decalPixels = 0;

        // §E — the message strip and the canopy hit markers run BEFORE the mask is computed, so they
        // survive even when the flight info is switched off (image@0x0C602..0x0C611).
        bool message = DrawMessage(target, font, scale, in state, layout, ink);
        if (state.ForwardView && decal is not null)
        {
            decalPixels = DrawHitMarkers(target, scale, art, in state, in options, decal);
        }

        // THE IN-WORLD TARGET DESIGNATOR's two label rows, drawn BEFORE the widget mask is
        // consulted.  They are not part of hud_per_frame_draw at all: hud_engagement_label_draw
        // @image@0x0CDB4 is called from the SCENE renderer (polygon_fill_mesh_render_setup
        // @image@0x14B9E), so no HUD mask bit and no target selection gates them — only each
        // object's own four guards, which the host applies.
        texts += DrawDesignators(target, font, scale, art, in state, in options);

        HudWidget mask = state.Widgets;
        if (mask == HudWidget.None)
        {
            return new HudFrameStats(mask, texts, 0, decalPixels, message);
        }

        // §1 ALTITUDE — image@0x0C649: (row = altY, x = altX − 0x24).
        if (mask.HasFlag(HudWidget.Altitude))
        {
            Text(target, font, scale, HudText.Altitude(art.Strings, state.AltitudeFeet),
                layout.AltitudeAnchor.X - 0x24, layout.AltitudeAnchor.Y, ink);
            texts++;
        }

        // §2 AIRSPEED and the G-load line under it — image@0x0C699.
        if (mask.HasFlag(HudWidget.Airspeed))
        {
            Text(target, font, scale, HudText.Airspeed(art.Strings, state.AirspeedFps),
                layout.AltitudeAnchor.X - 0x20, layout.StatusRow + 6, ink);
            Text(target, font, scale, HudText.GLoad(state.GLoadQ8),
                layout.AltitudeAnchor.X - 0x18, layout.StatusRow + 0xC, ink);
            texts += 2;
        }

        // §3 THROTTLE — image@0x0C722: (row = vsiY − 0xB, x = vsiX − 0x24).
        if (mask.HasFlag(HudWidget.Throttle))
        {
            Text(target, font, scale, HudText.Throttle(art.Strings, state.ThrottlePercent, state.Afterburner),
                layout.VsiAnchor.X - 0x24, layout.VsiAnchor.Y - 0xB, ink);
            texts++;
        }

        // §4 VERTICAL SPEED — image@0x0C763; the prefix chooses the x offset (0x38 / 0x28).
        if (mask.HasFlag(HudWidget.VerticalSpeed))
        {
            (string line, int offset) = HudText.VerticalSpeedLine(art.Strings, state.VerticalSpeed, state.LandingReady);
            Text(target, font, scale, line, layout.VsiAnchor.X - offset, layout.VsiAnchor.Y - 5, ink);
            texts++;
        }

        // §5 HEADING — image@0x0C7F8, centred by glyph_blit_dispatch_2's own rule.
        if (mask.HasFlag(HudWidget.Heading))
        {
            string heading = HudText.Heading(state.HeadingBam);
            int x = (centreX - (2 * heading.Length) + 1) & ~3;
            Text(target, font, scale, heading, x, layout.HeadingAnchorY, ink);
            texts++;
        }

        // §7 WEAPON + AMMO — image@0x0C871: (row = wptY − 5, x = wptX).
        if (mask.HasFlag(HudWidget.WeaponAmmo))
        {
            Text(
                target, font, scale,
                HudText.WeaponAmmo(art.Strings, state.WeaponName, state.WeaponRounds, state.HitPercent),
                layout.WaypointAnchor.X, layout.WaypointAnchor.Y - 5, ink);
            texts++;
        }

        // §8 the status stack — image@0x0C902: one column, three rows six scanlines apart; the three
        //   words are DGROUP [0x3058] / [0x305E] / [0x3064] (P4-R2: from the tree).
        if (mask.HasFlag(HudWidget.StatusText))
        {
            if (state.FlapsDown)
            {
                Text(target, font, scale, art.Strings.FlapsWord, layout.StatusColumn, layout.StatusRow, ink);
                texts++;
            }

            if (state.BrakeOn)
            {
                Text(target, font, scale, art.Strings.BrakeWord, layout.StatusColumn, layout.StatusRow + 6, ink);
                texts++;
            }

            if (state.GearDown)
            {
                Text(target, font, scale, art.Strings.GearWord, layout.StatusColumn, layout.StatusRow + 0xC, ink);
                texts++;
            }
        }

        // §9 ZOOM, and TIME above it while the clock is compressed — image@0x0C956.
        if (mask.HasFlag(HudWidget.Zoom))
        {
            Text(target, font, scale, HudText.Zoom(art.Strings, state.ZoomLevel),
                layout.WaypointAnchor.X, layout.WaypointAnchor.Y - 0xB, ink);
            texts++;
            string time = HudText.TimeCompression(art.Strings, state.TimeCompressionShift);
            if (time.Length > 0)
            {
                Text(target, font, scale, time,
                    layout.WaypointAnchor.X, layout.WaypointAnchor.Y - 0x11, ink);
                texts++;
            }
        }

        // §11 THE MARKER BLOCK — image@0x0C9E8.  Forward view only, and the waterline lives inside it.
        if (mask.HasFlag(HudWidget.Marker))
        {
            // The original's own gate makes these two EXCLUSIVE — `test byte [[0xED1E]+0x24], 0x10`
            // at image@0x0CA0B picks the box for a guided weapon and the pipper for a gun — and the
            // host applies it when it builds the state; the renderer draws what it is handed, which
            // is also what lets --hud-demo photograph both at once.
            bool refined = options.Hud == HudStyle.Refined;
            if (state.TargetMarker is { } box)
            {
                markerPixels += refined
                    ? DrawTargetBoxRefined(target, scale, box, ink, options.HudHalfStroke)
                    : DrawTargetBox(target, scale, box, ink, options.Filter);
            }

            if (state.Pipper is { } pipper)
            {
                markerPixels += refined
                    ? DrawPipperRefined(target, scale, pipper, ink, options.HudHalfStroke)
                    : DrawPipper(target, scale, pipper, ink, options.Filter);
            }

            markerPixels += refined
                ? DrawWaterlineRefined(target, scale, centreX, centreY, ink, options.HudHalfStroke)
                : DrawWaterline(target, scale, centreX, centreY, ink, options.Filter);
        }

        return new HudFrameStats(mask, texts, markerPixels, decalPixels, message);
    }

    /// <summary>
    /// The WATERLINE marker: two four-pixel bars either side of the viewport's centre.
    /// </summary>
    /// <remarks>
    /// <c>image@0x0CB09..0x0CB3E</c>: <c>gfx_hline_draw([0xF17A] − 6, [0xF17A] − 3, [0xF17C])</c> and
    /// <c>([0xF17A] + 3, [0xF17A] + 6, [0xF17C])</c>, both in <c>[0xF1AA]</c>.  The horizontal line
    /// drawer's ends are INCLUSIVE, so each bar is four design pixels.  It is unconditional inside
    /// the <c>0x400</c> arm, which is why the two props — whose lock/pipper block the
    /// <c>[0xC31A] &gt;= 2</c> guard skips — still get a gunsight of sorts.
    /// </remarks>
    /// <param name="filter">
    /// The panel's reconstruction filter: under <see cref="CockpitFilter.Smooth"/> the runs take a
    /// fractional edge instead of snapping to whole window pixels (audit row 14). The CLASSIC marks
    /// are one-design-pixel runs, so at 1080p each is a ~6-pixel-wide slab whose hard edge sat next
    /// to a coverage-ramped panel; the REFINED strokes were already analytic.
    /// </param>
    private static int DrawWaterline(
        PixelTarget target, CockpitScale scale, int centreX, int centreY, Rgb24 ink,
        CockpitFilter filter)
    {
        CockpitRenderer.FillDesignRect(
            target, scale, new PanelRect(centreX - 6, centreY, 4, 1), ink, filter);
        CockpitRenderer.FillDesignRect(
            target, scale, new PanelRect(centreX + 3, centreY, 4, 1), ink, filter);
        return 8;
    }

    /// <summary>
    /// The GUIDED-weapon marker: a 15 × 15 box outline and, on a confirmed lock, a diamond.
    /// </summary>
    /// <remarks>
    /// <c>image@0x0CA38..0x0CAF2</c>.  <c>gfx_rect_outline_draw @image@0x108BD</c> takes
    /// <c>(color, height, width, x_left, y_top)</c> in bp-ascending order — read off its own four
    /// edge calls — so the pushes make <c>x_left = [0xF1B2] − 7</c> and <c>y_top = [0xF1B4] − 7</c>,
    /// i.e. <b><c>[0xF1B2]</c> is X and <c>[0xF1B4]</c> is Y</b>, the order the projection
    /// settled.  The four lock lines then run <c>(X−9,Y) → (X,Y−9) → (X+9,Y) → (X,Y+9) → (X−9,Y)</c>:
    /// a DIAMOND, not the axis-aligned square the decoded file's comments describe.
    /// </remarks>
    /// <param name="filter">the panel's reconstruction filter (see <see cref="DrawWaterline"/>).</param>
    private static int DrawTargetBox(
        PixelTarget target, CockpitScale scale, in HudMarker marker, Rgb24 ink,
        CockpitFilter filter)
    {
        int x = (int)Math.Round(marker.X);
        int y = (int)Math.Round(marker.Y);
        int left = x - 7;
        int top = y - 7;
        const int Size = 15;

        CockpitRenderer.FillDesignRect(
            target, scale, new PanelRect(left, top, Size, 1), ink, filter);
        CockpitRenderer.FillDesignRect(
            target, scale, new PanelRect(left, top + Size - 1, Size, 1), ink, filter);
        CockpitRenderer.FillDesignRect(
            target, scale, new PanelRect(left, top, 1, Size), ink, filter);
        CockpitRenderer.FillDesignRect(
            target, scale, new PanelRect(left + Size - 1, top, 1, Size), ink, filter);
        int pixels = 4 * Size;

        if (!marker.Locked)
        {
            return pixels;
        }

        PanelRect clip = new PanelRect(
            x - 12, y - 12, 25, 25);
        Span<(int X, int Y)> corners =
        [
            (x - 9, y), (x, y - 9), (x + 9, y), (x, y + 9), (x - 9, y),
        ];
        for (int i = 0; i < 4; i++)
        {
            CockpitRenderer.DrawDesignLine(
                target, scale, corners[i].X, corners[i].Y, corners[i + 1].X, corners[i + 1].Y, ink, clip);
            pixels += 9;
        }

        return pixels;
    }

    /// <summary>
    /// The GUN PIPPER: a five-pixel ring at the lead point, plus up to four range dots.
    /// </summary>
    /// <remarks>
    /// <c>hud_target_lock_logic @image@0x0D1D3..0x0D22E</c> draws it as four short runs —
    /// <c>hline(cx−1 … cx+1, cy−2)</c>, <c>hline(cx−1 … cx+1, cy+2)</c>,
    /// <c>vline(cx−2, cy−1 … cy+1)</c>, <c>vline(cx+2, cy−1 … cy+1)</c> — which is a Bresenham
    /// circle of radius 2 without its corners.  The dots then go at <c>(cx, cy−3)</c>,
    /// <c>(cx+3, cy)</c>, <c>(cx, cy+3)</c>, <c>(cx−3, cy)</c> as the target crosses three quarters,
    /// a half and a quarter of the weapon's reach (<c>image@0x0D25B</c>, <c>0x0D27C</c>,
    /// <c>0x0D297</c>, <c>0x0D2B2</c>).
    /// </remarks>
    /// <param name="filter">the panel's reconstruction filter (see <see cref="DrawWaterline"/>).</param>
    private static int DrawPipper(
        PixelTarget target, CockpitScale scale, in HudMarker marker, Rgb24 ink,
        CockpitFilter filter)
    {
        int x = (int)Math.Round(marker.X);
        int y = (int)Math.Round(marker.Y);

        CockpitRenderer.FillDesignRect(
            target, scale, new PanelRect(x - 1, y - 2, 3, 1), ink, filter);
        CockpitRenderer.FillDesignRect(
            target, scale, new PanelRect(x - 1, y + 2, 3, 1), ink, filter);
        CockpitRenderer.FillDesignRect(
            target, scale, new PanelRect(x - 2, y - 1, 1, 3), ink, filter);
        CockpitRenderer.FillDesignRect(
            target, scale, new PanelRect(x + 2, y - 1, 1, 3), ink, filter);
        int pixels = 12;

        Span<(int X, int Y)> dots = [(x, y - 3), (x + 3, y), (x, y + 3), (x - 3, y)];
        for (int i = 0; i < Math.Clamp(marker.LeadDots, 0, 4); i++)
        {
            CockpitRenderer.FillDesignRect(
                target, scale, new PanelRect(dots[i].X, dots[i].Y, 1, 1), ink, filter);
            pixels++;
        }

        return pixels;
    }

    // The REFINED strokes (HudStyle.Refined).  Same geometry as the 1991 runs: the pipper ring's
    // centre is the lead point and its radius the 1991 ring's (2 design px, measured to the run
    // centres); the lead dots sit at radius 3 on the four axes; the waterline is the two 4-px bars;
    // the box is 15 × 15 about the marker and the diamond has half-size 9.  Only the STROKE changes:
    // anti-aliased at window resolution, with round dots and capsule ends, so the sight reads as a
    // gunsight and not as a staircase.  The marker is NOT rounded to a design pixel first — the lead
    // point is a projected double and the sub-pixel position is real information at host resolution.

    /// <summary>The stroke half-width of the refined HUD marks, in design pixels.</summary>
    public const double RefinedHalfStroke = 0.32;

    /// <summary>The radius of a refined lead dot, in design pixels.</summary>
    public const double RefinedDotRadius = 0.55;

    /// <summary>The pipper ring's radius, in design pixels (the 1991 runs sit at ±2).</summary>
    public const double PipperRingRadius = 2.0;

    /// <summary>The pipper's centre dot radius, in design pixels.</summary>
    public const double PipperCentreDotRadius = 0.35;

    private static int DrawPipperRefined(
        PixelTarget target, CockpitScale scale, in HudMarker marker, Rgb24 ink, double halfStroke)
    {
        double x = marker.X;
        double y = marker.Y;
        double k = halfStroke / RefinedHalfStroke;   // the dots scale with the stroke
        CockpitRenderer.DrawDesignRing(target, scale, x, y, PipperRingRadius, halfStroke, ink);
        CockpitRenderer.DrawDesignDot(target, scale, x, y, PipperCentreDotRadius * k, ink);
        int pixels = 12;

        Span<(double X, double Y)> dots = [(x, y - 3), (x + 3, y), (x, y + 3), (x - 3, y)];
        for (int i = 0; i < Math.Clamp(marker.LeadDots, 0, 4); i++)
        {
            CockpitRenderer.DrawDesignDot(target, scale, dots[i].X, dots[i].Y, RefinedDotRadius * k, ink);
            pixels++;
        }

        return pixels;
    }

    private static int DrawWaterlineRefined(
        PixelTarget target, CockpitScale scale, int centreX, int centreY, Rgb24 ink, double halfStroke)
    {
        // The 1991 bars cover columns [cx−6, cx−3] and [cx+3, cx+6] of row cy: capsules between the
        // outer pixel centres, i.e. from cx−5.5 to cx−2.5 and cx+3.5 to cx+6.5 at y = cy+0.5.
        double y = centreY + 0.5;
        PanelRect clip = new PanelRect(centreX - 8, centreY - 2, 17, 5);
        CockpitRenderer.DrawDesignStroke(target, scale, centreX - 5.5, y, centreX - 2.5, y, halfStroke, ink, clip);
        CockpitRenderer.DrawDesignStroke(target, scale, centreX + 3.5, y, centreX + 6.5, y, halfStroke, ink, clip);
        return 8;
    }

    private static int DrawTargetBoxRefined(
        PixelTarget target, CockpitScale scale, in HudMarker marker, Rgb24 ink, double halfStroke)
    {
        // The 1991 outline is the 15 × 15 pixel box whose outer pixels are (x−7 .. x+7): its
        // stroke centre runs through the outer pixels' centres, ±7 from the marker.
        double x = marker.X + 0.5;
        double y = marker.Y + 0.5;
        const double half = 7.0;
        PanelRect clip = new PanelRect((int)Math.Floor(marker.X) - 12, (int)Math.Floor(marker.Y) - 12, 26, 26);
        Span<(double X, double Y)> corners =
        [
            (x - half, y - half), (x + half, y - half), (x + half, y + half), (x - half, y + half), (x - half, y - half),
        ];
        for (int i = 0; i < 4; i++)
        {
            CockpitRenderer.DrawDesignStroke(
                target, scale, corners[i].X, corners[i].Y, corners[i + 1].X, corners[i + 1].Y, halfStroke, ink, clip);
        }

        int pixels = 60;
        if (!marker.Locked)
        {
            return pixels;
        }

        Span<(double X, double Y)> diamond =
        [
            (x - 9, y), (x, y - 9), (x + 9, y), (x, y + 9), (x - 9, y),
        ];
        for (int i = 0; i < 4; i++)
        {
            CockpitRenderer.DrawDesignStroke(
                target, scale, diamond[i].X, diamond[i].Y, diamond[i + 1].X, diamond[i + 1].Y, halfStroke, ink, clip);
            pixels += 9;
        }

        return pixels;
    }

    /// <summary>The message strip — the advisor line, and the two built-in stall warnings.</summary>
    /// <remarks>
    /// <c>draw_string @image@0x0CC85</c>: a pending message expires against
    /// <c>g_frame_time_accum</c>, and with none pending the aircraft's stall state prints
    /// <c>"APPROACHING STALL"</c> (<c>[0xF0BA] == 2</c>, DGROUP <c>[0x45D8]</c>) or <c>"STALL"</c>
    /// (<c>== 3</c>, <c>[0x45D2]</c>) centred as <c>x = (0x50 − len)·2</c>.  The row is the layout
    /// block's own <c>messageLineY</c> (<c>[0xF198]</c>).
    /// </remarks>
    private static bool DrawMessage(
        PixelTarget target,
        CockpitFont font,
        CockpitScale scale,
        in HudState state,
        HudLayout layout,
        Rgb24 ink)
    {
        if (state.Message is not { Length: > 0 } text)
        {
            return false;
        }

        int x = state.MessageX >= 0 ? state.MessageX : (0x50 - text.Length) * 2;
        Text(target, font, scale, text, x, layout.MessageLineY, ink);
        return true;
    }

    /// <summary>The canopy's bullet holes, masked into the frame.</summary>
    /// <remarks>
    /// <c>hud_damage_indicator_ring_draw @image@0x0CD75</c> walks the two-entry list at
    /// <c>[0xBA82]</c> backwards and blits the 48 × 38 decal at each entry's <c>(x, y)</c> every
    /// frame; nothing ever clears them, so the canopy keeps its holes for the sortie.
    /// </remarks>
    private static int DrawHitMarkers(
        PixelTarget target,
        CockpitScale scale,
        CockpitArt art,
        in HudState state,
        in CockpitOptions options,
        HudDecal decal)
    {
        if (state.HitMarkers is not { Count: > 0 } markers)
        {
            return 0;
        }

        // A RADIAL opacity: alphaCentre at the decal's centre falling to alphaEdge at its corners, so
        // the spider-web reads as glass damage without walling off the view.  1/1 = the original's
        // opaque blit.
        double alphaCentre = Math.Clamp(options.HitMarkerAlphaCentre, 0.0, 1.0);
        double alphaEdge = Math.Clamp(options.HitMarkerAlphaEdge, 0.0, 1.0);
        bool opaque = alphaCentre >= 1.0 && alphaEdge >= 1.0;
        double cx = decal.Width / 2.0, cy = decal.Height / 2.0;
        double halfDiagonal = Math.Sqrt((cx * cx) + (cy * cy));

        int painted = 0;
        foreach (PanelPoint at in markers)
        {
            PanelRect rect = new PanelRect(at.X, at.Y, decal.Width, decal.Height);
            (int x0, int x1, int y0, int y1) = CockpitRenderer.WindowBounds(target, rect, scale);
            for (int x = x0; x < x1; x++)
            {
                double fx = scale.ToDesignX(x + 0.5) - at.X;
                int dx = (int)fx;
                Span<uint> column = target.Column(x);
                for (int y = y0; y < y1; y++)
                {
                    double fy = scale.ToDesignY(y + 0.5) - at.Y;
                    int dy = (int)fy;
                    int index = decal.At(dx, dy);
                    if (index < 0 || index >= art.Palette.Count)
                    {
                        continue;
                    }

                    uint packed = target.Encode(art.Palette[index]);
                    if (opaque)
                    {
                        column[y] = packed;
                    }
                    else
                    {
                        double r = Math.Sqrt(((fx - cx) * (fx - cx)) + ((fy - cy) * (fy - cy))) / halfDiagonal;
                        double alpha = alphaCentre + ((alphaEdge - alphaCentre) * Math.Clamp(r, 0.0, 1.0));
                        CockpitRenderer.Blend(target, x, y, packed, (byte)Math.Round(alpha * 255.0));
                    }

                    painted++;
                }
            }
        }

        return painted;
    }

    /// <summary>
    /// The IN-WORLD TARGET DESIGNATOR labels: the type at <c>y + 9</c> and the chance-to-hit at <c>y
    /// + 15</c>, both left-aligned from <c>x − (2·len + 1)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>hud_engagement_label_draw @image@0x0CDB4</c> draws the two rows through its private helper
    /// at <c>image@0x0CEB9</c>: <c>adjusted_x = x − (2·len + 1)</c> (<c>image@0x0CEE8..0x0CEEB</c>),
    /// then a left and a right clip against <c>g_gfx_clip_x_min/_x_max</c> and a Y clip on
    /// <c>y &lt; clip_y_min || y + 5 &gt; clip_y_max</c>.  Measured against
    /// a captured frame of the original: the box's centre is
    /// (133, 61), the type row starts at <b>116</b> = <c>133 − 17</c> ✔ and the percentage row at
    /// <b>126</b> = <c>133 − 7</c> ✔ (its first glyph hidden behind the cockpit's gunsight post).
    /// </para>
    /// <para>
    /// The colour is the label's own, not the HUD's: <c>image@0x0CE2F..0x0CE6C</c> sets
    /// <c>g_text_color</c> to palette <b>12</b> when the object is engaging the player and palette
    /// <b>0</b> otherwise, for the shipped <c>g_cfg_sub_mode</c>.  Measured: the atlas' label is
    /// <c>(0,0,0)</c> and its target's <c>block[+0x1B]</c> is not the player.
    /// </para>
    /// </remarks>
    /// <param name="target">The frame.</param>
    /// <param name="font">The glyph strip.</param>
    /// <param name="scale">The design mapping.</param>
    /// <param name="art">The aircraft's art, for its palette.</param>
    /// <param name="state">The frame's HUD state.</param>
    /// <param name="options">The cockpit knobs.</param>
    /// <returns>How many rows were drawn.</returns>
    private static int DrawDesignators(
        PixelTarget target,
        CockpitFont font,
        CockpitScale scale,
        CockpitArt art,
        in HudState state,
        in CockpitOptions options)
    {
        if (state.Designators is not { Count: > 0 } labels)
        {
            return 0;
        }

        Rgb24 enemy = art.Palette.Count > DesignatorEnemyPaletteIndex
            ? art.Palette[DesignatorEnemyPaletteIndex]
            : new Rgb24(0, 0, 0);
        Rgb24 hostile = art.Palette.Count > DesignatorHostilePaletteIndex
            ? art.Palette[DesignatorHostilePaletteIndex]
            : new Rgb24(255, 85, 85);

        Rgb24 lockInk = art.Palette.Count > InkPaletteIndex
            ? art.Palette[InkPaletteIndex]
            : new Rgb24(255, 255, 85);

        int rows = 0;
        for (int i = 0; i < labels.Count; i++)
        {
            HudDesignator label = labels[i];
            Rgb24 ink = label.EngagingPlayer ? hostile : enemy;

            // The projected point is NOT rounded to a design pixel.  Rounding it makes the target
            // labels and the selection box jump: one design pixel is six host pixels at 1080p, so
            // the box and both rows step by six while the aeroplane under them slides.  The projector already
            // answers in doubles; only the classic filled box still snaps, because a filled design
            // rectangle is an integer thing.
            double x = label.X;
            double y = label.Y;

            // The SELECTED object's yellow box.  It is NOT the guided weapon's 15 × 15 one
            // (image@0x0CA38, HudState.TargetMarker): this is the 0x100 widget arm's own marker,
            // `gfx_rect_outline_draw(colour = g_hud_lock_box_color [0xF1AA], x − 4, y − 4, 9, 9)`
            // at image@0x1EDD4..0x1EDED, drawn whatever weapon is selected.  Measured on
            // a captured frame of the original nine columns of
            // (255,255,85) at 129..137 by 49..57, with the guns up.
            if (label.Selected)
            {
                if (options.Hud == HudStyle.Refined)
                {
                    DrawDesignatorBoxRefined(target, scale, x, y, lockInk, options.HudHalfStroke);
                }
                else
                {
                    DrawDesignatorBox(
                        target, scale, (int)Math.Round(x), (int)Math.Round(y), lockInk, in options);
                }
            }

            // A PORT ADDITION — the two label rows are drawn at a FIXED number of host pixels per
            // font pixel (1:1 in pixel terms; the port is not recreating 320×200 at 4K), with an
            // opacity, and can be switched off entirely.  The ANCHOR is still the design-space
            // projection, so a label rides its target exactly as before; only the glyph layout
            // changes.  The original's own left-align term `x − (2·len + 1)` (image@0x0CEE8) is a
            // CENTRING in disguise — 2·len is half of the 4·len the string occupies — so the
            // host-pixel rows are centred on the same point, and the second row is stacked one
            // glyph height below the first instead of at the design offset, which at 4K would fling
            // it a third of the screen away.
            if (!options.DesignatorLabels)
            {
                continue;
            }

            int labelScale = Math.Max(1, options.DesignatorLabelScale);
            double anchorX = scale.ToWindowX(x + 0.5);

            // The rows hang from the BOX's lower edge, a couple of host pixels under it, instead of
            // at the original's design offset (+9 rows, image@0x0CE74), which reads as confusingly
            // far below the aeroplane it belongs to.  At 320×200
            // the +9 row is half a box under a 9-pixel box; at 1080p it was 27 host pixels of
            // nothing between a 1:1 label and its target.  The design constant stays as the record
            // of the original's placement.
            double rowY = DesignatorLabelTop(scale, y);
            int rowStep = (font.Height * labelScale) + 1;

            if (!string.IsNullOrEmpty(label.TypeName))
            {
                DrawLabelRow(
                    target, font, label.TypeName, anchorX, rowY, labelScale, ink, in options);
                rowY += rowStep;
                rows++;
            }

            if (label.HitPercent >= 0)
            {
                string percent = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture, $"{label.HitPercent}%");
                DrawLabelRow(
                    target, font, percent, anchorX, rowY, labelScale, ink, in options);
                rows++;
            }
        }

        return rows;
    }

    /// <summary>One designator row, centred on the label's anchor and drawn in host pixels.</summary>
    /// <param name="target">The frame.</param>
    /// <param name="font">The glyph strip.</param>
    /// <param name="text">The row.</param>
    /// <param name="anchorX">The label's centre, in window pixels.</param>
    /// <param name="rowY">The row's top, in window pixels.</param>
    /// <param name="pixelScale">Host pixels per font pixel.</param>
    /// <param name="ink">The label's colour.</param>
    /// <param name="options">The cockpit knobs, for the opacity.</param>
    private static void DrawLabelRow(
        PixelTarget target,
        CockpitFont font,
        string text,
        double anchorX,
        double rowY,
        int pixelScale,
        Rgb24 ink,
        in CockpitOptions options)
    {
        int width = CockpitRenderer.MeasureUnscaled(font, text, pixelScale);
        CockpitRenderer.DrawTextUnscaled(
            target, font, text, anchorX - (width / 2.0), rowY, pixelScale, ink,
            options.DesignatorOpacity);
    }

    /// <summary>The SELECTED object's box: 9 design pixels a side (<c>image@0x1EDE4</c>).</summary>
    public const int DesignatorBoxSize = 9;

    /// <summary>Host pixels between the box's lower edge and the first label row.</summary>
    public const int DesignatorLabelGapPixels = 2;

    /// <summary>
    /// Where the first label row's top lands, in window pixels: the box's lower edge (the outer pixel
    /// row's far side, <c>y + 0.5 + 4.5</c> design rows under the projected point) plus
    /// <see cref="DesignatorLabelGapPixels"/>.  Drawn whether or not the box itself is up, so a label
    /// sits at the same place before and after the target is selected.
    /// </summary>
    /// <param name="scale">The design mapping.</param>
    /// <param name="y">The projected design row.</param>
    /// <returns>The row's top in window pixels.</returns>
    public static double DesignatorLabelTop(CockpitScale scale, double y) =>
        scale.ToWindowY(y + 0.5 + (DesignatorBoxSize / 2.0)) + DesignatorLabelGapPixels;

    /// <summary>
    /// The selected object's box as the REFINED HUD draws it: the same 9 × 9 outline, stroked
    /// through the outer pixels' centres (±4 design pixels of the point) at the HUD's own stroke
    /// width, anti-aliased at the projected double.
    /// </summary>
    /// <remarks>
    /// A six-pixel-thick filled box reads as too heavy beside the HUD and the guided-weapon target
    /// selection, so it is drawn thinner.  The guided weapon's 15 × 15 box (<see cref="DrawTargetBoxRefined"/>) was already a capsule stroke
    /// of <see cref="RefinedHalfStroke"/>; this is the same treatment for the 9 × 9 one.
    /// </remarks>
    private static void DrawDesignatorBoxRefined(
        PixelTarget target, CockpitScale scale, double x, double y, Rgb24 ink, double halfStroke)
    {
        double cx = x + 0.5;
        double cy = y + 0.5;
        const double half = DesignatorBoxSize / 2.0 - 0.5;   // 4: the outer pixels' centres
        PanelRect clip = new PanelRect((int)Math.Floor(x) - 8, (int)Math.Floor(y) - 8, 18, 18);
        Span<(double X, double Y)> corners =
        [
            (cx - half, cy - half), (cx + half, cy - half), (cx + half, cy + half), (cx - half, cy + half), (cx - half, cy - half),
        ];
        for (int i = 0; i < 4; i++)
        {
            CockpitRenderer.DrawDesignStroke(
                target, scale, corners[i].X, corners[i].Y, corners[i + 1].X, corners[i + 1].Y, halfStroke, ink, clip);
        }
    }

    /// <summary>
    /// The selected object's box — a 9 × 9 outline in <c>g_hud_lock_box_color [0xF1AA]</c>.
    /// </summary>
    private static void DrawDesignatorBox(
        PixelTarget target, CockpitScale scale, int x, int y, Rgb24 ink, in CockpitOptions options)
    {
        int left = x - (DesignatorBoxSize / 2);
        int top = y - (DesignatorBoxSize / 2);
        const int Size = DesignatorBoxSize;
        CockpitRenderer.FillDesignRect(
            target, scale, new PanelRect(left, top, Size, 1), ink, options.Filter);
        CockpitRenderer.FillDesignRect(
            target, scale, new PanelRect(left, top + Size - 1, Size, 1), ink, options.Filter);
        CockpitRenderer.FillDesignRect(
            target, scale, new PanelRect(left, top, 1, Size), ink, options.Filter);
        CockpitRenderer.FillDesignRect(
            target, scale, new PanelRect(left + Size - 1, top, 1, Size), ink, options.Filter);
    }

    /// <summary>The designator's type row, in design rows below the object — <c>image@0x0CE74</c>.</summary>
    public const int DesignatorTypeRowOffset = 9;

    /// <summary>Its percentage row — <c>image@0x0CE9A</c> (<c>add dx,0xf</c> after the first).</summary>
    public const int DesignatorPercentRowOffset = 0x0F;

    /// <summary>The label's palette index for an object that is NOT engaging the player: 0, black.</summary>
    public const int DesignatorEnemyPaletteIndex = 0;

    /// <summary>Its palette index for one that IS: 12 (<c>mov word [0x220],0xFF0C</c> @<c>image@0x0CE4F</c>).</summary>
    public const int DesignatorHostilePaletteIndex = 12;

    private static void Text(
        PixelTarget target,
        CockpitFont font,
        CockpitScale scale,
        string text,
        int designX,
        int designY,
        Rgb24 ink) =>
        CockpitRenderer.DrawText(target, font, scale, text, designX, designY, ink);
}
