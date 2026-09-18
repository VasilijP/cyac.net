using System.Diagnostics;
using System.Globalization;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Render.Cockpit;
using CYAC.Port.Render.Ground;

namespace CYAC.Port.Render.Map;

/// <summary>Where the map is drawn.</summary>
public enum MapMode
{
    /// <summary>Not drawn.</summary>
    Off = 0,

    /// <summary>
    /// The whole window — the original's own behaviour: F9 REPLACES the 3-D view with a 2-D screen
    /// (<c>mission_per_frame_render_phase</c>'s <c>[0xC320] == 0x0C</c> branch, <c>image@0x01570</c>,
    /// which jumps clean over the 3-D path to the combat dispatch at <c>image@0x01595</c>).
    /// </summary>
    Full = 1,

    /// <summary>
    /// PORT ADDITION — an inset in the bottom-right corner, over the flying view.  The original has
    /// no such thing (deviation D1).
    /// </summary>
    Inset = 2,
}

/// <summary>How much of the theatre the map draws.</summary>
public enum MapScenery
{
    /// <summary>
    /// Only what the ORIGINAL's map draws: rivers and roads.  The enqueuer
    /// <c>screen_buffer_iter_dispatch @image@0x1E766</c> copies a pool object into the course buffer
    /// only when its class descriptor near-pointer is <c>0x98E8</c> (road) or <c>0x985C</c> (river) —
    /// nothing else in the theatre reaches the map at all.
    /// </summary>
    Courses = 0,

    /// <summary>PORT ADDITION — every scenery class with a footprint (deviation D3).</summary>
    All = 1,
}

/// <summary>What the range readouts are quoted in.</summary>
public enum MapRangeUnits
{
    /// <summary>Nautical miles — the manual's own unit for ranges.</summary>
    NauticalMiles = 0,

    /// <summary>Feet — world units, one for one.</summary>
    Feet = 1,
}

/// <summary>The map's knobs.</summary>
/// <param name="Mode">Where it is drawn.</param>
/// <param name="Scenery">How much of the theatre.</param>
/// <param name="ZoomLevel">
/// The original's <c>g_map_zoom_level [0xD8A0]</c>, 7..12, or <b>0</b> for the port's FIT.
/// </param>
/// <param name="CentreOnPlayer">
/// At the FIT zoom: whether the frame is centred on the PLAYER (keeping the fit's scale) rather than
/// on the mission's own rectangle.  At one of the original's zoom levels it has no effect — that map
/// is always the player's, as the original's is.
/// </param>
/// <param name="Units">The unit of the scale bar and the rings.</param>
/// <param name="RangeRings">PORT ADDITION — the range rings around the player.</param>
/// <param name="NorthArrow">PORT ADDITION — the north arrow.</param>
/// <param name="ScaleBar">PORT ADDITION — the scale bar.</param>
/// <param name="Labels">Whether waypoint names and the caption are drawn.</param>
/// <param name="Blink">
/// Whether the player's cross blinks the way the original's does — drawn on 3 frames of 4
/// (<c>g_map_symbol_blink_phase [0xBA14] &amp; 3</c>, <c>image@0x1E722</c>).
/// </param>
/// <param name="StrokeScale">A multiplier on every drawn width, to taste.</param>
public readonly record struct MapViewOptions(
    MapMode Mode = MapMode.Full,
    MapScenery Scenery = MapScenery.All,
    int ZoomLevel = 0,
    bool CentreOnPlayer = false,
    MapRangeUnits Units = MapRangeUnits.NauticalMiles,
    bool RangeRings = true,
    bool NorthArrow = true,
    bool ScaleBar = true,
    bool Labels = true,
    bool Blink = true,
    double StrokeScale = 1.0);

/// <summary>What the map drew in one frame.</summary>
/// <param name="Segments">Scenery footprint segments actually drawn (after culling).</param>
/// <param name="SegmentsCulled">Segments whose bounding box missed the map rectangle.</param>
/// <param name="Actors">Object glyphs drawn.</param>
/// <param name="Waypoints">Waypoint markers drawn.</param>
/// <param name="Milliseconds">Wall time of the whole layer.</param>
public readonly record struct MapFrameStats(
    int Segments, int SegmentsCulled, int Actors, int Waypoints, double Milliseconds);

