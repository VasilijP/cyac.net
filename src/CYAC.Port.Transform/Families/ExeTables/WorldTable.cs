using System.Buffers.Binary;
using System.Text.Json;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>
/// The cloud deck's lattice offsets → <c>exe/tables/world.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>cloud_reposition_for_idx @image@0x2CD9E</c> reads a cloud's X with <c>mov cl,3 / mov bx,[bp+4] /
/// shl bx,cl / add dx,[bx+table] / adc ax,[bx+table+2]</c> (<c>image@0x2CDD2..0x2CDDD</c>) and its Z with
/// the same five instructions four bytes further on (<c>image@0x2CE56..0x2CE61</c>): the cloud index,
/// shifted by three, addresses eight-byte records of two <c>i32</c>.  The per-frame loop that calls it
/// runs <c>si</c> up to the <c>cmp si,imm8</c> at <c>image@0x2CD75</c>, which is the cloud count.  The
/// forward pass checks those instructions, reads the table's DGROUP offset, the record size and the
/// count, and refuses a shape other than the one <see cref="CloudDeckLattice"/> indexes.
/// </para>
/// <para>
/// The document holds the offsets as the image does, in position units, so the inverse re-encodes the
/// 72 bytes exactly; the instructions are only read, never re-encoded.
/// </para>
/// </remarks>
internal sealed class WorldExeTable : IExeTable
{
    /// <summary>The image offset of the <c>mov cl,3</c> that starts the X read.</summary>
    public const int XReadImageOffset = 0x2CDD2;

    /// <summary>The image offset of the <c>mov cl,3</c> that starts the Z read.</summary>
    public const int ZReadImageOffset = 0x2CE56;

    /// <summary>The image offset of the <c>cmp si,imm8</c> that bounds the per-frame loop.</summary>
    public const int CountImageOffset = 0x2CD75;

    /// <summary>Bytes per cloud record: two <c>i32</c>.</summary>
    public const int RecordBytes = 8;

    /// <inheritdoc/>
    public string TreePath => WorldTableDto.DataPath;

    /// <inheritdoc/>
    public string Description =>
        "the cloud deck's lattice: one (x, z) offset per cloud, which cloud_reposition_for_idx wraps " +
        "around the view anchor every frame";

    /// <summary>Where the per-frame placement says the cloud table is, and how many clouds it holds.</summary>
    /// <param name="image">The unpacked layer-1 image.</param>
    /// <returns>The table's DGROUP offset and the cloud count.</returns>
    /// <exception cref="InvalidDataException">The code is not the shape this build reads.</exception>
    public static (int Dgroup, int Clouds) LocateCloudOffsets(ReadOnlySpan<byte> image)
    {
        int x = ReadDisplacement(image, XReadImageOffset, "X");
        int z = ReadDisplacement(image, ZReadImageOffset, "Z");
        if (z != x + 4)
        {
            throw new InvalidDataException(
                $"cloud_reposition_for_idx reads X at DGROUP 0x{x:X4} (image@0x{XReadImageOffset + 7:X5}) and Z " +
                $"at 0x{z:X4} (image@0x{ZReadImageOffset + 7:X5}); the port reads Z four bytes after X");
        }

        InstructionOperands.Expect(image, CountImageOffset - 1, "inc si", [0x46]);
        int clouds = InstructionOperands.Imm8(image, CountImageOffset, "cmp si,imm8", [0x83, 0xFE]);
        if (clouds != CloudDeck.Count)
        {
            throw new InvalidDataException(
                $"the cloud loop (image@0x{CountImageOffset:X5}) places {clouds} clouds; the port's deck " +
                $"holds {CloudDeck.Count}");
        }

        int end = ExeAddresses.Image(x) + (clouds * RecordBytes);
        if (end > image.Length)
        {
            throw new InvalidDataException(
                $"the cloud table at DGROUP 0x{x:X4} (read at image@0x{XReadImageOffset + 7:X5}) runs past the image");
        }

        return (x, clouds);
    }

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        (int dgroup, int clouds) = LocateCloudOffsets(image);
        int table = ExeAddresses.Image(dgroup);
        List<CloudOffsetDto> offsets = new List<CloudOffsetDto>(clouds);
        for (int cloud = 0; cloud < clouds; cloud++)
        {
            int at = table + (cloud * RecordBytes);
            offsets.Add(new CloudOffsetDto
            {
                Cloud = cloud,
                X = BinaryPrimitives.ReadInt32LittleEndian(image[at..]),
                Z = BinaryPrimitives.ReadInt32LittleEndian(image[(at + 4)..]),
            });
        }

