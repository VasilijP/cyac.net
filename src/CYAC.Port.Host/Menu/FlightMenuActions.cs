using CYAC.Port.Core.Model.Mission;
namespace CYAC.Port.Host.Menu;

/// <summary>What one menu row DOES in the port.</summary>
/// <remarks>
/// The id is the original's own <c>(menu &lt;&lt; 8) | item</c>, and the two port additions take ids
/// outside the shipped range so nothing collides.
/// </remarks>
public enum FlightMenuAction
{
    /// <summary>The port has no such thing yet: dimmed, and Enter does nothing.</summary>
    Disabled,

    /// <summary>A caption row with no dispatch arm of its own (View's item 6).</summary>
    Caption,

    /// <summary>Restart the mission at once (the M0 path).</summary>
    RestartMission,

    /// <summary>End the sortie and show the debrief, the way Ctrl-Q does.</summary>
    EndMission,

    /// <summary>Quit the host.</summary>
    Exit,

    /// <summary>The three-line credits box.</summary>
    About,

    /// <summary>The port-settings dialog.</summary>
    PortSettings,

    /// <summary>The read-only Mission Stats panel.</summary>
    MissionStats,

    /// <summary>Cycle the menu's own panel opacity.</summary>
    MenuOpacity,

    /// <summary>Cycle the menu's own integer pixel scale, 1 → 2 → 3 → 4 → auto.</summary>
    MenuSize,

    /// <summary>Toggle a bit of the audio mute mask <c>g_audio_mute_mask [0xE483]</c>.</summary>
    SoundMaskBit,

    /// <summary>Select one of the port's two LOD policies (Graphics.Medium/High Detail).</summary>
    DetailLevel,

    /// <summary>Toggle the dithered horizon (Graphics.Dithered Horizon).</summary>
    DitheredHorizon,

    /// <summary>Toggle the cloud deck (Graphics.Clouds).</summary>
    Clouds,

    /// <summary>Toggle the bitmap explosions (Graphics.Bitmap Explosions).</summary>
    BitmapExplosions,

    /// <summary>Toggle the HUD text overlay (Graphics.Flight Info, the H key).</summary>
    FlightInfo,

    /// <summary>Select a camera (the fifteen View rows).</summary>
    View,

    /// <summary>
    /// Help &gt; Map / Envelope / Target / Yeager Window: toggle the overlay window whose
    /// <c>[0xF1CB]</c> bit is the parameter (the original pushes the window's Shift key,
    /// <c>image@0x21790..0x217A2</c>; the port flips the same live byte).
    /// </summary>
    OverlayWindow,
}

/// <summary>One row's effect, with the citation that justifies it.</summary>
/// <param name="Action">What the row does.</param>
/// <param name="Parameter">
/// The action's argument: a mute-mask bit, a detail level, a <see cref="Render.ViewMode"/>, …
/// </param>
/// <param name="Why">
/// Where the mapping comes from — an <c>image@</c> citation for a wired row, or the reason a row is
/// disabled.  The headless run prints it, so a run's own output says what is and is not wired.
/// </param>
public readonly record struct FlightMenuEffect(FlightMenuAction Action, int Parameter, string Why);

/// <summary>
/// The item → EFFECT table: the KNOWLEDGE half of the menu, keyed by the id the data document
/// publishes.
/// </summary>
/// <remarks>
/// <para>
/// The labels come from the executable through <c>cyac-transform</c>; Rows the port cannot honour
/// yet are <see cref="FlightMenuAction.Disabled"/> with the reason written down, so nothing is
/// faked and the gap is visible in the menu itself.
/// </para>
/// <para>
/// <b>Deliberately not implemented</b>: time compression (System 1x/2x/4x), the Location teleports,
/// the four input modes and the four overlay windows.
/// </para>
/// </remarks>
public static class FlightMenuActions
{
    /// <summary>The id the port gives its own "Restart Mission" row.</summary>
    /// <remarks>Outside the shipped 0x000..0x503 range on purpose.</remarks>
    public const int RestartMissionId = 0x0F0;

    /// <summary>The id the port gives its own "Port Settings…" row.</summary>
    public const int PortSettingsId = 0x0F1;

    /// <summary>The id the port gives its own "Menu Opacity" row.</summary>
    public const int MenuOpacityId = 0x0F2;

