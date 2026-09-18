using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Mission;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// MISSION SELECTION: the paged list of an era's historic missions (<c>scenario_filter_by_era
/// @image@0x24B24</c> + the detail panel <c>scenario_detail_panel_show</c>).
/// </summary>
/// <remarks>
/// <para>
/// Over the shared <c>smallv</c> backdrop, whose baked panel and Yeager photo the screen draws over.
/// The engine draws the title (<c>strings.json</c> index 0), the <c>PAGE %d OF %d</c> counter (DGROUP
/// <c>[0x2ED6]</c>, small 4×6 font), three mission rows, and the Previous Page / Next Page / Exit
/// buttons.  Everything below was measured off the original's screen (WWII and Vietnam eras) and
/// is verified pixel-exact — page 1 of both eras — by and <c>FrontEndPixelTests</c>.
/// </para>
/// <para>
/// <b>Three per page</b> (the atlas; WWII has 17 → 6 pages, Korea 16 → 6, Vietnam 17 → 6).  Each row
/// carries the national insignia in a small sunken/raised box (sunken = the SELECTED row, which also
/// gets a teal ► mark), the mission title (bold) with an underline rule, the date (4×6, right), the
/// description word-wrapped to two lines (regular prop3), and the mission's 1–3 difficulty dots at the
/// bottom-right (<c>MissionEntry.DifficultyRating</c>).  Previous Page greys on page 1, Next Page on
/// the last page (the original's own disabled stipple).
/// </para>
/// </remarks>
public sealed class MissionSelectionScreen : FrontEndScreen
{
    /// <summary>How many mission rows a page shows (measured on the original's screen).</summary>
    public const int RowsPerPage = 3;

    /// <summary>The backdrop every panel screen shares.</summary>
    public const string BackdropDocument = "images/smallv.json";

    /// <summary>The insignia strip document.</summary>
    public const string InsigniaDocument = "images/insigv.json";

    /// <summary>The insignia cell stride: 128 px / 4 cells = 32 (<c>image@0x256C2</c>).</summary>
    public const int InsigniaStride = 32;

    /// <summary>The insignia strip's transparent colour-key: palette index 9.</summary>
    public const int InsigniaKey = 9;

    // --- measured layout (all off the original's screen) ---
    private static readonly (int X, int Width, int Height, int Y0, int Pitch) Rows = (15, 230, 34, 48, 38);
    private const int RowFillIndex = 19;             // the row box interior
    private const int RowEdgeLight = 15;             // top + left edge
    private const int RowEdgeDark = 26;              // bottom + right edge
    private const int RowCornerTopRight = 18;
    private const int RowCornerBottomLeft = 24;
    private static readonly (int DX, int DY, int Width, int Height) InBox = (6, 6, 25, 21);  // dx=21−15
    private const int InBoxFillSelected = 23;
    private const int InBoxFillUnselected = 18;
    private const int InsigniaDX = 9;                // ox = row.X + 9 = 24
    private const int InsigniaDY = 11;
    private const int SelectMarkInk = 179;
    private const int SelectMarkEmboss = 26;
    private const int TextX = 54;
    private const int DateRight = 238;
    private const int TitleDY = 3;
    private const int DateDY = 4;
    private const int DescDY = 14;
    private const int DescWrapWidth = 182;           // x54..x236, before the difficulty dots
    private const int RowInk = 189;                  // teal
    private const int RowEmboss = 17;
    private const int DotRightX = 235;               // rightmost difficulty dot's left column
    private const int DotDY = 28;

    private static readonly (int X, int Y, int Width, int Height) PrevBox = (10, 170, 92, 15);
    private static readonly (int X, int Y, int Width, int Height) NextBox = (106, 170, 92, 15);
    private static readonly (int X, int Y, int Width, int Height) ExitBox = (206, 170, 44, 15);

    private readonly IndexedImage _backdrop;
    private readonly IndexedImage _insignia;
    private readonly FrontEndStrings _strings;
    private readonly IReadOnlyList<MissionEntry> _missions;
    private readonly string _title;
    private readonly string _pageFormat;
    private int _page;
    private int _row;

    /// <summary>Builds the screen for one era from the tree's catalogue.</summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="era">The era whose missions to page through.</param>
    public MissionSelectionScreen(DataTree tree, FrontEndStrings strings, MissionEra era)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(strings);
        _backdrop = tree.ImagePixels(BackdropDocument);
        _insignia = tree.ImagePixels(InsigniaDocument);
        _strings = strings;
        _missions = tree.Scenarios.ForEra(era);
        _title = strings[FrontEndStrings.MissionSelection];
        _pageFormat = strings.AtDgroup(FrontEndStrings.PageFormatDgroup);
        Era = era;

