using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Combat.Geometry;

/// <summary>
/// <c>engagement_slot_angle_update @image@0x066E4</c> — the manoeuvring engine: one engagement's heading, elevation
/// and bank integrated one step toward the targets the behaviour layer asked for, plus the fire-position accumulator
/// and the proximity fuze.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  1,622 bytes and the single biggest non-SMC function in the game's combat cluster; it has exactly two
/// doors — the per-shot FSM (<c>image@0x041EE</c>) and the evasion bearing setup (<c>image@0x04A34</c>), both C3b's.
/// </para>
/// <para>
/// <b>The shape.</b>  Each of the three attitude axes is driven by a SENTINEL-CODED source word
/// (<c>[0xED81]</c> heading, <c>[0xED83]</c> elevation, <c>[0xED85]</c> bank) whose value is either
/// a literal angle or one of five commands.  The vocabulary, byte-verified at
/// <c>image@0x06710</c> / <c>0x0686D</c> / <c>0x06B12</c>:
/// </para>
/// <list type="table">
///   <item><term><c>0x7FFE</c></term><description>HOLD — keep the current angle.</description></item>
///   <item><term><c>&lt; 0x1680</c></term><description>a literal angle.</description></item>
///   <item><term><c>[0x1680, 0x21C0)</c></term><description>BEARING to the acquisition target,
///     offset by <c>source − 0x1C20</c>.</description></item>
///   <item><term><c>0x21C0</c></term><description>bias the current angle by <c>−0x550</c> (−45°).</description></item>
///   <item><term><c>0x21C8</c></term><description>bias it by <c>+0x550</c>.</description></item>
///   <item><term>anything else</term><description>COMPLEX — decode the source words as an intercept
///     offset, rotate it and take the bearing to the result.</description></item>
/// </list>
/// <para>
/// The measured elevation convention applies throughout: <c>0</c> is LEVEL, <c>0x2D0</c> is straight UP and
/// <c>≈0x870</c> is down.
/// </para>
/// <para>
/// <b>RNG:</b> none.
/// </para>
/// <para>
/// Source of truth: the original's bytes <c>image@0x066E4..0x06D39</c> (disassembled
///).
/// </para>
/// </remarks>
public static class EngagementSlotAngleUpdate
{
    /// <summary>The HOLD sentinel — keep the axis where it is.</summary>
    public const short HoldSentinel = 0x7FFE;

    /// <summary>The first non-literal source value.</summary>
    public const short LiteralLimit = 0x1680;

    /// <summary>The first command value past the bearing range.</summary>
    public const short BearingLimit = 0x21C0;

    /// <summary>The offset subtracted from a bearing-range source.</summary>
    public const short BearingBias = 0x1C20;

    /// <summary>The bias-left / bias-down command.</summary>
    public const short BiasNegativeCommand = 0x21C0;

    /// <summary>The bias-right / bias-up command.</summary>
    public const short BiasPositiveCommand = 0x21C8;

    /// <summary>The angle a bias command applies — 45°.</summary>
    public const short BiasAngle = 0x0550;

    /// <summary>The COMPLEX arm's intercept-offset base (<c>image@0x0679D</c>).</summary>
    public const short ComplexOffsetBase = 0x5140;

    /// <summary>The bank source that also selects the complex bank regime (<c>image@0x06A56</c>).</summary>
    public const short BankComplexSentinel = 0x7FFF;

