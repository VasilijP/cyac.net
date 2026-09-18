using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Cockpit;
using CYAC.Port.Render;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host;

/// <summary>
/// Loads one aircraft's cockpit out of the data tree: the panel picture, the compositor pack's alpha
/// channel, the region overlay masks, the dial slots and the game's own font.
/// </summary>
/// <remarks>
/// <para>
/// Everything comes from documents (the data-tree rule): <c>images/&lt;suffix&gt;v.json</c> +
/// <c>.png</c> for the picture, <c>cockpits/&lt;suffix&gt;_vga.json</c> for the spans,
/// <c>images/masks/&lt;suffix&gt;_&lt;region&gt;.json</c> for the overlays,
/// <c>exe/tables/cockpit_layout.json</c> for the geometry, <c>dialinit.json</c> for the dials.
/// Nothing here opens <c>exe/image.l1.bin</c>.
/// </para>
/// <para>
/// VGA only.  <c>g_cfg_sub_mode [0x015E]</c> ships as 6 and the port is 320×200-only by decision
/// (memory: "CYAC is 320×200-only"); the CGA/EGA/Tandy packs are carried in the tree but never read.
/// </para>
/// </remarks>
public static class CockpitAssets
{
    /// <summary>The video-mode tag the port reads packs for.</summary>
    public const string VideoMode = "vga";

    /// <summary>The font the cockpit's text instruments are drawn with.</summary>
    /// <remarks>
    /// The smallest of the three the game ships, and the one whose 6-scanline cell fits the region
    /// rectangles the tables give (region 6's is 36×6).
    /// </remarks>
    public const string FontName = "4x6";

    /// <summary>The font the in-flight ESC menu bar is drawn with.</summary>
    /// <remarks>
    /// <b>Measured, not guessed.</b>  <c>1b.lib/propbold.fnt</c> is the only one of the three
    /// shipped fonts whose metrics reproduce the six title boxes and the six popup widths of
    /// a captured frame of the original exactly (8 + width + 8 per title; 24 + widest row
    /// per popup), and its codepoints 0x14/0x15/0x16 are the right-arrow, the 8-pixel blank gutter
    /// and the check mark the item records' marker byte selects.  The cockpit's own instruments use
    /// <see cref="FontName"/>, which is a different, smaller font.
    /// </remarks>
    public const string MenuFontName = "propbold";

    /// <summary>Loads one aircraft's cockpit art.</summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="basename">The aircraft's port basename, e.g. <c>"p51"</c>.</param>
    /// <param name="palette">The game's 256-colour VGA palette, already widened to 8 bits.</param>
    /// <returns>The art, or null when the tree does not carry this aircraft's cockpit.</returns>
    public static CockpitArt? Load(DataTree tree, string basename, IReadOnlyList<Rgb24> palette)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(basename);
        ArgumentNullException.ThrowIfNull(palette);

