using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Sim;
using CYAC.Port.Core.Sim.Combat.Geometry;
using CYAC.Port.Core.Sim.Session;

namespace CYAC.Port.Audio;

/// <summary>
/// The port's sound path, whole: <c>audio_event_dispatcher @image@0x29A40</c>, the SFX trampolines
/// behind it, the two continuous channels, the tone generator and the mixer.
/// </summary>
/// <remarks>
/// <para>
/// The host owns one of these.  The simulation side calls <see cref="PerFrame"/> once a frame and
/// the <see cref="IAudioEvents"/> methods whenever a kernel fires a sound; the audio side calls
/// <see cref="Render"/> to fill a device buffer or a WAV.  Those happen on different threads, so
/// every entry point takes one lock.
/// </para>
/// <para>
/// <b>Time.</b>  The runtime keeps a 48 kHz sample cursor and issues the driver's own <c>cmd 0x06</c>
/// tick every <see cref="AudioFormat.SamplesPerTick"/> samples inside <see cref="Render"/>.  In the
/// original that tick comes from the game's 256 Hz PIT interrupt (<c>audio_driver_tick_trampoline
/// @image@0x29A70</c> pushes cmd 6); the port has no interrupt, and driving it from the RENDER
/// keeps the generators' timing exactly proportional to the audio the listener hears — including in
/// a headless <c>--wav</c> run, where there is no wall clock at all.
/// </para>
/// <para>
/// <b>Determinism.</b> Nothing here writes simulation state, and the per-shot randomization comes
/// from the port's own <c>Audio.Jitter</c> stream (<see cref="RandomStreamId.AudioJitter"/>), never
/// from a <c>Sim.*</c> stream.  The shipped game draws its SFX jitter from the single global LFSR,
/// which is one of the reasons its sound settings can change a dogfight; the port's stream split is
/// the deliberate fix.
/// </para>
/// </remarks>
public sealed class AudioRuntime : IAudioEvents, IAudioSource
{
    /// <summary>Beyond this range the impact sound is dropped (<c>cmp si,0x2710</c> @<c>image@0x29BB5</c>).</summary>
    public const int ImpactAudibleRange = 0x2710;

    private readonly object _lock = new();
    private readonly AdlToneEngine _engine;
    private readonly AudioMixer _mixer;
    private readonly ContinuousChannels _continuous = new();
    private readonly FxRandomStream _jitter;
    private readonly DriverCallCensus? _census;
    private readonly SourceVoicePool _sources;
    private readonly Action<int, int, int, int> _engineDispatch;
    private readonly Action<int, int, int, int> _cockpitDispatch;

    private long _sample;
    private double _nextTick;
    private CombatPosition _listener;
    private long _frameTimeAccum;
    private string _site = "runtime";

    /// <summary>Builds the sound path over a validated driver module.</summary>
    /// <param name="driver">The <c>adldrive.drv</c> module.</param>
    /// <param name="jitter">
    /// The <c>Audio.Jitter</c> stream the per-shot pitch and volume draws come from.
    /// </param>
    /// <param name="census">An optional driver-call census.</param>
    public AudioRuntime(AdlDriverImage driver, FxRandomStream jitter, DriverCallCensus? census = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(jitter);
        _engine = new AdlToneEngine(driver);
        _jitter = jitter;
        _census = census;
        _mixer = new AudioMixer();
        _mixer.Add(this);

        // The positional layer.  The player's own engine lives on its voice from the first frame,
        // which is what lets a view change be a placement change instead of a stop/start.
        _sources = new SourceVoicePool(driver);
        _mixer.AddStereo(_sources);
        _engineDispatch = (c, t, p, v) => DispatchToPlayerVoice(c, t, p, v);
        _cockpitDispatch = (c, t, p, v) => DispatchLocked(c, t, p, v);
    }

    /// <summary>The generator engine, for tests and diagnostics.</summary>
    public AdlToneEngine Engine => _engine;

    /// <summary>The mixer — master volume and the two unbuilt voice slots.</summary>
    public AudioMixer Mixer => _mixer;

    /// <summary>The two continuous channels.</summary>
    public ContinuousChannels Continuous => _continuous;