    /// <summary>
    /// Runs one update.
    /// </summary>
    /// <param name="context">The geometry context.</param>
    /// <param name="mode">
    /// The original's <c>AL</c>.  Non-zero runs the change-detection epilogue, which latches
    /// <c>[0xED59]</c> bit2 when an axis actually moved and no bias flag explains it.
    /// </param>
    public static void Step(EngagementGeometryContext context, byte mode)
    {
        ArgumentNullException.ThrowIfNull(context);
        EngagementAngleView view = context.View;
        EngagementGeometryCensus census = context.Census;

        if (mode == 0)
        {
            census.ModeZero++;
        }
        else
        {
            census.ModeNonZero++;
        }

        view.HitResult = 0;                                                 // image@0x066EC
        context.Snapshot.Refresh(context, isHitCheck: false);                // image@0x066F1

        short savedHeading = view.Heading;                                  // image@0x066F4
        view.SavedHeading = savedHeading;                                   // image@0x066F7
        short savedElevation = view.Elevation;
        view.SavedElevation = savedElevation;                              // image@0x066FE

        // The four frame flags the epilogue reads (image@0x06702 zeroes all four).
        bool bankBiased = false;                                            // [bp-0x2C]
        bool elevationBiased = false;                                       // [bp-0x20]
        bool headingBiased = false;                                         // [bp-0x06]
        bool headingComplex = false;                                        // [bp-0x22]

        // ── phase 1: the heading target ────────────────────────────────────────────────────────
        short headingTarget;
        CombatPosition interceptLocal = default;
        short heldSource = view.HeadingSource;

        if (heldSource == HoldSentinel)                                     // image@0x06710
        {
            census.HeadingHold++;
            headingTarget = savedHeading;
        }
        else if (heldSource < LiteralLimit)                                 // image@0x0671E
        {
            census.HeadingDirect++;
            headingTarget = heldSource;
        }
        else if (heldSource < BearingLimit)                                 // image@0x0672C
        {
            if (view.AcquisitionTarget == 0)                                // image@0x06734
            {
                census.HeadingBearingNoTarget++;
                headingTarget = savedHeading;
            }
            else
            {
                census.HeadingBearing++;
                short bearing = CombatGeometry.Bearing2d(
                    view.FirePosition, view.InterceptPosition);             // image@0x0675B
                headingTarget = unchecked((short)(bearing + heldSource - BearingBias));
                LeadSet(context, view.InterceptPosition);                   // image@0x06831
            }
        }
        else if (heldSource == BiasNegativeCommand)                         // image@0x06772
        {
            census.HeadingBiasMinus++;
            headingTarget = unchecked((short)(savedHeading - BiasAngle));
            headingBiased = true;                                           // image@0x06780
        }
        else if (heldSource == BiasPositiveCommand)                         // image@0x06788
        {
            census.HeadingBiasPlus++;
            headingTarget = unchecked((short)(savedHeading + BiasAngle));
            headingBiased = true;
        }
        else
        {
            census.HeadingComplex++;
            headingComplex = true;                                          // image@0x06796
            interceptLocal = BuildInterceptOffset(context, out short bearing);
            headingTarget = bearing;
            LeadSet(context, interceptLocal);                               // image@0x06831
        }

        // ── phase 2: wrap the heading target and turn toward it ────────────────────────────────
        short headingWrapped = unchecked((short)Angle.Wrap(headingTarget).Units);   // image@0x06837
        short headingError = Angle.Normalize(
            unchecked((short)(headingWrapped - view.Heading)));             // image@0x06843

        if (headingError != 0)                                              // image@0x0684B
        {
            census.HeadingStepped++;
            // image@0x06851: a NEGATIVE error takes the PLAIN stub and a positive one the
            // negate-and-wrap stub — the asymmetry is the machine's.
            short rate = headingError < 0
                ? EngagementAngleSteppers.BankAngleScalePlain(view)          // image@0x06858
                : EngagementAngleSteppers.BankAngleScaleNegated(view);       // image@0x06853
            view.Heading = AngleStep.Toward(view.Heading, headingWrapped, rate);
        }

        // ── phase 3: the elevation target ──────────────────────────────────────────────────────
        short elevationTarget;
        short elevationSource = view.ElevationSource;

        if (elevationSource == HoldSentinel)                                // image@0x0686D
        {
            census.ElevationHold++;
            elevationTarget = view.Elevation;
        }
        else if (elevationSource < LiteralLimit)                            // image@0x06875
        {
            census.ElevationDirect++;
            elevationTarget = elevationSource;
        }
        else if (elevationSource < BearingLimit)                            // image@0x06884
        {
            if (view.AcquisitionTarget == 0)                                // image@0x0688C
            {
                census.ElevationBearingNoTarget++;
                elevationTarget = view.Elevation;
            }
            else
            {
                census.ElevationBearing++;
                short bearing = CombatGeometry.Elevation3d(
                    view.FirePosition, view.InterceptPosition);             // image@0x068CA
                elevationTarget = unchecked((short)(bearing + elevationSource - BearingBias));
                // image@0x068D9 stages [0xED42] itself, so this compare is the fire position
                // against ITSELF: distance 0, and the lead flag is ALWAYS set on this arm.
                LeadSet(context, view.FirePosition);
            }
        }
        else if (elevationSource == BiasNegativeCommand)                    // image@0x068E2
        {
            census.ElevationBiasMinus++;
            elevationTarget = unchecked((short)(view.Elevation - BiasAngle));
            elevationBiased = true;                                         // image@0x068F3
        }
        else if (elevationSource == BiasPositiveCommand)                    // image@0x068FA
        {
            census.ElevationBiasPlus++;
            elevationTarget = unchecked((short)(view.Elevation + BiasAngle));
            elevationBiased = true;
        }
        else
        {
            census.ElevationComplex++;
            // image@0x0690A reads the ipos LOCALS the heading COMPLEX arm staged.  When heading
            // did not take that arm they are the previous call's stack residue — a shipped
            // read-of-uninitialised-local.  The port reproduces the dependency but refuses to
            // invent the residue.
            if (!headingComplex)
            {
                throw new EngagementGeometrySeamException(
                    "engagement_slot_angle_update's COMPLEX elevation arm @image@0x0690A reads the "
                        + "intercept locals [bp-0x1C..-0x12] that only the COMPLEX heading arm "
                        + "stages; this call did not take that arm, so the original would read the "
                        + "previous call's stack residue.  0 of 27,177 P6 calls on the six v1.1 "
                        + "reference windows reach this.");
            }

            elevationTarget = CombatGeometry.Elevation3d(view.FirePosition, interceptLocal);
        }

        // ── phase 4: wrap, let the range selector have the last word, measure the error ────────
        short elevationWrapped = unchecked((short)Angle.Wrap(elevationTarget).Units);   // image@0x06943
        short elevationSelected = ShotAngleSelect.Select(
            context, elevationWrapped, out bool lockOn, ref elevationBiased);           // image@0x0694D
        short elevationError = Angle.Normalize(
            unchecked((short)(elevationSelected - view.Elevation)));                    // image@0x06957

        // ── the lead/proximity flag (image@0x0695F..0x06993) ───────────────────────────────────
        bool proximity =
            view.TargetMatch != 0
            && view.PlayerObject == view.AcquisitionTarget
            && Abs16(headingError) <= Angle.QuarterCircle
            && Abs16(elevationError) <= Angle.QuarterCircle;
        if (proximity)
        {
            census.ProximityFlagSet++;
        }

        // ── the elevation step (image@0x06994..0x069C5) ────────────────────────────────────────
        if (elevationError != 0)
        {
            census.ElevationStepped++;
            short step = EngagementAngleSteppers.BankSinScale(view);        // image@0x0699A
            if (lockOn)                                                     // image@0x069A0
            {
                census.ElevationStepBoosted++;
                step = unchecked((short)(step + 0xF0));                     // image@0x069A6
            }
            else if (proximity && elevationError < 0)                       // image@0x069AE
            {
                census.ElevationStepHalved++;
                step = unchecked((short)(step >> 1));                       // image@0x069BA
            }

            EngagementAngleSteppers.ElevationStepToward(view, elevationSelected, step);
        }

        ProximityFuze(context, headingError, elevationError);

        // ── phase 5: the bank target ───────────────────────────────────────────────────────────
        short bankTarget = BankTarget(context, headingError, headingComplex, elevationError, ref bankBiased);

        // ── phase 5b: wrap and turn (image@0x06B53..0x06B6D) ───────────────────────────────────
        short bankWrapped = unchecked((short)Angle.Wrap(bankTarget).Units);
        short bankRate = view.BankStepRate;
        if (bankRate > 0x0168)                                              // image@0x06B62
        {
            bankRate = 0x0168;
        }

        EngagementAngleSteppers.BankStepToward(view, bankWrapped, bankRate);

        ArcDecay(context, savedHeading, savedElevation);
        EnvelopeOverride(context, proximity);
        AltitudeDispatch(context);

        // ── the tail: walk the fire position along the arc (image@0x06CFD, 100% of calls) ──────
        EngagementSpeed.AccumulateFirePosition(context);

        // ── phase 9: change detection (image@0x06D00..0x06D34) ─────────────────────────────────
        if (mode == 0)
        {
            return;
        }

        if (ChainContinues(headingWrapped, view.Heading, headingBiased)
            && ChainContinues(elevationSelected, view.Elevation, elevationBiased)
            && ChainContinues(bankWrapped, view.Bank, bankBiased))
        {
            census.ChangeLatched++;
            view.SlotFlags = (byte)(view.SlotFlags | 4);                    // image@0x06D30
        }
    }

