using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// The two envelope PROBES the control integrator drives: the per-frame load-factor range scan and
/// the one-shot stall test.
/// </summary>
/// <remarks>
/// <para>
/// Field class: <b>INT-only</b>.  Both probes drive the stall / ejection state machine in
/// <c>aircraft_joystick_integrate_active @image@0x2B268</c>, so they are reproducible spine.
/// </para>
/// <para>
/// <b>These two have no reference implementation</b> (they stay as the L-range / L-stall
/// landings), so their source of truth is the bytes: every branch below cites the instruction that
/// decides it, from a disassembly of <c>[0x2B12A..0x2B268)</c>.
/// </para>
/// <para>
/// <b>Both probes borrow the aircraft's pitch accumulator and give it back.</b>  The original writes
/// a candidate load factor into master <c>+0xD6..+0xD9</c> (the
/// <see cref="Aircraft.PitchAxis"/> block's <see cref="ControlAxisState.Current"/>), calls the
/// envelope evaluator, and restores the saved value before returning — so a probe is observationally
/// pure on that field.  The port reproduces the save/restore rather than short-cutting it, because
/// the evaluator reads the field through the aircraft, exactly as the original reads it through the
/// master pointer.
/// </para>
/// <para>
/// <b>The original reaches its inputs through globals; the port takes them as parameters.</b>  The
/// scanner reads <c>g_active_aircraft_master_ptr [0xF1BC]</c> (four times — before, inside and after
/// its loop), <c>g_master_frame_counter [0xF0C8]</c> and <c>g_briefing_difficulty_idx [0xF10E]</c>.
/// The master pointer is the aircraft argument (an earlier pass measured the chain player-only: one distinct
/// master on ~45,000 flight frames), the frame counter comes from the port's tick clock and the
/// difficulty is an explicit flag — never a global read (brief, deliverable 2).
/// </para>
/// </remarks>
public static class AoaProbes
{
    /// <summary>
    /// The load factor <c>aoa_stall_probe</c> clamps a too-high pitch accumulator to: <c>0x200</c>
    /// Q8.8, i.e. +2 G (<c>mov word [bx+0xd6],0x200</c> @<c>image@0x2B221</c>).
    /// </summary>
    public const int StallProbeClampQ8 = 0x200;

    /// <summary>
    /// The floor <c>aoa_range_scanner</c> forces on its upper result: 2 G
    /// (<c>cmp word [bx+0xea],2 ; jge</c> @<c>image@0x2B1C8</c>).
    /// </summary>
    public const short ValidRangeMaximumFloor = 2;

    /// <summary>
    /// The ceiling <c>aoa_range_scanner</c> forces on its lower result: 0 G
    /// (<c>cmp word [bx+0xe8],0 ; jle</c> @<c>image@0x2B1BB</c>).
    /// </summary>
    public const short ValidRangeMinimumCeiling = 0;

