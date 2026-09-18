using System.Globalization;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Model.Cockpit;

/// <summary>
/// The eleven widget bits <c>hud_per_frame_draw @image@0x0C5A7</c> dispatches on, in the order it
/// tests them.
/// </summary>
/// <remarks>
/// The mask lives in <c>SI</c> for the whole function and every arm is one <c>test si, bit</c>.  The
/// names here are what each arm DRAWS, which is not always what the round-17c table called it: bit
/// <c>0x002</c> draws the vertical-speed line and bit <c>0x004</c> the heading number, and bit
/// <c>0x020</c> is the weapon+ammo readout (its LUT read at <c>image@0x0C885</c> is
/// <c>[bx − 0x12D4] ≡ g_hud_weapon_slot_ammo [0xED2C]</c>, H10a F8), not a "chance to hit" — the
/// chance is only the <c>" (%d%%)"</c> suffix it appends when a target is selected.
/// </remarks>
[Flags]
public enum HudWidget
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary><c>0x001</c> — the altitude readout, <c>"%6u FT"</c>.</summary>
    Altitude = 0x001,

    /// <summary><c>0x002</c> — the <c>"VSI:"</c> line (and its landing-ready <c>"*** VSI:"</c> form).</summary>
    VerticalSpeed = 0x002,

    /// <summary><c>0x004</c> — the centred heading number and its degree sign.</summary>
    Heading = 0x004,

    /// <summary>
    /// <c>0x008</c> — the selected target's NAME.  Dead in the shipped game: neither live mask
    /// carries this bit (<c>0x7F7</c> and <c>0x2F7</c> both have bit 3 clear).
    /// </summary>
    TargetName = 0x008,

    /// <summary><c>0x010</c> — the airspeed readout and the G-load line under it.</summary>
    Airspeed = 0x010,

    /// <summary><c>0x020</c> — the weapon name, its rounds and the chance-to-hit suffix.</summary>
    WeaponAmmo = 0x020,

    /// <summary><c>0x040</c> — <c>"THR: nnn%"</c> / <c>"AFT: nnn%"</c>.</summary>
    Throttle = 0x040,

    /// <summary><c>0x080</c> — the FLAPS / BRAKE / GEAR text stack.</summary>
    StatusText = 0x080,

    /// <summary><c>0x100</c> — the post-pass arm that calls <c>hud_target_screen_marker_draw</c>.</summary>
    PostLand = 0x100,

    /// <summary><c>0x200</c> — <c>"ZOOM:n"</c> and, under time compression, <c>"TIME:n"</c>.</summary>
    Zoom = 0x200,

    /// <summary>
    /// <c>0x400</c> — the target marker block: the lock box or the gunsight pipper, and the waterline
    /// marker, which is drawn unconditionally INSIDE this arm.
    /// </summary>
    Marker = 0x400,
}

/// <summary>
/// Which HUD widgets the original draws this frame.  It is a VIEW gate.
/// </summary>
/// <remarks>
/// <para>
/// <c>image@0x0C620..0x0C645</c>:
/// <c>si = ([0xE46F] != 0) ? ([0xB0] ? 0x7F7 : 0x500) : ([0xB0] ? 0x2F7 : return)</c>.
/// </para>
/// <para>
/// <c>[0xE46F]</c> is <c>g_view_mode_bit0_flag</c> — bit 0 of the current view's flag byte
/// <c>[0x2BC0 + view]</c>, set on the FORWARD view (id 0) and no other of the twenty (H10a F2, sole
/// writer <c>publish_view_mode</c> tail <c>image@0x23870</c>).  <c>[0xB0]</c> is
/// <c>g_flight_info_visible</c>, the Ctrl-F toggle.  So the difference between the forward view and
/// every other view is exactly <c>0x100 | 0x400</c>: an external view keeps all the TEXT and loses
/// the target marker, the gunsight and the waterline.  a captured frame of the original (cockpit
/// off, external chase) shows precisely that.
/// </para>
/// <para>
/// The decoded file labels <c>0x500</c> "post-crash"; it is not — it is the forward view with the
/// flight info switched OFF, i.e. the marker block alone.
/// </para>
/// </remarks>
public static class HudMask
{
    /// <summary>The forward view with the flight info on — every widget.</summary>
    public const int ForwardWithFlightInfo = 0x07F7;

