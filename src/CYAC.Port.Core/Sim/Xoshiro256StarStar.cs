using System.Numerics;

namespace CYAC.Port.Core.Sim;

/// <summary>
/// <c>xoshiro256**</c> — the 64-bit generator the port's presentation and audio streams draw from
/// (<c>platform</c>: the public-domain reference algorithm by Blackman and Vigna, implemented here
/// from its specification).
/// </summary>
/// <remarks>
/// <para>
/// FLOAT-only by field class.  It is used for <c>Fx.*</c> and <c>Audio.*</c> only — never for a
/// decision.  The <c>Sim.*</c> streams keep the shipped <see cref="Primitives.Lfsr16"/> instead
/// (amendment A2), because the original's rejection sampler makes the number of bits a draw consumes
/// algorithm-dependent, and the port's decisions have to stay verifiable against the patched
/// emulator twin.
/// </para>
/// <para>
/// The step, verbatim from the reference: <c>result = rotl(s1 * 5, 7) * 9</c>; then
/// <c>t = s1 &lt;&lt; 17</c>, <c>s2 ^= s0</c>, <c>s3 ^= s1</c>, <c>s1 ^= s2</c>, <c>s0 ^= s3</c>,
/// <c>s2 ^= t</c>, <c>s3 = rotl(s3, 45)</c>.  Period 2^256 − 1; the all-zero state is a fixed point,
/// which <see cref="FromSeed"/> cannot produce and <see cref="FromState"/> refuses.
/// </para>
/// </remarks>
public struct Xoshiro256StarStar
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    private Xoshiro256StarStar(ulong s0, ulong s1, ulong s2, ulong s3)
    {
        _s0 = s0;
        _s1 = s1;
        _s2 = s2;
        _s3 = s3;
    }

    /// <summary>The four state words, in order.</summary>
    public (ulong S0, ulong S1, ulong S2, ulong S3) State => (_s0, _s1, _s2, _s3);

    /// <summary>How many 64-bit draws this generator has produced since it was seeded.</summary>
    public ulong DrawCount { get; private set; }

    /// <summary>
    /// Seeds the generator from a single 64-bit value by filling the four state words with
    /// consecutive <see cref="SplitMix64"/> outputs — the seeding procedure the reference
    /// implementation prescribes.
    /// </summary>
    /// <param name="seed">Any 64-bit value, including 0.</param>
    public static Xoshiro256StarStar FromSeed(ulong seed)
    {
        ulong state = seed;
        ulong s0 = SplitMix64.Next(ref state);
        ulong s1 = SplitMix64.Next(ref state);
        ulong s2 = SplitMix64.Next(ref state);
        ulong s3 = SplitMix64.Next(ref state);
        if ((s0 | s1 | s2 | s3) == 0)
        {
            s0 = 1;                     // unreachable for splitmix64 output, defended anyway
        }

        return new Xoshiro256StarStar(s0, s1, s2, s3);
    }

    /// <summary>Restores an exact state — for replay checkpoints.</summary>
    /// <param name="s0">State word 0.</param>
    /// <param name="s1">State word 1.</param>
    /// <param name="s2">State word 2.</param>
    /// <param name="s3">State word 3.</param>
    /// <param name="drawCount">The draw counter to restore alongside it.</param>
    /// <exception cref="ArgumentException">All four words are zero — the generator's fixed point.</exception>
    public static Xoshiro256StarStar FromState(ulong s0, ulong s1, ulong s2, ulong s3, ulong drawCount = 0)
    {
        if ((s0 | s1 | s2 | s3) == 0)
        {
            throw new ArgumentException(
                "the all-zero state is xoshiro256**'s fixed point and produces only zeros.",
                nameof(s0));
        }

        return new Xoshiro256StarStar(s0, s1, s2, s3) { DrawCount = drawCount };
    }

    /// <summary>Draws the next 64 bits.</summary>
    public ulong NextUInt64()
    {
        unchecked
        {
            ulong result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
            ulong t = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = BitOperations.RotateLeft(_s3, 45);
            DrawCount++;
            return result;
        }
    }

    /// <summary>Draws the next 32 bits (the high half of a 64-bit draw — the reference's advice).</summary>
    public uint NextUInt32() => (uint)(NextUInt64() >> 32);

    /// <summary>
    /// A uniform draw in <c>[0, bound)</c>, unbiased by rejection (never a bare modulo).
    /// </summary>
    /// <param name="bound">Exclusive upper bound; must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bound"/> is 0.</exception>
    public ulong NextBounded(ulong bound)
    {
        ArgumentOutOfRangeException.ThrowIfZero(bound);

        // 2^64 mod bound, computed without 128-bit arithmetic: (0 - bound) mod bound.
        ulong reject = unchecked(0UL - bound) % bound;
        ulong x;
        do
        {
            x = NextUInt64();
        }
        while (x < reject);

        return x % bound;
    }

    /// <summary>A uniform double in <c>[0, 1)</c> with 53 significant bits — the reference recipe.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));
}
