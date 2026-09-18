namespace CYAC.Formats.EaLib;

// ---------------------------------------------------------------------------
// scenario.bin — the mission (scenario) catalog.  2a.lib idx "scenario.bin",
// LZSS-compressed (encoding flag 0x01): 1,611 B raw -> 8,200 B decompressed.
//
// Format:
//
//   record stride  164 (0xA4)   — `mov cx, 0xA4` image@0x2472B + `imul cx`
//                                 image@0x2472E; independently
//                                 `mov ax,0xA4; imul [bp-2]` image@0x24BEB.
//   record count   50           — derived at runtime by `mov cx,0xA4; div cx`
//                                 on the asset size, image@0x24B2B.
//   50 x 164 = 8,200 B exact (no header, no trailer).
//
// s_scenario_record (164 B):
//   +0x00  1    record_index_u8       mirrors the slot 0..49 (all 50 verified)
//                                     read image@0x2478A -> [0xEE2E]
//   +0x01  1    era_u8                0=WWII 1=Korea 2=Vietnam
//                                     `cmp byte es:[bx+1], al` image@0x24B51 vs
//                                     g_scenario_era_selector [0x2A0E] inside
//                                     scenario_filter_by_era image@0x24B24
//   +0x02  1    insignia_idx_u8       national marking of the PLAYER's side;
//                                     `mov al, es:[si+2]` image@0x24C2A ->
//                                     panel_sprite_blit image@0x2569C, which
//                                     blits a 32x11 frame at source-x = idx*32
//                                     (`shl ax,5` image@0x256C2) from the
//                                     "insig" atlas (2a.lib::INSIG.PIC).
//   +0x03  1    aircraft_idx_u8       player aircraft 0..5; `mov al,es:[bx+3]`
//                                     image@0x24799 -> word-index into the
//                                     flyable table at DGROUP+0xFBC
//                                     (= image@0x3CD1C), 6 live entries.
//   +0x04  1    opponent_class_u8     featured opponent aircraft class id
//                                     (0 = none); `cmp byte es:[bx+4],0`
//                                     image@0x247B8, non-zero -> class-table
//                                     lookup image@0x24058 -> [0xF1C6].
//   +0x05  1    difficulty_rating_u8  MISSION rating 1..3 (manual p.21 "small
//                                     squares"); dot render image@0x24D72..
//                                     0x24DA4; also -> [0xEE30] -> Yeager
//                                     pre-mission speech tier image@0x25493.
//                                     NOT the player-selectable EASY/NORMAL/
//                                     HARD/EXPERT setting (manual p.22).
//   +0x06  9    date_str[9]           "M-D-YY", NUL-term + NUL pad
//                                     `add ax, 6` image@0x24779 -> [0xEE24]
//   +0x0F  30   title_str[30]         `add ax, 0x0F` image@0x24765 -> [0xEE06]
//   +0x2D  14   s_filename[14]        .S mission-module filename
//                                     `add ax, 0x2D` image@0x24751 -> [0xEF82]
//   +0x3B  105  description[105]      picker/briefing blurb; `add si, 0x3B`
//                                     image@0x24D03 -> text_wrap_and_print
//                                     image@0x245DC (x=0xEE, w=0x36 chars)
//
// Padding audit: every one of the 200 string fields is NUL-terminated and every
// byte after the terminator is 0x00 — which is what makes ToBytes below a
// byte-exact re-emitter.
// ---------------------------------------------------------------------------
public static class ScenarioBinDecoder
{
    public const int RecordSize = 0xA4;      // 164 — image@0x2472B
    public const int ExpectedRecordCount = 50;
    public const int ExpectedDecompressedSize = RecordSize * ExpectedRecordCount; // 8200

    // Field offsets within a record.
    public const int OffRecordIndex = 0x00;
    public const int OffEra = 0x01;
    public const int OffInsigniaIdx = 0x02;
    public const int OffAircraftIdx = 0x03;
    public const int OffOpponentClass = 0x04;
    public const int OffDifficultyRating = 0x05;
    public const int OffDate = 0x06; public const int LenDate = 9;
    public const int OffTitle = 0x0F; public const int LenTitle = 30;
    public const int OffSFilename = 0x2D; public const int LenSFilename = 14;
    public const int OffDescription = 0x3B; public const int LenDescription = 105;

