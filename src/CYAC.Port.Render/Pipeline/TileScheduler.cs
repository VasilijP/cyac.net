namespace CYAC.Port.Render.Pipeline;

/// <summary>
/// The tile work pool: N persistent workers pulling tiles off one atomic counter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dynamic, because tiles are not equal.</b> A sky tile is nearly free and a horizon tile is
/// heavy, so a static split would leave most threads idle behind one straggler.  Every worker — the
/// calling thread included, as worker 0 — takes the next tile with one
/// <see cref="Interlocked.Increment(ref int)"/> until the frame's tiles run out.
/// </para>
/// <para>
/// <b>Which worker takes which tile can never reach the picture.</b> Two tiles never contain the
/// same pixel, each tile draws its own bin in the bin's own order, and the per-primitive pixel
/// counts are summed with integer adds — so the output is a function of the list, the tile size and
/// nothing else.  The pool exists to make it faster, not to make a decision.
/// </para>
/// <para>
/// <b>Why not <c>Parallel.For</c>.</b>  It allocates on every invocation (a range partitioner, a
/// closure, tasks), and a steady-state frame must allocate nothing.  These
/// threads are created once, grow-only, and park on an event between frames; a frame costs one
/// <c>Set</c> per helper and one <c>Wait</c>.
/// </para>
/// <para>
/// The helpers are BACKGROUND threads, so they never hold the process open, and they are created
/// lazily — a renderer that is never asked for more than one thread never starts one.
/// </para>
/// </remarks>
internal sealed class TileScheduler : IDisposable
{
    /// <summary>What a scheduler runs on each tile.</summary>
    /// <remarks>
    /// An interface rather than a delegate so that a frame's dispatch allocates nothing: the drawer
    /// implements it once and hands <c>this</c> to <see cref="Run"/>.
    /// </remarks>
    public interface IJob
    {
        /// <summary>Draws one tile.</summary>
        /// <param name="worker">Which worker's private state to use, 0-based.</param>
        /// <param name="tile">The tile index.</param>
        void Execute(int worker, int tile);
    }

    private Thread[] _helpers = [];
    private ManualResetEventSlim[] _wake = [];
    private readonly ManualResetEventSlim _idle = new(true, spinCount: 200);
    private IJob? _job;
    private int _tiles;
    private int _next;
    private int _outstanding;
    private Exception? _failure;
    private long _workerBytes;
    private volatile bool _shutdown;

    /// <summary>How many helper threads exist (the caller is always an extra worker).</summary>
    public int HelperCount => _helpers.Length;

    /// <summary>
    /// How many bytes the HELPER threads have allocated inside their tile work, ever.
    /// </summary>
    /// <remarks>
    /// The steady-state-allocates-nothing law has to be measurable on the
    /// threads that do the work, and the process-wide counter is useless for it: a parallel test
    /// host, the GC and the runtime all allocate on other threads throughout.  One
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> pair per helper per FRAME (not per tile)
    /// is exact and costs nothing measurable.
    /// </remarks>
    public long WorkerAllocatedBytes => Interlocked.Read(ref _workerBytes);

    /// <summary>
    /// Runs <paramref name="job"/> over <paramref name="tiles"/> tiles on
    /// <paramref name="workers"/> threads, and returns when every tile is done.
    /// </summary>
    /// <param name="job">The per-tile work.</param>
    /// <param name="tiles">How many tiles the frame has.</param>
    /// <param name="workers">
    /// How many workers to use, at least 1.  <b>1 runs the identical code on the calling thread</b>
    /// — the serial path is the parallel path with nobody to help.
    /// </param>
    public void Run(IJob job, int tiles, int workers)
    {
        ArgumentNullException.ThrowIfNull(job);
        ObjectDisposedException.ThrowIf(_shutdown, this);
        if (tiles <= 0)
        {
            return;
        }

        int helpers = Math.Min(Math.Max(1, workers) - 1, tiles - 1);
        if (helpers <= 0)
        {
            for (int t = 0; t < tiles; t++)
            {
                job.Execute(0, t);
            }

            return;
        }

        Grow(helpers);
        _job = job;
        _tiles = tiles;
        _next = 0;
        _outstanding = helpers;
        _idle.Reset();

        for (int i = 0; i < helpers; i++)
        {
            _wake[i].Set();
        }

        try
        {
            Drain(0);
        }
        finally
        {
            _idle.Wait();
            _job = null;
        }

        if (_failure is { } failure)
        {
            _failure = null;
            throw new InvalidOperationException("a tile worker faulted", failure);
        }
    }

    private void Drain(int worker)
    {
        IJob job = _job!;
        int tiles = _tiles;
        int tile;
        while ((tile = Interlocked.Increment(ref _next) - 1) < tiles)
        {
            job.Execute(worker, tile);
        }
    }

