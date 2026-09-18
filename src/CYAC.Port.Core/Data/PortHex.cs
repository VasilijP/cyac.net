using System.Globalization;

namespace CYAC.Port.Core.Data;

/// <summary>
/// The one place the data tree's <c>"0xNNNN"</c> convention is implemented.
/// </summary>
/// <remarks>
/// Addresses, near pointers and bit fields are written as hex STRINGS in the transformed data (L5:
/// the tree is meant to be read and hand-edited, and a DGROUP offset printed as <c>18264</c> is
/// unreadable next to the project's own citations). Magnitudes — counts, speeds, weights, bounds —
/// stay JSON numbers.
/// </remarks>
public static class PortHex
{
    /// <summary>Formats a value as <c>0x</c> plus at least <paramref name="digits"/> hex digits.</summary>
    /// <param name="value">The value to format.</param>
    /// <param name="digits">Minimum digit count (2 for a byte, 4 for a word, 5 for an image offset).</param>
    public static string Format(int value, int digits = 4) =>
        "0x" + value.ToString("X" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>Parses a <c>0xNNNN</c> (or bare hex) string.</summary>
    /// <param name="text">The text to parse.</param>
    /// <exception cref="InvalidDataException"><paramref name="text"/> is null, empty or not hex.</exception>
    public static int Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("expected a hex value like \"0x1158\", found nothing");
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        if (!int.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
        {
            throw new InvalidDataException($"\"{text}\" is not a hex value like \"0x1158\"");
        }

        return value;
    }

    /// <summary>Parses a hex string, or returns <paramref name="fallback"/> when it is absent.</summary>
    /// <param name="text">The text to parse, possibly null.</param>
    /// <param name="fallback">What an absent field means.</param>
    public static int ParseOrDefault(string? text, int fallback = 0) =>
        string.IsNullOrWhiteSpace(text) ? fallback : Parse(text);

    /// <summary>Upper-case hex for a byte span — the form every <c>unknown_0xNN</c> field uses.</summary>
    /// <param name="bytes">The bytes to encode.</param>
    public static string? Bytes(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 0 ? null : Convert.ToHexString(bytes);

    /// <summary>Decodes an <c>unknown_0xNN</c> field into the bytes it carries.</summary>
    /// <param name="text">The hex string, or null for "the field was absent".</param>
    /// <param name="expectedLength">How many bytes the layout says the span holds.</param>
    /// <exception cref="InvalidDataException">The text is not hex, or is the wrong length.</exception>
    public static byte[] BytesOrZero(string? text, int expectedLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new byte[expectedLength];
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(text.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"\"{text}\" is not a hex byte string", ex);
        }

        if (bytes.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"expected {expectedLength} hex byte(s), found {bytes.Length}");
        }

        return bytes;
    }
}
