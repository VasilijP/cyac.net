using System.Text.Json;
using CYAC.Formats.EaLib;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// The six flyable aircraft: <c>.fmd</c> + <c>.fme</c> merged into <c>aircraft/&lt;name&gt;.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// One aircraft is two archive members — its 298-byte flight model and its 504-byte flight envelope
/// — and the plan asks for one document per aircraft, so this family is the tree's only
/// <b>merge</b>: both members claim the same output path and the inverse is told which of them it is
/// rebuilding (<see cref="IFamilyTransform.Inverse(string, IReadOnlyList{LoadedOutput}, TransformContext)"/>).
/// Whichever member the archive walk reaches first writes the document; the second finds the path
/// already allocated and writes the identical bytes, so the manifest carries one row per member —
/// 12 rows, 6 files — and each row's unknown-byte count is that member's own.
/// </para>
/// <para>
/// The document is complete enough to be the runtime's only input:
/// <c>AircraftDefinition.Load</c> rebuilds both bodies from it through the same
/// <c>AircraftDataCodec</c> the inverse uses, so "loaded from the tree" and "parsed from the
/// original asset" cannot diverge.
/// </para>
/// <para>
/// Format source of truth: <c>src/CYAC.Formats/EaLib/FlightModelDecoder.cs</c>.  Field roles and
/// confidences come from that decoder's own <c>BlockRoles</c> / <c>TailFields</c>
/// tables, so a hypothesis stays visibly a hypothesis in the transformed data.
/// </para>
/// </remarks>
public sealed class AircraftTransform : IFamilyTransform
{
    /// <summary>The family name <c>--only</c> matches and the manifest records.</summary>
    public const string FamilyName = "aircraft";

    /// <summary>The extension of the flight-model member.</summary>
    public const string FlightModelExtension = ".fmd";

    /// <summary>The extension of the flight-envelope member.</summary>
    public const string EnvelopeExtension = ".fme";

    /// <inheritdoc/>
    public string Family => FamilyName;

    /// <inheritdoc/>
    public string TreeDescription =>
        "`aircraft/<name>.json` — one document per flyable aircraft, merging its `.fmd` flight model " +
        "(nine integrator blocks plus a fully named 154-byte scalar tail) and its `.fme` flight " +
        "envelope (14 V-n curves keyed by load factor). Editing it and running `--inverse` rebuilds " +
        "both members of `2b.lib`.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.EntryIndex is null)
        {
            return false;
        }

        string extension = source.Extension;
        if (extension is not (FlightModelExtension or EnvelopeExtension))
        {
            return false;
        }

        return IndexOf(source.Stem) >= 0;
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        string basename = source.Stem;
        int index = IndexOf(basename);
        if (index < 0)
        {
            throw new InvalidDataException($"{source}: '{basename}' is not a flyable aircraft basename");
        }

        bool isModel = source.Extension == FlightModelExtension;
        string companionName = basename + (isModel ? EnvelopeExtension : FlightModelExtension);
        byte[]? companion = context.Originals?.TryGetArchiveMember(source.OriginFile, companionName)
            ?? throw new InvalidDataException(
                $"{source}: {companionName} is not available, so the two halves cannot be merged");

