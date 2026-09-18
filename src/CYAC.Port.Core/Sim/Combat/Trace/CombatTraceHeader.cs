using System.Globalization;
using System.Text.Json;

namespace CYAC.Port.Core.Sim.Combat.Trace;

/// <summary>One stage trap the combat recorder samples.</summary>
/// <param name="Id">The stage id, 0..10 — CS0 <c>frame_pre</c> … CS10 <c>frame_end</c>.</param>
/// <param name="Name">The stage's name, e.g. <c>"before_engagement"</c>.</param>
/// <param name="ImageOffset">The <c>image@</c> offset of the trapped instruction.</param>
/// <param name="Enabled">False when the run disabled the stage (<c>--combat-trace-stages</c>).</param>
/// <param name="Meaning">The header's prose description, for a test's output.</param>
public readonly record struct CombatTraceStage(
    int Id, string Name, int ImageOffset, bool Enabled, string? Meaning);

/// <summary>One probed function.</summary>
/// <param name="Id">The probe id, 0..21.</param>
/// <param name="Name">The function's name.</param>
/// <param name="EntryImageOffset">Its first instruction.</param>
/// <param name="ExitImageOffsets">Every <c>ret</c>/<c>retf</c> it can leave through.</param>
/// <param name="IsFar">True for a <c>retf</c> function — which is how the arg latch offset is chosen.</param>
/// <param name="Enabled">False when the run disabled the probe (<c>--combat-trace-probes</c>).</param>
/// <param name="Subject">The header's note on what subject 1 is for this probe.</param>
public readonly record struct CombatTraceProbe(
    int Id,
    string Name,
    int EntryImageOffset,
    IReadOnlyList<int> ExitImageOffsets,
    bool IsFar,
    bool Enabled,
    string? Subject);

/// <summary>One DGROUP window the recorder copies into every STAGE record.</summary>
/// <param name="Name"></param>
/// <param name="DgroupOffset">Its DGROUP offset — this project's <c>[0x….]</c> notation.</param>
/// <param name="Length">How many bytes of it a record carries.</param>
/// <param name="RecordOffset">Where the window starts inside a record — AUTHORITATIVE.</param>
public readonly record struct CombatTraceGlobal(
    string Name, int DgroupOffset, int Length, int RecordOffset);

/// <summary>
/// C0f (trace format v1.5) — one FAR-segment region the <c>.tables.bin</c> sidecar carries after the DGROUP
/// static tables.
/// </summary>
/// <param name="Name">The project's name for the region, e.g. <c>g_world_grid_arena_lo</c>.</param>
/// <param name="SidecarOffset">Where its 8-byte descriptor starts in the sidecar — AUTHORITATIVE.</param>
/// <param name="DescriptorLength">Bytes of the descriptor (8: seg, off, usedLen, aux).</param>
/// <param name="PayloadLength">Fixed payload bytes that follow, zero-filled past the used length.</param>
/// <param name="Source">Where the recorder read the bytes, in this project's <c>[0x….]</c> notation.</param>
public readonly record struct CombatTraceFarTable(
    string Name, int SidecarOffset, int DescriptorLength, int PayloadLength, string? Source)
{
    /// <summary>Where the payload starts in the sidecar.</summary>
    public int PayloadOffset => SidecarOffset + DescriptorLength;
}

/// <summary>
/// The one-line JSON header of a <c>cyac-combat-trace</c> file (format v1, minor 0 or 1).
/// </summary>
/// <remarks>
/// <para>
/// Extended by C0b — the producer now stamps a <c>minor</c> number.  §12: <c>version</c> is the
/// RECORD-LAYOUT major and stays 1; <c>minor</c> counts purely additive revisions, so this reader
/// accepts any minor and exposes it as <see cref="MinorVersion"/>.  The only semantic gate is
/// <see cref="ExitSubjectsAreLatched"/>.
/// </para>
/// <para>
/// Written from alone — the port never references the emulator that writes these files.  §9's rules are implemented literally: reject a wrong <c>format</c>, reject a
/// version this reader does not understand, IGNORE unknown keys, and take every offset from
/// <c>fields</c> / <c>globals[].recOff</c> / <c>stageRecordLen</c> / <c>probeRecordLen</c> rather
/// than from a constant here.
/// </para>
/// <para>
/// A trace is only meaningful for the leg that produced it, and a VERIFICATION trace must be
/// <c>--no-lift</c>: eight probe entry addresses are also trap addresses, and with traps armed
/// stages CS0, CS1 and CS4 vanish entirely.  <see cref="Lifts"/> is what a test checks.
/// </para>
/// </remarks>
public sealed class CombatTraceHeader
{
    /// <summary>The only <c>format</c> value a reader accepts.</summary>
    public const string FormatName = "cyac-combat-trace";

