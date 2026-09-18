using System.Globalization;
using System.Text;
using CommandLine;
using CommandLine.Text;

namespace CYAC.Port.Host.Configuration;

/// <summary>
/// The port's own <c>--help</c> screen, grouped.
/// </summary>
/// <remarks>
/// <para>
/// CommandLineParser's automatic screen printed all <see cref="OptionGroups.Options"/> of them in
/// one 1,247-line list, which tells a player nothing about which of them they need.  This one
/// prints the four PLAYER groups — around thirty options — and says that <c>--help-all</c> has the
/// rest.  Nothing is hidden: <c>--help-all</c> prints every group, and every option keeps the help
/// text it declares.
/// </para>
/// <para>
/// The layout is the library's own, so a reader who knew the old screen finds the same shape: the
/// heading and copyright lines it builds, two spaces, the option's spec in a 28-column gutter, its
/// default in parentheses, and the text wrapped at eighty columns.
/// </para>
/// </remarks>
public static class FlyHelp
{
    /// <summary>The column the help text of an option starts in.</summary>
    private const int TextColumn = 30;

    /// <summary>The column a line is wrapped at.</summary>
    private const int Width = 80;

    /// <summary>Whether the command line asks for the help screen.</summary>
    /// <param name="args">The raw command line.</param>
    /// <param name="everything">Set when it was <c>--help-all</c>, which prints every group.</param>
    /// <returns>True when the caller should print the help screen and exit 0.</returns>
    public static bool Requested(IReadOnlyList<string>? args, out bool everything)
    {
        everything = false;
        bool asked = false;
        foreach (string argument in args ?? [])
        {
            if (string.Equals(argument, "--help-all", StringComparison.Ordinal))
            {
                everything = true;
                asked = true;
            }
            else if (string.Equals(argument, "--help", StringComparison.Ordinal))
            {
                asked = true;
            }
        }

        return asked;
    }

    /// <summary>The help screen.</summary>
    /// <param name="everything">True for <c>--help-all</c>: every group, not just the player's.</param>
    /// <returns>The screen, newline-terminated, ready to write.</returns>
    public static string Render(bool everything)
    {
        StringBuilder text = new StringBuilder();
        text.AppendLine(HeadingInfo.Default.ToString());
        text.AppendLine(CopyrightInfo.Default.ToString());

        IReadOnlyList<OptionGroup> groups = everything ? OptionGroups.All : OptionGroups.Player;
        foreach (OptionGroup group in groups)
        {
            Space(text);
            text.AppendLine(OptionGroups.Title(group));
            foreach (string line in Wrap(OptionGroups.Blurb(group), 2, Width))
            {
                text.AppendLine(line);
            }

            text.AppendLine();
            foreach (string name in OptionGroups.Names(group))
            {
                Append(text, name, OptionGroups.Options[name]);
            }
        }

        if (!everything)
        {
            Space(text);
            int hidden = OptionGroups.Count([OptionGroup.Instruments, OptionGroup.Developer]);
            foreach (string line in Wrap(
                $"--help-all lists the {hidden} developer and instrument options.", 2, Width))
            {
                text.AppendLine(line);
            }

            text.AppendLine();
        }

        Line(text, "--help", "Display this help screen.");
        Line(text, "--help-all", "Every option, grouped.");
        Line(text, "--version", "Display version information.");
        return text.ToString();
    }

    /// <summary>One blank line, never two, before whatever is written next.</summary>
    private static void Space(StringBuilder text)
    {
        string end = Environment.NewLine + Environment.NewLine;
        if (text.Length >= end.Length
            && text.ToString(text.Length - end.Length, end.Length) == end)
        {
            return;
        }

        text.AppendLine();
    }

    private static void Append(StringBuilder text, string name, OptionAttribute option)
    {
        string spec = option.ShortName.Length > 0 ? $"-{option.ShortName}, --{name}" : $"--{name}";
        string help = option.Default is null
            ? option.HelpText
            : $"(Default: {Format(option.Default)}) {option.HelpText}";
        Line(text, spec, help);
    }

    private static void Line(StringBuilder text, string spec, string help)
    {
        List<string> body = Wrap(help, TextColumn, Width).ToList();
        string gutter = "  " + spec;
        if (body.Count == 0)
        {
            text.AppendLine(gutter);
        }
        else
        {
            text.AppendLine(gutter.PadRight(TextColumn) + body[0].TrimStart());
            for (int i = 1; i < body.Count; i++)
            {
                text.AppendLine(body[i]);
            }
        }

        text.AppendLine();
    }

    /// <summary>Greedy word wrap, every line indented to <paramref name="indent"/>.</summary>
    private static IEnumerable<string> Wrap(string? text, int indent, int width)
    {
        string pad = new(' ', indent);
        foreach (string paragraph in (text ?? string.Empty).Split('\n'))
        {
            StringBuilder line = new StringBuilder(pad);
            bool empty = true;
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!empty && line.Length + 1 + word.Length > width)
                {
                    yield return line.ToString();
                    line.Clear().Append(pad);
                    empty = true;
                }

                if (!empty)
                {
                    line.Append(' ');
                }

                line.Append(word);
                empty = false;
            }

            if (!empty)
            {
                yield return line.ToString();
            }
        }
    }

    /// <summary>A default value as the library printed it: booleans lower case, the rest invariant.</summary>
    private static string Format(object value) => value switch
    {
        bool flag => flag ? "true" : "false",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
