namespace CYAC.Port.Render;

/// <summary>
/// The pinhole camera the scaffold renders through — an aspect-independent horizontal field of
/// view, resolved to a focal length in pixels per target.
/// </summary>
/// <param name="HorizontalFovDegrees">
/// The horizontal field of view.  Measured — see
/// <see cref="DefaultHorizontalFovDegrees"/>: the original's own projector fixes it at 102.68° for
/// the full-screen 320-pixel window.
/// </param>
public readonly record struct CameraLens(double HorizontalFovDegrees)
{
    /// <summary>
    /// The ORIGINAL's horizontal field of view for its full-screen 320-pixel window: 102.68°.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived from the projector rather than chosen.  CYAC's perspective divide is the routine
    /// <c>gfx_trig_cache_setup @image@0x19524</c> EMITS into
    /// <c>runtime_projection_code_buf image@0x19366</c>; reassembled (and verified 15,090/15,090
    /// against the genuine machine, see
    /// <c>CYAC.Port.Core.Sim.Combat.Player.ObjectScreenProjection</c>) it is
    /// </para>
    /// <code>
    /// screen_x = [0xE634] + ((X &lt;&lt; S) / Z)      screen_y = [0xE636] − ((Y &lt;&lt; S) / Z)
    /// S = g_gfx_zoom_shift [0xE836] = [0xD8A2] + [0xD8A0]   (image@0x016BF..0x016C6 → image@0x1472B)
    /// </code>
    /// <para>
    /// X, Y and Z are the same camera-space units, so the FOCAL LENGTH is exactly <c>2^S</c> pixels
    /// and the field of view is a property of the viewport width, not of the projector.
    /// <c>g_map_zoom_level [0xD8A0]</c> ships as 7 (<c>yeager.cfg@0x20</c> → <c>data/config.json
    /// "mapZoomLevel": 7</c>) and <c>[0xD8A2]</c> is 0 for the plain forward view — which is what
    /// the projection histogram shows: <c>S = 7</c> on 11,982 of 15,090 recorded projections, the rest
    /// zoomed/external.  <c>gfx_viewport_clip_rect_setup @image@0x11876</c> puts the centre at
    /// <c>(x_min + x_max) &gt;&gt; 1</c> = 160 for the full-screen window (the shipped
    /// <c>cockpitHiddenFlag</c> is 1), so
    /// </para>
    /// <code>
    /// hFOV = 2 · atan(160 / 2^7) = 2 · atan(1.25) = 102.6804°
    /// </code>
    /// <para>
    /// The same focal applies to Y, which is why the original's 320×200 image is the usual 1.2×
    /// vertical stretch on a 4:3 display (<c>2·atan(100/128)</c> = 76.0° across three quarters of the
    /// width).  The port renders SQUARE pixels and preserves the horizontal field exactly; with the
    /// cockpit art drawn, the same lens is simply cropped to a smaller window.  <c>--fov</c> is the
    /// knob.
    /// </para>
    /// </remarks>
    public const double DefaultHorizontalFovDegrees = 102.68038348415;

    /// <summary>The original's projector shift for the plain forward view: <c>[0xD8A0]</c> = 7.</summary>
    public const int OriginalZoomShift = 7;

    /// <summary>The original's full-screen viewport width in pixels: 320.</summary>
    public const int OriginalViewportWidth = 320;

    /// <summary>
    /// The horizontal field of view, in degrees, the original's projector gives a viewport of a
    /// given pixel width at a given zoom shift: <c>2·atan((width/2) / 2^shift)</c>.
    /// </summary>
    /// <param name="viewportWidthPixels">The 3-D window's width in ORIGINAL pixels.</param>
    /// <param name="zoomShift">The projector's <c>g_gfx_zoom_shift [0xE836]</c>.</param>
    public static double OriginalFovDegrees(
        int viewportWidthPixels = OriginalViewportWidth, int zoomShift = OriginalZoomShift) =>
        2.0 * Math.Atan(viewportWidthPixels / 2.0 / (1 << zoomShift)) * 180.0 / Math.PI;

    /// <summary>The default lens.</summary>
    /// <remarks>
    /// Spelled out rather than <c>new()</c>: a record struct's implicit parameterless constructor
    /// skips the primary constructor and would leave the field at zero — an infinite focal length.
    /// </remarks>
    public static CameraLens Default { get; } = new(DefaultHorizontalFovDegrees);

    /// <summary>Focal length in pixels for a target of a given width.</summary>
    /// <param name="width">The target's width in pixels.</param>
    /// <exception cref="InvalidOperationException">The field of view is not in (0°, 180°).</exception>
    public double FocalLengthPixels(int width)
    {
        if (!(HorizontalFovDegrees > 0.0 && HorizontalFovDegrees < 180.0))
        {
            throw new InvalidOperationException(
                $"horizontal field of view must be in (0, 180) degrees, got {HorizontalFovDegrees}");
        }

        return width / (2.0 * Math.Tan(HorizontalFovDegrees * Math.PI / 360.0));
    }
}

