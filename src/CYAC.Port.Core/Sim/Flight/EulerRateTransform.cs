using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// <c>orientation_trig_helper @image@0x2BB98</c> — turns the three body-axis control rates into the
/// three Euler-angle rates the apply-velocity stage integrates.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  This is the classical Euler kinematic transform: two of the three outputs are divided
/// by <c>sin(pitch)</c>, and the ±80° clamp on the pitch argument (<c>image@0x2BBA3..0x2BBBC</c>) is
/// precisely the singularity guard — the only two zeros of <see cref="TrigTables.NavSinX4"/>
/// (<c>0x2D0</c> and <c>0x870</c>) lie strictly inside the clamped-out intervals, so the divisor is
/// at least <c>4 × table[80]</c> = 11,376 and the two divides can never fault.
/// <see cref="Certify"/>'s test asserts that.
/// </para>
/// <para>
/// The inputs are the three <c>s_ctrl_state_block</c> CURRENT slots at master <c>+0x30</c>
/// (<see cref="Aircraft.RollDeadAxis"/>), <c>+0x40</c> (<see cref="Aircraft.HeadingAoaAxis"/>) and
/// <c>+0x50</c> (<see cref="Aircraft.RollAliveAxis"/>) — the original is called with <c>BX = master +
/// 0x30</c> and addresses the other two as <c>[bx+0x10]</c> / <c>[bx+0x20]</c> (729).
/// </para>
/// <para>
/// Source of truth: the bytes at <c>image@0x2BB98..0x2BD25</c>.
/// </para>
/// </remarks>
public static class EulerRateTransform
{
    /// <summary>The pitch clamp's lower band edge: <c>0x280</c> = 80° (<c>image@0x2BBA3</c>).</summary>
    public const int PitchClampLow = 0x0280;

    /// <summary>The clamp's mid test: <c>0x870</c> = 270° (<c>image@0x2BBA9</c>).</summary>
    public const int PitchClampMid = 0x0870;

    /// <summary>The clamp's upper band edge: <c>0x8C0</c> = 280° (<c>image@0x2BBB4</c>).</summary>
    public const int PitchClampHigh = 0x08C0;

    /// <summary>
    /// The smallest magnitude <see cref="TrigTables.NavSinX4"/> can take on the clamped domain:
    /// <c>4 × 2844</c> = 11,376, at the band edges themselves.
    /// </summary>
    /// <remarks>
    /// <c>NavSin</c> is the <c>+0x2D0</c>-shifted fold, so <c>NavSin(0x280)</c> reads quarter-table
    /// index 80 (= 2844), NOT index 640.  <see cref="Certify"/> re-derives this by exhaustion and
    /// the unit test asserts the two agree — the divisor guard is checked, not asserted.
    /// </remarks>
    public const int MinimumDivisorMagnitude = 11376;

    /// <summary>The transform's three outputs, in the original's 12-byte output block order.</summary>
    /// <param name="Roll">
    /// <c>OUT[+0]</c> — the roll-axis rate.  Master <c>+0x60</c> integrates it
    /// (<c>image@0x2BDD8</c>).
    /// </param>
    /// <param name="Pitch">
    /// <c>OUT[+4]</c> — the pitch-axis rate.  Master <c>+0x70</c> integrates it
    /// (<c>image@0x2BDF5</c>), and it is the slot the wind-drift arm biases.
    /// </param>
    /// <param name="Heading">
    /// <c>OUT[+8]</c> — the heading-axis rate.  Master <c>+0x80</c> integrates it
    /// (<c>image@0x2BE67</c>).
    /// </param>
    /// <param name="ClampArm">
    /// Which arm of the pitch clamp ran: 0 = kept (<c>&lt;= 0x280</c>), 1 = clamped up to
    /// <c>0x280</c>, 2 = kept (<c>&gt;= 0x8C0</c>), 3 = clamped down to <c>0x8C0</c>.
    /// </param>
    /// <param name="ClampedPitch">The pitch angle after the clamp — the sine/cosine argument.</param>
    public readonly record struct EulerRates(int Roll, int Pitch, int Heading, int ClampArm, Angle ClampedPitch);

