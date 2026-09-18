using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Data;

/// <summary>
/// The transformed data tree, as the running game reads it: one typed accessor per document, each
/// loaded lazily and cached.
/// </summary>
/// <remarks>
/// <para>
/// The data-tree rule in one type: <c>CYAC.Port.Core</c> opens JSON, PNG and WAV files that
/// <c>cyac-transform</c> produced, and never a <c>.lib</c> or the executable.  Find a tree with
/// <see cref="DataLocator"/>, hand it here, and call <see cref="InstallGlobalTables"/> once at
/// startup for the tables the port keeps as process-wide state (the trig tables, the explosion debris
/// angles, the class registry, the mission vocabulary, the win rules).
/// </para>
/// <para>
/// Every accessor is lazy: a tree built with <c>--only</c> some families is usable for whatever it
/// does contain, and touching a family it does not contain fails with the path that is missing
/// rather than at construction.
/// </para>
/// </remarks>
public sealed class DataTree
{
    private readonly Dictionary<string, object> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Opens a located tree.</summary>
    /// <param name="info">The tree, from <see cref="DataLocator"/>.</param>
    public DataTree(DataTreeInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
    }

    /// <summary>Locates and opens a tree in one step.</summary>
    /// <param name="explicitPath">The path a host was given, when it was given one.</param>
    /// <exception cref="DataTreeNotFoundException">No candidate held a readable manifest.</exception>
    public static DataTree Open(string? explicitPath = null) => new(DataLocator.Locate(explicitPath));

    /// <summary>Where the tree is and what its manifest says.</summary>
    public DataTreeInfo Info { get; }

    /// <summary>The tree's absolute root directory.</summary>
    public string Root => Info.Root;

    /// <summary>The absolute path of a tree-relative path, checking that it is there.</summary>
    /// <param name="relativePath">A forward-slashed path such as <c>"config.json"</c>.</param>
    /// <exception cref="DataTreeNotFoundException">The tree does not contain that file.</exception>
    public string Resolve(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string path = Info.Resolve(relativePath);
        return File.Exists(path) ? path : throw Missing(relativePath);
    }

    // ---------------------------------------------------------------------------- configuration

    /// <summary>
    /// The ORIGINAL's saved settings and mission progression (<c>config.json</c>, the transformed
    /// <c>yeager.cfg</c>).
    /// </summary>
    /// <remarks>
    /// The running game no longer reads this.  It is whatever the last person to play the original
    /// left behind, and a port has no business starting from a stranger's saved state: the overlay
    /// windows now come from the port's own <c>port.json</c> and the joystick extremes from
    /// <c>FlightColdStart.DefaultConfigCalibration</c>.  The document is still produced by the
    /// transform and still read here — by the tools, and by the round trip that proves it — because
    /// it is part of what the originals say, which is the tree's whole job.
    /// </remarks>
    public GameConfig Config => Cached(
        "config", () => GameConfigCodec.Deserialise(File.ReadAllBytes(Resolve("config.json"))));

    // --------------------------------------------------------------------------------- missions

    /// <summary>The mission picker's catalog (<c>scenarios.json</c>).</summary>
    public MissionCatalog Scenarios => Cached(
        "scenarios",
        () => MissionCatalog.Load(Read(MissionCatalog.DataPath, Json.ScenarioCatalogDto)));

    /// <summary>The authoring vocabulary the mission documents are written in.</summary>
    public MissionVocabularyDto Vocabulary => Cached(
        "vocabulary", () => Read(MissionVocabulary.DataPath, Json.MissionVocabularyDto));

    /// <summary>Every <c>.S</c> mission, keyed by asset name (<c>"ABB.S"</c>), in tree order.</summary>
    public IReadOnlyDictionary<string, MissionDefinition> Missions =>
        Cached("missions", () => LoadContainers(MissionDefinition.MissionFolder));

    /// <summary>Every <c>.W</c> theater catalog, keyed by asset name (<c>"GERMANY.W"</c>).</summary>
    public IReadOnlyDictionary<string, MissionDefinition> Theaters =>
        Cached("theaters", () => LoadContainers(MissionDefinition.TheaterFolder));

