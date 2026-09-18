using System.Collections.Frozen;
using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Model.Cockpit;

/// <summary>
/// The words and formats the in-flight screen draws, read from <c>exe/strings.json</c> by the DGROUP
/// address the original draws each one from.
/// </summary>
/// <remarks>
/// <para>
/// The HUD's status words and readout formats, the cockpit's empty weapon name, the overlay windows'
/// titles and footer format, the TARGET window's o'clock line and speed suffix, the NAV label and the
/// ESC menu's exit word.  Each address below is a citation: it names the literal the drawing code pushes
/// or copies (the <c>image@</c> next to it).  The text itself comes from the transformed tree; the port
/// carries none of it.
/// </para>
/// <para>
/// Every literal is required: a tree that lacks one is refused at load with the address and the role,
/// which is what the pre-flight Load step reports.  The formats are C <c>printf</c> literals and are
/// filled with <see cref="Primitives.PrintfFormat"/>.
/// </para>
/// </remarks>
public sealed class InFlightStrings
{
    /// <summary>The empty weapon name — <c>get_weapon_name_ptr_or_fallback @image@0x23E3C</c>.</summary>
    public const int EmptyWeaponNameDgroup = 0x2E6C;

    /// <summary>The altitude format for a value above 16 bits, <c>%6ld</c> — picked at <c>image@0x0C65C</c>.</summary>
    public const int AltitudeLongFormatDgroup = 0x3030;

    /// <summary>The altitude format for a 16-bit value, <c>%6u</c> — picked at <c>image@0x0C65C</c>.</summary>
    public const int AltitudeWordFormatDgroup = 0x3038;

    /// <summary>The throttle line's afterburner word — <c>image@0x0C722..0x0C762</c>.</summary>
    public const int AfterburnerWordDgroup = 0x3040;

    /// <summary>The throttle line's throttle word — <c>image@0x0C722..0x0C762</c>.</summary>
    public const int ThrottleWordDgroup = 0x3044;

    /// <summary>The vertical-speed prefix of the landing-ready cue — <c>image@0x0C763..0x0C7F7</c>.</summary>
    public const int VsiLandingPrefixDgroup = 0x3048;

    /// <summary>The plain vertical-speed prefix — <c>image@0x0C763..0x0C7F7</c>.</summary>
    public const int VsiPrefixDgroup = 0x3052;

    /// <summary>The status stack's flaps word — <c>image@0x0C902</c>.</summary>
    public const int FlapsWordDgroup = 0x3058;

    /// <summary>The status stack's brake word — <c>image@0x0C902</c>.</summary>
    public const int BrakeWordDgroup = 0x305E;

    /// <summary>The status stack's gear word — <c>image@0x0C902</c>.</summary>
    public const int GearWordDgroup = 0x3064;

    /// <summary>The time-compression prefix — <c>image@0x0C977..0x0C9C1</c>.</summary>
    public const int TimePrefixDgroup = 0x306A;

    /// <summary>The airspeed format — pushed by <c>hud_speed_indicator_format @image@0x0CB93</c>.</summary>
    public const int AirspeedFormatDgroup = 0x3070;

    /// <summary>The throttle line's format — <c>image@0x0C722..0x0C762</c>.</summary>
    public const int ThrottleFormatDgroup = 0x3187;

    /// <summary>The weapon readout's format — <c>image@0x0C871</c>.</summary>
    public const int WeaponFormatDgroup = 0x3193;

    /// <summary>The weapon readout's chance-to-hit suffix format — <c>image@0x0C8A5..0x0C8D6</c>.</summary>
    public const int HitChanceFormatDgroup = 0x3199;

    /// <summary>The readout for no weapon at all — copied as four words at <c>image@0x0C8E0</c>.</summary>
    public const int NoWeaponReadoutDgroup = 0x31A1;

    /// <summary>The zoom prefix — <c>gauge_dial_zoom_draw @image@0x0D85C</c>.</summary>
    public const int ZoomPrefixDgroup = 0x3BB8;

    /// <summary>The MAP window's title — pushed at <c>image@0x0EAF4</c>.</summary>
    public const int MapTitleDgroup = 0x3BBE;

    /// <summary>The ENVELOPE window's title — the envelope drawer's band (<c>image@0x0EBB0</c>).</summary>
    public const int EnvelopeTitleDgroup = 0x3BC2;

    /// <summary>The TARGET window's title word — drawn at design column 248 (<c>image@0x0EDCF</c>).</summary>
    public const int TargetTitleDgroup = 0x3BCC;

    /// <summary>The YEAGER window's title — pushed at <c>image@0x0EF3E</c>.</summary>
    public const int AdvisorTitleDgroup = 0x3BD4;

