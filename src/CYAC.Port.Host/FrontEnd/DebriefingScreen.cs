using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Mission;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// DEBRIEFING: where a historic sortie ends in MENU MODE, with MISSION STATS as its second view
/// (<c>ui_post_mission_stats_screen @image@0x26138</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>One screen, two views — which is what the original is.</b>  The modal loop at
/// <c>image@0x2615D</c> never pushes anything: its three widgets toggle
/// <c>g_post_mission_custom_toggle [0xBC30]</c> and re-run
/// <c>stats_screen_widget_reinit @image@0x260AE</c>, which paints EITHER the debrief record
/// (<c>ui_briefing_record_paint</c>, <c>[0xBC30] == 0</c>, <c>image@0x2612E</c>) OR
/// <c>mission_stats_screen</c> (<c>[0xBC30] != 0</c>, <c>image@0x2611F</c>).  So
/// <see cref="ShowingStats"/> IS <c>[0xBC30]</c>, and <c>Debrief</c> / <c>Stats</c> are widgets 0
/// and 1, which set it to 0 and 1 (<c>image@0x26199</c> / <c>image@0x261B8</c>).  <c>Done</c> is
/// widget 2 (<c>image@0x261D0</c>) and leaves.
/// </para>
/// <para>
/// <b>The widget flag bit 5 is DISABLED, not "visible".</b> <c>stats_screen_widget_reinit</c>
/// clears bit 5 on both widgets and sets it on widget 0 when <c>[0xBC30] == 0</c> and on widget 1
/// when <c>[0xBC30]!= 0</c> (<c>image@0x260CB</c> / <c>image@0x260DE</c>) — and
/// the original's debrief view shows <b>Debrief</b> greyed while
/// its stats view shows <b>Stats</b> greyed.  The button that would take you where you already
/// are is the greyed one; the decoded file's "enable/show widget" reading of the bit is therefore
/// inverted (reported).
/// </para>
/// <para>
/// The DEBRIEF view reuses MISSION DESCRIPTION's header bar and body measure byte for byte —
/// the original's screen shows them pixel-identical above row 57 — so the two screens
/// share one set of measured constants.  Both views are verified pixel-exact against
/// a captured frame of the original and
/// <c>11_mission_stats.png</c> by and <c>FrontEndPixelTests</c>.
/// </para>
/// </remarks>
public sealed class DebriefingScreen : FrontEndScreen
{
    /// <summary>The backdrop every panel screen shares.</summary>
    public const string BackdropDocument = "images/smallv.json";

    /// <summary>The insignia strip document.</summary>
    public const string InsigniaDocument = "images/insigv.json";

    // --- the button row, measured off the original's screen (identical between debrief and stats views) ---
    private static readonly (int X, int Y, int Width, int Height) DebriefBox = (10, 170, 91, 15);
    private static readonly (int X, int Y, int Width, int Height) StatsBox = (105, 170, 91, 15);
    private static readonly (int X, int Y, int Width, int Height) DoneBox = (206, 170, 44, 15);

    /// <summary>
    /// The vertical groove between the two view buttons and <c>Done</c>: columns 200–201 in index 29
    /// and column 202 in index 21, rows 168–186 (the original's screen).
    /// </summary>
    private static readonly (int X, int Y, int Height) Groove = (200, 168, 19);
    private const int GrooveDarkIndex = 29;
    private const int GrooveLightIndex = 21;

    // --- MISSION STATS, measured off the original's screen against the decoded constants ---
    private const int StatsTitleCentre = 130;        // image@0x25E76: mov ax, 0x82
    private const int StatsTitleY = 41;              // image@0x25E79: mov dx, 0x29
    private const int StatsLineX = 18;               // image@0x25EA9: mov ax, 0x12
    private const int StatsTabStop = 145;
    private const int StatsBandX = 10;
    private const int StatsBandWidth = 240;

