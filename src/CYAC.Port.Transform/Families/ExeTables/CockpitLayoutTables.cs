using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Flight;

namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>
/// The cockpit's CONSTANT layout: the per-aircraft 3-D viewport table, the seven HUD layout blocks
/// and the ten instrument regions' source tables (<c>exe/tables/cockpit_layout.json</c>).
/// </summary>
/// <remarks>
/// <para>
/// Three families the 1991 engine keeps in DGROUP and no document carried, so the port could not
/// draw a cockpit without reading <c>exe/image.l1.bin</c> (the data-tree rule):
/// </para>
/// <list type="number">
///   <item><b>The viewport table</b> <c>g_aircraft_viewport_table [0x3C02]</c> — six
///   <c>{x, y, width, height}</c> rows, copied whole into <c>g_active_viewport_rect [0xB10A]</c>
///   whenever the cockpit is drawn (<c>image@0x015E0..0x015F6</c>).</item>
///   <item><b>The HUD layout blocks</b> <c>[0x3078]</c> (full-screen default) and
///   <c>[0x309E + idx·0x26]</c> (per aircraft) — nineteen <c>u16</c> anchors each, copied into
///   <c>[0xF180..0xF1A5]</c> by one <c>rep movsw</c> (<c>image@0x0C5C0..0x0C5E2</c>).</item>
///   <item><b>The instrument-region tables</b> <c>[0x3E4A..0x4313]</c> — per region a per-aircraft
///   rectangle table (a zero width is the "this aircraft has no such instrument" gate), for four
///   regions an overlay pivot table, for the three binary-state regions an OFF/ON source-point pair,
///   plus the F-86's four combined gear+flap icons, the two countermeasure text positions and the
///   weapon+ammo colours.</item>
/// </list>
/// <para>
/// The regions' own 44-byte <c>s_cockpit_region</c> records are NOT published: they are runtime
/// state, and the only static words in them are the <c>state_fn</c>/<c>draw_fn</c> near pointers —
/// the polymorphic v-table, which the port re-implements rather than carries.  The two weapon+ammo
/// FORMAT strings are repeated as values but their bytes belong to <c>exe/strings.json</c>, which
/// already round-trips them.
/// </para>
/// </remarks>
internal sealed class CockpitLayoutExeTable : IExeTable
{
    /// <summary>The document's path in the data tree.</summary>
    public const string Path = "exe/tables/cockpit_layout.json";

    /// <summary>How many flyable aircraft the tables carry.</summary>
    public const int AircraftCount = 6;

    /// <summary><c>g_aircraft_viewport_table [0x3C02]</c> — six 8-byte rows.</summary>
    public const int ViewportTable = 0x3C02;

    /// <summary>The full-screen default HUD layout block <c>[0x3078]</c>.</summary>
    public const int HudLayoutDefault = 0x3078;

    /// <summary>The per-aircraft HUD layout blocks <c>[0x309E + idx·0x26]</c>.</summary>
    public const int HudLayoutPerAircraft = 0x309E;

    /// <summary>Bytes in one HUD layout block: nineteen <c>u16</c>.</summary>
    public const int HudLayoutBytes = 0x26;

    /// <summary>The cockpit-asset suffix near-pointer table <c>[0x4226]</c> (<c>image@0x0E3E8</c>).</summary>
    public const int SuffixPointerTable = 0x4226;

    /// <summary>The F-86's four combined gear+flap source points <c>[0x424C]</c>.</summary>
    public const int F86GearFlapRects = 0x424C;

    /// <summary>The F-4's chaff/flare text positions <c>[0x42EC]</c>.</summary>
    public const int CountermeasureTextF4 = 0x42EC;

    /// <summary>The MiG-21's chaff/flare text positions <c>[0x42F4]</c>.</summary>
    public const int CountermeasureTextMig21 = 0x42F4;

    /// <summary>The F-86-only weapon+ammo format string <c>[0x42FC]</c> (owned by the string catalogue).</summary>
    public const int WeaponAmmoFormatF86 = 0x42FC;

    /// <summary>The default weapon+ammo format string <c>[0x4300]</c> (owned by the string catalogue).</summary>
    public const int WeaponAmmoFormatDefault = 0x4300;

    /// <summary>The per-aircraft weapon+ammo foreground colour bytes <c>[0x4308]</c>.</summary>
    public const int WeaponAmmoTextColor = 0x4308;

