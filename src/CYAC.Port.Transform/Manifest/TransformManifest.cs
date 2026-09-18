using System.Reflection;
using System.Text.Json;
using CYAC.Port.Transform.Input;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Manifest;

/// <summary>Where an output's bytes came from in the original distribution.</summary>
/// <param name="File">The original file name, e.g. <c>"2a.lib"</c>.</param>
/// <param name="Entry">The EALIB member name, when the source is an archive member.</param>
/// <param name="Offset">The member's stored offset inside the archive, when applicable.</param>
/// <param name="Length">The member's stored length, when applicable.</param>
public sealed record ManifestSource(string File, string? Entry = null, int? Offset = null, int? Length = null);

/// <summary>One row of <c>manifest.json</c>'s <c>outputs[]</c>.</summary>
/// <param name="Path">The output's path relative to the data root.</param>
/// <param name="Family">The family that produced it.</param>
/// <param name="Role">Data / view / raw.</param>
/// <param name="Source">Where its bytes came from, or <see langword="null"/> for a pure generation.</param>
/// <param name="Fidelity">The verified (or, before <c>--verify</c>, the promised) fidelity.</param>
/// <param name="FidelityNote">Why the fidelity is not exact, when it is not.</param>
/// <param name="UnknownBytes">Bytes emitted as named <c>unknown_0xNN</c> fields.</param>
/// <param name="UnexplainedBytes">Bytes carried with no explanation at all (raw outputs).</param>
/// <param name="CodeBytes">Bytes of machine code the output records rather than converts.</param>
public sealed record ManifestOutput(
    string Path,
    string Family,
    OutputRole Role,
    ManifestSource? Source,
    OutputFidelity Fidelity,
    string? FidelityNote,
    int UnknownBytes,
    int UnexplainedBytes,
    int CodeBytes = 0);

/// <summary>
/// <c>&lt;data&gt;/manifest.json</c> — what the tool read, what it wrote, and how faithfully.
/// </summary>
/// <remarks>
/// Shape and intent: (the data tree) and §4 (input recognition).  The manifest is the tree's index
/// and the input to <c>--verify &lt;data-dir&gt;</c>, which is why it records
/// <see cref="SourceDirectory"/>: a later verify has to find the originals again, and the plan's
/// `--verify <![CDATA[<data-dir>]]>` form takes no original directory.
/// </remarks>
public sealed class TransformManifest
{
    /// <summary>The manifest's file name inside the data tree.</summary>
    public const string FileName = "manifest.json";

    /// <summary>The tool's name, as written into the manifest.</summary>
    public const string ToolName = "cyac-transform";

    /// <summary>
    /// The tool's version — the assembly's informational version, with the source-control suffix
    /// the SDK appends (<c>1.2.3+&lt;commit&gt;</c>) trimmed off, so a manifest stays comparable
    /// between builds of the same tool version.
    /// </summary>
    public static string ToolVersion { get; } = TrimBuildMetadata(
        typeof(TransformManifest).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(TransformManifest).Assembly.GetName().Version?.ToString()
        ?? "0.0.0");

    /// <summary>
    /// The version of the tree's layout: which documents exist, where, and in what shape.  Bump it
    /// whenever a tree written by an older build can no longer be read as-is, so a startup check can
    /// tell a stale tree from a current one without comparing tool versions alone.
    /// </summary>
    /// <remarks>
    /// 1: the manifest records the format and each input's provenance.
    /// 2: <c>exe/tables/advisor.json</c> is required; the advisor no longer knows its string offsets.
    /// 3: <c>exe/tables/create_mission.json</c> and <c>exe/tables/effect_look.json</c> are required; the
    /// custom-mission builder no longer knows its formation offsets, nor the renderer its debris angles.
    /// 4: <c>exe/tables/create_mission.json</c> carries an <c>altitudeFeet</c> section and
    /// <c>exe/tables/world.json</c> is required; the builder no longer knows its altitude table, nor the
    /// cloud deck its lattice.  The runtime also reads <c>combat_constants.json</c>'s
    /// <c>phaseAttributes</c> for the enemy gear rule, the six flyable display names from
    /// <c>aircraft/*.json</c>, and the in-flight screen's words and formats from <c>exe/strings.json</c>
    /// (all three were in format 3 trees already).
    /// </remarks>
    public const int CurrentTreeFormat = 4;

    private static string TrimBuildMetadata(string version)
    {
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }

    /// <summary>
    /// The tree's layout version: <see cref="CurrentTreeFormat"/> for a tree this build writes, and 0 for
    /// a manifest that predates the field.
    /// </summary>
    public int TreeFormat { get; init; } = CurrentTreeFormat;

    /// <summary>When the tree was generated (ISO-8601, UTC).</summary>
    public required string GeneratedUtc { get; init; }

    /// <summary>
    /// The tool version a read-back manifest says wrote it, or <see langword="null"/> for a manifest
    /// built in this process (which is always <see cref="ToolVersion"/>).
    /// </summary>
    public string? RecordedToolVersion { get; init; }

    /// <summary>The directory of originals the tree was built from.</summary>
    public required string SourceDirectory { get; init; }

    /// <summary>Whether the inputs matched a known distribution.</summary>
    public required bool SourceVerified { get; init; }

    /// <summary>The matched distribution's id, or <see langword="null"/>.</summary>
    public string? DistributionId { get; init; }

    /// <summary>The matched distribution's description, or <see langword="null"/>.</summary>
    public string? DistributionDescription { get; init; }