    /// <summary>The positional voices: the player's own, plus the K nearest sources.</summary>
    public SourceVoicePool Sources => _sources;

    /// <summary>The driver-call census, when one was supplied.</summary>
    public DriverCallCensus? Census => _census;

    /// <summary>
    /// <c>g_audio_mute_mask [0xE483]</c> — bit 0 gates the dispatcher itself, bits 1..4 the
    /// continuous channels.
    /// </summary>
    /// <remarks>
    /// The shipped <c>yeager.cfg</c> holds <c>0xFE</c> — bit 0 CLEAR.  The port's default is
    /// <c>0xFF</c> (everything audible) because the port's own default is "sound on"; the host's
    /// <c>--sound-mask</c> overrides it.
    /// </remarks>
    public byte MuteMask { get; set; } = 0xFF;

    /// <summary>
    /// Whether a driver is loaded — the dispatcher's second guard,
    /// <c>cmp word [0x346a],0</c> @<c>image@0x29A4A</c>.
    /// </summary>
    public bool DriverLoaded { get; set; } = true;

    /// <summary>
    /// <c>g_deadzone_enable_flag [0xE470]</c> — the soft-impact arm's branch
    /// (<c>image@0x29BFF</c>).
    /// </summary>
    public bool WarningsEnabled { get; set; } = true;

    /// <summary>How many driver calls the runtime has made.</summary>
    public long DriverCalls { get; private set; }

    /// <summary>How many calls the mute mask or the missing driver swallowed.</summary>
    public long GuardedCalls { get; private set; }

    /// <summary>How many <c>cmd 0x06</c> ticks the render has issued.</summary>
    public long Ticks { get; private set; }

    /// <summary>The render cursor, in 48 kHz samples.</summary>
    public long SampleCursor
    {
        get
        {
            lock (_lock)
            {
                return _sample;
            }
        }
    }

    /// <summary>The loudest pre-volume amplitude any rendered block reached.</summary>
    public double PeakAmplitude { get; private set; }

    // ------------------------------------------------------------------- the dispatcher itself

    /// <summary>
    /// <c>audio_event_dispatcher @image@0x29A40</c> — the one relay every in-flight sound goes
    /// through.
    /// </summary>
    /// <remarks>
    /// Two guards and then the far call into the driver: <c>test byte [0xe483],1</c>
    /// (@<c>image@0x29A43</c>) and <c>cmp word [0x346a],0</c> (@<c>image@0x29A4A</c>), then
    /// <c>lcall [0xbb36]</c> with the four argument words (@<c>image@0x29A5E</c>).
    /// </remarks>
    /// <param name="command">The driver command word.</param>
    /// <param name="toneId">The tone id.</param>
    /// <param name="pitch">The pitch argument.</param>
    /// <param name="volume">The volume argument.</param>
    public void Dispatch(int command, int toneId, int pitch, int volume)
    {
        lock (_lock)
        {
            DispatchLocked(command, toneId, pitch, volume);
        }
    }

    private void DispatchLocked(int command, int toneId, int pitch, int volume)
    {
        if ((MuteMask & (byte)AudioMuteFlags.Master) == 0 || !DriverLoaded)
        {
            GuardedCalls++;
            return;
        }

        ushort cmd = unchecked((ushort)command);
        ushort a0 = unchecked((ushort)toneId);
        ushort a1 = unchecked((ushort)pitch);
        ushort a2 = unchecked((ushort)volume);
        _engine.ApplyCommand(cmd, a0, a1, a2, _sample);
        _census?.Note(new PortDriverCall(_sample, cmd, a0, a1, a2, _site));
        DriverCalls++;
    }

    // ---------------------------------------------------------------------- the SFX trampolines

