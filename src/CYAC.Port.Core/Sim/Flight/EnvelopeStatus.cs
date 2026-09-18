namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// The four status codes <c>fme_envelope_status_eval @image@0x2AB76</c> is designed to return.
/// </summary>
/// <remarks>
/// <para>
/// INT-only: the flight envelope's verdict gates the stall/ejection state machine, so it is part of
/// the reproducible spine.
/// </para>
/// <para>
/// <b>These four are not the whole codomain.</b>  One arm of the original returns a raw data byte
/// instead of a status — see <see cref="EnvelopeEvaluation.LowSpeedQuirkFired"/>.  That is why the
/// evaluation carries a <see cref="byte"/> <see cref="EnvelopeEvaluation.Status"/> and this enum is
/// only its named part.
/// </para>
/// </remarks>
public enum EnvelopeStatusCode : byte
{
    /// <summary>
    /// 0 — inside the envelope.  Written by <c>sub al,al</c> at <c>image@0x2AC26</c> (the direct
    /// arm), <c>image@0x2A93C</c>/<c>image@0x2A9B4</c> (the altitude arm) and
    /// <c>image@0x2AA6C</c> (the low-speed arm).
    /// </summary>
    InEnvelope = 0,

    /// <summary>
    /// 1 — stall, or no envelope curve exists for the current load factor.  <c>mov al,1</c> at
    /// <c>image@0x2AB95</c> (no record) and at <c>image@0x2A8E2</c> (the speed-underrun arm).
    /// </summary>
    StallOrNoCurve = 1,

    /// <summary>
    /// 2 — above the altitude the envelope allows at this speed and load factor.  <c>mov al,2</c> at
    /// <c>image@0x2A962</c> and <c>image@0x2A9AF</c>.
    /// </summary>
    OverAltitudeLimit = 2,

    /// <summary>
    /// 3 — below the minimum speed the envelope allows.  <c>mov al,3</c> at <c>image@0x2AA48</c>.
    /// </summary>
    BelowMinimumSpeed = 3,
}

/// <summary>
/// Which of the five exit arms of <c>fme_envelope_status_eval @image@0x2AB76</c> produced a result.
/// </summary>
/// <remarks>
/// Report-only: the path never feeds a decision.  It exists so a verification run can show that a
/// recording exercised more than one arm — a trace in which only one path fires proves nothing
/// about the others.
/// </remarks>
public enum EnvelopePath
{
    /// <summary>No curve carries the load-factor key — <c>or dx,ax</c> ⇒ ZF, <c>image@0x2AB93</c>.</summary>
    NoCurve = 0,

    /// <summary>
    /// Airspeed below <c>points[peak].x</c> ⇒ <c>fme_speed_underrun_check @image@0x2A8C8</c>,
    /// entered at <c>image@0x2ABB5</c>.
    /// </summary>
    SpeedUnderrun = 1,

    /// <summary>
    /// ⇒ <c>fme_altitude_speed_check @image@0x2A8EC</c>, entered at <c>image@0x2AC01</c>.
    /// </summary>
    AltitudeCheck = 2,

    /// <summary>
    /// ⇒ <c>fme_low_speed_limit_check @image@0x2A9BE</c>, entered at <c>image@0x2AC20</c>.
    /// </summary>
    LowSpeedLimit = 3,

    /// <summary>
    /// The <c>jae</c> at <c>image@0x2AC19</c> skipped the low-speed call — <c>sub al,al</c> at
    /// <c>image@0x2AC26</c> returns 0 without any further work.
    /// </summary>
    Direct = 4,
}

/// <summary>
/// What one call to <c>fme_envelope_status_eval @image@0x2AB76</c> produced: the status byte it
/// returns in <c>AL</c>, plus the report-only census of how it got there.
/// </summary>
/// <param name="Status">
/// The <c>AL</c> byte.  Normally one of <see cref="EnvelopeStatusCode"/>, but see
/// <paramref name="LowSpeedQuirkFired"/>.
/// </param>
/// <param name="Path">Which arm ran.</param>
/// <param name="LowSpeedQuirkFired">
/// <see langword="true"/> when the low-speed arm's fast exit at <c>image@0x2AA17</c> fired, in which
/// case <paramref name="Status"/> is <b>not</b> a status code at all but the raw low byte of
/// <c>points[peak].x</c> — any value 0..255.  Reproduced exactly; see the quirk note on
/// <see cref="FlightEnvelopeQueries.EvaluateStatus"/>.
/// </param>
/// <param name="InterpolationRan">
/// <see langword="true"/> when <c>fme_linear_interp_x_from_y @image@0x2A74A</c> ran on this call
/// (the altitude and low-speed arms only).  Report-only.
/// </param>
public readonly record struct EnvelopeEvaluation(
    byte Status,
    EnvelopePath Path,
    bool LowSpeedQuirkFired,
    bool InterpolationRan)
{
    /// <summary>
    /// The status as a named code.  Only meaningful when <see cref="LowSpeedQuirkFired"/> is
    /// <see langword="false"/>; the quirk's raw byte is deliberately not forced into the enum.
    /// </summary>
    public EnvelopeStatusCode Code => (EnvelopeStatusCode)Status;

    /// <summary>True when the aircraft is inside its envelope (status 0) and the quirk did not fire.</summary>
    public bool IsInEnvelope => !LowSpeedQuirkFired && Status == (byte)EnvelopeStatusCode.InEnvelope;

    /// <summary>A short, stable description for test output and debugging.</summary>
    public override string ToString() =>
        LowSpeedQuirkFired
            ? $"quirk(raw 0x{Status:X2}) via {Path}"
            : $"{Code} via {Path}{(InterpolationRan ? " (interp)" : string.Empty)}";
}
