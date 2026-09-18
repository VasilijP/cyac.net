using System.Globalization;
using CYAC.Port.Core.Sim.Combat;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// The KILL-CROSSTALK census: two measurements of where a kill's debris and pilot end up.
/// </summary>
/// <remarks>
/// <para>
/// Symptom (a) was "right after I shot down an enemy, strange polygons appeared on MY plane — like
/// an opening parachute rendered at OUR coordinates", symptom (b) "enemies were firing at the place
/// where the shot-down plane crashed into the ground".  Both are questions about IDENTITY, so both
/// are answered by counting identities rather than by looking at pictures:
/// </para>
/// <list type="number">
///   <item><description>
///     every arm of the destruction pool (<c>slot_alloc_and_activate @image@0x2C4F6</c> →
///     <c>slot_ext_ptr_init_with_tag @image@0x2C447</c>) records the PARENT the original passes
///     (<c>[bp+0x10]</c>, pushed from <c>[0xED56]</c> at <c>image@0x08ACE</c>), the ext object's
///     resulting world position and the PLAYER's own — so "a chute at our coordinates" becomes a
///     number;
///   </description></item>
///   <item><description>
///     every frame, every live <c>s_combat_spawn_record</c> the AI owns is classified by what its
///     <c>+0x08</c> target reference points at — a live hostile, the player, or a DEPARTED object
///     (one <c>engagement_kill_finalize @image@0x0C36B</c> has re-classed to <c>crater</c>
///     <c>[0x52A2]</c> and stripped of its hostile bit, <c>and byte es:[si+3],0xca</c>
///     @<c>image@0x0C3E0</c>).
///   </description></item>
/// </list>
/// <para>
/// Instrumentation only: nothing here is read back by the kernel, and the host drives
/// <see cref="SampleShots"/> once per frame from outside <c>Sim/</c>.
/// </para>
/// </remarks>
public sealed class KillCrosstalkCensus
{
    /// <summary>The crater class record's DGROUP near offset — <c>[0x52A2]</c>.</summary>
    public const ushort CraterClassRef = Combat.CombatSpawnDepart.CraterClassRef;

    /// <summary>
    /// <c>WorldObjectFlags.Hostile</c> — bit 10, the bit <c>engagement_kill_finalize</c> clears.
    /// </summary>
    public const ushort HostileFlag = 0x0400;

    /// <summary><c>WorldObjectFlags.Active</c> — bit 0.</summary>
    public const ushort ActiveFlag = 0x0001;

    private readonly List<ExtArm> _arms = [];
    private readonly List<string> _shotLines = [];

    /// <summary>The host's frame counter, stamped onto each recorded event.</summary>
    public long Frame { get; set; }

    /// <summary>How near the player an armed ext object has to be to count as "on the player".</summary>
    /// <remarks>
    /// The acceptance number.  20 world units is the ext objects' own birth offset
    /// (<c>mov cx,0x14</c> @<c>image@0x2C47E</c>); an aircraft mesh is ~60 units across, so 300 is
    /// generous — an object that close to the eye IS "on my plane".
    /// </remarks>
    public const double PlayerProximityWorldUnits = 300.0;

