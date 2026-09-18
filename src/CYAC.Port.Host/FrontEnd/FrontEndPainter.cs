using CYAC.Port.Core.Data;
using CYAC.Port.Render;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// Draws the original's front-end screens: the 320×200 design space, the widget bevel, the embossed
/// text, the focus marks and the greyed (stippled) look.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every number in this file was measured off the reference atlas</b>
/// (a captured frame of the original, 320×200 frames in the game's own palette).  A
/// Python transcription of exactly these rules — — rebuilds <c>02_main_menu</c>,
/// <c>02b_main_menu_credits_focused</c> and <c>14_credits</c> with <b>zero</b> mismatching pixels
/// outside the emulator's own mouse pointer, and <c>FrontEndPixelTests</c> asserts the same thing
/// about THIS code.  So it is not a look-alike; it is the original's own layout, drawn at whatever
/// resolution the window has.
/// </para>
/// <para>
/// The relationship to M1's <see cref="Menu.FlightMenuRenderer"/>: the ESC menu bar is a DIFFERENT
/// widget (<c>menu_filled_rect_draw</c>) with its own, lighter bevel ramp, and it is deliberately
/// left untouched here — must keep passing.  What the two share is the idea (design pixels at an
/// integer scale) and the font.
/// </para>
/// </remarks>
public sealed class FrontEndPainter
{
    /// <summary>The design space every front-end screen is laid out in: the game's own frame.</summary>
    public const int DesignWidth = 320;

    /// <summary>Its height.</summary>
    public const int DesignHeight = 200;

    /// <summary>Palette index 7 — the panel and raised-button fill (170,170,170).</summary>
    public const int FillIndex = 7;

    /// <summary>Palette index 23 — a focused (pressed) list button's fill (121,121,121).</summary>
    public const int SunkenFillIndex = 23;

    /// <summary>The RAISED ramp: outer, inner, inner shadow, outer shadow.</summary>
    /// <remarks>Measured on <c>02</c>'s <c>Create Mission</c> button at (12, 93, 130, 15).</remarks>
    public static readonly (int Outer, int Inner, int InnerShadow, int OuterShadow) Raised =
        (16, 15, 26, 0);

    /// <summary>The SUNKEN ramp — the same shape with the light coming from the bottom right.</summary>
    /// <remarks>Measured on <c>02</c>'s focused <c>Fly Historic Mission</c> at (12, 75, 130, 15).</remarks>
    public static readonly (int Outer, int Inner, int InnerShadow, int OuterShadow) Sunken =
        (0, 26, 7, 16);

    /// <summary>Index 27 — the two far corners, <c>(x+w-1, y)</c> and <c>(x, y+h-1)</c>.</summary>
    public const int FarCornerIndex = 27;

    /// <summary>Index 22 — the corner at <c>(x+w-2, y+1)</c>.</summary>
    public const int HighCornerIndex = 22;

    /// <summary>Index 21 — the corner at <c>(x+1, y+h-2)</c>.</summary>
    public const int LowCornerIndex = 21;

    /// <summary>A screen title's ink, and a command button's label ink (85,85,85).</summary>
    public const int TitleInkIndex = 8;

    /// <summary>The emboss under a title or a command label (219,219,219).</summary>
    public const int TitleEmbossIndex = 17;

    /// <summary>An activity-list label's ink when it is not focused — the game's dark teal.</summary>
    public const int ListInkIndex = 189;

    /// <summary>An activity-list label's ink when it IS focused — the pale teal on the sunken fill.</summary>
    public const int ListFocusInkIndex = 179;

    /// <summary>The emboss under a focused list label.</summary>
    public const int ListFocusEmbossIndex = 26;

    /// <summary>
    /// The letterbox around the design space at a non-exact fit: index 26 (73,73,73).
    /// </summary>
    /// <remarks>
    /// The concept asks for "the panel fill's dark tone".  26 is the widget bevel's own inner shadow
    /// — the darkest grey the ramp uses before black — so the border reads as part of the same
    /// object rather than as a hole cut in the screen.  A port choice; the original never shows one,
    /// because it is 320×200 and nothing else.
    /// </remarks>
    public const int BorderIndex = 26;

