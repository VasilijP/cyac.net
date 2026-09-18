namespace CYAC.Port.Transform.Transform;

/// <summary>
/// Law-L4 support for families whose model covers a body field by field: diff the model's own
/// re-emission against the original and keep whatever differs, so a byte nobody named still survives
/// the round trip — named as a residue span and counted.
/// </summary>
/// <remarks>
/// This is the honest form of "the padding is always zero": the transform asserts nothing, it
/// measures.  On the shipping catalogs every diff comes back empty, which is the interesting result;
/// a foreign or modded asset with a surprise byte round-trips anyway and shows up in the burn-down.
/// </remarks>
public static class ByteResidue
{
    /// <summary>
    /// The maximal runs where <paramref name="rebuilt"/> differs from <paramref name="original"/>.
    /// </summary>
    /// <param name="original">The bytes that must come back.</param>
    /// <param name="rebuilt">What the model re-emits.</param>
    /// <exception cref="InvalidDataException">The two lengths differ — that is a model bug, not a residue.</exception>
    public static IReadOnlyList<(int Offset, byte[] Bytes)> Diff(
        ReadOnlySpan<byte> original, ReadOnlySpan<byte> rebuilt)
    {
        if (original.Length != rebuilt.Length)
        {
            throw new InvalidDataException(
                $"the model re-emits {rebuilt.Length} B for a {original.Length} B asset; " +
                "a residue can only patch bytes, not resize the file");
        }

        List<(int Offset, byte[] Bytes)> spans = new List<(int Offset, byte[] Bytes)>();
        int start = -1;
        for (int i = 0; i <= original.Length; i++)
        {
            bool differs = i < original.Length && original[i] != rebuilt[i];
            if (differs && start < 0)
            {
                start = i;
            }
            else if (!differs && start >= 0)
            {
                spans.Add((start, original[start..i].ToArray()));
                start = -1;
            }
        }

        return spans;
    }

    /// <summary>Total bytes in a residue list — the law-L4 count.</summary>
    /// <param name="spans">The residue spans.</param>
    public static int Count(IReadOnlyList<(int Offset, byte[] Bytes)> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        int total = 0;
        foreach ((int _, byte[] bytes) in spans)
        {
            total += bytes.Length;
        }

        return total;
    }

    /// <summary>Writes residue spans back over a re-emitted body.</summary>
    /// <param name="body">The re-emitted body, modified in place.</param>
    /// <param name="spans">The spans to restore.</param>
    /// <exception cref="InvalidDataException">A span lies outside the body.</exception>
    public static void Apply(Span<byte> body, IEnumerable<(int Offset, byte[] Bytes)> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        foreach ((int offset, byte[] bytes) in spans)
        {
            if (offset < 0 || offset + bytes.Length > body.Length)
            {
                throw new InvalidDataException(
                    $"residue span at 0x{offset:X} ({bytes.Length} B) lies outside the {body.Length} B body");
            }

            bytes.CopyTo(body[offset..]);
        }
    }
}