        byte[] model = isModel ? source.Content.ToArray() : companion;
        byte[] envelope = isModel ? companion : source.Content.ToArray();
        IReadOnlyList<string> displayNames = OriginalNames.For(context).FlyableDisplayNames;
        AircraftDefinitionDto document = Build(
            basename, index, source.OriginFile, model, envelope,
            index < displayNames.Count ? displayNames[index] : null);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            document, PortDataJsonContext.Readable.AircraftDefinitionDto);

        // Both members explain the same document.  The first to arrive allocates the path; the
        // second reuses it and writes identical bytes (the merge is symmetric by construction).
        string path = $"aircraft/{basename}.json";
        string allocated = context.IsAllocated(path) ? path : context.Allocate(path);

        return
        [
            new TransformOutput(
                allocated,
                json,
                OutputRole.Data,
                OutputFidelity.Exact,
                isModel
                    ? "the flight model half of the merged aircraft document"
                    : "the flight envelope half of the merged aircraft document"),
        ];
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">
    /// Always: one document explains two members, so the inverse needs the member name.
    /// </exception>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context) =>
        throw new NotSupportedException(
            "aircraft/<name>.json explains BOTH the .fmd and the .fme of one aircraft, so the " +
            "inverse must be told which member it is rebuilding; use the member-name overload.");

    /// <inheritdoc/>
    public byte[] Inverse(string memberName, IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(memberName);
        ArgumentNullException.ThrowIfNull(outputs);
        if (outputs.Count != 1)
        {
            throw new InvalidDataException(
                $"{memberName}: expected one aircraft document, found {outputs.Count}");
        }

        AircraftDefinitionDto document = JsonSerializer.Deserialize(
                                             outputs[0].Bytes, PortDataJsonContext.Readable.AircraftDefinitionDto)
                                         ?? throw new InvalidDataException($"{outputs[0].Path} is empty");

        string extension = Path.GetExtension(memberName).ToLowerInvariant();
        return extension switch
        {
            FlightModelExtension => AircraftDataCodec.ToFlightModelBytes(document),
            EnvelopeExtension => AircraftDataCodec.ToEnvelopeBytes(document),
            _ => throw new InvalidDataException(
                $"'{memberName}' is neither a {FlightModelExtension} nor a {EnvelopeExtension} member"),
        };
    }

    /// <summary>Builds one aircraft's merged document from the two decoded asset bodies.</summary>
    /// <param name="basename">The asset basename, e.g. <c>"f4"</c>.</param>
    /// <param name="index">The aircraft index <c>g_active_aircraft_idx</c> selects it with.</param>
    /// <param name="archive">The archive both members came from, e.g. <c>"2b.lib"</c>.</param>
    /// <param name="model">The 298 decoded <c>.fmd</c> bytes.</param>
    /// <param name="envelope">The 504 decoded <c>.fme</c> bytes.</param>
    /// <param name="displayName">
    /// The aircraft's display name, a reading aid the two members do not carry: the encyclopedia's short
    /// name, read from <c>pi.bin</c> (<see cref="OriginalNames.FlyableDisplayNames"/>); null for none.
    /// </param>
    public static AircraftDefinitionDto Build(
        string basename, int index, string archive, byte[] model, byte[] envelope, string? displayName = null)
    {
        FlightModelDecoder.FmdFile fmd = FlightModelDecoder.DecodeFmd(basename, model);
        FlightModelDecoder.FmeFile fme = FlightModelDecoder.DecodeFme(basename, envelope);

        List<AircraftInitBlockDto> blocks = new List<AircraftInitBlockDto>(fmd.Blocks.Length);
        foreach (FlightModelDecoder.IntegratorBlock block in fmd.Blocks)
        {
            blocks.Add(new AircraftInitBlockDto
            {
                Index = block.Index,
                Role = block.Role,
                Confidence = block.Confidence,
                Value = block.Value,
                Working = block.Working,
                HiBound = block.HiBound,
                LoBound = block.LoBound,
                BaseDir = block.BaseDir,
                DirStep = block.DirStep,
            });
        }

        List<AircraftTailFieldDto> tail = new List<AircraftTailFieldDto>(FlightModelDecoder.TailFields.Length);
        foreach ((int offset, string name, FlightModelDecoder.TailKind kind, string? scannerName, string note) in FlightModelDecoder.TailFields)
        {
            tail.Add(new AircraftTailFieldDto
            {
                Offset = PortHex.Format(offset, 3),
                Name = name,
                Type = kind.ToString().ToLowerInvariant(),
                Value = fmd.Tail[name],
                ScannerName = scannerName,
                Note = note,
            });
        }

        List<EnvelopeCurveDto> curves = new List<EnvelopeCurveDto>(fme.Records.Length);
        foreach (FlightModelDecoder.EnvelopeRecord record in fme.Records)
        {
            List<EnvelopePointDto> points = new List<EnvelopePointDto>(record.Points.Length);
            foreach (FlightModelDecoder.EnvelopePoint point in record.Points)
            {
                points.Add(new EnvelopePointDto { AirspeedFps = point.X, AltitudeUnits = point.Y });
            }

            curves.Add(new EnvelopeCurveDto
            {
                Index = record.Index,
                LoadFactorG = record.Ordinal,
                PointCount = record.PointCount,
                PeakIndex = record.PeakIdx,
                HighSpeedIndex = record.HighSpeedIdx,
                Points = points,
            });
        }

        return new AircraftDefinitionDto
        {
            Format = "cyac.aircraft/1",
            About =
                "One flyable aircraft, merged from its two 2b.lib members. THE FILE IS THE STRUCT: " +
                "flight_model_load_for_aircraft @image@0x2A112 loads the .fmd with " +
                "ealib_load_asset(mode=0xFF), which bulk-copies all 298 bytes 1:1 into " +
                "s_aircraft_master [0xEF98], so a tail field's offset here is also its offset in the " +
                "runtime struct. The loader then computes two values from it: fuel = initial_fuel << 8 " +
                "and heading_angular_rate = (gross_weight_lb * perf_limit) >> 11. " +
                "The .fme is a V-n-BY-ALTITUDE envelope: 14 curves keyed by the SIGNED integer load " +
                "factor in G (-4..+9), each point {airspeed in feet/second, altitude in feet/8}. " +
                "Point slots at or past pointCount are authoring-tool filler, not curve data - they " +
                "are carried verbatim because fme_low_speed_limit_check @image@0x2A9BE scans with a " +
                "fixed ceiling rather than the count, so they are reachable in principle. " +
                "Field names, roles and confidences come from " +
                "src/CYAC.Formats/EaLib/FlightModelDecoder.cs; a block whose confidence says " +
                "'hypothesis' is one whose bounds pattern holds in all six files but whose meaning is " +
                "not proven.",
            Name = basename,
            DisplayName = displayName,
            Index = index,
            FlightModelSource = $"{archive}/{basename.ToUpperInvariant()}{FlightModelExtension.ToUpperInvariant()}",
            EnvelopeSource = $"{archive}/{basename.ToUpperInvariant()}{EnvelopeExtension.ToUpperInvariant()}",
            InitBlocks = blocks,
            Tail = tail,
            Envelope = curves,
        };
    }

    private static int IndexOf(string basename)
    {
        IReadOnlyList<string> names = AircraftDefinition.FlyableBasenames;
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], basename, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
