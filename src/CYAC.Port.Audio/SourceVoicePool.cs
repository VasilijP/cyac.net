using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Geometry;

namespace CYAC.Port.Audio;

/// <summary>
/// The positional-source pool: the K nearest audible aeroplanes, each on its own
/// <see cref="SourceVoice"/>, plus the player's own voice (which is never one of the K).
/// </summary>
/// <remarks>
/// <para>
/// <b>Entirely the port's</b> (an authorised deviation).  The 1991 game sounds ONE engine —
/// <c>continuous_audio_state_update @image@0x29D2E</c> picks the player's aeroplane, or the watched
/// one in the two target views, and there is no second engine anywhere in the driver-call stream.
/// What is NOT the port's is every value the pool computes: the tone family is the class record's
/// own jet bit (<c>image@0x29E07</c>), the pitch is <c>engine_sound_pitch_compute
/// @image@0x29C20</c>, the volume is <c>engine_sound_volume_compute @image@0x29C9E</c>'s FLY-BY arm
/// — the one the original itself uses when the camera is outside, watching an aeroplane pass — and
/// the audible radius is the impact dispatcher's own <c>0x2710</c> (<c>image@0x29BB5</c>).
/// </para>
/// <para>
/// <b>The player's voice.</b>  Slot <see cref="PlayerVoiceId"/> exists whenever the sound path does.
/// Its TONE is not chosen here: it is whatever <see cref="ContinuousChannels"/> commits, which keeps
/// the original's channel A intact — the five engine tones, the idle-send protocol and the mechanism
/// sound 0x13 all reach the player's voice unchanged.  The pool only PLACES it (distance, pan,
/// filter), which is what lets the engine keep sounding across a view change with no stop/start.
/// </para>
/// </remarks>
public sealed class SourceVoicePool : IStereoAudioSource
{
    /// <summary>The voice id the player's own aeroplane always occupies.</summary>
    /// <remarks>Zero is not a legal pool-object arena offset, so it can never collide with an AI's.</remarks>
    public const int PlayerVoiceId = 0;

    /// <summary>The default cap the host's <c>--sound-sources</c> starts from.</summary>
    public const int DefaultMaxSources = 8;

    /// <summary>
    /// The default "heard through the air" cutoff, in hertz — the port's outside colour.
    /// </summary>
    /// <remarks>
    /// DEVIATION, and a deliberately small one (H13: "keep it small").  The original has no filter
    /// of any kind: the driver writes OPL registers and that is the whole signal path. This one pole
    /// is what makes the player's own engine change "a bit" when the camera leaves the cockpit, and
    /// it applies to every voice heard from outside.  <c>--engine-lowpass 0</c> removes it entirely.
    /// </remarks>
    public const double DefaultOutsideLowPassHz = 2_000.0;

    private readonly AdlDriverImage _driver;
    private readonly List<SourceVoice> _voices = [];
    private readonly List<SourceVoice> _spare = [];
    private readonly List<(int Index, int Range)> _ranked = [];
    private readonly SourceVoice _player;

    /// <summary>Builds the pool over the driver module every voice copies.</summary>
    /// <param name="driver">The <c>adldrive.drv</c> module.</param>
    public SourceVoicePool(AdlDriverImage driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        _driver = driver;
        _player = new SourceVoice(driver, PlayerVoiceId);
        _player.Place(1.0, 0.0, 0.0, 0, immediate: true);
    }

    /// <summary>How many NON-player sources may sound at once (<c>--sound-sources</c>).</summary>
    public int MaxSources { get; set; } = DefaultMaxSources;

    /// <summary>Beyond this range, in feet, a source is not mixed at all.</summary>
    public int AudibleRadiusFeet { get; set; } = PositionalAudio.AudibleRadiusFeet;

    /// <summary>The outside-listening low-pass cutoff, in hertz; 0 switches it off.</summary>
    public double OutsideLowPassHz { get; set; } = DefaultOutsideLowPassHz;

    /// <summary>The player's own voice — placed here, but toned by <see cref="ContinuousChannels"/>.</summary>
    public SourceVoice PlayerVoice => _player;

    /// <summary>The live non-player voices.</summary>
    public IReadOnlyList<SourceVoice> Voices => _voices;

