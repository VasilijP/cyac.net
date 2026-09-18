using CYAC.Port.Core.Model.Cockpit;

namespace CYAC.Port.Render.Cockpit;

/// <summary>One 1-bit overlay mask (<c>.msk</c>) — a set bit is opaque.</summary>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Height">Its height in scanlines.</param>
/// <param name="Opaque">
/// <see cref="Width"/> × <see cref="Height"/> flags, ROW MAJOR: <c>true</c> where the mask lets the
/// overlay through.
/// </param>
/// <remarks>
/// <c>gfx_masked_blit @image@0x1D162</c> is the engine's single transparency primitive and the only
/// consumer of these; the transform decodes each one to a 1-bit PNG plus the geometry it read out of
/// the cockpit region table (<c>data/images/masks/&lt;name&gt;.json</c>).
/// </remarks>
public sealed record CockpitMask(int Width, int Height, bool[] Opaque)
{
    /// <summary>Whether a mask pixel is opaque; outside the mask it is not.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    public bool At(int x, int y) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height && Opaque[(y * Width) + x];
}

/// <summary>
/// One aircraft's cockpit ART, in the game's own 320×200 design space: the panel picture, the ALPHA
/// CHANNEL that says which of its pixels overlap the 3-D viewport, the region overlay masks and the
/// layout tables that address them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The compositor pack carries no pixels of its own.</b> The 1991 engine paints the cockpit over
/// the world with a per-aircraft machine-code module out of <c>3a.lib</c>, and the transform
/// publishes that module as the set of opaque SPANS it paints plus a pixel raster.  Measured over
/// all six VGA packs, every one of those span pixels is byte-identical to the same pixel of
/// <c>&lt;suffix&gt;v.pic</c> (13,846 / 10,451 / 12,486 / 12,634 / 9,244 / 9,893 pixels, zero
/// differences), so the port keeps ONE picture and treats the span set purely as the cockpit's alpha
/// channel over the viewport band, which is exactly what it is.
/// </para>
/// <para>
/// Below the viewport the cockpit is fully opaque: <c>cockpit_panel_strip_blit @image@0x0DA26</c>
/// blits rows <c>[height, 200)</c> of the picture straight onto the page every frame (its other
/// three strips are zero-sized for every shipped aircraft, whose viewport is always
/// <c>{0, 0, 320, h}</c>).
/// </para>
/// </remarks>
public sealed class CockpitArt
{
    private readonly byte[] _picture;
    private readonly bool[] _opaque;
    private readonly byte[] _misc;
    private readonly Rgb24[] _palette;

    private CockpitArt(
        string basename,
        string assetSuffix,
        int aircraftIndex,
        PanelRect viewport,
        byte[] picture,
        bool[] opaque,
        int spanPixels,
        int inBandArtPixels,
        int leftoverSpritePixels,
        byte[] misc,
        int miscWidth,
        int miscHeight,
        IReadOnlyDictionary<string, CockpitMask> masks,
        IReadOnlyList<DialSlot> dials,
        CockpitLayout layout,
        IReadOnlyList<Rgb24> palette,
        InFlightStrings strings)
    {
        Basename = basename;
        AssetSuffix = assetSuffix;
        AircraftIndex = aircraftIndex;
        Viewport = viewport;
        _picture = picture;
        _opaque = opaque;
        SpanPixels = spanPixels;
        InBandArtPixels = inBandArtPixels;
        LeftoverSpritePixels = leftoverSpritePixels;
        _misc = misc;
        MiscWidth = miscWidth;
        MiscHeight = miscHeight;
        Masks = masks;
        Dials = dials;
        Layout = layout;
        _palette = palette as Rgb24[] ?? [.. palette];
        Palette = _palette;
        Strings = strings;
    }

    /// <summary>The design space's width — the game draws 320 columns.</summary>
    public const int Width = CockpitLayout.DesignWidth;

    /// <summary>Its height — 200 rows.</summary>
    public const int Height = CockpitLayout.DesignHeight;

