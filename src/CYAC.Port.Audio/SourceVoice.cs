namespace CYAC.Port.Audio;

/// <summary>
/// ONE positional voice: a whole tone generator of its own, placed in the stereo field.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an instance and not a driver slot.</b> The shipped driver has four SFX slots
/// (<c>AdlToneEngine</c>'s slot array, drv <c>@0x1568</c>'s descriptor table), and the 1991 game
/// never needs a fifth because it sounds exactly one engine.  A port that mixes eight bandits cannot
/// squeeze them through four slots without stealing them from each other, so each source gets its
/// OWN <see cref="AdlToneEngine"/> over its own copy of the driver image — the generator is a pure
/// function of (image, calls, cadence), so N instances are N independent FM synths and nothing about
/// any one of them differs from the ported single one (H11's sample-exact proof applies to each).
/// The deviation this makes is precisely that: the driver's four-slot pre-emption can no longer
/// steal one aeroplane's engine for another's.
/// </para>
/// <para>
/// <b>What is applied to its samples.</b>  A per-ear gain (distance × pan, smoothed over
/// <see cref="SmoothingSeconds"/> so a per-frame update cannot click) and an optional one-pole
/// low-pass — the port's "heard through the air" colour, which the original has no equivalent of.
/// </para>
/// </remarks>
public sealed class SourceVoice
{
    /// <summary>How long a gain change takes to arrive, in seconds.</summary>
    /// <remarks>
    /// The placement is recomputed once a SIMULATION frame; interpolating it over ~10 ms of audio is
    /// what keeps a fast fly-past from stepping.  It is a port-side smoothing constant and nothing
    /// of the original's.
    /// </remarks>
    public const double SmoothingSeconds = 0.010;

    private readonly AdlToneEngine _engine;
    private readonly double _smoothing =
        1.0 - Math.Exp(-1.0 / (SmoothingSeconds * AudioFormat.SampleRate));

    private double _gainLeft;
    private double _gainRight;
    private double _targetLeft;
    private double _targetRight;
    private double _lowPassCoefficient = 1.0;
    private double _lowPassStateLeft;
    private double _lowPassStateRight;
    private int _tone = ContinuousChannels.NoTone;
    private int _pitch;
    private int _volume;

    /// <summary>Builds a voice over its own copy of the driver module.</summary>
    /// <param name="driver">The <c>adldrive.drv</c> module.</param>
    /// <param name="id">The source identity (a pool object's arena offset, or 0 for the player).</param>
    public SourceVoice(AdlDriverImage driver, int id)
    {
        ArgumentNullException.ThrowIfNull(driver);
        _engine = new AdlToneEngine(driver);
        Id = id;
    }

    /// <summary>Which source this voice belongs to.</summary>
    public int Id { get; private set; }

    /// <summary>
    /// Hands a SILENCED voice to another source, so a bandit crossing the pool's edge does not
    /// allocate a whole new generator every frame.
    /// </summary>
    /// <param name="id">The new source's arena offset.</param>
    /// <remarks>
    /// Only legal after <see cref="Silence"/>: the generator's state is the driver's own mutable
    /// image, and cmd 8 is what puts it back where a fresh copy would be.
    /// </remarks>
    public void Reassign(int id)
    {
        Id = id;
        Starts = 0;
        Updates = 0;
        Stops = 0;
    }

    /// <summary>The engine tone it is holding, or <see cref="ContinuousChannels.NoTone"/>.</summary>
    public int Tone => _tone;

    /// <summary>Its last pitch argument.</summary>
    public int Pitch => _pitch;

    /// <summary>Its last volume argument.</summary>
    public int Volume => _volume;

    /// <summary>How many <c>cmd 0</c> starts this voice has issued.</summary>
    public long Starts { get; private set; }

    /// <summary>How many <c>cmd 4</c> updates.</summary>
    public long Updates { get; private set; }

    /// <summary>How many <c>cmd 2</c> stops.</summary>
    public long Stops { get; private set; }

    /// <summary>The range the last placement was computed at, in feet (diagnostics).</summary>
    public int RangeFeet { get; private set; }

    /// <summary>The pan the last placement resolved to (diagnostics).</summary>
    public double PanPosition { get; private set; }

    /// <summary>The generator, for tests.</summary>
    public AdlToneEngine Engine => _engine;

    /// <summary>Places the voice: a distance gain, a pan and an optional low-pass.</summary>
    /// <param name="gain">The distance gain (1.0 = no host-side attenuation).</param>
    /// <param name="pan">−1..+1.</param>
    /// <param name="lowPassHz">The cutoff, or 0 for none.</param>
    /// <param name="rangeFeet">The range this placement came from (diagnostics only).</param>
    /// <param name="immediate">Skip the smoothing (a voice that has just been allocated).</param>
    public void Place(double gain, double pan, double lowPassHz, int rangeFeet, bool immediate = false)
    {
        (double left, double right) = PositionalAudio.ChannelGains(pan);
        _targetLeft = left * gain;
        _targetRight = right * gain;
        _lowPassCoefficient = PositionalAudio.LowPassCoefficient(lowPassHz);
        RangeFeet = rangeFeet;
        PanPosition = pan;
        if (immediate)
        {
            _gainLeft = _targetLeft;
            _gainRight = _targetRight;
        }
    }

