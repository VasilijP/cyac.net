using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Aircraft;
using CYAC.Port.Render;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// THE HANGAR: the Test Flight's own screen (<c>ui_aircraft_stats_panel @image@0x264ED</c>,
/// activity-dispatch entry 2).
/// </summary>
/// <remarks>
/// <para>
/// Over <c>smallv</c>.  The title bar carries the aircraft's full name (bold) and its national
/// insignia; two grooved bands split the panel; the left column is <c>SIDE VIEW:</c> (the sheet
/// silhouette with its two dimension callouts) or <c>3D VIEW:</c> (the mesh, turning under the arrow
/// keys); the right column is <c>ARMAMENT:</c> / <c>POWER:</c> / <c>PERFORMANCE:</c>; the
/// description is wrapped below both; and the bottom row is <c>←</c> <c>→</c> <c>3d/2d</c>
/// <c>Exit</c> <c>Fly</c>, with <c>Fly</c> greyed on the eight aircraft the player cannot fly.
/// </para>
/// <para>
/// <b>Every number below was measured off the original's screen and then
/// CONFIRMED against the bytes</b> — the measurement and the disassembly agree on all of them, which
/// is why each carries an <c>image@</c> as well.  <c>FrontEndPixelTests</c> asserts that THIS code
/// produces the same.
/// </para>
/// <para>
/// <b>The panel is drawn in the 4×6 MONOSPACE font</b>, not in <c>prop3</c>: every heading, every
/// <c>pi.bin</c> value, both callouts and the description fit that font and no other (brute-forced
/// against the reference pixels, the F2 method).  Only the title-bar name and the five button
/// captions are <c>propbold</c>.  That is also why the PERFORMANCE lines' printf field widths
/// (<c>%7d</c>, <c>%10u</c>, <c>%10d</c>) line their numbers up.
/// </para>
/// <para>
/// <b>The arrow keys turn the model, they do not move the focus.</b>  That is the original's own
/// binding (<c>image@0x26BC5</c>: Up ⇒ <c>rotY − 0x28</c>, Down ⇒ <c>+0x28</c>, Left ⇒
/// <c>rotX + 0x28</c>, Right ⇒ <c>− 0x28</c>, each wrapped mod <c>0xB40</c>), and it is the only way
/// to rotate from the keyboard; Tab / Shift+Tab still walk the ring, as they do on every other
/// screen.
/// </para>
/// </remarks>
public sealed class HangarScreen : FrontEndScreen, IDisposable
{
    /// <summary>The backdrop every panel screen shares.</summary>
    public const string BackdropDocument = "images/smallv.json";

    /// <summary>The insignia strip document.</summary>
    public const string InsigniaDocument = "images/insigv.json";

    /// <summary>The two side-view sheet documents, by variant.</summary>
    public static readonly string[] SheetDocuments = ["images/planes0.json", "images/planes1.json"];

    // ---------------------------------------------------------------- the chrome (measured + cited)

    /// <summary>The aircraft name's pen (<c>image@0x26761: mov ax,0xE; mov dx,0x26</c>).</summary>
    private static readonly (int X, int Y) NameAt = (0x0E, 0x26);

    /// <summary>The insignia's top-left (<c>image@0x26745: panel_sprite_blit(idx, 0xE0, 0x24)</c>).</summary>
    private static readonly (int X, int Y) InsigniaAt = (0xE0, 0x24);

    /// <summary>The two grooved bands (<c>image@0x266DC</c>/<c>0x266E8</c>, AX = 0x30 / 0x86).</summary>
    private static readonly int[] BandRows = [0x30, 0x86];

    /// <summary>A band's left edge and width — the panel's own interior, as F3's stats bands.</summary>
    private const int BandX = 10;

    /// <summary>Its width.</summary>
    private const int BandWidth = 240;

