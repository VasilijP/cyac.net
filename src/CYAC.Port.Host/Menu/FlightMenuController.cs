using CYAC.Port.Core.Model.Mission;
using System.Globalization;
using System.Text.Json;
using CYAC.Port.Core.Data;
using CYAC.Port.Host.Settings;
using CYAC.Port.Host.Stats;
using CYAC.Port.Render;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host.Menu;

/// <summary>How the menu's panels are drawn this frame.</summary>
/// <remarks>Everything here is recomputed per frame; nothing about a check mark is cached.</remarks>
public readonly struct FlightMenuLook
{
    private readonly HashSet<int> _checked;

    /// <summary>Creates the look.</summary>
    /// <param name="checkedIds">The ids whose row shows a check mark this frame.</param>
    /// <param name="panelOpacity">How opaque the strip and popup panels are, 0..1.</param>
    /// <param name="helpUsed">Whether the strip shows the "help used" square.</param>
    public FlightMenuLook(HashSet<int> checkedIds, double panelOpacity, bool helpUsed)
    {
        _checked = checkedIds;
        PanelOpacity = panelOpacity;
        HelpUsed = helpUsed;
    }

    /// <summary>How opaque the panels are.</summary>
    public double PanelOpacity { get; }

    /// <summary>Whether the strip's "help used" square is shown.</summary>
    public bool HelpUsed { get; }

    /// <summary>Whether a row shows a check mark.</summary>
    /// <param name="id">The row's id.</param>
    public bool IsChecked(int id) => _checked is not null && _checked.Contains(id);

    /// <summary>Whether a row is live.</summary>
    /// <param name="id">The row's id.</param>
    public static bool IsEnabled(int id) => FlightMenuActions.IsEnabled(id);
}

/// <summary>
/// The in-flight ESC menu: the modal, its wiring to the host, and its per-frame check marks.
/// </summary>
/// <remarks>
/// <para>
/// The controller owns the AUGMENTED menu table: the six shipped menus exactly as the executable
/// carries them, with four, M1b port rows spliced into the <c>?</c> menu — "Restart Mission" before
/// "End Mission" (M0's one restart path), then "Menu Opacity", "Menu Size", "Port Settings..." and
/// M3's "Mission Stats..." after the credits. It relabels "Exit to DOS" as "Exit" and keeps its id,
/// because the host has no DOS to exit to. Nothing else about the shipped menus is changed.
/// </para>
/// <para>
/// <b>Pausing.</b> While the bar is up the host does not step the session; it keeps DRAWING every
/// presented frame from the frozen state, so an option changed in the menu shows on the very next
/// frame over the same scene.  The pieces of presentation state that would otherwise drift are
/// pinned in <c>FlightRasterizer.Render</c>
/// </para>
/// </remarks>
public sealed class FlightMenuController
{
    /// <summary>The opacities <see cref="FlightMenuAction.MenuOpacity"/> cycles through.</summary>
    /// <remarks>
    /// 1.0 is the original's own look; 0.85 is the default, because at 0.85 the panel still reads as
    /// a solid grey widget at a glance while a tracer, an explosion or a smoke column behind it is
    /// unmistakably visible — below about 0.6 the bevel stops reading as a raised panel.
    /// </remarks>
    public static readonly double[] OpacitySteps = [1.0, 0.85, 0.6, 0.4];

    /// <summary>The default panel opacity — what <c>--menu-opacity</c> starts at.</summary>
    public const double DefaultOpacity = 0.85;

    /// <summary>The <c>--menu-scale</c> value that means "work it out from the window".</summary>
    public const int AutoScale = 0;

    private readonly FlightRasterizer _host;
    private readonly FlightMenuTable _shipped;
    private readonly FlightMenuTable _table;
    private readonly FlightMenuState _state;
    private readonly FlightMenuRenderer _renderer = new();
    private readonly HashSet<int> _checked = [];
    private readonly List<string> _log = [];
    private readonly IReadOnlyList<string> _aboutLines;
    private readonly CockpitFont _font;

    private double _opacity;
    private int _scale;
    private bool _showAbout;

    /// <summary>The Port Settings dialog, when the run has a settings store.</summary>
    private readonly PortSettingsDialog? _dialog;

