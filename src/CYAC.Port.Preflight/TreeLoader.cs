using System.Text.Json;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Markings;
using CYAC.Port.Core.Model.Aircraft;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Preflight;

/// <summary>A document the loader could not read.</summary>
/// <param name="Document">The tree-relative document (or folder) that failed.</param>
/// <param name="Reason">Why.</param>
public sealed record TreeLoadFailure(string Document, string Reason);

/// <summary>What <see cref="TreeLoader.Load"/> produced.</summary>
/// <param name="Tree">The opened tree with its global tables installed, or null on failure.</param>
/// <param name="Counts">How many documents of each kind were read, in reading order.</param>
/// <param name="Failure">The first document that failed, or null.</param>
public sealed record TreeLoadResult(
    DataTree? Tree, IReadOnlyList<(string Kind, int Documents)> Counts, TreeLoadFailure? Failure)
{
    /// <summary>How many documents were read in total.</summary>
    public int Documents => Counts.Sum(c => c.Documents);
}

/// <summary>What <see cref="TreeLoader.Enhance"/> produced.</summary>
/// <param name="Meshes">The conditioned mesh library with every class built, or null on failure.</param>
/// <param name="Markings">The port's marking library, or null on failure.</param>
/// <param name="Classes">How many mesh classes were built.</param>
/// <param name="Lods">How many LODs they hold.</param>
/// <param name="Failure">The class that failed, or null.</param>
public sealed record TreeEnhanceResult(
    MeshLibrary? Meshes, MarkingLibrary? Markings, int Classes, int Lods, TreeLoadFailure? Failure)
{
    /// <summary>How each class's marking placements resolved (a diff over the generated base, or a full file).</summary>
    public IReadOnlyList<MarkingResolution> Placements { get; init; } = [];
}

/// <summary>
/// Reads a data tree the way the game will, all at once, so a damaged document is found before a sortie
/// needs it.
/// </summary>
/// <remarks>
/// Every public accessor of <see cref="DataTree"/> is touched over every document of its kind that the
/// tree holds, except <see cref="DataTree.ProgramImage"/>, which is a development artefact the game no
/// longer reads.  Documents inside a folder are read one by one first, so a failure names the file
/// rather than the folder.
/// </remarks>
public static class TreeLoader
{
    /// <summary>The cockpit video mode the port draws.</summary>
    public const string CockpitMode = "vga";

