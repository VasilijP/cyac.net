using CYAC.Port.Core.Model.Flight;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// The per-frame driver's own inline arithmetic — stage S1 → S2 and its S6 → S7 undo.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  These twelve instructions live in <c>aircraft_per_frame_update</c>'s body rather than
/// in a callee (<c>image@0x2A6BA..0x2A6F5</c> and <c>image@0x2A732..0x2A743</c>), so they belong to
/// no decoded function;
/// </para>
/// <para>
/// <b>It is a transient, and that is the whole point.</b> The driver SAVES the accumulator pair, adds
/// into it, derives <see cref="Aircraft.MassDiv32"/> from the sum, and then puts the saved value back
/// before returning — so <see cref="Aircraft.MassAccumulator"/> is unchanged across a frame and only
/// <c>+0xD4</c> survives.  K0's census measures exactly that: S1→S2 moves <c>+0xBC..+0xBD</c> on
/// every frame and S6→S7 moves it back.
/// </para>
/// <para>
/// CLOSED — the numbers this stage verifies against support the MASS reading and refute the heading
/// one: <c>+0xBC</c> holds <see cref="AircraftDefinition.GrossWeightLb"/> at load, the addend is
/// <see cref="Aircraft.HeadingAuthority"/> = live fuel ÷ 256, and the survivor is <c>(weight + fuel)
/// ÷ 32</c>, which predicted <c>+0xD4</c> to the unit on all three aircraft.  The scanner names were
/// <c>heading_accum_lo/hi_i16</c> and <c>heading_scaled_i16</c>; they are now
/// <c>mass_accum_lo/hi_i16</c> and <c>mass_div32_i16</c>, **RENAMED R1**, and this class is
/// <c>MassAccumulatorStage</c> (ex-<c>HeadingAccumulatorStage</c>).
/// </para>
/// </remarks>
public static class MassAccumulatorStage
{
    /// <summary>
    /// The right shift the driver applies to the summed accumulator: 5
    /// (five <c>sar dx,1 ; rcr ax,1</c> pairs, <c>image@0x2A6E1..0x2A6F4</c>).
    /// </summary>
    public const int Shift = 5;

    /// <summary>
    /// Runs S1 → S2: save the accumulator, add the per-frame authority, derive
    /// <see cref="Aircraft.MassDiv32"/>.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <returns>
    /// The saved accumulator (the original's <c>[bp-4]/[bp-2]</c>), to hand back to
    /// <see cref="Restore"/> at S6 → S7.
    /// </returns>
    /// <remarks>
    /// The addend is <c>mov ax,[bx+0xc1] ; cdq</c> (<c>image@0x2A6CC</c>) — a WORD read at an ODD
    /// offset inside <c>fuel_remaining_i32</c>, sign-extended by a 16-bit <c>CWD</c>.  The sum is a
    /// 32-bit <c>add</c>/<c>adc</c> that wraps, and only the LOW word of the shifted sum is stored
    /// (<c>mov [bx+0xd4],ax</c> @<c>image@0x2A6F5</c>).
    /// </remarks>
    public static int Run(Aircraft aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        int saved = aircraft.MassAccumulator;                       // image@0x2A6BE..0x2A6C9
        int sum = unchecked(saved + aircraft.HeadingAuthority);        // image@0x2A6CC..0x2A6D8
        aircraft.MassAccumulator = sum;
        aircraft.MassDiv32 = unchecked((short)(sum >> Shift));     // image@0x2A6E1..0x2A6F5
        return saved;
    }

    /// <summary>Runs S6 → S7: put the saved accumulator back (<c>image@0x2A732..0x2A743</c>).</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="saved">What <see cref="Run"/> returned for this frame.</param>
    public static void Restore(Aircraft aircraft, int saved)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        aircraft.MassAccumulator = saved;
    }
}
