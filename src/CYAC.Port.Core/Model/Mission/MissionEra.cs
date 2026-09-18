namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The three campaign eras — the scenario record's <c>+0x01 era_u8</c> and, one value wider, the
/// persisted era selector the mission picker filters by.
/// </summary>
/// <remarks>
/// <para>
/// An era is also a <b>theater</b>: <c>scenario_load_dispatch @image@0x09305</c> reads the selector
/// <c>g_scenario_era_selector [0x2A0E]</c>, doubles it and indexes the three-entry name table at
/// DGROUP+0x0FAA whose targets are <c>"germany.w"</c> / <c>"korea.w"</c> / <c>"vietnam.w"</c>
/// (<c>CYAC.Formats.EaLib.SDataModel.TheaterAssetForEra</c>, derived from the image), and
/// <c>scenario_filter_by_era @image@0x24B24</c> compacts the catalog against that same selector
/// (<c>cmp byte es:[bx+1], al</c> @<c>image@0x24B51</c>).
/// </para>
/// <para>
/// <see cref="Custom"/> exists only in the selector, never in a scenario record: the scanner types
/// <c>[0x2A0E]</c> as "u8; 0..3 era index (WW2/Korea/Vietnam/Custom)" (KNOWN_GLOBALS[0x2A0E]</c>, round
/// 21d).  The 50 shipped records use only 0..2 — 17 / 16 / 17 of them, in three contiguous runs (pinned
/// by <c>MissionCatalogTests</c>).
/// </para>
/// <para>INT-only: the era selects content, so it is part of the reproducible spine.</para>
/// </remarks>
public enum MissionEra
{
    /// <summary>0 — WWII / the Germany theater (<c>GERMANY.W</c>); records 0..16.</summary>
    WorldWarTwo = 0,

    /// <summary>1 — Korea (<c>KOREA.W</c>); records 17..32.</summary>
    Korea = 1,

    /// <summary>2 — Vietnam (<c>VIETNAM.W</c>); records 33..49.</summary>
    Vietnam = 2,

    /// <summary>
    /// 3 — the selector's fourth value.  No shipped scenario record carries it, and no theater asset
    /// answers to it; it is listed here because <c>g_scenario_era_selector</c> is documented as 0..3.
    /// </summary>
    Custom = 3,
}
