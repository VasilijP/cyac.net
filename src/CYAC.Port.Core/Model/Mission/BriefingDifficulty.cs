namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The player-selectable difficulty level — persisted at <c>yeager.cfg@0x23</c>, live at
/// <c>g_briefing_difficulty_idx [0xF10E]</c>, cycled by the briefing screen's <c>Diff:</c> button
/// (<c>ui_briefing_screen @image@0x25A3D</c>; manual p.22).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from a mission's authored <b>rating</b> (<see cref="MissionEntry.DifficultyRating"/>,
/// scenario record <c>+0x05</c>, 1..3): this is the global setting, that is per-mission content. The
/// level is read by <c>weapon_fire_combat_loop</c> @<c>image@0x0F79C</c> to pick a hit-probability
/// row (table at <c>[0x45EA]</c>) — KNOWN_GLOBALS[0xF10E]</c>, P56.
/// </para>
/// <para>
/// Naming note: the scanner calls the slot <c>briefing_difficulty_idx</c> because the briefing screen
/// owns the widget; the cfg rename puts it immediately after the mission-unlock memcpy in the
/// writer.  It is the difficulty LEVEL.
/// </para>
/// </remarks>
public enum BriefingDifficulty
{
    /// <summary>0 — Easy.</summary>
    Easy = 0,

    /// <summary>1 — Normal.  The shipped <c>yeager.cfg</c> value.</summary>
    Normal = 1,

    /// <summary>2 — Hard.</summary>
    Hard = 2,

    /// <summary>3 — Expert.</summary>
    Expert = 3,
}
