using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

// Wire formats for the three audio folders of the data tree (transform-).  They live beside the
// runtime (CYAC.Port.Core reads these documents) and the tool writes them through the same
// declarations, exactly as T5 placed the exe-table and aircraft wire formats here.
//
//   audio/speech/<name>.wav  + <name>.json   .SND speech clips  (SpeechTransform)
//   audio/music/<name>.json                  .SNG music streams (MusicTransform)
//   audio/drivers/<name>.json + <name>.code.bin   .DRV / .SP modules (SoundDriverTransform)
//
// Domain names throughout; `_`-prefixed fields are DERIVED annotations for readers and are ignored
// on the way back (the same convention T4 established for the mission documents).

/// <summary>Wire format of <c>audio/speech/&lt;name&gt;.json</c> — a <c>.SND</c> clip's header.</summary>
public sealed class SpeechClipDto
{
    /// <summary>Document format tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is and how it is edited.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>Where the bytes came from, e.g. <c>4a.lib/WELCOME1.SND</c>.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>The sibling file holding the samples.</summary>
    [JsonPropertyName("audioFile")]
    public string? AudioFile { get; init; }

    /// <summary>Header <c>+0x00</c> — the device play mode (0 = unsigned 8-bit PCM).</summary>
    [JsonPropertyName("mode")]
    public int Mode { get; init; }

    /// <summary>The mode's name, derived.</summary>
    [JsonPropertyName("_modeName")]
    public string? ModeName { get; init; }

    /// <summary>Header <c>+0x06</c> — the sample rate in Hz.</summary>
    [JsonPropertyName("sampleRate")]
    public int SampleRate { get; init; }

    /// <summary>Header <c>+0x02</c> — the sample count the module schedules.</summary>
    [JsonPropertyName("sampleCount")]
    public long SampleCount { get; init; }

    /// <summary>The clip's duration in milliseconds, derived.</summary>
    [JsonPropertyName("_durationMs")]
    public int DurationMs { get; init; }

    /// <summary>Bytes past the declared length, when the file carries any (none ship).</summary>
    [JsonPropertyName("unknown_trailing")]
    public string? UnknownTrailing { get; init; }

    /// <summary>
    /// When the payload is not mode 0, the samples are carried verbatim here instead of in a WAV
    /// (the module's mode-1 hardware-ADPCM path has never shipped and is not decoded).
    /// </summary>
    [JsonPropertyName("payloadFile")]
    public string? PayloadFile { get; init; }
}

/// <summary>One decoded <c>.SNG</c> event.</summary>
public sealed class MusicEventDto
{
    /// <summary>What the sequencer does: <c>delay</c>, <c>loop</c>, <c>program</c>, …</summary>
    [JsonPropertyName("event")]
    public string? Event { get; init; }

    /// <summary>The status byte's low nibble — the channel (absent on a delay).</summary>
    [JsonPropertyName("channel")]
    public int? Channel { get; init; }

    /// <summary>Delay only: the tick count.</summary>
    [JsonPropertyName("ticks")]
    public int? Ticks { get; init; }

    /// <summary>Note events: the note byte the driver indexes its pitch table with.</summary>
    [JsonPropertyName("note")]
    public int? Note { get; init; }

    /// <summary>Program change: the instrument number.</summary>
    [JsonPropertyName("program")]
    public int? Program { get; init; }

    /// <summary>AdLib volume: the operand byte; the driver stores <c>(0x7F-v)&gt;&gt;1</c>.</summary>
    [JsonPropertyName("controllerValue")]
    public int? ControllerValue { get; init; }

    /// <summary>The channel volume the driver derives, derived.</summary>
    [JsonPropertyName("_channelVolume")]
    public int? ChannelVolume { get; init; }

    /// <summary>
    /// The status byte's high nibble, present only when it is NOT the canonical spelling for the
    /// event (several nibbles map to one action, and a note-off has no canonical nibble at all).
    /// </summary>
    [JsonPropertyName("statusHigh")]
    public string? StatusHigh { get; init; }