    /// <summary>
    /// The three grooved bands, at design rows 53, 81 and 133.
    /// </summary>
    /// <remarks>
    /// These are the three calls to <c>image@0x25615</c> at <c>image@0x25E17</c> / <c>0x25E1F</c> /
    /// <c>0x25E27</c>, whose <c>AX</c> is <c>0x35</c>, <c>0x51</c>, <c>0x85</c> = 53, 81, 133 — the
    /// rows themselves.  The decoded C reads that <c>AX</c> as a <c>strings.bin</c> INDEX ("prints
    /// one header line"); the original's own screen shows three horizontal grooves at exactly those rows
    /// and no third text line, so the argument is a Y coordinate (reported).
    /// </remarks>
    private static readonly int[] StatsBandY = [53, 81, 133];

    private const int BandTopIndex = 22;
    private const int BandMiddleIndex = 24;
    private const int BandBottomIndex = 16;

    /// <summary>The eight stat lines' pen rows — the <c>DX</c> of each <c>cp_text_display_at_pos</c>.</summary>
    /// <remarks>
    /// <c>image@0x25EAC</c> 0x3C, <c>0x25EDA</c> 0x45, <c>0x25F04</c> 0x58, <c>0x25F39</c> 0x61,
    /// <c>0x25F63</c> 0x6F, <c>0x25F98</c> 0x78, <c>0x26071</c> 0x8C, <c>0x260A0</c> 0x95.
    /// </remarks>
    private static readonly int[] StatsLineY = [60, 69, 88, 97, 111, 120, 140, 149];

    /// <summary>Index of the <c>Debrief</c> button in the ring.</summary>
    public const int DebriefButtonIndex = 0;

    /// <summary>Index of <c>Stats</c>.</summary>
    public const int StatsButtonIndex = 1;

    /// <summary>Index of <c>Done</c> — where the ring starts (the original's screen marks it).</summary>
    public const int DoneButtonIndex = 2;

    private readonly IndexedImage _backdrop;
    private readonly IndexedImage _insignia;
    private readonly MissionEntry? _entry;
    private readonly bool _custom;
    private readonly SortieOutcome _outcome;
    private readonly string _body;
    private readonly string _statsTitle;
    private readonly string[] _statsLines;

    /// <summary>Builds the screen for a sortie that has just ended.</summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="outcome">What the sortie came to.</param>
    public DebriefingScreen(DataTree tree, FrontEndStrings strings, SortieOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(outcome);
        _backdrop = tree.ImagePixels(BackdropDocument);
        _insignia = tree.ImagePixels(InsigniaDocument);
        _outcome = outcome;
        _custom = outcome.Choice.IsCustom;
        _entry = _custom ? null : Resolve(tree.Scenarios, outcome);
        _body = Body(outcome);

        // A CUSTOM sortie's header is strings.json 88 "CUSTOM MISSION STATS", copied whole rather
        // than formatted: image@0x25E3A tests [0xEE04] and takes the 0x58 arm, which has no %s
        // (image@0x25E41..0x25E4E).
        _statsTitle = _custom
            ? strings[FrontEndStrings.CustomMissionStats]
            : MissionStatsLines.Title(strings, _entry?.Title ?? outcome.MissionKey);
        _statsLines = MissionStatsLines.Of(strings, outcome.Stats);
        ShowingStats = _custom;

        Add(new FrontEndButton(
            FrontEndCommand.ShowDebrief, DebriefBox.X, DebriefBox.Y, DebriefBox.Width,
            DebriefBox.Height, strings[FrontEndStrings.Debrief], FrontEndButtonStyle.Command));
        Add(new FrontEndButton(
            FrontEndCommand.ShowStats, StatsBox.X, StatsBox.Y, StatsBox.Width, StatsBox.Height,
            strings[FrontEndStrings.Stats], FrontEndButtonStyle.Command) { Letter = 'S' });
        Add(new FrontEndButton(
            FrontEndCommand.DoneDebriefing, DoneBox.X, DoneBox.Y, DoneBox.Width, DoneBox.Height,
            strings[FrontEndStrings.Done], FrontEndButtonStyle.Command) { Letter = 'D' });
        UpdateViewButtons();
        FocusOn(DoneButtonIndex);
    }

    /// <inheritdoc/>
    public override string Name => "DEBRIEFING";

    /// <summary>
    /// <c>g_post_mission_custom_toggle [0xBC30]</c>: false = the verdict, true = MISSION STATS.
    /// </summary>
    public bool ShowingStats { get; private set; }

