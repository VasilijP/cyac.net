namespace CYAC.Port.Core.Sim;

/// <summary>
/// A presentation or audio (<c>Fx.*</c> / <c>Audio.*</c>) random stream: a 64-bit
/// <see cref="Xoshiro256StarStar"/>, independently seeded, whose draws can never reach a decision.
/// </summary>
/// <remarks>
/// <para>
/// FLOAT-only.  Existence of this class is what closes the T8 finding by construction: in the
/// shipped game the explosion-puff triangle orientations at <c>effect_particle_draw
/// @image@0x03D81</c> come out of the SAME word the AI VM and hit determination use, so the
/// graphics-detail setting changes combat outcomes.  Here they come out of <c>Fx.Particles</c>, and
/// disabling effects, changing LOD, or replaying without sound moves nothing on the <c>Sim.*</c>
/// side (human DIRECTION §9.2, "replay without sound").
/// </para>
/// <para>
/// The generator is wider than the original's on purpose (D2: "higher bit width welcome") — these
/// streams never have to agree with the twin bit for bit, only with themselves.
/// </para>
/// </remarks>
public sealed class FxRandomStream
{
    private Xoshiro256StarStar _generator;

    internal FxRandomStream(RandomStreamId id, ulong seed, RandomStreamPolicy policy)
    {
        Id = id;
        Name = RandomStreamCatalog.Name(id);
        Policy = policy;
        _generator = Xoshiro256StarStar.FromSeed(seed);
    }

    /// <summary>Which stream this is.</summary>
    public RandomStreamId Id { get; }

    /// <summary>The canonical name — the string the stream's seed is derived from.</summary>
    public string Name { get; }

    /// <summary>This stream's determinism policy.</summary>
    public RandomStreamPolicy Policy { get; }

    /// <summary>The four live state words.</summary>
    public (ulong S0, ulong S1, ulong S2, ulong S3) State => _generator.State;

    /// <summary>How many 64-bit draws this stream has produced.</summary>
    public ulong DrawCount => _generator.DrawCount;

    /// <summary>Re-seeds the stream from a 64-bit value.</summary>
    /// <param name="seed">Any 64-bit value.</param>
    public void Seed(ulong seed) => _generator = Xoshiro256StarStar.FromSeed(seed);

    /// <summary>Draws the next 64 bits.</summary>
    public ulong NextUInt64() => _generator.NextUInt64();

    /// <summary>Draws the next 32 bits.</summary>
    public uint NextUInt32() => _generator.NextUInt32();

    /// <summary>A uniform draw in <c>[0, bound)</c>, unbiased by rejection.</summary>
    /// <param name="bound">Exclusive upper bound; must be positive.</param>
    public ulong NextBounded(ulong bound) => _generator.NextBounded(bound);

    /// <summary>A uniform double in <c>[0, 1)</c>.</summary>
    public double NextDouble() => _generator.NextDouble();

    internal Xoshiro256StarStar Generator
    {
        get => _generator;
        set => _generator = value;
    }
}
