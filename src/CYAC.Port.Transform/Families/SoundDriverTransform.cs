using System.Security.Cryptography;
using System.Text.Json;
using CYAC.Formats.Audio;
using CYAC.Port.Core.Data;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// The nine loadable audio modules of <c>1b.lib</c> — four <c>.DRV</c> music drivers and five
/// <c>.SP</c> speech backends → <c>audio/drivers/&lt;name&gt;.json</c> plus a recorded
/// <c>&lt;name&gt;.code.bin</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Law L6 — code is not data.</b> These modules are 8086 machine code that the engine loads from
/// the archive and far-calls blind. The transform does not convert them: it records the body and
/// publishes the ABI, which is what the port consumes (the port re-implements the drivers; it never
/// runs these bytes). Their bytes are counted as <c>code</c>, not as unknown and not as unexplained —
/// they are explained: the document says what the module is and how every entry point is called.
/// </para>
/// <para>
/// <b><c>.DRV</c></b> (2): a 16-byte container header, one far entry that bounds-checks a command code
/// and dispatches through a 16-entry near-pointer table, and a per-device synthesis engine. Eleven of
/// the sixteen commands are issued by CYAC; the other five are the library's standalone mode (its own
/// INT 8 hook, direct-MIDI feed, status polling) and are dormant here.
/// </para>
/// <para>
/// <b><c>.SP</c></b>: a 12-byte export header, byte-identical in all five modules, naming
/// <c>init</c>/<c>play</c>/<c>stop</c>/ <c>wait</c>/<c>poll</c> and the busy-byte offset. Only modes
/// 0/1/2 are ever dispatched, so only the first three are ever called; between play and stop the
/// engine reads exactly one module byte.
/// </para>
/// </remarks>
public sealed class SoundDriverTransform : IFamilyTransform
{
    /// <summary>The family name.</summary>
    public const string FamilyName = "audio-driver";

    /// <summary>The data tree folder this family writes into.</summary>
    public const string Folder = "audio/drivers";

    /// <inheritdoc/>
    public string Family => FamilyName;

    /// <inheritdoc/>
    public string TreeDescription =>
        "`audio/drivers/<name>.json` — a loadable .DRV music driver or .SP speech backend: its " +
        "container header, its export/command ABI and what the project knows about it. The body is " +
        "8086 MACHINE CODE and is recorded verbatim in `<name>.code.bin`, never converted; " +
        "the port re-implements these drivers rather than running them.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is not null
            && (SoundDriverModule.IsDriverAsset(source.Name) || SpeechDriverModule.IsSpeechAsset(source.Name));
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        string stem = TransformContext.SafeFileName(source.Stem);
        string jsonPath = context.Allocate($"{Folder}/{stem}.json");
        string codePath = context.Allocate($"{Folder}/{stem}.code.bin");