/// <summary>
/// The F9 MAP: the original's 2-D top-down theatre screen, drawn at host resolution.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the 1991 map is.</b>  F9 is not a camera: the scancode dispatch at <c>image@0x333BE</c>
/// maps <c>0x4300</c> to view id <b>0x0C</b> and <c>mission_per_frame_render_phase</c> branches on
/// <c>g_current_view_mode [0xC320] == 0x0C</c> (<c>image@0x01570</c>) into
/// <c>scene_frame_setup_or_redraw @image@0x1E43C</c>, a 2-D screen with its own orthographic
/// projector.  The simulation keeps running: the branch is inside the RENDER phase, after the frame's
/// input and physics, and it falls through to the same combat dispatch and page flip as the 3-D path.
/// </para>
/// <para>
/// <b>What it draws</b> — the whole of it, and the port draws all four:
/// </para>
/// <list type="number">
///   <item><description>a background of the ground green (the span fill at <c>image@0x1E871</c>);</description></item>
///   <item><description>every RIVER and ROAD placement as one straight line through its own centre
///   along its own heading (<c>briefing_map_screen_obj_line_draw @image@0x1EA8D</c>, half-lengths and
///   colours from <c>[0x2F8A]</c>/<c>[0x2F90]</c>);</description></item>
///   <item><description>every registered NAV WAYPOINT as a white 5×7 dot, a digit and its name
///   (<c>briefing_map_screen_label_draw_iter @image@0x1E955</c>);</description></item>
///   <item><description>the PLAYER as a blinking two-line cross at his own projected position
///   (<c>briefing_map_mesh_and_symbol_draw @image@0x1E823</c> phase 3), and a black border line
///   across the bottom row.</description></item>
/// </list>
/// <para>
/// The HUD text widgets stay on over it — <c>a captured frame of the original shows heading, altitude, speed, G, throttle, VSI and
/// <c>ZOOM:1</c> in yellow over the map — which is exactly <see cref="Core.Model.Cockpit.HudMask"/>'s
/// non-forward-view mask <c>0x2F7</c>.  The host keeps drawing them; the map does not draw them itself.
/// </para>
/// <para>
/// <b>Everything else this class draws is a labelled PORT ADDITION</b>: the other scenery classes,
/// the live aircraft, wrecks, the range rings, the scale bar, the north arrow, the caption and the
/// current-waypoint highlight.
/// </para>
/// <para>The layer is PURE: it reads the scene and writes pixels, never simulation state.</para>
/// </remarks>
public sealed class MapView
{
    private const double NauticalMileFeet = 6076.115485564304;   // the international nautical mile

    private static readonly double[] NiceNauticalMiles =
        [0.25, 0.5, 1, 2, 5, 10, 20, 50, 100, 200];

    /// <summary>How far back the port-added scenery classes are drawn: 45 % coverage.</summary>
    private const double PortSceneryAlpha = 0.45;

    private static readonly double[] NiceFeet =
        [500, 1000, 2000, 5000, 10000, 20000, 50000, 100000, 200000, 500000];

    private readonly MapFootprints _footprints = new();
    private readonly LineWidthModel _widths;
    private Rgb24[] _palette;

    /// <summary>Creates a map view.</summary>
    /// <param name="palette">
    /// The game's 256-entry VGA palette, already widened to 8 bits
    /// (<c>data/palettes/palette.json</c>); a shorter list is padded with black.
    /// </param>
    /// <param name="widths">
    /// The H15 line-width model, so a road and a river are drawn at the physical widths the ground
    /// layer gives them rather than at a made-up hairline.
    /// </param>
    public MapView(IReadOnlyList<Rgb24>? palette = null, LineWidthModel? widths = null)
    {
        _palette = Build(palette);
        _widths = widths ?? LineWidthModel.Default;
    }

    /// <summary>Replaces the palette.</summary>
    /// <param name="palette">The palette.</param>
    public void SetPalette(IReadOnlyList<Rgb24> palette) => _palette = Build(palette);

    /// <summary>The projection the last <see cref="Render"/> used — for a readout or a test.</summary>
    public MapProjection LastProjection { get; private set; }

    /// <summary>Draws the map.</summary>
    /// <param name="target">The pixels; the map fills all of it.</param>
    /// <param name="scene">What to draw.</param>
    /// <param name="options">The knobs.</param>
    /// <param name="font">The game's own font for the labels, or null to draw none.</param>
    /// <returns>What was drawn.</returns>
    public MapFrameStats Render(
        PixelTarget target, MapScene scene, in MapViewOptions options, CockpitFont? font)
    {
        ArgumentNullException.ThrowIfNull(scene);
        long started = Stopwatch.GetTimestamp();

        MapProjection projection = Project(scene, options, target.Width, target.Height);
        LastProjection = projection;

        // The one pixel-scale the furniture and the 1991 marker geometry are drawn at: the window's
        // own scaling of the original's 320×200 screen, exactly as the cockpit layer scales its
        // design space.
        double pixelScale = Math.Max(1.0, target.Width / (double)MapProjection.DesignWidth);
        double stroke = Math.Max(0.5, options.StrokeScale);

        Fill(target, _palette[MapLook.BackgroundColorIndex]);

        MapFrameStats stats = DrawScenery(target, scene, options, projection, stroke);

        if (options.RangeRings)
        {
            DrawRangeRings(target, scene, options, projection, pixelScale, stroke, font);
        }

        int waypoints = DrawWaypoints(target, scene, options, projection, pixelScale, font);
        int actors = DrawActors(target, scene, options, projection, pixelScale, stroke, font);

        // The original's own bottom border: gfx_hline_draw(0xFF00, [0x166] − 1, 0x140, 0)
        // @image@0x1E75B — a black line across the last row.
        uint border = target.Encode(_palette[MapLook.BorderColorIndex]);
        for (int x = 0; x < target.Width; x++)
        {
            target.SetPixel(x, target.Height - 1, border);
        }

        if (options.NorthArrow)
        {
            DrawNorthArrow(target, pixelScale, stroke, font);
        }

        if (options.ScaleBar)
        {
            DrawScaleBar(target, options, projection, pixelScale, stroke, font);
        }

        if (options.Labels && font is not null && options.Mode != MapMode.Inset)
        {
            DrawCaption(target, scene, options, projection, pixelScale, font);
        }

        if (options.Mode == MapMode.Inset)
        {
            // PORT ADDITION — the inset needs an edge, because it sits over the flying view.
            uint edge = target.Encode(_palette[MapLook.CaptionColorIndex]);
            for (int x = 0; x < target.Width; x++)
            {
                target.SetPixel(x, 0, edge);
                target.SetPixel(x, target.Height - 1, edge);
            }

            for (int y = 0; y < target.Height; y++)
            {
                target.SetPixel(0, y, edge);
                target.SetPixel(target.Width - 1, y, edge);
            }
        }

        return stats with
        {
            Actors = actors,
            Waypoints = waypoints,
            Milliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency,
        };
    }