    /// <summary>
    /// <c>aoa_range_scanner @image@0x2B12A</c> — sweeps every integer load factor the aircraft's
    /// pitch bounds allow, asks the envelope about each, and records the lowest and highest that came
    /// back "in envelope" into <see cref="Aircraft.ValidLoadFactorMin"/> /
    /// <see cref="Aircraft.ValidLoadFactorMax"/>.
    /// </summary>
    /// <param name="aircraft">
    /// The aircraft.  Reads <c>+0xDE</c>/<c>+0xE0</c> (the sweep bounds) and <c>+0xE6</c> (the cache
    /// sentinel); writes <c>+0xE6</c>, <c>+0xE8</c>, <c>+0xEA</c>; borrows and restores
    /// <c>+0xD6..+0xD9</c>.
    /// </param>
    /// <param name="altitudeQ8Feet">The player world object's <c>pos_y</c>, Q8 feet.</param>
    /// <param name="frameCounter">
    /// <c>g_master_frame_counter [0xF0C8]</c> — the per-frame cache key.  In the port this comes from
    /// the tick clock; it is NOT the simulation step (an earlier pass measured the scan doing real work on only
    /// 385 of 19,684 flight frames on the 060418 reference trace, because the frame counter advances
    /// more slowly than the det step).
    /// </param>
    /// <param name="easyDifficulty">
    /// <see langword="true"/> ⇔ the original's <c>g_briefing_difficulty_idx [0xF10E] == 0</c>
    /// (<c>cmp byte [0xf10e],0</c> @<c>image@0x2B1D5</c>): the easiest setting widens the result by
    /// one step in each direction, giving the player a one-G grace margin before the stall probe
    /// fires.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the sweep actually ran, <see langword="false"/> when the per-frame
    /// cache guard short-circuited it.  Report-only — the original returns <c>void</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Phase 1, the cache guard (<c>cmp word [bx+0xe6],cx ; jne</c> @<c>image@0x2B139</c>): when the
    /// sentinel already equals the frame counter the function returns having written nothing.
    /// <c>aircraft_pose_set</c> stamps <c>0xFFFF</c> there on every teleport (<c>image@0x2A3F1</c>),
    /// which forces a fresh scan on the next frame.
    /// </para>
    /// <para>
    /// Phase 3, the sweep, writes each candidate through the MSC unaligned-<c>i16</c> store idiom
    /// (<c>mov ax,si ; cwd ; mov dh,dl ; mov dl,ah ; mov ah,al ; sub al,al</c>,
    /// <c>image@0x2B168..0x2B171</c>, stored as two words at <c>+0xD6</c>/<c>+0xD8</c>).  Composed,
    /// those four bytes are exactly <c>candidate &lt;&lt; 8</c> — the Q8.8 load factor with a zero
    /// fraction — so the port assigns the accumulator directly and the fraction byte at
    /// <c>+0xD6</c> stays zero the way the <c>sub al,al</c> leaves it.
    /// </para>
    /// <para>
    /// Every compare in the sweep, the clamps and the difficulty widening is SIGNED
    /// (<c>jg</c> @<c>image@0x2B166</c>, <c>jge</c> @<c>image@0x2B196</c>/<c>0x2B1AB</c>,
    /// <c>jle</c> @<c>image@0x2B1A0</c>/<c>0x2B1C0</c>) — load factors run −4..+9.
    /// </para>
    /// </remarks>
    public static bool ScanValidLoadFactorRange(
        Aircraft aircraft,
        int altitudeQ8Feet,
        ushort frameCounter,
        bool easyDifficulty)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        // Phase 1 — image@0x2B131..0x2B13F: the per-frame cache guard.
        if (unchecked((ushort)aircraft.ResetSentinel) == frameCounter)
        {
            return false;
        }

        aircraft.ResetSentinel = unchecked((short)frameCounter);   // image@0x2B142

        // Phase 2 — image@0x2B146..0x2B15A: save the pitch accumulator, zero both outputs.
        int savedPitch = aircraft.PitchAxis.Current;
        aircraft.ValidLoadFactorMin = 0;
        aircraft.ValidLoadFactorMax = 0;

        // Phase 3 — image@0x2B15E..0x2B1AB: sweep min..max inclusive.
        short candidate = aircraft.MinLoadFactorG;                 // +0xE0
        if (candidate <= aircraft.MaxLoadFactorG)                  // image@0x2B162 cmp si,[bx+0xde]; jg
        {
            while (true)
            {
                aircraft.PitchAxis.Current = candidate << 8;       // image@0x2B177/0x2B17B
                if (FlightEnvelopeQueries.EvaluateStatus(aircraft, altitudeQ8Feet).Status == 0)
                {
                    // image@0x2B192 / image@0x2B19C — signed max/min tracking.
                    if (aircraft.ValidLoadFactorMax < candidate)
                    {
                        aircraft.ValidLoadFactorMax = candidate;
                    }

                    if (aircraft.ValidLoadFactorMin > candidate)
                    {
                        aircraft.ValidLoadFactorMin = candidate;
                    }
                }

                candidate++;                                        // image@0x2B1A6 inc si
                if (aircraft.MaxLoadFactorG < candidate)
                {
                    break;                                          // image@0x2B1AB jge not taken
                }
            }
        }

        // Phase 4 — image@0x2B1AD..0x2B1CF: restore, then clamp.
        aircraft.PitchAxis.Current = savedPitch;
        if (aircraft.ValidLoadFactorMin > ValidRangeMinimumCeiling)
        {
            aircraft.ValidLoadFactorMin = ValidRangeMinimumCeiling;
        }

        if (aircraft.ValidLoadFactorMax < ValidRangeMaximumFloor)
        {
            aircraft.ValidLoadFactorMax = ValidRangeMaximumFloor;
        }

        // Phase 5 — image@0x2B1D5..0x2B1F4: easy mode widens by one step, capped at the sweep bounds.
        if (easyDifficulty)
        {
            if (aircraft.MinLoadFactorG < aircraft.ValidLoadFactorMin)
            {
                aircraft.ValidLoadFactorMin--;                      // image@0x2B1E6 dec
            }

            if (aircraft.ValidLoadFactorMax < aircraft.MaxLoadFactorG)
            {
                aircraft.ValidLoadFactorMax++;                      // image@0x2B1F4 inc
            }
        }

