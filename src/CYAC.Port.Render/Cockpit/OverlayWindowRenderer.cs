using CYAC.Port.Core.Model.Cockpit;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.Mission;

namespace CYAC.Port.Render.Cockpit;

/// <summary>
/// The four in-flight overlay windows' shared CHROME.
/// </summary>
/// <remarks>
/// <para>
/// <c>cockpit_panel_draw_dispatch @image@0x0EA65</c> dispatches four sub-panels off
/// <c>g_inflight_overlay_visibility [0xF1CB]</c>, and all four wear the same frame: a 72 × 70 grey
/// panel with a one-pixel black drop shadow down its right side and along its bottom, an 11-row
/// title band, a 64 × 48 content rectangle and an 11-row footer band.  Measured off the original's
/// own frames and cross-checked against the dispatcher's published rows — see
/// <see cref="OverlayWindowLayout"/> for the byte trail.
/// </para>
/// <para>
/// <b>Fidelity ruling (human): chrome integer-scaled, contents host-res.</b> This class draws the
/// chrome through <see cref="CockpitRenderer"/>'s own design-space primitives, so a window scales
/// with the cockpit art and lands on the same fractional edges; what goes INSIDE the content
/// rectangle is each window's own slice, and it draws at host resolution.
/// </para>
/// <para>
/// W0 frames the MAP, ENVELOPE and TARGET windows and fills each content rectangle with its face
/// colour; the contents are W3, W4 and W2.  Closed by W5 — the fourth window is framed here like the
/// others, at design column 4, while and only while a message is up; see <see cref="AdvisorWindow"/>.
/// </para>
/// <para>
/// The layer is drawn AFTER the HUD, because a window overdraws the world and the designator label
/// under it (<c>21_mig21_cfg0F_target.png</c> shows the label's <c>MiG</c> clipped by the envelope
/// window).  The HUD's own two corner blocks are not overdrawn but SUPPRESSED — see
/// <see cref="OverlayWindowLayout.CoversHudTopLeft"/>.
/// </para>
/// </remarks>
public sealed class OverlayWindowRenderer
{
    /// <summary>
    /// The face colour behind a window's contents, by window.  The MAP and the TARGET clear to
    /// palette 0 (the atlas' black map face; the target window's own sky/ground fill is W2's), and
    /// the ENVELOPE's four-colour plot covers its whole face, so black is only ever seen for one
    /// frame at a mode change.
    /// </summary>
    public const int FacePaletteIndex = 0;

    /// <summary>
    /// The face a window's contents are cleared to, by window.
    /// </summary>
    /// <param name="window">Which window.</param>
    /// <returns>Its palette index.</returns>
    /// <remarks>
    /// Refined W4 — the ENVELOPE's face is never cleared at all.  <c>menu_filled_rect_draw(1, 1,
    /// 0x46, 0x48, [0xBCF0], draw_col)</c> at <c>image@0x0EBA4</c> paints the WHOLE 72 × 70 panel in
    /// the toolkit's background colour — <c>0xFF07</c> for the VGA sub-mode
    /// (<c>menu_bg_fill_color_select @image@0x20CB8</c>, P424) — and the drawer then paints its
    /// regions over the middle of it.  So when the load factor has no envelope record the drawer
    /// returns immediately (<c>image@0x0EC2B</c>) and the content shows GREY, not black.  Measured on
    /// every atlas frame: the four regions cover the face completely, so the difference is only
    /// visible in that degenerate case — but the degenerate case is the one worth getting right.
    /// <para>
    /// And the YEAGER window's face is the panel grey too, for the same reason: its drawer fills the
    /// whole 72 × 70 with <c>menu_filled_rect_draw</c> (<c>image@0x0EF36</c>) and then blits a 64 ×
    /// 42 portrait into the top of the content rectangle, leaving the six rows under it grey.
    /// Measured on the window's own pixels: of its 5,040 design pixels 2,047 are palette 7 and none
    /// is palette 0 outside the portrait's own dark tones
    /// (a captured frame of the original).
    /// </para>
    /// </remarks>
    public static int FaceFor(CockpitOverlayFlags window) =>
        window is CockpitOverlayFlags.Envelope or CockpitOverlayFlags.Yeager
            ? OverlayWindowLayout.ChromePaletteIndex
            : FacePaletteIndex;

