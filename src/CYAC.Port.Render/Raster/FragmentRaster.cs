using CYAC.Port.Render.Pipeline;

namespace CYAC.Port.Render.Raster;

/// <summary>
/// The FRAGMENT RASTER: a per-tile fragment buffer with analytic area coverage, resolved
/// front-to-back per pixel.
/// </summary>
/// <remarks>
/// <para>
/// This is the HOW stage's rasteriser and the heart of the design (the only one: the depth-tested
/// painter <c>ScanRaster</c> and the <c>ITileRaster</c> seam between them retired).  A <c>Fill…</c>
/// call writes no pixel: it emits one FRAGMENT per pixel it touches, carrying the exact fraction of
/// that pixel's AREA the primitive covers (<see cref="TileCoverage"/>), the depth at the pixel's
/// CENTRE, a colour and a sort key.  <see cref="EndTile"/> then walks each pixel's fragments
/// front-to-back and composites coverage as alpha.
/// </para>
/// <para>
/// <b>Anti-aliasing and translucency become one operation.</b> An aircraft covering 3 % of a pixel
/// contributes 3 % of its colour and slides continuously as it moves (the anti-shimmer law, at its
/// strongest reading); a 25 % puff over it contributes 25 %; a puff behind an opaque wing
/// contributes nothing, because the wing's fragment is nearer. Super-sampling buys nothing against
/// it — retired in R4.
/// </para>
/// <para>
/// <b>The combine rules</b> ("the seam rule") decide what happens when several fragments of ONE
/// object land on one pixel:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="CombineRule.Add"/> — a tessellated surface: CONSECUTIVE fragments
///   of the group form ONE layer whose coverage is the (clamped) SUM and whose colour is the
///   coverage-weighted mean.  Two abutting faces each covering half of an edge pixel give exactly 1,
///   so no background bleeds along a shared edge.  The run stops as soon as the sum reaches 1: a
///   full-coverage fragment simply wins, and nothing behind it inside the same instance is mixed in.
///   Consecutive is the right relation here — a fragment of another object at an intermediate depth
///   really does separate the two faces.</description></item>
///   <item><description><see cref="CombineRule.Max"/> — an overlap group (the seven-disc
///   <c>cloud</c>): the pixel takes the LARGEST coverage anywhere in the group and the front-most
///   member's colour, so the group is one layer however many of its parts overlap.  Here the
///   relation is the WHOLE pixel, not a run: the rule stands for "the original's single fixed
///   screen-space stipple mask", which does not care what depth another object happens to sit at.
///   It replaced R1/R2's blend-stamp buffer, which R4 deleted.</description></item>
///   <item><description><see cref="CombineRule.Over"/> — different objects: ordinary front-to-back
///   compositing, which is where smoke accumulates.</description></item>
///   <item><description><see cref="CombineRule.Glow"/> — the tracer halo, whose own
///   <see cref="TracerHalo.Composite"/> is a RELATIVE lightening of what is already there and
///   therefore cannot be an alpha at all.  Glow fragments are applied last, back to front, over the
///   resolved colour, and are depth-tested against the opaque guard.</description></item>
/// </list>
/// <para>
/// <b>The opaque depth guard</b> is what bounds the memory: the nearest fragment that covers a pixel
/// completely records its key, and any later fragment sorting BEHIND that key is dropped at
/// insertion.  The output cannot change, because reaching such a fragment in the resolve would mean
/// passing the covering one first — and that one takes the accumulated alpha to 1, where the walk
/// stops.
/// </para>
/// <para>
/// <b>There are no depth biases.</b> existed to break coplanar ties in a depth-buffered painter
/// (<c>SceneRenderer.LayerBiasPerPriority</c>, <c>EdgeBias</c>, <c>BackFacePenalty</c>,
/// <c>InfinityDepthBias</c>); the SORT KEY does that job with <see cref="DisplayPrimitive.Layer"/>,
/// <see cref="DisplayPrimitive.Priority"/> and <see cref="DisplayPrimitive.SubmissionIndex"/>, and
/// R4 deleted them.
/// </para>
/// <para>
/// <b>THE CONTRACT THAT MAKES TILING EXACT</b> (and the law R3 was designed around): this class
/// rasterises in ABSOLUTE target coordinates and merely RESTRICTS its loops to the tile rectangle.
/// It never translates a vertex by the tile origin — an integer offset would change the
/// floating-point rounding of an edge crossing or a depth-plane evaluation and the picture would
/// then depend on the tile size — and no per-pixel quantity may be a function of anything
/// accumulated across pixels (which is why the coverage is a pure per-pixel clip-and-shoelace and
/// not a signed-area sweep).  The same law is why the retro dither indexes its matrix
/// by the absolute pixel.  It reads and writes only pixels inside the rectangle, so tiles need no
/// synchronisation on the target.
/// </para>
/// <para>
/// <b>The terminal background</b> is whatever the target pixel already holds — the horizon fill and
/// the analytic ground layer still paint before the display list, and the resolve composites onto
/// them (<c>C += (1 − A)·dst</c>).  Folding those into the list as
/// <see cref="DrawLayer.Background"/> fills comes later.
/// </para>
/// <para>
/// <b>One compositing law.</b>  The final write goes through <see cref="Blend.Over"/>, which the
/// analytic ground layer uses too, so a 50 % layer is the same colour wherever it is painted.
/// The fragment resolve accumulates in <c>double</c> and quantises once, at the end.
/// </para>
/// <para>
/// <b>The two looks</b> are frame-wide choices carried in <see cref="ResolveStyle"/> and applied to
/// the fragment's TWO independent terms, which is why the fragment carries them separately
/// (<see cref="Emit"/>):
/// </para>
/// <list type="bullet">
///   <item><description><see cref="ResolveStyle.Dither"/> quantises the TRANSLUCENCY alpha to 0/1
///   through an 8×8 screen-space ordered matrix at the host's own pixel pitch
///   (<see cref="OrderedDither"/>).  Every EDGE stays analytic; a 50 % record draws the
///   checkerboard its <c>0x5A</c> selector drew, a soft puff draws the classic radial gradient of
///   dots.</description></item>
///   <item><description><see cref="ResolveStyle.Edges"/> = <see cref="EdgeMode.Hard"/> quantises
///   the AREA term instead: 1 where the pixel's CENTRE is inside the primitive, 0 elsewhere.  With
///   the dither it is the full retro look.</description></item>
/// </list>
/// <para>
/// Every buffer is grow-only; a steady-state frame allocates nothing on any worker.
/// </para>
/// </remarks>
internal sealed class FragmentRaster
{
    /// <summary>How many segments the smallest polygonised disc gets.</summary>
    private const int MinDiscSegments = 8;

    /// <summary>And the largest — a cost guard, not an accuracy one.</summary>
    private const int MaxDiscSegments = 512;

    /// <summary>
    /// The largest radial error a polygonised disc may have, in target pixels: 1/16.
    /// </summary>
    private const double DiscRimTolerance = 1.0 / 16.0;

    /// <summary>How many bits of <see cref="Fragment.Meta"/> the overlap group occupies.</summary>
    private const int GroupBits = 28;

    /// <summary>The group's mask inside <see cref="Fragment.Meta"/>.</summary>
    private const int GroupMask = (1 << GroupBits) - 1;

    private readonly TileCoverage _coverage = new();

    private Fragment[] _pool = new Fragment[4096];
    private int _poolCount;

    private int[] _head = [];
    private int[] _length = [];

    // Per TILE COLUMN, the first and last row a fragment reached.  Outside that span the column
    // is the background and nothing else, so it is a Span.Fill of one colour per zone instead of
    // a per-pixel evaluation — which is what keeps a sky tile as cheap as the memset the retired
    // horizon painter used to do (§4 of the report).
    private int[] _columnFirst = [];
    private int[] _columnLast = [];
    private ulong[] _guardKey = [];
    private float[] _guardDepth = [];

