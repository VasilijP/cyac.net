using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// CONFLICT SELECTION: pick the era whose historic missions you fly
/// (<c>ui_conflict_selection_screen</c>; the era selector <c>g_scenario_era_selector [0x2A0E]</c>).
/// </summary>
/// <remarks>
/// <para>
/// Over the shared <c>largev</c> backdrop and its baked panel, exactly like CHOOSE ACTIVITY.  Title is
/// <c>strings.json</c> index 8 "CONFLICT SELECTION"; the three era rows are indices 57/58/59 (World War
/// II / Korea / Vietnam); Exit is the DGROUP literal at <c>[0x3686]</c> and goes back to CHOOSE
/// ACTIVITY.  Every rectangle was measured off
/// a captured frame of the original and is verified pixel-exact by
/// and <c>FrontEndPixelTests</c>.
/// </para>
/// <para>
/// The era rows are 25 design rows tall (not the 15 of CHOOSE ACTIVITY) with a 30-row pitch, and the
/// SELECTED era is drawn sunken with the pale-teal caption — the same <see cref="FrontEndButtonStyle"/>
/// as an activity list.  The one currently focused IS the era the picker filters by.
/// </para>
/// </remarks>
public sealed class ConflictSelectionScreen : FrontEndScreen
{
    /// <summary>largev's baked inner panel — the same rectangle CHOOSE ACTIVITY fills.</summary>
    public static readonly (int X, int Y, int Width, int Height) Panel = ChooseActivityScreen.Panel;

    /// <summary>The three era rows: left edge, width, height, first row, row pitch.</summary>
    /// <remarks>Measured on the original's screen: x 12, w 130, h 25, first y 76, pitch 30.</remarks>
    public static readonly (int X, int Width, int Height, int Y, int Pitch) List = (12, 130, 25, 76, 30);

    /// <summary>The Exit command button, measured off the original's screen.</summary>
    public static readonly (int X, int Y, int Width, int Height) ExitBox = (6, 171, 44, 15);

    /// <summary>The backdrop this screen shares with CHOOSE ACTIVITY.</summary>
    public const string BackdropDocument = "images/largev.json";

    /// <summary>The three eras, in the atlas's row order.</summary>
    private static readonly MissionEra[] Eras =
        [MissionEra.WorldWarTwo, MissionEra.Korea, MissionEra.Vietnam];

    private readonly IndexedImage _backdrop;
    private readonly string _title;

    /// <summary>Builds the screen from the tree.</summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="strings">The label catalogue.</param>
    public ConflictSelectionScreen(DataTree tree, FrontEndStrings strings)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(strings);
        _backdrop = tree.ImagePixels(BackdropDocument);
        _title = strings[FrontEndStrings.ConflictSelection];

        Add(Era(0, strings[FrontEndStrings.WorldWarTwo], 'W'));
        Add(Era(1, strings[FrontEndStrings.Korea], 'K'));
        Add(Era(2, strings[FrontEndStrings.Vietnam], 'V'));
        Add(new FrontEndButton(
            FrontEndCommand.Ok,   // Exit pops back to CHOOSE ACTIVITY
            ExitBox.X,
            ExitBox.Y,
            ExitBox.Width,
            ExitBox.Height,
            strings.AtDgroup(FrontEndStrings.ExitDgroup),
            FrontEndButtonStyle.Command)
        {
            Letter = 'E',
        });
    }

    /// <inheritdoc/>
    public override string Name => "CONFLICT SELECTION";

    /// <summary>The era the focused row selects — what MISSION SELECTION filters by.</summary>
    public MissionEra SelectedEra => Focus < Eras.Length ? Eras[Focus] : MissionEra.WorldWarTwo;

    /// <summary>True when the focus is on an era row (rather than Exit).</summary>
    public bool EraFocused => Focus < Eras.Length;

    /// <inheritdoc/>
    public override void Render(in FrontEndPainter.Surface surface, FrontEndFonts fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        CockpitFont font = fonts.Bold;
        FrontEndPainter.Backdrop(surface, _backdrop);
        surface.Fill(Panel.X, Panel.Y, Panel.Width, Panel.Height, FrontEndPainter.FillIndex);

        int x = (ChooseActivityScreen.TitleCentreDoubled - FrontEndPainter.Measure(font, _title)) / 2;
        FrontEndPainter.Rule(
            surface, font, _title, x, ChooseActivityScreen.TitleY,
            FrontEndPainter.TitleInkIndex, FrontEndPainter.TitleEmbossIndex);

        for (int i = 0; i < Buttons.Count; i++)
        {
            Buttons[i].Render(surface, font, i == Focus);
        }
    }

    /// <summary>Esc backs up to CHOOSE ACTIVITY, exactly as Exit does.</summary>
    protected override FrontEndCommand Back() => FrontEndCommand.Ok;

    private static FrontEndButton Era(int index, string label, char letter) =>
        new(
            FrontEndCommand.OpenMissionSelection,
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
