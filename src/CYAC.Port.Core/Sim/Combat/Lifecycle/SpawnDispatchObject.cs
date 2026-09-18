using CYAC.Port.Core.Model.Combat;

namespace CYAC.Port.Core.Sim.Combat.Lifecycle;

/// <summary>
/// <c>spawn_dispatch_object @image@0x06F33</c> — the GENERIC top-level pooled-object spawn: every
/// combat-engine object (enemy aircraft, scripted world objects, the three destruction slots) is
/// born here.
/// </summary>
/// <remarks>
/// <para>
/// FAR, <c>retf 0x0C</c>, six pushed words.  Its shape is: copy a 58-byte
/// <c>s_spawn_template</c> onto its own frame, seed the destination slot from the template's MODEL
/// object, insert the object into the world pool through one of three variants, initialise the
/// engagement node ON THE TEMPLATE COPY, append a block to the mesh/render arena, and — when the
/// sixth argument says so — give the node a random priority and push it into the expiry list.
/// </para>
/// <para>
/// <b>The template's <c>+0x00</c> is a POINTER, not a value.</b> <c>mov bx,[bp-0x3e]</c>
/// (<c>image@0x06F4A</c>) loads the copy's first word and every subsequent read is
/// <c>[bx+N]</c> through <c>DS</c> — so the record the type tier, the engage flag and the hit points
/// come from is a DGROUP MODEL object, and <c>dst[+0x00] := model[+0x00]</c>,
/// <c>dst[+0x02] := model[+0x02] | 0x801</c>.
/// </para>
/// <para>
/// <b>The <c>0x801</c> is TEMPORARY.</b> Bit0 (active) and bit11 (carries-engagement) are forced on
/// only across the insert and then the ORIGINAL <c>dst[+0x02]</c> is written back
/// (<c>image@0x06FAA</c>) — the insert routines read the flags, the world does not keep them.
/// </para>
/// <para>
/// <b>Two different tier tests, and conflates them.</b> The flag-clear arm is <c>tier &lt; 2</c>
/// (<c>cmp al,2 / jge</c> @<c>image@0x06F6E</c>) but the random-priority narrowing is <c>tier ==
/// 0</c> (<c>cmp cx,1 / sbb cx,cx / and cl,0xf4 / add cx,0xf</c> @<c>image@0x07019</c>, which yields
/// mask 3 for tier 0 and mask 15 otherwise).  The scanner entry says "tier&lt;2 … narrows engagement
/// priority rand 0..3 vs 0..15"; the bytes say the two gates differ.  Reported here, not changed.
/// </para>
/// <para>
/// Source of truth: the original's bytes, read against.
/// </para>
/// </remarks>
public static class SpawnDispatchObject
{
    /// <summary>The template's length in bytes: 58 (<c>rep movsw cx=0x1D</c> @<c>image@0x06F48</c>).</summary>
    public const int TemplateBytes = 58;

    /// <summary>The flags forced on across the insert: bit0 ACTIVE | bit11 CARRIES-ENGAGEMENT.</summary>
    public const ushort TemporaryInsertFlags = 0x0801;

    /// <summary>
    /// Runs one spawn — <c>image@0x06F33..0x07043</c>.
    /// </summary>
    /// <param name="context">The lifecycle context.</param>
    /// <param name="template">
    /// The 58-byte <c>s_spawn_template</c> (the fifth pushed word, <c>[bp+0x0E]</c>); it is COPIED,
    /// and the copy is what <c>engagement_list_node_init</c> then edits.  Its first
    /// <see cref="EngagementState.StateBytes"/> bytes ARE an <see cref="EngagementState"/>; the
    /// remaining three ride along into the arena block untouched.
    /// </param>
    /// <param name="destinationSlot">The destination slot's near offset (<c>[bp+0x10]</c>).</param>
    /// <param name="parentRef">The parent object, or 0 (<c>[bp+0x0C]</c>).</param>
    /// <param name="flag2">The second insert selector (<c>[bp+0x08]</c>).</param>
    /// <param name="compactBlock">
    /// <c>[bp+0x06]</c>: non-zero asks for the COMPACT 32-byte arena block (and sets its
    /// <c>+0x05</c> bit4), zero for the full 58-byte one.  Only consulted when the model is
    /// engage-capable.
    /// </param>
    /// <param name="engageFlag">
    /// <c>[bp+0x0A]</c>: non-zero runs the engagement-list insert phase.
    /// </param>
    /// <returns>The inserted object's far pointer, the original's <c>DX:AX</c>.</returns>
    public static uint Run(
        EngagementLifecycleContext context,
        ReadOnlySpan<byte> template,
        ushort destinationSlot,
        ushort parentRef,
        byte flag2,
        byte compactBlock,
        byte engageFlag)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (template.Length != TemplateBytes)
        {
            throw new ArgumentException(
                $"a spawn template is {TemplateBytes} bytes (rep movsw cx=0x1D @image@0x06F48)",
                nameof(template));
        }

