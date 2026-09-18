using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace mode13hx.Util;

public static class AlignedAlloc
{
    public const int Alignment = 64; // must be a power of 2

    public static IntPtr Allocate(int size)
    {
        IntPtr rawMemory = Marshal.AllocHGlobal(size + Alignment - 1);
        if (rawMemory == IntPtr.Zero) { throw new OutOfMemoryException("Unable to allocate memory."); }
        return rawMemory;
    }
    
    public static IntPtr Align(IntPtr originalPtr)
    {
        long rawAddress = originalPtr.ToInt64();
        long alignedAddress = (rawAddress + Alignment - 1) & ~(Alignment - 1); // zero the lower bits (Alignment - 1 must be all ones)
        IntPtr alignedPtr = new(alignedAddress);
        return alignedPtr;
    }

    public static void Free(IntPtr originalPtr)
    {
        if (originalPtr == IntPtr.Zero) { return; }
        Marshal.FreeHGlobal(originalPtr);
    }
}

public sealed class AlignedArray<T> : IDisposable where T : unmanaged
{
    private readonly IntPtr alignedPtr;
    private readonly IntPtr originalPtr;
    public readonly int Length;
    private bool disposed;

    public AlignedArray(int length)
    {
        Length = length;
        int sizeInBytes;
        unsafe { sizeInBytes = length * sizeof(T); }
        originalPtr = AlignedAlloc.Allocate(sizeInBytes);
        alignedPtr = AlignedAlloc.Align(originalPtr);
    }
    
    public unsafe AlignedArray(T * existingPtr, int length)
    {
        if (((long)existingPtr & (AlignedAlloc.Alignment - 1)) != 0) { throw new Exception("Unable to represent unaligned address as aligned."); }
        Length = length;
        originalPtr = IntPtr.Zero;
        alignedPtr = (IntPtr)existingPtr;
    }

    public Span<T> AsSpan(int offset, int len) { unsafe { return new Span<T>((void*)(alignedPtr + offset*sizeof(T)), len); } }
    public Span<T> AsSpan() { unsafe { return new Span<T>((void*)alignedPtr, Length); } }
    
    public unsafe void CopyTo(int sourceOffset, uint* destination, int destinationOffset, int len)
    {
        Buffer.MemoryCopy((void*)(alignedPtr + sourceOffset * sizeof(T)), destination + destinationOffset, len * sizeof(T), len * sizeof(T));
    }
    
    // https://www.amd.com/en/developer/zen-software-studio/applications/spack/stream-benchmark.html
    private const  int NumThreads = 8;
    private readonly ParallelOptions parallelOptions = new() { MaxDegreeOfParallelism = NumThreads };
    public unsafe void CopyToPll(int sourceOffset, uint* destination, int destinationOffset, int len, int chunks = NumThreads)
    {
        int chunkSize = len / chunks;
        int remainder = len % chunks;
        uint* srcPtr = (uint*)alignedPtr;
        
        Parallel.For(0, chunks, parallelOptions, i =>
        //for (int i = 0; i < chunks; ++i) // <- interestingly, just by chunking blocks it seems to be faster even when copying sequentially (e.g. with 16 chunks), perhaps some cache effect
        {
            int chunkStart = i * chunkSize;
            int chunkEnd = chunkStart + chunkSize;
            if (i == chunks - 1) { chunkEnd += remainder; } // Last thread handles the remainder

            uint* sourceChunkPtr = srcPtr + sourceOffset + chunkStart;
            uint* destinationChunkPtr = destination + destinationOffset + chunkStart;
            long bytesToCopy = (chunkEnd - chunkStart) * sizeof(uint);
            Buffer.MemoryCopy(sourceChunkPtr, destinationChunkPtr, bytesToCopy, bytesToCopy); // TODO: try to use CopyToAvxCached256 and CopyToChnked with chunk size under 1M instead
        });
    }
    
