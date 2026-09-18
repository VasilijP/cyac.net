using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Grid;
using CYAC.Port.Core.Sim.Combat.Player;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// The SPATIAL INDEX of a running session: the 5 × 5 cell array (<see cref="WorldGridCellArray"/>)
/// and the two quadtree arenas (<see cref="WorldGridBuilder"/>) built from it, exactly where the
/// original builds them — <c>mission_state_machine @image@0x00C53</c> → <c>session_init_dispatcher_b
/// @image@0x32532</c>, after the containers are parsed and the pool is closed.
/// </summary>
/// <remarks>
/// This is H4 §5 A2 closed. The permissive <c>ITargetAcquisition</c> seam is replaced by C2b's own
/// <see cref="WorldGridQuery"/>, so a projectile's re-acquisition and both sides' fire gates now get
/// the answer the scene actually justifies instead of a constant "clear".
/// </remarks>
public sealed class SessionWorldGrid
{
    /// <summary>The synthetic segment word for the LO arena (<c>g_world_grid_lo_seg [0xF136]</c>).</summary>
    /// <remarks>Any non-zero word: the port's arenas are managed arrays, and the query only
    /// compares the word against 0 to decide whether an arena exists.</remarks>
    public const ushort LoArenaSegment = 0x7DBC;

    /// <summary>The synthetic segment word for the HI arena (<c>[0xF138]</c>).</summary>
    public const ushort HiArenaSegment = 0x7DDB;

    /// <summary><c>g_world_grid_lo_seg [0xF136]</c>.</summary>
    public const int LoArenaSegmentWord = 0xF136;

    /// <summary><c>g_world_grid_hi_seg [0xF138]</c>.</summary>
    public const int HiArenaSegmentWord = 0xF138;

    /// <summary><c>[0xF13A]</c> — the second copy the build latches (<c>image@0x28446</c>).</summary>
    public const int HiArenaSegmentMirror = 0xF13A;

    /// <summary><c>[0xF13C]</c> — the arena length in paragraphs.</summary>
    public const int ArenaParagraphs = 0xF13C;

    /// <summary><c>g_grid_2d_dim_a [0x008E]</c> — the pool descriptor's <c>+0x04</c>.</summary>
    public const int GridDimA = 0x008E;

    /// <summary><c>g_grid_2d_dim_b [0x0090]</c> — the descriptor's <c>+0x06</c>.</summary>
    public const int GridDimB = 0x0090;

    /// <summary><c>g_grid_2d_cell_list_base_off [0x0092]</c> — the descriptor's <c>+0x08</c>.</summary>
    public const int GridCellBaseOffset = 0x0092;

    /// <summary><c>g_grid_2d_cell_list_seg [0x0098]</c> — the descriptor's <c>+0x0E</c>.</summary>
    public const int GridCellSegment = 0x0098;

    /// <summary>The synthetic segment word the cell array is published under.</summary>
    public const ushort CellArraySegment = 0x77A2;

    private SessionWorldGrid(WorldGridCellArray cells, WorldGridIndex index)
    {
        Cells = cells;
        Index = index;
    }

    /// <summary>The cell array the build bucketed.</summary>
    public WorldGridCellArray Cells { get; }

    /// <summary>The two quadtree arenas.</summary>
    public WorldGridIndex Index { get; }

    /// <summary>
    /// Builds the whole index for a loaded scene — the tail of <c>session_init_dispatcher_b</c>.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="arena">The pool.</param>
    /// <param name="staticData">The DGROUP surface class records live in.</param>
    /// <returns>The built index.</returns>
    public static SessionWorldGrid Build(
        CombatRegisters registers, PoolArena arena, ICombatStaticData staticData)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(staticData);

        ushort listHead = WorldGridCellArray.FinalizeArenaList(arena, registers);
        WorldGridCellArray cells = WorldGridCellArray.Build(arena, listHead);
        WorldGridIndex index = WorldGridBuilder.BuildPair(cells, arena, staticData);

