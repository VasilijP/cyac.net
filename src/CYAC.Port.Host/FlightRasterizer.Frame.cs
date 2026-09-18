using System.Diagnostics;
using System.Globalization;
using CYAC.Port.Core.Model.Cockpit;
using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Sim;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Effects;
using CYAC.Port.Core.Sim.Combat.Lifecycle;
using CYAC.Port.Core.Sim.Combat.Player;
using CYAC.Port.Core.Sim.Flight;
using CYAC.Port.Core.Sim.Session;
using CYAC.Port.Host.Configuration;
using CYAC.Port.Host.Headless;
using CYAC.Port.Host.Input;
using CYAC.Port.Host.Menu;
using CYAC.Port.Host.Sound;
using CYAC.Port.Host.Settings;
using CYAC.Port.Host.Sim;
using CYAC.Port.Host.Stats;
using CYAC.Port.Render;
using CYAC.Port.Render.Cockpit;
using CYAC.Port.Render.Ground;
using CYAC.Port.Render.Map;
using mode13hx;
using mode13hx.Controls;
using mode13hx.Presentation;
using mode13hx.Util;

namespace CYAC.Port.Host;

/// <summary>
/// The FRAME LOOP: <see cref="Render"/> — step the simulation by the elapsed wall time,
/// build one <see cref="FlightSnapshot"/>, draw the scene, the cockpit, the HUD, the
/// windows and the readout once — and the per-phase timing it accumulates.
/// </summary>
/// <remarks>The reader's map of every one of this class's files is at the top of
/// <c>FlightRasterizer.cs</c>.</remarks>
public sealed partial class FlightRasterizer
{
    private readonly IFlightInputSource _input;

    private readonly bool _readout;

    private readonly Stopwatch _wall = Stopwatch.StartNew();

    private readonly MeshModel? _effectProbeMesh;

    /// <summary>The AGE the effect probe's instance is drawn at, or −1.</summary>
    private readonly int _effectProbeAge;

    /// <summary>The fork byte it carries.</summary>
    private readonly byte _effectProbeFork;

    private readonly double _effectProbeRange;

    /// <summary>How many <c>--effect-probe</c> instances to place (a burst, for measuring).</summary>
    private readonly int _effectProbeCount;

    /// <summary>
    /// The spacing of the <c>--effect-probe-count</c> grid, as a fraction of the probe range.
    /// </summary>
    /// <remarks>
    /// 0.18 of the range puts a 5 × 4 grid of twenty probes across roughly the middle half of a
    /// 102.68°-wide frame, so every one of them is on screen and none of them overlap.
    /// </remarks>
    private const double EffectProbeGridPitch = 0.18;

    /// <summary>The span the smoke probe puff is given: a WRECK-COLUMN puff's 25 frame units
    /// (<c>SmokePuffTable.LifetimeTrail</c>) in Q8 frame time.</summary>
    private const int ProbeSmokeSpanFrameTime = Core.Sim.Combat.Effects.SmokePuffTable.LifetimeTrail << 8;

    private readonly bool _autoRespawn;

    private readonly int _gearAngleOverride;

    private readonly CameraPose? _cameraOverride;

    private readonly Vec3 _cameraStep;

    private readonly bool _hidePlayerMesh;

    private int _cameraSteps;

    private double _lastHostSeconds;

    /// <summary>
    /// mode-13hx's <c>--dofps</c> (<c>-d</c>): the frametime chart drawn at frame end, exactly as
    /// <c>TestRasterizer</c> draws it — fed by <c>FrameBuffer.Fps</c>, which the presenter records.
    /// </summary>
    private readonly FrameChart _frameChart = new(8, 0, 120, Func.EncodePixelColor(0, 0xFF, 0), FrameBuffer.Fps);

    /// <summary>The <c>--dofps</c> toggle, shared with mode-13hx's control registry.</summary>
    private readonly ControlParam<bool> _drawFrameChart;

    /// <summary>The cockpit layer: the panel, its alpha channel, its regions and its dials.</summary>
    private readonly CockpitRenderer _cockpit = new();

    private readonly CockpitFont? _cockpitFont;

    /// <summary>
    /// The four IN-FLIGHT OVERLAY WINDOWS' chrome layer, and the live visibility byte
    /// <c>g_inflight_overlay_visibility [0xF1CB]</c> that Shift-1..Shift-4 flip.
    /// </summary>
    private readonly OverlayWindowRenderer _overlayWindows = new();

    private OverlayWindowFrameStats _lastOverlayWindows;

    /// <summary>The HUD overlay: the eleven widgets, the gunsight and the message strip.</summary>
    private readonly HudRenderer _hud = new();

    private readonly HudDecal? _hitDecal;

    /// <summary>The F9 MAP: the layer, its knobs and the little state F9 owns.</summary>
    private readonly MapView _map = new();

    private readonly bool _mapHud;

    // The presented frame's wall time by phase (see FrameTimingLine).
    private int _phaseFrames;

    private double _phaseTotalMilliseconds;

    private double _phaseSimMilliseconds;

    private double _phaseSceneMilliseconds;

    private double _phaseWorldMilliseconds;

    private double _phaseMapMilliseconds;

    private double _phaseCockpitMilliseconds;

    private double _phaseHudMilliseconds;

    private double _phaseWindowsMilliseconds;

    private double _phaseRestMilliseconds;

    private double _cockpitMilliseconds;

    private double _cockpitPanelMilliseconds;

    private double _cockpitHorizonMilliseconds;

