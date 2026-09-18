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
/// The port's <see cref="IRasterizer"/>: one host frame = run the simulation forward by the elapsed
/// wall time, then draw the latest state once.
/// </summary>
/// <remarks>
/// <para>
/// Runs on mode-13hx's rasterizer thread (<c>EngineWindow.RenderThreadMain</c>), which is also the
/// only thread that touches <see cref="FlightSession"/> — the window thread only writes the
/// <c>Control</c> table.  The simulation's rate is fixed and the render rate is free (step 1):
/// <see cref="StepPresenter"/> converts the frame's seconds into whole steps and carries the
/// remainder, so a 144 Hz host and a 30 Hz host run the same steps in the same order.
/// </para>
/// <para>
/// The renderer never sees <see cref="FlightSession"/>: it gets a <see cref="FlightSnapshot"/> built
/// after the last step, which is a value type of integers ("a renderer must never write back into sim
/// state").
/// </para>
/// </remarks>
// -----------------------------------------------------------------------------------------------
// THE MAP.  This class is ten files, one per concern; each of them is the same
// `partial class FlightRasterizer`.  Nothing was rewritten to split them — every member was
// MOVED verbatim, so a citation in one of these files means what it meant when the class was
// 5,991 lines in this one.
//
//   FlightRasterizer.cs          931  the state the concerns share, the constructor that reads
//                                     the options, the mission / menu / exit lifecycle, Dispose
//   FlightRasterizer.Options.cs  235  the static option-word parsers: a word -> an enum
//   FlightRasterizer.Frame.cs    944  Render — one host frame — and the per-phase timing it keeps
//   FlightRasterizer.Scene.cs    892  the camera and the view, and everything the display list is
//                                     filled with: aircraft, ground, clouds, sun, rounds, smoke
//   FlightRasterizer.Cockpit.cs  608  the cockpit panel's state: dials, radar, target panel
//   FlightRasterizer.Hud.cs      863  the HUD: text widgets, designators, markers, the gunsight
//   FlightRasterizer.Windows.cs  870  the four overlay windows, the map, and the navigation
//   FlightRasterizer.Input.cs    224  the debug / cheat keys, the switches they toggle, the sound
//   FlightRasterizer.Readout.cs  551  the developer readout rows and the screenshot / scene dump
//   FlightRasterizer.Debrief.cs  352  the debrief, the fate banner, the sortie statistics
//
// WHERE A MEMBER LIVES.  A method follows the concern it serves.  A field follows the ONE
// concern that reads it outside the constructor; a field two or more concerns read — most of
// the simulation and presentation state — stays here.  That rule keeps the renderer handles the
// frame loop drives (_cockpit, _hud, _map, _overlayWindows) in Frame.cs, beside the calls that
// use them.
// -----------------------------------------------------------------------------------------------
public sealed partial class FlightRasterizer : IRasterizer, IDisposable
{
    private FlightSession _session;

    private readonly CameraLens _lens;

    private readonly SceneRenderer _scene = new();

    private readonly SceneSnapshot? _snapshot;

    private MeshModel? _playerMesh;

    private SceneRenderOptions _sceneOptions;

    private readonly SceneColors _colors;

    private readonly InFlightStrings _strings;

    /// <summary>How many grey rounds and flashes the last frame drew.</summary>
    private int _roundInstances, _flashInstances;

    /// <summary>How many presentation puffs the last frame drew.</summary>
    private int _smokeInstances;

    private int _wreckSmokeInstances;

    private IReadOnlyDictionary<ushort, MeshModel> _classMeshes = new Dictionary<ushort, MeshModel>();

    private readonly Func<FlightSession>? _reopen;

    /// <summary>CHEAT — <c>--foe-hp</c>: the hit points every engagement is weakened to, or 0.</summary>
    private readonly int _foeHitPoints;

    /// <summary>
    /// Graphics."Clouds", <c>g_clouds_flag [0xB6]</c>.  A separate flag and not <c>_cloudTiles =
    /// 0</c>, because the tile loop runs <c>-n..n</c> and zero tiles is still ONE tile of nine
    /// cloud instances.
    /// </summary>
    private bool _cloudsOn = true;

    private readonly double _chaseDistance;

    private readonly double _chaseElevation;

    private readonly bool _groundBallsFixed;

    private int _groundBallInstances;

    private int _cloudInstances;

    private int _groundGridExponent;

    private int _combatInstances;

    private int _enemyGearDown;

    private int _worldRows;

    /// <summary>The last presented frame's size, so the readout can name the menu's scale.</summary>
    private int _frameWidth;

    private int _frameHeight;

    private long _hostFrames;

    private double _hostFps;

    private ViewMode _view;

    /// <summary>The presentation-side pose smoother (never writes sim state).</summary>
    private readonly PoseSmoother _smoother = new();

    /// <summary><c>--wreck-mesh on</c>: keep the aircraft mesh after it has come to rest.</summary>
    private readonly bool _wreckMesh;

    /// <summary>How many pool objects were drawn interpolated on the last frame.</summary>
    private int _smoothedObjects;

    /// <summary>Shadow objects whose slot names no owner this frame (never drawn).</summary>
    private readonly HashSet<ushort> _orphanShadows = [];

    /// <summary>The eighteen view keys' camera placements.</summary>
    private readonly ViewCamera _rig;

    /// <summary>
    /// The pool object the camera is INSIDE this frame, which the scene must not draw.
    /// </summary>
    /// <remarks>
    /// The per-view flag table's bit 1 is "cockpit-interior" and is set on exactly the six F1..F6
    /// views; Shift-F7 puts the eye inside the TARGET's cockpit instead, and the table gives it bit
    /// 7, "target as camera-origin".  Either way the eye is inside a mesh, and an eye inside a mesh
    /// sees one of its faces filling the frame.
    /// </remarks>
    private ushort _suppressObjectRef;

