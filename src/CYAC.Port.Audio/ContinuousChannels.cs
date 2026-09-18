using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Audio;

/// <summary>
/// Which views the player's own engine is heard in.
/// </summary>
/// <remarks>
/// <para>
/// The 1991 rule, <see cref="Original"/>, is at <c>image@0x29D86</c> and <c>image@0x29E4A</c>: the
/// engine channel is fed from the sounding OBJECT only when the cockpit-view flag (<c>[0xF12A] &amp;
/// 0x40</c>) is up, or in the external-target view during an engagement; in every other view, with
/// view-flag bit 7 clear, only the CIRCLING view (<c>[0xC320] == 0x0F</c>) makes a sound at all, and
/// that one is a FIXED tone 1 at a fixed pitch 0x12C (<c>image@0x29F39</c>).  That is why a chase
/// view falls silent.
/// </para>
/// <para>
/// <see cref="All"/> is the port's default and a labelled DEVIATION: the player's own engine
/// keeps sounding in every view, with the ORIGINAL's own tone family, pitch law and volume law —
/// including the fly-by view's distance attenuation, which is applied in all views instead of only
/// in view 0x0D, and the "watching from elsewhere" volume base the original uses for the target
/// views.  Nothing else about the channel changes: the mechanism sound, the mute bit and the
/// idle-send protocol are untouched.
/// </para>
/// </remarks>
public enum EngineViewPolicy
{
    /// <summary>The 1991 rule: cockpit / engaged-target views, plus the fixed circling tone.</summary>
    Original,

    /// <summary>The port's: the engine is heard in every view, attenuated by distance.</summary>
    All,
}

/// <summary>
/// <c>continuous_audio_state_update @image@0x29D2E</c> — the two LOOPING sound channels, stepped once per frame.
/// </summary>
/// <remarks>
/// <para>
/// Channel A is the ENGINE (the mute bit that gates the whole of section A is bit 1, which the
/// System menu labels "Engine sounds" — <c>test byte [0xe483],2</c> @<c>image@0x29D7C</c>; the
/// scanner's <c>g_weapon_sound_*</c> names for its three state words are a misnomer, H11 §3).
/// Channel B is the cockpit ambient: the stall cues and the RWR / lock tones.
/// </para>
/// <para>
/// Both channels run the same IDLE-SEND protocol, which this class reproduces exactly
/// (<c>image@0x29F4A..0x29FB9</c> and <c>image@0x29FE4..0x2A10B</c>):
/// </para>
/// <list type="number">
///   <item><description>the tone changed and the old one was valid ⇒ <c>cmd 2</c> (stop) the old;</description></item>
///   <item><description>the tone changed and the new one is valid ⇒ <c>cmd 0</c> (start) it;</description></item>
///   <item><description>
///     the tone is unchanged but a parameter moved ⇒ engine channel <c>cmd 4</c> (update), cockpit
///     channel <c>cmd 2</c> then <c>cmd 0</c> (the cockpit tones are not cmd-04-updatable — only
///     0x00-0x04 and 0x11 are).
///   </description></item>
/// </list>
/// <para>
/// <b>One deliberate deviation.</b>  The original never initialises the cockpit channel's two
/// parameter locals (<c>[bp-0x0C]</c>, <c>[bp-0x1A]</c>) on the paths that select tones 0x1E, 0x1F,
/// 0x0E, 0x0F, 0x10 or 0x21 — they carry whatever was on the stack.  It is harmless for the SOUND
/// (only tone 0x11 consumes pitch/volume, and it always sets both), but the change detector compares
/// them, so the shipped game can stop-and-restart a stall tone on a frame where nothing changed.
/// The port passes 0, which makes those tones steady.
/// </para>
/// </remarks>
public sealed class ContinuousChannels
{
    /// <summary>"No tone" — the sentinel both channels park on (<c>mov ax,0xffff</c> @<c>image@0x29D45</c>).</summary>
    public const int NoTone = -1;

    /// <summary>The engine channel's mechanism tone (gear, flaps, airbrake).</summary>
    /// <remarks>
    /// Forced while <c>g_frame_time_accum &lt; g_audio_time_threshold [0xBB3C:0xBB3E]</c>
    /// (<c>image@0x29D5F..0x29D79</c>), which the three
    /// <c>audio_threshold_bump_*</c> trampolines set to "now + delta".
    /// </remarks>
    public const int MechanismTone = 0x13;

