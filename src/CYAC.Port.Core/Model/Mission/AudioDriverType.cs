namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The music/effects driver family — persisted at <c>yeager.cfg@0x0E</c>, live at
/// <c>[0xE482]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The enum is byte-proven by the <c>.DRV</c> near-pointer table @<c>image@0x3F1CE</c>
/// (KNOWN_GLOBALS[0xE482]</c>: index 3 → <c>tnddrive.drv</c>, index 4 → <c>cmsdrive.drv</c> —
/// which corrected the earlier "3=CMS/4=Tandy" reading).  It keys
/// <c>audio_sng_asset_load_if_needed</c>'s <c>.SNG</c> filename table;
/// </para>
/// <para>
/// <b>Dual-use slot</b> (P477): the same DGROUP byte is <c>g_joystick_mid_axis_hi</c>.  The scanner
/// says it outright — "Port should split joystick-calib vs audio-driver-type" — so the port keeps the
/// persisted byte here (it is what the file means) and will carry the joystick mid-axis separately
/// when the input path is wired up.
/// </para>
/// </remarks>
public enum AudioDriverType
{
    /// <summary>0 — no driver.</summary>
    Silent = 0,

    /// <summary>1 — PC speaker.  The value the shipped <c>yeager.cfg</c> carries.</summary>
    PcSpeaker = 1,

    /// <summary>2 — AdLib / OPL2.</summary>
    AdLib = 2,

    /// <summary>3 — Tandy / PCjr (<c>tnddrive.drv</c>).</summary>
    Tandy = 3,

    /// <summary>4 — Creative Music System / Game Blaster (<c>cmsdrive.drv</c>).</summary>
    CreativeMusicSystem = 4,
}
