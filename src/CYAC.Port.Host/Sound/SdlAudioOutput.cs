using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CYAC.Port.Audio;
using Silk.NET.SDL;

namespace CYAC.Port.Host.Sound;

/// <summary>
/// Live playback on EVERY desktop the port publishes for: SDL2's audio device, through the
/// <c>Silk.NET.SDL</c> binding, with the native library carried by the package's own
/// <c>runtimes/&lt;rid&gt;/native</c> mechanism.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why SDL2 and not OpenAL.</b> proposed OpenAL Soft through <c>Silk.NET.OpenAL</c>.  The Q3
/// spike ran both on this machine and the OpenAL route lost on two measured facts:
/// <c>Silk.NET.OpenAL.Soft.Native</c> 1.21.1.1 ships <c>linux-x64 / osx-x64 / win-x64 / win-x86</c>
/// only — there is <b>no osx-arm64 native</b>, so the port's own development machine could never
/// exercise the shipped library — and the package is <b>LGPL-2.0-or-later</b>, where the binding
/// alone is MIT.  SDL2 (<c>Ultz.Native.SDL</c>, the native <c>Silk.NET.SDL</c> depends on) is
/// <b>zlib</b>-licensed, its macOS dylib is a universal x86_64 + arm64 binary, and it adds
/// <c>linux-arm64</c> and <c>win-arm64</c> as well.  Both spikes played a clean 440 Hz tone;
/// </para>
/// <para>
/// <b>The shape is the Mac output's.</b>  SDL2 pulls through a device callback on its own thread,
/// exactly as <see cref="MacAudioQueueOutput"/> does, so this class is the same machine with a
/// different door: render a block of frames from <see cref="AudioRuntime"/>, clamp it to signed
/// 16-bit, hand it over.  Nothing about the mixer, the driver tick or the event path changes, which
/// is why <c>--wav</c> renders bit-identical audio whichever device is open.
/// </para>
/// <para>
/// <b>Latency.</b>  1,024 frames is 21.3 ms at 48 kHz and SDL keeps two blocks in flight, so the
/// device latency is the ≈ 42.7 ms the macOS path already had (4 × 512).  The block is twice the
/// Mac's because the two platforms this code exists FOR could not be measured here: a 10.7 ms
/// callback is at the edge of a WASAPI shared-mode period and of a default PulseAudio quantum, and
/// a late callback is an audible hole.  <see cref="Report"/> prints the geometry SDL actually
/// granted, so a Windows or Linux run says what it got rather than what was asked for.
/// </para>
/// <para>
/// <b>Failure is never fatal.</b>  A missing native, a machine with no sound card, a headless CI
/// container: every one of them comes back as <c>(null, reason)</c> from
/// <see cref="TryStart(AudioRuntime)"/> and <see cref="AudioOutputSelector"/> turns it into a
/// <see cref="NullAudioOutput"/> carrying that reason.  The game plays on in silence; it never
/// crashes for want of a speaker.
/// </para>
/// </remarks>
public sealed unsafe class SdlAudioOutput : IAudioOutput
{
    /// <summary>
    /// Frames per device callback.  See the class remarks: 21.3 ms, two in flight ⇒ the Mac path's
    /// ≈ 42.7 ms of device latency.
    /// </summary>
    internal const int BufferFrames = 1024;

    /// <summary>SDL's own name for "let the backend pick the default output device".</summary>
    private const string DefaultDevice = "(default)";

    /// <summary>The handle the callback reaches this instance through.</summary>
    private GCHandle _self;

    private Sdl? _sdl;
    private uint _device;
    private int _callbackFrames;
    private string _driver = "?";
    private float[] _scratch = [];
    private short[] _pcm = [];
    private long _callbacks;
    private long _framesRendered;
    private bool _disposed;

    private SdlAudioOutput(AudioRuntime runtime) => Runtime = runtime;

    /// <summary>The sound path this output pulls from.</summary>
    private AudioRuntime Runtime { get; }

    /// <inheritdoc/>
    public string Name => $"SDL2 audio ({_driver})";

    /// <inheritdoc/>
    public bool IsRunning => _device != 0 && !_disposed;

