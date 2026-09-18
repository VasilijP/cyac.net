using CYAC.Port.Render;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host.Menu;

/// <summary>
/// Draws the in-flight menu bar the way the 1991 engine draws it, at host resolution.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every number in this file was measured off the six screenshots</b>
/// a captured frame of the original, which are clean 2× nearest upscales of the 320×200
/// frame (verified: not one of their 64,000 2×2 blocks is non-uniform), so their colours are real
/// palette indices once widened the way the VGA DAC widens six bits — <c>(v &lt;&lt; 2) | (v &gt;&gt; 4)</c>.
/// A Python transcription of exactly the rules below reproduces all six pull-downs
/// <b>pixel for pixel</b>: 112,160 menu pixels, zero mismatches.  So this is not a
/// look-alike; it is the original's own layout, drawn at whatever resolution the window has.
/// </para>
/// <para>
/// Design space is the game's 320×200 and the mapping to the window is an <b>integer</b>
/// number of host pixels per design pixel, <see cref="AutoScale"/> or <c>--menu-scale</c>, anchored
/// at the window's top-left.  5.4 host rows per design row at 1080p made the 13-row strip 70 px tall and the
/// popup a third of the screen — humongous — so it is scaled down to a quarter of that.  The
/// scale is integer because the drawing rules are pixel-exact at 1×: a
/// fractional scale re-quantises every bevel line and every glyph column and the object stops being
/// the original's.  Only the strip's WIDTH is resolution-dependent — it is a bar and spans the
/// window — while the titles, the popups and the About box stay in the original's 320-column
/// layout.
/// </para>
/// <para>
/// The one thing the original does that the port deliberately does NOT is save and restore the pixels under
/// the popup (<c>menu_popup_open</c> / <c>menu_popup_close</c>): the port re-renders the whole frozen scene
/// every presented frame instead, which is what lets a render option changed from the menu show up at once on
/// the SAME frame.
/// </para>
/// </remarks>
public sealed class FlightMenuRenderer
{
    /// <summary>The design-space width the whole menu is laid out in.</summary>
    public const int DesignWidth = 320;

    /// <summary>The design-space height the game composes its frame in.</summary>
    public const int DesignHeight = 200;

    /// <summary>
    /// The divisor in <see cref="AutoScale"/>: a quarter of the current pixel size.
    /// </summary>
    public const int AutoScaleDivisor = 4;

    /// <summary>The largest <c>--menu-scale</c> the "Menu Size" row cycles to before it wraps to auto.</summary>
    public const int MaxCycledScale = 4;

    /// <summary>The top strip's height in design rows — <c>[0xB7AC]</c> is forced to 0xD.</summary>
    public const int BarHeight = 13;

    /// <summary>One popup row's pitch in design rows.</summary>
    public const int RowHeight = 11;

    /// <summary>Design pixels of padding either side of a title inside its strip box.</summary>
    public const int TitlePadding = 4;

    /// <summary>Where a popup's check-mark glyph starts, relative to the popup's left edge.</summary>
    public const int GutterX = 8;

    /// <summary>Where a popup's label starts, relative to the popup's left edge (gutter + 8).</summary>
    public const int LabelX = 16;

    /// <summary>How far a popup's right-aligned accelerator text ends short of its right edge.</summary>
    public const int AcceleratorMargin = 8;

    /// <summary>The width a popup adds to its widest row: 16 left of the label, 8 right.</summary>
    public const int PopupPadding = 24;

    /// <summary>The gap between a label and its accelerator when that pair is the widest row.</summary>
    public const int AcceleratorGap = 16;

    /// <summary>
    /// The rightmost design column a popup's own right border may reach.
    /// </summary>
    /// <remarks>
    /// Measured: five of the six popups open exactly under their title box, but Help's opens at
    /// x = 143 while its title box is at x = 151, and 143 + 164 = 307.  The engine's own constant is
    /// <b>(open)</b> — 307 is 320 − 13, and 13 is the strip height, but one sample cannot tell a
    /// coincidence from a derivation.  What is certain is the observed result, and this reproduces
    /// it (a captured frame of the original).
    /// </remarks>
    public const int PopupRightLimit = 307;

