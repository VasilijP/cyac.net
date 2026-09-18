using System.IO.Compression;
using System.Security.Cryptography;

namespace CYAC.Port.Transform.Input;

/// <summary>How a discovered input was tied to its canonical name.</summary>
public enum InputMatch
{
    /// <summary>
    /// Its size and SHA-256 are a known distribution's file: identified by CONTENT, whatever it is
    /// called and wherever it sits.
    /// </summary>
    Content,

    /// <summary>
    /// Only its file name matches; its content is no known distribution's file — a different version,
    /// a damaged copy, or authored test data.
    /// </summary>
    Name,

    /// <summary>Read back from a manifest: name, size and digest only, no content.</summary>
    Recorded,
}

/// <summary>
/// One discovered original file: what it is, where it came from, what it hashes to — and its bytes.
/// </summary>
/// <param name="Name">The canonical (lower-case) file name.</param>
/// <param name="Provenance">
/// Where it was found: an absolute path, or <c>&lt;zip&gt; › folder/FILE.EXT</c> for a zip entry (one more
/// <c>›</c> per zip level).  For display and diagnosis only — the content never has to be opened again.
/// </param>
/// <param name="Size">Its length in bytes.</param>
/// <param name="Sha256">Its lower-case hex SHA-256 digest.</param>
/// <param name="Role">What the transform uses it for.</param>
public sealed record InputFile(string Name, string Provenance, long Size, string Sha256, InputRole Role)
{
    private readonly byte[]? _content;

    /// <summary>Creates a discovered input that carries its content.</summary>
    /// <param name="name">The canonical (lower-case) file name.</param>
    /// <param name="provenance">Where it was found.</param>
    /// <param name="content">Its bytes, exactly as they hashed.</param>
    /// <param name="sha256">Its lower-case hex SHA-256 digest.</param>
    /// <param name="role">What the transform uses it for.</param>
    /// <param name="matchedBy">How it was tied to <paramref name="name"/>.</param>
    public InputFile(
        string name, string provenance, byte[] content, string sha256, InputRole role, InputMatch matchedBy)
        : this(name, provenance, content?.LongLength ?? throw new ArgumentNullException(nameof(content)), sha256, role)
    {
        _content = content;
        MatchedBy = matchedBy;
    }

    /// <summary>How the file was tied to its canonical name.</summary>
    public InputMatch MatchedBy { get; init; } = InputMatch.Recorded;

    /// <summary>Whether the bytes are available (false for an input read back from a manifest).</summary>
    public bool HasContent => _content is not null;

    /// <summary>A copy of the file's bytes — the same bytes that produced <see cref="Sha256"/>.</summary>
    /// <exception cref="InvalidOperationException">The input was read back from a manifest.</exception>
    public byte[] ReadAllBytes() =>
        _content is null
            ? throw new InvalidOperationException(
                $"{Name}: this input was read back from a manifest and carries no content; locate the originals again")
            : (byte[])_content.Clone();
}

/// <summary>What kind of note the scan made about something it met.</summary>
public enum InputDiagnosticKind
{
    /// <summary>Named like a required file, but its content is no known distribution's.</summary>
    WrongContent,

    /// <summary>Another copy of a file already found; the shallowest copy is the one used.</summary>
    Duplicate,

    /// <summary>An archive format the scan cannot open (7z, rar, …): extract it first.</summary>
    UnsupportedArchive,

    /// <summary>A zip that could not be read.</summary>
    UnreadableArchive,

    /// <summary>A zip inside a zip inside a zip: deeper than the scan goes.</summary>
    NestedTooDeep,

    /// <summary>A plain file that could not be read.</summary>
    Unreadable,

    /// <summary>The scan stopped at its entry limit.</summary>
    ScanLimit,
}

/// <summary>One note the scan made.</summary>
/// <param name="Kind">What kind of note it is.</param>
/// <param name="Provenance">Where it was met.</param>
/// <param name="Message">What to tell a person.</param>
public sealed record InputDiagnostic(InputDiagnosticKind Kind, string Provenance, string Message);

