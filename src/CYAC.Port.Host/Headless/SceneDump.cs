using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Markings;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Sim.Combat.Effects;
using CYAC.Port.Render;

namespace CYAC.Port.Host.Headless;

/// <summary>
/// A SCENE DUMP: everything the 3-D renderer was handed for one presented frame, written beside the
/// F12 screenshot so the frame can be re-rendered — at any size, under any of the scrutiny switches,
/// on any thread count — as a STATIC TEST CASE.
/// </summary>
/// <remarks>
/// <para>
/// It is the renderer's INPUT, not its output: the camera, the lens, the sky/ground pair, the
/// palette, the render options, the asset-conditioning knobs the mesh library was built with, and
/// every instance of the frame — the theatre's statics AND the dynamics (the player, the enemies,
/// the puffs, the cloud deck, the ground grid, the sun) with the sim state each carried
/// (<see cref="SceneInstance"/>).  Meshes are named by basename and resolved through the data tree
/// at re-render, so a dump is small and stays valid across asset re-conditioning — and is
/// deliberately WRONG about a mesh whose conditioning changed since, which is what a test case is
/// for.
/// </para>
/// <para>
/// Not carried: the line-width model and the tracer halo (<see cref="SceneRenderOptions.LineWidths"/>,
/// <see cref="SceneRenderOptions.Tracer"/>) — the re-render uses their defaults.  The cockpit, the
/// HUD and the windows are not scene content and are not in the dump; the PNG beside it has them.
/// </para>
/// </remarks>
public sealed class SceneDumpDocument
{
    /// <summary>The format tag; a reader refuses anything else.</summary>
    public const string CurrentFormat = "cyac-scene/1";

    /// <summary>The format tag.</summary>
    public string Format { get; set; } = CurrentFormat;

    /// <summary>When the frame was captured, ISO-8601 UTC.</summary>
    public string? CapturedUtc { get; set; }

    /// <summary>The mission key, or the test-flight aircraft.</summary>
    public string? Mission { get; set; }

    /// <summary>The player's aircraft basename.</summary>
    public string? Aircraft { get; set; }

    /// <summary>The view the frame was presented in.</summary>
    public string? View { get; set; }

    /// <summary>The sortie's simulated seconds at capture.</summary>
    public double SimSeconds { get; set; }

    /// <summary>The target the world was drawn into.</summary>
    public SceneDumpTarget Target { get; set; } = new();

    /// <summary>The camera pose.</summary>
    public SceneDumpCamera Camera { get; set; } = new();

    /// <summary>The lens.</summary>
    public double HorizontalFovDegrees { get; set; } = CameraLens.DefaultHorizontalFovDegrees;

    /// <summary>The sky / ground pair, 8-bit RGB.</summary>
    public int[]? Sky { get; set; }

    /// <summary>The ground colour, 8-bit RGB.</summary>
    public int[]? Ground { get; set; }

    /// <summary>Whether the horizon ramp is taken from the palette (the shipped look).</summary>
    public bool RampFromPalette { get; set; }

    /// <summary>The 256-entry palette, 8-bit RGB triples; null leaves the renderer's grey ramp.</summary>
    public int[][]? Palette { get; set; }

    /// <summary>The asset-conditioning knobs the mesh library was built with.</summary>
    public SceneDumpConditioning Conditioning { get; set; } = new();

    /// <summary>The render options in force.</summary>
    public SceneDumpOptions Options { get; set; } = new();

    /// <summary>The theatre's name.</summary>
    public string? WorldName { get; set; }

    /// <summary>The theatre's static instances.</summary>
    public List<SceneDumpInstance> Statics { get; set; } = [];

