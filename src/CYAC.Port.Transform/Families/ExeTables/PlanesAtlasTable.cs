using System.Text.Json;
using System.Text.Json.Serialization;
using CYAC.Port.Core.Data;
using CYAC.Port.Transform.Json;

namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>
/// The HANGAR's side-view sheet table: which row of <c>planes0/1.pic</c> an aircraft's silhouette is
/// (<c>exe/tables/planes_atlas.json</c>).
/// </summary>
/// <remarks>
/// <para>
/// <c>plane_silhouette_draw @image@0x271E7</c> walks a table of five-byte
/// <c>s_planes_atlas_record</c>s at <b><c>image@0x35120</c></b> (segment <c>0x4512:0</c>, a
/// non-DGROUP near-data segment the MSC linker placed statically — it is NOT a
/// loaded asset), comparing <c>lookup_key</c> at <c>+0x03</c> against the key it was passed
/// (<c>image@0x271FD</c>) and stopping at the terminator record whose key is zero
/// (<c>image@0x27206</c>).  Fourteen records, one per encyclopedia page.
/// </para>
/// <para>
/// <b>What <c>key</c> is — resolved here, from the bytes.</b>  It is the aircraft's
/// <b>engagement-prototype DGROUP pointer</b>: its 46-byte stat block, the same word
/// <c>exe/tables/aircraft_classes.json</c> publishes as an entry's <c>value</c> and
/// <c>exe/tables/engagement.json</c> as a prototype's <c>dgroup</c>.  The hangar reaches it by
/// calling <c>aircraft_class_table_lookup @image@0x24058</c> with <c>pi.bin +0x00</c> (the class id)
/// and passing the returned pointer straight down as the lookup key
/// (<c>image@0x26643</c> → <c>[bp-0x10]</c> → <c>image@0x26A72</c>), which is why the earlier reading's
/// "silhouette key at <c>pi.bin +0x22</c>" was refuted: <c>+0x22</c> is the Y-centring offset.
/// All fourteen keys resolve: <c>0x163C</c> P-47D … <c>0x2470</c> MiG-21MF.
/// </para>
/// <para>
/// <b>And the rows are NOT in <c>pi.json</c> order.</b>  The seven variant-0 rows happen to be
/// <c>pi.json</c> 0..6 (P-47D, P-51D, FW-190A, Me-109E, Me-110B, Me-163B, Me-262A), but the seven
/// variant-1 rows are MiG-17F, F-86E, Yak-9, MiG-15, F-4E, MiG-21MF, F-105D — <c>pi.json</c>
/// 10, 8, 7, 9, 11, 13, 12.  A reader that assumed the index would draw the wrong aeroplane for
/// five of the fourteen; the key has to be resolved.  <c>_aircraft</c> is that resolution, derived
/// and ignored on the way back in.
/// </para>
/// <para>
/// <c>topY</c> is the row's first line in the 112 × 200 portrait sheet and <c>height</c> its line
/// count; the blit is the FULL 112-pixel sheet width (<c>image@0x2727D: mov ax,0x70</c>).  The field
/// calls <c>src_x_u8</c> is that <c>topY</c> — the atlas is portrait and the blit's axis names are
/// transposed.
/// </para>
/// </remarks>
internal sealed class PlanesAtlasExeTable : IExeTable
{
    /// <summary>The document's path in the data tree.</summary>
    public const string Path = "exe/tables/planes_atlas.json";

    /// <summary>Where the record table starts in the unpacked layer-1 image.</summary>
    public const int TableImageOffset = 0x35120;

    /// <summary>Bytes in one <c>s_planes_atlas_record</c>.</summary>
    public const int RecordStride = 5;

    /// <summary>How many silhouettes the sheets carry — one per encyclopedia page.</summary>
    public const int RecordCount = 14;

    /// <summary>The full sheet width every silhouette is blitted at (<c>image@0x2727D</c>).</summary>
    public const int SheetWidth = 0x70;

    /// <inheritdoc/>
    public string TreePath => Path;

    /// <inheritdoc/>
    public string Description =>
        "the hangar's side-view sheet table: which row of planes0/1.pic each aircraft's silhouette "
            + "is, keyed by its engagement-prototype pointer";

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        IReadOnlyDictionary<ushort, string> names = EngagementExeTable.PrototypeNames(image);
        List<PlanesAtlasRecordDto> rows = new List<PlanesAtlasRecordDto>(RecordCount);
        for (int i = 0; i < RecordCount; i++)
        {
            int at = TableImageOffset + (i * RecordStride);
            int key = image[at + 3] | (image[at + 4] << 8);
            if (key == 0)
            {
                throw new InvalidDataException(
                    $"the planes atlas table terminates after {i} records at image@0x{at:X5}; "
                        + $"{RecordCount} were expected");
            }

            rows.Add(new PlanesAtlasRecordDto
            {
                Index = i,
                Variant = image[at],
                TopY = image[at + 1],
                Height = image[at + 2],
                Key = PortHex.Format(key, 4),
                ImageOffset = PortHex.Format(at, 5),
                Aircraft = names.TryGetValue((ushort)key, out string? name)
                    ? name
                    : throw new InvalidDataException(
                        $"the planes atlas row at image@0x{at:X5} keys on [0x{key:X4}], which is not "
                            + "an engagement prototype"),
            });
        }

