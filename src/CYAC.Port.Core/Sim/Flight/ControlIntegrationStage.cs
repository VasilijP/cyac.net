using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>Which of the two control-integration arms a frame took.</summary>
/// <remarks>
/// The driver chooses on <c>cmp byte [bx+0x122],0; jne</c> at <c>image@0x2A6F9</c>, and the K0 trace
/// reports the choice in its <c>flags</c> bit1/bit2.
/// </remarks>
public enum ControlIntegrationArm
{
    /// <summary>
    /// <c>aircraft_ctrl_axes_relax_per_frame @image@0x2A5A2</c> — the aircraft is INACTIVE
    /// (<c>active_flag == 0</c>); the blocks relax toward defaults.
    /// </summary>
    Relax,

    /// <summary>
    /// <c>aircraft_joystick_integrate_active @image@0x2B268</c> — the aircraft is active; the real
    /// joystick → control integration and the three-state machine.
    /// </summary>
    Active,
}

/// <summary>What one <see cref="ControlIntegrationStage"/> step did — report-only, for histograms.</summary>
public record struct ControlIntegrationResult
{
    /// <summary>Which arm ran.</summary>
    public ControlIntegrationArm Arm { get; set; }

    /// <summary>The byte <c>flight_check_alive_or_active</c> produced (the active arm only).</summary>
    public byte GroundProximityFlag { get; set; }

    /// <summary><c>+0x122</c> as the stage found it.</summary>
    public byte StateOnEntry { get; set; }

    /// <summary><c>+0x122</c> as the stage left it.</summary>
    public byte StateOnExit { get; set; }

    /// <summary>The envelope verdict, or −1 when the envelope was not consulted this frame.</summary>
    public int EnvelopeStatus { get; set; }

    /// <summary>The advisory class dispatched, or −1 when no advisory fired.</summary>
    public int AdvisoryClass { get; set; }

    /// <summary>The HUD message posted, or −1 when none was.</summary>
    public int HudMessageId { get; set; }

    /// <summary>Whether <c>aoa_range_scanner</c>'s sweep ran (false ⇒ its per-frame cache hit).</summary>
    public bool RangeScanRan { get; set; }

    /// <summary>Whether a stall probe reported a stall this frame.</summary>
    public bool Stalled { get; set; }

    /// <summary>Whether the state-2 countdown underflowed and armed state 3.</summary>
    public bool CountdownExpired { get; set; }

    /// <summary>Whether the speed gate zeroed the X joystick mirror.</summary>
    public bool JoystickXZeroed { get; set; }

    /// <summary>Whether the speed gate zeroed the Y joystick mirror.</summary>
    public bool JoystickYZeroed { get; set; }

    /// <summary>Which arm the PITCH deflection took, or null when it did not run.</summary>
    public DeflectArm? PitchDeflect { get; set; }

    /// <summary>Which arm the ROLL deflection took, or null when it did not run.</summary>
    public DeflectArm? RollDeflect { get; set; }

    /// <summary>How many <c>ctrl_axis_post_integrate</c> calls ran their body (0..2).</summary>
    public int PostIntegrateRan { get; set; }

    /// <summary>How many <c>ctrl_axis_post_integrate</c> calls were skipped by the airspeed gate.</summary>
    public int PostIntegrateSkipped { get; set; }

    /// <summary>Whether the epilogue's two trim step-towards ran.</summary>
    public bool EpilogueTrimRan { get; set; }

    /// <summary>Whether the state-3 pull-up / ejection arm ran.</summary>
    public bool PullUpArmRan { get; set; }

    /// <summary>Whether the state-3 arm drew from the RNG (only when the bank accumulator is 0).</summary>
    public bool PullUpRandomDrawn { get; set; }

    /// <summary>
    /// The <c>aoa_physics_tick</c> seam's census for this frame, or <see langword="null"/> when the
    /// seam did not model it.  Its <see cref="VelocityTickResult.Accumulators"/> ARE the
    /// <c>g_aoa_physics_accum_block [0xBB40..0xBB4B]</c> window the chain leaves behind, which is
    /// why the driver can report the chain's whole DGROUP write set without a second call.
    /// </summary>
    public VelocityTickResult? VelocityTick { get; set; }
}

/// <summary>
/// Stage S2 → S3 of the per-frame flight driver: control integration, in both its arms.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  The driver tests <c>active_flag +0x122</c> and calls either
/// <c>aircraft_ctrl_axes_relax_per_frame @image@0x2A5A2</c> (76 B, FAR, <c>AX = 0x78</c>) or
/// <c>aircraft_joystick_integrate_active @image@0x2B268</c> (1,238 B, NEAR).  Both end in
/// <c>aoa_physics_tick @image@0x2B092</c>, which this stage takes as the
/// <see cref="IVelocityDynamics"/> seam.
/// </para>
/// <para>
/// Source of truth: the original's own bytes.
/// </para>
/// <para>
/// <b>What leaves the kernel.</b> Three FAR calls (<c>flight_advisor_dispatch @image@0x0F35B</c> and
/// <c>hud_warning_post @image@0x0CC70</c> twice) are UI trees whose return values no flight state
/// depends on; they become <see cref="IKernelWorld"/> events.
/// </para>
/// <para>
/// <b>What the stage writes outside the master.</b>  Measured by K0's census over three recordings:
/// <c>[0xF1BE]</c> (always), <c>[0xF1C4]</c> (always), <c>[0xF1B8]</c>/<c>[0xF1BA]</c> (the speed
/// gates and the state-3 blend), plus <c>[0xBB40..0xBB4B]</c> — which belongs to the velocity
/// seam, not to this stage.
/// </para>
/// </remarks>
public static class ControlIntegrationStage
{
    /// <summary>
    /// The rate AND target the driver hands the relax arm: <c>0x78</c> = 120
    /// (<c>mov ax,0x78</c> @<c>image@0x2A700</c>; the arm never re-reads <c>AX</c>, so the same
    /// constant is both the step rate and the fixed-point target — <c>image@0x2A5A9</c>).
    /// </summary>
    public const short RelaxRollTarget = 0x78;

