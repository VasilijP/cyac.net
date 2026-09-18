using System.Globalization;
using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// <c>g_engagement_phase_attr_table [0x0F0E]</c>: one attribute byte per engagement node phase, read
/// from <c>exe/tables/combat_constants.json</c> (<c>phaseAttributes</c>).
/// </summary>
/// <remarks>
/// <para>
/// A build constant with zero writers image-wide (3867), indexed by the phase byte: the engagement node
/// pass reads bits 0, 1 and 3 of it through the constant DGROUP surface
/// (<see cref="EngagementNodePass.PhaseAttributeTable"/>), the mesh prepare callback bit 2
/// (<see cref="EnemyGearState"/>, <c>image@0x2D9C0</c>) and the TARGET window bit 0 (<c>image@0x0A829</c>).  The
/// values are the shipped table's; the port carries only its place, its length and what each bit gates.
/// </para>
/// <para>
/// Like the trig tables and the debris angles, the table is process-wide: <see cref="DataTree.InstallGlobalTables"/>
/// installs it, and a caller that has its own (tests, tools) hands it over explicitly. Consolidation C1,: the tree
/// has carried the same table since the tables moved into the data tree.
/// </para>
/// </remarks>
public sealed class EngagementPhaseAttributes
{
    /// <summary>How many phases the table covers: <c>u8[14]</c>.</summary>
    /// <remarks>
    /// Every static writer of the phase byte writes at most <c>0x0D</c>, and the state machine's default
    /// arm normalises an out-of-range value to <c>0x0B</c> (3867).
    /// </remarks>
    public const int Count = 14;

    private static EngagementPhaseAttributes? _installed;

    private readonly byte[] _entries;

    /// <summary>Builds a table from explicit attribute bytes.</summary>
    /// <param name="entries">The <see cref="Count"/> attribute bytes, in phase order.</param>
    /// <exception cref="ArgumentException">The count is wrong.</exception>
    public EngagementPhaseAttributes(ReadOnlySpan<byte> entries)
    {
        if (entries.Length != Count)
        {
            throw new ArgumentException(
                $"the phase-attribute table holds {Count} entries, {entries.Length} were given", nameof(entries));
        }

        _entries = entries.ToArray();
    }

    /// <summary>Whether <see cref="Install"/> has supplied the process-wide table.</summary>
    public static bool IsInstalled => _installed is not null;

    /// <summary>The process-wide table.</summary>
    /// <exception cref="InvalidOperationException">No table has been installed.</exception>
    public static EngagementPhaseAttributes Installed =>
        _installed ?? throw new InvalidOperationException(
            "the engagement phase attributes have not been installed. Call DataTree.InstallGlobalTables " +
            "(which reads 'exe/tables/combat_constants.json' from the transformed data tree), or hand the " +
            "caller its own table.");

    /// <summary>The attribute bytes, in phase order.</summary>
    public ReadOnlySpan<byte> Entries => _entries;

    /// <summary>Reads the table out of its tree document.</summary>
    /// <param name="constants">The parsed <c>exe/tables/combat_constants.json</c>.</param>
    /// <returns>The table.</returns>
    /// <exception cref="InvalidDataException">
    /// The document has no <c>phaseAttributes</c>, the wrong number of them, or one that is not a byte.
    /// </exception>
    public static EngagementPhaseAttributes Load(CombatConstantsDto constants)
    {
        ArgumentNullException.ThrowIfNull(constants);
        List<int> values = constants.PhaseAttributes
                           ?? throw Malformed("it has no phaseAttributes");
        if (values.Count != Count)
        {
            throw Malformed($"phaseAttributes holds {values.Count} entries, expected {Count}");
        }

        byte[] entries = new byte[Count];
        for (int phase = 0; phase < Count; phase++)
        {
            int value = values[phase];
            entries[phase] = value is >= byte.MinValue and <= byte.MaxValue
                ? (byte)value
                : throw Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"phaseAttributes[{phase}] = {value} does not fit a u8"));
        }

        return new EngagementPhaseAttributes(entries);
    }

    /// <summary>Installs the process-wide table.</summary>
    /// <param name="attributes">The table, usually <c>DataTree.PhaseAttributes</c>.</param>
    public static void Install(EngagementPhaseAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        _installed = attributes;
    }

    /// <summary>Forgets the process-wide table — for tests that need the not-installed behaviour.</summary>
    public static void Uninstall() => _installed = null;

    /// <summary>One phase's attribute byte, 0 for a phase past the table's end.</summary>
    /// <param name="phase">The phase byte.</param>
    /// <remarks>
    /// The original indexes without a bound check, but a phase outside the table is unreachable (see
    /// <see cref="Count"/>); the port answers 0 for one rather than reading past the array.
    /// </remarks>
    public byte Of(byte phase) => phase < _entries.Length ? _entries[phase] : (byte)0;

    private static InvalidDataException Malformed(string problem) =>
        new($"exe/tables/combat_constants.json is malformed: {problem}");
}
