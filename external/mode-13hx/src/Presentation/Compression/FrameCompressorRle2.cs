using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using mode13hx.Util;
using mode13hx.Presentation;

namespace mode13hx.Presentation.Compression;

// Rle based compression optimized by 256bit vectorized comparison (which might compress 8 identical pixels in one go).
// Produces compressed blocks of pixels, for suppressed pixels the alpha channel has value of 0xFF, for copied pixels the alpha channel has value of 0x00, all other values are the number of additional repetitions of the pixel.
// Compressed frame could have largely variable number of blocks, but the block size is constant. On a GPU side the decompression is done in parallel, thats the reason for offset stored in the block.
// This is problematic for GPU, as each block could produce vastly different number of decompressed pixels.
public sealed class FrameCompressorRle2(int offset = 0, int count = 0) : IFrameCompressor
{
    public const int PixelsPerBlock = 15; // optimum for CPU is around 63 (for 64x4 byte block) but GPU is happier with larger number of blocks
    public const int BlockSize = PixelsPerBlock + 1; // uint offset + pixels
    
    private const int VecSize = 16; // works about the same with 256 and 512bit vectors, 256 (8 uints per vector) feels fastest and it is going to be more compatible
    private const int MaxRep = 192; //254 // Max length of RLE for 1 pixel, lower value balances GPU work per thread better but increases frame size and CPU work, must be between 1 and 254
    
    private readonly AlignedArray<uint> currentBlock = new(BlockSize);
    private int currentCount; // pixels already in a block
    private int currentRep; // current (last in a current block) pixel repetition count
    
    public string DecompressShaderName => "decomp_rle2.comp";

    public void Setup(FrameDescriptor fd, int compressorDesiredCount)
    {
        // slice the frame here
        int cbs = fd.Buffer.Width/compressorDesiredCount; // compression block column count (resolution width must be divisible by this number) 
        while (fd.Buffer.Width % cbs != 0) { cbs++; }; // check/adjust Cbs to work with different resolutions, it will drop to 1 compressor in worst case
        fd.Compressor = new IFrameCompressor[fd.Buffer.Width/cbs];
        for (int i = 0; i < fd.Compressor.Length; ++i) { fd.Compressor[i] = new FrameCompressorRle2(i * cbs*fd.Buffer.Height, (cbs-0)*fd.Buffer.Height); }; // create compressor collection per frame descriptor (it is possible to compress multiple frames in parallel)
    
        // could be 1/VecSize bigger in the worst case (no compressed blocks and a copy of the whole frame), additional space added because of preallocation done by chunks
        int maximumSize =  (1 + fd.Buffer.FrameSize / PixelsPerBlock) * BlockSize;
        fd.Blocks = new AlignedArray<uint>(maximumSize);
    }

    private double min = double.Pi;
    private double took, sum = 0, avg = 0;
    private int fcount = 0;
    // start to compress frame in a task awaited by a frame flipping thread
    // TODO: make the task cancellable (e.g.: for frame drop enabled)
    public void Start(FrameDescriptor fd)
    {
        Stopwatch sw = Stopwatch.StartNew();
        fd.Canvas.SetPenColor(Canvas.White).DrawString($"Compression block data: {fd.Count * sizeof(uint) / (1024.0 * 1024.0) :F2} MB, took:{took:F2}ms min:{min:F2} avg:{avg:f4}ms Compressors: {fd.Compressor.Length}", 10, 1, Canvas.Font9X16); // prints timing for previous frame
        fd.Count = 0;
         
        fd.CpuCompressionTask = Task.Run(() =>
        {
            ParallelOptions po = new() { MaxDegreeOfParallelism = 4 }; // TODO: replace with manually managed/synchronized threads
            Parallel.ForEach(fd.Compressor, po, c => { c.Compress(fd); });
            took = sw.Elapsed.TotalMilliseconds; // TODO: move telemetry to the task, add facilities to measure compression related stats to FrametimeComponent
            sum+=took; fcount++; //TODO: race condition here
            avg = sum / fcount;
            if (min > took) min = took;
        });
    }