    /// <summary>The status byte's offset in the stream, derived.</summary>
    [JsonPropertyName("_offset")]
    public string? Offset { get; init; }
}

/// <summary>Wire format of <c>audio/music/&lt;name&gt;.json</c> — a <c>.SNG</c> event stream.</summary>
public sealed class MusicStreamDto
{
    /// <summary>Document format tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is and how it is edited.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>Where the bytes came from, e.g. <c>1b.lib/yeagadl.sng</c>.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>Which driver's sequencer reads it: <c>ibm</c> / <c>adl</c> / <c>cms</c>.</summary>
    [JsonPropertyName("dialect")]
    public string? Dialect { get; init; }

    /// <summary>The <c>.DRV</c> module that parses this dialect.</summary>
    [JsonPropertyName("driverModule")]
    public string? DriverModule { get; init; }

    /// <summary>The MIDI note number the driver's pitch table starts at.</summary>
    [JsonPropertyName("noteBase")]
    public int NoteBase { get; init; }

    /// <summary>The dialect's grammar, spelled out for a reader.</summary>
    [JsonPropertyName("_grammar")]
    public IReadOnlyList<string>? Grammar { get; init; }

    /// <summary>Total delay ticks from the start to the loop, derived.</summary>
    [JsonPropertyName("_totalTicks")]
    public int TotalTicks { get; init; }

    /// <summary>How many events of each kind, derived.</summary>
    [JsonPropertyName("_eventCounts")]
    public IReadOnlyDictionary<string, int>? EventCounts { get; init; }

    /// <summary>The event stream, in order. Re-emitting it reproduces the asset byte for byte.</summary>
    [JsonPropertyName("events")]
    public IReadOnlyList<MusicEventDto>? Events { get; init; }
}

/// <summary>One command of a <c>.DRV</c> driver's 16-entry dispatch table.</summary>
public sealed class DriverCommandDto
{
    /// <summary>The even command code, as hex.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    /// <summary>The command's name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>What the routine does.</summary>
    [JsonPropertyName("meaning")]
    public string? Meaning { get; init; }

    /// <summary>The routine's in-driver near pointer, as hex.</summary>
    [JsonPropertyName("routineInDriver")]
    public string? RoutineInDriver { get; init; }

    /// <summary>The same routine as a file offset into this module, as hex.</summary>
    [JsonPropertyName("routineFileOffset")]
    public string? RoutineFileOffset { get; init; }

    /// <summary>Whether CYAC ever issues this command (five are dormant library features).</summary>
    [JsonPropertyName("sentByGame")]
    public bool SentByGame { get; init; }
}

/// <summary>One export of a <c>.SP</c> speech module's header.</summary>
public sealed class SpeechExportDto
{
    /// <summary>Its index in the 6-word header.</summary>
    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    /// <summary>The export's name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Its module-relative offset, as hex.</summary>
    [JsonPropertyName("offset")]
    public string? Offset { get; init; }

    /// <summary>False for the busy-byte slot, which is a data offset rather than an entry point.</summary>
    [JsonPropertyName("isCode")]
    public bool IsCode { get; init; }

    /// <summary>Whether the engine's thunk dispatcher ever calls it.</summary>
    [JsonPropertyName("dispatchedByGame")]
    public bool DispatchedByGame { get; init; }
}

/// <summary>A <c>.SP</c> module's compiled-in device descriptor.</summary>
public sealed class SpeechDescriptorDto
{
    /// <summary>Which descriptor: <c>pcm</c> (module+0x1FF) or <c>adpcm</c> (module+0x209).</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>Its module-relative address, as hex.</summary>
    [JsonPropertyName("at")]
    public string? At { get; init; }

    /// <summary>The detect routine's offset, as hex.</summary>
    [JsonPropertyName("detect")]
    public string? Detect { get; init; }