    private readonly CockpitArt? _cockpitArt;

    /// <summary>The game's palette, kept for the ESC menu bar's own colours.</summary>
    private readonly IReadOnlyList<Rgb24>? _palette;

    private readonly CYAC.Port.Core.Data.WeaponTablesDocumentDto? _weapons;

    private CockpitOptions _cockpitOptions;

    private CockpitFrameStats _lastCockpit;

    /// <summary>The TARGET window's live silhouette, built lazily on the first frame that needs one.</summary>
    private TargetSilhouetteView? _targetSilhouette;

    /// <summary>This frame's in-world designator labels; reused, never re-allocated.</summary>
    private readonly List<HudDesignator> _designators = [];

    /// <summary>The TARGET window's shipped label tables, or null when the tree was not read.</summary>
    private readonly TargetPanelLabels? _panelLabels;

    /// <summary>The YEAGER advisor's message law, or null when no tree was opened.</summary>
    private readonly AdvisorDispatch? _advisor;

    /// <summary>Which mission's effect seams the advisor's observers are currently on.</summary>
    private object? _advisorWiredTo;

    /// <summary>What the advisor window drew last frame, for the census line.</summary>
    private int _lastAdvisorLines;

    private CockpitOverlayFlags _overlays;

    /// <summary>How many contacts the MAP window plotted in the last frame.</summary>
    private int _lastMapDots;

    /// <summary>How many envelope-plot shapes the last frame painted (0 or 5).</summary>
    private int _lastEnvelopeShapes;

    /// <summary>
    /// <c>g_radar_last_sweep_time [0xBCAA/AC]</c>: the frame-time at which the CRT collapse stops.
    /// <c>radar_mode_toggle_with_sweep_reset</c> arms it to <c>now + 469</c> on the ON→OFF edge
    /// (<c>image@0x0E1F6</c>) and the sweep draws only while it is still ahead of the clock
    /// (<c>image@0x0E266..0x0E277</c>).  0 means "no sweep pending", which is where a mission starts
    /// — the shipped word is 0xFFFFFFFF there (measured), i.e. already expired.
    /// </summary>
    private uint _radarSweepDeadline;

    private readonly HudOverlayState _hudState = new();

    private bool _flightInfoVisible;

    private HudFrameStats _lastHud;

    private long _seenHudPosts;

    private string? _hudMessage;

    private uint _hudMessageExpiry;

    /// <summary>
    /// The HOST's own line on the message strip ("MISSION RESTARTED"), and how much longer it stays
    /// up, in host seconds.
    /// </summary>
    /// <remarks>
    /// It cannot use the kernel's own expiry stamp the way a warning or a radio call does
    /// (<see cref="HudMessageTable.MessageTicks"/> counted off <c>g_frame_time_accum [0xF0D2]</c>),
    /// because the event it announces is precisely the one that RESETS that clock: a stamp taken in
    /// the outgoing sortie's accumulator space means nothing in the incoming one.  So the host times
    /// its own notice in wall seconds and outranks both producers while it is up.
    /// </remarks>
    private string? _hostNotice;

    private double _hostNoticeSeconds;

    /// <summary>How long the host's own strip line stays up, in seconds.</summary>
    private const double HostNoticeSeconds = 2.5;

    /// <summary>One line per restart: what ended, and what the fresh sortie starts from.</summary>
    private readonly List<string> _restartLog = [];

    /// <summary>
    /// The in-flight ESC menu bar, or null when the data tree carries no menu document.
    /// </summary>
    private FlightMenuController? _menu;

    /// <summary>What "Exit" asks the host to do, or null in a headless run.</summary>
    private Action? _exitAction;

    /// <summary>Set by the menu's "Exit"; the headless loop stops on it.</summary>
    public bool ExitRequested { get; private set; }

    private readonly MapMode _mapMode;

    private MapViewOptions _mapOptions;

    private MapFrameStats _lastMap;

    private ViewMode _viewBeforeMap = ViewMode.CockpitForward;

    private int _mapFrames;

    private bool _shotRequested;

    private CameraPose _lastCamera;

    // The cockpit layer's own wall time, by phase, over every frame it was painted.
    private int _cockpitFrames;

    private double _mapMilliseconds;

