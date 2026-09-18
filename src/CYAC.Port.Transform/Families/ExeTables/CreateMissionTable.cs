using System.Buffers.Binary;
using System.Text.Json;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Mission;

namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>
/// The CREATE MISSION builder's formation offsets and altitude table →
/// <c>exe/tables/create_mission.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Formation offsets.</b>  <c>enemy_slot_fill_position @image@0x283A3</c> turns a formation row and
/// slot into a table address with <c>mov ax,slots / imul dx / add ax,[bp-2] / mov cx,record / imul cx
/// / mov si,ax / add si,table</c> (<c>image@0x283A9..0x283B8</c>).  The forward pass checks those seven
/// instructions and reads the three operands: the slots per row, the record size and the table's
/// DGROUP offset.  The row count is the range of <c>spawn_slot_idx_by_alliance_roll @image@0x2837C</c>
/// (<see cref="CustomMissionVocabulary.FormationRows"/>).  A layout other than the one the port's
/// builder indexes is refused.
/// </para>
/// <para>
/// <b>Altitude table.</b>  The builder's type-1 arm reads the player's altitude with <c>mov
/// bx,[bp-0xC] / mov bl,[bx+1] / sub bh,bh / shl bx,1 / mov ax,[bx+table] / sub dx,dx / mov cl,8</c>
/// (<c>image@0x27F56..0x27F66</c>): the picker row, doubled, indexes a table of words whose value is
/// then shifted up into the spawn position.  The forward pass checks those seven instructions and
/// reads the table's DGROUP offset from the word read; the row count is the ALTITUDE picker's
/// (<see cref="CustomMissionVocabulary.Altitude"/>).
/// </para>
/// <para>
/// The inverse re-encodes the formation table into its 90 bytes and the altitude table into its 12;
/// the instructions are only read, never re-encoded.
/// </para>
/// </remarks>
internal sealed class CreateMissionExeTable : IExeTable
{
    /// <summary>The image offset of the <c>mov ax,imm16</c> that starts the indexing.</summary>
    public const int IndexingImageOffset = 0x283A9;

    /// <summary>Bytes per formation record: three <c>i16</c>.</summary>
    public const int RecordBytes = 6;

    /// <summary>
    /// The image offset of the <c>mov bx,[bp-0xC]</c> that starts the builder's altitude read
    /// (<c>custom_mission_build_from_picks</c>' type-1 arm).
    /// </summary>
    public const int AltitudeReadImageOffset = 0x27F56;

    /// <summary>Bytes per altitude entry: one <c>u16</c>.</summary>
    public const int AltitudeEntryBytes = 2;

    /// <inheritdoc/>
    public string TreePath => CreateMissionTableDto.DataPath;

    /// <inheritdoc/>
    public string Description =>
        "the CREATE MISSION builder's formation offsets: one (x, y, z) triple per formation row and " +
        "slot, which enemy_slot_fill_position adds to a clause's origin; and its altitude table: the " +
        "feet each ALTITUDE picker row spawns the player at";

    /// <summary>Where the builder's altitude read says the altitude table is.</summary>
    /// <param name="image">The unpacked layer-1 image.</param>
    /// <returns>The table's DGROUP offset.</returns>
    /// <exception cref="InvalidDataException">The code is not the shape this build reads.</exception>
    public static int LocateAltitudes(ReadOnlySpan<byte> image)
    {
        int at = AltitudeReadImageOffset;
        InstructionOperands.Expect(image, at, "mov bx,[bp-0xC]", [0x8B, 0x5E, 0xF4]);
        InstructionOperands.Expect(image, at + 3, "mov bl,[bx+1]", [0x8A, 0x5F, 0x01]);
        InstructionOperands.Expect(image, at + 6, "sub bh,bh", [0x2A, 0xFF]);
        InstructionOperands.Expect(image, at + 8, "shl bx,1", [0xD1, 0xE3]);
        int dgroup = InstructionOperands.Word(image, at + 10, "mov ax,[bx+disp16]", [0x8B, 0x87]);
        InstructionOperands.Expect(image, at + 14, "sub dx,dx", [0x2B, 0xD2]);
        InstructionOperands.Expect(image, at + 16, "mov cl,8", [0xB1, 0x08]);

        int end = ExeAddresses.Image(dgroup) + (CustomMissionAltitudes.RowCount * AltitudeEntryBytes);
        if (end > image.Length)
        {
            throw new InvalidDataException(
                $"the altitude table at DGROUP 0x{dgroup:X4} (read at image@0x{at + 10:X5}) runs past the image");
        }

        return dgroup;
    }