    // -----------------------------------------------------------------------
    // THE ONLY HARDCODED "50" IN THE MISSION PATH.
    //
    // The record COUNT is size-derived everywhere (`mov cx,0xA4; div cx` on the
    // asset size, scenario_filter_by_era image@0x24B2B) — a 51st record flows
    // through the era filter, the picker and the detail panel untouched.
    //
    // record_index_u8 (+0x00) is different: it is NOT a decorative slot mirror,
    // it is the INDEX INTO A 50-BYTE PER-MISSION PROGRESS ARRAY at DGROUP
    // [0xEF50..0xEF81]:
    //
    //   read   image@0x246E0  `cmp byte [bx+0xEF50], 2` / image@0x246EC
    //                         `cmp byte [bx+0xEF4F], 2`  (bx = record[+0])
    //                         scenario_record_unlock_gate image@0x246DE
    //                         — the picker's "may this mission be selected?" gate:
    //                         arr[idx] >= 2 || (idx >= 1 && arr[idx-1] >= 2)
    //                         (the idx>=1 guard is `cmp ax,1 / jl` @image@0x246E7 — port build B6 D3).
    //   read   image@0x24CDE  `cmp byte [bx+0xEF50], 3`  scenario_detail_panel_show
    //                         — draws the extra status label in the detail panel.
    //   WRITE  image@0x25C89  `add si, 0xEF50` / image@0x25C8D `inc byte [si]` /
    //          image@0x25C94  `mov byte [si], 2` (clamp), with
    //          si = g_record_index_u8 [0xEE2E]  — ui_post_death_message
    //          image@0x25BAF bumps the mission's own progress byte at mission end.
    //   size   image@0x2D809  memset([0xEF50], 0, 0x32)  then memset([0xEF50], 2, 2)
    //          image@0x2D3EA  memcpy([0xEF50] <- cfg+0x24, 0x32)   (cfg load)
    //          image@0x2D7BA  memcpy(cfg+0x24 <- [0xEF50], 0x32)   (cfg save)
    //          0x32 = 50 exactly; sources/yeager.cfg is 86 B = 0x24 header + 50.
    //
    // The byte immediately after the array, [0xEF82], is g_record_filename_buf
    // (the 14-B active `.S` name, written at image@0x2475A) — so a record whose
    // record_index_u8 is 50 makes the engine INCREMENT THE FIRST CHARACTER OF THE
    // ACTIVE MISSION FILENAME at mission end (and then clamp it to 2).  Reads at
    // 50 are harmless (the `idx-1` arm of the gate falls back on arr[49]); the
    // write is not.
    //
    // ⇒ Authoring rule: a mission beyond the 50th must REUSE a record index in
    // 0..49 (a clone naturally reuses its template's).  Two missions sharing an
    // index simply share one progression slot — behaviourally invisible in the
    // shipped configuration, where the whole array is already 0x02.
    // -----------------------------------------------------------------------

    /// <summary>Entries in the per-mission progress array [0xEF50] — image@0x2D809 `mov ax,0x32`.</summary>
    public const int ProgressSlotCount = 50;

    /// <summary>Largest legal value of record_index_u8 (+0x00).</summary>
    public const int MaxRecordIndex = ProgressSlotCount - 1;

    /// <summary>DGROUP address of the progress array, for diagnostics.</summary>
    public const int ProgressArrayDgroupAddr = 0xEF50;

    /// <summary>Why <paramref name="recordIndex"/> is not usable, or null.</summary>
    public static string? ValidateRecordIndex(int recordIndex) =>
        recordIndex is >= 0 and <= MaxRecordIndex
            ? null
            : $"record_index_u8 = {recordIndex} is outside 0..{MaxRecordIndex}: the engine uses it " +
              $"to index the {ProgressSlotCount}-byte per-mission progress array at " +
              $"[0x{ProgressArrayDgroupAddr:X4}] and WRITES through it at image@0x25C8D — index " +
              $"{ProgressSlotCount} would increment g_record_filename_buf[0] ([0xEF82]). " +
              "Reuse an index 0..49 (e.g. the template mission's).";

