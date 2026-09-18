using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Grid;
using CYAC.Port.Core.Sim.Combat.Lifecycle;
using CYAC.Port.Core.Sim.Combat.Player;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// PER-ROUND GUNNERY: the grey ordinary rounds that trail every verified <c>bullet</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the original does.</b> <c>weapon_fire_event_scheduler @image@0x03514</c> fires once per 64-tick window and
/// <c>weapon_fire_check_and_spawn @image@0x03432</c> spawns ONE <c>bullet</c> object per burst — a single 64-foot
/// line record in palette 32 — while the ammunition counter drops by the weapon class's <c>+0x2C</c>
/// <c>ammoPerShot</c> (5 for the .50s and most cannon, 2 for the two heavy cannon, 10 for the four fast machine guns;
/// <c>exe/weapons.json</c>, H21c). Nothing in the image models an individual round: a drawn tracer IS a burst.
/// </para>
/// <para>
/// <b>What the port adds:</b> the weapon table's <c>ammoPerShot</c> IS the rounds-per-burst;
/// the verified <c>bullet</c> object stays the sole DAMAGE authority; the <c>N − 1</c> extra grey rounds trail it at
/// the cyclic-rate spacing, carry no halo, DO run a hit test and show a hit EFFECT, but deal no damage "for now".
/// </para>
/// <para>
/// <b>Presentation only, by construction.</b>  This type READS the combat register file and the
/// pool arena and WRITES nothing into either: the rounds live in this object, the impacts live in
/// this object, and the host draws both as <see cref="SceneInstance"/>s beside the pool's own.
/// <see cref="Step"/> runs after <see cref="MissionSession.Step"/> and is never called on a
/// verifying path (<c>--replay</c> runs <c>TraceReplay</c>, which has no host session), so every
/// verified replay stays bit-exact whether or not it exists.
/// </para>
/// <para>
/// <b>The cyclic law.</b>  A burst window is 64 frame-time ticks
/// (<see cref="BurstWindowTicks"/>) and pays <c>N</c> rounds, so the rounds of a belt leave the gun
/// <c>64 / N</c> ticks apart — 12.8 ticks for <c>N = 5</c>, which at the P-51's 4,400 ft/s
/// (<c>+0x14</c>, Q8 feet per 256 ticks) is 220 feet between rounds.  Round <c>i</c> of a burst
/// (<c>i = 1 … N − 1</c>) is born <c>i · 64 / N</c> ticks after the tracer, at the SHOOTER's position
/// then plus the tracer's own muzzle offset, and flies the muzzle direction AT ITS OWN BIRTH (the
/// live owner's heading/elevation then, so a burst fired through a turn fans into a curved stream)
/// at the tracer's speed through the verified integrator <see cref="CombatGeometry.AccumulateDistance3d"/>.  With the trigger held the belt is
/// continuous: tracer, N − 1 grey, tracer, … at one spacing, which is a 1-in-N tracer belt.
/// </para>
/// <para>
/// <b>Detecting a burst.</b>  The projectile row (CS0 → CS1, <c>image@0x00CC9</c>) ticks live shots
/// BEFORE the fire stage (CS3 → CS4, <c>image@0x00D98</c>) spawns new ones, so on the frame a shot
/// is spawned its pool object still sits at the muzzle.  Each spawn-table slot is shadowed by a
/// "ghost" that the same integrator advances once per frame; a slot whose pool object is not where
/// the ghost says — or that went idle → active, or changed owner or class — has a NEW shot in it.
/// The ghost is exact for every gun class because their envelope is off (<c>+0x1E = 0</c>); guided
/// classes pay one round and get no trail, so their envelope never matters here.
/// </para>
/// <para>
/// <b>The hit test — H29: the KERNEL's box, not a mesh sphere.</b> A grey round is tested against every live pool
/// object that carries an engagement block (the same admissibility the ported resolver demands at
/// <c>image@0x0BDBA</c>), excluding its own shooter and — for an AI shooter — its own side (the team-tag XOR of
/// <c>image@0x0299B</c>), by a segment-versus-AABB SLAB test on <b>exactly the volume the integer kernel scores
/// against</b>:
/// </para>
/// <list type="number">
///   <item>
///     the candidate's own <b>class-record box</b> <c>+0x30..+0x47</c>
///     (<see cref="WorldObjectClassRecord.BoxXMinOffset"/> …), Q8 feet, read through the same
///     <see cref="ICombatStaticData"/> surface;
///   </item>
///   <item>
///     inflated by the <b>weapon window</b> <c>HalfRange = (weapon +0x20) &lt;&lt; 8</c>, with the
///     same <c>+0x14</c> (AI shot) / <c>+0x96</c> (player shot, cheat <c>[0xE46D]</c> on) biases the
///     projectile row adds at <c>image@0x02905..0x0291B</c> — 0 feet for every gun class with cheats
///     off, so the grey round's volume IS the kernel's;
///   </item>
///   <item>
///     with the kernel's <b>four-cardinal orientation law</b> (<c>image@0x28A20..0x28AC6</c>): the
///     object-relative segment is swapped/negated for a <c>+0x12</c> yaw word of exactly
///     <c>0x2D0</c> / <c>0x5A0</c> / <c>0x870</c>, and for ANY other value (and for a
///     <see cref="WorldObjectFlags.NoOrientation"/> object) no transform is applied at all;
///   </item>
///   <item>
///     with the kernel's <c>i16</c> object-space gate (<c>image@0x28992..0x28A1A</c>): a candidate
///     whose relative segment does not fit in a signed word is rejected outright;
///   </item>
///   <item>
///     planes computed exactly as <c>image@0x28AC9..0x28B3B</c> does it —
///     <c>(field ± HalfRange) &gt;&gt; 8</c>, an arithmetic shift into feet.
///   </item>
/// </list>
/// <para>
/// D1 §2.2 measured the mesh sphere at 2.3–3.7× the kernel box by volume and 3.1–4.8× in the vertical: a round
/// passing 50 ft over a Focke-Wulf whose box is only ±25.5 ft tall lit a flash the kernel would never score.  D1
/// §4.1's <c>--pursue</c> experiment showed 26 air flashes against ZERO kernel damage. <see cref="HitBoxScale"/>
/// (<c>--round-hit-scale</c>, default 1.0 = the kernel's exact volume) is a PROPORTIONAL scale on the box about its
/// own centre.
/// </para>
/// <para>
/// What is deliberately NOT reproduced (this is a presentation proxy, not the kernel): the world-grid
/// cell walk and its first-match selection order — the trail takes the NEAREST admissible box along
/// the segment — the <c>+0x10</c> selection-window gate frame (immaterial: it only zeroes a window
/// that is already 0 for every gun class), Cohen–Sutherland's two shipped quirks (a slab test has no
/// iteration counter and no K-block), and the cheap <c>+0x08</c> half-extent dual-AABB pre-reject
/// (an accelerator, not a rule).
/// </para>
/// <para>
/// A hit retires the round and lights an <see cref="Impact"/> the host draws through the same
/// <c>explosio</c> anchor the deferred-effect pool uses (<c>deferred_effect_render @image@0x03E18</c>,
/// fork 0 — the growing disc and its eight shards), aged on the same <c>0x100</c>-tick clock.  A
/// round that reaches the ground plane lights a smaller one.
/// </para>
/// </remarks>
public sealed class GunneryRounds
{
    /// <summary>
    /// The fire scheduler's burst window: 64 frame-time ticks
    /// (<c>weapon_fire_event_scheduler @image@0x03514</c>; H17 §3).
    /// </summary>
    public const int BurstWindowTicks = 64;

