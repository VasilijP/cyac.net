using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CYAC.Port.Core.Data;
using CYAC.Port.Transform.Json;

namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>
/// The in-flight ESC menu bar's own text: six menus, six titles and fifty-four item records
/// (<c>exe/tables/flight_menus.json</c>).
/// </summary>
/// <remarks>
/// <list type="number">
///   <item><b>The titles blob</b> <c>g_menu_titles_blob [0x1030]</c> (<c>image@0x3CD90</c>) —
///   <c>?</c>, <c>System</c>, <c>View</c>, <c>Graphics</c>, <c>Help</c>, <c>Location</c>, each
///   NUL-terminated.</item>
///   <item><b>The descriptor table</b> <c>g_menu_descriptor_table [0x1060]</c>
///   (<c>image@0x3CDC0</c>) — six 24-byte <c>s_menu_descriptor</c> records whose first sixteen
///   bytes are runtime geometry (zero in the shipped image) and whose last eight are the static
///   join: <c>+0x10</c> title near pointer, <c>+0x12/+0x14</c> the items far pointer
///   (<c>0x44BB:off</c>, i.e. <c>image@0x34BB0 + off</c>), <c>+0x16</c> the item count.  Only the
///   eight static bytes of each record are published and rebuilt; the sixteen runtime ones are not
///   this document's.</item>
///   <item><b>The items blob</b> <c>g_menu_items_blob [image@0x34BB0]</c> — 850 contiguous bytes,
///   one record per item: <c>[marker 0x15|0x16] [label] [0x01 accelerator]? [*]? [0x00]</c>.  The
///   marker is a FONT GLYPH, not a flag: <c>propbold.fnt</c>'s codepoint 0x15 is an 8-pixel blank
///   and 0x16 the 8-pixel check mark, which is why the popup's left gutter is exactly 8 design
///   pixels wide.  A trailing <c>*</c> means "last item of this section"; a <c>\x14</c> inside a
///   label is the font's right-arrow glyph (View's "Player → Target").</item>
/// </list>
/// <para>
/// What is NOT here is the item → EFFECT map (35 items synthesise a BIOS scancode,
/// the rest toggle flags or call a setter).  That is knowledge, not data, so it lives in the port's
/// own code keyed by the <c>id</c> this document publishes — exactly the split the transform plan's
/// the no-original-data rule asks for.
/// </para>
/// </remarks>
internal sealed class FlightMenuExeTable : IExeTable
{
    /// <summary>The document's path in the data tree.</summary>
    public const string Path = "exe/tables/flight_menus.json";

    /// <summary>How many menus the bar carries.</summary>
    public const int MenuCount = 6;

    /// <summary><c>g_menu_titles_blob [0x1030]</c> — the six NUL-terminated titles.</summary>
    public const int TitlesBlob = 0x1030;

    /// <summary><c>g_menu_descriptor_table [0x1060]</c> — six <c>s_menu_descriptor</c> records.</summary>
    public const int DescriptorTable = 0x1060;

    /// <summary>Bytes in one <c>s_menu_descriptor</c>.</summary>
    public const int DescriptorStride = 0x18;

    /// <summary>The first published field of a descriptor: <c>+0x10</c>, the title near pointer.</summary>
    public const int DescriptorStaticOffset = 0x10;

    /// <summary>How many bytes of a descriptor are static and published (<c>+0x10..+0x17</c>).</summary>
    public const int DescriptorStaticBytes = 8;

    /// <summary>The items blob's own image offset — the far pointers all resolve inside it.</summary>
    public const int ItemsBlobImageOffset = 0x34BB0;

    /// <summary>The record marker that draws the blank gutter glyph (unchecked).</summary>
    public const byte MarkerUnchecked = 0x15;

    /// <summary>The record marker that draws the check-mark glyph.</summary>
    public const byte MarkerChecked = 0x16;

    /// <summary>The byte that separates a label from its accelerator text.</summary>
    public const byte AcceleratorSeparator = 0x01;

    /// <summary>The font's right-arrow glyph, used inside two View labels.</summary>
    public const byte ArrowGlyph = 0x14;

    /// <summary>The trailing byte that marks the last item of a section.</summary>
    public const byte SectionEndMark = (byte)'*';

