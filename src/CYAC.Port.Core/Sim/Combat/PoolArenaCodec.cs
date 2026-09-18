using System.Buffers.Binary;
using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// One record read out of the pool arena's raw bytes — a faithful, class-free view of
/// <c>s_pool_arena_entry</c> plus the engagement block that may follow it.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately NOT a <see cref="WorldObject"/>: that type owns a
/// <see cref="ClassRecord"/> and a parent/child tree, and an arena dump carries only a DGROUP near
/// pointer to the class.  <see cref="ToWorldObject"/> bridges the two once a class registry can
/// resolve <see cref="ClassRef"/>; until then the entry keeps the original's own numbers so
/// <see cref="PoolArenaCodec.Encode"/> can put every byte back exactly where it came from.
/// </para>
/// </remarks>
public sealed class PoolArenaEntry
{
    /// <summary>The record's byte offset inside the arena segment.</summary>
    public required ushort Offset { get; init; }

    /// <summary><c>+0x00</c> — the DGROUP near pointer to the object's class record.</summary>
    public required ushort ClassRef { get; init; }

    /// <summary><c>+0x02</c> — the flag/filter word.</summary>
    public required WorldObjectFlags Flags { get; set; }

    /// <summary><c>+0x04</c> — the next sibling's arena offset, or 0.</summary>
    public required ushort NextSiblingRef { get; init; }

    /// <summary><c>+0x06</c> — world X, i32.</summary>
    public required int X { get; init; }

    /// <summary><c>+0x0A</c> — world Y (up), i32.</summary>
    public required int Y { get; init; }

    /// <summary><c>+0x0E</c> — world Z, i32.</summary>
    public required int Z { get; init; }

    /// <summary>
    /// <c>+0x12</c> — the RAW heading word; 0 on a compact record (the word is not stored).
    /// </summary>
    /// <remarks>
    /// Raw rather than an <see cref="Angle"/> on purpose: <see cref="Angle"/> canonicalises into
    /// <c>[0, 0xB40)</c>, which would break the byte-exact round trip if the arena ever held an
    /// un-normalised word.  <see cref="ToWorldObject"/> is where the conversion happens.
    /// </remarks>
    public required short HeadingUnits { get; init; }

    /// <summary><c>+0x14</c> — the raw pitch word; 0 on a compact record.</summary>
    public required short PitchUnits { get; init; }

    /// <summary><c>+0x16</c> — the raw roll word; 0 on a compact record.</summary>
    public required short RollUnits { get; init; }

    /// <summary>
    /// The engagement block that follows the record, or <see langword="null"/> when
    /// <see cref="WorldObjectFlags.CarriesEngagement"/> is clear.
    /// </summary>
    /// <remarks>
    /// A STUB block (<see cref="IsEngagementStub"/>) is still decoded into a full 55-byte
    /// <see cref="EngagementState"/> — the arena only allocated 5 bytes for it, so bytes
    /// <c>+0x05</c> onwards are the NEXT record's and must never be written back.
    /// <see cref="PoolArenaCodec.Encode"/> honours that.
    /// </remarks>
    public EngagementState? Engagement { get; set; }

    /// <summary>
    /// True when the engagement block is the 5-byte stub form: its prototype is not engage-capable,
    /// so <c>spawn_dispatch_object</c> allocated only <see cref="EngagementState.StubBytes"/>.
    /// </summary>
    /// <remarks>
    /// The arena itself does not say so — the port infers it from the STRIDE to the next record when
    /// one is known (<see cref="PoolArenaCodec.Walk"/> does exactly that), which is measurable and
    /// needs no access to the DGROUP prototype table.
    /// </remarks>
    public bool IsEngagementStub { get; set; }

    /// <summary>Bytes of the base record: <c>0x12</c> when compact, else <c>0x18</c>.</summary>
    public int BaseBytes => Flags.HasFlag(WorldObjectFlags.NoOrientation)
        ? WorldObjectPool.EntryBytesCompact
        : WorldObjectPool.EntryBytesWithOrientation;

    /// <summary>
    /// Bytes the whole allocation occupies: the base record plus the engagement block's arena size
    /// (0, <see cref="EngagementState.StubBytes"/> or <see cref="EngagementState.StateBytesCompact"/>
    /// / <see cref="EngagementState.AllocatedBytesFull"/>).
    /// </summary>
    public int AllocatedBytes => BaseBytes + (Engagement is null
        ? 0
        : IsEngagementStub
            ? EngagementState.StubBytes
            : Engagement.IsCompact
                ? EngagementState.StateBytesCompact
                : EngagementState.AllocatedBytesFull);

    /// <summary>Bridges to the port's world model once the class pointer can be resolved.</summary>
    /// <param name="classRecord">The class <see cref="ClassRef"/> names.</param>
    public WorldObject ToWorldObject(ClassRecord classRecord)
    {
        ArgumentNullException.ThrowIfNull(classRecord);
        return new WorldObject(classRecord)
        {
            Flags = Flags,
            X = X,
            Y = Y,
            Z = Z,
            Heading = Angle.FromUnits(HeadingUnits),
            Pitch = Angle.FromUnits(PitchUnits),
            Roll = Angle.FromUnits(RollUnits),
            Engagement = Engagement,
            HasFullEngagementBlock = Engagement is not null && !IsEngagementStub,
        };
    }

