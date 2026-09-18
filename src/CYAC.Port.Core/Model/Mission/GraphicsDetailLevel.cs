namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The Graphics-menu detail level — persisted at <c>yeager.cfg@0x11</c>, live at
/// <c>g_graphics_detail_level [0xF108]</c>.
/// </summary>
/// <remarks>
/// The cfg byte loads here at boot but the runtime role is the graphics detail level, written by
/// <c>set_detail_level @image@0x23389</c> from the Graphics menu; One of the three "dual-use cfg
/// globals" the project tracks: the persisted slot and the runtime global are the same byte, and the
/// port keeps the persisted meaning here.
/// </remarks>
public enum GraphicsDetailLevel
{
    /// <summary>0 — Low.</summary>
    Low = 0,

    /// <summary>1 — Medium.  The shipped <c>yeager.cfg</c> value.</summary>
    Medium = 1,

    /// <summary>2 — High.</summary>
    High = 2,
}