    /// <summary>Opens a tree, installs its global tables and reads every document the game reads.</summary>
    /// <param name="dataDirectory">The tree's root.</param>
    public static TreeLoadResult Load(string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        List<(string Kind, int Documents)> counts = new List<(string Kind, int Documents)>();
        DataTreeInfo? info;
        try
        {
            info = DataLocator.TryRead(dataDirectory);
        }
        catch (DataTreeNotFoundException ex)
        {
            return new TreeLoadResult(null, counts, new TreeLoadFailure(DataLocator.ManifestFileName, ex.Message));
        }

        if (info is null)
        {
            return new TreeLoadResult(
                null, counts, new TreeLoadFailure(DataLocator.ManifestFileName, $"{dataDirectory} holds no manifest"));
        }

        DataTree tree = new DataTree(info);
        PortDataJsonContext json = PortDataJsonContext.Readable;
        Reader reader = new Reader(tree, counts);
        try
        {
            // The global tables first, in the order the game installs them: a mission document is only
            // readable once the vocabulary it is written in is loaded.  Each prerequisite is read on its
            // own so a failure names it; InstallGlobalTables then finds everything cached.
            reader.One("exe tables", "exe/tables/sine_quarter.json", () => _ = tree.ScalarTable("sine_quarter"));
            reader.One("exe tables", EffectLookTableDto.DataPath, () => _ = tree.DebrisAngles);
            // The enemy gear rule's phase table is a section of a document counted below.
            reader.One(
                "exe tables", "exe/tables/combat_constants.json (phaseAttributes)", () => _ = tree.PhaseAttributes,
                count: false);
            reader.One("class registry", ClassRegistry.DataPath, () => _ = tree.Classes);
            reader.One("mission vocabulary", MissionVocabulary.DataPath, () => MissionVocabulary.Load(tree.Vocabulary));
            reader.Folder("missions", MissionDefinition.MissionFolder, path =>
                MissionDefinition.Load(tree.Read(path, json.MissionDocumentDto)));
            reader.One(
                "global tables",
                "sine_quarter, classes, vocabulary, win rules, effect_look, phase attributes",
                tree.InstallGlobalTables,
                count: false);
            reader.Folder("theaters", MissionDefinition.TheaterFolder, path =>
                MissionDefinition.Load(tree.Read(path, json.MissionDocumentDto)));
            reader.One("theaters", MissionDefinition.TheaterFolder + "/", () => _ = tree.Theaters, count: false);

            reader.One("configuration", "config.json", () => _ = tree.Config);
            reader.One("scenario catalog", MissionCatalog.DataPath, () => _ = tree.Scenarios);
            reader.One("weapon tables", "exe/weapons.json", () => _ = tree.Weapons);
            reader.One("exe tables", "exe/tables/hit_probability.json", () => _ = tree.HitProbability);
            reader.One("exe tables", "exe/tables/kbd_scancode_to_ascii.json", () => _ = tree.ScancodeToAscii);
            reader.One("exe tables", "exe/tables/player_damage.json", () => _ = tree.PlayerDamage);
            reader.One("exe tables", "exe/tables/combat_constants.json", () => _ = tree.CombatConstants);
            reader.One("exe tables", "exe/tables/engagement.json", () => _ = tree.Engagement);
            reader.One("exe tables", "exe/tables/aircraft_classes.json", () => _ = tree.AircraftClasses);
            reader.One("exe tables", "exe/tables/flight_tuning.json", () => _ = tree.FlightTuning);
            reader.One("exe tables", "exe/tables/cockpit_layout.json", () => _ = tree.CockpitLayout);
            reader.One("exe tables", AircraftEncyclopedia.AtlasDataPath, () => _ = tree.PlanesAtlas);
            reader.One("exe tables", AdvisorTableDto.DataPath, () => _ = tree.Advisor);
            reader.One("exe tables", CreateMissionTableDto.DataPath, () => _ = tree.CustomMissionFormations);
            reader.One(
                "exe tables", CreateMissionTableDto.DataPath + " (altitudeFeet)", () => _ = tree.CustomMissionAltitudes,
                count: false);
            reader.One("exe tables", WorldTableDto.DataPath, () => _ = tree.CloudDeckLattice);
            // The in-flight menu table is parsed by the host's own reader; here it only has to be JSON.
            reader.One("exe tables", "exe/tables/flight_menus.json", () => ParseJson(tree, "exe/tables/flight_menus.json"));
            reader.One("dgroup constants", "exe/tables/*.json", () => _ = tree.Constants, count: false);
            reader.One("strings", "exe/strings.json", () => _ = tree.Strings);
            // The in-flight screen's words and formats: a missing one names its address.
            reader.One("strings", "exe/strings.json (in-flight words)", () => _ = tree.InFlightStrings, count: false);
            reader.One("strings", "strings.json", () => _ = tree.UiStrings);

            reader.One("aircraft", "aircraft/", () => _ = tree.Aircraft, count: false);
            foreach (string basename in AircraftDefinition.FlyableBasenames)
            {
                if (info.Has($"aircraft/{basename}.json"))
                {
                    reader.Count("aircraft");
                }
            }

            reader.One("encyclopedia", AircraftEncyclopedia.DataPath, () => _ = tree.Encyclopedia);
            reader.One("cockpit instruments", "dialinit.json", () => _ = tree.DialInit);
            reader.Folder("cockpit packs", "cockpits", path => _ = tree.CockpitPack(Stem(path)[..^("_" + CockpitMode).Length], CockpitMode),
                path => Stem(path).EndsWith("_" + CockpitMode, StringComparison.Ordinal));

            reader.Folder("palettes", "palettes", path => _ = tree.Palette(Stem(path)));
            reader.Folder("fonts", "images/fonts", path =>
            {
                _ = tree.Font(Stem(path));
                string strip = $"images/fonts/{Stem(path)}.png";
                if (info.Has(strip))
                {
                    _ = IndexedPng.Load(tree.Resolve(strip));
                }
            });
            reader.Folder("images", "images", path => LoadImage(tree, path));
            reader.Folder("images", "images/masks", path => LoadImage(tree, path));

            reader.Folder("meshes", "meshes", path => _ = tree.Mesh(Stem(path)));
            reader.Folder("exe meshes", "exe/meshes", path => _ = tree.ExeMesh(Stem(path)));
            reader.One("exe meshes", "exe/meshes/", () => _ = tree.ExeMeshes, count: false);

            reader.Folder("speech", "audio/speech", path =>
            {
                SpeechClipDto clip = tree.SpeechClip(Stem(path));
                if (clip.AudioFile is not null)
                {
                    _ = tree.SpeechWav(Stem(path));
                }
            });
            reader.Folder("music", "audio/music", path => _ = tree.Music(Stem(path)));
            reader.Folder("audio drivers", "audio/drivers", path => _ = tree.AudioDriver(Stem(path)));

        }
        catch (LoadFailedException ex)
        {
            return new TreeLoadResult(null, counts, ex.Failure);
        }

        return new TreeLoadResult(tree, counts, null);
    }

