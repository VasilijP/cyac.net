namespace CYAC.Port.Audio;

/// <summary>
/// The laws that place a sound in the world: the original's own distance attenuation, the port's
/// honest continuation of it past the range the original stops at, and a stereo pan.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is the original's and what is not.</b> The 1991 game attenuates an engine by distance in
/// exactly ONE view — the fly-by camera (<c>[0xC320] == 0x0D</c>, <c>image@0x29CE5</c>), where
/// <c>engine_sound_volume_compute @image@0x29C9E</c> subtracts <c>40 × range / 5000</c> from the
/// tone's volume argument and saturates that deduction at 40 (<c>image@0x29D0D</c> /
/// <c>image@0x29D1C</c>).  <see cref="ContinuousChannels.EngineVolume"/> is that function, and this
/// class does not re-implement it: <see cref="ExtraGainBeyondDriverRange"/> is what the port adds
/// BEYOND it, and <see cref="Pan"/> is a port-side stereo placement the original — mono, one
/// sounding object — has no equivalent of at all.  Both are labelled deviations.
/// </para>
/// <para>
/// <b>Why the continuation is a straight line.</b>  The driver's volume argument <c>v</c> becomes
/// <c>amp_base = v &lt;&lt; 6</c> and then the OPL attenuation <c>(amp ^ 0xFFF) &gt;&gt; 6</c>
/// (drv <c>@0x1911</c> for tone 0x03; <see cref="AdlToneEngine"/>), i.e. the total-level field
/// <c>TL = 63 − v</c>.  One TL step is 0.75 dB, so the original's own 40-step deduction over
/// <see cref="FlyByFullRangeFeet"/> IS a linear −30 dB ramp over 5,000 ft.  The port keeps that ramp
/// going at the same slope past 5,000 ft as a host-side gain, because the driver argument has
/// nowhere left to go — the tone's volume stays exactly the original's function of range, and the
/// deviation is one multiply.  At the audible radius the two together are ≈ −60 dB.
/// </para>
/// <para>
/// <b>Units.</b>  Ranges are what <c>object_range_from_view_anchor @image@0x24448</c> returns: a
/// saturated Manhattan sum in WORLD units, and a world unit is a FOOT (the cloud deck's own
/// <c>playerAltitudeFeet = pos_y &gt;&gt; 8</c>, <c>CYAC.Port.Core.Model.World.CloudDeck</c>).
/// </para>
/// </remarks>
public static class PositionalAudio
{
    /// <summary>
    /// The range over which the original's own deduction runs, in feet —
    /// <c>cmp ax,0x1388</c> @<c>image@0x29D05</c>.
    /// </summary>
    public const int FlyByFullRangeFeet = 0x1388;

    /// <summary>The deduction it reaches there, in driver volume steps (0.75 dB each).</summary>
    public const int FlyByMaxDeduction = 0x28;

    /// <summary>Decibels per driver volume step — one OPL total-level step.</summary>
    /// <remarks>
    /// The generator turns <c>v</c> into <c>TL = 63 − v</c> (<see cref="AdlToneEngine"/>'s
    /// <c>amp_base = v &lt;&lt; 6</c> and <c>(amp ^ 0xFFF) &gt;&gt; 6</c>), and the OPL2's TL field
    /// model reproduces).
    /// </remarks>
    public const double DecibelsPerVolumeStep = 0.75;

    /// <summary>
    /// How far a positional source may be and still be mixed, in feet.
    /// </summary>
    /// <remarks>
    /// Not invented: it is the original's own audible radius for a positional sound
    /// <c>sfx_object_impact_dispatch @image@0x29BA3</c> drops an impact entirely at
    /// <c>cmp si,0x2710</c> (<c>image@0x29BB5</c>).  The engine of an aeroplane 10,000 ft away is
    /// −60 dB down by <see cref="ExtraGainBeyondDriverRange"/> anyway, so the radius costs nothing
    /// audible and bounds the work.
    /// </remarks>
    public const int AudibleRadiusFeet = 0x2710;

    /// <summary>
    /// DEVIATION — the host-side gain that continues the original's ramp past
    /// <see cref="FlyByFullRangeFeet"/>.
    /// </summary>
    /// <param name="rangeFeet">The view anchor's range to the source.</param>
    /// <returns>A linear gain, 1.0 inside the original's own range.</returns>
    /// <remarks>
    /// Inside 5,000 ft this is exactly 1.0 and the whole attenuation is the original's own volume
    /// argument.  Outside it, the deduction the original saturates at 40 is allowed to keep growing
    /// — <c>40 × range / 5000 − 40</c> further steps of 0.75 dB — and those steps become this gain,
    /// because the tone's own argument cannot express them.
    /// </remarks>
    public static double ExtraGainBeyondDriverRange(int rangeFeet)
    {
        if (rangeFeet <= FlyByFullRangeFeet)
        {
            return 1.0;
        }

        double steps =
            ((double)FlyByMaxDeduction * rangeFeet / FlyByFullRangeFeet) - FlyByMaxDeduction;
        return Math.Pow(10.0, -DecibelsPerVolumeStep * steps / 20.0);
    }

