using System.Text;
using System.Text.Json;
using CYAC.Formats.EaLib;
using CYAC.Port.Transform.Json;

namespace CYAC.Port.Transform.Ealib;

/// <summary>
/// One member of an EALIB archive as the data tree records it: the directory record verbatim, plus
/// where the member's decoded content now lives.
/// </summary>
/// <param name="Index">The directory slot, 0-based.</param>
/// <param name="Name">The member name decoded from the 13-byte field (may be empty).</param>
/// <param name="NameField">The 13 raw name bytes — what a rebuild writes back.</param>
/// <param name="EncodingValue">The encoding flag byte: 0x00 raw, 0x01 LZSS, 0x03 .pic.</param>
/// <param name="StoredOffset">The member's offset inside the archive.</param>
/// <param name="StoredLength">The member's stored (still-encoded) length.</param>
/// <param name="DeclaredDecompressedSize">For flag 0x01, the u32-LE size header; else null.</param>
/// <param name="DecodedLength">The decoded body's length.</param>
/// <param name="Family">The family that explains this member, or <c>raw</c>.</param>
/// <param name="Content">The data-tree paths holding the member's content.</param>
public sealed record ArchiveMemberRecord(
    int Index,
    string Name,
    byte[] NameField,
    byte EncodingValue,
    int StoredOffset,
    int StoredLength,
    int? DeclaredDecompressedSize,
    int DecodedLength,
    string Family,
    IReadOnlyList<string> Content)
{
    /// <summary>The encoding flag as the format enum.</summary>
    public EncodingFlag Encoding => (EncodingFlag)EncodingValue;

    /// <summary>A name for logs when the directory field is empty.</summary>
    public string DisplayName => Name.Length == 0 ? $"__unnamed_{Index:D3}" : Name;
}

/// <summary>
/// <c>&lt;data&gt;/libs/&lt;lib&gt;/_directory.json</c> — the parsed EALIB directory, and the
/// round-trip key for the archive.
/// </summary>
/// <remarks>
/// <para>
/// Container layout (re-verified on all six shipping libs — see
/// <c>CYAC.Formats.EaLib.EaLibWriter</c>): 7-byte header <c>"EALIB" + u16 file_count</c>, then
/// <c>file_count + 1</c> 18-byte records <c>name[13] · flag_u8 · offset_u32-LE</c> with a sentinel
/// last record whose offset is the archive size, then the payloads contiguously in directory order.
/// Because there are no gaps and no padding, an archive rebuilt from this record plus the members'
/// decoded content is byte-identical to the original — which is exactly what <c>--verify</c> checks.
/// </para>
/// <para>
/// The member name is kept as its raw 13 bytes whenever they are not simply the ASCII name
/// NUL-padded, because the display name is lossy: <c>EaLibArchive</c> synthesises
/// <c>__unnamed_NNN</c> for an empty field, and a rebuild from that string would corrupt the
/// directory.
/// </para>
/// </remarks>
public sealed class ArchiveDirectory
{
    /// <summary>The directory record's file name inside a lib folder.</summary>
    public const string FileName = "_directory.json";

    /// <summary>The 13-byte name field width.</summary>
    public const int NameFieldSize = EaLibWriter.NameFieldSize;

    private ArchiveDirectory(string archive, int archiveBytes, string sha256, IReadOnlyList<ArchiveMemberRecord> members)
    {
        Archive = archive;
        ArchiveBytes = archiveBytes;
        Sha256 = sha256;
        Members = members;
    }

    /// <summary>The archive's canonical file name, e.g. <c>"2b.lib"</c>.</summary>
    public string Archive { get; }

    /// <summary>The original archive's length in bytes.</summary>
    public int ArchiveBytes { get; }

    /// <summary>The original archive's SHA-256, lower-case hex.</summary>
    public string Sha256 { get; }

    /// <summary>The members, in directory order.</summary>
    public IReadOnlyList<ArchiveMemberRecord> Members { get; }

    /// <summary>The data-tree folder holding one archive's outputs.</summary>
    /// <param name="archiveFileName">The archive file name, e.g. <c>"3a.lib"</c>.</param>
    public static string FolderFor(string archiveFileName) =>
        $"libs/{Path.GetFileNameWithoutExtension(archiveFileName).ToLowerInvariant()}";

    /// <summary>Builds a directory record.</summary>
    /// <param name="archiveFileName">The archive's canonical file name.</param>
    /// <param name="archiveBytes">The archive's length.</param>
    /// <param name="sha256">The archive's digest.</param>
    /// <param name="members">The member records, in directory order.</param>
    public static ArchiveDirectory Create(
        string archiveFileName, int archiveBytes, string sha256, IReadOnlyList<ArchiveMemberRecord> members) =>
        new(archiveFileName, archiveBytes, sha256, members);

    /// <summary>The 13-byte name field of an entry, exactly as it sits in the archive.</summary>
    /// <param name="entry">The directory entry.</param>
    public static byte[] NameFieldOf(EaLibEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.NameRawBytes.ToArray();
    }

