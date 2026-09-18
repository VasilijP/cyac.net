using System.Globalization;

namespace CYAC.Port.Render;

/// <summary>Which halves of the tracer halo are drawn.</summary>
/// <remarks>
/// (<c>--tracer-halo off|a|b|ab</c>).
/// </remarks>
public enum TracerHaloMode
{
    /// <summary>Neither: the tracer is its core capsule alone.</summary>
    Off = 0,

    /// <summary>A only — the tracer-coloured glow disc.</summary>
    Disc = 1,

    /// <summary>B only — the relative lightening toward white.</summary>
    Lighten = 2,

    /// <summary>Both, A then B.  The default.</summary>
    Both = 3,
}

/// <summary>
/// The tracer's HALO: a soft glow around the projectile core, in two independently switchable
/// halves.
/// </summary>
/// <param name="Mode">Which halves are drawn.</param>
/// <param name="RadiusFeet">
/// The halo's physical radius about the projectile's own segment, in feet (= world units).
/// </param>
/// <param name="Alpha">
/// A's peak opacity, at the centre of the profile.  The edge is always 0.
/// </param>
/// <param name="Glow">
/// B's peak fraction of the distance to white: a pixel under the centre of the profile moves
/// <c>Glow</c> of the way from its own value to 255.
/// </param>
/// <param name="FloorHostPixels">
/// The smallest radius the halo may be DRAWN at, in host pixels at a 1,920-pixel-wide window
/// (scaled with the window like <see cref="Ground.LineWidthModel.FloorPixelsFor"/>), so a distant
/// tracer keeps a visible bloom instead of collapsing into its own core.
/// </param>
/// <param name="ColorIndex">
/// The palette index the glow is drawn in, or <b>−1</b> for the tracer record's own colour
/// (<c>bullet</c>'s <c>.PNT</c> colour stream says palette 32).
/// </param>
/// <remarks>
/// <para>
/// <b>The design</b>: draw the projectile smaller and add a special-effect halo. A) a
/// semi-transparent circle, tracer-coloured, more transparent toward the edge; B) it lightens the
/// scene underneath by a RELATIVE amount — each pixel moves some fraction (e.g. 10 %) of its
/// distance to white — so it does little over a day sky and glows over a dark background or a
/// night sky.
/// </para>
/// <para>
/// <b>DEVIATION, labelled.</b> The original draws NOTHING of the sort: a tracer is one 64-foot
/// <c>line</c> record of the <c>bullet</c> mesh in palette 32, painted as a run of single 320×200
/// pixels, and <c>weapon_fire_event_scheduler @image@0x03514</c> spawns exactly one such object per
/// 64-tick window — a drawn tracer IS a whole burst, not a round.  The halo is a presentation choice
/// under the refined-render doctrine (represent, do not reproduce) and every one of its numbers is a
/// knob.
/// </para>
/// <para>
/// <b>Why B is not just "add some white".</b> An ADDITIVE bloom of a fixed amount is invisible over
/// a bright sky and blows out over dark ground; a RELATIVE one — <c>c += (255 − c)·g·w</c> — moves
/// each pixel a fraction of the distance it still has to travel, so it does almost nothing to a sky
/// already at 240 and lifts a black night sky by <c>255·g·w</c>.  It is also what a physical glare
/// veil does to a photographic negative.
/// </para>
/// </remarks>
public readonly record struct TracerHalo(
    TracerHaloMode Mode = TracerHaloMode.Both,
    double RadiusFeet = TracerHalo.DefaultRadiusFeet,
    double Alpha = TracerHalo.DefaultAlpha,
    double Glow = TracerHalo.DefaultGlow,
    double FloorHostPixels = TracerHalo.DefaultFloorHostPixels,
    int ColorIndex = -1)
{
    /// <summary>The halo's default physical radius: <b>4 feet</b> about the projectile's segment.</summary>
    public const double DefaultRadiusFeet = 4.0;

    /// <summary>A's default peak opacity: <b>0.5</b>.</summary>
    /// <remarks>
    /// Their <c>settings.json</c> holds <b>0.5</b>.
    /// </remarks>
    public const double DefaultAlpha = 0.5;

    /// <summary>B's default peak lightening: <b>0.10</b>, i.e. 10 %.</summary>
    public const double DefaultGlow = 0.10;

    /// <summary>
    /// The halo's default on-screen floor: <b>3 host pixels</b> of radius at 1,920 wide.
    /// </summary>
    /// <remarks>
    /// Twice the line floor (<see cref="Ground.LineWidthModel.DefaultFloorHostPixels"/> = 1.5), so a
    /// tracer at any range is a 1.5-pixel core inside a 6-pixel bloom and never degenerates into a
    /// bare mark.  4 feet of physical radius falls below it past about 1,000 feet at 1080p.
    /// </remarks>
    public const double DefaultFloorHostPixels = 3.0;

    /// <summary>
    /// The largest radius the halo may be drawn at, as a fraction of the target's WIDTH: 0.15.
    /// </summary>
    /// <remarks>
    /// A cost guard, not a look choice.  A projectile passing within a few feet of the eye projects
    /// a halo bigger than the frame, and the fill is <c>O(r²)</c>: at 3,840 super-samples wide the
    /// cap bounds one halo at about 1.0 M pixels instead of 8.3 M.  It binds only inside about
    /// 25 feet of the camera at the default radius, which is a single frame of a passing burst.
    /// </remarks>
    public const double MaxRadiusFraction = 0.15;

    /// <summary>The width the <see cref="FloorHostPixels"/> figure is stated at: 1,920.</summary>
    public const int FloorReferenceWidth = Ground.LineWidthModel.FloorReferenceWidth;

    /// <summary>The shipped halo.</summary>
    public static TracerHalo Default { get; } = new(
        TracerHaloMode.Both,
        DefaultRadiusFeet,
        DefaultAlpha,
        DefaultGlow,
        DefaultFloorHostPixels,
        -1);

    /// <summary>Whether this halo paints anything at all.</summary>
    public bool Enabled => (DrawsDisc && Alpha > 0.0) || (DrawsLighten && Glow > 0.0);

    /// <summary>Whether half A — the tracer-coloured glow disc — is drawn.</summary>
    public bool DrawsDisc => Mode is TracerHaloMode.Disc or TracerHaloMode.Both;

    /// <summary>Whether half B — the relative lightening — is drawn.</summary>
    public bool DrawsLighten => Mode is TracerHaloMode.Lighten or TracerHaloMode.Both;

    /// <summary>The halo's on-screen floor RADIUS for a window of a given width, in its pixels.</summary>
    /// <param name="hostWidthPixels">The window's width in HOST pixels (not super-samples).</param>
    public double FloorPixelsFor(int hostWidthPixels) =>
        FloorHostPixels <= 0
            ? 0.0
            : FloorHostPixels * hostWidthPixels / FloorReferenceWidth;

    /// <summary>
    /// The radial alpha PROFILE: <c>(1 − r²)²</c>, 1 at the centre and 0 at the rim.
    /// </summary>
    /// <param name="normalizedDistance">
    /// Distance from the projectile's segment, divided by the halo radius.
    /// </param>
    /// <returns>The profile weight in <c>[0, 1]</c>; 0 at and beyond the rim.</returns>
    /// <remarks>
    /// The port's own choice, and the cheapest curve with a ZERO GRADIENT at both ends: it has no
    /// visible seam at the rim (which an unsquared <c>1 − r²</c> would show as a hard circle at low
    /// alpha) and no cusp at the centre.  It is the same family as the soft-disc falloff an earlier pass gave
    /// the smoke puffs (<see cref="Raster.Paint.Soft"/>), squared.
    /// </remarks>
    public static double Profile(double normalizedDistance)
    {
        double r2 = normalizedDistance * normalizedDistance;
        if (r2 >= 1.0)
        {
            return 0.0;
        }

        double t = 1.0 - r2;
        return t * t;
    }

    /// <summary>
    /// B — the RELATIVE LIGHTENING law, on one channel: <c>c + (255 − c)·amount</c>.
    /// </summary>
    /// <param name="channel">The channel as it stands, 0…255.</param>
    /// <param name="amount">The fraction of the remaining distance to white, 0…1.</param>
    /// <returns>The lightened channel, unrounded.</returns>
    /// <remarks>
    /// The rule: move each pixel some fraction (e.g. 10 %) of its distance to white.  200 with 0.1
    /// becomes 205.5; 0 with 0.1 becomes 25.5 — so the same halo is nearly
    /// invisible over a day sky and unmistakable over dark ground or a night sky.
    /// </remarks>
    public static double LightenChannel(double channel, double amount) =>
        channel + ((255.0 - channel) * amount);

    /// <summary>
    /// What one pixel under the halo becomes: A over it, then B on the result.
    /// </summary>
    /// <param name="destination">The pixel as it stands, in the target's channel order.</param>
    /// <param name="tracer">The tracer's colour, in the same order.</param>
    /// <param name="weight">The profile weight at this pixel, 0…1.</param>
    /// <returns>The colour to store.</returns>
    /// <remarks>
    /// <para>
    /// <b>A is PREMULTIPLIED.</b>  The source alpha varies per pixel and the source colour does not,
    /// so the composite is written as <c>src·a + dst·(1 − a)</c> with <c>a = Alpha·weight</c> —
    /// the premultiplied-over form, which for a constant source colour is numerically the lerp and
    /// which stays correct if a later change ever gives the glow a per-pixel colour.
    /// </para>
    /// <para>
    /// <b>Order.</b>  A first, then B on A's result, so the centre of a bright halo goes tracer-pink
    /// and then hot toward white — the way a tracer's core reads on film.  With
    /// <see cref="TracerHaloMode.Lighten"/> alone the scene keeps its own colours and only brightens.
    /// </para>
    /// </remarks>
    public uint Composite(uint destination, uint tracer, double weight)
    {
        if (weight <= 0.0)
        {
            return destination;
        }

        double w = weight > 1.0 ? 1.0 : weight;
        double a = DrawsDisc ? Math.Clamp(Alpha, 0.0, 1.0) * w : 0.0;
        double g = DrawsLighten ? Math.Clamp(Glow, 0.0, 1.0) * w : 0.0;

        uint result = 0;
        for (int shift = 0; shift <= 16; shift += 8)
        {
            double d = (destination >> shift) & 0xFF;
            double s = (tracer >> shift) & 0xFF;

            // A — premultiplied over: src·a + dst·(1 − a).
            double c = a > 0.0 ? (s * a) + (d * (1.0 - a)) : d;

            // B — the relative lightening, on A's result.
            if (g > 0.0)
            {
                c = LightenChannel(c, g);
            }

            int v = (int)Math.Round(c, MidpointRounding.AwayFromZero);
            result |= (uint)Math.Clamp(v, 0, 255) << shift;
        }

        return result;
    }

    /// <summary>Parses <c>--tracer-halo off|a|b|ab</c>.</summary>
    /// <param name="text">The word; null or empty gives <see cref="TracerHaloMode.Both"/>.</param>
    /// <exception cref="FormatException">The word names no mode.</exception>
    public static TracerHaloMode ParseMode(string? text)
    {
        string word = (text ?? string.Empty).Trim().ToLowerInvariant();
        return word switch
        {
            "" or "ab" or "both" or "on" => TracerHaloMode.Both,
            "off" or "none" => TracerHaloMode.Off,
            "a" or "disc" => TracerHaloMode.Disc,
            "b" or "lighten" or "glow" => TracerHaloMode.Lighten,
            _ => throw new FormatException($"--tracer-halo: '{text}' is not off, a, b or ab"),
        };
    }

    /// <summary>A one-line description, for the host's readout.</summary>
    public override string ToString()
    {
        if (Mode == TracerHaloMode.Off)
        {
            return "halo off";
        }

        string colour = ColorIndex >= 0
            ? string.Create(CultureInfo.InvariantCulture, $" colour {ColorIndex}")
            : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"halo {Mode.ToString().ToLowerInvariant()} r {RadiusFeet:F1} ft (floor {FloorHostPixels:F1} px) alpha {Alpha:F2} glow {Glow:F2}{colour}");
    }
}
