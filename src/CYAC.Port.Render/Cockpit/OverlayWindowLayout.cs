using CYAC.Port.Core.Model.Cockpit;
using CYAC.Port.Core.Model.Mission;

namespace CYAC.Port.Render.Cockpit;

/// <summary>
/// Where the four IN-FLIGHT OVERLAY WINDOWS go, and how big they are.
/// </summary>
/// <remarks>
/// <para>
/// The windows are <c>cockpit_panel_draw_dispatch @image@0x0EA65</c>'s four sub-panels, gated one
/// bit each by <c>g_inflight_overlay_visibility [0xF1CB]</c> (<see cref="CockpitOverlayFlags"/>,
/// persisted at <c>yeager.cfg@0x1D</c>).  Their geometry is not a guess: the dispatcher itself
/// publishes it.
/// </para>
/// <list type="bullet">
/// <item>
/// <c>g_cockpit_hud_row [0xBCF0]</c> is the band's TOP row — <c>image@0x0EA6B..0x0EA77</c> computes
/// <c>0x13</c> (19) for the VGA sub-mode and <c>0x03</c> for CGA.  Measured on the original's own
/// frames: the chrome's first row is 19 (a captured frame of the original).
/// </item>
/// <item>
/// <c>g_cockpit_hud_row_bottom [0xBCFE] = g_cockpit_hud_row + 0x0B</c> (<c>image@0x0EA7A</c>) is the
/// CONTENT's top row — 30, which is exactly where the atlas' black map face starts.  So the title
/// band is <see cref="BandHeight"/> = 11 rows deep, and the footer band below the content is the
/// same depth.
/// </item>
/// <item>
/// The panel fill in the map drawer is <c>menu_filled_rect_draw(…, 0x46, 0x48, g_cockpit_hud_row, 4)</c> at
/// <c>image@0x0EAE8</c>.  The atlas says the window is 72 wide by 70 tall, so <c>0x48</c> = 72 is the WIDTH
/// and <c>0x46</c> = 70 the HEIGHT — note that some nearby labels pair the two the other way round.
/// </item>
/// <item>
/// The X is a CURSOR.  <c>g_cockpit_draw_row [0xBCF4]</c> starts at 4 (<c>image@0x0EA86</c>), the MAP drawer
/// sets it to <c>0x80</c> = 128 (<c>image@0x0EACE</c>), and the dispatcher hands it to the ENVELOPE drawer as
/// that drawer's own argument (<c>image@0x0EAA4</c>).  So with the map up the envelope sits at 128 and without
/// it at 4 — which is exactly what the six cfg variants measure (<c>cfg 0x01</c> puts the envelope at 4;
/// <c>cfg 0x0F</c> puts the map at 4 and the envelope at 128).  Its name is a misnomer: it is a draw COLUMN,
/// not a row.
/// </item>
/// <item>
/// The TARGET window does not use the cursor — it is pinned to the right at x = 244 in every variant
/// that shows it, i.e. <see cref="CockpitLayout.DesignWidth"/> − <see cref="Width"/> − 4.
/// </item>
/// </list>
/// <para>
/// Settled by making him speak: it takes the FIRST slot — the literal 4 at <c>image@0x0EF22</c>, the
/// map's own column — advances the cursor to <c>0x80</c> (<c>image@0x0EF1C</c>) and CLEARS whichever of the
/// map's or the envelope's bit would have shared the slot (<c>image@0x0EF02</c>).  See
/// <see cref="AdvisorWindow"/> and <see cref="EffectiveVisible"/>.
/// </para>
/// </remarks>
public static class OverlayWindowLayout
{
    /// <summary>The window's width in design pixels — <c>0x48</c> at <c>image@0x0EAE8</c>.</summary>
    public const int Width = 72;

    /// <summary>Its height — <c>0x46</c> at the same call.</summary>
    public const int Height = 70;

    /// <summary>
    /// The band's top row for the VGA sub-mode: <c>g_cockpit_hud_row [0xBCF0]</c> = <c>0x13</c>
    /// (<c>image@0x0EA6B</c>; the CGA build uses 3, which the port does not ship — the project's
    /// hard fact is that CYAC is 320×200 VGA only).
    /// </summary>
    public const int Top = 0x13;

    /// <summary>
    /// The depth of the title band above the content and of the footer band below it —
    /// <c>g_cockpit_hud_row_bottom − g_cockpit_hud_row = 0x0B</c> (<c>image@0x0EA7A</c>).
    /// </summary>
    public const int BandHeight = 0x0B;

    /// <summary>The left and right border, in design pixels (measured off the atlas).</summary>
    public const int BorderWidth = 4;

    /// <summary>
    /// The first design column of the left-packed slot — <c>g_cockpit_draw_row [0xBCF4]</c>'s
    /// initial value (<c>image@0x0EA86</c>).
    /// </summary>
    public const int FirstSlotX = 4;

