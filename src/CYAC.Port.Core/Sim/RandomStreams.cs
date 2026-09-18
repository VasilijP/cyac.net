using System.Security.Cryptography;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim;

/// <summary>
/// A snapshot of one stream's generator state — a replay checkpoint's atom.
/// </summary>
/// <param name="Stream">The canonical stream name.</param>
/// <param name="Kind">Which generator backs it.</param>
/// <param name="Word">The LFSR16 state word (0 for a xoshiro stream).</param>
/// <param name="S0">Xoshiro state word 0 (0 for an LFSR stream).</param>
/// <param name="S1">Xoshiro state word 1.</param>
/// <param name="S2">Xoshiro state word 2.</param>
/// <param name="S3">Xoshiro state word 3.</param>
/// <param name="Draws">Bits drawn (LFSR) or 64-bit draws made (xoshiro) — diagnostic, not state.</param>
public readonly record struct RandomStreamSnapshot(
    string Stream,
    RandomStreamKind Kind,
    ushort Word,
    ulong S0,
    ulong S1,
    ulong S2,
    ulong S3,
    long Draws);

/// <summary>
/// Every stream's state at one instant — what a replay checkpoint carries so a divergence can be
/// localised to a step range instead of "it ended differently" (item 7).
/// </summary>
/// <param name="SimMode">Whether the <c>Sim.*</c> streams were split or sharing one word.</param>
/// <param name="Streams">One snapshot per stream, in <see cref="RandomStreamCatalog.All"/> order.</param>
public sealed record RandomStreamsState(SimStreamMode SimMode, IReadOnlyList<RandomStreamSnapshot> Streams)
{
    /// <summary>
    /// Structural equality over the stream list — a checkpoint is a VALUE, and "the kernel is where
    /// it was" is the assertion every fixed-point test makes.  The synthesized record equality would
    /// compare the list by reference and quietly answer "different" for two identical snapshots.
    /// </summary>
    /// <param name="other">The state to compare with.</param>
    public bool Equals(RandomStreamsState? other) =>
        other is not null && SimMode == other.SimMode && Streams.SequenceEqual(other.Streams);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default(HashCode);
        hash.Add(SimMode);
        foreach (RandomStreamSnapshot snapshot in Streams)
        {
            hash.Add(snapshot);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// How a <see cref="RandomStreams"/> set is seeded and which streams are replay-exact.
/// </summary>
/// <remarks>
/// Recorded verbatim in an <see cref="InputLog"/> header: a replay that seeds differently is not a
/// replay.  Changing any field here changes the sequence every stream produces, so the config is
/// part of the kernel era's identity.
/// </remarks>
public sealed record RandomStreamsConfig
{
    /// <summary>
    /// The 64-bit master seed every stream's seed is derived from
    /// (<c>splitmix64(master ^ fnv1a64(name))</c>).
    /// </summary>
    public ulong MasterSeed { get; init; }

    /// <summary>Split (the port's default) or Compat (one shared <c>Sim.*</c> word, for verification).</summary>
    public SimStreamMode SimMode { get; init; } = SimStreamMode.Split;

    /// <summary>
    /// The binary's single 16-bit mission seed, when this session has one — the value the emulator
    /// forces with <c>--det SEED</c> and stamps in the CYEV v7 header.  Required by
    /// <see cref="SimStreamMode.Compat"/>; recorded but not used for derivation under
    /// <see cref="SimStreamMode.Split"/>.
    /// </summary>
    public ushort? SimSeedWord { get; init; }

    /// <summary>Per-stream policy overrides; anything absent is <see cref="RandomStreamPolicy.Seeded"/>.</summary>
    public IReadOnlyDictionary<RandomStreamId, RandomStreamPolicy>? Policies { get; init; }

    /// <summary>The policy in force for a stream.</summary>
    /// <param name="id">The stream.</param>
    public RandomStreamPolicy PolicyFor(RandomStreamId id) =>
        Policies is not null && Policies.TryGetValue(id, out RandomStreamPolicy policy)
            ? policy
            : RandomStreamPolicy.Seeded;
}

/// <summary>
/// The port's random-number kernel: ten independently seeded streams, one per consumer group.
/// </summary>
/// <remarks>
/// <para>
/// Answers D2 (human DIRECTION: "divide consumers … use a separate source for each — easier to
/// isolate each part, reproduce bugs, turn parts ON/OFF without influencing replay accuracy for the
/// rest").  The five <c>Sim.*</c> streams are INT-only and keep the shipped LFSR16 (amendment A2);
/// the five <c>Fx.*</c>/<c>Audio.*</c> streams are FLOAT-only xoshiro256**.
/// </para>
/// <para>
/// <b>Seeding.</b> Each stream's 64-bit seed is <c>splitmix64(master ^ fnv1a64(stream_name))</c>, so
/// adding a stream never re-seeds the others and a stream can be re-seeded alone.  A <c>Sim.*</c>
/// stream then installs the LOW WORD of that value through the original's force-to-1 defence —
/// <c>prng_seed_or_force1 @image@0x19EB6</c>: a zero seed becomes 1, because 0 is the LFSR's
/// absorbing state.
/// </para>
/// <para>
/// <b>How the five Sim words relate to the binary's single word — stated honestly.</b> They cannot
/// be identical by construction: the binary has ONE Sim word and the port has five.  Under
/// <see cref="SimStreamMode.Split"/> each of the five is a different derivative of the same master
/// seed, so a port session and a twin session share a seed but not a bit sequence — the port's
/// determinism is its own ("the port is not trying to reproduce the 1991 trajectory; it is trying to
/// reproduce its own, exactly, forever").  Emulator-vs-port verification of <i>decisions</i>
/// therefore requires <see cref="SimStreamMode.Compat"/>, where all five <c>Sim.*</c> streams share
/// ONE word seeded exactly as the twin's is.  Compat is a verification instrument, not the shipping
/// mode.
/// </para>
/// <para>
/// <b>The detail-level law.</b> Nothing in this namespace takes, reads or names a presentation
/// setting — no graphics detail level, no LOD, no resolution.  That is what makes "same inputs ⇒
/// same mission across every presentation setting" a port-only property the emulator twin cannot
/// have (amendment, V3: the detail level still gates seven NON-RNG sim sites in the binary).  A test
/// asserts the law over this namespace's whole API surface and source.
/// </para>
/// </remarks>
public sealed class RandomStreams
{
    private readonly Dictionary<RandomStreamId, SimRandomStream> _sim = [];
    private readonly Dictionary<RandomStreamId, FxRandomStream> _fx = [];

    private RandomStreams(RandomStreamsConfig config)
    {
        Config = config;

        Lfsr16Cell? shared = null;
        if (config.SimMode == SimStreamMode.Compat)
        {
            ushort word = config.SimSeedWord ?? (ushort)config.MasterSeed;
            shared = new Lfsr16Cell { Value = Lfsr16.FromSeed(word) };
        }

        foreach (RandomStreamId id in RandomStreamCatalog.All)
        {
            RandomStreamPolicy policy = config.PolicyFor(id);
            ulong seed = SeedFor(config.MasterSeed, id, policy);

            if (RandomStreamCatalog.IsSim(id))
            {
                Lfsr16Cell cell = shared ?? new Lfsr16Cell { Value = Lfsr16.FromSeed((ushort)seed) };
                _sim[id] = new SimRandomStream(id, cell, policy) { IsShared = shared is not null };
            }
            else
            {
                _fx[id] = new FxRandomStream(id, seed, policy);
            }
        }
    }

    /// <summary>How this set was seeded — recorded verbatim in the replay header.</summary>
    public RandomStreamsConfig Config { get; }

    /// <summary>Creates a stream set from a full configuration.</summary>
    /// <param name="config">The seeding configuration.</param>
    /// <exception cref="ArgumentException">
    /// A <c>Sim.*</c> stream is configured <see cref="RandomStreamPolicy.Free"/> — a decision-side
    /// stream seeded from entropy would make the mission unreplayable, which is the one thing this
    /// kernel exists to prevent.
    /// </exception>
    public static RandomStreams Create(RandomStreamsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        foreach (RandomStreamId id in RandomStreamCatalog.All)
        {
            if (RandomStreamCatalog.IsSim(id) && config.PolicyFor(id) == RandomStreamPolicy.Free)
            {
                throw new ArgumentException(
                    $"{RandomStreamCatalog.Name(id)} is a decision-side stream and must stay Seeded "
                        + "(contract §2.4 proposal D2-b); only Fx.*/Audio.* may run Free.",
                    nameof(config));
            }
        }

        return new RandomStreams(config);
    }

    /// <summary>Creates a split-mode set from a 64-bit master seed — the port's normal path.</summary>
    /// <param name="masterSeed">The replay header's master seed.</param>
    public static RandomStreams FromMasterSeed(ulong masterSeed) =>
        Create(new RandomStreamsConfig { MasterSeed = masterSeed });

    /// <summary>
    /// Creates a set from the binary's single 16-bit mission seed — the value a <c>det_v1</c>
    /// recording carries in its CYEV v7 header.
    /// </summary>
    /// <param name="simSeedWord">The 16-bit seed the twin was forced with.</param>
    /// <param name="mode">Split (default) or Compat (verification).</param>
    public static RandomStreams FromBinarySeed(
        ushort simSeedWord, SimStreamMode mode = SimStreamMode.Split) =>
        Create(new RandomStreamsConfig
        {
            MasterSeed = simSeedWord,
            SimSeedWord = simSeedWord,
            SimMode = mode,
        });

    /// <summary>The 64-bit seed a stream gets from a master seed, before any width reduction.</summary>
    /// <param name="masterSeed">The master seed.</param>
    /// <param name="id">The stream.</param>
    /// <param name="policy">Seeded (derive) or Free (host entropy).</param>
    public static ulong SeedFor(ulong masterSeed, RandomStreamId id, RandomStreamPolicy policy) =>
        policy == RandomStreamPolicy.Free
            ? SplitMix64.Mix(BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)))
            : SplitMix64.Mix(masterSeed ^ Fnv1a64.Hash(RandomStreamCatalog.Name(id)));