    /// <summary>Creates the rasterizer.</summary>
    /// <param name="session">The flight to run.</param>
    /// <param name="input">Where each frame's input comes from.</param>
    /// <param name="options">The command line.</param>
    /// <param name="readout">Draw the text readout (needs mode-13hx's font assets).</param>
    /// <param name="scene">
    /// The frame's scene — the theatre's static scenery plus the room for this frame's objects.  Null
    /// leaves the H1 scaffold behaviour: horizon only.
    /// </param>
    /// <param name="palette">The game's 256-colour VGA palette, already widened to 8 bits.</param>
    /// <param name="playerMesh">The player's own aircraft mesh, drawn in the external views.</param>
    /// <param name="cloudMesh">The <c>cloud</c> mesh, drawn once per cloud-deck slot.</param>
    /// <param name="groundGridMesh">
    /// The <c>spheres</c> mesh — the ground reference grid (<see cref="GroundGrid"/>); null draws none.
    /// </param>
    /// <param name="effectProbeMesh">
    /// <c>--effect-probe</c>: one instance of this class is drawn straight ahead of the camera, for
    /// photographing an effect that a headless sortie cannot be made to spawn on demand.
    /// </param>
    /// <param name="sunMesh">
    /// The <c>sun</c> mesh — one white disc kept 100 world units above the camera
    /// (<see cref="SunDisc"/>); null draws none.
    /// </param>
    /// <param name="reopen">
    /// Builds a FRESH session — what <c>--respawn</c> and the <c>r</c> key call after the flight
    /// ends.  The original goes to the debrief screen instead (<c>flight_session_end
    /// @image@0x30328</c>); the PoC has no front end, so it restarts the sortie.
    /// </param>
    /// <param name="classMeshes">
    /// DGROUP class-record offset → mesh, for the combat pool's objects (aircraft, bullets,
    /// explosions, chaff…).  Empty draws none.
    /// </param>
    /// <param name="strings">
    /// The in-flight screen's words and formats (<c>DataTree.InFlightStrings</c>): the HUD's, the
    /// weapon readout's, the overlay windows' and the o'clock line's.
    /// </param>
    /// <param name="cloudDeck">
    /// The cloud deck's lattice (<c>DataTree.CloudDeckLattice</c>); null draws no clouds.
    /// </param>
    public FlightRasterizer(
        FlightSession session,
        IFlightInputSource input,
        FlyOptions options,
        bool readout,
        InFlightStrings strings,
        SceneSnapshot? scene = null,
        IReadOnlyList<Rgb24>? palette = null,
        MeshModel? playerMesh = null,
        MeshModel? cloudMesh = null,
        MeshModel? groundGridMesh = null,
        MeshModel? sunMesh = null,
        MeshModel? effectProbeMesh = null,
        IReadOnlyDictionary<ushort, MeshModel>? classMeshes = null,
        Func<FlightSession>? reopen = null,
        SpriteImage? explosionSprite = null,
        CockpitArt? cockpitArt = null,
        CockpitFont? cockpitFont = null,
        CYAC.Port.Core.Data.WeaponTablesDocumentDto? weapons = null,
        HudDecal? hitDecal = null,
        HudMessageTable? hudMessages = null,
        CockpitOverlayFlags overlaysFromConfig = CockpitOverlayFlags.Target | CockpitOverlayFlags.Map,
        TargetPanelLabels? panelLabels = null,
        AdvisorMessages? advisorMessages = null,
        CloudDeckLattice? cloudDeck = null)
    {
        ArgumentNullException.ThrowIfNull(strings);
        _strings = strings;
        _cloudDeck = cloudDeck;
        _panelLabels = panelLabels;

        // The YEAGER advisor.  The message table is read once out of the data tree; the kill
        // advisory's 50 % gate draws from the Fx.Indicators stream, which is the stream the
        // project's own RNG census assigns to image@0x0F03F.
        _advisor = advisorMessages is null
            ? null
            : new AdvisorDispatch(advisorMessages, NextAdvisorGateDraw);
        _reopen = reopen;
        ArgumentNullException.ThrowIfNull(options);
        _cockpitArt = cockpitArt;
        _cockpitFont = cockpitFont;
        _weapons = weapons;
        _hitDecal = hitDecal;
        _hudMessages = hudMessages;
        _hudDemo = options.HudDemo;
        _navReadout = options.NavReadout?.Trim().ToLowerInvariant() switch
        {
            "off" => NavReadoutMode.Off,
            "full" => NavReadoutMode.Full,
            _ => NavReadoutMode.Compact,
        };
        // g_flight_info_visible [0xB0], the Ctrl-F toggle: on unless --flight-info off.
        _flightInfoVisible =
            !string.Equals(options.FlightInfo?.Trim(), "off", StringComparison.OrdinalIgnoreCase);
        _cockpitOptions = new CockpitOptions(
            Enabled: cockpitArt is not null
                && !string.Equals(options.Cockpit?.Trim(), "off", StringComparison.OrdinalIgnoreCase),
            // The test is on the NON-default word now: the shipped default is `nearest`
            // (FlyOptions.CockpitFilter carries the same choice), so an
            // absent value must read as nearest, not as smooth.
            Filter: string.Equals(
                options.CockpitFilter?.Trim(), "smooth", StringComparison.OrdinalIgnoreCase)
                ? CockpitFilter.Smooth
                : CockpitFilter.Nearest,
            Fit: string.Equals(
                options.CockpitFit?.Trim(), "uniform", StringComparison.OrdinalIgnoreCase)
                ? CockpitFit.Uniform
                : CockpitFit.Stretch,
            // The refinement knobs; every default is the refined look, every alternative the
            // plainer one.
            Needles: string.Equals(options.Needles?.Trim(), "line", StringComparison.OrdinalIgnoreCase)
                ? NeedleStyle.Line
                : NeedleStyle.Tapered,
            Window: string.Equals(options.InstrumentWindow?.Trim(), "bitmap", StringComparison.OrdinalIgnoreCase)
                ? InstrumentWindow.Bitmap
                : InstrumentWindow.Analytic,
            Hud: string.Equals(options.HudStyle?.Trim(), "classic", StringComparison.OrdinalIgnoreCase)
                ? HudStyle.Classic
                : HudStyle.Refined,
            // The needle turns at the pivot PIXEL's centre (+0.5, +0.5) unless nudged.
            NeedlePivotNudgeX: ParseNudge(options.NeedleNudge).X,
            NeedlePivotNudgeY: ParseNudge(options.NeedleNudge).Y,
            // Canopy bullet holes as translucent glass damage (1,1 = the original's opaque blit).
            HitMarkerAlphaCentre: ParsePair(options.HitMarkerAlpha, "--hit-marker-alpha", 0.9, 0.1).X,
            HitMarkerAlphaEdge: ParsePair(options.HitMarkerAlpha, "--hit-marker-alpha", 0.9, 0.1).Y,
            HudHalfStroke: options.HudStroke > 0 ? options.HudStroke / 2.0 : HudRenderer.RefinedHalfStroke,

            // A PORT ADDITION — the in-world designator labels: on/off, 1:1 host pixels by
            // default, and a configurable opacity.
            DesignatorLabels: !string.Equals(
                options.DesignatorLabels?.Trim(), "off", StringComparison.OrdinalIgnoreCase),
            DesignatorLabelScale: Math.Max(1, options.DesignatorLabelScale),
            DesignatorOpacity: Math.Clamp(options.DesignatorOpacity, 0.0, 1.0),

            // The overlay windows' own pixel scale (0 = auto).
            OverlayWindowScale: Math.Clamp(options.WindowScale, 0, 8));
        // The OVERLAY WINDOWS.  The default is the shipped yeager.cfg's own byte, which
        //   the caller resolves out of the data tree (GameConfig.Overlays = Target | Map); the
        //   `--windows` word overrides it for a photograph or a test.
        // The four --window-* words (the ESC menu rows' twins) are the start state;
        //   an explicit --windows word overrides all four (probes and tests); `cfg` is the tree's byte.
        _overlays = string.IsNullOrWhiteSpace(options.Windows)
            || string.Equals(options.Windows.Trim(), "cfg", StringComparison.OrdinalIgnoreCase)
            ? Settings.PortSettings.WindowsFromWords(options)
            : ParseWindows(options.Windows, overlaysFromConfig);

        // --cockpit-view screen|viewport (the settings store pushes it again on attach).
        CockpitViewFullScreen = string.Equals(
            options.CockpitView?.Trim(), "screen", StringComparison.OrdinalIgnoreCase);
        _smoothObjects = !string.Equals(options.SmoothObjects?.Trim(), "off", StringComparison.OrdinalIgnoreCase);
        _wreckMesh = string.Equals(options.WreckMesh?.Trim(), "on", StringComparison.OrdinalIgnoreCase);

        // The map's knobs.  Every default is the ORIGINAL's behaviour except the FIT zoom and the
        // port-added furniture, which are labelled deviations in the project's own notes.
        _mapMode = options.Map?.Trim().ToLowerInvariant() switch
        {
            "off" => MapMode.Off,
            "inset" => MapMode.Inset,
            _ => MapMode.Full,
        };
        _mapHud = !string.Equals(options.MapHud?.Trim(), "off", StringComparison.OrdinalIgnoreCase);
        _mapOptions = new MapViewOptions(
            Mode: _mapMode,
            Scenery: string.Equals(options.MapScenery?.Trim(), "courses", StringComparison.OrdinalIgnoreCase)
                ? MapScenery.Courses
                : MapScenery.All,
            // A word (or nonsense) means the port's FIT; a number is clamped to the original's own
            // 7..12 range, the range its two key handlers clamp to (image@0x0D8B5 / image@0x0D8C4).
            ZoomLevel: int.TryParse(options.MapZoom?.Trim(), out int level) && level > 0
                ? Math.Clamp(level, MapProjection.MinZoomLevel, MapProjection.MaxZoomLevel)
                : 0,
            CentreOnPlayer: false,
            Units: string.Equals(options.MapUnits?.Trim(), "ft", StringComparison.OrdinalIgnoreCase)
                ? MapRangeUnits.Feet
                : MapRangeUnits.NauticalMiles,
            RangeRings: !string.Equals(options.MapRings?.Trim(), "off", StringComparison.OrdinalIgnoreCase),
            NorthArrow: true,
            ScaleBar: true,
            Labels: !string.Equals(options.MapLabels?.Trim(), "off", StringComparison.OrdinalIgnoreCase),
            Blink: !string.Equals(options.MapBlink?.Trim(), "off", StringComparison.OrdinalIgnoreCase));
        _session = session;

        // The smoke layer bears a wreck's puffs where the wreck is DRAWN (PoseSmoother), not
        // where the engagement queue holds it between its coarse updates.
        if (session.Smoke is { } smokeLayer)
        {
            smokeLayer.PresentedPosition = PresentedFeet;
        }
        _input = input;
        _lens = new CameraLens(options.Fov);
        _readout = readout;
        _snapshot = scene;
        _playerMesh = playerMesh;
        _chaseDistance = options.ChaseDistance;
        _chaseElevation = options.ChaseElevation;
        _view = ViewNames.Parse(options.View);
        _rig = new ViewCamera
        {
            DistanceWorldUnits = options.ChaseDistance,
            ElevationDegrees = options.ChaseElevation,
        };
        _cloudMesh = cloudMesh;
        _groundGridMesh = groundGridMesh;
        _sunMesh = sunMesh;
        _effectProbeMesh = effectProbeMesh;
        _effectProbeRange = options.EffectProbeRange;
        _effectProbeCount = Math.Clamp(options.EffectProbeCount, 1, 400);
        _effectProbeAge = options.EffectAge;
        _effectProbeFork = (byte)Math.Clamp(options.EffectFork, 0, 255);
        _classMeshes = classMeshes ?? new Dictionary<ushort, MeshModel>();

        // per-round gunnery: the round mesh is authored here (the data carries none) and the flash
        // is the deferred-effect pool's own class.  The trail now tests the VERIFIED KERNEL's own
        // class-record hit box (+0x30..+0x47) out of the combat static surface, so the rasterizer
        // supplies no hit volume at all and `GunneryRounds.HitRadius` is gone. the three meshes are
        // resolved UNCONDITIONALLY now.  They used to be built only when the session already carried
        // a trail or a layer, which was fine while `--rounds` and `--smoke-trail` could only change
        // between sorties; now that the dialog can switch either on mid-flight (SmokeTrail.Enabled /
        // GunneryRounds.Enabled are settable), a null mesh would have made the switched-on effect
        // draw nothing at all.  A mesh costs a dictionary lookup and one authored streak; nothing is
        // drawn until there is something to draw.
        _roundLengthFeet = Math.Max(1.0, options.RoundLength);
        _roundColorIndex = options.RoundColor;
        _roundMesh = GunneryRounds.RoundMesh(_roundLengthFeet, _roundColorIndex);
        _classMeshes.TryGetValue(CombatSubsystemInit.DeferredEffectClassRecord, out _flashMesh);

        // The presentation smoke layer draws its puffs as the `smoke` class's own mesh (the
        // three disc records the prepare-callback law rewrites), so it needs no authored
        // geometry of its own — only the class the pool already instantiates.
        _classMeshes.TryGetValue(
            Core.Sim.Combat.Effects.SmokePuffTable.SmokeClassRecord, out _smokeMesh);

        // What a LAZILY-created layer or trail inherits from the command line.  The two knobs below
        // have no dialog row (they are not among the ten the dialog makes live), so they would be
        // lost if the object had to be built after start-up because its own row was `off` when the
        // session was opened.
        _smokeStretchRamp = !string.Equals(
            options.SmokeRamp?.Trim() ?? "original", "original", StringComparison.OrdinalIgnoreCase);
        _roundGroundHits = !string.Equals(
            options.RoundGroundHits?.Trim() ?? "on", "off", StringComparison.OrdinalIgnoreCase);

        _hidePlayerMesh = options.NoPlayerMesh;
        _autoRespawn = options.Respawn;
        _foeHitPoints = Math.Clamp(options.FoeHitPoints, 0, 255);
        _session.CheatEnemyHitPoints = _foeHitPoints;
        _cloudTiles = Math.Clamp(options.CloudTiles, 0, 8);
        _gearAngleOverride = options.GearAngle;
        _groundBallsFixed = !string.Equals(
            options.GroundBalls?.Trim() ?? "fixed", "classic", StringComparison.OrdinalIgnoreCase);
        _groundBallTiles = Math.Clamp(options.GroundBallTiles, 0, 8);
        _cameraOverride = ParseCamera(options.Camera);
        CameraPose? step = ParseCamera(options.CameraStep is null ? null : $"{options.CameraStep}");
        _cameraStep = step is { } s ? new Vec3(s.EyeX, s.EyeY, s.EyeZ) : default;
        Census = options.NearCensus > 0 ? new SceneCensus(options.NearCensus) : null;
        Instruments = options.InstrumentCensus ? new InstrumentCensus() : null;
        ProjectionCensus = options.ProjectionCensus
            ? new ProjectionWatch { FirstFrame = Math.Max(0, options.ProjectionFrom) }
            : null;
        // Super-sampling is retired: --ssaa fails to parse, the way --raster and --ground already
        // do, so a script that still asks for it says so loudly instead of getting something else.
        _noEjectionParts = options.NoEjectionParts;
        _drawFrameChart = ControlParamRegistry.Get("DrawFrameChart", options.Dofps);
        _sceneOptions = new SceneRenderOptions(
            DrawScenery: !options.NoScenery,
            DrawObjects: true,
            LodHysteresis: SceneRenderOptions.Default.LodHysteresis,
            MaxDrawDistanceWorldUnits: options.DrawDistance,
            ClassicCull: options.ClassicCull,
            Alpha: ParseAlpha(options.Alpha),
            Edges: ParseEdges(options.Edges),
            Articulation: !options.NoGearAnimation,
            DrawPaintTreeOrphans: options.TreeOrphans,
            // The smoke class's prepare-callback law (puffs grow 25 → 200 world units over
            // their life); off = the static 16–20-unit records the PoC drew.
            SmokeGrowth: !string.Equals(
                options.SmokeGrowth?.Trim() ?? "on", "off", StringComparison.OrdinalIgnoreCase),
            SmokeSizeScale: Math.Max(0.0, options.SmokeSize),
            SmokeDensity: Math.Max(0.0, options.SmokeDensity),
            Horizon: ParseHorizon(options.Horizon),
            HorizonBandDegrees: options.HorizonBand,
            Lod: ParseLod(options.Lod),
            NearInstanceExemptionWorldUnits: Math.Max(0.0, options.NearExempt),
            SoftEffects: !options.HardEffects,
            EffectDebris: !options.NoEffectDebris,
            BitmapExplosions: !string.Equals(
                options.BitmapExplosions?.Trim() ?? "on", "off", StringComparison.OrdinalIgnoreCase),
            // The player's INTERIOR MASK (InteriorMask; --seam-mask).
            SeamMask: !string.Equals(
                options.SeamMask?.Trim() ?? "on", "off", StringComparison.OrdinalIgnoreCase),
            // The RENDER SCRUTINY switches (the K / L / I keys' command-line twins).
            BackfaceCull: !string.Equals(
                options.BackfaceCull?.Trim() ?? "on", "off", StringComparison.OrdinalIgnoreCase),
            Wireframe: ParseWireframe(options.Wireframe),
            FaceColors: string.Equals(
                options.FaceColors?.Trim(), "contrast", StringComparison.OrdinalIgnoreCase)
                ? FaceColorMode.Contrast
                : FaceColorMode.Paint,
            FaceColorSeed: options.FaceColorSeed,
            WireColorIndex: Math.Clamp(options.WireColor, -1, 255),
            WireWidthPixels: Math.Max(0.0, options.WireWidth),
            MaskView: ParseMaskView(options.MaskView),
            // TILES AND THREADS.  Neither can reach the picture (the frame is bit-identical at
            // every tile size and thread count, TileInvarianceTests); the library defaults to one
            // thread and the HOST ships the whole machine.
            TileSize: Math.Clamp(options.Tile, 4, 8192),
            Threads: options.Threads > 0
                ? Math.Min(options.Threads, 256)
                : Environment.ProcessorCount,
            // The line width model.
            LineWidths: LineWidthModel.Default.WithOverrides(options.LineWidth) with
            {
                FloorHostPixels = Math.Max(0.0, options.LineFloor),
                TracerFloorHostPixels = Math.Max(0.0, options.TracerFloor),
                RoundFeet = Math.Max(0.0, options.RoundWidth),
                RoundFloorHostPixels = Math.Max(0.0, options.RoundFloor),
                // The sub-floor thread fade (a rope reads as a thread, not a sausage).
                ThreadFade = !string.Equals(
                    options.LineThread?.Trim() ?? "on", "off", StringComparison.OrdinalIgnoreCase),
            },
            // The tracer's halo.
            Tracer: new TracerHalo(
                CYAC.Port.Render.TracerHalo.ParseMode(options.TracerHalo),
                Math.Max(0.0, options.TracerHaloRadius),
                Math.Clamp(options.TracerHaloAlpha, 0.0, 1.0),
                Math.Clamp(options.TracerGlow, 0.0, 1.0),
                Math.Max(0.0, options.TracerHaloFloor),
                Math.Clamp(options.TracerColor, -1, 255)));

        // Where F12 writes, and what the mesh library was conditioned with (the scene dump
        // records it so --render-scene rebuilds the same meshes).
        _shotDirectory = string.IsNullOrWhiteSpace(options.ShotDirectory) ? "screenshots" : options.ShotDirectory.Trim();
        _conditioning = new SceneDumpConditioning
        {
            Markings = options.Markings?.Trim().ToLowerInvariant() ?? "vector",
            MarkingsDir = options.MarkingsDir,
            DecalLift = Math.Max(0.0, options.DecalLift),
            SheetInflate = !options.NoSheetInflate,
            SheetThickness = Math.Max(0.0, options.SheetThickness),
            Weld = Math.Max(0.0, options.Weld),
        };

        // The bitmap-explosion sprite the original loads once per session
        // (gx_subsystem_init_10x0E @image@0x03A04 -> "exp.rle").  A null sprite is the shipped
        // fallback, not an error: deferred_effect_render tests [0xB4A0] for exactly that.
        _scene.ExplosionSprite = explosionSprite;

        _palette = palette;
        if (palette is not null)
        {
            _scene.SetPalette(palette);
            _map.SetPalette(palette);       // The map's colours are palette indices too
            _colors = SceneColors.Default with { HorizonRamp = SceneColors.RampFrom(palette) };
        }
        else
        {
            _colors = SceneColors.Default;
        }
    }

