using System.Globalization;
using System.Text.Json;
using CYAC.Port.Core.Sim.Flight;

namespace CYAC.Port.Core.Sim;

/// <summary>
/// Reads and writes <see cref="InputLog"/> documents (JSON).
/// </summary>
/// <remarks>
/// Round-trip is the proof here as everywhere else: a log written and
/// read back is the same log, event for event and checkpoint for checkpoint, and a test asserts it.
/// </remarks>
public static class InputLogCodec
{
    /// <summary>The document's <c>format</c> tag.</summary>
    public const string FormatTag = "cyac.input-log/1";

    /// <summary>The <c>about</c> line every written log carries.</summary>
    public const string About =
        "CYAC port replay: step-indexed input landmarks + the seeds that reproduce the mission. "
            + "/§4.3,";

    /// <summary>Writes a log as JSON.</summary>
    /// <param name="log">The log to write.</param>
    /// <param name="stream">The destination.</param>
    public static void Write(InputLog log, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(stream);
        JsonSerializer.Serialize(stream, ToDto(log), InputLogJsonContext.Readable.InputLogDto);
    }

    /// <summary>Serialises a log to a JSON string.</summary>
    /// <param name="log">The log to write.</param>
    public static string ToJson(InputLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return JsonSerializer.Serialize(ToDto(log), InputLogJsonContext.Readable.InputLogDto);
    }

