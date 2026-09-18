using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Flight;

namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>
/// Reads the BASENAME of a <c>s_mesh_registry_slot</c> / class record from its DGROUP offset.
/// </summary>
/// <remarks>
/// Every one of those records is self-labelling: <c>+0x22</c> is a near pointer at a NUL-terminated
/// lowercase basename, relative to the record's own geometry segment at <c>+0x26</c> (law 6,
/// and the same rule <see cref="ExeMeshCodec"/> decodes the registry with).  The tables in this file
/// use it so a pointer can be published as a NAME beside its offset.
/// </remarks>
internal static class ClassBasename
{
    /// <summary>The longest basename the shipped records carry, plus room for the terminator.</summary>
    public const int MaxLength = 12;

    /// <summary>The record's basename, or null when the offset does not label itself like one.</summary>
    /// <param name="image">The unpacked layer-1 image.</param>
    /// <param name="dgroup">A DGROUP offset that should be a class record or registry slot.</param>
    public static string? For(ReadOnlySpan<byte> image, int dgroup)
    {
        int record = ExeAddresses.Image(dgroup);
        if (dgroup <= 0 || record + 0x28 > image.Length)
        {
            return null;
        }

        ushort segment = BinaryPrimitives.ReadUInt16LittleEndian(image[(record + 0x26)..]);
        int geometryBase = segment == 0
            ? ExeAddresses.DgroupImageBase
            : (segment * 16) - 0x10000;
        int at = geometryBase + BinaryPrimitives.ReadUInt16LittleEndian(image[(record + 0x22)..]);
        if (at <= 0 || at + MaxLength > image.Length)
        {
            return null;
        }

        StringBuilder text = new StringBuilder(MaxLength);
        for (int i = 0; i < MaxLength; i++)
        {
            byte b = image[at + i];
            if (b == 0)
            {
                return text.Length > 0 ? text.ToString() : null;
            }

            if (b is < 0x20 or > 0x7E)
            {
                return null;
            }

            text.Append((char)b);
        }

        return null;
    }
}

/// <summary>
/// The 46-entry AIRCRAFT-CLASS table at <c>image@0x34F90</c> — the join from an authored mission
/// class id to what the engine spawns for it.
/// </summary>
/// <remarks>
/// The only table the running port read at an ABSOLUTE image offset rather than through DGROUP.
/// Its three arms are the <c>kind</c> field; a flag-0 row that is not a sentinel names an
/// engagement prototype and a flag-1 row names a class record, both published by NAME beside the
/// DGROUP offset the kernel actually compares.
/// </remarks>
internal sealed class AircraftClassExeTable : IExeTable
{
    /// <summary>The table's absolute image offset — <c>g_aircraft_class_table_seg [0xB102]</c> + 0x30.</summary>
    public const int TableImage = 0x34F90;

    /// <summary>How many 3-byte records it holds.</summary>
    public const int Entries = 46;

    /// <summary>Bytes per record.</summary>
    public const int RecordBytes = 3;

    /// <summary>The document's path in the data tree.</summary>
    public const string Path = "exe/tables/aircraft_classes.json";

    /// <inheritdoc/>
    public string TreePath => Path;

    /// <inheritdoc/>
    public string Description =>
        "the 46-entry aircraft-class table: what a mission's authored class id spawns as — a player " +
        "or home-base sentinel, an engagement prototype, or a plain scenery class record";