    /// <summary>Resolves the projection for a frame — the fit, or one of the original's zoom levels.</summary>
    /// <param name="scene">The scene.</param>
    /// <param name="options">The knobs.</param>
    /// <param name="width">The target's width.</param>
    /// <param name="height">Its height.</param>
    public static MapProjection Project(
        MapScene scene, in MapViewOptions options, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(scene);
        (double minX, double minZ, double maxX, double maxZ) = scene.FitBounds;
        if (options.ZoomLevel <= 0 && maxX > minX && maxZ > minZ)
        {
            MapProjection fitted = MapProjection.Fit(
                minX, minZ, maxX, maxZ, width, height,
                Math.Max(24.0, Math.Min(width, height) * 0.07));
            return options.CentreOnPlayer ? fitted.CentredOn(scene.PlayerX, scene.PlayerZ) : fitted;
        }

        int level = options.ZoomLevel <= 0
            ? MapProjection.MinZoomLevel
            : Math.Clamp(options.ZoomLevel, MapProjection.MinZoomLevel, MapProjection.MaxZoomLevel);

        // At one of the ORIGINAL's zoom levels the map is the original's map, and the original's is
        // ALWAYS centred on the player: scene_frame_setup_or_redraw seeds its origin from the camera
        // object (image@0x1E45E) and then only CLAMPS it to the scene's bounds, which the port does
        // not do (deviation D5).
        return MapProjection.ForZoomLevel(scene.PlayerX, scene.PlayerZ, level, width, height);
    }

    private MapFrameStats DrawScenery(
        PixelTarget target, MapScene scene, in MapViewOptions options, in MapProjection projection,
        double stroke)
    {
        int drawn = 0, culled = 0;
        double margin = 8.0;
        foreach (SceneInstance instance in scene.Statics)
        {
            string basename = instance.Mesh.Basename;
            int courseColor = MapLook.CourseColorIndex(basename);
            if (options.Scenery == MapScenery.Courses && courseColor < 0)
            {
                culled++;
                continue;
            }

            // A cheap instance-level cull first: the mesh's own bounding radius around its centre.
            (double centreX, double centreY) = projection.ToScreen(instance.X, instance.Z);
            double radiusPixels =
                instance.Mesh.Coarsest.BoundingRadius * instance.EffectiveWorldScale
                    * projection.PixelsPerWorldUnit;
            if (centreX + radiusPixels < -margin || centreX - radiusPixels > target.Width + margin
                || centreY + radiusPixels < -margin || centreY - radiusPixels > target.Height + margin)
            {
                culled++;
                continue;
            }

            Basis3 basis = Basis3.FromEuler(instance.HeadingDegrees * Math.PI / 180.0, 0.0, 0.0);
            double widthPixels = Math.Max(
                1.0 * stroke,
                _widths.WidthFeetFor(basename) * projection.PixelsPerWorldUnit * stroke);

            foreach (MapFootprints.Segment segment in _footprints.Of(instance.Mesh, instance.EffectiveWorldScale))
            {
                Vec3 a = basis.ToWorld(new Vec3(segment.X0, 0.0, segment.Z0));
                Vec3 b = basis.ToWorld(new Vec3(segment.X1, 0.0, segment.Z1));
                (double x0, double y0) = projection.ToScreen(instance.X + a.X, instance.Z + a.Z);
                (double x1, double y1) = projection.ToScreen(instance.X + b.X, instance.Z + b.Z);

                int color = courseColor >= 0 ? courseColor : segment.ColorIndex;

                // The two classes the ORIGINAL draws are drawn solid; the port's added classes are
                // drawn back, so a theatre's buildings and hills read as CONTEXT and the rivers,
                // roads, waypoints and aircraft stay in front of them.
                double alpha = courseColor >= 0 ? 1.0 : PortSceneryAlpha;
                if (DrawLine(target, x0, y0, x1, y1, widthPixels / 2.0, _palette[color], alpha))
                {
                    drawn++;
                }
                else
                {
                    culled++;
                }
            }
        }

        return new MapFrameStats(drawn, culled, 0, 0, 0.0);
    }