    /// <summary>The families this run applied.</summary>
    public required IReadOnlyList<string> Families { get; init; }

    /// <summary>The inputs, hashed.</summary>
    public required IReadOnlyList<InputFile> Inputs { get; init; }

    /// <summary>Everything written into the tree.</summary>
    public required IReadOnlyList<ManifestOutput> Outputs { get; init; }

    /// <summary>Outputs whose fidelity is a given value.</summary>
    /// <param name="fidelity">The fidelity to count.</param>
    public int Count(OutputFidelity fidelity) => Outputs.Count(o => o.Fidelity == fidelity);

    /// <summary>The law-L4 burn-down: named-but-unknown bytes across the tree.</summary>
    public int UnknownBytes => Outputs.Sum(o => o.UnknownBytes);

    /// <summary>Bytes carried with no explanation at all — the raw outputs' size.</summary>
    public int UnexplainedBytes => Outputs.Sum(o => o.UnexplainedBytes);

    /// <summary>Bytes of machine code recorded rather than converted — explained, not open.</summary>
    public int CodeBytes => Outputs.Sum(o => o.CodeBytes);

    /// <summary>Serialises the manifest as indented JSON.</summary>
    public byte[] ToJson()
    {
        ManifestDto dto = new ManifestDto
        {
            Tool = ToolName,
            ToolVersion = ToolVersion,
            TreeFormat = TreeFormat,
            GeneratedUtc = GeneratedUtc,
            SourceDirectory = SourceDirectory,
            SourceVerified = SourceVerified,
            Distribution = new DistributionDto
            {
                Id = DistributionId,
                Description = DistributionDescription,
                Verified = SourceVerified,
            },
            Families = [.. Families],
            Inputs =
            [
                .. Inputs.Select(i => new InputDto
                {
                    Name = i.Name,
                    Size = i.Size,
                    Sha256 = i.Sha256,
                    Role = i.Role.ToString().ToLowerInvariant(),
                    Provenance = string.IsNullOrEmpty(i.Provenance) ? null : i.Provenance,
                }),
            ],
            Outputs =
            [
                .. Outputs.Select(o => new OutputDto
                {
                    Path = o.Path,
                    Family = o.Family,
                    Role = o.Role.ToString().ToLowerInvariant(),
                    Source = o.Source is null
                        ? null
                        : new SourceDto
                        {
                            File = o.Source.File,
                            Entry = o.Source.Entry,
                            Offset = o.Source.Offset,
                            Length = o.Source.Length,
                        },
                    Fidelity = o.Fidelity.ToString().ToLowerInvariant(),
                    FidelityNote = o.FidelityNote,
                    UnknownBytes = o.UnknownBytes,
                    UnexplainedBytes = o.UnexplainedBytes,
                    CodeBytes = o.CodeBytes,
                }),
            ],
            Totals = new TotalsDto
            {
                Outputs = Outputs.Count,
                Exact = Count(OutputFidelity.Exact),
                Canonical = Count(OutputFidelity.Canonical),
                Lossy = Count(OutputFidelity.Lossy),
                Generated = Count(OutputFidelity.Generated),
                Unverified = Count(OutputFidelity.Unverified),
                UnknownBytes = UnknownBytes,
                UnexplainedBytes = UnexplainedBytes,
                CodeBytes = CodeBytes,
            },
        };

        return JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.ManifestDto);
    }

    /// <summary>Reads a manifest back from the data tree.</summary>
    /// <param name="json">The <c>manifest.json</c> bytes.</param>
    /// <exception cref="InvalidDataException">The document is not a manifest this tool wrote.</exception>
    public static TransformManifest FromJson(ReadOnlySpan<byte> json)
    {
        ManifestDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.ManifestDto)
                          ?? throw new InvalidDataException("manifest.json is empty");
        if (!string.Equals(dto.Tool, ToolName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"manifest.json was not written by {ToolName}");
        }

        return new TransformManifest
        {
            TreeFormat = dto.TreeFormat ?? 0,
            RecordedToolVersion = dto.ToolVersion,
            GeneratedUtc = dto.GeneratedUtc ?? string.Empty,
            SourceDirectory = dto.SourceDirectory ?? string.Empty,
            SourceVerified = dto.SourceVerified,
            DistributionId = dto.Distribution?.Id,
            DistributionDescription = dto.Distribution?.Description,
            Families = dto.Families ?? [],
            Inputs =
            [
                .. (dto.Inputs ?? []).Select(i => new InputFile(
                    i.Name ?? string.Empty,
                    i.Provenance ?? string.Empty,
                    i.Size,
                    i.Sha256 ?? string.Empty,
                    Enum.Parse<InputRole>(i.Role ?? nameof(InputRole.Archive), ignoreCase: true))),
            ],
            Outputs =
            [
                .. (dto.Outputs ?? []).Select(o => new ManifestOutput(
                    o.Path ?? string.Empty,
                    o.Family ?? string.Empty,
                    Enum.Parse<OutputRole>(o.Role ?? nameof(OutputRole.Data), ignoreCase: true),
                    o.Source is null ? null : new ManifestSource(o.Source.File ?? string.Empty, o.Source.Entry, o.Source.Offset, o.Source.Length),
                    Enum.Parse<OutputFidelity>(o.Fidelity ?? nameof(OutputFidelity.Exact), ignoreCase: true),
                    o.FidelityNote,
                    o.UnknownBytes,
                    o.UnexplainedBytes,
                    o.CodeBytes)),
            ],
        };
    }
}
