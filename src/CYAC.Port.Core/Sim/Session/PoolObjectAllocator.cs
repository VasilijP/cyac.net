using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Lifecycle;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// The POOL ARENA ALLOCATOR — the five routines that actually create a pooled object, ported from the
/// bytes so the port can build a scene instead of only stepping one.
/// </summary>
/// <remarks>
/// <para>
/// Batch 6 ported <c>spawn_dispatch_object @image@0x06F33</c> but left its two allocator calls as
/// <see cref="ILifecycleEvents.PoolInsert"/> / <see cref="ILifecycleEvents.ArenaWrite"/> seams, which
/// <c>NullLifecycleEvents</c> answers with 0 — so a verification could REPLAY a spawn against a
/// recorded arena but a cold-started port could not MAKE one.  This type is those bodies:
/// <c>pool_arena_init @image@0x151B3</c>, <c>pool_arena_write_or_abort @image@0x15236</c>,
/// <c>pool_object_append @image@0x1527E</c>, <c>pool_insert_no_parent @image@0x152A4</c>,
/// <c>pool_insert_with_flag2 @image@0x1536C</c>, <c>pool_insert_with_bbox_or @image@0x153A1</c> and
/// <c>pool_child_link_push_front @image@0x153FE</c>.
/// </para>
/// <para>
/// <b>Where the descriptor lives.</b>  Eight of the nine descriptor words are inside the combat
/// register file's own <c>g_mesh_pool_arena_descriptor [0xB13C]+21</c> window, so they are kept THERE
/// and not duplicated — a reader of <c>[0xB148]</c> sees the true cursor.  The ninth,
/// <c>[0xB138]/[0xB13A]</c> (the current GROUP object, used only while a <c>.W</c> hierarchy is being
/// built), is outside every window and is a field here.
/// </para>
/// <para>
/// <b>What is NOT ported.</b>  <c>far_heap_alloc</c> (the port's arena is a managed byte array) and
/// <c>oom_error_abort_modal</c> (an <see cref="InvalidOperationException"/> instead — a PoC that
/// overruns its arena should say so, not paint a DOS modal).
/// </para>
/// </remarks>
public sealed class PoolObjectAllocator
{
    /// <summary><c>[0xB138]</c>/<c>[0xB13A]</c> — the current group object's far pointer (offset half).</summary>
    private ushort _groupObject;

    private readonly PoolArena _arena;
    private readonly CombatRegisters _registers;
    private readonly ICombatStaticData _staticData;