    /// <summary>
    /// A PORT ADDITION — the design→window mapping for ONE overlay window, at a fixed number of host
    /// pixels per DESIGN pixel.
    /// </summary>
    /// <param name="baseScale">The panel's own mapping, which says where the window belongs.</param>
    /// <param name="x">The window's design column.</param>
    /// <param name="pixelScale">Host pixels per design pixel; 1 is a strict 1:1.</param>
    /// <param name="rightPinned">Whether the window hangs off its RIGHT edge (the TARGET window).</param>
    /// <returns>A mapping every design-space draw call can use unchanged.</returns>
    /// <remarks>
    /// <para>
    /// A window's frame reads as too thick when it is enlarged to match the original: the chrome is a
    /// 4-pixel border and an 11-row band, and at 1080p the panel's own scale multiplies those by six,
    /// so a 1:1 scale suits it better at a higher resolution. Enlarging is
    /// correct for cockpit ART, which is a picture being enlarged, and wrong for a window whose frame is
    /// a frame.
    /// </para>
    /// <para>
    /// Rather than rewrite three dozen design-space draw calls in host coordinates, this SUBSTITUTES
    /// the mapping they already take: a <see cref="CockpitScale"/> whose scale is the chosen pixel
    /// size and whose offset pins the window where the panel's own mapping would have put it. Every
    /// existing call — the chrome, the bands, the map's dots, the target panel's rows — then draws
    /// the same geometry at the new size with no change at all.
    /// </para>
    /// <para>
    /// The anchor keeps each window in the corner it belongs to: a left-packed window's top-left
    /// stays where it was, and the TARGET window's RIGHT edge stays where it was, so shrinking them
    /// does not leave one floating in the middle of the band.
    /// </para>
    /// </remarks>
    public static CockpitScale WindowScale(
        CockpitScale baseScale, int x, int pixelScale, bool rightPinned)
    {
        double anchorX = rightPinned
            ? baseScale.ToWindowX(x + OverlayWindowLayout.Width)
                - (OverlayWindowLayout.Width * pixelScale)
            : baseScale.ToWindowX(x);
        double anchorY = baseScale.ToWindowY(OverlayWindowLayout.Top);

        return baseScale with
        {
            ScaleX = pixelScale,
            ScaleY = pixelScale,
            OffsetX = anchorX - (x * pixelScale),
            OffsetY = anchorY - (OverlayWindowLayout.Top * pixelScale),
        };
    }

    /// <summary>The pixel scale a window is drawn at, honouring the knob's auto value.</summary>
    /// <param name="target">The frame, for its size.</param>
    /// <param name="options">The cockpit knobs.</param>
    /// <returns>Host pixels per design pixel, at least 1.</returns>
    /// <remarks>
    /// <para>
    /// 0 means AUTO: HALF the design scale the window would otherwise get, floored at 2 — which is
    /// 3 at 1080p and 5 at 4K, i.e. the window keeps the same fraction of the screen at any
    /// resolution and its frame is half the thickness it used to be.
    /// </para>
    /// <para>
    /// AUTO is the honest compromise: it answers the complaint the ask was really about (the frame
    /// was six pixels thick per design pixel) without making the instrument useless, and
    /// <c>--window-scale 1</c> gives the literal reading to anyone who wants it. The comparison is
    /// in the port's own notes.
    /// </para>
    /// </remarks>
    public static int PixelScale(PixelTarget target, in CockpitOptions options)
    {
        if (options.OverlayWindowScale > 0)
        {
            return options.OverlayWindowScale;
        }

        double design = Math.Min(
            target.Width / (double)CockpitLayout.DesignWidth,
            target.Height / (double)CockpitLayout.DesignHeight);
        return Math.Max(2, (int)Math.Round(design / AutoScaleDivisor));
    }

    /// <summary>Which design column the MAP window occupies for a visibility mask.</summary>
    /// <param name="visible">The live <c>[0xF1CB]</c> mask.</param>
    /// <returns>The map's slot, or the first slot when it is not up.</returns>
    /// <remarks>
    /// The cursor law puts the map in the first slot whenever it is up
    /// (<see cref="OverlayWindowLayout.Slots"/>), but asking the layout is what keeps this correct
    /// if the Yeager advisor is ever given a slot ahead of it.
    /// </remarks>
    internal static int MapSlotX(CockpitOverlayFlags visible) =>
        MapSlotX(visible, advisorSpeaking: false);

    /// <summary>Which design column the MAP window occupies, the advisor's slot included.</summary>
    /// <param name="visible">The live <c>[0xF1CB]</c> mask.</param>
    /// <param name="advisorSpeaking">Whether the YEAGER window is on the band this frame.</param>
    /// <returns>The map's slot, or the first slot when it is not up.</returns>
    internal static int MapSlotX(CockpitOverlayFlags visible, bool advisorSpeaking)
    {
        foreach ((CockpitOverlayFlags window, int x) in OverlayWindowLayout.Slots(visible, advisorSpeaking))
        {
            if (window == CockpitOverlayFlags.Map)
            {
                return x;
            }
        }

        return OverlayWindowLayout.FirstSlotX;
    }

