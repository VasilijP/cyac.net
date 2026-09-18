using System.Diagnostics;
using System.Globalization;
using CYAC.Port.Core.Model.Cockpit;

namespace CYAC.Port.Render.Cockpit;

/// <summary>What one <see cref="CockpitRenderer.Render"/> drew — for tests and telemetry.</summary>
/// <param name="PanelPixels">Opaque cockpit pixels written from the scaled static art.</param>
/// <param name="BlendedPixels">Edge pixels written with partial coverage.</param>
/// <param name="RegionsDrawn">How many of the ten instrument regions drew anything.</param>
/// <param name="DialsDrawn">How many dial slots drew anything.</param>
/// <param name="ScaleRebuilds">How many times the scaled art has been rebuilt.</param>
/// <param name="Milliseconds">the whole layer's wall time this frame.</param>
/// <param name="PanelMilliseconds">Of which: the cached panel runs and edge blends.</param>
/// <param name="HorizonMilliseconds">Of which: region 0, the artificial-horizon ball.</param>
/// <param name="RegionsMilliseconds">Of which: all ten regions (the ball included).</param>
/// <param name="DialsMilliseconds">Of which: the dial needles and the altimeter readout.</param>
public readonly record struct CockpitFrameStats(
    long PanelPixels,
    long BlendedPixels,
    int RegionsDrawn,
    int DialsDrawn,
    int ScaleRebuilds,
    double Milliseconds = 0.0,
    double PanelMilliseconds = 0.0,
    double HorizonMilliseconds = 0.0,
    double RegionsMilliseconds = 0.0,
    double DialsMilliseconds = 0.0);

/// <summary>
/// The COCKPIT LAYER: the panel, the compositor's alpha channel over the 3-D viewport, the ten
/// instrument regions and the dial needles, drawn at host resolution over a world the host has
/// already rendered into the viewport rows.
/// </summary>
/// <remarks>
/// <para>
/// The original's order is world → panel strips (<c>cockpit_panel_strip_blit @image@0x0DA26</c>) →
/// compositor spans (<c>cockpit_sprites_post_blit @image@0x0E9BB</c>) → regions
/// (<c>cockpit_dispatch_state_changes @image@0x0E735</c>) → dials
/// (<c>cockpit_dial_state_compute_all @image@0x01FCF</c>) → the HUD overlay
/// (<c>hud_per_frame_draw @image@0x0C5A7</c>, H10b's).  This type draws everything but the last, so
/// the HUD can be laid on top afterwards without moving anything.
/// </para>
/// <para>
/// <b>Represent, don't reproduce</b>: the art is scaled and filtered rather than pixel-doubled, the
/// artificial horizon is an anti-aliased ball rather than the 1991 raster, and the needles are
/// anti-aliased vectors.  What IS reproduced exactly is the GEOMETRY: every rectangle, pivot, source
/// point and angle comes from the published tables.
/// </para>
/// <para>
/// One instance owns a cache of the scaled static art — the panel below the viewport plus the
/// compositor's spans over it, resolved to window pixels once and stored as column runs.  A frame is
/// then a set of contiguous column copies plus the small moving parts, which is what keeps the layer
/// inside its frame budget at 1920×1080.
/// </para>
/// </remarks>
public sealed class CockpitRenderer
{
    private const int RunStride = 3;

    private int[] _runs = [];
    private int[] _runOffsets = [0];
    private int _runCount;
    private uint[] _runColors = [];
    private int[] _edgePixels = [];
    private uint[] _edgeColors = [];
    private byte[] _edgeAlpha = [];
    private int _edgeCount;

    private string? _cacheAircraft;
    private int _cacheWidth;
    private int _cacheHeight;
    private CockpitFilter _cacheFilter;
    private CockpitFit _cacheFit;
    private PixelChannelOrder _cacheOrder;
    private int _cacheBlackFromRow;
    private int _rebuilds;

    // One resampled overlay per region: the state icons (3, 4, 5, 9) and the scopes' bezels (1, 2).
    // Both are STATIC art resampled to window pixels, so they belong in a cache beside the panel's
    // rather than in the frame.
    private readonly ScaledOverlay?[] _icons = new ScaledOverlay?[CockpitLayout.RegionCount];
    private readonly ScaledOverlay?[] _bezels = new ScaledOverlay?[CockpitLayout.RegionCount];

    // Both retired — the scale is not open: a scope pixel is 2^(shift+1) FEET
    // (RadarScope.FeetPerPixel, verified against the original to the pixel), and palette 0 is the
    // face's colour only while the radar is OFF.  The live constants are in RadarScope.

    /// <summary>
    /// A blip's radius in design pixels.  The original draws one pixel
    /// (<c>gfx_draw_pixel_clipped</c> through <c>per_frame_object_pixel_render @image@0x0D691</c>;
    /// the radar's colour-select returns <c>BX = 0</c> unconditionally, so a scope blip is never the
    /// two-pixel span the forward-view context can ask for).  At host resolution the port draws that
    /// pixel as a small anti-aliased disc, the same REPRESENT-don't-reproduce rule the needles and
    /// the HUD marks follow.
    /// </summary>
    public const double BlipRadius = 0.9;

    /// <summary>
    /// How far off course region 7's two marks stay lit: <c>0x20</c> BAM = 4°
    /// (<c>image@0x0DFCE</c> / <c>image@0x0DFD6</c>).
    /// </summary>
    public const int CourseMarkBam = 0x20;

    /// <summary>
    /// Their lit colour, palette 12 (<c>image@0x0E045</c> with VGA's sub-mode 6).
    /// </summary>
    public const int CourseMarkPaletteIndex = 12;

    /// <summary>How many times the scaled art has been rebuilt since this renderer was made.</summary>
    public int ScaleRebuilds => _rebuilds;

    /// <summary>
    /// Whether the original paints the cockpit this frame — the gate at
    /// <c>image@0x010AA..0x010CA</c>, byte for byte.
    /// </summary>
    /// <param name="options">The knobs, whose <c>Enabled</c> is the player's <c>[0xE471]</c>.</param>
    /// <param name="state">The frame's state.</param>
    /// <remarks>
    /// <c>[0xE472] = ([0xE471] != 0) &amp;&amp; ([0xE46F] != 0) &amp;&amp; ([0xC32F] == 0)</c>.  The
    /// middle term is bit 0 of the current view's flag byte <c>[0x2BC0 + view]</c>, which is set on
    /// view 0 alone — so the panel is painted in the forward cockpit view and nowhere else, and
    /// F2..F6 fly full-screen just as the external views do.
    /// </remarks>
    public static bool CockpitDrawn(in CockpitOptions options, in CockpitState state) =>
        options.Enabled
        && CockpitLayout.DrawsCockpitInView(state.ViewId)
        && !state.Crashed;

    /// <summary>The window geometry this frame's world and cockpit share.</summary>
    /// <param name="width">The window's width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="art">The aircraft's cockpit art.</param>
    /// <param name="options">The knobs.</param>
    /// <param name="state">The frame's state.</param>
    public static CockpitScale ScaleFor(
        int width, int height, CockpitArt art, in CockpitOptions options, in CockpitState state)
    {
        ArgumentNullException.ThrowIfNull(art);
        return CockpitScale.For(
            width, height, art.Viewport, options.Fit, CockpitDrawn(in options, in state));
    }

    /// <summary>Draws the cockpit over a frame whose world is already in the viewport rows.</summary>
    /// <param name="target">The frame.</param>
    /// <param name="art">The aircraft's cockpit art.</param>
    /// <param name="font">The game's font, for the text instruments.</param>
    /// <param name="state">The frame's state.</param>
    /// <param name="options">The knobs.</param>
    /// <param name="blackFromRow">
    /// The first window row whose underlay is NOT the world but nothing: the host draws the world
    /// into the viewport rows alone, so everything the art leaves open from this row down must read
    /// black.  The black is COMPOSITED INTO THE CACHE (an open pixel becomes a black run, an edge
    /// pixel is pre-blended over black), so a frame is one copy per column instead of the host's
    /// clear-then-copy of the same rows — at 4K that clear was 1.1 ms of the 2.7 ms the cockpit phase
    /// cost, measured with <c>FrameTimingLine</c>.  <c>-1</c> (the default) means the world is under
    /// every row (<c>--cockpit-view screen</c>, and every caller that clears itself).
    /// </param>
    /// <returns>What was drawn.</returns>
    public CockpitFrameStats Render(
        PixelTarget target,
        CockpitArt art,
        CockpitFont font,
        in CockpitState state,
        in CockpitOptions options,
        int blackFromRow = -1)
    {
        ArgumentNullException.ThrowIfNull(art);
        ArgumentNullException.ThrowIfNull(font);

        if (!CockpitDrawn(in options, in state))
        {
            return new CockpitFrameStats(0, 0, 0, 0, _rebuilds);
        }

        CockpitScale scale = CockpitScale.For(
            target.Width, target.Height, art.Viewport, options.Fit, cockpitDrawn: true);
        int blackFrom = blackFromRow < 0 ? int.MaxValue : blackFromRow;
        EnsureScaled(target, art, options, blackFrom);

        // The layer is TIMED per phase, so the frame-time cost of toggling the cockpit can be
        // attributed to a phase rather than guessed at.
        long started = Stopwatch.GetTimestamp();
        long panelPixels = 0;
        for (int i = 0; i < _runCount; i++)
        {
            int x = _runs[(i * RunStride) + 0];
            int y0 = _runs[(i * RunStride) + 1];
            int length = _runs[(i * RunStride) + 2];
            _runColors.AsSpan(_runOffsets[i], length).CopyTo(target.Column(x).Slice(y0, length));
            panelPixels += length;
        }

        // The edge list is built column by column, so one column slice serves a whole run of it.
        int column = -1;
        Span<uint> span = Span<uint>.Empty;
        for (int i = 0; i < _edgeCount; i++)
        {
            int packed = _edgePixels[i];
            int x = packed & 0xFFFF;
            if (x != column)
            {
                column = x;
                span = target.Column(x);
            }

            int y = packed >>> 16;
            span[y] = Mix(_edgeColors[i], span[y], _edgeAlpha[i]);
        }

        long panelDone = Stopwatch.GetTimestamp();
        int regions = DrawRegions(target, art, font, in state, in options, scale, out double horizonMs);
        long regionsDone = Stopwatch.GetTimestamp();
        int dials = DrawDials(target, art, font, in state, in options, scale);
        long dialsDone = Stopwatch.GetTimestamp();
        return new CockpitFrameStats(
            panelPixels, _edgeCount, regions, dials, _rebuilds,
            Stopwatch.GetElapsedTime(started, dialsDone).TotalMilliseconds,
            Stopwatch.GetElapsedTime(started, panelDone).TotalMilliseconds,
            horizonMs,
            Stopwatch.GetElapsedTime(panelDone, regionsDone).TotalMilliseconds,
            Stopwatch.GetElapsedTime(regionsDone, dialsDone).TotalMilliseconds);
    }

