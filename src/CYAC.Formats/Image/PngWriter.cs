using System.IO.Compression;

namespace CYAC.Formats.Image;

// Minimal PNG encoder for the --render-mesh CLI.
//
// Writes 8-bit RGB PNG (no alpha) from a BGRA u32 buffer (matching the mesh
// renderer's pixel format). Uses the .NET BCL's DeflateStream for IDAT
// compression — no third-party dependency.
//
// Output structure (per RFC 2083 / W3C PNG):
//   - 8-byte signature: 89 50 4E 47 0D 0A 1A 0A
//   - IHDR chunk: width, height, bit-depth=8, color-type=2 (RGB), compression=0,
//                 filter=0, interlace=0  (13 data bytes)
//   - IDAT chunk: zlib-wrapped Deflate of filtered scanlines
//                 (each scanline = filter byte 0 + width*3 RGB bytes)
//   - IEND chunk: empty
//
// CRC32 used in chunk trailers — implemented inline with the standard
// 0xEDB88320 polynomial table.
public static class PngWriter
{
    private const ulong PngSignature = 0x89504E470D0A1A0AUL;
    private static readonly uint[] Crc32Table = BuildCrcTable();

    // Write `buf` (BGRA u32 buffer of `width * height` u32s) to `path` as PNG.
    public static void WriteBgra(string path, uint[] buf, int width, int height)
    {
        if (buf is null || width <= 0 || height <= 0)
            throw new ArgumentException("invalid buffer/dimensions");
        if (buf.Length < width * height)
            throw new ArgumentException($"buffer too small: {buf.Length} < {width * height}");

        using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        WriteBgra(fs, buf, width, height);
    }

    public static void WriteBgra(Stream stream, uint[] buf, int width, int height)
    {
        // PNG signature.
        Span<byte> sig = stackalloc byte[8];
        sig[0] = 0x89; sig[1] = 0x50; sig[2] = 0x4E; sig[3] = 0x47;
        sig[4] = 0x0D; sig[5] = 0x0A; sig[6] = 0x1A; sig[7] = 0x0A;
        stream.Write(sig);

        // IHDR
        byte[] ihdr = new byte[13];
        WriteU32BE(ihdr, 0, (uint)width);
        WriteU32BE(ihdr, 4, (uint)height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // color type: 2 = RGB
        ihdr[10] = 0;  // compression: 0
        ihdr[11] = 0;  // filter: 0
        ihdr[12] = 0;  // interlace: 0
        WriteChunk(stream, "IHDR", ihdr);

        // IDAT: build filtered RGB scanlines, then zlib-deflate.
        // Each scanline = 1 filter byte (0=None) + width*3 RGB bytes.
        byte[] raw = new byte[height * (1 + width * 3)];
        int dst = 0;
        for (int y = 0; y < height; y++)
        {
            raw[dst++] = 0;  // filter type 0 (None)
            int rowOff = y * width;
            for (int x = 0; x < width; x++)
            {
                uint bgra = buf[rowOff + x];
                raw[dst++] = (byte)((bgra >> 16) & 0xFF);  // R
                raw[dst++] = (byte)((bgra >> 8) & 0xFF);   // G
                raw[dst++] = (byte)(bgra & 0xFF);          // B
            }
        }

        // zlib wrapper: 2-byte header + deflate stream + 4-byte Adler32.
        byte[] deflated = DeflateBytes(raw);
        byte[] zlib = new byte[2 + deflated.Length + 4];
        zlib[0] = 0x78;  // CMF: deflate, 32K window
        zlib[1] = 0x9C;  // FLG: default compression (and (CMF*256+FLG) % 31 == 0)
        Buffer.BlockCopy(deflated, 0, zlib, 2, deflated.Length);
        uint adler = Adler32(raw);
        WriteU32BE(zlib, 2 + deflated.Length, adler);
        WriteChunk(stream, "IDAT", zlib);

        // IEND
        WriteChunk(stream, "IEND", Array.Empty<byte>());
    }

    private static byte[] DeflateBytes(byte[] input)
    {
        using MemoryStream ms = new MemoryStream();
        using (DeflateStream ds = new DeflateStream(ms, CompressionLevel.Fastest, true))
        {
            ds.Write(input, 0, input.Length);
        }
        return ms.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        if (type.Length != 4) throw new ArgumentException("type must be 4 chars");
        Span<byte> lenBytes = stackalloc byte[4];
        WriteU32BE(lenBytes, 0, (uint)data.Length);
        stream.Write(lenBytes);

        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        stream.Write(typeBytes);

        if (data.Length > 0) stream.Write(data);

        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < 4; i++)
            crc = (crc >> 8) ^ Crc32Table[(crc ^ typeBytes[i]) & 0xFF];
        for (int i = 0; i < data.Length; i++)
            crc = (crc >> 8) ^ Crc32Table[(crc ^ data[i]) & 0xFF];
        crc ^= 0xFFFFFFFFu;

        Span<byte> crcBytes = stackalloc byte[4];
        WriteU32BE(crcBytes, 0, crc);
        stream.Write(crcBytes);
    }

    private static void WriteU32BE(byte[] buf, int off, uint value)
    {
        buf[off] = (byte)((value >> 24) & 0xFF);
        buf[off + 1] = (byte)((value >> 16) & 0xFF);
        buf[off + 2] = (byte)((value >> 8) & 0xFF);
        buf[off + 3] = (byte)(value & 0xFF);
    }

    private static void WriteU32BE(Span<byte> buf, int off, uint value)
    {
        buf[off] = (byte)((value >> 24) & 0xFF);
        buf[off + 1] = (byte)((value >> 16) & 0xFF);
        buf[off + 2] = (byte)((value >> 8) & 0xFF);
        buf[off + 3] = (byte)(value & 0xFF);
    }

    private static uint[] BuildCrcTable()
    {
        uint[] t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                if ((c & 1) != 0) c = 0xEDB88320u ^ (c >> 1);
                else c >>= 1;
            }
            t[n] = c;
        }
        return t;
    }

    private static uint Adler32(byte[] data)
    {
        const uint MOD = 65521;
        uint a = 1, b = 0;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % MOD;
            b = (b + a) % MOD;
        }
        return (b << 16) | a;
    }
}
