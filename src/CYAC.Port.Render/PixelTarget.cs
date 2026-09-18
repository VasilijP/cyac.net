namespace CYAC.Port.Render;

/// <summary>
/// How a host packs an (R, G, B) triple into the <c>uint</c> pixels of its frame buffer.
/// </summary>
/// <remarks>
/// The renderer knows the COLOURS (they are game facts); the host knows the ENCODING (it is a
/// property of its presentation layer).  This enum is the whole of the contract between them, so
/// <c>CYAC.Port.Render</c> never has to reference the host or guess at endianness.
/// </remarks>
public enum PixelChannelOrder
{
    /// <summary>
    /// <c>0x00RRGGBB</c> — red in bits 16..23.  What <c>CYAC.Formats.Image.PngWriter.WriteBgra</c>
    /// reads.
    /// </summary>
    RedHigh = 0,

    /// <summary>
    /// <c>0x00BBGGRR</c> — blue in bits 16..23.  What mode-13hx's
    /// <c>Func.EncodePixelColor</c> produces on a little-endian machine
    /// (<c>external/mode-13hx/src/Util/Functions.cs</c>).
    /// </summary>
    BlueHigh = 1,
}

/// <summary>
/// A rectangle of <c>uint</c> pixels the renderer may write, in the host's own layout.
/// </summary>
/// <remarks>
/// <para>
/// This is the entire host-facing surface of <c>CYAC.Port.Render</c>: a
/// span, its dimensions, its column stride and its channel order.  No window, no device, no
/// framebuffer type from any other assembly.
/// </para>
/// <para>
/// The layout is <b>column-major</b>, because mode-13hx's is: pixel <c>(x, y)</c> lives at
/// <c>x * ColumnStride + y</c> (<c>external/mode-13hx/src/BlankRasterizer.cs</c>: "buffer is
/// organized by columns … <c>frame.Offset + x * frame.Buffer.Height + y</c>").  That suits a
/// horizon renderer, whose natural primitive is a vertical span.  <see cref="ColumnStride"/> is
/// normally <see cref="Height"/>; it is a separate field so a host can hand over a sub-rectangle of
/// a taller buffer.
/// </para>
/// </remarks>
public readonly ref struct PixelTarget
{
    private readonly Span<uint> _pixels;

    /// <summary>Wraps a host frame buffer.</summary>
    /// <param name="pixels">The pixels; at least <c>(Width - 1) * ColumnStride + Height</c> long.</param>
    /// <param name="width">Visible width in pixels.</param>
    /// <param name="height">Visible height in pixels.</param>
    /// <param name="columnStride">Distance in <c>uint</c>s between column <c>x</c> and column <c>x + 1</c>.</param>
    /// <param name="order">How the host packs colour channels.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive, or the stride is shorter than the height.</exception>
    /// <exception cref="ArgumentException">The span is too short for the geometry.</exception>
    public PixelTarget(
        Span<uint> pixels,
        int width,
        int height,
        int columnStride,
        PixelChannelOrder order = PixelChannelOrder.BlueHigh)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columnStride, height);
        if (pixels.Length < ((width - 1) * columnStride) + height)
        {
            throw new ArgumentException(
                $"a {width}×{height} target with column stride {columnStride} needs "
                    + $"{((width - 1) * columnStride) + height} pixels, got {pixels.Length}",
                nameof(pixels));
        }

        _pixels = pixels;
        Width = width;
        Height = height;
        ColumnStride = columnStride;
        Order = order;
    }

    /// <summary>Visible width in pixels.</summary>
    public int Width { get; }

    /// <summary>Visible height in pixels.</summary>
    public int Height { get; }

    /// <summary>Distance in <c>uint</c>s between two neighbouring columns.</summary>
    public int ColumnStride { get; }

    /// <summary>How this target packs colour channels.</summary>
    public PixelChannelOrder Order { get; }

    /// <summary>
    /// The whole backing span, for the ONE caller that has to pin it.
    /// </summary>
    /// <remarks>
    /// A <c>ref struct</c> cannot be handed to a worker thread, so the tiled drawer pins this span
    /// for the duration of its parallel section and each worker rebuilds an identical
    /// <see cref="PixelTarget"/> over the pinned memory (<c>Pipeline/DisplayListDrawer</c>).  It
    /// stays INTERNAL: the assembly's host-facing contract is still "pixels in a
    /// <c>Span&lt;uint&gt;</c>", and nothing outside this assembly may take the address of a host's
    /// frame buffer.
    /// </remarks>
    internal Span<uint> Buffer => _pixels;

    /// <summary>One column of the target, top to bottom.</summary>
    /// <param name="x">The column.</param>
    public Span<uint> Column(int x) => _pixels.Slice(x * ColumnStride, Height);

    /// <summary>Packs a colour for this target.</summary>
    /// <param name="color">The colour.</param>
    public uint Encode(Rgb24 color) => Order switch
    {
        PixelChannelOrder.RedHigh => ((uint)color.R << 16) | ((uint)color.G << 8) | color.B,
        _ => ((uint)color.B << 16) | ((uint)color.G << 8) | color.R,
    };

    /// <summary>Fills rows <c>[yStart, yEnd)</c> of one column with a packed colour.</summary>
    /// <param name="x">The column.</param>
    /// <param name="yStart">First row, clamped to the target.</param>
    /// <param name="yEnd">One past the last row, clamped to the target.</param>
    /// <param name="packed">An already-<see cref="Encode(Rgb24)"/>d colour.</param>
    public void FillColumn(int x, int yStart, int yEnd, uint packed)
    {
        int start = Math.Clamp(yStart, 0, Height);
        int end = Math.Clamp(yEnd, 0, Height);
        if (end > start)
        {
            Column(x).Slice(start, end - start).Fill(packed);
        }
    }

    /// <summary>Writes one pixel, ignoring coordinates outside the target.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <param name="packed">An already-<see cref="Encode(Rgb24)"/>d colour.</param>
    public void SetPixel(int x, int y, uint packed)
    {
        if ((uint)x < (uint)Width && (uint)y < (uint)Height)
        {
            _pixels[(x * ColumnStride) + y] = packed;
        }
    }
}
