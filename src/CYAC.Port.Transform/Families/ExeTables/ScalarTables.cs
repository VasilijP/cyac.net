using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>
/// The quarter-period sine table — 721 <c>i16</c> at <c>image@0x34385</c> (far <c>0x4438:0005</c>).
/// </summary>
/// <remarks>
/// It has <b>no</b> exact generator: no scale <c>S</c> reproduces all 721 entries under floor,
/// nearest or ceiling rounding.  That is precisely why the no-original-data rule requires it to be extracted rather
/// than computed, and why <c>CYAC.Port.Core</c> loads it from here (<c>TrigTables.Load</c>) instead
/// of embedding it.
/// </remarks>
internal sealed class SineQuarterTable : IExeTable
{
    /// <inheritdoc/>
    public string TreePath => TrigTables.SineQuarterDataPath;

    /// <inheritdoc/>
    public string Description =>
        "the 721-entry quarter-period sine table (scale 16383) the original's four fold arms index; " +
        "extracted, not generated — no exact generator exists for it";

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        List<int> values = new List<int>(TrigTables.SineQuarterEntries);
        for (int i = 0; i < TrigTables.SineQuarterEntries; i++)
        {
            values.Add(BinaryPrimitives.ReadInt16LittleEndian(
                image.Slice(TrigTables.SineQuarterImageOffset + (i * 2), 2)));
        }

        ScalarTableDto dto = new ScalarTableDto
        {
            Format = "cyac.table.sineQuarter/1",
            About =
                "The original's quarter-period sine table: entry[i] approximates " +
                "sin(i * pi / 1440) * 16383 over [0 deg, 90 deg] inclusive. 721 entries, not 720 — " +
                "index 720 is reachable through two of the four quadrant-fold arms because the " +
                "original's jge takes equality. NO exact generator exists: the " +
                "per-entry feasible intervals for a scale S intersect to the empty set under floor, " +
                "nearest and ceiling rounding alike, so this table is DATA and must be shipped as " +
                "data. Readers: angle_cos_table_lookup @image@0x18346 and " +
                "angle_sin_table_lookup @image@0x18394.",
            Source = new DataSourceDto
            {
                Image = PortHex.Format(TrigTables.SineQuarterImageOffset, 5),
                FarAddress = "0x4438:0005",
                Bytes = TrigTables.SineQuarterEntries * 2,
                Entries = TrigTables.SineQuarterEntries,
                ElementType = "i16",
            },
            Values = values,
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.ScalarTableDto),
            0,
            $"sine_quarter: {values.Count} i16 entries, scale {TrigTables.Scale}");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json) =>
    [
        new ExeSlice(
            "sine quarter table",
            TrigTables.SineQuarterImageOffset,
            ScalarTableCodec.ToBytes(json, PortDataJsonContext.Readable.ScalarTableDto, TrigTables.SineQuarterEntries)),
    ];
}

/// <summary>
/// The arctangent octant table — 513 <c>u16</c> at <c>image@0x33F83</c> (far <c>0x43F8:0003</c>).
/// </summary>
/// <remarks>
/// Its generator IS exact (<c>trunc(atan(i/512) · 2880/2pi)</c>, B2), so the runtime computes it
/// (<c>Atan2Table</c>, the rule that proven formulas may be computed).  It is still extracted here,
/// because it is data in the executable and the extracted copy is what keeps the generator claim
/// honest: <c>Atan2TableTests</c> diffs the computed table against this document.
/// </remarks>
internal sealed class Atan2OctantTable : IExeTable
{
    /// <inheritdoc/>
    public string TreePath => Atan2Table.OctantDataPath;

    /// <inheritdoc/>
    public string Description =>
        "the 513-entry arctangent octant table atan2_bam indexes; its generator is proven exact, so " +
        "the runtime computes it and uses this copy only to keep that claim honest";

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        List<int> values = new List<int>(Atan2Table.OctantEntries);
        for (int i = 0; i < Atan2Table.OctantEntries; i++)
        {
            values.Add(BinaryPrimitives.ReadUInt16LittleEndian(
                image.Slice(Atan2Table.OctantImageOffset + (i * 2), 2)));
        }

