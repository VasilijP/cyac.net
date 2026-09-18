using System.Globalization;
using System.Text;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Flight;

namespace CYAC.Port.Core.Model.Aircraft;

/// <summary>
/// The fourteen aircraft the HANGAR pages through, and the fifteen matchup hints the TACTICS screen
/// shows: <c>pi.json</c> joined to the class table, the engagement prototypes and the side-view
/// sheet rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Data, not sim.</b>  Nothing here is read by <c>Sim/</c>: this is the encyclopedia the front
/// end draws — names, armament lines, engine, the compared numbers, the hangar view, the dimension
/// callouts and the description.  The numbers that make an aeroplane FLY live in
/// <c>aircraft/*.json</c> (<see cref="AircraftDefinition"/>) and the numbers that make one FIGHT
/// live in <c>exe/tables/engagement.json</c>; this document is the brochure.
/// </para>
/// <para>
/// <b>The join, and why it is a join.</b>  A page names its aircraft by CLASS ID
/// (<c>pi.bin +0x00</c>).  Three things the hangar needs are not in <c>pi.bin</c> at all and are
/// reached through that id exactly as <c>ui_aircraft_stats_panel @image@0x264ED</c> reaches them:
/// </para>
/// <list type="number">
///   <item>the class table (<c>aircraft_classes.json</c>) turns the id into the aircraft's
///   ENGAGEMENT-PROTOTYPE DGROUP pointer — its 46-byte stat block
///   (<c>aircraft_class_table_lookup @image@0x24058</c>, called at <c>image@0x26643</c>);</item>
///   <item>that pointer is the KEY the side-view sheet table is searched by
///   (<c>image@0x26A72</c> → <c>plane_silhouette_draw @image@0x271FD</c>), so
///   <see cref="EncyclopediaPlane.SilhouetteRow"/> is resolved, never assumed: the sheet's rows are
///   NOT in <c>pi.json</c> order (see <c>exe/tables/planes_atlas.json</c>);</item>
///   <item>and the stat block's <c>+0x2C</c> is the national-insignia cell the title bar blits
///   (<c>image@0x2673F</c> → <c>panel_sprite_blit @image@0x2569C</c>).</item>
/// </list>
/// <para>
/// Every page's own text is resolved from its string pool through its pointer table, so a string
/// that is not printable ASCII keeps its bytes: the Me-109E's title is
/// <c>Messerschmitt Me-109E \x0C Emil"</c>, and <c>0x0C</c> is <c>propbold</c>'s opening-quote
/// GLYPH, which the original draws as a left double quote.
/// </para>
/// </remarks>
public sealed class AircraftEncyclopedia
{
    /// <summary>The document this model is built from.</summary>
    public const string DataPath = "pi.json";

    /// <summary>The side-view sheet table's document.</summary>
    public const string AtlasDataPath = "exe/tables/planes_atlas.json";

    /// <summary>How many pages the original ships.</summary>
    public const int PageCount = 14;

    /// <summary>How many matchup hints it ships.</summary>
    public const int HintCount = 15;

    private AircraftEncyclopedia(
        IReadOnlyList<EncyclopediaPlane> planes, IReadOnlyList<MatchupHint> hints)
    {
        Planes = planes;
        Hints = hints;
    }

    /// <summary>The fourteen pages, in the order <c>←</c> and <c>→</c> cycle them.</summary>
    public IReadOnlyList<EncyclopediaPlane> Planes { get; }

    /// <summary>The fifteen matchup hints (F5's tactics screen).</summary>
    public IReadOnlyList<MatchupHint> Hints { get; }

    /// <summary>The page for an aircraft-class id, or <see langword="null"/>.</summary>
    /// <param name="classId">The class id (<c>pi.bin +0x00</c>).</param>
    public EncyclopediaPlane? ByClassId(int classId)
    {
        foreach (EncyclopediaPlane plane in Planes)
        {
            if (plane.AircraftClassId == classId)
            {
                return plane;
            }
        }

        return null;
    }

    /// <summary>The page whose flyable slot this is, or <see langword="null"/>.</summary>
    /// <param name="slot">An index into <see cref="AircraftDefinition.FlyableBasenames"/>.</param>
    public EncyclopediaPlane? ByFlyableSlot(int slot)
    {
        foreach (EncyclopediaPlane plane in Planes)
        {
            if (plane.FlyableSlot == slot)
            {
                return plane;
            }
        }

        return null;
    }