    private long _threshold;

    /// <summary>Which views the engine is heard in; <see cref="EngineViewPolicy.All"/> by default.</summary>
    public EngineViewPolicy ViewPolicy { get; set; } = EngineViewPolicy.All;

    /// <summary><c>g_weapon_sound_id_current [0xBB2E]</c> — the engine channel's live tone.</summary>
    public int EngineTone { get; private set; } = NoTone;

    /// <summary><c>g_weapon_sound_speed_param [0xBB2C]</c> — its pitch argument.</summary>
    public int EnginePitch { get; private set; }

    /// <summary><c>g_weapon_sound_note [0xBB3A]</c> — its VOLUME argument.</summary>
    public int EngineVolumeValue { get; private set; }

    /// <summary><c>g_cockpit_sound_id_current [0xBB34]</c> — the cockpit channel's live tone.</summary>
    public int CockpitTone { get; private set; } = NoTone;

    /// <summary><c>g_cockpit_sound_param1 [0xBB30]</c>.</summary>
    public int CockpitParam1 { get; private set; }

    /// <summary><c>g_cockpit_sound_param2 [0xBB32]</c>.</summary>
    public int CockpitParam2 { get; private set; }

    /// <summary>
    /// <c>g_audio_time_threshold [0xBB3C:0xBB3E]</c> — the deadline the mechanism tone runs to.
    /// </summary>
    public long MechanismDeadline => _threshold;

    /// <summary>How many <c>cmd 0</c> starts this channel pair has issued.</summary>
    public long Starts { get; private set; }

    /// <summary>How many <c>cmd 4</c> updates.</summary>
    public long Updates { get; private set; }

    /// <summary>How many <c>cmd 2</c> stops.</summary>
    public long Stops { get; private set; }

    /// <summary>
    /// <c>audio_threshold_bump_flap / _airbrake / _gear</c> — run the mechanism tone until
    /// <paramref name="frameTimeAccum"/> + <paramref name="delta"/>.
    /// </summary>
    /// <param name="frameTimeAccum">The frame-time clock now.</param>
    /// <param name="delta">0x20 flaps, 0x10 airbrake, 0x100 gear.</param>
    public void BumpMechanismDeadline(long frameTimeAccum, int delta) =>
        _threshold = frameTimeAccum + delta;

    /// <summary>
    /// <c>audio_continuous_state_reset @image@0x298BD</c> — park both channels and clear the
    /// mechanism deadline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The routine's whole write set, read off the bytes:
    /// </para>
    /// <code>
    /// 298C1  mov ax,0xffff
    /// 298C4  mov [0xbb2e],ax        ; the ENGINE channel's tone  → the 0xFFFF sentinel
    /// 298C7  mov [0xbb34],ax        ; the COCKPIT channel's tone → the same
    /// 298CA  sub ax,ax
    /// 298CC  mov [0xbb3e],ax        ; g_audio_time_threshold, high word → 0
    /// 298CF  mov [0xbb3c],ax        ; …low word → 0
    /// 298D2  push 8 / call 0x29a17  ; and cmd 8 — silence every generator
    /// </code>
    /// <para>
    /// It does NOT touch the four parameter words (<c>[0xBB2C]</c>, <c>[0xBB3A]</c>,
    /// <c>[0xBB30]</c>, <c>[0xBB32]</c>) — with both tones parked at the sentinel the next frame
    /// re-sends them anyway — so neither does this.  The <c>cmd 8</c> belongs to the caller
    /// (<see cref="AudioRuntime.ResetForNewScene"/>), which owns the generators.
    /// </para>
    /// <para>
    /// Its caller of record is <c>mission_state_machine @image@0x008E6</c> at
    /// <c>image@0x00B05</c> — the original resets these channels when a SCENE starts, which is
    /// exactly what the port's session reopen has to do.
    /// </para>
    /// </remarks>
    public void Reset()
    {
        EngineTone = NoTone;                                     // image@0x298C4
        CockpitTone = NoTone;                                    // image@0x298C7
        _threshold = 0;                                          // image@0x298CC / 0x298CF
    }

