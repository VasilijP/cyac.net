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
/// The developer READOUT — the telemetry rows printed over a frame and returned to the
/// headless driver — and the SCREENSHOT / scene-dump capture that writes a frame out.
/// </summary>
/// <remarks>The reader's map of every one of this class's files is at the top of
/// <c>FlightRasterizer.cs</c>.</remarks>
public sealed partial class FlightRasterizer
{
    // The RENDER SCRUTINY instruments: F12's screenshot + scene dump, and the state the K / L
    // / I keys flip lives in _sceneOptions.
    private readonly string _shotDirectory;

    private readonly SceneDumpConditioning _conditioning;

    private int _shotSerial;

    /// <summary>How many screenshots this rasterizer has written.</summary>
    public int ScreenshotsTaken { get; private set; }

    /// <summary>The last screenshot's PNG path, or null.</summary>
    public string? LastScreenshotPath { get; private set; }

    /// <summary>The last scene dump's path, or null.</summary>
    public string? LastSceneDumpPath { get; private set; }

    /// <summary>The current scrutiny state, one readout token per switch.</summary>
    public string DebugLookLine() => string.Create(
        CultureInfo.InvariantCulture,
        $"cull {(_sceneOptions.BackfaceCull ? "on" : "OFF")}  "
            + $"wire {_sceneOptions.Wireframe.ToString().ToLowerInvariant()}  "
            + $"faces {(_sceneOptions.FaceColors == FaceColorMode.Contrast ? $"contrast#{_sceneOptions.FaceColorSeed}" : "paint")}  "
            + $"mask {_sceneOptions.MaskView.ToString().ToLowerInvariant()}");

    /// <summary>
    /// Writes the composed frame as <c>shot_&lt;UTC&gt;_&lt;n&gt;.png</c> and the scene that made it
    /// as <c>shot_&lt;UTC&gt;_&lt;n&gt;.scene.json</c> in <c>--shot-dir</c>.
    /// </summary>
    /// <param name="buffer">The frame buffer.</param>
    /// <param name="frame">This frame's descriptor.</param>
    /// <param name="worldRows">How many rows the world was drawn into.</param>
    /// <param name="mapFullScreen">Whether the F9 map replaced the world (no scene to dump).</param>
    private void SaveScreenshot(FrameBuffer buffer, FrameDescriptor frame, int worldRows, bool mapFullScreen)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string basename = string.Create(CultureInfo.InvariantCulture, $"shot_{stamp}_{++_shotSerial:D3}");
        string png = Path.Combine(_shotDirectory, basename + ".png");
        try
        {
            FrameImage.SavePng(
                png,
                buffer.Data.AsSpan(frame.Offset, buffer.FrameSize),
                buffer.Width,
                buffer.Height,
                buffer.Height,
                blueHigh: true);
            LastScreenshotPath = png;
            ScreenshotsTaken++;

            string? dump = null;
            if (_snapshot is not null && !mapFullScreen)
            {
                dump = Path.Combine(_shotDirectory, basename + ".scene.json");
                SceneDumpDocument document = SceneDumpDocument.Capture(
                    _snapshot, in _lastCamera, _lens, _colors, _palette, _sceneOptions,
                    buffer.Width, buffer.Height, worldRows, _conditioning);
                document.Mission = CurrentMissionKey ?? _session.AircraftBasename;
                document.Aircraft = _session.AircraftBasename;
                document.View = ViewNames.NameOf(_view);
                document.SimSeconds = _session.SimulatedSeconds;
                document.Write(dump);
                LastSceneDumpPath = dump;
            }

            PostNotice(dump is null ? $"Saved {png}" : $"Saved {png} + scene dump");
            Console.WriteLine($"screenshot: {png}{(dump is null ? string.Empty : $"  scene: {dump}")}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            PostNotice($"Screenshot FAILED: {error.Message}");
            Console.Error.WriteLine($"screenshot: {error.Message}");
        }
    }

    /// <summary>The readout lines, in order — also what the headless run prints.</summary>
    /// <param name="view">The frame's snapshot.</param>
    public IReadOnlyList<string> ReadoutLines(in FlightSnapshot view) =>
        _session.Mission is null
            ? [SettingsLine(), StatsLine(), .. FlightReadout(view), FateLine(), FrameTimingLine, CockpitTimingLine]
            : [SettingsLine(), StatsLine(), .. FlightReadout(view), CombatReadout(), HitReadout(), GunneryLine(), SmokeLine(), SlotCensusLine(), FateLine(), MissionLine(), FrameTimingLine, CockpitTimingLine];

