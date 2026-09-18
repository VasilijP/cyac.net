using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Flight;

namespace CYAC.Port.Core.Model.Cockpit;

/// <summary>A rectangle in the game's own 320×200 design space.</summary>
/// <param name="X">Left column.</param>
/// <param name="Y">Top row.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in scanlines.</param>
/// <remarks>
/// Width and height are EXTENTS: <c>gfx_viewport_clip_rect_setup @image@0x11876</c> derives
/// <c>x_max = x + width − 1</c> and the window's centre as <c>(x_min + x_max) &gt;&gt; 1</c>.
/// </remarks>
public readonly record struct PanelRect(int X, int Y, int Width, int Height)
{
    /// <summary>Whether the rectangle has any area — a zero width is the tables' "absent" gate.</summary>
    public bool IsPresent => Width > 0 && Height > 0;

    /// <summary>One past the last column.</summary>
    public int Right => X + Width;

    /// <summary>One past the last row.</summary>
    public int Bottom => Y + Height;

    /// <summary>Whether a point lies inside the rectangle.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>A point in the game's own 320×200 design space.</summary>
/// <param name="X">Column.</param>
/// <param name="Y">Row.</param>
public readonly record struct PanelPoint(int X, int Y);

/// <summary>One flyable aircraft's cockpit geometry.</summary>
/// <param name="Basename">Its port basename (<c>"p51"</c>).</param>
/// <param name="AssetSuffix">Its cockpit-art file-name suffix (<c>"51"</c>).</param>
/// <param name="Viewport">The rectangle the 3-D world fills while the cockpit is drawn.</param>
public readonly record struct CockpitViewport(string Basename, string AssetSuffix, PanelRect Viewport);

/// <summary>One instrument region's per-aircraft source tables.</summary>
/// <param name="Index">Its push-order index 0..9.</param>
/// <param name="Name">What the instrument is.</param>
/// <param name="MaskSuffix">The <c>.msk</c> overlay it loads, or null when it is rectangle-only.</param>
/// <param name="Rects">Its rectangle per aircraft — a zero width means the aircraft has no such instrument.</param>
/// <param name="Pivots">Its overlay pivot per aircraft, when it has one.</param>
/// <param name="OffStateSource">Where the OFF-state icon lives in the cockpit art, per aircraft.</param>
/// <param name="OnStateSource">Where the ON-state icon lives, per aircraft.</param>
public sealed record CockpitRegionLayout(
    int Index,
    string Name,
    string? MaskSuffix,
    IReadOnlyList<PanelRect> Rects,
    IReadOnlyList<PanelPoint>? Pivots,
    IReadOnlyList<PanelPoint>? OffStateSource,
    IReadOnlyList<PanelPoint>? OnStateSource)
{
    /// <summary>The rectangle for one aircraft — region 9 carries a single one for every index.</summary>
    /// <param name="aircraftIndex">The aircraft's index 0..5.</param>
    public PanelRect RectFor(int aircraftIndex) =>
        Rects.Count == 1 ? Rects[0] : Rects[aircraftIndex];
}

/// <summary>One 38-byte HUD layout block, as nineteen named anchors.</summary>
/// <param name="Aircraft">The aircraft it belongs to, or null for the full-screen default.</param>
/// <param name="FlagsIndicatorX">The flaps/brake/gear text column.</param>
/// <param name="FlagsIndicatorColumn">Its companion column.</param>
/// <param name="AltitudeAnchor">The altitude readout's anchor.</param>
/// <param name="WaypointAnchor">The waypoint readout's anchor.</param>
/// <param name="VsiAnchor">The vertical-speed readout's anchor.</param>
/// <param name="HeadingAnchorY">The heading readout's row.</param>
/// <param name="WaypointSecondY">The waypoint block's second row.</param>
/// <param name="TargetMarkerClipLeft">The target marker's left clip bound.</param>
/// <param name="TargetMarkerClipRight">Its right clip bound.</param>
/// <param name="MessageLineY">The message strip's row.</param>
/// <param name="InnerClip">The HUD's inner clip rectangle.</param>
/// <remarks>
/// H10b draws these; H10a publishes them.  <c>hud_per_frame_draw @image@0x0C5C0</c> selects the
/// per-aircraft block when the cockpit is drawn this frame and the default when it is not.
/// </remarks>
public sealed record HudLayout(
    string? Aircraft,
    int FlagsIndicatorX,
    int FlagsIndicatorColumn,
    PanelPoint AltitudeAnchor,
    PanelPoint WaypointAnchor,
    PanelPoint VsiAnchor,
    int HeadingAnchorY,
    int WaypointSecondY,
    int TargetMarkerClipLeft,
    int TargetMarkerClipRight,
    int MessageLineY,
    PanelRect InnerClip)
{
    /// <summary>
    /// The LEFT COLUMN of the flaps/brake/gear text stack: the block's <c>+0x00</c>.
    /// </summary>
    /// <remarks>
    /// <c>image@0x0C90F..0x0C91F</c> pushes <c>"FLAPS"</c>, then <c>[0xF180]</c>, then
    /// <c>[0xF182]</c>, and <c>glyph_blit_dispatch @image@0x1F4AE</c> is
    /// <c>(str = [bp+0xA], x = [bp+8], row = [bp+6])</c> with <c>retf 6</c> — so the SECOND push is
    /// the x and the third is the row.  <c>+0x00</c> is 4 on every shipped block: column 4.
    /// </remarks>
    public int StatusColumn => FlagsIndicatorX;

    /// <summary>
    /// The TOP ROW shared by the flaps stack and the altitude/speed/G stack: <c>+0x02</c>.
    /// </summary>
    /// <remarks>
    /// The same three pushes make it the row, and the airspeed and G-load arms draw at <c>+0x02 +
    /// 6</c> and <c>+0x02 + 0xC</c> (<c>image@0x0C6BC</c>, <c>image@0x0C716</c>) — three
    /// six-scanline lines from row 1.  The document's field name <c>flagsIndicatorColumn</c> is a
    /// misnomer for a row; the value is 1 on every shipped block.
    /// </remarks>
    public int StatusRow => FlagsIndicatorColumn;
}

/// <summary>
/// The cockpit's constant layout, read from <c>exe/tables/cockpit_layout.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Three families in one document (see <c>CYAC.Port.Transform</c>'s <c>CockpitLayoutExeTable</c>):
/// the per-aircraft 3-D viewport rectangle, the seven HUD layout blocks and the ten instrument
/// regions' per-aircraft source tables.  Nothing here is transcribed into source.
/// </para>
/// <para>
/// The COCKPIT-DRAWN rule the engine applies each frame is
/// <c>[0xE472] = ([0xE471] != 0) &amp;&amp; ([0xE46F] != 0) &amp;&amp; ([0xC32F] == 0)</c>
/// (<c>image@0x010AA..0x010CA</c>).  <c>[0xE471]</c> is the player's own Backspace toggle,
/// <c>[0xC32F]</c> the augured-in flag, and <c>[0xE46F]</c> is bit 0 of the current view's flag byte
/// <c>[0x2BC0 + view]</c> — set on view 0 (F1) and NO other, so the cockpit is painted in the
/// forward view alone.  See <see cref="DrawsCockpitInView"/>.
/// </para>
/// </remarks>
public sealed class CockpitLayout
{
    private readonly IReadOnlyList<HudLayout> _hudPerAircraft;

    private CockpitLayout(
        IReadOnlyList<CockpitViewport> viewports,
        HudLayout hudDefault,
        IReadOnlyList<HudLayout> hudPerAircraft,
        IReadOnlyList<CockpitRegionLayout> regions,
        IReadOnlyList<PanelPoint> f86GearFlapIcons,
        IReadOnlyDictionary<string, (PanelPoint Chaff, PanelPoint Flare)> countermeasureText,
        IReadOnlyList<byte> weaponAmmoTextColor,
        IReadOnlyList<byte> weaponAmmoBackgroundColor)
    {
        Viewports = viewports;
        HudDefault = hudDefault;
        _hudPerAircraft = hudPerAircraft;
        Regions = regions;
        F86GearFlapIcons = f86GearFlapIcons;
        CountermeasureText = countermeasureText;
        WeaponAmmoTextColor = weaponAmmoTextColor;
        WeaponAmmoBackgroundColor = weaponAmmoBackgroundColor;
    }

    /// <summary>The original's design space: 320 columns.</summary>
    public const int DesignWidth = 320;

    /// <summary>The original's design space: 200 rows.</summary>
    public const int DesignHeight = 200;

    /// <summary>
    /// The one view the panel is painted in — <c>[0x2BC0 + view]</c> bit 0 is set on this id alone.
    /// </summary>
    public const int CockpitPaintedViewId = 0;

    /// <summary>
    /// How many instrument REGIONS a cockpit has — <c>cockpit_dispatch_state_changes
    /// @image@0x0E735</c> pushes exactly ten records.
    /// </summary>
    public const int RegionCount = 10;

    /// <summary>The six aircraft's viewport rows, in <c>g_active_aircraft_idx</c> order.</summary>
    public IReadOnlyList<CockpitViewport> Viewports { get; }

    /// <summary>The full-screen HUD layout block <c>[0x3078]</c>.</summary>
    public HudLayout HudDefault { get; }

    /// <summary>The ten instrument regions, in push order.</summary>
    public IReadOnlyList<CockpitRegionLayout> Regions { get; }

    /// <summary>The F-86's four combined gear+flap icons, indexed by <c>flapDown·2 | gearDown</c>.</summary>
    public IReadOnlyList<PanelPoint> F86GearFlapIcons { get; }

    /// <summary>Where the two countermeasure-equipped aircraft print their counts, by basename.</summary>
    public IReadOnlyDictionary<string, (PanelPoint Chaff, PanelPoint Flare)> CountermeasureText { get; }

    /// <summary>The weapon+ammo readout's text colour per aircraft.</summary>
    public IReadOnlyList<byte> WeaponAmmoTextColor { get; }

    /// <summary>Its background colour per aircraft.</summary>
    public IReadOnlyList<byte> WeaponAmmoBackgroundColor { get; }

    /// <summary>The HUD layout block for one aircraft.</summary>
    /// <param name="aircraftIndex">Its index 0..5.</param>
    public HudLayout HudFor(int aircraftIndex) => _hudPerAircraft[aircraftIndex];

    /// <summary>
    /// Whether the original paints the cockpit in a given view id — bit 0 of the per-view flag byte,
    /// which is set on the forward cockpit view and nothing else.
    /// </summary>
    /// <param name="viewId">The view id (<c>CYAC.Port.Render.ViewMode</c>'s own numbering).</param>
    public static bool DrawsCockpitInView(int viewId) => viewId == CockpitPaintedViewId;

    /// <summary>Looks an aircraft up by basename.</summary>
    /// <param name="basename">Its port basename.</param>
    /// <returns>Its index 0..5.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No flyable aircraft has that name.</exception>
    public int IndexOf(string basename)
    {
        ArgumentNullException.ThrowIfNull(basename);
        for (int i = 0; i < Viewports.Count; i++)
        {
            if (string.Equals(Viewports[i].Basename, basename, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(basename), basename, "not one of the six flyable aircraft");
    }

    /// <summary>Reads the layout out of an opened data tree.</summary>
    /// <param name="tree">The transformed data tree.</param>
    /// <exception cref="InvalidDataException">The document is missing a section.</exception>
    public static CockpitLayout Load(DataTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return From(tree.CockpitLayout);
    }

    /// <summary>Builds the layout from an already-read document.</summary>
    /// <param name="document">The <c>exe/tables/cockpit_layout.json</c> document.</param>
    /// <exception cref="InvalidDataException">The document is missing a section.</exception>
    public static CockpitLayout From(CockpitLayoutDto document)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<CockpitViewport> viewports = new List<CockpitViewport>();
        foreach (CockpitViewportDto row in document.Viewports ?? [])
        {
            viewports.Add(new CockpitViewport(
                row.Aircraft ?? throw new InvalidDataException("a viewport row has no aircraft"),
                row.AssetSuffix ?? throw new InvalidDataException("a viewport row has no asset suffix"),
                Rect(row.Viewport, "viewport")));
        }

        if (viewports.Count != AircraftDefinition.FlyableBasenames.Count)
        {
            throw new InvalidDataException(
                $"cockpit_layout.json carries {viewports.Count} viewport rows, expected "
                    + $"{AircraftDefinition.FlyableBasenames.Count}");
        }

        HudLayout? fallback = null;
        List<HudLayout> perAircraft = new List<HudLayout>();
        foreach (HudLayoutBlockDto block in document.HudLayouts ?? [])
        {
            HudLayout layout = new HudLayout(
                block.Aircraft,
                block.FlagsIndicatorX,
                block.FlagsIndicatorColumn,
                new PanelPoint(block.AltitudeAnchorX, block.AltitudeAnchorY),
                new PanelPoint(block.WaypointAnchorX, block.WaypointAnchorY),
                new PanelPoint(block.VsiAnchorX, block.VsiAnchorY),
                block.HeadingAnchorY,
                block.WaypointSecondY,
                block.TargetMarkerClipLeft,
                block.TargetMarkerClipRight,
                block.MessageLineY,
                Rect(block.InnerClip, "HUD inner clip"));

            if (block.Aircraft is null)
            {
                fallback = layout;
            }
            else
            {
                perAircraft.Add(layout);
            }
        }

        if (fallback is null || perAircraft.Count != viewports.Count)
        {
            throw new InvalidDataException(
                "cockpit_layout.json must carry one default HUD layout block and one per aircraft");
        }

        List<CockpitRegionLayout> regions = new List<CockpitRegionLayout>();
        foreach (CockpitRegionTableDto region in document.Regions ?? [])
        {
            regions.Add(new CockpitRegionLayout(
                region.Index,
                region.Name ?? throw new InvalidDataException("a cockpit region has no name"),
                region.MaskSuffix,
                [.. (region.Rects ?? []).Select(r => Rect(r, "region rectangle"))],
                Points(region.Pivots),
                Points(region.OffStateSource),
                Points(region.OnStateSource)));
        }

        PanelPoint[] icons = new PanelPoint[4];
        foreach (F86GearFlapIconDto icon in document.F86GearFlapIcons ?? [])
        {
            int state = (icon.FlapDown ? 2 : 0) | (icon.GearDown ? 1 : 0);
            icons[state] = Point(icon.Source, "F-86 gear/flap icon");
        }

        Dictionary<string, (PanelPoint, PanelPoint)> text = new Dictionary<string, (PanelPoint, PanelPoint)>(StringComparer.Ordinal);
        foreach (CountermeasureTextDto entry in document.CountermeasureText ?? [])
        {
            text[entry.Aircraft ?? throw new InvalidDataException("a countermeasure row has no aircraft")] =
                (Point(entry.Chaff, "chaff text"), Point(entry.Flare, "flare text"));
        }

        WeaponAmmoStyleDto style = document.WeaponAmmo
                                   ?? throw new InvalidDataException("cockpit_layout.json carries no weapon+ammo style");

        return new CockpitLayout(
            viewports,
            fallback,
            perAircraft,
            regions,
            icons,
            text,
            [.. (style.TextColorByAircraft ?? []).Select(v => (byte)v)],
            [.. (style.BackgroundColorByAircraft ?? []).Select(v => (byte)v)]);
    }

    private static PanelRect Rect(ScreenRectDto? dto, string what) =>
        dto is null
            ? throw new InvalidDataException($"cockpit_layout.json: a {what} is missing")
            : new PanelRect(dto.X, dto.Y, dto.Width, dto.Height);

    private static PanelPoint Point(ScreenPointDto? dto, string what) =>
        dto is null
            ? throw new InvalidDataException($"cockpit_layout.json: a {what} is missing")
            : new PanelPoint(dto.X, dto.Y);

    private static IReadOnlyList<PanelPoint>? Points(List<ScreenPointDto>? points) =>
        points is null ? null : [.. points.Select(p => Point(p, "point"))];
}