    /// <summary>
    /// The three lines <c>ui_about_yeager_dialog @image@0x219C4</c> prints — the <c>?</c> menu's
    /// "About Yeager..." box — at DGROUP <c>[0x10F0]</c>, <c>[0x110C]</c> and <c>[0x112C]</c>.
    /// </summary>
    /// <remarks>
    /// They are published HERE, with the menu that opens them, because two of them carry font glyph
    /// bytes (0x12 after the title, 0x11 for the copyright sign) that stop
    /// <c>exe/strings.json</c>'s printable-ASCII walk half way: that catalogue records them as
    /// "unterminated" and loses the rest of the line, so it cannot be the port's source for them.
    /// </remarks>
    public static readonly int[] AboutLines = [0x10F0, 0x110C, 0x112C];

    /// <inheritdoc/>
    public string TreePath => Path;

    /// <inheritdoc/>
    public string Description =>
        "the in-flight ESC menu bar: six menu titles and their fifty-four item records, with each "
            + "item's accelerator text, section end and dispatch id";

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        List<FlightMenuDto> menus = new List<FlightMenuDto>(MenuCount);
        int items = 0;
        foreach (MenuDescriptor record in Descriptors(image))
        {
            List<FlightMenuItemDto> rows = new List<FlightMenuItemDto>(record.ItemCount);
            int at = record.ItemsImageOffset;
            for (int i = 0; i < record.ItemCount; i++)
            {
                (FlightMenuItemDto item, int next) = ReadItem(image, at, record.Index, i);
                rows.Add(item);
                at = next;
            }

            items += rows.Count;
            menus.Add(new FlightMenuDto
            {
                Index = record.Index,
                Title = record.Title,
                Dgroup = PortHex.Format(record.Dgroup, 4),
                TitlePointer = PortHex.Format(record.TitlePointer, 4),
                ItemsSegment = PortHex.Format(record.ItemsSegment, 4),
                ItemsOffset = PortHex.Format(record.ItemsOffset, 4),
                ItemsImageOffset = PortHex.Format(record.ItemsImageOffset, 5),
                Items = rows,
            });
        }

        List<FlightMenuTextDto> about = new List<FlightMenuTextDto>(AboutLines.Length);
        foreach (int dgroup in AboutLines)
        {
            about.Add(new FlightMenuTextDto
            {
                Dgroup = PortHex.Format(dgroup, 4),
                ImageOffset = PortHex.Format(ExeAddresses.Image(dgroup), 5),
                Text = AsciiZ(image, ExeAddresses.Image(dgroup)),
            });
        }

