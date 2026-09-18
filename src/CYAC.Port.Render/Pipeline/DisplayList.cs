using CYAC.Port.Core.Model.World;
using CYAC.Port.Render.Raster;

namespace CYAC.Port.Render.Pipeline;

/// <summary>
/// The per-frame DISPLAY LIST: the array of screen-space primitives the WHAT stage produces and the
/// HOW stage draws.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the boundary.</b>  The stage that fills it owns culling, LOD, the projection, the near
/// clip, the record's colour/coverage/stipple law and every effect callback, and touches no pixel;
/// the stage that draws it knows nothing about aircraft, smoke or missions.  A change that leaks
/// game knowledge below this type is wrong even if it draws correctly.
/// </para>
/// <para>
/// <b>It allocates nothing in a steady state.</b>  Every buffer is grow-only and
/// <see cref="Clear"/> only resets the counts, so a frame that is no larger than the largest frame
/// so far allocates zero bytes.
/// </para>
/// <para>
/// <b>It carries no raster state.</b> A later change can replace <see cref="DisplayListDrawer"/> with a
/// tiled, threaded drawer and then with a fragment resolve; nothing here has to change for that,
/// which is why the screen bounds are computed now even though R1 never bins by them.
/// </para>
/// </remarks>
internal sealed class DisplayList
{
    /// <summary>
    /// How far past its own vertices a POINT or a thin LINE reaches: half the one-pixel mark it
    /// paints, plus one for the pixel it may partly cover.
    /// </summary>
    /// <remarks>
    /// It was <c>(PixelScale × 0.5) + 1</c> while <c>--ssaa</c> made a target pixel smaller than a
    /// host one; the field went with it.
    /// </remarks>
    private const double PointMargin = 1.5;

    private DisplayPrimitive[] _primitives = new DisplayPrimitive[1024];
    private ScreenVertex[] _vertices = new ScreenVertex[8192];
    private long[] _pixels = new long[1024];
    private string?[] _watchNames = new string?[1024];
    private int[] _watchRecords = new int[1024];
    private SpriteImage?[] _sprites = new SpriteImage?[1024];
    private SurfaceShaderEntry[] _shaders = new SurfaceShaderEntry[64];
    private MarkingPlaneEntry[] _planes = new MarkingPlaneEntry[128];
    private int _count;
    private int _vertexCount;
    private int _shaderCount;
    private int _planeCount;

    /// <summary>
    /// The appearance the next <c>Add…</c> calls carry — set once per record by the WHAT stage.
    /// </summary>
    public PrimitiveStyle Style;

    /// <summary>
    /// The radial profile and compositing law a <see cref="PrimitiveKind.Halo"/> primitive is
    /// resolved with.
    /// </summary>
    /// <remarks>
    /// The TYPE's name is historical — it was introduced for the tracer; what the HOW
    /// stage uses of it is only <c>TracerHalo.Profile</c> and <c>TracerHalo.Composite</c> — a radial
    /// falloff and a blend rule, with no notion of what is glowing.
    /// </remarks>
    public TracerHalo HaloProfile = TracerHalo.Default;

    /// <summary>
    /// The 256 packed colours a <see cref="PrimitiveKind.Sprite"/>'s palette indices resolve
    /// through.
    /// </summary>
    /// <remarks>
    /// The one colour the WHAT stage cannot pre-resolve: a sprite's colours are PER PIXEL, so the
    /// table has to reach the rasteriser.  It is a colour table, not a game fact.
    /// </remarks>
    public uint[] SpritePalette = [];

    /// <summary>How many primitives the frame holds.</summary>
    public int Count => _count;

    /// <summary>The frame's primitives, in submission order.</summary>
    public ReadOnlySpan<DisplayPrimitive> Primitives => _primitives.AsSpan(0, _count);

    /// <summary>The frame's shared vertex pool.</summary>
    public ReadOnlySpan<ScreenVertex> Vertices => _vertices.AsSpan(0, _vertexCount);

    /// <summary>Empties the list, keeping every buffer.</summary>
    /// <remarks>
    /// The sprite and census-tag columns are NOT nulled: they are only ever read below
    /// <see cref="Count"/>, and the objects they point at (a sprite, a mesh's basename) outlive
    /// every frame anyway, so clearing them would be pure per-frame work for no effect.
    /// </remarks>
    public void Clear()
    {
        _count = 0;
        _vertexCount = 0;
        _shaderCount = 0;
        _planeCount = 0;
    }

