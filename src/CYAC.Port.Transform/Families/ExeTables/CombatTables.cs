using System.Buffers.Binary;
using System.Text.Json;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Model.Flight;

namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>
/// The six per-aircraft player-damage weight tables plus the pointer array that selects one.
/// </summary>
/// <remarks>
/// <para>
/// The six 0x32-byte tables at DGROUP <c>0x446A..0x4595</c> and the six-entry pointer array at
/// <c>0x4596</c> are contiguous, so the whole block is one 312-byte round trip.
/// </para>
/// <para>
/// Measured while extracting: the 25 <b>low</b> bytes of every table sum to exactly 100, which is the
/// roulette's roll range (<c>prng_rand_bounded(0x64)</c> @<c>image@0x0F836</c>) — the weights are a
/// complete probability distribution, not an open-ended cumulative scan.  The odd bytes are a
/// parallel column the walk never reads (it advances by two and reads only the even byte); they are
/// carried as <c>unknown_0x01</c> and counted.
/// </para>
/// </remarks>
internal sealed class PlayerDamageExeTable : IExeTable
{
    /// <summary>DGROUP offset of the first aircraft's weight table.</summary>
    public const int TablesDgroup = 0x446A;

    /// <summary>DGROUP offset of the six-entry pointer array, immediately after the tables.</summary>
    public const int PointerArrayDgroup = PlayerDamageTable.TablePointerArrayDgroupOffset;

    /// <inheritdoc/>
    public string TreePath => "exe/tables/player_damage.json";

    /// <inheritdoc/>
    public string Description =>
        "the six per-aircraft damage-effect weight tables (25 stride-2 entries each) and the pointer " +
        "array engagement_state_init selects one with";

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        IReadOnlyList<string> basenames = AircraftDefinition.FlyableBasenames;
        List<string> pointers = new List<string>(PlayerDamageTable.FlyableAircraftCount);
        List<PlayerDamageTableDto> tables = new List<PlayerDamageTableDto>(PlayerDamageTable.FlyableAircraftCount);
        int unknown = 0;

        for (int aircraft = 0; aircraft < PlayerDamageTable.FlyableAircraftCount; aircraft++)
        {
            ushort pointer = BinaryPrimitives.ReadUInt16LittleEndian(
                image.Slice(ExeAddresses.Image(PointerArrayDgroup) + (aircraft * 2), 2));
            pointers.Add(PortHex.Format(pointer));

            List<PlayerDamageWeightDto> weights = new List<PlayerDamageWeightDto>(PlayerDamageTable.WeightEntries);
            for (int entry = 0; entry < PlayerDamageTable.WeightEntries; entry++)
            {
                int at = ExeAddresses.Image(pointer) + (entry * PlayerDamageTable.WeightStrideBytes);
                weights.Add(new PlayerDamageWeightDto
                {
                    Index = entry,
                    Effect = EffectName(entry),
                    Weight = image[at],
                    Unknown0x01 = PortHex.Bytes(image.Slice(at + 1, 1)),
                });
                unknown++;
            }

            tables.Add(new PlayerDamageTableDto
            {
                Aircraft = basenames[aircraft],
                Index = aircraft,
                Dgroup = PortHex.Format(pointer),
                Weights = weights,
            });
        }

        PlayerDamageTablesDto dto = new PlayerDamageTablesDto
        {
            Format = "cyac.table.playerDamage/1",
            About =
                "Which system breaks when the player's aircraft is hit. " +
                "engagement_state_init @0x0F6BC points g_damage_effect_weight_table_ptr [0xBD0E] (ex-g_weapon_slot_table_ptr, renamed U3) at the active " +
                "aircraft's table (from the pointer array below), and weapon_fire_combat_loop " +
                "@0x0F748 walks it: it reads a BYTE weight but advances the cursor by TWO " +
                "(image@0x0F811..0x0F821), accumulating until the running total passes " +
                "prng_rand_bounded(100). The chosen entry's index is both the damage-effect index and " +
                "the index into the per-effect hit counters. Entry 24 has no dispatch arm " +
                "(cmp ax,0x17; ja @image@0x0FBC8), so weighting it gives a hit a chance of doing " +
                "nothing at all - the FW-190 is the only shipped aircraft that uses it. " +
                "MEASURED HERE: every table's 25 weights sum to exactly 100, i.e. the roll range. " +
                "The odd byte of each entry is a parallel column with no identified reader.",
            Source = new DataSourceDto
            {
                Image = PortHex.Format(ExeAddresses.Image(TablesDgroup), 5),
                Dgroup = PortHex.Format(TablesDgroup),
                Bytes = (PlayerDamageTable.FlyableAircraftCount * PlayerDamageTable.WeightTableBytes)
                    + (PlayerDamageTable.FlyableAircraftCount * 2),
                Entries = PlayerDamageTable.FlyableAircraftCount,
                ElementType = "u8[50] weight table",
            },
            RollRange = PlayerDamageTable.RollRange,
            PointerArray = pointers,
            Tables = tables,
        };