    /// <summary>
    /// Opens the default output device, or says why it could not be opened.
    /// </summary>
    /// <param name="runtime">The sound path to pull from.</param>
    /// <returns>
    /// The started output and an empty reason, or null and the one line the readout should print.
    /// </returns>
    public static (IAudioOutput? Output, string Reason) TryStart(AudioRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        Sdl sdl;
        try
        {
            sdl = Sdl.GetApi();
        }
        catch (Exception error) when (IsNativeLoadFailure(error))
        {
            // The native did not resolve: a publish without the runtimes folder, a trimmed
            // self-contained build, an unsupported RID.  Name it; do not take the game down.
            return (null, $"the SDL2 native could not be loaded ({error.GetType().Name})");
        }

        SdlAudioOutput output = new SdlAudioOutput(runtime);
        try
        {
            // SDL installs SIGINT/SIGTERM handlers unless asked not to, and the host — not a
            // library inside it — owns how Ctrl+C ends a sortie.
            sdl.SetHint("SDL_NO_SIGNAL_HANDLERS", "1");
            if (sdl.InitSubSystem(Sdl.InitAudio) < 0)
            {
                return (null, $"SDL could not start its audio subsystem ({sdl.GetErrorS()})");
            }

            output._sdl = sdl;
            output._driver = Marshal.PtrToStringUTF8((nint)sdl.GetCurrentAudioDriver()) ?? "?";

            output._self = GCHandle.Alloc(output, GCHandleType.Normal);
            AudioSpec want = default;
            want.Freq = AudioFormat.SampleRate;

            // AUDIO_S16LSB.  Every RID the port publishes for is little-endian, and SDL is asked
            // for no format changes (the last argument is 0), so `have` comes back equal to `want`.
            want.Format = Sdl.AudioS16;
            want.Channels = (byte)AudioFormat.Channels;
            want.Samples = BufferFrames;
            want.Callback = new PfnAudioCallback(&OnBufferNeeded);
            want.Userdata = (void*)GCHandle.ToIntPtr(output._self);

            AudioSpec have = default;
            output._device = sdl.OpenAudioDevice((byte*)null, 0, &want, &have, 0);
            if (output._device == 0)
            {
                string error = sdl.GetErrorS();
                output.Dispose();
                return (null, $"SDL could not open an output device ({(error.Length == 0 ? "no reason given" : error)})");
            }

            output._callbackFrames = have.Samples;
            output._scratch = new float[have.Samples * AudioFormat.Channels];
            output._pcm = new short[have.Samples * AudioFormat.Channels];
            sdl.PauseAudioDevice(output._device, 0);
            return (output, string.Empty);
        }
        catch (Exception error) when (IsNativeLoadFailure(error))
        {
            output.Dispose();
            return (null, $"the SDL2 native failed part way through ({error.GetType().Name})");
        }
    }

    /// <inheritdoc/>
    public string Report()
    {
        double blockMs = _callbackFrames * 1000.0 / AudioFormat.SampleRate;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[audio] {Name} on {DefaultDevice}: {_callbackFrames} frames per callback "
                + $"({blockMs:0.0} ms, ≈ {2 * blockMs:0.0} ms of device latency), "
                + $"{Volatile.Read(ref _callbacks):N0} callback(s), "
                + $"{Runtime.SampleCursor / (double)AudioFormat.SampleRate:0.0} s rendered");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Sdl? sdl = _sdl;
        if (sdl is not null)
        {
            if (_device != 0)
            {
                // Pause first: the callback must be quiet before the handle it reaches us through
                // is freed.
                sdl.PauseAudioDevice(_device, 1);
                sdl.CloseAudioDevice(_device);
                _device = 0;
            }

            sdl.QuitSubSystem(Sdl.InitAudio);
            _sdl = null;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    /// <summary>
    /// The block bookkeeping, with no device and no SDL in sight: render
    /// <paramref name="frames"/> frames through <paramref name="render"/> in chunks the scratch
    /// buffers can hold, and write them out interleaved as clamped signed 16-bit.
    /// </summary>
    /// <param name="render">The pull source: fill a float buffer with this many frames.</param>
    /// <param name="scratch">The float block the pull source writes into.</param>
    /// <param name="pcm">The signed-16-bit block the device reads.</param>
    /// <param name="frames">How many frames the device asked for.</param>
    /// <returns>How many frames were written.</returns>
    /// <remarks>
    /// The chunk loop exists because the callback's length is the DEVICE's, not ours: SDL grants
    /// the block size it granted at open time, but a backend that later re-opens the device at a
    /// larger period would hand us a longer buffer, and a short write there is a burst of silence
    /// in the middle of an engine note.  Rendering in chunks costs nothing when, as on every
    /// platform measured, the callback is exactly the block that was asked for.
    /// </remarks>
    internal static int RenderPcm(RenderFrames render, float[] scratch, short[] pcm, int frames)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(scratch);
        ArgumentNullException.ThrowIfNull(pcm);

        int channels = AudioFormat.Channels;
        int chunk = Math.Min(scratch.Length, pcm.Length) / channels;
        if (chunk <= 0 || frames <= 0)
        {
            return 0;
        }

        int done = 0;
        while (done < frames)
        {
            int take = Math.Min(chunk, frames - done);
            render(scratch, take);
            for (int i = 0; i < take * channels; i++)
            {
                pcm[(done * channels) + i] = (short)Math.Clamp(
                    (int)(scratch[i] * AudioFormat.Int16Scale), short.MinValue, short.MaxValue);
            }

            done += take;
        }

        return done;
    }

