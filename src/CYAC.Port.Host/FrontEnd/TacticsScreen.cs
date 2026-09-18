using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Aircraft;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// TACTICS: the two-aeroplane comparison the MISSION DESCRIPTION's <c>Tactics</c> button opens
/// (<c>ui_plane_comparison_screen @image@0x26D05</c>).
/// </summary>
/// <remarks>
/// <para>
/// Over <c>smallv</c>.  The ally's short name is ruled at the left of the title bar and the enemy's
/// at the right; their two side-view silhouettes sit under them, <b>the ALLY mirrored</b> so the two
/// face each other; five stat rows print both aeroplanes' numbers from the shipped printf templates
/// with a <c>←</c> or <c>→</c> between the columns for whoever is ahead; and a paragraph at the
/// bottom gives the matchup's hint or the decoded five-axis advice (<see cref="TacticsAdvice"/>).
/// <c>←</c> <c>→</c> cycle the ally over the six flyables, <c>↑</c> <c>↓</c> the enemy over all
/// fourteen, both wrapping; <c>Ok</c> (or Esc) returns to the description with nothing changed —
/// the manual, p. 22: "this does not select airplanes for the mission".
/// </para>
/// <para>
/// <b>Every layout number was measured off the original's screen and then confirmed against the
/// bytes.</b>  The five button boxes are not even a measurement any more: the five-widget array at
/// <c>g_cmp_widget_arr [0x3AEC]</c> (installed at <c>image@0x26D11</c>) is IN the data tree, and its
/// <c>rect_x/rect_y/rect_w</c> fields are (12,172,36), (−1,172,36), (110,172,36), (−1,172,36) and
/// (208,172,40) — the widget rectangle is the caption area inside the 2-pixel bevel, so the boxes
/// are those inset by two, and the two <c>−1</c>s are <c>widget_one_init</c>'s horizontal
/// auto-layout sentinel (<c>prev.x + prev.w + 8</c>, <c>widget-ui-synthesis.md</c> §2).  The same
/// records carry the accelerators, which is where this screen's key bindings come from.
/// </para>
/// <para>
/// <b>The four arrow keys drive the four cycle buttons, not the focus ring</b> — the widget records'
/// <c>key_shortcut_alt_scancode_u8</c> (<c>+0x11</c>) are 0x4B, 0x4D, 0x48 and 0x50 (Left, Right,
/// Up, Down) and the fifth's <c>+0x10</c> is 0x0D (Enter).  Tab / Shift-Tab still walk the ring, as
/// everywhere else.
/// </para>
/// </remarks>
public sealed class TacticsScreen : FrontEndScreen
{
    /// <summary>The backdrop every panel screen shares.</summary>
    public const string BackdropDocument = "images/smallv.json";

    /// <summary>The two side-view sheet documents, by variant (F4's).</summary>
    public static readonly string[] SheetDocuments = HangarScreen.SheetDocuments;

    // ------------------------------------------------------------------------------ the title bar

    /// <summary>Both names' pen row (<c>image@0x26EB1</c>/<c>0x26ECE</c>: <c>mov dx,0x25</c>).</summary>
    private const int NameY = 0x25;

    /// <summary>The ally's pen column (<c>image@0x26EAE: mov ax,0x10</c>, print flag 0x82).</summary>
    private const int AllyNameX = 0x10;

    /// <summary>
    /// The column the enemy's name ends at (<c>image@0x26ECB: mov ax,0xF5</c>, print flag 0xA2 —
    /// the 0x20 bit is the right-align the hangar's height callout uses too).
    /// </summary>
    private const int EnemyNameRight = 0xF5;

    // ---------------------------------------------------------------------------- the silhouettes

    /// <summary>The ally block's left edge (<c>image@0x26E75: mov ax,0x10</c>).</summary>
    private const int AllySilhouetteX = 0x10;

    /// <summary>The enemy block's (<c>image@0x26E90: mov ax,0x88</c>).</summary>
    private const int EnemySilhouetteX = 0x88;

    /// <summary>
    /// The row both silhouettes are centred on before the page's own offset
    /// (<c>image@0x27190: add si,0x47</c> — the atlas key argument doubles as the centre row).
    /// </summary>
    private const int SilhouetteCentreY = 0x47;

    // ------------------------------------------------------------------------------ the stat rows

