using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Mission;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>Which picker the cascade is showing.</summary>
public enum CreateMissionPicker
{
    /// <summary>The six flyables (<c>DS:0x4C32</c>), shown when the value array is empty.</summary>
    Aircraft,

    /// <summary>2,500 … 40,000 feet (<c>DS:0x4C3E</c>), after the aircraft.</summary>
    Altitude,

    /// <summary>jumped / saw / was jumped by (<c>DS:0x4C56</c>), after the altitude.</summary>
    Verb,

    /// <summary>one … five (<c>DS:0x4C5C</c>), after the verb OR after an "and".</summary>
    Count,

    /// <summary>The seventeen opponents (<c>DS:0x4C66</c>), after a count.</summary>
    Enemy,

    /// <summary>"." or "and" (<c>DS:0x4C88</c>), after an enemy — unless three clauses are in.</summary>
    Conjunction,

    /// <summary>amateur … excellent (<c>DS:0x4C8C</c>), after the ".".</summary>
    Skill,

    /// <summary>
    /// The DONE state: no rows at all, <c>Done</c> enabled and focused
    /// (<c>create_mission_widget_dispatch</c>'s type-7 arm calls the picker with
    /// <c>BX = DX = 0</c>, <c>image@0x27892</c>).
    /// </summary>
    Done,
}

/// <summary>
/// CREATE MISSION: the sentence's picker cascade (<c>ui_create_mission_form @image@0x2769E</c> +
/// <c>create_mission_widget_dispatch @image@0x27796</c> + <c>create_mission_picker_one
/// @image@0x27987</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The screen IS the form.</b>  It owns the original's own state — the stride-2 value array
/// <c>g_create_mission_value_array [0xC2E4]</c> and its pair count <c>[0xC2E2]</c> — and every
/// transition below is one the bytes state.  <see cref="Picks"/> inverts the array into C1's
/// <see cref="CustomMissionPicks"/> once the cascade reaches <see cref="CreateMissionPicker.Done"/>.
/// </para>
/// <para>
/// <b>Every number here was measured off captured frames of the original</b>, which the rules
/// below rebuild and diff at 1×.
/// </para>
/// <para>
/// <b>The port commits a row at once.</b> The original runs two more frames after a commit (<c>SI =
/// 0</c> then the <c>SI &gt;= 2</c> exit, <c>image@0x27BA4</c>/<c>image@0x27BB5</c>); that is a redraw
/// delay, not a rule, and nothing observable depends on it.
/// </para>
/// </remarks>
public sealed class CreateMissionScreen : FrontEndScreen
{
    /// <summary>The backdrop every panel screen shares.</summary>
    public const string BackdropDocument = "images/smallv.json";

    /// <summary>
    /// The picker panel's widget rectangle <c>g_create_mission_picker_panel_rect [0x4BE8]</c> —
    /// bytes <c>15 00 4B 00 DC 00 53 00</c> at <c>image@0x40948</c>, drawn by
    /// <c>widget_beveled_box_draw</c> at <c>image@0x279E1</c>.
    /// </summary>
    public static readonly (int X, int Y, int Width, int Height) Panel = (21, 75, 220, 83);

    /// <summary>The row pitch inside a column: 15 (<c>add dx,0xF</c> @<c>image@0x2794F</c>).</summary>
    public const int RowPitch = 15;

    /// <summary>The gap between columns: 7 (<c>add ax,7</c> @<c>image@0x27968</c>).</summary>
    public const int ColumnGap = 7;

    /// <summary>
    /// Twice the column the grid is centred on: <c>0x86</c> (<c>add ax,0x86</c>
    /// @<c>image@0x278C2</c>) — the grid's first column is
    /// <c>0x86 + (columns × −(width+7)) / 2</c>.
    /// </summary>
    public const int GridCentreX = 0x86;

    /// <summary>
    /// The row the grid is centred on: <c>0x76</c> (<c>sub ax,0x76 / neg ax</c>
    /// @<c>image@0x278DC</c>).
    /// </summary>
    public const int GridCentreY = 0x76;

