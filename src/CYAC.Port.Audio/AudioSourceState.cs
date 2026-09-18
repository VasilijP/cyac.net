using CYAC.Port.Core.Sim.Combat;

namespace CYAC.Port.Audio;

/// <summary>
/// Where the LISTENER is and which way it faces, once a frame.
/// </summary>
/// <remarks>
/// The position is the view anchor <c>s_view_anchor [0xD88E]</c> — the camera — which is what
/// <c>object_range_from_view_anchor @image@0x24448</c> measures every positional sound from.  The
/// right vector is the port's own (the original is mono and has no such thing); the host passes it
/// as three doubles because this assembly may not reference <c>CYAC.Port.Render</c>.
/// </remarks>
public readonly record struct AudioListener
{
    /// <summary>The camera, in POSITION units (256 per world foot), as the arena keeps them.</summary>
    public required CombatPosition Position { get; init; }

    /// <summary>The camera basis' right vector, world X.</summary>
    public double RightX { get; init; }

    /// <summary>…Y.</summary>
    public double RightY { get; init; }

    /// <summary>…Z.</summary>
    public double RightZ { get; init; }

    /// <summary>
    /// Whether the camera is INSIDE the player's cockpit — the six F1..F6 views whose flag byte
    /// carries bit 1.
    /// </summary>
    /// <remarks>
    /// It decides two port-side things and nothing of the original's: the player's own engine is
    /// heard unfiltered and unpanned from inside, and filtered ("through the air") from outside.
    /// </remarks>
    public bool CockpitInterior { get; init; }
}

/// <summary>
/// One thing in the world that MAKES an engine noise, as the host publishes it each frame.
/// </summary>
/// <remarks>
/// <para>
/// The 1991 game sounds exactly ONE object: <c>continuous_audio_state_update @image@0x29D2E</c>
/// picks the player's own aeroplane (or, in the target views, the one being watched) and there is no
/// second engine anywhere in the driver-call stream.  A pool of these is therefore entirely the
/// port's (an authorised deviation) — but every VALUE in it is the original's: the tone family comes
/// from the class record's own jet bit (<c>test byte [bx+1],0x20</c> @<c>image@0x29E07</c>), the
/// pitch from <c>engine_sound_pitch_compute @image@0x29C20</c> and the volume from
/// <c>engine_sound_volume_compute @image@0x29C9E</c>'s fly-by arm.
/// </para>
/// </remarks>
public readonly record struct AudioSourceState
{
    /// <summary>The pool object's arena offset — the identity a voice is keyed by.</summary>
    public required int Id { get; init; }

    /// <summary>Where it is, in POSITION units (256 per world foot).</summary>
    public required CombatPosition Position { get; init; }

    /// <summary>Whether this is the player's own aeroplane.</summary>
    /// <remarks>
    /// The player's voice is not one of the K: its tone comes from
    /// <see cref="ContinuousChannels"/> — the original's own channel A, mechanism sound and all —
    /// and the pool only PLACES it.
    /// </remarks>
    public bool IsPlayer { get; init; }

    /// <summary>
    /// The class record's <c>flags_b_u8</c> bit 5 — a JET (<c>image@0x29E07</c>; set on
    /// <c>f4</c>, <c>f86</c>, <c>mig15</c>, <c>mig21</c> and on no piston, H11 §3.2).
    /// </summary>
    public bool JetEngine { get; init; }

    /// <summary><c>[0xF0BC]</c> bit 0 — the afterburner tone (jets only, <c>image@0x29E1B</c>).</summary>
    public bool Afterburner { get; init; }

    /// <summary>The damaged-engine tone (<c>[0xF1DC]</c>'s role, <c>image@0x29E0D</c>).</summary>
    public bool EngineDamaged { get; init; }

    /// <summary>Its airspeed in ft/s — <c>engine_sound_pitch_compute</c>'s <c>BX</c>.</summary>
    /// <remarks>
    /// For an AI this is its engagement block's own <c>+0x26</c> speed word
    /// (<c>EngagementState.SpeedFps</c>, the <c>&gt;&gt;8</c> view of the Q8 speed the manoeuvring
    /// kernel integrates).
    /// </remarks>
    public int AirspeedFeetPerSecond { get; init; }

    /// <summary>Its throttle per cent — the law's <c>DX</c> (<c>[0xF035]</c> for the player).</summary>
    /// <remarks>
    /// <c>(open)</c> for an AI: the shipped engagement block has no throttle field, and the manoeuvring
    /// kernel drives speed directly.  The port passes 0, so an AI's engine note follows its speed alone
    /// and sits below the player's at the same speed.
    /// </remarks>
    public int ThrottlePercent { get; init; }
}
