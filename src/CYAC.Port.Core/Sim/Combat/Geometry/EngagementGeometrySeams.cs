namespace CYAC.Port.Core.Sim.Combat.Geometry;

/// <summary>
/// <c>grid_2d_tallest_obstacle_score_at (ex-grid_2d_object_proximity_score_at) @image@0x21CEB</c> — the 2-D terrain-grid proximity score
/// the shot-angle selector falls back on when the target is closer than <c>0x7D0</c>.
/// </summary>
/// <remarks>
/// <para>
/// The world grid is C2b's subsystem, so this is a seam.  MEASURED: the arm that reaches it is taken <b>0
/// times in 27,177 observed calls</b> across all six reference windows, so verification arms it with
/// <see cref="UnavailableTerrainProximity"/> — a TRIPWIRE that names itself if a recording ever
/// gets there.
/// </para>
/// <para>
/// Call site <c>image@0x04D29</c>: the caller pushes <c>[0xED44]</c>, <c>[0xED42]</c>,
/// <c>[0xED4C]</c>, <c>[0xED4A]</c> — i.e. the fire position's X then Z, high word first — and
/// takes the score back in <c>AX</c>.
/// </para>
/// </remarks>
public interface ITerrainProximity
{
    /// <summary>Scores the terrain around a ground-plane point.</summary>
    /// <param name="x">The fire position's X (<c>[0xED42]</c>).</param>
    /// <param name="z">The fire position's Z (<c>[0xED4A]</c>).</param>
    /// <returns>The original's <c>AX</c>, compared UNSIGNED against the clipped range.</returns>
    ushort ScoreAt(int x, int z);
}

/// <summary>The tripwire terrain seam: it throws, naming the arm that is missing.</summary>
/// <remarks>
/// The K5/C2 convention — "the FSM did not run" must be a visible decision, never a silent default.
/// A verification catches this and counts the call as unverifiable rather than reporting a pass.
/// </remarks>
public sealed class UnavailableTerrainProximity : ITerrainProximity
{
    /// <summary>The shared instance.</summary>
    public static UnavailableTerrainProximity Instance { get; } = new();

    /// <inheritdoc/>
    public ushort ScoreAt(int x, int z) => throw new EngagementGeometrySeamException(
        "engagement_shot_angle_select_by_range @image@0x04D29 reached the terrain-grid sub-path "
            + "(grid_2d_tallest_obstacle_score_at @image@0x21CEB), which is C2b's world grid.  "
            + "This arm fired 0 times in 27,177 P6 calls over the six v1.1 reference windows; the "
            + "tripwire has now fired, so the world-grid seam must be wired up.");
}

/// <summary>A seam the kernel needed and did not have.</summary>
/// <remarks>Distinct from an ordinary failure: it names an arm no recording had reached.</remarks>
public sealed class EngagementGeometrySeamException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Which seam, and who owns it.</param>
    public EngagementGeometrySeamException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    /// <param name="message">Which seam, and who owns it.</param>
    /// <param name="innerException">The cause.</param>
    public EngagementGeometrySeamException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with the default message.</summary>
    public EngagementGeometrySeamException()
        : base("a combat-geometry seam was reached that this build does not implement")
    {
    }
}