    /// <summary>
    /// The ENVELOPE window's footer format — picked at <c>image@0x0EBE4</c> for an altitude above 16
    /// bits.
    /// </summary>
    /// <remarks>
    /// Its 16-bit twin at <c>[0x3BF2]</c> differs only in the conversion letter (<c>%5u</c>), and the
    /// catalogue splits that one at its zone's end, so the port fills this format for both forms and
    /// passes the 16-bit value for the word form.
    /// </remarks>
    public const int EnvelopeFooterFormatDgroup = 0x3BE2;

    /// <summary>The NAV label's format — <c>nav_waypoint_show_current @image@0x08D31</c>.</summary>
    public const int NavLabelFormatDgroup = 0x0F9E;

    /// <summary>The TARGET window's speed suffix — the radar/target drawer.</summary>
    public const int TargetSpeedSuffixDgroup = 0x0FFA;

    /// <summary>The o'clock line's format — <c>oclock_bearing_format @image@0x0AAA4</c>.</summary>
    public const int ClockFormatDgroup = 0x1000;

    /// <summary>The o'clock line's low suffix — <c>image@0x0AB36</c>.</summary>
    public const int LowSuffixDgroup = 0x100C;

    /// <summary>The o'clock line's high suffix — <c>image@0x0AB41</c>.</summary>
    public const int HighSuffixDgroup = 0x1010;

    /// <summary>
    /// The exit word the port's ESC menu shortens its exit row to — the widget toolkit's own exit
    /// button caption, the front end's <c>ExitDgroup</c>.
    /// </summary>
    public const int MenuExitWordDgroup = 0x3686;

    private readonly FrozenDictionary<int, string> _byDgroup;

    private InFlightStrings(FrozenDictionary<int, string> byDgroup) => _byDgroup = byDgroup;

    /// <summary>Every literal this type requires, with what it is for — the load check's list.</summary>
    public static IReadOnlyList<(int Dgroup, string Role)> Required { get; } =
    [
        (EmptyWeaponNameDgroup, "the cockpit's empty weapon name"),
        (AltitudeLongFormatDgroup, "the HUD's long altitude format"),
        (AltitudeWordFormatDgroup, "the HUD's word altitude format"),
        (AfterburnerWordDgroup, "the HUD's afterburner word"),
        (ThrottleWordDgroup, "the HUD's throttle word"),
        (VsiLandingPrefixDgroup, "the HUD's landing-ready vertical-speed prefix"),
        (VsiPrefixDgroup, "the HUD's vertical-speed prefix"),
        (FlapsWordDgroup, "the HUD's flaps word"),
        (BrakeWordDgroup, "the HUD's brake word"),
        (GearWordDgroup, "the HUD's gear word"),
        (TimePrefixDgroup, "the HUD's time-compression prefix"),
        (AirspeedFormatDgroup, "the HUD's airspeed format"),
        (ThrottleFormatDgroup, "the HUD's throttle format"),
        (WeaponFormatDgroup, "the HUD's weapon format"),
        (HitChanceFormatDgroup, "the HUD's chance-to-hit format"),
        (NoWeaponReadoutDgroup, "the HUD's no-weapon readout"),
        (ZoomPrefixDgroup, "the HUD's zoom prefix"),
        (MapTitleDgroup, "the MAP window's title"),
        (EnvelopeTitleDgroup, "the ENVELOPE window's title"),
        (TargetTitleDgroup, "the TARGET window's title"),
        (AdvisorTitleDgroup, "the YEAGER window's title"),
        (EnvelopeFooterFormatDgroup, "the ENVELOPE window's footer format"),
        (NavLabelFormatDgroup, "the NAV label's format"),
        (TargetSpeedSuffixDgroup, "the TARGET window's speed suffix"),
        (ClockFormatDgroup, "the o'clock line's format"),
        (LowSuffixDgroup, "the o'clock line's low suffix"),
        (HighSuffixDgroup, "the o'clock line's high suffix"),
        (MenuExitWordDgroup, "the ESC menu's exit word"),
    ];

    /// <summary>The empty weapon name the cockpit readout prints.</summary>
    public string EmptyWeaponName => At(EmptyWeaponNameDgroup);

    /// <summary>The HUD's altitude format for a value above 16 bits.</summary>
    public string AltitudeLongFormat => At(AltitudeLongFormatDgroup);

    /// <summary>The HUD's altitude format for a 16-bit value.</summary>
    public string AltitudeWordFormat => At(AltitudeWordFormatDgroup);

    /// <summary>The throttle line's afterburner word.</summary>
    public string AfterburnerWord => At(AfterburnerWordDgroup);

    /// <summary>The throttle line's throttle word.</summary>
    public string ThrottleWord => At(ThrottleWordDgroup);

    /// <summary>The landing-ready vertical-speed prefix.</summary>
    public string VsiLandingPrefix => At(VsiLandingPrefixDgroup);