    /// <summary>A short description for test output.</summary>
    public override string ToString() =>
        $"0x{Offset:X4} class=0x{ClassRef:X4} flags=0x{(ushort)Flags:X4} "
            + $"@({X},{Y},{Z}){(Engagement is null ? "" : IsEngagementStub ? " +stub" : " +eng")}";
}

/// <summary>
/// Reads the pool arena's raw bytes — the region a combat trace carries when it is produced with
/// <c>--combat-trace-pool N</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The descriptor.</b> <c>g_mesh_pool_arena_descriptor [0xB13C]</c> is <c>u16[10]</c>:
/// <c>+0x00/+0x02</c> entry-list head far pointer, <c>+0x04/+0x06</c> arena BASE,
/// <c>+0x08/+0x0A</c> LIMIT, <c>+0x0C/+0x0E</c> CURSOR, <c>+0x10/+0x12</c> TAIL (re-derived from
/// <c>pool_arena_heap_alloc_and_init @image@0x151B3</c>).  A record's <c>poolUsed</c> is
/// <c>cursor − base</c>, and the dumped bytes start AT the base.
/// </para>
/// <para>
/// <b>The walk starts at the PLAYER.</b> <c>g_alt_object_farptr [0x00C0]</c> is the head of the
/// sibling chain the combat code actually uses; the descriptor's own <c>+0x00</c> head has a zero
/// <c>+0x04</c> link and walks nowhere (independently reproduced here).
/// </para>
/// <para>
/// <b>Round trip is the proof</b>: <see cref="Walk"/> followed by
/// <see cref="Encode"/> reproduces the arena bytes exactly, because every field an entry does not
/// model is simply not written back.
/// </para>
/// </remarks>
public static class PoolArenaCodec
{
    /// <summary>Byte offset of the arena BASE offset inside the descriptor window.</summary>
    public const int DescriptorBaseOffset = 0x04;

    /// <summary>Byte offset of the arena BASE segment inside the descriptor window.</summary>
    public const int DescriptorBaseSegment = 0x06;

    /// <summary>Byte offset of the arena CURSOR offset inside the descriptor window.</summary>
    public const int DescriptorCursorOffset = 0x0C;

    /// <summary>Byte offset of the arena LIMIT offset inside the descriptor window.</summary>
    public const int DescriptorLimitOffset = 0x08;

    /// <summary>The arena's base near offset, from a 20-byte descriptor window.</summary>
    /// <param name="descriptor">The <c>[0xB13C]</c> window.</param>
    public static ushort BaseOffset(ReadOnlySpan<byte> descriptor) =>
        BinaryPrimitives.ReadUInt16LittleEndian(descriptor[DescriptorBaseOffset..]);

    /// <summary>The arena's bump cursor, from a 20-byte descriptor window.</summary>
    /// <param name="descriptor">The <c>[0xB13C]</c> window.</param>
    public static ushort CursorOffset(ReadOnlySpan<byte> descriptor) =>
        BinaryPrimitives.ReadUInt16LittleEndian(descriptor[DescriptorCursorOffset..]);

    /// <summary>Used bytes, i.e. <c>cursor − base</c> — the record's <c>poolUsed</c>.</summary>
    /// <param name="descriptor">The <c>[0xB13C]</c> window.</param>
    public static int UsedBytes(ReadOnlySpan<byte> descriptor) =>
        (ushort)(CursorOffset(descriptor) - BaseOffset(descriptor));

    /// <summary>
    /// Walks the sibling chain from <paramref name="headOffset"/> and decodes every record it
    /// reaches.
    /// </summary>
    /// <param name="arena">The dumped arena bytes, starting at the arena BASE.</param>
    /// <param name="baseOffset">The arena's base near offset (so a record at near offset
    /// <c>o</c> is at <c>arena[o − baseOffset]</c>).</param>
    /// <param name="headOffset">The first record's near offset — <c>g_alt_object_farptr [0x00C0]</c>.</param>
    /// <param name="maxEntries">A safety bound on a corrupt chain.</param>
    /// <returns>The entries in chain order.</returns>
    public static IReadOnlyList<PoolArenaEntry> Walk(
        ReadOnlySpan<byte> arena, ushort baseOffset, ushort headOffset, int maxEntries = 4096)
    {
        List<PoolArenaEntry> entries = new List<PoolArenaEntry>();
        HashSet<ushort> visited = new HashSet<ushort>();
        ushort cursor = headOffset;

        while (cursor != 0 && visited.Add(cursor) && entries.Count < maxEntries)
        {
            int index = cursor - baseOffset;
            if (index < 0 || index + WorldObjectPool.EntryBytesCompact > arena.Length)
            {
                break;
            }

            PoolArenaEntry? entry = Decode(arena, baseOffset, cursor);
            if (entry is null)
            {
                break;
            }

            entries.Add(entry);
            cursor = entry.NextSiblingRef;
        }

        // A block's arena size is not stored anywhere: it is the STRIDE to the next record in
        // ADDRESS order.  Sort a copy by offset, and mark a block a 5-byte stub when the gap to the
        // next record is exactly base + 5 (measured: 3 of 11 on v11_b2_clear_det step 58,500).
        PoolArenaEntry[] byAddress = entries.OrderBy(e => e.Offset).ToArray();
        for (int i = 0; i < byAddress.Length - 1; i++)
        {
            PoolArenaEntry entry = byAddress[i];
            if (entry.Engagement is null)
            {
                continue;
            }

            int stride = byAddress[i + 1].Offset - entry.Offset;
            entry.IsEngagementStub = stride == entry.BaseBytes + EngagementState.StubBytes;
        }

        return entries;
    }

