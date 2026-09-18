using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The mission picker's catalog: the 50 records of <c>2a.lib::scenario.bin</c>, in file order.
/// </summary>
/// <remarks>
/// <para>
/// The catalog is loaded from
/// <c>&lt;data&gt;/scenarios.json</c>, which <c>cyac-transform</c> writes and proves byte-exact
/// against <c>2a.lib::scenario.bin</c> on every <c>--verify</c>.  Went with it: re-emitting the asset
/// is the tool's job.
/// </para>
/// <para>
/// <b>The record count is not a constant in the engine.</b>  <c>scenario_filter_by_era
/// @image@0x24B24</c> derives it by dividing the asset size by the 164-byte stride
/// (<c>mov cx,0xA4; div cx</c> @<c>image@0x24B2B</c>), so a 51st record flows through the era filter,
/// the picker and the detail panel untouched — the engine's own capacity wall.  What is fixed at 50 is the
/// <see cref="MissionProgression"/> array a record indexes.  This type therefore reports
/// <see cref="Count"/> from the data and never assumes 50.
/// </para>
/// </remarks>
public sealed class MissionCatalog
{
    private MissionCatalog(ScenarioCatalogDto document)
    {
        Source = document;
        Entries = [.. (document.Missions ?? []).Select(r => new MissionEntry(r))];
    }

    /// <summary>The EALIB asset name: <c>scenario.bin</c>, in <c>2a.lib</c>, LZSS-compressed.</summary>
    public const string AssetName = "scenario.bin";

    /// <summary>Bytes per record: 164 (<c>0xA4</c>).</summary>
    public const int RecordBytes = MissionEntry.RecordBytes;

    /// <summary>The number of records the shipping catalog holds: 50.</summary>
    public const int ShippedRecordCount = 50;

    /// <summary>Where the catalog lives inside the data tree.</summary>
    public const string DataPath = "scenarios.json";

    /// <summary>Loads the catalog from its transformed document.</summary>
    /// <param name="document">The parsed <c>&lt;data&gt;/scenarios.json</c>.</param>
    /// <exception cref="InvalidDataException">The document carries no records.</exception>
    public static MissionCatalog Load(ScenarioCatalogDto document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Missions is not { Count: > 0 })
        {
            throw new InvalidDataException($"{DataPath} carries no mission records");
        }

        return new MissionCatalog(document);
    }

    /// <summary>The records, in file order.</summary>
    public IReadOnlyList<MissionEntry> Entries { get; }

    /// <summary>How many records the loaded asset held.</summary>
    public int Count => Entries.Count;

    /// <summary>The document behind this adapter.</summary>
    public ScenarioCatalogDto Source { get; }

    /// <summary>
    /// The records of one era, in catalog order — what <c>scenario_filter_by_era @image@0x24B24</c>
    /// leaves in the buffer after its in-place compaction (there is no index array; the survivors are
    /// moved to the front and counted in <c>g_scenario_filtered_count [0xBA0E]</c>).
    /// </summary>
    /// <param name="era">The era selector value.</param>
    public IReadOnlyList<MissionEntry> ForEra(MissionEra era) =>
        [.. Entries.Where(e => e.Era == era)];

    /// <summary>The entry whose <c>.S</c> module has this asset name, or <see langword="null"/>.</summary>
    /// <param name="assetName">An asset name such as <c>"ABB.S"</c> (case-insensitive).</param>
    public MissionEntry? ByModuleAsset(string assetName)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        return Entries.FirstOrDefault(e => e.HasModuleAsset(assetName));
    }

    /// <summary>The entry with this record index, or <see langword="null"/>.</summary>
    /// <param name="recordIndex">A record index, 0..49.</param>
    public MissionEntry? ByRecordIndex(int recordIndex) =>
        Entries.FirstOrDefault(e => e.RecordIndex == recordIndex);

    /// <summary>The entries a save has unlocked, in catalog order.</summary>
    /// <param name="progression">The save state to consult.</param>
    public IReadOnlyList<MissionEntry> UnlockedIn(MissionProgression progression)
    {
        ArgumentNullException.ThrowIfNull(progression);
        return [.. Entries.Where(e => e.IsUnlockedIn(progression))];
    }

}
