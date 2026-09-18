using System.Text;
using System.Text.Json;
using CYAC.Port.Core.Data;

namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>
/// Every NUL-terminated literal in the executable's catalogued string zones.
/// </summary>
/// <remarks>
/// <para>
/// The zone list is KNOWLEDGE: it is the <c>data/strings</c> region table, transcribed as
/// addresses, lengths, names and confidence levels.  The literals themselves are read from the
/// image at transform time.  This is the project's second-largest data fog after the meshes, and
/// the point of the document is to make it visible: each zone reports how many of its bytes are
/// inside a recovered literal and carries the rest as counted <c>unknown_spans</c>.
/// </para>
/// <para>
/// Two regions are EXCLUDED and one is carved, because they are mis-boundaried:
/// <c>data_after_atan_lut</c> (<c>0x34385</c>, 5 B) and the head of <c>str_zone_a_tail</c> are in
/// fact the 1,442-byte quarter-period SINE TABLE, which <c>exe/tables/sine_quarter.json</c> already
/// owns.  What remains of <c>str_zone_a_tail</c> after the table is nine bytes, carried here as
/// <c>str_zone_a_tail_post_sine</c>.  Reported for the scanner owner, not fixed here.
/// </para>
/// </remarks>
internal sealed class ExeStringCatalog : IExeTable
{
    /// <summary>The shortest run of printable bytes counted as a literal.</summary>
    public const int MinimumLiteralLength = 2;

    /// <summary>
    /// The <c>data/strings</c> regions: image offset, length, region name and the confidence
    /// records for it.
    /// </summary>
    private static readonly (int Image, int Bytes, string Zone, string Confidence)[] Zones =
    [
        (0x33F60, 0x0024, "str_copyright_ea", "verified"),
        (0x34927, 0x0009, "str_zone_a_tail_post_sine", "hypothesis"),
        (0x34970, 0x0241, "str_zone_a_tail_b", "hypothesis"),
        (0x34BB1, 0x03FE, "str_settings_menu", "verified"),
        (0x34FAF, 0x006B, "str_zone_b_uncatalogued_pre", "hypothesis"),
        (0x3511A, 0x0006, "str_zone_b_uncatalogued_pre_tail", "hypothesis"),
        (0x35170, 0x050E, "str_mentor_and_damage", "verified"),
        (0x3567E, 0x00B2, "str_zone_c_head_pre", "hypothesis"),
        (0x3BD68, 0x0038, "str_msc_rtl_copyright", "verified"),
        (0x3BDA0, 0x008C, "str_zone_d_uncatalogued", "hypothesis"),
        (0x3BE2C, 0x0083, "str_app_errors_and_cheats", "verified"),
        (0x3BEAF, 0x00BF, "str_zone_e_uncatalogued", "hypothesis"),
        (0x3BF6E, 0x004E, "str_font_filenames_a", "verified"),
        (0x3BFBC, 0x02FA, "str_zone_f_head", "hypothesis"),
        (0x3C2B6, 0x0070, "unidentified_data_at_3C2B6", "hypothesis"),
        (0x3C326, 0x00ED, "str_zone_f_tail_a", "hypothesis"),
        (0x3C41D, 0x0006, "str_pnt_extension", "verified"),
        (0x3C423, 0x0713, "str_zone_f_tail_b", "hypothesis"),
        (0x3CB36, 0x0134, "str_dialinit_and_maneuvers", "verified"),
        (0x3CC6A, 0x0078, "str_zone_g_uncatalogued", "hypothesis"),
        (0x3CCE2, 0x007E, "str_scenarios_and_radar", "verified"),
        (0x3CD60, 0x00F0, "str_zone_h_uncatalogued", "hypothesis"),
        (0x3CE50, 0x005C, "str_splash_credits", "verified"),
        (0x3CEAC, 0x03B0, "str_zone_i_uncatalogued", "hypothesis"),
        (0x3D25C, 0x0066, "str_aircraft_display_names", "verified"),
        (0x3D2C2, 0x0868, "str_zone_j_head", "hypothesis"),
        (0x3DB2A, 0x0080, "unidentified_data_at_3DB2A", "hypothesis"),
        (0x3DBAA, 0x0744, "str_zone_j_tail", "hypothesis"),
        (0x3E2EE, 0x0021, "str_factions", "verified"),
        (0x3E30F, 0x0015, "str_zone_k_uncatalogued", "hypothesis"),
        (0x3E324, 0x0417, "str_radio_chatter_corpus", "verified"),
        (0x3E73B, 0x004C, "str_zone_l_uncatalogued", "hypothesis"),
        (0x3E787, 0x01AF, "str_disk_prompts", "verified"),
        (0x3E936, 0x00A8, "g_lib_filename_table", "verified"),
        (0x3E9DF, 0x0211, "str_cinematic_and_fonts_b", "verified"),
        (0x3EBF0, 0x00D8, "str_zone_m_uncatalogued", "hypothesis"),
        (0x3ECC8, 0x045A, "str_cockpit_data_and_formats", "verified"),
        (0x3F122, 0x0122, "str_title_and_audio_filenames", "verified"),
        (0x3F244, 0x0015, "str_zone_o_uncatalogued", "hypothesis"),
        (0x3F259, 0x0097, "str_speech_sample_basenames", "partial"),
        (0x3F2F0, 0x00C7, "str_zone_p_uncatalogued", "hypothesis"),
        (0x3F3B7, 0x00BB, "str_difficulty_and_insignia", "verified"),
        (0x3F472, 0x0254, "str_zone_q_uncatalogued", "hypothesis"),
        (0x3F6C6, 0x00A5, "str_aircraft_datasheet_fmt", "verified"),
        (0x3F76B, 0x0162, "str_zone_r_uncatalogued", "hypothesis"),
        (0x3F8CD, 0x0088, "str_planes_pic_and_envelope", "verified"),
        (0x3F955, 0x070B, "str_zone_s_uncatalogued", "hypothesis"),
        (0x40060, 0x0050, "str_hud_overlay_filenames", "verified"),
        (0x400B0, 0x025E, "str_zone_t_uncatalogued", "hypothesis"),
        (0x4030E, 0x00C2, "str_damage_fmt_audio_tokens", "verified"),
        (0x403D0, 0x0032, "str_zone_u_uncatalogued", "hypothesis"),
        (0x40402, 0x005E, "str_cfg_token_table", "verified"),
        (0x40460, 0x0042, "str_zone_v_uncatalogued", "hypothesis"),
        (0x404A2, 0x002E, "str_cursor_assets", "verified"),
        (0x404D0, 0x0062, "str_zone_w_uncatalogued", "hypothesis"),
        (0x40532, 0x00A9, "str_view_labels", "verified"),
        (0x405DB, 0x0017, "str_zone_x_uncatalogued", "hypothesis"),
        (0x405F2, 0x009F, "str_film_errors", "verified"),
        (0x408F0, 0x0060, "str_film_replay_ui", "verified"),
        (0x409F4, 0x0012, "str_create_mission_fragments", "verified"),   // The seven Create Mission sentence fragments (round22c §4.1), carved from mesh_residue_0x40950
        (0x46DAB, 0x00AD, "str_msc_nmsg_block_a", "verified"),
        (0x46E58, 0x065B, "str_zone_z_uncatalogued", "hypothesis"),
        (0x474B3, 0x00AD, "str_msc_nmsg_block_b", "verified"),
    ];