    private double _cockpitRegionsMilliseconds;

    private double _cockpitDialsMilliseconds;

    /// <inheritdoc/>
    public void Render(FrameBuffer buffer, double secondsSinceLastFrame)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        FrameDescriptor? frame = buffer.StartNextFrame();
        _frameWidth = buffer.Width;
        _frameHeight = buffer.Height;

        // FRAME PHASE TIMER: where a presented frame's wall time goes (input + sim step, scene
        // assembly, the 3-D world, the map, the cockpit layer, the HUD, the four windows, the rest).
        // Toggling the cockpit costs frame time; the cockpit layer's own timer shows it is not the
        // layer, and this says which phase pays it.
        long phaseStart = Stopwatch.GetTimestamp();
        long phaseMark = phaseStart;

        // THE PAUSE.  While the ESC bar is up the session does not step, but the whole presentation
        // path still runs from the frozen state on every presented frame: the world, the cockpit,
        // the HUD and the menu are all redrawn, so a render option changed in the menu shows up at
        // once ON THE SAME SCENE.  Everything below therefore runs exactly as it does in flight,
        // with three things pinned:
        //     · the elapsed time given to any presentation consumer is 0 (below), so nothing ages;
        //     · the sound path is paused, because its own clock is the frame accumulator;
        //     · --camera's step counter does not advance.
        //   The pose smoother, the gunsight's attitude ring and the HUD message expiry need no
        //   pinning: all three are keyed off the SIMULATION's clock, which is frozen by
        //   construction.
        bool paused = _menu is { IsOpen: true };
        double elapsed = paused ? 0.0 : secondsSinceLastFrame;

        // The host's own strip line ages in WALL seconds, because the event it announces resets
        // the accumulator every other producer stamps its expiry against.
        if (!paused && _hostNoticeSeconds > 0.0)
        {
            _hostNoticeSeconds -= secondsSinceLastFrame;
            if (_hostNoticeSeconds <= 0.0)
            {
                _hostNoticeSeconds = 0.0;
                _hostNotice = null;
            }
        }

        FlightInputFrame input = _input.Sample(secondsSinceLastFrame);

        // THE DOOR OUT OF A SORTIE.  The overlay was F1's marked PLACEHOLDER and is gone in menu
        // mode: the original cuts straight to the DEBRIEFING screen, so the sortie ends the moment
        // the verdict stands and NO key is needed.  The hold before that moment is the one the port
        // already models — the fate machine's phases, or the End Mission the ESC menu just ran —
        // so the screen comes exactly when the flight loop would have left, not a frame earlier.
        // Direct mode is untouched: it keeps the overlay and its `R`. The ESC bar is excluded
        // because End Mission closes it in the same act that sets the verdict, and a bar left open
        // would otherwise be torn down mid-frame.
        if (MenuMode && !MenuOpen && _session.Outcome?.Debrief is not null)
        {
            ReturnToMenuRequested = true;
        }

        // The menu eats its own keys first, and while it is up NOTHING else reads the frame: the
        // arrows, Enter and ESC are flight controls when the bar is down and menu keys when it is
        // up, and one physical key cannot mean both at once.
        _menu?.HandleInput(
            input.MenuKeys, input.MenuTypeAhead, input.Held.Left, input.Held.Right);
        paused = _menu is { IsOpen: true };
        elapsed = paused ? 0.0 : secondsSinceLastFrame;
        if (paused)
        {
            input = new FlightInputFrame(default, NoKeys);
        }

        // The pilot's own Shift-E.  It is an EDGE, and the session consumes it on its next
        // simulation step so the arm runs where the original's key ladder runs it.
        if (input.Eject)
        {
            _session.EjectRequested = true;
        }

        // The three audio_threshold_bump_* trampolines fire off the status-flag EDGE the
        // cockpit-key toggles make (image@0x2A4B2 / 0x2A4C9 / 0x2A4EE, each right after its own
        // `xor [master+0x124], bit`), so the sound path is handed the byte before and after.
        byte statusBefore = (byte)_session.State.Aircraft.StatusFlags;

        // The map's own keys, honoured only while the map is up (the view this frame opened
        // with).  ZOOM is the original's own + / − pair — gauge_zoom_in_keyhandler
        // @image@0x0D8B5 clamps at 12 and gauge_zoom_out_keyhandler @image@0x0D8C4 at 7; the
        // centre (m) and fit (n) keys are port additions.
        IReadOnlyList<int> cockpitKeys = input.CockpitKeys;
        if (_mapMode != MapMode.Off && _view == ViewMode.Map)
        {
            if (input.MapZoomStep != 0)
            {
                int level = _mapOptions.ZoomLevel <= 0
                    ? MapProjection.MinZoomLevel
                    : _mapOptions.ZoomLevel;
                _mapOptions = _mapOptions with
                {
                    ZoomLevel = Math.Clamp(
                        level + input.MapZoomStep,
                        MapProjection.MinZoomLevel,
                        MapProjection.MaxZoomLevel),
                    CentreOnPlayer = true,
                };

                // The same physical key is the port's throttle: while the map is up it is the ZOOM
                // key alone, so the throttle must not also move.
                cockpitKeys = WithoutThrottleKeys(input.CockpitKeys);
            }

            if (input.MapCentre)
            {
                _mapOptions = _mapOptions with
                {
                    CentreOnPlayer = true,
                    ZoomLevel = _mapOptions.ZoomLevel <= 0
                        ? MapProjection.MinZoomLevel
                        : _mapOptions.ZoomLevel,
                };
            }

            if (input.MapFit)
            {
                _mapOptions = _mapOptions with { ZoomLevel = 0, CentreOnPlayer = false };
            }
        }

