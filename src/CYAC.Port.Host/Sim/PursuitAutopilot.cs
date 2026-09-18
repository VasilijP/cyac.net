using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Session;

namespace CYAC.Port.Host.Sim;

/// <summary>
/// A HOST-SIDE test pilot: a crude bank-to-turn pursuit that flies the aeroplane at the nearest live
/// bandit and holds the trigger inside a firing cone.
/// </summary>
/// <remarks>
/// <para>
/// This is an INPUT SOURCE, not simulation: it writes only the two stick axes and the trigger — the
/// same three values a human's keyboard produces — and reads only positions the sim already
/// publishes.  It exists because a headless sortie cannot otherwise stay in a fight: a straight-line
/// run merges once and never comes back, which is exactly what H4 §5 A3 measured ("the bandits sit
/// 4,000–8,000 world units out and neither side closes, because nobody is flying the aeroplane").
/// </para>
/// <para>
/// The bearing and elevation come from the SIM's own
/// <see cref="CombatGeometry.Bearing2d(CombatPosition, CombatPosition)"/> /
/// <see cref="CombatGeometry.Elevation3d(CombatPosition, CombatPosition)"/>, so the BAM convention
/// and the sign are the game's, not a host guess.
/// </para>
/// </remarks>
public sealed class PursuitAutopilot
{
    /// <summary>The BAM circle: 2,880 units.</summary>
    public const int BamCircle = 2880;

    /// <summary>Aileron demand per BAM of bank-angle error.</summary>
    public const double BankGain = 0.6;

    /// <summary>Bank angle commanded per BAM of heading error.</summary>
    public const double BankCommandGain = 3.0;

    /// <summary>The steepest bank the test pilot commands: 480 BAM = 60°.</summary>
    public const int MaximumBankBam = 480;

    /// <summary>Pull demand per foot of altitude error.</summary>
    public const double AltitudeGain = 0.08;

    /// <summary>Damping on the climb rate, per foot per second.</summary>
    public const double ClimbDamping = 0.6;

    /// <summary>The stick's own limit — the in-flight calibration window (H2 §A6).</summary>
    public const int StickLimit = 95;

    /// <summary>The firing cone's half-angle, in BAM (120 = 15°).</summary>
    public const int FiringConeBam = 120;

    /// <summary>The range inside which the autopilot fires, in world units.</summary>
    public const int FiringRange = 6_000;

    /// <summary>Below this altitude the test pilot climbs whatever the target is doing.</summary>
    public const int GroundFloorFeet = 1_500;

    /// <summary>How many frames the autopilot has held the trigger.</summary>
    public long TriggerFrames { get; private set; }

    /// <summary>How many frames it had a target to chase.</summary>
    public long TrackingFrames { get; private set; }

    /// <summary>The range to the current target in world units, or −1.</summary>
    public int Range { get; private set; } = -1;

    /// <summary>How many times the pilot had to pick a new bandit (its previous one left the list).</summary>
    public long TargetSwitches { get; private set; }

    private ushort _held;

    /// <summary>Flies one frame.</summary>
    /// <param name="mission">The running mission.</param>
    /// <returns>The stick axes and trigger to apply, or null when there is nothing to chase.</returns>
    public (short X, short Y, bool Trigger)? Fly(MissionSession mission)
    {
        ArgumentNullException.ThrowIfNull(mission);

        CombatPosition player = new CombatPosition(
            mission.Flight.Player.X, mission.Flight.Player.Y, mission.Flight.Player.Z);

        // The engagement list is the live-bandit list; each node's +0x02 is its owning object.
        // NEAREST, re-picked every frame.  A "stick to one bandit until it dies" variant was tried
        // and measured WORSE (the held bandit spends most of the sortie behind the pilot, so the
        // firing cone never opens): 0 rounds fired in 6,000 frames against 693 for this one.
        ushort best = 0;
        long bestDistance = long.MaxValue;
        PoolArena arena = mission.Combat.Arena;
        foreach (ushort node in arena.WalkList(mission.Combat.Registers.ExpiryListHead))
        {
            ushort owner = arena.NodeOwnerObject(node);
            if (owner == 0 || !arena.Covers(owner, 0x18) || owner == mission.Combat.Registers.PlayerObjectRef)
            {
                continue;
            }


            CombatPosition at = new CombatObjectView(arena, owner).Position;
            long dx = (at.X - player.X) >> 8;
            long dz = (at.Z - player.Z) >> 8;
            long dy = (at.Y - player.Y) >> 8;
            long distance = (dx * dx) + (dy * dy) + (dz * dz);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = owner;
            }
        }

        if (best == 0)
        {
            Range = -1;
            _held = 0;
            return null;
        }

        if (best != _held)
        {
            _held = best;
            TargetSwitches++;
        }

        TrackingFrames++;
        CombatPosition target = new CombatObjectView(arena, best).Position;
        Range = (int)Math.Sqrt(bestDistance);

        int bearingError = Wrap(
            CombatGeometry.Bearing2d(player, target) - mission.Flight.Player.Heading.Units);
        // BANK-ANGLE hold, not a raw aileron demand: a raw demand barrel-rolls (measured — the
        // aeroplane ends up inverted and flies into the ground with the stick full back).  The roll
        // word is the world object's +0x16, folded to ±half a circle.
        int bank = Wrap(mission.Flight.Player.Roll.Units);
        int wanted = Math.Clamp((int)(bearingError * BankCommandGain), -MaximumBankBam, MaximumBankBam);
        short x = Clamp((wanted - bank) * BankGain);

        // The pitch law is deliberately NOT an angle law.  A BAM elevation error would need the
        // world object's own pitch sign convention, which is the opposite of the master's heading
        // accumulator (aircraft_pose_set negates the yaw and not the pitch, image@0x2A38D), and a
        // sign slip there flies the aeroplane into the ground — measured.  Altitude and vertical
        // speed are unambiguous, so the pilot holds an altitude with rate damping instead.
        int altitudeFeet = mission.Flight.Player.Y >> 8;
        int targetFeet = Math.Max(target.Y >> 8, GroundFloorFeet);
        double climbRate = mission.Flight.Window.VerticalSpeed / 256.0;
        double pull = ((targetFeet - altitudeFeet) * AltitudeGain)
            - (climbRate * ClimbDamping)
            + (Math.Abs(bearingError) * 0.35);
        if (altitudeFeet < GroundFloorFeet)
        {
            pull = Math.Max(pull, (GroundFloorFeet - altitudeFeet) * 0.2);
        }

        short y = Clamp(pull);
        bool trigger = Math.Abs(bearingError) <= FiringConeBam && Range <= FiringRange;
        if (trigger)
        {
            TriggerFrames++;
        }

        return (x, y, trigger);
    }

    /// <summary>Folds a BAM difference into <c>[−1440, 1440)</c>.</summary>
    /// <param name="units">The raw difference.</param>
    public static int Wrap(int units)
    {
        int value = ((units % BamCircle) + BamCircle) % BamCircle;
        return value >= BamCircle / 2 ? value - BamCircle : value;
    }

    private static short Clamp(double demand) =>
        (short)Math.Clamp(demand, -StickLimit, StickLimit);
}