    /// <summary>
    /// The column divider's left column (<c>image@0x266F7..0x26729</c>: three one-pixel VERTICAL
    /// lines at x = 0x97, 0x98, 0x99 from y = 0x32 to y = 0x86).
    /// </summary>
    private const int DividerX = 0x97;

    /// <summary>Its top row.</summary>
    private const int DividerY1 = 0x32;

    /// <summary>Its bottom row.</summary>
    private const int DividerY2 = 0x86;

    /// <summary>Its three tones — <c>gfx_or_palette_color_select</c>(0x16 / 0x18 / 0x10).</summary>
    private static readonly int[] DividerTones = [22, 24, 16];

    /// <summary>The band's three rows: light, mid, dark (F3's <c>stats_panel_band_draw</c> shape).</summary>
    private static readonly int[] BandTones = [22, 24, 16];

    /// <summary>
    /// The two button-row grooves (<c>image@0x265BD</c>/<c>0x265C4</c>: <c>panel_title_bar_paint</c>
    /// at x = 0x57 and x = 0x96 — a VERTICAL groove, not a horizontal divider).
    /// </summary>
    private static readonly int[] GrooveColumns = [0x57, 0x96];

    /// <summary>The grooves' top row.</summary>
    private const int GrooveY = 168;

    /// <summary>Their height.</summary>
    private const int GrooveHeight = 19;

    // ------------------------------------------------------------------------------ the two columns

    /// <summary>The left column's heading pen (<c>image@0x26924: mov ax,0xE; mov dx,0x35</c>).</summary>
    private static readonly (int X, int Y) ViewHeadingAt = (0x0E, 0x35);

    /// <summary>The right column's pen column (<c>image@0x26784</c> and friends: 0xA0).</summary>
    private const int RightX = 0xA0;

    /// <summary><c>ARMAMENT:</c>'s pen row (<c>image@0x26784</c>).</summary>
    private const int ArmamentHeadingY = 0x35;

    /// <summary>The three armament lines' rows (<c>image@0x2679E</c>/<c>0x267BF</c>/<c>0x267E0</c>).</summary>
    private static readonly int[] ArmamentRows = [0x3E, 0x45, 0x4C];

    /// <summary><c>POWER:</c>'s row (<c>image@0x267F8</c>).</summary>
    private const int PowerHeadingY = 0x55;

    /// <summary>The engine line's row (<c>image@0x26812</c>).</summary>
    private const int EngineY = 0x5E;

    /// <summary><c>PERFORMANCE:</c>'s row (<c>image@0x2682A</c>).</summary>
    private const int PerformanceHeadingY = 0x67;

    /// <summary>The three performance rows (<c>image@0x26858</c>/<c>0x26886</c>/<c>0x268B4</c>).</summary>
    private static readonly int[] PerformanceRows = [0x70, 0x77, 0x7E];

    /// <summary>The description's pen (<c>image@0x268D5: text_wrap_and_print(desc, 0xE, 0xE8, 0x8B)</c>).</summary>
    private static readonly (int X, int Y) DescriptionAt = (0x0E, 0x8B);

    /// <summary>Its right edge — the wrap measure is <c>0xE8 − 0xE</c> = 218 columns.</summary>
    private const int DescriptionRight = 0xE8;

    /// <summary>The step between its lines, measured on the original's screen (139, 146, 153).</summary>
    private const int DescriptionLineStep = 7;

    /// <summary>A heading's ink: the panel teal.</summary>
    private const int HeadingInk = 189;

    /// <summary>A heading's emboss — the TITLE tone, one step lighter than a body line's.</summary>
    private const int HeadingEmboss = FrontEndPainter.TitleEmbossIndex;

    /// <summary>A value line's ink.</summary>
    private const int BodyInk = 189;

    /// <summary>Its emboss.</summary>
    private const int BodyEmboss = 18;

    // ------------------------------------------------------------------------------ the side view