    /// <summary>The forward view with the flight info off — the marker block alone.</summary>
    public const int ForwardMarkerOnly = 0x0500;

    /// <summary>Any other view with the flight info on — the text widgets, no marker.</summary>
    public const int OtherViewText = 0x02F7;

    /// <summary>
    /// The two corner blocks an OVERLAY WINDOW takes over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four in-flight windows do not overdraw the HUD's corners; the corners are simply not
    /// drawn while a window owns them.  Measured across the six <c>[0xF1CB]</c> variants
    /// (a captured frame of the original):
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <c>ZOOM:n</c> plus the weapon/ammo/hit-chance line (top left, <see cref="HudWidget.Zoom"/> |
    /// <see cref="HudWidget.WeaponAmmo"/>) is drawn in <c>cfg 0x00</c>, <c>0x02</c> and <c>0x08</c> and absent
    /// in <c>0x01</c>, <c>0x04</c> and <c>0x0F</c> — i.e. absent exactly when the ENVELOPE or the MAP
    /// holds the first slot.
    /// </item>
    /// <item>
    /// <c>THR:</c> plus <c>VSI:</c> (top right, <see cref="HudWidget.Throttle"/> |
    /// <see cref="HudWidget.VerticalSpeed"/>) is drawn in <c>cfg 0x00</c>, <c>0x01</c>, <c>0x04</c> and
    /// <c>0x08</c> and absent in <c>0x02</c> and <c>0x0F</c> — i.e. absent exactly when the TARGET
    /// window is up.
    /// </item>
    /// </list>
    /// <para>
    /// The altitude, airspeed, G-load and heading readouts sit ABOVE the window band (rows 0..3
    /// against the band's top row 19) and are never suppressed.
    /// </para>
    /// </remarks>
    public const int TopLeftBlock = (int)HudWidget.Zoom | (int)HudWidget.WeaponAmmo;

    /// <summary>The top-right block a TARGET window takes over.  See <see cref="TopLeftBlock"/>.</summary>
    public const int TopRightBlock = (int)HudWidget.Throttle | (int)HudWidget.VerticalSpeed;

    /// <summary>Drops the corner blocks an overlay window owns.</summary>
    /// <param name="mask">The view's own mask.</param>
    /// <param name="coversTopLeft">Whether a left-packed window holds the first slot.</param>
    /// <param name="coversTopRight">Whether the TARGET window is up.</param>
    /// <returns>The mask with the covered blocks cleared.</returns>
    public static HudWidget WithoutCoveredBlocks(
        HudWidget mask, bool coversTopLeft, bool coversTopRight)
    {
        if (coversTopLeft)
        {
            mask &= ~(HudWidget)TopLeftBlock;
        }

        if (coversTopRight)
        {
            mask &= ~(HudWidget)TopRightBlock;
        }

        return mask;
    }

    /// <summary>Which widgets are drawn.</summary>
    /// <param name="forwardView">Whether the current view is the forward one (<c>[0xE46F]</c>).</param>
    /// <param name="flightInfoVisible">The Ctrl-F toggle <c>g_flight_info_visible [0xB0]</c>.</param>
    public static HudWidget For(bool forwardView, bool flightInfoVisible) =>
        forwardView
            ? (HudWidget)(flightInfoVisible ? ForwardWithFlightInfo : ForwardMarkerOnly)
            : (flightInfoVisible ? (HudWidget)OtherViewText : HudWidget.None);
}

/// <summary>
/// Every string the HUD overlay prints, formatted the way the original formats it.
/// </summary>
/// <remarks>
/// <para>
/// Each method is one arm of <c>hud_per_frame_draw</c> or one of its four format helpers, with the
/// original's own format string, its own arithmetic and its own byte shuffles.  The quirks are kept:
/// they are what the 1991 screen showed.
/// </para>
/// <para>
/// The words and format strings come from the tree (<see cref="InFlightStrings"/>, by the DGROUP
/// address each arm uses); what stays here is the arithmetic and the shuffles. Ruling A1,.
/// </para>
/// </remarks>
public static class HudText
{
    /// <summary>The degree sign the heading readout appends — DGROUP <c>[0x3191]</c>, one byte.</summary>
    public const char DegreeSign = '\u007F';