    /// <summary>The divisor AUTO uses: half the design scale.</summary>
    public const double AutoScaleDivisor = 2.0;

    /// <summary>Draws every visible window's chrome over a finished frame.</summary>
    /// <param name="target">The frame.</param>
    /// <param name="art">The aircraft's cockpit art — for its palette and the design mapping.</param>
    /// <param name="font">The game's own glyph strip (the <c>4x6</c> font the bands use).</param>
    /// <param name="state">The frame's window state.</param>
    /// <param name="options">The cockpit knobs, for the design → window mapping.</param>
    /// <returns>What was drawn.</returns>
    public OverlayWindowFrameStats Render(
        PixelTarget target,
        CockpitArt art,
        CockpitFont font,
        in OverlayWindowState state,
        in CockpitOptions options)
    {
        ArgumentNullException.ThrowIfNull(art);
        ArgumentNullException.ThrowIfNull(font);

        if (!state.Any)
        {
            return default;
        }

        // The windows live in the same 320×200 design space the cockpit does, and they are drawn
        // whether or not the panel is painted (they sit over the world, above the viewport), so the
        // mapping is the panel's own with cockpitDrawn = true.  that mapping now only says WHERE
        // each window belongs; the window itself is drawn at PixelScale host pixels per design
        // pixel (WindowScale), so its frame stops being enlarged with the panel art.
        CockpitScale baseScale = CockpitScale.For(
            target.Width, target.Height, art.Viewport, options.Fit, cockpitDrawn: true);
        int pixelScale = PixelScale(target, in options);
        Rgb24 chrome = Palette(art, OverlayWindowLayout.ChromePaletteIndex);
        Rgb24 shadow = Palette(art, OverlayWindowLayout.ShadowPaletteIndex);
        Rgb24 ink = Palette(art, OverlayWindowLayout.TextPaletteIndex);

        int windows = 0;
        int chromePixels = 0;
        int texts = 0;

        foreach ((CockpitOverlayFlags window, int x) in OverlayWindowLayout.Slots(state.Visible, state.AdvisorPanel.Speaking))
        {
            if (window == CockpitOverlayFlags.Target && !state.TargetPresent)
            {
                continue;
            }

            CockpitScale scale = WindowScale(
                baseScale, x, pixelScale, window == CockpitOverlayFlags.Target);
            PanelRect frame = OverlayWindowLayout.Frame(x);
            PanelRect content = OverlayWindowLayout.Content(x);

            // The frame first, then the face over it: the original fills the whole rectangle in one
            // call (image@0x0EAE8) and its content is painted on top, so the border is what is left
            // of the fill.
            CockpitRenderer.FillDesignRect(target, scale, frame, chrome, options.Filter);
            CockpitRenderer.FillDesignRect(
                target, scale, content, Palette(art, FaceFor(window)), options.Filter);
            chromePixels += (frame.Width * frame.Height) - (content.Width * content.Height);
            chromePixels += DrawShadow(target, scale, frame, shadow, in options);

            int bandRow = OverlayWindowLayout.Top + OverlayWindowLayout.BandTextOffset;

            // The TARGET window's band is TWO strings at two fixed anchors, not one centred one:
            // the word TARGET at design column 248 (image@0x0EDCF) and the type at 312 − 4·len
            // (image@0x0EE02).  Every other window keeps the toolkit's centring rule.
            if (window == CockpitOverlayFlags.Target)
            {
                CockpitRenderer.DrawText(
                    target, font, scale, art.Strings.TargetTitle, OverlayWindowLayout.TargetTitleX, bandRow, ink);
                texts++;
                if (state.TargetTitle.Length > 0)
                {
                    CockpitRenderer.DrawText(
                        target, font, scale, state.TargetTitle,
                        OverlayWindowLayout.TargetTypeX(state.TargetTitle), bandRow, ink);
                    texts++;
                }
            }
            else
            {
                string title = TitleFor(art.Strings, window, in state);
                if (title.Length > 0)
                {
                    CockpitRenderer.DrawText(
                        target, font, scale, title,
                        OverlayWindowLayout.TitleX(window, x, title), bandRow, ink);
                    texts++;
                }
            }

            string footer = FooterFor(window, in state);
            if (footer.Length > 0)
            {
                CockpitRenderer.DrawText(
                    target, font, scale, footer,
                    OverlayWindowLayout.FooterX(x),
                    frame.Bottom - OverlayWindowLayout.BandHeight + OverlayWindowLayout.BandTextOffset,
                    ink);
                texts++;
            }

            windows++;
        }

        return new OverlayWindowFrameStats(windows, chromePixels, texts);
    }