    // THE OPAQUE SPRITE SLOT.  A fully-covering, depth-writing sprite pixel is the nearest opaque
    // thing at its pixel more often than not (the bitmap explosion, 0.5 M pixels at 1080p and 2.2 M
    // at 4K, every one a fragment), and a fragment that is the guard itself needs no pool entry and
    // no list walk: it is the pixel's TERMINAL colour.  So such a pixel is written here — key +
    // colour — and the resolve composites the list's fragments in front of it (key < slot) over it,
    // exactly as it composites them over the background.  Pixels the slot serves alone never enter
    // Resolve at all.  Measured serial cost per sprite pixel before this: ~10 ns; after: the two
    // stores.  Bit-identical to the list path by construction
    // (FragmentRasterTests.AnOpaqueSpriteThroughTheSlotMatchesTheListPath).
    private ulong[] _slotKey = [];
    private uint[] _slotColor = [];
    private int[] _spriteRows = [];

    /// <summary>Test seam: route opaque sprites through the list instead of the slot.</summary>
    internal bool UseSpriteSlot { get; set; } = true;
    private int[] _glow = new int[16];

    private ScreenVertex[] _shape = new ScreenVertex[MinDiscSegments];

    private float[] _depth = [];
    private int _height;
    private ResolveStyle _style = ResolveStyle.Default;

    /// <summary>The frame's TERMINAL FUNCTION: shared, read-only.</summary>
    private BackgroundField? _background;
    private InteriorMask? _mask;
    private long _seamPixels;

    /// <summary>
    /// How far apart in <c>1/z</c>, relative to the nearer, two Add-group members may be and
    /// still count as ADJACENT faces of one surface at an interior pixel: 2 %.  Faces that share
    /// the edge a pixel straddles differ by a sliver; the fuselage a wing crease is seen against
    /// sits feet behind it.
    /// </summary>
    internal const float AdjacentDepthTolerance = 0.02f;

    private bool SameAddGroup(int k, int group)
    {
        ref Fragment member = ref _pool[k];
        return !member.Consumed
            && (CombineRule)((member.Meta >> GroupBits) & 7) == CombineRule.Add
            && (member.Meta & GroupMask) == group;
    }

    private long _backgroundPixels;

    private int _originX;
    private int _originY;
    private int _tileWidth;
    private int _tileHeight;
    private int _clipX0;
    private int _clipX1 = -1;
    private int _clipY0;
    private int _clipY1 = -1;

    private ulong _keyBase;
    private bool _keyUsesDepth;
    private int _keyGroup;
    private CombineRule _keyCombine;
    private TracerHalo _halo = TracerHalo.Default;

    private long _inserted;
    private long _dropped;
    private long _trivialAccepts;
    private int _maxList;

    /// <summary>
    /// How many fragments this rasteriser has emitted since <see cref="BeginFrame"/>.
    /// </summary>
    /// <remarks>
    /// The fragment raster writes its pixels in <see cref="EndTile"/>, long after the <c>Fill…</c>
    /// call that produced them, so a per-primitive PIXEL count is not available where the tile
    /// worker brackets its calls.  What IS available, and is what the census
    /// actually wants, is how many pixels a primitive CONTRIBUTED to: its fragments.  A fragment
    /// the opaque guard drops is not counted — it could never have been visible.
    /// </remarks>
    public long PixelsWritten => _inserted;

    /// <summary>The fragment buffer's own census.</summary>
    public FragmentStats Fragments =>
        new(_inserted, _dropped, _maxList, _backgroundPixels, _trivialAccepts, _seamPixels);

    /// <summary>
    /// The TOTAL INK the frame has emitted: the sum of every kept fragment's coverage.
    /// </summary>
    /// <remarks>
    /// A diagnostic, not an output: it is how the invariance tests state coverage conservation — a
    /// polygon's summed coverage is its area, and a sub-pixel translation moves the picture without
    /// changing the total.  Summed in <c>double</c> in emission order, so it is stable for one
    /// worker and only approximately so across several; nothing the renderer draws depends on it.
    /// </remarks>
    public double EmittedCoverage { get; private set; }

    /// <summary>Starts a frame.</summary>
    /// <param name="depth">The frame's shared <c>1/z</c> buffer, already cleared.</param>
    /// <param name="targetWidth">The target's width.</param>
    /// <param name="targetHeight">Its height.</param>
    /// <param name="style">the frame's look: the retro dither and the edge rule.</param>
    /// <param name="background">
    /// The frame's TERMINAL FUNCTION: the colour of a pixel no fragment covers
    /// (<see cref="BackgroundField"/>).  Every worker shares one instance and only reads it, so it
    /// needs no lock; it replaces the resolve's former read of the pixel as it stood, and with it
    /// the last <c>Clear</c> in the world path.
    /// </param>
    public void BeginFrame(
        float[] depth,
        int targetWidth,
        int targetHeight,
        in ResolveStyle style,
        BackgroundField background,
        InteriorMask? mask = null)
    {
        ArgumentNullException.ThrowIfNull(depth);
        ArgumentNullException.ThrowIfNull(background);
        _depth = depth;
        _height = targetHeight;
        _style = style;
        _background = background;
        _mask = mask is { IsActive: true } ? mask : null;
        _seamPixels = 0;
        _inserted = 0;
        _dropped = 0;
        _maxList = 0;
        _backgroundPixels = 0;
        _trivialAccepts = 0;
        EmittedCoverage = 0.0;

        // Until the first BeginTile the clip rectangle is EMPTY, so a fill issued outside a tile
        // paints nothing rather than indexing a fragment buffer that has not been sized.
        _clipX0 = 0;
        _clipX1 = -1;
        _clipY0 = 0;
        _clipY1 = -1;
    }

    /// <summary>Starts one tile: an empty fragment buffer over its pixels.</summary>
    /// <param name="originX">The tile's left column, in absolute target pixels.</param>
    /// <param name="originY">Its top row.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <remarks>
    /// Nothing is cleared here.  <see cref="EndTile"/> resets exactly the pixels it resolved and the
    /// buffers are born empty, so the invariant "every head is −1 when a tile starts" holds without
    /// a per-tile memset — which at 4K would have been tens of megabytes of it per frame.
    /// </remarks>
    public void BeginTile(int originX, int originY, int width, int height)
    {
        _originX = originX;
        _originY = originY;
        _tileWidth = width;
        _tileHeight = height;
        _clipX0 = originX;
        _clipX1 = originX + width - 1;
        _clipY0 = originY;
        _clipY1 = originY + height - 1;
        _poolCount = 0;

        GrowTileBuffers(width * height);
        GrowColumnBuffers(width);

        Array.Fill(_columnFirst, int.MaxValue, 0, width);
        Array.Fill(_columnLast, -1, 0, width);

        _coverage.BeginTile(originY, height);
    }

    /// <summary>
    /// The sizes of this raster's five LAZILY GROWN scratch buffers.
    /// </summary>
    /// <param name="TilePixels">
    /// The per-pixel fragment heads, lengths and opaque guards (<c>tileWidth × tileHeight</c>).
    /// </param>
    /// <param name="TileColumns">The per-column first/last touched row (<c>tileWidth</c>).</param>
    /// <param name="Fragments">The fragment pool.</param>
    /// <param name="Glow">The per-pixel glow list the resolve collects on its way down.</param>
    /// <param name="ShapeVertices">The polygonised-disc / sprite-quad vertex scratch.</param>
    /// <remarks>
    /// See <see cref="EnsureScratch"/> for why this is public to the drawer at all.
    /// </remarks>
    internal readonly record struct ScratchCapacity(
        int TilePixels, int TileColumns, int Fragments, int Glow, int ShapeVertices)
    {
        /// <summary>The element-wise maximum of two capacities.</summary>
        /// <param name="a">One.</param>
        /// <param name="b">The other.</param>
        public static ScratchCapacity Max(in ScratchCapacity a, in ScratchCapacity b) =>
            new(
                Math.Max(a.TilePixels, b.TilePixels),
                Math.Max(a.TileColumns, b.TileColumns),
                Math.Max(a.Fragments, b.Fragments),
                Math.Max(a.Glow, b.Glow),
                Math.Max(a.ShapeVertices, b.ShapeVertices));
    }

