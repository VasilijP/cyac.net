namespace CYAC.Port.Audio;

/// <summary>One thing that produces samples into the mixer.</summary>
public interface IAudioSource
{
    /// <summary>Advances one 48 kHz frame and returns this source's contribution.</summary>
    /// <param name="sample">The absolute output sample index.</param>
    /// <returns>Nominally −1..+1.</returns>
    double RenderSample(long sample);
}

/// <summary>A source that produces a STEREO pair (a placed positional voice).</summary>
/// <remarks>
/// The original is mono: one driver, one OPL, one speaker. Everything that renders through this
/// interface is the port's positional layer (<see cref="SourceVoicePool"/>).
/// </remarks>
public interface IStereoAudioSource
{
    /// <summary>Advances one 48 kHz frame.</summary>
    /// <param name="sample">The absolute output sample index.</param>
    /// <param name="left">The left contribution.</param>
    /// <param name="right">The right contribution.</param>
    void RenderSample(long sample, out double left, out double right);
}

/// <summary>
/// The port's mixer: a fixed 48 kHz stereo float host format with every source summed into it.
/// </summary>
/// <remarks>
/// <para>
/// The model takes DOSBox-X's shape: ONE fixed host format,
/// every source rendered or resampled into it, mixing done in software.  Nothing here resamples —
/// the generator runs natively at <see cref="AudioFormat.SampleRate"/>.
/// </para>
/// <para>
/// <b>Where the mute bits are NOT.</b>  <c>g_audio_mute_mask [0xE483]</c> gates at the PRODUCER in
/// the original — bit 0 inside <c>audio_event_dispatcher @image@0x29A40</c> itself
/// (<c>test byte [0xe483],1</c> @<c>image@0x29A43</c>), bits 1..4 inside
/// <c>continuous_audio_state_update</c> before a tone is even chosen (<c>image@0x29D7C</c>,
/// <c>0x2A03F</c>, <c>0x29FD1</c>, <c>0x2A081</c>).  So the mask lives on
/// <see cref="AudioRuntime"/>, next to the dispatcher, and this class carries only what a mixer
/// really owns: the master volume and the source slots.
/// </para>
/// <para>
/// <b>The two unbuilt slots</b> are named because the architecture has to have a place for them (H11
/// 1): <see cref="MusicVoice"/> for a <c>.SNG</c> stream re-rendered on an OPL2-class synth
/// (front-end only in effect — a loaded song FREEZES the SFX generators, see
/// <see cref="AdlToneEngine"/>), and <see cref="PcmVoice"/> for the 50 raw 8-bit 7,168 Hz
/// <c>.SND</c> speech clips (briefing / debrief tiers, never in flight).  Both are out of H11's
/// build scope and both are simply <c>null</c>.
/// </para>
/// </remarks>
public sealed class AudioMixer
{
    private readonly List<IAudioSource> _sources = [];
    private readonly List<IStereoAudioSource> _stereoSources = [];

    /// <summary>Master volume, 0..1.</summary>
    public double MasterVolume { get; set; } = 1.0;

    /// <summary>
    /// The music voice — a <c>.SNG</c> stream on an OPL2-class synth.  Not built.
    /// </summary>
    public IAudioSource? MusicVoice { get; set; }

    /// <summary>
    /// The PCM voice — a <c>.SND</c> speech clip resampled from 7,168 Hz.  Not built.
    /// </summary>
    public IAudioSource? PcmVoice { get; set; }

    /// <summary>How many sources are mixed (excluding the two unbuilt slots).</summary>
    public int SourceCount => _sources.Count;

    /// <summary>Adds a source.</summary>
    /// <param name="source">The source.</param>
    public void Add(IAudioSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _sources.Add(source);
    }

    /// <summary>Adds a STEREO source (a placed positional voice).</summary>
    /// <param name="source">The source.</param>
    public void AddStereo(IStereoAudioSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _stereoSources.Add(source);
    }

    /// <summary>
    /// Renders <paramref name="frames"/> interleaved stereo frames (mono duplicated) starting at
    /// <paramref name="firstSample"/>.
    /// </summary>
    /// <param name="destination">
    /// The output, at least <c>frames × <see cref="AudioFormat.Channels"/></c> long.
    /// </param>
    /// <param name="frames">How many frames to render.</param>
    /// <param name="firstSample">The absolute sample index of the first frame.</param>
    /// <returns>The peak absolute pre-volume amplitude seen, for level reporting.</returns>
    public double Render(Span<float> destination, int frames, long firstSample)
    {
        if (frames < 0 || destination.Length < frames * AudioFormat.Channels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destination),
                $"{frames} stereo frames need {frames * AudioFormat.Channels} floats, "
                    + $"got {destination.Length}");
        }

        double peak = 0;
        for (int i = 0; i < frames; i++)
        {
            long sample = firstSample + i;
            double value = 0;
            for (int s = 0; s < _sources.Count; s++)
            {
                value += _sources[s].RenderSample(sample);
            }

            value += MusicVoice?.RenderSample(sample) ?? 0;
            value += PcmVoice?.RenderSample(sample) ?? 0;

            // The mono sum goes to both ears exactly as before; the positional voices add their
            // own per-ear contribution on top.
            double left = value;
            double right = value;
            for (int s = 0; s < _stereoSources.Count; s++)
            {
                _stereoSources[s].RenderSample(sample, out double l, out double r);
                left += l;
                right += r;
            }

            double magnitude = Math.Max(Math.Abs(left), Math.Abs(right));
            if (magnitude > peak)
            {
                peak = magnitude;
            }

            destination[i * 2] = (float)(left * MasterVolume);
            destination[(i * 2) + 1] = (float)(right * MasterVolume);
        }

        return peak;
    }
}
