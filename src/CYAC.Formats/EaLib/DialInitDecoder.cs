using System.Buffers.Binary;

namespace CYAC.Formats.EaLib;

// ---------------------------------------------------------------------------
// dialinit.bin — PER-AIRCRAFT COCKPIT-INSTRUMENT LAYOUT (2a.lib idx 5, LZSS
// flag 0x01: 1,024 B stored -> 4,320 B decompressed).
//
//   4,320 B = 6 player aircraft x 10 instruments x 72 B
//
// Loader: cockpit_layout_load_per_aircraft @image@0x01DAA — loads the asset,
// takes the 720-B section for g_active_aircraft_idx [0xC31A] (`mov ax,0x2D0;
// imul word [0xC31A]` @image@0x01DC3), and REP MOVSW's the ten 72-B records into
// the ten DGROUP dial slots, in record order:
//
//   rec 0 -> [0xEC8E] altimeter        rec 5 -> [0xEADE] brake
//   rec 1 -> [0xECD6] vsi              rec 6 -> [0xEB26] fuel
//   rec 2 -> [0xEC46] airspeed         rec 7 -> [0xEB6E] pct_meter_a
//   rec 3 -> [0xEA4E] compass          rec 8 -> [0xEBB6] pct_meter_b
//   rec 4 -> [0xEA96] bearing_pointer  rec 9 -> [0xEBFE] pct_meter_c
//
//
// Only part of it is AUTHORED: the rect, the pivot, the kind word, the three
// params, the keyframe count, the amplitude, the sweep step and the two flag
// bytes. Everything else (keyframe endpoints, current/previous needle rects, the
// embedded 10-byte backing-buffer descriptor, the draw-callback far pointer, the
// time accumulator and the dirty flag) is RUN-TIME state that
// dial_slot_init_active_state @image@0x018E6 fills in — and is zero in all 60
// shipped records, which this codec verifies rather than assumes.
// ---------------------------------------------------------------------------

/// <summary>
/// One cockpit instrument's authored layout plus whatever run-time state the file carries
/// (KNOWN_FIELDS["s_dial_instrument_record"]</c>, 72 bytes).
/// </summary>
public sealed class DialInstrumentRecord
{
    /// <summary>Bytes per record.</summary>
    public const int Size = 72;

    /// <summary><c>+0x00</c> — bounding-rect x, screen pixels.</summary>
    public short RectX { get; set; }

    /// <summary><c>+0x02</c> — bounding-rect y.</summary>
    public short RectY { get; set; }

    /// <summary><c>+0x04</c> — bounding-rect width.</summary>
    public short RectWidth { get; set; }

    /// <summary><c>+0x06</c> — bounding-rect height.</summary>
    public short RectHeight { get; set; }

    /// <summary><c>+0x08</c> — needle/text pivot x.</summary>
    public short PivotX { get; set; }

    /// <summary><c>+0x0A</c> — needle/text pivot y.</summary>
    public short PivotY { get; set; }

    /// <summary>
    /// <c>+0x0C</c> — instrument-style selector: 0x0C0C analog dial, 0x0404 text indicator,
    /// 0x0909 compass, 0x0F0F MiG-15 radar variant, 0x0000 empty slot.  (full nibble
    /// decoding) is still open.
    /// </summary>
    public ushort KindWord { get; set; }

    /// <summary><c>+0x0E</c> — type-specific parameter 0.</summary>
    public ushort Param0 { get; set; }

    /// <summary><c>+0x10</c> — type-specific parameter 1 (e.g. 0x03E8 = 1000).</summary>
    public ushort Param1 { get; set; }

    /// <summary><c>+0x12</c> — needle angle offset added after the value→angle map (P522).</summary>
    public ushort Param2 { get; set; }

    /// <summary><c>+0x14</c> — number of keyframe entries in the <c>+0x16</c> array (usually 1).</summary>
    public short KeyframeCount { get; set; }

    /// <summary><c>+0x1E</c> — needle-sweep amplitude / value range (P201).</summary>
    public short Amplitude { get; set; }

    /// <summary><c>+0x3E</c> — per-tick time-accumulator advance = the needle sweep rate (P201).</summary>
    public short ScrollStep { get; set; }

    /// <summary><c>+0x45</c> — 1 = the aircraft has this instrument, 0 = empty slot (P26).</summary>
    public bool Present { get; set; }

    /// <summary><c>+0x46</c> — needle-direction invert flag (P201).</summary>
    public byte DirectionInvert { get; set; }

