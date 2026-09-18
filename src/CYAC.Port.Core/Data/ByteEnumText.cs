using System.Globalization;

namespace CYAC.Port.Core.Data;

/// <summary>
/// Reads and writes single-byte original enum values as readable JSON strings, without ever losing
/// a value the enum does not name.
/// </summary>
/// <remarks>
/// <para>
/// Law L3 needs the round trip to close for <b>every</b> byte, including one outside the documented
/// enum — the shipped <c>yeager.cfg</c>'s audio mask is exactly such a case (<c>GameConfig</c>
/// remarks: "including values outside a documented enum").  So a named value is written as its name
/// and an unnamed one as <c>"0xNN"</c>; the reader accepts both.  Nothing is silently clamped to a
/// legal value.
/// </para>
/// </remarks>
public static class ByteEnumText
{
    /// <summary>The enum member's name, or <c>0xNN</c> when the byte is not a named value.</summary>
    /// <typeparam name="TEnum">The enum to name the byte with.</typeparam>
    /// <param name="value">The persisted byte.</param>
    public static string Write<TEnum>(byte value)
        where TEnum : struct, Enum
    {
        TEnum typed = (TEnum)Enum.ToObject(typeof(TEnum), value);
        return Enum.IsDefined(typed) ? typed.ToString() : Hex(value);
    }

    /// <summary>The byte a JSON string denotes — an enum member name or <c>0xNN</c>.</summary>
    /// <typeparam name="TEnum">The enum the name belongs to.</typeparam>
    /// <param name="text">The JSON value.</param>
    /// <param name="field">The field name, for the error message.</param>
    /// <exception cref="InvalidDataException">The text is neither a member name nor a hex byte.</exception>
    public static byte Read<TEnum>(string? text, string field)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException($"\"{field}\" is missing");
        }

        if (TryHex(text, out byte raw))
        {
            return raw;
        }

        if (Enum.TryParse<TEnum>(text, ignoreCase: true, out TEnum typed) && Enum.IsDefined(typed))
        {
            return Convert.ToByte(typed);
        }

        throw new InvalidDataException(
            $"\"{field}\": \"{text}\" is neither a {typeof(TEnum).Name} name " +
            $"({string.Join(", ", Enum.GetNames<TEnum>())}) nor a 0xNN byte");
    }

    /// <summary>
    /// Splits a byte into the names of the flag bits it sets, plus the bits no name covers.
    /// </summary>
    /// <typeparam name="TEnum">A <c>[Flags]</c> enum over single bits.</typeparam>
    /// <param name="value">The persisted byte.</param>
    /// <returns>The set flag names and, when some bits are unnamed, their mask as <c>0xNN</c>.</returns>
    public static (List<string> Names, string? UnknownBits) WriteFlags<TEnum>(byte value)
        where TEnum : struct, Enum
    {
        List<string> names = new List<string>();
        int covered = 0;
        foreach (TEnum member in Enum.GetValues<TEnum>())
        {
            int bit = Convert.ToInt32(member);
            if (bit == 0 || (bit & (bit - 1)) != 0)
            {
                continue;   // zero and composite members are not bits
            }

            covered |= bit;
            if ((value & bit) == bit)
            {
                names.Add(member.ToString());
            }
        }

        int leftover = value & ~covered;
        return (names, leftover == 0 ? null : Hex((byte)leftover));
    }

    /// <summary>Recombines <see cref="WriteFlags{TEnum}"/>'s output into the persisted byte.</summary>
    /// <typeparam name="TEnum">The <c>[Flags]</c> enum.</typeparam>
    /// <param name="names">The set flag names.</param>
    /// <param name="unknownBits">The unnamed bit mask as <c>0xNN</c>, or <see langword="null"/>.</param>
    /// <param name="field">The field name, for the error message.</param>
    /// <exception cref="InvalidDataException">A name is not a flag of the enum.</exception>
    public static byte ReadFlags<TEnum>(IReadOnlyList<string>? names, string? unknownBits, string field)
        where TEnum : struct, Enum
    {
        int value = 0;
        foreach (string name in names ?? [])
        {
            if (!Enum.TryParse<TEnum>(name, ignoreCase: true, out TEnum member) || !Enum.IsDefined(member))
            {
                throw new InvalidDataException(
                    $"\"{field}\": \"{name}\" is not a {typeof(TEnum).Name} flag " +
                    $"({string.Join(", ", Enum.GetNames<TEnum>())})");
            }

            value |= Convert.ToInt32(member);
        }

        if (!string.IsNullOrWhiteSpace(unknownBits))
        {
            if (!TryHex(unknownBits, out byte extra))
            {
                throw new InvalidDataException($"\"{field}\" unknown bits: expected 0xNN, got \"{unknownBits}\"");
            }

            value |= extra;
        }

        if (value is < 0 or > 0xFF)
        {
            throw new InvalidDataException($"\"{field}\" does not fit in one byte: 0x{value:X}");
        }

        return (byte)value;
    }

    /// <summary>Formats a byte as <c>0xNN</c>.</summary>
    /// <param name="value">The byte.</param>
    public static string Hex(byte value) => $"0x{value:X2}";

    /// <summary>Parses <c>0xNN</c> (or bare hex) into a byte.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="value">The parsed byte.</param>
    public static bool TryHex(string? text, out byte value)
    {
        value = 0;
        if (text is null)
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }
        else
        {
            return false;
        }

        return byte.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
