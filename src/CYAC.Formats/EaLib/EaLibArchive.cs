using System.Text;

namespace CYAC.Formats.EaLib;

// EALIB archive reader. Format:
//   Header (7 B): "EALIB" + u16-LE file_count
//   Directory: (file_count + 1) × 18 B entries:
//     name[13]  ASCII, NUL-padded
//     encoding_flag_u8  (0x00 raw / 0x01 compressed / 0x03 .pic)
//     offset_u32  LE — start of asset within the archive
//   Final entry is a sentinel: name = zeros, offset = file size.
public sealed class EaLibArchive
{
    public string Path { get; }
    public string ShortName { get; }
    public byte[] Data { get; }
    public int FileCount { get; }
    public IReadOnlyList<EaLibEntry> Entries { get; }

    public EaLibArchive(string path) : this(File.ReadAllBytes(path), path)
    {
    }

    /// <summary>
    /// Opens an archive that is already in memory — e.g. an entry read out of a zip (port pre-flight
    /// P1).  <paramref name="displayName"/> is what <see cref="Path"/>, <see cref="ShortName"/> and error
    /// messages show; nothing is ever read from it.
    /// </summary>
    public EaLibArchive(byte[] data, string displayName)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(displayName);
        Path = displayName;
        ShortName = System.IO.Path.GetFileName(displayName);
        Data = data;
        if (Data.Length < 7 || Data.AsSpan(0, 5).SequenceEqual("EALIB"u8) == false)
            throw new InvalidDataException($"{displayName}: not an EALIB archive");
        FileCount = Data[5] | (Data[6] << 8);

        List<EaLibEntry> entries = new List<EaLibEntry>(FileCount);
        for (int i = 0; i < FileCount; i++)
        {
            int rec = 7 + i * 18;
            int nextRec = 7 + (i + 1) * 18;
            int nameEnd = 0;
            while (nameEnd < 13 && Data[rec + nameEnd] != 0) nameEnd++;
            string name = Encoding.ASCII.GetString(Data, rec, nameEnd);
            byte flag = Data[rec + 13];
            int off = Data[rec + 14] | (Data[rec + 15] << 8)
                    | (Data[rec + 16] << 16) | (Data[rec + 17] << 24);
            int nextOff = Data[nextRec + 14] | (Data[nextRec + 15] << 8)
                        | (Data[nextRec + 16] << 16) | (Data[nextRec + 17] << 24);
            entries.Add(new EaLibEntry
            {
                Index = i,
                Name = name.Length == 0 ? $"__unnamed_{i:D3}" : name,
                Encoding = (EncodingFlag)flag,
                Offset = off,
                Length = nextOff - off,
                Archive = this,
            });
        }
        Entries = entries;
    }

    /// <summary>
    /// Find an entry with a given filename (case-insensitive comparison
    /// on the basename). Used by the .pic preview path to locate the
    /// `&lt;stem&gt;.pal` companion.
    /// </summary>
    public EaLibEntry? FindEntry(string name)
    {
        foreach (EaLibEntry e in Entries)
            if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                return e;
        return null;
    }
}
