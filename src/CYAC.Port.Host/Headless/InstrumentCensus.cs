using System.Globalization;
using CYAC.Port.Core.Model.Cockpit;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host.Headless;

/// <summary>
/// The INSTRUMENT CENSUS: what value the port feeds each of the ten dial slots and each of the ten
/// instrument regions over a sortie, and whether it ever CHANGES.
/// </summary>
/// <remarks>
/// <para>
/// Not all instruments were working: some needles and the ammo counter.
/// A frame is not evidence for that: an instrument can be drawn perfectly and still be fed a constant.
/// This walks the same <see cref="CockpitState"/> the cockpit layer draws from, one row per slot and
/// per region, and reports the value's range, how many DISTINCT values it took and — for the dials —
/// the same for the needle angle the shipped record maps it to (<c>dial_slot_value_compute
/// @image@0x02162</c>), because a slot whose range is clamped away moves its value and not its needle.
/// </para>
/// <para>
/// It is pure observation: nothing here is read by the renderer or written into the simulation.
/// </para>
/// </remarks>
public sealed class InstrumentCensus
{
    private readonly Channel[] _dialValues = Make(DialLayout.SlotCount);
    private readonly Channel[] _dialAngles = Make(DialLayout.SlotCount);
    private readonly Channel[] _dialSecondary = Make(DialLayout.SlotCount);
    private readonly Channel[] _regions = Make(CockpitLayout.RegionCount);
    private long _frames;

    /// <summary>The MiG-21-only afterburner region (<c>image@0x0E319</c>'s <c>[0xC31A] == 5</c>).</summary>
    private const int AfterburnerRegion = 9;

    private static Channel[] Make(int count)
    {
        Channel[] channels = new Channel[count];
        for (int i = 0; i < count; i++)
        {
            channels[i] = new Channel();
        }

        return channels;
    }

    /// <summary>How many frames were observed.</summary>
    public long Frames => _frames;

    /// <summary>Records one frame's instrument state.</summary>
    /// <param name="state">The state the cockpit layer is about to draw from.</param>
    /// <param name="art">The aircraft's art, for its dial records and region rectangles.</param>
    public void Observe(in CockpitState state, CockpitArt art)
    {
        ArgumentNullException.ThrowIfNull(art);
        _frames++;

        for (int slot = 0; slot < DialLayout.SlotCount && slot < art.Dials.Count; slot++)
        {
            DialSlot record = art.Dials[slot];
            int value = state.DialValue(slot);
            _dialValues[slot].Add(value);
            if (record.IsDrawn)
            {
                _dialAngles[slot].Add(DialNeedle.Angle(in record, value));
                if (record.NeedleCount > 1)
                {
                    _dialSecondary[slot].Add(
                        DialNeedle.Angle(in record, state.DialSecondary(slot)));
                }
            }
        }

        for (int region = 0; region < CockpitLayout.RegionCount; region++)
        {
            _regions[region].Add(RegionValue(region, in state));
        }

        ObserveRadar(in state, art);
    }

    // The RADAR's own channels, so a headless run SAYS what the scope is doing.
    private long _radarOnFrames;
    private long _radarSweepFrames;
    private long _radarSweeps;
    private int _lastSweepPhase = -1;
    private readonly Channel _radarPlotted = new();
    private readonly Channel _rwrPlotted = new();
    private long _threatSilent;
    private long _threatSteady;
    private long _threatBlinking;
    private int _scaleShift = RadarScope.DefaultScaleShift;

