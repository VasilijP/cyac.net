using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Markings;

/// <summary>Chipping: a hash noise cuts the layer's paint, biased toward its edges.</summary>
/// <param name="Amount">How much of the layer is chipped, 0…1 (the noise threshold).</param>
/// <param name="Scale">The chip size in picture units (the noise cell).</param>
/// <param name="Seed">The noise seed.</param>
/// <param name="EdgeBand">How far inside the edge the bias reaches, picture units.</param>
public readonly record struct Chipping(double Amount, double Scale, int Seed, double EdgeBand)
{
    /// <summary>The fraction of <see cref="Amount"/> that applies deep inside the shape (the rest is edge bias).</summary>
    public const double InteriorFraction = 0.35;

    /// <summary>Whether the effect does anything.</summary>
    public bool Active => Amount > 0.0 && Scale > 0.0;
}

/// <summary>Edge wear: a soft alpha ramp just inside the shape's edge — faded paint edges.</summary>
/// <param name="Width">The ramp's depth, picture units.</param>
/// <param name="Strength">How far the edge fades, 0…1.</param>
public readonly record struct EdgeWear(double Width, double Strength)
{
    /// <summary>Whether the effect does anything.</summary>
    public bool Active => Width > 0.0 && Strength > 0.0;
}

/// <summary>The wear effects a picture (or one of its layers) carries.</summary>
/// <param name="Chipping">The chipping, or default for none.</param>
/// <param name="EdgeWear">The edge wear, or default for none.</param>
public readonly record struct WearParams(Chipping Chipping = default, EdgeWear EdgeWear = default)
{
    /// <summary>No wear at all.</summary>
    public static WearParams None => default;

    /// <summary>Whether either effect is active.</summary>
    public bool Active => Chipping.Active || EdgeWear.Active;

    /// <summary>These parameters scaled: chip size, edge band and wear width multiply by the factor.</summary>
    /// <param name="factor">The factor.</param>
    public WearParams Scaled(double factor) => new(
        Chipping with { Scale = Chipping.Scale * factor, EdgeBand = Chipping.EdgeBand * factor },
        EdgeWear with { Width = EdgeWear.Width * factor });
}

/// <summary>One painted layer of a picture: a shape filled with one colour at one opacity.</summary>
/// <param name="Shape">The shape.</param>
/// <param name="Color">The paint.</param>
/// <param name="Opacity">0…1.</param>
/// <param name="Wear">This layer's own wear, or null to take the picture's.</param>
public sealed record MarkingLayer(MarkingShape Shape, SurfaceColor Color, double Opacity = 1.0, WearParams? Wear = null);

