using System.Diagnostics;
using System.Runtime.Intrinsics;
using mode13hx.Configuration;
using mode13hx.Util;
using mode13hx.Presentation;

namespace mode13hx.Presentation.Compression;

/// <summary>
/// Fixed-rate 1D wavelet compressor on 64 consecutive pixels (linear memory access).
/// 6-level integer Haar on contiguous vertical strips, 8 uints (32 bytes) output = 8:1 ratio.
/// Memory access pattern matches RLE/BL16 (sequential scan), unlike L26's strided 8x8 tiles.
/// GPU decompression via decomp_s64.comp.
/// </summary>
public sealed class FrameCompressorS64 : IFrameCompressor
{
    private const int BlockPixels = 64;
    private const int BlockUints = 8;
    private const int HaarLevels = 6; // 64 → 32 → 16 → 8 → 4 → 2 → 1

    // Per-subband quantization shift counts (all Q = power of 2)
    // Y channel (128 bits total)
    private const int SY_DC = 0;  // 8-bit unsigned, Q=1
    private const int SY_L6 = 1;  // 8-bit signed, Q=2
    private const int SY_L5 = 2;  // 6-bit signed, Q=4
    private const int SY_L4 = 3;  // 5-bit signed, Q=8
    private const int SY_L3 = 4;  // 4-bit signed, Q=16
    private const int SY_L2 = 5;  // 3-bit signed, Q=32
    // Co/Cg channels (64 bits each)
    private const int SC_DC = 1;  // 8-bit signed, Q=2
    private const int SC_L6 = 2;  // 6-bit signed, Q=4
    private const int SC_L5 = 3;  // 5-bit signed, Q=8
    private const int SC_L4 = 4;  // 4-bit signed, Q=16
    private const int SC_L3 = 5;  // 3-bit signed, Q=32

    private readonly int offset;     // pixel offset in frame buffer
    private readonly int pixelCount; // pixels this compressor handles
    private static uint gpuGroupsX;
    private static int threadCount = 16;

    public string DecompressShaderName => "decomp_s64.comp";

    // Sliced compressor instance
    private FrameCompressorS64(int offset, int count)
    {
        this.offset = offset;
        this.pixelCount = count;
    }

    // Public constructor (called from EngineWindow)
    public FrameCompressorS64(CommonOptions config)
    {
        this.offset = 0;
        this.pixelCount = 0;
        int totalPixels = config.Width * config.Height;
        int totalBlocks = totalPixels / BlockPixels;
        // Pre-multiply by 32 for VulkanRenderer's (groupsX + 31) / 32 formula
        gpuGroupsX = (uint)totalBlocks * 32;
        threadCount = Math.Max(1, config.CompressionThreads);
    }

