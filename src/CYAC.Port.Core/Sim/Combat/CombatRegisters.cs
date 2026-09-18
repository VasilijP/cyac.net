using System.Buffers.Binary;
using CYAC.Port.Core.Model.Combat;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>Which kernel half owns a combat register window.</summary>
public enum CombatFieldClass
{
    /// <summary>INT-only: part of the reproducible spine.  Verification compares it.</summary>
    IntegerSpine,

    /// <summary>
    /// FLOAT-only / presentation: HUD caches, render scratch, radar snapshots.  Verification MASKS
    /// it, because the port's own renderer produces it and the original's bytes are not a contract.
    /// </summary>
    Presentation,
}

/// <summary>One window of the combat register file.</summary>
/// <param name="Name"></param>
/// <param name="DgroupOffset">Its DGROUP offset.</param>
/// <param name="Length">Its length in bytes.</param>
/// <param name="Class">Whether verification compares or masks it.</param>
public readonly record struct CombatRegisterWindow(
    string Name, int DgroupOffset, int Length, CombatFieldClass Class);

/// <summary>
/// The combat kernel's DGROUP register file: every global the per-frame combat ladder reads or
/// writes, as one addressable block.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is offset-addressed and not a list of C# fields.</b>  The original's combat globals are
/// not independent variables — they are OVERLAYS.  <c>[0xED54..0xED8A]</c> IS an
/// <see cref="EngagementState"/> (the VM scratch that <c>engagement_state_snapshot</c> fills), and
/// <c>[0xED3C..0xED53]</c> IS a 24-byte copy of a pool object (<c>rep movsw cx=0x0C</c>
/// @<c>image@0x022CC</c>, copied back 9 or 12 words by
/// <c>engagement_state_restore</c> @<c>image@0x0234C</c>).  Several of them alias at sub-word width.
/// A flat, offset-addressed store is therefore the only faithful representation; the typed
/// properties below are the API game logic uses, and the offsets stay inside this file
/// (README: "no DGROUP offsets in game logic").
/// </para>
/// <para>
/// <b>The <c>[0xED3C]</c> block is a POOL-OBJECT COPY, not a bag of unrelated globals.</b> Read the
/// snapshot's second <c>rep movsw</c> and the field names line up one for one with
/// <c>s_pool_arena_entry</c>: <c>[0xED3C]</c> = <c>+0x00</c> class pointer, <c>[0xED3E]</c> =
/// <c>+0x02</c> flag word (which is why <c>test word ptr [0xed3e],2</c> @<c>image@0x022D0</c> — the
/// <c>NoOrientation</c> bit — decides whether to zero the angle triple), <c>[0xED42..0xED4D]</c> =
/// the three <c>i32</c> positions, and <c>[0xED4E/0x50/0x52]</c> = heading/pitch/roll.  See
/// <see cref="SubjectObject"/>.
/// </para>
/// <para>
/// <b>Shape.</b>  The store mirrors a combat trace's globals area byte for byte, so
/// <see cref="CombatRegistersCodec"/>'s round trip is exact and a verification test can diff the
/// port's post-state against a stage record with no re-layout.  A register file built without a
/// trace uses <see cref="CombatRegisterWindows.Core"/>.
/// </para>
/// </remarks>
public sealed class CombatRegisters
{
    private bool[]? _written;

    private readonly CombatRegisterWindow[] _windows;
    private readonly int[] _storeOffsets;
    private readonly byte[] _bytes;

    /// <summary>Builds an all-zero register file over a window layout.</summary>
    /// <param name="windows">The windows, in any order; they must not overlap.</param>
    public CombatRegisters(IEnumerable<CombatRegisterWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        _windows = [.. windows.OrderBy(w => w.DgroupOffset)];

        for (int i = 1; i < _windows.Length; i++)
        {
            if (_windows[i].DgroupOffset < _windows[i - 1].DgroupOffset + _windows[i - 1].Length)
            {
                throw new ArgumentException(
                    $"combat register windows overlap: {_windows[i - 1].Name} and {_windows[i].Name}",
                    nameof(windows));
            }
        }

        _storeOffsets = new int[_windows.Length];
        int cursor = 0;
        for (int i = 0; i < _windows.Length; i++)
        {
            _storeOffsets[i] = cursor;
            cursor += _windows[i].Length;
        }

        _bytes = new byte[cursor];
    }