    private int DrawWaypoints(
        PixelTarget target, MapScene scene, in MapViewOptions options, in MapProjection projection,
        double pixelScale, CockpitFont? font)
    {
        int drawn = 0;
        Rgb24 ink = _palette[MapLook.WaypointColorIndex];
        Rgb24 digitInk = _palette[MapLook.WaypointDigitColorIndex];
        double dotW = MapLook.WaypointDotWidth * pixelScale;
        double dotH = MapLook.WaypointDotHeight * pixelScale;

        foreach (MapWaypoint waypoint in scene.Waypoints)
        {
            (double x, double y) = projection.ToScreen(waypoint.X, waypoint.Z);
            if (x < -dotW || y < -dotH || x > target.Width + dotW || y > target.Height + dotH)
            {
                continue;
            }

            // The original's marker: a filled 5×7 rect whose top-left is (x−1, y−1)
            // (image@0x1EA14), the slot's digit inside it (image@0x1EA24: al = slot + '1'), and the
            // name 9 design pixels to its right (image@0x1EA55) in the same white.
            FillRect(target, x - pixelScale, y - pixelScale, dotW, dotH, ink);
            drawn++;

            if (font is not null)
            {
                CockpitScale scale = TextScale(target, pixelScale);
                string digit = ((waypoint.Slot + 1) % 10).ToString(CultureInfo.InvariantCulture);
                CockpitRenderer.DrawText(
                    target, font, scale, digit,
                    (int)Math.Round((x - (pixelScale * 0.5)) / scale.ScaleX),
                    (int)Math.Round((y - (pixelScale * 0.5)) / scale.ScaleY),
                    digitInk);

                // The original draws the name only when it FITS: image@0x1EA55 tests
                // x + 4·len + 9 < g_gfx_clip_x_max, and drops the name (keeping the dot) otherwise.
                double nameX = x + (MapLook.WaypointLabelGap * pixelScale);
                bool fits = nameX + (font.Measure(waypoint.Name) * scale.ScaleX) < target.Width;
                if (options.Labels && fits)
                {
                    CockpitRenderer.DrawText(
                        target, font, scale, waypoint.Name,
                        (int)Math.Round(nameX / scale.ScaleX),
                        (int)Math.Round((y - (pixelScale * 0.5)) / scale.ScaleY),
                        ink);
                }
            }

            // PORT ADDITION — the CURRENT waypoint (the one the compass points at,
            // g_nav_slot_current_index [0xEF92]) wears a ring.  The original draws all of them
            // identically and leaves the pilot to read the number (deviation D4).
            if (waypoint.Current)
            {
                DrawRing(target, x, y, 5.5 * pixelScale, 0.9 * pixelScale, ink, 1.0);
            }
        }

        return drawn;
    }

    private int DrawActors(
        PixelTarget target, MapScene scene, in MapViewOptions options, in MapProjection projection,
        double pixelScale, double stroke, CockpitFont? font)
    {
        int drawn = 0;
        foreach (MapActor actor in scene.Actors)
        {
            (double x, double y) = projection.ToScreen(actor.X, actor.Z);
            double reach = 12.0 * pixelScale;
            if (x < -reach || y < -reach || x > target.Width + reach || y > target.Height + reach)
            {
                continue;
            }

            if (actor.Kind == MapActorKind.Player)
            {
                // The original's blink gate: the cross is drawn on the three frames of four whose
                // phase byte is non-zero (image@0x1E722 / image@0x1E879).
                if (options.Blink && (scene.BlinkPhase & 3) == 0)
                {
                    continue;
                }

                DrawPlayerCross(
                    target, x, y, actor.HeadingDegrees, pixelScale, stroke,
                    _palette[MapLook.PlayerColorIndex]);
                drawn++;
                continue;
            }

            Rgb24 ink = _palette[ColorIndexOf(actor.Kind)];
            if (actor.Kind == MapActorKind.Wreck)
            {
                DrawCross(target, x, y, 2.5 * pixelScale, 0.8 * pixelScale * stroke, ink);
            }
            else
            {
                DrawChevron(target, x, y, actor.HeadingDegrees, pixelScale, stroke, ink);
            }

            drawn++;

            if (options.Labels && font is not null && actor.Label is { Length: > 0 } label)
            {
                // Placed at the HOST pixel, not snapped to a font-scaled design cell: the chevron
                // slides at the projector's double and its label used to step beside it by whole
                // glyph-scale pixels.
                CockpitRenderer.DrawTextUnscaled(
                    target, font, label,
                    x + (4.0 * pixelScale), y + (2.0 * pixelScale),
                    (int)Math.Max(1.0, Math.Round(pixelScale)), ink, 1.0);
            }
        }

        return drawn;
    }

