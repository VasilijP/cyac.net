using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

/// <summary>A rectangle in the game's own 320×200 screen space.</summary>
/// <remarks>
/// Every cockpit table stores rectangles as four <c>u16</c> in this order, and the consumers treat
/// words 2 and 3 as EXTENTS: <c>gfx_viewport_clip_rect_setup @image@0x11876</c> computes
/// <c>x_max = x + width − 1</c> and <c>y_max = y + height − 1</c>.
/// </remarks>
public sealed class ScreenRectDto
{
    /// <summary>Left column.</summary>
    [JsonPropertyName("x")]
    public int X { get; init; }

    /// <summary>Top row.</summary>
    [JsonPropertyName("y")]
    public int Y { get; init; }

    /// <summary>Width in pixels.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>Height in scanlines.</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }
}

/// <summary>A point in the game's own 320×200 screen space.</summary>
public sealed class ScreenPointDto
{
    /// <summary>Column.</summary>
    [JsonPropertyName("x")]
    public int X { get; init; }

    /// <summary>Row.</summary>
    [JsonPropertyName("y")]
    public int Y { get; init; }
}

/// <summary>One flyable aircraft's 3-D viewport row and its cockpit asset suffix.</summary>
/// <remarks>
/// The row is <c>g_aircraft_viewport_table [0x3C02] + idx·8</c>, copied whole into
/// <c>g_active_viewport_rect [0xB10A]</c> every frame the cockpit is drawn
/// (<c>image@0x015E0..0x015F6</c>).  The suffix is what
/// <c>cockpit_assets_load_all @image@0x0E356</c> concatenates to build the cockpit art's file names
/// — <c>&lt;suffix&gt;v.pic</c>, <c>&lt;suffix&gt;_horiz.msk</c> — read through the near-pointer
/// table <c>[0x4226]</c> (<c>image@0x0E3E8</c>).
/// </remarks>
public sealed class CockpitViewportDto
{
    /// <summary>The aircraft's port basename (<c>"p51"</c>), in <c>g_active_aircraft_idx</c> order.</summary>
    [JsonPropertyName("aircraft")]
    public string? Aircraft { get; init; }

    /// <summary>Its cockpit-asset file-name suffix (<c>"51"</c>).</summary>
    [JsonPropertyName("assetSuffix")]
    public string? AssetSuffix { get; init; }

    /// <summary>The DGROUP offset of the suffix string — the pointer the engine stores at <c>[0x4226 + idx·2]</c>.</summary>
    [JsonPropertyName("assetSuffixPointer")]
    public string? AssetSuffixPointer { get; init; }

    /// <summary>The row's own DGROUP offset.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>The 3-D viewport rectangle while the cockpit is drawn.</summary>
    [JsonPropertyName("viewport")]
    public ScreenRectDto? Viewport { get; init; }
}

/// <summary>One 38-byte HUD layout block: nineteen <c>u16</c> screen anchors.</summary>
/// <remarks>
/// <c>hud_per_frame_draw @image@0x0C5C0..0x0C5E2</c> copies one of these blocks into
/// <c>[0xF180..0xF1A5]</c> with a single <c>rep movsw cx=0x13</c>, choosing the per-aircraft block
/// at <c>[0x309E + idx·0x26]</c> when the cockpit is drawn this frame
/// (<c>g_cockpit_drawn_flag [0xE472] != 0</c>) and the full-screen default at <c>[0x3078]</c>
/// otherwise.
/// </remarks>
public sealed class HudLayoutBlockDto
{
    /// <summary>The aircraft this block belongs to, or null for the full-screen default.</summary>
    [JsonPropertyName("aircraft")]
    public string? Aircraft { get; init; }

    /// <summary>The block's DGROUP offset.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary><c>+0x00</c> — the flags/brake/gear text column (<c>[0xF180]</c>).</summary>
    [JsonPropertyName("flagsIndicatorX")]
    public int FlagsIndicatorX { get; init; }

    /// <summary><c>+0x02</c> — its companion column (<c>[0xF182]</c>).</summary>
    [JsonPropertyName("flagsIndicatorColumn")]
    public int FlagsIndicatorColumn { get; init; }

    /// <summary><c>+0x04</c> — the altitude readout's anchor column (<c>[0xF184]</c>).</summary>
    [JsonPropertyName("altitudeAnchorX")]
    public int AltitudeAnchorX { get; init; }