    /// <summary>The "a help feature has been used this mission" square: 4 × 4 at (311, 4).</summary>
    /// <remarks>
    /// Visible in all six screenshots (that session had cheats on).  The manual (p.24) makes it the
    /// Ace's-Challenge disqualifier: using any Help item marks the sortie.
    /// </remarks>
    public static readonly (int X, int Y, int Size) HelpUsedMark = (311, 4, 4);

    /// <summary>Palette index 7 — the light grey the strip and the popups are filled with.</summary>
    public const int FillIndex = 7;

    /// <summary>Palette index 15 — the bevel's outer highlight (top and left).</summary>
    public const int HighlightIndex = 15;

    /// <summary>Palette index 18 — the bevel's inner highlight (the second left column).</summary>
    public const int InnerHighlightIndex = 18;

    /// <summary>Palette index 8 — the bevel's inner shadow (bottom and right), and the text colour.</summary>
    public const int ShadowIndex = 8;

    /// <summary>Palette index 21 — the single corner pixel where the two inner bevels meet.</summary>
    public const int CornerIndex = 21;

    /// <summary>Palette index 0 — the bevel's outer shadow and the drop shadow.</summary>
    public const int BlackIndex = 0;

    /// <summary>Palette index 17 — the emboss under normal text.</summary>
    public const int TextEmbossIndex = 17;

    /// <summary>Palette index 28 — the selection bar, and a section rule.</summary>
    public const int SelectionIndex = 28;

    /// <summary>Palette index 26 — the emboss under selected (white) text.</summary>
    public const int SelectedEmbossIndex = 26;

    /// <summary>
    /// Palette index 21 — a DISABLED row's text.  A port addition: the original has no disabled
    /// state, so the colour is chosen, not measured (it is the grey ramp entry two steps down from
    /// the panel fill, which reads as "there but not for you").
    /// </summary>
    public const int DisabledTextIndex = 21;

    /// <summary>Palette index 24 — a disabled row's text when it is the HIGHLIGHTED one.</summary>
    public const int DisabledSelectedTextIndex = 24;

    private readonly List<(int X, int Width)> _titleBoxes = new(FlightMenuTable.MenuCount);

    /// <summary>How many design pixels the last frame's popup was wide.</summary>
    public int LastPopupWidth { get; private set; }

    /// <summary>Where the last frame's popup started, in design columns.</summary>
    public int LastPopupX { get; private set; }

    /// <summary>
    /// How many host pixels one design pixel is when <c>--menu-scale</c> says <c>auto</c>.
    /// </summary>
    /// <param name="width">The window's width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <returns>1 at 1920 × 1080, 2 at 3840 × 2160 — never less than 1.</returns>
    /// <remarks>
    /// <para>
    /// The cockpit's own design scale is <c>min(width / 320, height / 200)</c> — 5.4 at 1080p and
    /// 10.8 at 4K — and the menu is drawn at a QUARTER of that pixel size.  A quarter of
    /// 5.4 is 1.35 and a quarter of 10.8 is 2.7, so the integer taken is the FLOOR: the menu is never
    /// bigger than the quarter that was asked for, which is the direction the complaint came from
    /// ("it is humongous").  Rounding would give 3 at 4K, i.e. 1.11× the requested size.
    /// </para>
    /// <para>
    /// The result is an integer because the menu is a bitmap object: a fractional scale re-quantises
    /// the one-pixel bevel lines and the glyph columns that the six screenshots proved exact
    ///, and a 1.35× strip has bevel rows one pixel tall in some places and two in
    /// others.  Nearest, whole design pixels, anchored top-left.
    /// </para>
    /// </remarks>
    public static int AutoScale(int width, int height)
    {
        double design = Math.Min(width / (double)DesignWidth, height / (double)DesignHeight);
        return Math.Max(1, (int)(design / AutoScaleDivisor));
    }

