namespace CYAC.Port.Core.Sim;

/// <summary>
/// The port's random-number streams, one per consumer group.
/// </summary>
/// <remarks>
/// <para>
/// The set and the names are the ones the RNG consumer map assigns every <c>prng_*</c> call site in
/// the image to, a byte census of 195 sites (Sim 136, Fx 48, Audio 7, internal 4, unassigned 0).
/// Those names are load-bearing: a stream's seed is derived from its name
/// (<see cref="RandomStreams"/>), so renaming one re-seeds it and is a kernel-era bump.
/// </para>
/// <para>
/// The shipped game has <b>one</b> stream and one word of state, <c>g_prng_state_lo [0x07A8]</c>, and
/// everything draws from it — which is why, in the shipped binary, the graphics-detail setting
/// changes combat outcomes.  Splitting the streams is what makes "same inputs ⇒ same
/// mission across every presentation setting" true in the port (human DIRECTION §9.2: "our split of
/// random sources is then our force multiplier").
/// </para>
/// </remarks>
public enum RandomStreamId
{
    /// <summary><c>Sim.AI</c> — 79 sites: VM-host FSM, target acquisition, evasion, aim error, AI spawn timing.</summary>
    SimAi = 0,

    /// <summary><c>Sim.Fire</c> — 22 sites: shot/hit resolution, fire authorisation, cooldowns, projectile spawn, countermeasures.</summary>
    SimFire = 1,

    /// <summary><c>Sim.Damage</c> — 4 sites: damage amount/severity, structural failure, end-of-flight arming.</summary>
    SimDamage = 2,

    /// <summary><c>Sim.Mission</c> — 21 sites: mission/scenario GENERATION (custom-mission builder, load-time placement).</summary>
    SimMission = 3,

    /// <summary><c>Sim.Other</c> — 10 sites: other gameplay/world state (seeding, list ordering, ejection, cloud deck).</summary>
    SimOther = 4,

    /// <summary><c>Fx.Particles</c> — 8 sites: explosion puffs, debris, countermeasure tumble, effect sprites.</summary>
    FxParticles = 5,

    /// <summary><c>Fx.Indicators</c> — 29 sites: HUD markers, cockpit text, radio chatter, advisor messages, dial phases.</summary>
    FxIndicators = 6,

    /// <summary><c>Fx.Other</c> — 11 sites: front-end / post-mission text and modals.</summary>
    FxOther = 7,

    /// <summary><c>Audio.Jitter</c> — 6 sites: pitch / volume / duration perturbation of a sound.</summary>
    AudioJitter = 8,

    /// <summary><c>Audio.Voice</c> — 1 site: which sound or tune is played.</summary>
    AudioVoice = 9,
}

/// <summary>Which generator backs a stream.</summary>
public enum RandomStreamKind
{
    /// <summary>The shipped 16-bit Galois LFSR (<see cref="Primitives.Lfsr16"/>) — every <c>Sim.*</c> stream (A2).</summary>
    Lfsr16 = 0,

    /// <summary><see cref="Xoshiro256StarStar"/> — every <c>Fx.*</c> / <c>Audio.*</c> stream.</summary>
    Xoshiro256StarStar = 1,
}

/// <summary>Per-stream determinism policy.</summary>
public enum RandomStreamPolicy
{
    /// <summary>Seeded from the replay header — replay-exact.  The default for every stream.</summary>
    Seeded = 0,

    /// <summary>
    /// Seeded from host entropy on every run — deliberately NOT replay-exact.  Legal only for a
    /// stream whose draws can never reach a decision (i.e. never for <c>Sim.*</c>); the log records
    /// the choice so a divergent replay explains itself.
    /// </summary>
    Free = 1,
}

