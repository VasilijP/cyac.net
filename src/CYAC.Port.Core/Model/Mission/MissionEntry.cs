using CYAC.Port.Core.Data;
using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// One line of the mission picker: a 164-byte <c>scenario.bin</c> record — who you fly, when, against
/// what, and which <c>.S</c> module carries the mission itself.
/// </summary>
/// <remarks>
/// <para>
/// The original is <c>s_scenario_record</c> (stride <c>0xA4</c>, <c>mov cx,0xA4</c>
/// @<c>image@0x2472B</c>); the field map is KNOWN_FIELDS["s_scenario_record"]</c>.  This type
/// is the port's domain view of one record of <c>&lt;data&gt;/scenarios.json</c>, which the transform
/// writes and proves byte-exact against the asset.  Re-emitting <c>scenario.bin</c> is the tool's job,
/// not the runtime's.
/// </para>
/// <para>
/// <c>scenario_bin_load @image@0x246FA</c> publishes a selected record into DGROUP: <c>+0x2D</c> →
/// <c>g_record_filename_buf [0xEF82]</c>, <c>+0x0F</c> → <c>[0xEE06]</c>, <c>+0x06</c> →
/// <c>[0xEE24]</c>, <c>+0x00</c> → <c>g_record_index_u8 [0xEE2E]</c>, <c>+0x05</c> → <c>[0xEE30]</c>,
/// <c>+0x03</c> → <c>g_active_aircraft_idx [0xC31A]</c>, and <c>+0x04</c> through the class table into
/// <c>g_opponent_statblock_ptr [0xF1C6]</c>.
/// </para>
/// <para>INT-only, immutable, authored content.</para>
/// </remarks>
[OriginalStruct("s_scenario_record")]
public sealed class MissionEntry
{
    private readonly ScenarioEntryDto _record;

    internal MissionEntry(ScenarioEntryDto record) => _record = record;

    /// <summary>Bytes per record: 164 (<c>0xA4</c>).</summary>
    public const int RecordBytes = 0xA4;

    /// <summary>The document record this entry views, for callers that need a field this view omits.</summary>
    public ScenarioEntryDto Source => _record;

    /// <summary>The record's position in the catalog as loaded (0..49).</summary>
    public int Slot => _record.Slot;

    /// <summary>
    /// <c>+0x00</c> — the record's own index, and the key into
    /// <see cref="MissionProgression"/>.  All 50 shipped records mirror their slot.
    /// </summary>
    [OriginalField("+0x00", "record_index_u8")]
    public int RecordIndex => _record.RecordIndex;

    /// <summary>True when <see cref="RecordIndex"/> still mirrors <see cref="Slot"/>.</summary>
    public bool IndexMirrorsSlot => _record.RecordIndex == _record.Slot;

    /// <summary><c>+0x01</c> — the era, which is also the theater (<c>GERMANY.W</c>/<c>KOREA.W</c>/<c>VIETNAM.W</c>).</summary>
    [OriginalField("+0x01", "era_u8")]
    public MissionEra Era => (MissionEra)_record.Era;

    /// <summary>The theater asset this entry's era loads (<c>missions/_vocabulary.json</c>).</summary>
    public string TheaterAssetName =>
        _record.TheaterAsset ?? MissionVocabulary.TheaterAssetForEra[(int)Era];

    /// <summary>
    /// <c>+0x02</c> — the player side's national marking: a frame index into the 4-frame 32×11
    /// <c>INSIG.PIC</c> atlas (source-x = <c>idx &lt;&lt; 5</c>, <c>image@0x256C2</c>).
    /// </summary>
    [OriginalField("+0x02", "insignia_idx_u8")]
    public int InsigniaIndex => _record.InsigniaIndex;

    /// <summary>The insignia's name; the index space is verified, the labels are partial.</summary>
    public string InsigniaName => _record.InsigniaName ?? string.Empty;

    /// <summary>
    /// <c>+0x03</c> — the player's aircraft, 0..5, indexing the six-entry flyable table at
    /// DGROUP+0xFBC.  Same order as <c>AircraftDefinition.FlyableBasenames</c>.
    /// </summary>
    [OriginalField("+0x03", "aircraft_idx_u8")]
    public int PlayerAircraftIndex => _record.PlayerAircraftIndex;

    /// <summary>The player aircraft's name, byte-verified from its stat block.</summary>
    public string PlayerAircraftName => _record.PlayerAircraftName ?? string.Empty;

    /// <summary>
    /// <c>+0x04</c> — the featured opponent's class id (0 = none), looked up in the 46-entry class
    /// table @<c>image@0x34F90</c> by <c>aircraft_class_table_lookup @0x24058</c>.
    /// </summary>
    [OriginalField("+0x04", "opponent_class_u8")]
    public int OpponentClassId => _record.OpponentClassId;

    /// <summary>The opponent's name, or "(none)".</summary>
    public string OpponentName => _record.OpponentName ?? "(none)";

    /// <summary>
    /// <c>+0x05</c> — the authored mission RATING 1..3, drawn as that many dots by
    /// <c>scenario_detail_panel_show</c> (<c>image@0x24D72..0x24DA4</c>; manual p.21).
    /// </summary>
    /// <remarks>
    /// Not the player-selectable difficulty LEVEL — that is <see cref="BriefingDifficulty"/>
    /// (<c>cfg@0x23</c>).  Byte <c>+0x05</c> never reaches the opponent AI; it also picks
    /// Yeager's pre-mission speech tier @<c>image@0x25493</c>.
    /// </remarks>
    [OriginalField("+0x05", "difficulty_rating_u8")]
    public int DifficultyRating => _record.DifficultyRating;

    /// <summary><c>+0x06</c> — the mission date, "M-D-YY".</summary>
    [OriginalField("+0x06", "date_str[9]")]
    public string Date => _record.Date ?? string.Empty;

    /// <summary><c>+0x0F</c> — the picker's title line, e.g. "The Abbeville Boys".</summary>
    [OriginalField("+0x0F", "title_str[30]")]
    public string Title => _record.Title ?? string.Empty;

    /// <summary>
    /// <c>+0x2D</c> — the <c>.S</c> mission module's asset name, as authored (lower case; the EALIB
    /// directory spells it upper case, so compare case-insensitively).
    /// </summary>
    [OriginalField("+0x2D", "s_filename[14]")]
    public string ModuleAssetName => _record.ModuleAssetName ?? string.Empty;

    /// <summary><c>+0x3B</c> — the briefing blurb the detail panel word-wraps.</summary>
    [OriginalField("+0x3B", "description_str[105]")]
    public string Description => _record.Description ?? string.Empty;

    /// <summary>Whether this entry's record index is unlocked in a given save.</summary>
    /// <param name="progression">The save state to consult.</param>
    public bool IsUnlockedIn(MissionProgression progression)
    {
        ArgumentNullException.ThrowIfNull(progression);
        return progression.IsUnlocked(RecordIndex);
    }

    /// <summary>True when <paramref name="assetName"/> names this entry's <c>.S</c> module.</summary>
    /// <param name="assetName">An asset name such as <c>"ABB.S"</c> or <c>"abb.s"</c>.</param>
    public bool HasModuleAsset(string assetName) =>
        string.Equals(ModuleAssetName, assetName, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override string ToString() =>
        $"[{RecordIndex:D2}] {Era} {Date} {Title} ({ModuleAssetName})";
}
