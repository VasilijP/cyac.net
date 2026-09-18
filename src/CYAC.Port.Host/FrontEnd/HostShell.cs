using System.Globalization;
using CYAC.Port.Core.Data;
using CYAC.Port.Host.Input;
using CYAC.Port.Host.Settings;
using CYAC.Port.Host.Stats;
using CYAC.Port.Render;
using mode13hx;
using mode13hx.Presentation;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// THE SHELL: the window's rasterizer.  It owns the front end and at most one sortie.
/// </summary>
/// <remarks>
/// <para>
/// Per frame exactly one of the two renders: the <see cref="FlightRasterizer"/> when a sortie is up,
/// the <see cref="FrontEnd"/> otherwise.  Input goes to whichever is active, so the front end never
/// sees a flight key and the flight never sees a menu key.
/// </para>
/// <para>
/// <b>The one door out of a sortie</b>.  A sortie ends when the flight rasterizer raises
/// <see cref="FlightRasterizer.ReturnToMenuRequested"/> — the moment the verdict stands, with no key
/// at all — the overlay was F1's placeholder and is direct-mode only now, or the ESC menu's End
/// Mission on a Test Flight, which has no debrief.  The shell then calls
/// <see cref="FlightFactory.Close"/>, which routes the sortie through <see cref="SortieRecorder"/>
/// exactly once, disposes the rasterizer (the tile pool joins) and resets the sound path, and then
/// lands on <see cref="DebriefingScreen"/> or on CHOOSE ACTIVITY
/// (<see cref="SortieOutcome.LandsOnDebriefing"/>).  The menu's Restart Mission (unmapped) still
/// restarts IN PLACE inside the rasterizer and never reaches here.
/// </para>
/// <para>
/// <b>The port adds nothing to the original's screens</b>.  There is no stats line, no ✔ and no
/// <c>?</c> dialog over the menu — the ESC menu's Port Settings and Mission Stats panels stay where
/// they are, over a flying sortie.
/// </para>
/// <para>
/// <b>What F2–F7 drop in.</b>  A new screen is a <see cref="FrontEndScreen"/> pushed on
/// <see cref="FrontEnd"/>'s stack; nothing in this file changes for it.  A screen that must START a
/// sortie returns a <see cref="FrontEndRequest"/> and the ONE place that turns a request into a
/// sortie is <see cref="Start"/>, which already carries the mission slot, difficulty, aircraft and
/// site in a <see cref="LastSortie"/> — so F2's mission picker and F4's hangar hand the shell a
/// filled-in <see cref="LastSortie"/> and need no new plumbing at all.
/// </para>
/// </remarks>
public sealed class HostShell : IRasterizer, IDisposable
{
    private readonly FrontEnd _frontEnd;
    private readonly FlightFactory _factory;
    private readonly IFlightInputSource _input;
    private readonly IReadOnlyList<Rgb24> _palette;
    private readonly FrontEndFonts _fonts;
    private readonly PortSettingsStore _settings;

    /// <summary>
    /// Parent fix — whether <see cref="Start"/> writes the Last Mission memory into
    /// <c>settings.json</c>.  A WINDOW run remembers; a HEADLESS run never does: the batteries, the
    /// replays and scripted front-end walks must not litter a player's file ("the
    /// command line wins for a run and is not written back" — and M3's "headless never writes stats
    /// unless <c>--stats</c> is explicit").  Before this flag a headless click walk rewrote the
    /// player's <c>settings.json</c> with the walk's own sortie.
    /// </summary>
    private readonly bool _rememberSorties;
    private readonly List<string> _log = [];

    private Sortie? _sortie;
    private Action? _exitAction;
    private bool _disposed;

    // The boot title sequence, or null in a shell that was asked for none (and in any tree without
    // the title art).  It owns the palette while it runs; the shell owns nothing new.
    private readonly TitleSequence? _title;