    private static int ColorIndexOf(MapActorKind kind) => kind switch
    {
        MapActorKind.Hostile => MapLook.HostileColorIndex,
        MapActorKind.Friendly => MapLook.FriendlyColorIndex,
        MapActorKind.Wreck => MapLook.WreckColorIndex,
        _ => MapLook.CaptionColorIndex,
    };

    /// <summary>
    /// The player's marker, exactly as <c>briefing_map_mesh_and_symbol_draw</c> builds it.
    /// </summary>
    /// <remarks>
    /// The source vector <c>(0, 30)</c> (<c>image@0x1E882</c>) is rotated by the NEGATED heading
    /// (<c>image@0x1E898</c>), then two lines are drawn (<c>image@0x1E901..0x1E94B</c>): <c>(c +
    /// v/4) → (c − v/16)</c> and <c>(c + p) → (c − p)</c> with <c>p</c> the eighth-scale vector
    /// turned a right angle.  Pixel-verified against <c>a captured frame of the original: with heading 0 the game
    /// draws a vertical line from <c>(134, 150)</c> to <c>(134, 159)</c> and a bar from <c>(131,
    /// 152)</c> to <c>(137, 152)</c>, which is this construction to the pixel with <c>c = (134,
    /// 152)</c>.  The long arm therefore trails BEHIND the aeroplane and the short stub is its nose.
    /// </remarks>
    private static void DrawPlayerCross(
        PixelTarget target, double cx, double cy, double headingDegrees, double pixelScale,
        double stroke, Rgb24 ink)
    {
        // v = rotate((0, L), −heading) in SCREEN axes: +y is south, and the map's own north-up
        // inversion is already in the projection, so a heading of 0 leaves v pointing down-screen,
        // which is what the screenshot shows.
        double th = headingDegrees * Math.PI / 180.0;
        double vx = MapLook.CrossVectorLength * Math.Sin(th) * pixelScale;
        double vy = MapLook.CrossVectorLength * Math.Cos(th) * pixelScale;

        double half = Math.Max(0.5, 0.55 * pixelScale * stroke);
        DrawLine(
            target,
            cx + (vx * MapLook.CrossTailFraction), cy + (vy * MapLook.CrossTailFraction),
            cx - (vx * MapLook.CrossNoseFraction), cy - (vy * MapLook.CrossNoseFraction),
            half, ink, 1.0);

        double px = -vy * MapLook.CrossBarFraction;
        double py = vx * MapLook.CrossBarFraction;
        DrawLine(target, cx + px, cy + py, cx - px, cy - py, half, ink, 1.0);
    }

    /// <summary>PORT ADDITION — another aircraft: a heading-oriented chevron.</summary>
    private static void DrawChevron(
        PixelTarget target, double cx, double cy, double headingDegrees, double pixelScale,
        double stroke, Rgb24 ink)
    {
        double th = headingDegrees * Math.PI / 180.0;
        // The nose direction in SCREEN axes: world (−sin h, cos h) with north up ⇒ (−sin h, −cos h).
        double nx = -Math.Sin(th), ny = -Math.Cos(th);
        double sx = -ny, sy = nx;                       // the right wing
        double nose = 5.5 * pixelScale, span = 3.0 * pixelScale;
        double half = Math.Max(0.5, 0.5 * pixelScale * stroke);

        // A clean delta: the nose, the two swept wing tips, and the trailing edge across them.
        double tipX = cx + (nx * nose), tipY = cy + (ny * nose);
        double backX = cx - (nx * nose * 0.7), backY = cy - (ny * nose * 0.7);
        double leftX = backX - (sx * span), leftY = backY - (sy * span);
        double rightX = backX + (sx * span), rightY = backY + (sy * span);

        DrawLine(target, tipX, tipY, leftX, leftY, half, ink, 1.0);
        DrawLine(target, tipX, tipY, rightX, rightY, half, ink, 1.0);
        DrawLine(target, leftX, leftY, rightX, rightY, half, ink, 1.0);
    }

    private static void DrawCross(
        PixelTarget target, double cx, double cy, double reach, double half, Rgb24 ink)
    {
        DrawLine(target, cx - reach, cy - reach, cx + reach, cy + reach, half, ink, 1.0);
        DrawLine(target, cx - reach, cy + reach, cx + reach, cy - reach, half, ink, 1.0);
    }