    /// <summary>
    /// The near-camera diagnostic census (<c>--near-census</c>), or null when it is off.
    /// </summary>
    public SceneCensus? Census { get; }

    /// <summary>H8 addendum — the degenerate-projection watch, when <c>--projection-census</c> is on.</summary>
    public ProjectionWatch? ProjectionCensus { get; }

    /// <summary>
    /// The instrument census, when <c>--instrument-census</c> asked for one.
    /// </summary>
    public InstrumentCensus? Instruments { get; }

    /// <summary>H8 addendum — the mesh classes whose vertices had to be clamped at load.</summary>
    public IReadOnlyList<(string Class, int Lod, int Components)> MeshClampCensus { get; init; } = [];

    /// <summary>H8 addendum — the LODs whose executable inline vertex block disagrees with the .PNT one.</summary>
    public IReadOnlyList<(string Class, int Lod, int Rows)> MeshVertexDisagreements { get; init; } = [];

    /// <summary>How many times the sortie restarted — the flight ended, or the menu's own row.</summary>
    public int Respawns { get; private set; }

    /// <summary>
    /// Releases what the rasterizer owns that holds threads: the scene renderer's TILE POOL.
    /// </summary>
    /// <remarks>
    /// The host runs one rasterizer per process and its helper threads are background threads, so
    /// nothing leaked; this is what makes <see cref="SceneRenderer.Dispose"/> real rather than
    /// decorative — <c>Program</c>'s <c>using</c> joins the pool before the
    /// process exits instead of tearing it down under the runtime.  The sound path has its own
    /// <c>using</c> and the session owns no threads.
    /// </remarks>
    public void Dispose()
    {
        _scene.Dispose();
        _targetSilhouette?.Dispose();      // The panel's own renderer
    }

