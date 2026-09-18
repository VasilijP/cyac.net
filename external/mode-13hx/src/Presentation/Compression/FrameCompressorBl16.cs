using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using mode13hx.Configuration;
using mode13hx.Util;
using mode13hx.Presentation;

namespace mode13hx.Presentation.Compression;

// This compressor divides input image into blocks of 16 consecutive pixels. Compressed output is then formed by Width*Height/16 blocks + additional copied blocks.
// If all 16 pixels are the same, it writes the special block mark into unused byte (alpha channel), thus decompressor knows the implicit repeat count (VecSize == 16).
// If the 16 pixel block is not formed by 16 identical pixels, write down the offset and copy all 16 pixels 1:1 to compressed output at the offset.
//TODO: this is slower and produces much larger images than RLE variant, there is potential improvement if pixels are permutated on copied blocks and 12B instead of 16B are written (by removing Alpha)
//      potentially we could reshuffle 4x16 pixels into 3x16 vectors (to get rid of Alpha channel for 1:1 pixels), but it would be possible only if 4 consecutive blocks are just copied
//      pack 4*16 to 3*16 uint vectors for alignment (this could reduce 25% for non-compressible frames), (?)separate RGB to 64xR 64xG 64xB bytes?
//TODO: there is some strange bug (blip) causing long compression time when wall is viewed from a distance where height is ~100% of the frame height
//TODO: perhaps this has better potential if it employs some kind of a lossy compression per block (e.g.: HDMI or PNG -like schemes)    
public sealed class FrameCompressorBl16 : IFrameCompressor
{
    private const uint SpecialBlockMark = 1u << 31;

    private readonly int offset;
    private readonly int pixelCount;
    private readonly int blocksToWrite;
    private static uint gpuGroups;

    private const int VecSize = 16;
    private const int AllocationSize = VecSize * 1024; // Allocate x times VecSize

    private readonly AlignedArray<uint> blocks;

    public string DecompressShaderName => "decomp_b16.comp";

    private FrameCompressorBl16(int offset, int count)
    {
        if (offset % VecSize != 0) { throw new ArgumentException($"Offset {offset} is not aligned to {VecSize} pixels"); }
        if (count % VecSize != 0) { throw new ArgumentException($"Count {count} is not aligned to {VecSize} pixels"); }

        this.offset = offset;
        this.pixelCount = count;
        this.blocksToWrite = count / VecSize;
        blocks = new AlignedArray<uint>(this.blocksToWrite);
    }

    public FrameCompressorBl16(CommonOptions config)
    {
        this.offset = 0;
        this.pixelCount = 0;
        this.blocksToWrite = 0;
        // Pre-multiply by 32 to compensate for VulkanRenderer dividing by 32 (assumes local_size_x=32),
        // since decomp_b16.comp uses local_size_x=1
        FrameCompressorBl16.gpuGroups = (uint)(config.Width*config.Height/VecSize) * 32;
    }

    public void Setup(FrameDescriptor fd, int compressorDesiredCount)
    {
        // slice the frame here
        int cbs = fd.Buffer.Width/compressorDesiredCount; // compression block column count (resolution width must be divisible by this number) 
        while (fd.Buffer.Width % cbs != 0) { cbs++; }; // check/adjust Cbs to work with different resolutions, it will drop to 1 compressor in worst case
        fd.Compressor = new IFrameCompressor[fd.Buffer.Width/cbs];
        for (int i = 0; i < fd.Compressor.Length; ++i) { fd.Compressor[i] = new FrameCompressorBl16(i * cbs*fd.Buffer.Height, (cbs-0)*fd.Buffer.Height); }; // create compressor collection per frame descriptor (it is possible to compress multiple frames in parallel)
    
        // could be 1/VecSize bigger in the worst case (no compressed blocks and a copy of the whole frame), additional space added because of preallocation done by chunks
        int maximumSize = fd.Buffer.Width * fd.Buffer.Height / VecSize + fd.Buffer.Width * fd.Buffer.Height + fd.Compressor.Length * AllocationSize;
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
        fd.Count = fd.Buffer.Width*fd.Buffer.Height/VecSize; // there is at least 1/16th of pixels + whatever we have to copy 1:1
         
        fd.CpuCompressionTask = Task.Run(() =>
        {
            ParallelOptions po = new() { MaxDegreeOfParallelism = 4 };
            Parallel.ForEach(fd.Compressor, po, c => { c.Compress(fd); });
            took = sw.Elapsed.TotalMilliseconds; // TODO: move telemetry to the task, add facilities to measure compression related stats to FrametimeComponent
            sum+=took; fcount++; //TODO: race condition here
            avg = sum / fcount;
            if (min > took) min = took;
        });
    }
    
