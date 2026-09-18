namespace CYAC.Formats.EaLib;

// strings.bin (2a.lib idx 13, LZSS-compressed) decoder.
//
// Format (verified against image@0x23FFC strings_bin_lookup_and_copy in
// NUL-walk lookup; the trailing u16 offset table is unused by
// the runtime):
//
//   body (NUL-separated text):
//     string 0 starts at offset 0
//     string i (i >= 1) starts immediately after string (i-1)'s NUL
//   trailing offset table (244 B for strings.bin, 122 × u16-LE):
//     entry i = start offset of string (i+1)   [vestigial; emitted by
//     the EALIB asset packer regardless of consumer]
//
// The decoder walks NULs to slice strings; it never consults the trailer.
// Total string count = 123 (indices 0..122) — verified against the
// catalog dumped
public static class StringsBinDecoder
{
    public sealed record StringEntry(int Index, int Offset, int Length, string Text);

    public sealed record Catalog(
        IReadOnlyList<StringEntry> Strings,
        int BodyLength,
        int TrailerOffset,
        int TrailerLength,
        int RawCompressedLength,
        int DecompressedLength);

    public static Catalog DecodeFromEntry(EaLibEntry entry)
    {
        if (entry.Encoding != EncodingFlag.Compressed)
            throw new InvalidDataException(
                $"{entry.Archive.ShortName}::{entry.Name} is not LZSS-compressed " +
                $"(encoding={entry.Encoding}); strings.bin must be flag=0x01");
        byte[] decoded = entry.GetDecoded();
        return DecodeFromDecompressed(decoded, entry.Length);
    }

    public static Catalog DecodeFromDecompressed(byte[] decoded, int rawCompressedLength = 0)
    {
        // strings.bin has a NUL-separated
        // text body (which CAN contain empty strings between consecutive
        // NULs — index 78 of the shipping strings.bin is empty) followed
        // by some number of pad NULs and then a 244-B u16-LE trailer.
        // To find the end-of-body we look for the FIRST non-printable,
        // non-NUL byte (= trailer's first byte; the u16 offsets are
        // small enough that none of their high bytes is a printable
        // ASCII range, so this is unambiguous).  We then walk the body
        // [0..trailerStart) one NUL-terminated token at a time.
        int n = decoded.Length;
        int trailerScan = 0;
        while (trailerScan < n)
        {
            byte b = decoded[trailerScan];
            if (b != 0 && !IsPrintable(b)) break;
            trailerScan++;
        }
        // Walk back over any NUL padding immediately preceding the
        // trailer (the shipping strings.bin has 3 NULs between
        // "Credits\0" and the first u16).  body ends at trailerStart;
        // anything between body-end-NUL and trailerStart is padding.
        int bodyEnd = trailerScan;
        // The runtime treats index N as the N-th NUL-separated token.
        // We mirror that by splitting on NUL within [0, bodyEnd).  But
        // we don't want to count the pad NULs as empty strings: scan
        // backward from trailerScan to find the last NUL that
        // terminates a real (non-empty) token.
        // Detect padding run by counting trailing NULs in body.
        int padNuls = 0;
        while (bodyEnd - 1 >= 0 && decoded[bodyEnd - 1] == 0) { bodyEnd--; padNuls++; }
        // Include exactly ONE trailing NUL to terminate the final
        // string; everything else is pad.
        if (padNuls > 0) bodyEnd++;

        List<StringEntry> strings = new List<StringEntry>();
        int strStart = 0;
        for (int i = 0; i < bodyEnd; i++)
        {
            if (decoded[i] == 0)
            {
                int len = i - strStart;
                string text = System.Text.Encoding.Latin1.GetString(decoded, strStart, len);
                strings.Add(new StringEntry(strings.Count, strStart, len, text));
                strStart = i + 1;
            }
        }
        // Tail: a final un-terminated string is unusual but we capture it
        // for diagnostic visibility.
        if (strStart < bodyEnd)
        {
            int len = bodyEnd - strStart;
            string text = System.Text.Encoding.Latin1.GetString(decoded, strStart, len);
            strings.Add(new StringEntry(strings.Count, strStart, len, text));
        }

        // trailerOffset = first byte of the u16 table itself (after pad).
        int trailerOffset = trailerScan;
        int trailerLength = n - trailerOffset;

        return new Catalog(
            Strings: strings,
            BodyLength: trailerOffset,
            TrailerOffset: trailerOffset,
            TrailerLength: trailerLength,
            RawCompressedLength: rawCompressedLength,
            DecompressedLength: n);
    }

    private static bool IsPrintable(byte b)
        => (b >= 0x20 && b < 0x7F) || b == 0x09 || b == 0x0A || b == 0x0D;
}
