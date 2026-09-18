using System.Text;

namespace CYAC.Port.Core.Sim.Combat.Trace;

/// <summary>
/// One frame of a combat trace: the stage records between one CS0 and the next, plus the probe
/// records that fell inside it, in file order.
/// </summary>
/// <remarks>
/// <para>
/// A frame OPENS at CS0 and CLOSES at the last record before the next CS0.  It normally carries
/// CS0..CS10 exactly once each, with two measured exceptions: CS4 is the key-ladder LOOP HEAD and is
/// emitted only on its first arrival (the iteration count lands in CS5's <c>aux</c>), and a frame
/// that hits the key-<c>0x0B</c> escape ends after CS4 and carries
/// <see cref="CombatTraceFlags.KeyEscape"/>.
/// </para>
/// <para>
/// <see cref="IsComplete"/> says whether the frame reached CS10; a verifier normally skips the
/// incomplete ones and REPORTS how many it skipped.
/// </para>
/// </remarks>
public sealed class CombatTraceFrame
{
    private readonly Dictionary<int, CombatTraceRecord> _stages = [];
    private readonly List<CombatTraceRecord> _probes = [];

    internal CombatTraceFrame(CombatTraceRecord open)
    {
        _stages[open.Id] = open;
        Step = open.Step;
    }

    /// <summary>The det step the frame's CS0 record was taken in.</summary>
    public uint Step { get; }

    /// <summary>The stage records, by stage id.</summary>
    public IReadOnlyDictionary<int, CombatTraceRecord> Stages => _stages;

    /// <summary>The probe records that fell inside this frame, in file order.</summary>
    public IReadOnlyList<CombatTraceRecord> Probes => _probes;

    /// <summary>True when the frame reached CS10 (<c>frame_end</c>).</summary>
    public bool IsComplete => _stages.ContainsKey(10);

    /// <summary>True when the frame ended early through the key-<c>0x0B</c> escape.</summary>
    public bool EndedByKeyEscape =>
        _stages.TryGetValue(4, out CombatTraceRecord cs4) && cs4.Flags.HasFlag(CombatTraceFlags.KeyEscape);

    /// <summary>The record for a stage id, or an empty record when the frame has none.</summary>
    /// <param name="id">The stage id, 0..10.</param>
    public CombatTraceRecord Stage(int id) =>
        _stages.TryGetValue(id, out CombatTraceRecord record) ? record : default;

    /// <summary>True when the frame carries a record for a stage id.</summary>
    /// <param name="id">The stage id, 0..10.</param>
    public bool HasStage(int id) => _stages.ContainsKey(id);

    /// <summary>Every probe record of one probe id, in file order.</summary>
    /// <param name="id">The probe id, 0..21.</param>
    public IEnumerable<CombatTraceRecord> ProbesOf(int id) => _probes.Where(p => p.Id == id);

    /// <summary>
    /// The probe records that fell BETWEEN two stage records, by instruction count — which is how a
    /// verifier attributes a call to the stage that made it.
    /// </summary>
    /// <param name="id">The probe id.</param>
    /// <param name="fromStage">The opening stage id (exclusive lower bound on <c>instr</c>).</param>
    /// <param name="toStage">The closing stage id (inclusive upper bound on <c>instr</c>).</param>
    public IEnumerable<CombatTraceRecord> ProbesBetween(int id, int fromStage, int toStage)
    {
        if (!_stages.TryGetValue(fromStage, out CombatTraceRecord open) || !_stages.TryGetValue(toStage, out CombatTraceRecord close))
        {
            yield break;
        }

        foreach (CombatTraceRecord probe in _probes)
        {
            if (probe.Id == id
                && probe.InstructionCount >= open.InstructionCount
                && probe.InstructionCount <= close.InstructionCount)
            {
                yield return probe;
            }
        }
    }

    internal void Add(CombatTraceRecord record)
    {
        if (record.IsStage)
        {
            _stages.TryAdd(record.Id, record);
        }
        else
        {
            _probes.Add(record);
        }
    }

    /// <summary>A one-line description for test output.</summary>
    public override string ToString() =>
        $"frame step {Step} stages [{string.Join(",", _stages.Keys.Order())}] "
            + $"probes {_probes.Count}{(IsComplete ? "" : " INCOMPLETE")}";
}

