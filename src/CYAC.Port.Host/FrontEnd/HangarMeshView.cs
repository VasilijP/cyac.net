using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Aircraft;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Render;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// The HANGAR's <c>3D VIEW:</c>: the aircraft's own mesh, drawn by the PORT's display-list renderer
/// inside the panel's left column.
/// </summary>
/// <remarks>
/// <para>
/// <b>One renderer, not two.</b>  This is <see cref="SceneRenderer"/> — the same pipeline the flight
/// draws through, with the same <see cref="SceneRenderOptions.Default"/> level-of-detail and colour
/// law — pointed at a scene with no theatre statics and exactly one dynamic instance.  It writes
/// straight into a <see cref="PixelTarget"/> over the panel's own rectangle of the window
/// (<see cref="FrontEndPainter.Surface.Region"/>), so there is no offscreen buffer, no copy and
/// nothing to allocate per frame.
/// </para>
/// <para>
/// <b>The background is the panel grey, and no <c>CYAC.Port.Render</c> change was needed for it.</b>
/// The renderer's terminal background function paints sky above the horizon and ground below it;
/// hand it a <see cref="SceneColors"/> whose sky AND ground are the panel's own index 7 and an EMPTY
/// horizon ramp under <see cref="HorizonStyle.Flat"/>, and every pixel the mesh does not cover comes
/// out that one colour — a split between two identical colours is invisible, and the analytic
/// coverage blend of a colour with itself is that colour.  The original's own screen shows exactly that:
/// flat panel grey, no sky, no ground, no horizon.
/// </para>
/// <para>
/// <b>The placement is the original's.</b>  <c>ui_aircraft_stats_panel</c> builds a 24-byte object
/// record at <c>[bp-0x2E]</c> (published to the panel at <c>[0x3AE8]</c>) holding the aircraft's
/// class, the three <c>pi.bin</c> hangar coordinates promoted to 24.8 fixed point
/// (<c>image@0x26655</c>/<c>0x2666A</c>/<c>0x2667F</c>) and the two rotation angles at
/// <c>+0x12</c>/<c>+0x14</c>, and hands it to the mesh renderer at <c>image@0x26A11</c>.  So the
/// EYE is at the origin and the AEROPLANE is at <c>(hangarCameraX, hangarCameraY, hangarCameraZ)</c>
/// world units, turned by the two angles — which is what this builds.
/// </para>
/// <para>
/// <b>The lens.</b> The original's projector has a focal length of <c>1 &lt;&lt; zoomShift</c>
/// ORIGINAL pixels (<see cref="CameraLens.OriginalFovDegrees"/>).  Measured off the original's screen:
/// the P-51's densest LOD is 127 world units long and it is 329 units away, and it spans 99 design
/// pixels — a focal length of 256.5, i.e. <b>shift 8</b>, the only power of two anywhere near it. The
/// panel is <see cref="PanelWidth"/> = 128 design columns wide, so the view's horizontal field is
/// <c>2·atan(64/256)</c> = 28.07°.  <c>(open)</c> — the FW-190 in the original comes out
/// about 8 % narrower than 256 predicts, which is either its drawn extent being shorter than its
/// vertex box or a second scale not yet found; the 3-D view is judged by the frame, not by
/// a diff, so it is recorded rather than fitted away.
/// </para>
/// </remarks>
internal sealed class HangarMeshView : IDisposable
{
    /// <summary>The panel rectangle the view fills: <c>(0x10, 0x3D, 0x80, 0x49)</c>.</summary>
    /// <remarks><c>image@0x26573..0x2658A</c> — the four words at <c>[bp-0x3C]</c>.</remarks>
    public const int PanelX = 0x10;

    /// <summary>Its top row.</summary>
    public const int PanelY = 0x3D;

    /// <summary>Its width in design columns.</summary>
    public const int PanelWidth = 0x80;

    /// <summary>Its height in design rows.</summary>
    public const int PanelHeight = 0x49;

    /// <summary>The projector zoom shift the panel's framing measures to (see the remarks).</summary>
    public const int ZoomShift = 8;

    /// <summary>Palette index 7 — the panel fill, and therefore the 3-D view's background.</summary>
    public const int BackgroundIndex = FrontEndPainter.FillIndex;

    /// <summary>A full turn in the engine's angle units: <c>0xB40</c> = 2880.</summary>
    /// <remarks><c>angle_delta_add_wrap_0xB40 @image@0x1842A</c>, so one unit is 0.125°.</remarks>
    public const int FullTurn = 0xB40;

