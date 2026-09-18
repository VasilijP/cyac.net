using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// Where the flight kernel's <c>dt</c> comes from.
/// </summary>
/// <remarks>
/// <para>
/// The original measures <c>dt</c> from a ~256.1 Hz PIT counter and scales it by the player's time
/// compression: <c>g_scene_frame_dt_scaled [0xF11C]</c> is <c>5 &lt;&lt; shift</c> under
/// <c>det_v1</c> — measured 5×416,760 / 10×37,526 / 20×53,696 records on the 184559 trace, plus 1×24
/// for the first frame of each flight (K0-F2 note + the parent's 184559 addendum).
/// </para>
/// <para>
/// The port's law is the opposite (D1-c, human direction §9.1): "time compression = more sim steps
/// per presented frame, never a larger dt".  That is a <b>FIX</b>-class quirk — a compressed
/// original session and a compressed port session are different trajectories by construction,
/// because each step's <c>&gt;&gt; 8</c> truncates differently.
/// </para>
/// </remarks>
public enum DtPolicy
{
    /// <summary>
    /// The port's law: <c>dt</c> is <see cref="TickClock.Dt"/>, and time compression multiplies the
    /// number of steps a presented frame runs (<see cref="TimeCompressionMode.StepMultiplier"/>).
    /// The scene-start <c>dt = 1</c> rule still applies to the first flight frame.
    /// </summary>
    TickClock = 0,

    /// <summary>
    /// Compatibility / verification: <c>dt</c> is whatever the frame carries, i.e. the original's
    /// <c>[0xF11C]</c>.  This is the only policy under which a time-compressed recording can be
    /// reproduced step for step.
    /// </summary>
    Recorded = 1,
}

/// <summary>
/// Everything the flight kernel carries from one frame to the next.
/// </summary>
/// <remarks>
/// <para>
/// The three pieces are the original's three: the 298-byte <c>s_aircraft_master</c>
/// (<see cref="Aircraft"/>), the world object <c>master[+0x11A]</c> points at
/// (<see cref="Player"/>, K0 finding F1), and the DGROUP window the chain reads and writes outside
/// them (<see cref="Window"/>).  Nothing else survives a frame: the accumulator block
/// <c>[0xBB40..0xBB4B]</c> is zeroed at the top of every <c>aoa_physics_tick</c> and
/// <c>mat3_vec3_scratch [0x0CD8..0x0CEB]</c> is written before it is read, so both are frame-local
/// and appear only in <see cref="FlightKernelOutputs"/>.
/// </para>
/// <para>
/// <see cref="ControlThresholdFps"/> is hoisted because <c>airspeed_threshold_for_control
/// @image@0x2A76D</c> looks its answer up with the literal key 1 and is therefore a per-aircraft
/// constant; the original pays the lookup every frame.
/// </para>
/// </remarks>
public sealed class FlightKernelState
{
    /// <summary>Creates the kernel's carried state.</summary>
    /// <param name="aircraft">The master struct.</param>
    /// <param name="player">The player's world object (<c>master[+0x11A]</c>).</param>
    /// <param name="window">The DGROUP window (mirrors, calibration, kernel outputs).</param>
    public FlightKernelState(Aircraft aircraft, WorldObject player, FlightInputs window)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(window);
        Aircraft = aircraft;
        Player = player;
        Window = window;
        ControlThresholdFps = FlightEnvelopeQueries.AirspeedThresholdForControl(aircraft.Definition.Envelope);
    }

    /// <summary>The aircraft master struct.</summary>
    public Aircraft Aircraft { get; }

    /// <summary>The player's world object — position and attitude live here, not on the master.</summary>
    public WorldObject Player { get; }

    /// <summary>The DGROUP window the chain reads and writes outside the master.</summary>
    public FlightInputs Window { get; }

    /// <summary>
    /// <c>airspeed_threshold_for_control @image@0x2A76D</c> for this aircraft — a constant, hoisted.
    /// </summary>
    public ushort ControlThresholdFps { get; }

    /// <summary>
    /// How many flight frames this state has run since the flight engine armed.  Zero means the next
    /// frame is the scene's first, which the original runs with <c>dt = 1</c>
    /// (<c>scene_frame_timer_reset @image@0x0C0F8</c>; K0 finding F2).
    /// </summary>
    public long FramesRun { get; private set; }

    /// <summary>
    /// Re-arms the scene-start rule: the next frame is treated as the first of a flight
    /// (<c>flight_engine_first_frame_arm @image@0x225CE</c>).
    /// </summary>
    public void ArmFlight() => FramesRun = 0;

    internal void CountFrame() => FramesRun++;
}