        // The four OVERLAY WINDOW toggles (Shift-1..Shift-4).  Presentation only: the live byte
        // is the port's own mirror of g_inflight_overlay_visibility [0xF1CB] and no window ever
        // writes simulation state.
        if (input.OverlayToggles is { Count: > 0 } toggles)
        {
            foreach (CockpitOverlayFlags window in toggles)
            {
                _overlays ^= window;
            }

            // The ESC menu's four window rows mirror the live byte, so a key press is
            // announced to whoever holds them (PortSettingsStore.Attach).
            OverlaysChanged?.Invoke(_overlays);
        }

        // The RENDER SCRUTINY keys.  All presentation: K, L and I rewrite fields of the scene
        // options the next Render reads, F12 / P arms a capture the END of this frame performs (so
        // the picture is the composed frame, cockpit and HUD included, and the dump is the scene
        // this same frame handed the renderer).
        if (input.DebugKeys is { Count: > 0 } debugKeys)
        {
            foreach (DebugKey key in debugKeys)
            {
                HandleDebugKey(key);
            }
        }

        // The `advisor<n>` SCRIPT word: raise an advisory the way a kernel would.  There is no key
        // for it (the original has none either); it exists so a headless capture can put each of
        // Chuck's twenty-one messages and all three portraits on screen.  It goes through the DIRECT
        // door, so the cooldown still applies exactly as it does to a kill or a bandit.
        if (input.AdvisorCode >= 0 && input.AdvisorCode <= byte.MaxValue)
        {
            EnsureAdvisorWired();
            _advisor?.Raise((byte)input.AdvisorCode, AdvisorDispatchKind.Direct);
        }

        // The RADAR key.  The manual's R (p.51) toggles the radar, and since it is the port's R
        // too: neither restart accelerator is mapped, and the ESC menu's Restart Mission is the
        // one door.  The toggle is presentation only: it flips the display mode the original keeps
        // in region_1.flags bit 5 (image@0x0E1E3) and, through RadarState.PlayerEmitting,
        // PUBLISHES the emitter bit the same handler sets in the player's engagement block
        // (image@0x0E1DE) — which is simulation state W6 must write, not this layer.
        if (input.RadarToggle && HasRadar)
        {
            RadarOn = !RadarOn;
            RadarKeyPresses++;

            // The sweep clock is re-armed on the ON→OFF edge only (the SET→CLEAR transition test at
            // image@0x0E1E8/0x0E1ED), to frame_time + 469 (image@0x0E1F6).
            _radarSweepDeadline = RadarOn
                ? 0u
                : unchecked(_session.Clock.FrameTimeAccumulator + (uint)RadarScope.SweepDuration);
        }

        // The MAP WINDOW's ZOOM keys, `.` and `,` (manual p.14's map window; ladder arms
        // image@0x012B2 and image@0x012C7).  Presentation only — [0x3234] is a display scale, and
        // the port keeps its own mirror; the clamps are the original's own.
        if (input.WindowZoomStep != 0)
        {
            int shift = input.WindowZoomStep < 0
                ? MapWindow.ZoomIn(MapScaleShift)
                : MapWindow.ZoomOut(MapScaleShift);
            MapZoomKeyPresses++;
            MapScaleShift = shift;
        }

        // The two SITUATIONAL-AWARENESS keys, Ctrl-Z and Ctrl-A (manual p.52).  Both ladder arms
        // are gated on being in flight (cmp byte [0xC31C],0 / je @image@0x01294 and image@0x012A7)
        // and both call the SAME routine with the bogey/friendly selector.
        if (input.NearestBogey || input.NearestFriendly)
        {
            PostNotice(NearestObjectAdvisory(bogeys: input.NearestBogey));
            AdvisoryRequests++;
        }

        // The ONE line the pause turns off.  Everything below still runs, so the frame is
        // re-rendered from the frozen state rather than held as a picture.
        if (!paused)
        {
            _session.Advance(
                secondsSinceLastFrame,
                input.Held,
                cockpitKeys,
                input.Trigger,
                input.CombatKeys,
                input.WeaponStep);
        }

        Audio?.NoteCockpitToggle(statusBefore, (byte)_session.State.Aircraft.StatusFlags);
        Phase(ref phaseMark, ref _phaseSimMilliseconds);

        // The SORTIE STATISTICS.  The sortie is counted the moment it ends — which is this frame,
        // because MissionOutcome.Observe runs inside the step above — and BEFORE the restart check
        // below, so a debrief followed by `R` is one sortie and not two.
        Stats?.Observe(_session);

        // The flight-end verdict.  DamageCheckStage.CheckCrash (S4 → S5) writes master[+0x122] =
        // 0 when the aircraft hits the ground outside a landing zone (`damage_or_crash_check
        // @image@0x2C07C` + `crash_conditions_valid @image@0x2C25C`), and ControlIntegrationStage
        // then freezes the aircraft.  The original's own next step is `flight_session_end
        // @image@0x30328` → the debrief; the PoC restarts. with the fate machine on, the restart
        // waits for the whole death sequence: --respawn now means "when the sortie is OVER", not
        // "the instant the verdict lands".
        bool over = _session.Fate is { Enabled: true, Passive: false } fate ? fate.Ended : FlightEnded;

