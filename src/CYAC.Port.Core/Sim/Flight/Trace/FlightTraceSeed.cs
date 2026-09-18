using System.Buffers.Binary;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Flight.Trace;

/// <summary>
/// Builds a <see cref="FlightKernelState"/> from one <c>S0</c> record of a
/// <c>cyac-flight-trace</c> file — the ONE way the port can currently create a frame-1 flight state.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Sim.Flight.ColdStart.FlightColdStart"/> now builds frame 1 from the data tree alone,
/// and the host's normal start is <c>--test-flight</c>.  What survives is this type's real job:
/// <b>the VERIFICATION seed</b>.  Every closed-loop test and every <c>--replay</c> pass starts from
/// a genuine record, and a developer who wants to begin mid-sortie asks for a step; neither can be
/// expressed as a cold start.  The rule is promoted out of
/// <c>FlightKernelClosedLoopTests.Loop.Seed</c> so it lives in one place.
/// </para>
/// <para>
/// The record layout this reads is: the 298-byte <c>s_aircraft_master</c>
/// (<see cref="FlightTraceRecord.Master"/>), the 64-byte world object at <c>master[+0x11A]</c>
/// (<see cref="FlightTraceRecord.PlayerObject"/>, K0 finding F1) and the named DGROUP windows.
/// </para>
/// </remarks>
public static class FlightTraceSeed
{
    /// <summary>The trace global that names the aircraft a record was flying: <c>[0xC31A]</c>.</summary>
    public const string ActiveAircraftGlobal = "g_active_aircraft_idx";

    /// <summary>The trace global holding <c>[0xE478..0xE485]</c> — the axes and their calibration.</summary>
    public const string AxisWindowGlobal = "joystick_axes_and_calibration";

    /// <summary>The trace global holding the pull-up tuning table <c>[0x35F8]</c>.</summary>
    public const string PullUpTuningGlobal = "joystick_calib_table";

    /// <summary>The trace global holding <c>[0xF1B8..0xF1C4]</c> — the chain's own outputs.</summary>
    public const string KernelOutputsGlobal = "flight_kernel_outputs";

    /// <summary>
    /// The <c>data/</c> basename of the aircraft a record was flying.
    /// </summary>
    /// <param name="record">Any record of the frame.</param>
    /// <remarks>
    /// Per RECORD, never latched per file — a recording may hold several flights in different
    /// aircraft, and every record carries <c>g_active_aircraft_idx [0xC31A]</c> in its global
    /// window precisely so a reader never has to assume it.
    /// </remarks>
    public static string AircraftBasename(in FlightTraceRecord record) =>
        AircraftDefinition.FlyableBasenames[record.GlobalWord(ActiveAircraftGlobal)];

    /// <summary>The calibration extremes the deflections divide by, from a record's own window.</summary>
    /// <param name="record">The record to read.</param>
    /// <remarks>
    /// <c>[0xE47C]/[0xE47E]/[0xE480]/[0xE484]</c> — the divisors of
    /// <c>joystick_to_control_deflect</c>'s <c>idiv</c> (<c>image@0x2B94E</c>), so a zeroed window is
    /// a divide fault rather than a neutral stick.
    /// </remarks>
    public static JoystickCalibration ReadCalibration(in FlightTraceRecord record)
    {
        ReadOnlySpan<byte> axes = record.Global(AxisWindowGlobal);
        return new JoystickCalibration(
            ReadI16(axes, 0x04), ReadI16(axes, 0x06), ReadI16(axes, 0x08), ReadI16(axes, 0x0C));
    }

    /// <summary>The pull-up tuning table <c>[0x35F8]</c> as the record carried it.</summary>
    /// <param name="record">The record to read.</param>
    public static PullUpTuningTable ReadPullUpTuning(in FlightTraceRecord record) =>
        PullUpTuningTable.FromWords(record.Global(PullUpTuningGlobal));

    /// <summary>The two clamped joystick axes <c>[0xE478]/[0xE47A]</c> the record carried.</summary>
    /// <param name="record">The record to read.</param>
    public static (short X, short Y) ReadAxes(in FlightTraceRecord record)
    {
        ReadOnlySpan<byte> axes = record.Global(AxisWindowGlobal);
        return (ReadI16(axes, 0x00), ReadI16(axes, 0x02));
    }

