namespace CYAC.Port.Core.Sim.Combat.Player;

/// <summary>
/// The two RENDER-PHASE gate bytes <c>g_lockon_enable_flag [0x00B8]</c> and <c>g_hud_engagement_label_gate
/// [0x00B9]</c>: computed every frame immediately before the render dispatch (<c>image@0x01656..0x0168C</c>) and
/// CLEARED immediately after it (<c>image@0x016E8..0x016ED</c>), inside <c>mission_state_machine</c>'s FULL render
/// arm.
/// </summary>
/// <remarks>
/// <para>
/// The chain, byte for byte:
/// </para>
/// <code>
/// 01656 cmp byte [0xE46F],0; the VIEW-mode flag (scanner: g_view_mode_bit0_flag since, ex-g_weapon_fire_active_flag — a misnomer,
/// 0165B  je  0x166B              ;   its ONLY writer is publish_view_mode @image@0x23875,
/// 0165D  cmp byte [0xC31C],0     ;   `[0xE46F] = [0xF12A] &amp; 1`)
/// 01662 je 0x166B; [0xC31C] = the derived in-flight KEY GATE
/// 01664  mov byte [0xB8],1
/// 01669  jmp 0x1670
/// 0166B  mov byte [0xB8],0
/// 01670  cmp byte [0xE46F],0
/// 01675  je  0x168C
/// 01677  cmp byte [0xC31C],0
/// 0167C  je  0x168C
/// 0167E  cmp byte [0xB1],0       ; g_target_info_visible (cfg@0x1B, the Ctrl-T toggle)
/// 01683  je  0x168C
/// 01685  mov byte [0xB9],1
/// 0168A  jmp 0x1691
/// 0168C  mov byte [0xB9],0
///   … the render dispatch, then polygon_fill_mesh_render_setup @0x146B8 whose
///   `cmp byte [0xb8],0` @image@0x14B80 is the LOCK-ON's only door …
/// 016E8  sub al,al
/// 016EA  mov [0xB9],al
/// 016ED  mov [0xB8],al
/// </code>
/// <para>
/// Both bytes therefore read 0 on EVERY stage record: CS8's trap is at <c>image@0x0109F</c>, before the chain, and
/// CS9's at <c>image@0x01708</c>, after the clear.  That is why a driver seeded at CS8 ran the lock-on zero times
/// while the machine ran it 79,675 times over the recordings — the gate is a WITHIN-FRAME transient and has to be
/// COMPUTED, never read.
/// </para>
/// <para>
/// Neither byte is touched on the SHORT render arm (<c>cmp word [0xc320],0xc / jne</c>
/// @<c>image@0x01570</c>, whose arm ends <c>jmp 0x1703</c> @<c>image@0x01595</c>), so on those frames
/// they keep the 0 the previous full arm's clear left.
/// </para>
/// </remarks>
public static class LockOnGate
{
    /// <summary>
    /// <c>[0xE46F]</c> — the first term of both gates (<c>image@0x01656</c>, <c>image@0x01670</c>).
    /// </summary>
    /// <remarks>
    /// The bytes say otherwise — renamed <c>g_view_mode_bit0_flag</c>. Its SOLE writer image-wide is
    /// <c>publish_view_mode @image@0x23875</c> (<c>mov al,[0xF12A] / and al,1 / mov [0xE46F],al</c>), i.e. it is bit
    /// 0 of the VIEW-mode flag word <c>[0xF12A]</c>. Reported to the parent; the port does not rename
    /// a scanner symbol on its own.
    /// </remarks>
    public const int ViewModeBit0Flag = 0xE46F;

    /// <summary>
    /// <c>g_in_flight_key_gate [0xC31C]</c> — the second term, the derived latch
    /// <c>([0xEE58]==0 &amp;&amp; [0xC32F]==0 &amp;&amp; [0xC316]&lt;2)</c> the frame body recomputes at
    /// <c>image@0x00DE2..0x00DFE</c> (K10; C6 §4.1 row 1).
    /// </summary>
    public const int InFlightKeyGate = 0xC31C;

    /// <summary><c>g_target_info_visible [0x00B1]</c> — <c>[0x00B9]</c>'s third term.</summary>
    public const int TargetInfoVisible = 0x00B1;

    /// <summary><c>g_lockon_enable_flag [0x00B8]</c>.</summary>
    public const int LockOnEnable = 0x00B8;

