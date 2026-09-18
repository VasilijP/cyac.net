using System.Runtime.InteropServices;
using CYAC.Port.Audio;

namespace CYAC.Port.Host.Sound;

/// <summary>
/// Live playback on macOS: AudioToolbox's AudioQueue C API through P/Invoke, with no NuGet
/// dependency of any kind.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it does NOT need.</b>  The emulator's pump carries a whole elastic tracking policy
/// (<c>LiveAudioPacer</c>: a target lag, a ±12.5 % resample band, skip and stretch plans) because it
/// is chasing an INSTRUCTION COUNT — its audio time is the guest's, and the guest runs faster or
/// slower than real time.  The port has no such clock: the generator is driven by the render itself
/// (<see cref="AudioRuntime.Render"/> issues the 256 Hz driver tick as the cursor passes it), so the
/// audio time IS the device's time and there is nothing to track.
/// </para>
/// <para>
/// <b>Starvation.</b>  A refill can never be short of SAMPLES — the generator always has state to
/// render.  What it can be short of is EVENTS: if the host frame loop stalls, the sim sends no new
/// driver calls and the sound simply continues as it stood (a held engine note, a ringing release
/// tail) until the events resume.  That is a smooth, correct continuation rather than a hole, so the
/// policy is "render on, count the refills that overtook the last frame" —
/// <see cref="Report"/> prints that count.
/// </para>
/// </remarks>
public sealed class MacAudioQueueOutput : IAudioOutput
{
    private const int BufferFrames = 512;
    private const int BufferCount = 4;

    private readonly AudioRuntime _runtime;
    private readonly AudioQueueOutputCallback _callback;   // a GC root for the native callback
    private readonly float[] _scratch = new float[BufferFrames * AudioFormat.Channels];
    private readonly short[] _pcm = new short[BufferFrames * AudioFormat.Channels];
    private readonly object _sync = new();
    private IntPtr _queue;
    private bool _disposed;
    private long _refills;

    private MacAudioQueueOutput(AudioRuntime runtime)
    {
        _runtime = runtime;
        _callback = OnBufferNeeded;
    }

    /// <inheritdoc/>
    public string Name => "AudioToolbox AudioQueue";

    /// <inheritdoc/>
    public bool IsRunning => _queue != IntPtr.Zero && !_disposed;

    /// <summary>Starts playback, or returns null when this is not macOS or the device refuses.</summary>
    /// <param name="runtime">The sound path to pull from.</param>
    public static MacAudioQueueOutput? TryStart(AudioRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        try
        {
            MacAudioQueueOutput output = new MacAudioQueueOutput(runtime);
            AudioStreamBasicDescription format = new AudioStreamBasicDescription
            {
                SampleRate = AudioFormat.SampleRate,
                FormatId = 0x6C70636D,                     // 'lpcm'
                FormatFlags = 0x4 | 0x8,                   // signed integer | packed
                BytesPerPacket = 4,
                FramesPerPacket = 1,
                BytesPerFrame = 4,
                ChannelsPerFrame = AudioFormat.Channels,
                BitsPerChannel = 16,
            };

            if (AudioQueueNewOutput(
                    ref format, output._callback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0,
                    out output._queue) != 0
                || output._queue == IntPtr.Zero)
            {
                return null;
            }

            for (int i = 0; i < BufferCount; i++)
            {
                if (AudioQueueAllocateBuffer(output._queue, BufferFrames * 4, out IntPtr buffer) != 0)
                {
                    output.Dispose();
                    return null;
                }

                output.OnBufferNeeded(IntPtr.Zero, output._queue, buffer);   // prime
            }

            if (AudioQueueStart(output._queue, IntPtr.Zero) != 0)
            {
                output.Dispose();
                return null;
            }

            return output;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public string Report()
    {
        double bufferMs = BufferFrames * 1000.0 / AudioFormat.SampleRate;
        return $"[audio] {Name}: {BufferCount}×{BufferFrames} frames ({bufferMs:0.0} ms each, "
            + $"{BufferCount * bufferMs:0.0} ms of device latency), {_refills:N0} refill(s), "
            + $"{_runtime.SampleCursor / (double)AudioFormat.SampleRate:0.0} s rendered";
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_queue != IntPtr.Zero)
        {
            try
            {
                AudioQueueStop(_queue, true);
                AudioQueueDispose(_queue, true);
            }
            catch (DllNotFoundException)
            {
                // The framework went away underneath us; nothing to release.
            }

            _queue = IntPtr.Zero;
        }
    }

    private void OnBufferNeeded(IntPtr userData, IntPtr queue, IntPtr buffer)
    {
        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _runtime.Render(_scratch, BufferFrames);
                for (int i = 0; i < _pcm.Length; i++)
                {
                    _pcm[i] = (short)Math.Clamp(
                        (int)(_scratch[i] * AudioFormat.Int16Scale), short.MinValue, short.MaxValue);
                }

                // AudioQueueBuffer layout (64-bit): capacity u32@0, mAudioData ptr@8, byteSize u32@16.
                IntPtr data = Marshal.ReadIntPtr(buffer, 8);
                Marshal.Copy(_pcm, 0, data, _pcm.Length);
                Marshal.WriteInt32(buffer, 16, BufferFrames * 4);
                AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
                _refills++;
            }
        }
        catch (ObjectDisposedException)
        {
            // The queue is going away; the audio path must never take the game down.
        }
    }

    // ------------------------------------------------------------------------ P/Invoke surface

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    private delegate void AudioQueueOutputCallback(IntPtr userData, IntPtr queue, IntPtr buffer);

    private const string AudioToolbox =
        "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueNewOutput(
        ref AudioStreamBasicDescription format,
        AudioQueueOutputCallback callback,
        IntPtr userData,
        IntPtr callbackRunLoop,
        IntPtr runLoopMode,
        uint flags,
        out IntPtr queue);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint byteSize, out IntPtr buffer);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueEnqueueBuffer(
        IntPtr queue, IntPtr buffer, uint packetDescriptionCount, IntPtr packetDescriptions);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStop(
        IntPtr queue, [MarshalAs(UnmanagedType.I1)] bool immediate);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueDispose(
        IntPtr queue, [MarshalAs(UnmanagedType.I1)] bool immediate);
}