    /// <summary>
    /// <c>propbold</c> codepoint 0x17 — the solid ► on the LEFT of a focused button.
    /// </summary>
    /// <remarks>
    /// Measured, not assumed: 0x17 is a 4-wide advance with three inked columns forming a triangle
    /// whose apex is on the right, and it reproduces <c>02</c>'s left mark at pen x = 15 exactly.
    /// (M1's 0x14 is a different glyph — the arrow-with-a-tail of the View menu's "Player → Target".)
    /// </remarks>
    public const char FocusMarkLeft = '\u0017';

    /// <summary><c>propbold</c> codepoint 0x18 — the solid ◄ on the RIGHT of a focused button.</summary>
    public const char FocusMarkRight = '\u0018';

    /// <summary>
    /// The two marks as STRINGS, once: <c>char.ToString</c> allocates, and a focused button drew two
    /// of them every frame (48 B per front-end frame, which the hangar's allocation test caught).
    /// </summary>
    public static readonly string FocusMarkLeftText = FocusMarkLeft.ToString();

    /// <summary>The right mark, likewise.</summary>
    public static readonly string FocusMarkRightText = FocusMarkRight.ToString();

    /// <summary>A focused button's ► pen column, relative to the button's left edge.</summary>
    public const int FocusMarkInset = 3;

    /// <summary>A focused button's ◄ pen column is <c>w −</c> this.</summary>
    public const int FocusMarkRightInset = 6;

    /// <summary>A label's pen row inside its button.</summary>
    public const int LabelRow = 4;

    /// <summary>
    /// The integer scale a window of this size draws the 320×200 design at.
    /// </summary>
    /// <param name="width">The window's width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <returns>5 at 1920×1080, 10 at 3840×2160 — never less than 1.</returns>
    public static int AutoScale(int width, int height) =>
        Math.Max(1, Math.Min(width / DesignWidth, height / DesignHeight));

    /// <summary>Where the design space's top-left corner lands in the window, centred.</summary>
    /// <param name="width">The window's width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="scale">Host pixels per design pixel.</param>
    public static (int X, int Y) Origin(int width, int height, int scale) =>
        ((width - (DesignWidth * scale)) / 2, (height - (DesignHeight * scale)) / 2);

