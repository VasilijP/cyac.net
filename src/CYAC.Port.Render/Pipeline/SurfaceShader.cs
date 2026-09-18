using CYAC.Port.Core.Model.World;
using CYAC.Port.Render.Raster;

namespace CYAC.Port.Render.Pipeline;

/// <summary>
/// VECTOR MARKINGS — one marking's ATTRIBUTE PLANES on one primitive: <c>u/z</c> and <c>v/z</c> as
/// affine functions of the pixel, built next to the depth plane from the same view-space derivation,
/// plus the picture they feed.
/// </summary>
/// <remarks>
/// <para>
/// For a planar face, any quantity affine in model space is affine in view space, and divided by
/// <c>z</c> it is affine in SCREEN space — exactly as <c>1/z</c> is.  So the frame coordinate of the
/// pixel's surface point is <c>u = U(x, y) / Z(x, y)</c> with both numerator and denominator three-
/// term evaluations: no per-vertex attributes, no interpolation, exact at every pixel.
/// </para>
/// <para>
/// The footprint (picture units per pixel) is analytic too: <c>∂u/∂x = (A_u·Z − U·A_z) / Z²</c>, so
/// a marking anti-aliases from its signed distance and band-limits its noise without a derivative
/// buffer.
/// </para>
/// </remarks>
internal struct MarkingPlaneEntry
{
    /// <summary><c>u/z</c> over the pixel, model units.</summary>
    public DepthPlane U;

    /// <summary><c>v/z</c> over the pixel, model units.</summary>
    public DepthPlane V;

    /// <summary>Model units per picture unit (the placement's size).</summary>
    public double Scale;

    /// <summary>Whether the picture's X is negated on this face (a mirrored placement).</summary>
    public bool Flip;

    /// <summary>The picture; immutable and pure, shared across frames and threads.</summary>
    public ISurfacePicture Picture;
}

/// <summary>One shaded primitive's entry in the display list's per-frame shader table.</summary>
internal struct SurfaceShaderEntry
{
    /// <summary>The face's own paint, as the picture language sees it.</summary>
    public SurfaceColor Skin;

    /// <summary>Where its marking planes start in the list's plane pool.</summary>
    public int PlaneStart;

    /// <summary>How many markings the primitive carries.</summary>
    public int PlaneCount;
}

/// <summary>
/// The per-pixel colour rule a polygon fill runs — a struct-generic seam so the rasteriser's ONE
/// fill loop serves both the flat colour every primitive had until now and the shaded skin, with
/// the flat case inlined to what it always was.
/// </summary>
internal interface IPixelShade
{
    /// <summary>The packed colour of the pixel at <c>(x, y)</c>, given its <c>1/z</c>.</summary>
    /// <param name="x">Absolute column.</param>
    /// <param name="y">Absolute row.</param>
    /// <param name="invZ">The primitive's <c>1/z</c> at the pixel's centre.</param>
    uint Color(int x, int y, double invZ);
}

/// <summary>The constant colour of an unshaded primitive.</summary>
internal readonly struct FlatShade(uint packed) : IPixelShade
{
    /// <inheritdoc />
    public uint Color(int x, int y, double invZ) => packed;
}