    /// <summary>
    /// The palette index the artists filled the 3-D WINDOW with: <b>9</b>.
    /// </summary>
    /// <remarks>
    /// Measured, not chosen.  Take every pixel of an aircraft's picture inside its viewport band that
    /// the compositor pack does NOT paint, and index 9 is 99.9 % of them on the P-51, 94.5 % on the
    /// MiG-15, 93.3 % on the F-4, 88 % on the FW-190 and the F-86 and 79.6 % on the MiG-21 — a flat
    /// fill wherever the canopy shows sky, and never a colour the pack paints.  What is left over is
    /// COCKPIT ART inside the window band: the MiG-21's instrument shroud, the F-4's radar housing
    /// and weapon-icon strip.  The engine repaints those every frame by other means — the region
    /// draws (<c>cockpit_dispatch_state_changes @image@0x0E735</c>), the dial slots' backing-store
    /// restore (<c>dial_slot_teardown_if_dirty @image@0x01A2E</c>) and the weapon-icon walk
    /// (<c>cockpit_sprites_post_blit @image@0x0E9A8</c> over <c>[0xBC48]..[0xBC4E]</c>) — so the pack
    /// has no reason to carry them.
    /// <para>
    /// But "index ≠ 9 ⇒ cockpit" alone paints the DEAD ART the artists parked in the sky area of two
    /// pictures: seven red lever sprites and two "BRK" brake-lever states in <c>86v.pic</c> (2,405
    /// px) and two sprite blocks in <c>21v.pic</c> (2,830 px), all of which the engine blits from
    /// <c>miscv.pic</c> instead and never from the aircraft picture.  See
    /// <see cref="IsReachableFromTheSky"/> for the connectivity test that separates them.
    /// </para>
    /// </remarks>
    public const byte WindowFillerPaletteIndex = 9;

    /// <summary>The aircraft's port basename.</summary>
    public string Basename { get; }

    /// <summary>Its cockpit-art file-name suffix.</summary>
    public string AssetSuffix { get; }

    /// <summary>Its index 0..5.</summary>
    public int AircraftIndex { get; }

    /// <summary>The rectangle the 3-D world fills while the cockpit is drawn.</summary>
    public PanelRect Viewport { get; }

    /// <summary>How many pixels of the picture the compositor spans cover.</summary>
    public int SpanPixels { get; }

    /// <summary>
    /// How many further pixels inside the viewport band are cockpit ART rather than the window filler
    /// — see <see cref="WindowFillerPaletteIndex"/>.
    /// </summary>
    public int InBandArtPixels { get; }

    /// <summary>
    /// How many pixels of the picture are DEAD SPRITE STORAGE parked outside the canopy: non-filler
    /// art inside the viewport band that the flood fill reaches from the band's top edge.
    /// </summary>
    /// <remarks>
    /// 2,405 on the F-86 (seven red lever sprites at the top left, two "BRK" brake-lever states at
    /// the top right), 2,830 on the MiG-21 (two sprite blocks), and ZERO on the other four.  Both
    /// families also live in <c>miscv.pic</c>, which is the surface the region draws actually blit
    /// from (<c>region_3_flaps_draw_fn @image@0x0DD91</c> pushes <c>0x421A</c> on both its arms), so
    /// the copies inside the aircraft picture are never read by the engine.
    /// </remarks>
    public int LeftoverSpritePixels { get; }

    /// <summary>The shared instrument-sprite sheet's width.</summary>
    public int MiscWidth { get; }

    /// <summary>Its height.</summary>
    public int MiscHeight { get; }

    /// <summary>
    /// H10a fix pass — a palette index of <c>miscv.pic</c>, the SHARED instrument-sprite sheet the
    /// binary-state regions blit their lamps out of.
    /// </summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <remarks>
    /// <c>cockpit_assets_load_all</c> loads <c>"miscv.pic"</c> (<c>[0x4314]</c>, VGA) or
    /// <c>"misc.pic"</c> (<c>[0x431E]</c>) into the descriptor at <c>[0x421A]</c>
    /// (<c>image@0x0E379..0x0E390</c>), and regions 3, 4, 5 and 9 pass exactly that descriptor as
    /// <c>gfx_plain_blit</c>'s SOURCE (<c>image@0x0DD91</c>, <c>0x0DE1B</c>, <c>0x0DE6F</c>,
    /// <c>0x0E326</c>) — the aircraft's own <c>.pic</c> is the DESTINATION, <c>[0xE7EC]</c>, the
    /// screen surface.  Sampling the lamps out of the aircraft picture instead lands
    /// in its SKY, which is what made them solid blue blocks.
    /// </remarks>
    public byte MiscAt(int x, int y) =>
        (uint)x < (uint)MiscWidth && (uint)y < (uint)MiscHeight ? _misc[(y * MiscWidth) + x] : (byte)0;