    /// <summary>What the message strip says for a couple of seconds after a mid-flight restart.</summary>
    public const string RestartNoticeText = "MISSION RESTARTED";

    /// <summary>
    /// One line per restart, in order: what the outgoing sortie had reached and what the fresh one
    /// starts from.  The headless summary prints it; it is the proof that a restart is a COLD
    /// start of the same mission.
    /// </summary>
    public IReadOnlyList<string> RestartLog => _restartLog;

    /// <summary>Whether the flight has ended (<c>master[+0x122] == 0</c>).</summary>
    public bool FlightEnded => _session.State.Aircraft.ActiveState == 0;

    /// <summary>The session the host is flying (it changes on a respawn).</summary>
    public FlightSession Session => _session;

    /// <summary>How many host frames have been drawn.</summary>
    public long HostFrames => _hostFrames;

    // ---------------------------------------------------------------------------------------
    // The in-flight ESC menu's surface.  Every one of these is PRESENTATION or session
    //   level; not one of them writes into the integer kernel's state.
    // ---------------------------------------------------------------------------------------

    /// <summary>The menu bar, once <see cref="AttachMenu"/> has given it one.</summary>
    public FlightMenuController? Menu => _menu;

    /// <summary>True while the bar is up, which is what freezes the simulation.</summary>
    public bool MenuOpen => _menu is { IsOpen: true };