/// <summary>
/// The SURFACE SHADER: the skin's paint with every marking placed on the face composited over it,
/// per pixel.
/// </summary>
/// <remarks>
/// Pure over immutable data — the plane pool is written by the WHAT stage before any tile is
/// drawn and only read here, and every <see cref="ISurfacePicture"/> is a pure function — so tile
/// workers may run it concurrently and a frame is bit-identical at any thread count.
/// </remarks>
internal readonly struct MarkedShade(
    SurfaceColor skin,
    uint skinPacked,
    DepthPlane depth,
    MarkingPlaneEntry[] planes,
    int start,
    int count,
    PixelChannelOrder order) : IPixelShade
{
    /// <inheritdoc />
    public uint Color(int x, int y, double invZ)
    {
        if (!(invZ > 0.0))
        {
            return skinPacked;
        }

        SurfaceColor color = skin;
        bool touched = false;
        double z = 1.0 / invZ;
        double zz = z * z;
        for (int i = 0; i < count; i++)
        {
            ref readonly MarkingPlaneEntry m = ref planes[start + i];
            double pu = m.U.At(x, y);
            double pv = m.V.At(x, y);
            double u = pu * z;
            double v = pv * z;

            // u = P/Z with Z = 1/z, so ∂u/∂x = (A_p·Z − P·A_z) / Z² = (A_p·Z − P·A_z)·z².
            double ux = ((m.U.A * invZ) - (pu * depth.A)) * zz;
            double uy = ((m.U.B * invZ) - (pu * depth.B)) * zz;
            double vx = ((m.V.A * invZ) - (pv * depth.A)) * zz;
            double vy = ((m.V.B * invZ) - (pv * depth.B)) * zz;
            double footprint = Math.Max(
                Math.Max(Math.Abs(ux), Math.Abs(uy)), Math.Max(Math.Abs(vx), Math.Abs(vy)));

            double inv = 1.0 / m.Scale;
            double px = u * inv;
            double py = v * inv;
            double fp = footprint * inv;
            if (m.Flip)
            {
                px = -px;
            }

            if (!m.Picture.Bounds.Contains(px, py, fp))
            {
                continue;
            }

            color = m.Picture.Shade(color, px, py, fp);
            touched = true;
        }

        if (!touched)
        {
            return skinPacked;
        }

        return order == PixelChannelOrder.RedHigh
            ? ((uint)color.R << 16) | ((uint)color.G << 8) | color.B
            : ((uint)color.B << 16) | ((uint)color.G << 8) | color.R;
    }
}

/// <summary>
/// S1's DEBUG picture: a unit checkerboard in picture space, anti-aliased from its own signed
/// distance, with the footprint shown as a tint — the proof that the planes, the divide and the
/// derivative are right before any real picture exists (S1).
/// </summary>
/// <param name="cell">The checker cell size in picture units.</param>
/// <param name="a">One square's colour.</param>
/// <param name="b">The other's.</param>
internal sealed class DebugCheckerPicture(double cell, SurfaceColor a, SurfaceColor b) : ISurfacePicture
{
    /// <inheritdoc />
    public SurfaceBounds Bounds { get; } = new(-1.0, -1.0, 1.0, 1.0);

    /// <inheritdoc />
    public SurfaceColor Shade(SurfaceColor skin, double x, double y, double footprint)
    {
        // Distance to the nearest grid line on each axis; the sign alternates per cell.
        double fx = x / cell, fy = y / cell;
        double ix = Math.Floor(fx), iy = Math.Floor(fy);
        double dx = Math.Min(fx - ix, ix + 1.0 - fx) * cell;
        double dy = Math.Min(fy - iy, iy + 1.0 - fy) * cell;
        double d = Math.Min(dx, dy);
        bool dark = (((long)ix + (long)iy) & 1) != 0;
        double signed = dark ? -d : d;   // negative inside a dark square

        // Coverage of the dark square at this pixel: 0.5 at its edge, ramping over one footprint.
        double fp = Math.Max(footprint, 1e-9);
        double coverage = Math.Clamp(0.5 - (signed / fp), 0.0, 1.0);

        // Inside the unit box the picture replaces the skin entirely; outside it is the skin.
        if (x < Bounds.MinX || x > Bounds.MaxX || y < Bounds.MinY || y > Bounds.MaxY)
        {
            return skin;
        }

        byte Mix(byte p, byte q) => (byte)Math.Clamp(Math.Round(p + ((q - p) * coverage)), 0, 255);
        return new SurfaceColor(Mix(b.R, a.R), Mix(b.G, a.G), Mix(b.B, a.B));
    }
}
