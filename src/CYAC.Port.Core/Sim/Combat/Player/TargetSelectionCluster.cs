using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Geometry;

namespace CYAC.Port.Core.Sim.Combat.Player;

/// <summary>
/// The six stack words <c>combat_target_range_and_angle_qualify @image@0x07C50</c> receives,
/// collapsed to the port's single-arena model (the original's three far pointers all carry the pool
/// segment <c>[0x0094]</c> at every one of its callers).
/// </summary>
/// <param name="Mode">
/// <c>[bp+4]</c> — non-zero adds the sight-line and fire-angle gates.  1 at
/// <c>weapon_fire_sight_line_check</c> (<c>image@0x033CB</c>),
/// <c>spawn_table_retarget_on_lock_change</c> (<c>image@0x035EA</c>) and both
/// <c>combat_target_qualify_from_globals</c> doors; the acquisition machine's own two doors also
/// pass 1 (<c>image@0x08200</c>, <c>image@0x082F8</c>).
/// </param>
/// <param name="CandidateRef">
/// <c>[bp+6]</c> — the object being qualified.  0 fails immediately (<c>image@0x07C58</c>).
/// </param>
/// <param name="ShooterRef"><c>[bp+0x0A]</c> — the shooter's object.</param>
/// <param name="AimRecordRef">
/// <c>[bp+0x0E]</c> — the AIM RECORD: the weapon-class descriptor at the two player doors, and the
/// class prototype's <c>+0x0E + 2·slot</c> entry at the AI doors.  Its <c>+0x00</c> is the kind
/// byte, <c>+0x04</c>/<c>+0x05</c> the min-range and max-angle gates, <c>+0x24</c> the flag byte and
/// <c>+0x28</c> the aim sub-record the sight line uses.
/// </param>
public readonly record struct TargetQualifyRequest(
    byte Mode, ushort CandidateRef, ushort ShooterRef, ushort AimRecordRef);

/// <summary>
/// The TARGET-SELECTION cluster — the P22/P23/P24 probes and the last un-ported callee of the AI's
/// acquisition state machine: <c>combat_target_score_and_fire @image@0x07952</c>,
/// <c>combat_target_range_and_angle_qualify @image@0x07C50</c>, <c>combat_target_qualify_from_globals
/// @image@0x07DBA</c>, <c>target_sight_line_check @image@0x07DE6</c>,
/// <c>target_engagement_eligibility_check @image@0x07904</c>, <c>target_type_filter_check
/// @image@0x078D0</c>, <c>engagement_subject_pos_manhattan_dist (ex-weapon_fire_pos_manhattan_dist)
/// @image@0x074B6</c> and <c>weapon_guidance_angle_track @image@0x08440</c>.
/// </summary>
/// <remarks>
/// <para>
/// C3b kept this whole cluster behind <see cref="ITargetSelection"/> because nothing probed it, and
/// paid for it with 513 unverifiable <c>enemy_target_acquisition_state_machine</c> calls (unchanged
/// through C0c and C0d).  C0d's ask B3 added the three probes; this file is what spends them.
/// </para>
/// <para>
/// <b>Two DEAD skill-tier arms, both the same shipped shape.</b> In <see cref="RangeAndAngleQualify"/>
/// the second block tests <c>([0xED59] &amp; 3) &gt;= 3</c> but is reached only when the first block's
/// <c>&gt;= 2</c> already failed (<c>image@0x07CD5</c> → <c>image@0x07CE6</c>), so it can never run;
/// in <see cref="ScoreAndFire"/> the same pattern appears with <c>&gt;= 1</c> then <c>&gt;= 2</c>
/// (<c>image@0x07B40</c> → <c>image@0x07B4A</c>).  Both are transliterated as written and tripwired,
/// not "cleaned up".
/// </para>
/// <para>
/// <b>the skill-tier block is PLAYER-ONLY.</b> <c>image@0x07B39</c> is inside the <c>cmp [0xc0],ax /
/// jne 0x7b5b</c> guard at <c>image@0x07B12</c>, so the tier reduction (like the chaff/flare decoy
/// halving beside it) applies only when the scored candidate IS the player object — the AI discounting
/// a shot aimed at the player, not a general skill scale.  It also subtracts <c>score &gt;&gt; 2</c>,
/// not <c>score &gt;&gt; 1</c>: the <c>jmp 0x7b56</c> at <c>image@0x07B47</c> lands on a SECOND <c>shr
/// ax,1</c>.  Both errors were invisible until trace format v1.4 put the scorer's own score and floor
/// on the P22 record (ask E2); they are what made C6's two OPEN P22 calls open.  See the comment at
/// the site.
/// </para>
/// </remarks>
public static class TargetSelectionCluster
{
    /// <summary>The penalty an out-of-régime candidate takes — <c>add word ptr [bp-0x28],0x1b58</c>.</summary>
    public const int RegimePenalty = 0x1B58;

    /// <summary>The "give up and re-scan later" score — <c>cmp word ptr [bp-0x28],0xea</c>.</summary>
    public const int AbandonScore = 0xEA;

    /// <summary>The "not good enough right now" score — <c>cmp word ptr [bp-0x28],0x75</c>.</summary>
    public const int RejectScore = 0x75;