    /// <summary>
    /// One master frame in frame-time ticks: <c>0x100</c> (<c>SceneFrameTimer</c>, the frame
    /// counter is <c>frameTime &gt;&gt; 8</c>) — the unit a weapon class's <c>+0x1F</c> lifetime is
    /// stated in, and the life of a flash (<c>DeferredEffectPool.FlashFrameTime</c>).
    /// </summary>
    public const int TicksPerMasterFrame = 0x100;

    /// <summary>The ground-strike flash draws at this fraction of the air-hit flash's size.</summary>
    public const double GroundFlashScale = 0.5;

    /// <summary>The largest number of live rounds kept; older ones expire first.</summary>
    public const int MaxLiveRounds = 512;

    private readonly SlotShadow[] _slots = new SlotShadow[CombatSpawn.SlotCount];
    private readonly List<Burst> _bursts = [];
    private readonly List<Round> _rounds = [];
    private readonly List<Impact> _impacts = [];
    private readonly List<Candidate> _candidates = [];
    private ushort _playerRef;
    private byte _sequence;
    private int _burstCounter;

    /// <summary>Whether the trail is produced at all; off leaves the tracer alone (the H17 look).</summary>
    /// <remarks>
    /// <b>settable while the sortie is flying</b> (the <c>--rounds</c> row of the port settings dialog).
    /// OFF stops the trail BEARING: the slot shadows go and the bursts still owing rounds are dropped, so
    /// nothing new is born — but every round already in the air keeps flying, keeps being hit-tested and
    /// expires on its own class lifetime, and the flashes already burning finish burning ("let the live
    /// ones expire").  ON resumes with the next burst the ported spawn table opens.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The spacing override in world units (feet) between consecutive rounds of a belt; 0 (the
    /// default) takes the cyclic law <c>speed · 64 / N</c>.
    /// </summary>
    /// <remarks>
    /// <b>settable while the sortie is flying</b> (<c>--round-spacing</c>).  It is read when a BURST
    /// is opened, so a burst already bearing keeps the spacing it opened with and the next burst
    /// takes the new one.  Rounds already in the air are not moved: their spacing is history,
    /// written in world positions.
    /// </remarks>
    public double SpacingWorldUnits { get; set; }

    /// <summary>
    /// A PROPORTIONAL scale on every hit box about its own centre; 1.0 (the default) is the
    /// verified kernel's own volume, to the foot (<c>--round-hit-scale</c>).  0.5 halves every
    /// half-extent.  Presentation only: it moves flashes, never damage.
    /// </summary>
    /// <remarks>
    /// <b>settable while the sortie is flying</b>.  It is read at the moment of each hit test
    /// (<see cref="TryHit"/>), so a round in flight is tested with the scale in force when the test
    /// happens — the rule — and the change is live on the very next frame.
    /// </remarks>
    public double HitBoxScale { get; set; } = 1.0;

    /// <summary>Whether a round striking the ground plane lights a (smaller) flash.</summary>
    public bool GroundHits { get; init; } = true;

    /// <summary>The live grey rounds, in birth order.</summary>
    public IReadOnlyList<Round> Rounds => _rounds;

    /// <summary>The impact flashes still burning.</summary>
    public IReadOnlyList<Impact> Impacts => _impacts;

    /// <summary>The bursts still bearing rounds.</summary>
    public int PendingBursts => _bursts.Count;

    /// <summary>The ids (<see cref="Round.BurstId"/>) of the bursts still bearing rounds.</summary>
    public IEnumerable<int> PendingBurstIds => _bursts.Select(b => b.Id);

    /// <summary>The running census.</summary>
    public GunneryCensus Census { get; } = new();