    private void DrawRangeRings(
        PixelTarget target, MapScene scene, in MapViewOptions options, in MapProjection projection,
        double pixelScale, double stroke, CockpitFont? font)
    {
        double step = NiceStep(options.Units, projection, target.Width);
        if (!(step > 0.0))
        {
            return;
        }

        (double cx, double cy) = projection.ToScreen(scene.PlayerX, scene.PlayerZ);
        Rgb24 ink = _palette[MapLook.FurnitureColorIndex];
        for (int i = 1; i <= 3; i++)
        {
            double radius = step * i * projection.PixelsPerWorldUnit;
            if (radius > (target.Width + target.Height) * 1.5)
            {
                break;
            }

            DrawRing(target, cx, cy, radius, Math.Max(0.5, 0.28 * pixelScale * stroke), ink, 0.30);

            if (options.Labels && font is not null && options.Mode != MapMode.Inset)
            {
                CockpitScale scale = TextScale(target, pixelScale);
                CockpitRenderer.DrawText(
                    target, font, scale, Distance(step * i, options.Units),
                    (int)Math.Round((cx + radius + (2.0 * pixelScale)) / scale.ScaleX),
                    (int)Math.Round((cy - (3.0 * pixelScale)) / scale.ScaleY),
                    ink);
            }
        }
    }

    private void DrawNorthArrow(
        PixelTarget target, double pixelScale, double stroke, CockpitFont? font)
    {
        double x = target.Width - (14.0 * pixelScale);
        double y = 16.0 * pixelScale;
        double reach = 8.0 * pixelScale;
        double half = Math.Max(0.5, 0.6 * pixelScale * stroke);
        Rgb24 ink = _palette[MapLook.FurnitureColorIndex];

        DrawLine(target, x, y + reach, x, y - reach, half, ink, 1.0);
        DrawLine(target, x, y - reach, x - (reach * 0.4), y - (reach * 0.45), half, ink, 1.0);
        DrawLine(target, x, y - reach, x + (reach * 0.4), y - (reach * 0.45), half, ink, 1.0);

        if (font is not null)
        {
            CockpitScale scale = TextScale(target, pixelScale);
            CockpitRenderer.DrawText(
                target, font, scale, "N",
                (int)Math.Round((x - (1.5 * pixelScale)) / scale.ScaleX),
                (int)Math.Round((y - reach - (8.0 * pixelScale)) / scale.ScaleY),
                ink);
        }
    }

    private void DrawScaleBar(
        PixelTarget target, in MapViewOptions options, in MapProjection projection,
        double pixelScale, double stroke, CockpitFont? font)
    {
        double step = NiceStep(options.Units, projection, target.Width);
        double length = step * projection.PixelsPerWorldUnit;
        if (!(length > 4.0) || length > target.Width)
        {
            return;
        }

        // Clear of the HUD's own bottom band (design rows 187..197 in the 1991 screen, which the
        // port scales), so the two never overprint.
        double x0 = 10.0 * pixelScale;
        double y = target.Height - (24.0 * pixelScale);
        double half = Math.Max(0.5, 0.6 * pixelScale * stroke);
        Rgb24 ink = _palette[MapLook.FurnitureColorIndex];

        DrawLine(target, x0, y, x0 + length, y, half, ink, 1.0);
        DrawLine(target, x0, y - (3.0 * pixelScale), x0, y + (3.0 * pixelScale), half, ink, 1.0);
        DrawLine(
            target, x0 + length, y - (3.0 * pixelScale), x0 + length, y + (3.0 * pixelScale),
            half, ink, 1.0);

        if (font is not null)
        {
            CockpitScale scale = TextScale(target, pixelScale);
            CockpitRenderer.DrawText(
                target, font, scale, Distance(step, options.Units),
                (int)Math.Round((x0 + (length / 2.0) - (8.0 * pixelScale)) / scale.ScaleX),
                (int)Math.Round((y - (11.0 * pixelScale)) / scale.ScaleY),
                ink);
        }
    }

