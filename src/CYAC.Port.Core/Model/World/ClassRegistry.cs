using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Model.World;

/// <summary>
/// Every world-object class the shipping game defines, as loaded from the transformed data tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the data is.</b> The 23 records live in the original's DGROUP (base
/// <c>image@0x3BD60</c>) and are extracted into <c>exe/classes.json</c> by <c>cyac-transform</c>;
/// <see cref="Load"/> installs them.  That was a law-L1 violation: no original game data may live in
/// shipped source.  The knowledge — the addresses, the invariants and the two defects below — stays
/// here; the numbers moved to the data tree, where the transform proves them byte-exact against the
/// image region.
/// </para>
/// <para>
/// <b>Provenance of the extraction.</b> Source of truth, in the project's precedence order: the
/// <b>bytes</b> of the layer-1 image, cross-checked field for field against and the
/// <c>g_class_record_*</c> entries in KNOWN_GLOBALS</c>.  The census's own columns
/// (descriptor near pointer, kind/count, prepare/draw callbacks, filter word, <c>+0x0E</c>)
/// reproduce exactly, 22/22.
/// </para>
/// <para>
/// <b>Two report-only findings</b> are facts about this data and are pinned by
/// <c>ClassRegistryTests</c> rather than smoothed over:
/// </para>
/// <list type="number">
/// <item><description><c>mount2</c> and <c>mountain</c> put their render descriptor at
/// <c>record+0x60</c>, not <c>+0x50</c> — so's
/// "render descriptor ALWAYS at record+0x50" (and law 6) is wrong for 2 of 22.  The port carries the
/// offset as a per-record fact (<see cref="ClassRecord.RenderDescriptorOffsetInRecord"/>), never as
/// a special case in code.</description></item>
/// <item><description>The census predicate <c>byte[+0] == 0x80</c> is a <i>filter</i>, not a
/// structural marker: <c>+0x00</c> is the render-layer priority byte, and at least one further class
/// record with the identical layout — <c>crater</c> <c>[0x52A2]</c>, priority <c>0x1E</c> — falls
/// outside it.
/// owner of the <c>0x0214</c> filter word.  It is kept in <see cref="BeyondCensus"/>, not in
/// <see cref="All"/>, so the census count stays auditable.</description></item>
/// </list>
/// <para>
/// All of it is INT-only, immutable, authored content.
/// </para>
/// </remarks>
public static class ClassRegistry
{
    /// <summary>The data tree path the records are transformed to: <c>exe/classes.json</c>.</summary>
    public const string DataPath = "exe/classes.json";

    /// <summary>
    /// <c>mesh_lod_prepare_gear_and_flame_state @image@0x2D8E9</c> — the prepare callback the six
    /// jet/piston fighter classes share.
    /// </summary>
    public const int MeshLodPrepareGearAndFlameState = 0x2D8E9;

    /// <summary>The number of class records the class census enumerated: 22.</summary>
    public const int CensusCount = 22;

    private static IReadOnlyList<ClassRecord>? _all;
    private static IReadOnlyList<ClassRecord>? _beyondCensus;
    private static IReadOnlyList<ClassRecord>? _everything;

    /// <summary>Whether <see cref="Load"/> has supplied the records.</summary>
    public static bool IsLoaded => _everything is not null;

    /// <summary>The 22 classes the class census enumerated, in DGROUP order.</summary>
    /// <exception cref="InvalidOperationException">The registry has not been loaded.</exception>
    public static IReadOnlyList<ClassRecord> All => _all ?? throw NotLoaded();

    /// <summary>
    /// Class records with the same layout that the class-census predicate did not surface — see the
    /// type remarks.  The shipping game has exactly one: <c>crater</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The registry has not been loaded.</exception>
    public static IReadOnlyList<ClassRecord> BeyondCensus => _beyondCensus ?? throw NotLoaded();

    /// <summary>Every class record the extraction found, census and beyond.</summary>
    /// <exception cref="InvalidOperationException">The registry has not been loaded.</exception>
    public static IReadOnlyList<ClassRecord> Everything => _everything ?? throw NotLoaded();

    /// <summary>Class <c>bullet</c> — record <c>[0x4FC0]</c> (<c>image@0x40D20</c>).</summary>
    public static ClassRecord Bullet => Get("bullet");

    /// <summary>Class <c>chaff</c> — record <c>[0x5080]</c> (<c>image@0x40DE0</c>).</summary>
    public static ClassRecord Chaff => Get("chaff");

    /// <summary>Class <c>cloud</c> — record <c>[0x51F6]</c> (<c>image@0x40F56</c>).</summary>
    public static ClassRecord Cloud => Get("cloud");

    /// <summary>Class <c>eject1</c> — record <c>[0x5334]</c> (<c>image@0x41094</c>).</summary>
    public static ClassRecord Eject1 => Get("eject1");

