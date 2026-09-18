using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

/// <summary>
/// <c>exe/tables/world.json</c>: the constant tables that place things in the world the scene is drawn
/// in.
/// </summary>
/// <remarks>
/// <para>
/// It carries one table today, the cloud deck's lattice offsets that
/// <c>cloud_reposition_for_idx @image@0x2CD9E</c> wraps around the view anchor every frame.  The
/// transform finds the table, its record size and its count by reading the instructions that index it.
/// </para>
/// <para>
/// The document is shaped as sections, so the world's other placement tables can join it.
/// </para>
/// </remarks>
public sealed class WorldTableDto
{
    /// <summary>Where the document lives in the data tree.</summary>
    public const string DataPath = "exe/tables/world.json";

    /// <summary>The document's schema tag.</summary>
    public const string FormatTag = "cyac.table.world/1";

    /// <summary>The document's schema tag, <see cref="FormatTag"/>.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is, in prose.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>The cloud deck's lattice offsets.</summary>
    [JsonPropertyName("cloudDeck")]
    public CloudDeckTableDto? CloudDeck { get; init; }
}

/// <summary>The cloud deck's lattice offsets: one <c>(x, z)</c> pair of <c>i32</c> per cloud.</summary>
public sealed class CloudDeckTableDto
{
    /// <summary>What the table is and who reads it.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>Where the table lives and what it holds.</summary>
    [JsonPropertyName("source")]
    public DataSourceDto? Source { get; init; }

    /// <summary>
    /// The instructions that read a cloud's X, as the image offset of the <c>mov cl,imm8</c> that sizes
    /// the record; the table's DGROUP offset is the displacement of the <c>add</c> that follows.
    /// </summary>
    [JsonPropertyName("xReadAt")]
    public string? XReadAt { get; init; }

    /// <summary>The same instructions for a cloud's Z, four bytes further into the record.</summary>
    [JsonPropertyName("zReadAt")]
    public string? ZReadAt { get; init; }

    /// <summary>The <c>cmp si,imm8</c> that bounds the per-frame loop over the clouds.</summary>
    [JsonPropertyName("countedAt")]
    public string? CountedAt { get; init; }

    /// <summary>How many clouds the table places.</summary>
    [JsonPropertyName("clouds")]
    public int Clouds { get; init; }

    /// <summary>Every cloud's offset, in table order, in position units (world units × 256).</summary>
    [JsonPropertyName("offsets")]
    public List<CloudOffsetDto>? Offsets { get; init; }
}

/// <summary>One cloud's lattice offset, in position units.</summary>
public sealed class CloudOffsetDto
{
    /// <summary>The cloud's index.</summary>
    [JsonPropertyName("cloud")]
    public int Cloud { get; init; }

    /// <summary>The X offset.</summary>
    [JsonPropertyName("x")]
    public long X { get; init; }

    /// <summary>The Z offset.</summary>
    [JsonPropertyName("z")]
    public long Z { get; init; }
}