    /// <summary>One live grey round.</summary>
    /// <param name="Position">Its position, sim units (world × 256).</param>
    /// <param name="Heading">The tracer's heading word it flies.</param>
    /// <param name="Elevation">The tracer's elevation word.</param>
    /// <param name="SpeedQ8">The tracer's Q8 speed.</param>
    /// <param name="ExpiryTick">The frame-time tick it expires at.</param>
    /// <param name="Owner">The shooter's pool object.</param>
    /// <param name="WeaponClassRef">The weapon class's DGROUP offset.</param>
    /// <param name="BurstIndex">Which round of its burst it is, 1 … N − 1.</param>
    /// <param name="BurstId">The burst it belongs to: a running count of trailing bursts.</param>
    public readonly record struct Round(
        CombatPosition Position,
        short Heading,
        short Elevation,
        int SpeedQ8,
        long ExpiryTick,
        ushort Owner,
        ushort WeaponClassRef,
        int BurstIndex,
        int BurstId)
    {
        /// <summary>World X, world units.</summary>
        public double X => Position.X / 256.0;

        /// <summary>World Y (up), world units.</summary>
        public double Y => Position.Y / 256.0;

        /// <summary>World Z, world units.</summary>
        public double Z => Position.Z / 256.0;

        /// <summary>The heading in degrees, the renderer's convention (<see cref="CombatSceneObjects.DegreesPerUnit"/>).</summary>
        public double HeadingDegrees => unchecked((ushort)Heading) * CombatSceneObjects.DegreesPerUnit;

        /// <summary>The pitch in degrees.</summary>
        public double PitchDegrees => unchecked((ushort)Elevation) * CombatSceneObjects.DegreesPerUnit;
    }

    /// <summary>One impact flash.</summary>
    /// <param name="X">World X, world units.</param>
    /// <param name="Y">World Y.</param>
    /// <param name="Z">World Z.</param>
    /// <param name="BornTick">The frame-time tick it was lit at.</param>
    /// <param name="Sequence">The orientation seed the debris ring turns by (the record's <c>+0x0A</c>).</param>
    /// <param name="Ground">True for a ground strike, which draws smaller.</param>
    /// <param name="Victim">The struck pool object, 0 for the ground.</param>
    public readonly record struct Impact(
        double X, double Y, double Z, long BornTick, byte Sequence, bool Ground, ushort Victim)
    {
        /// <summary>The flash's age on the effect clock, in frame-time ticks.</summary>
        /// <param name="nowTick">The current frame time.</param>
        public int AgeAt(long nowTick) => (int)Math.Clamp(nowTick - BornTick, 0, TicksPerMasterFrame);

        /// <summary>Whether the flash has burnt out.</summary>
        /// <param name="nowTick">The current frame time.</param>
        public bool ExpiredAt(long nowTick) => nowTick - BornTick >= TicksPerMasterFrame;
    }

    /// <summary>A burst still bearing rounds.</summary>
    private sealed class Burst
    {
        public int Id;
        public ushort Owner;
        public ushort WeaponClassRef;
        public short Heading;
        public short Elevation;
        public int SpeedQ8;
        public CombatPosition MuzzleOffset;
        public CombatPosition LastOwnerPosition;
        public int Remaining;
        public int Total;
        public double NextBirthTick;
        public double IntervalTicks;
        public int LifetimeTicks;
    }

    /// <summary>What a spawn slot looked like last frame.</summary>
    private struct SlotShadow
    {
        public bool Active;
        public ushort Owner;
        public ushort WeaponClassRef;
        public ushort PoolObject;
        public CombatPosition Ghost;
    }

    /// <summary>
    /// A hit candidate, read once per frame — everything the kernel's own clip test reads about it.
    /// </summary>
    /// <param name="ObjectRef">The pool near offset (the original's <c>BX</c>).</param>
    /// <param name="ClassRef">Its class record's DGROUP near offset (<c>+0x00</c>).</param>
    /// <param name="ScaledX">
    /// Its world X as the clip reads it: <c>(i32)position &gt;&gt; 8</c>, feet
    /// (<c>image@0x28950..0x2898F</c>).
    /// </param>
    /// <param name="ScaledY">Its world Y, same scaling.</param>
    /// <param name="ScaledZ">Its world Z, same scaling.</param>
    /// <param name="RotCode">
    /// The orientation arm the clip takes: <c>0x2D0</c> / <c>0x5A0</c> / <c>0x870</c>, or 0 for
    /// "no transform at all" — which is what the clip's DEFAULT arm does for every other yaw, and
    /// what a <see cref="WorldObjectFlags.NoOrientation"/> object gets (<c>image@0x28A20</c>).
    /// </param>
    /// <param name="TeamBit">Its engagement block's <c>+0x05</c> bit 6, the team tag.</param>
    /// <param name="IsPlayer">Whether it is the player's own object.</param>
    private readonly record struct Candidate(
        ushort ObjectRef,
        ushort ClassRef,
        int ScaledX,
        int ScaledY,
        int ScaledZ,
        ushort RotCode,
        bool TeamBit,
        bool IsPlayer);