    /// <inheritdoc/>
    public void PlayTone(int toneId)
    {
        lock (_lock)
        {
            _site = "fixed_tone";
            DispatchLocked(0, toneId, 0, 0);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <c>sfx_object_impact_dispatch @image@0x29BA3</c>, transcribed:
    /// </para>
    /// <code>
    /// range = object_range_from_view_anchor(pos);   if (range &gt;= 0x2710) return;   // 0x29BB5
    /// if (AL != 0) {                                                                  // 0x29BBB
    ///     volume = 0x3F − (8 × range / 0x2710);                                        // 0x29BC3
    ///     pitch  = prng_rand_bounded(3) + 4;                                           // 0x29BD4
    ///     tone   = (prng_rand8() &amp; 1) ? 0x15 : 0x16;                                // 0x29BE0
    ///     dispatch(0, tone, pitch, volume);                                            // 0x29BF7
    /// } else if ([0xE470] != 0) dispatch(0, 0x1C);                                     // 0x29C06
    /// else                      sfx_play_tone14_fallback();                            // 0x29C14
    /// </code>
    /// </remarks>
    public void ObjectImpact(CombatPosition position, bool loud)
    {
        lock (_lock)
        {
            int range = ViewAnchorRange.Compute(position, _listener);
            if (range >= ImpactAudibleRange)
            {
                return;
            }

            _site = "object_impact";
            if (loud)
            {
                int volume = 0x3F - (8 * range / ImpactAudibleRange);         // image@0x29BC3..0x29BD1
                int pitch = (int)_jitter.NextBounded(3) + 4;                  // image@0x29BD4..0x29BDC
                int tone = (_jitter.NextUInt32() & 1) != 0 ? SfxTone.ImpactA : SfxTone.ImpactB;
                DispatchLocked(0, tone, pitch, volume);
            }
            else if (WarningsEnabled)
            {
                DispatchLocked(0, SfxTone.MenuClick, 0, 0);                   // image@0x29C06
            }
            else
            {
                DispatchLocked(0, SfxTone.ImpactFallback, 0, 0);              // image@0x29C14
            }
        }
    }

    /// <inheritdoc/>
    public void WeaponFire(bool airToGround, int volume)
    {
        lock (_lock)
        {
            _site = "weapon_fire";
            if (airToGround)
            {
                DispatchLocked(0, SfxTone.AirToGroundFire, 0, 0);             // image@0x29A82
            }
            else
            {
                DispatchLocked(0, SfxTone.Gun, 5, volume & 0xFF);             // image@0x29A90..0x29AA1
            }
        }
    }

    /// <inheritdoc/>
    public void MechanismSound(int frameTimeUnits)
    {
        lock (_lock)
        {
            _continuous.BumpMechanismDeadline(_frameTimeAccum, frameTimeUnits);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <c>sfx_play_tone15_random @image@0x29ACA</c>: <c>volume = 0x3F − rand(3)</c>
    /// (<c>image@0x29ACA..0x29AD7</c>), <c>pitch = rand(4) + 3</c> (<c>image@0x29AD8..0x29AE3</c>).
    /// </para>
    /// <para>
    /// <c>sfx_play_tone16_random @image@0x29AF2</c>: <c>volume = 0x3F − rand(3)</c>, <c>pitch = 6 −
    /// rand(2)</c> (<c>image@0x29B00..0x29B0D</c>).  This is the trampoline behind
    /// <c>IEngagementNodeEvents.PlayDepartureTone</c> — <c>lcall 0x3981:0x2e2</c>
    /// @<c>image@0x08BA0</c>, and <c>0x3981:0x2E2</c> resolves to <c>image@0x29AF2</c>, not to the
    /// "sfx_play_tone2e2 @image@0x29AE2" the seam's own doc comment names.
    /// </para>
    /// </remarks>
    public void PlayRandomizedImpactTone(bool variantB)
    {
        lock (_lock)
        {
            int volume = 0x3F - (int)_jitter.NextBounded(3);
            int pitch = variantB ? 6 - (int)_jitter.NextBounded(2) : (int)_jitter.NextBounded(4) + 3;
            _site = variantB ? "tone16_random" : "tone15_random";
            DispatchLocked(0, variantB ? SfxTone.ImpactB : SfxTone.ImpactA, pitch, volume);
        }
    }

    /// <summary>
    /// <c>menu_key_sfx @image@0x29B8F</c> — the UI click; <c>AL == 1</c> plays 0x09, anything else
    /// 0x1C.
    /// </summary>
    /// <param name="selector">The original's <c>AL</c>.</param>
    public void PlayMenuKey(int selector)
    {
        lock (_lock)
        {
            _site = "menu_key";
            DispatchLocked(0, selector == 1 ? SfxTone.Tone09 : SfxTone.MenuClick, 0, 0);
        }
    }

    /// <summary>
    /// An AI's weapon firing, as a POSITIONAL sound.
    /// </summary>
    /// <param name="sourceId">The firing object's arena offset (<c>[0xED56]</c>).</param>
    /// <param name="position">Where it is.</param>
    /// <param name="selected">
    /// Whether it is the player's selected object — the original's own gate
    /// (<c>cmp bx,[0xBE]</c> @<c>image@0x29AA8</c>).
    /// </param>
    /// <param name="airToGround">The <c>[weapon+0x24] &amp; 0x10</c> arm (<c>image@0x29A7C</c>).</param>
    /// <param name="volume">The weapon record's <c>+0x2B</c> byte (<c>image@0x29A9E</c>).</param>
    /// <remarks>
    /// <para>
    /// The 1991 game plays an AI's gun ONLY when the shooter is the player's selected object, at the
    /// weapon record's own fixed volume and with no position at all
    /// (<c>sfx_selected_object_audio_dispatch @image@0x29AA8</c> → <c>image@0x29A79</c>).  With the
    /// positional pool on (<c>--sound-sources</c> &gt; 0) the port instead plays it on the shooter's OWN
    /// voice, attenuated and panned like its engine — an authorised deviation — and falls back to
    /// the original's gate exactly when the pool is off.
    /// </para>
    /// </remarks>
    public void ObjectWeaponFire(
        int sourceId, CombatPosition position, bool selected, bool airToGround, int volume)
    {
        lock (_lock)
        {
            if (_sources.MaxSources > 0)
            {
                SourceVoice? voice = _sources.Find(sourceId);
                if (voice is null)
                {
                    _sources.NoteDroppedFireSound();
                    return;
                }

                int range = ViewAnchorRange.Compute(position, _listener);
                if (airToGround)
                {
                    voice.PlayOneShot(SfxTone.AirToGroundFire, 0, 0, _sample, _census, "source_gun");
                }
                else
                {
                    voice.PlayOneShot(
                        SfxTone.Gun,
                        5,
                        PositionalAudio.AttenuateVolume(volume & 0xFF, range),
                        _sample,
                        _census,
                        "source_gun");
                }

                DriverCalls++;
                return;
            }

            if (selected)
            {
                _site = "weapon_fire_selected";
                if (airToGround)
                {
                    DispatchLocked(0, SfxTone.AirToGroundFire, 0, 0);
                }
                else
                {
                    DispatchLocked(0, SfxTone.Gun, 5, volume & 0xFF);
                }
            }
        }
    }

    // ---------------------------------------------------------------------- the per-frame step

    /// <summary>
    /// Runs <c>continuous_audio_state_update @image@0x29D2E</c> for one frame and records the
    /// listener position the positional impacts range against.
    /// </summary>
    /// <param name="inputs">This frame's state.</param>
    /// <param name="listener">
    /// The view anchor <c>s_view_anchor [0xD88E]</c> — the camera, which is what
    /// <c>object_range_from_view_anchor</c> measures from.
    /// </param>
    public void PerFrame(in ContinuousAudioInputs inputs, CombatPosition listener) =>
        PerFrame(
            inputs,
            new AudioListener { Position = listener, CockpitInterior = inputs.CockpitView },
            []);

    /// <summary>
    /// The per-frame step, with the positional sources the host published.
    /// </summary>
    /// <param name="inputs">This frame's state.</param>
    /// <param name="listener">The camera: where it is and which way its right vector points.</param>
    /// <param name="sources">Everything that could sound this frame, the player included.</param>
    public void PerFrame(
        in ContinuousAudioInputs inputs,
        in AudioListener listener,
        IReadOnlyList<AudioSourceState> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        lock (_lock)
        {
            _listener = listener.Position;
            _frameTimeAccum = inputs.FrameTimeAccum;
            _site = "continuous";
            _sources.Update(listener, sources, _sample, inputs.DriverType, _census);
            _continuous.Update(inputs, MuteMask, _engineDispatch, _cockpitDispatch);
        }
    }

    /// <summary>
    /// <c>audio_continuous_state_reset @image@0x298BD</c>, whole: park both channels, clear the
    /// mechanism deadline and silence every generator with the routine's own <c>cmd 8</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The original calls it when a SCENE starts — <c>mission_state_machine @image@0x008E6</c> at
    /// <c>image@0x00B05</c>, on the way into a flight.  The port's session reopen (a respawn, or the
    /// fate machine's restart) is that same moment, and NOT calling it was the "gear sound plays for
    /// ever after a restart" bug: the mechanism deadline
    /// <c>g_audio_time_threshold [0xBB3C:0xBB3E]</c> is an ABSOLUTE frame time, the new scene's
    /// <c>g_frame_time_accum</c> starts again at zero, and the whole of the next sortie is spent
    /// below a deadline the last one armed — which holds engine-channel tone <c>0x13</c> down
    /// (<c>image@0x29D5F..0x29D79</c>) for as long as the previous sortie lasted.
    /// </para>
    /// </remarks>
    public void ResetForNewScene()
    {
        lock (_lock)
        {
            _continuous.Reset();                                  // image@0x298C4..0x298CF
            _site = "scene_reset";
            DispatchLocked(8, 0, 0, 0);                           // image@0x298D2 — cmd 8
            _sources.Reset(_sample);
        }
    }

    /// <summary>Notes a session reopen in the census, without making a sound.</summary>
    /// <param name="note">What happened, for the log's site column.</param>
    /// <remarks>
    /// A marker only — command <c>0xFF</c> is not a driver command, so it reaches no generator and
    /// no histogram; it exists so a tone log can be cut at the restart.
    /// </remarks>
    public void MarkCensus(string note)
    {
        lock (_lock)
        {
            _census?.Note(new PortDriverCall(_sample, 0x00FF, 0, 0, 0, note));
        }
    }

    private void DispatchToPlayerVoice(int command, int toneId, int pitch, int volume)
    {
        if ((MuteMask & (byte)AudioMuteFlags.Master) == 0 || !DriverLoaded)
        {
            GuardedCalls++;
            return;
        }

        _sources.PlayerVoice.Dispatch(
            command, toneId, pitch, volume, _sample, _census, "player_engine");
        DriverCalls++;
    }

    // ---------------------------------------------------------------------------- rendering

    /// <inheritdoc/>
    double IAudioSource.RenderSample(long sample) => _engine.RenderSample(sample);

    /// <summary>
    /// Fills <paramref name="destination"/> with interleaved stereo float frames, issuing the
    /// driver's 256 Hz tick as the cursor passes it.
    /// </summary>
    /// <param name="destination">At least <c>frames × 2</c> floats.</param>
    /// <param name="frames">How many frames to render.</param>
    public void Render(Span<float> destination, int frames)
    {
        lock (_lock)
        {
            int done = 0;
            while (done < frames)
            {
                // Render up to the next driver tick, then take it.  A block never straddles a tick,
                // so the generator's state changes land on the exact sample they belong to.
                int untilTick = (int)Math.Max(1, Math.Ceiling(_nextTick - _sample));
                int chunk = Math.Min(frames - done, untilTick);
                double peak = _mixer.Render(destination[(done * 2)..], chunk, _sample);
                if (peak > PeakAmplitude)
                {
                    PeakAmplitude = peak;
                }

                _sample += chunk;
                done += chunk;

                if (_sample >= _nextTick)
                {
                    _site = "tick";
                    DispatchLocked(6, 0, 0, 0);

                    // Every positional voice runs the same 256 Hz sequencer tick.  They are not
                    // censused: one line per voice per tick would be the whole log.
                    _sources.Tick(_sample);
                    Ticks++;
                    _nextTick += AudioFormat.SamplesPerTick;
                }
            }
        }
    }

    /// <summary>Renders <paramref name="frames"/> frames and returns them as signed 16-bit PCM.</summary>
    /// <param name="frames">How many frames.</param>
    public short[] RenderInt16(int frames)
    {
        float[] floats = new float[frames * AudioFormat.Channels];
        Render(floats, frames);
        short[] pcm = new short[floats.Length];
        for (int i = 0; i < floats.Length; i++)
        {
            pcm[i] = (short)Math.Clamp(
                (int)(floats[i] * AudioFormat.Int16Scale), short.MinValue, short.MaxValue);
        }

        return pcm;
    }
}