    /// <summary>The five rows' pen column (<c>image@0x26F12</c> and friends: <c>mov ax,0x10</c>).</summary>
    private const int StatX = 0x10;

    /// <summary>The first row (<c>image@0x2706C: mov [bp-0x74],0x59</c> — the ARM line).</summary>
    private const int StatY = 0x59;

    /// <summary>The step between them (<c>image@0x27096: add [bp-0x74],7</c>).</summary>
    private const int StatStep = 7;

    /// <summary>Where an ally-ahead <c>←</c> is drawn (<c>image@0x270B4: mov ax,0x82</c>).</summary>
    private const int AllyArrowX = 0x82;

    /// <summary>Where an enemy-ahead <c>→</c> is (<c>image@0x27087: mov ax,0x94</c>).</summary>
    private const int EnemyArrowX = 0x94;

    /// <summary>Every row's ink: the panel teal, as F4's hangar values.</summary>
    private const int BodyInk = 189;

    /// <summary>Its emboss.</summary>
    private const int BodyEmboss = 18;

    // --------------------------------------------------------------------------------- the chrome

    /// <summary>
    /// The band over the advice paragraph (<c>image@0x26E5B: stats_screen_header_line_draw(0x7E)</c>).
    /// </summary>
    private const int BandY = 0x7E;

    /// <summary>A band's left edge — the panel's own interior, as F3's and F4's.</summary>
    private const int BandX = 10;

    /// <summary>Its width.</summary>
    private const int BandWidth = 240;

    /// <summary>Its three rows: light, mid, dark.</summary>
    private static readonly int[] BandTones = [22, 24, 16];

    /// <summary>
    /// The two button-row grooves (<c>image@0x26D62</c>/<c>0x26D6A</c>:
    /// <c>panel_divider_stripe_draw(0x64)</c> and <c>(0xC6)</c> — that routine takes an X and
    /// draws a VERTICAL groove, the same correction F4 made to <c>panel_title_bar_paint</c>).
    /// </summary>
    private static readonly int[] GrooveColumns = [0x64, 0xC6];

    /// <summary>The grooves' top row.</summary>
    private const int GrooveY = 168;

    /// <summary>Their height.</summary>
    private const int GrooveHeight = 19;

    // ------------------------------------------------------------------------- the advice bar

    /// <summary>
    /// The paragraph's pen and wrap (<c>image@0x2737D..0x27385</c>:
    /// <c>text_wrap_and_print(buffer, 0x10, 0xF5, 0x84, 0, 0)</c>).
    /// </summary>
    private const int AdviceX = 0x10;

    /// <summary>Its right edge.</summary>
    private const int AdviceRight = 0xF5;

    /// <summary>Its first pen row.</summary>
    private const int AdviceY = 0x84;

    /// <summary>The step between its lines, measured on the original's screen (132, 139).</summary>
    private const int AdviceStep = 7;

    // -------------------------------------------------------------------------------- the buttons

    private static readonly (int X, int Y, int Width, int Height)[] Boxes =
    [
        (10, 170, 40, 15), (54, 170, 40, 15), (108, 170, 40, 15), (152, 170, 40, 15),
        (206, 170, 44, 15),
    ];

    /// <summary>Ring index of the <c>←</c> button — widget id 0 (<c>image@0x2710A</c>).</summary>
    public const int PreviousAllyIndex = 0;

    /// <summary>Ring index of <c>→</c> — widget id 1 (<c>image@0x2713B</c>).</summary>
    public const int NextAllyIndex = 1;

    /// <summary>Ring index of <c>↑</c> — widget id 2 (<c>image@0x2714B</c>).</summary>
    public const int PreviousEnemyIndex = 2;

    /// <summary>Ring index of <c>↓</c> — widget id 3 (<c>image@0x2715F</c>).</summary>
    public const int NextEnemyIndex = 3;

    /// <summary>Ring index of <c>Ok</c> — widget id 4, where the ring opens (the original's screen).</summary>
    public const int OkIndex = 4;

    private readonly IndexedImage _backdrop;
    private readonly IndexedImage[] _sheets;
    private readonly FrontEndStrings _strings;
    private readonly AircraftEncyclopedia _encyclopedia;
    private readonly string[] _statFormats;
    private readonly string _allyArrow;
    private readonly string _enemyArrow;
    private Lfsr16 _rng;
    private int _allySlot;
    private int _allyPage;
    private int _enemyPage;
    private PairText? _text;
    private IReadOnlyList<string>? _adviceLines;

