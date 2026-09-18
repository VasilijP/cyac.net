using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// The engagement state COPY machinery: pool block ↔ DGROUP scratch, both directions.
/// </summary>
/// <remarks>
/// <para>
/// These two routines are the reason the whole combat kernel can be written against one 55-byte
/// record: everything between a <see cref="Snapshot"/> and the matching <see cref="Restore"/> works
/// on the DGROUP scratch, and only these two touch the arena.  Both are transliterated instruction
/// by instruction from <c>engagement_state_snapshot @image@0x02290</c> and
/// <c>engagement_state_restore @image@0x02316</c>.
/// </para>
/// <para>
/// <b>Snapshot makes THREE copies, restore makes TWO</b> — and the asymmetry is deliberate in the
/// original: the third copy's source is a mission-load constant (the prototype's arc-parameter
/// block), so there is nothing to write back.
/// </para>
/// </remarks>
public static class EngagementStateCopy
{
    /// <summary>Words the scratch's subject-object copy carries: 12 (<c>0x18</c> bytes).</summary>
    public const int SubjectObjectWords = 0x0C;

    /// <summary>Words restore puts back for a compact subject object: 9 (<c>0x12</c> bytes).</summary>
    public const int SubjectObjectWordsCompact = 9;

    /// <summary>
    /// <c>engagement_state_snapshot @image@0x02290</c> — arena block → the DGROUP scratch, plus the
    /// subject-object copy and the prototype's arc parameters.
    /// </summary>
    /// <param name="arena">The pool arena.</param>
    /// <param name="registers">The combat register file.</param>
    /// <param name="blockRef">The block's near offset — the original's <c>BX</c>.</param>
    /// <param name="prototypes">The prototype table seam (the third copy's source).</param>
    /// <remarks>
    /// <list type="number">
    /// <item><description><b>The block.</b>  <c>test word ptr [si+5],0x10</c>
    /// (<c>image@0x0229F</c>): SET ⇒ <c>rep movsw cx=0x10</c> = 32 bytes and then the FORCED re-arm
    /// <c>[0xED74] := 0</c>, <c>[0xED76] := 0xFFFF</c> (<c>image@0x022AB</c>/<c>0x022B2</c>) — the
    /// compact block has no room for a script pointer, so the scratch must not inherit the previous
    /// engagement's.  CLEAR ⇒ <c>rep movsw cx=0x1B / movsb</c> = 55 bytes.</description></item>
    /// <item><description><b>The subject object.</b>  <c>si := [0xED56]</c> (the scratch's
    /// <c>+0x02</c> owner reference, read with an <c>SS:</c> override because <c>DS</c> is the pool
    /// segment at that point), then <c>rep movsw cx=0x0C</c> into <c>[0xED3C]</c>
    /// (<c>image@0x022C1..0x022CC</c>) — 24 bytes, i.e. the WHOLE <c>s_pool_arena_entry</c>.  Then
    /// <c>test word ptr [0xED3E],2</c> (<c>image@0x022D0</c>): if the copied flag word has the
    /// zero-orientation bit, ZERO the angle triple at <c>[0xED4E]/[0xED50]/[0xED52]</c> — because a
    /// compact record has no orientation words and what was copied is the next record's
    /// bytes.</description></item>
    /// <item><description><b>The arc parameters.</b>  <c>si := [0xED54]</c> (the prototype),
    /// <c>cx := (proto[+0x0C] &amp; 3) &gt; 0 ? 2 : 0x1C</c>, <c>si := proto[+0x26]</c>, copy into
    /// <c>[0xED8E]</c> (<c>image@0x022EA..0x02311</c>).</description></item>
    /// </list>
    /// </remarks>
    public static void Snapshot(
        PoolArena arena, CombatRegisters registers, ushort blockRef, IEngagementPrototypes prototypes)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(prototypes);