    /// <summary>
    /// The COMPLEX heading arm (<c>image@0x06796..0x0682D</c>): decode the three source words as an
    /// offset from the intercept snapshot, rotate the horizontal pair, and take the bearing to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pair <c>([0xED81] − 0x5140, [0xED85] − 0x5140)</c> is rotated about the DGROUP pivot
    /// <c>[0x0680]/[0x0682]</c> by the target's own heading <c>[0xEDC2]</c>
    /// (<c>angle_vec2_rotate_inplace @image@0x184DE</c>, <c>image@0x067B8</c>) — i.e. the offset is
    /// expressed in the TARGET's frame and rotated into the world's.  For the
    /// <c>0x184DE</c> reconciliation.
    /// </para>
    /// <para>
    /// Each axis is then <c>offset &lt;&lt; 8</c> added to the corresponding snapshot word
    /// (<c>image@0x067BD</c>, <c>0x067D9</c>, <c>0x067F8</c>), which is why an offset unit is 1/256
    /// of a world unit here.  The Y axis takes <c>[0xED83] − 0x5140</c> and is NOT rotated.
    /// </para>
    /// </remarks>
    private static CombatPosition BuildInterceptOffset(
        EngagementGeometryContext context, out short bearing)
    {
        EngagementAngleView view = context.View;

        short offsetX = unchecked((short)(view.HeadingSource - ComplexOffsetBase));   // image@0x0679D
        short offsetZ = unchecked((short)(view.BankSource - ComplexOffsetBase));      // image@0x067A6
        short pivotX = unchecked((short)context.StaticData.Word(0x0680));
        short pivotY = unchecked((short)context.StaticData.Word(0x0682));
        Vector2Rotate.RotateInPlace(
            view.InterceptHeading, pivotX, pivotY, ref offsetX, ref offsetZ);          // image@0x067B8

        short offsetY = unchecked((short)(view.ElevationSource - ComplexOffsetBase));  // image@0x067DC

        CombatPosition position = new CombatPosition(
            unchecked(view.InterceptX + (offsetX << 8)),                               // image@0x067C9
            unchecked(view.InterceptY + (offsetY << 8)),                               // image@0x067EA
            unchecked(view.InterceptZ + (offsetZ << 8)));                              // image@0x06804

        bearing = CombatGeometry.Bearing2d(view.FirePosition, position);               // image@0x06826
        return position;
    }