        // The three command buttons live in the base ring only so their LETTERS (P/N/E) work; row
        // navigation and Select are handled by the overridden Press below.
        Add(new FrontEndButton(
            FrontEndCommand.PreviousPage, PrevBox.X, PrevBox.Y, PrevBox.Width, PrevBox.Height,
            strings[FrontEndStrings.PreviousPage], FrontEndButtonStyle.Command) { Letter = 'P' });
        Add(new FrontEndButton(
            FrontEndCommand.NextPage, NextBox.X, NextBox.Y, NextBox.Width, NextBox.Height,
            strings[FrontEndStrings.NextPage], FrontEndButtonStyle.Command) { Letter = 'N' });
        Add(new FrontEndButton(
            FrontEndCommand.Ok, ExitBox.X, ExitBox.Y, ExitBox.Width, ExitBox.Height,
            strings.AtDgroup(FrontEndStrings.ExitDgroup), FrontEndButtonStyle.Command) { Letter = 'E' });
        UpdatePageButtons();
    }

    /// <inheritdoc/>
    public override string Name => "MISSION SELECTION";

    /// <summary>The era this list shows.</summary>
    public MissionEra Era { get; }

    /// <summary>The era's missions, filtered and in slot order (what the picker pages through).</summary>
    public IReadOnlyList<MissionEntry> Missions => _missions;

    /// <summary>How many missions the era holds.</summary>
    public int MissionCount => _missions.Count;

    /// <summary>How many pages the era fills, three per page (at least one).</summary>
    public int PageCount => Math.Max(1, (_missions.Count + RowsPerPage - 1) / RowsPerPage);

    /// <summary>The page currently shown (0-based).</summary>
    public int Page => _page;

    /// <summary>The selected row within the page (0-based).</summary>
    public int SelectedRow => _row;

    /// <summary>How many rows this page actually shows (the last page may be short).</summary>
    public int RowsOnPage => Math.Min(RowsPerPage, _missions.Count - (_page * RowsPerPage));

    /// <summary>The mission the selected row names, or <see langword="null"/> if the era is empty.</summary>
    public MissionEntry? SelectedEntry
    {
        get
        {
            int index = (_page * RowsPerPage) + _row;
            return index >= 0 && index < _missions.Count ? _missions[index] : null;
        }
    }

    /// <summary>Turns to the previous page (no-op on the first), selecting its last row.</summary>
    public void PreviousPage()
    {
        if (_page > 0)
        {
            _page--;
            _row = RowsOnPage - 1;
            UpdatePageButtons();
        }
    }

    /// <summary>Turns to the next page (no-op on the last), selecting its first row.</summary>
    public void NextPage()
    {
        if (_page < PageCount - 1)
        {
            _page++;
            _row = 0;
            UpdatePageButtons();
        }
    }

    /// <inheritdoc/>
    public override FrontEndCommand Press(FrontEndKey key)
    {
        switch (key)
        {
            case FrontEndKey.Down or FrontEndKey.Right or FrontEndKey.Next:
                if (_row < RowsOnPage - 1)
                {
                    _row++;
                }
                else
                {
                    NextPage();   // a selection past the page edge pages
                }

                return FrontEndCommand.None;
            case FrontEndKey.Up or FrontEndKey.Left or FrontEndKey.Previous:
                if (_row > 0)
                {
                    _row--;
                }
                else
                {
                    PreviousPage();
                }

                return FrontEndCommand.None;
            case FrontEndKey.Select:
                return SelectedEntry is null
                    ? FrontEndCommand.None
                    : FrontEndCommand.OpenMissionDescription;
            default:
                return Back();
        }
    }

    /// <summary>Esc backs up to CONFLICT SELECTION.</summary>
    protected override FrontEndCommand Back() => FrontEndCommand.Ok;

    /// <summary>
    /// The widget ids are the ORIGINAL's own: 0/1/2 the page and Exit buttons, 3/4/5 the three
    /// mission rows.
    /// </summary>
    /// <remarks>
    /// <c>scenario_bin_load @image@0x246FA</c> installs the six-record array
    /// <c>g_scenario_picker_widgets [0x2EE4]</c> and dispatches the poll's out-param exactly this
    /// way (<c>image@0x248ED</c>): id 0 → <c>scenario_picker_scroll_apply(0, −3)</c>, id 1 →
    /// <c>(0, +3)</c>, id 2 → cancel, ids 3..5 → <c>scroll_top + id − 3</c> is the mission.  The
    /// port's ring is in the same order, so a row's handle is <c>3 + row</c>.
    /// </remarks>
    public const int FirstRowHandle = 3;

    /// <summary>
    /// The rows' own <c>s_ui_widget</c> rectangles, from the shipped array: (16, 49 + 38·row, 228,
    /// 32) — the drawn row box (15, 48 + 38·row, 230, 34) less its ONE-pixel bevel.
    /// </summary>
    private const int RowBevelInset = 1;

    /// <inheritdoc/>
    public override int HitTest(int x, int y)
    {
        int button = base.HitTest(x, y);
        if (button != NoWidget)
        {
            return button;
        }

        for (int row = 0; row < RowsOnPage; row++)
        {
            (int rx, int ry, int rw, int rh) = RowRect(row);
            if (x >= rx && x < rx + rw && y >= ry && y < ry + rh)
            {
                return FirstRowHandle + row;
            }
        }

        return NoWidget;
    }

    /// <inheritdoc/>
    public override void Hover(int handle)
    {
        if (handle < FirstRowHandle)
        {
            base.Hover(handle);
            return;
        }

        int row = handle - FirstRowHandle;
        if (row < RowsOnPage)
        {
            _row = row;
        }
    }

    /// <summary>
    /// A mission row opens on ONE click, exactly as the original's list does.
    /// </summary>
    /// <remarks>
    /// Not a guess and not a UI convention: <c>scenario_bin_load</c>'s mouse arm
    /// (<c>image@0x248ED</c>) takes ids 3..5 straight to <c>select_and_validate_pick</c>
    /// (<c>image@0x2490E</c>) — the same label the <b>Enter</b> key jumps to
    /// (<c>image@0x248BE</c>) — which publishes the record and sets the picker's return flag to 1,
    /// i.e. the screen closes and the briefing opens.  There is no second-click state anywhere in
    /// the loop.
    /// </remarks>
    /// <param name="handle">The widget.</param>
    public override FrontEndCommand Click(int handle)
    {
        if (handle < FirstRowHandle)
        {
            // NOT base.Click: this screen's Select is the LIST's ("open the selected row"), so a
            // click on a command button must fire that button's own command instead.
            FocusOn(handle);
            return Activate(handle);
        }

        int row = handle - FirstRowHandle;
        if (row >= RowsOnPage)
        {
            return FrontEndCommand.None;
        }

        _row = row;
        return SelectedEntry is null
            ? FrontEndCommand.None
            : FrontEndCommand.OpenMissionDescription;
    }

    /// <inheritdoc/>
    public override bool Accepts(int handle) =>
        handle >= FirstRowHandle
            ? handle - FirstRowHandle < RowsOnPage
            : base.Accepts(handle);

    /// <summary>
    /// The pointer parks on the SELECTED ROW here, because that is what this screen's Tab moves
    /// (the marks are the list's selected row — F1 finding 1) and what Space opens.
    /// </summary>
    public override (int X, int Y) PointerTarget
    {
        get
        {
            (int rx, int ry, int rw, int rh) = RowRect(_row);
            return (rx + rw - FrontEndPointer.PlacementInset, ry + rh - FrontEndPointer.PlacementInset);
        }
    }

    /// <inheritdoc/>
    public override string LabelOf(int handle)
    {
        if (handle < FirstRowHandle)
        {
            return base.LabelOf(handle);
        }

        int index = (_page * RowsPerPage) + handle - FirstRowHandle;
        return index >= 0 && index < _missions.Count ? _missions[index].Title : string.Empty;
    }

    /// <summary>One row's <c>s_ui_widget</c> rectangle.</summary>
    /// <param name="row">The row on the page, 0-based.</param>
    private static (int X, int Y, int Width, int Height) RowRect(int row) =>
        (Rows.X + RowBevelInset,
         Rows.Y0 + (row * Rows.Pitch) + RowBevelInset,
         Rows.Width - (2 * RowBevelInset),
         Rows.Height - (2 * RowBevelInset));

    /// <inheritdoc/>
    public override void Render(in FrontEndPainter.Surface surface, FrontEndFonts fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        FrontEndPainter.Backdrop(surface, _backdrop);

        // Title, centred on the panel's interior (x=130), with its underline rule.
        int titleX = 130 - (FrontEndPainter.Measure(fonts.Bold, _title) / 2);
        FrontEndPainter.Rule(
            surface, fonts.Bold, _title, titleX, 37,
            FrontEndPainter.TitleInkIndex, FrontEndPainter.TitleEmbossIndex);

        // PAGE n OF m, small font, right-aligned to x245.
        string page = FrontEndStrings.FormatPrintf(_pageFormat, _page + 1, PageCount);
        FrontEndPainter.Text(
            surface, fonts.Small, page, 245 - FrontEndPainter.Measure(fonts.Small, page), 38,
            FrontEndPainter.TitleInkIndex, FrontEndPainter.TitleEmbossIndex);

        int start = _page * RowsPerPage;
        for (int i = 0; i < RowsPerPage && start + i < _missions.Count; i++)
        {
            RenderRow(surface, fonts, _missions[start + i], Rows.Y0 + (i * Rows.Pitch), i == _row);
        }

        // The groove between the page buttons and Exit (measured on the original's screen).
        surface.Fill(201, 168, 2, 19, 29);
        surface.Fill(203, 168, 1, 19, 21);

        foreach (FrontEndButton button in Buttons)
        {
            button.Render(surface, fonts.Bold, focused: false);
        }
    }

    private void RenderRow(
        in FrontEndPainter.Surface surface,
        FrontEndFonts fonts,
        MissionEntry mission,
        int y0,
        bool selected)
    {
        // The row box: a 1-pixel raised bevel (light top-left, dark bottom-right) over fill 19.
        surface.Fill(Rows.X, y0, Rows.Width, Rows.Height, RowFillIndex);
        surface.Fill(Rows.X, y0, Rows.Width, 1, RowEdgeLight);
        surface.Fill(Rows.X, y0, 1, Rows.Height, RowEdgeLight);
        surface.Fill(Rows.X, y0 + Rows.Height - 1, Rows.Width, 1, RowEdgeDark);
        surface.Fill(Rows.X + Rows.Width - 1, y0, 1, Rows.Height, RowEdgeDark);
        surface.Pixel(Rows.X + Rows.Width - 1, y0, RowCornerTopRight);
        surface.Pixel(Rows.X, y0 + Rows.Height - 1, RowCornerBottomLeft);

        // The insignia box: standard widget bevel, sunken when the row is selected.
        int ibx = Rows.X + InBox.DX;
        int iby = y0 + InBox.DY;
        FrontEndPainter.Bevel(
            surface, ibx, iby, InBox.Width, InBox.Height,
            selected ? FrontEndPainter.Sunken : FrontEndPainter.Raised,
            selected ? InBoxFillSelected : InBoxFillUnselected);
        FrontEndPainter.InsigniaStrip(
            surface, _insignia, mission.InsigniaIndex, InsigniaStride, InsigniaKey,
            Rows.X + InsigniaDX, y0 + InsigniaDY);
        if (selected)
        {
            RenderSelectMark(surface, Rows.X + InsigniaDX, y0 + 9);
        }

        // Title (bold) with its underline rule, and the date (small) right-aligned.
        FrontEndPainter.Rule(surface, fonts.Bold, mission.Title, TextX, y0 + TitleDY, RowInk, RowEmboss);
        FrontEndPainter.Text(
            surface, fonts.Small, mission.Date,
            DateRight - FrontEndPainter.Measure(fonts.Small, mission.Date), y0 + DateDY,
            RowInk, RowEmboss);

        // Description, word-wrapped to two lines (regular font).
        IReadOnlyList<string> lines = FrontEndText.Wrap(fonts.Prop, mission.Description, DescWrapWidth, 2);
        for (int i = 0; i < lines.Count; i++)
        {
            FrontEndPainter.Text(
                surface, fonts.Prop, lines[i], TextX, y0 + DescDY + (i * (fonts.Prop.Height + 1)),
                RowInk, RowEmboss);
        }

        // Difficulty: 1..3 dots, 2×2 each, right-aligned, embossed.
        for (int k = 0; k < mission.DifficultyRating; k++)
        {
            int dx = DotRightX - (k * 3);
            surface.Fill(dx, y0 + DotDY + 2, 2, 1, RowEmboss);
            surface.Fill(dx, y0 + DotDY, 2, 2, RowInk);
        }
    }

    // The teal ► that marks the SELECTED row, drawn embossed in the insignia box's top-left corner.
    private static void RenderSelectMark(in FrontEndPainter.Surface surface, int mx, int my)
    {
        ReadOnlySpan<(int X, int Y)> triangle =
            [(0, 0), (1, 0), (2, 0), (0, 1), (1, 1), (0, 2)];
        foreach ((int dx, int dy) in triangle)
        {
            surface.Pixel(mx + dx, my + dy + 1, SelectMarkEmboss);
        }

        foreach ((int dx, int dy) in triangle)
        {
            surface.Pixel(mx + dx, my + dy, SelectMarkInk);
        }
    }

    private void UpdatePageButtons()
    {
        Buttons[0].Enabled = _page > 0;                    // Previous Page
        Buttons[0].DisabledReason = _page > 0 ? string.Empty : "already on the first page";
        Buttons[1].Enabled = _page < PageCount - 1;        // Next Page
        Buttons[1].DisabledReason = _page < PageCount - 1 ? string.Empty : "already on the last page";
    }
}