        FlightMenusDto dto = new FlightMenusDto
        {
            Format = "cyac.table.flightMenus/1",
            About =
                "The in-flight pull-down menu bar ESC opens during a sortie. `menus` is "
                + "g_menu_descriptor_table [0x1060], six 24-byte s_menu_descriptor records: only "
                + "the static half (+0x10 title near pointer, +0x12/+0x14 the items far pointer, "
                + "+0x16 the item count) is published, because +0x00..+0x0F is popup geometry the "
                + "engine fills at runtime and is zero in the shipped image. Each item's bytes are "
                + "[marker][label][0x01 accel]?[*]?[0x00] in g_menu_items_blob @image@0x34BB0. The "
                + "MARKER is a font glyph and not a flag: propbold.fnt codepoint 0x15 is an "
                + "8-pixel blank and 0x16 the 8-pixel check mark, which is why the popup's left "
                + "gutter is 8 design pixels wide; every shipped record stores 0x15, and the "
                + "checked state is written at runtime by menu_item_set_checkmark @image@0x21335. "
                + "`sectionEnd` is the trailing '*' (a horizontal rule follows the item); "
                + "`arrowGlyph` says the label contains U+0014, the font's right arrow, which the "
                + "two 'Player -> Target' rows use. `id` is (menu << 8) | item, the value "
                + "menu_bar_engine @image@0x20D04 returns and the 53-entry dispatch ladder at "
                + "image@0x2186F switches on; View's item 6 has no ladder arm, which is what makes "
                + "'(Press Shift For External)' a caption rather than a command. What each id DOES "
                + "is knowledge and lives in the port's code, not here.",
            DgroupImageBase = PortHex.Format(ExeAddresses.DgroupImageBase, 5),
            DescriptorTableDgroup = PortHex.Format(DescriptorTable, 4),
            TitlesBlobDgroup = PortHex.Format(TitlesBlob, 4),
            ItemsBlobImageOffset = PortHex.Format(ItemsBlobImageOffset, 5),
            Menus = menus,
            AboutDialog = about,
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.FlightMenusDto),
            0,
            $"flight_menus: {menus.Count} menus, {items} items");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        FlightMenusDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.FlightMenusDto)
                             ?? throw new InvalidDataException("the flight-menu document is empty");

        List<FlightMenuDto> menus = dto.Menus ?? [];
        if (menus.Count != MenuCount)
        {
            throw new InvalidDataException($"expected {MenuCount} menus, found {menus.Count}");
        }

        List<byte> titles = new List<byte>(64);
        List<ExeSlice> slices = new List<ExeSlice>(MenuCount + 2);
        List<byte> blob = new List<byte>(1024);

        for (int i = 0; i < menus.Count; i++)
        {
            FlightMenuDto menu = menus[i];
            List<FlightMenuItemDto> rows = menu.Items ?? [];
            titles.AddRange(Encoding.Latin1.GetBytes(
                menu.Title ?? throw new InvalidDataException($"menu {i} has no title")));
            titles.Add(0);

            // The eight static bytes of a 24-byte record are NOT contiguous with the next record's,
            // so each menu contributes its own slice; +0x00..+0x0F is runtime geometry and is not
            // this document's to rebuild.
            byte[] descriptor = new byte[DescriptorStaticBytes];
            Span<byte> head = descriptor.AsSpan();
            BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)PortHex.Parse(menu.TitlePointer));
            BinaryPrimitives.WriteUInt16LittleEndian(head[2..], (ushort)PortHex.Parse(menu.ItemsOffset));
            BinaryPrimitives.WriteUInt16LittleEndian(head[4..], (ushort)PortHex.Parse(menu.ItemsSegment));
            BinaryPrimitives.WriteUInt16LittleEndian(head[6..], (ushort)rows.Count);
            slices.Add(new ExeSlice(
                $"menu {i} ({menu.Title}) descriptor static fields",
                ExeAddresses.Image(PortHex.Parse(menu.Dgroup) + DescriptorStaticOffset),
                descriptor));

            // The six blobs are contiguous, in menu order: the far pointer of menu i + 1 is exactly
            // the end of menu i's records, which is why one slice rebuilds all 850 bytes.
            int expected = ItemsBlobImageOffset + blob.Count;
            int stated = PortHex.Parse(menu.ItemsImageOffset);
            if (stated != expected)
            {
                throw new InvalidDataException(
                    $"menu {i}'s items start at image@0x{stated:X5} but the previous menu's records "
                        + $"end at image@0x{expected:X5}");
            }

            foreach (FlightMenuItemDto item in rows)
            {
                WriteItem(blob, item);
            }
        }

        slices.Add(new ExeSlice("menu titles blob", ExeAddresses.Image(TitlesBlob), [.. titles]));
        slices.Add(new ExeSlice("menu items blob", ItemsBlobImageOffset, [.. blob]));

        List<FlightMenuTextDto> about = dto.AboutDialog ?? [];
        if (about.Count != AboutLines.Length)
        {
            throw new InvalidDataException(
                $"expected {AboutLines.Length} About lines, found {about.Count}");
        }

        foreach (FlightMenuTextDto line in about)
        {
            List<byte> bytes = new List<byte>(64);
            bytes.AddRange(Encoding.Latin1.GetBytes(
                line.Text ?? throw new InvalidDataException($"About line {line.Dgroup} has no text")));
            bytes.Add(0);
            slices.Add(new ExeSlice(
                $"About line {line.Dgroup}",
                ExeAddresses.Image(PortHex.Parse(line.Dgroup)),
                [.. bytes]));
        }

        return slices;
    }

    /// <summary>
    /// The six descriptors' static halves, resolved.  Public so the tests can assert the join
    /// without re-deriving the address arithmetic.
    /// </summary>
    /// <param name="image">The unpacked layer-1 image.</param>
    internal static List<MenuDescriptor> Descriptors(ReadOnlySpan<byte> image)
    {
        List<MenuDescriptor> records = new List<MenuDescriptor>(MenuCount);
        for (int i = 0; i < MenuCount; i++)
        {
            int dgroup = DescriptorTable + (i * DescriptorStride);
            int at = ExeAddresses.Image(dgroup) + DescriptorStaticOffset;
            ushort titlePointer = BinaryPrimitives.ReadUInt16LittleEndian(image[at..]);
            ushort offset = BinaryPrimitives.ReadUInt16LittleEndian(image[(at + 2)..]);
            ushort segment = BinaryPrimitives.ReadUInt16LittleEndian(image[(at + 4)..]);
            ushort count = BinaryPrimitives.ReadUInt16LittleEndian(image[(at + 6)..]);
            records.Add(new MenuDescriptor(
                i,
                dgroup,
                titlePointer,
                AsciiZ(image, ExeAddresses.Image(titlePointer)),
                offset,
                segment,
                ExeAddresses.Resolve(offset, segment),
                count));
        }

        return records;
    }

    /// <summary>One descriptor's static half, with its pointers resolved.</summary>
    /// <param name="Index">The menu's index, 0..5.</param>
    /// <param name="Dgroup">The record's DGROUP offset.</param>
    /// <param name="TitlePointer">Field <c>+0x10</c>.</param>
    /// <param name="Title">The title text it points at.</param>
    /// <param name="ItemsOffset">Field <c>+0x12</c>.</param>
    /// <param name="ItemsSegment">Field <c>+0x14</c>.</param>
    /// <param name="ItemsImageOffset">Where the two together resolve to.</param>
    /// <param name="ItemCount">Field <c>+0x16</c>.</param>
    internal sealed record MenuDescriptor(
        int Index,
        int Dgroup,
        ushort TitlePointer,
        string Title,
        ushort ItemsOffset,
        ushort ItemsSegment,
        int ItemsImageOffset,
        int ItemCount);

    private static (FlightMenuItemDto Item, int Next) ReadItem(
        ReadOnlySpan<byte> image, int at, int menu, int index)
    {
        byte marker = image[at];
        if (marker is not (MarkerUnchecked or MarkerChecked))
        {
            throw new InvalidDataException(
                $"menu {menu} item {index} at image@0x{at:X5} starts with 0x{marker:X2}, not a "
                    + "0x15/0x16 marker glyph");
        }

        int end = at + 1;
        while (image[end] != 0)
        {
            end++;
        }

        ReadOnlySpan<byte> body = image[(at + 1)..end];
        bool sectionEnd = body.Length > 0 && body[^1] == SectionEndMark;
        if (sectionEnd)
        {
            body = body[..^1];
        }

        int separator = body.IndexOf(AcceleratorSeparator);
        ReadOnlySpan<byte> labelBytes = separator < 0 ? body : body[..separator];
        string? accelerator = separator < 0
            ? null
            : Encoding.Latin1.GetString(body[(separator + 1)..]);

        string label = Encoding.Latin1.GetString(labelBytes);
        return (
            new FlightMenuItemDto
            {
                Id = PortHex.Format((menu << 8) | index, 3),
                Index = index,
                Label = label,
                Accelerator = accelerator,
                SectionEnd = sectionEnd,
                ArrowGlyph = label.Contains((char)ArrowGlyph, StringComparison.Ordinal),
                Checked = marker == MarkerChecked,
                ImageOffset = PortHex.Format(at, 5),
            },
            end + 1);
    }

    private static void WriteItem(List<byte> blob, FlightMenuItemDto item)
    {
        blob.Add(item.Checked ? MarkerChecked : MarkerUnchecked);
        blob.AddRange(Encoding.Latin1.GetBytes(
            item.Label ?? throw new InvalidDataException($"item {item.Id} has no label")));
        if (item.Accelerator is { } accelerator)
        {
            blob.Add(AcceleratorSeparator);
            blob.AddRange(Encoding.Latin1.GetBytes(accelerator));
        }

        if (item.SectionEnd)
        {
            blob.Add(SectionEndMark);
        }

        blob.Add(0);
    }

    private static string AsciiZ(ReadOnlySpan<byte> image, int at)
    {
        int end = at;
        while (image[end] != 0)
        {
            end++;
        }

        return Encoding.Latin1.GetString(image[at..end]);
    }
}

