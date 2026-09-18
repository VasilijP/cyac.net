using System.Buffers.Binary;
using System.Text;

namespace CYAC.Formats.EaLib;

// ---------------------------------------------------------------------------
// The NUL-SEPARATED STRING TABLE container shared by `strings.bin` (2a.lib, the
// UI string catalog) and `strings2.bin` (1b.lib, the copy-protection answer table).  Both are LZSS members (flag 0x01); this codec works
// on the DECOMPRESSED body.
//
// LAYOUT (structural, derived — not a guess; proven on both shipping files by
// StringsBinCodecTests):
//
//   +0x0000  string 0, NUL-terminated
//            string i (i >= 1) starts immediately after string (i-1)'s NUL
//   ...      the body ends with the last string's NUL
//   pad      NUL padding to the table (3 bytes in both shipping files)
//   table    (N-1) x u16-LE, entry i = the START OFFSET of string (i+1)
//
// The RUNTIME never reads the table: `strings_bin_lookup_and_copy @image@0x23FFC`
// walks NULs, and `cp_table_lookup_by_index
// @image@0x025186` does the same for strings2.bin.  The table is emitted by EA's
// asset packer regardless of consumer, and because every entry is derivable from
// the strings themselves, this codec REGENERATES it rather than carrying it —
// which is what makes <see cref="ToBytes"/> byte-exact with nothing left
// unexplained.
//
// WHY A SECOND PARSER (see StringsBinDecoder).  That decoder finds the
// table by scanning for the first non-printable byte.  It is right for
// strings.bin, but strings2.bin's table opens with 0x3A 0x00 0x65 0x00 0x6A 0x00
// (offsets 58/101/106) — three printable low bytes — so the scan overshoots by 9
// bytes and reports 211 strings instead of 205.  The rule below is structural
// instead of statistical: the split is the one N for which the trailing
// 2*(N-1) bytes ARE the offset table and the gap is all NUL.  On both shipping
// files exactly one N satisfies it.
// ---------------------------------------------------------------------------

/// <summary>
/// One parsed NUL-separated string table: its strings plus the two framing
/// quantities needed to rebuild the file byte for byte.
/// </summary>
/// <param name="Strings">The strings in index order, without their NUL terminators (latin-1).</param>
/// <param name="PadNulCount">NUL bytes between the last string's terminator and the offset table (3 in both shipping files).</param>
/// <param name="BodyLength">Bytes up to and including the last string's NUL.</param>
/// <param name="TableOffset">Offset of the trailing u16 offset table.</param>
public sealed record StringsBinFile(
    IReadOnlyList<string> Strings,
    int PadNulCount,
    int BodyLength,
    int TableOffset)
{
    /// <summary>Entries in the trailing offset table: one per string after the first.</summary>
    public int TableEntryCount => Math.Max(0, Strings.Count - 1);
}

/// <summary>
/// Structural parser and byte-exact re-emitter for the <c>strings.bin</c> /
/// <c>strings2.bin</c> container (see the file header for the layout and its
/// citations).
/// </summary>
public static class StringsBinCodec
{
    /// <summary>NUL bytes between the string body and the offset table in both shipping files.</summary>
    public const int ShippingPadNuls = 3;

    /// <summary>
    /// Splits a decompressed body into its strings, deriving the body/table boundary from the
    /// structure rather than from byte statistics.
    /// </summary>
    /// <param name="body">The decompressed asset body.</param>
    /// <exception cref="InvalidDataException">No split makes the trailer the offset table of the strings before it.</exception>
    public static StringsBinFile Parse(ReadOnlySpan<byte> body)
    {
        List<int> starts = new List<int>();
        StringsBinFile? found = null;
        int position = 0;

        while (position < body.Length)
        {
            int terminator = body[position..].IndexOf((byte)0);
            if (terminator < 0)
            {
                break;
            }

            starts.Add(position);
            int end = position + terminator + 1;
            int count = starts.Count;
            int tableOffset = body.Length - (2 * (count - 1));

            if (tableOffset >= end && IsAllZero(body[end..tableOffset]) && TableMatches(body, tableOffset, starts))
            {
                if (found is not null)
                {
                    throw new InvalidDataException(
                        $"ambiguous strings table: both {found.Strings.Count} and {count} strings satisfy " +
                        "the trailing-offset-table rule");
                }

                found = new StringsBinFile(ReadStrings(body, starts, end), tableOffset - end, end, tableOffset);
            }

            position = end;
        }

        return found ?? throw new InvalidDataException(
            "not a strings.bin-shaped asset: no split leaves a trailing u16 table of the string " +
            "offsets (entry i = start of string i+1) behind a NUL-padded string body");
    }

    /// <summary>Rebuilds the decompressed body: strings, NUL padding, regenerated offset table.</summary>
    /// <param name="file">The parsed (or edited) table.</param>
    /// <exception cref="InvalidDataException">A string is not encodable, or an offset does not fit a u16.</exception>
    public static byte[] ToBytes(StringsBinFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        List<byte> body = new List<byte>(4096);
        List<int> starts = new List<int>(file.Strings.Count);

        foreach (string text in file.Strings)
        {
            starts.Add(body.Count);
            foreach (char c in text)
            {
                if (c > 0xFF)
                {
                    throw new InvalidDataException(
                        $"string {starts.Count - 1} carries U+{(int)c:X4}, which does not fit the " +
                        "asset's latin-1 byte encoding");
                }

                if (c == '\0')
                {
                    throw new InvalidDataException(
                        $"string {starts.Count - 1} carries an embedded NUL, which would split it in two");
                }

                body.Add((byte)c);
            }

            body.Add(0);
        }

        for (int i = 0; i < file.PadNulCount; i++)
        {
            body.Add(0);
        }

        for (int i = 1; i < starts.Count; i++)
        {
            if (starts[i] > ushort.MaxValue)
            {
                throw new InvalidDataException(
                    $"string {i} starts at {starts[i]}, past the u16 offset table's reach");
            }

            body.Add((byte)(starts[i] & 0xFF));
            body.Add((byte)(starts[i] >> 8));
        }

        return [.. body];
    }

    private static IReadOnlyList<string> ReadStrings(ReadOnlySpan<byte> body, List<int> starts, int end)
    {
        List<string> strings = new List<string>(starts.Count);
        for (int i = 0; i < starts.Count; i++)
        {
            int stop = i + 1 < starts.Count ? starts[i + 1] - 1 : end - 1;
            strings.Add(Encoding.Latin1.GetString(body[starts[i]..stop]));
        }

        return strings;
    }

    private static bool TableMatches(ReadOnlySpan<byte> body, int tableOffset, List<int> starts)
    {
        for (int i = 1; i < starts.Count; i++)
        {
            int at = tableOffset + (2 * (i - 1));
            if (BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(at, 2)) != starts[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> span)
    {
        foreach (byte b in span)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }
}