    /// <summary>A decision-side stream.</summary>
    /// <param name="id">One of the five <c>Sim.*</c> streams.</param>
    /// <exception cref="ArgumentOutOfRangeException">Not a <c>Sim.*</c> stream.</exception>
    public SimRandomStream Sim(RandomStreamId id) =>
        _sim.TryGetValue(id, out SimRandomStream? stream)
            ? stream
            : throw new ArgumentOutOfRangeException(
                nameof(id), id, "not a Sim.* stream — presentation streams come from Fx().");

    /// <summary>A presentation or audio stream.</summary>
    /// <param name="id">One of the five <c>Fx.*</c> / <c>Audio.*</c> streams.</param>
    /// <exception cref="ArgumentOutOfRangeException">Not a presentation stream.</exception>
    public FxRandomStream Fx(RandomStreamId id) =>
        _fx.TryGetValue(id, out FxRandomStream? stream)
            ? stream
            : throw new ArgumentOutOfRangeException(
                nameof(id), id, "not an Fx.*/Audio.* stream — decision streams come from Sim().");

    /// <summary>Enemy/AI decisions: VM-host FSM, acquisition, evasion, aim error, spawn timing.</summary>
    public SimRandomStream SimAi => _sim[RandomStreamId.SimAi];

    /// <summary>Shot/hit resolution, fire authorisation, cooldowns, projectile spawn, countermeasures.</summary>
    public SimRandomStream SimFire => _sim[RandomStreamId.SimFire];

