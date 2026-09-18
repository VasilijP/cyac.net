using System.Globalization;
using CYAC.Port.Host.Configuration;

namespace CYAC.Port.Host.Settings;

/// <summary>What SHAPE a port setting has, which decides how it is shown and stored.</summary>
public enum PortSettingKind
{
    /// <summary>On or off — one JSON boolean, one dialog row with <c>ON</c> / <c>OFF</c>.</summary>
    Toggle,

    /// <summary>One of a closed list of words — one JSON string, cycled with Left/Right.</summary>
    Choice,

    /// <summary>A number in a range — one JSON number, stepped with Left/Right (accelerating).</summary>
    Number,

    /// <summary>A palette index — a number, drawn with the colour itself as a swatch.</summary>
    Color,

    /// <summary>
    /// A bit MASK — one JSON number, but shown as one toggle row per named bit.
    /// </summary>
    /// <remarks>
    /// It exists for exactly one setting, <c>--sound-mask</c>: the System menu flips its five bits
    /// one at a time (<c>g_audio_mute_mask [0xE483]</c>) while the command line takes the whole
    /// byte.  Keeping it ONE table entry keeps the one-entry-one-command-line-twin law while
    /// the dialog still shows five switches.
    /// </remarks>
    Bits,
}

/// <summary>One setting's VALUE, in whichever of the three shapes its kind uses.</summary>
/// <param name="Flag">A <see cref="PortSettingKind.Toggle"/>'s state.</param>
/// <param name="Word">A <see cref="PortSettingKind.Choice"/>'s word.</param>
/// <param name="Number">A <see cref="PortSettingKind.Number"/>, <see cref="PortSettingKind.Color"/> or <see cref="PortSettingKind.Bits"/> value.</param>
/// <remarks>
/// Deliberately not a boxed <c>object</c>: the settings file, the command line and the dialog all
/// have to agree on what a value IS, and a struct with three named fields says which field a kind
/// reads without a cast anywhere.
/// </remarks>
public readonly record struct SettingValue(bool Flag, string Word, double Number)
{
    /// <summary>A toggle's value.</summary>
    /// <param name="on">Whether it is on.</param>
    public static SettingValue Toggle(bool on) => new(on, on ? "on" : "off", on ? 1.0 : 0.0);

    /// <summary>A choice's value.</summary>
    /// <param name="word">The chosen word.</param>
    public static SettingValue Choice(string word) => new(false, word, 0.0);

    /// <summary>A number's (or a colour's, or a mask's) value.</summary>
    /// <param name="value">The number.</param>
    public static SettingValue Of(double value) => new(
        value != 0.0,
        value.ToString("0.####", CultureInfo.InvariantCulture),
        value);
}

/// <summary>
/// ONE port setting: its name, its shape, its default, its help text, and the three places it has to
/// reach — the command line, the settings file and the LIVE presentation state.
/// </summary>
/// <remarks>
/// <para>
/// This record is the single source of truth the M2 brief asks for ("ONE table driving both the
/// command-line options and the dialog rows").  <see cref="PortSettings.All"/> is the table; the
/// dialog's rows, <c>settings.json</c>'s entries and the reflection check that every row really has a
/// command-line twin all read it, so the three can never drift.
/// </para>
/// <para>
/// <b>The command-line twin.</b>  <see cref="Name"/> is the option's own long name — <c>alpha</c> is
/// <c>--alpha</c> — and <see cref="PortSettings.CommandLineTwins"/> asserts by reflection that
/// <see cref="FlyOptions"/> carries an <c>[Option]</c> of that name whose <c>Default</c> is
/// <see cref="Default"/>.  The port therefore cannot grow a settings row without the flag, or change
/// one default without the other.
/// </para>
/// </remarks>
public sealed record PortSetting
{
    /// <summary>The option's long name — <c>alpha</c> for <c>--alpha</c>, and the JSON key.</summary>
    public required string Name { get; init; }

    /// <summary>Which section of the dialog the row sits in (<see cref="PortSettings.Sections"/>).</summary>
    public required string Section { get; init; }

    /// <summary>The row's label, in the dialog's own words.</summary>
    public required string Label { get; init; }

    /// <summary>The setting's shape.</summary>
    public required PortSettingKind Kind { get; init; }

    /// <summary>What the row's footer says, and what the file's <c>$comment</c> repeats.</summary>
    public required string Help { get; init; }

    /// <summary>The built-in default — what <c>R</c> resets the row to.</summary>
    public required SettingValue Default { get; init; }

    /// <summary>A <see cref="PortSettingKind.Choice"/>'s words, in cycle order; empty otherwise.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>A <see cref="PortSettingKind.Bits"/>' bit names, low bit first; empty otherwise.</summary>
    public IReadOnlyList<string> Bits { get; init; } = [];

    /// <summary>A number's smallest value.</summary>
    public double Minimum { get; init; }

    /// <summary>A number's largest value.</summary>
    public double Maximum { get; init; } = 1.0;

    /// <summary>How far one Left/Right press moves a number (before acceleration).</summary>
    public double Step { get; init; } = 1.0;

    /// <summary>How many decimals the row prints.</summary>
    public int Decimals { get; init; }

    /// <summary>The unit printed after a number, or the empty string.</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>Reads the setting out of the parsed command line.</summary>
    public required Func<FlyOptions, SettingValue> Read { get; init; }