/// <summary>What one <see cref="HorizonRenderer.Render"/> drew — for tests and telemetry.</summary>
/// <param name="SkyPixels">Pixels filled with <see cref="SceneColors.Sky"/>.</param>
/// <param name="GroundPixels">Pixels filled with <see cref="SceneColors.Ground"/>.</param>
/// <param name="BlendedPixels">Pixels on the split line that got a coverage blend of the two.</param>
/// <param name="CentreRow">
/// The row the horizon crosses at the target's horizontal centre, in pixels from the top; can lie
/// outside the target.  <see cref="double.NaN"/> when the horizon is vertical.
/// </param>
/// <param name="RowsPerColumn">
/// The horizon's slope in rows per column (positive = the line descends to the right).
/// <see cref="double.NaN"/> when the horizon is vertical.
/// </param>
public readonly record struct HorizonFrameStats(
    long SkyPixels,
    long GroundPixels,
    long BlendedPixels,
    double CentreRow,
    double RowsPerColumn);

/// <summary>How the sky/ground transition is painted.</summary>
public enum HorizonStyle
{
    /// <summary>A hard split with a one-pixel coverage blend — H1's scaffold, and the original with
    /// the "Dithered Horizon" option OFF.</summary>
    Flat = 0,

    /// <summary>
    /// The default: the original's own 31-entry palette ramp
    /// (<see cref="SceneColors.RampFirstPaletteIndex"/>), interpolated smoothly across the band in
    /// true colour — represent, don't reproduce.
    /// </summary>
    Refined = 1,

    /// <summary>
    /// The same band, but stepped into the original's 31 discrete palette entries — the A/B that
    /// shows what the 1991 screen actually held.
    /// </summary>
    Classic = 2,
}

