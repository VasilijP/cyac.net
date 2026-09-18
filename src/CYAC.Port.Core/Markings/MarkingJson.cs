using System.Globalization;
using System.Text.Json;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Markings;

/// <summary>
/// VECTOR MARKINGS — the JSON reader for pictures (<c>cyac.marking/1</c>) and the font
/// (<c>cyac.marking-font/1</c>).
/// </summary>
/// <remarks>
/// <para>
/// Read through <see cref="JsonDocument"/> by hand rather than through the source-generated
/// context: a shape tree is polymorphic (<c>"type"</c> chooses the node) and small, and reflection-
/// free hand parsing keeps the assembly trimming-friendly the way <c>PortDataJson</c> does.
/// </para>
/// <para>
/// A shape node is <c>{ "type": …, … }</c>:
/// <c>circle{r}</c>, <c>rect{w,h}</c> (full extents), <c>roundrect{w,h,radius}</c>,
/// <c>ring{r,width}</c>, <c>polygon{points:[[x,y],…]}</c>, <c>star{points,r,inner,rotation}</c>,
/// <c>union{children}</c>, <c>subtract{children}</c> (first minus the rest), <c>intersect{children}</c>,
/// <c>outline{child,width}</c>, <c>offset{child,offset}</c>,
/// <c>transform{child,x,y,rotation,scale}</c>, <c>stroke{points,width}</c>,
/// <c>glyphs{text,height,spacing}</c> (needs the font).
/// </para>
/// </remarks>
public static class MarkingJson
{
    /// <summary>The picture format tag.</summary>
    public const string PictureFormat = "cyac.marking/1";

    /// <summary>The font format tag.</summary>
    public const string FontFormat = "cyac.marking-font/1";

    /// <summary>How every markings document is read: trailing commas and comments allowed, for hand editing.</summary>
    internal static JsonDocumentOptions DocumentOptions { get; } =
        new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Parses a picture document.</summary>
    /// <param name="json">The document text.</param>
    /// <param name="font">The font <c>glyphs</c> nodes lay out with, or null when the picture has none.</param>
    /// <param name="textOverride">A placement's own text for a <c>glyphs</c> node (a code, a serial), or
    /// null.</param>
    /// <param name="colorOverride">A placement's own colour for every layer that declares <c>"color": "@"</c>, or
    /// null.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="InvalidDataException">The document is not a picture.</exception>
    public static MarkingPicture ParsePicture(string json, MarkingFont? font, string? textOverride = null, SurfaceColor? colorOverride = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        JsonElement root = document.RootElement;
        string format = Str(root, "format") ?? string.Empty;
        if (format != PictureFormat)
        {
            throw new InvalidDataException($"expected format '{PictureFormat}', got '{format}'");
        }

        string name = Str(root, "name") ?? throw new InvalidDataException("a picture needs a \"name\"");
        WearParams wear = root.TryGetProperty("wear", out JsonElement w) ? ParseWear(w) : WearParams.None;
        List<MarkingLayer> layers = new List<MarkingLayer>();
        if (root.TryGetProperty("layers", out JsonElement layerArray))
        {
            foreach (JsonElement layer in layerArray.EnumerateArray())
            {
                MarkingShape? shape = ParseShape(layer.GetProperty("shape"), font, textOverride);
                if (shape is null)
                {
                    continue;   // an empty glyphs node: nothing to paint
                }

                // "@" = the placement's colour; "@#RRGGBB" = the placement's colour, else this default.
                string colorText = Str(layer, "color") ?? "#000000";
                SurfaceColor color = colorText.StartsWith('@')
                    ? colorOverride ?? (colorText.Length > 1 ? SurfaceColor.Parse(colorText[1..]) : new SurfaceColor(0, 0, 0))
                    : SurfaceColor.Parse(colorText);
                double opacity = Num(layer, "opacity") ?? 1.0;
                WearParams? layerWear = layer.TryGetProperty("wear", out JsonElement lw) ? ParseWear(lw) : null;
                layers.Add(new MarkingLayer(shape, color, opacity, layerWear));
            }
        }

        SurfaceColor? bare = Str(root, "bareMetal") is { } bm ? SurfaceColor.Parse(bm) : null;
        return new MarkingPicture(
            name, layers, wear,
            symmetric: Bool(root, "symmetric") ?? false,
            noMirror: Bool(root, "noMirror") ?? false,
            bareMetal: bare);
    }

    /// <summary>Parses the wear block: <c>{ chipping: {amount, scale, seed, edgeBand}, edgeWear: {width, strength} }</c>.</summary>
    /// <param name="element">The block.</param>
    public static WearParams ParseWear(JsonElement element)
    {
        Chipping chipping = default(Chipping);
        if (element.TryGetProperty("chipping", out JsonElement c))
        {
            chipping = new Chipping(
                Num(c, "amount") ?? 0.0,
                Num(c, "scale") ?? 0.1,
                (int)(Num(c, "seed") ?? 1),
                Num(c, "edgeBand") ?? 0.1);
        }

        EdgeWear edge = default(EdgeWear);
        if (element.TryGetProperty("edgeWear", out JsonElement e))
        {
            edge = new EdgeWear(Num(e, "width") ?? 0.0, Num(e, "strength") ?? 1.0);
        }

        return new WearParams(chipping, edge);
    }