    /// <summary>One frame of radar state.</summary>
    private void ObserveRadar(in CockpitState state, CockpitArt art)
    {
        RadarState radar = state.Radar;
        _scaleShift = radar.ScaleShift;
        if (radar.On)
        {
            _radarOnFrames++;
        }

        if (radar.Sweeping)
        {
            _radarSweepFrames++;
            if (_lastSweepPhase < 0 || radar.SweepPhase < _lastSweepPhase)
            {
                _radarSweeps++;
            }
        }

        _lastSweepPhase = radar.SweepPhase;

        PanelRect radarRect = art.Layout.Regions[1].RectFor(art.AircraftIndex);
        PanelRect rwrRect = art.Layout.Regions[2].RectFor(art.AircraftIndex);
        int plottedRadar = 0;
        int plottedRwr = 0;
        foreach (ScopeContact contact in radar.Contacts ?? [])
        {
            switch (contact.Threat)
            {
                case ScopeThreat.Blinking: _threatBlinking++; break;
                case ScopeThreat.Steady: _threatSteady++; break;
                default: _threatSilent++; break;
            }

            if (radar.On && radarRect.IsPresent
                && RadarScope.RadarShows(in contact, radarRect.Width, radarRect.Height, radar.ScaleShift))
            {
                plottedRadar++;
            }

            if (rwrRect.IsPresent && RadarScope.RwrShows(
                    in contact, rwrRect.Width, rwrRect.Height, radar.ScaleShift, radar.RwrSampleCounter))
            {
                plottedRwr++;
            }
        }

        _radarPlotted.Add(plottedRadar);
        _rwrPlotted.Add(plottedRwr);
    }

    /// <summary>
    /// The RADAR census: what the switch, the sweep and the two scopes did over the run.
    /// </summary>
    /// <param name="art">The aircraft's art, for the two rectangles' range gates.</param>
    /// <param name="keyPresses">How many times the <b>R</b> key was pressed.</param>
    /// <returns>Lines to print, or none when the aircraft has no radar.</returns>
    public IReadOnlyList<string> RadarLines(CockpitArt art, int keyPresses)
    {
        ArgumentNullException.ThrowIfNull(art);
        PanelRect radarRect = art.Layout.Regions[1].RectFor(art.AircraftIndex);
        PanelRect rwrRect = art.Layout.Regions[2].RectFor(art.AircraftIndex);
        if (!radarRect.IsPresent && !rwrRect.IsPresent)
        {
            return [string.Create(
                CultureInfo.InvariantCulture,
                $"   radar   {art.Basename} has NEITHER instrument (both rectangles zero-width)")];
        }

        return
        [
            string.Create(
                CultureInfo.InvariantCulture,
                $"   radar   R×{keyPresses}  ON {_radarOnFrames:N0} / {_frames:N0} frame(s)  "
                    + $"sweep {_radarSweeps:N0} collapse(s), {_radarSweepFrames:N0} frame(s)"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"           plotted: radar {_radarPlotted}  rwr {_rwrPlotted}  "
                    + $"threats silent {_threatSilent:N0} / steady {_threatSteady:N0} / "
                    + $"blinking {_threatBlinking:N0}"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"           shift {_scaleShift} = {RadarScope.FeetPerPixel(_scaleShift):N0} ft/px; "
                    + $"gate radar {Gate(radarRect):N0} ft, rwr {Gate(rwrRect):N0} ft"),
        ];

        double Gate(PanelRect rect) => rect.IsPresent
            ? RadarScope.RangeGateFeet(rect.Width, rect.Height, _scaleShift)
            : 0.0;
    }

    /// <summary>
    /// Records one frame's TARGET WINDOW and in-world DESIGNATOR state.
    /// </summary>
    /// <param name="windows">The frame's overlay-window state.</param>
    /// <param name="designators">The frame's designator labels, or null.</param>
    public void ObserveTarget(
        in OverlayWindowState windows, IReadOnlyList<HudDesignator>? designators)
    {
        // The window is DRAWN only when its visibility bit is up as well — `--windows off` leaves the
        // gate answering "there is a target" while nothing is painted.
        if (windows.TargetPresent && windows.Visible.HasFlag(CockpitOverlayFlags.Target))
        {
            _targetFrames++;
            TargetPanelState panel = windows.TargetPanel;
            Note(_targetTitles, windows.TargetTitle);
            Note(_targetManoeuvres, panel.Manoeuvre);
            Note(_targetLockStates, panel.LockState);
            Note(_targetClocks, panel.Clock);
            Note(_targetSpeeds, panel.Speed);
            Note(_targetRanges, panel.Range);
            Note(_targetCaptions, panel.Caption);
            _targetStations.Add(panel.WeaponStation);
        }

        int labels = 0;
        foreach (HudDesignator label in designators ?? [])
        {
            labels++;
            if (label.HitPercent >= 0)
            {
                _designatorPercent.Add(label.HitPercent);
            }

            if (label.EngagingPlayer)
            {
                _designatorsHostile++;
            }
        }

        _designatorCount.Add(labels);
    }