        // (1) the block -> the scratch.
        bool compact = (arena.Byte((ushort)(blockRef + 0x05)) & 0x10) != 0;
        int blockBytes = compact ? EngagementState.StateBytesCompact : EngagementState.StateBytes;
        registers.Write(
            CombatRegisters.ScratchDgroupOffset, arena.Read(blockRef, blockBytes));
        if (compact)
        {
            registers.SetWord(CombatRegisters.ScratchDgroupOffset + 0x20, 0);        // [0xED74]
            registers.SetWord(CombatRegisters.ScratchDgroupOffset + 0x22, 0xFFFF);   // [0xED76]
        }

        // (2) the subject pool object -> [0xED3C], then the zero-orientation fix-up.
        ushort subjectRef = registers.Word(CombatRegisters.ScratchDgroupOffset + 0x02);
        registers.Write(
            CombatRegisters.SubjectObjectDgroupOffset, arena.Read(subjectRef, SubjectObjectWords * 2));
        if ((registers.SubjectObjectFlags & (ushort)WorldObjectFlags.NoOrientation) != 0)
        {
            registers.SetWord(CombatRegisters.SubjectObjectDgroupOffset + 0x12, 0);  // [0xED4E]
            registers.SetWord(CombatRegisters.SubjectObjectDgroupOffset + 0x14, 0);  // [0xED50]
            registers.SetWord(CombatRegisters.SubjectObjectDgroupOffset + 0x16, 0);  // [0xED52]
        }

        // (3) the prototype's arc parameters -> [0xED8E].
        ushort prototypeRef = registers.Word(CombatRegisters.ScratchDgroupOffset);
        int arcBytes = (prototypes.FlagsWord(prototypeRef) & 3) > 0
            ? CombatRegisters.ArcParameterBytesShort
            : CombatRegisters.ArcParameterBytes;
        registers.Write(
            CombatRegisters.ArcParameterDgroupOffset,
            prototypes.ArcParameters(prototypeRef, arcBytes));
    }

    /// <summary>
    /// <c>engagement_state_restore @image@0x02316</c> — the DGROUP scratch → the arena block, plus
    /// the subject object's own record.
    /// </summary>
    /// <param name="arena">The pool arena.</param>
    /// <param name="registers">The combat register file.</param>
    /// <param name="blockRef">The block's near offset — the original's <c>BX</c>.</param>
    /// <remarks>
    /// <para>
    /// <b>The block:</b> <c>cx := 0x20</c>, and <c>test word ptr [0xED59],0x10 / jne</c> keeps it —
    /// otherwise <c>cx := 0x37</c> (<c>image@0x02321..0x0232C</c>).  Note the test is on the SCRATCH
    /// copy of the flags byte, not on the arena's: a pass that changed the flag changes the copy
    /// size.
    /// </para>
    /// <para>
    /// <b>The subject object:</b> <c>si := 0xED3C</c>, <c>di := [0xED56]</c>,
    /// <c>cx := (([0xED3E] &amp; 2) != 0) ? 9 : 0x0C</c> WORDS (<c>image@0x02337..0x0234C</c>) —
    /// i.e. <c>0x12</c> bytes for a compact record and <c>0x18</c> for a full one, exactly the two
    /// arena record sizes.  This is how a manoeuvre the FSM computed on the scratch reaches the
    /// object's position and attitude.
    /// </para>
    /// </remarks>
    public static void Restore(PoolArena arena, CombatRegisters registers, ushort blockRef)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(registers);

        int blockBytes = (registers.Byte(CombatRegisters.ScratchDgroupOffset + 0x05) & 0x10) != 0
            ? EngagementState.StateBytesCompact
            : EngagementState.StateBytes;
        registers.Read(CombatRegisters.ScratchDgroupOffset, blockBytes)
            .CopyTo(arena.Span(blockRef, blockBytes));

        ushort subjectRef = registers.Word(CombatRegisters.ScratchDgroupOffset + 0x02);
        int words = (registers.SubjectObjectFlags & (ushort)WorldObjectFlags.NoOrientation) != 0
            ? SubjectObjectWordsCompact
            : SubjectObjectWords;
        registers.Read(CombatRegisters.SubjectObjectDgroupOffset, words * 2)
            .CopyTo(arena.Span(subjectRef, words * 2));
    }
}
