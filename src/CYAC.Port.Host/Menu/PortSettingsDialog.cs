using System.Globalization;
using System.Text;
using CYAC.Port.Host.Settings;
using CYAC.Port.Render;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host.Menu;

/// <summary>One line of the settings dialog.</summary>
/// <param name="Section">Which section it belongs to.</param>
/// <param name="Setting">The setting it shows, or null for a section heading.</param>
/// <param name="Bit">
/// For a <see cref="PortSettingKind.Bits"/> setting, which bit this row switches; −1 otherwise.
/// </param>
/// <param name="Label">What the row's left column says.</param>
public readonly record struct SettingsRow(string Section, PortSetting? Setting, int Bit, string Label)
{
    /// <summary>Whether the row is a section heading rather than a value.</summary>
    public bool IsHeading => Setting is null;
}

/// <summary>
/// The PORT SETTINGS dialog: the `?` menu's "Port Settings…" row, drawn as one more of the original's
/// bevelled panels and driven by the same key stream as the menu bar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Look.</b> The original's front-end widget look: a bevelled panel, <c>propbold</c> labels
/// embossed one row down, section rule lines, and the same selection band the pull-downs use — all at
/// the menu's own INTEGER scale and under <c>--menu-opacity</c>, so the frozen scene shows
/// through the panel while a look is tuned.  The one row type the menu bar lacks is added here: a row
/// with a VALUE.
/// </para>
/// <para>
/// <b>Keys</b> (the original's own vocabulary, manual p.9 — "press the Tab key to move the cursor …
/// spacebar … Esc"): Up/Down move, Left/Right change, Space/Enter step, PgUp/PgDn jump a section,
/// Home/End the ends, <c>R</c> resets a row to its built-in default, ESC backs up one level to the
/// menu bar, exactly as the original's menus do.
/// </para>
/// <para>
/// <b>The dialog owns no state that matters.</b>  Every value it shows is
/// <see cref="PortSettingsStore.Value"/>, and every change goes through
/// <see cref="PortSettingsStore.Set"/>, which pushes the value live, keeps the options a restart
/// rebuilds from in step, and writes the file — so what is on screen, what is in memory and what is
/// on disk cannot diverge.
/// </para>
/// </remarks>
public sealed class PortSettingsDialog
{
    /// <summary>The dialog's title.</summary>
    public const string Title = "PORT SETTINGS";

    /// <summary>Design pixels of blank between the panel's bevel and its content.</summary>
    public const int Margin = 6;

    /// <summary>Design columns between the label column and the value column.</summary>
    public const int ColumnGap = 12;

    /// <summary>How many host-frames a direction must be held before it starts repeating.</summary>
    public const int HoldDelayFrames = 14;

    /// <summary>The narrowest the panel is ever drawn.</summary>
    public const int MinimumWidth = 240;

    /// <summary>How many value rows the dialog draws when the window can hold them all.</summary>
    public const int MinimumVisibleRows = 4;

    private readonly PortSettingsStore _store;
    private readonly FlightMenuRenderer _renderer = new();
    private readonly List<SettingsRow> _rows = [];

    private int _selected;
    private int _top;
    private int _heldFrames;
    private int _heldDirection;

    /// <summary>Builds the dialog over a settings store.</summary>
    /// <param name="store">The store whose values it shows and changes.</param>
    public PortSettingsDialog(PortSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        foreach (string section in PortSettings.Sections)
        {
            List<PortSetting> members = PortSettings.All.Where(s => s.Section == section && !s.Hidden).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            _rows.Add(new SettingsRow(section, null, -1, section));
            foreach (PortSetting setting in members)
            {
                if (setting.Kind == PortSettingKind.Bits)
                {
                    for (int bit = 0; bit < setting.Bits.Count; bit++)
                    {
                        _rows.Add(new SettingsRow(section, setting, bit, setting.Bits[bit]));
                    }
                }
                else
                {
                    _rows.Add(new SettingsRow(section, setting, -1, setting.Label));
                }
            }
        }

        _selected = FirstValueRow(0, +1);
    }