/// <summary>
/// The per-step inputs the flight kernel takes from OUTSIDE itself — everything the driver reads
/// that is neither the aircraft, the player object, nor a value the chain itself wrote.
/// </summary>
/// <param name="JoystickAxisX">
/// <c>g_joystick_axis_x_clamped [0xE478]</c>.  The driver's prologue mirrors it into
/// <c>[0xF1B8]</c> at <c>image@0x2A6AE</c>.
/// </param>
/// <param name="JoystickAxisY">
/// <c>g_joystick_axis_y_clamped [0xE47A]</c> → <c>[0xF1BA]</c> at <c>image@0x2A6B4</c>.
/// </param>
/// <param name="MasterFrameCounter">
/// <c>g_master_frame_counter [0xF0C8]</c> — <c>aoa_range_scanner</c>'s cache key
/// (<c>image@0x2B131</c>).  Under the port's clock this is <see cref="TickClock.MasterFrameCounter"/>.
/// </param>
/// <param name="EasyDifficulty">
/// <c>g_briefing_difficulty_idx [0xF10E] == 0</c> — widens the accepted AoA band by ±1
/// (<c>image@0x2B1D5</c>).
/// </param>
/// <param name="RecordedDt">
/// <c>g_scene_frame_dt_scaled [0xF11C]</c> as the frame carried it.  Consumed only under
/// <see cref="DtPolicy.Recorded"/>; ignored under <see cref="DtPolicy.TickClock"/>.
/// </param>
public readonly record struct FlightFrameInputs(
    short JoystickAxisX,
    short JoystickAxisY,
    ushort MasterFrameCounter,
    bool EasyDifficulty,
    int RecordedDt = 0);

/// <summary>
/// The DGROUP values the flight chain produced this frame — the exact write set measured ("What the chain actually
/// WRITES").
/// </summary>
/// <param name="PhysicsAccumulators">
/// <c>g_aoa_physics_accum_block [0xBB40..0xBB4B]</c> — three <c>i32</c>, rebuilt from zero by every
/// <c>aoa_physics_tick</c>.  Empty when the velocity seam does not model them.
/// </param>
/// <param name="CachedAirspeedHi"><c>[0xF1BE]</c> — written by control integration, read by apply-velocity.</param>
/// <param name="Matrix"><c>mat3_vec3_scratch [0x0CD8..0x0CE9]</c> — the projection matrix, 9 words.</param>
/// <param name="VzScratch"><c>[0x0CEA]</c> — the projected Z the same helper leaves behind.</param>
/// <param name="VerticalSpeed"><c>g_vertical_speed_i32 [0xF1C0..0xF1C3]</c>.</param>
/// <param name="WindDriftAccumulator"><c>g_wind_drift_accumulator [0x3606]</c>.</param>
/// <param name="GroundProximityMargin">
/// <c>[0xF1C4]</c> — <c>flight_check_alive_or_active</c>'s verdict, a speed-derived ground-proximity margin and NOT
/// an "alive" flag.
/// </param>
public readonly record struct FlightKernelOutputs(
    IReadOnlyList<int> PhysicsAccumulators,
    short CachedAirspeedHi,
    Mat3Q14 Matrix,
    ushort VzScratch,
    int VerticalSpeed,
    short WindDriftAccumulator,
    byte GroundProximityMargin);

