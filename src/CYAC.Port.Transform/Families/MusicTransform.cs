using System.Globalization;
using System.Text.Json;
using CYAC.Formats.Audio;
using CYAC.Port.Core.Data;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// The three <c>.SNG</c> music streams of <c>1b.lib</c> → <c>audio/music/&lt;name&gt;.json</c>: the
/// event stream, named, in the dialect its driver parses.
/// </summary>
/// <remarks>
/// <para>
/// There is no single <c>.SNG</c> format: each driver's sequencer parses its own MIDI-flavoured
/// dialect, and the engine picks the file to match the driver it loaded (<c>g_audio_sng_name_table
/// [0x349A]</c> keyed by <c>[0xE482]</c>).
/// </para>
/// <para>
/// <b>Every byte becomes exactly one event</b>, so the round trip is <c>exact</c> with zero unknown
/// bytes: a status byte plus, where the dialect says so, one operand. Where several status nibbles
/// mean the same thing (the ibm/cms dispatcher treats 0x90/0xA0/0xB0 alike, and anything below 0x90
/// as a note-off) the event records the nibble it was written with, so a re-emission cannot
/// normalise the stream behind the listener's back.
/// </para>
/// <para>
/// The three files are three arrangements of one ~35.8k-tick piece.
/// </para>
/// </remarks>
public sealed class MusicTransform : IFamilyTransform
{
    /// <summary>The family name.</summary>
    public const string FamilyName = "sng";

    /// <summary>The data tree folder this family writes into.</summary>
    public const string Folder = "audio/music";

    /// <inheritdoc/>
    public string Family => FamilyName;

    /// <inheritdoc/>
    public string TreeDescription =>
        "`audio/music/<name>.json` — a .SNG music stream as a named event list (delay / note_on / " +
        "note_off / program / volume / loop) in the dialect its driver parses; every byte of the " +
        "asset is one event, so the list re-emits it exactly.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is not null
            && source.Extension == ".sng"
            && SngSequence.DialectForAsset(source.Name) is not null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        SngDialect dialect = SngSequence.DialectForAsset(source.Name)
                             ?? throw new InvalidDataException(
                                 $"{source}: no .SNG dialect is documented for this asset name; the grammar is " +
                                 "per-driver and cannot be guessed");

        IReadOnlyList<SngEvent> events = SngSequence.Parse(source.Content.Span, dialect);
        MusicStreamDto dto = Describe(source, dialect, events);
        string path = context.Allocate($"{Folder}/{TransformContext.SafeFileName(source.Stem)}.json");

