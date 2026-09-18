using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using CYAC.Port.Preflight;
using mode13hx;
using mode13hx.Model;
using mode13hx.Presentation;
using mode13hx.Util;

namespace CYAC.Port.Host.Startup;

/// <summary>What the dashboard draws this frame.</summary>
/// <param name="Snapshot">The pipeline's latest snapshot.</param>
/// <param name="Visible">False while the show policy keeps the screen off; the frame is then blank.</param>
/// <param name="AllDetails">Whether the player asked for every row's details.</param>
/// <param name="Seconds">Wall seconds since the screen opened — what the running lamp blinks on.</param>
/// <param name="Message">A line drawn above the keys, or null.</param>
public readonly record struct DashboardView(
    PreflightSnapshot Snapshot,
    bool Visible = true,
    bool AllDetails = false,
    double Seconds = 0.0,
    string? Message = null);

/// <summary>
/// The pre-flight dashboard, drawn with the port's own assets alone: no original asset exists until
/// the transform has run, so the panel is lines and mode-13hx's 9 × 16 font.
/// </summary>
/// <remarks>
/// <para>
/// It is an <see cref="IRasterizer"/> of its own so that the boot screen, the headless PNG and the
/// tests all drive exactly the same painter.  Everything is laid out in DESIGN pixels — a 9 × 16
/// character cell, a 68-column panel — and multiplied by a whole <see cref="Scale"/>, so the panel
/// fits a 640 × 400 window at 1× and reads deliberately at 1920 × 1080 at 2×.
/// </para>
/// <para>
/// The frame is cleared every time: frame slots are recycled by the presenter, so anything not
/// painted is the previous frame's pixels.
/// </para>
/// </remarks>
public sealed class PreflightScreen : IRasterizer
{
    /// <summary>The font cell's width in design pixels.</summary>
    public const int CellWidth = 9;

    /// <summary>The font cell's height in design pixels.</summary>
    public const int CellHeight = 16;

    /// <summary>How many characters wide the panel's text column is.</summary>
    public const int Columns = 68;

    /// <summary>The panel's inner margin, in design pixels.</summary>
    public const int Padding = 10;

    /// <summary>One step row's height, in design pixels.</summary>
    public const int RowHeight = 18;

    /// <summary>Where a row's name starts, in character cells (the lamp owns the first two).</summary>
    public const int NameColumn = 2;

    /// <summary>Where a row's summary starts, in character cells.</summary>
    public const int SummaryColumn = 13;

    /// <summary>How many cells the duration is given at the right edge.</summary>
    public const int DurationCells = 8;

    /// <summary>Where a detail line starts, in character cells.</summary>
    public const int DetailColumn = 4;

    /// <summary>The most lines one detail line may be wrapped into before the rest is cut.</summary>
    public const int DetailWrapLines = 3;

    private static readonly uint Black = Func.EncodePixelColor(0, 0, 0);
    private static readonly uint Panel = Func.EncodePixelColor(16, 16, 40);
    private static readonly uint PanelEdge = Func.EncodePixelColor(96, 96, 128);
    private static readonly uint HeaderBand = Func.EncodePixelColor(0, 0, 112);
    private static readonly uint Title = Func.EncodePixelColor(255, 255, 255);
    private static readonly uint Label = Func.EncodePixelColor(208, 208, 208);
    private static readonly uint Quiet = Func.EncodePixelColor(144, 144, 160);
    private static readonly uint Detail = Func.EncodePixelColor(176, 176, 192);
    private static readonly uint Rule = Func.EncodePixelColor(72, 72, 96);

    private static readonly uint LampPending = Func.EncodePixelColor(112, 112, 112);
    private static readonly uint LampRunning = Func.EncodePixelColor(255, 176, 0);
    private static readonly uint LampOk = Func.EncodePixelColor(0, 208, 64);
    private static readonly uint LampCached = Func.EncodePixelColor(0, 208, 208);
    private static readonly uint LampWarning = Func.EncodePixelColor(255, 224, 0);
    private static readonly uint LampFailed = Func.EncodePixelColor(255, 72, 72);
    private static readonly uint LampSkipped = Func.EncodePixelColor(96, 96, 112);

    private readonly bool _text;

    /// <summary>Creates the screen.</summary>
    /// <param name="text">
    /// Whether the bitmap font is reachable.  A run whose working directory holds no
    /// <c>resources/texture</c> draws the lamps and the frame and leaves the words out, rather than
    /// failing to open a window at all.
    /// </param>
    public PreflightScreen(bool text = true)
    {
        _text = text;
    }

    /// <summary>What the next <see cref="Render"/> draws.</summary>
    public DashboardView View { get; set; }

    /// <summary>The port's own version, for the title's right-hand corner.</summary>
    public static string HostVersion { get; } = ReadVersion();