    /// <inheritdoc/>
    public string TreePath => "exe/strings.json";

    /// <inheritdoc/>
    public string Description =>
        "every NUL-terminated literal in the executable's catalogued string zones, with the zone it " +
        "belongs to and how much of that zone is NOT text";

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        List<ExeStringZoneDto> zones = new List<ExeStringZoneDto>(Zones.Length);
        int unknown = 0;
        int strings = 0;

        foreach ((int start, int length, string zone, string confidence) in Zones)
        {
            ReadOnlySpan<byte> region = image.Slice(start, length);
            List<ExeStringDto> literals = new List<ExeStringDto>();
            List<ByteSpanDto> spans = new List<ByteSpanDto>();
            int textBytes = 0;
            int spanStart = -1;

            for (int at = 0; at < region.Length;)
            {
                int run = at;
                while (run < region.Length && IsText(region[run]))
                {
                    run++;
                }

                if (run - at >= MinimumLiteralLength)
                {
                    if (spanStart >= 0)
                    {
                        spans.Add(Span(spanStart, region[spanStart..at]));
                        spanStart = -1;
                    }

                    // The usual case is a NUL-terminated literal, and swallowing the NUL keeps the
                    // burn-down honest (a terminator is not an unexplained byte).  But plenty of the
                    // original's literals end on a control byte instead — the settings menu uses
                    // 0x01/0x15 as column separators — and the MS C run-time copyright fills its
                    // zone with no terminator at all, so the terminator is optional and recorded.
                    bool terminated = run < region.Length && region[run] == 0;
                    literals.Add(new ExeStringDto
                    {
                        Image = PortHex.Format(start + at, 5),
                        Dgroup = start >= ExeAddresses.DgroupImageBase
                            ? PortHex.Format(start + at - ExeAddresses.DgroupImageBase)
                            : null,
                        Text = Encoding.ASCII.GetString(region[at..run]),
                        Unterminated = terminated ? null : true,
                    });
                    textBytes += run - at + (terminated ? 1 : 0);
                    at = terminated ? run + 1 : run;
                    continue;
                }

                if (spanStart < 0)
                {
                    spanStart = at;
                }

                at++;
            }

            if (spanStart >= 0)
            {
                spans.Add(Span(spanStart, region[spanStart..]));
            }

            int spanBytes = spans.Sum(s => s.Hex.Length / 2);
            unknown += spanBytes;
            strings += literals.Count;
            zones.Add(new ExeStringZoneDto
            {
                Zone = zone,
                Image = PortHex.Format(start, 5),
                Bytes = length,
                ZoneConfidence = confidence,
                TextBytes = textBytes,
                Strings = literals,
                UnknownSpans = spans.Count == 0 ? null : spans,
            });
        }

