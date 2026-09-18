using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CYAC.Port.Core.Sim.Flight;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// Where the combat kernel's <c>dt</c> comes from — the exact analogue of
/// <see cref="CYAC.Port.Core.Sim.Flight.DtPolicy"/>, and for the same reason.
/// </summary>
/// <remarks>
/// The combat ladder reads the SAME word the flight kernel does: <c>g_scene_frame_dt_scaled
/// [0xF11C]</c>, set by <c>scene_frame_timer_advance @image@0x0C120</c> at <c>image@0x00B43</c>
/// before the frame body runs (ladder row 0).  Every combat record of every reference recording
/// carries <c>dt = 5</c> (C0: "dt = 5 on every combat record"), i.e. the det build's uncompressed
/// step — the compressed values 10 and 20 appear in the FLIGHT traces of time-compressed sessions
/// and would reach the combat kernel identically.
/// </remarks>
public enum CombatDtPolicy
{
    /// <summary>
    /// The port's law (D1-c): <c>dt</c> is <see cref="TickClock.Dt"/> and time compression
    /// multiplies the number of steps, never the step.
    /// </summary>
    TickClock = 0,

    /// <summary>Verification: <c>dt</c> is the frame's own <c>[0xF11C]</c>.</summary>
    Recorded = 1,
}