        ScalarTableDto dto = new ScalarTableDto
        {
            Format = "cyac.table.atan2Octant/1",
            About =
                "One octant of arctangent in the original's angle unit (2880 units per circle): " +
                "entry[i] = atan(i/512) for i in 0..512, ramping 0 .. 0x168 (45 deg). " +
                "atan2_bam @image@0x15CF4 reduces any input to this octant by taking absolute " +
                "values, ordering the two magnitudes and using atan(o/a) = 90 deg - atan(a/o). " +
                "Unlike the sine table this one HAS an exact generator, so the port computes it and " +
                "keeps this extraction as the oracle.",
            Source = new DataSourceDto
            {
                Image = PortHex.Format(Atan2Table.OctantImageOffset, 5),
                FarAddress = "0x43F8:0003",
                Bytes = Atan2Table.OctantEntries * 2,
                Entries = Atan2Table.OctantEntries,
                ElementType = "u16",
            },
            Generator = new TableGeneratorDto
            {
                Formula = Atan2Table.GeneratorFormula,
                Exact = true,
                Note =
                    "Truncation toward zero, NOT nearest: nearest-integer rounding is infeasible for " +
                    "every scale (the per-entry intervals intersect to the empty set). Verified for " +
                    "all 513 entries.",
            },
            Values = values,
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.ScalarTableDto),
            0,
            $"atan2_octant: {values.Count} u16 entries, generator proven exact");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json) =>
    [
        new ExeSlice(
            "arctangent octant table",
            Atan2Table.OctantImageOffset,
            ScalarTableCodec.ToBytes(json, PortDataJsonContext.Readable.ScalarTableDto, Atan2Table.OctantEntries)),
    ];
}

/// <summary>Shared encode/decode for the two 16-bit scalar tables.</summary>
internal static class ScalarTableCodec
{
    /// <summary>Re-encodes a scalar-table document into little-endian 16-bit words.</summary>
    /// <param name="json">The document.</param>
    /// <param name="typeInfo">Its source-generated type info.</param>
    /// <param name="expectedEntries">How many entries the layout says it holds.</param>
    /// <exception cref="InvalidDataException">The document is malformed or the wrong length.</exception>
    public static byte[] ToBytes(
        ReadOnlySpan<byte> json,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ScalarTableDto> typeInfo,
        int expectedEntries)
    {
        ScalarTableDto dto = JsonSerializer.Deserialize(json, typeInfo)
                             ?? throw new InvalidDataException("the scalar-table document is empty");
        if (dto.Values is not { } values || values.Count != expectedEntries)
        {
            throw new InvalidDataException(
                $"expected {expectedEntries} values, found {dto.Values?.Count ?? 0}");
        }

        byte[] bytes = new byte[expectedEntries * 2];
        for (int i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), unchecked((ushort)values[i]));
        }

        return bytes;
    }
}

/// <summary>
/// The difficulty-indexed hit-probability table, <c>g_hit_probability_by_difficulty [0x45EA]</c>.
/// </summary>
internal sealed class HitProbabilityExeTable : IExeTable
{
    /// <summary>The table's DGROUP offset.</summary>
    public const int Dgroup = 0x45EA;

    public const int Entries = 4;

    /// <inheritdoc/>
    public string TreePath => "exe/tables/hit_probability.json";

    /// <inheritdoc/>
    public string Description =>
        "the four difficulty-indexed hit-probability thresholds the damage roll and the HUD's " +
        "chance-to-hit both gate on";

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        List<int> values = new List<int>(Entries);
        for (int i = 0; i < Entries; i++)
        {
            values.Add(image[ExeAddresses.Image(Dgroup) + i]);
        }