    /// <summary>Whether the dialog is up (and therefore eating the menu's keys).</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Every row, headings included — the census a headless run can print.</summary>
    public IReadOnlyList<SettingsRow> Rows => _rows;

    /// <summary>Which row is highlighted.</summary>
    public int Selected => _selected;

    /// <summary>The first visible row's index — what scrolling moves.</summary>
    public int ScrollTop => _top;

    /// <summary>The panel's design-space rectangle as the last frame drew it.</summary>
    /// <remarks>
    /// The M1b lesson: a design-centre sampler proves the colours and never the GEOMETRY.  This is
    /// what the geometry guard reads — the panel's design units must be the same at every scale.
    /// </remarks>
    public (int X, int Y, int Width, int Height) LastPanel { get; private set; }

    /// <summary>How many value rows the last frame had room for.</summary>
    public int LastVisibleRows { get; private set; }

    /// <summary>Opens the dialog at its first row.</summary>
    public void Open()
    {
        IsOpen = true;
        _heldFrames = 0;
        _heldDirection = 0;
    }

    /// <summary>Closes it.</summary>
    public void Close() => IsOpen = false;

    /// <summary>Applies one frame's keys.</summary>
    /// <param name="keys">The menu-key edges this frame, in order.</param>
    /// <param name="typed">A letter typed this frame, or <c>'\0'</c> — <c>R</c> resets the row.</param>
    /// <param name="heldLeft">Whether the LEFT arrow is held (for the number rows' acceleration).</param>
    /// <param name="heldRight">Whether RIGHT is held.</param>
    /// <returns>True when the dialog closed this frame and the bar should take the keys back.</returns>
    public bool HandleInput(
        IReadOnlyList<FlightMenuKey>? keys, char typed, bool heldLeft, bool heldRight)
    {
        bool closed = false;
        if (keys is not null)
        {
            foreach (FlightMenuKey key in keys)
            {
                switch (key)
                {
                    case FlightMenuKey.Close or FlightMenuKey.Open or FlightMenuKey.Settings:
                        // ESC backs up ONE level, the way the original's menus do: the bar is still
                        // there behind this panel and the simulation is still frozen.
                        Close();
                        closed = true;
                        break;
                    case FlightMenuKey.Up:
                        Move(-1);
                        break;
                    case FlightMenuKey.Down:
                        Move(+1);
                        break;
                    case FlightMenuKey.Left:
                        Change(-1, 0);
                        break;
                    case FlightMenuKey.Right or FlightMenuKey.Enter:
                        Change(+1, 0);
                        break;
                    case FlightMenuKey.Home:
                        _selected = FirstValueRow(0, +1);
                        break;
                    case FlightMenuKey.End:
                        _selected = FirstValueRow(_rows.Count - 1, -1);
                        break;
                    case FlightMenuKey.PageDown:
                        Section(+1);
                        break;
                    case FlightMenuKey.PageUp:
                        Section(-1);
                        break;
                    case FlightMenuKey.Reset:
                        ResetRow();
                        break;
                    default:
                        break;
                }

                if (closed)
                {
                    return true;
                }
            }
        }

        if (typed is 'r' or 'R')
        {
            ResetRow();
        }

        // The ACCELERATION.  The port's keyboard is polled as strict edges (one press = one edge,
        // ControlInputSource §arming), so a hold cannot be read off the edge stream — it is read
        // off the arrows' HELD state, which is the same state the stick reads when the bar is down.
        // After HoldDelayFrames the row steps every frame, and PortSetting.Advance grows the step
        // with the count.
        int direction = (heldRight ? 1 : 0) - (heldLeft ? 1 : 0);
        if (direction == 0 || direction != _heldDirection)
        {
            _heldFrames = 0;
            _heldDirection = direction;
        }
        else
        {
            _heldFrames++;
            if (_heldFrames > HoldDelayFrames)
            {
                Change(direction, _heldFrames - HoldDelayFrames);
            }
        }

        return false;
    }