    /// <summary>
    /// DEVIATION — the original's own deduction applied to a FIXED tone volume (the gun).
    /// </summary>
    /// <param name="baseVolume">The tone's own volume argument (the gun's is <c>weapon[+0x2B]</c>).</param>
    /// <param name="rangeFeet">The range to the source.</param>
    /// <returns>The volume argument to dispatch, never below zero.</returns>
    /// <remarks>
    /// The shipped game never attenuates a gun by distance — it plays the AI's gun at the weapon
    /// record's own volume, and only when the shooter is the player's SELECTED object
    /// (<c>sfx_selected_object_audio_dispatch @image@0x29AA8</c>).  A port that mixes every bandit
    /// positionally has to, so it uses the SAME curve the original uses for the fly-by engine:
    /// <c>−min(40, 40 × range / 5000)</c> (<c>image@0x29D0D</c> / <c>image@0x29D1C</c>), with
    /// <see cref="ExtraGainBeyondDriverRange"/> carrying the rest.
    /// </remarks>
    public static int AttenuateVolume(int baseVolume, int rangeFeet)
    {
        int deduction = rangeFeet >= FlyByFullRangeFeet
            ? FlyByMaxDeduction
            : FlyByMaxDeduction * rangeFeet / FlyByFullRangeFeet;
        return Math.Max(0, baseVolume - deduction);
    }

    /// <summary>
    /// DEVIATION — where the source sits between the listener's ears.
    /// </summary>
    /// <param name="deltaX">Source minus listener, world X.</param>
    /// <param name="deltaY">…Y.</param>
    /// <param name="deltaZ">…Z.</param>
    /// <param name="rightX">The camera basis' RIGHT vector, world X.</param>
    /// <param name="rightY">…Y.</param>
    /// <param name="rightZ">…Z.</param>
    /// <returns>−1 hard left, 0 centred, +1 hard right.</returns>
    /// <remarks>
    /// The cosine between the source direction and the camera's own right vector
    /// (<c>CYAC.Port.Render.CameraPose.Right</c>, which the host passes in as three doubles — the
    /// audio assembly may not reference the renderer).  The original is MONO: it has one sounding
    /// object and no pan at all, so every part of this is the port's.
    /// </remarks>
    public static double Pan(
        double deltaX, double deltaY, double deltaZ, double rightX, double rightY, double rightZ)
    {
        double length = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ));
        if (length <= 0)
        {
            return 0;
        }

        double dot = ((deltaX * rightX) + (deltaY * rightY) + (deltaZ * rightZ)) / length;
        return Math.Clamp(dot, -1.0, 1.0);
    }

    /// <summary>
    /// The constant-power pan law, normalised so a CENTRED source is unity in both ears.
    /// </summary>
    /// <param name="pan">−1..+1 from <see cref="Pan"/>.</param>
    /// <returns>The two channel gains.</returns>
    /// <remarks>
    /// <c>cos/sin</c> of the quarter-turn, times <c>√2</c> so that <c>pan = 0</c> gives
    /// <c>(1, 1)</c> — which is what keeps the player's own engine, heard from inside the cockpit
    /// where the pan is zero by construction, at exactly the level H11 rendered it at.
    /// </remarks>
    public static (double Left, double Right) ChannelGains(double pan)
    {
        double angle = (Math.Clamp(pan, -1.0, 1.0) + 1.0) * (Math.PI / 4.0);
        const double Normalise = 1.4142135623730951;   // √2
        return (Math.Cos(angle) * Normalise, Math.Sin(angle) * Normalise);
    }

    /// <summary>
    /// The one-pole low-pass coefficient for a cutoff at <paramref name="cutoffHz"/>.
    /// </summary>
    /// <param name="cutoffHz">The cutoff, or 0 / negative for "no filter".</param>
    /// <returns>The coefficient <c>a</c> of <c>y += a·(x − y)</c>; 1.0 means "pass through".</returns>
    public static double LowPassCoefficient(double cutoffHz) =>
        cutoffHz <= 0 || cutoffHz >= AudioFormat.SampleRate / 2.0
            ? 1.0
            : 1.0 - Math.Exp(-2.0 * Math.PI * cutoffHz / AudioFormat.SampleRate);
}
