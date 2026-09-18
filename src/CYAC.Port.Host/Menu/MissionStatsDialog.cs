using System.Globalization;
using CYAC.Port.Host.Stats;
using CYAC.Port.Render;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host.Menu;

/// <summary>One line of the Mission Stats panel.</summary>
/// <param name="Section">Which mission (or "TOTALS") the row belongs to.</param>
/// <param name="Label">The row's left column; a heading repeats the section.</param>
/// <param name="Value">The row's right column, or null for a heading.</param>
/// <param name="Current">Whether the row belongs to the mission being flown right now.</param>
/// <param name="Indent">How far the label is indented (a death-cause breakdown row).</param>
public readonly record struct StatsRow(
    string Section, string Label, string? Value, bool Current, int Indent)
{
    /// <summary>Whether the row is a section heading rather than a number.</summary>
    public bool IsHeading => Value is null;
}

/// <summary>
/// The READ-ONLY "Mission Stats…" dialog behind the <c>?</c> menu: what <c>stats.json</c> holds, in
/// the original's own bevelled panel.
/// </summary>
/// <remarks>
/// <para>
/// It is M2's settings panel with the editing taken out: the same
/// <see cref="FlightMenuRenderer"/> primitives, the same integer scale, the same opacity, the same
/// key stream — but no row can be changed, because a statistic is a record of what happened and
/// there is nothing to choose.  <c>R</c>, which resets a settings row, deliberately does NOTHING
/// here; resetting the statistics is deleting the file, and the footer says so.
/// </para>
/// <para>
/// Sections are missions: TOTALS first (the sum of every record), then one section per mission that
/// has a record, in catalogue order with the Test Flight last.  The mission being flown right now is
/// marked and its heading is drawn in the highlight colour, so opening the panel mid-sortie answers
/// "how have I done at THIS one" without any hunting.  PgUp/PgDn step between missions.
/// </para>
/// </remarks>
public sealed class MissionStatsDialog
{
    /// <summary>The dialog's title.</summary>
    public const string Title = "MISSION STATS";

    /// <summary>The label of the derived totals section.</summary>
    public const string TotalsSection = "ALL MISSIONS";

    /// <summary>Design pixels of blank between the panel's bevel and its content.</summary>
    public const int Margin = 6;

    /// <summary>Design columns between the label column and the value column.</summary>
    public const int ColumnGap = 12;

    /// <summary>The narrowest the panel is ever drawn.</summary>
    public const int MinimumWidth = 240;

    /// <summary>How many rows the dialog draws when the window can hold them all.</summary>
    public const int MinimumVisibleRows = 4;

    private readonly PortStatsStore _store;
    private readonly FlightMenuRenderer _renderer = new();
    private readonly List<StatsRow> _rows = [];

    private int _selected;
    private int _top;
    private int _builtVersion = -1;
    private string _currentKey = string.Empty;

    /// <summary>Builds the dialog over a statistics store.</summary>
    /// <param name="store">The store whose records it shows.</param>
    public MissionStatsDialog(PortStatsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        Build();
    }

    /// <summary>Whether the dialog is up (and therefore eating the menu's keys).</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Every row, headings included.</summary>
    public IReadOnlyList<StatsRow> Rows => _rows;

    /// <summary>Which row is highlighted.</summary>
    public int Selected => _selected;

    /// <summary>The first visible row's index — what scrolling moves.</summary>
    public int ScrollTop => _top;

    /// <summary>The panel's design-space rectangle as the last frame drew it.</summary>
    /// <remarks>What the geometry guard reads (the M1b lesson, as for M2's dialog).</remarks>
    public (int X, int Y, int Width, int Height) LastPanel { get; private set; }

    /// <summary>How many rows the last frame had room for.</summary>
    public int LastVisibleRows { get; private set; }

    /// <summary>Opens the dialog, at the mission being flown when there is a record for it.</summary>
    /// <param name="current">The current sortie's mission key, or null.</param>
    public void Open(string? current = null)
    {
        _currentKey = MissionIdentity.Normalise(current);
        Build();
        IsOpen = true;
        JumpToCurrent();
    }

    /// <summary>Closes it.</summary>
    public void Close() => IsOpen = false;