    /// <summary>What the sortie came to — what both views are drawn from.</summary>
    public SortieOutcome Outcome => _outcome;

    /// <summary>The catalogue record the header bar is drawn from, or null for an unknown mission.</summary>
    public MissionEntry? Entry => _entry;

    /// <summary>The MISSION STATS title line, for the tests.</summary>
    public string StatsTitle => _statsTitle;

    /// <summary>Its eight stat lines, for the tests.</summary>
    public IReadOnlyList<string> StatsLines => _statsLines;

    /// <summary>The verdict text, cleaned of the module's own control bytes.</summary>
    public string BodyText => _body;

    /// <summary>
    /// <c>D</c> is <c>Done</c>, and <c>Debrief</c> has no letter at all.
    /// </summary>
    /// <remarks>
    /// The two share a first letter, and the widget toolkit's shortcut field is a single byte per
    /// widget scanned in list order (<c>widget-ui-synthesis.md</c> §2, <c>s_ui_widget +0x10</c>), so
    /// the FIRST match wins — which would be <c>Debrief</c>, the button that is greyed in the very
    /// view it is offered from.  Rather than bind a letter to a refusal, F3 gives <c>D</c> to
    /// <c>Done</c> (the focused, always-live button that the original's screen marks) and leaves
    /// <c>Debrief</c> letterless, the way F1 left <c>Credits</c> letterless for exactly the same
    /// clash.  Esc is <c>Done</c> as well.  <c>(open)</c> — the original's own answer has not been
    /// observed.
    /// </remarks>
    protected override FrontEndCommand Back() => FrontEndCommand.DoneDebriefing;

    /// <summary>Widget 0: back to the verdict (<c>mov [0xBC30],0</c> @<c>image@0x261AA</c>).</summary>
    public void ShowVerdict()
    {
        if (_custom)
        {
            return;   // the button is greyed; nothing can reach the verdict of a custom sortie
        }

        ShowingStats = false;
        UpdateViewButtons();
    }

    /// <summary>
    /// Whether this is a CUSTOM sortie's debriefing: the STATS view only, with both view
    /// buttons stippled and <c>CUSTOM MISSION STATS</c> as its header.
    /// </summary>
    public bool IsCustom => _custom;

    /// <summary>Widget 1: MISSION STATS (<c>mov [0xBC30],1</c> @<c>image@0x261C9</c>).</summary>
    public void ShowStatistics()
    {
        ShowingStats = true;
        UpdateViewButtons();
    }

    /// <inheritdoc/>
    public override void Render(in FrontEndPainter.Surface surface, FrontEndFonts fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        FrontEndPainter.Backdrop(surface, _backdrop);
        if (ShowingStats)
        {
            RenderStats(surface, fonts);
        }
        else
        {
            RenderVerdict(surface, fonts);
        }

        surface.Fill(Groove.X, Groove.Y, 2, Groove.Height, GrooveDarkIndex);
        surface.Fill(Groove.X + 2, Groove.Y, 1, Groove.Height, GrooveLightIndex);
        for (int i = 0; i < Buttons.Count; i++)
        {
            Buttons[i].Render(surface, fonts.Bold, i == Focus);
        }
    }

