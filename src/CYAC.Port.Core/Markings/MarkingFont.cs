using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Markings;

/// <summary>
/// VECTOR MARKINGS (<c>Glyphs</c>) — the port's own STROKE FONT for codes, serials and numbers:
/// each glyph is a few polylines in a 1-unit-high em box, stroked with one width, so a glyph is a
/// <see cref="StrokeShape"/> union and text is a union of glyphs.
/// </summary>
/// <remarks>
/// A sans, slightly stencil-like look: it is KNOWLEDGE we author, loaded from <c>font.json</c>
/// (embedded; an override directory may replace it).  Unknown characters draw nothing and still
/// advance.
/// </remarks>
public sealed class MarkingFont
{
    private readonly Dictionary<char, (double X, double Y)[][]> _glyphs;

    /// <summary>Creates a font.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="strokeWidth">The stroke width in em units.</param>
    /// <param name="advance">The default advance (glyph box width + gap) in em units.</param>
    /// <param name="glyphs">Per character, its strokes as polylines in the em box (y up, height 1).</param>
    public MarkingFont(
        string name,
        double strokeWidth,
        double advance,
        IReadOnlyDictionary<char, (double X, double Y)[][]> glyphs)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        StrokeWidth = strokeWidth;
        Advance = advance;
        _glyphs = new Dictionary<char, (double X, double Y)[][]>(glyphs ?? throw new ArgumentNullException(nameof(glyphs)));
    }

    /// <summary>The font's name.</summary>
    public string Name { get; }

    /// <summary>The stroke width, em units.</summary>
    public double StrokeWidth { get; }

    /// <summary>The advance per character, em units.</summary>
    public double Advance { get; }

    /// <summary>The characters the font draws.</summary>
    public IReadOnlyCollection<char> Characters => _glyphs.Keys;

    /// <summary>Whether the font has strokes for a character.</summary>
    /// <param name="c">The character (case-insensitive: the font is uppercase).</param>
    public bool Has(char c) => _glyphs.ContainsKey(char.ToUpperInvariant(c));

    /// <summary>
    /// Lays out a string as one shape: glyphs left to right from x = 0, baseline y = 0, cap height
    /// <paramref name="height"/>, centred when asked.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="height">The cap height in picture units.</param>
    /// <param name="spacing">Extra advance per character, as a fraction of the height (0 = the font's).</param>
    /// <param name="centre">Whether the string is centred on the origin instead of starting there.</param>
    /// <returns>The shape, or null for an empty string.</returns>
    public MarkingShape? Layout(string text, double height, double spacing = 0.0, bool centre = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || !(height > 0.0))
        {
            return null;
        }

        double advance = (Advance + spacing) * height;
        double width = advance * text.Length;
        double x0 = centre ? -0.5 * (width - (spacing * height)) : 0.0;
        double y0 = centre ? -0.5 * height : 0.0;
        List<MarkingShape> strokes = new List<MarkingShape>();
        for (int i = 0; i < text.Length; i++)
        {
            if (!_glyphs.TryGetValue(char.ToUpperInvariant(text[i]), out (double X, double Y)[][]? glyph))
            {
                continue;
            }

            double gx = x0 + (i * advance);
            foreach ((double X, double Y)[] polyline in glyph)
            {
                (double X, double Y)[] points = new (double X, double Y)[polyline.Length];
                for (int k = 0; k < polyline.Length; k++)
                {
                    points[k] = (gx + (polyline[k].X * height), y0 + (polyline[k].Y * height));
                }

                strokes.Add(new StrokeShape(points, StrokeWidth * height));
            }
        }

        return strokes.Count == 0 ? null : new UnionShape(strokes);
    }
}