/// <summary>
/// <c>shot_trajectory_proximity_accum @image@0x08510</c> as the geometry engine's FIRST act
/// (<c>image@0x066F1</c>, <c>AL = 0</c>) — refresh the acquisition target's snapshot, or leave it
/// alone while the six-key cache is fresh.
/// </summary>
/// <remarks>
/// <para>
/// A seam because the routine's state is SHARED ACROSS ENGAGEMENT NODES and mutated by seven other
/// callers: the cache <c>[0xB53E..0xB547]</c> records which node, target, weapon type and hit-check
/// flag last refreshed it, and the per-shot FSM's own <c>AL = 1</c> call additionally MOVES the
/// current snapshot along the target's heading (the ACTIVE path).  Neither the cache nor the other
/// callers are inside a P6 probe window, so a verification feeds this seam from the trace and
/// censuses its write set (<c>[0xEDB0..0xEDC7]</c> + <c>[0xEDC8..0xEDDF]</c> + the cache) instead of
/// comparing it — the same rule C2 applied to the world-grid query.
/// </para>
/// <para>
/// <see cref="TargetSnapshotAccumulator"/> is the real thing and is what the port's own runtime
/// uses; it is unit-tested from the bytes.
/// </para>
/// </remarks>
public interface ITargetSnapshot
{
    /// <summary>Runs the routine.</summary>
    /// <param name="context">The geometry context.</param>
    /// <param name="isHitCheck">The original's <c>AL</c>; the geometry engine always passes false.</param>
    void Refresh(EngagementGeometryContext context, bool isHitCheck);
}

/// <summary>The real <c>shot_trajectory_proximity_accum</c> — see <see cref="ShotTargetSnapshot"/>.</summary>
public sealed class TargetSnapshotAccumulator : ITargetSnapshot
{
    /// <summary>The shared instance.</summary>
    public static TargetSnapshotAccumulator Instance { get; } = new();

    /// <inheritdoc/>
    public void Refresh(EngagementGeometryContext context, bool isHitCheck) =>
        ShotTargetSnapshot.Accumulate(context, isHitCheck);
}

/// <summary>
/// The per-arm census of the geometry engine: which branch of which routine the run actually took.
/// </summary>
/// <remarks>
/// The rule: every branch and arm is censused, and an arm no recording ever reaches
/// is unit-tested from the bytes and carries a tripwire.  Every counter here is named after the
/// arm's own image address so a report line is checkable.
/// </remarks>
public sealed class EngagementGeometryCensus
{
    /// <summary>Calls of <c>engagement_slot_angle_update</c>, by the <c>AL</c> mode byte.</summary>
    public long ModeZero { get; set; }

    /// <summary>Calls with a non-zero <c>AL</c> — the change-detection epilogue runs.</summary>
    public long ModeNonZero { get; set; }

    /// <summary>Heading arm: the <c>0x7FFE</c> HOLD sentinel (<c>image@0x06710</c>).</summary>
    public long HeadingHold { get; set; }

    /// <summary>Heading arm: a DIRECT angle below <c>0x1680</c> (<c>image@0x0671E</c>).</summary>
    public long HeadingDirect { get; set; }

    /// <summary>Heading arm: the intercept BEARING path (<c>image@0x0673B</c>).</summary>
    public long HeadingBearing { get; set; }

    /// <summary>Heading arm: intercept-bearing requested but no acq target (<c>image@0x06734</c>).</summary>
    public long HeadingBearingNoTarget { get; set; }

    /// <summary>Heading arm: the <c>0x21C0</c> bias-left branch (<c>image@0x0677A</c>).</summary>
    public long HeadingBiasMinus { get; set; }

    /// <summary>Heading arm: the <c>0x21C8</c> bias-right branch (<c>image@0x06790</c>).</summary>
    public long HeadingBiasPlus { get; set; }

    /// <summary>Heading arm: the COMPLEX intercept build (<c>image@0x06796</c>) — the rotate path.</summary>
    public long HeadingComplex { get; set; }

    /// <summary>The heading feed-in ran a bank-scale step (<c>image@0x0684D</c> not taken).</summary>
    public long HeadingStepped { get; set; }

    /// <summary>Elevation arm: the <c>0x7FFE</c> HOLD sentinel.</summary>
    public long ElevationHold { get; set; }

    /// <summary>Elevation arm: a DIRECT angle below <c>0x1680</c>.</summary>
    public long ElevationDirect { get; set; }

    /// <summary>Elevation arm: the 3-D intercept BEARING path (<c>image@0x0689A</c>).</summary>
    public long ElevationBearing { get; set; }

    /// <summary>Elevation arm: bearing requested but no acq target.</summary>
    public long ElevationBearingNoTarget { get; set; }

