using System.Buffers.Binary;
using System.Text;
using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Data;

/// <summary>
/// The game's CONSTANT DGROUP surface, rebuilt from the open documents — the port's replacement for
/// reading <c>data/exe/image.l1.bin</c> at run time.
/// </summary>
/// <remarks>
/// <para>
/// The 1991 engine addresses all of its static tuning data by DGROUP near offset, and it passes
/// those offsets around as handles: a pool object holds its class record's offset, an engagement
/// block holds its prototype's, a prototype holds its weapon classes'.  A port that wants to be
/// byte-exact against the recordings has to keep those handles, so this type keeps the ADDRESS SPACE
/// and rebuilds its CONTENTS from the transformed tree: every region is written from a typed
/// document (<c>exe/classes.json</c>, <c>exe/meshes/*.json</c>, <c>exe/weapons.json</c>,
/// <c>exe/tables/*.json</c>), never copied out of the image.
/// </para>
/// <para>
/// A DGROUP offset is <b>knowledge</b>, not data (the no-original-data rule — "what ships is names,
/// offsets, layouts"), and the runtime never dereferences an image offset: the documents publish
/// their pointers by NAME beside the offset, and <see cref="Load"/> writes each structure at the
/// offset its own document declares.
/// </para>
/// <para>
/// <b>Strict by construction.</b>  Only the bytes a document actually explains are marked defined;
/// reading anything else throws <see cref="InvalidOperationException"/> naming the offset.  So a
/// table the port needs and the transform does not publish is a loud failure at the first frame that
/// wants it, never a silent zero — which is what makes "the running port reads nothing from the
/// image" a checkable claim rather than a hope.
/// </para>
/// </remarks>
public sealed class DgroupConstants
{
    /// <summary>DGROUP is one real-mode segment: 64 KB of near offsets.</summary>
    public const int Size = 0x10000;

    /// <summary>
    /// DGROUP's own origin inside the L1 image (SEGMENT_ORIGINS</c>) — used only by the dev-side
    /// oracle that diffs this surface against the shipped image.
    /// </summary>
    public const int DgroupImageBase = 0x3BD60;

    private readonly byte[] _bytes = new byte[Size];
    private readonly bool[] _defined = new bool[Size];

    private DgroupConstants()
    {
    }

    /// <summary>How many of the 64 KB this surface explains.</summary>
    public int DefinedBytes { get; private set; }

    /// <summary>Builds the surface from an opened data tree.</summary>
    /// <param name="tree">The transformed data tree.</param>
    /// <exception cref="InvalidDataException">A document is missing a section the surface needs.</exception>
    public static DgroupConstants Load(DataTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        DgroupConstants surface = new DgroupConstants();

        foreach ((int dgroup, byte[] bytes) in DgroupDocumentCodec.ClassRecords(tree.Classes))
        {
            surface.Define(dgroup, bytes);
        }

        foreach ((int dgroup, byte[] bytes) in DgroupDocumentCodec.RegistrySlots(tree.ExeMeshes.Values))
        {
            // A class record and a registry slot can be the SAME record (21 of the 23 classes are in
            // the registry).  The two documents agree by construction — the transform reads both from
            // the same bytes — so the first writer wins and the second is skipped.
            surface.DefineIfFree(dgroup, bytes);
        }

        foreach ((int dgroup, byte[] bytes) in DgroupDocumentCodec.Weapons(tree.Weapons))
        {
            surface.Define(dgroup, bytes);
        }

        foreach ((int dgroup, byte[] bytes) in DgroupDocumentCodec.PlayerDamage(tree.PlayerDamage))
        {
            surface.Define(dgroup, bytes);
        }

        surface.Define(
            HitProbabilityTable.DgroupOffset,
            DgroupDocumentCodec.HitProbability(tree.HitProbability));

        foreach ((int dgroup, byte[] bytes) in DgroupDocumentCodec.CombatConstants(tree.CombatConstants))
        {
            surface.Define(dgroup, bytes);
        }

        foreach ((int dgroup, byte[] bytes) in DgroupDocumentCodec.Engagement(tree.Engagement))
        {
            surface.Define(dgroup, bytes);
        }

        foreach ((int dgroup, byte[] bytes) in DgroupDocumentCodec.FlightTuning(tree.FlightTuning))
        {
            surface.Define(dgroup, bytes);
        }

        foreach ((int dgroup, byte[] bytes) in DgroupDocumentCodec.CockpitLayout(tree.CockpitLayout))
        {
            surface.Define(dgroup, bytes);
        }

        return surface;
    }

    /// <summary>Reads one constant byte.</summary>
    /// <param name="dgroupOffset">Its DGROUP offset.</param>
    /// <exception cref="InvalidOperationException">No document explains that byte.</exception>
    public byte Byte(int dgroupOffset)
    {
        Require(dgroupOffset, 1);
        return _bytes[dgroupOffset];
    }

