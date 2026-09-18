using System.Buffers.Binary;
using System.Text;

namespace CYAC.Formats.EaLib;

// ---------------------------------------------------------------------------
// pi.bin — the AIRCRAFT ENCYCLOPEDIA (hangar "plane info" pages) + the
// tactical MATCHUP-HINT table.  2a.lib::pi.bin, LZSS-compressed (encoding flag
// 0x01): 3,908 B raw -> 6,036 B body.
//
// Format:
//
// The plane header is 0x34 B of MIXED types
// (u8 + 6 near-ptr + u8 + ptr + 5 u16 + ptr + 9 u16), and the hint's two
// leading bytes are an (ally_class, enemy_class) MATCHUP KEY in the
// aircraft-class id space.
//
// BODY LAYOUT (6,036 B).  The runtime keeps the whole body as ONE far
// allocation reached through g_pi_bin_far_ptr_off/seg [0xBC34]/[0xBC36]
// (`les bx,[0xbc34]` image@0x273D3 / image@0x2662A / image@0x27151), so every
// pointer inside a record is an ABSOLUTE near-offset into the body:
//
//   +0x0000  u16  plane_count = 14      `cmp word es:[bx], si` image@0x273D7
//   +0x0002  u16  plane_off[14]         `mov cx, es:[bx+si]` image@0x2662E
//   +0x001E  12 B zero pad
//   +0x002A  u16  hint_count  = 15      `mov ax, es:[bx+0x2a]` image@0x2730E
//   +0x002C  u16  hint_off[15]          `add ax, 0x2c` image@0x27301
//   +0x004A  10 B zero pad
//   +0x0054       14 plane records      (0x34-B header + string blob)
//   +0x1009       15 hint records       ({u8 ally, u8 enemy} + ASCII text)
//
// *** THERE IS NO STRIDE. ***  Records are variable-length AND the directory
// is NOT monotonic (plane_off[7] = 0x0A8C sits after plane_off[9] = 0x0955 in
// the file).  Never index by multiplication — always go through the directory,
// and bound a record by the smallest larger offset drawn from BOTH directories
// (that is what SpanEnd() below does; the old decoder's dir2Start bound only
// happened to work).
//
// The 14 encyclopedia aircraft are 14 of the 19 aircraft classes; the five
// missing ones are exactly the non-fighters (6 B-17E, 7 B-29, 8 B-52D, 13 L-5,
// 23 Truck) — they have stat blocks and meshes but no encyclopedia page.
// ---------------------------------------------------------------------------
public static class PiBinDecoder
{
    public const int ExpectedBodySize = 6036;
    public const int ExpectedPlaneCount = 14;   // image@0x273D7
    public const int ExpectedHintCount = 15;    // image@0x2730E

    public const int OffPlaneCount = 0x00;
    public const int OffPlaneDir = 0x02;
    public const int OffHintCount = 0x2A;       // image@0x2730E
    public const int OffHintDir = 0x2C;         // image@0x27301

    /// <summary>Plane-record header size; the string blob starts here.</summary>
    public const int PlaneHeaderLen = 0x34;