/// <summary>
/// VECTOR MARKINGS — a PICTURE: an ordered list of painted layers (painter's order) plus its wear,
/// evaluated per pixel as a pure function of the point and the footprint.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anti-aliasing</b>: a layer's coverage at a pixel is <c>saturate(½ − d / footprint)</c> — the
/// exact area of a straight edge crossing the pixel, resolution-independent.
/// </para>
/// <para>
/// <b>Band-limiting</b>: chipping noise is multiplied by <c>saturate(1 − 2·footprint / scale)</c>,
/// so at range a marking is a clean shape and close up it is chipped — the anti-shimmer law.
/// The noise is a hash of the picture-space position: deterministic, thread-free.
/// </para>
/// <para>
/// <b>Bare metal</b> (§3.2 <c>Skin</c>): where a chip cuts a layer, the exposed colour is
/// <see cref="BareMetal"/> when the picture names one, else whatever lies beneath.
/// </para>
/// </remarks>
/// <summary>
/// What a CHIP EXPOSES, decided per placement.  Every built-in picture names an aluminium
/// <c>#A8ACB0</c>, which is right for an insignia painted straight onto a natural-metal jet and wrong
/// for a painted WW2 aeroplane, where a chipped star shows the camouflage it was painted over — the
/// polygon's own colour.
/// </summary>
/// <param name="Kind">Which rule.</param>
/// <param name="Color">The explicit colour, for <see cref="BareMetalKind.Explicit"/>.</param>
/// <param name="Shade">
/// For <see cref="BareMetalKind.Skin"/>: the signed fraction the underlying paint is lightened
/// (+) or darkened (−) by, e.g. −0.3 = a scuff 30 % darker than the paint it sits on; 0 = the
/// paint exactly (the same as <see cref="BareMetalKind.Paint"/>).
/// </param>
/// <remarks>
/// JSON spellings (a placement's <c>"bareMetal"</c>): <c>"none"</c> / <c>"paint"</c> = the paint
/// beneath, <c>"#RRGGBB"</c> = that colour, <c>"skin"</c> = the paint, <c>"skin:-0.3"</c> = the paint
/// shaded.  Absent = the picture's own <c>bareMetal</c>.
/// </remarks>
public readonly record struct BareMetalSpec(BareMetalKind Kind, SurfaceColor Color = default, double Shade = 0.0)
{
    /// <summary>The paint beneath the layer — the underlying polygon's own colour.</summary>
    public static BareMetalSpec Paint => new(BareMetalKind.Paint);

    /// <summary>Parses the JSON spelling (see the type's remarks).</summary>
    /// <param name="text">The spelling.</param>
    /// <exception cref="FormatException">Not a known spelling.</exception>
    public static BareMetalSpec Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string t = text.Trim();
        string lower = t.ToLowerInvariant();
        if (lower is "none" or "paint" or "skin")
        {
            return Paint;
        }

        if (lower.StartsWith("skin:", StringComparison.Ordinal))
        {
            return double.TryParse(t[5..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double shade)
                ? new BareMetalSpec(BareMetalKind.Skin, default, Math.Clamp(shade, -1.0, 1.0))
                : throw new FormatException($"bareMetal '{text}': expected skin:<fraction>");
        }

        return new BareMetalSpec(BareMetalKind.Explicit, SurfaceColor.Parse(t));
    }

    /// <summary>The JSON spelling.</summary>
    public override string ToString() => Kind switch
    {
        BareMetalKind.Explicit => Color.ToString(),
        BareMetalKind.Skin => Shade == 0.0 ? "skin" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"skin:{Shade:0.###}"),
        _ => "none",
    };

    /// <summary>The exposed colour over a given paint, or null for the paint itself.</summary>
    /// <param name="skin">The face's paint.</param>
    /// <param name="pictureDefault">The picture's own bare metal, for <see cref="BareMetalKind.Picture"/>.</param>
    public SurfaceColor? Resolve(SurfaceColor skin, SurfaceColor? pictureDefault) => Kind switch
    {
        BareMetalKind.Picture => pictureDefault,
        BareMetalKind.Explicit => Color,
        BareMetalKind.Skin when Shade != 0.0 => new SurfaceColor(Shaded(skin.R), Shaded(skin.G), Shaded(skin.B)),
        _ => null,
    };

    private byte Shaded(byte channel) => (byte)Math.Clamp((int)Math.Round(channel * (1.0 + Shade)), 0, 255);
}

/// <summary>The rules of <see cref="BareMetalSpec"/>.</summary>
public enum BareMetalKind
{
    /// <summary>The picture's own <c>bareMetal</c> (the default).</summary>
    Picture = 0,

    /// <summary>The paint beneath — what lies under the layer, i.e. the polygon's colour.</summary>
    Paint = 1,

    /// <summary>One explicit colour.</summary>
    Explicit = 2,

    /// <summary>The paint beneath, lightened or darkened by <see cref="BareMetalSpec.Shade"/>.</summary>
    Skin = 3,
}

public sealed class MarkingPicture : ISurfacePicture
{
    private readonly MarkingLayer[] _layers;
    private readonly bool _anyChipping;

    /// <summary>The placement's bare-metal rule over the picture's own (<see cref="BareMetalSpec"/>).</summary>
    private readonly BareMetalSpec _bareMetalRule;

    /// <summary>Creates a picture.</summary>
    /// <param name="name">Its name (the library key).</param>
    /// <param name="layers">Its layers, bottom first.</param>
    /// <param name="wear">The picture-wide wear.</param>
    /// <param name="symmetric">Whether a mirrored placement may keep the picture unflipped.</param>
    /// <param name="noMirror">Whether the picture must never be flipped (codes read the same both sides).</param>
    /// <param name="bareMetal">What chipping exposes, or null for the paint beneath.</param>
    public MarkingPicture(
        string name,
        IReadOnlyList<MarkingLayer> layers,
        WearParams wear = default,
        bool symmetric = false,
        bool noMirror = false,
        SurfaceColor? bareMetal = null,
        BareMetalSpec bareMetalRule = default)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _bareMetalRule = bareMetalRule;
        _layers = [.. layers ?? throw new ArgumentNullException(nameof(layers))];
        Wear = wear;
        Symmetric = symmetric;
        NoMirror = noMirror;
        BareMetal = bareMetal;

        double band = 0.0;
        SurfaceBounds? bounds = null;
        foreach (MarkingLayer layer in _layers)
        {
            WearParams w = layer.Wear ?? wear;
            band = Math.Max(band, Math.Max(w.EdgeWear.Width, w.Chipping.EdgeBand));
            _anyChipping |= w.Chipping.Active;
            bounds = bounds is { } b ? b.Union(layer.Shape.Bounds) : layer.Shape.Bounds;
        }