    // ---- names -------------------------------------------------------------
    //
    // The decoder keeps ids.  The designations are read from the originals at
    // transform time (CYAC.Port.Transform OriginalNames: the class table's
    // engagement prototypes and the flyable table at DGROUP+0xFBC); the era,
    // insignia and rating words the reading aids print are the port's own labels
    // (ReadingAidLabels).
    //
    // What the ids index:
    //   era_u8            compared against g_scenario_era_selector [0x2A0E] in
    //                     scenario_filter_by_era image@0x24B24 (image@0x24B51);
    //                     0..2, census 17 / 16 / 17.
    //   insignia_idx_u8   a frame of the 4-frame 32x11 "insig" atlas
    //                     (2a.lib::INSIG.PIC) via panel_sprite_blit image@0x2569C
    //                     (source-x = idx << 5, image@0x256C2); exactly 4 frames.
    //   aircraft_idx_u8   a slot of the 6-entry flyable table at DGROUP+0xFBC
    //                     (image@0x3CD1C, read at image@0x247A4): near pointers to
    //                     the stat blocks whose +0x04 word points at the name.
    //   opponent_class_u8 an id into the 46-entry class table image@0x34F90
    //                     (lookup image@0x24058); 0 = no featured opponent
    //                     (image@0x247B8).
    //   difficulty_rating_u8  1..3 dots (image@0x24D72..0x24DA4; manual p.21),
    //                     census {1:17, 2:15, 3:18}; not the Diff: LEVEL (p.22).

    // ---- model -----------------------------------------------------------

    public sealed record ScenarioRecord(
        int Slot,
        byte RecordIndex,
        byte Era,
        byte InsigniaIdx,
        byte AircraftIdx,
        byte OpponentClass,
        byte DifficultyRating,
        string Date,
        string Title,
        string SFilename,
        string Description)
    {
        /// <summary>
        /// The manual's "small squares" rating rendered as filled/hollow dots.
        /// The engine prints exactly <c>DifficultyRating</c> '.' glyphs
        /// (image@0x24D8C writes the NUL at buf[rating]); we show three slots
        /// so the column stays column-aligned.
        /// </summary>
        public string RatingDots
        {
            get
            {
                int n = Math.Clamp((int)DifficultyRating, 0, 3);
                return new string('●', n) + new string('○', 3 - n);
            }
        }

        /// <summary>True when the mirrored slot byte disagrees with the slot.</summary>
        public bool IndexMirrorOk => RecordIndex == Slot;
    }

    public sealed record Catalog(
        IReadOnlyList<ScenarioRecord> Records,
        int RecordSizeBytes,
        int DecompressedLength,
        int RawCompressedLength,
        byte[] DecompressedBytes);

    // ---- decode ----------------------------------------------------------

    public static Catalog DecodeFromEntry(EaLibEntry entry)
    {
        if (entry.Encoding != EncodingFlag.Compressed)
            throw new InvalidDataException(
                $"{entry.Archive.ShortName}::{entry.Name} is not LZSS-compressed " +
                $"(encoding={entry.Encoding}); scenario.bin must be flag=0x01");
        byte[] decoded = entry.GetDecoded();
        return DecodeFromDecompressed(decoded, entry.Length);
    }

    public static Catalog DecodeFromDecompressed(byte[] decoded, int rawCompressedLength = 0)
    {
        if (decoded.Length % RecordSize != 0)
            throw new InvalidDataException(
                $"scenario.bin: {decoded.Length} B is not a multiple of the " +
                $"{RecordSize}-B record stride (image@0x2472B)");
        int count = decoded.Length / RecordSize;   // runtime does the same div, image@0x24B2B

        List<ScenarioRecord> records = new List<ScenarioRecord>(count);
        for (int i = 0; i < count; i++)
        {
            int b = i * RecordSize;
            records.Add(new ScenarioRecord(
                Slot: i,
                RecordIndex: decoded[b + OffRecordIndex],
                Era: decoded[b + OffEra],
                InsigniaIdx: decoded[b + OffInsigniaIdx],
                AircraftIdx: decoded[b + OffAircraftIdx],
                OpponentClass: decoded[b + OffOpponentClass],
                DifficultyRating: decoded[b + OffDifficultyRating],
                Date: ReadCString(decoded, b + OffDate, LenDate),
                Title: ReadCString(decoded, b + OffTitle, LenTitle),
                SFilename: ReadCString(decoded, b + OffSFilename, LenSFilename),
                Description: ReadCString(decoded, b + OffDescription, LenDescription)));
        }

        return new Catalog(
            Records: records,
            RecordSizeBytes: RecordSize,
            DecompressedLength: decoded.Length,
            RawCompressedLength: rawCompressedLength,
            DecompressedBytes: decoded);
    }