    /// <summary>
    /// The 4-D proximity fuze (<c>image@0x069C6..0x06A4F</c>) — the one place this function sets
    /// <c>g_engagement_hit_result [0x0F0C]</c>.
    /// </summary>
    /// <remarks>
    /// Five gates in series, each tighter than the last: the script must be idle
    /// (<c>[0xED76] == -1</c>), the lead flag up, the acquisition target must BE the player, the
    /// phase must be 4 or 0x0C, and both attitude errors must be inside <c>0x50</c> (10°).  Then
    /// the player's own attitude has to be nearly HEAD-ON — the heading difference at least
    /// <c>0x528</c> (165°) and the elevation sum within <c>0xA0</c> (20°) — before a 3-axis distance
    /// under <c>0x9C4</c> finally scores the hit.
    /// </remarks>
    private static void ProximityFuze(
        EngagementGeometryContext context, short headingError, short elevationError)
    {
        EngagementAngleView view = context.View;
        if (view.ScriptProgramCounter != -1)                                // image@0x069C6
        {
            return;
        }

        if (view.LeadEnable == 0                                            // image@0x069D0
            || view.PlayerObject != view.AcquisitionTarget                  // image@0x069DA
            || (view.SlotPhase != 4 && view.SlotPhase != 0x0C)              // image@0x069E0
            || Abs16(headingError) > 0x50                                   // image@0x069F6
            || Abs16(elevationError) > 0x50)                                // image@0x06A03
        {
            context.Census.FuzeArmedNoHit++;
            return;
        }

        ushort player = view.PlayerObject;
        short playerHeading = unchecked((short)context.Arena.Word((ushort)(player + 0x12)));
        short headingDelta = Angle.Normalize(
            unchecked((short)(view.Heading - playerHeading)));              // image@0x06A0F
        if (Abs16(headingDelta) < 0x0528)                                   // image@0x06A1D
        {
            context.Census.FuzeArmedNoHit++;
            return;
        }

        short playerElevation = unchecked((short)context.Arena.Word((ushort)(player + 0x14)));
        // image@0x06A2A ADDS the two elevations — a head-on pass has them equal and opposite.
        short elevationDelta = Angle.Normalize(
            unchecked((short)(playerElevation + view.Elevation)));
        if (Abs16(elevationDelta) > 0xA0)                                   // image@0x06A38
        {
            context.Census.FuzeArmedNoHit++;
            return;
        }

        ushort distance = FirePosGeometry.Distance3dScaled(
            view.FirePosition,
            ObjectPosition(context.Arena, view.AcquisitionTarget),
            applySentinelBias: false,
            0,
            0,
            0);                                                             // image@0x06A43
        if (distance <= 0x09C4)                                             // image@0x06A46 jbe
        {
            context.Census.FuzeHit++;
            view.HitResult = 1;                                             // image@0x06A4B
        }
        else
        {
            context.Census.FuzeArmedNoHit++;
        }
    }

