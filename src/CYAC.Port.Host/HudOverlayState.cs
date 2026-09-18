using CYAC.Port.Core.Model.Cockpit;

namespace CYAC.Port.Host;

/// <summary>
/// The two pieces of HUD state the original keeps between frames: the aircraft's own ATTITUDE
/// HISTORY (which the gunsight leads the target with) and the canopy's BULLET HOLES.
/// </summary>
/// <remarks>
/// <para>
/// Presentation state only — nothing here is read by the simulation, and the host owns it so the
/// renderer stays pure.
/// </para>
/// <para>
/// <b>The history ring.</b>  <c>hud_sample_history_advance @image@0x0CF5D</c> runs once a frame and,
/// whenever <c>g_frame_time_accum</c> has advanced <b>16</b> units past the last sample, shifts the
/// 15-entry ring at <c>[0xBA8C..0xBB2B]</c> down by one 10-byte record (a <c>memmove</c> of 0x96
/// bytes) and writes the newest one: the player object's <c>+0x12/+0x14/+0x16</c> (heading,
/// elevation, roll) and the accumulator as an <c>i32</c> at <c>+0x06</c>.  15 × 16 = 240 units of
/// history, and the gunsight's default lag of <c>0x40</c> reaches four entries back.
/// </para>
/// <para>
/// <b>The bullet holes.</b>  <c>image@0x0CD26</c> appends at most TWO entries to <c>[0xBA82]</c>,
/// each a random point: <c>x = (rand(0xC4) + 0x32) &amp; 0xF0</c> and
/// <c>y = rand(block[+0x12] − block[+0x10] − 0x30) + block[+0x10] + 5</c>, where the block is the
/// aircraft's own HUD layout block — so <c>+0x10</c> and <c>+0x12</c>, the heading row and the
/// waypoint second row, bound the band the decals land in.  Nothing ever clears them.
/// </para>
/// </remarks>
public sealed class HudOverlayState
{
    /// <summary>How many attitude samples the ring holds — <c>(0xBB22 − 0xBA8C) / 10 + 1</c>.</summary>
    public const int HistoryEntries = 15;

    /// <summary>Accumulator units between samples — <c>image@0x0CF66</c>'s <c>add ax, 0x10</c>.</summary>
    public const int HistoryIntervalTicks = 16;

    /// <summary>The gunsight's lag when the five guards do not produce a time of flight.</summary>
    /// <remarks><c>image@0x0D137</c>: <c>mov si, 0x40</c>.</remarks>
    public const int DefaultLagTicks = 0x40;

    /// <summary>The most hit decals the canopy ever carries — <c>cmp word [0xBA80], 2</c>.</summary>
    public const int MaxHitMarkers = 2;

    private readonly (uint Stamp, double Heading, double Pitch)[] _history =
        new (uint, double, double)[HistoryEntries];

    private readonly List<PanelPoint> _hits = new(MaxHitMarkers);

    private int _samples;
    private uint _lastStamp;
    private bool _seeded;
    private int _lastDamage = -1;

    /// <summary>The bullet-hole decals the canopy carries, in the order they were taken.</summary>
    public IReadOnlyList<PanelPoint> HitMarkers => _hits;

    /// <summary>How many attitude samples the ring currently holds.</summary>
    public int SampleCount => _samples;

    /// <summary>Offers this frame's attitude to the ring; it is stored on the 16-unit boundary.</summary>
    /// <param name="accumulator">The frame-time accumulator, <c>g_frame_time_accum [0xF0D2]</c>.</param>
    /// <param name="headingDegrees">The player's heading.</param>
    /// <param name="pitchDegrees">Its pitch.</param>
    public void Sample(uint accumulator, double headingDegrees, double pitchDegrees)
    {
        if (_seeded && unchecked(accumulator - _lastStamp) < HistoryIntervalTicks)
        {
            return;
        }

        // The original shifts the whole ring down one record and writes the newest at the top; the
        // port keeps the same order so index 0 is the OLDEST.
        for (int i = 0; i + 1 < HistoryEntries; i++)
        {
            _history[i] = _history[i + 1];
        }

        _history[HistoryEntries - 1] = (accumulator, headingDegrees, pitchDegrees);
        _samples = Math.Min(HistoryEntries, _samples + 1);
        _lastStamp = accumulator;
        _seeded = true;
    }