    public unsafe void CompressSafe(FrameDescriptor fd)
    {
        fd.GpuDecompressionGroupsX = gpuGroups;
        fd.GpuDecompressionGroupsY = 1;
        if (!Avx512F.IsSupported) { CompressVector128(fd); return; }
        const uint specialBlockMark = 1u << 31;
        int inputOffset = fd.Offset + offset;
        int target = offset / VecSize; // where to write blocks
        uint* inputPtr = fd.Buffer.Data.AsPointer();
        for (int i = 0; i < blocksToWrite; ++i)
        {
            Vector512<uint> pixelVector = Avx512F.LoadVector512(inputPtr + inputOffset);
            inputOffset += VecSize;
            uint pixel = pixelVector.GetElement(0);
            Vector512<uint> broadcastVector = Vector512.Create(pixel); // copy first element into comparison vector
            if (pixelVector.Equals(broadcastVector)) // store just first pixel
            {
                fd.Blocks[target++] = pixel;
            }
            else // store a special mark+offset instead of the pixel and copy complete block to at the offset
            {
                int index = Interlocked.Add(ref fd.Count, VecSize) - VecSize; // reserve VecSize elements and write from "current" fd.Count (inclusive) to fd.Count + VecSize (exclusive)
                fd.Blocks[target++] = specialBlockMark | (uint)index;
                uint* destPtr = fd.Blocks.AsPointer();
                Avx512F.Store(destPtr + index, pixelVector); 
            }
        }
    }
    
    public unsafe void Compress(FrameDescriptor fd)  //TODO: alignment of block buffer, alignment of frame buffer
    { 
        fd.GpuDecompressionGroupsX = gpuGroups;
        fd.GpuDecompressionGroupsY = 1;
        if (pixelCount % VecSize != 0) { throw new ArgumentException($"Count {pixelCount} is not aligned to {VecSize} pixels"); }
        if (!Avx512F.IsSupported) { CompressVector128(fd); return; }
        int inputOffset = fd.Offset + offset;
        int target = offset / VecSize; // where to write blocks
        
        uint* buffer = blocks.AsPointer();
        int bufferCount = 0;
        
        int indexBase = Interlocked.Add(ref fd.Count, AllocationSize) - AllocationSize; // Allocate a larger chunk of indices TODO: allocate extra space for blocks, otherwise this could blow out of the buffer
        int indexOffset = 0; // Offset within the allocated chunk
        
        uint* inputPtr = fd.Buffer.Data.AsPointer();
        uint* blocksPtr = fd.Blocks.AsPointer();
        
        uint* destPtr;
        for (int i = 0; i < blocksToWrite; ++i) 
        { 
            Vector512<uint> pixelVector = Avx512F.LoadVector512(inputPtr + inputOffset); 
            inputOffset += VecSize; 
             
            uint pixel = pixelVector.GetElement(0); 
            Vector512<uint> broadcastVector = Vector512.Create(pixel); 

            if (pixelVector.Equals(broadcastVector)) 
            { 
                buffer[bufferCount++] = pixel; // All elements are equal; store the pixel 
            } 
            else 
            {
                int index = indexBase + indexOffset; 
                indexOffset += VecSize; 
                buffer[bufferCount++] = SpecialBlockMark | (uint)index; // Not all elements are equal; store special mark and index 
                 
                destPtr = blocksPtr + index;
                Avx512F.Store(destPtr, pixelVector); 
                 
                if (indexOffset >= AllocationSize)
                { 
                    indexBase = Interlocked.Add(ref fd.Count, AllocationSize) - AllocationSize; // Allocate a larger chunk of indices TODO: allocate extra space for blocks, otherwise this could blow out of the buffer
                    indexOffset = 0;
                } 
            } 
        }
        
        destPtr = blocksPtr + target;
        while (bufferCount >= VecSize) // copy multiples of VecSize to the target
        {
            Vector512<uint> bufferVec = Avx512F.LoadVector512(buffer);
            Avx512F.Store(destPtr, bufferVec); // allocated a buffer for complete block compressed by this instance, then stored when loop ends to consecutive address which is much quicker
            bufferCount -= VecSize;
            destPtr += VecSize;
            buffer += VecSize;
        }
        // flush remaining values in the buffer
        for (int i = 0; i < bufferCount; ++i) { destPtr[i] = buffer[i]; }
    }

