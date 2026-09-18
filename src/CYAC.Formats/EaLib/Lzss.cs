namespace CYAC.Formats.EaLib;

// LZSS decoder for EALIB flag=0x01 assets — port of. Reference: decompressor at
// image@0x2FA64
//
// Fixed parameters:
//   window 4096, max match 18, threshold 3, fill byte 0x20, init write pos 0x0FEE.
//   Control bits LSB-first; high-byte 0xFF sentinel marks "no more bits in dx".
//   match flag=0 → (offset_lo:8, (offset_hi:4 | (len-3):4))  -- 12-bit offset.
//   literal flag=1 → next byte.
//
// On-disk payload layout: u32-LE declared_size, then LZSS stream.
public static class Lzss
{
    private const int WindowSize = 4096;
    private const int MaxMatch = 18;
    private const int Threshold = 3;
    private const byte FillByte = 0x20;
    private const int InitPos = WindowSize - MaxMatch; // 4078 = 0x0FEE

    public static byte[] Decompress(ReadOnlySpan<byte> payload, int expectedSize)
    {
        byte[] output = new byte[expectedSize];
        byte[] window = new byte[WindowSize];
        window.AsSpan().Fill(FillByte);

        int outPos = 0;
        int winPos = InitPos;
        int src = 0;
        int dx = 0; // forces refill on first iteration

        while (outPos < expectedSize)
        {
            if ((dx & 0x100) == 0)
            {
                if (src >= payload.Length) break;
                dx = 0xFF00 | payload[src++];
            }
            int bit = dx & 1;
            dx >>= 1;

            if (bit != 0)
            {
                if (src >= payload.Length) break;
                byte b = payload[src++];
                output[outPos++] = b;
                window[winPos] = b;
                winPos = (winPos + 1) & (WindowSize - 1);
            }
            else
            {
                if (src + 1 >= payload.Length) break;
                byte b1 = payload[src++];
                byte b2 = payload[src++];
                int offset = b1 | ((b2 & 0xF0) << 4);
                int length = (b2 & 0x0F) + Threshold;
                for (int i = 0; i < length && outPos < expectedSize; i++)
                {
                    byte b = window[(offset + i) & (WindowSize - 1)];
                    output[outPos++] = b;
                    window[winPos] = b;
                    winPos = (winPos + 1) & (WindowSize - 1);
                }
            }
        }

        if (outPos != expectedSize)
            throw new InvalidDataException(
                $"LZSS truncated: produced {outPos} of {expectedSize} bytes");
        return output;
    }

    public static byte[] DecompressAsset(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 4)
            throw new InvalidDataException("Compressed asset too short for size header");
        int declared = raw[0] | (raw[1] << 8) | (raw[2] << 16) | (raw[3] << 24);
        return Decompress(raw[4..], declared);
    }
}