    /// <summary>The store the dialog changes, and the menu's own rows persist through.</summary>
    private readonly PortSettingsStore? _store;

    /// <summary>The read-only Mission Stats panel, when the run keeps statistics.</summary>
    private readonly MissionStatsDialog? _stats;

    /// <summary>Creates the controller.</summary>
    /// <param name="host">The rasterizer whose state the items change.</param>
    /// <param name="tree">The data tree the menu document comes from.</param>
    /// <param name="font">The game's own <c>propbold</c> font (<c>CockpitAssets.MenuFontName</c>).</param>
    /// <param name="opacity">The <c>--menu-opacity</c> value.</param>
    /// <param name="scale">
    /// The <c>--menu-scale</c> value: host pixels per design pixel, or <see cref="AutoScale"/> for "a
    /// quarter of the cockpit's".
    /// </param>
    /// <param name="store">
    /// The port settings store the "Port Settings…" dialog changes and the six option rows persist
    /// through, or null (the menu then flips the live state alone, as it did in M1).
    /// </param>
    /// <param name="stats">
    /// The statistics store the read-only "Mission Stats…" panel shows, or null (the row then says the run
    /// keeps none).
    /// </param>
    public FlightMenuController(
        FlightRasterizer host,
        DataTree tree,
        CockpitFont font,
        double opacity,
        int scale = AutoScale,
        PortSettingsStore? store = null,
        PortStatsStore? stats = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(font);
        _host = host;
        _font = font;
        _shipped = FlightMenuTable.Load(tree);
        _aboutLines = ReadAboutLines(tree);
        _table = Augment(_shipped, tree.InFlightStrings.MenuExitWord);
        _state = new FlightMenuState(_table);
        _opacity = Math.Clamp(opacity, 0.0, 1.0);
        _scale = Math.Max(AutoScale, scale);
        _store = store;
        _dialog = store is null ? null : new PortSettingsDialog(store);
        _stats = stats is null ? null : new MissionStatsDialog(stats);
    }

    /// <summary>Whether the bar is up — and therefore whether the simulation is frozen.</summary>
    public bool IsOpen => _state.IsOpen;

    /// <summary>The menu's own log: every open, close and activation, for the headless summary.</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>How many presented frames have been drawn with the simulation frozen.</summary>
    public long PausedFrames { get; private set; }