        return true;
    }

    /// <summary><c>aoa_range_scanner</c> over the player's world object.</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="playerObject">The player's scene object (its <c>Y</c> is the altitude).</param>
    /// <param name="frameCounter">The per-frame cache key.</param>
    /// <param name="easyDifficulty">The easiest briefing setting.</param>
    /// <returns>Whether the sweep ran.</returns>
    public static bool ScanValidLoadFactorRange(
        Aircraft aircraft, WorldObject playerObject, ushort frameCounter, bool easyDifficulty)
    {
        ArgumentNullException.ThrowIfNull(playerObject);
        return ScanValidLoadFactorRange(aircraft, playerObject.Y, frameCounter, easyDifficulty);
    }

    /// <summary>
    /// <c>aoa_stall_probe @image@0x2B1FE</c> — is the aircraft stalled?  Clamps the pitch accumulator
    /// into <c>[0, +2 G]</c>, asks the envelope, restores the accumulator, and reports whether the
    /// answer was <see cref="EnvelopeStatusCode.StallOrNoCurve"/>.
    /// </summary>
    /// <param name="aircraft">The aircraft; <c>+0xD6..+0xD9</c> is borrowed and restored.</param>
    /// <param name="altitudeQ8Feet">The player world object's <c>pos_y</c>, Q8 feet.</param>
    /// <returns><see langword="true"/> iff the envelope returned status 1.</returns>
    /// <remarks>
    /// <para>
    /// Misnomer, corrected P529 — there is no joystick read, no port <c>0x201</c> and no button state
    /// anywhere in the 106 bytes; the function is an envelope stall probe.  Its three call sites are
    /// all inside <c>aircraft_joystick_integrate_active</c> (<c>image@0x2B43A</c>,
    /// <c>image@0x2B45B</c>, <c>image@0x2B4A4</c> — note THREE, not two).
    /// </para>
    /// <para>
    /// <b>The clamp is asymmetric and word-wise.</b>  The original tests the two halves of the
    /// accumulator separately: <c>or dx,dx ; jl</c> @<c>image@0x2B216</c> skips the upper clamp when
    /// the high word is negative; <c>jg</c> @<c>image@0x2B21A</c> clamps unconditionally when it is
    /// positive; only when it is zero does <c>cmp ax,0x200 ; jbe</c> @<c>image@0x2B21C</c> decide, and
    /// that low-word compare is UNSIGNED.  Then a second gate re-reads the (possibly just-written)
    /// high word and zeroes both halves when it is negative (<c>image@0x2B22D..0x2B23A</c>).
    /// Composed over the whole <c>i32</c> that is exactly "clamp to <c>[0, 0x200]</c>", because the
    /// upper clamp always leaves a non-negative value — the port keeps the word-wise form so the
    /// equivalence stays visible rather than assumed.
    /// </para>
    /// <para>
    /// Status 2 and 3 are NOT stalls: only <c>cmp byte [bp-5],1</c> @<c>image@0x2B257</c> produces a
    /// 1.
    /// </para>
    /// </remarks>
    public static bool StallProbe(Aircraft aircraft, int altitudeQ8Feet)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        // image@0x2B208..0x2B213: save both halves of the accumulator.
        int saved = aircraft.PitchAxis.Current;
        ushort low = unchecked((ushort)saved);
        short high = unchecked((short)(saved >> 16));

        // image@0x2B216..0x2B227: the upper clamp.
        int clamped = saved;
        if (high >= 0 && (high > 0 || low > StallProbeClampQ8))
        {
            clamped = StallProbeClampQ8;
        }

        // image@0x2B22D..0x2B23A: the negative gate, re-reading the CURRENT high word.
        if (unchecked((short)(clamped >> 16)) < 0)
        {
            clamped = 0;
        }

        aircraft.PitchAxis.Current = clamped;
        byte status = FlightEnvelopeQueries.EvaluateStatus(aircraft, altitudeQ8Feet).Status;

        // image@0x2B245..0x2B253: restore before mapping the result.
        aircraft.PitchAxis.Current = saved;

        return status == (byte)EnvelopeStatusCode.StallOrNoCurve;   // image@0x2B257
    }

    /// <summary><c>aoa_stall_probe</c> over the player's world object.</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="playerObject">The player's scene object (its <c>Y</c> is the altitude).</param>
    /// <returns><see langword="true"/> iff the envelope returned status 1.</returns>
    public static bool StallProbe(Aircraft aircraft, WorldObject playerObject)
    {
        ArgumentNullException.ThrowIfNull(playerObject);
        return StallProbe(aircraft, playerObject.Y);
    }
}
