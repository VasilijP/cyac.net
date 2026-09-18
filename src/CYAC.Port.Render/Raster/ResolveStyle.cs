namespace CYAC.Port.Render.Raster;

/// <summary>
/// The frame-wide PRESENTATION choices the fragment resolve makes ("two looks").
/// </summary>
/// <param name="Dither">
/// Quantise every fragment's TRANSLUCENCY alpha to 0/1 through the screen-space ordered matrix
/// (<see cref="OrderedDither"/>) instead of compositing it — <c>--alpha dither</c>.  The area term is
/// untouched, so edges stay analytic unless <paramref name="Edges"/> says otherwise.
/// </param>
/// <param name="Edges">
/// Whether a fragment's AREA term is the exact area of the pixel the primitive covers
/// (<see cref="EdgeMode.Analytic"/>) or the classic centre sample (<see cref="EdgeMode.Hard"/>).
/// </param>
/// <remarks>
/// <para>
/// It is PRESENTATION, not game knowledge: nothing in it names an aircraft, a record or a mission,
/// and it reaches the rasteriser through <see cref="FragmentRaster.BeginFrame"/> (deleted in R4 with
/// its one implementation) rather than through the display list's primitives, because it is a
/// property of the frame and not of any one of them.
/// </para>
/// <para>
/// The two looks are orthogonal and all four combinations are legal: analytic edges with dithered
/// translucency (dither patterns are retro, so the port makes them properly screen-space retro),
/// and hard edges with it (the FULL retro look — the same switch applied to every fragment).
/// </para>
/// </remarks>
internal readonly record struct ResolveStyle(
    bool Dither = false, EdgeMode Edges = EdgeMode.Analytic)
{
    /// <summary>The shipped look: analytic coverage everywhere.</summary>
    public static ResolveStyle Default => new(false, EdgeMode.Analytic);

    /// <summary>The retro dither with analytic edges — <c>--alpha dither</c> on its own.</summary>
    /// <returns>The style.</returns>
    public static ResolveStyle Retro() => new(true, EdgeMode.Analytic);
}