/// <summary>What one <see cref="FlightKernel.Step(FlightKernelState, FlightFrameInputs, int, IKernelRandom, IKernelWorld, IVelocityDynamics)"/> did — the per-stage censuses, for tests and telemetry.</summary>
/// <param name="Dt">The <c>dt</c> the frame ran with.</param>
/// <param name="ThrottleFuel">Stage S0 → S1.</param>
/// <param name="SavedMassAccumulator">The value stage S1 → S2 stashed and stage S6 → S7 restored.</param>
/// <param name="ControlIntegration">Stage S2 → S3.</param>
/// <param name="ApplyVelocity">Stage S3 → S4.</param>
/// <param name="CrashCheck">Stage S4 → S5.</param>
/// <param name="GroundTilt">Stage S5 → S6.</param>
/// <param name="Outputs">The chain's DGROUP write set for this frame.</param>
public readonly record struct FlightKernelStepResult(
    int Dt,
    ThrottleFuelResult ThrottleFuel,
    int SavedMassAccumulator,
    ControlIntegrationResult ControlIntegration,
    ApplyVelocityResult ApplyVelocity,
    CrashCheckResult CrashCheck,
    GroundTiltOutcome GroundTilt,
    FlightKernelOutputs Outputs);

/// <summary>
/// The per-frame flight driver: <c>aircraft_per_frame_update @image@0x2A69E</c>, the function that
/// owns the whole integer flight kernel for one simulation step.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  The driver is 171 bytes of spine with eight verification stages on it; this class is that spine, with
/// each stage delegated to the type that was verified against the genuine machine for it:
/// </para>
/// <list type="table">
///   <item><term>prologue</term><description><c>[0xF1B8]/[0xF1BA] := [0xE478]/[0xE47A]</c> (<c>image@0x2A6AE</c>/<c>0x2A6B4</c>) — here.</description></item>
///   <item><term>S0 → S1</term><description><see cref="ThrottleFuelStage"/> (<c>image@0x2A5EE</c>).</description></item>
///   <item><term>S1 → S2</term><description><see cref="MassAccumulatorStage.Run"/> — the driver's own inline block (<c>image@0x2A6CC..0x2A6F4</c>).</description></item>
///   <item><term>S2 → S3</term><description><see cref="ControlIntegrationStage"/>, which picks its arm on <c>master[+0x122]</c> (<c>image@0x2A6F9</c>).</description></item>
///   <item><term>S3 → S4</term><description><see cref="ApplyVelocityStage"/> (<c>image@0x2BD26</c>), after the driver saves the PRE-physics altitude (<c>image@0x2A71C</c>).</description></item>
///   <item><term>S4 → S5</term><description><see cref="DamageCheckStage.CheckCrash"/> (<c>image@0x2C07C</c>), called with that saved altitude.</description></item>
///   <item><term>S5 → S6</term><description><see cref="DamageCheckStage.ApplyGroundTilt"/> (<c>image@0x2C184</c>).</description></item>
///   <item><term>S6 → S7</term><description><see cref="MassAccumulatorStage.Restore"/> (<c>image@0x2A732..0x2A743</c>).</description></item>
/// </list>
/// <para>
/// <b>What the port does not model.</b>  The driver's first act is
/// <c>g_active_aircraft_master_ptr [0xF1BC] := BX</c> (<c>image@0x2A6A7</c>); in the port the "active
/// master" is the <see cref="FlightKernelState.Aircraft"/> argument, so the global has no counterpart
/// and the several helpers that re-read it (<c>image@0x2A70D</c>, <c>aoa_range_scanner</c>,
/// <c>airspeed_threshold_for_control</c>) simply take the aircraft.  An earlier pass measured the chain
/// player-only: one distinct master pointer over ~86,000 flight frames of three recordings.
/// </para>
/// <para>
/// <b>Verification.</b> Every stage is exact against the genuine machine on all 86,287 complete flight frames of the
/// three K0 traces, and this driver runs them CLOSED-LOOP — seeded once from the first frame, then
/// driven by inputs alone — in <c>FlightKernelClosedLoopTests</c>.
/// </para>
/// </remarks>
public static class FlightKernel
{
    /// <summary>
    /// The <c>dt</c> the original's first flight frame of a scene runs with: <b>1</b>.
    /// </summary>
    /// <remarks>
    /// <c>scene_frame_timer_reset @image@0x0C0F8</c> seeds <c>[0xF11C]</c> with 1 and the flight
    /// engine's first armed frame reads it before the timer has advanced.  Measured on every stage
    /// and probe record of the first flight step of all three K0 traces (060418 step 92,764;
    /// 185356 step 241,878; 184559 step 192,642) — K0 finding F2, amendment.
    /// </remarks>
    public const int SceneStartDt = 1;