        return SoundDriverModule.IsDriverAsset(source.Name)
            ? ForwardMusicDriver(source, jsonPath, codePath)
            : ForwardSpeechDriver(source, jsonPath, codePath);
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput json = outputs.FirstOrDefault(o => o.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("an audio driver expects one .json output");
        AudioDriverDto dto = JsonSerializer.Deserialize(json.Bytes, PortDataJsonContext.Readable.AudioDriverDto)
                             ?? throw new InvalidDataException($"{json.Path} is empty");

        string wanted = dto.CodeFile
            ?? throw new InvalidDataException($"{json.Path} names no code file");
        LoadedOutput code = outputs.FirstOrDefault(
                                o => string.Equals(Path.GetFileName(o.Path), wanted, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException($"{json.Path} names \"{wanted}\", which is not in the tree");

        if (dto.CodeBytes != code.Bytes.Length)
        {
            throw new InvalidDataException(
                $"{code.Path} is {code.Bytes.Length} bytes but {json.Path} says {dto.CodeBytes}");
        }

        if (dto.Kind == MusicDriverKind)
        {
            return SoundDriverModule.FromParts(
                Path.GetFileName(json.Path),
                dto.DeclaredSize ?? (SoundDriverModule.HeaderBytes + code.Bytes.Length),
                PortHex.ParseOrDefault(dto.EntryOffset, SoundDriverModule.ShippedEntryOffset),
                PortHex.ParseOrDefault(dto.RetfOffset, SoundDriverModule.ShippedRetfOffset),
                PortHex.BytesOrZero(dto.Reserved, SoundDriverModule.HeaderBytes - 6),
                code.Bytes).ToBytes();
        }

        List<int> offsets = (dto.Exports ?? throw new InvalidDataException($"{json.Path} lists no exports"))
            .OrderBy(e => e.Slot)
            .Select(e => PortHex.Parse(e.Offset))
            .ToList();
        return SpeechDriverModule.FromParts(Path.GetFileName(json.Path), offsets, code.Bytes).ToBytes();
    }

    private const string MusicDriverKind = "music_driver";
    private const string SpeechDriverKind = "speech_driver";

    private static IReadOnlyList<TransformOutput> ForwardMusicDriver(
        TransformSource source, string jsonPath, string codePath)
    {
        SoundDriverModule module = SoundDriverModule.Parse(source.Content.Span, source.Name);
        UnknownBytes unknown = new UnknownBytes();
        unknown.AddIfNonZero(6, module.Reserved);

        List<DriverCommandDto> commands = new List<DriverCommandDto>(SoundDriverModule.DispatchTableEntries);
        foreach (SoundDriverCommand command in SoundDriverModule.Commands)
        {
            int inDriver = module.DispatchTable[command.Code / 2];
            commands.Add(new DriverCommandDto
            {
                Code = $"0x{command.Code:X2}",
                Name = command.Name,
                Meaning = command.Meaning,
                RoutineInDriver = $"0x{inDriver:X4}",
                RoutineFileOffset = $"0x{inDriver + SoundDriverModule.HeaderBytes:X4}",
                SentByGame = command.SentByGame,
            });
        }

        AudioDriverDto dto = new AudioDriverDto
        {
            Format = "cyac.audio-driver/1",
            About =
                "One loadable .DRV music driver. The engine loads the whole file at buf_seg:0 and " +
                "far-calls {buf_seg + ((entryOffset+0xF)>>4), 0} with the command code in [bp+6]; the " +
                "entry bounds-checks it against 0x20 and dispatches through the 16-entry near-pointer " +
                "table, so a near pointer inside the module is a FILE offset minus 0x10. The body is " +
                "machine code and is recorded, not converted: the port re-implements the " +
                "driver from this ABI and the sound-design tables the round-97/98 census documents.",
            Source = $"{source.OriginFile}/{source.Name}",
            Kind = MusicDriverKind,
            Device = MusicDriverDevice(source.Stem),
            Banner = module.Banner,
            Identification = MusicDriverNotes(source.Stem, module),
            CodeFile = Path.GetFileName(codePath),
            CodeBytes = module.Body.Length,
            CodeSha256 = Sha256(module.Body),
            DeclaredSize = module.DeclaredSize,
            EntryOffset = $"0x{module.EntryOffset:X4}",
            RetfOffset = $"0x{module.RetfOffset:X4}",
            Reserved = PortHex.Bytes(module.Reserved),
            DispatchTableInDriver = $"0x{SoundDriverModule.DispatchTableInDriverOffset:X2}",
            Commands = commands,
        };

        return
        [
            new TransformOutput(
                jsonPath,
                JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.AudioDriverDto),
                OutputRole.Data,
                OutputFidelity.Exact,
                null,
                unknown.Count),
            new TransformOutput(
                codePath,
                module.Body,
                OutputRole.Code,
                OutputFidelity.Exact,
                "8086 machine code, recorded verbatim; the sibling .json names its ABI",
                0,
                0,
                module.Body.Length),
        ];
    }

    private static IReadOnlyList<TransformOutput> ForwardSpeechDriver(
        TransformSource source, string jsonPath, string codePath)
    {
        SpeechDriverModule module = SpeechDriverModule.Parse(source.Content.Span, source.Name);

        List<SpeechExportDto> exports = module.Exports
            .Select(e => new SpeechExportDto
            {
                Slot = e.Slot,
                Name = e.Name,
                Offset = $"0x{e.Offset:X4}",
                IsCode = e.IsCode,
                DispatchedByGame = e.DispatchedByGame,
            })
            .ToList();

        List<SpeechDescriptorDto> descriptors = new List<SpeechDescriptorDto>(2);
        AddDescriptor(descriptors, module, "pcm", SpeechDriverModule.DescriptorOffset, source.Stem);
        AddDescriptor(descriptors, module, "adpcm", SpeechDriverModule.AdpcmDescriptorOffset, source.Stem);

        AudioDriverDto dto = new AudioDriverDto
        {
            Format = "cyac.audio-driver/1",
            About =
                "One .SP digitized-speech backend. Its first 12 bytes are the export header the " +
                "engine's thunk dispatcher indexes by mode (0 init / 1 play / 2 stop; slots 3 and 4 " +
                "are never dispatched, and slot 5 is the offset of the BUSY byte the engine polls " +
                "while a clip plays). `init` latches the compiled-in device descriptor at module+0x1FF " +
                "and takes its play routine from descriptor+4. The body is machine code and is " +
                "recorded, not converted.",
            Source = $"{source.OriginFile}/{source.Name}",
            Kind = SpeechDriverKind,
            Device = SpeechDriverDevice(source.Stem),
            Identification = SpeechDriverNotes(source.Stem, module),
            CodeFile = Path.GetFileName(codePath),
            CodeBytes = module.Body.Length,
            CodeSha256 = Sha256(module.Body),
            Exports = exports,
            Descriptors = descriptors,
        };

        return
        [
            new TransformOutput(
                jsonPath,
                JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.AudioDriverDto),
                OutputRole.Data,
                OutputFidelity.Exact),
            new TransformOutput(
                codePath,
                module.Body,
                OutputRole.Code,
                OutputFidelity.Exact,
                "8086 machine code, recorded verbatim; the sibling .json names its ABI",
                0,
                0,
                module.Body.Length),
        ];
    }

