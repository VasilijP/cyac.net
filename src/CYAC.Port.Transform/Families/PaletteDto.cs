using System.Text.Json;
using System.Text.Json.Serialization;

namespace CYAC.Port.Transform.Families;

// Wire format of <data>/palettes/<name>.json.  The RAW 6-bit DAC values are the source of truth
// (transform-plan L5); the sibling .png is a human swatch rendered from them.

internal sealed class PaletteDto
{
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("about")]
    public string? About { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("componentBits")]
    public int ComponentBits { get; init; }

    [JsonPropertyName("colorCount")]
    public int ColorCount { get; init; }

    [JsonPropertyName("colors")]
    [JsonConverter(typeof(PaletteColorsConverter))]
    public List<int[]>? Colors { get; init; }

    // Law L4: a byte tail that is not a whole RGB triple has no meaning yet, so it is named and
    // counted rather than dropped.  (No shipping .pal has one — every one decodes to 768 bytes.)
    [JsonPropertyName("unknown_trailing")]
    public string? UnknownTrailing { get; init; }
}

/// <summary>
/// Writes each palette entry as a one-line <c>[r, g, b]</c> row.
/// </summary>
/// <remarks>
/// <see cref="Utf8JsonWriter.WriteRawValue(string, bool)"/> bypasses the indenting writer, which is
/// the only way to keep a 256-entry palette both indented <i>and</i> readable — the default would
/// spread every component onto its own line.
/// </remarks>
internal sealed class PaletteColorsConverter : JsonConverter<List<int[]>>
{
    public override List<int[]>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("\"colors\" must be an array of [r, g, b] rows");
        }

        List<int[]> colors = new List<int[]>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException($"\"colors\"[{colors.Count}] must be an [r, g, b] row");
            }

            List<int> components = new List<int>(3);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                components.Add(reader.GetInt32());
            }

            if (components.Count != 3)
            {
                throw new JsonException(
                    $"\"colors\"[{colors.Count}] has {components.Count} components; expected 3");
            }

            colors.Add([.. components]);
        }

        return colors;
    }

    public override void Write(Utf8JsonWriter writer, List<int[]> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartArray();

        // WriteRawValue bypasses the indenting writer entirely, so the row's own line break and
        // indentation are part of the raw text (leading whitespace inside an array is legal JSON).
        // The writer still emits the separating commas and the closing bracket's indentation.
        string pad = writer.Options.Indented ? new string(' ', writer.CurrentDepth * 2) : string.Empty;
        string lead = writer.Options.Indented ? "\n" : string.Empty;
        foreach (int[] color in value)
        {
            writer.WriteRawValue(
                $"{lead}{pad}[{color[0]}, {color[1]}, {color[2]}]", skipInputValidation: true);
        }

        writer.WriteEndArray();
    }
}
