using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Sim.Mission;
using CYAC.Port.Core.Sim.Session;

namespace CYAC.Port.Host.Sim;

/// <summary>
/// WHEN THE SORTIE IS OVER, and what the debrief says: the host-side watcher that turns the fate
/// machine's verdict and the mission module's <c>get_debrief_text</c> into the screen the original
/// shows after a flight.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the original does.</b>  <c>mission_state_machine</c> leaves the flight loop when
/// <c>g_session_end_deadline_frame [0xC390]</c> expires, calls <c>flight_session_end
/// @image@0x30328</c>, then <c>ui_post_death_message @image@0x25BAF</c> and
/// <c>ui_post_mission_stats_screen @image@0x26138</c>.  The first of those branches on
/// <c>g_object_destroyed_flag [0xEE58]</c>: destroyed ⇒ the random "you died" blurb and mode 6;
/// alive ⇒ the module's own verdict and mode 4 or 5.  <see cref="MissionDebrief"/> is that decision.
/// </para>
/// <para>
/// <b>Winning does not end the flight.</b>  The module raises <c>[0xB562]</c>, the dispatch fires
/// advisory <c>0x12</c>, and the sortie carries on — the pilot flies home.  So this watcher ends the
/// sortie on the two things that really end it: the player's own death (the fate machine, H9) and a
/// LANDING.
/// </para>
/// <para>
/// <b>(open) — the landing end.</b>  Which byte ends a sortie by landing is not decoded: the
/// original's own live path scores a landing (<c>ui_post_death_message</c>'s tail compares the
/// frame time against <c>alt_object[+0x0A:+0x0C]</c> and then calls
/// <c>landing_zone_bbox_distance</c>), but what LEAVES the flight loop after a successful landing is
/// not one of the writers the port has named.  The host therefore uses its own rule —
/// <see cref="LandedSeconds"/> seconds stopped on the ground inside a landing zone with the gear
/// down — and says so.
/// </para>
/// </remarks>
public sealed class MissionOutcome
{
    private readonly UiStringCatalogDto _strings;
    private double _stoppedSeconds;
    private bool _airborne;

    /// <summary>Builds a watcher over the string catalogue the death blurbs come from.</summary>
    /// <param name="strings">The tree's <c>strings.json</c>.</param>
    public MissionOutcome(UiStringCatalogDto strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        _strings = strings;
    }

    /// <summary>
    /// How long the aeroplane must sit still in a landing zone before the sortie counts as over —
    /// the host's own rule, not the original's.
    /// </summary>
    public const double LandedSeconds = 3.0;

    /// <summary>Below this ground speed the aeroplane counts as stopped, in ft/s.</summary>
    public const int StoppedFps = 12;

    /// <summary>
    /// How far above its resting height the aeroplane may be and still count as ON THE GROUND, in
    /// the kernel's 1/256-ft units — one foot.
    /// </summary>
    /// <remarks>
    /// A parked aeroplane rests at exactly <c>master[+0x116]</c>, its class ground clearance
    /// (mission 44 measures <c>Player.Y == 4864 == AirspeedB</c> with the wheels on the runway), and
    /// <see cref="PlayerFate"/> rests a wreck at the same height.  A foot of slack absorbs the
    /// gear's own travel without letting a low pass read as a landing.
    /// </remarks>
    public const int GroundSlack = 256;

    /// <summary>The debrief, once the sortie is over; null while it is still flying.</summary>
    public MissionDebrief? Debrief { get; private set; }

    /// <summary>The simulated second the sortie ended, or −1.</summary>
    public double EndedAtSeconds { get; private set; } = -1.0;

    /// <summary>Whether the sortie ended because the aeroplane was landed and stopped.</summary>
    public bool EndedByLanding { get; private set; }

    /// <summary>
    /// Whether the aeroplane has LEFT THE GROUND at least once this sortie.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sortie may only be ended by a landing once there has been a take-off.  Several missions
    /// START the player parked — mission 44 "Going Downtown" opens on the runway at 19 ft and 0 ft/s,
    /// inside the theatre's own landing zone — so without this latch <see cref="Landed"/> holds from
    /// the first step and the sortie ends itself <see cref="LandedSeconds"/> later, before the pilot
    /// has touched the throttle.  The debrief that followed was "not accomplished", which made it
    /// read as a win-rule fault; the win rules were never consulted.
    /// </para>
    /// <para>
    /// The test is the altitude half of <see cref="Landed"/> — above the aeroplane's own
    /// <c>master[+0x116]</c> ground clearance — so TAXIING is not a take-off and an aborted roll
    /// cannot end the sortie either.  A mission that starts in the air latches on its first step,
    /// which is every mission the port could already fly.
    /// </para>
    /// </remarks>
    public bool HasBeenAirborne => _airborne;

