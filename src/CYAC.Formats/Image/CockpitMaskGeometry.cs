using System.Text;

namespace CYAC.Formats.Image;

/// <summary>
/// Where a per-aircraft cockpit <c>.msk</c> mask's width and height come from: a rectangle read out
/// of the layer-1 image's DGROUP, not out of the file.
/// </summary>
/// <param name="Aircraft">The aircraft's file-name suffix, e.g. <c>"21"</c>.</param>
/// <param name="AircraftIndex">Its index in the suffix table (0..5).</param>
/// <param name="Region">The cockpit region key, e.g. <c>"radar"</c>.</param>
/// <param name="TableDgroupOffset">The rectangle's DGROUP offset — the citation for the numbers.</param>
/// <param name="X">The region's screen X (rect word 0).</param>
/// <param name="Y">The region's screen Y (rect word 1).</param>
/// <param name="Width">The mask's width in pixels (rect word 2).</param>
/// <param name="Height">The mask's height in scanlines (rect word 3).</param>
public sealed record CockpitMaskRect(
    string Aircraft,
    int AircraftIndex,
    string Region,
    int TableDgroupOffset,
    int X,
    int Y,
    int Width,
    int Height);

/// <summary>
/// Reads the per-aircraft cockpit region tables out of the layer-1 image so that a headerless
/// <c>.msk</c> can be given its geometry.
/// </summary>
/// <remarks>
/// <para><b>How the game does it.</b> <c>cockpit_assets_load_all</c> builds each mask's file name
/// by <c>strcat</c>-ing an aircraft suffix onto <c>"_radar.msk"</c> / <c>"_rwr.msk"</c> /
/// <c>"_horiz.msk"</c> / <c>"_comp.msk"</c> and calls <c>cockpit_mask_load_via_concat</c>
/// (<c>image@0x0DAE7</c>) with <c>g_active_aircraft_idx * 8 + BASE</c> — a <b>4-word rectangle</b>
/// <c>{x, y, width, height}</c> per aircraft.  The function copies that rectangle into the region
/// struct's <c>clip_x1..clip_y2</c> and passes words 2 and 3 to the blitter as the blit extent
/// (<c>image@0x0DB3A</c>/<c>0x0DB3D</c>), so they are the mask's own pixel dimensions.</para>
///
/// <para><b>The bases</b> are the immediates at the four call sites that build a file name:
/// horizon <c>image@0x0E576</c>, radar <c>image@0x0E433</c>, RWR <c>image@0x0E4C3</c>, compass
/// <c>image@0x0E680</c>.  The aircraft suffix table is the near-pointer array at
/// <c>DGROUP+0x4226</c> (<c>image@0x0E3E8</c>), read here rather than hard-coded so the
/// aircraft-index order comes from the image.</para>
///
/// <para><b>Not to be confused with the 2-word tables</b> at <c>DGROUP+0x3E7A</c> / <c>0x3EEE</c> /
/// <c>0x3F62</c> / <c>0x4146</c> (indexed <c>idx * 4</c>), which the same call passes as its
/// <c>descriptor</c> argument.  names the last of those <c>g_cockpit_region_7_compass_pivot</c> and
/// it is a screen-space <b>pivot point</b>, not a size: for the F-4 radar it holds (160, 136) while
/// the rectangle is (144, 113, 32, 25) — the pivot is a point inside the rectangle, and a 160×136
/// mask would need 2,720 bytes where the file has 100.</para>
/// </remarks>
public static class CockpitMaskGeometry
{
    /// <summary>Image offset of DGROUP's base (§ Address conventions).</summary>
    public const int DgroupImageOffset = 0x3BD60;

    /// <summary>DGROUP offset of the aircraft-suffix near-pointer table (<c>image@0x0E3E8</c>).</summary>
    public const int SuffixTableDgroupOffset = 0x4226;

    /// <summary>How many aircraft the cockpit tables carry.</summary>
    public const int AircraftCount = 6;

    /// <summary>Bytes per aircraft in a region rectangle table: four u16 words.</summary>
    public const int RectStride = 8;

