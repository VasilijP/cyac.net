using System.Globalization;
using CYAC.Port.Core.Model.Cockpit;

namespace CYAC.Port.Render.Cockpit;

/// <summary>
/// Which colour byte the MAP window picks for a contact, from
/// <c>pixel_obj_color_select_forward_view @image@0x0D2F6</c> and the REGISTER callback that fills
/// <c>slot[+4]</c> (<c>pixel_obj_project_forward_view @image@0x0D2C0</c>).
/// </summary>
/// <remarks>
/// <para>
/// The four bytes live in a six-byte block at DGROUP <c>[0x325B..0x3260]</c> whose SHIPPED values
/// are static data in the image (<c>image@0x3EFBB..0x3EFC0</c> = <c>B3 C4 0E 08 02 0A</c>);
/// <c>pixel_obj_slot_array_reset_and_color_init @image@0x0D5DA</c> overwrites them with the CGA set
/// <c>BB BB 0F 0D 0D 0F</c> only when <c>g_cfg_sub_mode [0x015E] == 0</c>, and the shipped 320×200
/// build runs sub-mode 6.  An image-wide scan finds no other writer of any of the six.
/// </para>
/// <para>
/// Each byte is TWO nibbles, and the altitude picks one: <c>test al,0xF0</c> at
/// <c>image@0x0D322</c> and the three-way compare of the contact's Y against the camera object's
/// (<c>image@0x0D32C..0x0D344</c>) — at or ABOVE the player takes the HIGH nibble (<c>shr
/// al,4</c>), below it the low one (<c>and al,0x0F</c>).  A byte whose high nibble is zero has no
/// altitude sense and is used whole.
/// </para>
/// </remarks>
public enum MapDotColour
{
    /// <summary>
    /// An object carrying an engagement block whose <c>+0x05</c> bit 6 (the HOSTILE bit) is CLEAR —
    /// colour byte <c>a [0x325B] = 0xB3</c>, chosen at <c>image@0x0D2DD</c>.  Light cyan above the
    /// player, cyan below.
    /// </summary>
    Friendly = 0,

    /// <summary>
    /// The same, with the hostile bit SET (<c>test word es:[si+5],0x40</c> @<c>image@0x0D2D5</c>) —
    /// colour byte <c>b [0x325C] = 0xC4</c>.  Light red above, red below.
    /// </summary>
    Hostile = 1,

    /// <summary>
    /// A combat-spawn object (a shot) whose spawn-table slot still holds its ORIGINAL target:
    /// <c>slot[+8] != 0 &amp;&amp; slot[+8] == slot[+0x0A]</c> (<c>image@0x0D312..0x0D31A</c>, over the
    /// record <c>spawn_slot_lookup_or_echo @image@0x03729</c> returns) — colour byte
    /// <c>c [0x325D] = 0x0E</c>, yellow, with no altitude sense.
    /// </summary>
    GuidedLocked = 2,

    /// <summary>
    /// Any other object without an engagement block — colour byte <c>d [0x325E] = 0x08</c>, dark
    /// grey, no altitude sense (<c>image@0x0D2EC</c>).
    /// </summary>
    Spawn = 3,
}

