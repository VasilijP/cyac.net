using System.Text.RegularExpressions;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// word-wrapping for the historic-chain screens, transcribed from the verified reference drawer.
/// </summary>
/// <remarks>
/// Two shapes the engine uses: the mission-list <b>description</b> is greedily wrapped to a fixed line
/// count (the row is only tall enough for two); the mission <b>briefing</b> is wrapped paragraph by
/// paragraph (a <c>\n\n</c> becomes one blank line) and PRESERVES a paragraph's internal runs of spaces
/// — the shipped briefings contain double spaces after sentences, and the engine renders them, so a
/// naive <c>string.Split</c> that collapses them shifts every following word and fails the pixel diff.
/// Both were checked to reproduce all four MISSION DESCRIPTION references and both MISSION SELECTION
/// references with zero mismatches.
/// </remarks>
public static partial class FrontEndText
{
    [GeneratedRegex(@"\S+|\s+")]
    private static partial Regex TokenRegex();

    /// <summary>
    /// Greedy word-wrap to at most <paramref name="maxLines"/> lines of at most
    /// <paramref name="width"/> design columns (spaces collapsed — the descriptions have none to keep).
    /// </summary>
    /// <param name="font">The body font.</param>
    /// <param name="text">The text to wrap.</param>
    /// <param name="width">The maximum line width in design columns.</param>
    /// <param name="maxLines">The maximum number of lines.</param>
    /// <returns>The wrapped lines (never more than <paramref name="maxLines"/>).</returns>
    public static IReadOnlyList<string> Wrap(CockpitFont font, string text, int width, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);
        List<string> lines = new List<string>();
        string current = string.Empty;
        foreach (string word in text.Split(
                     ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length == 0 || font.Measure(candidate) <= width)
            {
                current = candidate;
            }
            else
            {
                lines.Add(current);
                current = word;
                if (lines.Count == maxLines)
                {
                    return lines;
                }
            }
        }

        if (current.Length > 0 && lines.Count < maxLines)
        {
            lines.Add(current);
        }

        return lines;
    }

    /// <summary>
    /// Wraps a multi-paragraph briefing, preserving internal spacing and inserting a blank line for
    /// each <c>\n\n</c>.
    /// </summary>
    /// <param name="font">The body font.</param>
    /// <param name="text">The briefing text (with <c>\n\n</c> paragraph breaks).</param>
    /// <param name="width">The maximum line width in design columns.</param>
    /// <returns>The lines, with <see cref="string.Empty"/> marking a blank line between paragraphs.</returns>
    public static IReadOnlyList<string> WrapParagraphs(CockpitFont font, string text, int width)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);
        List<string> lines = new List<string>();
        foreach (string paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n"))
        {
            string flat = paragraph.Replace('\n', ' ');
            string line = string.Empty;
            string pending = string.Empty;
            foreach (Match token in TokenRegex().Matches(flat))
            {
                string t = token.Value;
                if (t.Trim().Length == 0)
                {
                    if (line.Length > 0)
                    {
                        pending += t;
                    }

                    continue;
                }

                if (line.Length == 0)
                {
                    line = t;
                    pending = string.Empty;
                }
                else if (font.Measure(line + pending + t) <= width)
                {
                    line += pending + t;
                    pending = string.Empty;
                }
                else
                {
                    lines.Add(line);
                    line = t;
                    pending = string.Empty;
                }
            }

            if (line.Length > 0)
            {
                lines.Add(line);
            }

            lines.Add(string.Empty);   // blank line between paragraphs
        }

        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