    private static void AddDescriptor(
        List<SpeechDescriptorDto> into, SpeechDriverModule module, string kind, int at, string stem)
    {
        if (module.ReadDescriptor(at) is not { } words)
        {
            return;
        }

        into.Add(new SpeechDescriptorDto
        {
            Kind = kind,
            At = $"0x{at:X4}",
            Detect = $"0x{words[0]:X4}",
            Stop = $"0x{words[1]:X4}",
            Play = $"0x{words[2]:X4}",
            Param0 = $"0x{words[3]:X4}",
            Param1 = $"0x{words[4]:X4}",
            Params = DescriptorParams(stem),
        });
    }

    private static string? DescriptorParams(string stem) => stem.ToLowerInvariant() switch
    {
        // BLASTER's pair is read off the module's own constants: I/O base and jumper IRQ.
        "blaster" => "param0 = the DSP I/O base, param1 = the jumper IRQ — the module's own defaults, " +
                     "overwritten by init's arguments when those are non-zero",
        "adlib" => "param0 = the OPL2 register port (0x388)",
        _ => "the two words init overwrites with its arguments when those are non-zero; this device's " +
             "use of them is not documented in the project",
    };

    private static string MusicDriverDevice(string stem) => stem.ToLowerInvariant() switch
    {
        "ibmdrive" => "PC speaker (driver type 1)",
        "adldrive" => "AdLib / OPL2 (driver type 2)",
        "tnddrive" => "Tandy / PCjr (driver type 3)",
        "cmsdrive" => "CMS / Game Blaster (driver type 4)",
        _ => "unknown",
    };

    private static string SpeechDriverDevice(string stem) => stem.ToLowerInvariant() switch
    {
        "ibmspkr" => "PC speaker, 3-level bit-bang",
        "adlib" => "AdLib, OPL2 total-level used as a DAC",
        "covox" => "Covox Speech Thing (parallel-port DAC)",
        "tandytl" => "Tandy PSSJ DAC via DMA",
        "blaster" => "Sound Blaster DSP + DMA + completion IRQ",
        _ => "unknown",
    };

    private static IReadOnlyList<string> MusicDriverNotes(string stem, SoundDriverModule module)
    {
        List<string> notes = new List<string>
        {
            module.HasShippedShape
                ? "container header matches all four shipped drivers (entry 0x0010, retf 0x002F, " +
                  "reserved zero, declared size = file size)"
                : "container header DIFFERS from the four shipped drivers — see the fields above",
            $"{SoundDriverModule.Commands.Count(c => c.SentByGame)} of " +
            $"{SoundDriverModule.DispatchTableEntries} commands are issued by CYAC; the rest are the " +
            "library's standalone mode and are dormant in this game",
        };

        switch (stem.ToLowerInvariant())
        {
            case "ibmdrive":
                notes.Add("reference architecture: 4 virtual channels onto one PIT-channel-2 voice, a " +
                          "44-entry tone table (0x00-0x21 SFX, 0x22-0x2B the .SNG program voices), a " +
                          "512-byte noise table and square/sine wavetables");
                break;
            case "adldrive":
                notes.Add("carries a 79-record, 12-byte-per-record FM instrument bank at asset offset " +
                          "0xF30");
                notes.Add("SHIPPED BUG: the cmd-8 silence sweep walks the 18-entry OPERATOR map instead " +
                          "of the 9 channels, so it drops rhythm mode and wipes 0xC0-0xC5 " +
                          "");
                notes.Add("only driver that implements cmd 0x1E — OPL2 I/O settle-delay calibration, " +
                          "NOT tempo");
                break;
            case "tnddrive":
                notes.Add("tnd and cms are one source built twice; the NCR8496 semantics differ from a " +
                          "generic SN76496");
                break;
            case "cmsdrive":
                notes.Add("SHIPPED MISLABEL: the build banner says TANDY — cmsdrive is the second build " +
                          "of the tnddrive source (+323 bytes of relocation), and it plays one octave " +
                          "above tandy because the SAA clock is 2x and the formula compensates but the " +
                          "clock constant does not(dual-capture ratio 2.000)");
                break;
            default:
                break;
        }

        return notes;
    }

    private static IReadOnlyList<string> SpeechDriverNotes(string stem, SpeechDriverModule module)
    {
        List<string> notes = new List<string>
        {
            module.HasSharedExportHeader
                ? "export header is the one all five shipped .SP modules share: six little-endian " +
                  "words, init 0x006A, play 0x019C, stop 0x004E, wait 0x0045, poll 0x0028 and the " +
                  "module-relative offset of the busy byte, 0x0026"
                : "export header DIFFERS from the five shipped modules — see the offsets above",
            "the engine calls init/play/stop only, and reads exactly one module byte between play and " +
            "stop: the busy byte",
        };

        if (stem.Equals("blaster", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("its PIT-ch0 / IVT[8] block is UNREACHABLE dead code: none of the four offsets " +
                      "that reach it appears anywhere in the module");
            notes.Add("hooks IVT[IRQ+8] and takes its sample clock from the DSP time constant");
        }

        return notes;
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