/// <summary>
/// The LAW of the in-flight MAP window: where it is centred, what it plots, in what colour, how far
/// it reaches, and what the <c>,</c> / <c>.</c> zoom keys do to it.
/// </summary>
/// <remarks>
/// <para>
/// Sources.  The drawer is <c>image@0x0EAC7</c> — the function still calls
/// <c>cockpit_airspeed_altimeter_draw</c>, a misnomer W0 disproved (the dispatch at
/// <c>image@0x0EA93</c> is <c>test byte [0xF1CB],4 / call 0x0EAC7</c> and the bit → window mapping
/// was measured one bit at a time).  It arms the shared pixel-obj engine —
/// <c>per_frame_object_pixel_setup @image@0x0D6D8</c> at <c>image@0x0EB39</c> — with the same
/// projector the radar and the RWR use, so <see cref="RadarScope"/> is the projection and this class
/// is only what the MAP does differently.
/// </para>
/// <para>
/// Verified against the original to the PIXEL and to the PALETTE INDEX.  With the paired screenshot
/// + DGROUP + object-pool dumps of (whose frames are byte-identical to the atlas) the projector
/// places, at <c>a_mig21_110</c> (player at (58017041, 678333, 10085242) heading 360, shift 9):
/// <c>0x53EB</c> → (37,31), <c>0x543D</c> → (38,31), <c>0x548F</c> → (38,32) — all three measured
/// palette 12 in a captured frame of the original, which is
/// colour byte <c>b</c>'s HIGH nibble, and all three ARE above the player; and <c>0x5399</c> →
/// (39,54), measured palette 11 = colour byte <c>a</c>'s high nibble, the friendly wingman.  At
/// <c>b_f4_190</c>: <c>0x5399</c> → (37,67) measured palette 12 (above) and <c>0x53EB</c> → (36,67)
/// measured palette <b>4</b> — byte <c>b</c>'s LOW nibble, a hostile BELOW the player.  That last
/// pixel is the altitude rule's own proof.
/// </para>
/// </remarks>
public static class MapWindow
{
    /// <summary>
    /// The address of the window's own title, drawn LEFT-ALIGNED at design column
    /// <see cref="TitleX"/> — the three-character literal at DGROUP <c>0x3BBE</c> (file offset
    /// corrected W4 — the DGROUP's initialised image starts at <c>image@0x3BD60</c>, so
    /// <c>DS:0x3BBE</c> is <c>image@0x3F91E</c>; <c>0x4791E</c> is 0x8000 past it and reads as
    /// zeroes. <c>image@0x3F91E</c>, between the zoom prefix and the ENVELOPE title).  The text is
    /// <see cref="InFlightStrings.MapTitle"/>. Read from the tree.
    /// </summary>
    /// <remarks>
    /// …c</c> reads <c>DS:0x3BBE</c> as "a RUNTIME buffer, BSS-zero at startup,
    /// populated upstream by a not-yet-decoded formatter … holds a pre-formatted flight-data string
    /// such as <c>13392 FT</c>".  It is nothing of the kind: the bytes at DGROUP <c>0x3BBE</c> are
    /// <c>4D 41 50 00</c> in the shipped image.
    /// </remarks>
    public const int TitleDgroup = InFlightStrings.MapTitleDgroup;

    /// <summary>
    /// Where that title starts: the literal <c>0x20</c> pushed at <c>image@0x0EAF4</c> as
    /// <c>glyph_blit_dispatch</c>'s x argument — no centring term at all.
    /// </summary>
    /// <remarks>
    /// MEASURED: the first ink of <c>MAP</c> is at design column <b>32</b> in every atlas frame
    /// that shows the window (<c>21_mig21_cfg04_target.png</c>, <c>21_mig21_cfg0F_target.png</c>,
    /// the six <c>30_f4_*.png</c>).  28 is not where the glyphs are, and the six-character field
    /// was an artefact of fitting a centring rule to a left-aligned literal. Same shape as the
    /// correction an earlier pass made to the TARGET window's title.
    /// </remarks>
    public const int TitleX = 0x20;

    /// <summary>
    /// The content rectangle the map plots into, in design pixels: the four-word local the drawer
    /// builds at <c>image@0x0EB09..0x0EB19</c> — <c>{8, g_cockpit_hud_row_bottom, 0x40, 0x30}</c> —
    /// and hands the pixel-obj engine as its clip rect (arg 7).
    /// </summary>
    /// <remarks>
    /// It is the same rectangle <see cref="OverlayWindowLayout.Content"/> derives for the first slot,
    /// which is the cross-check: 4 + 4 = 8, 19 + 11 = 30, 72 − 8 = 64, 70 − 22 = 48.  Measured on the
    /// atlas as 3,070 black pixels inside it.
    /// </remarks>
    public static PanelRect Content => new(8, OverlayWindowLayout.Top + OverlayWindowLayout.BandHeight, 0x40, 0x30);

    /// <summary>
    /// The player's own position on the face — the pixel-obj screen centre the drawer passes:
    /// <c>0x28</c> = 40 (<c>image@0x0EB22</c>) and <c>g_cockpit_hud_row_bottom + 0x18</c> = 54
    /// (<c>image@0x0EB26</c>).
    /// </summary>
    /// <remarks>
    /// So the map is CENTRED on the player, not player-at-the-bottom like the radar: measured, the
    /// white pixel is at (40, 54) in every atlas frame that shows the window, and the F-4's single
    /// contact walks DOWN past it (row 50 at 90 M, 54 at 110 M, 57 at 130 M, 61 at 150 M, 64 at 170
    /// M, 67 at 190 M) as it goes from ahead of the aeroplane to behind it.
    /// </remarks>
    public static (int X, int Y) Centre =>
        (0x28, OverlayWindowLayout.Top + OverlayWindowLayout.BandHeight + 0x18);