    /// <summary>VECTOR MARKINGS — how many surface-shader entries the frame holds.</summary>
    public int ShaderCount => _shaderCount;

    /// <summary>VECTOR MARKINGS — one surface-shader entry.</summary>
    /// <param name="index">The entry's index (<see cref="DisplayPrimitive.Shader"/>).</param>
    public ref readonly SurfaceShaderEntry ShaderAt(int index) => ref _shaders[index];

    /// <summary>VECTOR MARKINGS — the frame's marking-plane pool (read by the tile workers).</summary>
    public MarkingPlaneEntry[] Planes => _planes;

    /// <summary>VECTOR MARKINGS — one shaded primitive's marking planes.</summary>
    /// <param name="entry">Its shader entry.</param>
    public ReadOnlySpan<MarkingPlaneEntry> PlanesOf(in SurfaceShaderEntry entry) =>
        _planes.AsSpan(entry.PlaneStart, entry.PlaneCount);

    /// <summary>
    /// VECTOR MARKINGS — appends a surface-shader entry: the face's skin and its marking planes.
    /// </summary>
    /// <param name="skin">The face's own paint.</param>
    /// <param name="planes">One plane pair per marking on the face; copied into the frame's pool.</param>
    /// <returns>The entry's index, for <see cref="AddShadedPolygon"/>.</returns>
    public int AddShader(SurfaceColor skin, ReadOnlySpan<MarkingPlaneEntry> planes)
    {
        if (_shaders.Length == _shaderCount)
        {
            Array.Resize(ref _shaders, _shaders.Length * 2);
        }

        if (_planes.Length < _planeCount + planes.Length)
        {
            Array.Resize(ref _planes, Math.Max(_planeCount + planes.Length, _planes.Length * 2));
        }

        planes.CopyTo(_planes.AsSpan(_planeCount));
        int index = _shaderCount++;
        _shaders[index] = new SurfaceShaderEntry
        {
            Skin = skin,
            PlaneStart = _planeCount,
            PlaneCount = planes.Length,
        };
        _planeCount += planes.Length;
        return index;
    }

    /// <summary>
    /// VECTOR MARKINGS — appends a filled convex polygon whose colour is computed per pixel by a
    /// surface shader.
    /// </summary>
    /// <param name="vertices">Its projected vertices, in order; at least three.</param>
    /// <param name="depth">Its <c>1/z</c> plane.</param>
    /// <param name="sortDepth">View-space Z at its centroid.</param>
    /// <param name="shader">The entry <see cref="AddShader"/> returned.</param>
    /// <returns>The primitive's index.</returns>
    public int AddShadedPolygon(
        ReadOnlySpan<ScreenVertex> vertices, DepthPlane depth, double sortDepth, int shader)
    {
        int index = AddPolygon(vertices, depth, sortDepth);
        _primitives[index].Shader = shader;
        return index;
    }

    /// <summary>One primitive's vertices.</summary>
    /// <param name="primitive">The primitive.</param>
    public ReadOnlySpan<ScreenVertex> VerticesOf(in DisplayPrimitive primitive) =>
        _vertices.AsSpan(primitive.VertexStart, primitive.VertexCount);

    /// <summary>The sprite a <see cref="PrimitiveKind.Sprite"/> primitive draws.</summary>
    /// <param name="index">The primitive's index.</param>
    public SpriteImage? SpriteAt(int index) => _sprites[index];

    /// <summary>
    /// The OUTPUT column: how many target pixels primitive <paramref name="index"/> wrote.
    /// </summary>
    /// <param name="index">The primitive's index.</param>
    /// <remarks>
    /// The HOW stage fills this; the WHAT stage reads it back after the draw to feed the census and
    /// the projection watch, which is what keeps those hooks above the boundary.
    /// </remarks>
    public long PixelsAt(int index) => _pixels[index];

    /// <summary>Records what one primitive wrote.</summary>
    /// <param name="index">The primitive's index.</param>
    /// <param name="pixels">Target pixels written.</param>
    public void SetPixels(int index, long pixels) => _pixels[index] = pixels;

