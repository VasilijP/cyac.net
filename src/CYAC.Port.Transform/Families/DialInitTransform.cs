using System.Globalization;
using System.Text.Json;
using CYAC.Formats.EaLib;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// <c>dialinit.bin</c> → <c>dialinit.json</c>: where every cockpit instrument sits, per flyable
/// aircraft.
/// </summary>
/// <remarks>
/// <para>
/// 6 aircraft × 10 instruments × 72 bytes (<see cref="DialInitDecoder"/>).  only its authored half
/// is in the file — the engine-filled half (needle rects, backing-buffer descriptor, draw callback,
/// time accumulator) is zero in all 60 shipped records and is emitted only if a file ever carries
/// something there.
/// </para>
/// </remarks>
public sealed class DialInitTransform : IFamilyTransform
{
    /// <summary>The data-tree path this family writes.</summary>
    public const string OutputPath = "dialinit.json";

    /// <inheritdoc/>
    public string Family => "dialinit";

    /// <inheritdoc/>
    public string TreeDescription =>
        "`dialinit.json` — the cockpit instrument layout of each of the six flyable aircraft: the " +
        "rect and pivot of every dial, its style, its sweep range and whether that aircraft has it " +
        "at all. Moving a gauge is an edit here.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is not null && DialInitDecoder.IsDialInit(source.Name);
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<DialCockpitLayout> layouts = DialInitDecoder.Parse(source.Content.Span);
        DialInitDto dto = new DialInitDto
        {
            Format = "cyac.cockpit-dials/1",
            About =
                "Per-aircraft cockpit instrument layout. cockpit_layout_load_per_aircraft " +
                "@image@0x01DAA copies the section of the flown aircraft into the ten dial slots, " +
                "record i into slot i. `rect` is [x, y, w, h] in 320x200 screen pixels and `pivot` " +
                "is where the needle turns. `present: false` means the aircraft has no such " +
                "instrument. Fields prefixed `_` are derived and ignored on import.",
            Source = $"{source.OriginFile}/{source.Name}",
            SlotNames = [.. DialInitDecoder.SlotNames],
            Cockpits = [.. layouts.Select(l => ToDto(l, OriginalNames.For(context)))],
        };

        string path = context.Allocate(OutputPath);
        int present = layouts.Sum(l => l.Instruments.Count(i => i.Present));
        return
        [
            new TransformOutput(
                path,
                JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.DialInitDto),
                OutputRole.Data,
                OutputFidelity.Exact,
                $"{layouts.Count} cockpits, {present} instruments present"),
        ];
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput json = outputs.SingleOrDefault(o => o.Role == OutputRole.Data)
                            ?? throw new InvalidDataException("dialinit expects exactly one data output");
        return ToBytes(json.Bytes);
    }

    /// <summary>Rebuilds <c>dialinit.bin</c>'s decompressed body from the tree's JSON.</summary>
    /// <param name="json">The <c>dialinit.json</c> bytes.</param>
    /// <exception cref="InvalidDataException">The document is malformed.</exception>
    public static byte[] ToBytes(ReadOnlySpan<byte> json)
    {
        DialInitDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.DialInitDto)
                          ?? throw new InvalidDataException("dialinit.json is empty");
        List<DialCockpitDto> cockpits = dto.Cockpits ?? throw new InvalidDataException("dialinit.json has no \"cockpits\"");

        List<DialCockpitLayout> layouts = new List<DialCockpitLayout>(cockpits.Count);
        foreach (DialCockpitDto cockpit in cockpits)
        {
            List<DialInstrumentDto> instruments = cockpit.Instruments
                                                  ?? throw new InvalidDataException(
                                                      $"cockpit {cockpit.AircraftIndex} has no \"instruments\"");
            layouts.Add(new DialCockpitLayout(
                cockpit.AircraftIndex, [.. instruments.Select(FromDto)]));
        }

        return DialInitDecoder.ToBytes(layouts);
    }

    /// <summary>The instrument-style name for a kind word, or null when the value is not one of the five.</summary>
    /// <param name="kindWord">The record's <c>+0x0C</c> selector.</param>
    public static string? KindName(ushort kindWord) => kindWord switch
    {
        0x0C0C => "analog dial",
        0x0404 => "text indicator",
        0x0909 => "compass",
        0x0F0F => "MiG-15 radar variant (also the CGA runtime override)",
        0x0000 => "empty slot",
        _ => null,
    };

    private static DialCockpitDto ToDto(DialCockpitLayout layout, OriginalNames names) => new()
    {
        AircraftIndex = layout.AircraftIndex,
        AircraftName = layout.AircraftIndex < names.PlayerAircraft.Count
            ? names.PlayerAircraft[layout.AircraftIndex]
            : null,
        Instruments = [.. layout.Instruments.Select((r, slot) => new DialInstrumentDto
        {
            Slot = slot,
            SlotName = DialInitDecoder.SlotNames[slot],
            DgroupAddress = $"0x{DialInitDecoder.SlotDgroupAddresses[slot]:X4}",
            Present = r.Present,
            Rect = [r.RectX, r.RectY, r.RectWidth, r.RectHeight],
            Pivot = [r.PivotX, r.PivotY],
            KindWord = $"0x{r.KindWord:X4}",
            KindName = KindName(r.KindWord),
            Param0 = r.Param0,
            Param1 = r.Param1,
            NeedleAngleOffset = r.Param2,
            KeyframeCount = r.KeyframeCount,
            Amplitude = r.Amplitude,
            SweepStep = r.ScrollStep,
            DirectionInvert = r.DirectionInvert,
            RuntimeStateHex = r.RuntimeState is null ? null : Convert.ToHexString(r.RuntimeState),
        })],
    };

    private static DialInstrumentRecord FromDto(DialInstrumentDto dto)
    {
        List<int> rect = dto.Rect ?? throw new InvalidDataException($"instrument {dto.Slot} has no \"rect\"");
        List<int> pivot = dto.Pivot ?? throw new InvalidDataException($"instrument {dto.Slot} has no \"pivot\"");
        if (rect.Count != 4 || pivot.Count != 2)
        {
            throw new InvalidDataException(
                $"instrument {dto.Slot}: \"rect\" needs [x, y, w, h] and \"pivot\" needs [x, y]");
        }

        return new DialInstrumentRecord
        {
            RectX = (short)rect[0],
            RectY = (short)rect[1],
            RectWidth = (short)rect[2],
            RectHeight = (short)rect[3],
            PivotX = (short)pivot[0],
            PivotY = (short)pivot[1],
            KindWord = ParseKindWord(dto.KindWord, dto.Slot),
            Param0 = (ushort)dto.Param0,
            Param1 = (ushort)dto.Param1,
            Param2 = (ushort)dto.NeedleAngleOffset,
            KeyframeCount = (short)dto.KeyframeCount,
            Amplitude = (short)dto.Amplitude,
            ScrollStep = (short)dto.SweepStep,
            Present = dto.Present,
            DirectionInvert = (byte)dto.DirectionInvert,
            RuntimeState = string.IsNullOrEmpty(dto.RuntimeStateHex)
                ? null
                : Convert.FromHexString(dto.RuntimeStateHex),
        };
    }

    private static ushort ParseKindWord(string? text, int slot)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidDataException($"instrument {slot} has no \"kindWord\"");
        }

        string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return ushort.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort value)
            ? value
            : throw new InvalidDataException($"instrument {slot}: \"kindWord\" = \"{text}\" is not a hex word");
    }
}
