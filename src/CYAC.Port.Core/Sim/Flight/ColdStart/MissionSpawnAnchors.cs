using CYAC.Port.Core.Model.Mission;

namespace CYAC.Port.Core.Sim.Flight.ColdStart;

/// <summary>
/// Resolves a container's <see cref="MissionPlacementKind.AtSite"/> anchors against a theater's site
/// table, turning an anchor-relative <see cref="MissionPosition"/> into world coordinates.
/// </summary>
/// <remarks>
/// <para>
/// The original draws each anchor at load time and the draw is RANDOM, so the port cannot compute
/// the answer — it can only apply one.  The position-tag-2 resolver inside
/// <c>wld_or_s_asset_parser</c> is:
/// </para>
/// <code>
/// image@0x09F82  count     := [0xB6AA + 2*type]        ; the by-type site count
/// image@0x09FB4  first     := [0xB6BC + 4*type]        ; the by-type first record (far)
/// image@0x09FD0  lcall prng_rand_bounded(count)        ; the RANDOM start index
/// image@0x09FD8  rec       := first + 0x0B*rand        ; section-1 records are 11 bytes
/// image@0x09FF8  while rec[+2] != 0: rec += 0x0B (wrapping at first+0x0B*count)
/// image@0x09FFF  rec[+2]   := 1                        ; mark the site visited
/// image@0x0A008  X := rec[+3] (i32) ; Y := 0 ; Z := rec[+7] (i32)
/// </code>
/// <para>
/// so the outcome is "the first UNVISITED site of the requested type, starting from a random one",
/// and a site is only ever visited once per load.  <see cref="Draws"/> therefore names, per anchor,
/// which site of that type the draw landed on — the port's replacement for the PRNG, and the knob a
/// host exposes as "which airfield do I start at".
/// </para>
/// <para>
/// Sites carry no Y: they live on the ground plane (<c>image@0x0A018</c> zeroes the Y pair), which is
/// why every <c>at_site</c> mission starts on the deck unless a later relative placement lifts it.
/// </para>
/// </remarks>
/// <param name="Theater">The <c>.W</c> catalog whose <see cref="MissionDefinition.Sites"/> the draws index.</param>
/// <param name="Draws">
/// One entry per anchor, in anchor order: the 0-based ordinal <b>within the anchor's site type</b>.
/// A missing entry means ordinal 0.
/// </param>
public sealed record MissionSpawnAnchors(MissionDefinition Theater, IReadOnlyList<int> Draws)
{
    /// <summary>The draw every anchor takes when a host does not choose: the first site of the type.</summary>
    public const int FirstSite = 0;

    /// <summary>An anchor set that takes <see cref="FirstSite"/> for every anchor.</summary>
    /// <param name="theater">The theater catalog.</param>
    public static MissionSpawnAnchors First(MissionDefinition theater) => new(theater, []);

    /// <summary>The sites of one type, in section-1 order.</summary>
    /// <param name="siteType">A <see cref="MissionSite.Type"/> value (2 = landing zone, 6 = engagement zone).</param>
    public IReadOnlyList<MissionSite> SitesOfType(int siteType) =>
        [.. Theater.Sites.Where(s => s.Type == siteType)];

    /// <summary>The site one anchor drew.</summary>
    /// <param name="anchorId">The anchor's ordinal, as <see cref="MissionPosition.AnchorId"/> reports it.</param>
    /// <param name="siteType">The anchor's site type.</param>
    /// <exception cref="InvalidOperationException">The theater has no site of that type.</exception>
    /// <remarks>A draw past the type's site count WRAPS, exactly as the resolver's own walk does.</remarks>
    public MissionSite Site(int anchorId, int siteType)
    {
        IReadOnlyList<MissionSite> sites = SitesOfType(siteType);
        if (sites.Count == 0)
        {
            throw new InvalidOperationException(
                $"{Theater.AssetName} has no site of type {siteType}; the anchor cannot be drawn " +
                "(the original aborts with internal error 0x2D at image@0x09F8F).");
        }

        int ordinal = anchorId >= 0 && anchorId < Draws.Count ? Draws[anchorId] : FirstSite;

        // The resolver WRAPS: prng_rand_bounded picks a start index and the walk that follows steps
        // forward modulo the by-type count (`while rec[+2]!= 0: rec += 0x0B`, wrapping at first +
        // 0x0B*count, image@0x09FF8).  A draw past the end is therefore not an error — it is the
        // same site the original would have reached — and a host that names one draw for a container
        // whose anchors have DIFFERENT types (ABB.S: a type-1 marker and a type-6 home base, with 24
        // and 3 sites) depends on it.
        int wrapped = ((ordinal % sites.Count) + sites.Count) % sites.Count;
        return sites[wrapped];
    }

    /// <summary>The world coordinates of an anchor-relative position.</summary>
    /// <param name="position">A resolved <see cref="MissionPosition"/>.</param>
    /// <returns>The same position with <see cref="MissionPosition.IsWorldAbsolute"/> true.</returns>
    public MissionPosition ToWorld(MissionPosition position)
    {
        if (position.IsWorldAbsolute)
        {
            return position;
        }

        MissionSite site = Site(position.AnchorId, position.AnchorSiteType);
        return MissionPosition.World(site.X + position.X, position.Y, site.Z + position.Z);
    }
}
