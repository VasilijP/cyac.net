using System.Globalization;
using CYAC.Port.Audio;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Sim;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Session;
using CYAC.Port.Host.Sim;
using CYAC.Port.Render;

namespace CYAC.Port.Host.Sound;

/// <summary>
/// The host's sound: it owns the <see cref="AudioRuntime"/>, wires it to the session's effect
/// seams, feeds the two continuous channels once a frame, and either pushes the mixer to a device
/// or accumulates it into a WAV.
/// </summary>
/// <remarks>
/// <para>
/// This is the only class in the port that knows both the simulation and an audio device.  Nothing
/// it does can reach simulation state: it READS a snapshot each frame and it hands the kernels an
/// <see cref="IAudioEvents"/> whose every method is one-way.  <c>--replay</c> never builds one
/// (<see cref="TraceReplay"/> does not use <see cref="FlightSession"/> at all), so the verified
/// replays are untouched by construction.
/// </para>
/// </remarks>
public sealed class HostAudio : IDisposable
{
    private readonly AudioRuntime _runtime;
    private readonly DriverCallCensus _census;
    private readonly IAudioOutput _output;
    private readonly List<float> _wav = [];
    private readonly List<AudioSourceState> _sources = [];
    private readonly Dictionary<ushort, bool> _jetByClass = [];
    private readonly string? _wavPath;
    private readonly int _wavFrameCap;
    private long _frameTime;
    private double _wavCarry;
    private bool _finished;
    private int _fireTonesSeen;
    private int _countermeasuresSeen;

    private HostAudio(
        AudioRuntime runtime,
        IAudioOutput output,
        string? wavPath,
        DriverCallCensus census,
        double wavSeconds,
        bool resetOnReopen)
    {
        _runtime = runtime;
        _output = output;
        _wavPath = wavPath;
        _census = census;
        _wavFrameCap = (int)Math.Max(1, wavSeconds * AudioFormat.SampleRate);
        ResetOnReopen = resetOnReopen;
        BuildEngineSourceClasses();
    }

    /// <summary>The sound path itself.</summary>
    public AudioRuntime Runtime => _runtime;

    /// <summary>The driver-call census this run built.</summary>
    public DriverCallCensus Census => _census;

    /// <summary>How many seconds of audio have been rendered.</summary>
    public double RenderedSeconds => _runtime.SampleCursor / (double)AudioFormat.SampleRate;

    /// <summary>Whether frames are being accumulated for a WAV file.</summary>
    public bool WritesWav => _wavPath is not null;

    /// <summary>
    /// Whether a session REOPEN runs <c>audio_continuous_state_reset @image@0x298BD</c>.
    /// </summary>
    /// <remarks>
    /// On (the default) is the correct behaviour and the fix for the restart-loop bug; off is the
    /// H11 behaviour, kept ONLY so the bug can be reproduced and measured from the same binary
    /// (<c>--audio-reset off</c>, H13 §2).
    /// </remarks>
    public bool ResetOnReopen { get; }

    /// <summary>The total time the per-frame audio step has cost, in milliseconds.</summary>
    public double PumpMilliseconds { get; private set; }

    /// <summary>How many frames the per-frame audio step has run.</summary>
    public long PumpFrames { get; private set; }

