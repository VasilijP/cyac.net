using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Effects;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// <c>gx_subsystem_init_30x1B @image@0x023B6</c> — the SESSION-init pass that gives every one of the
/// 30 combat spawn slots its own pool object.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece without which nothing can fire.  <c>combat_spawn_slot_alloc_and_film_record
/// @image@0x02423</c> does NOT create a projectile's world object — it reuses the one already at
/// <c>slot[+0x02]</c> (<c>image@0x024B0</c>, "pre-existing, never assigned", C6's own note) — and
/// that object is allocated HERE, once per session, then immediately deactivated:
/// </para>
/// <code>
/// 023CA  [bp-0x18] := 0x74AE            ; a 24-byte zeroed template whose +0x00 is a CLASS RECORD
/// 023CF  si := 0xB479                    ; the LAST slot; the loop walks DOWN
/// 023D4  ax := pool_insert_with_bbox_or(record = &amp;[bp-0x18], parent = 0x8A)
/// 023E1 slot[+2]:= ax; the slot's pool object
/// 023EC  es:[ax+2] &amp;= 0xFE           ; …deactivated (flag bit0 clear)
/// 023F1  slot[+0] := 0                   ; the slot is free
/// 023F5  si -= 0x1B                      ; until si &lt; 0xB16A
/// </code>
/// <para>
/// <c>0x74AE</c> is NOT a magic number — it is a
/// DGROUP near pointer to the static class record at <c>image@0x4320E</c>, whose <c>+0x2E</c> filter
/// word (<c>0x0200</c>) <c>pool_insert_with_bbox_or</c> ORs into the new object's flags.  A port must
/// RELOCATE the constant, not copy the object.
/// </para>
/// <para>
/// <b>What a missing call looks like</b>, measured on the port before this landed: every spawn slot's
/// <c>+0x02</c> stayed 0, so the first AI shot of the sortie built its projectile through
/// <c>CombatObjectView(arena, 0)</c> and wrote the shot's flags and position over arena bytes
/// <c>0x0002..0x0011</c> — which is the FIRST mission object's record.  The symptom was a
/// <c>g_render_object_list_head [0x0096]</c> sibling chain that walked off the arena on frame 341.
/// </para>
/// </remarks>
public static class CombatSubsystemInit
{
    /// <summary>The spawn table's LAST slot (<c>mov si,0xB479</c> @<c>image@0x023CF</c>).</summary>
    public const int SpawnTableLastSlot = 0xB479;

    /// <summary>Its FIRST (<c>cmp si,0xB16A / jae</c> @<c>image@0x023F8</c>).</summary>
    public const int SpawnTableFirstSlot = 0xB16A;

    /// <summary>One slot's stride: <c>0x1B</c> = 27 bytes.</summary>
    public const int SpawnSlotStride = 0x1B;

    /// <summary>How many slots there are: 30.</summary>
    public const int SpawnSlotCount =
        ((SpawnTableLastSlot - SpawnTableFirstSlot) / SpawnSlotStride) + 1;

    /// <summary>
    /// The template's <c>+0x00</c> — a DGROUP near pointer to the static class record at
    /// <c>image@0x4320E</c> (not a "magic seed").
    /// </summary>
    public const ushort ProjectileClassRecord = 0x74AE;

    /// <summary>Runs the session init over one arena.</summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="arena">The pool arena.</param>
    /// <param name="allocator">The pool allocator.</param>
    /// <param name="scratchRef">The staging record's near offset.</param>
    /// <returns>How many slot objects were allocated.</returns>
    public static int SpawnTable(
        CombatRegisters registers,
        PoolArena arena,
        PoolObjectAllocator allocator,
        ushort scratchRef)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(allocator);

        int allocated = 0;
        for (int slot = SpawnTableLastSlot; slot >= SpawnTableFirstSlot; slot -= SpawnSlotStride)
        {
            arena.Span(scratchRef, PoolObjectAllocator.FullRecordBytes).Clear();  // image@0x023C8
            arena.SetWord(scratchRef, ProjectileClassRecord);                     // image@0x023CA

            ushort obj = allocator.InsertWithParent(
                scratchRef, ScenarioObjectLoader.RenderListParentRecord);         // image@0x023DC
            registers.SetWord(slot + 0x02, obj);                                  // image@0x023E1
            arena.SetByte(                                                        // image@0x023EC
                (ushort)(obj + 0x02), (byte)(arena.Byte((ushort)(obj + 0x02)) & 0xFE));
            registers.SetWord(slot, 0);                                           // image@0x023F1
            allocated++;
        }

