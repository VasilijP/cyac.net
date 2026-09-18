using System.Diagnostics;
using System.Runtime.Intrinsics;
using mode13hx.Configuration;
using mode13hx.Util;
using mode13hx.Presentation;

namespace mode13hx.Presentation.Compression;

/// <summary>
/// Experimental: L26's exact 2D Haar + PackBlock, but reading 64 CONSECUTIVE pixels instead of 8×8 spatial tiles.
/// Purpose: isolate memory access pattern as the only variable vs L26.
/// Same GPU decompressor as L26 (decomp_l26.comp) — visual quality will differ since the 8×8 "tile"
/// is actually a vertical strip, but this is for performance measurement, not production use.
/// </summary>
public sealed class FrameCompressorSL64 : IFrameCompressor
{
    private const int BlockPixels = 64;
    private const int BlockUints = 8;
    private const int TileSize = 8; // treated as 8×8 for Haar2D, but loaded linearly

    private readonly int offset;
    private readonly int pixelCount;
    private static uint gpuGroupsX;
    private static int threadCount = 16;

    // Same 2D Haar math as L26 but linear pixel output mapping
    public string DecompressShaderName => "decomp_sl64.comp";

    private FrameCompressorSL64(int offset, int count)
    {
        this.offset = offset;
        this.pixelCount = count;
    }

    public FrameCompressorSL64(CommonOptions config)
    {
        this.offset = 0;
        this.pixelCount = 0;
        int totalPixels = config.Width * config.Height;
        int totalBlocks = totalPixels / BlockPixels;
        gpuGroupsX = (uint)totalBlocks * 32;
        threadCount = Math.Max(1, config.CompressionThreads);
    }

    public void Setup(FrameDescriptor fd, int compressorDesiredCount)
    {
        int totalPixels = fd.Buffer.Width * fd.Buffer.Height;
        int totalBlocks = totalPixels / BlockPixels;

        // Slice by contiguous pixel ranges (like RLE/BL16/S64)
        int sliceCount = Math.Min(threadCount, totalBlocks);
        int pixelsPerSlice = (totalPixels / sliceCount / BlockPixels) * BlockPixels;
        int actualCount = totalPixels / pixelsPerSlice;

        fd.Compressor = new IFrameCompressor[actualCount];
        for (int i = 0; i < actualCount; ++i)
        {
            int off = i * pixelsPerSlice;
            int cnt = (i == actualCount - 1) ? totalPixels - off : pixelsPerSlice;
            fd.Compressor[i] = new FrameCompressorSL64(off, cnt);
        }

        fd.Blocks = new AlignedArray<uint>(totalBlocks * BlockUints);
    }

    private double min = double.Pi;
    private double took, parTime, sum, avg;
    private int fcount;
    private static ParallelOptions parallelOpts;

    public void Start(FrameDescriptor fd)
    {
        Stopwatch sw = Stopwatch.StartNew();
        string hud = $"SL64 {fd.Count * sizeof(uint) / (1024.0 * 1024.0):F2}MB took:{took:F1}ms par:{parTime:F1}ms min:{min:F1} avg:{avg:F1} T:{fd.Compressor.Length}";
        fd.Canvas.SetPenColor(0, 0, 0).DrawString(hud, 12, 3, Canvas.Font9X16);
        fd.Canvas.SetPenColor(Canvas.White).DrawString(hud, 10, 1, Canvas.Font9X16);
        fd.Count = 0;

        parallelOpts ??= new ParallelOptions { MaxDegreeOfParallelism = threadCount };
        int totalBlocks = fd.Buffer.Width * fd.Buffer.Height / BlockPixels;
        int blockCount = totalBlocks * BlockUints;

        fd.CpuCompressionTask = Task.Run(() =>
        {
            Stopwatch parSw = Stopwatch.StartNew();
            if (fd.Compressor.Length == 1)
                fd.Compressor[0].Compress(fd);
            else
                Parallel.ForEach(fd.Compressor, parallelOpts, c => { c.Compress(fd); });
            parTime = parSw.Elapsed.TotalMilliseconds;
            fd.Count = blockCount;
            fd.GpuDecompressionGroupsX = gpuGroupsX;
            fd.GpuDecompressionGroupsY = 1;
            took = sw.Elapsed.TotalMilliseconds;
            sum += took; fcount++;
            avg = sum / fcount;
            if (min > took) min = took;
        });
    }

    public unsafe void Compress(FrameDescriptor fd)
    {
        fd.GpuDecompressionGroupsX = gpuGroupsX;
        fd.GpuDecompressionGroupsY = 1;

        uint* inputPtr = fd.Buffer.Data.AsPointer() + fd.Offset + offset;
        uint* outputPtr = fd.Blocks.AsPointer();

        int* yCoeffs = stackalloc int[BlockPixels];
        int* coCoeffs = stackalloc int[BlockPixels];
        int* cgCoeffs = stackalloc int[BlockPixels];
        uint* packed = stackalloc uint[BlockUints];

        Vector128<uint> mask0xFF = Vector128.Create(0xFFu);

        for (int blockStart = 0; blockStart < pixelCount; blockStart += BlockPixels)
        {
            int blockIndex = (offset + blockStart) / BlockPixels;
            uint* pixels = inputPtr + blockStart;

            // Load 64 CONSECUTIVE pixels (linear, cache-perfect) into 8×8 coefficient layout
            // pixel[0..7] → row 0, pixel[8..15] → row 1, ..., pixel[56..63] → row 7
            for (int i = 0; i < BlockPixels; i += 4)
            {
                Vector128<uint> px = Vector128.Load(pixels + i);
                Vector128<int> r = (px & mask0xFF).AsInt32();
                Vector128<int> g = (Vector128.ShiftRightLogical(px, 8) & mask0xFF).AsInt32();
                Vector128<int> b = (Vector128.ShiftRightLogical(px, 16) & mask0xFF).AsInt32();
                Vector128<int> co = r - b;
                Vector128<int> t = b + Vector128.ShiftRightArithmetic(co, 1);
                Vector128<int> cg = g - t;
                Vector128<int> y = t + Vector128.ShiftRightArithmetic(cg, 1);
                y.Store(yCoeffs + i);
                co.Store(coCoeffs + i);
                cg.Store(cgCoeffs + i);
            }

            // Apply L26's exact 2D Haar transform (treats the 64 values as 8×8 grid)
            FrameCompressorL26.ForwardHaar2D(yCoeffs, TileSize, 3);
            FrameCompressorL26.ForwardHaar2D(coCoeffs, TileSize, 3);
            FrameCompressorL26.ForwardHaar2D(cgCoeffs, TileSize, 3);

            // Apply L26's exact PackBlock
            FrameCompressorL26.PackBlock(yCoeffs, coCoeffs, cgCoeffs, packed);

            // Write to output
            uint* dest = outputPtr + blockIndex * BlockUints;
            for (int i = 0; i < BlockUints; i++)
                dest[i] = packed[i];
        }
    }

    public void CompressSafe(FrameDescriptor fd) => Compress(fd);

    public void Decompress(FrameDescriptor fd)
    {
        throw new NotImplementedException();
    }
}
