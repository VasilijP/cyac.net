using System.Text.Json;
using CYAC.Formats.Cockpit;
using CYAC.Port.Core.Data;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// The 30 cockpit compositor packs of <c>3a.lib</c> → <c>cockpits/&lt;aircraft&gt;_&lt;mode&gt;.json</c>
/// plus, for the two modes whose generator is solved, the cockpit itself as an indexed PNG.
/// </summary>
/// <remarks>
/// <para>
/// A pack is <b>machine code</b> (the code-is-not-data rule): a straight-line paint program the shipping
/// tools generated from a cockpit bitmap, LCALLed once per frame with the draw page's segment
/// (<c>cockpit_sprites_post_blit @image@0x0E9A8</c>).  So the transform does not "convert" it — it
/// recovers the PARAMETERS it was generated from, which is what the port's cockpit renderer wants:
/// the opaque spans (the cockpit's alpha channel over the 3-D viewport) and their palette indices.
/// </para>
/// <para>
/// <b>What is proved and what is not.</b> For VGA (Mode-X) and MCGA the generator is solved
/// constructively: parse → spans → regenerate reproduces all 12 packs byte-for-byte, so those documents
/// are the pack.  The CGA and TANDY packs drive dormant modes and the EGA packs are a different program
/// family carrying the unresolved <c>mov ds,[bp+6]</c> anomaly (needs a sub-mode-1 recording): for those
/// six-plus-twelve the document carries the region census that does hold — header entry points,
/// pixel-data extent, element list — and the regions it cannot explain travel verbatim in sibling files,
/// counted.
/// </para>
/// </remarks>
public sealed class CockpitTransform : IFamilyTransform
{
    /// <summary>The family name <c>--only</c> matches and the manifest records.</summary>
    public const string FamilyName = "cockpit";

    /// <summary>The data-tree folder this family writes into.</summary>
    public const string Folder = "cockpits";

    /// <summary>The archive the packs live in.</summary>
    public const string PackArchive = "3a.lib";

    /// <summary>
    /// The <c>g_cfg_sub_mode [0x15E]</c> value that selects each mode's pack, from the loader's
    /// suffix table <c>g_weapon_icon_mode_suffix_table [0x4372]</c>.
    /// </summary>
    public static IReadOnlyDictionary<CockpitPackMode, int> SubModes { get; } =
        new Dictionary<CockpitPackMode, int>
        {
            [CockpitPackMode.Cga] = 0,
            [CockpitPackMode.Ega] = 1,
            [CockpitPackMode.Mcga] = 4,
            [CockpitPackMode.Tandy] = 5,
            [CockpitPackMode.Vga] = 6,
        };

    /// <inheritdoc/>
    public string Family => FamilyName;

