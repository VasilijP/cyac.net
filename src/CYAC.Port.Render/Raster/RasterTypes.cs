namespace CYAC.Port.Render.Raster;

/// <summary>One projected vertex: screen position and the reciprocal of its view-space depth.</summary>
/// <param name="X">Screen X in pixels (sub-pixel).</param>
/// <param name="Y">Screen Y in pixels (sub-pixel), growing downward.</param>
/// <param name="InvZ">1 / view-space Z — the quantity that interpolates linearly in screen space.</param>
internal readonly record struct ScreenVertex(double X, double Y, double InvZ);

/// <summary>
/// The depth function of one planar polygon, as an affine function of the pixel coordinates.
/// </summary>
/// <param name="A">Coefficient of <c>x</c>.</param>
/// <param name="B">Coefficient of <c>y</c>.</param>
/// <param name="C">Constant term.</param>
/// <remarks>
/// For a perspective camera, <c>1/z</c> over a PLANE is exactly affine in screen coordinates, so a
/// three-term evaluation per pixel is not an approximation — it is the closed form.  Derived from the
/// view-space plane <c>n·P = d</c> and the pixel's view ray <c>(u, v, f)</c>:
/// <c>1/z = (n_x·u + n_y·v + n_z·f) / (d·f)</c>.
/// </remarks>
internal readonly record struct DepthPlane(double A, double B, double C)
{
    /// <summary>Evaluates <c>1/z</c> at a pixel's centre.</summary>
    /// <param name="x">Pixel column.</param>
    /// <param name="y">Pixel row.</param>
    public double At(int x, int y) => (A * x) + (B * y) + C;
}

/// <summary>What a primitive paints: an already-encoded colour and how much of a pixel it covers.</summary>
/// <param name="Packed">The colour, in the target's own channel order.</param>
/// <param name="Coverage">
/// The primitive's TRANSLUCENCY alpha: the fraction of the pixels the ORIGINAL would have painted,
/// <c>popcount(record[+4]) / 8</c> (<see cref="CYAC.Port.Core.Model.World.MeshFace.Coverage"/>),
/// times the instance's own opacity.  1.0 for a solid record; 0.5 for the <c>0x5A</c>/<c>0xA5</c>
/// checkerboard the propeller discs, shadows and clouds use.  It is INDEPENDENT of the fragment's
/// area coverage, and it is the term <see cref="AlphaMode.Dither"/> quantises (R4).
/// </param>
/// <param name="Soft">
/// For a DISC only: fade the coverage radially, <c>1 − (d/R)²</c>, so the primitive reads as a puff instead
/// of a coin — smoke as soft translucent discs/billboards.  The original cannot do
/// this: it has one 4+4-bit screen mask per record and its own age-indexed dither table is the closest it
/// gets (<c>effect_particle_draw @image@0x03CD5</c> indexes <c>[0x0E16]</c> by the puff's age).  Represent,
/// don't reproduce.
/// </param>
/// <remarks>
/// All three retired: the blend-stamp buffer became <c>CombineRule.Max</c> in the fragment resolve (R3),
/// and the mask became the host-pitch ordered matrix (<see cref="OrderedDither"/>) which reads the record's
/// alpha and nothing else.
/// </remarks>
internal readonly record struct Paint(uint Packed, double Coverage = 1.0, bool Soft = false);

/// <summary>The port's one COMPOSITING LAW: <c>dst + (src − dst)·a</c>, per channel.</summary>
/// <remarks>
/// Promoted out of the retired <c>ScanRaster</c>, which is where this assembly's blend used to
/// live.  there is now exactly ONE producer of a translucent pixel, the fragment resolve
/// (<see cref="FragmentRaster"/>): that layer folded into the pipeline,.*
/// </remarks>
internal static class Blend
{
    /// <summary>
    /// Coverage at or above which a primitive is treated as OPAQUE — anything the shipped selectors
    /// can produce is either 1.0 or at most 0.5, so this only guards float noise.
    /// </summary>
    public const double OpaqueCoverage = 0.999;

    /// <summary>Per-channel <c>dst + (src − dst)·a</c>, independent of the host's channel order.</summary>
    /// <param name="destination">The pixel as it stands.</param>
    /// <param name="source">The colour being painted.</param>
    /// <param name="alpha">Its coverage, 0..1.</param>
    /// <returns>The composited pixel.</returns>
    /// <remarks>
    /// Fixed point in 1/256ths, floored — the step the whole assembly shares, so two paths that mix
    /// the same two colours at the same alpha land on the same byte.
    /// </remarks>
    public static uint Over(uint destination, uint source, double alpha)
    {
        int a = (int)Math.Round(Math.Clamp(alpha, 0.0, 1.0) * 256.0);
        uint result = 0;
        for (int shift = 0; shift <= 16; shift += 8)
        {
            int d = (int)((destination >> shift) & 0xFF);
            int s = (int)((source >> shift) & 0xFF);
            int v = d + (((s - d) * a) >> 8);
            result |= (uint)Math.Clamp(v, 0, 255) << shift;
        }

        return result;
    }
}