    /// <summary>
    /// A row the PORT dialog does not list, because the game's own menu carries it (the four
    /// overlay windows live under Help, as in the original).  It is still a setting: persisted,
    /// twinned with its option, applied on attach.
    /// </summary>
    public bool Hidden { get; init; }

    /// <summary>
    /// A row the game reads when it STARTS, so it is neither live nor "takes effect on the next
    /// sortie": nothing in a flying sortie depends on it, and a change is in force the next time the
    /// game is launched.
    /// </summary>
    /// <remarks>
    /// <c>startup-check</c> is the first of these — the pre-flight dashboard's show policy, read once
    /// by the boot screen before the game exists.  The dialog says so in its footer instead of
    /// promising the next sortie, and <see cref="IsLive"/> stays false, which is the truth.
    /// </remarks>
    public bool ReadAtStartup { get; init; }

    /// <summary>Writes the setting back into the options the host builds everything from.</summary>
    public required Action<FlyOptions, SettingValue> Write { get; init; }

    /// <summary>
    /// Pushes the setting into the LIVE presentation state, or null when it cannot be changed
    /// mid-sortie (see <see cref="IsLive"/>).
    /// </summary>
    public Action<FlightRasterizer, SettingValue>? Apply { get; init; }

    /// <summary>
    /// True for a setting the FRONT END reads afresh every frame, which is live without having
    /// anything to push into a rasterizer.
    /// </summary>
    /// <remarks>
    /// <c>frontend-scale</c> is the first of these: <c>HostShell.Scale</c> asks the store for it on
    /// every presented menu frame, so the dialog's row moves the menu at once — and a sortie in the
    /// air is not affected by it at all, so there is nothing for <see cref="Apply"/> to do.  The
    /// flag exists so <see cref="IsLive"/> keeps meaning "takes effect at once" rather than
    /// "reaches the rasterizer", which is the property the M2b test asserts.
    /// </remarks>
    public bool LiveWithoutApply { get; init; }

    /// <summary>Whether the setting takes effect at once, without restarting the sortie.</summary>
    public bool IsLive => Apply is not null || LiveWithoutApply;

    /// <summary>Puts a value into the setting's own range — clamped, snapped, and word-checked.</summary>
    /// <param name="value">Any value.</param>
    /// <returns>The value this setting would actually hold.</returns>
    public SettingValue Normalise(SettingValue value) => Kind switch
    {
        PortSettingKind.Toggle => SettingValue.Toggle(value.Flag),
        PortSettingKind.Choice => SettingValue.Choice(
            Choices.FirstOrDefault(
                c => string.Equals(c, value.Word?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? Default.Word),
        PortSettingKind.Color or PortSettingKind.Bits => SettingValue.Of(
            Math.Clamp(Math.Round(value.Number), Minimum, Maximum)),
        _ => SettingValue.Of(Math.Clamp(
            Math.Round(value.Number, Math.Max(Decimals, 0)), Minimum, Maximum)),
    };

    /// <summary>Whether two values of THIS setting are the same value.</summary>
    /// <param name="a">One value.</param>
    /// <param name="b">The other.</param>
    public bool Same(SettingValue a, SettingValue b) => Kind switch
    {
        PortSettingKind.Toggle => a.Flag == b.Flag,
        PortSettingKind.Choice => string.Equals(a.Word, b.Word, StringComparison.OrdinalIgnoreCase),
        _ => Math.Abs(a.Number - b.Number) < 1e-9,
    };

    /// <summary>How the value reads on the row (and in the readout).</summary>
    /// <param name="value">The value.</param>
    public string Format(SettingValue value) => Kind switch
    {
        PortSettingKind.Toggle => value.Flag ? "ON" : "OFF",
        PortSettingKind.Choice => value.Word,
        PortSettingKind.Bits => "0x" + ((int)value.Number).ToString("X2", CultureInfo.InvariantCulture),
        PortSettingKind.Color => ((int)value.Number).ToString(CultureInfo.InvariantCulture),
        _ => value.Number.ToString(
                "0." + new string('0', Math.Max(Decimals, 0)), CultureInfo.InvariantCulture)
            + (Unit.Length > 0 ? " " + Unit : string.Empty),
    };

    /// <summary>The next value one Left/Right press produces.</summary>
    /// <param name="value">The current value.</param>
    /// <param name="direction">−1 for Left, +1 for Right.</param>
    /// <param name="acceleration">
    /// How many host frames the key has been held; a number row multiplies its step by
    /// <c>1 + held / 12</c>, capped at ten, so a slow tap is exact and a hold sweeps the range.
    /// </param>
    public SettingValue Advance(SettingValue value, int direction, int acceleration)
    {
        if (direction == 0)
        {
            return value;
        }

        switch (Kind)
        {
            case PortSettingKind.Toggle:
                return SettingValue.Toggle(!value.Flag);
            case PortSettingKind.Choice:
            {
                int index = 0;
                for (int i = 0; i < Choices.Count; i++)
                {
                    if (string.Equals(Choices[i], value.Word, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }

                index = ((index + direction) % Choices.Count + Choices.Count) % Choices.Count;
                return SettingValue.Choice(Choices[index]);
            }

            default:
            {
                double step = Step * Math.Min(10.0, 1.0 + (Math.Max(0, acceleration) / 12.0));
                if (Kind is PortSettingKind.Color or PortSettingKind.Bits)
                {
                    step = Math.Max(1.0, Math.Round(step));
                }

                return Normalise(SettingValue.Of(value.Number + (direction * step)));
            }
        }
    }
}