    /// <summary>The windows, in ascending DGROUP order.</summary>
    public IReadOnlyList<CombatRegisterWindow> Windows => _windows;

    /// <summary>Total bytes the file holds.</summary>
    public int ByteCount => _bytes.Length;

    /// <summary>The whole store, in window order — the encode side of the codec's round trip.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>The whole store, writable — for the codec only.</summary>
    internal Span<byte> MutableBytes => _bytes;

    /// <summary>True when a DGROUP range falls entirely inside one window.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    /// <param name="length">How many bytes.</param>
    public bool Covers(int dgroupOffset, int length) => StoreOffset(dgroupOffset, length) >= 0;

    /// <summary>Reads a DGROUP range.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    /// <param name="length">How many bytes.</param>
    /// <exception cref="ArgumentException">No window covers the range.</exception>
    public ReadOnlySpan<byte> Read(int dgroupOffset, int length) =>
        _bytes.AsSpan(Require(dgroupOffset, length), length);

    /// <summary>Writes a DGROUP range.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    /// <param name="source">The bytes to write.</param>
    /// <exception cref="ArgumentException">No window covers the range.</exception>
    public void Write(int dgroupOffset, ReadOnlySpan<byte> source) =>
        source.CopyTo(_bytes.AsSpan(Require(dgroupOffset, source.Length), source.Length));

    /// <summary>
    /// Turns on WRITE TRACKING — the exact analogue of <see cref="PoolArena.TrackWrites"/>, and for
    /// the same reason: a verification has to separate the bytes the ported piece wrote from the
    /// bytes an oracle-fed seam's callee wrote, and the register file is shared between them.
    /// (C2 addition; off by default and costing nothing when off.)
    /// </summary>
    public void TrackWrites() => _written ??= new bool[_bytes.Length];

    /// <summary>
    /// Clears the write set without turning tracking off — C7's addition, so the assembled driver's
    /// verification can separate the write set of ONE stage (CS<i>k</i> → CS<i>k</i>+1) from the
    /// frame's.  A no-op when tracking is off.
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

    /// <summary>Whether a DGROUP byte has been written since <see cref="TrackWrites"/>.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    public bool WasWritten(int dgroupOffset)
    {
        if (_written is null)
        {
            return false;
        }

        int index = StoreOffset(dgroupOffset, 1);
        return index >= 0 && _written[index];
    }

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
    /// Suspends write tracking until the returned handle is disposed, then restores the write set —
    /// so a seam's installed bytes do not join the port's own write set.  (C2 addition.)
    /// </summary>
    /// <returns>A handle that restores the write set.</returns>
    public IDisposable SuspendWriteTracking() => new WriteTrackingScope(this);

    private sealed class WriteTrackingScope : IDisposable
    {
        private readonly CombatRegisters _registers;
        private readonly bool[]? _saved;

        public WriteTrackingScope(CombatRegisters registers)
        {
            _registers = registers;
            _saved = registers._written is null ? null : (bool[])registers._written.Clone();
            registers._written = null;
        }

        public void Dispose() => _registers._written = _saved;
    }

    /// <summary>A writable span over a DGROUP range.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    /// <param name="length">How many bytes.</param>
    public Span<byte> Span(int dgroupOffset, int length)
    {
        int index = Require(dgroupOffset, length);
        if (_written is not null)
        {
            for (int i = 0; i < length; i++)
            {
                _written[index + i] = true;
            }
        }

        return _bytes.AsSpan(index, length);
    }

    /// <summary>Reads a DGROUP word.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    public ushort Word(int dgroupOffset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(Read(dgroupOffset, 2));