    /// <summary>
    /// <c>combat_target_score_and_fire @image@0x07952</c> — P22.  Pick a target for the shooter, or
    /// answer 0 and leave a re-scan cooldown behind.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="preferExisting">The original's <c>AL</c>.</param>
    /// <param name="fireEnabled">The original's <c>DL</c>.</param>
    /// <param name="cooldown">
    /// The original's <c>BX</c> out-pointer: a re-scan cooldown in ticks, written on every arm that
    /// answers 0 (0 on a plain reject, <c>(rand(30)+30) &lt;&lt; 2</c> when nothing qualified at all,
    /// <c>(rand(10)+10) &lt;&lt; 2</c> when a target was found but scored out of reach).
    /// </param>
    /// <returns>The original's <c>AX</c>: the selected object's pool near offset, or 0.</returns>
    public static ushort ScoreAndFire(
        PlayerCombatContext context, bool preferExisting, bool fireEnabled, out short cooldown)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;
        PlayerCombatCensus census = context.Census;
        census.ScorerCalls++;
        cooldown = 0;

        ushort selected;
        ushort current = registers.Word(PlayerCombatOffsets.AcquisitionTarget);

        // image@0x0795D..0x07976 — an eligible current target is kept without any scoring.
        if (current != 0 && EligibilityCheck(context, current))
        {
            census.ScorerKeptTarget++;
            selected = current;
        }
        else if (registers.Word(PlayerCombatOffsets.ActiveTargetCount) == 1)
        {
            // image@0x0797A..0x079AD — a single candidate: the table's first entry, if eligible.
            census.ScorerSingleCandidate++;
            ushort block = registers.Word(PlayerCombatOffsets.ActiveTargetTable);
            ushort owner = arena.Word((ushort)(block + 0x02));
            if (!EligibilityCheck(context, owner))
            {
                cooldown = NoCandidateCooldown(context);
                return 0;
            }

            selected = owner;
        }
        else
        {
            // image@0x079BE..0x07A6D — the DOWNWARD walk over g_active_target_table.
            census.ScorerTableWalks++;
            selected = 0;
            int best = 0;
            // image@0x079C3..0x079CB — the cursor starts at `[0xEDE4] + 2·count`, which is the
            // LAST entry (`[0xEDE6] + 2·(count−1)`), and the loop TESTS-then-READS with the
            // decrement at its TAIL (image@0x07A52 / image@0x07A56).  Reading before the decrement
            // is what makes the last entry a candidate at all.
            int cursor = PlayerCombatOffsets.ActiveTargetTable - 2
                + (registers.Word(PlayerCombatOffsets.ActiveTargetCount) * 2);

            for (; unchecked((ushort)cursor) >= PlayerCombatOffsets.ActiveTargetTable; cursor -= 2)
            {
                ushort block = registers.Word(cursor);
                ushort owner = arena.Word((ushort)(block + 0x02));
                if (!EligibilityCheck(context, owner))
                {
                    continue;
                }

                census.ScorerCandidatesScored++;
                CombatObjectView candidate = new CombatObjectView(arena, owner);

                // image@0x079F9..0x07A0F — the HIGH word of the 2-D Manhattan distance from the
                // shooter's scratch position [0xED42].
                int score = ManhattanDistanceHighWord(context, candidate.Position);

                // image@0x07A12..0x07A37 — the régime penalty.
                bool penalise;
                if ((registers.Byte(PlayerCombatOffsets.EngagementSlotFlags) & 0x80) != 0
                    && (context.StaticData.Byte(arena.Word(block) + 0x0C) & 0x80) == 0)
                {
                    penalise = true;
                }
                else if ((registers.Byte(PlayerCombatOffsets.EngagementLoadState) & 1) == 0)
                {
                    penalise = false;
                }
                else
                {
                    penalise = registers.Word(PlayerCombatOffsets.PlayerObject) != owner;
                }

                if (penalise)
                {
                    census.ScorerPenalised++;
                    score = unchecked((short)(score + RegimePenalty));
                }

                // image@0x07A39..0x07A51 — the first candidate wins by default; later ones must
                // beat the best strictly (an UNSIGNED compare).
                if (selected == 0 || unchecked((ushort)score) < unchecked((ushort)best))
                {
                    selected = owner;
                    best = score;
                }
            }

            if (selected == 0)
            {
                cooldown = NoCandidateCooldown(context);
                return 0;
            }
        }