/// <summary>
/// The scaffold scene: sky above, ground below, split by the true horizon for the player's attitude.
/// </summary>
/// <remarks>
/// <para>
/// This is the SHAPE the original draws — "the original fills exactly two spans + a slanted split" —
/// but NOT its code: per renderer law D2 the geometry is written from scratch in <c>double</c>, at
/// any resolution, and nothing of the 1991 projector, bit-serial CSD transform or mesh JIT is ported
/// (anti-shimmer law).
/// </para>
/// <para>
/// <b>The geometry.</b>  Take a camera frame with X right, Y up, Z forward.  The world's up vector,
/// expressed in that frame for an aircraft at pitch <c>θ</c> (nose-up positive) and roll <c>φ</c>
/// (right wing down positive), is
/// <code>
/// U = (−sin φ · cos θ,  cos φ · cos θ,  sin θ)
/// </code>
/// The true horizon is the set of view directions perpendicular to <c>U</c>, and the sky is the
/// half-space <c>d · U &gt; 0</c>.  Projecting a direction <c>d = (u, c_y − y, f)</c> through a
/// pinhole of focal length <c>f</c> gives, for every column <c>x</c> (with <c>u = x + ½ − c_x</c>),
/// <code>
/// y_horizon(x) = c_y + (u · U_x + f · U_z) / U_y
/// </code>
/// which is linear in <c>x</c> — one multiply-add per column, no per-pixel trigonometry.  When
/// <c>U_y</c> is zero the aircraft is exactly knife-edge and the horizon is vertical; the sign of
/// <c>u · U_x + f · U_z</c> then decides each column outright.  Yaw does not appear because the true
/// horizon is invariant under rotation about the world's up axis.
/// </para>
/// <para>
/// Sanity, in the two cases anyone can check by hand: level flight puts the line across the middle;
/// a 90° right bank puts <c>U = (−1, 0, 0)</c>, i.e. a vertical split with the ground on the RIGHT —
/// which is where the ground is when the right wing is pointing at it.
/// </para>
/// <para>
/// The scaffold deliberately ignores altitude: the true horizon of a flat earth is at eye level from
/// any height, and CYAC's world IS a flat plane (project memory "terrain RESOLVED: flat plane +
/// named meshes, NO heightmap"), so there is no dip-angle term to get wrong.
/// </para>
/// </remarks>
public static class HorizonRenderer
{
    /// <summary>
    /// How thick the horizon band is, in DEGREES of elevation measured perpendicular to the horizon,
    /// at low altitude: <b>8.93°</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A screen fraction ties the haze to the WINDOW, so the band changed with the resolution, the
    /// aspect ratio and <c>--fov</c>.  It is a property of the LENS: the original draws it at one
    /// fixed field of view and it is an angular height.
    /// </para>
    /// <para>
    /// The number is H3b's own measurement, converted through the original's own focal length.
    /// a captured frame of the original (532 ft, 41° of bank, "Dithered Horizon" on) shows the
    /// run of ramp indices 225..253 covering 27 rows vertically at 320×200 where the horizon's slope
    /// is −0.876, i.e. <c>27·cos(atan 0.876)</c> ≈ <b>20 scanlines perpendicular</b>; the original's
    /// projector has a focal length of exactly <c>2^7</c> = 128 pixels
    /// (<see cref="CameraLens.DefaultHorizontalFovDegrees"/>), so the band subtends
    /// <c>2·atan(10 / 128)</c> = <b>8.9276°</b>.  Rendered at any resolution or field of view, that
    /// angle is what the port draws.
    /// </para>
    /// </remarks>
    public const double DefaultBandDegrees = 8.92766446;

    /// <summary>
    /// See <see cref="DefaultBandDegrees"/>.
    /// </summary>
    [Obsolete("The band is angular now: use DefaultBandDegrees.")]
    public const double DefaultBandFractionOfHeight = 0.10;

    /// <summary>
    /// The altitude at or below which the band is at its THINNEST, in world units: <b>2,048</b>.
    /// </summary>
    /// <remarks>
    /// <see cref="BandAltitudeScale"/>.
    /// </remarks>
    public const double BandThinnestAltitudeWorldUnits = 2048.0;

    /// <summary>
    /// The altitude at or above which the band stops growing, in world units: <b>8,192</b>.
    /// </summary>
    public const double BandThickestAltitudeWorldUnits = 8192.0;