    /// <summary>How big this raster's scratch buffers have grown.</summary>
    internal ScratchCapacity Scratch =>
        new(_head.Length, _columnFirst.Length, _pool.Length, _glow.Length, _shape.Length);

    /// <summary>
    /// Grows this raster's scratch to at least <paramref name="capacity"/>, and to no more than the
    /// element-wise maximum of that and what it already holds.
    /// </summary>
    /// <param name="capacity">The sizes to reach.</param>
    /// <remarks>
    /// <para>
    /// <b>This is the cure for a flaky "steady-state frames allocate nothing" assertion, and the
    /// allocation it removes was REAL, not a measurement artefact.</b> Every one of these buffers
    /// grows on demand from whatever the worker's own tiles have needed so far — the fragment pool
    /// from the busiest tile's fragment count, the head/guard arrays from the largest tile — and the
    /// scheduler decides which worker meets which tile (<see cref="Pipeline.TileScheduler"/>: "every
    /// worker takes the NEXT tile").  So a worker that had drawn only sky tiles for twenty frames
    /// would allocate the first time the dynamic schedule handed it the horizon, on any frame,
    /// however warm the renderer was.  Measured: 524,312 B on the calling thread, the fragment pool
    /// doubling 8,192 → 16,384 entries of 32 B, in 2 runs of 9.
    /// </para>
    /// <para>
    /// The drawer therefore equalises every worker's scratch to the frame's maximum once per frame,
    /// on the calling thread, between dispatches.  The buffers hold nothing across a tile — the
    /// pool is reset by <see cref="BeginTile"/> and the heads by <c>EndTile</c> — so growing one is
    /// a fresh array, never a copy, and the invariants the fill establishes are re-established here.
    /// After it, a schedule change cannot allocate: what any worker has needed, all of them have.
    /// </para>
    /// <para>
    /// <b>The levelling converges only because this is EXACT.</b> The drawer feeds every worker the
    /// largest capacity any worker holds, so any slack added here becomes the next frame's maximum,
    /// which every other worker is then levelled to, with slack again.  The per-tile buffers used to
    /// add that slack (they grew by doubling), and a worker holding more than half the maximum was
    /// levelled to twice its own size: the capacity doubled every two frames with no bound, past 10
    /// GB in under twenty frames of the headless mig15 mission at 800 × 500.  Every buffer here is
    /// therefore sized to exactly what is asked
    /// (<c>TileInvarianceTests.LevellingScratchReachesTheFrameMaximumAndNeverPassesIt</c>).
    /// </para>
    /// </remarks>
    internal void EnsureScratch(in ScratchCapacity capacity)
    {
        GrowTileBuffers(capacity.TilePixels);
        GrowColumnBuffers(capacity.TileColumns);

        if (_pool.Length < capacity.Fragments)
        {
            _pool = new Fragment[capacity.Fragments];
        }

        if (_glow.Length < capacity.Glow)
        {
            _glow = new int[capacity.Glow];
        }

        if (_shape.Length < capacity.ShapeVertices)
        {
            _shape = new ScreenVertex[capacity.ShapeVertices];
        }
    }

    /// <summary>Sizes the per-PIXEL tile buffers, with the invariants a fresh tile expects.</summary>
    /// <param name="pixels">How many pixels the buffers must hold.</param>
    /// <remarks>
    /// Sized to EXACTLY <paramref name="pixels"/>, never by doubling.  The demand is one tile's area,
    /// which the tile size bounds, so there is no open-ended growth to amortise: a worker meets at
    /// most the four areas of a target's tile grid (full, right edge, bottom edge, corner).  And
    /// <see cref="EnsureScratch"/> passes through here, where a doubling would feed the per-frame
    /// levelling back into itself.
    /// </remarks>
    private void GrowTileBuffers(int pixels)
    {
        if (_head.Length >= pixels)
        {
            return;
        }

        _head = new int[pixels];
        Array.Fill(_head, -1);
        _length = new int[pixels];
        _guardKey = new ulong[pixels];
        Array.Fill(_guardKey, ulong.MaxValue);
        _guardDepth = new float[pixels];
        _slotKey = new ulong[pixels];
        Array.Fill(_slotKey, ulong.MaxValue);
        _slotColor = new uint[pixels];
    }

    /// <summary>Sizes the per-COLUMN tile buffers (their contents are filled per tile).</summary>
    /// <param name="columns">How many columns the buffers must hold.</param>
    /// <remarks>Exactly <paramref name="columns"/>, for the reason <see cref="GrowTileBuffers"/>
    /// gives.</remarks>
    private void GrowColumnBuffers(int columns)
    {
        if (_columnFirst.Length >= columns)
        {
            return;
        }

        _columnFirst = new int[columns];
        _columnLast = new int[columns];
    }

    /// <summary>THE RESOLVE: turns the tile's fragments into pixels.</summary>
    /// <param name="target">The pixels.</param>
    /// <remarks>
    /// <para>
    /// One pass over EVERY pixel of the tile, in the target's own column-major order.  A pixel that
    /// received fragments has its list walked front-to-back in key order — each combine group's run
    /// merged, coverage composited as alpha, the walk stopping at <c>A ≥ 0.999</c> — and is finished
    /// against the TERMINAL FUNCTION (<see cref="BackgroundField"/>); the glow fragments collected
    /// on the way are applied afterwards, back to front, over the resolved colour.  A pixel that
    /// received none is the terminal function alone.
    /// </para>
    /// <para>
    /// The walk covers the whole tile now: with no background pass in front of the list, EVERY pixel
    /// of the frame is written exactly once, here.  The list of touched pixels retired with it — a
    /// pixel's per-pixel state is reset exactly when it is read, and the guard/length arrays can
    /// only be non-default where <c>_head</c> is set, so nothing needs a clear.
    /// </para>
    /// </remarks>
    public void EndTile(in PixelTarget target)
    {
        BackgroundField background = _background!;
        int tileEnd = _originY + _tileHeight;
        for (int lx = 0; lx < _tileWidth; lx++)
        {
            int x = _originX + lx;
            BackgroundColumn column = background.Column(x);
            Span<uint> pixels = target.Column(x);
            int columnBase = lx * _tileHeight;

            // The rows a fragment reached; outside them the column IS the background.
            int firstRow = _columnFirst[lx];
            int lastRow = _columnLast[lx];
            if (lastRow < firstRow)
            {
                PaintBackground(pixels, background, in column, _originY, tileEnd);
                continue;
            }

            PaintBackground(pixels, background, in column, _originY, _originY + firstRow);
            PaintBackground(pixels, background, in column, _originY + lastRow + 1, tileEnd);

            for (int ly = firstRow; ly <= lastRow; ly++)
            {
                int index = columnBase + ly;
                int head = _head[index];
                int y = _originY + ly;
                ulong slotKey = _slotKey[index];
                if (head < 0)
                {
                    if (slotKey == ulong.MaxValue)
                    {
                        pixels[y] = background.At(in column, y);
                        _backgroundPixels++;
                        continue;
                    }

                    // The slot alone: the pixel IS the sprite texel, and the guard it set is the
                    // pixel's depth.
                    pixels[y] = _slotColor[index];
                    _depth[(x * _height) + y] = _guardDepth[index];
                    _slotKey[index] = ulong.MaxValue;
                    _guardKey[index] = ulong.MaxValue;
                    _guardDepth[index] = 0f;
                    continue;
                }

                ulong guard = _guardKey[index];
                float guardDepth = _guardDepth[index];
                if (_length[index] > _maxList)
                {
                    _maxList = _length[index];
                }

                _head[index] = -1;
                _length[index] = 0;
                _guardKey[index] = ulong.MaxValue;
                _guardDepth[index] = 0f;
                _slotKey[index] = ulong.MaxValue;
                if (guard != ulong.MaxValue)
                {
                    _depth[(x * _height) + y] = guardDepth;
                }

                Resolve(target, x, y, head, guardDepth, slotKey, _slotColor[index], background, in column);
            }
        }

        _poolCount = 0;
    }