        // A MID-FLIGHT restart bypasses the `over` gate; the post-sortie one waits for the end.
        // Neither accelerator is mapped, so what reaches here is the ESC menu's Restart Mission,
        // --respawn and the script words.
        if (_reopen is not null && (input.RestartNow || (over && (_autoRespawn || input.Respawn))))
        {
            Reopen(input.RestartNow);
        }

        // A view key is a PRESENTATION decision: it never reaches the simulation.
        if (input.RequestedView is { } requested)
        {
            // F9 selects the MAP (view id 0x0C, image@0x33288).  --map off keeps the previous
            // view instead, which is what the PoC did before the map existed.
            if (requested != ViewMode.Map || _mapMode != MapMode.Off)
            {
                if (requested == ViewMode.Map && _view != ViewMode.Map)
                {
                    _viewBeforeMap = _view;
                }

                _view = requested;
            }
        }
        else if (input.ToggleView)
        {
            _view = _view == ViewMode.CockpitForward ? ViewMode.ExternalChase : ViewMode.CockpitForward;
        }

        // Whether the map is up AFTER this frame's view key: what the frame draws.
        bool mapUp = _mapMode != MapMode.Off && _view == ViewMode.Map;

        // BACKSPACE shows and hides the cockpit, exactly as the original's key ladder does
        // (gameplay_key_backspace_cockpit_toggle @image@0x013D8 flips g_cockpit_visible_flag
        // [0xE471]).  It is PRESENTATION and never reaches the simulation.
        if (input.ToggleCockpit && _cockpitArt is not null)
        {
            // Through the settings store when there is one, so the key and the dialog's own row can
            // never disagree and the choice survives the run (the original persists it in
            // yeager.cfg byte 0x1F).
            ToggleSetting("cockpit", () =>
                _cockpitOptions = _cockpitOptions with { Enabled = !_cockpitOptions.Enabled });
        }

        // The HUD text overlay's own switch, g_flight_info_visible [0xB0].
        if (input.ToggleFlightInfo)
        {
            ToggleSetting("flight-info", () => _flightInfoVisible = !_flightInfoVisible);
        }

        // The NAV key.  Both ladder arms are guarded by the in-mission flag (cmp byte [0xC31C],0 at
        // image@0x01274 / image@0x01285), so a Test Flight without a mission session never reaches
        // the thunks — and a mission with no waypoints reaches them and finds nothing, which is the
        // original's own zero case.
        if (input.NavStep != 0)
        {
            _session.Mission?.CycleNavWaypoint(input.NavStep);
        }

        FlightSnapshot view = _session.Snapshot();

        // The DEATH CAMERA's view substitution has to be decided ONCE, before anything else reads
        // "which view is this".  H9 computed it inside the camera block, so the cockpit gate, the
        // HUD's forward-view mask and the player-mesh gate all went on reading the player's CHOSEN
        // view: with F1 selected at the moment of the crash the camera swung out to the circling
        // view while the wreck itself stayed hidden (the interior-view rule) and the HUD kept
        // drawing its forward-view-only waterline and pipper over it.
        CameraSubject? deathSubject = FateSubject();

        // In the INSET map mode the world is still drawn, so the camera (and the HUD's own view
        // gate) belong to the view F9 interrupted, not to the map.
        ViewMode chosenView = mapUp && _mapMode == MapMode.Inset ? _viewBeforeMap : _view;
        ViewMode effectiveView = deathSubject is not null && IsCockpitInterior(chosenView)
            ? ViewMode.Circling
            : chosenView;
        EffectiveView = effectiveView;
        CockpitState cockpitState = BuildCockpitState(view, effectiveView);

        // The INSTRUMENT CENSUS observes the same state the cockpit layer draws from, one row per
        // dial slot and per region.  Pure observation (--instrument-census).
        if (_cockpitArt is not null && Instruments is { } census)
        {
            census.Observe(in cockpitState, _cockpitArt);
            if (census.NavWaypoints.Length == 0)
            {
                census.NavWaypoints = DescribeNavWaypoints();
            }
        }

        // The 3-D world fills the aircraft's VIEWPORT ROWS when the cockpit is painted and the
        // whole window when it is not (image@0x015F3 vs image@0x01641).  The world keeps the full
        // window WIDTH either way, so the horizontal field of view is untouched and the vertical
        // one follows from the viewport's height (CockpitScale).
        CockpitScale cockpitScale = _cockpitArt is null
            ? CockpitScale.For(buffer.Width, buffer.Height, default, CockpitFit.Stretch, false)
            : CockpitRenderer.ScaleFor(
                buffer.Width, buffer.Height, _cockpitArt, _cockpitOptions, cockpitState);
        // --cockpit-view: VIEWPORT (the original) keeps the world in the aircraft's own viewport
        // rows, so the horizon sits where image@0x118A4's (y_min+y_max)>>1 puts it; SCREEN is the
        // port's option — the world fills the window and the panel is painted over it, so the
        // horizon is at the middle of the screen. is the PANEL painted this frame at all?  (Forward
        // view, alive, cockpit on.) It is what tells the SCREEN option apart from a view that
        // simply has no cockpit.
        bool cockpitPainted = _cockpitArt is not null && _cockpitFont is not null
            && CockpitRenderer.CockpitDrawn(in _cockpitOptions, in cockpitState);
        int worldRows = CockpitViewFullScreen && cockpitPainted
            ? buffer.Height
            : Math.Clamp(cockpitScale.ViewportHeightPixels, 1, buffer.Height);

