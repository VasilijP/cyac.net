using System.Buffers.Binary;

namespace CYAC.Formats.EaLib;

// ---------------------------------------------------------------------------
// .FME / .FMD — the per-aircraft FLIGHT-ENVELOPE and FLIGHT-MODEL files.
//
// 2b.lib ships 12 LZSS assets (encoding flag 0x01): one `.FME` + one `.FMD`
// for each of the SIX flyable aircraft.  The six basenames are not guessed —
// they are the literal contents of `g_aircraft_basename_table` at
// DGROUP+0x2A02, indexed by `g_active_aircraft_idx` [0xC31A]:
//
//     0 p51   1 fw190   2 f86   3 mig15   4 f4   5 mig21
//
// `flight_model_load_for_aircraft @ image@0x2A112` builds "<basename>.FMD" and
// "<basename>.FME" from that table (literals at image@0x35EE / image@0x35F3).
//
// This class is
// its C# twin and re-emits byte-exact too.
//
// -------------------------------------------------------------------------
// .FMD — 298 B.  Loaded by `ealib_load_asset(mode=0xFF, DS:SI=&master)`
// (image@0x2A145), i.e. BULK-COPIED 1:1 into `s_aircraft_master` [0xEF98]:
// FMD[X] -> master[+X] for all 298 bytes.  The file therefore IS
// the initial image of the runtime aircraft struct, and its layout is the
// master struct's layout:
//
//   0x00..0x8F   9 x 16-B INTEGRATOR BLOCKS (the s_ctrl_state_block template)
//   0x90..0x129  154-B scalar tail
//
// The 16-B template (KNOWN_FIELDS["s_ctrl_state_block"]):
//
//   +0x00 i32 value       integration accumulator   (0 in every shipped file)
//   +0x04 i32 working     scratch                   (0 in every shipped file)
//   +0x08 i16 hi_bound    positive-axis limit
//   +0x0A i16 lo_bound    negative-axis limit
//   +0x0C i16 base_dir    directional base step
//   +0x0E i16 dir_step    sign-flip kick / decay target
//
// The template is byte-anchored for the three PROVEN control blocks: scanner
// names master+0x38/+0x3A `ctrl_dead_hi_bound`/`ctrl_dead_lo_bound` and
// +0x3C/+0x3E `ctrl_dead_base_dir`/`ctrl_dead_dir_step` (block 3), the
// `ctrl_alive_*` analogues at +0x58..+0x5E (block 5), and the G-axis block at
// master+0xD6 whose hi/lo bounds land at +0xDE/+0xE0 (in the TAIL).  For the
// remaining blocks the same +/- pair shape holds in all six shipped files but
// the semantics are a hypothesis.
//
// -------------------------------------------------------------------------
// .FME — 504 B = 14 x 36 B `FlightModelEnvelopeRecord`.  Loaded by
// `flight_envelope_load @ image@0x2AA76`, far ptr stored at master+0x11E/+0x120,
// then all 14 records walked to accumulate max-X -> master+0x126 and max-Y ->
// master+0x128 (image@0x2AB07 / image@0x2AB19).  Record stride 0x24 verified
// `add si,0x24` image@0x2AB25; point stride 4 verified `add si,4` image@0x2AB1D.
//
// THE FME IS A V-n-BY-ALTITUDE ENVELOPE.  Each record is keyed by a SIGNED
// ordinal at +0x00 running -4..+9 with rec[i].ordinal == i - 4 in every
// shipped file.  The lookup key is master+0xD7 (`fme_record_lookup_by_key
// @ image@0x2AB36`; CBW at image@0x2AB47 proves the SIGNED compare) — and
// master+0xD7 is the HIGH byte of the Q8.8 word at 0xF06E ==
// `g_player_gload_q8`, i.e. the INTEGER LOAD FACTOR in G.  The ordinal is G.
//
// Within a record `points[k] = { i16 x, i16 y }`:
//   x = airspeed, feet/second
//   y = ALTITUDE in units of feet/8.  `fme_altitude_speed_check @
//       image@0x2A8EC` forms altitude_scaled = (i32)FMD[+0xA..+0xD] >> 11
//       (image@0x2A90E..image@0x2A922) and compares it against points[k].y
//       (image@0x2A934 / image@0x2A95A).  The FMD altitude i32 is Q8 feet, so
//       y == feet >> 3.
//
// Each curve reads: "at this load factor, the maximum altitude attainable at
// each airspeed" — zero at the low-speed (stall) end, rising to a peak,
// falling back to zero at the high-speed end.
//
// ⚠ points[n_points..7] are FILLER (stale authoring-tool buffer bytes, often a
// copy of some record's 4-byte {ord,n,peak,hs} header).  They are NOT read by
// the bounded consumers, but `fme_low_speed_limit_check @ image@0x2A9BE`
// scans with a FIXED ceiling of fme_base+0x20 rather than n_points, so they
// are reachable in principle.  See P8 findings §4.2.
// ---------------------------------------------------------------------------
public static class FlightModelDecoder
{
    public const int FmeLength = 504;
    public const int FmeRecordCount = 14;
    public const int FmeRecordStride = 0x24;   // image@0x2AB25
    public const int FmeMaxPoints = 8;
    public const int FmePointStride = 4;       // image@0x2AB1D
    public const int FmeFirstPointOff = 4;     // image@0x2AAD7

