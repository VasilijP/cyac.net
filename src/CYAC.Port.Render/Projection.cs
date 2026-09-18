namespace CYAC.Port.Render;

/// <summary>A projected point: its pixel position and the reciprocal of its view-space depth.</summary>
/// <param name="X">Screen X in pixels, sub-pixel.</param>
/// <param name="Y">Screen Y in pixels, sub-pixel, growing downward.</param>
/// <param name="InvZ">1 / view-space Z.</param>
public readonly record struct ScreenPoint(double X, double Y, double InvZ);

/// <summary>
/// The perspective divide — the ONE piece of the pipeline that has a direct counterpart in the
/// original, and the reason the port's field of view is a measurement rather than a taste.
/// </summary>
/// <remarks>
/// <para>
/// The original's divide is the routine <c>gfx_trig_cache_setup @image@0x19524</c> EMITS at run time
/// into <c>runtime_projection_code_buf image@0x19366</c>; reassembled it is
/// </para>
/// <code>
/// screen_x = g_gfx_screen_centre_x [0xE634] + ((X &lt;&lt; S) / Z)
/// screen_y = g_gfx_screen_centre_y [0xE636] − ((Y &lt;&lt; S) / Z)
/// S        = g_gfx_zoom_shift [0xE836] = [0xD8A2] + [0xD8A0]
/// </code>
/// <para>
/// (<c>CYAC.Port.Core.Sim.Combat.Player.ObjectScreenProjection</c>, verified
/// 15,090 / 15,090 against the genuine machine.)  <c>X</c>, <c>Y</c> and <c>Z</c> are the same
/// camera-space units, so <c>2^S</c> IS the focal length in pixels — see
/// <see cref="CameraLens.DefaultHorizontalFovDegrees"/>.
/// </para>
/// <para>
/// What the port does NOT reproduce is the 1991 arithmetic: the 16-bit <c>IDIV</c> that is meant to
/// fault (its return addresses are stamped into <c>cs:[0x8FF4/6/2]</c> and
/// <c>int00_divzero_isr @image@0x191C8</c> saturates at ±0x7F00 — 71,084 fix-ups over the recorded sorties),
/// the <c>((v &lt;&lt; 8) | (v &amp; 0xFF))</c> shift-block quirk, and the emitted code itself.  Geometry
/// is <c>double</c> (renderer law D2).
/// </para>
/// </remarks>
public static class Projection
{
    /// <summary>Projects one view-space point.</summary>
    /// <param name="viewSpace">The point, in the camera's frame (X right, Y up, Z forward).</param>
    /// <param name="focalPixels">The focal length in pixels (<see cref="CameraLens.FocalLengthPixels"/>).</param>
    /// <param name="centreX">The viewport's centre column.</param>
    /// <param name="centreY">The viewport's centre row.</param>
    /// <returns>The projected point.</returns>
    /// <remarks>
    /// Y is SUBTRACTED, exactly as the emitted routine subtracts it from
    /// <c>g_gfx_screen_centre_y</c>: screen rows grow downward and world Y grows upward.
    /// </remarks>
    public static ScreenPoint ToScreen(Vec3 viewSpace, double focalPixels, double centreX, double centreY)
    {
        double invZ = 1.0 / viewSpace.Z;
        return new ScreenPoint(
            centreX + (viewSpace.X * focalPixels * invZ),
            centreY - (viewSpace.Y * focalPixels * invZ),
            invZ);
    }
}