    // InFlightStrings.TargetTitle, read from the tree by that address.

    /// <summary>
    /// The TARGET window's CONTENTS: five text rows and the weapon-station ladder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drawn as a pass of its own, AFTER the host has rendered the silhouette into the same
    /// rectangle, because in the original they are the same order:
    /// <c>polygon_fill_mesh_render_setup</c> paints the mini-scene (<c>image@0x0A78E</c>) and the
    /// labels go over it (<c>image@0x0A7E1</c> onwards).
    /// </para>
    /// <para>
    /// The content text is <b>BLACK</b>, not the band's white: <c>image@0x0A7C8</c> sets
    /// <c>g_text_color [0x220] = byte[0xF1A8] &lt;&lt; 8</c> with <c>AL</c> forced to zero (<c>sub
    /// al,al</c>), so the foreground index is palette 0 whatever else happens.  Measured on the
    /// atlas: the title rows are <c>(255,255,255)</c> and every content row is <c>(0,0,0)</c>.
    /// </para>
    /// </remarks>
    /// <param name="target">The frame.</param>
    /// <param name="art">The aircraft's cockpit art — for its palette and the design mapping.</param>
    /// <param name="font">The game's own glyph strip.</param>
    /// <param name="state">The frame's window state.</param>
    /// <param name="options">The cockpit knobs.</param>
    /// <returns>How many strings were drawn.</returns>
    public int RenderTargetContents(
        PixelTarget target,
        CockpitArt art,
        CockpitFont font,
        in OverlayWindowState state,
        in CockpitOptions options)
    {
        ArgumentNullException.ThrowIfNull(art);
        ArgumentNullException.ThrowIfNull(font);

        if (!state.Visible.HasFlag(CockpitOverlayFlags.Target) || !state.TargetPresent)
        {
            return 0;
        }

        CockpitScale scale = WindowScale(
            CockpitScale.For(
                target.Width, target.Height, art.Viewport, options.Fit, cockpitDrawn: true),
            OverlayWindowLayout.TargetSlotX,
            PixelScale(target, in options),
            rightPinned: true);
        Rgb24 ink = Palette(art, ContentTextPaletteIndex);
        TargetPanelState panel = state.TargetPanel;
        int texts = 0;

        // image@0x0A7EA — the AI manoeuvre, centred one row below the content's top.
        if (panel.Manoeuvre.Length > 0)
        {
            CockpitRenderer.DrawText(
                target, font, scale, panel.Manoeuvre,
                TargetPanel.CentredColumn(panel.Manoeuvre), TargetPanel.ManoeuvreRow, ink);
            texts++;
        }

        // image@0x0A83B — the radar lock state, six rows below it.
        if (panel.LockState.Length > 0)
        {
            CockpitRenderer.DrawText(
                target, font, scale, panel.LockState,
                TargetPanel.CentredColumn(panel.LockState), TargetPanel.LockStateRow, ink);
            texts++;
        }

        // image@0x0A8D3 — the clock bearing, centred near the bottom.
        if (panel.Clock.Length > 0)
        {
            CockpitRenderer.DrawText(
                target, font, scale, panel.Clock,
                TargetPanel.CentredColumn(panel.Clock), TargetPanel.ClockRow, ink);
            texts++;
        }

        // image@0x0A8A7 — the speed, left-aligned four columns in from the content's edge.
        if (panel.Speed.Length > 0)
        {
            CockpitRenderer.DrawText(
                target, font, scale, panel.Speed,
                TargetPanel.SpeedColumn, TargetPanel.BottomRow, ink);
            texts++;
        }

        // image@0x0A928 — the range shares that row, right-aligned; it is CENTRED instead when the
        // speed line was not produced (the original's [bp-0xC] selector).
        if (panel.Range.Length > 0)
        {
            CockpitRenderer.DrawText(
                target, font, scale, panel.Range,
                panel.RangeCentred
                    ? TargetPanel.CentredColumn(panel.Range)
                    : TargetPanel.RightAlignedColumn(panel.Range),
                TargetPanel.BottomRow, ink);
            texts++;
        }

        // The ENGAGEMENT CAPTION, in the FOOTER band.  It is drawn by cockpit_target_info_panel_draw
        // itself (image@0x0EE91), AFTER its own cockpit_text_color_set (image@0x0EE76) and BEFORE
        // the clip rectangle is restored — so it is the BAND's white, not the content's black, and
        // it is centred on the panel's own centre.  Measured: `Wingman` starts at design column 264,
        // the same as `PURSUIT`.
        if (panel.Caption.Length > 0)
        {
            CockpitRenderer.DrawText(
                target, font, scale, panel.Caption,
                TargetPanel.CentredColumn(panel.Caption), TargetPanel.CaptionRow,
                Palette(art, OverlayWindowLayout.TextPaletteIndex));
            texts++;
        }

        DrawWeaponStationLadder(target, scale, panel.WeaponStation, Palette(art, LadderPaletteIndex), in options);
        return texts;
    }

