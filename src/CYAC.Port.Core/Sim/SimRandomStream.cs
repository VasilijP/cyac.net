using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim;

/// <summary>
/// The one mutable LFSR16 word a set of <see cref="SimRandomStream"/>s draws from.
/// </summary>
/// <remarks>
/// A cell rather than a field so <see cref="SimStreamMode.Compat"/> can point all five
/// <c>Sim.*</c> streams at the SAME word — which is what the binary has ("one stream, one
/// word of state") and therefore what emulator-vs-port verification needs.
/// </remarks>
internal sealed class Lfsr16Cell
{
    public Lfsr16 Value;

    /// <summary>Total bits drawn from this word, across every stream that shares it.</summary>
    public long BitsDrawn;
}

/// <summary>
/// A decision-side (<c>Sim.*</c>) random stream: the shipped 16-bit Galois LFSR, with the shipped
/// draw semantics, exactly.
/// </summary>
/// <remarks>
/// <para>
/// INT-only (F7).  Every method here is a thin, deliberate forward to <see cref="Lfsr16"/>, which is
/// the verified transliteration of the original's generator family (<c>prng_lfsr_step
/// @image@0x19EC6</c>, <c>prng_rand16 @0x19EFE</c>, <c>prng_rand8 @0x19F06</c>, <c>prng_rand_bounded
/// @0x19F0E</c>, <c>prng_rand_centered_n16 @0x28362</c>, <c>prng_seed_or_force1 @0x19EB6</c>) —
/// behaviourally verified against the original's bytes by
/// <c>src/CYAC.Tools.SlrUnpacker/DiffTest.Batch5.cs:187-277</c>.  The wrapper adds a name, a seed
/// policy and a bit counter; it adds no arithmetic, so the existing <c>Lfsr16</c> tests extend to it.
/// </para>
/// <para>
/// <b>Why not a wider generator.</b> Amendment A2: <c>prng_rand_bounded</c>'s rejection loop consumes
/// a <i>variable</i> number of bits, so the number of bits every later draw consumes is
/// algorithm-dependent.  Swap the generator and the port's decisions can no longer be verified
/// against the patched twin — which is the whole reason the emulator survives as a reference
/// instrument.  A <c>Sim.*</c> generator upgrade is a new kernel era (X2), not a refactor.
/// </para>
/// </remarks>
public sealed class SimRandomStream
{
    private readonly Lfsr16Cell _cell;

    internal SimRandomStream(RandomStreamId id, Lfsr16Cell cell, RandomStreamPolicy policy)
    {
        Id = id;
        Name = RandomStreamCatalog.Name(id);
        Policy = policy;
        _cell = cell;
    }

    /// <summary>Which stream this is.</summary>
    public RandomStreamId Id { get; }

    /// <summary>The canonical name — the string the stream's seed is derived from.</summary>
    public string Name { get; }

    /// <summary>This stream's determinism policy.</summary>
    public RandomStreamPolicy Policy { get; }

    /// <summary>The live 16-bit state word — the port's <c>g_prng_state_lo [0x07A8]</c> equivalent.</summary>
    public ushort State => _cell.Value.State;

    /// <summary>
    /// How many LFSR bits this stream has drawn.  Under <see cref="SimStreamMode.Compat"/> the five
    /// <c>Sim.*</c> streams share a word, so this counts the shared word's total.
    /// </summary>
    public long BitsDrawn => _cell.BitsDrawn;

    /// <summary>True when this stream shares its state word with the other <c>Sim.*</c> streams.</summary>
    public bool IsShared { get; internal init; }

    /// <summary>Installs a seed through the original's force-to-1 defence (<c>prng_seed_or_force1</c>).</summary>
    /// <param name="seed">The seed word; 0 becomes 1, because 0 is the LFSR's absorbing state.</param>
    public void Seed(ushort seed) => _cell.Value.Seed(seed);

    /// <summary>Draws <paramref name="bits"/> bits, MSB-first — <c>prng_lfsr_step @image@0x19EC6</c>.</summary>
    /// <param name="bits">How many bits to draw, 0..65535.</param>
    public ushort Step(int bits)
    {
        ushort result = _cell.Value.Step(bits);
        _cell.BitsDrawn += bits;
        return result;
    }

    /// <summary>A full 16-bit draw — <c>prng_rand16 @image@0x19EFE</c>.</summary>
    public ushort Rand16() => Step(16);

    /// <summary>An 8-bit draw in <c>[0, 255]</c> — <c>prng_rand8 @image@0x19F06</c>.</summary>
    public ushort Rand8() => Step(8);

    /// <summary>
    /// A uniform draw in <c>[0, bound)</c> — <c>prng_rand_bounded @image@0x19F0E</c>: smallest
    /// covering power of two plus rejection, never a modulo.
    /// </summary>
    /// <param name="bound">Exclusive upper bound, 0..32768.</param>
    public ushort RandBounded(ushort bound)
    {
        // Re-run the original's k search here so the bit counter sees every REDRAW, which a plain
        // forward to Lfsr16.RandBounded could not report (the loop lives inside it).
        if (bound == 0)
        {
            return 0;                                               // image@0x19F1D/0x19F1F
        }

        int k = BitsFor(bound);
        ushort result;
        do
        {
            result = Step(k);                                       // image@0x19F30/0x19F32
        }
        while (result >= bound);                                    // image@0x19F35/0x19F37

        return result;
    }

    /// <summary>
    /// A draw centred on zero, uniform in <c>[-n·8, n·8)</c> — <c>prng_rand_centered_n16 @image@0x28362</c>.
    /// </summary>
    /// <param name="n">The range multiplier; the draw spans <c>n·16</c> values.</param>
    public short RandCenteredN16(short n)
    {
        short half = unchecked((short)(n * 8));                     // image@0x28368
        ushort bound = unchecked((ushort)(half * 2));               // image@0x2836E
        ushort raw = RandBounded(bound);                            // image@0x28370
        return unchecked((short)(raw - half));                      // image@0x28375
    }

    /// <summary>
    /// How many bits one attempt of <see cref="RandBounded"/> consumes: the least <c>k &gt;= 1</c>
    /// with <c>2^k &gt;= bound</c> (<c>image@0x19F17..0x19F2B</c>).  A rejected draw spends another
    /// <c>k</c>, which is why the total is variable — and why substituting a modulo desynchronises a
    /// replay rather than merely skewing a distribution.
    /// </summary>
    /// <param name="bound">Exclusive upper bound, 1..32768.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bound"/> is 0 or above 0x8000 — above 0x8000 the original's 16-bit
    /// power-of-two scratch wraps and its search loop never exits (a dormant hang; no observed caller
    /// passes a bound above 0x66).
    /// </exception>
    public static int BitsFor(ushort bound)
    {
        ArgumentOutOfRangeException.ThrowIfZero(bound);
        if (bound > 0x8000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bound),
                bound,
                "prng_rand_bounded (image@0x19F25) hangs for bounds above 0x8000.");
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

        return k;
    }

    internal Lfsr16Cell Cell => _cell;
}
