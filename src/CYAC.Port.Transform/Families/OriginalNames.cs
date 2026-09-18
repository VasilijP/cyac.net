using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using CYAC.Formats.EaLib;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Transform.Families.ExeTables;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// The names the game gives its class ids and its six flyable aircraft, read from the originals at
/// transform time for the tree's <c>_…Name</c> reading aids.
/// </summary>
/// <remarks>
/// <para>
/// The decoders carry ids only.  Every name here is looked up where the game keeps it:
/// </para>
/// <list type="bullet">
/// <item><see cref="AircraftClasses"/>: the 46-entry class table at <c>image@0x34F90</c>
/// (<see cref="AircraftClassExeTable"/>).  A flag-0 row that is not a sentinel points at an engagement
/// prototype, the aircraft's stat block, whose <c>+0x04</c> word points at its designation
/// (<see cref="EngagementExeTable.PrototypeNames"/>).</item>
/// <item><see cref="SceneryClasses"/>: a flag-1 row of the same table points at a mesh-registry
/// descriptor, which names its mesh (<see cref="ClassBasename"/>).</item>
/// <item><see cref="PlayerAircraft"/>: the six near pointers of <c>g_flyable_statblock_ptr_table</c>
/// at DGROUP <c>0x0FBC</c> (<c>image@0x3CD1C</c>, read at <c>image@0x247A4</c>), the table
/// <c>scenario.bin</c>'s aircraft index selects from.</item>
/// <item><see cref="FlyableDisplayNames"/>: <c>pi.bin</c>'s encyclopedia page whose class id is the one
/// the class table gives the flyable prototype; its short name.</item>
/// </list>
/// <para>
/// A name the originals do not give (no program image, no <c>pi.bin</c>, a pointer that resolves to
/// nothing) is simply absent; the reading aids then print the id.  The words the game never prints
/// for an id are the port's own (<see cref="ReadingAidLabels"/>).
/// </para>
/// </remarks>
public sealed class OriginalNames
{
    /// <summary>The archive <c>pi.bin</c> ships in.</summary>
    private const string PiArchive = "2a.lib";

    private static readonly ConditionalWeakTable<TransformContext, OriginalNames> PerRun = [];

    private OriginalNames(
        IReadOnlyDictionary<int, string> aircraftClasses,
        IReadOnlyDictionary<int, string> sceneryClasses,
        IReadOnlyList<string> playerAircraft,
        IReadOnlyList<string> flyableDisplayNames)
    {
        AircraftClasses = aircraftClasses;
        SceneryClasses = sceneryClasses;
        PlayerAircraft = playerAircraft;
        FlyableDisplayNames = flyableDisplayNames;
    }

    /// <summary>No originals: every lookup misses.</summary>
    public static OriginalNames None { get; } = new(
        new Dictionary<int, string>(), new Dictionary<int, string>(), [], []);

    /// <summary>Class id → designation, for the ids whose class-table row is an engagement prototype.</summary>
    public IReadOnlyDictionary<int, string> AircraftClasses { get; }

    /// <summary>Class id → mesh basename, for the ids whose class-table row is a scenery class record.</summary>
    public IReadOnlyDictionary<int, string> SceneryClasses { get; }

    /// <summary>Flyable slot (scenario.bin's aircraft index) → designation.</summary>
    public IReadOnlyList<string> PlayerAircraft { get; }

    /// <summary>Flyable slot → the encyclopedia's short name; empty without <c>pi.bin</c>.</summary>
    public IReadOnlyList<string> FlyableDisplayNames { get; }

    /// <summary>The names for one forward run, read once from the run's originals.</summary>
    /// <param name="context">The run's context; without originals the result is <see cref="None"/>.</param>
    public static OriginalNames For(TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return PerRun.GetValue(context, static c => c.Originals is { } originals ? Read(originals) : None);
    }

    /// <summary>Reads the names from a provider of the originals.</summary>
    /// <param name="originals">The originals.</param>
    public static OriginalNames Read(IOriginalData originals)
    {
        ArgumentNullException.ThrowIfNull(originals);
        return originals.TryGetProgramImage() is { } image
            ? Read(image, originals.TryGetArchiveMember(PiArchive, PiTransform.AssetName))
            : None;
    }