    private void DrawCaption(
        PixelTarget target, MapScene scene, in MapViewOptions options, in MapProjection projection,
        double pixelScale, CockpitFont font)
    {
        CockpitScale scale = TextScale(target, pixelScale);
        Rgb24 ink = _palette[MapLook.CaptionColorIndex];
        string zoom = options.ZoomLevel <= 0
            ? "FIT"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"ZOOM:{1 << Math.Clamp(options.ZoomLevel - MapProjection.MinZoomLevel, 0, 15)}");
        string caption = string.Create(
            CultureInfo.InvariantCulture,
            $"MAP  {scene.TheatreName}{(scene.MissionName.Length > 0 ? "  " + scene.MissionName : string.Empty)}"
                + $"  {zoom}  1px={projection.WorldUnitsPerPixel:F0} ft");
        CockpitRenderer.DrawText(
            target, font, scale, caption,
            (int)Math.Round((8.0 * pixelScale) / scale.ScaleX),
            (int)Math.Round((6.0 * pixelScale) / scale.ScaleY),
            ink);
    }

    // ------------------------------------------------------------------ primitives

    private static CockpitScale TextScale(PixelTarget target, double pixelScale)
    {
        double s = Math.Max(1.0, Math.Round(pixelScale));
        return new CockpitScale(target.Width, target.Height, s, s, 0.0, 0.0, target.Height);
    }

    private static string Distance(double feet, MapRangeUnits units) => units == MapRangeUnits.Feet
        ? string.Create(CultureInfo.InvariantCulture, $"{feet:N0} ft")
        : string.Create(CultureInfo.InvariantCulture, $"{feet / NauticalMileFeet:0.##} nm");

    /// <summary>A round distance whose on-screen length is a comfortable fraction of the window.</summary>
    private static double NiceStep(MapRangeUnits units, in MapProjection projection, int width)
    {
        double wanted = width * 0.18 * projection.WorldUnitsPerPixel;
        double[] ladder = units == MapRangeUnits.Feet ? NiceFeet : NiceNauticalMiles;
        double unit = units == MapRangeUnits.Feet ? 1.0 : NauticalMileFeet;
        double best = ladder[0] * unit;
        foreach (double candidate in ladder)
        {
            best = candidate * unit;
            if (best >= wanted)
            {
                break;
            }
        }

        return best;
    }

    private static void Fill(PixelTarget target, Rgb24 color)
    {
        uint packed = target.Encode(color);
        for (int x = 0; x < target.Width; x++)
        {
            target.FillColumn(x, 0, target.Height, packed);
        }
    }

    private static void FillRect(
        PixelTarget target, double x, double y, double w, double h, Rgb24 color)
    {
        uint packed = target.Encode(color);
        int x0 = (int)Math.Round(x), x1 = (int)Math.Round(x + w);
        int y0 = (int)Math.Round(y), y1 = (int)Math.Round(y + h);
        for (int px = Math.Max(0, x0); px < Math.Min(target.Width, x1); px++)
        {
            target.FillColumn(px, y0, y1, packed);
        }
    }

    /// <summary>
    /// An anti-aliased capsule stroke, walked along its MAJOR axis so a long line costs its length
    /// and not its bounding box.
    /// </summary>
    /// <returns>Whether any pixel was touched.</returns>
    private static bool DrawLine(
        PixelTarget target, double x0, double y0, double x1, double y1, double halfWidth,
        Rgb24 color, double alpha)
    {
        if (!double.IsFinite(x0) || !double.IsFinite(y0) || !double.IsFinite(x1) || !double.IsFinite(y1))
        {
            return false;
        }

        double reach = halfWidth + 1.0;
        // Cheap reject before any per-pixel work.
        if ((Math.Max(x0, x1) < -reach) || (Math.Min(x0, x1) > target.Width + reach)
            || (Math.Max(y0, y1) < -reach) || (Math.Min(y0, y1) > target.Height + reach))
        {
            return false;
        }

        uint packed = target.Encode(color);
        double dx = x1 - x0, dy = y1 - y0;
        double lengthSquared = (dx * dx) + (dy * dy);
        bool painted = false;

        if (lengthSquared <= 1e-9)
        {
            painted |= Splat(target, x0, y0, halfWidth, packed, alpha);
            return painted;
        }

        bool horizontal = Math.Abs(dx) >= Math.Abs(dy);
        double slope = horizontal ? dy / dx : dx / dy;
        double spread = (halfWidth * Math.Sqrt(1.0 + (slope * slope))) + 1.0;

        double aMajor = horizontal ? x0 : y0;
        double bMajor = horizontal ? x1 : y1;
        int first = (int)Math.Floor(Math.Min(aMajor, bMajor) - reach);
        int last = (int)Math.Ceiling(Math.Max(aMajor, bMajor) + reach);
        int limit = horizontal ? target.Width : target.Height;
        first = Math.Max(first, 0);
        last = Math.Min(last, limit - 1);

        for (int major = first; major <= last; major++)
        {
            double m = major + 0.5;
            double t = Math.Clamp(
                horizontal ? (m - x0) / dx : (m - y0) / dy, 0.0, 1.0);
            double centre = horizontal ? y0 + (t * dy) : x0 + (t * dx);
            int minorFirst = (int)Math.Floor(centre - spread);
            int minorLast = (int)Math.Ceiling(centre + spread);
            int minorLimit = horizontal ? target.Height : target.Width;
            minorFirst = Math.Max(minorFirst, 0);
            minorLast = Math.Min(minorLast, minorLimit - 1);

            for (int minor = minorFirst; minor <= minorLast; minor++)
            {
                double px = horizontal ? m : minor + 0.5;
                double py = horizontal ? minor + 0.5 : m;
                double u = Math.Clamp((((px - x0) * dx) + ((py - y0) * dy)) / lengthSquared, 0.0, 1.0);
                double nx = x0 + (u * dx) - px;
                double ny = y0 + (u * dy) - py;
                double distance = Math.Sqrt((nx * nx) + (ny * ny));
                double coverage = Math.Clamp(halfWidth + 0.5 - distance, 0.0, 1.0) * alpha;
                if (coverage > 0.0)
                {
                    CockpitRenderer.Blend(
                        target, horizontal ? major : minor, horizontal ? minor : major,
                        packed, (byte)Math.Round(coverage * 255.0));
                    painted = true;
                }
            }
        }

        return painted;
    }

    private static bool Splat(
        PixelTarget target, double cx, double cy, double radius, uint packed, double alpha)
    {
        bool painted = false;
        int x0 = (int)Math.Floor(cx - radius - 1), x1 = (int)Math.Ceiling(cx + radius + 1);
        int y0 = (int)Math.Floor(cy - radius - 1), y1 = (int)Math.Ceiling(cy + radius + 1);
        for (int x = Math.Max(0, x0); x <= Math.Min(target.Width - 1, x1); x++)
        {
            for (int y = Math.Max(0, y0); y <= Math.Min(target.Height - 1, y1); y++)
            {
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                double coverage =
                    Math.Clamp(radius + 0.5 - Math.Sqrt((dx * dx) + (dy * dy)), 0.0, 1.0) * alpha;
                if (coverage > 0.0)
                {
                    CockpitRenderer.Blend(target, x, y, packed, (byte)Math.Round(coverage * 255.0));
                    painted = true;
                }
            }
        }

        return painted;
    }

    /// <summary>
    /// An anti-aliased circle stroke, scanned as TWO ARCS per column so a 1,000-pixel range ring
    /// costs its circumference and not its bounding box (which at 1920×1080 is four million tests).
    /// </summary>
    private static void DrawRing(
        PixelTarget target, double cx, double cy, double radius, double halfStroke, Rgb24 color,
        double alpha)
    {
        if (!(radius > 0.0))
        {
            return;
        }

        uint packed = target.Encode(color);
        double outer = radius + halfStroke + 1.0;
        double inner = Math.Max(0.0, radius - halfStroke - 1.0);
        int x0 = Math.Max(0, (int)Math.Floor(cx - outer));
        int x1 = Math.Min(target.Width - 1, (int)Math.Ceiling(cx + outer));

        for (int x = x0; x <= x1; x++)
        {
            double dx = x + 0.5 - cx;
            double outerSpan = (outer * outer) - (dx * dx);
            if (outerSpan <= 0.0)
            {
                continue;
            }

            outerSpan = Math.Sqrt(outerSpan);
            double innerSquared = (inner * inner) - (dx * dx);
            double innerSpan = innerSquared > 0.0 ? Math.Sqrt(innerSquared) : 0.0;

            // The two bands the circle crosses this column: [−outer, −inner] and [+inner, +outer].
            // Where the column is inside the hole they are disjoint; where it is not, innerSpan is 0
            // and the two merge into one, which the loop handles by construction.
            Band(target, cx, cy, radius, halfStroke, packed, alpha, x, dx, -outerSpan, -innerSpan);
            if (innerSpan > 0.0)
            {
                Band(target, cx, cy, radius, halfStroke, packed, alpha, x, dx, innerSpan, outerSpan);
            }
        }
    }

    private static void Band(
        PixelTarget target, double cx, double cy, double radius, double halfStroke, uint packed,
        double alpha, int x, double dx, double from, double to)
    {
        int y0 = Math.Max(0, (int)Math.Floor(cy + from));
        int y1 = Math.Min(target.Height - 1, (int)Math.Ceiling(cy + to));
        for (int y = y0; y <= y1; y++)
        {
            double dy = y + 0.5 - cy;
            double distance = Math.Abs(Math.Sqrt((dx * dx) + (dy * dy)) - radius);
            double coverage = Math.Clamp(halfStroke + 0.5 - distance, 0.0, 1.0) * alpha;
            if (coverage > 0.0)
            {
                CockpitRenderer.Blend(target, x, y, packed, (byte)Math.Round(coverage * 255.0));
            }
        }
    }

    private static Rgb24[] Build(IReadOnlyList<Rgb24>? palette)
    {
        Rgb24[] built = new Rgb24[256];
        if (palette is not null)
        {
            for (int i = 0; i < built.Length && i < palette.Count; i++)
            {
                built[i] = palette[i];
            }
        }
        else
        {
            // A palette-less map still has to be legible: the four colours the layer leans on.
            built[MapLook.BackgroundColorIndex] = SceneColors.Default.Ground;
            built[MapLook.RoadColorIndex] = new Rgb24(170, 170, 170);
            built[MapLook.RiverColorIndex] = new Rgb24(0, 0, 170);
            built[MapLook.WaypointColorIndex] = new Rgb24(255, 255, 255);
            built[MapLook.PlayerColorIndex] = new Rgb24(85, 85, 85);
            built[MapLook.FriendlyColorIndex] = new Rgb24(85, 85, 255);
            built[MapLook.HostileColorIndex] = new Rgb24(255, 85, 85);
            built[MapLook.WreckColorIndex] = new Rgb24(170, 0, 0);
        }

        return built;
    }
}