    /// <summary>The relax arm's Y-position target: −80 (<c>mov dx,0xffb0</c> @<c>image@0x2A5D7</c>).</summary>
    public const short RelaxPositionYTarget = -80;

    /// <summary>The relax arm's Y-position rate: 10 (<c>mov ax,0xa</c> @<c>image@0x2A5D4</c>).</summary>
    public const short RelaxPositionYRate = 10;

    /// <summary>The relax arm's forward-velocity target: 350 (<c>mov dx,0x15e</c> @<c>image@0x2A5E4</c>).</summary>
    public const short RelaxForwardVelocityTarget = 350;

    /// <summary>The relax arm's forward-velocity rate: 25 (<c>mov ax,0x19</c> @<c>image@0x2A5E1</c>).</summary>
    public const short RelaxForwardVelocityRate = 25;

    /// <summary>The ×256 pre-bias the active arm applies to the pitch block: <c>0x100</c>.</summary>
    /// <remarks>
    /// Subtracted from the pitch block's current AND working <c>i32</c>s and from both its bounds
    /// (as a <c>dec</c>) before the deflection, and added back after
    /// (<c>image@0x2B4C2..0x2B4DF</c> / <c>image@0x2B50F..0x2B52C</c>) — a transient shift of the
    /// load-factor origin from 1 G to 0 G for the duration of one integration.
    /// </remarks>
    public const int PitchBias = 0x100;

    /// <summary>The state-3 arm's Y-position target: −90 (<c>mov dx,0xffa6</c> @<c>image@0x2B364</c>).</summary>
    public const short PullUpPositionYTarget = -90;

    /// <summary>The state-3 arm's bank magnitude: ±90 (<c>image@0x2B394</c>/<c>image@0x2B39C</c>).</summary>
    public const short PullUpBankMagnitude = 0x5A;

    /// <summary>The state-3 arm's bank rate scale: <c>0x14</c> = 20 (<c>image@0x2B3AD</c>).</summary>
    public const short PullUpBankRateScale = 0x14;

    /// <summary>The state-3 arm's unbanked bank bias: <c>0xB4</c> = 180 (<c>image@0x2B3A7</c>).</summary>
    public const short PullUpUnbankedBias = 0xB4;

    /// <summary>The full-authority blend base: <c>0x100</c> (<c>image@0x2B3CF</c>).</summary>
    public const short PullUpBlendBase = 0x100;

    /// <summary>
    /// The floor below which the state-3 countdown stops being decremented:
    /// <c>0x9C00</c> read SIGNED = −25600 (<c>cmp word [bx+0xf8],0x9c00 ; jle</c> @<c>image@0x2B424</c>).
    /// </summary>
    public const short PullUpCountdownFloor = unchecked((short)0x9C00);

    /// <summary>The HUD message ordinal for an overspeed warning (<c>image@0x2B5BE</c>).</summary>
    public const int OverspeedMessageId = 0x262;

    /// <summary>The HUD message ordinal for "the low-speed timer expired" (<c>image@0x2B604</c>).</summary>
    public const int LowSpeedExpiredMessageId = 0x29E;

    /// <summary>
    /// The HUD message ordinal posted once the timer has passed
    /// <see cref="Aircraft.LowSpeedWarningThreshold"/> but not yet its limit (<c>image@0x2B61A</c>).
    /// </summary>
    public const int LowSpeedLateMessageId = 0x287;

    /// <summary>
    /// The HUD message ordinal posted while the timer is still below
    /// <see cref="Aircraft.LowSpeedWarningThreshold"/> (<c>image@0x2B622</c>).
    /// </summary>
    public const int LowSpeedEarlyMessageId = 0x26F;

    /// <summary>The message-table selector all three HUD posts push (<c>mov cx,0x453d</c>).</summary>
    public const int HudMessageTable = 0x453D;

    /// <summary>The advisory class the state-3 pull-up arm dispatches (<c>image@0x2B44A</c>).</summary>
    public const byte PullUpAdvisoryClass = 0x0F;

    /// <summary>The advisory class the overspeed arm dispatches (<c>image@0x2B5D5</c>).</summary>
    public const byte OverspeedAdvisoryClass = 0x10;