    /// <summary>Where the indexing instructions say the table is, and its shape.</summary>
    /// <param name="image">The unpacked layer-1 image.</param>
    /// <returns>The table's DGROUP offset and its slots per row.</returns>
    /// <exception cref="InvalidDataException">The code is not the shape this build indexes.</exception>
    public static (int Dgroup, int SlotsPerRow) Locate(ReadOnlySpan<byte> image)
    {
        int at = IndexingImageOffset;
        int slotsPerRow = InstructionOperands.Word(image, at, "mov ax,imm16", [0xB8]);
        InstructionOperands.Expect(image, at + 3, "imul dx", [0xF7, 0xEA]);
        InstructionOperands.Expect(image, at + 5, "add ax,[bp+disp8]", [0x03, 0x46]);
        int recordBytes = InstructionOperands.Word(image, at + 8, "mov cx,imm16", [0xB9]);
        InstructionOperands.Expect(image, at + 11, "imul cx", [0xF7, 0xE9]);
        InstructionOperands.Expect(image, at + 13, "mov si,ax", [0x8B, 0xF0]);
        int dgroup = InstructionOperands.Word(image, at + 15, "add si,imm16", [0x81, 0xC6]);

        if (slotsPerRow != CustomMissionVocabulary.FormationSlotsPerRow || recordBytes != RecordBytes)
        {
            throw new InvalidDataException(
                $"enemy_slot_fill_position (image@0x{at:X5}) indexes {slotsPerRow} slots per row of " +
                $"{recordBytes}-byte records; the port's builder reads " +
                $"{CustomMissionVocabulary.FormationSlotsPerRow} slots of {RecordBytes}-byte records");
        }

        int end = ExeAddresses.Image(dgroup) + (CustomMissionFormations.SlotCount * RecordBytes);
        if (end > image.Length)
        {
            throw new InvalidDataException(
                $"the formation table at DGROUP 0x{dgroup:X4} (read at image@0x{at + 15:X5}) runs past the image");
        }

        return (dgroup, slotsPerRow);
    }

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        (int dgroup, int slotsPerRow) = Locate(image);
        int table = ExeAddresses.Image(dgroup);
        int rows = CustomMissionVocabulary.FormationRows;
        List<FormationOffsetDto> offsets = new List<FormationOffsetDto>(CustomMissionFormations.SlotCount);
        for (int row = 0; row < rows; row++)
        {
            for (int slot = 0; slot < slotsPerRow; slot++)
            {
                int at = table + (CustomMissionVocabulary.FormationSlotIndex(row, slot) * RecordBytes);
                offsets.Add(new FormationOffsetDto
                {
                    Row = row,
                    Slot = slot,
                    X = BinaryPrimitives.ReadInt16LittleEndian(image[at..]),
                    Y = BinaryPrimitives.ReadInt16LittleEndian(image[(at + 2)..]),
                    Z = BinaryPrimitives.ReadInt16LittleEndian(image[(at + 4)..]),
                });
            }
        }

        int altitudeDgroup = LocateAltitudes(image);
        int altitudeTable = ExeAddresses.Image(altitudeDgroup);
        List<int> feet = new List<int>(CustomMissionAltitudes.RowCount);
        for (int row = 0; row < CustomMissionAltitudes.RowCount; row++)
        {
            feet.Add(BinaryPrimitives.ReadUInt16LittleEndian(image[(altitudeTable + (row * AltitudeEntryBytes))..]));
        }