        CombatRegisters r = context.Registers;
        PoolArena arena = context.Arena;
        LifecycleCensus census = context.Census;
        census.SpawnDispatches++;

        // The 58-byte copy the rest of the routine works on (image@0x06F3E..0x06F48).
        byte[] copyBytes = template.ToArray();
        EngagementState copy = new EngagementState(copyBytes.AsSpan(0, EngagementState.StateBytes));
        ushort modelRef = copy.PrototypeRef;                                    // image@0x06F4A

        arena.SetWord(destinationSlot, context.StaticData.Word(modelRef));      // image@0x06F52
        ushort savedFlags = arena.Word((ushort)(destinationSlot + 0x02));       // image@0x06F54
        arena.SetWord(
            (ushort)(destinationSlot + 0x02),
            (ushort)(context.StaticData.Word(modelRef + 0x02) | TemporaryInsertFlags)); // image@0x06F63

        int tier = context.StaticData.Byte(modelRef + 0x0C) & 3;                // image@0x06F69
        if (tier < 2)                                                           // image@0x06F6E jge
        {
            census.SpawnTierClear++;
            arena.SetByte(
                (ushort)(destinationSlot + 0x02),
                (byte)(arena.Byte((ushort)(destinationSlot + 0x02)) & 0xEF));   // image@0x06F75
        }

        uint inserted;
        if (parentRef != 0)                                                     // image@0x06F79
        {
            census.SpawnInsertArm[0]++;
            inserted = context.Events.PoolInsert(destinationSlot, parentRef, flag2); // image@0x06F85
        }
        else if (flag2 != 0)                                                    // image@0x06F8C
        {
            census.SpawnInsertArm[1]++;
            inserted = context.Events.PoolInsert(destinationSlot, 0, flag2);    // image@0x06F95
        }
        else
        {
            census.SpawnInsertArm[2]++;
            inserted = context.Events.PoolInsert(destinationSlot, 0, 0);        // image@0x06F9F
        }

        arena.SetWord((ushort)(destinationSlot + 0x02), savedFlags);            // image@0x06FB0

        // engagement_list_node_init on the TEMPLATE COPY, with the inserted object's near offset.
        EngagementList.NodeInit(                                                // image@0x06FB9
            copy,
            unchecked((ushort)inserted),
            context.Prototypes,
            r.MasterFrameCounter,
            context.Random);

        int blockBytes;
        bool armCompact = false;
        if ((context.StaticData.Byte(modelRef + 0x0C) & 8) != 0)                // image@0x06FBF
        {
            if (compactBlock != 0)                                              // image@0x06FC5
            {
                blockBytes = 0x20;
                armCompact = true;
                census.SpawnArenaBlock[0]++;
            }
            else
            {
                blockBytes = 0x3A;
                census.SpawnArenaBlock[1]++;
            }
        }
        else
        {
            blockBytes = 5;                                                     // image@0x06FF6
            census.SpawnArenaBlock[2]++;
        }

        copy.Bytes.CopyTo(copyBytes);
        uint block = context.Events.ArenaWrite(copyBytes, blockBytes);          // image@0x06FD3/0x06FFA
        ushort blockRef = unchecked((ushort)block);
        if (armCompact)
        {
            arena.SetByte(                                                      // image@0x06FE2
                (ushort)(blockRef + 0x05),
                (byte)(arena.Byte((ushort)(blockRef + 0x05)) | 0x10));
        }

        if (engageFlag != 0)                                                    // image@0x07005
        {
            census.SpawnListInserts++;
            int draw = context.Random.Rand8();                                  // image@0x0700B
            // cmp cx,1 / sbb cx,cx / and cl,0xf4 / add cx,0xf  (image@0x07019..0x07021):
            // tier 0 -> mask 0x0003, any other tier -> mask 0x000F.
            int mask = tier == 0 ? 0x0003 : 0x000F;
            arena.SetWord((ushort)(blockRef + 0x0B), (ushort)(draw & mask));    // image@0x07029
            r.ExpiryListHead =
                EngagementList.NodeSortedInsert(arena, r, blockRef, r.ExpiryListHead); // image@0x07032
        }

        return inserted;                                                        // image@0x07038
    }
}