/// <summary>
/// The combat kernel's era descriptor — the four things that decide whether a replay recorded by one
/// build can be re-simulated by another, stamped by <c>InputLog</c> beside
/// <see cref="FlightKernelEra"/>.
/// </summary>
/// <param name="Era">The determinism era — <see cref="KernelEra.DetV1"/> for this build.</param>
/// <param name="StepTicks">The simulation step in original PIT ticks.</param>
/// <param name="DtPolicy">How <c>dt</c> is produced.</param>
/// <param name="StreamPolicy">
/// Which random stream the kernel draws from.  The combat kernel is by far the LFSR's biggest
/// consumer: counts 52 draw sites in the bytecode interpreter alone, 11 in the evasion init, 10 in
/// the player-damage roulette, 8 in the sustain tick, 4 in the fire handler, 2 each in the scorer
/// and the acquisition machine, 1 in the state update — and the det build keeps every one of them on
/// the SINGLE LFSR16 word <c>g_prng_state [0x07A8]</c>, so the DRAW ORDER between subsystems is
/// itself part of the era.  That is why the driver hands one <see cref="RegisterFileCombatRandom"/>
/// to every sub-context.
/// </param>
/// <param name="LadderOrder">
/// A hash over the ladder — the ordered list of the eleven stages and what runs between them.  Two
/// builds that run the same code in a different order produce different trajectories from the same
/// seed (the RNG interleaves), so the ORDER is era-defining in a way it is not for the flight
/// kernel, whose eight stages make only one draw between them.
/// </param>
/// <param name="FieldSetHash">
/// A hash over the ORIGINAL field set the kernel reads and writes: every DGROUP window of the combat
/// register file, every field of the 55-byte engagement record, the 27-byte spawn record's stride
/// and count, and the pool-object fields the ladder integrates.
/// </param>
public readonly record struct CombatKernelEra(
    string Era,
    int StepTicks,
    CombatDtPolicy DtPolicy,
    string StreamPolicy,
    string LadderOrder,
    string FieldSetHash)
{
    /// <summary>The stream every combat draw comes from, as a wire name.</summary>
    /// <remarks>
    /// <c>Sim.Other</c> on LFSR16 — the same family the flight kernel's single draw uses, and by
    /// the SAME WORD: the det build moved only the 54 Fx/Audio sites to the second generator.  A
    /// port that gave the combat kernel its own stream would be a different era.
    /// </remarks>
    public const string KernelStreamPolicy = "Sim.Other/lfsr16 (shared word [0x07A8])";

    /// <summary>
    /// The ladder, in order — the eleven stage boundaries and the piece that runs between each pair.
    /// </summary>
    /// <remarks>
    /// Every row cites the frame body's own trap address, so this list IS the era's definition of
    /// "the order the combat subsystems run in" and a diff of two builds' manifests says which step
    /// moved.
    /// </remarks>
    public static IReadOnlyList<(CombatStage From, CombatStage To, string Site, string What)> Ladder
    { get; } =
    [
        (CombatStage.FramePre, CombatStage.AfterProjectiles, "image@0x00CC9",
            "combat_spawn_slot_count -> combat_object_tick x30 (BACKWARDS from slot 29)"),
        (CombatStage.AfterProjectiles, CombatStage.AfterEffects, "image@0x00CCC..0x00CE0",
            "EXTERNAL: the five effect ticks"),
        (CombatStage.AfterEffects, CombatStage.AfterFlight, "image@0x00CE5..0x00D93",
            "EXTERNAL: flight_advisor_dispatch + the stick read + flight_engine_per_frame_top"),
        (CombatStage.AfterFlight, CombatStage.AfterPlayerFire, "image@0x00D98..0x00E13",
            "the ground-shadow pose, the [0xC31C] gate, weapon_fire_event_scheduler (call 1)"),
        (CombatStage.AfterPlayerFire, CombatStage.BeforeEngagement, "image@0x00E08..0x01071",
            "the KEY LADDER loop: per key an arm then the scheduler again"),
        (CombatStage.BeforeEngagement, CombatStage.AfterEngagement, "image@0x01092",
            "engagement_expiry_loop -> weapon_fire_combat_loop_per_shot (the 14-arm FSM)"),
        (CombatStage.AfterEngagement, CombatStage.AfterPlayerTick, "image@0x01095",
            "engagement_per_frame_tick (the player sustain tick)"),
        (CombatStage.AfterPlayerTick, CombatStage.AfterAdmission, "image@0x0109A",
            "engagement_new_slot_select_and_commit (the admitter)"),
        (CombatStage.AfterAdmission, CombatStage.AfterMissionHook, "image@0x010AA..0x01703",
            "EXTERNAL render phase + target_acquisition_state_machine_step + the .S per-frame hook"),
        (CombatStage.AfterMissionHook, CombatStage.FrameEnd, "image@0x01708..0x017DF",
            "EXTERNAL: hud_per_frame_draw and the present"),
    ];

    /// <summary>
    /// The pool-object (<c>s_pool_arena_entry</c>) fields the combat ladder integrates — the world
    /// object's own state, as distinct from the engagement record hanging off it.
    /// </summary>
    public static IReadOnlyList<(int Offset, int Length, string Name)> PoolObjectFields { get; } =
    [
        (0x00, 2, "class_record_nearptr"),
        (0x02, 2, "flag_filter_word"),
        (0x04, 2, "list_link"),
        (0x06, 4, "pos_x"),
        (0x0A, 4, "pos_y"),
        (0x0E, 4, "pos_z"),
        (0x12, 2, "heading"),
        (0x14, 2, "pitch"),
        (0x16, 2, "roll"),
    ];

    /// <summary>The canonical manifest the field-set hash is taken over.</summary>
    public static string FieldSetManifest { get; } = BuildFieldManifest();

    /// <summary>The canonical manifest the ladder hash is taken over.</summary>
    public static string LadderManifest { get; } = BuildLadderManifest();

    /// <summary>This build's field-set hash.</summary>
    public static string CurrentFieldSetHash { get; } = Hash(FieldSetManifest);

    /// <summary>This build's ladder hash.</summary>
    public static string CurrentLadderHash { get; } = Hash(LadderManifest);

    /// <summary>This build's era descriptor, under the port's own dt law.</summary>
    public static CombatKernelEra Current { get; } = For(CombatDtPolicy.TickClock);

    /// <summary>The era descriptor for a given dt policy.</summary>
    /// <param name="dtPolicy">The dt policy the session runs under.</param>
    public static CombatKernelEra For(CombatDtPolicy dtPolicy) => new(
        KernelEra.DetV1,
        TickClock.StepTicks,
        dtPolicy,
        KernelStreamPolicy,
        CurrentLadderHash,
        CurrentFieldSetHash);

    private static string BuildFieldManifest()
    {
        List<string> lines = new List<string>();
        foreach (CombatRegisterWindow window in CombatRegisterWindows.All)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"dgroup [0x{window.DgroupOffset:X4}]:{window.Length} {window.Name} {window.Class}"));
        }

        foreach (EngagementFieldSpan field in EngagementStateCodec.Layout)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"engagement +0x{field.Offset:X2}:{field.Length} {field.Name}"));
        }

        foreach ((int offset, int length, string name) in PoolObjectFields)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture, $"object +0x{offset:X2}:{length} {name}"));
        }

        lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"spawn table [0x{CombatSpawnTable.TableDgroupOffset:X4}] "
                + $"{CombatSpawnTable.SlotCount}x{CombatSpawnTable.SlotBytes}"));

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines) + '\n';
    }

    private static string BuildLadderManifest()
    {
        StringBuilder sb = new StringBuilder();
        foreach ((CombatStage from, CombatStage to, string site, string what) in Ladder)
        {
            sb.Append(CultureInfo.InvariantCulture, $"CS{(int)from}->CS{(int)to} {site} {what}\n");
        }

        return sb.ToString();
    }

    private static string Hash(string manifest)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(manifest));
        StringBuilder text = new StringBuilder("0x", 18);
        for (int i = 0; i < 8; i++)
        {
            text.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
