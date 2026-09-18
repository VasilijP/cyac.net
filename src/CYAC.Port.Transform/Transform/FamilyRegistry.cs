using CYAC.Port.Transform.Families;

namespace CYAC.Port.Transform.Transform;

/// <summary>
/// The set of family transforms a run may apply, and the <c>--only</c> filter over it.
/// </summary>
/// <remarks>
/// Builders T2…T7 extend the port by adding a <see cref="IFamilyTransform"/> to
/// <see cref="CreateAll"/> — nothing else in the tool needs to change: the runner asks the registry
/// which family claims each source, and the manifest and generated README follow from the family's
/// own metadata.
/// </remarks>
public sealed class FamilyRegistry
{
    /// <summary>The family name of the fallback that carries not-yet-explained bytes verbatim.</summary>
    public const string RawFamily = "raw";

    /// <summary>The family name of the EALIB container layer (the <c>_directory.json</c> files).</summary>
    public const string ArchiveFamily = "ealib";

    /// <summary>The family name of <c>yeager.cfg</c>.</summary>
    public const string ConfigFamily = "config";

    /// <summary>The family name of <c>yeager.exe</c>.</summary>
    public const string ExeFamily = "exe";

    /// <summary>The family name of the data tables that live inside <c>yeager.exe</c>.</summary>
    public const string ExeTablesFamily = ExeTablesTransform.FamilyName;

    /// <summary>
    /// The family name of the 3-D meshes.  It is NOT a standalone family even though one of its two
    /// sources is the executable: it also claims the <c>.PNT</c> members of <c>1a.lib</c>, so
    /// selecting it must still walk the archives.
    /// </summary>
    public const string MeshFamily = MeshTransform.FamilyName;

    /// <summary>
    /// The families whose source is a standalone file rather than an archive member.  Selecting only
    /// these leaves the EALIB archives untouched.
    /// </summary>
    public static IReadOnlyList<string> StandaloneFamilies { get; } =
        [ConfigFamily, ExeFamily, ExeTablesFamily];

    private readonly List<IFamilyTransform> _selected;

    private FamilyRegistry(IReadOnlyList<IFamilyTransform> all, IReadOnlyList<string> selectedNames)
    {
        All = all;
        SelectedNames = selectedNames;
        _selected = [.. all.Where(f => selectedNames.Contains(f.Family, StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>Every family this build of the tool knows, whether selected or not.</summary>
    public IReadOnlyList<IFamilyTransform> All { get; }

    /// <summary>The families this run will apply, including the pseudo-families <c>ealib</c>/<c>raw</c>.</summary>
    public IReadOnlyList<string> SelectedNames { get; }

    /// <summary>The content families this run will apply.</summary>
    public IReadOnlyList<IFamilyTransform> Selected => _selected;

    /// <summary>Every family name the <c>--only</c> filter accepts.</summary>
    public static IReadOnlyList<string> KnownNames { get; } =
        [.. CreateAll().Select(f => f.Family), ArchiveFamily, RawFamily];

    /// <summary>Every family transform this build of the tool carries.</summary>
    /// <remarks>Registration point for later builders (T2…T7).</remarks>
    public static IReadOnlyList<IFamilyTransform> CreateAll() =>
    [
        new ConfigTransform(),
        new ExeTransform(),
        new ExeTablesTransform(),
        new PaletteTransform(),
        new PicTransform(),
        new FontTransform(),
        new RleTransform(),
        new MaskTransform(),
        new ScenarioTransform(),
        new PiTransform(),
        new StringsTransform(),
        new CpAnswersTransform(),
        new RemapTransform(),
        new DialInitTransform(),
        new MissionTransform(world: false),
        new MissionTransform(world: true),
        new AircraftTransform(),
        new MeshTransform(),
        new CockpitTransform(),
        new SpeechTransform(),
        new MusicTransform(),
        new SoundDriverTransform(),
    ];

    /// <summary>
    /// Builds the registry for a run.
    /// </summary>
    /// <param name="only">
    /// The <c>--only</c> family names, or an empty list for "everything".  Unknown names throw.
    /// </param>
    /// <exception cref="ArgumentException">A name is not in <see cref="KnownNames"/>.</exception>
    public static FamilyRegistry Create(IReadOnlyList<string> only)
    {
        IReadOnlyList<IFamilyTransform> all = CreateAll();
        if (only.Count == 0)
        {
            return new FamilyRegistry(all, [.. KnownNames]);
        }

        foreach (string name in only)
        {
            if (!KnownNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"unknown family \"{name}\"; known families: {string.Join(", ", KnownNames)}", nameof(only));
            }
        }

        return new FamilyRegistry(all, [.. only]);
    }

    /// <summary>True when a family name is part of this run.</summary>
    /// <param name="family">The family name to test.</param>
    public bool IsSelected(string family) =>
        SelectedNames.Contains(family, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the run touches the EALIB archives at all — i.e. anything but a <c>--only config</c>
    /// run.  A selected content family implies the container layer, because the container is how its
    /// members are reached.
    /// </summary>
    public bool TouchesArchives =>
        SelectedNames.Any(n => !StandaloneFamilies.Contains(n, StringComparer.OrdinalIgnoreCase));

    /// <summary>The selected family that claims a source, or <see langword="null"/> for none.</summary>
    /// <param name="source">The source to classify.</param>
    public IFamilyTransform? Claim(TransformSource source)
    {
        foreach (IFamilyTransform family in _selected)
        {
            if (family.Claims(source))
            {
                return family;
            }
        }

        return null;
    }

    /// <summary>Looks a family up by name across every family the tool carries.</summary>
    /// <param name="family">The family name.</param>
    public IFamilyTransform? ByName(string family) =>
        All.FirstOrDefault(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase));
}