    // The pointer and the click, and the ONE cached delegate they hand requests back on (a
    // fresh Action per frame would allocate, which the front end must not).
    private readonly FrontEndMouse _mouse;
    private readonly Action<FrontEndRequest> _act;

    /// <summary>Builds the shell.</summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="factory">How a sortie is opened and closed.</param>
    /// <param name="input">Where a frame's input comes from.</param>
    /// <param name="palette">The game's palette, widened to 8 bits.</param>
    /// <param name="fonts">The three front-end fonts (F2: propbold, prop3, 4x6).</param>
    /// <param name="settings">The port settings store — <c>frontend-scale</c> and the memory.</param>
    /// <param name="rememberSorties">Whether a sortie is written into the Last Mission memory.</param>
    /// <param name="withTitle">
    /// Whether the shell opens on the BOOT TITLE SEQUENCE (<see cref="TitleSequence"/>): the window
    /// goes black, "Electronic Arts presents" fades in, holds, cross-fades to the CHUCK YEAGER'S AIR
    /// COMBAT title, holds until a key, fades to black, and CHOOSE ACTIVITY fades up — the original's
    /// own boot, <c>ui_title_screen_pair_show @image@0x24DB0</c>.  Both menu-mode routes ask for it;
    /// DIRECT MODE never builds a shell at all, so it never has one.
    /// </param>
    public HostShell(
        DataTree tree,
        FlightFactory factory,
        IFlightInputSource input,
        IReadOnlyList<Rgb24> palette,
        FrontEndFonts fonts,
        PortSettingsStore settings,
        bool rememberSorties = true,
        bool withTitle = false)
    {
        _rememberSorties = rememberSorties;
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(fonts);
        ArgumentNullException.ThrowIfNull(settings);
        _factory = factory;
        _input = input;
        _palette = palette;
        _fonts = fonts;
        _settings = settings;
        _frontEnd = new FrontEnd(tree, () => settings.LastSortie, palette);

        // The original's own arrow, from the original's own cursor.pic.  A tree without it simply
        // has no pointer (the keyboard model of F1–F5 is untouched by its absence).
        _mouse = new FrontEndMouse(tree);
        _act = Act;

        // The boot title pair.  A tree without title0v/title1v simply has no sequence and the
        // menu is up on frame 0.
        _title = withTitle ? TitleSequence.Load(tree, palette) : null;
    }

    /// <summary>The boot title sequence while it runs, null once it is over.</summary>
    public TitleSequence? Title => _title is { Done: false } running ? running : null;

    /// <summary>The front end's mouse: the pointer, the hover and the click.</summary>
    public FrontEndMouse Mouse => _mouse;

    /// <summary>The front end, for the tests and the headless summary.</summary>
    public FrontEnd Menu => _frontEnd;

    /// <summary>The sortie in the air, or null while the menu is up.</summary>
    public Sortie? Sortie => _sortie;

    /// <summary>The flight rasterizer, or null while the menu is up.</summary>
    public FlightRasterizer? Flight => _sortie?.Rasterizer;

    /// <summary>How many rows of the last frame the 3-D world wrote (0 in the menu).</summary>
    public int WorldRows => _sortie?.Rasterizer.WorldRows ?? 0;

    /// <summary>Whether the game has been asked to close.</summary>
    public bool ExitRequested { get; private set; }

    /// <summary>Every sortie start and end, and every front-end refusal.</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>How many frames the shell has drawn with the FRONT END up.</summary>
    public long FrontEndFrames { get; private set; }

    /// <summary>
    /// What the sortie that has just ended came to, or null before the first one ends.
    /// </summary>
    /// <remarks>
    /// The hand-off: the DEBRIEFING and MISSION STATS screens are drawn
    /// from THIS, so they never reach into a rasterizer that no longer exists — by the time a
    /// front-end screen is up, the sortie has been disposed.  F1 only records it.
    /// </remarks>
    public SortieOutcome? LastOutcome { get; private set; }

    /// <summary>Sets what "Exit to DOS" runs (M1's fix: raise a flag, the window closes itself).</summary>
    /// <param name="exitAction">The action.</param>
    public void SetExitAction(Action exitAction) => _exitAction = exitAction;

