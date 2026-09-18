namespace CYAC.Port.Core.Sim;

/// <summary>
/// The player-facing time-compression setting — the original's
/// <c>g_scene_tick_shift [0xF104]</c>, written by <c>set_tick_shift @image@0x0C233</c>.
/// </summary>
/// <remarks>
/// INT-only (F8).  The names are the strings the original posts when the setting changes:
/// "TIME COMPRESSION OFF" / "TIME COMPRESSION ON" / "MEGA TIME COMPRESSION ON"
/// (<c>image@0x34F10 / 0x34F25 / 0x34F39</c>;
/// </remarks>
public enum TimeCompression
{
    /// <summary>Shift 0 — 1×, the normal rate.</summary>
    Off = 0,

    /// <summary>Shift 1 — 2×.</summary>
    On = 1,

    /// <summary>Shift 2 — 4×, "MEGA".</summary>
    Mega = 2,
}

/// <summary>
/// How <see cref="TimeCompression"/> is applied — the port's deliberate divergence from the
/// original, kept selectable so the emulator twin can be modelled exactly.
/// </summary>
/// <remarks>
/// <para>
/// The shipped game (and the <c>det_v1</c> patched twin, P1: <c>dt = STEP_TICKS &lt;&lt;
/// [0xF104]</c>) multiplies <b>dt</b> by the compression shift, at <c>image@0x0C13E</c>.  Every
/// integrator then takes a bigger Q8 step, and each step's <c>&gt;&gt; 8</c> truncates differently —
/// so a 4× session is not a 1× session run four times as fast, it is a different trajectory.
/// </para>
/// <para>
/// The port's default multiplies the <b>step count</b> instead (proposal D1-c, human DIRECTION §9.1:
/// "time compression = more sim steps per presented frame, never a larger dt"), so every step is
/// identical and a 4× replay reproduces a 1× replay's per-step state. **This is a FIX-class quirk**
/// — recorded, not hidden.
/// </para>
/// </remarks>
public enum TimeCompressionMode
{
    /// <summary>
    /// The port's law: <c>Dt</c> is always <see cref="TickClock.StepTicks"/> and the compression
    /// shift multiplies how many steps a presented frame runs (1 / 2 / 4).
    /// </summary>
    StepMultiplier = 0,

    /// <summary>
    /// The twin's behaviour: one step per presented frame, <c>Dt = StepTicks &lt;&lt; shift</c>.
    /// Model the patched binary with this; never verify port gameplay with it.
    /// </summary>
    DtScale = 1,
}

/// <summary>
/// A <see cref="TickClock"/>'s complete state — everything a replay checkpoint has to carry to
/// resume the clock exactly (item 7).
/// </summary>
/// <param name="Step">The step ordinal: how many steps have been run since the clock was reset —
/// frames AND UI-phase steps (see <see cref="StepKind"/>).</param>
/// <param name="FrameTimeAccumulator">
/// The Q8 frame-time accumulator, the original's <c>g_frame_time_accum [0xF0D2/F0D4]</c> (u32).
/// </param>
/// <param name="Compression">The live time-compression setting.</param>
/// <param name="Mode">How that setting is applied.</param>
/// <param name="IdleSteps">how many of <paramref name="Step"/> were <see cref="StepKind.Idle"/>
/// (UI phases). <c>Step - IdleSteps</c> is the frame count, and the accumulator is a function of
/// the frame count alone.</param>
public readonly record struct TickClockState(
    ulong Step,
    uint FrameTimeAccumulator,
    TimeCompression Compression,
    TimeCompressionMode Mode,
    ulong IdleSteps = 0);