    /// <summary>The highlighted row's help text — what the footer says.</summary>
    public string HelpText
    {
        get
        {
            SettingsRow row = _rows[Math.Clamp(_selected, 0, _rows.Count - 1)];
            if (row.Setting is not { } setting)
            {
                return string.Empty;
            }

            string live = setting.IsLive
                ? string.Empty
                : setting.ReadAtStartup
                    ? "  [read when the game starts]"
                    : "  [takes effect on the next sortie]";
            string pinned = _store.IsFromCommandLine(setting)
                ? $"  [--{setting.Name} was given on the command line]"
                : string.Empty;
            return setting.Help + live + pinned;
        }
    }

    /// <summary>Draws the dialog over the frozen frame.</summary>
    /// <param name="target">The whole window.</param>
    /// <param name="font">The menu's own <c>propbold</c> font.</param>
    /// <param name="palette">The game's palette, widened to 8 bits.</param>
    /// <param name="opacity">The panel opacity <c>--menu-opacity</c> holds.</param>
    /// <param name="scale">host pixels per design pixel; the dialog follows the menu's scale.</param>
    public void Render(
        PixelTarget target,
        CockpitFont font,
        IReadOnlyList<Rgb24> palette,
        double opacity,
        int scale)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);

        int designWidth = Math.Max(MinimumWidth, target.Width / scale);
        int designHeight = Math.Max(80, target.Height / scale);
        int lineHeight = font.Height + 1;

        int labelWidth = 0, valueWidth = 0;
        foreach (SettingsRow row in _rows)
        {
            labelWidth = Math.Max(labelWidth, font.Measure(RowLabel(row)));
            if (row.Setting is { } setting)
            {
                valueWidth = Math.Max(valueWidth, font.Measure(ValueText(row, setting)) + SwatchRoom(setting));
            }
        }

        int width = Math.Clamp(
            (Margin * 2) + labelWidth + ColumnGap + valueWidth + 4,
            MinimumWidth,
            Math.Max(MinimumWidth, designWidth - 8));

        // The vertical budget: title, rule, N rows, rule, the footer's wrapped help.
        int footerLines = 3;
        int fixedRows = lineHeight + 3 + 3 + (footerLines * lineHeight) + (Margin * 2);
        int top = FlightMenuRenderer.BarHeight + 3;
        int room = Math.Max(
            FlightMenuRenderer.RowHeight * MinimumVisibleRows,
            designHeight - top - 6 - fixedRows);
        int visible = Math.Clamp(room / FlightMenuRenderer.RowHeight, MinimumVisibleRows, _rows.Count);
        LastVisibleRows = visible;
        ScrollInto(visible);

        int height = fixedRows + (visible * FlightMenuRenderer.RowHeight);
        int x = Math.Max(2, (designWidth - width) / 2);
        int y = Math.Max(top, top + ((designHeight - top - height) / 2));
        LastPanel = (x, y, width, height);

        _renderer.DrawPanel(target, palette, x, y, width, height, opacity, scale);

        int pen = y + Margin;
        Text(target, font, palette, Title, x + Margin, pen,
            FlightMenuRenderer.ShadowIndex, FlightMenuRenderer.TextEmbossIndex, opacity, scale);

        // The scroll hint sits opposite the title, right-aligned like an accelerator.
        string position = string.Create(
            CultureInfo.InvariantCulture,
            $"{ValueRowNumber(_selected)}/{ValueRowCount}");
        Text(
            target, font, palette, position,
            x + width - Margin - font.Measure(position), pen,
            FlightMenuRenderer.DisabledTextIndex, FlightMenuRenderer.TextEmbossIndex, opacity, scale);
        pen += lineHeight;
        _renderer.FillRect(
            target, palette, x + 2, pen, width - 4, 1, FlightMenuRenderer.SelectionIndex, opacity, scale);
        pen += 3;

        for (int i = 0; i < visible; i++)
        {
            int index = _top + i;
            if (index >= _rows.Count)
            {
                break;
            }

            DrawRow(target, font, palette, _rows[index], index, x, pen, width, opacity, scale);
            pen += FlightMenuRenderer.RowHeight;
        }

        _renderer.FillRect(
            target, palette, x + 2, pen, width - 4, 1, FlightMenuRenderer.SelectionIndex, opacity, scale);
        pen += 3;

        foreach (string line in Wrap(font, HelpText, width - (Margin * 2), footerLines))
        {
            Text(
                target, font, palette, line, x + Margin, pen,
                FlightMenuRenderer.ShadowIndex, FlightMenuRenderer.TextEmbossIndex,
                opacity, scale);
            pen += lineHeight;
        }
    }

    /// <summary>One line per row: the census a headless run prints, so a run says what it holds.</summary>
    public IEnumerable<string> CensusLines()
    {
        foreach (SettingsRow row in _rows)
        {
            if (row.Setting is not { } setting)
            {
                yield return $"── {row.Section}";
                continue;
            }

            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"   {RowLabel(row),-22} {ValueText(row, setting),-12} "
                    + $"{(setting.IsLive ? "live    " : setting.ReadAtStartup ? "startup " : "restart ")}"
                    + $"{(_store.IsDefault(setting) ? " " : "*")}"
                    + $"{(_store.IsFromCommandLine(setting) ? "~" : " ")} --{setting.Name}");
        }
    }

    private static int SwatchRoom(PortSetting setting) =>
        setting.Kind == PortSettingKind.Color ? 12 : 0;

    private string RowLabel(SettingsRow row) => row.Bit >= 0 ? "  " + row.Label : row.Label;

    private string ValueText(SettingsRow row, PortSetting setting) =>
        row.Bit >= 0
            ? (((int)_store.Value(setting).Number & (1 << row.Bit)) != 0 ? "ON" : "OFF")
            : setting.Format(_store.Value(setting));

    private int ValueRowCount => _rows.Count(r => !r.IsHeading);

    private int ValueRowNumber(int index)
    {
        int n = 0;
        for (int i = 0; i <= index && i < _rows.Count; i++)
        {
            if (!_rows[i].IsHeading)
            {
                n++;
            }
        }

        return n;
    }

    private void DrawRow(
        PixelTarget target,
        CockpitFont font,
        IReadOnlyList<Rgb24> palette,
        SettingsRow row,
        int index,
        int x,
        int y,
        int width,
        double opacity,
        int scale)
    {
        bool selected = index == _selected;
        if (selected)
        {
            _renderer.FillRect(
                target, palette, x + 2, y, width - 4, FlightMenuRenderer.RowHeight,
                FlightMenuRenderer.SelectionIndex, opacity, scale);
        }

        int ink = selected ? FlightMenuRenderer.HighlightIndex : FlightMenuRenderer.ShadowIndex;
        int emboss = selected
            ? FlightMenuRenderer.SelectedEmbossIndex
            : FlightMenuRenderer.TextEmbossIndex;

        if (row.Setting is not { } setting)
        {
            // A SECTION heading: the original's own rule line under a caption.
            Text(target, font, palette, row.Label, x + Margin, y + 2, ink, emboss, opacity, scale);
            _renderer.FillRect(
                target, palette, x + 2, y + FlightMenuRenderer.RowHeight - 1, width - 4, 1,
                FlightMenuRenderer.SelectionIndex, opacity, scale);
            return;
        }

        // The gutter marks: `*` changed from the default, `~` pinned by the command line.
        string marker = _store.IsFromCommandLine(setting)
            ? "~"
            : _store.IsDefault(setting) ? string.Empty : "*";
        if (marker.Length > 0)
        {
            Text(target, font, palette, marker, x + 2, y + 2, ink, emboss, opacity, scale);
        }

        Text(target, font, palette, RowLabel(row), x + Margin, y + 2, ink, emboss, opacity, scale);

        string value = ValueText(row, setting);
        int right = x + width - Margin - font.Measure(value);
        Text(target, font, palette, value, right, y + 2, ink, emboss, opacity, scale);

        int swatch = (int)Math.Round(_store.Value(setting).Number);
        if (setting.Kind == PortSettingKind.Color && row.Bit < 0 && swatch >= 0)
        {
            // The SWATCH: the colour itself, in a one-pixel dark frame, left of the number.  A
            // NEGATIVE index means "the record's own colour", which has no swatch to show.
            int sx = right - 12;
            _renderer.FillRect(
                target, palette, sx, y + 2, 9, 7, FlightMenuRenderer.BlackIndex, opacity, scale);
            _renderer.FillRect(
                target, palette, sx + 1, y + 3, 7, 5, swatch, opacity, scale, opaque: true);
        }
    }

    private void Text(
        PixelTarget target,
        CockpitFont font,
        IReadOnlyList<Rgb24> palette,
        string text,
        int x,
        int y,
        int ink,
        int emboss,
        double opacity,
        int scale) =>
        _renderer.DrawText(target, font, palette, text, x, y, ink, emboss, opacity, scale);

    /// <summary>Breaks the help text into at most <paramref name="lines"/> lines that fit.</summary>
    /// <param name="font">The font it will be drawn in.</param>
    /// <param name="text">The help text.</param>
    /// <param name="width">The available design columns.</param>
    /// <param name="lines">How many lines the footer has.</param>
    private static List<string> Wrap(CockpitFont font, string text, int width, int lines)
    {
        List<string> wrapped = new List<string>(lines);
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            return wrapped;
        }

        StringBuilder line = new System.Text.StringBuilder();
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (font.Measure(candidate) <= width || line.Length == 0)
            {
                line.Clear();
                line.Append(candidate);
                continue;
            }

            wrapped.Add(line.ToString());
            line.Clear();
            line.Append(word);
            if (wrapped.Count == lines - 1)
            {
                break;
            }
        }

        if (wrapped.Count < lines && line.Length > 0)
        {
            string last = line.ToString();
            while (last.Length > 1 && font.Measure(last + "...") > width)
            {
                last = last[..^1];
            }

            wrapped.Add(
                wrapped.Count == lines - 1 && font.Measure(line.ToString()) > width
                    ? last + "..."
                    : line.ToString());
        }

        return wrapped;
    }

    private void Move(int step)
    {
        int next = _selected;
        for (int i = 0; i < _rows.Count; i++)
        {
            next += step;
            if (next < 0)
            {
                next = _rows.Count - 1;
            }
            else if (next >= _rows.Count)
            {
                next = 0;
            }

            if (!_rows[next].IsHeading)
            {
                _selected = next;
                return;
            }
        }
    }

    private void Section(int step)
    {
        string current = _rows[_selected].Section;
        List<string> sections = _rows.Select(r => r.Section).Distinct().ToList();
        int index = sections.IndexOf(current);
        index = ((index + step) % sections.Count + sections.Count) % sections.Count;
        string wanted = sections[index];
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Section == wanted && !_rows[i].IsHeading)
            {
                _selected = i;
                return;
            }
        }
    }

    private int FirstValueRow(int from, int step)
    {
        for (int i = from; i >= 0 && i < _rows.Count; i += step)
        {
            if (!_rows[i].IsHeading)
            {
                return i;
            }
        }

        return 0;
    }

    private void ScrollInto(int visible)
    {
        // The heading above the highlighted row stays on screen when it can, so a row is never
        // shown without the section it belongs to.
        int wanted = _selected > 0 && _rows[_selected - 1].IsHeading ? _selected - 1 : _selected;
        if (wanted < _top)
        {
            _top = wanted;
        }
        else if (_selected >= _top + visible)
        {
            _top = _selected - visible + 1;
        }

        _top = Math.Clamp(_top, 0, Math.Max(0, _rows.Count - visible));
    }

    private void Change(int direction, int acceleration)
    {
        SettingsRow row = _rows[_selected];
        if (row.Setting is not { } setting)
        {
            return;
        }

        if (row.Bit >= 0)
        {
            int mask = (int)Math.Round(_store.Value(setting).Number) ^ (1 << row.Bit);
            _store.Set(setting, SettingValue.Of(mask));
            return;
        }

        _store.Set(setting, setting.Advance(_store.Value(setting), direction, acceleration));
    }

    private void ResetRow()
    {
        SettingsRow row = _rows[_selected];
        if (row.Setting is { } setting)
        {
            _store.Reset(setting);
        }
    }
}