    /// <summary>Whether an exception is "the native is not there", rather than a bug of ours.</summary>
    /// <param name="error">The exception the binding threw.</param>
    private static bool IsNativeLoadFailure(Exception error) => error switch
    {
        DllNotFoundException or EntryPointNotFoundException or FileNotFoundException
            or BadImageFormatException or PlatformNotSupportedException => true,
        TypeInitializationException { InnerException: { } inner } => IsNativeLoadFailure(inner),
        _ => false,
    };

    /// <summary>
    /// SDL's device callback, on SDL's own thread.  Nothing may escape it: an exception crossing
    /// back into native code takes the process down.
    /// </summary>
    /// <param name="userData">The <see cref="GCHandle"/> the output was opened with.</param>
    /// <param name="stream">The device's buffer.</param>
    /// <param name="length">Its length in bytes.</param>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBufferNeeded(void* userData, byte* stream, int length)
    {
        try
        {
            if (GCHandle.FromIntPtr((nint)userData).Target is not SdlAudioOutput output
                || output._disposed)
            {
                NativeMemory.Fill(stream, (nuint)Math.Max(0, length), 0);
                return;
            }

            output.Fill(stream, length);
        }
        catch (Exception)
        {
            // The sound path must never take the game down.  A silent block is the safe answer.
            if (length > 0)
            {
                NativeMemory.Fill(stream, (nuint)length, 0);
            }
        }
    }

    /// <summary>Renders one device block.</summary>
    /// <param name="stream">The device's buffer.</param>
    /// <param name="length">Its length in bytes.</param>
    private void Fill(byte* stream, int length)
    {
        int bytesPerFrame = AudioFormat.Channels * sizeof(short);
        int frames = length / bytesPerFrame;
        if (frames <= 0)
        {
            return;
        }

        if (frames * AudioFormat.Channels > _pcm.Length)
        {
            // The device grew under us (see RenderPcm's remarks).  Grow with it rather than write
            // a short block.
            _pcm = new short[frames * AudioFormat.Channels];
        }

        int written = RenderPcm(Runtime.Render, _scratch, _pcm, frames);
        fixed (short* source = _pcm)
        {
            Buffer.MemoryCopy(source, stream, length, (long)written * bytesPerFrame);
        }

        Interlocked.Add(ref _framesRendered, written);
        Interlocked.Increment(ref _callbacks);
    }

    /// <summary>How many frames the device has pulled.  Diagnostics only.</summary>
    internal long FramesRendered => Interlocked.Read(ref _framesRendered);
}

/// <summary>
/// The pull source a device block is rendered through: fill the first <paramref name="frames"/>
/// interleaved frames of <paramref name="destination"/>.
/// </summary>
/// <param name="destination">The float buffer to fill.</param>
/// <param name="frames">How many frames to render.</param>
/// <remarks>
/// A named delegate rather than an <c>Action&lt;&gt;</c> because <c>AudioRuntime.Render</c> takes a
/// <see cref="Span{T}"/>, which no generic delegate can carry.
/// </remarks>
public delegate void RenderFrames(Span<float> destination, int frames);