    /// <summary>
    /// What the map drawer advances the cursor to — <c>mov word [0xBCF4],0x80</c>
    /// (<c>image@0x0EACE</c>).
    /// </summary>
    public const int SecondSlotX = 0x80;

    /// <summary>
    /// The row a band's text sits on: the title is drawn at <c>g_cockpit_hud_row + 3</c>
    /// (<c>image@0x0EAFB</c>: <c>add ax,3</c> before the glyph blit), and the atlas' title glyphs
    /// occupy rows 22..26 — 19 + 3 = 22. ✔
    /// </summary>
    public const int BandTextOffset = 3;

    /// <summary>The chrome's palette index: 7, the toolkit grey (<c>data/palettes/palette.png</c>).</summary>
    public const int ChromePaletteIndex = 7;

    /// <summary>The drop shadow's palette index: 0.</summary>
    public const int ShadowPaletteIndex = 0;

    /// <summary>The band text's palette index: 15, white.</summary>
    public const int TextPaletteIndex = 15;

    /// <summary>The window's outer rectangle for a slot's left edge.</summary>
    /// <param name="x">The slot's first design column.</param>
    /// <returns>The 72 × 70 rectangle at <see cref="Top"/>.</returns>
    public static PanelRect Frame(int x) => new(x, Top, Width, Height);

    /// <summary>The 64 × 48 content rectangle inside a window at this slot.</summary>
    /// <param name="x">The slot's first design column.</param>
    /// <returns>The rectangle the window's own contents are drawn into.</returns>
    public static PanelRect Content(int x) => new(
        x + BorderWidth,
        Top + BandHeight,
        Width - (2 * BorderWidth),
        Height - (2 * BandHeight));

    /// <summary>The TARGET window's pinned slot — x = 244 on the original's 320-column screen.</summary>
    public const int TargetSlotX = CockpitLayout.DesignWidth - Width - 4;

    /// <summary>
    /// Which window sits at which design column, for a visibility mask — the dispatch order
    /// (advisor, map, envelope, target) walked over the cursor, with the target pinned right.
    /// </summary>
    /// <param name="visible">The live <c>[0xF1CB]</c> mask.</param>
    /// <returns>The windows to draw, in dispatch order, each with its slot's left edge.</returns>
    public static IReadOnlyList<(CockpitOverlayFlags Window, int X)> Slots(CockpitOverlayFlags visible) =>
        Slots(visible, advisorSpeaking: false);

    /// <summary>
    /// The mask the rest of the frame must use: the live <c>[0xF1CB]</c> with whatever the speaking
    /// advisor cleared out of it.
    /// </summary>
    /// <param name="visible">The live mask.</param>
    /// <param name="advisorSpeaking">Whether a message's display window is open.</param>
    /// <returns>The mask the four drawers actually see.</returns>
    /// <remarks>
    /// The clear happens INSIDE the frame's dispatch (<c>image@0x0EF02</c>) and is undone at its end
    /// (<c>image@0x0EABD</c>), so every drawer downstream of the advisor reads the reduced mask and
    /// nothing outside the dispatch ever does.  A CONTENTS pass that asks the raw mask instead would
    /// draw a window whose chrome was never framed — the same defect class as the TARGET
    /// silhouette's.
    /// </remarks>
    public static CockpitOverlayFlags EffectiveVisible(
        CockpitOverlayFlags visible, bool advisorSpeaking) =>
        advisorSpeaking && visible.HasFlag(CockpitOverlayFlags.Yeager)
            ? visible & ~AdvisorWindow.Suppresses(visible)
            : visible;

