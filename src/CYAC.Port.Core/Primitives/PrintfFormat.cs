using System.Globalization;
using System.Text;

namespace CYAC.Port.Core.Primitives;

/// <summary>
/// Fills a C <c>printf</c>-style format string, the way the original's shipped format literals are
/// written.
/// </summary>
/// <remarks>
/// <para>
/// The shipped literals carry C conversions (<c>%s</c>, <c>%d</c>, <c>%6ld</c>), not .NET <c>{0}</c>
/// placeholders, so <see cref="string.Format(string, object?[])"/> would print them verbatim.  This
/// substitutes the arguments into the conversions left to right.  Recognised: <c>%%</c>, and
/// <c>%[0][width][l](s|d|u)</c>.  The argument is printed with the invariant culture and padded on the
/// left to the minimum field width (with zeros under the <c>0</c> flag); the caller passes a value of the
/// type the conversion means, so <c>%u</c> gets an unsigned number and <c>%ld</c> a 32-bit one.  Any
/// other <c>%</c> is copied through, so an unexpected conversion never eats an argument.
/// </para>
/// <para>
/// Moved here from the front end's <c>FrontEndStrings.FormatPrintf</c> in P4-R2 so the cockpit and the
/// HUD can fill their own shipped formats; the <c>l</c> length flag is new (no front-end literal
/// carries it, so the front end's output is unchanged).
/// </para>
/// </remarks>
public static class PrintfFormat
{
    /// <summary>Fills a printf-style literal.</summary>
    /// <param name="format">The literal.</param>
    /// <param name="args">The arguments, in order.</param>
    /// <returns>The filled string.</returns>
    public static string Format(string format, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(args);
        StringBuilder result = new StringBuilder(format.Length + 16);
        int next = 0;
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] == '%' && i + 1 < format.Length && format[i + 1] == '%')
            {
                // C's own escape: "%d%%" prints "43%".
                result.Append('%');
                i++;
            }
            else if (format[i] == '%' && Conversion(format, i, out int end, out int width, out bool zero)
                && next < args.Length)
            {
                // The MINIMUM FIELD WIDTH lines numbers up in a monospace font, and the ZERO flag pads
                // with zeros ("%02d" prints 7 as "07").
                string text = Convert.ToString(args[next++], CultureInfo.InvariantCulture) ?? string.Empty;
                result.Append(width > text.Length
                    ? (zero ? text.PadLeft(width, '0') : text.PadLeft(width))
                    : text);
                i = end;
            }
            else
            {
                result.Append(format[i]);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Whether a <c>%</c> at <paramref name="at"/> starts a conversion this fills, and how wide.
    /// </summary>
    /// <param name="format">The literal.</param>
    /// <param name="at">The index of the <c>%</c>.</param>
    /// <param name="end">The index of the conversion letter, when there is one.</param>
    /// <param name="width">The minimum field width, or 0.</param>
    /// <param name="zero">Whether the width carries C's <c>0</c> flag.</param>
    private static bool Conversion(string format, int at, out int end, out int width, out bool zero)
    {
        end = at;
        width = 0;
        int i = at + 1;
        zero = i < format.Length && format[i] == '0';
        if (zero)
        {
            i++;
        }

        while (i < format.Length && char.IsAsciiDigit(format[i]))
        {
            width = (width * 10) + (format[i] - '0');
            i++;
        }

        if (i < format.Length && format[i] == 'l')
        {
            i++;
        }

        if (i >= format.Length || format[i] is not ('s' or 'd' or 'u'))
        {
            width = 0;
            zero = false;
            return false;
        }

        end = i;
        return true;
    }
}
