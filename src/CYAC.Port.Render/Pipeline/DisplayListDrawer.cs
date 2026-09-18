using CYAC.Port.Render.Raster;

namespace CYAC.Port.Render.Pipeline;

/// <summary>What one <see cref="DisplayListDrawer.Draw"/> cost.</summary>
/// <param name="PixelsWritten">Target pixels the whole list wrote.</param>
/// <param name="Opaque">Primitives drawn in the opaque pass.</param>
/// <param name="Translucent">Primitives drawn in the translucent pass.</param>
/// <param name="HaloPixels">
/// Of <paramref name="PixelsWritten"/>, the part <see cref="PrimitiveKind.Halo"/> primitives blended.
/// </param>
/// <param name="HaloTicks">
/// <see cref="System.Diagnostics.Stopwatch"/> ticks spent on <see cref="PrimitiveKind.Halo"/>
/// primitives, summed over every worker.  A per-KIND figure — a kind is geometry, not a game
/// concept — so the caller can keep reporting what its glows cost without the drawer knowing what
/// glows.
/// </param>
/// <param name="Tiles">how many tiles the frame was cut into.</param>
/// <param name="BinEntries">
/// How many (primitive, tile) pairs the binner produced; divided by <paramref name="Opaque"/> +
/// <paramref name="Translucent"/> it is the mean number of tiles a primitive touches.
/// </param>
/// <param name="Fragments">fragments the frame kept.</param>
/// <param name="FragmentsDropped">fragments the per-pixel opaque depth guard rejected.</param>
/// <param name="MaxFragmentList">the longest per-pixel fragment list in the frame.</param>
/// <param name="BackgroundPixels">
/// Pixels the terminal function was evaluated for (<see cref="FragmentStats.BackgroundPixels"/>).
/// </param>
/// <param name="TrivialAccepts">
/// (primitive, tile) pairs the trivial accept took (<see cref="FragmentStats.TrivialAccepts"/>).
/// </param>
internal readonly record struct DrawListStats(
    long PixelsWritten,
    int Opaque,
    int Translucent,
    long HaloPixels,
    long HaloTicks,
    int Tiles = 0,
    int BinEntries = 0,
    long Fragments = 0,
    long FragmentsDropped = 0,
    int MaxFragmentList = 0,
    long BackgroundPixels = 0,
    long TrivialAccepts = 0,
    long SeamPixels = 0);

/// <summary>
/// The HOW stage: a BINNED, TILED, MULTI-THREADED drawer for a <see cref="DisplayList"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two passes, and that is the whole fix (R1).</b> Opaque primitives go down first in
/// <see cref="DisplayPrimitive.SubmissionIndex"/> order, depth-tested and depth-WRITING — the depth
/// buffer is what makes their order irrelevant.  Everything else then goes down sorted
/// (<see cref="DisplayPrimitive.Layer"/>, <see cref="DisplayPrimitive.SortDepth"/> far→near,
/// submission index), depth-tested against the opaque depth and writing NO depth.  A translucent
/// fragment that stamped its depth is exactly what made a smoke puff's barely-visible rim erase the
/// puff behind it and the 50 % propeller disc erase a B-17.
/// </para>
/// <para>
/// <b>Tiles and threads (R2).</b> The frame is cut into square tiles; every primitive is pushed into
/// each tile its screen box touches, keeping the pass's own order (<see cref="TileBinner"/>); and
/// the tiles are drawn by a pool of persistent workers taking them off one atomic counter
/// (<see cref="TileScheduler"/>).  Because two tiles never contain the same pixel and each tile
/// draws its bin in the frame's order, the picture is a function of the display list alone:
/// <b>bit-identical at any thread count and any tile size</b>, which is what
/// <c>TileInvarianceTests</c> asserts.
/// </para>
/// <para>
/// <b>It knows no game type.</b>  Nothing in this file's signatures or body names an aircraft, a
/// record, an instance, a mesh, an effect or a mission — only primitives, vertices, depths, tiles
/// and pixels.
/// </para>
/// <para>
/// <b>What retired here.</b> <c>ScanRaster</c>, its <c>BeginTranslucentGroup</c> and its blend-stamp
/// buffer (<see cref="CombineRule.Max"/> became a real per-pixel maximum in the fragment resolve),
/// the multiplicative depth biases (the sort key does that job), and <c>--ssaa</c> with its
/// super-sampled buffer and its <c>pixelScale</c> plumbing — all.
/// </para>
/// </remarks>
internal sealed class DisplayListDrawer : TileScheduler.IJob, IDisposable
{
    private readonly TileBinner _binner = new();
    private readonly TileScheduler _scheduler = new();
    private TileWorker[] _workers = [];
    private int[] _order = new int[256];
    private ulong[] _keys = new ulong[256];