        ShapeBounds = bounds ?? new SurfaceBounds(0, 0, 0, 0);
        // The early-out box: the shapes' box plus one pixel of anti-aliasing ramp — added per pixel
        // by the renderer through the footprint margin — so only the wear band is baked in here.
        Bounds = ShapeBounds.Grow(band);
    }

    /// <summary>The picture's name.</summary>
    public string Name { get; }

    /// <summary>The layers, bottom first.</summary>
    public IReadOnlyList<MarkingLayer> Layers => _layers;

    /// <summary>The picture-wide wear (a layer may override it).</summary>
    public WearParams Wear { get; }

    /// <summary>Whether a mirrored placement may keep it unflipped (a star, a roundel).</summary>
    public bool Symmetric { get; }

    /// <summary>Whether it must never be flipped (a code, a serial).</summary>
    public bool NoMirror { get; }

    /// <summary>What chipping exposes, or null for the paint beneath.</summary>
    public SurfaceColor? BareMetal { get; }

    /// <summary>The union of the layers' boxes, without the wear band.</summary>
    public SurfaceBounds ShapeBounds { get; }

    /// <inheritdoc />
    public SurfaceBounds Bounds { get; }

    /// <summary>This picture with different wear (the placement's override), sharing the layers.</summary>
    /// <param name="wear">The new picture-wide wear.</param>
    /// <param name="perLayer">Whether layer-level wear is dropped too.</param>
    public MarkingPicture WithWear(WearParams wear, bool perLayer = false) => new(
        Name,
        perLayer ? _layers.Select(l => l with { Wear = null }).ToArray() : _layers,
        wear, Symmetric, NoMirror, BareMetal, _bareMetalRule);

    /// <summary>This picture with a placement's bare-metal rule, sharing the layers.</summary>
    /// <param name="rule">What a chip exposes on this placement.</param>
    public MarkingPicture WithBareMetal(BareMetalSpec rule) => new(
        Name, _layers, Wear, Symmetric, NoMirror, BareMetal, rule);

    /// <summary>The bare-metal rule in force (the placement's, else the picture's).</summary>
    public BareMetalSpec BareMetalRule => _bareMetalRule;

    /// <inheritdoc />
    public SurfaceColor Shade(SurfaceColor skin, double x, double y, double footprint)
    {
        double fp = Math.Max(footprint, 1e-9);
        double r = skin.R, g = skin.G, b = skin.B;
        bool touched = false;
        // What a chip exposes on THIS placement: the rule resolves against the face's paint
        // once per pixel (a skin-shaded rule follows the polygon it sits on).
        SurfaceColor? bareMetal = _bareMetalRule.Resolve(skin, BareMetal);

        foreach (MarkingLayer layer in _layers)
        {
            SurfaceBounds bounds = layer.Shape.Bounds;
            WearParams wear = layer.Wear ?? Wear;
            double margin = fp + Math.Max(wear.EdgeWear.Width, wear.Chipping.EdgeBand);
            if (!bounds.Contains(x, y, margin))
            {
                continue;
            }

            double d = layer.Shape.Distance(x, y);
            double coverage = Saturate(0.5 - (d / fp));
            if (coverage <= 0.0)
            {
                continue;
            }

            coverage *= layer.Opacity;

            if (wear.EdgeWear.Active)
            {
                double ramp = Saturate(-d / wear.EdgeWear.Width);
                coverage *= 1.0 - (wear.EdgeWear.Strength * (1.0 - ramp));
            }

            double exposedR = r, exposedG = g, exposedB = b;
            if (wear.Chipping.Active)
            {
                double amplitude = Saturate(1.0 - (2.0 * fp / wear.Chipping.Scale));
                if (amplitude > 0.0)
                {
                    double edge = wear.Chipping.EdgeBand > 0.0 ? Saturate(1.0 - (Math.Abs(d) / wear.Chipping.EdgeBand)) : 0.0;
                    // The threshold is the noise QUANTILE of the wanted fraction, so `amount` IS the
                    // chipped share of the layer's interior (the noise is far from uniform).
                    double threshold = MarkingNoise.Quantile(wear.Chipping.Amount * amplitude
                        * (Chipping.InteriorFraction + ((1.0 - Chipping.InteriorFraction) * edge)));
                    double noise = MarkingNoise.Value(x / wear.Chipping.Scale, y / wear.Chipping.Scale, wear.Chipping.Seed);
                    // A soft threshold about one pixel of noise slope wide (the noise changes by
                    // ~fp/scale per pixel), clamped so it anti-aliases a chip's edge and never
                    // smears the field into a haze; the amplitude term above handles range.
                    double softness = Math.Min(0.12, Math.Max(0.02, 0.6 * fp / wear.Chipping.Scale));
                    double keep = Saturate((noise - threshold) / softness);
                    if (keep < 1.0 && bareMetal is { } bare)
                    {
                        // The chip goes through to bare metal where the layer WAS covering.
                        double chip = (1.0 - keep) * coverage;
                        exposedR = r + ((bare.R - r) * chip);
                        exposedG = g + ((bare.G - g) * chip);
                        exposedB = b + ((bare.B - b) * chip);
                    }

                    coverage *= keep;
                }
            }

            r = exposedR + ((layer.Color.R - exposedR) * coverage);
            g = exposedG + ((layer.Color.G - exposedG) * coverage);
            b = exposedB + ((layer.Color.B - exposedB) * coverage);
            touched = true;
        }

        if (!touched)
        {
            return skin;
        }

        return new SurfaceColor(Quantise(r), Quantise(g), Quantise(b));
    }

    private static byte Quantise(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    private static double Saturate(double v) => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;
}