    /// <summary>
    /// The own-ship marker's palette index: 15, the low byte of the literal <c>0xFF0F</c> the render
    /// pass draws it with (<c>image@0x0D6CA..0x0D6CE</c>), unconditionally, after the contact loop.
    /// </summary>
    /// <remarks>
    /// It does NOT blink.  The full-screen F9 map's own symbol does — that one is
    /// <c>screen_buffer_iter_dispatch @image@0x1E766</c> reading <c>g_map_symbol_blink_phase
    /// [0xBA14]</c> (<c>image@0x1E722</c> / <c>image@0x1E879</c>) — but this window is a different
    /// subsystem and its marker is one unconditional pixel per frame. Measured: palette 15 at
    /// (40,54) in all 30 atlas frames that show the window, including the consecutive-instant series
    /// <c>25_mig21_radar_88..150</c>.
    /// </remarks>
    public const int OwnShipPaletteIndex = 15;

    /// <summary>The face behind the plot: palette 0 — the black inset the atlas shows.</summary>
    /// <remarks>
    /// <c>per_frame_object_pixel_render</c>'s step 0 fills the whole clip rect with the colour the
    /// context's colour-select callback returns for <c>SI = 0</c> (<c>image@0x0D611..0x0D628</c>),
    /// and the map's returns the literal <c>0xFF00</c> (<c>image@0x0D2FA</c>): <c>AH = 0xFF</c> is
    /// the solid-colour tag, <c>AL = 0</c> the palette index.
    /// </remarks>
    public const int FacePaletteIndex = 0;

    /// <summary>
    /// The map's zoom, and the shared pixel-obj scale: <c>g_stipple_scale_shift [0x3234]</c>, which
    /// is 9 at load (<c>mov word [0x3234],9</c> @<c>image@0x00ABD</c>).
    /// </summary>
    public const int DefaultScaleShift = RadarScope.DefaultScaleShift;

    /// <summary>The zoom-in floor — <c>cmp word [0x3234],8 / jle</c> (<c>image@0x012B9</c>).</summary>
    public const int MinScaleShift = RadarScope.MinScaleShift;

    /// <summary>The zoom-out ceiling — <c>cmp word [0x3234],0x0B / jge</c> (<c>image@0x012CE</c>).</summary>
    public const int MaxScaleShift = RadarScope.MaxScaleShift;

