namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// One byte of the 50-slot campaign progression, as a name a modder can edit.
/// </summary>
/// <remarks>
/// Source of truth: <c>CYAC.Port.Core.Model.Mission.MissionProgression</c> — the array is
/// <c>g_mission_unlock_array [0xEF50]</c>, mirrored to <c>yeager.cfg@0x24..0x55</c>.  The writer
/// (<c>ui_post_death_message @image@0x25BAF</c>) increments a slot and clamps it at 2, and the picker
/// gate (<c>scenario_record_unlock_gate @image@0x246DE</c>) passes at &gt;= 2 — so 0/1/2 are the only
/// values the game itself produces.  Any other byte is still carried, as <c>0xNN</c>.
/// </remarks>
public enum MissionSlotState : byte
{
    /// <summary>0 — never flown; the record is closed unless the previous one is <see cref="Completed"/>.</summary>
    Locked = 0,

    /// <summary>1 — flown but not yet at the gate's threshold.</summary>
    Attempted = 1,

    /// <summary>2 — the writer's clamp: this record and the next one are open.</summary>
    Completed = 2,
}