    /// <summary>
    /// Resolves the frame's <c>dt</c> under a policy.
    /// </summary>
    /// <param name="policy">Which law applies.</param>
    /// <param name="state">The kernel state — its <see cref="FlightKernelState.FramesRun"/> decides the scene-start rule.</param>
    /// <param name="clock">The port's clock; required under <see cref="DtPolicy.TickClock"/>.</param>
    /// <param name="recordedDt">The frame's own <c>[0xF11C]</c>; used under <see cref="DtPolicy.Recorded"/>.</param>
    public static int ResolveDt(DtPolicy policy, FlightKernelState state, TickClock? clock, int recordedDt)
    {
        ArgumentNullException.ThrowIfNull(state);
        switch (policy)
        {
            case DtPolicy.Recorded:
                return recordedDt;

            case DtPolicy.TickClock:
                ArgumentNullException.ThrowIfNull(clock);
                return state.FramesRun == 0 ? SceneStartDt : clock.Dt;

            default:
                throw new ArgumentOutOfRangeException(nameof(policy), policy, "not a dt policy");
        }
    }

    /// <summary>
    /// Runs one frame of the flight kernel — the whole of
    /// <c>aircraft_per_frame_update @image@0x2A69E</c>.
    /// </summary>
    /// <param name="state">The carried state; mutated in place, exactly as the original mutates the struct.</param>
    /// <param name="inputs">This frame's outside inputs.</param>
    /// <param name="dt">The frame's <c>dt</c> — see <see cref="ResolveDt"/>.</param>
    /// <param name="random">The kernel's one <c>prng_rand8</c> draw.</param>
    /// <param name="world">Where the three UI notifications and the landing-zone query go.</param>
    /// <param name="velocity">The <c>aoa_physics_tick</c> seam; <see cref="VelocityDynamics.Instance"/> by default.</param>
    /// <returns>The per-stage censuses and the frame's DGROUP write set.</returns>
    public static FlightKernelStepResult Step(
        FlightKernelState state,
        FlightFrameInputs inputs,
        int dt,
        IKernelRandom random,
        IKernelWorld world,
        IVelocityDynamics velocity)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(velocity);

        Aircraft aircraft = state.Aircraft;
        WorldObject player = state.Player;
        FlightInputs window = state.Window;

        // ── PROLOGUE (image@0x2A6A7..0x2A6B4) ────────────────────────────────────────────────
        // [0xF1BC]:= BX is the port's `state.Aircraft` and has no counterpart.  The two mirror
        // copies do: every downstream deflection reads the MIRROR, never the axis, and the stage
        // rewrites the mirrors within the frame, so the copy is load-bearing.
        window.JoystickX = inputs.JoystickAxisX;
        window.JoystickY = inputs.JoystickAxisY;

        // ── S0 → S1 : aircraft_throttle_fuel_step @image@0x2A5EE ────────────────────────────
        ThrottleFuelResult throttleFuel = ThrottleFuelStage.Run(aircraft, dt);

        // ── S1 → S2 : the driver's own heading-accumulator block (image@0x2A6CC..0x2A6F4) ────
        int savedAccumulator = MassAccumulatorStage.Run(aircraft);