    /// <summary>
    /// Draws the strip and the open menu's popup over the frame.
    /// </summary>
    /// <param name="target">The whole window.</param>
    /// <param name="font">The game's own <c>propbold</c> font.</param>
    /// <param name="palette">The game's 256-colour VGA palette, widened to 8 bits.</param>
    /// <param name="state">Which menu is popped and which row is highlighted.</param>
    /// <param name="look">Per-row check marks, enablement and the panel opacity.</param>
    /// <param name="scale">host pixels per design pixel; ≥ 1.</param>
    public void Render(
        PixelTarget target,
        CockpitFont font,
        IReadOnlyList<Rgb24> palette,
        FlightMenuState state,
        in FlightMenuLook look,
        int scale)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);

        Painter painter = new Painter(target, scale, palette, look.PanelOpacity);

        // The strip is a BAR: it spans the window at any scale, so its design width is however many
        // design columns the window holds (320 exactly when the scale is the original's own 1:1 on
        // a 320-wide screen).  Everything INSIDE it — the title boxes from design column 8, the
        // popups, the 307 clamp — stays in the original's 320-column layout.
        int barWidth = BarDesignWidth(target.Width, scale);

        MeasureTitles(font, state);
        Bevel(painter, 0, 0, barWidth, BarHeight);
        for (int i = 0; i < _titleBoxes.Count; i++)
        {
            (int bx, int bw) = _titleBoxes[i];
            bool open = i == state.MenuIndex;
            if (open)
            {
                painter.Fill(bx, 1, bw, BarHeight - 3, SelectionIndex);
            }

            Text(
                painter,
                font,
                state.Menus[i].Title,
                bx + TitlePadding,
                2,
                open ? HighlightIndex : ShadowIndex,
                open ? SelectedEmbossIndex : TextEmbossIndex);
        }

        if (look.HelpUsed)
        {
            // The square is 9 design columns short of the strip's RIGHT end (311 of 320), and the
            // strip's right end is now the window's, so it follows the bar out there.
            painter.Fill(
                barWidth - (DesignWidth - HelpUsedMark.X),
                HelpUsedMark.Y,
                HelpUsedMark.Size,
                HelpUsedMark.Size,
                BlackIndex);
        }

        FlightMenu menu = state.Menu;
        int width = PopupWidth(font, menu);
        int x = Math.Min(_titleBoxes[state.MenuIndex].X, PopupRightLimit - width);
        int height = (RowHeight * menu.Items.Count) + 3;
        int y = BarHeight - 1;
        LastPopupWidth = width;
        LastPopupX = x;

        // The drop shadow: two columns down the right, one row along the bottom, offset the way the
        // 320×200 pixel aspect makes symmetric (two horizontal ≈ one vertical).
        for (int row = y + 3; row <= y + height; row++)
        {
            painter.Pixel(x + width, row, BlackIndex);
            painter.Pixel(x + width + 1, row, BlackIndex);
        }

        for (int column = x + 3; column <= x + width + 1; column++)
        {
            painter.Pixel(column, y + height, BlackIndex);
        }

        Bevel(painter, x, y, width, height);

        for (int i = 0; i < menu.Items.Count; i++)
        {
            FlightMenuItem item = menu.Items[i];
            int rowY = y + 1 + (RowHeight * i);
            bool selected = i == state.ItemIndex;
            bool enabled = FlightMenuLook.IsEnabled(item.Id);
            if (selected)
            {
                painter.Fill(x + 2, rowY, width - 4, RowHeight, SelectionIndex);
            }

            int ink = (selected, enabled) switch
            {
                (true, true) => HighlightIndex,
                (true, false) => DisabledSelectedTextIndex,
                (false, true) => ShadowIndex,
                (false, false) => DisabledTextIndex,
            };
            int emboss = selected ? SelectedEmbossIndex : TextEmbossIndex;

            if (look.IsChecked(item.Id))
            {
                Text(painter, font, CheckGlyph, x + GutterX, rowY + 2, ink, emboss);
            }

            Text(painter, font, item.Label, x + LabelX, rowY + 2, ink, emboss);
            if (item.Accelerator is { } accelerator)
            {
                Text(
                    painter,
                    font,
                    accelerator,
                    x + width - AcceleratorMargin - font.Measure(accelerator),
                    rowY + 2,
                    ink,
                    emboss);
            }

            // The section rule goes ON TOP of the row's text: "High Detail" and "Target Info" have
            // descenders whose emboss reaches the rule's own row, and in the screenshots the rule
            // wins (yeager_051 row 45, yeager_052 row 78).
            if (item.SectionEnd && !selected)
            {
                painter.Fill(x + 2, rowY + RowHeight - 1, width - 4, 1, SelectionIndex);
            }
        }
    }

    /// <summary>Draws one bevelled panel with its drop shadow — what the About box is built from.</summary>
    /// <param name="target">The whole window.</param>
    /// <param name="palette">The game's palette.</param>
    /// <param name="x">Design column of its left edge.</param>
    /// <param name="y">Design row of its top edge.</param>
    /// <param name="w">Its width in design columns.</param>
    /// <param name="h">Its height in design rows.</param>
    /// <param name="opacity">The panel opacity.</param>
    /// <param name="scale">host pixels per design pixel.</param>
    public void DrawPanel(
        PixelTarget target,
        IReadOnlyList<Rgb24> palette,
        int x,
        int y,
        int w,
        int h,
        double opacity,
        int scale)
    {
        ArgumentNullException.ThrowIfNull(palette);
        Painter painter = new Painter(target, scale, palette, opacity);
        for (int row = y + 3; row <= y + h; row++)
        {
            painter.Pixel(x + w, row, BlackIndex);
            painter.Pixel(x + w + 1, row, BlackIndex);
        }

        for (int column = x + 3; column <= x + w + 1; column++)
        {
            painter.Pixel(column, y + h, BlackIndex);
        }

        Bevel(painter, x, y, w, h);
    }

    /// <summary>
    /// Fills one design-space rectangle: a selection bar, a section rule, a colour swatch.
    /// </summary>
    /// <param name="target">The whole window.</param>
    /// <param name="palette">The game's palette.</param>
    /// <param name="x">Design column of its left edge.</param>
    /// <param name="y">Design row of its top edge.</param>
    /// <param name="w">Its width in design columns.</param>
    /// <param name="h">Its height in design rows.</param>
    /// <param name="index">The palette index.</param>
    /// <param name="opacity">The panel opacity.</param>
    /// <param name="scale">Host pixels per design pixel.</param>
    /// <param name="opaque">True to ignore the opacity, the way text does.</param>
    public void FillRect(
        PixelTarget target,
        IReadOnlyList<Rgb24> palette,
        int x,
        int y,
        int w,
        int h,
        int index,
        double opacity,
        int scale,
        bool opaque = false)
    {
        ArgumentNullException.ThrowIfNull(palette);
        new Painter(target, scale, palette, opacity).Fill(x, y, w, h, index, opaque);
    }

    /// <summary>Draws one embossed string in design space — what the About box's lines are.</summary>
    /// <param name="target">The whole window.</param>
    /// <param name="font">The game's own font.</param>
    /// <param name="palette">The game's palette.</param>
    /// <param name="text">The line.</param>
    /// <param name="x">Design column of its left edge.</param>
    /// <param name="y">Design row of its top edge.</param>
    /// <param name="ink">The palette index the glyphs are drawn in.</param>
    /// <param name="emboss">The index the one-row-down pass is drawn in.</param>
    /// <param name="opacity">The panel opacity (text itself is always opaque).</param>
    /// <param name="scale">host pixels per design pixel.</param>
    public void DrawText(
        PixelTarget target,
        CockpitFont font,
        IReadOnlyList<Rgb24> palette,
        string text,
        int x,
        int y,
        int ink,
        int emboss,
        double opacity,
        int scale)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(palette);
        Text(new Painter(target, scale, palette, opacity), font, text, x, y, ink, emboss);
    }

    /// <summary>
    /// How many design columns the strip spans on a window this wide: the whole window, rounded
    /// UP so its right bevel is never left short of the last host column.
    /// </summary>
    /// <param name="windowWidth">The window's width in pixels.</param>
    /// <param name="scale">Host pixels per design pixel.</param>
    public static int BarDesignWidth(int windowWidth, int scale) =>
        Math.Max(DesignWidth, (windowWidth + scale - 1) / scale);

    /// <summary>
    /// A menu's popup width: 24 design pixels of furniture plus its widest row, where a row with an
    /// accelerator is label + 16 + accelerator.
    /// </summary>
    /// <param name="font">The game's own font.</param>
    /// <param name="menu">The menu.</param>
    /// <remarks>Reproduces all six shipped widths exactly (157/102/168/129/164/101).</remarks>
    public static int PopupWidth(CockpitFont font, FlightMenu menu)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(menu);
        int widest = 0;
        foreach (FlightMenuItem item in menu.Items)
        {
            int row = font.Measure(item.Label);
            if (item.Accelerator is { } accelerator)
            {
                row += AcceleratorGap + font.Measure(accelerator);
            }

            widest = Math.Max(widest, row);
        }

        return PopupPadding + widest;
    }

    /// <summary>The check-mark glyph, <c>propbold.fnt</c> codepoint 0x16, as a one-character string.</summary>
    private const string CheckGlyph = "\u0016";

    /// <summary>
    /// The six title boxes: the first starts at design column 8 and each is its title's width plus
    /// eight, laid end to end.  Reproduces all six shipped boxes exactly.
    /// </summary>
    private void MeasureTitles(CockpitFont font, FlightMenuState state)
    {
        _titleBoxes.Clear();
        int x = GutterX;
        for (int i = 0; i < FlightMenuTable.MenuCount; i++)
        {
            int width = font.Measure(state.Menus[i].Title) + 8;
            _titleBoxes.Add((x, width));
            x += width;
        }
    }

    /// <summary>
    /// The 3-D bevel <c>menu_filled_rect_draw</c> puts round the strip and every popup: one white
    /// row on top, a white column plus an index-18 column down the left, an index-8 row and column
    /// inside a black row and column on the bottom and right.
    /// </summary>
    private static void Bevel(in Painter painter, int x, int y, int w, int h)
    {
        painter.Fill(x + 2, y + 1, w - 4, h - 3, FillIndex);
        for (int column = x; column < x + w - 1; column++)
        {
            painter.Pixel(column, y, HighlightIndex);
        }

        painter.Pixel(x + w - 1, y, ShadowIndex);
        for (int row = y; row < y + h - 1; row++)
        {
            painter.Pixel(x, row, HighlightIndex);
        }

        for (int row = y + 1; row < y + h - 2; row++)
        {
            painter.Pixel(x + 1, row, InnerHighlightIndex);
        }

        painter.Pixel(x + 1, y + h - 2, CornerIndex);
        for (int column = x + 2; column < x + w; column++)
        {
            painter.Pixel(column, y + h - 2, ShadowIndex);
        }

        for (int column = x; column < x + w; column++)
        {
            painter.Pixel(column, y + h - 1, BlackIndex);
        }

        for (int row = y + 1; row < y + h; row++)
        {
            painter.Pixel(x + w - 1, row, BlackIndex);
        }

        for (int row = y + 1; row < y + h - 1; row++)
        {
            painter.Pixel(x + w - 2, row, ShadowIndex);
        }
    }

    /// <summary>
    /// One string, drawn the way the engine's own text routine draws it: the whole string first one
    /// row DOWN in the emboss colour, then again in the ink colour.  That is what makes the titles
    /// and the item labels look engraved into the panel, and it is measurable — the two colours
    /// alternate exactly a row apart in every screenshot.
    /// </summary>
    private static void Text(
        in Painter painter, CockpitFont font, string text, int x, int y, int ink, int emboss)
    {
        Glyphs(painter, font, text, x, y + 1, emboss);
        Glyphs(painter, font, text, x, y, ink);
    }

    private static void Glyphs(
        in Painter painter, CockpitFont font, string text, int x, int y, int index)
    {
        int pen = x;
        foreach (char c in text)
        {
            int advance = font.Advance(c);
            for (int gy = 0; gy < font.Height; gy++)
            {
                for (int gx = 0; gx < advance; gx++)
                {
                    if (font.Ink(c, gx, gy))
                    {
                        painter.Pixel(pen + gx, y + gy, index, opaque: true);
                    }
                }
            }

            pen += advance;
        }
    }

    /// <summary>
    /// Puts design-space pixels into the window: nearest, whole design pixels at an INTEGER scale
    /// anchored at the top-left, with the panel colours blended over what is already there and the
    /// text left opaque.
    /// </summary>
    private readonly ref struct Painter
    {
        private readonly PixelTarget _target;
        private readonly int _scale;
        private readonly IReadOnlyList<Rgb24> _palette;
        private readonly double _opacity;

        public Painter(
            PixelTarget target, int scale, IReadOnlyList<Rgb24> palette, double opacity)
        {
            _target = target;
            _scale = Math.Max(1, scale);
            _palette = palette;
            _opacity = Math.Clamp(opacity, 0.0, 1.0);
        }

        /// <summary>Fills one design-space rectangle.</summary>
        /// <param name="x">Design column.</param>
        /// <param name="y">Design row.</param>
        /// <param name="w">Width in design columns.</param>
        /// <param name="h">Height in design rows.</param>
        /// <param name="index">The palette index.</param>
        /// <param name="opaque">
        /// True for TEXT, which is never blended: --menu-opacity fades the panel so the frozen
        /// scene shows through it, and unreadable labels would defeat the point.
        /// </param>
        public void Fill(int x, int y, int w, int h, int index, bool opaque = false)
        {
            if (w <= 0 || h <= 0)
            {
                return;
            }

            Rgb24 colour = Colour(index);
            int x0 = Math.Max(0, x * _scale);
            int x1 = Math.Min(_target.Width, (x + w) * _scale);
            int y0 = Math.Max(0, y * _scale);
            int y1 = Math.Min(_target.Height, (y + h) * _scale);
            for (int column = x0; column < x1; column++)
            {
                for (int row = y0; row < y1; row++)
                {
                    Blend(column, row, colour, opaque);
                }
            }
        }

        /// <summary>Fills one design-space pixel.</summary>
        /// <param name="x">Design column.</param>
        /// <param name="y">Design row.</param>
        /// <param name="index">The palette index.</param>
        /// <param name="opaque">True for text; see <see cref="Fill"/>.</param>
        public void Pixel(int x, int y, int index, bool opaque = false) =>
            Fill(x, y, 1, 1, index, opaque);

        private Rgb24 Colour(int index) =>
            index < _palette.Count ? _palette[index] : new Rgb24(255, 255, 255);

        private void Blend(int x, int y, Rgb24 colour, bool opaque)
        {
            if (opaque || _opacity >= 1.0)
            {
                _target.SetPixel(x, y, _target.Encode(colour));
                return;
            }

            Span<uint> column = _target.Column(x);
            uint was = column[y];
            // The target is BlueHigh (mode-13hx's own packing); decode, mix, re-encode.
            int b = (int)((was >> 16) & 0xFF);
            int g = (int)((was >> 8) & 0xFF);
            int r = (int)(was & 0xFF);
            _target.SetPixel(x, y, _target.Encode(new Rgb24(
                (byte)Math.Round((colour.R * _opacity) + (r * (1.0 - _opacity))),
                (byte)Math.Round((colour.G * _opacity) + (g * (1.0 - _opacity))),
                (byte)Math.Round((colour.B * _opacity) + (b * (1.0 - _opacity))))));
        }
    }
}