    /// <summary>The advisory class the low-speed arm dispatches (<c>image@0x2B62F</c>).</summary>
    public const byte LowSpeedAdvisoryClass = 0x11;

    /// <summary>The airspeed floor below which the X joystick mirror is zeroed: 5 (<c>image@0x2B2C4</c>).</summary>
    public const short JoystickXAirspeedFloor = 5;

    /// <summary>The rate the epilogue's X-position step uses: <c>0x2D</c> = 45 (<c>image@0x2B6BF</c>).</summary>
    public const short EpilogueBankRate = 0x2D;

    /// <summary>The rate both epilogue trim steps use: <c>0x28</c> = 40 (<c>image@0x2B706</c>).</summary>
    public const short EpilogueTrimRate = 0x28;

    /// <summary>The epilogue trim target for the <c>+0x30</c> block: <c>0x1E</c> = 30 (<c>image@0x2B709</c>).</summary>
    public const short EpilogueTrimTargetRollDead = 0x1E;

    /// <summary>The epilogue trim target for the <c>+0x50</c> block: <c>0x14</c> = 20 (<c>image@0x2B719</c>).</summary>
    public const short EpilogueTrimTargetRollAlive = 0x14;

    /// <summary>Runs the stage, choosing the arm the way the driver does.</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="playerAltitudeQ8Feet">
    /// The player world object's <c>pos_y</c> (K0 finding F1: master <c>+0x11A</c> is the player
    /// object, not an FMD copy).
    /// </param>
    /// <param name="inputs">The joystick mirrors, calibration and pull-up tuning.</param>
    /// <param name="dt"><c>g_scene_frame_dt_scaled [0xF11C]</c> for this step.</param>
    /// <param name="frameCounter"><c>g_master_frame_counter [0xF0C8]</c>, the range scanner's cache key.</param>
    /// <param name="easyDifficulty"><c>g_briefing_difficulty_idx [0xF10E] == 0</c>.</param>
    /// <param name="velocity">The <c>aoa_physics_tick</c> seam both arms end in.</param>
    /// <param name="world">Where the three UI notifications go.</param>
    /// <param name="random">The one <c>prng_rand8</c> draw the state-3 arm can make.</param>
    /// <returns>Which arms fired — report-only.</returns>
    public static ControlIntegrationResult Run(
        Aircraft aircraft,
        int playerAltitudeQ8Feet,
        FlightInputs inputs,
        int dt,
        ushort frameCounter,
        bool easyDifficulty,
        IVelocityDynamics velocity,
        IKernelWorld world,
        IKernelRandom random)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(velocity);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(random);

        ControlIntegrationResult result = new ControlIntegrationResult
        {
            StateOnEntry = aircraft.ActiveState,
            EnvelopeStatus = -1,
            AdvisoryClass = -1,
            HudMessageId = -1,
        };

        // image@0x2A6F9 `cmp byte [bx+0x122],0 ; jne` — the driver's own choice.
        if (aircraft.ActiveState == 0)
        {
            result.VelocityTick = RunRelaxArm(aircraft, playerAltitudeQ8Feet, inputs, dt, velocity);
            result.Arm = ControlIntegrationArm.Relax;
            result.StateOnExit = aircraft.ActiveState;
            return result;
        }

