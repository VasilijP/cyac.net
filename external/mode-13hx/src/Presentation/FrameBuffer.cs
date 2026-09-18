using System.Diagnostics;
using mode13hx.Configuration;
using mode13hx.Presentation.Compression;
using mode13hx.Util;

namespace mode13hx.Presentation;

public class FrameBuffer
{
    public readonly int Width;
    public readonly int Height;
    public readonly int FrameSize;
    public int DroppedFrames { get; private set; }
    public static readonly FrametimeComponent Fps = new(400, 4.0);

    private readonly int frameBufferCount; // +1 for the actual buffer being rendered to
    private readonly bool dropFrames;
    private readonly bool fastMode;

    public readonly AlignedArray<uint> Data;

    private readonly FrameDescriptor[] frameDescriptors;
    private readonly Stopwatch frameTimer = Stopwatch.StartNew();
    private double lastFrameTime = 0;

    // Slot lifecycle state machine. Each slot has exactly one state at any moment.
    //   Free       — available for the rasterizer to claim
    //   Rendering  — claimed by rasterizer; write in progress
    //   Ready      — finished rendering; queued for the presenter (Sequence determines order)
    //   Held       — taken by Use, GPU/upload may still reference the data; released via ReleaseFrame
    private enum SlotState : byte { Free, Rendering, Ready, Held }

    private struct SlotRecord
    {
        public SlotState State;
        public uint Sequence; // monotonic at Publish time; orders Ready and Held slots
    }

    // Flat array of slot metadata. Chosen over LinkedList/Stack because at our slot counts (typically 2–16)
    // sequential scans of contiguous struct memory beat pointer-walking and avoid per-operation
    // LinkedListNode<T> allocations that pumped GC pressure in the previous design.
    // See 3drenderbasic/perf-test-comp/SlotPoolBench for the empirical comparison (~0.4–0.6× time, 0 vs 5–50 MB GC).
    private readonly SlotRecord[] slots;
    private uint nextSequence;

    // Pre-allocated scratch for the indices of dropped slots in Use. Sized at construction; reused per call.
    // Only accessed from the presenter thread (single-threaded by window event loop).
    private readonly int[] dropScratch;

    private readonly object stateLock = new();
    // Legacy mode only: rasterizer waits on this when no Free slot. Fast mode's rasterizer drops oldest instead.
    private readonly SemaphoreSlim freeAvailable;

    public FrameBuffer(CommonOptions config, IFrameCompressor compressor = null)
    {
        Width = config.Width;
        Height = config.Height;
        FrameSize = Width * Height;
        dropFrames = config.DropFrames;
        fastMode = config.Fast;
        frameBufferCount = config.FramesPrerenderLimit + 1;
        Data = new AlignedArray<uint>(frameBufferCount * Width * Height);
        frameDescriptors = new FrameDescriptor[frameBufferCount];

        for (int i = 0; i < frameBufferCount; ++i) { frameDescriptors[i] = new FrameDescriptor(i, FrameOffset(i), this, compressor); }

        // All slots start Free (enum default = 0).
        slots = new SlotRecord[frameBufferCount];
        dropScratch = new int[frameBufferCount];
        freeAvailable = new SemaphoreSlim(frameBufferCount, frameBufferCount);
    }

    private int FrameOffset(int frameIndex) => frameIndex * FrameSize;

    /// <summary>Rasterizer claims the next slot to render into.</summary>
    /// <remarks>
    /// In legacy mode (default) blocks until a Free slot is available.
    /// In <see cref="CommonOptions.Fast"/> mode never blocks: if no slot is Free, drops the oldest unread
    /// Ready slot. Prefers slots whose CPU compression task has already completed to avoid stalling the
    /// rasterizer; if none have completed, falls back to the absolute oldest (and waits on its task).
    /// </remarks>
    public FrameDescriptor StartNextFrame()
    {
        int slot;
        bool waitCompression = false;
        if (fastMode)
        {
            lock (stateLock)
            {
                while (true)
                {
                    slot = FindFirstFree();
                    if (slot >= 0) { slots[slot].State = SlotState.Rendering; break; }

                    // Prefer dropping a Ready slot whose compression task has already finished.
                    slot = FindOldestReady(requireCompressionDone: true);
                    if (slot >= 0)
                    {
                        slots[slot].State = SlotState.Rendering;
                        DroppedFrames++;
                        break;
                    }

                    // Fallback: drop the absolute oldest Ready and wait for its compression to finish
                    // before reusing the slot (the task reads slot data via pointer).
                    slot = FindOldestReady(requireCompressionDone: false);
                    if (slot >= 0)
                    {
                        slots[slot].State = SlotState.Rendering;
                        DroppedFrames++;
                        waitCompression = true;
                        break;
                    }

                    // All slots are presenter-Held. Rare. Wait for any state change.
                    Monitor.Wait(stateLock);
                }
            }
        }
        else
        {
            freeAvailable.Wait();
            lock (stateLock)
            {
                slot = FindFirstFree();
                slots[slot].State = SlotState.Rendering;
            }
        }
        if (waitCompression) frameDescriptors[slot].CpuCompressionTask.Wait();
        return frameDescriptors[slot];
    }