/// <summary>
/// Reads a <c>cyac-combat-trace</c> file (format v1) — the per-frame, per-stage + per-call oracle a
/// <c>Sim/Combat</c> verification test compares the port against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from alone</b> (§0: "which must be implementable from this document alone; the port
/// never references the emulator").  One JSON header line, then variable-count records distinguished
/// by a leading <c>kind</c> byte whose length the header gives. A trailing partial record means the
/// producer was killed mid-write and is DISCARDED; an unknown <c>kind</c> means a newer producer and
/// the reader STOPS, because it cannot know the length.
/// </para>
/// <para>
/// A trace is a development artefact a checkout need not carry: tests locate one by walking up from
/// the test assembly and SKIP with a clear message when it is absent.
/// </para>
/// </remarks>
public sealed class CombatTraceReader : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly long _bodyStart;
    private readonly byte[] _buffer;

    private CombatTraceReader(Stream stream, bool ownsStream, CombatTraceHeader header, long bodyStart)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        Header = header;
        _bodyStart = bodyStart;
        _buffer = new byte[Math.Max(header.StageRecordLength, header.ProbeRecordLength)];
    }

    /// <summary>The trace's header — provenance and every layout number.</summary>
    public CombatTraceHeader Header { get; }

    /// <summary>True when the reader stopped at a record kind it did not understand.</summary>
    public bool StoppedAtUnknownKind { get; private set; }

    /// <summary>Bytes of a trailing partial record the reader discarded.</summary>
    public long TrailingPartialBytes { get; private set; }

    /// <summary>Opens a trace file.</summary>
    /// <param name="path">Path to the <c>.ctr</c> file.</param>
    /// <exception cref="InvalidDataException">The file is not a v1 <c>cyac-combat-trace</c>.</exception>
    public static CombatTraceReader Open(string path)
    {
        FileStream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        try
        {
            return Open(stream, ownsStream: true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens a trace over an already-positioned seekable stream.</summary>
    /// <param name="stream">The stream, positioned at the start of the file.</param>
    /// <param name="ownsStream">Whether disposing the reader disposes the stream.</param>
    /// <exception cref="InvalidDataException">The stream is not a v1 <c>cyac-combat-trace</c>.</exception>
    public static CombatTraceReader Open(Stream stream, bool ownsStream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("a combat trace must be read from a seekable stream", nameof(stream));
        }

        List<byte> line = new List<byte>(4096);
        while (true)
        {
            int next = stream.ReadByte();
            if (next < 0)
            {
                throw new InvalidDataException(
                    "the combat trace ended before its header line was terminated by a newline");
            }

            if (next == '\n')
            {
                break;
            }

            line.Add((byte)next);
        }

        CombatTraceHeader header = CombatTraceHeader.Parse(Encoding.UTF8.GetString([.. line]));
        return new CombatTraceReader(stream, ownsStream, header, stream.Position);
    }

    /// <summary>
    /// Every whole record, in file order.  The yielded record's buffer is REUSED — call
    /// <see cref="CombatTraceRecord.Copy"/> to keep one past the next iteration.
    /// </summary>
    public IEnumerable<CombatTraceRecord> Records()
    {
        _stream.Position = _bodyStart;
        StoppedAtUnknownKind = false;
        TrailingPartialBytes = 0;

        while (true)
        {
            int kind = _stream.ReadByte();
            if (kind < 0)
            {
                yield break;
            }

            int length = Header.RecordLength((byte)kind);
            if (length < 0)
            {
                // §1: an unknown kind means a newer producer.  A v1 reader cannot know the record's
                // length, so it must STOP and report rather than resynchronise on a guess.
                StoppedAtUnknownKind = true;
                yield break;
            }

            _buffer[0] = (byte)kind;
            int read = ReadExactly(_buffer.AsSpan(1, length - 1));
            if (read < length - 1)
            {
                TrailingPartialBytes = read + 1;
                yield break;                            // truncated tail: discard it
            }

            yield return new CombatTraceRecord(Header, _buffer, length);
        }
    }

    /// <summary>
    /// The trace's frames.  Each frame OWNS its records (they are copied), so a verifier can hold
    /// two stages of the same frame at once.
    /// </summary>
    /// <remarks>
    /// Frames are grouped by the CS0 record, never by <see cref="CombatTraceRecord.Step"/> — a
    /// watchdog idle step can bump the step in the middle of a frame body.  Records that precede
    /// the first CS0 are dropped: they belong to a frame the window cut in half.
    /// </remarks>
    public IEnumerable<CombatTraceFrame> Frames()
    {
        CombatTraceFrame? current = null;
        foreach (CombatTraceRecord record in Records())
        {
            if (record.IsStage && record.Id == 0)
            {
                if (current is not null)
                {
                    yield return current;
                }

                current = new CombatTraceFrame(record.Copy());
                continue;
            }

            current?.Add(record.Copy());
        }

        if (current is not null)
        {
            yield return current;
        }
    }

    /// <summary>Releases the underlying stream when this reader owns it.</summary>
    public void Dispose()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }

    private int ReadExactly(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = _stream.Read(destination[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