    /// <summary>
    /// Puts design-space pixels into the window: whole design pixels at an INTEGER scale, the
    /// 320×200 design centred, everything opaque.
    /// </summary>
    /// <remarks>
    /// Integer for the reason M1b gives: these widgets are made of one-pixel bevel lines and a
    /// one-row emboss under every glyph, and a fractional scale re-quantises each of them
    /// independently until the object stops being the original's.
    /// </remarks>
    public readonly ref struct Surface
    {
        private readonly PixelTarget _target;
        private readonly Span<uint> _frame;
        private readonly IReadOnlyList<Rgb24> _palette;

        /// <summary>Wraps a window.</summary>
        /// <param name="target">The whole window.</param>
        /// <param name="palette">The game's 256-colour palette, widened to 8 bits.</param>
        /// <param name="scale">Host pixels per design pixel; ≥ 1.</param>
        /// <param name="originX">Where design column 0 starts, in window pixels.</param>
        /// <param name="originY">Where design row 0 starts.</param>
        public Surface(
            PixelTarget target, IReadOnlyList<Rgb24> palette, int scale, int originX, int originY)
            : this(target, default, palette, scale, originX, originY)
        {
        }

        /// <summary>
        /// Wraps a window and keeps its raw pixels, so a screen may hand one design rectangle
        /// of it to the 3-D renderer (<see cref="Region"/>).
        /// </summary>
        /// <param name="target">The whole window.</param>
        /// <param name="frame">
        /// The same pixels <paramref name="target"/> was built over, column-major with
        /// <c>target.ColumnStride</c> between columns.  An empty span means "no region may be
        /// taken", which is what the pixel tests that render into their own buffer pass.
        /// </param>
        /// <param name="palette">The game's 256-colour palette, widened to 8 bits.</param>
        /// <param name="scale">Host pixels per design pixel; ≥ 1.</param>
        /// <param name="originX">Where design column 0 starts, in window pixels.</param>
        /// <param name="originY">Where design row 0 starts.</param>
        public Surface(
            PixelTarget target,
            Span<uint> frame,
            IReadOnlyList<Rgb24> palette,
            int scale,
            int originX,
            int originY)
        {
            _target = target;
            _frame = frame;
            _palette = palette;
            Scale = Math.Max(1, scale);
            OriginX = originX;
            OriginY = originY;
        }

        /// <summary>Whether <see cref="Region"/> can be taken from this surface.</summary>
        public bool HasFrame => !_frame.IsEmpty;

        /// <summary>
        /// One design rectangle of the window as a <see cref="PixelTarget"/>, at the HOST's own pixel
        /// pitch: what the hangar's 3-D column is rendered straight into.
        /// </summary>
        /// <remarks>
        /// No offscreen buffer and no copy — the sub-target shares the window's column stride, so the
        /// scene renderer writes the panel's pixels where they already are and a steady-state 3-D
        /// frame allocates nothing.  The rectangle is clipped to the window; an empty one (the
        /// design space is entirely off-screen, or this surface carries no frame) comes back as
        /// <see langword="false"/>.
        /// </remarks>
        /// <param name="x">Design column of the rectangle's left edge.</param>
        /// <param name="y">Design row of its top edge.</param>
        /// <param name="w">Its width in design columns.</param>
        /// <param name="h">Its height in design rows.</param>
        /// <param name="region">The sub-target, when there is one.</param>
        public bool Region(int x, int y, int w, int h, out PixelTarget region)
        {
            region = default;
            if (_frame.IsEmpty || w <= 0 || h <= 0)
            {
                return false;
            }

            int x0 = Math.Max(0, OriginX + (x * Scale));
            int y0 = Math.Max(0, OriginY + (y * Scale));
            int x1 = Math.Min(_target.Width, OriginX + ((x + w) * Scale));
            int y1 = Math.Min(_target.Height, OriginY + ((y + h) * Scale));
            if (x1 <= x0 || y1 <= y0)
            {
                return false;
            }

            int stride = _target.ColumnStride;
            int start = (x0 * stride) + y0;
            int length = ((x1 - x0 - 1) * stride) + (y1 - y0);
            region = new PixelTarget(
                _frame.Slice(start, length), x1 - x0, y1 - y0, stride, _target.Order);
            return true;
        }

        /// <summary>Host pixels per design pixel.</summary>
        public int Scale { get; }

        /// <summary>Where design column 0 starts, in window pixels.</summary>
        public int OriginX { get; }

        /// <summary>Where design row 0 starts, in window pixels.</summary>
        public int OriginY { get; }

        /// <summary>Paints the WHOLE window one colour — the letterbox, before the design.</summary>
        /// <param name="index">The palette index.</param>
        public void Clear(int index)
        {
            uint colour = _target.Encode(Colour(index));
            for (int x = 0; x < _target.Width; x++)
            {
                _target.Column(x).Fill(colour);
            }
        }

        /// <summary>Fills one design-space rectangle.</summary>
        /// <param name="x">Design column.</param>
        /// <param name="y">Design row.</param>
        /// <param name="w">Width in design columns.</param>
        /// <param name="h">Height in design rows.</param>
        /// <param name="index">The palette index.</param>
        public void Fill(int x, int y, int w, int h, int index)
        {
            if (w <= 0 || h <= 0)
            {
                return;
            }

            uint colour = _target.Encode(Colour(index));
            int x0 = Math.Max(0, OriginX + (x * Scale));
            int x1 = Math.Min(_target.Width, OriginX + ((x + w) * Scale));
            int y0 = Math.Max(0, OriginY + (y * Scale));
            int y1 = Math.Min(_target.Height, OriginY + ((y + h) * Scale));
            for (int column = x0; column < x1; column++)
            {
                _target.Column(column).Slice(y0, y1 - y0).Fill(colour);
            }
        }

        /// <summary>Fills one design-space pixel.</summary>
        /// <param name="x">Design column.</param>
        /// <param name="y">Design row.</param>
        /// <param name="index">The palette index.</param>
        public void Pixel(int x, int y, int index) => Fill(x, y, 1, 1, index);

        private Rgb24 Colour(int index) =>
            (uint)index < (uint)_palette.Count ? _palette[index] : new Rgb24(255, 255, 255);
    }