    /// <summary>
    /// The silhouette's destination column: the panel's own left edge plus 0x10
    /// (<c>image@0x26A75: mov ax,[bp-0x3C]; add ax,0x10</c> ⇒ 0x10 + 0x10 = 32).
    /// </summary>
    private const int SilhouetteX = HangarMeshView.PanelX + 0x10;

    /// <summary>
    /// The row the silhouette is CENTRED on before the page's own offset:
    /// <c>panel.y + panel.h/2</c> = 0x3D + 36 = 97 (<c>image@0x26A7C..0x26A8B</c>).
    /// </summary>
    private const int SilhouetteCentreY =
        HangarMeshView.PanelY + (HangarMeshView.PanelHeight / 2);

    /// <summary>The length callout's line row (<c>image@0x26B40: mov ax,0x74</c>).</summary>
    private const int LengthLineY = 0x74;

    /// <summary>Its text's pen row (<c>image@0x26B7C: mov dx,0x77</c>).</summary>
    private const int LengthTextY = 0x77;

    /// <summary>
    /// How far above the height callout's mid-point its text's pen sits
    /// (<c>image@0x26B10..0x26B13: ax = (y1+y2)/2; dx = ax; dec dx; dec dx</c>).
    /// </summary>
    private const int HeightTextRise = 2;

    // ------------------------------------------------------------------------------ the button row

    private static readonly (int X, int Y, int Width, int Height) PreviousBox = (10, 170, 34, 15);
    private static readonly (int X, int Y, int Width, int Height) NextBox = (48, 170, 34, 15);
    private static readonly (int X, int Y, int Width, int Height) ToggleBox = (94, 170, 52, 15);
    private static readonly (int X, int Y, int Width, int Height) ExitBox = (158, 170, 44, 15);
    private static readonly (int X, int Y, int Width, int Height) FlyBox = (206, 170, 44, 15);

    /// <summary><c>propbold</c> codepoint 0x0F — the solid ◄ the <c>←</c> button carries.</summary>
    /// <remarks>
    /// Measured, not assumed: 0x0F is a 9-wide advance whose fourteen inked pixels reproduce
    /// the original's left button exactly at the centred pen x = 23.  (0x17/0x18, F1's focus
    /// marks, are the SMALL pair; these are the big ones.)
    /// </remarks>
    public const char ArrowLeft = '\u000F';

    /// <summary><c>propbold</c> codepoint 0x10 — the solid ► of the <c>→</c> button.</summary>
    public const char ArrowRight = '\u0010';

    /// <summary>Ring index of the <c>←</c> button.</summary>
    public const int PreviousButtonIndex = 0;

    /// <summary>Ring index of the <c>→</c> button.</summary>
    public const int NextButtonIndex = 1;

    /// <summary>Ring index of the <c>3d/2d</c> button.</summary>
    public const int ToggleButtonIndex = 2;

    /// <summary>Ring index of <c>Exit</c>.</summary>
    public const int ExitButtonIndex = 3;

    /// <summary>Ring index of <c>Fly</c> — where the ring opens (the original's screen marks it).</summary>
    public const int FlyButtonIndex = 4;

    /// <summary>
    /// The rotation the view opens at and returns to whenever <c>3d/2d</c> is pressed:
    /// <c>(0x2D0, 0)</c> — a quarter turn, i.e. a pure side view with the nose to the left.
    /// </summary>
    /// <remarks><c>image@0x26520</c> (entry) and <c>image@0x26CA1</c>/<c>0x26CA6</c> (the toggle).</remarks>
    public const int InitialRotationX = 0x2D0;

    /// <summary>One arrow press (<c>image@0x26BF3: mov ax,0x28</c>).</summary>
    public const int RotationStep = 0x28;

