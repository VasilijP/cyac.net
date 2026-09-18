using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Render.Map;

/// <summary>What one moving thing on the map IS, which decides its glyph and its colour.</summary>
/// <remarks>
/// Every kind except <see cref="Player"/> is a PORT ADDITION: the 1991 map draws the courses, the waypoints
/// and the player's own heading cross and nothing else.  The two SIDE colours are the
/// game's own identification key all the same — see <see cref="MapLook"/>.
/// </remarks>
public enum MapActorKind
{
    /// <summary>The player's own aeroplane — the original's blinking heading cross.</summary>
    Player = 0,

    /// <summary>Another aircraft on the player's side (<c>WorldObjectFlags.Hostile</c> clear).</summary>
    Friendly = 1,

    /// <summary>A hostile aircraft (flag word bit 10).</summary>
    Hostile = 2,

    /// <summary>A crater or a burnt-out wreck — a dead object still in the world.</summary>
    Wreck = 3,

    /// <summary>Anything else alive in the pool the map bothers to plot (a bailed-out pilot, a truck).</summary>
    Other = 4,
}

/// <summary>One plotted object: where it is, which way it points and what it is.</summary>
/// <param name="X">World X, world units (feet).</param>
/// <param name="Z">World Z.</param>
/// <param name="HeadingDegrees">
/// Its heading in the engine's own convention — 0 points at +Z and the angle turns toward −X
/// (<c>SceneInstance.HeadingDegrees</c>, established by <c>angle_mat3_pair_build @image@0x1BFA4</c>).
/// </param>
/// <param name="Kind">What it is.</param>
/// <param name="Label">An optional name drawn beside it, or null.</param>
public readonly record struct MapActor(
    double X, double Z, double HeadingDegrees, MapActorKind Kind, string? Label = null);

/// <summary>One nav waypoint as the map draws it.</summary>
/// <param name="Slot">
/// Its slot, 0-based; the original prints <c>slot + 1</c> (<c>image@0x1EA24</c>: <c>al = [bp−2] + 0x31</c>).
/// </param>
/// <param name="Name">Its label, from the <c>.S</c> record (<c>NavWaypointPlacement.Label</c>).</param>
/// <param name="X">Where it is THIS frame, world units — a tracking waypoint follows its actors.</param>
/// <param name="Z">Ditto.</param>
/// <param name="Current">
/// Whether it is the slot <c>g_nav_slot_current_index [0xEF92]</c> names, i.e. the one the compass points
/// at.  The original's map draws every waypoint identically; the highlight is a port addition (deviation
/// D4).
/// </param>
/// <param name="Tracks">Whether the slot tracks live actors rather than a fixed point.</param>
public readonly record struct MapWaypoint(
    int Slot, string Name, double X, double Z, bool Current, bool Tracks);

/// <summary>
/// Everything the map draws, as the SIM's own read-only view of the world.
/// </summary>
/// <remarks>
/// The host builds it; the renderer only reads it ("a renderer must never write back into sim
/// state").  It is deliberately a small value bag rather than a window onto the session: the map
/// must be drawable from a test with three hand-written instances.
/// </remarks>
public sealed class MapScene
{
    /// <summary>Creates a scene.</summary>
    /// <param name="statics">The theatre's static scenery — <c>WorldScene.Statics</c>.</param>
    /// <param name="actors">This frame's plotted objects, the player among them.</param>
    /// <param name="waypoints">The sortie's registered nav waypoints, in slot order.</param>
    public MapScene(
        IReadOnlyList<SceneInstance> statics,
        IReadOnlyList<MapActor> actors,
        IReadOnlyList<MapWaypoint> waypoints)
    {
        ArgumentNullException.ThrowIfNull(statics);
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(waypoints);
        Statics = statics;
        Actors = actors;
        Waypoints = waypoints;
    }

    /// <summary>The theatre's scenery placements.</summary>
    public IReadOnlyList<SceneInstance> Statics { get; }

    /// <summary>The moving objects.</summary>
    public IReadOnlyList<MapActor> Actors { get; }

    /// <summary>The nav waypoints.</summary>
    public IReadOnlyList<MapWaypoint> Waypoints { get; }

    /// <summary>The player's world X, for centring and for the range readouts.</summary>
    public double PlayerX { get; init; }

    /// <summary>The player's world Z.</summary>
    public double PlayerZ { get; init; }

    /// <summary>
    /// The 2-bit blink phase <c>g_map_symbol_blink_phase [0xBA14]</c>: the player's cross is drawn
    /// when it is non-zero, so the symbol blinks at a quarter of the frame rate.
    /// </summary>
    /// <remarks>
    /// <c>scene_frame_setup_or_redraw</c> increments the byte and passes <c>&amp; 3</c> as the
    /// cross-draw gate (<c>image@0x1E722..0x1E729</c>), and
    /// <c>briefing_map_mesh_and_symbol_draw</c> returns early when it is zero
    /// (<c>image@0x1E879</c>).
    /// </remarks>
    public int BlinkPhase { get; init; } = 1;

    /// <summary>The theatre's name, for the map's own caption.</summary>
    public string TheatreName { get; init; } = string.Empty;

    /// <summary>The mission's name, or empty for a Test Flight.</summary>
    public string MissionName { get; init; } = string.Empty;

    /// <summary>
    /// The world rectangle the FIT frames: the mission's own objects and waypoints, else the
    /// player's neighbourhood.  <c>(minX, minZ, maxX, maxZ)</c>.
    /// </summary>
    /// <remarks>
    /// The host computes it because it knows which objects are the MISSION's — the editor's
    /// <c>FitToContent(missionOnly)</c> distinction.  An empty rectangle (min ≥ max) makes
    /// <see cref="MapView"/> fall back to the player at the current zoom.
    /// </remarks>
    public (double MinX, double MinZ, double MaxX, double MaxZ) FitBounds { get; init; }
}
