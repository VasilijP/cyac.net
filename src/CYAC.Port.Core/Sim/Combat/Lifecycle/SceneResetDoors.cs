namespace CYAC.Port.Core.Sim.Combat.Lifecycle;

/// <summary>
/// The two SCENE-lifecycle doors the combat kernel shares with the flight kernel and the mission
/// loader: <c>scene_setup_or_camera_reset @image@0x22643</c> (the flight-init / camera-reset gate
/// the expiry loop's post-drain kill arm calls with <c>AL = 3</c>) and the combat half of
/// <c>scene_or_mission_state_reset @image@0x0C3FD</c>.
/// </summary>
/// <remarks>
/// Neither is verifiable from the combat traces — the first runs at flight start and on a kill
/// (never inside a C5 window), the second only at mission load, and a trace is defined for ONE leg
/// of gameplay.  They are ported from the bytes with unit tests and named seams so C7's per-frame
/// driver and the mission loader have them; their arms carry the same tripwire discipline as
/// everything else in this subsystem.
/// </remarks>
public static class SceneResetDoors
{
    /// <summary>
    /// <c>g_active_aircraft_load_ack [0xC316]</c> — the three-state flight-init gate this door is
    /// the entry of: 0 = flying, 1 = this door ran, 2 = the first frame is armed.
    /// </summary>
    public const int ActiveAircraftLoadAck = 0xC316;

    /// <summary>
    /// <c>g_text_string_mode [0xF0BA]</c>, which is <c>s_aircraft_master +0x122</c> (the master
    /// struct starts at <c>0xEF98</c>) — K8's FLIGHT-END channel, cleared at
    /// <c>image@0x2265A</c> in the same six instructions that raise the ack.
    /// </summary>
    public const int FlightEndChannel = 0xF0BA;

    /// <summary><c>g_scene_word_B95E [0xB95E]</c> — zeroed beside the admitter's timer.</summary>
    public const int SceneWordB95E = 0xB95E;

    /// <summary><c>g_scene_flag_F0E5 [0xF0E5]</c> — armed to 1 by the mission reset.</summary>
    public const int SceneFlagF0E5 = 0xF0E5;

    /// <summary><c>g_scene_flag_F0E4 [0xF0E4]</c> — cleared by the mission reset.</summary>
    public const int SceneFlagF0E4 = 0xF0E4;

    /// <summary><c>g_scene_init_guard_flag [0x0F0B]</c>.</summary>
    public const int SceneInitGuard = 0x0F0B;

    /// <summary>
    /// <c>scene_setup_or_camera_reset @image@0x22643</c> — FAR, one register argument in <c>AL</c>.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="mode">
    /// The scene type in <c>AL</c>: 0 or 3 take the aircraft-scene arm, 1 the tone arm, and 2 does
    /// neither (<c>image@0x2265F..0x2266D</c> — an <c>or</c>/<c>dec</c>/<c>dec dec</c> ladder, so the
    /// domain really is {0, 1, 2, 3} and everything above 3 also reaches only the epilogue).
    /// </param>
    /// <param name="events">The scene seam.</param>
    /// <returns><c>true</c> when the door actually ran; <c>false</c> when the ack made it idempotent.</returns>
    /// <remarks>
    /// The IDEMPOTENCE is the point: <c>cmp byte ptr [0xc316],0 / jne</c> (<c>image@0x22647</c>) returns
    /// immediately once the gate has been raised, and only <c>active_aircraft_load_and_state_reset
    /// @image@0x224CA</c> lowers it again.  The expiry loop's post-drain kill arm calls this with <c>AL =
    /// 3</c>, so on a kill in mid-flight the whole body runs exactly once and then stops answering (K10
    /// §2.5).
    /// </remarks>
    public static bool SetupOrCameraReset(
        CombatRegisters registers, byte mode, ISceneLifecycleEvents events)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(events);

        if (registers.Byte(ActiveAircraftLoadAck) != 0)                         // image@0x22647
        {
            return false;                                                       // image@0x22685
        }

        events.CombatEventNotify(0);                                            // image@0x22650
        registers.SetByte(ActiveAircraftLoadAck, 1);                            // image@0x22655
        registers.SetByte(FlightEndChannel, 0);                                 // image@0x2265A

        if (mode is 0 or 3)                                                     // image@0x22664/0x2266D
        {
            events.AircraftSceneStateInit(0);                                   // image@0x22671
            events.CombatEventNotify(3);                                        // image@0x22676
        }
        else if (mode == 1)                                                     // image@0x22669
        {
            events.PlaySceneTone();                                             // image@0x2267D
        }