    /// <summary>The only wire MAJOR version this reader knows.</summary>
    public const int SupportedVersion = 1;

    /// <summary>
    /// The highest MINOR version this reader knows about.  A minor bump is ADDITIVE by definition, so a higher one is
    /// read, not refused: every offset already comes from the header.  What the number gates is SEMANTICS —
    /// <see cref="ExitSubjectsAreLatched"/> and, from v1.2, <see cref="Subject2IsDumpedWhenAbsent"/>;.  v1.3 is a
    /// pure APPEND: six more <c>probeGlobals</c> rows, a third subject object with its stamp, the subject block's
    /// exec window with its stamp, and three more probes.  It gates one further semantic —
    /// <see cref="Subject3IsDumped"/>. **4**;.  v1.4 appends the scorer's own decision triple to a P22 record and
    /// five more probes, and WIDENS the stage window <c>player_damage_and_score_block</c> to <c>[0xF1C6]+0x34</c> —
    /// absorbing the <c>g_damage_effect_hit_count_22_alias (ex-g_kill_confirmed_flag) [0xF1F6]+3</c> row it now
    /// contains — which moves every <c>globals[]</c> record offset after it.  A header-driven reader is unaffected;
    /// the number gates one further semantic, <see cref="ScoreCompareIsRecorded"/>. <b>5</b>;.  v1.5 widens the stage
    /// window <c>object_pool_and_grid_segments</c> 14 → 16 B so <c>g_grid_2d_cell_list_seg [0x0098]</c> is carried,
    /// and appends a FAR-table section to the <c>.tables.bin</c> sidecar: the two world-grid quadtree arenas and the
    /// 2-D cell-head array, none of which is in DGROUP or in the L1 image.  It gates one further semantic,
    /// <see cref="GridArenasAreCaptured"/>. <b>6</b>;.  v1.6 lands the seven widenings C7 and an earlier pass asked for: three new
    /// stage windows (the render-slot block <c>[0xD8A4]+0xBB8</c> — the lock-on list's NODES, the view anchor
    /// <c>[0xD88E]+22</c> and <c>g_view_changed_flag [0xF1B6]+2</c>), one widened stage window (<c>[0xF1B2]</c> 2 →
    /// 4, so the reticle's screen Y has a home), one more <c>probeGlobals</c> span (the <c>.S</c> module's OUT
    /// parameter <c>[0xB562]+2</c>), one appended probe field (<c>probeP26Out</c>) and two more probes (P30 the key
    /// ladder's cooked key, P31 <c>publish_view_mode</c>).  It gates two further semantics,
    /// <see cref="ProjectionOutWordsAreRecorded"/> and <see cref="RenderListNodesAreCaptured"/>.
    /// </summary>
    public const int KnownMinorVersion = 6;

    /// <summary>Record kind byte 0 — a STAGE record.</summary>
    public const byte KindStage = 0;

    /// <summary>Record kind byte 1 — a PROBE ENTRY record.</summary>
    public const byte KindProbeEntry = 1;

    /// <summary>Record kind byte 2 — a PROBE EXIT record.</summary>
    public const byte KindProbeExit = 2;

    private readonly Dictionary<string, int> _fields;
    private readonly Dictionary<int, CombatTraceStage> _stages;
    private readonly Dictionary<int, CombatTraceProbe> _probes;
    private readonly Dictionary<string, CombatTraceGlobal> _globals;
    private readonly List<CombatTraceGlobal> _globalsByOffset;
    private readonly Dictionary<string, CombatTraceGlobal> _probeGlobals;
    private readonly List<CombatTraceGlobal> _probeGlobalsByOffset;

