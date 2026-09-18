using CYAC.Port.Core.Data;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// CHOOSE ACTIVITY, the original's root menu (<c>ui_choose_activity_screen @image@0x25717</c>, the
/// 7-entry dispatch at <c>image@0x00C73</c>).
/// </summary>
/// <remarks>
/// <para>
/// Every rectangle below is measured off
/// a captured frame of the original and reproduced pixel for pixel
///.  The labels come from <c>data/strings.json</c> by index and the backdrop
/// from <c>data/images/largev.json</c>; nothing on this screen is a literal.
/// </para>
/// <para>
/// <b>The two greyed rows.</b> "Greyed, not hidden": the port keeps them in the ring so the player
/// can see what the original does and that this build will not do it, and they are drawn in the
/// original's own disabled rendering.
/// </para>
/// </remarks>
public sealed class ChooseActivityScreen : FrontEndScreen
{
    /// <summary>largev's own inner panel: the rectangle the screen's content lives in.</summary>
    /// <remarks>
    /// The backdrop already carries the bevelled frame AND this interior (index 20, which is the
    /// same colour as the fill index 7 — the shipped palette has six duplicate entries).  The screen
    /// fills it anyway so it is self-contained; the result is RGB-identical to the shipped art.
    /// </remarks>
    public static readonly (int X, int Y, int Width, int Height) Panel = (10, 59, 133, 106);

    /// <summary>
    /// Twice the column a screen title is centred on — the title's pen is <c>(this − width) / 2</c>.
    /// </summary>
    /// <remarks>
    /// Measured on the two largev screens: <c>CHOOSE ACTIVITY</c> (89 columns) starts at 35 and
    /// <c>03_conflict_selection</c>'s title (108 columns) at 25, so both centre on column 79.5.  That
    /// is NOT the panel's own centre (76.5), and two samples cannot say which box the engine centres
    /// in — <b>(open)</b>; the measured number is what is used.
    /// </remarks>
    public const int TitleCentreDoubled = 159;

    /// <summary>The title's pen row; its rule follows at <c>+8</c> (ink) and <c>+9</c> (emboss).</summary>
    public const int TitleY = 62;

    /// <summary>The five stacked buttons: left edge, width, height, first row, row pitch.</summary>
    public static readonly (int X, int Width, int Height, int Y, int Pitch) List = (12, 130, 15, 75, 18);

    /// <summary>The bottom row's <c>Exit to DOS</c> button.</summary>
    public static readonly (int X, int Y, int Width, int Height) ExitBox = (6, 171, 77, 15);

    /// <summary>The bottom row's <c>Credits</c> button.</summary>
    public static readonly (int X, int Y, int Width, int Height) CreditsBox = (87, 171, 61, 15);

    /// <summary>The backdrop this screen and the conflict menu share.</summary>
    public const string BackdropDocument = "images/largev.json";

    private readonly IndexedImage _backdrop;
    private readonly string _title;

    /// <summary>Builds the screen from the tree.</summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="lastSortie">What <c>settings.json</c> remembers, or null (Last Mission is greyed).</param>
    public ChooseActivityScreen(DataTree tree, FrontEndStrings strings, LastSortie? lastSortie)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(strings);
        _backdrop = tree.ImagePixels(BackdropDocument);
        _title = strings[FrontEndStrings.ChooseActivity];

        Add(Row(0, FrontEndCommand.FlyHistoricMission, strings[FrontEndStrings.FlyHistoricMission], 'F'));
        // LIVE: the Create Mission pickers (image@0x00C40 -> ui_create_mission_form @image@0x2769E)
        // are ported, and Done flies the sentence through C1's MissionColdStart.Custom..
        Add(Row(1, FrontEndCommand.CreateMission, strings[FrontEndStrings.CreateMission], 'C'));
        Add(Row(2, FrontEndCommand.TestFlight, strings[FrontEndStrings.TestFlight], 'T'));
        Add(Row(3, FrontEndCommand.LastMission, strings[FrontEndStrings.LastMission], 'L') with
        {
            Enabled = lastSortie is not null,
            DisabledReason = lastSortie is null
                ? "nothing has been flown yet - settings.json has no lastSortie"
                : string.Empty,
        });
        Add(Row(4, FrontEndCommand.ReviewFilm, strings[FrontEndStrings.ReviewFilm], 'R') with
        {
            Enabled = false,

            // image@0x00A60 -> ui_film_review_screen @image@0x32868.  The port has no
            // film system at all - FlightMenuActions says the same thing about the ? menu's two
            // film rows.
            DisabledReason =
                "ui_film_review_screen @image@0x32868 (image@0x00A60) needs the film "
                    + "(*.F) system, which the port does not have",
        });
        Add(new FrontEndButton(
            FrontEndCommand.ExitToDos,
            ExitBox.X,
            ExitBox.Y,
            ExitBox.Width,
            ExitBox.Height,
            strings[FrontEndStrings.ExitToDos],
            FrontEndButtonStyle.Command)
        {
            Letter = 'E',
        });
        Add(new FrontEndButton(
            FrontEndCommand.Credits,
            CreditsBox.X,
            CreditsBox.Y,
            CreditsBox.Width,
            CreditsBox.Height,
            strings[FrontEndStrings.Credits],
            FrontEndButtonStyle.Command)
        {
            // No letter.  `C` is Create Mission's and the manual's rule is FIRST letters, so
            // Credits has none in the original either - the atlas reached it with Tab
            // on the original's screen.  Giving it one would invent a key the original does not have.
            Letter = '\0',
        });
    }

    /// <inheritdoc/>
    public override string Name => "CHOOSE ACTIVITY";

    /// <inheritdoc/>
    public override void Render(in FrontEndPainter.Surface surface, FrontEndFonts fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        CockpitFont font = fonts.Bold;
        FrontEndPainter.Backdrop(surface, _backdrop);
        surface.Fill(Panel.X, Panel.Y, Panel.Width, Panel.Height, FrontEndPainter.FillIndex);

        int width = FrontEndPainter.Measure(font, _title);
        int x = (TitleCentreDoubled - width) / 2;
        FrontEndPainter.Text(
            surface,
            font,
            _title,
            x,
            TitleY,
            FrontEndPainter.TitleInkIndex,
            FrontEndPainter.TitleEmbossIndex);

        // The rule under the title is the title's own width, drawn the way a glyph row is: the ink
        // line with its emboss one row below (the original's screen, rows 70 and 71, x 35..123).
        surface.Fill(x, TitleY + font.Height + 1, width, 1, FrontEndPainter.TitleEmbossIndex);
        surface.Fill(x, TitleY + font.Height, width, 1, FrontEndPainter.TitleInkIndex);

        for (int i = 0; i < Buttons.Count; i++)
        {
            Buttons[i].Render(surface, font, i == Focus);
        }
    }

    private static FrontEndButton Row(int index, FrontEndCommand command, string label, char letter) =>
        new(
            command,
            List.X,
            List.Y + (index * List.Pitch),
            List.Width,
            List.Height,
            label,
            FrontEndButtonStyle.ActivityList)
        {
            Letter = letter,
        };
}
