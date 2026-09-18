using System.Globalization;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Sim.Combat.Player;
using CYAC.Port.Core.Sim.Flight;
using CYAC.Port.Core.Sim.Session;
using CYAC.Port.Host.FrontEnd;
using CYAC.Port.Host.Menu;
using CYAC.Port.Host.Sim;
using CYAC.Port.Render;

namespace CYAC.Port.Host.Input;

/// <summary>
/// A scripted pilot for the headless run: "hold these controls between these two moments".
/// </summary>
/// <remarks>
/// <para>
/// Syntax: comma-separated entries <c>start-end:name[+name…]</c>, times in seconds of SIMULATED
/// wall clock from the start of the run.  Example, the H1 acceptance script:
/// <c>0-2:back,2-4:right</c> — two seconds of stick back, then two of stick right.
/// </para>
/// <para>
/// Names: <c>left right forward back centre</c> for the stick, and the cockpit keys <c>throttleup
/// throttledown throttle1..throttle5 afterburner brakes flaps gear</c>, plus the two camera actions
/// <c>viewcockpit</c> and <c>viewchase</c>, plus H9's <c>eject</c> (Shift-E) and H26's
/// <c>navnext</c> / <c>navprev</c> (W and Shift+W) and H27's <c>viewmap</c> (F9), <c>mapzoomin</c> /
/// <c>mapzoomout</c> (<c>+</c> / <c>−</c>), <c>mapcentre</c> and <c>mapfit</c>, plus M0's
/// <c>restart</c> (restart the mission at once — the key is unmapped; the ESC menu's Restart Mission
/// item is the one door) and <c>respawn</c> ("fly again", which only does anything once the sortie
/// is over), plus W1's <c>radar</c> (<b>R</b>, the manual's radar switch, p.51), plus M1's ESC-menu
/// words <c>menu</c> (open or close), <c>menuup menudown menuleft menuright menuhome menuend
/// menupgup menupgdn menuenter menuesc</c> and <c>menutype&lt;c&gt;</c> — one trailing character,
/// e.g. <c>menutypec</c>, because the entry is already split on <c>:</c> and <c>+</c>, plus M2's
/// PORT SETTINGS words <c>settings</c> (open the dialog from anywhere), <c>settingsup settingsdown
/// settingsleft settingsright settingshome settingsend settingspgup settingspgdn settingsenter
/// settingsreset settingsesc</c>, plus F6's MOUSE words <c>mouseto:&lt;x&gt;x&lt;y&gt;</c> (alias
/// <c>mouse:</c>) and <c>click:&lt;x&gt;x&lt;y&gt;</c> — design-space columns and rows joined with
/// an <c>x</c>, because the script is comma-separated at the top level.  <c>click:</c> HOLDS the
/// left button for its interval, so the widget is pressed by the RELEASE at the interval's end,
/// which is the original's own press rule; give it at least two frames. A cockpit key fires ONCE, on
/// the frame its interval opens — it is an edge, not a hold, exactly as <c>cockpit_key_dispatch
/// @image@0x2A3FF</c> sees it.
/// </para>
/// </remarks>
public sealed class KeyScript : IFlightInputSource
{
    private readonly List<Entry> _entries;
    private readonly List<int> _pressed = [];
    private double _now;

    private KeyScript(List<Entry> entries) => _entries = entries;

    /// <summary>How many intervals the script holds.</summary>
    public int Count => _entries.Count;

    /// <summary>The last moment any interval covers, in seconds.</summary>
    public double EndSeconds => _entries.Count == 0 ? 0 : _entries.Max(e => e.End);

    /// <summary>Parses a script.</summary>
    /// <param name="text">The script text; null or empty gives an inert script.</param>
    /// <exception cref="FormatException">An entry is malformed or names an unknown control.</exception>
    public static KeyScript Parse(string? text)
    {
        List<Entry> entries = new List<Entry>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new KeyScript(entries);
        }

        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // At most TWO halves: the names half may itself carry a colon (`key:X`), and before
            // F1 a second colon simply threw.
            string[] halves = part.Split(':', 2);
            if (halves.Length != 2)
            {
                throw new FormatException($"script entry '{part}' is not '<start>-<end>:<names>'");
            }

