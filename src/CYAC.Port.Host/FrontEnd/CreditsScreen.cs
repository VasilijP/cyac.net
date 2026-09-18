using CYAC.Port.Core.Data;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// The CREDITS screen: the <c>credv</c> photograph and one <c>Ok</c> button.
/// </summary>
/// <remarks>
/// <para>
/// <b>The credits list is PAINTED INTO the picture.</b>  The names and the roles
/// ("Additional Programming", "Technical Director", …) exist nowhere in the L1 image as text — they
/// are pixels of <c>2a.lib/credv.pic</c>, which the transform already publishes as
/// <c>data/images/credv.png</c>.  So this screen has nothing to typeset: a diff of
/// a captured frame of the original against the shipped picture leaves
/// exactly one object, the button at (271, 181, 44, 15), plus the mouse pointer.
/// </para>
/// <para>
/// The button is the screen's only widget, so it always has the focus and carries the
/// <c>► ◄</c> marks — which is how the reference draws it, on a RAISED bevel with the grey label.
/// </para>
/// </remarks>
public sealed class CreditsScreen : FrontEndScreen
{
    /// <summary>The credits photograph.</summary>
    public const string BackdropDocument = "images/credv.json";

    /// <summary>The <c>Ok</c> button, measured off the original's screen.</summary>
    public static readonly (int X, int Y, int Width, int Height) OkBox = (271, 181, 44, 15);

    private readonly IndexedImage _backdrop;

    /// <summary>Builds the screen from the tree.</summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="strings">The label catalogue.</param>
    public CreditsScreen(DataTree tree, FrontEndStrings strings)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(strings);
        _backdrop = tree.ImagePixels(BackdropDocument);
        Add(new FrontEndButton(
            FrontEndCommand.Ok,
            OkBox.X,
            OkBox.Y,
            OkBox.Width,
            OkBox.Height,
            strings.AtDgroup(FrontEndStrings.OkDgroup),
            FrontEndButtonStyle.Command)
        {
            Letter = 'O',
        });
    }

    /// <inheritdoc/>
    public override string Name => "CREDITS";

    /// <inheritdoc/>
    public override void Render(in FrontEndPainter.Surface surface, FrontEndFonts fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        CockpitFont font = fonts.Bold;
        FrontEndPainter.Backdrop(surface, _backdrop);
        Buttons[0].Render(surface, font, focused: true);
    }

    /// <summary>Esc leaves the credits, exactly as <c>Ok</c> does (the manual's "Esc backs up").</summary>
    protected override FrontEndCommand Back() => FrontEndCommand.Ok;
}