    /// <summary>Runs one frame of both channels and emits the driver calls they imply.</summary>
    /// <param name="inputs">This frame's state.</param>
    /// <param name="muteMask"><c>g_audio_mute_mask [0xE483]</c>.</param>
    /// <param name="dispatch">Where the <c>(cmd, tone, pitch, volume)</c> calls go.</param>
    public void Update(in ContinuousAudioInputs inputs, byte muteMask, Action<int, int, int, int> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        Update(inputs, muteMask, dispatch, dispatch);
    }

    /// <summary>
    /// The same frame, with the two channels routed SEPARATELY.
    /// </summary>
    /// <param name="inputs">This frame's state.</param>
    /// <param name="muteMask"><c>g_audio_mute_mask [0xE483]</c>.</param>
    /// <param name="engineDispatch">Where channel A's calls go — the player's own positional voice.</param>
    /// <param name="cockpitDispatch">Where channel B's calls go — the mono cockpit generator.</param>
    /// <remarks>
    /// The original has one dispatcher and one OPL; the split is the port's, and it exists so the
    /// ENGINE (channel A, tones 0x00-0x04 and the mechanism 0x13) can be placed in the stereo field
    /// by <see cref="SourceVoicePool"/> while the cockpit ambient — stall, RWR, lock — stays where a
    /// warning tone belongs, in the middle of the listener's head.
    /// </remarks>
    public void Update(
        in ContinuousAudioInputs inputs,
        byte muteMask,
        Action<int, int, int, int> engineDispatch,
        Action<int, int, int, int> cockpitDispatch)
    {
        ArgumentNullException.ThrowIfNull(engineDispatch);
        ArgumentNullException.ThrowIfNull(cockpitDispatch);

        // Guard 2 — master audio enable.  Both channels park, and NOTHING is sent
        // (image@0x29D3E..0x29D4E).
        if ((muteMask & (byte)AudioMuteFlags.Master) == 0)
        {
            EngineTone = NoTone;
            CockpitTone = NoTone;
            return;
        }

        int engineTone = NoTone, enginePitch = -1;
        bool targetView = false;

        if (inputs.FrameTimeAccum < _threshold)
        {
            engineTone = MechanismTone;                          // image@0x29D75
        }
        else if ((muteMask & (byte)AudioMuteFlags.Engine) != 0)       // image@0x29D7C
        {
            (engineTone, enginePitch, targetView) = SelectEngineTone(inputs, ViewPolicy);
        }

        CommitEngine(engineTone, enginePitch, targetView, inputs, engineDispatch);
        CommitCockpit(SelectCockpitTone(inputs, muteMask), cockpitDispatch);
    }

    // ------------------------------------------------------------------ channel A: the engine