    /// <summary>
    /// The readout's FIRST line: which settings file this run resolved, and where its values came
    /// from.
    /// </summary>
    public string SettingsLine() => SettingsStore?.ReadoutLine() ?? "settings -";

    /// <summary>
    /// The readout's SECOND line: which statistics file this run resolved and what it holds,
    /// printed beside the settings path so both port-owned files are named on screen.
    /// </summary>
    public string StatsLine() => Stats?.Store.ReadoutLine() ?? "stats  -";

    /// <summary>
    /// The MISSION line: the module the port is running, its counters, whether the objective is met
    /// and what the debrief would say.
    /// </summary>
    public string MissionLine()
    {
        if (_session.Mission is not { } mission)
        {
            return string.Empty;
        }

        if (mission.Module is not { } module)
        {
            string name = mission.Combat.Mission.AssetName;
            return $"MISSION {name} — NO WIN-RULE MODEL; the sortie cannot be won.  "
                + (mission.UnmodelledModule?.MissingVocabulary ?? "(not one of the unmodelled five)");
        }

        string counters = module.Counters.Count == 0
            ? "--"
            : string.Join(
                " ",
                module.Counters.Select((value, i) =>
                    $"+0x{module.ByteVariableOffsets[i]:X}={value}"));
        string debrief = _session.Outcome?.Debrief is { } done
            ? $"  DEBRIEF mode {done.PostMissionMode} {(done.Accomplished ? "ACCOMPLISHED" : done.Outcome.ToString().ToUpperInvariant())}"
            : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"MISSION {module.AssetName} {module.Rules.WinConditionKind}  ctr {counters}  "
                + $"hook {module.CheckWinCalls}  "
                + $"{(mission.MissionWon ? $"★ OBJECTIVE MET at {mission.MissionWonAtSeconds:F0} s" : "objective open")}"
                + $"  radio {mission.RadioMessagesPosted}"
                + $"  advisory {mission.Combat.LifecycleEvents.AdvisorActions}{debrief}");
    }

    /// <summary>
    /// The PLAYER-FATE line: what ended the sortie, where the machine is, and the SLIDE census
    /// (the acceptance number — X/Z drift after the wreck came to rest, which must be 0).
    /// </summary>
    public string FateLine()
    {
        if (_session.Fate is not { Enabled: true } fate)
        {
            return "FATE not built";
        }

        string eject = _session.LastEject is { } shot
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"  eject {shot.AirspeedFps} ft/s{(shot.AboveSafeSpeed ? " ABOVE 733" : string.Empty)}"
                    + $" roll {shot.CoinFlip}{(shot.Fatal ? " FATAL" : string.Empty)}"
                    + $" slot {shot.SlotAddress:X4} pilot {shot.PilotObjectRef:X4}"
                    // The slot's +0x02 kind byte ([0xEF2E] & 0x40 = the aircraft has an
                    // EJECTION SEAT): non-zero lights the SEAT object (image@0x2C69E).
                    + $" kind {_session.Mission?.Combat.Registers.Byte(shot.SlotAddress + ObjectSlotPool.KindFlag) ?? 0:X2}")
            : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"FATE{(fate.Passive ? " PASSIVE (--fate off: the kernel keeps stepping)" : string.Empty)} {fate.Phase} {fate.Cause}{(fate.CrashReason == PlayerCrashReason.None ? string.Empty : $"/{fate.CrashReason}")}"
                + $"  step {(fate.DestroyedStep < 0 ? "-" : fate.DestroyedStep.ToString("N0", CultureInfo.InvariantCulture))}"
                + $"  {fate.SecondsSinceDestroyed,5:F1} s  rest {fate.SecondsSinceRest,5:F1} s"
                + $"  drift X {fate.RestDriftX:F3} Z {fate.RestDriftZ:F3}"
                + $"  ram {_session.ProximityKillHits:N0}/{_session.ProximityKillQueries:N0}{eject}"
                + $"{(_session.CheatDeathAtSeconds >= 0 ? $"  ★ CHEAT --player-death-at {_session.CheatDeathAtSeconds:F1} s" : string.Empty)}");
    }

    /// <summary>The combat line — what the integer kernel is doing this frame.</summary>
    public string CombatReadout()
    {
        if (_session.Mission is not { } mission)
        {
            return string.Empty;
        }

        CombatKernelCensus census = mission.Context.Census;
        CombatRegisters registers = mission.Combat.Registers;
        ushort locked = registers.Word(0x00BC);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"WPN {registers.Word(0xED2A)}/{registers.Word(0xED22)} rnds {mission.RoundsRemaining,5}  "
                + $"shots {registers.SpawnActiveCount,3}  hull {registers.Byte(0xF1D8),3}/"
                + $"{registers.Byte(0xF1D9),3}  kills {registers.Word(0xF106)}  "
                + $"lock {(locked == 0 ? "----" : $"{locked:X4}")}  "
                + $"engaged {census.EngagementFrames:N0}  objs {_combatInstances}"
                + $" (gear dn {_enemyGearDown}, smoothed {_smoothedObjects}, coarse updates {_smoother.UpdatesCoarse:N0})  "
                + $"list {mission.RenderedObjects}  lockcalls {census.LockOnCalls:N0}");
    }

    /// <summary>
    /// The HIT CENSUS line — rounds fired, rounds on target, kills, the enemies' hull states and
    /// what the effect subsystems are doing about them.
    /// </summary>
    public string HitReadout()
    {
        if (_session.Mission is not { } mission)
        {
            return string.Empty;
        }

        HitCensus hits = mission.Hits;
        EffectCensus effects = mission.VmEffects.Census;
        string hulls = hits.EnemyHull.Count == 0
            ? "--"
            : string.Join("/", hits.EnemyHull);
        string pursuit = _session.Pursuit is { } pilot
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"  |  pursuit track {pilot.TrackingFrames:N0} fire {pilot.TriggerFrames:N0} rng {pilot.Range}")
            : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"HITS fired {hits.RoundsFired,5}  onTgt {hits.RoundsOnTarget,4}  kills {hits.Kills}  "
                + $"score [BD08] {hits.ScoreShots} [BD14] {hits.ScoreHits}  hurt {hits.DamageApplications,3}  "
                + $"foe {hulls}  |  smoke rows {effects.DamageSmokeRows + effects.ScriptEffectRows}"
                + $" puffs {effects.PuffsEmitted}/{effects.LivePuffs} live  "
                + $"flash {effects.DeferredEffectsFired}/{effects.DeferredEffectsScheduled}{pursuit}");
    }

    /// <summary>
    /// The DESTRUCTION-POOL census: what each of the three <c>s_object_slot</c> records is doing,
    /// and how many craters are on the ground.
    /// </summary>
    /// <returns>One readout line.</returns>
    private string SlotCensusLine()
    {
        if (_session.Mission is not { } mission)
        {
            return string.Empty;
        }

        CombatRegisters registers = mission.Combat.Registers;
        PoolArena arena = mission.Combat.Arena;
        List<string> parts = new List<string>();
        for (int slot = Core.Sim.Combat.Lifecycle.ObjectSlotPool.FirstSlot;
             slot <= Core.Sim.Combat.Lifecycle.ObjectSlotPool.LastSlot;
             slot += Core.Sim.Combat.Lifecycle.ObjectSlotPool.SlotBytes)
        {
            byte active = registers.Byte(slot + Core.Sim.Combat.Lifecycle.ObjectSlotPool.ActiveFlag);
            byte state = registers.Byte(slot + Core.Sim.Combat.Lifecycle.ObjectSlotTick.StateByte);
            string name = active == 0
                ? "idle"
                : state switch
                {
                    0 => "armed",
                    1 => "falling",
                    2 => "eject2",
                    3 => "eject3",
                    _ => "chute",
                };
            parts.Add(name);
        }

        EffectCensus effects = mission.VmEffects.Census;
        string states = string.Join(" ", parts);
        int onGround = Core.Sim.Combat.Effects.CraterPool.LiveCount(registers, arena);
        string cheat = _foeHitPoints > 0
            ? string.Create(CultureInfo.InvariantCulture, $"  CHEAT --foe-hp {_foeHitPoints}")
            : string.Empty;
        // The aircraft-shadow slots (subsystem4x04_table [0xB93A]): owner→shadow, '*' = the player's.
        List<string> shadows = new List<string>();
        for (int slot = ShadowTable.FirstSlot; slot <= ShadowTable.LastSlot; slot += ShadowTable.SlotBytes)
        {
            ushort owner = registers.Word(slot + ShadowTable.OwnerObject);
            ushort shadowRef = registers.Word(slot + ShadowTable.ShadowObject);
            shadows.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{owner:X4}{(owner != 0 && owner == registers.PlayerObjectRef ? "*" : string.Empty)}→{shadowRef:X4}"
                    + $"{(arena.Covers(shadowRef, 0x18) && (arena.Word((ushort)(shadowRef + 0x02)) & 1) != 0 ? "!" : string.Empty)}"));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"SLOTS {states}  live {effects.ObjectSlotsLive}  "
                + $"slot-frames {effects.ObjectSlotSlotFrames:N0}  craters {effects.Craters} "
                + $"({onGround} on the ground){cheat}  shadows [{string.Join(" ", shadows)}] player {registers.PlayerObjectRef:X4}\n"
                + $"   SMOOTH updates every-step {_smoother.UpdatesEveryStep:N0}  coarse {_smoother.UpdatesCoarse:N0}  longest {_smoother.LongestIntervalSeconds:F2} s  tracked {_smoother.Tracked}");
    }

    private IReadOnlyList<string> FlightReadout(in FlightSnapshot view) =>
    [
        string.Create(
            CultureInfo.InvariantCulture,
            $"ALT {view.AltitudeFeet,7:N0} ft   SPD {view.AirspeedFps,5} ft/s ({view.AirspeedFps * 3600.0 / 5280.0,6:F1} mph)"),
        string.Create(
            CultureInfo.InvariantCulture,
            $"HDG {view.HeadingDegrees,6:F1}    PITCH {view.PitchDegrees,6:F1}    ROLL {view.RollDegrees,7:F1}"),
        string.Create(
            CultureInfo.InvariantCulture,
            $"THR {view.ThrottlePercent,4} %    {Flags(view)}"),
        string.Create(
            CultureInfo.InvariantCulture,
            $"step {_session.StepsRun,8:N0}  sim {_session.SimulatedSeconds,7:F1} s  "
                + $"{TickClock.TicksPerSecond / TickClock.StepTicks:F1} Hz   host {_hostFps,5:F0} fps"),
        string.Create(
            CultureInfo.InvariantCulture,
            $"{_session.AircraftBasename}  {_session.SeedDescription}  "
                + $"hud {_session.World.HudPosts:N0}  advisor {_session.World.Advisories:N0}"
                + $"{(_session.DroppedSteps > 0 ? $"  DROPPED {_session.DroppedSteps:N0}" : string.Empty)}"),
        string.Create(
            CultureInfo.InvariantCulture,
            $"{ViewNames.NameOf(_view)}{(_rig.FellBack ? " (no anchor - external)" : string.Empty)}  "
                + $"{_session.TheaterAssetName}  meshes {LastScene.InstancesDrawn:N0}/"
                + $"{LastScene.InstancesConsidered:N0}  faces {LastScene.FacesDrawn:N0}/"
                + $"{LastScene.FacesSubmitted:N0}  bf-cull {LastScene.FacesBackfaceCulled:N0}  "
                + $"gear {_session.GearAngleBam}/{Core.Sim.Flight.GearDeployAngle.Retracted}"),
        string.Create(
            CultureInfo.InvariantCulture,
            $"lod {(_sceneOptions.Lod == LodPolicy.Max ? "max" : "classic")}  "
                + $"alpha {_sceneOptions.Alpha.ToString().ToLowerInvariant()}  "
                + $"edges {_sceneOptions.Edges.ToString().ToLowerInvariant()}  "
                + $"clouds {_cloudInstances}  "
                + $"balls {(_groundGridExponent < 0
                    ? "off"
                    : _groundBallsFixed
                        ? $"fixed {_groundBallInstances}x{GroundGrid.BallCount}"
                        : $"classic x{1 << _groundGridExponent}")}"
                + $"  {MenuScaleLine()}"
                + $"  {DebugLookLine()}"),

        // What the fragment buffer did this frame.
        RasterLine(),

        // The analytic ground layer's own census and the widths in force.
        GroundLine(),

        // The tracer halo's settings and what it cost this frame.
        TracerLine(),
    ];

    /// <summary>
    /// What the readout says about the ESC menu's own integer pixel scale, so a player can see by
    /// number what the "Menu Size" row is doing by eye.
    /// </summary>
    private string MenuScaleLine() =>
        _menu is null || _frameWidth <= 0
            ? "menu -"
            : _menu.ScaleDescription(_frameWidth, _frameHeight);

    /// <summary>The readout's own <c>GROUND</c> line: the display list's decal tier.</summary>
    /// <remarks>
    /// The layer is gone — the flat world is in the display list, so what there is to report is how
    /// many primitives its tier holds; its pixels are counted with everything else's on the
    /// <c>RASTER</c> line.
    /// </remarks>
    private string GroundLine()
    {
        GroundDecalStats g = LastScene.Ground;
        LineWidthModel w = _sceneOptions.LineWidths ?? LineWidthModel.Default;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"GROUND decals  {g.Polygons:N0} poly + {g.Bands:N0} band(s), {g.Clipped:N0} clipped"
                + $"  widths road {w.RoadFeet:F0} river {w.RiverFeet:F0} mark {w.MarkFeet:F0}"
                + $" tracer {w.TracerFeet:F2} rope {w.RopeFeet:F2} strut {w.StrutFeet:F2}"
                + $" line {w.LineFeet:F2} ft, floor {w.FloorHostPixels:F2} px");
    }

    /// <summary>The readout's own <c>TRACER</c> line.</summary>
    private string TracerLine()
    {
        TracerHalo halo = _sceneOptions.Tracer ?? CYAC.Port.Render.TracerHalo.Default;
        TracerFrameStats t = LastScene.Tracer;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"TRACER {halo}  {t.Segments} segment(s), {t.Pixels:N0} px, {t.Milliseconds:F3} ms this frame");
    }

    /// <summary>The readout's own <c>RASTER</c> line: the frame's fragment census.</summary>
    private string RasterLine()
    {
        DisplayFrameStats display = LastScene.Display;
        double perPrimitive = display.Primitives == 0
            ? 0.0
            : (double)display.Fragments / display.Primitives;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"RASTER  {display.Primitives:N0} primitives  {display.Fragments:N0} fragments ({perPrimitive:F1}/primitive, {display.FragmentsDropped:N0} dropped by the opaque guard, longest per-pixel list {display.MaxFragmentList})  background {display.BackgroundPixels:N0} px  {display.TrivialAccepts:N0} trivial-accept tile(s)  seam mask {display.SeamMaskInteriorPixels:N0} interior px, {display.SeamPixels:N0} seam px healed, {display.SeamMaskMilliseconds:F2} ms");
    }

    /// <summary>The readout's own <c>ROUNDS</c> line.</summary>
    private string GunneryLine()
    {
        // The trail object exists even when the row is off, so the readout asks whether it is
        // BEARING, and says how much of it is still in the air after a mid-sortie switch-off.
        if (_session.Gunnery is not { Enabled: true } gunnery)
        {
            int expiring = _session.Gunnery is { } off ? off.Rounds.Count + off.Impacts.Count : 0;
            return "ROUNDS off (--rounds off): one tracer per burst"
                + (expiring > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"  ({expiring} still expiring)")
                    : string.Empty);
        }

        GunneryCensus census = gunnery.Census;

        // The KERNEL's own scoring, beside the flashes, so the two can be compared without a second
        // instrument: the flash test is now the same class-record box the kernel's grid query
        // clips against, so `hits air` and `acq` should move together.
        string kernel = string.Empty;
        if (_session.Mission is { } mission)
        {
            ProjectileKernelCensus kc = mission.Context.Projectiles.Census;
            kernel = string.Create(
                CultureInfo.InvariantCulture,
                $"  |  kernel grid {kc.GridQueries:N0}(acq {kc.Acquired:N0}, same {kc.SameTarget:N0})"
                    + $"  fire {kc.FireHandlerCalls:N0}(enemy {kc.EnemyHits:N0}, player {kc.PlayerHits:N0},"
                    + $" none {kc.NoDamage:N0}, miss {kc.NearMiss:N0}, kill {kc.Kills:N0})");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"ROUNDS live {gunnery.Rounds.Count,3}  flashes {gunnery.Impacts.Count,2}  born {census.RoundsBorn:N0}"
                + $"  hits air {census.AirHits:N0}(on player {census.AirHitsOnPlayer:N0}, on foe {census.AirHits - census.AirHitsOnPlayer:N0})"
                + $" ground {census.GroundHits:N0}  expired {census.RoundsExpired:N0}"
                + $"  bursts {census.Bursts:N0} of {census.TracersSeen:N0} tracer(s) (+{census.BurstsWithoutTrail} without a trail)"
                + $"  drawn {_roundInstances} round(s) + {_flashInstances} flash(es)") + kernel;
    }

    /// <summary>The readout's own <c>SMOKE</c> line: the layer beside the sim's own pool.</summary>
    private string SmokeLine()
    {
        int simLive = _session.Mission is { } mission
            ? mission.VmEffects.Census.LivePuffs
            : 0;
        // As above: the layer exists even when the row is off (the dialog needs something to switch
        // on), so the line asks Enabled, and names the puffs still expiring behind it.
        if (_session.Smoke is not { Enabled: true } smoke)
        {
            int expiring = _session.Smoke is { } off ? off.Puffs.Count : 0;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"SMOKE off (--smoke-trail off): the sim's own {simLive} puff(s) of 15 slots")
                + (expiring > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"  ({expiring} layer puff(s) still expiring)")
                    : string.Empty);
        }

        SmokeTrailCensus census = smoke.Census;

        // The layer-owned wreck trail's own line: its live count against its OWN budget, the
        // falling wrecks it has picked up, and the knobs.
        string wreck = smoke.WreckTrail
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"\n            WRECK live {smoke.WreckPuffsLive,3}/{smoke.WreckCapacity}"
                    + $"  trails {census.WreckTrailsSeen:N0}  born {census.WreckPuffsBorn:N0}"
                    + $"  expired {census.WreckPuffsExpired:N0}  evicted {census.WreckPuffsEvicted:N0}"
                    + $"  every {smoke.WreckIntervalSeconds:0.##} s  colour {smoke.WreckColorIndex}"
                    + $"  size x{smoke.WreckSizeScale:0.##}  drawn {_wreckSmokeInstances}")
            : "\n            WRECK off (--wreck-smoke off)";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"SMOKE live {smoke.Puffs.Count,3}/{smoke.Capacity}  sim {smoke.SimPuffsLive,2}/{Core.Sim.Combat.Effects.SmokePuffTable.SlotCount}"
                + $" ({simLive} by tick)  born {census.PuffsBorn:N0} of {census.SpawnsSeen:N0} spawn(s)"
                + $"  expired {census.PuffsExpired:N0}  evicted {census.PuffsEvicted:N0}"
                + $"  life ×{smoke.LifeScale:0.##} ramp {(smoke.StretchRamp ? "stretched" : "original")}"
                + $" fade {smoke.FadeFraction:0.##}  drawn {_smokeInstances}{wreck}");
    }

    private static string Flags(in FlightSnapshot view)
    {
        AircraftStatusFlags flags = (AircraftStatusFlags)view.StatusFlags;
        List<string> parts = new List<string>();
        if (flags.HasFlag(AircraftStatusFlags.LandingGear))
        {
            parts.Add("GEAR");
        }

        if (flags.HasFlag(AircraftStatusFlags.Flaps))
        {
            parts.Add("FLAPS");
        }

        if (flags.HasFlag(AircraftStatusFlags.Airbrake))
        {
            parts.Add("BRAKE");
        }

        if (flags.HasFlag(AircraftStatusFlags.Afterburner))
        {
            parts.Add("AB");
        }

        if (!view.Active)
        {
            parts.Add("INACTIVE");
        }

        return parts.Count == 0 ? "clean" : string.Join(' ', parts);
    }

    private void DrawReadout(Canvas canvas, in FlightSnapshot view)
    {
        int y = 16;
        canvas.SetPenColor(Func.EncodePixelColor(0, 0, 0));
        foreach (string line in ReadoutLines(view))
        {
            canvas.DrawString(line, 17, y + 1, Canvas.Font9X16);
            y += 20;
        }

        y = 16;
        canvas.SetPenColor(Func.EncodePixelColor(255, 255, 255));
        foreach (string line in ReadoutLines(view))
        {
            canvas.DrawString(line, 16, y, Canvas.Font9X16);
            y += 20;
        }
    }
}