        int terminator = TableImageOffset + (RecordCount * RecordStride);
        int terminatorKey = image[terminator + 3] | (image[terminator + 4] << 8);
        if (terminatorKey != 0)
        {
            throw new InvalidDataException(
                $"the planes atlas table has no terminator at image@0x{terminator:X5}: its key is "
                    + $"0x{terminatorKey:X4}");
        }

        PlanesAtlasDto dto = new PlanesAtlasDto
        {
            Format = "cyac.table.planesAtlas/1",
            About =
                "The HANGAR's side-view sheets. plane_silhouette_draw @image@0x271E7 walks these "
                + "five-byte s_planes_atlas_record rows at image@0x35120 (segment 0x4512:0, placed "
                + "statically by the linker - not a loaded asset) and takes the one whose "
                + "`key` matches the aircraft it was handed, stopping at the zero-key terminator. "
                + "`key` is the aircraft's ENGAGEMENT-PROTOTYPE DGROUP pointer - its 46-byte stat "
                + "block, the same word aircraft_classes.json publishes as an entry's `value` and "
                + "engagement.json as a prototype's `dgroup`; the hangar gets it by calling "
                + "aircraft_class_table_lookup @image@0x24058 with pi.bin +0x00 and passing the "
                + "result down (image@0x26643 -> image@0x26A72). `variant` selects the sheet "
                + "(0 = planes0.pic, 1 = planes1.pic), `topY` is the row's first line in the "
                + "112x200 portrait sheet and `height` its line count; the blit is the full "
                + "112-pixel sheet width (image@0x2727D). NOTE the rows are NOT in pi.json order: "
                + "the variant-1 seven run MiG-17F, F-86E, Yak-9, MiG-15, F-4E, MiG-21MF, F-105D "
                + "= pi.json 10, 8, 7, 9, 11, 13, 12, so the key must be resolved rather than the "
                + "index assumed. `_aircraft` is that resolution and is ignored on import.",
            TableImageOffset = PortHex.Format(TableImageOffset, 5),
            RecordBytes = RecordStride,
            SheetWidth = SheetWidth,
            Records = rows,
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.PlanesAtlasDto),
            0,
            $"planes_atlas: {rows.Count} silhouette rows over 2 sheets");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        PlanesAtlasDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.PlanesAtlasDto)
                             ?? throw new InvalidDataException("the planes-atlas document is empty");
        List<PlanesAtlasRecordDto> rows = dto.Records ?? [];
        if (rows.Count != RecordCount)
        {
            throw new InvalidDataException(
                $"expected {RecordCount} silhouette rows, found {rows.Count}");
        }

        // The fourteen records are contiguous, so one slice rebuilds the whole run.  The terminator
        // is NOT rebuilt: it is five zero bytes the linker emitted and no field of this document
        // describes, so it stays outside the region the verifier diffs.
        byte[] bytes = new byte[RecordCount * RecordStride];
        for (int i = 0; i < rows.Count; i++)
        {
            PlanesAtlasRecordDto row = rows[i];
            int at = i * RecordStride;
            bytes[at] = checked((byte)row.Variant);
            bytes[at + 1] = checked((byte)row.TopY);
            bytes[at + 2] = checked((byte)row.Height);
            int key = PortHex.Parse(row.Key);
            bytes[at + 3] = (byte)key;
            bytes[at + 4] = (byte)(key >> 8);
        }

        return [new ExeSlice("planes atlas record table", TableImageOffset, bytes)];
    }
}

/// <summary><c>exe/tables/planes_atlas.json</c>: the hangar's fourteen side-view rows.</summary>
public sealed class PlanesAtlasDto
{
    /// <summary>The document's schema tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is, in prose.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>Where the record table starts in the unpacked image.</summary>
    [JsonPropertyName("tableImageOffset")]
    public string? TableImageOffset { get; init; }

    /// <summary>Bytes in one record.</summary>
    [JsonPropertyName("recordBytes")]
    public int RecordBytes { get; init; }

    /// <summary>The sheet width every silhouette is blitted at.</summary>
    [JsonPropertyName("sheetWidth")]
    public int SheetWidth { get; init; }

    /// <summary>The rows, in table order (which is sheet order, not <c>pi.json</c> order).</summary>
    [JsonPropertyName("records")]
    public List<PlanesAtlasRecordDto>? Records { get; init; }
}

/// <summary>One silhouette row.</summary>
public sealed class PlanesAtlasRecordDto
{
    /// <summary>Its position in the table, 0..13.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>Field <c>+0x00</c>: 0 = <c>planes0.pic</c>, 1 = <c>planes1.pic</c>.</summary>
    [JsonPropertyName("variant")]
    public int Variant { get; init; }

    /// <summary>Field <c>+0x01</c>: the row's first line in the 112 × 200 sheet.</summary>
    [JsonPropertyName("topY")]
    public int TopY { get; init; }

    /// <summary>Field <c>+0x02</c>: how many lines the row is.</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>
    /// Field <c>+0x03</c>: the aircraft's engagement-prototype DGROUP pointer — the match key.
    /// </summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>Where the record starts in the unpacked image.</summary>
    [JsonPropertyName("imageOffset")]
    public string? ImageOffset { get; init; }

    /// <summary>Derived: which aircraft <see cref="Key"/> resolves to.  Ignored on import.</summary>
    [JsonPropertyName("_aircraft")]
    public string? Aircraft { get; init; }
}
