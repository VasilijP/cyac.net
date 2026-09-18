namespace CYAC.Port.Audio;

/// <summary>
/// Everything <c>continuous_audio_state_update @image@0x29D2E</c> reads, as a read-only per-frame
/// snapshot the host fills in.
/// </summary>
/// <remarks>
/// <para>
/// The original reads these out of DGROUP; the port hands them over explicitly so the audio path can
/// stay a pure function of its inputs and can never reach into simulation state, which is what
/// keeps a sortie deterministic.  Every field names the global it stands for.
/// </para>
/// <para>
/// A default-constructed value is the INERT one: no cockpit view, no engagement, a stopped
/// aeroplane, no warnings — which selects "no sound requested" on both channels.
/// </para>
/// </remarks>
public readonly record struct ContinuousAudioInputs
{
    /// <summary><c>g_frame_time_accum [0xF0D2:0xF0D4]</c> — the 32-bit per-scene frame-time clock.</summary>
    public required long FrameTimeAccum { get; init; }

    /// <summary><c>g_view_flag_byte_cached [0xF12A]</c> bit 0x40 — the cockpit view is up.</summary>
    public bool CockpitView { get; init; }

    /// <summary><c>g_view_flag_byte_cached [0xF12A]</c> bit 0x02 — a target-view sub-flag.</summary>
    /// <remarks>
    /// Passed straight to the volume law as its <c>DX</c>.  It is NOT an "airborne flag" — see
    /// <see cref="ContinuousChannels.EngineVolume"/>.
    /// </remarks>
    public bool TargetViewSubFlag { get; init; }

    /// <summary><c>g_view_flag_byte_cached [0xF12A]</c> bit 0x80.</summary>
    public bool ViewFlagBit7 { get; init; }

    /// <summary><c>g_current_view_mode [0xC320]</c> — the view id, 0x00..0x13.</summary>
    /// <remarks>
    /// The same numbering the port's <c>ViewMode</c> uses: 0x0C External Target, 0x0D Fly-By,
    /// 0x0F Circling, 0x12 Target's Cockpit.
    /// </remarks>
    public int ViewMode { get; init; }

    /// <summary><c>g_engagement_mode_flag [0xF28A]</c>.</summary>
    public bool EngagementMode { get; init; }

    /// <summary><c>[0xC32F]</c> — the player is no longer flying (ejected / film replay).</summary>
    public bool PlayerNotFlying { get; init; }

    /// <summary><c>g_target_view_active_flag [0xF1D2]</c>.</summary>
    public bool TargetViewActive { get; init; }

    /// <summary>The sounding object's slot-active bit (<c>obj[+2] &amp; 1</c> @<c>image@0x29DA5</c>).</summary>
    public bool ObjectActive { get; init; }

    /// <summary>
    /// The sounding object's class flag <c>[classRecord+1] &amp; 0x20</c> (<c>test byte
    /// [bx+1],0x20</c> @<c>image@0x29E07</c>) — a JET.
    /// </summary>
    /// <remarks>
    /// Byte-proven on the shipped class registry: set on <c>f4</c>, <c>f86</c>, <c>mig15</c> and
    /// <c>mig21</c> (<c>flags 0x2C</c>), clear on <c>p51</c>, <c>fw190</c>, <c>me109</c> and
    /// <c>b17</c> (<c>flags 0x0C</c>) — <c>data/exe/classes.json</c>.  It picks the whole tone family:
    /// jets get 0x00/0x01/0x02, pistons get 0x03/0x04.
    /// </remarks>
    public bool JetEngine { get; init; }

    /// <summary>
    /// <c>g_input_state_bitfield [0xF0BC]</c> bit 0 — the AFTERBURNER
    /// (= <c>s_aircraft_master+0x124</c> bit 0, K6).
    /// </summary>
    public bool Afterburner { get; init; }

    /// <summary>
    /// <c>g_weapon_burst_state_b [0xF1DC]</c> — the player-damage flag two arms of
    /// <c>weapon_fire_combat_loop</c> raise (<c>image@0x0FB10</c>, <c>image@0x0FB22</c>).
    /// </summary>
    /// <remarks>It selects the DAMAGED engine tone: jet 0x02 instead of 0x00/0x01, piston 0x04
    /// instead of 0x03.  Which damage effect exactly is <c>(open)</c> — H11 §4.</remarks>
    public bool EngineDamaged { get; init; }

    /// <summary><c>g_airspeed [0xEF99]</c>, in ft/s.</summary>
    /// <remarks>
    /// Units pinned by the eject rule <c>cmp [0xEF99],0x2DD</c> = 733 ft/s = the manual's 500 mph.
    /// </remarks>
    public int AirspeedFeetPerSecond { get; init; }

    /// <summary><c>[0xF035]</c> — the throttle, per cent (an earlier pass named it from cockpit dial slot 5).</summary>
    public int ThrottlePercent { get; init; }

    /// <summary><c>g_player_gload_q8 [0xF06E]</c> — the signed load factor, Q8.8 (1.0 G = 0x0100).</summary>
    public int LoadFactorQ8 { get; init; }

    /// <summary>
    /// The range from the view anchor to the sounding object, for the fly-by view's attenuation.
    /// </summary>
    /// <remarks>
    /// <c>object_range_from_view_anchor(alt_object + 6)</c> @<c>image@0x29CFB</c>, consumed only when
    /// <see cref="ViewMode"/> is 0x0D.
    /// </remarks>
    public int RangeToSource { get; init; }

    /// <summary><c>[0xF0BA]</c> — 2 and 3 select cockpit tones 0x1E and 0x1F.</summary>
    /// <remarks>
    /// Gated by mute bit 3, which the System menu labels "Stall sounds" — so these are the stall
    /// cues.  The scanner name <c>g_text_string_mode</c> is a misnomer here; H11 §3 proposes a
    /// rename.
    /// </remarks>
    public int StallState { get; init; }

    /// <summary><c>g_deadzone_enable_flag [0xE470]</c> — the warning-tone master enable.</summary>
    public bool WarningsEnabled { get; init; }

    /// <summary><c>[0xC31C]</c> — the in-flight key gate, which also gates the cockpit channel.</summary>
    public bool InFlightGate { get; init; }

    /// <summary><c>g_rwr_lock_active_flag [0xEF4A]</c>.</summary>
    public bool RadarWarningActive { get; init; }

    /// <summary><c>g_scene_word_F0C2 [0xF0C2]</c> — the active-target counter.</summary>
    public int ActiveTargets { get; init; }

    /// <summary><c>g_engagements_locked_on_player [0xF128]</c>.</summary>
    public int EngagementsLockedOnPlayer { get; init; }

    /// <summary><c>g_lock_type_byte [0xF1B7]</c> — 1 or 2 select the lock-tone pair.</summary>
    public int LockType { get; init; }

    /// <summary>
    /// The first byte of the current weapon's name string (<c>[0xED1E]</c> → <c>[bx]</c>), or −1 when
    /// the pointer is null.
    /// </summary>
    /// <remarks><c>cmp byte [bx],1</c> @<c>image@0x2A096</c> — 1 selects the radar-lock tone of the
    /// pair, anything else the IR one.</remarks>
    public int WeaponNameFirstByte { get; init; }

    /// <summary>
    /// The audio driver type <c>[0xE482]</c> — 1 (PC speaker) halves the engine pitch
    /// (<c>image@0x29C5D</c>).
    /// </summary>
    /// <remarks>The port renders the AdLib driver, so this is 2 unless a test says otherwise.</remarks>
    public int DriverType { get; init; }
}