    public const int FmdLength = 298;
    public const int FmdBlockCount = 9;
    public const int FmdBlockStride = 0x10;
    public const int FmdTailOff = 0x90;
    public const int FmdTailLength = FmdLength - FmdTailOff;   // 154

    /// <summary>
    /// The six flyable basenames in `g_active_aircraft_idx` [0xC31A] order,
    /// as stored in `g_aircraft_basename_table` DGROUP+0x2A02.
    /// </summary>
    public static readonly string[] Basenames =
        { "p51", "fw190", "f86", "mig15", "f4", "mig21" };

    // A display name is not in the .fme/.fmd: it is the pi.bin page's short name of the
    // flyable class, which the caller reads and passes to LoadFromArchive.

    /// <summary>Feet-per-second to miles-per-hour.</summary>
    public static double FpsToMph(int fps) => fps * 3600.0 / 5280.0;

    // =====================================================================
    // .FME
    // =====================================================================
    public sealed class EnvelopePoint
    {
        public short X { get; init; }          // airspeed, fps
        public short Y { get; init; }          // altitude / 8, feet
        public int AltitudeFt => Y * 8;
        public override string ToString() => $"({X} fps, {AltitudeFt} ft)";
    }

    public sealed class EnvelopeRecord
    {
        public required int Index { get; init; }
        /// <summary>+0x00 i8 — SIGNED load factor in G (-4..+9); the lookup key.</summary>
        public required sbyte Ordinal { get; init; }
        /// <summary>+0x01 u8 — valid point count; loop bound image@0x2AAC8.</summary>
        public required byte PointCount { get; init; }
        /// <summary>+0x02 u8 — index of the peak-Y point (image@0x2A95A).</summary>
        public required byte PeakIdx { get; init; }
        /// <summary>+0x03 u8 — high-speed boundary point (image@0x2A934 / image@0x2ABEC).</summary>
        public required byte HighSpeedIdx { get; init; }
        /// <summary>All 8 slots; only [0..PointCount-1] are authored.</summary>
        public required EnvelopePoint[] Points { get; init; }

        public IEnumerable<EnvelopePoint> ValidPoints => Points.Take(PointCount);
        public IEnumerable<EnvelopePoint> FillerPoints => Points.Skip(PointCount);
        public int FillerCount => FmeMaxPoints - PointCount;

        /// <summary>points[0].x — the low-speed (zero-altitude) end of the curve.</summary>
        public int StallSpeedFps => Points.Length > 0 ? Points[0].X : 0;
        public int MaxSpeedFps => ValidPoints.Select(p => (int)p.X).DefaultIfEmpty(0).Max();
        public int PeakAltitudeFt =>
            PeakIdx < FmeMaxPoints ? Points[PeakIdx].AltitudeFt : 0;
    }

