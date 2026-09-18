using System.Reflection;
using System.Text.Json;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Markings;

/// <summary>Where a class's resolved placements came from.</summary>
public enum MarkingPlacementSource
{
    /// <summary>A diff (<c>placements/&lt;class&gt;.diff.json</c>) applied to the generated base.</summary>
    Diff = 0,

    /// <summary>A full placements document (<c>placements/&lt;class&gt;.json</c>), used as written.</summary>
    FullFile = 1,

    /// <summary>A set handed to <see cref="MarkingLibrary.SetPlacements"/>: an editor's live set.</summary>
    Editor = 2,
}

/// <summary>How one class's placements were resolved.</summary>
/// <param name="Class">The mesh basename.</param>
/// <param name="Source">Where they came from.</param>
/// <param name="Document">The document read: a file path, or the embedded resource's name.</param>
/// <param name="BaseMismatch">
/// For a diff made against a different base (the generator or the data tree changed since), what differs;
/// otherwise null. The placements still resolved.
/// </param>
public sealed record MarkingResolution(string Class, MarkingPlacementSource Source, string Document, string? BaseMismatch)
{
    /// <summary>Whether the class resolved against the base its diff was made for (always true for other sources).</summary>
    public bool BaseMatches => BaseMismatch is null;
}

/// <summary>What <see cref="MarkingLibrary.SavePlacementsDiff"/> wrote.</summary>
/// <param name="Path">The diff file written.</param>
/// <param name="RemovedFullFile">The stale full placements file deleted beside it, or null.</param>
public sealed record MarkingSaveResult(string Path, string? RemovedFullFile);