    /// <summary>One <c>slot_ext_ptr_init_with_tag</c> arm.</summary>
    /// <param name="Frame">The host frame it happened on.</param>
    /// <param name="ExtObjectRef">The ext object (pilot or canopy).</param>
    /// <param name="ParentRef">The parent the allocator passed.</param>
    /// <param name="PlayerRef"><c>g_player_object [0x00C0]</c> at that instant.</param>
    /// <param name="ExtClassRef">The ext object's class record.</param>
    /// <param name="ParentClassRef">The parent's class record — <c>crater</c> once it is a wreck.</param>
    /// <param name="ExtX">The ext object's world X after placement.</param>
    /// <param name="ExtY">…Y.</param>
    /// <param name="ExtZ">…Z.</param>
    /// <param name="ParentX">The parent object's world X.</param>
    /// <param name="ParentY">…Y.</param>
    /// <param name="ParentZ">…Z.</param>
    /// <param name="PlayerX">The player object's world X.</param>
    /// <param name="PlayerY">…Y.</param>
    /// <param name="PlayerZ">…Z.</param>
    public readonly record struct ExtArm(
        long Frame,
        ushort ExtObjectRef,
        ushort ParentRef,
        ushort PlayerRef,
        ushort ExtClassRef,
        ushort ParentClassRef,
        double ExtX,
        double ExtY,
        double ExtZ,
        double ParentX,
        double ParentY,
        double ParentZ,
        double PlayerX,
        double PlayerY,
        double PlayerZ)
    {
        /// <summary>The distance from the ext object to the parent it was placed against.</summary>
        public double DistanceToParent
        {
            get
            {
                double dx = ExtX - ParentX;
                double dy = ExtY - ParentY;
                double dz = ExtZ - ParentZ;
                return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            }
        }

        /// <summary>True when the allocator's parent WAS the player's own aircraft.</summary>
        public bool ParentIsPlayer => ParentRef == PlayerRef;

        /// <summary>The distance from the ext object to the player, in world units.</summary>
        public double DistanceToPlayer
        {
            get
            {
                double dx = ExtX - PlayerX;
                double dy = ExtY - PlayerY;
                double dz = ExtZ - PlayerZ;
                return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            }
        }

        /// <summary>True when this arm put debris on top of a player who was not the victim.</summary>
        public bool IsCrosstalk =>
            !ParentIsPlayer && DistanceToPlayer < PlayerProximityWorldUnits;
    }

    /// <summary>Every ext-object arm this run saw.</summary>
    public IReadOnlyList<ExtArm> Arms => _arms;

    /// <summary>Arms whose parent was the player's own aircraft (the cheat key's shape).</summary>
    public int ArmsOnPlayerParent { get; private set; }

    /// <summary>Arms that dropped debris within <see cref="PlayerProximityWorldUnits"/> of a player who was not the
    /// victim.</summary>
    public int CrosstalkArms { get; private set; }

    /// <summary>Frames sampled by <see cref="SampleShots"/>.</summary>
    public long ShotFramesSampled { get; private set; }

    /// <summary>AI-owned live shot slots seen, summed over frames.</summary>
    public long AiShotSlotFrames { get; private set; }

    /// <summary>…of which aimed at the player.</summary>
    public long AiShotsAtPlayer { get; private set; }

    /// <summary>…of which aimed at a live hostile that is not the player.</summary>
    public long AiShotsAtOther { get; private set; }

    /// <summary>…of which aimed at a DEPARTED object (a wreck/crater or a de-hostiled hulk).</summary>
    public long AiShotsAtDeparted { get; private set; }

    /// <summary>…of which carried no target at all.</summary>
    public long AiShotsUntargeted { get; private set; }

    /// <summary>Live shot slot-frames the PLAYER owns.</summary>
    public long PlayerShotSlotFrames { get; private set; }

    /// <summary>AI shot slot-frames whose OWNER has already departed — a wreck still shooting.</summary>
    public long AiShotsFromDeparted { get; private set; }

    /// <summary>Up to sixteen sample lines describing shots involving departed objects.</summary>
    public IReadOnlyList<string> DepartedShotSamples => _shotLines;

    /// <summary>Records one <c>slot_ext_ptr_init_with_tag</c> arm.</summary>
    /// <param name="arm">The arm.</param>
    public void RecordExtArm(in ExtArm arm)
    {
        if (_arms.Count < 512)
        {
            _arms.Add(arm);
        }

        if (arm.ParentIsPlayer)
        {
            ArmsOnPlayerParent++;
        }

        if (arm.IsCrosstalk)
        {
            CrosstalkArms++;
        }
    }