    /// <summary>How many retired voices are kept for re-use rather than re-allocated.</summary>
    public const int SpareCapacity = 8;

    /// <summary>How many voices have ever been allocated.</summary>
    public long Allocations { get; private set; }

    /// <summary>How many allocations a retired voice satisfied instead.</summary>
    public long Reuses { get; private set; }

    /// <summary>How many were evicted (out of range, gone, or beyond the cap).</summary>
    public long Evictions { get; private set; }

    /// <summary>How many gun sounds were dropped because their source had no voice.</summary>
    public long DroppedFireSounds { get; private set; }

    /// <summary>The nearest non-player source's range last frame, in feet; −1 when there was none.</summary>
    public int NearestRangeFeet { get; private set; } = -1;

    /// <summary>The sum of live non-player voices over every frame — for a per-source cost.</summary>
    public long VoiceFrames { get; private set; }

    /// <summary>How many frames <see cref="Update"/> has run.</summary>
    public long Frames { get; private set; }

    /// <summary>Finds the live voice of a source, or null.</summary>
    /// <param name="id">The pool object's arena offset.</param>
    /// <returns>Its voice, or null when it has none this frame.</returns>
    public SourceVoice? Find(int id)
    {
        for (int i = 0; i < _voices.Count; i++)
        {
            if (_voices[i].Id == id)
            {
                return _voices[i];
            }
        }

        return null;
    }

    /// <summary>Notes that a gun sound had nowhere to go.</summary>
    public void NoteDroppedFireSound() => DroppedFireSounds++;