        // The four descriptor fields mesh_sort_insert_and_encode writes through [0x008A]
        // (image@0x1C7E4 / 0x1C7F5 / 0x1C866 / 0x1C83E) — a running host's own readers need them.
        registers.SetWord(GridDimA, (ushort)cells.RowCount);
        registers.SetWord(GridDimB, (ushort)cells.ColumnCount);
        registers.SetWord(GridCellBaseOffset, WorldGridCellArray.BaseOffset);
        registers.SetWord(GridCellSegment, CellArraySegment);

        registers.SetWord(LoArenaSegmentWord, LoArenaSegment);
        registers.SetWord(HiArenaSegmentWord, HiArenaSegment);
        registers.SetWord(HiArenaSegmentMirror, HiArenaSegment);
        registers.SetWord(
            ArenaParagraphs,
            (ushort)((Math.Max(index.Lo?.UsedLength ?? 0, index.Hi?.UsedLength ?? 0) + 15) / 16));

        return new SessionWorldGrid(cells, index);
    }
}

/// <summary>
/// <c>world_grid_frustum_query_and_select @image@0x28540</c> for a RUNNING host — C2b's ported
/// query over the session's own grid, replacing H4's permissive constant-zero seam.
/// </summary>
/// <param name="grid">The session's spatial index.</param>
/// <param name="registers">The combat register file (the query's DGROUP staging area).</param>
/// <param name="arena">The pool.</param>
/// <param name="staticData">The DGROUP surface.</param>
/// <remarks>
/// <para>
/// The argument mapping is the call site's own push order (<c>image@0x0291D..0x02955</c>): the
/// local position copy becomes vector 1, the self position (six words read out of the pool at
/// <see cref="GridQueryRequest.SelfPositionRef"/>) becomes vector 2.
/// </para>
/// <para>
/// <b>The one seam left permissive</b> is <see cref="ILockOnGridQuery"/> — the player lock-on's
/// FIFTH gate (<c>image@0x031B4</c>), whose thirteen-word frame is built by the render phase and
/// whose ExcludeRef/half-range the port's own display list does not carry.  It stays 0 = "in view",
/// and <see cref="LockOnQueries"/> counts it.
/// </para>
/// </remarks>
public sealed class SessionWorldGridAcquisition(
    SessionWorldGrid grid,
    CombatRegisters registers,
    PoolArena arena,
    ICombatStaticData staticData) : ITargetAcquisition, ILockOnGridQuery
{
    private readonly SessionWorldGrid _grid = grid ?? throw new ArgumentNullException(nameof(grid));

    private readonly CombatRegisters _registers =
        registers ?? throw new ArgumentNullException(nameof(registers));

    private readonly PoolArena _arena = arena ?? throw new ArgumentNullException(nameof(arena));

    private readonly ICombatStaticData _staticData =
        staticData ?? throw new ArgumentNullException(nameof(staticData));

    /// <summary>Queries answered by the real grid.</summary>
    public long Queries { get; private set; }

    /// <summary>Queries that selected an object.</summary>
    public long Selected { get; private set; }

    /// <summary>Queries the ground plane blocked (the <c>0xFFFF</c> sentinel).</summary>
    public long Blocked { get; private set; }

    /// <summary>Queries that fell into a grid seam the port cannot answer, and returned 0.</summary>
    public long SeamMisses { get; private set; }

    /// <summary>Lock-on frustum gates still answered permissively.</summary>
    public long LockOnQueries { get; private set; }

    /// <summary>The query's own arm census.</summary>
    public WorldGridCounters Counters { get; } = new();

    /// <inheritdoc/>
    public ushort Query(in GridQueryRequest request, out CombatPosition selectionPoint)
    {
        Queries++;
        WorldGridOutputBlock output = new WorldGridOutputBlock();
        WorldGridQueryRequest portRequest = new WorldGridQueryRequest(
            ExcludeRef: request.ExcludeRef,
            Vector1: AxisWords(request.LocalPositionCopy),
            Vector2: PoolWords(_arena, request.SelfPositionRef),
            Vector2SourceOffset: request.SelfPositionRef,
            HalfRangeLo: unchecked((ushort)request.HalfRange),
            HalfRangeHi: unchecked((ushort)(request.HalfRange >> 16)),
            OutputPtr: 1,
            RoutingMask: request.RoutingMask,
            VisMode: request.VisMode,
            RangeGate: request.RangeGate,
            SubGate: request.SubGate,
            LoopGate: request.LoopGate);

        WorldGridQueryContext context = new WorldGridQueryContext
        {
            State = new WorldGridState(_registers),
            Arena = _arena,
            StaticData = _staticData,
            Index = _grid.Index,
            Output = output,
            Counters = Counters,
        };

        ushort answer;
        try
        {
            using (_registers.SuspendWriteTracking())
            {
                answer = WorldGridQuery.Run(context, in portRequest);
            }
        }
        catch (WorldGridSeamException)
        {
            // A scene the port's index cannot describe: answer the permissive way an earlier pass did, and count
            // it, rather than killing the sortie.
            SeamMisses++;
            selectionPoint = default;
            return 0;
        }

        selectionPoint = new CombatPosition(
            (output.Words[1] << 16) | output.Words[0],
            (output.Words[3] << 16) | output.Words[2],
            (output.Words[5] << 16) | output.Words[4]);

        if (answer == WorldGridQuery.BlockedSentinel)
        {
            Blocked++;
        }
        else if (answer != 0)
        {
            Selected++;
        }

        return answer;
    }

    /// <inheritdoc/>
    public ushort Query(ushort node, ushort objectRef)
    {
        LockOnQueries++;
        return 0;
    }

    /// <summary>The six words of one <see cref="CombatPosition"/>, low word first.</summary>
    /// <param name="position">The position.</param>
    private static ushort[] AxisWords(CombatPosition position) =>
    [
        unchecked((ushort)position.X), unchecked((ushort)(position.X >> 16)),
        unchecked((ushort)position.Y), unchecked((ushort)(position.Y >> 16)),
        unchecked((ushort)position.Z), unchecked((ushort)(position.Z >> 16)),
    ];

    /// <summary>The six words at a pool near offset — the far pointer the caller pushes.</summary>
    /// <param name="pool">The arena.</param>
    /// <param name="nearOffset">The offset the caller pushed.</param>
    private static ushort[] PoolWords(PoolArena pool, ushort nearOffset)
    {
        ushort[] words = new ushort[6];
        for (int w = 0; w < 6; w++)
        {
            ushort at = unchecked((ushort)(nearOffset + (2 * w)));
            words[w] = pool.Covers(at, 2) ? pool.Word(at) : (ushort)0;
        }

        return words;
    }
}

/// <summary>
/// Runs several containers through the pool in ORDER — <c>scenario_load_dispatch</c> phase 1 parses
/// the era's theatre (<c>image@0x09335</c>) and only then <c>g_record_filename_buf [0xEF82]</c>
/// (<c>image@0x0935F</c>), so the theatre's scenery is in the pool before the mission's aircraft.
/// </summary>
/// <param name="loaders">The loaders, in parse order.</param>
public sealed class ScenarioLoadSequence(IReadOnlyList<ScenarioObjectLoader> loaders)
    : Combat.Lifecycle.IScenarioLoader
{
    private readonly IReadOnlyList<ScenarioObjectLoader> _loaders =
        loaders ?? throw new ArgumentNullException(nameof(loaders));

    /// <summary>The loaders, in parse order.</summary>
    public IReadOnlyList<ScenarioObjectLoader> Loaders => _loaders;

    /// <inheritdoc/>
    public void LoadScenario()
    {
        foreach (ScenarioObjectLoader loader in _loaders)
        {
            loader.LoadScenario();
        }
    }
}
