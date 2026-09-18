using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>
/// The three weapon tables: the static weapon-class descriptors, the per-aircraft loadouts, and the
/// 4-character name pool the HUD prints.
/// </summary>
/// <remarks>
/// <para>
/// The three regions are contiguous in DGROUP order <c>0x43B8</c> (names) → <c>0x43FE</c>
/// (loadouts), with the descriptor table further back at <c>0x1158</c>.  Each is its own slice, so a
/// failure names the table.
/// </para>
/// <para>
/// Measured while extracting: <c>s_weapon_class_desc +0x10</c> holds a DGROUP near pointer to the
/// world-object class record the projectile renders as — <c>0x4FC0</c> (<c>bullet</c>) in all 17 gun
/// records, <c>0x74AE</c> (<c>hell</c>) in all three guided ones.  Hypothesis-grade: no reader is
/// decoded, but shows <c>0x74AE</c> stamped into a pool template as exactly that kind of pointer.
/// </para>
/// </remarks>
internal sealed class WeaponExeTables : IExeTable
{
    /// <summary>DGROUP offset of the 4-character weapon-name pool.</summary>
    public const int NamePoolDgroup = 0x43B8;

    /// <summary>Bytes the name pool occupies, up to the per-aircraft table.</summary>
    public const int NamePoolBytes = PerAircraftDgroup - NamePoolDgroup;

    /// <summary>DGROUP offset of the per-aircraft weapon table.</summary>
    public const int PerAircraftDgroup = 0x43FE;

    /// <summary>Bytes per per-aircraft record.</summary>
    public const int PerAircraftRecordBytes = 0x12;

    /// <summary>Loadout slots per aircraft.</summary>
    public const int SlotsPerAircraft = 3;

    /// <inheritdoc/>
    public string TreePath => "exe/weapons.json";

    /// <inheritdoc/>
    public string Description =>
        "the 20 weapon-class descriptors (speed envelope, damage roll, ammunition), the six " +
        "per-aircraft loadouts and the 4-character weapon names the HUD prints";

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        int unknown = 0;
        List<WeaponClassRecordDto> classes = new List<WeaponClassRecordDto>(WeaponClass.StaticTableRecordCount);
        for (int i = 0; i < WeaponClass.StaticTableRecordCount; i++)
        {
            int dgroup = WeaponClass.StaticTableDgroupOffset + (i * WeaponClass.RecordBytes);
            ReadOnlySpan<byte> record = image.Slice(ExeAddresses.Image(dgroup), WeaponClass.RecordBytes);
            unknown += 4 + 10 + 2 + 6 + 1;

            classes.Add(new WeaponClassRecordDto
            {
                Index = i,
                Dgroup = PortHex.Format(dgroup),
                MinimumRange = record[0x04],
                MaximumScore = record[0x05],
                ProjectileClassPointer =
                    PortHex.Format(BinaryPrimitives.ReadUInt16LittleEndian(record[0x10..])),
                InitialSpeed = BinaryPrimitives.ReadInt16LittleEndian(record[0x14..]),
                BoostSpeedMax = BinaryPrimitives.ReadInt16LittleEndian(record[0x16..]),
                CoastSpeedMin = BinaryPrimitives.ReadInt16LittleEndian(record[0x18..]),
                BoostAcceleration = BinaryPrimitives.ReadInt16LittleEndian(record[0x1A..]),
                CoastDeceleration = BinaryPrimitives.ReadInt16LittleEndian(record[0x1C..]),
                BoostFrames = record[0x1E],
                LifetimeFrames = record[0x1F],
                TargetScoreKey = BinaryPrimitives.ReadInt16LittleEndian(record[0x20..]),
                DamageScale = record[0x22],
                DamageMultiplier = record[0x23],
                ClassFlags = PortHex.Format(record[0x24], 2),
                FireTone = PortHex.Format(record[0x2B], 2),
                AmmoPerShot = record[0x2C],
                Unknown0x00 = PortHex.Bytes(record.Slice(0x00, 4)),
                Unknown0x06 = PortHex.Bytes(record.Slice(0x06, 10)),
                Unknown0x12 = PortHex.Bytes(record.Slice(0x12, 2)),
                Unknown0x25 = PortHex.Bytes(record.Slice(0x25, 6)),
                Unknown0x2D = PortHex.Bytes(record.Slice(0x2D, 1)),
            });
        }

