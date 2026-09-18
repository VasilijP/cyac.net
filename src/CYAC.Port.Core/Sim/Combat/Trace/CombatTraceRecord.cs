using System.Buffers.Binary;

namespace CYAC.Port.Core.Sim.Combat.Trace;

/// <summary>
/// The <c>flags</c> byte of a combat-trace record.
/// </summary>
/// <remarks>
/// Bits 1..7 are RESET at CS0, so a frame's render arm must be read from its CS9 or CS10 record and
/// never from CS0 — the direct analogue of K0's finding 4.6.
/// </remarks>
[Flags]
public enum CombatTraceFlags : byte
{
    /// <summary>No flag set.</summary>
    None = 0x00,

    /// <summary>bit0 — the record's step is an IDLE step the watchdog opened.  Per STEP.</summary>
    IdleStep = 0x01,

    /// <summary>bit1 — the frame took the SHORT render arm (<c>image@0x01577</c>).  Per FRAME.</summary>
    ShortRenderArm = 0x02,

    /// <summary>bit2 — the frame took the FULL render arm (<c>image@0x01598</c>).  Per FRAME.</summary>
    FullRenderArm = 0x04,

    /// <summary>bit3 — at least one key was dispatched this frame (<c>image@0x00E27</c>).</summary>
    KeyDispatched = 0x08,

    /// <summary>
    /// bit4 — the frame hit the key-<c>0x0B</c> escape (<c>image@0x00E6F</c>) and ended after CS4.
    /// </summary>
    KeyEscape = 0x10,

    /// <summary>bit5 — reserved, always 0 in v1.</summary>
    Reserved5 = 0x20,

    /// <summary>bit6 — reserved, always 0 in v1.</summary>
    Reserved6 = 0x40,

    /// <summary>bit7 — reserved, always 0 in v1.</summary>
    Reserved7 = 0x80,
}