    /// <summary>Gives the rasterizer its menu and its exit action.</summary>
    /// <param name="menu">The controller.</param>
    /// <param name="exitAction">What the menu's "Exit" runs, or null (headless).</param>
    public void AttachMenu(FlightMenuController menu, Action? exitAction = null)
    {
        _menu = menu;
        _exitAction = exitAction;
    }

    /// <summary>Sets what the menu's "Exit" runs (the window path knows only after the window exists).</summary>
    /// <param name="exitAction">The action.</param>
    public void SetExitAction(Action exitAction) => _exitAction = exitAction;

    /// <summary>
    /// The ONE restart path: the menu's "Restart Mission" ends up here.
    /// </summary>
    public void RestartMission()
    {
        if (_reopen is not null)
        {
            Reopen(midFlight: true);
        }
    }

    /// <summary>
    /// The <c>?</c> menu's "End Mission": ends the sortie the way Ctrl-Q does, through the mission
    /// outcome, so the debrief overlay appears over the frozen scene.
    /// </summary>
    /// <remarks>
    /// The original synthesises Ctrl-Q (<c>image@0x21676</c>), which reaches
    /// <c>flight_session_end @image@0x30328</c> → the debrief.  A Test Flight has no outcome object
    /// and therefore no debrief; there the item does nothing, which is honest.
    /// </remarks>
    public void EndMission()
    {
        if (_session.Outcome is { } outcome)
        {
            outcome.ForceEnd(_session, Core.Sim.Mission.MissionDebriefOutcome.Survived);
            return;
        }

        // A TEST FLIGHT has no outcome and therefore no debrief, which is what the original does too
        // (its Test Flight ends with no debrief screen at all).  In MENU mode it goes straight back
        // to CHOOSE ACTIVITY, which is the honest thing once there IS a menu to go back to.  In
        // direct mode it still does nothing: direct mode does not change.
        ReturnToMenuRequested = MenuMode;
    }

