namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// What a <c>.S</c>/<c>.W</c> object opener declares.  The opener tag is the first byte of an object
/// in the tag stream and the engine dispatches on it at <c>image@0x099CC..</c>.
/// </summary>
/// <remarks>
/// Names and semantics: (the opener census) and <c>CYAC.Formats.EaLib.SDataModel</c>'s authoring
/// vocabulary, which this enum mirrors one-for-one. Shipped use across the 54 containers: 2748
/// <see cref="ClassInstance"/>, 81 <see cref="NavWaypoint"/>, 67 <see cref="Marker"/>, 12
/// <see cref="Airport"/>, 1 <see cref="GroundEffect"/>, 0 <see cref="NamedMesh"/>.
/// </remarks>
public enum MissionObjectKind
{
    /// <summary>
    /// Any class-table opener (byte 0, 1 or 6..45): an aircraft, a vehicle, the player, the home-base
    /// reference, or — in a <c>.W</c> — a scenery prototype.  See <see cref="MissionObject.ClassId"/>.
    /// </summary>
    ClassInstance,

    /// <summary>Opener <c>0x02</c> — a pure place definition; prim 0, so nothing spawns.</summary>
    Marker,

    /// <summary>Opener <c>0x03</c> — a labelled named mesh (kind 0xFFFD).  Dormant: no shipped use.</summary>
    NamedMesh,

    /// <summary>
    /// Opener <c>0x04</c> — a cockpit nav waypoint.  Its lead byte is the slot index into
    /// <c>g_nav_slot_record_array [0xB564]</c> (stride 0x2C) and its label is the name the nav/briefing
    /// map shows (<c>nav_slot_record_register @0x08CE4</c>, P15 closure c).
    /// </summary>
    NavWaypoint,

    /// <summary>Opener <c>0x05</c> — kind 0xFFFB, a ground effect spawned via <c>subsystem4x19_row_attach (ex-projectile_spawn)</c>.</summary>
    GroundEffect,

    /// <summary>
    /// Opener <c>0x19</c> — the hardcoded prim <c>0x4D00</c>, which is the same descriptor class id 26
    /// points at: an airport (<c>SDataModel.Prim4D00Name</c>).
    /// </summary>
    Airport,
}