/// <summary>
/// One fixed-size record of a combat trace — a STAGE window or a PROBE window.
/// </summary>
/// <remarks>
/// <para>
/// <b>The buffer is REUSED.</b>  <see cref="CombatTraceReader.Records"/> yields the same underlying
/// array over and over, so a 175 MB trace costs no allocation; a record that must outlive the next
/// iteration has to be <see cref="Copy"/>-ed.  <see cref="CombatTraceReader.Frames"/> copies for you.
/// </para>
/// <para>
/// Every offset comes from the <see cref="Header"/>, never from a constant here.
/// </para>
/// </remarks>
public readonly struct CombatTraceRecord
{
    private readonly byte[] _buffer;
    private readonly int _length;

    internal CombatTraceRecord(CombatTraceHeader header, byte[] buffer, int length)
    {
        Header = header;
        _buffer = buffer;
        _length = length;
    }

    /// <summary>The trace's header — the source of every offset below.</summary>
    public CombatTraceHeader Header { get; }

    /// <summary>True for a default-constructed record ("no record").</summary>
    public bool IsEmpty => _buffer is null;

    /// <summary>The record's raw bytes.</summary>
    public ReadOnlySpan<byte> Bytes => _buffer.AsSpan(0, _length);

    /// <summary>The leading kind byte: 0 stage, 1 probe entry, 2 probe exit.</summary>
    public byte Kind => _buffer[Header.Field("kind")];

    /// <summary>True for a STAGE record.</summary>
    public bool IsStage => Kind == CombatTraceHeader.KindStage;

    /// <summary>True for a PROBE record of either direction.</summary>
    public bool IsProbe => Kind is CombatTraceHeader.KindProbeEntry or CombatTraceHeader.KindProbeExit;

    /// <summary>True for a probe ENTRY record.</summary>
    public bool IsProbeEntry => Kind == CombatTraceHeader.KindProbeEntry;

    /// <summary>True for a probe EXIT record.</summary>
    public bool IsProbeExit => Kind == CombatTraceHeader.KindProbeExit;

    /// <summary>The stage id (0..10) or probe id (0..21).</summary>
    public int Id => _buffer[Header.Field("id")];

    /// <summary>The stage this record was taken at, or null when it is a probe / unknown id.</summary>
    public CombatTraceStage? Stage =>
        IsStage && Header.Stages.TryGetValue(Id, out CombatTraceStage stage) ? stage : null;

    /// <summary>The probe this record belongs to, or null when it is a stage / unknown id.</summary>
    public CombatTraceProbe? Probe =>
        IsProbe && Header.Probes.TryGetValue(Id, out CombatTraceProbe probe) ? probe : null;

    /// <summary>The record's flag bits.</summary>
    public CombatTraceFlags Flags => (CombatTraceFlags)_buffer[Header.Field("flags")];

    /// <summary>
    /// The <c>aux</c> byte: on CS5 the key-ladder iteration count for the frame; on a probe the call
    /// depth after the event; 0 otherwise.
    /// </summary>
    public byte Aux => _buffer[Header.Field("aux")];

    /// <summary>The determinism simulation step (<c>DetSteps</c>) at the trap.</summary>
    /// <remarks>
    /// NOT a frame counter — a watchdog idle step can bump it mid-frame, which is why frames are
    /// grouped by the CS0 record and never by this ("the step/frame relation").
    /// </remarks>
    public uint Step => BinaryPrimitives.ReadUInt32LittleEndian(Slice("step", 4));

    /// <summary>The emulator's instruction count at the trap — a total order over all records.</summary>
    public ulong InstructionCount => BinaryPrimitives.ReadUInt64LittleEndian(Slice("instr", 8));

    /// <summary><c>g_scene_frame_dt_scaled [0xF11C]</c> — 5 on every recorded combat record.</summary>
    public ushort Dt => BinaryPrimitives.ReadUInt16LittleEndian(Slice("dt", 2));

    /// <summary><c>g_prng_state_lo [0x07A8]</c> — the Sim LFSR word, the RNG draw oracle.</summary>
    public ushort RandomState => BinaryPrimitives.ReadUInt16LittleEndian(Slice("rng", 2));

    /// <summary>The pool arena's used length in bytes; <c>0xFFFF</c> means the u16 clamped.</summary>
    public ushort PoolUsed => BinaryPrimitives.ReadUInt16LittleEndian(Slice("poolUsed", 2));

    /// <summary>FNV-1a 32 over the arena's used region — the arena's identity per record.</summary>
    public uint PoolHash => BinaryPrimitives.ReadUInt32LittleEndian(Slice("poolHash", 4));

    /// <summary><c>g_engagement_script_seg [0xED74]</c>; 0 = no program loaded.</summary>
    public ushort ExecSegment => BinaryPrimitives.ReadUInt16LittleEndian(Slice("execSeg", 2));

    /// <summary><c>g_engagement_script_pc [0xED76]</c> as the SIGNED word it is; −1 = none.</summary>
    public short ExecPc => BinaryPrimitives.ReadInt16LittleEndian(Slice("execPc", 2));

    /// <summary>Probe ENTRY: the return offset on the stack.  Else 0.</summary>
    public ushort CallerIp => BinaryPrimitives.ReadUInt16LittleEndian(Slice("callerIp", 2));

    /// <summary>Probe ENTRY of a FAR function: the return segment.  Else 0.</summary>
    public ushort CallerCs => BinaryPrimitives.ReadUInt16LittleEndian(Slice("callerCs", 2));

    /// <summary>The 298-byte <c>s_aircraft_master</c> window (STAGE records only).</summary>
    public ReadOnlySpan<byte> Master =>
        _buffer.AsSpan(Header.Field("master"), Header.MasterLength);

    /// <summary>
    /// The 79-byte PLAYER pool object through <c>g_alt_object_farptr [0x00C0]</c> (STAGE records).
    /// </summary>
    public ReadOnlySpan<byte> PlayerObject =>
        _buffer.AsSpan(Header.Field("playerObj"), Header.PoolObjectLength);

    /// <summary>The pool arena's used region, when the leg was run with <c>--combat-trace-pool</c>.</summary>
    /// <remarks>Empty when <see cref="CombatTraceHeader.PoolLength"/> is 0.</remarks>
    public ReadOnlySpan<byte> Pool =>
        Header.PoolLength > 0 && Header.HasField("pool")
            ? _buffer.AsSpan(Header.Field("pool"), Header.PoolLength)
            : [];

    /// <summary><b>138 bytes, <c>[0xED3C..0xEDC5]</c></b> from format v1.2: the window now carries
    /// the span C3a had to reconstruct, and stops exactly at the snapshot-join window
    /// <c>[0xEDC6]</c>; §13.1 (PROBE records only).  The length always comes from
    /// <see cref="CombatTraceHeader.VmRegisterLength"/>, never from a constant.</summary>
    public ReadOnlySpan<byte> VmRegisters =>
        _buffer.AsSpan(Header.Field("probeVm"), Header.VmRegisterLength);

    /// <summary>Subject 1 — the pool object the probe names (PROBE records only, §5).</summary>
    public ReadOnlySpan<byte> Subject1 =>
        _buffer.AsSpan(Header.Field("probeSubj1"), Header.PoolObjectLength);

    /// <summary>Subject 2 — <c>g_acq_current_target [0xED6F]</c>'s pool object (PROBE records).</summary>
    public ReadOnlySpan<byte> Subject2 =>
        _buffer.AsSpan(Header.Field("probeSubj2"), Header.PoolObjectLength);

    /// <summary>The subject <c>s_combat_spawn_record</c> (P0 and P8 only; zero otherwise).</summary>
    public ReadOnlySpan<byte> SpawnRecord =>
        _buffer.AsSpan(Header.Field("probeSpawn"), Header.SpawnRecordLength);

    /// <summary>The head of the VM exec buffer (zero when <see cref="ExecSegment"/> is 0).</summary>
    public ReadOnlySpan<byte> ExecBuffer =>
        _buffer.AsSpan(Header.Field("probeExec"), Header.ProbeExecLength);

    /// <summary>
    /// The VM register file's TAIL, DGROUP <c>[0xEDCE..0xEE05]</c>, on PROBE records: the home of
    /// <c>g_engagement_kill_fired_flag [0xEDE5]</c>, which <c>weapon_fire_combat_loop_per_shot</c>
    /// writes and <c>engagement_expiry_loop</c> reads after the drain.
    /// EMPTY on a pre-v1.1 trace.
    /// </summary>
    public ReadOnlySpan<byte> ScratchTail =>
        Header.ScratchTailLength > 0 && Header.HasField("probeScratchTail")
            ? _buffer.AsSpan(Header.Field("probeScratchTail"), Header.ScratchTailLength)
            : [];

    /// <summary>
    /// The near offset <see cref="Subject1"/> was read at.  For a BX-subject probe this is the
    /// ENTRY's <c>BX</c> on BOTH records of the call; otherwise it is <c>g_alt_object_farptr
    /// [0x00C0]</c>.  0 means "no object" — which is how a reader tells that apart from an all-zero
    /// object.  Null on a pre-v1.1 trace.
    /// </summary>
    public ushort? Subject1Offset => Header.HasField("probeSubj1Off")
        ? BinaryPrimitives.ReadUInt16LittleEndian(Slice("probeSubj1Off", 2))
        : null;

    /// <summary>
    /// The near offset <see cref="Subject2"/> was read at, i.e. <c>g_acq_current_target
    /// [0xED6F]</c> at the moment of the record.  Removes C1 §4.6.3's inference.  Null on a
    /// pre-v1.1 trace.
    /// </summary>
    public ushort? Subject2Offset => Header.HasField("probeSubj2Off")
        ? BinaryPrimitives.ReadUInt16LittleEndian(Slice("probeSubj2Off", 2))
        : null;

    /// <summary>
    /// The pool segment both subject windows were read in (<c>g_object_pool_segment [0x0094]</c>,
    /// falling back to <c>[0x00C2]</c>).  Null on a pre-v1.1 trace.
    /// </summary>
    public ushort? SubjectSegment => Header.HasField("probeSubjSeg")
        ? BinaryPrimitives.ReadUInt16LittleEndian(Slice("probeSubjSeg", 2))
        : null;

    /// <summary>
    /// The near pointer a <c>world_grid_frustum_query_and_select</c> (P20) caller passed as its
    /// OUT block (far stack arg 5, <c>[bp+0x10]</c>), latched at entry.  0 on every other probe;
    /// null on a pre-v1.1 trace.
    /// </summary>
    public ushort? OutPointer => Header.HasField("probeOutPtr")
        ? BinaryPrimitives.ReadUInt16LittleEndian(Slice("probeOutPtr", 2))
        : null;

    /// <summary>
    /// The six words at <see cref="OutPointer"/>: on a P20 EXIT record, the selection point the
    /// query wrote.  EMPTY on a pre-v1.1 trace or a non-P20 record.
    /// </summary>
    public ReadOnlySpan<byte> OutBlock =>
        Header.OutBlockLength > 0 && Header.HasField("probeOutBlock")
            ? _buffer.AsSpan(Header.Field("probeOutBlock"), Header.OutBlockLength)
            : [];

    /// <summary>
    /// Subject 3: the 79-byte pool object at <c>g_engagement_player_slot_nearptr [0xED56]</c>, i.e.
    /// the engagement block's OWNER slot (PROBE records).  EMPTY on a pre-v1.3 trace.
    /// </summary>
    /// <remarks>
    /// The machine's own read is what makes this the right window:
    /// <c>engagement_state_snapshot @image@0x02290</c> is
    /// <c>mov si,ss:[0xED56] / mov di,0xED3C / mov cx,0xC / rep movsw</c> at
    /// <c>image@0x022C1..0x022CD</c>, so <c>[0xED3C..0xED53]</c> IS this object's first 24 bytes, and
    /// <c>weapon_fire_outcome_and_evasion_init</c> reads it past those 24 at
    /// <c>image@0x08841</c>/<c>0x08851</c>.  It is a DIFFERENT object from both v1.2 subjects — C4
    /// One measurement read <c>[0xED56] = 0x48F9</c> where both stamps said <c>0x4A41</c>, and on
    /// the C0d canary the stamp differs from BOTH on 5,102 of 5,102 probe records.
    /// <see cref="Subject3Offset"/> is 0 exactly when there was no object; the window is dumped from
    /// the pool segment's head in that case, exactly like subject 2 (§13.4's rule, unchanged).
    /// </remarks>
    public ReadOnlySpan<byte> Subject3 =>
        Header.HasField("probeSubj3")
            ? _buffer.AsSpan(Header.Field("probeSubj3"), Header.PoolObjectLength)
            : [];

    /// <summary>
    /// The near offset <see cref="Subject3"/> was read at (<c>[0xED56]</c> itself). Null on a
    /// pre-v1.3 trace.
    /// </summary>
    public ushort? Subject3Offset => Header.HasField("probeSubj3Off")
        ? BinaryPrimitives.ReadUInt16LittleEndian(Slice("probeSubj3Off", 2))
        : null;

    /// <summary>
    /// The head of the exec buffer at the SUBJECT BLOCK's own script segment, i.e. at
    /// <c>block[+0x20]</c> (PROBE records of the BX-subject probes P3/P12/P17/P21).  EMPTY on a
    /// pre-v1.3 trace, and zero when <see cref="SubjectExecSegment"/> is 0.
    /// </summary>
    /// <remarks>
    /// <c>engagement_state_snapshot</c> copies <c>block+0x00..+0x36</c> to <c>[0xED54..0xED8A]</c>
    /// (<c>mov di,0xED54</c> @<c>image@0x0229C</c>, <c>mov cx,0x1B / rep movsw / movsb</c>
    /// @<c>image@0x022BB</c>), so <c>block+0x20</c> IS <c>g_engagement_script_seg [0xED74]</c> after
    /// the snapshot — and a P3 step that takes that path runs a program the ENTRY record's
    /// <see cref="ExecBuffer"/> (the OLD segment's) does not describe.  A zero-filled block
    /// stands in for it, and the one call where that is not provable is counted.
    /// </remarks>
    public ReadOnlySpan<byte> SubjectExecBuffer =>
        Header.HasField("probeExecSubj")
            ? _buffer.AsSpan(Header.Field("probeExecSubj"), Header.ProbeExecLength)
            : [];

    /// <summary>
    /// The segment <see cref="SubjectExecBuffer"/> was read at (the subject block's <c>+0x20</c>); 0
    /// when the probe has no block subject or the block carries no program.  Null on a pre-v1.3
    /// trace.
    /// </summary>
    public ushort? SubjectExecSegment => Header.HasField("probeExecSubjSeg")
        ? BinaryPrimitives.ReadUInt16LittleEndian(Slice("probeExecSubjSeg", 2))
        : null;

    /// <summary>
    /// <c>combat_target_score_and_fire</c>'s final SCORE, latched at its own compare <c>cmp
    /// ax,[bp-0x2c]</c> (<c>image@0x07B8E</c>, where <c>AX</c> holds the value just stored to
    /// <c>[bp-0x28]</c>).  Null on a pre-v1.4 trace or on a record that is not a P22 EXIT.
    /// </summary>
    /// <remarks>
    /// The <c>jb</c> after that compare is the function's ONLY success arm, so
    /// <c>score &lt; floor</c> is exactly "a target was selected".  A P22 ENTRY record carries
    /// zeros: the latch is cleared there, so an entry value is never a measurement.
    /// </remarks>
    public ushort? ScoreCompareScore => ScoreCompareWord(0);

    /// <summary>
    /// The FLOOR the score is compared against (<c>[bp-0x2C]</c> at
    /// <c>image@0x07B8E</c>).  Null on a pre-v1.4 trace or a non-P22-exit record.
    /// </summary>
    public ushort? ScoreCompareFloor => ScoreCompareWord(1);

    /// <summary>
    /// How many times the call reached <c>image@0x07B8E</c>.  0 means the call returned before
    /// scoring any candidate (the <c>image@0x079B0</c> retry-timer arm); the bytes say it can
    /// never exceed 1, and the recorder MEASURES that instead of asserting it.
    /// </summary>
    public ushort? ScoreCompareHits => ScoreCompareWord(2);

    private ushort? ScoreCompareWord(int index)
    {
        if (!Header.ScoreCompareIsRecorded
            || Header.ScoreCompareLength < 6
            || !Header.HasField("probeScoreCmp")
            || Kind != CombatTraceHeader.KindProbeExit
            || Id != 22)
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(
            _buffer.AsSpan(Header.Field("probeScoreCmp") + (2 * index), 2));
    }

    /// <summary>
    /// The word at <c>object_screen_pos_project</c>'s FIRST out pointer (its ENTRY-latched
    /// <c>arg0</c>), which the perspective projector fills with the screen <b>Y</b>. Null on a
    /// pre-v1.6 trace or on a record that is not a P26 one.
    /// </summary>
    /// <remarks>
    /// P26 forwards <c>[bp+4]</c> and <c>[bp+6]</c> straight through to
    /// <c>gfx_perspective_project_trampoline</c>, whose slots are <c>[bp+6] = *out_y</c> and
    /// <c>[bp+8] = *out_x</c>, and P26 pushes <c>[bp+6]</c> before <c>[bp+4]</c> — so <c>arg0</c>
    /// is the OUT-Y pointer.  An ENTRY record carries the PRE-call value at the same pointer, so
    /// the pair says what the call changed.
    /// </remarks>
    public ushort? ProjectedScreenY => ProjectionOutWord(0);

    /// <summary>
    /// The word at P26's SECOND out pointer (its ENTRY-latched <c>arg1</c>), the screen
    /// <b>X</b>.  Null on a pre-v1.6 trace or a non-P26 record.
    /// </summary>
    public ushort? ProjectedScreenX => ProjectionOutWord(1);

    private ushort? ProjectionOutWord(int index)
    {
        if (!Header.ProjectionOutWordsAreRecorded
            || !Header.HasField("probeP26Out")
            || Kind == CombatTraceHeader.KindStage
            || Id != 26)
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(
            _buffer.AsSpan(Header.Field("probeP26Out") + (2 * index), 2));
    }

    /// <summary>
    /// One DGROUP window a PROBE record carries, by the name the header's <c>probeGlobals</c> array
    /// gives it.
    /// </summary>
    /// <param name="name">The window's name, e.g. <c>"engagement_arc_and_snapshot_keys"</c>.</param>
    /// <exception cref="ArgumentException">The header does not carry that probe window.</exception>
    public ReadOnlySpan<byte> ProbeGlobal(string name)
    {
        if (!Header.ProbeGlobals.TryGetValue(name, out CombatTraceGlobal window))
        {
            throw new ArgumentException(
                $"the combat trace does not carry a probe window named '{name}' (pre-v1.2 traces "
                    + "carry no `probeGlobals` array at all)",
                nameof(name));
        }

        return _buffer.AsSpan(window.RecordOffset, window.Length);
    }

    /// <summary>
    /// Reads any DGROUP address that falls inside a PROBE record's windows: the VM register file,
    /// the scratch tail, <c>[0xB52E..0xB547]</c>, <c>[0xF0D2..0xF0D5]</c> and <c>[0x0F0C]</c>.
    /// The probe-record twin of <see cref="Dgroup"/>.
    /// </summary>
    /// <param name="dgroupOffset">The DGROUP offset, e.g. <c>0xB532</c>.</param>
    /// <param name="length">How many bytes.</param>
    /// <exception cref="ArgumentException">No probe window covers the whole range.</exception>
    public ReadOnlySpan<byte> ProbeDgroup(int dgroupOffset, int length = 2)
    {
        int offset = Header.ProbeRecordOffsetOfDgroup(dgroupOffset, length);
        if (offset < 0)
        {
            throw new ArgumentException(
                $"DGROUP 0x{dgroupOffset:X4}+{length} is not inside any window a PROBE record "
                    + "carries",
                nameof(dgroupOffset));
        }

        return _buffer.AsSpan(offset, length);
    }

    /// <summary>A DGROUP word from a probe record's windows.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    public ushort ProbeDgroupWord(int dgroupOffset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(ProbeDgroup(dgroupOffset, 2));

    /// <summary>A DGROUP byte from a probe record's windows.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    public byte ProbeDgroupByte(int dgroupOffset) => ProbeDgroup(dgroupOffset, 1)[0];

    /// <summary>One of the fourteen captured register words.</summary>
    /// <param name="name">A name from the header's <c>regNames</c> (<c>"bx"</c>, <c>"arg0"</c>, …).</param>
    /// <exception cref="ArgumentException">The header does not carry that register.</exception>
    public ushort Register(string name)
    {
        int index = Header.RegisterIndex(name);
        if (index < 0)
        {
            throw new ArgumentException(
                $"the combat trace does not carry a register named '{name}'", nameof(name));
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(
            _buffer.AsSpan(Header.Field("regs") + (index * 2), 2));
    }

    /// <summary>One DGROUP window, by the name the header gives it (STAGE records only).</summary>
    /// <param name="name"></param>
    /// <exception cref="ArgumentException">The header does not carry that window.</exception>
    public ReadOnlySpan<byte> Global(string name)
    {
        if (!Header.Globals.TryGetValue(name, out CombatTraceGlobal global))
        {
            throw new ArgumentException(
                $"the combat trace does not carry a global window named '{name}'", nameof(name));
        }

        return _buffer.AsSpan(global.RecordOffset, global.Length);
    }

    /// <summary>Reads any DGROUP address that falls inside a dumped window (STAGE records only).</summary>
    /// <param name="dgroupOffset">The DGROUP offset, e.g. <c>0xEDAA</c>.</param>
    /// <param name="length">How many bytes.</param>
    /// <exception cref="ArgumentException">No dumped window covers the whole range.</exception>
    public ReadOnlySpan<byte> Dgroup(int dgroupOffset, int length = 2)
    {
        int offset = Header.RecordOffsetOfDgroup(dgroupOffset, length);
        if (offset < 0)
        {
            throw new ArgumentException(
                $"DGROUP 0x{dgroupOffset:X4}+{length} is not inside any dumped window",
                nameof(dgroupOffset));
        }

        return _buffer.AsSpan(offset, length);
    }

    /// <summary>A DGROUP word.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    public ushort DgroupWord(int dgroupOffset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(Dgroup(dgroupOffset, 2));

    /// <summary>A DGROUP byte.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    public byte DgroupByte(int dgroupOffset) => Dgroup(dgroupOffset, 1)[0];

    /// <summary>An owning copy that survives the reader's next step.</summary>
    public CombatTraceRecord Copy() => new(Header, [.. Bytes], _length);

    /// <summary>A one-line description for test output.</summary>
    public override string ToString() =>
        IsStage
            ? $"step {Step} CS{Id} ({Stage?.Name ?? "?"}) flags=0x{(byte)Flags:X2} rng=0x{RandomState:X4}"
            : $"step {Step} P{Id} {(IsProbeEntry ? "in " : "out")} ({Probe?.Name ?? "?"}) "
                + $"rng=0x{RandomState:X4}";

    private ReadOnlySpan<byte> Slice(string field, int length) =>
        _buffer.AsSpan(Header.Field(field), length);
}