    /// <summary>
    /// Phase 5 (<c>image@0x06A50..0x06B52</c>) — the bank target, in two regimes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The COMPLEX regime (taken when the heading arm was complex, or the bank source is
    /// <c>0x7FFF</c>) makes bank a pure function of the heading error: <c>−4 × error</c>, clamped
    /// into <c>±[0xED96]</c> and further to <c>±0xF0</c> whenever the elevation error is large
    /// (<c>image@0x06AE9</c> — don't roll hard while pulling hard).  With the lead flag up and the
    /// bank already small it instead LEVELS relative to the target's own heading, with a 0x28
    /// dead-band (<c>image@0x06A86..0x06ADA</c>).
    /// </para>
    /// <para>
    /// The SIMPLE regime is the same five-command vocabulary as the other two axes.  A source that
    /// matches NO command falls through to <c>image@0x06B53</c> and the original then reads the
    /// UNINITIALISED local <c>[bp-0x26]</c>; the port refuses instead
    /// (<see cref="EngagementGeometryCensus.BankNoRegime"/> counts the arm and the caller sees the
    /// bank left alone).
    /// </para>
    /// </remarks>
    private static short BankTarget(
        EngagementGeometryContext context,
        short headingError,
        bool headingComplex,
        short elevationError,
        ref bool bankBiased)
    {
        EngagementAngleView view = context.View;
        EngagementGeometryCensus census = context.Census;
        short source = view.BankSource;

        if (!headingComplex && source != BankComplexSentinel)               // image@0x06A50/0x06A56
        {
            if (source == HoldSentinel)                                     // image@0x06B12
            {
                census.BankHold++;
                return view.Bank;
            }

            if (source < LiteralLimit)                                      // image@0x06B20
            {
                census.BankDirect++;
                return source;
            }

            if (source == BiasNegativeCommand)                              // image@0x06B2E
            {
                census.BankBiasMinus++;
                bankBiased = true;                                          // image@0x06B4F
                return unchecked((short)(view.Bank - BiasAngle));
            }

            if (source == BiasPositiveCommand)                              // image@0x06B3E
            {
                census.BankBiasPlus++;
                bankBiased = true;
                return unchecked((short)(view.Bank + BiasAngle));
            }

            census.BankNoRegime++;
            throw new EngagementGeometrySeamException(
                $"engagement_slot_angle_update's SIMPLE bank source 0x{source:X4} matches no "
                    + "regime, so image@0x06B53 reads the uninitialised local [bp-0x26].  0 of "
                    + "27,177 P6 calls on the six v1.1 reference windows reach this.");
        }

        census.BankComplex++;
        short bank = unchecked((short)-unchecked((short)(headingError << 2)));   // image@0x06A61
        short halfClamp = unchecked((short)(view.AngleClamp >> 1));              // image@0x06A6D

        if (view.LeadEnable != 0 && Abs16(bank) < halfClamp)                     // image@0x06A76
        {
            census.BankLeadCorrection++;
            short relative = Angle.Normalize(
                unchecked((short)(view.Heading - view.InterceptHeading)));       // image@0x06A89

            // image@0x06A95: fold the relative bearing into ±90° so that "behind" reads as the
            // mirrored approach, not as a 180° roll command.
            if (relative > Angle.QuarterCircle)
            {
                relative = unchecked((short)(Angle.HalfCircle - relative));      // image@0x06A9A
            }
            else if (relative < unchecked((short)0xFD30))
            {
                relative = unchecked((short)(0xFA60 - relative));                // image@0x06AA7
            }

            if (Abs16(relative) < 0x28)                                          // image@0x06AB8
            {
                bank = 0;                                                        // image@0x06ABD
            }
            else
            {
                bank = relative > 0
                    ? unchecked((short)(relative - 0x28))                        // image@0x06ACD
                    : unchecked((short)(relative + 0x28));                       // image@0x06AD5
            }
        }

        short clamp = view.AngleClamp;                                           // image@0x06ADB
        if (Abs16(elevationError) > 0x50 && clamp > 0xF0)                        // image@0x06AE9
        {
            clamp = 0xF0;                                                        // image@0x06AF5
        }

        if (bank > clamp)                                                        // image@0x06AFD
        {
            return clamp;
        }

        // image@0x06B08: the stored value on this arm is the NEGATED clamp.
        return unchecked((short)-clamp) > bank ? unchecked((short)-clamp) : bank;
    }