    /// <summary>One mission or theater by asset name, or <see langword="null"/>.</summary>
    /// <param name="assetName">An asset name such as <c>"ABB.S"</c> or <c>"GERMANY.W"</c>.</param>
    public MissionDefinition? Container(string assetName)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        return Missions.TryGetValue(assetName, out MissionDefinition? mission) ? mission
            : Theaters.TryGetValue(assetName, out MissionDefinition? theater) ? theater
            : null;
    }

    // -------------------------------------------------------------------------------- aircraft

    /// <summary>The six flyable aircraft, keyed by basename (<c>"p51"</c>), in index order.</summary>
    public IReadOnlyDictionary<string, AircraftDefinition> Aircraft => Cached(
        "aircraft",
        () =>
        {
            Dictionary<string, AircraftDefinition> byName = new Dictionary<string, AircraftDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (string basename in AircraftDefinition.FlyableBasenames)
            {
                string path = $"aircraft/{basename}.json";
                if (Info.Has(path))
                {
                    byName[basename] = AircraftDefinition.Load(Read(path, Json.AircraftDefinitionDto));
                }
            }

            return (IReadOnlyDictionary<string, AircraftDefinition>)byName;
        });

    /// <summary>
    /// The hangar's aircraft encyclopedia and the tactics screen's matchup hints
    /// (<c>pi.json</c>, joined to the class table, the engagement prototypes and the side-view
    /// sheet rows).
    /// </summary>
    public Model.Aircraft.AircraftEncyclopedia Encyclopedia => Cached(
        "encyclopedia",
        () => Model.Aircraft.AircraftEncyclopedia.Load(
            Read(Model.Aircraft.AircraftEncyclopedia.DataPath, Json.AircraftEncyclopediaDto),
            PlanesAtlas,
            AircraftClasses,
            Engagement));

    /// <summary>The hangar's side-view sheet rows (<c>exe/tables/planes_atlas.json</c>).</summary>
    public PlanesAtlasTableDto PlanesAtlas => Cached(
        "planes_atlas",
        () => Read(Model.Aircraft.AircraftEncyclopedia.AtlasDataPath, Json.PlanesAtlasTableDto));

    // ------------------------------------------------------------------------- executable data

    /// <summary>
    /// The unpacked layer-1 program image — DEV-SIDE ONLY.
    /// </summary>
    /// <remarks>
    /// The transform keeps emitting it because <c>cyac-transform --verify</c> and the dev instruments
    /// need it, but nothing under <c>Sim/</c>, <c>Model/</c> or <c>CYAC.Port.Host</c> reads it any
    /// more: the constant DGROUP surface is rebuilt from the documents (<see cref="Constants"/>), and
    /// <c>DgroupConstantsTests</c> uses this image purely as the ORACLE that proves the two agree.
    /// </remarks>
    public byte[] ProgramImage => Cached("image.l1", () => File.ReadAllBytes(Resolve("exe/image.l1.bin")));

    /// <summary>The world-object class records (<c>exe/classes.json</c>).</summary>
    public ClassRegistryDocumentDto Classes =>
        Cached("classes", () => Read(ClassRegistry.DataPath, Json.ClassRegistryDocumentDto));

    /// <summary>The weapon-class table and the per-aircraft loadouts (<c>exe/weapons.json</c>).</summary>
    public WeaponTablesDocumentDto Weapons =>
        Cached("weapons", () => Read("exe/weapons.json", Json.WeaponTablesDocumentDto));

    /// <summary>One extracted scalar table, by its file name under <c>exe/tables/</c>.</summary>
    /// <param name="name">A table name such as <c>"sine_quarter"</c>.</param>
    public ScalarTableDto ScalarTable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Cached($"table:{name}", () => Read($"exe/tables/{name}.json", Json.ScalarTableDto));
    }

    /// <summary>The four-entry hit-probability table.</summary>
    public HitProbabilityTableDto HitProbability =>
        Cached("hit_probability", () => Read("exe/tables/hit_probability.json", Json.HitProbabilityTableDto));

    /// <summary>
    /// The game's own 256-byte scancode-to-ASCII table (<c>exe/tables/kbd_scancode_to_ascii.json</c>,
    /// from <c>image@0x3501A</c>) — the translation
    /// <c>kbd_ring_dequeue_and_translate @image@0x296E4</c> performs instead of the BIOS's.
    /// </summary>
    public ScancodeTableDto ScancodeToAscii =>
        Cached(
            "kbd_scancode_to_ascii",
            () => Read("exe/tables/kbd_scancode_to_ascii.json", Json.ScancodeTableDto));

    /// <summary>The six per-aircraft player-damage weight tables.</summary>
    public PlayerDamageTablesDto PlayerDamage =>
        Cached("player_damage", () => Read("exe/tables/player_damage.json", Json.PlayerDamageTablesDto));

    /// <summary>
    /// The small constant combat tables (<c>exe/tables/combat_constants.json</c>).
    /// </summary>
    public CombatConstantsDto CombatConstants =>
        Cached("combat_constants", () => Read("exe/tables/combat_constants.json", Json.CombatConstantsDto));

    /// <summary>
    /// <c>g_engagement_phase_attr_table [0x0F0E]</c>, out of
    /// <c>exe/tables/combat_constants.json</c> (<c>phaseAttributes</c>), checked as it is read.
    /// </summary>
    /// <remarks><see cref="InstallGlobalTables"/> installs it for the enemy gear rule.</remarks>
    /// <exception cref="InvalidDataException">The document breaks one of the rules.</exception>
    public Sim.Combat.EngagementPhaseAttributes PhaseAttributes =>
        Cached("phase_attributes", () => Sim.Combat.EngagementPhaseAttributes.Load(CombatConstants));

    /// <summary>The engagement-prototype zone (<c>exe/tables/engagement.json</c>).</summary>
    public EngagementDocumentDto Engagement =>
        Cached("engagement", () => Read("exe/tables/engagement.json", Json.EngagementDocumentDto));

    /// <summary>The 46-entry aircraft-class table (<c>exe/tables/aircraft_classes.json</c>).</summary>
    public AircraftClassTableDto AircraftClasses =>
        Cached("aircraft_classes", () => Read("exe/tables/aircraft_classes.json", Json.AircraftClassTableDto));

    /// <summary>The flight-side constants (<c>exe/tables/flight_tuning.json</c>).</summary>
    public FlightTuningDto FlightTuning =>
        Cached("flight_tuning", () => Read("exe/tables/flight_tuning.json", Json.FlightTuningDto));

    /// <summary>
    /// The cockpit's constant layout: the per-aircraft 3-D viewport, the seven HUD layout blocks
    /// and the ten instrument regions' source tables (<c>exe/tables/cockpit_layout.json</c>).
    /// </summary>
    public CockpitLayoutDto CockpitLayout =>
        Cached("cockpit_layout", () => Read("exe/tables/cockpit_layout.json", Json.CockpitLayoutDto));

    /// <summary>
    /// Where each in-flight advisor code points its two text lines (<c>exe/tables/advisor.json</c>),
    /// checked against <see cref="AdvisorTableRules"/> and joined to <see cref="Strings"/> as it is read.
    /// </summary>
    /// <exception cref="InvalidDataException">The document breaks one of the rules.</exception>
    public AdvisorTableDto Advisor =>
        Cached(
            "advisor",
            () => AdvisorTableRules.Validate(Read(AdvisorTableDto.DataPath, Json.AdvisorTableDto), Strings));

    /// <summary>
    /// The CREATE MISSION builder's formation offsets (<c>exe/tables/create_mission.json</c>), checked
    /// against the layout <see cref="CustomMissionVocabulary"/> names as they are read.
    /// </summary>
    /// <exception cref="InvalidDataException">The document breaks one of the rules.</exception>
    public CustomMissionFormations CustomMissionFormations =>
        Cached(
            "create_mission",
            () => CustomMissionFormations.Load(Read(CreateMissionTableDto.DataPath, Json.CreateMissionTableDto)));

    /// <summary>
    /// The CREATE MISSION builder's altitude table (<c>exe/tables/create_mission.json</c>, section
    /// <c>altitudeFeet</c>), checked against the ALTITUDE picker as it is read.
    /// </summary>
    /// <exception cref="InvalidDataException">The document breaks one of the rules.</exception>
    public CustomMissionAltitudes CustomMissionAltitudes =>
        Cached(
            "create_mission:altitudes",
            () => CustomMissionAltitudes.Load(Read(CreateMissionTableDto.DataPath, Json.CreateMissionTableDto)));

    /// <summary>
    /// The explosion debris angle tables (<c>exe/tables/effect_look.json</c>), checked as they are read.
    /// </summary>
    /// <remarks><see cref="InstallGlobalTables"/> installs them for the renderer.</remarks>
    /// <exception cref="InvalidDataException">The document breaks one of the rules.</exception>
    public EffectDebrisAngles DebrisAngles =>
        Cached(
            "effect_look",
            () => EffectDebrisAngles.Load(Read(EffectLookTableDto.DataPath, Json.EffectLookTableDto)));

    /// <summary>
    /// The cloud deck's lattice (<c>exe/tables/world.json</c>, section <c>cloudDeck</c>), checked as it is
    /// read.
    /// </summary>
    /// <exception cref="InvalidDataException">The document breaks one of the rules.</exception>
    public CloudDeckLattice CloudDeckLattice =>
        Cached(
            "world:cloud_deck",
            () => CloudDeckLattice.Load(Read(WorldTableDto.DataPath, Json.WorldTableDto)));

    /// <summary>One of the game's bitmap fonts (<c>images/fonts/&lt;name&gt;.json</c>).</summary>
    /// <param name="name">Its file stem, e.g. <c>"4x6"</c>.</param>
    public FontDocumentDto Font(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Cached($"font:{name}", () => Read($"images/fonts/{name}.json", Json.FontDocumentDto));
    }

    /// <summary>The cockpit instrument layout of all six aircraft (<c>dialinit.json</c>).</summary>
    public DialInitDocumentDto DialInit =>
        Cached("dialinit", () => Read("dialinit.json", Json.DialInitDocumentDto));

    /// <summary>
    /// One cockpit compositor pack — the opaque spans the cockpit repaints over the 3-D viewport.
    /// </summary>
    /// <param name="assetSuffix">The aircraft's cockpit-asset suffix, e.g. <c>"51"</c>.</param>
    /// <param name="mode">The video mode's folder tag, e.g. <c>"vga"</c>.</param>
    public CockpitPackDocumentDto CockpitPack(string assetSuffix, string mode = "vga")
    {
        ArgumentNullException.ThrowIfNull(assetSuffix);
        ArgumentNullException.ThrowIfNull(mode);
        return Cached(
            $"cockpit-pack:{assetSuffix}_{mode}",
            () => Read($"cockpits/{assetSuffix}_{mode}.json", Json.CockpitPackDocumentDto));
    }

    /// <summary>
    /// The constant DGROUP surface, rebuilt from every document above — what the combat kernel
    /// reads instead of <see cref="ProgramImage"/>.
    /// </summary>
    public DgroupConstants Constants => Cached("dgroup", () => DgroupConstants.Load(this));

    /// <summary>The UI string literals the port displays.</summary>
    public ExeStringCatalogDto Strings =>
        Cached("strings", () => Read("exe/strings.json", Json.ExeStringCatalogDto));

    /// <summary>
    /// The in-flight screen's words and formats, out of <see cref="Strings"/> by address, checked as they
    /// are read.
    /// </summary>
    /// <exception cref="InvalidDataException">The catalogue lacks one of them.</exception>
    public Model.Cockpit.InFlightStrings InFlightStrings =>
        Cached("in_flight_strings", () => Model.Cockpit.InFlightStrings.Load(Strings));

    /// <summary>
    /// The INDEXED UI string catalogue (<c>strings.json</c>, the transform's
    /// <c>2a.lib/strings.bin</c>) — the death and advice blurbs the debrief draws from.
    /// </summary>
    public UiStringCatalogDto UiStrings =>
        Cached("ui_strings", () => Read("strings.json", Json.UiStringCatalogDto));

    // --------------------------------------------------------------------- palettes and images

    /// <summary>One palette by name, e.g. <c>"palette"</c> or <c>"title0v"</c>.</summary>
    /// <param name="name">The palette's file stem under <c>palettes/</c>.</param>
    public PaletteDocumentDto Palette(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Cached($"palette:{name}", () => Read($"palettes/{name}.json", Json.PaletteDocumentDto));
    }

    /// <summary>One image document by tree-relative path, e.g. <c>"images/insig.json"</c>.</summary>
    /// <param name="relativePath">The document's path inside the tree.</param>
    public ImageDocumentDto Image(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return Cached($"image:{relativePath}", () => Read(relativePath, Json.ImageDocumentDto));
    }

    /// <summary>An image's pixels: the palette indices from its sibling 8-bit indexed PNG.</summary>
    /// <param name="relativePath">The image DOCUMENT's path, e.g. <c>"images/insig.json"</c>.</param>
    /// <exception cref="InvalidDataException">The document names no pixel file.</exception>
    public IndexedImage ImagePixels(string relativePath)
    {
        ImageDocumentDto document = Image(relativePath);
        string pixels = document.Pixels
            ?? throw new InvalidDataException($"{relativePath} names no \"pixels\" file");
        return Cached($"pixels:{pixels}", () => IndexedPng.Load(Resolve(pixels)));
    }

    // ------------------------------------------------------------------------------------ mesh

    /// <summary>One <c>.PNT</c> mesh by basename, e.g. <c>"bridge"</c>.</summary>
    /// <param name="basename">The mesh's file stem under <c>meshes/</c>.</param>
    public PntMeshDocumentDto Mesh(string basename)
    {
        ArgumentNullException.ThrowIfNull(basename);
        return Cached($"mesh:{basename}", () => Read($"meshes/{basename}.json", Json.PntMeshDocumentDto));
    }

    /// <summary>
    /// One executable-resident mesh by basename — its registry slot and its per-LOD shape records
    /// (<c>exe/meshes/&lt;name&gt;.json</c>).
    /// </summary>
    /// <param name="basename">The mesh's file stem under <c>exe/meshes/</c>.</param>
    public ExeMeshDocumentDto ExeMesh(string basename)
    {
        ArgumentNullException.ThrowIfNull(basename);
        return Cached(
            $"exe-mesh:{basename}", () => Read($"exe/meshes/{basename}.json", Json.ExeMeshDocumentDto));
    }

    /// <summary>Whether the tree carries both halves of a mesh's geometry.</summary>
    /// <param name="basename">The mesh's file stem.</param>
    /// <remarks>
    /// The executable-resident half is the one that must be there: a LOD whose vertices are inline
    /// needs no <c>.PNT</c> at all (<c>bridge</c>, <c>build</c>, <c>revet</c>, <c>hangar</c>, …).
    /// </remarks>
    public bool HasExeMesh(string basename)
    {
        ArgumentNullException.ThrowIfNull(basename);
        return Info.Has($"exe/meshes/{basename}.json");
    }

    /// <summary>Every executable-resident mesh in the tree, keyed by basename.</summary>
    /// <remarks>
    /// Folder metadata (<c>_census.json</c>) is skipped, as everywhere else — see
    /// <see cref="ReadFolder{T}"/>.
    /// </remarks>
    public IReadOnlyDictionary<string, ExeMeshDocumentDto> ExeMeshes =>
        Cached("exe-meshes", () => ReadFolder("exe/meshes", Json.ExeMeshDocumentDto));

    // ----------------------------------------------------------------------------------- audio

    /// <summary>One speech clip's header by name, e.g. <c>"welcome1"</c>.</summary>
    /// <param name="name">The clip's file stem under <c>audio/speech/</c>.</param>
    public SpeechClipDto SpeechClip(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Cached($"speech:{name}", () => Read($"audio/speech/{name}.json", Json.SpeechClipDto));
    }

    /// <summary>One speech clip's WAV bytes, ready to hand to a mixer.</summary>
    /// <param name="name">The clip's file stem under <c>audio/speech/</c>.</param>
    /// <exception cref="InvalidDataException">The clip is not PCM, so there is no WAV to read.</exception>
    public byte[] SpeechWav(string name)
    {
        SpeechClipDto clip = SpeechClip(name);
        string file = clip.AudioFile
            ?? throw new InvalidDataException(
                $"audio/speech/{name}.json carries a mode-{clip.Mode} payload, which is not PCM and " +
                "has no WAV; its samples are in the sibling .pcm file");
        return Cached($"speech-wav:{name}", () => File.ReadAllBytes(Resolve($"audio/speech/{file}")));
    }

    /// <summary>One music stream by name, e.g. <c>"yeagadl"</c>.</summary>
    /// <param name="name">The stream's file stem under <c>audio/music/</c>.</param>
    public MusicStreamDto Music(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Cached($"music:{name}", () => Read($"audio/music/{name}.json", Json.MusicStreamDto));
    }

    /// <summary>One audio driver's ABI by name, e.g. <c>"adldrive"</c> or <c>"blaster"</c>.</summary>
    /// <param name="name">The module's file stem under <c>audio/drivers/</c>.</param>
    public AudioDriverDto AudioDriver(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Cached($"driver:{name}", () => Read($"audio/drivers/{name}.json", Json.AudioDriverDto));
    }

    // ------------------------------------------------------------------------------- lifecycle

    /// <summary>
    /// Installs the tables the port keeps as process-wide state.  A host calls this once at startup.
    /// </summary>
    /// <remarks>
    /// These are global because the original's are: the trig tables, the class registry, the explosion
    /// debris angles and the engagement phase attributes are DGROUP arrays every subsystem (or the
    /// renderer's static draw code) indexes, and a mission's vocabulary and rules are looked up by name
    /// from wherever the rules are asked about.  Everything else on this type is per-tree state and
    /// stays here. The debris angles joined them; P4-R2, the same day — so did the phase attributes,
    /// which the enemy gear rule reads.
    /// </remarks>
    public void InstallGlobalTables()
    {
        TrigTables.Load(ScalarTable("sine_quarter"));
        ClassRegistry.Load(Classes.Classes
            ?? throw new InvalidDataException($"{ClassRegistry.DataPath} carries no classes"));
        MissionVocabulary.Load(Vocabulary);
        MissionWinRules.Load(
            Missions.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Source.Module ?? new MissionModuleDto(),
                StringComparer.OrdinalIgnoreCase));
        EffectDebrisAngles.Install(DebrisAngles);
        Sim.Combat.EngagementPhaseAttributes.Install(PhaseAttributes);
    }

    /// <summary>Reads and parses one document from the tree.</summary>
    /// <typeparam name="T">The document's DTO type.</typeparam>
    /// <param name="relativePath">Its path inside the tree.</param>
    /// <param name="typeInfo">Its source-generated type info.</param>
    /// <exception cref="DataTreeNotFoundException">The tree does not contain that document.</exception>
    public T Read<T>(string relativePath, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(typeInfo);

        return JsonSerializer.Deserialize(File.ReadAllBytes(Resolve(relativePath)), typeInfo)
            ?? throw new InvalidDataException($"{relativePath} is empty");
    }

    /// <summary>Every document under a folder, by file stem, in ordinal name order.</summary>
    /// <typeparam name="T">The documents' DTO type.</typeparam>
    /// <param name="folder">A tree-relative folder such as <c>"missions"</c>.</param>
    /// <param name="typeInfo">The documents' source-generated type info.</param>
    public IReadOnlyDictionary<string, T> ReadFolder<T>(string folder, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(folder);
        Dictionary<string, T> documents = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        string directory = Info.Resolve(folder);
        if (!Directory.Exists(directory))
        {
            return documents;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            if (stem.StartsWith('_'))
            {
                // `_vocabulary.json`, `_index.json`, `_census.json`: the folder's own metadata, not
                // one of the things it holds.
                continue;
            }

            documents[stem] = Read($"{folder}/{Path.GetFileName(file)}", typeInfo);
        }

        return documents;
    }

    private static PortDataJsonContext Json => PortDataJsonContext.Readable;

    private DataTreeNotFoundException Missing(string relativePath) =>
        new($"the data tree at '{Root}' has no '{relativePath}'. It was built with families " +
            $"[{string.Join(", ", Info.Families)}]; re-run {DataLocator.ToolName} without --only " +
            "to produce the whole tree.");

    private IReadOnlyDictionary<string, MissionDefinition> LoadContainers(string folder)
    {
        Dictionary<string, MissionDefinition> byAsset = new Dictionary<string, MissionDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (MissionDocumentDto? document in ReadFolder(folder, Json.MissionDocumentDto).Values)
        {
            MissionDefinition definition = MissionDefinition.Load(document);
            byAsset[definition.AssetName] = definition;
        }

        return byAsset;
    }

    private T Cached<T>(string key, Func<T> load)
        where T : notnull
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out object? hit))
            {
                return (T)hit;
            }

            T value = load();
            _cache[key] = value;
            return value;
        }
    }
}
