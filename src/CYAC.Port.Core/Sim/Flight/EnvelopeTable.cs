using CYAC.Port.Core.Model.Flight;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// A byte-offset view of a <see cref="FlightEnvelope"/> — the 504-byte <c>.fme</c> table exactly as
/// the original addresses it, without ever materialising the bytes.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists.  The FME queries do not index points by number: they compute a <b>byte offset</b>
/// (<c>cbw</c> on a point index, <c>shl bx,1</c> twice, <c>add bx,si</c>) and then read a word there
/// — and several of those reads deliberately land <i>outside</i> the point array.  The low-speed and
/// altitude arms both interpolate through <c>[si-4]/[si-2]</c>, so when the bracket is
/// <c>points[0]</c> the "previous point" they read is the record's own 4-byte HEADER
/// (<c>ordinal|n_points</c> as one word at <c>si-4</c>, <c>peak_idx|high_speed_idx</c> at
/// <c>si-2</c>).  A points-indexed model cannot express that; a byte-offset model reproduces it for
/// free.
/// </para>
/// <para>
/// Layout (<see cref="EnvelopeCurve"/>): 14 records of <c>0x24</c> bytes (<c>add si,0x24</c>
/// @<c>image@0x2AB25</c>); each record is <c>{ i8 ordinal, u8 n_points, u8 peak_idx, u8
/// high_speed_idx }</c> then 8 points of <c>{ i16 x, i16 y }</c> (point stride 4, <c>add si,4</c>
/// @<c>image@0x2AB1D</c>).
/// </para>
/// <para>
/// Offsets are record-table offsets, i.e. what the original holds in <c>SI</c> minus the table's own
/// far-pointer offset.  Every read the shipped data produces lands in <c>[0, 504)</c>; an offset
/// outside that range means the original would have read whatever the far heap holds next, which no
/// shipped record does, so it throws rather than inventing a value.
/// </para>
/// </remarks>
internal readonly struct EnvelopeTable
{
    /// <summary>Bytes one <c>FlightModelEnvelopeRecord</c> occupies.</summary>
    public const int RecordStride = EnvelopeCurve.Bytes;   // 0x24

    /// <summary>Bytes in the whole table: 14 × <see cref="RecordStride"/>.</summary>
    public const int TableBytes = FlightEnvelope.FileBytes;   // 504

    /// <summary>Byte offset of the first point inside a record (<c>points[0].x</c>).</summary>
    public const int PointsOffset = 4;

    /// <summary>Bytes one point occupies.</summary>
    public const int PointStride = 4;

    private readonly FlightEnvelope _envelope;

    /// <summary>Wraps a parsed envelope.</summary>
    /// <param name="envelope">The 14 curves.</param>
    public EnvelopeTable(FlightEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        _envelope = envelope;
    }

    /// <summary>The wrapped envelope.</summary>
    public FlightEnvelope Envelope => _envelope;

    /// <summary>Byte offset of record <paramref name="index"/> in the table.</summary>
    /// <param name="index">The record's position in the file, 0..13.</param>
    public static int RecordOffset(int index) => index * RecordStride;

    /// <summary>Reads one byte of the table.</summary>
    /// <param name="offset">A table-relative byte offset.</param>
    /// <exception cref="ArgumentOutOfRangeException">The offset is outside the 504-byte table.</exception>
    public byte Byte(int offset)
    {
        if ((uint)offset >= TableBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                "the FME queries read outside the 504-byte envelope table; in the original that is a "
                    + "far-heap read past the asset, which no shipped record produces");
        }

        EnvelopeCurve curve = _envelope.Curves[offset / RecordStride];
        int inRecord = offset % RecordStride;
        if (inRecord < PointsOffset)
        {
            return inRecord switch
            {
                0 => unchecked((byte)curve.LoadFactorG),
                1 => curve.PointCount,
                2 => curve.PeakIndex,
                _ => curve.HighSpeedIndex,
            };
        }

        EnvelopePoint point = curve.Points[(inRecord - PointsOffset) / PointStride];
        return ((inRecord - PointsOffset) % PointStride) switch
        {
            0 => unchecked((byte)point.AirspeedFps),
            1 => unchecked((byte)((ushort)point.AirspeedFps >> 8)),
            2 => unchecked((byte)point.AltitudeEighths),
            _ => unchecked((byte)((ushort)point.AltitudeEighths >> 8)),
        };
    }

    /// <summary>
    /// Reads one little-endian 16-bit word of the table.  The original's word reads are not
    /// necessarily aligned to a point (see the type remarks), so this composes two bytes.
    /// </summary>
    /// <param name="offset">A table-relative byte offset.</param>
    public ushort Word(int offset) => (ushort)(Byte(offset) | (Byte(offset + 1) << 8));

    /// <summary>Reads one word of the table as a signed value.</summary>
    /// <param name="offset">A table-relative byte offset.</param>
    public short SignedWord(int offset) => unchecked((short)Word(offset));
}