    /// <summary>
    /// Which window sits at which design column, with the YEAGER advisor's own slot taken into
    /// account.
    /// </summary>
    /// <param name="visible">The live <c>[0xF1CB]</c> mask.</param>
    /// <param name="advisorSpeaking">
    /// Whether a message is inside its display window this frame
    /// (<see cref="AdvisorPanelState.Speaking"/>).  The advisor draws nothing otherwise, and then it
    /// neither takes a slot nor moves the cursor.
    /// </param>
    /// <returns>The windows to draw, in dispatch order, each with its slot's left edge.</returns>
    /// <remarks>
    /// The advisor is dispatched FIRST (<c>test al,8 / call 0xeedb</c>, <c>image@0x0EA8C</c>) and it
    /// draws at the FIRST slot, exactly where the map goes: both push the literal 4 as
    /// <c>menu_filled_rect_draw</c>'s x (<c>image@0x0EF22</c> / <c>image@0x0EAD4</c>).  That is why
    /// it CLEARS the map's bit for the rest of the frame — see
    /// <see cref="AdvisorWindow.Suppresses"/> — and why it advances the cursor to
    /// <see cref="SecondSlotX"/> before drawing, so the envelope still lands at 128.
    /// </remarks>
    public static IReadOnlyList<(CockpitOverlayFlags Window, int X)> Slots(
        CockpitOverlayFlags visible, bool advisorSpeaking)
    {
        List<(CockpitOverlayFlags, int)> slots = new List<(CockpitOverlayFlags, int)>(4);
        int cursor = FirstSlotX;

        // image@0x0EA8C dispatches bit 3 (the YEAGER advisor) FIRST.  Closed — it is a 72 × 70
        // window at design column 4, measured on four frames in which Chuck speaks; it moves the
        // cursor to 0x80 (image@0x0EF1C) and takes the map's bit, or the envelope's, off the band
        // for the rest of the frame.
        if (advisorSpeaking && visible.HasFlag(CockpitOverlayFlags.Yeager))
        {
            slots.Add((CockpitOverlayFlags.Yeager, cursor));
            cursor = SecondSlotX;                                   // image@0x0EF1C
            visible = EffectiveVisible(visible, advisorSpeaking: true);   // image@0x0EF02..0x0EF1C
        }

        // image@0x0EA93 — bit 2, the MAP, takes the first slot and advances the cursor.
        if (visible.HasFlag(CockpitOverlayFlags.Map))
        {
            slots.Add((CockpitOverlayFlags.Map, cursor));
            cursor = SecondSlotX;
        }

        // image@0x0EA9D — bit 0, the ENVELOPE, which is handed the cursor as its argument.
        if (visible.HasFlag(CockpitOverlayFlags.Envelope))
        {
            slots.Add((CockpitOverlayFlags.Envelope, cursor));
        }

        // image@0x0EAAA — bit 1, the TARGET, pinned right and additionally gated on there BEING a
        // target (target_in_range_view_check @image@0x0A557 through the lcall at image@0x0EAB1).
        if (visible.HasFlag(CockpitOverlayFlags.Target))
        {
            slots.Add((CockpitOverlayFlags.Target, TargetSlotX));
        }

        return slots;
    }

    /// <summary>Whether a left-packed window covers the HUD's top-left text block.</summary>
    /// <param name="visible">The live mask.</param>
    /// <returns>True when the top-left corner belongs to a window.</returns>
    /// <remarks>
    /// Measured, not assumed: the HUD's <c>ZOOM:1</c> + <c>&lt;weapon&gt; :&lt;rounds&gt; (&lt;hit%&gt;)</c>
    /// block is drawn in <c>cfg 0x00</c>, <c>0x02</c> and <c>0x08</c> and absent in <c>0x01</c>,
    /// <c>0x04</c> and <c>0x0F</c> — i.e. absent exactly when the ENVELOPE or the MAP holds the
    /// first slot (the six <c>21_mig21_cfg*_target.png</c> frames).
    /// </remarks>
    public static bool CoversHudTopLeft(CockpitOverlayFlags visible) =>
        visible.HasFlag(CockpitOverlayFlags.Map) || visible.HasFlag(CockpitOverlayFlags.Envelope);

    /// <summary>Whether the TARGET window covers the HUD's top-right block.</summary>
    /// <param name="visible">The live mask.</param>
    /// <returns>True when the top-right corner belongs to the target window.</returns>
    /// <remarks>
    /// The HUD's <c>THR:</c> / <c>VSI:</c> pair is drawn in <c>cfg 0x00</c>, <c>0x01</c>, <c>0x04</c>
    /// and <c>0x08</c> and absent in <c>0x02</c> and <c>0x0F</c>.
    /// </remarks>
    public static bool CoversHudTopRight(CockpitOverlayFlags visible) =>
        visible.HasFlag(CockpitOverlayFlags.Target);