        IReadOnlyList<string> basenames = AircraftDefinition.FlyableBasenames;
        List<AircraftWeaponRecordDto> loadouts = new List<AircraftWeaponRecordDto>(basenames.Count);
        for (int i = 0; i < basenames.Count; i++)
        {
            int dgroup = PerAircraftDgroup + (i * PerAircraftRecordBytes);
            ReadOnlySpan<byte> record = image.Slice(ExeAddresses.Image(dgroup), PerAircraftRecordBytes);

            List<AircraftWeaponSlotDto> slots = new List<AircraftWeaponSlotDto>(SlotsPerAircraft);
            for (int slot = 0; slot < SlotsPerAircraft; slot++)
            {
                ushort ammo = BinaryPrimitives.ReadUInt16LittleEndian(record[(0x06 + (slot * 4))..]);
                ushort namePointer = BinaryPrimitives.ReadUInt16LittleEndian(record[(0x08 + (slot * 4))..]);
                slots.Add(new AircraftWeaponSlotDto
                {
                    Slot = slot,
                    Ammunition = ammo,
                    NamePointer = PortHex.Format(namePointer),
                    Name = namePointer == 0 ? null : ReadString(image, namePointer),
                });
            }

            loadouts.Add(new AircraftWeaponRecordDto
            {
                Aircraft = basenames[i],
                Index = i,
                Dgroup = PortHex.Format(dgroup),
                Slots = slots,

                // The record's three head words, named: player_weapon_loadout_publish
                // @image@0x2762D publishes +0x00 to g_chaff_stock [0xED32] and +0x02 to
                // g_flare_stock [0xED33] (both as BYTES) two instructions before the slot walk, and
                // +0x04 is the aircraft's default weapon slot.
                ChaffStock = BinaryPrimitives.ReadUInt16LittleEndian(record[0x00..]),
                FlareStock = BinaryPrimitives.ReadUInt16LittleEndian(record[0x02..]),
                DefaultSlot = BinaryPrimitives.ReadUInt16LittleEndian(record[0x04..]),
            });
        }

        ReadOnlySpan<byte> pool = image.Slice(ExeAddresses.Image(NamePoolDgroup), NamePoolBytes);
        List<WeaponNameDto> names = new List<WeaponNameDto>();
        for (int at = 0; at < pool.Length;)
        {
            if (pool[at] == 0)
            {
                at++;
                continue;
            }

            int end = at;
            while (end < pool.Length && pool[end] != 0)
            {
                end++;
            }

            names.Add(new WeaponNameDto
            {
                Dgroup = PortHex.Format(NamePoolDgroup + at),
                Text = Encoding.ASCII.GetString(pool[at..end]),
            });
            at = end + 1;
        }