    /// <summary>Blits a 320×200 <c>.pic</c> over the whole design space.</summary>
    /// <param name="surface">The surface.</param>
    /// <param name="backdrop">The picture's palette indices (320×200).</param>
    /// <exception cref="ArgumentException">The picture is not 320×200.</exception>
    /// <remarks>
    /// The backdrops (<c>largev</c>, <c>smallv</c>, <c>credv</c>) are VGA-256 <c>.pic</c> screens
    /// whose documents declare <c>palette.binding: default-vga</c>, i.e. they are drawn in the SAME
    /// palette <c>Program.ReadPalette</c> reads — no per-screen palette document is needed.
    /// </remarks>
    public static void Backdrop(in Surface surface, in IndexedImage backdrop)
    {
        if (backdrop.Width != DesignWidth || backdrop.Height != DesignHeight)
        {
            throw new ArgumentException(
                $"a front-end backdrop must be {DesignWidth}×{DesignHeight}, this one is "
                    + $"{backdrop.Width}×{backdrop.Height}",
                nameof(backdrop));
        }

        byte[] indices = backdrop.Indices;
        for (int y = 0; y < DesignHeight; y++)
        {
            int row = y * DesignWidth;
            int x = 0;
            while (x < DesignWidth)
            {
                // Run-length the row: a .pic screen is mostly flat runs, and one Fill per run costs
                // far less than one per design pixel at scale 10.
                byte index = indices[row + x];
                int run = 1;
                while (x + run < DesignWidth && indices[row + x + run] == index)
                {
                    run++;
                }

                surface.Fill(x, y, run, 1, index);
                x += run;
            }
        }
    }

    /// <summary>
    /// Blits one rectangle of an indexed picture, opaquely (the hangar's side-view sheets).
    /// </summary>
    /// <remarks>
    /// <c>plane_silhouette_draw @image@0x271E7</c> ends in <c>gfx_plain_blit</c> — a PLAIN blit, with
    /// no colour key: the whole 112 × <c>height</c> rectangle is copied, background and all.  It is
    /// invisible because the sheets' background is palette index 20, which is RGB-identical to the
    /// panel's own index 7 (one of the six duplicate entries in the shipped palette, the same
    /// aliasing F1 found in <c>largev</c>).  Reproducing the plain blit rather than colour-keying it
    /// is what makes the diff against the original's screen exact — and it is what the original does.
    /// </remarks>
    /// <param name="surface">The surface.</param>
    /// <param name="picture">The source picture's palette indices.</param>
    /// <param name="sourceX">The rectangle's left column in the picture.</param>
    /// <param name="sourceY">Its top row.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="destinationX">The design column its left edge lands on.</param>
    /// <param name="destinationY">The design row its top edge lands on.</param>
    public static void Blit(
        in Surface surface,
        in IndexedImage picture,
        int sourceX,
        int sourceY,
        int width,
        int height,
        int destinationX,
        int destinationY)
    {
        byte[] indices = picture.Indices;
        for (int row = 0; row < height; row++)
        {
            int sy = sourceY + row;
            if ((uint)sy >= (uint)picture.Height)
            {
                continue;
            }

            int line = sy * picture.Width;
            int column = 0;
            while (column < width)
            {
                int sx = sourceX + column;
                if ((uint)sx >= (uint)picture.Width)
                {
                    break;
                }

                // Run-length the row, exactly as Backdrop does: one Fill per run instead of one per
                // design pixel keeps the cost sane at scale 10.
                byte index = indices[line + sx];
                int run = 1;
                while (column + run < width
                    && sourceX + column + run < picture.Width
                    && indices[line + sourceX + column + run] == index)
                {
                    run++;
                }

                surface.Fill(destinationX + column, destinationY + row, run, 1, index);
                column += run;
            }
        }
    }