    /// <summary>Writes a DGROUP word.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    /// <param name="value">The value.</param>
    public void SetWord(int dgroupOffset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(Span(dgroupOffset, 2), value);

    /// <summary>Reads a DGROUP byte.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    public byte Byte(int dgroupOffset) => Read(dgroupOffset, 1)[0];

    /// <summary>Writes a DGROUP byte.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    /// <param name="value">The value.</param>
    public void SetByte(int dgroupOffset, byte value) => Span(dgroupOffset, 1)[0] = value;

    // ---- the named registers the C1 deliverables touch -----------------------------------------

    /// <summary>
    /// <c>g_prng_state_lo [0x07A8]</c> — the Sim LFSR16 word.  Every combat draw advances it, and a
    /// stage's post-state word is the verified proof of its draw COUNT and ORDER.
    /// </summary>
    public ushort RandomState { get => Word(0x07A8); set => SetWord(0x07A8, value); }

    /// <summary>
    /// <c>g_object_pool_segment [0x0094]</c> — the segment every arena near pointer is read in.
    /// </summary>
    public ushort PoolSegment { get => Word(0x0094); set => SetWord(0x0094, value); }

    /// <summary>
    /// <c>g_alt_object_farptr [0x00C0]</c> — the PLAYER's pool-object near offset, and the head of
    /// the sibling chain the arena walk follows.
    /// </summary>
    public ushort PlayerObjectRef { get => Word(0x00C0); set => SetWord(0x00C0, value); }

    /// <summary>
    /// <c>g_master_frame_counter [0xF0C8]</c> — what the expiry loop compares every node's
    /// <see cref="EngagementState.FrameDeadline"/> against.
    /// </summary>
    public ushort MasterFrameCounter { get => Word(0xF0C8); set => SetWord(0xF0C8, value); }

    /// <summary>
    /// <c>g_scene_init_guard_flag [0x0F0B]</c> — when it is 0,
    /// <c>engagement_list_node_sorted_insert</c> degrades to a plain PUSH FRONT
    /// (<c>cmp byte ptr [0xf0b],0 / jne</c> @<c>image@0x02373</c>).
    /// </summary>
    public byte SceneInitGuard { get => Byte(0x0F0B); set => SetByte(0x0F0B, value); }

    /// <summary>
    /// <c>g_engagement_expiry_list_head [0xEDAA]</c> — the arena near offset of the first node.
    /// </summary>
    public ushort ExpiryListHead { get => Word(0xEDAA); set => SetWord(0xEDAA, value); }

    /// <summary>
    /// <c>g_engagement_kill_fired_flag [0xEDE5]</c> — cleared at the top of every expiry loop and set
    /// by the kill path; a non-zero value at the end of the drain runs the camera-reset arm.
    /// </summary>
    public byte KillFiredFlag { get => Byte(0xEDE5); set => SetByte(0xEDE5, value); }

    /// <summary>
    /// <c>g_engagement_node_pass_enabled (ex-g_scene_byte_flag_EDE1) [0xEDE1]</c> — when it is 0 the expiry loop SKIPS the per-node FSM
    /// (<c>cmp byte ptr [0xede1],0 / je</c> @<c>image@0x073DE</c>) but still restores and re-inserts.
    /// </summary>
    public byte NodePassEnabled { get => Byte(0xEDE1); set => SetByte(0xEDE1, value); }

    /// <summary>
    /// <c>g_combat_spawn_active_count [0xED20]</c> — the active-slot count the projectile walk leaves.
    /// </summary>
    public ushort SpawnActiveCount { get => Word(0xED20); set => SetWord(0xED20, value); }

    /// <summary><c>g_engagements_targeting_player (ex-g_scene_word_F122) [0xF122]</c> — decremented on a target departure.</summary>
    public ushort SceneWordF122 { get => Word(0xF122); set => SetWord(0xF122, value); }

    /// <summary><c>g_engagements_locked_on_player (ex-g_scene_word_F128) [0xF128]</c>, i16 — decremented on a target departure.</summary>
    public short SceneWordF128 { get => (short)Word(0xF128); set => SetWord(0xF128, (ushort)value); }

    /// <summary>
    /// <c>g_active_statblock_ptr [0xED54]</c> — the VM SCRATCH, which IS a 55-byte
    /// <see cref="EngagementState"/> at the same layout.  Reads and writes go straight through to
    /// the store, so a mutation of the returned object is a mutation of the register file.
    /// </summary>
    /// <remarks>
    /// Proven by the two copy routines: <c>engagement_state_snapshot</c> copies 32 or 55 bytes from
    /// the arena block to <c>di = 0xed54</c> (<c>image@0x0229C</c>), and
    /// <c>engagement_state_restore</c> copies them back from <c>si = 0xed54</c>
    /// (<c>image@0x0231C</c>).  Every <c>g_engagement_*</c> global in <c>[0xED54..0xED8A]</c> is
    /// therefore a field of THIS record.
    /// </remarks>
    public EngagementState Scratch
    {
        get => EngagementStateCodec.Decode(Read(ScratchDgroupOffset, EngagementState.StateBytes));
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Write(ScratchDgroupOffset, value.Bytes);
        }
    }