        // ── S2 → S3 : control integration, both arms (image@0x2A6F9..0x2A70D) ───────────────
        ControlIntegrationResult integration = ControlIntegrationStage.Run(
            aircraft,
            player.Y,
            window,
            dt,
            inputs.MasterFrameCounter,
            inputs.EasyDifficulty,
            velocity,
            world,
            random);

        // The driver re-reads [0xF1BC], follows master[+0x11A] and saves the world object's CURRENT
        // altitude (image@0x2A70D..0x2A722) — the argument stage S5 consumes.  It is the PRE-physics
        // value, which is why it is taken here and not after the next call.
        int previousAltitude = player.Y;

        // ── S3 → S4 : aircraft_physics_apply_velocity @image@0x2BD26 ────────────────────────
        ApplyVelocityResult applied = ApplyVelocityStage.Run(aircraft, player, window, dt);

        // ── S4 → S5 : damage_or_crash_check @image@0x2C07C (image@0x2A726) ──────────────────
        CrashCheckResult crash = DamageCheckStage.CheckCrash(aircraft, player, window, previousAltitude, world);

        // ── S5 → S6 : damage_recovery_or_tilt @image@0x2C184 (image@0x2A72F) ────────────────
        GroundTiltOutcome tilt = DamageCheckStage.ApplyGroundTilt(aircraft, player, state.ControlThresholdFps);

        // ── S6 → S7 : restore the heading accumulator (image@0x2A732..0x2A743) ──────────────
        MassAccumulatorStage.Restore(aircraft, savedAccumulator);

        state.CountFrame();

        FlightKernelOutputs outputs = new FlightKernelOutputs(
            integration.VelocityTick?.Accumulators ?? [],
            window.CachedAirspeedHi,
            applied.Projection.Matrix,
            applied.Projection.VzScratch,
            window.VerticalSpeed,
            window.WindDriftAccumulator,
            window.GroundProximityFlag);

        return new FlightKernelStepResult(
            dt, throttleFuel, savedAccumulator, integration, applied, crash, tilt, outputs);
    }

    /// <summary>
    /// Runs one frame under the port's own clock and random kernel — the shipping entry point.
    /// </summary>
    /// <param name="state">The carried state.</param>
    /// <param name="inputs">This frame's outside inputs; <see cref="FlightFrameInputs.MasterFrameCounter"/> is ignored in favour of the clock's.</param>
    /// <param name="clock">The port's tick clock; supplies <c>dt</c> and the frame counter.</param>
    /// <param name="streams">The random kernel; the flight kernel draws from <c>Sim.Other</c>.</param>
    /// <param name="world">Where the UI notifications go.</param>
    /// <param name="velocity">The velocity-dynamics seam; <see cref="VelocityDynamics.Instance"/> by default.</param>
    /// <returns>The per-stage censuses and the frame's DGROUP write set.</returns>
    /// <remarks>
    /// This overload is <see cref="DtPolicy.TickClock"/> by construction: <c>dt</c> is
    /// <see cref="TickClock.Dt"/> (<see cref="SceneStartDt"/> on the flight's first frame), so a
    /// session the player time-compresses runs MORE STEPS rather than bigger ones and cannot
    /// reproduce a compressed original recording — that is the FIX, and
    /// <see cref="DtPolicy.Recorded"/> is how a recording is verified.
    /// </remarks>
    public static FlightKernelStepResult Step(
        FlightKernelState state,
        FlightFrameInputs inputs,
        TickClock clock,
        RandomStreams streams,
        IKernelWorld world,
        IVelocityDynamics? velocity = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(streams);
        return Step(
            state,
            inputs with { MasterFrameCounter = clock.MasterFrameCounter },
            ResolveDt(DtPolicy.TickClock, state, clock, inputs.RecordedDt),
            new SimStreamKernelRandom(streams.SimOther),
            world,
            velocity ?? VelocityDynamics.Instance);
    }
}
