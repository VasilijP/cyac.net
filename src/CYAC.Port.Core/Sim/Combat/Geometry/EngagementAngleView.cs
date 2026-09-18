namespace CYAC.Port.Core.Sim.Combat.Geometry;

/// <summary>
/// A named view of the DGROUP working set the manoeuvring geometry engine
/// (<c>engagement_slot_angle_update @image@0x066E4</c> and its ~20 leaves) reads and writes.
/// </summary>
/// <remarks>
/// <para>
/// INT-only: every field here decides where an enemy points and therefore whether it hits.  The view is byte-backed
/// over <see cref="CombatRegisters"/> rather than a record of C# fields for the reason C1 gives for the register file
/// itself — these globals OVERLAY each other. The clearest case is the engagement SPEED (ex-"the arc accumulator",
/// R1): <c>[0xED79]</c> is its low word, <c>[0xED7B]</c> its high word, and <c>[0xED7A]</c> is a THIRD,
/// independently-addressed word straddling the two (<c>image@0x04A74</c> reads <c>[0xED7A]</c> for the ceiling test
/// while <c>image@0x04A90</c> updates <c>[0xED79]/[0xED7B]</c>).  With a flat store that aliasing is free and exact.
/// </para>
/// <para>
/// Source of truth: the original's bytes.
/// </para>
/// </remarks>
/// <param name="registers">The combat register file the view addresses.</param>
public readonly struct EngagementAngleView(CombatRegisters registers)
{
    private readonly CombatRegisters _registers =
        registers ?? throw new ArgumentNullException(nameof(registers));

    // ── DGROUP offsets, all byte-verified────────────────────────────────────────────

    /// <summary><c>g_engagement_hit_result [0x0F0C]</c> — the proximity-fuze verdict this call writes.</summary>
    public const int HitResultOffset = 0x0F0C;

    /// <summary><c>g_object_pool_segment [0x0094]</c>.</summary>
    public const int PoolSegmentOffset = 0x0094;

    /// <summary><c>g_alt_object_farptr_off [0x00C0]</c> — the player's pool object.</summary>
    public const int PlayerObjectOffset = 0x00C0;

    /// <summary><c>g_engagement_subject_copy_pos_x_lo (ex-g_weapon_fire_pos_x_lo) [0xED42]</c> — the fire-position triple's first i32.</summary>
    public const int FirePositionOffset = 0xED42;

    /// <summary><c>g_engagement_slot_heading [0xED4E]</c>.</summary>
    public const int HeadingOffset = 0xED4E;

    /// <summary>
    /// <c>[0xEDB0..0xEDC7]</c> — the REFERENCE 24-byte snapshot of the acq target's pool object
    /// (<c>shot_trajectory_proximity_accum</c>'s second <c>rep movsw</c>, <c>image@0x085AB</c>).
    /// </summary>
    public const int TargetSnapshotRefOffset = 0xEDB0;

    /// <summary>
    /// <c>[0xEDC8..0xEDDF]</c> — the CURRENT 24-byte snapshot, copied straight from the pool
    /// (<c>image@0x0859A</c>) and then copied on to <see cref="TargetSnapshotRefOffset"/>.
    /// </summary>
    public const int TargetSnapshotCurOffset = 0xEDC8;

    /// <summary>How many bytes each snapshot holds — <c>rep movsw</c> with <c>CX = 0x0C</c>.</summary>
    public const int TargetSnapshotBytes = 24;

    /// <summary>The 6-key snapshot cache <c>[0xB53E..0xB547]</c> (<c>image@0x08519</c>).</summary>
    public const int SnapshotCacheOffset = 0xB53E;

    /// <summary>The register file this view addresses.</summary>
    public CombatRegisters Registers => _registers;

    // ── the sentinel-coded targets ──────────────────────────────────────────────────────────────

    /// <summary><c>g_engagement_heading_src [0xED81]</c> — the sentinel-coded heading source.</summary>
    public short HeadingSource
    {
        get => Signed(0xED81);
        set => SetSigned(0xED81, value);
    }

    /// <summary><c>g_engagement_elevation_src [0xED83]</c>.</summary>
    public short ElevationSource
    {
        get => Signed(0xED83);
        set => SetSigned(0xED83, value);
    }

    /// <summary><c>g_engagement_bank_src [0xED85]</c>.</summary>
    public short BankSource
    {
        get => Signed(0xED85);
        set => SetSigned(0xED85, value);
    }

    /// <summary><c>g_engagement_altitude_delta [0xED87]</c>.</summary>
    public short AltitudeDelta
    {
        get => Signed(0xED87);
        set => SetSigned(0xED87, value);
    }

    // ── the integrated attitude triple ──────────────────────────────────────────────────────────

    /// <summary><c>g_engagement_slot_heading [0xED4E]</c>.</summary>
    public short Heading
    {
        get => Signed(HeadingOffset);
        set => SetSigned(HeadingOffset, value);
    }

    /// <summary><c>g_engagement_slot_elevation [0xED50]</c>.</summary>
    public short Elevation
    {
        get => Signed(0xED50);
        set => SetSigned(0xED50, value);
    }

    /// <summary><c>g_engagement_slot_bank [0xED52]</c>.</summary>
    public short Bank
    {
        get => Signed(0xED52);
        set => SetSigned(0xED52, value);
    }

    // ── the fire-position accumulator [0xED42..0xED4D] ──────────────────────────────────────────

    /// <summary><c>[0xED42]</c> — the weapon fire position's X, signed 32-bit.</summary>
    public int FirePositionX
    {
        get => Int32(FirePositionOffset);
        set => SetInt32(FirePositionOffset, value);
    }

    /// <summary><c>[0xED46]</c> — its Y (ALTITUDE).</summary>
    public int FirePositionY
    {
        get => Int32(0xED46);
        set => SetInt32(0xED46, value);
    }

    /// <summary><c>[0xED4A]</c> — its Z.</summary>
    public int FirePositionZ
    {
        get => Int32(0xED4A);
        set => SetInt32(0xED4A, value);
    }

    /// <summary>The fire position as a triple.</summary>
    public CombatPosition FirePosition
    {
        get => new(FirePositionX, FirePositionY, FirePositionZ);
        set
        {
            FirePositionX = value.X;
            FirePositionY = value.Y;
            FirePositionZ = value.Z;
        }
    }

    // ── the target snapshot (SnapRef), read as the intercept position ───────────────────────────

    /// <summary>The reference snapshot's <c>+0x06</c> — the target's X (<c>[0xEDB6]</c>).</summary>
    public int InterceptX => Int32(0xEDB6);

    /// <summary>Its <c>+0x0A</c> — the target's Y / altitude (<c>[0xEDBA]</c>).</summary>
    public int InterceptY => Int32(0xEDBA);

    /// <summary>Its <c>+0x0E</c> — the target's Z (<c>[0xEDBE]</c>).</summary>
    public int InterceptZ => Int32(0xEDBE);

    /// <summary>The intercept position as a triple.</summary>
    public CombatPosition InterceptPosition => new(InterceptX, InterceptY, InterceptZ);

    /// <summary>
    /// Its <c>+0x12</c> — the target's HEADING word (<c>[0xEDC2]</c>).  it is the snapshot's
    /// orientation field, which is why the complex-bank arm subtracts it from our own heading.
    /// </summary>
    public short InterceptHeading => Signed(0xEDC2);

    // ── the engagement SPEED (ex-"arc accumulator") and its ARC envelope ────────────────────────

    /// <summary>
    /// <c>[0xED79..0xED7C]</c> — the engagement's Q8 SPEED, signed 32-bit
    /// (<c>g_engagement_speed_q8_lo_word</c>/<c>_hi_word</c>; ex-"the Q8.8 arc accumulator", R1).
    /// Aliased at byte granularity with <see cref="SpeedIntegerPart"/>.
    /// </summary>
    public int SpeedQ8
    {
        get => Int32(0xED79);
        set => SetInt32(0xED79, value);
    }

    /// <summary>
    /// <c>g_engagement_speed_int_word [0xED7A]</c> (ex-<c>g_engagement_arc_accum_lo</c>, R1) — the
    /// OVERLAPPING middle word (bits 8..23 of <see cref="SpeedQ8"/>), i.e. the speed's INTEGER part,
    /// which is what every arc ceiling/floor comparison reads.
    /// </summary>
    public short SpeedIntegerPart => Signed(0xED7A);

    /// <summary><c>g_engagement_arc_floor [0xED98]</c>.</summary>
    public short ArcFloor
    {
        get => Signed(0xED98);
        set => SetSigned(0xED98, value);
    }

    /// <summary><c>g_engagement_arc_heading_lo [0xED9A]</c>.</summary>
    public short ArcHeading
    {
        get => Signed(0xED9A);
        set => SetSigned(0xED9A, value);
    }

    /// <summary><c>g_engagement_arc_ceiling [0xED9C]</c>.</summary>
    public short ArcCeiling
    {
        get => Signed(0xED9C);
        set => SetSigned(0xED9C, value);
    }

    /// <summary><c>[0xED9E]</c> — the arc INCREASE rate (the range ceiling <c>arc_param_update</c> stages).</summary>
    public short ArcIncreaseRate
    {
        get => Signed(0xED9E);
        set => SetSigned(0xED9E, value);
    }

    /// <summary><c>g_engagement_angle_override [0xEDA0]</c> — the arc DECREASE rate.</summary>
    public short ArcDecreaseRate
    {
        get => Signed(0xEDA0);
        set => SetSigned(0xEDA0, value);
    }

    /// <summary><c>[0xB52E]</c> — the arc PRE-DECAY rate (<c>image@0x04A7A</c>).</summary>
    public short ArcDecayRate => Signed(0xB52E);

    /// <summary>
    /// <c>g_engagement_node_dt_i16 [0xEDAE]</c> (ex-<c>g_enemy_spawn_angle_divisor</c>, R1 — it is a
    /// per-node dt, <c>[0xF0D2] − [0xED5D]</c>, not a divisor) — the shared Q8 rate scale.
    /// </summary>
    public short NodeDt
    {
        get => Signed(0xEDAE);
        set => SetSigned(0xEDAE, value);
    }

    // ── the bank / clamp parameter block [0xED8E..0xEDAA] ───────────────────────────────────────

    /// <summary><c>g_engagement_bank_scale_base [0xED8E]</c>.</summary>
    public short BankScaleBase
    {
        get => Signed(0xED8E);
        set => SetSigned(0xED8E, value);
    }

    /// <summary><c>[0xED90]</c> — the bank step rate, clamped to <c>0x168</c> at the call site.</summary>
    public short BankStepRate => Signed(0xED90);

    /// <summary><c>g_engagement_evasion_heading_max [0xED92]</c>.</summary>
    public short EvasionHeadingMax => Signed(0xED92);

    /// <summary><c>g_engagement_angle_clamp [0xED96]</c>.</summary>
    public short AngleClamp => Signed(0xED96);

    /// <summary><c>g_engagement_range_ref [0xEDA2]</c>.</summary>
    public short RangeReference => Signed(0xEDA2);

    /// <summary><c>g_engagement_arc_desc_w16 [0xEDA4] (ex-g_engagement_actual_window, H2)</c>.</summary>
    public ushort ActualWindow => _registers.Word(0xEDA4);

    /// <summary><c>g_acq_weapon_type_ref [0xEDA6]</c>.</summary>
    public byte ArcParamA => _registers.Byte(0xEDA6);

    /// <summary><c>[0xEDA7]</c> — the arc-param interpolation input.</summary>
    public byte ArcParamB => _registers.Byte(0xEDA7);

    /// <summary><c>[0xEDA8]</c> — the arc-param floor input.</summary>
    public byte ArcParamC => _registers.Byte(0xEDA8);

    /// <summary><c>[0xEDA9]</c> — the Phase-6 per-step rate byte.</summary>
    public byte ArcStepRateByte => _registers.Byte(0xEDA9);

    // ── slot state ──────────────────────────────────────────────────────────────────────────────

    /// <summary><c>g_weapon_desc_table_ptr [0xED54]</c>.</summary>
    public ushort WeaponDescriptorTable => _registers.Word(0xED54);

    /// <summary><c>g_engagement_player_slot_nearptr [0xED56]</c>.</summary>
    public ushort PlayerSlotReference => _registers.Word(0xED56);

    /// <summary><c>g_engagement_slot_flags [0xED59]</c> — bit2 is the "attitude changed" latch.</summary>
    public byte SlotFlags
    {
        get => _registers.Byte(0xED59);
        set => _registers.SetByte(0xED59, value);
    }

    /// <summary><c>[0xED5A]</c> — the load-state byte; bit1 caps the arc target at <c>0xFA</c>.</summary>
    public byte LoadState => _registers.Byte(0xED5A);

    /// <summary><c>g_engagement_fsm_loop_count [0xED62]</c>.</summary>
    public ushort FsmLoopCount
    {
        get => _registers.Word(0xED62);
        set => _registers.SetWord(0xED62, value);
    }

    /// <summary><c>g_engagement_slot_phase [0xED61]</c>.</summary>
    public byte SlotPhase => _registers.Byte(0xED61);

    /// <summary><c>g_engagement_type_slot_idx [0xED64]</c>.</summary>
    public byte TypeSlotIndex => _registers.Byte(0xED64);

    /// <summary><c>g_acq_current_target [0xED6F]</c> — the acq target's pool near offset.</summary>
    public ushort AcquisitionTarget => _registers.Word(0xED6F);

    /// <summary><c>g_engagement_script_pc [0xED76]</c> — an i16; <c>-1</c> arms the fuze/select paths.</summary>
    public short ScriptProgramCounter
    {
        get => Signed(0xED76);
        set => SetSigned(0xED76, value);
    }

    /// <summary><c>g_engagement_timer_init [0xED78]</c> — bit6 also arms the select path.</summary>
    public byte TimerInit => _registers.Byte(0xED78);

    /// <summary><c>[0xED7F]</c> — the terrain perturbation cache byte.</summary>
    public byte PerturbCache
    {
        get => _registers.Byte(0xED7F);
        set => _registers.SetByte(0xED7F, value);
    }

    /// <summary><c>g_engagement_lead_enable [0xEDE0]</c>.</summary>
    public byte LeadEnable
    {
        get => _registers.Byte(0xEDE0);
        set => _registers.SetByte(0xEDE0, value);
    }

    /// <summary><c>g_engagement_target_match [0xEDE4]</c>.</summary>
    public byte TargetMatch => _registers.Byte(0xEDE4);

    /// <summary><c>g_engagement_hit_result [0x0F0C]</c>.</summary>
    public byte HitResult
    {
        get => _registers.Byte(HitResultOffset);
        set => _registers.SetByte(HitResultOffset, value);
    }

    /// <summary><c>[0xB530]</c> — the heading this call saw on entry (written by phase 0).</summary>
    public short SavedHeading
    {
        get => Signed(0xB530);
        set => SetSigned(0xB530, value);
    }

    /// <summary><c>[0xB536]</c> — the elevation this call saw on entry.</summary>
    public short SavedElevation
    {
        get => Signed(0xB536);
        set => SetSigned(0xB536, value);
    }

    /// <summary>
    /// <c>[0xB532]</c> — the arc TIER <c>engagement_arc_param_update</c> stages
    /// (<c>image@0x06676</c>); zero disables the phase-6 arc decay entirely.
    /// </summary>
    public short ArcTier
    {
        get => Signed(0xB532);
        set => SetSigned(0xB532, value);
    }

    /// <summary><c>g_object_pool_segment [0x0094]</c>.</summary>
    public ushort PoolSegment => _registers.Word(PoolSegmentOffset);

    /// <summary><c>g_alt_object_farptr_off [0x00C0]</c> — the player's pool near offset.</summary>
    public ushort PlayerObject => _registers.Word(PlayerObjectOffset);

    /// <summary><c>g_frame_time_accum_lo [0xF0D2]</c> / <c>_hi [0xF0D4]</c> as one 32-bit value.</summary>
    public int FrameTime => Int32(0xF0D2);

    /// <summary>A signed word of the register file.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    public short Signed(int dgroupOffset) => unchecked((short)_registers.Word(dgroupOffset));

    /// <summary>Writes a signed word of the register file.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    /// <param name="value">The value.</param>
    public void SetSigned(int dgroupOffset, short value) =>
        _registers.SetWord(dgroupOffset, unchecked((ushort)value));

    /// <summary>A signed 32-bit value (two little-endian words) of the register file.</summary>
    /// <param name="dgroupOffset">The DGROUP offset of the low word.</param>
    public int Int32(int dgroupOffset) =>
        unchecked((int)(_registers.Word(dgroupOffset) | ((uint)_registers.Word(dgroupOffset + 2) << 16)));

    /// <summary>Writes a signed 32-bit value.</summary>
    /// <param name="dgroupOffset">The DGROUP offset of the low word.</param>
    /// <param name="value">The value.</param>
    public void SetInt32(int dgroupOffset, int value)
    {
        _registers.SetWord(dgroupOffset, unchecked((ushort)value));
        _registers.SetWord(dgroupOffset + 2, unchecked((ushort)(value >> 16)));
    }
}