    /// <summary>The stop routine's offset, as hex.</summary>
    [JsonPropertyName("stop")]
    public string? Stop { get; init; }

    /// <summary>The play routine's offset, as hex.</summary>
    [JsonPropertyName("play")]
    public string? Play { get; init; }

    /// <summary>Device parameter 0 — <c>init</c>'s first argument overwrites it when non-zero.</summary>
    [JsonPropertyName("param0")]
    public string? Param0 { get; init; }

    /// <summary>Device parameter 1 — <c>init</c>'s second argument overwrites it when non-zero.</summary>
    [JsonPropertyName("param1")]
    public string? Param1 { get; init; }

    /// <summary>What the two parameters mean for this device, where the project documents it.</summary>
    [JsonPropertyName("_params")]
    public string? Params { get; init; }
}

/// <summary>Wire format of <c>audio/drivers/&lt;name&gt;.json</c> — a <c>.DRV</c> or <c>.SP</c> module.</summary>
public sealed class AudioDriverDto
{
    /// <summary>Document format tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is, and why the body is not converted.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>Where the bytes came from, e.g. <c>1b.lib/adldrive.drv</c>.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary><c>music_driver</c> (a <c>.DRV</c>) or <c>speech_driver</c> (a <c>.SP</c>).</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The device this module drives, from the project's driver tables.</summary>
    [JsonPropertyName("device")]
    public string? Device { get; init; }

    /// <summary>The build banner in the module's data, when it carries one.</summary>
    [JsonPropertyName("banner")]
    public string? Banner { get; init; }

    /// <summary>What this project knows about the module beyond its ABI.</summary>
    [JsonPropertyName("_identification")]
    public IReadOnlyList<string>? Identification { get; init; }

    /// <summary>The recorded machine code, as a sibling file.</summary>
    [JsonPropertyName("codeFile")]
    public string? CodeFile { get; init; }

    /// <summary>How many bytes that file holds.</summary>
    [JsonPropertyName("codeBytes")]
    public int CodeBytes { get; init; }

    /// <summary>Its SHA-256, so an edited body is a loud failure rather than a silent one.</summary>
    [JsonPropertyName("codeSha256")]
    public string? CodeSha256 { get; init; }

    // -- .DRV container -------------------------------------------------------------------------

    /// <summary><c>.DRV</c> <c>+0x00</c> — the declared module size, header included.</summary>
    [JsonPropertyName("declaredSize")]
    public int? DeclaredSize { get; init; }

    /// <summary><c>.DRV</c> <c>+0x02</c> — the entry offset.</summary>
    [JsonPropertyName("entryOffset")]
    public string? EntryOffset { get; init; }

    /// <summary><c>.DRV</c> <c>+0x04</c> — the dispatcher's <c>retf</c> offset.</summary>
    [JsonPropertyName("retfOffset")]
    public string? RetfOffset { get; init; }

    /// <summary><c>.DRV</c> <c>+0x06..0x0F</c> — reserved header bytes; zero in all four shipped drivers.</summary>
    [JsonPropertyName("reserved_0x06")]
    public string? Reserved { get; init; }

    /// <summary>The in-driver offset the near-pointer dispatch table starts at, as hex.</summary>
    [JsonPropertyName("dispatchTableInDriver")]
    public string? DispatchTableInDriver { get; init; }

    /// <summary>The 16 commands, in code order.</summary>
    [JsonPropertyName("commands")]
    public IReadOnlyList<DriverCommandDto>? Commands { get; init; }

    // -- .SP export header ----------------------------------------------------------------------

    /// <summary><c>.SP</c> — the six export-header words.</summary>
    [JsonPropertyName("exports")]
    public IReadOnlyList<SpeechExportDto>? Exports { get; init; }

    /// <summary><c>.SP</c> — the compiled-in device descriptors.</summary>
    [JsonPropertyName("descriptors")]
    public IReadOnlyList<SpeechDescriptorDto>? Descriptors { get; init; }
}
