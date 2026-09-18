using CYAC.Formats.EaLib;

namespace CYAC.Port.Transform.Transform;

/// <summary>
/// The bytes a family transform is asked to explain, plus where in the original distribution they
/// came from.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Content"/> is always the <b>decoded</b> body: for an EALIB member that is
/// <c>EaLibEntry.GetDecoded</c> (LZSS already undone), for a standalone file it is the file
/// itself.  A family therefore never re-implements the container.
/// </para>
/// </remarks>
public sealed class TransformSource
{
    private TransformSource(
        string originFile,
        string name,
        ReadOnlyMemory<byte> content,
        int? entryIndex,
        EncodingFlag? encoding,
        int? storedOffset,
        int? storedLength)
    {
        OriginFile = originFile;
        Name = name;
        Content = content;
        EntryIndex = entryIndex;
        Encoding = encoding;
        StoredOffset = storedOffset;
        StoredLength = storedLength;
    }

    /// <summary>The original file this came from: <c>"yeager.cfg"</c>, <c>"1b.lib"</c>, ….</summary>
    public string OriginFile { get; }

    /// <summary>The EALIB member name, or the file name for a standalone source.</summary>
    public string Name { get; }

    /// <summary>The decoded bytes to explain.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    /// <summary>The member's directory slot, or <see langword="null"/> for a standalone file.</summary>
    public int? EntryIndex { get; }

    /// <summary>The member's on-disk encoding flag, or <see langword="null"/> for a standalone file.</summary>
    public EncodingFlag? Encoding { get; }

    /// <summary>The member's stored offset inside the archive, or <see langword="null"/>.</summary>
    public int? StoredOffset { get; }

    /// <summary>The member's stored (still-encoded) length, or <see langword="null"/>.</summary>
    public int? StoredLength { get; }

    /// <summary>The lib stem of an archive member (<c>"1b"</c> for <c>1b.lib</c>), else the origin file.</summary>
    public string OriginStem => Path.GetFileNameWithoutExtension(OriginFile);

    /// <summary>The source name without its extension, lower-cased — the usual output stem.</summary>
    public string Stem
    {
        get
        {
            string stem = Path.GetFileNameWithoutExtension(Name);
            return stem.Length == 0 ? Name.ToLowerInvariant() : stem.ToLowerInvariant();
        }
    }

    /// <summary>The source name's extension, lower-cased and including the dot (may be empty).</summary>
    public string Extension => Path.GetExtension(Name).ToLowerInvariant();

    /// <summary>Wraps an EALIB member.</summary>
    /// <param name="archiveFileName">The archive's canonical file name, e.g. <c>"1b.lib"</c>.</param>
    /// <param name="entry">The directory entry.</param>
    /// <param name="decoded">Its decoded body.</param>
    public static TransformSource FromArchiveEntry(
        string archiveFileName, EaLibEntry entry, ReadOnlyMemory<byte> decoded) =>
        new(archiveFileName, entry.Name, decoded, entry.Index, entry.Encoding, entry.Offset, entry.Length);

    /// <summary>Wraps a standalone original file.</summary>
    /// <param name="fileName">The file's canonical name, e.g. <c>"yeager.cfg"</c>.</param>
    /// <param name="content">Its bytes.</param>
    public static TransformSource FromFile(string fileName, ReadOnlyMemory<byte> content) =>
        new(fileName, fileName, content, null, null, null, null);

    /// <summary>A short citation for logs and manifest notes.</summary>
    public override string ToString() =>
        EntryIndex is null ? OriginFile : $"{OriginFile}/{Name}";
}