    /// <summary>Runs the transform.</summary>
    /// <param name="aircraft">The aircraft — reads the <c>+0x30</c>/<c>+0x40</c>/<c>+0x50</c> blocks only.</param>
    /// <param name="pitch">
    /// The pitch attitude, already wrapped by the caller (<c>angle_wrap_0_to_0xB40</c>
    /// @<c>image@0x2BDA7</c>).  Clamped here.
    /// </param>
    /// <param name="roll">
    /// The roll attitude, already wrapped (<c>image@0x2BD7D</c>).  The original's argument name in the verified
    /// lift is "yaw"; the bytes say it comes from master <c>+0x60</c>, which
    /// <c>aircraft_physics_apply_velocity</c> phase 8 writes to the world object's ROLL word
    /// (<c>image@0x2BF56</c>).
    /// </param>
    public static EulerRates Run(Aircraft aircraft, Angle pitch, Angle roll)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        // --- the ±80° clamp (image@0x2BBA0..0x2BBBC), all three compares SIGNED ------------------
        int raw = unchecked((short)pitch.Units);
        int clampArm;
        int clamped;
        if (raw <= PitchClampLow)
        {
            clampArm = 0;
            clamped = raw;
        }
        else if (raw < PitchClampMid)
        {
            clampArm = 1;
            clamped = PitchClampLow;
        }
        else if (raw >= PitchClampHigh)
        {
            clampArm = 2;
            clamped = raw;
        }
        else
        {
            clampArm = 3;
            clamped = PitchClampHigh;
        }

        Angle clampedPitch = new Angle(unchecked((ushort)clamped));
        int sinPitch = TrigTables.NavSinX4(clampedPitch);
        int cosPitch = TrigTables.NavCosX4(clampedPitch);
        int sinRoll = TrigTables.NavSinX4(roll);
        int cosRoll = TrigTables.NavCosX4(roll);

        // The two inputs are pre-shifted by the `mov dh,dl / mov dl,ah / mov ah,al / sub al,al` byte
        // shuffle at image@0x2BBC6 and image@0x2BC06 — algebraically a 32-bit `<< 8` with the TOP
        // BYTE DROPPED, which is why it is an unchecked shift and not a multiply.
        int rollAlive = ShiftLeft8(MasterBlocks.Current(aircraft, MasterBlock.RollAlive));   // [bx+0x20]
        int headingAoa = ShiftLeft8(MasterBlocks.Current(aircraft, MasterBlock.HeadingAoa)); // [bx+0x10]
        int rollDead = MasterBlocks.Current(aircraft, MasterBlock.RollDead);                 // [bx]

        // §1/§2 — the two Euler quotients (image@0x2BBF2 / image@0x2BC32).
        int quotient1 = Fixed.Div32SignedNormed(Fixed.Mul32Shr16(sinRoll, rollAlive), sinPitch);
        int quotient2 = Fixed.Div32SignedNormed(Fixed.Mul32Shr16(cosRoll, headingAoa), sinPitch);

        // §3 — OUT[+8] = (q2 >> 8) + (q1 >> 8), each shifted before the add (image@0x2BC49).
        int heading = unchecked((quotient2 >> 8) + (quotient1 >> 8));

        // §4/§5 — OUT[+0] = (q1·cos_pitch >> 8) + ((q2·cos_pitch >> 8) + master[+0x30]). The
        // second term's master add happens BEFORE the accumulate into OUT (image@0x2BCB9), so
        // the two 32-bit wraps are ordered exactly this way.
        int term1 = Fixed.Mul32Shr16(cosPitch, quotient1) >> 8;
        int term2 = unchecked((Fixed.Mul32Shr16(cosPitch, quotient2) >> 8) + rollDead);
        int rollRate = unchecked(term1 + term2);

        // §6/§7 — OUT[+4] = −(cos_roll·rollAlive >> 8) + (sin_roll·headingAoa >> 8).
        int pitchRate = unchecked(-(Fixed.Mul32Shr16(cosRoll, rollAlive) >> 8)
            + (Fixed.Mul32Shr16(sinRoll, headingAoa) >> 8));

        return new EulerRates(rollRate, pitchRate, heading, clampArm, clampedPitch);
    }

    /// <summary>
    /// The <c>mov dh,dl ; mov dl,ah ; mov ah,al ; sub al,al</c> byte shuffle: <c>DX:AX &lt;&lt;= 8</c>
    /// with the product's top byte discarded (<c>image@0x2BBC6</c>).
    /// </summary>
    /// <param name="value">The 32-bit value.</param>
    internal static int ShiftLeft8(int value) => unchecked(value << 8);

    /// <summary>
    /// The divisor guard the ±80° clamp provides, as a checkable statement: over the clamped domain
    /// <c>[0, 0x280] ∪ [0x8C0, 0xB40)</c> the sine never comes within
    /// <see cref="MinimumDivisorMagnitude"/> of zero.
    /// </summary>
    /// <returns>The smallest <c>|sin×4|</c> the clamp admits.</returns>
    public static int Certify()
    {
        int smallest = int.MaxValue;
        for (int units = 0; units < Angle.FullCircle; units++)
        {
            bool admitted = units <= PitchClampLow || units >= PitchClampHigh;
            if (!admitted)
            {
                continue;
            }

            int magnitude = Math.Abs(TrigTables.NavSinX4(new Angle((ushort)units)));
            if (magnitude < smallest)
            {
                smallest = magnitude;
            }
        }

        return smallest;
    }
}