    /// <summary>Wires the allocator over one arena and one register file.</summary>
    /// <param name="arena">The pool arena (the port's stand-in for the pool segment).</param>
    /// <param name="registers">The combat register file that holds the descriptor.</param>
    /// <param name="staticData">The constant DGROUP surface the class records live in.</param>
    public PoolObjectAllocator(
        PoolArena arena, CombatRegisters registers, ICombatStaticData staticData)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(staticData);
        _arena = arena;
        _registers = registers;
        _staticData = staticData;
    }

    /// <summary><c>[0xB140]</c> — the arena's base near offset.</summary>
    public const int ArenaBase = 0xB140;

    /// <summary><c>[0xB142]</c> — the arena's segment.</summary>
    public const int ArenaSegment = 0xB142;

    /// <summary><c>[0xB144]</c> — the arena's inclusive limit offset.</summary>
    public const int ArenaLimit = 0xB144;

    /// <summary><c>[0xB148]</c> — the bump cursor.</summary>
    public const int ArenaCursor = 0xB148;

    /// <summary><c>[0xB13C]</c> — the head of the arena's object list.</summary>
    public const int ArenaListHead = 0xB13C;

    /// <summary><c>[0xB14C]</c> — its tail.</summary>
    public const int ArenaListTail = 0xB14C;

    /// <summary><c>[0xE840]</c> — the segment the object-list walkers load into <c>ES</c>.</summary>
    public const int ObjectListSegment = 0xE840;

    /// <summary>A record's length when its flag bit1 (<c>NoOrientation</c>) is set: 18 bytes.</summary>
    public const int CompactRecordBytes = WorldObjectPool.EntryBytesCompact;

    /// <summary>…and when it is clear: 24 bytes.</summary>
    public const int FullRecordBytes = WorldObjectPool.EntryBytesWithOrientation;

    /// <summary>How many objects this allocator has inserted.</summary>
    public int ObjectsInserted { get; private set; }

    /// <summary>How many bytes it has appended.</summary>
    public int BytesAppended { get; private set; }

    /// <summary>The arena's bump cursor as it now stands.</summary>
    public ushort Cursor => _registers.Word(ArenaCursor);

    /// <summary>The arena's segment word.</summary>
    public ushort Segment => _registers.Word(ArenaSegment);

    /// <summary>
    /// <c>pool_arena_init @image@0x151B3</c> — publish the descriptor over an already-allocated arena.
    /// </summary>
    /// <param name="segment">
    /// The synthetic segment word the port uses for the pool (the original's
    /// <c>far_heap_alloc</c> result).  It must be non-zero: zero is the "no pool" sentinel.
    /// </param>
    /// <param name="byteCount">The arena's size in bytes.</param>
    /// <remarks>
    /// <c>[0xB148]:= [0xB140] + 2</c> (<c>inc ax / inc ax</c> @<c>image@0x151D9</c>): the cursor
    /// starts TWO bytes past the base, which is what makes near offset 0 a usable null even though the
    /// base itself is 0 (an earlier pass measured base <c>0x0000</c>, segment <c>0x7440</c>).
    /// </remarks>
    public void Initialise(ushort segment, int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfZero(segment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);

        ushort baseOffset = _arena.BaseOffset;
        _registers.SetWord(ArenaBase, baseOffset);                     // image@0x151BE
        _registers.SetWord(ArenaSegment, segment);                     // image@0x151C4
        _registers.SetWord(ArenaCursor, (ushort)(baseOffset + 2));     // image@0x151D9/0x151DB
        _registers.SetWord(ArenaCursor + 2, segment);                  // image@0x151DE
        _registers.SetWord(ArenaLimit, (ushort)(baseOffset + byteCount - 1)); // image@0x151E9
        _registers.SetWord(ArenaLimit + 2, segment);                   // image@0x151EC
        _registers.SetWord(ArenaListHead, 0);                          // image@0x151F5
        _registers.SetWord(ArenaListHead + 2, 0);                      // image@0x151F2
        _registers.SetWord(ArenaListTail, 0);                          // image@0x151FB
        _registers.SetWord(ArenaListTail + 2, 0);                      // image@0x151F8
        _registers.SetWord(ObjectListSegment, segment);                // image@0x15206
        _registers.PoolSegment = segment;
        _groupObject = 0;
    }

    /// <summary>
    /// <c>pool_arena_write_or_abort @image@0x15236</c> — append bytes at the cursor and advance it.
    /// </summary>
    /// <param name="source">The bytes to append.</param>
    /// <param name="count">How many of them (the original's <c>[bp+6]</c>).</param>
    /// <returns>The OLD cursor — the original's <c>DX:AX</c> reduced to its near half.</returns>
    /// <exception cref="InvalidOperationException">The arena would overflow.</exception>
    public ushort Append(ReadOnlySpan<byte> source, int count)
    {
        ushort cursor = _registers.Word(ArenaCursor);
        if ((ushort)(cursor + count) > _registers.Word(ArenaLimit))    // image@0x1524C jbe
        {
            throw new InvalidOperationException(
                $"the pool arena is full: cursor 0x{cursor:X4} + {count} passes the limit "
                    + $"0x{_registers.Word(ArenaLimit):X4} (the original calls "
                    + "oom_error_abort_modal @image@0x15252)");
        }

        source[..count].CopyTo(_arena.Span(cursor, count));            // image@0x15266
        _registers.SetWord(ArenaCursor, (ushort)(cursor + count));     // image@0x1526E
        BytesAppended += count;
        return cursor;
    }

    /// <summary>
    /// <c>pool_object_append @image@0x1527E</c> — append a record whose length its own flag word says.
    /// </summary>
    /// <param name="recordRef">The record's near offset inside the arena.</param>
    /// <returns>The appended copy's near offset.</returns>
    /// <remarks>
    /// <c>and ax,2 / cmp ax,1 / sbb ax,ax / and ax,6 / add ax,0x12</c>
    /// (<c>image@0x1528A..0x15295</c>) is 0x12 when bit1 is SET and 0x18 when it is clear.
    /// </remarks>
    public ushort AppendRecord(ushort recordRef)
    {
        int bytes = (_arena.Byte((ushort)(recordRef + 2)) & 0x02) != 0
            ? CompactRecordBytes
            : FullRecordBytes;
        return Append(_arena.Read(recordRef, bytes), bytes);
    }

    /// <summary>
    /// <c>pool_insert_no_parent @image@0x152A4</c> — append the record and chain it into the arena's
    /// own object list (or, inside a <c>.W</c> group, into that group's child chain).
    /// </summary>
    /// <param name="recordRef">The staged record's near offset.</param>
    /// <returns>The inserted object's near offset.</returns>
    public ushort InsertNoParent(ushort recordRef)
    {
        ushort obj = AppendRecord(recordRef);
        _arena.SetWord((ushort)(obj + 4), 0);                          // image@0x152BB

        if (_groupObject != 0)                                         // image@0x152C1 or/je
        {
            ushort head = _arena.Word((ushort)(_groupObject + 0x18));  // image@0x152CE
            if (head == 0)
            {
                _arena.SetWord((ushort)(_groupObject + 0x18), obj);    // image@0x152D5
            }
            else
            {
                ushort node = head;                                    // image@0x152F0
                while (_arena.Word((ushort)(node + 4)) != 0)           // image@0x1530F
                {
                    node = _arena.Word((ushort)(node + 4));            // image@0x152FF
                }

                _arena.SetWord((ushort)(node + 4), obj);               // image@0x15320
            }
        }
        else
        {
            if (_registers.Word(ArenaListHead) == 0
                && _registers.Word(ArenaListHead + 2) == 0)            // image@0x15326
            {
                _registers.SetWord(ArenaListHead, obj);                // image@0x15331
                _registers.SetWord(ArenaListHead + 2, Segment);        // image@0x15335
            }

            if (_registers.Word(ArenaListTail) != 0
                || _registers.Word(ArenaListTail + 2) != 0)            // image@0x15338
            {
                _arena.SetWord(
                    (ushort)(_registers.Word(ArenaListTail) + 4), obj); // image@0x15345
            }

            _registers.SetWord(ArenaListTail, obj);                    // image@0x1534C
            _registers.SetWord(ArenaListTail + 2, Segment);            // image@0x15350
        }

        ApplyClassFilterWord(obj);                                     // image@0x15356..0x1535C
        ObjectsInserted++;
        return obj;
    }

    /// <summary>
    /// <c>pool_insert_with_flag2 @image@0x1536C</c> — insert a record, storing it in the COMPACT
    /// 18-byte form when its euler triple is all zero.
    /// </summary>
    /// <param name="recordRef">The staged record's near offset.</param>
    /// <returns>The inserted object's near offset.</returns>
    /// <remarks>
    /// The flag is set for the duration of the insert ONLY: <c>mov [si+2],di</c> (<c>image@0x15396</c>)
    /// writes the original word back, so the flag bit exists to tell <see cref="AppendRecord"/> how
    /// many bytes to copy and is not a property the staged record keeps.
    /// </remarks>
    public ushort InsertWithFlag2(ushort recordRef)
    {
        ushort savedFlags = _arena.Word((ushort)(recordRef + 2));      // image@0x15377
        if (_arena.Word((ushort)(recordRef + 0x12)) == 0
            && _arena.Word((ushort)(recordRef + 0x14)) == 0
            && _arena.Word((ushort)(recordRef + 0x16)) == 0)           // image@0x1537A..0x1538A
        {
            _arena.SetByte(
                (ushort)(recordRef + 2),
                (byte)(_arena.Byte((ushort)(recordRef + 2)) | 0x02));  // image@0x1538C
        }

        ushort obj = InsertNoParent(recordRef);                        // image@0x15391
        _arena.SetWord((ushort)(recordRef + 2), savedFlags);           // image@0x15396
        return obj;
    }

    /// <summary>
    /// <c>pool_insert_with_bbox_or @image@0x153A1</c> — insert a record and push it onto the front of
    /// the DGROUP list a parent pseudo-record names.
    /// </summary>
    /// <param name="recordRef">The staged record's near offset.</param>
    /// <param name="parentRef">
    /// The parent's DGROUP near offset.  It is NOT a pool object: <c>image@0x1540A</c> reads
    /// <c>DS:[parent + 0x0C]</c>, so the scenario loader's <c>0x8A</c> means the list head
    /// <c>g_render_object_list_head [0x0096]</c>.
    /// </param>
    /// <returns>The inserted object's near offset.</returns>
    public ushort InsertWithParent(ushort recordRef, ushort parentRef)
    {
        ushort obj = AppendRecord(recordRef);                          // image@0x153A9
        LinkUnderParent(parentRef, obj);                               // image@0x153B5
        ApplyClassFilterWord(obj);                                     // image@0x153BC..0x153C2
        ObjectsInserted++;
        return obj;
    }

    /// <summary>
    /// <c>pool_child_link_push_front @image@0x153FE</c> — push an object onto the head of the list at
    /// <c>DGROUP[parent + 0x0C]</c>.
    /// </summary>
    /// <param name="parentRef">The parent pseudo-record's DGROUP near offset.</param>
    /// <param name="objectRef">The object's near offset.</param>
    public void LinkUnderParent(ushort parentRef, ushort objectRef)
    {
        int headAt = parentRef + 0x0C;                                 // image@0x15405
        ushort previous = _registers.Word(headAt);                     // image@0x1540A
        _registers.SetWord(headAt, objectRef);                         // image@0x15411
        _arena.SetWord((ushort)(objectRef + 4), previous);             // image@0x15416
    }

    /// <summary>The current <c>.W</c> group object whose children a later insert chains under.</summary>
    /// <param name="groupRef">The group object's near offset, or 0 for none.</param>
    public void SetGroupObject(ushort groupRef) => _groupObject = groupRef;

    /// <summary>
    /// <c>obj[+2] |= classRecord[+0x2E]</c> — the class's own pool-filter word
    /// (<see cref="ClassRecord.PoolFilterWord"/>), OR-ed in after the insert
    /// (<c>image@0x15356..0x1535C</c> and <c>image@0x153BC..0x153C2</c>).
    /// </summary>
    /// <param name="objectRef">The inserted object.</param>
    private void ApplyClassFilterWord(ushort objectRef)
    {
        ushort classRef = _arena.Word(objectRef);
        if (classRef == 0)
        {
            return;
        }

        ushort filter = _staticData.Word(classRef + 0x2E);
        _arena.SetWord(
            (ushort)(objectRef + 2),
            (ushort)(_arena.Word((ushort)(objectRef + 2)) | filter));
    }
}