    /// <summary>The member name a 13-byte field denotes: ASCII up to the first NUL (may be empty).</summary>
    /// <param name="nameField">The raw 13 bytes.</param>
    public static string DecodeName(ReadOnlySpan<byte> nameField)
    {
        int end = 0;
        while (end < nameField.Length && nameField[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(nameField[..end]);
    }

    /// <summary>True when the raw field is exactly the ASCII name followed by NUL padding.</summary>
    /// <param name="nameField">The raw 13 bytes.</param>
    /// <param name="name">The decoded name.</param>
    public static bool NameFieldIsPlain(ReadOnlySpan<byte> nameField, string name)
    {
        if (name.Length == 0)
        {
            return false;   // an empty field is never "plain": it must round-trip through the hex form
        }

        Span<byte> expected = stackalloc byte[NameFieldSize];
        Encoding.ASCII.GetBytes(name, expected);
        return nameField.SequenceEqual(expected);
    }

    /// <summary>Serialises the record as the tree's JSON document.</summary>
    public byte[] ToJson()
    {
        ArchiveDirectoryDto dto = new ArchiveDirectoryDto
        {
            Format = "cyac.ealib.directory/1",
            About =
                "EALIB container directory. Header \"EALIB\"+u16 " +
                "count, then count+1 records of name[13]·flag_u8·offset_u32-LE (last is the sentinel " +
                "whose offset is the archive size), then payloads contiguously in directory order. " +
                "`content` names the files this member's decoded body now lives in; rebuilding those " +
                "in index order reproduces the archive byte-for-byte.",
            Archive = Archive,
            ArchiveBytes = ArchiveBytes,
            Sha256 = Sha256,
            MemberCount = Members.Count,
            Entries =
            [
                .. Members.Select(m => new ArchiveEntryDto
                {
                    Index = m.Index,
                    Name = m.Name,
                    NameFieldHex = NameFieldIsPlain(m.NameField, m.Name) ? null : Convert.ToHexString(m.NameField),
                    Encoding = Enum.IsDefined(m.Encoding) ? m.Encoding.ToString() : $"0x{m.EncodingValue:X2}",
                    EncodingValue = m.EncodingValue,
                    StoredOffset = m.StoredOffset,
                    StoredLength = m.StoredLength,
                    DeclaredDecompressedSize = m.DeclaredDecompressedSize,
                    DecodedLength = m.DecodedLength,
                    Family = m.Family,
                    Content = [.. m.Content],
                }),
            ],
        };

        return JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.ArchiveDirectoryDto);
    }

    /// <summary>Reads a directory record back from the tree.</summary>
    /// <param name="json">The <c>_directory.json</c> bytes.</param>
    /// <exception cref="InvalidDataException">The document is malformed.</exception>
    public static ArchiveDirectory FromJson(ReadOnlySpan<byte> json)
    {
        ArchiveDirectoryDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.ArchiveDirectoryDto)
                                  ?? throw new InvalidDataException($"{FileName} is empty");
        List<ArchiveEntryDto> entries = dto.Entries ?? throw new InvalidDataException($"{FileName} has no \"entries\"");
        if (entries.Count != dto.MemberCount)
        {
            throw new InvalidDataException(
                $"{FileName}: \"memberCount\" is {dto.MemberCount} but \"entries\" holds {entries.Count}");
        }

        List<ArchiveMemberRecord> members = new List<ArchiveMemberRecord>(entries.Count);
        foreach (ArchiveEntryDto e in entries)
        {
            string name = e.Name ?? string.Empty;
            byte[] field;
            if (!string.IsNullOrEmpty(e.NameFieldHex))
            {
                field = Convert.FromHexString(e.NameFieldHex);
                if (field.Length != NameFieldSize)
                {
                    throw new InvalidDataException(
                        $"{FileName} entry {e.Index}: nameFieldHex is {field.Length} B, expected {NameFieldSize}");
                }
            }
            else
            {
                field = new byte[NameFieldSize];
                if (Encoding.ASCII.GetByteCount(name) > NameFieldSize)
                {
                    throw new InvalidDataException(
                        $"{FileName} entry {e.Index}: name \"{name}\" exceeds the {NameFieldSize}-byte field");
                }

                Encoding.ASCII.GetBytes(name, field);
            }

            members.Add(new ArchiveMemberRecord(
                e.Index,
                name,
                field,
                e.EncodingValue,
                e.StoredOffset,
                e.StoredLength,
                e.DeclaredDecompressedSize,
                e.DecodedLength,
                e.Family ?? Transform.FamilyRegistry.RawFamily,
                e.Content ?? []));
        }

        return new ArchiveDirectory(
            dto.Archive ?? string.Empty, dto.ArchiveBytes, dto.Sha256 ?? string.Empty, members);
    }

    /// <summary>
    /// Rebuilds the archive from this record plus each member's decoded body, in directory order.
    /// </summary>
    /// <param name="decodedBodies">One decoded body per member, in the same order as <see cref="Members"/>.</param>
    /// <exception cref="InvalidDataException">The body count does not match the member count.</exception>
    public byte[] Rebuild(IReadOnlyList<byte[]> decodedBodies)
    {
        ArgumentNullException.ThrowIfNull(decodedBodies);
        if (decodedBodies.Count != Members.Count)
        {
            throw new InvalidDataException(
                $"{Archive}: {decodedBodies.Count} bodies for {Members.Count} members");
        }

        List<EaLibWriter.Item> items = new List<EaLibWriter.Item>(Members.Count);
        for (int i = 0; i < Members.Count; i++)
        {
            items.Add(new EaLibWriter.Item(
                Members[i].NameField, Members[i].Encoding, EncodeMember(Members[i], decodedBodies[i])));
        }

        return EaLibWriter.Build(items);
    }

    /// <summary>
    /// One member's STORED bytes: for flag 0x01 the u32-LE size header plus EA's own LZSS stream
    /// (<c>LzssCompressor.Policy.EaExact</c>, byte-identical on all 138 shipping compressed assets);
    /// otherwise the decoded body unchanged.
    /// </summary>
    /// <param name="member">The member's directory record.</param>
    /// <param name="decodedBody">Its decoded body.</param>
    public static byte[] EncodeMember(ArchiveMemberRecord member, byte[] decodedBody)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.Encoding == EncodingFlag.Compressed
            ? LzssCompressor.CompressAsset(decodedBody)
            : decodedBody;
    }
}
