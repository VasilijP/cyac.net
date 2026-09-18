using CYAC.Port.Core.Sim.Combat;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// The SOUND seam: the eight places the combat and lifecycle kernels reach the audio system, in the
/// shape the original's SFX trampolines have.
/// </summary>
/// <remarks>
/// <para>
/// Batch 6 and H4/H7 counted these calls (<c>EffectCensus.Sounds</c>) because there was no audio
/// path to make them.  There is one now, in <c>CYAC.Port.Audio</c>; this interface is how the
/// integer kernels reach it without <c>CYAC.Port.Core</c> knowing anything about sound.  It is
/// SEMANTIC on purpose: the fixed tone ids, the driver command codes and the per-shot randomization
/// belong to the audio assembly, which owns <c>adldrive.drv</c>'s vocabulary.
/// </para>
/// <para>
/// <b>The audio path never writes simulation state</b>, which is what keeps a sortie
/// deterministic: every method here is a one-way notification, no return value influences a decision, and the
/// implementation draws its per-shot randomization from the port's own <c>Audio.Jitter</c> stream
/// (<c>RandomStreamId.AudioJitter</c>), never from a <c>Sim.*</c> stream.  The shipped game has one
/// LFSR and its SFX draws DO perturb combat; the port's split is the deliberate fix.
/// </para>
/// </remarks>
public interface IAudioEvents
{
    /// <summary>
    /// <c>sfx_object_impact_dispatch @image@0x29BA3</c> — the positional impact sound.
    /// </summary>
    /// <remarks>
    /// The routine takes a far pointer to the object's position triple and an <c>AL</c> byte, ranges
    /// the position against the view anchor (<c>lcall 0x32aa:0x19a8</c> =
    /// <c>object_range_from_view_anchor @image@0x24448</c>) and drops the sound entirely beyond
    /// <c>0x2710</c>.  <c>AL != 0</c> takes the loud random branch, <c>AL == 0</c> the soft one.
    /// Call sites: <c>image@0x02715</c> (AL=1), <c>image@0x0C0D6</c> (AL = the resolver's
    /// lethal-class flag), <c>image@0x08B4D</c> and <c>image@0x08BE9</c> (AL=1), <c>image@0x05571</c>
    /// (AL=1), and <c>image@0x2C7C9</c> (<c>sub al,al</c> — AL=0).
    /// </remarks>
    /// <param name="position">Where it happened, in world position units.</param>
    /// <param name="loud">The <c>AL</c> byte: true for a weapon impact, false for a soft one.</param>
    void ObjectImpact(CombatPosition position, bool loud);

    /// <summary>
    /// <c>sfx_weapon_type_tone_dispatch @image@0x29A79</c> — a weapon firing.
    /// </summary>
    /// <remarks>
    /// <c>test byte [bx+0x24],0x10</c> @<c>image@0x29A7C</c> splits the two arms; the gun arm passes
    /// the weapon record's own <c>+0x2B</c> byte as the volume.  Reached for an AI's shot through
    /// <c>sfx_selected_object_audio_dispatch @image@0x29AA8</c>, which is silent unless the firing
    /// engagement is the player's selected object (<c>cmp bx,[0xBE]</c>) and view-flag bit 2 is set
    /// (<c>image@0x083D8</c>).
    /// </remarks>
    /// <param name="airToGround">The <c>[+0x24] &amp; 0x10</c> arm.</param>
    /// <param name="volume">The weapon record's <c>[+0x2B]</c> byte (the gun arm's volume).</param>
    void WeaponFire(bool airToGround, int volume);

    /// <summary>
    /// The same weapon sound, from an object that is NOT the player, with where it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The 1991 game plays an AI's gun through <c>sfx_selected_object_audio_dispatch
    /// @image@0x29AA8</c>, which forwards to the same trampoline as the player's own gun but only
    /// when the firing engagement is the player's SELECTED object (<c>cmp bx,[0xBE]</c>) and
    /// view-flag bit 2 is set (<c>test byte [0xf12a],4</c> @<c>image@0x29AAE</c>) — no position, no
    /// attenuation, one shooter at most.  This seam carries the shooter's identity and position so a
    /// port with a positional mixer can place it; an implementation with the pool switched off falls
    /// back to the original's gate, which is what <paramref name="selected"/> is for.
    /// </para>
    /// <para>
    /// Call site: <c>enemy_target_acquisition_state_machine</c>'s fire arm, <c>image@0x083D8</c>,
    /// with the shooter in <c>[0xED56]</c>.
    /// </para>
    /// </remarks>
    /// <param name="sourceId">The firing object's arena offset (<c>[0xED56]</c>).</param>
    /// <param name="position">Where the shooter is, in world position units.</param>
    /// <param name="selected">Whether it is the player's selected object (<c>[0xBE]</c>).</param>
    /// <param name="airToGround">The <c>[+0x24] &amp; 0x10</c> arm.</param>
    /// <param name="volume">The weapon record's <c>[+0x2B]</c> byte.</param>
    void ObjectWeaponFire(
        int sourceId, CombatPosition position, bool selected, bool airToGround, int volume);

    /// <summary>One of the fixed-tone trampolines.</summary>
    /// <remarks>
    /// <c>sfx_play_tone0d @image@0x29B82</c> (0x0D), <c>sfx_play_tone16_random @image@0x29AF2</c>
    /// (0x16), <c>sfx_play_tone14_fallback @image@0x29ABD</c> (0x14),
    /// <c>sfx_play_tone0a @image@0x29B1C</c> (0x0A), <c>sfx_play_tone09 @image@0x29B60</c> (0x09),
    /// <c>sfx_countermeasure_deploy_sound @image@0x29B29</c> (0x20).
    /// </remarks>
    /// <param name="toneId">The driver tone id the trampoline pushes.</param>
    void PlayTone(int toneId);

    /// <summary>
    /// The two RANDOMIZED impact trampolines — <c>sfx_play_tone15_random @image@0x29ACA</c> and
    /// <c>sfx_play_tone16_random @image@0x29AF2</c>.
    /// </summary>
    /// <remarks>
    /// Each draws its own pitch and volume before dispatching: variant A takes
    /// <c>volume = 0x3F − rand(3)</c>, <c>pitch = rand(4) + 3</c>; variant B
    /// <c>volume = 0x3F − rand(3)</c>, <c>pitch = 6 − rand(2)</c>.  Variant B is also the
    /// engagement DEPARTURE cue (<c>lcall 0x3981:0x2e2</c> @<c>image@0x08BA0</c>).
    /// </remarks>
    /// <param name="variantB">True for tone 0x16, false for tone 0x15.</param>
    void PlayRandomizedImpactTone(bool variantB);

    /// <summary>
    /// <c>audio_threshold_bump_flap / _airbrake / _gear @image@0x29B36 / 0x29B4B / 0x29B6D</c> — run
    /// the mechanism sound for a while.
    /// </summary>
    /// <remarks>
    /// Each writes <c>g_audio_time_threshold [0xBB3C:0xBB3E] = g_frame_time_accum + delta</c> (flap
    /// 0x20, airbrake 0x10, gear 0x100), and <c>continuous_audio_state_update</c>'s first test
    /// (<c>image@0x29D5F..0x29D79</c>) forces engine-channel tone <c>0x13</c> for as long as the
    /// frame-time accumulator is below it.  It is the ACTUATOR sound.
    /// </remarks>
    /// <param name="frameTimeUnits">The delta: 0x20 flaps, 0x10 airbrake, 0x100 gear.</param>
    void MechanismSound(int frameTimeUnits);
}