    /// <summary>Builds the model from the tree's four documents.</summary>
    /// <param name="encyclopedia"><c>pi.json</c>.</param>
    /// <param name="atlas"><c>exe/tables/planes_atlas.json</c>.</param>
    /// <param name="classes"><c>exe/tables/aircraft_classes.json</c>.</param>
    /// <param name="engagement"><c>exe/tables/engagement.json</c>.</param>
    /// <exception cref="InvalidDataException">A document is missing a row the join needs.</exception>
    public static AircraftEncyclopedia Load(
        AircraftEncyclopediaDto encyclopedia,
        PlanesAtlasTableDto atlas,
        AircraftClassTableDto classes,
        EngagementDocumentDto engagement)
    {
        ArgumentNullException.ThrowIfNull(encyclopedia);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(classes);
        ArgumentNullException.ThrowIfNull(engagement);

        Dictionary<int, EngagementPrototypeDto> prototypeByDgroup = new Dictionary<int, EngagementPrototypeDto>();
        foreach (EngagementPrototypeDto prototype in engagement.Prototypes ?? [])
        {
            if (prototype.Dgroup is { } dgroup)
            {
                prototypeByDgroup[PortHex.Parse(dgroup)] = prototype;
            }
        }

        Dictionary<int, int> pointerByClassId = new Dictionary<int, int>();
        foreach (AircraftClassEntryDto entry in classes.Entries ?? [])
        {
            if (entry.Value is { } value)
            {
                pointerByClassId[entry.ClassId] = PortHex.Parse(value);
            }
        }

        Dictionary<int, SilhouetteRow> rowByKey = new Dictionary<int, SilhouetteRow>();
        foreach (PlanesAtlasRowDto row in atlas.Records ?? [])
        {
            if (row.Key is { } key)
            {
                rowByKey[PortHex.Parse(key)] = new SilhouetteRow(
                    row.Index, row.Variant, row.TopY, row.Height, atlas.SheetWidth);
            }
        }

        List<EncyclopediaPlane> planes = new List<EncyclopediaPlane>(PageCount);
        foreach (EncyclopediaPlaneDto page in encyclopedia.Planes ?? [])
        {
            planes.Add(Build(page, pointerByClassId, prototypeByDgroup, rowByKey));
        }

        List<MatchupHint> hints = new List<MatchupHint>(HintCount);
        foreach (EncyclopediaHintDto hint in encyclopedia.Hints ?? [])
        {
            hints.Add(new MatchupHint(
                hint.Index, hint.PlayerClassId, hint.EnemyClassId, hint.Text ?? string.Empty));
        }

        return new AircraftEncyclopedia(planes, hints);
    }

    private static EncyclopediaPlane Build(
        EncyclopediaPlaneDto page,
        IReadOnlyDictionary<int, int> pointerByClassId,
        IReadOnlyDictionary<int, EngagementPrototypeDto> prototypeByDgroup,
        IReadOnlyDictionary<int, SilhouetteRow> rowByKey)
    {
        if (!pointerByClassId.TryGetValue(page.AircraftClassId, out int pointer))
        {
            throw new InvalidDataException(
                $"pi.json page {page.Index} names class {page.AircraftClassId}, which "
                    + "exe/tables/aircraft_classes.json does not carry");
        }

        if (!prototypeByDgroup.TryGetValue(pointer, out EngagementPrototypeDto? prototype))
        {
            throw new InvalidDataException(
                $"pi.json page {page.Index} resolves to [0x{pointer:X4}], which "
                    + "exe/tables/engagement.json does not carry as a prototype");
        }

        if (!rowByKey.TryGetValue(pointer, out SilhouetteRow row))
        {
            throw new InvalidDataException(
                $"pi.json page {page.Index} resolves to [0x{pointer:X4}], which "
                    + "exe/tables/planes_atlas.json has no silhouette row for");
        }

        Dictionary<int, string> pool = new Dictionary<int, string>();
        foreach (EncyclopediaStringDto text in page.Strings ?? [])
        {
            pool[text.Offset] = text.Text ?? Latin1(text.Hex);
        }

        string Resolve(string field) =>
            page.TextPointers is { } pointers
                && pointers.TryGetValue(field, out int at)
                && pool.TryGetValue(at, out string? value)
                    ? value
                    : string.Empty;

        return new EncyclopediaPlane(
            page.Index,
            page.AircraftClassId,
            prototype.Name ?? string.Empty,
            prototype.ClassRecord ?? string.Empty,
            pointer,
            prototype.InsigniaIndex,
            page.FlyablePlayerSlot,
            Resolve("nameFull"),
            Resolve("nameShort"),
            [Resolve("armament1"), Resolve("armament2"), Resolve("armament3")],
            Resolve("armamentSummary"),
            Resolve("engine"),
            Resolve("description"),
            page.ArmamentRating,
            page.WeightLb,
            page.MaxSpeedMph,
            page.MaxAltitudeFt,
            page.ThrustToWeightQ8,
            page.WingLoadingPsf,
            new HangarView(page.HangarCameraX, page.HangarCameraY, page.HangarCameraZ),
            page.SilhouetteYOffset,
            new DimensionCallouts(
                page.LengthFt,
                page.LengthIn,
                page.HeightFt,
                page.HeightIn,
                page.LengthCalloutX1,
                page.LengthCalloutX2,
                page.HeightCalloutY1,
                page.HeightCalloutY2),
            row);
    }