    public sealed class FmeFile
    {
        public required string Basename { get; init; }
        public required byte[] Body { get; init; }
        public required int RawCompressedLength { get; init; }
        public required EnvelopeRecord[] Records { get; init; }

        public sbyte GLoadMin => Records[0].Ordinal;
        public sbyte GLoadMax => Records[^1].Ordinal;

        /// <summary>
        /// Replicates `flight_envelope_load`'s max-scan (image@0x2AAB3..0x2AB2E):
        /// UNSIGNED compares (`jbe` image@0x2AB02 / image@0x2AB13), bounded by
        /// n_points, skipping records with n_points &lt;= 0
        /// (`cmp byte es:[si+1],0` / `jle` image@0x2AAC8).
        /// Returns (master+0x126 corner_x, master+0x128 corner_y).
        /// </summary>
        public (int X, int Y) EnvelopeCorner()
        {
            int cx = 0, cy = 0;
            foreach (EnvelopeRecord r in Records)
            {
                if (r.PointCount == 0) continue;
                for (int k = 0; k < r.PointCount; k++)
                {
                    int x = (ushort)r.Points[k].X;
                    int y = (ushort)r.Points[k].Y;
                    if (x > cx) cx = x;
                    if (y > cy) cy = y;
                }
            }
            return (cx, cy);
        }