    /// <summary>Builds the comparison for one matchup.</summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="allySlot">Which flyable the ally starts on, 0..5.</param>
    /// <param name="enemyPage">Which encyclopedia page the enemy starts on, 0..13.</param>
    /// <param name="seed">
    /// The seed for the screen's own advice stream.  The original has no seed — it draws from the
    /// game's live LFSR — so this is the port's, and it is what makes the paragraph reproducible
    /// (see <see cref="TacticsAdvice"/>).
    /// </param>
    public TacticsScreen(
        DataTree tree, FrontEndStrings strings, int allySlot, int enemyPage, ushort seed)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(strings);
        _backdrop = tree.ImagePixels(BackdropDocument);
        _sheets = [.. SheetDocuments.Select(tree.ImagePixels)];
        _strings = strings;
        _encyclopedia = tree.Encyclopedia;
        _rng = Lfsr16.FromSeed(seed);
        _allySlot = WrapAlly(allySlot);
        _allyPage = AllyPage(_allySlot);
        _enemyPage = WrapEnemy(enemyPage);
        _statFormats =
        [
            strings.AtDgroup(FrontEndStrings.ArmamentFormatDgroup),
            strings.AtDgroup(FrontEndStrings.MaxSpeedFormatDgroup),
            strings.AtDgroup(FrontEndStrings.MaxAltFormatDgroup),
            strings.AtDgroup(FrontEndStrings.ThrustWeightFormatDgroup),
            strings.AtDgroupRun(FrontEndStrings.WingLoadingFormatDgroup),
        ];
        _allyArrow = strings.AtDgroupRun(FrontEndStrings.ArrowAllyDgroup);
        _enemyArrow = strings.AtDgroupRun(FrontEndStrings.ArrowEnemyDgroup);

        for (int i = 0; i < FrontEndStrings.CycleCaptionDgroup.Length; i++)
        {
            Add(new FrontEndButton(
                CycleCommands[i], Boxes[i].X, Boxes[i].Y, Boxes[i].Width, Boxes[i].Height,
                strings.AtDgroupRun(FrontEndStrings.CycleCaptionDgroup[i]),
                FrontEndButtonStyle.Command));
        }