    private static string Latin1(string? hex) => hex is { Length: > 0 }
        ? System.Text.Encoding.Latin1.GetString(Convert.FromHexString(hex))
        : string.Empty;
}

/// <summary>
/// Where the hangar puts the aircraft in its 3-D view (<c>pi.bin +0x1C..+0x20</c>).
/// </summary>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="Z">World Z — the viewing distance.</param>
/// <remarks>
/// The three are WHOLE WORLD UNITS in the document; the engine promotes each to the 24.8 fixed point
/// its object positions are kept in (<c>cdq; cl = 8; shl_i32_by_cl</c> at
/// <c>image@0x26655</c>/<c>0x2666A</c>/<c>0x2667F</c>) and writes them into the 24-byte object
/// record the panel publishes at <c>[0x3AE8]</c>, whose <c>+0x12</c>/<c>+0x14</c> are the two
/// rotation angles the arrow keys turn.  So it is a POSITION for the aeroplane with the eye at the
/// origin, not a camera placement — which is how the port draws it.
/// </remarks>
public readonly record struct HangarView(int X, int Y, int Z);

/// <summary>The SIDE VIEW's two dimension callouts, in design pixels and feet/inches.</summary>
/// <param name="LengthFt">The length callout's feet.</param>
/// <param name="LengthIn">Its inches.</param>
/// <param name="HeightFt">The height callout's feet.</param>
/// <param name="HeightIn">Its inches.</param>
/// <param name="LengthX1">The horizontal line's left end (<c>image@0x26B23</c>).</param>
/// <param name="LengthX2">Its right end (<c>image@0x26B27</c>).</param>
/// <param name="HeightY1">The vertical line's top (<c>image@0x26AAE</c>).</param>
/// <param name="HeightY2">Its bottom (<c>image@0x26AB6</c>).</param>
public readonly record struct DimensionCallouts(
    int LengthFt,
    int LengthIn,
    int HeightFt,
    int HeightIn,
    int LengthX1,
    int LengthX2,
    int HeightY1,
    int HeightY2);

/// <summary>Which row of which sheet an aircraft's side view is.</summary>
/// <param name="Index">Its position in the table, 0..13.</param>
/// <param name="Variant">0 = <c>planes0.pic</c>, 1 = <c>planes1.pic</c>.</param>
/// <param name="TopY">The row's first line in the 112 × 200 portrait sheet.</param>
/// <param name="Height">How many lines the row is.</param>
/// <param name="SheetWidth">The blit's width: the whole sheet (<c>image@0x2727D</c>).</param>
public readonly record struct SilhouetteRow(
    int Index, int Variant, int TopY, int Height, int SheetWidth)
{
    /// <summary>The sheet's basename in <c>data/images/</c>.</summary>
    public string SheetName => Variant == 0 ? "planes0" : "planes1";
}

/// <summary>One matchup hint from <c>pi.bin</c>'s second directory (F5's tactics screen).</summary>
/// <param name="Index">Its position in the directory.</param>
/// <param name="PlayerClassId">The player's aircraft class.</param>
/// <param name="EnemyClassId">The enemy's.</param>
/// <param name="Text">The advice, as shipped.</param>
public readonly record struct MatchupHint(int Index, int PlayerClassId, int EnemyClassId, string Text);