/// <summary>How the five <c>Sim.*</c> streams relate to the binary's single state word.</summary>
/// <remarks>
/// The binary has exactly one Sim word (<c>g_prng_state_lo [0x07A8]</c>), so five independently
/// seeded streams cannot be identical to it by construction.  <see cref="Compat"/> exists so
/// emulator-vs-port verification of <i>decisions</i> is possible at all.
/// </remarks>
public enum SimStreamMode
{
    /// <summary>
    /// The port's default: five independently seeded LFSR16 words.  Isolates consumers, so a
    /// subsystem can be turned off, re-ordered or fixed without moving any other stream.
    /// </summary>
    Split = 0,

    /// <summary>
    /// Verification mode: all five <c>Sim.*</c> streams share ONE LFSR16 word, seeded exactly as
    /// <c>prng_seed_or_force1 @image@0x19EB6</c> seeds the binary's.  With the same draw order the
    /// port then consumes the same bits in the same sequence as the patched twin.
    /// </summary>
    Compat = 1,
}

/// <summary>The names, classification and per-stream facts of <see cref="RandomStreamId"/>.</summary>
public static class RandomStreamCatalog
{
    /// <summary>Every stream, in declaration order.</summary>
    public static IReadOnlyList<RandomStreamId> All { get; } =
        Enum.GetValues<RandomStreamId>();

    /// <summary>The canonical wire name — <c>"Sim.AI"</c>, <c>"Fx.Particles"</c>, … .</summary>
    /// <param name="id">The stream.</param>
    /// <exception cref="ArgumentOutOfRangeException">Not a declared stream.</exception>
    public static string Name(RandomStreamId id) => id switch
    {
        RandomStreamId.SimAi => "Sim.AI",
        RandomStreamId.SimFire => "Sim.Fire",
        RandomStreamId.SimDamage => "Sim.Damage",
        RandomStreamId.SimMission => "Sim.Mission",
        RandomStreamId.SimOther => "Sim.Other",
        RandomStreamId.FxParticles => "Fx.Particles",
        RandomStreamId.FxIndicators => "Fx.Indicators",
        RandomStreamId.FxOther => "Fx.Other",
        RandomStreamId.AudioJitter => "Audio.Jitter",
        RandomStreamId.AudioVoice => "Audio.Voice",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "not a declared random stream"),
    };

    /// <summary>Parses a canonical wire name.</summary>
    /// <param name="name">The name, e.g. <c>"Sim.AI"</c>.</param>
    /// <exception cref="InvalidDataException">The name is not one of the ten streams.</exception>
    public static RandomStreamId Parse(string? name)
    {
        foreach (RandomStreamId id in All)
        {
            if (string.Equals(Name(id), name, StringComparison.Ordinal))
            {
                return id;
            }
        }

        throw new InvalidDataException(
            $"\"{name}\" is not a random stream; expected one of {string.Join(", ", All.Select(Name))}");
    }

    /// <summary>True for the five decision-side streams — the INT-only spine.</summary>
    /// <param name="id">The stream.</param>
    public static bool IsSim(RandomStreamId id) => id <= RandomStreamId.SimOther;

    /// <summary>Which generator backs the stream: LFSR16 for <c>Sim.*</c>, xoshiro256** otherwise.</summary>
    /// <param name="id">The stream.</param>
    public static RandomStreamKind Kind(RandomStreamId id) =>
        IsSim(id) ? RandomStreamKind.Lfsr16 : RandomStreamKind.Xoshiro256StarStar;

    /// <summary>
    /// How many call sites the consumer map assigns to this stream, kept here so a port-side stream that
    /// nothing draws from is visible as such.
    /// </summary>
    /// <param name="id">The stream.</param>
    public static int OriginalCallSites(RandomStreamId id) => id switch
    {
        RandomStreamId.SimAi => 79,
        RandomStreamId.SimFire => 22,
        RandomStreamId.SimDamage => 4,
        RandomStreamId.SimMission => 21,
        RandomStreamId.SimOther => 10,
        RandomStreamId.FxParticles => 8,
        RandomStreamId.FxIndicators => 29,
        RandomStreamId.FxOther => 11,
        RandomStreamId.AudioJitter => 6,
        RandomStreamId.AudioVoice => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "not a declared random stream"),
    };
}