    /// <summary>Decodes one record at a known arena offset.</summary>
    /// <param name="arena">The dumped arena bytes, starting at the arena BASE.</param>
    /// <param name="baseOffset">The arena's base near offset.</param>
    /// <param name="offset">The record's near offset.</param>
    /// <returns>The entry, or null when the record does not fit in the dump.</returns>
    public static PoolArenaEntry? Decode(ReadOnlySpan<byte> arena, ushort baseOffset, ushort offset)
    {
        int index = offset - baseOffset;
        if (index < 0 || index + WorldObjectPool.EntryBytesCompact > arena.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> record = arena[index..];
        WorldObjectFlags flags = (WorldObjectFlags)BinaryPrimitives.ReadUInt16LittleEndian(record[0x02..]);
        bool compact = flags.HasFlag(WorldObjectFlags.NoOrientation);
        int baseBytes = compact
            ? WorldObjectPool.EntryBytesCompact
            : WorldObjectPool.EntryBytesWithOrientation;
        if (index + baseBytes > arena.Length)
        {
            return null;
        }

        PoolArenaEntry entry = new PoolArenaEntry
        {
            Offset = offset,
            ClassRef = BinaryPrimitives.ReadUInt16LittleEndian(record),
            Flags = flags,
            NextSiblingRef = BinaryPrimitives.ReadUInt16LittleEndian(record[0x04..]),
            X = BinaryPrimitives.ReadInt32LittleEndian(record[0x06..]),
            Y = BinaryPrimitives.ReadInt32LittleEndian(record[0x0A..]),
            Z = BinaryPrimitives.ReadInt32LittleEndian(record[0x0E..]),
            HeadingUnits = compact ? (short)0 : BinaryPrimitives.ReadInt16LittleEndian(record[0x12..]),
            PitchUnits = compact ? (short)0 : BinaryPrimitives.ReadInt16LittleEndian(record[0x14..]),
            RollUnits = compact ? (short)0 : BinaryPrimitives.ReadInt16LittleEndian(record[0x16..]),
        };

        if (flags.HasFlag(WorldObjectFlags.CarriesEngagement)
            && index + baseBytes + EngagementState.StateBytes <= arena.Length)
        {
            entry.Engagement = EngagementStateCodec.Decode(record[baseBytes..]);
        }

        return entry;
    }

    /// <summary>Writes one record back over the arena bytes it came from.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="arena">The arena bytes, starting at the arena BASE.</param>
    /// <param name="baseOffset">The arena's base near offset.</param>
    /// <remarks>
    /// A STUB block writes back only its 5 live bytes; a compact block only 32.  Anything else
    /// would clobber the NEXT record, because those are the sizes the arena actually allocated.
    /// </remarks>
    public static void Encode(PoolArenaEntry entry, Span<byte> arena, ushort baseOffset)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int index = entry.Offset - baseOffset;
        Span<byte> record = arena[index..];
        bool compact = entry.Flags.HasFlag(WorldObjectFlags.NoOrientation);

        BinaryPrimitives.WriteUInt16LittleEndian(record, entry.ClassRef);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x02..], (ushort)entry.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x04..], entry.NextSiblingRef);
        BinaryPrimitives.WriteInt32LittleEndian(record[0x06..], entry.X);
        BinaryPrimitives.WriteInt32LittleEndian(record[0x0A..], entry.Y);
        BinaryPrimitives.WriteInt32LittleEndian(record[0x0E..], entry.Z);
        if (!compact)
        {
            BinaryPrimitives.WriteInt16LittleEndian(record[0x12..], entry.HeadingUnits);
            BinaryPrimitives.WriteInt16LittleEndian(record[0x14..], entry.PitchUnits);
            BinaryPrimitives.WriteInt16LittleEndian(record[0x16..], entry.RollUnits);
        }

        if (entry.Engagement is { } block)
        {
            int liveBytes = entry.IsEngagementStub
                ? EngagementState.StubBytes
                : block.IsCompact ? EngagementState.StateBytesCompact : EngagementState.StateBytes;
            block.Bytes[..liveBytes].CopyTo(record[entry.BaseBytes..]);
        }
    }
}
