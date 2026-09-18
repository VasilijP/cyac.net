using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CYAC.Formats.EaLib;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// <c>pi.bin</c> → <c>pi.json</c>: the hangar's aircraft encyclopedia (14 pages) and the tactical
/// matchup hints (15).
/// </summary>
/// <remarks>
/// <para>
/// Layout and every field citation live in <see cref="PiBinDecoder"/>.
/// The two directories at <c>+0x02</c> and <c>+0x2C</c> hold body-absolute offsets, records are
/// variable-length and the plane directory is <b>not</b> monotonic, so the JSON keeps each record's
/// offset and each string's offset rather than pretending the file is an array.
/// </para>
/// <para>
/// <b>Editing note.</b>  Text lives in a per-record string pool at fixed body offsets and the header
/// fields point at those offsets; this transform does not re-lay-out the file, so changing a
/// string's LENGTH needs the offsets fixed up by hand.  Changing a number (speed, weight, ceiling)
/// or replacing text of the same length is safe.  Re-layout would be a genuine feature — it is
/// simply not what proves the format understood, which is what T4 owed.
/// </para>
/// </remarks>
public sealed class PiTransform : IFamilyTransform
{
    /// <summary>The data-tree path this family writes.</summary>
    public const string OutputPath = "pi.json";

    /// <summary>The asset name this family claims.</summary>
    public const string AssetName = "pi.bin";

    /// <summary>The pointer fields of a plane record, in header order — the keys of <c>textPointers</c>.</summary>
    public static readonly string[] TextPointerNames =
    [
        "nameFull", "nameShort", "armament1", "armament2", "armament3",
        "armamentSummary", "engine", "description",
    ];

    private static readonly int[] TextPointerOffsets =
    [
        PiBinDecoder.PfNameFullPtr, PiBinDecoder.PfNameShortPtr, PiBinDecoder.PfArm1Ptr,
        PiBinDecoder.PfArm2Ptr, PiBinDecoder.PfArm3Ptr, PiBinDecoder.PfArmSumPtr,
        PiBinDecoder.PfEnginePtr, PiBinDecoder.PfDescriptionPtr,
    ];

    /// <inheritdoc/>
    public string Family => "pi";

    /// <inheritdoc/>
    public string TreeDescription =>
        "`pi.json` — the hangar encyclopedia: one page per aircraft (names, armament, engine, the " +
        "compared performance numbers, the hangar camera and the dimension callouts) plus the " +
        "matchup hints the tactics screen shows for a given (you, enemy) pairing.";

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
        PiBinDecoder.PiBin pi = PiBinDecoder.DecodeFromDecompressed(body, source.StoredLength ?? 0);

        PiDto dto = new PiDto
        {
            Format = "cyac.aircraft-encyclopedia/1",
            About =
                "The hangar's aircraft pages and the tactics screen's matchup hints. Offsets are " +
                "body-absolute because the records are variable-length and the plane directory is " +
                "not sorted: `bodyOffset` locates a record, `textPointers` says which " +
                "string each header field points at, and `strings` is the record's own pool. " +
                "Numbers can be edited freely; changing a string's LENGTH shifts nothing else, so " +
                "the pointers and offsets would have to be fixed up by hand. `_`-prefixed fields " +
                "are derived and ignored on import.",
            Source = $"{source.OriginFile}/{source.Name}",
            BodyBytes = body.Length,
            PlaneDirectory = [.. pi.PlaneOffsets.Select(o => (int)o)],
            HintDirectory = [.. pi.HintOffsets.Select(o => (int)o)],
            Planes = [.. pi.Planes.Select(p => ToDto(p, body, OriginalNames.For(context)))],
            Hints = [.. pi.Hints.Select(h => ToDto(h, OriginalNames.For(context)))],
        };

        // The model's own re-emission is the honest measure of what it does not explain: the two
        // zero gaps between the directories, any tail after a record's last string, and anything
        // else the field map misses come back as counted residue spans.
        IReadOnlyList<(int Offset, byte[] Bytes)> residue = ByteResidue.Diff(body, Rebuild(dto, []));
        PiDto withResidue = new PiDto
        {
            Format = dto.Format,
            About = dto.About,
            Source = dto.Source,
            BodyBytes = dto.BodyBytes,
            PlaneDirectory = dto.PlaneDirectory,
            HintDirectory = dto.HintDirectory,
            Planes = dto.Planes,
            Hints = dto.Hints,
            UnknownResidue = ScenarioTransform.ToResidueDtos(residue),
        };

