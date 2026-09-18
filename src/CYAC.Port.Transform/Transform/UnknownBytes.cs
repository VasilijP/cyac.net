using System.Globalization;

namespace CYAC.Port.Transform.Transform;

/// <summary>
/// Law-L4 bookkeeping: bytes whose meaning is still open are emitted as <c>unknown_0xNN</c> JSON
/// fields (hex strings) so the round trip still closes, and counted so the burn-down is visible.
/// </summary>
/// <remarks>
/// A family with no unknown bytes is exact.  A family with zero unknown bytes and <c>exact</c> fidelity is
/// <i>understood</i>; anything else is a to-do with a number on it.
/// </remarks>
public sealed class UnknownBytes
{
    private readonly SortedDictionary<int, byte[]> _spans = [];

    /// <summary>The field-name prefix, so both halves of the round trip agree on it.</summary>
    public const string FieldPrefix = "unknown_";

    /// <summary>The number of bytes recorded as unknown.</summary>
    public int Count => _spans.Values.Sum(v => v.Length);

    /// <summary>True when nothing is open.</summary>
    public bool IsEmpty => _spans.Count == 0;

    /// <summary>Records a span of bytes whose meaning is open.</summary>
    /// <param name="offset">The span's offset inside the source body.</param>
    /// <param name="bytes">The bytes themselves.</param>
    public void Add(int offset, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        _spans[offset] = bytes.ToArray();
    }

    /// <summary>
    /// Records a span only when it is not all zero — the common "reserved / padding" case, where
    /// carrying the zeros adds noise but a non-zero byte is a real finding.
    /// </summary>
    /// <param name="offset">The span's offset inside the source body.</param>
    /// <param name="bytes">The bytes themselves.</param>
    /// <returns>True when the span was recorded.</returns>
    public bool AddIfNonZero(int offset, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b != 0)
            {
                Add(offset, bytes);
                return true;
            }
        }

        return false;
    }

    /// <summary>The JSON fields to emit: <c>unknown_0xNN</c> → upper-case hex byte string.</summary>
    public IReadOnlyDictionary<string, string> ToFields() =>
        _spans.ToDictionary(
            kv => $"{FieldPrefix}0x{kv.Key:X2}",
            kv => Convert.ToHexString(kv.Value));

    /// <summary>Reads the fields back, keyed by the offset encoded in the field name.</summary>
    /// <param name="fields">The <c>unknown_0xNN</c> fields as parsed from JSON.</param>
    /// <exception cref="InvalidDataException">A field name or value is malformed.</exception>
    public static IReadOnlyDictionary<int, byte[]> FromFields(IReadOnlyDictionary<string, string>? fields)
    {
        Dictionary<int, byte[]> result = new Dictionary<int, byte[]>();
        if (fields is null)
        {
            return result;
        }

        foreach ((string key, string value) in fields)
        {
            if (!key.StartsWith(FieldPrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"\"{key}\" is not an {FieldPrefix}* field");
            }

            string offsetText = key[FieldPrefix.Length..];
            if (!offsetText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(offsetText.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int offset))
            {
                throw new InvalidDataException($"\"{key}\": expected {FieldPrefix}0xNN");
            }

            result[offset] = Convert.FromHexString(value);
        }

        return result;
    }
}