    /// <summary>
    /// Phase 6 (<c>image@0x06B6E..0x06BFE</c>) — bleed the arc accumulator by how far the attitude
    /// actually moved this call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gated entirely on <c>[0xB532]</c>, the arc TIER that
    /// <c>engagement_arc_param_update @image@0x0661C</c> stages (<see cref="EngagementArcParams"/>).
    /// The bleed is
    /// <c>tier × ((max(|Δheading|, |Δelevation|) &lt;&lt; 8) / [0xEDAE]) / [0xED8E] − 1</c>
    /// steps of <c>(([0xEDA9] &lt;&lt; 5) · [0xEDAE]) &gt;&gt; 8</c> each — i.e. an engagement that
    /// manoeuvres hard spends its arc budget faster.
    /// </para>
    /// </remarks>
    private static void ArcDecay(
        EngagementGeometryContext context, short savedHeading, short savedElevation)
    {
        EngagementAngleView view = context.View;
        if (view.ArcTier == 0)                                              // image@0x06B6E
        {
            return;
        }

        context.Census.ArcDecayGateOpen++;

        short headingMoved = Abs16(Angle.Normalize(
            unchecked((short)(view.Heading - savedHeading))));              // image@0x06B7B
        short elevationMoved = Abs16(Angle.Normalize(
            unchecked((short)(view.Elevation - savedElevation))));          // image@0x06B8F
        short moved = elevationMoved > headingMoved ? elevationMoved : headingMoved;

        short scaled = Fixed.IDiv16(moved << 8, view.NodeDt).Quotient;   // image@0x06BB8
        short steps = Fixed.MulDiv16Signed(view.ArcTier, scaled, view.BankScaleBase);
        short remaining = unchecked((short)(steps - 1));                    // image@0x06BD2 dec
        if (remaining <= 0)                                                 // image@0x06BD5 jle
        {
            return;
        }

        context.Census.ArcDecayApplied++;
        short unit = Fixed.MulDiv16SignedShr8(
            unchecked((short)(view.ArcStepRateByte << 5)), view.NodeDt);
        int total = Fixed.IMul16(remaining, unit);                          // image@0x06BF2
        view.SpeedQ8 = unchecked(view.SpeedQ8 - total);       // image@0x06BF7
    }

