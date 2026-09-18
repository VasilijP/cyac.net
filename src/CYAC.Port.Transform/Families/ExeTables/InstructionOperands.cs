using System.Buffers.Binary;

namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>
/// Reads an operand out of one instruction of the unpacked image, after checking the instruction's
/// opcode bytes — how a table finds itself by following the code that indexes it.
/// </summary>
/// <remarks>
/// Every failure names the instruction, its image offset and the bytes found, so a different build of
/// the executable is refused with a message rather than read at the wrong place.
/// </remarks>
internal static class InstructionOperands
{
    /// <summary>Checks that an instruction starts with the given opcode bytes.</summary>
    /// <param name="image">The unpacked layer-1 image.</param>
    /// <param name="at">The instruction's image offset.</param>
    /// <param name="what">The instruction, as assembly, for the message.</param>
    /// <param name="opcode">The bytes it must start with.</param>
    /// <exception cref="InvalidDataException">The bytes differ.</exception>
    public static void Expect(ReadOnlySpan<byte> image, int at, string what, ReadOnlySpan<byte> opcode)
    {
        if (at < 0 || at + opcode.Length > image.Length || !image.Slice(at, opcode.Length).SequenceEqual(opcode))
        {
            int available = Math.Clamp(image.Length - Math.Max(at, 0), 0, opcode.Length);
            string found = at < 0 || available == 0
                ? "nothing"
                : Convert.ToHexString(image.Slice(at, available));
            throw new InvalidDataException(
                $"expected `{what}` ({Convert.ToHexString(opcode)}…) at image@0x{at:X5}, found {found}");
        }
    }

    /// <summary>Checks an instruction's opcode and reads the 8-bit immediate that follows it.</summary>
    /// <param name="image">The unpacked layer-1 image.</param>
    /// <param name="at">The instruction's image offset.</param>
    /// <param name="what">The instruction, as assembly, for the message.</param>
    /// <param name="opcode">The bytes before the immediate.</param>
    /// <returns>The immediate, zero-extended.</returns>
    public static int Imm8(ReadOnlySpan<byte> image, int at, string what, ReadOnlySpan<byte> opcode)
    {
        Expect(image, at, what, opcode);
        Available(image, at, opcode.Length + 1, what);
        return image[at + opcode.Length];
    }

    /// <summary>Checks an instruction's opcode and reads the 16-bit word that follows it.</summary>
    /// <param name="image">The unpacked layer-1 image.</param>
    /// <param name="at">The instruction's image offset.</param>
    /// <param name="what">The instruction, as assembly, for the message.</param>
    /// <param name="opcode">The bytes before the word (an immediate or a displacement).</param>
    /// <returns>The word, zero-extended.</returns>
    public static int Word(ReadOnlySpan<byte> image, int at, string what, ReadOnlySpan<byte> opcode)
    {
        Expect(image, at, what, opcode);
        Available(image, at, opcode.Length + 2, what);
        return BinaryPrimitives.ReadUInt16LittleEndian(image[(at + opcode.Length)..]);
    }

    private static void Available(ReadOnlySpan<byte> image, int at, int length, string what)
    {
        if (at + length > image.Length)
        {
            throw new InvalidDataException($"`{what}` at image@0x{at:X5} runs past the end of the image");
        }
    }
}
