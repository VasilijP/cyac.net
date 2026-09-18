namespace CYAC.Port.Render;

/// <summary>
/// One PALETTE-INDEXED sprite with a colour key, as the port's renderer draws it.
/// </summary>
/// <remarks>
/// <para>
/// The engine's sprites are run-length-encoded (<c>gfx_sprite_clip_and_blit @image@0x1D966</c>,
/// the transform decodes each one to a plain index raster plus its key
/// (<c>data/images/&lt;name&gt;.json</c> + <c>.png</c>, family <c>rle</c>), so by the time the
/// renderer sees it the run structure is gone and what is left is width × height palette indices
/// and the index that means "see through".  This type is that, and nothing else: no file access, no
/// palette (the renderer owns the palette), no decode.
/// </para>
/// <para>
/// A sprite is drawn as a screen-space BILLBOARD at a requested destination size, which is exactly
/// what the original's blitter does: <c>gfx_sprite_blit_centered_xlat_on @image@0x13E61</c> takes
/// the destination width and height as Bresenham numerators and the callee stretches the source to
/// fit (scanner :309 — "the callee's width/height are the REQUESTED DESTINATION extent, not the
/// sprite's size"), centring on the given point (<c>dst = centre − dim/2</c>).
/// </para>
/// </remarks>
/// <param name="Width">The source raster's width in pixels.</param>
/// <param name="Height">Its height.</param>
/// <param name="Indices">
/// <see cref="Width"/> × <see cref="Height"/> palette indices, ROW MAJOR — index
/// <c>(y · Width) + x</c>, which is how the transform writes the PNG.
/// </param>
/// <param name="ColorKey">
/// The index that is see-through (<c>data/images/exp.json</c> <c>"colorKey": 224</c>).
/// </param>
public sealed record SpriteImage(int Width, int Height, byte[] Indices, byte ColorKey)
{
    /// <summary>Builds a sprite, checking the raster's size.</summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="indices">The palette indices, row major.</param>
    /// <param name="colorKey">The transparent index.</param>
    /// <returns>The sprite.</returns>
    /// <exception cref="ArgumentException">The raster is not <c>width × height</c> bytes.</exception>
    public static SpriteImage Create(int width, int height, byte[] indices, byte colorKey)
    {
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (indices.Length != width * height)
        {
            throw new ArgumentException(
                $"a {width}×{height} sprite needs {width * height} indices, got {indices.Length}",
                nameof(indices));
        }

        return new SpriteImage(width, height, indices, colorKey);
    }

    /// <summary>The index at a source pixel.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <returns>The palette index, or <see cref="ColorKey"/> outside the raster.</returns>
    public byte At(int x, int y) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height ? Indices[(y * Width) + x] : ColorKey;
}
