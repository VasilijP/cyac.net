using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Mission;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// MISSION DESCRIPTION: the briefing and the launch buttons for one historic mission
/// (<c>scenario_briefing_show @image@0x25239</c>).
/// </summary>
/// <remarks>
/// <para>
/// Over <c>smallv</c>.  The title bar carries the national insignia, the player aircraft's short name
/// (<c>MissionEntry.PlayerAircraftName</c>, small font), the mission title (bold, UPPER-cased, centred, with
/// a rule) and the date (small, right).  The body is the module's briefing text
/// (<c>module.briefing.text</c>, regular font, word-wrapped preserving its internal spacing).  The bottom
/// row is <c>Diff: %s</c> (a button that cycles Easy→Normal→Hard→Expert in place), <c>Tactics</c> (greyed
/// until F5), <c>Exit</c> (back to the list) and <c>Ok</c> (start the sortie).
/// </para>
/// <para>
/// <b>Diff is the player's AI difficulty</b> (<c>--difficulty</c> 0..3, <c>g_difficulty_level [0xF10E]</c>),
/// distinct from the row's rating dots on MISSION SELECTION.  It cycles in place and never moves the
/// focus.  <b>Ok</b> hands the shell a <see cref="LastSortie"/> for this mission's slot at the chosen
/// difficulty; the shell flies it through the plumbing F1 built.
/// </para>
/// </remarks>
public sealed class MissionDescriptionScreen : FrontEndScreen
{
    /// <summary>The backdrop every panel screen shares.</summary>
    public const string BackdropDocument = "images/smallv.json";

    /// <summary>The insignia strip document.</summary>
    public const string InsigniaDocument = "images/insigv.json";

    // --- measured layout (the original's screen) ---
    private static readonly (int X, int Y, int Width, int Height) InBox = (14, 38, 23, 15);
    private static readonly (int X, int Y) InsigniaAt = (16, 40);
    private static readonly (int X, int Y) AircraftAt = (42, 42);
    private const int TitleCentre = 138;
    private const int TitleY = 41;
    private const int DateRight = 245;
    private const int DateY = 42;

    /// <summary>The body text's pen column — shared with F3's DEBRIEFING (<c>10</c> == <c>05</c>).</summary>
    public const int BodyX = 16;

    /// <summary>Its first pen row.</summary>
    public const int BodyY = 57;

    /// <summary>The step between wrapped body lines.</summary>
    public const int BodyLineStep = 9;

    /// <summary>The body's wrap measure: x16..x244, the panel's interior width.</summary>
    public const int BodyWrapWidth = 228;

    /// <summary>The body's ink: teal.</summary>
    public const int BodyInk = 189;

    /// <summary>Its emboss — one step lighter than the panel fill 7.</summary>
    public const int BodyEmboss = 18;
    private const int RowFillIndex = 19;
    private const int RowEdgeLight = 15;
    private const int RowEdgeDark = 26;
    private const int RowCornerTopRight = 18;
    private const int RowCornerBottomLeft = 24;

    private static readonly (int X, int Y, int Width, int Height) DiffBox = (10, 170, 81, 15);
    private static readonly (int X, int Y, int Width, int Height) TacticsBox = (95, 170, 57, 15);
    private static readonly (int X, int Y, int Width, int Height) ExitBox = (156, 170, 44, 15);
    private static readonly (int X, int Y, int Width, int Height) OkBox = (206, 170, 44, 15);

    /// <summary>Index of the Diff button in the ring — Select on it cycles rather than fires.</summary>
    public const int DiffButtonIndex = 0;

    /// <summary>Index of the Ok button — the ring starts focused here (as the original's screen shows).</summary>
    public const int OkButtonIndex = 3;

    private readonly IndexedImage _backdrop;
    private readonly IndexedImage _insignia;
    private readonly FrontEndStrings _strings;
    private readonly MissionEntry _entry;
    private readonly string _title;
    private readonly string _briefing;
    private readonly string _diffFormat;
    private int _difficulty;