    private static (int Tone, int Pitch, bool TargetView) SelectEngineTone(
        in ContinuousAudioInputs inputs, EngineViewPolicy policy)
    {
        // image@0x29D86: the cockpit-view flag, or the external-target view during an engagement,
        // reaches the object-driven selector; anything else falls to the two small arms below.
        bool objectPath = inputs.CockpitView
            || (inputs.EngagementMode && inputs.ViewMode == 0x0C);

        // H13 DEVIATION — with EngineViewPolicy.All the player's own aeroplane takes the object path
        // in EVERY view.  Nothing below this line changes: the same four gates, the same tone
        // family, the same pitch law.  What changes is the volume the caller then computes, which
        // uses the fly-by distance arm in all views (CommitEngine).
        bool outsideVoice = false;
        if (!objectPath)
        {
            if (policy != EngineViewPolicy.All)
            {
                // image@0x29E4A..: with view-flag bit 7 clear, only the CIRCLING view (0x0F) sounds,
                // at a fixed pitch (image@0x29F39..0x29F49).  Bit 7 set is the target-view branch,
                // which the port's host does not drive yet — it selects the same object path from
                // the player's own engagement block, so it is left silent rather than guessed. (open)
                if (!inputs.ViewFlagBit7 && inputs.ViewMode == 0x0F)
                {
                    return (1, 0x12C, false);
                }

                return (NoTone, -1, false);
            }

            outsideVoice = true;
        }

        // image@0x29DA5..0x29DC7 — four gates before a tone is chosen at all.
        if (!inputs.ObjectActive
            || (inputs.TargetViewActive && !inputs.EngagementMode)
            || inputs.PlayerNotFlying)
        {
            return (NoTone, -1, false);
        }

        // image@0x29DCA — [0xF12A] & 2.  listening from OUTSIDE the cockpit takes the same arm
        // the original takes when you watch from the target's seat (image@0x29E6E) — the quieter
        // base level of a remote listener, which is the original's own answer to this question
        // and not an invented one.
        bool targetView = outsideVoice || inputs.TargetViewSubFlag;

        // image@0x29E04..0x29E31 — the tone family is the sounding object's own class flag.
        int tone;
        if (inputs.JetEngine)
        {
            // image@0x29E0D: damaged ⇒ 2; else the afterburner bit picks 1 over 0.
            //   `mov al,[0xf0bc] / and al,1 / cmp al,1 / sbb ax,ax / inc ax` = (bit0 ? 1 : 0).
            tone = inputs.EngineDamaged ? 2 : inputs.Afterburner ? 1 : 0;
        }
        else
        {
            // image@0x29E27: `cmp [0xf1dc],1 / sbb ax,ax / add ax,4` = (damaged ? 4 : 3).
            tone = inputs.EngineDamaged ? 4 : 3;
        }

        int absLoad = Math.Abs(inputs.LoadFactorQ8);             // image@0x29E34..0x29E3A (CDQ/XOR/SUB)
        int pitch = EnginePitch16(
            tone,
            inputs.AirspeedFeetPerSecond,
            inputs.ThrottlePercent,
            absLoad,
            inputs.DriverType);
        return (tone, pitch, targetView);
    }

    /// <summary>
    /// <c>engine_sound_pitch_compute @image@0x29C20</c> — the engine channel's PITCH argument.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two laws, split on the tone family.  Piston (tones 3/4, <c>image@0x29C68</c>):
    /// <c>speed × 250 / 600 + throttle + 20</c>, then <c>+= pitch × |G| / 0x3C00</c>, clamped to
    /// <c>0x190</c>.  Jet (tones 0/1/2, <c>image@0x29C2F</c>): <c>speed × 200 / 1000 + 2 × throttle</c>,
    /// same G blend, clamped to <c>0x1D6</c>, and HALVED when the driver type is 1 (PC speaker,
    /// <c>image@0x29C5D</c>).
    /// </para>
    /// <para>
    /// The <c>DX</c> operand is <c>[0xF035]</c> = the THROTTLE per cent, which is what makes
    /// the engine note follow the throttle; <c>BX</c> is <c>g_airspeed [0xEF99]</c> in ft/s and the
    /// stack word is <c>|g_player_gload_q8|</c>.  The decoded file's "engage_dx / abs_alt_stk"
    /// naming predates both renames.
    /// </para>
    /// </remarks>
    /// <param name="tone">The selected tone id.</param>
    /// <param name="airspeed">Airspeed, ft/s.</param>
    /// <param name="throttlePercent">Throttle, per cent.</param>
    /// <param name="absoluteLoadFactorQ8">|load factor|, Q8.8.</param>
    /// <param name="driverType">The audio driver type <c>[0xE482]</c>.</param>
    /// <returns>The dispatcher's <c>pitch</c> argument.</returns>
    public static int EnginePitch16(
        int tone, int airspeed, int throttlePercent, int absoluteLoadFactorQ8, int driverType)
    {
        short speed = Sat16(airspeed);
        short throttle = Sat16(throttlePercent);
        short load = Sat16(absoluteLoadFactorQ8);

        if (tone is 3 or 4)
        {
            int pitch = Fixed.MulDiv16Signed(speed, 0xFA, 0x258) + throttle + 0x14;
            pitch += Fixed.MulDiv16Signed(Sat16(pitch), load, 0x3C00);
            return Math.Min(pitch, 0x190);
        }

        int jet = Fixed.MulDiv16Signed(speed, 0xC8, 0x3E8) + (throttle * 2);
        jet += Fixed.MulDiv16Signed(Sat16(jet), load, 0x3C00);
        jet = Math.Min(jet, 0x1D6);
        return driverType == 1 ? jet >> 1 : jet;
    }

