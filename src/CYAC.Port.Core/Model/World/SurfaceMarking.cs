namespace CYAC.Port.Core.Model.World;

/// <summary>An 8-bit RGB triple — the colour vocabulary of the surface shading contract.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <remarks>
/// VECTOR MARKINGS.  <c>CYAC.Port.Core</c> has no renderer type to borrow (<c>Rgb24</c> lives in
/// <c>CYAC.Port.Render</c>), and the marking language is Core knowledge that the renderer merely
/// evaluates, so the colour crosses the boundary as this triple and the renderer packs it in the
/// target's own channel order.
/// </remarks>
public readonly record struct SurfaceColor(byte R, byte G, byte B)
{
    /// <summary>Parses <c>#RRGGBB</c> (or <c>RRGGBB</c>).</summary>
    /// <param name="text">The hex text.</param>
    /// <exception cref="FormatException">Not six hex digits.</exception>
    public static SurfaceColor Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string hex = text.StartsWith('#') ? text[1..] : text;
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int value))
        {
            throw new FormatException($"a colour is #RRGGBB, got '{text}'");
        }

        return new SurfaceColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary><c>#RRGGBB</c>.</summary>
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// VECTOR MARKINGS — a resolution-free 2-D picture the renderer can evaluate at any point of a
/// surface: what a marking IS, once the geometry has said where the pixel lies on it.
/// </summary>
/// <remarks>
/// <para>
/// The contract the fragment renderer's surface shader calls per covered pixel of a marked face.
/// It is PURE over immutable data: the tile workers run in parallel and a frame must be
/// bit-identical at any thread count, so an implementation may not keep per-call state and must be
/// a function of its arguments alone.
/// </para>
/// <para>
/// Coordinates are the picture's own space (the placement's <c>Size</c> scales it into model
/// units); the FOOTPRINT is how many picture units one target pixel spans, which is what
/// anti-aliasing (coverage = distance ÷ footprint) and band-limiting (noise fades as the footprint
/// grows — the anti-shimmer law, <c>native-port-plan.md</c> §7.6) are stated in.
/// </para>
/// </remarks>
public interface ISurfacePicture
{
    /// <summary>
    /// The picture-space box outside of which <see cref="Shade"/> returns the skin unchanged —
    /// the renderer's per-pixel early-out.  Grown by whatever wear band the picture applies.
    /// </summary>
    SurfaceBounds Bounds { get; }

    /// <summary>The picture composited over the skin at one point.</summary>
    /// <param name="skin">The surface's own paint at this pixel.</param>
    /// <param name="x">Picture-space X.</param>
    /// <param name="y">Picture-space Y.</param>
    /// <param name="footprint">Picture units per target pixel (the larger of the two axes).</param>
    /// <returns>The shaded colour.</returns>
    SurfaceColor Shade(SurfaceColor skin, double x, double y, double footprint);
}

/// <summary>An axis-aligned box in picture space.</summary>
/// <param name="MinX">Left.</param>
/// <param name="MinY">Bottom.</param>
/// <param name="MaxX">Right.</param>
/// <param name="MaxY">Top.</param>
public readonly record struct SurfaceBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>Whether a point lies inside the box, grown by a margin on every side.</summary>
    /// <param name="x">Picture-space X.</param>
    /// <param name="y">Picture-space Y.</param>
    /// <param name="margin">The margin, picture units.</param>
    public bool Contains(double x, double y, double margin) =>
        x >= MinX - margin && x <= MaxX + margin && y >= MinY - margin && y <= MaxY + margin;

    /// <summary>The box grown by a margin on every side.</summary>
    /// <param name="margin">The margin.</param>
    public SurfaceBounds Grow(double margin) =>
        new(MinX - margin, MinY - margin, MaxX + margin, MaxY + margin);

    /// <summary>The smallest box holding both.</summary>
    /// <param name="other">The other box.</param>
    public SurfaceBounds Union(SurfaceBounds other) => new(
        Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY),
        Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));

    /// <summary>The box's width.</summary>
    public double Width => MaxX - MinX;

    /// <summary>The box's height.</summary>
    public double Height => MaxY - MinY;
}

/// <summary>
/// VECTOR MARKINGS — one picture PLACED on a surface: a frame in model space (origin and two unit
/// axes), the scale from picture units to model units, and the picture.
/// </summary>
/// <param name="OriginX">The frame origin's model X — where the picture's (0, 0) lands.</param>
/// <param name="OriginY">Its model Y.</param>
/// <param name="OriginZ">Its model Z.</param>
/// <param name="UX">The picture's +X axis in model space (unit length).</param>
/// <param name="UY">…</param>
/// <param name="UZ">…</param>
/// <param name="VX">The picture's +Y axis in model space (unit length, ⟂ U).</param>
/// <param name="VY">…</param>
/// <param name="VZ">…</param>
/// <param name="Size">Model units per picture unit.</param>
/// <param name="Flip">Whether the picture is mirrored (its X negated) on this face.</param>
/// <param name="Picture">The picture.</param>
/// <remarks>
/// <para>
/// The renderer recovers a pixel's model-space point from the face's plane and projects it INTO the
/// frame: <c>u = U·(m − O)</c>, <c>v = V·(m − O)</c>, model units, then divides by
/// <see cref="Size"/>.  The component of <c>m − O</c> along the frame's normal <c>U × V</c> is
/// dropped by construction, which is what lets one frame authored on the shipped plane serve an
/// inflated skin that sits a couple of units off it.
/// </para>
/// <para>
/// The frame is a value: a placement file names it in numbers, the resolver copies it onto every
/// face it reaches, and nothing here refers back to the placement it came from.
/// </para>
/// </remarks>
public readonly record struct SurfaceMarkingFrame(
    double OriginX, double OriginY, double OriginZ,
    double UX, double UY, double UZ,
    double VX, double VY, double VZ,
    double Size,
    bool Flip,
    ISurfacePicture Picture);