    /// <summary>
    /// The opaque primitives' indices, in SUBMISSION order.
    /// </summary>
    /// <remarks>
    /// It was implicit before (the binner walked the whole list and skipped the translucents); the
    /// binner takes both passes' orders explicitly now, which is what let R3b measure what a
    /// front-to-back opaque order would buy — 7,469 more fragments dropped by the per-pixel guard
    /// out of 773,447, i.e. 1 %, because the theatre's ground decals barely overlap.  Recorded in
    /// the report rather than shipped.
    /// </remarks>
    private int[] _opaqueOrder = new int[256];
    private float[] _depth = [];

    // The frame's target, flattened so that a worker thread can rebuild an identical PixelTarget
    // over the pinned pixels — a ref struct cannot be handed to another thread.
    private nint _pixels;
    private int _pixelCount;
    private int _width;
    private int _height;
    private int _columnStride;
    private PixelChannelOrder _channelOrder;
    private DisplayList? _list;

    // Which of the frame's two dispatches the scheduler is running (see Draw).
    private volatile bool _clearing;
    private int _clearChunk;
    private int _clearLength;

    /// <summary>The tile side the last frame used, in target pixels.</summary>
    public int TileSize => _binner.TileSize;

    /// <summary>How many helper threads the pool has started.</summary>
    public int HelperThreads => _scheduler.HelperCount;

    /// <summary>Bytes the helper threads have allocated inside their tile work, ever.</summary>
    public long WorkerAllocatedBytes => _scheduler.WorkerAllocatedBytes;

    /// <summary>Stops the tile pool and joins its helper threads.</summary>
    /// <remarks>R2 §6's owed tidy; see <see cref="TileScheduler.Dispose"/> for the contract.</remarks>
    public void Dispose() => _scheduler.Dispose();

    /// <summary>How many workers the drawer holds per-worker state for.</summary>
    public int WorkerCount => _workers.Length;

    /// <summary>One worker's lazily grown scratch sizes, for the zero-allocation law.</summary>
    /// <param name="worker">Which worker, 0-based.</param>
    public FragmentRaster.ScratchCapacity WorkerScratch(int worker) =>
        _workers[worker].Raster.Scratch;

    /// <summary>
    /// The <c>1/z</c> the last frame left at one pixel, for tests.
    /// </summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <remarks>
    /// The depth buffer stays FRAME-WIDE even though the drawing is per tile: two tiles never
    /// contain the same pixel, so one shared buffer is race-free by construction, each tile clears
    /// only its own region (which parallelises what used to be one frame-wide <c>Array.Clear</c>),
    /// and this read-back therefore means the same thing at every tile size and thread count.
    /// </remarks>
    public float DepthAt(int x, int y) => _depth[(x * _height) + y];

    /// <summary>Draws every primitive of one frame's list.</summary>
    /// <param name="target">The pixels.</param>
    /// <param name="list">The frame's display list.</param>
    /// <param name="tileSize">the tile side in target pixels (<c>--tile</c>).</param>
    /// <param name="threads">how many workers to use (<c>--threads</c>); 1 is serial.</param>
    /// <param name="style">the frame's look (the retro dither and the edge rule).</param>
    /// <param name="background">
    /// The frame's TERMINAL FUNCTION (<see cref="BackgroundField"/>).  The tiles cover every pixel of
    /// the target and each one finishes its pixels against this, so the world path clears nothing and
    /// paints no background pass.
    /// </param>
    /// <returns>What the draw cost.</returns>
    public DrawListStats Draw(
        in PixelTarget target,
        DisplayList list,
        int tileSize,
        int threads,
        in ResolveStyle style,
        BackgroundField background,
        InteriorMask? mask = null)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(background);

        ReadOnlySpan<DisplayPrimitive> primitives = list.Primitives;

        // ---- the frame's two passes, as index lists -------------------------------------------
        int opaque = 0;
        int translucent = 0;
        EnsureOrder(primitives.Length);
        for (int i = 0; i < primitives.Length; i++)
        {
            ref readonly DisplayPrimitive primitive = ref primitives[i];
            if (primitive.IsOpaque)
            {
                _opaqueOrder[opaque++] = i;
                continue;
            }

            _order[translucent] = i;
            _keys[translucent] = SortKey(in primitive);
            translucent++;
        }

        if (translucent > 1)
        {
            Array.Sort(_keys, _order, 0, translucent);
        }

