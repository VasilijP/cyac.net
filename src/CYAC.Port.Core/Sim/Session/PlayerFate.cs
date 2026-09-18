using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Flight;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>How the player's sortie ended.</summary>
/// <remarks>
/// Each cause is named by the WRITER the port already has; nothing here invents a sim rule.
/// </remarks>
public enum PlayerFateCause
{
    /// <summary>Still flying.</summary>
    None = 0,

    /// <summary>
    /// <c>damage_or_crash_check</c>'s kill arm: <c>master[+0x122] := 0</c>
    /// (<see cref="CrashCheckOutcome.Killed"/>, <c>image@0x2C0D6</c>).
    /// </summary>
    Crash,

    /// <summary>
    /// The player's own death deadline expired — <c>engagement_per_frame_tick @image@0x0FE3C</c>
    /// writes <c>g_player_slot_destroyed_flag [0xEE58] := 1</c>, <c>scene_setup_or_camera_reset(2)</c>
    /// and <c>[0xC390] := frame + 4</c>.
    /// </summary>
    ShotDown,

    /// <summary>
    /// <c>engagement_kill_query @image@0x226B2</c> named an object at the player's own position —
    /// <c>flight_engine_per_frame_top</c>'s Path A (<c>image@0x229B7</c>).
    /// </summary>
    Rammed,

    /// <summary>The pilot pulled the handle: Shift-E, <c>image@0x012F8..0x0134B</c>.</summary>
    Ejected,
}

/// <summary>Which of <c>damage_or_crash_check</c>'s two kill arms fired.</summary>
public enum PlayerCrashReason
{
    /// <summary>Not a crash.</summary>
    None = 0,

    /// <summary>
    /// <c>crash_conditions_valid @image@0x2C25C</c> rejected the contact — gear up, too much bank,
    /// too much pitch, too fast, too much rotation or too much sink.
    /// </summary>
    ConditionsRejected,

    /// <summary>
    /// The contact was survivable but the aeroplane was outside every landing zone
    /// (<c>crash_secondary_check @image@0x09255</c>).
    /// </summary>
    OutsideLandingZone,
}

/// <summary>Where the fate machine has got to.</summary>
public enum PlayerFatePhase
{
    /// <summary>Nothing has happened; the flight kernel owns the aeroplane.</summary>
    Flying = 0,

    /// <summary>
    /// The sortie is over and the aeroplane is still in the air: the fate machine's own ballistic
    /// fall is flying the wreck down.
    /// </summary>
    Destroyed,

    /// <summary>The wreck is on the ground and the destruction sequence is playing.</summary>
    Wreck,

    /// <summary>The hold expired; the host may restart or wait for the player.</summary>
    Ended,
}

/// <summary>What one simulation step tells the fate machine about the player.</summary>
/// <param name="ActiveState"><c>master[+0x122]</c> after this step's flight chain.</param>
/// <param name="CrashOutcome">Which arm <see cref="DamageCheckStage.CheckCrash"/> took this step.</param>
/// <param name="CrashKilledByConditions">
/// <see cref="CrashCheckResult.KilledBy"/>: true when <c>crash_conditions_valid</c> rejected the
/// contact, false when the landing-zone query did.
/// </param>
/// <param name="ObjectDestroyedFlag"><c>g_player_slot_destroyed_flag [0xEE58]</c>.</param>
/// <param name="ProximityKillRef">
/// <c>engagement_kill_query @image@0x226B2</c>'s answer this step, or 0 (also 0 when the host does
/// not run the query — a Test Flight has no world grid).
/// </param>
/// <param name="EjectRequested">The Shift-E arm ran this step.</param>
public readonly record struct PlayerFateObservation(
    byte ActiveState,
    CrashCheckOutcome CrashOutcome,
    bool CrashKilledByConditions,
    byte ObjectDestroyedFlag,
    ushort ProximityKillRef,
    bool EjectRequested);