    /// <summary>
    /// The six planes of a candidate's hit box, in FEET and object-local: exactly what the
    /// verified clip test computes at <c>image@0x28AC9..0x28B3B</c> (<c>plane =
    /// (classRecord[+0x30..+0x47] ± HalfRange) &gt;&gt; 8</c>).
    /// </summary>
    /// <param name="XLow">The <c>-X</c> plane.</param>
    /// <param name="XHigh">The <c>+X</c> plane.</param>
    /// <param name="YLow">The <c>-Y</c> plane.</param>
    /// <param name="YHigh">The <c>+Y</c> plane.</param>
    /// <param name="ZLow">The <c>-Z</c> plane.</param>
    /// <param name="ZHigh">The <c>+Z</c> plane.</param>
    public readonly record struct HitBox(
        double XLow, double XHigh, double YLow, double YHigh, double ZLow, double ZHigh)
    {
        /// <summary>
        /// The box the kernel would clip against for one class record and one weapon window.
        /// </summary>
        /// <param name="staticData">The DGROUP surface the class records live in.</param>
        /// <param name="classRef">The class record's DGROUP near offset.</param>
        /// <param name="halfRange">
        /// The query's <c>HalfRange</c> — <c>(weapon +0x20) &lt;&lt; 8</c> plus the projectile row's
        /// own <c>+0x14</c>/<c>+0x96</c> bias, in Q8 feet (<c>image@0x0291D</c>).
        /// </param>
        /// <returns>The six planes, in feet.</returns>
        public static HitBox FromClassRecord(ICombatStaticData staticData, ushort classRef, int halfRange) =>
            new(
                Plane(staticData, classRef, WorldObjectClassRecord.BoxXMinOffset, -halfRange),
                Plane(staticData, classRef, WorldObjectClassRecord.BoxXMaxOffset, halfRange),
                Plane(staticData, classRef, WorldObjectClassRecord.BoxYMinOffset, -halfRange),
                Plane(staticData, classRef, WorldObjectClassRecord.BoxYMaxOffset, halfRange),
                Plane(staticData, classRef, WorldObjectClassRecord.BoxZMinOffset, -halfRange),
                Plane(staticData, classRef, WorldObjectClassRecord.BoxZMaxOffset, halfRange));

        /// <summary>
        /// This box scaled PROPORTIONALLY about its own centre — <c>--round-hit-scale</c>.  At 1.0 it
        /// is this box exactly; at 0.5 every half-extent is halved.
        /// </summary>
        /// <param name="scale">The multiplier; negatives are clamped to 0.</param>
        /// <returns>The scaled box.</returns>
        public HitBox Scaled(double scale)
        {
            if (scale == 1.0)
            {
                return this;
            }

            double s = Math.Max(0.0, scale);
            (double xLow, double xHigh) = Axis(XLow, XHigh, s);
            (double yLow, double yHigh) = Axis(YLow, YHigh, s);
            (double zLow, double zHigh) = Axis(ZLow, ZHigh, s);
            return new HitBox(xLow, xHigh, yLow, yHigh, zLow, zHigh);

            static (double Low, double High) Axis(double low, double high, double s)
            {
                double centre = (low + high) / 2.0;
                double half = (high - low) / 2.0 * s;
                return (centre - half, centre + half);
            }
        }

        /// <summary>The half-extents (X, Y, Z), in feet.</summary>
        public (double X, double Y, double Z) HalfExtents =>
            ((XHigh - XLow) / 2.0, (YHigh - YLow) / 2.0, (ZHigh - ZLow) / 2.0);

        /// <summary>
        /// One plane constant, <c>(field ± adjust) &gt;&gt; 8</c> as a signed word — the clip's own
        /// arithmetic, truncating toward −∞ exactly as <c>SAR</c> does.
        /// </summary>
        private static short Plane(ICombatStaticData staticData, ushort classRef, int fieldOffset, int adjust)
        {
            int v = WorldObjectClassRecord.Int32(staticData, classRef, fieldOffset);
            return unchecked((short)(unchecked(v + adjust) >> 8));
        }
    }

    /// <summary>Advances the trail by one frame.  Call AFTER <see cref="MissionSession.Step"/>.</summary>
    /// <param name="mission">The mission whose combat state is read.</param>
    public void Step(MissionSession mission)
    {
        ArgumentNullException.ThrowIfNull(mission);
        CombatRegisters registers = mission.Combat.Registers;
        PoolArena arena = mission.Combat.Arena;
        SessionCombatStaticData statics = mission.Combat.StaticData;
        int dt = unchecked((short)registers.Word(MissionSession.SceneFrameDt));
        long now = FrameTime(registers);

        // A mid-sortie `--rounds off` stops the trail BEARING and nothing else: the bursts still
        // owing rounds are dropped (a scheduled round is not a live one) and the slot shadows go,
        // so re-enabling starts clean; the rounds and flashes already alive fly, hit and expire
        // below.  With the trail off from cold start every list is empty and these are no-ops, so
        // `--rounds off` is unchanged.
        if (Enabled)
        {
            DetectBursts(registers, arena, statics, now);
            BearRounds(arena, now);
        }
        else
        {
            _bursts.Clear();
            Array.Clear(_slots);
        }

        FlyRounds(registers, arena, statics, dt, now);
        RetireImpacts(now);
    }

    /// <summary>Forgets every round, burst and flash — the session was reopened.</summary>
    public void Reset()
    {
        Array.Clear(_slots);
        _bursts.Clear();
        _rounds.Clear();
        _impacts.Clear();
    }

    /// <summary>The frame-time accumulator <c>[0xF0D2:0xF0D4]</c>, unsigned 32-bit.</summary>
    /// <param name="registers">The register file.</param>
    public static long FrameTime(CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        return registers.Word(SceneFrameTimer.FrameTime)
            | ((long)registers.Word(SceneFrameTimer.FrameTime + 2) << 16);
    }

    /// <summary>
    /// The spacing between consecutive rounds of a belt for a weapon class, in world units.
    /// </summary>
    /// <param name="speedQ8">The shot's Q8 speed (feet × 256 per 256 ticks).</param>
    /// <param name="roundsPerBurst">The class's <c>ammoPerShot</c>.</param>
    public static double CyclicSpacingWorldUnits(int speedQ8, int roundsPerBurst) =>
        roundsPerBurst <= 0 ? 0.0 : (speedQ8 / 256.0) * (BurstWindowTicks / (double)roundsPerBurst) / 256.0;

    // ------------------------------------------------------------------ the burst detector

