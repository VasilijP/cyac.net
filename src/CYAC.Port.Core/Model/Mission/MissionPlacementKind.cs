namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// How an object states where it is.  Exactly one placement closes every object in the tag stream;
/// the engine's cascade is <c>image@0x09D70..0x09F5D</c>.
/// </summary>
/// <remarks>
/// Shipped census over the 54 containers: 2378 <see cref="GroundPlane"/> (all in the three <c>.W</c>),
/// 308 <see cref="RelativeToPlace"/>, 98 <see cref="RelativeToPrevious"/>, 82 <see cref="AtSite"/>, 25
/// <see cref="TrackActors"/>, 18 <see cref="Absolute"/>, 0 <see cref="EraMatrix"/>.
/// </remarks>
public enum MissionPlacementKind
{
    /// <summary>Tag 0 — absolute world x, y, z.</summary>
    Absolute = 0,

    /// <summary>Tag 1 — absolute x and z with y forced to 0 (<c>image@0x09DA9</c>).  The <c>.W</c> idiom.</summary>
    GroundPlane = 1,

    /// <summary>
    /// Tag 2 — at a <b>randomly chosen unvisited</b> section-1 site of the given type (prng
    /// @<c>image@0x09FD0</c>, visited flag at slot <c>+2</c> @<c>image@0x09FFF</c>).  The picked site's
    /// subtype becomes the object's place-type byte.  This is what makes a mission's absolute
    /// coordinates a run-time property.
    /// </summary>
    AtSite = 2,

    /// <summary>Tag 3 — the <c>[3 eras][subtype_max]</c> coordinate matrix.  Dormant: no shipped use.</summary>
    EraMatrix = 3,

    /// <summary>
    /// Tag 4 — offsets from the <b>previous object's</b> final position (<c>[0xB556]</c>, written back
    /// for every object at <c>image@0x0A2FD..0x0A30B</c>).
    /// </summary>
    RelativeToPrevious = 4,

    /// <summary>Tag 5 — offsets from the mission spawn origin <c>[0xEE34]</c> (the class-0 player position).</summary>
    RelativeToSpawnOrigin = 5,

    /// <summary>
    /// Tag 6 — offsets from an earlier object's registered actor slot (<c>[0xEE74 + 12k]</c>).  The
    /// dominant <c>.S</c> placement, and the corpus contains zero forward references.
    /// </summary>
    RelativeToPlace = 6,

    /// <summary>
    /// Tag 7 — not a point: up to three actor-slot indexes (0xFF = unused) that a nav waypoint
    /// <b>tracks</b>, resolved live by <c>slot_record_get_pos @0x08E0D</c> (first live slot wins).
    /// </summary>
    TrackActors = 7,
}
