namespace CYAC.Formats.EaLib;

// Catalog of `strings_bin_lookup_and_copy` callers, sourced from the
// 29-callsite cross-reference done. Each entry records:
//   * image@ offset of the LCALL site in the L1 image
//   * the string index passed in AX (or a textual range for computed AX)
//   * the enclosing function (image@ of its prologue)
//
// Used by the viewer's Strings pane to populate the "Used by" column
// per string index.
public static class StringsBinCallerCatalog
{
    public sealed record CallSite(
        int CallerImageOffset,
        string AxDescription,
        int? FixedStringIndex,
        int[]? IndexRange,
        int EnclosingPrologueOffset,
        string EnclosingFunctionLabel);

    public static readonly IReadOnlyList<CallSite> CallSites = new[]
    {
        // (caller image@, AxDescription, FixedStringIndex, IndexRange, enclosing prologue, enclosing label)
        new CallSite(0x100BB, "mov ax,si; si=0x69+rand(4) | 0x6D+rand(4) | 0x71+f(score,4) | 0x75 const",
                     null, new[] {105,106,107,108, 109,110,111,112, 113,114,115,116, 117},
                     0x10060, "damage_state_blurb_renderer"),
        new CallSite(0x2481D, "sub ax,ax (= 0)", 0, null, 0x246FA, "scenario_bin_load"),
        new CallSite(0x25717, "mov ax,7", 7, null, 0x256D2, "ui_choose_activity_screen"),
        new CallSite(0x25866, "mov ax,8", 8, null, 0x25814, "ui_conflict_selection_screen"),
        new CallSite(0x25BDE, "add ax, 0x50 (rand-of-4 death msg)", null, new[] {80,81,82,83},
                     0x25BAF, "ui_post_death_message"),
        new CallSite(0x25C11, "add ax, 0x54 (rand-of-4 advice msg)", null, new[] {84,85,86,87},
                     0x25BAF, "ui_post_death_message"),
        new CallSite(0x25DEA, "mov ax,cx; cx in {0x76,0x77,0x78,0x79} (perf rating)", null,
                     new[] {118,119,120,121}, 0x25D5C, "performance_rating_classifier"),
        new CallSite(0x25E44, "mov ax, 0x58", 88, null, 0x25E00, "mission_stats_screen"),
        new CallSite(0x25E5E, "mov ax, 0x45", 69, null, 0x25E00, "mission_stats_screen"),
        new CallSite(0x25E89, "mov ax, 0x46", 70, null, 0x25E00, "mission_stats_screen"),
        new CallSite(0x25EB7, "mov ax, 0x47", 71, null, 0x25E00, "mission_stats_screen"),
        new CallSite(0x25EE9, "mov ax, 0x48", 72, null, 0x25E00, "mission_stats_screen"),
        new CallSite(0x25F0F, "mov ax, 0x49", 73, null, 0x25E00, "mission_stats_screen"),
        new CallSite(0x25F48, "mov ax, 0x4A", 74, null, 0x25E00, "mission_stats_screen"),
        new CallSite(0x25F6E, "mov ax, 0x4B", 75, null, 0x25E00, "mission_stats_screen"),
        new CallSite(0x25FA3, "mov ax, 0x4C", 76, null, 0x25E00, "mission_stats_screen"),
        new CallSite(0x2607C, "mov ax, 0x4D", 77, null, 0x25E00, "mission_stats_screen"),
        new CallSite(0x26393, "mov ax, 0x43", 67, null, 0x2630F, "ui_load_file_dialog"),
        new CallSite(0x26777, "mov ax, 1", 1, null, 0x264ED, "ui_aircraft_stats_panel"),
        new CallSite(0x267EB, "mov ax, 2", 2, null, 0x264ED, "ui_aircraft_stats_panel"),
        new CallSite(0x2681D, "mov ax, 3", 3, null, 0x264ED, "ui_aircraft_stats_panel"),
        new CallSite(0x26839, "mov ax, 4", 4, null, 0x264ED, "ui_aircraft_stats_panel"),
        new CallSite(0x26867, "mov ax, 5", 5, null, 0x264ED, "ui_aircraft_stats_panel"),
        new CallSite(0x26895, "mov ax, 6", 6, null, 0x264ED, "ui_aircraft_stats_panel"),
        new CallSite(0x2691D, "(sbb/and/+9) ax in {9, 68}", null, new[] {9, 68},
                     0x264ED, "ui_aircraft_stats_panel"),
        new CallSite(0x2747C, "mov ax, 0xA", 10, null, 0x2745D, "ui_same_plane_warning"),
        new CallSite(0x27601, "(sbb/inc/shl/+0x59) ax in 0x59..0x68 (tactical advice block)",
                     null, new[] {89,90,91,92,93,94,95,96,97,98,99,100,101,102,103,104},
                     0x275ED, "tactical_advice_text_selector"),
        new CallSite(0x27DDE, "mov ax, 0x34 | 0x35 (singular/plural sep)", null,
                     new[] {52, 53}, 0x27CA2, "engagement_narrative_composer"),
        new CallSite(0x2EA86, "mov ax,bx; neg ax (negated string idx, 0..122)", null, null,
                     0x2EA2C, "scenario_text_record_reader"),
    };

    /// <summary>
    /// Return the call sites that reference string <paramref name="idx"/>
    /// either as a fixed index or within a computed range.
    /// </summary>
    public static IEnumerable<CallSite> Lookup(int idx)
    {
        foreach (CallSite cs in CallSites)
        {
            if (cs.FixedStringIndex == idx)
                yield return cs;
            else if (cs.IndexRange is not null && Array.IndexOf(cs.IndexRange, idx) >= 0)
                yield return cs;
        }
    }
}