            string[] range = halves[0].Split('-');
            if (range.Length != 2
                || !double.TryParse(range[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double start)
                || !double.TryParse(range[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double end))
            {
                throw new FormatException($"script entry '{part}' has no '<start>-<end>' seconds range");
            }

            HeldControls held = default(HeldControls);
            List<int> keys = new List<int>();
            List<int> combat = new List<int>();
            bool eject = false;
            bool fire = false;
            int weaponStep = 0;
            int navStep = 0;
            int mapZoomStep = 0;
            int windowZoomStep = 0;                          // The MAP WINDOW's `.` / `,`
            bool mapCentre = false;
            bool mapFit = false;
            bool restartNow = false;
            bool respawn = false;
            bool radar = false;
            bool nearestBogey = false;
            bool nearestFriendly = false;
            int advisorCode = -1;
            List<DebugKey> debugKeys = new List<DebugKey>();            // The scrutiny words
            List<FlightMenuKey> menuKeys = new List<FlightMenuKey>();
            List<CockpitOverlayFlags> overlays = new List<CockpitOverlayFlags>();
            List<FrontEndKey> frontEndKeys = new List<FrontEndKey>();
            char typed = '\0';
            ViewMode? view = null;
            (int X, int Y)? warp = null;
            bool mouseLeft = false;
            foreach (string name in halves[1].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (name.ToLowerInvariant())
                {
                    // The arrow keys also carry their FRONT-END meaning, exactly as `up`/`down` below
                    // do and as ControlInputSource.FrontEndBindings binds the physical keys at the
                    // window (StickLeft/StickRight -> FrontEndKey.Left/Right). The HANGAR is the
                    // first screen that needs them: they turn its 3-D model.
                    case "left":
                        held = held with { Left = true };
                        frontEndKeys.Add(FrontEndKey.Left);
                        break;
                    case "right":
                        held = held with { Right = true };
                        frontEndKeys.Add(FrontEndKey.Right);
                        break;
                    case "forward": held = held with { Forward = true }; break;
                    case "back": held = held with { Back = true }; break;
                    case "centre" or "center": held = held with { Centre = true }; break;
                    case "throttleup": keys.Add(CockpitKeys.ThrottleUpKey); break;
                    case "throttledown": keys.Add(CockpitKeys.ThrottleDownKey); break;
                    case "throttle1": keys.Add(CockpitKeys.FirstPresetKey + 0); break;
                    case "throttle2": keys.Add(CockpitKeys.FirstPresetKey + 1); break;
                    case "throttle3": keys.Add(CockpitKeys.FirstPresetKey + 2); break;
                    case "throttle4": keys.Add(CockpitKeys.FirstPresetKey + 3); break;
                    case "throttle5": keys.Add(CockpitKeys.FirstPresetKey + 4); break;
                    case "afterburner": keys.Add(CockpitKeys.AfterburnerKey); break;
                    case "brakes": keys.Add(CockpitKeys.BrakeKey); break;
                    case "flaps": keys.Add(CockpitKeys.FlapKey); break;
                    case "gear": keys.Add(CockpitKeys.GearKey); break;
                    case "fire": fire = true; break;
                    case "chaff": combat.Add(CombatKeyLadder.ChaffKey); break;
                    case "flare": combat.Add(CombatKeyLadder.FlareKey); break;
                    case "target": combat.Add(MissionSession.TargetCycleKey); break;
                    // The manual's `'` (p.48): target the object nearest the crosshair.
                    // `targetcam` stays as the older alias for the same cooked key.
                    case "targetnearest" or "targetcam":
                        combat.Add(CombatKeyLadder.LockOnListModeKey);
                        break;
                    // The four OVERLAY WINDOW toggles (Shift-1..Shift-4, manual p.14).
                    case "windowenvelope": overlays.Add(CockpitOverlayFlags.Envelope); break;
                    case "windowtarget": overlays.Add(CockpitOverlayFlags.Target); break;
                    case "windowmap": overlays.Add(CockpitOverlayFlags.Map); break;
                    case "windowyeager": overlays.Add(CockpitOverlayFlags.Yeager); break;
                    case "weaponnext": weaponStep = 1; break;
                    case "weaponprev": weaponStep = -1; break;
                    case "viewcockpit": view = ViewMode.CockpitForward; break;
                    case "viewchase": view = ViewMode.ExternalChase; break;
                    case "eject": eject = true; break;
                    case "navnext": navStep = 1; break;      // W
                    case "navprev": navStep = -1; break;     // Shift+W
                    case "viewmap": view = ViewMode.Map; break;          // F9
                    // The map's ZOOM keys ARE the port's throttle keys (the original's `+` /
                    // `−` are its zoom keys and nothing else), so a script presses BOTH
                    // meanings, exactly as the keyboard does, and the rasterizer decides.
                    case "mapzoomin":
                        mapZoomStep = 1;
                        keys.Add(CockpitKeys.ThrottleUpKey);
                        break;
                    case "mapzoomout":
                        mapZoomStep = -1;
                        keys.Add(CockpitKeys.ThrottleDownKey);
                        break;
                    case "mapcentre" or "mapcenter": mapCentre = true; break;   // M
                    case "mapfit": mapFit = true; break;                 // N
                    case "restart": restartNow = true; break;            // M0 (the ESC menu's item)
                    case "respawn": respawn = true; break;               // M0 (--respawn by hand)
                    // The MAP WINDOW's own zoom keys, `.` and `,` (image@0x012B2 / image@0x012C7).
                    // Distinct from `mapzoomin` / `mapzoomout` above, which are H27's PORT-ADDED
                    // zoom for the F9 FULL-SCREEN map — different subsystems.
                    case "windowzoomin": windowZoomStep = -1; break;
                    case "windowzoomout": windowZoomStep = 1; break;
                    case "radar": radar = true; break;                   // R, the radar switch
                    case "bogey": nearestBogey = true; break;            // Ctrl-Z
                    case "friendly": nearestFriendly = true; break;      // Ctrl-A
                    // The RENDER SCRUTINY keys (F12 / P, K, L, I, Shift+I): a headless capture
                    // can photograph, dump and switch the debug looks exactly as a player does at
                    // the window.
                    case "screenshot" or "shot": debugKeys.Add(DebugKey.Screenshot); break;
                    case "cull": debugKeys.Add(DebugKey.CullToggle); break;
                    case "wireframe" or "wire": debugKeys.Add(DebugKey.WireframeCycle); break;
                    case "facecolors" or "contrast": debugKeys.Add(DebugKey.FaceColorsToggle); break;
                    case "reshuffle": debugKeys.Add(DebugKey.FaceColorsReshuffle); break;
                    case "maskview" or "silhouette": debugKeys.Add(DebugKey.MaskViewCycle); break;
                    // The in-flight ESC menu.
                    case "menu": menuKeys.Add(FlightMenuKey.Open); break;
                    case "menuesc": menuKeys.Add(FlightMenuKey.Close); break;
                    case "menuup": menuKeys.Add(FlightMenuKey.Up); break;
                    case "menudown": menuKeys.Add(FlightMenuKey.Down); break;
                    case "menuleft": menuKeys.Add(FlightMenuKey.Left); break;
                    case "menuright": menuKeys.Add(FlightMenuKey.Right); break;
                    case "menuhome": menuKeys.Add(FlightMenuKey.Home); break;
                    case "menuend": menuKeys.Add(FlightMenuKey.End); break;
                    case "menupgup": menuKeys.Add(FlightMenuKey.PageUp); break;
                    case "menupgdn": menuKeys.Add(FlightMenuKey.PageDown); break;
                    case "menuenter": menuKeys.Add(FlightMenuKey.Enter); break;
                    // The PORT SETTINGS dialog.  `settings` is a key of its own (open the bar if
                    // it is down and go straight into the dialog); the rest are readable ALIASES
                    // of the menu keys the dialog shares with the bar, so a settings script says
                    // what it is doing.
                    case "settings": menuKeys.Add(FlightMenuKey.Settings); break;
                    case "settingsesc": menuKeys.Add(FlightMenuKey.Close); break;
                    case "settingsup": menuKeys.Add(FlightMenuKey.Up); break;
                    case "settingsdown": menuKeys.Add(FlightMenuKey.Down); break;
                    case "settingsleft": menuKeys.Add(FlightMenuKey.Left); break;
                    case "settingsright": menuKeys.Add(FlightMenuKey.Right); break;
                    case "settingshome": menuKeys.Add(FlightMenuKey.Home); break;
                    case "settingsend": menuKeys.Add(FlightMenuKey.End); break;
                    case "settingspgup": menuKeys.Add(FlightMenuKey.PageUp); break;
                    case "settingspgdn": menuKeys.Add(FlightMenuKey.PageDown); break;
                    case "settingsenter": menuKeys.Add(FlightMenuKey.Enter); break;
                    case "settingsreset": menuKeys.Add(FlightMenuKey.Reset); break;
                    // The read-only MISSION STATS panel.  `stats` is a key of its own; the rest
                    // are aliases of the menu keys the panel shares with the bar.
                    case "stats": menuKeys.Add(FlightMenuKey.Stats); break;
                    case "statsesc": menuKeys.Add(FlightMenuKey.Close); break;
                    case "statsup": menuKeys.Add(FlightMenuKey.Up); break;
                    case "statsdown": menuKeys.Add(FlightMenuKey.Down); break;
                    case "statshome": menuKeys.Add(FlightMenuKey.Home); break;
                    case "statsend": menuKeys.Add(FlightMenuKey.End); break;
                    case "statspgup": menuKeys.Add(FlightMenuKey.PageUp); break;
                    case "statspgdn": menuKeys.Add(FlightMenuKey.PageDown); break;
                    // The FRONT END's keys (the manual's model, p. 20).  Each word presses the
                    // PHYSICAL key, so it carries every meaning that key carries at the window: Tab
                    // and Esc also open the ESC menu bar, Enter is also its Enter and the arrows are
                    // also its arrows (ControlInputSource emits both lists from the same edge).
                    // Only SPACE is the front end's alone — in flight it is the trigger, which is
                    // held rather than edged.
                    case "tab":
                        frontEndKeys.Add(FrontEndKey.Next);
                        menuKeys.Add(FlightMenuKey.Open);
                        break;
                    case "shifttab": frontEndKeys.Add(FrontEndKey.Previous); break;
                    case "up":
                        frontEndKeys.Add(FrontEndKey.Up);
                        menuKeys.Add(FlightMenuKey.Up);
                        break;
                    case "down":
                        frontEndKeys.Add(FrontEndKey.Down);
                        menuKeys.Add(FlightMenuKey.Down);
                        break;
                    case "space": frontEndKeys.Add(FrontEndKey.Select); break;
                    case "enter":
                        frontEndKeys.Add(FrontEndKey.Select);
                        menuKeys.Add(FlightMenuKey.Enter);
                        break;
                    case "esc":
                        frontEndKeys.Add(FrontEndKey.Back);
                        menuKeys.Add(FlightMenuKey.Open);
                        break;

                    // The CREATE MISSION pickers' grid keys.  `home`/`end` carry the stats panel's
                    // meaning too (the same physical key), and `backspace` is the form's Back Up.
                    case "home":
                        frontEndKeys.Add(FrontEndKey.Home);
                        menuKeys.Add(FlightMenuKey.Home);
                        break;
                    case "end":
                        frontEndKeys.Add(FrontEndKey.End);
                        menuKeys.Add(FlightMenuKey.End);
                        break;
                    case "backspace":
                        frontEndKeys.Add(FrontEndKey.BackUp);
                        break;
                    default:
                        // The MOUSE: `mouseto:<x>x<y>` puts the pointer at a DESIGN point and
                        // `click:<x>x<y>` puts it there and HOLDS the left button for the interval,
                        // so the release at the interval's end is what activates the widget — the
                        // original's own press rule (FrontEnd.Click).  The two numbers are joined
                        // with `x` rather than a comma because the script itself is
                        // comma-separated at the top level.
                        if (ParseMousePoint(name, "mouseto:", out (int X, int Y) warpTo)
                            || ParseMousePoint(name, "mouse:", out warpTo))
                        {
                            warp = warpTo;
                            break;
                        }

                        if (ParseMousePoint(name, "click:", out (int X, int Y) clickAt))
                        {
                            warp = clickAt;
                            mouseLeft = true;
                            break;
                        }

                        // `menutypeX` — one character, no separator: the entry itself is already
                        // split on ':' and '+', so neither could be used here.
                        if (name.StartsWith("menutype", StringComparison.OrdinalIgnoreCase)
                            && name.Length == "menutype".Length + 1)
                        {
                            typed = name[^1];
                            break;
                        }

                        // `key:X`: the front end's first-letter rule.  It is spelt with a colon
                        // because the entry is split on ':' ONCE per half and this word is inside
                        // the second half, where a second colon is free.
                        if (name.StartsWith("key:", StringComparison.OrdinalIgnoreCase)
                            && name.Length == "key:".Length + 1)
                        {
                            typed = char.ToUpperInvariant(name[^1]);
                            break;
                        }

                        // `advisor<n>` raises advisory action code n (0..20), the port's capture
                        // instrument for the YEAGER window.  Presentation-only: it feeds the
                        // advisor QUEUE, which never writes simulation state.
                        if (name.StartsWith("advisor", StringComparison.Ordinal)
                            && int.TryParse(
                                name.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture,
                                out int code))
                        {
                            advisorCode = code;
                            break;
                        }

                        throw new FormatException($"script entry '{part}' names unknown control '{name}'");
                }
            }

            entries.Add(new Entry(
                start, end, held, keys, view, fire, combat, weaponStep, eject, navStep,
                mapZoomStep, mapCentre, mapFit, restartNow, respawn, menuKeys, typed, frontEndKeys,
                warp, mouseLeft, overlays, radar, nearestBogey, nearestFriendly,
                windowZoomStep, advisorCode, debugKeys));
        }

        return new KeyScript(entries);
    }