    /// <summary>How many host pixels one design pixel is worth on a target this size.</summary>
    /// <param name="width">The target's width.</param>
    /// <param name="height">Its height.</param>
    /// <remarks>
    /// The panel is 632 × ~300 design pixels, so 1× fits a 640 × 400 window exactly and every larger
    /// window takes the largest whole multiple that still leaves a margin.
    /// </remarks>
    public static int Scale(int width, int height) =>
        Math.Clamp(Math.Min(width / 660, height / 420), 1, 4);

    /// <summary>The lamp colour of a state.</summary>
    /// <param name="state">The state.</param>
    public static uint LampColor(StepState state) => state switch
    {
        StepState.Running => LampRunning,
        StepState.Ok => LampOk,
        StepState.Cached => LampCached,
        StepState.Warning => LampWarning,
        StepState.Failed => LampFailed,
        StepState.Skipped => LampSkipped,
        _ => LampPending,
    };

    /// <summary>The keys line a snapshot offers.</summary>
    /// <param name="snapshot">The snapshot.</param>
    public static string Keys(PreflightSnapshot snapshot) =>
        (PreflightDashboard.IsReady(snapshot) ? "Enter continue   " : string.Empty)
        + "R rescan   D details   Esc quit";

    /// <inheritdoc/>
    public void Render(FrameBuffer buffer, double secondsSinceLastFrame)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _ = secondsSinceLastFrame;
        FrameDescriptor? frame = buffer.StartNextFrame();
        Paint(frame.Canvas, buffer.Width, buffer.Height, View, _text);
        buffer.FinishFrame(frame);
    }

    /// <summary>Paints one dashboard frame.</summary>
    /// <param name="canvas">The frame's canvas.</param>
    /// <param name="width">The target's width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="view">What to draw.</param>
    /// <param name="text">Whether the bitmap font may be used.</param>
    public static void Paint(Canvas canvas, int width, int height, DashboardView view, bool text = true)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        canvas.ResetClip();
        canvas.SetPenColor(Black);
        canvas.Rectangle(0, 0, width, height);
        if (!view.Visible || view.Snapshot is null)
        {
            return;
        }

        int scale = Scale(width, height);
        IReadOnlyList<DashboardRow> rows = PreflightDashboard.Rows(view.Snapshot, view.AllDetails);

        // A detail line is WRAPPED, not cut off: the line that says which folder to put the game into
        // is the one most likely to be too long, and half a path helps nobody.
        List<IReadOnlyList<string>> wanted = rows
            .Select(r => (IReadOnlyList<string>)[.. r.Details.SelectMany(d => Wrap(d, Columns - DetailColumn))])
            .ToList();

        // The panel's height follows its content, so the details a run opens do not push the keys off
        // the screen: the budget is what the window has room for, and the overflow is said out loud.
        int panelWidth = (Columns * CellWidth) + (Padding * 2);
        int fixedHeight = (Padding * 2) + CellHeight + 8 + 1 + 8 + (rows.Count * RowHeight) + 8 + 1 + 6
            + CellHeight + (view.Message is null ? 0 : CellHeight);
        int room = Math.Max(0, ((height / scale) - 8 - fixedHeight) / CellHeight);
        ImmutableArray<string>[] details = Budget(wanted, room);
        int panelHeight = fixedHeight + (details.Sum(d => d.Length) * CellHeight);

        int originX = ((width - (panelWidth * scale)) / 2) + 1;
        int originY = Math.Max(1, (height - (panelHeight * scale)) / 2);
        Painter paint = new Painter(canvas, originX, originY, scale, text);

        paint.Fill(0, 0, panelWidth, panelHeight, Panel);
        paint.Outline(0, 0, panelWidth, panelHeight, PanelEdge);
        paint.Fill(1, 1, panelWidth - 2, CellHeight + Padding - 2, HeaderBand);

        int y = Padding / 2;
        paint.Text("CHUCK YEAGER'S AIR COMBAT - port pre-flight check", Padding, y, Title);
        string version = "v" + HostVersion;
        paint.Text(version, panelWidth - Padding - (version.Length * CellWidth), y, Quiet);
        y += CellHeight + 4;
        paint.Fill(Padding, y, panelWidth - (Padding * 2), 1, Rule);
        y += 1 + 8;

        for (int i = 0; i < rows.Count; i++)
        {
            DashboardRow row = rows[i];
            paint.Lamp(Padding, y + 3, row.State, view.Seconds);
            paint.Text(row.Name, Padding + (NameColumn * CellWidth), y, Colour(row.State));
            paint.Text(
                Fit(row.Summary, Columns - SummaryColumn - DurationCells - 1),
                Padding + (SummaryColumn * CellWidth),
                y,
                row.State == StepState.Pending ? Quiet : Label);
            if (row.Duration.Length > 0)
            {
                paint.Text(
                    row.Duration,
                    panelWidth - Padding - (row.Duration.Length * CellWidth),
                    y,
                    Quiet);
            }

            y += RowHeight;
            foreach (string line in details[i])
            {
                paint.Text(
                    Fit(line, Columns - DetailColumn),
                    Padding + (DetailColumn * CellWidth),
                    y,
                    row.State == StepState.Failed ? LampFailed : Detail);
                y += CellHeight;
            }
        }

        y += 8;
        paint.Fill(Padding, y, panelWidth - (Padding * 2), 1, Rule);
        y += 1 + 6;
        if (view.Message is { } message)
        {
            paint.Text(Fit(message, Columns), Padding, y, LampFailed);
            y += CellHeight;
        }

        paint.Text(Keys(view.Snapshot), Padding, y, Label);
    }

    /// <summary>The colour a row's own name is drawn in.</summary>
    /// <param name="state">The row's state.</param>
    private static uint Colour(StepState state) => state switch
    {
        StepState.Pending => Quiet,
        StepState.Skipped => Quiet,
        StepState.Failed => LampFailed,
        StepState.Warning => LampWarning,
        _ => Label,
    };

    /// <summary>
    /// Shares the room the window has among the rows that want details, in row order, and says when
    /// a row's lines were cut.
    /// </summary>
    /// <param name="rows">Each row's detail lines, already wrapped to the columns they have.</param>
    /// <param name="room">How many detail lines fit at all.</param>
    private static ImmutableArray<string>[] Budget(IReadOnlyList<IReadOnlyList<string>> rows, int room)
    {
        ImmutableArray<string>[] taken = new ImmutableArray<string>[rows.Count];
        int left = room;
        for (int i = 0; i < rows.Count; i++)
        {
            IReadOnlyList<string> wanted = rows[i];
            if (wanted.Count == 0 || left <= 0)
            {
                taken[i] = [];
                continue;
            }

            if (wanted.Count <= left)
            {
                taken[i] = [.. wanted];
                left -= wanted.Count;
                continue;
            }

            List<string> cut = wanted.Take(Math.Max(0, left - 1)).ToList();
            cut.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"... and {wanted.Count - cut.Count} more line(s)"));
            taken[i] = [.. cut];
            left = 0;
        }

        return taken;
    }

    /// <summary>
    /// Breaks a line into the lines it needs, rather than cutting its end off: at a space where there
    /// is one, inside the word where there is not (a path or a sha256 has none), with continuation
    /// lines indented.  The last allowed line is cut with an ellipsis, so one long line cannot take
    /// the panel over.
    /// </summary>
    /// <param name="text">The line.</param>
    /// <param name="columns">How many characters there is room for.</param>
    /// <param name="maxLines">The most lines to produce.</param>
    public static ImmutableArray<string> Wrap(string? text, int columns, int maxLines = DetailWrapLines)
    {
        string line = (text ?? string.Empty).TrimEnd();
        if (columns <= 0 || maxLines <= 0)
        {
            return [];
        }

        if (line.Length <= columns)
        {
            return [line];
        }

        List<string> lines = new List<string>();
        ReadOnlySpan<char> rest = line.AsSpan();
        int indent = 0;
        while (!rest.IsEmpty)
        {
            string margin = new(' ', indent);
            if (lines.Count == maxLines - 1)
            {
                lines.Add(Fit(margin + rest.ToString(), columns));
                break;
            }

            int room = columns - indent;
            if (rest.Length <= room)
            {
                lines.Add(margin + rest.ToString());
                break;
            }

            int cut = rest[..(room + 1)].LastIndexOf(' ');
            if (cut <= 0)
            {
                cut = room;   // one unbreakable token: split it rather than lose its tail
            }

            lines.Add((margin + rest[..cut].ToString()).TrimEnd());
            rest = rest[cut..].TrimStart();
            indent = 2;
        }

        return [.. lines];
    }

    /// <summary>Cuts a line to the columns it has, with an ellipsis when it did not fit.</summary>
    /// <param name="text">The line.</param>
    /// <param name="columns">How many characters there is room for.</param>
    public static string Fit(string? text, int columns)
    {
        string line = text ?? string.Empty;
        if (columns <= 0)
        {
            return string.Empty;
        }

        return line.Length <= columns ? line : line[..Math.Max(0, columns - 3)] + "...";
    }

    private static string ReadVersion()
    {
        string version = typeof(PreflightScreen).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }

    /// <summary>Draws in DESIGN pixels: every coordinate is multiplied by the scale and offset.</summary>
    /// <param name="canvas">The frame's canvas.</param>
    /// <param name="originX">Where the panel's left edge sits, in target pixels.</param>
    /// <param name="originY">Its top edge.</param>
    /// <param name="scale">Target pixels per design pixel.</param>
    /// <param name="text">Whether the bitmap font may be used.</param>
    private readonly record struct Painter(Canvas Canvas, int originX, int originY, int scale, bool text)
    {
        /// <summary>Fills a design-space rectangle.</summary>
        /// <param name="x">Left.</param>
        /// <param name="y">Top.</param>
        /// <param name="w">Width.</param>
        /// <param name="h">Height.</param>
        /// <param name="colour">The encoded colour.</param>
        public void Fill(int x, int y, int w, int h, uint colour)
        {
            Canvas.SetPenColor(colour);
            Canvas.Rectangle(originX + (x * scale), originY + (y * scale), w * scale, h * scale);
        }

        /// <summary>Draws a one-design-pixel frame around a rectangle.</summary>
        /// <param name="x">Left.</param>
        /// <param name="y">Top.</param>
        /// <param name="w">Width.</param>
        /// <param name="h">Height.</param>
        /// <param name="colour">The encoded colour.</param>
        public void Outline(int x, int y, int w, int h, uint colour)
        {
            Fill(x, y, w, 1, colour);
            Fill(x, y + h - 1, w, 1, colour);
            Fill(x, y, 1, h, colour);
            Fill(x + w - 1, y, 1, h, colour);
        }

        /// <summary>Draws one line of text with a one-design-pixel drop shadow.</summary>
        /// <param name="line">The text; anything outside printable ASCII becomes a question mark.</param>
        /// <param name="x">Where the first cell starts.</param>
        /// <param name="y">The top of the cell.</param>
        /// <param name="colour">The encoded colour.</param>
        public void Text(string line, int x, int y, uint colour)
        {
            if (!text || line.Length == 0)
            {
                return;
            }

            Glyphs(line, x + 1, y + 1, Black);
            Glyphs(line, x, y, colour);
        }

        /// <summary>Draws a step's lamp: a 9 × 9 mark in the row's own colour.</summary>
        /// <param name="x">Left.</param>
        /// <param name="y">Top.</param>
        /// <param name="state">The step's state.</param>
        /// <param name="seconds">Wall seconds, for the running lamp's blink.</param>
        public void Lamp(int x, int y, StepState state, double seconds)
        {
            uint colour = LampColor(state);
            switch (state)
            {
                case StepState.Pending or StepState.Skipped:
                    Outline(x, y, 9, 9, colour);
                    break;

                case StepState.Running:
                    Outline(x, y, 9, 9, colour);
                    if ((int)(seconds * 3.0) % 2 == 0)
                    {
                        Fill(x + 2, y + 2, 5, 5, colour);
                    }

                    break;

                case StepState.Warning:
                    // A triangle, so a warning is told apart from a lamp by SHAPE as well as colour.
                    Canvas.SetPenColor(colour);
                    for (int row = 0; row < 9; row++)
                    {
                        int half = (row + 1) / 2;
                        Fill(x + 4 - half, y + row, (half * 2) + 1, 1, colour);
                    }

                    break;

                case StepState.Failed:
                    for (int i = 0; i < 9; i++)
                    {
                        Fill(x + i, y + i, 1, 1, colour);
                        Fill(x + 8 - i, y + i, 1, 1, colour);
                    }

                    break;

                default:
                    Fill(x, y, 9, 9, colour);
                    break;
            }
        }

        private void Glyphs(string line, int x, int y, uint colour)
        {
            Font? font = Canvas.Font9X16;
            int perRow = font.Texture.Width / font.CharacterWidth;
            Canvas.SetPenColor(colour);
            for (int i = 0; i < line.Length; i++)
            {
                // The 9 × 16 font is ASCII; the punctuation the pipeline writes gets the lookalike a
                // reader expects instead of a question mark, one character for one so the columns
                // still line up.
                char c = line[i];
                int index = c switch
                {
                    >= ' ' and <= '~' => c,
                    '—' or '–' or '−' => '-',
                    '·' or '•' or '…' => '.',
                    '‘' or '’' => '\'',
                    '“' or '”' => '"',
                    '✓' or '✔' => '+',
                    _ => '?',
                };
                int sourceX = (index % perRow) * font.CharacterWidth;
                int sourceY = (index / perRow) * font.CharacterHeight;
                int left = x + (i * CellWidth);
                for (int px = 0; px < font.CharacterWidth; px++)
                {
                    for (int py = 0; py < font.CharacterHeight; py++)
                    {
                        uint pixel = font.Texture.Data[((sourceX + px) * font.Texture.Height) + sourceY + py];
                        if (pixel == 0xFFFFFFFF)
                        {
                            Canvas.Rectangle(
                                originX + ((left + px) * scale),
                                originY + ((y + py) * scale),
                                scale,
                                scale);
                        }
                    }
                }
            }
        }
    }
}