        result.Arm = ControlIntegrationArm.Active;
        RunActiveArm(
            aircraft, playerAltitudeQ8Feet, inputs, dt, frameCounter, easyDifficulty,
            velocity, world, random, ref result);
        result.StateOnExit = aircraft.ActiveState;
        return result;
    }

    /// <summary>
    /// Convenience overload for a driver that already holds the port's clock, streams and player
    /// object.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="playerObject">The player's world object — its <c>Y</c> is the altitude.</param>
    /// <param name="inputs">The joystick mirrors, calibration and pull-up tuning.</param>
    /// <param name="clock">The tick clock; its <see cref="TickClock.Dt"/> and
    /// <see cref="TickClock.MasterFrameCounter"/> are read.</param>
    /// <param name="streams">The random streams; the kernel's one draw is <c>Sim.Other</c>.</param>
    /// <param name="easyDifficulty"><c>g_briefing_difficulty_idx [0xF10E] == 0</c>.</param>
    /// <param name="velocity">The velocity-dynamics seam.</param>
    /// <param name="world">Where the three UI notifications go.</param>
    /// <returns>Which arms fired — report-only.</returns>
    /// <remarks>
    /// <see cref="TickClock.Dt"/> is <b>not</b> the whole story on the first flight frame:
    /// <c>[0xF11C]</c> reads 1 on the first frame after the flight engine arms and 5 afterwards,
    /// and time compression scales it further.  The kernel driver owns that rule; this overload
    /// simply asks the clock.
    /// </remarks>
    public static ControlIntegrationResult Run(
        Aircraft aircraft,
        WorldObject playerObject,
        FlightInputs inputs,
        TickClock clock,
        RandomStreams streams,
        bool easyDifficulty,
        IVelocityDynamics velocity,
        IKernelWorld world)
    {
        ArgumentNullException.ThrowIfNull(playerObject);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(streams);
        return Run(
            aircraft,
            playerObject.Y,
            inputs,
            clock.Dt,
            clock.MasterFrameCounter,
            easyDifficulty,
            velocity,
            world,
            new SimStreamKernelRandom(streams.SimOther));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // aircraft_ctrl_axes_relax_per_frame @image@0x2A5A2 — the INACTIVE arm (76 B, 7 calls, 0
    // branches).
    private static VelocityTickResult? RunRelaxArm(
        Aircraft aircraft,
        int playerAltitudeQ8Feet,
        FlightInputs inputs,
        int dt,
        IVelocityDynamics velocity)
    {
        // image@0x2A5AB — the ONE asymmetric case: driven to a fixed non-zero target, at a rate
        // equal to that same target, so it snaps in about one frame.
        ControlAxisKernel.CtrlAxisStepToward(
            aircraft, MasterBlock.RollDead, RelaxRollTarget, RelaxRollTarget, dt);

        // image@0x2A5B6 / 0x2A5C0 / 0x2A5CA — decay to zero at each block's own +0x0E rate.
        ControlAxisKernel.CtrlAxisAliveNormalize(aircraft, MasterBlock.Pitch, dt);
        ControlAxisKernel.CtrlAxisAliveNormalize(aircraft, MasterBlock.HeadingAoa, dt);
        ControlAxisKernel.CtrlAxisAliveNormalize(aircraft, MasterBlock.RollAlive, dt);

        // image@0x2A5DA / 0x2A5E7 — the same template applied to the POSITION and VELOCITY overlays.
        ControlAxisKernel.CtrlAxisStepToward(
            aircraft, MasterBlock.AttitudePitch, RelaxPositionYRate, RelaxPositionYTarget, dt);
        ControlAxisKernel.CtrlAxisStepToward(
            aircraft, MasterBlock.ForwardVelocity,
            RelaxForwardVelocityRate, RelaxForwardVelocityTarget, dt);

        return velocity.Tick(aircraft, dt, playerAltitudeQ8Feet, inputs.GroundProximityFlag);   // image@0x2A5EA
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    //  aircraft_joystick_integrate_active @image@0x2B268 — the ACTIVE arm.
    // ═════════════════════════════════════════════════════════════════════════════════════════
    private static void RunActiveArm(
        Aircraft aircraft,
        int altitude,
        FlightInputs inputs,
        int dt,
        ushort frameCounter,
        bool easyDifficulty,
        IVelocityDynamics velocity,
        IKernelWorld world,
        IKernelRandom random,
        ref ControlIntegrationResult result)
    {
        // ── §A the prologue (image@0x2B268..0x2B28E) ─────────────────────────────────────────
        // The three control-surface words are SAVED on the frame and restored in the epilogue, so
        // everything this function does to them is transient — except on the low-speed-expired arm,
        // which overwrites two of the SAVED copies (image@0x2B5F6 / image@0x2B5FD).
        short savedElevator = aircraft.ElevatorAuthority;      // [bp-0xE]
        short savedRollGain = aircraft.RollAuthorityGain;      // [bp-0xC]
        short savedHeadingInput = aircraft.HeadingInput;       // [bp-0x10]

        inputs.GroundProximityFlag = ControlAxisKernel.GroundProximityFlag(aircraft, altitude);   // image@0x2B28B
        result.GroundProximityFlag = inputs.GroundProximityFlag;

        // image@0x2B28E — aoa_range_scanner @0x2B12A, K1's AoaProbes.
        result.RangeScanRan =
            AoaProbes.ScanValidLoadFactorRange(aircraft, altitude, frameCounter, easyDifficulty);

        // ── §B the altitude cut-off and the two speed gates (image@0x2B291..0x2B2D0) ─────────
        // `cmp es:[si+0xC],ax ; jle` — the compared word is the player object's pos_y HIGH word.
        short altitudeHighWord = unchecked((short)(altitude >> 16));
        if (altitudeHighWord > aircraft.AltitudeCutoffForHeadingInput)
        {
            aircraft.HeadingInput = 0;                          // image@0x2B2A3
        }

        if (inputs.GroundProximityFlag != 0)
        {
            // image@0x2B2B0 airspeed_threshold_for_control @0x2A76D — K1's query.  Its only side
            // effect is caching BX into [0xF1BC], the value already there.
            short threshold = unchecked((short)FlightEnvelopeQueries.AirspeedThresholdForControl(
                aircraft.Definition.Envelope));
            short airspeed = unchecked((short)aircraft.CurrentAirspeedFps);

            // image@0x2B2B9/0x2B2BC — SIGNED `jge`, so EQUALITY SKIPS the zeroing.
            if (airspeed < threshold)
            {
                inputs.JoystickY = 0;
                result.JoystickYZeroed = true;
            }

            // image@0x2B2C4/0x2B2C8 — SIGNED `jge`, equality skips.
            if (airspeed < JoystickXAirspeedFloor)
            {
                inputs.JoystickX = 0;
                result.JoystickXZeroed = true;
            }
        }

        // ── the state dispatch (image@0x2B2D0) ───────────────────────────────────────────────
        int advisoryClass = -1;
        if (aircraft.ActiveState == 3)
        {
            RunPullUpArm(aircraft, altitude, inputs, dt, world, random, ref result);
            advisoryClass = PullUpAdvisoryClass;                 // image@0x2B44A
        }
        else
        {
            advisoryClass = RunEnvelopeDispatch(
                aircraft, altitude, dt, world, ref result, ref savedElevator, ref savedHeadingInput);
        }

        if (advisoryClass >= 0)
        {
            world.DispatchFlightAdvisor((byte)advisoryClass);    // image@0x2B44C
            result.AdvisoryClass = advisoryClass;
        }

        RunSharedTail(
            aircraft, altitude, inputs, dt, velocity, ref result,
            savedElevator, savedRollGain, savedHeadingInput);
    }

    /// <summary>
    /// §C..§I / §P..§S — the state-2 gate, the envelope verdict and its four arms.
    /// </summary>
    /// <returns>The advisory class to dispatch, or −1.</returns>
    private static int RunEnvelopeDispatch(
        Aircraft aircraft,
        int altitude,
        int dt,
        IKernelWorld world,
        ref ControlIntegrationResult result,
        ref short savedElevator,
        ref short savedHeadingInput)
    {
        // ── §C the state-2 gate (image@0x2B454) ──────────────────────────────────────────────
        if (aircraft.ActiveState == 2)
        {
            // image@0x2B45B — aoa_stall_probe @0x2B1FE.
            if (AoaProbes.StallProbe(aircraft, altitude))
            {
                result.Stalled = true;

                // ── §H the countdown arm (image@0x2B488) ─────────────────────────────────────
                short countdown = unchecked((short)(aircraft.StateCountdown - dt));
                aircraft.StateCountdown = countdown;
                if (countdown >= 0)
                {
                    return -1;                                   // `jns` @image@0x2B493
                }

                aircraft.ActiveState = 3;                        // image@0x2B495
                aircraft.StateCountdown = aircraft.PullUpCountdownReload;   // image@0x2B49A
                result.CountdownExpired = true;
                return -1;
            }

            aircraft.ActiveState = 1;                            // image@0x2B466
        }

        // ── the envelope call (image@0x2B46C) — K1's fme_envelope_status_eval ────────────────
        byte status = FlightEnvelopeQueries.EvaluateStatus(aircraft, altitude).Status;
        result.EnvelopeStatus = status;

        // ── §E the four-way switch (image@0x2B46F..0x2B484) ──────────────────────────────────
        switch (status)
        {
            case 1:
                // §F/§G — image@0x2B4A4 aoa_stall_probe again.
                if (!AoaProbes.StallProbe(aircraft, altitude))
                {
                    return -1;                                   // straight to the shared tail
                }

                result.Stalled = true;

                // §I the state-2 arm (image@0x2B590)
                aircraft.ActiveState = 2;
                aircraft.StateCountdown = aircraft.StallCountdownReload;
                aircraft.LowSpeedElapsed = 0;                    // image@0x2B5A1
                return -1;

            case 2:
                return RunOverspeedArm(aircraft, world, ref result);

            case 3:
                return RunLowSpeedArm(
                    aircraft, dt, world, ref result, ref savedElevator, ref savedHeadingInput);

            default:
                // status 0 — or, through the raw-byte quirk, anything above 3.
                aircraft.LowSpeedElapsed = 0;                    // image@0x2B5A1
                return -1;
        }
    }

    /// <summary>§P/§Q — status 2, overspeed at altitude (<c>image@0x2B5AA</c>).</summary>
    private static int RunOverspeedArm(
        Aircraft aircraft, IKernelWorld world, ref ControlIntegrationResult result)
    {
        aircraft.ElevatorAuthority = 0;                          // image@0x2B5B0
        aircraft.HeadingInput = 0;                               // image@0x2B5B4
        aircraft.RollAuthorityGain = unchecked((short)(aircraft.RollAuthorityGain << 2));

        world.PostHudWarning(OverspeedMessageId, HudMessageTable);   // image@0x2B5C6
        result.HudMessageId = OverspeedMessageId;

        aircraft.LowSpeedElapsed = 0;                            // image@0x2B5CF
        return OverspeedAdvisoryClass;
    }

    /// <summary>§R/§S — status 3, below minimum speed (<c>image@0x2B5DA</c>).</summary>
    private static int RunLowSpeedArm(
        Aircraft aircraft,
        int dt,
        IKernelWorld world,
        ref ControlIntegrationResult result,
        ref short savedElevator,
        ref short savedHeadingInput)
    {
        aircraft.RollAuthorityGain = unchecked((short)(aircraft.RollAuthorityGain << 1));

        short limit = aircraft.LowSpeedWarningLimit;             // image@0x2B5E2
        short elapsed = unchecked((short)(aircraft.LowSpeedElapsed + dt));   // image@0x2B5EA
        aircraft.LowSpeedElapsed = elapsed;

        int messageId;
        if (elapsed >= limit)                                    // SIGNED `jl` @image@0x2B5F2
        {
            // The timer EXPIRED: the two SAVED copies are rewritten, so the epilogue restores
            // zeros instead of the entry values (image@0x2B5F6 / image@0x2B5FD).
            savedElevator = 0;
            aircraft.ElevatorAuthority = 0;
            savedHeadingInput = 0;
            aircraft.HeadingInput = 0;
            messageId = LowSpeedExpiredMessageId;
        }
        else if (aircraft.LowSpeedWarningThreshold > elapsed)    // SIGNED `jg` @image@0x2B618
        {
            messageId = LowSpeedEarlyMessageId;
        }
        else
        {
            messageId = LowSpeedLateMessageId;
        }

        world.PostHudWarning(messageId, HudMessageTable);        // image@0x2B62A
        result.HudMessageId = messageId;
        return LowSpeedAdvisoryClass;
    }

    /// <summary>
    /// §K..§O — the shared tail every arm joins at <c>image@0x2B4AE</c>: the two deflections, the
    /// heading-from-AoA interpolation, the physics seam and the epilogue.
    /// </summary>
    private static void RunSharedTail(
        Aircraft aircraft,
        int altitude,
        FlightInputs inputs,
        int dt,
        IVelocityDynamics velocity,
        ref ControlIntegrationResult result,
        short savedElevator,
        short savedRollGain,
        short savedHeadingInput)
    {
        // image@0x2B4AE — a low-altitude frame forces the state back to 1.
        if (inputs.GroundProximityFlag != 0)
        {
            aircraft.ActiveState = 1;
        }

        // ── the ×256 pre-bias (image@0x2B4C2..0x2B4DF) ───────────────────────────────────────
        ControlAxisState pitch = aircraft.PitchAxis;
        pitch.Working = unchecked(pitch.Working - PitchBias);
        pitch.Current = unchecked(pitch.Current - PitchBias);
        pitch.HiBound = unchecked((short)(pitch.HiBound - 1));
        pitch.LoBound = unchecked((short)(pitch.LoBound - 1));

        // image@0x2B4E0..0x2B4FD — the pitch deflection.  The envelope bounds are the AoA scan's
        // outputs MINUS ONE (`dec ax` @image@0x2B4E9 / image@0x2B4EF).
        result.PitchDeflect = ControlAxisKernel.JoystickToControlDeflect(
            aircraft,
            MasterBlock.Pitch,
            inputs.Calibration.YMinimum,
            inputs.Calibration.YMaximum,
            inputs.JoystickY,
            unchecked((short)(aircraft.ValidLoadFactorMin - 1)),
            unchecked((short)(aircraft.ValidLoadFactorMax - 1)),
            dt);

        NotePostIntegrate(ControlAxisKernel.PostIntegrate(aircraft, MasterBlock.Pitch), ref result);

        // ── the matching post-bias (image@0x2B50F..0x2B52C) ──────────────────────────────────
        pitch.HiBound = unchecked((short)(pitch.HiBound + 1));
        pitch.LoBound = unchecked((short)(pitch.LoBound + 1));
        pitch.Working = unchecked(pitch.Working + PitchBias);
        pitch.Current = unchecked(pitch.Current + PitchBias);

        // ── the airspeed hand-off and the heading-from-AoA result (image@0x2B52D..0x2B556) ───
        short airspeed = unchecked((short)aircraft.CurrentAirspeedFps);
        inputs.CachedAirspeedHi = airspeed;                      // image@0x2B530 → [0xF1BE]

        short heading = unchecked((short)ControlAxisKernel.LinearInterpClamped(
            unchecked((short)pitch.Current), airspeed));         // image@0x2B535/0x2B539
        aircraft.HeadingAoaAxis.Working = heading;               // image@0x2B543/0x2B546 (+0x44, cwd)
        aircraft.HeadingAoaAxis.Current = aircraft.HeadingAoaAxis.Working;   // image@0x2B553/0x2B556

        // ── the roll deflection: which block depends on the ground-proximity flag ────────────
        if (inputs.GroundProximityFlag != 0)
        {
            // §L image@0x2B563 — the +0x50 block, with the X axis and BOTH calibration bounds
            // NEGATED (three `neg ax`), then the +0x30 block decayed toward zero.
            result.RollDeflect = ControlAxisKernel.JoystickToControlDeflect(
                aircraft,
                MasterBlock.RollAlive,
                unchecked((short)-inputs.Calibration.XMinimum),
                unchecked((short)-inputs.Calibration.XMaximum),
                unchecked((short)-inputs.JoystickX),
                aircraft.RollAliveAxis.LoBound,                  // push [bx+0x5A] → [bp+0xA]
                aircraft.RollAliveAxis.HiBound,                  // push [bx+0x58] → [bp+0xC]
                dt);
            ControlAxisKernel.CtrlAxisAliveNormalize(aircraft, MasterBlock.RollDead, dt);
        }
        else
        {
            // §M image@0x2B634 — the +0x30 block, un-negated, then +0x50 zeroed outright.
            result.RollDeflect = ControlAxisKernel.JoystickToControlDeflect(
                aircraft,
                MasterBlock.RollDead,
                inputs.Calibration.XMinimum,
                inputs.Calibration.XMaximum,
                inputs.JoystickX,
                aircraft.RollDeadAxis.LoBound,                   // push [bx+0x3A] → [bp+0xA]
                aircraft.RollDeadAxis.HiBound,                   // push [bx+0x38] → [bp+0xC]
                dt);
            NotePostIntegrate(
                ControlAxisKernel.PostIntegrate(aircraft, MasterBlock.RollDead), ref result);
            aircraft.RollAliveAxis.Current = 0;                  // image@0x2B664/0x2B667
            aircraft.RollAliveAxis.Working = 0;                  // image@0x2B66A/0x2B66D
        }

        // ── §N the physics seam (image@0x2B670..0x2B695) ─────────────────────────────────────
        int savedPitchCurrent = 0;
        bool pitchParked = aircraft.ActiveState == 3;
        if (pitchParked)
        {
            savedPitchCurrent = pitch.Current;                   // [bp-8]/[bp-6]
            pitch.Current = PitchBias;                           // image@0x2B689/0x2B68F
        }

        // The two ambient values the subtree reads are taken AT the call: [0xF1C4] may have
        // been rewritten by the prologue (image@0x2B28B) and by §K's force-to-1 above.
        result.VelocityTick = velocity.Tick(aircraft, dt, altitude, inputs.GroundProximityFlag);  // image@0x2B695

        // ── §O the epilogue (image@0x2B698..0x2B743) ─────────────────────────────────────────
        // The restore is gated on the state as it stands AFTER the physics tick, not on the
        // `pitchParked` decision — the two can disagree if the tick moves +0x122.
        if (aircraft.ActiveState == 3)
        {
            // If the tick had MOVED +0x122 to 3, the original would restore from an uninitialised
            // frame slot ([bp-8]/[bp-6] is written only by the park above); the port restores a
            // zero instead.  No observed frame does that — the velocity subtree has no writer for
            // +0x122 — so it is documented, not modelled.
            pitch.Current = savedPitchCurrent;                   // image@0x2B6A9/0x2B6AD
        }

        if (inputs.GroundProximityFlag != 0)
        {
            // image@0x2B6BF — step the X-position block toward 0 at rate 45.
            ControlAxisKernel.CtrlAxisStepToward(
                aircraft, MasterBlock.AttitudeRoll, EpilogueBankRate, 0, dt);

            aircraft.AngularVelocityA.Value = 0;                 // image@0x2B6CC/0x2B6CF

            // image@0x2B6D2..0x2B6E1: zero block B's value unless its HIGH word is negative.
            int angularB = aircraft.AngularVelocityB.Value;
            if (unchecked((short)(angularB >> 16)) >= 0 && angularB != 0)
            {
                aircraft.AngularVelocityB.Value = 0;
            }
        }

        // image@0x2B6E8/0x2B6EC — a negative forward velocity is zeroed; equality is NOT negative.
        if (unchecked((short)(aircraft.ForwardVelocity.Value >> 16)) < 0)
        {
            aircraft.ForwardVelocity.Value = 0;
        }

        // image@0x2B6FD/0x2B701 — SIGNED `jg`, so EQUALITY RUNS the two trim steps.
        if (aircraft.LowSpeedWarningThreshold <= aircraft.LowSpeedElapsed)
        {
            ControlAxisKernel.CtrlAxisStepToward(
                aircraft, MasterBlock.RollDead, EpilogueTrimRate, EpilogueTrimTargetRollDead, dt);
            ControlAxisKernel.CtrlAxisStepToward(
                aircraft, MasterBlock.RollAlive, EpilogueTrimRate, EpilogueTrimTargetRollAlive, dt);
            result.EpilogueTrimRan = true;
        }

        // image@0x2B71F..0x2B737 — the transient restore, in the original's order.
        aircraft.HeadingInput = savedHeadingInput;
        aircraft.ElevatorAuthority = savedElevator;
        aircraft.RollAuthorityGain = savedRollGain;
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // §D-state3 — the pull-up / ejection autopilot, image@0x2B2DA..0x2B44A (~380 B).
    // Ported straight from the BYTES.
    // ═════════════════════════════════════════════════════════════════════════════════════════
    private static void RunPullUpArm(
        Aircraft aircraft,
        int altitude,
        FlightInputs inputs,
        int dt,
        IKernelWorld world,
        IKernelRandom random,
        ref ControlIntegrationResult result)
    {
        result.PullUpArmRan = true;
        PullUpTuningTable tuning = inputs.PullUpTuning;

        // image@0x2B2DB..0x2B2E6 — the envelope speed for the CURRENT load factor at this altitude.
        // The original pushes (master, curve.seg, curve.off) and calls
        // aircraft_fme_speed_interpolate @0x2A7FA (`ret 6`); a missing curve would make it read
        // through a null far pointer, which the port refuses rather than reproduces.
        EnvelopeCurve curve = FlightEnvelopeQueries.FindCurve(aircraft.Definition.Envelope, aircraft.GLoadInteger)
                              ?? throw new InvalidOperationException(
                                  "the pull-up arm reached aircraft_fme_speed_interpolate @image@0x2A7FA with no "
                                  + $".fme curve for load factor {aircraft.GLoadInteger}; the original would read "
                                  + "through the null far pointer fme_record_lookup_by_key returned "
                                  + "(image@0x2B2DF), which is a wild read, not a defined value.");

        short envelopeSpeed = unchecked((short)FlightEnvelopeQueries.InterpolateSpeed(
            aircraft, altitude, curve, out _));                  // [bp-4]

        short airspeed = unchecked((short)aircraft.CurrentAirspeedFps);
        short capped = airspeed > envelopeSpeed ? envelopeSpeed : airspeed;   // image@0x2B2F6 `jle`

        // image@0x2B301..0x2B324 — the speed error, shifted and clamped into the tuning window.
        short authority = unchecked((short)((envelopeSpeed - capped) << tuning.SpeedErrorShift));
        if (authority < tuning.SpeedErrorFloor)
        {
            authority = tuning.SpeedErrorFloor;                  // image@0x2B316
        }

        if (authority > tuning.SpeedErrorCeiling)
        {
            authority = tuning.SpeedErrorCeiling;                // image@0x2B324
        }

        // image@0x2B32D..0x2B34F — and capped again by how far the countdown has run.
        short ramp = unchecked((short)(
            (aircraft.PullUpCountdownReload - aircraft.StateCountdown) >> tuning.CountdownShift));
        if (ramp > tuning.CountdownCeiling)
        {
            ramp = tuning.CountdownCeiling;                      // image@0x2B344
        }

        if (authority > ramp)
        {
            authority = ramp;                                    // image@0x2B34F
        }

        // image@0x2B352..0x2B367 — pull the nose up: drive the Y-position block toward −90.
        short pitchRate = Fixed.MulDiv16SignedShr8(tuning.PitchRate, authority);
        ControlAxisKernel.CtrlAxisStepToward(
            aircraft, MasterBlock.AttitudePitch, pitchRate, PullUpPositionYTarget, dt);

        // image@0x2B36E..0x2B3AA — and roll wings-level, or pick a side.
        short bankTarget;
        if (aircraft.StatusFlags.HasFlag(AircraftStatusFlags.Unknown6))
        {
            int bank = aircraft.AttitudeRoll;
            bool right;
            if (bank > 0)
            {
                right = true;                                    // image@0x2B379/0x2B381
            }
            else if (bank < 0)
            {
                right = false;                                   // image@0x2B389
            }
            else
            {
                // image@0x2B38B — the kernel's ONE random draw: a coin flip on an exactly level
                // aircraft.  `test al,1 ; je` ⇒ bit 0 clear rolls LEFT.
                right = (random.NextRand8() & 1) != 0;
                result.PullUpRandomDrawn = true;
            }

            bankTarget = right ? PullUpBankMagnitude : (short)-PullUpBankMagnitude;
        }
        else
        {
            // image@0x2B3A4 — the unbanked case: the bank word's middle byte pair, plus 180.
            bankTarget = unchecked((short)(aircraft.AttitudeRollMidWord + PullUpUnbankedBias));
        }

        short bankRate = Fixed.MulDiv16SignedShr8(PullUpBankRateScale, authority);
        ControlAxisKernel.CtrlAxisStepToward(
            aircraft, MasterBlock.AttitudeRoll, bankRate, bankTarget, dt);   // image@0x2B3C2
        ControlAxisKernel.WorldWrapAxis(aircraft, MasterBlock.AttitudeRoll); // image@0x2B3CC

        // image@0x2B3CF..0x2B400 — fade the pilot's own inputs out as the autopilot takes over.
        short blend = unchecked((short)(PullUpBlendBase - authority));
        if (blend < tuning.BlendFloor)
        {
            blend = tuning.BlendFloor;                           // image@0x2B3DE
        }

        inputs.JoystickX = Fixed.MulDiv16SignedShr8(
            inputs.JoystickX, unchecked((short)(blend >> 2)));   // image@0x2B3E6/0x2B3ED
        inputs.JoystickY = Fixed.MulDiv16SignedShr8(inputs.JoystickY, blend);   // image@0x2B3FB

        // image@0x2B403..0x2B41A — the elevator uses the UNCLAMPED blend: the original recomputes
        // `0x100 − authority` from scratch (image@0x2B40E) rather than reading [bp-0xA] back.
        aircraft.ElevatorAuthority = Fixed.MulDiv16SignedShr8(
            aircraft.ElevatorAuthority, unchecked((short)(PullUpBlendBase - authority)));
        aircraft.RollAuthorityGain = unchecked((short)(aircraft.RollAuthorityGain << 2));

        // image@0x2B424..0x2B445 — run the countdown down, then let a recovered aircraft out.
        if (aircraft.StateCountdown > PullUpCountdownFloor)
        {
            aircraft.StateCountdown = unchecked((short)(aircraft.StateCountdown - dt));
        }

        if (aircraft.StateCountdown <= 0)                        // SIGNED `jg` @image@0x2B438
        {
            bool stalled = AoaProbes.StallProbe(aircraft, altitude);   // image@0x2B43A
            result.Stalled |= stalled;
            if (!stalled)
            {
                aircraft.ActiveState = 1;                        // image@0x2B445
            }
        }
    }

    private static void NotePostIntegrate(bool ran, ref ControlIntegrationResult result)
    {
        if (ran)
        {
            result.PostIntegrateRan++;
        }
        else
        {
            result.PostIntegrateSkipped++;
        }
    }
}
