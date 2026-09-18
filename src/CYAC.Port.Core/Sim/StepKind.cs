namespace CYAC.Port.Core.Sim;

/// <summary>
/// What one simulation step DID — the port's UI-phase model.
/// </summary>
/// <remarks>
/// <para>
/// <b>The model, in three sentences.</b>
/// (1) A <b>step</b> is the port's atomic unit of progress, and every step — whichever kind — delivers
/// the input stamped for it at its top, so step ordinals are continuous and an input log is
/// deliverable everywhere.
/// (2) A <see cref="Frame"/> step advances the simulation: <see cref="TickClock.AdvanceStep"/> adds
/// <see cref="TickClock.Dt"/> to the frame-time accumulator, and every subsystem runs.
/// (3) An <see cref="Idle"/> step is a UI PHASE — a menu, a modal dialog, a loader: input is
/// delivered, the simulation is NOT advanced (the accumulator does not move, so no deadline,
/// timer or integrator ages), and presentation may still run.
/// </para>
/// <para>
/// <b>Where it comes from.</b> This is the port's reading of a fact measured in the original binary:
/// the in-flight menu bar <c>menu_bar_engine @image@0x20D04</c> blocks in
/// <c>kbd_event_wait_until_nonzero @image@0x204F8</c> and calls <c>scene_frame_timer_advance
/// @image@0x0C120</c> <b>zero</b> times. So the shipped game already has phases that run no frame at
/// all, the frame-time accumulator does not advance in them, and yet they must still take input —
/// otherwise the key that closes the menu is the key that cannot be delivered.  The emulator's det era
/// models this with IDLE STEPS (CYEV v8 era-flag bit 0); the port models the same thing with this
/// enum, so a recording and a port replay index input the same way.
/// </para>
/// <para>
/// <b>The consequence worth stating out loud.</b> Step ordinal is NOT frame ordinal.  A log's step
/// count is frames + UI-phase steps, and only <see cref="TickClock.FrameSteps"/> drives the
/// accumulator.  Anything that used to read "step" as "frame" reads
/// <see cref="TickClock.FrameSteps"/> instead.
/// </para>
/// </remarks>
public enum StepKind
{
    /// <summary>The simulation runs: input is applied, the clock accumulates, subsystems tick.</summary>
    Frame = 0,

    /// <summary>
    /// A UI phase: input is applied and presentation may run, but the simulation is not advanced —
    /// the clock's accumulator, and therefore every frame-counted deadline in the game, stands still.
    /// </summary>
    Idle = 1,
}