    // ---------------------------------------------------------------- the scaled static art

    private void EnsureScaled(PixelTarget target, CockpitArt art, in CockpitOptions options, int blackFromRow)
    {
        if (_cacheAircraft == art.Basename
            && _cacheWidth == target.Width
            && _cacheHeight == target.Height
            && _cacheFilter == options.Filter
            && _cacheFit == options.Fit
            && _cacheOrder == target.Order
            && _cacheBlackFromRow == blackFromRow)
        {
            return;
        }

        Rebuild(target, art, options, blackFromRow);
        _cacheAircraft = art.Basename;
        _cacheWidth = target.Width;
        _cacheHeight = target.Height;
        _cacheFilter = options.Filter;
        _cacheFit = options.Fit;
        _cacheOrder = target.Order;
        _cacheBlackFromRow = blackFromRow;
        _rebuilds++;
    }

    private void Rebuild(PixelTarget target, CockpitArt art, in CockpitOptions options, int blackFromRow)
    {
        CockpitScale scale = CockpitScale.For(
            target.Width, target.Height, art.Viewport, options.Fit, cockpitDrawn: true);

        List<int> runs = new List<int>(1 << 14);
        List<uint> runColors = new List<uint>(1 << 18);
        List<int> edgePixels = new List<int>(1 << 14);
        List<uint> edgeColors = new List<uint>(1 << 14);
        List<byte> edgeAlpha = new List<byte>(1 << 14);

        uint[] column = new uint[target.Height];
        byte[] alpha = new byte[target.Height];
        bool nearest = options.Filter == CockpitFilter.Nearest;

        for (int x = 0; x < target.Width; x++)
        {
            double dx = scale.ToDesignX(x + 0.5);
            for (int y = 0; y < target.Height; y++)
            {
                double dy = scale.ToDesignY(y + 0.5);
                (Rgb24 color, byte coverage) = nearest
                    ? SampleNearest(art, dx, dy)
                    : SampleSmooth(art, dx, dy);
                if (y >= blackFromRow && coverage < 255)
                {
                    // Nothing is under this row: an open pixel IS black and an edge pixel is its
                    // colour over black — both opaque, both part of the run (see Render's remarks).
                    column[y] = coverage == 0 ? 0u : Mix(target.Encode(color), 0u, coverage);
                    alpha[y] = 255;
                    continue;
                }

                column[y] = target.Encode(color);
                alpha[y] = coverage;
            }

            int run = -1;
            for (int y = 0; y < target.Height; y++)
            {
                if (alpha[y] == 255)
                {
                    if (run < 0)
                    {
                        run = y;
                    }

                    runColors.Add(column[y]);
                    continue;
                }

                if (run >= 0)
                {
                    runs.Add(x);
                    runs.Add(run);
                    runs.Add(y - run);
                    run = -1;
                }

                if (alpha[y] > 0)
                {
                    edgePixels.Add(x | (y << 16));
                    edgeColors.Add(column[y]);
                    edgeAlpha.Add(alpha[y]);
                }
            }

            if (run >= 0)
            {
                runs.Add(x);
                runs.Add(run);
                runs.Add(target.Height - run);
            }
        }

        _runs = [.. runs];
        _runCount = runs.Count / RunStride;
        _runColors = [.. runColors];
        _edgePixels = [.. edgePixels];
        _edgeColors = [.. edgeColors];
        _edgeAlpha = [.. edgeAlpha];
        _edgeCount = edgePixels.Count;

        _runOffsets = new int[_runCount + 1];
        int at = 0;
        for (int i = 0; i < _runCount; i++)
        {
            _runOffsets[i] = at;
            at += _runs[(i * RunStride) + 2];
        }

        _runOffsets[_runCount] = at;
    }

    private static (Rgb24 Color, byte Coverage) SampleNearest(CockpitArt art, double dx, double dy)
    {
        int x = (int)Math.Floor(dx);
        int y = (int)Math.Floor(dy);
        return art.IsOpaque(x, y) ? (art.ColorAt(art.IndexAt(x, y)), (byte)255) : (default, (byte)0);
    }

    // Bilinear over the OPAQUE neighbours only, so the cockpit's silhouette gets a soft edge instead
    // of bleeding the world's colours into the art or the art's into the world.
    private static (Rgb24 Color, byte Coverage) SampleSmooth(CockpitArt art, double dx, double dy)
    {
        double fx = dx - 0.5;
        double fy = dy - 0.5;
        int x0 = (int)Math.Floor(fx);
        int y0 = (int)Math.Floor(fy);
        double tx = fx - x0;
        double ty = fy - y0;

        double r = 0.0;
        double g = 0.0;
        double b = 0.0;
        double weight = 0.0;
        for (int j = 0; j < 2; j++)
        {
            for (int i = 0; i < 2; i++)
            {
                double w = (i == 0 ? 1.0 - tx : tx) * (j == 0 ? 1.0 - ty : ty);
                int x = x0 + i;
                int y = y0 + j;
                if (w <= 0.0 || !art.IsOpaque(x, y))
                {
                    continue;
                }

                Rgb24 color = art.ColorAt(art.IndexAt(x, y));
                r += color.R * w;
                g += color.G * w;
                b += color.B * w;
                weight += w;
            }
        }

        if (weight <= 0.0)
        {
            return (default, 0);
        }

        Rgb24 blended = new Rgb24(
            (byte)Math.Clamp(Math.Round(r / weight), 0, 255),
            (byte)Math.Clamp(Math.Round(g / weight), 0, 255),
            (byte)Math.Clamp(Math.Round(b / weight), 0, 255));
        return (blended, (byte)Math.Clamp(Math.Round(weight * 255.0), 0, 255));
    }

    // ---------------------------------------------------------------- the moving parts

    private int DrawRegions(
        PixelTarget target, CockpitArt art, CockpitFont font, in CockpitState state,
        in CockpitOptions options, CockpitScale scale, out double horizonMilliseconds)
    {
        int drawn = 0;
        horizonMilliseconds = 0.0;
        foreach (CockpitRegionLayout region in art.Layout.Regions)
        {
            PanelRect rect = region.RectFor(art.AircraftIndex);
            if (!rect.IsPresent)
            {
                continue;
            }

            if (region.Index == 0)
            {
                long ballStarted = Stopwatch.GetTimestamp();
                bool ball = DrawArtificialHorizon(target, art, rect, in state, in options, scale);
                horizonMilliseconds += Stopwatch.GetElapsedTime(ballStarted).TotalMilliseconds;
                if (ball)
                {
                    drawn++;
                }

                continue;
            }

            bool did = region.Index switch
            {
                1 => DrawScope(target, art, region, rect, in state, scale, in options, rwr: false),
                2 => DrawScope(target, art, region, rect, in state, scale, in options, rwr: true),
                3 => DrawStateIcon(
                    target, art, region, rect, FlapIconState(art, in state), scale, in options),
                4 => DrawStateIcon(
                    target, art, region, rect, state.GearDown ? 1 : 0, scale, in options),
                5 => DrawStateIcon(
                    target, art, region, rect, state.BrakeOn ? 1 : 0, scale, in options),
                6 => DrawWeaponAmmo(target, art, font, rect, in state, scale, in options),
                7 => DrawCompassDots(target, art, region, rect, in state, scale, in options),
                8 => DrawCountermeasures(target, art, font, in state, scale),
                9 => DrawAfterburner(target, art, rect, in state, scale, in options),
                _ => false,
            };

            if (did)
            {
                drawn++;
            }
        }

        return drawn;
    }

    // region_3_flaps_state_fn @image@0x0DD55: the F-86 returns the two-bit composite
    // (flapDown * 2 | gearDown) and indexes its own four-icon table; every other aircraft returns the
    // flap bit alone and indexes the per-aircraft OFF/ON pair.
    private static int FlapIconState(CockpitArt art, in CockpitState state) =>
        art.Basename == "f86"
            ? ((state.FlapsDown ? 2 : 0) | (state.GearDown ? 1 : 0))
            : (state.FlapsDown ? 1 : 0);

    private bool DrawStateIcon(
        PixelTarget target,
        CockpitArt art,
        CockpitRegionLayout region,
        PanelRect rect,
        int iconState,
        CockpitScale scale,
        in CockpitOptions options)
    {
        PanelPoint source;
        if (region.Index == 3 && art.Basename == "f86")
        {
            // image@0x0DD91..0x0DDA4 — the state-indexed table, not the per-aircraft one.
            source = art.Layout.F86GearFlapIcons[Math.Clamp(iconState, 0, 3)];
        }
        else
        {
            IReadOnlyList<PanelPoint>? table = iconState != 0 ? region.OnStateSource : region.OffStateSource;
            if (table is null || table.Count <= art.AircraftIndex)
            {
                return false;
            }

            source = table[art.AircraftIndex];
        }

        BlitDesignRect(target, art, region.Index, rect, source, scale, in options);
        return true;
    }

    // region_9_afterburner_draw_fn @image@0x0E322 — a CONSTANT source column 0x40 and a one-row shift
    // between the off (0x1E) and on (0x1F) icons, MiG-21 only.
    private bool DrawAfterburner(
        PixelTarget target, CockpitArt art, PanelRect rect, in CockpitState state, CockpitScale scale,
        in CockpitOptions options)
    {
        if (art.Basename != "mig21")
        {
            return false;
        }

        BlitDesignRect(
            target, art, 9, rect, new PanelPoint(0x40, state.Afterburner ? 0x1F : 0x1E), scale,
            in options);
        return true;
    }

