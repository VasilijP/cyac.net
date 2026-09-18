using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

/// <summary>
/// The UI string catalogue at <c>strings.json</c> — the transform's document for
/// <c>2a.lib/strings.bin</c>.
/// </summary>
/// <remarks>
/// Index order IS the interface: it is what the code passes in <c>AX</c> to
/// <c>strings_bin_lookup_and_copy @image@0x23FFC</c>, so inserting a string renumbers everything
/// after it.  Distinct from <see cref="ExeStringCatalogDto"/> (<c>exe/strings.json</c>), which is
/// the run of NUL-terminated literals baked into the image and is addressed by OFFSET.
/// </remarks>
public sealed class UiStringCatalogDto
{
    /// <summary>The document's format tag, <c>cyac.strings/1</c>.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>The asset it came from, <c>2a.lib/strings.bin</c>.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>How many NUL bytes the original file pads with.</summary>
    [JsonPropertyName("padNuls")]
    public int PadNuls { get; init; }

    /// <summary>The strings, in index order.</summary>
    [JsonPropertyName("strings")]
    public List<UiStringDto>? Strings { get; init; }
}

/// <summary>One entry of <see cref="UiStringCatalogDto"/>.</summary>
public sealed class UiStringDto
{
    /// <summary>Its index — what the code passes in <c>AX</c>.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The text (latin-1, may carry a tab).</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }
}
