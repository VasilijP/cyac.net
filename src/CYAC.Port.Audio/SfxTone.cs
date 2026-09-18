namespace CYAC.Port.Audio;

/// <summary>
/// The driver tone ids the engine's SFX trampolines push, each named for the trampoline that pushes
/// it.
/// </summary>
/// <remarks>
/// <para>
/// The driver's SFX tone space is <c>0x00..0x21</c> — ids <c>0x22..0x2B</c> are the ten <c>.SNG</c>
/// music program voices and never reach the tone table.
/// </para>
/// </remarks>
public static class SfxTone
{
    /// <summary>0x00 — jet engine, normal (continuous channel A; cmd-04 updatable).</summary>
    public const int JetEngine = 0x00;

    /// <summary>0x01 — jet engine, AFTERBURNER (<c>[0xF0BC]</c> bit 0).</summary>
    public const int JetAfterburner = 0x01;

    /// <summary>0x02 — jet engine, damaged (<c>[0xF1DC]</c>).</summary>
    public const int JetEngineDamaged = 0x02;

    /// <summary>0x03 — piston engine, normal.  The hottest cmd-04 target in the recorded driver stream.</summary>
    public const int PistonEngine = 0x03;

    /// <summary>0x04 — piston engine, damaged.</summary>
    public const int PistonEngineDamaged = 0x04;

    /// <summary>0x09 — <c>sfx_play_tone09 @image@0x29B60</c>; also <c>menu_key_sfx</c>'s AL=1 arm.</summary>
    public const int Tone09 = 0x09;

    /// <summary>0x0A — <c>sfx_play_tone0a @image@0x29B1C</c>, a per-frame ambient/alert cue.</summary>
    public const int Tone0A = 0x0A;

    /// <summary>0x0B — a continuous tone with a cmd-04 pitch stub (drv <c>@0x1851</c>); unused by CYAC.</summary>
    public const int Tone0B = 0x0B;

    /// <summary>
    /// 0x0C — THE GUN.  <c>sfx_weapon_type_tone_dispatch @image@0x29A79</c>'s non-air-to-ground
    /// arm pushes <c>(tone 0x0C, pitch 5, volume = weapon[+0x2B])</c>.
    /// </summary>
    /// <remarks>
    /// This confirms the reading (218 of 330 catalogued SFX events in a recorded session were
    /// tone 0x0C, "gunfire (hypothesis)") and explains its constant <c>(5, 82)</c> argument pair:
    /// 82 = 0x52 is the firing weapon record's own <c>+0x2B</c> byte.  Its state block carries
    /// <c>retrig_a = 1, loop = 6</c> — a six-times-re-attacked burst.
    /// </remarks>
    public const int Gun = 0x0C;

    /// <summary>0x0D — <c>sfx_play_tone0d @image@0x29B82</c>; the engagement altitude-clamp cue.</summary>
    public const int Tone0D = 0x0D;

    /// <summary>0x0E — IR lock, low threat (<c>[0xF1B7] == 1</c>, weapon-name byte 1).</summary>
    public const int LockLowRadar = 0x0E;

    /// <summary>0x0F — radar lock, high threat (<c>[0xF1B7] == 2</c>, weapon-name byte 1).</summary>
    public const int LockHighRadar = 0x0F;

    /// <summary>0x10 — the <see cref="LockLowRadar"/> pair's non-radar partner (<c>+2</c>).</summary>
    public const int LockLowInfrared = 0x10;

    /// <summary>0x11 — the RADAR-WARNING tone; the only cockpit tone that is cmd-04 updatable.</summary>
    public const int RadarWarning = 0x11;

    /// <summary>
    /// 0x13 — the gear / flap / airbrake ACTUATOR, run by the
    /// <c>audio_threshold_bump_*</c> deadline.
    /// </summary>
    /// <remarks>
    /// The three bump trampolines <c>@image@0x29B36 / 0x29B4B / 0x29B6D</c> set the threshold to
    /// <c>now + 0x20 / 0x10 / 0x100</c> for flaps, airbrake and gear, and the engine channel plays
    /// this tone for exactly that long. It is the mechanism's own noise, and the gear — 0x100
    /// frame-time units — runs sixteen times as long as the flaps.
    /// </remarks>
    public const int Mechanism = 0x13;

    /// <summary>0x14 — <c>sfx_play_tone14_fallback @image@0x29ABD</c>, the soft impact.</summary>
    public const int ImpactFallback = 0x14;

    /// <summary>0x15 — weapon impact, variant A (<c>sfx_play_tone15_random @image@0x29ACA</c>).</summary>
    public const int ImpactA = 0x15;

    /// <summary>0x16 — weapon impact, variant B (<c>sfx_play_tone16_random @image@0x29AF2</c>).</summary>
    public const int ImpactB = 0x16;

    /// <summary>0x17 — air-to-ground weapon fire (<c>[weapon+0x24] &amp; 0x10</c>).</summary>
    public const int AirToGroundFire = 0x17;

    /// <summary>0x1C — the menu keypress click; also the soft ground-impact thud.</summary>
    public const int MenuClick = 0x1C;

    /// <summary>0x1E — cockpit ambient for <c>[0xF0BA] == 2</c>, gated by the "Stall sounds" bit.</summary>
    public const int Stall1 = 0x1E;

    /// <summary>0x1F — cockpit ambient for <c>[0xF0BA] == 3</c>.</summary>
    public const int Stall2 = 0x1F;

    /// <summary>0x20 — <c>sfx_countermeasure_deploy_sound @image@0x29B29</c>, chaff / flare.</summary>
    public const int CountermeasureDeploy = 0x20;

    /// <summary>0x21 — the <see cref="LockHighRadar"/> pair's non-radar partner (<c>+0x12</c>).</summary>
    public const int LockHighInfrared = 0x21;
}