    /// <summary>Elevation arm: the <c>0x21C0</c> bias-down branch.</summary>
    public long ElevationBiasMinus { get; set; }

    /// <summary>Elevation arm: the <c>0x21C8</c> bias-up branch.</summary>
    public long ElevationBiasPlus { get; set; }

    /// <summary>Elevation arm: the COMPLEX 3-D bearing off the staged intercept block.</summary>
    public long ElevationComplex { get; set; }

    /// <summary>The elevation stepper ran (<c>image@0x06998</c> not taken).</summary>
    public long ElevationStepped { get; set; }

    /// <summary>The lead/proximity flag was set (<c>image@0x06989</c>).</summary>
    public long ProximityFlagSet { get; set; }

    /// <summary>The lock-on <c>+0xF0</c> boost fired (<c>image@0x069A6</c>).</summary>
    public long ElevationStepBoosted { get; set; }

    /// <summary>The proximity <c>&gt;&gt;1</c> halving fired (<c>image@0x069BA</c>).</summary>
    public long ElevationStepHalved { get; set; }

    /// <summary>The proximity-fuze gate ran to its end and set <c>[0x0F0C]</c> (<c>image@0x06A4B</c>).</summary>
    public long FuzeHit { get; set; }

    /// <summary>The proximity-fuze gate was ARMED (<c>[0xED76] == -1</c>) but did not fire.</summary>
    public long FuzeArmedNoHit { get; set; }

    /// <summary>Bank arm: the COMPLEX regime (<c>image@0x06A61</c>).</summary>
    public long BankComplex { get; set; }

    /// <summary>Bank arm: the complex regime's lead correction ran (<c>image@0x06A86</c>).</summary>
    public long BankLeadCorrection { get; set; }

    /// <summary>Bank arm: HOLD (<c>0x7FFE</c>).</summary>
    public long BankHold { get; set; }

    /// <summary>Bank arm: DIRECT.</summary>
    public long BankDirect { get; set; }

    /// <summary>Bank arm: the <c>0x21C0</c> bias branch.</summary>
    public long BankBiasMinus { get; set; }

    /// <summary>Bank arm: the <c>0x21C8</c> bias branch.</summary>
    public long BankBiasPlus { get; set; }

    /// <summary>
    /// Bank arm: the source matched NO regime, so the original reads the UNINITIALISED
    /// <c>[bp-0x26]</c> local (<c>image@0x06B44</c> falls through to <c>0x06B53</c>).
    /// </summary>
    public long BankNoRegime { get; set; }

    /// <summary>Phase 6 ran (<c>[0xB532] != 0</c>, <c>image@0x06B6E</c>).</summary>
    public long ArcDecayGateOpen { get; set; }

    /// <summary>Phase 6's step count survived the decrement, so the accumulator was decayed.</summary>
    public long ArcDecayApplied { get; set; }

    /// <summary>Phase 7's envelope override rescaled <c>[0xEDA0]</c> (<c>image@0x06C22</c>).</summary>
    public long EnvelopeOverride { get; set; }

    /// <summary>Phase 8: <c>altitudeDelta &lt; 0xFC18</c> with NO acq target (<c>image@0x06C3C</c>).</summary>
    public long AltitudeDeepNoTarget { get; set; }

    /// <summary>Phase 8: the deep-altitude gap arm at or past <c>0x1388</c> (<c>image@0x06C6B</c>).</summary>
    public long AltitudeGapCeiling { get; set; }

    /// <summary>Phase 8: the five-way graded range arms (<c>image@0x06C72..0x06CAD</c>), by index 0..4.</summary>
    public long[] AltitudeGapGrades { get; } = new long[5];

    /// <summary>Phase 8: the mild-negative arm reached the class filter (<c>image@0x06CD6</c>).</summary>
    public long AltitudeNegativeTracked { get; set; }

    /// <summary>Phase 8: the mild-negative arm skipped to the free stepper.</summary>
    public long AltitudeNegativeSkipped { get; set; }

