namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The placement that closes one authored object: what the file says, plus the position the port
/// could derive from it.
/// </summary>
/// <remarks>
/// <see cref="Resolved"/> is <see langword="null"/> exactly when the placement does not denote a point
/// the port can compute statically — <see cref="MissionPlacementKind.TrackActors"/> (which follows
/// live actors) and <see cref="MissionPlacementKind.EraMatrix"/> (dormant), plus any placement whose
/// chain depends on one of those.  Everything else resolves; see <see cref="MissionPosition"/> for what
/// "resolved" means when the chain is anchored on a random site pick.
/// </remarks>
/// <param name="Kind">The placement tag the file used.</param>
/// <param name="OffsetX">The authored X — an absolute coordinate for the absolute kinds, an offset otherwise.</param>
/// <param name="OffsetY">The authored Y (always 0 for <see cref="MissionPlacementKind.GroundPlane"/>).</param>
/// <param name="OffsetZ">The authored Z.</param>
/// <param name="SiteType">For <see cref="MissionPlacementKind.AtSite"/>: the section-1 site type to pick from.</param>
/// <param name="PlaceSlot">For <see cref="MissionPlacementKind.RelativeToPlace"/>: the actor slot the offsets are measured from.</param>
/// <param name="TrackedActorSlots">For <see cref="MissionPlacementKind.TrackActors"/>: the up-to-three slots, 0xFF meaning unused.</param>
/// <param name="Resolved">The derived position, or <see langword="null"/> when the placement is not a static point.</param>
public sealed record MissionPlacement(
    MissionPlacementKind Kind,
    int OffsetX,
    int OffsetY,
    int OffsetZ,
    int SiteType,
    int PlaceSlot,
    IReadOnlyList<int>? TrackedActorSlots,
    MissionPosition? Resolved)
{
    /// <summary>The <see cref="TrackedActorSlots"/> value meaning "no slot": <c>0xFF</c>.</summary>
    public const int UnusedTrackedSlot = 0xFF;

    /// <summary>True when the port derived a position for this placement.</summary>
    public bool IsResolved => Resolved is not null;

    /// <summary>
    /// The tracked slots that are actually in use (<see cref="UnusedTrackedSlot"/> filtered out).
    /// </summary>
    public IEnumerable<int> LiveTrackedActorSlots =>
        TrackedActorSlots?.Where(s => s != UnusedTrackedSlot) ?? [];
}