    /// <summary>The plain vertical-speed prefix.</summary>
    public string VsiPrefix => At(VsiPrefixDgroup);

    /// <summary>The status stack's flaps word.</summary>
    public string FlapsWord => At(FlapsWordDgroup);

    /// <summary>The status stack's brake word.</summary>
    public string BrakeWord => At(BrakeWordDgroup);

    /// <summary>The status stack's gear word.</summary>
    public string GearWord => At(GearWordDgroup);

    /// <summary>The time-compression prefix.</summary>
    public string TimePrefix => At(TimePrefixDgroup);

    /// <summary>The airspeed format.</summary>
    public string AirspeedFormat => At(AirspeedFormatDgroup);

    /// <summary>The throttle line's format: a word and a percentage.</summary>
    public string ThrottleFormat => At(ThrottleFormatDgroup);

    /// <summary>The weapon readout's format: a name and a round count.</summary>
    public string WeaponFormat => At(WeaponFormatDgroup);

    /// <summary>The weapon readout's chance-to-hit suffix format.</summary>
    public string HitChanceFormat => At(HitChanceFormatDgroup);

    /// <summary>The readout for no weapon at all.</summary>
    public string NoWeaponReadout => At(NoWeaponReadoutDgroup);

    /// <summary>The zoom prefix.</summary>
    public string ZoomPrefix => At(ZoomPrefixDgroup);

    /// <summary>The MAP window's title.</summary>
    public string MapTitle => At(MapTitleDgroup);

    /// <summary>The ENVELOPE window's title.</summary>
    public string EnvelopeTitle => At(EnvelopeTitleDgroup);

    /// <summary>The TARGET window's title word.</summary>
    public string TargetTitle => At(TargetTitleDgroup);

    /// <summary>The YEAGER window's title.</summary>
    public string AdvisorTitle => At(AdvisorTitleDgroup);

    /// <summary>The ENVELOPE window's footer format: a load factor and an altitude.</summary>
    public string EnvelopeFooterFormat => At(EnvelopeFooterFormatDgroup);

    /// <summary>The NAV label's format: a slot number and a name.</summary>
    public string NavLabelFormat => At(NavLabelFormatDgroup);

    /// <summary>The TARGET window's speed suffix.</summary>
    public string TargetSpeedSuffix => At(TargetSpeedSuffixDgroup);

    /// <summary>The o'clock line's format: an hour.</summary>
    public string ClockFormat => At(ClockFormatDgroup);

    /// <summary>The o'clock line's suffix for a target well below.</summary>
    public string LowSuffix => At(LowSuffixDgroup);

    /// <summary>The o'clock line's suffix for a target well above.</summary>
    public string HighSuffix => At(HighSuffixDgroup);

    /// <summary>The ESC menu's exit word.</summary>
    public string MenuExitWord => At(MenuExitWordDgroup);

    /// <summary>Reads the literals out of the tree's catalogue.</summary>
    /// <param name="catalog">The parsed <c>exe/strings.json</c>.</param>
    /// <returns>The strings.</returns>
    /// <exception cref="InvalidDataException">The catalogue lacks one of the <see cref="Required"/> literals.</exception>
    public static InFlightStrings Load(ExeStringCatalogDto catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Dictionary<int, string> byDgroup = new Dictionary<int, string>();
        foreach (ExeStringZoneDto zone in catalog.Zones ?? [])
        {
            foreach (ExeStringDto literal in zone.Strings ?? [])
            {
                if (literal.Dgroup is { } address && literal.Text is { } text)
                {
                    byDgroup[PortHex.Parse(address)] = text;
                }
            }
        }

        return FromLiterals(byDgroup);
    }

    /// <summary>Builds the strings from literals keyed by DGROUP address (tests, tools).</summary>
    /// <param name="byDgroup">The literals; every <see cref="Required"/> address must be present.</param>
    /// <returns>The strings.</returns>
    /// <exception cref="InvalidDataException">A required literal is missing.</exception>
    public static InFlightStrings FromLiterals(IReadOnlyDictionary<int, string> byDgroup)
    {
        ArgumentNullException.ThrowIfNull(byDgroup);
        Dictionary<int, string> chosen = new Dictionary<int, string>(Required.Count);
        foreach ((int dgroup, string role) in Required)
        {
            chosen[dgroup] = byDgroup.TryGetValue(dgroup, out string? text)
                ? text
                : throw new InvalidDataException(
                    $"exe/strings.json carries no literal at DGROUP [0x{dgroup:X4}] ({role}); the in-flight " +
                    "screen draws its words from the catalogue and never from a literal of its own");
        }

        return new InFlightStrings(chosen.ToFrozenDictionary());
    }

    private string At(int dgroup) => _byDgroup[dgroup];
}