    /// <summary>Builds the screen for one mission at a starting difficulty.</summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="entry">The mission this describes.</param>
    /// <param name="difficulty">The starting <c>Diff:</c> value, 0..3 (Normal by default).</param>
    public MissionDescriptionScreen(
        DataTree tree, FrontEndStrings strings, MissionEntry entry, int difficulty)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(entry);
        _backdrop = tree.ImagePixels(BackdropDocument);
        _insignia = tree.ImagePixels(InsigniaDocument);
        _strings = strings;
        _entry = entry;
        _difficulty = Math.Clamp(difficulty, 0, 3);
        _title = entry.Title.ToUpperInvariant();
        _diffFormat = strings.AtDgroup(FrontEndStrings.DiffFormatDgroup);
        _briefing = ResolveBriefing(tree, entry);

        // The ring: Diff, Tactics (greyed), Exit, Ok.  Diff carries no command — its Select cycles.
        Add(new FrontEndButton(
            FrontEndCommand.None, DiffBox.X, DiffBox.Y, DiffBox.Width, DiffBox.Height,
            DiffLabel(), FrontEndButtonStyle.Command) { Letter = 'D' });
        // Tactics is LIVE: it opens the comparison screen (ui_plane_comparison_screen
        // @image@0x26D05, the briefing screen's widget id 1). Landed
        Add(new FrontEndButton(
            FrontEndCommand.OpenTactics, TacticsBox.X, TacticsBox.Y, TacticsBox.Width,
            TacticsBox.Height, strings.AtDgroup(FrontEndStrings.TacticsDgroup),
            FrontEndButtonStyle.Command) { Letter = 'T' });
        Add(new FrontEndButton(
            FrontEndCommand.Ok, ExitBox.X, ExitBox.Y, ExitBox.Width, ExitBox.Height,
            strings.AtDgroup(FrontEndStrings.ExitDgroup), FrontEndButtonStyle.Command) { Letter = 'X' });
        Add(new FrontEndButton(
            FrontEndCommand.StartSortie, OkBox.X, OkBox.Y, OkBox.Width, OkBox.Height,
            strings.AtDgroup(FrontEndStrings.OkDgroup), FrontEndButtonStyle.Command) { Letter = 'O' });
        FocusOn(OkButtonIndex);
    }

    /// <inheritdoc/>
    public override string Name => "MISSION DESCRIPTION";

    /// <summary>The mission's catalogue slot — what <c>Ok</c> hands the shell.</summary>
    public int Slot => _entry.Slot;

    /// <summary>The mission this describes, which <c>Tactics</c> opens its comparison for.</summary>
    public MissionEntry Entry => _entry;

    /// <summary>The chosen player difficulty, 0..3.</summary>
    public int Difficulty => _difficulty;

    /// <inheritdoc/>
    public override FrontEndCommand Press(FrontEndKey key)
    {
        // Diff cycles in place: Select on it advances the difficulty and never moves the focus.
        if (key == FrontEndKey.Select && Focus == DiffButtonIndex)
        {
            _difficulty = (_difficulty + 1) % 4;
            return FrontEndCommand.None;
        }

        return base.Press(key);
    }

    /// <summary>Esc backs up to MISSION SELECTION.</summary>
    protected override FrontEndCommand Back() => FrontEndCommand.Ok;

    /// <inheritdoc/>
    public override void Render(in FrontEndPainter.Surface surface, FrontEndFonts fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        FrontEndPainter.Backdrop(surface, _backdrop);

        // Title bar: insignia box + insignia, aircraft short name, title (upper, centred) + rule, date.
        RenderHeaderBar(surface, fonts, _insignia, _entry);

        // Briefing body.
        IReadOnlyList<string> lines = FrontEndText.WrapParagraphs(fonts.Prop, _briefing, BodyWrapWidth);
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length > 0)
            {
                FrontEndPainter.Text(
                    surface, fonts.Prop, lines[i], BodyX, BodyY + (i * BodyLineStep),
                    BodyInk, BodyEmboss);
            }
        }

        // Buttons.  The Diff caption is recomputed from the current difficulty each frame.
        for (int i = 0; i < Buttons.Count; i++)
        {
            FrontEndButton button = i == DiffButtonIndex ? Buttons[i] with { Label = DiffLabel() } : Buttons[i];
            button.Render(surface, fonts.Bold, i == Focus);
        }
    }

    private string DiffLabel() => FrontEndStrings.FormatPrintf(
        _diffFormat, _strings.AtDgroup(FrontEndStrings.DifficultyWordDgroup[_difficulty]));

    /// <summary>
    /// The MISSION DESCRIPTION header bar: the insignia box, the aircraft's short name, the
    /// upper-cased title with its rule, and the date.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="DebriefingScreen"/> because it is literally the same bar:
    /// the original's own screen is pixel-identical between the two views for every row above the body
    /// (measured — the first differing row of the two frames is 57, the body's own first pen row).
    /// </remarks>
    /// <param name="surface">The surface.</param>
    /// <param name="fonts">The three front-end fonts.</param>
    /// <param name="insignia">The <c>insigv</c> strip.</param>
    /// <param name="entry">The mission whose bar this is.</param>
    public static void RenderHeaderBar(
        in FrontEndPainter.Surface surface,
        FrontEndFonts fonts,
        in IndexedImage insignia,
        MissionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        ArgumentNullException.ThrowIfNull(entry);
        RenderInsigniaBox(surface, insignia, entry.InsigniaIndex);
        FrontEndPainter.Text(
            surface, fonts.Small, entry.PlayerAircraftName, AircraftAt.X, AircraftAt.Y,
            FrontEndPainter.TitleInkIndex, FrontEndPainter.TitleEmbossIndex);
        string title = entry.Title.ToUpperInvariant();
        int titleX = TitleCentre - (FrontEndPainter.Measure(fonts.Bold, title) / 2);
        FrontEndPainter.Rule(
            surface, fonts.Bold, title, titleX, TitleY,
            FrontEndPainter.TitleInkIndex, FrontEndPainter.TitleEmbossIndex);
        FrontEndPainter.Text(
            surface, fonts.Small, entry.Date,
            DateRight - FrontEndPainter.Measure(fonts.Small, entry.Date), DateY,
            FrontEndPainter.TitleInkIndex, FrontEndPainter.TitleEmbossIndex);
    }

    private static void RenderInsigniaBox(
        in FrontEndPainter.Surface surface, in IndexedImage insignia, int insigniaIndex)
    {
        (int x, int y, int w, int h) = InBox;
        surface.Fill(x, y, w, h, RowFillIndex);
        surface.Fill(x, y, w, 1, RowEdgeLight);
        surface.Fill(x, y, 1, h, RowEdgeLight);
        surface.Fill(x, y + h - 1, w, 1, RowEdgeDark);
        surface.Fill(x + w - 1, y, 1, h, RowEdgeDark);
        surface.Pixel(x + w - 1, y, RowCornerTopRight);
        surface.Pixel(x, y + h - 1, RowCornerBottomLeft);
        FrontEndPainter.InsigniaStrip(
            surface, insignia, insigniaIndex, MissionSelectionScreen.InsigniaStride,
            MissionSelectionScreen.InsigniaKey, InsigniaAt.X, InsigniaAt.Y);
    }

    private static string ResolveBriefing(DataTree tree, MissionEntry entry)
    {
        foreach ((string key, MissionDefinition definition) in tree.Missions)
        {
            if (string.Equals(key, entry.ModuleAssetName, StringComparison.OrdinalIgnoreCase)
                && definition.Module?.BriefingText is { } text)
            {
                return text;
            }
        }

        return string.Empty;
    }
}