/// <summary>
/// THE PLAYER'S OWN END — an <b>authorised deviation</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the ORIGINAL does, and why this exists.</b>  The original has no player death sequence at
/// all: the frame the flight-end channel goes 1 → 0,
/// <c>flight_engine_per_frame_top @image@0x227C7</c> takes Path A
/// (<c>image@0x229AA..0x229E8</c>) — <c>scene_setup_or_camera_reset(3)</c> then
/// <c>destroyed_flag_set_and_frame_deadline_arm @image@0x1004A</c>, which arms
/// <c>g_session_end_deadline_frame [0xC390] := [0xF0C8] + 4</c> — and four frames later
/// <c>mission_state_machine</c> (<c>image@0x00B48</c>) calls
/// <c>flight_session_end @image@0x30328</c> and the screen becomes the DEBRIEF, whose blurb is
/// <c>damage_state_blurb_renderer @image@0x10060</c> ("Augured in").  <b>It cuts away within four
/// frames and never draws the crash.</b>
/// </para>
/// <para>
/// The PoC has no debrief screen, so those four frames became "keep stepping the flight kernel for
/// ever" — the relax arm of <c>ControlIntegrationStage</c> freezes the controls but
/// <c>ApplyVelocityStage</c> keeps integrating the frozen velocity, and the wreck slides across the
/// ground at its impact speed until the player presses <c>r</c>.  That is the slide seen in play,
/// and it is an artefact of the PoC, not of the game.
/// </para>
/// <para>
/// <b>The deviation.</b>  This type replaces those never-ending frames with a small state machine —
/// <see cref="PlayerFatePhase.Flying"/> → <see cref="PlayerFatePhase.Destroyed"/> →
/// <see cref="PlayerFatePhase.Wreck"/> → <see cref="PlayerFatePhase.Ended"/> — that (a) stops the
/// flight kernel dead, exactly as the original stops running flight frames, and (b) plays the
/// destruction sequence H7 built for an ENEMY on the player's own object.  Everything it triggers on
/// is a writer the port already had; everything it ADDS is presentation-adjacent and labelled.
/// </para>
/// <para>
/// <b>Law.</b>  It is inert until its trigger frame — it writes no simulation state at all while
/// <see cref="Phase"/> is <see cref="PlayerFatePhase.Flying"/> — and it is OFF by construction on
/// every verifying path (the two host replays never build a <see cref="PlayerFate"/> at all,
/// <c>TraceReplay</c> drives <c>FlightKernel.Step</c> directly).  <see cref="Enabled"/> is the
/// host's <c>--fate off</c> knob.
/// </para>
/// <para>
/// It is in <c>Sim/</c> and not in the renderer because a headless test has to be able to assert its
/// numbers, and because the fall it integrates writes the player's world object — which is what
/// makes the smoke trail, the shadow and the chase camera follow the wreck down with no special
/// case anywhere else.  It uses <c>double</c>, which is legal here for the same reason
/// <c>Sim/Session</c>'s other host-side glue is: it is NOT a verified kernel body and nothing
/// downstream of it is compared to a recording.
/// </para>
/// </remarks>
public sealed class PlayerFate
{
    /// <summary>How long the wreck is held before <see cref="PlayerFatePhase.Ended"/>, in seconds.</summary>
    public const double DefaultHoldSeconds = 6.0;

    /// <summary>How long after the trigger the "fly again" hint appears, in seconds.</summary>
    public const double HintAfterSeconds = 1.5;

    /// <summary>
    /// The port's own ballistic constant: 32.174 ft/s², standard gravity
    /// (<c>platform</c>: the aeroplane's world units ARE feet — <c>altitude_ft_get @image@0x0F5AE</c>
    /// is <c>pos_y &gt;&gt; 8</c>, so one world unit is one foot).
    /// </summary>
    /// <remarks>
    /// DEVIATION.  The original has no falling wreck to give a constant to; this is the port's.
    /// </remarks>
    public const double GravityFeetPerSecondSquared = 32.174;

    /// <summary>The port's own slow roll while the wreck falls, in degrees per second.</summary>
    /// <remarks>DEVIATION, chosen so a six-second fall turns the aeroplane about one and a half
    /// times — enough to read as "out of control", slow enough not to strobe.</remarks>
    public const double FallRollDegreesPerSecond = 90.0;

    /// <summary>Position units per world unit (foot): the arena's <c>world &lt;&lt; 8</c>.</summary>
    public const double PositionUnitsPerFoot = 256.0;