    /// <summary>
    /// ADDS what one tile wrote for a primitive to its total.
    /// </summary>
    /// <param name="index">The primitive's index.</param>
    /// <param name="pixels">Target pixels this tile wrote for it.</param>
    /// <remarks>
    /// A primitive can straddle several tiles and those tiles can be drawn by different threads, so
    /// the column is accumulated atomically.  Addition of integers is associative and commutative,
    /// so the total is the same whatever order the tiles finish in — which is what makes the census
    /// numbers thread-count invariant.  The counter starts at 0 because <see cref="Clear"/>'s
    /// successor <c>Begin</c> zeroes each entry as the primitive is appended.
    /// </remarks>
    public void AddPixels(int index, long pixels)
    {
        if (pixels != 0)
        {
            Interlocked.Add(ref _pixels[index], pixels);
        }
    }

    /// <summary>The census tag's class name, or null when the primitive is not watched.</summary>
    /// <param name="index">The primitive's index.</param>
    public string? WatchNameAt(int index) => _watchNames[index];

    /// <summary>The census tag's record index.</summary>
    /// <param name="index">The primitive's index.</param>
    public int WatchRecordAt(int index) => _watchRecords[index];

    /// <summary>Appends a filled convex polygon with an affine depth plane.</summary>
    /// <param name="vertices">Its projected vertices, in order; at least three.</param>
    /// <param name="depth">Its <c>1/z</c> plane.</param>
    /// <param name="sortDepth">View-space Z at its centroid.</param>
    /// <returns>The primitive's index.</returns>
    public int AddPolygon(ReadOnlySpan<ScreenVertex> vertices, DepthPlane depth, double sortDepth)
    {
        int index = Begin(PrimitiveKind.Polygon, vertices, sortDepth);
        _primitives[index].Depth = depth;
        Bounds(index, 0.0);
        return index;
    }

    /// <summary>Appends a screen-space disc at one depth.</summary>
    /// <param name="centre">Its projected centre.</param>
    /// <param name="radius">Its screen radius in target pixels.</param>
    /// <param name="sortDepth">View-space Z at the centre.</param>
    /// <returns>The primitive's index.</returns>
    public int AddDisc(ScreenVertex centre, double radius, double sortDepth)
    {
        int index = Begin(PrimitiveKind.Disc, One(centre), sortDepth);
        _primitives[index].ScalarA = radius;
        Bounds(index, radius);
        return index;
    }

    /// <summary>Appends the block one point record paints.</summary>
    /// <param name="vertex">Its projected position.</param>
    /// <param name="sortDepth">View-space Z there.</param>
    /// <returns>The primitive's index.</returns>
    public int AddPoint(ScreenVertex vertex, double sortDepth)
    {
        int index = Begin(PrimitiveKind.Point, One(vertex), sortDepth);
        Bounds(index, PointMargin);
        return index;
    }

    /// <summary>Appends a segment with a half-width at each end.</summary>
    /// <param name="a">Its first projected endpoint.</param>
    /// <param name="b">Its second.</param>
    /// <param name="halfA">Half its screen width at <paramref name="a"/>, in target pixels.</param>
    /// <param name="halfB">Half its screen width at <paramref name="b"/>.</param>
    /// <param name="sortDepth">View-space Z at the midpoint.</param>
    /// <returns>The primitive's index.</returns>
    public int AddCapsule(
        ScreenVertex a, ScreenVertex b, double halfA, double halfB, double sortDepth)
    {
        int index = Begin(PrimitiveKind.Capsule, Two(a, b), sortDepth);
        _primitives[index].ScalarA = halfA;
        _primitives[index].ScalarB = halfB;
        Bounds(index, Math.Max(halfA, halfB));
        return index;
    }

    /// <summary>Appends a palette-indexed billboard.</summary>
    /// <param name="centre">Its projected centre; the whole billboard shares its depth.</param>
    /// <param name="width">The destination width in target pixels.</param>
    /// <param name="height">The destination height.</param>
    /// <param name="sprite">The source raster and its colour key.</param>
    /// <param name="sortDepth">View-space Z at the centre.</param>
    /// <returns>The primitive's index.</returns>
    public int AddSprite(
        ScreenVertex centre, double width, double height, SpriteImage sprite, double sortDepth)
    {
        int index = Begin(PrimitiveKind.Sprite, One(centre), sortDepth);
        _primitives[index].ScalarA = width;
        _primitives[index].ScalarB = height;
        _sprites[index] = sprite;
        Bounds(index, 0.5 * Math.Max(width, height));
        return index;
    }