    /// <summary>Parses one shape node.</summary>
    /// <param name="node">The node.</param>
    /// <param name="font">The font for <c>glyphs</c>.</param>
    /// <param name="textOverride">The text a <c>glyphs</c> node draws instead of its own.</param>
    /// <returns>The shape, or null for a glyph run with nothing to draw.</returns>
    public static MarkingShape? ParseShape(JsonElement node, MarkingFont? font, string? textOverride = null)
    {
        string type = (Str(node, "type") ?? throw new InvalidDataException("a shape needs a \"type\"")).ToLowerInvariant();
        switch (type)
        {
            case "circle":
                return new CircleShape(Req(node, "r"));
            case "rect":
                return new RectShape(0.5 * Req(node, "w"), 0.5 * Req(node, "h"));
            case "roundrect":
                return new RoundRectShape(0.5 * Req(node, "w"), 0.5 * Req(node, "h"), Req(node, "radius"));
            case "ring":
                return new RingShape(Req(node, "r"), Req(node, "width"));
            case "polygon":
                return new PolygonShape(Points(node.GetProperty("points")));
            case "star":
                return StarShape.Create(
                    (int)(Num(node, "points") ?? 5), Req(node, "r"),
                    Num(node, "inner") ?? Req(node, "r") * 0.382, Num(node, "rotation") ?? 90.0);
            case "union":
                return new UnionShape(Children(node, font, textOverride));
            case "subtract":
                {
                    MarkingShape[] children = Children(node, font, textOverride);
                    return new SubtractShape(children[0], children.Skip(1).ToArray());
                }

            case "intersect":
                return new IntersectShape(Children(node, font, textOverride));
            case "outline":
                return new OutlineShape(Child(node, font, textOverride), Req(node, "width"));
            case "offset":
                return new OffsetShape(Child(node, font, textOverride), Req(node, "offset"));
            case "transform":
                return new TransformShape(
                    Child(node, font, textOverride),
                    Num(node, "x") ?? 0.0, Num(node, "y") ?? 0.0,
                    Num(node, "rotation") ?? 0.0, Num(node, "scale") ?? 1.0);
            case "stroke":
                return new StrokeShape(Points(node.GetProperty("points")), Req(node, "width"));
            case "glyphs":
                {
                    if (font is null)
                    {
                        throw new InvalidDataException("a glyphs node needs a font");
                    }

                    string text = textOverride ?? Str(node, "text") ?? string.Empty;
                    return font.Layout(text, Num(node, "height") ?? 1.0, Num(node, "spacing") ?? 0.0, Bool(node, "centre") ?? true);
                }

            default:
                throw new InvalidDataException($"unknown shape type '{type}'");
        }
    }

    /// <summary>Parses the font document.</summary>
    /// <param name="json">The document text.</param>
    public static MarkingFont ParseFont(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        JsonElement root = document.RootElement;
        string format = Str(root, "format") ?? string.Empty;
        if (format != FontFormat)
        {
            throw new InvalidDataException($"expected format '{FontFormat}', got '{format}'");
        }

        Dictionary<char, (double X, double Y)[][]> glyphs = new Dictionary<char, (double X, double Y)[][]>();
        foreach (JsonProperty glyph in root.GetProperty("glyphs").EnumerateObject())
        {
            if (glyph.Name.Length != 1)
            {
                throw new InvalidDataException($"a glyph key is one character, got '{glyph.Name}'");
            }

            List<(double X, double Y)[]> strokes = new List<(double X, double Y)[]>();
            foreach (JsonElement stroke in glyph.Value.EnumerateArray())
            {
                strokes.Add(Points(stroke));
            }

            glyphs[char.ToUpperInvariant(glyph.Name[0])] = [.. strokes];
        }

        return new MarkingFont(
            Str(root, "name") ?? "font",
            Num(root, "strokeWidth") ?? 0.16,
            Num(root, "advance") ?? 0.78,
            glyphs);
    }

    private static MarkingShape[] Children(JsonElement node, MarkingFont? font, string? textOverride)
    {
        List<MarkingShape> list = new List<MarkingShape>();
        foreach (JsonElement child in node.GetProperty("children").EnumerateArray())
        {
            if (ParseShape(child, font, textOverride) is { } shape)
            {
                list.Add(shape);
            }
        }

        if (list.Count == 0)
        {
            throw new InvalidDataException("a boolean node needs at least one child");
        }

        return [.. list];
    }

    private static MarkingShape Child(JsonElement node, MarkingFont? font, string? textOverride) =>
        ParseShape(node.GetProperty("child"), font, textOverride)
            ?? throw new InvalidDataException("the child draws nothing");

    private static (double X, double Y)[] Points(JsonElement array)
    {
        List<(double X, double Y)> points = new List<(double X, double Y)>();
        foreach (JsonElement p in array.EnumerateArray())
        {
            points.Add((p[0].GetDouble(), p[1].GetDouble()));
        }

        return [.. points];
    }

    private static double Req(JsonElement node, string name) =>
        Num(node, name) ?? throw new InvalidDataException($"shape '{Str(node, "type")}' needs \"{name}\"");

    internal static string? Str(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static double? Num(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    internal static bool? Bool(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;

    /// <summary>Formats a number the way the files are written: invariant, short.</summary>
    /// <param name="v">The number.</param>
    internal static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