/// <summary>
/// The result of searching one or more locations for the shipping originals: the files found, the
/// files missing, which known distribution (if any) they are, and every note the search made.
/// </summary>
/// <remarks>
/// <para>
/// This is the P0 hash manifest, widened by P1 of: someone drops one or more files — plain or zipped,
/// with or without folders inside — and the port has to find what it needs.  So a location may be a
/// directory (searched recursively) or a <c>.zip</c> (entries in any folder; one zip inside a zip is
/// opened too), and several locations are searched as one.
/// </para>
/// <para>
/// <b>Identification is by content.</b>  Only files whose size is one of the catalog's sizes are hashed,
/// and a size + SHA-256 pair names the file whatever it is called.  A file whose NAME is a required name
/// but whose content matches nothing is still taken — that keeps authored test sets and
/// <c>--allow-unknown-version</c> working — and it is reported, because for a player it means "a
/// different version or a damaged copy".  When a file exists more than once, the shallowest copy wins
/// (a plain file before a zip entry before a nested-zip entry), then ordinal provenance order.
/// </para>
/// </remarks>
public sealed class InputSet
{
    /// <summary>Separates a zip from the entry inside it in a provenance string.</summary>
    public const string ProvenanceSeparator = " › ";

    /// <summary>Separates several searched locations in <see cref="Directory"/>.</summary>
    public const string RootSeparator = " | ";

    /// <summary>The largest zip-inside-a-zip the scan will open in memory.</summary>
    public const long MaxNestedArchiveBytes = 64L * 1024 * 1024;

    /// <summary>How many directory levels below a location the scan descends.</summary>
    public const int MaxDirectoryDepth = 12;

    /// <summary>How many files and zip entries the scan looks at before it stops.</summary>
    public const int MaxEntries = 250_000;

    private static readonly string[] UnsupportedArchiveExtensions =
        [".7z", ".rar", ".arj", ".lha", ".lzh", ".cab", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".iso"];

    private InputSet(
        IReadOnlyList<string> roots,
        IReadOnlyList<InputFile> files,
        IReadOnlyList<string> missing,
        KnownDistribution? distribution,
        IReadOnlyList<InputDiagnostic> diagnostics)
    {
        Roots = roots;
        Files = files;
        Missing = missing;
        Distribution = distribution;
        Diagnostics = diagnostics;
    }

    /// <summary>The absolute locations that were searched.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>
    /// The searched locations as one string (joined by <see cref="RootSeparator"/>) — what the manifest
    /// records, and what a later <c>--verify</c> searches again.
    /// </summary>
    public string Directory => string.Join(RootSeparator, Roots);

    /// <summary>Every recognised input file found, in <c>yeager.exe</c>, archive, save order.</summary>
    public IReadOnlyList<InputFile> Files { get; }

    /// <summary>Required file names that were not found (see <see cref="KnownDistributions.RequiredNames"/>).</summary>
    public IReadOnlyList<string> Missing { get; }

    /// <summary>The matched distribution, or <see langword="null"/> when the set is unrecognised.</summary>
    public KnownDistribution? Distribution { get; }

    /// <summary>Everything the search noticed besides the files themselves, in the order met.</summary>
    public IReadOnlyList<InputDiagnostic> Diagnostics { get; }

    /// <summary>True when every required file was found.</summary>
    public bool IsComplete => Missing.Count == 0;

    /// <summary>The <c>yeager.exe</c> input, when present.</summary>
    public InputFile? Executable => Find(KnownDistributions.ExecutableName);

    /// <summary>The <c>yeager.cfg</c> save, when present. Its absence is not an error.</summary>
    public InputFile? Save => Find(KnownDistributions.ConfigName);

    /// <summary>The six EALIB archives that were found, in install order.</summary>
    public IReadOnlyList<InputFile> Archives =>
        [.. KnownDistributions.ArchiveNames.Select(Find).Where(f => f is not null).Select(f => f!)];

    /// <summary>Looks a recognised input up by its canonical name.</summary>
    /// <param name="name">A canonical file name such as <c>"2b.lib"</c>.</param>
    public InputFile? Find(string name) =>
        Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Searches one location against the known distributions.</summary>
    /// <param name="location">A directory (searched recursively) or a <c>.zip</c>.</param>
    /// <exception cref="DirectoryNotFoundException">The location does not exist.</exception>
    public static InputSet Scan(string location) => Scan([location], KnownDistributions.All);

    /// <summary>Searches one location against a given catalog.</summary>
    /// <param name="location">A directory (searched recursively) or a <c>.zip</c>.</param>
    /// <param name="catalog">The distributions that identify files by content.</param>
    /// <exception cref="DirectoryNotFoundException">The location does not exist.</exception>
    public static InputSet Scan(string location, IReadOnlyList<KnownDistribution> catalog) =>
        Scan([location], catalog);

    /// <summary>Searches several locations as one: discover, hash, identify, match.</summary>
    /// <param name="locations">Directories and/or <c>.zip</c> files.</param>
    /// <param name="catalog">The distributions that identify files by content.</param>
    /// <exception cref="DirectoryNotFoundException">A location does not exist.</exception>
    public static InputSet Scan(IReadOnlyList<string> locations, IReadOnlyList<KnownDistribution> catalog)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(catalog);