        int[] sums = [.. tables.Select(t => t.Weights!.Sum(w => w.Weight))];
        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.PlayerDamageTablesDto),
            unknown,
            $"player_damage: {tables.Count} tables x {PlayerDamageTable.WeightEntries} weights, " +
            $"sums {string.Join('/', sums)}");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        PlayerDamageTablesDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.PlayerDamageTablesDto)
                                    ?? throw new InvalidDataException("the player-damage document is empty");
        if (dto.Tables is not { } tables || tables.Count != PlayerDamageTable.FlyableAircraftCount)
        {
            throw new InvalidDataException(
                $"expected {PlayerDamageTable.FlyableAircraftCount} damage tables, found " +
                $"{dto.Tables?.Count ?? 0}");
        }

        if (dto.PointerArray is not { } pointers || pointers.Count != PlayerDamageTable.FlyableAircraftCount)
        {
            throw new InvalidDataException(
                $"expected {PlayerDamageTable.FlyableAircraftCount} table pointers, found " +
                $"{dto.PointerArray?.Count ?? 0}");
        }

        List<ExeSlice> slices = new List<ExeSlice>(tables.Count + 1);
        foreach (PlayerDamageTableDto table in tables)
        {
            if (table.Weights is not { } weights || weights.Count != PlayerDamageTable.WeightEntries)
            {
                throw new InvalidDataException(
                    $"aircraft '{table.Aircraft}': expected {PlayerDamageTable.WeightEntries} " +
                    $"weights, found {table.Weights?.Count ?? 0}");
            }

            byte[] bytes = new byte[PlayerDamageTable.WeightTableBytes];
            for (int i = 0; i < weights.Count; i++)
            {
                bytes[i * PlayerDamageTable.WeightStrideBytes] = (byte)weights[i].Weight;
                bytes[(i * PlayerDamageTable.WeightStrideBytes) + 1] =
                    PortHex.BytesOrZero(weights[i].Unknown0x01, 1)[0];
            }

            slices.Add(new ExeSlice(
                $"{table.Aircraft} damage weights",
                ExeAddresses.Image(PortHex.Parse(table.Dgroup)),
                bytes));
        }

        byte[] pointerBytes = new byte[pointers.Count * 2];
        for (int i = 0; i < pointers.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                pointerBytes.AsSpan(i * 2, 2), (ushort)PortHex.Parse(pointers[i]));
        }

        slices.Add(new ExeSlice(
            "damage-table pointer array", ExeAddresses.Image(PointerArrayDgroup), pointerBytes));
        return slices;
    }

    private static string EffectName(int index) =>
        index < PlayerDamageTable.EffectCount
            ? ((PlayerDamageEffect)index).ToString()
            : "none";
}

/// <summary>
/// The 20-record film-review widget table, <c>g_film_review_widget_table [0x49D8]</c>.
/// </summary>
internal sealed class FilmReviewWidgetExeTable : IExeTable
{
    /// <summary>The table's DGROUP offset.</summary>
    public const int Dgroup = 0x49D8;

    /// <summary>Records in the table.</summary>
    public const int Records = 20;

    /// <summary>Bytes per record — the original <c>s_ui_widget</c>.</summary>
    public const int RecordBytes = 0x16;

    /// <inheritdoc/>
    public string TreePath => "exe/tables/film_review_widgets.json";