/// <summary><c>exe/tables/flight_menus.json</c>: the ESC menu bar's six menus.</summary>
public sealed class FlightMenusDto
{
    /// <summary>The document's schema tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is, in prose.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>DGROUP's own image offset, so every DGROUP address here can be resolved.</summary>
    [JsonPropertyName("dgroupImageBase")]
    public string? DgroupImageBase { get; init; }

    /// <summary><c>g_menu_descriptor_table</c>'s DGROUP offset.</summary>
    [JsonPropertyName("descriptorTableDgroup")]
    public string? DescriptorTableDgroup { get; init; }

    /// <summary><c>g_menu_titles_blob</c>'s DGROUP offset.</summary>
    [JsonPropertyName("titlesBlobDgroup")]
    public string? TitlesBlobDgroup { get; init; }

    /// <summary>Where the item records begin in the image.</summary>
    [JsonPropertyName("itemsBlobImageOffset")]
    public string? ItemsBlobImageOffset { get; init; }

    /// <summary>The six menus, left to right along the bar.</summary>
    [JsonPropertyName("menus")]
    public List<FlightMenuDto>? Menus { get; init; }

    /// <summary>The three lines the <c>?</c> menu's "About Yeager..." box prints.</summary>
    [JsonPropertyName("aboutDialog")]
    public List<FlightMenuTextDto>? AboutDialog { get; init; }
}