    /// <summary>The id the port gives its own "Menu Size" row.</summary>
    public const int MenuSizeId = 0x0F3;

    /// <summary>The id the port gives its own "Mission Stats…" row.</summary>
    public const int MissionStatsId = 0x0F4;

    /// <summary>The label the port's own restart row shows.</summary>
    public const string RestartMissionLabel = "Restart Mission";

    /// <summary>
    /// Its accelerator text.  BLANKED — neither restart accelerator is mapped ("R or shift R
    /// as a restart could be unmapped completely, there is an in-game menu now where the restart is a
    /// new option"), so the row must not advertise a key that no longer exists.  It is left EMPTY
    /// rather than moved to another key: every free letter in flight is either the original's or one
    /// keystroke from being it, and this row is a port addition the menu already makes reachable.
    /// </summary>
    public const string RestartMissionAccelerator = "";

    /// <summary>The label the port's own settings row shows.</summary>
    public const string PortSettingsLabel = "Port Settings...";

    /// <summary>The label the port's own opacity row shows.</summary>
    public const string MenuOpacityLabel = "Menu Opacity";

    /// <summary>The label the port's own scale row shows.</summary>
    public const string MenuSizeLabel = "Menu Size";

    /// <summary>The label the port's own statistics row shows.</summary>
    public const string MissionStatsLabel = "Mission Stats...";

    private const string NoCheats =
        "(open) the port exposes no cheat flag: [0xE46A..0xE46D], [0xB1] and [0xB2] are not in the "
            + "verified kernel's surface, and decided cheats default OFF";