        try
        {
            CockpitLayout layout = CockpitLayout.Load(tree);
            DialLayout dials = DialLayout.Load(tree);
            int index = layout.IndexOf(basename);
            string suffix = layout.Viewports[index].AssetSuffix;

            IndexedImage picture = tree.ImagePixels($"images/{suffix}v.json");
            if (picture.Width != CockpitArt.Width || picture.Height != CockpitArt.Height)
            {
                throw new InvalidDataException(
                    $"images/{suffix}v.png is {picture.Width}x{picture.Height}, expected "
                        + $"{CockpitArt.Width}x{CockpitArt.Height}");
            }

            CockpitPackDocumentDto pack = tree.CockpitPack(suffix, VideoMode);
            List<IReadOnlyList<int>> spans = new List<IReadOnlyList<int>>(pack.Runs?.Count ?? 0);
            foreach (List<int> run in pack.Runs ?? [])
            {
                spans.Add(run);
            }

            Dictionary<string, CockpitMask> masks = new Dictionary<string, CockpitMask>(StringComparer.Ordinal);
            foreach (CockpitRegionLayout region in layout.Regions)
            {
                if (region.MaskSuffix is not { } maskSuffix)
                {
                    continue;
                }

                string path = $"images/masks/{suffix}_{maskSuffix}.json";
                if (!tree.Info.Has(path))
                {
                    continue;
                }

                IndexedImage pixels = tree.ImagePixels(path);
                bool[] opaque = new bool[pixels.Width * pixels.Height];
                for (int i = 0; i < opaque.Length; i++)
                {
                    opaque[i] = pixels.Indices[i] != 0;
                }

                masks[maskSuffix] = new CockpitMask(pixels.Width, pixels.Height, opaque);
            }

            // H10a fix pass — miscv.pic is the SHARED instrument-sprite sheet the binary-state
            // regions blit their lamps out of (CockpitArt.MiscAt).
            IndexedImage misc = tree.ImagePixels("images/miscv.json");

            // The ONE run-time patch cockpit_layout_load_per_aircraft makes to the shipped
            // records: slot 6's param1 is the aircraft's fuel capacity, scaled
            // (image@0x01EE8..0x01F12).  Without it the gauge's range is 0..0 and the needle is
            // pinned at its offset — see FuelGauge.
            DialSlot[] slots = dials.For(index).ToArray();
            slots[FuelGauge.Slot] = FuelGauge.Patch(
                in slots[FuelGauge.Slot], index, tree.Aircraft[basename].InitialFuelUnits);

            return CockpitArt.Create(
                basename, layout, [.. picture.Indices], spans, masks, slots, palette,
                [.. misc.Indices], misc.Width, misc.Height, tree.InFlightStrings);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine(
                $"cockpit art for '{basename}' not available ({error.Message}); flying without it.");
            return null;
        }
    }

    /// <summary>
    /// Loads the canopy hit decal: <c>bullet.pic</c> masked through <c>bulletm.msk</c>.
    /// </summary>
    /// <param name="tree">The data tree.</param>
    /// <returns>The decal, or null when the tree does not carry it.</returns>
    /// <remarks>
    /// <c>image@0x0CD03..0x0CD22</c> loads exactly these two assets, named at DGROUP
    /// <c>[0x31AE] = "bullet.pic"</c> and <c>[0x31B9] = "bulletm.msk"</c>, into the descriptor
    /// <c>[0x31C6]</c> that <c>hud_damage_indicator_ring_draw</c> blits from at 0x30 × 0x26.
    /// </remarks>
    public static HudDecal? LoadHitDecal(DataTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        try
        {
            IndexedImage pixels = tree.ImagePixels("images/bullet.json");
            IndexedImage mask = tree.ImagePixels("images/masks/bulletm.json");
            if (mask.Width != pixels.Width || mask.Height != pixels.Height)
            {
                throw new InvalidDataException(
                    $"bulletm.msk is {mask.Width}x{mask.Height} but bullet.pic is "
                        + $"{pixels.Width}x{pixels.Height}");
            }

            bool[] opaque = new bool[mask.Indices.Length];
            for (int i = 0; i < opaque.Length; i++)
            {
                opaque[i] = mask.Indices[i] != 0;
            }

            return new HudDecal(pixels.Width, pixels.Height, [.. pixels.Indices], opaque);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine(
                $"the canopy hit decal is not available ({error.Message}); flying without it.");
            return null;
        }
    }

    /// <summary>Loads the game's own bitmap font.</summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="name">Its file stem.</param>
    /// <returns>The font, or null when the tree does not carry it.</returns>
    public static CockpitFont? LoadFont(DataTree tree, string name = FontName)
    {
        ArgumentNullException.ThrowIfNull(tree);
        try
        {
            FontDocumentDto document = tree.Font(name);
            string strip = document.Strip
                ?? throw new InvalidDataException($"images/fonts/{name}.json names no strip");

            // The font document names its image "strip", not "pixels", so it is loaded directly
            // rather than through DataTree.ImagePixels.
            IndexedImage pixels = IndexedPng.Load(tree.Resolve(strip));

            return CockpitFont.Create(
                pixels.Width,
                pixels.Height,
                [.. pixels.Indices],
                document.ColumnOffsets
                    ?? throw new InvalidDataException(
                        $"images/fonts/{name}.json has no column table"));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine(
                $"font '{name}' not available ({error.Message}); the cockpit's text instruments "
                    + "will be blank.");
            return null;
        }
    }
}
