using System.Diagnostics;
using System.Globalization;
using System.Text;
using CYAC.Port.Core.Model.Cockpit;
using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Sim;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Effects;
using CYAC.Port.Core.Sim.Combat.Lifecycle;
using CYAC.Port.Core.Sim.Combat.Player;
using CYAC.Port.Core.Sim.Flight;
using CYAC.Port.Core.Sim.Session;
using CYAC.Port.Host.Configuration;
using CYAC.Port.Host.Headless;
using CYAC.Port.Host.Input;
using CYAC.Port.Host.Menu;
using CYAC.Port.Host.Sound;
using CYAC.Port.Host.Settings;
using CYAC.Port.Host.Sim;
using CYAC.Port.Host.Stats;
using CYAC.Port.Render;
using CYAC.Port.Render.Cockpit;
using CYAC.Port.Render.Ground;
using CYAC.Port.Render.Map;
using mode13hx;
using mode13hx.Controls;
using mode13hx.Presentation;
using mode13hx.Util;

namespace CYAC.Port.Host;

/// <summary>
/// The end-of-mission DEBRIEF and fate banner, the sortie statistics they read, and the
/// small text helpers both use.
/// </summary>
/// <remarks>The reader's map of every one of this class's files is at the top of
/// <c>FlightRasterizer.cs</c>.</remarks>
public sealed partial class FlightRasterizer
{
    /// <summary>The sortie recorder, once <see cref="AttachStats"/> has given it one.</summary>
    /// <remarks>
    /// It is the ONLY thing in the host that writes <c>stats.json</c>, and it is read-only towards
    /// the simulation: everything it records is a number the integer kernels already publish.
    /// </remarks>
    public SortieRecorder? Stats { get; private set; }

    /// <summary>Gives the rasterizer the sortie recorder that counts its flights.</summary>
    /// <param name="recorder">The recorder, already told about the first sortie.</param>
    public void AttachStats(SortieRecorder recorder) => Stats = recorder;

    /// <summary>The key of the mission being flown, for the stats panel's own highlight.</summary>
    public string? CurrentMissionKey => Stats?.Identity(_session).Key;

    /// <summary>
    /// The centred banner that names how the sortie ended, and the "fly again" hint.
    /// </summary>
    /// <param name="canvas">The frame's canvas.</param>
    /// <param name="width">The target's width.</param>
    /// <param name="height">Its height.</param>
    /// <remarks>
    /// The original has no such banner in flight — it cuts to the DEBRIEF within four frames and
    /// puts the wording there (<c>damage_state_blurb_renderer @image@0x10060</c>: "Augured in" when
    /// <c>[0xC32F] != 0</c> or <c>[0xC316] &gt; 0</c>).  The PoC has no debrief screen, so this is
    /// the deviation's own readout, and it says only what the fate machine latched.
    /// </remarks>
    private void DrawFateBanner(Canvas canvas, int width, int height)
    {
        if (_session.Fate is not { Enabled: true, Passive: false } fate
            || fate.Phase == PlayerFatePhase.Flying)
        {
            return;
        }

        string banner = fate.Banner;
        // A plain hyphen, not an em dash: mode-13hx's Font9X16 has no glyph for U+2014 and renders it
        // as a pilcrow.  Neither restart accelerator is mapped and R is the radar switch; the ONE
        // door is the ESC menu's Restart Mission.
        string hint = fate.ShowHint ? "ESC - menu" : string.Empty;
        int y = (height / 2) - 40;
        DrawCentred(canvas, banner, width, y, Func.EncodePixelColor(255, 235, 120));
        if (hint.Length > 0)
        {
            DrawCentred(canvas, hint, width, y + 30, Func.EncodePixelColor(220, 220, 220));
        }
    }