        HitProbabilityTableDto dto = new HitProbabilityTableDto
        {
            Format = "cyac.table.hitProbability/1",
            About =
                "Indexed by g_briefing_difficulty_idx [0xF10E]: " +
                "mov bl,[0xf10e]; sub bh,bh; mov dl,[bx+0x45ea] @image@0x0F79C, inside " +
                "weapon_fire_combat_loop's damage roll. The same table gates " +
                "engagement_hit_pct_compute @0x031CB, the HUD's \"%d%%\" chance-to-hit. " +
                "Lower index = easier.",
            Source = new DataSourceDto
            {
                Image = PortHex.Format(ExeAddresses.Image(Dgroup), 5),
                Dgroup = PortHex.Format(Dgroup),
                Bytes = Entries,
                Entries = Entries,
                ElementType = "u8",
            },
            ByDifficulty = values,
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.HitProbabilityTableDto),
            0,
            $"hit_probability: {values.Count} difficulty levels");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        HitProbabilityTableDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.HitProbabilityTableDto)
                                     ?? throw new InvalidDataException("the hit-probability document is empty");
        if (dto.ByDifficulty is not { } values || values.Count != Entries)
        {
            throw new InvalidDataException(
                $"expected {Entries} hit-probability entries, found {dto.ByDifficulty?.Count ?? 0}");
        }

        byte[] bytes = new byte[Entries];
        for (int i = 0; i < values.Count; i++)
        {
            bytes[i] = (byte)values[i];
        }

        return [new ExeSlice("hit probability table", ExeAddresses.Image(Dgroup), bytes)];
    }
}

/// <summary>
/// The game's own 256-byte scancode-to-ASCII translation table at <c>image@0x3501A</c>.
/// </summary>
internal sealed class ScancodeExeTable : IExeTable
{
    /// <summary>The table's image offset; the original addresses it as <c>ES:0x4501:[bx+0x0A]</c>.</summary>
    public const int ImageOffset = 0x3501A;

    /// <summary>Its length: 256 bytes — 0x00..0x7F unshifted, 0x80..0xFF shifted.</summary>
    public const int Entries = 256;

    /// <inheritdoc/>
    public string TreePath => "exe/tables/kbd_scancode_to_ascii.json";

    /// <inheritdoc/>
    public string Description =>
        "the game's own scancode-to-ASCII table (it replaces the BIOS translation entirely), " +
        "unshifted half then shifted half";

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        List<ScancodeEntryDto> entries = new List<ScancodeEntryDto>(Entries);
        for (int i = 0; i < Entries; i++)
        {
            byte value = image[ImageOffset + i];
            entries.Add(new ScancodeEntryDto
            {
                Index = PortHex.Format(i, 2),
                Ascii = value,
                Text = value is >= 0x20 and < 0x7F
                    ? Encoding.ASCII.GetString([value])
                    : null,
            });
        }

        ScancodeTableDto dto = new ScancodeTableDto
        {
            Format = "cyac.table.scancodeToAscii/1",
            About =
                "The game replaces the BIOS keyboard translation completely: " +
                "custom_int09_keyboard_isr @image@0x295DA fills its own ring buffer and " +
                "kbd_ring_dequeue_and_translate @image@0x296E4 translates through this table " +
                "(ES:0x4501 is a RELOCATED IN-IMAGE data segment, not a BIOS segment). " +
                "Index 0x00..0x7F is the unshifted half and 0x80..0xFF the shifted half (`or bl,0x80` " +
                "selects it); the synthetic scancodes 0x60..0x6F carry the keypad legends the " +
                "E0-prefix path produces. F1..F10 translate to 0x00 - they are scancode-only keys.",
            Source = new DataSourceDto
            {
                Image = PortHex.Format(ImageOffset, 5),
                FarAddress = "0x4501:000A",
                Bytes = Entries,
                Entries = Entries,
                ElementType = "u8",
            },
            Entries = entries,
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.ScancodeTableDto),
            0,
            $"kbd_scancode_to_ascii: {entries.Count} entries");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        ScancodeTableDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.ScancodeTableDto)
                               ?? throw new InvalidDataException("the scancode document is empty");
        if (dto.Entries is not { } entries || entries.Count != Entries)
        {
            throw new InvalidDataException(
                $"expected {Entries} scancode entries, found {dto.Entries?.Count ?? 0}");
        }

        byte[] bytes = new byte[Entries];
        for (int i = 0; i < entries.Count; i++)
        {
            bytes[i] = (byte)entries[i].Ascii;
        }

        return [new ExeSlice("scancode to ASCII table", ImageOffset, bytes)];
    }
}
