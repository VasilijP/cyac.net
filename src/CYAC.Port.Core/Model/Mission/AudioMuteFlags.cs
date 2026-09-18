namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The five-bit audio mask — persisted at <c>yeager.cfg@0x0D</c>, live at
/// <c>g_audio_mute_mask [0xE483]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Bit map from KNOWN_GLOBALS[0xE483]</c>: the five bits are XOR-toggled by the System menu's
/// <c>Engine</c> / <c>RWR</c> / <c>Stall</c> / <c>Lock Sounds</c> items through the
/// <c>ingame_menu_state_refresh</c> dispatch ladder @<c>image@0x21686..0x216A6</c>.  cfg-context
/// misnomer; the cfg byte does load here at boot, which is how it got that name.
/// </para>
/// <para>
/// <b>The polarity is not settled in the docs.</b>  The name says "mute", but the shipped file holds
/// <c>0xFE</c> — bit 0 clear and every other bit set, including the three bits above the documented
/// five.  The port therefore models the bits, not a mute/unmute predicate, and
/// <c>GameConfigTests</c> pins the shipped value rather than an interpretation of it.
/// </para>
/// </remarks>
[Flags]
public enum AudioMuteFlags
{
    /// <summary>No bits set.</summary>
    None = 0,

    /// <summary>0x01 — master.</summary>
    Master = 0x01,

    /// <summary>0x02 — engine sound.</summary>
    Engine = 0x02,

    /// <summary>0x04 — radar-warning receiver.</summary>
    RadarWarning = 0x04,

    /// <summary>0x08 — stall warning.</summary>
    Stall = 0x08,

    /// <summary>0x10 — missile-lock tone.</summary>
    Lock = 0x10,
}