        // ---- bin, then draw the tiles ----------------------------------------------------------
        _binner.Bin(
            list, _opaqueOrder.AsSpan(0, opaque), opaque,
            _order.AsSpan(0, translucent), translucent,
            target.Width, target.Height, tileSize);

        EnsureDepth(target.Width * target.Height);
        int workers = Math.Max(1, threads);
        EnsureWorkers(workers);
        for (int w = 0; w < workers; w++)
        {
            _workers[w].BeginFrame(_depth, target.Width, target.Height, style, background, mask);
        }

        ClearDepth(target.Width * target.Height, workers);

        _list = list;
        _width = target.Width;
        _height = target.Height;
        _columnStride = target.ColumnStride;
        _channelOrder = target.Order;
        _pixelCount = target.Buffer.Length;

        RunTiles(target, workers);
        EqualiseWorkerScratch(workers);

        // ---- sum the per-worker counters (integers: the order of summation cannot matter) ------
        long pixels = 0;
        long haloPixels = 0;
        long haloTicks = 0;
        long fragments = 0;
        long dropped = 0;
        long backgroundPixels = 0;
        long trivialAccepts = 0;
        long seamPixels = 0;
        int longest = 0;
        for (int w = 0; w < workers; w++)
        {
            pixels += _workers[w].Raster.PixelsWritten;
            haloPixels += _workers[w].HaloPixels;
            haloTicks += _workers[w].HaloTicks;
            FragmentStats counts = _workers[w].Raster.Fragments;
            fragments += counts.Inserted;
            dropped += counts.Dropped;
            backgroundPixels += counts.BackgroundPixels;
            trivialAccepts += counts.TrivialAccepts;
            seamPixels += counts.SeamPixels;
            longest = Math.Max(longest, counts.MaxListLength);
        }

