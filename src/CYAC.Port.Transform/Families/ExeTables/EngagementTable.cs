using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CYAC.Port.Core.Data;

namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>
/// The ENGAGEMENT ZONE — DGROUP <c>[0x14F0..0x2540)</c>, the AI's whole per-class tuning surface.
/// </summary>
/// <remarks>
/// <para>
/// The zone tiles exactly, and the tiling is derived rather than assumed.  The 46-entry aircraft-class
/// table names 19 <c>s_engagement_class_proto</c> records (46 B each); each is preceded by its own ARC
/// DESCRIPTOR, and a descriptor that owns a RANGE TABLE is 30 bytes with the table's head at
/// <c>+0x1C</c> — a table whose sorted 6-byte records end exactly where the descriptor begins.  The
/// records' <c>+0x00</c> words point at self-delimiting run-length BAND STRINGS which fill the space
/// before the table, and the prototypes' <c>+0x04</c> words point into one NUL-terminated name run at
/// the head of the zone.
/// </para>
/// <para>
/// Walking that gives 100 % of the zone except 41 single zero bytes at ODD addresses (the assembler's
/// word alignment between structures, listed as <c>alignmentPads</c>) and one 14-byte fragment at
/// <c>[0x24E8]</c> that is the head of a 20th prototype the build dropped.
/// </para>
/// <para>
/// <b>Why it matters.</b> Before this document the running port read all of it out of
/// <c>data/exe/image.l1.bin</c> — the single biggest transform ask of the combat kernel.
/// </para>
/// </remarks>
internal sealed class EngagementExeTable : IExeTable
{
    /// <summary>Bytes of an <c>s_engagement_class_proto</c>.</summary>
    public const int PrototypeBytes = 0x2E;

    /// <summary>Bytes of an arc descriptor that owns a range table.</summary>
    public const int DescriptorWithTableBytes = 0x1E;

    /// <summary>Bytes of an arc descriptor that does not.</summary>
    public const int DescriptorBytes = 0x1C;

    /// <summary>Bytes of one <c>s_arc_range_record</c>.</summary>
    public const int RangeRecordBytes = 6;

    /// <summary>How many weapon slots a prototype has.</summary>
    public const int WeaponSlots = 4;

    /// <summary>Where the zone's NUL-terminated name run starts — right after the weapon-class table.</summary>
    public const int NameTable = 0x14F0;

    /// <summary>The DGROUP offset of the dropped 20th prototype's surviving head.</summary>
    public const int TruncatedPrototype = 0x24E8;

    /// <summary>How many bytes of it survive before the next structure.</summary>
    public const int TruncatedPrototypeBytes = 14;

    /// <summary>
    /// The DGROUP offset of the KILLED prototype — the record <c>engagement_kill_finalize @image@0x0C36B</c>
    /// stamps into a destroyed slot (<c>mov es:[di],0x2540</c> @image@0x0C3EA).
    /// </summary>
    public const int KilledPrototype = 0x2540;

    /// <summary>Its length: a prototype head plus the <c>0xFFFF</c> word that closes it, before the type-resource table at <c>[0x2550]</c>.</summary>
    public const int KilledPrototypeBytes = 16;

    /// <summary>The document's path in the data tree.</summary>
    public const string Path = "exe/tables/engagement.json";

    /// <inheritdoc/>
    public string TreePath => Path;

    /// <inheritdoc/>
    public string Description =>
        "the 19 engagement class prototypes (the aircraft STAT BLOCKS), their arc descriptors, arc " +
        "range tables and run-length band strings, the name run they all point into, the dropped " +
        "\"Man\" fragment and the KILLED prototype at [0x2540]";

    /// <summary>Every prototype's DGROUP offset mapped to the name its <c>+0x04</c> pointer names.</summary>
    /// <param name="image">The unpacked layer-1 image.</param>
    public static IReadOnlyDictionary<ushort, string> PrototypeNames(ReadOnlySpan<byte> image)
    {
        Dictionary<ushort, string> names = new Dictionary<ushort, string>();
        foreach (int dgroup in AircraftClassExeTable.PrototypeOffsets(image))
        {
            names[(ushort)dgroup] = ReadName(image, Word(image, dgroup + 0x04));
        }

        return names;
    }

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        IReadOnlyList<int> offsets = AircraftClassExeTable.PrototypeOffsets(image);
        SortedSet<int> claimed = new SortedSet<int>();

