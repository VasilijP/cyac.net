using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Sim;

// Wire format of a `.rpy` input log (proposal D3-e).  JSON, not a compact binary: the log is a
// DOCUMENT a person reads and hand-edits when a replay misbehaves ("step 5,652, key 0x1C make" is a
// debuggable line; a hex blob is not), and the same transform-plan principle (L5) that put
// hex-string addresses in the data tree applies here.  Size is a non-issue at the observed
// densities: the parity recording is 11 events over 29,545 steps.  A compact form can be added
// later behind the same codec if a session ever produces millions of samples.
//
// Field names are the domain names in camelCase; enums are strings; 16/64-bit seeds are "0x…"
// strings, magnitudes are numbers.

/// <summary>Wire format of one input landmark.</summary>
#pragma warning disable CS1591
public sealed class InputEventDto
{
    [JsonPropertyName("step")]
    public uint Step { get; init; }

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    [JsonPropertyName("device")]
    public string? Device { get; init; }

    /// <summary>Key events: the scancode, break flag in bit 7.</summary>
    [JsonPropertyName("scancode")]
    public string? Scancode { get; init; }

    /// <summary>Joystick events: axis X, 0..1023 (512 = centre).</summary>
    [JsonPropertyName("axisX")]
    public int? AxisX { get; init; }

    /// <summary>Joystick events: axis Y, 0..1023 (512 = centre).</summary>
    [JsonPropertyName("axisY")]
    public int? AxisY { get; init; }

    /// <summary>Mouse events: screen X, 0..319.</summary>
    [JsonPropertyName("x")]
    public int? X { get; init; }

    /// <summary>Mouse events: screen Y, 0..199.</summary>
    [JsonPropertyName("y")]
    public int? Y { get; init; }

    /// <summary>Pressed buttons, by name.</summary>
    [JsonPropertyName("buttons")]
    public List<string>? Buttons { get; init; }
}

/// <summary>Wire format of one stream's generator state inside a checkpoint.</summary>
public sealed class RandomStreamStateDto
{
    [JsonPropertyName("stream")]
    public string? Stream { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>LFSR16 streams: the 16-bit state word.</summary>
    [JsonPropertyName("word")]
    public string? Word { get; init; }

    /// <summary>xoshiro256** streams: the four state words.</summary>
    [JsonPropertyName("state")]
    public List<string>? State { get; init; }

    /// <summary>Bits drawn (LFSR) or 64-bit draws made (xoshiro).</summary>
    [JsonPropertyName("draws")]
    public long Draws { get; init; }
}

/// <summary>Wire format of the whole random kernel inside a checkpoint.</summary>
public sealed class RandomStreamsStateDto
{
    [JsonPropertyName("simMode")]
    public string? SimMode { get; init; }

    [JsonPropertyName("streams")]
    public List<RandomStreamStateDto>? Streams { get; init; }
}

/// <summary>Wire format of the tick clock inside a checkpoint.</summary>
public sealed class TickClockStateDto
{
    [JsonPropertyName("step")]
    public ulong Step { get; init; }

    [JsonPropertyName("frameTimeAccumulator")]
    public uint FrameTimeAccumulator { get; init; }

    [JsonPropertyName("compression")]
    public string? Compression { get; init; }

    [JsonPropertyName("compressionMode")]
    public string? CompressionMode { get; init; }

    /// <summary>Derived from the accumulator; written for readability, ignored on read.</summary>
    [JsonPropertyName("masterFrameCounter")]
    public int? MasterFrameCounter { get; init; }

    /// <summary>Derived from the accumulator; written for readability, ignored on read.</summary>
    [JsonPropertyName("frameCountScaled")]
    public int? FrameCountScaled { get; init; }
}

/// <summary>Wire format of a replay checkpoint.</summary>
public sealed class InputLogCheckpointDto
{
    [JsonPropertyName("step")]
    public uint Step { get; init; }

    [JsonPropertyName("clock")]
    public TickClockStateDto? Clock { get; init; }

    [JsonPropertyName("random")]
    public RandomStreamsStateDto? Random { get; init; }

    [JsonPropertyName("simStateHash")]
    public string? SimStateHash { get; init; }
}

/// <summary>Wire format of the flight kernel's era descriptor inside a log header.</summary>
public sealed class FlightKernelEraDto
{
    [JsonPropertyName("era")]
    public string? Era { get; init; }

    [JsonPropertyName("stepTicks")]
    public int StepTicks { get; init; }

    [JsonPropertyName("dtPolicy")]
    public string? DtPolicy { get; init; }

    [JsonPropertyName("streamPolicy")]
    public string? StreamPolicy { get; init; }

    /// <summary>The first 8 bytes of the SHA-256 of the kernel's field-set manifest.</summary>
    [JsonPropertyName("fieldSetHash")]
    public string? FieldSetHash { get; init; }
}

/// <summary>Wire format of a whole input log — <c>&lt;name&gt;.rpy</c>.</summary>
public sealed class InputLogDto
{
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("about")]
    public string? About { get; init; }

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; }

    [JsonPropertyName("kernelEra")]
    public string? KernelEra { get; init; }

    [JsonPropertyName("stepTicks")]
    public int StepTicks { get; init; }

    [JsonPropertyName("compressionMode")]
    public string? CompressionMode { get; init; }

    /// <summary><see cref="InputLogHeader.FlightKernel"/>.</summary>
    [JsonPropertyName("flightKernel")]
    public FlightKernelEraDto? FlightKernel { get; init; }

    [JsonPropertyName("masterSeed")]
    public string? MasterSeed { get; init; }

    [JsonPropertyName("simSeedWord")]
    public string? SimSeedWord { get; init; }

    [JsonPropertyName("simStreamMode")]
    public string? SimStreamMode { get; init; }

    [JsonPropertyName("streamPolicies")]
    public Dictionary<string, string>? StreamPolicies { get; init; }

    [JsonPropertyName("stepCount")]
    public uint? StepCount { get; init; }

    /// <summary><see cref="InputLogHeader.OpensIdleSteps"/>.</summary>
    [JsonPropertyName("opensIdleSteps")]
    public bool OpensIdleSteps { get; init; }

    /// <summary><see cref="InputLogHeader.IdleStepCount"/>.</summary>
    [JsonPropertyName("idleStepCount")]
    public uint? IdleStepCount { get; init; }

    /// <summary><see cref="InputLogHeader.SourceIdleInstrThreshold"/>.</summary>
    [JsonPropertyName("sourceIdleInstrThreshold")]
    public uint? SourceIdleInstrThreshold { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("events")]
    public List<InputEventDto>? Events { get; init; }

    [JsonPropertyName("checkpoints")]
    public List<InputLogCheckpointDto>? Checkpoints { get; init; }
}
#pragma warning restore CS1591

/// <summary>
/// The source-generated serialisation context for input logs — the same no-reflection doctrine as
/// <c>Data/PortDataJson.cs</c>, kept separate because a replay is not a data-tree document.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InputLogDto))]
public sealed partial class InputLogJsonContext : JsonSerializerContext
{
    private static readonly JsonSerializerOptions ReadableOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The context every input log is written and read through.</summary>
    public static InputLogJsonContext Readable { get; } = new(ReadableOptions);
}