    /// <summary>Class <c>eject4</c> — record <c>[0x5618]</c> (<c>image@0x41378</c>).</summary>
    public static ClassRecord Eject4 => Get("eject4");

    /// <summary>Class <c>explosio</c> — record <c>[0x59EE]</c> (<c>image@0x4174E</c>); the shipped 8-character truncation of "explosion".</summary>
    public static ClassRecord Explosion => Get("explosio");

    /// <summary>Class <c>f4</c> — record <c>[0x5AB2]</c> (<c>image@0x41812</c>).</summary>
    public static ClassRecord F4 => Get("f4");

    /// <summary>Class <c>f86</c> — record <c>[0x61C0]</c> (<c>image@0x41F20</c>).</summary>
    public static ClassRecord F86 => Get("f86");

    /// <summary>Class <c>flare</c> — record <c>[0x6B34]</c> (<c>image@0x42894</c>).</summary>
    public static ClassRecord Flare => Get("flare");

    /// <summary>Class <c>fw190</c> — record <c>[0x6BBE]</c> (<c>image@0x4291E</c>).</summary>
    public static ClassRecord Fw190 => Get("fw190");

    /// <summary>Class <c>hell</c> — record <c>[0x74AE]</c> (<c>image@0x4320E</c>); the guided-weapon projectile mesh.</summary>
    public static ClassRecord Hell => Get("hell");

    /// <summary>Class <c>hells</c> — record <c>[0x765C]</c> (<c>image@0x433BC</c>).</summary>
    public static ClassRecord Hells => Get("hells");

    /// <summary>Class <c>l5sh</c> — record <c>[0x773C]</c> (<c>image@0x4349C</c>).</summary>
    public static ClassRecord L5sh => Get("l5sh");

    /// <summary>Class <c>me109</c> — record <c>[0x785A]</c> (<c>image@0x435BA</c>).</summary>
    public static ClassRecord Me109 => Get("me109");

    /// <summary>Class <c>mig15</c> — record <c>[0x801E]</c> (<c>image@0x43D7E</c>).</summary>
    public static ClassRecord Mig15 => Get("mig15");

    /// <summary>Class <c>mig21</c> — record <c>[0x8770]</c> (<c>image@0x444D0</c>).</summary>
    public static ClassRecord Mig21 => Get("mig21");

    /// <summary>Class <c>mount2</c> — record <c>[0x8E22]</c> (<c>image@0x44B82</c>); descriptor at <c>record+0x60</c>.</summary>
    public static ClassRecord Mount2 => Get("mount2");

    /// <summary>Class <c>mountain</c> — record <c>[0x8EF6]</c> (<c>image@0x44C56</c>); descriptor at <c>record+0x60</c>.</summary>
    public static ClassRecord Mountain => Get("mountain");

    /// <summary>Class <c>p51</c> — record <c>[0x8FFC]</c> (<c>image@0x44D5C</c>).</summary>
    public static ClassRecord P51 => Get("p51");

    /// <summary>Class <c>smoke</c> — record <c>[0x9AFA]</c> (<c>image@0x4585A</c>).</summary>
    public static ClassRecord Smoke => Get("smoke");

    /// <summary>Class <c>trees</c> — record <c>[0xA00C]</c> (<c>image@0x45D6C</c>).</summary>
    public static ClassRecord Trees => Get("trees");

    /// <summary>Class <c>b17</c> — record <c>[0xA552]</c> (<c>image@0x462B2</c>).</summary>
    public static ClassRecord B17 => Get("b17");

    /// <summary>Class <c>crater</c> — record <c>[0x52A2]</c> (<c>image@0x41002</c>); outside the census predicate.</summary>
    public static ClassRecord Crater => Get("crater");

    /// <summary>
    /// Installs the class records from the transformed <c>exe/classes.json</c> document.
    /// </summary>
    /// <param name="classes">The document's class list, in the order the transform wrote it.</param>
    /// <exception cref="InvalidDataException">A record is malformed or the set is not the shipped one.</exception>
    public static void Load(IReadOnlyList<ClassRecordDto> classes)
    {
        ArgumentNullException.ThrowIfNull(classes);
        if (classes.Count == 0)
        {
            throw new InvalidDataException($"{DataPath}: the class list is empty");
        }

        List<ClassRecord> census = new List<ClassRecord>();
        List<ClassRecord> beyond = new List<ClassRecord>();
        foreach (ClassRecordDto dto in classes)
        {
            ClassRecord record = ToRecord(dto);
            (dto.CensusMember ? census : beyond).Add(record);
        }

        if (census.Count != CensusCount)
        {
            throw new InvalidDataException(
                $"{DataPath}: the class census enumerated {CensusCount} class records, this " +
                $"document marks {census.Count} of {classes.Count} as census members");
        }

        _all = census;
        _beyondCensus = beyond;
        _everything = [.. census, .. beyond];
    }

