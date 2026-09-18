namespace CYAC.Formats.EaLib;

// ---------------------------------------------------------------------------
// strings2.bin — the COPY-PROTECTION answer table (1b.lib idx 13, LZSS,
// 1,611 B stored -> 5,532 B decompressed).  Round 7c found the asset and read
// its manual cross-references; the decompressed body is an ordinary
// <see cref="StringsBinCodec"/> string table, and its INDEXING is fixed by
// copy_protection_prompt @image@0x2529C:
//
//   * the question index is drawn with prng_rand_bounded(0x66) => r in 0..101
//   (102 questions — KNOWN_FUNCTIONS[0x2529C]);
//   * question r = string 2r+1, answer r = string 2r+2 (the NUL walker
//     cp_table_lookup_by_index @image@0x25186 returns the n-th string);
//   * string 0 is the failure message the prompt prints after a wrong answer.
//
// 1 + 2*102 = 205 strings, which is exactly what the shipping asset decompresses
// to (205 was predicted from the index table before the LZSS codec was
// known — CONFIRMED here, and the strings are plain text, so §4.3's
// "strip-hi" reconstruction is superseded).
//
// The port never asks the question (there is no disk to check), but the table is
// part of the game's knowledge: it is a 102-entry aircraft-specification quiz
// drawn from the manual (pp.129-158).
// ---------------------------------------------------------------------------

/// <summary>One copy-protection challenge: a manual-lookup question and its expected answer.</summary>
/// <param name="Index">The challenge number <c>r</c>, 0..101 — what <c>prng_rand_bounded(0x66)</c> draws.</param>
/// <param name="Question">The prompt text (string <c>2r+1</c>).</param>
/// <param name="Answer">The expected answer (string <c>2r+2</c>), compared case-insensitively.</param>
public sealed record CpChallenge(int Index, string Question, string Answer);

/// <summary>
/// The parsed <c>strings2.bin</c>: the failure message plus the 102 (question, answer) pairs.
/// </summary>
/// <param name="FailureMessage">String 0 — printed after a wrong answer.</param>
/// <param name="Challenges">The challenges in index order.</param>
/// <param name="PadNulCount">The container's NUL padding, carried so the round trip closes.</param>
public sealed record CpAnswerTable(
    string FailureMessage,
    IReadOnlyList<CpChallenge> Challenges,
    int PadNulCount);

/// <summary>
/// Reads and re-emits the copy-protection answer table (see the file header for the
/// layout and its citations).
/// </summary>
public static class CpAnswerTableCodec
{
    /// <summary>
    /// Challenges the prompt can draw: 102 (<c>prng_rand_bounded(0x66)</c>, image@0x2529C).
    /// </summary>
    public const int ShippingChallengeCount = 0x66;

    /// <summary>The asset's name in <c>1b.lib</c> (DGROUP literal at image@0x3F182).</summary>
    public const string AssetName = "strings2.bin";

    /// <summary>Whether an asset name is the copy-protection table.</summary>
    /// <param name="name">An EALIB member name.</param>
    public static bool IsAnswerTable(string name) =>
        string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a decompressed <c>strings2.bin</c> body.</summary>
    /// <param name="body">The decompressed asset body.</param>
    /// <exception cref="InvalidDataException">The body is not a string table, or its string count is not <c>1 + 2n</c>.</exception>
    public static CpAnswerTable Parse(ReadOnlySpan<byte> body)
    {
        StringsBinFile table = StringsBinCodec.Parse(body);
        if (table.Strings.Count < 1 || table.Strings.Count % 2 == 0)
        {
            throw new InvalidDataException(
                $"strings2.bin holds {table.Strings.Count} strings; the prompt's indexing " +
                "(question = 2r+1, answer = 2r+2, image@0x2529C) needs 1 + 2n");
        }

        int count = (table.Strings.Count - 1) / 2;
        List<CpChallenge> challenges = new List<CpChallenge>(count);
        for (int r = 0; r < count; r++)
        {
            challenges.Add(new CpChallenge(r, table.Strings[(2 * r) + 1], table.Strings[(2 * r) + 2]));
        }

        return new CpAnswerTable(table.Strings[0], challenges, table.PadNulCount);
    }

    /// <summary>Rebuilds the decompressed body from the model.</summary>
    /// <param name="table">The (possibly edited) answer table.</param>
    public static byte[] ToBytes(CpAnswerTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        List<string> strings = new List<string>((table.Challenges.Count * 2) + 1) { table.FailureMessage };
        foreach (CpChallenge challenge in table.Challenges)
        {
            strings.Add(challenge.Question);
            strings.Add(challenge.Answer);
        }

        return StringsBinCodec.ToBytes(
            new StringsBinFile(strings, table.PadNulCount, 0, 0));
    }
}
