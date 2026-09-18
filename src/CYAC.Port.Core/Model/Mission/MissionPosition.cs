namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// A resolved mission coordinate: world units, stated against the anchor the placement chain bottoms
/// out on.
/// </summary>
/// <remarks>
/// <para>
/// A <c>.S</c> mission does not author absolute coordinates for its engagement — it anchors on a
/// <see cref="MissionPlacementKind.AtSite"/> pick, which the engine makes at load time by choosing a
/// random unvisited theater site of the requested type.  Every relative placement downstream of that
/// pick is therefore exactly determined <i>relative to the anchor</i> and undetermined in absolute
/// world terms until the mission loads.  This type keeps both facts: <see cref="AnchorId"/> says
/// which anchor, <see cref="X"/>/<see cref="Y"/>/<see cref="Z"/> say where relative to it.
/// </para>
/// <para>
/// <see cref="AnchorId"/> = <see cref="WorldAnchor"/> means the chain reached a
/// <see cref="MissionPlacementKind.Absolute"/> or <see cref="MissionPlacementKind.GroundPlane"/> root,
/// so the coordinates are absolute world units.  Otherwise it is the ordinal of the
/// <c>at_site</c> object in the container's stream — separate picks are separate anchors, because the
/// engine draws each one independently.
/// </para>
/// <para>
/// Units are the stream's world units: the i24 the file stores is <c>value &lt;&lt; 8</c>
/// (<c>read_3bytes @image@0x0A403</c>), and the decoder has already shifted it down.  Y is up, as in
/// <c>WorldObject</c>.
/// </para>
/// </remarks>
/// <param name="AnchorId">
/// <see cref="WorldAnchor"/> for world-absolute, else the 0-based ordinal of the <c>at_site</c> pick
/// this position hangs off.
/// </param>
/// <param name="AnchorSiteType">The section-1 site type of that pick, or 0 when world-absolute.</param>
/// <param name="X">World-unit X, relative to the anchor.</param>
/// <param name="Y">World-unit Y (up), relative to the anchor.</param>
/// <param name="Z">World-unit Z, relative to the anchor.</param>
public readonly record struct MissionPosition(int AnchorId, int AnchorSiteType, int X, int Y, int Z)
{
    /// <summary>The <see cref="AnchorId"/> value meaning "absolute world coordinates": −1.</summary>
    public const int WorldAnchor = -1;

    /// <summary>Builds a world-absolute position.</summary>
    /// <param name="x">World X.</param>
    /// <param name="y">World Y.</param>
    /// <param name="z">World Z.</param>
    public static MissionPosition World(int x, int y, int z) => new(WorldAnchor, 0, x, y, z);

    /// <summary>True when the coordinates are absolute world units rather than anchor-relative.</summary>
    public bool IsWorldAbsolute => AnchorId == WorldAnchor;

    /// <summary>This position shifted by an offset, keeping the anchor.</summary>
    /// <param name="dx">Offset X.</param>
    /// <param name="dy">Offset Y.</param>
    /// <param name="dz">Offset Z.</param>
    public MissionPosition Offset(int dx, int dy, int dz) =>
        this with { X = X + dx, Y = Y + dy, Z = Z + dz };
}
