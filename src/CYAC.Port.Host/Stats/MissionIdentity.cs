using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Sim.Flight.ColdStart;

namespace CYAC.Port.Host.Stats;

/// <summary>
/// WHICH MISSION a statistics record belongs to: the stable identity <c>stats.json</c> is keyed by,
/// and the two secondary fields that let a record be recognised again if the key itself ever moves.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key is the <c>.S</c> module's asset name</b> — <c>moduleAssetName</c> in
/// <c>data/scenarios.json</c> (<c>s_scenario_record +0x2D</c>), lower-cased: <c>abb.s</c>,
/// <c>escort2.s</c>, …  It is the right key for three reasons, all checked against the shipped
/// catalogue:
/// </para>
/// <list type="number">
/// <item>it is <b>unique</b> across all 50 records, while <c>title</c> is not (slots 7 and 18 are
/// both "Ground Attack", slots 22 and 37 are both "MiGCAP");</item>
/// <item>it is what the mission actually IS — the module that carries its win rules — so it survives
/// a re-titled, re-dated or re-ordered catalogue;</item>
/// <item>the <b>slot</b> does not survive any of those, which is exactly why the rule
/// ("leave the progression out, the missions are all open") makes the 50-byte unlock array
/// <c>[0xEF50]</c> the wrong thing to key on.</item>
/// </list>
/// <para>
/// <see cref="Title"/>, <see cref="Date"/> and <see cref="Slot"/> are stored BESIDE each record so a
/// human reading the file sees what it is about, and so a record whose key is missing can still be
/// matched on title+date (<see cref="PortStatsStore.Find"/>).  The catalogue is authoritative for
/// all three: a record is refreshed from it whenever the mission is flown.
/// </para>
/// </remarks>
/// <param name="Key">The <c>.S</c> module asset name, lower-cased — the file's own key.</param>
/// <param name="Title">The picker's title line (<c>+0x0F</c>), for the reader.</param>
/// <param name="Date">The mission date (<c>+0x06</c>, "M-D-YY"), for the reader.</param>
/// <param name="Slot">The catalogue's picker line 0..49, or <see cref="TestFlightSlot"/>.</param>
public readonly record struct MissionIdentity(string Key, string Title, string Date, int Slot)
{
    /// <summary>The slot a Test Flight is given: it is in no catalogue line at all.</summary>
    public const int TestFlightSlot = -1;

    /// <summary>
    /// The slot a CUSTOM mission is given: it is in no catalogue line either, and
    /// <see cref="TestFlightSlot"/> is taken.
    /// </summary>
    public const int CustomMissionSlot = -2;

    /// <summary>
    /// The prefix a custom sortie's statistics key carries: <c>custom:&lt;hex&gt;</c>.
    /// </summary>
    public const string CustomKeyPrefix = "custom:";

    /// <summary>The title the Test Flight's own record carries.</summary>
    public const string TestFlightTitle = "Test Flight";

    /// <summary>
    /// The Test Flight's own key.  It is a mission too, and the module it flies is the same
    /// <c>FREE.S</c> the original loads for it
    /// (<see cref="FlightColdStart.TestFlightMissionAsset"/>) — which is in no catalogue record, so
    /// the key cannot collide with one.
    /// </summary>
    public static MissionIdentity TestFlight { get; } = new(
        FlightColdStart.TestFlightMissionAsset.ToLowerInvariant(),
        TestFlightTitle,
        string.Empty,
        TestFlightSlot);

    /// <summary>The identity of one catalogue entry.</summary>
    /// <param name="entry">The scenario record.</param>
    public static MissionIdentity Of(MissionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new MissionIdentity(
            Normalise(entry.ModuleAssetName), entry.Title, entry.Date, entry.Slot);
    }

    /// <summary>The identity of a mission the catalogue does not describe (a module name alone).</summary>
    /// <param name="assetName">The <c>.S</c> module's asset name.</param>
    public static MissionIdentity OfModule(string assetName) =>
        new(Normalise(assetName), string.Empty, string.Empty, TestFlightSlot);

    /// <summary>
    /// The identity of ONE custom sortie.
    /// </summary>
    /// <param name="valueArray">The picks' wire form — <c>CustomMissionPicks.ToValueArray()</c>.</param>
    /// <param name="title">The composed sentence, without its quote marks.</param>
    /// <remarks>
    /// <b>Each distinct story is its own record.</b>  The key is the PICKS, not the module: the
    /// module a custom mission loads is <c>FREE.S</c>, which already belongs to the Test Flight
    /// (<see cref="TestFlight"/>), and two custom missions with different sentences are as different
    /// as two catalogue records are.  <see cref="Date"/> is empty because a custom sortie has none.
    /// </remarks>
    public static MissionIdentity OfCustom(ReadOnlySpan<byte> valueArray, string title) =>
        new(
            CustomKeyPrefix + Convert.ToHexString(valueArray).ToLowerInvariant(),
            title ?? string.Empty,
            string.Empty,
            CustomMissionSlot);

    /// <summary>Whether this is a custom sortie's record.</summary>
    public bool IsCustom => Key.StartsWith(CustomKeyPrefix, StringComparison.Ordinal);

    /// <summary>Whether this is the Test Flight's record.</summary>
    public bool IsTestFlight =>
        string.Equals(Key, TestFlight.Key, StringComparison.Ordinal);

    /// <summary>What the dialog's section heading and the debrief line call this mission.</summary>
    public string DisplayName =>
        string.IsNullOrEmpty(Title) ? Key.ToUpperInvariant() : Title;

    /// <summary>The file's own spelling of a module asset name: lower case, trimmed.</summary>
    /// <param name="assetName">Any spelling of it.</param>
    public static string Normalise(string? assetName) =>
        (assetName ?? string.Empty).Trim().ToLowerInvariant();
}