    /// <summary>Reads a log from JSON.</summary>
    /// <param name="stream">The document stream.</param>
    /// <exception cref="InvalidDataException">The document is empty or malformed.</exception>
    public static InputLog Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        InputLogDto dto = JsonSerializer.Deserialize(stream, InputLogJsonContext.Readable.InputLogDto)
                          ?? throw new InvalidDataException("the input log document is empty.");
        return FromDto(dto);
    }

    /// <summary>Reads a log from a JSON string.</summary>
    /// <param name="json">The document text.</param>
    /// <exception cref="InvalidDataException">The document is empty or malformed.</exception>
    public static InputLog FromJson(string json)
    {
        InputLogDto dto = JsonSerializer.Deserialize(json, InputLogJsonContext.Readable.InputLogDto)
                          ?? throw new InvalidDataException("the input log document is empty.");
        return FromDto(dto);
    }

    private static InputLogDto ToDto(InputLog log)
    {
        InputLogHeader header = log.Header;
        Dictionary<string, string> policies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (RandomStreamId id in RandomStreamCatalog.All)
        {
            policies[RandomStreamCatalog.Name(id)] = header.Random.PolicyFor(id).ToString();
        }

        return new InputLogDto
        {
            Format = FormatTag,
            About = About,
            FormatVersion = header.FormatVersion,
            KernelEra = header.KernelEra,
            StepTicks = header.StepTicks,
            CompressionMode = header.CompressionMode.ToString(),
            FlightKernel = header.FlightKernel is { } kernel
                ? new FlightKernelEraDto
                {
                    Era = kernel.Era,
                    StepTicks = kernel.StepTicks,
                    DtPolicy = kernel.DtPolicy.ToString(),
                    StreamPolicy = kernel.StreamPolicy,
                    FieldSetHash = kernel.FieldSetHash,
                }
                : null,
            MasterSeed = Hex64(header.Random.MasterSeed),
            SimSeedWord = header.Random.SimSeedWord is { } word ? Hex16(word) : null,
            SimStreamMode = header.Random.SimMode.ToString(),
            StreamPolicies = policies,
            StepCount = header.StepCount,
            OpensIdleSteps = header.OpensIdleSteps,
            IdleStepCount = header.IdleStepCount,
            SourceIdleInstrThreshold = header.SourceIdleInstrThreshold,
            Notes = header.Notes,
            Events = log.Events.Select(ToDto).ToList(),
            Checkpoints = log.Checkpoints.Count == 0
                ? null
                : log.Checkpoints.Select(ToDto).ToList(),
        };
    }

    private static InputEventDto ToDto(InputEvent e) => e switch
    {
        KeyInputEvent key => new InputEventDto
        {
            Step = key.Step,
            Slot = key.Slot,
            Device = InputDevice.Key.ToString(),
            Scancode = Hex8(key.Scancode),
        },
        JoystickInputEvent stick => new InputEventDto
        {
            Step = stick.Step,
            Slot = stick.Slot,
            Device = InputDevice.Joystick.ToString(),
            AxisX = stick.AxisX,
            AxisY = stick.AxisY,
            Buttons = FlagNames((int)stick.Buttons, JoystickButtonNames),
        },
        MouseInputEvent mouse => new InputEventDto
        {
            Step = mouse.Step,
            Slot = mouse.Slot,
            Device = InputDevice.Mouse.ToString(),
            X = mouse.X,
            Y = mouse.Y,
            Buttons = FlagNames((int)mouse.Buttons, MouseButtonNames),
        },
        _ => throw new InvalidDataException($"unknown input event type {e.GetType().Name}"),
    };

    private static InputLogCheckpointDto ToDto(InputLogCheckpoint checkpoint) => new()
    {
        Step = checkpoint.Step,
        Clock = new TickClockStateDto
        {
            Step = checkpoint.Clock.Step,
            FrameTimeAccumulator = checkpoint.Clock.FrameTimeAccumulator,
            Compression = checkpoint.Clock.Compression.ToString(),
            CompressionMode = checkpoint.Clock.Mode.ToString(),
            MasterFrameCounter = (int)(checkpoint.Clock.FrameTimeAccumulator >> 8) & 0xFFFF,
            FrameCountScaled = (int)(checkpoint.Clock.FrameTimeAccumulator >> 6) & 0xFFFF,
        },
        Random = new RandomStreamsStateDto
        {
            SimMode = checkpoint.Random.SimMode.ToString(),
            Streams = checkpoint.Random.Streams.Select(s => new RandomStreamStateDto
            {
                Stream = s.Stream,
                Kind = s.Kind.ToString(),
                Word = s.Kind == RandomStreamKind.Lfsr16 ? Hex16(s.Word) : null,
                State = s.Kind == RandomStreamKind.Lfsr16
                    ? null
                    : [Hex64(s.S0), Hex64(s.S1), Hex64(s.S2), Hex64(s.S3)],
                Draws = s.Draws,
            }).ToList(),
        },
        SimStateHash = checkpoint.SimStateHash,
    };

    private static InputLog FromDto(InputLogDto dto)
    {
        if (!string.Equals(dto.Format, FormatTag, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"expected an input log tagged \"{FormatTag}\", found \"{dto.Format}\".");
        }

        Dictionary<RandomStreamId, RandomStreamPolicy> policies = new Dictionary<RandomStreamId, RandomStreamPolicy>();
        if (dto.StreamPolicies is { } table)
        {
            foreach ((string name, string policy) in table)
            {
                policies[RandomStreamCatalog.Parse(name)] = ParseEnum<RandomStreamPolicy>(policy);
            }
        }

        InputLogHeader header = new InputLogHeader
        {
            FormatVersion = dto.FormatVersion,
            KernelEra = dto.KernelEra ?? InputLogHeader.DetV1Era,
            StepTicks = dto.StepTicks == 0 ? TickClock.StepTicks : dto.StepTicks,
            CompressionMode = ParseEnum<TimeCompressionMode>(
                dto.CompressionMode ?? nameof(TimeCompressionMode.StepMultiplier)),
            FlightKernel = dto.FlightKernel is { } kernel
                ? new FlightKernelEra(
                    kernel.Era ?? InputLogHeader.DetV1Era,
                    kernel.StepTicks == 0 ? TickClock.StepTicks : kernel.StepTicks,
                    ParseEnum<DtPolicy>(kernel.DtPolicy ?? nameof(DtPolicy.TickClock)),
                    kernel.StreamPolicy ?? FlightKernelEra.KernelStreamPolicy,
                    kernel.FieldSetHash ?? string.Empty)
                : null,
            Random = new RandomStreamsConfig
            {
                MasterSeed = ParseHex64(dto.MasterSeed),
                SimSeedWord = dto.SimSeedWord is null ? null : (ushort)ParseHex64(dto.SimSeedWord),
                SimMode = ParseEnum<SimStreamMode>(dto.SimStreamMode ?? nameof(SimStreamMode.Split)),
                Policies = policies.Count == 0 ? null : policies,
            },
            StepCount = dto.StepCount,
            OpensIdleSteps = dto.OpensIdleSteps,
            IdleStepCount = dto.IdleStepCount,
            SourceIdleInstrThreshold = dto.SourceIdleInstrThreshold,
            Notes = dto.Notes,
        };

        List<InputEvent> events = (dto.Events ?? []).Select(FromDto).ToList();
        List<InputLogCheckpoint> checkpoints = (dto.Checkpoints ?? []).Select(FromDto).ToList();
        return new InputLog(header, events, checkpoints);
    }

    private static InputEvent FromDto(InputEventDto dto)
    {
        InputDevice device = ParseEnum<InputDevice>(dto.Device);
        return device switch
        {
            InputDevice.Key => new KeyInputEvent(dto.Step, dto.Slot, (byte)ParseHex64(dto.Scancode)),
            InputDevice.Joystick => new JoystickInputEvent(
                dto.Step,
                dto.Slot,
                dto.AxisX ?? JoystickInputEvent.AxisCentre,
                dto.AxisY ?? JoystickInputEvent.AxisCentre,
                (JoystickButtons)ParseFlags(dto.Buttons, JoystickButtonNames)),
            InputDevice.Mouse => new MouseInputEvent(
                dto.Step,
                dto.Slot,
                dto.X ?? 0,
                dto.Y ?? 0,
                (MouseButtons)ParseFlags(dto.Buttons, MouseButtonNames)),
            _ => throw new InvalidDataException($"unknown input device \"{dto.Device}\""),
        };
    }

    private static InputLogCheckpoint FromDto(InputLogCheckpointDto dto)
    {
        TickClockStateDto clockDto = dto.Clock
                                     ?? throw new InvalidDataException($"checkpoint at step {dto.Step} has no clock state.");
        RandomStreamsStateDto randomDto = dto.Random
                                          ?? throw new InvalidDataException($"checkpoint at step {dto.Step} has no random state.");

        TickClockState clock = new TickClockState(
            clockDto.Step,
            clockDto.FrameTimeAccumulator,
            ParseEnum<TimeCompression>(clockDto.Compression ?? nameof(TimeCompression.Off)),
            ParseEnum<TimeCompressionMode>(
                clockDto.CompressionMode ?? nameof(TimeCompressionMode.StepMultiplier)));

        List<RandomStreamSnapshot> streams = new List<RandomStreamSnapshot>();
        foreach (RandomStreamStateDto s in randomDto.Streams ?? [])
        {
            RandomStreamKind kind = ParseEnum<RandomStreamKind>(s.Kind);
            if (kind == RandomStreamKind.Lfsr16)
            {
                streams.Add(new RandomStreamSnapshot(
                    s.Stream ?? string.Empty, kind, (ushort)ParseHex64(s.Word), 0, 0, 0, 0, s.Draws));
            }
            else
            {
                List<string> words = s.State ?? throw new InvalidDataException(
                    $"stream \"{s.Stream}\" is xoshiro256** but carries no state array.");
                if (words.Count != 4)
                {
                    throw new InvalidDataException(
                        $"stream \"{s.Stream}\" carries {words.Count} state word(s); xoshiro256** has 4.");
                }

                streams.Add(new RandomStreamSnapshot(
                    s.Stream ?? string.Empty,
                    kind,
                    0,
                    ParseHex64(words[0]),
                    ParseHex64(words[1]),
                    ParseHex64(words[2]),
                    ParseHex64(words[3]),
                    s.Draws));
            }
        }

        return new InputLogCheckpoint(
            dto.Step,
            clock,
            new RandomStreamsState(
                ParseEnum<SimStreamMode>(randomDto.SimMode ?? nameof(SimStreamMode.Split)), streams),
            dto.SimStateHash);
    }

    private static readonly string[] JoystickButtonNames =
        [nameof(JoystickButtons.Trigger), nameof(JoystickButtons.Button2)];

    private static readonly string[] MouseButtonNames =
        [nameof(MouseButtons.Left), nameof(MouseButtons.Right), nameof(MouseButtons.Middle)];

    private static List<string> FlagNames(int mask, string[] names)
    {
        List<string> set = new List<string>();
        for (int bit = 0; bit < names.Length; bit++)
        {
            if ((mask & (1 << bit)) != 0)
            {
                set.Add(names[bit]);
            }
        }

        return set;
    }

    private static int ParseFlags(List<string>? set, string[] names)
    {
        int mask = 0;
        foreach (string name in set ?? [])
        {
            int bit = Array.FindIndex(names, n => string.Equals(n, name, StringComparison.Ordinal));
            if (bit < 0)
            {
                throw new InvalidDataException(
                    $"\"{name}\" is not one of {string.Join(", ", names)}");
            }

            mask |= 1 << bit;
        }

        return mask;
    }

    private static TEnum ParseEnum<TEnum>(string? text)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(text, ignoreCase: false, out TEnum value)
            ? value
            : throw new InvalidDataException(
                $"\"{text}\" is not a {typeof(TEnum).Name}; expected one of "
                    + string.Join(", ", Enum.GetNames<TEnum>()));

    private static string Hex8(byte value) =>
        "0x" + value.ToString("X2", CultureInfo.InvariantCulture);

    private static string Hex16(ushort value) =>
        "0x" + value.ToString("X4", CultureInfo.InvariantCulture);

    private static string Hex64(ulong value) =>
        "0x" + value.ToString("X16", CultureInfo.InvariantCulture);

    private static ulong ParseHex64(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("expected a hex value like \"0xABCD\", found nothing");
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value)
            ? value
            : throw new InvalidDataException($"\"{text}\" is not a hex value like \"0xABCD\"");
    }
}
