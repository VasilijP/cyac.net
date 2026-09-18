using CYAC.Port.Core.Model.Cockpit;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Primitives;
using CYAC.Port.Render;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host;

/// <summary>
/// The TARGET window's LIVE SILHOUETTE: the target aircraft's own mesh, seen from 400 feet away along
/// the line the player is looking down.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a 3-D mini-scene, not a picture.</b>  <c>cockpit_radar_scope_draw @image@0x0A5D1</c>
/// hands <c>polygon_fill_mesh_render_setup @image@0x146B8</c> its own render context
/// (<c>[0xB74A]</c>, the second instance of the same 0x5A-byte object the mission renderer uses at
/// <c>[0xC330]</c>) and its own clip rectangle, so the panel gets sky, ground and a horizon of its
/// own — which the atlas confirms: a captured frame of the original's panel
/// is half sky and half grass with the horizon across it, while <c>30_f4_110.png</c>'s is pure sky.
/// </para>
/// <para>
/// <b>Where the camera is.</b>  <c>image@0x0A6BF..0x0A6D8</c> adds
/// <c>angle_distance_xyz_accum(g_radar_range, elevation = wrap(pitch + 0x5A0), heading = bearing)</c>
/// to the target's own position, where <c>(bearing, pitch)</c> are
/// <c>radar_bearing_range_farptr_unpack(player, target)</c>'s two outputs.  <c>+0x5A0</c> is 180° and
/// the spherical parametrisation negates all three components across it, so the point is
/// <b>the target's position minus the camera distance along the player-to-target ray</b> — 400 feet
/// from the target, on the player's side of it.  The mesh renderer is then handed the same
/// <c>(bearing, pitch)</c> as its view angles, i.e. it looks straight back at the target.
/// </para>
/// <para>
/// <b>How far.</b>  <see cref="TargetPanel.SilhouetteCameraDistance"/> — the class record's
/// <c>+0x0D</c> byte in sixteen-foot steps, <c>0x19</c> = 400 ft for every fighter.  Measured:
/// <c>g_radar_range [0xB7A4] = 102400</c> in every probe dump.
/// </para>
/// <para>
/// <b>The lens.</b>  <c>1 &lt;&lt; 7</c> design pixels of focal length (the literal pushed at
/// <c>image@0x0A776</c>), over the 64-column content rectangle — a 28.07° horizontal field, the same
/// the hangar's 3-D view uses at its own width.
/// </para>
/// <para>
/// <b>One renderer, not two</b> — this is <see cref="SceneRenderer"/>, the flight's own pipeline,
/// pointed at a scene with no theatre statics and exactly one dynamic instance, writing straight
/// into a <see cref="PixelTarget"/> over the window's own content rectangle.  The pattern is
/// <c>FrontEnd/HangarMeshView</c>'s, which F4 landed for the hangar.
/// </para>
/// </remarks>
public sealed class TargetSilhouetteView : IDisposable
{
    private readonly SceneRenderer _renderer = new();
    private readonly SceneSnapshot _snapshot = new(WorldScene.FromInstances("targetpanel", []));
    private readonly CameraLens _lens = new(CameraLens.OriginalFovDegrees(
        TargetPanelContentWidth, TargetPanel.SilhouetteZoomShift));
    private readonly SceneColors _colors;
    private readonly SceneRenderOptions _options;
    private bool _disposed;

    /// <summary>The content rectangle's width in design columns — 64 (<c>image@0x0EE28</c>).</summary>
    public const int TargetPanelContentWidth = 0x40;

    /// <summary>Builds the view over the game's palette and the flight's own colours.</summary>
    /// <param name="palette">The game's palette, widened to 8 bits.</param>
    /// <param name="colors">The flight's sky/ground pair and horizon ramp.</param>
    /// <param name="options">The flight's scene options, which the panel inherits.</param>
    public TargetSilhouetteView(
        IReadOnlyList<Rgb24> palette, SceneColors colors, SceneRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(palette);
        _colors = colors;

        // The panel is 64 × 48 design pixels: a tile bigger than the whole view costs a thread hop
        // for nothing, and no census or projection watch is wanted here.
        _options = options with { TileSize = Math.Max(16, options.TileSize) };
        if (palette.Count > 0)
        {
            _renderer.SetPalette(palette);
        }
    }

