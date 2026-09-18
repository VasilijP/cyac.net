namespace CYAC.Formats.EaLib;

// EALIB directory-entry encoding flag (byte 13 of the 14-byte name slot).
// Codec at image@0x2FA64.
public enum EncodingFlag : byte
{
    Raw = 0x00,        // .PNT, .SND, raw data — load as-is
    Compressed = 0x01, // LZSS (N=4096, F=18, threshold=3, fill=0x20, init_pos=0x0FEE)
    Pic = 0x03,        // .PIC self-describing (PXPK magic) — codec at image@0x2FBD6 (partial)
}