    private CombatTraceHeader(
        string json,
        Dictionary<string, int> fields,
        Dictionary<int, CombatTraceStage> stages,
        Dictionary<int, CombatTraceProbe> probes,
        Dictionary<string, CombatTraceGlobal> globals,
        Dictionary<string, CombatTraceGlobal> probeGlobals)
    {
        Json = json;
        _fields = fields;
        _stages = stages;
        _probes = probes;
        _globals = globals;
        _globalsByOffset = [.. globals.Values.OrderBy(g => g.DgroupOffset)];
        _probeGlobals = probeGlobals;
        _probeGlobalsByOffset = [.. probeGlobals.Values.OrderBy(g => g.DgroupOffset)];
    }

    /// <summary>The header line exactly as it appeared, for provenance in a test's output.</summary>
    public string Json { get; }

    /// <summary>The <c>.evq</c> recording this leg replayed.</summary>
    public string? Recording { get; private set; }

    /// <summary>The guest image the leg ran.</summary>
    public string? Exe { get; private set; }

    /// <summary>The determinism seed as it was stamped (a <c>"0x…"</c> string).</summary>
    public string? Seed { get; private set; }

    /// <summary>The kernel era (<c>"det_v1"</c>).</summary>
    public string? Era { get; private set; }

    /// <summary>The lift roster the leg ran; a verification trace is <c>"none"</c>.</summary>
    public string? Lifts { get; private set; }