        _list = null;
        return new DrawListStats(
            pixels,
            opaque,
            translucent,
            haloPixels,
            haloTicks,
            _binner.TileCount,
            _binner.OpaqueEntries + _binner.TranslucentEntries,
            fragments,
            dropped,
            longest,
            backgroundPixels,
            trivialAccepts,
            seamPixels);
    }

    /// <summary>
    /// Levels every worker's lazily grown scratch up to the frame's maximum.
    /// </summary>
    /// <param name="workers">How many workers this frame used.</param>
    /// <remarks>
    /// <para>
    /// <b>What it fixes.</b>  A worker's fragment pool, per-pixel head/guard arrays and glow list all
    /// grow on demand from the tiles THAT worker has happened to draw, and the scheduler is dynamic —
    /// whichever worker is free takes the next tile.  A worker that had drawn only cheap tiles for
    /// twenty frames therefore allocated the first time the schedule handed it the heavy one, on any
    /// later frame, and "steady-state frames allocate nothing" held only
    /// for the workers that had already been unlucky.  That is exactly the flake M1 §6.4 reported:
    /// <c>TileInvarianceTests.SteadyStateFramesAllocateNothingOnAnyThread</c> failing 2 runs in 9
    /// with 524,312 B on the calling thread — one fragment-pool doubling, 16,384 × 32 B + header.
    /// </para>
    /// <para>
    /// <b>Why here.</b>  Between the tile dispatch and the next frame no worker is running, so the
    /// arrays can be replaced without a lock; the buffers carry nothing across a tile, so growing one
    /// is a fresh array rather than a copy; and it is <c>workers</c> pointer comparisons, which do not
    /// show in a frame time.  It converges after the FIRST frame of an unchanged scene: whatever tile
    /// is heaviest is met by some worker every frame, and after this call all of them are sized for
    /// it.  It cannot reach the picture — capacities are not read by any drawing code.
    /// </para>
    /// </remarks>
    private void EqualiseWorkerScratch(int workers)
    {
        FragmentRaster.ScratchCapacity capacity = _workers[0].Raster.Scratch;
        for (int w = 1; w < workers; w++)
        {
            capacity = FragmentRaster.ScratchCapacity.Max(capacity, _workers[w].Raster.Scratch);
        }

        for (int w = 0; w < workers; w++)
        {
            _workers[w].Raster.EnsureScratch(capacity);
        }
    }

    /// <summary>
    /// Pins the target's pixels for the parallel section and runs the tile pool over them.
    /// </summary>
    /// <remarks>
    /// A <see cref="PixelTarget"/> is a <c>ref struct</c> and cannot cross a thread boundary, so the
    /// drawer pins the span here — pinning it for the whole section, which is exactly as long as any
    /// worker can be inside it — and each worker rebuilds an identical target over the same memory
    /// in <see cref="Execute"/>.  This is the only <c>unsafe</c> code in the assembly and it takes
    /// no address that outlives the <c>fixed</c> block.
    /// </remarks>
    private unsafe void RunTiles(in PixelTarget target, int workers)
    {
        fixed (uint* pixels = target.Buffer)
        {
            _pixels = (nint)pixels;
            try
            {
                _scheduler.Run(this, _binner.TileCount, workers);
            }
            finally
            {
                _pixels = 0;
            }
        }
    }

    /// <summary>
    /// Clears the frame's depth buffer in ONE parallel pre-pass over big contiguous chunks.
    /// </summary>
    /// <param name="length">How many depth elements the frame uses.</param>
    /// <param name="workers">How many workers to spread the clear over.</param>
    /// <remarks>
    /// <para>
    /// The depth buffer is column-major, so a TILE's share of it is <c>tileWidth</c> separate runs of
    /// <c>tileHeight</c> floats.  Clearing it inside <c>BeginTile</c> therefore turned one frame-wide
    /// memset into tens of thousands of half-kilobyte ones — measured +1.8 ms on a serial
    /// 3840×2160 frame.  Clearing it here instead is both contiguous and threaded: the
    /// scheduler's barrier between the two dispatches is what makes it safe, since every element is
    /// zero before any tile reads it.
    /// </para>
    /// <para>
    /// Two chunks per worker, so a straggling chunk cannot hold the frame; a chunk is at least 64 KB
    /// of floats, below which the dispatch is worth more than the memset.
    /// </para>
    /// </remarks>
    private void ClearDepth(int length, int workers)
    {
        const int MinChunk = 16 * 1024;
        int chunks = Math.Clamp(length / MinChunk, 1, workers * 2);
        _clearChunk = ((length + chunks - 1) / chunks + 63) & ~63;
        _clearLength = length;
        _clearing = true;
        try
        {
            _scheduler.Run(this, chunks, workers);
        }
        finally
        {
            _clearing = false;
        }
    }

    /// <summary>Draws one tile — or clears one depth chunk — on one worker.</summary>
    /// <param name="worker">Which worker's private state to use.</param>
    /// <param name="index">The tile index, or the depth chunk in the clear pre-pass.</param>
    public unsafe void Execute(int worker, int index)
    {
        if (_clearing)
        {
            int start = index * _clearChunk;
            if (start < _clearLength)
            {
                Array.Clear(_depth, start, Math.Min(_clearChunk, _clearLength - start));
            }

            return;
        }

        PixelTarget target = new PixelTarget(
            new Span<uint>((void*)_pixels, _pixelCount), _width, _height, _columnStride, _channelOrder);
        _workers[worker].DrawTile(
            target,
            _list!,
            _binner.RectOf(index, _width, _height),
            _binner.OpaqueBin(index),
            _binner.TranslucentBin(index));
    }

    /// <summary>
    /// The TOTAL order the translucent pass draws in: layer, then far to near, then submission.
    /// </summary>
    /// <param name="primitive">The primitive.</param>
    /// <remarks>
    /// Packed into one <c>ulong</c> so the sort is a primitive-key sort — no comparer object, no
    /// allocation, and a strict total order, which is what makes the output independent of the
    /// scheduling the tiled drawer imposes.  A non-negative <c>float</c>'s bit pattern orders
    /// exactly as its value does, so complementing it turns "largest Z first" into an ascending key.
    /// </remarks>
    private static ulong SortKey(in DisplayPrimitive primitive)
    {
        ulong layer = (ulong)((int)primitive.Layer & 0xF) << 60;
        double z = double.IsFinite(primitive.SortDepth) ? primitive.SortDepth : 0.0;
        uint bits = BitConverter.SingleToUInt32Bits((float)Math.Clamp(z, 0.0, float.MaxValue));
        ulong depth = (ulong)~bits << 28;
        ulong submission = (uint)primitive.SubmissionIndex & 0x0FFFFFFFUL;
        return layer | depth | submission;
    }

    private void EnsureOrder(int needed)
    {
        if (_order.Length >= needed)
        {
            return;
        }

        int size = Math.Max(needed, _order.Length * 2);
        _order = new int[size];
        _keys = new ulong[size];
        _opaqueOrder = new int[size];
    }

    private void EnsureDepth(int needed)
    {
        if (_depth.Length < needed)
        {
            _depth = new float[needed];
        }
    }

    private void EnsureWorkers(int needed)
    {
        if (_workers.Length >= needed)
        {
            return;
        }

        TileWorker[] grown = new TileWorker[needed];
        Array.Copy(_workers, grown, _workers.Length);
        for (int i = _workers.Length; i < needed; i++)
        {
            grown[i] = new TileWorker();
        }

        _workers = grown;
    }
}