    /// <summary>Parses a <c>&lt;word&gt;&lt;x&gt;x&lt;y&gt;</c> mouse point.</summary>
    /// <param name="name">The script word.</param>
    /// <param name="prefix">The word's prefix, including its colon.</param>
    /// <param name="point">The design point it names.</param>
    private static bool ParseMousePoint(string name, string prefix, out (int X, int Y) point)
    {
        point = default;
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] halves = name[prefix.Length..].Split('x', StringSplitOptions.TrimEntries);
        if (halves.Length != 2
            || !int.TryParse(halves[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(halves[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
        {
            throw new FormatException(
                $"script word '{name}' is not '{prefix}<x>x<y>' (design-space columns and rows)");
        }

        point = (x, y);
        return true;
    }

    /// <inheritdoc/>
    public FlightInputFrame Sample(double elapsedSeconds)
    {
        double previous = _now;
        _now += elapsedSeconds;

        _pressed.Clear();
        _combat.Clear();
        bool fire = false;
        bool eject = false;
        int weaponStep = 0;
        int navStep = 0;
        int mapZoomStep = 0;
        int windowZoomStep = 0;
        bool mapCentre = false;
        bool mapFit = false;
        bool restartNow = false;
        bool respawn = false;
        bool radar = false;
        bool nearestBogey = false;
        bool nearestFriendly = false;
        int advisorCode = -1;
        bool mouseLeft = false;
        (int X, int Y)? warp = null;
        char typed = '\0';
        _menuKeys.Clear();
        _frontEndKeys.Clear();
        _overlays.Clear();
        _debugKeys.Clear();
        HeldControls held = default(HeldControls);
        ViewMode? requested = null;
        foreach (Entry entry in _entries)
        {
            if (_now >= entry.Start && _now < entry.End)
            {
                held = new HeldControls(
                    held.Left || entry.Held.Left,
                    held.Right || entry.Held.Right,
                    held.Forward || entry.Held.Forward,
                    held.Back || entry.Held.Back,
                    held.Centre || entry.Held.Centre);
                fire |= entry.Fire;

                // The left button is HELD for the interval, so the RELEASE (the frame the
                // interval closes) is what presses the widget, exactly as at a real window.
                mouseLeft |= entry.MouseLeft;
            }

            // The edge: the first sample at or after the interval's start.
            if (previous < entry.Start && _now >= entry.Start)
            {
                _pressed.AddRange(entry.Keys);
                _combat.AddRange(entry.Combat);
                if (entry.WeaponStep != 0)
                {
                    weaponStep = entry.WeaponStep;
                }

                requested ??= entry.View;
                eject |= entry.Eject;
                if (entry.NavStep != 0)
                {
                    navStep = entry.NavStep;
                }

                if (entry.MapZoomStep != 0)
                {
                    mapZoomStep = entry.MapZoomStep;
                }

                if (entry.WindowZoomStep != 0)
                {
                    windowZoomStep = entry.WindowZoomStep;
                }

                mapCentre |= entry.MapCentre;
                mapFit |= entry.MapFit;
                restartNow |= entry.RestartNow;
                respawn |= entry.Respawn;
                radar |= entry.Radar;
                nearestBogey |= entry.NearestBogey;             // The `bogey` script word
                nearestFriendly |= entry.NearestFriendly;       // The `friendly` script word
                if (entry.AdvisorCode >= 0)
                {
                    advisorCode = entry.AdvisorCode;            // The `advisor<n>` word
                }

                _menuKeys.AddRange(entry.MenuKeys);
                _frontEndKeys.AddRange(entry.FrontEndKeys);
                _overlays.AddRange(entry.Overlays);
                _debugKeys.AddRange(entry.DebugKeys);
                if (entry.Typed != '\0')
                {
                    typed = entry.Typed;
                }

                warp ??= entry.Warp;
            }
        }

        return new FlightInputFrame(
            held, _pressed, requested, ToggleView: false, fire, _combat, weaponStep,
            Respawn: respawn, Eject: eject, NavStep: navStep, MapZoomStep: mapZoomStep,
            MapCentre: mapCentre, MapFit: mapFit, RestartNow: restartNow,
            MenuKeys: _menuKeys, MenuTypeAhead: typed, FrontEndKeys: _frontEndKeys,
            MouseLeft: mouseLeft, MouseWarp: warp, OverlayToggles: _overlays,
            RadarToggle: radar, NearestBogey: nearestBogey, NearestFriendly: nearestFriendly,
            WindowZoomStep: windowZoomStep, AdvisorCode: advisorCode, DebugKeys: _debugKeys);
    }

    private readonly List<int> _combat = [];
    private readonly List<DebugKey> _debugKeys = [];
    private readonly List<FlightMenuKey> _menuKeys = [];
    private readonly List<FrontEndKey> _frontEndKeys = [];
    private readonly List<CockpitOverlayFlags> _overlays = [];

    private sealed record Entry(
        double Start,
        double End,
        HeldControls Held,
        IReadOnlyList<int> Keys,
        ViewMode? View,
        bool Fire,
        IReadOnlyList<int> Combat,
        int WeaponStep,
        bool Eject,
        int NavStep,
        int MapZoomStep,
        bool MapCentre,
        bool MapFit,
        bool RestartNow,
        bool Respawn,
        IReadOnlyList<FlightMenuKey> MenuKeys,
        char Typed,
        IReadOnlyList<FrontEndKey> FrontEndKeys,
        (int X, int Y)? Warp,
        bool MouseLeft,
        IReadOnlyList<CockpitOverlayFlags> Overlays,
        bool Radar,
        bool NearestBogey,
        bool NearestFriendly,
        int WindowZoomStep,
        int AdvisorCode,
        IReadOnlyList<DebugKey> DebugKeys);
}