    /// <summary>
    /// The same plain blit, MIRRORED left-to-right about the rectangle's own centre: the TACTICS
    /// screen's ally silhouette.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>plane_silhouette_draw @image@0x271E7</c> takes a flip flag and, when it is set, calls
    /// <c>gfx_hflip_per_mode</c> on the WHOLE atlas buffer before the blit
    /// (<c>image@0x2724A</c>) and again afterwards to put it back (<c>image@0x27292</c>).  The
    /// buffer is the 112-column sheet and the blit copies all 112 columns, so the visible effect is
    /// exactly a mirror of the row about its own centre — which is what this does, without touching
    /// the shared sheet.
    /// </para>
    /// <para>
    /// It is the <b>ALLY</b> (left) column that is flipped, not the enemy:
    /// <c>ui_plane_comparison_screen</c> pushes <c>cl = 1</c> for the left block
    /// (<c>image@0x26E79</c>) and <c>al = 0</c> for the right (<c>image@0x26E94</c>), and
    /// the original's own screen agrees — the P-51's markings read <c>V … 08</c>, the mirror of the
    /// hangar's <c>20 … Y</c>.  The two aeroplanes end up nose to nose.
    /// </para>
    /// </remarks>
    /// <param name="surface">The surface.</param>
    /// <param name="picture">The source picture's palette indices.</param>
    /// <param name="sourceX">The rectangle's left column in the picture.</param>
    /// <param name="sourceY">Its top row.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="destinationX">The design column its left edge lands on.</param>
    /// <param name="destinationY">The design row its top edge lands on.</param>
    public static void BlitMirrored(
        in Surface surface,
        in IndexedImage picture,
        int sourceX,
        int sourceY,
        int width,
        int height,
        int destinationX,
        int destinationY)
    {
        byte[] indices = picture.Indices;
        for (int row = 0; row < height; row++)
        {
            int sy = sourceY + row;
            if ((uint)sy >= (uint)picture.Height)
            {
                continue;
            }

            int line = sy * picture.Width;
            int column = 0;
            while (column < width)
            {
                int sx = sourceX + width - 1 - column;
                if ((uint)sx >= (uint)picture.Width)
                {
                    column++;
                    continue;
                }

                byte index = indices[line + sx];
                int run = 1;
                while (column + run < width
                    && sx - run >= 0
                    && indices[line + sx - run] == index)
                {
                    run++;
                }

                surface.Fill(destinationX + column, destinationY + row, run, 1, index);
                column += run;
            }
        }
    }

    /// <summary>Draws one widget bevel with its fill.</summary>
    /// <param name="surface">The surface.</param>
    /// <param name="x">Design column of its left edge.</param>
    /// <param name="y">Design row of its top edge.</param>
    /// <param name="w">Its width in design columns.</param>
    /// <param name="h">Its height in design rows.</param>
    /// <param name="ramp"><see cref="Raised"/> or <see cref="Sunken"/>.</param>
    /// <param name="fill">The interior's palette index.</param>
    public static void Bevel(
        in Surface surface,
        int x,
        int y,
        int w,
        int h,
        (int Outer, int Inner, int InnerShadow, int OuterShadow) ramp,
        int fill)
    {
        surface.Fill(x + 2, y + 2, w - 4, h - 4, fill);
        surface.Fill(x, y, w - 1, 1, ramp.Outer);
        surface.Pixel(x + w - 1, y, FarCornerIndex);
        surface.Fill(x, y, 1, h - 1, ramp.Outer);
        surface.Pixel(x, y + h - 1, FarCornerIndex);
        surface.Fill(x + 1, y + 1, w - 3, 1, ramp.Inner);
        surface.Pixel(x + w - 2, y + 1, HighCornerIndex);
        surface.Fill(x + 1, y + 1, 1, h - 3, ramp.Inner);
        surface.Pixel(x + 1, y + h - 2, LowCornerIndex);
        surface.Fill(x + 2, y + h - 2, w - 3, 1, ramp.InnerShadow);
        surface.Fill(x + w - 2, y + 2, 1, h - 3, ramp.InnerShadow);
        surface.Fill(x + 1, y + h - 1, w - 1, 1, ramp.OuterShadow);
        surface.Fill(x + w - 1, y + 1, 1, h - 1, ramp.OuterShadow);
    }

