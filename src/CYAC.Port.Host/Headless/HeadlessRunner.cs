using System.Globalization;
using CYAC.Formats.Image;
using mode13hx;
using mode13hx.Configuration;
using mode13hx.Presentation;

namespace CYAC.Port.Host.Headless;

/// <summary>
/// Renders host frames to PNG files without opening a window — the way to SEE what the port draws
/// without a display, and the source of every visual claim made about it.
/// </summary>
/// <remarks>
/// It is a presenter, and behaves like one: it drives the same <see cref="FrameBuffer"/> lifecycle
/// mode-13hx's <c>EngineWindow</c> does — <c>StartNextFrame</c> / <c>FinishFrame</c> from the
/// rasterizer, then <c>Use</c> / <c>ReleaseFrame</c> from here — so the rasterizer under test is
/// exactly the one the window runs, and slots are recycled instead of running out.
/// </remarks>
public sealed class HeadlessRunner
{
    private readonly FrameBuffer _buffer;
    private readonly IRasterizer _rasterizer;
    private readonly Func<int> _worldRows;
    private readonly int _width;
    private readonly int _height;

    /// <summary>Creates a headless presenter.</summary>
    /// <param name="config">The frame geometry (mode-13hx's own options object).</param>
    /// <param name="rasterizer">The rasterizer to drive.</param>
    /// <param name="worldRows">
    /// How many rows of a frame the 3-D world wrote, for the H12 pixel census; null when the
    /// rasterizer has no world (the front-end shell before it starts a sortie).  The parameter
    /// exists because the runner now drives <see cref="IRasterizer"/> rather than
    /// <see cref="FlightRasterizer"/> — the shell is one too.
    /// </param>
    public HeadlessRunner(
        CommonOptions config, IRasterizer rasterizer, Func<int>? worldRows = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rasterizer);
        _buffer = new FrameBuffer(config);
        _rasterizer = rasterizer;
        _worldRows = worldRows ?? (() => 0);
        _width = config.Width;
        _height = config.Height;
    }

    /// <summary>
    /// The jaggedness/shimmer census, when <c>--pixel-census</c> asked for one.
    /// </summary>
    public PixelCensus? Census { get; init; }

    /// <summary>
    /// <c>--crop</c>: the rectangle to magnify beside every saved frame, or null.
    /// </summary>
    public (int X, int Y, int Width, int Height)? Crop { get; init; }

    /// <summary><c>--crop-scale</c>: the crop's nearest-neighbour magnification.</summary>
    public int CropScale { get; init; } = 4;

    /// <summary>Renders one frame and, if asked, saves it.</summary>
    /// <param name="elapsedSeconds">The wall time this frame stands for.</param>
    /// <param name="pngPath">Where to write the PNG, or null to render and discard.</param>
    public void RenderFrame(double elapsedSeconds, string? pngPath)
    {
        _rasterizer.Render(_buffer, elapsedSeconds);

        FrameDescriptor? descriptor = _buffer.Use();
        try
        {
            // The census reads the FINISHED frame, and only the rows the 3-D world wrote.
            Census?.Observe(
                descriptor.Buffer.Data.AsSpan(descriptor.Offset, descriptor.Buffer.FrameSize),
                _width,
                _height,
                _worldRows());

            if (pngPath is not null)
            {
                Save(descriptor, pngPath);
            }
        }
        finally
        {
            _buffer.ReleaseFrame(descriptor);
        }
    }

    /// <summary>The conventional file name for frame <paramref name="index"/>.</summary>
    /// <param name="index">The frame ordinal.</param>
    public static string FrameFileName(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"frame_{index:D5}.png");

    private void Save(FrameDescriptor descriptor, string path)
    {
        // The frame is column-major and blue-high (mode-13hx's Func.EncodePixelColor); PngWriter
        // wants row-major with red high, so the copy transposes AND swaps R with B. through
        // FrameImage, which the F12 screenshot and --render-scene share.
        uint[] rows = FrameImage.SavePng(
            path,
            descriptor.Buffer.Data.AsSpan(descriptor.Offset, descriptor.Buffer.FrameSize),
            _width,
            _height,
            _height,
            blueHigh: true);

        if (Crop is { } crop)
        {
            SaveCrop(rows, crop, Path.ChangeExtension(path, ".crop.png"));
        }
    }

    /// <summary>
    /// Writes one rectangle of the frame, magnified with NEAREST sampling.
    /// </summary>
    /// <param name="rows">The frame, row-major and red-high (what <c>PngWriter</c> wants).</param>
    /// <param name="crop">The rectangle in frame pixels.</param>
    /// <param name="path">Where to write it.</param>
    /// <remarks>
    /// Nearest and not bilinear on purpose: the point of a 4× crop is to show the ACTUAL
    /// pixels the renderer produced — a staircase at <c>--edges hard</c> and its graded replacement
    /// at <c>--edges analytic</c> (retired) — and any smoothing here would put the anti-aliasing in
    /// the instrument instead of in the renderer.
    /// </remarks>
    private void SaveCrop(uint[] rows, (int X, int Y, int Width, int Height) crop, string path)
    {
        int x0 = Math.Clamp(crop.X, 0, _width - 1);
        int y0 = Math.Clamp(crop.Y, 0, _height - 1);
        int w = Math.Clamp(crop.Width, 1, _width - x0);
        int h = Math.Clamp(crop.Height, 1, _height - y0);
        int scale = Math.Clamp(CropScale, 1, 32);

        uint[] magnified = new uint[w * scale * h * scale];
        for (int y = 0; y < h * scale; y++)
        {
            int sourceRow = (y0 + (y / scale)) * _width;
            int destinationRow = y * w * scale;
            for (int x = 0; x < w * scale; x++)
            {
                magnified[destinationRow + x] = rows[sourceRow + x0 + (x / scale)];
            }
        }

        PngWriter.WriteBgra(path, magnified, w * scale, h * scale);
    }
}
