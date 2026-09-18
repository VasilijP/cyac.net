namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// One section-1 site record of a theater catalog: a named spot on the map that missions anchor to.
/// </summary>
/// <remarks>
/// <para>
/// Only the three <c>.W</c> theater catalogs carry sites (94 of them: GERMANY 31 / KOREA 34 / VIETNAM 29); a
/// <c>.S</c> mission has none and refers to them by <b>type</b> through <see cref="MissionPlacementKind.AtSite"/>,
/// which the engine resolves to a random unvisited site of that type.  Type/subtype semantics.
/// </para>
/// <para>
/// Types 2 and 6 additionally register through <c>landing_zone_entry_add @0x090AE</c> — flags 0x40 at
/// <c>image@0x098C9</c> for type 2, flags 0 at <c>image@0x098AA</c> for type 6.
/// </para>
/// </remarks>
/// <param name="Index">The record's position in section 1.</param>
/// <param name="Type">The site type: 1 generic, 2 landing zone, 6 engagement zone (shipped values).</param>
/// <param name="Subtype">The subtype byte (types 1..7 carry one); becomes the place-type of an object placed here.</param>
/// <param name="X">World-unit X.</param>
/// <param name="Z">World-unit Z (sites live on the ground plane).</param>
public sealed record MissionSite(int Index, int Type, int? Subtype, int X, int Z)
{
    /// <summary>Type 1 — a generic site (subtype 0); 73 of the 94 shipped sites.</summary>
    public const int GenericSiteType = 1;

    /// <summary>Type 2 — a landing zone (subtypes 4..8); 12 shipped.</summary>
    public const int LandingZoneType = 2;

    /// <summary>Type 6 — an engagement zone (subtypes 1..3); 9 shipped.</summary>
    public const int EngagementZoneType = 6;

    /// <summary>True for a generic anchor site.</summary>
    public bool IsGeneric => Type == GenericSiteType;

    /// <summary>True for a landing zone.</summary>
    public bool IsLandingZone => Type == LandingZoneType;

    /// <summary>True for an engagement zone.</summary>
    public bool IsEngagementZone => Type == EngagementZoneType;
}