    /// <summary>
    /// How much thicker the band is at <see cref="BandThickestAltitudeWorldUnits"/> than at
    /// <see cref="BandThinnestAltitudeWorldUnits"/>: <b>62/39</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The H5b brief asked whether the band should "fade with altitude if the ramp's 31 entries imply
    /// it".  The ramp does not imply it — <b>the bytes do</b>, and they say something sharper: the
    /// band is ASYMMETRIC about the horizon and only its GROUND half grows.
    /// </para>
    /// <para>
    /// <c>mesh_billboard_bbox_fill</c> §3b builds the call at <c>image@0x18C3E..0x18CA1</c>.  It
    /// takes the camera's world Y — <c>g_per_mesh_state_cam_world_y_lo/hi [0xE826]:[0xE828]</c>, an
    /// <c>i32</c> in <c>world &lt;&lt; 8</c> position units — arithmetic-shifts it right by 13
    /// (<c>mov al,ah / mov ah,dl / mov dl,dh / shl dh,1 / sbb dh,dh</c> then five
    /// <c>sar dx,1 / rcr ax,1</c>), i.e. <c>altitude_ft / 32</c>, and clamps the result to
    /// <c>[0x40, 0x100]</c> (<c>image@0x18C65..0x18C76</c>) — so it saturates below 2,048 ft and
    /// above 8,192 ft.  That value goes to <c>gfx_scaled_span_dispatch @image@0x18EA6</c> in
    /// <c>BX</c>, where the two perpendicular extents are formed as
    /// <c>muldiv16_signed_shr8(0x20, 0x100)</c> = a constant <b>32</b> and
    /// <c>muldiv16_signed_shr8(0x1E, BX)</c> = <b>30·BX/256 = 7…30</b>
    /// (<c>image@0x18F34</c>/<c>0x18F42</c>), and the first is SUBTRACTED from the band's centre
    /// while the second is ADDED to it (<c>image@0x18F5C</c>/<c>0x18F65</c>).
    /// </para>
    /// <para>
    /// So the perpendicular span runs <c>32</c> one way and <c>7…30</c> the other, total
    /// <b>39 at ≤2,048 ft rising to 62 at ≥8,192 ft</b> — and 62 is exactly the ramp's own payload
    /// length (31 palette indices, each stored twice, <c>image@0x34930</c>), i.e. at height the blit
    /// is 1:1 and lower down the ground half is compressed.  <c>(open)</c>: whether those units are
    /// destination SCANLINES or SOURCE rows is not pinned here — the dispatcher rescales once more
    /// through <c>g_view2d_xform_b</c> — so the port takes the RATIO from the bytes and the absolute
    /// size from the screenshot measurement.
    /// </para>
    /// </remarks>
    public const double BandThickestOverThinnest = 62.0 / 39.0;

    /// <summary>
    /// The multiplier on <see cref="DefaultBandDegrees"/> for a camera altitude.
    /// </summary>
    /// <param name="altitudeWorldUnits">The camera's altitude in world units (= feet).</param>
    /// <returns>1 at or below 2,048 ft, rising linearly to 62/39 at or above 8,192 ft.</returns>
    /// <remarks>See <see cref="BandThickestOverThinnest"/> for the derivation and its `(open)`.</remarks>
    public static double BandAltitudeScale(double altitudeWorldUnits)
    {
        double bx = Math.Clamp(altitudeWorldUnits / 32.0, 64.0, 256.0);   // image@0x18C5F..0x18C76
        return (32.0 + (30.0 * bx / 256.0)) / 39.0;                       // image@0x18F34/0x18F42
    }

    /// <summary>
    /// A thread-local <see cref="BackgroundField"/> so the painter allocates nothing per call.
    /// The field is configured and read on the same thread inside one call.
    /// </summary>
    [ThreadStatic]
    private static BackgroundField? t_field;

    /// <summary>Fills the whole target with the horizon for one frame.</summary>
    /// <param name="target">The pixels to write.</param>
    /// <param name="view">The frame's simulation view.</param>
    /// <param name="lens">The camera.</param>
    /// <param name="colors">The sky / ground pair.</param>
    /// <param name="edges">
    /// <see cref="EdgeMode.Analytic"/> grades the pixels the split crosses by their coverage, which
    /// removes the stair-stepping a slanted split otherwise shows at any resolution;
    /// <see cref="EdgeMode.Hard"/> is the centre test.  Folded into <see cref="EdgeMode"/> in R8 —
    /// R3b §7: the two "both map to the centre test".
    /// </param>
    /// <returns>What was drawn.</returns>
    public static HorizonFrameStats Render(
        in PixelTarget target,
        in FlightSnapshot view,
        CameraLens lens,
        SceneColors colors,
        EdgeMode edges = EdgeMode.Analytic) =>
        Render(target, view.PitchRadians, view.RollRadians, lens, colors, edges);

