using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Sim;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// The velocity-dynamics sub-step the control-integration stage ends in —
/// <c>aoa_physics_tick @image@0x2B092</c>.
/// </summary>
/// <remarks>
/// <para>
/// It has exactly TWO doors image-wide and <b>both control-integration arms end in it</b>:
/// <c>call</c> @<c>image@0x2B695</c> inside <c>aircraft_joystick_integrate_active</c> and
/// <c>call</c> @<c>image@0x2A5EA</c> at the end of
/// <c>aircraft_ctrl_axes_relax_per_frame @image@0x2A5A2</c>.  It is a
/// separate stage, so this stage takes it as a seam.
/// </para>
/// <para>
/// The K0 trace brackets the whole subtree with probes 3/4
/// (<c>aoa_physics_tick_entry</c>/<c>_ret</c>), so a verification test can supply an
/// implementation that simply installs the recorded post-call state — which is what makes this
/// stage verifiable before K3 exists.
/// </para>
/// </remarks>
public interface IVelocityDynamics
{
    /// <summary>Runs one velocity-dynamics tick over the aircraft's own state.</summary>
    /// <param name="aircraft">The aircraft; the tick reads and writes its integrator blocks.</param>
    /// <param name="dt">
    /// <c>g_scene_frame_dt_scaled [0xF11C]</c> for this step.  The original re-reads the global
    /// rather than taking an argument; the port passes it so nothing in <c>Sim/Flight</c> depends on
    /// ambient state.
    /// </param>
    /// <param name="playerAltitudeQ8Feet">
    /// The player world object's <c>pos_y</c> (<c>WorldObject +0x0A</c>, Q8 feet).  The subtree
    /// reaches it through master <c>+0x11A</c> (K0 finding F1) inside
    /// <c>aircraft_fme_speed_interpolate</c>, called from <c>vel_state10_component_accum
    /// @image@0x2AE67</c>.
    /// </param>
    /// <param name="groundProximityFlag">
    /// <c>[0xF1C4]</c> at the moment of the call — the speed-derived ground-proximity margin
    ///.  <c>vel_roll_aoa_correction_accum</c> reads it twice
    /// (<c>image@0x2AD88</c>, <c>image@0x2ADEC</c>).
    /// </param>
    /// <returns>
    /// The tick's branch census, whose <see cref="VelocityTickResult.Accumulators"/> ARE the
    /// <c>g_aoa_physics_accum_block [0xBB40..0xBB4B]</c> window the chain leaves behind — or
    /// <see langword="null"/> when the implementation does not model it (a trace oracle that installs
    /// a recorded post-call state, say).
    /// </returns>
    /// <remarks>
    /// Widened — the subtree reads two ambient values besides the master and
    /// <c>dt</c>, and the rule is that nothing in <c>Sim/Flight</c> reads ambient state.
    /// Both call sites already hold them. Widened — the driver reports the chain's
    /// whole DGROUP write set, and <c>[0xBB40]</c> is produced only here; returning the census beats
    /// a second, stateful accessor.
    /// </remarks>
    VelocityTickResult? Tick(Aircraft aircraft, int dt, int playerAltitudeQ8Feet, byte groundProximityFlag);
}

/// <summary>
/// A velocity-dynamics seam that does nothing — for tests and for a driver assembled before K3
/// lands.
/// </summary>
/// <remarks>
/// Deliberately not a silent default: a caller has to name it, so "the physics did not run" is
/// always a visible decision in the calling code.
/// </remarks>
public sealed class NoVelocityDynamics : IVelocityDynamics
{
    /// <summary>The shared instance.</summary>
    public static NoVelocityDynamics Instance { get; } = new();

    /// <inheritdoc/>
    public VelocityTickResult? Tick(
        Aircraft aircraft, int dt, int playerAltitudeQ8Feet, byte groundProximityFlag) => null;
}

/// <summary>
/// One notification the flight kernel sends OUT of the simulation — the three calls
/// <c>aircraft_joystick_integrate_active</c> makes into the UI.
/// </summary>
/// <remarks>
/// <para>
/// The K0 trace treats all three as a boundary and measured that no flight-kernel state depends on
/// them (<c>flight_advisor_dispatch</c>'s only effect inside the trace window is an idempotent
/// <c>[0xF1BC]</c> write).  The port therefore emits them as fire-and-forget events and never
/// calls UI from the kernel.
/// </para>
/// </remarks>
public interface IKernelWorld
{
    /// <summary>
    /// <c>hud_warning_post @image@0x0CC70</c> — post a HUD warning line
    /// (<c>lcall</c> @<c>image@0x2B5C6</c> and @<c>image@0x2B62A</c>).
    /// </summary>
    /// <param name="messageId">
    /// The message ordinal the original pushes in <c>AX</c>: <c>0x262</c> overspeed,
    /// <c>0x29E</c> low-speed timer expired, <c>0x287</c> / <c>0x26F</c> the two low-speed warnings.
    /// </param>
    /// <param name="messageTable">
    /// The table selector the original pushes in <c>CX</c> — always the literal <c>0x453D</c> at
    /// these three sites.
    /// </param>
    void PostHudWarning(int messageId, int messageTable);