    /// <inheritdoc/>
    public string Description =>
        "the 20 widgets of the film-review control panel: ten VCR transport buttons, six labelled " +
        "buttons and four view-panel hit rectangles";

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        List<UiWidgetDto> widgets = new List<UiWidgetDto>(Records);
        for (int i = 0; i < Records; i++)
        {
            int dgroup = Dgroup + (i * RecordBytes);
            ReadOnlySpan<byte> record = image.Slice(ExeAddresses.Image(dgroup), RecordBytes);
            ushort drawOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x02..]);
            ushort drawSegment = BinaryPrimitives.ReadUInt16LittleEndian(record[0x04..]);

            widgets.Add(new UiWidgetDto
            {
                Record = i,
                Dgroup = PortHex.Format(dgroup),
                Id = record[0x00],
                KeyShortcut = PortHex.Format(record[0x01], 2),
                CustomDrawFn = FarPointer(drawOffset, drawSegment),
                LabelStringRef = BinaryPrimitives.ReadInt16LittleEndian(record[0x06..]),
                Rect =
                [
                    BinaryPrimitives.ReadUInt16LittleEndian(record[0x08..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(record[0x0A..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(record[0x0C..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(record[0x0E..]),
                ],
                KeyShortcutAltAscii = PortHex.Format(record[0x10], 2),
                KeyShortcutAltScancode = PortHex.Format(record[0x11], 2),
                Flags = PortHex.Format(record[0x12], 2),
                HoverPrevious = record[0x13],
                HoverCurrent = record[0x14],
                RenderState = record[0x15],
            });
        }

        UiWidgetTableDto dto = new UiWidgetTableDto
        {
            Format = "cyac.table.uiWidgets/1",
            About =
                "ui_film_review_screen registers these with widget_list_init(base=0x49D8, count=0x14) " +
                "(image@0x328A9 / 0x32915 / 0x3315D). Records 0-9 are the two rows of 13x11-px VCR " +
                "transport buttons, 10-15 the labelled IN/OUT/NEXT TARGET/LOAD/SAVE/EXIT buttons " +
                "(labels at [0x4916]) and 16-19 four large view-panel hit rectangles. Every record's " +
                "custom-draw far pointer is film_review_button_draw @0x326B6, whose segment word is " +
                "MZ-relocation-patched in all 20; custom draw fires iff that segment is non-zero AND " +
                "flags bit2 is clear (round-41 input consolidation). +0x13/+0x14/+0x15 are runtime " +
                "state and ship as zero. Field names: KNOWN_FIELDS[\"s_ui_widget\"].",
            Source = new DataSourceDto
            {
                Image = PortHex.Format(ExeAddresses.Image(Dgroup), 5),
                Dgroup = PortHex.Format(Dgroup),
                Bytes = Records * RecordBytes,
                Entries = Records,
                ElementType = "s_ui_widget",
            },
            Widgets = widgets,
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.UiWidgetTableDto),
            0,
            $"film_review_widgets: {widgets.Count} s_ui_widget records");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        UiWidgetTableDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.UiWidgetTableDto)
                               ?? throw new InvalidDataException("the widget-table document is empty");
        if (dto.Widgets is not { } widgets || widgets.Count != Records)
        {
            throw new InvalidDataException(
                $"expected {Records} widget records, found {dto.Widgets?.Count ?? 0}");
        }

        byte[] bytes = new byte[Records * RecordBytes];
        for (int i = 0; i < widgets.Count; i++)
        {
            UiWidgetDto widget = widgets[i];
            Span<byte> record = bytes.AsSpan(i * RecordBytes, RecordBytes);
            if (widget.Rect is not { Count: 4 } rect)
            {
                throw new InvalidDataException($"widget {i} must carry a 4-word rectangle");
            }

            record[0x00] = (byte)widget.Id;
            record[0x01] = (byte)PortHex.Parse(widget.KeyShortcut);
            BinaryPrimitives.WriteUInt16LittleEndian(
                record[0x02..], (ushort)PortHex.ParseOrDefault(widget.CustomDrawFn?.Offset));
            BinaryPrimitives.WriteUInt16LittleEndian(
                record[0x04..], (ushort)PortHex.ParseOrDefault(widget.CustomDrawFn?.Segment));
            BinaryPrimitives.WriteInt16LittleEndian(record[0x06..], (short)widget.LabelStringRef);
            for (int k = 0; k < 4; k++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(record[(0x08 + (k * 2))..], (ushort)rect[k]);
            }

            record[0x10] = (byte)PortHex.Parse(widget.KeyShortcutAltAscii);
            record[0x11] = (byte)PortHex.Parse(widget.KeyShortcutAltScancode);
            record[0x12] = (byte)PortHex.Parse(widget.Flags);
            record[0x13] = (byte)widget.HoverPrevious;
            record[0x14] = (byte)widget.HoverCurrent;
            record[0x15] = (byte)widget.RenderState;
        }

        return [new ExeSlice("film-review widget table", ExeAddresses.Image(Dgroup), bytes)];
    }

    internal static FarPointerDto FarPointer(ushort offset, ushort segment)
    {
        int resolved = ExeAddresses.Resolve(offset, segment);
        return new FarPointerDto
        {
            Offset = PortHex.Format(offset),
            Segment = PortHex.Format(segment),
            ImageOffset = resolved == 0 ? null : PortHex.Format(resolved, 5),
        };
    }
}