    // The MAP WINDOW's own channels.
    private long _mapFrames;
    private readonly Channel _mapDots = new();
    private readonly Dictionary<string, int> _mapZooms = [];
    private long _mapFriendly;
    private long _mapHostile;
    private long _mapAbove;
    private long _mapWide;

    /// <summary>
    /// Records one frame's MAP WINDOW state.
    /// </summary>
    /// <param name="windows">The frame's overlay-window state.</param>
    /// <param name="dots">How many contacts the renderer actually plotted this frame.</param>
    public void ObserveMap(in OverlayWindowState windows, int dots)
    {
        if (!windows.EffectiveVisible.HasFlag(CockpitOverlayFlags.Map))
        {
            return;
        }

        _mapFrames++;
        _mapDots.Add(dots);
        Note(_mapZooms, MapWindow.ZoomLabel(windows.MapPanel.ScaleShift));

        int shift = Math.Clamp(
            windows.MapPanel.ScaleShift, MapWindow.MinScaleShift, MapWindow.MaxScaleShift);
        foreach (ScopeContact contact in windows.MapPanel.Contacts ?? [])
        {
            if (!MapWindow.Shows(in contact, shift))
            {
                continue;
            }

            switch (contact.MapDot)
            {
                case MapDotColour.Hostile: _mapHostile++; break;
                case MapDotColour.Friendly: _mapFriendly++; break;
                default: break;
            }

            if (contact.AtOrAbovePlayer)
            {
                _mapAbove++;
            }

            if (contact.Wide)
            {
                _mapWide++;
            }
        }
    }

    // The ENVELOPE WINDOW's own channels.
    private long _envelopeFrames;
    private long _envelopeDrawn;
    private readonly Channel _envelopeLoadFactor = new();
    private readonly Channel _envelopeMarkerX = new();
    private readonly Channel _envelopeMarkerY = new();
    private readonly Channel _envelopePivotX = new();
    private readonly Channel _envelopePivotY = new();
    private readonly Dictionary<string, int> _envelopeFooters = [];
    private long _envelopeBlinkBright;
    private int _envelopeCornerX;
    private int _envelopeCornerY;

    /// <summary>
    /// Records one frame's ENVELOPE WINDOW state.
    /// </summary>
    /// <param name="windows">The frame's overlay-window state.</param>
    /// <param name="shapes">How many shapes the renderer actually painted (0 or 5).</param>
    /// <remarks>
    /// The marker's design position is what makes the instrument's whole point measurable in a
    /// headless run: if the aeroplane's state changes and the marker does not move, the window is
    /// drawn but dead.
    /// </remarks>
    public void ObserveEnvelope(in OverlayWindowState windows, int shapes)
    {
        if (!windows.EffectiveVisible.HasFlag(CockpitOverlayFlags.Envelope))
        {
            return;
        }

        _envelopeFrames++;
        Note(_envelopeFooters, windows.EnvelopeFooter);
        EnvelopePanelState panel = windows.EnvelopePanel;
        _envelopeCornerX = panel.CornerX;
        _envelopeCornerY = panel.CornerY;
        if (shapes <= 0 || panel.Curve is not { } curve)
        {
            return;
        }

        _envelopeDrawn++;
        _envelopeLoadFactor.Add(curve.LoadFactorG);
        if (EnvelopeWindow.MarkerPaletteIndex(panel.RenderFrameCounter)
            == EnvelopeWindow.MarkerBrightPaletteIndex)
        {
            _envelopeBlinkBright++;
        }

        int x = OverlayWindowRenderer.EnvelopeSlotX(
            windows.Visible, windows.AdvisorPanel.Speaking);
        PanelRect marker = EnvelopeWindow.MarkerRect(
            panel.AirspeedFps, panel.AltitudeQ8, panel.CornerX, panel.CornerY, x);
        _envelopeMarkerX.Add(marker.X);
        _envelopeMarkerY.Add(marker.Y);

        IReadOnlyList<EnvelopePoint> points = curve.Points;
        if (curve.PeakIndex < points.Count)
        {
            _envelopePivotX.Add(
                EnvelopeWindow.PlotColumn(points[curve.PeakIndex].AirspeedFps, panel.CornerX, x));
        }

        if (curve.HighSpeedIndex < points.Count)
        {
            _envelopePivotY.Add(
                EnvelopeWindow.PlotRow(points[curve.HighSpeedIndex].AltitudeEighths, panel.CornerY));
        }
    }