    /// <summary>Appends a segment with a radial glow profile.</summary>
    /// <param name="a">Its first projected endpoint.</param>
    /// <param name="b">Its second.</param>
    /// <param name="radiusA">The glow radius at <paramref name="a"/>, in target pixels.</param>
    /// <param name="radiusB">The glow radius at <paramref name="b"/>.</param>
    /// <param name="sortDepth">View-space Z at the midpoint.</param>
    /// <returns>The primitive's index.</returns>
    public int AddHalo(
        ScreenVertex a, ScreenVertex b, double radiusA, double radiusB, double sortDepth)
    {
        int index = Begin(PrimitiveKind.Halo, Two(a, b), sortDepth);
        _primitives[index].ScalarA = radiusA;
        _primitives[index].ScalarB = radiusB;
        Bounds(index, Math.Max(radiusA, radiusB));
        return index;
    }

    private ReadOnlySpan<ScreenVertex> One(ScreenVertex v)
    {
        _scratch[0] = v;
        return _scratch.AsSpan(0, 1);
    }

    private ReadOnlySpan<ScreenVertex> Two(ScreenVertex a, ScreenVertex b)
    {
        _scratch[0] = a;
        _scratch[1] = b;
        return _scratch.AsSpan(0, 2);
    }

    private readonly ScreenVertex[] _scratch = new ScreenVertex[2];

    private int Begin(PrimitiveKind kind, ReadOnlySpan<ScreenVertex> vertices, double sortDepth)
    {
        EnsurePrimitives(_count + 1);
        EnsureVertices(_vertexCount + vertices.Length);
        vertices.CopyTo(_vertices.AsSpan(_vertexCount));

        int index = _count;
        ref DisplayPrimitive primitive = ref _primitives[index];
        primitive = default;
        primitive.Kind = kind;
        primitive.VertexStart = _vertexCount;
        primitive.VertexCount = vertices.Length;
        primitive.Color = Style.Color;
        primitive.Coverage = Style.Coverage;
        primitive.Profile = Style.Profile;
        primitive.Pattern = Style.Pattern;
        primitive.Group = Style.Group;
        primitive.Combine = Style.Combine;
        primitive.Layer = Style.Layer;
        primitive.Priority = Style.Priority;
        primitive.SubmissionIndex = index;
        primitive.SortDepth = sortDepth;
        primitive.Shader = -1;

        _vertexCount += vertices.Length;
        _pixels[index] = 0;
        _watchNames[index] = Style.WatchName;
        _watchRecords[index] = Style.WatchRecord;
        _sprites[index] = null;
        _count++;
        return index;
    }

    private void Bounds(int index, double margin)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (ScreenVertex v in VerticesOf(_primitives[index]))
        {
            minX = Math.Min(minX, v.X);
            maxX = Math.Max(maxX, v.X);
            minY = Math.Min(minY, v.Y);
            maxY = Math.Max(maxY, v.Y);
        }

        ref DisplayPrimitive primitive = ref _primitives[index];
        primitive.MinX = minX - margin;
        primitive.MinY = minY - margin;
        primitive.MaxX = maxX + margin;
        primitive.MaxY = maxY + margin;
    }

    private void EnsurePrimitives(int needed)
    {
        if (_primitives.Length >= needed)
        {
            return;
        }

        int size = Math.Max(needed, _primitives.Length * 2);
        Array.Resize(ref _primitives, size);
        Array.Resize(ref _pixels, size);
        Array.Resize(ref _watchNames, size);
        Array.Resize(ref _watchRecords, size);
        Array.Resize(ref _sprites, size);
    }

    private void EnsureVertices(int needed)
    {
        if (_vertices.Length >= needed)
        {
            return;
        }

        Array.Resize(ref _vertices, Math.Max(needed, _vertices.Length * 2));
    }
}