    /// <summary>Reads one constant little-endian word.</summary>
    /// <param name="dgroupOffset">Its DGROUP offset.</param>
    /// <exception cref="InvalidOperationException">No document explains those bytes.</exception>
    public ushort Word(int dgroupOffset)
    {
        Require(dgroupOffset, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(dgroupOffset));
    }

    /// <summary>Reads a constant span.</summary>
    /// <param name="dgroupOffset">Its DGROUP offset.</param>
    /// <param name="length">How many bytes.</param>
    /// <exception cref="InvalidOperationException">No document explains those bytes.</exception>
    public ReadOnlySpan<byte> Span(int dgroupOffset, int length)
    {
        Require(dgroupOffset, length);
        return _bytes.AsSpan(dgroupOffset, length);
    }

    /// <summary>Whether every byte of a range is explained by some document.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    /// <param name="length">How many bytes.</param>
    public bool Defines(int dgroupOffset, int length)
    {
        if (dgroupOffset < 0 || length < 0 || dgroupOffset + length > Size)
        {
            return false;
        }

        for (int i = 0; i < length; i++)
        {
            if (!_defined[dgroupOffset + i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Every defined offset, ascending — the dev-side oracle walks it against the image.</summary>
    public IEnumerable<int> DefinedOffsets()
    {
        for (int i = 0; i < Size; i++)
        {
            if (_defined[i])
            {
                yield return i;
            }
        }
    }

    private void Define(int dgroupOffset, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            int at = dgroupOffset + i;
            if (_defined[at] && _bytes[at] != bytes[i])
            {
                throw new InvalidDataException(
                    $"two documents disagree about DGROUP 0x{at:X4}: 0x{_bytes[at]:X2} and 0x{bytes[i]:X2}");
            }

            if (!_defined[at])
            {
                _defined[at] = true;
                DefinedBytes++;
            }

            _bytes[at] = bytes[i];
        }
    }

    private void DefineIfFree(int dgroupOffset, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            int at = dgroupOffset + i;
            if (_defined[at])
            {
                continue;
            }

            _defined[at] = true;
            _bytes[at] = bytes[i];
            DefinedBytes++;
        }
    }

    private void Require(int dgroupOffset, int length)
    {
        if (!Defines(dgroupOffset, length))
        {
            throw new InvalidOperationException(
                $"DGROUP 0x{dgroupOffset:X4}..0x{dgroupOffset + length - 1:X4} is not published by any "
                    + "document in the data tree, so the running port cannot read it. Either the "
                    + "region belongs in a cyac-transform exe-table (see "
                    + ") or the caller is reading the wrong offset.");
        }
    }
}

/// <summary>
/// Turns the tree's typed documents back into the DGROUP bytes the 1991 engine addressed.
/// </summary>
/// <remarks>
/// These encoders are the mirror of <c>cyac-transform</c>'s extractors and are proved against the
/// shipped image by <c>DgroupConstantsTests</c>, which diffs every byte the surface defines against
/// <c>DataTree.ProgramImage</c>.  Keeping them in <c>CYAC.Port.Core</c> rather than in the tool is
/// deliberate: the RUNTIME needs them (the data-tree rule), and the runtime may not depend on
/// <c>cyac-transform</c>.
/// </remarks>
public static class DgroupDocumentCodec
{
    /// <summary>Bytes of a class record's render descriptor (<c>s_class_render_desc</c>).</summary>
    public const int RenderDescriptorBytes = 0x0E;

    /// <summary>Bytes of a mesh-registry slot before any embedded LOD-0 descriptor.</summary>
    public const int RegistrySlotBytes = 0x50;

    /// <summary>The 23 world-object class records, each at the offset its document declares.</summary>
    /// <param name="document">The <c>exe/classes.json</c> document.</param>
    public static IEnumerable<(int Dgroup, byte[] Bytes)> ClassRecords(ClassRegistryDocumentDto document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (ClassRecordDto item in document.Classes ?? [])
        {
            ClassRenderDescriptorDto descriptor = item.RenderDescriptor
                                                  ?? throw new InvalidDataException($"class '{item.Name}' has no render descriptor");
            ClassBoundsDto bounds = item.Bounds
                                    ?? throw new InvalidDataException($"class '{item.Name}' has no bounds");
            int descriptorInRecord = PortHex.Parse(descriptor.OffsetInRecord);
            byte[] record = new byte[descriptorInRecord + RenderDescriptorBytes];

            record[0x00] = (byte)PortHex.Parse(item.RenderLayerPriority);
            record[0x01] = (byte)PortHex.Parse(item.Flags);
            Word(record, 0x02, item.MeshExtent);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x08), item.RawExtent);
            record[0x0C] = unchecked((byte)(sbyte)item.ScaleShiftExponent);
            record[0x0D] = PortHex.BytesOrZero(item.Unknown0x0D, 1)[0];
            List<int> thresholds = item.LodThresholds ?? [0, 0, 0];
            for (int i = 0; i < 3 && i < thresholds.Count; i++)
            {
                Word(record, 0x0E + (i * 2), thresholds[i]);
            }

            Word(record, 0x14, PortHex.Parse(descriptor.Dgroup));
            Word(record, 0x16, PortHex.ParseOrDefault(item.LodFaceDescriptor1));
            Word(record, 0x18, PortHex.ParseOrDefault(item.LodFaceDescriptor2));
            Word(record, 0x1A, item.VertexCount);
            Word(record, 0x22, PortHex.Parse(item.NamePointer));
            Word(record, 0x2C, item.GroundClearance);
            Word(record, 0x2E, PortHex.ParseOrDefault(item.PoolFilterWord));
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x30), bounds.MinX);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x34), bounds.MaxX);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x38), bounds.MinY);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x3C), bounds.MaxY);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x40), bounds.MinZ);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x44), bounds.MaxZ);
            Word(record, 0x48, PortHex.ParseOrDefault(item.SecondaryFaceDescriptor));
            PortHex.BytesOrZero(item.Unknown0x4A, 6).CopyTo(record.AsSpan(0x4A));
            if (descriptorInRecord == 0x60)
            {
                PortHex.BytesOrZero(item.Unknown0x50, 0x10).CopyTo(record.AsSpan(0x50));
            }

            Span<byte> target = record.AsSpan(descriptorInRecord, RenderDescriptorBytes);
            target[0x00] = (byte)PortHex.Parse(descriptor.Kind);
            target[0x01] = (byte)descriptor.ElementCount;
            Word(target, 0x02, PortHex.ParseOrDefault(descriptor.PrepareCallback?.Offset));
            Word(target, 0x04, PortHex.ParseOrDefault(descriptor.PrepareCallback?.Segment));
            Word(target, 0x06, PortHex.ParseOrDefault(descriptor.DrawCallback?.Offset));
            Word(target, 0x08, PortHex.ParseOrDefault(descriptor.DrawCallback?.Segment));
            Word(target, 0x0A, PortHex.ParseOrDefault(descriptor.ElementList));
            Word(target, 0x0C, PortHex.ParseOrDefault(descriptor.Flags));

            yield return (PortHex.Parse(item.Dgroup), record);
        }
    }

    /// <summary>
    /// The DGROUP-resident mesh-registry slots — the class records of every object the class registry
    /// does not carry, which a pooled object still reaches through its own <c>+0x00</c> pointer.
    /// </summary>
    /// <param name="meshes">The <c>exe/meshes/*.json</c> documents.</param>
    public static IEnumerable<(int Dgroup, byte[] Bytes)> RegistrySlots(
        IEnumerable<ExeMeshDocumentDto> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        foreach (ExeMeshDocumentDto mesh in meshes)
        {
            if (mesh.Slot is not { } slot || slot.Dgroup is null)
            {
                continue;
            }

            // Every registry slot is DGROUP-resident — the 64-entry registry holds NEAR pointers.
            // (`geometrySegment` says where the slot's GEOMETRY lives, not the slot itself.)
            byte[] record = new byte[Math.Min(slot.Bytes, RegistrySlotBytes)];
            record[0x00] = (byte)PortHex.ParseOrDefault(slot.RenderLayerPriority);
            record[0x01] = (byte)PortHex.ParseOrDefault(slot.Flags);
            Word(record, 0x02, slot.MeshExtent);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x08), slot.RawExtent);
            record[0x0C] = unchecked((byte)(sbyte)slot.ScaleShiftExponent);
            record[0x0D] = (byte)slot.TargetPanelCameraDistanceSteps;   // named (ex-residue)
            List<int> thresholds = slot.LodThresholds ?? [];
            for (int i = 0; i < 3 && i < thresholds.Count; i++)
            {
                Word(record, 0x0E + (i * 2), thresholds[i]);
            }

            List<string> pointers = slot.LodPointers ?? [];
            for (int i = 0; i < 3 && i < pointers.Count; i++)
            {
                Word(record, 0x14 + (i * 2), PortHex.ParseOrDefault(pointers[i]));
            }

            Word(record, 0x1A, slot.VertexCount);
            Word(record, 0x22, PortHex.ParseOrDefault(slot.BasenamePointer));
            Word(record, 0x2C, slot.GroundClearance);
            Word(record, 0x2E, PortHex.ParseOrDefault(slot.PoolFilterWord));
            List<int> bounds = slot.Bounds ?? [];
            for (int i = 0; i < 6 && i < bounds.Count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x30 + (i * 4)), bounds[i]);
            }

            Word(record, 0x24, PortHex.ParseOrDefault(slot.GeometryPointer?.Offset));
            Word(record, 0x26, PortHex.ParseOrDefault(slot.GeometryPointer?.Segment));
            Word(record, 0x48, PortHex.ParseOrDefault(slot.SecondaryFaceDescriptor));

            // Law L4's residue: the bytes inside the record the model does not name, keyed by image
            // offset.  Applying them is what makes this reconstruction byte-exact.
            int slotImage = PortHex.Parse(slot.Image);
            foreach ((string key, string hex) in mesh.Unknown ?? [])
            {
                int at = PortHex.Parse(key["unknown_".Length..]) - slotImage;
                byte[] span = Convert.FromHexString(hex);
                if (at >= 0 && at + span.Length <= record.Length)
                {
                    span.CopyTo(record.AsSpan(at));
                }
            }

            yield return (PortHex.Parse(slot.Dgroup), record);
        }
    }

    /// <summary>The weapon-class table, the name pool and the per-aircraft loadouts.</summary>
    /// <param name="document">The <c>exe/weapons.json</c> document.</param>
    public static IEnumerable<(int Dgroup, byte[] Bytes)> Weapons(WeaponTablesDocumentDto document)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<WeaponClassRecordDto> classes = document.WeaponClasses ?? [];
        byte[] classBytes = new byte[classes.Count * WeaponClass.RecordBytes];
        for (int i = 0; i < classes.Count; i++)
        {
            WeaponClassRecordDto item = classes[i];
            Span<byte> record = classBytes.AsSpan(i * WeaponClass.RecordBytes, WeaponClass.RecordBytes);
            PortHex.BytesOrZero(item.Unknown0x00, 4).CopyTo(record);
            record[0x04] = (byte)item.MinimumRange;
            record[0x05] = (byte)item.MaximumScore;
            PortHex.BytesOrZero(item.Unknown0x06, 10).CopyTo(record[0x06..]);
            Word(record, 0x10, PortHex.ParseOrDefault(item.ProjectileClassPointer));
            PortHex.BytesOrZero(item.Unknown0x12, 2).CopyTo(record[0x12..]);
            Word(record, 0x14, item.InitialSpeed);
            Word(record, 0x16, item.BoostSpeedMax);
            Word(record, 0x18, item.CoastSpeedMin);
            Word(record, 0x1A, item.BoostAcceleration);
            Word(record, 0x1C, item.CoastDeceleration);
            record[0x1E] = (byte)item.BoostFrames;
            record[0x1F] = (byte)item.LifetimeFrames;
            Word(record, 0x20, item.TargetScoreKey);
            record[0x22] = (byte)item.DamageScale;
            record[0x23] = (byte)item.DamageMultiplier;
            record[0x24] = (byte)PortHex.Parse(item.ClassFlags);
            PortHex.BytesOrZero(item.Unknown0x25, 6).CopyTo(record[0x25..]);
            record[0x2B] = (byte)PortHex.Parse(item.FireTone);
            record[0x2C] = (byte)item.AmmoPerShot;
            record[0x2D] = PortHex.BytesOrZero(item.Unknown0x2D, 1)[0];
        }

        yield return (WeaponClass.StaticTableDgroupOffset, classBytes);

        foreach (WeaponNameDto name in document.WeaponNames ?? [])
        {
            yield return (
                PortHex.Parse(name.Dgroup), Encoding.ASCII.GetBytes((name.Text ?? string.Empty) + "\0"));
        }

        List<AircraftWeaponRecordDto> loadouts = document.PerAircraftWeapons ?? [];
        foreach (AircraftWeaponRecordDto item in loadouts)
        {
            byte[] record = new byte[WeaponLoadoutRecordBytes];
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x00), (ushort)item.ChaffStock);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x02), (ushort)item.FlareStock);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x04), (ushort)item.DefaultSlot);
            List<AircraftWeaponSlotDto> slots = item.Slots ?? [];
            for (int slot = 0; slot < slots.Count && slot < 3; slot++)
            {
                Word(record, 0x06 + (slot * 4), slots[slot].Ammunition);
                Word(record, 0x08 + (slot * 4), PortHex.ParseOrDefault(slots[slot].NamePointer));
            }

            yield return (PortHex.Parse(item.Dgroup), record);
        }
    }

    /// <summary>Bytes of one per-aircraft weapon-loadout record.</summary>
    public const int WeaponLoadoutRecordBytes = 0x12;

    /// <summary>The six player-damage weight tables and the pointer array that selects one.</summary>
    /// <param name="document">The <c>exe/tables/player_damage.json</c> document.</param>
    public static IEnumerable<(int Dgroup, byte[] Bytes)> PlayerDamage(PlayerDamageTablesDto document)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<PlayerDamageTableDto> tables = document.Tables ?? [];
        foreach (PlayerDamageTableDto table in tables)
        {
            List<PlayerDamageWeightDto> weights = table.Weights ?? [];
            byte[] bytes = new byte[weights.Count * PlayerDamageTable.WeightStrideBytes];
            for (int i = 0; i < weights.Count; i++)
            {
                bytes[i * PlayerDamageTable.WeightStrideBytes] = (byte)weights[i].Weight;
                bytes[(i * PlayerDamageTable.WeightStrideBytes) + 1] =
                    PortHex.BytesOrZero(weights[i].Unknown0x01, 1)[0];
            }

            yield return (PortHex.Parse(table.Dgroup), bytes);
        }

        List<string> pointers = document.PointerArray ?? [];
        byte[] pointerBytes = new byte[pointers.Count * 2];
        for (int i = 0; i < pointers.Count; i++)
        {
            Word(pointerBytes, i * 2, PortHex.Parse(pointers[i]));
        }

        yield return (PlayerDamageTable.TablePointerArrayDgroupOffset, pointerBytes);
    }

    /// <summary>The four-entry hit-probability table.</summary>
    /// <param name="document">The <c>exe/tables/hit_probability.json</c> document.</param>
    public static byte[] HitProbability(HitProbabilityTableDto document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return [.. (document.ByDifficulty ?? []).Select(v => (byte)v)];
    }

    /// <summary>The small constant combat tables.</summary>
    /// <param name="document">The <c>exe/tables/combat_constants.json</c> document.</param>
    public static IEnumerable<(int Dgroup, byte[] Bytes)> CombatConstants(CombatConstantsDto document)
    {
        ArgumentNullException.ThrowIfNull(document);
        yield return (CombatConstantOffsets.OriginPivot, WordBytes(document.OriginPivot));
        yield return (CombatConstantOffsets.FireBonus, ByteBytes(document.AircraftTypeFireBonus));
        yield return (CombatConstantOffsets.PhaseAttributes, ByteBytes(document.PhaseAttributes));

        foreach (SkillTableRowDto row in document.SkillTables ?? [])
        {
            yield return (PortHex.Parse(row.Dgroup), ByteBytes(row.BySkill));
        }

        yield return (
            CombatConstantOffsets.AdmissionProbability,
            ByteBytes(document.AdmissionProbabilityByDifficulty));
        yield return (
            CombatConstantOffsets.AdmissionInterval,
            ByteBytes(document.AdmissionIntervalByDifficulty));
        yield return (CombatConstantOffsets.AdmissionCap, ByteBytes(document.AdmissionCapByDifficulty));
        yield return (
            CombatConstantOffsets.EngagementDuration,
            WordBytes(document.EngagementDurationByAircraft));

        foreach (NullClassProbeDto probe in document.NullClassProbe ?? [])
        {
            yield return (PortHex.Parse(probe.Dgroup), [(byte)probe.Value]);
        }

        List<FlyablePrototypeDto> flyable = document.FlyablePrototypes ?? [];
        byte[] pointers = new byte[flyable.Count * 2];
        for (int i = 0; i < flyable.Count; i++)
        {
            Word(pointers, i * 2, PortHex.Parse(flyable[i].Dgroup));
        }

        yield return (CombatConstantOffsets.FlyablePrototypeTable, pointers);

        foreach (TypeResourceEntryDto entry in document.TypeResources ?? [])
        {
            byte[] pair = new byte[4];
            Word(pair, 0, PortHex.Parse(entry.TypeDgroup));
            Word(pair, 2, PortHex.Parse(entry.ResourceDgroup));
            yield return (PortHex.Parse(entry.Dgroup), pair);
        }
    }

    /// <summary>The engagement zone: prototypes, arc descriptors, range tables and band strings.</summary>
    /// <param name="document">The <c>exe/tables/engagement.json</c> document.</param>
    public static IEnumerable<(int Dgroup, byte[] Bytes)> Engagement(EngagementDocumentDto document)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<byte> names = new List<byte>();
        foreach (string name in document.Names ?? [])
        {
            names.AddRange(Encoding.ASCII.GetBytes(name));
            names.Add(0);
        }

        names.AddRange(new byte[document.NamePadBytes]);
        yield return (PortHex.Parse(document.NameTableDgroup), [.. names]);

        foreach (EngagementPrototypeDto prototype in document.Prototypes ?? [])
        {
            yield return (PortHex.Parse(prototype.Dgroup), PrototypeBytes(prototype, EngagementRecordBytes));
        }

        if (document.TruncatedPrototype is { } dropped)
        {
            yield return (PortHex.Parse(dropped.Dgroup), PrototypeBytes(dropped, TruncatedPrototypeBytes));
        }

        if (document.KilledPrototype is { } killed)
        {
            yield return (PortHex.Parse(killed.Dgroup), PrototypeBytes(killed, KilledPrototypeBytes));
        }

        foreach (ArcDescriptorDto descriptor in document.ArcDescriptors ?? [])
        {
            byte[] bytes = new byte[descriptor.Bytes];
            Word(bytes, 0x00, descriptor.BankScaleBase);
            Word(bytes, 0x02, descriptor.BankStepRate);
            Word(bytes, 0x04, descriptor.ShotAngleMax);
            Word(bytes, 0x06, descriptor.DescentHeadingMax);
            Word(bytes, 0x08, descriptor.AngleClamp);
            Word(bytes, 0x0A, descriptor.ArcFloorMinimum);
            Word(bytes, 0x0C, descriptor.ArcAccumulatorHeading);
            Word(bytes, 0x0E, descriptor.ArcCeilingCap);
            Word(bytes, 0x10, descriptor.ArcIncreaseRate);
            Word(bytes, 0x12, descriptor.AngleOverride);
            Word(bytes, 0x14, descriptor.RangeReference);
            Word(bytes, 0x16, descriptor.EngagementWindow);
            List<int> reference = descriptor.WeaponTypeReference ?? [0, 0];
            bytes[0x18] = (byte)reference[0];
            bytes[0x19] = (byte)(reference.Count > 1 ? reference[1] : 0);
            PortHex.BytesOrZero(descriptor.Unknown0x1A, 2).CopyTo(bytes.AsSpan(0x1A));
            if (descriptor.Bytes >= 0x1E)
            {
                Word(bytes, 0x1C, PortHex.ParseOrDefault(descriptor.RangeTableDgroup));
            }

            yield return (PortHex.Parse(descriptor.Dgroup), bytes);
        }

        foreach (ArcRangeTableDto table in document.RangeTables ?? [])
        {
            List<ArcRangeRecordDto> records = table.Records ?? [];
            byte[] bytes = new byte[records.Count * ArcRangeRecordBytes];
            for (int i = 0; i < records.Count; i++)
            {
                Word(bytes, i * ArcRangeRecordBytes, PortHex.ParseOrDefault(records[i].BandDgroup));
                Word(bytes, (i * ArcRangeRecordBytes) + 2, records[i].Key);
                Word(bytes, (i * ArcRangeRecordBytes) + 4, records[i].ArcHeading);
            }

            yield return (PortHex.Parse(table.Dgroup), bytes);

            foreach (ArcRangeRecordDto record in records)
            {
                if (record.Bands is not { Count: > 0 } bands)
                {
                    continue;
                }

                byte[] band = new byte[bands.Count];
                for (int b = 0; b < bands.Count; b++)
                {
                    band[b] = (byte)(((bands[b].Width >> 4) & 0x3F) | ((bands[b].TierCode & 3) << 6));
                }

                yield return (PortHex.Parse(record.BandDgroup), band);
            }
        }

        foreach (string pad in document.AlignmentPads ?? [])
        {
            yield return (PortHex.Parse(pad), [0]);
        }
    }

    /// <summary>The flight-side constants.</summary>
    /// <param name="document">The <c>exe/tables/flight_tuning.json</c> document.</param>
    public static IEnumerable<(int Dgroup, byte[] Bytes)> FlightTuning(FlightTuningDto document)
    {
        ArgumentNullException.ThrowIfNull(document);
        yield return (FlightConstantOffsets.PullUpTuning, WordBytes(document.PullUpTuning));

        byte[] radius = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(radius, document.LandingZoneCaptureRadius);
        yield return (FlightConstantOffsets.CaptureRadius, radius);
    }

    /// <summary>
    /// The cockpit's constant layout: the viewport table, the seven HUD layout blocks, the ten
    /// region tables and the small style tables around them.
    /// </summary>
    /// <param name="document">The <c>exe/tables/cockpit_layout.json</c> document.</param>
    /// <exception cref="InvalidDataException">A table is missing or the wrong length.</exception>
    public static IEnumerable<(int Dgroup, byte[] Bytes)> CockpitLayout(CockpitLayoutDto document)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<CockpitViewportDto> viewports = document.Viewports ?? [];
        byte[] suffixPointers = new byte[viewports.Count * 2];
        for (int i = 0; i < viewports.Count; i++)
        {
            yield return (PortHex.Parse(viewports[i].Dgroup), RectBytes(viewports[i].Viewport));
            Word(suffixPointers, i * 2, PortHex.Parse(viewports[i].AssetSuffixPointer));
        }

        yield return (CockpitLayoutOffsets.SuffixPointerTable, suffixPointers);

        foreach (HudLayoutBlockDto block in document.HudLayouts ?? [])
        {
            yield return (PortHex.Parse(block.Dgroup), HudLayoutBytes(block));
        }

        foreach (CockpitRegionTableDto region in document.Regions ?? [])
        {
            yield return (PortHex.Parse(region.RectTableDgroup), RectTableBytes(region.Rects));
            if (region.PivotTableDgroup is { } pivots)
            {
                yield return (PortHex.Parse(pivots), PointTableBytes(region.Pivots));
            }

            if (region.OffStateTableDgroup is { } off)
            {
                yield return (PortHex.Parse(off), PointTableBytes(region.OffStateSource));
            }

            if (region.OnStateTableDgroup is { } on)
            {
                yield return (PortHex.Parse(on), PointTableBytes(region.OnStateSource));
            }
        }

        List<F86GearFlapIconDto> icons = document.F86GearFlapIcons ?? [];
        byte[] iconBytes = new byte[icons.Count * 4];
        foreach (F86GearFlapIconDto icon in icons)
        {
            int state = (icon.FlapDown ? 2 : 0) | (icon.GearDown ? 1 : 0);
            PointBytes(icon.Source).CopyTo(iconBytes.AsSpan(state * 4));
        }

        yield return (CockpitLayoutOffsets.F86GearFlapIcons, iconBytes);

        foreach (CountermeasureTextDto text in document.CountermeasureText ?? [])
        {
            byte[] bytes = new byte[8];
            PointBytes(text.Chaff).CopyTo(bytes.AsSpan(0));
            PointBytes(text.Flare).CopyTo(bytes.AsSpan(4));
            yield return (PortHex.Parse(text.Dgroup), bytes);
        }

        if (document.WeaponAmmo is { } style)
        {
            yield return (
                PortHex.Parse(style.TextColorDgroup), ByteBytes(style.TextColorByAircraft));
            yield return (
                PortHex.Parse(style.BackgroundColorDgroup),
                ByteBytes(style.BackgroundColorByAircraft));
        }
    }

    private static byte[] RectBytes(ScreenRectDto? rect)
    {
        ScreenRectDto value = rect ?? throw new InvalidDataException("a cockpit table row has no rectangle");
        byte[] bytes = new byte[8];
        Word(bytes, 0, value.X);
        Word(bytes, 2, value.Y);
        Word(bytes, 4, value.Width);
        Word(bytes, 6, value.Height);
        return bytes;
    }

    private static byte[] PointBytes(ScreenPointDto? point)
    {
        ScreenPointDto value = point ?? throw new InvalidDataException("a cockpit table row has no point");
        byte[] bytes = new byte[4];
        Word(bytes, 0, value.X);
        Word(bytes, 2, value.Y);
        return bytes;
    }

    private static byte[] RectTableBytes(List<ScreenRectDto>? rects)
    {
        List<ScreenRectDto> list = rects ?? [];
        byte[] bytes = new byte[list.Count * 8];
        for (int i = 0; i < list.Count; i++)
        {
            RectBytes(list[i]).CopyTo(bytes.AsSpan(i * 8));
        }

        return bytes;
    }

    private static byte[] PointTableBytes(List<ScreenPointDto>? points)
    {
        List<ScreenPointDto> list = points ?? [];
        byte[] bytes = new byte[list.Count * 4];
        for (int i = 0; i < list.Count; i++)
        {
            PointBytes(list[i]).CopyTo(bytes.AsSpan(i * 4));
        }

        return bytes;
    }

    private static byte[] HudLayoutBytes(HudLayoutBlockDto block)
    {
        ScreenRectDto clip = block.InnerClip
                             ?? throw new InvalidDataException(
                                 $"HUD layout block {block.Dgroup} has no inner clip rectangle");
        byte[] bytes = new byte[CockpitLayoutOffsets.HudLayoutBytes];
        int[] words =
        [
            block.FlagsIndicatorX, block.FlagsIndicatorColumn,
            block.AltitudeAnchorX, block.AltitudeAnchorY,
            block.WaypointAnchorX, block.WaypointAnchorY,
            block.VsiAnchorX, block.VsiAnchorY,
            block.HeadingAnchorY, block.WaypointSecondY,
            block.TargetMarkerClipLeft, block.TargetMarkerClipRight,
            block.MessageLineY,
            clip.X, clip.Y, clip.Width, clip.Height,
        ];

        for (int i = 0; i < words.Length; i++)
        {
            Word(bytes, i * 2, words[i]);
        }

        PortHex.BytesOrZero(block.Unknown0x22, 2).CopyTo(bytes.AsSpan(0x22));
        PortHex.BytesOrZero(block.Unknown0x24, 2).CopyTo(bytes.AsSpan(0x24));
        return bytes;
    }

    /// <summary>Bytes of an <c>s_engagement_class_proto</c>.</summary>
    public const int EngagementRecordBytes = 0x2E;

    /// <summary>Bytes of the dropped prototype's surviving head.</summary>
    public const int TruncatedPrototypeBytes = 14;

    /// <summary>Bytes of the killed prototype at <c>[0x2540]</c>: a head plus its closing <c>0xFFFF</c>.</summary>
    public const int KilledPrototypeBytes = 16;

    /// <summary>Bytes of one <c>s_arc_range_record</c>.</summary>
    public const int ArcRangeRecordBytes = 6;

    private static byte[] PrototypeBytes(EngagementPrototypeDto dto, int length)
    {
        byte[] bytes = new byte[length];
        Word(bytes, 0x00, PortHex.Parse(dto.ClassRecordDgroup));
        PortHex.BytesOrZero(dto.Unknown0x02, 1).CopyTo(bytes.AsSpan(0x02));
        PortHex.BytesOrZero(dto.Unknown0x03, 1).CopyTo(bytes.AsSpan(0x03));
        Word(bytes, 0x04, PortHex.Parse(dto.NamePointer));
        List<int> divisors = dto.FireAuthorityDivisors ?? [0, 0, 0];
        for (int i = 0; i < 3 && i < divisors.Count; i++)
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

        if (length != EngagementRecordBytes)
        {
            return bytes;
        }

        List<PrototypeWeaponSlotDto> slots = dto.WeaponSlots ?? [];
        for (int i = 0; i < slots.Count && i < 4; i++)
        {
            Word(bytes, 0x0E + (i * 2), PortHex.ParseOrDefault(slots[i].WeaponClassDgroup));
            List<int> muzzle = slots[i].MuzzleOffset ?? [0, 0, 0];
            for (int axis = 0; axis < 3 && axis < muzzle.Count; axis++)
            {
                bytes[0x1A + (i * 3) + axis] = unchecked((byte)(sbyte)muzzle[axis]);
            }
        }

        List<int> init = dto.InitParams ?? [0, 0, 0, 0];
        for (int i = 0; i < 4 && i < init.Count; i++)
        {
            bytes[0x16 + i] = (byte)init[i];
        }

        Word(bytes, 0x26, PortHex.ParseOrDefault(dto.ArcDescriptorDgroup));
        bytes[0x28] = (byte)dto.CountsTowardPlayerPressure;
        PortHex.BytesOrZero(dto.Unknown0x29, 3).CopyTo(bytes.AsSpan(0x29));
        bytes[0x2C] = (byte)dto.InsigniaIndex;
        bytes[0x2D] = (byte)dto.ExpirySeconds;
        return bytes;
    }

    private static void Word(Span<byte> target, int at, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(target[at..], unchecked((ushort)value));

    private static byte[] ByteBytes(List<int>? values) =>
        [.. (values ?? []).Select(v => (byte)v)];

    private static byte[] WordBytes(List<int>? values)
    {
        List<int> list = values ?? [];
        byte[] bytes = new byte[list.Count * 2];
        for (int i = 0; i < list.Count; i++)
        {
            Word(bytes, i * 2, list[i]);
        }

        return bytes;
    }
}