    private void DetectBursts(
        CombatRegisters registers, PoolArena arena, ICombatStaticData statics, long now)
    {
        int dt = unchecked((short)registers.Word(MissionSession.SceneFrameDt));
        for (int i = 0; i < CombatSpawn.SlotCount; i++)
        {
            ushort offset = unchecked((ushort)(CombatSpawnTable.TableDgroupOffset + (i * CombatSpawn.SlotBytes)));
            SpawnRecordRef record = new SpawnRecordRef(registers, offset);
            ref SlotShadow shadow = ref _slots[i];

            if (!record.IsActive || !arena.Covers(record.PoolObjectRef, 0x18))
            {
                shadow.Active = false;
                continue;
            }

            CombatObjectView shot = new CombatObjectView(arena, record.PoolObjectRef);
            CombatPosition position = shot.Position;

            bool fresh;
            if (!shadow.Active
                || shadow.Owner != record.OwnerId
                || shadow.WeaponClassRef != record.WeaponClassRef
                || shadow.PoolObject != record.PoolObjectRef)
            {
                fresh = true;
            }
            else
            {
                // The ghost flies the same integrator the projectile row ran this frame
                // (image@0x028A5..0x028CC): a live shot lands exactly on it, a new one does not.
                CombatPosition expected = CombatGeometry.AccumulateDistance3d(
                    shadow.Ghost, StepDistance(record.SpeedQ8, dt), shot.Elevation, shot.Heading);
                fresh = expected != position;
            }

            shadow.Active = true;
            shadow.Owner = record.OwnerId;
            shadow.WeaponClassRef = record.WeaponClassRef;
            shadow.PoolObject = record.PoolObjectRef;
            shadow.Ghost = position;

            if (fresh)
            {
                Census.TracersSeen++;
                OpenBurst(arena, statics, record, shot, now);
            }
        }
    }

    private void OpenBurst(
        PoolArena arena, ICombatStaticData statics, SpawnRecordRef record, CombatObjectView shot, long now)
    {
        WeaponClassView weapon = new WeaponClassView(statics, record.WeaponClassRef);
        int rounds = weapon.AmmoPerShot;

        // A guided class (+0x24 bit 4) pays one round and has a boost/coast envelope the ghost does
        // not model; it never trails.  So does anything that pays a single round.
        if (rounds <= 1 || (weapon.ClassFlags & WeaponFireScheduler.GuidedBit) != 0 || weapon.BoostFrames != 0)
        {
            Census.BurstsWithoutTrail++;
            return;
        }

        CombatPosition ownerPosition = arena.Covers(record.OwnerId, 0x18)
            ? new CombatObjectView(arena, record.OwnerId).Position
            : shot.Position;
        CombatPosition muzzle = shot.Position;

        double interval = SpacingWorldUnits > 0.0
            ? SpacingWorldUnits * 256.0 * 256.0 / Math.Max(1, record.SpeedQ8)
            : BurstWindowTicks / (double)rounds;

        _bursts.Add(new Burst
        {
            Id = ++_burstCounter,
            Owner = record.OwnerId,
            WeaponClassRef = record.WeaponClassRef,
            Heading = shot.Heading,
            Elevation = shot.Elevation,
            SpeedQ8 = record.SpeedQ8,
            MuzzleOffset = new CombatPosition(
                muzzle.X - ownerPosition.X, muzzle.Y - ownerPosition.Y, muzzle.Z - ownerPosition.Z),
            LastOwnerPosition = ownerPosition,
            Remaining = rounds - 1,
            Total = rounds,
            NextBirthTick = now + interval,
            IntervalTicks = interval,
            // +0x1F — the class's lifetime in master frames (the spawn's expiry, image@0x0250E).
            LifetimeTicks = Math.Max(1, (int)statics.Byte(record.WeaponClassRef + 0x1F)) * TicksPerMasterFrame,
        });
        Census.Bursts++;
    }

    // ------------------------------------------------------------------ births and flight

    private void BearRounds(PoolArena arena, long now)
    {
        for (int b = _bursts.Count - 1; b >= 0; b--)
        {
            Burst burst = _bursts[b];
            while (burst.Remaining > 0 && burst.NextBirthTick <= now)
            {
                // The muzzle direction AT THIS ROUND'S BIRTH, not the tracer's spawn direction. A
                // round leaves the gun along the barrel as it points now, so a burst fired while the
                // aircraft is turning fans into a curved stream instead of a giveaway parallel one.
                // The tracer keeps its own spawn direction; a live owner's current heading/elevation
                // is the sweeping muzzle for the grey rounds.
                short heading = burst.Heading;
                short elevation = burst.Elevation;
                if (arena.Covers(burst.Owner, 0x18))
                {
                    CombatObjectView owner = new CombatObjectView(arena, burst.Owner);
                    if ((owner.Flags & CombatSceneObjects.ActiveFlag) != 0)
                    {
                        burst.LastOwnerPosition = owner.Position;
                        heading = owner.Heading;
                        elevation = owner.Elevation;
                    }
                }

                CombatPosition born = new CombatPosition(
                    burst.LastOwnerPosition.X + burst.MuzzleOffset.X,
                    burst.LastOwnerPosition.Y + burst.MuzzleOffset.Y,
                    burst.LastOwnerPosition.Z + burst.MuzzleOffset.Z);

                // Born between two frames: it has already flown the residue of this frame, along its
                // own muzzle direction.
                double flownTicks = now - burst.NextBirthTick;
                int residue = (int)Math.Round((double)burst.SpeedQ8 * flownTicks / 256.0);
                if (residue > 0)
                {
                    born = CombatGeometry.AccumulateDistance3d(born, residue, elevation, heading);
                }

                int index = burst.Total - burst.Remaining;
                _rounds.Add(new Round(
                    born,
                    heading,
                    elevation,
                    burst.SpeedQ8,
                    (long)Math.Round(burst.NextBirthTick) + burst.LifetimeTicks,
                    burst.Owner,
                    burst.WeaponClassRef,
                    index,
                    burst.Id));
                Census.RoundsBorn++;
                burst.Remaining--;
                burst.NextBirthTick += burst.IntervalTicks;
            }

            if (burst.Remaining <= 0)
            {
                _bursts.RemoveAt(b);
            }
        }

        while (_rounds.Count > MaxLiveRounds)
        {
            _rounds.RemoveAt(0);
            Census.RoundsDropped++;
        }
    }