    public void Setup(FrameDescriptor fd, int compressorDesiredCount)
    {
        int totalPixels = fd.Buffer.Width * fd.Buffer.Height;
        int totalBlocks = totalPixels / BlockPixels;

        // Slice by contiguous pixel ranges (like RLE/BL16)
        int sliceCount = Math.Min(threadCount, totalBlocks);
        int pixelsPerSlice = (totalPixels / sliceCount / BlockPixels) * BlockPixels; // align to block boundary
        int actualCount = totalPixels / pixelsPerSlice;

        fd.Compressor = new IFrameCompressor[actualCount];
        for (int i = 0; i < actualCount; ++i)
        {
            int off = i * pixelsPerSlice;
            int cnt = (i == actualCount - 1) ? totalPixels - off : pixelsPerSlice;
            fd.Compressor[i] = new FrameCompressorS64(off, cnt);
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
        string hud = $"S64 {fd.Count * sizeof(uint) / (1024.0 * 1024.0):F2}MB took:{took:F1}ms par:{parTime:F1}ms min:{min:F1} avg:{avg:F1} T:{fd.Compressor.Length}";
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
        int* temp = stackalloc int[BlockPixels];

        Vector128<uint> mask0xFF = Vector128.Create(0xFFu);

        for (int blockStart = 0; blockStart < pixelCount; blockStart += BlockPixels)
        {
            int blockIndex = (offset + blockStart) / BlockPixels;
            uint* pixels = inputPtr + blockStart;

            // Load 64 consecutive pixels + YCoCg-R (fully contiguous, perfect for cache)
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

            // Forward 1D Haar (6 levels) per channel
            ForwardHaar1D(yCoeffs, temp, BlockPixels, HaarLevels);
            ForwardHaar1D(coCoeffs, temp, BlockPixels, HaarLevels);
            ForwardHaar1D(cgCoeffs, temp, BlockPixels, HaarLevels);

            // Quantize + pack
            PackBlock(yCoeffs, coCoeffs, cgCoeffs, outputPtr + blockIndex * BlockUints);
        }
    }

    public void CompressSafe(FrameDescriptor fd) => Compress(fd);

    /// <summary>Forward 1D integer Haar lifting (in-place with temp buffer).</summary>
    internal static unsafe void ForwardHaar1D(int* data, int* temp, int size, int levels)
    {
        int n = size;
        for (int level = 0; level < levels; level++)
        {
            int half = n / 2;
            // Process pairs with Vector128 where possible
            int i = 0;
            for (; i + 3 < half; i += 4)
            {
                // Load 8 consecutive values, deinterleave to even/odd
                Vector128<int> v0 = Vector128.Load(data + 2 * i);
                Vector128<int> v1 = Vector128.Load(data + 2 * i + 4);
                Vector128<int> even = Vector128.Shuffle(v0, ShufEvenLo) | Vector128.Shuffle(v1, ShufEvenHi);
                Vector128<int> odd  = Vector128.Shuffle(v0, ShufOddLo)  | Vector128.Shuffle(v1, ShufOddHi);
                Vector128<int> d = odd - even;
                Vector128<int> s = even + Vector128.ShiftRightArithmetic(d, 1);
                s.Store(temp + i);
                d.Store(temp + half + i);
            }
            // Scalar remainder
            for (; i < half; i++)
            {
                int e = data[2 * i], o = data[2 * i + 1];
                int d = o - e;
                temp[i] = e + (d >> 1);
                temp[half + i] = d;
            }
            // Copy back
            for (int j = 0; j < n; j++) data[j] = temp[j];
            n = half;
        }
    }

    // Shuffle indices (same as L26)
    private static readonly Vector128<int> ShufEvenLo = Vector128.Create(0, 2, 4, 4);
    private static readonly Vector128<int> ShufEvenHi = Vector128.Create(4, 4, 0, 2);
    private static readonly Vector128<int> ShufOddLo  = Vector128.Create(1, 3, 4, 4);
    private static readonly Vector128<int> ShufOddHi  = Vector128.Create(4, 4, 1, 3);

    /// <summary>
    /// Quantize + pack into 8 uints (256 bits).
    /// Y(128): DC(8u) + L6(8s) + L5(2×6s) + L4(4×5s) + L3(8×4s) + L2(16×3s)
    /// Co(64): DC(8s) + L6(6s) + L5(2×5s) + L4(4×4s) + L3(8×3s)
    /// Cg(64): same as Co
    /// </summary>
    internal static unsafe void PackBlock(int* y, int* co, int* cg, uint* output)
    {
        ulong acc = 0;
        int ab = 0, wi = 0;

        // === Y channel (128 bits) ===
        // Coefficient layout after 6-level 1D Haar:
        // [DC, L6d, L5d×2, L4d×4, L3d×8, L2d×16, L1d×32]
        // Indices: [0], [1], [2..3], [4..7], [8..15], [16..31], [32..63]

        // DC: 8 bits unsigned
        acc = (uint)Clamp(y[0], 0, 255); ab = 8;

        // L6: 1 × 8 bits signed
        acc |= ((ulong)((uint)Quantize(y[1], SY_L6, 8) & 0xFFu)) << ab; ab += 8; // ab=16

        // L5: 2 × 6 bits signed
        acc |= ((ulong)((uint)Quantize(y[2], SY_L5, 6) & 0x3Fu)) << ab; ab += 6;
        acc |= ((ulong)((uint)Quantize(y[3], SY_L5, 6) & 0x3Fu)) << ab; ab += 6; // ab=28

        // L4: 4 × 5 bits signed = 20 bits
        {
            uint p = ((uint)Quantize(y[4], SY_L4, 5) & 0x1Fu)
                   | (((uint)Quantize(y[5], SY_L4, 5) & 0x1Fu) << 5)
                   | (((uint)Quantize(y[6], SY_L4, 5) & 0x1Fu) << 10)
                   | (((uint)Quantize(y[7], SY_L4, 5) & 0x1Fu) << 15);
            acc |= (ulong)p << ab; ab += 20; // ab=48 → flush
            output[wi++] = (uint)acc; acc >>= 32; ab -= 32; // ab=16
        }

        // L3: 8 × 4 bits signed = 32 bits — batch 4 at a time
        {
            uint p = ((uint)Quantize(y[8],  SY_L3, 4) & 0xFu)
                   | (((uint)Quantize(y[9],  SY_L3, 4) & 0xFu) << 4)
                   | (((uint)Quantize(y[10], SY_L3, 4) & 0xFu) << 8)
                   | (((uint)Quantize(y[11], SY_L3, 4) & 0xFu) << 12);
            acc |= (ulong)p << ab; ab += 16; // ab=32 → flush
            output[wi++] = (uint)acc; acc >>= 32; ab -= 32; // ab=0
        }
        {
            uint p = ((uint)Quantize(y[12], SY_L3, 4) & 0xFu)
                   | (((uint)Quantize(y[13], SY_L3, 4) & 0xFu) << 4)
                   | (((uint)Quantize(y[14], SY_L3, 4) & 0xFu) << 8)
                   | (((uint)Quantize(y[15], SY_L3, 4) & 0xFu) << 12);
            acc = p; ab = 16;
        }

        // L2: 16 × 3 bits signed = 48 bits — batch 4 at a time (4 batches × 12 bits)
        for (int i = 0; i < 16; i += 4)
        {
            uint p = ((uint)Quantize(y[16 + i],     SY_L2, 3) & 7u)
                   | (((uint)Quantize(y[16 + i + 1], SY_L2, 3) & 7u) << 3)
                   | (((uint)Quantize(y[16 + i + 2], SY_L2, 3) & 7u) << 6)
                   | (((uint)Quantize(y[16 + i + 3], SY_L2, 3) & 7u) << 9);
            acc |= (ulong)p << ab; ab += 12;
            if (ab >= 32) { output[wi++] = (uint)acc; acc >>= 32; ab -= 32; }
        }
        // Y L1: zeroed (32 coefficients at indices [32..63], 0 bits)

        // === Co channel (64 bits) ===
        PackChroma(co, ref acc, ref ab, ref wi, output);

        // === Cg channel (64 bits) ===
        PackChroma(cg, ref acc, ref ab, ref wi, output);

        if (ab > 0) output[wi] = (uint)acc;
    }

    /// <summary>Pack one chroma channel: DC(8s) + L6(6s) + L5(2×5s) + L4(4×4s) + L3(8×3s) = 64 bits.</summary>
    internal static unsafe void PackChroma(int* ch, ref ulong acc, ref int ab, ref int wi, uint* output)
    {
        // DC: 8 bits signed
        acc |= ((ulong)((uint)Quantize(ch[0], SC_DC, 8) & 0xFFu)) << ab; ab += 8;
        if (ab >= 32) { output[wi++] = (uint)acc; acc >>= 32; ab -= 32; }

        // L6: 1 × 6 bits
        acc |= ((ulong)((uint)Quantize(ch[1], SC_L6, 6) & 0x3Fu)) << ab; ab += 6;

        // L5: 2 × 5 bits = 10 bits
        acc |= ((ulong)((uint)Quantize(ch[2], SC_L5, 5) & 0x1Fu)) << ab; ab += 5;
        acc |= ((ulong)((uint)Quantize(ch[3], SC_L5, 5) & 0x1Fu)) << ab; ab += 5;
        if (ab >= 32) { output[wi++] = (uint)acc; acc >>= 32; ab -= 32; }

        // L4: 4 × 4 bits = 16 bits
        {
            uint p = ((uint)Quantize(ch[4], SC_L4, 4) & 0xFu)
                   | (((uint)Quantize(ch[5], SC_L4, 4) & 0xFu) << 4)
                   | (((uint)Quantize(ch[6], SC_L4, 4) & 0xFu) << 8)
                   | (((uint)Quantize(ch[7], SC_L4, 4) & 0xFu) << 12);
            acc |= (ulong)p << ab; ab += 16;
            if (ab >= 32) { output[wi++] = (uint)acc; acc >>= 32; ab -= 32; }
        }

        // L3: 8 × 3 bits = 24 bits — batch 4 at a time
        for (int i = 0; i < 8; i += 4)
        {
            uint p = ((uint)Quantize(ch[8 + i],     SC_L3, 3) & 7u)
                   | (((uint)Quantize(ch[8 + i + 1], SC_L3, 3) & 7u) << 3)
                   | (((uint)Quantize(ch[8 + i + 2], SC_L3, 3) & 7u) << 6)
                   | (((uint)Quantize(ch[8 + i + 3], SC_L3, 3) & 7u) << 9);
            acc |= (ulong)p << ab; ab += 12;
            if (ab >= 32) { output[wi++] = (uint)acc; acc >>= 32; ab -= 32; }
        }
        // L2+L1: zeroed
    }

    public unsafe void Decompress(FrameDescriptor fd)
    {
        // CPU-side decompression for testing — not used in normal operation
        throw new NotImplementedException();
    }

    // ── Shared helpers (same as L26) ──────────────────────────────────────────

    internal static int Quantize(int value, int shift, int bits)
    {
        if (shift == 0)
        {
            int mx = (1 << (bits - 1)) - 1, mn = -(1 << (bits - 1));
            return value < mn ? mn : value > mx ? mx : value;
        }
        int bias = 1 << (shift - 1);
        int biased = value >= 0 ? value + bias : value - bias;
        int correction = (biased >> 31) & ((1 << shift) - 1);
        int rounded = (biased + correction) >> shift;
        int maxVal = (1 << (bits - 1)) - 1;
        int minVal = -(1 << (bits - 1));
        return rounded < minVal ? minVal : rounded > maxVal ? maxVal : rounded;
    }

    internal static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
}