    private static readonly Dictionary<int, FlightMenuEffect> Table = new()
    {
        // ----? menu ----
        [0x000] = new(FlightMenuAction.Disabled, 0,
            "(open) there is no film system in the port; image@0x21661 pushes 'p' to reach "
                + "ui_film_review_screen @image@0x32868"),
        [0x001] = new(FlightMenuAction.Disabled, 0,
            "(open) auto-save film has nothing to save; image@0x2166C flips [0xB4]"),
        [RestartMissionId] = new(FlightMenuAction.RestartMission, 0,
            "PORT ADDITION — FlightRasterizer.RestartMission, the port's one restart path"),
        [0x002] = new(FlightMenuAction.EndMission, 0,
            "image@0x21676 pushes Ctrl-Q, which the original routes to flight_session_end "
                + "@image@0x30328 → the debrief; the port ends the sortie through the mission "
                + "outcome so the same debrief overlay appears"),
        [0x003] = new(FlightMenuAction.Exit, 0,
            "image@0x2167B pushes Ctrl-C — \"Exit to DOS\"; here it closes the host"),
        [0x004] = new(FlightMenuAction.About, 0,
            "image@0x21680 calls ui_about_yeager_dialog @image@0x219C4, whose three lines are at "
                + "DGROUP [0x10F0]/[0x110C]/[0x112C]"),
        [MenuOpacityId] = new(FlightMenuAction.MenuOpacity, 0,
            "PORT ADDITION — cycles --menu-opacity so the frozen scene can be seen behind "
                + "the panels while a render option is tuned"),
        [MenuSizeId] = new(FlightMenuAction.MenuSize, 0,
            "PORT ADDITION — cycles --menu-scale 1 → 2 → 3 → 4 → auto so the menu's pixel "
                + "size is picked BY EYE on the frozen scene; the readout prints what is in force"),
        [PortSettingsId] = new(FlightMenuAction.PortSettings, 0,
            "PORT ADDITION — the settings dialog over settings.json"),
        [MissionStatsId] = new(FlightMenuAction.MissionStats, 0,
            "PORT ADDITION — the READ-ONLY per-mission statistics stats.json keeps; the "
                + "original has no such screen (its own post-mission page, mission_stats_screen "
                + "@image@0x25E00, scores ONE sortie and keeps nothing)"),

        // ---- System menu ----
        [0x100] = new(FlightMenuAction.SoundMaskBit, 0x01,
            "image@0x21686 pushes Ctrl-S; bit 0 of g_audio_mute_mask [0xE483] gates "
                + "audio_event_dispatcher itself (image@0x29A43)"),
        [0x101] = new(FlightMenuAction.SoundMaskBit, 0x02,
            "image@0x2168B — xor [0xE483], 2 (engine, image@0x29D7C)"),
        [0x102] = new(FlightMenuAction.SoundMaskBit, 0x04,
            "image@0x21698 — xor [0xE483], 4 (radar warning, image@0x2A03F)"),
        [0x103] = new(FlightMenuAction.SoundMaskBit, 0x08,
            "image@0x2169F — xor [0xE483], 8 (stall, image@0x29FD1)"),
        [0x104] = new(FlightMenuAction.SoundMaskBit, 0x10,
            "image@0x216A6 — xor [0xE483], 0x10 (lock, image@0x2A081)"),
        [0x105] = new(FlightMenuAction.Disabled, 0,
            "(open) the port is keyboard-only; input_mode_set(2) = Keyboard is the mode it is "
                + "always in"),
        [0x106] = new(FlightMenuAction.Disabled, 0,
            "(open) no joystick support yet; image@0x216B9 calls input_mode_set(1)"),
        [0x107] = new(FlightMenuAction.Disabled, 0,
            "(open) the host reads no mouse; image@0x216BD calls input_mode_set(3)"),
        [0x108] = new(FlightMenuAction.Disabled, 0,
            "(open) the host reads no mouse; image@0x216C1 calls input_mode_set(4)"),
        [0x109] = new(FlightMenuAction.Disabled, 0,
            "(open) time compression is a SIM decision under the fixed-step determinism contract "
                + "; image@0x216C5 calls set_tick_shift(0)"),
        [0x10A] = new(FlightMenuAction.Disabled, 0, "(open) see 1x Time; set_tick_shift(1)"),
        [0x10B] = new(FlightMenuAction.Disabled, 0, "(open) see 1x Time; set_tick_shift(2)"),

        // ---- View menu: every row is a camera the port already has ----
        [0x200] = new(FlightMenuAction.View, (int)Render.ViewMode.CockpitForward, "image@0x216DB pushes F1"),
        [0x201] = new(FlightMenuAction.View, (int)Render.ViewMode.CockpitBack, "image@0x216E0 pushes F2"),
        [0x202] = new(FlightMenuAction.View, (int)Render.ViewMode.CockpitLeft, "image@0x216E6 pushes F3"),
        [0x203] = new(FlightMenuAction.View, (int)Render.ViewMode.CockpitRight, "image@0x216EC pushes F4"),
        [0x204] = new(FlightMenuAction.View, (int)Render.ViewMode.CockpitUp, "image@0x216F2 pushes F5"),
        [0x205] = new(FlightMenuAction.View, (int)Render.ViewMode.CockpitDown, "image@0x216F8 pushes F6"),
        [0x206] = new(FlightMenuAction.Caption, 0,
            "\"(Press Shift For External)\" has NO arm in the 53-entry ladder — it is a caption"),
        [0x207] = new(FlightMenuAction.View, (int)Render.ViewMode.PlaneToTarget, "image@0x216FE pushes F7"),
        [0x208] = new(FlightMenuAction.View, (int)Render.ViewMode.TargetToPlane, "image@0x21704 pushes F8"),
        [0x209] = new(FlightMenuAction.View, (int)Render.ViewMode.Map, "image@0x2170A pushes F9 (H27's map)"),
        [0x20A] = new(FlightMenuAction.View, (int)Render.ViewMode.FlyBy, "image@0x21710 pushes F10"),
        [0x20B] = new(FlightMenuAction.View, (int)Render.ViewMode.TargetCockpit, "image@0x21716 pushes Shift-F7"),
        [0x20C] = new(FlightMenuAction.View, (int)Render.ViewMode.ExternalTarget, "image@0x2171C pushes Shift-F8"),
        [0x20D] = new(FlightMenuAction.View, (int)Render.ViewMode.Circling, "image@0x21722 pushes Shift-F9"),
        [0x20E] = new(FlightMenuAction.View, (int)Render.ViewMode.Missile, "image@0x21728 pushes Shift-F10"),

        // ---- Graphics menu ----
        [0x300] = new(FlightMenuAction.Disabled, 0,
            "(open) the port has two LOD policies, not three; image@0x2172E calls set_detail_level(0)"),
        [0x301] = new(FlightMenuAction.DetailLevel, (int)Render.LodPolicy.Classic,
            "image@0x21738 — set_detail_level(1) = Medium; the port's CLASSIC cascade is the "
                + "original's own distance LOD"),
        [0x302] = new(FlightMenuAction.DetailLevel, (int)Render.LodPolicy.Max,
            "image@0x2173D — set_detail_level(2) = High; the port's MAX policy"),
        [0x303] = new(FlightMenuAction.Disabled, 0,
            "(open) the port always draws the detailed mesh; image@0x21742 flips [0xF0FE]"),
        [0x304] = new(FlightMenuAction.DitheredHorizon, 0,
            "image@0x21749 flips g_dithered_horizon_flag [0x0781]; the port's equivalent is the "
                + "REFINED horizon band against the flat one (--horizon)"),
        [0x305] = new(FlightMenuAction.Clouds, 0,
            "image@0x21750 flips g_clouds_flag [0xB6]"),
        [0x306] = new(FlightMenuAction.BitmapExplosions, 0,
            "image@0x2175F flips g_bitmap_explosions_flag [0xC31E]"),
        [0x307] = new(FlightMenuAction.FlightInfo, 0,
            "image@0x21766 pushes Ctrl-F, which flips g_flight_info_visible [0xB0] "
                + "(the port's H key)"),

        // ---- Help menu ----
        [0x400] = new(FlightMenuAction.Disabled, 0, NoCheats + "; image@0x2176C pushes Ctrl-I"),
        [0x401] = new(FlightMenuAction.Disabled, 0, NoCheats + "; image@0x21772 pushes Ctrl-U"),
        [0x402] = new(FlightMenuAction.Disabled, 0, NoCheats + "; image@0x21778 pushes Ctrl-E"),
        [0x403] = new(FlightMenuAction.Disabled, 0, NoCheats + "; image@0x2177E pushes Ctrl-L"),
        [0x404] = new(FlightMenuAction.Disabled, 0, NoCheats + "; image@0x21784 pushes Ctrl-B"),
        [0x405] = new(FlightMenuAction.Disabled, 0, NoCheats + "; image@0x2178A pushes Ctrl-T"),
        // The four windows are built; the items toggle the live [0xF1CB]
        // mirror and take their check marks from it (ingame_menu_state_refresh reads bits
        // 4/1/2/8 for 0x406..0x409).
        [0x406] = new(FlightMenuAction.OverlayWindow, (int)CockpitOverlayFlags.Map,
            "image@0x21790 pushes Shift-1 — Map Window, [0xF1CB] bit 2 (0x04)"),
        [0x407] = new(FlightMenuAction.OverlayWindow, (int)CockpitOverlayFlags.Envelope,
            "image@0x21796 pushes Shift-2 — Envelope Window, [0xF1CB] bit 0 (0x01)"),
        [0x408] = new(FlightMenuAction.OverlayWindow, (int)CockpitOverlayFlags.Target,
            "image@0x2179C pushes Shift-3 — Target Window, [0xF1CB] bit 1 (0x02)"),
        [0x409] = new(FlightMenuAction.OverlayWindow, (int)CockpitOverlayFlags.Yeager,
            "image@0x217A2 pushes Shift-4 — Yeager Window, [0xF1CB] bit 3 (0x08)"),

        // ---- Location menu ----
        [0x500] = new(FlightMenuAction.Disabled, 0,
            "(open) aircraft_pose_set @image@0x2A25C is not ported; image@0x217A8"),
        [0x501] = new(FlightMenuAction.Disabled, 0, "(open) see On Runway; image@0x217E3"),
        [0x502] = new(FlightMenuAction.Disabled, 0, "(open) see On Runway; image@0x21826"),
        [0x503] = new(FlightMenuAction.Disabled, 0, "(open) see On Runway; image@0x21832"),
    };

    /// <summary>What a row does, or a <see cref="FlightMenuAction.Disabled"/> row for an unknown id.</summary>
    /// <param name="id">The row's id.</param>
    public static FlightMenuEffect For(int id) =>
        Table.TryGetValue(id, out FlightMenuEffect effect)
            ? effect
            : new FlightMenuEffect(
                FlightMenuAction.Disabled, 0, $"(open) no effect is decoded for id 0x{id:X3}");

    /// <summary>Whether the port honours the row.</summary>
    /// <param name="id">The row's id.</param>
    public static bool IsEnabled(int id) =>
        For(id).Action is not (FlightMenuAction.Disabled or FlightMenuAction.Caption);

    /// <summary>Every id the table knows, for the census the headless run prints.</summary>
    public static IEnumerable<int> Ids => Table.Keys;
}