        List<ArcDescriptorDto> descriptors = new List<ArcDescriptorDto>(offsets.Count);
        List<ArcRangeTableDto> tables = new List<ArcRangeTableDto>(offsets.Count);
        List<EngagementPrototypeDto> prototypes = new List<EngagementPrototypeDto>(offsets.Count);
        int unknown = 0;

        foreach (int prototype in offsets)
        {
            prototypes.Add(ReadPrototype(image, prototype, PrototypeBytes, ref unknown));
            Claim(claimed, prototype, PrototypeBytes);

            // A descriptor owns a range table when the 30-byte reading's +0x1C is the head of a
            // sorted table that ends exactly where the descriptor starts; otherwise it is 28 bytes.
            int wide = prototype - DescriptorWithTableBytes;
            ushort head = Word(image, wide + 0x1C);
            int end = TableEnd(image, head);
            bool ownsTable = end == wide;
            int descriptor = ownsTable ? wide : prototype - DescriptorBytes;

            descriptors.Add(ReadDescriptor(image, descriptor, ownsTable, ref unknown));
            Claim(claimed, descriptor, ownsTable ? DescriptorWithTableBytes : DescriptorBytes);

            if (!ownsTable)
            {
                continue;
            }

            ArcRangeTableDto table = ReadRangeTable(image, head, claimed);
            tables.Add(table);
        }

        // The name run: from the head of the zone up to the first byte anything else claims.
        int firstClaim = claimed.Min;
        List<string> names = new List<string>();
        int cursor = NameTable;
        while (cursor < firstClaim && image[ExeAddresses.Image(cursor)] != 0)
        {
            string name = ReadName(image, (ushort)cursor);
            names.Add(name);
            cursor += name.Length + 1;
        }

        int namePad = firstClaim - cursor;
        Claim(claimed, NameTable, firstClaim - NameTable);

        EngagementPrototypeDto truncated = ReadPrototype(image, TruncatedPrototype, TruncatedPrototypeBytes, ref unknown);
        Claim(claimed, TruncatedPrototype, TruncatedPrototypeBytes);
        EngagementPrototypeDto killed = ReadPrototype(image, KilledPrototype, KilledPrototypeBytes, ref unknown);

        List<string> pads = new List<string>();
        for (int at = NameTable; at < ZoneEnd(offsets); at++)
        {
            if (claimed.Contains(at))
            {
                continue;
            }

            if (image[ExeAddresses.Image(at)] != 0 || (at & 1) == 0)
            {
                throw new InvalidDataException(
                    $"DGROUP 0x{at:X4} is inside the engagement zone, is claimed by no structure and " +
                    "is not an odd-address zero pad — the zone's tiling is wrong");
            }

            pads.Add(PortHex.Format(at));
        }

