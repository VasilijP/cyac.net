using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// What a class id in a mission file denotes: the two engine pseudo-classes, the 19 aircraft/vehicle
/// classes and the 19 scenery prototypes — and, where the repo documents the link, the mesh class
/// record that draws it.
/// </summary>
/// <remarks>
/// <para>
/// The ids index the 46-entry class table @<c>image@0x34F90</c> (3-byte records
/// <c>{u8 flag, u16 value}</c>), read by <c>aircraft_class_table_lookup @0x24058</c>.  Flag-0 entries
/// (ids 6..24) point at a 46-byte aircraft stat block whose <c>+0x04</c> names the class; flag-1
/// entries (ids 26..45, minus the flag-2 hole at 35) point at a MESH-REGISTRY descriptor, which is why
/// the scenery names below <i>are</i> mesh basenames (derived from the image, and
/// <c>MissionVocabulary</c> carries the same table, read from <c>missions/_vocabulary.json</c>).
/// </para>
/// <para>
/// <b>Mesh resolution is deliberately partial.</b> <see cref="MeshClass(int)"/> answers for the scenery
/// ids (their names are descriptor names) and for the six flyable aircraft, where the chain is documented
/// end to end: the flyable table <c>g_flyable_statblock_ptr_table</c> (DGROUP <c>0x0FBC</c>) pins aircraft
/// index ↔ class id (0 → 22, …) that name table left the decoder; the transform reads the names through
/// the flyable table, and <c>AircraftDefinition.FlyableBasenames</c> pins the same index ↔ the asset
/// basename the class record carries.  For the other 13 aircraft/vehicle ids the repo has no cited
/// class-id → mesh-descriptor link, so this type answers <see langword="null"/> rather than guess — even
/// where a same-looking name exists in <see cref="ClassRegistry"/> (reported as an open item).
/// </para>
/// </remarks>
public static class MissionClassCatalog
{
    /// <summary>Class id 0 — the PLAYER pseudo-class (table record <c>{0,0xFFFF}</c>, placement kind 0xFFFF).</summary>
    public const int PlayerClassId = 0;

    /// <summary>Class id 1 — the HOME-BASE-POS pseudo-class (<c>{0,0xFFFE}</c>) read by the made-it-home check.</summary>
    public const int HomeBasePositionClassId = 1;

    /// <summary>The lowest class id that spawns a real object: 6.</summary>
    public const int FirstSpawningClassId = 6;

    /// <summary>
    /// Class id → mesh basename, for the six flyable aircraft only.  Derived from the two documented
    /// tables that share the aircraft index (see the type remarks).
    /// </summary>
    private static readonly Dictionary<int, string> FlyableMeshBasenames = new()
    {
        [22] = "p51",     // aircraft index 0 — "P-51D",    statblock 0x1734
        [12] = "fw190",   // aircraft index 1 — "FW-190A",  statblock 0x1812
        [10] = "f86",     // aircraft index 2 — "F-86E",    statblock 0x1D1E
        [18] = "mig15",   // aircraft index 3 — "MiG-15",   statblock 0x1F60
        [9] = "f4",       // aircraft index 4 — "F-4E",     statblock 0x2104
        [20] = "mig21",   // aircraft index 5 — "MiG-21MF", statblock 0x2470
    };

    /// <summary>The display name of a class id, or <see langword="null"/> when the id is not a class.</summary>
    /// <param name="classId">A class-table id.</param>
    public static string? DisplayName(int classId)
    {
        if (MissionVocabulary.AircraftClassNames.TryGetValue(classId, out string? aircraft))
        {
            return aircraft;
        }

        return MissionVocabulary.SceneryClassNames.TryGetValue(classId, out string? scenery) ? scenery : null;
    }

    /// <summary>
    /// The mesh basename a class id draws with, when the repo documents the link; otherwise
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="classId">A class-table id.</param>
    public static string? MeshBasename(int classId)
    {
        if (FlyableMeshBasenames.TryGetValue(classId, out string? flyable))
        {
            return flyable;
        }

        return MissionVocabulary.SceneryClassNames.TryGetValue(classId, out string? scenery) ? scenery : null;
    }

    /// <summary>
    /// The class record from <see cref="ClassRegistry"/> for a class id, or <see langword="null"/> when
    /// the link is undocumented or the mesh is outside the class census.
    /// </summary>
    /// <param name="classId">A class-table id.</param>
    public static ClassRecord? MeshClass(int classId)
    {
        string? basename = MeshBasename(classId);
        return basename is null ? null : ClassRegistry.Find(basename);
    }

    /// <summary>True for the aircraft/vehicle ids a <c>.S</c> mission may place: 6..24.</summary>
    /// <param name="classId">A class-table id.</param>
    public static bool IsAircraftClass(int classId) =>
        classId >= FirstSpawningClassId && MissionVocabulary.AircraftClassNames.ContainsKey(classId);

    /// <summary>True for the scenery prototype ids only the <c>.W</c> catalogs place: 26..45 (no 35).</summary>
    /// <param name="classId">A class-table id.</param>
    public static bool IsSceneryClass(int classId) =>
        MissionVocabulary.SceneryClassNames.ContainsKey(classId);

    /// <summary>The aircraft/vehicle class ids, ascending: 6..24.</summary>
    public static IReadOnlyList<int> AircraftClassIds =>
        [.. MissionVocabulary.AircraftClassNames.Keys.Where(id => id >= FirstSpawningClassId).Order()];

    /// <summary>The scenery prototype class ids, ascending.</summary>
    public static IReadOnlyList<int> SceneryClassIds =>
        [.. MissionVocabulary.SceneryClassNames.Keys.Order()];
}