    /// <summary>The panel opacity in force — <c>--menu-opacity</c>, or the settings row.</summary>
    public double Opacity
    {
        get => _opacity;
        set => _opacity = Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>The Port Settings dialog, or null when the run has no settings store.</summary>
    public PortSettingsDialog? Dialog => _dialog;

    /// <summary>The Mission Stats panel, or null when the run keeps no statistics.</summary>
    public MissionStatsDialog? Stats => _stats;

    /// <summary>
    /// The <c>--menu-scale</c> SETTING: host pixels per design pixel, or
    /// <see cref="AutoScale"/> (0) when the window decides.
    /// </summary>
    public int ScaleSetting
    {
        get => _scale;
        set => _scale = Math.Clamp(value, AutoScale, Configuration.FlyOptions.MaxMenuScale);
    }

    /// <summary>The scale actually used on a window this size.</summary>
    /// <param name="width">The window's width in pixels.</param>
    /// <param name="height">Its height.</param>
    public int EffectiveScale(int width, int height) =>
        _scale > 0 ? _scale : FlightMenuRenderer.AutoScale(width, height);

    /// <summary>What the readout says about the menu's size.</summary>
    /// <param name="width">The window's width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <returns>e.g. <c>menu 1x (auto)</c>.</returns>
    public string ScaleDescription(int width, int height) => string.Create(
        CultureInfo.InvariantCulture,
        $"menu {EffectiveScale(width, height)}x{(_scale > 0 ? string.Empty : " (auto)")}");

    /// <summary>The six menus as the port shows them (the shipped ones plus the port's own rows).</summary>
    public IReadOnlyList<FlightMenu> Menus => _table.Menus;

    /// <summary>The shipped menus, exactly as the executable carries them.</summary>
    public IReadOnlyList<FlightMenu> ShippedMenus => _shipped.Menus;

    /// <summary>Applies one frame's menu keys.</summary>
    /// <param name="keys">The menu-key edges this frame, in order.</param>
    /// <param name="typed">A letter or digit typed this frame, for type-ahead, or <c>'\0'</c>.</param>
    /// <param name="heldLeft">whether the LEFT arrow is held (the dialog's number acceleration).</param>
    /// <param name="heldRight">whether RIGHT is held.</param>
    public void HandleInput(
        IReadOnlyList<FlightMenuKey>? keys,
        char typed,
        bool heldLeft = false,
        bool heldRight = false)
    {
        // The Port Settings dialog is modal OVER the bar, exactly as the About box is: while it is
        // up it takes every key, and ESC backs up one level to the bar (the original's own menu
        // behaviour) with the simulation still frozen behind both.
        if (_dialog is { IsOpen: true } dialog)
        {
            if (dialog.HandleInput(keys, typed, heldLeft, heldRight))
            {
                Note("port settings closed");
            }

            return;
        }

        // The Mission Stats panel is modal in exactly the same way, and read-only: it takes every
        // key while it is up and ESC backs up one level to the bar.
        if (_stats is { IsOpen: true } statsPanel)
        {
            if (statsPanel.HandleInput(keys))
            {
                Note("mission stats closed");
            }

            return;
        }

        if (keys is not null)
        {
            foreach (FlightMenuKey key in keys)
            {
                // The About box is modal over the menu: any menu key closes it and nothing else.
                if (_showAbout)
                {
                    _showAbout = false;
                    continue;
                }

                // The script's `settings` word (and any future direct key): open the bar if it is
                // down and go straight into the dialog.
                if (key is FlightMenuKey.Settings or FlightMenuKey.Stats)
                {
                    if (!_state.IsOpen)
                    {
                        _state.Open();
                        Note($"opened (sim frozen at step {_host.Session.StepsRun:N0})");
                    }

                    if (key == FlightMenuKey.Settings)
                    {
                        OpenSettings();
                    }
                    else
                    {
                        OpenStats();
                    }

                    return;
                }

                bool wasOpen = _state.IsOpen;
                FlightMenuItem? chosen = _state.Press(key);
                if (!wasOpen && _state.IsOpen)
                {
                    Note($"opened (sim frozen at step {_host.Session.StepsRun:N0})");
                }
                else if (wasOpen && !_state.IsOpen)
                {
                    Note($"closed (sim resumes at step {_host.Session.StepsRun:N0})");
                }

                if (chosen is { } item)
                {
                    Activate(item);
                }
            }
        }

        if (typed != '\0' && _state.IsOpen && !_showAbout && _state.TypeAhead(typed))
        {
            // Silent: type-ahead only moves the highlight.
        }
    }

    /// <summary>Draws the bar, the open popup and — when it is up — the About box.</summary>
    /// <param name="target">The whole window.</param>
    /// <param name="palette">The game's palette, widened to 8 bits.</param>
    public void Render(PixelTarget target, IReadOnlyList<Rgb24> palette)
    {
        if (!_state.IsOpen)
        {
            return;
        }

        PausedFrames++;
        RefreshCheckmarks();
        int scale = EffectiveScale(target.Width, target.Height);
        FlightMenuLook look = new FlightMenuLook(_checked, _opacity, _host.HelpFeatureUsed);
        _renderer.Render(target, _font, palette, _state, in look, scale);
        if (_showAbout)
        {
            FlightMenuAboutBox.Render(target, _font, palette, _aboutLines, _opacity, scale);
        }

        // The Port Settings dialog sits over the bar, at the SAME integer scale (M1b §4.3: it
        // must never invent a scale of its own, so "Menu Size" moves the dialog too).
        if (_dialog is { IsOpen: true } dialog)
        {
            dialog.Render(target, _font, palette, _opacity, scale);
        }

        // And so does the Mission Stats panel (only one of the two is ever up).
        if (_stats is { IsOpen: true } statsPanel)
        {
            statsPanel.Render(target, _font, palette, _opacity, scale);
        }
    }

    /// <summary>One line per row: its id, its label and what the port does with it.</summary>
    /// <remarks>The headless summary prints it, so a run says for itself what is and is not wired.</remarks>
    public IEnumerable<string> WiringLines()
    {
        foreach (FlightMenu menu in _table.Menus)
        {
            foreach (FlightMenuItem item in menu.Items)
            {
                FlightMenuEffect effect = FlightMenuActions.For(item.Id);
                string label = item.Label.Replace(FlightMenuTable.ArrowGlyph, '>');
                yield return string.Create(
                    CultureInfo.InvariantCulture,
                    $"0x{item.Id:X3} {menu.Title,-9} {label,-27} "
                        + $"{(FlightMenuActions.IsEnabled(item.Id) ? "WIRED   " : "disabled")} "
                        + $"{effect.Action,-18} {effect.Why}");
            }
        }
    }

    /// <summary>
    /// Recomputes every check mark from LIVE state, the way
    /// <c>ingame_menu_state_refresh @image@0x213A0</c> does before it opens the bar — never cached.
    /// </summary>
    private void RefreshCheckmarks()
    {
        _checked.Clear();

        // System.Sound..Lock Sounds — the five bits of g_audio_mute_mask [0xE483].
        int mask = _host.SoundMask;
        for (int bit = 0; bit < 5; bit++)
        {
            if ((mask & (1 << bit)) != 0)
            {
                _checked.Add(0x100 + bit);
            }
        }

        // System.Keyboard — [0xE45C] == 2 is the mode the port is permanently in.
        _checked.Add(0x105);

        // System.1x Time — [0xF104] == 0; the port never compresses time.
        _checked.Add(0x109);

        // View — [0xC320], exactly as the refresher's own cases read it.
        foreach (FlightMenuItem item in _table.Menus[2].Items)
        {
            FlightMenuEffect effect = FlightMenuActions.For(item.Id);
            if (effect.Action == FlightMenuAction.View && effect.Parameter == (int)_host.View)
            {
                _checked.Add(item.Id);
            }
        }

        // Graphics — [0xF108] detail, [0x0781] horizon, [0xB6] clouds, [0xC31E] explosions, [0xB0].
        _checked.Add(_host.Lod == LodPolicy.Max ? 0x302 : 0x301);
        if (_host.DitheredHorizon)
        {
            _checked.Add(0x304);
        }

        if (_host.CloudsVisible)
        {
            _checked.Add(0x305);
        }

        if (_host.BitmapExplosions)
        {
            _checked.Add(0x306);
        }

        if (_host.FlightInfoVisible)
        {
            _checked.Add(0x307);
        }

        // Help — the four windows, from the live [0xF1CB] mirror.
        foreach ((int id, CockpitOverlayFlags window) in HelpWindowItems)
        {
            if (_host.Overlays.HasFlag(window))
            {
                _checked.Add(id);
            }
        }
    }

    /// <summary>Help &gt; Map / Envelope / Target / Yeager Window and the bit each one shows.</summary>
    internal static readonly (int Id, CockpitOverlayFlags Window)[] HelpWindowItems =
    [
        (0x406, CockpitOverlayFlags.Map), (0x407, CockpitOverlayFlags.Envelope),
        (0x408, CockpitOverlayFlags.Target), (0x409, CockpitOverlayFlags.Yeager),
    ];

    private void Activate(FlightMenuItem item)
    {
        FlightMenuEffect effect = FlightMenuActions.For(item.Id);
        if (effect.Action is FlightMenuAction.Disabled or FlightMenuAction.Caption)
        {
            Note($"0x{item.Id:X3} \"{item.Label}\" is not wired — {effect.Why}");
            return;
        }

        Note($"0x{item.Id:X3} \"{item.Label}\" → {effect.Action}");
        switch (effect.Action)
        {
            case FlightMenuAction.RestartMission:
                _state.Close();
                _host.RestartMission();
                break;
            case FlightMenuAction.EndMission:
                _state.Close();
                _host.EndMission();
                break;
            case FlightMenuAction.Exit:
                _state.Close();
                _host.RequestExit();
                break;
            case FlightMenuAction.About:
                _showAbout = true;
                break;
            case FlightMenuAction.PortSettings:
                OpenSettings();
                break;
            case FlightMenuAction.MissionStats:
                OpenStats();
                break;
            case FlightMenuAction.MenuOpacity:
                // Through the settings table, so the choice persists (M2: "the existing menu
                // items that flip options go through the SAME table").
                Persist(
                    "menu-opacity",
                    SettingValue.Of(OpacitySteps[
                        (Array.FindIndex(OpacitySteps, o => Math.Abs(o - _opacity) < 0.001) + 1)
                            % OpacitySteps.Length]),
                    () => _opacity = OpacitySteps[
                        (Array.FindIndex(OpacitySteps, o => Math.Abs(o - _opacity) < 0.001) + 1)
                            % OpacitySteps.Length]);
                break;
            case FlightMenuAction.MenuSize:
                // 1 → 2 → 3 → 4 → auto → 1.  Picked by eye on the frozen scene.
                Persist(
                    "menu-scale",
                    SettingValue.Choice(NextScaleWord()),
                    () => _scale = _scale >= FlightMenuRenderer.MaxCycledScale
                        ? AutoScale
                        : _scale + 1);
                break;
            case FlightMenuAction.SoundMaskBit:
                Persist(
                    "sound-mask",
                    SettingValue.Of(_host.SoundMask ^ effect.Parameter),
                    () => _host.SoundMask ^= effect.Parameter);
                break;
            case FlightMenuAction.DetailLevel:
                Persist(
                    "lod",
                    SettingValue.Choice(
                        (LodPolicy)effect.Parameter == LodPolicy.Max ? "max" : "classic"),
                    () => _host.Lod = (LodPolicy)effect.Parameter);
                break;
            case FlightMenuAction.DitheredHorizon:
                // The row is a TOGGLE of the original's [0x0781]; the port's own horizon setting has
                // three values, so the toggle moves between REFINED and FLAT and leaves CLASSIC to
                // the dialog.
                Persist(
                    "horizon",
                    SettingValue.Choice(_host.DitheredHorizon ? "flat" : "refined"),
                    () => _host.DitheredHorizon = !_host.DitheredHorizon);
                break;
            case FlightMenuAction.Clouds:
                Persist(
                    "clouds",
                    SettingValue.Choice(_host.CloudsVisible ? "off" : "on"),
                    () => _host.CloudsVisible = !_host.CloudsVisible);
                break;
            case FlightMenuAction.BitmapExplosions:
                Persist(
                    "bitmap-explosions",
                    SettingValue.Toggle(!_host.BitmapExplosions),
                    () => _host.BitmapExplosions = !_host.BitmapExplosions);
                break;
            case FlightMenuAction.FlightInfo:
                Persist(
                    "flight-info",
                    SettingValue.Toggle(!_host.FlightInfoVisible),
                    () => _host.FlightInfoVisible = !_host.FlightInfoVisible);
                break;
            case FlightMenuAction.View:
                _host.SelectView((ViewMode)effect.Parameter);
                break;
            case FlightMenuAction.OverlayWindow:
            {
                // Help > … Window: through the window's own persisted row when a store is attached
                // (it applies SetOverlay), straight to the live byte otherwise.
                CockpitOverlayFlags window = (CockpitOverlayFlags)effect.Parameter;
                bool up = _host.Overlays.HasFlag(window);
                Persist(
                    WindowRowName(window),
                    SettingValue.Toggle(!up),
                    () => _host.SetOverlay(window, !up));
                break;
            }

            default:
                break;
        }
    }

    private static string WindowRowName(CockpitOverlayFlags window) => window switch
    {
        CockpitOverlayFlags.Envelope => "window-envelope",
        CockpitOverlayFlags.Target => "window-target",
        CockpitOverlayFlags.Map => "window-map",
        _ => "window-yeager",
    };

    /// <summary>Opens the Port Settings dialog, or says why the run has none.</summary>
    private void OpenSettings()
    {
        if (_dialog is null)
        {
            _host.PostNotice("NO SETTINGS FILE FOR THIS RUN");
            Note("port settings unavailable — the run has no settings store");
            return;
        }

        _dialog.Open();
        Note("port settings opened");
    }

    /// <summary>Opens the Mission Stats panel at the mission being flown.</summary>
    private void OpenStats()
    {
        if (_stats is null)
        {
            _host.PostNotice("THIS RUN KEEPS NO STATISTICS");
            Note("mission stats unavailable — the run has no statistics store");
            return;
        }

        _stats.Open(_host.CurrentMissionKey);
        Note("mission stats opened");
    }

    /// <summary>
    /// Changes an option through the SETTINGS TABLE when the run has a store, so the row
    /// persists to <c>settings.json</c>, and directly otherwise (M1's behaviour).
    /// </summary>
    /// <param name="name">The setting's command-line name.</param>
    /// <param name="value">Its new value.</param>
    /// <param name="fallback">What to do when there is no store.</param>
    private void Persist(string name, SettingValue value, Action fallback)
    {
        if (_store is { } store && PortSettings.Find(name) is { } setting)
        {
            store.Set(setting, value);
            return;
        }

        fallback();
    }

    /// <summary>The next word of the "Menu Size" cycle: 1 → 2 → 3 → 4 → auto.</summary>
    private string NextScaleWord() =>
        _scale >= FlightMenuRenderer.MaxCycledScale
            ? "auto"
            : (_scale + 1).ToString(CultureInfo.InvariantCulture);

    private void Note(string what) => _log.Add(string.Create(
        CultureInfo.InvariantCulture, $"menu {what}"));

    /// <summary>
    /// The shipped menus with the port's own rows spliced into the <c>?</c> menu, and the exit-to-DOS
    /// row relabelled.
    /// </summary>
    /// <param name="shipped">The shipped menus.</param>
    /// <param name="exitWord">
    /// The front end's own exit word (DGROUP <c>[0x3686]</c>, from the tree), which the relabelled
    /// row reads.
    /// </param>
    private static FlightMenuTable Augment(FlightMenuTable shipped, string exitWord)
    {
        FlightMenu help = shipped.Menus[0];
        List<FlightMenuItem> rows = new List<FlightMenuItem>(help.Items.Count + 3);
        foreach (FlightMenuItem item in help.Items)
        {
            // Restart Mission goes immediately before End Mission, which is the row it is the
            // kinder cousin of: both leave the sortie, one of them comes straight back.
            if (item.Id == 0x002)
            {
                rows.Add(new FlightMenuItem(
                    FlightMenuActions.RestartMissionId,
                    0,
                    rows.Count,
                    FlightMenuActions.RestartMissionLabel,
                    FlightMenuActions.RestartMissionAccelerator,
                    SectionEnd: false));
            }

            rows.Add(item with
            {
                // The exit-to-DOS row keeps its id and takes the front end's exit word; "About ..."
                // becomes a section end so the two port rows below it read as the port's own group.
                Label = item.Id == 0x003 ? exitWord : item.Label,
                SectionEnd = item.Id == 0x004 || item.SectionEnd,
                Index = rows.Count,
            });
        }

        rows.Add(new FlightMenuItem(
            FlightMenuActions.MenuOpacityId, 0, rows.Count,
            FlightMenuActions.MenuOpacityLabel, null, SectionEnd: false));
        rows.Add(new FlightMenuItem(
            FlightMenuActions.MenuSizeId, 0, rows.Count,
            FlightMenuActions.MenuSizeLabel, null, SectionEnd: false));
        rows.Add(new FlightMenuItem(
            FlightMenuActions.PortSettingsId, 0, rows.Count,
            FlightMenuActions.PortSettingsLabel, null, SectionEnd: false));
        rows.Add(new FlightMenuItem(
            FlightMenuActions.MissionStatsId, 0, rows.Count,
            FlightMenuActions.MissionStatsLabel, null, SectionEnd: false));

        List<FlightMenu> menus = new List<FlightMenu>(shipped.Menus.Count) { new(0, help.Title, rows) };
        for (int i = 1; i < shipped.Menus.Count; i++)
        {
            menus.Add(shipped.Menus[i]);
        }

        return FlightMenuTable.From(menus);
    }

    /// <summary>The three credit lines, out of the same document the menus come from.</summary>
    private static IReadOnlyList<string> ReadAboutLines(DataTree tree)
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(tree.Resolve(FlightMenuTable.DataPath)));
        List<string> lines = new List<string>(3);
        if (document.RootElement.TryGetProperty("aboutDialog", out JsonElement about))
        {
            foreach (JsonElement line in about.EnumerateArray())
            {
                lines.Add(line.GetProperty("text").GetString() ?? string.Empty);
            }
        }

        return lines;
    }
}