    /// <summary>The region overlay masks, keyed by their suffix (<c>"horiz"</c>, <c>"radar"</c>, …).</summary>
    public IReadOnlyDictionary<string, CockpitMask> Masks { get; }

    /// <summary>The aircraft's ten dial slots.</summary>
    public IReadOnlyList<DialSlot> Dials { get; }

    /// <summary>The cockpit layout tables.</summary>
    public CockpitLayout Layout { get; }

    /// <summary>The 256-colour VGA palette the picture's indices mean.</summary>
    public IReadOnlyList<Rgb24> Palette { get; }

    /// <summary>
    /// The words and formats the cockpit, the HUD and the overlay windows print, from the same tree as
    /// the art.
    /// </summary>
    public InFlightStrings Strings { get; }

    /// <summary>
    /// One palette entry, through the backing ARRAY rather than the <see cref="Palette"/> interface.
    /// </summary>
    /// <param name="index">The palette index; anything outside 0…255 reads as black.</param>
    /// <remarks>
    /// The resamplers take four palette entries per pixel, and an <c>IReadOnlyList&lt;T&gt;</c>
    /// indexer over a <c>T[]</c> goes through the runtime's array-interface thunk, which costs many
    /// times a plain element load.  The public shape of <see cref="Palette"/> is unchanged.
    /// </remarks>
    public Rgb24 ColorAt(int index) =>
        (uint)index < (uint)_palette.Length ? _palette[index] : default;

    /// <summary>The picture's palette index at a design pixel.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    public byte IndexAt(int x, int y) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height ? _picture[(y * Width) + x] : (byte)0;

    /// <summary>Whether a design pixel is part of the cockpit rather than of the world.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    public bool IsOpaque(int x, int y) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height && _opaque[(y * Width) + x];

    /// <summary>Assembles one aircraft's art.</summary>
    /// <param name="basename">Its port basename.</param>
    /// <param name="layout">The cockpit layout tables.</param>
    /// <param name="picture">The 320×200 palette indices of <c>&lt;suffix&gt;v.pic</c>, row major.</param>
    /// <param name="spans">The compositor pack's opaque spans, each <c>[row, x, length]</c>.</param>
    /// <param name="masks">The region overlay masks by suffix.</param>
    /// <param name="dials">The aircraft's ten dial slots.</param>
    /// <param name="palette">The 256-colour VGA palette.</param>
    /// <param name="misc">The shared sprite sheet's palette indices, row major.</param>
    /// <param name="miscWidth">Its width.</param>
    /// <param name="miscHeight">Its height.</param>
    /// <param name="strings">The in-flight words and formats (<c>DataTree.InFlightStrings</c>).</param>
    /// <exception cref="ArgumentException">The picture is not 320×200.</exception>
    public static CockpitArt Create(
        string basename,
        CockpitLayout layout,
        byte[] picture,
        IReadOnlyList<IReadOnlyList<int>> spans,
        IReadOnlyDictionary<string, CockpitMask> masks,
        IReadOnlyList<DialSlot> dials,
        IReadOnlyList<Rgb24> palette,
        byte[] misc,
        int miscWidth,
        int miscHeight,
        InFlightStrings strings)
    {
        ArgumentNullException.ThrowIfNull(misc);
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(basename);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(masks);
        ArgumentNullException.ThrowIfNull(dials);
        ArgumentNullException.ThrowIfNull(palette);
        if (picture.Length != Width * Height)
        {
            throw new ArgumentException(
                $"a cockpit picture is {Width}x{Height} = {Width * Height} indices, "
                    + $"got {picture.Length}",
                nameof(picture));
        }

        int index = layout.IndexOf(basename);
        PanelRect viewport = layout.Viewports[index].Viewport;
        bool[] opaque = new bool[Width * Height];

        // Below the viewport the panel strip covers the whole width every frame
        // (cockpit_panel_strip_blit @image@0x0DA26, its fourth call).
        for (int y = viewport.Height; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                opaque[(y * Width) + x] = true;
            }
        }

        int spanPixels = 0;
        foreach (IReadOnlyList<int> run in spans)
        {
            if (run.Count < 3)
            {
                throw new ArgumentException("a compositor span is [row, x, length]", nameof(spans));
            }

            int row = run[0];
            int start = run[1];
            int length = run[2];
            for (int k = 0; k < length; k++)
            {
                int x = start + k;
                if ((uint)row >= (uint)Height || (uint)x >= (uint)Width)
                {
                    continue;
                }

                if (!opaque[(row * Width) + x])
                {
                    spanPixels++;
                }

                opaque[(row * Width) + x] = true;
            }
        }

