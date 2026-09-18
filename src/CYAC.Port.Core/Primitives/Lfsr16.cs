namespace CYAC.Port.Core.Primitives;

/// <summary>
/// The game's random number generator: a 16-bit Galois LFSR with tap mask <c>0xB400</c>.
/// </summary>
/// <remarks>
/// <para>
/// INT-only and the single most reproduction-critical primitive in the port: film playback and
/// mission reproducibility both depend on drawing the same bits in the same order. Never substitute a
/// host RNG, and never replace <see cref="RandBounded"/>'s rejection loop with a modulo.
/// </para>
/// <para>
/// Source of truth: (<c>prng_lfsr_step @image@0x19EC6</c>, state <c>g_prng_state_lo [0x07A8]</c>),
/// (<c>prng_rand_bounded @image@0x19F0E</c>), <c>prng_seed_or_force1 @image@0x19EB6</c>,
/// <c>prng_rand16 @image@0x19EFE</c>, <c>prng_rand8 @image@0x19F06</c>,.  Behaviourally verified
/// against the original's bytes by <c>src/CYAC.Tools.SlrUnpacker/DiffTest.Batch5.cs:187-277</c>:
/// exhaustive single-step over all 65536 states, n-bit draws for n = 0..16, measured period 65535
/// (maximal length), and zero-state absorption.
/// </para>
/// <para>
/// Per bit: eject the LSB as the output bit, shift the state right, and — if the ejected bit was 1 —
/// XOR the tap mask into the state.  The original XORs the byte constant <c>0xB4</c> into the state's
/// HIGH byte (<c>XOR byte ptr [0x7A9],0xB4</c> at <c>image@0x19EDF</c>), which as a word operation is
/// <c>0xB400</c>.  Output bits accumulate MSB-first in a 16-bit register.
/// </para>
/// <para>
/// This is a mutable struct because the original's state is one mutable global word; copy it and you
/// fork the stream, which is occasionally what you want (speculative rolls) and usually not.
/// </para>
/// </remarks>
public struct Lfsr16
{
    /// <summary>The Galois feedback mask: <c>0xB400</c> — taps at word bits 15, 13, 12 and 10.</summary>
    public const ushort Taps = 0xB400;

    /// <summary>The generator's period from any non-zero state: 65535 = 2^16 − 1 (maximal length).</summary>
    public const int Period = 65535;

    /// <summary>Creates a generator seeded through the original's force-to-1 defence.</summary>
    /// <param name="seed">The seed word; 0 is replaced by 1.</param>
    public Lfsr16(ushort seed) => State = seed == 0 ? (ushort)1 : seed;

    /// <summary>The live 16-bit state word — the port's <c>g_prng_state_lo [0x07A8]</c>.</summary>
    public ushort State { get; private set; }

    /// <summary>Creates a generator seeded through the original's force-to-1 defence.</summary>
    public static Lfsr16 FromSeed(ushort seed) => new(seed);

    /// <summary>
    /// Creates a generator with an exact state word, bypassing the force-to-1 defence.
    /// </summary>
    /// <remarks>
    /// The original has no such entry point — every seed goes through
    /// <c>prng_seed_or_force1 @image@0x19EB6</c>.  This exists to model the raw state word itself:
    /// state 0 is ABSORBING (it can never leave, and every draw is 0), which is exactly the failure
    /// the force-to-1 defence exists to prevent, and which the port must be able to reproduce and
    /// test rather than pretend away.
    /// </remarks>
    public static Lfsr16 FromRawState(ushort state) => new() { State = state };

    /// <summary>
    /// Installs a seed with the original's defence: a zero seed becomes 1, because 0 is the LFSR's
    /// absorbing state.  <c>prng_seed_or_force1 @image@0x19EB6</c>.
    /// </summary>
    public void Seed(ushort seed) => State = seed == 0 ? (ushort)1 : seed;

    /// <summary>
    /// Advances the LFSR <paramref name="bits"/> times and returns the ejected bits, MSB-first.
    /// The original <c>prng_lfsr_step @image@0x19EC6</c>.
    /// </summary>
    /// <remarks>
    /// The accumulator is a 16-bit register (<c>DI</c>), so for <paramref name="bits"/> &gt; 16 the
    /// early bits shift out; <c>bits = 0</c> advances nothing and returns 0.  The original's loop
    /// counter is a word tested before each decrement, so its domain is <c>[0, 65535]</c> — a
    /// "negative" count of −1 means 65535 iterations, not zero.
    /// </remarks>
    /// <param name="bits">How many bits to draw, 0..65535.</param>
    public ushort Step(int bits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bits, 0xFFFF);

