using CYAC.Port.Core.Sim.Flight;

namespace CYAC.Port.Core.Sim;

/// <summary>Which device an <see cref="InputEvent"/> came from.</summary>
/// <remarks>
/// The port keeps them apart in the log because a replay has to reproduce the DEVICE, not the
/// converged slot: the same axis pair can come from a stick, from the numpad-as-joystick map
/// (<c>kbd_numpad_joystick_position_set @0x01819</c>) or from the mouse ISR
/// (<c>mouse_event_dispatcher @0x2E046</c>).
/// </remarks>
public enum InputDevice
{
    /// <summary>A key transition (make or break) — the discrete, QUEUED device (D3-c).</summary>
    Key = 0,

    /// <summary>A joystick position + button sample — a CONTINUOUS device, sampled once per step.</summary>
    Joystick = 1,

    /// <summary>A mouse position + button sample — continuous, sampled once per step.</summary>
    Mouse = 2,
}

/// <summary>Joystick buttons, as the game reads them (<c>joystick_secondary_read @image@0x20A74</c>).</summary>
[Flags]
public enum JoystickButtons
{
    /// <summary>Nothing pressed.</summary>
    None = 0,

    /// <summary>Button 1 — the trigger.</summary>
    Trigger = 1,

    /// <summary>Button 2.</summary>
    Button2 = 2,
}

/// <summary>Mouse buttons (the INT 33h AX=0x0C button mask).</summary>
[Flags]
public enum MouseButtons
{
    /// <summary>Nothing pressed.</summary>
    None = 0,

    /// <summary>Left button.</summary>
    Left = 1,

    /// <summary>Right button.</summary>
    Right = 2,

    /// <summary>Middle button.</summary>
    Middle = 4,
}

/// <summary>
/// One input landmark: a device state change stamped with a SIMULATION step, never a clock.
/// </summary>
/// <remarks>
/// Contract §3.3 proposal D3-a: "an input event is stamped with a SIMULATION landmark, not a clock. The
/// stamp is <c>(step_index, slot)</c> … wall-clock, host frame number and instruction counts never appear
/// in the replay." This is the direct application of that finding: record and
/// replay must share delivery dynamics by construction, and a timestamp that means "wherever the machine
/// happened to be" is not a landmark.
/// </remarks>
/// <param name="Step">The step at whose TOP this event is applied.</param>
/// <param name="Slot">The event's ordinal within that step; ties are broken by this, never by time.</param>
public abstract record InputEvent(uint Step, int Slot)
{
    /// <summary>Which device raised it.</summary>
    public abstract InputDevice Device { get; }
}

/// <summary>A key make or break.</summary>
/// <remarks>
/// The payload is the game's own scancode space, make/break in bit 7 — what
/// <c>custom_int09_keyboard_isr @image@0x295DA</c> puts in its ring and
/// <c>kbd_ring_dequeue_and_translate @image@0x296E4</c> dequeues.  E0-prefixed keys have already been
/// folded into the synthetic 0x60..0x6F space by the ISR, so the log carries exactly one byte per
/// transition.
/// </remarks>
/// <param name="Step">The step at whose top this event is applied.</param>
/// <param name="Slot">The event's ordinal within that step.</param>
/// <param name="Scancode">The scancode, break flag in bit 7.</param>
public sealed record KeyInputEvent(uint Step, int Slot, byte Scancode) : InputEvent(Step, Slot)
{
    /// <inheritdoc/>
    public override InputDevice Device => InputDevice.Key;

    /// <summary>The scancode without its break flag.</summary>
    public byte Code => (byte)(Scancode & 0x7F);

    /// <summary>True when this is a key RELEASE (bit 7 set).</summary>
    public bool IsBreak => (Scancode & 0x80) != 0;
}

/// <summary>A joystick device-state sample: stick position and buttons at this step.</summary>
/// <remarks>
/// Axes are the device's own 0..1023 range with 512 = centre (zero DEFLECTION, not zero count).
/// Contract §3.3 proposal D3-c: sampling the position removes the original's capacitor-discharge
/// timing loop (<c>joystick_axis_timing_loop @image@0x20A41</c>, a CLI/STI port-0x201 read that is
/// host-timing-dependent by construction) from the simulation entirely.
/// </remarks>
/// <param name="Step">The step at whose top this sample is applied.</param>
/// <param name="Slot">The event's ordinal within that step.</param>
/// <param name="AxisX">Stick X, 0..1023, 512 = centre.</param>
/// <param name="AxisY">Stick Y, 0..1023, 512 = centre.</param>
/// <param name="Buttons">Pressed buttons.</param>
public sealed record JoystickInputEvent(
    uint Step, int Slot, int AxisX, int AxisY, JoystickButtons Buttons) : InputEvent(Step, Slot)
{
    /// <summary>The device's centre count on both axes.</summary>
    public const int AxisCentre = 512;

    /// <summary>One past the device's maximum count.</summary>
    public const int AxisRange = 1024;

    /// <inheritdoc/>
    public override InputDevice Device => InputDevice.Joystick;
}

