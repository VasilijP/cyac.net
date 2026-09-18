using CYAC.Port.Core.Data;
using System.Text.Json;
using CYAC.Formats.EaLib;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// <c>scenario.bin</c> → <c>scenarios.json</c>: the 50-mission picker catalog.
/// </summary>
/// <remarks>
/// <para>
/// One 164-byte <c>s_scenario_record</c> per mission (<c>mov cx,0xA4</c> @<c>image@0x2472B</c>);
/// Decoding and byte-exact re-emission are <see cref="ScenarioBinDecoder"/>'s; the JSON names are
/// the port's (<see cref="MissionEntry"/>), so a modder edits <c>title</c>, not <c>+0x0F</c>.
/// </para>
/// <para>
/// Every field is consumer-cited, so the record model covers the whole record and there is nothing
/// to carry as <c>unknown_*</c> — except whatever a diff of the model's own re-emission turns up
/// (<see cref="ByteResidue"/>), which on the shipping catalog is nothing at all.
/// </para>
/// </remarks>
public sealed class ScenarioTransform : IFamilyTransform
{
    /// <summary>The data-tree path this family writes.</summary>
    public const string OutputPath = "scenarios.json";

    /// <summary>The asset name this family claims.</summary>
    public const string AssetName = "scenario.bin";

    /// <inheritdoc/>
    public string Family => "scenario";

    /// <inheritdoc/>
    public string TreeDescription =>
        "`scenarios.json` — the mission picker's catalog: 50 records of who you fly, when, against " +
        "what, how hard it is rated, and which `.S` module carries the mission. Editing a title, a " +
        "date or a description here changes what the picker shows.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is not null
            && string.Equals(source.Name, AssetName, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        byte[] body = source.Content.ToArray();
        ScenarioBinDecoder.Catalog catalog = ScenarioBinDecoder.DecodeFromDecompressed(body, source.StoredLength ?? 0);
        IReadOnlyList<(int Offset, byte[] Bytes)> residue = ByteResidue.Diff(body, ScenarioBinDecoder.ToBytes(catalog));

        ScenarioCatalogDto dto = new ScenarioCatalogDto
        {
            Format = "cyac.scenarios/1",
            About =
                "The mission catalog (scenario.bin). One record per picker line; `era` selects the " +
                "theater (.W) as well as the period, `moduleAssetName` names the .S module in " +
                "2b.lib that holds the mission itself, and `recordIndex` keys the 50-byte " +
                "per-mission unlock array [0xEF50] the save file carries. Fields prefixed `_` are " +
                "derived names for readers and are ignored when this file is read back.",
            Source = $"{source.OriginFile}/{source.Name}",
            RecordBytes = ScenarioBinDecoder.RecordSize,
            Missions = [.. catalog.Records.Select(r => ToDto(r, OriginalNames.For(context)))],
            UnknownResidue = ToResidueDtos(residue),
        };

        string path = context.Allocate(OutputPath);
        return
        [
            new TransformOutput(
                path,
                JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.ScenarioCatalogDto),
                OutputRole.Data,
                OutputFidelity.Exact,
                $"{catalog.Records.Count} mission records",
                ByteResidue.Count(residue)),
        ];
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput json = outputs.SingleOrDefault(o => o.Role == OutputRole.Data)
                            ?? throw new InvalidDataException("scenario expects exactly one data output");
        return ToBytes(json.Bytes);
    }

    /// <summary>Rebuilds <c>scenario.bin</c>'s decompressed body from the tree's JSON.</summary>
    /// <param name="json">The <c>scenarios.json</c> bytes.</param>
    /// <exception cref="InvalidDataException">The document is malformed.</exception>
    public static byte[] ToBytes(ReadOnlySpan<byte> json)
    {
        ScenarioCatalogDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.ScenarioCatalogDto)
                                 ?? throw new InvalidDataException("scenarios.json is empty");
        List<ScenarioEntryDto> missions = dto.Missions ?? throw new InvalidDataException("scenarios.json has no \"missions\"");

        List<ScenarioBinDecoder.ScenarioRecord> records = new List<ScenarioBinDecoder.ScenarioRecord>(missions.Count);
        for (int i = 0; i < missions.Count; i++)
        {
            ScenarioEntryDto m = missions[i];
            records.Add(new ScenarioBinDecoder.ScenarioRecord(
                Slot: i,
                RecordIndex: Byte(m.RecordIndex, "recordIndex", i),
                Era: Byte(m.Era, "era", i),
                InsigniaIdx: Byte(m.InsigniaIndex, "insigniaIndex", i),
                AircraftIdx: Byte(m.PlayerAircraftIndex, "playerAircraftIndex", i),
                OpponentClass: Byte(m.OpponentClassId, "opponentClassId", i),
                DifficultyRating: Byte(m.DifficultyRating, "difficultyRating", i),
                Date: m.Date ?? string.Empty,
                Title: m.Title ?? string.Empty,
                SFilename: m.ModuleAssetName ?? string.Empty,
                Description: m.Description ?? string.Empty));
        }

        byte[] body = ScenarioBinDecoder.ToBytes(new ScenarioBinDecoder.Catalog(
            records, ScenarioBinDecoder.RecordSize, records.Count * ScenarioBinDecoder.RecordSize, 0, []));
        ByteResidue.Apply(body, FromResidueDtos(dto.UnknownResidue));
        return body;
    }

    internal static List<ResidueSpanDto>? ToResidueDtos(IReadOnlyList<(int Offset, byte[] Bytes)> spans) =>
        spans.Count == 0
            ? null
            : [.. spans.Select(s => new ResidueSpanDto { Offset = s.Offset, Hex = Convert.ToHexString(s.Bytes) })];

    internal static IEnumerable<(int Offset, byte[] Bytes)> FromResidueDtos(List<ResidueSpanDto>? spans) =>
        spans is null
            ? []
            : spans.Select(s => (s.Offset, Convert.FromHexString(s.Hex ?? string.Empty)));

    private static byte Byte(int value, string field, int index) =>
        value is >= 0 and <= 0xFF
            ? (byte)value
            : throw new InvalidDataException($"missions[{index}].{field} = {value} does not fit in a byte");

    private static ScenarioEntryDto ToDto(ScenarioBinDecoder.ScenarioRecord r, OriginalNames names) => new()
    {
        Slot = r.Slot,
        RecordIndex = r.RecordIndex,
        Era = r.Era,
        EraName = ReadingAidLabels.Era(r.Era),
        TheaterAsset = r.Era < SDataModel.TheaterAssetForEra.Length
            ? SDataModel.TheaterAssetForEra[r.Era]
            : null,
        InsigniaIndex = r.InsigniaIdx,
        InsigniaName = ReadingAidLabels.InsigniaFrame(r.InsigniaIdx),
        PlayerAircraftIndex = r.AircraftIdx,
        PlayerAircraftName = names.PlayerAircraftLabel(r.AircraftIdx),
        OpponentClassId = r.OpponentClass,
        OpponentName = names.AircraftClassLabel(r.OpponentClass),
        DifficultyRating = r.DifficultyRating,
        DifficultyName = ReadingAidLabels.DifficultyRating(r.DifficultyRating),
        Date = r.Date,
        Title = r.Title,
        ModuleAssetName = r.SFilename,
        Description = r.Description,
    };
}