    /// <summary>
    /// The aircraft's attitude <paramref name="lagTicks"/> accumulator units ago, interpolated
    /// between the two samples that bracket it.
    /// </summary>
    /// <param name="accumulator">The frame-time accumulator now.</param>
    /// <param name="lagTicks">How far back to look.</param>
    /// <param name="headingDegrees">The heading then.</param>
    /// <param name="pitchDegrees">The pitch then.</param>
    /// <returns>False when the ring holds nothing yet.</returns>
    /// <remarks>
    /// <c>hud_history_bracket_interp @image@0x0CFBA</c> walks the ring backwards for the newest
    /// record no newer than the wanted stamp, takes its successor as the other bracket, and
    /// interpolates.  A negative stamp is clamped to zero at <c>image@0x0CFC2</c>.
    /// </remarks>
    public bool AttitudeAt(
        uint accumulator, int lagTicks, out double headingDegrees, out double pitchDegrees)
    {
        headingDegrees = 0.0;
        pitchDegrees = 0.0;
        if (_samples == 0)
        {
            return false;
        }

        int first = HistoryEntries - _samples;
        long wanted = Math.Max(0L, (long)accumulator - Math.Max(0, lagTicks));

        int older = first;
        for (int i = HistoryEntries - 1; i >= first; i--)
        {
            if (_history[i].Stamp <= wanted)
            {
                older = i;
                break;
            }
        }

        int newer = Math.Min(HistoryEntries - 1, older + 1);
        (uint Stamp, double Heading, double Pitch) a = _history[older];
        (uint Stamp, double Heading, double Pitch) b = _history[newer];
        double span = b.Stamp - (double)a.Stamp;
        double t = span <= 0.0 ? 0.0 : Math.Clamp((wanted - a.Stamp) / span, 0.0, 1.0);

        headingDegrees = a.Heading + (ShortestArc(b.Heading - a.Heading) * t);
        pitchDegrees = a.Pitch + ((b.Pitch - a.Pitch) * t);
        return true;
    }

    /// <summary>
    /// Stamps a new bullet hole on the canopy when the player's damage total has grown.
    /// </summary>
    /// <param name="damage">
    /// <c>g_engagement_rounds_accum [0xF1CC]</c> — what <c>weapon_fire_combat_loop @image@0x0F748</c>
    /// adds every hit to.  Pass −1 outside a mission.
    /// </param>
    /// <param name="layout">The aircraft's own HUD layout block, which bounds the decal band.</param>
    /// <param name="seed">A deterministic seed — the simulation step, so a replay repeats.</param>
    public void ObserveDamage(int damage, HudLayout layout, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (damage < 0)
        {
            _lastDamage = -1;
            return;
        }

        if (_lastDamage < 0)
        {
            _lastDamage = damage;
            return;
        }

        if (damage <= _lastDamage)
        {
            return;
        }

        _lastDamage = damage;
        if (_hits.Count >= MaxHitMarkers)
        {
            return;
        }

        // image@0x0CD4C..0x0CD71 — the same two draws, from the port's own deterministic stream.
        Random random = new Random(unchecked((int)(seed ^ 0x5EED_C1ACUL)));
        int x = (random.Next(0xC4) + 0x32) & 0xF0;
        int band = layout.WaypointSecondY - layout.HeadingAnchorY - 0x30;
        int y = layout.HeadingAnchorY + 5 + (band > 0 ? random.Next(band) : 0);
        _hits.Add(new PanelPoint(x, y));
    }

    /// <summary>Forgets the decals and the history — a new sortie starts with a clean canopy.</summary>
    public void Reset()
    {
        _hits.Clear();
        _samples = 0;
        _seeded = false;
        _lastDamage = -1;
    }

    private static double ShortestArc(double degrees)
    {
        double d = degrees % 360.0;
        if (d > 180.0)
        {
            d -= 360.0;
        }
        else if (d < -180.0)
        {
            d += 360.0;
        }

        return d;
    }
}