    /// <summary>
    /// The per-frame outside inputs a record supplies — everything
    /// <see cref="FlightKernel.Step(FlightKernelState, FlightFrameInputs, int, IKernelRandom, IKernelWorld, IVelocityDynamics)"/>
    /// reads that is neither state nor a kernel output.
    /// </summary>
    /// <param name="pre">The frame's <c>S0</c> record.</param>
    /// <remarks>
    /// The axes, <c>g_master_frame_counter [0xF0C8]</c>, <c>g_briefing_difficulty_idx [0xF10E]</c>
    /// and <c>g_scene_frame_dt_scaled [0xF11C]</c> — the K5 input set, which is why a closed loop
    /// over them is a proof and not a transcription.
    /// </remarks>
    public static FlightFrameInputs FrameInputs(in FlightTraceRecord pre)
    {
        (short x, short y) = ReadAxes(pre);
        return new FlightFrameInputs(
            x,
            y,
            pre.GlobalWord("frame_time_group"),
            pre.GlobalByte("g_briefing_difficulty_idx") == 0,
            pre.GlobalWord("g_scene_frame_dt_scaled"));
    }

    /// <summary>
    /// Builds the kernel's carried state from an <c>S0</c> record: the master struct, the player's
    /// world object and the DGROUP window.
    /// </summary>
    /// <param name="definition">The aircraft the record was flying (see <see cref="AircraftBasename"/>).</param>
    /// <param name="pre">The <c>S0</c> record to seed from.</param>
    /// <param name="calibration">The frame's calibration window (see <see cref="ReadCalibration"/>).</param>
    /// <param name="tuning">The frame's pull-up tuning table (see <see cref="ReadPullUpTuning"/>).</param>
    /// <returns>A state the kernel can be stepped from, and a pool that owns the player object.</returns>
    /// <remarks>
    /// <see cref="Aircraft.Load"/> seeds the 53 master bytes the codec does not carry AND the whole
    /// FME envelope table, so the definition is load-bearing, not cosmetic; the codec then overlays
    /// the record's own 298 bytes.  The player object is inserted into a fresh
    /// <see cref="WorldObjectPool"/> as class <c>f4</c> — the kernel reads only the object's position
    /// and attitude, never its class, and the closed-loop verification runs on this exact shape.
    /// </remarks>
    public static FlightSeedResult CreateState(
        AircraftDefinition definition,
        in FlightTraceRecord pre,
        JoystickCalibration calibration,
        PullUpTuningTable tuning)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Aircraft aircraft = Aircraft.Load(definition);
        AircraftMasterCodec.ApplyMasterBytes(pre.Master, aircraft);

        WorldObjectPool pool = new WorldObjectPool();
        WorldObject player = pool.Insert(ClassRegistry.F4);
        ReadOnlySpan<byte> window = pre.PlayerObject;
        player.X = ReadI32(window, 0x06);
        player.Y = ReadI32(window, 0x0A);
        player.Z = ReadI32(window, 0x0E);
        player.Heading = new Angle(ReadU16(window, 0x12));
        player.Pitch = new Angle(ReadU16(window, 0x14));
        player.Roll = new Angle(ReadU16(window, 0x16));

        ReadOnlySpan<byte> outputs = pre.Global(KernelOutputsGlobal);
        FlightInputs inputs = new FlightInputs
        {
            Calibration = calibration,
            PullUpTuning = tuning,
            JoystickX = ReadI16(outputs, 0x00),
            JoystickY = ReadI16(outputs, 0x02),
            CachedAirspeedHi = ReadI16(outputs, 0x06),
            GroundProximityFlag = outputs[0x0C],
            VerticalSpeed = ReadI32(outputs, 0x08),
            WindDriftAccumulator = unchecked((short)pre.GlobalWord("g_wind_drift_accumulator")),
        };

        return new FlightSeedResult(new FlightKernelState(aircraft, player, inputs), pool);
    }

    private static int ReadI32(ReadOnlySpan<byte> window, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(window[offset..]);

    private static short ReadI16(ReadOnlySpan<byte> window, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(window[offset..]);

    private static ushort ReadU16(ReadOnlySpan<byte> window, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(window[offset..]);
}

/// <summary>What <see cref="FlightTraceSeed.CreateState"/> produced.</summary>
/// <param name="State">The kernel state, ready to be stepped.</param>
/// <param name="Pool">
/// The scene's object pool, with <see cref="FlightKernelState.Player"/> already inserted.  It is
/// returned rather than discarded because it is the scene's object list and its arena budget: the
/// steps that add scenery and other aircraft (steps 3 and 4) insert into THIS pool.
/// </param>
public readonly record struct FlightSeedResult(FlightKernelState State, WorldObjectPool Pool);