    // InFlightStrings.NoWeaponReadout, DGROUP [0x31A1].

    /// <summary>The full circle in the engine's ⅛-degree BAM.</summary>
    public const int BamFullCircle = 0xB40;

    /// <summary>The altitude readout, in the long or the word format.</summary>
    /// <param name="strings">The in-flight words and formats.</param>
    /// <param name="feet">Altitude in whole feet; <c>abs_i24_pack @image@0x0F5AE</c> clamps it at 0.</param>
    /// <remarks>
    /// <c>image@0x0C65C</c> picks the signed long format (<c>[0x3030]</c>) when the high word is set and
    /// the unsigned short one (<c>[0x3038]</c>) when it is not.
    /// </remarks>
    public static string Altitude(InFlightStrings strings, int feet)
    {
        ArgumentNullException.ThrowIfNull(strings);
        int clamped = Math.Max(0, feet);
        return clamped > ushort.MaxValue
            ? PrintfFormat.Format(strings.AltitudeLongFormat, clamped)
            : PrintfFormat.Format(strings.AltitudeWordFormat, (ushort)clamped);
    }

    /// <summary>The airspeed readout, in the airspeed format (<c>[0x3070]</c>).</summary>
    /// <param name="strings">The in-flight words and formats.</param>
    /// <param name="feetPerSecond">The player object's TAS at <c>+0x26</c>.</param>
    /// <remarks>
    /// <c>hud_speed_indicator_format @image@0x0CB73</c>: <c>mph = tas · 3600 / 5280</c> through
    /// <c>muldiv @image@0x11974</c> (<c>dx = 0x0E10</c>, <c>bx = 0x14A0</c>), clamped at zero
    /// (<c>image@0x0CB8C</c>) — feet per second to miles per hour.
    /// </remarks>
    public static string Airspeed(InFlightStrings strings, int feetPerSecond)
    {
        ArgumentNullException.ThrowIfNull(strings);
        int mph = (int)((long)feetPerSecond * 3600 / 5280);
        return PrintfFormat.Format(strings.AirspeedFormat, Math.Max(0, mph));
    }

    /// <summary>The G-load line, <c>" 1.0 G"</c>.</summary>
    /// <param name="gLoadQ8"><c>g_player_gload_q8 [0xF06E]</c> — 1.0 G is <c>0x0100</c>.</param>
    /// <remarks>
    /// <c>image@0x0C6C5..0x0C70A</c>: <c>g10 = (gload · 10) &gt;&gt; 8</c> (a signed
    /// <c>muldiv16_signed_shr8</c>), the sign goes into <c>buf[0]</c> as <c>' '</c> or <c>'-'</c>,
    /// <c>"%02d"</c> lands at <c>buf+1</c>, and then four byte writes turn <c>" 10"</c> into <c>"
    /// 1.0 G"</c>.  The shuffle only moves ONE digit, so a reading of 10.0 G or more prints its
    /// tens digit and loses the units digit — a shipped quirk, reproduced here.
    /// </remarks>
    public static string GLoad(int gLoadQ8)
    {
        int tenths = (gLoadQ8 * 10) >> 8;
        char sign = ' ';
        if (tenths < 0)
        {
            tenths = -tenths;
            sign = '-';
        }

        // sprintf(buf + 1, "%02d", tenths) — at least two digits, more if the value needs them.
        string digits = tenths.ToString("D2", CultureInfo.InvariantCulture);
        char[] buf = new char[7];
        buf[0] = sign;
        for (int i = 0; i < 6 && i < digits.Length; i++)
        {
            buf[1 + i] = digits[i];
        }

        buf[5] = 'G';
        buf[4] = ' ';
        buf[3] = buf[2];
        buf[2] = '.';
        return new string(buf, 0, 6);
    }