    private void FlyRounds(
        CombatRegisters registers, PoolArena arena, ICombatStaticData statics, int dt, long now)
    {
        if (_rounds.Count == 0)
        {
            return;
        }

        ReadCandidates(registers, arena);

        for (int r = _rounds.Count - 1; r >= 0; r--)
        {
            Round round = _rounds[r];
            if (now >= round.ExpiryTick)
            {
                _rounds.RemoveAt(r);
                Census.RoundsExpired++;
                continue;
            }

            CombatPosition from = round.Position;
            CombatPosition to = CombatGeometry.AccumulateDistance3d(
                from, StepDistance(round.SpeedQ8, dt), round.Elevation, round.Heading);

            if (TryHit(registers, statics, round, from, to, out Impact impact))
            {
                _impacts.Add(impact with { BornTick = now, Sequence = _sequence++ });
                _rounds.RemoveAt(r);
                Census.AirHits++;
                continue;
            }

            if (to.Y <= 0)
            {
                if (GroundHits)
                {
                    double t = from.Y > to.Y ? from.Y / (double)(from.Y - to.Y) : 0.0;
                    // The scheduler never lets a flash sink into the ground plane: its Y is
                    // clamped up to 0x1E00 (30 ft) at image@0x03AFD.  A ground strike keeps that rule.
                    _impacts.Add(new Impact(
                        (from.X + ((to.X - from.X) * t)) / 256.0,
                        Combat.Effects.DeferredEffectPool.MinimumY / 256.0,
                        (from.Z + ((to.Z - from.Z) * t)) / 256.0,
                        now,
                        _sequence++,
                        Ground: true,
                        Victim: 0));
                }

                _rounds.RemoveAt(r);
                Census.GroundHits++;
                continue;
            }

            _rounds[r] = round with { Position = to };
        }
    }

    private void RetireImpacts(long now)
    {
        for (int i = _impacts.Count - 1; i >= 0; i--)
        {
            if (_impacts[i].ExpiredAt(now))
            {
                _impacts.RemoveAt(i);
            }
        }
    }

    // ------------------------------------------------------------------ the hit test

    private void ReadCandidates(CombatRegisters registers, PoolArena arena)
    {
        _candidates.Clear();
        ushort player = registers.PlayerObjectRef;
        _playerRef = player;
        ushort cursor = registers.Word(LifecycleOffsets.RenderListHead);
        for (int visited = 0; cursor != 0 && visited < 512; visited++)
        {
            if (!arena.Covers(cursor, 0x18))
            {
                break;
            }

            CombatObjectView view = new CombatObjectView(arena, cursor);
            ushort next = arena.Word((ushort)(cursor + 0x04));
            if ((view.Flags & CombatSceneObjects.ActiveFlag) != 0 && view.ClassRef != 0 && view.CarriesEngagement)
            {
                CombatPosition position = view.Position;
                _candidates.Add(new Candidate(
                    cursor,
                    view.ClassRef,
                    // image@0x28950..0x2898F — the clip works on (i32)position >> 8, i.e. feet.
                    position.X >> 8,
                    position.Y >> 8,
                    position.Z >> 8,
                    RotationCode(view),
                    (arena.Byte((ushort)(view.EngagementBlockRef + 0x05)) & 0x40) != 0,
                    cursor == player));
            }

            cursor = next;
        }
    }

    /// <summary>
    /// Which of the clip's four orientation arms a candidate takes
    /// (<c>image@0x28A20..0x28AC6</c>): its <c>+0x12</c> yaw word when that word is exactly
    /// <c>0x2D0</c> / <c>0x5A0</c> / <c>0x870</c>, and 0 — meaning NO transform at all — for every
    /// other value, including a <see cref="WorldObjectFlags.NoOrientation"/> object, which never
    /// reaches the switch.
    /// </summary>
    private static ushort RotationCode(CombatObjectView view)
    {
        if ((view.Flags & (ushort)WorldObjectFlags.NoOrientation) != 0)
        {
            return 0;
        }

        ushort raw = unchecked((ushort)view.Heading);
        return raw is 0x2D0 or 0x5A0 or 0x870 ? raw : (ushort)0;
    }

    /// <summary>
    /// The query's <c>HalfRange</c> for one grey round, Q8 feet — the projectile row's own window
    /// arithmetic (<c>image@0x028F1..0x0291D</c>): the weapon class's <c>+0x20</c> selection window,
    /// plus <c>0x14</c> for an AI shot or <c>0x96</c> for the player's when the Easy-Aiming cheat
    /// byte <c>[0xE46D]</c> is set, shifted left 8.
    /// </summary>
    /// <remarks>
    /// The record's <c>+0x10</c> gate frame (<c>image@0x028F4</c>, which zeroes the window until the
    /// selection window opens) is NOT modelled: it can only zero a window that is already 0 for
    /// every gun class, and grey rounds exist only for gun classes.
    /// </remarks>
    private int HalfRangeFor(ICombatStaticData statics, CombatRegisters registers, in Round round)
    {
        short window = new WeaponClassView(statics, round.WeaponClassRef).SelectionWindow;
        if (round.Owner != _playerRef)
        {
            window = unchecked((short)(window + 0x14));
        }
        else if (registers.Byte(EngagementFireAuthority.CheatFlagDgroupOffset) != 0)
        {
            window = unchecked((short)(window + 0x96));
        }

        return unchecked(window << 8);
    }

