using System.Globalization;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Aircraft;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Sim.Session;
using CYAC.Port.Render;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>What the front end asks the shell to do when a widget fires.</summary>
public enum FrontEndRequest
{
    /// <summary>Nothing — the key moved the focus or changed screen.</summary>
    None,

    /// <summary>Fly a historic mission (the one the command line named until F2 lands the pickers).</summary>
    FlyMission,

    /// <summary>Fly the Test Flight of the aeroplane the HANGAR's <c>Fly</c> chose.</summary>
    FlyTestFlight,

    /// <summary>
    /// Fly the CUSTOM MISSION the Create Mission pickers just built
    /// (<c>ui_create_mission_form</c>'s FLY exit, <c>image@0x2775E</c>).
    /// </summary>
    FlyCustomMission,

    /// <summary>Re-fly what <c>settings.json</c> remembers.</summary>
    FlyLastSortie,

    /// <summary>Leave the game.</summary>
    Exit,
}

/// <summary>
/// The SCREEN STACK: which front-end screen is up, and what a key does to it.
/// </summary>
/// <remarks>
/// <para>
/// The stack is what makes Esc mean "back up" (the manual, p. 20) without any screen knowing what
/// opened it.  F1 has two screens; F2–F7 push their own onto the same stack and need change nothing
/// here — a screen returns a <see cref="FrontEndCommand"/>, and the ONE place that turns a command
/// into a push, a pop or a <see cref="FrontEndRequest"/> is <see cref="Press"/>.
/// </para>
/// <para>
/// The front end never touches sim state and the flight never knows it exists (protocol §5): the
/// only thing it hands the shell is a request enum.
/// </para>
/// </remarks>
public sealed class FrontEnd
{
    private readonly DataTree _tree;
    private readonly FrontEndStrings _strings;
    private readonly IReadOnlyList<Rgb24> _palette;
    private readonly Func<LastSortie?> _lastSortie;
    private readonly List<FrontEndScreen> _stack = [];
    private readonly List<string> _log = [];

    /// <summary>Builds the front end and puts CHOOSE ACTIVITY up.</summary>
    /// <param name="tree">The data tree the screens draw from.</param>
    /// <param name="lastSortie">
    /// Where the <c>Last Mission</c> row's memory comes from — read afresh whenever the root screen
    /// is rebuilt, so the row lights up as soon as one sortie has been flown.
    /// </param>
    /// <param name="palette">
    /// The game's palette, widened to 8 bits: the HANGAR's 3-D view resolves its mesh's colour
    /// indices through it.  An empty list gives a hangar with no 3-D column, which is what a test
    /// that only asserts state passes.
    /// </param>
    public FrontEnd(DataTree tree, Func<LastSortie?> lastSortie, IReadOnlyList<Rgb24>? palette = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(lastSortie);
        _tree = tree;
        _strings = new FrontEndStrings(tree);
        _palette = palette ?? [];
        _lastSortie = lastSortie;
        LastHangarPage = DefaultHangarPage(tree);
        _stack.Add(new ChooseActivityScreen(tree, _strings, lastSortie()));
    }

    /// <summary>The screen the player is looking at.</summary>
    public FrontEndScreen Current => _stack[^1];

    /// <summary>How deep the Back stack is (1 = the root menu).</summary>
    public int Depth => _stack.Count;

    /// <summary>Every navigation and every refused button, for the headless summary.</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>
    /// The sortie MISSION DESCRIPTION's <c>Ok</c> chose, waiting for the shell to fly it.
    /// </summary>
    /// <remarks>
    /// The historic chain ends when <c>Ok</c> fires: the front end builds the <see cref="LastSortie"/>
    /// for the mission's slot at the chosen <c>Diff:</c> and stores it here, then returns
    /// <see cref="FrontEndRequest.FlyMission"/>.  The shell reads this instead of the command-line
    /// default, so the sortie flown is the mission the player picked.  Null until <c>Ok</c> fires.
    /// </remarks>
    public LastSortie? PendingSortie { get; private set; }