    /// <summary>The per-aircraft weapon+ammo background colour bytes <c>[0x430E]</c>.</summary>
    public const int WeaponAmmoBackgroundColor = 0x430E;

    /// <summary>Bytes per aircraft in a region rectangle table.</summary>
    private const int RectStride = 8;

    /// <summary>Bytes per aircraft in a point table.</summary>
    private const int PointStride = 4;

    /// <summary>
    /// The ten regions, in the dispatcher's push order — name, mask suffix, and the DGROUP offsets of
    /// the tables each one indexes.
    /// </summary>
    /// <remarks>
    /// Region 9's rectangle table is a SINGLE record, not one per aircraft: it is loaded only when
    /// <c>g_active_aircraft_idx == 5</c>.  Regions 3, 4 and 5 are the binary-state indicators whose
    /// draw functions pick an OFF or ON source point from a 4-byte per-aircraft table; the others
    /// draw procedurally or as text.
    /// </remarks>
    private static readonly RegionSpec[] Regions =
    [
        new(0, "artificialHorizon", "horiz", 0x3E4A, AircraftCount, 0x3E7A, null, null),
        new(1, "radarMonitor", "radar", 0x3EBE, AircraftCount, 0x3EEE, null, null),
        new(2, "radarWarningReceiver", "rwr", 0x3F32, AircraftCount, 0x3F62, null, null),
        new(3, "flaps", null, 0x3FA6, AircraftCount, null, 0x425C, 0x4274),
        new(4, "landingGear", null, 0x405E, AircraftCount, null, 0x42BC, 0x42D4),
        new(5, "wheelBrake", null, 0x4002, AircraftCount, null, 0x428C, 0x42A4),
        new(6, "weaponAmmo", null, 0x40BA, AircraftCount, null, null, null),
        new(7, "compass", "comp", 0x4116, AircraftCount, 0x4146, null, null),
        new(8, "chaffFlare", null, 0x418A, AircraftCount, null, null, null),
        new(9, "afterburner", null, 0x41E6, 1, null, null, null),
    ];

    /// <inheritdoc/>
    public string TreePath => Path;

    /// <inheritdoc/>
    public string Description =>
        "the cockpit's constant layout: each aircraft's 3-D viewport rectangle, the seven HUD " +
        "layout blocks and the ten instrument regions' per-aircraft source tables";