    /// <summary>The frame's dynamic instances, in the order the host added them.</summary>
    public List<SceneDumpInstance> Dynamics { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Writes the dump.</summary>
    /// <param name="path">Where.</param>
    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Reads a dump.</summary>
    /// <param name="path">The file.</param>
    /// <exception cref="InvalidDataException">The file is not a scene dump of a known format.</exception>
    public static SceneDumpDocument Read(string path)
    {
        SceneDumpDocument document = JsonSerializer.Deserialize<SceneDumpDocument>(File.ReadAllText(path), Json)
                                     ?? throw new InvalidDataException($"{path}: not a scene dump");
        if (!string.Equals(document.Format, CurrentFormat, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{path}: scene dump format '{document.Format}' is not '{CurrentFormat}'");
        }

        return document;
    }

    /// <summary>
    /// Captures the renderer's input for one frame.
    /// </summary>
    /// <param name="scene">The frame's snapshot — statics and this frame's dynamics.</param>
    /// <param name="camera">The camera pose.</param>
    /// <param name="lens">The lens.</param>
    /// <param name="colors">The sky / ground pair.</param>
    /// <param name="palette">The installed palette, or null.</param>
    /// <param name="options">The render options in force.</param>
    /// <param name="width">The target's width.</param>
    /// <param name="height">The frame's height.</param>
    /// <param name="worldRows">How many rows the world was drawn into.</param>
    /// <param name="conditioning">The mesh-library conditioning knobs.</param>
    public static SceneDumpDocument Capture(
        SceneSnapshot scene,
        in CameraPose camera,
        CameraLens lens,
        SceneColors colors,
        IReadOnlyList<Rgb24>? palette,
        SceneRenderOptions options,
        int width,
        int height,
        int worldRows,
        SceneDumpConditioning conditioning)
    {
        ArgumentNullException.ThrowIfNull(scene);
        SceneDumpDocument document = new SceneDumpDocument
        {
            CapturedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Target = new SceneDumpTarget { Width = width, Height = height, WorldRows = worldRows },
            Camera = new SceneDumpCamera
            {
                EyeX = camera.EyeX,
                EyeY = camera.EyeY,
                EyeZ = camera.EyeZ,
                HeadingRadians = camera.HeadingRadians,
                PitchRadians = camera.PitchRadians,
                RollRadians = camera.RollRadians,
            },
            HorizontalFovDegrees = lens.HorizontalFovDegrees,
            Sky = [colors.Sky.R, colors.Sky.G, colors.Sky.B],
            Ground = [colors.Ground.R, colors.Ground.G, colors.Ground.B],
            RampFromPalette = colors.HorizonRamp is { Count: > 0 },
            Palette = palette is null ? null : [.. palette.Select(c => new[] { (int)c.R, (int)c.G, (int)c.B })],
            Conditioning = conditioning,
            Options = SceneDumpOptions.From(options),
            WorldName = scene.World.Name,
        };
        foreach (SceneInstance instance in scene.World.Statics)
        {
            document.Statics.Add(SceneDumpInstance.From(in instance));
        }

        foreach (SceneInstance instance in scene.Dynamic)
        {
            document.Dynamics.Add(SceneDumpInstance.From(in instance));
        }

        return document;
    }

    /// <summary>The sky / ground pair the dump carries, with the palette ramp when it had one.</summary>
    public SceneColors Colors()
    {
        Rgb24 sky = Rgb(Sky, SceneColors.Default.Sky);
        Rgb24 ground = Rgb(Ground, SceneColors.Default.Ground);
        IReadOnlyList<Rgb24>? palette = PaletteColors();
        return new SceneColors(sky, ground, RampFromPalette && palette is not null ? SceneColors.RampFrom(palette) : null);
    }

    /// <summary>The palette as colours, or null when the dump carries none.</summary>
    public IReadOnlyList<Rgb24>? PaletteColors()
    {
        if (Palette is not { Length: 256 })
        {
            return null;
        }

        Rgb24[] colors = new Rgb24[256];
        for (int i = 0; i < 256; i++)
        {
            colors[i] = Rgb(Palette[i], default);
        }

        return colors;
    }

    /// <summary>The camera pose.</summary>
    public CameraPose CameraPose() => new(
        Camera.EyeX, Camera.EyeY, Camera.EyeZ,
        Camera.HeadingRadians, Camera.PitchRadians, Camera.RollRadians);

    /// <summary>
    /// Rebuilds the frame's snapshot against a mesh library.
    /// </summary>
    /// <param name="meshes">The library the basenames resolve through.</param>
    /// <param name="skipped">
    /// Receives the basename of every instance the library could not resolve, or null.  The
    /// host authors a few meshes of its own that are in no data tree — the gunnery <c>round</c>
    /// streak (<c>GunneryRounds.RoundMesh</c>) is one — and a frame captured with rounds in the
    /// air names them; they are dropped from the re-render and reported, not fatal (a dump with
    /// bullets in it is exactly the kind of frame worth keeping).
    /// </param>
    public SceneSnapshot Snapshot(MeshLibrary meshes, ICollection<string>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        List<SceneInstance> statics = new List<SceneInstance>(Statics.Count);
        foreach (SceneDumpInstance row in Statics)
        {
            if (row.TryToInstance(meshes, out SceneInstance instance))
            {
                statics.Add(instance);
            }
            else
            {
                skipped?.Add(row.Mesh);
            }
        }

        SceneSnapshot snapshot = new SceneSnapshot(WorldScene.FromInstances(WorldName ?? "dump", statics));
        snapshot.BeginFrame();
        foreach (SceneDumpInstance row in Dynamics)
        {
            if (row.TryToInstance(meshes, out SceneInstance instance))
            {
                snapshot.Add(instance);
            }
            else
            {
                skipped?.Add(row.Mesh);
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Re-renders the dump.
    /// </summary>
    /// <param name="meshes">The mesh library (built with <see cref="Conditioning"/>, or another on purpose).</param>
    /// <param name="width">The target's width.</param>
    /// <param name="height">The frame's height (rows below <paramref name="worldRows"/> stay black).</param>
    /// <param name="worldRows">How many rows the world is drawn into.</param>
    /// <param name="options">The render options.</param>
    /// <param name="stats">What the frame drew.</param>
    /// <param name="skipped">Receives the basenames of instances the library could not resolve (see <see cref="Snapshot"/>).</param>
    /// <returns>Row-major, red-high pixels, <c>width × height</c>.</returns>
    public uint[] Render(
        MeshLibrary meshes, int width, int height, int worldRows, SceneRenderOptions options,
        out SceneFrameStats stats, ICollection<string>? skipped = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        worldRows = Math.Clamp(worldRows, 1, height);
        uint[] pixels = new uint[width * height];
        PixelTarget target = new PixelTarget(pixels, width, worldRows, height, PixelChannelOrder.RedHigh);
        using SceneRenderer renderer = new SceneRenderer();
        if (PaletteColors() is { } palette)
        {
            renderer.SetPalette(palette);
        }

        stats = renderer.Render(
            target, Snapshot(meshes, skipped), CameraPose(), new CameraLens(HorizontalFovDegrees), Colors(), options);
        return FrameImage.ToRows(pixels, width, height, height, blueHigh: false);
    }

    private static Rgb24 Rgb(int[]? triple, Rgb24 fallback) =>
        triple is { Length: >= 3 }
            ? new Rgb24((byte)Math.Clamp(triple[0], 0, 255), (byte)Math.Clamp(triple[1], 0, 255), (byte)Math.Clamp(triple[2], 0, 255))
            : fallback;
}

/// <summary>The target geometry of a dumped frame.</summary>
public sealed class SceneDumpTarget
{
    /// <summary>Columns.</summary>
    public int Width { get; set; }

    /// <summary>Rows of the whole frame.</summary>
    public int Height { get; set; }

    /// <summary>Rows the 3-D world was drawn into (the cockpit viewport, or the whole frame).</summary>
    public int WorldRows { get; set; }
}

/// <summary>A dumped camera pose (<see cref="CameraPose"/>).</summary>
public sealed class SceneDumpCamera
{
    /// <summary>Eye X, world units.</summary>
    public double EyeX { get; set; }

    /// <summary>Eye Y.</summary>
    public double EyeY { get; set; }

    /// <summary>Eye Z.</summary>
    public double EyeZ { get; set; }

    /// <summary>Heading, radians.</summary>
    public double HeadingRadians { get; set; }

    /// <summary>Pitch, radians.</summary>
    public double PitchRadians { get; set; }

    /// <summary>Roll, radians.</summary>
    public double RollRadians { get; set; }
}

/// <summary>The asset-conditioning knobs the mesh library was built with (<see cref="MeshConditioning"/>).</summary>
public sealed class SceneDumpConditioning
{
    /// <summary><c>vector</c>, <c>shipped</c> or <c>off</c>.</summary>
    public string Markings { get; set; } = "shipped";

    /// <summary>The markings overlay directory, or null for the built-ins.</summary>
    public string? MarkingsDir { get; set; }

    /// <summary>The decal lift, model units.</summary>
    public double DecalLift { get; set; }

    /// <summary>Whether the sheets are inflated.</summary>
    public bool SheetInflate { get; set; } = true;

    /// <summary>The sheet thickness factor.</summary>
    public double SheetThickness { get; set; } = 1.0;

    /// <summary>The vertex weld distance, model units (0 = off).</summary>
    public double Weld { get; set; } = VertexWeld.DefaultEpsilonModelUnits;

    /// <summary>Builds the mesh library these knobs describe.</summary>
    /// <param name="tree">The data tree.</param>
    public MeshLibrary BuildMeshes(DataTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        MarkingsMode mode = Markings.Trim().ToLowerInvariant() switch
        {
            "vector" => MarkingsMode.Vector,
            "off" => MarkingsMode.Off,
            _ => MarkingsMode.Shipped,
        };
        MarkingLibrary? library = mode == MarkingsMode.Vector
            ? new Core.Markings.MarkingLibrary(MarkingsDir ?? Core.Markings.MarkingLibrary.FindSourceDirectory(tree.Root), tree)
            : null;
        return new MeshLibrary(tree, new MeshConditioning(
            Math.Max(0.0, DecalLift),
            SheetInflate ? SheetInflationOptions.Default.Scaled(Math.Max(0.0, SheetThickness)) : null,
            mode,
            library,
            VertexWeldModelUnits: Math.Max(0.0, Weld)));
    }
}

/// <summary>The scalar render options a dump carries (<see cref="SceneRenderOptions"/> less the line-width and tracer models).</summary>
public sealed class SceneDumpOptions
{
    public bool DrawScenery { get; set; } = true;
    public bool DrawObjects { get; set; } = true;
    public double LodHysteresis { get; set; } = SceneRenderOptions.DefaultLodHysteresis;
    public double MaxDrawDistanceWorldUnits { get; set; }
    public bool ClassicCull { get; set; }
    public string Alpha { get; set; } = nameof(AlphaMode.Blend);
    public string Edges { get; set; } = nameof(EdgeMode.Analytic);
    public bool Articulation { get; set; } = true;
    public string Horizon { get; set; } = nameof(HorizonStyle.Refined);
    public double HorizonBandDegrees { get; set; } = HorizonRenderer.DefaultBandDegrees;
    public string Lod { get; set; } = nameof(LodPolicy.Max);
    public double NearInstanceExemptionWorldUnits { get; set; } = SceneRenderOptions.DefaultNearInstanceExemptionWorldUnits;
    public double EyeInsideSkipRadiusWorldUnits { get; set; } = SceneRenderOptions.DefaultEyeInsideSkipRadiusWorldUnits;
    public bool SoftEffects { get; set; } = true;
    public bool EffectDebris { get; set; } = true;
    public bool BitmapExplosions { get; set; } = true;
    public bool DrawPaintTreeOrphans { get; set; }
    public bool SmokeGrowth { get; set; } = true;
    public double SmokeSizeScale { get; set; } = 1.0;
    public double SmokeDensity { get; set; } = 1.0;
    public int TileSize { get; set; } = SceneRenderOptions.DefaultTileSize;
    public int Threads { get; set; } = 1;
    public bool SeamMask { get; set; } = true;
    public bool BackfaceCull { get; set; } = true;
    public string Wireframe { get; set; } = nameof(WireframeMode.Off);
    public string FaceColors { get; set; } = nameof(FaceColorMode.Paint);
    public int FaceColorSeed { get; set; }
    public int WireColorIndex { get; set; } = -1;
    public double WireWidthPixels { get; set; } = 1.0;
    public string MaskView { get; set; } = nameof(Render.MaskView.Off);

    /// <summary>The dump form of a live option block.</summary>
    /// <param name="o">The options.</param>
    public static SceneDumpOptions From(SceneRenderOptions o) => new()
    {
        DrawScenery = o.DrawScenery,
        DrawObjects = o.DrawObjects,
        LodHysteresis = o.LodHysteresis,
        MaxDrawDistanceWorldUnits = o.MaxDrawDistanceWorldUnits,
        ClassicCull = o.ClassicCull,
        Alpha = o.Alpha.ToString(),
        Edges = o.Edges.ToString(),
        Articulation = o.Articulation,
        Horizon = o.Horizon.ToString(),
        HorizonBandDegrees = o.HorizonBandDegrees,
        Lod = o.Lod.ToString(),
        NearInstanceExemptionWorldUnits = o.NearInstanceExemptionWorldUnits,
        EyeInsideSkipRadiusWorldUnits = o.EyeInsideSkipRadiusWorldUnits,
        SoftEffects = o.SoftEffects,
        EffectDebris = o.EffectDebris,
        BitmapExplosions = o.BitmapExplosions,
        DrawPaintTreeOrphans = o.DrawPaintTreeOrphans,
        SmokeGrowth = o.SmokeGrowth,
        SmokeSizeScale = o.SmokeSizeScale,
        SmokeDensity = o.SmokeDensity,
        TileSize = o.TileSize,
        Threads = o.Threads,
        SeamMask = o.SeamMask,
        BackfaceCull = o.BackfaceCull,
        Wireframe = o.Wireframe.ToString(),
        FaceColors = o.FaceColors.ToString(),
        FaceColorSeed = o.FaceColorSeed,
        WireColorIndex = o.WireColorIndex,
        WireWidthPixels = o.WireWidthPixels,
        MaskView = o.MaskView.ToString(),
    };

    /// <summary>The live option block these describe (line widths and tracer at their defaults).</summary>
    public SceneRenderOptions ToOptions() => SceneRenderOptions.Default with
    {
        DrawScenery = DrawScenery,
        DrawObjects = DrawObjects,
        LodHysteresis = LodHysteresis,
        MaxDrawDistanceWorldUnits = MaxDrawDistanceWorldUnits,
        ClassicCull = ClassicCull,
        Alpha = Enum.TryParse<AlphaMode>(Alpha, true, out AlphaMode alpha) ? alpha : AlphaMode.Blend,
        Edges = Enum.TryParse<EdgeMode>(Edges, true, out EdgeMode edges) ? edges : EdgeMode.Analytic,
        Articulation = Articulation,
        Horizon = Enum.TryParse<HorizonStyle>(Horizon, true, out HorizonStyle horizon) ? horizon : HorizonStyle.Refined,
        HorizonBandDegrees = HorizonBandDegrees,
        Lod = Enum.TryParse<LodPolicy>(Lod, true, out LodPolicy lod) ? lod : LodPolicy.Max,
        NearInstanceExemptionWorldUnits = NearInstanceExemptionWorldUnits,
        EyeInsideSkipRadiusWorldUnits = EyeInsideSkipRadiusWorldUnits,
        SoftEffects = SoftEffects,
        EffectDebris = EffectDebris,
        BitmapExplosions = BitmapExplosions,
        DrawPaintTreeOrphans = DrawPaintTreeOrphans,
        SmokeGrowth = SmokeGrowth,
        SmokeSizeScale = SmokeSizeScale,
        SmokeDensity = SmokeDensity,
        TileSize = TileSize,
        Threads = Math.Max(1, Threads),
        SeamMask = SeamMask,
        BackfaceCull = BackfaceCull,
        Wireframe = Enum.TryParse<WireframeMode>(Wireframe, true, out WireframeMode wire) ? wire : WireframeMode.Off,
        FaceColors = Enum.TryParse<FaceColorMode>(FaceColors, true, out FaceColorMode faces) ? faces : FaceColorMode.Paint,
        FaceColorSeed = FaceColorSeed,
        WireColorIndex = WireColorIndex,
        WireWidthPixels = WireWidthPixels,
        MaskView = Enum.TryParse<MaskView>(MaskView, true, out MaskView mask) ? mask : Render.MaskView.Off,
    };
}

/// <summary>One dumped instance (<see cref="SceneInstance"/>, the mesh by basename).</summary>
public sealed class SceneDumpInstance
{
    /// <summary>The mesh's basename, resolved through the mesh library at re-render.</summary>
    public string Mesh { get; set; } = string.Empty;

    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Heading { get; set; }
    public double Pitch { get; set; }
    public double Roll { get; set; }
    public int Gear { get; set; } = -1;
    public double Opacity { get; set; } = 1.0;
    public double Scale { get; set; }
    public int EffectAge { get; set; } = -1;
    public int EffectFork { get; set; }
    public int EffectSequence { get; set; }
    public int[]? HiddenLeaves { get; set; }
    public bool AtInfinity { get; set; }
    public int[]? Smoke { get; set; }
    public bool SeamMask { get; set; }

    /// <summary>The dump row of a live instance.</summary>
    /// <param name="i">The instance.</param>
    public static SceneDumpInstance From(in SceneInstance i) => new()
    {
        Mesh = i.Mesh.Basename,
        X = i.X,
        Y = i.Y,
        Z = i.Z,
        Heading = i.HeadingDegrees,
        Pitch = i.PitchDegrees,
        Roll = i.RollDegrees,
        Gear = i.GearAngleBam,
        Opacity = i.Opacity,
        Scale = i.WorldScaleOverride,
        EffectAge = i.EffectAgeFrameTime,
        EffectFork = i.EffectFork,
        EffectSequence = i.EffectSequence,
        HiddenLeaves = i.HiddenLeafNodes is { Count: > 0 } hidden ? [.. hidden] : null,
        AtInfinity = i.AtInfinity,
        Smoke = i.SmokePuff is { } puff ? [puff.Kind, puff.AgeFrameTime, puff.SpanFrameTime] : null,
        SeamMask = i.SeamMask,
    };

    /// <summary>The live instance this row describes.</summary>
    /// <param name="meshes">The mesh library.</param>
    /// <exception cref="InvalidDataException">The basename has no mesh.</exception>
    public SceneInstance ToInstance(MeshLibrary meshes) =>
        TryToInstance(meshes, out SceneInstance instance)
            ? instance
            : throw new InvalidDataException($"scene dump names mesh '{Mesh}', which the data tree does not have");

    /// <summary>The live instance this row describes, or false when the library has no such mesh.</summary>
    /// <param name="meshes">The mesh library.</param>
    /// <param name="instance">The instance.</param>
    public bool TryToInstance(MeshLibrary meshes, out SceneInstance instance)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        MeshModel? mesh = meshes.TryGet(Mesh);
        if (mesh is null)
        {
            instance = default;
            return false;
        }

        instance = new SceneInstance(
            mesh, X, Y, Z, Heading, Pitch, Roll, Gear, Opacity, Scale, EffectAge,
            (byte)Math.Clamp(EffectFork, 0, 255), (byte)Math.Clamp(EffectSequence, 0, 255),
            HiddenLeaves, AtInfinity,
            Smoke is { Length: 3 } s ? new SmokePuffState((byte)Math.Clamp(s[0], 0, 255), s[1], s[2]) : null,
            SeamMask);
        return true;
    }
}
