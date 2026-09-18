using CYAC.Port.Core.Data;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Host.FrontEnd;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host;

/// <summary>
/// The TARGET window's LABELS, resolved once out of the data tree.
/// </summary>
/// <remarks>
/// <para>
/// Three shipped tables and one name list, none of them retyped (protocol rule 6):
/// </para>
/// <list type="bullet">
/// <item>
/// <c>g_ai_maneuver_name_table [0x0F1C]</c> — sixteen DGROUP near pointers at <c>image@0x3CC7C</c>,
/// indexed by the target's engagement block <c>+0x2C</c> (<c>image@0x0A7F9</c>): <c>SCISSORS</c>,
/// <c>CLIMB</c>, … <c>PURSUIT</c> … <c>LOOP</c>, with entry 0 an empty string.
/// </item>
/// <item>
/// the RADAR LOCK-STATE labels at <c>[0x0FF0]</c> — five pointers at <c>image@0x3CD50</c>, indexed by
/// the block's <c>+0x11</c> (<c>image@0x0A841</c>): <c>SEARCHING</c>, <c>SEARCHING</c>, a NULL,
/// <c>TRACKING</c>, <c>FIRING</c>.  The NULL is what the drawer's
/// <c>cmp word [bx+0xff0],0 / je</c> guard at <c>image@0x0A834</c> is for.
/// </item>
/// <item>
/// <c>g_engagement_phase_attr_table [0x0F0E]</c>, whose bit 0 gates the lock-state line
/// (<c>image@0x0A829</c>) — the tree's <c>exe/tables/combat_constants.json</c> <c>phaseAttributes</c>
/// (<see cref="DataTree.PhaseAttributes"/>).  That transcription is gone.
/// </item>
/// <item>
/// the aircraft TYPE names — <c>exe/tables/engagement.json</c>'s own <c>name</c> per prototype, which
/// is exactly the string the prototype's <c>+0x04</c> pointer addresses (<c>image@0x0EDEC</c>).
/// </item>
/// </list>
/// <para>
/// Both pointer tables live inside catalogued zones of <c>data/exe/strings.json</c>
/// (<c>str_zone_g_uncatalogued</c> and <c>str_scenarios_and_radar</c>), so
/// <see cref="FrontEndStrings.BytesAtDgroup"/> reconstructs their bytes and
/// <see cref="FrontEndStrings.AtDgroupRun"/> reads each string they point at — the same
/// runs-plus-unknown-spans reconstruction the front end's widget tables use.
/// </para>
/// </remarks>
public sealed class TargetPanelLabels
{
    private readonly string[] _manoeuvres;
    private readonly string?[] _lockStates;
    private readonly EngagementPhaseAttributes _phaseAttributes;
    private readonly Dictionary<ushort, string> _typeNames = [];

    /// <summary>Reads the tables out of an opened data tree.</summary>
    /// <param name="tree">The tree.</param>
    /// <param name="strings">Its DGROUP literal catalogue.</param>
    public TargetPanelLabels(DataTree tree, FrontEndStrings strings)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(strings);

        _manoeuvres = ReadTable(strings, TargetPanel.ManoeuvreTableDgroup, TargetPanel.ManoeuvreTableCount)
            .Select(static s => s ?? string.Empty).ToArray();
        _lockStates = ReadTable(strings, TargetPanel.LockStateTableDgroup, TargetPanel.LockStateTableCount);
        _phaseAttributes = tree.PhaseAttributes;

        NearestBogeyPrefix = LiteralAtImage(tree, NearestBogeyPrefixImage);
        NearestFriendlyPrefix = LiteralAtImage(tree, NearestFriendlyPrefixImage);
        NoBogeys = LiteralAtImage(tree, NoBogeysImage);
        NoFriendlies = LiteralAtImage(tree, NoFriendliesImage);