    /// <summary>
    /// Byte-exact re-emitter (mirrors's round-trip proof).  Legal because
    /// the padding audit found every byte after each field's NUL terminator
    /// to be 0x00 in all 200 string fields.
    /// </summary>
    public static byte[] ToBytes(Catalog catalog)
    {
        byte[] outBuf = new byte[catalog.Records.Count * RecordSize];
        for (int i = 0; i < catalog.Records.Count; i++)
        {
            ScenarioRecord r = catalog.Records[i];
            int b = i * RecordSize;
            outBuf[b + OffRecordIndex] = r.RecordIndex;
            outBuf[b + OffEra] = r.Era;
            outBuf[b + OffInsigniaIdx] = r.InsigniaIdx;
            outBuf[b + OffAircraftIdx] = r.AircraftIdx;
            outBuf[b + OffOpponentClass] = r.OpponentClass;
            outBuf[b + OffDifficultyRating] = r.DifficultyRating;
            WriteCString(outBuf, b + OffDate, LenDate, r.Date);
            WriteCString(outBuf, b + OffTitle, LenTitle, r.Title);
            WriteCString(outBuf, b + OffSFilename, LenSFilename, r.SFilename);
            WriteCString(outBuf, b + OffDescription, LenDescription, r.Description);
        }
        return outBuf;
    }

    private static string ReadCString(byte[] buf, int off, int maxLen)
    {
        int n = 0;
        while (n < maxLen && buf[off + n] != 0) n++;
        return System.Text.Encoding.Latin1.GetString(buf, off, n);
    }

    private static void WriteCString(byte[] buf, int off, int maxLen, string s)
    {
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes(s);
        if (bytes.Length >= maxLen)
            throw new InvalidDataException(
                $"scenario field overflow: \"{s}\" needs {bytes.Length + 1} B, field is {maxLen} B");
        Buffer.BlockCopy(bytes, 0, buf, off, bytes.Length);
        // Remainder stays 0x00 — matches the shipping padding exactly.
    }

    // ---- insignia atlas --------------------------------------------------

    /// <summary>
    /// The insignia sprite atlas geometry, as the engine uses it:
    /// panel_sprite_blit image@0x2569C pushes height 0x0B (image@0x256A3) and
    /// width 0x20 (image@0x256A7) and computes source-x = idx &lt;&lt; 5
    /// (image@0x256BB..0x256C2).  2a.lib::INSIG.PIC is 128x11 = exactly 4
    /// frames, so the index space 0..3 has no slack.
    /// </summary>
    public const int InsigniaFrameWidth = 0x20;   // image@0x256A7
    public const int InsigniaFrameHeight = 0x0B;  // image@0x256A3
    public const int InsigniaFrameCount = 4;      // 128 / 32, atlas-derived

    /// <summary>
    /// Slice one 32x11 frame out of a decoded insignia atlas given as flat
    /// row-major palette indices (see PicFile.PixelIndices()).
    /// Returns a 32*11 index array.
    /// </summary>
    public static byte[] SliceInsigniaFrame(
        byte[] atlasIndices, int atlasWidth, int atlasHeight, int frameIdx)
    {
        int x0 = frameIdx << 5;   // image@0x256C2: shl ax,5
        byte[] frame = new byte[InsigniaFrameWidth * InsigniaFrameHeight];
        for (int y = 0; y < InsigniaFrameHeight; y++)
        {
            if (y >= atlasHeight) break;
            for (int x = 0; x < InsigniaFrameWidth; x++)
            {
                int sx = x0 + x;
                if (sx >= atlasWidth) continue;
                frame[y * InsigniaFrameWidth + x] = atlasIndices[y * atlasWidth + sx];
            }
        }
        return frame;
    }
}
