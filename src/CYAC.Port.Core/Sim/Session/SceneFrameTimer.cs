using CYAC.Port.Core.Sim.Combat;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// <c>scene_frame_timer_advance @image@0x0C120</c> — step 0 of the gameplay frame
/// (<c>image@0x00B43</c>), the routine that makes <c>dt</c> and the frame counter.
/// </summary>
/// <remarks>
/// <para>
/// Porting it matters more than it looks, because of one line: <b>the master frame counter is NOT a
/// counter.</b>
/// </para>
/// <code>
/// image@0x0C154  [0xF0D2:0xF0D4] += dt          ; the 32-bit FRAME TIME
/// image@0x0C15C [0xF0C8]:= [0xF0D3]; the frame counter is frameTime &gt;&gt; 8
/// image@0x0C162  [0xF0D0] := frameTime &gt;&gt; 6  ; ulshr_i32_by_cl(6)
/// image@0x0C180  [0xF11C] := dt
/// </code>
/// <para>
/// So <c>g_master_frame_counter [0xF0C8]</c> advances by ONE every 256 ticks of frame time — at the
/// determinism contract's fixed <c>dt = 5</c>, once every 51.2 frames — and every deadline the combat
/// kernel arms against it (the admitter's <c>[0xB95C]</c>, the engagement expiry
/// <c>node[+0x0B]</c>, the weapon-fire 64-tick window) is on that slow clock.  A host that
/// incremented <c>[0xF0C8]</c> per frame would run the whole AI 51× too fast.
/// </para>
/// <para>
/// <c>[0xEDAE]</c>, the per-node dt the manoeuvring geometry divides by, is
/// <c>[0xF0D2] − [0xED5D]</c> — so a host that never advances the frame time hands
/// <c>ArcDecay</c> a zero divisor and the port faults where the 8086 would <c>#DE</c>
/// (<c>CombatKernelFixtures.DeclaredResiduals</c>'s second entry, from the other direction).
/// </para>
/// <para>
/// The port supplies <c>dt</c> from its own clock rather than from the BIOS tick delta the original
/// measures (<c>[0xF0FA] − [0xF0F6]</c>, scaled by the time-compression shift <c>[0xF104]</c> and
/// clamped to <c>[1, 0x80]</c>): the determinism fixes the step, which is exactly what that clamp
/// exists to approximate.
/// </para>
/// </remarks>
public static class SceneFrameTimer
{
    /// <summary><c>g_master_frame_counter [0xF0C8]</c>.</summary>
    public const int MasterFrameCounter = 0xF0C8;

    /// <summary><c>g_frame_time_seconds [0xF0D0]</c> — the frame time arithmetic-shifted right 6.</summary>
    public const int FrameTimeShifted = 0xF0D0;

    /// <summary><c>g_frame_time [0xF0D2]</c> — the 32-bit frame-time accumulator.</summary>
    public const int FrameTime = 0xF0D2;

    /// <summary><c>g_scene_frame_dt_scaled [0xF11C]</c>.</summary>
    public const int FrameDt = 0xF11C;

    /// <summary>The smallest dt the original accepts (<c>cmp si,1 / jge</c> @<c>image@0x0C140</c>).</summary>
    public const int MinimumDt = 1;

    /// <summary>The largest (<c>cmp si,0x80 / jle</c> @<c>image@0x0C148</c>).</summary>
    public const int MaximumDt = 0x80;

    /// <summary>Advances the scene clock by one frame.</summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="dt">The frame's dt in ticks; clamped to <c>[1, 0x80]</c> as the original does.</param>
    /// <returns>The dt actually used.</returns>
    public static ushort Advance(CombatRegisters registers, int dt)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ushort clamped = (ushort)Math.Clamp(dt, MinimumDt, MaximumDt);

        int lo = registers.Word(FrameTime);
        int hi = registers.Word(FrameTime + 2);
        uint frameTime = unchecked((uint)((hi << 16) | lo)) + clamped;   // image@0x0C154
        registers.SetWord(FrameTime, unchecked((ushort)frameTime));
        registers.SetWord(FrameTime + 2, unchecked((ushort)(frameTime >> 16)));

        // image@0x0C15C — `mov ax,[0xF0D3]`, the UNALIGNED word one byte into the accumulator.
        registers.SetWord(MasterFrameCounter, unchecked((ushort)(frameTime >> 8)));
        registers.SetWord(FrameTimeShifted, unchecked((ushort)(frameTime >> 6)));  // image@0x0C170
        registers.SetWord(FrameDt, clamped);                                       // image@0x0C180
        return clamped;
    }
}