        return Score(context, selected, preferExisting, fireEnabled, out cooldown);
    }

    /// <summary><c>image@0x079B0..0x079BB</c> then <c>image@0x07BE9</c> — nothing qualified.</summary>
    private static short NoCandidateCooldown(PlayerCombatContext context) =>
        unchecked((short)((context.Random.RandBounded(0x1E) + 0x1E) << 2));

    /// <summary><c>image@0x07A75..0x07C4E</c> — the selected candidate's scoring and the fire gates.</summary>
    private static ushort Score(
        PlayerCombatContext context,
        ushort selected,
        bool preferExisting,
        bool fireEnabled,
        out short cooldown)
    {
        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;
        PlayerCombatCensus census = context.Census;
        cooldown = 0;

        CombatObjectView candidate = new CombatObjectView(arena, selected);
        ushort blockRef = candidate.EngagementBlockRef;     // image@0x07A78, the far thunk

        // image@0x07A83..0x07AB4 — a fresh HIGH-word Manhattan distance, this time in 3-D.
        CombatPosition position = candidate.Position;
        int score =
            Abs16(unchecked((short)(registers.Word(0xED44) - unchecked((short)(position.X >> 16)))))
            + Abs16(unchecked((short)(registers.Word(0xED4C) - unchecked((short)(position.Z >> 16)))))
            + Abs16(unchecked((short)(registers.Word(0xED48) - unchecked((short)(position.Y >> 16)))));
        score = unchecked((short)score);

        int weaponSlot;
        short floor;
        ushort aimRecord;

        if (preferExisting)
        {
            // image@0x07ABD..0x07AF0 — the ENGAGE régime: slot 1, the prototype's own +0x28 floor.
            weaponSlot = 1;
            ushort prototype = registers.Word(PlayerCombatOffsets.ActiveStatBlock);
            floor = context.StaticData.Byte(prototype + 0x28);
            aimRecord = 0x0F74;

            if (registers.Byte(PlayerCombatOffsets.AcquisitionState) == 1
                && (arena.Byte((ushort)(blockRef + 0x05)) & 8) != 0)
            {
                floor = unchecked((short)((floor >> 1) + floor));
                fireEnabled = false;      // image@0x07AEC — the DL argument is CLEARED in place.
            }
        }
        else
        {
            // image@0x07AF2..0x07B0C — the SEARCH régime: slot 2, a floor of 0x4E or 0x75.
            weaponSlot = 2;
            floor = registers.Word(PlayerCombatOffsets.AcquisitionTarget) == 0
                ? (short)0x4E
                : (short)0x75;
            aimRecord = unchecked((ushort)(registers.Word(PlayerCombatOffsets.ActiveStatBlock) + 0x29));
        }

        // image@0x07B0F..0x07B5A — the PLAYER-TARGET score adjustments.
        //
        // CORRECTED by C0e, from trace format v1.4's own oracle (ask E2, the scorer's score/floor
        // pair on the P22 EXIT record).  The bytes put BOTH inside the same guard:
        //
        //   image@0x07B0F  mov ax,[bp-0xa]        ; the selected candidate
        //   image@0x07B12  cmp [0xc0],ax          ; g_alt_object_farptr = the PLAYER
        //   image@0x07B16 jne 0x7b5b; NOT the player -> skip EVERYTHING to 0x7B5B
        //   image@0x07B18..0x07B36                ;   the decoy halving
        //   image@0x07B39..0x07B5A                ;   the skill-tier reduction
        //   image@0x07B5B  mov ax,[bp-0x28]       ; the join
        //
        // `image@0x07B39` is reachable ONLY from inside that branch — through the `jne 0x7b39` at
        // image@0x07B2B, the `jae 0x7b39` at image@0x07B34, or the fall-through from the halving at
        // image@0x07B36.  So the skill tier discounts the score of a shot aimed AT THE PLAYER, and
        // nothing else; scoring an AI candidate never touches it.
        //
        // …and the reduction is by a QUARTER, not a half: image@0x07B42 does `mov ax,[bp-0x28] / shr
        // ax,1` and then `jmp 0x7b56`, which lands ON A SECOND `shr ax,1` (image@0x07B56, shared
        // with the dead arm below) before `sub [bp-0x28],ax` at image@0x07B58.  Two shifts, so the
        // subtrahend is `score >> 2`.
        //
        // Measured, on the three v12_b3b_upright_det calls that made it visible:
        // the machine's own score at image@0x07B8E is 80 / 77 / 80 and the corrected port agrees on
        // all three; before the fix the port answered 40 / 77 / 40 and picked a target twice where
        // the machine picked none.
        if (registers.Word(PlayerCombatOffsets.PlayerObject) == selected)
        {
            ushort frame = registers.Word(PlayerCombatOffsets.MasterFrameCounter);
            bool halve =
                (weaponSlot == 2 && frame < registers.Word(PlayerCombatOffsets.FlareDecoyExpiry))
                || (weaponSlot == 1 && frame < registers.Word(PlayerCombatOffsets.ChaffDecoyExpiry));
            if (halve)
            {
                score = unchecked((ushort)score) >> 1;
            }

            // image@0x07B39..0x07B5A — the skill tier's score bonus.  The `>= 2` block is DEAD (see
            // the type's remarks); it is written out so the tripwire can fire if it ever runs.
            int tier = registers.Byte(PlayerCombatOffsets.EngagementSlotFlags) & 3;
            if (tier >= 1)
            {
                score = unchecked((short)(score - (unchecked((ushort)score) >> 2)));
            }
            else if (tier >= 2)
            {
                throw new InvalidOperationException(
                    "combat_target_score_and_fire's second skill-tier arm (image@0x07B53) ran: it "
                    + "is unreachable because the >= 1 test at image@0x07B40 subsumes it.");
            }
        }

        // image@0x07B5B..0x07B8B — the score is scaled by 64 and divided by the class's per-slot
        // divisor, prototype[+6 + slot] (C2's "proto +0x06 divisor").
        ushort prototypeRef = arena.Word(blockRef);
        byte divisor = context.StaticData.Byte(prototypeRef + 6 + weaponSlot);
        // image@0x07B5B..0x07B77: `sub dx,dx` then six `shl ax,1 / rcl dx,1` — a ZERO-extended
        // 32-bit shift, not a sign-extended one.
        score = Fixed.IDiv16(unchecked((int)((uint)(ushort)score << 6)), divisor).Quotient;

        // image@0x07B8E — the ONLY success path.  A score BELOW the floor means the target is close
        // enough to be worth shooting at, and control goes straight to the fire gates; a score at or
        // above it always answers 0, and the three arms below only decide HOW LONG the shooter waits
        // before scanning again.
        if (unchecked((ushort)score) < unchecked((ushort)floor))
        {
            return FireGates(context, selected, aimRecord, fireEnabled, out cooldown);
        }

        score = unchecked((short)(score - floor));

        // image@0x07B99..0x07BC2 — a second, 2-D score measured from the PLAYER's position.
        CombatObjectView player = new CombatObjectView(arena, registers.Word(PlayerCombatOffsets.PlayerObject));
        CombatPosition scratch = new CombatPosition(
            unchecked((int)(registers.Word(0xED42) | (registers.Word(0xED44) << 16))),
            0,
            unchecked((int)(registers.Word(0xED4A) | (registers.Word(0xED4C) << 16))));
        short playerScore = unchecked((short)(
            unchecked((short)(CombatGeometry.Proximity2d(scratch, player.Position) >> 16)) - floor));

        // image@0x07BC3..0x07BD0 — both scores hopeless: a long re-scan cooldown.
        if (unchecked((ushort)score) >= AbandonScore
            && unchecked((ushort)playerScore) >= AbandonScore)
        {
            cooldown = NoCandidateCooldown(context);
            return 0;
        }

        // image@0x07BD3..0x07BDC — either score inside the near band: no cooldown at all, so the
        // shooter re-scans on the very next pass.
        if (unchecked((ushort)score) < RejectScore || unchecked((ushort)playerScore) < RejectScore)
        {
            return 0;
        }

        // image@0x07BDE..0x07BF2 — the middle band: a short cooldown.
        census.ScorerCooldownDraws++;
        cooldown = unchecked((short)((context.Random.RandBounded(0x0A) + 0x0A) << 2));
        return 0;
    }

    /// <summary><c>image@0x07BF4..0x07C4E</c> — the sight line and the line-of-sight grid probe.</summary>
    private static ushort FireGates(
        PlayerCombatContext context,
        ushort selected,
        ushort aimRecord,
        bool fireEnabled,
        out short cooldown)
    {
        CombatRegisters registers = context.Registers;
        PlayerCombatCensus census = context.Census;
        cooldown = 0;

        if (fireEnabled)
        {
            census.ScorerSightLines++;
            if (!SightLine(
                context, selected, aimRecord, registers.Word(PlayerCombatOffsets.ShooterObject)))
            {
                return 0;
            }
        }

        // image@0x07C0B..0x07C35 — an eleven-word world-grid probe along the shot vector; a non-zero
        // answer means something is in the way.
        census.ScorerGridProbes++;
        CombatPosition from = new CombatPosition(
            unchecked((int)(registers.Word(0xED42) | (registers.Word(0xED44) << 16))),
            unchecked((int)(registers.Word(0xED46) | (registers.Word(0xED48) << 16))),
            unchecked((int)(registers.Word(0xED4A) | (registers.Word(0xED4C) << 16))));

        if (context.Events.ScorerLineOfSightQuery(
                from,
                unchecked((ushort)(selected + 6)),
                registers.Word(PlayerCombatOffsets.ShooterObject)) != 0)
        {
            return 0;
        }

        census.ScorerSelected++;
        return selected;
    }

    /// <summary>
    /// <c>target_engagement_eligibility_check @image@0x07904</c> — may this object be a target at
    /// all?
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="objectRef">The candidate's pool near offset.</param>
    /// <returns>The original's <c>AL</c>.</returns>
    public static bool EligibilityCheck(PlayerCombatContext context, ushort objectRef)
    {
        ArgumentNullException.ThrowIfNull(context);
        PoolArena arena = context.Arena;
        CombatObjectView candidate = new CombatObjectView(arena, objectRef);

        // image@0x07912 — the object must be ACTIVE.
        if ((candidate.Flags & 1) == 0)
        {
            return false;
        }

        ushort blockRef = candidate.EngagementBlockRef;
        byte flags = arena.Byte((ushort)(blockRef + 0x05));

        // image@0x0792E..0x0793F — the COALITION test is an XOR of the block's flag byte against
        // [0xED59]'s: bit6 must DIFFER (opposite sides), and bit5 must be clear.
        if (((flags ^ context.Registers.Byte(PlayerCombatOffsets.EngagementSlotFlags)) & 0x40) == 0)
        {
            return false;
        }

        if ((flags & 0x20) != 0)
        {
            return false;
        }

        return !TypeFilterRejects(context, blockRef);
    }

    /// <summary>
    /// <c>target_type_filter_check @image@0x078D0</c> — the two "not right now" filters.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="blockRef">The candidate's engagement block.</param>
    /// <returns>The original's <c>AL</c>: non-zero REJECTS the candidate.</returns>
    public static bool TypeFilterRejects(PlayerCombatContext context, ushort blockRef)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;

        if (registers.Word(PlayerCombatOffsets.PlayerEngagementBlock) == blockRef)
        {
            // image@0x078DC..0x078E8 — the player is off-limits while the cockpit is not flying.
            return unchecked((sbyte)registers.Byte(PlayerCombatOffsets.AircraftLoadAck)) > 0
                || registers.Byte(PlayerCombatOffsets.InputMode) != 0;
        }

        // image@0x078F2..0x078FA — phase 6 is "leaving"; nobody re-engages it.
        return context.Arena.Byte((ushort)(blockRef + 0x0D)) == 6;
    }

    /// <summary>
    /// <c>engagement_subject_pos_manhattan_dist @image@0x074B6</c> — the 32-bit 2-D Manhattan distance
    /// between the shooter's scratch position <c>[0xED42]</c>/<c>[0xED4A]</c> and a point.  The
    /// scorer keeps only its HIGH word (<c>mov [bp-0x28],dx</c> @<c>image@0x07A0F</c>).
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="position">The candidate's world position.</param>
    /// <returns>The high word of the distance, as the original's <c>DX</c>.</returns>
    public static short ManhattanDistanceHighWord(PlayerCombatContext context, CombatPosition position)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        int scratchX = unchecked((int)(registers.Word(0xED42) | (registers.Word(0xED44) << 16)));
        int scratchZ = unchecked((int)(registers.Word(0xED4A) | (registers.Word(0xED4C) << 16)));
        int dx = unchecked(scratchX - position.X);
        int dz = unchecked(scratchZ - position.Z);
        if (dx < 0)
        {
            dx = unchecked(-dx);
        }

        if (dz < 0)
        {
            dz = unchecked(-dz);
        }

        return unchecked((short)(unchecked(dx + dz) >> 16));
    }

    /// <summary>
    /// <c>combat_target_qualify_from_globals @image@0x07DBA</c> — P23: the six-argument wrapper the
    /// acquisition machine uses, which reads every argument but the mode from DGROUP.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="mode">The original's <c>AL</c>.</param>
    /// <returns>The original's <c>AL</c>.</returns>
    public static bool QualifyFromGlobals(PlayerCombatContext context, byte mode)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;

        // image@0x07DBC..0x07DDF — the aim record is prototype[+0x0E + 2·slot].
        int slot = registers.Byte(PlayerCombatOffsets.EngagementTypeSlot);
        ushort aimRecord = context.StaticData.Word(
            registers.Word(PlayerCombatOffsets.ActiveStatBlock) + 0x0E + (slot * 2));

        return RangeAndAngleQualify(context, new TargetQualifyRequest(
            Mode: mode,
            CandidateRef: registers.Word(PlayerCombatOffsets.AcquisitionTarget),
            ShooterRef: registers.Word(PlayerCombatOffsets.ShooterObject),
            AimRecordRef: aimRecord));
    }

    /// <summary>
    /// <c>combat_target_range_and_angle_qualify @image@0x07C50</c> — the range, divisor-scaled
    /// angle, régime and (in mode ≠ 0) sight-line and fire-angle gates.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="request">The six stack words.</param>
    /// <returns>The original's <c>AL</c>.</returns>
    public static bool RangeAndAngleQualify(
        PlayerCombatContext context, in TargetQualifyRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;
        ICombatStaticData statics = context.StaticData;
        context.Census.QualifyCalls++;

        // image@0x07C58..0x07C6C
        if (request.CandidateRef == 0)
        {
            {
                // The diagnostic names the REJECTING GATE, so a divergence in a
                // verification points at an instruction instead of a byte.
                context.Census.LastQualifyReject = "image@0x07C58";
                return false;
            }
        }

        CombatObjectView candidate = new CombatObjectView(arena, request.CandidateRef);
        if ((candidate.Flags & 1) == 0)
        {
            {
                // The diagnostic names the REJECTING GATE, so a divergence in a
                // verification points at an instruction instead of a byte.
                context.Census.LastQualifyReject = "image@0x07C6C";
                return false;
            }
        }

        // image@0x07C6E..0x07CB2 — a HIGH-word 3-axis Manhattan distance.
        CombatObjectView shooter = new CombatObjectView(arena, request.ShooterRef);
        CombatPosition a = shooter.Position;
        CombatPosition b = candidate.Position;
        short distance = unchecked((short)(
            Abs16(unchecked((short)((a.Z >> 16) - (b.Z >> 16))))
            + Abs16(unchecked((short)((a.Y >> 16) - (b.Y >> 16))))
            + Abs16(unchecked((short)((a.X >> 16) - (b.X >> 16))))));

        // image@0x07CB5..0x07CC5 — the aim record's two gate bytes.
        short angleLimit = statics.Byte(request.AimRecordRef + 5);
        if (unchecked((ushort)distance) < statics.Byte(request.AimRecordRef + 4))
        {
            {
                // The diagnostic names the REJECTING GATE, so a divergence in a
                // verification points at an instruction instead of a byte.
                context.Census.LastQualifyReject = "image@0x07CC5";
                return false;
            }
        }

        // image@0x07CC7..0x07CF1 — a skilled shooter judges the PLAYER closer than he is.  The
        // `>= 3` block at image@0x07CEF is DEAD for the same reason as the scorer's.
        if (request.CandidateRef == registers.Word(PlayerCombatOffsets.PlayerObject))
        {
            int tier = registers.Byte(PlayerCombatOffsets.EngagementSlotFlags) & 3;
            if (tier >= 2)
            {
                ushort raw = unchecked((ushort)distance);
                distance = unchecked((short)((raw >> 1) + (raw >> 2)));
            }
            else if (tier >= 3)
            {
                throw new InvalidOperationException(
                    "combat_target_range_and_angle_qualify's second skill-tier arm "
                    + "(image@0x07CEF) ran: it is unreachable because the >= 2 test at "
                    + "image@0x07CD5 subsumes it.");
            }
        }

        // image@0x07CF2..0x07D2C — scale by 64 and divide by the candidate prototype's per-kind
        // divisor, then compare against the aim record's angle gate.
        ushort prototypeRef = candidate.EngagementPrototypeRef;
        byte divisor = statics.Byte(prototypeRef + 6 + statics.Byte(request.AimRecordRef));
        // image@0x07D04..0x07D1F — the same ZERO-extended 32-bit shift.
        short scaled =
            Fixed.IDiv16(unchecked((int)((uint)(ushort)distance << 6)), divisor).Quotient;
        if (unchecked((ushort)scaled) > unchecked((ushort)angleLimit))
        {
            {
                // The diagnostic names the REJECTING GATE, so a divergence in a
                // verification points at an instruction instead of a byte.
                context.Census.LastQualifyReject = "image@0x07D2C";
                return false;
            }
        }

        // image@0x07D31..0x07D6D — the air-to-ground régime gates.
        if ((statics.Byte(request.AimRecordRef + 0x24) & WeaponFireScheduler.GuidedBit) != 0)
        {
            byte kind = statics.Byte(request.AimRecordRef);
            if (kind == 1)
            {
                // The SHOOTER's own block must carry +0x05 bit3.
                ushort shooterBlock = shooter.EngagementBlockRef;
                if ((arena.Byte((ushort)(shooterBlock + 0x05)) & 8) == 0)
                {
                    {
                        // The diagnostic names the REJECTING GATE, so a divergence in a
                        // verification points at an instruction instead of a byte.
                        context.Census.LastQualifyReject = "image@0x07D52";
                        return false;
                    }
                }
            }
            else if (kind == 0 && (statics.Byte(prototypeRef + 0x0C) & 4) == 0)
            {
                // A ground weapon may only engage a prototype flagged as a ground target.
                {
                    // The diagnostic names the REJECTING GATE, so a divergence in a
                    // verification points at an instruction instead of a byte.
                    context.Census.LastQualifyReject = "image@0x07D6B";
                    return false;
                }
            }
        }

        if (request.Mode == 0)
        {
            context.Census.QualifyPassed++;
            return true;
        }

        // image@0x07D74..0x07D8A — the sight line.
        if (!SightLine(
            context,
            request.CandidateRef,
            unchecked((ushort)(request.AimRecordRef + 0x28)),
            request.ShooterRef))
        {
            {
                // The diagnostic names the REJECTING GATE, so a divergence in a
                // verification points at an instruction instead of a byte.
                context.Census.LastQualifyReject = "image@0x07D88";
                return false;
            }
        }

        // image@0x07D8B..0x07DAE — and, for an air-to-ground gun, the fire-angle cone.
        if ((statics.Byte(request.AimRecordRef + 0x24) & WeaponFireScheduler.GuidedBit) != 0
            && statics.Byte(request.AimRecordRef) == 0
            && !FireAngleQualify(context, request.CandidateRef, request.ShooterRef))
        {
            {
                // The diagnostic names the REJECTING GATE, so a divergence in a
                // verification points at an instruction instead of a byte.
                context.Census.LastQualifyReject = "image@0x07DAC";
                return false;
            }
        }

        context.Census.QualifyPassed++;
        return true;
    }

    /// <summary>
    /// <c>target_sight_line_check @image@0x07DE6</c> — P24: is the candidate inside the aim
    /// record's cone, measured from the shooter's WEAPON axis rather than its airframe axis?
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="candidateRef">The original's <c>[bp+4]</c>.</param>
    /// <param name="aimRecordRef">The original's <c>[bp+6]</c>.</param>
    /// <param name="shooterRef">The original's <c>[bp+8]</c>.</param>
    /// <returns>The original's <c>AL</c>.</returns>
    /// <remarks>
    /// The routine SWAPS the shooter object's <c>+0x12</c>/<c>+0x14</c> orientation words with the
    /// weapon's own aim angles for the duration of the cone test and swaps them back afterwards —
    /// three <c>xor</c> exchanges each way (<c>image@0x07E14..0x07E40</c> and
    /// <c>image@0x07E5F..0x07E8B</c>).  The port does the same, so a verification sees the
    /// shooter's words unchanged at the exit but the cone measured from the weapon's axis.
    /// </remarks>
    public static bool SightLine(
        PlayerCombatContext context, ushort candidateRef, ushort aimRecordRef, ushort shooterRef)
    {
        ArgumentNullException.ThrowIfNull(context);
        PoolArena arena = context.Arena;
        PlayerCombatCensus census = context.Census;
        census.SightLineCalls++;

        // image@0x07DED..0x07DF6 — a 0x5A aim record short-circuits to TRUE.
        if (context.StaticData.Byte(aimRecordRef + 2) == 0x5A)
        {
            census.SightLineTagShortCircuits++;
            return true;
        }

        // image@0x07E09..0x07E11 — the weapon-corrected aim angles.
        CombatBearing aim = WeaponAimAngles(context, aimRecordRef, shooterRef);

        CombatObjectView shooter = new CombatObjectView(arena, shooterRef);
        short savedHeading = shooter.Heading;
        short savedElevation = shooter.Elevation;
        shooter.Heading = aim.Heading;
        shooter.Elevation = aim.Elevation;

        // image@0x07E41..0x07E5C — the cone half-angle is the aim record's +2 byte scaled by 16.
        bool inside = CombatGeometry.BearingConeCheck(
            shooter.Position,
            shooter.Heading,
            shooter.Elevation,
            new CombatObjectView(arena, candidateRef).Position,
            unchecked((short)(context.StaticData.Byte(aimRecordRef + 2) << 4)));

        shooter.Heading = savedHeading;
        shooter.Elevation = savedElevation;
        return inside;
    }

    /// <summary>
    /// <c>weapon_guidance_angle_track @image@0x08440</c> — the acquisition machine's STATE-4 aim
    /// solution: the heading and elevation the AI's shot is launched with.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="heading">The word the original leaves in its caller's <c>[bp-0x16]</c>.</param>
    /// <param name="elevation">The word it leaves in <c>[bp-0x14]</c>.</param>
    public static void GuidanceAngles(
        PlayerCombatContext context, out short heading, out short elevation)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;
        context.Census.GuidanceCalls++;

        // image@0x08449..0x0845C — the aim record is prototype[+0x0E + 2·slot], as in P23.
        int slot = registers.Byte(PlayerCombatOffsets.EngagementTypeSlot);
        ushort aimRecord = context.StaticData.Word(
            registers.Word(PlayerCombatOffsets.ActiveStatBlock) + 0x0E + (slot * 2));

        // image@0x0845F..0x08472 — the base solution: the weapon-corrected angles toward the target.
        ushort shooterRef = registers.Word(PlayerCombatOffsets.ShooterObject);
        CombatBearing solution = WeaponAimAngles(context, unchecked((ushort)(aimRecord + 0x28)), shooterRef);
        heading = solution.Heading;
        elevation = solution.Elevation;

        // image@0x08475..0x0847E — only a LEADING weapon (+0x24 & 0x20) runs the trajectory
        // accumulator and the lead step.
        if ((context.StaticData.Byte(aimRecord + 0x24) & 0x20) == 0)
        {
            return;
        }

        context.Census.GuidanceLeadArms++;
        context.Events.AccumulateShotTrajectory(1);

        // image@0x08486..0x084C2 — the lead bearing from the scratch position to the accumulated
        // closest-approach point [0xEDCE]/[0xEDD2]/[0xEDD6].
        CombatPosition from = new CombatPosition(
            unchecked((int)(registers.Word(0xED42) | (registers.Word(0xED44) << 16))),
            unchecked((int)(registers.Word(0xED46) | (registers.Word(0xED48) << 16))),
            unchecked((int)(registers.Word(0xED4A) | (registers.Word(0xED4C) << 16))));
        CombatPosition to = new CombatPosition(
            unchecked((int)(registers.Word(0xEDCE) | (registers.Word(0xEDD0) << 16))),
            unchecked((int)(registers.Word(0xEDD2) | (registers.Word(0xEDD4) << 16))),
            unchecked((int)(registers.Word(0xEDD6) | (registers.Word(0xEDD8) << 16))));
        CombatBearing lead = CombatGeometry.BearingAndElevation(from, to);

        // image@0x084C3..0x08509 — the weapon-corrected version of the lead, then a bounded step
        // toward it whose rate is the aim record's +0x2A byte scaled by 16.
        CombatBearing corrected = WeaponAimAngles(context, unchecked((ushort)(aimRecord + 0x28)), shooterRef);
        short rate = unchecked((short)(context.StaticData.Byte(aimRecord + 0x2A) << 4));
        heading = AngleStep.Toward(corrected.Heading, lead.Heading, rate);
        elevation = AngleStep.Toward(corrected.Elevation, lead.Elevation, rate);
        _ = arena;
    }

    /// <summary>
    /// <c>target_angle_wrap_and_combine @image@0x07E96</c> — the weapon-corrected aim direction:
    /// where a weapon whose aim record says "straight ahead" or "dead astern" is actually pointing,
    /// given the shooter's attitude.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="aimSubRecordRef">The original's <c>BX</c> — the aim record's <c>+0x28</c> block.</param>
    /// <param name="shooterRef">The pushed far pointer's offset half.</param>
    /// <returns>The pair the original writes through its <c>AX</c> and <c>DX</c> out-pointers.</returns>
    /// <remarks>
    /// Four cases, in the order the machine tests them:
    /// <list type="number">
    /// <item><description>
    /// <c>(0, 0)</c> — a nose gun: the shooter's own <c>+0x12</c>/<c>+0x14</c> verbatim
    /// (<c>image@0x07EA0..0x07EBB</c>).
    /// </description></item>
    /// <item><description>
    /// <c>(0x5A, 0)</c> — a TAIL gun: heading + 180°, elevation NEGATED, both wrapped
    /// (<c>image@0x07EBE..0x07EEB</c>).
    /// </description></item>
    /// <item><description>
    /// the shooter's PITCH or ROLL inside <c>[0x0F0, 0xA50]</c> — the full Euler composition, the
    /// renderer's <c>angle_3d_orientation_combine @image@0x1C122</c> (<c>image@0x07F11..0x07F43</c>).
    /// Closed by C8 — <see cref="OrientationCombine.Combine"/>; the
    /// seam member <c>IPlayerCombatEvents.OrientationCombine</c> and its 1,097-byte driver
    /// </description></item>
    /// <item><description>
    /// otherwise (near-level) — the linear shortcut: heading + <c>aim[0] &lt;&lt; 4</c> wrapped, and
    /// <c>aim[1] &lt;&lt; 4</c> as the elevation, with NO wrap on the second
    /// (<c>image@0x07F48..0x07F73</c>).
    /// </description></item>
    /// </list>
    /// </remarks>
    public static CombatBearing WeaponAimAngles(
        PlayerCombatContext context, ushort aimSubRecordRef, ushort shooterRef)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatObjectView shooter = new CombatObjectView(context.Arena, shooterRef);
        byte aimHeadingByte = context.StaticData.Byte(aimSubRecordRef);
        byte aimPitchByte = context.StaticData.Byte(aimSubRecordRef + 1);

        if (aimHeadingByte == 0 && aimPitchByte == 0)
        {
            return new CombatBearing(shooter.Heading, shooter.Elevation);
        }

        if (aimHeadingByte == 0x5A && aimPitchByte == 0)
        {
            return new CombatBearing(
                unchecked((short)Angle.Wrap(
                    unchecked((short)(shooter.Heading + Angle.HalfCircle))).Units),
                unchecked((short)Angle.Wrap(unchecked((short)-shooter.Elevation)).Units));
        }

        // image@0x07EEE..0x07F0F — the two attitude windows.  Both compares are SIGNED.
        short roll = unchecked((short)context.Arena.Word((ushort)(shooterRef + 0x16)));
        bool matrix = (shooter.Elevation >= 0x00F0 && shooter.Elevation <= 0x0A50)
            || (roll >= 0x00F0 && roll <= 0x0A50);

        if (matrix)
        {
            // image@0x07F11..0x07F43 — the full Euler composition.  The eight pushed words are the
            // three out-pointers, the two aim bytes ZERO-extended and scaled by 16
            // (image@0x07F1D..0x07F2E) and the shooter's three orientation words.  The third
            // out-pointer is this routine's own [bp-2] (image@0x07F11) and is never read again
            // after the call returns (image@0x07F3E..0x07F43), so the BANK is discarded here.
            context.Census.GuidanceMatrixCases++;
            OrientationCombine.Result combined = OrientationCombine.Combine(
                shooter.Heading,
                shooter.Elevation,
                roll,
                unchecked((short)(aimHeadingByte << 4)),
                unchecked((short)(aimPitchByte << 4)));
            return new CombatBearing(combined.Heading, combined.Elevation);
        }

        return new CombatBearing(
            unchecked((short)Angle.Wrap(
                unchecked((short)((aimHeadingByte << 4) + shooter.Heading))).Units),
            unchecked((short)(aimPitchByte << 4)));
    }

    /// <summary>
    /// <c>target_fire_angle_qualify @image@0x0383A</c> — the final fire-angle cone of
    /// <see cref="RangeAndAngleQualify"/>, measured in <c>0xB40</c> angle space.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="candidateRef">The original's <c>[bp+4]</c>.</param>
    /// <param name="shooterRef">The original's <c>[bp+8]</c>.</param>
    /// <returns>The original's <c>AL</c>.</returns>
    /// <remarks>
    /// The ELEVATION window is tested first (<c>±0x118</c>, ≈ 25°); a shooter pointing within 25° of
    /// straight UP (<c>0x2D0</c>) or straight DOWN (<c>0x870</c>) then passes outright, because
    /// heading is meaningless there — the same escape <see cref="CombatGeometry.BearingConeCheck"/>
    /// has, at a different threshold.  Otherwise the heading window must also hold.
    /// </remarks>
    public static bool FireAngleQualify(
        PlayerCombatContext context, ushort candidateRef, ushort shooterRef)
    {
        ArgumentNullException.ThrowIfNull(context);
        PoolArena arena = context.Arena;
        CombatObjectView shooter = new CombatObjectView(arena, shooterRef);
        CombatObjectView candidate = new CombatObjectView(arena, candidateRef);

        short delta = Fold(unchecked((short)(shooter.Elevation - candidate.Elevation)));
        if (delta > 0x118)
        {
            return false;
        }

        if (Abs16(unchecked((short)(shooter.Elevation - Angle.QuarterCircle))) < 0x118)
        {
            return true;
        }

        if (Abs16(unchecked((short)(shooter.Elevation - Angle.ThreeQuarterCircle))) < 0x118)
        {
            return true;
        }

        return Fold(unchecked((short)(shooter.Heading - candidate.Heading))) <= 0x118;

        static short Fold(short raw)
        {
            short folded = Abs16(raw);
            return folded > Angle.HalfCircle
                ? unchecked((short)(Angle.FullCircle - folded))
                : folded;
        }
    }

    /// <summary>The 16-bit <c>cwd / xor / sub</c> absolute value the cluster uses everywhere.</summary>
    private static short Abs16(short value) => value < 0 ? unchecked((short)-value) : value;
}