    /// <summary>True when <see cref="Lifts"/> says the leg ran with no lifts armed.</summary>
    public bool IsNoLift => string.Equals(Lifts, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The wire MINOR version; 0 for a pre-v1.1 file, which does not carry the key at all.
    /// </summary>
    public int MinorVersion { get; private set; }

    /// <summary>
    /// Whether a BX-subject probe's EXIT <see cref="CombatTraceRecord.Subject1"/> window is the object
    /// the call was made ON (v1.1+), or the 79 zero bytes a pre-v1.1 recorder produced by reading the
    /// callee-clobbered <c>BX</c>.
    /// </summary>
    /// <remarks>
    /// A consumer that INSTALLS an exit subject must check this: installing a void window wipes the
    /// subject's engagement block.  Probes P3 / P12 / P17 / P21 are the BX-subject ones.
    /// </remarks>
    public bool ExitSubjectsAreLatched => MinorVersion >= 1;

    /// <summary>
    /// Whether a probe record's <see cref="CombatTraceRecord.Subject2"/> window carries the pool
    /// segment's first bytes when <see cref="CombatTraceRecord.Subject2Offset"/> is 0 (v1.2+), or the
    /// 79 zero bytes a pre-v1.2 recorder wrote there.
    /// </summary>
    /// <remarks>
    /// The machine reads those bytes regardless — <c>shot_trajectory_proximity_accum</c>'s <c>rep
    /// movsw cx=0x0C</c> at <c>image@0x0859A</c> copies 24 bytes from <c>[0x0094]:[0xED6F]</c>
    /// whatever <c>[0xED6F]</c> holds.  Either way "no object" is decided by the STAMP being 0 and
    /// never by the window's contents.
    /// </remarks>
    public bool Subject2IsDumpedWhenAbsent => MinorVersion >= 2;

    /// <summary>
    /// Whether a probe record carries the THIRD subject object (the engagement block's owner through
    /// <c>g_engagement_player_slot_nearptr [0xED56]</c>) and the exec window at the SUBJECT BLOCK's
    /// own script segment (v1.3+);
    /// </summary>
    /// <remarks>
    /// A consumer that needs the VM's mode-5 inputs (<see cref="CombatTraceRecord.Subject3"/> plus
    /// <c>[0x0F80]</c>, which is transient inside a frame) or the pre-state of a SWAPPED program
    /// buffer (<see cref="CombatTraceRecord.SubjectExecBuffer"/>) must check this rather than stand
    /// in zeros; both gaps were measured, and the calls they cost counted.
    /// </remarks>
    public bool Subject3IsDumped => MinorVersion >= 3;

    /// <summary>
    /// Whether a P22 (<c>combat_target_score_and_fire</c>) probe record carries the scorer's own
    /// decision triple (v1.4+): <see cref="CombatTraceRecord.ScoreCompareScore"/>,
    /// <see cref="CombatTraceRecord.ScoreCompareFloor"/> and
    /// <see cref="CombatTraceRecord.ScoreCompareHits"/>;
    /// </summary>
    /// <remarks>
    /// Both values are STACK LOCALS of the callee (<c>AX</c> and <c>[bp-0x2C]</c> at the compare
    /// <c>cmp ax,[bp-0x2c]</c>, <c>image@0x07B8E</c>), so they are gone by the time the <c>ret</c>
    /// executes and no DGROUP window can carry them.  The <c>jb</c> that follows the compare is the
    /// function's ONLY success arm, so this triple is what explains a P22 return value the record's
    /// inputs alone cannot.  Zero on every other probe kind and on a pre-v1.4 file — a consumer
    /// must check this rather than read three zeros as "score 0, floor 0".
    /// </remarks>
    public bool ScoreCompareIsRecorded => MinorVersion >= 4;

    /// <summary>
    /// Whether the trace's <c>.tables.bin</c> sidecar carries the FAR-segment regions the world grid
    /// needs (v1.5+): the two quadtree arenas <c>g_world_grid_lo_seg [0xF136]</c> /
    /// <c>g_world_grid_hi_seg [0xF138]</c> and the 2-D cell-head array <c>[0x0098]:[0x0092]</c>;
    /// </summary>
    /// <remarks>
    /// None of them is in DGROUP and none is in the L1 image (they are built into far-heap segments
    /// at scene load), and an earlier pass measured that the arenas CANNOT be reconstructed from the dumped pool
    /// bytes — the cell-head array decides which objects each leaf holds, and 485 of 100,373 traced
    /// P20 answers are grid-sourced.  So a consumer that wants to run
    /// <c>world_grid_frustum_query_and_select</c> for real must check this rather than stand in an
    /// empty arena: an empty arena makes the 99,100 "found nothing" answers pass for the wrong
    /// reason.
    /// </remarks>
    public bool GridArenasAreCaptured => MinorVersion >= 5;

    /// <summary>
    /// Whether a P26 <c>object_screen_pos_project</c> record carries the two words its OUT POINTERS
    /// name (v1.6+, field <c>probeP26Out</c>);
    /// </summary>
    /// <remarks>
    /// P26 writes nothing itself: it forwards its <c>[bp+4]</c> / <c>[bp+6]</c> straight through to
    /// the perspective projector, so before v1.6 an exit record showed a projection's INPUTS and
    /// never its answer, and none of the 15,090 recorded pairs could feed anything.  From v1.6 the
    /// record carries <c>{u16 *arg0 (out_y), u16 *arg1 (out_x)}</c> on BOTH the entry (pre-call)
    /// and the exit (post-call) record, so a consumer must check this rather than read two zeros as
    /// a projection to the origin.
    /// </remarks>
    public bool ProjectionOutWordsAreRecorded => MinorVersion >= 6;

    /// <summary>
    /// Whether a STAGE record carries the RENDER-SLOT BLOCK <c>[0xD8A4..0xE45B]</c>, and with it the
    /// linked-list NODES the player lock-on walks from <c>g_engagement_object_list_head [0xE90A]</c>
    /// (v1.6+);
    /// </summary>
    /// <remarks>
    /// The nodes are 22-byte records bump-allocated DOWN from the top of that block and read on
    /// plain DS, and before v1.6 no window carried a single byte of one — which is why C9 had to
    /// count 148 P14 calls UNCERTIFIABLE and attribute 52 driver bytes to a named list-mode gap.  A
    /// consumer that walks the render list must check this rather than treat an absent window as an
    /// empty list.
    /// </remarks>
    public bool RenderListNodesAreCaptured => MinorVersion >= 6;

    /// <summary>
    /// The FAR regions the sidecar carries after the DGROUP static tables, in sidecar order;
    /// empty on a pre-v1.5 file.
    /// </summary>
    public IReadOnlyList<CombatTraceFarTable> FarTables { get; private set; } = [];

    /// <summary>Total bytes of the DGROUP <c>staticTables</c> section of the sidecar, i.e. where
    /// the far-table section begins; 0 on a pre-v1.5 file.</summary>
    public int StaticTablesLength { get; private set; }

    /// <summary>Looks a far table up by name.</summary>
    /// <param name="name"></param>
    /// <returns>The declaration, or null when this trace does not carry it.</returns>
    public CombatTraceFarTable? FarTable(string name)
    {
        foreach (CombatTraceFarTable t in FarTables)
        {
            if (string.Equals(t.Name, name, StringComparison.Ordinal))
            {
                return t;
            }
        }

        return null;
    }

    /// <summary>Bytes of <c>s_aircraft_master</c> in a stage record (298 in v1).</summary>
    public int MasterLength { get; private set; }

    /// <summary>Bytes of a pool-object window (79 in v1 = <c>0x18</c> + <c>0x37</c>).</summary>
    public int PoolObjectLength { get; private set; }

    /// <summary>Total bytes of the DGROUP windows in a stage record.</summary>
    public int GlobalsLength { get; private set; }

    /// <summary>Bytes of the pool arena appended to each stage record; 0 when none.</summary>
    public int PoolLength { get; private set; }

    /// <summary>Bytes of the VM register file in a probe record (126 in v1).</summary>
    public int VmRegisterLength { get; private set; }

    /// <summary>Bytes of the subject spawn record in a probe record (27 in v1).</summary>
    public int SpawnRecordLength { get; private set; }

    /// <summary>Bytes of the VM exec-buffer head in a probe record (64 in v1).</summary>
    public int ProbeExecLength { get; private set; }

    /// <summary>The DGROUP offset the VM register file window starts at.</summary>
    public int VmRegisterDgroupOffset { get; private set; }

    /// <summary>
    /// The DGROUP offset of the VM register file's TAIL window a probe record also carries
    /// (<c>0xEDCE</c>): the home of <c>g_engagement_kill_fired_flag [0xEDE5]</c>.  0 in a
    /// pre-v1.1 file.
    /// </summary>
    public int ScratchTailDgroupOffset { get; private set; }

    /// <summary>Bytes of that tail window (56).  0 in a pre-v1.1 file.</summary>
    public int ScratchTailLength { get; private set; }

    /// <summary>
    /// Bytes of the six-word OUT block a P20 probe record carries (12).  0 in a pre-v1.1 file.
    /// </summary>
    public int OutBlockLength { get; private set; }

    /// <summary>
    /// Total bytes of the three DGROUP spans appended to a probe record after the v1.1 fields
    /// (31 = <c>[0xB52E]</c>+26, <c>[0xF0D2]</c>+4, <c>[0x0F0C]</c>+1).  0 on a pre-v1.2 file.
    /// The per-span record offsets are in <see cref="ProbeGlobals"/>.
    /// </summary>
    public int ProbeExtrasLength { get; private set; }

    /// <summary>
    /// Bytes of the scorer's decision triple on a P22 probe record (6 = <c>u16 score</c>,
    /// <c>u16 floor</c>, <c>u16 compare-count</c>).  0 on a pre-v1.4 file.
    /// </summary>
    public int ScoreCompareLength { get; private set; }

    /// <summary>A STAGE record's stride in bytes.</summary>
    public int StageRecordLength { get; private set; }

    /// <summary>A PROBE record's stride in bytes.</summary>
    public int ProbeRecordLength { get; private set; }

    /// <summary>The DGROUP paragraph the <c>dgroupOff</c> values are relative to.</summary>
    public int DgroupSegment { get; private set; }

    /// <summary>The first det step the recorder windowed on, or 0.</summary>
    public long StepLow { get; private set; }

    /// <summary>The last det step the recorder windowed on, or null for "to the end".</summary>
    public long? StepHigh { get; private set; }

    /// <summary>The names of the fourteen register words at the <c>regs</c> field, in order.</summary>
    public IReadOnlyList<string> RegisterNames { get; private set; } = [];

    /// <summary>The stage table, by id.</summary>
    public IReadOnlyDictionary<int, CombatTraceStage> Stages => _stages;

    /// <summary>The probe table, by id.</summary>
    public IReadOnlyDictionary<int, CombatTraceProbe> Probes => _probes;

    /// <summary>The DGROUP windows, by name.</summary>
    public IReadOnlyDictionary<string, CombatTraceGlobal> Globals => _globals;

    /// <summary>
    /// The DGROUP windows a PROBE record carries, by name: the header's <c>probeGlobals</c> array,
    /// which is to a probe record exactly what <c>globals</c> is to a stage record.  EMPTY on a
    /// pre-v1.2 trace, whose probe DGROUP windows are reachable only through the <c>probeVm</c> /
    /// <c>probeScratchTail</c> field offsets.
    /// </summary>
    /// <remarks>
    /// v1.2 lists five spans: the VM register file <c>[0xED3C..0xEDC5]</c>, the scratch tail
    /// <c>[0xEDCE..0xEE05]</c>, and the three an earlier pass asked for — <c>[0xB52E..0xB547]</c> (the FSM
    /// prologue's outputs and the target-snapshot cache keys), <c>[0xF0D2..0xF0D5]</c> (the frame-time
    /// accumulator) and <c>[0x0F0C]</c> (the proximity fuze's verdict).
    /// </remarks>
    public IReadOnlyDictionary<string, CombatTraceGlobal> ProbeGlobals => _probeGlobals;

    /// <summary>Byte offset of a named record field (<c>step</c>, <c>master</c>, <c>pool</c>, …).</summary>
    /// <param name="name">The field's name in the header's <c>fields</c> object.</param>
    /// <exception cref="InvalidDataException">The header does not carry that field.</exception>
    public int Field(string name) => _fields.TryGetValue(name, out int offset)
        ? offset
        : throw new InvalidDataException(
            $"the combat-trace header has no 'fields.{name}' offset (format v1 defines it)");

    /// <summary>Whether the header carries a named field offset at all.</summary>
    /// <param name="name">The field's name.</param>
    public bool HasField(string name) => _fields.ContainsKey(name);

    /// <summary>The record stride for a kind byte, or −1 for an unknown kind (§1: STOP).</summary>
    /// <param name="kind">The record's leading <c>kind</c> byte.</param>
    public int RecordLength(byte kind) => kind switch
    {
        KindStage => StageRecordLength,
        KindProbeEntry or KindProbeExit => ProbeRecordLength,
        _ => -1,
    };

    /// <summary>The index of a named register inside a record's register group, or −1.</summary>
    /// <param name="name">One of the header's <c>regNames</c>.</param>
    public int RegisterIndex(string name)
    {
        for (int i = 0; i < RegisterNames.Count; i++)
        {
            if (string.Equals(RegisterNames[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Locates a DGROUP address inside the dumped windows — the reader's <c>dgroup()</c> helper.
    /// </summary>
    /// <param name="dgroupOffset">The DGROUP offset to read.</param>
    /// <param name="length">How many bytes.</param>
    /// <returns>The record offset, or −1 when no window contains the whole range.</returns>
    public int RecordOffsetOfDgroup(int dgroupOffset, int length)
    {
        foreach (CombatTraceGlobal window in _globalsByOffset)
        {
            if (window.DgroupOffset <= dgroupOffset
                && dgroupOffset + length <= window.DgroupOffset + window.Length)
            {
                return window.RecordOffset + (dgroupOffset - window.DgroupOffset);
            }
        }

        return -1;
    }

    /// <summary>
    /// The same lookup for a PROBE record: locates a DGROUP address inside the windows a probe
    /// record carries (<see cref="ProbeGlobals"/>).
    /// </summary>
    /// <param name="dgroupOffset">The DGROUP offset to read.</param>
    /// <param name="length">How many bytes.</param>
    /// <returns>The record offset, or −1 when no probe window contains the whole range.</returns>
    public int ProbeRecordOffsetOfDgroup(int dgroupOffset, int length)
    {
        foreach (CombatTraceGlobal window in _probeGlobalsByOffset)
        {
            if (window.DgroupOffset <= dgroupOffset
                && dgroupOffset + length <= window.DgroupOffset + window.Length)
            {
                return window.RecordOffset + (dgroupOffset - window.DgroupOffset);
            }
        }

        return -1;
    }

    /// <summary>Parses one header line.</summary>
    /// <param name="json">The file's first line, without its terminating newline.</param>
    /// <exception cref="InvalidDataException">
    /// The line is not a v1 <c>cyac-combat-trace</c> header, or a required key is missing.
    /// </exception>
    public static CombatTraceHeader Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        string format = String(root, "format");
        if (!string.Equals(format, FormatName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"not a combat trace: format is '{format}', expected '{FormatName}'");
        }

        int version = Int(root, "version");
        if (version != SupportedVersion)
        {
            throw new InvalidDataException(
                $"combat-trace version {version} is not v{SupportedVersion}; this reader refuses to "
                    + "guess a layout it does not know.  NB a MINOR "
                    + "bump (v1.1) is additive and does not land here — it is read normally.");
        }

        Dictionary<string, int> fields = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonProperty field in root.GetProperty("fields").EnumerateObject())
        {
            fields[field.Name] = field.Value.GetInt32();
        }

        Dictionary<int, CombatTraceStage> stages = new Dictionary<int, CombatTraceStage>();
        if (root.TryGetProperty("stages", out JsonElement stageList))
        {
            foreach (JsonElement entry in stageList.EnumerateArray())
            {
                int id = Int(entry, "id");
                stages[id] = new CombatTraceStage(
                    id,
                    String(entry, "name"),
                    ParseImageOffset(OptionalString(entry, "image") ?? "0"),
                    Bool(entry, "enabled", true),
                    OptionalString(entry, "meaning"));
            }
        }

        Dictionary<int, CombatTraceProbe> probes = new Dictionary<int, CombatTraceProbe>();
        if (root.TryGetProperty("probes", out JsonElement probeList))
        {
            foreach (JsonElement entry in probeList.EnumerateArray())
            {
                int id = Int(entry, "id");
                List<int> exits = new List<int>();
                if (entry.TryGetProperty("exits", out JsonElement exitList))
                {
                    foreach (JsonElement exit in exitList.EnumerateArray())
                    {
                        exits.Add(ParseImageOffset(exit.GetString() ?? "0"));
                    }
                }

                probes[id] = new CombatTraceProbe(
                    id,
                    String(entry, "name"),
                    ParseImageOffset(OptionalString(entry, "entry") ?? "0"),
                    exits,
                    Bool(entry, "far", false),
                    Bool(entry, "enabled", true),
                    OptionalString(entry, "subject"));
            }
        }

        Dictionary<string, CombatTraceGlobal> globals = new Dictionary<string, CombatTraceGlobal>(StringComparer.Ordinal);
        if (root.TryGetProperty("globals", out JsonElement globalList))
        {
            foreach (JsonElement entry in globalList.EnumerateArray())
            {
                string name = String(entry, "name");
                globals[name] = new CombatTraceGlobal(
                    name, Int(entry, "dgroupOff"), Int(entry, "len"), Int(entry, "recOff"));
            }
        }

        // The probe record's own DGROUP map; absent on a pre-v1.2 file.
        Dictionary<string, CombatTraceGlobal> probeGlobals = new Dictionary<string, CombatTraceGlobal>(StringComparer.Ordinal);
        if (root.TryGetProperty("probeGlobals", out JsonElement probeGlobalList))
        {
            foreach (JsonElement entry in probeGlobalList.EnumerateArray())
            {
                string name = String(entry, "name");
                probeGlobals[name] = new CombatTraceGlobal(
                    name, Int(entry, "dgroupOff"), Int(entry, "len"), Int(entry, "recOff"));
            }
        }

        CombatTraceHeader header = new CombatTraceHeader(json, fields, stages, probes, globals, probeGlobals)
        {
            Recording = OptionalString(root, "recording"),
            Exe = OptionalString(root, "exe"),
            Seed = OptionalString(root, "seed"),
            Era = OptionalString(root, "era"),
            Lifts = OptionalString(root, "lifts"),
            MasterLength = Int(root, "masterLen"),
            PoolObjectLength = Int(root, "playerObjLen"),
            GlobalsLength = Int(root, "globalsLen"),
            PoolLength = OptionalInt(root, "poolLen") ?? 0,
            VmRegisterLength = OptionalInt(root, "vmRegLen") ?? 0,
            VmRegisterDgroupOffset = OptionalInt(root, "vmRegOff") ?? 0,
            SpawnRecordLength = OptionalInt(root, "spawnRecLen") ?? 0,
            MinorVersion = OptionalInt(root, "minor") ?? 0,
            ScratchTailDgroupOffset = OptionalInt(root, "scratchTailOff") ?? 0,
            ScratchTailLength = OptionalInt(root, "scratchTailLen") ?? 0,
            OutBlockLength = OptionalInt(root, "outBlockLen") ?? 0,
            ProbeExtrasLength = OptionalInt(root, "probeExtrasLen") ?? 0,
            ScoreCompareLength = OptionalInt(root, "scoreCmpLen") ?? 0,
            ProbeExecLength = OptionalInt(root, "probeExecLen") ?? 0,
            StageRecordLength = Int(root, "stageRecordLen"),
            ProbeRecordLength = Int(root, "probeRecordLen"),
            DgroupSegment = Int(root, "dgroupSeg"),
            StepLow = OptionalInt(root, "stepLo") ?? 0,
            StepHigh = OptionalInt(root, "stepHi"),
            RegisterNames = ReadStrings(root, "regNames"),
            StaticTablesLength = OptionalInt(root, "staticTablesLen") ?? 0,
        };

        // The FAR-table section of the .tables.bin sidecar; absent before v1.5.
        if (root.TryGetProperty("farTables", out JsonElement farList))
        {
            List<CombatTraceFarTable> far = new List<CombatTraceFarTable>();
            foreach (JsonElement entry in farList.EnumerateArray())
            {
                far.Add(new CombatTraceFarTable(
                    String(entry, "name"),
                    Int(entry, "sidecarOff"),
                    OptionalInt(entry, "descLen") ?? 8,
                    Int(entry, "payloadLen"),
                    OptionalString(entry, "source")));
            }

            header.FarTables = far;
        }

        if (header.StageRecordLength <= 0 || header.ProbeRecordLength <= 0)
        {
            throw new InvalidDataException(
                $"combat-trace record strides must be positive (stage {header.StageRecordLength}, "
                    + $"probe {header.ProbeRecordLength})");
        }

        return header;
    }

    /// <summary>A one-line provenance summary for a test's output.</summary>
    public override string ToString() =>
        $"{FormatName} v{SupportedVersion}.{MinorVersion} recording={Recording} exe={Exe} seed={Seed} era={Era} "
            + $"lifts={Lifts} stage={StageRecordLength}B probe={ProbeRecordLength}B "
            + $"pool={PoolLength}B stages={_stages.Count} probes={_probes.Count} "
            + $"globals={_globals.Count} steps={StepLow}..{(StepHigh?.ToString(CultureInfo.InvariantCulture) ?? "end")}";

    private static int ParseImageOffset(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(text, CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> ReadStrings(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement list))
        {
            return [];
        }

        List<string> values = new List<string>();
        foreach (JsonElement entry in list.EnumerateArray())
        {
            values.Add(entry.GetString() ?? string.Empty);
        }

        return values;
    }

    private static string String(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.GetString() is { } text
            ? text
            : throw new InvalidDataException($"the combat-trace header has no string '{property}'");

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int number)
            ? number
            : throw new InvalidDataException($"the combat-trace header has no number '{property}'");

    /// <remarks>
    /// The <c>ValueKind</c> guard is load-bearing.  A WHOLE-RECORDING leg (no
    /// <c>--combat-trace-steps</c>) writes <c>"stepHi": null</c>, and
    /// <c>JsonElement.TryGetInt32</c> THROWS on a null token rather than returning false — so every
    /// un-windowed trace was unreadable until this guard existed.  Every trace before C7's was
    /// windowed, which is why no earlier builder met it.
    /// </remarks>
    private static int? OptionalInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
                ? number
                : null;

    private static bool Bool(JsonElement element, string property, bool fallback) =>
        element.TryGetProperty(property, out JsonElement value)
            ? value.ValueKind == JsonValueKind.True
            : fallback;
}