        Span<uint> full = buffer.Data.AsSpan(frame.Offset, buffer.FrameSize);
        PixelTarget target = new PixelTarget(
            full,
            buffer.Width,
            worldRows,
            buffer.Height,
            PixelChannelOrder.BlueHigh);   // mode-13hx's Func.EncodePixelColor packs blue high

        // The world is drawn STRAIGHT INTO THE FRAME.
        PixelTarget world = target;
        _worldRows = worldRows;

        // --camera pins the eye for inspection; it is PRESENTATION state and the simulation is
        // untouched by it, exactly like --gear-angle.
        CameraPose camera;
        if (_cameraOverride is { } pinned)
        {
            camera = pinned with
            {
                EyeX = pinned.EyeX + (_cameraStep.X * _cameraSteps),
                EyeY = pinned.EyeY + (_cameraStep.Y * _cameraSteps),
                EyeZ = pinned.EyeZ + (_cameraStep.Z * _cameraSteps),
            };
            if (!paused)
            {
                _cameraSteps++;
            }
        }
        else
        {
            // The eighteen view keys.  The renderer is HANDED the poses: the host resolves
            // g_lockon_target [0x00BC] and the newest live projectile out of the sim. a dead pilot
            // does not watch from his own cockpit: once the fate machine has fired, an interior
            // view becomes the CIRCLING view (Shift-F9) of whatever the sequence is about.  It is
            // presentation only, the view keys still work, and the player's own choice is
            // remembered in _view.
            CameraSubject subject = deathSubject ?? CameraSubject.FromSnapshot(view);
            camera = _rig.Build(
                effectiveView,
                subject,
                TargetSubject(),
                MissileSubject(),
                _session.SimulatedSeconds);
        }

        _lastCamera = camera;   // For the scene dump

        // The sound path's per-frame step: continuous_audio_state_update @image@0x29D2E, with the
        // camera as the view anchor object_range_from_view_anchor measures from. the whole camera
        // pose, because the positional layer pans from its right vector. the sound path is paused
        // with the simulation: its own clock is the frame accumulator the frozen session no
        // longer advances, and a WAV run must not accrue seconds of silence while the player
        // reads a menu.
        if (paused)
        {
            Audio?.Pause();
        }
        else
        {
            Audio?.PerFrame(_session, _view, camera, elapsed);
        }

        _suppressObjectRef = effectiveView == ViewMode.TargetCockpit && !_rig.FellBack && _session.Mission is { } inside
            ? inside.Combat.Registers.Word(LockOnTargetWord)
            : (ushort)0;