    private readonly IndexedImage _backdrop;
    private readonly IndexedImage _insignia;
    private readonly IndexedImage[] _sheets;
    private readonly FrontEndStrings _strings;
    private readonly AircraftEncyclopedia _encyclopedia;
    private readonly HangarMeshView? _mesh;
    private readonly string _feetInches;
    private int _page;
    private PageText? _text;
    private bool _threeD;
    private int _rotationX = InitialRotationX;
    private int _rotationY;
    private bool _disposed;

    /// <summary>Builds the hangar.</summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="palette">The game's palette — the 3-D view resolves mesh colours through it.</param>
    /// <param name="page">Which encyclopedia page to open on.</param>
    public HangarScreen(DataTree tree, FrontEndStrings strings, IReadOnlyList<Rgb24> palette, int page)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(palette);
        _backdrop = tree.ImagePixels(BackdropDocument);
        _insignia = tree.ImagePixels(InsigniaDocument);
        _sheets = [.. SheetDocuments.Select(tree.ImagePixels)];
        _strings = strings;
        _encyclopedia = tree.Encyclopedia;
        _feetInches = strings.AtDgroup(FrontEndStrings.FeetInchesFormatDgroup);
        _mesh = palette.Count == 0 ? null : new HangarMeshView(tree, palette);
        _page = Wrap(page);