    /// <summary>
    /// The region-rectangle table base for each mask-name suffix, keyed by the file-name stem the
    /// game concatenates.
    /// </summary>
    public static IReadOnlyDictionary<string, int> RegionRectTables { get; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["horiz"] = 0x3E4A,   // region 0, image@0x0E576
            ["radar"] = 0x3EBE,   // region 1, image@0x0E433
            ["rwr"] = 0x3F32,     // region 2, image@0x0E4C3
            ["comp"] = 0x4116,    // region 7, image@0x0E680
        };

    /// <summary>Reads the six aircraft file-name suffixes out of the image.</summary>
    /// <param name="image">The layer-1 image at load segment 0x1000.</param>
    /// <exception cref="InvalidDataException">The image is too short or a suffix is not printable ASCII.</exception>
    public static IReadOnlyList<string> ReadAircraftSuffixes(ReadOnlySpan<byte> image)
    {
        List<string> suffixes = new List<string>(AircraftCount);
        for (int i = 0; i < AircraftCount; i++)
        {
            int pointer = ReadWord(image, SuffixTableDgroupOffset + (i * 2));
            StringBuilder text = new System.Text.StringBuilder(4);
            for (int k = 0; k < 8; k++)
            {
                int at = DgroupImageOffset + pointer + k;
                if (at >= image.Length)
                {
                    throw new InvalidDataException(
                        $"aircraft suffix {i}: DGROUP+0x{pointer:X4} runs past the image");
                }

                byte b = image[at];
                if (b == 0)
                {
                    break;
                }

                if (b is < 0x20 or > 0x7E)
                {
                    throw new InvalidDataException(
                        $"aircraft suffix {i} at DGROUP+0x{pointer:X4} is not printable ASCII");
                }

                text.Append((char)b);
            }

            if (text.Length == 0)
            {
                throw new InvalidDataException($"aircraft suffix {i} at DGROUP+0x{pointer:X4} is empty");
            }

            suffixes.Add(text.ToString());
        }

        return suffixes;
    }

    /// <summary>
    /// Resolves a mask file name of the form <c>&lt;aircraft&gt;_&lt;region&gt;.msk</c> to its
    /// rectangle, or returns <see langword="null"/> when the name is not one of those.
    /// </summary>
    /// <param name="image">The layer-1 image at load segment 0x1000.</param>
    /// <param name="maskName">The EALIB member name, e.g. <c>"21_radar.msk"</c>.</param>
    public static CockpitMaskRect? TryResolve(ReadOnlySpan<byte> image, string maskName)
    {
        ArgumentNullException.ThrowIfNull(maskName);
        string stem = System.IO.Path.GetFileNameWithoutExtension(maskName);
        int split = stem.IndexOf('_', StringComparison.Ordinal);
        if (split <= 0 || split == stem.Length - 1)
        {
            return null;
        }

        string aircraft = stem[..split];
        string region = stem[(split + 1)..];
        if (!RegionRectTables.TryGetValue(region, out int tableBase))
        {
            return null;
        }

        IReadOnlyList<string> suffixes = ReadAircraftSuffixes(image);
        int index = -1;
        for (int i = 0; i < suffixes.Count; i++)
        {
            if (string.Equals(suffixes[i], aircraft, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return null;
        }

        int offset = tableBase + (index * RectStride);
        return new CockpitMaskRect(
            suffixes[index],
            index,
            region.ToLowerInvariant(),
            offset,
            ReadWord(image, offset),
            ReadWord(image, offset + 2),
            ReadWord(image, offset + 4),
            ReadWord(image, offset + 6));
    }

    private static int ReadWord(ReadOnlySpan<byte> image, int dgroupOffset)
    {
        int at = DgroupImageOffset + dgroupOffset;
        if (at + 1 >= image.Length)
        {
            throw new InvalidDataException(
                $"DGROUP+0x{dgroupOffset:X4} (image@0x{at:X5}) is past the end of the " +
                $"{image.Length}-byte image");
        }

        return image[at] | (image[at + 1] << 8);
    }
}
