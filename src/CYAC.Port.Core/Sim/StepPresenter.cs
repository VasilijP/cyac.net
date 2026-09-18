namespace CYAC.Port.Core.Sim;

/// <summary>
/// The one place real time enters the port: converts elapsed host seconds into a number of
/// simulation steps to run, and carries the remainder.
/// </summary>
/// <remarks>
/// <para>
/// FLOAT-only, and outside the reproducible spine by construction — the sim never sees a clock (human
/// DIRECTION,: "wall clock enters exactly once: the presenter decides how many steps to run this frame
/// from elapsed real time, with a max-steps cap to avoid spiral-of-death. Nothing inside a step reads a
/// clock").  Two hosts with different frame rates therefore run the same steps in the same order — only
/// the number per presented frame differs.
/// </para>
/// <para>
/// <b>The cap.</b> The original clamped its measured dt into <c>[1, 128]</c> accumulator units
/// (<c>image@0x0C140..0x0C150</c>) and then reset the tick baseline to <i>now</i> (<c>image@0x0C179</c>)
/// — i.e. it <b>discarded</b> whatever time the clamp cut off.  The port does the same in the new units:
/// at most <see cref="TickClock.MaxStepsPerHostFrame"/> steps in one host frame (proposal D1-d), and the
/// surplus is dropped rather than banked, so a stall cannot pay itself back as a burst.
/// </para>
/// <para>
/// <b>The clamp's lower half.</b> D1-d also says "never fewer than 1", which the original needed
/// because it had no remainder to carry: a frame shorter than one tick would otherwise have advanced
/// nothing at all.  The port carries the remainder (<see cref="PendingTicks"/>), so a host frame
/// faster than ~51 Hz legitimately runs 0 steps and the leftover fraction is spent on a later frame.
/// Forcing a step there would run the simulation <i>faster than real time</i> on a fast machine,
/// which is the very host-dependence the fixed step exists to remove.  Recorded as a deliberate
/// reading of D1-d, not an omission.
/// </para>
/// <para>
/// <b>Pause</b> is zero steps and banks nothing.
/// </para>
/// <para>
/// <b>The UI phase.</b> <see cref="UiPhase"/> does NOT stop the step loop: while it is set the presenter
/// keeps emitting steps at the same rate and labels them <see cref="StepKind.Idle"/>. The host still
/// delivers each step's stamped input and may still present; only the simulation stands still
/// (<see cref="TickClock.AdvanceStep"/> skips the accumulator).  That is what keeps a step-indexed input
/// log deliverable inside a menu — the property whose absence deadlocked two human recordings on.  Time
/// compression is a simulation setting, so a UI phase runs ONE step per host frame regardless of it.
/// Pause is the different thing: pause emits no steps at all, so a paused game takes no input either.
/// </para>
/// </remarks>
/// <param name="clock">The clock whose <see cref="TickClock.StepsPerPresentedFrame"/> this presenter
/// multiplies by; the presenter never advances it, so the host stays in charge of the step loop.</param>
public sealed class StepPresenter(TickClock clock)
{
    private double _pendingTicks;

    /// <summary>The clock this presenter paces.</summary>
    public TickClock Clock { get; } = clock;

    /// <summary>When set, <see cref="Advance"/> returns 0 and banks nothing.</summary>
    public bool Paused { get; set; }

    /// <summary>
    /// The host is in a UI PHASE (a menu, a modal dialog, a loading screen): steps keep coming so
    /// input keeps being delivered, but they are <see cref="StepKind.Idle"/> and the simulation
    /// does not advance.  See the type's remarks; this is the port's counterpart of the emulator's
    /// det-era idle steps (CYEV era-flag bit 0).
    /// </summary>
    public bool UiPhase { get; set; }

    /// <summary>The kind of step <see cref="Advance"/> is currently emitting.</summary>
    public StepKind Kind => UiPhase ? StepKind.Idle : StepKind.Frame;

    /// <summary>How many of <see cref="StepsEmitted"/> were UI-phase steps.</summary>
    public ulong IdleStepsEmitted { get; private set; }

    /// <summary>
    /// The carried fraction of a step, in ticks — always in <c>[0, StepTicks)</c> after
    /// <see cref="Advance"/>.
    /// </summary>
    public double PendingTicks => _pendingTicks;

    /// <summary>How many steps the presenter has emitted since it was created or reset.</summary>
    public ulong StepsEmitted { get; private set; }

    /// <summary>True when the last <see cref="Advance"/> hit the cap and discarded time.</summary>
    public bool LastFrameHitCap { get; private set; }

    /// <summary>How many host frames have hit the cap (a spiral-of-death counter for the host's HUD).</summary>
    public long CappedFrames { get; private set; }

    /// <summary>
    /// Converts a host frame's elapsed real time into the number of simulation steps to run.
    /// </summary>
    /// <param name="elapsedRealSeconds">Seconds of wall time since the previous call.</param>
    /// <returns>Steps to run, in <c>[0, <see cref="TickClock.MaxStepsPerHostFrame"/>]</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The elapsed time is negative, NaN or infinite — a host clock fault the presenter refuses to
    /// turn into a simulation fault.
    /// </exception>
    public int Advance(double elapsedRealSeconds)
    {
        if (double.IsNaN(elapsedRealSeconds) || double.IsInfinity(elapsedRealSeconds)
            || elapsedRealSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elapsedRealSeconds),
                elapsedRealSeconds,
                "elapsed host time must be a finite, non-negative number of seconds.");
        }

        LastFrameHitCap = false;
        if (Paused)
        {
            return 0;
        }

        _pendingTicks += elapsedRealSeconds * TickClock.TicksPerSecond;

        long wholeSteps = (long)(_pendingTicks / TickClock.StepTicks);
        if (wholeSteps <= 0)
        {
            return 0;
        }

        _pendingTicks -= (double)wholeSteps * TickClock.StepTicks;

        // A UI phase runs one step per elapsed step-time: time compression scales the SIMULATION,
        // and a UI phase is not running one.
        long steps = UiPhase ? wholeSteps : wholeSteps * Clock.StepsPerPresentedFrame;
        if (steps > TickClock.MaxStepsPerHostFrame)
        {
            steps = TickClock.MaxStepsPerHostFrame;
            _pendingTicks = 0.0;                    // the original's baseline reset: discard, never bank
            LastFrameHitCap = true;
            CappedFrames++;
        }

        StepsEmitted += (ulong)steps;
        if (UiPhase) IdleStepsEmitted += (ulong)steps;
        return (int)steps;
    }

    /// <summary>Drops the carried remainder and the counters — a scene change, not a pause.</summary>
    public void Reset()
    {
        _pendingTicks = 0.0;
        StepsEmitted = 0;
        IdleStepsEmitted = 0;
        CappedFrames = 0;
        LastFrameHitCap = false;
    }

    /// <summary>
    /// The whole host frame in one call: how many steps to run and what kind they are. A host
    /// loop that uses this cannot forget to label a UI phase's steps, which is the one
    /// mistake that turns a menu into a deadlock.
    /// </summary>
    /// <param name="elapsedRealSeconds">Seconds of wall time since the previous call.</param>
    /// <returns>The step count and the kind every one of them is.</returns>
    public (int Steps, StepKind Kind) AdvanceSteps(double elapsedRealSeconds) =>
        (Advance(elapsedRealSeconds), Kind);
}
