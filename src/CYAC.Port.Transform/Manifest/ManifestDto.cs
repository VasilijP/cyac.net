using System.Text.Json.Serialization;

namespace CYAC.Port.Transform.Manifest;

// Wire-format DTOs for <data>/manifest.json.  Same doctrine as CYAC.Port.Core/Schema/SchemaJson.cs:
// the JSON uses snake_case keys and a flat shape, the public model (TransformManifest) stays clean,
// and (de)serialisation goes through the source-generated context so the tool stays
// trimming-friendly and no reflection-based serialiser is ever reached.

internal sealed class ManifestDto
{
    [JsonPropertyName("tool")]
    public string? Tool { get; init; }

    [JsonPropertyName("tool_version")]
    public string? ToolVersion { get; init; }

    /// <summary>The tree's layout version; absent in manifests written before it existed (format 0).</summary>
    [JsonPropertyName("tree_format")]
    public int? TreeFormat { get; init; }

    [JsonPropertyName("generated_utc")]
    public string? GeneratedUtc { get; init; }

    [JsonPropertyName("source_directory")]
    public string? SourceDirectory { get; init; }

    [JsonPropertyName("source_verified")]
    public bool SourceVerified { get; init; }

    [JsonPropertyName("distribution")]
    public DistributionDto? Distribution { get; init; }

    [JsonPropertyName("families")]
    public List<string>? Families { get; init; }

    [JsonPropertyName("inputs")]
    public List<InputDto>? Inputs { get; init; }

    [JsonPropertyName("outputs")]
    public List<OutputDto>? Outputs { get; init; }

    [JsonPropertyName("totals")]
    public TotalsDto? Totals { get; init; }
}

internal sealed class DistributionDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("verified")]
    public bool Verified { get; init; }
}

internal sealed class InputDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>Where the file was found (a path, or <c>zip › entry</c>); informational.</summary>
    [JsonPropertyName("provenance")]
    public string? Provenance { get; init; }
}

internal sealed class OutputDto
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("family")]
    public string? Family { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("source")]
    public SourceDto? Source { get; init; }

    [JsonPropertyName("fidelity")]
    public string? Fidelity { get; init; }

    [JsonPropertyName("fidelity_note")]
    public string? FidelityNote { get; init; }

    [JsonPropertyName("unknown_bytes")]
    public int UnknownBytes { get; init; }

    [JsonPropertyName("unexplained_bytes")]
    public int UnexplainedBytes { get; init; }

    [JsonPropertyName("code_bytes")]
    public int CodeBytes { get; init; }
}

internal sealed class SourceDto
{
    [JsonPropertyName("file")]
    public string? File { get; init; }

    [JsonPropertyName("entry")]
    public string? Entry { get; init; }

    [JsonPropertyName("offset")]
    public int? Offset { get; init; }

    [JsonPropertyName("length")]
    public int? Length { get; init; }
}

internal sealed class TotalsDto
{
    [JsonPropertyName("outputs")]
    public int Outputs { get; init; }

    [JsonPropertyName("exact")]
    public int Exact { get; init; }

    [JsonPropertyName("canonical")]
    public int Canonical { get; init; }

    [JsonPropertyName("lossy")]
    public int Lossy { get; init; }

    [JsonPropertyName("generated")]
    public int Generated { get; init; }

    [JsonPropertyName("unverified")]
    public int Unverified { get; init; }

    [JsonPropertyName("unknown_bytes")]
    public int UnknownBytes { get; init; }

    [JsonPropertyName("unexplained_bytes")]
    public int UnexplainedBytes { get; init; }

    [JsonPropertyName("code_bytes")]
    public int CodeBytes { get; init; }
}