    // ---- plane-record field offsets (citation per line) -------------
    public const int PfClassId = 0x00;        // image@0x273E5 -> class table image@0x24058
    public const int PfNameFullPtr = 0x01;    // image@0x26758 (hangar title)
    public const int PfNameShortPtr = 0x03;   // image@0x26EA5 (tactics column header)
    public const int PfArm1Ptr = 0x05;        // image@0x2678F
    public const int PfArm2Ptr = 0x07;        // image@0x267A6 — row SKIPPED when 0
    public const int PfArm3Ptr = 0x09;        // image@0x267C7 — row SKIPPED when 0
    public const int PfArmSumPtr = 0x0B;      // image@0x26EDE "ARM:%24s"
    public const int PfArmRating = 0x0D;      // image@0x26F20 (compared stat #1)
    public const int PfEnginePtr = 0x0E;      // image@0x26803 "POWER:"
    public const int PfWeightLb = 0x10;       // image@0x2688E "WEIGHT"
    public const int PfMaxSpeedMph = 0x12;    // image@0x26832 / image@0x26F33
    public const int PfMaxAltFt = 0x14;       // image@0x26860 / image@0x26F74
    public const int PfThrustWeightQ8 = 0x16; // image@0x26FB5 — u8.8
    public const int PfWingLoadingPsf = 0x18; // image@0x27026
    public const int PfDescriptionPtr = 0x1A; // image@0x268BC (4-line blurb)
    public const int PfHangarPosX = 0x1C;     // image@0x26655 (<<8 -> i32)
    public const int PfHangarPosY = 0x1E;     // image@0x2666A
    public const int PfHangarPosZ = 0x20;     // image@0x2667F (camera distance)
    public const int PfSilhouetteYOff = 0x22; // image@0x26A87
    public const int PfLengthFt = 0x24;       // image@0x26B54  "%d'%d\""
    public const int PfLengthIn = 0x26;       // image@0x26B50
    public const int PfHeightFt = 0x28;       // image@0x26AEE
    public const int PfHeightIn = 0x2A;       // image@0x26AEA
    public const int PfDimLenX1 = 0x2C;       // image@0x26B23 (left tick x)
    public const int PfDimLenX2 = 0x2E;       // image@0x26B27 (right tick x)
    public const int PfDimHgtY1 = 0x30;       // image@0x26AAE (top tick y)
    public const int PfDimHgtY2 = 0x32;       // image@0x26AB6 (bottom tick y)

    // ---- hint-record field offsets --------------------------------
    public const int HfAllyClass = 0x00;      // `cmp byte es:[bx], al`    image@0x27337
    public const int HfEnemyClass = 0x01;     // `cmp byte es:[si+1], al`  image@0x2733F
    public const int HfText = 0x02;           // `lea dx,[si+2]`           image@0x27351

    // ---- semantic tables ---------------------------------------------------

    // The decoder keeps ids.  The class ids are the scenario.bin +0x04 id space (the class table
    // image@0x34F90, whose engagement prototypes name themselves at stat-block +0x04); P6 §3
    // confirmed all 15 matchup hints decode to sensible pairs.

    /// <summary>
    /// class id -&gt; player-aircraft slot 0..5 for the six FLYABLE types. Two
    /// independent derivations agree: the 6-entry table u16
    /// flyable_statblock_ptr[6] at DGROUP+0x0FBC (= image@0x3CD1C, read at
    /// image@0x247A4) holds exactly these classes' stat-block pointers, and the
    /// 6 live arms of the CS jump table in is_aircraft_flyable_get_player_idx
    /// image@0x27404 are its inverse. Slot order is the flyable table's
    /// (scenario.bin's aircraft_idx_u8).
    /// </summary>
    public static readonly IReadOnlyDictionary<int, int> FlyableSlotByClass =
        new Dictionary<int, int>
        {
            [22] = 0,  // P-51D    statblock 0x1734
            [12] = 1,  // FW-190A  statblock 0x1812
            [10] = 2,  // F-86E    statblock 0x1D1E
            [18] = 3,  // MiG-15   statblock 0x1F60
            [9] = 4,   // F-4E     statblock 0x2104
            [20] = 5,  // MiG-21MF statblock 0x2470
        };

    /// <summary>
    /// class id -&gt; national-marking frame index 0..3, i.e. the aircraft stat
    /// block's +0x2C byte (`mov cl,[0xef4e]` image@0x2452C -&gt;
    /// panel_sprite_blit).  Values byte-read from the 19 stat blocks by P1 §2.3
    /// / P6 §6; the frame indexes the same 4-frame 32x11 "insig" atlas the
    /// Scenarios tab shows (scenario.bin's insignia_idx_u8).
    /// This is NOT a pi.bin field — pi.bin +0x2C is the length-callout left
    /// tick (P6 defect D2) — it is a join through the class id.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, int> InsigniaIdxByClass =
        new Dictionary<int, int>
        {
            [6] = 0, [7] = 0, [8] = 0, [9] = 0, [10] = 0, [11] = 0,
            [13] = 0, [21] = 0, [22] = 0, [23] = 0,     // USAAF / USAF
            [12] = 1, [14] = 1, [15] = 1, [16] = 1, [17] = 1,  // Luftwaffe
            [18] = 2, [24] = 2,                          // Soviet / N.Korean
            [19] = 3, [20] = 3,                          // N.Vietnamese
        };

