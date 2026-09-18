using System.Text;

namespace CYAC.Port.Core.Sim.Flight.Trace;

/// <summary>
/// Reads a <c>cyac-flight-trace</c> file (format v1) — the per-step, per-stage oracle a
/// <c>Sim/Flight</c> verification test compares the port against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from alone.</b> That document is explicit that its consumers "must be implementable
/// from this document alone; the port never references the emulator that writes them, and
/// this class is the port side of that contract: one JSON header line, then fixed-size records of
/// <c>recordLen</c> bytes back to back, every offset taken from the header.  A trailing partial
/// record means the producer was killed mid-write and is discarded.
/// </para>
/// <para>
/// A trace is a development artefact, not shipped data: it lives under and a checkout need not carry
/// it.  Tests locate one by walking up from the test assembly and SKIP with a clear message when it
/// is absent.
/// </para>
/// <para>
/// <b>Allocation.</b>  <see cref="Records"/> reuses one buffer, so a 125 MB trace streams without
/// pressure; <see cref="FlightTraceRecord.Copy"/> is how a caller retains one.
/// </para>
/// </remarks>
public sealed class FlightTraceReader : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly long _bodyStart;
    private readonly byte[] _buffer;

    private FlightTraceReader(Stream stream, bool ownsStream, FlightTraceHeader header, long bodyStart)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        Header = header;
        _bodyStart = bodyStart;
        _buffer = new byte[header.RecordLength];
    }

    /// <summary>The trace's header — provenance and every layout number.</summary>
    public FlightTraceHeader Header { get; }

    /// <summary>How many whole records the file holds.</summary>
    public long RecordCount => (_stream.Length - _bodyStart) / Header.RecordLength;

    /// <summary>Bytes of a trailing partial record, if the producer was killed mid-write.</summary>
    public long TrailingPartialBytes => (_stream.Length - _bodyStart) % Header.RecordLength;

    /// <summary>Opens a trace file.</summary>
    /// <param name="path">Path to the <c>.ftr</c> file.</param>
    /// <exception cref="InvalidDataException">The file is not a v1 <c>cyac-flight-trace</c>.</exception>
    public static FlightTraceReader Open(string path)
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
    /// <exception cref="InvalidDataException">The stream is not a v1 <c>cyac-flight-trace</c>.</exception>
    public static FlightTraceReader Open(Stream stream, bool ownsStream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("a flight trace must be read from a seekable stream", nameof(stream));
        }

        List<byte> line = new List<byte>(1024);
        while (true)
        {
            int next = stream.ReadByte();
            if (next < 0)
            {
                throw new InvalidDataException(
                    "the flight trace ended before its header line was terminated by a newline");
            }

            if (next == '\n')
            {
                break;
            }

            line.Add((byte)next);
        }

        FlightTraceHeader header = FlightTraceHeader.Parse(Encoding.UTF8.GetString([.. line]));
        return new FlightTraceReader(stream, ownsStream, header, stream.Position);
    }

    /// <summary>
    /// Every whole record, in file order.  The yielded record's buffer is REUSED — call
    /// <see cref="FlightTraceRecord.Copy"/> to keep one past the next iteration.
    /// </summary>
    public IEnumerable<FlightTraceRecord> Records()
    {
        _stream.Position = _bodyStart;
        while (true)
        {
            int read = ReadExactly(_buffer);
            if (read < _buffer.Length)
            {
                yield break;                    // a trailing partial record: discard it
            }

            yield return new FlightTraceRecord(Header, _buffer);
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

    private int ReadExactly(byte[] destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = _stream.Read(destination, total, destination.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