    /// <summary>Phase 8: the positive-delta clamped dispatch (<c>image@0x06CF7</c>).</summary>
    public long AltitudePositive { get; set; }

    /// <summary>Phase 8: <c>altitudeDelta == 0</c> — no dispatch at all.</summary>
    public long AltitudeIdle { get; set; }

    /// <summary>The change-detection epilogue set <c>[0xED59]</c> bit2 (<c>image@0x06D30</c>).</summary>
    public long ChangeLatched { get; set; }

    /// <summary><c>shot_trajectory_proximity_accum</c>: cache HIT, so no snapshot refresh.</summary>
    public long SnapshotCacheHit { get; set; }

    /// <summary>Its cache MISS, so both snapshots were refreshed.</summary>
    public long SnapshotRefreshed { get; set; }

    /// <summary>
    /// Snapshot bytes the port could not store because <c>[0xEDC6..0xEDCD]</c> is a DGROUP HOLE
    /// that no combat register window covers.
    /// </summary>
    public long SnapshotHoleBytes { get; set; }

    /// <summary>
    /// Its hit-check path refused the acquisition machine's "no weapon slot" sentinel
    /// <c>[0xED64] == 0xFF</c> (<c>image@0x0812C</c>) instead of indexing 0x20C bytes past the
    /// prototype's four-entry weapon-slot table (<c>image@0x085C1</c>) — quirk
    /// <c>acq-no-weapon-slot-indexes-past-the-table</c>.
    /// </summary>
    public long SnapshotNoWeaponSlot { get; set; }

    /// <summary><c>engagement_shot_angle_select_by_range</c>: not armed (<c>image@0x04CF1</c>).</summary>
    public long ShotSelectNotArmed { get; set; }

    /// <summary>Its perturbation-cache hit (<c>image@0x04D19</c> via <c>0x04D14</c>).</summary>
    public long ShotSelectPerturbHit { get; set; }

    /// <summary>Its terrain-grid sub-path (<c>image@0x04D29</c>) — the C2b seam.</summary>
    public long ShotSelectTerrain { get; set; }

    /// <summary>Its branch A, the altitude clamp (<c>image@0x04D59</c>).</summary>
    public long ShotSelectBranchA { get; set; }

    /// <summary>Its branch B, the range window and lock-on sweep (<c>image@0x04D9C</c>).</summary>
    public long ShotSelectBranchB { get; set; }

    /// <summary>Branch B's bounded lock-on sweep took at least one iteration (<c>image@0x04DD8</c>).</summary>
    public long ShotSelectSweep { get; set; }

    /// <summary>The selector committed a change and killed the script PC (<c>image@0x04E2E</c>).</summary>
    public long ShotSelectScriptKill { get; set; }

    /// <summary>The lead-set proximity check set <c>[0xEDE0]</c> (<c>image@0x06F21</c>).</summary>
    public long LeadEnableSet { get; set; }