    /// <summary>
    /// A picker row's height.  The layout writes <c>rect_h = 0xFFFF</c>
    /// (<c>image@0x2793B</c>) and the toolkit's own default takes over:
    /// <c>widget_one_init</c>'s <c>MOV word [SI+0xE],0x0B</c> (<c>image@0x2E228</c>) — eleven, which
    /// is what the frames measure.
    /// </summary>
    public const int RowHeight = 11;

    /// <summary>The sentence's pen column (<c>push 0x15</c> @<c>image@0x27E5D</c>).</summary>
    public const int SentenceX = 21;

    /// <summary>Its first pen row (<c>push 0x2A</c> @<c>image@0x27E65</c>).</summary>
    public const int SentenceY = 42;

    /// <summary>
    /// Its right edge (<c>push 0xF0</c> @<c>image@0x27E61</c>): the usable width is
    /// <c>SentenceRight − SentenceX</c> = 219, which the atlas brackets to 213..220.
    /// </summary>
    public const int SentenceRight = 0xF0;

    /// <summary>Its line pitch, measured on the three-line frames: 9.</summary>
    public const int SentencePitch = 9;

    /// <summary>The opening quote's pen, drawn on its own (<c>image@0x27E45..0x27E52</c>).</summary>
    public static readonly (int X, int Y) QuotePen = (16, 42);

    /// <summary>The sentence's ink — the game's teal, measured.</summary>
    public const int SentenceInkIndex = 189;

    /// <summary>Its emboss.</summary>
    public const int SentenceEmbossIndex = 18;

    /// <summary>A FOCUSED picker row's interior — the sunken fill, measured.</summary>
    public const int RowSunkenFillIndex = 23;

    /// <summary>A focused row's label ink (on that fill).</summary>
    public const int RowFocusInkIndex = 15;

    /// <summary>Its emboss.</summary>
    public const int RowFocusEmbossIndex = 26;

    /// <summary>A focused row's <c>►</c> pen, relative to the row rectangle's left edge.</summary>
    public const int RowMarkInset = 1;

    /// <summary>Its <c>◄</c> pen is <c>rect.w −</c> this.</summary>
    public const int RowMarkRightInset = 4;

    /// <summary>
    /// The vertical divider between <c>Back Up</c> and <c>Exit</c>: two columns of index 29 and one
    /// of 21, rows 168..186.
    /// </summary>
    /// <remarks>
    /// Its X is the ARGUMENT of <c>panel_divider_stripe_draw(0x96)</c> (<c>image@0x276FF</c>):
    /// <c>0x96</c> is 150, exactly where the frames put the stripe.  The decoded file reads that
    /// word as a <c>colour_idx</c>; it is a column (reported).
    /// </remarks>
    public static readonly (int X, int Y, int Height) Divider = (0x96, 168, 19);

    private const int DividerDarkIndex = 29;
    private const int DividerLightIndex = 21;

    /// <summary>The widget id of <c>Back Up</c> — template record 0 (<c>image@0x40950</c>).</summary>
    public const int BackUpButtonIndex = 0;

    /// <summary>The widget id of <c>Exit</c> — template record 1.</summary>
    public const int ExitButtonIndex = 1;

    /// <summary>The widget id of <c>Done</c> — template record 2.</summary>
    public const int DoneButtonIndex = 2;

    /// <summary>
    /// The first picker row's widget id: 3 (<c>al = index + 3</c> @<c>image@0x2791B</c>) — the same
    /// numbering MISSION SELECTION's rows use, and for the same reason (the three template widgets
    /// come first in the buffer).
    /// </summary>
    public const int FirstRowHandle = 3;

    // The three template widgets' own rectangles, image@0x40950 (66 B, three s_ui_widget).
    private static readonly (int X, int Y, int Width, int Height) BackUpRect = (12, 172, 130, RowHeight);
    private static readonly (int X, int Y, int Width, int Height) ExitRect = (160, 172, 40, RowHeight);
    private static readonly (int X, int Y, int Width, int Height) DoneRect = (208, 172, 40, RowHeight);