    /// <summary>Damage amount/severity, structural failure, end-of-flight arming.</summary>
    public SimRandomStream SimDamage => _sim[RandomStreamId.SimDamage];

    /// <summary>Mission/scenario generation — the custom-mission builder and load-time placement.</summary>
    public SimRandomStream SimMission => _sim[RandomStreamId.SimMission];

    /// <summary>Other gameplay/world state: seeding, list ordering, ejection, the cloud deck.</summary>
    public SimRandomStream SimOther => _sim[RandomStreamId.SimOther];

    /// <summary>Explosion puffs, debris, countermeasure tumble, effect sprites.</summary>
    public FxRandomStream FxParticles => _fx[RandomStreamId.FxParticles];

    /// <summary>HUD markers, cockpit text, radio chatter, advisor messages, dial phases.</summary>
    public FxRandomStream FxIndicators => _fx[RandomStreamId.FxIndicators];

    /// <summary>Front-end and post-mission text and modals.</summary>
    public FxRandomStream FxOther => _fx[RandomStreamId.FxOther];

    /// <summary>Pitch / volume / duration perturbation of a sound.</summary>
    public FxRandomStream AudioJitter => _fx[RandomStreamId.AudioJitter];

    /// <summary>Which sound or tune is played.</summary>
    public FxRandomStream AudioVoice => _fx[RandomStreamId.AudioVoice];