    /// <summary>Adds another census into this one.</summary>
    /// <param name="other">The census to fold in.</param>
    public void Add(EngagementGeometryCensus other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ModeZero += other.ModeZero;
        ModeNonZero += other.ModeNonZero;
        HeadingHold += other.HeadingHold;
        HeadingDirect += other.HeadingDirect;
        HeadingBearing += other.HeadingBearing;
        HeadingBearingNoTarget += other.HeadingBearingNoTarget;
        HeadingBiasMinus += other.HeadingBiasMinus;
        HeadingBiasPlus += other.HeadingBiasPlus;
        HeadingComplex += other.HeadingComplex;
        HeadingStepped += other.HeadingStepped;
        ElevationHold += other.ElevationHold;
        ElevationDirect += other.ElevationDirect;
        ElevationBearing += other.ElevationBearing;
        ElevationBearingNoTarget += other.ElevationBearingNoTarget;
        ElevationBiasMinus += other.ElevationBiasMinus;
        ElevationBiasPlus += other.ElevationBiasPlus;
        ElevationComplex += other.ElevationComplex;
        ElevationStepped += other.ElevationStepped;
        ProximityFlagSet += other.ProximityFlagSet;
        ElevationStepBoosted += other.ElevationStepBoosted;
        ElevationStepHalved += other.ElevationStepHalved;
        FuzeHit += other.FuzeHit;
        FuzeArmedNoHit += other.FuzeArmedNoHit;
        BankComplex += other.BankComplex;
        BankLeadCorrection += other.BankLeadCorrection;
        BankHold += other.BankHold;
        BankDirect += other.BankDirect;
        BankBiasMinus += other.BankBiasMinus;
        BankBiasPlus += other.BankBiasPlus;
        BankNoRegime += other.BankNoRegime;
        ArcDecayGateOpen += other.ArcDecayGateOpen;
        ArcDecayApplied += other.ArcDecayApplied;
        EnvelopeOverride += other.EnvelopeOverride;
        AltitudeDeepNoTarget += other.AltitudeDeepNoTarget;
        AltitudeGapCeiling += other.AltitudeGapCeiling;
        for (int i = 0; i < AltitudeGapGrades.Length; i++)
        {
            AltitudeGapGrades[i] += other.AltitudeGapGrades[i];
        }

        AltitudeNegativeTracked += other.AltitudeNegativeTracked;
        AltitudeNegativeSkipped += other.AltitudeNegativeSkipped;
        AltitudePositive += other.AltitudePositive;
        AltitudeIdle += other.AltitudeIdle;
        ChangeLatched += other.ChangeLatched;
        SnapshotCacheHit += other.SnapshotCacheHit;
        SnapshotRefreshed += other.SnapshotRefreshed;
        SnapshotHoleBytes += other.SnapshotHoleBytes;
        SnapshotNoWeaponSlot += other.SnapshotNoWeaponSlot;
        ShotSelectNotArmed += other.ShotSelectNotArmed;
        ShotSelectPerturbHit += other.ShotSelectPerturbHit;
        ShotSelectTerrain += other.ShotSelectTerrain;
        ShotSelectBranchA += other.ShotSelectBranchA;
        ShotSelectBranchB += other.ShotSelectBranchB;
        ShotSelectSweep += other.ShotSelectSweep;
        ShotSelectScriptKill += other.ShotSelectScriptKill;
        LeadEnableSet += other.LeadEnableSet;
    }