    // ---- model -------------------------------------------------------------

    /// <summary>One encyclopedia aircraft: the 0x34-B header + its string blob.</summary>
    public sealed class PlaneRecord
    {
        public required int Index { get; init; }
        public required int Offset { get; init; }
        public required int Length { get; init; }

        public required byte AircraftClassId { get; init; }
        public required ushort NameFullPtr { get; init; }
        public required ushort NameShortPtr { get; init; }
        public required ushort Armament1Ptr { get; init; }
        public required ushort Armament2Ptr { get; init; }
        public required ushort Armament3Ptr { get; init; }
        public required ushort ArmamentSumPtr { get; init; }
        public required byte ArmamentRating { get; init; }
        public required ushort EngineNamePtr { get; init; }
        public required ushort WeightLb { get; init; }
        public required ushort MaxSpeedMph { get; init; }
        public required ushort MaxAltFt { get; init; }
        public required ushort ThrustWeightQ8 { get; init; }
        public required ushort WingLoadingPsf { get; init; }
        public required ushort DescriptionPtr { get; init; }
        public required short HangarPosX { get; init; }
        public required short HangarPosY { get; init; }
        public required short HangarPosZ { get; init; }
        public required short SilhouetteYOff { get; init; }
        public required ushort LengthFt { get; init; }
        public required ushort LengthIn { get; init; }
        public required ushort HeightFt { get; init; }
        public required ushort HeightIn { get; init; }
        public required ushort DimLenX1 { get; init; }
        public required ushort DimLenX2 { get; init; }
        public required ushort DimHgtY1 { get; init; }
        public required ushort DimHgtY2 { get; init; }

        /// <summary>Record bytes from +0x34 to the record end (the string pool).</summary>
        public required byte[] Blob { get; init; }

        // Strings resolved through the body-absolute near pointers above.
        public required string NameFull { get; init; }
        public required string NameShort { get; init; }
        /// <summary>ARMAMENT lines 1..3; an entry is "" when its pointer is 0.</summary>
        public required string[] Armament { get; init; }
        public required string ArmamentSummary { get; init; }
        public required string EngineName { get; init; }
        public required string Description { get; init; }

        /// <summary>Flyable player slot 0..5, or -1 for the 8 AI-only types.</summary>
        public int FlyableSlot =>
            FlyableSlotByClass.TryGetValue(AircraftClassId, out int s) ? s : -1;
        public bool IsFlyable => FlyableSlot >= 0;

        /// <summary>National-marking frame index 0..3 (stat block +0x2C), or -1.</summary>
        public int InsigniaIdx =>
            InsigniaIdxByClass.TryGetValue(AircraftClassId, out int i) ? i : -1;

        public double ThrustWeight => ThrustWeightQ8 / 256.0;
        /// <summary>THRUST/WEIGHT exactly as the game prints it (see <see cref="FormatQ8"/>).</summary>
        public string ThrustWeightText => FormatQ8(ThrustWeightQ8);

        /// <summary>Length as the game prints it: fmt "%d'%d\"" (DGROUP+0x3966).</summary>
        public string LengthText => $"{LengthFt}'{LengthIn}\"";
        public string HeightText => $"{HeightFt}'{HeightIn}\"";

        /// <summary>The 3 ARMAMENT rows the engine actually draws (0-ptr rows skipped).</summary>
        public IEnumerable<string> ArmamentLinesShown
        {
            get
            {
                yield return Armament[0];                                // image@0x2678F
                if (Armament2Ptr != 0) yield return Armament[1];         // image@0x267A6
                if (Armament3Ptr != 0) yield return Armament[2];         // image@0x267C7
            }
        }
    }

    /// <summary>
    /// One tactical matchup hint: {u8 ally_class, u8 enemy_class, char text[]}.
    /// Picked by the linear search at image@0x272DE; a miss falls through to
    /// the generic axis advice at image@0x2745D.
    /// </summary>
    public sealed class HintRecord
    {
        public required int Index { get; init; }
        public required int Offset { get; init; }
        public required int Length { get; init; }
        public required byte AllyClassId { get; init; }
        public required byte EnemyClassId { get; init; }
        public required string Text { get; init; }
        /// <summary>Bytes from +0x02 to the record end (text + any pad).</summary>
        public required byte[] Blob { get; init; }
    }