        // image@0x023FE..0x02418 — the nine combat globals the init zeroes.
        registers.SetWord(0xB168, 0);
        registers.SetWord(0xB494, 0);
        registers.SetWord(0xED3A, 0);
        registers.SetWord(0xED38, 0);
        registers.SetWord(0xED36, 0);
        registers.SetWord(0xED34, 0);
        registers.SetWord(0xB49A, 0);
        registers.SetWord(0xB498, 0);
        registers.SetByte(0xB496, 0);
        return allocated;
    }

    /// <summary>
    /// The class record the DEFERRED-EFFECT pool's ten objects are instantiated from — a DGROUP near
    /// pointer to <c>image@0x4174E</c> (<c>gx_subsystem_init_10x0E @image@0x03A04</c>;
    /// corrected the scanner's "magic seed 0x59EE" the same way it corrected <c>0x74AE</c>).
    /// </summary>
    public const ushort DeferredEffectClassRecord = 0x59EE;

    /// <summary>
    /// <c>gx_subsystem_init_smoke @image@0x0B086</c> and
    /// <c>gx_subsystem_init_10x0E @image@0x03A04</c> — the pool objects the two EFFECT tables switch
    /// on and off.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="arena">The pool arena.</param>
    /// <param name="allocator">The pool allocator.</param>
    /// <param name="scratchRef">The staging record's near offset.</param>
    /// <param name="countermeasures">the five chaff/flare slots' bytes.</param>
    /// <returns>How many effect objects were allocated (15 puffs + 10 flashes).</returns>
    /// <remarks>
    /// Both are the same template as the spawn table's: a zeroed 24-byte record whose <c>+0x00</c> is
    /// a static CLASS-RECORD near pointer, inserted through <c>pool_insert_with_bbox_or(record,
    /// parent 0x8A)</c> so the object lands on <c>g_render_object_list_head [0x0096]</c>.  Neither
    /// initialiser deactivates its objects the way the spawn table's does — the puff pool's own
    /// allocator tests the ACTIVE bit to find a free slot, and a freshly inserted object has it CLEAR
    /// because the insert never sets it.
    /// </remarks>
    public static int EffectTables(
        CombatRegisters registers,
        PoolArena arena,
        PoolObjectAllocator allocator,
        ushort scratchRef,
        CountermeasureTable countermeasures)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(countermeasures);

        ushort Insert(ushort classRecord)
        {
            arena.Span(scratchRef, PoolObjectAllocator.FullRecordBytes).Clear();
            arena.SetWord(scratchRef, classRecord);
            ushort obj = allocator.InsertWithParent(
                scratchRef, ScenarioObjectLoader.RenderListParentRecord);
            arena.SetByte(
                (ushort)(obj + 0x02), (byte)(arena.Byte((ushort)(obj + 0x02)) & 0xFE));
            return obj;
        }

        int allocated = SmokePuffTable.Initialise(registers, Insert);

        // image@0x03A04 — the ten deferred-effect records, walked the same way.
        foreach (int record in DeferredEffectPool.Records())
        {
            registers.SetWord(record, Insert(DeferredEffectClassRecord));
            allocated++;
        }

        // gx_subsystem_init_5x06 @image@0x2DB2A: the five CRATER objects.  Without them
        // subsystem5x06_per_frame_slot_fire has no object to place and a ground impact leaves no
        // mark, which is the "no smoking wreck" half of the defect.
        allocated += CraterPool.Initialise(registers, Insert);

        // H16 seam 2 — gx_subsystem_init_5x1C @image@0x0AB70: the five CHAFF/FLARE cloud objects.
        // Without them countermeasure_cloud_spawn finds no slot, returns 0, and the dispensers take
        // their pool-full arm — which is why the cockpit's counters were frozen at 15/15. The slots
        // live in their own CountermeasureTable, not in the register file: the declared window is
        // 26 bytes short of the real table (see that type's remarks).
        allocated += CountermeasurePool.Initialise(countermeasures, Insert);

        return allocated;
    }

    /// <summary>
    /// <c>object_pool_init_3_slots @image@0x2C2B6</c> — give the three destruction slots their nine
    /// pool objects: per slot a PILOT, a SEAT and a CANOPY.
    /// </summary>
    /// <param name="context">The lifecycle context (<c>spawn_dispatch_object</c> needs it).</param>
    /// <param name="allocator">The pool allocator.</param>
    /// <param name="scratchRef">The staging record's near offset.</param>
    /// <returns>How many objects were allocated (9 when all three slots build).</returns>
    /// <remarks>
    /// <para>
    /// The slots themselves exist from the moment the register file does — they are DGROUP — but
    /// their <c>ext_ptr_a/b/c</c> are zero until this runs, and <c>slot_alloc_and_activate</c> never
    /// creates them (it only re-stamps <c>ext_ptr_a</c>'s class, <c>image@0x2C655</c>).  So without
    /// this call the whole ejection sequence writes through a null pool pointer, which is why H5a
    /// counted <c>per_object_tick</c> instead of running it.
    /// </para>
    /// <para>
    /// <c>ext_ptr_a</c> is born through <c>spawn_dispatch_object @image@0x06F33</c> with the 58-byte
    /// template whose prototype is <c>[0x24E8]</c> — H6b's DROPPED 20th engagement prototype
    /// <c>"Man"</c>, whose <c>class_record_nearptr</c> is <c>0x5334</c> = <c>eject1</c>.  Nothing
    /// else in the image references that prototype, so its only surviving use is this: the ejecting
    /// pilot.  <c>ext_ptr_b</c> (<c>eject1</c> again) and <c>ext_ptr_c</c> (<c>canopy</c>
    /// <c>[0x5030]</c>) go through the plain insert.
    /// </para>
    /// <para>
    /// All three are deactivated on the spot (<c>and byte es:[bx+2],0xfe</c> @<c>image@0x2C30C</c>
    /// for A; B and C are inserted inactive because the insert never sets bit 0).
    /// </para>
    /// </remarks>
    public static int ObjectSlotTable(
        Combat.Lifecycle.EngagementLifecycleContext context,
        PoolObjectAllocator allocator,
        ushort scratchRef)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(allocator);

        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;
        int allocated = 0;

        ushort Insert(ushort classRecord)
        {
            arena.Span(scratchRef, PoolObjectAllocator.FullRecordBytes).Clear();
            arena.SetWord(scratchRef, classRecord);
            ushort obj = allocator.InsertWithParent(
                scratchRef, ScenarioObjectLoader.RenderListParentRecord);
            arena.SetByte((ushort)(obj + 0x02), (byte)(arena.Byte((ushort)(obj + 0x02)) & 0xFE));
            allocated++;
            return obj;
        }

        Span<byte> template = stackalloc byte[Combat.Lifecycle.SpawnDispatchObject.TemplateBytes];
        for (int slot = Combat.Lifecycle.ObjectSlotPool.LastSlot;
             slot >= Combat.Lifecycle.ObjectSlotPool.FirstSlot;
             slot -= Combat.Lifecycle.ObjectSlotPool.SlotBytes)
        {
            registers.SetByte(slot + Combat.Lifecycle.ObjectSlotPool.ActiveFlag, 0);  // image@0x2C2D9

            // image@0x2C2DC..0x2C301 — ext_ptr_a through spawn_dispatch_object, template
            // prototype = the dropped "Man" record, destination = the 24-byte scratch.
            arena.Span(scratchRef, PoolObjectAllocator.FullRecordBytes).Clear();
            arena.SetWord(scratchRef, PilotClassRecord);
            template.Clear();
            template[0] = unchecked((byte)DroppedManPrototype);
            template[1] = (byte)(DroppedManPrototype >> 8);
            uint pilot = Combat.Lifecycle.SpawnDispatchObject.Run(
                context, template, scratchRef, ScenarioObjectLoader.RenderListParentRecord, 0, 0, 0);
            ushort pilotRef = unchecked((ushort)pilot);
            registers.SetWord(slot + Combat.Lifecycle.ObjectSlotPool.ExtPointerA, pilotRef);
            arena.SetByte(
                (ushort)(pilotRef + 0x02),
                (byte)(arena.Byte((ushort)(pilotRef + 0x02)) & 0xFE));           // image@0x2C30C
            allocated++;

            registers.SetWord(
                slot + Combat.Lifecycle.ObjectSlotPool.ExtPointerB, Insert(PilotClassRecord));
            registers.SetWord(
                slot + Combat.Lifecycle.ObjectSlotPool.ExtPointerC, Insert(CanopyClassRecord));
        }

        return allocated;
    }

    /// <summary>
    /// <c>eject1</c> <c>[0x5334]</c> — the pilot's and the seat's class
    /// (<c>mov word [bp-0x18],0x5334</c> @<c>image@0x2C2E1</c> and <c>image@0x2C316</c>).
    /// </summary>
    public const ushort PilotClassRecord = 0x5334;

    /// <summary>
    /// <c>canopy</c> <c>[0x5030]</c> — the jettisoned canopy's class
    /// (<c>mov word [bp-0x18],0x5030</c> @<c>image@0x2C32B</c>).
    /// </summary>
    public const ushort CanopyClassRecord = 0x5030;

    /// <summary>
    /// The prototype the pilot's spawn template names: <c>[0x24E8]</c>, H6b's dropped 20th
    /// engagement prototype <c>"Man"</c> (<c>mov word [bp-0x52],0x24e8</c> @<c>image@0x2C2E6</c>).
    /// </summary>
    public const ushort DroppedManPrototype = 0x24E8;
}