    /// <summary>
    /// Builds the conditioned mesh library for every class the tree holds, with the host's default
    /// conditioning and the port's vector markings, each class's placements resolved against the base
    /// generated from this tree.
    /// </summary>
    /// <param name="tree">A loaded tree.</param>
    /// <param name="markingsDirectory">A markings override directory, or null for the built-ins alone.</param>
    public static TreeEnhanceResult Enhance(DataTree tree, string? markingsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        string markingsSource = markingsDirectory is null ? "markings (built-in)" : $"markings ({markingsDirectory})";
        MarkingLibrary markings;
        try
        {
            markings = new MarkingLibrary(markingsDirectory, tree);
            foreach (string name in markings.Names)
            {
                _ = markings.Get(name);
            }
        }
        catch (Exception ex) when (IsLoadError(ex))
        {
            return new TreeEnhanceResult(null, null, 0, 0, new TreeLoadFailure(markingsSource, Describe(ex)));
        }

        // Every class with placements is resolved here, so a diff whose key no longer resolves fails the step
        // by name instead of failing a mesh build later.
        foreach (string placement in markings.PlacementClasses)
        {
            try
            {
                _ = markings.TryGetPlacements(placement);
            }
            catch (Exception ex) when (IsLoadError(ex))
            {
                return new TreeEnhanceResult(null, markings, 0, 0, new TreeLoadFailure($"markings for class {placement}", Describe(ex)))
                {
                    Placements = markings.Resolutions,
                };
            }
        }

        MeshLibrary meshes = new MeshLibrary(
            tree,
            MeshConditioning.Default with { Markings = MarkingsMode.Vector, MarkingLibrary = markings });
        int classes = 0;
        int lods = 0;
        foreach (string basename in tree.ExeMeshes.Keys.Order(StringComparer.Ordinal))
        {
            try
            {
                MeshModel model = meshes.Get(basename);
                classes++;
                lods += model.Lods.Count;
            }
            catch (Exception ex) when (IsLoadError(ex))
            {
                return new TreeEnhanceResult(
                    null, markings, classes, lods,
                    new TreeLoadFailure($"class {basename} (exe/meshes/{basename}.json, meshes/{basename}.json)", Describe(ex)))
                {
                    Placements = markings.Resolutions,
                };
            }
        }

        return new TreeEnhanceResult(meshes, markings, classes, lods, null) { Placements = markings.Resolutions };
    }

    // A health check reports whatever a document does to its reader; only cancellation and the runtime's
    // own fatal conditions pass through.
    internal static bool IsLoadError(Exception ex) =>
        ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException);

    internal static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static void LoadImage(DataTree tree, string path)
    {
        ImageDocumentDto document = tree.Image(path);
        if (document.Pixels is not null)
        {
            _ = tree.ImagePixels(path);
        }
    }

    private static void ParseJson(DataTree tree, string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(tree.Resolve(path)));
    }

    private static string Stem(string relativePath) => Path.GetFileNameWithoutExtension(relativePath);

    private sealed class LoadFailedException(TreeLoadFailure failure) : Exception(failure.Reason)
    {
        public TreeLoadFailure Failure { get; } = failure;
    }

    private sealed class Reader(DataTree tree, List<(string Kind, int Documents)> counts)
    {
        public void One(string kind, string document, Action read, bool count = true)
        {
            try
            {
                read();
            }
            catch (Exception ex) when (IsLoadError(ex))
            {
                throw new LoadFailedException(new TreeLoadFailure(document, Describe(ex)));
            }

            if (count)
            {
                Count(kind);
            }
        }

        public void Folder(string kind, string folder, Action<string> read, Func<string, bool>? include = null)
        {
            string directory = tree.Info.Resolve(folder);
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(file);
                string path = $"{folder}/{name}";
                if (name.StartsWith('_') || (include is not null && !include(path)))
                {
                    continue;
                }

                One(kind, path, () => read(path));
            }
        }

        public void Count(string kind)
        {
            int at = counts.FindIndex(c => c.Kind == kind);
            if (at < 0)
            {
                counts.Add((kind, 1));
            }
            else
            {
                counts[at] = (kind, counts[at].Documents + 1);
            }
        }
    }
}