    /// <summary>
    /// The run-time half of the record (<c>+0x16..+0x1D</c>, <c>+0x20..+0x3D</c>,
    /// <c>+0x40..+0x44</c> and the <c>+0x47</c> alignment byte), or <see langword="null"/> when it is
    /// all zero — which it is in every shipped record.  Carried verbatim so an edited or foreign file
    /// still round-trips.
    /// </summary>
    public byte[]? RuntimeState { get; set; }

    /// <summary>True when the slot carries no instrument (kind word 0, empty rect).</summary>
    public bool IsEmpty => !Present && KindWord == 0 && RectWidth == 0 && RectHeight == 0;
}

/// <summary>
/// One player aircraft's cockpit: its ten instrument records in dial-slot order.
/// </summary>
/// <param name="AircraftIndex">The player-aircraft index 0..5 (<c>g_active_aircraft_idx [0xC31A]</c>).</param>
/// <param name="Instruments">The ten records, index i = dial slot i.</param>
public sealed record DialCockpitLayout(int AircraftIndex, IReadOnlyList<DialInstrumentRecord> Instruments);

/// <summary>
/// Reads and re-emits <c>dialinit.bin</c> (see the file header for the layout and its citations).
/// </summary>
public static class DialInitDecoder
{
    /// <summary>Player aircraft with a cockpit section: 6.</summary>
    public const int AircraftCount = 6;

    /// <summary>Instrument records per aircraft: 10.</summary>
    public const int InstrumentCount = 10;

    /// <summary>Bytes per aircraft section: 720 (<c>mov ax,0x2D0</c> @image@0x01DC3).</summary>
    public const int SectionSize = InstrumentCount * DialInstrumentRecord.Size;

    /// <summary>The whole asset: 4,320 bytes.</summary>
    public const int ExpectedSize = AircraftCount * SectionSize;

    /// <summary>The asset's name in <c>2a.lib</c> (DGROUP literal at DGROUP+0xDD6, image@0x01DB2).</summary>
    public const string AssetName = "dialinit.bin";

    /// <summary>
    /// Index i names the DGROUP slot record i is copied into.
    /// </summary>
    public static readonly string[] SlotNames =
    [
        "altimeter", "vsi", "airspeed", "compass", "bearing_pointer",
        "brake", "fuel", "pct_meter_a", "pct_meter_b", "pct_meter_c",
    ];

    /// <summary>The DGROUP address of each dial slot, in record order (image@0x01DCD..).</summary>
    public static readonly int[] SlotDgroupAddresses =
    [
        0xEC8E, 0xECD6, 0xEC46, 0xEA4E, 0xEA96, 0xEADE, 0xEB26, 0xEB6E, 0xEBB6, 0xEBFE,
    ];