/// <summary>
/// The deterministic VALUE NOISE the wear effects read: a lattice of hashed values with smooth
/// interpolation, two octaves, in [0, 1).  A pure function of its arguments.
/// </summary>
public static class MarkingNoise
{
    /// <summary>The noise at a point.</summary>
    /// <param name="x">X in noise cells.</param>
    /// <param name="y">Y in noise cells.</param>
    /// <param name="seed">The seed.</param>
    public static double Value(double x, double y, int seed)
    {
        double a = Lattice(x, y, seed);
        double b = Lattice((x * 2.03) + 17.1, (y * 2.03) - 9.7, seed + 977);
        return Saturate((0.65 * a) + (0.35 * b));
    }

    private static double Lattice(double x, double y, int seed)
    {
        double fx = Math.Floor(x), fy = Math.Floor(y);
        int ix = (int)fx, iy = (int)fy;
        double tx = x - fx, ty = y - fy;
        double sx = tx * tx * (3.0 - (2.0 * tx));
        double sy = ty * ty * (3.0 - (2.0 * ty));
        double v00 = Hash(ix, iy, seed), v10 = Hash(ix + 1, iy, seed);
        double v01 = Hash(ix, iy + 1, seed), v11 = Hash(ix + 1, iy + 1, seed);
        double top = v00 + ((v10 - v00) * sx);
        double bottom = v01 + ((v11 - v01) * sx);
        return top + ((bottom - top) * sy);
    }

    private static readonly double[] Quantiles = BuildQuantiles();

    /// <summary>
    /// The noise value below which a fraction <paramref name="p"/> of the field lies — computed
    /// once from a fixed sample of the field, so a chipping <c>amount</c> is the share of the area
    /// actually chipped rather than a raw threshold on a bell-shaped distribution.
    /// </summary>
    /// <param name="p">The fraction, 0…1.</param>
    public static double Quantile(double p)
    {
        if (!(p > 0.0))
        {
            return -1.0;    // nothing chips (the noise is never below −1)
        }

        if (p >= 1.0)
        {
            return 2.0;
        }

        double index = p * (Quantiles.Length - 1);
        int i = (int)index;
        double t = index - i;
        return Quantiles[i] + ((Quantiles[Math.Min(i + 1, Quantiles.Length - 1)] - Quantiles[i]) * t);
    }

    private static double[] BuildQuantiles()
    {
        const int n = 256;
        double[] samples = new double[n * n];
        int k = 0;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                samples[k++] = Value((x + 0.37) * 0.173, (y + 0.61) * 0.173, 12345);
            }
        }

        Array.Sort(samples);
        double[] quantiles = new double[1025];
        for (int i = 0; i < quantiles.Length; i++)
        {
            quantiles[i] = samples[Math.Min(samples.Length - 1, (int)((long)i * (samples.Length - 1) / (quantiles.Length - 1)))];
        }

        return quantiles;
    }

    /// <summary>A 32-bit integer hash of a lattice point, as a value in [0, 1).</summary>
    /// <param name="x">Lattice X.</param>
    /// <param name="y">Lattice Y.</param>
    /// <param name="seed">The seed.</param>
    public static double Hash(int x, int y, int seed)
    {
        uint h = (uint)x * 0x9E3779B1u;
        h ^= (uint)y * 0x85EBCA77u;
        h ^= (uint)seed * 0xC2B2AE3Du;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return h * (1.0 / 4294967296.0);
    }

    private static double Saturate(double v) => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;
}