    public sealed class PiBin
    {
        public required byte[] Body { get; init; }
        public required int RawCompressedLength { get; init; }
        public required IReadOnlyList<PlaneRecord> Planes { get; init; }
        public required IReadOnlyList<HintRecord> Hints { get; init; }
        public required ushort[] PlaneOffsets { get; init; }
        public required ushort[] HintOffsets { get; init; }
        /// <summary>Pad between the plane directory and +0x2A (12 B of zeros).</summary>
        public required byte[] Gap1 { get; init; }
        /// <summary>Pad between the hint directory and the first record (10 B of zeros).</summary>
        public required byte[] Gap2 { get; init; }
        /// <summary>Bytes after the last record's span end (empty in the shipped file).</summary>
        public required byte[] Trailer { get; init; }

        /// <summary>True when the plane directory is not sorted (it is not — P6 §1).</summary>
        public bool DirectoryIsMonotonic
        {
            get
            {
                for (int i = 1; i < PlaneOffsets.Length; i++)
                    if (PlaneOffsets[i] < PlaneOffsets[i - 1]) return false;
                return true;
            }
        }
    }

    // ---- decode -------------------------------------------------------------

    public static PiBin DecodeFromEntry(EaLibEntry entry)
    {
        if (entry.Encoding != EncodingFlag.Compressed)
            throw new InvalidDataException(
                $"{entry.Archive.ShortName}::{entry.Name} is not LZSS-compressed " +
                $"(encoding={entry.Encoding}); pi.bin must be flag=0x01");
        return DecodeFromDecompressed(entry.GetDecoded(), entry.Length);
    }

