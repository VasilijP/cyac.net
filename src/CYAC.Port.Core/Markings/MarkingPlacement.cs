using System.Text;
using System.Text.Json;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Markings;

/// <summary>Which faces of the placed frame's plane receive the picture (<c>sides</c>).</summary>
public enum PlacementSides
{
    /// <summary>Faces whose outward normal agrees with the frame's normal (within 60°).</summary>
    Top = 0,

    /// <summary>Both: the near side as authored, the far side with the picture turned so its viewer sees it unmirrored.</summary>
    Both = 1,

    /// <summary>Both sides with the SAME frame — ink through paper (the Balkenkreuz on a thin wing).</summary>
    Through = 2,

    /// <summary>
    /// WRAPPED AROUND an axis (a fuselage band, a tail stripe): <c>U</c> is the axis, the origin
    /// the station on it, and EVERY face whose normal is roughly perpendicular to the axis gets its
    /// own frame with <c>V = n_face × U</c>, so the picture's X runs along the axis and its Y runs
    /// around the body.  The picture should be tall in Y (a band is invariant around).
    /// </summary>
    Around = 3,
}

/// <summary>Which model plane the placement is mirrored across to make its twin (<c>mirror</c>).</summary>
public enum PlacementMirror
{
    /// <summary>No twin.</summary>
    None = 0,

    /// <summary>Across <c>x = 0</c>: the other wing, the other fuselage side.</summary>
    X = 1,

    /// <summary>Across <c>y = 0</c>: above / below (rare).</summary>
    Y = 2,
}

/// <summary>
/// VECTOR MARKINGS — one PLACEMENT: a picture, a frame in model units of the shipped geometry, and how it spreads
/// (sides, mirror), plus what shipped records it replaces.
/// </summary>
/// <param name="Picture">The picture's library name.</param>
/// <param name="Origin">The frame origin, model units.</param>
/// <param name="U">The picture's +X axis in model space, as written (the resolver normalises it).</param>
/// <param name="V">The picture's +Y axis, as written (the resolver makes it ⟂ to U).</param>
/// <param name="Size">Model units per picture unit.</param>
/// <param name="Sides">Which faces of the plane receive it.</param>
/// <param name="Mirror">Whether a twin is generated across a model plane.</param>
/// <param name="Text">Text for a <c>glyphs</c> picture, or null.</param>
/// <param name="Color">The colour for the picture's <c>@</c> layers, or null.</param>
/// <param name="Wear">A wear override for the whole picture, or null.</param>
/// <param name="Replaces">Per LOD index, the shipped records this placement stands in for (hidden under vector markings).</param>
/// <param name="Depth">How far off the frame plane a face may sit and still receive the picture, model units (0 = the default rule).</param>
/// <param name="Note">Free text for the author.</param>
/// <param name="BareMetal">
/// What a CHIP EXPOSES on this placement (<see cref="BareMetalSpec"/>): absent = the picture's own aluminium;
/// <c>"none"</c> = the paint beneath (the polygon's colour — right for a painted aeroplane); <c>"#RRGGBB"</c>;
/// <c>"skin:-0.3"</c> = the paint darkened 30 %.
/// </param>
public sealed record MarkingPlacement(
    string Picture,
    (double X, double Y, double Z) Origin,
    (double X, double Y, double Z) U,
    (double X, double Y, double Z) V,
    double Size,
    PlacementSides Sides = PlacementSides.Top,
    PlacementMirror Mirror = PlacementMirror.None,
    string? Text = null,
    SurfaceColor? Color = null,
    WearParams? Wear = null,
    IReadOnlyDictionary<int, int[]>? Replaces = null,
    double Depth = 0.0,
    string? Note = null,
    BareMetalSpec? BareMetal = null)
{
    /// <summary>
    /// The identity of a placement the bootstrap generated (<c>shipped:…</c>, <c>band:…</c>,
    /// <c>rule:…</c>), or null for one added by hand.
    /// </summary>
    /// <remarks>
    /// Never serialised: a diff names base placements by it, and an editor carries it through edits
    /// (a <c>with</c> copy keeps it) so a saved diff can tell a moved base placement from a new one.
    /// </remarks>
    public string? Key { get; init; }

    // The JSON tokens the note and text were read from. A hand-edited file may spell a character
    // differently from the serialiser (a literal apostrophe where it writes an escape); writing the
    // token back while it still decodes to the value keeps such a file byte-stable through a load
    // and a save.
    internal string? NoteToken { get; init; }

    internal string? TextToken { get; init; }

    /// <summary>The frame's normal, <c>U × V</c>, normalised.</summary>
    public (double X, double Y, double Z) Normal
    {
        get
        {
            (double ux, double uy, double uz) = Unit(U);
            (double vx, double vy, double vz) = Unit(V);
            return Unit(((uy * vz) - (uz * vy), (uz * vx) - (ux * vz), (ux * vy) - (uy * vx)));
        }
    }

    /// <summary>This placement with U normalised and V made perpendicular to it and normalised.</summary>
    public MarkingPlacement Orthonormalised()
    {
        (double X, double Y, double Z) u = Unit(U);
        double d = (V.X * u.X) + (V.Y * u.Y) + (V.Z * u.Z);
        (double X, double Y, double Z) v = Unit((V.X - (d * u.X), V.Y - (d * u.Y), V.Z - (d * u.Z)));
        return this with { U = u, V = v };
    }

    internal static (double X, double Y, double Z) Unit((double X, double Y, double Z) v)
    {
        double l = Math.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));
        return l > 1e-12 ? (v.X / l, v.Y / l, v.Z / l) : (1.0, 0.0, 0.0);
    }
}