    // variant 1, safe version
    public void CompressSafe(FrameDescriptor fd)
    {
        int inputOffset = fd.Offset + offset; // inclusive
        int endOffset = fd.Offset + offset + count; // inputOffset + frame.Buffer.FrameSize; // exclusive
        int outputOffset = offset;
    
        // ST for PoC
        currentCount = 0;
        currentRep = 0; // current pixel repetition count
        currentBlock[0] = (uint)outputOffset;
        
        // loop through all pixels
        while (inputOffset < endOffset)
        {
            if (currentCount == 0) { currentBlock[++currentCount] = fd.Buffer.Data[inputOffset++]; outputOffset++; continue; }
            
            uint fbPixel = fd.Buffer.Data[inputOffset];
            if (currentBlock[currentCount] != fbPixel || currentRep == MaxRep)
            { 
                // flush pixel
                if (currentRep > 0)
                {
                    Func.DecodePixelColor(currentBlock[currentCount], out int r, out int g, out int b);
                    currentBlock[currentCount] = Func.EncodePixelColorRgba(r, g, b, currentRep);
                    currentRep = 0;
                }
                
                // flush block if full
                if (currentCount == PixelsPerBlock) { Flush(fd); currentBlock[0] = (uint)outputOffset; currentCount = 0; continue; }
            }
            
            if (currentBlock[currentCount] == fbPixel) { currentRep++; inputOffset++; outputOffset++; continue; }
            currentBlock[++currentCount] = fd.Buffer.Data[inputOffset++]; outputOffset++;
        }
        
        // flush last pixel
        Func.DecodePixelColor(currentBlock[currentCount], out int r1, out int g1, out int b1);
        currentBlock[currentCount] = Func.EncodePixelColorRgba(r1, g1, b1, currentRep);
        
        Flush(fd); // flush last block
        fd.GpuDecompressionGroupsX = (uint)(fd.Count / BlockSize);
        fd.GpuDecompressionGroupsY = PixelsPerBlock;
    }