        List<string> roots = locations.Select(Path.GetFullPath).ToList();
        foreach (string root in roots)
        {
            if (!File.Exists(root) && !System.IO.Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"no such file or directory: {root}");
            }
        }

        Dictionary<(long Size, string Sha256), string> known = new Dictionary<(long Size, string Sha256), string>();
        HashSet<long> sizes = new HashSet<long>();
        foreach (KnownDistribution distribution in catalog)
        {
            foreach ((string name, KnownFile file) in distribution.Files)
            {
                known.TryAdd((file.Size, file.Sha256.ToLowerInvariant()), name.ToLowerInvariant());
                sizes.Add(file.Size);
            }
        }

        List<InputDiagnostic> diagnostics = new List<InputDiagnostic>();
        Scanner scanner = new Scanner(sizes, diagnostics);
        foreach (string root in roots)
        {
            scanner.Search(root);
        }

        List<Hit> hits = new List<Hit>();
        foreach (Candidate candidate in scanner.Candidates
                     .OrderBy(c => c.Depth)
                     .ThenBy(c => c.Provenance, StringComparer.Ordinal))
        {
            byte[] bytes;
            try
            {
                bytes = candidate.Content ?? File.ReadAllBytes(candidate.Provenance);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new InputDiagnostic(
                    InputDiagnosticKind.Unreadable, candidate.Provenance, $"could not be read: {ex.Message}"));
                continue;
            }

            string sha = Digest(bytes);
            if (known.TryGetValue((bytes.LongLength, sha), out string? canonical))
            {
                hits.Add(new Hit(canonical, InputMatch.Content, candidate.Provenance, bytes, sha));
            }
            else if (KnownDistributions.RoleOf(candidate.FileName) is not null)
            {
                hits.Add(new Hit(
                    candidate.FileName.ToLowerInvariant(), InputMatch.Name, candidate.Provenance, bytes, sha));
            }
        }

        List<InputFile> files = new List<InputFile>();
        List<string> missing = new List<string>();
        foreach (string canonical in KnownDistributions.RequiredNames.Append(KnownDistributions.ConfigName))
        {
            bool required = canonical != KnownDistributions.ConfigName;
            List<Hit> byContent = hits.Where(h => h.Canonical == canonical && h.Match == InputMatch.Content).ToList();
            List<Hit> byName = hits.Where(h => h.Canonical == canonical && h.Match == InputMatch.Name).ToList();
            Hit? chosen = byContent.FirstOrDefault() ?? byName.FirstOrDefault();
            if (chosen is null)
            {
                if (required)
                {
                    missing.Add(canonical);
                }

                continue;
            }

            files.Add(new InputFile(
                canonical, chosen.Provenance, chosen.Content, chosen.Sha256,
                KnownDistributions.RoleOf(canonical)!.Value, chosen.Match));

            List<Hit> sameKind = chosen.Match == InputMatch.Content ? byContent : byName;
            foreach (Hit copy in sameKind.Skip(1).Where(h => h.Sha256 == chosen.Sha256))
            {
                diagnostics.Add(new InputDiagnostic(
                    InputDiagnosticKind.Duplicate, copy.Provenance,
                    $"another copy of {canonical}; using {chosen.Provenance}"));
            }

            if (!required)
            {
                continue;
            }

            foreach (Hit impostor in byName)
            {
                diagnostics.Add(new InputDiagnostic(
                    InputDiagnosticKind.WrongContent, impostor.Provenance,
                    $"named like {canonical}, but its content ({impostor.Content.Length:N0} B, sha256 " +
                    $"{impostor.Sha256[..12]}…) is not {canonical} of any known distribution — a different " +
                    "version or a damaged copy"));
            }
        }

        return new InputSet(
            roots,
            files,
            missing,
            missing.Count == 0 ? KnownDistributions.Match(files, catalog) : null,
            diagnostics);
    }

    /// <summary>The lower-case hex SHA-256 of a file (platform: FIPS 180-4).</summary>
    /// <param name="path">The file to hash.</param>
    public static string Digest(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>The lower-case hex SHA-256 of a byte block.</summary>
    /// <param name="bytes">The bytes to hash.</param>
    public static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsZip(string fileName) =>
        fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnsupportedArchive(string fileName) =>
        UnsupportedArchiveExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private sealed record Candidate(string FileName, string Provenance, long Size, int Depth, byte[]? Content);

    private sealed record Hit(string Canonical, InputMatch Match, string Provenance, byte[] Content, string Sha256);

    /// <summary>
    /// Walks directories and zips and keeps only the entries worth hashing: a catalog size, or a
    /// required name.  Zip entries are read while the archive is open; plain files are read later.
    /// </summary>
    private sealed class Scanner(HashSet<long> sizes, List<InputDiagnostic> diagnostics)
    {
        private int _entries;

        public List<Candidate> Candidates { get; } = [];

        public void Search(string root)
        {
            if (File.Exists(root))
            {
                AddFile(root);
                return;
            }

            EnumerationOptions options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = MaxDirectoryDepth,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            };
            foreach (string path in System.IO.Directory.EnumerateFiles(root, "*", options))
            {
                if (!CountEntry(path))
                {
                    return;
                }

                AddFile(path);
            }
        }

        private void AddFile(string path)
        {
            string name = Path.GetFileName(path);
            try
            {
                if (IsZip(name))
                {
                    using FileStream stream = File.OpenRead(path);
                    SearchZip(stream, path, depth: 1);
                    return;
                }

                if (IsUnsupportedArchive(name))
                {
                    diagnostics.Add(Unsupported(path));
                    return;
                }

                long size = new FileInfo(path).Length;
                if (IsInteresting(name, size))
                {
                    Candidates.Add(new Candidate(name, path, size, 0, null));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new InputDiagnostic(
                    InputDiagnosticKind.Unreadable, path, $"could not be read: {ex.Message}"));
            }
        }

        // `depth` is how deep this zip's ENTRIES sit: 1 for a zip on disk, 2 for a zip inside it.
        private void SearchZip(Stream stream, string provenance, int depth)
        {
            try
            {
                using ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (entry.Name.Length == 0)
                    {
                        continue;   // a folder entry
                    }

                    string at = provenance + ProvenanceSeparator + entry.FullName;
                    if (!CountEntry(at))
                    {
                        return;
                    }

                    try
                    {
                        SearchZipEntry(entry, at, depth);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
                    {
                        diagnostics.Add(new InputDiagnostic(
                            InputDiagnosticKind.UnreadableArchive, at, $"this zip entry could not be read: {ex.Message}"));
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                diagnostics.Add(new InputDiagnostic(
                    InputDiagnosticKind.UnreadableArchive, provenance, $"not a readable zip archive: {ex.Message}"));
            }
        }

        private void SearchZipEntry(ZipArchiveEntry entry, string at, int depth)
        {
            if (IsZip(entry.Name))
            {
                if (depth >= 2)
                {
                    diagnostics.Add(new InputDiagnostic(
                        InputDiagnosticKind.NestedTooDeep, at,
                        "a zip inside a zip inside a zip is not opened; extract the outer one first"));
                    return;
                }

                if (entry.Length > MaxNestedArchiveBytes)
                {
                    diagnostics.Add(new InputDiagnostic(
                        InputDiagnosticKind.ScanLimit, at,
                        $"a zip inside a zip larger than {MaxNestedArchiveBytes / (1024 * 1024)} MB is not opened; extract it first"));
                    return;
                }

                using MemoryStream inner = new MemoryStream(Read(entry), writable: false);
                SearchZip(inner, at, depth + 1);
                return;
            }

            if (IsUnsupportedArchive(entry.Name))
            {
                diagnostics.Add(Unsupported(at));
                return;
            }

            if (IsInteresting(entry.Name, entry.Length))
            {
                Candidates.Add(new Candidate(entry.Name, at, entry.Length, depth, Read(entry)));
            }
        }

        private bool IsInteresting(string fileName, long size) =>
            sizes.Contains(size) || KnownDistributions.RoleOf(fileName) is not null;

        private bool CountEntry(string at)
        {
            if (++_entries <= MaxEntries)
            {
                return true;
            }

            if (_entries == MaxEntries + 1)
            {
                diagnostics.Add(new InputDiagnostic(
                    InputDiagnosticKind.ScanLimit, at,
                    $"the search stopped after {MaxEntries:N0} files and zip entries; point it at a smaller folder"));
            }

            return false;
        }

        private static InputDiagnostic Unsupported(string at) =>
            new(InputDiagnosticKind.UnsupportedArchive, at,
                "an archive format the port cannot open; extract it, or re-pack it as a .zip");

        private static byte[] Read(ZipArchiveEntry entry)
        {
            using Stream stream = entry.Open();
            byte[] bytes = new byte[entry.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
    }
}
