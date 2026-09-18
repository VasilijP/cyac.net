namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// Whether a NON-PLAYER aircraft is drawn with its landing gear down — the original's own per-object
/// rule, not a broadcast of the player's gear.
/// </summary>
/// <remarks>
/// <para>
/// <b>The original does NOT show the player's gear on anyone else.</b>
/// <c>mesh_lod_prepare_gear_and_flame_state @image@0x2D8E9</c> is the per-class <c>prepare</c> callback every
/// aircraft LOD descriptor carries (<c>lcall [si+2]</c> @<c>image@0x1E1E2</c>), and its gear half begins by
/// asking whether the object being drawn IS the player:
/// </para>
/// <code>
/// 2D94D  a1 0c ea        mov ax, [0xEA0C]        ; g_render_current_object_id
/// 2D950  39 06 c0 00     cmp [0x00C0], ax        ; g_alt_object_farptr = the PLAYER's object
/// 2D954  75 50           jne 0x2D9A6             ; ← NOT the player: this rule
/// </code>
/// <para>
/// so <c>g_gear_deploy_angle_bam [0xEF96]</c> — the only ANIMATED gear angle in the game — reaches
/// the mesh only while the player's own aircraft is being drawn.  The non-player arm is a
/// TWO-STATE decision taken per draw from two of the object's own bits:
/// </para>
/// <code>
/// 2D9A6 80 3e 8a f2 00 cmp byte [0xF28A], 0; g_model_preview_mode_flag
/// 2D9AB  75 1a           jne 0x2D9C7             ;   non-zero ⇒ gear UP for everyone
/// 2D9AD  c4 1e 0c ea     les bx, [0xEA0C]        ; the object being drawn
/// 2D9B1 26 f6 47 03 08 test byte es:[bx+3], 8; the OBJECT's own flag bit 3 ("hostile")
/// 2D9B6  74 56           je  0x2DA0E             ;   CLEAR ⇒ gear DOWN
/// 2D9B8 26 8a 47 25 mov al, es:[bx+0x25]; the object's own phase byte
/// 2D9BC  8a d8 / 2a ff   bl := al ; bh := 0
/// 2D9C0  f6 87 0e 0f 04  test byte [bx+0xF0E], 4 ; g_engagement_phase_attr_table[phase] &amp; 4
/// 2D9C5  75 47           jne 0x2DA0E             ;   SET ⇒ gear DOWN
///        (fall through)  ⇒ 0x2D9C7               ; ⇒ gear UP
/// </code>
/// <para>
/// <b>The two terminal arms, corrected.</b> H5a §4 had the two arms the wrong way round, and the port would
/// have drawn every airborne bandit's wheels down exactly as before.  The 22 bytes are BSP paint-tree LEAF
/// TAGS and <c>mesh_poly_tree_walk @image@0x1A8A8</c> reads bit 1 as "draw this leaf" — <b>3 draws, 1
/// skips</b> (<c>test byte[si],2 / je → ret</c> @<c>image@0x1A8D6</c>).  <c>image@0x2D9C7</c> writes <b>1</b>
/// into all 22 = the gear geometry is SKIPPED = <b>wheels UP</b>; <c>image@0x2DA0E</c> zeroes the 17
/// articulation ANGLE words (0 is the EXTENDED end) and then FALLS THROUGH to <c>image@0x2DA40</c>, which
/// writes <b>3</b> into the same 22 = <b>wheels DOWN</b>.  The player's own retracted case proves the polarity
/// independently: <c>cmp [0xEF96],0x2D0 / je 0x2D9C7</c> @<c>image@0x2D956</c> sends the FULLY RETRACTED
/// player to <c>0x2D9C7</c>.
/// </para>
/// <para>
/// So the rule reads, in words: <b>an engaged (bit-3 "hostile") aircraft flies wheels UP unless its
/// engagement phase is one whose attribute byte carries bit 2</b> (in the shipped
/// <see cref="EngagementPhaseAttributes"/>, three phases: 0, 8 and 9).  Everything that is NOT
/// engage-capable keeps the authored, wheels-down model, which is what a parked aeroplane wants.
/// </para>
/// <para>
/// The port turns the verdict into a <c>SceneInstance.GearAngleBam</c> of
/// <c>GearDeployAngle.Extended</c> (0) or <c>GearDeployAngle.Retracted</c> (0x2D0), because
/// <c>GearPose</c> already hides all the gear geometry at the retracted end
/// (<c>image@0x2D956</c>'s universal rule) and draws it whole at the extended end — the same two
/// states, reached through the machinery the port already has.
/// </para>
/// </remarks>
public static class EnemyGearState
{
    // Consolidation C1,: the tree has published the same fourteen bytes as
    // exe/tables/combat_constants.json phaseAttributes; the rule reads
    // EngagementPhaseAttributes now.

