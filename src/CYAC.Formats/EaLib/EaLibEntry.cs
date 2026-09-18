namespace CYAC.Formats.EaLib;

public sealed class EaLibEntry
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required EncodingFlag Encoding { get; init; }
    public required int Offset { get; init; }
    public required int Length { get; init; }
    public required EaLibArchive Archive { get; init; }

    public ReadOnlyMemory<byte> RawBytes => Archive.Data.AsMemory(Offset, Length);

    /// <summary>
    /// The 13 raw name bytes exactly as they sit in the directory record
    /// (NUL-padded, NOT trimmed).  <see cref="Name"/> is a display form and is
    /// synthesised for empty names, so it must never be used to rebuild a
    /// directory — the write path (EaLibWriter) uses these bytes instead so a
    /// zero-edit re-emit is byte-exact.
    /// </summary>
    public ReadOnlyMemory<byte> NameRawBytes => Archive.Data.AsMemory(7 + Index * 18, 13);

    // For flag=0x01 assets the first 4 bytes are u32-LE declared size.
    public int? DeclaredDecompressedSize =>
        Encoding == EncodingFlag.Compressed && Length >= 4
            ? RawBytes.Span[0] | (RawBytes.Span[1] << 8) | (RawBytes.Span[2] << 16) | (RawBytes.Span[3] << 24)
            : null;

    public byte[] GetDecoded()
    {
        ReadOnlySpan<byte> raw = RawBytes.Span;
        return Encoding switch
        {
            EncodingFlag.Compressed => Lzss.DecompressAsset(raw),
            _ => raw.ToArray(),
        };
    }

    public override string ToString() =>
        $"[{Index:D3}] {Name,-14} {Encoding,-10} {Length,7} B";
}
