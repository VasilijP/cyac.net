using System.Text.Json;
using CYAC.Port.Core.Data;

namespace CYAC.Port.Host.Menu;

/// <summary>
/// The in-flight ESC menu bar's six menus, read out of <c>data/exe/tables/flight_menus.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Not one of the fifty-four labels is written in this source file: the titles, the item texts and
/// the accelerator texts all come from the shipping executable through <c>cyac-transform</c>
/// (<c>FlightMenuExeTable</c>), which is the transform-first law — the port ships knowledge, not
/// data.  What the port DOES add is in <see cref="FlightMenuActions"/>: what each id means.
/// </para>
/// <para>
/// <b>Why the document is parsed here rather than through <c>DataTree</c>.</b> Every other exe-table
/// has a typed reader in <c>CYAC.Port.Core</c>, whose DTOs the transform shares.  This type's
/// landing zone excludes <c>CYAC.Port.Core</c> entirely, so the host reads the document itself.  It
/// uses the <c>System.Text.Json</c> DOM rather than a reflection-based deserialise, so nothing here
/// needs a serialisation context and nothing trims away.  <b>(open)</b> this should move
/// the DTO into <c>CYAC.Port.Core.Data</c> beside <c>CockpitLayoutDto</c> and give <c>DataTree</c> a
/// <c>FlightMenus</c> property, at which point this parser becomes three lines.
/// </para>
/// </remarks>
public sealed class FlightMenuTable
{
    /// <summary>Where the document lives in the data tree.</summary>
    public const string DataPath = "exe/tables/flight_menus.json";

    /// <summary>How many menus the bar carries.</summary>
    public const int MenuCount = 6;

    /// <summary>The right-arrow glyph a View label carries, <c>propbold.fnt</c> codepoint 0x14.</summary>
    public const char ArrowGlyph = '\u0014';

    private FlightMenuTable(IReadOnlyList<FlightMenu> menus) => Menus = menus;

    /// <summary>The six menus, left to right along the strip.</summary>
    public IReadOnlyList<FlightMenu> Menus { get; }

    /// <summary>Every item of every menu, in id order.</summary>
    public IEnumerable<FlightMenuItem> AllItems => Menus.SelectMany(m => m.Items);

    /// <summary>Wraps a menu list the host has augmented with its own rows.</summary>
    /// <param name="menus">The six menus.</param>
    /// <exception cref="ArgumentException">There are not six of them.</exception>
    public static FlightMenuTable From(IReadOnlyList<FlightMenu> menus)
    {
        ArgumentNullException.ThrowIfNull(menus);
        if (menus.Count != MenuCount)
        {
            throw new ArgumentException($"expected {MenuCount} menus, got {menus.Count}", nameof(menus));
        }

        return new FlightMenuTable(menus);
    }

    /// <summary>Reads the document out of the data tree.</summary>
    /// <param name="tree">The data tree.</param>
    /// <exception cref="InvalidDataException">The document is missing a field or a menu.</exception>
    public static FlightMenuTable Load(DataTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(tree.Resolve(DataPath)));
        if (!document.RootElement.TryGetProperty("menus", out JsonElement menus))
        {
            throw new InvalidDataException($"{DataPath} carries no \"menus\" array");
        }

        List<FlightMenu> read = new List<FlightMenu>(MenuCount);
        foreach (JsonElement menu in menus.EnumerateArray())
        {
            int index = menu.GetProperty("index").GetInt32();
            List<FlightMenuItem> items = new List<FlightMenuItem>();
            foreach (JsonElement item in menu.GetProperty("items").EnumerateArray())
            {
                string label = item.GetProperty("label").GetString() ?? string.Empty;
                items.Add(new FlightMenuItem(
                    PortHex.Parse(item.GetProperty("id").GetString()),
                    index,
                    item.GetProperty("index").GetInt32(),
                    label,
                    item.TryGetProperty("accel", out JsonElement accel) ? accel.GetString() : null,
                    item.TryGetProperty("sectionEnd", out JsonElement end) && end.GetBoolean()));
            }

            read.Add(new FlightMenu(
                index,
                menu.GetProperty("title").GetString() ?? "?",
                items));
        }

        if (read.Count != MenuCount)
        {
            throw new InvalidDataException(
                $"{DataPath} carries {read.Count} menus, expected {MenuCount}");
        }

        return new FlightMenuTable(read);
    }
}

/// <summary>One pull-down menu: the title in the strip and the rows under it.</summary>
/// <param name="Index">Its position in the strip, 0..5.</param>
/// <param name="Title">The title text, out of <c>g_menu_titles_blob [0x1030]</c>.</param>
/// <param name="Items">Its rows, top to bottom.</param>
public sealed record FlightMenu(int Index, string Title, IReadOnlyList<FlightMenuItem> Items);

/// <summary>One row of a pull-down menu.</summary>
/// <param name="Id">
/// <c>(menu &lt;&lt; 8) | item</c> — what <c>menu_bar_engine @image@0x20D04</c> returns and what the
/// 53-entry dispatch ladder at <c>image@0x2186F</c> switches on.
/// </param>
/// <param name="MenuIndex">Which menu it belongs to.</param>
/// <param name="Index">Its position in that menu.</param>
/// <param name="Label">Its text as shipped; may contain <see cref="FlightMenuTable.ArrowGlyph"/>.</param>
/// <param name="Accelerator">The accelerator text drawn right-aligned, or null.</param>
/// <param name="SectionEnd">Whether a horizontal rule follows the row (the record's trailing <c>*</c>).</param>
public sealed record FlightMenuItem(
    int Id,
    int MenuIndex,
    int Index,
    string Label,
    string? Accelerator,
    bool SectionEnd);