    /// <summary>Reads the shipped prototype offsets (the flag-0, non-sentinel rows) in table order.</summary>
    /// <param name="image">The unpacked layer-1 image.</param>
    public static IReadOnlyList<int> PrototypeOffsets(ReadOnlySpan<byte> image)
    {
        SortedSet<int> offsets = new SortedSet<int>();
        for (int i = 0; i < Entries; i++)
        {
            int at = TableImage + (i * RecordBytes);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(image[(at + 1)..]);
            if (image[at] == 0 && value is not (0 or 0xFFFF or 0xFFFE))
            {
                offsets.Add(value);
            }
        }

        return [.. offsets];
    }

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        IReadOnlyDictionary<ushort, string> names = EngagementExeTable.PrototypeNames(image);
        List<AircraftClassEntryDto> entries = new List<AircraftClassEntryDto>(Entries);
        for (int i = 0; i < Entries; i++)
        {
            int at = TableImage + (i * RecordBytes);
            byte flag = image[at];
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(image[(at + 1)..]);
            (string kind, string? target) = (flag, value) switch
            {
                (0, 0xFFFF) => ("sentinel", "player"),
                (0, 0xFFFE) => ("sentinel", "homeBase"),
                (0, _) => ("engagementPrototype", names.GetValueOrDefault(value)),
                (1, _) => ("classRecord", ClassBasename.For(image, value)),
                _ => ("indexOnly", null),
            };

            entries.Add(new AircraftClassEntryDto
            {
                ClassId = i,
                Kind = kind,
                Flag = flag,
                Target = target,
                Value = PortHex.Format(value),
            });
        }

        AircraftClassTableDto dto = new AircraftClassTableDto
        {
            Format = "cyac.table.aircraftClasses/1",
            About =
                "aircraft_class_table_lookup @image@0x24058 reads a 3-byte record {u8 flag, u16 value} " +
                "at idx*3 + 0x30 in the segment g_aircraft_class_table_seg [0xB102], a static linker " +
                "fixup at image@0x34F60 with zero writers image-wide. Three arms: " +
                "flag 0 returns the value in AX (ids 0 and 1 are the player/home-base sentinels " +
                "0xFFFF/0xFFFE, the rest are DGROUP engagement-prototype pointers); flag 1 stores the " +
                "value through the caller's OUT pointer and returns 0, so the object spawns as plain " +
                "scenery with no engagement block; flag 2 stores nothing and returns the index it was " +
                "passed, and no shipped mission authors one. `target` names what the word points at; " +
                "`value` is the word itself, which is the handle the kernel passes around and the key " +
                "exe/tables/engagement.json and exe/classes.json are indexed by.",
            Source = new DataSourceDto
            {
                Image = PortHex.Format(TableImage, 5),
                Bytes = Entries * RecordBytes,
                Entries = Entries,
                ElementType = "{u8 flag, u16 value}",
            },
            Entries = entries,
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.AircraftClassTableDto),
            0,
            $"aircraft_classes: {entries.Count} rows " +
            $"({entries.Count(e => e.Kind == "engagementPrototype")} prototypes, " +
            $"{entries.Count(e => e.Kind == "classRecord")} class records)");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        AircraftClassTableDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.AircraftClassTableDto)
                                    ?? throw new InvalidDataException("the aircraft-class document is empty");
        if (dto.Entries is not { } entries || entries.Count != Entries)
        {
            throw new InvalidDataException(
                $"expected {Entries} aircraft-class rows, found {dto.Entries?.Count ?? 0}");
        }

        byte[] bytes = new byte[Entries * RecordBytes];
        for (int i = 0; i < entries.Count; i++)
        {
            bytes[i * RecordBytes] = (byte)entries[i].Flag;
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan((i * RecordBytes) + 1), (ushort)PortHex.Parse(entries[i].Value));
        }

        return [new ExeSlice("aircraft-class table", TableImage, bytes)];
    }
}

/// <summary>
/// The small CONSTANT combat tables scattered through DGROUP — everything the combat kernel used to
/// take out of <c>data/exe/image.l1.bin</c> that is not a prototype, a class record or a weapon.
/// </summary>
internal sealed class CombatConstantsExeTable : IExeTable
{
    /// <summary><c>g_vec2_origin_pivot [0x0680]</c>.</summary>
    public const int OriginPivot = 0x0680;

    /// <summary><c>g_aircraft_type_fire_bonus_u8x4 [0x0DEC]</c>.</summary>
    public const int FireBonus = 0x0DEC;