        EngagementDocumentDto dto = new EngagementDocumentDto
        {
            Format = "cyac.table.engagement/1",
            About =
                "The AI's per-class tuning surface, DGROUP [0x14F0..0x2540). An " +
                "s_engagement_class_proto IS an aircraft's 46-byte STAT BLOCK: the six " +
                "flyable aircraft's blocks are in here too, and the one the player flies " +
                "is copied into g_record_aircraft_data [0xEF22] at mission load. Each prototype is " +
                "preceded by its ARC DESCRIPTOR, the 28 bytes " +
                "engagement_arc_desc_init_and_heading_select @image@0x06E56 copies wholesale into " +
                "[0xED8E..0xEDA9] — which is why the descriptor's field names here ARE the " +
                "names of those globals. A 30-byte descriptor also owns a RANGE TABLE at +0x1C: " +
                "sorted 6-byte records looked up by the engagement's ALTITUDE band " +
                "(engagement_range_table_lookup @image@0x0864E), each pointing at a run-length BAND " +
                "STRING whose bytes give (b & 0x3F) << 4 arc units and whose two high bits are the " +
                "tier code (1 up, 2 down, 0 end). MEASURED HERE, 19/19: a prototype has exactly as " +
                "many non-zero +0x1A muzzle-offset triples as non-zero +0x0E weapon-slot pointers, " +
                "which is what makes those twelve bytes four signed {x,y,z} gun mounts rather than an " +
                "open block. The zone tiles completely; what is left over is 41 single zero bytes at " +
                "odd addresses (word alignment) and truncatedPrototype.",
            DgroupImageBase = PortHex.Format(ExeAddresses.DgroupImageBase, 5),
            Source = new DataSourceDto
            {
                Image = PortHex.Format(ExeAddresses.Image(NameTable), 5),
                Dgroup = PortHex.Format(NameTable),
                Bytes = ZoneEnd(offsets) - NameTable,
                Entries = prototypes.Count,
                ElementType = "s_engagement_class_proto",
            },
            NameTableDgroup = PortHex.Format(NameTable),
            Names = names,
            NamePadBytes = namePad,
            Prototypes = prototypes,
            ArcDescriptors = descriptors,
            RangeTables = tables,
            AlignmentPads = pads,
            TruncatedPrototype = truncated,
            KilledPrototype = killed,
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.EngagementDocumentDto),
            unknown,
            $"engagement: {prototypes.Count} prototypes, {descriptors.Count} arc descriptors, " +
            $"{tables.Count} range tables ({tables.Sum(t => t.Records!.Count)} records), " +
            $"{names.Count} names, {pads.Count} alignment pads");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        EngagementDocumentDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.EngagementDocumentDto)
                                    ?? throw new InvalidDataException("the engagement document is empty");
        List<ExeSlice> slices = new List<ExeSlice>();

        List<byte> names = new List<byte>();
        foreach (string name in dto.Names ?? [])
        {
            names.AddRange(Encoding.ASCII.GetBytes(name));
            names.Add(0);
        }

        names.AddRange(new byte[dto.NamePadBytes]);
        slices.Add(new ExeSlice(
            "engagement name run", ExeAddresses.Image(PortHex.Parse(dto.NameTableDgroup)), [.. names]));

        foreach (EngagementPrototypeDto prototype in dto.Prototypes ?? [])
        {
            slices.Add(WritePrototype(prototype, PrototypeBytes));
        }

        if (dto.TruncatedPrototype is { } dropped)
        {
            slices.Add(WritePrototype(dropped, TruncatedPrototypeBytes));
        }

        if (dto.KilledPrototype is { } killed)
        {
            slices.Add(WritePrototype(killed, KilledPrototypeBytes));
        }

        foreach (ArcDescriptorDto descriptor in dto.ArcDescriptors ?? [])
        {
            slices.Add(WriteDescriptor(descriptor));
        }

        foreach (ArcRangeTableDto table in dto.RangeTables ?? [])
        {
            int head = PortHex.Parse(table.Dgroup);
            List<ArcRangeRecordDto> records = table.Records ?? [];
            byte[] bytes = new byte[records.Count * RangeRecordBytes];
            for (int i = 0; i < records.Count; i++)
            {
                Span<byte> span = bytes.AsSpan(i * RangeRecordBytes);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    span, (ushort)PortHex.ParseOrDefault(records[i].BandDgroup));
                BinaryPrimitives.WriteInt16LittleEndian(span[2..], (short)records[i].Key);
                BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)records[i].ArcHeading);

                if (records[i].Bands is not { Count: > 0 } bands)
                {
                    continue;
                }

                byte[] band = new byte[bands.Count];
                for (int b = 0; b < bands.Count; b++)
                {
                    band[b] = (byte)(((bands[b].Width >> 4) & 0x3F) | ((bands[b].TierCode & 3) << 6));
                }

                slices.Add(new ExeSlice(
                    $"arc band string at 0x{PortHex.ParseOrDefault(records[i].BandDgroup):X4}",
                    ExeAddresses.Image(PortHex.ParseOrDefault(records[i].BandDgroup)),
                    band));
            }

            slices.Add(new ExeSlice(
                $"arc range table at 0x{head:X4}", ExeAddresses.Image(head), bytes));
        }

        foreach (string pad in dto.AlignmentPads ?? [])
        {
            slices.Add(new ExeSlice(
                $"alignment pad at 0x{PortHex.Parse(pad):X4}",
                ExeAddresses.Image(PortHex.Parse(pad)),
                [0]));
        }

        return slices;
    }

    private static int ZoneEnd(IReadOnlyList<int> prototypes) =>
        prototypes[^1] + PrototypeBytes;

    private static void Claim(SortedSet<int> claimed, int dgroup, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (!claimed.Add(dgroup + i))
            {
                throw new InvalidDataException(
                    $"two structures of the engagement zone both claim DGROUP 0x{dgroup + i:X4}");
            }
        }
    }

    private static int TableEnd(ReadOnlySpan<byte> image, ushort head)
    {
        if (head < NameTable || ExeAddresses.Image(head) + RangeRecordBytes > image.Length)
        {
            return -1;
        }

        for (int at = head, guard = 0; guard < 256; at += RangeRecordBytes, guard++)
        {
            if (BinaryPrimitives.ReadInt16LittleEndian(
                image[(ExeAddresses.Image(at) + 2)..]) == -1)
            {
                return at + RangeRecordBytes;
            }
        }

        return -1;
    }

    private static ArcRangeTableDto ReadRangeTable(
        ReadOnlySpan<byte> image, ushort head, SortedSet<int> claimed)
    {
        List<ArcRangeRecordDto> records = new List<ArcRangeRecordDto>();
        for (int at = head; ; at += RangeRecordBytes)
        {
            ushort band = Word(image, at);
            short key = unchecked((short)Word(image, at + 2));
            Claim(claimed, at, RangeRecordBytes);

            List<ArcBandDto>? bands = null;
            if (key != -1)
            {
                bands = [];
                for (int cursor = band; ; cursor++)
                {
                    byte b = image[ExeAddresses.Image(cursor)];
                    bands.Add(new ArcBandDto { Width = (b & 0x3F) << 4, TierCode = b >> 6 });
                    Claim(claimed, cursor, 1);
                    if ((b & 0xC0) == 0)
                    {
                        break;
                    }
                }
            }

            records.Add(new ArcRangeRecordDto
            {
                Dgroup = PortHex.Format(at),
                Key = key,
                ArcHeading = Word(image, at + 4),
                BandDgroup = PortHex.Format(band),
                Bands = bands,
            });

            if (key == -1)
            {
                break;
            }
        }

        return new ArcRangeTableDto { Dgroup = PortHex.Format(head), Records = records };
    }

    private static ArcDescriptorDto ReadDescriptor(
        ReadOnlySpan<byte> image, int dgroup, bool ownsTable, ref int unknown)
    {
        unknown += 2;
        return new ArcDescriptorDto
        {
            Dgroup = PortHex.Format(dgroup),
            Bytes = ownsTable ? DescriptorWithTableBytes : DescriptorBytes,
            BankScaleBase = Word(image, dgroup + 0x00),
            BankStepRate = Word(image, dgroup + 0x02),
            ShotAngleMax = Word(image, dgroup + 0x04),
            DescentHeadingMax = Word(image, dgroup + 0x06),
            AngleClamp = Word(image, dgroup + 0x08),
            ArcFloorMinimum = Word(image, dgroup + 0x0A),
            ArcAccumulatorHeading = Word(image, dgroup + 0x0C),
            ArcCeilingCap = Word(image, dgroup + 0x0E),
            ArcIncreaseRate = Word(image, dgroup + 0x10),
            AngleOverride = Word(image, dgroup + 0x12),
            RangeReference = Word(image, dgroup + 0x14),
            EngagementWindow = Word(image, dgroup + 0x16),
            WeaponTypeReference =
            [
                image[ExeAddresses.Image(dgroup + 0x18)], image[ExeAddresses.Image(dgroup + 0x19)],
            ],
            Unknown0x1A = PortHex.Bytes(image.Slice(ExeAddresses.Image(dgroup + 0x1A), 2)),
            RangeTableDgroup = ownsTable ? PortHex.Format(Word(image, dgroup + 0x1C)) : null,
        };
    }

    private static ExeSlice WriteDescriptor(ArcDescriptorDto dto)
    {
        int dgroup = PortHex.Parse(dto.Dgroup);
        byte[] bytes = new byte[dto.Bytes];
        void W(int at, int value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), unchecked((ushort)value));

        W(0x00, dto.BankScaleBase);
        W(0x02, dto.BankStepRate);
        W(0x04, dto.ShotAngleMax);
        W(0x06, dto.DescentHeadingMax);
        W(0x08, dto.AngleClamp);
        W(0x0A, dto.ArcFloorMinimum);
        W(0x0C, dto.ArcAccumulatorHeading);
        W(0x0E, dto.ArcCeilingCap);
        W(0x10, dto.ArcIncreaseRate);
        W(0x12, dto.AngleOverride);
        W(0x14, dto.RangeReference);
        W(0x16, dto.EngagementWindow);
        List<int> reference = dto.WeaponTypeReference ?? [0, 0];
        bytes[0x18] = (byte)reference[0];
        bytes[0x19] = (byte)reference.ElementAtOrDefault(1);
        PortHex.BytesOrZero(dto.Unknown0x1A, 2).CopyTo(bytes.AsSpan(0x1A));
        if (dto.Bytes == DescriptorWithTableBytes)
        {
            W(0x1C, PortHex.ParseOrDefault(dto.RangeTableDgroup));
        }

        return new ExeSlice(
            $"arc descriptor at 0x{dgroup:X4}", ExeAddresses.Image(dgroup), bytes);
    }

    private static EngagementPrototypeDto ReadPrototype(
        ReadOnlySpan<byte> image, int dgroup, int length, ref int unknown)
    {
        bool whole = length == PrototypeBytes;
        int at = ExeAddresses.Image(dgroup);
        ushort classRecord = Word(image, dgroup + 0x00);
        ushort namePointer = Word(image, dgroup + 0x04);

        List<PrototypeWeaponSlotDto>? slots = null;
        if (whole)
        {
            slots = [];
            for (int i = 0; i < WeaponSlots; i++)
            {
                ushort pointer = Word(image, dgroup + 0x0E + (i * 2));
                int offset = ExeAddresses.Image(dgroup + 0x1A + (i * 3));
                slots.Add(new PrototypeWeaponSlotDto
                {
                    Slot = i,
                    WeaponClass = pointer == 0
                        ? null
                        : (pointer - Core.Model.Combat.WeaponClass.StaticTableDgroupOffset)
                            / Core.Model.Combat.WeaponClass.RecordBytes,
                    WeaponClassDgroup = PortHex.Format(pointer),
                    MuzzleOffset =
                    [
                        (sbyte)image[offset], (sbyte)image[offset + 1], (sbyte)image[offset + 2],
                    ],
                });
            }
        }

        // Law L4: +0x02, +0x03, +0x0B and +0x0D have no identified reader on a whole record; the
        // 14-byte fragment carries the first four of them and nothing after; the 16-byte killed
        // prototype adds its closing word, whose only observed value is 0xFFFF and whose reader is
        // the whole-record slot-0 pointer read the killed record never takes.
        unknown += whole ? 7 : length == KilledPrototypeBytes ? 6 : 4;

        return new EngagementPrototypeDto
        {
            Name = ReadName(image, namePointer),
            Dgroup = PortHex.Format(dgroup),
            Image = PortHex.Format(at, 5),
            ClassRecord = ClassBasename.For(image, classRecord),
            ClassRecordDgroup = PortHex.Format(classRecord),
            NamePointer = PortHex.Format(namePointer),
            FireAuthorityDivisors =
            [
                image[at + 0x06], image[at + 0x07], image[at + 0x08],
            ],
            InitialHitPoints = image[at + 0x09],
            Armour = image[at + 0x0A],
            Flags = PortHex.Format(image[at + 0x0C], 2),
            WeaponSlots = slots,
            InitParams = whole
                ? [image[at + 0x16], image[at + 0x17], image[at + 0x18], image[at + 0x19]]
                : null,
            ArcDescriptorDgroup = whole ? PortHex.Format(Word(image, dgroup + 0x26)) : null,
            CountsTowardPlayerPressure = whole ? image[at + 0x28] : 0,
            InsigniaIndex = whole ? image[at + 0x2C] : 0,
            ExpirySeconds = whole ? image[at + 0x2D] : 0,
            Unknown0x02 = PortHex.Bytes(image.Slice(at + 0x02, 1)),
            Unknown0x03 = PortHex.Bytes(image.Slice(at + 0x03, 1)),
            Unknown0x0B = PortHex.Bytes(image.Slice(at + 0x0B, 1)),
            Unknown0x0D = PortHex.Bytes(image.Slice(at + 0x0D, 1)),
            Unknown0x29 = whole ? PortHex.Bytes(image.Slice(at + 0x29, 3)) : null,
            Unknown0x0E = length == KilledPrototypeBytes ? PortHex.Bytes(image.Slice(at + 0x0E, 2)) : null,
        };
    }

    private static ExeSlice WritePrototype(EngagementPrototypeDto dto, int length)
    {
        int dgroup = PortHex.Parse(dto.Dgroup);
        byte[] bytes = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes, (ushort)PortHex.Parse(dto.ClassRecordDgroup));
        PortHex.BytesOrZero(dto.Unknown0x02, 1).CopyTo(bytes.AsSpan(0x02));
        PortHex.BytesOrZero(dto.Unknown0x03, 1).CopyTo(bytes.AsSpan(0x03));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(0x04), (ushort)PortHex.Parse(dto.NamePointer));
        List<int> divisors = dto.FireAuthorityDivisors ?? [0, 0, 0];
        for (int i = 0; i < 3; i++)
        {
            bytes[0x06 + i] = (byte)divisors[i];
        }

        bytes[0x09] = (byte)dto.InitialHitPoints;
        bytes[0x0A] = (byte)dto.Armour;
        PortHex.BytesOrZero(dto.Unknown0x0B, 1).CopyTo(bytes.AsSpan(0x0B));
        bytes[0x0C] = (byte)PortHex.ParseOrDefault(dto.Flags);
        PortHex.BytesOrZero(dto.Unknown0x0D, 1).CopyTo(bytes.AsSpan(0x0D));
        if (length == KilledPrototypeBytes)
        {
            PortHex.BytesOrZero(dto.Unknown0x0E, 2).CopyTo(bytes.AsSpan(0x0E));
        }

        if (length == PrototypeBytes)
        {
            List<PrototypeWeaponSlotDto> slots = dto.WeaponSlots ?? [];
            for (int i = 0; i < WeaponSlots && i < slots.Count; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    bytes.AsSpan(0x0E + (i * 2)),
                    (ushort)PortHex.ParseOrDefault(slots[i].WeaponClassDgroup));
                List<int> muzzle = slots[i].MuzzleOffset ?? [0, 0, 0];
                for (int axis = 0; axis < 3; axis++)
                {
                    bytes[0x1A + (i * 3) + axis] = unchecked((byte)(sbyte)muzzle[axis]);
                }
            }

            List<int> init = dto.InitParams ?? [0, 0, 0, 0];
            for (int i = 0; i < 4; i++)
            {
                bytes[0x16 + i] = (byte)init[i];
            }

            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(0x26), (ushort)PortHex.ParseOrDefault(dto.ArcDescriptorDgroup));
            bytes[0x28] = (byte)dto.CountsTowardPlayerPressure;
            PortHex.BytesOrZero(dto.Unknown0x29, 3).CopyTo(bytes.AsSpan(0x29));
            bytes[0x2C] = (byte)dto.InsigniaIndex;
            bytes[0x2D] = (byte)dto.ExpirySeconds;
        }

        return new ExeSlice(
            $"engagement prototype at 0x{dgroup:X4}", ExeAddresses.Image(dgroup), bytes);
    }

    private static ushort Word(ReadOnlySpan<byte> image, int dgroup) =>
        BinaryPrimitives.ReadUInt16LittleEndian(image[ExeAddresses.Image(dgroup)..]);

    private static string ReadName(ReadOnlySpan<byte> image, ushort pointer)
    {
        int at = ExeAddresses.Image(pointer);
        StringBuilder text = new StringBuilder(16);
        while (at < image.Length && image[at] != 0 && text.Length < 32)
        {
            text.Append((char)image[at++]);
        }

        return text.ToString();
    }
}