    // H10a fix pass — the SOURCE is miscv.pic (CockpitArt.MiscAt), the shared instrument-sprite sheet
    // the loader puts in the descriptor at [0x421A]; the aircraft's own picture is the blit's
    // DESTINATION.  See CockpitArt.MiscAt for the byte trail. and it is resampled with the PANEL'S
    // OWN FILTER at the PANEL'S OWN design coordinate. The icon replaces a rectangle of art that
    // Rebuild has already resampled from `scale.ToDesign*(pixel + 0.5)`.  The old tap was CENTRED
    // correctly but was a NEAREST reconstruction — a blocky lever sitting on a bilinear panel — and
    // its rectangle landed on whole window pixels while the art around it was sampled at fractional
    // design positions, which shows up as a small discrepancy.  Feeding the sheet the SAME
    // design coordinate through the SAME bilinear kernel makes an icon pixel and the panel pixel it
    // replaces come from the same place through the same filter, so the two layers register exactly.
    // The kernel is CLAMPED to the icon's own source rectangle (clamp-to-edge), because the sheet
    // packs the sprites edge to edge — an unclamped tap would drag the neighbouring lever's pixels
    // into the icon's outermost column. The rectangle's own edges are treated like the panel's
    // silhouette: the design rect's window bounds are FRACTIONAL, so an edge pixel takes the icon by
    // its fractional coverage over the panel already drawn there (which is the same art, so a
    // half-covered pixel is half of each). Under Nearest nothing of this applies — whole source
    // pixels into whole window pixels, the 1991 look enlarged. The resample is CACHED the way the
    // panel's is (<see cref="ScaledOverlay"/>): its input is the window, the filter and which sprite
    // the state selects, so it is rebuilt when a lever moves or the window changes and a frame is
    // otherwise a copy.
    private void BlitDesignRect(
        PixelTarget target,
        CockpitArt art,
        int regionIndex,
        PanelRect rect,
        PanelPoint source,
        CockpitScale scale,
        in CockpitOptions options)
    {
        ScaledOverlay overlay = _icons[regionIndex] ??= new ScaledOverlay();
        if (!overlay.Matches(art, rect, source.X, source.Y, target, in options))
        {
            BuildIcon(overlay, target, art, rect, source, scale, in options);
            overlay.Remember(art, rect, source.X, source.Y, target, in options);
        }

        overlay.Paint(target);
    }

    private static void BuildIcon(
        ScaledOverlay overlay,
        PixelTarget target,
        CockpitArt art,
        PanelRect rect,
        PanelPoint source,
        CockpitScale scale,
        in CockpitOptions options)
    {
        (int x0, int x1, int y0, int y1) = WindowBounds(target, rect, scale);
        overlay.Resize(x0, x1, y0, y1);
        bool nearest = options.Filter == CockpitFilter.Nearest;

        // The sprite offset is an INTEGER translation of the panel's design space, so the sampler
        // works in the panel's own coordinates — the SAME `scale.ToDesign*(pixel + 0.5)` doubles
        // Rebuild used, never an incrementally stepped approximation of them — and the whole-pixel
        // tap is shifted by the offset at the end.  That is what makes the registration EXACT rather
        // than merely close: the two layers share the fractional part bit for bit.
        int offsetX = source.X - rect.X;
        int offsetY = source.Y - rect.Y;
        double windowLeft = scale.ToWindowX(rect.X);
        double windowRight = scale.ToWindowX(rect.Right);
        double windowTop = scale.ToWindowY(rect.Y);
        double windowBottom = scale.ToWindowY(rect.Bottom);

        int at = 0;
        for (int x = x0; x < x1; x++)
        {
            double dx = scale.ToDesignX(x + 0.5);
            double coverageX = nearest ? 1.0 : EdgeCoverage(x, windowLeft, windowRight);
            for (int y = y0; y < y1; y++, at++)
            {
                double dy = scale.ToDesignY(y + 0.5);
                if (nearest)
                {
                    int sx = (int)Math.Floor(dx) + offsetX;
                    int sy = (int)Math.Floor(dy) + offsetY;
                    overlay.Color[at] = target.Encode(art.ColorAt(art.MiscAt(sx, sy)));
                    overlay.Coverage[at] = 255;
                    continue;
                }

                double coverage = coverageX * EdgeCoverage(y, windowTop, windowBottom);
                if (coverage <= 0.0)
                {
                    overlay.Coverage[at] = 0;
                    continue;
                }

                overlay.Color[at] = target.Encode(
                    SampleSheetSmooth(art, dx, dy, rect, offsetX, offsetY));
                overlay.Coverage[at] = (byte)Math.Round(coverage * 255.0);
            }
        }
    }

    // The scope's BEZEL: the aircraft's own art returned over the instrument wherever the region's
    // overlay mask is set, resampled with the panel's own filter at the panel's own design coordinate
    // so a bezel pixel is bit-identical to the panel pixel beside it.
    private static void BuildBezel(
        ScaledOverlay overlay,
        PixelTarget target,
        CockpitArt art,
        PanelRect rect,
        CockpitMask mask,
        CockpitScale scale,
        in CockpitOptions options)
    {
        (int x0, int x1, int y0, int y1) = WindowBounds(target, rect, scale);
        overlay.Resize(x0, x1, y0, y1);
        bool nearest = options.Filter == CockpitFilter.Nearest;

        int at = 0;
        for (int x = x0; x < x1; x++)
        {
            double dx = scale.ToDesignX(x + 0.5);
            int designX = (int)dx;
            for (int y = y0; y < y1; y++, at++)
            {
                double dy = scale.ToDesignY(y + 0.5);
                if (!mask.At(designX - rect.X, (int)dy - rect.Y))
                {
                    continue;
                }

                (Rgb24 color, byte coverage) = nearest
                    ? SampleNearest(art, dx, dy)
                    : SampleSmooth(art, dx, dy);
                overlay.Color[at] = target.Encode(color);
                overlay.Coverage[at] = coverage;
            }
        }
    }

    /// <summary>
    /// One region's STATIC art, resampled to window pixels once and painted per frame.
    /// </summary>
    /// <remarks>
    /// The state icons and the scope bezels are functions of the aircraft, the window, the filter and
    /// which sprite the state selects — never of the frame — so they are cached exactly the way the
    /// panel is (<see cref="CockpitRenderer.EnsureScaled"/>).  A frame then pays one store per pixel,
    /// plus a blend on the rectangle's fractional edge, instead of a bilinear tap; the arithmetic that
    /// registers the icon with the panel happens on the rebuild, so the pixels are identical either
    /// way.  The layout is COLUMN MAJOR, matching <see cref="PixelTarget.Column"/>.
    /// </remarks>
    private sealed class ScaledOverlay
    {
        private string _aircraft = string.Empty;
        private PanelRect _rect;
        private int _sourceX = int.MinValue;
        private int _sourceY = int.MinValue;
        private int _width;
        private int _height;
        private CockpitFilter _filter;
        private CockpitFit _fit;
        private PixelChannelOrder _order;
        private bool _built;

        /// <summary>The resolved colour of each window pixel of the rectangle.</summary>
        public uint[] Color { get; private set; } = [];

        /// <summary>How much of each of them the rectangle covers, 0…255.</summary>
        public byte[] Coverage { get; private set; } = [];

        private int X0 { get; set; }

        private int X1 { get; set; }

        private int Y0 { get; set; }

        private int Y1 { get; set; }

        /// <summary>Whether the cached pixels still answer for this frame.</summary>
        public bool Matches(
            CockpitArt art, PanelRect rect, int sourceX, int sourceY, PixelTarget target,
            in CockpitOptions options) =>
            _built
            && string.Equals(_aircraft, art.Basename, StringComparison.Ordinal)
            && _rect == rect
            && _sourceX == sourceX
            && _sourceY == sourceY
            && _width == target.Width
            && _height == target.Height
            && _filter == options.Filter
            && _fit == options.Fit
            && _order == target.Order;

        /// <summary>Records what the pixels were built for.</summary>
        public void Remember(
            CockpitArt art, PanelRect rect, int sourceX, int sourceY, PixelTarget target,
            in CockpitOptions options)
        {
            _aircraft = art.Basename;
            _rect = rect;
            _sourceX = sourceX;
            _sourceY = sourceY;
            _width = target.Width;
            _height = target.Height;
            _filter = options.Filter;
            _fit = options.Fit;
            _order = target.Order;
            _built = true;
        }

        /// <summary>Sizes the buffers for a window rectangle, growing them only when it grows.</summary>
        /// <param name="x0">First column.</param>
        /// <param name="x1">One past the last.</param>
        /// <param name="y0">First row.</param>
        /// <param name="y1">One past the last.</param>
        public void Resize(int x0, int x1, int y0, int y1)
        {
            X0 = x0;
            X1 = x1;
            Y0 = y0;
            Y1 = y1;
            int need = Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0);
            if (Color.Length < need)
            {
                Color = new uint[need];
                Coverage = new byte[need];
            }

            Array.Clear(Coverage, 0, need);
        }