    private double _x;
    private double _y;
    private double _z;
    private double _vx;
    private double _vy;
    private double _vz;
    private double _headingDegrees;
    private double _rollDegrees;
    private byte _previousActiveState = 1;
    private byte _previousDestroyedFlag;
    private bool _restLatched;
    private int _restX;
    private int _restY;
    private int _restZ;
    private Angle _restHeading;
    private Angle _restPitch;
    private Angle _restRoll;
    private bool _impactPending;
    private bool _smokePending;

    /// <summary>Whether the machine does anything at all (the host's <c>--fate</c> knob).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// PASSIVE mode (<c>--fate off</c>): classify and MEASURE, change nothing.
    /// </summary>
    /// <remarks>
    /// It is what produces the BEFORE number: with the machine passive the
    /// flight kernel keeps stepping exactly as it did before H9, the aeroplane keeps sliding, and
    /// <see cref="RestDriftX"/>/<see cref="RestDriftZ"/> measure how far.  A passive machine writes
    /// no simulation state at all and makes no world-grid query.
    /// </remarks>
    public bool Passive { get; init; }

    /// <summary>
    /// The aeroplane's resting altitude in POSITION units — <c>master[+0x116]</c>, the player
    /// object's class-record ground clearance (H2: <c>image@0x2A165..0x2A16F</c>; 5,376 = 21 ft for
    /// the P-51).
    /// </summary>
    /// <remarks>
    /// It is what <c>flight_check_alive_or_active @image@0x2C22C</c> compares the player's <c>pos_y</c>
    /// against, so resting the wreck ON it puts the aeroplane exactly where the original leaves a
    /// parked one — never sunk into the ground plane.
    /// </remarks>
    public int GroundClearance { get; init; }

    /// <summary>How long the wreck is held before the machine reports <see cref="PlayerFatePhase.Ended"/>.</summary>
    public double HoldSeconds { get; init; } = DefaultHoldSeconds;

    /// <summary>Where the machine has got to.</summary>
    public PlayerFatePhase Phase { get; private set; } = PlayerFatePhase.Flying;

    /// <summary>What ended the sortie.</summary>
    public PlayerFateCause Cause { get; private set; } = PlayerFateCause.None;

    /// <summary>Which crash arm fired, when <see cref="Cause"/> is <see cref="PlayerFateCause.Crash"/>.</summary>
    public PlayerCrashReason CrashReason { get; private set; } = PlayerCrashReason.None;

    /// <summary>The object <c>engagement_kill_query</c> named, when the cause was a ram.</summary>
    public ushort RammedObjectRef { get; private set; }

    /// <summary>Whether the 500-mph rule killed the ejecting pilot (<c>image@0x0130F</c>).</summary>
    public bool EjectionWasFatal { get; set; }

    /// <summary>The simulation step the trigger landed on.</summary>
    public long DestroyedStep { get; private set; } = -1;

    /// <summary>Seconds of simulated time since the trigger.</summary>
    public double SecondsSinceDestroyed { get; private set; }

    /// <summary>Seconds since the wreck reached the ground, or 0 while it is still falling.</summary>
    public double SecondsSinceRest { get; private set; }

    /// <summary>How many steps the machine has run since the trigger.</summary>
    public long StepsSinceDestroyed { get; private set; }

    /// <summary>How far the wreck's X has moved since it came to rest, in world units.</summary>
    /// <remarks>The acceptance number: it must be exactly 0.</remarks>
    public double RestDriftX { get; private set; }

    /// <summary>…and its Z.</summary>
    public double RestDriftZ { get; private set; }

    /// <summary>True while the sortie is over — the flight kernel must not be stepped again.</summary>
    public bool FlightStopped => !Passive && Phase != PlayerFatePhase.Flying;

    /// <summary>
    /// True once the machine is in <see cref="PlayerFatePhase.Ended"/> — the host's cue to restart
    /// (<c>--respawn</c>) or to wait for the player's <c>r</c>.
    /// </summary>
    public bool Ended => Phase == PlayerFatePhase.Ended;

    /// <summary>
    /// Whether the wreck should be watched from OUTSIDE: the cockpit is not a place to be after the
    /// pilot has left it.
    /// </summary>
    public bool WantsExternalView => !Passive && Phase != PlayerFatePhase.Flying;