/// <summary>One HANGAR page: everything the screen prints about one aeroplane.</summary>
/// <param name="Index">Its position in the encyclopedia, 0..13.</param>
/// <param name="AircraftClassId">Its class id (<c>pi.bin +0x00</c>).</param>
/// <param name="ClassName">The prototype's own short name, e.g. <c>MiG-21MF</c>.</param>
/// <param name="MeshBasename">Its mesh class, e.g. <c>mig21</c> — <c>classRecord</c> in the table.</param>
/// <param name="PrototypeDgroup">Its 46-byte stat block's DGROUP pointer — the silhouette key.</param>
/// <param name="InsigniaIndex">Which <c>insigv</c> cell the title bar blits (<c>+0x2C</c>).</param>
/// <param name="FlyableSlot">
/// Its index in <see cref="AircraftDefinition.FlyableBasenames"/>, or −1 for the eight the player
/// cannot fly — which is what greys <c>Fly</c> (<c>is_aircraft_flyable_get_player_idx
/// @image@0x27404</c>).
/// </param>
/// <param name="NameFull">The title-bar name.</param>
/// <param name="NameShort">The short name the tactics screen's column header uses.</param>
/// <param name="ArmamentLines">The three <c>ARMAMENT:</c> lines, blank where the record has none.</param>
/// <param name="ArmamentSummary">The one-line summary the tactics screen prints.</param>
/// <param name="Engine">The <c>POWER:</c> line.</param>
/// <param name="Description">The blurb under the panel.</param>
/// <param name="ArmamentRating">The tactics screen's armament rating.</param>
/// <param name="WeightLb"><c>WEIGHT</c>, pounds.</param>
/// <param name="MaxSpeedMph"><c>MAX SPEED</c>, mph.</param>
/// <param name="MaxAltitudeFt"><c>MAX ALT</c>, feet.</param>
/// <param name="ThrustToWeightQ8">Thrust-to-weight as u8.8.</param>
/// <param name="WingLoadingPsf">Wing loading, pounds per square foot.</param>
/// <param name="HangarView">Where the 3-D view puts it.</param>
/// <param name="SilhouetteYOffset">How far the side view's vertical centre moves.</param>
/// <param name="Callouts">The two dimension callouts.</param>
/// <param name="SilhouetteRow">Which sheet row its side view is.</param>
public sealed record EncyclopediaPlane(
    int Index,
    int AircraftClassId,
    string ClassName,
    string MeshBasename,
    int PrototypeDgroup,
    int InsigniaIndex,
    int FlyableSlot,
    string NameFull,
    string NameShort,
    IReadOnlyList<string> ArmamentLines,
    string ArmamentSummary,
    string Engine,
    string Description,
    int ArmamentRating,
    int WeightLb,
    int MaxSpeedMph,
    int MaxAltitudeFt,
    int ThrustToWeightQ8,
    int WingLoadingPsf,
    HangarView HangarView,
    int SilhouetteYOffset,
    DimensionCallouts Callouts,
    SilhouetteRow SilhouetteRow)
{
    /// <summary>Whether the player may fly it — the six with a <see cref="FlyableSlot"/>.</summary>
    public bool IsFlyable => FlyableSlot >= 0;

    /// <summary>
    /// Its basename in <see cref="AircraftDefinition.FlyableBasenames"/>, or empty when it is not
    /// flyable.
    /// </summary>
    public string FlyableBasename => IsFlyable
        && FlyableSlot < AircraftDefinition.FlyableBasenames.Count
            ? AircraftDefinition.FlyableBasenames[FlyableSlot]
            : string.Empty;

    /// <summary>Thrust-to-weight as a number — <c>+0x16</c> is u8.8.</summary>
    public double ThrustToWeight => ThrustToWeightQ8 / 256.0;

    /// <summary>Its length as the callout prints it, e.g. <c>32'2"</c>.</summary>
    /// <param name="format">The DGROUP literal <c>%d'%d"</c> (<c>[0x3966]</c>).</param>
    public string LengthText(string format) => Feet(format, Callouts.LengthFt, Callouts.LengthIn);

    /// <summary>Its height as the callout prints it.</summary>
    /// <param name="format">The DGROUP literal <c>%d'%d"</c> (<c>[0x3966]</c>).</param>
    public string HeightText(string format) => Feet(format, Callouts.HeightFt, Callouts.HeightIn);

    private static string Feet(string format, int feet, int inches)
    {
        ArgumentNullException.ThrowIfNull(format);
        StringBuilder result = new System.Text.StringBuilder(format.Length + 4);
        int next = 0;
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] == '%' && i + 1 < format.Length && format[i + 1] == 'd')
            {
                result.Append((next++ == 0 ? feet : inches).ToString(CultureInfo.InvariantCulture));
                i++;
            }
            else
            {
                result.Append(format[i]);
            }
        }

        return result.ToString();
    }
}