    /// <summary><c>g_engagement_phase_attr_table [0x0F0E]</c>, 14 bytes.</summary>
    public const int PhaseAttributes = 0x0F0E;

    /// <summary>The skill-indexed tuning block, 16 rows of four bytes.</summary>
    public const int SkillTables = 0x0F40;

    /// <summary>How many rows the skill block holds.</summary>
    public const int SkillRows = 16;

    /// <summary><c>g_flyable_statblock_ptr_table [0x0FBC]</c>, six near pointers.</summary>
    public const int FlyablePrototypeTable = 0x0FBC;

    /// <summary><c>g_type_resource_table [0x2550]</c>, 14 entries plus a zero terminator.</summary>
    public const int TypeResourceTable = 0x2550;

    /// <summary>How many <c>{type, resource}</c> entries the table holds before its terminator.</summary>
    public const int TypeResourceEntries = 14;

    /// <summary>The three per-difficulty admission tables.</summary>
    public const int AdmissionProbability = 0x2A10;

    /// <summary>The admission interval table.</summary>
    public const int AdmissionInterval = 0x2A14;

    /// <summary>The concurrent-engagement cap table.</summary>
    public const int AdmissionCap = 0x2A18;

    /// <summary><c>g_engagement_duration_table [0x45A2]</c>, six words.</summary>
    public const int EngagementDuration = 0x45A2;

    /// <summary>The document's path in the data tree.</summary>
    public const string Path = "exe/tables/combat_constants.json";

    /// <summary>
    /// The two DGROUP bytes the shipped null dereference at <c>image@0x035BF</c> / <c>0x035C5</c>
    /// reads — see <see cref="NullClassProbeDto"/>.
    /// </summary>
    public static readonly int[] NullProbeOffsets = [0x0000, 0x0024];

    private static readonly (int Row, string Role)[] SkillRoles =
    [
        (0x0F40, "the AI VM's mode-7 probability gate (image@0x051F4)"),
        (0x0F70, "the manoeuvre ENVELOPE table (engagement_slot_angle_update @image@0x06C1C)"),
        (0x0F74, "the ENGAGE-régime aim record (target selection @image@0x07ABD)"),
        (0x0F7C, "g_acq_aim_bias_gain_table, indexed by the skill level (image@0x082A9)"),
    ];

    /// <inheritdoc/>
    public string TreePath => Path;

    /// <inheritdoc/>
    public string Description =>
        "the small constant combat tables: the fire bonus, the per-phase attributes, the sixteen " +
        "skill-indexed tuning rows, the flyable prototype pointers, the type→resource table, the " +
        "three difficulty tables and the per-aircraft engagement duration";

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        IReadOnlyDictionary<ushort, string> names = EngagementExeTable.PrototypeNames(image);

        List<SkillTableRowDto> skill = new List<SkillTableRowDto>(SkillRows);
        for (int i = 0; i < SkillRows; i++)
        {
            int row = SkillTables + (i * 4);
            skill.Add(new SkillTableRowDto
            {
                Dgroup = PortHex.Format(row),
                Role = SkillRoles.FirstOrDefault(r => r.Row == row).Role,
                BySkill = [.. Bytes(image, row, 4)],
            });
        }

        List<FlyablePrototypeDto> flyable = new List<FlyablePrototypeDto>(AircraftDefinition.FlyableBasenames.Count);
        for (int i = 0; i < AircraftDefinition.FlyableBasenames.Count; i++)
        {
            ushort pointer = Word(image, FlyablePrototypeTable + (i * 2));
            if (!names.TryGetValue(pointer, out string? name))
            {
                throw new InvalidDataException(
                    $"g_flyable_statblock_ptr_table[{i}] = 0x{pointer:X4} is not an engagement " +
                    "prototype the class table names");
            }

            flyable.Add(new FlyablePrototypeDto
            {
                Aircraft = AircraftDefinition.FlyableBasenames[i],
                Prototype = name,
                Dgroup = PortHex.Format(pointer),
            });
        }