    /// <summary>
    /// The MAP window's CONTENTS: the contacts, then the player's own marker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is <c>per_frame_object_pixel_render</c>'s own: the face is filled first (the chrome
    /// pass already did it, in the same palette-0 the map's colour-select callback returns for
    /// <c>SI = 0</c>, <c>image@0x0D2FA</c>), then the ten slots are plotted
    /// (<c>image@0x0D630..0x0D6A9</c>), and the own-ship pixel goes on LAST, unconditionally
    /// (<c>image@0x0D6C2..0x0D6CE</c>) — so a contact that lands on the centre is overdrawn by it.
    /// </para>
    /// <para>
    /// <b>Fidelity ruling (human): chrome integer-scaled, contents host-res.</b> A dot is an
    /// anti-aliased disc at the projector's fractional position, the same treatment an earlier pass gave the
    /// scopes' blips, rather than a snapped design pixel.  A <see cref="ScopeContact.Wide"/> contact
    /// (a bomber) is the original's two-pixel span, drawn as two discs one design pixel apart.
    /// </para>
    /// </remarks>
    /// <param name="target">The frame.</param>
    /// <param name="art">The aircraft's cockpit art — for its palette and the design mapping.</param>
    /// <param name="state">The frame's window state.</param>
    /// <param name="options">The cockpit knobs.</param>
    /// <returns>How many contacts were plotted (the own-ship marker is not one).</returns>
    public int RenderMapContents(
        PixelTarget target,
        CockpitArt art,
        in OverlayWindowState state,
        in CockpitOptions options)
    {
        ArgumentNullException.ThrowIfNull(art);

        // The EFFECTIVE mask: a speaking advisor takes the map off the band for the rest of the
        // frame, and the contents pass has to see that as the chrome pass does.
        CockpitOverlayFlags visible = state.EffectiveVisible;
        if (!visible.HasFlag(CockpitOverlayFlags.Map))
        {
            return 0;
        }

        CockpitScale scale = WindowScale(
            CockpitScale.For(
                target.Width, target.Height, art.Viewport, options.Fit, cockpitDrawn: true),
            MapSlotX(state.Visible, state.AdvisorPanel.Speaking),
            PixelScale(target, in options),
            rightPinned: false);
        int shift = Math.Clamp(
            state.MapPanel.ScaleShift, MapWindow.MinScaleShift, MapWindow.MaxScaleShift);
        int plotted = 0;

        foreach (ScopeContact contact in state.MapPanel.Contacts ?? [])
        {
            if (!MapWindow.Shows(in contact, shift))
            {
                continue;
            }

            (double x, double y) = MapWindow.Plot(in contact, shift);
            Rgb24 ink = Palette(art, MapWindow.DotPaletteIndex(contact.MapDot, contact.AtOrAbovePlayer));
            CockpitRenderer.FillDesignDisc(target, scale, x + 0.5, y + 0.5, DotRadius, ink);
            if (contact.Wide)
            {
                // image@0x0D698..0x0D6A4 — gfx_hline_draw from x to x+1, one row.
                CockpitRenderer.FillDesignDisc(target, scale, x + 1.5, y + 0.5, DotRadius, ink);
            }

            plotted++;
        }

        (int cx, int cy) = MapWindow.Centre;
        CockpitRenderer.FillDesignDisc(
            target, scale, cx + 0.5, cy + 0.5, DotRadius,
            Palette(art, MapWindow.OwnShipPaletteIndex));
        return plotted;
    }

