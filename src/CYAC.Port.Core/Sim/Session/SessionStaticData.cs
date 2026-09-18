using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Sim.Combat;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// The combat kernel's CONSTANT DGROUP surface, rebuilt from the transformed tree's own documents.
/// </summary>
/// <remarks>
/// <para>
/// Every one of those tables now has an open-format document (<c>exe/tables/engagement.json</c>,
/// <c>combat_constants.json</c>, <c>aircraft_classes.json</c>, <c>flight_tuning.json</c>, on top of
/// <c>exe/classes.json</c>, <c>exe/weapons.json</c>, <c>exe/meshes/*.json</c> and the two combat
/// tables), so this type reads <see cref="DgroupConstants"/> and the running port opens no image at
/// all.
/// </para>
/// <para>
/// <b>Live beats static.</b> A DGROUP address the RUNNING register file covers is read from there:
/// the document's value is the load-time constant, and anything the kernel has written since is the
/// truth.  The one region where this is load-bearing is the player's runtime engagement prototype
/// <c>g_record_aircraft_data [0xEF22]</c>, which is all-zero in the image and is built at mission
/// load.
/// </para>
/// </remarks>
public sealed class SessionCombatStaticData : ICombatStaticData
{
    private readonly DgroupConstants _constants;

    /// <summary>DGROUP's own origin inside the L1 image (SEGMENT_ORIGINS</c>).</summary>
    public const int DgroupImageBase = DgroupConstants.DgroupImageBase;

    /// <summary>Reads the constant surface from the tree's documents.</summary>
    /// <param name="constants">The rebuilt DGROUP surface, i.e. <c>DataTree.Constants</c>.</param>
    public SessionCombatStaticData(DgroupConstants constants)
    {
        ArgumentNullException.ThrowIfNull(constants);
        _constants = constants;
    }

    /// <summary>The live register file, whose covered offsets win over the image's constants.</summary>
    public CombatRegisters? Live { get; set; }

    /// <inheritdoc/>
    public byte Byte(int dgroupOffset) =>
        Live is not null && Live.Covers(dgroupOffset, 1)
            ? Live.Byte(dgroupOffset)
            : _constants.Byte(dgroupOffset);

    /// <inheritdoc/>
    public ushort Word(int dgroupOffset) =>
        Live is not null && Live.Covers(dgroupOffset, 2)
            ? Live.Word(dgroupOffset)
            : _constants.Word(dgroupOffset);

    /// <summary>A CONSTANT DGROUP span (never the live register file).</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    /// <param name="length">How many bytes.</param>
    public ReadOnlySpan<byte> ImageSpan(int dgroupOffset, int length) =>
        _constants.Span(dgroupOffset, length);
}

/// <summary>
/// <see cref="IEngagementPrototypes"/> over the same program image — the 46-byte
/// <c>s_engagement_class_proto</c> records at DGROUP ≈<c>0x1812..0x24E8</c>.
/// </summary>
/// <remarks>
/// Mission-load constants with no writers, so the image IS the source; the same stopgap ruling as
/// <see cref="SessionCombatStaticData"/>.
/// </remarks>
/// <param name="staticData">The constant surface the records are read through.</param>
public sealed class SessionEngagementPrototypes(SessionCombatStaticData staticData)
    : IEngagementPrototypes
{
    private readonly SessionCombatStaticData _data =
        staticData ?? throw new ArgumentNullException(nameof(staticData));

    /// <inheritdoc/>
    public ushort FlagsWord(ushort prototypeRef) => _data.Word(prototypeRef + 0x0C);

    /// <inheritdoc/>
    public ReadOnlySpan<byte> ArcParameters(ushort prototypeRef, int length) =>
        _data.ImageSpan(_data.Word(prototypeRef + 0x26), length);

    /// <inheritdoc/>
    public byte InitialHitPoints(ushort prototypeRef) => _data.Byte(prototypeRef + 0x09);

    /// <inheritdoc/>
    public EngagementInitParams InitParams(ushort prototypeRef) => new(
        _data.Byte(prototypeRef + 0x16),
        _data.Byte(prototypeRef + 0x17),
        _data.Byte(prototypeRef + 0x18),
        _data.Byte(prototypeRef + 0x19));

    /// <inheritdoc/>
    public byte ExpirySeconds(ushort prototypeRef) => _data.Byte(prototypeRef + 0x2D);

    /// <inheritdoc/>
    public byte CountsTowardsPlayerPressure(ushort prototypeRef) => _data.Byte(prototypeRef + 0x28);

    /// <inheritdoc/>
    public byte SlotDescriptorTag(ushort prototypeRef, byte slotIndex) =>
        _data.Byte(_data.Word(prototypeRef + 0x0E + (slotIndex * 2)));
}

/// <summary>What <c>aircraft_class_table_lookup @image@0x24058</c> answers for one class id.</summary>
/// <param name="Flag">The record's flag byte: 0, 1 or anything else.</param>
/// <param name="Value">Its word.</param>
public readonly record struct AircraftClassEntry(byte Flag, ushort Value)
{
    /// <summary>The player's sentinel, <c>0xFFFF</c> — class id 0.</summary>
    public const ushort PlayerSentinel = 0xFFFF;

    /// <summary>The home-base sentinel, <c>0xFFFE</c> — class id 1.</summary>
    public const ushort HomeBaseSentinel = 0xFFFE;

    /// <summary>True when <see cref="Value"/> is a DGROUP engagement-class PROTOTYPE pointer.</summary>
    public bool IsEngagementPrototype =>
        Flag == 0 && Value != PlayerSentinel && Value != HomeBaseSentinel;

    /// <summary>True when <see cref="Value"/> is a DGROUP CLASS-RECORD pointer (a scenery class).</summary>
    public bool IsClassRecord => Flag == 1;
}