        ushort state = State;
        ushort result = 0;
        for (int i = 0; i < bits; i++)
        {
            int bit = state & 1;                                    // image@0x19ED4
            state = (ushort)(state >> 1);                           // image@0x19ED7
            if (bit != 0)
            {
                state ^= Taps;                                      // image@0x19EDF
            }

            result = unchecked((ushort)((result << 1) | bit));      // image@0x19EE4..0x19EEA
        }

        State = state;
        return result;
    }

    /// <summary>A full 16-bit draw — <c>prng_rand16 @image@0x19EFE</c> (<c>prng_lfsr_step(16)</c>).</summary>
    public ushort Rand16() => Step(16);

    /// <summary>An 8-bit draw in <c>[0, 255]</c> — <c>prng_rand8 @image@0x19F06</c> (<c>prng_lfsr_step(8)</c>).</summary>
    public ushort Rand8() => Step(8);

    /// <summary>
    /// A uniform draw in <c>[0, bound)</c> — <c>prng_rand_bounded @image@0x19F0E</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Smallest covering power of two plus rejection, NOT modulo: find the least
    /// <c>k &gt;= 1</c> with <c>2^k &gt;= bound</c>, draw <c>k</c> bits, and redraw while the result
    /// is <c>&gt;= bound</c>.  That is unbiased, and it consumes a variable number of LFSR bits — so
    /// substituting a modulo would desynchronise every later draw in a replay, not merely skew the
    /// distribution.
    /// </para>
    /// <para>
    /// <c>bound = 0</c> returns 0 (the original returns its untouched argument).  <c>k</c> is at
    /// least 1 even for bounds 1 and 2 — bound 1 therefore rerolls single bits until it draws a 0.
    /// </para>
    /// </remarks>
    /// <param name="bound">Exclusive upper bound, 0..32768.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bound"/> exceeds 32768.  The original's <c>SHL DI,1</c> wraps its 16-bit
    /// power-of-two scratch to 0 at that point and the search loop never terminates — a dormant hang
    /// (no observed caller passes a bound above 0x66).  The port refuses rather than hanging.
    /// </exception>
    public ushort RandBounded(ushort bound)
    {
        if (bound == 0)
        {
            return 0;                                               // image@0x19F1D/0x19F1F
        }

        if (bound > 0x8000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bound),
                bound,
                "prng_rand_bounded (image@0x19F25) hangs for bounds above 0x8000: its 16-bit "
                    + "power-of-two scratch wraps to 0 and the covering-power search never exits.");
        }

        int k = 1;                                                  // image@0x19F17
        int pow = 2;                                                // image@0x19F1A
        if (bound > 2)                                              // image@0x19F21/0x19F23
        {
            do
            {
                k++;                                                // image@0x19F25
                pow <<= 1;                                          // image@0x19F26
            }
            while (pow < bound);                                    // image@0x19F28/0x19F2B
        }

        ushort result;
        do
        {
            result = Step(k);                                       // image@0x19F30/0x19F32
        }
        while (result >= bound);                                    // image@0x19F35/0x19F37

        return result;
    }

    /// <summary>
    /// A draw centred on zero: uniform in <c>[-n·8, n·8)</c> — the original
    /// <c>prng_rand_centered_n16 @image@0x28362</c>, used for custom-mission spawn jitter.
    /// </summary>
    /// <remarks>
    /// <c>n</c> is shifted left by 3 to make the half-width, doubled to make the bound, and the draw
    /// has the half-width subtracted (<c>image@0x28368</c> / <c>0x2836E</c> / <c>0x28375</c>).  All
    /// three steps are 16-bit register arithmetic and wrap accordingly.  Observed callers pass
    /// n = 10, 25, 30 and 40 — widths of ±80 to ±320 world units.
    /// </remarks>
    /// <param name="n">The range multiplier; the draw spans <c>n·16</c> values.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <c>n·16</c> exceeds 32768 as a 16-bit word (which includes every negative <paramref name="n"/>) —
    /// see <see cref="RandBounded"/>.
    /// </exception>
    public short RandCenteredN16(short n)
    {
        short half = unchecked((short)(n * 8));                     // image@0x28368
        ushort bound = unchecked((ushort)(half * 2));               // image@0x2836E
        ushort raw = RandBounded(bound);                            // image@0x28370
        return unchecked((short)(raw - half));                      // image@0x28375
    }
}