        WorldTableDto dto = new WorldTableDto
        {
            Format = WorldTableDto.FormatTag,
            About =
                "Constant tables that place things in the world the scene is drawn in, each found by reading " +
                "the instructions that index it.",
            CloudDeck = new CloudDeckTableDto
            {
                About =
                    "The cloud deck (cloud_deck_spawn @image@0x2CC38) is a lattice of clouds at one altitude. " +
                    "Every frame cloud_reposition_for_idx @image@0x2CD9E adds each cloud's offset to the view " +
                    "anchor's period base and wraps it by whole periods until it lies near the anchor, so the " +
                    "deck tiles the world. The offsets are in position units (world units x 256). The table's " +
                    "address and record size are read from the instructions at xReadAt and zReadAt, the cloud " +
                    "count from the loop bound at countedAt.",
                Source = new DataSourceDto
                {
                    Image = PortHex.Format(table, 5),
                    Dgroup = PortHex.Format(dgroup),
                    Bytes = clouds * RecordBytes,
                    Entries = clouds,
                    ElementType = "i32[2]",
                },
                XReadAt = PortHex.Format(XReadImageOffset, 5),
                ZReadAt = PortHex.Format(ZReadImageOffset, 5),
                CountedAt = PortHex.Format(CountImageOffset, 5),
                Clouds = clouds,
                Offsets = offsets,
            },
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.WorldTableDto),
            0,
            $"world: cloud deck, {clouds} offsets");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        WorldTableDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.WorldTableDto)
                            ?? throw new InvalidDataException($"{WorldTableDto.DataPath} is empty");
        _ = CloudDeckLattice.Load(dto);
        CloudDeckTableDto deck = dto.CloudDeck!;
        DataSourceDto source = deck.Source
                               ?? throw new InvalidDataException($"{WorldTableDto.DataPath}: cloudDeck has no source");
        int image = PortHex.Parse(source.Image);
        int dgroup = PortHex.Parse(source.Dgroup);
        if (image != ExeAddresses.Image(dgroup) || source.Bytes != CloudDeck.Count * RecordBytes)
        {
            throw new InvalidDataException(
                $"{WorldTableDto.DataPath}: cloudDeck.source says image {source.Image}, dgroup {source.Dgroup}, " +
                $"{source.Bytes} B; expected the DGROUP address and {CloudDeck.Count * RecordBytes} B");
        }

        byte[] bytes = new byte[CloudDeck.Count * RecordBytes];
        foreach (CloudOffsetDto offset in deck.Offsets!)
        {
            Span<byte> record = bytes.AsSpan(offset.Cloud * RecordBytes);
            BinaryPrimitives.WriteInt32LittleEndian(record, (int)offset.X);
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], (int)offset.Z);
        }

        return [new ExeSlice("cloud deck offsets", image, bytes)];
    }

    // mov cl,3 / mov bx,[bp+4] / shl bx,cl / add dx,[bx+disp16] / adc ax,[bx+disp16+2]
    private static int ReadDisplacement(ReadOnlySpan<byte> image, int at, string axis)
    {
        int shift = InstructionOperands.Imm8(image, at, "mov cl,imm8", [0xB1]);
        if ((1 << shift) != RecordBytes)
        {
            throw new InvalidDataException(
                $"cloud_reposition_for_idx (image@0x{at:X5}) indexes {1 << shift}-byte records; the port reads " +
                $"{RecordBytes}-byte records");
        }

        InstructionOperands.Expect(image, at + 2, "mov bx,[bp+4]", [0x8B, 0x5E, 0x04]);
        InstructionOperands.Expect(image, at + 5, "shl bx,cl", [0xD3, 0xE3]);
        int low = InstructionOperands.Word(image, at + 7, "add dx,[bx+disp16]", [0x03, 0x97]);
        int high = InstructionOperands.Word(image, at + 11, "adc ax,[bx+disp16]", [0x13, 0x87]);
        if (high != low + 2)
        {
            throw new InvalidDataException(
                $"cloud_reposition_for_idx reads the {axis} offset's low word at 0x{low:X4} and its high word at " +
                $"0x{high:X4} (image@0x{at + 7:X5}); an i32 keeps them two bytes apart");
        }

        return low;
    }
}
