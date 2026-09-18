using System.Text;

namespace CYAC.Port.Core.Sim;

/// <summary>
/// The <c>splitmix64</c> seed sequence (<c>platform</c> — the public-domain reference generator by
/// Steele/Lea/Flood, published as the companion seeder of the xoshiro/xoroshiro family and
/// implemented here from that specification).
/// </summary>
/// <remarks>
/// Its job in the port is <b>seed derivation only</b> (D2-mech): every stream's seed is
/// <c>splitmix64(master ^ fnv1a64(stream_name))</c>, so adding a stream never re-seeds the others and
/// a single stream can be re-seeded alone.  It is deliberately not one of the drawing generators.
/// </remarks>
public static class SplitMix64
{
    /// <summary>The golden-ratio increment <c>0x9E3779B97F4A7C15</c>.</summary>
    public const ulong Gamma = 0x9E37_79B9_7F4A_7C15;

    private const ulong Multiplier1 = 0xBF58_476D_1CE4_E5B9;
    private const ulong Multiplier2 = 0x94D0_49BB_1331_11EB;

    /// <summary>Advances a splitmix64 state and returns its output.</summary>
    /// <param name="state">The 64-bit state, advanced in place.</param>
    public static ulong Next(ref ulong state)
    {
        unchecked
        {
            ulong z = state += Gamma;
            z = (z ^ (z >> 30)) * Multiplier1;
            z = (z ^ (z >> 27)) * Multiplier2;
            return z ^ (z >> 31);
        }
    }

    /// <summary>
    /// One splitmix64 output derived from <paramref name="value"/> — i.e. the first draw of a
    /// generator seeded with it.  This is what "<c>splitmix64(x)</c>" means throughout the contract.
    /// </summary>
    /// <param name="value">The value to mix.</param>
    public static ulong Mix(ulong value)
    {
        ulong state = value;
        return Next(ref state);
    }
}

/// <summary>
/// FNV-1a, 64-bit (<c>platform</c> — Fowler/Noll/Vo, public domain), over a name's UTF-8 bytes.
/// </summary>
/// <remarks>
/// Used for exactly one thing: turning a stream's NAME into the value that is XORed into the master
/// seed.  Naming the streams rather than numbering them is what makes the seeding stable when the
/// stream set grows — a new stream changes no existing stream's seed.
/// </remarks>
public static class Fnv1a64
{
    /// <summary>The 64-bit offset basis, <c>0xCBF29CE484222325</c>.</summary>
    public const ulong OffsetBasis = 0xCBF2_9CE4_8422_2325;

    /// <summary>The 64-bit prime, <c>0x100000001B3</c>.</summary>
    public const ulong Prime = 0x0000_0100_0000_01B3;

    /// <summary>Hashes a string's UTF-8 bytes.</summary>
    /// <param name="text">The text to hash.</param>
    public static ulong Hash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Hash(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Hashes a byte span.</summary>
    /// <param name="bytes">The bytes to hash.</param>
    public static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        unchecked
        {
            ulong hash = OffsetBasis;
            foreach (byte b in bytes)
            {
                hash ^= b;
                hash *= Prime;
            }

            return hash;
        }
    }
}