/// <summary>One NUL-terminated string the menu owns, with where it lives.</summary>
public sealed class FlightMenuTextDto
{
    /// <summary>Its DGROUP offset.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>Its offset in the unpacked image.</summary>
    [JsonPropertyName("imageOffset")]
    public string? ImageOffset { get; init; }

    /// <summary>
    /// The text as shipped.  U+0011 and U+0012 are font glyphs (the copyright sign and the mark
    /// after the title), not control characters.
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>One pull-down menu: its title and its items.</summary>
public sealed class FlightMenuDto
{
    /// <summary>Its position in the bar, 0..5.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The title drawn in the top strip.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>The descriptor record's DGROUP offset.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>Descriptor field <c>+0x10</c>: the title's near pointer.</summary>
    [JsonPropertyName("titlePointer")]
    public string? TitlePointer { get; init; }

    /// <summary>Descriptor field <c>+0x14</c>: the items far pointer's segment half.</summary>
    [JsonPropertyName("itemsSegment")]
    public string? ItemsSegment { get; init; }

    /// <summary>Descriptor field <c>+0x12</c>: its offset half.</summary>
    [JsonPropertyName("itemsOffset")]
    public string? ItemsOffset { get; init; }

    /// <summary>Where the two together resolve to in the unpacked image.</summary>
    [JsonPropertyName("itemsImageOffset")]
    public string? ItemsImageOffset { get; init; }

    /// <summary>The menu's item records, top to bottom.</summary>
    [JsonPropertyName("items")]
    public List<FlightMenuItemDto>? Items { get; init; }
}

/// <summary>One item row of a pull-down menu.</summary>
public sealed class FlightMenuItemDto
{
    /// <summary><c>(menu &lt;&lt; 8) | item</c> — what the engine returns and dispatches on.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Its position in the menu.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>
    /// The row's text as shipped.  A U+0014 inside it is the font's right-arrow glyph.
    /// </summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>The accelerator text drawn right-aligned, or absent when the row has none.</summary>
    [JsonPropertyName("accel")]
    public string? Accelerator { get; init; }

    /// <summary>Whether a horizontal rule follows this row (the record's trailing <c>*</c>).</summary>
    [JsonPropertyName("sectionEnd")]
    public bool SectionEnd { get; init; }

    /// <summary>Whether the label contains the right-arrow glyph.</summary>
    [JsonPropertyName("arrowGlyph")]
    public bool ArrowGlyph { get; init; }

    /// <summary>
    /// Whether the SHIPPED record carries the checked marker glyph (0x16).  Every one of the
    /// fifty-four carries 0x15: the check marks are written at runtime.
    /// </summary>
    [JsonPropertyName("checked")]
    public bool Checked { get; init; }

    /// <summary>Where the record starts in the unpacked image.</summary>
    [JsonPropertyName("imageOffset")]
    public string? ImageOffset { get; init; }
}