        string path = context.Allocate(OutputPath);
        return
        [
            new TransformOutput(
                path,
                JsonSerializer.SerializeToUtf8Bytes(withResidue, TransformJsonContext.Readable.PiDto),
                OutputRole.Data,
                OutputFidelity.Exact,
                $"{pi.Planes.Count} encyclopedia pages, {pi.Hints.Count} matchup hints",
                ByteResidue.Count(residue)),
        ];
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput json = outputs.SingleOrDefault(o => o.Role == OutputRole.Data)
                            ?? throw new InvalidDataException("pi expects exactly one data output");
        return ToBytes(json.Bytes);
    }

    /// <summary>Rebuilds <c>pi.bin</c>'s decompressed body from the tree's JSON.</summary>
    /// <param name="json">The <c>pi.json</c> bytes.</param>
    /// <exception cref="InvalidDataException">The document is malformed.</exception>
    public static byte[] ToBytes(ReadOnlySpan<byte> json)
    {
        PiDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.PiDto)
                    ?? throw new InvalidDataException("pi.json is empty");
        return Rebuild(dto, ScenarioTransform.FromResidueDtos(dto.UnknownResidue));
    }

    private static byte[] Rebuild(PiDto dto, IEnumerable<(int Offset, byte[] Bytes)> residue)
    {
        List<PiPlaneDto> planes = dto.Planes ?? throw new InvalidDataException("pi.json has no \"planes\"");
        List<PiHintDto> hints = dto.Hints ?? throw new InvalidDataException("pi.json has no \"hints\"");
        List<int> planeDirectory = dto.PlaneDirectory
                                   ?? throw new InvalidDataException("pi.json has no \"planeDirectory\"");
        List<int> hintDirectory = dto.HintDirectory
                                  ?? throw new InvalidDataException("pi.json has no \"hintDirectory\"");

        byte[] body = new byte[dto.BodyBytes];
        Write16(body, PiBinDecoder.OffPlaneCount, planeDirectory.Count);
        for (int i = 0; i < planeDirectory.Count; i++)
        {
            Write16(body, PiBinDecoder.OffPlaneDir + (i * 2), planeDirectory[i]);
        }

        Write16(body, PiBinDecoder.OffHintCount, hintDirectory.Count);
        for (int i = 0; i < hintDirectory.Count; i++)
        {
            Write16(body, PiBinDecoder.OffHintDir + (i * 2), hintDirectory[i]);
        }

        foreach (PiPlaneDto plane in planes)
        {
            WritePlane(body, plane);
        }

        foreach (PiHintDto hint in hints)
        {
            WriteHint(body, hint);
        }

        ByteResidue.Apply(body, residue);
        return body;
    }

    private static void WritePlane(byte[] body, PiPlaneDto plane)
    {
        int o = plane.BodyOffset;
        if (o < 0 || o + PiBinDecoder.PlaneHeaderLen > body.Length)
        {
            throw new InvalidDataException(
                $"plane {plane.Index}: bodyOffset 0x{o:X} leaves no room for its 0x34-byte header");
        }

        body[o + PiBinDecoder.PfClassId] = (byte)plane.AircraftClassId;
        body[o + PiBinDecoder.PfArmRating] = (byte)plane.ArmamentRating;

        Dictionary<string, int> pointers = plane.TextPointers ?? [];
        for (int i = 0; i < TextPointerNames.Length; i++)
        {
            pointers.TryGetValue(TextPointerNames[i], out int pointer);
            Write16(body, o + TextPointerOffsets[i], pointer);
        }

        Write16(body, o + PiBinDecoder.PfWeightLb, plane.WeightLb);
        Write16(body, o + PiBinDecoder.PfMaxSpeedMph, plane.MaxSpeedMph);
        Write16(body, o + PiBinDecoder.PfMaxAltFt, plane.MaxAltitudeFt);
        Write16(body, o + PiBinDecoder.PfThrustWeightQ8, plane.ThrustToWeightQ8);
        Write16(body, o + PiBinDecoder.PfWingLoadingPsf, plane.WingLoadingPsf);
        Write16(body, o + PiBinDecoder.PfHangarPosX, plane.HangarCameraX);
        Write16(body, o + PiBinDecoder.PfHangarPosY, plane.HangarCameraY);
        Write16(body, o + PiBinDecoder.PfHangarPosZ, plane.HangarCameraZ);
        Write16(body, o + PiBinDecoder.PfSilhouetteYOff, plane.SilhouetteYOffset);
        Write16(body, o + PiBinDecoder.PfLengthFt, plane.LengthFt);
        Write16(body, o + PiBinDecoder.PfLengthIn, plane.LengthIn);
        Write16(body, o + PiBinDecoder.PfHeightFt, plane.HeightFt);
        Write16(body, o + PiBinDecoder.PfHeightIn, plane.HeightIn);
        Write16(body, o + PiBinDecoder.PfDimLenX1, plane.LengthCalloutX1);
        Write16(body, o + PiBinDecoder.PfDimLenX2, plane.LengthCalloutX2);
        Write16(body, o + PiBinDecoder.PfDimHgtY1, plane.HeightCalloutY1);
        Write16(body, o + PiBinDecoder.PfDimHgtY2, plane.HeightCalloutY2);

        foreach (PiStringDto text in plane.Strings ?? [])
        {
            WriteString(body, text, $"plane {plane.Index}");
        }
    }

    private static void WriteHint(byte[] body, PiHintDto hint)
    {
        int o = hint.BodyOffset;
        if (o < 0 || o + PiBinDecoder.HfText > body.Length)
        {
            throw new InvalidDataException($"hint {hint.Index}: bodyOffset 0x{o:X} is outside the body");
        }

        body[o + PiBinDecoder.HfAllyClass] = (byte)hint.PlayerClassId;
        body[o + PiBinDecoder.HfEnemyClass] = (byte)hint.EnemyClassId;
        byte[] text = Encoding.Latin1.GetBytes(hint.Text ?? string.Empty);
        byte[] trailing = string.IsNullOrEmpty(hint.TrailingHex) ? [] : Convert.FromHexString(hint.TrailingHex);
        int at = o + PiBinDecoder.HfText;
        if (at + text.Length + 1 + trailing.Length > body.Length)
        {
            throw new InvalidDataException($"hint {hint.Index}: its text runs past the end of the body");
        }

        text.CopyTo(body, at);
        body[at + text.Length] = 0;
        trailing.CopyTo(body, at + text.Length + 1);
    }

    private static void WriteString(byte[] body, PiStringDto text, string what)
    {
        byte[] bytes = text.Hex is { Length: > 0 }
            ? Convert.FromHexString(text.Hex)
            : Encoding.Latin1.GetBytes(text.Text ?? string.Empty);
        if (text.Offset < 0 || text.Offset + bytes.Length + 1 > body.Length)
        {
            throw new InvalidDataException(
                $"{what}: a string at offset 0x{text.Offset:X} does not fit in the {body.Length} B body");
        }

        bytes.CopyTo(body, text.Offset);
        body[text.Offset + bytes.Length] = 0;
    }

    private static PiPlaneDto ToDto(PiBinDecoder.PlaneRecord p, byte[] body, OriginalNames names)
    {
        Dictionary<string, int> pointers = new Dictionary<string, int>(TextPointerNames.Length);
        for (int i = 0; i < TextPointerNames.Length; i++)
        {
            pointers[TextPointerNames[i]] =
                BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(p.Offset + TextPointerOffsets[i], 2));
        }

        return new PiPlaneDto
        {
            Index = p.Index,
            BodyOffset = p.Offset,
            AircraftClassId = p.AircraftClassId,
            ClassName = names.AircraftClassLabel(p.AircraftClassId),
            FlyablePlayerSlot = p.FlyableSlot,
            ArmamentRating = p.ArmamentRating,
            WeightLb = p.WeightLb,
            MaxSpeedMph = p.MaxSpeedMph,
            MaxAltitudeFt = p.MaxAltFt,
            ThrustToWeightQ8 = p.ThrustWeightQ8,
            ThrustToWeightText = p.ThrustWeightText,
            WingLoadingPsf = p.WingLoadingPsf,
            HangarCameraX = p.HangarPosX,
            HangarCameraY = p.HangarPosY,
            HangarCameraZ = p.HangarPosZ,
            SilhouetteYOffset = p.SilhouetteYOff,
            LengthFt = p.LengthFt,
            LengthIn = p.LengthIn,
            HeightFt = p.HeightFt,
            HeightIn = p.HeightIn,
            LengthCalloutX1 = p.DimLenX1,
            LengthCalloutX2 = p.DimLenX2,
            HeightCalloutY1 = p.DimHgtY1,
            HeightCalloutY2 = p.DimHgtY2,
            TextPointers = pointers,
            Strings = ReadPool(p.Blob, p.Offset + PiBinDecoder.PlaneHeaderLen, pointers),
        };
    }

    private static PiHintDto ToDto(PiBinDecoder.HintRecord h, OriginalNames names)
    {
        byte[] blob = h.Blob;
        int terminator = Array.IndexOf(blob, (byte)0);
        byte[] trailing = terminator < 0 ? [] : blob[(terminator + 1)..];
        return new PiHintDto
        {
            Index = h.Index,
            BodyOffset = h.Offset,
            PlayerClassId = h.AllyClassId,
            PlayerName = names.AircraftClassLabel(h.AllyClassId),
            EnemyClassId = h.EnemyClassId,
            EnemyName = names.AircraftClassLabel(h.EnemyClassId),
            Text = h.Text,
            TrailingHex = trailing.Length == 0 ? null : Convert.ToHexString(trailing),
        };
    }

    private static List<PiStringDto> ReadPool(byte[] blob, int baseOffset, Dictionary<string, int> pointers)
    {
        List<PiStringDto> pool = new List<PiStringDto>();
        int start = 0;
        for (int i = 0; i < blob.Length; i++)
        {
            if (blob[i] != 0)
            {
                continue;
            }

            Span<byte> run = blob.AsSpan(start, i - start);
            int offset = baseOffset + start;
            List<string> usedBy = pointers.Where(kv => kv.Value == offset).Select(kv => kv.Key).ToList();
            bool printable = true;
            foreach (byte b in run)
            {
                if (b is < 0x20 or > 0x7E)
                {
                    printable = false;
                }
            }

            pool.Add(new PiStringDto
            {
                Offset = offset,
                Text = printable ? Encoding.Latin1.GetString(run) : null,
                Hex = printable ? null : Convert.ToHexString(run),
                UsedBy = usedBy.Count == 0 ? null : usedBy,
            });
            start = i + 1;
        }

        return pool;
    }

    private static void Write16(byte[] body, int offset, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(offset, 2), (ushort)value);
}