    /// <summary>Whether the sortie is over.</summary>
    public bool Ended => Debrief is not null;

    /// <summary>Reads the session after one simulation step.  It writes nothing into the sim.</summary>
    /// <param name="session">The host's session.</param>
    public void Observe(FlightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Ended || session.Mission is not { } mission)
        {
            return;
        }

        // ── the player's own end (H9's fate machine, which already models all four causes) ─────
        if (session.Fate is { Enabled: true, Passive: false, Phase: PlayerFatePhase.Ended })
        {
            End(session, mission, MissionDebriefOutcome.Killed);
            return;
        }

        // ── the take-off latch: a landing can only END what a take-off began ──────────────────
        _airborne |= Airborne(session);

        // ── landed and stopped ───────────────────────────────────────────────────────────────
        if (_airborne && Landed(session))
        {
            _stoppedSeconds += FlightSession.StepSeconds;
            if (_stoppedSeconds >= LandedSeconds)
            {
                EndedByLanding = true;
                End(session, mission, MissionDebriefOutcome.Survived);
            }

            return;
        }

        _stoppedSeconds = 0;
    }

    /// <summary>Whether the aeroplane is off the ground right now.</summary>
    /// <param name="session">The host's session.</param>
    /// <remarks>
    /// The altitude test is <see cref="Landed"/>'s own, negated: <c>Player.Y</c> above the
    /// aeroplane's class ground clearance <c>master[+0x116]</c>, in the same 1/256-ft units.
    /// </remarks>
    public static bool Airborne(FlightSession session) => !OnGround(session);

    /// <summary>Whether the wheels are on the ground right now — the altitude test, alone.</summary>
    /// <param name="session">The host's session.</param>
    /// <remarks>
    /// A UNITS bug: <c>master[+0x116]</c> is ALREADY in the kernel's 1/256-ft units, the same units
    /// as <c>Player.Y</c>, so shifting it again put the "on the ground" ceiling at 4,865 FEET.  It
    /// was inert only because <see cref="Landed"/> also demands <see cref="StoppedFps"/>, which
    /// nothing in the air ever satisfies; it would have made the take-off latch below meaningless.
    /// </remarks>
    public static bool OnGround(FlightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.State.Player.Y <= session.State.Aircraft.AirspeedB + GroundSlack;
    }

    /// <summary>Whether the aeroplane is down, slow and in a landing zone right now.</summary>
    /// <param name="session">The host's session.</param>
    public static bool Landed(FlightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Aircraft aircraft = session.State.Aircraft;
        return aircraft.ActiveState != 0
            && OnGround(session)
            && aircraft.CurrentAirspeedFps <= StoppedFps
            && session.World.Zones is { } zones
            && zones.Contains(session.State.Player.X, session.State.Player.Z);
    }

    /// <summary>Ends the sortie now, with the outcome the caller decided (a host command).</summary>
    /// <param name="session">The host's session.</param>
    /// <param name="outcome">Which arm of <c>ui_post_death_message</c> to take.</param>
    public void ForceEnd(FlightSession session, MissionDebriefOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Ended && session.Mission is { } mission)
        {
            End(session, mission, outcome);
        }
    }

    private void End(FlightSession session, MissionSession mission, MissionDebriefOutcome outcome)
    {
        EndedAtSeconds = session.SimulatedSeconds;
        if (outcome == MissionDebriefOutcome.Killed)
        {
            // The draw is the port's, derived from the sortie's own length so a replayed flight
            // shows the same blurb (DeathBlurbDraw.FromSeed's labelled deviation).
            Debrief = MissionDebrief.ForDeath(
                _strings, DeathBlurbDraw.FromSeed((ulong)session.StepsRun));
            return;
        }

        Debrief = mission.Module is { } module
            ? MissionDebrief.ForSurvivor(module.GetDebriefText())
            : MissionDebrief.ForUnmodelled(mission.Combat.Mission.AssetName);
    }
}