    /// <summary>
    /// The ENVELOPE window's CONTENTS: the four-colour region plot and the marker.
    /// </summary>
    /// <param name="target">The frame.</param>
    /// <param name="art">The aircraft's cockpit art — for its palette and the design mapping.</param>
    /// <param name="state">The frame's window state.</param>
    /// <param name="options">The cockpit knobs.</param>
    /// <returns>How many of the drawer's five shapes were painted — 0 or 5.</returns>
    /// <remarks>
    /// <para>
    /// The order is the drawer's own, and it matters twice: the LOWER-RIGHT rectangle is drawn after
    /// the UPPER-RIGHT one, so the high-speed boundary row itself comes out light blue rather than
    /// cyan; and the curve's filled polygon goes over all three rectangles, so the green region is
    /// what the aeroplane CAN reach and the three coloured rectangles are the three ways it cannot
    /// (too slow / too fast and too high / too fast and too low).  The marker goes on last
    /// (<c>image@0x0EDA0</c>), so it is never hidden by the plot.
    /// </para>
    /// </remarks>
    public int RenderEnvelopeContents(
        PixelTarget target,
        CockpitArt art,
        in OverlayWindowState state,
        in CockpitOptions options)
    {
        ArgumentNullException.ThrowIfNull(art);

        // And the same for the envelope, which is the window the advisor takes when there is no
        // map (image@0x0EF10..0x0EF1C).
        CockpitOverlayFlags visible = state.EffectiveVisible;
        if (!visible.HasFlag(CockpitOverlayFlags.Envelope)
            || state.EnvelopePanel.Curve is not { } curve)
        {
            return 0;
        }

        int x = EnvelopeSlotX(state.Visible, state.AdvisorPanel.Speaking);
        CockpitScale scale = WindowScale(
            CockpitScale.For(
                target.Width, target.Height, art.Viewport, options.Fit, cockpitDrawn: true),
            x,
            PixelScale(target, in options),
            rightPinned: false);

        EnvelopePanelState panel = state.EnvelopePanel;
        Span<double> xs = stackalloc double[EnvelopeCurve.PointSlots];
        Span<double> ys = stackalloc double[EnvelopeCurve.PointSlots];
        int count = EnvelopeWindow.Project(curve, panel.CornerX, panel.CornerY, x, xs, ys);
        if (count < 3)
        {
            return 0;
        }

        int pivotColumn = (int)xs[Math.Min(curve.PeakIndex, count - 1)];
        int pivotRow = (int)ys[Math.Min(curve.HighSpeedIndex, count - 1)];
        PanelRect content = OverlayWindowLayout.Content(x);

        // image@0x0ECB1 / image@0x0ECC7 / image@0x0ED00 — three gfx_filled_scanline_rect calls.
        CockpitRenderer.FillDesignRect(
            target, scale, EnvelopeWindow.LeftRegion(pivotColumn, x),
            Palette(art, EnvelopeWindow.LeftPaletteIndex), options.Filter);
        CockpitRenderer.FillDesignRect(
            target, scale, EnvelopeWindow.UpperRightRegion(pivotColumn, pivotRow, x),
            Palette(art, EnvelopeWindow.UpperRightPaletteIndex), options.Filter);
        CockpitRenderer.FillDesignRect(
            target, scale, EnvelopeWindow.LowerRightRegion(pivotColumn, pivotRow, x),
            Palette(art, EnvelopeWindow.LowerRightPaletteIndex), options.Filter);

        // image@0x0ED29 — gfx_polygon_scanline_fill over the projected points, colour 0xFF02.
        CockpitRenderer.FillDesignPolygon(
            target, scale, xs, ys, count,
            Palette(art, EnvelopeWindow.CurvePaletteIndex), content);

        // image@0x0EDA0 — the 2 x 2 marker, blinking with the render frame's parity.
        CockpitRenderer.FillDesignRect(
            target, scale,
            EnvelopeWindow.MarkerRect(
                panel.AirspeedFps, panel.AltitudeQ8, panel.CornerX, panel.CornerY, x),
            Palette(art, EnvelopeWindow.MarkerPaletteIndex(panel.RenderFrameCounter)),
            options.Filter);

        return EnvelopeShapes;
    }

    /// <summary>How many shapes the envelope drawer paints: three rectangles, a curve and a marker.</summary>
    public const int EnvelopeShapes = 5;