    /// <summary>The vertical-speed digits, <c>"03.18"</c> with a leading sign column.</summary>
    /// <param name="verticalSpeed"><c>g_vertical_speed_i32 [0xF1C0]</c>.</param>
    /// <remarks>
    /// <c>hud_vertical_speed_format @image@0x0CBA7</c>: divide by <b>43</b>, write <c>' '</c> or
    /// <c>'-'</c>, print <c>"%04d"</c> (DGROUP <c>[0x31A9]</c>), then <c>buf[4]=buf[3]</c>,
    /// <c>buf[3]=buf[2]</c>, <c>buf[2]='.'</c>.
    /// </remarks>
    public static string VerticalSpeed(int verticalSpeed)
    {
        int v = verticalSpeed / 43;
        char sign = ' ';
        if (v < 0)
        {
            v = -v;
            sign = '-';
        }

        string digits = v.ToString("D4", CultureInfo.InvariantCulture);
        char[] buf = new char[8];
        buf[0] = sign;
        for (int i = 0; i < 6 && i < digits.Length; i++)
        {
            buf[1 + i] = digits[i];
        }

        // image@0x0CBDC..0x0CBEF, with SI already one past the sign byte: buf[6] = 0,
        // buf[5] = buf[4], buf[4] = buf[3], buf[3] = '.' — four digits become "dd.dd".
        buf[6] = '\0';
        buf[5] = buf[4];
        buf[4] = buf[3];
        buf[3] = '.';
        return new string(buf, 0, 6);
    }

    /// <summary>The whole vertical-speed line, prefix included.</summary>
    /// <param name="strings">The in-flight words and formats.</param>
    /// <param name="verticalSpeed"><c>g_vertical_speed_i32 [0xF1C0]</c>.</param>
    /// <param name="landingReady">
    /// Whether the aircraft is a prop with its gear down and <c>crash_conditions_valid
    /// @image@0x2C25C</c> says a touchdown would be survivable — the original's own three-star cue.
    /// </param>
    /// <returns>The line and the x offset it is drawn at, left of <c>vsiAnchorX</c>.</returns>
    /// <remarks>
    /// <c>image@0x0C763..0x0C7F7</c>: the landing-ready prefix (<c>[0x3048]</c>) at <c>0x38</c>, the plain
    /// one (<c>[0x3052]</c>) at <c>0x28</c>.
    /// </remarks>
    public static (string Text, int XOffset) VerticalSpeedLine(
        InFlightStrings strings, int verticalSpeed, bool landingReady)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return landingReady
            ? (strings.VsiLandingPrefix + VerticalSpeed(verticalSpeed), 0x38)
            : (strings.VsiPrefix + VerticalSpeed(verticalSpeed), 0x28);
    }

