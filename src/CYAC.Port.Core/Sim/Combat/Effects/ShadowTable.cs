using CYAC.Port.Core.Sim.Combat.Lifecycle;

namespace CYAC.Port.Core.Sim.Combat.Effects;

/// <summary>
/// The AIRCRAFT-SHADOW subsystem — five pool objects that follow the five nearest qualifying aircraft
/// along the ground.
/// </summary>
/// <remarks>
/// <para>
/// <b>H8 finding — this is what calls "subsystem4x04", the "detail-level-gated LOD enemy-object
/// tracker" / "proxy" family.</b> It is not an LOD tracker and the objects are not proxies:
/// <c>subsystem4x04_type_to_resource_lookup @image@0x0B6CA</c> maps an aircraft's class record to its
/// <c>*sh</c> SHADOW class (<c>p51 → p51sh</c>, <c>b17 → b17sh</c>, …
/// <c>data/exe/tables/combat_constants.json → typeResources</c>), and the per-frame pass copies the
/// owner's X, Z and HEADING into the tracked object while never touching its Y — a mesh pinned to the
/// ground plane under the aeroplane.  The names are left alone here (they are H6a's landing zone);
/// the rename is proposed in the project's own notes.
/// </para>
/// <para>
/// The five slots live at <c>g_subsystem4x04_table [0xB93A..0xB94D]</c>, stride 4:
/// <c>{+0x00 shadow_object_nearptr, +0x02 owner_object_nearptr}</c>.  The shadow objects are
/// allocated ONCE per session and re-pointed at whatever they are following; the owner half is the
/// occupancy flag.
/// </para>
/// <para>
/// The PLAYER's own shadow is a different, dedicated object: <c>g_alt_object2_farptr [0x00C4]</c>,
/// created at cold start (<c>image@0x09421..0x09456</c>) and posed by the frame body itself
/// (<c>image@0x00D98..0x00DE1</c>, <see cref="Player.WeaponFireScheduler.FireStage"/>).  So a running
/// game shows up to SIX shadows — the player's and five others.
/// </para>
/// <para>
/// <b>Which aircraft have no shadow of their own.</b> Only twelve <c>*sh</c> meshes ship, and the
/// table has fourteen rows; the terminator row at <c>[0x2588]</c> is <c>{type 0x0000, resource
/// 0x736E}</c> and <c>image@0x0B6E1</c> returns <c>[bx+2]</c> of the row it STOPPED on — so an
/// aircraft that is not in the table gets <c>fw190sh</c>, the terminator's own resource, as its
/// shadow.  <c>fw190</c>, <c>me109</c>, <c>p47</c>, <c>yak9</c> and <c>l5</c> all take that path,
/// which is why <c>fw190sh</c> has no table row of its own and is still reachable, and why
/// <c>l5sh</c> (registry index −1) is never used at all.
/// </para>
/// </remarks>
public static class ShadowTable
{
    /// <summary>The LAST slot (<c>mov si,0xB94A</c> @<c>image@0x0B64E</c>); the walks go DOWN.</summary>
    public const int LastSlot = 0xB94A;

    /// <summary>The FIRST (<c>cmp si,0xB93A / jae</c> @<c>image@0x0B66A</c>).</summary>
    public const int FirstSlot = 0xB93A;

    /// <summary>One slot's stride: 4 bytes.</summary>
    public const int SlotBytes = 4;

    /// <summary>How many slots there are: 5 (closed in P59).</summary>
    public const int SlotCount = ((LastSlot - FirstSlot) / SlotBytes) + 1;

    /// <summary><c>+0x00</c> — the SHADOW object's pool near offset (allocated once, never freed).</summary>
    public const int ShadowObject = 0x00;

    /// <summary><c>+0x02</c> — the OWNER object it is following; 0 = the slot is free.</summary>
    public const int OwnerObject = 0x02;

    /// <summary><c>g_subsystem4x04_sentinel_byte [0xB94E]</c> — the cached graphics-detail byte.</summary>
    public const int CachedDetailByte = 0xB94E;

    /// <summary><c>g_subsystem4x04_frame_cursor [0x254E]</c> — the frame this pass last maintained.</summary>
    public const int FrameCursor = 0x254E;

