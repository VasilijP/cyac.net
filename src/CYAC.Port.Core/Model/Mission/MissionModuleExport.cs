namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The five entry points a <c>.S</c> mission module exports — the slot ABI the engine has always
/// called "the combat vtable".
/// </summary>
/// <remarks>
/// <para>
/// Mission success rules are <b>native code, not data</b>.  The loader <c>wld_or_s_asset_parser</c>
/// far-heap-allocs the trailer block and stores it at <c>[0xFB8]</c>/<c>[0xFBA]</c>
/// (<c>image@0x0A310..0x0A34C</c>); exactly four <c>les bx,[0xFB8]</c> sites exist image-wide, one per
/// dispatched export.  Slot 0 is reached instead by the briefing path.
/// </para>
/// <para>
/// The export table is five u16 offsets at the start of the block, each relative to the block base;
/// in shipped modules each target begins with the MSC far prologue <c>55 8B EC</c>.
/// </para>
/// </remarks>
public enum MissionModuleExport
{
    /// <summary>
    /// 0 — the briefing-text copier.  <c>s_asset_briefing_text_extract @image@0x0966B</c> allocates a
    /// 1200-byte far block (<c>mov ax,0x04B0</c> @<c>image@0x09671</c>) and far-calls this slot to fill
    /// it; the engine bound-checks nothing, so a longer briefing would overrun.
    /// </summary>
    BriefingText = 0,

    /// <summary>1 — the kill/event hook, dispatched by <c>combat_vtable_slot_fn2_dispatch @0x08C72</c> (<c>es:[bx+2]</c>).</summary>
    SlotDestroyed = 1,

    /// <summary>2 — the AI-bytecode <c>0xE0</c> script-callable hook, <c>combat_vtable_slot_fn4_dispatch @0x08CAB</c> (<c>es:[bx+4]</c>).</summary>
    ScriptHook = 2,

    /// <summary>
    /// 3 — the objective predicate, run <b>every frame</b> by
    /// <c>combat_vtable_slot_dispatch @0x08BF0</c> (<c>es:[bx+6]</c>); a non-zero <c>DX:AX</c> goes
    /// straight to <c>show_cockpit_text_string</c>.
    /// </summary>
    WinCondition = 3,

    /// <summary>4 — the debrief-text selector, called from <c>ui_post_death_message @0x25BAF</c> (<c>es:[bx+8]</c>).</summary>
    DebriefText = 4,
}
