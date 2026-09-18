using CYAC.Port.Core.Model.Combat;

namespace CYAC.Port.Core.Sim.Combat.Effects;

/// <summary>
/// The DEFERRED-EFFECT pool <c>[0xB4A2..0xB520]</c> — ten 14-byte records, each owning a pool object
/// that is switched on at an impact point for one master frame and then hands over to a smoke
/// emitter.
/// </summary>
/// <remarks>
/// <para>
/// This is the "impact flash then a puff" of a shell striking something.  The scheduler
/// (<c>deferred_effect_record_schedule @image@0x03A7C</c>) lights the record's own pool object at the
/// impact point and stamps a deadline <c>0x100</c> frame-time units out; the per-frame tick
/// (<c>deferred_effect_pool_fire_tick @image@0x03C22</c>, one of the mission frame's five effect
/// ticks) fires every record whose deadline has passed, which switches the flash object OFF and, when
/// the record carries an effect type, attaches a KIND-3 emitter row at the same point
/// (<c>deferred_effect_record_fire @image@0x03BC8</c> → <c>subsystem4x19_row_attach</c> with
/// <c>owner = 0</c>).
/// </para>
/// <para>Record layout: <c>+0</c> the pool object, <c>+2</c> the <c>u32</c> schedule time,
/// <c>+6</c> the <c>u32</c> deadline, <c>+0x0A</c> a sequence number, <c>+0x0C</c> a parameter byte,
/// <c>+0x0D</c> the effect type (0 = flash only).</para>
/// </remarks>
public static class DeferredEffectPool
{
    /// <summary>The LAST record (<c>mov si,0xB520</c> @<c>image@0x03A84</c>).</summary>
    public const int LastRecord = 0xB520;

    /// <summary>The FIRST (<c>cmp si,0xB4A2 / jae</c> @<c>image@0x03AA6</c>).</summary>
    public const int FirstRecord = 0xB4A2;

    /// <summary>One record's stride: <c>0x0E</c> = 14 bytes.</summary>
    public const int RecordStride = 0x0E;

    /// <summary>How many records there are: 10.</summary>
    public const int RecordCount = ((LastRecord - FirstRecord) / RecordStride) + 1;

    /// <summary>
    /// The EVICTION scan's lower bound (<c>cmp bx,0xB4B0 / jae</c> @<c>image@0x03AE8</c>) — one
    /// record higher than the free scan's, so record <c>0xB4A2</c> is never evicted.  Shipped
    /// behaviour, reproduced.
    /// </summary>
    public const int EvictionLowestRecord = 0xB4B0;

    /// <summary>The floor the scheduler clamps the impact Y to: <c>0x00001E00</c>.</summary>
    /// <remarks><c>cmp word [bp+0xa],0x1E00 / jae</c> @<c>image@0x03AFD</c> — a flash never sinks
    /// into the ground plane.</remarks>
    public const int MinimumY = 0x1E00;

    /// <summary>How long a flash lives, in frame-time units: <c>0x100</c> — one master frame.</summary>
    public const int FlashFrameTime = 0x100;

    /// <summary>The puff kind the fire hands the emitter: 3 (<c>mov al,3</c> @<c>image@0x03BF5</c>).</summary>
    public const byte FireEmitterKind = 3;

    /// <summary><c>g_frame_time_accum [0xF0D2]</c> — the 32-bit clock the deadlines are on.</summary>
    public const int FrameTimeAccumulator = 0xF0D2;

    /// <summary><c>g_deferred_effect_sequence [0x0DFC]</c>.</summary>
    public const int SequenceCounter = 0x0DFC;

    /// <summary><c>[0x1028]</c> — the record the HUD's selected-object readout is following.</summary>
    public const int SelectedRecord = 0x1028;

    /// <summary>The ten record offsets, LAST first.</summary>
    public static IEnumerable<int> Records()
    {
        for (int record = LastRecord; record >= FirstRecord; record -= RecordStride)
        {
            yield return record;
        }
    }

    /// <summary>
    /// <c>deferred_effect_record_schedule @image@0x03A7C</c> — light a flash at an impact point.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="arena">The pool.</param>
    /// <param name="position">The impact point; its Y is clamped to <see cref="MinimumY"/>.</param>
    /// <param name="parameter">The record's <c>+0x0C</c> byte.</param>
    /// <param name="effectType">The record's <c>+0x0D</c> byte; 0 means "flash only, no emitter".</param>
    /// <param name="subject">
    /// The object the caller is describing; when it equals <c>g_selected_object [0x00BE]</c> the
    /// record becomes the HUD's followed one (<c>image@0x03BB2</c>).
    /// </param>
    /// <returns>The record used, or −1 when every record is busy and none could be evicted.</returns>
    public static int Schedule(
        CombatRegisters registers,
        PoolArena arena,
        CombatPosition position,
        byte parameter,
        byte effectType,
        ushort subject)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        int record = -1;
        foreach (int candidate in Records())
        {
            ushort obj = registers.Word(candidate);
            if (obj != 0 && arena.Covers(obj, 4) && (arena.Byte((ushort)(obj + 2)) & 1) == 0)
            {
                record = candidate;
                break;                                               // image@0x03AA1
            }
        }