/// <summary>The DGROUP offsets <c>exe/tables/combat_constants.json</c> publishes.</summary>
public static class CombatConstantOffsets
{
    /// <summary><c>g_vec2_origin_pivot</c>.</summary>
    public const int OriginPivot = 0x0680;

    /// <summary><c>g_aircraft_type_fire_bonus_u8x4</c>.</summary>
    public const int FireBonus = 0x0DEC;

    /// <summary><c>g_engagement_phase_attr_table</c>.</summary>
    public const int PhaseAttributes = 0x0F0E;

    /// <summary>The first of the sixteen skill-indexed tuning rows.</summary>
    public const int SkillTables = 0x0F40;

    /// <summary><c>g_flyable_statblock_ptr_table</c>.</summary>
    public const int FlyablePrototypeTable = 0x0FBC;

    /// <summary><c>g_type_resource_table</c>.</summary>
    public const int TypeResourceTable = 0x2550;

    /// <summary><c>g_engagement_spawn_probability_table</c>.</summary>
    public const int AdmissionProbability = 0x2A10;

    /// <summary>The admission interval table.</summary>
    public const int AdmissionInterval = 0x2A14;

    /// <summary>The concurrent-engagement cap table.</summary>
    public const int AdmissionCap = 0x2A18;

    /// <summary><c>g_engagement_duration_table</c>.</summary>
    public const int EngagementDuration = 0x45A2;
}