        events.ArmFirstFrame();                                                 // image@0x22682
        return true;
    }

    /// <summary>
    /// The COMBAT half of <c>scene_or_mission_state_reset @image@0x0C3FD</c>
    /// (<c>image@0x0C49E..0x0C4DE</c>) — everything the admitter, the expiry list and the pressure
    /// flags need at mission load.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="arena">The pool arena the sort rebuild walks.</param>
    /// <param name="loader">The scenario loader, which BUILDS the list inside the guard window.</param>
    /// <remarks>
    /// <para>
    /// This is where C1's <c>[0x0F0B] == 0</c> push-front rule comes from, and the bracket is exact:
    /// the guard is cleared at <c>image@0x0C4B9</c>, the scenario-load core runs at
    /// <c>image@0x0C4BC</c> (<c>lcall 0x108e:0x8a25</c> = <c>image@0x09305</c>) with every
    /// <c>engagement_list_node_sorted_insert</c> degraded to a PUSH FRONT, the guard is raised at
    /// <c>image@0x0C4C1</c>, and <c>engagement_list_sort_rebuild @image@0x07154</c> then sorts the
    /// whole chain (<c>image@0x0C4C6</c>).  Nothing else in the image opens that window.
    /// </para>
    /// <para>
    /// <c>[0xB95C]:= 0</c> (<c>image@0x0C4A3</c>) makes the admitter's timer gate OPEN on the mission's
    /// first frame, and <c>[0xF0CE]:= 0</c> (<c>image@0x0C4B0</c>) is the ONLY writer that ever clears
    /// the never-cleared-elsewhere "the player has been engaged" flag — so a fresh mission admits
    /// nobody until something first engages the player.
    /// </para>
    /// </remarks>
    public static void MissionStateResetCombatHalf(
        CombatRegisters registers, PoolArena arena, IScenarioLoader loader)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(loader);

        registers.SetWord(SceneWordB95E, 0);                                    // image@0x0C4A0
        registers.SetWord(LifecycleOffsets.AdmissionNextDueFrame, 0);           // image@0x0C4A3
        registers.SetByte(SceneFlagF0E5, 1);                                    // image@0x0C4A8
        registers.NodePassEnabled = 1;                                          // image@0x0C4AB
        registers.SetByte(LifecycleOffsets.PlayerEverEngaged, 0);               // image@0x0C4B0
        registers.SetByte(LifecycleOffsets.PlayerEngagedThisPass, 0);           // image@0x0C4B3
        registers.SetByte(SceneFlagF0E4, 0);                                    // image@0x0C4B6

        registers.SceneInitGuard = 0;                                           // image@0x0C4B9
        loader.LoadScenario();                                                  // image@0x0C4BC
        registers.SceneInitGuard = 1;                                           // image@0x0C4C1
        EngagementList.SortRebuild(arena, registers);                           // image@0x0C4C6

        registers.SetByte(0xC32F, 0);                                           // image@0x0C4CB
        registers.SetWord(LifecycleOffsets.DestructionFocusObject,
            registers.PlayerObjectRef);                                         // image@0x0C4D7
        registers.SetWord(LifecycleOffsets.DestructionFocusObject + 2,
            registers.Word(LifecycleOffsets.PlayerObjectSegment));              // image@0x0C4DA
    }
}

/// <summary>The four out-of-subsystem calls <see cref="SceneResetDoors"/> makes.</summary>
public interface ISceneLifecycleEvents
{
    /// <summary>
    /// <c>combat_event_notify @image@0x0FFCA</c> with the given <c>AL</c>
    /// (<c>image@0x22650</c> with 0, <c>image@0x22676</c> with 3).
    /// </summary>
    /// <param name="eventCode">The <c>AL</c> byte.</param>
    void CombatEventNotify(byte eventCode);

    /// <summary>
    /// <c>aircraft_scene_state_init @image@0x22596</c> with <c>AL = 0</c>
    /// (<c>image@0x22671</c>) — the flight kernel's own scene init.
    /// </summary>
    /// <param name="mode">The <c>AL</c> byte — always 0 at this site.</param>
    void AircraftSceneStateInit(byte mode);

    /// <summary>
    /// <c>sfx_play_tone16_random @image@0x29AF2</c> (<c>image@0x2267D</c>) — a generic combat SFX
    /// tone, not a scene-audio init (P370's correction).
    /// </summary>
    void PlaySceneTone();

    /// <summary>
    /// <c>flight_engine_first_frame_arm @image@0x225CE</c> (<c>image@0x22682</c>), which advances
    /// <c>[0xC316]</c> from 1 to 2.
    /// </summary>
    void ArmFirstFrame();
}

/// <summary>
/// <c>wld_or_s_asset_parser</c>'s scenario-load core, <c>image@0x09305</c> — the routine that fills
/// the engagement list while <c>g_scene_init_guard_flag [0x0F0B]</c> is 0.
/// </summary>
public interface IScenarioLoader
{
    /// <summary>Builds the mission's objects and engagement nodes.</summary>
    void LoadScenario();
}

/// <summary>Every scene-lifecycle call, discarded.</summary>
public sealed class NullSceneLifecycleEvents : ISceneLifecycleEvents, IScenarioLoader
{
    /// <summary>The shared instance.</summary>
    public static NullSceneLifecycleEvents Instance { get; } = new();

    /// <inheritdoc/>
    public void CombatEventNotify(byte eventCode)
    {
    }

    /// <inheritdoc/>
    public void AircraftSceneStateInit(byte mode)
    {
    }

    /// <inheritdoc/>
    public void PlaySceneTone()
    {
    }

    /// <inheritdoc/>
    public void ArmFirstFrame()
    {
    }

    /// <inheritdoc/>
    public void LoadScenario()
    {
    }
}