    /// <summary>Whether an asset name is the cockpit-layout table.</summary>
    /// <param name="name">An EALIB member name.</param>
    public static bool IsDialInit(string name) =>
        string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a decompressed <c>dialinit.bin</c> body.</summary>
    /// <param name="body">The decompressed asset body; must be 4,320 bytes.</param>
    /// <exception cref="InvalidDataException">The body is not <c>6 × 10 × 72</c> bytes.</exception>
    public static IReadOnlyList<DialCockpitLayout> Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length != ExpectedSize)
        {
            throw new InvalidDataException(
                $"dialinit.bin is {body.Length} B; the loader reads {AircraftCount} sections of " +
                $"{SectionSize} B ({ExpectedSize} B total, image@0x01DC3)");
        }

        List<DialCockpitLayout> layouts = new List<DialCockpitLayout>(AircraftCount);
        for (int aircraft = 0; aircraft < AircraftCount; aircraft++)
        {
            List<DialInstrumentRecord> records = new List<DialInstrumentRecord>(InstrumentCount);
            for (int slot = 0; slot < InstrumentCount; slot++)
            {
                records.Add(ReadRecord(
                    body.Slice((aircraft * SectionSize) + (slot * DialInstrumentRecord.Size),
                               DialInstrumentRecord.Size)));
            }

            layouts.Add(new DialCockpitLayout(aircraft, records));
        }

        return layouts;
    }

    /// <summary>Rebuilds the decompressed body from the model.</summary>
    /// <param name="layouts">The six cockpit layouts.</param>
    /// <exception cref="InvalidDataException">The model does not hold 6 × 10 records.</exception>
    public static byte[] ToBytes(IReadOnlyList<DialCockpitLayout> layouts)
    {
        ArgumentNullException.ThrowIfNull(layouts);
        if (layouts.Count != AircraftCount)
        {
            throw new InvalidDataException(
                $"dialinit.bin holds {AircraftCount} aircraft sections, not {layouts.Count}");
        }

        byte[] body = new byte[ExpectedSize];
        for (int aircraft = 0; aircraft < AircraftCount; aircraft++)
        {
            IReadOnlyList<DialInstrumentRecord> records = layouts[aircraft].Instruments;
            if (records.Count != InstrumentCount)
            {
                throw new InvalidDataException(
                    $"aircraft {aircraft} has {records.Count} instrument records, not {InstrumentCount}");
            }

            for (int slot = 0; slot < InstrumentCount; slot++)
            {
                WriteRecord(
                    body.AsSpan((aircraft * SectionSize) + (slot * DialInstrumentRecord.Size),
                                DialInstrumentRecord.Size),
                    records[slot]);
            }
        }

        return body;
    }

    /// <summary>
    /// The run-time byte ranges of a record, in order: the bytes this codec carries verbatim
    /// because the engine, not the author, writes them.
    /// </summary>
    internal static readonly (int Offset, int Length)[] RuntimeRanges =
    [
        (0x16, 0x08),   // keyframe endpoint array (P522)
        (0x20, 0x1E),   // current/previous needle rects, backing descriptor, draw callback
        (0x40, 0x05),   // time accumulator (lo/hi) + dirty flag
        (0x47, 0x01),   // alignment byte after the three u8 flags
    ];

    private static short I16(ReadOnlySpan<byte> record, int o) =>
        BinaryPrimitives.ReadInt16LittleEndian(record.Slice(o, 2));

    private static ushort U16(ReadOnlySpan<byte> record, int o) =>
        BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(o, 2));

    private static void W16(Span<byte> record, int o, int v) =>
        BinaryPrimitives.WriteInt16LittleEndian(record.Slice(o, 2), (short)v);

    private static DialInstrumentRecord ReadRecord(ReadOnlySpan<byte> record)
    {
        List<byte> runtime = new List<byte>(0x30);
        bool anyRuntime = false;
        foreach ((int offset, int length) in RuntimeRanges)
        {
            ReadOnlySpan<byte> span = record.Slice(offset, length);
            foreach (byte b in span)
            {
                if (b != 0)
                {
                    anyRuntime = true;
                }
            }

            runtime.AddRange(span);
        }

        return new DialInstrumentRecord
        {
            RectX = I16(record, 0x00),
            RectY = I16(record, 0x02),
            RectWidth = I16(record, 0x04),
            RectHeight = I16(record, 0x06),
            PivotX = I16(record, 0x08),
            PivotY = I16(record, 0x0A),
            KindWord = U16(record, 0x0C),
            Param0 = U16(record, 0x0E),
            Param1 = U16(record, 0x10),
            Param2 = U16(record, 0x12),
            KeyframeCount = I16(record, 0x14),
            Amplitude = I16(record, 0x1E),
            ScrollStep = I16(record, 0x3E),
            Present = record[0x45] != 0,
            DirectionInvert = record[0x46],
            RuntimeState = anyRuntime ? [.. runtime] : null,
        };
    }

    private static void WriteRecord(Span<byte> record, DialInstrumentRecord value)
    {
        W16(record, 0x00, value.RectX);
        W16(record, 0x02, value.RectY);
        W16(record, 0x04, value.RectWidth);
        W16(record, 0x06, value.RectHeight);
        W16(record, 0x08, value.PivotX);
        W16(record, 0x0A, value.PivotY);
        BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(0x0C, 2), value.KindWord);
        BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(0x0E, 2), value.Param0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(0x10, 2), value.Param1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(0x12, 2), value.Param2);
        W16(record, 0x14, value.KeyframeCount);
        W16(record, 0x1E, value.Amplitude);
        W16(record, 0x3E, value.ScrollStep);
        record[0x44] = 0;                        // dirty flag — run-time, and zero in every shipped record
        record[0x45] = (byte)(value.Present ? 1 : 0);
        record[0x46] = value.DirectionInvert;

        if (value.RuntimeState is not { } runtime)
        {
            return;
        }

        int expected = RuntimeRanges.Sum(r => r.Length);
        if (runtime.Length != expected)
        {
            throw new InvalidDataException(
                $"an instrument's run-time state is {runtime.Length} B; the record reserves {expected} B");
        }

        int at = 0;
        foreach ((int offset, int length) in RuntimeRanges)
        {
            runtime.AsSpan(at, length).CopyTo(record.Slice(offset, length));
            at += length;
        }
    }
}