    /// <summary>
    /// Whether the camera should follow the EJECTING PILOT rather than the aeroplane — true only for
    /// <see cref="PlayerFateCause.Ejected"/>, where H7's three-slot pool really has a pilot object.
    /// </summary>
    public bool WantsPilotView => Cause == PlayerFateCause.Ejected;

    /// <summary>The banner the host draws over the frame.</summary>
    public string Banner => Cause switch
    {
        PlayerFateCause.Crash => CrashReason == PlayerCrashReason.OutsideLandingZone
            ? "YOU CRASHED - no runway under you"
            : "YOU CRASHED",
        PlayerFateCause.ShotDown => "SHOT DOWN",
        PlayerFateCause.Rammed => "COLLISION",
        PlayerFateCause.Ejected => EjectionWasFatal ? "EJECTED - too fast" : "EJECTED - safe",
        _ => string.Empty,
    };

    /// <summary>Whether the "fly again" hint has been up long enough to show.</summary>
    public bool ShowHint =>
        Phase != PlayerFatePhase.Flying && SecondsSinceDestroyed >= HintAfterSeconds;

    /// <summary>
    /// Takes the ONE "the wreck just hit the ground" event, if it is pending.
    /// </summary>
    /// <returns>True exactly once per sortie, on the frame the wreck reaches the ground.</returns>
    /// <remarks>
    /// The host answers it with H7's own ground-impact treatment —
    /// <c>SessionEffects.ImpactEffects</c>, i.e. <c>subsystem5x06_per_frame_slot_fire @image@0x2DB69</c>
    /// (a <c>crater</c>) plus a deferred-effect record with <c>+0x0C = +0x0D = 1</c> (the bitmap
    /// explosion and the smoke column that follows it).
    /// </remarks>
    public bool TakeImpact()
    {
        bool pending = _impactPending;
        _impactPending = false;
        return pending;
    }

    /// <summary>Takes the ONE "start trailing smoke" event, if it is pending.</summary>
    /// <returns>True exactly once, on the trigger frame of a fall.</returns>
    /// <remarks>
    /// Answered with <c>SessionEffects.SpawnDamageSmoke</c> — the same <c>subsystem4x19</c> emitter
    /// row <c>weapon_fire_combat_loop</c> attaches to a damaged bandit
    /// (<c>image@0x0BF6C..0x0BF92</c>), owner = the player's own pool object.
    /// </remarks>
    public bool TakeSmokeTrail()
    {
        bool pending = _smokePending;
        _smokePending = false;
        return pending;
    }