    private bool TryHit(
        CombatRegisters registers,
        ICombatStaticData statics,
        in Round round,
        CombatPosition from,
        CombatPosition to,
        out Impact impact)
    {
        impact = default;
        if (_candidates.Count == 0)
        {
            return false;
        }

        // The shooter's side: its engagement block's +0x05 bit 6, when it carries one.
        bool playerShot = round.Owner == _playerRef;
        bool? shooterTeam = null;
        foreach (Candidate candidate in _candidates)
        {
            if (candidate.ObjectRef == round.Owner)
            {
                shooterTeam = candidate.TeamBit;
                break;
            }
        }

        int halfRange = HalfRangeFor(statics, registers, round);
        double best = double.MaxValue;
        Candidate hit = default;
        bool any = false;
        foreach (Candidate candidate in _candidates)
        {
            if (candidate.ObjectRef == round.Owner)
            {
                continue;
            }

            // image@0x0299B..0x029C0 — an AI shot never hits its own side.  The player's rounds
            // are not team-tested, as the ported path is not (`selfTeam` skips the XOR).
            if (!playerShot && shooterTeam is { } team && team == candidate.TeamBit)
            {
                continue;
            }

            if (!TestCandidate(statics, candidate, halfRange, from, to, out double t))
            {
                continue;
            }

            if (t < best)
            {
                best = t;
                hit = candidate;
                any = true;
            }
        }

        if (!any)
        {
            return false;
        }

        Census.Victims[hit.ObjectRef] = Census.Victims.GetValueOrDefault(hit.ObjectRef) + 1;
        if (hit.IsPlayer)
        {
            Census.AirHitsOnPlayer++;
        }
        impact = new Impact(
            (from.X + ((to.X - from.X) * best)) / 256.0,
            (from.Y + ((to.Y - from.Y) * best)) / 256.0,
            (from.Z + ((to.Z - from.Z) * best)) / 256.0,
            0,
            0,
            Ground: false,
            Victim: hit.ObjectRef);
        return true;
    }

    /// <summary>
    /// One candidate's whole test: object-relative segment, the <c>i16</c> gate, the
    /// four-cardinal orientation law, then the slab test against the class box.
    /// </summary>
    /// <param name="statics">The DGROUP surface the class records live in.</param>
    /// <param name="candidate">The candidate.</param>
    /// <param name="halfRange">The weapon window, Q8 feet.</param>
    /// <param name="from">The round's position at the start of this frame, sim units.</param>
    /// <param name="to">Its position at the end, sim units.</param>
    /// <param name="t">Where along the segment it enters, <c>t ∈ [0, 1]</c>.</param>
    /// <returns>Whether the segment meets the box.</returns>
    private bool TestCandidate(
        ICombatStaticData statics,
        in Candidate candidate,
        int halfRange,
        CombatPosition from,
        CombatPosition to,
        out double t)
    {
        t = -1.0;

        // image@0x28992..0x28A1A — the segment is made object-relative and each axis must fit in a
        // signed word or the candidate is rejected outright.  (The clip truncates the segment to
        // whole feet; the trail keeps the round's Q8 sub-foot position, which only makes the entry
        // point it draws the flash at smoother — the accept/reject decision is the same volume.)
        double ax = (from.X / 256.0) - candidate.ScaledX;
        double ay = (from.Y / 256.0) - candidate.ScaledY;
        double az = (from.Z / 256.0) - candidate.ScaledZ;
        double bx = (to.X / 256.0) - candidate.ScaledX;
        double by = (to.Y / 256.0) - candidate.ScaledY;
        double bz = (to.Z / 256.0) - candidate.ScaledZ;
        if (!FitsWord(ax) || !FitsWord(ay) || !FitsWord(az) ||
            !FitsWord(bx) || !FitsWord(by) || !FitsWord(bz))
        {
            return false;
        }

        (ax, az) = RotateXz(candidate.RotCode, ax, az);
        (bx, bz) = RotateXz(candidate.RotCode, bx, bz);

        HitBox box = HitBox.FromClassRecord(statics, candidate.ClassRef, halfRange).Scaled(HitBoxScale);
        t = SegmentBoxEntry(ax, ay, az, bx, by, bz, box);
        return t >= 0.0;

        static bool FitsWord(double v) => v is >= short.MinValue and <= short.MaxValue;
    }

    /// <summary>
    /// The clip's four-cardinal orientation law applied to one point's X/Z
    /// (<c>image@0x28A20..0x28AC6</c>).  <c>0x2D0</c> (90°) maps <c>(x, z) → (z, −x)</c>,
    /// <c>0x5A0</c> (180°) negates both, <c>0x870</c> (270°) maps <c>(x, z) → (−z, x)</c>; every
    /// other code — the clip's DEFAULT arm — applies NO transform, which is why a manoeuvring bandit
    /// is tested against an un-rotated box.
    /// </summary>
    /// <param name="rotCode">The candidate's <c>+0x12</c> yaw word.</param>
    /// <param name="x">The point's object-relative X.</param>
    /// <param name="z">Its object-relative Z.</param>
    /// <returns>The transformed X and Z.</returns>
    public static (double X, double Z) RotateXz(ushort rotCode, double x, double z) => rotCode switch
    {
        0x2D0 => (z, -x),
        0x5A0 => (-x, -z),
        0x870 => (-z, x),
        _ => (x, z),
    };