        bool mapFullScreen = mapUp && _mapMode == MapMode.Full;
        if (mapFullScreen)
        {
            // The original's own answer: the map REPLACES the 3-D view.  Nothing of the world is
            // drawn — mission_per_frame_render_phase's [0xC320] == 0x0C branch jumps clean over the
            // 3-D path (image@0x01570 → image@0x01595).
            LastScene = default;
        }
        else if (_snapshot is null)
        {
            LastScene = new SceneFrameStats(
                // The ONE place the background is still PAINTED rather than resolved: the frames
                // before a scene snapshot exists, which have no display list to push. It is the
                // same BackgroundField the resolve evaluates, so the two cannot drift.
                HorizonRenderer.Render(
                    world,
                    camera.PitchRadians,
                    camera.RollRadians,
                    _lens,
                    _colors,
                    _sceneOptions.Horizon,
                    _sceneOptions.HorizonBandDegrees,
                    camera.Eye.Y,
                    _sceneOptions.Edges),
                0, 0, 0, 0, 0, 0);
        }
        else
        {
            _snapshot.BeginFrame();

            // The player's own aircraft is scene content only when the camera is OUTSIDE it — which
            // is every view whose flag byte does NOT carry bit 1, "cockpit-interior" (bit 1 is set
            // on exactly F1..F6). the WRECK is scene content whenever the camera is outside it, and
            // after a fate trigger the camera is outside it even when the player's chosen view is
            // F1..F6 (the death camera).  Gating on _view left the wreck invisible in exactly the
            // frames the death camera exists to show. once the aircraft has come to REST it is a
            // wreck, and the original leaves a destroyed aircraft as a crater
            // (engagement_kill_finalize re-classes the victim to `crater` [0x52A2]); the crater and
            // its smoke are H7's, so the mesh goes. --wreck-mesh on restores H9 D4's look.
            // Superseded here; the death camera still has the crater, the fireball and the smoke to
            // show.
            bool atRest = _session.Fate is { Enabled: true, Passive: false, Phase: PlayerFatePhase.Wreck or PlayerFatePhase.Ended };
            if (_playerMesh is not null && !_hidePlayerMesh && !IsCockpitInterior(effectiveView)
                && (!atRest || _wreckMesh))
            {
                _snapshot.Add(new SceneInstance(
                    _playerMesh,
                    view.X / 256.0,
                    view.Y / 256.0,
                    view.Z / 256.0,
                    view.HeadingDegrees,
                    view.PitchDegrees,
                    view.RollDegrees,
                    _gearAngleOverride >= 0 ? _gearAngleOverride : _session.GearAngleBam,
                    // The AFTERBURNER PLUME.  Its leaves ship tagged DRAWN and the original's
                    // prepare callback hides them every frame the burner is cold (FlameLeaves;
                    // mesh_lod_prepare_gear_and_flame_state @image@0x2D8E9 piece 1), so a port that
                    // never runs the callback flies a MiG-21 and an F-4 with the plume permanently
                    // lit.  The callback's "is this the object the
                    // camera follows" half is not modelled: the player's own aeroplane is the only
                    // one the port ever lights (see the enemy site).
                    HiddenLeafNodes: FlameLeaves.Hidden(
                        _session.AircraftBasename,
                        ((AircraftStatusFlags)view.StatusFlags).HasFlag(AircraftStatusFlags.Afterburner)),
                    // The one instance the frame builds an INTERIOR MASK for: the player's own
                    // aeroplane is the model that gets turned "like a barbecue" and scrutinised
                    // from every angle, so its shared-edge seams are the ones that show.
                    // --seam-mask off gates it in the scene options.
                    SeamMask: true));
            }

            AddCloudDeck(camera);
            AddGroundGrid(camera);
            AddSun(camera);
            if (_effectProbeMesh is not null)
            {
                // --effect-probe-count places a GRID of probes across the view at the probe range,
                // so a per-instance cost (the tracer halo's) can be measured at a chosen count.
                // One probe (the default) sits exactly where it always did, straight ahead: the
                // grid is centred and a 1×1 grid has a zero offset.
                int count = Math.Max(1, _effectProbeCount);
                int columns = (int)Math.Ceiling(Math.Sqrt(count));
                double pitch = _effectProbeRange * EffectProbeGridPitch;
                for (int i = 0; i < count; i++)
                {
                    int column = i % columns;
                    int rowIndex = i / columns;
                    int rows = (count + columns - 1) / columns;
                    Vec3 ahead = camera.Eye
                                 + (camera.Forward * _effectProbeRange)
                                 + (camera.Right * ((column - ((columns - 1) / 2.0)) * pitch))
                                 + (camera.Up * ((rowIndex - ((rows - 1) / 2.0)) * pitch));
                    _snapshot.Add(new SceneInstance(
                        _effectProbeMesh,
                        ahead.X,
                        ahead.Y,
                        ahead.Z,
                        HeadingDegrees: 0.0,
                        EffectAgeFrameTime: _effectProbeAge,
                        EffectFork: _effectProbeFork,
                        // `--effect-probe smoke --effect-age A` photographs a WRECK COLUMN puff
                        // (kind 3, 25-s span) at A/256 of its life; --effect-fork picks another
                        // kind (0..4).  -1 leaves the static records.
                        SmokePuff: _effectProbeAge >= 0
                            && string.Equals(_effectProbeMesh.Basename, "smoke", StringComparison.Ordinal)
                            ? new Core.Sim.Combat.Effects.SmokePuffState(
                                (byte)(_effectProbeFork is >= 1 and <= 4 ? _effectProbeFork : 3),
                                _effectProbeAge * ProbeSmokeSpanFrameTime / 256,
                                ProbeSmokeSpanFrameTime)
                            : null));
                }
            }
            AddCombatObjects(effectiveView);
            AddGunnery();
            AddSmoke();
            Phase(ref phaseMark, ref _phaseSceneMilliseconds);
            LastScene = _scene.Render(
                world, _snapshot, camera, _lens, _colors, _sceneOptions, Census, ProjectionCensus);
            Phase(ref phaseMark, ref _phaseWorldMilliseconds);
        }

        // The MAP, over the whole window or in its corner.  It goes before the cockpit and the HUD
        // because in the original those two are the layers ON TOP of it: the panel is not painted in
        // any view but F1 (image@0x010AA), and the HUD's own text widgets ARE drawn over the map —
        // a captured frame of the original
        if (mapFullScreen)
        {
            PixelTarget whole = new PixelTarget(
                full, buffer.Width, buffer.Height, buffer.Height, PixelChannelOrder.BlueHigh);
            _lastMap = _map.Render(
                whole, BuildMapScene(in view), _mapOptions with { Mode = MapMode.Full }, _cockpitFont);
            _mapFrames++;
            _mapMilliseconds += _lastMap.Milliseconds;
        }

        Phase(ref phaseMark, ref _phaseMapMilliseconds);

        // The cockpit goes on last of the pixel layers, over a world that has already been clipped
        // to the viewport rows; the HUD overlay then goes over the cockpit.
        if (_cockpitArt is not null && _cockpitFont is not null
            && (worldRows < buffer.Height || cockpitPainted))
        {
            // Whatever the panel does not cover is not the world's either: the clear was a second
            // full write of every row below the viewport, 1.1 ms of the cockpit phase's 2.7 ms at 4K
            // (FrameTimingLine).  The renderer now composites the black into its cached runs from
            // `worldRows` down, so the rows are written once. under --cockpit-view screen the world
            // already fills those rows and the panel is painted OVER it, so nothing is black
            // (worldRows == the height).
            PixelTarget below = new PixelTarget(
                full, buffer.Width, buffer.Height, buffer.Height, PixelChannelOrder.BlueHigh);
            _lastCockpit = _cockpit.Render(
                below, _cockpitArt, _cockpitFont, cockpitState, _cockpitOptions,
                worldRows < buffer.Height ? worldRows : -1);
            if (_lastCockpit.PanelPixels > 0)
            {
                // Where the cockpit layer's frame time goes (see CockpitTimingLine).
                _cockpitFrames++;
                _cockpitMilliseconds += _lastCockpit.Milliseconds;
                _cockpitPanelMilliseconds += _lastCockpit.PanelMilliseconds;
                _cockpitHorizonMilliseconds += _lastCockpit.HorizonMilliseconds;
                _cockpitRegionsMilliseconds += _lastCockpit.RegionsMilliseconds;
                _cockpitDialsMilliseconds += _lastCockpit.DialsMilliseconds;
            }
        }
        else
        {
            _lastCockpit = default;
        }