    private readonly IndexedImage _backdrop;
    private readonly FrontEndStrings _strings;
    private readonly List<CreateMissionPair> _pairs = [];
    private readonly List<(int X, int Y, int Width, int Height)> _rows = [];
    private readonly List<string> _lines = [];
    private CreateMissionPicker _picker;
    private int _selection;

    /// <summary>Builds the form on its first picker.</summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="strings">The label catalogue.</param>
    public CreateMissionScreen(DataTree tree, FrontEndStrings strings)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(strings);
        _backdrop = tree.ImagePixels(BackdropDocument);
        _strings = strings;

        Add(Button(FrontEndCommand.CreateMissionBackUp, BackUpRect, strings.AtDgroupRun(
            CreateMissionSentence.BackUpDgroup)));
        Add(Button(FrontEndCommand.Ok, ExitRect, strings.AtDgroupRun(FrontEndStrings.ExitDgroup)));
        Add(Button(FrontEndCommand.FlyCustomMission, DoneRect, strings[FrontEndStrings.Done]));
        Advance();
    }

    /// <inheritdoc/>
    public override string Name => "CREATE MISSION";

    /// <summary>Which picker is up.</summary>
    public CreateMissionPicker Picker => _picker;

    /// <summary>The value array's filled pairs, in order — the original's <c>[0xC2E4]</c>.</summary>
    public IReadOnlyList<CreateMissionPair> Pairs => _pairs;

    /// <summary>The sentence as it now reads, without the opening quote.</summary>
    public string Sentence => CreateMissionSentence.Compose(_strings, _pairs);

    /// <summary>How many rows the current picker offers (0 in the DONE state).</summary>
    public int RowCount => _rows.Count;

    /// <summary>
    /// Which widget the <c>► ◄</c> marks are on: 0..2 for the buttons, <c>3 + row</c> for a row.
    /// </summary>
    public int Selection => _selection;

    /// <summary>Whether the cascade has reached the DONE state, so <c>Done</c> would fly.</summary>
    public bool CanFly => _picker == CreateMissionPicker.Done;

    /// <summary>
    /// The finished pick set — valid only once <see cref="CanFly"/> is true.
    /// </summary>
    /// <exception cref="InvalidOperationException">The cascade has not reached DONE.</exception>
    public CustomMissionPicks Picks
    {
        get
        {
            if (!CanFly)
            {
                throw new InvalidOperationException(
                    "the CREATE MISSION form has not reached its DONE state; the value array is "
                        + "incomplete (ui_create_mission_form only flies when the last pair's type "
                        + "byte is 7, image@0x2774F).");
            }

            return CustomMissionPicks.FromValueArray(ValueArray());
        }
    }

    /// <summary>The value array as the original's 2-byte pairs — the Last-Mission wire form.</summary>
    /// <returns>The bytes, <c>2 × Pairs.Count</c> of them.</returns>
    public byte[] ValueArray()
    {
        byte[] bytes = new byte[_pairs.Count * 2];
        for (int i = 0; i < _pairs.Count; i++)
        {
            bytes[i * 2] = _pairs[i].Type;
            bytes[(i * 2) + 1] = _pairs[i].Row;
        }

        return bytes;
    }

    /// <summary>
    /// The option table, rows per column and column width of one picker — the seven arms of
    /// <c>create_mission_widget_dispatch</c> (<c>image@0x277D3..0x27890</c>).
    /// </summary>
    /// <param name="picker">Which picker.</param>
    /// <returns>Its type byte, its options, its <c>items_per_col</c> and its <c>col_width_px</c>.</returns>
    public static (byte Type, IReadOnlyList<CustomMissionOption> Options, int ItemsPerColumn, int ColumnWidth)
        Descriptor(CreateMissionPicker picker) => picker switch
        {
            // image@0x2783E: ax=0, bx=0x4C32, dx=6, pushes 2 then 0x38
            CreateMissionPicker.Aircraft =>
                (CustomMissionPicks.AircraftType, CustomMissionVocabulary.PlayerAircraft, 2, 0x38),

            // image@0x277F0: ax=1, bx=0x4C3E, dx=6 (borrowed at image@0x2784C), pushes 3 then 0x4D
            CreateMissionPicker.Altitude =>
                (CustomMissionPicks.AltitudeType, CustomMissionVocabulary.Altitude, 3, 0x4D),

            // image@0x27801: ax=2, bx=0x4C56, dx=3, pushes 3 then 0x5D
            CreateMissionPicker.Verb =>
                (CustomMissionPicks.VerbType, CustomMissionVocabulary.Verb, 3, 0x5D),

            // image@0x27816: ax=3, bx=0x4C5C, dx=5, pushes 5 then 0x2D
            CreateMissionPicker.Count =>
                (CustomMissionPicks.CountType, CustomMissionVocabulary.Count, 5, 0x2D),

            // image@0x2782A: ax=4, bx=0x4C66, dx=0x11, pushes 5 then 0x2E
            CreateMissionPicker.Enemy =>
                (CustomMissionPicks.EnemyType, CustomMissionVocabulary.Enemy, 5, 0x2E),

            // image@0x27851: ax=0 (the row VALUE overwrites it), bx=0x4C88, dx=2, pushes 2 then 0x28
            CreateMissionPicker.Conjunction =>
                (CustomMissionPicks.AircraftType, CustomMissionVocabulary.Conjunction, 2, 0x28),

            // image@0x2787E: ax=7, bx=0x4C8C, dx=4, pushes 5 then 0x41
            CreateMissionPicker.Skill =>
                (CustomMissionPicks.SkillType, CustomMissionVocabulary.Skill, 5, 0x41),

            _ => (CustomMissionPicks.AircraftType, [], 0, 0),
        };

    /// <summary>
    /// <c>create_mission_picker_layout @image@0x278A2</c> — every row's rectangle, column-major.
    /// </summary>
    /// <param name="count">How many options the picker has.</param>
    /// <param name="itemsPerColumn">Its <c>items_per_col</c>.</param>
    /// <param name="columnWidth">Its <c>col_width_px</c>.</param>
    /// <param name="into">The list the rectangles are appended to (cleared first).</param>
    /// <remarks>
    /// <c>columns = ceil(count / items_per_col)</c> (<c>image@0x278B9</c>); the first column's X is
    /// <c>0x86 + (columns × −(width+7)) / 2</c> and the first row's Y is
    /// <c>0x76 − (min(items_per_col, count) × 15) / 2</c>, both with C's truncate-toward-zero
    /// division (<c>cdq / sub ax,dx / sar ax,1</c>).
    /// </remarks>
    public static void Layout(
        int count,
        int itemsPerColumn,
        int columnWidth,
        List<(int X, int Y, int Width, int Height)> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (count <= 0 || itemsPerColumn <= 0)
        {
            return;
        }

        int columns = (itemsPerColumn + count - 1) / itemsPerColumn;
        int x = GridCentreX + (columns * -(columnWidth + ColumnGap) / 2);
        int firstY = GridCentreY - (Math.Min(itemsPerColumn, count) * RowPitch / 2);
        int y = firstY;
        int inColumn = 0;
        for (int i = 0; i < count; i++)
        {
            into.Add((x, y, columnWidth, RowHeight));
            y += RowPitch;
            if (++inColumn < itemsPerColumn)
            {
                continue;
            }

            inColumn = 0;
            y = firstY;
            x += columnWidth + ColumnGap;
        }
    }

    /// <summary>
    /// <c>Back Up</c> — <c>ui_create_mission_form</c>'s <c>AX == 0</c> arm
    /// (<c>image@0x27719..0x27740</c>): drop the last pick, and drop the SYNTHESISED "." pair with
    /// it when three clauses are in.
    /// </summary>
    public void BackUp()
    {
        if (_pairs.Count == 0)
        {
            return;   // image@0x2771D: cmp [0xC2E2],ax / jle — nothing to undo
        }

        // image@0x27725: only when create_mission_count_sum's CLAUSE count has reached three, and
        // only when the last pair is the "." the type-4 arm wrote for you, is a SECOND pair dropped.
        if (CreateMissionSentence.ClauseCount(_pairs) >= CustomMissionPicks.MaxClauses
            && _pairs[^1].Type == CustomMissionPicks.StopType)
        {
            _pairs.RemoveAt(_pairs.Count - 1);      // image@0x27738
        }

        _pairs.RemoveAt(_pairs.Count - 1);          // image@0x2773C
        Advance();
    }

    /// <summary>Commits the focused row, or the row at an index.</summary>
    /// <param name="row">Which row of the current picker.</param>
    /// <returns>Whether it was a real row.</returns>
    public bool Commit(int row)
    {
        (byte type, IReadOnlyList<CustomMissionOption> options, _, _) = Descriptor(_picker);
        if ((uint)row >= (uint)options.Count)
        {
            return false;
        }

        byte value = (byte)options[row].Value;
        _pairs.Add(new CreateMissionPair(
            _picker == CreateMissionPicker.Conjunction ? value : type, value));

        // image@0x27872..0x27876 — the conjunction picker writes type 0 and then copies its ROW
        // byte over its own type byte, so the pair becomes (6,6) "." or (5,5) "and"; the value IS
        // the next picker's type.  Done above by writing `value` as the type directly.
        Advance();
        return true;
    }

    /// <inheritdoc/>
    public override FrontEndCommand Press(FrontEndKey key)
    {
        switch (key)
        {
            case FrontEndKey.Up:
                StepRow(-1);
                return FrontEndCommand.None;
            case FrontEndKey.Down:
                StepRow(1);
                return FrontEndCommand.None;
            case FrontEndKey.Left:
                StepColumn(-1);
                return FrontEndCommand.None;
            case FrontEndKey.Right:
                StepColumn(1);
                return FrontEndCommand.None;
            case FrontEndKey.Home:
                if (_rows.Count > 0)
                {
                    Select(FirstRowHandle);
                }

                return FrontEndCommand.None;
            case FrontEndKey.End:
                if (_rows.Count > 0)
                {
                    Select(FirstRowHandle + _rows.Count - 1);
                }

                return FrontEndCommand.None;
            case FrontEndKey.Next:
                StepRing(1);
                return FrontEndCommand.None;
            case FrontEndKey.Previous:
                StepRing(-1);
                return FrontEndCommand.None;
            case FrontEndKey.BackUp:
                return Buttons[BackUpButtonIndex].Enabled
                    ? FrontEndCommand.CreateMissionBackUp
                    : FrontEndCommand.None;
            case FrontEndKey.Select:
                return Fire(_selection);
            case FrontEndKey.Back:
                return Back();
            default:
                return FrontEndCommand.None;
        }
    }

    /// <summary>
    /// The picker rows carry a DIGIT accelerator of their own: <c>si[0x10] = index + 0x31</c> for
    /// the first ten rows (<c>image@0x27940..0x2794C</c>), i.e. <c>1</c>..<c>9</c> and then
    /// <c>:</c> for row ten; rows eleven and up have none (the layout's <c>memset</c> left zero).
    /// </summary>
    /// <param name="letter">The typed character.</param>
    /// <returns>What it fired.</returns>
    public override FrontEndCommand Type(char letter)
    {
        for (int row = 0; row < _rows.Count && row < RowAcceleratorCount; row++)
        {
            if (RowAccelerator(row) == letter)
            {
                Select(FirstRowHandle + row);
                return Fire(_selection);
            }
        }

        return base.Type(letter);
    }

    /// <summary>How many rows get a digit accelerator: ten (<c>cmp [bp-4],9 / jg</c>).</summary>
    public const int RowAcceleratorCount = 10;

    /// <summary>The accelerator character of a row — <c>'1' + index</c>, so row ten gets <c>:</c>.</summary>
    /// <param name="row">The row index.</param>
    public static char RowAccelerator(int row) => (char)('1' + row);

    /// <summary>Esc is <c>Exit</c>: back to CHOOSE ACTIVITY with nothing remembered.</summary>
    /// <remarks>
    /// Widget 1's accelerator byte is <c>0x1B</c> (<c>image@0x40976</c>), and
    /// a captured frame of the original is CHOOSE ACTIVITY.
    /// </remarks>
    protected override FrontEndCommand Back() => FrontEndCommand.Ok;

    /// <inheritdoc/>
    public override int HitTest(int x, int y)
    {
        int button = base.HitTest(x, y);
        if (button != NoWidget)
        {
            return button;
        }

        for (int row = 0; row < _rows.Count; row++)
        {
            (int rx, int ry, int rw, int rh) = _rows[row];
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
        }

        if (handle >= 0 && handle < FirstRowHandle + _rows.Count)
        {
            Select(handle);
        }
    }

    /// <inheritdoc/>
    public override FrontEndCommand Click(int handle)
    {
        if (handle < 0 || handle >= FirstRowHandle + _rows.Count)
        {
            return FrontEndCommand.None;
        }

        Hover(handle);
        return Fire(handle);
    }

    /// <inheritdoc/>
    public override bool Accepts(int handle) =>
        handle >= FirstRowHandle
            ? handle - FirstRowHandle < _rows.Count
            : base.Accepts(handle);

    /// <inheritdoc/>
    public override string LabelOf(int handle)
    {
        if (handle < FirstRowHandle)
        {
            return base.LabelOf(handle);
        }

        int row = handle - FirstRowHandle;
        (_, IReadOnlyList<CustomMissionOption> options, _, _) = Descriptor(_picker);
        return row < options.Count ? _strings[options[row].StringIndex] : string.Empty;
    }

    /// <inheritdoc/>
    public override (int X, int Y) PointerTarget
    {
        get
        {
            if (_selection < FirstRowHandle)
            {
                return Buttons[_selection].PointerPlacement;
            }

            (int rx, int ry, int rw, int rh) = _rows[_selection - FirstRowHandle];
            return (rx + rw - FrontEndPointer.PlacementInset, ry + rh - FrontEndPointer.PlacementInset);
        }
    }

    /// <inheritdoc/>
    public override void Render(in FrontEndPainter.Surface surface, FrontEndFonts fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        FrontEndPainter.Backdrop(surface, _backdrop);

        // The sentence: its own opening quote, then the wrapped body (image@0x27E45, image@0x27E6D).
        FrontEndPainter.Text(
            surface, fonts.Prop, _strings.AtDgroupRun(CreateMissionSentence.OpenQuoteDgroup), QuotePen.X, QuotePen.Y,
            SentenceInkIndex, SentenceEmbossIndex);
        CreateMissionSentence.Wrap(fonts.Prop, Sentence, SentenceRight - SentenceX, _lines);
        for (int i = 0; i < _lines.Count; i++)
        {
            FrontEndPainter.Text(
                surface, fonts.Prop, _lines[i], SentenceX, SentenceY + (i * SentencePitch),
                SentenceInkIndex, SentenceEmbossIndex);
        }

        surface.Fill(Divider.X, Divider.Y, 2, Divider.Height, DividerDarkIndex);
        surface.Fill(Divider.X + 2, Divider.Y, 1, Divider.Height, DividerLightIndex);

        FrontEndPainter.ThinBevel(
            surface, Panel.X, Panel.Y, Panel.Width, Panel.Height,
            FrontEndPainter.ThinEdgeLightIndex, FrontEndPainter.ThinEdgeDarkIndex,
            FrontEndPainter.FillIndex);

        (_, IReadOnlyList<CustomMissionOption> options, _, _) = Descriptor(_picker);
        for (int row = 0; row < _rows.Count; row++)
        {
            bool focused = _selection == FirstRowHandle + row;
            (int rx, int ry, int rw, int rh) = _rows[row];
            FrontEndPainter.ThinBevel(
                surface, rx, ry, rw, rh,
                focused ? FrontEndPainter.ThinEdgeDarkIndex : FrontEndPainter.ThinEdgeLightIndex,
                focused ? FrontEndPainter.ThinEdgeLightIndex : FrontEndPainter.ThinEdgeDarkIndex,
                focused ? RowSunkenFillIndex : FrontEndPainter.FillIndex);

            int ink = focused ? RowFocusInkIndex : FrontEndPainter.TitleInkIndex;
            int emboss = focused ? RowFocusEmbossIndex : FrontEndPainter.TitleEmbossIndex;
            string caption = _strings[options[row].StringIndex];
            int penY = ry + FrontEndButton.LabelRowFor(rh, fonts.Bold.Height);
            FrontEndPainter.Text(
                surface, fonts.Bold, caption,
                rx + (rw / 2) - (FrontEndPainter.Measure(fonts.Bold, caption) / 2), penY, ink, emboss);
            if (!focused)
            {
                continue;
            }

            FrontEndPainter.Text(
                surface, fonts.Bold, FrontEndPainter.FocusMarkLeftText,
                rx + RowMarkInset, penY, ink, emboss);
            FrontEndPainter.Text(
                surface, fonts.Bold, FrontEndPainter.FocusMarkRightText,
                rx + rw - RowMarkRightInset, penY, ink, emboss);
        }

        for (int i = 0; i < Buttons.Count; i++)
        {
            Buttons[i].Render(surface, fonts.Bold, i == _selection);
        }
    }

    private static FrontEndButton Button(
        FrontEndCommand command, (int X, int Y, int Width, int Height) rect, string label) =>
        new(
            command,
            rect.X - FrontEndButton.BevelInset,
            rect.Y - FrontEndButton.BevelInset,
            rect.Width + (2 * FrontEndButton.BevelInset),
            rect.Height + (2 * FrontEndButton.BevelInset),
            label,
            FrontEndButtonStyle.Command);

    private FrontEndCommand Fire(int handle)
    {
        if (handle >= FirstRowHandle)
        {
            Commit(handle - FirstRowHandle);
            return FrontEndCommand.None;
        }

        return Activate(handle);
    }

    /// <summary>
    /// <c>create_mission_widget_dispatch @image@0x27796</c> — which picker the LAST committed
    /// pair's type byte calls for (<c>al = value_array[(idx−1)*2]</c>, <c>image@0x277AD</c>).
    /// </summary>
    private void Advance()
    {
        while (true)
        {
            if (_pairs.Count == 0)
            {
                Show(CreateMissionPicker.Aircraft);      // image@0x277A4 -> image@0x2783E
                return;
            }

            switch (_pairs[^1].Type)
            {
                case CustomMissionPicks.AircraftType:
                    Show(CreateMissionPicker.Altitude);
                    return;
                case CustomMissionPicks.AltitudeType:
                    Show(CreateMissionPicker.Verb);
                    return;
                case CustomMissionPicks.VerbType:
                case CustomMissionPicks.AndType:         // image@0x27816 serves both arms
                    Show(CreateMissionPicker.Count);
                    return;
                case CustomMissionPicks.CountType:
                    Show(CreateMissionPicker.Enemy);
                    return;
                case CustomMissionPicks.EnemyType:
                    // image@0x277D3 — with three clauses in, the form does NOT offer the
                    // conjunction: it writes the "." pair ITSELF and re-enters the dispatcher, so
                    // the skill picker comes next
                    // (a captured frame of the original).
                    if (CreateMissionSentence.ClauseCount(_pairs) >= CustomMissionPicks.MaxClauses)
                    {
                        _pairs.Add(new CreateMissionPair(
                            CustomMissionPicks.StopType, CustomMissionPicks.StopType));
                        continue;
                    }

                    Show(CreateMissionPicker.Conjunction);
                    return;
                case CustomMissionPicks.StopType:
                    Show(CreateMissionPicker.Skill);
                    return;
                default:
                    Show(CreateMissionPicker.Done);      // type 7, image@0x27892
                    return;
            }
        }
    }

    private void Show(CreateMissionPicker picker)
    {
        _picker = picker;
        (_, IReadOnlyList<CustomMissionOption> options, int itemsPerColumn, int columnWidth) = Descriptor(picker);
        Layout(options.Count, itemsPerColumn, columnWidth, _rows);

        // image@0x27A59 / image@0x27A6F — Back Up is live once anything has been picked; Done only
        // in the DONE state.  (AL = 1 CLEARS the disabled bit 0x20, image@0x2EA5D.)
        Buttons[BackUpButtonIndex].Enabled = _pairs.Count > 0;
        Buttons[BackUpButtonIndex].DisabledReason = _pairs.Count > 0
            ? string.Empty
            : "nothing has been picked yet (image@0x27A59: AL = [0xC2E2] > 0)";
        Buttons[DoneButtonIndex].Enabled = picker == CreateMissionPicker.Done;
        Buttons[DoneButtonIndex].DisabledReason = picker == CreateMissionPicker.Done
            ? string.Empty
            : "the sentence is not finished (image@0x27A6F: AL = the last pick's type == 7)";

        // image@0x27A27 — the DONE state focuses widget 2 (buf+0x2C); every picker focuses row 0.
        Select(picker == CreateMissionPicker.Done ? DoneButtonIndex : FirstRowHandle);
    }

    /// <summary>
    /// Moves the one selection, and keeps the base ring's <see cref="FrontEndScreen.Focus"/> on a
    /// LIVE button while it is on a row.
    /// </summary>
    /// <remarks>
    /// The base ring only knows the three command buttons, and <c>FrontEnd.Resolve</c> reads
    /// <c>Focused</c> to explain a refused Select.  A row's Select is never a refusal, so parking
    /// the ring on <c>Exit</c> — the one button that is live in every state — is what stops a row
    /// commit from logging "Done is not available".
    /// </remarks>
    /// <param name="handle">The widget: 0..2 a button, <c>3 + row</c> a row.</param>
    private void Select(int handle)
    {
        _selection = handle;
        FocusOn(handle < Buttons.Count ? handle : ExitButtonIndex);
    }

    private void StepRow(int by)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        int row = Math.Max(0, _selection - FirstRowHandle);
        row = ((row + by) % _rows.Count + _rows.Count) % _rows.Count;
        Select(FirstRowHandle + row);
    }

    /// <summary>
    /// Left / Right move by a whole COLUMN and wrap through the grid's empty corner.
    /// </summary>
    /// <remarks>
    /// Measured: from Yak-9 (row 16 = column 3, slot 1) <c>Right</c> lands on B-29 (row 1 = column
    /// 0, slot 1) and <c>Left</c> on MiG-15 (row 11); from MiG-17 (row 12 = column 2, slot 2)
    /// <c>Right</c> lands on B-52 (row 2) because column 3 has no slot 2 — i.e. the step is
    /// <c>(row ± items_per_col) mod (columns × items_per_col)</c>, repeated until it lands on a row
    /// that exists (verified on the original's screen).
    /// </remarks>
    /// <param name="by">−1 for Left, +1 for Right.</param>
    private void StepColumn(int by)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        (_, IReadOnlyList<CustomMissionOption> options, int itemsPerColumn, _) = Descriptor(_picker);
        int columns = (itemsPerColumn + options.Count - 1) / itemsPerColumn;
        int cells = columns * itemsPerColumn;
        int row = Math.Max(0, _selection - FirstRowHandle);
        for (int guard = 0; guard < columns; guard++)
        {
            row = ((row + (by * itemsPerColumn)) % cells + cells) % cells;
            if (row < _rows.Count)
            {
                Select(FirstRowHandle + row);
                return;
            }
        }
    }

    private void StepRing(int by)
    {
        int widgets = Buttons.Count + _rows.Count;
        Select(((_selection + by) % widgets + widgets) % widgets);
    }
}