    /// <summary>How many frames drew a silhouette.</summary>
    public int Frames { get; private set; }

    /// <summary>Draws one target into the TARGET window's content rectangle.</summary>
    /// <param name="frame">The whole window's pixels.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="scale">The design → window mapping the cockpit layer uses.</param>
    /// <param name="mesh">The target's own mesh.</param>
    /// <param name="pose">Its world pose — position in world units and its three angles in degrees.</param>
    /// <param name="player">The player's world position, in world units.</param>
    /// <param name="cameraDistanceFeet">
    /// <see cref="TargetPanel.SilhouetteCameraDistance"/> for the target's class, in the renderer's
    /// own FEET (the arena's Q8 units divided by 256); 0 skips the draw.
    /// </param>
    /// <param name="gearAngleBam">The target's gear articulation angle.</param>
    /// <param name="hiddenLeafNodes">Which paint-tree leaves its prepare callback hides.</param>
    /// <returns>Whether anything was drawn.</returns>
    public bool Render(
        Span<uint> frame,
        int width,
        int height,
        CockpitScale scale,
        MeshModel mesh,
        SilhouettePose pose,
        Vec3 player,
        double cameraDistanceFeet,
        int gearAngleBam,
        IReadOnlyList<int>? hiddenLeafNodes)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (cameraDistanceFeet <= 0.0)
        {
            return false;
        }

        PanelRect content = TargetPanel.Content;
        int x0 = Math.Max(0, (int)Math.Floor(scale.ToWindowX(content.X)));
        int x1 = Math.Min(width, (int)Math.Ceiling(scale.ToWindowX(content.Right)));
        int y0 = Math.Max(0, (int)Math.Floor(scale.ToWindowY(content.Y)));
        int y1 = Math.Min(height, (int)Math.Ceiling(scale.ToWindowY(content.Bottom)));
        if (x1 - x0 < 2 || y1 - y0 < 2)
        {
            return false;
        }

        Vec3 target = new Vec3(pose.X, pose.Y, pose.Z);
        Vec3 toTarget = target - player;
        double length = toTarget.Length;
        if (length <= 0.0)
        {
            return false;
        }

        // The camera sits `cameraDistance` back along the player-to-target ray, looking at the
        // target: the original's `target + radar_range at (bearing, pitch + 180°)`.
        Vec3 eye = target - (toTarget * (cameraDistanceFeet / length));
        CameraPose camera = CameraRig.LookAt(eye, target);

        _snapshot.BeginFrame();
        _snapshot.Add(new SceneInstance(
            mesh,
            pose.X,
            pose.Y,
            pose.Z,
            pose.HeadingDegrees,
            pose.PitchDegrees,
            pose.RollDegrees,
            gearAngleBam,
            Opacity: 1.0,
            WorldScaleOverride: 0.0,
            HiddenLeafNodes: hiddenLeafNodes));

        PixelTarget region = new PixelTarget(
            frame[((x0 * height) + y0)..], x1 - x0, y1 - y0, height, PixelChannelOrder.BlueHigh);
        _renderer.Render(region, _snapshot, camera, _lens, _colors, _options);
        Frames++;
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _renderer.Dispose();
    }
}

/// <summary>The pose the silhouette draws its one instance at.</summary>
/// <param name="X">World X, world units.</param>
/// <param name="Y">World Y, world units.</param>
/// <param name="Z">World Z, world units.</param>
/// <param name="HeadingDegrees">Its heading.</param>
/// <param name="PitchDegrees">Its pitch.</param>
/// <param name="RollDegrees">Its roll.</param>
public readonly record struct SilhouettePose(
    double X, double Y, double Z, double HeadingDegrees, double PitchDegrees, double RollDegrees);