        List<NullClassProbeDto> probes = new List<NullClassProbeDto>(NullProbeOffsets.Length);
        foreach (int offset in NullProbeOffsets)
        {
            probes.Add(new NullClassProbeDto
            {
                Dgroup = PortHex.Format(offset),
                Value = image[ExeAddresses.Image(offset)],
                BelongsTo = "the Microsoft C run-time copyright banner the linker places at DGROUP 0",
            });
        }

        List<TypeResourceEntryDto> typeResources = new List<TypeResourceEntryDto>(TypeResourceEntries + 1);
        for (int i = 0; i <= TypeResourceEntries; i++)
        {
            int at = TypeResourceTable + (i * 4);
            ushort type = Word(image, at);
            ushort resource = Word(image, at + 2);
            typeResources.Add(new TypeResourceEntryDto
            {
                Dgroup = PortHex.Format(at),
                Type = ClassBasename.For(image, type),
                Resource = ClassBasename.For(image, resource),
                TypeDgroup = PortHex.Format(type),
                ResourceDgroup = PortHex.Format(resource),
            });
        }

        CombatConstantsDto dto = new CombatConstantsDto
        {
            Format = "cyac.table.combatConstants/1",
            About =
                "Compile-time constants with no writer image-wide that the combat kernel reads every " +
                "frame — the most modder-visible numbers in the game and, until this document, the " +
                "reason a running port still opened data/exe/image.l1.bin. " +
                "aircraftTypeFireBonus is indexed by engagementBlock[+0x05] & 3 " +
                "(mov al,[bx+0xdec]); phaseAttributes by the node phase (bit0 run-acquisition, " +
                "bit2 the non-player GEAR-DOWN bit, bit3 block-ground-impact, bit4 skip-cap); every " +
                "skillTables row by g_acq_skill_level [0xED59] & 3; the three admission tables by " +
                "g_briefing_difficulty_idx; engagementDurationByAircraft by the active aircraft index " +
                "(engagement_state_init @0x0F6BC). The type→resource pairs are DGROUP pointers at " +
                "class records / registry slots, named here and keyed by the offset the kernel " +
                "compares; the table ends in a zero entry, which is included so the walk terminates.",
            DgroupImageBase = PortHex.Format(ExeAddresses.DgroupImageBase, 5),
            OriginPivot = [.. Words(image, OriginPivot, 2).Select(w => (int)(short)w)],
            AircraftTypeFireBonus = [.. Bytes(image, FireBonus, 4)],
            PhaseAttributes = [.. Bytes(image, PhaseAttributes, 14)],
            SkillTables = skill,
            FlyablePrototypes = flyable,
            NullClassProbe = probes,
            TypeResources = typeResources,
            AdmissionProbabilityByDifficulty = [.. Bytes(image, AdmissionProbability, 4)],
            AdmissionIntervalByDifficulty = [.. Bytes(image, AdmissionInterval, 4)],
            AdmissionCapByDifficulty = [.. Bytes(image, AdmissionCap, 4)],
            EngagementDurationByAircraft = [.. Words(image, EngagementDuration, 6).Select(w => (int)w)],
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.CombatConstantsDto),
            0,
            $"combat_constants: {skill.Count} skill rows, {typeResources.Count} type→resource " +
            $"entries, {flyable.Count} flyable prototypes");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        CombatConstantsDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.CombatConstantsDto)
                                 ?? throw new InvalidDataException("the combat-constants document is empty");

        List<ExeSlice> slices = new List<ExeSlice>
        {
            Slice("origin pivot", OriginPivot, WordBytes(dto.OriginPivot, 2)),
            Slice("aircraft-type fire bonus", FireBonus, ByteBytes(dto.AircraftTypeFireBonus, 4)),
            Slice("engagement phase attributes", PhaseAttributes, ByteBytes(dto.PhaseAttributes, 14)),
            Slice("admission probability", AdmissionProbability, ByteBytes(dto.AdmissionProbabilityByDifficulty, 4)),
            Slice("admission interval", AdmissionInterval, ByteBytes(dto.AdmissionIntervalByDifficulty, 4)),
            Slice("admission cap", AdmissionCap, ByteBytes(dto.AdmissionCapByDifficulty, 4)),
            Slice("engagement duration", EngagementDuration, WordBytes(dto.EngagementDurationByAircraft, 6)),
        };

        if (dto.SkillTables is not { } rows || rows.Count != SkillRows)
        {
            throw new InvalidDataException(
                $"expected {SkillRows} skill rows, found {dto.SkillTables?.Count ?? 0}");
        }

        byte[] skill = new byte[SkillRows * 4];
        for (int i = 0; i < rows.Count; i++)
        {
            ByteBytes(rows[i].BySkill, 4).CopyTo(skill.AsSpan(i * 4));
        }

        slices.Add(Slice("skill-indexed tuning rows", SkillTables, skill));

        if (dto.TypeResources is not { } typeResources
            || typeResources.Count != TypeResourceEntries + 1)
        {
            throw new InvalidDataException(
                $"expected {TypeResourceEntries + 1} type→resource entries, found " +
                $"{dto.TypeResources?.Count ?? 0}");
        }

        byte[] pairs = new byte[typeResources.Count * 4];
        for (int i = 0; i < typeResources.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                pairs.AsSpan(i * 4), (ushort)PortHex.Parse(typeResources[i].TypeDgroup));
            BinaryPrimitives.WriteUInt16LittleEndian(
                pairs.AsSpan((i * 4) + 2), (ushort)PortHex.Parse(typeResources[i].ResourceDgroup));
        }

        slices.Add(Slice("type→resource table", TypeResourceTable, pairs));

        if (dto.FlyablePrototypes is not { } flyable
            || flyable.Count != AircraftDefinition.FlyableBasenames.Count)
        {
            throw new InvalidDataException(
                $"expected {AircraftDefinition.FlyableBasenames.Count} flyable prototype pointers, " +
                $"found {dto.FlyablePrototypes?.Count ?? 0}");
        }

        byte[] pointers = new byte[flyable.Count * 2];
        for (int i = 0; i < flyable.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                pointers.AsSpan(i * 2), (ushort)PortHex.Parse(flyable[i].Dgroup));
        }

        slices.Add(Slice("flyable stat-block pointer table", FlyablePrototypeTable, pointers));

        foreach (NullClassProbeDto probe in dto.NullClassProbe ?? [])
        {
            slices.Add(Slice(
                $"null-dereference probe byte at 0x{PortHex.Parse(probe.Dgroup):X4}",
                PortHex.Parse(probe.Dgroup),
                [(byte)probe.Value]));
        }

        return slices;
    }

    /// <summary>
    /// The DGROUP offset of one flyable aircraft's engagement prototype, for the runtime loader.
    /// </summary>
    /// <param name="index">The flyable aircraft index 0..5.</param>
    public static int FlyablePrototypePointer(int index) => FlyablePrototypeTable + (index * 2);

    private static ExeSlice Slice(string name, int dgroup, byte[] bytes) =>
        new(name, ExeAddresses.Image(dgroup), bytes);

    private static IEnumerable<int> Bytes(ReadOnlySpan<byte> image, int dgroup, int count)
    {
        int[] values = new int[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = image[ExeAddresses.Image(dgroup) + i];
        }

        return values;
    }

    private static IEnumerable<ushort> Words(ReadOnlySpan<byte> image, int dgroup, int count)
    {
        ushort[] values = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = Word(image, dgroup + (i * 2));
        }

        return values;
    }

    private static ushort Word(ReadOnlySpan<byte> image, int dgroup) =>
        BinaryPrimitives.ReadUInt16LittleEndian(image[ExeAddresses.Image(dgroup)..]);

    private static byte[] ByteBytes(List<int>? values, int count)
    {
        if (values is null || values.Count != count)
        {
            throw new InvalidDataException($"expected {count} byte values, found {values?.Count ?? 0}");
        }

        return [.. values.Select(v => (byte)v)];
    }

    private static byte[] WordBytes(List<int>? values, int count)
    {
        if (values is null || values.Count != count)
        {
            throw new InvalidDataException($"expected {count} word values, found {values?.Count ?? 0}");
        }

        byte[] bytes = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), unchecked((ushort)values[i]));
        }

        return bytes;
    }
}