    /// <summary>
    /// The DEBRIEF overlay: the screen the original cuts to when the sortie is over.
    /// </summary>
    /// <param name="canvas">The frame's canvas.</param>
    /// <param name="width">The target's width.</param>
    /// <param name="height">Its height.</param>
    /// <remarks>
    /// <para>
    /// The content is the original's: <c>ui_post_death_message @image@0x25BAF</c>'s two arms — the
    /// module's own <c>get_debrief_text</c> for a survivor, the <c>strings.bin</c> "Augured in"
    /// family plus a piece of advice for a death — and the post-mission mode <c>[0xBC31]</c> that
    /// <c>ui_post_mission_stats_screen</c> keys its Yeager voice line from.
    /// </para>
    /// <para>
    /// <b>DIRECT MODE ONLY.</b> the placeholder is retired: in menu mode there is no overlay at all
    /// and the shell cuts to <see cref="FrontEnd.DebriefingScreen"/>, which is what the original
    /// does.  This body now runs only when <see cref="MenuMode"/> is false, i.e. under <c>fly
    /// --mission N</c> / <c>--test-flight X</c> / <c>--seed-trace</c>, where it is exactly what H25
    /// left.
    /// </para>
    /// <para>
    /// <b>What the original's screen has that this does not.</b> It is a full-screen art page, not an
    /// overlay: <c>mission_stats_screen @image@0x25E00</c> paints a background, the mission title and
    /// date from the scenario record, an insignia, three widgets, and it PLAYS a speech clip (audio
    /// index 4/5/6, the port has no speech voice yet).  Closed that: the original's own
    /// page is Debrief / Stats / Done over the mission's header bar, and the port draws it — the
    /// three widgets are the two VIEWS plus Done, not "replay custom / replay standard / continue";
    /// see <see cref="FrontEnd.DebriefingScreen"/>.
    /// </para>
    /// </remarks>
    private void DrawDebrief(Canvas canvas, int width, int height)
    {
        // In MENU mode there is no overlay at all: the shell tears the flight down on this very
        // frame and pushes DEBRIEFING.  In DIRECT mode nothing changed.
        if (MenuMode || _session.Outcome?.Debrief is not { } debrief)
        {
            return;
        }

        uint heading = debrief.Accomplished
            ? Func.EncodePixelColor(140, 255, 150)
            : Func.EncodePixelColor(255, 190, 120);
        uint body = Func.EncodePixelColor(230, 230, 230);
        int columns = Math.Max(24, (width - 160) / 9);
        List<(string Text, uint Colour)> lines = new List<(string Text, uint Colour)>
        {
            (debrief.Accomplished ? "MISSION ACCOMPLISHED" : "MISSION NOT ACCOMPLISHED", heading),
            (string.Empty, body),
        };

        foreach (string line in Wrap(debrief.Text, columns))
        {
            lines.Add((line, body));
        }

        foreach (string line in Wrap(debrief.Advice, columns))
        {
            lines.Add((line, body));
        }

        if (_session.Mission is { } mission)
        {
            HitCensus hits = mission.Hits;
            lines.Add((string.Empty, body));
            lines.Add((
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"kills {hits.Kills}   rounds fired {hits.RoundsFired:N0}   on target "
                        + $"{hits.RoundsOnTarget:N0}   hull {hits.PlayerHullA}/{hits.PlayerHullB}"),
                body));
            string landed = _session.Outcome!.EndedByLanding ? "   landed" : string.Empty;
            lines.Add((
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"post-mission mode [0xBC31] = {debrief.PostMissionMode}   sortie "
                        + $"{_session.Outcome.EndedAtSeconds:F0} s{landed}"),
                Func.EncodePixelColor(160, 160, 160)));
        }

        // The MISSION STATISTICS, under the tallies: what this mission has come to over every
        // sortie, and the totals under it in a dimmer colour.  The sortie that has just ended is
        // already in them — the recorder counts it inside the same frame's step, above.
        if (Stats is { } recorder)
        {
            MissionStatsRecord record = recorder.Store.Record(recorder.CurrentMission);
            string mine = StatsSummary(record);
            if (mine.Length > 0)
            {
                lines.Add((string.Empty, body));
                lines.Add(($"this mission: {mine}", Func.EncodePixelColor(200, 210, 235)));
                string all = StatsSummary(recorder.Store.Totals);
                if (all.Length > 0)
                {
                    lines.Add(($"all missions: {all}", Func.EncodePixelColor(150, 155, 170)));
                }
            }
        }

        lines.Add((string.Empty, body));

        // The overlay is direct-mode only now, so the line is exactly the one H25/M0 wrote and
        // nothing branches on MenuMode. R is the radar switch now; the ESC menu's Restart Mission
        // is the one restart, and it is reachable from here too.
        lines.Add(("ESC - menu", Func.EncodePixelColor(220, 220, 220)));

        int y = Math.Max(24, (height / 2) - (lines.Count * 20 / 2));

        // A PANEL behind the text.  The original replaces the whole screen with an art page, so a
        // debrief that is legible over whatever the last frame happened to be is the port's own
        // (D5); an opaque plate is the cheapest thing that always reads.
        int longest = 0;
        foreach ((string text, _) in lines)
        {
            longest = Math.Max(longest, text.Length);
        }

        int panelWidth = Math.Min(width - 16, (longest * 9) + 48);
        int panelHeight = (lines.Count * 20) + 24;
        FillPanel(
            canvas,
            (width - panelWidth) / 2,
            y - 20,
            panelWidth,
            panelHeight,
            Func.EncodePixelColor(12, 16, 28));

        foreach ((string text, uint colour) in lines)
        {
            if (text.Length > 0)
            {
                DrawCentred(canvas, text, width, y, colour);
            }

            y += 20;
        }
    }

    /// <summary>
    /// One statistics record as the debrief's own line: only what is non-zero, so a first sortie
    /// says "1 play - 1 accomplished" and nothing else.
    /// </summary>
    /// <param name="record">The mission's record, or the totals.</param>
    private static string StatsSummary(MissionStatsRecord record)
    {
        List<string> parts = new List<string>(6);
        if (record.Plays > 0)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture, $"{record.Plays} play{(record.Plays == 1 ? string.Empty : "s")}"));
        }

        if (record.Completions > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{record.Completions} accomplished"));
        }

        if (record.Kills > 0)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture, $"{record.Kills} kill{(record.Kills == 1 ? string.Empty : "s")}"));
        }

        if (record.Deaths > 0)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture, $"{record.Deaths} death{(record.Deaths == 1 ? string.Empty : "s")}"));
        }

        if (record.Landings > 0)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{record.Landings} landing{(record.Landings == 1 ? string.Empty : "s")}"));
        }

        if (record.SecondsPlayed > 0)
        {
            parts.Add($"{MissionStatsRecord.Clock(record.SecondsPlayed)} flown");
        }

        if (record.BestSortieSeconds is { } best)
        {
            parts.Add($"best {MissionStatsRecord.Clock(best)}");
        }

        return string.Join(" - ", parts);
    }

    /// <summary>Fills a rectangle with one colour — two triangles, the canvas's own primitive.</summary>
    /// <param name="canvas">The canvas.</param>
    /// <param name="x">Left.</param>
    /// <param name="y">Top.</param>
    /// <param name="w">Width.</param>
    /// <param name="h">Height.</param>
    /// <param name="colour">The encoded fill colour.</param>
    private static void FillPanel(Canvas canvas, int x, int y, int w, int h, uint colour)
    {
        canvas.SetPenColor(colour);
        canvas.FillTriangle(x, y, x + w, y, x, y + h);
        canvas.FillTriangle(x + w, y, x + w, y + h, x, y + h);
    }

    /// <summary>Wraps a debrief paragraph to a column count, honouring the module's own breaks.</summary>
    /// <param name="text">The latin-1 text, or null.</param>
    /// <param name="columns">How many characters fit.</param>
    /// <remarks>
    /// A module string carries <c>0x01</c> as a PARAGRAPH BREAK and terminates with a NUL
    /// (<see cref="Core.Model.Mission.MissionWinRules.Briefing"/>); the mode-13hx font has a glyph
    /// for neither, so both become line structure here.
    /// </remarks>
    private static IEnumerable<string> Wrap(string? text, int columns)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        foreach (string paragraph in text.Replace('\u0001', '\n').Replace("\0", string.Empty)
                     .Replace('\u000C', '"').Split('\n'))
        {
            StringBuilder line = new System.Text.StringBuilder();
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > columns)
                {
                    yield return line.ToString();
                    line.Clear();
                }

                if (line.Length > 0)
                {
                    line.Append(' ');
                }

                line.Append(word);
            }

            if (line.Length > 0)
            {
                yield return line.ToString();
            }
        }
    }

    /// <summary>Draws one line centred, with a one-pixel drop shadow.</summary>
    /// <param name="canvas">The canvas.</param>
    /// <param name="text">The line.</param>
    /// <param name="width">The target's width.</param>
    /// <param name="y">The baseline.</param>
    /// <param name="color">The encoded pen colour.</param>
    private static void DrawCentred(Canvas canvas, string text, int width, int y, uint color)
    {
        // Canvas.Font9X16 is a fixed 9-pixel cell, so the centring is exact.
        int x = Math.Max(0, (width - (text.Length * 9)) / 2);
        canvas.SetPenColor(Func.EncodePixelColor(0, 0, 0));
        canvas.DrawString(text, x + 1, y + 1, Canvas.Font9X16);
        canvas.SetPenColor(color);
        canvas.DrawString(text, x, y, Canvas.Font9X16);
    }
}