    /// <summary>
    /// Paints a run of one column from the terminal function alone.
    /// </summary>
    /// <param name="pixels">The target's column.</param>
    /// <param name="background">The frame's background function.</param>
    /// <param name="column">Its constants for this column.</param>
    /// <param name="firstRow">The first absolute row to paint.</param>
    /// <param name="endRow">One past the last.</param>
    /// <remarks>
    /// The column's two flat runs are a <see cref="Span{T}.Fill"/> of one colour each — the same
    /// memset the retired horizon painter did — and only the rows between them, where the band ramps
    /// or the split grades, are evaluated per pixel.  That is why folding the background into the
    /// resolve costs a sky tile nothing.
    /// </remarks>
    private void PaintBackground(
        Span<uint> pixels,
        BackgroundField background,
        in BackgroundColumn column,
        int firstRow,
        int endRow)
    {
        if (endRow <= firstRow)
        {
            return;
        }

        int mixedStart = Math.Clamp(column.FirstMixed, firstRow, endRow);
        int mixedEnd = Math.Clamp(column.EndMixed, mixedStart, endRow);
        if (mixedStart > firstRow)
        {
            pixels.Slice(firstRow, mixedStart - firstRow).Fill(column.Above);
        }

        for (int y = mixedStart; y < mixedEnd; y++)
        {
            pixels[y] = background.At(in column, y);
        }

        if (endRow > mixedEnd)
        {
            pixels.Slice(mixedEnd, endRow - mixedEnd).Fill(column.Below);
        }

        _backgroundPixels += endRow - firstRow;
    }

    /// <summary>Announces the sort key every fragment of the next primitive carries.</summary>
    /// <param name="key">Its layer, priority, overlap group, combine rule and submission index.</param>
    /// <remarks>
    /// <para>
    /// The walk order is packed into one <c>ulong</c>, ASCENDING = front to back:
    /// </para>
    /// <list type="table">
    ///   <listheader><term>bits</term><description>meaning</description></listheader>
    ///   <item><term>61…63</term><description><c>7 − Layer</c>: <see cref="DrawLayer.Overlay"/>
    ///   first, then <see cref="DrawLayer.World"/>, then <see cref="DrawLayer.GroundDecal"/>, then
    ///   <see cref="DrawLayer.AtInfinity"/>, then the background</description></item>
    ///   <item><term>30…60</term><description>in <see cref="DrawLayer.World"/> only, the complement
    ///   of the <c>1/z</c> float's bits — nearest first.  Zero in every other layer, which is how
    ///   "ground decals ignore depth" is expressed</description></item>
    ///   <item><term>22…29</term><description><c>255 − Priority</c>: the original's painter priority,
    ///   highest in front (city 0x05 under airport 0x06 under road 0x14 under urban 0x15 under
    ///   river 0x28)</description></item>
    ///   <item><term>0…21</term><description>the complement of the submission index: later-submitted
    ///   is in front, which is painter's order and the final tie-break</description></item>
    /// </list>
    /// <para>
    /// A non-negative <c>float</c>'s bit pattern orders exactly as its value does, and <c>1/z</c> is
    /// never negative, so its sign bit is free and 31 bits are enough for the depth.
    /// </para>
    /// </remarks>
    public void BeginPrimitive(in PrimitiveKey key)
    {
        _keyBase = ((ulong)(7 - (int)key.Layer) << 61)
            | ((ulong)(255 - Math.Clamp(key.Priority, 0, 255)) << 22)
            | ((ulong)(uint)~key.SubmissionIndex & 0x3FFFFFUL);
        _keyUsesDepth = key.Layer == DrawLayer.World;
        _keyGroup = key.Group & GroupMask;
        _keyCombine = key.Combine;
    }

    /// <summary>Fills a projected polygon with exact area coverage.</summary>
    /// <param name="target">The pixels (unused: the resolve writes them).</param>
    /// <param name="polygon">Its projected vertices, in order; at least three.</param>
    /// <param name="depth">Its <c>1/z</c> plane, evaluated at each pixel's centre.</param>
    /// <param name="paint">Its colour and coverage.</param>
    /// <param name="writeDepth">Whether a fully-covering fragment may set the opaque guard.</param>
    public void FillPolygon(
        in PixelTarget target,
        ReadOnlySpan<ScreenVertex> polygon,
        DepthPlane depth,
        Paint paint,
        bool writeDepth)
    {
        _ = target;
        FillPolygon(polygon, depth, paint, writeDepth, new FlatShade(paint.Packed));
    }

    /// <summary>
    /// VECTOR MARKINGS — fills a projected polygon whose colour is computed PER PIXEL by a surface
    /// shader.
    /// </summary>
    /// <param name="target">The pixels (unused: the resolve writes them).</param>
    /// <param name="polygon">Its projected vertices, in order; at least three.</param>
    /// <param name="depth">Its <c>1/z</c> plane.</param>
    /// <param name="paint">Its coverage (its packed colour is the skin the shader starts from).</param>
    /// <param name="writeDepth">Whether a fully-covering fragment may set the opaque guard.</param>
    /// <param name="shade">The per-pixel colour rule.</param>
    /// <remarks>
    /// The SAME loops as the flat fill — trivial accept, the hard centre rule, the exact-area strip
    /// walk — through one struct-generic core, so coverage, the depth key, the group and the combine
    /// rule are untouched and only the colour handed to <c>Emit</c> differs.  The flat fill goes
    /// through the same core with a constant, which the JIT inlines to what it was.
    /// </remarks>
    public void FillPolygon(
        in PixelTarget target,
        ReadOnlySpan<ScreenVertex> polygon,
        DepthPlane depth,
        Paint paint,
        bool writeDepth,
        in MarkedShade shade)
    {
        _ = target;
        FillPolygon(polygon, depth, paint, writeDepth, shade);
    }

    private void FillPolygon<TShade>(
        ReadOnlySpan<ScreenVertex> polygon,
        DepthPlane depth,
        Paint paint,
        bool writeDepth,
        TShade shade)
        where TShade : struct, IPixelShade
    {
        if (polygon.Length < 3 || !(paint.Coverage > 0.0))
        {
            return;
        }

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (ScreenVertex v in polygon)
        {
            if (!double.IsFinite(v.X) || !double.IsFinite(v.Y))
            {
                return;
            }

            minX = Math.Min(minX, v.X);
            maxX = Math.Max(maxX, v.X);
            minY = Math.Min(minY, v.Y);
            maxY = Math.Max(maxY, v.Y);
        }

        int x0 = Math.Max(_clipX0, (int)Math.Floor(minX));
        int x1 = Math.Min(_clipX1, (int)Math.Ceiling(maxX) - 1);
        int y0 = Math.Max(_clipY0, (int)Math.Floor(minY));
        int y1 = Math.Min(_clipY1, (int)Math.Ceiling(maxY) - 1);
        if (x1 < x0 || y1 < y0)
        {
            return;
        }

        // Trivial accept: a tile lying wholly inside the polygon gets coverage 1 on every pixel
        // with no edge work at all — the big win for the screen-spanning decals and for the
        // sky/ground fills that come later.  Bit-identical to the general path, which
        // gives an unmarked interior pixel coverage exactly 1.0 — under BOTH edge rules, since a
        // pixel wholly inside the polygon also has its centre inside it.
        if (TileCoverage.Contains(polygon, x0, y0, x1 + 1, y1 + 1))
        {
            _trivialAccepts++;
            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    double invZ = depth.At(x, y);
                    Emit(
                        x, y, 1.0, paint.Coverage, invZ, shade.Color(x, y, invZ), _keyCombine,
                        writeDepth);
                }
            }

