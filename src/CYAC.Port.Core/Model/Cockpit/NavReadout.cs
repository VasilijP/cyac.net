using System.Globalization;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Model.Cockpit;

/// <summary>How much of the NAV readout the host draws (<c>--nav-readout</c>).</summary>
public enum NavReadoutMode
{
    /// <summary>Nothing.</summary>
    Off,

    /// <summary>One line: the label, the relative bearing and the range in nautical miles.</summary>
    Compact,

    /// <summary>
    /// Three lines: the label, both bearings and both range units, and what the slot IS.
    /// </summary>
    Full,
}

/// <summary>
/// The NAV waypoint READOUT: a labelled DEVIATION that says in text what the 1991 cockpit says with
/// two lights and a needle.
/// </summary>
/// <remarks>
/// <para>
/// <b>The original has no such readout.</b>  Its answer to "where is the waypoint" is the compass
/// course-deviation pair (<c>region_7_compass_state_fn @image@0x0DFB4</c>: a ±<c>0x20</c> BAM dead
/// band on the RELATIVE bearing, lighting <c>0x4000</c> right-of-course or <c>0x8000</c>
/// left-of-course), the bearing pointer on dial slot 4
/// (<c>cockpit_dial_state_compute_all @image@0x01FCF</c>), and — for the RANGE — the F9 map, which
/// the manual explicitly sends the pilot to (p.53: "To find out the distance to the waypoint, press
/// F9 to bring up the Map").  The port has no map yet, so this block stands in for it.
/// </para>
/// <para>
/// Everything it prints is read, not invented: the label is the record's own name field
/// (<c>[0xB564] + 0x2C·slot + 0x0D</c>), the bearings are
/// <see cref="NavBearing"/> — i.e. <c>object_bearing_to_slot_compute @image@0x08E72</c> with
/// <c>AL = 1</c> and <c>AL = 0</c> — and the range is the XZ distance in the same Q8 feet the
/// player's <c>+0x06</c>/<c>+0x0E</c> carry.  A TRACKING waypoint whose actors are all gone has NO
/// position (<c>slot_record_get_pos @image@0x08E0D</c> fails and <c>image@0x08E89</c> returns 0), and
/// the readout says so with dashes instead of freezing the last bearing.
/// </para>
/// <para>
/// There is no degree glyph: mode-13hx's <c>Canvas.DrawString</c> indexes its font sheet by
/// <c>(byte)ch</c>, so U+00B0 would land on the CP437 shade block — the same trap H10a hit with the
/// em dash.  Bearings are printed as <c>L045</c> / <c>R045</c> and <c>brg 045</c> instead.
/// </para>
/// </remarks>
public static class NavReadout
{
    /// <summary>Feet in one nautical mile — the unit the readout prints ranges in.</summary>
    public const double FeetPerNauticalMile = 6076.11548556;

    /// <summary>What the readout has to say about the current slot.</summary>
    /// <param name="Mode">The <c>--nav-readout</c> setting.</param>
    /// <param name="HasMission">False for a Test Flight, which has no nav slots at all.</param>
    /// <param name="SlotIndex"><c>g_nav_slot_current_index [0xEF92]</c>.</param>
    /// <param name="SlotCount"><c>g_nav_slot_count [0xEF90]</c>.</param>
    /// <param name="Label">The record's name field, or null when nothing registered the slot.</param>
    /// <param name="Tracks">Whether the record is a TRACKING one (placement tag 7).</param>
    /// <param name="RangeFeetQ8">
    /// The XZ distance to the waypoint in Q8 feet, or null when it has no position this frame.
    /// </param>
    /// <param name="RelativeBam">The bearing relative to the heading, <c>(−0x5A0, +0x5A0]</c>.</param>
    /// <param name="AbsoluteBam">The absolute bearing, <c>[0, 0xB40)</c>.</param>
    public readonly record struct State(
        NavReadoutMode Mode,
        bool HasMission,
        int SlotIndex,
        int SlotCount,
        string? Label,
        bool Tracks,
        double? RangeFeetQ8,
        int RelativeBam,
        int AbsoluteBam);

    /// <summary>The lines to draw, top to bottom; empty when there is nothing to say.</summary>
    /// <param name="state">What the frame knows.</param>
    public static IReadOnlyList<string> Lines(in State state)
    {
        if (state.Mode == NavReadoutMode.Off || !state.HasMission)
        {
            return [];
        }

        if (state.SlotCount <= 0)
        {
            // The original's own zero case: no waypoint was registered, so both needles sit at zero
            // and the NAV key has nothing to cycle (image@0x08E89).
            return state.Mode == NavReadoutMode.Full ? ["NAV  no waypoints"] : [];
        }

        // nav_waypoint_show_current @image@0x08D31 prints the slot ONE-BASED; so does this.
        string head = state.Label is { } label
            ? $"NAV {state.SlotIndex + 1}/{state.SlotCount}  {label}"
            : $"NAV {state.SlotIndex + 1}/{state.SlotCount}  (slot not registered)";

        if (state.RangeFeetQ8 is not { } rangeQ8)
        {
            // A TRACKING waypoint whose actors are all dead — the compass falls back to the heading
            // and there is no bearing to print.
            string why = state.Label is null
                ? "no record"
                : state.Tracks ? "no live actor" : "no position";
            return state.Mode == NavReadoutMode.Full
                ? [head, $"     ----   --- nm   ({why})"]
                : [$"{head}   ----   --- nm"];
        }

        double feet = rangeQ8 / 256.0;
        double miles = feet / FeetPerNauticalMile;
        string relative = Bearing(state.RelativeBam);
        if (state.Mode == NavReadoutMode.Compact)
        {
            return [string.Create(
                CultureInfo.InvariantCulture,
                $"{head}   {relative}   {miles,5:F1} nm")];
        }

        return
        [
            head,
            string.Create(
                CultureInfo.InvariantCulture,
                $"     rel {relative}   brg {Absolute(state.AbsoluteBam)}"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"     {miles,5:F1} nm ({feet,9:N0} ft)   "
                    + $"{(state.Tracks ? "TRACKING" : "fixed point")}"),
        ];
    }

    /// <summary>A relative bearing as a signed degree count with a turn side.</summary>
    /// <param name="bam">The BAM, <c>(−0x5A0, +0x5A0]</c>.</param>
    /// <remarks>
    /// The BAM is the heading word's own unit — <c>DialNeedle.FullTurnBam</c> = <c>0x0B40</c> is a
    /// full turn, so a degree is <see cref="Angle.UnitsPerDegree"/> = 8.  Positive is RIGHT of the nose: the compass's own
    /// <c>cmp di,0x20 / jg</c> arm sets the right-of-course bit (<c>image@0x0DFCE</c>).
    /// </remarks>
    /// <returns>A four-character field: a side letter and three digits.
    /// </remarks>
    public static string Bearing(int bam)
    {
        int degrees = (int)Math.Round(bam / (double)Angle.UnitsPerDegree);
        char side = degrees == 0 ? ' ' : degrees > 0 ? 'R' : 'L';
        return string.Create(CultureInfo.InvariantCulture, $"{side}{Math.Abs(degrees),3:D3}");
    }

    /// <summary>An absolute bearing as a three-digit compass course.</summary>
    /// <param name="bam">The BAM, <c>[0, 0xB40)</c>.</param>
    public static string Absolute(int bam)
    {
        int degrees = (int)Math.Round(bam / (double)Angle.UnitsPerDegree) % 360;
        if (degrees < 0)
        {
            degrees += 360;
        }

        return degrees.ToString("D3", CultureInfo.InvariantCulture);
    }
}