    /// <summary>
    /// Where a segment first enters an axis-aligned box, as a parameter <c>t ∈ [0, 1]</c> along
    /// the segment, or <c>−1</c> when it misses.  A segment that STARTS inside answers 0.  This
    /// is the ordinary slab test; the volume it tests is the integer kernel's
    /// (<see cref="HitBox"/>).
    /// </summary>
    /// <param name="ax">Segment start X, box-local.</param>
    /// <param name="ay">Segment start Y.</param>
    /// <param name="az">Segment start Z.</param>
    /// <param name="bx">Segment end X.</param>
    /// <param name="by">Segment end Y.</param>
    /// <param name="bz">Segment end Z.</param>
    /// <param name="box">The box.</param>
    /// <returns>The entry parameter, or <c>−1</c>.</returns>
    public static double SegmentBoxEntry(
        double ax, double ay, double az,
        double bx, double by, double bz,
        HitBox box)
    {
        double enter = 0.0, exit = 1.0;
        if (!Slab(ax, bx - ax, box.XLow, box.XHigh, ref enter, ref exit) ||
            !Slab(ay, by - ay, box.YLow, box.YHigh, ref enter, ref exit) ||
            !Slab(az, bz - az, box.ZLow, box.ZHigh, ref enter, ref exit))
        {
            return -1.0;
        }

        return enter;

        static bool Slab(double origin, double delta, double low, double high, ref double enter, ref double exit)
        {
            if (low > high)
            {
                return false;   // a degenerate (inverted) class box admits nothing.
            }

            if (Math.Abs(delta) < 1e-12)
            {
                return origin >= low && origin <= high;
            }

            double t0 = (low - origin) / delta;
            double t1 = (high - origin) / delta;
            if (t0 > t1)
            {
                (t0, t1) = (t1, t0);
            }

            enter = Math.Max(enter, t0);
            exit = Math.Min(exit, t1);
            return enter <= exit;
        }
    }

    /// <summary>
    /// The distance a shot flies in one frame: <c>(speed × dt) &gt;&gt; 8</c>, the projectile row's
    /// own product (<c>image@0x028A5..0x028CC</c>, an unsigned 32×32 low product and an arithmetic
    /// shift).
    /// </summary>
    /// <param name="speedQ8">The Q8 speed.</param>
    /// <param name="dt">The frame's dt.</param>
    public static int StepDistance(int speedQ8, int dt) =>
        unchecked((int)((uint)speedQ8 * (uint)dt)) >> 8;

    /// <summary>
    /// The GREY ROUND's mesh: one line record along <c>+Z</c> from the round's position forward,
    /// the same shape and orientation as the shipped <c>bullet</c> (<c>data/meshes/bullet.json</c>:
    /// vertices <c>(0,0,64) → (0,0,0)</c>, palette 32, tag <c>0x01</c>) but shorter and in a grey.
    /// </summary>
    /// <param name="lengthWorldUnits">The streak's length in feet.</param>
    /// <param name="colorIndex">Its palette index.</param>
    /// <remarks>
    /// Its basename <c>round</c> is what the renderer's width model keys on
    /// (<c>LineWidthModel.ClassOf</c>) — it is a mesh the port authors, not one the data carries,
    /// and the only such mesh; nothing in <c>data/</c> is copied.
    /// </remarks>
    public static MeshModel RoundMesh(double lengthWorldUnits, int colorIndex)
    {
        int length = Math.Max(1, (int)Math.Round(lengthWorldUnits));
        return MeshLibrary.Build(
            MeshBasename,
            new ExeMeshDocumentDto
            {
                Basename = MeshBasename,
                Slot = new ExeMeshSlotDto
                {
                    ScaleShiftExponent = 0,
                    RenderLayerPriority = "0x80",
                    MeshExtent = length,
                    LodThresholds = [100000, 0, 0],
                },
                Lods =
                [
                    new ExeMeshLodDto
                    {
                        Index = 0,
                        Descriptor = new ExeMeshLodDescriptorDto { RecordCount = 1, VertexCount = 2 },
                        InlineVertices = [[0, 0, length], [0, 0, 0]],
                        Records =
                        [
                            new ExeMeshRecordDto
                            {
                                Primitive = "line",
                                Opcode = 1,
                                Tag = "0x01",
                                Color = Math.Clamp(colorIndex, 0, 255),
                                Indices = [0, 1],
                            },
                        ],
                    },
                ],
            },
            null);
    }

    /// <summary>The grey round mesh's basename.</summary>
    public const string MeshBasename = "round";
}

/// <summary>What the gunnery trail did.</summary>
public sealed class GunneryCensus
{
    /// <summary>Verified tracer spawns seen (every new spawn-table shot).</summary>
    public long TracersSeen { get; internal set; }

    /// <summary>Bursts that bore a trail.</summary>
    public long Bursts { get; internal set; }

    /// <summary>Spawns that bore none: guided, single-round or enveloped classes.</summary>
    public long BurstsWithoutTrail { get; internal set; }

    /// <summary>Grey rounds born.</summary>
    public long RoundsBorn { get; internal set; }

    /// <summary>Grey rounds that timed out (the class's <c>+0x1F</c> lifetime).</summary>
    public long RoundsExpired { get; internal set; }

    /// <summary>Grey rounds dropped for the live cap.</summary>
    public long RoundsDropped { get; internal set; }

    /// <summary>Grey rounds that struck an engagement object.</summary>
    public long AirHits { get; internal set; }

    /// <summary>
    /// How many of <see cref="AirHits"/> struck the PLAYER's own object (an AI burst's
    /// rounds).  The rest struck someone else, which for a player sortie means a bandit.
    /// </summary>
    public long AirHitsOnPlayer { get; internal set; }

    /// <summary>Grey rounds that reached the ground plane.</summary>
    public long GroundHits { get; internal set; }

    /// <summary>Hits per struck object.</summary>
    public Dictionary<ushort, int> Victims { get; } = [];
}