    /// <summary>Decode a round-10a-wrapped asset (u32 declared size + LZSS stream).</summary>
    public static PiBin Decode(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 4)
            throw new InvalidDataException(
                $"pi.bin too short ({raw.Length} bytes; need >= 4 for the size header)");
        int declared = BinaryPrimitives.ReadInt32LittleEndian(raw[..4]);
        if (declared <= 0 || declared > 0x10000)
            throw new InvalidDataException(
                $"pi.bin declared size = {declared} (expected {ExpectedBodySize})");
        return DecodeFromDecompressed(Lzss.Decompress(raw[4..], declared), raw.Length);
    }

    public static PiBin DecodeFromDecompressed(byte[] body, int rawCompressedLength = 0)
    {
        ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(o, 2));
        short I16(int o) => BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(o, 2));

        int planeCount = U16(OffPlaneCount);
        if (planeCount < 1 || OffPlaneDir + planeCount * 2 > body.Length)
            throw new InvalidDataException($"pi.bin plane_count invalid: {planeCount}");
        ushort[] planeOff = new ushort[planeCount];
        for (int i = 0; i < planeCount; i++) planeOff[i] = U16(OffPlaneDir + i * 2);

        int hintCount = U16(OffHintCount);
        if (hintCount < 1 || OffHintDir + hintCount * 2 > body.Length)
            throw new InvalidDataException($"pi.bin hint_count invalid: {hintCount}");
        ushort[] hintOff = new ushort[hintCount];
        for (int i = 0; i < hintCount; i++) hintOff[i] = U16(OffHintDir + i * 2);

        // Record bounds: the smallest offset strictly greater than this one,
        // drawn from BOTH directories (they are one variable-length arena and
        // the plane directory is non-monotonic).
        int[] bounds = planeOff.Select(x => (int)x)
                             .Concat(hintOff.Select(x => (int)x))
                             .Append(body.Length)
                             .Distinct().OrderBy(x => x).ToArray();
        int SpanEnd(int off) => bounds.First(x => x > off);

        int firstRecord = planeOff.Min();
        byte[] gap1 = body.AsSpan(OffPlaneDir + planeCount * 2,
                               OffHintCount - (OffPlaneDir + planeCount * 2)).ToArray();
        byte[] gap2 = body.AsSpan(OffHintDir + hintCount * 2,
                               firstRecord - (OffHintDir + hintCount * 2)).ToArray();

        string Str(int ptr)
        {
            if (ptr == 0 || ptr >= body.Length) return "";
            int end = ptr;
            while (end < body.Length && body[end] != 0) end++;
            return Encoding.Latin1.GetString(body, ptr, end - ptr);
        }

        List<PlaneRecord> planes = new List<PlaneRecord>(planeCount);
        for (int i = 0; i < planeCount; i++)
        {
            int o = planeOff[i];
            int end = SpanEnd(o);
            if (end - o < PlaneHeaderLen)
                throw new InvalidDataException(
                    $"pi.bin plane {i} at 0x{o:X4}: span {end - o} B < 0x34-B header");
            ushort a1 = U16(o + PfArm1Ptr), a2 = U16(o + PfArm2Ptr), a3 = U16(o + PfArm3Ptr);
            planes.Add(new PlaneRecord
            {
                Index = i,
                Offset = o,
                Length = end - o,
                AircraftClassId = body[o + PfClassId],
                NameFullPtr = U16(o + PfNameFullPtr),
                NameShortPtr = U16(o + PfNameShortPtr),
                Armament1Ptr = a1,
                Armament2Ptr = a2,
                Armament3Ptr = a3,
                ArmamentSumPtr = U16(o + PfArmSumPtr),
                ArmamentRating = body[o + PfArmRating],
                EngineNamePtr = U16(o + PfEnginePtr),
                WeightLb = U16(o + PfWeightLb),
                MaxSpeedMph = U16(o + PfMaxSpeedMph),
                MaxAltFt = U16(o + PfMaxAltFt),
                ThrustWeightQ8 = U16(o + PfThrustWeightQ8),
                WingLoadingPsf = U16(o + PfWingLoadingPsf),
                DescriptionPtr = U16(o + PfDescriptionPtr),
                HangarPosX = I16(o + PfHangarPosX),
                HangarPosY = I16(o + PfHangarPosY),
                HangarPosZ = I16(o + PfHangarPosZ),
                SilhouetteYOff = I16(o + PfSilhouetteYOff),
                LengthFt = U16(o + PfLengthFt),
                LengthIn = U16(o + PfLengthIn),
                HeightFt = U16(o + PfHeightFt),
                HeightIn = U16(o + PfHeightIn),
                DimLenX1 = U16(o + PfDimLenX1),
                DimLenX2 = U16(o + PfDimLenX2),
                DimHgtY1 = U16(o + PfDimHgtY1),
                DimHgtY2 = U16(o + PfDimHgtY2),
                Blob = body.AsSpan(o + PlaneHeaderLen, end - o - PlaneHeaderLen).ToArray(),
                NameFull = Str(U16(o + PfNameFullPtr)),
                NameShort = Str(U16(o + PfNameShortPtr)),
                Armament = new[] { Str(a1), Str(a2), Str(a3) },
                ArmamentSummary = Str(U16(o + PfArmSumPtr)),
                EngineName = Str(U16(o + PfEnginePtr)),
                Description = Str(U16(o + PfDescriptionPtr)),
            });
        }

        List<HintRecord> hints = new List<HintRecord>(hintCount);
        for (int i = 0; i < hintCount; i++)
        {
            int o = hintOff[i];
            int end = SpanEnd(o);
            if (end - o < 3)
                throw new InvalidDataException(
                    $"pi.bin hint {i} at 0x{o:X4}: span {end - o} B too small");
            hints.Add(new HintRecord
            {
                Index = i,
                Offset = o,
                Length = end - o,
                AllyClassId = body[o + HfAllyClass],
                EnemyClassId = body[o + HfEnemyClass],
                Text = Str(o + HfText),
                Blob = body.AsSpan(o + HfText, end - o - HfText).ToArray(),
            });
        }

        int lastEnd = SpanEnd(hintOff.Max());
        return new PiBin
        {
            Body = body,
            RawCompressedLength = rawCompressedLength,
            Planes = planes,
            Hints = hints,
            PlaneOffsets = planeOff,
            HintOffsets = hintOff,
            Gap1 = gap1,
            Gap2 = gap2,
            Trailer = body.AsSpan(lastEnd).ToArray(),
        };
    }

    // ---- byte-exact re-emitter ---------------------------------------------

    /// <summary>
    /// Rebuild the 6,036-B body from the decoded model.  The round-trip proof:
    /// every header field is written
    /// back from its typed slot, the two zero pads and each record's string
    /// blob are carried verbatim, so a byte diff against the LZSS output
    /// proves the field map covers the whole file.
    /// </summary>
    public static byte[] ToBytes(PiBin pi)
    {
        byte[] body = pi.Body;
        byte[] outBuf = new byte[body.Length];
        void W16(int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(outBuf.AsSpan(o, 2), (ushort)v);

        W16(OffPlaneCount, pi.PlaneOffsets.Length);
        for (int i = 0; i < pi.PlaneOffsets.Length; i++) W16(OffPlaneDir + i * 2, pi.PlaneOffsets[i]);
        pi.Gap1.CopyTo(outBuf, OffPlaneDir + pi.PlaneOffsets.Length * 2);

        W16(OffHintCount, pi.HintOffsets.Length);
        for (int i = 0; i < pi.HintOffsets.Length; i++) W16(OffHintDir + i * 2, pi.HintOffsets[i]);
        pi.Gap2.CopyTo(outBuf, OffHintDir + pi.HintOffsets.Length * 2);

        foreach (PlaneRecord p in pi.Planes)
        {
            int o = p.Offset;
            outBuf[o + PfClassId] = p.AircraftClassId;
            W16(o + PfNameFullPtr, p.NameFullPtr);
            W16(o + PfNameShortPtr, p.NameShortPtr);
            W16(o + PfArm1Ptr, p.Armament1Ptr);
            W16(o + PfArm2Ptr, p.Armament2Ptr);
            W16(o + PfArm3Ptr, p.Armament3Ptr);
            W16(o + PfArmSumPtr, p.ArmamentSumPtr);
            outBuf[o + PfArmRating] = p.ArmamentRating;
            W16(o + PfEnginePtr, p.EngineNamePtr);
            W16(o + PfWeightLb, p.WeightLb);
            W16(o + PfMaxSpeedMph, p.MaxSpeedMph);
            W16(o + PfMaxAltFt, p.MaxAltFt);
            W16(o + PfThrustWeightQ8, p.ThrustWeightQ8);
            W16(o + PfWingLoadingPsf, p.WingLoadingPsf);
            W16(o + PfDescriptionPtr, p.DescriptionPtr);
            W16(o + PfHangarPosX, p.HangarPosX);
            W16(o + PfHangarPosY, p.HangarPosY);
            W16(o + PfHangarPosZ, p.HangarPosZ);
            W16(o + PfSilhouetteYOff, p.SilhouetteYOff);
            W16(o + PfLengthFt, p.LengthFt);
            W16(o + PfLengthIn, p.LengthIn);
            W16(o + PfHeightFt, p.HeightFt);
            W16(o + PfHeightIn, p.HeightIn);
            W16(o + PfDimLenX1, p.DimLenX1);
            W16(o + PfDimLenX2, p.DimLenX2);
            W16(o + PfDimHgtY1, p.DimHgtY1);
            W16(o + PfDimHgtY2, p.DimHgtY2);
            p.Blob.CopyTo(outBuf, o + PlaneHeaderLen);
        }

        foreach (HintRecord h in pi.Hints)
        {
            outBuf[h.Offset + HfAllyClass] = h.AllyClassId;
            outBuf[h.Offset + HfEnemyClass] = h.EnemyClassId;
            h.Blob.CopyTo(outBuf, h.Offset + HfText);
        }

        if (pi.Trailer.Length > 0)
            pi.Trailer.CopyTo(outBuf, outBuf.Length - pi.Trailer.Length);
        return outBuf;
    }

    // ---- formatting helpers -------------------------------------------------

    /// <summary>
    /// Format a u8.8 fixed-point value the way the engine prints THRUST/WEIGHT:
    /// integer part = the HIGH byte, fraction = <c>lo * 100 / 256</c> (the
    /// 32-bit muldiv at image@0x26FB5: <c>dx=0x64, bx=0x100,
    /// lcall 0x201D:0x17A4</c>), then <c>"%d.%02d"</c>
    /// (the THRUST/WEIGHT row's format literal at DGROUP+0x39BC).
    /// </summary>
    public static string FormatQ8(ushort q8)
    {
        int whole = q8 >> 8;
        int frac = (q8 & 0xFF) * 100 / 256;   // integer division, as the game does
        return $"{whole}.{frac:D2}";
    }
}