    /// <summary><c>+0x06</c> — its anchor row (<c>[0xF186]</c>).</summary>
    [JsonPropertyName("altitudeAnchorY")]
    public int AltitudeAnchorY { get; init; }

    /// <summary><c>+0x08</c> — the waypoint readout's anchor column (<c>[0xF188]</c>).</summary>
    [JsonPropertyName("waypointAnchorX")]
    public int WaypointAnchorX { get; init; }

    /// <summary><c>+0x0A</c> — its anchor row (<c>[0xF18A]</c>).</summary>
    [JsonPropertyName("waypointAnchorY")]
    public int WaypointAnchorY { get; init; }

    /// <summary><c>+0x0C</c> — the vertical-speed readout's anchor column (<c>[0xF18C]</c>).</summary>
    [JsonPropertyName("vsiAnchorX")]
    public int VsiAnchorX { get; init; }

    /// <summary><c>+0x0E</c> — its anchor row (<c>[0xF18E]</c>).</summary>
    [JsonPropertyName("vsiAnchorY")]
    public int VsiAnchorY { get; init; }

    /// <summary><c>+0x10</c> — the heading readout's anchor row (<c>[0xF190]</c>).</summary>
    [JsonPropertyName("headingAnchorY")]
    public int HeadingAnchorY { get; init; }

    /// <summary><c>+0x12</c> — the waypoint block's second row (<c>[0xF192]</c>).</summary>
    [JsonPropertyName("waypointSecondY")]
    public int WaypointSecondY { get; init; }

    /// <summary>
    /// <c>+0x14</c> — the target marker's LEFT clip bound.  Named here: the marker clips to
    /// <c>[+0x14 + 4 … +0x16 − 4]</c> around <c>g_gfx_screen_centre_x [0xE634]</c>,
    /// <c>hud_target_screen_marker_draw @image@0x1ED6A..0x1ED75</c>.
    /// </summary>
    [JsonPropertyName("targetMarkerClipLeft")]
    public int TargetMarkerClipLeft { get; init; }

    /// <summary><c>+0x16</c> — the target marker's RIGHT clip bound (<c>image@0x1ED44..0x1ED51</c>).</summary>
    [JsonPropertyName("targetMarkerClipRight")]
    public int TargetMarkerClipRight { get; init; }

    /// <summary><c>+0x18</c> — the message strip's row (<c>[0xF198]</c>).</summary>
    [JsonPropertyName("messageLineY")]
    public int MessageLineY { get; init; }

    /// <summary><c>+0x1A</c> — the HUD's inner clip rectangle (<c>[0xF19A..0xF1A1]</c>).</summary>
    [JsonPropertyName("innerClip")]
    public ScreenRectDto? InnerClip { get; init; }

    /// <summary>
    /// <c>+0x22</c> — no reader anywhere in the image (exhaustive displacement sweep for
    /// <c>0xF1A2</c>: zero hits), so it is carried as an open byte pair.
    /// </summary>
    [JsonPropertyName("unknown_0x22")]
    public string? Unknown0x22 { get; init; }

    /// <summary><c>+0x24</c> — likewise unread (<c>0xF1A4</c>: zero hits).</summary>
    [JsonPropertyName("unknown_0x24")]
    public string? Unknown0x24 { get; init; }
}

/// <summary>One of the ten cockpit instrument REGIONS and the per-aircraft tables it indexes.</summary>
/// <remarks>
/// The region's own 44-byte record (<c>s_cockpit_region</c>) is runtime state and is not published;
/// what ships here is the CONSTANT data the record is filled from at mission load
/// (<c>cockpit_assets_load_all @image@0x0E356</c> → <c>cockpit_mask_load_via_concat @image@0x0DAE7</c>)
/// and the constant source rectangles the draw functions index.
/// </remarks>
public sealed class CockpitRegionTableDto
{
    /// <summary>The region's push-order index 0..9.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>What the region is (<c>"artificialHorizon"</c>, <c>"gear"</c>, …).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The <c>.msk</c> overlay suffix this region loads, or null when it is rect-only.</summary>
    [JsonPropertyName("maskSuffix")]
    public string? MaskSuffix { get; init; }

    /// <summary>The DGROUP offset of the per-aircraft rectangle table.</summary>
    [JsonPropertyName("rectTableDgroup")]
    public string? RectTableDgroup { get; init; }