        foreach (EngagementPrototypeDto prototype in tree.Engagement.Prototypes ?? [])
        {
            if (prototype.Name is { } name && prototype.Dgroup is { } dgroup)
            {
                _typeNames[(ushort)PortHex.Parse(dgroup)] = name;
            }
        }
    }

    /// <summary>The AI manoeuvre's name for a block's <c>+0x2C</c> byte.</summary>
    /// <param name="index">The byte.</param>
    /// <returns>The name, or empty for entry 0 and for an index off the end.</returns>
    public string Manoeuvre(int index) =>
        (uint)index < (uint)_manoeuvres.Length ? _manoeuvres[index] : string.Empty;

    /// <summary>The radar lock-state label for a block's <c>+0x11</c> byte.</summary>
    /// <param name="index">The byte.</param>
    /// <returns>The label, or empty where the table's entry is the NULL.</returns>
    public string LockState(int index) =>
        (uint)index < (uint)_lockStates.Length ? _lockStates[index] ?? string.Empty : string.Empty;

    /// <summary>
    /// Whether the target's engagement PHASE lets the lock-state line be drawn at all —
    /// <c>g_engagement_phase_attr_table[phase] &amp; 1</c> (<c>image@0x0A829</c>).
    /// </summary>
    /// <param name="phase">The block's <c>+0x0D</c> byte.</param>
    /// <returns>True when the line may be drawn.</returns>
    public bool PhaseShowsLockState(byte phase) =>
        (_phaseAttributes.Of(phase) & 1) != 0;

    /// <summary>The aircraft TYPE name for an engagement prototype's DGROUP offset.</summary>
    /// <param name="prototype">The prototype's offset — the engagement block's <c>+0x00</c> word.</param>
    /// <returns>The name, or empty when the tree does not carry that prototype.</returns>
    public string TypeName(ushort prototype) =>
        _typeNames.TryGetValue(prototype, out string? name) ? name : string.Empty;

    /// <summary>How many prototypes carry a name — for a test that the tree really was read.</summary>
    public int TypeNameCount => _typeNames.Count;

    /// <summary><c>DGROUP[0x2B92]</c> — <c>"NEAREST BOGEY IS AT "</c> (<c>image@0x3E8F2</c>).</summary>
    public string NearestBogeyPrefix { get; }

    /// <summary><c>DGROUP[0x2BA8]</c> — <c>"NEAREST FRIENDLY IS AT "</c> (<c>image@0x3E908</c>).</summary>
    public string NearestFriendlyPrefix { get; }

    /// <summary>
    /// <c>"CAN'T FIND ANY BOGIES"</c> — <c>image@0x34F60</c>, in the static segment <c>0x44F6</c>
    /// rather than DGROUP (<c>image@0x23F28</c> pushes the segment itself).
    /// </summary>
    public string NoBogeys { get; }

    /// <summary><c>"CAN'T FIND ANY FRIENDLIES"</c> — <c>image@0x34F76</c>.</summary>
    public string NoFriendlies { get; }

    /// <summary>
    /// One shipped literal by its IMAGE offset — the four advisory strings live in two different
    /// segments, so an address is the only key that reaches both.
    /// </summary>
    /// <param name="tree">The tree.</param>
    /// <param name="image">The literal's <c>image@</c> offset.</param>
    /// <returns>The text, or empty when the tree's catalogue does not carry it.</returns>
    private static string LiteralAtImage(DataTree tree, int image)
    {
        foreach (ExeStringZoneDto zone in tree.Strings.Zones ?? [])
        {
            foreach (ExeStringDto literal in zone.Strings ?? [])
            {
                if (literal.Image is { } at && literal.Text is { } text
                    && PortHex.Parse(at) == image)
                {
                    return text;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>Where <c>"NEAREST BOGEY IS AT "</c> lives — <c>DGROUP[0x2B92]</c>.</summary>
    private const int NearestBogeyPrefixImage = 0x3E8F2;

    /// <summary>Where <c>"NEAREST FRIENDLY IS AT "</c> lives — <c>DGROUP[0x2BA8]</c>.</summary>
    private const int NearestFriendlyPrefixImage = 0x3E908;

    /// <summary>Where <c>"CAN'T FIND ANY BOGIES"</c> lives — <c>0x44F6:0x0000</c>.</summary>
    private const int NoBogeysImage = 0x34F60;

    /// <summary>Where <c>"CAN'T FIND ANY FRIENDLIES"</c> lives — <c>0x44F6:0x0016</c>.</summary>
    private const int NoFriendliesImage = 0x34F76;

    private static string?[] ReadTable(FrontEndStrings strings, int dgroup, int count)
    {
        byte[] pointers = strings.BytesAtDgroup(dgroup, count * 2);
        string?[] table = new string?[count];
        for (int i = 0; i < count; i++)
        {
            int at = pointers[i * 2] | (pointers[(i * 2) + 1] << 8);
            table[i] = at == 0 ? null : strings.AtDgroupRun(at);
        }

        return table;
    }
}
