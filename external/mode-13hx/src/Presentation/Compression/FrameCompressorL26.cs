using System.Diagnostics;
using System.Runtime.Intrinsics;
using mode13hx.Configuration;
using mode13hx.Util;
using mode13hx.Presentation;

namespace mode13hx.Presentation.Compression;

/// <summary>
/// Fixed-rate block wavelet compressor (integer Haar transform + YCoCg-R color space).
/// Each 8x8 pixel tile compresses to exactly 8 uints (32 bytes) = 8:1 ratio.
/// GPU decompression via decomp_l26.comp.
/// </summary>
public sealed class FrameCompressorL26 : IFrameCompressor
{
    private const int BlockPixels = 64; // 8x8
    private const int BlockUints = 8;   // 32 bytes per compressed block
    private const int TileSize = 8;

    // Per-subband quantization as shift counts (all Q factors are powers of 2)
    // Quantize: (value + rounding) >> shift.  Dequantize: value << shift.
    // Y channel
    private const int SY_DC = 0;    // Q=1,  8-bit unsigned, range 0-255 — no quantization
    private const int SY_L3 = 2;    // Q=4,  6-bit signed → ±124 effective range
    private const int SY_L2 = 4;    // Q=16, 4-bit signed → ±112
    private const int SY_L1 = 6;    // Q=64, 2-bit signed → ±64
    // Co/Cg channels
    private const int SC_DC = 1;    // Q=2,  8-bit signed → ±254
    private const int SC_L3 = 3;    // Q=8,  5-bit signed → ±120
    private const int SC_L2 = 5;    // Q=32, 3-bit signed → ±96

    // Texture dimensions: texW = frameHeight, texH = frameWidth (column-major buffer, transposed texture)
    // Buffer addressing: pixel(screenX, screenY) = buffer[screenX * frameHeight + screenY]
    //                   = buffer[texY * texW + texX] in texture space
    private readonly int sliceTileRowOffset; // starting tile row (along texH) for this compressor slice
    private readonly int sliceTileRowCount;  // number of tile rows this slice handles
    private readonly int texW;    // texture width = frameHeight (buffer row stride in texture space)
    private readonly int texH;    // texture height = frameWidth
    private readonly int tilesX;  // tile count along texW
    private readonly int tilesY;  // tile count along texH
    private static uint gpuGroupsX;
    private static int threadCount = 4; // controlled via --threads CLI parameter

    public string DecompressShaderName => "decomp_l26.comp";

    // Constructor for sliced compressor instances
    private FrameCompressorL26(int sliceTileRowOffset, int sliceTileRowCount, int texW, int texH, int tilesX, int tilesY)
    {
        this.sliceTileRowOffset = sliceTileRowOffset;
        this.sliceTileRowCount = sliceTileRowCount;
        this.texW = texW;
        this.texH = texH;
        this.tilesX = tilesX;
        this.tilesY = tilesY;
    }

