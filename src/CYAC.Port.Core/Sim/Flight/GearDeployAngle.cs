using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// <c>g_gear_deploy_angle_bam [0xEF96]</c> — the landing gear's RETRACTION angle, and the two
/// functions that own it.
/// </summary>
/// <remarks>
/// <para>
/// INT-only simulation state, not render state: the original steps it inside
/// <c>flight_engine_per_frame_top @image@0x227C7</c> phase 4 (<c>lcall</c> @<c>image@0x2294F</c>),
/// once per flight frame, and the renderer only reads it.  Source of truth:, re-read against Capstone
/// over <c>image@0x22A56..0x22AA3</c>.
/// </para>
/// <para>
/// <b>The sense.</b>  <c>flight_gear_angle_reset @image@0x22A56</c> writes 0 at aircraft load, and
/// the Test Flight's cold start is parked with the gear DOWN (H2 §D), so <b>0 is EXTENDED and
/// <c>0x2D0</c> (90°) is RETRACTED</b> — the scanner's name reads backwards.  The step function
/// negates its delta when the gear bit is set (<c>image@0x22A78</c>: <c>test [0xF0BC],4 / neg si</c>),
/// which under that reading means "gear-down commanded ⇒ walk the angle back to 0".  Consistent, and
/// it closes D1 of the decoded file ("which sense of the bit means extending vs retracting is NOT
/// resolved here").
/// </para>
/// <para>
/// <c>[0xF0BC]</c> bit 2 IS the gear bit — <c>image@0x31341</c> sets it from
/// <c>g_film_gear_state [0xC06E]</c>, and it sits at the same bit index as
/// <c>master[+0x124] &amp; 0x04</c> = <see cref="AircraftStatusFlags.LandingGear"/>, which is what the
/// port drives it from.
/// </para>
/// </remarks>
public static class GearDeployAngle
{
    /// <summary>Fully extended: <c>[0xEF96] = 0</c>, what <c>image@0x22A56</c> writes at load.</summary>
    public const int Extended = 0;

    /// <summary>Fully retracted: <c>0x2D0</c> = 720 BAM = 90° (the clamp at <c>image@0x22A85</c>).</summary>
    public const int Retracted = 0x2D0;

    /// <summary>
    /// The ramp rate: <c>0x168</c> = 360 BAM = <b>45° per second</b> (<c>image@0x22A6A</c>), scaled by
    /// the frame's <c>g_scene_frame_dt_scaled [0xF11C]</c> through
    /// <c>muldiv16_signed_shr8 @image@0x11980</c>.
    /// </summary>
    public const int RateBamPerSecond = 0x168;

    /// <summary>
    /// <c>flight_gear_angle_reset @image@0x22A56</c> — the value the angle takes at aircraft load.
    /// </summary>
    public static int Reset() => Extended;

    /// <summary>
    /// <c>flight_gear_angle_step @image@0x22A5D</c> — one frame of the gear animation.
    /// </summary>
    /// <param name="angleBam">The angle as it stands.</param>
    /// <param name="frameDeltaScaled">
    /// The frame's <c>g_scene_frame_dt_scaled [0xF11C]</c> — <see cref="TickClock.Dt"/> in the port.
    /// </param>
    /// <param name="gearDownCommanded">
    /// <c>[0xF0BC] &amp; 4</c> — the gear bit, i.e. <see cref="AircraftStatusFlags.LandingGear"/>.
    /// </param>
    /// <param name="previewMode">
    /// <c>[0xF28A]</c> — the MODEL-PREVIEW flag, misnomer.  Byte-exhaustive writer census (F28A
    /// abs</c>): set to 1 by <c>ui_aircraft_stats_panel</c>'s 3-D view (<c>image@0x269C7</c>) and by
    /// the film-review screen (<c>image@0x32936</c>); cleared by <c>flight_session_start</c>
    /// (<c>image@0x3026E</c>, <c>0x3031F</c>), <c>session_init_dispatcher_b</c> (<c>0x3254A</c>) and
    /// the film-review screen's exit (<c>0x334E4</c>).  Non-zero makes the step the WHOLE range in one
    /// frame (<c>image@0x22A65</c>: <c>mov si,0x2D0</c>) — the rotating showroom model snaps its
    /// wheels. Wrong: in a flight session the flag is 0, so an AIRBORNE start —
    /// <c>aircraft_pose_set</c>'s <c>and [si+0x124],0xFB</c> @<c>image@0x2A2F7</c> commands the gear
    /// UP while <c>flight_gear_angle_reset</c> has just put the angle at 0 = EXTENDED — RETRACTS the
    /// gear over the first 2 s (0x2D0 / 0x168 per second), exactly what the MiG-15 does
    /// at mission start in DOSBox.
    /// </param>
    /// <returns>The new angle, clamped to <c>[0, 0x2D0]</c>.</returns>
    public static int Step(
        int angleBam, int frameDeltaScaled, bool gearDownCommanded, bool previewMode)
    {
        int delta = previewMode
            ? Retracted
            : Fixed.MulDiv16SignedShr8((short)RateBamPerSecond, (short)frameDeltaScaled);

        if (gearDownCommanded)
        {
            delta = -delta;
        }

        int next = unchecked((short)(angleBam + delta));
        return next > Retracted ? Retracted : next < Extended ? Extended : next;
    }

    /// <summary>How far through the retraction the gear is, 0 (down) … 1 (up).</summary>
    /// <param name="angleBam">The angle.</param>
    public static double Fraction(int angleBam) =>
        Math.Clamp(angleBam / (double)Retracted, 0.0, 1.0);
}
