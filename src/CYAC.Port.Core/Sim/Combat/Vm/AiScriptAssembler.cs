using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Sim.Combat.Vm;

/// <summary>What one authored AI script became, or why it could not be installed.</summary>
/// <param name="Program">The assembled bytecode, PRE-patch.</param>
/// <param name="Refusal">Why the script was refused, or <see langword="null"/> on success.</param>
public readonly record struct AiScriptAssembly(byte[] Program, string? Refusal)
{
    /// <summary>True when the script assembled.</summary>
    public bool Ok => Refusal is null;
}

/// <summary>
/// The port's own ASSEMBLER for the authored AI scripts the mission documents carry —
/// <c>MissionAiScriptDto.Steps</c> → the engagement VM's bytecode, in the exact wire form
/// <c>wld_or_s_asset_parser</c>'s tag-<c>0x89</c> record holds.
/// </summary>
/// <remarks>
/// <para>
/// H4 §5 A1 left the bandits on the engine's own generated behaviour because the port had a DECODER
/// (<see cref="AiScriptDecoder"/>) and no inverse.  <c>CYAC.Formats</c> has one
/// (<c>EaLib/AiScriptSynthesizer.cs</c>, whose round-trip is proved against all 268 shipped
/// scripts) but <c>Port.Core</c> may not reference it, so the encoding is re-derived here from the
/// same source it is: the VM's own operand widths (<see cref="AiScriptDecoder.OperandBytes"/>) and
/// the patcher's alphabet (<c>ai_script_named_place_patch @image@0x094EF</c>).
/// </para>
/// <para>
/// <b>The wire form.</b>  Every instruction is <c>{u16 delay, u8 opcode, operands…}</c>.  Operands
/// are one of three widths: a WORD (2 bytes, little-endian), a BYTE, or a WORLD-UNIT triple-byte
/// <c>i24</c> (<c>read_3bytes @image@0x0A403</c>'s inverse).  A <c>radio</c> step's text is
/// NUL-terminated ASCII.  The two NAMED-PLACE pseudo-ops fold the place index into the opcode byte:
/// <c>0xE3 + place</c> and <c>0xF0 + place</c>.
/// </para>
/// <para>
/// <b>What it refuses</b>, and why each refusal is an engine fact: a step whose opcode is outside
/// the alphabet the patcher can walk (it would derail the load-time walk); a coordinate outside the
/// <c>i24</c> range the wire has; a place index outside 0..12 (<c>[0xEE5A]</c> is <c>u16[13]</c>);
/// and a program longer than 712 bytes (the parser stages it in <c>[bp-0x2C8]</c>,
/// <c>image@0x09D2D</c>).
/// </para>
/// </remarks>
public static class AiScriptAssembler
{
    /// <summary>The parser's staging buffer: 712 bytes (<c>[bp-0x2C8]</c>, <c>image@0x09D2D</c>).</summary>
    public const int MaximumProgramBytes = 712;

    /// <summary>How many named places / actor slots there are: 13 (<c>[0xEE5A]</c> is u16[13]).</summary>
    public const int SlotCount = 13;

    /// <summary>The largest world-unit coordinate the <c>i24</c> wire holds.</summary>
    public const int UnitsMaximum = 0x7FFFFF;

    /// <summary>The smallest.</summary>
    public const int UnitsMinimum = -0x800000;

    /// <summary>One operand of one opcode.</summary>
    /// <param name="Key">Which document property carries it.</param>
    /// <param name="Kind">Its wire width.</param>
    /// <param name="Count">How many of them.</param>
    private readonly record struct Field(string Key, FieldKind Kind, int Count = 1);

    private enum FieldKind
    {
        Word,
        Byte,
        Units,
        Text,
    }

    /// <summary>The authorable alphabet — the ops the shipped 268 scripts use.</summary>
    private static readonly Dictionary<string, (byte Code, bool PlaceOp, Field[] Fields)> Ops =
        new(StringComparer.Ordinal)
        {
            ["idle"] = (0x00, false, []),
            ["orient_abs"] = (0x02, false, [new("target", FieldKind.Units, 3)]),
            ["phase8_auto"] = (0x08, false, []),
            ["track_actor"] = (0x0A, false, [new("actor", FieldKind.Word), new("params", FieldKind.Word, 3)]),
            ["phase_0d"] = (0x0D, false, [new("mode", FieldKind.Byte), new("params", FieldKind.Word, 3)]),
            ["kill_actor"] = (0xD5, false, [new("actor", FieldKind.Word)]),
            ["spawn_actor"] = (0xD6, false,
            [
                new("actor", FieldKind.Word), new("type", FieldKind.Word),
                new("heading", FieldKind.Word), new("duration", FieldKind.Word),
            ]),
            ["clear_script_flags"] = (0xD7, false, [new("mask", FieldKind.Word)]),
            ["set_script_flags"] = (0xD8, false, [new("flags", FieldKind.Word)]),
            ["set_heading"] = (0xD9, false, [new("heading", FieldKind.Word)]),
            ["retarget"] = (0xDA, false, []),
            ["clear_counter"] = (0xDB, false, []),
            ["loop"] = (0xDD, false, []),
            ["fire_weapon"] = (0xDE, false, [new("actor", FieldKind.Word)]),
            ["radio"] = (0xDF, false, [new("text", FieldKind.Text)]),
            ["mission_event"] = (0xE0, false, [new("event", FieldKind.Word)]),
            ["set_engage_flag"] = (0xE1, false, []),
            ["clear_engage_flag"] = (0xE2, false, []),
            ["heading_place"] = (0xE3, true, [new("offset", FieldKind.Units, 2)]),
            ["orient_place"] = (0xF0, true, [new("offset", FieldKind.Units, 3)]),
            ["orient_prev_origin"] = (0xFD, false, [new("offset", FieldKind.Units, 3)]),
            ["end_script"] = (0xFE, false, []),
            ["suspend"] = (0xFF, false, []),
        };