    /// <summary>
    /// Cleans a mission module's text the way the original's own renderer reads it.
    /// </summary>
    /// <param name="text">The raw module string.</param>
    /// <remarks>
    /// A module string carries <c>0x01</c> as a paragraph break and <c>0x0C</c> as a double quote,
    /// and terminates with a NUL — the rule <c>FlightRasterizer.Wrap</c> already states for the H25
    /// overlay, and the one that makes <c>data/missions/abb.json</c>'s <c>d1</c> reproduce
    /// the original's own screen's two paragraphs exactly.
    /// </remarks>
    public static string CleanModuleText(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace('\u0001', '\n').Replace("\0", string.Empty).Replace('\u000C', '"');

    private static string Body(SortieOutcome outcome)
    {
        string text = CleanModuleText(outcome.Text);
        string advice = CleanModuleText(outcome.Advice);

        // The death arm is a line plus (usually) a piece of advice; the survivor arm is the module's
        // own verdict alone — image@0x25BDB..0x25C1B vs image@0x25C71 (H25's MissionDebrief).
        return advice.Length == 0 ? text : $"{text}\n\n{advice}";
    }

    private static MissionEntry? Resolve(MissionCatalog catalog, SortieOutcome outcome)
    {
        if (catalog.ByModuleAsset(outcome.MissionKey) is { } byAsset)
        {
            return byAsset;
        }

        foreach (MissionEntry entry in catalog.Entries)
        {
            if (entry.Slot == outcome.Choice.Slot)
            {
                return entry;
            }
        }

        return null;
    }

    private void UpdateViewButtons()
    {
        // stats_screen_widget_reinit @image@0x260AE greys a view button when
        //   `[0xEE04] != 0 || <that view is the one showing>`: the two JNEs at image@0x260C2 and
        //   image@0x260D5 jump straight TO the `or …,0x20`, so a CUSTOM sortie has BOTH stippled
        //   and only Done live — which is what
        //   a captured frame of the original shows.
        if (_custom)
        {
            const string why =
                "a custom mission has no verdict to show; image@0x260C2 / image@0x260D5 grey both "
                    + "view buttons when [0xEE04] is set";
            Buttons[DebriefButtonIndex].Enabled = false;
            Buttons[DebriefButtonIndex].DisabledReason = why;
            Buttons[StatsButtonIndex].Enabled = false;
            Buttons[StatsButtonIndex].DisabledReason = why;
            return;
        }

        // stats_screen_widget_reinit @image@0x260AE: bit 5 on widget 0 when [0xBC30] == 0, on
        // widget 1 when it is set — the button for the view you are already looking at is greyed.
        Buttons[DebriefButtonIndex].Enabled = ShowingStats;
        Buttons[DebriefButtonIndex].DisabledReason = ShowingStats
            ? string.Empty
            : "the verdict is already showing (image@0x260CB sets its disabled bit)";
        Buttons[StatsButtonIndex].Enabled = !ShowingStats;
        Buttons[StatsButtonIndex].DisabledReason = ShowingStats
            ? "MISSION STATS is already showing (image@0x260DE sets its disabled bit)"
            : string.Empty;
    }

    private void RenderVerdict(in FrontEndPainter.Surface surface, FrontEndFonts fonts)
    {
        if (_entry is { } entry)
        {
            MissionDescriptionScreen.RenderHeaderBar(surface, fonts, _insignia, entry);
        }

        IReadOnlyList<string> lines = FrontEndText.WrapParagraphs(
            fonts.Prop, _body, MissionDescriptionScreen.BodyWrapWidth);
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length > 0)
            {
                FrontEndPainter.Text(
                    surface,
                    fonts.Prop,
                    lines[i],
                    MissionDescriptionScreen.BodyX,
                    MissionDescriptionScreen.BodyY + (i * MissionDescriptionScreen.BodyLineStep),
                    MissionDescriptionScreen.BodyInk,
                    MissionDescriptionScreen.BodyEmboss);
            }
        }
    }

    private void RenderStats(in FrontEndPainter.Surface surface, FrontEndFonts fonts)
    {
        int titleX = StatsTitleCentre - (FrontEndPainter.Measure(fonts.Bold, _statsTitle) / 2);
        FrontEndPainter.Rule(
            surface, fonts.Bold, _statsTitle, titleX, StatsTitleY,
            FrontEndPainter.TitleInkIndex, FrontEndPainter.TitleEmbossIndex);

        foreach (int y in StatsBandY)
        {
            surface.Fill(StatsBandX, y, StatsBandWidth, 1, BandTopIndex);
            surface.Fill(StatsBandX + 1, y + 1, StatsBandWidth, 1, BandMiddleIndex);
            surface.Fill(StatsBandX, y + 2, StatsBandWidth, 1, BandBottomIndex);
        }

        for (int i = 0; i < _statsLines.Length && i < StatsLineY.Length; i++)
        {
            FrontEndPainter.Text(
                surface,
                fonts.Prop,
                _statsLines[i],
                StatsLineX,
                StatsLineY[i],
                MissionDescriptionScreen.BodyInk,
                MissionDescriptionScreen.BodyEmboss,
                stippled: false,
                tabStop: StatsTabStop);
        }
    }
}
