using System.Buffers.Binary;
using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// The pool arena as the combat kernel sees it: a flat byte region addressed by the original's near
/// offsets, in which pool records and their embedded engagement blocks live.
/// </summary>
/// <remarks>
/// <para>
/// The combat kernel does not walk a C# object graph — it walks near pointers inside one segment
/// (<c>g_object_pool_segment [0x0094]</c>), and the expiry list, the departure path and the damage
/// resolver all address blocks and objects by those offsets.  Reproducing that faithfully is what
/// makes <c>state[k] == F_k(state[k−1])</c> checkable against a trace's raw arena dump, so this type
/// keeps the bytes and hands out typed views over them.  <see cref="PoolArenaCodec"/> is the bridge
/// to the port's own <see cref="WorldObject"/> model.
/// </para>
/// <para>
/// Field class: the arena is INT-only spine for everything the combat kernel touches; the positions
/// inside a record are the integer half of DUAL pairs.
/// </para>
/// </remarks>
public sealed class PoolArena
{
    private readonly byte[] _bytes;
    private bool[]? _written;

    /// <summary>Wraps a copy of an arena region.</summary>
    /// <param name="bytes">The arena bytes, starting at <paramref name="baseOffset"/>.</param>
    /// <param name="baseOffset">
    /// The near offset the region starts at — the descriptor's <c>+0x04</c> BASE.
    /// </param>
    public PoolArena(ReadOnlySpan<byte> bytes, ushort baseOffset)
    {
        _bytes = bytes.ToArray();
        BaseOffset = baseOffset;
    }

    /// <summary>The near offset the region starts at.</summary>
    public ushort BaseOffset { get; }

    /// <summary>How many bytes the region holds.</summary>
    public int Length => _bytes.Length;

    /// <summary>The whole region — the encode side of a round trip.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>An owning copy.</summary>
    public PoolArena Clone() => new(_bytes, BaseOffset);

    /// <summary>True when a near range falls inside the region.</summary>
    /// <param name="nearOffset">The near offset.</param>
    /// <param name="length">How many bytes.</param>
    public bool Covers(ushort nearOffset, int length)
    {
        int index = nearOffset - BaseOffset;
        return index >= 0 && index + length <= _bytes.Length;
    }

    /// <summary>Reads a near range.</summary>
    /// <param name="nearOffset">The near offset.</param>
    /// <param name="length">How many bytes.</param>
    public ReadOnlySpan<byte> Read(ushort nearOffset, int length) =>
        _bytes.AsSpan(Require(nearOffset, length), length);

    /// <summary>
    /// Turns on WRITE TRACKING: every byte a later <see cref="Span"/> hands out is remembered.
    /// </summary>
    /// <remarks>
    /// An AUDIT facility, off by default and costing nothing when off.  It exists because a
    /// verification has to separate the bytes the ported piece WROTE (which must match the oracle
    /// exactly) from the bytes an oracle-fed seam's callee wrote (which the port does not model yet)
    /// — the arena is shared between the two and a whole-region diff cannot tell them apart.  The
    /// analogue is <c>VelocityTickResult.Accumulators</c>: a stage reports its own write set.
    /// </remarks>
    public void TrackWrites() => _written ??= new bool[_bytes.Length];

    /// <summary>
    /// Clears the write set without turning tracking off — C7's addition, so the assembled driver's
    /// verification can separate the write set of ONE stage from the frame's.  A no-op when
    /// tracking is off.
    /// </summary>
    /// <returns>How many bytes were in the write set.</returns>
    public int ResetWrites()
    {
        if (_written is null)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < _written.Length; i++)
        {
            if (_written[i])
            {
                count++;
                _written[i] = false;
            }
        }