    /// <summary>
    /// The YEAGER window's CONTENTS: Chuck's portrait and his two lines.
    /// </summary>
    /// <param name="target">The frame.</param>
    /// <param name="art">The aircraft's cockpit art — its palette, and the sheet the portrait is cut from.</param>
    /// <param name="font">The game's own glyph strip.</param>
    /// <param name="state">The frame's window state.</param>
    /// <param name="options">The cockpit knobs.</param>
    /// <returns>How many text lines were drawn — 0, 1 or 2.</returns>
    /// <remarks>
    /// <para>
    /// The drawer's own order: the panel fill (the chrome pass), the title, the PORTRAIT
    /// (<c>gfx_plain_blit</c> at <c>image@0x0EF72</c>), then the clip rectangle and the two lines —
    /// line 2 first (<c>image@0x0EFB1</c>), line 1 second (<c>image@0x0EFE2</c>).  Nothing overlaps,
    /// so the port draws them in reading order.
    /// </para>
    /// <para>
    /// <b>Fidelity.</b>  The portrait is ART — the same <c>miscv.pic</c> the cockpit's own lamps and
    /// levers come out of — so it is integer-scaled with the window and resampled with the panel's
    /// own filter, exactly like every other sprite the cockpit takes from that sheet.  The two lines
    /// are the game's 4 × 6 glyphs through <see cref="CockpitRenderer.DrawText"/>, which is
    /// nearest-sampled and unblended: at the window's integer pixel scale that IS a 1:1 host-pixel
    /// grid (one font pixel → one <c>pixelScale</c> × <c>pixelScale</c> block of hard pixels), and
    /// <c>--window-scale 1</c> gives the literal one-host-pixel-per-font-pixel reading.
    /// </para>
    /// </remarks>
    public int RenderAdvisorContents(
        PixelTarget target,
        CockpitArt art,
        CockpitFont font,
        in OverlayWindowState state,
        in CockpitOptions options)
    {
        ArgumentNullException.ThrowIfNull(art);
        ArgumentNullException.ThrowIfNull(font);

        if (!state.AdvisorUp)
        {
            return 0;
        }

        CockpitScale scale = WindowScale(
            CockpitScale.For(
                target.Width, target.Height, art.Viewport, options.Fit, cockpitDrawn: true),
            AdvisorWindow.SlotX,
            PixelScale(target, in options),
            rightPinned: false);

        AdvisorPanelState panel = state.AdvisorPanel;
        (int sourceX, int sourceY) = AdvisorWindow.PortraitSource(panel.DisplayType);
        CockpitRenderer.BlitDesignSheet(
            target, art, scale, AdvisorWindow.Portrait, sourceX, sourceY, in options);

        Rgb24 ink = Palette(art, AdvisorWindow.TextPaletteIndex);
        bool hasLine2 = !string.IsNullOrEmpty(panel.Line2);
        int texts = 0;

        if (!string.IsNullOrEmpty(panel.Line1))
        {
            CockpitRenderer.DrawText(
                target, font, scale, panel.Line1,
                AdvisorWindow.TextColumn(panel.Line1.Length),
                AdvisorWindow.Line1Row(hasLine2), ink);
            texts++;
        }

        if (hasLine2)
        {
            CockpitRenderer.DrawText(
                target, font, scale, panel.Line2,
                AdvisorWindow.TextColumn(panel.Line2.Length),
                AdvisorWindow.Line2Row, ink);
            texts++;
        }

        return texts;
    }

    /// <summary>Which design column the ENVELOPE window occupies for a visibility mask.</summary>
    /// <param name="visible">The live <c>[0xF1CB]</c> mask.</param>
    /// <returns>The envelope's slot, or the first slot when it is not up.</returns>
    /// <remarks>
    /// The cursor law puts it in the SECOND slot when the map is up and the first otherwise
    /// (<c>image@0x0EACE</c> / <c>image@0x0EAA4</c>) — asking the layout is what keeps this and the
    /// chrome pass in step.
    /// </remarks>
    public static int EnvelopeSlotX(CockpitOverlayFlags visible) =>
        EnvelopeSlotX(visible, advisorSpeaking: false);

    /// <summary>
    /// Which design column the ENVELOPE window occupies, the advisor's slot included.
    /// </summary>
    /// <param name="visible">The live <c>[0xF1CB]</c> mask.</param>
    /// <param name="advisorSpeaking">Whether the YEAGER window is on the band this frame.</param>
    /// <returns>The envelope's slot, or the first slot when it is not up.</returns>
    /// <remarks>
    /// This MUST be asked with the raw mask and the advisor's state, never with the suppressed mask:
    /// with all four bits up and Chuck speaking the map is gone, but the cursor has still been
    /// advanced (<c>image@0x0EF1C</c>) and the envelope is still at 128.  Handing this the reduced
    /// mask instead put the envelope's plot at column 4, over the portrait — caught by the per-pixel
    /// diff against the original, which is what the diff is for.
    /// </remarks>
    public static int EnvelopeSlotX(CockpitOverlayFlags visible, bool advisorSpeaking)
    {
        foreach ((CockpitOverlayFlags window, int x) in OverlayWindowLayout.Slots(visible, advisorSpeaking))
        {
            if (window == CockpitOverlayFlags.Envelope)
            {
                return x;
            }
        }

        return OverlayWindowLayout.FirstSlotX;
    }

    /// <summary>
    /// A map dot's radius in design pixels — the same 0.9 an earlier pass gave a scope blip, so one original
    /// pixel reads as one dot at any host resolution.
    /// </summary>
    public const double DotRadius = CockpitRenderer.BlipRadius;

    /// <summary>The content text's palette index — 0, black (see <see cref="RenderTargetContents"/>).</summary>
    public const int ContentTextPaletteIndex = 0;

