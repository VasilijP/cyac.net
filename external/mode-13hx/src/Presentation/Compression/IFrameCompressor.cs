namespace mode13hx.Presentation.Compression;

/// <summary>
/// Compresses a frame into a frame.Blocks and frame.Count, enables faster transfer of frames to GPU.
/// Trade-off: CPU-side compression and GPU-side decompression consumes resources, but allows higher frame rate if the PCIe bus is the bottleneck.
/// </summary>
public interface IFrameCompressor
{
    /// <summary>
    /// Sets up the compressors for a frame.
    /// </summary>
    /// <param name="frame">frame buffer frame</param>
    /// <param name="compressorDesiredCount">desired count of compressors, might be adjusted due to frame size (divisibility) and alignment</param>
    void Setup(FrameDescriptor frame, int compressorDesiredCount);
    
    /// <summary>
    /// Start the compression task for the frame.
    /// </summary>
    /// <param name="fd"></param>
    void Start(FrameDescriptor fd);
    
    string DecompressShaderName { get; }
    
    /// <summary>
    /// Compresses a frame or its portion into a frame.Blocks and updates frame.Count.
    /// Must be thread-safe (in respect to frame), there will be at most one invocation per instance of FrameCompressor at the same time.
    /// </summary>
    /// <param name="frame"></param>
    void Compress(FrameDescriptor frame);
    
    // For Testing purposes, compress without any optimizations or unsafe operations.
    void CompressSafe(FrameDescriptor frame);
    
    // For TESTing purposes - decompression is normally done by GPU.
    void Decompress(FrameDescriptor frame);
}