    /// <summary>
    /// Rebuilds the pool for one frame: rank by range, keep the nearest
    /// <see cref="MaxSources"/>, and place / tone every voice.
    /// </summary>
    /// <param name="listener">The camera.</param>
    /// <param name="sources">Everything that could sound this frame.</param>
    /// <param name="sample">The output sample the frame's calls land at.</param>
    /// <param name="driverType">The audio driver type <c>[0xE482]</c> (pitch law input).</param>
    /// <param name="census">Where the calls are logged, or null.</param>
    public void Update(
        in AudioListener listener,
        IReadOnlyList<AudioSourceState> sources,
        long sample,
        int driverType,
        DriverCallCensus? census)
    {
        ArgumentNullException.ThrowIfNull(sources);

        // ── the player's voice: placement only, its tone belongs to the continuous channel ──────
        NearestRangeFeet = -1;
        bool playerSeen = false;
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].IsPlayer)
            {
                PlacePlayer(listener, sources[i]);
                playerSeen = true;
                break;
            }
        }

        if (!playerSeen)
        {
            _player.Place(1.0, 0.0, listener.CockpitInterior ? 0.0 : OutsideLowPassHz, 0);
        }

        // ── rank the rest by the original's own range function ──────────────────────────────────
        _ranked.Clear();
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].IsPlayer)
            {
                continue;
            }

            int range = ViewAnchorRange.Compute(sources[i].Position, listener.Position);
            if (range <= AudibleRadiusFeet)
            {
                _ranked.Add((i, range));
            }
        }

        _ranked.Sort(static (a, b) => a.Range.CompareTo(b.Range));
        int keep = Math.Min(Math.Max(MaxSources, 0), _ranked.Count);
        if (keep > 0)
        {
            NearestRangeFeet = _ranked[0].Range;
        }

        // ── evict every voice the frame did not keep ────────────────────────────────────────────
        for (int v = _voices.Count - 1; v >= 0; v--)
        {
            bool kept = false;
            for (int r = 0; r < keep; r++)
            {
                if (sources[_ranked[r].Index].Id == _voices[v].Id)
                {
                    kept = true;
                    break;
                }
            }

            if (!kept)
            {
                _voices[v].Silence(sample);
                if (_spare.Count < SpareCapacity)
                {
                    _spare.Add(_voices[v]);          // reusable: silenced, so its generator is clean
                }

                _voices.RemoveAt(v);
                Evictions++;
            }
        }

        // ── place and tone the survivors ────────────────────────────────────────────────────────
        for (int r = 0; r < keep; r++)
        {
            AudioSourceState state = sources[_ranked[r].Index];
            int range = _ranked[r].Range;
            SourceVoice? voice = Find(state.Id);
            bool fresh = voice is null;
            if (voice is null)
            {
                if (_spare.Count > 0)
                {
                    voice = _spare[^1];
                    _spare.RemoveAt(_spare.Count - 1);
                    voice.Reassign(state.Id);
                    Reuses++;
                }
                else
                {
                    voice = new SourceVoice(_driver, state.Id);
                    Allocations++;
                }

                _voices.Add(voice);
            }

            Place(voice, listener, state, range, fresh);

            int tone = EngineToneFor(state);
            int pitch = ContinuousChannels.EnginePitch16(
                tone, state.AirspeedFeetPerSecond, state.ThrottlePercent, 0, driverType);

            // The FLY-BY arm of the original's own volume law (view 0x0D): the base level it uses
            // for an aeroplane watched from outside, minus its own distance deduction.
            int volume = ContinuousChannels.EngineVolume(tone, targetView: true, viewMode: 0x0D, range);
            voice.CommitEngineTone(tone, pitch, volume, sample, census, "source_engine");
        }

        Frames++;
        VoiceFrames += _voices.Count;
    }

    /// <summary>Silences and drops every voice (a session reopen, or sound going off).</summary>
    /// <param name="sample">The output sample the silence lands at.</param>
    public void Reset(long sample)
    {
        _player.Silence(sample);
        _player.Place(1.0, 0.0, 0.0, 0, immediate: true);
        for (int i = 0; i < _voices.Count; i++)
        {
            _voices[i].Silence(sample);
        }

        _voices.Clear();
        _spare.Clear();
        NearestRangeFeet = -1;
    }

    /// <summary>Issues the driver's 256 Hz tick to every live voice.</summary>
    /// <param name="sample">The output sample the tick lands at.</param>
    public void Tick(long sample)
    {
        _player.Tick(sample);
        for (int i = 0; i < _voices.Count; i++)
        {
            _voices[i].Tick(sample);
        }
    }

    /// <summary>Renders one stereo frame of every live voice.</summary>
    /// <param name="sample">The absolute output sample index.</param>
    /// <param name="left">The summed left contribution.</param>
    /// <param name="right">…and right.</param>
    public void RenderSample(long sample, out double left, out double right)
    {
        _player.RenderSample(sample, out left, out right);
        for (int i = 0; i < _voices.Count; i++)
        {
            _voices[i].RenderSample(sample, out double l, out double r);
            left += l;
            right += r;
        }
    }

    /// <summary>
    /// The original's own engine-tone family selector, <c>image@0x29E04..0x29E31</c>, for one source.
    /// </summary>
    /// <param name="state">The source.</param>
    /// <returns>0/1/2 for a jet, 3/4 for a piston.</returns>
    public static int EngineToneFor(in AudioSourceState state) =>
        state.JetEngine
            ? state.EngineDamaged ? 2 : state.Afterburner ? 1 : 0   // image@0x29E0D / 0x29E1B
            : state.EngineDamaged ? 4 : 3;                          // image@0x29E27

    private void PlacePlayer(in AudioListener listener, in AudioSourceState state)
    {
        if (listener.CockpitInterior)
        {
            // Inside your own aeroplane: centred, unattenuated, unfiltered — bit-for-bit the level
            // H11 rendered, because the pan is 0 and the range is 0.
            _player.Place(1.0, 0.0, 0.0, 0);
            return;
        }

        int range = ViewAnchorRange.Compute(state.Position, listener.Position);
        Place(_player, listener, state, range, immediate: false);
    }

    private void Place(
        SourceVoice voice,
        in AudioListener listener,
        in AudioSourceState state,
        int range,
        bool immediate)
    {
        double gain = PositionalAudio.ExtraGainBeyondDriverRange(range);
        double pan = PositionalAudio.Pan(
            (state.Position.X - listener.Position.X) / 256.0,
            (state.Position.Y - listener.Position.Y) / 256.0,
            (state.Position.Z - listener.Position.Z) / 256.0,
            listener.RightX,
            listener.RightY,
            listener.RightZ);
        voice.Place(gain, pan, OutsideLowPassHz, range, immediate);
    }
}