    /// <summary>The ENVELOPE window census lines.</summary>
    /// <returns>Lines to print.</returns>
    public IReadOnlyList<string> EnvelopeLines() =>
    [
        string.Create(
            CultureInfo.InvariantCulture,
            $"   envelope window up {_envelopeFrames:N0} / {_frames:N0} frame(s), plot drawn "
                + $"{_envelopeDrawn:N0}  curve {_envelopeLoadFactor} G  "
                + $"corner {_envelopeCornerX:N0} fps x {_envelopeCornerY:N0} (×8 ft)"),
        string.Create(
            CultureInfo.InvariantCulture,
            $"           marker x {_envelopeMarkerX} y {_envelopeMarkerY}  "
                + $"pivot x {_envelopePivotX} y {_envelopePivotY}  "
                + $"blink bright {_envelopeBlinkBright:N0} / {_envelopeDrawn:N0}  "
                + $"footer {Show(_envelopeFooters)}"),
    ];

    // ── W5, the YEAGER advisor ────────────────────────────────────────────────────────────

    private long _advisorFrames;
    private long _advisorSpeaking;
    private long _advisorLines;
    private readonly Dictionary<string, int> _advisorMessages = [];
    private readonly HashSet<AdvisorDisplayType> _advisorTypes = [];
    private string _advisorLine1 = string.Empty;
    private string _advisorLine2 = string.Empty;

    /// <summary>What the YEAGER advisor window did this frame.</summary>
    /// <param name="windows">The frame's window state.</param>
    /// <param name="lines">How many text lines <c>RenderAdvisorContents</c> drew.</param>
    public void ObserveAdvisor(in OverlayWindowState windows, int lines)
    {
        if (!windows.Visible.HasFlag(CockpitOverlayFlags.Yeager))
        {
            return;
        }

        _advisorFrames++;
        if (!windows.AdvisorPanel.Speaking)
        {
            return;
        }

        _advisorSpeaking++;
        _advisorLines += lines;
        AdvisorPanelState panel = windows.AdvisorPanel;
        _advisorTypes.Add(panel.DisplayType);

        // A message is up for about four seconds, i.e. ~200 frames, and only its FIRST frame is
        // new: comparing the two line references keeps the census allocation-free while a message
        // holds, which matters because it runs inside the frame.
        if (ReferenceEquals(panel.Line1, _advisorLine1)
            && ReferenceEquals(panel.Line2, _advisorLine2))
        {
            return;
        }

        _advisorLine1 = panel.Line1;
        _advisorLine2 = panel.Line2;
        Note(
            _advisorMessages,
            string.IsNullOrEmpty(panel.Line2) ? panel.Line1 : panel.Line1 + " / " + panel.Line2);
    }

    /// <summary>The YEAGER advisor census lines.</summary>
    /// <param name="raised">How many the integer kernels raised.</param>
    /// <param name="spoken">How many advisories the queue let through.</param>
    /// <param name="suppressed">How many it refused.</param>
    /// <param name="mask">The one-shot bitmask <c>[0xBCFA]/[0xBCFC]</c>.</param>
    /// <returns>Lines to print.</returns>
    public IReadOnlyList<string> AdvisorLines(int raised, int spoken, int suppressed, uint mask) =>
    [
        string.Create(
            CultureInfo.InvariantCulture,
            $"   yeager  window enabled {_advisorFrames:N0} / {_frames:N0} frame(s), SPEAKING "
                + $"{_advisorSpeaking:N0}  lines {_advisorLines:N0}  "
                + $"raised {raised:N0} / dispatched {spoken:N0} / suppressed {suppressed:N0}  "
                + $"mask 0x{mask:X8}"),
        string.Create(
            CultureInfo.InvariantCulture,
            $"           portrait {string.Join('+', _advisorTypes.Order())}  "
                + $"said {Show(_advisorMessages)}"),
    ];

