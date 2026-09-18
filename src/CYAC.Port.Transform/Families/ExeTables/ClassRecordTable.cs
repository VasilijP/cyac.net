using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>
/// The 23 world-object class records — the original <c>s_mesh_registry_slot</c>.
/// </summary>
/// <remarks>
/// <para>
/// The DGROUP addresses below are KNOWLEDGE: they are the <c>g_class_record_*</c> globals already
/// names, enumerated by the class census and extended with <c>crater [0x52A2]</c>, which the
/// census predicate (<c>byte[+0] == 0x80</c>) filtered out.  The records' CONTENTS are read from the
/// image at transform time and never appear in this source.
/// </para>
/// <para>
/// Each record's round-trip range runs from the record to the end of its render descriptor: the
/// descriptor's position is read from <c>+0x14</c>, which is <c>+0x50</c> for 21 records and
/// <c>+0x60</c> for <c>mount2</c> and <c>mountain</c> — the B3 defect, carried as a fact rather than
/// hard-coded.
/// </para>
/// </remarks>
internal sealed class ClassRecordTable : IExeTable
{
    /// <summary>Bytes of a render descriptor — the original <c>s_class_render_desc</c>.</summary>
    public const int DescriptorBytes = 0x0E;

    /// <summary>
    /// The class records' DGROUP offsets, census members first in DGROUP order, then the records the
    /// census predicate missed.  Addresses only — see the type remarks.
    /// </summary>
    private static readonly (int Dgroup, bool Census)[] Records =
    [
        (0x4FC0, true), (0x5080, true), (0x51F6, true), (0x5334, true), (0x5618, true),
        (0x59EE, true), (0x5AB2, true), (0x61C0, true), (0x6B34, true), (0x6BBE, true),
        (0x74AE, true), (0x765C, true), (0x773C, true), (0x785A, true), (0x801E, true),
        (0x8770, true), (0x8E22, true), (0x8EF6, true), (0x8FFC, true), (0x9AFA, true),
        (0xA00C, true), (0xA552, true),
        (0x52A2, false),
    ];

    /// <summary>
    /// The DGROUP offsets of the records this table carries — the set the mesh family asks about so
    /// its own documents can say which registry slots <c>exe/classes.json</c> also models (T6).
    /// </summary>
    public static IReadOnlySet<int> DgroupOffsets { get; } = Records.Select(r => r.Dgroup).ToHashSet();

    /// <inheritdoc/>
    public string TreePath => ClassRegistry.DataPath;

    /// <inheritdoc/>
    public string Description =>
        "the 23 world-object class records (s_mesh_registry_slot) every pooled object points at: " +
        "render priority, LOD thresholds and bounds, plus each class's render descriptor";

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        List<ClassRecordDto> classes = new List<ClassRecordDto>(Records.Length);
        int unknown = 0;