/// <summary>The DGROUP offsets <c>exe/tables/flight_tuning.json</c> publishes.</summary>
public static class FlightConstantOffsets
{
    /// <summary>The seven-word pull-up tuning table, <c>g_joystick_calib_table_BASE</c>.</summary>
    public const int PullUpTuning = 0x35F8;

    /// <summary>The landing-zone capture radius, an <c>i32</c>.</summary>
    public const int CaptureRadius = 0x9E74;
}

/// <summary>
/// The DGROUP offsets <c>exe/tables/cockpit_layout.json</c> publishes that are not carried row by
/// row in the document itself.
/// </summary>
public static class CockpitLayoutOffsets
{
    /// <summary>The per-aircraft 3-D viewport table, <c>g_aircraft_viewport_table</c>.</summary>
    public const int ViewportTable = 0x3C02;

    /// <summary>The full-screen default HUD layout block.</summary>
    public const int HudLayoutDefault = 0x3078;

    /// <summary>The first per-aircraft HUD layout block.</summary>
    public const int HudLayoutPerAircraft = 0x309E;

    /// <summary>Bytes in one HUD layout block: nineteen <c>u16</c>.</summary>
    public const int HudLayoutBytes = 0x26;

    /// <summary>The cockpit-asset suffix near-pointer table (<c>image@0x0E3E8</c>).</summary>
    public const int SuffixPointerTable = 0x4226;

    /// <summary>The F-86's four combined gear+flap source points.</summary>
    public const int F86GearFlapIcons = 0x424C;
}