        CreateMissionTableDto dto = new CreateMissionTableDto
        {
            Format = CreateMissionTableDto.FormatTag,
            About =
                "The CREATE MISSION builder (custom_mission_build_from_picks @image@0x27E76) places each " +
                "enemy a clause asks for at the clause's origin plus one of these offsets, in world feet: " +
                "enemy_slot_fill_position @image@0x283A3 picks the triple at row*slotsPerRow + slot, where " +
                "the row is the alliance roll of spawn_slot_idx_by_alliance_roll @image@0x2837C and the slot " +
                "is the enemy's place in its clause. An allied class adds half of each offset again. The " +
                "table's address, the slots per row and the six-byte record are read from the indexing " +
                "instructions at indexedAt.",
            FormationOffsets = new FormationOffsetTableDto
            {
                Source = new DataSourceDto
                {
                    Image = PortHex.Format(table, 5),
                    Dgroup = PortHex.Format(dgroup),
                    Bytes = CustomMissionFormations.SlotCount * RecordBytes,
                    Entries = CustomMissionFormations.SlotCount,
                    ElementType = "i16[3]",
                },
                IndexedAt = PortHex.Format(IndexingImageOffset, 5),
                Rows = rows,
                SlotsPerRow = slotsPerRow,
                Offsets = offsets,
            },
            AltitudeFeet = new AltitudeFeetTableDto
            {
                About =
                    "The altitude each row of the form's ALTITUDE picker stands for, in feet: the builder's " +
                    "type-1 arm reads the word at row*2 and spawns the player at that altitude " +
                    "(g_player_spawn_pos.Y = feet << 8). The table's address is read from the instructions " +
                    "at indexedAt; the row count is the picker's.",
                Source = new DataSourceDto
                {
                    Image = PortHex.Format(altitudeTable, 5),
                    Dgroup = PortHex.Format(altitudeDgroup),
                    Bytes = CustomMissionAltitudes.RowCount * AltitudeEntryBytes,
                    Entries = CustomMissionAltitudes.RowCount,
                    ElementType = "u16",
                },
                IndexedAt = PortHex.Format(AltitudeReadImageOffset, 5),
                Rows = CustomMissionAltitudes.RowCount,
                Feet = feet,
            },
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.CreateMissionTableDto),
            0,
            $"create_mission: formation offsets, {rows} rows x {slotsPerRow} slots; altitudes, " +
            $"{CustomMissionAltitudes.RowCount} rows");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        CreateMissionTableDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.CreateMissionTableDto)
                                    ?? throw new InvalidDataException($"{CreateMissionTableDto.DataPath} is empty");
        CustomMissionFormations formations = CustomMissionFormations.Load(dto);
        DataSourceDto source = dto.FormationOffsets!.Source
                               ?? throw new InvalidDataException($"{CreateMissionTableDto.DataPath}: formationOffsets has no source");
        int image = PortHex.Parse(source.Image);
        int dgroup = PortHex.Parse(source.Dgroup);
        if (image != ExeAddresses.Image(dgroup)
            || source.Bytes != CustomMissionFormations.SlotCount * RecordBytes)
        {
            throw new InvalidDataException(
                $"{CreateMissionTableDto.DataPath}: formationOffsets.source says image {source.Image}, " +
                $"dgroup {source.Dgroup}, {source.Bytes} B; expected the DGROUP address and " +
                $"{CustomMissionFormations.SlotCount * RecordBytes} B");
        }

        byte[] bytes = new byte[CustomMissionFormations.SlotCount * RecordBytes];
        for (int row = 0; row < CustomMissionVocabulary.FormationRows; row++)
        {
            for (int slot = 0; slot < CustomMissionVocabulary.FormationSlotsPerRow; slot++)
            {
                (short x, short y, short z) = formations.Offset(row, slot);
                Span<byte> record = bytes.AsSpan(CustomMissionVocabulary.FormationSlotIndex(row, slot) * RecordBytes);
                BinaryPrimitives.WriteInt16LittleEndian(record, x);
                BinaryPrimitives.WriteInt16LittleEndian(record[2..], y);
                BinaryPrimitives.WriteInt16LittleEndian(record[4..], z);
            }
        }

        CustomMissionAltitudes altitudes = CustomMissionAltitudes.Load(dto);
        DataSourceDto altitudeSource = dto.AltitudeFeet!.Source
                                       ?? throw new InvalidDataException($"{CreateMissionTableDto.DataPath}: altitudeFeet has no source");
        int altitudeImage = PortHex.Parse(altitudeSource.Image);
        int altitudeDgroup = PortHex.Parse(altitudeSource.Dgroup);
        if (altitudeImage != ExeAddresses.Image(altitudeDgroup)
            || altitudeSource.Bytes != CustomMissionAltitudes.RowCount * AltitudeEntryBytes)
        {
            throw new InvalidDataException(
                $"{CreateMissionTableDto.DataPath}: altitudeFeet.source says image {altitudeSource.Image}, " +
                $"dgroup {altitudeSource.Dgroup}, {altitudeSource.Bytes} B; expected the DGROUP address and " +
                $"{CustomMissionAltitudes.RowCount * AltitudeEntryBytes} B");
        }

        byte[] altitudeBytes = new byte[CustomMissionAltitudes.RowCount * AltitudeEntryBytes];
        for (int row = 0; row < CustomMissionAltitudes.RowCount; row++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                altitudeBytes.AsSpan(row * AltitudeEntryBytes), (ushort)altitudes.Feet(row));
        }

        return
        [
            new ExeSlice("formation offsets", image, bytes),
            new ExeSlice("altitude table", altitudeImage, altitudeBytes),
        ];
    }
}
