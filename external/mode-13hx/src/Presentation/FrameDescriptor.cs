using mode13hx.Presentation.Compression;
using mode13hx.Util;

namespace mode13hx.Presentation;

public sealed class FrameDescriptor
{
    private const int CompressorCountPreference = 8;

    public readonly int FrameIndex;
    public readonly int Offset;
    public readonly FrameBuffer Buffer;
    public Span<uint> FrameSpan => Buffer.Data.AsSpan(Offset, Buffer.FrameSize);
    public AlignedArray<uint> Array;

    public readonly Canvas Canvas;

    public IFrameCompressor[] Compressor; // compressors for frame slices (if enabled)
    public AlignedArray<uint> Blocks; // compressed blocks of pixels
    public int Count; // number of Blocks uints occupied by the compressed blocks
    public int Transferred; // number of bytes transferred to the GPU (either compressed or uncompressed frame)
    public Task CpuCompressionTask = Task.CompletedTask; // cpu task compressing the frame
    public uint GpuDecompressionGroupsX; // how many groups should be launched to decompress the frame
    public uint GpuDecompressionGroupsY; // how many groups should be launched to decompress the frame

    internal FrameDescriptor(int frameIndex, int offset, FrameBuffer buffer, IFrameCompressor compressor = null)
    {
        FrameIndex = frameIndex;
        Offset = offset;
        Buffer = buffer;
        Canvas = new Canvas(this);
        Array = buffer.Data.SubArray(offset, buffer.FrameSize);
        compressor?.Setup(this, CompressorCountPreference);
    }
}