    /// <summary>The throttle line: the throttle or afterburner word and a percentage.</summary>
    /// <param name="strings">The in-flight words and formats.</param>
    /// <param name="percent"><c>[0xF035]</c>, which this arm prints as a percentage.</param>
    /// <param name="afterburner"><c>g_input_state_bitfield [0xF0BC]</c> bit 0.</param>
    /// <remarks>
    /// <c>image@0x0C722..0x0C762</c>: the format at DGROUP <c>[0x3187]</c>, filled with the word at
    /// <c>[0x3040]</c> (afterburner) or <c>[0x3044]</c>.
    /// </remarks>
    public static string Throttle(InFlightStrings strings, int percent, bool afterburner)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return PrintfFormat.Format(
            strings.ThrottleFormat, afterburner ? strings.AfterburnerWord : strings.ThrottleWord, percent);
    }

    /// <summary>The heading number and its degree sign.</summary>
    /// <param name="headingBam">The player object's <c>+0x12</c> heading in ⅛-degree BAM.</param>
    /// <remarks>
    /// <c>image@0x0C7FE..0x0C818</c>: NEGATE, wrap into <c>[0, 0xB40)</c>
    /// (<c>angle_wrap_0_to_0xB40 @image@0x18410</c>), arithmetic-shift right by 3 — 2,880 BAM / 8 =
    /// 360 — then decimal, then <c>strcat</c> of DGROUP <c>[0x3191]</c>, the single byte
    /// <c>0x7F</c>, which the game's font draws as the degree ring.
    /// </remarks>
    public static string Heading(int headingBam)
    {
        int wrapped = ((-headingBam % BamFullCircle) + BamFullCircle) % BamFullCircle;
        return string.Create(CultureInfo.InvariantCulture, $"{wrapped >> 3}{DegreeSign}");
    }

    /// <summary>The zoom line: the zoom prefix and the magnification.</summary>
    /// <param name="strings">The in-flight words and formats.</param>
    /// <param name="zoomLevel"><c>g_camera_zoom_level [0xD8A0]</c>, 7..12.</param>
    /// <remarks>
    /// <c>gauge_dial_zoom_draw @image@0x0D85C</c>: <c>strcpy</c> of DGROUP <c>[0x3BB8]</c>, then
    /// <c>1 &lt;&lt; (level − 7)</c> appended after it.
    /// </remarks>
    public static string Zoom(InFlightStrings strings, int zoomLevel)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return strings.ZoomPrefix
            + (1 << Math.Clamp(zoomLevel - 7, 0, 15)).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The time-compression line: the time prefix and the factor; empty at 1×.</summary>
    /// <param name="strings">The in-flight words and formats.</param>
    /// <param name="compressionShift"><c>g_scene_tick_shift [0xF104]</c>, 0/1/2.</param>
    /// <remarks>
    /// <c>image@0x0C977..0x0C9C1</c> draws it only when the shift is non-zero, as the prefix at
    /// <c>[0x306A]</c> followed by <c>1 &lt;&lt; shift</c>.  This closes the decoded file's open
    /// question Q1: the value IS the time compression — the same 1× / 2× / 4× the System menu offers —
    /// not a weapon id.
    /// </remarks>
    public static string TimeCompression(InFlightStrings strings, int compressionShift)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return compressionShift == 0
            ? string.Empty
            : strings.TimePrefix
                + (1 << Math.Clamp(compressionShift, 0, 15)).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The weapon + ammo readout, with the chance-to-hit suffix.</summary>
    /// <param name="strings">The in-flight words and formats.</param>
    /// <param name="weaponName">The selected slot's name, or null for no weapon.</param>
    /// <param name="rounds">Its rounds — <c>g_hud_weapon_slot_ammo [0xED2C + 2·slot]</c>.</param>
    /// <param name="hitPercent">
    /// The chance-to-hit percentage, or −1 when no target is selected or the slot is empty.
    /// </param>
    /// <remarks>
    /// <c>image@0x0C871..0x0C901</c>: the name-and-rounds format (DGROUP <c>[0x3193]</c>) and, when
    /// both <c>g_lockon_target [0x00BC]</c> and the ammo count are non-zero, the chance-to-hit format
    /// (<c>[0x3199]</c>) appended.  With no weapon record the whole thing is the no-weapon readout
    /// (<c>[0x31A1]</c>, copied as four words at <c>image@0x0C8E0</c>).
    /// </remarks>
    public static string WeaponAmmo(
        InFlightStrings strings, string? weaponName, int rounds, int hitPercent = -1)
    {
        ArgumentNullException.ThrowIfNull(strings);
        if (string.IsNullOrEmpty(weaponName) || rounds < 0)
        {
            return strings.NoWeaponReadout;
        }

        string text = PrintfFormat.Format(strings.WeaponFormat, weaponName, rounds);
        return hitPercent < 0
            ? text
            : text + PrintfFormat.Format(strings.HitChanceFormat, hitPercent);
    }
}