        /// <summary>Paints the cached pixels over what is already in the frame.</summary>
        /// <param name="target">The frame.</param>
        public void Paint(PixelTarget target)
        {
            int at = 0;
            for (int x = X0; x < X1; x++)
            {
                Span<uint> column = target.Column(x);
                for (int y = Y0; y < Y1; y++, at++)
                {
                    byte coverage = Coverage[at];
                    if (coverage == 0)
                    {
                        continue;
                    }

                    column[y] = coverage == 255
                        ? Color[at]
                        : Mix(Color[at], column[y], coverage);
                }
            }
        }
    }

    /// <summary>
    /// How much of one window pixel a design edge pair covers, along one axis — the icon rectangle's
    /// fractional edge (R6).
    /// </summary>
    /// <param name="pixel">The window pixel's index; it spans <c>[pixel, pixel + 1)</c>.</param>
    /// <param name="low">The rectangle's low edge in window pixels.</param>
    /// <param name="high">Its high edge.</param>
    private static double EdgeCoverage(int pixel, double low, double high) =>
        Math.Clamp(Math.Min(pixel + 1.0, high) - Math.Max(pixel, low), 0.0, 1.0);

    // The sprite sheet's bilinear tap, the SAME kernel as SampleSmooth (same footprint, same
    // weights, same accumulate-and-divide rounding, so an icon pixel over identical art is
    // bit-identical to the panel pixel it replaces), with two differences:
    //     • the design coordinate is the PANEL'S — the tap is clamped to the icon's own rectangle in
    //       panel space and only then translated onto the sheet by the sprite's integer offset;
    //     • the opacity gate becomes a CLAMP.  The sheet has no alpha and its sprites touch (the
    //       P-51's gear, flaps and brake icons are three 16-wide sprites at x = 0, 16, 32 of one
    //       row-block), so clamp-to-edge is what stops a neighbour bleeding into the outermost
    //       column.
    private static Rgb24 SampleSheetSmooth(
        CockpitArt art, double dx, double dy, PanelRect rect, int offsetX, int offsetY)
    {
        double fx = dx - 0.5;
        double fy = dy - 0.5;
        int x0 = (int)Math.Floor(fx);
        int y0 = (int)Math.Floor(fy);
        double tx = fx - x0;
        double ty = fy - y0;

        double r = 0.0;
        double g = 0.0;
        double b = 0.0;
        double weight = 0.0;
        for (int j = 0; j < 2; j++)
        {
            for (int i = 0; i < 2; i++)
            {
                double w = (i == 0 ? 1.0 - tx : tx) * (j == 0 ? 1.0 - ty : ty);
                if (w <= 0.0)
                {
                    continue;
                }

                int x = Math.Clamp(x0 + i, rect.X, rect.Right - 1) + offsetX;
                int y = Math.Clamp(y0 + j, rect.Y, rect.Bottom - 1) + offsetY;
                Rgb24 color = art.ColorAt(art.MiscAt(x, y));
                r += color.R * w;
                g += color.G * w;
                b += color.B * w;
                weight += w;
            }
        }

        if (weight <= 0.0)
        {
            return default;
        }

        return new Rgb24(
            (byte)Math.Clamp(Math.Round(r / weight), 0, 255),
            (byte)Math.Clamp(Math.Round(g / weight), 0, 255),
            (byte)Math.Clamp(Math.Round(b / weight), 0, 255));
    }

    // region_0_horizon_draw_fn @image@0x0E0AE draws two analog arcs for the props and a filled
    // pitch-ladder polygon for the jets.  The port REPRESENTS both with one anti-aliased attitude
    // ball inside the region's own rectangle, clipped by the aircraft's `_horiz` mask, which is what
    // gives the instrument its round window. with InstrumentWindow.Analytic the round window is a
    // circle FITTED to the mask's clear pixels (InstrumentCircle.Fit) and its rim is anti-aliased at
    // window resolution, so the ball's edge is no longer the 320×200 bitmap's staircase.  A mask
    // whose clear region is not round keeps the bitmap.
    private static bool DrawArtificialHorizon(
        PixelTarget target, CockpitArt art, PanelRect rect, in CockpitState state,
        in CockpitOptions options, CockpitScale scale)
    {
        art.Masks.TryGetValue("horiz", out CockpitMask? mask);
        InstrumentCircle? circle = options.Window == InstrumentWindow.Analytic && mask is not null
            ? InstrumentCircle.Fit(mask, rect)
            : null;
        Rgb24 sky = art.Palette[SceneColors.SkyPaletteIndex];
        Rgb24 ground = art.Palette[SceneColors.GroundPaletteIndex];
        Rgb24 bar = art.Palette[15];

        double roll = state.RollBam * 2.0 * Math.PI / DialNeedle.FullTurnBam;
        // Pitch slides the split line; a quarter of the ball's height per 45° keeps the whole ±90°
        // range inside the window the mask cuts.
        double pitchRows = SignedBam(state.PitchBam) / (double)DialNeedle.FullTurnBam * 4.0 * rect.Height;

        double cx = rect.X + (rect.Width / 2.0);
        double cy = rect.Y + (rect.Height / 2.0);
        double sin = Math.Sin(roll);
        double cos = Math.Cos(roll);

        (int x0, int x1, int y0, int y1) = WindowBounds(target, rect, scale);
        double stepY = 1.0 / scale.ScaleY;
        double firstY = scale.ToDesignY(y0 + 0.5);
        for (int x = x0; x < x1; x++)
        {
            double dx = scale.ToDesignX(x + 0.5);
            int maskX = (int)dx - rect.X;
            Span<uint> column = target.Column(x);
            double dy = firstY;
            for (int y = y0; y < y1; y++, dy += stepY)
            {
                // The mask's SET bits are the bezel the overlay blit puts back over the instrument
                // (gfx_masked_blit @image@0x1D162), so the instrument itself shows through the
                // CLEAR ones — data/images/masks/51_horiz.png is a dark dome on a set surround.
                double rim = 1.0;
                if (circle is { } c)
                {
                    // Coverage of the window disc over this pixel, one WINDOW pixel wide.  The bitmap
                    // still cuts where it is more than a pixel INSIDE the circle (a dome's flat edge).
                    double d = Math.Sqrt(((dx - c.X) * (dx - c.X)) + ((dy - c.Y) * (dy - c.Y)));
                    rim = Math.Clamp(((c.Radius - d) * scale.ScaleY) + 0.5, 0.0, 1.0);
                    if (rim <= 0.0
                        || (d < c.Radius - 1.0 && mask is not null && mask.At(maskX, (int)dy - rect.Y)))
                    {
                        continue;
                    }
                }
                else if (mask is not null && mask.At(maskX, (int)dy - rect.Y))
                {
                    continue;
                }

                double u = ((dx - cx) * sin) + ((dy - cy) * cos) + pitchRows;
                Rgb24 color = u switch
                {
                    < -0.5 => sky,
                    > 0.5 => ground,
                    _ => Mix(sky, ground, Math.Clamp(u + 0.5, 0.0, 1.0)),
                };

                // The horizon BAR anti-aliased one window pixel wide (it was a hard 0.35
                // design-unit band, a staircase at 6×).
                double barCoverage = circle is not null
                    ? Math.Clamp(((0.35 - Math.Abs(u)) * scale.ScaleY) + 0.5, 0.0, 1.0)
                    : (Math.Abs(u) < 0.35 ? 1.0 : 0.0);
                if (barCoverage > 0.0)
                {
                    color = Mix(color, bar, barCoverage);
                }

                if (rim < 1.0)
                {
                    column[y] = Mix(target.Encode(color), column[y], (byte)Math.Round(rim * 255.0));
                }
                else
                {
                    column[y] = target.Encode(color);
                }
            }
        }

        return true;
    }

    // THE RADAR MONITOR (region 1, image@0x0E212) and THE RWR (region 2, image@0x0E186).
    //
    // Those two words are not buffers at all: they are the two contexts' one-word tick_deadline
    // slots in the shared pixel-obj engine (per_frame_object_pixel_setup reads *arg1 into [0x3254]
    // @image@0x0D6F1 and writes it back @image@0x0D775).  Both scopes are a TOP-DOWN ground-plane
    // plot through pixel_obj_world_to_screen_project @image@0x0D3B4, with the own ship at the
    // region's own pivot — near the BOTTOM of the radar's face, which is what makes it a forward
    // cone, and at the CENTRE of the RWR's, which is why the RWR sees all round.  The law and
    // every constant live in RadarScope.cs.
    //
    // The frame is: clear the face (palette 0 with the radar off, palette [0x325F] = 2 otherwise),
    // plot, then let the aircraft's own bezel mask come back over the top.
    private bool DrawScope(
        PixelTarget target,
        CockpitArt art,
        CockpitRegionLayout region,
        PanelRect rect,
        in CockpitState state,
        CockpitScale scale,
        in CockpitOptions options,
        bool rwr)
    {
        art.Masks.TryGetValue(rwr ? "rwr" : "radar", out CockpitMask? mask);
        RadarState radar = state.Radar;
        Rgb24 blip = art.Palette[RadarScope.BlipPaletteIndex];
        Rgb24 ownShip = art.Palette[RadarScope.OwnShipPaletteIndex];

        // THE FACE.  per_frame_object_pixel_render's step 0 fills the whole clip rect with the
        // context's SI=0 colour every frame (image@0x0D611..0x0D628), which for BOTH scopes is
        // [0x325F] = palette 2 (image@0x0D360 / image@0x0D37A).  With the radar OFF the monitor's own
        // sweep arm gets there first and fills the same rectangle with 0xFF00 = palette 0
        // (image@0x0E246..0x0E25A), and the blip arm never runs — which is the whole reason the scope
        // reads as "black when off". Region 2 has no fill of its OWN; the shared render pass fills
        // it.  Measured: 219 px of palette 2 inside (200,154,24,17) at every captured tick,
        // a captured frame of the original through FillDesignRect, so the face's own rectangle gets
        // the panel's fractional edge under Smooth instead of a hard step (R6 audit row 4).
        bool lit = rwr || radar.On;
        FillDesignRect(
            target,
            scale,
            rect,
            art.Palette[lit ? RadarScope.FacePaletteIndex : RadarScope.OffPaletteIndex],
            options.Filter);

        // THE CRT COLLAPSE.  Switching the radar off arms [0xBCAA/AC] = frame_time + 469
        // (image@0x0E1F6) and the sweep arm walks the three shapes as si climbs to 469.
        if (!rwr && radar.Sweeping)
        {
            DrawSweep(target, scale, art, region, rect, radar.SweepPhase, in options);
        }

        // THE PLOT.  The own ship sits at the region's PIVOT, which is the screen centre the region
        // hands the pixel-obj engine ([0x3F10]/[0x3F12] for the radar, [0x3F84]/[0x3F86] for the RWR)
        // — measured (177,190) and (210,162) on the MiG-21, i.e. the radar's is one row above the
        // bottom of a 33-row face and the RWR's is its centre.  The engine draws it as a single
        // palette-15 pixel every frame, unconditionally (image@0x0D6CE).
        PanelPoint pivot = region.Pivots is { } pivots && pivots.Count > art.AircraftIndex
            ? pivots[art.AircraftIndex]
            : new PanelPoint(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));

        // With the radar off nothing is plotted: the sweep arm returns before the blip arm's
        // per_frame_object_pixel_setup call is ever reached (image@0x0E219's je).
        if (lit)
        {
            foreach (ScopeContact contact in radar.Contacts ?? [])
            {
                bool shown = rwr
                    ? RadarScope.RwrShows(
                        in contact, rect.Width, rect.Height, radar.ScaleShift, radar.RwrSampleCounter)
                    : RadarScope.RadarShows(in contact, rect.Width, rect.Height, radar.ScaleShift);
                if (!shown)
                {
                    continue;
                }

                (double bx, double by) = RadarScope.Plot(in contact, pivot.X, pivot.Y, radar.ScaleShift);

                // The render pass clips the projected PIXEL against the region rectangle
                // (image@0x0D66B..0x0D67E) and drops it whole; the port tests the same point.
                if (bx < rect.X || bx > rect.Right || by < rect.Y || by > rect.Bottom)
                {
                    continue;
                }

                FillDesignDisc(target, scale, bx + 0.5, by + 0.5, BlipRadius, blip);
            }

            FillDesignDisc(target, scale, pivot.X + 0.5, pivot.Y + 0.5, BlipRadius, ownShip);
        }

        // The mask's SET bits are the bezel the overlay blit puts back over the instrument, so the
        // aircraft's own art returns on top of the face (same polarity as the horizon's, §D5). it is
        // painted LAST, after the plot: in the original the panel art composites back over the whole
        // region, which is what masks the rectangular face and its blips to the round CRT (measured —
        // the lit face is a circle of radius rect.h/2 about the pivot's column and the face's centre
        // row, and the sweep's band is that circle's chord). and it returns through the PANEL'S OWN
        // sampler, so a bezel pixel is bit-identical to the panel pixel around it: the old nearest
        // tap of art.IndexAt made the bezel a blocky ring inside a bilinear panel, the same
        // misregistration the state icons had.  The row coordinate is `scale.ToDesignY(y + 0.5)` —
        // what Rebuild used — and not the stepped approximation of it, so "identical" means
        // bit-identical.  The MASK decision itself stays a whole-pixel test: it is a 1-bit bitmap,
        // not art.  Like the state icons the bezel is static art, so it is resampled into a cached
        // overlay and only painted per frame.
        if (mask is not null)
        {
            ScaledOverlay bezel = _bezels[region.Index] ??= new ScaledOverlay();
            if (!bezel.Matches(art, rect, -1, -1, target, in options))
            {
                BuildBezel(bezel, target, art, rect, mask, scale, in options);
                bezel.Remember(art, rect, -1, -1, target, in options);
            }

            bezel.Paint(target);
        }

        return true;
    }

    /// <summary>
    /// The CRT collapse the <b>R</b> key plays when the radar goes off:
    /// <c>region_1_radar_monitor_draw_fn</c>'s sweep arm (<c>image@0x0E27A..0x0E303</c>), whose three
    /// shapes are a full-width band that flattens, a centre line that shortens, and one pixel.
    /// </summary>
    private static void DrawSweep(
        PixelTarget target,
        CockpitScale scale,
        CockpitArt art,
        CockpitRegionLayout region,
        PanelRect rect,
        int phase,
        in CockpitOptions options)
    {
        Rgb24 colour = art.Palette[RadarScope.FacePaletteIndex];
        int centreY = RadarScope.FaceCentreRow(rect.Y, rect.Height);
        (int halfHeight, int halfWidth, int which) = RadarScope.SweepShape(phase, rect.Width, rect.Height);

        // The sweep's X centre is the region's own pivot — the same word [0x3F10] the engine takes as
        // its screen centre (image@0x0E2E2 / image@0x0E2FA), NOT the rectangle's middle.
        int centreX = region.Pivots is { } pivots && pivots.Count > art.AircraftIndex
            ? pivots[art.AircraftIndex].X
            : rect.X + (rect.Width / 2);

        switch (which)
        {
            case 0:
                FillDesignRect(
                    target,
                    scale,
                    new PanelRect(rect.X, centreY - halfHeight, rect.Width, (2 * halfHeight) + 1),
                    colour,
                    options.Filter);
                break;

            case 1:
                FillDesignRect(
                    target,
                    scale,
                    new PanelRect(centreX - halfWidth, centreY, (2 * halfWidth) + 1, 1),
                    colour,
                    options.Filter);
                break;

            default:
                FillDesignRect(
                    target, scale, new PanelRect(centreX, centreY, 1, 1), colour, options.Filter);
                break;
        }
    }

    // region_7_compass_draw_fn @image@0x0DFF1 — a baseline across the rectangle and two 2×2 marks at
    // the per-aircraft pivot [0x4146 + 4·idx], lit by the two high bits of the state word.
    // Those bits are a COURSE-DEVIATION pair, and the quantity behind them is the
    //    RELATIVE BEARING to the nav slot, not the bank angle.  region_7_compass_state_fn
    //    @image@0x0DFB4..0x0DFDD, for the two props only:
    //        di = object_bearing_to_slot_compute(AL = 1)          // the relative bearing
    //        si  = (di <= +0x20 ? 0x4000 : 0) | (di >= -0x20 ? 0x8000 : 0)
    //        return (heading >> 4) | si
    //    and the draw arm lights the LEFT mark (pivot.x − 3) on 0x8000 and the RIGHT one
    //    (pivot.x + 2) on 0x4000 (image@0x0E071 / 0x0E095), so both are lit inside ±4° of the
    //    course and one goes out as the needle swings off it.  The lit colour is palette 12 in VGA
    //    (image@0x0E045: `cmp word [0x015E],1 / sbb di,di / and di,3 / add di,0xFF0C` — sub-mode 6
    //    leaves 0xFF0C; only sub-mode 0 gets palette 15) and the unlit colour is palette 0
    //    (image@0x0E07B)..
    private static bool DrawCompassDots(
        PixelTarget target,
        CockpitArt art,
        CockpitRegionLayout region,
        PanelRect rect,
        in CockpitState state,
        CockpitScale scale,
        in CockpitOptions options)
    {
        IReadOnlyList<PanelPoint>? pivots = region.Pivots;
        if (pivots is null || pivots.Count <= art.AircraftIndex)
        {
            return false;
        }

        PanelPoint pivot = pivots[art.AircraftIndex];
        if (pivot is { X: 0, Y: 0 })
        {
            return false;
        }

        Rgb24 lit = art.Palette[CourseMarkPaletteIndex];
        Rgb24 dark = art.Palette[0];
        int drift = state.BearingPointerBam;

        // The marks and the baseline take the PANEL'S edge treatment: at 1080p a "2 × 2 design pixel"
        // mark is 12 × 11 window pixels, and a hard rectangle beside a coverage-ramped panel is
        // exactly the disease R6 §5.4 named (audit rows 9/11/14).
        FillDesignRect(
            target, scale, new PanelRect(pivot.X - 3, pivot.Y + 10, 2, 2),
            drift >= -CourseMarkBam ? lit : dark, options.Filter);
        FillDesignRect(
            target, scale, new PanelRect(pivot.X + 2, pivot.Y + 10, 2, 2),
            drift <= CourseMarkBam ? lit : dark, options.Filter);
        FillDesignRect(
            target, scale, new PanelRect(rect.X, rect.Y + rect.Height - 1, rect.Width, 1),
            art.Palette[15], options.Filter);
        return true;
    }

    // region_6_draw_fn @image@0x0DF28 — clear-fill the rectangle with the aircraft's background byte,
    // then print "%4d" (F-86) or "%4s %4d" (everyone else) in its foreground byte.  The number is the
    // selected slot's ROUNDS: the state function's [bx - 0x12D4] with bx = 2 * [0xED2A] is
    // g_hud_weapon_slot_ammo [0xED2C], not a hit probability.
    private static bool DrawWeaponAmmo(
        PixelTarget target,
        CockpitArt art,
        CockpitFont font,
        PanelRect rect,
        in CockpitState state,
        CockpitScale scale,
        in CockpitOptions options)
    {
        byte background = Style(art.Layout.WeaponAmmoBackgroundColor, art.AircraftIndex);
        byte foreground = Style(art.Layout.WeaponAmmoTextColor, art.AircraftIndex);
        // The background panel takes the panel's own edge treatment (R6 audit row 9).
        FillDesignRect(target, scale, rect, art.Palette[background], options.Filter);
        if (state.WeaponRounds < 0)
        {
            return true;
        }

        string text = art.Basename == "f86"
            ? state.WeaponRounds.ToString(CultureInfo.InvariantCulture).PadLeft(4)
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{(state.WeaponName ?? art.Strings.EmptyWeaponName).PadLeft(4)} {state.WeaponRounds,4}");

        DrawText(target, font, scale, text, rect.X, rect.Y, art.Palette[foreground]);
        return true;
    }

    // region_8_chaff_flare_draw_fn @image@0x0DEA7 — two zero-padded two-digit counts at the
    // aircraft's own text positions.
    private static bool DrawCountermeasures(
        PixelTarget target, CockpitArt art, CockpitFont font, in CockpitState state, CockpitScale scale)
    {
        if (!art.Layout.CountermeasureText.TryGetValue(art.Basename, out (PanelPoint Chaff, PanelPoint Flare) where))
        {
            return false;
        }

        Rgb24 color = art.Palette[15];
        DrawText(
            target, font, scale, state.ChaffCount.ToString("D2", CultureInfo.InvariantCulture),
            where.Chaff.X, where.Chaff.Y, color);
        DrawText(
            target, font, scale, state.FlareCount.ToString("D2", CultureInfo.InvariantCulture),
            where.Flare.X, where.Flare.Y, color);
        return true;
    }

    // H10a fix pass — EVERY present slot is NEEDLES.  dial_slot_lines_draw @image@0x0195C draws
    // slot[+0x14] lines from the pivot to each keyframe endpoint, colouring line i with BYTE i of
    // the "kind word" (image@0x01985: `mov al,[bx+si+0x0C]`).  Needle 0 takes the slot's own value
    // and the rest take the SECONDARY value (dial_slot_value_compute walks the keyframe array
    // backwards and only the FIRST entry reads [bp+6], image@0x0217C..0x02188).
    private static int DrawDials(
        PixelTarget target, CockpitArt art, CockpitFont font, in CockpitState state,
        in CockpitOptions options, CockpitScale scale)
    {
        int drawn = 0;
        foreach (DialSlot slot in art.Dials)
        {
            if (!slot.IsDrawn)
            {
                continue;
            }

            for (int needle = 0; needle < slot.NeedleCount; needle++)
            {
                int value = needle == 0
                    ? state.DialValue(slot.Slot)
                    : state.DialSecondary(slot.Slot);
                (double tx, double ty) = DialNeedle.Tip(in slot, DialNeedle.Angle(in slot, value));
                // The pivot and the keyframe endpoints are pixel ADDRESSES; the original's line
                // filler paints those pixels, so the geometric needle runs between pixel CENTRES:
                // +0.5 (CockpitOptions.NeedlePivotNudge*) — a nudge left and down, because drawing
                // from the pixel's CORNER puts the needle half a pixel off.
                double px = slot.Pivot.X + options.NeedlePivotNudgeX;
                double py = slot.Pivot.Y + options.NeedlePivotNudgeY;
                tx += options.NeedlePivotNudgeX;
                ty += options.NeedlePivotNudgeY;
                if (options.Needles == NeedleStyle.Tapered)
                {
                    // A needle, not a hair: NeedleHubHalfWidth at the pivot tapering to
                    // NeedleTipHalfWidth, over a hub disc, all in DESIGN units so the proportions
                    // hold at every window size.
                    DrawDesignNeedle(
                        target, scale, px, py, tx, ty,
                        NeedleHubHalfWidth, NeedleTipHalfWidth,
                        art.Palette[slot.NeedleColor(needle)], slot.Rect);
                }
                else
                {
                    DrawDesignLine(
                        target, scale, px, py, tx, ty,
                        art.Palette[slot.NeedleColor(needle)], slot.Rect);
                }
            }

            drawn++;
        }

        // The ONE numeric readout in the cockpit: the altimeter's THOUSANDS drum.  It is not a "kind"
        // — cockpit_dial_state_compute_all arms the far callback [0xECC8]/[0xECCA] =
        // dial_numeric_text_blit @image@0x01C80 on SLOT 0 alone (image@0x0200A), and only on the
        // frame the thousands digit changes (image@0x01FF8 against [0xB164]). and it is drawn at the
        // slot's PIVOT with a per-aircraft nudge, three digits zero-padded, not at the rectangle's
        // corner (DialNumericReadout, image@0x01CBA..0x01D46). The port used to put a bare "0"
        // outside the F-86's altimeter bezel.
        if (art.Dials.Count > DialNumericReadout.Slot && art.Dials[DialNumericReadout.Slot].IsDrawn)
        {
            DialSlot altimeter = art.Dials[DialNumericReadout.Slot];
            PanelPoint origin = DialNumericReadout.Origin(in altimeter, art.AircraftIndex);
            DrawText(
                target, font, scale,
                DialNumericReadout.Text(state.AltitudeThousands, art.AircraftIndex),
                origin.X, origin.Y,
                art.Palette[altimeter.NeedleColor(0)]);
        }

        return drawn;
    }

    // ---------------------------------------------------------------- drawing primitives

    private static byte Style(IReadOnlyList<byte> table, int index) =>
        index < table.Count ? table[index] : (byte)15;

    private static int SignedBam(int bam)
    {
        int wrapped = DialNeedle.Wrap(bam);
        return wrapped >= DialNeedle.FullTurnBam / 2 ? wrapped - DialNeedle.FullTurnBam : wrapped;
    }

    private static Rgb24 Mix(Rgb24 a, Rgb24 b, double t) => new(
        (byte)Math.Clamp(Math.Round((a.R * (1.0 - t)) + (b.R * t)), 0, 255),
        (byte)Math.Clamp(Math.Round((a.G * (1.0 - t)) + (b.G * t)), 0, 255),
        (byte)Math.Clamp(Math.Round((a.B * (1.0 - t)) + (b.B * t)), 0, 255));

    internal static void Blend(PixelTarget target, int x, int y, uint color, byte alpha)
    {
        if ((uint)x >= (uint)target.Width || (uint)y >= (uint)target.Height)
        {
            return;
        }

        Span<uint> column = target.Column(x);
        column[y] = Mix(color, column[y], alpha);
    }

    /// <summary>Alpha-blends two packed pixels, channel by channel.</summary>
    private static uint Mix(uint over, uint under, byte alpha)
    {
        uint mixed = 0;
        for (int shift = 0; shift < 24; shift += 8)
        {
            int a = (int)((over >> shift) & 0xFF);
            int b = (int)((under >> shift) & 0xFF);
            mixed |= (uint)(((a * alpha) + (b * (255 - alpha)) + 127) / 255) << shift;
        }

        return mixed;
    }

    internal static (int X0, int X1, int Y0, int Y1) WindowBounds(
        PixelTarget target, PanelRect rect, CockpitScale scale) =>
        (Math.Max(0, (int)Math.Floor(scale.ToWindowX(rect.X))),
         Math.Min(target.Width, (int)Math.Ceiling(scale.ToWindowX(rect.Right))),
         Math.Max(0, (int)Math.Floor(scale.ToWindowY(rect.Y))),
         Math.Min(target.Height, (int)Math.Ceiling(scale.ToWindowY(rect.Bottom))));

    /// <summary>
    /// Fills a DESIGN-space rectangle, with a fractional edge under
    /// <see cref="CockpitFilter.Smooth"/>.
    /// </summary>
    /// <param name="target">The frame.</param>
    /// <param name="scale">The design → window mapping.</param>
    /// <param name="rect">The rectangle, in design pixels.</param>
    /// <param name="color">What to paint it.</param>
    /// <param name="filter">
    /// The panel's own reconstruction filter.  <see cref="CockpitFilter.Nearest"/> keeps the whole
    /// window pixels this always painted; <see cref="CockpitFilter.Smooth"/> grades the four boundary
    /// pixels by the fraction of each the rectangle covers, the same
    /// <see cref="EdgeCoverage"/> the state icons got in R6.
    /// </param>
    /// <remarks>
    /// R6 §5.4 named this "the next visible thing in this layer": the rectangle was painted into
    /// <c>floor(left) … ceil(right)</c>, so it grew or shrank by up to a whole window pixel and had a
    /// HARD edge where the panel around it is a coverage ramp — visible on the compass marks, the
    /// ammo background, the radar face and the Classic HUD's runs.  The interior is untouched (it is
    /// a solid colour and <see cref="PixelTarget.FillColumn"/> is what makes a full-screen radar face
    /// cheap); only the boundary pixels blend, and a rectangle whose edges land on whole window
    /// pixels is bit-identical to what this painted before.
    /// </remarks>
    internal static void FillDesignRect(
        PixelTarget target,
        CockpitScale scale,
        PanelRect rect,
        Rgb24 color,
        CockpitFilter filter = CockpitFilter.Nearest)
    {
        uint packed = target.Encode(color);
        (int x0, int x1, int y0, int y1) = WindowBounds(target, rect, scale);
        if (filter == CockpitFilter.Nearest)
        {
            for (int x = x0; x < x1; x++)
            {
                target.FillColumn(x, y0, y1, packed);
            }

            return;
        }

        double left = scale.ToWindowX(rect.X);
        double right = scale.ToWindowX(rect.Right);
        double top = scale.ToWindowY(rect.Y);
        double bottom = scale.ToWindowY(rect.Bottom);

        // The interior rows are whole pixels, so the common case is still one FillColumn per column
        // and only the first and last row of each column are blended.
        int fullY0 = Math.Min(y1, Math.Max(y0, (int)Math.Ceiling(top)));
        int fullY1 = Math.Max(fullY0, Math.Min(y1, (int)Math.Floor(bottom)));

        for (int x = x0; x < x1; x++)
        {
            double coverageX = EdgeCoverage(x, left, right);
            if (coverageX <= 0.0)
            {
                continue;
            }

            if (coverageX >= 1.0)
            {
                target.FillColumn(x, fullY0, fullY1, packed);
            }
            else
            {
                for (int y = fullY0; y < fullY1; y++)
                {
                    Blend(target, x, y, packed, (byte)Math.Round(coverageX * 255.0));
                }
            }

            for (int y = y0; y < fullY0; y++)
            {
                Fringe(target, x, y, packed, coverageX * EdgeCoverage(y, top, bottom));
            }

            for (int y = fullY1; y < y1; y++)
            {
                Fringe(target, x, y, packed, coverageX * EdgeCoverage(y, top, bottom));
            }
        }
    }

    /// <summary>One boundary pixel of a design rectangle, at its own coverage.</summary>
    /// <param name="target">The frame.</param>
    /// <param name="x">Its column.</param>
    /// <param name="y">Its row.</param>
    /// <param name="packed">The rectangle's colour, already encoded.</param>
    /// <param name="coverage">The fraction of the pixel the rectangle covers, 0..1.</param>
    private static void Fringe(PixelTarget target, int x, int y, uint packed, double coverage)
    {
        if (coverage > 0.0)
        {
            Blend(target, x, y, packed, (byte)Math.Round(Math.Min(coverage, 1.0) * 255.0));
        }
    }

    /// Internal, not private: the MAP window's dots are the same anti-aliased design-space disc as
    /// a scope blip, and OverlayWindowRenderer draws them.
    internal static void FillDesignDisc(
        PixelTarget target, CockpitScale scale, double cx, double cy, double radius, Rgb24 color)
    {
        PanelRect rect = new PanelRect(
            (int)Math.Floor(cx - radius) - 1,
            (int)Math.Floor(cy - radius) - 1,
            (int)Math.Ceiling(radius * 2) + 2,
            (int)Math.Ceiling(radius * 2) + 2);
        uint packed = target.Encode(color);
        (int x0, int x1, int y0, int y1) = WindowBounds(target, rect, scale);
        for (int x = x0; x < x1; x++)
        {
            double dx = scale.ToDesignX(x + 0.5);
            for (int y = y0; y < y1; y++)
            {
                double dy = scale.ToDesignY(y + 0.5);
                double d = Math.Sqrt(((dx - cx) * (dx - cx)) + ((dy - cy) * (dy - cy)));
                double coverage = Math.Clamp(radius + 0.5 - d, 0.0, 1.0);
                if (coverage > 0.0)
                {
                    Blend(target, x, y, packed, (byte)Math.Round(coverage * 255.0));
                }
            }
        }
    }

    /// <summary>
    /// Fills a DESIGN-space polygon the way the original's own rasteriser does, but at host
    /// resolution and anti-aliased.
    /// </summary>
    /// <param name="target">The frame.</param>
    /// <param name="scale">The design → window mapping.</param>
    /// <param name="xs">The outline's design columns, <paramref name="count"/> of them.</param>
    /// <param name="ys">Its design rows.</param>
    /// <param name="count">How many vertices; fewer than three draws nothing.</param>
    /// <param name="color">What to paint it.</param>
    /// <param name="clip">The rectangle the fill may not leave, in design pixels.</param>
    /// <remarks>
    /// <para>
    /// <c>gfx_polygon_scanline_fill @image@0x11B16</c> is a <b>two-active-edge</b> fill, not an
    /// even-odd one: it finds the top and bottom vertices, walks ONE left edge chain and ONE right
    /// edge chain down the silhouette, and emits one inclusive span per scanline.
    /// This reproduces that model — one span per HOST row, the two chains interpolated at the row's
    /// own centre — rather than a generic polygon filler, so a shape the original would fill in one
    /// piece is filled in one piece here too.
    /// </para>
    /// <para>
    /// The deviation is deliberate and named: the original's spans come from an integer Bresenham
    /// DDA with half-coordinate sub-pixel rounding, and the port's come from the exact line at the
    /// host pixel's centre with fractional coverage at both ends.  That is the port's rule — chrome
    /// integer-scaled, CONTENTS at host resolution — and it is the same choice a radar blip and a
    /// map dot are drawn by.
    /// </para>
    /// </remarks>
    internal static void FillDesignPolygon(
        PixelTarget target,
        CockpitScale scale,
        ReadOnlySpan<double> xs,
        ReadOnlySpan<double> ys,
        int count,
        Rgb24 color,
        PanelRect clip)
    {
        if (count < 3 || xs.Length < count || ys.Length < count)
        {
            return;
        }

        int top = 0;
        int bottom = 0;
        for (int i = 1; i < count; i++)
        {
            if (ys[i] < ys[top])
            {
                top = i;
            }

            if (ys[i] > ys[bottom])
            {
                bottom = i;
            }
        }

        uint packed = target.Encode(color);
        (int cx0, int cx1, int cy0, int cy1) = WindowBounds(target, clip, scale);
        double designTop = ys[top];
        double designBottom = ys[bottom];
        double clipLeft = scale.ToWindowX(clip.X);
        double clipRight = scale.ToWindowX(clip.Right);

        for (int y = cy0; y < cy1; y++)
        {
            // The outline's coordinates are PIXEL INDICES, not corners: design row k is the band
            // [k, k+1) and the original's scanline walk is inclusive of both the top and the bottom
            // vertex row.  So the row's centre in the outline's own space is
            // ToDesignY(host + 0.5) − 0.5 — without that half-pixel the bottom row of a polygon whose
            // lowest vertex sits on the content's last row is dropped, which is exactly what the
            // MiG-21's +0 G curve showed (no green on design row 77, where the atlas has 13..41).
            double dy = scale.ToDesignY(y + 0.5) - 0.5;
            if (dy < designTop || dy > designBottom)
            {
                continue;
            }

            // The two chains: forward (top → … → bottom) and backward.  Whichever is smaller at this
            // row is the left edge — the original picks them by the sign of the first step, which is
            // the same thing for a silhouette with one span per row.
            double a = ChainX(xs, ys, count, top, bottom, dy, forward: true);
            double b = ChainX(xs, ys, count, top, bottom, dy, forward: false);
            double left = scale.ToWindowX(Math.Min(a, b));
            double right = scale.ToWindowX(Math.Max(a, b) + 1.0);
            left = Math.Max(left, clipLeft);
            right = Math.Min(right, clipRight);
            if (right <= left)
            {
                continue;
            }

            int x0 = Math.Max(cx0, (int)Math.Floor(left));
            int x1 = Math.Min(cx1, (int)Math.Ceiling(right));
            for (int x = x0; x < x1; x++)
            {
                double coverage = EdgeCoverage(x, left, right);
                if (coverage >= 1.0)
                {
                    target.Column(x)[y] = packed;
                }
                else if (coverage > 0.0)
                {
                    Blend(target, x, y, packed, (byte)Math.Round(coverage * 255.0));
                }
            }
        }
    }

    /// <summary>Where one edge chain of a polygon crosses a design row.</summary>
    /// <param name="xs">The outline's design columns.</param>
    /// <param name="ys">Its design rows.</param>
    /// <param name="count">The vertex count.</param>
    /// <param name="top">The index of the topmost vertex.</param>
    /// <param name="bottom">The index of the bottommost vertex.</param>
    /// <param name="dy">The design row to cross.</param>
    /// <param name="forward">Whether to walk the chain by ascending index.</param>
    /// <returns>The design column, or the chain's last x when no segment spans the row.</returns>
    private static double ChainX(
        ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, int count,
        int top, int bottom, double dy, bool forward)
    {
        int at = top;
        double lastX = xs[top];
        for (int step = 0; step < count; step++)
        {
            int next = forward ? at + 1 : at - 1;
            next = ((next % count) + count) % count;
            double y0 = ys[at];
            double y1 = ys[next];
            if (y1 != y0 && dy >= Math.Min(y0, y1) && dy <= Math.Max(y0, y1))
            {
                double t = (dy - y0) / (y1 - y0);
                return xs[at] + ((xs[next] - xs[at]) * t);
            }

            lastX = xs[next];
            if (at == bottom || next == bottom)
            {
                break;
            }

            at = next;
        }

        return lastX;
    }

    /// <summary>A tapered needle's half-width at the pivot, in design pixels.</summary>
    public const double NeedleHubHalfWidth = 0.6;

    /// <summary>A tapered needle's half-width at the tip, in design pixels.</summary>
    public const double NeedleTipHalfWidth = 0.12;

    /// <summary>The hub disc's radius, in design pixels.</summary>
    public const double NeedleHubRadius = 0.8;

    // A TAPERED needle: coverage against the pivot→tip segment where the half-width falls linearly
    // from hubHalf to tipHalf along it, plus a hub disc.  Anti-aliased one WINDOW pixel wide, like
    // DrawDesignLine, clipped to the slot's own rectangle for the same reason.
    public static void DrawDesignNeedle(
        PixelTarget target,
        CockpitScale scale,
        double x0,
        double y0,
        double x1,
        double y1,
        double hubHalf,
        double tipHalf,
        Rgb24 color,
        PanelRect clip,
        double hubRadius = NeedleHubRadius)
    {
        double margin = Math.Max(hubHalf, hubRadius) + 1.0;
        PanelRect box = new PanelRect(
            (int)Math.Floor(Math.Min(x0, x1) - margin),
            (int)Math.Floor(Math.Min(y0, y1) - margin),
            (int)Math.Ceiling(Math.Abs(x1 - x0) + (2 * margin)) + 1,
            (int)Math.Ceiling(Math.Abs(y1 - y0) + (2 * margin)) + 1);
        PanelRect rect = new PanelRect(
            Math.Max(box.X, clip.X),
            Math.Max(box.Y, clip.Y),
            Math.Min(box.Right, clip.Right) - Math.Max(box.X, clip.X),
            Math.Min(box.Bottom, clip.Bottom) - Math.Max(box.Y, clip.Y));
        if (!rect.IsPresent)
        {
            return;
        }

        double ax = x1 - x0;
        double ay = y1 - y0;
        double lengthSquared = (ax * ax) + (ay * ay);
        uint packed = target.Encode(color);
        double pixel = 1.0 / Math.Max(0.001, scale.ScaleY);   // one window pixel, in design units

        (int bx0, int bx1, int by0, int by1) = WindowBounds(target, rect, scale);
        double stepY = 1.0 / scale.ScaleY;
        double firstY = scale.ToDesignY(by0 + 0.5);
        for (int x = bx0; x < bx1; x++)
        {
            double dx = scale.ToDesignX(x + 0.5);
            double dy = firstY;
            for (int y = by0; y < by1; y++, dy += stepY)
            {
                double t = lengthSquared <= 0.0
                    ? 0.0
                    : Math.Clamp((((dx - x0) * ax) + ((dy - y0) * ay)) / lengthSquared, 0.0, 1.0);
                double px = x0 + (t * ax);
                double py = y0 + (t * ay);
                double d = Math.Sqrt(((dx - px) * (dx - px)) + ((dy - py) * (dy - py)));
                double half = hubHalf + ((tipHalf - hubHalf) * t);
                double coverage = Math.Clamp(((half - d) / pixel) + 0.5, 0.0, 1.0);

                double hub = Math.Sqrt(((dx - x0) * (dx - x0)) + ((dy - y0) * (dy - y0)));
                coverage = Math.Max(coverage, Math.Clamp(((hubRadius - hub) / pixel) + 0.5, 0.0, 1.0));
                if (coverage > 0.0)
                {
                    Blend(target, x, y, packed, (byte)Math.Round(coverage * 255.0));
                }
            }
        }
    }

    // A constant-width anti-aliased CAPSULE stroke (round ends), clipped to a rectangle.
    public static void DrawDesignStroke(
        PixelTarget target, CockpitScale scale, double x0, double y0, double x1, double y1,
        double halfWidth, Rgb24 color, PanelRect clip) =>
        DrawDesignNeedle(target, scale, x0, y0, x1, y1, halfWidth, halfWidth, color, clip, hubRadius: 0.0);

    // An anti-aliased RING (a circle stroke) in design coordinates, one window pixel of
    // anti-aliasing on both edges.
    public static void DrawDesignRing(
        PixelTarget target, CockpitScale scale, double cx, double cy, double radius, double halfStroke, Rgb24 color)
    {
        double outer = radius + halfStroke + 1.0;
        PanelRect rect = new PanelRect(
            (int)Math.Floor(cx - outer),
            (int)Math.Floor(cy - outer),
            (int)Math.Ceiling(outer * 2) + 1,
            (int)Math.Ceiling(outer * 2) + 1);
        uint packed = target.Encode(color);
        double pixel = 1.0 / Math.Max(0.001, scale.ScaleY);
        (int x0, int x1, int y0, int y1) = WindowBounds(target, rect, scale);
        double stepY = 1.0 / scale.ScaleY;
        double firstY = scale.ToDesignY(y0 + 0.5);
        for (int x = x0; x < x1; x++)
        {
            double dx = scale.ToDesignX(x + 0.5);
            double dy = firstY;
            for (int y = y0; y < y1; y++, dy += stepY)
            {
                double d = Math.Abs(Math.Sqrt(((dx - cx) * (dx - cx)) + ((dy - cy) * (dy - cy))) - radius);
                double coverage = Math.Clamp(((halfStroke - d) / pixel) + 0.5, 0.0, 1.0);
                if (coverage > 0.0)
                {
                    Blend(target, x, y, packed, (byte)Math.Round(coverage * 255.0));
                }
            }
        }
    }

    // An anti-aliased DISC with a window-pixel rim (FillDesignDisc's rim is a design pixel wide,
    // which is a blur at 6× scale).
    public static void DrawDesignDot(
        PixelTarget target, CockpitScale scale, double cx, double cy, double radius, Rgb24 color) =>
        DrawDesignRing(target, scale, cx, cy, radius / 2.0, radius / 2.0, color);

    // An anti-aliased needle: analytic coverage against the segment, computed in DESIGN space so the
    // needle keeps its 1991 thickness relative to the dial face however far the window is scaled.
    internal static void DrawDesignLine(
        PixelTarget target,
        CockpitScale scale,
        double x0,
        double y0,
        double x1,
        double y1,
        Rgb24 color,
        PanelRect clip)
    {
        const double HalfWidth = 0.55;
        PanelRect box = new PanelRect(
            (int)Math.Floor(Math.Min(x0, x1)) - 1,
            (int)Math.Floor(Math.Min(y0, y1)) - 1,
            (int)Math.Ceiling(Math.Abs(x1 - x0)) + 2,
            (int)Math.Ceiling(Math.Abs(y1 - y0)) + 2);

        // Clipped to the slot's OWN rectangle.  The original's line drawer clips to the screen,
        // but the erase only restores the rectangle the record saved at init
        // (dial_slot_init_active_state @image@0x0192E pushes slot[+0x00..+0x06] as the backing
        // store), so a needle outside it would smear; the port clips instead of smearing.  Four of
        // the six cockpits never reach the edge; the P-51's fuel and percentage slots sit their
        // pivot ON it, which is a hint that the X term's runtime factor is below 1 (open item O2).
        PanelRect rect = new PanelRect(
            Math.Max(box.X, clip.X),
            Math.Max(box.Y, clip.Y),
            Math.Min(box.Right, clip.Right) - Math.Max(box.X, clip.X),
            Math.Min(box.Bottom, clip.Bottom) - Math.Max(box.Y, clip.Y));
        if (!rect.IsPresent)
        {
            return;
        }

        double ax = x1 - x0;
        double ay = y1 - y0;
        double lengthSquared = (ax * ax) + (ay * ay);
        uint packed = target.Encode(color);
        double half = HalfWidth + (0.5 / Math.Max(0.001, scale.ScaleY));

        (int bx0, int bx1, int by0, int by1) = WindowBounds(target, rect, scale);
        double lineStepY = 1.0 / scale.ScaleY;
        double lineFirstY = scale.ToDesignY(by0 + 0.5);
        for (int x = bx0; x < bx1; x++)
        {
            double dx = scale.ToDesignX(x + 0.5);
            double dy = lineFirstY;
            for (int y = by0; y < by1; y++, dy += lineStepY)
            {
                double t = lengthSquared <= 0.0
                    ? 0.0
                    : Math.Clamp((((dx - x0) * ax) + ((dy - y0) * ay)) / lengthSquared, 0.0, 1.0);
                double px = x0 + (t * ax);
                double py = y0 + (t * ay);
                double d = Math.Sqrt(((dx - px) * (dx - px)) + ((dy - py) * (dy - py)));
                double coverage = Math.Clamp(half - d, 0.0, 1.0);
                if (coverage > 0.0)
                {
                    Blend(target, x, y, packed, (byte)Math.Round(coverage * 255.0));
                }
            }
        }
    }

    /// <summary>
    /// A PORT ADDITION — draws a string at a FIXED number of host pixels per font pixel, anchored
    /// in WINDOW space, with an opacity.
    /// </summary>
    /// <param name="target">The frame.</param>
    /// <param name="font">The glyph strip.</param>
    /// <param name="text">What to draw.</param>
    /// <param name="hostX">The left edge, in window pixels.</param>
    /// <param name="hostY">The top edge, in window pixels.</param>
    /// <param name="pixelScale">Host pixels per font pixel — 1 is a strict 1:1.</param>
    /// <param name="color">The ink.</param>
    /// <param name="opacity">0 (invisible) to 1 (solid).</param>
    /// <returns>How many font pixels were painted.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="DrawText"/> maps a DESIGN rectangle onto the window and samples the glyph per host
    /// pixel, so its text grows with the window — at 4K a 6-pixel-tall glyph becomes 27 pixels tall.
    /// That is right for text that belongs to the panel, which is a picture being enlarged. It is
    /// wrong for the in-world designator labels: their letters read best at 1:1 in pixel terms,
    /// because the port is not recreating 320×200 at 4K.
    /// </para>
    /// <para>
    /// So this one takes the ANCHOR from the design projection (the label still rides its target)
    /// and then lays the glyphs out in host pixels, which keeps them the same physical size at every
    /// resolution. Nothing here is the original's — the original had one resolution — so it carries
    /// no <c>image@</c> citation, only the ask it answers.
    /// </para>
    /// </remarks>
    internal static int DrawTextUnscaled(
        PixelTarget target,
        CockpitFont font,
        string text,
        double hostX,
        double hostY,
        int pixelScale,
        Rgb24 color,
        double opacity)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);

        byte alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255.0), 0, 255);
        if (alpha == 0 || pixelScale <= 0)
        {
            return 0;
        }

        uint packed = target.Encode(color);
        int pen = (int)Math.Round(hostX);
        int top = (int)Math.Round(hostY);
        int painted = 0;

        foreach (char c in text)
        {
            int advance = font.Advance(c);
            if (advance <= 0)
            {
                continue;
            }

            for (int gx = 0; gx < advance; gx++)
            {
                for (int gy = 0; gy < font.Height; gy++)
                {
                    if (!font.Ink(c, gx, gy))
                    {
                        continue;
                    }

                    for (int sx = 0; sx < pixelScale; sx++)
                    {
                        for (int sy = 0; sy < pixelScale; sy++)
                        {
                            Blend(target, pen + (gx * pixelScale) + sx, top + (gy * pixelScale) + sy, packed, alpha);
                        }
                    }

                    painted++;
                }
            }

            pen += advance * pixelScale;
        }

        return painted;
    }

    /// <summary>The width of a string drawn by <see cref="DrawTextUnscaled"/>, in host pixels.</summary>
    /// <param name="font">The glyph strip.</param>
    /// <param name="text">The string.</param>
    /// <param name="pixelScale">Host pixels per font pixel.</param>
    /// <returns>Its width.</returns>
    internal static int MeasureUnscaled(CockpitFont font, string text, int pixelScale)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);
        int width = 0;
        foreach (char c in text)
        {
            int advance = font.Advance(c);
            if (advance > 0)
            {
                width += advance * pixelScale;
            }
        }

        return width;
    }

    /// <summary>
    /// A rectangle of the shared instrument sheet <c>miscv.pic</c> blitted into a design-space rectangle:
    /// the port's <c>gfx_plain_blit @image@0x1CF24</c> for the advisor window's Chuck Yeager PORTRAIT.
    /// </summary>
    /// <param name="target">The frame.</param>
    /// <param name="art">The aircraft's cockpit art — <see cref="CockpitArt.MiscAt"/> is the sheet.</param>
    /// <param name="scale">The design → window mapping (for a window, its own pixel scale).</param>
    /// <param name="dest">Where the rectangle goes, in design pixels.</param>
    /// <param name="sourceX">The sheet column the rectangle is cut from.</param>
    /// <param name="sourceY">The sheet row.</param>
    /// <param name="options">The cockpit knobs — only <see cref="CockpitOptions.Filter"/> is read.</param>
    /// <returns>How many window pixels were written.</returns>
    /// <remarks>
    /// <para>
    /// The original's blit is a <c>rep movsb</c> rectangle copy with no clip, no colour key and no
    /// scale, so every destination pixel is one source pixel; here the destination rectangle is
    /// mapped through <paramref name="scale"/> like every other design-space primitive.  The sampler
    /// is the SAME one the cockpit's own sprite overlays use (<c>BuildIcon</c>): NEAREST reproduces
    /// the 1991 pixels enlarged, and the smooth filter resamples the photograph with the panel's own
    /// bilinear kernel, CLAMPED to the source rectangle so the neighbouring sprite on the sheet
    /// cannot bleed into the portrait's edge column.
    /// </para>
    /// <para>
    /// No allocation: the pixels go straight into the target, which is what lets the advisor window
    /// be drawn every frame it is up without a steady-state allocation.
    /// </para>
    /// </remarks>
    internal static int BlitDesignSheet(
        PixelTarget target,
        CockpitArt art,
        CockpitScale scale,
        PanelRect dest,
        int sourceX,
        int sourceY,
        in CockpitOptions options)
    {
        ArgumentNullException.ThrowIfNull(art);

        (int x0, int x1, int y0, int y1) = WindowBounds(target, dest, scale);
        bool nearest = options.Filter == CockpitFilter.Nearest;
        int offsetX = sourceX - dest.X;
        int offsetY = sourceY - dest.Y;
        int painted = 0;

        // The palette is encoded ONCE rather than per pixel: a 64 x 42 portrait at the window's
        // pixel scale is tens of thousands of taps, and under NEAREST every one of them lands on one
        // of 256 colours.
        Span<uint> encoded = stackalloc uint[256];
        if (nearest)
        {
            for (int i = 0; i < encoded.Length; i++)
            {
                encoded[i] = target.Encode(art.ColorAt(i));
            }
        }

        for (int x = x0; x < x1; x++)
        {
            double dx = scale.ToDesignX(x + 0.5);
            int sx = Math.Clamp((int)Math.Floor(dx), dest.X, dest.Right - 1) + offsetX;
            Span<uint> column = target.Column(x);
            for (int y = y0; y < y1; y++)
            {
                double dy = scale.ToDesignY(y + 0.5);
                if (nearest)
                {
                    int sy = Math.Clamp((int)Math.Floor(dy), dest.Y, dest.Bottom - 1) + offsetY;
                    column[y] = encoded[art.MiscAt(sx, sy)];
                }
                else
                {
                    column[y] = target.Encode(
                        SampleSheetSmooth(art, dx, dy, dest, offsetX, offsetY));
                }

                painted++;
            }
        }

        return painted;
    }

    internal static void DrawText(
        PixelTarget target,
        CockpitFont font,
        CockpitScale scale,
        string text,
        int designX,
        int designY,
        Rgb24 color)
    {
        uint packed = target.Encode(color);
        int pen = designX;
        foreach (char c in text)
        {
            int advance = font.Advance(c);
            if (advance <= 0)
            {
                continue;
            }

            PanelRect cell = new PanelRect(pen, designY, advance, font.Height);
            (int x0, int x1, int y0, int y1) = WindowBounds(target, cell, scale);
            double textStepY = 1.0 / scale.ScaleY;
            double textFirstY = scale.ToDesignY(y0 + 0.5);
            for (int x = x0; x < x1; x++)
            {
                int gx = (int)scale.ToDesignX(x + 0.5) - pen;
                Span<uint> column = target.Column(x);
                double gy = textFirstY;
                for (int y = y0; y < y1; y++, gy += textStepY)
                {
                    if (font.Ink(c, gx, (int)gy - designY))
                    {
                        column[y] = packed;
                    }
                }
            }

            pen += advance;
        }
    }
}