    private readonly DataTree _tree;
    private readonly Rgb24 _background;
    private readonly IReadOnlyList<Rgb24> _palette;
    private readonly SceneRenderer _renderer = new();
    private readonly SceneSnapshot _snapshot = new(WorldScene.FromInstances("hangar", []));
    private readonly CameraLens _lens = new(CameraLens.OriginalFovDegrees(PanelWidth, ZoomShift));
    private readonly SceneColors _colors;

    /// <summary>
    /// The flight's own options with the horizon flattened — hoisted so a 3-D frame copies nothing.
    /// </summary>
    private readonly SceneRenderOptions _options =
        SceneRenderOptions.Default with { Horizon = HorizonStyle.Flat };
    private MeshLibrary? _meshes;
    private bool _disposed;

    /// <summary>Builds the view over a tree and the game's palette.</summary>
    /// <param name="tree">The data tree the meshes come from.</param>
    /// <param name="palette">The game's palette, widened to 8 bits.</param>
    public HangarMeshView(DataTree tree, IReadOnlyList<Rgb24> palette)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(palette);
        _tree = tree;
        _palette = palette;
        _background = palette.Count > BackgroundIndex
            ? palette[BackgroundIndex]
            : new Rgb24(170, 170, 170);
        _colors = new SceneColors(_background, _background);
        _renderer.SetPalette(palette);
    }

    /// <summary>Whether the mesh library has been built yet (it is built on the first 3-D frame).</summary>
    public bool MeshesLoaded => _meshes is not null;

    /// <summary>How the two rotation angles map onto the renderer's pose: degrees per angle unit.</summary>
    public const double DegreesPerAngleUnit = 360.0 / FullTurn;

    /// <summary>Draws one aeroplane into the panel.</summary>
    /// <param name="surface">The design surface (it must carry a frame — see <see cref="FrontEndPainter.Surface.Region"/>).</param>
    /// <param name="plane">The encyclopedia page being shown.</param>
    /// <param name="rotationX">The <c>[bp-0x1C]</c> angle: the aeroplane's heading, in angle units.</param>
    /// <param name="rotationY">The <c>[bp-0x1A]</c> angle: its pitch.</param>
    /// <returns>Whether anything was drawn.</returns>
    public bool Render(
        in FrontEndPainter.Surface surface, EncyclopediaPlane plane, int rotationX, int rotationY)
    {
        ArgumentNullException.ThrowIfNull(plane);
        if (!surface.Region(PanelX, PanelY, PanelWidth, PanelHeight, out PixelTarget region))
        {
            return false;
        }

        _meshes ??= new MeshLibrary(_tree);
        if (_meshes.TryGet(plane.MeshBasename) is not { } mesh)
        {
            return false;
        }

        HangarView view = plane.HangarView;
        _snapshot.BeginFrame();
        _snapshot.Add(new SceneInstance(
            mesh,
            view.X,
            view.Y,
            view.Z,
            HeadingDegrees: rotationX * DegreesPerAngleUnit,
            PitchDegrees: rotationY * DegreesPerAngleUnit,
            // The hangar shows the aeroplane CLEAN: the original's screen has no gear down.  The
            // original's panel never writes g_gear_deploy_angle_bam [0xEF96], so the mesh draws at
            // whatever the last flight left; the port states the pose it means instead of drawing an
            // un-posed articulation, which hangs a wheel under the P-51.
            GearAngleBam: GearArticulation.RetractedAngle,
            // And CLEAN also means the afterburner plume is out.  The showroom is the original's
            // MODEL PREVIEW (g_model_preview_mode_flag [0xF28A] = 1, set by
            // ui_aircraft_stats_panel's 3-D view @image@0x269C7), and the prepare callback's plume
            // gate fails on it outright (image@0x2D902), so the MiG-21 and the F-4 stand in the
            // hangar cold.
            HiddenLeafNodes: FlameLeaves.Hidden(plane.MeshBasename, lit: false)));

        _renderer.Render(
            region,
            _snapshot,
            new CameraPose(0.0, 0.0, 0.0, 0.0, 0.0, 0.0),
            _lens,
            _colors,
            _options);
        return true;
    }

    /// <summary>Fills the panel with the flat background, for a frame with no mesh to draw.</summary>
    /// <param name="surface">The design surface.</param>
    public static void Clear(in FrontEndPainter.Surface surface) =>
        surface.Fill(PanelX, PanelY, PanelWidth, PanelHeight, BackgroundIndex);

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

    /// <summary>The palette the view resolves the mesh's colour indices through.</summary>
    /// <remarks>Kept so a test can prove it is the GAME's palette and not a default ramp.</remarks>
    public IReadOnlyList<Rgb24> Palette => _palette;
}
