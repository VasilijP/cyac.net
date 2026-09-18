using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

/// <summary>
/// <c>exe/tables/effect_look.json</c>: the constant tables <c>deferred_effect_render @image@0x03E18</c>
/// draws an explosion with.
/// </summary>
/// <remarks>
/// <para>
/// Today it carries the two debris angle tables: the eight BAM angles the disc arm
/// (<c>effect_particle_draw @image@0x03C5A</c>) places its shards at, and the sixteen the burst arm
/// (<c>image@0x03E6C</c>) uses.  The transform finds each table, and how many entries it has, by reading
/// the instructions that index it: the <c>and</c> that bounds the shard counter and the
/// <c>push word [bx+disp16]</c> that reads the angle.
/// </para>
/// <para>
/// The document is shaped to take the effect's other small tables as further sections.
/// </para>
/// </remarks>
public sealed class EffectLookTableDto
{
    /// <summary>Where the document lives in the data tree.</summary>
    public const string DataPath = "exe/tables/effect_look.json";

    /// <summary>The document's schema tag.</summary>
    public const string FormatTag = "cyac.table.effectLook/1";

    /// <summary>The document's schema tag, <see cref="FormatTag"/>.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is, in prose.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>The disc arm's debris angles.</summary>
    [JsonPropertyName("shardAngles")]
    public AngleTableDto? ShardAngles { get; init; }

    /// <summary>The burst arm's debris angles.</summary>
    [JsonPropertyName("burstShardAngles")]
    public AngleTableDto? BurstShardAngles { get; init; }
}

/// <summary>A table of BAM angles (2,880 units to the circle) that a draw loop indexes.</summary>
public sealed class AngleTableDto
{
    /// <summary>Where the table lives and what it holds.</summary>
    [JsonPropertyName("source")]
    public DataSourceDto? Source { get; init; }

    /// <summary>The image offset of the <c>and</c> that bounds the index to the table.</summary>
    [JsonPropertyName("maskedAt")]
    public string? MaskedAt { get; init; }

    /// <summary>The image offset of the <c>push word [bx+disp16]</c> that reads an entry.</summary>
    [JsonPropertyName("readAt")]
    public string? ReadAt { get; init; }

    /// <summary>The angles, in table order.</summary>
    [JsonPropertyName("values")]
    public List<int>? Values { get; init; }
}