    /// <summary>
    /// Fills the whole target with the horizon for an arbitrary CAMERA attitude.
    /// </summary>
    /// <param name="target">The pixels to write.</param>
    /// <param name="pitchRadians">The camera's pitch; positive looks up.</param>
    /// <param name="rollRadians">The camera's roll; positive puts the right of the frame down.</param>
    /// <param name="lens">The camera.</param>
    /// <param name="colors">The sky / ground pair.</param>
    /// <param name="edges">analytic coverage on the split, or the centre test.</param>
    /// <returns>What was drawn.</returns>
    /// <remarks>
    /// The overload the external views need: an external camera's attitude is the VIEW ANCHOR's, not
    /// the aircraft's (<c>s_view_anchor [0xD89A/9C/9E]</c>), and a chase camera never banks with the
    /// target.
    /// </remarks>
    public static HorizonFrameStats Render(
        in PixelTarget target,
        double pitchRadians,
        double rollRadians,
        CameraLens lens,
        SceneColors colors,
        EdgeMode edges = EdgeMode.Analytic) =>
        Render(
            target, pitchRadians, rollRadians, lens, colors, HorizonStyle.Flat,
            DefaultBandDegrees, 0.0, edges);

    /// <summary>Fills the whole target, with an explicit band style.</summary>
    /// <param name="target">The pixels to write.</param>
    /// <param name="pitchRadians">The camera's pitch; positive looks up.</param>
    /// <param name="rollRadians">The camera's roll; positive puts the right of the frame down.</param>
    /// <param name="lens">The camera.</param>
    /// <param name="colors">The sky / ground pair and, for a band, the ramp.</param>
    /// <param name="style">How the transition is painted.</param>
    /// <param name="bandDegrees">
    /// The band's thickness perpendicular to the horizon, in DEGREES of elevation at low altitude.
    /// See <see cref="DefaultBandDegrees"/>.
    /// </param>
    /// <param name="altitudeWorldUnits">
    /// The camera's altitude, which grows the band's GROUND half
    /// (<see cref="BandAltitudeScale"/>, <c>image@0x18C3E</c>).
    /// </param>
    /// <param name="edges">
    /// <see cref="EdgeMode.Hard"/> makes the split a centre test, the same switch the fragment resolve
    /// applies to every other edge in the frame.  and the ONLY one: folded in — R3b §7.
    /// </param>
    /// <returns>What was drawn.</returns>
    /// <remarks>
    /// Deleted: a target pixel IS a host pixel since R4, so the parameter had exactly one legal value;
    /// R4 §8 named it dead weight for whoever came next.
    /// </remarks>
    public static HorizonFrameStats Render(
        in PixelTarget target,
        double pitchRadians,
        double rollRadians,
        CameraLens lens,
        SceneColors colors,
        HorizonStyle style,
        double bandDegrees = DefaultBandDegrees,
        double altitudeWorldUnits = 0.0,
        EdgeMode edges = EdgeMode.Analytic)
    {
        BackgroundField field = t_field ??= new BackgroundField();
        field.Configure(
            pitchRadians, rollRadians, lens, colors, style, bandDegrees,
            altitudeWorldUnits, target.Width, target.Height, target.Order, edges);
        Paint(target, field);
        return field.Census();
    }

    /// <summary>
    /// Paints a whole target from the background function, column by column.
    /// </summary>
    /// <param name="target">The pixels.</param>
    /// <param name="field">The frame's background function.</param>
    /// <remarks>
    /// The world path does NOT go through here — there the background is the fragment resolve's
    /// terminal function and no pixel is ever written twice.  This painter exists for the ONE case
    /// that has no display list to push: the host's frames before a scene snapshot exists
    /// (<c>FlightRasterizer</c>'s no-snapshot arm).  Both read the same
    /// <see cref="BackgroundField"/>, so they cannot drift.
    /// </remarks>
    internal static void Paint(in PixelTarget target, BackgroundField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        for (int x = 0; x < target.Width; x++)
        {
            BackgroundColumn column = field.Column(x);
            Span<uint> pixels = target.Column(x);
            int first = Math.Clamp(column.FirstMixed, 0, target.Height);
            int end = Math.Clamp(column.EndMixed, first, target.Height);
            if (first > 0)
            {
                pixels[..first].Fill(column.Above);
            }

            for (int y = first; y < end; y++)
            {
                pixels[y] = field.At(in column, y);
            }

            if (end < target.Height)
            {
                pixels[end..].Fill(column.Below);
            }
        }
    }
}