/// <summary>A class's placements — the <c>placements/&lt;class&gt;.json</c> document.</summary>
/// <param name="Class">The mesh basename.</param>
/// <param name="Placements">The placements, in file order.</param>
/// <param name="Nation">The nation the bootstrap chose pictures for (informational).</param>
public sealed record MarkingSet(string Class, IReadOnlyList<MarkingPlacement> Placements, string? Nation = null)
{
    /// <summary>The document format tag.</summary>
    public const string Format = "cyac.markings/1";

    /// <summary>The placement fields in the order a document writes them.</summary>
    internal static readonly IReadOnlyList<string> FieldOrder =
        ["picture", "text", "color", "origin", "u", "v", "size", "sides", "mirror", "depth", "bareMetal", "wear", "replaces", "note"];

    /// <summary>Parses a placements document.</summary>
    /// <param name="json">The document text.</param>
    /// <exception cref="InvalidDataException">The document is not a placements file.</exception>
    /// <remarks>
    /// Values are kept as written: in particular U and V are not orthonormalised here (the resolver
    /// does that when it builds frames), so a document read and written again is unchanged.
    /// </remarks>
    public static MarkingSet Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json, MarkingJson.DocumentOptions);
        JsonElement root = document.RootElement;
        string format = MarkingJson.Str(root, "format") ?? string.Empty;
        if (format != Format)
        {
            throw new InvalidDataException($"expected format '{Format}', got '{format}'");
        }

        string cls = MarkingJson.Str(root, "class") ?? throw new InvalidDataException("a placements file needs a \"class\"");
        List<MarkingPlacement> placements = new List<MarkingPlacement>();
        if (root.TryGetProperty("placements", out JsonElement array))
        {
            foreach (JsonElement p in array.EnumerateArray())
            {
                placements.Add(ParsePlacement(p));
            }
        }

        return new MarkingSet(cls, placements, MarkingJson.Str(root, "nation"));
    }

    /// <summary>Writes the document text (the file the browser saves and the repo commits).</summary>
    public string Serialize()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\n  \"format\": \"").Append(Format).Append("\",\n  \"class\": \"").Append(Class).Append("\",\n");
        if (Nation is not null)
        {
            sb.Append("  \"nation\": \"").Append(Nation).Append("\",\n");
        }

        sb.Append("  \"placements\": [\n");
        WritePlacements(sb, Placements);
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    /// <summary>Parses one placement object.</summary>
    internal static MarkingPlacement ParsePlacement(JsonElement p) =>
        new MarkingPlacement(
            MarkingJson.Str(p, "picture") ?? throw new InvalidDataException("a placement needs a \"picture\""),
            Vec(p, "origin"), Vec(p, "u"), Vec(p, "v"),
            MarkingJson.Num(p, "size") ?? 10.0,
            Enum.TryParse<PlacementSides>(MarkingJson.Str(p, "sides") ?? "top", ignoreCase: true, out PlacementSides sides) ? sides : PlacementSides.Top,
            Enum.TryParse<PlacementMirror>(MarkingJson.Str(p, "mirror") ?? "none", ignoreCase: true, out PlacementMirror mirror) ? mirror : PlacementMirror.None,
            MarkingJson.Str(p, "text"),
            MarkingJson.Str(p, "color") is { } c ? SurfaceColor.Parse(c) : null,
            p.TryGetProperty("wear", out JsonElement w) ? MarkingJson.ParseWear(w) : null,
            p.TryGetProperty("replaces", out JsonElement r) && r.ValueKind == JsonValueKind.Object ? ParseReplaces(r) : null,
            MarkingJson.Num(p, "depth") ?? 0.0,
            MarkingJson.Str(p, "note"),
            MarkingJson.Str(p, "bareMetal") is { } bm ? BareMetalSpec.Parse(bm) : null)
        {
            NoteToken = StringToken(p, "note"),
            TextToken = StringToken(p, "text"),
        };

    /// <summary>
    /// A placement with one field replaced from a diff's JSON value; <c>null</c> deletes an optional
    /// field. Stricter than <see cref="ParsePlacement"/>: an unknown field, a wrong type or an unknown
    /// enum word throws.
    /// </summary>
    internal static MarkingPlacement WithField(MarkingPlacement p, string field, JsonElement value)
    {
        bool delete = value.ValueKind == JsonValueKind.Null;
        if (delete && field is "picture" or "origin" or "u" or "v" or "size" or "sides" or "mirror")
        {
            throw new InvalidDataException($"the placement field \"{field}\" is required and cannot be deleted");
        }

        return field switch
        {
            "picture" => p with { Picture = RequiredString(value, field) },
            "text" => delete ? p with { Text = null, TextToken = null } : p with { Text = RequiredString(value, field), TextToken = value.GetRawText() },
            "color" => p with { Color = delete ? null : SurfaceColor.Parse(RequiredString(value, field)) },
            "origin" => p with { Origin = Vec3(value, field) },
            "u" => p with { U = Vec3(value, field) },
            "v" => p with { V = Vec3(value, field) },
            "size" => p with { Size = RequiredNumber(value, field) },
            "sides" => p with { Sides = RequiredEnum<PlacementSides>(value, field) },
            "mirror" => p with { Mirror = RequiredEnum<PlacementMirror>(value, field) },
            "depth" => p with { Depth = delete ? 0.0 : RequiredNumber(value, field) },
            "bareMetal" => p with { BareMetal = delete ? null : BareMetalSpec.Parse(RequiredString(value, field)) },
            "wear" => p with { Wear = delete ? null : value.ValueKind == JsonValueKind.Object ? MarkingJson.ParseWear(value) : throw WrongType(field, "an object") },
            "replaces" => p with { Replaces = delete ? null : value.ValueKind == JsonValueKind.Object ? ParseReplaces(value) : throw WrongType(field, "an object") },
            "note" => delete ? p with { Note = null, NoteToken = null } : p with { Note = RequiredString(value, field), NoteToken = value.GetRawText() },
            _ => throw new InvalidDataException($"unknown placement field \"{field}\""),
        };
    }

    /// <summary>A field's JSON value as a document writes it, or null when the placement omits it.</summary>
    internal static string? FieldJson(MarkingPlacement p, string field) => field switch
    {
        "picture" => Quoted(p.Picture),
        "text" => p.Text is null ? null : WrittenString(p.Text, p.TextToken),
        "color" => p.Color is { } color ? Quoted(color.ToString()) : null,
        "origin" => Vec(p.Origin),
        "u" => Vec(p.U),
        "v" => Vec(p.V),
        "size" => MarkingJson.F(p.Size),
        "sides" => Quoted(p.Sides.ToString().ToLowerInvariant()),
        "mirror" => Quoted(p.Mirror.ToString().ToLowerInvariant()),
        "depth" => p.Depth > 0.0 ? MarkingJson.F(p.Depth) : null,
        "bareMetal" => p.BareMetal is { } bareMetal ? Quoted(bareMetal.ToString()) : null,
        "wear" => p.Wear is { } wear ? WearJson(wear) : null,
        "replaces" => p.Replaces is { Count: > 0 } replaces ? ReplacesJson(replaces) : null,
        "note" => p.Note is null ? null : WrittenString(p.Note, p.NoteToken),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "unknown placement field"),
    };

    /// <summary>Writes placement objects, one per entry, comma-separated, in the document layout.</summary>
    internal static void WritePlacements(StringBuilder sb, IReadOnlyList<MarkingPlacement> placements)
    {
        for (int i = 0; i < placements.Count; i++)
        {
            WritePlacement(sb, placements[i]);
            sb.Append(i + 1 < placements.Count ? "," : string.Empty).Append('\n');
        }
    }

    private static void WritePlacement(StringBuilder sb, MarkingPlacement p)
    {
        void Field(string separator, string name)
        {
            if (FieldJson(p, name) is { } json)
            {
                sb.Append(separator).Append('"').Append(name).Append("\": ").Append(json);
            }
        }

        const string Line = ",\n      ";
        sb.Append("    { ");
        Field(string.Empty, "picture");
        Field(", ", "text");
        Field(", ", "color");
        Field(Line, "origin");
        Field(", ", "u");
        Field(", ", "v");
        Field(", ", "size");
        Field(Line, "sides");
        Field(", ", "mirror");
        Field(", ", "depth");
        Field(", ", "bareMetal");
        Field(Line, "wear");
        Field(Line, "replaces");
        Field(Line, "note");
        sb.Append(" }");
    }

    private static string WearJson(WearParams wear)
    {
        StringBuilder sb = new StringBuilder("{ ");
        if (wear.Chipping.Active)
        {
            sb.Append("\"chipping\": { \"amount\": ").Append(MarkingJson.F(wear.Chipping.Amount))
              .Append(", \"scale\": ").Append(MarkingJson.F(wear.Chipping.Scale))
              .Append(", \"seed\": ").Append(wear.Chipping.Seed)
              .Append(", \"edgeBand\": ").Append(MarkingJson.F(wear.Chipping.EdgeBand)).Append(" }");
        }

        if (wear.EdgeWear.Active)
        {
            if (wear.Chipping.Active)
            {
                sb.Append(", ");
            }

            sb.Append("\"edgeWear\": { \"width\": ").Append(MarkingJson.F(wear.EdgeWear.Width))
              .Append(", \"strength\": ").Append(MarkingJson.F(wear.EdgeWear.Strength)).Append(" }");
        }

        return sb.Append(" }").ToString();
    }

    private static string ReplacesJson(IReadOnlyDictionary<int, int[]> replaces) =>
        "{ " + string.Join(", ", replaces.OrderBy(kv => kv.Key).Select(kv => $"\"{kv.Key}\": [{string.Join(", ", kv.Value)}]")) + " }";

    private static Dictionary<int, int[]> ParseReplaces(JsonElement r)
    {
        Dictionary<int, int[]> replaces = new Dictionary<int, int[]>();
        foreach (JsonProperty lod in r.EnumerateObject())
        {
            replaces[int.Parse(lod.Name, System.Globalization.CultureInfo.InvariantCulture)] =
                lod.Value.EnumerateArray().Select(e => e.GetInt32()).ToArray();
        }

        return replaces;
    }

    // Names and enum words are written unescaped, as the format always has.
    private static string Quoted(string value) => "\"" + value + "\"";

    private static string WrittenString(string value, string? token)
    {
        if (token is not null)
        {
            using JsonDocument document = JsonDocument.Parse(token);
            if (document.RootElement.GetString() == value)
            {
                return token;
            }
        }

        return JsonSerializer.Serialize(value);
    }

    private static string? StringToken(JsonElement node, string name) =>
        node.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetRawText() : null;

    private static string RequiredString(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw WrongType(field, "a string");

    private static double RequiredNumber(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.Number ? value.GetDouble() : throw WrongType(field, "a number");

    private static TEnum RequiredEnum<TEnum>(JsonElement value, string field)
        where TEnum : struct, Enum
    {
        string word = RequiredString(value, field);
        return Enum.GetNames<TEnum>().FirstOrDefault(n => string.Equals(n, word, StringComparison.OrdinalIgnoreCase)) is { } name
            ? Enum.Parse<TEnum>(name)
            : throw new InvalidDataException($"the placement field \"{field}\" has no value \"{word}\"");
    }

    private static InvalidDataException WrongType(string field, string expected) =>
        new($"the placement field \"{field}\" must be {expected}");

    private static (double X, double Y, double Z) Vec(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out JsonElement v))
        {
            throw new InvalidDataException($"a placement's \"{name}\" is [x, y, z]");
        }

        return Vec3(v, name);
    }

    private static (double X, double Y, double Z) Vec3(JsonElement v, string name)
    {
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 3 || v.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Number))
        {
            throw new InvalidDataException($"a placement's \"{name}\" is [x, y, z]");
        }

        return (v[0].GetDouble(), v[1].GetDouble(), v[2].GetDouble());
    }

    private static string Vec((double X, double Y, double Z) v) =>
        $"[{MarkingJson.F(v.X)}, {MarkingJson.F(v.Y)}, {MarkingJson.F(v.Z)}]";
}