        Phase(ref phaseMark, ref _phaseCockpitMilliseconds);

        // The HUD OVERLAY goes over everything, exactly as the original's sole call to
        // hud_per_frame_draw at image@0x01708 does, after the cockpit and before the page flip.
        if (_cockpitArt is not null && _cockpitFont is not null && (!mapFullScreen || _mapHud))
        {
            PixelTarget whole = new PixelTarget(
                full, buffer.Width, buffer.Height, buffer.Height, PixelChannelOrder.BlueHigh);
            HudState hud = BuildHudState(
                view, camera, cockpitState, cockpitScale, buffer.Width, worldRows, effectiveView);
            _lastHud = _hud.Render(whole, _cockpitArt, _cockpitFont, hud, _cockpitOptions, _hitDecal);
        }
        else
        {
            _lastHud = default;
        }

        // The four OVERLAY WINDOWS go over the HUD, because a window overdraws the world and the
        // designator label under it (21_mig21_cfg0F_target.png clips the label's "MiG" with the
        // envelope window).  The HUD's own corners are suppressed, not covered.
        Phase(ref phaseMark, ref _phaseHudMilliseconds);
        if (_cockpitArt is not null && _cockpitFont is not null && !mapFullScreen)
        {
            PixelTarget whole = new PixelTarget(
                full, buffer.Width, buffer.Height, buffer.Height, PixelChannelOrder.BlueHigh);
            OverlayWindowState windows = BuildOverlayWindowState(in view);
            Instruments?.ObserveTarget(in windows, _designators);
            _lastOverlayWindows = _overlayWindows.Render(
                whole, _cockpitArt, _cockpitFont, windows, _cockpitOptions);

            // The MAP window's dots go into the face the chrome pass just cleared, in the
            // original's own order (fill, plot, own-ship marker last: image@0x0D611 → 0x0D6CE).
            // The census counts what the RENDERER actually drew, not a re-derivation of it.
            _lastMapDots = _overlayWindows.RenderMapContents(
                whole, _cockpitArt, windows, _cockpitOptions);
            Instruments?.ObserveMap(in windows, _lastMapDots);

            // The ENVELOPE window's four-colour plot and its marker go into the face the chrome
            // pass just painted, in the drawer's own order (three rectangles, the curve's filled
            // polygon over them, the marker last: image@0x0ECB1 → 0x0EDA0).
            _lastEnvelopeShapes = _overlayWindows.RenderEnvelopeContents(
                whole, _cockpitArt, windows, _cockpitOptions);
            Instruments?.ObserveEnvelope(in windows, _lastEnvelopeShapes);

            // The YEAGER window's PORTRAIT and its two lines, in the drawer's own order (the panel
            // fill, the title, gfx_plain_blit at image@0x0EF72, then the two lines inside the clip
            // rectangle).  Drawn every frame Chuck is speaking and no frame else.
            _lastAdvisorLines = _overlayWindows.RenderAdvisorContents(
                whole, _cockpitArt, _cockpitFont, windows, _cockpitOptions);
            Instruments?.ObserveAdvisor(in windows, _lastAdvisorLines);

            // The TARGET window's SILHOUETTE goes into the content rectangle the chrome just
            // cleared, and its five text rows go over the silhouette — the original's own order
            // (polygon_fill_mesh_render_setup @image@0x0A78E, then the labels from image@0x0A7E1).
            // The silhouette needs its own PixelTarget over that rectangle, so it is drawn here
            // rather than inside the chrome pass. AND the window must be UP: without that test,
            // turning the window off removes only its frame and leaves the silhouette drawing in
            // the sky.  The chrome pass and RenderTargetContents both test the [0xF1CB] bit, so
            // this call tests it too.
            if (windows.TargetPresent && windows.Visible.HasFlag(CockpitOverlayFlags.Target))
            {
                // The silhouette's sub-target must follow the WINDOW's own pixel scale, not the
                // panel's, or it would keep the old enlarged rectangle while the chrome around it
                // shrank.  Same substitution the chrome uses.
                PixelTarget panelTarget = new PixelTarget(
                    full, buffer.Width, buffer.Height, buffer.Height, PixelChannelOrder.BlueHigh);
                CockpitScale windowScale = OverlayWindowRenderer.WindowScale(
                    CockpitScale.For(
                        buffer.Width, buffer.Height, _cockpitArt.Viewport, _cockpitOptions.Fit,
                        cockpitDrawn: true),
                    OverlayWindowLayout.TargetSlotX,
                    OverlayWindowRenderer.PixelScale(panelTarget, in _cockpitOptions),
                    rightPinned: true);
                DrawTargetSilhouette(full, buffer.Width, buffer.Height, windowScale);
                PixelTarget panelText = new PixelTarget(
                    full, buffer.Width, buffer.Height, buffer.Height, PixelChannelOrder.BlueHigh);
                _overlayWindows.RenderTargetContents(
                    panelText, _cockpitArt, _cockpitFont, windows, _cockpitOptions);
            }
        }
        else
        {
            _lastOverlayWindows = default;
        }

        Phase(ref phaseMark, ref _phaseWindowsMilliseconds);