    /// <summary>
    /// The class record every shadow object is BORN as: <c>0x8D84</c> = <c>mig21sh</c>
    /// (<c>mov word [bp-0x18],0x8d84</c> @<c>image@0x0B644</c>).  It is overwritten by the
    /// type→resource lookup the moment the slot claims an owner, so the seed only has to be a
    /// <c>*sh</c>-shaped class.
    /// </summary>
    public const ushort SeedClassRecord = 0x8D84;

    /// <summary>The staging record's flag word: 4 (<c>image@0x0B649</c>) — inactive, full 24-byte record.</summary>
    public const ushort SeedFlags = 0x0004;

    /// <summary><c>g_type_resource_table [0x2550]</c> — <c>{u16 class, u16 shadow_class}</c>, stride 4.</summary>
    public const int TypeResourceTable = 0x2550;

    /// <summary>
    /// The range from the VIEW ANCHOR past which a shadow is dropped and never given:
    /// <c>0x32C8</c> = 13,000 (<c>cmp ax,0x32c8</c> @<c>image@0x0B75B</c> and <c>image@0x0B7D6</c>).
    /// </summary>
    public const ushort MaximumRange = 0x32C8;

    /// <summary><c>g_graphics_detail [0xF108]</c> — shadows need it at 1 or more.</summary>
    public const int GraphicsDetail = 0xF108;

    /// <summary>The minimum detail level that draws shadows (<c>cmp byte [0xF108],1 / jae</c>).</summary>
    public const byte MinimumDetail = 1;

    /// <summary><c>g_engagement_mode_flag [0xF28A]</c> — non-zero swaps the acceptance test.</summary>
    public const int EngagementModeFlag = 0xF28A;

    /// <summary><c>eject4 [0x5618]</c> — the parachutist, who gets a shadow even though he is not an aircraft.</summary>
    public const ushort ParachutistClassRecord = 0x5618;

    /// <summary><c>crater [0x52A2]</c> — a wreck; never shadowed, and a shadowed owner that becomes one is dropped.</summary>
    public const ushort CraterClassRecord = 0x52A2;

    /// <summary>
    /// Flag-word bit 15 (<c>or byte es:[bx+3],0x80</c> @<c>image@0x0B802</c>) — "this object already
    /// has a shadow".  <c>subsystem4x04_slot_evict</c> clears it again
    /// (<c>and byte es:[si+3],0x7f</c> @<c>image@0x0B697</c>).
    /// </summary>
    public const ushort ShadowedFlag = 0x8000;

    /// <summary>
    /// <c>gx_subsystem_init_4x04 @image@0x0B630</c> — give each of the five slots its pool object.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="arena">The pool arena.</param>
    /// <param name="allocator">The pool allocator.</param>
    /// <param name="scratchRef">The staging record's near offset.</param>
    /// <returns>How many shadow objects were allocated.</returns>
    public static int Initialise(
        CombatRegisters registers,
        PoolArena arena,
        Session.PoolObjectAllocator allocator,
        ushort scratchRef)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(allocator);

        int allocated = 0;
        for (int slot = LastSlot; slot >= FirstSlot; slot -= SlotBytes)
        {
            arena.Span(scratchRef, Session.PoolObjectAllocator.FullRecordBytes).Clear();  // image@0x0B642
            arena.SetWord(scratchRef, SeedClassRecord);                                   // image@0x0B644
            arena.SetWord((ushort)(scratchRef + 0x02), SeedFlags);                        // image@0x0B649

            ushort obj = allocator.InsertWithParent(                                      // image@0x0B65B
                scratchRef, Session.ScenarioObjectLoader.RenderListParentRecord);
            registers.SetWord(slot + ShadowObject, obj);                                  // image@0x0B660
            registers.SetWord(slot + OwnerObject, 0);                                     // image@0x0B662
            allocated++;
        }