/// <summary>
/// The port's marking library: the built-in pictures, font and per-class placement diffs embedded in this
/// assembly, optionally overlaid by a directory on disk (<c>fly --markings-dir</c>, the browser's editing target).
/// </summary>
/// <remarks>
/// <para>
/// The pictures and the font are the port's own knowledge, not data from the shipped game: they ship inside
/// <c>CYAC.Port.Core</c> as <c>Markings/pictures/*.json</c> and <c>Markings/font.json</c>. An override directory with
/// the same layout replaces any picture of the same name and the font, file by file.
/// </para>
/// <para>
/// A class's placements ship as a <see cref="MarkingDiff"/> over the <see cref="MarkingBootstrap"/> base, which
/// is derived from the original meshes and so is generated here, per class, on first use. That needs the data
/// tree: a library that should resolve placements is opened with one. The lookup order for a class is: a set
/// handed to <see cref="SetPlacements"/>; the override directory's full <c>placements/&lt;class&gt;.json</c>
/// (used as written, for modders and hand-made sets); its <c>placements/&lt;class&gt;.diff.json</c>; the
/// embedded diff.
/// </para>
/// <para>
/// Pictures are parsed once and shared: a <see cref="MarkingPicture"/> is immutable. A parameterised instance
/// (a <c>glyphs</c> picture with a placement's own text, a colour, wear or bare-metal override) is a new picture
/// built from the same document, cached by its parameters. Every member is thread-safe.
/// </para>
/// </remarks>
public sealed class MarkingLibrary
{
    private const string ResourcePrefix = "CYAC.Port.Core.Markings.";
    private const string DiffSuffix = ".diff";
    private readonly Dictionary<string, string> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MarkingPicture> _pictures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Name, string? Text, SurfaceColor? Color, WearParams? Wear, BareMetalSpec? BareMetal), MarkingPicture> _instances = [];
    private readonly Lock _gate = new();

    // Placements have their own lock: resolving a class builds its base, which reads pictures under _gate.
    private readonly Dictionary<string, PlacementDocument> _placementDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MarkingSet> _placements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MarkingResolution> _resolutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MarkingSet?> _bases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, MarkingSet?>? _baseProvider;
    private readonly Lock _placementGate = new();
    private MeshLibrary? _plainMeshes;

    /// <summary>Opens the built-in library, overlaid by a directory when one is given.</summary>
    /// <param name="overrideDirectory">A directory holding <c>pictures/*.json</c>, <c>font.json</c> and/or <c>placements/*.json</c>, or null.</param>
    /// <param name="tree">
    /// The data tree the placement bases are generated from, or null for a library that only serves pictures
    /// (and full placements files).
    /// </param>
    public MarkingLibrary(string? overrideDirectory = null, DataTree? tree = null)
        : this(overrideDirectory, tree, null)
    {
    }

    /// <summary>Opens the library with an explicit source of placement bases instead of a data tree.</summary>
    /// <param name="overrideDirectory">The override directory, or null.</param>
    /// <param name="baseProvider">The base set for a class (placements keyed), or null when there is none.</param>
    public MarkingLibrary(string? overrideDirectory, Func<string, MarkingSet?> baseProvider)
        : this(overrideDirectory, null, baseProvider ?? throw new ArgumentNullException(nameof(baseProvider)))
    {
    }

    private MarkingLibrary(string? overrideDirectory, DataTree? tree, Func<string, MarkingSet?>? baseProvider)
    {
        Tree = tree;
        _baseProvider = baseProvider ?? (tree is null ? null : GenerateBase);
        Assembly assembly = typeof(MarkingLibrary).Assembly;
        string? fontJson = null;
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using StreamReader reader = new StreamReader(stream);
            string text = reader.ReadToEnd();
            string tail = resource[ResourcePrefix.Length..];
            if (tail == "font.json")
            {
                fontJson = text;
            }
            else if (tail.StartsWith("pictures.", StringComparison.Ordinal))
            {
                _documents[tail["pictures.".Length..^".json".Length]] = text;
            }
            else if (tail.StartsWith("placements.", StringComparison.Ordinal))
            {
                AddPlacementDocument(tail["placements.".Length..^".json".Length], text, resource, fromOverride: false);
            }
        }

        OverrideDirectory = overrideDirectory;
        if (overrideDirectory is not null && Directory.Exists(overrideDirectory))
        {
            string fontPath = Path.Combine(overrideDirectory, "font.json");
            if (File.Exists(fontPath))
            {
                fontJson = File.ReadAllText(fontPath);
            }

            string pictures = Path.Combine(overrideDirectory, "pictures");
            if (Directory.Exists(pictures))
            {
                foreach (string file in Directory.EnumerateFiles(pictures, "*.json"))
                {
                    _documents[Path.GetFileNameWithoutExtension(file)] = File.ReadAllText(file);
                }
            }

            string placements = Path.Combine(overrideDirectory, "placements");
            if (Directory.Exists(placements))
            {
                foreach (string file in Directory.EnumerateFiles(placements, "*.json"))
                {
                    AddPlacementDocument(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file), file, fromOverride: true);
                }
            }
        }

        Font = fontJson is null ? null : MarkingJson.ParseFont(fontJson);
    }

    /// <summary>The directory overlaid on the built-ins, or null.</summary>
    public string? OverrideDirectory { get; }

    /// <summary>The data tree placement bases are generated from, or null.</summary>
    public DataTree? Tree { get; }

    /// <summary>The font, or null when neither the assembly nor the override has one.</summary>
    public MarkingFont? Font { get; }

    /// <summary>The picture names, sorted.</summary>
    public IReadOnlyList<string> Names => _documents.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The raw document of a picture, for a viewer.</summary>
    /// <param name="name">The picture's name.</param>
    public string? DocumentOf(string name) => _documents.GetValueOrDefault(name);

    /// <summary>A picture by name, parsed once.</summary>
    /// <param name="name">The picture's name.</param>
    /// <exception cref="KeyNotFoundException">No such picture.</exception>
    public MarkingPicture Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            if (_pictures.TryGetValue(name, out MarkingPicture? hit))
            {
                return hit;
            }

            if (!_documents.TryGetValue(name, out string? json))
            {
                throw new KeyNotFoundException($"no marking picture '{name}' (have: {string.Join(", ", Names)})");
            }

            MarkingPicture picture = MarkingJson.ParsePicture(json, Font);
            _pictures[name] = picture;
            return picture;
        }
    }

    /// <summary>A picture by name, or null.</summary>
    /// <param name="name">The picture's name.</param>
    public MarkingPicture? TryGet(string name) => _documents.ContainsKey(name) ? Get(name) : null;

    /// <summary>
    /// A picture INSTANCE: the named picture with a placement's own text, colour, wear and/or bare metal.
    /// </summary>
    /// <param name="name">The picture's name.</param>
    /// <param name="text">Text for its <c>glyphs</c> nodes, or null for the picture's own.</param>
    /// <param name="color">The colour for layers declared <c>"color": "@"</c>, or null.</param>
    /// <param name="wear">The picture-wide wear override, or null for the picture's own.</param>
    /// <param name="bareMetal">What a chip exposes, or null for the picture's own.</param>
    /// <returns>The instance (shared by parameters).</returns>
    public MarkingPicture Instance(string name, string? text = null, SurfaceColor? color = null, WearParams? wear = null, BareMetalSpec? bareMetal = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (text is null && color is null && wear is null && bareMetal is null)
        {
            return Get(name);
        }

        lock (_gate)
        {
            (string name, string? text, SurfaceColor? color, WearParams? wear, BareMetalSpec? bareMetal) key = (name, text, color, wear, bareMetal);
            if (_instances.TryGetValue(key, out MarkingPicture? hit))
            {
                return hit;
            }

            if (!_documents.TryGetValue(name, out string? json))
            {
                throw new KeyNotFoundException($"no marking picture '{name}'");
            }

            MarkingPicture picture = MarkingJson.ParsePicture(json, Font, text, color);
            if (wear is { } w)
            {
                picture = picture.WithWear(w);
            }

            if (bareMetal is { } bm)
            {
                picture = picture.WithBareMetal(bm);
            }

            _instances[key] = picture;
            return picture;
        }
    }

    /// <summary>The classes that have placements (a document or an editor's set), sorted.</summary>
    public IReadOnlyList<string> PlacementClasses
    {
        get
        {
            lock (_placementGate)
            {
                return _placementDocuments.Keys.Union(_placements.Keys, StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    /// <summary>How every class resolved so far was resolved, sorted by class.</summary>
    public IReadOnlyList<MarkingResolution> Resolutions
    {
        get
        {
            lock (_placementGate)
            {
                return _resolutions.Values.OrderBy(r => r.Class, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    /// <summary>A class's placements, resolved once and cached, or null when it has none.</summary>
    /// <param name="basename">The mesh basename.</param>
    /// <exception cref="MarkingDiffException">The class's diff names a key its generated base does not have.</exception>
    /// <exception cref="InvalidDataException">The class's document is malformed.</exception>
    /// <exception cref="InvalidOperationException">The class ships a diff and the library has no data tree to generate its base.</exception>
    public MarkingSet? TryGetPlacements(string basename)
    {
        ArgumentNullException.ThrowIfNull(basename);
        lock (_placementGate)
        {
            if (_placements.TryGetValue(basename, out MarkingSet? hit))
            {
                return hit;
            }

            if (!_placementDocuments.TryGetValue(basename, out PlacementDocument? document))
            {
                return null;
            }

            (MarkingSet set, MarkingResolution resolution) = Resolve(basename, document);
            _placements[basename] = set;
            _resolutions[basename] = resolution;
            return set;
        }
    }

    /// <summary>
    /// The generated base a class's diff applies to (placements keyed), built once and cached; null when the library
    /// has no data tree or the tree has no such class.
    /// </summary>
    /// <param name="basename">The mesh basename.</param>
    public MarkingSet? TryGetBase(string basename)
    {
        ArgumentNullException.ThrowIfNull(basename);
        lock (_placementGate)
        {
            return BaseOf(basename);
        }
    }

    /// <summary>
    /// Replaces a class's placements in memory (the editor's live set); nothing is written.
    /// </summary>
    /// <param name="set">The set.</param>
    public void SetPlacements(MarkingSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        lock (_placementGate)
        {
            _placements[set.Class] = set;
            _resolutions[set.Class] = new MarkingResolution(set.Class, MarkingPlacementSource.Editor, "editor", null);
        }
    }

    /// <summary>
    /// Writes a class's placements as a FULL document to <c>&lt;dir&gt;/placements/&lt;class&gt;.json</c> and adopts
    /// them. For hand-made sets and exports; the port's own classes are saved with <see cref="SavePlacementsDiff"/>.
    /// </summary>
    /// <param name="set">The set.</param>
    /// <param name="directory">The target directory (the override directory when null).</param>
    /// <returns>The path written.</returns>
    /// <exception cref="InvalidOperationException">No directory to write to.</exception>
    public string SavePlacements(MarkingSet set, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        string folder = PlacementsFolder(directory);
        string path = Path.Combine(folder, set.Class + ".json");
        string text = set.Serialize();
        File.WriteAllText(path, text);
        lock (_placementGate)
        {
            _placementDocuments[set.Class] = new PlacementDocument(MarkingPlacementSource.FullFile, text, path, FromOverride: true);
        }

        SetPlacements(set);
        return path;
    }

    /// <summary>
    /// Writes a class's placements as a diff over its generated base to <c>&lt;dir&gt;/placements/&lt;class&gt;.diff.json</c>,
    /// deletes a full <c>&lt;class&gt;.json</c> in that same folder (it would win over the diff), and adopts the result.
    /// </summary>
    /// <param name="set">The set; placements that came from the base keep their <see cref="MarkingPlacement.Key"/>.</param>
    /// <param name="directory">The target directory (the override directory when null).</param>
    /// <exception cref="InvalidOperationException">No directory to write to, or no base for the class.</exception>
    public MarkingSaveResult SavePlacementsDiff(MarkingSet set, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        string folder = PlacementsFolder(directory);
        MarkingSet baseSet = TryGetBase(set.Class)
                             ?? throw new InvalidOperationException($"no generated base for '{set.Class}': the library needs a data tree that holds the class");
        MarkingDiff diff = MarkingDiff.Create(baseSet, set, MarkingBootstrap.Generator);
        string path = Path.Combine(folder, set.Class + DiffSuffix + ".json");
        string text = diff.Serialize();
        File.WriteAllText(path, text);

        string full = Path.Combine(folder, set.Class + ".json");
        string? removed = null;
        if (File.Exists(full))
        {
            File.Delete(full);
            removed = full;
        }

        lock (_placementGate)
        {
            _placementDocuments[set.Class] = new PlacementDocument(MarkingPlacementSource.Diff, text, path, FromOverride: true);
            _placements[set.Class] = diff.Apply(baseSet);
            _resolutions[set.Class] = new MarkingResolution(set.Class, MarkingPlacementSource.Diff, path, null);
        }

        return new MarkingSaveResult(path, removed);
    }

    /// <summary>
    /// Where a development checkout keeps the source files: the <c>src/CYAC.Port.Core/Markings</c>
    /// directory found by walking up from <paramref name="start"/>, or null.
    /// </summary>
    /// <param name="start">A directory to start from (the executable's, the data tree's).</param>
    public static string? FindSourceDirectory(string? start)
    {
        DirectoryInfo? dir = start is null ? null : new DirectoryInfo(start);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "CYAC.Port.Core", "Markings");
            if (File.Exists(Path.Combine(candidate, "font.json")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private void AddPlacementDocument(string name, string text, string origin, bool fromOverride)
    {
        bool diff = name.EndsWith(DiffSuffix, StringComparison.OrdinalIgnoreCase);
        string cls = diff ? name[..^DiffSuffix.Length] : name;
        PlacementDocument document = new PlacementDocument(diff ? MarkingPlacementSource.Diff : MarkingPlacementSource.FullFile, text, origin, fromOverride);
        if (!_placementDocuments.TryGetValue(cls, out PlacementDocument? existing) || document.Rank > existing.Rank)
        {
            _placementDocuments[cls] = document;
        }
    }

    private (MarkingSet Set, MarkingResolution Resolution) Resolve(string cls, PlacementDocument document)
    {
        if (document.Kind == MarkingPlacementSource.FullFile)
        {
            return (Parsed(cls, document, () => MarkingSet.Parse(document.Json)), new MarkingResolution(cls, document.Kind, document.Origin, null));
        }

        MarkingDiff diff = Parsed(cls, document, () => MarkingDiff.Parse(document.Json));
        if (!string.Equals(diff.Class, cls, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"placements for '{cls}' ({document.Origin}): the diff names class '{diff.Class}'");
        }

        if (_baseProvider is null)
        {
            throw new InvalidOperationException(
                $"placements for '{cls}' are a diff over the generated base ({document.Origin}); open the marking library with the data tree to resolve them");
        }

        MarkingSet baseSet = BaseOf(cls)
                             ?? throw new InvalidDataException($"placements for '{cls}' ({document.Origin}): the data tree has no such class to generate a base from");
        MarkingSet set = diff.Apply(baseSet);
        return (set, new MarkingResolution(cls, MarkingPlacementSource.Diff, document.Origin, diff.BaseMismatch(baseSet, MarkingBootstrap.Generator)));
    }

    private static T Parsed<T>(string cls, PlacementDocument document, Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException)
        {
            throw new InvalidDataException($"placements for '{cls}' ({document.Origin}): {ex.Message}", ex);
        }
    }

    private MarkingSet? BaseOf(string cls)
    {
        if (_baseProvider is null)
        {
            return null;
        }

        if (!_bases.TryGetValue(cls, out MarkingSet? baseSet))
        {
            baseSet = _baseProvider(cls);
            _bases[cls] = baseSet;
        }

        return baseSet;
    }

    // The base as the browser's Bootstrap button builds it: the unconditioned mesh, the class's nation, these pictures.
    // Runs under _placementGate.
    private MarkingSet? GenerateBase(string cls)
    {
        if (!Tree!.HasExeMesh(cls))
        {
            return null;
        }

        _plainMeshes ??= new MeshLibrary(Tree, MeshConditioning.None);
        return MarkingBootstrap.Propose(_plainMeshes.Get(cls), MarkingNation.ForClass(Tree, cls), this);
    }

    private string PlacementsFolder(string? directory)
    {
        string dir = directory ?? OverrideDirectory ?? throw new InvalidOperationException("no markings directory to save into");
        string folder = Path.Combine(dir, "placements");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private sealed record PlacementDocument(MarkingPlacementSource Kind, string Json, string Origin, bool FromOverride)
    {
        // An override beats a built-in; within one source a full file beats a diff.
        public int Rank => (FromOverride ? 2 : 0) + (Kind == MarkingPlacementSource.FullFile ? 1 : 0);
    }
}