    /// <summary>
    /// The encyclopedia page the HANGAR opens on next time.
    /// </summary>
    /// <remarks>
    /// The original saves the chosen aircraft on the way out of the panel (<c>[0xC31A] →
    /// g_saved_aircraft_idx [0xC326]</c>, <c>image@0x26CD2</c>) and restores it on the resume fast
    /// path (<c>image@0x26500</c>).  A run with NO <c>yeager.cfg</c> at all opens on the P-51D as
    /// well.  The default is the aircraft of player
    /// slot 0 — <c>g_active_aircraft_idx [0xC31A]</c> starts at the P-51 — which is <c>pi.json</c>
    /// page 1, not page 0.
    /// </remarks>
    public int LastHangarPage { get; private set; }

    /// <summary>
    /// The page the hangar opens on before any test flight was flown: the aircraft of player slot 0,
    /// the P-51D (see <see cref="LastHangarPage"/>'s correction) — resolved from the encyclopedia,
    /// never a literal index.
    /// </summary>
    /// <param name="tree">The data tree.</param>
    public static int DefaultHangarPage(DataTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        IReadOnlyList<EncyclopediaPlane> planes = tree.Encyclopedia.Planes;
        for (int i = 0; i < planes.Count; i++)
        {
            if (planes[i].FlyableSlot == 0)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// The encyclopedia page the TACTICS screen's RIGHT column opens on for a mission.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The original reads <c>g_scene_misc_state_1C6 [0xF1C6]</c>, and when that is zero falls back to
    /// <c>g_cmp_initial_enemy_tab [0x3B5A]</c> indexed by the ALLY's flyable slot
    /// (<c>image@0x26D31..0x26D47</c>); either way the word is a 46-byte stat-block pointer, which
    /// <c>enemy_idx_to_pi_bin_idx @image@0x273C6</c> reverses into a <c>pi.bin</c> page.
    /// </para>
    /// <para>
    /// <c>[0xF1C6]</c> is the mission's own opponent — the original's own screen shows Abbeville, whose
    /// <c>opponentClassId</c> is 14, and the screen opens on the Me-109E, not on the P-51D's
    /// table default.  So the port resolves <see cref="MissionEntry.OpponentClassId"/> when the
    /// mission names one.
    /// </para>
    /// <para>
    /// The fallback table's six words are, in flyable-slot order,
    /// <c>{0x1812, 0x1734, 0x1F60, 0x1D1E, 0x2470, 0x2104}</c> (<c>image@0x3F8BA</c>) — FW-190A,
    /// P-51D, MiG-15, F-86E, MiG-21MF, F-4E.  That is exactly <b>the other flyable of the same
    /// conflict</b>, i.e. slot <c>xor 1</c>, which is what the port computes rather than shipping the
    /// six numbers.
    /// </para>
    /// </remarks>
    /// <param name="tree">The data tree.</param>
    /// <param name="entry">The mission.</param>
    public static int OpeningEnemyPage(DataTree tree, MissionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(entry);
        AircraftEncyclopedia encyclopedia = tree.Encyclopedia;
        if (entry.OpponentClassId != 0
            && encyclopedia.ByClassId(entry.OpponentClassId) is { } opponent)
        {
            return opponent.Index;
        }

        return encyclopedia.ByFlyableSlot(entry.PlayerAircraftIndex ^ 1)?.Index
            ?? DefaultHangarPage(tree);
    }

    /// <summary>
    /// The seed the TACTICS screen's advice stream starts from for a mission.
    /// </summary>
    /// <remarks>
    /// The original has none: it draws from the game's live LFSR, which has been stepping since
    /// boot, so its advice differs every time the screen is opened.  The port makes the paragraph a
    /// function of the mission, which is reproducible, pinnable by a test and — since the seed
    /// changes per mission — still varied across the catalogue.  The <c>+1</c> keeps slot 0 off the
    /// LFSR's absorbing state.
    /// </remarks>
    /// <param name="entry">The mission.</param>
    public static ushort AdviceSeed(MissionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return (ushort)(entry.Slot + 1);
    }

    /// <summary>The <c>Diff:</c> a mission opens at when nothing was flown before: Normal.</summary>
    public const int DefaultDifficulty = 1;

    /// <summary>The <c>at_site</c> a menu-chosen mission opens with: draw 0.</summary>
    public const int DefaultSite = 0;

    /// <summary>Draws the current screen.</summary>
    /// <param name="surface">The design surface.</param>
    /// <param name="fonts">The three front-end fonts (F2).</param>
    public void Render(in FrontEndPainter.Surface surface, FrontEndFonts fonts) =>
        Current.Render(surface, fonts);

    /// <summary>
    /// Rebuilds the root screen and unwinds to it — what a sortie's end lands on.
    /// </summary>
    /// <remarks>
    /// The root is REBUILT rather than kept, because <c>Last Mission</c>'s enablement is a property
    /// of the settings file and the sortie that has just ended wrote it.  The focus is carried
    /// across so the player's eye lands where it left.
    /// </remarks>
    public void ReturnToRoot()
    {
        int focus = _stack[0].Focus;
        Unwind();
        ChooseActivityScreen root = new ChooseActivityScreen(_tree, _strings, _lastSortie());
        root.FocusOn(focus);
        _stack.Add(root);
    }

    /// <summary>
    /// A historic sortie has ended: unwind to the root and put DEBRIEFING up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stack is CLEARED first and the rebuilt root left underneath, so <c>Done</c> is an ordinary
    /// pop and lands on a CHOOSE ACTIVITY whose <c>Last Mission</c> row has seen the sortie that has
    /// just been remembered — the same reason <see cref="ReturnToRoot"/> rebuilds it.
    /// </para>
    /// <para>
    /// This is the MENU-mode end of a HISTORIC sortie only (ratified: "standard game has debriefing
    /// in the menu only").  A Test Flight has no debrief in the original either and goes straight to
    /// the root; direct mode never comes here at all.
    /// </para>
    /// </remarks>
    /// <param name="outcome">What the sortie came to.</param>
    public void ShowDebriefing(SortieOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ReturnToRoot();
        _stack.Add(new DebriefingScreen(_tree, _strings, outcome));
        Note($"DEBRIEFING: {outcome.MissionKey} — "
            + (outcome.Accomplished ? "accomplished" : "not accomplished"));
    }

    /// <summary>Applies one key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>What the shell must do about it.</returns>
    public FrontEndRequest Press(FrontEndKey key) => Resolve(Current.Press(key), key);

    /// <summary>Applies one typed character (the manual's first-letter rule).</summary>
    /// <param name="letter">The character.</param>
    /// <returns>What the shell must do about it.</returns>
    public FrontEndRequest Type(char letter) => Resolve(Current.Type(letter), null);

    /// <summary>
    /// The pointer moved over a widget: bring the <c>► ◄</c> ring with it.
    /// </summary>
    /// <param name="handle">The widget under the pointer.</param>
    public void Hover(int handle)
    {
        if (handle != FrontEndScreen.NoWidget)
        {
            Current.Hover(handle);
        }
    }

    /// <summary>
    /// A left click was released over the widget it was pressed on.
    /// </summary>
    /// <remarks>
    /// The original fires on the RELEASE, not the press: <c>ui_per_frame_input_poll</c>'s
    /// button-edge dispatch reaches the "widget activated" arm only on the transition to
    /// button-UP, and only when the widget under the cursor is still the one the hover latched
    /// (<c>image@0x2E73D..0x2E769</c>: <c>[0xBD5A] == 0</c>, <c>[0xBD62] == local_4</c> ⇒
    /// <c>event_type = 2</c> and the widget's <c>id_u8</c> goes to the out-param).  A press that
    /// slides off its widget before the release fires nothing.
    /// </remarks>
    /// <param name="handle">The widget.</param>
    /// <returns>What the shell must do about it.</returns>
    public FrontEndRequest Click(int handle)
    {
        if (handle == FrontEndScreen.NoWidget)
        {
            return FrontEndRequest.None;
        }

        FrontEndScreen screen = Current;
        FrontEndCommand command = screen.Click(handle);
        if (command == FrontEndCommand.None && !screen.Accepts(handle))
        {
            // The same refusal a greyed button gives Space, in the same words.
            Note($"{screen.LabelOf(handle)} is not available - {screen.ReasonOf(handle)}");
        }

        return Resolve(command, null);
    }

    /// <summary>Where the pointer belongs for the current screen's selection.</summary>
    public (int X, int Y) PointerTarget => Current.PointerTarget;

    private FrontEndRequest Resolve(FrontEndCommand command, FrontEndKey? key)
    {
        if (command == FrontEndCommand.None)
        {
            // A Select or a letter that hit a GREYED button says why, once, in the log: it is the
            // one place a player-visible refusal is recorded, and the headless run prints it.
            if (key is FrontEndKey.Select && !Current.Focused.Enabled)
            {
                Note($"{Current.Focused.Label} is not available - {Current.Focused.DisabledReason}");
            }

            return FrontEndRequest.None;
        }

        switch (command)
        {
            case FrontEndCommand.Credits:
                _stack.Add(new CreditsScreen(_tree, _strings));
                Note("CREDITS");
                return FrontEndRequest.None;
            case FrontEndCommand.Ok:
                if (_stack.Count > 1)
                {
                    Pop();
                    Note($"back to {Current.Name}");
                }

                return FrontEndRequest.None;
            case FrontEndCommand.FlyHistoricMission:
                // Fly Historic Mission opens the CONFLICT → SELECTION → DESCRIPTION chain (F1
                // flew mission 0 directly; now the player chooses).
                _stack.Add(new ConflictSelectionScreen(_tree, _strings));
                Note("Fly Historic Mission");
                return FrontEndRequest.None;
            case FrontEndCommand.OpenMissionSelection:
                if (Current is ConflictSelectionScreen conflict)
                {
                    _stack.Add(new MissionSelectionScreen(_tree, _strings, conflict.SelectedEra));
                    Note($"CONFLICT: {conflict.SelectedEra}");
                }

                return FrontEndRequest.None;
            case FrontEndCommand.OpenMissionDescription:
                if (Current is MissionSelectionScreen { SelectedEntry: { } entry })
                {
                    int startDifficulty = _lastSortie() is { IsMission: true } previous
                        ? previous.Difficulty
                        : DefaultDifficulty;
                    _stack.Add(new MissionDescriptionScreen(_tree, _strings, entry, startDifficulty));
                    Note($"MISSION: {entry.Title}");
                }

                return FrontEndRequest.None;
            case FrontEndCommand.PreviousPage:
                (Current as MissionSelectionScreen)?.PreviousPage();
                return FrontEndRequest.None;
            case FrontEndCommand.NextPage:
                (Current as MissionSelectionScreen)?.NextPage();
                return FrontEndRequest.None;
            case FrontEndCommand.StartSortie:
                if (Current is MissionDescriptionScreen description)
                {
                    PendingSortie = new LastSortie(
                        LastSortie.MissionKind, description.Slot, description.Difficulty,
                        string.Empty, DefaultSite);
                    Note($"Ok: fly {description.Name} slot {description.Slot} diff {description.Difficulty}");
                    return FrontEndRequest.FlyMission;
                }

                return FrontEndRequest.None;
            case FrontEndCommand.OpenTactics:
                // The briefing screen's own widget id 1 (image@0x25B15's sole LCALL to
                // ui_plane_comparison_screen).  It opens on the MISSION's own pair and changes
                // nothing: "this does not select airplanes for the mission" (manual p.22).
                if (Current is MissionDescriptionScreen briefing)
                {
                    MissionEntry mission = briefing.Entry;
                    TacticsScreen tactics = new TacticsScreen(
                        _tree,
                        _strings,
                        mission.PlayerAircraftIndex,
                        OpeningEnemyPage(_tree, mission),
                        AdviceSeed(mission));
                    _stack.Add(tactics);
                    Note($"TACTICS: {tactics.Ally.NameShort} vs {tactics.Enemy.NameShort}");
                }

                return FrontEndRequest.None;
            case FrontEndCommand.PreviousAlly:
                (Current as TacticsScreen)?.StepAlly(-1);
                return FrontEndRequest.None;
            case FrontEndCommand.NextAlly:
                (Current as TacticsScreen)?.StepAlly(1);
                return FrontEndRequest.None;
            case FrontEndCommand.PreviousEnemy:
                (Current as TacticsScreen)?.StepEnemy(-1);
                return FrontEndRequest.None;
            case FrontEndCommand.NextEnemy:
                (Current as TacticsScreen)?.StepEnemy(1);
                return FrontEndRequest.None;
            case FrontEndCommand.ShowDebrief:
                // The two view buttons are a TOGGLE inside the one screen ([0xBC30]), never a push:
                // handled here, like F2's page turns, so a letter fires them too.
                (Current as DebriefingScreen)?.ShowVerdict();
                return FrontEndRequest.None;
            case FrontEndCommand.ShowStats:
                (Current as DebriefingScreen)?.ShowStatistics();
                return FrontEndRequest.None;
            case FrontEndCommand.DoneDebriefing:
                // Widget 2 (image@0x261D0): the modal loop's only exit.  The screen sits on a
                // rebuilt CHOOSE ACTIVITY, so leaving it is a pop like any other.
                if (_stack.Count > 1)
                {
                    Pop();
                    Note($"Done: back to {Current.Name}");
                }

                return FrontEndRequest.None;
            case FrontEndCommand.CreateMission:
                // The last greyed activity but Review Film: activity-dispatch entry[1]
                // (image@0x00C40 -> ui_create_mission_form @image@0x2769E).
                _stack.Add(new CreateMissionScreen(_tree, _strings));
                Note("Create Mission");
                return FrontEndRequest.None;
            case FrontEndCommand.CreateMissionBackUp:
                (Current as CreateMissionScreen)?.BackUp();
                return FrontEndRequest.None;
            case FrontEndCommand.FlyCustomMission:
                if (Current is CreateMissionScreen { CanFly: true } form)
                {
                    CustomMissionPicks picks = form.Picks;
                    PendingSortie = new LastSortie(
                        LastSortie.CustomKind,
                        LastSortie.CustomSlot,
                        CustomMissionBuild.ForcedDifficulty,
                        string.Empty,
                        DefaultSite,
                        Convert.ToHexString(picks.ToValueArray()));
                    Note($"Done: fly \"{form.Sentence}\"");
                    return FrontEndRequest.FlyCustomMission;
                }

                return FrontEndRequest.None;
            case FrontEndCommand.TestFlight:
                // Test Flight OPENS THE HANGAR (activity-dispatch entry 2,
                // ui_aircraft_stats_panel @image@0x264ED); F1 flew the command line's aircraft
                // directly.  The page it opens on is the one the last test flight left
                // (g_saved_aircraft_idx [0xC326] — see the hangar's own remarks).
                _stack.Add(new HangarScreen(_tree, _strings, _palette, LastHangarPage));
                Note("Test Flight");
                return FrontEndRequest.None;
            case FrontEndCommand.PreviousAircraft:
                (Current as HangarScreen)?.Step(-1);
                return FrontEndRequest.None;
            case FrontEndCommand.NextAircraft:
                (Current as HangarScreen)?.Step(1);
                return FrontEndRequest.None;
            case FrontEndCommand.ToggleHangarView:
                (Current as HangarScreen)?.ToggleView();
                return FrontEndRequest.None;
            case FrontEndCommand.FlyTestFlight:
                if (Current is HangarScreen hangar)
                {
                    LastHangarPage = hangar.Page;
                    PendingSortie = hangar.Sortie;
                    Note($"Fly: test flight {hangar.Plane.ClassName} [{hangar.Plane.FlyableBasename}]");
                    return FrontEndRequest.FlyTestFlight;
                }

                return FrontEndRequest.None;
            case FrontEndCommand.LastMission:
                Note("Last Mission");
                return FrontEndRequest.FlyLastSortie;
            case FrontEndCommand.ExitToDos:
                Note("Exit to DOS");
                return FrontEndRequest.Exit;
            default:
                return FrontEndRequest.None;
        }
    }

    // A screen may own unmanaged-ish state (F4's hangar owns a SceneRenderer with a tile pool), so
    // every path that removes one from the stack goes through these two.
    private void Pop()
    {
        (_stack[^1] as IDisposable)?.Dispose();
        _stack.RemoveAt(_stack.Count - 1);
    }

    private void Unwind()
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            (_stack[i] as IDisposable)?.Dispose();
        }

        _stack.Clear();
    }

    private void Note(string what) =>
        _log.Add(string.Create(CultureInfo.InvariantCulture, $"front end: {what}"));
}