            return;
        }

        // The HARD edge rule: the area term is the classic centre sample, so the exact-area
        // machinery is not run at all and one winding test per pixel is the whole cost.  Coverage
        // conservation deliberately does not hold under it — that IS the retro look, and
        // RetroDitherTests asserts the centre rule directly instead.
        bool hard = _style.Edges == EdgeMode.Hard;

        for (int x = x0; x <= x1; x++)
        {
            if (_coverage.ClipToStrip(polygon, x) < 3)
            {
                continue;
            }

            int rowLo = Math.Max(y0, (int)Math.Floor(_coverage.StripMinY));
            int rowHi = Math.Min(y1, (int)Math.Floor(_coverage.StripMaxY));
            if (rowHi < rowLo)
            {
                continue;
            }

            if (hard)
            {
                for (int y = rowLo; y <= rowHi; y++)
                {
                    // The strip clip only cuts the polygon at x and x+1, and the sample is inside
                    // that strip, so the winding of the clipped polygon at it IS the winding of the
                    // whole polygon at it.
                    if (_coverage.Inside(x + 0.5, y + 0.5))
                    {
                        double invZ = depth.At(x, y);
                        Emit(
                            x, y, 1.0, paint.Coverage, invZ, shade.Color(x, y, invZ), _keyCombine,
                            writeDepth);
                    }
                }

                continue;
            }

            _coverage.MarkBoundaryRows(x, rowLo, rowHi);
            bool inside = false;
            bool known = false;
            for (int y = rowLo; y <= rowHi; y++)
            {
                double area;
                if (_coverage.IsBoundaryRow(y))
                {
                    area = _coverage.RowArea(y);
                    known = false;
                }
                else
                {
                    if (!known)
                    {
                        // No edge crosses this run of rows inside this column, so the winding is
                        // constant over the whole run: one test settles all of it.
                        inside = _coverage.Inside(x + 0.5, y + 0.5);
                        known = true;
                    }

                    area = inside ? 1.0 : 0.0;
                }

                if (!(area > 0.0))
                {
                    continue;
                }

                double invZ = depth.At(x, y);
                Emit(
                    x, y, area, paint.Coverage, invZ, shade.Color(x, y, invZ), _keyCombine,
                    writeDepth);
            }
        }
    }

    /// <summary>Fills a screen-space disc at one depth.</summary>
    /// <param name="target">The pixels (unused).</param>
    /// <param name="centre">Its projected centre.</param>
    /// <param name="radius">Its screen radius in target pixels.</param>
    /// <param name="paint">Its colour and coverage.</param>
    /// <param name="writeDepth">Whether it may set the opaque guard.</param>
    /// <remarks>
    /// <para>
    /// A HARD disc is polygonised and goes through the exact-area path, at a segment count chosen
    /// from the radius so the rim never deviates by more than 1/16 of a pixel, and at the
    /// AREA-PRESERVING radius <c>R·√(θ/sin θ)</c> so the polygon has exactly the disc's area
    /// whatever <c>n</c> is.  A disc SMALLER than a pixel therefore contributes its true area to the
    /// pixels it touches instead of the whole-pixel block a centre-sampled point paints — this is
    /// "distant objects are precisely averaged" and it is where the port stops flickering.
    /// </para>
    /// <para>
    /// A SOFT disc (<see cref="Paint.Soft"/>) already falls to zero at its rim, so its coverage is
    /// the profile <c>1 − (d/R)²</c> at the pixel's centre times the record's own coverage and its
    /// edge needs no area term.
    /// </para>
    /// </remarks>
    public void FillDisc(
        in PixelTarget target,
        ScreenVertex centre,
        double radius,
        Paint paint,
        bool writeDepth)
    {
        if (!(radius > 0.0) || !double.IsFinite(radius) || !(paint.Coverage > 0.0)
            || !double.IsFinite(centre.X) || !double.IsFinite(centre.Y))
        {
            return;
        }

        if (paint.Soft)
        {
            SoftDisc(centre, radius, paint);
            return;
        }

        int segments = SegmentsFor(radius);
        if (_shape.Length < segments)
        {
            _shape = new ScreenVertex[Math.Max(segments, _shape.Length * 2)];
        }

        double step = 2.0 * Math.PI / segments;
        double equalArea = radius * Math.Sqrt(step / Math.Sin(step));
        for (int i = 0; i < segments; i++)
        {
            double angle = i * step;
            _shape[i] = new ScreenVertex(
                centre.X + (equalArea * Math.Cos(angle)),
                centre.Y + (equalArea * Math.Sin(angle)),
                centre.InvZ);
        }

        FillPolygon(
            target,
            _shape.AsSpan(0, segments),
            new DepthPlane(0.0, 0.0, centre.InvZ),
            paint,
            writeDepth);
    }

    /// <summary>Writes the block one point record paints — as an exactly-covered square.</summary>
    /// <param name="target">The pixels (unused).</param>
    /// <param name="vertex">Its projected position.</param>
    /// <param name="paint">Its colour and coverage.</param>
    /// <param name="writeDepth">Whether it may set the opaque guard.</param>
    /// <remarks>
    /// One pixel square CENTRED on the sample, through the polygon path — so a point that falls between two pixels contributes its
    /// true share to each instead of jumping from one to the other.
    /// </remarks>
    public void DrawPoint(
        in PixelTarget target, ScreenVertex vertex, Paint paint, bool writeDepth)
    {
        if (!double.IsFinite(vertex.X) || !double.IsFinite(vertex.Y))
        {
            return;
        }

        const double half = 0.5;
        Quad(
            vertex.X - half, vertex.Y - half, vertex.X + half, vertex.Y + half, vertex.InvZ);
        FillPolygon(
            target,
            _shape.AsSpan(0, 4),
            new DepthPlane(0.0, 0.0, vertex.InvZ),
            paint,
            writeDepth);
    }

    // Retired with PrimitiveKind.Line, its only caller.  Every line the game draws has a real
    // width now; a zero-width segment is a degenerate capsule and paints nothing, which is what a
    // line of no width should do.

    /// <summary>Blits a palette-indexed billboard at a constant depth.</summary>
    /// <param name="target">The pixels (unused).</param>
    /// <param name="sprite">The source raster and its colour key.</param>
    /// <param name="centre">Its projected centre.</param>
    /// <param name="width">The destination width in target pixels.</param>
    /// <param name="height">The destination height.</param>
    /// <param name="opacity">A coverage multiplier, 0…1.</param>
    /// <param name="palette">The 256 packed colours, in the target's channel order.</param>
    /// <param name="writeDepth">Whether it may set the opaque guard.</param>
    /// <remarks>
    /// One fragment per covered pixel, its colour the NEAREST source sample as before and its
    /// coverage the billboard's opacity.  Area-sampling the source and anti-aliasing the
    /// billboard's own rectangle are refinements that can be added later without changing anything
    /// above this call.
    /// </remarks>
    public void DrawSprite(
        in PixelTarget target,
        SpriteImage sprite,
        ScreenVertex centre,
        double width,
        double height,
        double opacity,
        ReadOnlySpan<uint> palette,
        bool writeDepth)
    {
        ArgumentNullException.ThrowIfNull(sprite);
        _ = target;
        if (width < 1.0 || height < 1.0 || palette.Length < 256)
        {
            return;
        }

        double coverage = Math.Clamp(opacity, 0.0, 1.0);
        if (!(coverage > 0.0))
        {
            return;
        }

        double left = centre.X - (width / 2.0);
        double top = centre.Y - (height / 2.0);
        int xStart = Math.Max(_clipX0, (int)Math.Ceiling(left));
        int xEnd = Math.Min(_clipX1, (int)Math.Floor(left + width) - 1);
        int yStart = Math.Max(_clipY0, (int)Math.Ceiling(top));
        int yEnd = Math.Min(_clipY1, (int)Math.Floor(top + height) - 1);
        if (xEnd < xStart || yEnd < yStart)
        {
            return;
        }

        if (UseSpriteSlot && writeDepth && coverage >= Blend.OpaqueCoverage)
        {
            DrawSpriteOpaque(sprite, centre.InvZ, left, top, width, height, xStart, xEnd, yStart, yEnd, palette);
            return;
        }

        for (int x = xStart; x <= xEnd; x++)
        {
            int sx = (int)((x + 0.5 - left) * sprite.Width / width);
            for (int y = yStart; y <= yEnd; y++)
            {
                int sy = (int)((y + 0.5 - top) * sprite.Height / height);
                byte index = sprite.At(sx, sy);
                if (index == sprite.ColorKey)
                {
                    continue;
                }

                Emit(x, y, 1.0, coverage, centre.InvZ, palette[index], _keyCombine, writeDepth);
            }
        }
    }

    /// <summary>
    /// An OPAQUE, depth-writing sprite through the slot (see the field's remarks): the same texel
    /// selection as the general loop, the same key, the same guard rule, and no fragment.  The
    /// dither is irrelevant here: at alpha 1 <see cref="OrderedDither.Quantise"/> is 1 at every
    /// pixel, so the retro look sees exactly what it saw.
    /// </summary>
    private void DrawSpriteOpaque(
        SpriteImage sprite,
        double invZ,
        double left,
        double top,
        double width,
        double height,
        int xStart,
        int xEnd,
        int yStart,
        int yEnd,
        ReadOnlySpan<uint> palette)
    {
        // The key and the depth are the SAME for every pixel of a billboard (Emit's own arithmetic,
        // hoisted): it is one screen-parallel plane.
        float z = (float)invZ;
        if (!(z > 0f))
        {
            z = 0f;
        }

        ulong key = _keyUsesDepth
            ? _keyBase | ((ulong)(~BitConverter.SingleToUInt32Bits(z) & 0x7FFFFFFFu) << 30)
            : _keyBase;

        int rows = yEnd - yStart + 1;
        if (_spriteRows.Length < rows)
        {
            _spriteRows = new int[Math.Max(rows, _spriteRows.Length * 2)];
        }

        for (int y = yStart; y <= yEnd; y++)
        {
            _spriteRows[y - yStart] = (int)((y + 0.5 - top) * sprite.Height / height);
        }

        byte colorKey = sprite.ColorKey;
        int localY0 = yStart - _originY;
        for (int x = xStart; x <= xEnd; x++)
        {
            int sx = (int)((x + 0.5 - left) * sprite.Width / width);
            int localX = x - _originX;
            int columnBase = localX * _tileHeight;
            int first = int.MaxValue;
            int last = -1;
            for (int y = yStart; y <= yEnd; y++)
            {
                byte index = sprite.At(sx, _spriteRows[y - yStart]);
                if (index == colorKey)
                {
                    continue;
                }

                int localY = localY0 + (y - yStart);
                int cell = columnBase + localY;
                if (key > _guardKey[cell])
                {
                    _dropped++;
                    continue;
                }

                _slotKey[cell] = key;
                _slotColor[cell] = palette[index];
                _guardKey[cell] = key;
                _guardDepth[cell] = z;
                _inserted++;
                EmittedCoverage += 1.0;
                if (localY < first)
                {
                    first = localY;
                }

                last = localY;
            }

            if (last >= 0)
            {
                if (first < _columnFirst[localX])
                {
                    _columnFirst[localX] = first;
                }

                if (last > _columnLast[localX])
                {
                    _columnLast[localX] = last;
                }
            }
        }
    }

    /// <summary>Blends a radial glow along one projected segment.</summary>
    /// <param name="target">The pixels (unused).</param>
    /// <param name="a">Its first endpoint.</param>
    /// <param name="b">Its second.</param>
    /// <param name="radiusA">The glow radius at <paramref name="a"/>, in target pixels.</param>
    /// <param name="radiusB">The glow radius at <paramref name="b"/>.</param>
    /// <param name="color">The glow colour, already in the target's channel order.</param>
    /// <param name="halo">The radial profile and its compositing law.</param>
    /// <returns>How many fragments it emitted.</returns>
    /// <remarks>
    /// The geometry is H17's — the distance to the SEGMENT, an
    /// interpolated radius and an interpolated depth — but the weight becomes a
    /// <see cref="CombineRule.Glow"/> fragment instead of a pixel write, and the depth TEST moves to
    /// the resolve, where it is made against the opaque guard.
    /// </remarks>
    public long BlendHaloSegment(
        in PixelTarget target,
        ScreenVertex a,
        ScreenVertex b,
        double radiusA,
        double radiusB,
        uint color,
        TracerHalo halo)
    {
        _ = target;
        double maxRadius = Math.Max(radiusA, radiusB);
        if (!halo.Enabled || !(maxRadius > 0.0) || !double.IsFinite(maxRadius))
        {
            return 0;
        }

        _halo = halo;
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = (dx * dx) + (dy * dy);

        int xStart = Math.Max(_clipX0, (int)Math.Ceiling(Math.Min(a.X, b.X) - maxRadius - 0.5));
        int xEnd = Math.Min(_clipX1, (int)Math.Floor(Math.Max(a.X, b.X) + maxRadius - 0.5));
        int yStart = Math.Max(_clipY0, (int)Math.Ceiling(Math.Min(a.Y, b.Y) - maxRadius - 0.5));
        int yEnd = Math.Min(_clipY1, (int)Math.Floor(Math.Max(a.Y, b.Y) + maxRadius - 0.5));
        if (xEnd < xStart || yEnd < yStart)
        {
            return 0;
        }

        long touched = 0;
        for (int x = xStart; x <= xEnd; x++)
        {
            double px = x + 0.5;
            for (int y = yStart; y <= yEnd; y++)
            {
                double py = y + 0.5;
                double t = lengthSquared > 0.0
                    ? Math.Clamp((((px - a.X) * dx) + ((py - a.Y) * dy)) / lengthSquared, 0.0, 1.0)
                    : 0.0;
                double cx = a.X + (dx * t);
                double cy = a.Y + (dy * t);
                double distance = Math.Sqrt(((px - cx) * (px - cx)) + ((py - cy) * (py - cy)));
                double radius = radiusA + ((radiusB - radiusA) * t);
                if (!(radius > 0.0))
                {
                    continue;
                }

                double weight = TracerHalo.Profile(distance / radius);
                if (!(weight > 0.0))
                {
                    continue;
                }

                // A glow is NOT an alpha (it is a relative lightening of the destination, §2.4 of
                // the same law), so its weight rides the AREA channel and the retro dither
                // leaves it alone: a speckled tracer halo would be a defect, not a look.
                if (Emit(
                    x, y, weight, 1.0, a.InvZ + ((b.InvZ - a.InvZ) * t), color, CombineRule.Glow,
                    guard: false))
                {
                    touched++;
                }
            }
        }

        return touched;
    }

    /// <summary>
    /// The soft (radial-profile) disc: coverage is the profile at each pixel's centre.
    /// </summary>
    private void SoftDisc(ScreenVertex centre, double radius, Paint paint)
    {
        double inverseSquared = 1.0 / (radius * radius);
        int xStart = Math.Max(_clipX0, (int)Math.Ceiling(centre.X - radius - 0.5));
        int xEnd = Math.Min(_clipX1, (int)Math.Floor(centre.X + radius - 0.5));
        for (int x = xStart; x <= xEnd; x++)
        {
            double dx = x + 0.5 - centre.X;
            double half = (radius * radius) - (dx * dx);
            if (half <= 0.0)
            {
                continue;
            }

            half = Math.Sqrt(half);
            int yStart = Math.Max(_clipY0, (int)Math.Ceiling(centre.Y - half - 0.5));
            int yEnd = Math.Min(_clipY1, (int)Math.Floor(centre.Y + half - 0.5));
            for (int y = yStart; y <= yEnd; y++)
            {
                double dy = y + 0.5 - centre.Y;
                double fade = 1.0 - (((dx * dx) + (dy * dy)) * inverseSquared);
                if (!(fade > 0.0))
                {
                    continue;
                }

                // A soft disc has no area term: its shape IS its alpha, so the whole profile
                // goes through the translucency channel and the retro dither turns it into the
                // classic radial gradient of dots.
                Emit(
                    x, y, 1.0, paint.Coverage * fade, centre.InvZ, paint.Packed, _keyCombine,
                    guard: false);
            }
        }
    }

    /// <summary>Emits one fragment, or drops it behind the pixel's opaque guard.</summary>
    /// <param name="x">Its absolute column.</param>
    /// <param name="y">Its absolute row.</param>
    /// <param name="area">
    /// The fraction of the pixel's AREA the primitive's geometry covers — the analytic anti-aliasing
    /// term, 1 for a kind that has no edge of its own at this pixel (a soft disc, a sprite).
    /// </param>
    /// <param name="alpha">
    /// Its TRANSLUCENCY alpha: the record's own <c>popcount(selector)/8</c> times the instance's
    /// opacity, times the radial profile for a soft disc.  Kept SEPARATE from
    /// <paramref name="area"/> because the retro look quantises this one and only this one.
    /// </param>
    /// <param name="invZ">Its <c>1/z</c> at the pixel's centre.</param>
    /// <param name="color">Its colour, packed in the target's channel order.</param>
    /// <param name="combine">How it combines with the rest of its group.</param>
    /// <param name="guard">Whether a fully-covering fragment may move the opaque guard.</param>
    /// <returns>Whether the fragment was kept.</returns>
    /// <remarks>
    /// <b>Where the retro look happens.</b> The quantiser runs HERE, at insertion, rather than in
    /// <see cref="Resolve"/>, and that is a choice with three consequences worth stating: the
    /// absolute pixel and both terms are in hand at exactly this point; a fragment the pattern
    /// clears is never inserted at all (so it costs no memory and no walk, exactly as the mask used
    /// to cost nothing); and the resolve keeps ONE code path, with no per-fragment branch in the hot
    /// loop.  It is still a RESOLVE-TIME rule in the sense means — a presentation choice applied to
    /// the fragment, not to the geometry — because the area term, the sort key, the opaque guard and
    /// every combine rule are untouched by it.
    /// </remarks>
    private bool Emit(
        int x,
        int y,
        double area,
        double alpha,
        double invZ,
        uint color,
        CombineRule combine,
        bool guard)
    {
        if (_style.Dither)
        {
            alpha = OrderedDither.Quantise(x, y, alpha);
        }

        double coverage = area * alpha;
        if (!(coverage > 0.0))
        {
            return false;
        }

        if (coverage > 1.0)
        {
            coverage = 1.0;
        }

        float z = (float)invZ;
        if (!(z > 0f))
        {
            z = 0f;
        }

        ulong key = _keyUsesDepth
            ? _keyBase | ((ulong)(~BitConverter.SingleToUInt32Bits(z) & 0x7FFFFFFFu) << 30)
            : _keyBase;

        int localX = x - _originX;
        int localY = y - _originY;
        int index = (localX * _tileHeight) + localY;
        if (key > _guardKey[index])
        {
            _dropped++;
            return false;
        }

        if (_poolCount == _pool.Length)
        {
            Array.Resize(ref _pool, _pool.Length * 2);
        }

        int slot = _poolCount++;
        ref Fragment fragment = ref _pool[slot];
        fragment.Key = key;
        fragment.Coverage = (float)coverage;
        fragment.Depth = z;
        fragment.Color = color;
        fragment.Meta = _keyGroup | ((int)combine << GroupBits);
        fragment.Consumed = false;

        if (localY < _columnFirst[localX])
        {
            _columnFirst[localX] = localY;
        }

        if (localY > _columnLast[localX])
        {
            _columnLast[localX] = localY;
        }

        int head = _head[index];
        if (head < 0)
        {
            fragment.Next = -1;
            _head[index] = slot;
        }
        else if (key <= _pool[head].Key)
        {
            fragment.Next = head;
            _head[index] = slot;
        }
        else
        {
            int previous = head;
            while (_pool[previous].Next >= 0 && _pool[_pool[previous].Next].Key < key)
            {
                previous = _pool[previous].Next;
            }

            fragment.Next = _pool[previous].Next;
            _pool[previous].Next = slot;
        }

        _length[index]++;
        _inserted++;
        EmittedCoverage += coverage;

        if (guard && coverage >= Blend.OpaqueCoverage && key < _guardKey[index])
        {
            _guardKey[index] = key;
            _guardDepth[index] = z;
        }

        return true;
    }

    /// <summary>Composites one pixel's fragments and writes it.</summary>
    /// <param name="target">The pixels.</param>
    /// <param name="x">Its column.</param>
    /// <param name="y">Its row.</param>
    /// <param name="head">The first fragment, nearest first.</param>
    /// <param name="guardDepth">The <c>1/z</c> of the nearest fully-covering fragment, or 0.</param>
    /// <param name="background">the frame's terminal function.</param>
    /// <param name="column">Its constants for this column.</param>
    private void Resolve(
        in PixelTarget target,
        int x,
        int y,
        int head,
        float guardDepth,
        ulong slotKey,
        uint slotColor,
        BackgroundField background,
        in BackgroundColumn column)
    {
        double accR = 0.0, accG = 0.0, accB = 0.0, alpha = 0.0;
        int glowCount = 0;

        for (int f = head; f >= 0 && alpha < Blend.OpaqueCoverage;)
        {
            ref Fragment fragment = ref _pool[f];

            // The opaque sprite slot: the list is key-sorted, so the first fragment behind the
            // slot ends the walk — the slot is what a list fragment of the same key would have
            // been, an opaque layer everything farther is hidden by.  (slotKey is MaxValue when
            // there is none, and nothing is behind that.)
            if (fragment.Key > slotKey)
            {
                break;
            }

            if (fragment.Consumed)
            {
                f = fragment.Next;
                continue;
            }

            CombineRule combine = (CombineRule)((fragment.Meta >> GroupBits) & 7);
            if (combine == CombineRule.Glow)
            {
                if (glowCount == _glow.Length)
                {
                    Array.Resize(ref _glow, _glow.Length * 2);
                }

                _glow[glowCount++] = f;
                f = fragment.Next;
                continue;
            }

            int group = fragment.Meta & GroupMask;
            double layerAlpha;
            double r, g, b;

            if (combine == CombineRule.Add && group != 0)
            {
                // A tessellated surface: consecutive fragments of the group are ONE layer whose
                // coverage is the sum and whose colour is the coverage-weighted mean.  The run stops
                // at 1, where a full-coverage fragment has simply won. at an INTERIOR pixel of the
                // masked instance (InteriorMask) the run is also cut where the depth stops being
                // ADJACENT: two faces that meet at an edge leave the pixel short, and a face of the
                // same aeroplane much farther back (the fuselage behind a wing crease) must not be
                // what fills the gap — that is the dotted line — so the adjacent faces are made
                // whole instead.  A single adjacent face over a far one is an internal silhouette
                // and keeps its blend.
                double sum = 0.0, wr = 0.0, wg = 0.0, wb = 0.0;
                bool interior = _mask is { } m && group == m.Group && m.IsInterior(x, y);
                float nearDepth = fragment.Depth;
                int adjacentMembers = 0;
                bool healed = false;
                int k = f;
                while (k >= 0)
                {
                    ref Fragment member = ref _pool[k];
                    if (member.Key > slotKey
                        || member.Consumed
                        || (CombineRule)((member.Meta >> GroupBits) & 7) != CombineRule.Add
                        || (member.Meta & GroupMask) != group)
                    {
                        break;
                    }

                    if (interior)
                    {
                        bool adjacent = Math.Abs(member.Depth - nearDepth) <= AdjacentDepthTolerance * nearDepth;
                        if (!adjacent && sum < 1.0 && adjacentMembers >= 2)
                        {
                            healed = true;
                            break;
                        }

                        if (adjacent)
                        {
                            adjacentMembers++;
                        }
                    }

                    double weight = member.Coverage;
                    sum += weight;
                    wr += weight * ((member.Color >> 16) & 0xFF);
                    wg += weight * ((member.Color >> 8) & 0xFF);
                    wb += weight * (member.Color & 0xFF);
                    k = member.Next;
                    if (sum >= 1.0)
                    {
                        break;
                    }
                }

                layerAlpha = sum >= 1.0 ? 1.0 : sum;

                // THE SEAM RULE'S SECOND HALF (InteriorMask): a pixel strictly inside the masked
                // instance's silhouette is the instance's, whatever two faces left it short — the
                // shortfall is a seam, and the layer is made whole.  The colour is still the
                // coverage-weighted mean of what was there.
                if (layerAlpha < 1.0 && interior && (healed || k < 0 || !SameAddGroup(k, group)))
                {
                    layerAlpha = 1.0;
                    _seamPixels++;
                }

                r = wr / sum;
                g = wg / sum;
                b = wb / sum;
                f = k;
            }
            else if (combine == CombineRule.Max && group != 0)
            {
                // An overlap group: the pixel takes the largest coverage anywhere in the group and
                // the front-most member's colour — one layer, however many parts overlap.
                double largest = fragment.Coverage;
                uint color = fragment.Color;
                for (int k = fragment.Next; k >= 0;)
                {
                    ref Fragment member = ref _pool[k];
                    if (member.Key > slotKey)
                    {
                        break;
                    }

                    if (!member.Consumed
                        && (CombineRule)((member.Meta >> GroupBits) & 7) == CombineRule.Max
                        && (member.Meta & GroupMask) == group)
                    {
                        if (member.Coverage > largest)
                        {
                            largest = member.Coverage;
                        }

                        member.Consumed = true;
                    }

                    k = member.Next;
                }

                layerAlpha = largest;
                r = (color >> 16) & 0xFF;
                g = (color >> 8) & 0xFF;
                b = color & 0xFF;
                f = fragment.Next;
            }
            else
            {
                layerAlpha = fragment.Coverage;
                r = (fragment.Color >> 16) & 0xFF;
                g = (fragment.Color >> 8) & 0xFF;
                b = fragment.Color & 0xFF;
                f = fragment.Next;
            }

            double contribution = (1.0 - alpha) * layerAlpha;
            accR += contribution * r;
            accG += contribution * g;
            accB += contribution * b;
            alpha += contribution;
        }

        // The TERMINAL FUNCTION, evaluated ONLY where the resolve reaches it: a pixel the fragments
        // already cover opaquely never pays for it, and the arithmetic is the same either way
        // (Blend.Over at α ≥ 0.999 rounds its weight to 256/256 and returns the source exactly), so
        // this is a cost saving and not a second rule.
        uint result;
        if (alpha >= Blend.OpaqueCoverage)
        {
            result = Pack(accR / alpha, accG / alpha, accB / alpha);
        }
        else if (slotKey != ulong.MaxValue)
        {
            // The opaque sprite is the terminal — folded in as the fully-covering layer the list
            // path would have walked to, in the SAME double accumulator, so a translucent
            // fragment over a slot pixel packs to exactly what it packs to over a list pixel.
            // (Blend.Over, the background's fixed-point blend, lands one LSB lower per channel;
            // the identity test caught that on its first run.)
            double contribution = 1.0 - alpha;
            accR += contribution * ((slotColor >> 16) & 0xFF);
            accG += contribution * ((slotColor >> 8) & 0xFF);
            accB += contribution * (slotColor & 0xFF);
            alpha += contribution;
            result = Pack(accR / alpha, accG / alpha, accB / alpha);
        }
        else
        {
            result = background.At(in column, y);
            _backgroundPixels++;
            if (alpha > 0.0)
            {
                uint source = Pack(accR / alpha, accG / alpha, accB / alpha);
                result = Blend.Over(result, source, alpha);
            }
        }

        // The glow is a relative lightening of what is already there, so it is applied over the
        // resolved colour, BACK TO FRONT (the walk collected it front to back), and only where it is
        // in front of the nearest opaque surface — the depth test the scan raster made per pixel.
        for (int i = glowCount - 1; i >= 0; i--)
        {
            ref Fragment fragment = ref _pool[_glow[i]];
            if (fragment.Depth <= guardDepth)
            {
                continue;
            }

            result = _halo.Composite(result, fragment.Color, fragment.Coverage);
        }

        target.SetPixel(x, y, result);
    }

    /// <summary>Packs three 0…255 channels back into the target's channel order.</summary>
    private static uint Pack(double r, double g, double b) =>
        ((uint)Math.Clamp((int)Math.Round(r), 0, 255) << 16)
        | ((uint)Math.Clamp((int)Math.Round(g), 0, 255) << 8)
        | (uint)Math.Clamp((int)Math.Round(b), 0, 255);

    /// <summary>
    /// How many segments a disc of a given radius is polygonised with, so the rim never deviates by
    /// more than <see cref="DiscRimTolerance"/> of a pixel.
    /// </summary>
    /// <param name="radius">The screen radius in target pixels.</param>
    /// <remarks>
    /// A regular <c>n</c>-gon on a circle of radius <c>R</c> deviates from it by about
    /// <c>R·θ²/8</c> with <c>θ = 2π/n</c>, so <c>n ≈ 2π·√(R / 8·tol)</c> — 9 segments at one pixel
    /// of radius, 89 at a hundred, 281 at a thousand.  The work is <c>O(n)</c> against the
    /// <c>O(R²)</c> pixels the disc covers, so it is never the cost.
    /// </remarks>
    private static int SegmentsFor(double radius)
    {
        double n = 2.0 * Math.PI * Math.Sqrt(radius / (8.0 * DiscRimTolerance));
        if (!(n > MinDiscSegments))
        {
            return MinDiscSegments;
        }

        return n >= MaxDiscSegments ? MaxDiscSegments : (int)Math.Ceiling(n);
    }

    /// <summary>Fills the shape scratch with an axis-aligned quad.</summary>
    private void Quad(double x0, double y0, double x1, double y1, double invZ)
    {
        _shape[0] = new ScreenVertex(x0, y0, invZ);
        _shape[1] = new ScreenVertex(x1, y0, invZ);
        _shape[2] = new ScreenVertex(x1, y1, invZ);
        _shape[3] = new ScreenVertex(x0, y1, invZ);
    }

    /// <summary>One (primitive, pixel) pair: what the resolve composites.</summary>
    private struct Fragment
    {
        /// <summary>The walk order — see <see cref="BeginPrimitive"/>.</summary>
        public ulong Key;

        /// <summary>The fraction of the pixel it covers, times the primitive's own coverage.</summary>
        public float Coverage;

        /// <summary><c>1/z</c> at the pixel's centre.</summary>
        public float Depth;

        /// <summary>Its colour, packed in the target's channel order.</summary>
        public uint Color;

        /// <summary>The overlap group in the low bits, the combine rule above them.</summary>
        public int Meta;

        /// <summary>The next fragment of this pixel, further back; −1 at the end.</summary>
        public int Next;

        /// <summary>Whether an earlier <see cref="CombineRule.Max"/> layer already took it in.</summary>
        public bool Consumed;
    }
}