        foreach ((int dgroup, bool census) in Records)
        {
            int at = ExeAddresses.Image(dgroup);
            ushort descriptorPointer = BinaryPrimitives.ReadUInt16LittleEndian(image[(at + 0x14)..]);
            int descriptorInRecord = descriptorPointer - dgroup;
            if (descriptorInRecord is not (0x50 or 0x60))
            {
                throw new InvalidDataException(
                    $"class record [0x{dgroup:X4}] puts its render descriptor at +0x{descriptorInRecord:X}; " +
                    "the shipping records use +0x50 or +0x60");
            }

            ushort secondary = BinaryPrimitives.ReadUInt16LittleEndian(image[(at + 0x48)..]);
            ReadOnlySpan<byte> record = image.Slice(at, descriptorInRecord + DescriptorBytes);
            string? displaced = descriptorInRecord == 0x60 ? PortHex.Bytes(record.Slice(0x50, 0x10)) : null;
            unknown += 1 + 6 + (displaced is null ? 0 : 0x10);

            classes.Add(new ClassRecordDto
            {
                Name = ReadName(image, BinaryPrimitives.ReadUInt16LittleEndian(record[0x22..])),
                Dgroup = PortHex.Format(dgroup),
                Image = PortHex.Format(at, 5),
                Bytes = record.Length,
                CensusMember = census,
                RenderLayerPriority = PortHex.Format(record[0x00], 2),
                Flags = PortHex.Format(record[0x01], 2),
                MeshExtent = BinaryPrimitives.ReadUInt16LittleEndian(record[0x02..]),
                RawExtent = BinaryPrimitives.ReadInt32LittleEndian(record[0x08..]),
                ScaleShiftExponent = (sbyte)record[0x0C],
                LodThresholds =
                [
                    BinaryPrimitives.ReadUInt16LittleEndian(record[0x0E..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(record[0x10..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(record[0x12..]),
                ],
                LodFaceDescriptor1 = PortHex.Format(BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..])),
                LodFaceDescriptor2 = PortHex.Format(BinaryPrimitives.ReadUInt16LittleEndian(record[0x18..])),
                VertexCount = BinaryPrimitives.ReadUInt16LittleEndian(record[0x1A..]),
                NamePointer = PortHex.Format(BinaryPrimitives.ReadUInt16LittleEndian(record[0x22..])),
                GroundClearance = BinaryPrimitives.ReadUInt16LittleEndian(record[0x2C..]),
                PoolFilterWord = PortHex.Format(BinaryPrimitives.ReadUInt16LittleEndian(record[0x2E..])),
                Bounds = new ClassBoundsDto
                {
                    MinX = BinaryPrimitives.ReadInt32LittleEndian(record[0x30..]),
                    MaxX = BinaryPrimitives.ReadInt32LittleEndian(record[0x34..]),
                    MinY = BinaryPrimitives.ReadInt32LittleEndian(record[0x38..]),
                    MaxY = BinaryPrimitives.ReadInt32LittleEndian(record[0x3C..]),
                    MinZ = BinaryPrimitives.ReadInt32LittleEndian(record[0x40..]),
                    MaxZ = BinaryPrimitives.ReadInt32LittleEndian(record[0x44..]),
                },
                SecondaryFaceDescriptor = PortHex.Format(secondary),
                RenderDescriptor = ReadDescriptor(record, descriptorPointer, descriptorInRecord),
                Unknown0x0D = PortHex.Bytes(record.Slice(0x0D, 1)),
                Unknown0x4A = PortHex.Bytes(record.Slice(0x4A, 6)),
                Unknown0x50 = displaced,
            });
        }

        ClassRegistryDocumentDto dto = new ClassRegistryDocumentDto
        {
            Format = "cyac.classRegistry/1",
            About =
                "Every world-object class the shipping game defines - the static, DGROUP-resident " +
                "record a pooled object points at through its +0x00 near pointer " +
                "(KNOWN_FIELDS[\"s_mesh_registry_slot\"]). Two facts the published census " +
                "gets wrong are carried here as data rather than smoothed over: (1) mount2 and " +
                "mountain put their render descriptor at record+0x60, not +0x50, and their +0x48 " +
                "points at the 16-byte block that displaced it; (2) the census predicate " +
                "byte[+0] == 0x80 is a FILTER, not a structural marker - +0x00 is the render-layer " +
                "priority byte and crater [0x52A2], priority 0x1E, has the identical layout " +
                "(censusMember: false). Invariants measured over all 23: " +
                "rawExtent == meshExtent << (8 + scaleShiftExponent); groundClearance == -bounds.minY; " +
                "flags bit2 set iff lodFaceDescriptor1 is non-zero; every draw callback NULL on disk " +
                "(it is runtime-patched with mesh-JIT code).",
            DgroupImageBase = PortHex.Format(ExeAddresses.DgroupImageBase, 5),
            Classes = classes,
        };

        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.ClassRegistryDocumentDto),
            unknown,
            $"classes: {classes.Count} records ({classes.Count(c => c.CensusMember)} in the class census)");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        ClassRegistryDocumentDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.ClassRegistryDocumentDto)
                                       ?? throw new InvalidDataException("the class-registry document is empty");
        if (dto.Classes is not { } classes || classes.Count != Records.Length)
        {
            throw new InvalidDataException(
                $"expected {Records.Length} class records, found {dto.Classes?.Count ?? 0}");
        }

        List<ExeSlice> slices = new List<ExeSlice>(classes.Count);
        foreach (ClassRecordDto item in classes)
        {
            ClassRenderDescriptorDto descriptor = item.RenderDescriptor
                                                  ?? throw new InvalidDataException($"class '{item.Name}' has no render descriptor");
            ClassBoundsDto bounds = item.Bounds
                                    ?? throw new InvalidDataException($"class '{item.Name}' has no bounds");
            if (item.LodThresholds is not { Count: 3 } thresholds)
            {
                throw new InvalidDataException($"class '{item.Name}' must carry three LOD thresholds");
            }

            int descriptorInRecord = PortHex.Parse(descriptor.OffsetInRecord);
            byte[] record = new byte[descriptorInRecord + DescriptorBytes];

            record[0x00] = (byte)PortHex.Parse(item.RenderLayerPriority);
            record[0x01] = (byte)PortHex.Parse(item.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x02), (ushort)item.MeshExtent);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x08), item.RawExtent);
            record[0x0C] = unchecked((byte)(sbyte)item.ScaleShiftExponent);
            record[0x0D] = PortHex.BytesOrZero(item.Unknown0x0D, 1)[0];
            for (int i = 0; i < 3; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    record.AsSpan(0x0E + (i * 2)), (ushort)thresholds[i]);
            }

            BinaryPrimitives.WriteUInt16LittleEndian(
                record.AsSpan(0x14), (ushort)PortHex.Parse(descriptor.Dgroup));
            BinaryPrimitives.WriteUInt16LittleEndian(
                record.AsSpan(0x16), (ushort)PortHex.ParseOrDefault(item.LodFaceDescriptor1));
            BinaryPrimitives.WriteUInt16LittleEndian(
                record.AsSpan(0x18), (ushort)PortHex.ParseOrDefault(item.LodFaceDescriptor2));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x1A), (ushort)item.VertexCount);
            BinaryPrimitives.WriteUInt16LittleEndian(
                record.AsSpan(0x22), (ushort)PortHex.Parse(item.NamePointer));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x2C), (ushort)item.GroundClearance);
            BinaryPrimitives.WriteUInt16LittleEndian(
                record.AsSpan(0x2E), (ushort)PortHex.ParseOrDefault(item.PoolFilterWord));
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x30), bounds.MinX);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x34), bounds.MaxX);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x38), bounds.MinY);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x3C), bounds.MaxY);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x40), bounds.MinZ);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x44), bounds.MaxZ);
            BinaryPrimitives.WriteUInt16LittleEndian(
                record.AsSpan(0x48), (ushort)PortHex.ParseOrDefault(item.SecondaryFaceDescriptor));
            PortHex.BytesOrZero(item.Unknown0x4A, 6).CopyTo(record.AsSpan(0x4A));
            if (descriptorInRecord == 0x60)
            {
                PortHex.BytesOrZero(item.Unknown0x50, 0x10).CopyTo(record.AsSpan(0x50));
            }

            WriteDescriptor(record.AsSpan(descriptorInRecord, DescriptorBytes), descriptor);
            slices.Add(new ExeSlice(
                $"class record '{item.Name}'", ExeAddresses.Image(PortHex.Parse(item.Dgroup)), record));
        }

        return slices;
    }

    private static ClassRenderDescriptorDto ReadDescriptor(
        ReadOnlySpan<byte> record, ushort descriptorPointer, int descriptorInRecord)
    {
        ReadOnlySpan<byte> descriptor = record.Slice(descriptorInRecord, DescriptorBytes);
        return new ClassRenderDescriptorDto
        {
            Dgroup = PortHex.Format(descriptorPointer),
            OffsetInRecord = PortHex.Format(descriptorInRecord, 2),
            Kind = PortHex.Format(descriptor[0x00], 2),
            ElementCount = descriptor[0x01],
            PrepareCallback = FilmReviewWidgetExeTable.FarPointer(
                BinaryPrimitives.ReadUInt16LittleEndian(descriptor[0x02..]),
                BinaryPrimitives.ReadUInt16LittleEndian(descriptor[0x04..])),
            DrawCallback = FilmReviewWidgetExeTable.FarPointer(
                BinaryPrimitives.ReadUInt16LittleEndian(descriptor[0x06..]),
                BinaryPrimitives.ReadUInt16LittleEndian(descriptor[0x08..])),
            ElementList = PortHex.Format(BinaryPrimitives.ReadUInt16LittleEndian(descriptor[0x0A..])),
            Flags = PortHex.Format(BinaryPrimitives.ReadUInt16LittleEndian(descriptor[0x0C..])),
        };
    }

    private static void WriteDescriptor(Span<byte> target, ClassRenderDescriptorDto descriptor)
    {
        target[0x00] = (byte)PortHex.Parse(descriptor.Kind);
        target[0x01] = (byte)descriptor.ElementCount;
        BinaryPrimitives.WriteUInt16LittleEndian(
            target[0x02..], (ushort)PortHex.ParseOrDefault(descriptor.PrepareCallback?.Offset));
        BinaryPrimitives.WriteUInt16LittleEndian(
            target[0x04..], (ushort)PortHex.ParseOrDefault(descriptor.PrepareCallback?.Segment));
        BinaryPrimitives.WriteUInt16LittleEndian(
            target[0x06..], (ushort)PortHex.ParseOrDefault(descriptor.DrawCallback?.Offset));
        BinaryPrimitives.WriteUInt16LittleEndian(
            target[0x08..], (ushort)PortHex.ParseOrDefault(descriptor.DrawCallback?.Segment));
        BinaryPrimitives.WriteUInt16LittleEndian(
            target[0x0A..], (ushort)PortHex.ParseOrDefault(descriptor.ElementList));
        BinaryPrimitives.WriteUInt16LittleEndian(
            target[0x0C..], (ushort)PortHex.ParseOrDefault(descriptor.Flags));
    }

    private static string ReadName(ReadOnlySpan<byte> image, ushort namePointer)
    {
        int start = ExeAddresses.Image(namePointer);
        int end = start;
        while (end < image.Length && image[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(image[start..end]);
    }
}