/// <summary>
/// The FLIGHT-side DGROUP constants: the pull-up tuning table and the landing-zone capture radius.
/// </summary>
/// <remarks>
/// H2 §E3's ask.  Both are compile-time constants with no writer image-wide, and both were read
/// straight out of <c>DataTree.ProgramImage</c> until this document existed.
/// </remarks>
internal sealed class FlightTuningExeTable : IExeTable
{
    /// <summary>The pull-up tuning table, <c>g_joystick_calib_table_BASE [0x35F8]</c>.</summary>
    public const int PullUpTuning = 0x35F8;

    /// <summary>How many words it holds.</summary>
    public const int PullUpWords = 7;

    /// <summary>The landing-zone capture radius, <c>[0x9E74]</c>, as an <c>i32</c>.</summary>
    public const int CaptureRadius = 0x9E74;

    /// <summary>The document's path in the data tree.</summary>
    public const string Path = "exe/tables/flight_tuning.json";

    /// <inheritdoc/>
    public string TreePath => Path;

    /// <inheritdoc/>
    public string Description =>
        "the flight-side compile-time constants: the seven-word pull-up tuning table and the " +
        "landing-zone capture radius";

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        List<int> words = new List<int>(PullUpWords);
        for (int i = 0; i < PullUpWords; i++)
        {
            words.Add(BinaryPrimitives.ReadUInt16LittleEndian(
                image[(ExeAddresses.Image(PullUpTuning) + (i * 2))..]));
        }