    /// <summary>
    /// <c>flight_advisor_dispatch @image@0x0F35B</c> — run the rate-limited flight-advisor tree
    /// (<c>lcall</c> @<c>image@0x2B44C</c>).
    /// </summary>
    /// <param name="advisoryClass">
    /// The class byte the original leaves in <c>AL</c>: <c>0x0F</c> from the state-3 pull-up arm
    /// (<c>image@0x2B44A</c>), <c>0x10</c> from the overspeed arm (<c>image@0x2B5D5</c>),
    /// <c>0x11</c> from the low-speed arm (<c>image@0x2B62F</c>).
    /// </param>
    void DispatchFlightAdvisor(byte advisoryClass);

    /// <summary>
    /// <c>crash_secondary_check @image@0x09255</c> — is the player inside a landing zone?
    /// (<c>lcall</c> @<c>image@0x2C0C9</c>, its only door image-wide.)
    /// </summary>
    /// <returns>
    /// True when the player object is within <c>g_landing_zone_capture_radius [0x9E74]</c> of an
    /// entry of the landing-zone table — the original's <c>AL = 1</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This one is a QUERY, not a notification, and its answer decides whether a survivable ground
    /// contact kills the aircraft: <c>damage_or_crash_check</c> deactivates on <b>false</b>
    /// (<c>image@0x2C0D0</c>).  It is on this seam because it reads the world (the landing-zone
    /// table, the far heap and the named-mesh globals), not the flight kernel.
    /// </para>
    /// <para>
    /// <c>NullKernelWorld</c> answers <b>false</b> — "no landing zone anywhere" — which is the honest
    /// answer for a kernel driven without a world, not a lenient one.  0 of 86,287 frames
    /// consulted it: the shipped aircraft all fly with <c>status_flags</c> bit 2 clear, so
    /// <c>crash_conditions_valid</c> short-circuits to "crash" before this call is reached
    /// (<c>image@0x2C25F</c>).
    /// </para>
    /// </remarks>
    bool IsWithinLandingZone();
}

/// <summary>A world seam that discards every notification.</summary>
public sealed class NullKernelWorld : IKernelWorld
{
    /// <summary>The shared instance.</summary>
    public static NullKernelWorld Instance { get; } = new();

    /// <inheritdoc/>
    public void PostHudWarning(int messageId, int messageTable)
    {
    }

    /// <inheritdoc/>
    public void DispatchFlightAdvisor(byte advisoryClass)
    {
    }

    /// <inheritdoc/>
    /// <remarks>False — a kernel with no world has no landing zone to be inside of.</remarks>
    public bool IsWithinLandingZone() => false;
}

/// <summary>
/// The single random draw the flight kernel makes — <c>prng_rand8 @image@0x19F06</c>, called once
/// from the state-3 pull-up arm (<c>lcall 0x201D:0x9D36</c> @<c>image@0x2B38B</c>).
/// </summary>
/// <remarks>
/// The determinism contract routes it to <c>Sim.Other</c> ("ejection"), which is an
/// <see cref="Primitives.Lfsr16"/> stream under <c>det_v1</c>.  The stage takes the draw through
/// this seam so a verification test can drive it from a recorded <c>g_prng_state [0x07A8]</c>
/// word instead of a freshly seeded stream.
/// </remarks>
public interface IKernelRandom
{
    /// <summary>Draws eight bits — the original's <c>prng_rand8</c>.</summary>
    byte NextRand8();
}

/// <summary>Routes the kernel's one draw to a <see cref="SimRandomStream"/>.</summary>
/// <param name="stream">The stream — <c>Sim.Other</c> by the consumer map.</param>
public sealed class SimStreamKernelRandom(SimRandomStream stream) : IKernelRandom
{
    private readonly SimRandomStream _stream =
        stream ?? throw new ArgumentNullException(nameof(stream));

    /// <inheritdoc/>
    public byte NextRand8() => unchecked((byte)_stream.Rand8());
}
