using System.Text;

namespace CYAC.Formats.Image;

/// <summary>
/// The inverse of <see cref="FontDecoder"/>: rebuilds a <c>.fnt</c> payload from its parts.
/// </summary>
/// <remarks>
/// Layout as documented on <see cref="FontDecoder"/> (loaders <c>image@0x1F01A</c> /
/// <c>image@0x1F1F4</c>).  Only the <b>decompressed</b> payload is produced — the EALIB container
/// layer re-applies the flag=0x01 u32 size header and the LZSS stream, so this stays a pure format
/// codec.  T3 verified 3/3 shipping fonts rebuild byte-identically.
/// </remarks>
public static class FontEncoder
{
    /// <summary>
    /// Rebuilds the decompressed <c>.fnt</c> payload.
    /// </summary>
    /// <param name="formatVersion">The format byte at +0x0D (<c>0x02</c> on every shipping font).</param>
    /// <param name="name">The font name written at +0x0E, NUL-terminated.</param>
    /// <param name="nameFieldTail">The bytes between the name's NUL and +0x20 (see <see cref="FontDecoder.Font.NameFieldTail"/>).</param>
    /// <param name="field0x20">The u16 at +0x20 whose meaning is still open.</param>
    /// <param name="bytesPerRow">Stride of the unified glyph bitmap.</param>
    /// <param name="height">Scanlines per glyph.</param>
    /// <param name="maxWidth">The informative maximum glyph width at +0x26.</param>
    /// <param name="sentinel">The u32 at +0x28 (<see cref="FontDecoder.FormatSentinel"/>).</param>
    /// <param name="offsetTable">The 256 pixel-column offsets.</param>
    /// <param name="glyphData">The <paramref name="bytesPerRow"/> × <paramref name="height"/> glyph bitmap.</param>
    /// <param name="trailing">Bytes after the glyph bitmap, if any.</param>
    /// <exception cref="ArgumentException">A part does not have the length the layout requires.</exception>
    public static byte[] Encode(
        byte formatVersion,
        string name,
        ReadOnlySpan<byte> nameFieldTail,
        int field0x20,
        int bytesPerRow,
        int height,
        int maxWidth,
        uint sentinel,
        ReadOnlySpan<ushort> offsetTable,
        ReadOnlySpan<byte> glyphData,
        ReadOnlySpan<byte> trailing = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (offsetTable.Length != FontDecoder.TableEntries)
        {
            throw new ArgumentException(
                $"the offset table has {FontDecoder.TableEntries} entries, got {offsetTable.Length}",
                nameof(offsetTable));
        }

        int nameBytes = Encoding.ASCII.GetByteCount(name);
        if (nameBytes + 1 + nameFieldTail.Length != FontDecoder.NameFieldSize)
        {
            throw new ArgumentException(
                $"\"{name}\" + NUL + {nameFieldTail.Length} tail bytes is " +
                $"{nameBytes + 1 + nameFieldTail.Length} B; the name field is " +
                $"{FontDecoder.NameFieldSize} B", nameof(name));
        }

        int dataSize = bytesPerRow * height;
        if (glyphData.Length != dataSize)
        {
            throw new ArgumentException(
                $"glyph data is {glyphData.Length} B; {bytesPerRow}x{height} needs {dataSize} B",
                nameof(glyphData));
        }

        byte[] payload = new byte[FontDecoder.DataOffset + dataSize + trailing.Length];
        Encoding.ASCII.GetBytes("[DeluxeFont]", payload.AsSpan(0, FontDecoder.MarkerLength - 1));
        payload[FontDecoder.MarkerLength - 1] = 0x00;
        payload[FontDecoder.MarkerLength] = formatVersion;
        Encoding.ASCII.GetBytes(name, payload.AsSpan(FontDecoder.NameOffset, nameBytes));
        payload[FontDecoder.NameOffset + nameBytes] = 0x00;
        nameFieldTail.CopyTo(payload.AsSpan(FontDecoder.NameOffset + nameBytes + 1));

        WriteU16(payload, 0x20, field0x20);
        WriteU16(payload, 0x22, bytesPerRow);
        WriteU16(payload, 0x24, height);
        WriteU16(payload, 0x26, maxWidth);
        payload[0x28] = (byte)sentinel;
        payload[0x29] = (byte)(sentinel >> 8);
        payload[0x2A] = (byte)(sentinel >> 16);
        payload[0x2B] = (byte)(sentinel >> 24);

        for (int i = 0; i < FontDecoder.TableEntries; i++)
        {
            WriteU16(payload, FontDecoder.TableOffset + (i * 2), offsetTable[i]);
        }

        glyphData.CopyTo(payload.AsSpan(FontDecoder.DataOffset));
        trailing.CopyTo(payload.AsSpan(FontDecoder.DataOffset + dataSize));
        return payload;
    }

    private static void WriteU16(byte[] buffer, int offset, int value)
    {
        if (value is < 0 or > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "does not fit a u16");
        }

        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }
}