    /// <summary>
    /// The region's rectangle per aircraft — a zero WIDTH means the aircraft does not have the
    /// instrument, which is the gate the loader tests.  Region 9 carries a SINGLE rectangle, not one
    /// per aircraft (<c>[0x41E6]</c>, MiG-21 only).
    /// </summary>
    [JsonPropertyName("rects")]
    public List<ScreenRectDto>? Rects { get; init; }

    /// <summary>The DGROUP offset of the per-aircraft overlay pivot table, when the region has one.</summary>
    [JsonPropertyName("pivotTableDgroup")]
    public string? PivotTableDgroup { get; init; }

    /// <summary>The overlay's pivot point per aircraft.</summary>
    [JsonPropertyName("pivots")]
    public List<ScreenPointDto>? Pivots { get; init; }

    /// <summary>The DGROUP offset of the per-aircraft OFF-state source-point table, when there is one.</summary>
    [JsonPropertyName("offStateTableDgroup")]
    public string? OffStateTableDgroup { get; init; }

    /// <summary>
    /// Where in <c>miscv.pic</c> — the SHARED instrument-sprite sheet, not the aircraft's own
    /// picture — the OFF-state lamp lives, per aircraft.
    /// </summary>
    /// <remarks>
    /// H10a fix pass. <c>cockpit_assets_load_all</c> loads <c>"miscv.pic"</c> (<c>[0x4314]</c>) or
    /// <c>"misc.pic"</c> (<c>[0x431E]</c>) into the buffer descriptor at <c>[0x421A]</c>
    /// (<c>image@0x0E379..0x0E390</c>), and the binary-state draw functions pass THAT descriptor as
    /// <c>gfx_plain_blit</c>'s source with this point as its source x/y, while the destination is
    /// <c>[0xE7EC]</c>, the screen surface, at the region's own rectangle
    /// (<c>image@0x0DDC6..0x0DDE3</c>).
    /// </remarks>
    [JsonPropertyName("offStateSource")]
    public List<ScreenPointDto>? OffStateSource { get; init; }

    /// <summary>The DGROUP offset of the per-aircraft ON-state source-point table, when there is one.</summary>
    [JsonPropertyName("onStateTableDgroup")]
    public string? OnStateTableDgroup { get; init; }

    /// <summary>Where in <c>miscv.pic</c> the ON-state lamp lives, per aircraft.</summary>
    [JsonPropertyName("onStateSource")]
    public List<ScreenPointDto>? OnStateSource { get; init; }
}

/// <summary>One of the F-86's four combined gear+flap icons.</summary>
/// <remarks>
/// <c>region_3_flaps_draw_fn @image@0x0DD91..0x0DDA4</c> indexes <c>[0x424C]</c> by the two-bit
/// composite <c>flapDown·2 | gearDown</c> its state function returns, so the F-86 has ONE indicator
/// for both and no separate gear region (its region-4 row is zero).
/// </remarks>
public sealed class F86GearFlapIconDto
{
    /// <summary>Whether the gear is down in this state (bit 0).</summary>
    [JsonPropertyName("gearDown")]
    public bool GearDown { get; init; }

    /// <summary>Whether the flaps are down in this state (bit 1).</summary>
    [JsonPropertyName("flapDown")]
    public bool FlapDown { get; init; }

    /// <summary>Where the icon lives in <c>miscv.pic</c>, the shared sprite sheet.</summary>
    [JsonPropertyName("source")]
    public ScreenPointDto? Source { get; init; }
}

/// <summary>Where one aircraft prints its chaff and flare counts.</summary>
/// <remarks><c>region_8_chaff_flare_draw_fn @image@0x0DEA7</c>: F-4 → <c>[0x42EC]</c>, MiG-21 → <c>[0x42F4]</c>.</remarks>
public sealed class CountermeasureTextDto
{
    /// <summary>The aircraft's port basename.</summary>
    [JsonPropertyName("aircraft")]
    public string? Aircraft { get; init; }

    /// <summary>The table's DGROUP offset.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>Where the two-digit chaff count is drawn.</summary>
    [JsonPropertyName("chaff")]
    public ScreenPointDto? Chaff { get; init; }

    /// <summary>Where the two-digit flare count is drawn.</summary>
    [JsonPropertyName("flare")]
    public ScreenPointDto? Flare { get; init; }
}