        // The INSET map is the LAST pixel layer, because in this mode the cockpit and the HUD are
        // still drawn and it must not disappear under the panel.
        if (mapUp && !mapFullScreen)
        {
            // PORT ADDITION — a third of the window, bottom-right, inside a margin.
            int inW = Math.Max(120, buffer.Width / 3);
            int inH = Math.Max(90, buffer.Height / 3);
            int margin = Math.Max(4, buffer.Width / 120);
            int x0 = Math.Max(0, buffer.Width - inW - margin);
            int y0 = Math.Max(0, buffer.Height - inH - margin);
            PixelTarget inset = new PixelTarget(
                full[((x0 * buffer.Height) + y0)..],
                inW, inH, buffer.Height, PixelChannelOrder.BlueHigh);
            _lastMap = _map.Render(
                inset, BuildMapScene(in view), _mapOptions with { Mode = MapMode.Inset }, _cockpitFont);
            _mapFrames++;
            _mapMilliseconds += _lastMap.Milliseconds;
        }

        Phase(ref phaseMark, ref _phaseRestMilliseconds);
        _phaseFrames++;
        _phaseTotalMilliseconds += Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;

        _hostFrames++;
        double now = _wall.Elapsed.TotalSeconds;
        double delta = now - _lastHostSeconds;
        if (delta > 0)
        {
            // A slow exponential average: a per-frame reciprocal is unreadable.
            _hostFps = _hostFps <= 0 ? 1.0 / delta : (_hostFps * 0.95) + (0.05 / delta);
        }

        _lastHostSeconds = now;

        if (_readout && !mapFullScreen)
        {
            DrawReadout(frame.Canvas, view);
        }

        if (!mapFullScreen)
        {
            DrawNavReadout(frame.Canvas, view, buffer.Width, worldRows);
        }
        DrawFateBanner(frame.Canvas, buffer.Width, buffer.Height);
        DrawDebrief(frame.Canvas, buffer.Width, buffer.Height);

        // The ESC menu bar goes over EVERYTHING (the original's own order: the modal draws after
        // the frame is composed).  It is the last pixel layer, drawn from the same frozen state
        // the rest of this frame came from.
        if (_menu is { IsOpen: true } menu && _palette is not null)
        {
            menu.Render(
                new PixelTarget(
                    buffer.Data.AsSpan(frame.Offset, buffer.FrameSize),
                    buffer.Width,
                    buffer.Height,
                    buffer.Height,
                    PixelChannelOrder.BlueHigh),
                _palette);
        }

        if (_drawFrameChart.Value)
        {
            _frameChart.Draw(frame.Canvas);   // --dofps: the frametime chart, drawn last like mode-13hx's own
        }

        // F12: the composed frame as it is about to be presented, and the scene behind it.
        // After every layer, before the hand-off.
        if (_shotRequested)
        {
            _shotRequested = false;
            SaveScreenshot(buffer, frame, worldRows, mapFullScreen);
        }

        buffer.FinishFrame(frame);
    }

    /// <summary>Adds the time since <paramref name="mark"/> to <paramref name="sum"/> and re-marks.</summary>
    private static void Phase(ref long mark, ref double sum)
    {
        long now = Stopwatch.GetTimestamp();
        sum += Stopwatch.GetElapsedTime(mark, now).TotalMilliseconds;
        mark = now;
    }

    /// <summary>
    /// The presented frame's mean wall time and where it goes, over every frame this rasterizer
    /// rendered: input + simulation step, scene assembly, the 3-D world, the map, the cockpit
    /// layer, the HUD, the four windows (the TARGET silhouette included) and the rest.
    /// </summary>
    public string FrameTimingLine => _phaseFrames == 0
        ? "FRAME phases: none"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"FRAME {_phaseTotalMilliseconds / _phaseFrames:F2} ms mean over {_phaseFrames:N0} frame(s): "
                + $"sim {_phaseSimMilliseconds / _phaseFrames:F2}, "
                + $"scene {_phaseSceneMilliseconds / _phaseFrames:F2}, "
                + $"world {_phaseWorldMilliseconds / _phaseFrames:F2}, "
                + $"map {_phaseMapMilliseconds / _phaseFrames:F2}, "
                + $"cockpit {_phaseCockpitMilliseconds / _phaseFrames:F2}, "
                + $"hud {_phaseHudMilliseconds / _phaseFrames:F2}, "
                + $"windows {_phaseWindowsMilliseconds / _phaseFrames:F2}, "
                + $"rest {_phaseRestMilliseconds / _phaseFrames:F2}");

    /// <summary>
    /// The cockpit layer's mean wall time and where it goes: the cached panel copy, the
    /// artificial-horizon ball, the other regions and the dial needles — the instrument that says
    /// which phase pays for a cockpit toggle.
    /// </summary>
    public string CockpitTimingLine => _cockpitFrames == 0
        ? "COCKPIT layer: not painted"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"COCKPIT layer {_cockpitMilliseconds / _cockpitFrames:F2} ms mean over {_cockpitFrames:N0} frame(s): "
                + $"panel copy {_cockpitPanelMilliseconds / _cockpitFrames:F2}, "
                + $"horizon ball {_cockpitHorizonMilliseconds / _cockpitFrames:F2}, "
                + $"other regions {(_cockpitRegionsMilliseconds - _cockpitHorizonMilliseconds) / _cockpitFrames:F2}, "
                + $"dials {_cockpitDialsMilliseconds / _cockpitFrames:F2}; "
                + $"this frame {_lastCockpit.Milliseconds:F2} ms");
}
