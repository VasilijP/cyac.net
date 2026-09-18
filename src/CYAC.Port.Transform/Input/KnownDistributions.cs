namespace CYAC.Port.Transform.Input;

/// <summary>
/// One file of a recognised original distribution: the size and SHA-256 the transform expects.
/// </summary>
/// <param name="Size">The file length in bytes.</param>
/// <param name="Sha256">The lower-case hex SHA-256 digest.</param>
public sealed record KnownFile(long Size, string Sha256);

/// <summary>
/// A recognised set of shipping originals — the P0 "hash manifest" gate.
/// </summary>
/// <param name="Id">A stable identifier written into <c>manifest.json</c>.</param>
/// <param name="Description">A human sentence naming the release this set came from.</param>
/// <param name="Files">Required files, keyed by lower-case file name.</param>
/// <param name="L1ImageSha256">
/// The SHA-256 of the layer-1 program image the static unpacker must produce from this set's
/// <c>yeager.exe</c> at load segment 0x1000 — the project's reference artefact.
/// </param>
/// <param name="L1ExeSha256">
/// The SHA-256 of the reconstructed loadable executable for the same image.  Checking it proves the
/// relocation walker, not just the payload expansion.
/// </param>
public sealed record KnownDistribution(
    string Id,
    string Description,
    IReadOnlyDictionary<string, KnownFile> Files,
    string L1ImageSha256,
    string L1ExeSha256);

/// <summary>
/// The table of original distributions <c>cyac-transform</c> recognises.
/// </summary>
/// <remarks>
/// <para>
/// Digests are <b>knowledge about</b> the originals, not the originals themselves — the no-original-data rule forbids
/// shipping game bytes, and a hash is neither reversible nor a copy.
/// </para>
/// <para>
/// The project's own copy is CYAC v1.0 (project memory "CYAC has 3 DOS versions; ours is most
/// likely v1.0"; the second independent copy checked hashes identically for every file
/// except <c>yeager.cfg</c>, which is a save — project memory ".lib + yeager.exe verified intact").
/// Adding a version means adding an entry here, never relaxing the check.
/// </para>
/// </remarks>
public static class KnownDistributions
{
    /// <summary>The executable every recognised set must contain.</summary>
    public const string ExecutableName = "yeager.exe";

    /// <summary>The save file — optional, and never matched against a digest.</summary>
    public const string ConfigName = "yeager.cfg";

    /// <summary>The six EALIB archives, in install order (disk 1a/1b, 2a/2b, 3a, 4a).</summary>
    public static IReadOnlyList<string> ArchiveNames { get; } =
        ["1a.lib", "1b.lib", "2a.lib", "2b.lib", "3a.lib", "4a.lib"];

    /// <summary>Every file an original directory must contain for the transform to run.</summary>
    public static IReadOnlyList<string> RequiredNames { get; } =
        [ExecutableName, .. ArchiveNames];

    /// <summary>The recognised sets.</summary>
    public static IReadOnlyList<KnownDistribution> All { get; } =
    [
        new KnownDistribution(
            "cyac-1.0-project-reference",
            "Chuck Yeager's Air Combat (EA, 1991), DOS v1.0 — this project's reference copy.",
            new Dictionary<string, KnownFile>(StringComparer.OrdinalIgnoreCase)
            {
                ["yeager.exe"] = new(184_310, "a97e65866f30abfc90513e1724fd8185341dd1a9430f6a62862fa9e9f294f83a"),
                ["1a.lib"] = new(34_426, "a64da3a42616be15937f25f145735e43afd79a32a75f950ea4f067abd5068853"),
                ["1b.lib"] = new(101_681, "dff404f043357d3a663a896e70a0a8b4089387b2e5aad61b7ec3211f67d2fb03"),
                ["2a.lib"] = new(276_276, "c4cbe41a63319c9c5d8f8e8ce1aa5ea90aa0e31b6a0343641406a7bd84c20461"),
                ["2b.lib"] = new(85_683, "48ad7846deaa8e25da55838511f3b8df8c427cd0431c8f19836a8f9d15ec49b9"),
                ["3a.lib"] = new(339_508, "189f8953173b31f5294f6c1daf05bb0094659af4c3a73217f6a433b43d287a0a"),
                ["4a.lib"] = new(292_628, "bcf23ec6babfdc922be196156032da6f48f88b390ebda70ea1e65cf3a1e068f9"),
            },
            "2ceb7a95ba5ad78de461fc754d8c3add3f14b47c82f63813f39d85404b8ff0d2",
            "aa7c2838c0713de3b67d85c2e68a80c12b04ee03d1718add46c4243ed68c02aa"),
    ];

    /// <summary>The role a file name plays, or <see langword="null"/> when it is not an input.</summary>
    /// <param name="fileName">A bare file name; matched case-insensitively.</param>
    public static InputRole? RoleOf(string fileName)
    {
        if (string.Equals(fileName, ExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return InputRole.Executable;
        }

        if (string.Equals(fileName, ConfigName, StringComparison.OrdinalIgnoreCase))
        {
            return InputRole.Save;
        }

        foreach (string archive in ArchiveNames)
        {
            if (string.Equals(fileName, archive, StringComparison.OrdinalIgnoreCase))
            {
                return InputRole.Archive;
            }
        }

        return null;
    }

    /// <summary>
    /// The distribution whose every required file matches <paramref name="files"/> by size and
    /// digest, or <see langword="null"/> when the set is not recognised.
    /// </summary>
    /// <param name="files">The scanned inputs.</param>
    public static KnownDistribution? Match(IReadOnlyList<InputFile> files) => Match(files, All);

    /// <summary>
    /// The distribution of <paramref name="catalog"/> whose every required file matches
    /// <paramref name="files"/> by size and digest, or <see langword="null"/>.
    /// </summary>
    /// <param name="files">The scanned inputs.</param>
    /// <param name="catalog">The distributions to consider (tests inject authored ones).</param>
    public static KnownDistribution? Match(IReadOnlyList<InputFile> files, IReadOnlyList<KnownDistribution> catalog)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(catalog);
        foreach (KnownDistribution candidate in catalog)
        {
            bool ok = true;
            foreach (string required in RequiredNames)
            {
                KnownFile expected = candidate.Files[required];
                InputFile? actual = files.FirstOrDefault(
                    f => string.Equals(f.Name, required, StringComparison.OrdinalIgnoreCase));
                if (actual is null || actual.Size != expected.Size ||
                    !string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return candidate;
            }
        }

        return null;
    }
}