    /// <summary>
    /// What the footer band prints: <c>'x'</c> followed by <c>1 &lt;&lt; (0x0B − shift)</c>.
    /// </summary>
    /// <param name="scaleShift">The live <c>[0x3234]</c>.</param>
    /// <returns><c>x8</c>, <c>x4</c>, <c>x2</c> or <c>x1</c>.</returns>
    /// <remarks>
    /// <c>image@0x0EB51..0x0EB63</c>: <c>mov byte [bp-0x12],0x78</c> (<c>'x'</c>), then <c>mov ax,1
    /// / mov cl,0x0B / sub cl,[0x3234] / shl ax,cl</c> and the space-padded integer formatter into
    /// <c>&amp;buf[1]</c>.  The atlas reads <b><c>x4</c></b> and every dump reads <c>[0x3234] =
    /// 9</c>, so the pairing is measured, not assumed: shift 8 → <c>x8</c> (512 ft per design
    /// pixel), 9 → <c>x4</c> (1,024), 10 → <c>x2</c> (2,048), 11 → <c>x1</c> (4,096).  The number is
    /// a MAGNIFICATION against the most-zoomed-out setting, not a range.
    /// </remarks>
    public static string ZoomLabel(int scaleShift) =>
        "x" + (1 << (0x0B - Math.Clamp(scaleShift, MinScaleShift, MaxScaleShift)))
            .ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The <c>.</c> key: zoom IN one step (<c>image@0x012B2</c> — <c>cmp [0x3234],8 / jle skip /
    /// dec [0x3234]</c>).
    /// </summary>
    /// <param name="scaleShift">The live shift.</param>
    /// <returns>The new shift, unchanged at the floor.</returns>
    /// <remarks>
    /// The key is cooked <c>0x2E</c> = <c>'.'</c>: the in-flight ladder's running compare chain from
    /// <c>image@0x00E27</c> reaches this arm at key 46, and its neighbour at 44 (<c>','</c>).  Both
    /// arms are gated on the in-flight byte <c>[0xC31C]</c>.
    /// </remarks>
    public static int ZoomIn(int scaleShift) =>
        scaleShift > MinScaleShift ? scaleShift - 1 : scaleShift;

    /// <summary>
    /// The <c>,</c> key: zoom OUT one step (<c>image@0x012C7</c> — <c>cmp [0x3234],0x0B / jge skip /
    /// inc [0x3234]</c>).
    /// </summary>
    /// <param name="scaleShift">The live shift.</param>
    /// <returns>The new shift, unchanged at the ceiling.</returns>
    public static int ZoomOut(int scaleShift) =>
        scaleShift < MaxScaleShift ? scaleShift + 1 : scaleShift;

    /// <summary>
    /// How far the map reaches, in feet: the pixel-obj proximity gate for THIS context's rectangle,
    /// <c>(64 + 48) &lt;&lt; (8 + shift)</c> world units (<c>image@0x0D732..0x0D74B</c>).
    /// </summary>
    /// <param name="scaleShift">The live shift.</param>
    /// <returns>57,344 ft at the default shift 9.</returns>
    /// <remarks>
    /// Measured: <c>[0x3250/52] = 14,680,064</c> in every probe dump, and
    /// <c>112 × 2^17 = 14,680,064</c> ✔.  The window itself only shows ±32 px, i.e. ±32,768 ft
    /// across at shift 9, so the gate is looser than the face and the rectangle clip is what
    /// actually bounds the plot.
    /// </remarks>
    public static double RangeGateFeet(int scaleShift) =>
        (Content.Width + Content.Height) * (double)(1 << scaleShift);

    /// <summary>
    /// The colour BYTE for a contact class — the four shipped values, as static data.
    /// </summary>
    /// <param name="colour">Which class.</param>
    /// <returns>The packed two-nibble byte.</returns>
    public static int DotByte(MapDotColour colour) => colour switch
    {
        MapDotColour.Friendly => 0xB3,       // [0x325B], image@0x3EFBB
        MapDotColour.Hostile => 0xC4,        // [0x325C], image@0x3EFBC
        MapDotColour.GuidedLocked => 0x0E,   // [0x325D], image@0x3EFBD
        _ => 0x08,                            // [0x325E], image@0x3EFBE
    };

    /// <summary>
    /// The palette index a contact is plotted in: the colour byte, resolved by altitude.
    /// </summary>
    /// <param name="colour">Which class.</param>
    /// <param name="atOrAbovePlayer">
    /// Whether the contact's Y is at or above the player's — the three-way compare at
    /// <c>image@0x0D32C..0x0D344</c>, where EQUAL takes the high nibble (the low-word test is
    /// <c>jb</c>, strictly below).
    /// </param>
    /// <returns>A palette index, 0..15.</returns>
    public static int DotPaletteIndex(MapDotColour colour, bool atOrAbovePlayer)
    {
        int packed = DotByte(colour);
        if ((packed & 0xF0) == 0)
        {
            return packed;                    // image@0x0D324 — no altitude sense
        }

        return atOrAbovePlayer ? packed >> 4 : packed & 0x0F;
    }

    /// <summary>
    /// Where a contact lands inside the face, in design pixels — the shared projector, at the map's
    /// own centre and the shared scale shift.
    /// </summary>
    /// <param name="contact">The contact.</param>
    /// <param name="scaleShift">The live <c>[0x3234]</c>.</param>
    /// <returns>The design point, fractional — a window's contents are drawn at host resolution.</returns>
    /// <remarks>
    /// Identical arithmetic to <see cref="RadarScope.Plot"/> — the same
    /// <c>pixel_obj_world_to_screen_project @image@0x0D3B4</c> serves all three contexts, so the map
    /// rotates with the aeroplane exactly as the scopes do (the projector rotates the world delta by
    /// <c>wrap(−camera heading)</c> at <c>image@0x0D403..0x0D417</c>).  The map is therefore
    /// <b>heading-up, not north-up</b> — and it is measured: at <c>a_mig21_110</c> the camera heading
    /// is 360 units = 45°, and the three hostiles only land on their measured pixels when the delta
    /// is rotated by −45°.
    /// </remarks>
    public static (double X, double Y) Plot(in ScopeContact contact, int scaleShift)
    {
        (int cx, int cy) = Centre;
        return RadarScope.Plot(in contact, cx, cy, scaleShift);
    }

    /// <summary>
    /// Whether the map plots this contact at all: the range gate, then the rectangle.
    /// </summary>
    /// <param name="contact">The contact.</param>
    /// <param name="scaleShift">The live shift.</param>
    /// <returns>True when a dot is drawn for it.</returns>
    /// <remarks>
    /// The REGISTER pass gates on the 2-D Manhattan distance
    /// (<c>pixel_obj_proximity_and_register @image@0x0D482</c>) and then on the projected point
    /// falling inside the clip rectangle (<c>image@0x0D4D4..0x0D4F0</c>); the RENDER pass re-tests
    /// the rectangle every frame (<c>image@0x0D662..0x0D67E</c>).  The port tests both, in the same
    /// order, and skips the original's ten-slot arbitration — see the report's deviations.
    /// </remarks>
    public static bool Shows(in ScopeContact contact, int scaleShift)
    {
        if (contact.ManhattanFeet >= RangeGateFeet(scaleShift))
        {
            return false;
        }

        (double x, double y) = Plot(in contact, scaleShift);
        PanelRect rect = Content;
        return x >= rect.X && x < rect.Right && y >= rect.Y && y < rect.Bottom;
    }
}