/// <summary>
/// The 46-entry AIRCRAFT-CLASS TABLE at <c>image@0x34F90</c> — the join between a mission's authored
/// class id and what the engine spawns for it.
/// </summary>
/// <remarks>
/// <para>
/// <c>aircraft_class_table_lookup @image@0x24058</c> reads a 3-byte record
/// <c>{u8 flag, u16 value}</c> at <c>idx*3 + 0x30</c> in the segment
/// <c>g_aircraft_class_table_seg [0xB102]</c>, a static linker fixup pointing at
/// <c>image@0x34F60</c> with zero writers image-wide — so the table is
/// <c>image@0x34F90</c>.  Its three arms:
/// </para>
/// <list type="bullet">
///   <item><description><b>flag 0</b> — the value is returned in <c>AX</c>.  Ids 0 and 1 carry the
///   sentinels <c>0xFFFF</c>/<c>0xFFFE</c> (player / home base); ids 6..24 carry a DGROUP
///   engagement-class prototype pointer.</description></item>
///   <item><description><b>flag 1</b> — ids 26..45: the value is stored through the caller's OUT
///   pointer (the staged pool record's <c>+0x00</c>) and <c>AX</c> comes back 0, so the object spawns
///   as plain scenery with no engagement block.</description></item>
///   <item><description><b>flag 2</b> — ids 2..5, 25, 35: NOTHING is stored and the ENTRY index comes
///   back in <c>AX</c>.  No shipped mission authors one.
///   </description></item>
/// </list>
/// </remarks>
public sealed class AircraftClassTable
{
    private readonly AircraftClassEntry[] _entries;

    /// <summary>The table's absolute image offset: <c>image@0x34F60 + 0x30</c>.</summary>
    public const int TableImageOffset = 0x34F90;

    /// <summary>How many 3-byte records it holds.</summary>
    public const int EntryCount = 46;

    private AircraftClassTable(AircraftClassEntry[] entries) => _entries = entries;

    /// <summary>
    /// Reads the table from <c>exe/tables/aircraft_classes.json</c>, resolving every named pointer
    /// against the document that owns the structure it names.
    /// </summary>
    /// <param name="tree">The opened data tree.</param>
    /// <exception cref="InvalidDataException">
    /// A row names a target no document publishes, or names one whose declared DGROUP offset is not
    /// the offset the row's own word carries — i.e. the tree is internally inconsistent.
    /// </exception>
    /// <remarks>
    /// This is what "a pointer is a NAME, resolved at load" means in practice: the row says
    /// <c>"target": "P-51D"</c> or <c>"target": "airport"</c>, the loader looks that name up in
    /// <c>exe/tables/engagement.json</c> / <c>exe/classes.json</c> / <c>exe/meshes/*.json</c>, and the
    /// offset it gets back must be the one the row carries.  Nothing dereferences an image offset, and
    /// a rename in one document that is not carried through to the other is a load-time error.
    /// </remarks>
    public static AircraftClassTable Load(DataTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        AircraftClassTableDto document = tree.AircraftClasses;
        if (document.Entries is not { } rows || rows.Count != EntryCount)
        {
            throw new InvalidDataException(
                $"exe/tables/aircraft_classes.json must carry {EntryCount} rows, found "
                    + $"{document.Entries?.Count ?? 0}");
        }

        Dictionary<string, int> prototypes = (tree.Engagement.Prototypes ?? [])
            .Where(p => p.Name is not null)
            .ToDictionary(p => p.Name!, p => PortHex.Parse(p.Dgroup), StringComparer.Ordinal);
        Dictionary<string, int> classes = (tree.Classes.Classes ?? [])
            .Where(c => c.Name is not null)
            .GroupBy(c => c.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => PortHex.Parse(g.First().Dgroup), StringComparer.OrdinalIgnoreCase);

        AircraftClassEntry[] entries = new AircraftClassEntry[EntryCount];
        for (int i = 0; i < EntryCount; i++)
        {
            AircraftClassEntryDto row = rows[i];
            ushort value = (ushort)PortHex.Parse(row.Value);
            if (row.Target is { } target)
            {
                int? resolved = row.Kind switch
                {
                    "engagementPrototype" => prototypes.TryGetValue(target, out int p) ? p : null,
                    "classRecord" => ResolveClass(tree, classes, target),
                    _ => null,
                };

                if (resolved is { } offset && offset != value)
                {
                    throw new InvalidDataException(
                        $"aircraft class {i} names \"{target}\", which the tree places at "
                            + $"0x{offset:X4}, but the row's own word is 0x{value:X4}");
                }

                if (resolved is null && row.Kind is "engagementPrototype" or "classRecord")
                {
                    throw new InvalidDataException(
                        $"aircraft class {i} names \"{target}\", which no document in the tree publishes");
                }
            }

            entries[i] = new AircraftClassEntry((byte)row.Flag, value);
        }

        return new AircraftClassTable(entries);
    }

    private static int? ResolveClass(
        DataTree tree, IReadOnlyDictionary<string, int> classes, string name)
    {
        if (classes.TryGetValue(name, out int fromRegistry))
        {
            return fromRegistry;
        }

        return tree.HasExeMesh(name) && tree.ExeMesh(name).Slot?.Dgroup is { } dgroup
            ? PortHex.Parse(dgroup)
            : null;
    }

    /// <summary>The entry for a class id, or <see langword="null"/> when the id is off the table.</summary>
    /// <param name="classId">The authored class id.</param>
    public AircraftClassEntry? Lookup(int classId) =>
        classId >= 0 && classId < EntryCount ? _entries[classId] : null;
}