    /// <summary>Assembles one authored script.</summary>
    /// <param name="script">The document's structured script, or its raw hex.</param>
    /// <param name="hex">The attribute's <c>hex</c> fallback, when the transform could not structure it.</param>
    /// <returns>The program bytes, or a refusal.</returns>
    public static AiScriptAssembly Assemble(MissionAiScriptDto? script, string? hex)
    {
        if (hex is { Length: > 0 })
        {
            // The transform kept the payload as bytes because it could not structure it; those
            // bytes ARE the program, so install them verbatim.
            return Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal))
                is { } raw && raw.Length <= MaximumProgramBytes
                ? new AiScriptAssembly(raw, null)
                : new AiScriptAssembly([], $"raw payload longer than {MaximumProgramBytes} B");
        }

        if (script?.Steps is not { Count: > 0 } steps)
        {
            return new AiScriptAssembly([], "the attribute carries neither steps nor hex");
        }

        List<byte> bytes = new List<byte>(64);
        foreach (MissionAiStepDto step in steps)
        {
            if (step.Op is not { Length: > 0 } name || !Ops.TryGetValue(name, out (byte Code, bool PlaceOp, Field[] Fields) op))
            {
                return new AiScriptAssembly(
                    [], $"opcode '{step.Op}' is outside the authorable alphabet");
            }

            int place = step.Place ?? 0;
            if (op.PlaceOp && (place < 0 || place >= SlotCount))
            {
                return new AiScriptAssembly([], $"place {place} is outside 0..{SlotCount - 1}");
            }

            bytes.Add((byte)(step.Delay & 0xFF));
            bytes.Add((byte)((step.Delay >> 8) & 0xFF));
            bytes.Add((byte)(op.Code + (op.PlaceOp ? place : 0)));

            foreach (Field field in op.Fields)
            {
                string? refusal = Emit(bytes, field, step);
                if (refusal is not null)
                {
                    return new AiScriptAssembly([], $"{name}: {refusal}");
                }
            }
        }

        return bytes.Count > MaximumProgramBytes
            ? new AiScriptAssembly(
                [], $"program {bytes.Count} B > {MaximumProgramBytes} (the parser's staging buffer)")
            : new AiScriptAssembly([.. bytes], null);
    }

    private static string? Emit(List<byte> bytes, in Field field, MissionAiStepDto step)
    {
        switch (field.Kind)
        {
            case FieldKind.Text:
                string text = step.Text ?? string.Empty;
                foreach (char c in text)
                {
                    if (c is < ' ' or > '~')
                    {
                        return "the radio string has non-ASCII bytes";
                    }

                    bytes.Add((byte)c);
                }

                bytes.Add(0);
                return null;

            case FieldKind.Units:
                IReadOnlyList<int>? units = Values(field.Key, step);
                if (units is null || units.Count < field.Count)
                {
                    return $"'{field.Key}' needs {field.Count} coordinate(s)";
                }

                for (int i = 0; i < field.Count; i++)
                {
                    int value = units[i];
                    if (value is < UnitsMinimum or > UnitsMaximum)
                    {
                        return $"coordinate {value} is outside the i24 world-unit range";
                    }

                    bytes.Add((byte)(value & 0xFF));
                    bytes.Add((byte)((value >> 8) & 0xFF));
                    bytes.Add((byte)((value >> 16) & 0xFF));
                }

                return null;

            case FieldKind.Byte:
                bytes.Add((byte)(Scalar(field.Key, step) ?? 0));
                return null;

            default:
                if (field.Count > 1)
                {
                    IReadOnlyList<int>? words = Values(field.Key, step);
                    if (words is null || words.Count < field.Count)
                    {
                        return $"'{field.Key}' needs {field.Count} word(s)";
                    }

                    for (int i = 0; i < field.Count; i++)
                    {
                        bytes.Add((byte)(words[i] & 0xFF));
                        bytes.Add((byte)((words[i] >> 8) & 0xFF));
                    }

                    return null;
                }

                int word = Scalar(field.Key, step) ?? 0;
                bytes.Add((byte)(word & 0xFF));
                bytes.Add((byte)((word >> 8) & 0xFF));
                return null;
        }
    }

    private static IReadOnlyList<int>? Values(string key, MissionAiStepDto step) => key switch
    {
        "target" => step.Target,
        "params" => step.Params,
        "offset" => step.Offset,
        _ => null,
    };

    private static int? Scalar(string key, MissionAiStepDto step) => key switch
    {
        "actor" => step.Actor,
        "flags" => step.Flags,
        "mask" => step.Mask,
        "event" => step.Event,
        "mode" => step.Mode,
        "type" => step.Type,
        "heading" => step.Heading,
        "duration" => step.Duration,
        _ => step.Value,
    };
}
