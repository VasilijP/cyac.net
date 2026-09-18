namespace CYAC.Port.Audio;

/// <summary>The one host audio format everything in this assembly speaks.</summary>
/// <remarks>
/// Fixed 48 kHz stereo float, modelled on DOSBox-X's mixer: one fixed host format,
/// every source resampled into it, mixing in software.  Nothing here resamples yet — the
/// generator renders natively at this rate, which is the whole point of the refined model.
/// </remarks>
public static class AudioFormat
{
    /// <summary>Frames per second.</summary>
    public const int SampleRate = 48_000;

    /// <summary>Channels in the host format.</summary>
    public const int Channels = 2;

    /// <summary>
    /// The driver's own tick rate, in hertz — how often <c>cmd 0x06</c> reaches the generators.
    /// </summary>
    /// <remarks>
    /// 256 Hz: the game's <c>pit_install_256hz</c> drives the tick the driver's cmd-06 sequencer
    /// advances on (a recorded session's inter-tick spacing agrees).  One tick
    /// is <see cref="SamplesPerTick"/> = 187.5 output samples, which is why the generator's ramps
    /// are interpolated rather than stepped.
    /// </remarks>
    public const int TickRate = 256;

    /// <summary>Output samples in one driver tick (187.5).</summary>
    public const double SamplesPerTick = (double)SampleRate / TickRate;

    /// <summary>
    /// The scale from the generator's nominal −1..+1 to signed 16-bit, kept identical to the
    /// emulator's mixer so a port WAV and an emulator WAV of the same sound have the same level.
    /// </summary>
    public const double Int16Scale = 22_000.0;
}