    /// <summary>The lit edge of a ONE-pixel bevel (top and left of a raised widget).</summary>
    public const int ThinEdgeLightIndex = 15;

    /// <summary>Its shaded edge (bottom and right of a raised widget).</summary>
    public const int ThinEdgeDarkIndex = 26;

    /// <summary>Its <c>(x+w−1, y)</c> corner, where the two edges meet.</summary>
    public const int ThinCornerTopRightIndex = 18;

    /// <summary>Its <c>(x, y+h−1)</c> corner.</summary>
    public const int ThinCornerBottomLeftIndex = 24;

    /// <summary>
    /// <c>widget_beveled_box_draw @image@0x2EAA3</c>'s look: a ONE-pixel bevel drawn AROUND the
    /// widget's own rectangle, with the two odd corners.
    /// </summary>
    /// <remarks>
    /// Measured off a captured frame of the original: the CREATE
    /// MISSION picker panel <c>[0x4BE8] = (21,75,220,83)</c> is drawn as (20,74,222,85), and every
    /// picker row's <c>(x, y, col_width, 11)</c> as its own rectangle grown by one.  It is the same
    /// object MISSION SELECTION's mission rows wear (F2's own <c>RowEdgeLight</c> / <c>RowEdgeDark</c>
    /// / corner 18 / corner 24), which is why the indices agree.  A FOCUSED row swaps the two edge
    /// colours and takes the sunken fill, exactly as a list button does.
    /// </remarks>
    /// <param name="surface">The surface.</param>
    /// <param name="x">The widget rectangle's left edge.</param>
    /// <param name="y">Its top edge.</param>
    /// <param name="w">Its width.</param>
    /// <param name="h">Its height.</param>
    /// <param name="light">The lit edge's index (top + left).</param>
    /// <param name="dark">The shaded edge's index (bottom + right).</param>
    /// <param name="fill">The interior's index.</param>
    public static void ThinBevel(
        in Surface surface, int x, int y, int w, int h, int light, int dark, int fill)
    {
        int bx = x - 1;
        int by = y - 1;
        int bw = w + 2;
        int bh = h + 2;
        surface.Fill(x, y, w, h, fill);
        surface.Fill(bx, by, bw - 1, 1, light);
        surface.Pixel(bx + bw - 1, by, ThinCornerTopRightIndex);
        surface.Fill(bx, by, 1, bh - 1, light);
        surface.Pixel(bx, by + bh - 1, ThinCornerBottomLeftIndex);
        surface.Fill(bx + 1, by + bh - 1, bw - 1, 1, dark);
        surface.Fill(bx + bw - 1, by + 1, 1, bh - 1, dark);
    }