        // The third source of opacity: cockpit ART inside the viewport band that the pack does not
        // paint because the engine repaints it by another route each frame.  Without this the F-4's
        // radar housing and the MiG-21's instrument shroud are transparent to the world.
        // …but only where the canopy ENCLOSES it.  Art the flood fill can walk
        // to from the band's top edge is outside the canopy altogether and is dead sprite storage.
        bool[] sky = IsReachableFromTheSky(opaque, viewport.Height);
        int artPixels = 0;
        int leftoverPixels = 0;
        for (int y = 0; y < viewport.Height && y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int at = (y * Width) + x;
                if (opaque[at] || picture[at] == WindowFillerPaletteIndex)
                {
                    continue;
                }

                if (sky[at])
                {
                    leftoverPixels++;
                    continue;
                }

                opaque[at] = true;
                artPixels++;
            }
        }

        return new CockpitArt(
            basename,
            layout.Viewports[index].AssetSuffix,
            index,
            viewport,
            [.. picture],
            opaque,
            spanPixels,
            artPixels,
            leftoverPixels,
            [.. misc],
            miscWidth,
            miscHeight,
            masks,
            dials,
            layout,
            palette,
            strings);
    }

    /// <summary>
    /// Which pixels of the viewport band lie OUTSIDE the canopy: the four-connected flood fill that
    /// starts on the band's top edge and may not cross a compositor span.
    /// </summary>
    /// <param name="span">The span set so far — <c>true</c> where the compositor pack paints.</param>
    /// <param name="bandHeight">The viewport's height, the band's last row + 1.</param>
    /// <remarks>
    /// <para>
    /// The compositor pack paints the canopy FRAME, an unbroken arch across the band on all six
    /// aircraft (its published spans are the arch, the rails and the coaming).  So the band splits
    /// cleanly in two: what the fill reaches is the sky OUTSIDE the arch, and what it cannot reach is
    /// the canopy's own interior plus the housings the arch encloses.  Art in the first is dead
    /// storage (<see cref="LeftoverSpritePixels"/>); art in the second is real cockpit the engine
    /// repaints by a route the port does not model (<see cref="WindowFillerPaletteIndex"/>).
    /// </para>
    /// <para>
    /// <b>Verified against the original's own frames.</b>  For each aircraft with a cockpit
    /// screenshot in <c>sources/my_screenshots</c>, a band pixel that ever equals the picture in a
    /// screenshot is cockpit and one that never does is world.  Against that oracle the rule below
    /// makes ZERO pixels transparent that the original shows as cockpit, and outside the ten region
    /// and ten dial rectangles (which the engine redraws with computed content) it leaves 9 pixels
    /// disputed on the F-86 (a captured frame of the original and four more), 5 on the MiG-21
    /// (<c>yeager_073/074</c>), 5 on the MiG-15 (<c>yeager_070/071</c>), 0 on the FW-190
    /// (<c>yeager_075..078</c>) and 259 on the F-4 (<c>yeager_061..064</c>, all of them the F9 MAP
    /// window lying over the left canopy rail).  The "≠ 9"-only rule disputed 3,008 and 2,972 on the
    /// F-86 and the MiG-21.
    /// </para>
    /// </remarks>
    private static bool[] IsReachableFromTheSky(bool[] span, int bandHeight)
    {
        int band = Math.Clamp(bandHeight, 0, Height);
        bool[] reached = new bool[Width * Height];
        Queue<int> queue = new Queue<int>();
        for (int x = 0; x < Width && band > 0; x++)
        {
            if (!span[x])
            {
                reached[x] = true;
                queue.Enqueue(x);
            }
        }

        while (queue.Count > 0)
        {
            int at = queue.Dequeue();
            int x = at % Width;
            int y = at / Width;
            Visit(x - 1, y);
            Visit(x + 1, y);
            Visit(x, y - 1);
            Visit(x, y + 1);
        }

        return reached;

        void Visit(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)band)
            {
                return;
            }

            int at = (y * Width) + x;
            if (reached[at] || span[at])
            {
                return;
            }

            reached[at] = true;
            queue.Enqueue(at);
        }
    }
}