    public unsafe void CopyToChnked(int sourceOffset, uint* destination, int destinationOffset, int len, int chunks = NumThreads)
    {
        int chunkSize = len / chunks;
        int remainder = len % chunks;
        uint* srcPtr = (uint*)alignedPtr;
        
        for (int i = 0; i < chunks; ++i) // <- interestingly, just by chunking blocks it seems to be faster even when copying sequentially (e.g. with 16 chunks), perhaps some cache effect
        {
            int chunkStart = i * chunkSize;
            int chunkEnd = chunkStart + chunkSize;
            if (i == chunks - 1) { chunkEnd += remainder; } // Last thread handles the remainder

            uint* sourceChunkPtr = srcPtr + sourceOffset + chunkStart;
            uint* destinationChunkPtr = destination + destinationOffset + chunkStart;
            long bytesToCopy = (chunkEnd - chunkStart) * sizeof(uint);
            Buffer.MemoryCopy(sourceChunkPtr, destinationChunkPtr, bytesToCopy, bytesToCopy);
        }
    }
    
    // using non-temporal stores
    public unsafe void CopyToAvx(int sourceOffset, uint* destination, int destinationOffset, int len)
    {
        const int vectorSize = 16; // 512 bits at 32 bits per uint
        const int mul = 8; // how many vectors to load in a row (before storing)
        int i = 0;
        uint * source = (uint*)alignedPtr + sourceOffset;
        destination += destinationOffset;
        
        for (; i + mul*vectorSize < len; i += mul*vectorSize)
        {
            Vector512<uint> vector0 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 0*vectorSize);
            Vector512<uint> vector1 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 1*vectorSize);
            Vector512<uint> vector2 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 2*vectorSize);
            Vector512<uint> vector3 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 3*vectorSize);
            Vector512<uint> vector4 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 4*vectorSize);
            Vector512<uint> vector5 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 5*vectorSize);
            Vector512<uint> vector6 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 6*vectorSize);
            Vector512<uint> vector7 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 7*vectorSize);
            /*Vector512<uint> vector8 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 8*vectorSize);
            Vector512<uint> vector9 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 9*vectorSize);
            Vector512<uint> vector10 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 10*vectorSize);
            Vector512<uint> vector11 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 11*vectorSize);
            Vector512<uint> vector12 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 12*vectorSize);
            Vector512<uint> vector13 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 13*vectorSize);
            Vector512<uint> vector14 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 14*vectorSize);
            Vector512<uint> vector15 = Avx512F.LoadAlignedVector512NonTemporal(source + i + 15*vectorSize);*/
            Avx512F.StoreAlignedNonTemporal(destination + i + 0*vectorSize, vector0);
            Avx512F.StoreAlignedNonTemporal(destination + i + 1*vectorSize, vector1);
            Avx512F.StoreAlignedNonTemporal(destination + i + 2*vectorSize, vector2);
            Avx512F.StoreAlignedNonTemporal(destination + i + 3*vectorSize, vector3);
            Avx512F.StoreAlignedNonTemporal(destination + i + 4*vectorSize, vector4);
            Avx512F.StoreAlignedNonTemporal(destination + i + 5*vectorSize, vector5);
            Avx512F.StoreAlignedNonTemporal(destination + i + 6*vectorSize, vector6);
            Avx512F.StoreAlignedNonTemporal(destination + i + 7*vectorSize, vector7);
            /*Avx512F.StoreAlignedNonTemporal(destination + i + 8*vectorSize, vector8);
            Avx512F.StoreAlignedNonTemporal(destination + i + 9*vectorSize, vector9);
            Avx512F.StoreAlignedNonTemporal(destination + i + 10*vectorSize, vector10);
            Avx512F.StoreAlignedNonTemporal(destination + i + 11*vectorSize, vector11);
            Avx512F.StoreAlignedNonTemporal(destination + i + 12*vectorSize, vector12);
            Avx512F.StoreAlignedNonTemporal(destination + i + 13*vectorSize, vector13);
            Avx512F.StoreAlignedNonTemporal(destination + i + 14*vectorSize, vector14);
            Avx512F.StoreAlignedNonTemporal(destination + i + 15*vectorSize, vector15);*/
        }
        
        for (; i < len; ++i) { destination[i] = source[i]; } // remaining elements
        Sse.StoreFence(); // ensure that all non-temporal stores are committed to memory
    }
    
    public unsafe void CopyToAvxCached512(int sourceOffset, uint* destination, int destinationOffset, int len)
    {
        const int vectorSize = 16; // 512 bits at 32 bits per uint
        const int mul = 8; // how many vectors to load in a row (before storing)
        int i = 0;
        uint * source = (uint*)alignedPtr + sourceOffset;
        destination += destinationOffset;
        
        for (; i + mul*vectorSize < len; i += mul*vectorSize)
        {
            Vector512<uint> vector0 = Avx512F.LoadAlignedVector512(source + i + 0*vectorSize);
            Vector512<uint> vector1 = Avx512F.LoadAlignedVector512(source + i + 1*vectorSize);
            Vector512<uint> vector2 = Avx512F.LoadAlignedVector512(source + i + 2*vectorSize);
            Vector512<uint> vector3 = Avx512F.LoadAlignedVector512(source + i + 3*vectorSize);
            Vector512<uint> vector4 = Avx512F.LoadAlignedVector512(source + i + 4*vectorSize);
            Vector512<uint> vector5 = Avx512F.LoadAlignedVector512(source + i + 5*vectorSize);
            Vector512<uint> vector6 = Avx512F.LoadAlignedVector512(source + i + 6*vectorSize);
            Vector512<uint> vector7 = Avx512F.LoadAlignedVector512(source + i + 7*vectorSize);
            Avx512F.StoreAligned(destination + i + 0*vectorSize, vector0);
            Avx512F.StoreAligned(destination + i + 1*vectorSize, vector1);
            Avx512F.StoreAligned(destination + i + 2*vectorSize, vector2);
            Avx512F.StoreAligned(destination + i + 3*vectorSize, vector3);
            Avx512F.StoreAligned(destination + i + 4*vectorSize, vector4);
            Avx512F.StoreAligned(destination + i + 5*vectorSize, vector5);
            Avx512F.StoreAligned(destination + i + 6*vectorSize, vector6);
            Avx512F.StoreAligned(destination + i + 7*vectorSize, vector7);
        }
        
        for (; i < len; ++i) { destination[i] = source[i]; } // remaining elements
        Sse.StoreFence(); // ensure that all non-temporal stores are committed to memory
    }
    
    public unsafe void CopyToAvxCached256(int sourceOffset, uint* destination, int destinationOffset, int len)
    {
        const int vectorSize = 8; // 256 bits at 32 bits per uint
        const int mul = 8; // how many vectors to load in a row (before storing)
        int i = 0;
        uint * source = (uint*)alignedPtr + sourceOffset;
        destination += destinationOffset;
        
        for (; i + mul*vectorSize < len; i += mul*vectorSize)
        {
            Vector256<uint> vector0 = Avx.LoadAlignedVector256(source + i + 0*vectorSize);
            Vector256<uint> vector1 = Avx.LoadAlignedVector256(source + i + 1*vectorSize);
            Vector256<uint> vector2 = Avx.LoadAlignedVector256(source + i + 2*vectorSize);
            Vector256<uint> vector3 = Avx.LoadAlignedVector256(source + i + 3*vectorSize);
            Vector256<uint> vector4 = Avx.LoadAlignedVector256(source + i + 4*vectorSize);
            Vector256<uint> vector5 = Avx.LoadAlignedVector256(source + i + 5*vectorSize);
            Vector256<uint> vector6 = Avx.LoadAlignedVector256(source + i + 6*vectorSize);
            Vector256<uint> vector7 = Avx.LoadAlignedVector256(source + i + 7*vectorSize);
            Avx.StoreAligned(destination + i + 0*vectorSize, vector0);
            Avx.StoreAligned(destination + i + 1*vectorSize, vector1);
            Avx.StoreAligned(destination + i + 2*vectorSize, vector2);
            Avx.StoreAligned(destination + i + 3*vectorSize, vector3);
            Avx.StoreAligned(destination + i + 4*vectorSize, vector4);
            Avx.StoreAligned(destination + i + 5*vectorSize, vector5);
            Avx.StoreAligned(destination + i + 6*vectorSize, vector6);
            Avx.StoreAligned(destination + i + 7*vectorSize, vector7);
        }
        
        for (; i < len; ++i) { destination[i] = source[i]; } // remaining elements
        Sse.StoreFence(); // ensure that all non-temporal stores are committed to memory
    }
    
    public unsafe T* AsPointer() { return (T*)alignedPtr; }

    public void Dispose()
    {
        if (disposed) return;
        AlignedAlloc.Free(originalPtr);
        disposed = true;
    }

    public T this[int i]
    {
        get => AsSpan()[i];
        set => AsSpan()[i] = value;
    }

    public unsafe AlignedArray<T> SubArray(int offset, int bufferFrameSize)
    {
        return new AlignedArray<T>(AsPointer() + offset, bufferFrameSize);
    }
}