    /// <summary>The MAP window census line.</summary>
    /// <param name="zoomKeyPresses">How many times <c>.</c> / <c>,</c> were pressed.</param>
    /// <returns>Lines to print.</returns>
    public IReadOnlyList<string> MapLines(int zoomKeyPresses) =>
    [
        string.Create(
            CultureInfo.InvariantCulture,
            $"   map     window up {_mapFrames:N0} / {_frames:N0} frame(s)  zoom {Show(_mapZooms)}  "
                + $"key ×{zoomKeyPresses}  dots {_mapDots}"),
        string.Create(
            CultureInfo.InvariantCulture,
            $"           dot-frames: hostile {_mapHostile:N0} / friendly {_mapFriendly:N0}; "
                + $"above the player {_mapAbove:N0}; wide (bomber) {_mapWide:N0}; "
                + $"range gate {MapWindow.RangeGateFeet(_scaleShift):N0} ft"),
    ];

    /// <summary>The TARGET window and designator census lines.</summary>
    /// <param name="advisoryRequests">How many times Ctrl-Z / Ctrl-A were pressed.</param>
    /// <returns>Lines to print.</returns>
    public IReadOnlyList<string> TargetLines(int advisoryRequests) =>
    [
        string.Create(
            CultureInfo.InvariantCulture,
            $"   target  window up {_targetFrames:N0} / {_frames:N0} frame(s)  "
                + $"title {Show(_targetTitles)}  manoeuvre {Show(_targetManoeuvres)}  "
                + $"lock {Show(_targetLockStates)}"),
        string.Create(
            CultureInfo.InvariantCulture,
            $"           clock {Show(_targetClocks)}  speed {Show(_targetSpeeds)}  "
                + $"range {Show(_targetRanges)}  stations {_targetStations}  "
                + $"caption {Show(_targetCaptions)}"),
        string.Create(
            CultureInfo.InvariantCulture,
            $"           designators {_designatorCount} label(s), hit% {_designatorPercent}, "
                + $"{_designatorsHostile:N0} frame-label(s) engaging the player; "
                + $"advisory key ×{advisoryRequests}"),
    ];

    private static void Note(Dictionary<string, int> seen, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        seen[text] = seen.TryGetValue(text, out int n) ? n + 1 : 1;
    }

    private static string Show(Dictionary<string, int> seen)
    {
        if (seen.Count == 0)
        {
            return "-";
        }

        List<string> examples = new List<string>(3);
        foreach (string key in seen.Keys)
        {
            examples.Add(key);
            if (examples.Count == 3)
            {
                break;
            }
        }

        string shown = string.Join(" / ", examples);
        return seen.Count <= examples.Count
            ? $"\"{shown}\""
            : string.Create(CultureInfo.InvariantCulture, $"{seen.Count} distinct (\"{shown}\" …)");
    }

    private long _targetFrames;
    private long _designatorsHostile;
    private readonly Dictionary<string, int> _targetTitles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _targetManoeuvres = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _targetLockStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _targetClocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _targetSpeeds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _targetRanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _targetCaptions = new(StringComparer.Ordinal);
    private readonly Channel _targetStations = new();
    private readonly Channel _designatorCount = new();
    private readonly Channel _designatorPercent = new();