    public void FinishFrame(FrameDescriptor fd)
    {
        fd.Compressor?[0].Start(fd); // start compressing frame if enabled
        lock (stateLock)
        {
            slots[fd.FrameIndex].State = SlotState.Ready;
            slots[fd.FrameIndex].Sequence = ++nextSequence;
            Monitor.PulseAll(stateLock);
        }

        double time = frameTimer.Elapsed.TotalSeconds;
        Fps.RecordFrame(time - lastFrameTime); // intervals between rendered frames (closer to actual framerate, even if some are later dropped)
        lastFrameTime = time;
    }

    /// <summary>
    /// Presenter takes the next frame to display. With <see cref="CommonOptions.DropFrames"/>=true (default) or in
    /// fast mode, returns the latest Ready frame and drops the rest. Otherwise returns the oldest (FIFO).
    /// </summary>
    public FrameDescriptor Use()
    {
        int slot;
        int dropCount = 0;
        bool dropping = dropFrames || fastMode;

        lock (stateLock)
        {
            while (true)
            {
                slot = dropping ? FindNewestReady() : FindOldestReady(requireCompressionDone: false);
                if (slot >= 0) break;
                Monitor.Wait(stateLock);
            }

            slots[slot].State = SlotState.Held;

            if (dropping)
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i].State == SlotState.Ready) dropScratch[dropCount++] = i;
                }
            }
        }

        // Wait for compression tasks on dropped slots before marking them Free —
        // the task reads via slot pointer and would race with the rasterizer claiming the slot next.
        if (dropCount > 0)
        {
            for (int i = 0; i < dropCount; i++) frameDescriptors[dropScratch[i]].CpuCompressionTask.Wait();
            lock (stateLock)
            {
                for (int i = 0; i < dropCount; i++)
                {
                    slots[dropScratch[i]].State = SlotState.Free;
                    if (!fastMode) freeAvailable.Release();
                }
                DroppedFrames += dropCount;
                Monitor.PulseAll(stateLock);
            }
        }

        FrameDescriptor fd = frameDescriptors[slot];
        fd.CpuCompressionTask.Wait();

        if (fd.Count > 0) // compressed frame
            fd.Transferred = fd.Count * sizeof(uint);
        else              // uncompressed frame
            fd.Transferred = FrameSize * sizeof(uint);

        return fd;
    }

    /// <summary>
    /// Return the oldest Held slot (FIFO) to the Free pool. Use this for SSBO direct-read where the GPU
    /// holds slots across multiple in-flight frames and releases follow inFlight cycle order. Also fine
    /// for upload-copy where there's typically only one Held slot at a time.
    /// </summary>
    public void ReleaseFrame()
    {
        lock (stateLock)
        {
            int oldest = -1;
            uint oldestSeq = uint.MaxValue;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].State == SlotState.Held && slots[i].Sequence < oldestSeq)
                {
                    oldest = i;
                    oldestSeq = slots[i].Sequence;
                }
            }
            if (oldest >= 0)
            {
                slots[oldest].State = SlotState.Free;
                if (!fastMode) freeAvailable.Release();
                Monitor.PulseAll(stateLock);
            }
        }
    }

    /// <summary>
    /// Return a specific Held slot to the Free pool, regardless of FIFO order. Use this when a slot's data
    /// has been consumed synchronously and can be reused immediately, even if other older slots are still
    /// fence-tied (e.g. compressed-frame branch where pixels have been memcpy'd into a separate buffer
    /// and the slot is no longer GPU-referenced).
    /// </summary>
    public void ReleaseFrame(FrameDescriptor fd)
    {
        lock (stateLock)
        {
            slots[fd.FrameIndex].State = SlotState.Free;
            if (!fastMode) freeAvailable.Release();
            Monitor.PulseAll(stateLock);
        }
    }

    private int FindFirstFree()
    {
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].State == SlotState.Free) return i;
        return -1;
    }

    private int FindNewestReady()
    {
        int newest = -1;
        uint newestSeq = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].State == SlotState.Ready && (newest < 0 || slots[i].Sequence > newestSeq))
            {
                newest = i;
                newestSeq = slots[i].Sequence;
            }
        }
        return newest;
    }

    private int FindOldestReady(bool requireCompressionDone)
    {
        int oldest = -1;
        uint oldestSeq = uint.MaxValue;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].State != SlotState.Ready || slots[i].Sequence >= oldestSeq) continue;
            if (requireCompressionDone && !frameDescriptors[i].CpuCompressionTask.IsCompleted) continue;
            oldest = i;
            oldestSeq = slots[i].Sequence;
        }
        return oldest;
    }
}