    /// <summary>
    /// Where a band's string starts, in design columns.  Each window has its OWN anchor — there is no
    /// shared centring rule.
    /// </summary>
    /// <param name="window">Which window's band is being drawn.</param>
    /// <param name="x">The window's left edge.</param>
    /// <param name="title">The title string.</param>
    /// <returns>The design column the first glyph is drawn at.</returns>
    /// <remarks>
    /// <para>
    /// *(corrected W3, after W2 had already corrected the same rule for the TARGET window.  The three
    /// drawers each push a DIFFERENT x, and none of them is a centring:</para>
    /// <list type="bullet">
    /// <item>MAP — the literal <c>0x20</c> = 32 (<c>image@0x0EAF4</c>), left-aligned, and the string
    /// is the three-character literal <c>"MAP"</c> at DGROUP <c>0x3BBE</c>
    /// (the DGROUP image base is
    /// <c>image@0x3BD60</c>, so the literal is at <c>image@0x3F91E</c> <c>image@0x3F91E</c>:
    /// <c>4D 41 50 00</c>).  MEASURED: the <c>M</c>'s first ink column is
    /// <b>32</b>, not 28, in every atlas frame that shows the window.</item>
    /// <item>ENVELOPE — <c>(cursor + 0x14) &amp; 0xFC</c> (<c>image@0x0EBB0..0x0EBB8</c>), which is
    /// 148 at the second slot and 24 at the first.  For an eight-character string that happens to
    /// equal the old rule, which is why it fitted.</item>
    /// <item>TARGET — the literal <c>0xF8</c> = 248 plus the type at <c>312 − 4·len</c>.</item>
    /// </list>
    /// <para>The six-character field was an artefact of fitting one rule to three literals.)*</para>
    /// </remarks>
    public static int TitleX(CockpitOverlayFlags window, int x, string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        return window switch
        {
            CockpitOverlayFlags.Map => MapWindow.TitleX,
            CockpitOverlayFlags.Target => TargetTitleX,

            // A FOURTH anchor, and a fourth rule: the advisor's title is the literal 16 the drawer
            // pushes at image@0x0EF42.  Four windows, four different anchors — W0's "one law" is
            // now disproved from every side.
            CockpitOverlayFlags.Yeager => AdvisorWindow.TitleX,
            _ => EnvelopeTitleX(x),
        };
    }

    /// <summary>
    /// The ENVELOPE window's title anchor: <c>(x + 0x14) &amp; 0xFC</c>.
    /// </summary>
    /// <param name="x">The window's left edge — the drawer's own argument, the cursor.</param>
    /// <returns>The design column its title starts at.</returns>
    /// <remarks>
    /// <c>image@0x0EBB0..0x0EBB8</c>: <c>mov ax,[bp-0x64] / add ax,0x14 / and al,0xFC</c>, where
    /// <c>[bp-0x64]</c> is the <c>AX</c> the dispatcher passed (<c>image@0x0EAA4</c>) — i.e.
    /// <c>g_cockpit_draw_row [0xBCF4]</c>, the slot cursor.  Measured: <c>ENVELOPE</c> starts at 148
    /// with the window at 128.  Another part of the port owns the rest of that window; this anchor is here because the
    /// shared <see cref="TitleX"/> had to name it.
    /// </remarks>
    public static int EnvelopeTitleX(int x) => (x + 0x14) & 0xFC;

    /// <summary>
    /// The TARGET window's title band is <b>not centred</b>: it is TWO strings at two fixed anchors.
    /// This is the first, the literal word <c>TARGET</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It reproduces it only because that string happens to be sixteen characters long.
    /// <c>cockpit_target_info_panel_draw</c> draws the word <c>TARGET</c> with
    /// <c>glyph_blit_dispatch(str = DGROUP[0x3BCC], x = 0xF8, y = g_cockpit_hud_row + 3)</c> at
    /// <c>image@0x0EDCF..0x0EDDE</c> — <b><c>x</c> is the literal 248</b>, the content rectangle's
    /// own left edge, with no length term at all.  Measured: the atlas' <c>T</c> is at design column
    /// 248 in every frame that shows the window.
    /// </para>
    /// </remarks>
    public const int TargetTitleX = 0xF8;

    /// <summary>
    /// Where the TARGET window's title band puts the target's TYPE: <c>(0x4E − len) · 4</c>.
    /// </summary>
    /// <param name="typeName">The type string, e.g. <c>MiG-21MF</c>.</param>
    /// <returns>The design column its first glyph is drawn at.</returns>
    /// <remarks>
    /// <c>image@0x0EE02..0x0EE0B</c>: <c>sub cx,0x4E / neg cx / shl cx,1 / shl cx,1</c> with
    /// <c>cx</c> the string's length — i.e. <c>312 − 4·len</c>, a RIGHT edge pinned at design column
    /// 312 (the content rectangle's right edge plus one).  Measured: <c>MiG-21MF</c> (8 characters)
    /// starts at 280 in a captured frame of the original, and
    /// <c>312 − 32 = 280</c> ✔.
    /// </remarks>
    public static int TargetTypeX(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        return (0x4E - typeName.Length) * 4;
    }

    /// <summary>
    /// Where a FOOTER string starts: the content rectangle's own left edge, left-aligned.
    /// </summary>
    /// <param name="x">The window's left edge.</param>
    /// <returns>The design column the first glyph is drawn at.</returns>
    /// <remarks>
    /// Measured: the MAP's <c>x4</c> begins at design column 8 with the window at 4, i.e. exactly
    /// the content's left edge.  The ENVELOPE's <c>1</c> begins one cell further in because its
    /// G-load is formatted into a two-column field (<c>" 1 G"</c>), which is the string's own
    /// padding and not a different anchor.
    /// </remarks>
    public static int FooterX(int x) => x + BorderWidth;
}