    // Public constructor (called from EngineWindow)
    public FrameCompressorL26(CommonOptions config)
    {
        // Texture is transposed: texW = frameHeight, texH = frameWidth
        this.texW = (config.Height + TileSize - 1) / TileSize * TileSize;
        this.texH = (config.Width + TileSize - 1) / TileSize * TileSize;
        this.tilesX = this.texW / TileSize;
        this.tilesY = this.texH / TileSize;
        this.sliceTileRowOffset = 0;
        this.sliceTileRowCount = 0;
        int totalBlocks = tilesX * tilesY;
        gpuGroupsX = (uint)totalBlocks * 32;
        threadCount = Math.Max(1, config.CompressionThreads);
        parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = threadCount };
    }

    public void Setup(FrameDescriptor fd, int compressorDesiredCount)
    {
        // Texture is transposed: texW = frameHeight, texH = frameWidth
        int tw = (fd.Buffer.Height + TileSize - 1) / TileSize * TileSize;
        int th = (fd.Buffer.Width + TileSize - 1) / TileSize * TileSize;
        int txCount = tw / TileSize;
        int tyCount = th / TileSize;
        int totalBlocks = txCount * tyCount;

        // Slice count = thread count (each slice runs on one thread, no oversubscription)
        int sliceCount = Math.Min(threadCount, tyCount);
        int rowsPerCompressor = tyCount / sliceCount;
        if (rowsPerCompressor < 1) rowsPerCompressor = 1;
        int actualCount = tyCount / rowsPerCompressor;

        fd.Compressor = new IFrameCompressor[actualCount];
        for (int i = 0; i < actualCount; ++i)
        {
            int tileOff = i * rowsPerCompressor;
            int tileCnt = (i == actualCount - 1) ? tyCount - tileOff : rowsPerCompressor;
            fd.Compressor[i] = new FrameCompressorL26(tileOff, tileCnt, tw, th, txCount, tyCount);
        }

        // Fixed-rate: exactly totalBlocks * BlockUints
        fd.Blocks = new AlignedArray<uint>(totalBlocks * BlockUints);
    }

    private double min = double.Pi;
    private double took, parTime, sum, avg;
    private int fcount;
    private static ParallelOptions parallelOpts; // initialized in constructor after threadCount is set

    public void Start(FrameDescriptor fd)
    {
        Stopwatch sw = Stopwatch.StartNew();
        // Shadow + white HUD text for readability under lossy compression
        string hud = $"L26 {fd.Count * sizeof(uint) / (1024.0 * 1024.0):F2}MB took:{took:F1}ms par:{parTime:F1}ms min:{min:F1} avg:{avg:F1} T:{fd.Compressor.Length}";
        fd.Canvas.SetPenColor(0, 0, 0).DrawString(hud, 12, 3, Canvas.Font9X16);
        fd.Canvas.SetPenColor(Canvas.White).DrawString(hud, 10, 1, Canvas.Font9X16);
        fd.Count = 0;

        int blockCount = tilesX * tilesY * BlockUints;
        fd.CpuCompressionTask = Task.Run(() =>
        {
            Stopwatch parSw = Stopwatch.StartNew();
            if (fd.Compressor.Length == 1)
                fd.Compressor[0].Compress(fd); // single-thread: skip Parallel overhead entirely
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

        uint* inputPtr = fd.Buffer.Data.AsPointer() + fd.Offset;
        uint* outputPtr = fd.Blocks.AsPointer();
        // Column-major buffer: pixel(screenX, screenY) = buffer[screenX * frameHeight + screenY]
        // In texture space: buffer[texY * texW + texX] where texW = frameHeight
        int frameHeight = fd.Buffer.Height; // = texW = buffer stride in texture space
        int frameWidth = fd.Buffer.Width;   // = texH

        // Temp buffers for one tile (stack-allocated)
        int* yCoeffs = stackalloc int[BlockPixels];
        int* coCoeffs = stackalloc int[BlockPixels];
        int* cgCoeffs = stackalloc int[BlockPixels];
        uint* packed = stackalloc uint[BlockUints];

        for (int tyRel = 0; tyRel < sliceTileRowCount; tyRel++)
        {
            int ty = sliceTileRowOffset + tyRel;
            for (int tx = 0; tx < tilesX; tx++)
            {
                int blockIndex = ty * tilesX + tx;
                int texBaseX = tx * TileSize;  // texture X = screenY direction
                int texBaseY = ty * TileSize;  // texture Y = screenX direction

                // Load 8x8 tile in texture space, convert RGB→YCoCg-R
                if (texBaseX + TileSize <= frameHeight && texBaseY + TileSize <= frameWidth)
                {
                    // Fast path: interior tile, no bounds checking, Vector128 pixel load + color convert
                    Vector128<uint> mask0xFF = Vector128.Create(0xFFu);
                    for (int row = 0; row < TileSize; row++)
                    {
                        uint* rowSrc = inputPtr + (texBaseY + row) * frameHeight + texBaseX;
                        int idx = row * TileSize;
                        for (int col = 0; col < TileSize; col += 4)
                        {
                            Vector128<uint> pixels = Vector128.Load(rowSrc + col);
                            Vector128<int> r = (pixels & mask0xFF).AsInt32();
                            Vector128<int> g = (Vector128.ShiftRightLogical(pixels, 8) & mask0xFF).AsInt32();
                            Vector128<int> b = (Vector128.ShiftRightLogical(pixels, 16) & mask0xFF).AsInt32();
                            Vector128<int> co = r - b;
                            Vector128<int> t = b + Vector128.ShiftRightArithmetic(co, 1);
                            Vector128<int> cg = g - t;
                            Vector128<int> y = t + Vector128.ShiftRightArithmetic(cg, 1);
                            y.Store(yCoeffs + idx + col);
                            co.Store(coCoeffs + idx + col);
                            cg.Store(cgCoeffs + idx + col);
                        }
                    }
                }
                else
                {
                    // Edge tiles: scalar with clamping
                    for (int row = 0; row < TileSize; row++)
                    {
                        for (int col = 0; col < TileSize; col++)
                        {
                            int clX = Math.Min(texBaseX + col, frameHeight - 1);
                            int clY = Math.Min(texBaseY + row, frameWidth - 1);
                            uint pixel = inputPtr[clY * frameHeight + clX];
                            int r = (int)(pixel & 0xFFu);
                            int g = (int)((pixel >> 8) & 0xFFu);
                            int bx = (int)((pixel >> 16) & 0xFFu);
                            int co = r - bx;
                            int t = bx + (co >> 1);
                            int cg = g - t;
                            int idx = row * TileSize + col;
                            yCoeffs[idx] = t + (cg >> 1);
                            coCoeffs[idx] = co;
                            cgCoeffs[idx] = cg;
                        }
                    }
                }

                // Forward 2D Haar (3 levels) per channel
                ForwardHaar2D(yCoeffs, TileSize, 3);
                ForwardHaar2D(coCoeffs, TileSize, 3);
                ForwardHaar2D(cgCoeffs, TileSize, 3);

                // Quantize + pack
                PackBlock(yCoeffs, coCoeffs, cgCoeffs, packed);

                // Write to output (fixed position, no atomics needed)
                uint* dest = outputPtr + blockIndex * BlockUints;
                for (int i = 0; i < BlockUints; i++)
                    dest[i] = packed[i];
            }
        }
    }

    public void CompressSafe(FrameDescriptor fd) => Compress(fd); // scalar implementation is already safe

    // Shuffle indices for deinterleaving 8 ints (2×Vector128) into even/odd
    private static readonly Vector128<int> ShufEvenLo = Vector128.Create(0, 2, 4, 4); // {a0,a2, 0, 0} (idx>=4 → 0)
    private static readonly Vector128<int> ShufEvenHi = Vector128.Create(4, 4, 0, 2); // { 0, 0,a4,a6}
    private static readonly Vector128<int> ShufOddLo  = Vector128.Create(1, 3, 4, 4); // {a1,a3, 0, 0}
    private static readonly Vector128<int> ShufOddHi  = Vector128.Create(4, 4, 1, 3); // { 0, 0,a5,a7}

    /// <summary>
    /// Forward 2D integer Haar lifting, specialized for 8x8 with Vector128.
    /// Level 1 rows: shuffle-deinterleave. Column transforms: 4 columns at a time.
    /// Levels 2-3: scalar (small sizes, low impact).
    /// </summary>
    internal static unsafe void ForwardHaar2D(int* c, int size, int levels)
    {
        // ── Level 1 (n=8) ──
        // Row transforms: 8 rows, deinterleave with Vector128 shuffle
        for (int row = 0; row < 8; row++)
        {
            int* p = c + row * 8;
            Vector128<int> v0 = Vector128.Load(p);
            Vector128<int> v1 = Vector128.Load(p + 4);
            Vector128<int> even = Vector128.Shuffle(v0, ShufEvenLo) | Vector128.Shuffle(v1, ShufEvenHi);
            Vector128<int> odd  = Vector128.Shuffle(v0, ShufOddLo)  | Vector128.Shuffle(v1, ShufOddHi);
            Vector128<int> d = odd - even;
            Vector128<int> s = even + Vector128.ShiftRightArithmetic(d, 1);
            s.Store(p);     // smooth → [0..3]
            d.Store(p + 4); // detail → [4..7]
        }
        // Column transforms: process 4 columns at a time (2 passes: cols 0-3, cols 4-7)
        for (int colBase = 0; colBase < 8; colBase += 4)
        {
            // Load all 8 rows for these 4 columns into vectors
            Vector128<int> r0 = Vector128.Load(c + 0 * 8 + colBase);
            Vector128<int> r1 = Vector128.Load(c + 1 * 8 + colBase);
            Vector128<int> r2 = Vector128.Load(c + 2 * 8 + colBase);
            Vector128<int> r3 = Vector128.Load(c + 3 * 8 + colBase);
            Vector128<int> r4 = Vector128.Load(c + 4 * 8 + colBase);
            Vector128<int> r5 = Vector128.Load(c + 5 * 8 + colBase);
            Vector128<int> r6 = Vector128.Load(c + 6 * 8 + colBase);
            Vector128<int> r7 = Vector128.Load(c + 7 * 8 + colBase);
            // Pairs: (r0,r1), (r2,r3), (r4,r5), (r6,r7) → smooth rows 0-3, detail rows 4-7
            Vector128<int> d0 = r1 - r0; Vector128<int> s0 = r0 + Vector128.ShiftRightArithmetic(d0, 1);
            Vector128<int> d1 = r3 - r2; Vector128<int> s1 = r2 + Vector128.ShiftRightArithmetic(d1, 1);
            Vector128<int> d2 = r5 - r4; Vector128<int> s2 = r4 + Vector128.ShiftRightArithmetic(d2, 1);
            Vector128<int> d3 = r7 - r6; Vector128<int> s3 = r6 + Vector128.ShiftRightArithmetic(d3, 1);
            s0.Store(c + 0 * 8 + colBase); s1.Store(c + 1 * 8 + colBase);
            s2.Store(c + 2 * 8 + colBase); s3.Store(c + 3 * 8 + colBase);
            d0.Store(c + 4 * 8 + colBase); d1.Store(c + 5 * 8 + colBase);
            d2.Store(c + 6 * 8 + colBase); d3.Store(c + 7 * 8 + colBase);
        }

        // ── Level 2 (n=4, top-left 4x4 quadrant) ──
        // Row transforms: 4 rows of 4 values
        for (int row = 0; row < 4; row++)
        {
            int* p = c + row * 8;
            int e0 = p[0], o0 = p[1], e1 = p[2], o1 = p[3];
            int d0x = o0 - e0, d1x = o1 - e1;
            p[0] = e0 + (d0x >> 1); p[1] = e1 + (d1x >> 1);
            p[2] = d0x; p[3] = d1x;
        }
        // Column transforms: 4 columns, process all at once
        {
            Vector128<int> r0 = Vector128.Load(c + 0 * 8);
            Vector128<int> r1 = Vector128.Load(c + 1 * 8);
            Vector128<int> r2 = Vector128.Load(c + 2 * 8);
            Vector128<int> r3 = Vector128.Load(c + 3 * 8);
            Vector128<int> d0 = r1 - r0; Vector128<int> s0 = r0 + Vector128.ShiftRightArithmetic(d0, 1);
            Vector128<int> d1 = r3 - r2; Vector128<int> s1 = r2 + Vector128.ShiftRightArithmetic(d1, 1);
            s0.Store(c + 0 * 8); s1.Store(c + 1 * 8);
            d0.Store(c + 2 * 8); d1.Store(c + 3 * 8);
        }

        // ── Level 3 (n=2, top-left 2x2) ──
        // Row transforms: 2 rows of 2 values
        {
            int e = c[0], o = c[1]; int dx = o - e;
            c[0] = e + (dx >> 1); c[1] = dx;
            e = c[8]; o = c[9]; dx = o - e;
            c[8] = e + (dx >> 1); c[9] = dx;
        }
        // Column transforms: 2 columns
        {
            int e0 = c[0], o0 = c[8]; int d0 = o0 - e0;
            c[0] = e0 + (d0 >> 1); c[8] = d0;
            int e1 = c[1], o1 = c[9]; int d1 = o1 - e1;
            c[1] = e1 + (d1 >> 1); c[9] = d1;
        }
    }

    /// <summary>
    /// Inverse 2D integer Haar lifting (in-place).
    /// </summary>
    private static unsafe void InverseHaar2D(int* coeffs, int size, int levels)
    {
        int* temp = stackalloc int[size];
        int n = size >> levels; // start from smallest subband
        for (int level = levels - 1; level >= 0; level--)
        {
            n *= 2;
            // Columns
            for (int col = 0; col < n; col++)
            {
                int half = n / 2;
                for (int i = 0; i < half; i++)
                {
                    int s = coeffs[i * size + col];
                    int d = coeffs[(half + i) * size + col];
                    int even = s - (d >> 1);
                    int odd = d + even;
                    temp[2 * i] = even;
                    temp[2 * i + 1] = odd;
                }
                for (int i = 0; i < n; i++) coeffs[i * size + col] = temp[i];
            }
            // Rows
            for (int row = 0; row < n; row++)
            {
                int* rowPtr = coeffs + row * size;
                int half = n / 2;
                for (int i = 0; i < half; i++)
                {
                    int s = rowPtr[i];
                    int d = rowPtr[half + i];
                    int even = s - (d >> 1);
                    int odd = d + even;
                    temp[2 * i] = even;
                    temp[2 * i + 1] = odd;
                }
                for (int i = 0; i < n; i++) rowPtr[i] = temp[i];
            }
        }
    }

    /// <summary>
    /// Quantize wavelet coefficients and pack into 8 uints (256 bits).
    /// Uses 64-bit accumulator to avoid per-coefficient word-boundary checks.
    /// Batches same-width coefficients (4×4bit → 16bit chunk, 4×2bit → 8bit chunk).
    /// </summary>
    internal static unsafe void PackBlock(int* y, int* co, int* cg, uint* output)
    {
        ulong acc = 0;
        int ab = 0;  // accumulated bits
        int wi = 0;  // output word index

        // === Y channel (138 bits) ===

        // Y DC: 8 bits unsigned
        acc = (uint)Clamp(y[0], 0, 255); ab = 8;

        // Y L3: 3 × 6 bits (LH3, HL3, HH3)
        acc |= ((ulong)((uint)Quantize(y[1],  SY_L3, 6) & 0x3Fu)) << ab; ab += 6;
        acc |= ((ulong)((uint)Quantize(y[8],  SY_L3, 6) & 0x3Fu)) << ab; ab += 6;
        acc |= ((ulong)((uint)Quantize(y[9],  SY_L3, 6) & 0x3Fu)) << ab; ab += 6; // ab=26

        // Y L2: 12 × 4 bits — batch 4 coefficients into 16-bit chunks (3 batches)
        // LH2: [0,2],[0,3],[1,2],[1,3]
        {
            uint p = ((uint)Quantize(y[2],  SY_L2, 4) & 0xFu)
                   | (((uint)Quantize(y[3],  SY_L2, 4) & 0xFu) << 4)
                   | (((uint)Quantize(y[10], SY_L2, 4) & 0xFu) << 8)
                   | (((uint)Quantize(y[11], SY_L2, 4) & 0xFu) << 12);
            acc |= (ulong)p << ab; ab += 16; // ab=42 → flush
            output[wi++] = (uint)acc; acc >>= 32; ab -= 32; // ab=10
        }
        // HL2: [2,0],[2,1],[3,0],[3,1]
        {
            uint p = ((uint)Quantize(y[16], SY_L2, 4) & 0xFu)
                   | (((uint)Quantize(y[17], SY_L2, 4) & 0xFu) << 4)
                   | (((uint)Quantize(y[24], SY_L2, 4) & 0xFu) << 8)
                   | (((uint)Quantize(y[25], SY_L2, 4) & 0xFu) << 12);
            acc |= (ulong)p << ab; ab += 16; // ab=26
        }
        // HH2: [2,2],[2,3],[3,2],[3,3]
        {
            uint p = ((uint)Quantize(y[18], SY_L2, 4) & 0xFu)
                   | (((uint)Quantize(y[19], SY_L2, 4) & 0xFu) << 4)
                   | (((uint)Quantize(y[26], SY_L2, 4) & 0xFu) << 8)
                   | (((uint)Quantize(y[27], SY_L2, 4) & 0xFu) << 12);
            acc |= (ulong)p << ab; ab += 16; // ab=42 → flush
            output[wi++] = (uint)acc; acc >>= 32; ab -= 32; // ab=10
        }

        // Y L1 LH1: 16 × 2 bits — batch 4 coefficients into 8-bit chunks (4 batches)
        // rows [0..3], cols [4..7]
        for (int r = 0; r < 4; r++)
        {
            int* p = y + r * 8 + 4;
            uint pk = ((uint)Quantize(p[0], SY_L1, 2) & 3u)
                    | (((uint)Quantize(p[1], SY_L1, 2) & 3u) << 2)
                    | (((uint)Quantize(p[2], SY_L1, 2) & 3u) << 4)
                    | (((uint)Quantize(p[3], SY_L1, 2) & 3u) << 6);
            acc |= (ulong)pk << ab; ab += 8;
            if (ab >= 32) { output[wi++] = (uint)acc; acc >>= 32; ab -= 32; }
        }

        // Y L1 HL1: 16 × 2 bits — rows [4..7], cols [0..3]
        for (int r = 4; r < 8; r++)
        {
            int* p = y + r * 8;
            uint pk = ((uint)Quantize(p[0], SY_L1, 2) & 3u)
                    | (((uint)Quantize(p[1], SY_L1, 2) & 3u) << 2)
                    | (((uint)Quantize(p[2], SY_L1, 2) & 3u) << 4)
                    | (((uint)Quantize(p[3], SY_L1, 2) & 3u) << 6);
            acc |= (ulong)pk << ab; ab += 8;
            if (ab >= 32) { output[wi++] = (uint)acc; acc >>= 32; ab -= 32; }
        }

        // === Co channel (59 bits) ===
        PackChroma(co, ref acc, ref ab, ref wi, output);

        // === Cg channel (59 bits) ===
        PackChroma(cg, ref acc, ref ab, ref wi, output);

        // Flush remaining bits
        if (ab > 0) output[wi] = (uint)acc;
    }

    /// <summary>Pack one chroma channel: DC(8s) + L3(3×5s) + L2(12×3s) = 59 bits.</summary>
    internal static unsafe void PackChroma(int* ch, ref ulong acc, ref int ab, ref int wi, uint* output)
    {
        // DC: 8 bits signed
        acc |= ((ulong)((uint)Quantize(ch[0], SC_DC, 8) & 0xFFu)) << ab; ab += 8;
        if (ab >= 32) { output[wi++] = (uint)acc; acc >>= 32; ab -= 32; }

        // L3: 3 × 5 bits (LH3, HL3, HH3)
        acc |= ((ulong)((uint)Quantize(ch[1], SC_L3, 5) & 0x1Fu)) << ab; ab += 5;
        acc |= ((ulong)((uint)Quantize(ch[8], SC_L3, 5) & 0x1Fu)) << ab; ab += 5;
        acc |= ((ulong)((uint)Quantize(ch[9], SC_L3, 5) & 0x1Fu)) << ab; ab += 5;
        if (ab >= 32) { output[wi++] = (uint)acc; acc >>= 32; ab -= 32; }

        // L2: 12 × 3 bits — batch 4 coefficients into 12-bit chunks (3 batches)
        // LH2: [0,2],[0,3],[1,2],[1,3]
        {
            uint p = ((uint)Quantize(ch[2],  SC_L2, 3) & 7u)
                   | (((uint)Quantize(ch[3],  SC_L2, 3) & 7u) << 3)
                   | (((uint)Quantize(ch[10], SC_L2, 3) & 7u) << 6)
                   | (((uint)Quantize(ch[11], SC_L2, 3) & 7u) << 9);
            acc |= (ulong)p << ab; ab += 12;
            if (ab >= 32) { output[wi++] = (uint)acc; acc >>= 32; ab -= 32; }
        }
        // HL2: [2,0],[2,1],[3,0],[3,1]
        {
            uint p = ((uint)Quantize(ch[16], SC_L2, 3) & 7u)
                   | (((uint)Quantize(ch[17], SC_L2, 3) & 7u) << 3)
                   | (((uint)Quantize(ch[24], SC_L2, 3) & 7u) << 6)
                   | (((uint)Quantize(ch[25], SC_L2, 3) & 7u) << 9);
            acc |= (ulong)p << ab; ab += 12;
            if (ab >= 32) { output[wi++] = (uint)acc; acc >>= 32; ab -= 32; }
        }
        // HH2: [2,2],[2,3],[3,2],[3,3]
        {
            uint p = ((uint)Quantize(ch[18], SC_L2, 3) & 7u)
                   | (((uint)Quantize(ch[19], SC_L2, 3) & 7u) << 3)
                   | (((uint)Quantize(ch[26], SC_L2, 3) & 7u) << 6)
                   | (((uint)Quantize(ch[27], SC_L2, 3) & 7u) << 9);
            acc |= (ulong)p << ab; ab += 12;
            if (ab >= 32) { output[wi++] = (uint)acc; acc >>= 32; ab -= 32; }
        }
    }

    /// <summary>
    /// Unpack 8 uints back into wavelet coefficients (CPU-side, for testing).
    /// </summary>
    private static unsafe void UnpackBlock(uint* input, int* y, int* co, int* cg)
    {
        // Zero everything (HH1 for Y and all L1 for Co/Cg are not stored)
        for (int i = 0; i < BlockPixels; i++) { y[i] = 0; co[i] = 0; cg[i] = 0; }

        int bitPos = 0;

        // === Y channel ===
        y[0] = (int)ReadBits(input, ref bitPos, 8); // DC unsigned
        y[0 * 8 + 1] = Dequantize(ReadBitsSigned(input, ref bitPos, 6), SY_L3);
        y[1 * 8 + 0] = Dequantize(ReadBitsSigned(input, ref bitPos, 6), SY_L3);
        y[1 * 8 + 1] = Dequantize(ReadBitsSigned(input, ref bitPos, 6), SY_L3);
        for (int r = 0; r < 2; r++)
            for (int c = 2; c < 4; c++)
                y[r * 8 + c] = Dequantize(ReadBitsSigned(input, ref bitPos, 4), SY_L2);
        for (int r = 2; r < 4; r++)
            for (int c = 0; c < 2; c++)
                y[r * 8 + c] = Dequantize(ReadBitsSigned(input, ref bitPos, 4), SY_L2);
        for (int r = 2; r < 4; r++)
            for (int c = 2; c < 4; c++)
                y[r * 8 + c] = Dequantize(ReadBitsSigned(input, ref bitPos, 4), SY_L2);
        for (int r = 0; r < 4; r++)
            for (int c = 4; c < 8; c++)
                y[r * 8 + c] = Dequantize(ReadBitsSigned(input, ref bitPos, 2), SY_L1);
        for (int r = 4; r < 8; r++)
            for (int c = 0; c < 4; c++)
                y[r * 8 + c] = Dequantize(ReadBitsSigned(input, ref bitPos, 2), SY_L1);

        // === Co channel ===
        co[0] = Dequantize(ReadBitsSigned(input, ref bitPos, 8), SC_DC);
        co[0 * 8 + 1] = Dequantize(ReadBitsSigned(input, ref bitPos, 5), SC_L3);
        co[1 * 8 + 0] = Dequantize(ReadBitsSigned(input, ref bitPos, 5), SC_L3);
        co[1 * 8 + 1] = Dequantize(ReadBitsSigned(input, ref bitPos, 5), SC_L3);
        for (int r = 0; r < 2; r++)
            for (int c = 2; c < 4; c++)
                co[r * 8 + c] = Dequantize(ReadBitsSigned(input, ref bitPos, 3), SC_L2);
        for (int r = 2; r < 4; r++)
            for (int c = 0; c < 2; c++)
                co[r * 8 + c] = Dequantize(ReadBitsSigned(input, ref bitPos, 3), SC_L2);
        for (int r = 2; r < 4; r++)
            for (int c = 2; c < 4; c++)
                co[r * 8 + c] = Dequantize(ReadBitsSigned(input, ref bitPos, 3), SC_L2);

        // === Cg channel ===
        cg[0] = Dequantize(ReadBitsSigned(input, ref bitPos, 8), SC_DC);
        cg[0 * 8 + 1] = Dequantize(ReadBitsSigned(input, ref bitPos, 5), SC_L3);
        cg[1 * 8 + 0] = Dequantize(ReadBitsSigned(input, ref bitPos, 5), SC_L3);
        cg[1 * 8 + 1] = Dequantize(ReadBitsSigned(input, ref bitPos, 5), SC_L3);
        for (int r = 0; r < 2; r++)
            for (int c = 2; c < 4; c++)
                cg[r * 8 + c] = Dequantize(ReadBitsSigned(input, ref bitPos, 3), SC_L2);
        for (int r = 2; r < 4; r++)
            for (int c = 0; c < 2; c++)
                cg[r * 8 + c] = Dequantize(ReadBitsSigned(input, ref bitPos, 3), SC_L2);
        for (int r = 2; r < 4; r++)
            for (int c = 2; c < 4; c++)
                cg[r * 8 + c] = Dequantize(ReadBitsSigned(input, ref bitPos, 3), SC_L2);
    }

    public unsafe void Decompress(FrameDescriptor fd)
    {
        uint* blocksPtr = fd.Blocks.AsPointer();
        uint* outputPtr = fd.Buffer.Data.AsPointer() + fd.Offset;
        int frameHeight = fd.Buffer.Height; // = texW = buffer stride
        int frameWidth = fd.Buffer.Width;   // = texH
        int tw = (frameHeight + TileSize - 1) / TileSize * TileSize;
        int th = (frameWidth + TileSize - 1) / TileSize * TileSize;
        int txCount = tw / TileSize;
        int tyCount = th / TileSize;

        int* yC = stackalloc int[BlockPixels];
        int* coC = stackalloc int[BlockPixels];
        int* cgC = stackalloc int[BlockPixels];

        for (int ty = 0; ty < tyCount; ty++)
        {
            for (int tx = 0; tx < txCount; tx++)
            {
                int blockIndex = ty * txCount + tx;
                UnpackBlock(blocksPtr + blockIndex * BlockUints, yC, coC, cgC);

                InverseHaar2D(yC, TileSize, 3);
                InverseHaar2D(coC, TileSize, 3);
                InverseHaar2D(cgC, TileSize, 3);

                int texBaseX = tx * TileSize;
                int texBaseY = ty * TileSize;
                for (int row = 0; row < TileSize; row++)
                {
                    for (int col = 0; col < TileSize; col++)
                    {
                        int texX = texBaseX + col;
                        int texY = texBaseY + row;
                        if (texX >= frameHeight || texY >= frameWidth) continue;

                        int idx = row * TileSize + col;
                        int yVal = yC[idx];
                        int coVal = coC[idx];
                        int cgVal = cgC[idx];

                        // Inverse YCoCg-R → RGB
                        int t = yVal - (cgVal >> 1);
                        int g = cgVal + t;
                        int bVal = t - (coVal >> 1);
                        int r = coVal + bVal;

                        r = Clamp(r, 0, 255);
                        g = Clamp(g, 0, 255);
                        bVal = Clamp(bVal, 0, 255);

                        // Column-major: buffer[screenX * frameHeight + screenY] where screenX=texY, screenY=texX
                        outputPtr[texY * frameHeight + texX] = (uint)r | ((uint)g << 8) | ((uint)bVal << 16) | 0xFF000000u;
                    }
                }
            }
        }
    }

    // ── Bit packing helpers ──────────────────────────────────────────────────

    private static unsafe void WriteBits(uint* buf, ref int bitPos, uint value, int bits)
    {
        int wordIdx = bitPos >> 5;
        int bitIdx = bitPos & 31;
        uint mask = (1u << bits) - 1;
        value &= mask;
        buf[wordIdx] |= value << bitIdx;
        if (bitIdx + bits > 32) // spans two words
            buf[wordIdx + 1] |= value >> (32 - bitIdx);
        bitPos += bits;
    }

    private static unsafe void WriteBitsSigned(uint* buf, ref int bitPos, int value, int bits)
    {
        uint mask = (1u << bits) - 1;
        WriteBits(buf, ref bitPos, (uint)value & mask, bits);
    }

    private static unsafe uint ReadBits(uint* buf, ref int bitPos, int bits)
    {
        int wordIdx = bitPos >> 5;
        int bitIdx = bitPos & 31;
        uint mask = (1u << bits) - 1;
        uint value = (buf[wordIdx] >> bitIdx) & mask;
        if (bitIdx + bits > 32)
            value |= (buf[wordIdx + 1] << (32 - bitIdx)) & mask;
        bitPos += bits;
        return value;
    }

    private static unsafe int ReadBitsSigned(uint* buf, ref int bitPos, int bits)
    {
        uint raw = ReadBits(buf, ref bitPos, bits);
        // Sign-extend: if top bit is set, fill upper bits with 1s
        if ((raw & (1u << (bits - 1))) != 0)
            raw |= ~((1u << bits) - 1);
        return (int)raw;
    }

    /// <summary>Quantize using arithmetic shift (Q must be power of 2). Rounds to nearest, clamps to signed bit range.</summary>
    internal static int Quantize(int value, int shift, int bits)
    {
        if (shift == 0) // Q=1, no quantization
        {
            int mx = (1 << (bits - 1)) - 1, mn = -(1 << (bits - 1));
            return value < mn ? mn : value > mx ? mx : value;
        }
        // Round-to-nearest: add Q/2 bias (positive) or subtract Q/2 bias (negative)
        int bias = 1 << (shift - 1);
        int biased = value >= 0 ? value + bias : value - bias;
        // Arithmetic >> rounds toward -inf, but we need truncation toward zero (matching C# division).
        // Fix: for negative biased values, add (Q-1) before shifting to get ceiling instead of floor.
        int correction = (biased >> 31) & ((1 << shift) - 1);
        int rounded = (biased + correction) >> shift;
        int maxVal = (1 << (bits - 1)) - 1;
        int minVal = -(1 << (bits - 1));
        return rounded < minVal ? minVal : rounded > maxVal ? maxVal : rounded;
    }
    private static int Dequantize(int value, int shift) => value << shift;
    internal static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
}