    /// <summary>
    /// The object flag bit the rule tests at <c>pool_object[+0x03]</c>: <b>bit 3</b>.
    /// </summary>
    /// <remarks>
    /// <c>test byte es:[bx+3], 8</c> @<c>image@0x2D9B1</c>.  The same byte and the same bit that
    /// <c>missile_track_on_visible_leaf @image@0x0723E</c> reads as "hostile" (2002, "pool[+3]
    /// bit3 hostile").
    /// </remarks>
    public const byte ObjectFlagBit = 0x08;

    /// <summary>The bit of the phase-attribute entry that means "wheels DOWN": <b>bit 2</b>.</summary>
    /// <remarks>
    /// <c>test byte [bx+0xF0E], 4</c> @<c>image@0x2D9C0</c>, <c>jne</c> to the wheels-down arm.  In the
    /// shipped table three phases carry it: 0, 8 and 9.
    /// </remarks>
    public const byte PhaseAttributeGearDownBit = 0x04;

    /// <summary>
    /// Whether a non-player aircraft draws its gear DOWN this frame.
    /// </summary>
    /// <param name="engagementModeFlag">
    /// <c>[0xF28A]</c> — the MODEL-PREVIEW flag (<c>g_model_preview_mode_flag</c>, ex): 1 only in the
    /// aircraft-stats 3-D view and the film-review screen, 0 in every flight session (writer census
    /// in <c>GearDeployAngle.Step</c>).  So the "gear UP for everyone" arm is the showroom's, and in
    /// flight the rule below decides.
    /// </param>
    /// <param name="objectFlagsByte">The object's own <c>+0x03</c> byte.</param>
    /// <param name="phase">The object's own <c>+0x25</c> phase byte.</param>
    /// <param name="phaseAttributes">
    /// The phase-attribute table to read, or null for the process-wide one
    /// (<see cref="EngagementPhaseAttributes.Installed"/>).  It is only read when the first two tests
    /// leave the decision to the phase.
    /// </param>
    /// <returns>
    /// True for wheels DOWN (<c>image@0x2DA0E</c> → <c>0x2DA40</c>, leaf tags 3), false for wheels UP
    /// (<c>image@0x2D9C7</c>, leaf tags 1).
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The phase decides, no table was handed over and none is installed.
    /// </exception>
    public static bool GearDown(
        byte engagementModeFlag,
        byte objectFlagsByte,
        byte phase,
        EngagementPhaseAttributes? phaseAttributes = null)
    {
        if (engagementModeFlag != 0)
        {
            return false;                                  // image@0x2D9AB → 0x2D9C7, wheels UP
        }

        if ((objectFlagsByte & ObjectFlagBit) == 0)
        {
            return true;                                   // image@0x2D9B6 → 0x2DA0E, wheels DOWN
        }

        // image@0x2D9C5: the bit SET jumps to the wheels-down arm; clear falls through to wheels up.
        EngagementPhaseAttributes table = phaseAttributes ?? EngagementPhaseAttributes.Installed;
        return (table.Of(phase) & PhaseAttributeGearDownBit) != 0;
    }
}