        return count;
    }

    /// <summary>Whether a byte has been written since <see cref="TrackWrites"/> was called.</summary>
    /// <param name="index">A byte index into <see cref="Bytes"/> (NOT a near offset).</param>
    public bool WasWritten(int index) => _written is not null && _written[index];

    /// <summary>How many distinct bytes have been written since tracking was turned on.</summary>
    public int WrittenByteCount
    {
        get
        {
            if (_written is null)
            {
                return 0;
            }

            int count = 0;
            foreach (bool written in _written)
            {
                if (written)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// Suspends write tracking until the returned handle is disposed, then restores the write set to
    /// exactly what it was — so bytes an oracle-fed SEAM's callee installs do not join the port's own
    /// write set.  (C2 addition; C1's behaviour is unchanged when it is never called.)
    /// </summary>
    /// <returns>A handle that restores the write set.</returns>
    public IDisposable SuspendWriteTracking() => new WriteTrackingScope(this);

    private sealed class WriteTrackingScope : IDisposable
    {
        private readonly PoolArena _arena;
        private readonly bool[]? _saved;

        public WriteTrackingScope(PoolArena arena)
        {
            _arena = arena;
            _saved = arena._written is null ? null : (bool[])arena._written.Clone();
            arena._written = null;
        }

        public void Dispose() => _arena._written = _saved;
    }

    /// <summary>A writable span over a near range.</summary>
    /// <param name="nearOffset">The near offset.</param>
    /// <param name="length">How many bytes.</param>
    public Span<byte> Span(ushort nearOffset, int length)
    {
        int index = Require(nearOffset, length);
        if (_written is not null)
        {
            for (int i = 0; i < length; i++)
            {
                _written[index + i] = true;
            }
        }

        return _bytes.AsSpan(index, length);
    }

    /// <summary>Reads a word.</summary>
    /// <param name="nearOffset">The near offset.</param>
    public ushort Word(ushort nearOffset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(Read(nearOffset, 2));

    /// <summary>Writes a word.</summary>
    /// <param name="nearOffset">The near offset.</param>
    /// <param name="value">The value.</param>
    public void SetWord(ushort nearOffset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(Span(nearOffset, 2), value);

    /// <summary>Reads a byte.</summary>
    /// <param name="nearOffset">The near offset.</param>
    public byte Byte(ushort nearOffset) => Read(nearOffset, 1)[0];

    /// <summary>Writes a byte.</summary>
    /// <param name="nearOffset">The near offset.</param>
    /// <param name="value">The value.</param>
    public void SetByte(ushort nearOffset, byte value) => Span(nearOffset, 1)[0] = value;

    // ---- objects -------------------------------------------------------------------------------

    /// <summary>An object's <c>+0x02</c> flag word.</summary>
    /// <param name="objectRef">The object's near offset.</param>
    public WorldObjectFlags ObjectFlags(ushort objectRef) =>
        (WorldObjectFlags)Word((ushort)(objectRef + 0x02));

    /// <summary>Sets an object's flag word.</summary>
    /// <param name="objectRef">The object's near offset.</param>
    /// <param name="flags">The new flags.</param>
    public void SetObjectFlags(ushort objectRef, WorldObjectFlags flags) =>
        SetWord((ushort)(objectRef + 0x02), (ushort)flags);

    /// <summary>
    /// Where an object's engagement block starts —
    /// <c>object_pool_get_engagement_fieldoff @image@0x0225A</c>: <c>+0x12</c> when the flag word's
    /// bit1 is SET, else <c>+0x18</c>.
    /// </summary>
    /// <param name="objectRef">The object's near offset.</param>
    public ushort EngagementBlockRef(ushort objectRef) => (ushort)(objectRef
        + (ObjectFlags(objectRef).HasFlag(WorldObjectFlags.NoOrientation)
            ? WorldObjectPool.EntryBytesCompact
            : WorldObjectPool.EntryBytesWithOrientation));

    /// <summary>True when the object's flag word has bit11 — it carries an engagement block.</summary>
    /// <param name="objectRef">The object's near offset.</param>
    public bool CarriesEngagement(ushort objectRef) =>
        ObjectFlags(objectRef).HasFlag(WorldObjectFlags.CarriesEngagement);

    // ---- engagement blocks / list nodes ---------------------------------------------------------

    /// <summary>Reads an engagement block as an owning <see cref="EngagementState"/>.</summary>
    /// <param name="blockRef">The block's near offset.</param>
    public EngagementState ReadBlock(ushort blockRef) =>
        EngagementStateCodec.Decode(Read(blockRef, EngagementState.StateBytes));

    /// <summary>Writes the first <paramref name="count"/> bytes of a block.</summary>
    /// <param name="blockRef">The block's near offset.</param>
    /// <param name="state">The block.</param>
    /// <param name="count">32 or 55 — snapshot/restore's two sizes.</param>
    public void WriteBlock(ushort blockRef, EngagementState state, int count)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Bytes[..count].CopyTo(Span(blockRef, count));
    }

    /// <summary>A node's <c>+0x07</c> NEXT link.</summary>
    /// <param name="nodeRef">The node (= engagement block) near offset.</param>
    public ushort NodeNext(ushort nodeRef) => Word((ushort)(nodeRef + 0x07));

    /// <summary>Sets a node's NEXT link.</summary>
    /// <param name="nodeRef">The node's near offset.</param>
    /// <param name="next">The next node's near offset, or 0.</param>
    public void SetNodeNext(ushort nodeRef, ushort next) => SetWord((ushort)(nodeRef + 0x07), next);

    /// <summary>A node's <c>+0x0B</c> frame deadline — the list's sort key.</summary>
    /// <param name="nodeRef">The node's near offset.</param>
    public ushort NodeDeadline(ushort nodeRef) => Word((ushort)(nodeRef + 0x0B));

    /// <summary>Sets a node's frame deadline.</summary>
    /// <param name="nodeRef">The node's near offset.</param>
    /// <param name="deadline">The frame it is next due.</param>
    public void SetNodeDeadline(ushort nodeRef, ushort deadline) =>
        SetWord((ushort)(nodeRef + 0x0B), deadline);

    /// <summary>A node's <c>+0x0D</c> phase / subtype byte.</summary>
    /// <param name="nodeRef">The node's near offset.</param>
    public byte NodePhase(ushort nodeRef) => Byte((ushort)(nodeRef + 0x0D));

    /// <summary>A node's <c>+0x2D</c> polymorphic word (the expiry loop reads it as an owner).</summary>
    /// <param name="nodeRef">The node's near offset.</param>
    public ushort NodePhaseWord(ushort nodeRef) => Word((ushort)(nodeRef + 0x2D));

    /// <summary>A node's <c>+0x02</c> owning-object back reference.</summary>
    /// <param name="nodeRef">The node's near offset.</param>
    public ushort NodeOwnerObject(ushort nodeRef) => Word((ushort)(nodeRef + 0x02));

    /// <summary>Walks the expiry list from a head, yielding node near offsets.</summary>
    /// <param name="head">The head node's near offset, or 0 for an empty list.</param>
    /// <param name="maxNodes">A safety bound on a corrupt chain.</param>
    public IEnumerable<ushort> WalkList(ushort head, int maxNodes = 256)
    {
        HashSet<ushort> seen = new HashSet<ushort>();
        ushort cursor = head;
        while (cursor != 0 && seen.Add(cursor) && seen.Count <= maxNodes && Covers(cursor, 0x37))
        {
            yield return cursor;
            cursor = NodeNext(cursor);
        }
    }

    private int Require(ushort nearOffset, int length)
    {
        int index = nearOffset - BaseOffset;
        return index >= 0 && index + length <= _bytes.Length
            ? index
            : throw new ArgumentOutOfRangeException(
                nameof(nearOffset),
                $"near offset 0x{nearOffset:X4}+{length} is outside the dumped arena "
                    + $"[0x{BaseOffset:X4}, 0x{BaseOffset + _bytes.Length:X4})");
    }
}