        Add(new FrontEndButton(
            FrontEndCommand.Ok, Boxes[OkIndex].X, Boxes[OkIndex].Y, Boxes[OkIndex].Width,
            Boxes[OkIndex].Height, strings.AtDgroup(FrontEndStrings.OkDgroup),
            FrontEndButtonStyle.Command) { Letter = 'O' });
        FocusOn(OkIndex);
    }

    private static readonly FrontEndCommand[] CycleCommands =
    [
        FrontEndCommand.PreviousAlly, FrontEndCommand.NextAlly,
        FrontEndCommand.PreviousEnemy, FrontEndCommand.NextEnemy,
    ];

    /// <inheritdoc/>
    public override string Name => "TACTICS";

    /// <summary>Which flyable slot the left column is on, 0..5.</summary>
    public int AllySlot => _allySlot;

    /// <summary>Which encyclopedia page the right column is on, 0..13.</summary>
    public int EnemyPage => _enemyPage;

    /// <summary>The left-hand aeroplane.</summary>
    /// <remarks>
    /// Resolved to a PAGE INDEX when the slot changes rather than looked up per frame:
    /// <see cref="AircraftEncyclopedia.ByFlyableSlot"/> walks an <c>IReadOnlyList</c>, whose
    /// enumerator boxes, and a steady front-end frame must allocate nothing (protocol §8).
    /// </remarks>
    public EncyclopediaPlane Ally => _encyclopedia.Planes[_allyPage];

    /// <summary>The right-hand one.</summary>
    public EncyclopediaPlane Enemy => _encyclopedia.Planes[_enemyPage];

    /// <summary>The paragraph at the bottom: the hint or the axis advice.</summary>
    public string Advice => Text().Advice;

    /// <summary>The five THRESHOLDED outcomes the advice is scored from.</summary>
    public IReadOnlyList<AxisOutcome> Outcomes => Text().Outcomes;

    /// <summary>The five arrow directions the rows are drawn with (no thresholds).</summary>
    public IReadOnlyList<ArrowDirection> Arrows => Text().Arrows;

    /// <summary>
    /// Steps the ally over the six flyables, wrapping
    /// (<c>image@0x2710A</c>: <c>−1</c> wraps to 5; <c>image@0x2713B</c>: <c>+1</c> wraps to 0).
    /// </summary>
    /// <param name="by">−1 or +1.</param>
    public void StepAlly(int by)
    {
        _allySlot = WrapAlly(_allySlot + by);
        _allyPage = AllyPage(_allySlot);
        Invalidate();
    }

    /// <summary>
    /// Steps the enemy over all fourteen pages, wrapping (<c>image@0x27151</c>/<c>0x27163</c>, whose
    /// bound is <c>pi.bin</c>'s own plane count).
    /// </summary>
    /// <param name="by">−1 or +1.</param>
    public void StepEnemy(int by)
    {
        _enemyPage = WrapEnemy(_enemyPage + by);
        Invalidate();
    }

    /// <inheritdoc/>
    public override FrontEndCommand Press(FrontEndKey key)
    {
        // The four arrows FIRE THE FOUR CYCLE BUTTONS — the widget records' own accelerators (+0x11 =
        // 0x4B / 0x4D / 0x48 / 0x50) — rather than walking the ring, which Tab still does.
        switch (key)
        {
            case FrontEndKey.Left:
                return FrontEndCommand.PreviousAlly;
            case FrontEndKey.Right:
                return FrontEndCommand.NextAlly;
            case FrontEndKey.Up:
                return FrontEndCommand.PreviousEnemy;
            case FrontEndKey.Down:
                return FrontEndCommand.NextEnemy;
            default:
                return base.Press(key);
        }
    }

    /// <summary>Esc leaves the comparison for MISSION DESCRIPTION (<c>image@0x270E0</c>).</summary>
    protected override FrontEndCommand Back() => FrontEndCommand.Ok;

    /// <inheritdoc/>
    public override void Render(in FrontEndPainter.Surface surface, FrontEndFonts fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        EncyclopediaPlane ally = Ally;
        EncyclopediaPlane enemy = Enemy;
        PairText text = Text();
        FrontEndPainter.Backdrop(surface, _backdrop);

        // The title bar: the ally's short name ruled at the left, the enemy's ruled at the right.
        FrontEndPainter.Rule(
            surface, fonts.Bold, ally.NameShort, AllyNameX, NameY,
            FrontEndPainter.TitleInkIndex, FrontEndPainter.TitleEmbossIndex);
        FrontEndPainter.Rule(
            surface,
            fonts.Bold,
            enemy.NameShort,
            EnemyNameRight - FrontEndPainter.Measure(fonts.Bold, enemy.NameShort),
            NameY,
            FrontEndPainter.TitleInkIndex,
            FrontEndPainter.TitleEmbossIndex);

        // The two silhouettes; only the ALLY is mirrored (see FrontEndPainter.BlitMirrored).
        Silhouette(surface, ally, AllySilhouetteX, mirrored: true);
        Silhouette(surface, enemy, EnemySilhouetteX, mirrored: false);

        // The five stat rows and the arrow between the columns.
        for (int i = 0; i < TacticsAdvice.AxisCount; i++)
        {
            int y = StatY + (i * StatStep);
            FrontEndPainter.Text(surface, fonts.Small, text.Rows[i], StatX, y, BodyInk, BodyEmboss);
            switch (text.Arrows[i])
            {
                case ArrowDirection.Ally:
                    FrontEndPainter.Text(
                        surface, fonts.Small, _allyArrow, AllyArrowX, y, BodyInk, BodyEmboss);
                    break;
                case ArrowDirection.Enemy:
                    FrontEndPainter.Text(
                        surface, fonts.Small, _enemyArrow, EnemyArrowX, y, BodyInk, BodyEmboss);
                    break;
                default:
                    break;
            }
        }

        Band(surface, BandY);

        IReadOnlyList<string> lines = _adviceLines ??= FrontEndText.WrapParagraphs(
            fonts.Small, text.Advice, AdviceRight - AdviceX);
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length > 0)
            {
                FrontEndPainter.Text(
                    surface, fonts.Small, lines[i], AdviceX, AdviceY + (i * AdviceStep),
                    BodyInk, BodyEmboss);
            }
        }

        foreach (int column in GrooveColumns)
        {
            Groove(surface, column);
        }

        for (int i = 0; i < Buttons.Count; i++)
        {
            Buttons[i].Render(surface, fonts.Bold, i == Focus);
        }
    }

    private void Silhouette(
        in FrontEndPainter.Surface surface, EncyclopediaPlane plane, int x, bool mirrored)
    {
        SilhouetteRow row = plane.SilhouetteRow;
        IndexedImage sheet = _sheets[Math.Clamp(row.Variant, 0, _sheets.Length - 1)];
        int top = SilhouetteCentreY + plane.SilhouetteYOffset - (row.Height / 2);
        if (mirrored)
        {
            FrontEndPainter.BlitMirrored(
                surface, sheet, 0, row.TopY, row.SheetWidth, row.Height, x, top);
        }
        else
        {
            FrontEndPainter.Blit(
                surface, sheet, 0, row.TopY, row.SheetWidth, row.Height, x, top);
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

    /// <summary>
    /// Everything one matchup costs, composed once when the pair changes — the original's own
    /// dirty check (<c>image@0x26DC8</c>: the columns are only re-rendered when
    /// <c>(ally, enemy)</c> differs from what this video page last drew).
    /// </summary>
    /// <param name="Rows">The five filled stat lines.</param>
    /// <param name="Arrows">The five arrow directions (a raw comparison, no thresholds).</param>
    /// <param name="Outcomes">The five thresholded outcomes the advice was scored from.</param>
    /// <param name="Advice">The paragraph, before it is wrapped.</param>
    private sealed record PairText(
        IReadOnlyList<string> Rows,
        IReadOnlyList<ArrowDirection> Arrows,
        IReadOnlyList<AxisOutcome> Outcomes,
        string Advice);

    private void Invalidate()
    {
        _text = null;
        _adviceLines = null;
    }

    private PairText Text() => _text ??= Compose();

    private PairText Compose()
    {
        EncyclopediaPlane ally = Ally;
        EncyclopediaPlane enemy = Enemy;

        // THRUST/WEIGHT: "%d.%02d" from the u8.8 ratio — the integer part is the high byte and the
        // fraction (value & 0xFF) * 100 / 256 (image@0x26FBD..0x26FD0).
        (int allyWhole, int allyFraction) = Ratio(ally.ThrustToWeightQ8);
        (int enemyWhole, int enemyFraction) = Ratio(enemy.ThrustToWeightQ8);
        string[] rows = new string[TacticsAdvice.AxisCount];

        // ARM: the ally's summary right-aligned into the format's own %24s, then the enemy's
        // STRCAT'd on (image@0x26EDB / image@0x26EF6) — the eight trailing spaces are in the format.
        rows[0] = FrontEndStrings.FormatPrintf(_statFormats[0], ally.ArmamentSummary)
            + enemy.ArmamentSummary;
        rows[1] = FrontEndStrings.FormatPrintf(_statFormats[1], ally.MaxSpeedMph, enemy.MaxSpeedMph);
        rows[2] = FrontEndStrings.FormatPrintf(
            _statFormats[2], ally.MaxAltitudeFt, enemy.MaxAltitudeFt);
        rows[3] = FrontEndStrings.FormatPrintf(
            _statFormats[3], allyWhole, allyFraction, enemyWhole, enemyFraction);
        rows[4] = FrontEndStrings.FormatPrintf(
            _statFormats[4], ally.WingLoadingPsf, enemy.WingLoadingPsf);

        string advice = TacticsAdvice.Compose(_strings, _encyclopedia, ally, enemy, ref _rng);
        return new PairText(
            rows, TacticsAdvice.Arrows(ally, enemy), TacticsAdvice.Compare(ally, enemy), advice);
    }

    private static (int Whole, int Fraction) Ratio(int q8) => (q8 >> 8, ((q8 & 0xFF) * 100) / 256);

    private int AllyPage(int slot)
    {
        for (int i = 0; i < _encyclopedia.Planes.Count; i++)
        {
            if (_encyclopedia.Planes[i].FlyableSlot == slot)
            {
                return i;
            }
        }

        return 0;
    }

    private int WrapAlly(int slot)
    {
        int count = 0;
        for (int i = 0; i < _encyclopedia.Planes.Count; i++)
        {
            if (_encyclopedia.Planes[i].IsFlyable)
            {
                count++;
            }
        }

        return count == 0 ? 0 : ((slot % count) + count) % count;
    }

    private int WrapEnemy(int page)
    {
        int count = _encyclopedia.Planes.Count;
        return count == 0 ? 0 : ((page % count) + count) % count;
    }
}
