namespace CYAC.Port.Render.Cockpit;

/// <summary>
/// The game's own bitmap font, drawn at host resolution — a 1-bit glyph STRIP plus the column table
/// that cuts it into glyphs (<c>data/images/fonts/&lt;name&gt;.json</c> + <c>.png</c>).
/// </summary>
/// <remarks>
/// <para>
/// A DeluxeFont bitmap font: one band of <c>Height</c> scanlines, <c>StripWidth</c> pixels wide,
/// ink where the bit is set; glyph <c>c</c> occupies columns <c>[Left(c), Left(c) + Width(c))</c>.
/// The engine renders one character at a time through <c>hud_glyph_render @image@0x1F9E8</c>; the
/// port draws the same bits, scaled by whole pixels so the letterforms stay crisp.
/// </para>
/// <para>
/// The cockpit's text instruments — the weapon+ammo readout and the chaff/flare counters — use it
/// so their numbers are the ones the 1991 screen showed, not a modern substitute.
/// </para>
/// </remarks>
public sealed class CockpitFont
{
    private readonly bool[] _ink;
    private readonly int[] _left;
    private readonly int[] _width;

    private CockpitFont(int stripWidth, int height, bool[] ink, int[] left, int[] width)
    {
        StripWidth = stripWidth;
        Height = height;
        _ink = ink;
        _left = left;
        _width = width;
    }

    /// <summary>The strip's width in pixels.</summary>
    public int StripWidth { get; }

    /// <summary>The glyph height in scanlines.</summary>
    public int Height { get; }

    /// <summary>Builds a font from the transform's strip and its own column table.</summary>
    /// <param name="stripWidth">The strip's width.</param>
    /// <param name="height">The glyph height.</param>
    /// <param name="strip">
    /// <paramref name="stripWidth"/> × <paramref name="height"/> indices, row major; non-zero is ink.
    /// </param>
    /// <param name="columnOffsets">
    /// The font's 256-entry column table.  Character <c>c</c> occupies columns <c>[columnOffsets[c −
    /// 1], columnOffsets[c])</c> — the table holds glyph END columns.
    /// </param>
    /// <exception cref="ArgumentException">The strip's size does not match its geometry.</exception>
    /// <remarks>
    /// <b>The end-column reading is a correction.</b>  <c>CYAC.Formats.Image.FontDecoder</c> pairs
    /// <c>table[c] .. table[c + 1]</c> and labels the result character <c>c</c>, which puts every
    /// glyph one codepoint too high: drawn that way <c>'0'</c> comes out as <c>'1'</c>, <c>'A'</c> as
    /// <c>'B'</c> and <c>'-'</c> as <c>'.'</c> — measured on <c>data/images/fonts/4x6.png</c> by
    /// rendering the cells.  The game itself obviously prints the right letters, so the labelling is
    /// the transform's and this is the reading that agrees with the 1991 screen.
    /// </remarks>
    public static CockpitFont Create(
        int stripWidth, int height, byte[] strip, IReadOnlyList<int> columnOffsets)
    {
        ArgumentNullException.ThrowIfNull(strip);
        ArgumentNullException.ThrowIfNull(columnOffsets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (strip.Length != stripWidth * height)
        {
            throw new ArgumentException(
                $"a {stripWidth}x{height} strip needs {stripWidth * height} pixels, got {strip.Length}",
                nameof(strip));
        }

        bool[] ink = new bool[strip.Length];
        for (int i = 0; i < strip.Length; i++)
        {
            ink[i] = strip[i] != 0;
        }

        int[] left = new int[256];
        int[] width = new int[256];
        for (int c = 0; c < 256 && c < columnOffsets.Count; c++)
        {
            int start = c == 0 ? 0 : columnOffsets[c - 1];
            int end = columnOffsets[c];
            if (end <= start || end > stripWidth)
            {
                continue;
            }

            left[c] = start;
            width[c] = end - start;
        }

        return new CockpitFont(stripWidth, height, ink, left, width);
    }

    /// <summary>The advance of one character in design pixels.</summary>
    /// <param name="c">The character.</param>
    public int Advance(char c) => (uint)c < 256 ? _width[c] : 0;

    /// <summary>The width of a whole string in design pixels.</summary>
    /// <param name="text">The string.</param>
    public int Measure(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int total = 0;
        foreach (char c in text)
        {
            total += Advance(c);
        }

        return total;
    }

    /// <summary>Whether a character's pixel is ink.</summary>
    /// <param name="c">The character.</param>
    /// <param name="x">Column inside the glyph.</param>
    /// <param name="y">Row inside the glyph.</param>
    public bool Ink(char c, int x, int y)
    {
        if ((uint)c >= 256 || (uint)x >= (uint)_width[c] || (uint)y >= (uint)Height)
        {
            return false;
        }

        int column = _left[c] + x;
        return (uint)column < (uint)StripWidth && _ink[(y * StripWidth) + column];
    }
}