    /// <summary>
    /// Classifies every live shot slot this frame by WHO fired it and what it is aimed at.
    /// </summary>
    /// <param name="registers">The combat register file (the spawn table and the player).</param>
    /// <param name="arena">The pool arena (the owner and target objects).</param>
    public void SampleShots(CombatRegisters registers, PoolArena arena)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        ShotFramesSampled++;
        ushort player = registers.PlayerObjectRef;
        for (int index = 0; index < CombatSpawnTable.SlotCount; index++)
        {
            int slot = CombatSpawnTable.TableDgroupOffset + (index * CombatSpawnTable.SlotBytes);
            if (registers.Word(slot) == 0)                       // image@0x02693 — free slot
            {
                continue;
            }

            ushort owner = registers.Word(slot + 0x06);
            ushort target = registers.Word(slot + 0x08);
            if (owner == player)
            {
                PlayerShotSlotFrames++;
                continue;                                        // the player's own rounds
            }

            AiShotSlotFrames++;

            // WHO fired it.  A DEPARTED owner is the crosstalk symptom: a wreck that keeps
            // shooting from the crash site.
            if (IsDeparted(arena, owner, out ushort ownerClass))
            {
                AiShotsFromDeparted++;
                Sample(
                    $"frame {Frame,7:N0}  slot {index,2}  DEPARTED OWNER {owner:X4} class {ownerClass:X4}"
                        + $" at {Describe(arena, owner)}  target {target:X4}");
            }

            // …and WHAT it is aimed at.
            if (target == 0 || target == EngagementFireResolver.NearMissRef)
            {
                AiShotsUntargeted++;
                continue;
            }

            if (target == player)
            {
                AiShotsAtPlayer++;
                continue;
            }

            if (IsDeparted(arena, target, out ushort targetClass))
            {
                AiShotsAtDeparted++;
                Sample(
                    $"frame {Frame,7:N0}  slot {index,2}  DEPARTED TARGET {target:X4} class {targetClass:X4}"
                        + $" at {Describe(arena, target)}  owner {owner:X4}");
            }
            else
            {
                AiShotsAtOther++;
            }
        }
    }

    /// <summary>
    /// True when an object has been through <c>engagement_kill_finalize @image@0x0C36B</c> — its
    /// class is <c>crater</c>, or it has lost its ACTIVE or HOSTILE bit
    /// (<c>and byte es:[si+3],0xca</c> @<c>image@0x0C3E0</c>).
    /// </summary>
    /// <param name="arena">The arena.</param>
    /// <param name="objectRef">The object.</param>
    /// <param name="classRef">Its class record, or 0 when it is not in the arena.</param>
    /// <returns>Whether it has departed.</returns>
    private static bool IsDeparted(PoolArena arena, ushort objectRef, out ushort classRef)
    {
        classRef = 0;
        if (objectRef == 0 || !arena.Covers(objectRef, 0x18))
        {
            return false;
        }

        CombatObjectView view = new CombatObjectView(arena, objectRef);
        classRef = view.ClassRef;
        ushort flags = view.Flags;
        return classRef == CraterClassRef
            || (flags & ActiveFlag) == 0
            || (flags & HostileFlag) == 0;
    }

    private static string Describe(PoolArena arena, ushort objectRef)
    {
        CombatPosition position = new CombatObjectView(arena, objectRef).Position;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"({position.X / 256.0,8:F0},{position.Y / 256.0,7:F0},{position.Z / 256.0,8:F0})");
    }

    private void Sample(string line)
    {
        if (_shotLines.Count < 16)
        {
            _shotLines.Add(line);
        }
    }

    /// <summary>Frames on which at least one destruction-slot ext object was live.</summary>
    public long SlotObjectFrames { get; private set; }

    /// <summary>…of which put a live ext object within <see cref="PlayerProximityWorldUnits"/> of the
    /// player.</summary>
    public long SlotObjectFramesNearPlayer { get; private set; }

    /// <summary>The closest a live ext object ever came to the player, in world units.</summary>
    public double ClosestExtObjectToPlayer { get; private set; } = double.PositiveInfinity;

    /// <summary>
    /// Walks the three <c>s_object_slot</c> records' nine ext objects and measures how near the
    /// player the LIVE ones are — the per-frame half of symptom (a), which the arm-time census
    /// cannot see because the pilot keeps falling for thousands of frames after it is armed.
    /// </summary>
    /// <param name="registers">The combat register file (the slot table and the player).</param>
    /// <param name="arena">The pool arena.</param>
    public void SampleSlotObjects(CombatRegisters registers, PoolArena arena)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        ushort player = registers.PlayerObjectRef;
        if (!arena.Covers(player, 0x18))
        {
            return;
        }

        CombatPosition playerPosition = new CombatObjectView(arena, player).Position;
        bool any = false;
        bool near = false;
        for (int slot = Combat.Lifecycle.ObjectSlotPool.FirstSlot;
             slot <= Combat.Lifecycle.ObjectSlotPool.LastSlot;
             slot += Combat.Lifecycle.ObjectSlotPool.SlotBytes)
        {
            for (int at = 0x04; at <= 0x08; at += 2)
            {
                ushort objectRef = registers.Word(slot + at);
                if (objectRef == 0 || !arena.Covers(objectRef, 0x18))
                {
                    continue;
                }

                CombatObjectView view = new CombatObjectView(arena, objectRef);
                if ((view.Flags & ActiveFlag) == 0)
                {
                    continue;
                }

                any = true;
                CombatPosition position = view.Position;
                double dx = (position.X - playerPosition.X) / 256.0;
                double dy = (position.Y - playerPosition.Y) / 256.0;
                double dz = (position.Z - playerPosition.Z) / 256.0;
                double distance = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
                if (distance < ClosestExtObjectToPlayer)
                {
                    ClosestExtObjectToPlayer = distance;
                }

                if (distance < PlayerProximityWorldUnits)
                {
                    near = true;
                    Sample(string.Create(
                        CultureInfo.InvariantCulture,
                        $"frame {Frame,7:N0}  EXT {objectRef:X4} class {view.ClassRef:X4} "
                            + $"{Describe(arena, objectRef)} is {distance:F0} units from the player"));
                }
            }
        }

        if (any)
        {
            SlotObjectFrames++;
        }

        if (near)
        {
            SlotObjectFramesNearPlayer++;
        }
    }

    /// <summary>The one-line verdict a headless run prints.</summary>
    /// <returns>The summary.</returns>
    public string Summary() => string.Create(
        CultureInfo.InvariantCulture,
        $"CROSSTALK ext-arms {_arms.Count} (player-parent {ArmsOnPlayerParent}, "
            + $"near-player crosstalk {CrosstalkArms})  |  AI shot slot-frames {AiShotSlotFrames:N0} "
            + $"(player {AiShotsAtPlayer:N0}, other {AiShotsAtOther:N0}, "
            + $"at-departed {AiShotsAtDeparted:N0}, from-departed {AiShotsFromDeparted:N0}, "
            + $"untargeted {AiShotsUntargeted:N0}); player slot-frames {PlayerShotSlotFrames:N0} "
            + $"over {ShotFramesSampled:N0} frame(s)  |  ext-object frames {SlotObjectFrames:N0} "
            + $"(near-player {SlotObjectFramesNearPlayer:N0}, closest "
            + $"{(double.IsInfinity(ClosestExtObjectToPlayer) ? "n/a" : ClosestExtObjectToPlayer.ToString("F0", CultureInfo.InvariantCulture))})");

    /// <summary>The per-arm detail lines.</summary>
    /// <returns>One line per recorded arm.</returns>
    public IEnumerable<string> ArmLines()
    {
        foreach (ExtArm arm in _arms)
        {
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"frame {arm.Frame,7:N0}  ext {arm.ExtObjectRef:X4}/{arm.ExtClassRef:X4} "
                    + $"parent {arm.ParentRef:X4}/{arm.ParentClassRef:X4} "
                    + $"player {arm.PlayerRef:X4}{(arm.ParentIsPlayer ? " ★PLAYER-PARENT" : string.Empty)}  "
                    + $"ext at ({arm.ExtX,8:F0},{arm.ExtY,7:F0},{arm.ExtZ,8:F0})  "
                    + $"parent at ({arm.ParentX,8:F0},{arm.ParentY,7:F0},{arm.ParentZ,8:F0}) d {arm.DistanceToParent,6:F0}  "
                    + $"player at ({arm.PlayerX,8:F0},{arm.PlayerY,7:F0},{arm.PlayerZ,8:F0})  "
                    + $"d {arm.DistanceToPlayer,9:F0}{(arm.IsCrosstalk ? "  ★CROSSTALK" : string.Empty)}");
        }
    }
}
