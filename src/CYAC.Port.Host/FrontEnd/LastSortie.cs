using System.Globalization;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// What <c>settings.json</c> remembers so the menu's <c>Last Mission</c> row can re-fly it.
/// </summary>
/// <param name="Kind"><see cref="MissionKind"/> or <see cref="TestFlightKind"/>.</param>
/// <param name="Slot">The scenario-catalogue slot, or −1 for a Test Flight.</param>
/// <param name="Difficulty">0..3 — the MISSION DESCRIPTION screen's <c>Diff:</c> button (F2).</param>
/// <param name="Aircraft">The Hangar basename the Test Flight flew, e.g. <c>p51</c>.</param>
/// <param name="Site">The <c>at_site</c> draw the sortie was opened with.</param>
/// <param name="Picks">
/// A CUSTOM mission's seven picks, as the hex of <c>CustomMissionPicks.ToValueArray</c>; empty for
/// every other kind.  It is the ORIGINAL's own memory: the fly exit copies 0x28 bytes of
/// <c>[0xC2E4]</c> to a far buffer and the resume path copies them back (<c>image@0x27774</c> /
/// <c>image@0x276CF</c>), which is what makes <c>Last Mission</c> re-fly the same sentence.
/// </param>
/// <remarks>
/// <para>
/// It is written at every sortie's START, so a crash mid-sortie still leaves the row usable, and it
/// is deliberately NOT in <c>stats.json</c>: M3 §4.5 — the statistics file is an append-only record
/// of what was flown and "a corrupt save must not cost the statistics".  It is not progression either
/// (all 50 missions stay open); it is one line of "what you were doing last".
/// </para>
/// <para>
/// The mission is keyed by SLOT rather than by the module asset name that <c>stats.json</c> keys on,
/// because the slot is what the command line and the pickers speak and a <c>Last Mission</c> that
/// pointed at a re-ordered catalogue would fly the wrong sortie silently rather than visibly.
/// <c>(open)</c> — F2 may want the asset name beside it once the mission picker exists.
/// </para>
/// </remarks>
public sealed record LastSortie(
    string Kind, int Slot, int Difficulty, string Aircraft, int Site, string Picks = "")
{
    /// <summary>The <c>kind</c> of a historic mission.</summary>
    public const string MissionKind = "mission";

    /// <summary>The <c>kind</c> of a Test Flight.</summary>
    public const string TestFlightKind = "testFlight";

    /// <summary>The <c>kind</c> of a CUSTOM mission built by the CREATE MISSION pickers.</summary>
    public const string CustomKind = "custom";

    /// <summary>
    /// The <see cref="Slot"/> a custom mission carries: it is in no catalogue line, and
    /// <c>-1</c> is already the Test Flight's.
    /// </summary>
    public const int CustomSlot = -2;

    /// <summary>Whether this is a historic mission rather than a Test Flight.</summary>
    public bool IsMission => string.Equals(Kind, MissionKind, StringComparison.Ordinal);

    /// <summary>Whether this is a CUSTOM mission.</summary>
    public bool IsCustom =>
        string.Equals(Kind, CustomKind, StringComparison.Ordinal) && Picks.Length > 0;

    /// <summary>What the readout and the log say about it.</summary>
    public string Describe() => IsCustom
        ? string.Create(CultureInfo.InvariantCulture, $"custom mission {Picks} (site {Site})")
        : IsMission
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"mission {Slot} (difficulty {Difficulty}, site {Site})")
        : string.Create(
            CultureInfo.InvariantCulture,
            $"test flight {(Aircraft.Length > 0 ? Aircraft : "(default)")} (site {Site})");
}