    /// <summary>The weapon-station ladder's palette index: 15, the low nibble of <c>0xF00F</c>.</summary>
    public const int LadderPaletteIndex = 15;

    /// <summary>
    /// The footer band's WEAPON-STATION ladder: a dithered one-pixel run down design column 248.
    /// </summary>
    /// <remarks>
    /// <c>image@0x0EE9B..0x0EEC9</c>.  The original's colour word carries the stipple key
    /// <c>0xF0</c>, which is why its run comes out as alternate lit rows — the port draws the same
    /// alternate rows directly rather than modelling the Mode-X dither mask, because the mask is a
    /// property of the 320×200 pixel grid the port does not have: a window's contents are drawn at
    /// host resolution.  Measured: rows 81/83/85 for a station index of 3, 83/85 for 2.
    /// </remarks>
    private static void DrawWeaponStationLadder(
        PixelTarget target, CockpitScale scale, int station, Rgb24 ink, in CockpitOptions options)
    {
        (int first, int last) = TargetPanel.WeaponStationRows(station);
        for (int row = first + 1; row <= last; row += 2)
        {
            CockpitRenderer.FillDesignRect(
                target, scale, new PanelRect(TargetPanel.WeaponStationColumn, row, 1, 1), ink,
                options.Filter);
        }
    }

    /// <summary>The title band's string for a window.</summary>
    /// <param name="strings">The in-flight words: the four window titles.</param>
    /// <param name="window">Which window.</param>
    /// <param name="state">The frame's state.</param>
    /// <returns>The string, or empty when the window has no title.</returns>
    /// <remarks>
    /// The three shipped titles come from the atlas' own glyphs: <c>MAP</c>, <c>ENVELOPE</c> and
    /// <c>TARGET</c> followed by the target's type.  The Yeager window's title is <b>(open)</b> — he
    /// was silent in every captured frame, so making him speak is the only way to read it off the result.
    /// </remarks>
    private static string TitleFor(InFlightStrings strings, CockpitOverlayFlags window, in OverlayWindowState state) =>
        window switch
        {
            // The M is at design column 32, the literal the drawer pushes (image@0x0EAF4), and the
            // string is the three-character "MAP" at DGROUP 0x3BBE.  No blanks, no centring: see
            // OverlayWindowLayout.TitleX.
            CockpitOverlayFlags.Map => strings.MapTitle,
            CockpitOverlayFlags.Envelope => strings.EnvelopeTitle,

            // Done: the drawer pushes DGROUP[0x3BD4] at image@0x0EF3E, which is "CHUCK YEAGER"
            // (image@0x3F934), and the OCR of four captured frames reads exactly that at design
            // column 16.
            CockpitOverlayFlags.Yeager => strings.AdvisorTitle,
            CockpitOverlayFlags.Target => state.TargetTitle.Length > 0
                ? strings.TargetTitle + "  " + state.TargetTitle
                : strings.TargetTitle,
            _ => string.Empty,
        };

    private static string FooterFor(CockpitOverlayFlags window, in OverlayWindowState state) =>
        window switch
        {
            CockpitOverlayFlags.Map => state.MapFooter,
            CockpitOverlayFlags.Envelope => state.EnvelopeFooter,

            // There is no callsign anywhere in the panel.  The marks an earlier pass read as a colon are the
            // WEAPON-STATION LADDER, a dithered vertical run at design column 248 whose length is
            // block[+0x05] & 3: three lit rows for the MiG-21's target, two for the F-4's.
            // RenderTargetContents draws it.
            _ => string.Empty,
        };

    /// <summary>
    /// The one-pixel drop shadow: the column just right of the frame from its second row down to its
    /// last, and the row just below it from its second column to that same column.
    /// </summary>
    /// <remarks>
    /// Measured on <c>21_mig21_cfg0F_target.png</c>: with the MAP window at x = 4 the shadow is
    /// palette 0 at column 76 over rows 20..88 and at row 89 over columns 5..76.
    /// </remarks>
    private static int DrawShadow(
        PixelTarget target,
        CockpitScale scale,
        PanelRect frame,
        Rgb24 shadow,
        in CockpitOptions options)
    {
        PanelRect right = new PanelRect(frame.Right, frame.Y + 1, 1, frame.Height);
        PanelRect bottom = new PanelRect(frame.X + 1, frame.Bottom, frame.Width, 1);
        CockpitRenderer.FillDesignRect(target, scale, right, shadow, options.Filter);
        CockpitRenderer.FillDesignRect(target, scale, bottom, shadow, options.Filter);
        return frame.Height + frame.Width;
    }

    private static Rgb24 Palette(CockpitArt art, int index) =>
        art.Palette.Count > index ? art.Palette[index] : new Rgb24(170, 170, 170);
}