    /// <summary><c>g_hud_engagement_label_gate [0x00B9]</c>.</summary>
    public const int HudEngagementLabelGate = 0x00B9;

    /// <summary>
    /// Runs <c>image@0x01656..0x0168C</c>: writes both gate bytes and answers whether the lock-on
    /// door at <c>image@0x14B80</c> opens this frame.
    /// </summary>
    /// <param name="registers">The DGROUP combat register file.</param>
    /// <returns><see langword="true"/> when <c>[0x00B8]</c> ends up 1.</returns>
    public static bool Compute(CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);

        bool view = registers.Byte(ViewModeBit0Flag) != 0;          // image@0x01656 / 0x01670
        bool keys = registers.Byte(InFlightKeyGate) != 0;           // image@0x0165D / 0x01677
        bool lockOn = view && keys;                                 // image@0x01664 / 0x0166B
        bool label = lockOn && registers.Byte(TargetInfoVisible) != 0;   // image@0x0167E

        registers.SetByte(LockOnEnable, lockOn ? (byte)1 : (byte)0);
        registers.SetByte(HudEngagementLabelGate, label ? (byte)1 : (byte)0);
        return lockOn;
    }

    /// <summary>
    /// <c>g_view_mode [0xC320]</c> — the published view id, read by the republish guard.
    /// </summary>
    public const int ViewMode = 0xC320;

    /// <summary><c>[0xC32E]</c> — the guard's "radar view already taken" latch.</summary>
    public const int ViewModeRadarLatch = 0xC32E;

    /// <summary><c>[0x00C8] g_radar_track_slot</c> — the first guard arm's third term.</summary>
    public const int RadarTrackSlot = 0x00C8;

    /// <summary><c>[0x00CA]</c> — the second guard arm's second term.</summary>
    public const int RadarTrackAlt = 0x00CA;

    /// <summary><c>[0x00BE]</c> — the third guard arm's first term (the last lock).</summary>
    public const int LastLockedTarget = 0x00BE;

    /// <summary><c>g_view_flag_byte_cached [0xF12A]</c> — <c>viewModeFlagTable[view]</c>.</summary>
    public const int ViewFlagsCached = 0xF12A;

    /// <summary>
    /// The constant DGROUP byte table <c>publish_view_mode</c> indexes with the view id — <c>mov
    /// al,[si+0x2BC0] / mov [0xF12A],al</c> (<c>image@0x23859</c>).  The table is data of the
    /// shipped image and is not quoted here; the recordings reach its entries <c>0x0F</c> and
    /// <c>0x00</c>.
    /// </summary>
    public const int ViewModeFlagTable = 0x2BC0;

    /// <summary>
    /// <c>view_mode_radar_track_guard @image@0x238BE</c> — the FIRST call <c>mission_state_machine</c>
    /// makes after CS8's own trap (<c>lcall</c> @<c>image@0x0109F</c>), and the reason a gate seeded from
    /// a CS8 record can be one frame behind.
    /// </summary>
    /// <param name="registers">The DGROUP combat register file, seeded at CS8.</param>
    /// <returns>The view id the guard republishes, or <see langword="null"/> when no arm fires.</returns>
    /// <remarks>
    /// <para>
    /// Three arms, evaluated in order, each ending in <c>publish_view_mode</c>
    /// (<c>lcall 0x32aa:0xcca</c> = <c>image@0x2376A</c>) with the view id in <c>AX</c>:
    /// </para>
    /// <code>
    /// 238BE  cmp [0xC320],0x0F / jne… cmp byte [0xC32E],0 / je… cmp [0x00C8],0 / je…
    /// 238D3  mov ax,0x0F / lcall publish_view_mode
    /// 238DB  cmp [0xC320],0x0F / jne… cmp [0x00CA],0 / jne…
    /// 238E9  sub ax,ax  / lcall publish_view_mode / mov byte [0xC32E],1
    /// 238F5  cmp [0x00BE],0 / jne… test byte [0xF12A],8 / je…
    /// 23903  sub ax,ax  / lcall publish_view_mode
    /// 2390A  retf
    /// </code>
    /// <para>
    /// Every one of the six inputs is inside an existing trace window
    /// (<c>player_bounds_and_view_state [0xC30C]+37</c> for <c>[0xC320]</c>/<c>[0xC32E]</c>,
    /// <c>session_and_hud_selection_flags [0x00AC]+32</c> for <c>[0x00BE]</c>/<c>[0x00C8]</c>/
    /// <c>[0x00CA]</c>, <c>frame_time_and_kill_tally_block [0xF0BA]+114</c> for <c>[0xF12A]</c>),
    /// so the DECISION is portable even though the view subsystem is not.
    /// </para>
    /// <para>
    /// The port models the guard's decision ONLY — the value of <c>[0xE46F]</c> the gate then reads.
    /// <c>publish_view_mode</c>'s other effects (the view anchor, <c>[0xC320]</c>, <c>[0xF1B6]</c>, its
    /// jump-table arm and its three <c>lcall</c>s) stay in the render-phase external channel, because
    /// this file does not own the view subsystem.  Measured over the six reference windows: the guard
    /// fires on exactly ONE of 9,006 frames (<c>session_20260828_132715_det</c> step 234,895, arm two)
    /// and turns C9's 9,005 / 9,006 into <b>9,006 / 9,006</b>.
    /// </para>
    /// </remarks>
    public static int? ViewGuardRepublish(CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        int view = registers.Word(ViewMode);
        int latch = registers.Byte(ViewModeRadarLatch);
        int? published = null;

        if (view != 0x0F && latch != 0 && registers.Word(RadarTrackSlot) != 0)   // image@0x238BE
        {
            published = 0x0F;
            view = 0x0F;
            latch = 0;                                                          // image@0x23774
        }

        if (view == 0x0F && registers.Word(RadarTrackAlt) == 0)                  // image@0x238DB
        {
            published = 0;
            view = 0;
            latch = 1;                                                          // image@0x238F0
        }

        if (registers.Word(LastLockedTarget) == 0                                // image@0x238F5
            && (registers.Byte(ViewFlagsCached) & 8) != 0)
        {
            published = 0;
        }

        _ = latch;
        return published;
    }

    /// <summary>
    /// The gate's first term after the republish guard — <c>[0xE46F]</c> as
    /// <c>polygon_fill_mesh_render_setup</c>'s door will see it.
    /// </summary>
    /// <param name="registers">The register file, seeded at CS8.</param>
    /// <param name="staticData">The constant DGROUP (the view-mode flag table).</param>
    /// <returns>The effective <c>[0xE46F]</c>.</returns>
    /// <remarks>
    /// <c>publish_view_mode</c>'s tail is <c>mov al,[si+0x2BC0] / mov [0xF12A],al / and al,1 /
    /// mov [0xE46F],al</c> (<c>image@0x23859..0x23875</c>), so a republish of view <c>V</c> leaves
    /// <c>[0xE46F] = viewModeFlagTable[V] &amp; 1</c>.
    /// </remarks>
    public static byte EffectiveViewFlag(CombatRegisters registers, ICombatStaticData staticData)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(staticData);
        int? published = ViewGuardRepublish(registers);
        return published is { } view
            ? (byte)(staticData.Byte(ViewModeFlagTable + view) & 1)              // image@0x23873
            : registers.Byte(ViewModeBit0Flag);
    }

    /// <summary>
    /// <see cref="Compute(CombatRegisters)"/>, but with the within-span republish
    /// <see cref="ViewGuardRepublish"/> names taken into account.
    /// </summary>
    /// <param name="registers">The DGROUP combat register file.</param>
    /// <param name="staticData">The constant DGROUP.</param>
    /// <returns><see langword="true"/> when <c>[0x00B8]</c> ends up 1.</returns>
    public static bool Compute(CombatRegisters registers, ICombatStaticData staticData)
    {
        ArgumentNullException.ThrowIfNull(registers);
        byte view = EffectiveViewFlag(registers, staticData);
        bool keys = registers.Byte(InFlightKeyGate) != 0;
        bool lockOn = view != 0 && keys;
        bool label = lockOn && registers.Byte(TargetInfoVisible) != 0;
        registers.SetByte(LockOnEnable, lockOn ? (byte)1 : (byte)0);
        registers.SetByte(HudEngagementLabelGate, label ? (byte)1 : (byte)0);
        return lockOn;
    }

    /// <summary>
    /// Runs <c>image@0x016E8..0x016ED</c> — the unconditional post-render clear of BOTH bytes, in
    /// the machine's order (<c>[0x00B9]</c> first).
    /// </summary>
    /// <param name="registers">The DGROUP combat register file.</param>
    public static void ClearAfterRender(CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        registers.SetByte(HudEngagementLabelGate, 0);               // image@0x016EA
        registers.SetByte(LockOnEnable, 0);                         // image@0x016ED
    }
}
