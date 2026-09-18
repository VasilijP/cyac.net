namespace CYAC.Port.Host.Sound;

/// <summary>An audio output device the host can push the mixer's frames to.</summary>
/// <remarks>
/// <para>
/// There are now TWO real devices: <see cref="MacAudioQueueOutput"/>, still the macOS default, and
/// <see cref="SdlAudioOutput"/>, which opens on every desktop the port publishes for.
/// <see cref="AudioOutputSelector"/> picks between them and <c>--audio-output</c> forces one.
/// </para>
/// <para>
/// <see cref="NullAudioOutput"/> remains the answer for <c>--wav</c>, for a silent headless run,
/// for <c>--audio-output none</c> and for a machine where every device refused — in which case it
/// carries the reasons, so a player who hears nothing is told why.
/// </para>
/// </remarks>
public interface IAudioOutput : IDisposable
{
    /// <summary>What to call this output in the readout.</summary>
    string Name { get; }

    /// <summary>Whether the device is actually running.</summary>
    bool IsRunning { get; }

    /// <summary>A one-line health report for the run's summary.</summary>
    string Report();
}
