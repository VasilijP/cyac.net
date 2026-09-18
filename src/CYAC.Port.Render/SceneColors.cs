namespace CYAC.Port.Render;

/// <summary>An 8-bit-per-channel colour.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <remarks>
/// The port renders in true colour (D2), so a colour is an RGB triple, not a palette index.  Where
/// a colour comes from the game's own VGA palette its 6-bit DAC components are widened by
/// <see cref="FromVga6"/>.
/// </remarks>
public readonly record struct Rgb24(byte R, byte G, byte B)
{
    /// <summary>
    /// Widens a VGA DAC triple (6 bits per channel, 0..63) to 8 bits, the way a VGA DAC does.
    /// </summary>
    /// <param name="r">Red, 0..63.</param>
    /// <param name="g">Green, 0..63.</param>
    /// <param name="b">Blue, 0..63.</param>
    /// <remarks>
    /// <c>round(v * 255 / 63)</c>.  Checked against captures of the original: palette
    /// entry 120 is <c>(0, 35, 57)</c> in <c>data/palettes/palette.json</c> and the sky pixels of
    /// a captured frame of the original are exactly <c>(0, 142, 231)</c> — the same colour.
    /// </remarks>
    public static Rgb24 FromVga6(int r, int g, int b) => new(
        (byte)(((r * 255) + 31) / 63),
        (byte)(((g * 255) + 31) / 63),
        (byte)(((b * 255) + 31) / 63));
}

/// <summary>
/// The two colours the scaffold horizon fills, and where they come from.
/// </summary>
/// <param name="Sky">Everything above the horizon line.</param>
/// <param name="Ground">Everything below it.</param>
/// <remarks>
/// <para>
/// The original fills exactly two spans split by a slanted line, with the colour pair taken from the
/// mesh registry record rather than from a global — so there is no "the sky colour" byte in the image
/// to cite.
/// </para>
/// <para>
/// The two values come from PRIMARY runtime evidence instead: in captured frames of the original
/// the sky pixels are all exactly <c>#008EE7</c> and the grass-green ground is <c>#71B600</c>; both are exact 6→8 widenings of entries in the shipped VGA palette
/// (<c>asset:1a/palette</c> → <c>data/palettes/palette.json</c>): <b>index 120</b> (and its duplicate
/// 224) = <c>(0, 35, 57)</c> = sky, <b>index 254</b> = <c>(28, 45, 0)</c> = ground.
/// </para>
/// <para>
/// <b>(open)</b> The ground colour is theatre-dependent — the Edwards desert of
/// a captured frame of the original shows a pale sand instead
/// (<c>#D7DBCB</c> / <c>#C7D3A6</c>), and which mesh or theatre datum selects it is still open.
/// </para>
/// </remarks>
/// <param name="HorizonRamp">
/// The <b>horizon band</b>: the 31 palette entries the original ramps between sky and ground,
/// SKY-FIRST.  Empty falls back to a straight two-colour interpolation.  See
/// <see cref="SceneColors.RampFirstPaletteIndex"/>.
/// </param>
public readonly record struct SceneColors(
    Rgb24 Sky, Rgb24 Ground, IReadOnlyList<Rgb24>? HorizonRamp = null)
{
    /// <summary>VGA palette index of the sky colour in the shipped palette (its duplicate is 224).</summary>
    public const int SkyPaletteIndex = 120;

    /// <summary>VGA palette index of the green ground colour in the shipped palette.</summary>
    public const int GroundPaletteIndex = 254;

    /// <summary>
    /// First palette index of the HORIZON RAMP: <b>224</b>, the sky end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The original's horizon gradient is not a dither and not a blend — it is a source scanline of
    /// PALETTE INDICES that the gradient rasteriser scales onto the screen.  The scanline lives at
    /// segment <c>0x4493</c> offset 0 = <c>image@0x34930</c>:
    /// <c>00 FE FE FD FD FC FC … E1 E1 E0 E0 00</c> — indices <c>0xFE</c> (254) down to <c>0xE0</c>
    /// (224), each repeated twice.  Its only reference image-wide is <c>image@0x18C7C</c>
    /// (<c>mov cx,0x4493</c>) inside the <c>g_dithered_horizon_flag [0x781]</c> arm (§3b,
    /// <c>image@0x18C10</c>) of <c>mesh_billboard_bbox_fill @image@0x1892C</c>, which hands it to
    /// <c>gfx_scaled_span_dispatch @image@0x18EA6</c> → the twin rasterisers
    /// <c>gfx_span_fill_ega_scaled @image@0x17D1A</c> / <c>gfx_sprite_blit_scanline_ega
    /// @image@0x1799E</c> that:1841 already calls "the SKY/GROUND HORIZON
    /// GRADIENT".
    /// </para>
    /// <para>
    /// The anchors are exact: palette <b>224</b> is byte-identical to palette 120 (the pinned sky) and
    /// palette <b>254</b> is the pinned ground, so the ramp runs sky → near-white → grass with no
    /// discontinuity at either end.  In true colour the port interpolates the same 31 anchors instead
    /// of stepping them (represent, don't reproduce).
    /// </para>
    /// </remarks>
    public const int RampFirstPaletteIndex = 224;

    /// <summary>How many palette entries the ramp spans: 224..254 inclusive = 31.</summary>
    public const int RampLength = GroundPaletteIndex - RampFirstPaletteIndex + 1;

    /// <summary>The shipped sky / green-ground pair — see the type's remarks for the derivation.</summary>
    public static SceneColors Default { get; } = new(
        Rgb24.FromVga6(0, 35, 57),
        Rgb24.FromVga6(28, 45, 0),
        null);

    /// <summary>The colour at a fraction through the band, 0 = sky … 1 = ground.</summary>
    /// <param name="t">The fraction; clamped.</param>
    public Rgb24 BandColor(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        IReadOnlyList<Rgb24>? ramp = HorizonRamp;
        if (ramp is null || ramp.Count < 2)
        {
            return Mix(Sky, Ground, t);
        }

        double at = t * (ramp.Count - 1);
        int i = Math.Min((int)at, ramp.Count - 2);
        return Mix(ramp[i], ramp[i + 1], at - i);
    }

    /// <summary>Builds the ramp view of a 256-entry palette.</summary>
    /// <param name="palette">The game's palette, already widened to 8 bits.</param>
    /// <exception cref="ArgumentException">The palette is not 256 entries.</exception>
    public static Rgb24[] RampFrom(IReadOnlyList<Rgb24> palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (palette.Count != 256)
        {
            throw new ArgumentException(
                $"a VGA palette has 256 entries, got {palette.Count}", nameof(palette));
        }

        Rgb24[] ramp = new Rgb24[RampLength];
        for (int i = 0; i < RampLength; i++)
        {
            ramp[i] = palette[RampFirstPaletteIndex + i];
        }

        return ramp;
    }

    private static Rgb24 Mix(Rgb24 a, Rgb24 b, double t) => new(
        (byte)Math.Clamp(Math.Round((a.R * (1 - t)) + (b.R * t)), 0, 255),
        (byte)Math.Clamp(Math.Round((a.G * (1 - t)) + (b.G * t)), 0, 255),
        (byte)Math.Clamp(Math.Round((a.B * (1 - t)) + (b.B * t)), 0, 255));
}