    /// <summary>The census as report lines, one arm per entry, zero-valued arms included.</summary>
    /// <returns>Name/count pairs in report order.</returns>
    public IEnumerable<(string Arm, long Count)> Lines()
    {
        yield return ("mode AL=0", ModeZero);
        yield return ("mode AL!=0", ModeNonZero);
        yield return ("heading HOLD", HeadingHold);
        yield return ("heading DIRECT", HeadingDirect);
        yield return ("heading BEARING", HeadingBearing);
        yield return ("heading BEARING-no-target", HeadingBearingNoTarget);
        yield return ("heading BIAS-", HeadingBiasMinus);
        yield return ("heading BIAS+", HeadingBiasPlus);
        yield return ("heading COMPLEX", HeadingComplex);
        yield return ("heading stepped", HeadingStepped);
        yield return ("elevation HOLD", ElevationHold);
        yield return ("elevation DIRECT", ElevationDirect);
        yield return ("elevation BEARING", ElevationBearing);
        yield return ("elevation BEARING-no-target", ElevationBearingNoTarget);
        yield return ("elevation BIAS-", ElevationBiasMinus);
        yield return ("elevation BIAS+", ElevationBiasPlus);
        yield return ("elevation COMPLEX", ElevationComplex);
        yield return ("elevation stepped", ElevationStepped);
        yield return ("proximity flag set", ProximityFlagSet);
        yield return ("elevation step +0xF0", ElevationStepBoosted);
        yield return ("elevation step >>1", ElevationStepHalved);
        yield return ("fuze HIT [0x0F0C]=1", FuzeHit);
        yield return ("fuze armed, no hit", FuzeArmedNoHit);
        yield return ("bank COMPLEX", BankComplex);
        yield return ("bank complex lead correction", BankLeadCorrection);
        yield return ("bank HOLD", BankHold);
        yield return ("bank DIRECT", BankDirect);
        yield return ("bank BIAS-", BankBiasMinus);
        yield return ("bank BIAS+", BankBiasPlus);
        yield return ("bank NO-REGIME (uninitialised local)", BankNoRegime);
        yield return ("arc decay gate open [0xB532]!=0", ArcDecayGateOpen);
        yield return ("arc decay applied", ArcDecayApplied);
        yield return ("envelope override", EnvelopeOverride);
        yield return ("altitude deep, no target", AltitudeDeepNoTarget);
        yield return ("altitude gap >= 0x1388", AltitudeGapCeiling);
        yield return ("altitude gap grade +0x32", AltitudeGapGrades[0]);
        yield return ("altitude gap grade +0x19", AltitudeGapGrades[1]);
        yield return ("altitude gap grade +5", AltitudeGapGrades[2]);
        yield return ("altitude gap grade -5", AltitudeGapGrades[3]);
        yield return ("altitude gap grade -0x19", AltitudeGapGrades[4]);
        yield return ("altitude negative tracked", AltitudeNegativeTracked);
        yield return ("altitude negative skipped", AltitudeNegativeSkipped);
        yield return ("altitude positive", AltitudePositive);
        yield return ("altitude idle", AltitudeIdle);
        yield return ("change latched [0xED59] bit2", ChangeLatched);
        yield return ("snapshot cache hit", SnapshotCacheHit);
        yield return ("snapshot refreshed", SnapshotRefreshed);
        yield return ("snapshot bytes in the DGROUP hole", SnapshotHoleBytes);
        yield return ("snapshot refused the no-weapon-slot sentinel", SnapshotNoWeaponSlot);
        yield return ("shot-select not armed", ShotSelectNotArmed);
        yield return ("shot-select perturb-cache hit", ShotSelectPerturbHit);
        yield return ("shot-select TERRAIN", ShotSelectTerrain);
        yield return ("shot-select branch A", ShotSelectBranchA);
        yield return ("shot-select branch B", ShotSelectBranchB);
        yield return ("shot-select lock-on sweep", ShotSelectSweep);
        yield return ("shot-select script kill", ShotSelectScriptKill);
        yield return ("lead-enable set [0xEDE0]=1", LeadEnableSet);
    }
}

/// <summary>
/// Everything one call of the manoeuvring geometry engine operates on: the combat register file,
/// the pool arena, the constant DGROUP tables and the world-grid seam.
/// </summary>
/// <remarks>
/// The engine is otherwise PURE — it draws no random numbers (verified: neither <c>0x066E4</c> nor
/// any function in its subtree appears in the site census, and the verification asserts
/// <c>[0x07A8]</c> unchanged across every call).
/// </remarks>
public sealed class EngagementGeometryContext
{
    /// <summary>The combat register file — the engine's whole DGROUP working set.</summary>
    public required CombatRegisters Registers { get; init; }

    /// <summary>The pool arena, for the acq target's and the player's objects.</summary>
    public required PoolArena Arena { get; init; }

    /// <summary>
    /// The constant DGROUP regions: the envelope table <c>[0x0F70]</c>, the rotate pivot
    /// <c>[0x0680]</c> and the engagement class prototypes.
    /// </summary>
    public required ICombatStaticData StaticData { get; init; }

    /// <summary>The world-grid seam; the tripwire by default.</summary>
    public ITerrainProximity Terrain { get; init; } = UnavailableTerrainProximity.Instance;

    /// <summary>The target-snapshot seam; the real routine by default.</summary>
    public ITargetSnapshot Snapshot { get; init; } = TargetSnapshotAccumulator.Instance;

    /// <summary>The per-arm census this run fills.</summary>
    public EngagementGeometryCensus Census { get; } = new();

    /// <summary>The named view of <see cref="Registers"/>.</summary>
    public EngagementAngleView View => new(Registers);
}