    /// <summary>Reads the names from the unpacked image and, when given, <c>pi.bin</c>'s decoded body.</summary>
    /// <param name="image">The unpacked layer-1 image at load segment <c>0x1000</c>.</param>
    /// <param name="piBin">The decoded <c>pi.bin</c>, or null.</param>
    public static OriginalNames Read(ReadOnlySpan<byte> image, byte[]? piBin)
    {
        if (image.Length < AircraftClassExeTable.TableImage + (AircraftClassExeTable.Entries * AircraftClassExeTable.RecordBytes))
        {
            return None;
        }

        IReadOnlyDictionary<ushort, string> prototypes = EngagementExeTable.PrototypeNames(image);
        SortedDictionary<int, string> aircraft = new SortedDictionary<int, string>();
        SortedDictionary<int, string> scenery = new SortedDictionary<int, string>();
        Dictionary<ushort, int> classOfPrototype = new Dictionary<ushort, int>();
        for (int id = 0; id < AircraftClassExeTable.Entries; id++)
        {
            int at = AircraftClassExeTable.TableImage + (id * AircraftClassExeTable.RecordBytes);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(image[(at + 1)..]);
            switch (image[at])
            {
                case 0 when prototypes.TryGetValue(value, out string? designation):
                    aircraft[id] = designation;
                    classOfPrototype.TryAdd(value, id);
                    break;
                case 1 when ClassBasename.For(image, value) is { } basename:
                    scenery[id] = basename;
                    break;
            }
        }

        List<string> player = new List<string>(AircraftDefinition.FlyableBasenames.Count);
        List<int> flyableClasses = new List<int>(AircraftDefinition.FlyableBasenames.Count);
        for (int slot = 0; slot < AircraftDefinition.FlyableBasenames.Count; slot++)
        {
            int at = ExeAddresses.Image(CombatConstantsExeTable.FlyablePrototypeTable + (slot * 2));
            ushort pointer = BinaryPrimitives.ReadUInt16LittleEndian(image[at..]);
            if (!prototypes.TryGetValue(pointer, out string? designation)
                || !classOfPrototype.TryGetValue(pointer, out int classId))
            {
                break;
            }

            player.Add(designation);
            flyableClasses.Add(classId);
        }

        return new OriginalNames(aircraft, scenery, player, DisplayNames(piBin, flyableClasses));
    }

    /// <summary>
    /// The name a reading aid prints for a <c>class</c> object's id: the port's word for the two
    /// sentinels, the designation, or null.
    /// </summary>
    /// <param name="classId">A class-table id.</param>
    public string? ClassName(int classId) => classId switch
    {
        ReadingAidLabels.PlayerClassId => ReadingAidLabels.PlayerClass,
        ReadingAidLabels.HomeBaseClassId => ReadingAidLabels.HomeBaseClass,
        _ => AircraftClasses.GetValueOrDefault(classId),
    };

    /// <summary>
    /// The name of any placed object: for a <c>class</c> object <see cref="ClassName"/>, the scenery
    /// basename, or <c>class N</c>; for a <c>prim_4d00</c> the airport mesh class 26 shares; otherwise
    /// the opener's own name.
    /// </summary>
    /// <param name="objectKind">The object's opener name.</param>
    /// <param name="classId">Its class-table id.</param>
    public string DescribeClass(string objectKind, int classId) => objectKind switch
    {
        "class" => ClassName(classId) ?? SceneryClasses.GetValueOrDefault(classId) ?? $"class {classId}",
        "prim_4d00" => SDataModel.Prim4D00Name,
        _ => objectKind,
    };

    /// <summary>
    /// An aircraft class id as the scenario and encyclopedia aids print it: the port's word for 0 (no
    /// featured opponent), the designation, or <see cref="ReadingAidLabels.Unknown"/>.
    /// </summary>
    /// <param name="classId">A class-table id.</param>
    public string AircraftClassLabel(int classId) =>
        classId == 0
            ? ReadingAidLabels.NoOpponent
            : AircraftClasses.GetValueOrDefault(classId) ?? ReadingAidLabels.Unknown(classId);

    /// <summary>A flyable slot's designation, or <see cref="ReadingAidLabels.Unknown"/>.</summary>
    /// <param name="slot">The aircraft index.</param>
    public string PlayerAircraftLabel(int slot) =>
        slot >= 0 && slot < PlayerAircraft.Count ? PlayerAircraft[slot] : ReadingAidLabels.Unknown(slot);

    private static List<string> DisplayNames(byte[]? piBin, List<int> flyableClasses)
    {
        if (piBin is null)
        {
            return [];
        }

        PiBinDecoder.PiBin pi;
        try
        {
            pi = PiBinDecoder.DecodeFromDecompressed(piBin);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException
                                       or InvalidOperationException)
        {
            return [];
        }

        List<string> names = new List<string>(flyableClasses.Count);
        foreach (int classId in flyableClasses)
        {
            if (pi.Planes.FirstOrDefault(p => p.AircraftClassId == classId) is not { } page)
            {
                break;
            }

            names.Add(page.NameShort);
        }

        return names;
    }
}