/// <summary>
/// The lifecycle's out-calls with the ALLOCATOR wired in — what a running port hands
/// <c>spawn_dispatch_object</c> where a verification hands it an oracle.
/// </summary>
/// <param name="allocator">The pool allocator.</param>
/// <param name="arena">The arena the debris rotation reads its parent from.</param>
public sealed class SessionLifecycleEvents(PoolObjectAllocator allocator, PoolArena arena)
    : PortedLifecycleEvents(arena)
{
    private readonly PoolObjectAllocator _allocator =
        allocator ?? throw new ArgumentNullException(nameof(allocator));

    /// <summary>
    /// The register file the crosstalk census reads the player's own object out of, and the census
    /// itself.  Both null unless a host asks for the census; the kernel never reads either.
    /// </summary>
    public CombatRegisters? Registers { get; set; }

    /// <summary>The kill-crosstalk census, when a host has switched it on.</summary>
    public KillCrosstalkCensus? Crosstalk { get; set; }

    /// <summary>Cockpit-text calls the mission module made (the port has no cockpit text yet).</summary>
    public int CockpitTexts { get; private set; }

    /// <summary>Advisor actions requested.</summary>
    public int AdvisorActions { get; private set; }

    /// <inheritdoc/>
    public override uint PoolInsert(ushort slotRef, ushort parentRef, byte flag2)
    {
        ushort obj = parentRef != 0
            ? _allocator.InsertWithParent(slotRef, parentRef)          // image@0x06F85
            : flag2 != 0
                ? _allocator.InsertWithFlag2(slotRef)                  // image@0x06F95
                : _allocator.InsertNoParent(slotRef);                  // image@0x06F9F
        return ((uint)_allocator.Segment << 16) | obj;
    }

    /// <inheritdoc/>
    public override uint ArenaWrite(ReadOnlySpan<byte> source, int count) =>
        ((uint)_allocator.Segment << 16) | _allocator.Append(source, count);

    /// <inheritdoc/>
    /// <remarks>
    /// <c>slot_ext_ptr_init_with_tag @image@0x2C447</c>: switch the ext object ON, put it <b>20
    /// body-frame units forward</b> of its parent (<c>spawn_record_position_fill @image@0x2367F</c>
    /// with <c>y_h = 0, x_h = 0x14, z_h = 0</c>, <c>image@0x2C47E</c> — the same primitive the view
    /// anchor uses to sit 25 units ahead of the tracked object), and copy the parent's three Euler
    /// words (<c>image@0x2C4BB..0x2C4DE</c>).  The trailing <c>pending_sprite_slot_enqueue(0xC330,
    /// ext)</c> is the 8-frame sprite queue, which the port's renderer does not have and does not
    /// need — it walks the pool.
    /// </remarks>
    public override void InitialiseExtPoolObject(ushort slotObjectRef, ushort parentRef)
    {
        if (slotObjectRef == 0 || parentRef == 0
            || !Arena.Covers(slotObjectRef, 0x18) || !Arena.Covers(parentRef, 0x18))
        {
            return;
        }

        Arena.SetByte(                                                       // image@0x2C46E
            (ushort)(slotObjectRef + 0x02),
            (byte)(Arena.Byte((ushort)(slotObjectRef + 0x02)) | 1));

        new CombatObjectView(Arena, slotObjectRef).Position =
            Combat.Player.SpawnRecordPositionFill.Run(Arena, parentRef, 0, ExtObjectForwardOffset, 0);

        Arena.SetWord(
            (ushort)(slotObjectRef + 0x12), Arena.Word((ushort)(parentRef + 0x12)));
        Arena.SetWord(
            (ushort)(slotObjectRef + 0x14), Arena.Word((ushort)(parentRef + 0x14)));
        Arena.SetWord(
            (ushort)(slotObjectRef + 0x16), Arena.Word((ushort)(parentRef + 0x16)));
        ExtPoolInits++;

        if (Crosstalk is { } census && Registers is { } registers)
        {
            ushort player = registers.PlayerObjectRef;
            CombatObjectView ext = new CombatObjectView(Arena, slotObjectRef);
            CombatObjectView parent = new CombatObjectView(Arena, parentRef);
            CombatPosition extPosition = ext.Position;
            CombatPosition parentPosition = parent.Position;
            CombatPosition playerPosition = Arena.Covers(player, 0x18)
                ? new CombatObjectView(Arena, player).Position
                : default;
            census.RecordExtArm(new KillCrosstalkCensus.ExtArm(
                census.Frame,
                slotObjectRef,
                parentRef,
                player,
                ext.ClassRef,
                parent.ClassRef,
                extPosition.X / 256.0,
                extPosition.Y / 256.0,
                extPosition.Z / 256.0,
                parentPosition.X / 256.0,
                parentPosition.Y / 256.0,
                parentPosition.Z / 256.0,
                playerPosition.X / 256.0,
                playerPosition.Y / 256.0,
                playerPosition.Z / 256.0));
        }
    }

    /// <summary>
    /// The body-frame FORWARD offset the ext objects are born at: <c>0x14</c> = 20 units
    /// (<c>mov cx,0x14 / push cx</c> @<c>image@0x2C47E</c>).
    /// </summary>
    public const short ExtObjectForwardOffset = 0x14;

    /// <summary>How many ext-pool objects have been initialised.</summary>
    public int ExtPoolInits { get; private set; }

    /// <summary>
    /// The far pointer of the LAST cockpit text the mission module returned.
    /// </summary>
    /// <remarks>
    /// This seam has exactly one caller image-wide: <c>combat_vtable_slot_dispatch</c>'s
    /// <c>lcall 0x108e:0xc37b</c> @<c>image@0x08C5B</c>, with the module's non-zero <c>DX:AX</c>.
    /// Keeping it here — rather than reading the evaluator's own state — means the host hears a
    /// radio call by the ORIGINAL's own path.
    /// </remarks>
    public uint LastCockpitText { get; private set; }

    /// <inheritdoc/>
    public override void ShowCockpitText(uint textFarPointer)
    {
        CockpitTexts++;
        LastCockpitText = textFarPointer;
    }

    /// <summary>
    /// The host's advisor listener for the MISSION MODULE's own advisory.
    /// </summary>
    /// <remarks>
    /// The module's win hook raises action code <c>0x12</c> (the completion message) through
    /// <c>ai_advisor_check_then_dispatch @image@0x0F333</c> (<c>image@0x08C69</c>), i.e. the
    /// ONE-SHOT door — which is why a mission can only be congratulated once.  Optional and
    /// presentation-only, exactly like <see cref="SessionEffects.AdvisorObserver"/>.
    /// </remarks>
    public Action<byte>? AdvisorObserver { get; set; }

    /// <inheritdoc/>
    public override void AdvisorAction(byte actionCode)
    {
        AdvisorActions++;
        AdvisorObserver?.Invoke(actionCode);
    }
}
