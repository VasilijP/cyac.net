using CYAC.Port.Render;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host.Menu;

/// <summary>
/// The <c>?</c> menu's "About Yeager..." box: three centred lines in a bevelled panel, closed by any
/// key.
/// </summary>
/// <remarks>
/// <para>
/// <c>ui_about_yeager_dialog @image@0x219C4</c> prints the three strings at DGROUP <c>[0x10F0]</c> /
/// <c>[0x110C]</c> / <c>[0x112C]</c> and waits for a keystroke.  There is no screenshot of it, so —
/// unlike the bar and the popups, which are reproduced pixel for pixel — the box's own geometry is
/// the port's: the same bevel, the same font, the same colours, centred, sized to its widest line.
/// <b>(open)</b> a screenshot of the original's box would let this be measured too.
/// </para>
/// <para>
/// The two lines carry font glyph bytes (U+0012 after the title, U+0011 for the copyright sign) and
/// <c>propbold</c> draws both, so the text goes to the drawer exactly as the executable stores it.
/// </para>
/// </remarks>
public static class FlightMenuAboutBox
{
    /// <summary>Design pixels of padding round the text inside the panel.</summary>
    public const int Padding = 12;

    /// <summary>Design rows between two lines.</summary>
    public const int LineHeight = 11;

    /// <summary>Draws the box centred over the frame.</summary>
    /// <param name="target">The whole window.</param>
    /// <param name="font">The game's own <c>propbold</c> font.</param>
    /// <param name="palette">The game's palette, widened to 8 bits.</param>
    /// <param name="lines">The three credit lines, out of <c>exe/tables/flight_menus.json</c>.</param>
    /// <param name="opacity">The panel opacity <c>--menu-opacity</c> is holding.</param>
    /// <param name="scale">host pixels per design pixel; the box follows the menu's scale.</param>
    public static void Render(
        PixelTarget target,
        CockpitFont font,
        IReadOnlyList<Rgb24> palette,
        IReadOnlyList<string> lines,
        double opacity,
        int scale)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            return;
        }

        int widest = 0;
        foreach (string line in lines)
        {
            widest = Math.Max(widest, font.Measure(line));
        }

        int width = widest + (Padding * 2);
        int height = (LineHeight * lines.Count) + (Padding * 2) - 3;

        // Centred in the WINDOW measured in design pixels, so the box sits in the middle of the
        // screen at every scale.  That space no longer covers the window once the menu has its own
        // integer scale. It is never allowed above the strip.
        int designWidth = Math.Max(width, target.Width / Math.Max(1, scale));
        int designHeight = Math.Max(height, target.Height / Math.Max(1, scale));
        int x = (designWidth - width) / 2;
        int y = Math.Max(FlightMenuRenderer.BarHeight + 2, (designHeight - height) / 2);

        FlightMenuRenderer renderer = new FlightMenuRenderer();
        renderer.DrawPanel(target, palette, x, y, width, height, opacity, scale);
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            renderer.DrawText(
                target,
                font,
                palette,
                line,
                x + ((width - font.Measure(line)) / 2),
                y + Padding + (LineHeight * i) - 4,
                FlightMenuRenderer.ShadowIndex,
                FlightMenuRenderer.TextEmbossIndex,
                opacity,
                scale);
        }
    }
}
