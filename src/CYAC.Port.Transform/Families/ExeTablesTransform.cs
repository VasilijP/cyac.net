using CYAC.Formats.Exe;
using CYAC.Port.Transform.Families.ExeTables;
using CYAC.Port.Transform.Input;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// The censused data tables that live inside <c>yeager.exe</c> → <c>exe/*.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// *(twenty since P4-R2)* documents: the world-object class records, the three weapon tables, the
/// trigonometric tables, the combat tables, the keyboard translation table, the film-review widget table,
/// the catalogue of executable-resident string literals, the engagement zone, the small combat
/// constants, the aircraft-class table and the flight tuning, the cockpit's constant layout, the
/// in-flight ESC menu bar's six menus and fifty-four items, the in-flight advisor's line table, the
/// CREATE MISSION builder's formation offsets and altitude table (two sections of the same
/// document), the explosion's debris angles and the cloud deck's lattice. Each is extracted from
/// the unpacked layer-1 image the <c>exe</c> family produces.
/// </para>
/// <para>
/// <b>Why this family exists.</b>  An earlier build transcribed three of these
/// tables into <c>CYAC.Port.Core</c> as C# literals — 721 sine entries, 513 arctangent entries and
/// 23 class records.  Law L1 forbids original game data in shipped source, so the transcriptions are
/// gone and the runtime loads these documents instead
/// (<c>TrigTables.Load</c>, <c>ClassRegistry.Load</c>); the arctangent table, whose generator is
/// proven exact, is computed and merely checked against this extraction.
/// </para>
/// <para>
/// <b>Verification.</b>  Like the <c>exe</c> family this one has no whole-source inverse — the
/// executable is not re-packed — so the round trip is proved per table: <see cref="Rebuild"/> re-encodes
/// each document and <see cref="TreeVerifier"/> diffs the result against exactly the image slices
/// the table occupies.  Every shipped table round-trips <c>exact</c>.
/// </para>
/// </remarks>
public sealed class ExeTablesTransform : IFamilyTransform
{
    private static readonly IExeTable[] All =
    [
        new ClassRecordTable(),
        new WeaponExeTables(),
        new HitProbabilityExeTable(),
        new PlayerDamageExeTable(),
        new SineQuarterTable(),
        new Atan2OctantTable(),
        new ScancodeExeTable(),
        new AircraftClassExeTable(),
        new EngagementExeTable(),
        new CombatConstantsExeTable(),
        new FlightTuningExeTable(),
        new CockpitLayoutExeTable(),
        new FlightMenuExeTable(),
        new PlanesAtlasExeTable(),
        new AdvisorExeTable(),
        new CreateMissionExeTable(),
        new EffectLookExeTable(),
        new WorldExeTable(),
        new FilmReviewWidgetExeTable(),
        new ExeStringCatalog(),
    ];

    /// <summary>The family name <c>--only</c> matches and the manifest records.</summary>
    public const string FamilyName = "exe-tables";

    /// <inheritdoc/>
    public string Family => FamilyName;

    /// <inheritdoc/>
    public string TreeDescription =>
        "`exe/classes.json`, `exe/weapons.json`, `exe/strings.json` and `exe/tables/*.json` — the " +
        "data tables that live inside the executable itself: world-object classes, weapons, the " +
        "combat tables, the trigonometric tables, the keyboard translation table, the film-review " +
        "widgets, every string literal in the catalogued zones, and the whole CONSTANT DGROUP " +
        "surface the AI and the flight model tune from — the 19 engagement class prototypes with " +
        "their arc descriptors, range tables and band strings (`engagement.json`), the small " +
        "constant tables (`combat_constants.json`), the 46-entry aircraft-class table " +
        "(`aircraft_classes.json`) and the two flight constants (`flight_tuning.json`), plus the " +
        "cockpit's constant layout — each aircraft's 3-D viewport rectangle, the seven HUD layout " +
        "blocks and the ten instrument regions' source tables (`cockpit_layout.json`), plus the " +
        "in-flight ESC menu bar's six menus and their fifty-four item records with their " +
        "accelerator texts (`flight_menus.json`), plus the hangar's side-view sheet table " +
        "(`planes_atlas.json`), plus the in-flight advisor's line table: where each action code's " +
        "handler points its two text lines, as offsets into the advisory string table (`advisor.json`), " +
        "plus the CREATE MISSION builder's formation offsets and altitude table (`create_mission.json`), the " +
        "explosion's debris angles (`effect_look.json`) and the cloud deck's lattice (`world.json`), each " +
        "found by reading the instructions that index it. " +
        "The runtime loads these documents instead of carrying the numbers in its own source " +
        "and instead of reading `exe/image.l1.bin`.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => new(
        OutputFidelity.Exact,
        "the executable is not re-packed, so the round trip is proved per table: each document is " +
        "re-encoded and diffed against the image slices it came from");

    /// <summary>The paths this family writes, in output order — what the verifier walks.</summary>
    public static IReadOnlyList<string> TreePaths { get; } = [.. All.Select(t => t.TreePath)];

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is null
            && string.Equals(source.Name, KnownDistributions.ExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        byte[] image = context.Originals?.TryGetProgramImage()
            ?? UnpackImage(source);

        List<TransformOutput> outputs = new List<TransformOutput>(All.Length);
        foreach (IExeTable table in All)
        {
            ExeTableResult result = table.Forward(image);
            outputs.Add(new TransformOutput(
                context.Allocate(table.TreePath),
                result.Json,
                OutputRole.Data,
                OutputFidelity.Exact,
                $"{table.Description} — {result.Summary}",
                result.UnknownBytes));
        }

        return outputs;
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always: this family's source is the packed executable.</exception>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context) =>
        throw new NotSupportedException(
            "the exe-tables family has no whole-source inverse: its source is yeager.exe, which the " +
            "transform unpacks and does not re-pack. Its law-L3 " +
            $"proof is per table — see {nameof(ExeTablesTransform)}.{nameof(Rebuild)}, which " +
            "TreeVerifier diffs against the image slices each table occupies.");

    /// <summary>
    /// Re-encodes one of this family's documents into the original image bytes it came from.
    /// </summary>
    /// <param name="treePath">The document's path in the data tree.</param>
    /// <param name="json">Its contents, as read back from the tree.</param>
    /// <returns>The image runs the document reproduces, or null when the path is not this family's.</returns>
    public static IReadOnlyList<ExeSlice>? Rebuild(string treePath, byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        foreach (IExeTable table in All)
        {
            if (string.Equals(table.TreePath, treePath, StringComparison.OrdinalIgnoreCase))
            {
                return table.Inverse(json);
            }
        }

        return null;
    }

    private static byte[] UnpackImage(TransformSource source)
    {
        UnpackedImage unpacked = YeagerExeUnpacker.Unpack(source.Content.Span, source.Name);
        return unpacked.ImageAtLoadSeg(unpacked.LoadSegment);
    }
}
