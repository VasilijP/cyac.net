namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// Which in-flight overlay windows are visible — persisted at <c>yeager.cfg@0x1D</c>, live at
/// <c>g_inflight_overlay_visibility [0xF1CB]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Bit map from KNOWN_GLOBALS[0xF1CB]</c>: the four bits are toggled by Shift-1..Shift-4 and by
/// the Help menu's Map / Envelope / Target / Yeager Window items, and <c>cockpit_panel_draw_dispatch
/// @0x0EA65</c> reads the word as the gate over the four cockpit sub-panels — the "PG CONFIRMED" note
/// in the scanner entry. cfg-context misnomer.
/// </para>
/// <para>
/// The third of the three dual-use cfg slots the port must eventually split (the other two are
/// <see cref="AudioMuteFlags"/> <c>[0xE483]</c> and <see cref="GraphicsDetailLevel"/> <c>[0xF108]</c>):
/// the persisted byte and the live overlay state are the same storage.
/// </para>
/// </remarks>
[Flags]
public enum CockpitOverlayFlags
{
    /// <summary>No overlay visible.</summary>
    None = 0,

    /// <summary>0x01 — the flight-envelope window.</summary>
    Envelope = 0x01,

    /// <summary>0x02 — the target window.  Set in the shipped <c>yeager.cfg</c>.</summary>
    Target = 0x02,

    /// <summary>0x04 — the map window.  Set in the shipped <c>yeager.cfg</c>.</summary>
    Map = 0x04,

    /// <summary>0x08 — the Yeager advisor window.</summary>
    Yeager = 0x08,
}