        int radius = BinaryPrimitives.ReadInt32LittleEndian(
            image[ExeAddresses.Image(CaptureRadius)..]);

        FlightTuningDto dto = new FlightTuningDto
        {
            Format = "cyac.table.flightTuning/1",
            About =
                "Two constants the flight cold start needs and no document carried. " +
                "pullUpTuning is the seven-word block at " +
                "g_joystick_calib_table_BASE [0x35F8] — shift counts and clamp bounds with zero write " +
                "sites image-wide — which FlightColdStart seeds PullUpTuningTable from. " +
                "landingZoneCaptureRadius is the i32 at g_landing_zone_capture_radius [0x9E74]: each " +
                "axis's excess is max(0, |player - centre|) and the zone is entered when the larger " +
                "excess is zero, i.e. a Chebyshev square of this half-width.",
            DgroupImageBase = PortHex.Format(ExeAddresses.DgroupImageBase, 5),
            PullUpTuning = words,
            LandingZoneCaptureRadius = radius,
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.FlightTuningDto),
            0,
            $"flight_tuning: {words.Count} pull-up words, capture radius 0x{radius:X8}");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        FlightTuningDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.FlightTuningDto)
                              ?? throw new InvalidDataException("the flight-tuning document is empty");
        if (dto.PullUpTuning is not { } words || words.Count != PullUpWords)
        {
            throw new InvalidDataException(
                $"expected {PullUpWords} pull-up words, found {dto.PullUpTuning?.Count ?? 0}");
        }

        byte[] table = new byte[PullUpWords * 2];
        for (int i = 0; i < words.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(i * 2), unchecked((ushort)words[i]));
        }

        byte[] radius = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(radius, dto.LandingZoneCaptureRadius);

        return
        [
            new ExeSlice("pull-up tuning table", ExeAddresses.Image(PullUpTuning), table),
            new ExeSlice("landing-zone capture radius", ExeAddresses.Image(CaptureRadius), radius),
        ];
    }
}