    /// <summary>
    /// <c>engine_sound_note_compute @image@0x29C9E</c> — the engine channel's VOLUME argument.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is a VOLUME, not a "note": the value lands in the dispatcher's third slot, and tone 0x03's
    /// cmd-04 stub writes it as <c>amp_base = volume &lt;&lt; 6</c> (<c>W(0x1CA3, vol &lt;&lt;
    /// 6)</c>, drv <c>@0x1911</c>), which the generator turns into attenuation <c>(amp ^ 0xFFF)
    /// &gt;&gt; 6</c>.  Its "distance deduction" is therefore a distance ATTENUATION, and the view it
    /// applies in — <c>[0xC320] == 0x0D</c> — is the FLY-BY camera, where the aeroplane really does
    /// pass and recede.
    /// </para>
    /// <para>
    /// Its <c>DX</c> is the TARGET-VIEW flag, not an "airborne flag" (the decoded file marked that
    /// RUNTIME-EVIDENCE NEEDED; the caller settles it statically — <c>[bp-0x18]</c> is <c>[0xF12A]
    /// &amp; 2</c> or 1 when the view is Target's Cockpit, <c>image@0x29DCA</c> /
    /// <c>image@0x29E6E</c>).  Watching from the target's seat makes the other aeroplane's engine
    /// quieter, which is the point.
    /// </para>
    /// <para>
    /// Piston (3/4): 0x3A, or 0x3F when the flag is clear.  Jet (0/1/2): 0x2D, or 0x37.  Clamped to
    /// 0x3F, then the fly-by attenuation subtracts <c>40 × range / 5000</c>, saturating at 40.
    /// </para>
    /// </remarks>
    /// <param name="tone">The selected tone id.</param>
    /// <param name="targetView">The <c>DX</c> flag.</param>
    /// <param name="viewMode">The view id <c>[0xC320]</c>.</param>
    /// <param name="rangeToSource">The view anchor's range to the sounding object.</param>
    /// <returns>The dispatcher's <c>volume</c> argument.</returns>
    public static int EngineVolume(int tone, bool targetView, int viewMode, int rangeToSource)
    {
        int volume = tone is 3 or 4
            ? 0x3A + (targetView ? 0 : 5)                        // image@0x29CBD..0x29CC7
            : 0x2D + (targetView ? 0 : 0xA);                     // image@0x29CCD..0x29CD7
        volume = Math.Min(volume, 0x3F);                         // image@0x29CDA

        if (viewMode != 0x0D)                                    // image@0x29CE5 — the FLY-BY view
        {
            return volume;
        }

        int range = Math.Clamp(rangeToSource, 0, ushort.MaxValue);
        return range >= 0x1388
            ? volume - 0x28                                      // image@0x29D0D
            : volume - Fixed.MulDiv16Signed(0x28, Sat16(range), 0x1388);   // image@0x29D1C
    }

    private void CommitEngine(
        int tone,
        int pitch,
        bool targetView,
        in ContinuousAudioInputs inputs,
        Action<int, int, int, int> dispatch)
    {
        int previous = EngineTone;
        if (tone != previous && previous >= 0)
        {
            dispatch(2, previous, 0, 0);                         // image@0x29F57
            Stops++;
        }

        // H13 DEVIATION — with EngineViewPolicy.All the FLY-BY arm of the volume law
        // (image@0x29CE5's `[0xC320] == 0x0D` test) is taken in every view, so the player's own
        // engine is attenuated by the camera's distance to it wherever the camera is.  In an
        // interior view that range is zero and the law's deduction is zero with it, which is why
        // the cockpit still sounds exactly as H11 rendered it.
        int viewMode = ViewPolicy == EngineViewPolicy.All ? 0x0D : inputs.ViewMode;
        int volume = EngineVolume(tone, targetView, viewMode, inputs.RangeToSource);

        if (tone >= 0)
        {
            if (tone != previous)
            {
                dispatch(0, tone, pitch, volume);                // image@0x29F7A
                Starts++;
            }
            else if (pitch != EnginePitch || volume != EngineVolumeValue)
            {
                dispatch(4, tone, pitch, volume);                // image@0x29FA0
                Updates++;
            }
        }

        EngineTone = tone;                                       // image@0x29FAD
        EnginePitch = pitch;                                     // image@0x29FB3
        EngineVolumeValue = volume;                              // image@0x29FB9
    }

