using CYAC.Formats.Image;

namespace CYAC.Port.Host.Headless;

/// <summary>
/// Turns a rendered frame into the row-major, red-high pixel rows <see cref="PngWriter"/> wants,
/// and writes the PNG.  Shared by the headless runner (every saved frame), the F12 screenshot and
/// the <c>--render-scene</c> re-render, so the three cannot drift in their channel handling.
/// </summary>
internal static class FrameImage
{
    /// <summary>
    /// Transposes a COLUMN-MAJOR frame into row-major pixels, swapping red and blue when the source
    /// packs blue high (mode-13hx's <c>Func.EncodePixelColor</c>).
    /// </summary>
    /// <param name="source">The frame, column-major: pixel (x, y) at <c>x * columnStride + y</c>.</param>
    /// <param name="width">Columns.</param>
    /// <param name="height">Rows to take from each column.</param>
    /// <param name="columnStride">Pixels per column in the source (≥ height).</param>
    /// <param name="blueHigh">Whether the source packs blue in bits 16..23 (red there otherwise).</param>
    /// <returns>Row-major, red-high pixels, <c>width × height</c>.</returns>
    public static uint[] ToRows(ReadOnlySpan<uint> source, int width, int height, int columnStride, bool blueHigh)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columnStride, height);
        uint[] rows = new uint[width * height];
        for (int x = 0; x < width; x++)
        {
            int column = x * columnStride;
            for (int y = 0; y < height; y++)
            {
                uint pixel = source[column + y];
                if (blueHigh)
                {
                    pixel = ((pixel & 0x0000_00FFu) << 16)
                        | (pixel & 0x0000_FF00u)
                        | ((pixel & 0x00FF_0000u) >> 16);
                }

                rows[(y * width) + x] = pixel;
            }
        }

        return rows;
    }

    /// <summary>Writes a column-major frame as a PNG, creating the directory.</summary>
    /// <param name="path">Where to write it.</param>
    /// <param name="source">The frame (see <see cref="ToRows"/>).</param>
    /// <param name="width">Columns.</param>
    /// <param name="height">Rows.</param>
    /// <param name="columnStride">Pixels per column in the source.</param>
    /// <param name="blueHigh">Whether the source packs blue high.</param>
    /// <returns>The row-major pixels that were written, for a caller that wants a crop too.</returns>
    public static uint[] SavePng(
        string path, ReadOnlySpan<uint> source, int width, int height, int columnStride, bool blueHigh)
    {
        uint[] rows = ToRows(source, width, height, columnStride, blueHigh);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        PngWriter.WriteBgra(path, rows, width, height);
        return rows;
    }
}