    /// <summary>
    /// The CONTINUOUS-channel commit protocol, for this voice's engine tone
    /// (<c>image@0x29F4A..0x29FB9</c>): stop what changed, start what is new, update what only moved.
    /// </summary>
    /// <param name="tone">The wanted tone, or <see cref="ContinuousChannels.NoTone"/>.</param>
    /// <param name="pitch">Its pitch argument.</param>
    /// <param name="volume">Its volume argument.</param>
    /// <param name="sample">The output sample the call lands at.</param>
    /// <param name="census">Where to log the calls, or null.</param>
    /// <param name="site">The census site name.</param>
    public void CommitEngineTone(
        int tone, int pitch, int volume, long sample, DriverCallCensus? census, string site)
    {
        int previous = _tone;
        if (tone != previous && previous >= 0)
        {
            Dispatch(2, previous, 0, 0, sample, census, site);      // image@0x29F57
            Stops++;
        }

        if (tone >= 0)
        {
            if (tone != previous)
            {
                Dispatch(0, tone, pitch, volume, sample, census, site);   // image@0x29F7A
                Starts++;
            }
            else if (pitch != _pitch || volume != _volume)
            {
                Dispatch(4, tone, pitch, volume, sample, census, site);   // image@0x29FA0
                Updates++;
            }
        }

        _tone = tone;
        _pitch = pitch;
        _volume = volume;
    }

    /// <summary>Plays a one-shot tone on this voice (a gun burst, an impact).</summary>
    /// <param name="tone">The tone id.</param>
    /// <param name="pitch">Its pitch argument.</param>
    /// <param name="volume">Its volume argument.</param>
    /// <param name="sample">The output sample the call lands at.</param>
    /// <param name="census">Where to log the call, or null.</param>
    /// <param name="site">The census site name.</param>
    public void PlayOneShot(
        int tone, int pitch, int volume, long sample, DriverCallCensus? census, string site) =>
        Dispatch(0, tone, pitch, volume, sample, census, site);

    /// <summary>Raw access to this voice's generator (the driver's own command words).</summary>
    /// <param name="command">The driver command.</param>
    /// <param name="toneId">Argument 0.</param>
    /// <param name="pitch">Argument 1.</param>
    /// <param name="volume">Argument 2.</param>
    /// <param name="sample">The output sample the call lands at.</param>
    /// <param name="census">Where to log it, or null.</param>
    /// <param name="site">The census site name.</param>
    public void Dispatch(
        int command,
        int toneId,
        int pitch,
        int volume,
        long sample,
        DriverCallCensus? census,
        string site)
    {
        ushort cmd = unchecked((ushort)command);
        ushort a0 = unchecked((ushort)toneId);
        ushort a1 = unchecked((ushort)pitch);
        ushort a2 = unchecked((ushort)volume);
        _engine.ApplyCommand(cmd, a0, a1, a2, sample);
        census?.Note(new PortDriverCall(sample, cmd, a0, a1, a2, site));
    }

    /// <summary>The driver's 256 Hz tick (<c>cmd 6</c>), never censused — one per voice per tick.</summary>
    /// <param name="sample">The output sample the tick lands at.</param>
    public void Tick(long sample) => _engine.ApplyCommand(6, 0, 0, 0, sample);

    /// <summary>
    /// <c>cmd 8</c> — silence everything this voice holds
    /// (<c>audio_continuous_state_reset @image@0x298D2</c> uses the same command).
    /// </summary>
    /// <param name="sample">The output sample it lands at.</param>
    public void Silence(long sample)
    {
        _engine.ApplyCommand(8, 0, 0, 0, sample);
        _tone = ContinuousChannels.NoTone;
        _pitch = 0;
        _volume = 0;
        _lowPassStateLeft = 0;
        _lowPassStateRight = 0;
    }

    /// <summary>Renders one stereo frame.</summary>
    /// <param name="sample">The absolute output sample index.</param>
    /// <param name="left">This voice's left contribution.</param>
    /// <param name="right">…and right.</param>
    public void RenderSample(long sample, out double left, out double right)
    {
        _gainLeft += (_targetLeft - _gainLeft) * _smoothing;
        _gainRight += (_targetRight - _gainRight) * _smoothing;

        double mono = _engine.RenderSample(sample);
        left = mono * _gainLeft;
        right = mono * _gainRight;

        if (_lowPassCoefficient < 1.0)
        {
            _lowPassStateLeft += (left - _lowPassStateLeft) * _lowPassCoefficient;
            _lowPassStateRight += (right - _lowPassStateRight) * _lowPassCoefficient;
            left = _lowPassStateLeft;
            right = _lowPassStateRight;
        }
    }
}