/// <summary>The WEAPON+AMMO region's per-aircraft colours and its two format strings.</summary>
/// <remarks>
/// <para>
/// <c>region_6_draw_fn @image@0x0DF28</c> clear-fills its rectangle with the background byte and
/// draws the text in the foreground byte.  The two format strings live in the string catalogue
/// (<c>exe/strings.json</c> owns those bytes); they are repeated here as VALUES so the region is
/// readable in one place.
/// </para>
/// <para>
/// already said "weapon + ammo (NOT chance-to-hit — that's a HUD overlay)", and the address
/// arithmetic settles it: the state function reads <c>[bx − 0x12D4]</c> with <c>bx =
/// 2·g_hud_current_weapon_slot [0xED2A]</c>, and <c>−0x12D4</c> modulo 64 KB is <c>0xED2C</c> =
/// <c>g_hud_weapon_slot_ammo</c>, the <c>u16[4]</c> of rounds per slot that
/// <c>player_weapon_loadout_publish @image@0x27616</c> fills.  Hence the default format <c>"%4s
/// %4d"</c>: the weapon's NAME and its ROUNDS.
/// </para>
/// </remarks>
public sealed class WeaponAmmoStyleDto
{
    /// <summary>The DGROUP offset of the per-aircraft foreground-colour bytes.</summary>
    [JsonPropertyName("textColorDgroup")]
    public string? TextColorDgroup { get; init; }

    /// <summary>The foreground palette index per aircraft.</summary>
    [JsonPropertyName("textColorByAircraft")]
    public List<int>? TextColorByAircraft { get; init; }

    /// <summary>The DGROUP offset of the per-aircraft background-colour bytes.</summary>
    [JsonPropertyName("backgroundColorDgroup")]
    public string? BackgroundColorDgroup { get; init; }

    /// <summary>The background palette index per aircraft.</summary>
    [JsonPropertyName("backgroundColorByAircraft")]
    public List<int>? BackgroundColorByAircraft { get; init; }

    /// <summary>The F-86's format string (<c>[0x42FC]</c>), owned by <c>exe/strings.json</c>.</summary>
    [JsonPropertyName("formatF86")]
    public string? FormatF86 { get; init; }

    /// <summary>Every other aircraft's format string (<c>[0x4300]</c>).</summary>
    [JsonPropertyName("formatDefault")]
    public string? FormatDefault { get; init; }
}

/// <summary>
/// The cockpit's CONSTANT layout: the per-aircraft 3-D viewport, the HUD layout blocks and the ten
/// instrument regions' source tables (<c>exe/tables/cockpit_layout.json</c>).
/// </summary>
/// <remarks>
/// The three families the 1991 engine keeps in DGROUP and the port had no document for: the viewport
/// table <c>[0x3C02]</c>, the seven HUD layout blocks <c>[0x3078..0x3181]</c>, and the cockpit region
/// tables <c>[0x3E4A..0x4313]</c> plus the asset-suffix pointer table <c>[0x4226]</c>.
/// </remarks>
public sealed class CockpitLayoutDto
{
    /// <summary>The document's format tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is, for a reader who has never seen the engine.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>DGROUP's own image offset, so every <c>dgroup</c> field can be located.</summary>
    [JsonPropertyName("dgroupImageBase")]
    public string? DgroupImageBase { get; init; }

    /// <summary>The six flyable aircraft's viewport rows, in <c>g_active_aircraft_idx</c> order.</summary>
    [JsonPropertyName("viewports")]
    public List<CockpitViewportDto>? Viewports { get; init; }

    /// <summary>
    /// The seven HUD layout blocks: the full-screen default first (<c>[0x3078]</c>), then one per
    /// aircraft.
    /// </summary>
    [JsonPropertyName("hudLayouts")]
    public List<HudLayoutBlockDto>? HudLayouts { get; init; }

    /// <summary>The ten instrument regions and their per-aircraft tables.</summary>
    [JsonPropertyName("regions")]
    public List<CockpitRegionTableDto>? Regions { get; init; }

    /// <summary>The F-86's four combined gear+flap icons, indexed by the state composite 0..3.</summary>
    [JsonPropertyName("f86GearFlapIcons")]
    public List<F86GearFlapIconDto>? F86GearFlapIcons { get; init; }

    /// <summary>Where the two countermeasure-equipped aircraft print their counts.</summary>
    [JsonPropertyName("countermeasureText")]
    public List<CountermeasureTextDto>? CountermeasureText { get; init; }

    /// <summary>The weapon+ammo region's colours and formats.</summary>
    [JsonPropertyName("weaponAmmo")]
    public WeaponAmmoStyleDto? WeaponAmmo { get; init; }
}