    /// <summary>Forgets the loaded records — for tests that need the not-loaded behaviour.</summary>
    public static void Unload()
    {
        _all = null;
        _beyondCensus = null;
        _everything = null;
    }

    /// <summary>Looks a class up by its original lowercase name (ordinal), across <see cref="Everything"/>.</summary>
    /// <param name="name">The class basename, e.g. <c>"mig21"</c>.</param>
    public static ClassRecord? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (ClassRecord record in Everything)
        {
            if (string.Equals(record.Name, name, StringComparison.Ordinal))
            {
                return record;
            }
        }

        return null;
    }

    /// <summary>Gets a class by its original lowercase name; throws when there is no such class.</summary>
    /// <param name="name">The class basename, e.g. <c>"mig21"</c>.</param>
    /// <exception cref="KeyNotFoundException">No class record carries that name.</exception>
    public static ClassRecord Get(string name) =>
        Find(name) ?? throw new KeyNotFoundException($"no world-object class named '{name}'.");

    private static ClassRecord ToRecord(ClassRecordDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (dto.LodThresholds is not { Count: 3 } thresholds)
        {
            throw new InvalidDataException(
                $"{DataPath}: class '{dto.Name}' must carry three LOD thresholds");
        }

        ClassBoundsDto bounds = dto.Bounds
                                ?? throw new InvalidDataException($"{DataPath}: class '{dto.Name}' has no bounds");
        ClassRenderDescriptorDto descriptor = dto.RenderDescriptor
                                              ?? throw new InvalidDataException($"{DataPath}: class '{dto.Name}' has no render descriptor");

        return new ClassRecord
        {
            Name = dto.Name ?? throw new InvalidDataException($"{DataPath}: a class record has no name"),
            DgroupOffset = PortHex.Parse(dto.Dgroup),
            ImageOffset = PortHex.Parse(dto.Image),
            RenderLayerPriority = (byte)PortHex.Parse(dto.RenderLayerPriority),
            Flags = (MeshClassFlags)(byte)PortHex.Parse(dto.Flags),
            MeshExtent = (ushort)dto.MeshExtent,
            RawExtent = dto.RawExtent,
            ScaleShiftExponent = (sbyte)dto.ScaleShiftExponent,
            TargetPanelCameraDistanceSteps = PortHex.ParseOrDefault(dto.Unknown0x0D),
            LodThreshold0 = (ushort)thresholds[0],
            LodThreshold1 = (ushort)thresholds[1],
            LodThreshold2 = (ushort)thresholds[2],
            LodFaceDescriptor1Offset = (ushort)PortHex.ParseOrDefault(dto.LodFaceDescriptor1),
            LodFaceDescriptor2Offset = (ushort)PortHex.ParseOrDefault(dto.LodFaceDescriptor2),
            VertexCount = (ushort)dto.VertexCount,
            GroundClearance = (ushort)dto.GroundClearance,
            PoolFilterWord = (WorldObjectFlags)(ushort)PortHex.ParseOrDefault(dto.PoolFilterWord),
            BoundsMinX = bounds.MinX,
            BoundsMaxX = bounds.MaxX,
            BoundsMinY = bounds.MinY,
            BoundsMaxY = bounds.MaxY,
            BoundsMinZ = bounds.MinZ,
            BoundsMaxZ = bounds.MaxZ,
            SecondaryFaceDescriptorOffset = (ushort)PortHex.ParseOrDefault(dto.SecondaryFaceDescriptor),
            RenderDescriptorOffsetInRecord = PortHex.Parse(descriptor.OffsetInRecord),
            RenderDescriptor = new ClassRenderDescriptor(
                dgroupOffset: PortHex.Parse(descriptor.Dgroup),
                kind: (byte)PortHex.Parse(descriptor.Kind),
                elementCount: (byte)descriptor.ElementCount,
                prepareCallbackImageOffset: PortHex.ParseOrDefault(descriptor.PrepareCallback?.ImageOffset),
                drawCallbackImageOffset: PortHex.ParseOrDefault(descriptor.DrawCallback?.ImageOffset),
                elementListOffset: (ushort)PortHex.ParseOrDefault(descriptor.ElementList),
                flags: (ushort)PortHex.ParseOrDefault(descriptor.Flags)),
        };
    }

    private static InvalidOperationException NotLoaded() =>
        new($"the world-object class registry has not been loaded. Call {nameof(ClassRegistry)}." +
            $"{nameof(Load)} with '{DataPath}' from the transformed data tree (run " +
            "`cyac-transform <originals> <data>` to produce one).");
}
