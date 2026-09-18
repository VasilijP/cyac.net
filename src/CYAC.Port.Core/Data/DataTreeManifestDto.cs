using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

// The RUNTIME's read view of <data>/manifest.json.  The tool's own writer model
// (CYAC.Port.Transform/Manifest) carries more — per-output fidelity notes, the input digests, the
// totals — but the port needs only what tells it whether this tree is one it can use and what it
// contains, so this declares that and ignores the rest (System.Text.Json skips unknown members).
//
// Reading the manifest is how DataLocator distinguishes a data tree from any other directory, so
// this is deliberately tolerant: every field is optional and a missing one means "not stated".

/// <summary>The distribution the tree's originals matched.</summary>
public sealed class DataTreeDistributionDto
{
    /// <summary>The known-version id, e.g. <c>cyac-1.0-project-reference</c>.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>A human-readable description of that version.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Whether the input files matched it by SHA-256.</summary>
    [JsonPropertyName("verified")]
    public bool Verified { get; init; }
}

/// <summary>One original file the tree was built from.</summary>
public sealed class DataTreeInputDto
{
    /// <summary>The file's name, e.g. <c>2b.lib</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Its size in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }

    /// <summary>Its SHA-256, lower-case hex.</summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    /// <summary>Its role: <c>executable</c>, <c>archive</c> or <c>save</c>.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }
}

/// <summary>The runtime's read view of <c>&lt;data&gt;/manifest.json</c>.</summary>
public sealed class DataTreeManifestDto
{
    /// <summary>The tool that wrote the tree; must be <c>cyac-transform</c>.</summary>
    [JsonPropertyName("tool")]
    public string? Tool { get; init; }

    /// <summary>That tool's version.</summary>
    [JsonPropertyName("tool_version")]
    public string? ToolVersion { get; init; }

    /// <summary>When it was written, ISO-8601 UTC.</summary>
    [JsonPropertyName("generated_utc")]
    public string? GeneratedUtc { get; init; }

    /// <summary>Whether the originals matched a known distribution.</summary>
    [JsonPropertyName("source_verified")]
    public bool SourceVerified { get; init; }

    /// <summary>Which distribution, when one matched.</summary>
    [JsonPropertyName("distribution")]
    public DataTreeDistributionDto? Distribution { get; init; }

    /// <summary>The families the run applied — what the tree contains.</summary>
    [JsonPropertyName("families")]
    public List<string>? Families { get; init; }

    /// <summary>The original files the tree was built from.</summary>
    [JsonPropertyName("inputs")]
    public List<DataTreeInputDto>? Inputs { get; init; }
}