    // Vector128 cross-platform implementation (NEON on ARM, SSE on x86)
    private unsafe void CompressVector128(FrameDescriptor fd)
    {
        int inputOffset = fd.Offset + offset;
        int target = offset / VecSize;

        uint* buffer = blocks.AsPointer();
        int bufferCount = 0;

        int indexBase = Interlocked.Add(ref fd.Count, AllocationSize) - AllocationSize;
        int indexOffset = 0;

        uint* inputPtr = fd.Buffer.Data.AsPointer();
        uint* blocksPtr = fd.Blocks.AsPointer();

        uint* destPtr;
        for (int i = 0; i < blocksToWrite; ++i)
        {
            // Load 16 pixels as 4 × Vector128<uint>
            Vector128<uint> v0 = Vector128.Load(inputPtr + inputOffset);
            Vector128<uint> v1 = Vector128.Load(inputPtr + inputOffset + 4);
            Vector128<uint> v2 = Vector128.Load(inputPtr + inputOffset + 8);
            Vector128<uint> v3 = Vector128.Load(inputPtr + inputOffset + 12);
            inputOffset += VecSize;

            uint pixel = v0.GetElement(0);
            Vector128<uint> broadcast = Vector128.Create(pixel);

            if (v0 == broadcast && v1 == broadcast && v2 == broadcast && v3 == broadcast)
            {
                buffer[bufferCount++] = pixel; // All elements are equal; store the pixel
            }
            else
            {
                int index = indexBase + indexOffset;
                indexOffset += VecSize;
                buffer[bufferCount++] = SpecialBlockMark | (uint)index; // Not all elements are equal; store special mark and index

                destPtr = blocksPtr + index;
                v0.Store(destPtr);
                v1.Store(destPtr + 4);
                v2.Store(destPtr + 8);
                v3.Store(destPtr + 12);

                if (indexOffset >= AllocationSize)
                {
                    indexBase = Interlocked.Add(ref fd.Count, AllocationSize) - AllocationSize;
                    indexOffset = 0;
                }
            }
        }

        destPtr = blocksPtr + target;
        while (bufferCount >= 4) // copy multiples of 4 to the target using Vector128
        {
            Vector128<uint> bufferVec = Vector128.Load(buffer);
            bufferVec.Store(destPtr);
            bufferCount -= 4;
            destPtr += 4;
            buffer += 4;
        }
        // flush remaining values in the buffer
        for (int i = 0; i < bufferCount; ++i) { destPtr[i] = buffer[i]; }
    }

    // TODO: try to decompress 1 pixel per GPU thread and compare GPU load - no looping in shader code, each thread will either copy 1 pixel from 1:1 block or copy the RLE pixel to its respective place.
    // TODO: (?) or decompress 4 pixels per GPU thread (instead of 16), to align with 64xRGBA -> 64xR 64xG 64xB packing
    public void Decompress(FrameDescriptor frame)
    {
        throw new NotImplementedException();
    }
}
