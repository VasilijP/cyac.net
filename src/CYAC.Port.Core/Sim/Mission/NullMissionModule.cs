using CYAC.Port.Core.Sim.Combat.Lifecycle;

namespace CYAC.Port.Core.Sim.Mission;

/// <summary>
/// The mission module of one of the FIVE missions whose rules the IR does not model: it is INSTALLED
/// (the original loads a module for every mission), it counts every dispatch, and it never wins.
/// </summary>
/// <remarks>
/// <para>
/// ALONE, BOLO, GAUNTLET, INSTR and MOOLAH.  They were cut on EFFORT, not feasibility, so this stub
/// is a placeholder for real rules, not a verdict.
/// </para>
/// <para>
/// Making it <see cref="IsInstalled"/> = true is the faithful answer (<c>[0x0FBA] != 0</c> for
/// every mission) and it costs nothing: the verified dispatch then fires on schedule, the win flag
/// it cleared beforehand stays 0, and the census counts the calls the real module would have taken.
/// The visible consequence is the honest one — <b>these five missions can be flown but not won.</b>
/// </para>
/// </remarks>
/// <param name="assetName">The mission's asset name, e.g. <c>"BOLO.S"</c>.</param>
public sealed class NullMissionModule(string assetName) : IMissionModule
{
    /// <summary>The mission whose rules are missing.</summary>
    public string AssetName { get; } =
        assetName ?? throw new ArgumentNullException(nameof(assetName));

    /// <inheritdoc/>
    public bool IsInstalled => true;

    /// <summary>How many per-frame hooks reached the stub.</summary>
    public int CheckWinCalls { get; private set; }

    /// <summary>How many <c>on_slot_destroyed</c> dispatches did.</summary>
    public int SlotDestroyedCalls { get; private set; }

    /// <summary>How many AI-script <c>0xE0</c> hooks did.</summary>
    public int SecondaryEventCalls { get; private set; }

    /// <summary>What this mission's rules would need — a readout line, not a guess.</summary>
    public string MissingVocabulary => UnmodelledMissions.MissingVocabulary(AssetName);

    /// <inheritdoc/>
    /// <remarks>Returns 0: no win flag, no radio call.</remarks>
    public uint CheckWinCondition(in MissionHookArgs args)
    {
        CheckWinCalls++;
        return 0;
    }

    /// <inheritdoc/>
    public void OnSlotDestroyed(ushort slotIndex) => SlotDestroyedCalls++;

    /// <inheritdoc/>
    public void OnSecondaryEvent(ushort argument) => SecondaryEventCalls++;
}

/// <summary>
/// The five shipped missions with no win-rule model, and the vocabulary each one needs.
/// </summary>
public static class UnmodelledMissions
{
    /// <summary>The five asset names.</summary>
    public static IReadOnlyList<string> AssetNames { get; } =
        ["ALONE.S", "BOLO.S", "GAUNTLET.S", "INSTR.S", "MOOLAH.S"];

    /// <summary>Whether a mission is one of the five.</summary>
    /// <param name="assetName">An asset name such as <c>"BOLO.S"</c> (case-insensitive).</param>
    public static bool Contains(string assetName)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        foreach (string name in AssetNames)
        {
            if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What the named mission's rules need before they can run.</summary>
    /// <param name="assetName">An asset name (case-insensitive).</param>
    public static string MissingVocabulary(string assetName)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        return assetName.ToUpperInvariant() switch
        {
            "ALONE.S" or "GAUNTLET.S" =>
                "land-at-place: fn3 stores its args into module data (cs:[0xC..0x11]), the helper "
                    + "does far loads through the stored pointer, adds a place offset and takes a "
                    + "Manhattan |dx|+|dz| distance (~15 new instruction forms, plus word and "
                    + "far-pointer data items)",
            "MOOLAH.S" =>
                "land-at-place (as ALONE/GAUNTLET) plus the timed-radio idiom, which IS modelled",
            "BOLO.S" =>
                "deadline arming: a WORD variable with a non-zero (-1 sentinel) initial value, "
                    + "`cmp word cs:[..],-1`, actor-field arming (`add bx,0x18; cmp es:[bx+0x1B],0`), "
                    + "clock arithmetic into module data and a register-vs-memory compare",
            "INSTR.S" =>
                "the 111-instruction checkride monitor — deliberately out of the synthesiser's "
                    + "scope; the port would interpret it rather than re-emit it",
            _ => "(not one of the five unmodelled missions)",
        };
    }
}