    /// <summary>Captures every stream's state.</summary>
    public RandomStreamsState Snapshot()
    {
        List<RandomStreamSnapshot> snapshots = new List<RandomStreamSnapshot>(RandomStreamCatalog.All.Count);
        foreach (RandomStreamId id in RandomStreamCatalog.All)
        {
            string name = RandomStreamCatalog.Name(id);
            if (RandomStreamCatalog.IsSim(id))
            {
                SimRandomStream stream = _sim[id];
                snapshots.Add(new RandomStreamSnapshot(
                    name, RandomStreamKind.Lfsr16, stream.State, 0, 0, 0, 0, stream.BitsDrawn));
            }
            else
            {
                FxRandomStream stream = _fx[id];
                (ulong s0, ulong s1, ulong s2, ulong s3) = stream.State;
                snapshots.Add(new RandomStreamSnapshot(
                    name, RandomStreamKind.Xoshiro256StarStar, 0, s0, s1, s2, s3,
                    (long)stream.DrawCount));
            }
        }

        return new RandomStreamsState(Config.SimMode, snapshots);
    }

    /// <summary>Restores a state captured by <see cref="Snapshot"/>.</summary>
    /// <param name="state">The captured state.</param>
    /// <exception cref="ArgumentException">
    /// The snapshot was taken in the other <see cref="SimStreamMode"/>, names a stream this set does
    /// not have, omits one, or (in Compat) disagrees with itself about the shared word.
    /// </exception>
    public void Restore(RandomStreamsState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SimMode != Config.SimMode)
        {
            throw new ArgumentException(
                $"checkpoint was taken in {state.SimMode} mode, this set runs {Config.SimMode}; "
                    + "a Sim stream-mode change is a kernel-era bump (contract §4.4).",
                nameof(state));
        }

        if (state.Streams.Count != RandomStreamCatalog.All.Count)
        {
            throw new ArgumentException(
                $"checkpoint carries {state.Streams.Count} stream(s), this set has "
                    + $"{RandomStreamCatalog.All.Count}.",
                nameof(state));
        }

        ushort? sharedWord = null;
        foreach (RandomStreamSnapshot snapshot in state.Streams)
        {
            RandomStreamId id = RandomStreamCatalog.Parse(snapshot.Stream);
            if (RandomStreamCatalog.IsSim(id))
            {
                if (Config.SimMode == SimStreamMode.Compat)
                {
                    if (sharedWord is { } word && word != snapshot.Word)
                    {
                        throw new ArgumentException(
                            "a Compat checkpoint must carry ONE Sim word; this one disagrees with "
                                + $"itself ({word:X4} vs {snapshot.Word:X4}).",
                            nameof(state));
                    }

                    sharedWord = snapshot.Word;
                }

                _sim[id].Cell.Value = Lfsr16.FromRawState(snapshot.Word);
                _sim[id].Cell.BitsDrawn = snapshot.Draws;
            }
            else
            {
                _fx[id].Generator = Xoshiro256StarStar.FromState(
                    snapshot.S0, snapshot.S1, snapshot.S2, snapshot.S3, (ulong)snapshot.Draws);
            }
        }
    }
}
