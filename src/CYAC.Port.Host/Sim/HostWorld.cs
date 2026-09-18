using CYAC.Port.Core.Sim.Flight;

namespace CYAC.Port.Host.Sim;

/// <summary>
/// The host's <see cref="IKernelWorld"/> — where the flight kernel's three outward notifications go
/// while the PoC has no HUD, no advisor voice and no landing-zone table.
/// </summary>
/// <remarks>
/// <para>
/// <c>hud_warning_post</c> and <c>flight_advisor_dispatch</c> are counted and their message ids kept,
/// so the readout can show that the kernel IS talking (a stall warning during a scripted pull is the
/// cheapest proof the scaffold is alive).  Drawing them is the cockpit-HUD step, which is deferred.
/// </para>
/// <para>
/// <see cref="IsWithinLandingZone"/> is the one INPUT of the three. The cold start loads the theater,
/// and its type-2 / type-6 sites ARE that table, so <see cref="Zones"/> now answers the query for
/// real through <see cref="LandingZoneTable"/>.  A false answer was not neutral: it made every ground
/// contact fatal, and a Test Flight parked ON an airstrip died inside a second.
/// <see cref="LandingZoneAnswer"/> still overrides it, which is how the trace replay feeds the
/// genuine machine's own verdict exactly as <c>FlightKernelClosedLoopTests.CountingWorld</c> does.
/// </para>
/// </remarks>
public sealed class HostWorld : IKernelWorld
{
    /// <summary>How many HUD warnings the kernel posted.</summary>
    public long HudPosts { get; private set; }

    /// <summary>How many flight-advisor dispatches the kernel made.</summary>
    public long Advisories { get; private set; }

    /// <summary>How many times the landing-zone seam was consulted.</summary>
    public long LandingZoneQueries { get; private set; }

    /// <summary>The most recent HUD message id, or -1.</summary>
    public int LastHudMessageId { get; private set; } = -1;

    /// <summary>The most recent advisory class, or -1.</summary>
    public int LastAdvisoryClass { get; private set; } = -1;

    /// <summary>
    /// Where the flight kernel's advisories now GO.
    /// </summary>
    /// <remarks>
    /// <c>image@0x2B44C</c> is an <c>lcall</c> to <c>ai_advisor_repeated_dispatch @image@0x0F35B</c>,
    /// not to the message dispatcher itself, so the three classes the control-integration stage
    /// raises (15 from the pull-up arm, 16 the overspeed advisory and 17 the low-speed advisory, the
    /// kernel's <c>PullUp</c>/<c>Overspeed</c>/<c>LowSpeedAdvisoryClass</c>) come through the
    /// RATE-LIMITED door.  Presentation-only: the queue never writes back, so the recorded replays
    /// do not see it.  Null in every headless run that does not draw.
    /// </remarks>
    public AdvisorDispatch? Advisor { get; set; }

    /// <summary>
    /// Forces <see cref="IsWithinLandingZone"/>'s answer, overriding <see cref="Zones"/>.  The trace
    /// replay sets it per frame from the genuine machine's own S7 record.
    /// </summary>
    public bool? LandingZoneAnswer { get; set; }

    /// <summary>The theater's landing-zone table, or null when the host did not load one.</summary>
    public LandingZoneTable? Zones { get; set; }

    /// <summary>Where the query reads the player from — set once by the session.</summary>
    public FlightKernelState? State { get; set; }

    /// <inheritdoc/>
    public void PostHudWarning(int messageId, int messageTable)
    {
        HudPosts++;
        LastHudMessageId = messageId;
    }

    /// <inheritdoc/>
    public void DispatchFlightAdvisor(byte advisoryClass)
    {
        Advisories++;
        LastAdvisoryClass = advisoryClass;
        Advisor?.Raise(advisoryClass, AdvisorDispatchKind.RateLimited);   // image@0x2B44C
    }

    /// <inheritdoc/>
    public bool IsWithinLandingZone()
    {
        LandingZoneQueries++;
        if (LandingZoneAnswer is bool forced)
        {
            return forced;
        }

        return Zones is not null
            && State is not null
            && Zones.Contains(State.Player.X, State.Player.Z);
    }
}