    /// <summary>
    /// Runs the machine for one simulation step, AFTER the step's kernels have run.
    /// </summary>
    /// <param name="state">The flight kernel's state — the player world object is what this writes.</param>
    /// <param name="observation">What the step's kernels reported.</param>
    /// <param name="dtSeconds">The step's own duration in seconds.</param>
    /// <param name="step">The session's step counter, for <see cref="DestroyedStep"/>.</param>
    /// <returns>
    /// True when the flight kernel must NOT be stepped again — i.e. the sortie is over.
    /// </returns>
    public bool Step(
        FlightKernelState state, in PlayerFateObservation observation, double dtSeconds, long step)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Enabled)
        {
            return false;
        }

        if (Phase == PlayerFatePhase.Flying)
        {
            PlayerFateCause cause = Classify(observation);
            byte active = observation.ActiveState;
            byte destroyed = observation.ObjectDestroyedFlag;
            _previousActiveState = active;
            _previousDestroyedFlag = destroyed;
            if (cause == PlayerFateCause.None)
            {
                return false;
            }

            Trigger(state, cause, observation, step);
            return FlightStopped;
        }

        Advance(state, dtSeconds);
        return FlightStopped;
    }

    /// <summary>Which trigger, if any, this step carried.</summary>
    /// <param name="observation">The step's report.</param>
    /// <remarks>
    /// The order is the order the ORIGINAL would resolve them in: the pilot's own command first
    /// (<c>image@0x012F8</c> runs inside the key ladder, before the frame's flight chain), then the
    /// flight chain's crash verdict, then the two things
    /// <c>flight_engine_per_frame_top</c>'s Path A tests — the <c>[0xF0BA]</c> edge and
    /// <c>engagement_kill_query</c>.
    /// </remarks>
    private PlayerFateCause Classify(in PlayerFateObservation observation)
    {
        if (observation.EjectRequested)
        {
            return PlayerFateCause.Ejected;
        }

        // (a) master[+0x122] 1 → 0 with the crash verdict — DamageCheckStage.CheckCrash's kill arm.
        if (observation.ActiveState == 0 && _previousActiveState != 0
            && observation.CrashOutcome == CrashCheckOutcome.Killed)
        {
            return PlayerFateCause.Crash;
        }

        // (b) [0xEE58] 0 → 1 — the sustain tick's death deadline (image@0x0FE3C).
        if (observation.ObjectDestroyedFlag != 0 && _previousDestroyedFlag == 0)
        {
            return PlayerFateCause.ShotDown;
        }

        // (c) engagement_kill_query named something (image@0x229B7's kill_result).
        return observation.ProximityKillRef != 0 ? PlayerFateCause.Rammed : PlayerFateCause.None;
    }

    /// <summary>Latches the fate and the pose the aeroplane had at the trigger frame.</summary>
    private void Trigger(
        FlightKernelState state,
        PlayerFateCause cause,
        in PlayerFateObservation observation,
        long step)
    {
        Cause = cause;
        DestroyedStep = step;
        RammedObjectRef = observation.ProximityKillRef;
        CrashReason = cause == PlayerFateCause.Crash
            ? observation.CrashKilledByConditions
                ? PlayerCrashReason.ConditionsRejected
                : PlayerCrashReason.OutsideLandingZone
            : PlayerCrashReason.None;

        WorldObject player = state.Player;
        _x = player.X / PositionUnitsPerFoot;
        _y = player.Y / PositionUnitsPerFoot;
        _z = player.Z / PositionUnitsPerFoot;
        _headingDegrees = player.Heading.ToDegrees();
        _rollDegrees = SignedDegrees(player.Roll);
        double pitchRadians = SignedDegrees(player.Pitch) * Math.PI / 180.0;
        double headingRadians = _headingDegrees * Math.PI / 180.0;

        // The engine's own forward vector — CameraPose's remarks derive it from
        // BodyVelocityProjection.RotationFromEuler: forward(h,p) = (-sin h cos p, sin p, cos h cos p).
        double speed = state.Aircraft.CurrentAirspeedFps;
        _vx = -Math.Sin(headingRadians) * Math.Cos(pitchRadians) * speed;
        _vy = Math.Sin(pitchRadians) * speed;
        _vz = Math.Cos(headingRadians) * Math.Cos(pitchRadians) * speed;

        double ground = GroundClearance / PositionUnitsPerFoot;
        if (cause == PlayerFateCause.Crash || _y <= ground)
        {
            // Already on the ground: the wreck rests where it stopped, clamped ON the plane.
            _y = Math.Max(_y, ground);
            EnterRest(state);
            return;
        }

        Phase = PlayerFatePhase.Destroyed;
        _smokePending = !Passive;
        Publish(state);
    }

    /// <summary>One step of the fall, the rest, or the hold.</summary>
    private void Advance(FlightKernelState state, double dtSeconds)
    {
        StepsSinceDestroyed++;
        SecondsSinceDestroyed += dtSeconds;

        if (Passive)
        {
            // MEASURE ONLY: whatever the still-running flight kernel did to the player object is the
            // slide, and this is the "before" number.
            SecondsSinceRest += dtSeconds;
            MeasureDrift(state);
            return;
        }

        if (Phase == PlayerFatePhase.Destroyed)
        {
            // DEVIATION — the port's own ballistic fall.  The velocity vector the aeroplane had at
            // the trigger, plus gravity; the drawn pitch follows that vector, so the nose drops on
            // its own instead of being animated.  Drag is deliberately absent: a wreck that keeps
            // its speed reaches the ground, and a PoC that models drag here would be inventing a
            // second flight model.
            _vy -= GravityFeetPerSecondSquared * dtSeconds;
            _x += _vx * dtSeconds;
            _y += _vy * dtSeconds;
            _z += _vz * dtSeconds;
            _rollDegrees += FallRollDegreesPerSecond * dtSeconds;

            double ground = GroundClearance / PositionUnitsPerFoot;
            if (_y <= ground)
            {
                _y = ground;
                EnterRest(state);
                return;
            }

            Publish(state);
            return;
        }

        SecondsSinceRest += dtSeconds;
        Publish(state);
        if (Phase == PlayerFatePhase.Wreck && SecondsSinceRest >= HoldSeconds)
        {
            Phase = PlayerFatePhase.Ended;
        }
    }

    /// <summary>Puts the wreck on the ground and asks the host for H7's impact treatment.</summary>
    private void EnterRest(FlightKernelState state)
    {
        Phase = PlayerFatePhase.Wreck;
        _impactPending = !Passive;

        // The RESTING attitude is level: a wreck lies on its belly.  Latching the three integers here
        // (rather than re-rounding the doubles every step) is what makes the drift EXACTLY zero.
        _restX = ToPositionUnits(_x);
        _restY = Math.Max(ToPositionUnits(_y), GroundClearance);
        _restZ = ToPositionUnits(_z);
        _restHeading = Angle.Wrap(unchecked((short)Math.Round(_headingDegrees * Angle.UnitsPerDegree)));
        _restPitch = Angle.Zero;
        _restRoll = Angle.Zero;
        _restLatched = true;
        Publish(state);
    }

    /// <summary>Writes the machine's pose into the player world object.</summary>
    /// <remarks>
    /// This is the ONE sim write the fate machine makes, and it happens only after the trigger frame.
    /// It goes into the world object rather than into a presentation-only pose so that everything
    /// already wired to the player — <c>MissionSession.PublishPlayerPose</c> and therefore the arena
    /// object, the aircraft-shadow subsystem, the damage-smoke emitter and every camera in
    /// <c>ViewCamera</c> — follows the wreck down with no special case.
    /// </remarks>
    private void Publish(FlightKernelState state)
    {
        if (Passive)
        {
            MeasureDrift(state);
            return;
        }

        WorldObject player = state.Player;
        if (_restLatched)
        {
            RestDriftX = (player.X - _restX) / PositionUnitsPerFoot;
            RestDriftZ = (player.Z - _restZ) / PositionUnitsPerFoot;
            player.X = _restX;
            player.Y = _restY;
            player.Z = _restZ;
            player.Heading = _restHeading;
            player.Pitch = _restPitch;
            player.Roll = _restRoll;
            return;
        }

        player.X = ToPositionUnits(_x);
        player.Y = ToPositionUnits(_y);
        player.Z = ToPositionUnits(_z);
        player.Heading = Angle.Wrap(
            unchecked((short)Math.Round(_headingDegrees * Angle.UnitsPerDegree)));

        double speed = Math.Sqrt((_vx * _vx) + (_vy * _vy) + (_vz * _vz));
        double pitchDegrees = speed > 0
            ? Math.Asin(Math.Clamp(_vy / speed, -1.0, 1.0)) * 180.0 / Math.PI
            : 0.0;
        player.Pitch = Angle.Wrap(unchecked((short)Math.Round(pitchDegrees * Angle.UnitsPerDegree)));
        player.Roll = Angle.Wrap(unchecked((short)Math.Round(_rollDegrees * Angle.UnitsPerDegree)));
    }

    /// <summary>How far the player object has moved from the latched rest point.</summary>
    /// <param name="state">The flight kernel's state.</param>
    private void MeasureDrift(FlightKernelState state)
    {
        if (!_restLatched)
        {
            return;
        }

        RestDriftX = (state.Player.X - _restX) / PositionUnitsPerFoot;
        RestDriftZ = (state.Player.Z - _restZ) / PositionUnitsPerFoot;
    }

    /// <summary>World units (feet) → the arena's <c>world &lt;&lt; 8</c> position units.</summary>
    private static int ToPositionUnits(double feet) =>
        (int)Math.Round(Math.Clamp(feet * PositionUnitsPerFoot, int.MinValue, int.MaxValue));

    /// <summary>A canonical BAM as signed degrees — <c>FlightSnapshot</c>'s own conversion.</summary>
    private static double SignedDegrees(Angle angle) =>
        Angle.Normalize(unchecked((short)angle.Units)) / (double)Angle.UnitsPerDegree;
}
