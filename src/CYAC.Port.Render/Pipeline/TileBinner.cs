namespace CYAC.Port.Render.Pipeline;

/// <summary>One tile of the target, in absolute target pixels.</summary>
/// <param name="X">Its left column.</param>
/// <param name="Y">Its top row.</param>
/// <param name="Width">Its width; an edge tile is partial.</param>
/// <param name="Height">Its height.</param>
internal readonly record struct TileRect(int X, int Y, int Width, int Height);

/// <summary>
/// The target cut into square tiles, and the display list BINNED into them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Binning by the bounding box is exact per-tile culling.</b> Because the WHAT stage projects
/// everything once and every primitive already carries its screen bounds
/// (<see cref="DisplayPrimitive.MinX"/>…), pushing a primitive's index into every tile its box
/// touches leaves each tile with exactly the primitives that can reach it ("the pixel pyramid").  A
/// tile that gets a primitive which turns out to paint nothing inside it costs one wasted setup and
/// no pixels, so the bin may be conservative but never short.
/// </para>
/// <para>
/// <b>Order is preserved by construction.</b> Both passes walk the source order — submission order
/// for the opaque bin, the drawer's sorted order for the translucent one — so each tile inherits
/// that order without re-sorting.  That is what makes a depth TIE resolve the same way in every
/// tile and at every tile size.
/// </para>
/// <para>
/// Every buffer is grow-only; a frame no larger than the largest so far allocates nothing.
/// </para>
/// </remarks>
internal sealed class TileBinner
{
    /// <summary>The smallest tile side the drawer will accept.</summary>
    public const int MinTileSize = 4;

    /// <summary>The largest.</summary>
    public const int MaxTileSize = 8192;

    /// <summary>The default tile side, in target pixels ("128×128 to start").</summary>
    public const int DefaultTileSize = 128;

    private int[] _counts = new int[64];
    private int[] _cursor = new int[64];
    private int[] _opaqueStart = new int[65];
    private int[] _translucentStart = new int[65];
    private int[] _opaqueItems = new int[1024];
    private int[] _translucentItems = new int[1024];

    /// <summary>The tile side in target pixels.</summary>
    public int TileSize { get; private set; } = DefaultTileSize;

    /// <summary>How many tile columns the target is cut into.</summary>
    public int TilesX { get; private set; }

    /// <summary>How many tile rows.</summary>
    public int TilesY { get; private set; }

    /// <summary>How many tiles the frame has.</summary>
    public int TileCount => TilesX * TilesY;

    /// <summary>How many (primitive, tile) pairs the opaque bin holds.</summary>
    public int OpaqueEntries => TileCount == 0 ? 0 : _opaqueStart[TileCount];

    /// <summary>How many the translucent bin holds.</summary>
    public int TranslucentEntries => TileCount == 0 ? 0 : _translucentStart[TileCount];

    /// <summary>One tile's rectangle in absolute target pixels.</summary>
    /// <param name="tile">The tile index, row-major.</param>
    /// <param name="targetWidth">The target's width.</param>
    /// <param name="targetHeight">Its height.</param>
    public TileRect RectOf(int tile, int targetWidth, int targetHeight)
    {
        int tx = tile % TilesX;
        int ty = tile / TilesX;
        int x = tx * TileSize;
        int y = ty * TileSize;
        return new TileRect(
            x, y, Math.Min(TileSize, targetWidth - x), Math.Min(TileSize, targetHeight - y));
    }

    /// <summary>The opaque primitives that reach one tile, in submission order.</summary>
    /// <param name="tile">The tile index.</param>
    public ReadOnlySpan<int> OpaqueBin(int tile) =>
        _opaqueItems.AsSpan(_opaqueStart[tile], _opaqueStart[tile + 1] - _opaqueStart[tile]);

    /// <summary>The translucent primitives that reach one tile, in the drawer's sort order.</summary>
    /// <param name="tile">The tile index.</param>
    public ReadOnlySpan<int> TranslucentBin(int tile) =>
        _translucentItems.AsSpan(
            _translucentStart[tile], _translucentStart[tile + 1] - _translucentStart[tile]);

    /// <summary>Cuts the target into tiles and bins one frame's list into them.</summary>
    /// <param name="list">The frame's display list.</param>
    /// <param name="opaqueOrder">
    /// The opaque primitives' indices in the drawer's own COARSE FRONT-TO-BACK order (see
    /// <c>DisplayListDrawer.OpaqueSortKey</c>).  It still may — the picture is a function of the
    /// sort key, not of the draw order — but a bin ordered front to back lets the per-pixel opaque
    /// guard drop what it hides instead of inserting it: measured 10.2 → 4.1 ms on a serial 1080p
    /// test flight, §4.
    /// </param>
    /// <param name="opaqueCount">How many of them there are.</param>
    /// <param name="translucentOrder">
    /// The translucent primitives' indices in the drawer's total sort order.
    /// </param>
    /// <param name="translucentCount">How many of them there are.</param>
    /// <param name="targetWidth">The target's width in pixels.</param>
    /// <param name="targetHeight">Its height.</param>
    /// <param name="tileSize">The tile side in target pixels.</param>
    public void Bin(
        DisplayList list,
        ReadOnlySpan<int> opaqueOrder,
        int opaqueCount,
        ReadOnlySpan<int> translucentOrder,
        int translucentCount,
        int targetWidth,
        int targetHeight,
        int tileSize)
    {
        ArgumentNullException.ThrowIfNull(list);

        TileSize = Math.Clamp(tileSize, MinTileSize, MaxTileSize);
        TilesX = ((targetWidth - 1) / TileSize) + 1;
        TilesY = ((targetHeight - 1) / TileSize) + 1;
        int tiles = TilesX * TilesY;
        EnsureTiles(tiles);

        ReadOnlySpan<DisplayPrimitive> primitives = list.Primitives;
        BinPass(
            primitives, opaqueOrder, opaqueCount, targetWidth, targetHeight,
            tiles, opaque: true, ref _opaqueStart, ref _opaqueItems);
        BinPass(
            primitives, translucentOrder, translucentCount, targetWidth, targetHeight,
            tiles, opaque: false, ref _translucentStart, ref _translucentItems);
    }