    /// <inheritdoc/>
    public ExeTableResult Forward(ReadOnlySpan<byte> image)
    {
        (string Text, int Pointer)[] suffixes = ReadSuffixes(image);
        List<CockpitViewportDto> viewports = new List<CockpitViewportDto>(AircraftCount);
        for (int i = 0; i < AircraftCount; i++)
        {
            int row = ViewportTable + (i * RectStride);
            viewports.Add(new CockpitViewportDto
            {
                Aircraft = AircraftDefinition.FlyableBasenames[i],
                AssetSuffix = suffixes[i].Text,
                AssetSuffixPointer = PortHex.Format(suffixes[i].Pointer, 4),
                Dgroup = PortHex.Format(row, 4),
                Viewport = Rect(image, row),
            });
        }

        List<HudLayoutBlockDto> layouts = new List<HudLayoutBlockDto>(AircraftCount + 1)
        {
            HudBlock(image, null, HudLayoutDefault),
        };

        for (int i = 0; i < AircraftCount; i++)
        {
            layouts.Add(HudBlock(
                image,
                AircraftDefinition.FlyableBasenames[i],
                HudLayoutPerAircraft + (i * HudLayoutBytes)));
        }

        List<CockpitRegionTableDto> regions = new List<CockpitRegionTableDto>(Regions.Length);
        foreach (RegionSpec spec in Regions)
        {
            List<ScreenRectDto> rects = new List<ScreenRectDto>(spec.RectRows);
            for (int i = 0; i < spec.RectRows; i++)
            {
                rects.Add(Rect(image, spec.RectTable + (i * RectStride))!);
            }

            regions.Add(new CockpitRegionTableDto
            {
                Index = spec.Index,
                Name = spec.Name,
                MaskSuffix = spec.MaskSuffix,
                RectTableDgroup = PortHex.Format(spec.RectTable, 4),
                Rects = rects,
                PivotTableDgroup = spec.PivotTable is { } pivot ? PortHex.Format(pivot, 4) : null,
                Pivots = spec.PivotTable is { } p ? Points(image, p) : null,
                OffStateTableDgroup = spec.OffTable is { } off ? PortHex.Format(off, 4) : null,
                OffStateSource = spec.OffTable is { } o ? Points(image, o) : null,
                OnStateTableDgroup = spec.OnTable is { } on ? PortHex.Format(on, 4) : null,
                OnStateSource = spec.OnTable is { } n ? Points(image, n) : null,
            });
        }

        List<F86GearFlapIconDto> icons = new List<F86GearFlapIconDto>(4);
        for (int state = 0; state < 4; state++)
        {
            icons.Add(new F86GearFlapIconDto
            {
                GearDown = (state & 1) != 0,
                FlapDown = (state & 2) != 0,
                Source = Point(image, F86GearFlapRects + (state * PointStride)),
            });
        }

        List<CountermeasureTextDto> countermeasures = new List<CountermeasureTextDto>(2)
        {
            CountermeasureText(image, "f4", CountermeasureTextF4),
            CountermeasureText(image, "mig21", CountermeasureTextMig21),
        };

        WeaponAmmoStyleDto style = new WeaponAmmoStyleDto
        {
            TextColorDgroup = PortHex.Format(WeaponAmmoTextColor, 4),
            TextColorByAircraft = Bytes(image, WeaponAmmoTextColor),
            BackgroundColorDgroup = PortHex.Format(WeaponAmmoBackgroundColor, 4),
            BackgroundColorByAircraft = Bytes(image, WeaponAmmoBackgroundColor),
            FormatF86 = AsciiZ(image, WeaponAmmoFormatF86),
            FormatDefault = AsciiZ(image, WeaponAmmoFormatDefault),
        };

        CockpitLayoutDto dto = new CockpitLayoutDto
        {
            Format = "cyac.table.cockpitLayout/1",
            About =
                "The cockpit's constant layout, out of DGROUP. `viewports` is " +
                "g_aircraft_viewport_table [0x3C02]: the rectangle the 3-D world is drawn into while " +
                "the cockpit is on, copied into g_active_viewport_rect [0xB10A] at image@0x015F3 " +
                "(cockpit off, or any view but the forward one, gives the full screen instead - " +
                "image@0x01641). Width and height are EXTENTS: the clip setup at image@0x11876 " +
                "computes x_max = x + width - 1. Everything below the viewport is the panel strip the " +
                "engine blits out of <assetSuffix>v.pic (cockpit_panel_strip_blit @image@0x0DA26). " +
                "`hudLayouts` are the seven 38-byte blocks hud_per_frame_draw copies into " +
                "[0xF180..0xF1A5]: the per-aircraft one when the cockpit is drawn, the default when it " +
                "is not. `regions` are the ten instrument regions' per-aircraft source tables - a " +
                "zero-width rectangle means that aircraft does not have the instrument, which is the " +
                "gate cockpit_assets_load_all tests before loading its .msk overlay. The regions' " +
                "offStateSource/onStateSource points and the F-86's four icons are coordinates in " +
                "miscv.pic, the SHARED instrument-sprite sheet the loader puts in the descriptor at " +
                "[0x421A] (image@0x0E379), not in the aircraft's own picture - that one is the " +
                "blit's destination.",
            DgroupImageBase = PortHex.Format(ExeAddresses.DgroupImageBase, 5),
            Viewports = viewports,
            HudLayouts = layouts,
            Regions = regions,
            F86GearFlapIcons = icons,
            CountermeasureText = countermeasures,
            WeaponAmmo = style,
        };

        // Two words per HUD block have no reader anywhere in the image; they are carried as hex.
        int unknown = (AircraftCount + 1) * 4;
        return new ExeTableResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.CockpitLayoutDto),
            unknown,
            $"cockpit_layout: {viewports.Count} viewports, {layouts.Count} HUD blocks, "
                + $"{regions.Count} regions");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json)
    {
        CockpitLayoutDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.CockpitLayoutDto)
                               ?? throw new InvalidDataException("the cockpit-layout document is empty");

        List<CockpitViewportDto> viewports = dto.Viewports ?? [];
        if (viewports.Count != AircraftCount)
        {
            throw new InvalidDataException(
                $"expected {AircraftCount} viewport rows, found {viewports.Count}");
        }

        List<ExeSlice> slices = new List<ExeSlice>();

        byte[] viewportBytes = new byte[AircraftCount * RectStride];
        byte[] suffixPointers = new byte[AircraftCount * 2];
        for (int i = 0; i < AircraftCount; i++)
        {
            WriteRect(viewportBytes.AsSpan(i * RectStride), viewports[i].Viewport, $"viewport {i}");
            BinaryPrimitives.WriteUInt16LittleEndian(
                suffixPointers.AsSpan(i * 2),
                (ushort)PortHex.Parse(viewports[i].AssetSuffixPointer));
        }

        slices.Add(new ExeSlice(
            "aircraft viewport table", ExeAddresses.Image(ViewportTable), viewportBytes));
        slices.Add(new ExeSlice(
            "cockpit asset suffix pointers", ExeAddresses.Image(SuffixPointerTable), suffixPointers));

        List<HudLayoutBlockDto> layouts = dto.HudLayouts ?? [];
        if (layouts.Count != AircraftCount + 1)
        {
            throw new InvalidDataException(
                $"expected {AircraftCount + 1} HUD layout blocks, found {layouts.Count}");
        }

        // The default block and the six per-aircraft blocks are contiguous: 0x3078 + 0x26 = 0x309E.
        byte[] hud = new byte[(AircraftCount + 1) * HudLayoutBytes];
        for (int i = 0; i < layouts.Count; i++)
        {
            WriteHudBlock(hud.AsSpan(i * HudLayoutBytes, HudLayoutBytes), layouts[i]);
        }

        slices.Add(new ExeSlice("HUD layout blocks", ExeAddresses.Image(HudLayoutDefault), hud));

        List<CockpitRegionTableDto> regions = dto.Regions ?? [];
        if (regions.Count != Regions.Length)
        {
            throw new InvalidDataException(
                $"expected {Regions.Length} cockpit regions, found {regions.Count}");
        }

        foreach (RegionSpec spec in Regions)
        {
            CockpitRegionTableDto region = regions.Single(r => r.Index == spec.Index);
            List<ScreenRectDto> rects = region.Rects ?? [];
            if (rects.Count != spec.RectRows)
            {
                throw new InvalidDataException(
                    $"region {spec.Index} ({spec.Name}) wants {spec.RectRows} rectangles, "
                        + $"found {rects.Count}");
            }

            byte[] bytes = new byte[spec.RectRows * RectStride];
            for (int i = 0; i < rects.Count; i++)
            {
                WriteRect(bytes.AsSpan(i * RectStride), rects[i], $"region {spec.Index} rect {i}");
            }

            slices.Add(new ExeSlice(
                $"region {spec.Index} ({spec.Name}) rectangles",
                ExeAddresses.Image(spec.RectTable),
                bytes));

            AddPoints(slices, spec.PivotTable, region.Pivots, $"region {spec.Index} pivots");
            AddPoints(slices, spec.OffTable, region.OffStateSource, $"region {spec.Index} off-state");
            AddPoints(slices, spec.OnTable, region.OnStateSource, $"region {spec.Index} on-state");
        }

        List<F86GearFlapIconDto> icons = dto.F86GearFlapIcons ?? [];
        if (icons.Count != 4)
        {
            throw new InvalidDataException($"expected 4 F-86 gear/flap icons, found {icons.Count}");
        }

        byte[] iconBytes = new byte[4 * PointStride];
        for (int state = 0; state < 4; state++)
        {
            F86GearFlapIconDto icon = icons.Single(
                i => (((i.FlapDown ? 2 : 0) | (i.GearDown ? 1 : 0)) == state));
            WritePoint(iconBytes.AsSpan(state * PointStride), icon.Source, $"F-86 icon {state}");
        }

        slices.Add(new ExeSlice(
            "F-86 gear/flap icons", ExeAddresses.Image(F86GearFlapRects), iconBytes));

        foreach ((string aircraft, int dgroup) in
            new[] { ("f4", CountermeasureTextF4), ("mig21", CountermeasureTextMig21) })
        {
            CountermeasureTextDto text = (dto.CountermeasureText ?? []).Single(
                c => string.Equals(c.Aircraft, aircraft, StringComparison.Ordinal));
            byte[] bytes = new byte[PointStride * 2];
            WritePoint(bytes.AsSpan(0), text.Chaff, $"{aircraft} chaff text");
            WritePoint(bytes.AsSpan(PointStride), text.Flare, $"{aircraft} flare text");
            slices.Add(new ExeSlice(
                $"{aircraft} countermeasure text positions", ExeAddresses.Image(dgroup), bytes));
        }

        WeaponAmmoStyleDto style = dto.WeaponAmmo
                                   ?? throw new InvalidDataException("the document carries no weapon+ammo style");
        slices.Add(new ExeSlice(
            "weapon+ammo text colours",
            ExeAddresses.Image(WeaponAmmoTextColor),
            ToBytes(style.TextColorByAircraft, "weapon+ammo text colours")));
        slices.Add(new ExeSlice(
            "weapon+ammo background colours",
            ExeAddresses.Image(WeaponAmmoBackgroundColor),
            ToBytes(style.BackgroundColorByAircraft, "weapon+ammo background colours")));

        return slices;
    }

    private static void AddPoints(
        List<ExeSlice> slices, int? table, List<ScreenPointDto>? points, string what)
    {
        if (table is not { } dgroup)
        {
            return;
        }

        List<ScreenPointDto> list = points ?? [];
        if (list.Count != AircraftCount)
        {
            throw new InvalidDataException(
                $"{what} wants {AircraftCount} points, found {list.Count}");
        }

        byte[] bytes = new byte[AircraftCount * PointStride];
        for (int i = 0; i < list.Count; i++)
        {
            WritePoint(bytes.AsSpan(i * PointStride), list[i], $"{what}[{i}]");
        }

        slices.Add(new ExeSlice(what, ExeAddresses.Image(dgroup), bytes));
    }

    private static byte[] ToBytes(List<int>? values, string what)
    {
        List<int> list = values ?? [];
        if (list.Count != AircraftCount)
        {
            throw new InvalidDataException($"{what} wants {AircraftCount} bytes, found {list.Count}");
        }

        byte[] bytes = new byte[AircraftCount];
        for (int i = 0; i < list.Count; i++)
        {
            bytes[i] = (byte)list[i];
        }

        return bytes;
    }

    private static void WriteRect(Span<byte> target, ScreenRectDto? rect, string what)
    {
        ScreenRectDto value = rect ?? throw new InvalidDataException($"{what} has no rectangle");
        BinaryPrimitives.WriteUInt16LittleEndian(target, (ushort)value.X);
        BinaryPrimitives.WriteUInt16LittleEndian(target[2..], (ushort)value.Y);
        BinaryPrimitives.WriteUInt16LittleEndian(target[4..], (ushort)value.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(target[6..], (ushort)value.Height);
    }

    private static void WritePoint(Span<byte> target, ScreenPointDto? point, string what)
    {
        ScreenPointDto value = point ?? throw new InvalidDataException($"{what} has no point");
        BinaryPrimitives.WriteUInt16LittleEndian(target, (ushort)value.X);
        BinaryPrimitives.WriteUInt16LittleEndian(target[2..], (ushort)value.Y);
    }

    private static void WriteHudBlock(Span<byte> target, HudLayoutBlockDto block)
    {
        ScreenRectDto clip = block.InnerClip
                             ?? throw new InvalidDataException(
                                 $"HUD layout block {block.Dgroup} has no inner clip rectangle");
        int[] words =
        [
            block.FlagsIndicatorX, block.FlagsIndicatorColumn,
            block.AltitudeAnchorX, block.AltitudeAnchorY,
            block.WaypointAnchorX, block.WaypointAnchorY,
            block.VsiAnchorX, block.VsiAnchorY,
            block.HeadingAnchorY, block.WaypointSecondY,
            block.TargetMarkerClipLeft, block.TargetMarkerClipRight,
            block.MessageLineY,
            clip.X, clip.Y, clip.Width, clip.Height,
        ];

        for (int i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(target[(i * 2)..], (ushort)words[i]);
        }

        PortHex.BytesOrZero(block.Unknown0x22, 2).CopyTo(target[0x22..]);
        PortHex.BytesOrZero(block.Unknown0x24, 2).CopyTo(target[0x24..]);
    }

    private static HudLayoutBlockDto HudBlock(ReadOnlySpan<byte> image, string? aircraft, int dgroup)
    {
        int at = ExeAddresses.Image(dgroup);
        ushort[] words = new ushort[17];
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt16LittleEndian(image[(at + (i * 2))..]);
        }

        return new HudLayoutBlockDto
        {
            Aircraft = aircraft,
            Dgroup = PortHex.Format(dgroup, 4),
            FlagsIndicatorX = words[0],
            FlagsIndicatorColumn = words[1],
            AltitudeAnchorX = words[2],
            AltitudeAnchorY = words[3],
            WaypointAnchorX = words[4],
            WaypointAnchorY = words[5],
            VsiAnchorX = words[6],
            VsiAnchorY = words[7],
            HeadingAnchorY = words[8],
            WaypointSecondY = words[9],
            TargetMarkerClipLeft = words[10],
            TargetMarkerClipRight = words[11],
            MessageLineY = words[12],
            InnerClip = new ScreenRectDto
            {
                X = words[13],
                Y = words[14],
                Width = words[15],
                Height = words[16],
            },
            Unknown0x22 = Convert.ToHexString(image.Slice(at + 0x22, 2)),
            Unknown0x24 = Convert.ToHexString(image.Slice(at + 0x24, 2)),
        };
    }

    private static CountermeasureTextDto CountermeasureText(
        ReadOnlySpan<byte> image, string aircraft, int dgroup) =>
        new()
        {
            Aircraft = aircraft,
            Dgroup = PortHex.Format(dgroup, 4),
            Chaff = Point(image, dgroup),
            Flare = Point(image, dgroup + PointStride),
        };

    private static List<int> Bytes(ReadOnlySpan<byte> image, int dgroup)
    {
        List<int> values = new List<int>(AircraftCount);
        for (int i = 0; i < AircraftCount; i++)
        {
            values.Add(image[ExeAddresses.Image(dgroup) + i]);
        }

        return values;
    }

    private static List<ScreenPointDto> Points(ReadOnlySpan<byte> image, int dgroup)
    {
        List<ScreenPointDto> points = new List<ScreenPointDto>(AircraftCount);
        for (int i = 0; i < AircraftCount; i++)
        {
            points.Add(Point(image, dgroup + (i * PointStride)));
        }

        return points;
    }

    private static ScreenPointDto Point(ReadOnlySpan<byte> image, int dgroup)
    {
        int at = ExeAddresses.Image(dgroup);
        return new ScreenPointDto
        {
            X = BinaryPrimitives.ReadUInt16LittleEndian(image[at..]),
            Y = BinaryPrimitives.ReadUInt16LittleEndian(image[(at + 2)..]),
        };
    }

    private static ScreenRectDto Rect(ReadOnlySpan<byte> image, int dgroup)
    {
        int at = ExeAddresses.Image(dgroup);
        return new ScreenRectDto
        {
            X = BinaryPrimitives.ReadUInt16LittleEndian(image[at..]),
            Y = BinaryPrimitives.ReadUInt16LittleEndian(image[(at + 2)..]),
            Width = BinaryPrimitives.ReadUInt16LittleEndian(image[(at + 4)..]),
            Height = BinaryPrimitives.ReadUInt16LittleEndian(image[(at + 6)..]),
        };
    }

    private static string AsciiZ(ReadOnlySpan<byte> image, int dgroup)
    {
        int at = ExeAddresses.Image(dgroup);
        StringBuilder text = new System.Text.StringBuilder(16);
        for (int i = 0; i < 32; i++)
        {
            byte b = image[at + i];
            if (b == 0)
            {
                return text.ToString();
            }

            if (b is < 0x20 or > 0x7E)
            {
                throw new InvalidDataException(
                    $"DGROUP 0x{dgroup:X4} is not a printable NUL-terminated string");
            }

            text.Append((char)b);
        }

        throw new InvalidDataException($"DGROUP 0x{dgroup:X4} has no terminator within 32 bytes");
    }

    // The six cockpit-asset suffixes, read through the near-pointer table the loader uses, so the
    // order comes from the image and never from a transcription.
    private static (string Text, int Pointer)[] ReadSuffixes(ReadOnlySpan<byte> image)
    {
        (string, int)[] suffixes = new (string, int)[AircraftCount];
        for (int i = 0; i < AircraftCount; i++)
        {
            int pointer = BinaryPrimitives.ReadUInt16LittleEndian(
                image[(ExeAddresses.Image(SuffixPointerTable) + (i * 2))..]);
            suffixes[i] = (AsciiZ(image, pointer), pointer);
        }

        return suffixes;
    }

    private sealed record RegionSpec(
        int Index,
        string Name,
        string? MaskSuffix,
        int RectTable,
        int RectRows,
        int? PivotTable,
        int? OffTable,
        int? OnTable);
}