    public unsafe void Compress(FrameDescriptor fd)
    {
        if (!Avx512F.IsSupported) { CompressVector128(fd); return; }

        int inputOffset = fd.Offset + offset; // inclusive
        int endOffset = fd.Offset + offset + count; // inputOffset + frame.Buffer.FrameSize; // exclusive
        currentCount = 0;
        currentRep = 0; // Current pixel repetition count
        int outputOffset = offset;
        uint* currentBlockPtr = currentBlock.AsPointer();
        currentBlockPtr[0] = (uint)outputOffset;

        int vectorIndex = VecSize; // Index within the current vector (0 to VecSize-1 or VecSize of vector was processed)
        Vector512<uint> pixelVector = default;
        uint * pixelVectorPtr = stackalloc uint[VecSize];
        uint* inputPtr = fd.Buffer.Data.AsPointer();
        
        while (inputOffset < endOffset)
        {
            if (vectorIndex == VecSize)
            {
                // Load pixels into the vector
                if (inputOffset + VecSize < endOffset) 
                { 
                    pixelVector = Avx512F.LoadVector512(inputPtr + inputOffset);
                    pixelVector.Store(pixelVectorPtr); 
                }
                inputOffset += VecSize;
                vectorIndex = 0;
            }
                
            // Check if the complete vector or its part matches the previous pixel -> very quick resolution of multiple pixels :)
            if (currentCount != 0)
            {
                Vector512<uint> currentPixel = Vector512.Create(currentBlockPtr[currentCount]);
                switch (vectorIndex)
                {
                    // complete 512 bit vector matches
                    case 0 when currentRep + VecSize <= MaxRep && pixelVector.Equals(currentPixel):
                        currentRep += VecSize;
                        outputOffset += VecSize;
                        vectorIndex = VecSize;
                        continue;
                    // first half of the vector matches
                    case 0 when currentRep + VecSize/2 <= MaxRep && pixelVector.GetLower().Equals(currentPixel.GetLower()):
                        currentRep += VecSize/2;
                        outputOffset += VecSize/2;
                        vectorIndex = VecSize/2;
                        continue;
                    // first quarter of the vector matches
                    case 0 when currentRep + VecSize/4 <= MaxRep && pixelVector.GetLower().GetLower().Equals(currentPixel.GetLower().GetLower()):
                        currentRep += VecSize/4;
                        outputOffset += VecSize/4;
                        vectorIndex = VecSize/4;
                        continue;
                    // second quarter of the vector matches
                    case VecSize/4 when pixelVector.GetLower().GetUpper().Equals(currentPixel.GetLower().GetLower()) && currentRep + VecSize/4 <= MaxRep:
                        currentRep += VecSize/4;
                        outputOffset += VecSize/4;
                        vectorIndex = VecSize/2;
                        continue;
                    // second half of the vector matches
                    case VecSize/2 when pixelVector.GetUpper().Equals(currentPixel.GetLower()) && currentRep + VecSize/2 <= MaxRep:
                        currentRep += VecSize/2;
                        outputOffset += VecSize/2;
                        vectorIndex = VecSize;
                        continue;
                    // 3rd quarter of the vector matches
                    case VecSize/2 when pixelVector.GetUpper().GetLower().Equals(currentPixel.GetLower().GetLower()) && currentRep + VecSize/4 <= MaxRep:
                        currentRep += VecSize/4;
                        outputOffset += VecSize/4;
                        vectorIndex = VecSize/2 + VecSize/4;
                        continue;
                    // 4th quarter of the vector matches
                    case VecSize/2 + VecSize/4 when pixelVector.GetUpper().GetUpper().Equals(currentPixel.GetLower().GetLower()) && currentRep + VecSize/4 <= MaxRep:
                        currentRep += VecSize/4;
                        outputOffset += VecSize/4;
                        vectorIndex = VecSize;
                        continue;
                }
            }
            
            if (currentCount == 0) { currentBlockPtr[++currentCount] = pixelVectorPtr[vectorIndex++]; ++outputOffset; continue; }
            
            if (currentRep != MaxRep && currentBlockPtr[currentCount] == pixelVectorPtr[vectorIndex]) { ++vectorIndex; ++currentRep; ++outputOffset; continue; } // Pixels are the same
            
            // Flush pixel
            if (currentRep != 0)
            {
                Func.DecodePixelColor(currentBlockPtr[currentCount], out int r, out int g, out int b);
                currentBlockPtr[currentCount] = Func.EncodePixelColorRgba(r, g, b, currentRep);
                currentRep = 0;
            }

            // Flush block if full
            if (currentCount == PixelsPerBlock) { Flush(fd); currentBlockPtr[0] = (uint)outputOffset; currentCount = 0; }
            
            currentBlockPtr[++currentCount] = pixelVectorPtr[vectorIndex++]; ++outputOffset;
        }

        // Flush any remaining repetition count
        if (currentRep > 0)
        {
            Func.DecodePixelColor(currentBlockPtr[currentCount], out int r1, out int g1, out int b1);
            currentBlockPtr[currentCount] = Func.EncodePixelColorRgba(r1, g1, b1, currentRep);
        }

        // Flush the last block if it has data
        if (currentCount > 0) { Flush(fd); currentBlockPtr[0] = (uint)outputOffset; currentCount = 0;}
        fd.GpuDecompressionGroupsX = (uint)(fd.Count / BlockSize);
        fd.GpuDecompressionGroupsY = PixelsPerBlock;
    }