        IReadOnlyList<(int Offset, byte[] Bytes)> residue = ByteResidue.Diff(pool, RebuildNamePool(names));
        WeaponTablesDocumentDto dto = new WeaponTablesDocumentDto
        {
            Format = "cyac.weaponTables/1",
            About =
                "How a shot flies, how hard it hits and what the HUD calls it. " +
                "THE CHAIN: an aircraft's stat block (from the six-entry pointer array at DGROUP " +
                "0x0FBC, mov ax,[si+0xfbc] @image@0x247A4) carries three weapon-class near pointers " +
                "at +0x0E/+0x10/+0x12, each landing on a record of the static table below; a 46-byte " +
                "rep movsw @image@0x247B5 copies that stat block to g_record_aircraft_data [0xEF22], " +
                "which is why [0xEF30] looks unwritten. The per-aircraft table is separate: it holds " +
                "the magazine sizes and the 4-character names. " +
                "NAMING NOTE: the schema calls +0x00..+0x13 name_str; that is a misnomer - " +
                "the shipped records hold binary there and no reader of a string at +0x00 exists, so " +
                "the block is carried as unknown_* apart from the two fields " +
                "combat_target_range_and_angle_qualify @0x07C50 reads (+0x04 minimum range, " +
                "+0x05 maximum score). +0x14 is a time-of-flight, not a range. " +
                "MEASURED HERE: +0x10 is a class-record near pointer - 0x4FC0 (bullet) on all 17 gun " +
                "records, 0x74AE (hell) on all three guided ones; hypothesis-grade, no reader decoded. " +
                "The per-aircraft record's three head words are named after their reader: " +
                "player_weapon_loadout_publish @image@0x2762D copies +0x00 to g_chaff_stock [0xED32] " +
                "and +0x02 to g_flare_stock [0xED33] (as BYTES), and +0x04 is the aircraft's default " +
                "weapon slot - 15/15/gun on the F-4E and the MiG-21MF, zeros elsewhere.",
            DgroupImageBase = PortHex.Format(ExeAddresses.DgroupImageBase, 5),
            WeaponClassSource = new DataSourceDto
            {
                Image = PortHex.Format(WeaponClass.StaticTableImageOffset, 5),
                Dgroup = PortHex.Format(WeaponClass.StaticTableDgroupOffset),
                Bytes = WeaponClass.StaticTableRecordCount * WeaponClass.RecordBytes,
                Entries = WeaponClass.StaticTableRecordCount,
                ElementType = "s_weapon_class_desc",
            },
            WeaponClasses = classes,
            PerAircraftSource = new DataSourceDto
            {
                Image = PortHex.Format(ExeAddresses.Image(PerAircraftDgroup), 5),
                Dgroup = PortHex.Format(PerAircraftDgroup),
                Bytes = basenames.Count * PerAircraftRecordBytes,
                Entries = basenames.Count,
                ElementType = "u16[9]",
            },
            PerAircraftWeapons = loadouts,
            WeaponNameSource = new DataSourceDto
            {
                Image = PortHex.Format(ExeAddresses.Image(NamePoolDgroup), 5),
                Dgroup = PortHex.Format(NamePoolDgroup),
                Bytes = NamePoolBytes,
                Entries = names.Count,
                ElementType = "asciiz",
            },
            WeaponNames = names,
            WeaponNameResidue =
                [.. residue.Select(r => new ByteSpanDto(PortHex.Format(r.Offset, 2), Convert.ToHexString(r.Bytes)))],
        };