        public byte[] ToBytes()
        {
            byte[] b = new byte[FmeLength];
            for (int i = 0; i < FmeRecordCount; i++)
            {
                int o = i * FmeRecordStride;
                EnvelopeRecord r = Records[i];
                b[o] = unchecked((byte)r.Ordinal);
                b[o + 1] = r.PointCount;
                b[o + 2] = r.PeakIdx;
                b[o + 3] = r.HighSpeedIdx;
                for (int k = 0; k < FmeMaxPoints; k++)
                {
                    int p = o + FmeFirstPointOff + k * FmePointStride;
                    BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(p, 2), r.Points[k].X);
                    BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(p + 2, 2), r.Points[k].Y);
                }
            }
            return b;
        }
    }

    public static FmeFile DecodeFme(string basename, byte[] body, int rawCompressedLength = 0)
    {
        if (body.Length != FmeLength)
            throw new InvalidDataException(
                $"{basename}.fme: expected {FmeLength} B body, got {body.Length}");

        EnvelopeRecord[] recs = new EnvelopeRecord[FmeRecordCount];
        for (int i = 0; i < FmeRecordCount; i++)
        {
            int o = i * FmeRecordStride;
            EnvelopePoint[] pts = new EnvelopePoint[FmeMaxPoints];
            for (int k = 0; k < FmeMaxPoints; k++)
            {
                int p = o + FmeFirstPointOff + k * FmePointStride;
                pts[k] = new EnvelopePoint
                {
                    X = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(p, 2)),
                    Y = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(p + 2, 2)),
                };
            }
            recs[i] = new EnvelopeRecord
            {
                Index = i,
                Ordinal = unchecked((sbyte)body[o]),
                PointCount = body[o + 1],
                PeakIdx = body[o + 2],
                HighSpeedIdx = body[o + 3],
                Points = pts,
            };
        }
        return new FmeFile
        {
            Basename = basename,
            Body = body,
            RawCompressedLength = rawCompressedLength,
            Records = recs,
        };
    }

    // =====================================================================
    // .FMD
    // =====================================================================
    /// <summary>Role / confidence annotation per 16-B block (index == master offset / 16).</summary>
    public static readonly (int MasterOff, string Role, string Confidence)[] BlockRoles =
    {
        (0x00, "forward velocity (vel_forward_i32)",           "base verified / bounds hypothesis"),
        (0x10, "angular velocity 2 (state_10_u32)",            "base verified / bounds hypothesis"),
        (0x20, "angular velocity 3 (state_20_u32)",            "base verified / bounds hypothesis"),
        (0x30, "roll control, DEAD path (s_ctrl_state_block)", "verified"),
        (0x40, "heading-from-AoA (state_40)",                  "base verified / bounds hypothesis"),
        (0x50, "roll control, ALIVE path (s_ctrl_state_block)","verified"),
        (0x60, "pos_x   (bounds look like heading limits +/-180)", "hypothesis"),
        (0x70, "pos_y   (bounds look like pitch   limits +/- 90)", "hypothesis"),
        (0x80, "pos_z   (bounds look like roll    limits +/-180)", "hypothesis"),
    };

    public enum TailKind { U8, I16, U16, I32 }

    /// <summary>
    /// The 154-B scalar tail, field by field.  Every offset in 0x90..0x129
    /// appears exactly once, so the tail is 100% covered by this table.
    /// </summary>
    public static readonly (int Off, string Name, TailKind Kind, string? ScannerName, string Note)[]
        TailFields =
    {
        (0x090, "tail_90",              TailKind.U16, null,                       "0 in all 6"),
        (0x092, "perf_param_1",         TailKind.U16, "perf_param_1_u16",         "300 prop / 350 jet"),
        (0x094, "tail_94",              TailKind.U16, null,                       "50 in all 6"),
        (0x096, "tail_96",              TailKind.U16, null,                       "100 in all 6"),
        (0x098, "tail_98",              TailKind.U16, null,                       "2585 in all 6"),
        (0x09A, "engine_pulse_count",   TailKind.U8,  "engine_pulse_count_u8",    "15/12 props, 0 jets"),
        (0x09B, "tail_9B",              TailKind.U8,  null,                       "0 in all 6"),
        (0x09C, "thrust_floor",         TailKind.I32, "s_aircraft_master +0x9C",  "runtime slot, 0 on disk"),
        (0x0A0, "throttle_target",      TailKind.I32, "s_aircraft_master +0xA0",  "runtime slot, 0 on disk"),
        (0x0A4, "hp_initial",           TailKind.U16, "s_aircraft_master +0xA4",  "100 in all 6"),
        (0x0A6, "hp_reference",         TailKind.U16, "s_aircraft_master +0xA6",  "0 in all 6"),
        (0x0A8, "perf_param_2a",        TailKind.U16, "perf_param_2a_u16",        "== 2b in all 6"),
        (0x0AA, "perf_param_2b",        TailKind.U16, "perf_param_2b_u16",        "== 2a in all 6"),
        (0x0AC, "tail_AC",              TailKind.U16, null,                       "6400 in all 6"),
        (0x0AE, "tail_AE",              TailKind.U16, null,                       "19250 in all 6"),
        (0x0B0, "tail_B0",              TailKind.U16, null,                       "100 in all 6"),
        (0x0B2, "tail_B2",              TailKind.U16, null,                       "0 in all 6"),
        (0x0B4, "tail_B4",              TailKind.U16, null,                       "0 in all 6"),
        (0x0B6, "supersonic_param",     TailKind.U16, null,                       "0 except f4=512, mig21=460"),
        (0x0B8, "tail_B8",              TailKind.U16, null,                       "0 in all 6"),
        (0x0BA, "tail_BA",              TailKind.U16, null,                       "0 in all 6"),
        (0x0BC, "gross_weight_lb",      TailKind.U16, "perf_scalar_max_u16",      "== pi.bin WEIGHT for p51/fw190"),
        (0x0BE, "tail_BE",              TailKind.U16, null,                       "0 on disk (runtime heading_accum hi)"),
        (0x0C0, "fuel_remaining",       TailKind.I32, "s_aircraft_master +0xC0",  "0 on disk; loader writes initial_fuel<<8"),
        (0x0C4, "tail_C4",              TailKind.U16, null,                       "0 in all 6"),
        (0x0C6, "tail_C6",              TailKind.U16, null,                       "0 in all 6"),
        (0x0C8, "initial_fuel",         TailKind.U16, "engine_power_u16",         "loader: master+0xC0 = this << 8"),
        (0x0CA, "tail_CA",              TailKind.U16, null,                       "0 in all 6"),
        (0x0CC, "armament_count",       TailKind.U16, "armament_count_u16",       "6/6/8/6/35/17"),
        (0x0CE, "tail_CE",              TailKind.U16, null,                       "0 in all 6"),
        (0x0D0, "perf_limit",           TailKind.U16, "perf_limit_u16",           "loader: master+0xD2 = (0xBC*this)>>11"),
        (0x0D2, "heading_angular_rate", TailKind.U16, "s_aircraft_master +0xD2",  "0 on disk; loader-computed"),
        (0x0D4, "heading_scaled",       TailKind.U16, "s_aircraft_master +0xD4",  "0 on disk"),
        // ---- master+0xD6 s_ctrl_state_block: the G / load-factor axis ------
        (0x0D6, "gload_current_q8",     TailKind.I32, "s_aircraft_master +0xD6",  "0 on disk; lo word = g_player_gload_q8 [0xF06E]"),
        (0x0DA, "gload_working",        TailKind.I32, "s_aircraft_master +0xDA",  "0 on disk (scratch)"),
        (0x0DE, "gload_max",            TailKind.I16, "era_tier_u16",             "+7 prop / +8 Korea jet / +9 Vietnam jet"),
        (0x0E0, "gload_min",            TailKind.I16, null,                       "-4 in all 6"),
        (0x0E2, "gload_ctrl_base_dir",  TailKind.I16, "stall_low_u16",            "AoA/G ctrl base_dir"),
        (0x0E4, "gload_ctrl_dir_step",  TailKind.I16, "stall_high_u16",           "AoA/G ctrl dir_step"),
        // -------------------------------------------------------------------
        (0x0E6, "reset_sentinel",       TailKind.I16, "s_aircraft_master +0xE6",  "-1 in all 6"),
        (0x0E8, "aoa_valid_min",        TailKind.I16, "s_aircraft_master +0xE8",  "0 on disk (runtime)"),
        (0x0EA, "aoa_valid_max",        TailKind.I16, "s_aircraft_master +0xEA",  "0 on disk (runtime)"),
        (0x0EC, "control_rate",         TailKind.U16, "control_rate_u16",         "12/15/24/18/60/55"),
        (0x0EE, "tail_EE",              TailKind.U16, null,                       "256 in all 6"),
        (0x0F0, "gear_limit",           TailKind.U16, "gear_limit_u16",           "230/256/230/256/640/537"),
        (0x0F2, "tail_F2",              TailKind.U16, null,                       "256 in all 6"),
        (0x0F4, "tail_F4",              TailKind.U16, null,                       "256 in all 6"),
        (0x0F6, "tail_F6",              TailKind.U16, null,                       "512 in all 6"),
        (0x0F8, "tail_F8",              TailKind.U16, null,                       "0 in all 6"),
        (0x0FA, "jet_flag_q8",          TailKind.U16, "afterburner_avail_u16",    "0 props / 256 jets"),
        (0x0FC, "tail_FC",              TailKind.U16, null,                       "128 in all 6"),
        (0x0FE, "tail_FE",              TailKind.U16, null,                       "102 props / 153 jets"),
        (0x100, "tail_100",             TailKind.U16, null,                       "153 in all 6"),
        (0x102, "tail_102",             TailKind.U16, null,                       "76 props / 25 jets"),
        (0x104, "tail_104",             TailKind.U16, null,                       "128 in all 6"),
        (0x106, "camera_alt_hold",      TailKind.U16, "s_aircraft_master +0x106", "0 on disk (runtime)"),
        (0x108, "tail_108",             TailKind.U16, null,                       "2560 in all 6"),
        (0x10A, "tail_10A",             TailKind.U16, null,                       "3840 in all 6"),
        (0x10C, "tail_10C",             TailKind.U16, null,                       "0 in all 6"),
        (0x10E, "airspeed_a",           TailKind.I32, "s_aircraft_master +0x10E", "0 on disk (runtime)"),
        (0x112, "ceiling_ft_q8",        TailKind.I32, "(scanner: 'fingerprint')", "== service ceiling << 8"),
        (0x116, "airspeed_b",           TailKind.I32, "s_aircraft_master +0x116", "0 on disk; loader overrides"),
        (0x11A, "fmd_ptr_off",          TailKind.U16, "s_aircraft_master +0x11A", "0 on disk; loader writes"),
        (0x11C, "fmd_ptr_seg",          TailKind.U16, "s_aircraft_master +0x11C", "0 on disk; loader writes"),
        (0x11E, "fme_ptr_off",          TailKind.U16, "s_aircraft_master +0x11E", "0 on disk; flight_envelope_load writes"),
        (0x120, "fme_ptr_seg",          TailKind.U16, "s_aircraft_master +0x120", "0 on disk; flight_envelope_load writes"),
        (0x122, "active_flag",          TailKind.U8,  "s_aircraft_master +0x122", "0 on disk; loader writes 1"),
        (0x123, "damage_flags",         TailKind.U8,  "s_aircraft_master +0x123", "0 on disk"),
        (0x124, "status_flags_init",    TailKind.U8,  "s_aircraft_master +0x124", "0x20 props / 0x60 jets"),
        (0x125, "tail_125",             TailKind.U8,  null,                       "0 in all 6"),
        (0x126, "envelope_corner_x",    TailKind.U16, "s_aircraft_master +0x126", "0 on disk; flight_envelope_load computes"),
        (0x128, "envelope_corner_y",    TailKind.U16, "s_aircraft_master +0x128", "0 on disk; flight_envelope_load computes"),
    };

    public sealed class IntegratorBlock
    {
        public required int Index { get; init; }
        public int MasterOffset => Index * FmdBlockStride;
        public required int Value { get; init; }      // +0x00 i32
        public required int Working { get; init; }    // +0x04 i32
        public required short HiBound { get; init; }  // +0x08 i16
        public required short LoBound { get; init; }  // +0x0A i16
        public required short BaseDir { get; init; }  // +0x0C i16
        public required short DirStep { get; init; }  // +0x0E i16

        public string Role => BlockRoles[Index].Role;
        public string Confidence => BlockRoles[Index].Confidence;
    }

    public sealed class FmdFile
    {
        public required string Basename { get; init; }
        public required byte[] Body { get; init; }
        public required int RawCompressedLength { get; init; }
        public required IntegratorBlock[] Blocks { get; init; }
        public required Dictionary<string, int> Tail { get; init; }

        public int this[string field] => Tail[field];

        /// <summary>FMD+0x112 i32 &gt;&gt; 8 == service ceiling in feet.</summary>
        public int CeilingFt => Tail["ceiling_ft_q8"] >> 8;
        /// <summary>block[0].hi_bound — the forward-velocity positive limit, fps.</summary>
        public int MaxForwardSpeedFps => Blocks[0].HiBound;
        public int GLoadMax => Tail["gload_max"];
        public int GLoadMin => Tail["gload_min"];
        public bool IsJet => Tail["jet_flag_q8"] != 0;

        /// <summary>What the loader writes to master+0xC0 (fuel = initial_fuel &lt;&lt; 8).</summary>
        public int LoaderFuel => Tail["initial_fuel"] << 8;
        /// <summary>master+0xD2 = (gross_weight * perf_limit) &gt;&gt; 11 (loader override).</summary>
        public int LoaderHeadingRate => (Tail["gross_weight_lb"] * Tail["perf_limit"]) >> 11;

        public byte[] ToBytes()
        {
            byte[] b = new byte[FmdLength];
            foreach (IntegratorBlock blk in Blocks)
            {
                int o = blk.Index * FmdBlockStride;
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o, 4), blk.Value);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 4, 4), blk.Working);
                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o + 8, 2), blk.HiBound);
                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o + 0xA, 2), blk.LoBound);
                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o + 0xC, 2), blk.BaseDir);
                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o + 0xE, 2), blk.DirStep);
            }
            foreach ((int off, string name, TailKind kind, string? _, string _) in TailFields)
            {
                int v = Tail[name];
                switch (kind)
                {
                    case TailKind.U8: b[off] = (byte)v; break;
                    case TailKind.I16:
                        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(off, 2), (short)v); break;
                    case TailKind.U16:
                        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(off, 2), (ushort)v); break;
                    case TailKind.I32:
                        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(off, 4), v); break;
                }
            }
            return b;
        }
    }

    public static FmdFile DecodeFmd(string basename, byte[] body, int rawCompressedLength = 0)
    {
        if (body.Length != FmdLength)
            throw new InvalidDataException(
                $"{basename}.fmd: expected {FmdLength} B body, got {body.Length}");

        IntegratorBlock[] blocks = new IntegratorBlock[FmdBlockCount];
        for (int i = 0; i < FmdBlockCount; i++)
        {
            int o = i * FmdBlockStride;
            blocks[i] = new IntegratorBlock
            {
                Index = i,
                Value = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(o, 4)),
                Working = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(o + 4, 4)),
                HiBound = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(o + 8, 2)),
                LoBound = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(o + 0xA, 2)),
                BaseDir = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(o + 0xC, 2)),
                DirStep = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(o + 0xE, 2)),
            };
        }

        Dictionary<string, int> tail = new Dictionary<string, int>(TailFields.Length);
        foreach ((int off, string name, TailKind kind, string? _, string _) in TailFields)
        {
            tail[name] = kind switch
            {
                TailKind.U8 => body[off],
                TailKind.I16 => BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(off, 2)),
                TailKind.U16 => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(off, 2)),
                TailKind.I32 => BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(off, 4)),
                _ => 0,
            };
        }

        return new FmdFile
        {
            Basename = basename,
            Body = body,
            RawCompressedLength = rawCompressedLength,
            Blocks = blocks,
            Tail = tail,
        };
    }

    // =====================================================================
    // Aggregate: all 6 aircraft x {fme, fmd}
    // =====================================================================
    public sealed class Aircraft
    {
        public required int Index { get; init; }
        public required string Basename { get; init; }

        /// <summary>The caller's display name for the aircraft; empty when it gave none.</summary>
        public string DisplayName { get; init; } = "";
        public required FmeFile Fme { get; init; }
        public required FmdFile Fmd { get; init; }
    }

    public sealed class FlightModelSet
    {
        public required Aircraft[] Aircraft { get; init; }
        public int Count => Aircraft.Length;
    }

    /// <summary>
    /// Load all 12 assets from an open 2b.lib archive.  Entry names in the
    /// archive are upper-case ("P51.FME"); lookup is case-insensitive.
    /// </summary>
    /// <param name="lib">The open 2b.lib.</param>
    /// <param name="displayNames">
    /// The six display names in <see cref="Basenames"/> order, or null.  They are not in these
    /// assets: they are the pi.bin pages' short names of the flyable classes.
    /// </param>
    public static FlightModelSet LoadFromArchive(EaLibArchive lib, IReadOnlyList<string>? displayNames = null)
    {
        List<Aircraft> list = new List<Aircraft>();
        for (int i = 0; i < Basenames.Length; i++)
        {
            string bn = Basenames[i];
            EaLibEntry fmeEntry = FindEntryCI(lib, bn + ".fme")
                                  ?? throw new InvalidDataException($"{bn}.fme not found in {lib.ShortName}");
            EaLibEntry fmdEntry = FindEntryCI(lib, bn + ".fmd")
                                  ?? throw new InvalidDataException($"{bn}.fmd not found in {lib.ShortName}");
            list.Add(new Aircraft
            {
                Index = i,
                Basename = bn,
                DisplayName = displayNames is not null && i < displayNames.Count ? displayNames[i] : "",
                Fme = DecodeFme(bn, fmeEntry.GetDecoded(), fmeEntry.Length),
                Fmd = DecodeFmd(bn, fmdEntry.GetDecoded(), fmdEntry.Length),
            });
        }
        return new FlightModelSet { Aircraft = list.ToArray() };
    }

    private static EaLibEntry? FindEntryCI(EaLibArchive lib, string name)
        => lib.Entries.FirstOrDefault(
            e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}