        registers.SetByte(CachedDetailByte, 0xFF);                                        // image@0x0B670
        registers.SetWord(FrameCursor, 0xFFFF);                                           // image@0x0B675
        return allocated;
    }

    /// <summary>
    /// <c>subsystem4x04_type_to_resource_lookup @image@0x0B6CA</c> — an aircraft class record's
    /// shadow class.
    /// </summary>
    /// <param name="data">The constant DGROUP surface the table lives in.</param>
    /// <param name="classRecord">The owner's class record near offset.</param>
    /// <returns>
    /// The shadow class record.  On a MISS this is the TERMINATOR row's own <c>+0x02</c> (<c>mov
    /// ax,[bx+2]</c> @<c>image@0x0B6E1</c> is reached by both exits), which the shipped table makes
    /// <c>fw190sh [0x736E]</c> — so every unlisted aeroplane casts an FW-190's shadow.
    /// </returns>
    public static ushort ShadowClassFor(ICombatStaticData data, ushort classRecord)
    {
        ArgumentNullException.ThrowIfNull(data);
        int at = TypeResourceTable;
        while (data.Word(at) != 0)                            // image@0x0B6DC cmp word [bx],0 / jne
        {
            if (data.Word(at) == classRecord)                 // image@0x0B6D4 cmp [bp+4],ax / je
            {
                break;
            }

            at += 4;                                          // image@0x0B6D9
        }

        return data.Word(at + 2);                             // image@0x0B6E1
    }

    /// <summary>
    /// <c>subsystem4x04_slot_evict @image@0x0B681</c> — stop following, and switch the shadow off.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="arena">The arena.</param>
    /// <param name="slot">The slot's DGROUP offset.</param>
    public static void Evict(CombatRegisters registers, PoolArena arena, int slot)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        ushort owner = registers.Word(slot + OwnerObject);
        if (owner == 0)                                       // image@0x0B688 je
        {
            return;
        }

        if (arena.Covers(owner, 0x18))
        {
            arena.SetByte(                                    // image@0x0B697 and es:[si+3],0x7f
                (ushort)(owner + 0x03),
                (byte)(arena.Byte((ushort)(owner + 0x03)) & 0x7F));
        }

        ushort shadow = registers.Word(slot + ShadowObject);
        if (arena.Covers(shadow, 0x18))
        {
            arena.SetByte(                                    // image@0x0B69E and es:[si+2],0xfe
                (ushort)(shadow + 0x02),
                (byte)(arena.Byte((ushort)(shadow + 0x02)) & 0xFE));
        }

        registers.SetWord(slot + OwnerObject, 0);             // image@0x0B6A3
    }

    /// <summary>
    /// <c>subsystem4x04_table_clear @image@0x0B6AF</c> — evict every slot and re-arm the frame cursor.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="arena">The arena.</param>
    public static void Clear(CombatRegisters registers, PoolArena arena)
    {
        ArgumentNullException.ThrowIfNull(registers);
        for (int slot = LastSlot; slot >= FirstSlot; slot -= SlotBytes)
        {
            Evict(registers, arena, slot);                    // image@0x0B6B6
        }

        registers.SetWord(FrameCursor, 0xFFFF);               // image@0x0B6C2
    }

    /// <summary>
    /// <c>subsystem4x04_per_frame_dispatch @image@0x0B6EA</c> — release, acquire and pose.
    /// </summary>
    /// <param name="context">The lifecycle context (registers, arena, class records, prototypes).</param>
    /// <returns>How many slots carry a live shadow after the pass.</returns>
    /// <remarks>
    /// <para>
    /// Called once per frame from the RENDER phase (<c>lcall 0x108e:0xae0a</c> @<c>image@0x015C5</c>,
    /// immediately after the view anchor's own <c>image@0x015BD</c> setup; the other site is
    /// <c>image@0x32BD6</c>).  The maintenance passes run only when <c>[0x254E]</c> is behind the
    /// master frame counter (<c>image@0x0B713</c>), so a second call in the same frame only re-poses.
    /// </para>
    /// <para>
    /// The port always takes the DETAIL-ON path.  Nothing in <c>Sim/</c> may name a presentation setting
    /// (the determinism law), and the alternative would be a
    /// scene whose object set depends on a display option.  The gate the original applies is <c>cmp byte
    /// [0xF108],1 / jae</c> @<c>image@0x0B6F2</c> — shadows at Medium and above, none at Low; the port's
    /// equivalent knob is the host's <c>--shadows off</c>, which is a RENDERER filter and never changes
    /// the pool.
    /// </para>
    /// </remarks>
    public static int PerFrameUpdate(EngagementLifecycleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;
        CombatPosition anchor = RangeAnchor(context);

        ushort frame = registers.MasterFrameCounter;
        if (registers.Word(FrameCursor) != frame)             // image@0x0B713 cmp / jne
        {
            registers.SetWord(FrameCursor, frame);            // image@0x0B722
            Release(context, anchor);                         // image@0x0B725..0x0B76B
            Acquire(context, anchor);                         // image@0x0B76D..0x0B83F
        }

        return Pose(registers, arena);                        // image@0x0B842..0x0B89D
    }

    /// <summary>
    /// The point the 13,000-unit cull measures from.
    /// </summary>
    /// <param name="context">The lifecycle context.</param>
    /// <returns><c>s_view_anchor [0xD88E]</c> when the session publishes one, else the player.</returns>
    /// <remarks>
    /// <b>A deliberate deviation, and the only one in this file.</b> The original measures from
    /// <c>s_view_anchor [0xD88E]</c> through <c>object_range_from_view_anchor @image@0x24448</c>
    /// (<c>image@0x0B756</c> / <c>image@0x0B7D1</c>).  The port does not publish that anchor: the kernel
    /// measured <c>PortedPlayerCombatEvents.RangeFromViewAnchor</c>'s saturated <c>0xFFFF</c> as the
    /// value the recordings agree with, so writing a real anchor into <c>[0xD88E]</c> would move
    /// a VERIFIED path (the AI shot's muzzle gate, <c>cmp ax,0x7d0 / jae</c> @<c>image@0x03960</c>).
    /// The anchor is therefore taken from the PLAYER's own object when <c>[0xD88E]</c> is still zero —
    /// which is where the original's anchor sits for the cockpit view and within a chase distance of it
    /// for the external ones, and the test it feeds is a 13,000-unit cull.  <c>(open)</c>: publishing
    /// the real anchor is the right answer once the muzzle gate is re-verified against a trace that
    /// carries <c>[0xD88E]</c>.
    /// </remarks>
    public static CombatPosition RangeAnchor(EngagementLifecycleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatPosition anchor = Geometry.ViewAnchorRange.AnchorFrom(context.Registers);
        if (anchor.X != 0 || anchor.Y != 0 || anchor.Z != 0)
        {
            return anchor;
        }

        ushort player = context.Registers.PlayerObjectRef;
        return context.Arena.Covers(player, 0x18)
            ? new CombatObjectView(context.Arena, player).Position
            : anchor;
    }

    /// <summary><c>object_range_from_view_anchor @image@0x24448</c> against the chosen anchor.</summary>
    private static ushort RangeTo(PoolArena arena, ushort objectRef, CombatPosition anchor) =>
        Geometry.ViewAnchorRange.Compute(
            new CombatObjectView(arena, objectRef).Position, anchor);

    /// <summary>Drops the shadows whose owner is gone, wrecked, or out of range.</summary>
    private static void Release(EngagementLifecycleContext context, CombatPosition anchor)
    {
        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;
        for (int slot = LastSlot; slot >= FirstSlot; slot -= SlotBytes)
        {
            ushort owner = registers.Word(slot + OwnerObject);
            if (owner == 0)                                   // image@0x0B72A je
            {
                continue;
            }

            bool keep = arena.Covers(owner, 0x18)
                && (arena.Word((ushort)(owner + 0x02)) & 0x0001) != 0    // image@0x0B73F test 1
                && arena.Word(owner) != CraterClassRecord               // image@0x0B746 cmp 0x52a2
                && RangeTo(arena, owner, anchor) <= MaximumRange;       // image@0x0B756
            if (!keep)
            {
                Evict(registers, arena, slot);                          // image@0x0B761
            }
        }
    }

    /// <summary>Gives a shadow to every newly qualifying object, while free slots last.</summary>
    private static void Acquire(EngagementLifecycleContext context, CombatPosition anchor)
    {
        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;
        bool engagementMode = registers.Byte(EngagementModeFlag) != 0;  // image@0x0B790

        ushort cursor = registers.Word(LifecycleOffsets.RenderListHead);
        for (int visited = 0; cursor != 0 && visited < 512; visited++)
        {
            ushort next = arena.Covers(cursor, 0x18)
                ? arena.Word((ushort)(cursor + 0x04))                   // image@0x0B831
                : (ushort)0;
            if (Qualifies(context, cursor, engagementMode, anchor))
            {
                Claim(context, cursor);
            }

            cursor = next;
        }
    }

    /// <summary>The acceptance test, <c>image@0x0B783..0x0B7E3</c>.</summary>
    private static bool Qualifies(
        EngagementLifecycleContext context,
        ushort objectRef,
        bool engagementMode,
        CombatPosition anchor)
    {
        PoolArena arena = context.Arena;
        if (!arena.Covers(objectRef, 0x18))
        {
            return false;
        }

        ushort flags = arena.Word((ushort)(objectRef + 0x02));
        if ((flags & 0x0001) == 0)                            // image@0x0B786 test 1 / je
        {
            return false;
        }

        ushort classRef = arena.Word(objectRef);
        if (engagementMode)
        {
            // image@0x0B797: bit11 (carries an engagement) and not a wreck.
            if ((flags & 0x0800) == 0 || classRef == CraterClassRecord)
            {
                return false;
            }
        }
        else
        {
            // image@0x0B7AB: the AIRCRAFT predicate, with the parachutist as the one exception.
            if (!EngagementAdmission.ObjectIsEngaged(context, objectRef)
                && classRef != ParachutistClassRecord)        // image@0x0B7B7 cmp 0x5618
            {
                return false;
            }

            if (classRef == CraterClassRecord)                // image@0x0B7C1
            {
                return false;
            }
        }

        // image@0x0B7D1 — within 13,000 of the view anchor…
        if (RangeTo(arena, objectRef, anchor) > MaximumRange)
        {
            return false;
        }

        // …and not already carrying one (image@0x0B7DE test es:[bx+3],0x80).
        return (flags & ShadowedFlag) == 0;
    }

    /// <summary>Puts one qualifying object into the first free slot, <c>image@0x0B7E5..0x0B82B</c>.</summary>
    private static void Claim(EngagementLifecycleContext context, ushort objectRef)
    {
        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;
        for (int slot = LastSlot; slot >= FirstSlot; slot -= SlotBytes)
        {
            if (registers.Word(slot + OwnerObject) != 0)      // image@0x0B7EE cmp / je
            {
                continue;
            }

            registers.SetWord(slot + OwnerObject, objectRef); // image@0x0B7FC
            arena.SetByte(                                    // image@0x0B802 or es:[bx+3],0x80
                (ushort)(objectRef + 0x03),
                (byte)(arena.Byte((ushort)(objectRef + 0x03)) | 0x80));

            ushort shadow = registers.Word(slot + ShadowObject);
            if (arena.Covers(shadow, 0x18))
            {
                arena.SetByte(                                // image@0x0B81A or es:[bx+2],1
                    (ushort)(shadow + 0x02),
                    (byte)(arena.Byte((ushort)(shadow + 0x02)) | 0x01));
                arena.SetWord(                                // image@0x0B82B mov es:[di],ax
                    shadow, ShadowClassFor(context.StaticData, arena.Word(objectRef)));
            }

            return;
        }
    }

    /// <summary>
    /// Copies each owner's X, Z and HEADING onto its shadow — <c>image@0x0B86C..0x0B892</c>.
    /// </summary>
    /// <remarks>
    /// The Y pair (<c>+0x0A</c>/<c>+0x0C</c>) is NEVER copied, which is the whole trick: the shadow
    /// object keeps the <c>Y = 0</c> it was created with and slides along the ground plane under the
    /// aeroplane.  The player's own shadow is posed the same way three instructions apart in the
    /// frame body (<c>image@0x00D98</c>).
    /// </remarks>
    private static int Pose(CombatRegisters registers, PoolArena arena)
    {
        int live = 0;
        for (int slot = LastSlot; slot >= FirstSlot; slot -= SlotBytes)
        {
            ushort owner = registers.Word(slot + OwnerObject);
            if (owner == 0)                                   // image@0x0B847 je
            {
                continue;
            }

            ushort shadow = registers.Word(slot + ShadowObject);
            if (!arena.Covers(owner, 0x18) || !arena.Covers(shadow, 0x18))
            {
                continue;
            }

            arena.SetWord((ushort)(shadow + 0x06), arena.Word((ushort)(owner + 0x06)));  // X lo
            arena.SetWord((ushort)(shadow + 0x08), arena.Word((ushort)(owner + 0x08)));  // X hi
            arena.SetWord((ushort)(shadow + 0x0E), arena.Word((ushort)(owner + 0x0E)));  // Z lo
            arena.SetWord((ushort)(shadow + 0x10), arena.Word((ushort)(owner + 0x10)));  // Z hi
            arena.SetWord((ushort)(shadow + 0x12), arena.Word((ushort)(owner + 0x12)));  // heading
            live++;
        }

        return live;
    }
}