        ExeStringCatalogDto dto = new ExeStringCatalogDto
        {
            Format = "cyac.exeStrings/1",
            About =
                "The literals the executable itself carries - file names, printf formats, cockpit " +
                "messages, radio chatter, menu labels. The zone list is the data/strings region " +
                "table, and the confidence per zone is that map's own, " +
                "so a hypothesis zone is one nobody has walked instruction by instruction. " +
                "A literal here is a maximal run of at least two printable ASCII bytes, plus its NUL " +
                "when it has one (many do not: the settings menu separates columns with 0x01/0x15 " +
                "and the MS C run-time copyright fills its zone exactly, so `unterminated` is a " +
                "per-literal fact). Everything else in a zone is carried verbatim in unknown_spans " +
                "and counted, which is what makes the remaining fog measurable rather than assumed - " +
                "and it shows immediately that the `verified` zones really are text while several " +
                "`hypothesis` ones are almost entirely something else. " +
                "EXCLUSION:'s data_after_atan_lut (image@0x34385) and the head of " +
                "str_zone_a_tail are not strings at all - they are the quarter-period sine table, " +
                "which exe/tables/sine_quarter.json owns; only the nine bytes after it are kept here.",
            DgroupImageBase = PortHex.Format(ExeAddresses.DgroupImageBase, 5),
            StringCount = strings,
            Zones = zones,
        };

        int zoneBytes = Zones.Sum(z => z.Bytes);
        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.ExeStringCatalogDto),
            unknown,
            $"strings: {strings} literals in {zones.Count} zones, " +
            $"{zoneBytes - unknown}/{zoneBytes} B are text");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        ExeStringCatalogDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.ExeStringCatalogDto)
                                  ?? throw new InvalidDataException("the string-catalogue document is empty");
        if (dto.Zones is not { } zones || zones.Count != Zones.Length)
        {
            throw new InvalidDataException(
                $"expected {Zones.Length} string zones, found {dto.Zones?.Count ?? 0}");
        }

        List<ExeSlice> slices = new List<ExeSlice>(zones.Count);
        foreach (ExeStringZoneDto zone in zones)
        {
            int start = PortHex.Parse(zone.Image);
            byte[] region = new byte[zone.Bytes];

            foreach (ExeStringDto literal in zone.Strings ?? [])
            {
                int at = PortHex.Parse(literal.Image) - start;
                byte[] text = Encoding.ASCII.GetBytes(literal.Text ?? string.Empty);
                if (at < 0 || at + text.Length > region.Length)
                {
                    throw new InvalidDataException(
                        $"zone {zone.Zone}: the literal at {literal.Image} does not fit");
                }

                text.CopyTo(region.AsSpan(at));
            }

            foreach (ByteSpanDto span in zone.UnknownSpans ?? [])
            {
                byte[] bytes = Convert.FromHexString(span.Hex);
                int at = PortHex.Parse(span.Offset);
                if (at < 0 || at + bytes.Length > region.Length)
                {
                    throw new InvalidDataException(
                        $"zone {zone.Zone}: the span at {span.Offset} does not fit");
                }

                bytes.CopyTo(region.AsSpan(at));
            }

            slices.Add(new ExeSlice($"string zone {zone.Zone}", start, region));
        }

        return slices;
    }

    private static ByteSpanDto Span(int offset, ReadOnlySpan<byte> bytes) =>
        new(PortHex.Format(offset, 4), Convert.ToHexString(bytes));

    // The original's own text is 7-bit ASCII throughout; a byte outside it is data, not a character.
    private static bool IsText(byte value) =>
        value is >= 0x20 and < 0x7F or 0x09 or 0x0A or 0x0D;
}