    // Vector128 cross-platform implementation (NEON on ARM, SSE on x86)
    private unsafe void CompressVector128(FrameDescriptor fd)
    {
        const int vecSize = 4; // 128-bit / 32-bit = 4 pixels per vector

        int inputOffset = fd.Offset + offset;
        int endOffset = fd.Offset + offset + count;
        currentCount = 0;
        currentRep = 0;
        int outputOffset = offset;
        uint* currentBlockPtr = currentBlock.AsPointer();
        currentBlockPtr[0] = (uint)outputOffset;

        int vectorIndex = vecSize;
        Vector128<uint> pixelVector = default;
        uint* pixelVectorPtr = stackalloc uint[vecSize];
        uint* inputPtr = fd.Buffer.Data.AsPointer();

        while (inputOffset < endOffset)
        {
            if (vectorIndex == vecSize)
            {
                if (inputOffset + vecSize < endOffset)
                {
                    pixelVector = Vector128.Load(inputPtr + inputOffset);
                    pixelVector.Store(pixelVectorPtr);
                }
                inputOffset += vecSize;
                vectorIndex = 0;
            }

            // Vector-width RLE acceleration: check if all 4 pixels match current pixel
            if (currentCount != 0 && vectorIndex == 0)
            {
                Vector128<uint> currentPixel = Vector128.Create(currentBlockPtr[currentCount]);
                if (currentRep + vecSize <= MaxRep && pixelVector == currentPixel)
                {
                    currentRep += vecSize;
                    outputOffset += vecSize;
                    vectorIndex = vecSize;
                    continue;
                }
            }

            if (currentCount == 0) { currentBlockPtr[++currentCount] = pixelVectorPtr[vectorIndex++]; ++outputOffset; continue; }

            if (currentRep != MaxRep && currentBlockPtr[currentCount] == pixelVectorPtr[vectorIndex]) { ++vectorIndex; ++currentRep; ++outputOffset; continue; }

            // Flush pixel
            if (currentRep != 0)
            {
                Func.DecodePixelColor(currentBlockPtr[currentCount], out int r, out int g, out int b);
                currentBlockPtr[currentCount] = Func.EncodePixelColorRgba(r, g, b, currentRep);
                currentRep = 0;
            }

            // Flush block if full
            if (currentCount == PixelsPerBlock) { Flush(fd); currentBlockPtr[0] = (uint)outputOffset; currentCount = 0; }

            currentBlockPtr[++currentCount] = pixelVectorPtr[vectorIndex++]; ++outputOffset;
        }

        // Flush any remaining repetition count
        if (currentRep > 0)
        {
            Func.DecodePixelColor(currentBlockPtr[currentCount], out int r1, out int g1, out int b1);
            currentBlockPtr[currentCount] = Func.EncodePixelColorRgba(r1, g1, b1, currentRep);
        }

        // Flush the last block if it has data
        if (currentCount > 0) { Flush(fd); currentBlockPtr[0] = (uint)outputOffset; currentCount = 0; }
        fd.GpuDecompressionGroupsX = (uint)(fd.Count / BlockSize);
        fd.GpuDecompressionGroupsY = PixelsPerBlock;
    }

    private unsafe void Flush(FrameDescriptor fd)
    {
        if (currentCount == 0) { return; }
        while (currentCount < PixelsPerBlock) { currentBlock[++currentCount] = 0xFFFFFFFF; } // this will surely set A channel to 0xFF for remaining (suppressed) pixels
        int off = Interlocked.Add(ref fd.Count, BlockSize) - BlockSize;
        currentBlock.CopyTo(0, fd.Blocks.AsPointer(), off, BlockSize);
    }
    
    // variant 1 decompression (for testing), safe version
    public void Decompress(FrameDescriptor frame)
    {
        int aShift = (BitConverter.IsLittleEndian)?24:0;
        for (int i = 0; i < frame.Count / BlockSize; ++i)
        {
            int blockOffset = i * BlockSize;
            int targetOffset = frame.Offset + (int)frame.Blocks[blockOffset];
            for (int pixelIndex = 1; pixelIndex <= PixelsPerBlock; ++pixelIndex)
            {
                uint pixel = frame.Blocks[blockOffset + pixelIndex];
                Func.DecodePixelColor(pixel, out int r, out int g, out int b);
                int rep = (int)((pixel >> aShift) & 0xFF);
                if (rep == 255) { continue; } // skip suppressed pixels
                pixel = Func.EncodePixelColor(r, g, b);
                for (int j = 0; j <= rep; ++j) { frame.Buffer.Data[targetOffset++] = pixel; }
            }
        }
    }
}