    /// <summary>Applies one frame's keys.</summary>
    /// <param name="keys">The menu-key edges this frame, in order.</param>
    /// <returns>True when the dialog closed this frame and the bar should take the keys back.</returns>
    public bool HandleInput(IReadOnlyList<FlightMenuKey>? keys)
    {
        if (keys is null)
        {
            return false;
        }

        foreach (FlightMenuKey key in keys)
        {
            switch (key)
            {
                case FlightMenuKey.Close or FlightMenuKey.Open or FlightMenuKey.Stats
                    or FlightMenuKey.Settings:
                    // ESC backs up ONE level: the bar is still there and the sim is still frozen.
                    Close();
                    return true;
                case FlightMenuKey.Up:
                    Move(-1);
                    break;
                case FlightMenuKey.Down:
                    Move(+1);
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
                default:
                    // Enter, Left, Right and R are all NO-OPS: nothing here can be changed.
                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// The footer, one entry per line: what the panel is, how to move, and WHERE the file is.
    /// </summary>
    /// <remarks>
    /// The three lines are explicit rather than word-wrapped because the last of them is a PATH,
    /// which no wrap can break sensibly and which must never push the sentence that says how to
    /// reset the statistics off the panel.
    /// </remarks>
    public IReadOnlyList<string> FooterLines =>
        _store.Enabled
            ? [
                "Read-only. There is no reset key: delete that file to start again.",
                "PgUp / PgDn = previous / next mission,  ESC = back.",
                Settings.AtomicJson.Short(_store.Path),
            ]
            : [
                $"This run is NOT keeping statistics ({_store.DisabledReason}).",
                "ESC = back.  The file it would keep them in:",
                Settings.AtomicJson.Short(_store.Path),
            ];

    /// <summary>The footer as one string — what a census or a test reads.</summary>
    public string HelpText => string.Join("  ", FooterLines);

    /// <summary>Draws the panel over the frozen frame.</summary>
    /// <param name="target">The whole window.</param>
    /// <param name="font">The menu's own <c>propbold</c> font.</param>
    /// <param name="palette">The game's palette, widened to 8 bits.</param>
    /// <param name="opacity">The panel opacity <c>--menu-opacity</c> holds.</param>
    /// <param name="scale">Host pixels per design pixel; the dialog follows the menu's scale.</param>
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
        if (_builtVersion != _store.Version)
        {
            Build();
        }

        int designWidth = Math.Max(MinimumWidth, target.Width / scale);
        int designHeight = Math.Max(80, target.Height / scale);
        int lineHeight = font.Height + 1;

        int labelWidth = 0, valueWidth = 0;
        foreach (StatsRow row in _rows)
        {
            labelWidth = Math.Max(labelWidth, font.Measure(RowLabel(row)));
            if (row.Value is { } value)
            {
                valueWidth = Math.Max(valueWidth, font.Measure(value));
            }
        }

        // The FOOTER's last line is a path, and a path that has to be elided is a footer that has
        // stopped saying where the file is — so the panel widens for it when the window allows.
        int footerWidth = 0;
        foreach (string line in FooterLines)
        {
            footerWidth = Math.Max(footerWidth, font.Measure(line));
        }

        int width = Math.Clamp(
            Math.Max(
                (Margin * 2) + labelWidth + ColumnGap + valueWidth + 4,
                (Margin * 2) + footerWidth),
            MinimumWidth,
            Math.Max(MinimumWidth, designWidth - 8));

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

        string position = string.Create(
            CultureInfo.InvariantCulture, $"{SectionNumber()}/{SectionCount}");
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

        foreach (string line in FooterLines.Take(footerLines))
        {
            Text(
                target, font, palette, Fit(font, line, width - (Margin * 2)), x + Margin, pen,
                FlightMenuRenderer.ShadowIndex, FlightMenuRenderer.TextEmbossIndex, opacity, scale);
            pen += lineHeight;
        }
    }

    /// <summary>One line per row — the census a headless run prints for the panel itself.</summary>
    public IEnumerable<string> CensusLines()
    {
        foreach (StatsRow row in _rows)
        {
            yield return row.IsHeading
                ? $"── {row.Label}{(row.Current ? "   ← this sortie" : string.Empty)}"
                : $"   {RowLabel(row),-24} {row.Value}";
        }
    }

    /// <summary>Rebuilds the rows from the store — TOTALS, then one section per mission.</summary>
    private void Build()
    {
        _rows.Clear();
        _builtVersion = _store.Version;

        MissionStatsRecord totals = _store.Totals;
        AddSection(TotalsSection, totals, current: false);
        foreach (MissionStatsRecord record in _store.Records)
        {
            AddSection(
                record.Mission.DisplayName,
                record,
                string.Equals(record.Mission.Key, _currentKey, StringComparison.Ordinal));
        }

        _selected = FirstValueRow(0, +1);
        _top = 0;
    }

    private void AddSection(string section, MissionStatsRecord record, bool current)
    {
        string heading = current ? section + "  (this sortie)" : section;
        _rows.Add(new StatsRow(section, heading, null, current, 0));
        Add(section, "Plays", Count(record.Plays), current);
        Add(section, "Accomplished", Count(record.Completions), current);
        Add(section, "Failed", Count(record.Failures), current);
        Add(section, "Abandoned", Count(record.Abandoned), current);
        Add(section, "Kills", Count(record.Kills), current);
        Add(section, "Deaths", Count(record.Deaths), current);
        AddCause(section, "shot down", record.DeathsShotDown, current);
        AddCause(section, "crashed", record.DeathsCrashed, current);
        AddCause(section, "rammed", record.DeathsRammed, current);
        AddCause(section, "ejected", record.DeathsEjected, current);
        Add(section, "Landings", Count(record.Landings), current);
        Add(section, "Rounds fired", Count(record.RoundsFired), current);
        Add(
            section,
            "On target",
            record.Accuracy is { } hit
                ? string.Create(
                    CultureInfo.InvariantCulture, $"{record.RoundsOnTarget:N0}  ({hit * 100:F1} %)")
                : Count(record.RoundsOnTarget),
            current);
        Add(section, "Time flown", MissionStatsRecord.Clock(record.SecondsPlayed), current);
        Add(
            section,
            "Best sortie",
            record.BestSortieSeconds is { } best ? MissionStatsRecord.Clock(best) : "-",
            current);
        Add(section, "First played", record.FirstPlayed ?? "-", current);
        Add(section, "Last played", record.LastPlayed ?? "-", current);
    }

    private void AddCause(string section, string label, int count, bool current)
    {
        if (count > 0)
        {
            _rows.Add(new StatsRow(section, label, Count(count), current, 1));
        }
    }

    private void Add(string section, string label, string value, bool current) =>
        _rows.Add(new StatsRow(section, label, value, current, 0));

    private static string Count(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string RowLabel(StatsRow row) =>
        row.Indent > 0 ? new string(' ', row.Indent * 2) + row.Label : row.Label;

    private int SectionCount => _rows.Count(r => r.IsHeading);

    private int SectionNumber()
    {
        int n = 0;
        for (int i = 0; i <= _selected && i < _rows.Count; i++)
        {
            if (_rows[i].IsHeading)
            {
                n++;
            }
        }

        return Math.Max(1, n);
    }

    private void DrawRow(
        PixelTarget target,
        CockpitFont font,
        IReadOnlyList<Rgb24> palette,
        StatsRow row,
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

        int ink = selected
            ? FlightMenuRenderer.HighlightIndex
            : row is { IsHeading: true, Current: true }
                ? FlightMenuRenderer.HighlightIndex
                : FlightMenuRenderer.ShadowIndex;
        int emboss = selected
            ? FlightMenuRenderer.SelectedEmbossIndex
            : FlightMenuRenderer.TextEmbossIndex;

        Text(target, font, palette, RowLabel(row), x + Margin, y + 2, ink, emboss, opacity, scale);

        if (row.Value is not { } value)
        {
            _renderer.FillRect(
                target, palette, x + 2, y + FlightMenuRenderer.RowHeight - 1, width - 4, 1,
                FlightMenuRenderer.SelectionIndex, opacity, scale);
            return;
        }

        if (value.Length > 0)
        {
            Text(
                target, font, palette, value, x + width - Margin - font.Measure(value), y + 2,
                ink, emboss, opacity, scale);
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

    /// <summary>
    /// Shortens a footer line to fit, keeping its TAIL — a path's useful half is its end.
    /// </summary>
    /// <param name="font">The font it will be drawn in.</param>
    /// <param name="text">The line.</param>
    /// <param name="width">The available design columns.</param>
    private static string Fit(CockpitFont font, string text, int width)
    {
        if (width <= 0 || font.Measure(text) <= width)
        {
            return text;
        }

        string trimmed = text;
        while (trimmed.Length > 1 && font.Measure("..." + trimmed) > width)
        {
            trimmed = trimmed[1..];
        }

        return "..." + trimmed;
    }

    private void JumpToCurrent()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Current && _rows[i].IsHeading && i + 1 < _rows.Count)
            {
                _selected = i + 1;
                return;
            }
        }

        _selected = FirstValueRow(0, +1);
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
        List<string> sections = _rows.Where(r => r.IsHeading).Select(r => r.Section).ToList();
        if (sections.Count == 0)
        {
            return;
        }

        int index = sections.IndexOf(_rows[Math.Clamp(_selected, 0, _rows.Count - 1)].Section);
        index = (((index < 0 ? 0 : index) + step) % sections.Count + sections.Count) % sections.Count;
        string wanted = sections[index];
        for (int i = 0; i < _rows.Count; i++)
        {
            if (string.Equals(_rows[i].Section, wanted, StringComparison.Ordinal)
                && !_rows[i].IsHeading)
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
}