    /// <summary>
    /// Phase 7 (<c>image@0x06BFF..0x06C2C</c>) — rescale the arc DECREASE rate through the
    /// four-entry envelope table <c>[0x0F70]</c> when the engagement is close and pointing very
    /// high or very low.
    /// </summary>
    /// <remarks>
    /// The elevation window is the complement of <c>[0x5C8, 0xB18)</c>
    /// (<c>image@0x06C05</c>/<c>0x06C0D</c>, both SIGNED) — i.e. the override fires only when the
    /// engagement is pointing steeply UP (below <c>0x5C8</c>, which includes the <c>0x2D0</c>
    /// straight-up reading) or steeply DOWN.  The table index is <c>[0xED59] &amp; 3</c>, the slot's
    /// own weapon-state, and the multiplier is applied to <c>[0xEDA0]</c> in place.
    /// </remarks>
    private static void EnvelopeOverride(EngagementGeometryContext context, bool proximity)
    {
        if (!proximity)                                                     // image@0x06BFF
        {
            return;
        }

        EngagementAngleView view = context.View;
        bool high = view.Elevation >= 0x0B18;                               // image@0x06C05
        bool low = !high && view.Elevation < 0x05C8;                        // image@0x06C0D
        if (!high && !low)
        {
            return;
        }

        context.Census.EnvelopeOverride++;
        int index = view.SlotFlags & 3;                                     // image@0x06C15
        byte factor = context.StaticData.Byte(0x0F70 + index);              // image@0x06C1C
        view.ArcDecreaseRate = Fixed.MulDiv16SignedShr8(view.ArcDecreaseRate, factor);
    }

