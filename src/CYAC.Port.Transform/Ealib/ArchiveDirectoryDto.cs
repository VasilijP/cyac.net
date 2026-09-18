using System.Text.Json.Serialization;

namespace CYAC.Port.Transform.Ealib;

// Wire format of <data>/libs/<lib>/_directory.json — the EALIB container's own record, and the key
// that makes an archive rebuildable from the tree (transform-, T1).

internal sealed class ArchiveDirectoryDto
{
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("about")]
    public string? About { get; init; }

    [JsonPropertyName("archive")]
    public string? Archive { get; init; }

    [JsonPropertyName("archiveBytes")]
    public int ArchiveBytes { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("memberCount")]
    public int MemberCount { get; init; }

    [JsonPropertyName("entries")]
    public List<ArchiveEntryDto>? Entries { get; init; }
}

internal sealed class ArchiveEntryDto
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    // Present only when the 13-byte directory field is not simply the ASCII name NUL-padded — an
    // empty or non-printable field must still rebuild byte-exactly.
    [JsonPropertyName("nameFieldHex")]
    public string? NameFieldHex { get; init; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; init; }

    [JsonPropertyName("encodingValue")]
    public byte EncodingValue { get; init; }

    [JsonPropertyName("storedOffset")]
    public int StoredOffset { get; init; }

    [JsonPropertyName("storedLength")]
    public int StoredLength { get; init; }

    [JsonPropertyName("declaredDecompressedSize")]
    public int? DeclaredDecompressedSize { get; init; }

    [JsonPropertyName("decodedLength")]
    public int DecodedLength { get; init; }

    [JsonPropertyName("family")]
    public string? Family { get; init; }

    [JsonPropertyName("content")]
    public List<string>? Content { get; init; }
}