    /// <summary>
    /// The sortie is over and the player has asked for the MENU: the shell tears the flight down and
    /// CHOOSE ACTIVITY comes back.
    /// </summary>
    /// <remarks>
    /// It is raised in exactly two places, both of them the one door out of a sortie:
    /// <b>Enter or Esc on the debrief overlay</b>, and <b>End Mission on a Test Flight</b>, which
    /// has no debrief to land on (the original's Test Flight shows none). The menu's Restart Mission
    /// still restarts IN PLACE and never raises it.  Nothing else in the rasterizer reads it; the
    /// shell polls it after <see cref="Render"/> and clears it by disposing the rasterizer.
    /// </remarks>
    public bool ReturnToMenuRequested { get; private set; }

    /// <summary>
    /// True when this sortie is being flown UNDER THE SHELL (menu mode), false in DIRECT mode
    /// (<c>fly --mission N</c> / <c>--test-flight X</c> / <c>--seed-trace</c>).
    /// </summary>
    /// <remarks>
    /// The rule: <b>direct mode's behaviour does not change at all</b> — no shell, no
    /// menu, the debrief overlay (ESC for the menu), and the sortie's end does not go anywhere.
    /// That keeps the headless runs and the recorded replays
    /// habit exactly what they were.  So every F1 addition to this class is behind this flag,
    /// which only <see cref="FrontEnd.FlightFactory"/> ever sets.
    /// </remarks>
    public bool MenuMode { get; set; }

    /// <summary>The <c>?</c> menu's "Exit": close the window, or stop the headless loop.</summary>
    public void RequestExit()
    {
        ExitRequested = true;
        _exitAction?.Invoke();
    }

    /// <summary>Puts one line on the host's own message strip (M0's <c>_hostNotice</c>).</summary>
    /// <param name="text">The line.</param>
    public void PostNotice(string text)
    {
        _hostNotice = text;
        _hostNoticeSeconds = HostNoticeSeconds;
    }

    /// <summary>The port settings store, once <see cref="AttachSettings"/> has given it one.</summary>
    public PortSettingsStore? SettingsStore { get; private set; }

    /// <summary>Gives the rasterizer the settings store its own key toggles persist through.</summary>
    /// <param name="store">The store.</param>
    public void AttachSettings(PortSettingsStore store) => SettingsStore = store;

    /// <summary>
    /// The frame's radar state, kept so the MAP window plots the SAME contact list the scopes do
    /// without walking the arena twice (the original's engine builds one slot table for all three
    /// contexts).
    /// </summary>
    private RadarState _lastRadar = RadarState.None;