    /// <summary>The DGROUP offset of the VM scratch block.</summary>
    public const int ScratchDgroupOffset = 0xED54;

    /// <summary>The DGROUP offset of the subject-object copy the snapshot fills.</summary>
    public const int SubjectObjectDgroupOffset = 0xED3C;

    /// <summary>
    /// <c>[0xED3C..0xED53]</c> — the 24-byte copy of the pool object the scratch's <c>+0x02</c> names.
    /// See the type remarks for why this is a <c>s_pool_arena_entry</c> and not six unrelated globals.
    /// </summary>
    public ReadOnlySpan<byte> SubjectObject => Read(SubjectObjectDgroupOffset, 0x18);

    /// <summary>
    /// The subject object's flag word — <c>g_engagement_player_flags [0xED3E]</c>, i.e.
    /// <c>s_pool_arena_entry +0x02</c>.  The expiry loop's ALIVE gate is
    /// <c>(flags &amp; 0x801) == 0x801</c> (<c>image@0x07379..0x07382</c>): bit0 ACTIVE and bit11
    /// CARRIES-ENGAGEMENT both set.
    /// </summary>
    public ushort SubjectObjectFlags
    {
        get => Word(0xED3E);
        set => SetWord(0xED3E, value);
    }

    /// <summary>The DGROUP offset of the arc-parameter block the snapshot's third copy fills.</summary>
    public const int ArcParameterDgroupOffset = 0xED8E;

    /// <summary>
    /// <c>[0xED8E..]</c> — the arc-parameter block, 28 or 2 bytes copied from
    /// <c>prototype[+0x26]</c> by the snapshot's third <c>rep movsw</c>
    /// (<c>image@0x02301..0x02311</c>).
    /// </summary>
    public ReadOnlySpan<byte> ArcParameters => Read(ArcParameterDgroupOffset, ArcParameterBytes);

    /// <summary>Bytes of the arc-parameter block's long form: 28 (<c>0x1C</c>).</summary>
    public const int ArcParameterBytes = 0x1C;

    /// <summary>Bytes of the arc-parameter block's short form: 2.</summary>
    public const int ArcParameterBytesShort = 2;

    /// <summary>An owning copy of the whole file.</summary>
    public CombatRegisters Clone()
    {
        CombatRegisters copy = new CombatRegisters(_windows);
        _bytes.CopyTo(copy._bytes, 0);
        return copy;
    }

    /// <summary>A one-line description for test output.</summary>
    public override string ToString() =>
        $"combat registers: {_windows.Length} windows / {_bytes.Length} B, "
            + $"rng=0x{(Covers(0x07A8, 2) ? Word(0x07A8) : 0):X4}";

    private int StoreOffset(int dgroupOffset, int length)
    {
        for (int i = 0; i < _windows.Length; i++)
        {
            CombatRegisterWindow window = _windows[i];
            if (window.DgroupOffset <= dgroupOffset
                && dgroupOffset + length <= window.DgroupOffset + window.Length)
            {
                return _storeOffsets[i] + (dgroupOffset - window.DgroupOffset);
            }
        }

        return -1;
    }

    private int Require(int dgroupOffset, int length)
    {
        int offset = StoreOffset(dgroupOffset, length);
        return offset >= 0
            ? offset
            : throw new ArgumentException(
                $"DGROUP 0x{dgroupOffset:X4}+{length} is not inside any combat register window",
                nameof(dgroupOffset));
    }
}