    /// <summary>
    /// The quantity each REGION draws from, in the order <c>cockpit_dispatch_state_changes
    /// @image@0x0E735</c> pushes them.
    /// </summary>
    /// <param name="region">The region index 0..9.</param>
    /// <param name="state">The frame's state.</param>
    /// <remarks>
    /// Regions 0, 1 and 2 have a NULL state function and redraw on a timer, so their "value"
    /// here is the quantity the port actually draws from; the other seven are the state
    /// functions' own returns.
    /// </remarks>
    private static int RegionValue(int region, in CockpitState state) => region switch
    {
        0 => (state.PitchBam * 0x10000) + state.RollBam,                     // image@0x0E0AE
        // Region 1's own state IS the mode bit [0x3F06] bit 5 and, while it is clear, the sweep's
        // phase; region 2 has no mode, so its state is what it plots.
        1 => state.Radar.On ? 1 : -1 - Math.Max(state.Radar.SweepPhase, -1),  // image@0x0E212
        2 => state.Radar.Contacts?.Count ?? -1,                               // image@0x0E186
        3 => (state.FlapsDown ? 2 : 0) | (state.GearDown ? 1 : 0),           // image@0x0DD55
        4 => state.GearDown ? 1 : 0,                                         // image@0x0DE41
        5 => state.BrakeOn ? 1 : 0,                                          // image@0x0DDED
        6 => state.WeaponRounds,                                             // image@0x0DF16
        7 => state.BearingPointerBam,                                        // image@0x0DFB4
        8 => (state.ChaffCount * 0x100) + state.FlareCount,                  // image@0x0DE95
        9 => state.Afterburner ? 1 : 0,                                      // image@0x0E319
        _ => 0,
    };

    private static readonly string[] RegionNames =
    [
        "artificialHorizon", "radarMonitor", "rwr", "flaps", "landingGear",
        "wheelBrake", "weaponAmmo", "compass", "chaffFlare", "afterburner",
    ];

    private static readonly string[] SlotNames =
    [
        "altimeter", "vsi", "airspeed", "compass", "bearingPointer",
        "throttle", "fuel", "pctMeterA", "pctMeterB", "pctMeterC",
    ];

    /// <summary>
    /// What the two BEARING instruments have to point at: the nav waypoints the load
    /// registered, described for the census header.
    /// </summary>
    public string NavWaypoints { get; set; } = string.Empty;

    /// <summary>The census, one line per instrument.</summary>
    /// <param name="art">The aircraft's art, so absent instruments can say so.</param>
    /// <returns>Lines to print.</returns>
    public IReadOnlyList<string> Lines(CockpitArt art)
    {
        ArgumentNullException.ThrowIfNull(art);
        List<string> lines = new List<string>
        {
            string.Create(
                CultureInfo.InvariantCulture,
                $"INSTRUMENTS {art.Basename} over {_frames:N0} frame(s); nav {NavWaypoints} — dial slots:"),
        };

        for (int slot = 0; slot < DialLayout.SlotCount && slot < art.Dials.Count; slot++)
        {
            DialSlot record = art.Dials[slot];
            string drawn = record.IsDrawn
                ? $"{record.NeedleCount} needle(s)"
                : record.Present ? "present, not drawn" : "ABSENT";
            string secondary = _dialSecondary[slot].Seen > 0
                ? $"  needle2 {_dialSecondary[slot]}"
                : string.Empty;
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"   slot {slot} {SlotNames[slot],-14} {drawn,-18} value {_dialValues[slot]}  "
                    + $"angle {_dialAngles[slot]}{secondary}"));
        }

        lines.Add("   regions:");
        for (int region = 0; region < CockpitLayout.RegionCount; region++)
        {
            // Region 9's table is a SINGLE record, so RectFor hands it to every aircraft; its own
            // gate is `cmp word [0xC31A],5`, the MiG-21 alone.
            PanelRect rect = region == AfterburnerRegion && art.Basename != "mig21"
                ? default
                : art.Layout.Regions[region].RectFor(art.AircraftIndex);
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"   region {region} {RegionNames[region],-18} "
                    + $"{(rect.IsPresent ? "drawn" : "ABSENT"),-6} state {_regions[region]}"));
        }

        return lines;
    }

    /// <summary>One observed integer channel: its range and how many distinct values it took.</summary>
    private sealed class Channel
    {
        private readonly HashSet<int> _distinct = [];

        public int Seen { get; private set; }

        public int Min { get; private set; } = int.MaxValue;

        public int Max { get; private set; } = int.MinValue;

        public void Add(int value)
        {
            Seen++;
            Min = Math.Min(Min, value);
            Max = Math.Max(Max, value);
            if (_distinct.Count < 4096)
            {
                _distinct.Add(value);
            }
        }

        public override string ToString() => Seen == 0
            ? "—"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"[{Min}..{Max}] {_distinct.Count} distinct{(_distinct.Count <= 1 ? " ★CONSTANT" : string.Empty)}");
    }
}
