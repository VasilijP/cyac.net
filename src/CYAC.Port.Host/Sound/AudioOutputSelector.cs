namespace CYAC.Port.Host.Sound;

/// <summary>
/// Which device the host opens, and what it says when none of them opens.
/// </summary>
/// <remarks>
/// <para>
/// The whole of the policy is <see cref="Order"/>, which is a pure function of the request and the
/// operating system, and <see cref="Open"/>, which walks that order through a caller-supplied
/// opener.  Neither touches a device, an <c>OperatingSystem</c> call or a P/Invoke, so the tests
/// state the policy for all three platforms and all four requests from this machine — (the port has
/// never been RUN on Windows or Linux) means a policy that can only be observed by running there is
/// a policy nobody has checked.
/// </para>
/// <para>
/// <b>Why macOS keeps AudioQueue.</b>  <see cref="MacAudioQueueOutput"/> is already accepted, has
/// no dependency at all and has been listened to for a year of sorties.  SDL2 is the answer for the
/// platforms that had none; it is not a reason to replace one that works.  <c>--audio-output sdl</c>
/// forces the cross-platform path here so it can be exercised on the development machine, which is
/// the only machine the project has.
/// </para>
/// </remarks>
public static class AudioOutputSelector
{
    /// <summary>The devices to try, in order, for a request on an operating system.</summary>
    /// <param name="requested">What <c>--audio-output</c> asked for.</param>
    /// <param name="isMacOS">Whether this is macOS (the caller passes it; nothing here asks).</param>
    /// <returns>The devices to try; empty when the request was to open nothing.</returns>
    public static IReadOnlyList<AudioOutputKind> Order(AudioOutputKind requested, bool isMacOS) =>
        requested switch
        {
            AudioOutputKind.None => [],
            AudioOutputKind.Mac => [AudioOutputKind.Mac],
            AudioOutputKind.Sdl => [AudioOutputKind.Sdl],

            // Auto: the Mac's own output first on macOS, and SDL2 behind it so a Mac whose
            // AudioQueue refuses still gets sound; SDL2 alone everywhere else.
            _ => isMacOS ? [AudioOutputKind.Mac, AudioOutputKind.Sdl] : [AudioOutputKind.Sdl],
        };

    /// <summary>
    /// Walks <see cref="Order"/> and returns the first device that opened, or a
    /// <see cref="NullAudioOutput"/> naming every device that refused and why.
    /// </summary>
    /// <param name="requested">What <c>--audio-output</c> asked for.</param>
    /// <param name="isMacOS">Whether this is macOS.</param>
    /// <param name="open">
    /// Opens one device: the output, or null and the one line explaining the refusal.
    /// </param>
    /// <returns>An output; never null, and never throwing.</returns>
    public static IAudioOutput Open(
        AudioOutputKind requested,
        bool isMacOS,
        Func<AudioOutputKind, (IAudioOutput? Output, string Reason)> open)
    {
        ArgumentNullException.ThrowIfNull(open);

        IReadOnlyList<AudioOutputKind> order = Order(requested, isMacOS);
        if (order.Count == 0)
        {
            return new NullAudioOutput("--audio-output none opens no device");
        }

        List<string> refusals = new List<string>(order.Count);
        foreach (AudioOutputKind kind in order)
        {
            (IAudioOutput? output, string reason) = open(kind);
            if (output is not null)
            {
                return output;
            }

            refusals.Add(
                $"{AudioOutputKinds.Label(kind)}: "
                    + (string.IsNullOrWhiteSpace(reason) ? "refused" : reason.Trim()));
        }

        return new NullAudioOutput(string.Join("; ", refusals));
    }
}