    /// <summary>
    /// Builds the sound path, or returns null when it is switched off or the driver is not in the
    /// data tree.
    /// </summary>
    /// <param name="tree">The transformed data tree (it carries <c>adldrive.code.bin</c>).</param>
    /// <param name="enabled">The <c>--sound</c> switch.</param>
    /// <param name="volume">The <c>--sound-volume</c> master, 0..1.</param>
    /// <param name="mask">The <c>--sound-mask</c> value for <c>g_audio_mute_mask [0xE483]</c>.</param>
    /// <param name="wavPath">Where to write a WAV, or null for a live device.</param>
    /// <param name="silent">Build the path but open no device (a headless run with no WAV).</param>
    /// <param name="wavSeconds">How much audio a WAV run keeps.</param>
    /// <param name="seed">The session seed the <c>Audio.Jitter</c> stream derives from.</param>
    /// <param name="diagnostics">Where to print what happened.</param>
    /// <param name="enginePolicy">which views the player's engine is heard in.</param>
    /// <param name="maxSources">how many positional sources may sound (<c>--sound-sources</c>).</param>
    /// <param name="audibleRadiusFeet">the positional radius, in feet.</param>
    /// <param name="outsideLowPassHz">the outside-listening low-pass cutoff, 0 = off.</param>
    /// <param name="resetOnReopen">reset the sound path when a session reopens.</param>
    /// <param name="outputKind">which live output device to open (<c>--audio-output</c>).</param>
    public static HostAudio? TryCreate(
        DataTree tree,
        bool enabled,
        double volume,
        byte mask,
        string? wavPath,
        bool silent,
        double wavSeconds,
        ulong seed,
        TextWriter diagnostics,
        EngineViewPolicy enginePolicy = EngineViewPolicy.All,
        int maxSources = SourceVoicePool.DefaultMaxSources,
        int audibleRadiusFeet = PositionalAudio.AudibleRadiusFeet,
        double outsideLowPassHz = SourceVoicePool.DefaultOutsideLowPassHz,
        bool resetOnReopen = true,
        AudioOutputKind outputKind = AudioOutputKind.Auto)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!enabled)
        {
            return null;
        }

        AdlDriverImage driver;
        try
        {
            driver = AdlDriverImage.FromDataTree(tree.Root);
        }
        catch (FileNotFoundException error)
        {
            diagnostics.WriteLine($"warning: sound is off — {error.Message}");
            return null;
        }
        catch (InvalidDataException error)
        {
            diagnostics.WriteLine($"warning: sound is off — {error.Message}");
            return null;
        }

        DriverCallCensus census = new DriverCallCensus();

        // The shipped game draws them from the single LFSR).
        RandomStreams streams = RandomStreams.FromMasterSeed(seed);
        AudioRuntime runtime = new AudioRuntime(driver, streams.AudioJitter, census)
        {
            MuteMask = mask,
        };
        runtime.Mixer.MasterVolume = Math.Clamp(volume, 0, 1);
        runtime.Continuous.ViewPolicy = enginePolicy;
        runtime.Sources.MaxSources = Math.Max(0, maxSources);
        runtime.Sources.AudibleRadiusFeet = Math.Max(0, audibleRadiusFeet);
        runtime.Sources.OutsideLowPassHz = Math.Max(0, outsideLowPassHz);

        // The live device.  --wav and a silent headless run still short-circuit it; every other
        // run goes through AudioOutputSelector, which is where the per-OS order lives.
        IAudioOutput output = wavPath is not null
            ? new NullAudioOutput("--wav renders the sortie to a file instead")
            : silent
            ? new NullAudioOutput("--headless without --wav opens no device")
            : AudioOutputSelector.Open(outputKind, OperatingSystem.IsMacOS(), kind => OpenDevice(kind, runtime));

        diagnostics.WriteLine(
            $"sound: adldrive.drv ({driver.Length:N0} B, drv-space) → "
                + $"mask 0x{mask:X2}, volume {runtime.Mixer.MasterVolume:0.00}, "
                + $"output {(wavPath is null ? output.Name : "WAV " + wavPath)}"
                + $" (--audio-output {AudioOutputKinds.Name(outputKind)}"
                + $", {(output.IsRunning ? "running" : "not running")})");
        diagnostics.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"sound: engine views {(enginePolicy == EngineViewPolicy.All ? "ALL (deviation)" : "original")}"
                + $", {runtime.Sources.MaxSources} positional source(s) within "
                + $"{runtime.Sources.AudibleRadiusFeet:N0} ft, outside low-pass "
                + $"{(runtime.Sources.OutsideLowPassHz <= 0 ? "off" : $"{runtime.Sources.OutsideLowPassHz:N0} Hz")}"
                + $", reset-on-reopen {(resetOnReopen ? "on" : "OFF (H11 bug repro)")}"));
        return new HostAudio(runtime, output, wavPath, census, wavSeconds, resetOnReopen);
    }

    /// <summary>
    /// Opens one device kind, or says why it refused.  The only place in the host that knows which
    /// class each <see cref="AudioOutputKind"/> stands for.
    /// </summary>
    /// <param name="kind">The device to try.</param>
    /// <param name="runtime">The sound path it would pull from.</param>
    private static (IAudioOutput? Output, string Reason) OpenDevice(
        AudioOutputKind kind, AudioRuntime runtime) => kind switch
        {
            AudioOutputKind.Mac => (
                MacAudioQueueOutput.TryStart(runtime),
                OperatingSystem.IsMacOS()
                    ? "AudioToolbox refused to open an output queue"
                    : "AudioQueue is macOS-only"),
            AudioOutputKind.Sdl => SdlAudioOutput.TryStart(runtime),
            _ => (null, "no device"),
        };

    /// <summary>Hands the session's effect seams to the sound path.</summary>
    /// <param name="session">The flight session.</param>
    public void Attach(FlightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Mission is { } mission)
        {
            mission.VmEffects.Audio = _runtime;
        }
    }

    /// <summary>
    /// The per-frame step: run <c>continuous_audio_state_update</c> and, on the WAV path, render the
    /// frame's worth of samples.
    /// </summary>
    /// <param name="session">The live session.</param>
    /// <param name="view">The view the player is in — <c>g_current_view_mode [0xC320]</c>.</param>
    /// <param name="listenerWorldX">The camera's WORLD X (the view anchor <c>[0xD88E]</c>).</param>
    /// <param name="listenerWorldY">The camera's world Y.</param>
    /// <param name="listenerWorldZ">The camera's world Z.</param>
    /// <param name="elapsedSeconds">Wall seconds this frame stands for.</param>
    /// <remarks>
    /// UNITS.  The renderer works in WORLD units and the arena — which is what
    /// <c>object_range_from_view_anchor</c> and every <see cref="CombatPosition"/> speak — works in
    /// POSITION units, 256 to the world unit (<c>RenderListBuilder</c>'s own <c>&gt;&gt; 8</c>).  The
    /// camera is therefore scaled UP on the way in, or every impact would range 256× too close and
    /// the <c>0x2710</c> gate would let through sounds a quarter of a mile away.
    /// </remarks>
    public void PerFrame(
        FlightSession session,
        ViewMode view,
        double listenerWorldX,
        double listenerWorldY,
        double listenerWorldZ,
        double elapsedSeconds) =>
        PerFrame(
            session,
            view,
            new CameraPose(listenerWorldX, listenerWorldY, listenerWorldZ, 0, 0, 0),
            elapsedSeconds);

    /// <summary>
    /// The per-frame step, with the whole camera POSE: the sound path needs the listener's right
    /// vector to pan, not just where the listener is.
    /// </summary>
    /// <param name="session">The live session.</param>
    /// <param name="view">The view the player is in — <c>g_current_view_mode [0xC320]</c>.</param>
    /// <param name="camera">The camera this frame was drawn from.</param>
    /// <param name="elapsedSeconds">Wall seconds this frame stands for.</param>
    public void PerFrame(
        FlightSession session,
        ViewMode view,
        in CameraPose camera,
        double elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(session);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();

        int listenerX = PositionUnits(camera.EyeX);
        int listenerY = PositionUnits(camera.EyeY);
        int listenerZ = PositionUnits(camera.EyeZ);
        FlightSnapshot snapshot = session.Snapshot();
        _frameTime = FrameTimeOf(session);

        ContinuousAudioInputs inputs = new ContinuousAudioInputs
        {
            FrameTimeAccum = _frameTime,
            CockpitView = FlightRasterizer.IsCockpitInterior(view),
            ViewMode = (int)view,
            ObjectActive = snapshot.Active,
            InFlightGate = snapshot.Active,
            JetEngine = IsJet(session.AircraftBasename),
            Afterburner = (snapshot.StatusFlags & (byte)AircraftStatusFlags.Afterburner) != 0,
            EngineDamaged = false,
            AirspeedFeetPerSecond = snapshot.AirspeedFps,
            ThrottlePercent = snapshot.ThrottlePercent,
            LoadFactorQ8 = session.State.Aircraft.GLoadQ8,
            RangeToSource = RangeToPlayer(snapshot, listenerX, listenerY, listenerZ),
            WarningsEnabled = true,
            PlayerNotFlying = session.Fate is { Enabled: true, Passive: false, Ended: true },
            DriverType = 2,                                   // the port renders adldrive.drv
            WeaponNameFirstByte = -1,                         // (open) — the lock tones, H11 §4
        };

        // Everything that makes an engine noise this frame: the player always, and every live
        // pool object whose class the shipped registry marks as an aeroplane.
        Vec3 right = camera.Right;
        AudioListener listener = new AudioListener
        {
            Position = new CombatPosition(listenerX, listenerY, listenerZ),
            RightX = right.X,
            RightY = right.Y,
            RightZ = right.Z,
            CockpitInterior = FlightRasterizer.IsCockpitInterior(view),
        };

        CollectSources(session, snapshot, inputs);
        _runtime.PerFrame(inputs, listener, _sources);

        if (session.Mission is { } mission)
        {
            DrainPlayerSounds(mission);
        }

        if (_wavPath is not null && elapsedSeconds > 0)
        {
            RenderIntoWav(elapsedSeconds);
        }

        PumpMilliseconds +=
            (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        PumpFrames++;
    }

    /// <summary>
    /// The sound path's PAUSE, for the frames the in-flight ESC menu freezes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The original pauses audio the moment the menu opens: the ESC branch's second call is
    /// <c>lcall 0x3981:0xad</c> = <c>image@0x299BD</c>, the audio prep, before the modal runs.
    /// The port's equivalent is simply not to step the runtime —
    /// <see cref="PerFrame(FlightSession, ViewMode, in CameraPose, double)"/> is what advances the
    /// continuous channels and renders the WAV, and both are measured in a frame-time accumulator
    /// the frozen session no longer moves.
    /// </para>
    /// <para>
    /// It is deliberately NOT a reset: the engine note, the warnings and the mechanism deadlines
    /// must all be exactly where they were when the player closes the menu again, which is what a
    /// pause means and a reset would not give.
    /// </para>
    /// </remarks>
    public void Pause() => PausedFrames++;

    /// <summary>How many presented frames the sound path sat out.</summary>
    public long PausedFrames { get; private set; }

    /// <summary>
    /// The three <c>audio_threshold_bump_*</c> trampolines, from the cockpit-key toggles that call
    /// them (<c>image@0x2A4B2</c> airbrake, <c>image@0x2A4C9</c> flaps, <c>image@0x2A4EE</c> gear).
    /// </summary>
    /// <remarks>
    /// Each of the three original sites sits immediately after the matching
    /// <c>xor [master+0x124], bit</c> — brake bit 3 (<c>image@0x2A49F</c>), flap bit 1
    /// (<c>image@0x2A4C4</c>), gear bit 2 (<c>image@0x2A4E9</c>) — so the port fires them off the
    /// same status-flag EDGE and inherits every one of the original's own gates for free.
    /// </remarks>
    /// <param name="before">The aircraft status byte before the key.</param>
    /// <param name="after">…and after it.</param>
    public void NoteCockpitToggle(byte before, byte after)
    {
        int changed = before ^ after;
        if ((changed & (byte)AircraftStatusFlags.Flaps) != 0)
        {
            _runtime.MechanismSound(0x20);                    // image@0x29B3D
        }

        if ((changed & (byte)AircraftStatusFlags.LandingGear) != 0)
        {
            _runtime.MechanismSound(0x100);                   // image@0x29B74
        }

        if ((changed & (byte)AircraftStatusFlags.Airbrake) != 0)
        {
            _runtime.MechanismSound(0x10);                    // image@0x29B52
        }
    }

    /// <summary>
    /// A session REOPEN (a respawn, or the fate machine's restart).
    /// </summary>
    /// <param name="session">The freshly opened session.</param>
    /// <remarks>
    /// <para>
    /// Two things happen here, and an earlier pass did neither.  The new session's mission gets the sound seam
    /// again — without this an AI's gun, every impact and the whole kill sequence are silent for
    /// the rest of the run, because <see cref="Attach"/> wired the OLD <c>MissionSession</c>.  And
    /// the sound path runs the original's own scene reset
    /// (<c>audio_continuous_state_reset @image@0x298BD</c>, which
    /// <c>mission_state_machine @image@0x008E6</c> calls at <c>image@0x00B05</c> on the way into a
    /// flight): both channel sentinels back to 0xFFFF, the mechanism deadline
    /// <c>[0xBB3C:0xBB3E]</c> back to 0 and <c>cmd 8</c> to silence the generators.
    /// </para>
    /// <para>
    /// Leaving the deadline behind is what made the gear sound loop for ever: it is an ABSOLUTE
    /// frame time, the new scene's <c>g_frame_time_accum</c> restarts at zero, and the engine
    /// channel is then held on tone <c>0x13</c> (<c>image@0x29D5F..0x29D79</c>) for as many
    /// frame-time units as the previous sortie had accumulated.
    /// </para>
    /// </remarks>
    public void NoteSessionReopen(FlightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _runtime.MarkCensus("session_reopen");
        _fireTonesSeen = 0;
        _countermeasuresSeen = 0;
        if (ResetOnReopen)
        {
            _runtime.ResetForNewScene();
        }

        Attach(session);
    }

    /// <summary>
    /// The sortie is OVER and there is no next scene yet: the quiet half of
    /// <see cref="NoteSessionReopen"/>.
    /// </summary>
    /// <remarks>
    /// Same reason as the reopen: the continuous channels and the mechanism deadlines are
    /// stamped in the session's own <c>g_frame_time_accum</c>, and the next sortie's restarts at
    /// zero — leaving them behind is what made the gear sound loop for ever.  The difference is that
    /// nothing is re-wired afterwards, because the player is in the menu and the menu makes no sound
    /// (the front end's own sound is a 0.2 item).
    /// </remarks>
    public void NoteSessionEnded()
    {
        _runtime.MarkCensus("session_ended");
        _fireTonesSeen = 0;
        _countermeasuresSeen = 0;
        if (ResetOnReopen)
        {
            _runtime.ResetForNewScene();
        }
    }

    /// <summary>Renders any remaining audio and writes the WAV.</summary>
    public void Finish()
    {
        if (_finished || _wavPath is null || _wav.Count == 0)
        {
            return;
        }

        _finished = true;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_wavPath))!);
        WavWriter.Write(_wavPath, WavWriter.ToInt16(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_wav)));
    }

    /// <summary>The lines the run's summary prints.</summary>
    public IEnumerable<string> ReportLines()
    {
        yield return _output.Report();
        yield return string.Create(
            CultureInfo.InvariantCulture,
            $"[audio] {_runtime.DriverCalls:N0} driver call(s) ({_runtime.GuardedCalls:N0} guarded), "
                + $"{_runtime.Ticks:N0} tick(s), {RenderedSeconds:F1} s rendered, "
                + $"peak |x| {_runtime.PeakAmplitude:F3}");
        yield return string.Create(
            CultureInfo.InvariantCulture,
            $"[audio] continuous: engine tone {ToneText(_runtime.Continuous.EngineTone)} "
                + $"pitch {_runtime.Continuous.EnginePitch} volume {_runtime.Continuous.EngineVolumeValue}; "
                + $"cockpit tone {ToneText(_runtime.Continuous.CockpitTone)}; "
                + $"{_runtime.Continuous.Starts:N0} start(s), {_runtime.Continuous.Updates:N0} update(s), "
                + $"{_runtime.Continuous.Stops:N0} stop(s)");
        yield return $"[audio] tone starts: {(_runtime.Census?.HistogramLine() ?? "(no census)")}";
        SourceVoicePool sources = _runtime.Sources;
        yield return string.Create(
            CultureInfo.InvariantCulture,
            $"[audio] sources: cap {sources.MaxSources}, {sources.Voices.Count} live, "
                + $"{sources.Allocations:N0} allocated ({sources.Reuses:N0} re-used), "
                + $"{sources.Evictions:N0} evicted, "
                + $"nearest {(sources.NearestRangeFeet < 0 ? "(none)" : $"{sources.NearestRangeFeet:N0} ft")}, "
                + $"{sources.DroppedFireSounds:N0} fire sound(s) with no voice; "
                + $"mean {(sources.Frames == 0 ? 0 : (double)sources.VoiceFrames / sources.Frames):F2} "
                + $"live source(s)/frame; player voice at {sources.PlayerVoice.RangeFeet:N0} ft, "
                + $"pan {sources.PlayerVoice.PanPosition:+0.00;-0.00;0.00}");
        if (PumpFrames > 0)
        {
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"[audio] per-frame cost {PumpMilliseconds / PumpFrames:F3} ms mean over "
                    + $"{PumpFrames:N0} frame(s)");
        }

        if (_wavPath is not null)
        {
            yield return $"[audio] WAV → {_wavPath}";
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Finish();
        _output.Dispose();
    }

    /// <summary>
    /// The PLAYER's own two sounds, drained from the ported seams' own records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>weapon_fire_check_and_spawn @image@0x03432</c> ends with
    /// <c>mov bx,[0xED1E] / lcall sfx_weapon_type_tone_dispatch</c> (<c>image@0x03506</c>), and that
    /// routine splits on the weapon class record's own <c>+0x24</c> bit <c>0x10</c>: set ⇒ tone
    /// <c>0x17</c>, clear ⇒ <c>tone 0x0C</c> with <c>pitch 5</c> and the record's <c>+0x2B</c> byte
    /// as the volume — the GUN BURST, and 70 of the 166 tone starts in a recorded session.
    /// </para>
    /// <para>
    /// <c>chaff_fire @image@0x0AF68</c> and its flare sibling call
    /// <c>sfx_countermeasure_deploy_sound</c> (<c>image@0x0AFC5</c> / <c>image@0x0B029</c>), which
    /// pushes tone <c>0x20</c>.
    /// </para>
    /// </remarks>
    private void DrainPlayerSounds(MissionSession mission)
    {
        SessionPlayerCombatEvents events = mission.PlayerEvents;
        ICombatStaticData data = mission.Context.StaticData;
        List<ushort> tones = events.FireTones;
        for (int i = _fireTonesSeen; i < tones.Count; i++)
        {
            ushort weaponClass = tones[i];
            bool guided = (data.Byte(weaponClass + 0x24) & 0x10) != 0;   // image@0x29A7C
            _runtime.WeaponFire(guided, data.Byte(weaponClass + 0x2B));  // image@0x29A90
        }

        _fireTonesSeen = tones.Count;

        for (; _countermeasuresSeen < events.CountermeasureSounds; _countermeasuresSeen++)
        {
            _runtime.PlayTone(SfxTone.CountermeasureDeploy);
        }
    }

    /// <summary>
    /// Builds this frame's source list: the player, then every live pool object of an AEROPLANE
    /// class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which classes have engines.</b>  The original never asks — it sounds one object and takes
    /// its class record from that object.  The port needs a predicate, and the shipped registry
    /// supplies one without inventing anything: <see cref="MeshClassFlags.FixedLodScale"/> (bit 3)
    /// is set on exactly the eight aeroplanes (<c>p51</c>, <c>fw190</c>, <c>me109</c>, <c>b17</c> =
    /// <c>0x0C</c>; <c>f4</c>, <c>f86</c>, <c>mig15</c>, <c>mig21</c> = <c>0x2C</c>) and on none of
    /// the other fifteen classes (<c>data/exe/classes.json</c>) — an 8/8, 0/15 census a test pins.
    /// Bit 5 of the same byte then picks jet or piston, which IS the original's own reader
    /// (<c>test byte [bx+1],0x20</c> @<c>image@0x29E07</c>).
    /// </para>
    /// </remarks>
    private void CollectSources(
        FlightSession session, in FlightSnapshot snapshot, in ContinuousAudioInputs inputs)
    {
        _sources.Clear();
        _sources.Add(new AudioSourceState
        {
            Id = SourceVoicePool.PlayerVoiceId,
            Position = new CombatPosition(snapshot.X, snapshot.Y, snapshot.Z),
            IsPlayer = true,
            JetEngine = inputs.JetEngine,
            Afterburner = inputs.Afterburner,
            EngineDamaged = inputs.EngineDamaged,
            AirspeedFeetPerSecond = inputs.AirspeedFeetPerSecond,
            ThrottlePercent = inputs.ThrottlePercent,
        });

        if (_runtime.Sources.MaxSources <= 0 || session.Mission is not { } mission)
        {
            return;
        }

        foreach (CombatSceneObject live in CombatSceneObjects.Live(mission.Combat.Registers, mission.Combat.Arena))
        {
            if (live.IsPlayer || !_jetByClass.TryGetValue(live.ClassRecordRef, out bool jet))
            {
                continue;
            }

            _sources.Add(new AudioSourceState
            {
                Id = live.ObjectRef,
                Position = new CombatPosition(
                    PositionUnits(live.X), PositionUnits(live.Y), PositionUnits(live.Z)),
                JetEngine = jet,
                AirspeedFeetPerSecond = live.AirspeedFps,

                // (open) — an AI has no throttle register and no afterburner or damage bit of its
                // own; its note follows its speed alone.
                ThrottlePercent = 0,
            });
        }
    }

    /// <summary>Maps every AEROPLANE class record to its jet/piston bit, once.</summary>
    private void BuildEngineSourceClasses()
    {
        if (!ClassRegistry.IsLoaded)
        {
            return;
        }

        foreach (ClassRecord record in ClassRegistry.Everything)
        {
            if (record.Flags.HasFlag(MeshClassFlags.FixedLodScale))
            {
                _jetByClass[(ushort)record.DgroupOffset] =
                    record.Flags.HasFlag(MeshClassFlags.JetEngineSound);   // image@0x29E07 — the JET bit
            }
        }
    }

    /// <summary>World units → the arena's position units (256 per world unit).</summary>
    private static int PositionUnits(double world) =>
        (int)Math.Clamp(world * 256.0, int.MinValue, int.MaxValue);

    private static string ToneText(int tone) =>
        tone < 0 ? "none" : string.Create(CultureInfo.InvariantCulture, $"0x{tone:X2}");

    /// <summary>
    /// <c>g_frame_time_accum [0xF0D2:0xF0D4]</c>, from the mission's own register file when there is
    /// one.
    /// </summary>
    private long FrameTimeOf(FlightSession session) =>
        session.Mission is { } mission
            ? unchecked((uint)(mission.Combat.Registers.Word(SceneFrameTimer.FrameTime)
                | (mission.Combat.Registers.Word(SceneFrameTimer.FrameTime + 2) << 16)))
            : _frameTime + 1;

    /// <summary>Whether the aircraft's class record carries the JET bit.</summary>
    /// <remarks>
    /// <c>test byte [bx+1],0x20</c> @<c>image@0x29E07</c> reads the class record's <c>flags_b_u8</c>
    /// bit 5 to pick the tone family, and the shipped registry sets it on exactly the four jets
    /// (<c>f4</c>, <c>f86</c>, <c>mig15</c>, <c>mig21</c> = <c>0x2C</c>) and on none of the pistons
    /// (<c>0x0C</c>).  <see cref="MeshClassFlags.JetEngineSound"/> is that bit, renamed from
    /// <c>Unknown5</c> now that its reader is byte-verified.
    /// </remarks>
    private static bool IsJet(string basename)
    {
        if (!ClassRegistry.IsLoaded)
        {
            return false;
        }

        try
        {
            return ClassRegistry.Get(basename).Flags.HasFlag(MeshClassFlags.JetEngineSound);
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>object_range_from_view_anchor</c> from the camera to the player, for the fly-by view's
    /// attenuation.
    /// </summary>
    private static int RangeToPlayer(in FlightSnapshot snapshot, int x, int y, int z) =>
        CYAC.Port.Core.Sim.Combat.Geometry.ViewAnchorRange.Compute(
            new CombatPosition(snapshot.X, snapshot.Y, snapshot.Z), new CombatPosition(x, y, z));

    private void RenderIntoWav(double seconds)
    {
        if (_wav.Count >= _wavFrameCap * AudioFormat.Channels)
        {
            return;                       // --wav-seconds reached; the sortie runs on in silence
        }

        double want = (seconds * AudioFormat.SampleRate) + _wavCarry;
        int frames = (int)want;
        _wavCarry = want - frames;
        if (frames <= 0)
        {
            return;
        }

        float[] block = new float[frames * AudioFormat.Channels];
        _runtime.Render(block, frames);
        _wav.AddRange(block);
    }
}