        unknown += ByteResidue.Count(residue);
        int guided = classes.Count(c => (PortHex.Parse(c.ClassFlags) & (int)WeaponClassFlags.Guided) != 0);
        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.WeaponTablesDocumentDto),
            unknown,
            $"weapons: {classes.Count} classes ({guided} guided), {loadouts.Count} loadouts, " +
            $"{names.Count} names");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        WeaponTablesDocumentDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.WeaponTablesDocumentDto)
                                      ?? throw new InvalidDataException("the weapon-tables document is empty");
        if (dto.WeaponClasses is not { } classes || classes.Count != WeaponClass.StaticTableRecordCount)
        {
            throw new InvalidDataException(
                $"expected {WeaponClass.StaticTableRecordCount} weapon-class records, found " +
                $"{dto.WeaponClasses?.Count ?? 0}");
        }

        byte[] classBytes = new byte[classes.Count * WeaponClass.RecordBytes];
        for (int i = 0; i < classes.Count; i++)
        {
            WeaponClassRecordDto item = classes[i];
            Span<byte> record = classBytes.AsSpan(i * WeaponClass.RecordBytes, WeaponClass.RecordBytes);
            PortHex.BytesOrZero(item.Unknown0x00, 4).CopyTo(record);
            record[0x04] = (byte)item.MinimumRange;
            record[0x05] = (byte)item.MaximumScore;
            PortHex.BytesOrZero(item.Unknown0x06, 10).CopyTo(record[0x06..]);
            BinaryPrimitives.WriteUInt16LittleEndian(
                record[0x10..], (ushort)PortHex.ParseOrDefault(item.ProjectileClassPointer));
            PortHex.BytesOrZero(item.Unknown0x12, 2).CopyTo(record[0x12..]);
            BinaryPrimitives.WriteInt16LittleEndian(record[0x14..], (short)item.InitialSpeed);
            BinaryPrimitives.WriteInt16LittleEndian(record[0x16..], (short)item.BoostSpeedMax);
            BinaryPrimitives.WriteInt16LittleEndian(record[0x18..], (short)item.CoastSpeedMin);
            BinaryPrimitives.WriteInt16LittleEndian(record[0x1A..], (short)item.BoostAcceleration);
            BinaryPrimitives.WriteInt16LittleEndian(record[0x1C..], (short)item.CoastDeceleration);
            record[0x1E] = (byte)item.BoostFrames;
            record[0x1F] = (byte)item.LifetimeFrames;
            BinaryPrimitives.WriteInt16LittleEndian(record[0x20..], (short)item.TargetScoreKey);
            record[0x22] = (byte)item.DamageScale;
            record[0x23] = (byte)item.DamageMultiplier;
            record[0x24] = (byte)PortHex.Parse(item.ClassFlags);
            PortHex.BytesOrZero(item.Unknown0x25, 6).CopyTo(record[0x25..]);
            record[0x2B] = (byte)PortHex.Parse(item.FireTone);
            record[0x2C] = (byte)item.AmmoPerShot;
            record[0x2D] = PortHex.BytesOrZero(item.Unknown0x2D, 1)[0];
        }

        if (dto.PerAircraftWeapons is not { } loadouts
            || loadouts.Count != AircraftDefinition.FlyableBasenames.Count)
        {
            throw new InvalidDataException(
                $"expected {AircraftDefinition.FlyableBasenames.Count} per-aircraft weapon records, " +
                $"found {dto.PerAircraftWeapons?.Count ?? 0}");
        }

        byte[] loadoutBytes = new byte[loadouts.Count * PerAircraftRecordBytes];
        for (int i = 0; i < loadouts.Count; i++)
        {
            AircraftWeaponRecordDto item = loadouts[i];
            Span<byte> record = loadoutBytes.AsSpan(i * PerAircraftRecordBytes, PerAircraftRecordBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(record[0x00..], (ushort)item.ChaffStock);
            BinaryPrimitives.WriteUInt16LittleEndian(record[0x02..], (ushort)item.FlareStock);
            BinaryPrimitives.WriteUInt16LittleEndian(record[0x04..], (ushort)item.DefaultSlot);
            if (item.Slots is not { Count: SlotsPerAircraft } slots)
            {
                throw new InvalidDataException(
                    $"aircraft '{item.Aircraft}': expected {SlotsPerAircraft} weapon slots");
            }

            for (int slot = 0; slot < slots.Count; slot++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    record[(0x06 + (slot * 4))..], (ushort)slots[slot].Ammunition);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    record[(0x08 + (slot * 4))..], (ushort)PortHex.ParseOrDefault(slots[slot].NamePointer));
            }
        }

        byte[] pool = RebuildNamePool(dto.WeaponNames ?? []);
        if (dto.WeaponNameResidue is { } residue)
        {
            ByteResidue.Apply(
                pool, residue.Select(r => (PortHex.Parse(r.Offset), Convert.FromHexString(r.Hex))));
        }

        return
        [
            new ExeSlice("weapon-class descriptor table", WeaponClass.StaticTableImageOffset, classBytes),
            new ExeSlice("weapon-name pool", ExeAddresses.Image(NamePoolDgroup), pool),
            new ExeSlice("per-aircraft weapon table", ExeAddresses.Image(PerAircraftDgroup), loadoutBytes),
        ];
    }

    private static byte[] RebuildNamePool(IReadOnlyList<WeaponNameDto> names)
    {
        byte[] pool = new byte[NamePoolBytes];
        foreach (WeaponNameDto name in names)
        {
            int at = PortHex.Parse(name.Dgroup) - NamePoolDgroup;
            byte[] text = Encoding.ASCII.GetBytes(name.Text ?? string.Empty);
            if (at < 0 || at + text.Length + 1 > pool.Length)
            {
                throw new InvalidDataException(
                    $"weapon name \"{name.Text}\" at {name.Dgroup} does not fit the name pool");
            }

            text.CopyTo(pool.AsSpan(at));
        }

        return pool;
    }

    private static string ReadString(ReadOnlySpan<byte> image, ushort dgroupOffset)
    {
        int start = ExeAddresses.Image(dgroupOffset);
        int end = start;
        while (end < image.Length && image[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(image[start..end]);
    }
}
