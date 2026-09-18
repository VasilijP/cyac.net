using System.Globalization;
using System.Text;

namespace CYAC.Formats.EaLib;

/// <summary>The Python exception classes the win-rule analysis distinguishes.</summary>
internal enum PyErrorKind
{
    ValueError,
    KeyError,
    IndexError,
    TypeError,
    StopIteration,
    AssertionError,
    StructError,
    NameError,
}

internal sealed class PyError(PyErrorKind kind, string message) : Exception(message)
{
    public PyErrorKind Kind { get; } = kind;

    public static PyError Value(string message) => new(PyErrorKind.ValueError, message);

    public static PyError Key(long key) => new(PyErrorKind.KeyError, key.ToString(CultureInfo.InvariantCulture));

    public static PyError Index() => new(PyErrorKind.IndexError, "list index out of range");

    public static PyError Type(string message) => new(PyErrorKind.TypeError, message);

    public static PyError StopIteration() => new(PyErrorKind.StopIteration, string.Empty);

    public static PyError Assertion() => new(PyErrorKind.AssertionError, string.Empty);
}

/// <summary>Python's text conventions, as far as the win-rule analysis relies on them.</summary>
internal static class PyText
{
    /// <summary><c>f"{v:#x}"</c>.</summary>
    public static string Hex(long v) =>
        v < 0 ? "-0x" + (-v).ToString("x", CultureInfo.InvariantCulture)
              : "0x" + v.ToString("x", CultureInfo.InvariantCulture);

    /// <summary><c>str(v)</c> for an int.</summary>
    public static string Dec(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>int(s, base)</c> for base 0, 10 or 16; raises ValueError like Python.</summary>
    public static long Int(string s, int @base)
    {
        if (TryInt(s, @base, out long v))
        {
            return v;
        }

        throw PyError.Value($"invalid literal for int() with base {@base}: {Repr(s)}");
    }

    public static bool TryInt(string s, int @base, out long value)
    {
        value = 0;
        ReadOnlySpan<char> t = s.AsSpan().Trim(" \t\n\r\f\v");
        bool negative = false;
        if (t.Length > 0 && (t[0] == '+' || t[0] == '-'))
        {
            negative = t[0] == '-';
            t = t[1..];
        }

        int radix = @base;
        bool prefixed = false;
        if (t.Length >= 2 && t[0] == '0' && (@base == 0 || @base == 16) && (t[1] == 'x' || t[1] == 'X'))
        {
            radix = 16;
            prefixed = true;
            t = t[2..];
        }
        else if (@base == 0 && t.Length >= 2 && t[0] == '0' && (t[1] is 'o' or 'O' or 'b' or 'B'))
        {
            radix = t[1] is 'o' or 'O' ? 8 : 2;
            prefixed = true;
            t = t[2..];
        }
        else if (@base == 0)
        {
            radix = 10;
        }

        if (t.Length == 0)
        {
            return false;
        }

        // Underscores may separate digits (and follow a base prefix), never lead, trail or double.
        if (t[0] == '_' && !prefixed)
        {
            return false;
        }

        long acc = 0;
        bool lastUnderscore = false;
        int digits = 0;
        foreach (char c in t)
        {
            if (c == '_')
            {
                if (lastUnderscore)
                {
                    return false;
                }

                lastUnderscore = true;
                continue;
            }

            int d = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'z' => c - 'a' + 10,
                >= 'A' and <= 'Z' => c - 'A' + 10,
                _ => 99,
            };
            if (d >= radix)
            {
                return false;
            }

            lastUnderscore = false;
            digits++;
            acc = unchecked((acc * radix) + d);
        }

        if (lastUnderscore || digits == 0)
        {
            return false;
        }

        // Base 0 forbids leading zeros in a non-zero decimal literal.
        if (@base == 0 && radix == 10 && acc != 0 && t[0] == '0')
        {
            return false;
        }

        value = negative ? -acc : acc;
        return true;
    }

    /// <summary><c>s.rsplit(", ", 1)[1]</c>; raises IndexError when there is no separator.</summary>
    public static string AfterLastComma(string s)
    {
        int i = s.LastIndexOf(", ", StringComparison.Ordinal);
        if (i < 0)
        {
            throw PyError.Index();
        }

        return s[(i + 2)..];
    }

    /// <summary><c>s.index(sub)</c>; raises ValueError when absent.</summary>
    public static int IndexOf(string s, string sub)
    {
        int i = s.IndexOf(sub, StringComparison.Ordinal);
        if (i < 0)
        {
            throw PyError.Value("substring not found");
        }

        return i;
    }

    /// <summary>Python slice <c>s[start:stop]</c> with clamping; a negative stop counts from the end.</summary>
    public static string Slice(string s, int start, int stop)
    {
        if (stop < 0)
        {
            stop += s.Length;
        }

        start = Math.Clamp(start, 0, s.Length);
        stop = Math.Clamp(stop, 0, s.Length);
        return stop <= start ? string.Empty : s[start..stop];
    }

    /// <summary><c>repr(s)</c> for the plain texts the analysis produces.</summary>
    public static string Repr(string s)
    {
        char quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        StringBuilder sb = new StringBuilder().Append(quote);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c == quote)
                    {
                        sb.Append('\\').Append(c);
                    }
                    else if (c < ' ' || c == '\x7f')
                    {
                        sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append(quote).ToString();
    }

    /// <summary><c>repr(list_of_str)</c>.</summary>
    public static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Repr)) + "]";

    /// <summary><c>repr(x)</c> for an optional int.</summary>
    public static string Repr(long? v) => v is { } x ? Dec(x) : "None";
}