    /// <summary>
    /// How far outside its own screen bounds a primitive may paint, in target pixels.
    /// </summary>
    /// <remarks>
    /// A polygon's fragments never reach outside its own vertex box; a POINT, however, is a one-pixel
    /// square CENTRED on its sample and a thin LINE is a one-pixel-wide quad, so either can reach
    /// half a pixel past the bounds the vertices give.  One flat margin for every kind is a couple of
    /// extra bin entries per frame and removes the whole class of "the tile boundary ate a dot" bug —
    /// and the tile-size invariance test would catch it if it were ever too small. it was <c>(0.5 ×
    /// pixelScale) + 1</c> while <c>--ssaa</c> existed; a target pixel is a host pixel now.
    /// </remarks>
    private const double Margin = 1.5;

    private void BinPass(
        ReadOnlySpan<DisplayPrimitive> primitives,
        ReadOnlySpan<int> order,
        int count,
        int targetWidth,
        int targetHeight,
        int tiles,
        bool opaque,
        ref int[] start,
        ref int[] items)
    {
        Array.Clear(_counts, 0, tiles);
        const double margin = Margin;

        // Pass 1 — how many entries each tile gets.
        int total = 0;
        int n = count;
        for (int k = 0; k < n; k++)
        {
            int i = order[k];
            ref readonly DisplayPrimitive primitive = ref primitives[i];
            if (opaque != primitive.IsOpaque)
            {
                continue;
            }

            if (!Range(in primitive, margin, targetWidth, targetHeight,
                    out int tx0, out int tx1, out int ty0, out int ty1))
            {
                continue;
            }

            for (int ty = ty0; ty <= ty1; ty++)
            {
                int row = ty * TilesX;
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    _counts[row + tx]++;
                    total++;
                }
            }
        }

        // Prefix sum → each tile's slice of the item array.
        start[0] = 0;
        for (int t = 0; t < tiles; t++)
        {
            start[t + 1] = start[t] + _counts[t];
            _cursor[t] = start[t];
        }

        if (items.Length < total)
        {
            items = new int[Math.Max(total, items.Length * 2)];
        }

        // Pass 2 — fill, in the SAME order, so every tile inherits it.
        for (int k = 0; k < n; k++)
        {
            int i = order[k];
            ref readonly DisplayPrimitive primitive = ref primitives[i];
            if (opaque != primitive.IsOpaque)
            {
                continue;
            }

            if (!Range(in primitive, margin, targetWidth, targetHeight,
                    out int tx0, out int tx1, out int ty0, out int ty1))
            {
                continue;
            }

            for (int ty = ty0; ty <= ty1; ty++)
            {
                int row = ty * TilesX;
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    items[_cursor[row + tx]++] = i;
                }
            }
        }
    }

    /// <summary>The inclusive tile range one primitive's screen box covers, or false if it is off screen.</summary>
    private bool Range(
        in DisplayPrimitive primitive,
        double margin,
        int targetWidth,
        int targetHeight,
        out int tx0,
        out int tx1,
        out int ty0,
        out int ty1)
    {
        tx0 = tx1 = ty0 = ty1 = 0;
        double minX = primitive.MinX - margin;
        double maxX = primitive.MaxX + margin;
        double minY = primitive.MinY - margin;
        double maxY = primitive.MaxY + margin;
        if (!(maxX >= 0.0) || !(maxY >= 0.0) || !(minX <= targetWidth) || !(minY <= targetHeight))
        {
            return false;   // wholly off screen, or a NaN box
        }

        int x0 = (int)Math.Floor(Math.Max(0.0, minX));
        int x1 = (int)Math.Floor(Math.Min(targetWidth - 1.0, maxX));
        int y0 = (int)Math.Floor(Math.Max(0.0, minY));
        int y1 = (int)Math.Floor(Math.Min(targetHeight - 1.0, maxY));
        tx0 = x0 / TileSize;
        tx1 = x1 / TileSize;
        ty0 = y0 / TileSize;
        ty1 = y1 / TileSize;
        return true;
    }

    private void EnsureTiles(int tiles)
    {
        if (_counts.Length < tiles)
        {
            int size = Math.Max(tiles, _counts.Length * 2);
            _counts = new int[size];
            _cursor = new int[size];
            _opaqueStart = new int[size + 1];
            _translucentStart = new int[size + 1];
        }
    }
}