    /// <summary>
    /// Gives a session's gunnery trail the hit spheres: each class record's mesh bounding radius in
    /// world units (the densest LOD's, scaled by the class's own exponent).
    /// </summary>
    /// <param name="session">The session whose trail is wired.</param>
    /// <summary>
    /// Opens a FRESH session of the same mission and site, and resets everything the host itself
    /// carries across it.
    /// </summary>
    /// <param name="midFlight">
    /// True when the sortie was still running (the ESC menu's Restart Mission the accelerator is
    /// unmapped), false for the post-sortie "fly again" — it changes only what the log line says and
    /// whether the strip announces it.
    /// </param>
    /// <remarks>
    /// <para>
    /// This is the ONE restart path: <c>--respawn</c>, the script words, the ESC menu's own row
    /// mid-flight and (next) the ESC menu's "Restart Mission" all arrive here.  The session itself
    /// is rebuilt by <c>Program.OpenSession</c>, so everything the SIMULATION owns — the flight
    /// kernel, the combat kernel, the mission module and its counters, the NAV slot, the fate
    /// machine, the wreck, the debrief verdict — is new by construction.  What needs resetting is
    /// only what the HOST keeps outside the session.
    /// </para>
    /// <para>
    /// Deliberately NOT reset: the camera (<see cref="_view"/> and <see cref="_viewBeforeMap"/>) and
    /// the map's zoom/centre.  A view is the player's standing choice, not sortie state, and the
    /// restart re-flies the SAME mission over the SAME site, so a framing that was useful a second
    /// ago still is.  <see cref="EffectiveView"/> recovers on its own: the death camera is derived
    /// from the fate machine every frame, and the fresh machine is idle.
    /// </para>
    /// </remarks>
    private void Reopen(bool midFlight)
    {
        FlightSession before = _session;
        FlightSnapshot wasAt = before.Snapshot();
        int wasKills = before.Mission?.Hits.Kills ?? 0;
        long wasRounds = before.Mission?.Hits.RoundsFired ?? 0;

        // The outgoing sortie ENDS here.  A restart is an ABANDONMENT unless an outcome already
        // stands (the bare `r` after a debrief): Abandon counts whichever it was, and does
        // nothing at all when this frame's Observe already counted it.
        Stats?.Abandon(before, midFlight ? "restart mid-flight" : "restarted after the debrief");

        _session = _reopen!();
        Respawns++;

        // …and the fresh sortie is a new PLAY of the same mission.
        Stats?.Begin(_session);

        // The cheats the constructor put on the FIRST session are the run's, not the
        // sortie's: without this the first restart quietly turned --foe-hp off.
        _session.CheatEnemyHitPoints = _foeHitPoints;


        // A reopened session is a new SCENE: the sound path is reset exactly where the original
        // resets it (mission_state_machine @image@0x008E6 calls audio_continuous_state_reset
        // @image@0x298BD at image@0x00B05) and re-wired to the new mission's effect seams.
        // Without the reset the last sortie's mechanism deadline (g_audio_time_threshold
        // [0xBB3C]) outlives the frame-time clock it was measured against and the gear sound runs
        // for the whole of the next sortie; without the re-wire every combat sound after the
        // first respawn is silent.
        Audio?.NoteSessionReopen(_session);

        // The CANOPY and the gunsight's attitude history.  Both are host-side presentation state
        // and neither was being reset, so before M0 a respawned sortie began with the
        // previous one's bullet holes and with a history ring stamped in the OLD accumulator space
        // — which the new sortie's accumulator, restarting at 0, reads as the future.
        _hudState.Reset();

        // The MESSAGE STRIP.  _seenHudPosts is a running total of the SESSION's posts, so a fresh
        // session (whose total restarts at 0) always differs from the remembered one and would
        // re-post the dead sortie's last warning; the latched text and its accumulator expiry
        // belong to the old clock too.
        _seenHudPosts = 0;
        _hudMessage = null;
        _hudMessageExpiry = 0;

        // The pose smoother's tracks are keyed by POOL OBJECT REF, and a fresh mission fills the
        // same slots with different aeroplanes: a surviving track would interpolate the new object
        // out of the old one's pose for a frame or two (the 20,000-unit jump guard does not catch a
        // re-spawn a few hundred feet away).  PoseSmoother has a real Reset now — M0 §4.6's
        // Render-side ask.  Same effect, and it no longer reads like a frame the host draws and
        // throws away.
        _smoother.Reset();
        _smoothedObjects = 0;
        _orphanShadows.Clear();

        // cockpit_advisor_state_reset @image@0x0EA41: the bitmask, the two timestamps and
        // the 21 per-event stamps are zeroed when a sortie's assets load, so a restart
        // gets its one-shot advisories back.  The observers are re-attached on the next
        // frame (EnsureAdvisorWired compares the mission reference).
        _advisor?.Reset();
        _advisorWiredTo = null;
        _lastAdvisorLines = 0;
        _suppressObjectRef = 0;

        if (midFlight)
        {
            _hostNotice = RestartNoticeText;
            _hostNoticeSeconds = HostNoticeSeconds;
        }

        FlightSnapshot at = _session.Snapshot();
        _restartLog.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"restart #{Respawns} ({(midFlight ? "mid-flight" : "sortie over")}) "
                + $"at host frame {_hostFrames:N0}: was sim {before.SimulatedSeconds:F1} s "
                + $"step {before.StepsRun:N0} at ({wasAt.X:N0},{wasAt.Y:N0},{wasAt.Z:N0}) "
                + $"hdg {wasAt.HeadingDegrees:F1} kills {wasKills} fired {wasRounds:N0}"
                + $" → sim {_session.SimulatedSeconds:F1} s step {_session.StepsRun:N0} at "
                + $"({at.X:N0},{at.Y:N0},{at.Z:N0}) hdg {at.HeadingDegrees:F1} "
                + $"kills {_session.Mission?.Hits.Kills ?? 0} fired {_session.Mission?.Hits.RoundsFired ?? 0:N0}"));
    }
}