    // ---------------------------------------------------------------- channel B: cockpit ambient

    private static (int Tone, int Param1, int Param2) SelectCockpitTone(
        in ContinuousAudioInputs inputs, byte muteMask)
    {
        // image@0x29FBC..0x29FCF — not during an engagement view, cockpit view up, in-flight gate.
        if (inputs.EngagementMode || !inputs.CockpitView || !inputs.InFlightGate)
        {
            return (NoTone, 0, 0);
        }

        // image@0x29FD1 — mute bit 3, "Stall sounds".
        if ((muteMask & (byte)AudioMuteFlags.Stall) != 0)
        {
            if (inputs.StallState == 2)
            {
                return (0x1E, 0, 0);                             // image@0x29FDF
            }

            if (inputs.StallState == 3)
            {
                return (0x1F, 0, 0);                             // image@0x2A02A
            }
        }

        // image@0x2A031 — the RWR, mute bit 2, two intensities.
        if (inputs.WarningsEnabled
            && inputs.RadarWarningActive
            && (muteMask & (byte)AudioMuteFlags.RadarWarning) != 0)
        {
            if (inputs.ActiveTargets > 0)
            {
                return (0x11, 0x5C, 100);                        // image@0x2A04D
            }

            if (inputs.EngagementsLockedOnPlayer > 0)
            {
                return (0x11, 0x3C, 0x32);                       // image@0x2A065
            }
        }

        // image@0x2A077 — the LOCK tones, mute bit 4.
        if (!inputs.WarningsEnabled
            || (muteMask & (byte)AudioMuteFlags.Lock) == 0
            || inputs.WeaponNameFirstByte < 0)
        {
            return (NoTone, 0, 0);
        }

        bool radar = inputs.WeaponNameFirstByte == 1;            // image@0x2A096
        return inputs.LockType switch
        {
            2 => (0x0F + (radar ? 0 : 0x12), 0, 0),              // image@0x2A0A9 — 0x0F or 0x21
            1 => (0x0E + (radar ? 0 : 2), 0, 0),                 // image@0x2A0C4 — 0x0E or 0x10
            _ => (NoTone, 0, 0),
        };
    }

    private void CommitCockpit((int Tone, int Param1, int Param2) wanted, Action<int, int, int, int> dispatch)
    {
        int previous = CockpitTone;
        if (wanted.Tone != previous && previous >= 0)
        {
            dispatch(2, previous, 0, 0);                         // image@0x29FF5
            Stops++;
        }

        if (wanted.Tone >= 0)
        {
            if (wanted.Tone != previous)
            {
                dispatch(0, wanted.Tone, wanted.Param1, wanted.Param2);   // image@0x2A01A
                Starts++;
            }
            else if (wanted.Param1 != CockpitParam1 || wanted.Param2 != CockpitParam2)
            {
                // image@0x2A0E8 then image@0x2A0F9 — stop and restart, because none of the cockpit
                // tones is cmd-04-updatable.
                dispatch(2, wanted.Tone, 0, 0);
                Stops++;
                dispatch(0, wanted.Tone, wanted.Param1, wanted.Param2);
                Starts++;
            }
        }

        CockpitTone = wanted.Tone;                               // image@0x2A0FF
        CockpitParam1 = wanted.Param1;                           // image@0x2A105
        CockpitParam2 = wanted.Param2;                           // image@0x2A10B
    }

    /// <summary>Clamps a port-side quantity into the original's 16-bit register domain.</summary>
    /// <remarks>
    /// The original's inputs are already <c>u16</c>/<c>i16</c> globals, so nothing can exceed this
    /// range in a faithful run; the clamp is a guard against a host feeding a wider value, not a
    /// modelled behaviour.
    /// </remarks>
    private static short Sat16(int value) =>
        (short)Math.Clamp(value, short.MinValue, short.MaxValue);
}