    /// <summary>
    /// Draws one embossed string: the whole string one row down in the emboss colour, then again in
    /// the ink colour — the engine's own text routine.
    /// </summary>
    /// <param name="surface">The surface.</param>
    /// <param name="font">The game's <c>propbold</c>.</param>
    /// <param name="text">The line.</param>
    /// <param name="x">Design column of its pen.</param>
    /// <param name="y">Design row of its pen.</param>
    /// <param name="ink">The ink index.</param>
    /// <param name="emboss">The emboss index.</param>
    /// <param name="stippled">
    /// True for a DISABLED widget: a glyph pixel survives only where <c>(x + y)</c> is odd.
    /// </param>
    /// <param name="tabStop">
    /// Where a <c>\t</c> in the string moves the pen to, in design columns; −1 (the default) draws
    /// the tab as an ordinary glyph.  The MISSION STATS labels (<c>strings.json</c> 70..77) all end
    /// in <c>\t</c> and their values line up on one column.
    /// </param>
    public static void Text(
        in Surface surface,
        CockpitFont font,
        string text,
        int x,
        int y,
        int ink,
        int emboss,
        bool stippled = false,
        int tabStop = -1)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);
        Glyphs(surface, font, text, x, y + 1, emboss, stippled, tabStop);
        Glyphs(surface, font, text, x, y, ink, stippled, tabStop);
    }

    /// <summary>How wide a string is in design columns.</summary>
    /// <param name="font">The game's <c>propbold</c>.</param>
    /// <param name="text">The string.</param>
    public static int Measure(CockpitFont font, string text)
    {
        ArgumentNullException.ThrowIfNull(font);
        return font.Measure(text);
    }

    /// <summary>
    /// An embossed title with the underline rule the engine draws beneath it: the string, then a
    /// one-row ink line the string's own width with its emboss one row below.
    /// </summary>
    /// <remarks>
    /// The same shape F1 drew by hand for CHOOSE ACTIVITY's title; the historic-chain screens reuse it
    /// for the screen title (MISSION SELECTION), each mission row's title, and the description's title.
    /// </remarks>
    /// <param name="surface">The surface.</param>
    /// <param name="font">The caption font.</param>
    /// <param name="text">The title.</param>
    /// <param name="x">Design column of the pen.</param>
    /// <param name="y">Design row of the pen.</param>
    /// <param name="ink">The ink index.</param>
    /// <param name="emboss">The emboss index.</param>
    public static void Rule(
        in Surface surface, CockpitFont font, string text, int x, int y, int ink, int emboss)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);
        Text(surface, font, text, x, y, ink, emboss);
        int width = font.Measure(text);
        surface.Fill(x, y + font.Height + 1, width, 1, emboss);
        surface.Fill(x, y + font.Height, width, 1, ink);
    }

    /// <summary>
    /// Blits one cell of the national-insignia strip (<c>insigv.pic</c>), colour-keyed.
    /// </summary>
    /// <remarks>
    /// <c>insigv</c> is a 128×11 VGA-256 <c>.pic</c> holding four 32-wide cells indexed by
    /// <c>MissionEntry.InsigniaIndex</c> (source-x = <c>idx &lt;&lt; 5</c>, <c>image@0x256C2</c>).  Its
    /// background is palette index 9, which is TRANSPARENT: a pixel is drawn only where it is not 9, so
    /// the mission-row's box fill (or the description bar's) shows through the roundel's gaps.  Cell
    /// width, origin and the key were all measured against the original's screen.
    /// </remarks>
    /// <param name="surface">The surface.</param>
    /// <param name="strip">The <c>insigv</c> pixels (128×11).</param>
    /// <param name="cellIndex">Which insignia (0..3).</param>
    /// <param name="cellWidth">The stride between cells: 32.</param>
    /// <param name="transparentIndex">The colour-key index: 9.</param>
    /// <param name="ox">Design column the cell's left edge lands on.</param>
    /// <param name="oy">Design row the cell's top edge lands on.</param>
    public static void InsigniaStrip(
        in Surface surface,
        in IndexedImage strip,
        int cellIndex,
        int cellWidth,
        int transparentIndex,
        int ox,
        int oy)
    {
        byte[] indices = strip.Indices;
        int sx = cellIndex * cellWidth;
        for (int gy = 0; gy < strip.Height; gy++)
        {
            int row = gy * strip.Width;
            for (int gx = 0; gx < cellWidth && sx + gx < strip.Width; gx++)
            {
                int index = indices[row + sx + gx];
                if (index != transparentIndex)
                {
                    surface.Pixel(ox + gx, oy + gy, index);
                }
            }
        }
    }

    private static void Glyphs(
        in Surface surface,
        CockpitFont font,
        string text,
        int x,
        int y,
        int index,
        bool stippled,
        int tabStop = -1)
    {
        int pen = x;
        foreach (char c in text)
        {
            if (c == '\t' && tabStop >= 0)
            {
                pen = tabStop;
                continue;
            }

            int advance = font.Advance(c);
            for (int gy = 0; gy < font.Height; gy++)
            {
                for (int gx = 0; gx < advance; gx++)
                {
                    if (!font.Ink(c, gx, gy))
                    {
                        continue;
                    }

                    if (stippled && (pen + gx + y + gy) % 2 == 0)
                    {
                        continue;
                    }

                    surface.Pixel(pen + gx, y + gy, index);
                }
            }

            pen += advance;
        }
    }
}