        return
        [
            new TransformOutput(
                path,
                JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.MusicStreamDto),
                OutputRole.Data,
                OutputFidelity.Exact),
        ];
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput json = outputs.FirstOrDefault(o => o.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("a music stream expects one .json output");
        return ToBytes(json.Bytes);
    }

    /// <summary>Rebuilds the <c>.SNG</c> bytes from the tree's JSON.</summary>
    /// <param name="json">The <c>audio/music/&lt;name&gt;.json</c> bytes.</param>
    /// <exception cref="InvalidDataException">The document is malformed.</exception>
    public static byte[] ToBytes(ReadOnlySpan<byte> json)
    {
        MusicStreamDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.MusicStreamDto)
                             ?? throw new InvalidDataException("music JSON is empty");
        if (!Enum.TryParse<SngDialect>(dto.Dialect, ignoreCase: true, out SngDialect dialect))
        {
            throw new InvalidDataException($"\"{dto.Dialect}\" is not a known .SNG dialect");
        }

        List<SngEvent> events = new List<SngEvent>();
        foreach (MusicEventDto e in dto.Events ?? [])
        {
            events.Add(ToEvent(e, dialect));
        }

        return SngSequence.Emit(events, dialect);
    }

    private static SngEvent ToEvent(MusicEventDto dto, SngDialect dialect)
    {
        SngEventKind kind = KindOf(dto.Event)
                            ?? throw new InvalidDataException($"\"{dto.Event}\" is not a known .SNG event");

        byte high = dto.StatusHigh is null
            ? SngSequence.CanonicalStatusHigh(dialect, kind)
            : (byte)(int.Parse(
                dto.StatusHigh.AsSpan(dto.StatusHigh.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 0xF0);
        if (high == 0xFF)
        {
            throw new InvalidDataException(
                $"a {kind} event in the {dialect} dialect has no canonical status nibble, so it must " +
                "carry \"statusHigh\"");
        }

        int channel = kind == SngEventKind.Delay
            ? ((dto.Ticks ?? 0) >> 8) & 0x0F
            : dto.Channel ?? 0;

        int? operand = kind switch
        {
            SngEventKind.Delay => (dto.Ticks ?? 0) & 0xFF,
            SngEventKind.Program => dto.Program,
            SngEventKind.Volume => dto.ControllerValue,
            SngEventKind.NoteOn or SngEventKind.NoteOff => dto.Note,
            _ => null,
        };

        return new SngEvent(0, kind, high, channel, operand);
    }

    private static MusicStreamDto Describe(
        TransformSource source, SngDialect dialect, IReadOnlyList<SngEvent> events)
    {
        SortedDictionary<string, int> counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int ticks = 0;
        foreach (SngEvent e in events)
        {
            string name = EventName(e.Kind);
            counts[name] = counts.TryGetValue(name, out int n) ? n + 1 : 1;
            if (e.Kind == SngEventKind.Delay)
            {
                ticks += e.Ticks;
            }
        }

        List<MusicEventDto> rows = new List<MusicEventDto>(events.Count);
        foreach (SngEvent e in events)
        {
            byte canonical = SngSequence.CanonicalStatusHigh(dialect, e.Kind);
            rows.Add(new MusicEventDto
            {
                Event = EventName(e.Kind),
                Channel = e.Kind == SngEventKind.Delay ? null : e.Channel,
                Ticks = e.Kind == SngEventKind.Delay ? e.Ticks : null,
                Note = e.Kind is SngEventKind.NoteOn or SngEventKind.NoteOff ? e.Operand : null,
                Program = e.Kind == SngEventKind.Program ? e.Operand : null,
                ControllerValue = e.Kind == SngEventKind.Volume ? e.Operand : null,
                ChannelVolume = e.Kind == SngEventKind.Volume && e.Operand is int v
                    ? (0x7F - v) >> 1
                    : null,
                StatusHigh = e.StatusHigh == canonical ? null : $"0x{e.StatusHigh:X2}",
                Offset = $"0x{e.Offset:X4}",
            });
        }

        return new MusicStreamDto
        {
            Format = "cyac.music/1",
            About =
                "One .SNG music stream as an ordered event list. The grammar is PER DRIVER — this " +
                "file records which one it is written in. `ticks` are sequencer ticks (driver cmd 6); " +
                "`note` is the byte the driver indexes its pitch table with, so the sounding pitch is " +
                "`note - noteBase` semitones above the table's first entry. `_`-prefixed fields are " +
                "derived and ignored on the way back; `statusHigh` appears only where the stream uses " +
                "a non-canonical spelling of an event and must then be kept.",
            Source = $"{source.OriginFile}/{source.Name}",
            Dialect = dialect.ToString().ToLowerInvariant(),
            DriverModule = SngSequence.DriverModule(dialect),
            NoteBase = SngSequence.NoteBase(dialect),
            Grammar = Grammar(dialect),
            TotalTicks = ticks,
            EventCounts = counts,
            Events = rows,
        };
    }

    private static SngEventKind? KindOf(string? name) => name switch
    {
        "delay" => SngEventKind.Delay,
        "loop" => SngEventKind.Loop,
        "program" => SngEventKind.Program,
        "note_on" => SngEventKind.NoteOn,
        "note_off" => SngEventKind.NoteOff,
        "volume" => SngEventKind.Volume,
        "ignored" => SngEventKind.Ignored,
        _ => null,
    };

    private static string EventName(SngEventKind kind) => kind switch
    {
        SngEventKind.Delay => "delay",
        SngEventKind.Loop => "loop",
        SngEventKind.Program => "program",
        SngEventKind.NoteOn => "note_on",
        SngEventKind.NoteOff => "note_off",
        SngEventKind.Volume => "volume",
        _ => "ignored",
    };

    private static IReadOnlyList<string> Grammar(SngDialect dialect) => dialect switch
    {
        SngDialect.Ibm =>
        [
            "0xFn lo    delay ((n<<8)|lo) ticks",
            "0xEn       loop to the loop start",
            "0xCn/0xDn prog   program change on channel n",
            "0x9n/0xAn/0xBn note   note on, channel n (no velocity byte)",
            "below 0x90 note off for channel n, no operand",
            "sequencer: asset:1b/ibmdrive.drv@0x0A12",
        ],
        SngDialect.Adl =>
        [
            "0xFn lo    delay ((n<<8)|lo) ticks",
            "0xEn       loop to the loop start",
            "0xDn       consumed and discarded",
            "0xCn prog  program change on channel n",
            "0xBn v     channel volume; the driver stores (0x7F-v)>>1",
            "0xAn       consumed and discarded",
            "0x9n note  note on (channels 0xB+ are rhythm slots and take NO note byte)",
            "0x8n note  note off (channels 0xB+ take no note byte)",
            "below 0x80 consumed and discarded",
            "handler: asset:1b/adldrive.drv@0x12E4, rhythm at @0x144F/@0x14D3",
        ],
        SngDialect.Cms =>
        [
            "0xFn lo    delay ((n<<8)|lo) ticks",
            "0xEn       loop to the loop start",
            "0xCn/0xDn prog   program change on channel n",
            "0x9n/0xAn/0xBn note   note on, channel n",
            "below 0x90 note off for channel n, no operand",
            "sequencer: asset:1b/tnddrive.drv@0x17D2; notes below the driver base are dropped",
        ],
        _ => [],
    };
}