/// <summary>A mouse sample: cursor position in SCREEN pixels and the button mask.</summary>
/// <param name="Step">The step at whose top this sample is applied.</param>
/// <param name="Slot">The event's ordinal within that step.</param>
/// <param name="X">Screen X, 0..319.</param>
/// <param name="Y">Screen Y, 0..199.</param>
/// <param name="Buttons">Pressed buttons.</param>
public sealed record MouseInputEvent(
    uint Step, int Slot, int X, int Y, MouseButtons Buttons) : InputEvent(Step, Slot)
{
    /// <inheritdoc/>
    public override InputDevice Device => InputDevice.Mouse;
}

/// <summary>
/// A replay checkpoint: everything needed to resume the kernel at a step, plus a hash of the
/// simulation's INT-only state so a divergence is localised to a step range.
/// </summary>
/// <param name="Step">The step this checkpoint was taken at the top of.</param>
/// <param name="Clock">The clock's state.</param>
/// <param name="Random">Every stream's state.</param>
/// <param name="SimStateHash">
/// A hash of the INT-only simulation state, or null when the host did not supply one.  The port's
/// simulation state does not exist yet, so this is carried, not computed, here.
/// </param>
public sealed record InputLogCheckpoint(
    uint Step,
    TickClockState Clock,
    RandomStreamsState Random,
    string? SimStateHash = null);

/// <summary>
/// The header of an input log — the identity a replay is valid under.
/// </summary>
/// <remarks>
/// Contract §4.3: a replay names the kernel era it was recorded under, and "a replay is valid only
/// under the kernel era it names".  The mission identity and the data-tree identity of §4.3 items
/// 2–3 are not modelled yet; <see cref="Notes"/> carries whatever provenance the writer has, and
/// the omission is recorded rather than faked.
/// </remarks>
public sealed record InputLogHeader
{
    /// <summary>The on-disk format version this build writes.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The determinism era of record — the profile id the <c>det_v1</c> exes are stamped with.</summary>
    public const string DetV1Era = Flight.KernelEra.DetV1;

    /// <summary>The log format version.</summary>
    public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>The kernel era. A replay under another era is refused, not adapted.</summary>
    public string KernelEra { get; init; } = DetV1Era;

    /// <summary>The step length in PIT ticks — <see cref="TickClock.StepTicks"/> for <c>det_v1</c>.</summary>
    public int StepTicks { get; init; } = TickClock.StepTicks;

    /// <summary>
    /// Does this log's step index include <see cref="StepKind.Idle"/> steps (UI phases)?
    /// </summary>
    /// <remarks>
    /// Part of the ERA, not metadata: an idle step changes what a step ordinal MEANS, so a log
    /// recorded with UI-phase steps and replayed by a counter that opens none (or the reverse) lands
    /// every event on the wrong step.  Imported from the emulator's CYEV v8 era-flag bit 0
    /// (<see cref="CyevReader.EraFlagIdleSteps"/>); a v7 recording has none, which is exactly the
    /// pre- emulator rule set and is why that flag exists rather than being assumed.
    /// </remarks>
    public bool OpensIdleSteps { get; init; }

    /// <summary>
    /// For an imported log: the emulator's idle-watchdog threshold, in emulator instructions (CYEV
    /// v9 <c>idleInstr</c>; a pre-v9 file reads <see cref="CyevReader.LegacyIdleInstr"/>).
    /// </summary>
    /// <remarks>
    /// Provenance, deliberately kept even though the port's simulation cannot use it: it is an
    /// instruction count, and no port quantity is measured in emulator instructions.  It is here
    /// because it is the parameter that decides how MANY idle steps the source recording opened, so
    /// two recordings that disagree about a session can name it.  <c>null</c> = the log did not come
    /// from an emulator recording.
    /// </remarks>
    public uint? SourceIdleInstrThreshold { get; init; }

    /// <summary>
    /// How many of <see cref="InputLogHeader.StepCount"/> were UI-phase steps, when it is known.  A
    /// log whose era opens no idle steps knows the answer is 0; one that does cannot learn it from a
    /// CYEV file (records carry a step ordinal, not a step kind), and then it is null.
    /// </summary>
    public uint? IdleStepCount { get; init; }

    /// <summary>How time compression is applied (a step-schedule property, so part of the era).</summary>
    public TimeCompressionMode CompressionMode { get; init; } = TimeCompressionMode.StepMultiplier;