        Add(new FrontEndButton(
            FrontEndCommand.PreviousAircraft, PreviousBox.X, PreviousBox.Y, PreviousBox.Width,
            PreviousBox.Height, ArrowLeft.ToString(), FrontEndButtonStyle.Command));
        Add(new FrontEndButton(
            FrontEndCommand.NextAircraft, NextBox.X, NextBox.Y, NextBox.Width, NextBox.Height,
            ArrowRight.ToString(), FrontEndButtonStyle.Command));
        Add(new FrontEndButton(
            FrontEndCommand.ToggleHangarView, ToggleBox.X, ToggleBox.Y, ToggleBox.Width,
            ToggleBox.Height, strings[FrontEndStrings.ThreeDTwoD], FrontEndButtonStyle.Command));
        Add(new FrontEndButton(
            FrontEndCommand.Ok, ExitBox.X, ExitBox.Y, ExitBox.Width, ExitBox.Height,
            strings.AtDgroup(FrontEndStrings.ExitDgroup), FrontEndButtonStyle.Command) { Letter = 'X' });
        Add(new FrontEndButton(
            FrontEndCommand.FlyTestFlight, FlyBox.X, FlyBox.Y, FlyBox.Width, FlyBox.Height,
            strings[FrontEndStrings.Fly], FrontEndButtonStyle.Command) { Letter = 'F' });
        FocusOn(FlyButtonIndex);
        UpdateFlyButton();
    }

    /// <inheritdoc/>
    public override string Name => "HANGAR";

    /// <summary>Which of the fourteen encyclopedia pages is up.</summary>
    public int Page => _page;

    /// <summary>The aeroplane on that page.</summary>
    public EncyclopediaPlane Plane => _encyclopedia.Planes[_page];

    /// <summary>Whether the left column is showing the mesh rather than the sheet.</summary>
    /// <remarks><c>[bp-0x42]</c>: the original opens in the 2-D view (<c>image@0x265DC</c>).</remarks>
    public bool ThreeD => _threeD;

    /// <summary>The model's heading angle, 0..<c>0xB3F</c> (<c>[bp-0x1C]</c>).</summary>
    public int RotationX => _rotationX;

    /// <summary>Its pitch angle (<c>[bp-0x1A]</c>).</summary>
    public int RotationY => _rotationY;

    /// <summary>Whether the 3-D view has built its mesh library yet.</summary>
    public bool MeshesLoaded => _mesh?.MeshesLoaded ?? false;

    /// <summary>What <c>Fly</c> hands the shell.</summary>
    /// <remarks>
    /// The Test Flight's own site is F1's default draw — the hangar chooses the aeroplane, not the
    /// place.
    /// </remarks>
    public LastSortie Sortie => new(
        LastSortie.TestFlightKind, -1, 0, Plane.FlyableBasename, FrontEnd.DefaultSite);

    /// <summary>Steps the page by one, wrapping (<c>image@0x26C22</c> / <c>0x26C80</c>).</summary>
    /// <param name="by">−1 or +1.</param>
    public void Step(int by)
    {
        _page = Wrap(_page + by);
        _text = null;              // recomposed on the next frame (see Compose)
        UpdateFlyButton();
    }

    /// <summary>
    /// Flips the view and resets the rotation to <c>(0x2D0, 0)</c> (<c>image@0x26C98</c>).
    /// </summary>
    public void ToggleView()
    {
        _threeD = !_threeD;
        _rotationX = InitialRotationX;
        _rotationY = 0;
    }

    /// <summary>Turns the model, wrapping into <c>[0, 0xB40)</c>.</summary>
    /// <param name="deltaX">Added to the heading angle.</param>
    /// <param name="deltaY">Added to the pitch angle.</param>
    public void Rotate(int deltaX, int deltaY)
    {
        _rotationX = WrapAngle(_rotationX + deltaX);
        _rotationY = WrapAngle(_rotationY + deltaY);
    }

    /// <inheritdoc/>
    public override FrontEndCommand Press(FrontEndKey key)
    {
        // The four arrows TURN THE MODEL here, exactly as image@0x26BC5 dispatches them; they do
        // not walk the ring, which Tab / Shift+Tab still do.
        switch (key)
        {
            case FrontEndKey.Up:
                Rotate(0, -RotationStep);
                return FrontEndCommand.None;
            case FrontEndKey.Down:
                Rotate(0, RotationStep);
                return FrontEndCommand.None;
            case FrontEndKey.Left:
                Rotate(RotationStep, 0);
                return FrontEndCommand.None;
            case FrontEndKey.Right:
                Rotate(-RotationStep, 0);
                return FrontEndCommand.None;
            default:
                return base.Press(key);
        }
    }

    /// <summary>Esc leaves the hangar for CHOOSE ACTIVITY, exactly as <c>Exit</c> does.</summary>
    protected override FrontEndCommand Back() => FrontEndCommand.Ok;

    /// <inheritdoc/>
    public override void Render(in FrontEndPainter.Surface surface, FrontEndFonts fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        EncyclopediaPlane plane = Plane;
        PageText text = _text ??= Compose(fonts);
        FrontEndPainter.Backdrop(surface, _backdrop);

        // Title bar: the full name in bold, then the national insignia.
        FrontEndPainter.Text(
            surface, fonts.Bold, plane.NameFull, NameAt.X, NameAt.Y,
            FrontEndPainter.TitleInkIndex, FrontEndPainter.TitleEmbossIndex);
        FrontEndPainter.InsigniaStrip(
            surface, _insignia, plane.InsigniaIndex, MissionSelectionScreen.InsigniaStride,
            MissionSelectionScreen.InsigniaKey, InsigniaAt.X, InsigniaAt.Y);

        // The panel's own chrome: two horizontal bands, then the vertical column divider OVER them.
        foreach (int row in BandRows)
        {
            Band(surface, row);
        }

        for (int i = 0; i < DividerTones.Length; i++)
        {
            surface.Fill(DividerX + i, DividerY1, 1, DividerY2 - DividerY1 + 1, DividerTones[i]);
        }

        // The left column.
        FrontEndPainter.Rule(
            surface,
            fonts.Small,
            _strings[_threeD ? FrontEndStrings.ThreeDView : FrontEndStrings.SideView],
            ViewHeadingAt.X,
            ViewHeadingAt.Y,
            HeadingInk,
            HeadingEmboss);
        if (_threeD)
        {
            if (_mesh is null || !_mesh.Render(surface, plane, _rotationX, _rotationY))
            {
                HangarMeshView.Clear(surface);
            }
        }
        else
        {
            RenderSideView(surface, fonts, plane, text);
        }

        // The right column.
        RenderStats(surface, fonts, text);

        // The description, under both columns.  Its wrap, like every other derived string on this
        // screen, is composed once per PAGE (see Compose) so a steady frame allocates nothing.
        IReadOnlyList<string> lines = text.Description;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length > 0)
            {
                FrontEndPainter.Text(
                    surface, fonts.Small, lines[i], DescriptionAt.X,
                    DescriptionAt.Y + (i * DescriptionLineStep), BodyInk, BodyEmboss);
            }
        }

        // The button row: its two grooves, then the five widgets.
        foreach (int column in GrooveColumns)
        {
            Groove(surface, column);
        }

        for (int i = 0; i < Buttons.Count; i++)
        {
            Buttons[i].Render(surface, fonts.Bold, i == Focus);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mesh?.Dispose();
    }

    private void RenderSideView(
        in FrontEndPainter.Surface surface,
        FrontEndFonts fonts,
        EncyclopediaPlane plane,
        PageText text)
    {
        SilhouetteRow row = plane.SilhouetteRow;
        IndexedImage sheet = _sheets[Math.Clamp(row.Variant, 0, _sheets.Length - 1)];
        int centre = SilhouetteCentreY + plane.SilhouetteYOffset;

        // The hangar never MIRRORS its side view: plane_silhouette_draw's flip flag is pushed as
        // zero here (image@0x26A8F: sub al,al; push ax).  The TACTICS screen is where the flag is
        // used — its right-hand aeroplane faces the left-hand one (F5).
        FrontEndPainter.Blit(
            surface, sheet, 0, row.TopY, row.SheetWidth, row.Height,
            SilhouetteX, centre - (row.Height / 2));

        // The HEIGHT callout: a vertical line down the silhouette's own column, its text
        // right-aligned against it and centred on the span.
        DimensionCallouts callouts = plane.Callouts;
        EmbossedColumn(surface, SilhouetteX, callouts.HeightY1, callouts.HeightY2);
        string height = text.Height;
        FrontEndPainter.Text(
            surface,
            fonts.Small,
            height,
            SilhouetteX - FrontEndPainter.Measure(fonts.Small, height),
            ((callouts.HeightY1 + callouts.HeightY2) / 2) - HeightTextRise,
            BodyInk,
            BodyEmboss);

        // The LENGTH callout: a horizontal line under the silhouette, its text centred below it.
        EmbossedRow(surface, callouts.LengthX1, callouts.LengthX2, LengthLineY);
        string length = text.Length;
        FrontEndPainter.Text(
            surface,
            fonts.Small,
            length,
            ((callouts.LengthX1 + callouts.LengthX2) / 2)
                - (FrontEndPainter.Measure(fonts.Small, length) / 2),
            LengthTextY,
            BodyInk,
            BodyEmboss);
    }

    private void RenderStats(in FrontEndPainter.Surface surface, FrontEndFonts fonts, PageText text)
    {
        EncyclopediaPlane plane = Plane;
        FrontEndPainter.Rule(
            surface, fonts.Small, _strings[FrontEndStrings.Armament], RightX, ArmamentHeadingY,
            HeadingInk, HeadingEmboss);
        for (int i = 0; i < ArmamentRows.Length && i < plane.ArmamentLines.Count; i++)
        {
            FrontEndPainter.Text(
                surface, fonts.Small, plane.ArmamentLines[i], RightX, ArmamentRows[i],
                BodyInk, BodyEmboss);
        }

        FrontEndPainter.Rule(
            surface, fonts.Small, _strings[FrontEndStrings.Power], RightX, PowerHeadingY,
            HeadingInk, HeadingEmboss);
        FrontEndPainter.Text(
            surface, fonts.Small, plane.Engine, RightX, EngineY, BodyInk, BodyEmboss);

        FrontEndPainter.Rule(
            surface, fonts.Small, _strings[FrontEndStrings.Performance], RightX,
            PerformanceHeadingY, HeadingInk, HeadingEmboss);
        for (int i = 0; i < PerformanceRows.Length; i++)
        {
            FrontEndPainter.Text(
                surface, fonts.Small, text.Performance[i], RightX, PerformanceRows[i],
                BodyInk, BodyEmboss);
        }
    }

    private static void Band(in FrontEndPainter.Surface surface, int y)
    {
        surface.Fill(BandX, y, BandWidth, 1, BandTones[0]);
        surface.Fill(BandX + 1, y + 1, BandWidth, 1, BandTones[1]);
        surface.Fill(BandX, y + 2, BandWidth, 1, BandTones[2]);
    }

    private static void Groove(in FrontEndPainter.Surface surface, int x)
    {
        surface.Fill(x, GrooveY, 2, GrooveHeight, 29);
        surface.Fill(x + 2, GrooveY, 1, GrooveHeight, 21);
    }

    // Both callout lines are drawn TWICE, the emboss one pixel down/right of the ink — the same
    // rule the text uses (image@0x26AC3 pushes the emboss colour, image@0x26ADD the ink).
    private static void EmbossedColumn(in FrontEndPainter.Surface surface, int x, int y1, int y2)
    {
        surface.Fill(x, y1 + 1, 1, y2 - y1 + 1, BodyEmboss);
        surface.Fill(x, y1, 1, y2 - y1 + 1, BodyInk);
    }

    private static void EmbossedRow(in FrontEndPainter.Surface surface, int x1, int x2, int y)
    {
        surface.Fill(x1, y + 1, x2 - x1 + 1, 1, BodyEmboss);
        surface.Fill(x1, y, x2 - x1 + 1, 1, BodyInk);
    }

    /// <summary>
    /// Everything the page's own text costs, composed once when the page changes.
    /// </summary>
    /// <param name="Description">The blurb, already wrapped to the panel's measure.</param>
    /// <param name="Performance">The three filled PERFORMANCE lines.</param>
    /// <param name="Height">The height callout, e.g. <c>13'8"</c>.</param>
    /// <param name="Length">The length callout.</param>
    private sealed record PageText(
        IReadOnlyList<string> Description,
        IReadOnlyList<string> Performance,
        string Height,
        string Length);

    private PageText Compose(FrontEndFonts fonts)
    {
        EncyclopediaPlane plane = Plane;
        string[] performance = new string[PerformanceRows.Length];
        int[] values = [plane.MaxSpeedMph, plane.MaxAltitudeFt, plane.WeightLb];
        for (int i = 0; i < performance.Length; i++)
        {
            performance[i] = FrontEndStrings.FormatPrintf(
                _strings[FrontEndStrings.PerformanceLineFirst + i], values[i]);
        }

        return new PageText(
            FrontEndText.WrapParagraphs(
                fonts.Small, plane.Description, DescriptionRight - DescriptionAt.X),
            performance,
            plane.HeightText(_feetInches),
            plane.LengthText(_feetInches));
    }

    private void UpdateFlyButton()
    {
        FrontEndButton fly = Buttons[FlyButtonIndex];
        EncyclopediaPlane plane = Plane;
        fly.Enabled = plane.IsFlyable;
        fly.DisabledReason = plane.IsFlyable
            ? string.Empty
            : $"{plane.ClassName} is an enemy-only aircraft - the original greys Fly for it too "
                + "(is_aircraft_flyable_get_player_idx @image@0x27404)";
    }

    private int Wrap(int page)
    {
        int count = _encyclopedia.Planes.Count;
        return count == 0 ? 0 : ((page % count) + count) % count;
    }

    private static int WrapAngle(int angle) =>
        ((angle % HangarMeshView.FullTurn) + HangarMeshView.FullTurn) % HangarMeshView.FullTurn;
}