    /// <summary>
    /// Phase 8 (<c>image@0x06C2D..0x06CFC</c>) — pick the arc accumulator's target from the
    /// altitude delta <c>[0xED87]</c> and the acquisition target's own range band.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three regimes on the delta.  Below <c>0xFC18</c> (a deep dive command) the engine measures
    /// the real 3-axis distance to the target, subtracts the delta and a fixed <c>0x61A8</c>, and
    /// grades the remainder into five bands that nudge the target's own <c>+0x26</c> range by
    /// <c>+0x32 / +0x19 / +5 / −5 / −0x19</c> (<c>image@0x06C72..0x06CAD</c>) — a coarse
    /// range-keeping law.  A gap at or past <c>0x1388</c> abandons it and drives straight to the
    /// ceiling.
    /// </para>
    /// <para>
    /// A mildly NEGATIVE delta uses the target's range plus the delta plus <c>0x1F4</c>, but only
    /// when the target's class prototype has neither of the low two bits of its <c>+0x0C</c> word
    /// (<c>image@0x06CD6</c>); a POSITIVE delta uses the delta itself; zero does nothing at all.
    /// </para>
    /// </remarks>
    private static void AltitudeDispatch(EngagementGeometryContext context)
    {
        EngagementAngleView view = context.View;
        EngagementGeometryCensus census = context.Census;
        short delta = view.AltitudeDelta;

        if (delta < unchecked((short)0xFC18))                               // image@0x06C2D
        {
            ushort target = view.AcquisitionTarget;
            if (target == 0)                                                // image@0x06C35
            {
                census.AltitudeDeepNoTarget++;
                EngagementSpeed.StepTowardHeading(context);                  // image@0x06CEA
                return;
            }

            ushort block = context.Arena.EngagementBlockRef(target);        // image@0x06C43
            short range = unchecked((short)context.Arena.Word((ushort)(block + 0x26)));

            ushort distance = FirePosGeometry.Distance3dScaled(
                view.FirePosition,
                ObjectPosition(context.Arena, target),
                applySentinelBias: false,
                0,
                0,
                0);                                                         // image@0x06C59
            short gap = unchecked((short)(distance - delta - 0x61A8));      // image@0x06C5C

            if (gap >= 0x1388)                                              // image@0x06C66
            {
                census.AltitudeGapCeiling++;
                EngagementSpeed.StepTowardCeiling(context);                  // image@0x06C6B
                return;
            }

            (int grade, short adjust) = gap > 0x07D0 ? (0, (short)0x32)     // image@0x06C72
                : gap > 0x03E8 ? (1, (short)0x19)                           // image@0x06C80
                : gap > 0x0032 ? (2, (short)0x05)                           // image@0x06C8E
                : gap > unchecked((short)0xFFCE) ? (3, (short)-0x05)        // image@0x06C9C
                : (4, (short)-0x19);                                        // image@0x06CAA
            census.AltitudeGapGrades[grade]++;
            EngagementSpeed.DispatchClamped(context, unchecked((short)(range + adjust)));
            return;
        }

        if (delta < 0)                                                      // image@0x06CB2
        {
            ushort target = view.AcquisitionTarget;
            bool skip = target == 0;                                        // image@0x06CB9
            ushort block = 0;
            if (!skip)
            {
                block = context.Arena.EngagementBlockRef(target);           // image@0x06CC4
                ushort prototype = context.Arena.Word(block);               // image@0x06CD3
                skip = (context.StaticData.Byte(prototype + 0x0C) & 3) != 0;   // image@0x06CD6
            }

            if (skip)
            {
                census.AltitudeNegativeSkipped++;
                EngagementSpeed.StepTowardHeading(context);                  // image@0x06CEA
                return;
            }

            census.AltitudeNegativeTracked++;
            short range = unchecked((short)context.Arena.Word((ushort)(block + 0x26)));
            EngagementSpeed.DispatchClamped(
                context, unchecked((short)(range + delta + 0x01F4)));       // image@0x06CE0
            return;
        }

        if (delta > 0)                                                      // image@0x06CF0
        {
            census.AltitudePositive++;
            EngagementSpeed.DispatchClamped(context, delta);                 // image@0x06CF7
            return;
        }

        census.AltitudeIdle++;
    }

    /// <summary>
    /// One level of the change-detection chain (<c>image@0x06D06</c>, and twice more): the chain
    /// continues while each axis either did not move or has a bias flag explaining the move.
    /// </summary>
    private static bool ChainContinues(short wanted, short actual, bool biased) =>
        wanted == actual || biased;

    private static void LeadSet(EngagementGeometryContext context, CombatPosition block)
    {
        if (FirePosGeometry.ProximityLeadSet(context.View, block))
        {
            context.Census.LeadEnableSet++;
        }
    }

    private static short Abs16(short v) => v == short.MinValue ? v : (short)Math.Abs(v);

    private static CombatPosition ObjectPosition(PoolArena arena, ushort objectRef) => new(
        ReadInt32(arena, (ushort)(objectRef + 0x06)),
        ReadInt32(arena, (ushort)(objectRef + 0x0A)),
        ReadInt32(arena, (ushort)(objectRef + 0x0E)));

    private static int ReadInt32(PoolArena arena, ushort nearOffset) =>
        unchecked((int)(arena.Word(nearOffset) | ((uint)arena.Word((ushort)(nearOffset + 2)) << 16)));
}