    /// <summary>Starts a sortie at once, without going through the menu.</summary>
    /// <param name="choice">Which sortie.</param>
    /// <remarks>
    /// The start rule: <c>fly --mission N</c> / <c>--test-flight X</c> / <c>--seed-trace</c> go
    /// straight into flight as they always have; the menu is where the sortie's END lands.
    /// </remarks>
    /// <param name="keepSeedTrace">
    /// True only for the sortie the command line asked for, which may carry a <c>--seed-trace</c>.
    /// </param>
    public void Start(LastSortie choice, bool keepSeedTrace = false)
    {
        ArgumentNullException.ThrowIfNull(choice);
        EndSortie("a new sortie was started");
        _sortie = _factory.Open(choice, keepSeedTrace);
        _sortie.Rasterizer.SetExitAction(RequestExit);
        if (_rememberSorties)
        {
            _settings.Remember(choice);
        }

        Note($"sortie: {choice.Describe()}");
    }

    /// <inheritdoc/>
    public void Render(FrameBuffer buffer, double secondsSinceLastFrame)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_sortie is { } flying)
        {
            flying.Rasterizer.Render(buffer, secondsSinceLastFrame);
            if (flying.Rasterizer.ExitRequested)
            {
                RequestExit();
            }
            else if (flying.Rasterizer.ReturnToMenuRequested)
            {
                EndSortie("the sortie ended");

                // The ONE door out, and where it lands.  A historic sortie that reached a verdict
                // lands on DEBRIEFING; a Test Flight, or a sortie left before it ended, goes
                // straight back to CHOOSE ACTIVITY, which is what the original does too.  Either
                // way EndSortie above has already routed it through SortieRecorder exactly once.
                if (LastOutcome is { LandsOnDebriefing: true } debriefed)
                {
                    _frontEnd.ShowDebriefing(debriefed);
                }
                else
                {
                    _frontEnd.ReturnToRoot();
                }
            }

            return;
        }

        RenderFrontEnd(buffer, secondsSinceLastFrame);
    }

    /// <summary>Abandons an unfinished sortie — the window closed, or the headless loop stopped.</summary>
    /// <param name="why">What the recorder's log should say.</param>
    public void EndSortie(string why)
    {
        if (_sortie is not { } sortie)
        {
            return;
        }

        _sortie = null;
        LastOutcome = SortieOutcome.Of(sortie, _factory.Recorder);
        _factory.Close(sortie, why);
        Note($"sortie over: {why}");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EndSortie("the run ended");
    }

    /// <summary>The scale the front end draws at on a window this size.</summary>
    /// <param name="width">The window's width.</param>
    /// <param name="height">Its height.</param>
    /// <remarks>
    /// <c>frontend-scale</c> is <c>auto</c> (the largest whole 320×200 that fits) or a number.  The
    /// setting is read per frame, so the M2 dialog's own row moves the menu live.
    /// </remarks>
    public int Scale(int width, int height)
    {
        string word = PortSettings.Find(FrontEndScaleSetting) is { } setting
            ? _settings.Value(setting).Word
            : "auto";
        return int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fixedScale)
            && fixedScale > 0
                ? Math.Min(fixedScale, FrontEndPainter.AutoScale(width, height) * MaxOversizeFactor)
                : FrontEndPainter.AutoScale(width, height);
    }

    /// <summary>The settings-table row that sizes the front end.</summary>
    public const string FrontEndScaleSetting = "frontend-scale";

    /// <summary>
    /// How far past the auto scale an explicit <c>--frontend-scale</c> may go before it is clamped.
    /// </summary>
    /// <remarks>
    /// A screen scaled larger than the window would simply lose its right and bottom edges; one step
    /// past the fit is deliberate (a player who wants the text bigger and can live with the crop),
    /// two would be a mistake nobody meant.
    /// </remarks>
    public const int MaxOversizeFactor = 2;

    private void RenderFrontEnd(FrameBuffer buffer, double secondsSinceLastFrame)
    {
        FrameDescriptor? frame = buffer.StartNextFrame();
        FlightInputFrame input = _input.Sample(secondsSinceLastFrame);
        int scale = Scale(buffer.Width, buffer.Height);

        // The boot title sequence owns the frame until it is over: the composite and its own
        // palette while a title is up, then the menu itself through the palette ramp.  No key
        // reaches the front end while it runs — the original drains the keyboard before its event
        // loop (image@0x25735) — and the pointer is not drawn (TitleSequence's remarks).
        if (_title is { Done: false } title)
        {
            RenderTitle(title, buffer, frame, input, secondsSinceLastFrame, scale);
            return;
        }

        HandleInput(input, scale);

        PixelTarget target = new PixelTarget(
            buffer.Data.AsSpan(frame.Offset, buffer.FrameSize),
            buffer.Width,
            buffer.Height,
            buffer.Height,
            PixelChannelOrder.BlueHigh);
        (int originX, int originY) = FrontEndPainter.Origin(buffer.Width, buffer.Height, scale);
        // The surface carries the frame's own pixels as well as the target, so the hangar's 3-D
        // column can be rendered straight into a sub-rectangle of the window.
        FrontEndPainter.Surface surface = new FrontEndPainter.Surface(
            target,
            buffer.Data.AsSpan(frame.Offset, buffer.FrameSize),
            _palette,
            scale,
            originX,
            originY);
        if (originX != 0 || originY != 0)
        {
            surface.Clear(FrontEndPainter.BorderIndex);
        }

        _frontEnd.Render(surface, _fonts);

        // The pointer is drawn LAST, over everything the screen painted, at the same integer
        // scale.  It is never part of what a screen draws, which is what keeps the F1–F5 1× pixel
        // diffs (which render a SCREEN, not a shell frame) pointer-free.
        _mouse.Pointer?.Draw(surface);

        FrontEndFrames++;
        buffer.FinishFrame(frame);
    }

    /// <summary>
    /// One frame of the boot title sequence.
    /// </summary>
    /// <remarks>
    /// While a TITLE is up the frame is the composite (<see cref="TitleSequence.Picture"/>) through
    /// the sequence's own palette, and the letterbox is BLACK (index 0 of both title palettes) —
    /// the original is 320 × 200 and has no border at all, and a grey frame around a fading title
    /// would be the port's own invention.  From <see cref="TitlePhase.MenuFadeIn"/> on it is the
    /// FRONT END that is painted, through the same ramp: CHOOSE ACTIVITY is drawn complete and dark
    /// and the palette brings it up, which is exactly what
    /// <c>ui_choose_activity_screen @image@0x256D2</c> does — it draws the panel, the widgets and
    /// the title (<c>image@0x25701..0x25730</c>) and only then calls
    /// <c>vga_palette_dirty_crossfade_restore @image@0x2505C</c> (<c>image@0x2573E</c>), whose whole
    /// body is a 129-step fade from black to the runtime palette, gated on the
    /// <c>g_vga_dac_dirty_flag [0x3420]</c> the title screen set.
    /// </remarks>
    /// <param name="title">The sequence.</param>
    /// <param name="buffer">The frame buffer.</param>
    /// <param name="frame">The frame being written.</param>
    /// <param name="input">This frame's input.</param>
    /// <param name="secondsSinceLastFrame">How much time this frame is worth.</param>
    /// <param name="scale">Host pixels per design pixel.</param>
    private void RenderTitle(
        TitleSequence title,
        FrameBuffer buffer,
        FrameDescriptor frame,
        in FlightInputFrame input,
        double secondsSinceLastFrame,
        int scale)
    {
        TitlePhase was = title.Phase;
        title.Advance(secondsSinceLastFrame, AnyKey(input));
        if (title.Phase != was)
        {
            Note($"title: {was} → {title.Phase}");
        }

        PixelTarget target = new PixelTarget(
            buffer.Data.AsSpan(frame.Offset, buffer.FrameSize),
            buffer.Width,
            buffer.Height,
            buffer.Height,
            PixelChannelOrder.BlueHigh);
        (int originX, int originY) = FrontEndPainter.Origin(buffer.Width, buffer.Height, scale);
        FrontEndPainter.Surface surface = new FrontEndPainter.Surface(
            target,
            buffer.Data.AsSpan(frame.Offset, buffer.FrameSize),
            title.Palette,
            scale,
            originX,
            originY);
        if (originX != 0 || originY != 0)
        {
            surface.Clear(title.ShowsTitles ? TitleLetterboxIndex : FrontEndPainter.BorderIndex);
        }

        if (title.ShowsTitles)
        {
            FrontEndPainter.Backdrop(surface, title.Picture);
        }
        else
        {
            _frontEnd.Render(surface, _fonts);
        }

        FrontEndFrames++;
        buffer.FinishFrame(frame);
    }

    /// <summary>
    /// The letterbox colour while a TITLE is up: index 0, black in both title palettes.
    /// </summary>
    public const int TitleLetterboxIndex = 0;

    /// <summary>
    /// Whether ANY key's press edge landed this frame, which is what the title sequence's polls
    /// test.
    /// </summary>
    /// <remarks>
    /// <c>kbd_event_poll_and_classify @image@0x20488</c> reports whatever is in the keyboard buffer,
    /// so every key counts and no mouse button does — the title screen's three polls
    /// (<c>image@0x24FFA</c> and the two <c>timed_tick_wait(…, dl=1)</c> calls) never look at
    /// <c>g_mouse_button_state [0x4741]</c>.
    /// </remarks>
    /// <param name="input">The frame's input.</param>
    private static bool AnyKey(in FlightInputFrame input) =>
        input.FrontEndKeys is { Count: > 0 } || input.MenuTypeAhead != '\0';

    /// <summary>
    /// One front-end frame's input — the pointer, the keys and the click, in
    /// <see cref="FrontEndMouse"/>'s own order (the rule that keeps the pointer and the <c>►
    /// ◄</c> ring agreeing about what Space will press is documented there).
    /// </summary>
    /// <param name="input">The frame's input.</param>
    /// <param name="scale">Host pixels per design pixel, for the motion deltas.</param>
    private void HandleInput(in FlightInputFrame input, int scale) =>
        _mouse.Frame(_frontEnd, input, scale, _act);

    private void Act(FrontEndRequest request)
    {
        switch (request)
        {
            case FrontEndRequest.FlyMission:
                // The MISSION DESCRIPTION's Ok filled PendingSortie with the chosen slot and
                // difficulty; before F2 there was no picker and this fell back to mission 0.
                Start(_frontEnd.PendingSortie ?? _factory.HistoricSortie());
                break;
            case FrontEndRequest.FlyTestFlight:
                // The HANGAR's Fly filled PendingSortie with the aeroplane the player paged to;
                // before F4 there was no hangar and this fell back to the command line's.
                Start(_frontEnd.PendingSortie is { IsMission: false, Aircraft.Length: > 0 } chosen
                    ? chosen
                    : _factory.TestFlightSortie());
                break;
            case FrontEndRequest.FlyCustomMission:
                // CREATE MISSION's Done filled PendingSortie with the sentence's picks.
                if (_frontEnd.PendingSortie is { IsCustom: true } custom)
                {
                    Start(custom);
                }

                break;
            case FrontEndRequest.FlyLastSortie:
                if (_settings.LastSortie is { } remembered)
                {
                    Start(remembered);
                }

                break;
            case FrontEndRequest.Exit:
                RequestExit();
                break;
            default:
                break;
        }
    }

    private void RequestExit()
    {
        ExitRequested = true;
        _exitAction?.Invoke();
    }

    private void Note(string what) =>
        _log.Add(string.Create(CultureInfo.InvariantCulture, $"shell {what}"));
}