    /// <inheritdoc/>
    public string TreeDescription =>
        "`cockpits/<aircraft>_<mode>.json` — a cockpit compositor pack as the parameters it was " +
        "generated from: the opaque spans it paints over the 3-D viewport, the element count and " +
        "the module's entry points. For VGA and MCGA the sibling `.png` holds the cockpit's palette " +
        "indices and the pair regenerates the pack's machine code byte for byte; the dormant CGA/" +
        "TANDY modes and the EGA family (whose program is still open) carry their pixel data and " +
        "code verbatim beside the document, counted.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => new(
        OutputFidelity.Exact,
        "VGA and MCGA packs are regenerated from the cockpit bitmap; the other modes' unmodelled " +
        "regions are carried verbatim, so every pack rebuilds exactly either way");

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is not null
            && source.Extension == ".bin"
            && string.Equals(source.OriginFile, PackArchive, StringComparison.OrdinalIgnoreCase)
            && CockpitPackParser.TryModeOf(source.Name, out _);
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        if (!CockpitPackParser.TryModeOf(source.Name, out CockpitPackMode mode))
        {
            throw new InvalidDataException($"{source.Name} does not name a cockpit mode");
        }

        ReadOnlySpan<byte> body = source.Content.Span;
        CockpitPackLayout layout = CockpitPackParser.ReadLayout(body, mode);
        string stem = TransformContext.SafeFileName(source.Stem);
        string jsonPath = context.Allocate($"{Folder}/{stem}.json");
        string aircraft = stem.Split('_')[0];
        IReadOnlyList<CockpitRegionDto> regions = Regions(layout);

        return CockpitPackParser.IsGenerated(mode)
            ? Generated(source, context, mode, layout, body, jsonPath, stem, aircraft, regions)
            : Carried(source, mode, layout, body, jsonPath, stem, aircraft, regions, context);
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput document = outputs.FirstOrDefault(o => o.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidDataException("a cockpit pack expects a .json document among its outputs");
        CockpitPackDto dto = JsonSerializer.Deserialize(document.Bytes, TransformJsonContext.Readable.CockpitPackDto)
                             ?? throw new InvalidDataException("the cockpit document is empty");
        CockpitPackMode mode = Enum.Parse<CockpitPackMode>(dto.Mode ?? string.Empty, ignoreCase: true);

        if (dto.Regenerated)
        {
            LoadedOutput png = outputs.FirstOrDefault(o => o.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                               ?? throw new InvalidDataException("a regenerated cockpit pack expects its .png");
            return CockpitPackGenerator.Emit(mode, RunsOf(dto, png.Bytes), dto.ElementCount);
        }

        byte[] pixels = Carried(outputs, dto, ".pixels.bin");
        byte[] code = Carried(outputs, dto, ".code.bin");
        byte[] elements = ElementsOf(dto);
        List<byte> pack = new List<byte>(4 + pixels.Length + elements.Length + code.Length);
        pack.AddRange(Word(PortHex.Parse(dto.Header?.PaintEntry)));
        pack.AddRange(Word(PortHex.Parse(dto.Header?.ElementListOffset)));
        pack.AddRange(pixels);
        pack.AddRange(elements);
        pack.AddRange(code);
        return [.. pack];
    }

    private static IReadOnlyList<TransformOutput> Generated(
        TransformSource source,
        TransformContext context,
        CockpitPackMode mode,
        CockpitPackLayout layout,
        ReadOnlySpan<byte> body,
        string jsonPath,
        string stem,
        string aircraft,
        IReadOnlyList<CockpitRegionDto> regions)
    {
        CockpitPaintProgram program = CockpitPackParser.Simulate(body, layout);
        if (program.DataCursorEnd != layout.DataEnd)
        {
            throw new InvalidDataException(
                $"{source.Name}: the paint program consumed pixel data up to 0x{program.DataCursorEnd:X4} " +
                $"but the region ends at 0x{layout.DataEnd:X4} — the parse does not account for the pack");
        }

        ImagePalette palette = ImagePalettes.Resolve(context, source.OriginFile, source.Stem, bitsPerPixel: 8);
        byte[] canvas = new byte[CockpitPackParser.ScreenWidth * CockpitPackParser.ScreenHeight];
        foreach (CockpitPaintRun run in program.Runs)
        {
            run.Pixels.CopyTo(canvas.AsSpan((run.Row * CockpitPackParser.ScreenWidth) + run.X));
        }

        string pngPath = context.Allocate($"{Folder}/{stem}.png");
        CockpitPackDto dto = Document(source, mode, layout, aircraft, regions, regenerated: true);
        dto = new CockpitPackDto
        {
            Format = dto.Format,
            About = dto.About,
            Source = dto.Source,
            Aircraft = dto.Aircraft,
            Mode = dto.Mode,
            SubMode = dto.SubMode,
            Regenerated = true,
            Header = dto.Header,
            Regions = dto.Regions,
            ElementCount = dto.ElementCount,
            ElementBytes = dto.ElementBytes,
            Pixels = pngPath,
            Palette = new PaletteBindingDto { Source = palette.Source, Binding = palette.Binding },
            Runs = [.. program.Runs.Select(r => (IReadOnlyList<int>)new[] { r.Row, r.X, r.Pixels.Length })],
            PaintedRows = [program.FirstRow, program.LastRow],
            PixelCount = program.PixelCount,
            Accounting =
                $"every byte explained: {layout.CodeBytes} B of code and {layout.DataBytes} B of " +
                $"pixel data regenerate from {program.Runs.Count} span(s) and the element count, " +
                "and the round trip proves it",
        };

        return
        [
            new TransformOutput(
                jsonPath,
                JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.CockpitPackDto),
                OutputRole.Data,
                OutputFidelity.Exact,
                $"{mode}: {program.PixelCount} opaque pixels on rows {program.FirstRow}..{program.LastRow}, " +
                $"{layout.CodeBytes} B of generated code"),
            new TransformOutput(
                pngPath,
                Image.IndexedPng.Encode(
                    CockpitPackParser.ScreenWidth, CockpitPackParser.ScreenHeight, canvas, palette.Rgb),
                OutputRole.Data,
                OutputFidelity.Exact,
                $"palette: {palette.Source} ({palette.Binding}); pixels outside `runs` are transparent"),
        ];
    }

    private static IReadOnlyList<TransformOutput> Carried(
        TransformSource source,
        CockpitPackMode mode,
        CockpitPackLayout layout,
        ReadOnlySpan<byte> body,
        string jsonPath,
        string stem,
        string aircraft,
        IReadOnlyList<CockpitRegionDto> regions,
        TransformContext context)
    {
        if (layout.CodeStart != layout.ElementListOffset + layout.ElementListBytes)
        {
            throw new InvalidDataException(
                $"{source.Name}: the code region starts at 0x{layout.CodeStart:X4} but the element " +
                $"list ends at 0x{layout.ElementListOffset + layout.ElementListBytes:X4}; this pack is " +
                "not laid out header | pixel data | element list | code");
        }

        string pixelPath = context.Allocate($"{Folder}/{stem}.pixels.bin");
        string codePath = context.Allocate($"{Folder}/{stem}.code.bin");
        CockpitPackDto dto = Document(source, mode, layout, aircraft, regions, regenerated: false);
        dto = new CockpitPackDto
        {
            Format = dto.Format,
            About = dto.About,
            Source = dto.Source,
            Aircraft = dto.Aircraft,
            Mode = dto.Mode,
            SubMode = dto.SubMode,
            Regenerated = false,
            Header = dto.Header,
            Regions = dto.Regions,
            ElementCount = dto.ElementCount,
            ElementBytes = dto.ElementBytes,
            Carried = [pixelPath, codePath],
            Accounting =
                $"explained: the {layout.HeaderLength}-byte header (entry points) and the " +
                $"{layout.ElementListBytes}-byte element list. Not explained: {layout.DataBytes} B of " +
                $"pixel data and {layout.CodeBytes} B of paint code, carried verbatim — " +
                (mode == CockpitPackMode.Ega
                    ? "the EGA packs are a second program family whose `mov ds,[bp+6]` prologue is " +
                      "unresolved; settling it needs a sub-mode-1 recording"
                    : "this is a dormant mode (a reviewed decision), so no generator was written"),
        };

        return
        [
            new TransformOutput(
                jsonPath,
                JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.CockpitPackDto),
                OutputRole.Data,
                OutputFidelity.Exact,
                $"{mode}: header + element list explained; {layout.DataBytes + layout.CodeBytes} B carried"),
            new TransformOutput(
                pixelPath,
                body[layout.DataStart..layout.DataEnd].ToArray(),
                OutputRole.Data,
                OutputFidelity.Exact,
                "pixel data, not yet decoded into an image for this mode",
                0,
                layout.DataBytes),
            new TransformOutput(
                codePath,
                body[layout.CodeStart..layout.CodeEnd].ToArray(),
                OutputRole.Data,
                OutputFidelity.Exact,
                "8086 paint code, carried verbatim and never converted",
                0,
                layout.CodeBytes),
        ];
    }

    private static CockpitPackDto Document(
        TransformSource source,
        CockpitPackMode mode,
        CockpitPackLayout layout,
        string aircraft,
        IReadOnlyList<CockpitRegionDto> regions,
        bool regenerated) =>
        new()
        {
            Format = "cyac.cockpit.pack/1",
            About =
                "A cockpit compositor pack: the module 3a.lib ships per aircraft and video mode, " +
                "LCALLed once per frame with the draw page's segment to repaint the cockpit over the " +
                "3-D viewport (cockpit_sprites_post_blit @image@0x0E9A8). It is CODE, so this " +
                "document holds the parameters it was generated from rather than a translation of " +
                "it: `runs` are the opaque spans - the cockpit's alpha channel - and the sibling " +
                ".png holds their palette indices. The header words are ENTRY POINTS, not counts, " +
                "and the element list is inert: nothing in the pack or the game reads it, " +
                "only its length carries information. Port hazard from the body: the pack exits with " +
                "the Mode-X map mask left on plane 3 and GC[5]=0x40, and never restores them.",
            Source = $"{source.OriginFile}/{source.Name}",
            Aircraft = aircraft,
            Mode = mode.ToString(),
            SubMode = SubModes[mode],
            Regenerated = regenerated,
            Header = new CockpitHeaderDto
            {
                PaintEntry = PortHex.Format(layout.PaintEntry),
                ElementListOffset = PortHex.Format(layout.ElementListOffset),
                InitEntry = layout.InitEntry is { } init ? PortHex.Format(init) : null,
                UnusedEntry = layout.UnusedEntry is { } unused ? PortHex.Format(unused) : null,
                DataPointer = layout.DataPointerOffset is { } offset
                    ? new ExeFarPointerDto
                    {
                        Offset = PortHex.Format(offset),
                        Segment = PortHex.Format(layout.DataPointerSegment ?? 0),
                    }
                    : null,
            },
            Regions = regions,
            ElementCount = layout.Elements.Length,
            ElementBytes = layout.ElementsAreIdentity ? null : Convert.ToHexString(layout.Elements),
        };

    private static IReadOnlyList<CockpitRegionDto> Regions(CockpitPackLayout layout) =>
    [
        new CockpitRegionDto { Name = "header", Offset = PortHex.Format(0), Bytes = layout.HeaderLength },
        new CockpitRegionDto
        {
            Name = "code", Offset = PortHex.Format(layout.CodeStart), Bytes = layout.CodeBytes,
        },
        new CockpitRegionDto
        {
            Name = "pixelData", Offset = PortHex.Format(layout.DataStart), Bytes = layout.DataBytes,
        },
        new CockpitRegionDto
        {
            Name = "elementList",
            Offset = PortHex.Format(layout.ElementListOffset),
            Bytes = layout.ElementListBytes,
        },
    ];

    private static IReadOnlyList<CockpitPaintRun> RunsOf(CockpitPackDto dto, byte[] png)
    {
        (int width, int height, byte[] indices, _) = Image.IndexedPng.Decode(png);
        if (width != CockpitPackParser.ScreenWidth || height != CockpitPackParser.ScreenHeight)
        {
            throw new InvalidDataException(
                $"a cockpit PNG is {CockpitPackParser.ScreenWidth}x{CockpitPackParser.ScreenHeight}; " +
                $"this one is {width}x{height}");
        }

        List<CockpitPaintRun> runs = new List<CockpitPaintRun>();
        foreach (IReadOnlyList<int> run in dto.Runs ?? [])
        {
            if (run.Count != 3)
            {
                throw new InvalidDataException("a `runs` entry is [row, x, length]");
            }

            int row = run[0];
            int x = run[1];
            int length = run[2];
            if (row < 0 || row >= height || x < 0 || length < 1 || x + length > width)
            {
                throw new InvalidDataException(
                    $"the run [row {row}, x {x}, length {length}] leaves the {width}x{height} screen");
            }

            runs.Add(new CockpitPaintRun(row, x, indices.AsSpan((row * width) + x, length).ToArray()));
        }

        return runs;
    }

    private static byte[] ElementsOf(CockpitPackDto dto)
    {
        if (!string.IsNullOrEmpty(dto.ElementBytes))
        {
            byte[] carried = Convert.FromHexString(dto.ElementBytes);
            byte[] withTerminator = new byte[carried.Length + 1];
            carried.CopyTo(withTerminator, 0);
            withTerminator[^1] = CockpitPackParser.ElementListTerminator;
            return withTerminator;
        }

        byte[] identity = new byte[dto.ElementCount + 1];
        for (int i = 0; i < dto.ElementCount; i++)
        {
            identity[i] = (byte)i;
        }

        identity[^1] = CockpitPackParser.ElementListTerminator;
        return identity;
    }

    private static byte[] Carried(IReadOnlyList<LoadedOutput> outputs, CockpitPackDto dto, string suffix)
    {
        string? path = dto.Carried?.FirstOrDefault(
            p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        LoadedOutput output = outputs.FirstOrDefault(o => string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase))
                              ?? throw new InvalidDataException($"this cockpit pack's document names no {suffix} file");
        return output.Bytes;
    }

    private static byte[] Word(int value) => [(byte)(value & 0xFF), (byte)((value >> 8) & 0xFF)];
}
