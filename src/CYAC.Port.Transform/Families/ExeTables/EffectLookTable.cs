using System.Buffers.Binary;
using System.Text.Json;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>
/// The explosion's debris angle tables → <c>exe/tables/effect_look.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each shard loop of <c>deferred_effect_render</c> bounds its index with an <c>and</c> and reads the
/// angle with <c>shl bx,1 / push word [bx+table]</c>: the disc arm at <c>image@0x03D67</c> /
/// <c>image@0x03D6C</c>, the burst arm at <c>image@0x040CA</c> / <c>image@0x040DC</c>.  The forward
/// pass checks those instructions and reads the mask (the table holds mask + 1 entries) and the
/// table's DGROUP offset from them; a count other than the one the port draws is refused.
/// </para>
/// <para>
/// The inverse re-encodes the two tables into their bytes; the instructions are only read.
/// </para>
/// </remarks>
internal sealed class EffectLookExeTable : IExeTable
{
    /// <summary>The disc arm's <c>and bx,imm8</c>.</summary>
    public const int ShardMaskImageOffset = 0x03D67;

    /// <summary>The disc arm's <c>push word [bx+disp16]</c>.</summary>
    public const int ShardReadImageOffset = 0x03D6C;

    /// <summary>The burst arm's <c>and ax,imm16</c>.</summary>
    public const int BurstMaskImageOffset = 0x040CA;

    /// <summary>The burst arm's <c>push word [bx+disp16]</c>.</summary>
    public const int BurstReadImageOffset = 0x040DC;

    /// <inheritdoc/>
    public string TreePath => EffectLookTableDto.DataPath;

    /// <inheritdoc/>
    public string Description =>
        "the explosion's debris angle tables: the BAM angles the disc arm and the burst arm of " +
        "deferred_effect_render place their shards at";

    /// <summary>Where the disc arm's code says its table is, and how many entries it holds.</summary>
    /// <param name="image">The unpacked layer-1 image.</param>
    /// <returns>The table's DGROUP offset and entry count.</returns>
    public static (int Dgroup, int Entries) LocateShardTable(ReadOnlySpan<byte> image)
    {
        int mask = InstructionOperands.Imm8(image, ShardMaskImageOffset, "and bx,imm8", [0x83, 0xE3]);
        return Locate(image, mask, ShardReadImageOffset, EffectDebrisAngles.ShardCount, "disc arm");
    }

    /// <summary>Where the burst arm's code says its table is, and how many entries it holds.</summary>
    /// <param name="image">The unpacked layer-1 image.</param>
    /// <returns>The table's DGROUP offset and entry count.</returns>
    public static (int Dgroup, int Entries) LocateBurstTable(ReadOnlySpan<byte> image)
    {
        int mask = InstructionOperands.Word(image, BurstMaskImageOffset, "and ax,imm16", [0x25]);
        return Locate(image, mask, BurstReadImageOffset, EffectDebrisAngles.BurstShardCount, "burst arm");
    }

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        AngleTableDto shard = Section(image, LocateShardTable(image), ShardMaskImageOffset, ShardReadImageOffset);
        AngleTableDto burst = Section(image, LocateBurstTable(image), BurstMaskImageOffset, BurstReadImageOffset);
        EffectLookTableDto dto = new EffectLookTableDto
        {
            Format = EffectLookTableDto.FormatTag,
            About =
                "The look of deferred_effect_render @image@0x03E18. shardAngles are the BAM angles " +
                "(2880 units to the circle) at which effect_particle_draw @image@0x03C5A places the shards " +
                "around a growing explosion disc; burstShardAngles are the ones its burst arm " +
                "(image@0x03E6C) uses. Both loops read entry (sequence + i) & mask, where sequence is the " +
                "effect record's +0x0A byte and i the shard. Each table's address and size are read from " +
                "its loop: maskedAt is the and that bounds the index, readAt the push word [bx+table].",
            ShardAngles = shard,
            BurstShardAngles = burst,
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.EffectLookTableDto),
            0,
            $"effect_look: debris angles, {shard.Values!.Count} + {burst.Values!.Count}");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        EffectLookTableDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.EffectLookTableDto)
                                 ?? throw new InvalidDataException($"{EffectLookTableDto.DataPath} is empty");
        EffectDebrisAngles angles = EffectDebrisAngles.Load(dto);
        return
        [
            Slice("shard angles", dto.ShardAngles!, angles.Shard),
            Slice("burst shard angles", dto.BurstShardAngles!, angles.Burst),
        ];
    }

    private static (int Dgroup, int Entries) Locate(
        ReadOnlySpan<byte> image, int mask, int readAt, int expected, string arm)
    {
        InstructionOperands.Expect(image, readAt - 2, "shl bx,1", [0xD1, 0xE3]);
        int dgroup = InstructionOperands.Word(image, readAt, "push word [bx+disp16]", [0xFF, 0xB7]);
        int entries = mask + 1;
        if (entries != expected)
        {
            throw new InvalidDataException(
                $"the {arm}'s shard index is masked to {entries} entries; the port draws {expected} " +
                $"(image@0x{readAt:X5})");
        }

        if (ExeAddresses.Image(dgroup) + (entries * 2) > image.Length)
        {
            throw new InvalidDataException(
                $"the {arm}'s angle table at DGROUP 0x{dgroup:X4} (read at image@0x{readAt:X5}) runs past the image");
        }

        return (dgroup, entries);
    }

    private static AngleTableDto Section(
        ReadOnlySpan<byte> image, (int Dgroup, int Entries) table, int maskedAt, int readAt)
    {
        int at = ExeAddresses.Image(table.Dgroup);
        List<int> values = new List<int>(table.Entries);
        for (int i = 0; i < table.Entries; i++)
        {
            values.Add(BinaryPrimitives.ReadUInt16LittleEndian(image[(at + (i * 2))..]));
        }

        return new AngleTableDto
        {
            Source = new DataSourceDto
            {
                Image = PortHex.Format(at, 5),
                Dgroup = PortHex.Format(table.Dgroup),
                Bytes = table.Entries * 2,
                Entries = table.Entries,
                ElementType = "u16",
            },
            MaskedAt = PortHex.Format(maskedAt, 5),
            ReadAt = PortHex.Format(readAt, 5),
            Values = values,
        };
    }

    private static ExeSlice Slice(string name, AngleTableDto section, ReadOnlySpan<ushort> angles)
    {
        DataSourceDto source = section.Source
                               ?? throw new InvalidDataException($"{EffectLookTableDto.DataPath}: {name} has no source");
        int image = PortHex.Parse(source.Image);
        int dgroup = PortHex.Parse(source.Dgroup);
        if (image != ExeAddresses.Image(dgroup) || source.Bytes != angles.Length * 2)
        {
            throw new InvalidDataException(
                $"{EffectLookTableDto.DataPath}: {name} source says image {source.Image}, dgroup " +
                $"{source.Dgroup}, {source.Bytes} B; expected the DGROUP address and {angles.Length * 2} B");
        }

        byte[] bytes = new byte[angles.Length * 2];
        for (int i = 0; i < angles.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), angles[i]);
        }

        return new ExeSlice(name, image, bytes);
    }
}