    /// <summary>
    /// The FLIGHT kernel's era descriptor: its step length, its <c>dt</c> policy, the random stream
    /// it draws from, and a hash over the original field set it reads and writes.
    /// </summary>
    /// <remarks>
    /// Contract §4.4: "a replay is valid only under the kernel era it names", and an era bump is
    /// required by a change to the step schedule, to the <c>Sim</c> draw order, or to a field's
    /// INT/DUAL/FLOAT class.  <see cref="KernelEra"/> alone cannot express the last of those, so the
    /// flight kernel stamps the field set it was built from: two builds whose
    /// <see cref="FlightKernelEra.FieldSetHash"/> differ do not simulate the same state, whatever
    /// their era string says.  <see langword="null"/> in a log written before this field existed.
    /// </remarks>
    public FlightKernelEra? FlightKernel { get; init; } = FlightKernelEra.Current;

    /// <summary>The random kernel's seeding configuration.</summary>
    public RandomStreamsConfig Random { get; init; } = new();

    /// <summary>How many steps the recorded session ran, when the writer knew.</summary>
    public uint? StepCount { get; init; }

    /// <summary>Free-text provenance — where this log came from (e.g. an imported recording's name).</summary>
    public string? Notes { get; init; }
}

/// <summary>
/// The port's replay format (D3): step-indexed input plus the seeds that make the mission
/// reproducible.
/// </summary>
/// <remarks>
/// <para>
/// Ratified (D3): the port's replay is an INPUT LOG, re-simulated — not a telemetry stream.  The
/// original's <c>.F</c> film is the other thing and stays a game feature: it records
/// <i>outcomes</i>, contains no input, no seed and not even every object (<c>film_obj_include_filter
/// @image@0x303B0</c>), so it can be re-rendered but never re-simulated and can never prove the
/// port's simulation correct.
/// </para>
/// <para>
/// The events are kept sorted by <c>(Step, Slot)</c>, which is the order a step applies them in — at
/// the TOP of the step, before any subsystem runs, with nothing reading input again until the next
/// step (proposal D3-b).
/// </para>
/// </remarks>
public sealed class InputLog
{
    private readonly List<InputEvent> _events;

    /// <summary>Creates a log.</summary>
    /// <param name="header">The replay identity.</param>
    /// <param name="events">The input landmarks; sorted by <c>(Step, Slot)</c> on construction.</param>
    /// <param name="checkpoints">Optional checkpoints, sorted by step.</param>
    public InputLog(
        InputLogHeader header,
        IEnumerable<InputEvent>? events = null,
        IEnumerable<InputLogCheckpoint>? checkpoints = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        Header = header;
        _events = (events ?? []).OrderBy(e => e.Step).ThenBy(e => e.Slot).ToList();
        Checkpoints = (checkpoints ?? []).OrderBy(c => c.Step).ToList();
    }

    /// <summary>The replay identity.</summary>
    public InputLogHeader Header { get; }

    /// <summary>Every input landmark, in <c>(Step, Slot)</c> order.</summary>
    public IReadOnlyList<InputEvent> Events => _events;

    /// <summary>The checkpoint chain, in step order.</summary>
    public IReadOnlyList<InputLogCheckpoint> Checkpoints { get; }

    /// <summary>The last step any event is stamped for, or null when the log has no events.</summary>
    public uint? LastEventStep => _events.Count == 0 ? null : _events[^1].Step;

    /// <summary>The events a given step applies, in slot order.</summary>
    /// <param name="step">The step ordinal.</param>
    public IEnumerable<InputEvent> EventsForStep(uint step) => _events.Where(e => e.Step == step);

    /// <summary>
    /// Imports a <c>det_v1</c>-era emulator recording (CYEV v7/v8/v9) as an input log.
    /// </summary>
    /// <param name="stream">The <c>.evq</c> stream.</param>
    /// <param name="notes">Provenance to record in the header.</param>
    public static InputLog FromCyev(Stream stream, string? notes = null) =>
        CyevReader.Import(stream, notes).Log;

    /// <summary>
    /// Imports a <c>det_v1</c>-era emulator recording (CYEV v7/v8/v9) as an input log.
    /// </summary>
    /// <param name="bytes">The <c>.evq</c> bytes.</param>
    /// <param name="notes">Provenance to record in the header.</param>
    public static InputLog FromCyev(ReadOnlySpan<byte> bytes, string? notes = null) =>
        CyevReader.Import(bytes, notes).Log;

    /// <summary>Reads a JSON input log.</summary>
    /// <param name="stream">The document stream.</param>
    public static InputLog Read(Stream stream) => InputLogCodec.Read(stream);

    /// <summary>Writes this log as JSON.</summary>
    /// <param name="stream">The destination stream.</param>
    public void Write(Stream stream) => InputLogCodec.Write(this, stream);
}
