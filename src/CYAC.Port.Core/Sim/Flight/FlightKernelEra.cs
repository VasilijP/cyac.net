using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// The determinism eras this build knows.
/// </summary>
/// <remarks>
/// "A replay is valid only under the kernel era it names." An era bump is required by any change to
/// the step length or step schedule, to the <c>Sim</c> draw order or <c>prng_*</c> semantics, to
/// input landmark semantics, to a field's INT/DUAL/FLOAT class, or to the transformed data an
/// integer-side table comes from.
/// </remarks>
public static class KernelEra
{
    /// <summary>
    /// <c>det_v1</c> — the era ratified (amendment A1: <c>STEP_TICKS = 5</c>, <c>Sim.*</c>
    /// on LFSR16), and the profile id the <c>det_v1</c> emulator exes are stamped with.
    /// </summary>
    public const string DetV1 = "det_v1";
}

/// <summary>
/// The flight kernel's era descriptor — the four things that decide whether a replay recorded by one build can be
/// re-simulated by another.
/// </summary>
/// <param name="Era">The determinism era, <see cref="KernelEra.DetV1"/> for this build.</param>
/// <param name="StepTicks">The simulation step in original PIT ticks (<see cref="TickClock.StepTicks"/>).</param>
/// <param name="DtPolicy">
/// How the kernel's <c>dt</c> is produced: <see cref="Flight.DtPolicy.TickClock"/> is the port's own
/// law (a fixed step; time compression multiplies the STEP COUNT), <see cref="Flight.DtPolicy.Recorded"/>
/// replays the original's <c>g_scene_frame_dt_scaled [0xF11C]</c> sequence and exists for
/// verification against a recording.
/// </param>
/// <param name="StreamPolicy">
/// Which random stream the kernel draws from, and with which generator — the kernel makes exactly ONE draw
/// (<c>prng_rand8</c> inside the state-3 pull-up arm;
/// </param>
/// <param name="FieldSetHash">
/// A hash over the ORIGINAL field set the kernel reads and writes — every
/// <c>s_aircraft_master</c> offset the port models, every DGROUP window in the chain's read/write
/// set, and the player world-object fields the chain integrates.  A field entering or leaving that
/// set changes what a replay's state means, so it is part of the era (§4.4 "a field migrating
/// between INT / DUAL / FLOAT").
/// </param>
public readonly record struct FlightKernelEra(
    string Era,
    int StepTicks,
    DtPolicy DtPolicy,
    string StreamPolicy,
    string FieldSetHash)
{
    /// <summary>
    /// The stream the kernel's single <c>prng_rand8</c> draw comes from, as a wire name:
    /// <c>Sim.Other</c> on LFSR16 (<see cref="SimStreamKernelRandom"/>).
    /// </summary>
    public const string KernelStreamPolicy = "Sim.Other/lfsr16";

    /// <summary>
    /// The DGROUP windows the flight chain reads or writes, as
    /// <c>(guest offset, byte length, name)</c>.
    /// </summary>
    /// <remarks>
    /// Derived from (two independent derivations: the read/write sets,
    /// and an exhaustive Capstone sweep of direct-addressed operands over the chain's 69-function
    /// call closure) narrowed to the windows the PORT's kernel consumes — the landing-zone tables,
    /// the named-mesh slots and the OOM-abort tree's globals sit behind the
    /// <see cref="IKernelWorld"/> seam and are the world's state, not the kernel's.
    /// </remarks>
    public static IReadOnlyList<(int Offset, int Length, string Name)> DgroupWindows { get; } =
    [
        (0x07A8, 2, "g_prng_state"),                 // RW prng_lfsr_step @image@0x19ED0 — the ONE draw
        (0x0CD8, 20, "mat3_vec3_scratch"),           // W  apply-velocity's projection scratch
        (0x35F8, 14, "joystick_calib_table"),        // R  the pull-up tuning table (7 words)
        (0x3606, 2, "g_wind_drift_accumulator"),     // RW apply-velocity + damage_or_crash_check
        (0xBB40, 12, "g_aoa_physics_accum_block"),   // RW aoa_physics_tick's 6-word accumulator
        (0xE478, 2, "g_joystick_axis_x_clamped"),    // R  the driver's mirror source (image@0x2A6AB)
        (0xE47A, 2, "g_joystick_axis_y_clamped"),    // R  idem (image@0x2A6B1)
        (0xE47C, 2, "g_joystick_x_min_i16"),         // R  joystick_to_control_deflect divisor
        (0xE47E, 2, "g_joystick_x_max_i16"),         // R  idem
        (0xE480, 2, "g_joystick_y_min_i16"),         // R  idem
        (0xE484, 2, "g_joystick_y_max_i16"),         // R  idem
        (0xF0C8, 2, "g_master_frame_counter"),       // R  aoa_range_scanner's cache key
        (0xF10E, 1, "g_briefing_difficulty_idx"),    // R  aoa_range_scanner's ±1 widening
        (0xF11C, 2, "g_scene_frame_dt_scaled"),      // R  by every integrator (12 sites)
        (0xF1B8, 2, "joystick_x_mirror"),            // W  the driver's prologue, R the deflections
        (0xF1BA, 2, "joystick_y_mirror"),            // W  idem
        (0xF1BE, 2, "cached_airspeed_hi"),           // W  control integration, R apply-velocity
        (0xF1C0, 4, "g_vertical_speed_i32"),         // W  apply-velocity, R crash_conditions_valid
        (0xF1C4, 1, "ground_proximity_margin"),      // RW control integration
    ];

    /// <summary>
    /// The player world-object fields the chain integrates — <c>s_object_slot</c> offsets inside the
    /// block <c>master[+0x11A]</c> points at (K0 finding F1).
    /// </summary>
    public static IReadOnlyList<(int Offset, int Length, string Name)> PlayerObjectFields { get; } =
    [
        (0x06, 4, "pos_x"),
        (0x0A, 4, "pos_y"),
        (0x0E, 4, "pos_z"),
        (0x12, 2, "heading"),
        (0x14, 2, "pitch"),
        (0x16, 2, "roll"),
    ];

    /// <summary>
    /// The canonical text the hash is taken over: one line per field, sorted ordinally, so a diff of
    /// two builds' manifests says exactly which field entered or left the kernel.
    /// </summary>
    public static string FieldSetManifest { get; } = BuildManifest();

    /// <summary>
    /// This build's hash of the field set — the first 8 bytes of the SHA-256 of
    /// <see cref="FieldSetManifest"/>, as <c>0x…</c>.
    /// </summary>
    public static string CurrentFieldSetHash { get; } = ComputeFieldSetHash();

    /// <summary>This build's era descriptor, under the port's own dt law.</summary>
    public static FlightKernelEra Current { get; } = For(Flight.DtPolicy.TickClock);

    /// <summary>The era descriptor for a given dt policy.</summary>
    /// <param name="dtPolicy">The dt policy the session runs under.</param>
    public static FlightKernelEra For(DtPolicy dtPolicy) => new(
        KernelEra.DetV1,
        TickClock.StepTicks,
        dtPolicy,
        KernelStreamPolicy,
        CurrentFieldSetHash);

    private static string BuildManifest()
    {
        List<string> lines = new List<string>();
        foreach (AircraftMasterField field in AircraftMasterCodec.ModelledFields)
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"master +0x{field.Offset:X3}:{field.Width} {field.OriginalName}"));
        }

        foreach ((int offset, int length, string name) in DgroupWindows)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"dgroup [0x{offset:X4}]:{length} {name}"));
        }

        foreach ((int offset, int length, string name) in PlayerObjectFields)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"object +0x{offset:X2}:{length} {name}"));
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines) + '\n';
    }

    private static string ComputeFieldSetHash()
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(FieldSetManifest));
        StringBuilder text = new StringBuilder("0x", 18);
        for (int i = 0; i < 8; i++)
        {
            text.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
