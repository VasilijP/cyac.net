using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Flight.ColdStart;

/// <summary>
/// The pose and the two seeds a container's class-0 PLAYER record hands the flight engine — the
/// four values <c>wld_or_s_asset_parser</c>'s player branch latches into DGROUP.
/// </summary>
/// <remarks>
/// <para>
/// The branch is <c>image@0x0A084..0x0A0B9</c>, taken when the object's class-table lookup returned
/// the <c>-1</c> player sentinel (<c>image@0x09A44</c>):
/// </para>
/// <code>
/// image@0x0A08A  [0xEE34] g_player_spawn_pos      &lt;- 12 B, the resolved XYZ i32 triple
/// image@0x0A0A7  [0xEE4C] g_player_init_euler_seed &lt;- 6 B, yaw/pitch/roll in 1/8-degree BAM
/// image@0x0A0B2  [0xEE52] g_player_init_speed_seed &lt;- attr 0x98 (initial_speed)
/// image@0x0A0B9  [0xEE56] g_player_init_timer_seed &lt;- attr 0x9C (player_timer_seed)
/// </code>
/// <para>
/// <c>scenario_load_dispatch</c> phase 2 then copies the first two straight into the 0x18-byte spawn
/// record it hands <c>spawn_dispatch_object @0x06F33</c> (<c>image@0x093C8</c> / <c>image@0x093DE</c>),
/// so they become the player world object's <c>+0x06/+0x0A/+0x0E</c> position and
/// <c>+0x12/+0x14/+0x16</c> attitude; <c>active_aircraft_load_and_state_reset</c> consumes the two
/// seeds (<c>image@0x22525..0x2255A</c>).
/// </para>
/// <para>
/// <b>Units.</b>  <see cref="X"/>/<see cref="Y"/>/<see cref="Z"/> are WORLD units as the container
/// authors them; the world object stores them <c>&lt;&lt; 8</c>
/// (<see cref="FlightColdStart.WorldToObjectShift"/>).
/// </para>
/// </remarks>
/// <param name="X">World X of the spawn.</param>
/// <param name="Y">World Y (up) of the spawn; an <c>at_site</c> anchor puts it on the ground plane.</param>
/// <param name="Z">World Z of the spawn.</param>
/// <param name="Heading">Initial yaw (<c>[0xEE4C]</c>).</param>
/// <param name="Pitch">Initial pitch (<c>[0xEE4E]</c>).</param>
/// <param name="Roll">Initial roll (<c>[0xEE50]</c>).</param>
/// <param name="InitialSpeedSeed">
/// <c>[0xEE52]</c> — feet per second.  <c>active_aircraft_load_and_state_reset</c> writes
/// <c>master[+0x00..0x03] = seed &lt;&lt; 8</c> unless the seed is <c>-1</c>
/// (<c>cmp word [0xEE52],-1 ; je</c> @<c>image@0x22525</c>).
/// </param>
/// <param name="TimerSeed">
/// <c>[0xEE56]</c> — <c>&lt;&lt; 8</c> into the timer pair <c>[0xF034..0xF03B]</c>
/// (<c>image@0x22545..0x2255A</c>).  Not flight-kernel state; carried so a host can install it.
/// </param>
public readonly record struct FlightSpawn(
    int X,
    int Y,
    int Z,
    Angle Heading,
    Angle Pitch,
    Angle Roll,
    short InitialSpeedSeed,
    short TimerSeed)
{
    /// <summary>The <see cref="InitialSpeedSeed"/> / <see cref="TimerSeed"/> value meaning "leave it": −1.</summary>
    public const short NoSeed = -1;

    /// <summary>The world object's X, in the object's own <c>&lt;&lt; 8</c> units.</summary>
    public int ObjectX => X << FlightColdStart.WorldToObjectShift;

    /// <summary>The world object's Y, in the object's own <c>&lt;&lt; 8</c> units.</summary>
    public int ObjectY => Y << FlightColdStart.WorldToObjectShift;

    /// <summary>The world object's Z, in the object's own <c>&lt;&lt; 8</c> units.</summary>
    public int ObjectZ => Z << FlightColdStart.WorldToObjectShift;

    /// <summary>
    /// Reads the spawn a container's class-0 object authors, with its <c>at_site</c> anchor drawn.
    /// </summary>
    /// <param name="mission">The container (<c>FREE.S</c> for a Test Flight).</param>
    /// <param name="anchors">Which theater site each of the container's anchors drew.</param>
    /// <exception cref="InvalidOperationException">
    /// The container has no class-0 object, or its placement chain does not resolve to a point.
    /// </exception>
    public static FlightSpawn FromMission(MissionDefinition mission, MissionSpawnAnchors anchors)
    {
        ArgumentNullException.ThrowIfNull(mission);
        ArgumentNullException.ThrowIfNull(anchors);

        MissionObject player = mission.PlayerStart
                               ?? throw new InvalidOperationException(
                                   $"{mission.AssetName} has no class-0 PLAYER object, so nothing writes " +
                                   "g_player_spawn_pos [0xEE34] (image@0x0A08A) and the spawn would stay at the origin.");

        MissionPosition resolved = player.Placement.Resolved
                                   ?? throw new InvalidOperationException(
                                       $"{mission.AssetName}'s player placement ({player.Placement.Kind}) does not resolve " +
                                       "to a static point.");

        MissionPosition world = anchors.ToWorld(resolved);
        IReadOnlyList<int>? aux = player.AuxVector;

        return new FlightSpawn(
            world.X,
            world.Y,
            world.Z,
            player.Heading ?? (aux is { Count: > 0 } ? Angle.FromUnits(aux[0]) : default),
            aux is { Count: > 1 } ? Angle.FromUnits(aux[1]) : default,
            aux is { Count: > 2 } ? Angle.FromUnits(aux[2]) : default,
            player.InitialSpeed is int speed ? unchecked((short)speed) : NoSeed,
            player.PlayerTimerSeed is int timer ? unchecked((short)timer) : NoSeed);
    }
}