/// <summary>
/// The port's simulation clock: a fixed step of <see cref="StepTicks"/> PIT ticks that keeps the
/// original's Q8 frame-time accumulator semantics verbatim.
/// </summary>
/// <remarks>
/// <para>
/// INT-only (F8).
/// </para>
/// <code>
///   accum += dt_scaled                                      image@0x0C154 / 0x0C158  (32-bit ADD/ADC)
///   g_master_frame_counter [0xF0C8] = accum >> 8             image@0x0C15C / 0x0C15F  (word alias @0xF0D3)
///   g_frame_count_scaled   [0xF0D0] = accum >> 6             image@0x0C162..0x0C170   (sar_i32_by_cl, CL=6)
///   g_scene_frame_dt_scaled [0xF11C] = dt_scaled             image@0x0C180
/// </code>
/// <para>
/// What the port changes, deliberately and only here: <b>dt is no longer a measurement of the host</b>.  The
/// original derived it by differencing a ~256.1 Hz PIT counter and clamping the result into <c>[1, 128]</c>
/// — a 128× spread that makes the trajectory host-speed-dependent by construction.  The port runs a fixed
/// <see cref="StepTicks"/>-tick step, which is "a machine exactly at the shipped ~51 fps cap" — the rate the
/// game was tuned at — made exact (amendment A1: <c>STEP_TICKS = 5</c>, ratified).  Every non-dt-scaled
/// per-frame constant in the game (AI script delays, angular-velocity step clamps, the <c>[0xC390]</c>
/// 4-frame grace, damage timers) therefore keeps its shipped meaning, which is the reason the step is five
/// ticks and not one.
/// </para>
/// <para>
/// <b>Nothing in this class reads a wall clock.</b>  Real time enters the port exactly once, in
/// <see cref="StepPresenter"/>, which converts elapsed seconds into a step count and hands it in.
/// </para>
/// <para>
/// <b>Known divergences from the twin</b> (both FIX-class):
/// <list type="number">
///   <item><see cref="TimeCompressionMode.StepMultiplier"/> — see that enum.</item>
///   <item>The original's first frame of a scene runs with <c>[0xF11C] = 1</c>
///   (<c>scene_frame_timer_reset @image@0x0C0F8</c>;
///   The port has no 1-tick step: step 0 of a scene is a
///   full <see cref="StepTicks"/>-tick step like every other.  A clean-room clock's accumulator can
///   therefore never equal the twin's <c>[0xF0D2]</c> exactly, and the cross-check test asserts the
///   port's own predicted progression rather than the emulator's memory.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class TickClock
{
    /// <summary>
    /// The simulation step, in original PIT ticks: <b>5</b>.
    /// </summary>
    /// <remarks>
    /// Ratified (amendment A1).  Five ticks of the ~256.1 Hz PIT is ~51.2 Hz, which is exactly the
    /// shipped frame cap: <c>frame_rate_tick_barrier @image@0x2D296</c> spins until <c>g_tick16
    /// [0x43B6] &gt;= 5</c>.  The emulator's det era uses the same constant
    /// (<c>DetPolicy.StepTicks</c>, CYEV v7 header field <c>stepTicks</c>), so a recording and a
    /// port replay open steps the same way.
    /// </remarks>
    public const int StepTicks = 5;

    /// <summary>
    /// The PIT reload the game programs: <c>0x1233</c> = 4659 (<c>image@0x2D02C..0x2D037</c>,
    /// <c>pit_install_256hz @image@0x2D002</c>;
    /// </summary>
    public const int PitReload = 0x1233;

    /// <summary>The 8253/8254 input frequency on a PC, in Hz: 1,193,182 (<c>platform</c>).</summary>
    public const double PitInputHz = 1_193_182.0;

    /// <summary>
    /// The game's tick rate in Hz — <c>PitInputHz / PitReload</c> ≈ 256.1026.  One accumulator unit
    /// is one of these ticks.
    /// </summary>
    public const double TicksPerSecond = PitInputHz / PitReload;

    /// <summary>
    /// The most steps one host frame may run: <b>128</b> — proposal D1-d, the original's
    /// <c>clamp(dt, 1, 128)</c> (<c>image@0x0C140..0x0C150</c>) re-expressed in steps. Enforced
    /// by <see cref="StepPresenter"/>.
    /// </summary>
    public const int MaxStepsPerHostFrame = 128;

    private TimeCompression _compression;

    /// <summary>Creates a clock at step 0 with an empty accumulator.</summary>
    /// <param name="mode">How time compression is applied; the port's law by default.</param>
    public TickClock(TimeCompressionMode mode = TimeCompressionMode.StepMultiplier) => Mode = mode;

    /// <summary>How <see cref="Compression"/> is applied.</summary>
    public TimeCompressionMode Mode { get; }

    /// <summary>
    /// The live time-compression setting — the original's <c>g_scene_tick_shift [0xF104]</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Not one of the three shipped values.</exception>
    public TimeCompression Compression
    {
        get => _compression;
        set
        {
            if (value is not (TimeCompression.Off or TimeCompression.On or TimeCompression.Mega))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, "set_tick_shift @image@0x0C233 writes only 0, 1 or 2.");
            }

            _compression = value;
        }
    }

    /// <summary>The compression shift as a number: 0, 1 or 2.</summary>
    public int CompressionShift => (int)_compression;

    /// <summary>How many steps have run since the last <see cref="Reset"/> — the step ordinal a replay
    /// stamps its input with (proposal D3-a). this counts BOTH kinds (<see cref="StepKind"/>), because
    /// input is indexed by step and a UI phase takes input.</summary>
    public ulong Step { get; private set; }

    /// <summary>How many of <see cref="Step"/> were <see cref="StepKind.Idle"/>: steps in a UI
    /// phase, which deliver input and advance nothing else.</summary>
    public ulong IdleSteps { get; private set; }

    /// <summary>How many of <see cref="Step"/> actually ran the simulation. THIS is the game's
    /// frame count: <see cref="FrameTimeAccumulator"/> is <c>FrameSteps × Dt</c> for a clock that
    /// never changed compression, and every deadline the game expresses in frames counts these.
    /// Reading <see cref="Step"/> as a frame count is the mistake this pair exists to
    /// prevent.</summary>
    public ulong FrameSteps => Step - IdleSteps;

    /// <summary>
    /// The Q8 frame-time accumulator — <c>g_frame_time_accum [0xF0D2/F0D4]</c>, 32 bits, wrapping.
    /// </summary>
    public uint FrameTimeAccumulator { get; private set; }

    /// <summary>
    /// <c>g_master_frame_counter [0xF0C8]</c> = <c>accum &gt;&gt; 8</c>, read as a word.  This is the
    /// counter every gameplay deadline is expressed in: the session end deadline <c>[0xC390]</c> and
    /// the hard cap <c>0x1FFC</c> both compare against it.
    /// </summary>
    public ushort MasterFrameCounter => (ushort)(FrameTimeAccumulator >> 8);

    /// <summary>
    /// <c>g_frame_count_scaled [0xF0D0]</c> = <c>accum &gt;&gt; 6</c>, stored as a word — the game's
    /// four-times-finer schedule counter.
    /// </summary>
    public ushort FrameCountScaled => (ushort)(FrameTimeAccumulator >> 6);

    /// <summary>
    /// The step's dt in accumulator units — the original's <c>g_scene_frame_dt_scaled [0xF11C]</c>,
    /// which every integrator multiplies by in Q8 (<c>muldiv16_signed_shr8 @image@0x11980</c>;
    /// </summary>
    /// <remarks>
    /// Under <see cref="TimeCompressionMode.StepMultiplier"/> this is the constant
    /// <see cref="StepTicks"/>; under <see cref="TimeCompressionMode.DtScale"/> it is
    /// <c>StepTicks &lt;&lt; shift</c>, which is what <c>det_v1</c>'s P1 patch writes.
    /// </remarks>
    public int Dt => Mode == TimeCompressionMode.DtScale ? StepTicks << CompressionShift : StepTicks;

    /// <summary>
    /// How many simulation steps one presented frame runs: 1 / 2 / 4 under the port's law, always 1
    /// under <see cref="TimeCompressionMode.DtScale"/>.
    /// </summary>
    public int StepsPerPresentedFrame =>
        Mode == TimeCompressionMode.StepMultiplier ? 1 << CompressionShift : 1;

    /// <summary>
    /// Runs one step: bumps the ordinal always, and accumulates <see cref="Dt"/> only for a
    /// <see cref="StepKind.Frame"/> step.
    /// </summary>
    /// <param name="kind">
    /// V4. <see cref="StepKind.Frame"/> (the default) advances the simulation.
    /// <see cref="StepKind.Idle"/> is a UI phase: the step exists — so its stamped input is
    /// delivered and the ordinal stays continuous — but the accumulator does not move, which is
    /// what the original does in a frame-less phase (it never calls
    /// <c>scene_frame_timer_advance @image@0x0C120</c> there; V4b §2).
    /// </param>
    public void AdvanceStep(StepKind kind = StepKind.Frame)
    {
        Step++;
        if (kind == StepKind.Idle)
        {
            IdleSteps++;
            return;
        }

        FrameTimeAccumulator = unchecked(FrameTimeAccumulator + (uint)Dt);
    }

    /// <summary>Runs <paramref name="steps"/> steps of one kind.</summary>
    /// <param name="steps">How many steps to run; 0 is legal (a paused frame).</param>
    /// <param name="kind">Which kind they are; frames by default.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="steps"/> is negative.</exception>
    public void Advance(int steps, StepKind kind = StepKind.Frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        for (int i = 0; i < steps; i++)
        {
            AdvanceStep(kind);
        }
    }

    /// <summary>Returns the clock to step 0 with an empty accumulator, keeping the compression setting.</summary>
    /// <remarks>The original's <c>scene_frame_timer_reset @image@0x0C0F8</c> is the analogue; the
    /// port does not reproduce its <c>[0xF11C] = 1</c> first frame (see the type's remarks).</remarks>
    public void Reset()
    {
        Step = 0;
        IdleSteps = 0;
        FrameTimeAccumulator = 0;
    }

    /// <summary>Captures the clock's whole state for a replay checkpoint.</summary>
    public TickClockState Snapshot() => new(Step, FrameTimeAccumulator, _compression, Mode, IdleSteps);

    /// <summary>Restores a state captured by <see cref="Snapshot"/>.</summary>
    /// <param name="state">The captured state.</param>
    /// <exception cref="ArgumentException">The state was captured under a different
    /// <see cref="TimeCompressionMode"/> — a mode change is a kernel-era bump, not a
    /// restore.</exception>
    public void Restore(TickClockState state)
    {
        if (state.Mode != Mode)
        {
            throw new ArgumentException(
                $"checkpoint was taken under {state.Mode}, this clock runs {Mode}; "
                    + "a compression-mode change is a kernel-era bump (contract §4.4).",
                nameof(state));
        }

        if (state.IdleSteps > state.Step)
        {
            throw new ArgumentException(
                $"checkpoint claims {state.IdleSteps} idle step(s) out of {state.Step} — a step is "
                    + "either a frame or a UI phase, never both and never neither.",
                nameof(state));
        }

        Step = state.Step;
        IdleSteps = state.IdleSteps;
        FrameTimeAccumulator = state.FrameTimeAccumulator;
        Compression = state.Compression;
    }

    /// <summary>
    /// What <see cref="FrameTimeAccumulator"/> holds after <paramref name="steps"/> steps of a clock
    /// that started at zero and never changed compression — the prediction the cross-check test
    /// asserts against a recording's step count.
    /// </summary>
    /// <param name="steps">How many FRAME steps ran (UI-phase steps do not accumulate — V4).</param>
    /// <param name="dt">The per-step dt; <see cref="StepTicks"/> under the port's law.</param>
    public static uint PredictAccumulator(ulong steps, int dt = StepTicks) =>
        unchecked((uint)(steps * (ulong)dt));
}
