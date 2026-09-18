using System.Buffers.Binary;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Sim.Flight.ColdStart;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// The landing-zone table the flight kernel's one WORLD query reads — the answer
/// <see cref="IKernelWorld.IsWithinLandingZone"/> owes, computed from the theater's own site table
/// instead of stubbed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the entries come from.</b>  <c>wld_or_s_asset_parser</c>'s section-1 loop calls
/// <c>landing_zone_entry_add @image@0x090AE</c> for every record of type 2
/// (<c>image@0x098C9</c>, flags <c>0x40</c> = ACTIVE) and type 6 (<c>image@0x098AA</c>, flags 0), in
/// file order, up to the table's nine slots.  Those are exactly
/// <see cref="MissionSite.LandingZoneType"/> and <see cref="MissionSite.EngagementZoneType"/>, so a
/// theater's <see cref="MissionDefinition.Sites"/> IS the table — GERMANY.W's seven of them are the
/// <c>g_landing_zone_table_count [0xB614] = 7</c> every Test Flight trace record carries.
/// </para>
/// <para>
/// <b>The predicate.</b>  <c>crash_secondary_check @image@0x09255</c> asks
/// <c>landing_zone_proximity_scan @image@0x0919F</c> for the minimum distance over all entries
/// (both filter flags 1, <c>image@0x09267..0x0926A</c>) and answers <c>AL = 1</c> iff that minimum
/// is <b>not greater than zero</b> (<c>cmp word [bp-2],0 ; jg</c> @<c>image@0x09272</c>) — i.e. iff
/// the player is INSIDE a zone.  The distance is Chebyshev, not Euclidean, and it is computed on the
/// HIGH WORDS only (<c>landing_zone_bbox_distance @image@0x08FAF</c>):
/// </para>
/// <code>
/// thr  = hi16(1.5 × g_landing_zone_capture_radius [0x9E74])      image@0x08FB7..0x08FCA
/// dx   = max(0, |hi16(player.X) − hi16(zone.X)| − thr)           image@0x08FCC..0x08FE3
/// dz   = max(0, |hi16(player.Z) − hi16(zone.Z)| − thr)           image@0x08FE5..0x09000
/// return dx + dz                                                 image@0x09000
/// </code>
/// <para>
/// The scan walks the table BACKWARD and keeps the incumbent on a tie
/// (<c>jge</c> @<c>image@0x091FA</c>), so the lowest-indexed zone wins — irrelevant to this type,
/// which only reports whether any zone contains the point.
/// </para>
/// </remarks>
public sealed class LandingZoneTable
{
    /// <summary>Slots the original's table has: 9 (<c>0x90</c> bytes of stride <c>0x10</c>).</summary>
    /// <remarks><c>landing_zone_table_clear @image@0x0900D</c> zeroes exactly that many.</remarks>
    public const int MaxEntries = 9;

    /// <summary>Where the capture radius lives in DGROUP: <c>[0x9E74]</c>, an i32.</summary>
    public const int CaptureRadiusDgroupOffset = 0x9E74;

    private readonly (int X, int Z)[] _zones;

    private LandingZoneTable((int X, int Z)[] zones, int captureRadius)
    {
        _zones = zones;
        CaptureRadius = captureRadius;
    }

    /// <summary><c>g_landing_zone_capture_radius [0x9E74]</c>, in world-object units.</summary>
    public int CaptureRadius { get; }

    /// <summary>The threshold the Chebyshev excess is measured against: <c>hi16(1.5 × radius)</c>.</summary>
    public int Threshold => (int)(((long)CaptureRadius + (CaptureRadius >> 1)) >> 16);

    /// <summary>How many zones the theater registered.</summary>
    public int Count => _zones.Length;

    /// <summary>Builds the table a theater catalog registers.</summary>
    /// <param name="theater">A <c>.W</c> catalog.</param>
    /// <param name="captureRadius">
    /// <c>[0x9E74]</c> — use <see cref="ShippedCaptureRadius"/> to read the shipped value.
    /// </param>
    public static LandingZoneTable FromTheater(MissionDefinition theater, int captureRadius)
    {
        ArgumentNullException.ThrowIfNull(theater);
        (int X, int Z)[] zones = theater.Sites
            .Where(site => site.Type is MissionSite.LandingZoneType or MissionSite.EngagementZoneType)
            .Take(MaxEntries)
            .Select(site => (
                X: site.X << FlightColdStart.WorldToObjectShift,
                Z: site.Z << FlightColdStart.WorldToObjectShift))
            .ToArray();

        return new LandingZoneTable(zones, captureRadius);
    }

    /// <summary>The shipped capture radius, from <c>exe/tables/flight_tuning.json</c>.</summary>
    /// <param name="tree">The opened data tree.</param>
    /// <remarks>
    /// A compile-time constant with no writer image-wide
    /// (<c>g_landing_zone_capture_radius_lo/hi_i16</c>); shipped value <c>0x00155800</c>. The
    /// <c>flight_tuning</c> table landed.
    /// </remarks>
    public static int ShippedCaptureRadius(DataTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return BinaryPrimitives.ReadInt32LittleEndian(
            tree.Constants.Span(CaptureRadiusDgroupOffset, 4));
    }

    /// <summary>
    /// The minimum Chebyshev excess over every zone — 0 inside one, <see cref="int.MaxValue"/> when
    /// the table is empty.
    /// </summary>
    /// <param name="x">The player object's world X.</param>
    /// <param name="z">The player object's world Z.</param>
    /// <remarks>
    /// The original seeds the running minimum with <c>0x7FFF</c> (<c>image@0x091AC</c>), which is
    /// what an empty table returns; this type reports <see cref="int.MaxValue"/> instead, because the
    /// only consumer compares it against zero.
    /// </remarks>
    public int Distance(int x, int z)
    {
        int best = int.MaxValue;
        int threshold = Threshold;
        int px = HighWord(x);
        int pz = HighWord(z);

        foreach ((int zoneX, int zoneZ) in _zones)
        {
            int dx = Math.Max(0, Math.Abs(px - HighWord(zoneX)) - threshold);
            int dz = Math.Max(0, Math.Abs(pz - HighWord(zoneZ)) - threshold);
            best = Math.Min(best, dx + dz);
        }

        return best;
    }

    /// <summary>The answer <c>crash_secondary_check</c> gives: is the player inside a landing zone?</summary>
    /// <param name="x">The player object's world X.</param>
    /// <param name="z">The player object's world Z.</param>
    public bool Contains(int x, int z) => Distance(x, z) <= 0;

    /// <summary>
    /// The high word the original compares, as a SIGNED 16-bit value: the guest reads
    /// <c>[si+2]</c>/<c>[bx+2]</c>, i.e. the i32's upper half.
    /// </summary>
    /// <param name="value">A world-object coordinate.</param>
    private static int HighWord(int value) => unchecked((short)(value >> 16));
}