    private void Grow(int helpers)
    {
        if (_helpers.Length >= helpers)
        {
            return;
        }

        Thread[] threads = new Thread[helpers];
        ManualResetEventSlim[] wake = new ManualResetEventSlim[helpers];
        Array.Copy(_helpers, threads, _helpers.Length);
        Array.Copy(_wake, wake, _wake.Length);

        // The CALLER's barrier is waited on inside every frame, so its lazily created
        // internals must exist before the first one.  It is primed on the first growth only.
        if (_helpers.Length == 0)
        {
            _idle.Reset();
            Prime(_idle);
            _idle.Set();
        }

        for (int i = _helpers.Length; i < helpers; i++)
        {
            int index = i;
            wake[i] = new ManualResetEventSlim(false, spinCount: 200);

            // Primed HERE, where the event is unset and unreachable: no thread exists that could
            // set it, so the wait must take the blocking path.
            Prime(wake[i]);
            threads[i] = new Thread(() => HelperMain(index))
            {
                IsBackground = true,
                Name = $"cyac-tile-{index}",
            };
        }

        // Publish both arrays before any new thread can look at them.  The events already in
        // _wake are carried over by reference, so a helper that is parked on its old event is woken
        // by the new array's entry — it is the same object.
        int existing = _helpers.Length;
        _wake = wake;
        _helpers = threads;
        for (int i = existing; i < helpers; i++)
        {
            threads[i].Start();
        }
    }

    /// <summary>
    /// Forces one gate's lazily created internals into existence, off the frame path.
    /// </summary>
    /// <param name="gate">An UNSET event no other thread can reach yet.</param>
    /// <remarks>
    /// <para>
    /// <b>Why.</b>  <see cref="ManualResetEventSlim"/> creates the object it monitors on the first
    /// wait that actually BLOCKS — its spin phase allocates nothing — so the very first frame whose
    /// spin loses the race allocates 24 bytes on whichever thread lost it.  That is a one-off, but
    /// it is a one-off that lands INSIDE a frame, at a moment the schedule picks, which is precisely
    /// what makes "a steady-state frame allocates nothing" flaky rather than false (the other, larger
    /// half of the same flake was the per-worker scratch — see
    /// <c>DisplayListDrawer.EqualiseWorkerScratch</c>).  Doing it here moves the allocation into the
    /// pool's construction, where it belongs.
    /// </para>
    /// <para>
    /// <b>Why it is deterministic.</b>  The gate is unset and nothing else holds a reference to it
    /// yet, so the wait cannot be satisfied and cannot return before its timeout: it must reach the
    /// blocking path.  The return value is therefore always <c>false</c>, and the cost is one
    /// millisecond per gate, paid once when the pool grows.
    /// </para>
    /// </remarks>
    private static void Prime(ManualResetEventSlim gate)
    {
        _ = gate.Wait(millisecondsTimeout: 1);
    }

    private void HelperMain(int index)
    {
        ManualResetEventSlim wake = _wake[index];
        while (true)
        {
            wake.Wait();
            wake.Reset();

            // The exit door.  Dispose sets the flag and then wakes every helper, and it is only
            // ever called when no frame is running, so a helper that sees the flag has no
            // outstanding count to decrement and nothing to drain — it just leaves and is joined.
            if (_shutdown)
            {
                return;
            }

            // A helper is only ever woken between Run's Reset and its Wait, and Run does not return
            // until every helper has decremented, so a wake-up can never be lost or doubled.
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                Drain(index + 1);
            }
            catch (Exception error)
            {
                // Never leave the caller waiting on a worker that died: remember the fault, let the
                // countdown complete, and let Run rethrow it on the calling thread.
                Interlocked.CompareExchange(ref _failure, error, null);
            }

            Interlocked.Add(
                ref _workerBytes, GC.GetAllocatedBytesForCurrentThread() - allocated);

            if (Interlocked.Decrement(ref _outstanding) == 0)
            {
                _idle.Set();
            }
        }
    }

    /// <summary>
    /// Stops the pool: every helper is woken, leaves its loop and is JOINED.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one thing left to tidy: the helpers are BACKGROUND
    /// threads, so they never hold the process open, but a renderer that is dropped left its helpers
    /// parked forever.  That is harmless in the host (one renderer for the life of the process) and
    /// in the tests, and it is still hygiene — a test class that builds a renderer per case, or a
    /// future tool that opens several, accumulates parked threads and their stacks.
    /// </para>
    /// <para>
    /// It must be called with no frame in flight, which is what <c>IDisposable</c> on the renderer
    /// means: <see cref="Run"/> does not return until every helper has finished, so "disposed" and
    /// "drawing" cannot overlap unless the owner uses one renderer from two threads at once, which
    /// nothing does and which the class never supported.  Disposing twice, or disposing a pool that
    /// never grew, is a no-op; drawing after disposing throws rather than hanging.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        Thread[] helpers = _helpers;
        ManualResetEventSlim[] wake = _wake;
        for (int i = 0; i < helpers.Length; i++)
        {
            wake[i].Set();
        }

        for (int i = 0; i < helpers.Length; i++)
        {
            helpers[i].Join();
            wake[i].Dispose();
        }

        _helpers = [];
        _wake = [];
        _idle.Dispose();
    }
}
