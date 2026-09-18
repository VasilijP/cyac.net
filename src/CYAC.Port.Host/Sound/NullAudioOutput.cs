namespace CYAC.Port.Host.Sound;

/// <summary>
/// Windows and Linux open <see cref="SdlAudioOutput"/> now. The output for a run that opens no
/// device at all: <c>--wav</c>, a <c>--headless</c> run without one, <c>--audio-output none</c>,
/// and the last resort when every device refused.
/// </summary>
/// <remarks>
/// It never renders, so the generator's cursor does not advance and the run costs nothing.  The
/// sound path itself is still built and still receives every event — <c>--wav</c> renders it, and
/// the driver-call census still counts it, which is how a developer verifies the audio on a machine
/// with no device at all.
/// </remarks>
public sealed class NullAudioOutput : IAudioOutput
{
    /// <summary>Why there is no device.</summary>
    private readonly string _reason;

    /// <summary>Creates the silent output.</summary>
    /// <param name="reason">What to tell the user.</param>
    public NullAudioOutput(string reason) => _reason = reason;

    /// <inheritdoc/>
    public string Name => "none";

    /// <inheritdoc/>
    public bool IsRunning => false;

    /// <inheritdoc/>
    public string Report() => $"[audio] no output device — {_reason}";

    /// <inheritdoc/>
    public void Dispose()
    {
        // Nothing to release.
    }
}