        if (record < 0)
        {
            // image@0x03AB2..0x03AF2 — evict the EARLIEST-scheduled record and fire it first.
            record = FirstRecord;
            uint earliest = ReadU32(registers, FirstRecord + 2);
            for (int candidate = LastRecord; candidate >= EvictionLowestRecord; candidate -= RecordStride)
            {
                uint stamp = ReadU32(registers, candidate + 2);
                if (stamp < earliest)
                {
                    record = candidate;
                    earliest = stamp;
                }
            }

            Fire(registers, arena, record, act: true);
        }

        ushort target = registers.Word(record);
        if (target == 0 || !arena.Covers(target, 0x12))
        {
            return -1;
        }

        int y = position.Y < MinimumY ? MinimumY : position.Y;       // image@0x03AFD
        new CombatObjectView(arena, target).Position =
            new CombatPosition(position.X, y, position.Z);
        arena.SetByte((ushort)(target + 2), (byte)(arena.Byte((ushort)(target + 2)) | 1));

        uint now = ReadU32(registers, FrameTimeAccumulator);
        WriteU32(registers, record + 2, now);                        // image@0x03B64
        WriteU32(registers, record + 6, now + FlashFrameTime);       // image@0x03B77
        ushort sequence = unchecked((ushort)(registers.Word(SequenceCounter) + 1));
        registers.SetWord(SequenceCounter, sequence);
        registers.SetWord(record + 0x0A, sequence);
        registers.SetByte(record + 0x0C, parameter);
        registers.SetByte(record + 0x0D, effectType);

        if (registers.Word(0x00BE) == subject)                       // image@0x03BB2
        {
            registers.SetWord(SelectedRecord, (ushort)record);
            registers.SetWord(0xEF94, subject);
        }

        return record;
    }

    /// <summary>
    /// <c>deferred_effect_record_fire @image@0x03BC8</c> — the flash goes out and, when the record
    /// carries a type, an emitter row takes its place.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="arena">The pool.</param>
    /// <param name="record">The record's DGROUP offset.</param>
    /// <param name="act">
    /// The <c>AL</c> argument: 1 from the per-frame tick (fire the effect), 0 from the silent flush.
    /// </param>
    /// <returns>True when an emitter row was attached.</returns>
    public static bool Fire(CombatRegisters registers, PoolArena arena, int record, bool act)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        ushort obj = registers.Word(record);
        bool attached = false;
        if (obj != 0 && arena.Covers(obj, 0x12))
        {
            arena.SetByte((ushort)(obj + 2), (byte)(arena.Byte((ushort)(obj + 2)) & 0xFE));
            if (registers.Byte(record + 0x0D) != 0 && act)
            {
                attached = EffectEmitterTable.Attach(
                    registers,
                    arena,
                    new CombatObjectView(arena, obj).Position,
                    ownerRef: 0,
                    interval: 0,
                    firstDelay: 0,
                    lifetime: 0,
                    classifier: 0,
                    kind: FireEmitterKind) >= 0;                     // image@0x03C08
            }
        }

        if (registers.Word(SelectedRecord) == record)
        {
            registers.SetWord(SelectedRecord, 0);                    // image@0x03C13
        }

        return attached;
    }

    /// <summary>
    /// <c>deferred_effect_pool_fire_tick @image@0x03C22</c> — one of the five effect ticks: fire
    /// every record whose deadline has passed.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="arena">The pool.</param>
    /// <returns>How many records fired.</returns>
    public static int PerFrameTick(CombatRegisters registers, PoolArena arena)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        uint now = ReadU32(registers, FrameTimeAccumulator);
        int fired = 0;
        foreach (int record in Records())
        {
            ushort obj = registers.Word(record);
            if (obj == 0 || !arena.Covers(obj, 4) || (arena.Byte((ushort)(obj + 2)) & 1) == 0)
            {
                continue;                                            // image@0x03C2E
            }

            // image@0x03C3C — the hi word is compared SIGNED (jg/jl) and the lo word UNSIGNED (ja),
            // the MSC codegen asymmetry the scanner's own note names.
            short deadlineHi = unchecked((short)registers.Word(record + 8));
            short nowHi = unchecked((short)(now >> 16));
            if (deadlineHi > nowHi)
            {
                continue;
            }

            if (deadlineHi == nowHi && registers.Word(record + 6) > (ushort)now)
            {
                continue;
            }

            Fire(registers, arena, record, act: true);
            fired++;
        }

        return fired;
    }

    private static uint ReadU32(CombatRegisters registers, int at) =>
        (uint)(registers.Word(at) | (registers.Word(at + 2) << 16));

    private static void WriteU32(CombatRegisters registers, int at, uint value)
    {
        registers.SetWord(at, unchecked((ushort)value));
        registers.SetWord(at + 2, unchecked((ushort)(value >> 16)));
    }
}
