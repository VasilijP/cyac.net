using CYAC.Port.Core.Sim.Combat;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// The mission's NAVIGATION SLOTS: the three-word state the original's NAV key ladder owns, and the waypoint records
/// the two cockpit bearing instruments read through it.
/// </summary>
/// <remarks>
/// <para>
/// The state is three DGROUP globals, and all three are inside the port's VERIFIED register
/// windows (<c>CombatRegisterWindows</c>: <c>nav_slots_and_master_base</c> at <c>0xEF90</c> covers
/// the count and the index, and <c>deferred_effects_and_acq_flags</c> at <c>0xB520</c> ends on
/// <c>0xB564</c> so it covers the flag) — so this type keeps them THERE rather than in a shadow of
/// its own:
/// </para>
/// <list type="table">
///   <item><term><c>g_nav_slot_count [0xEF90]</c></term><description>u16 — how many slots the load registered.</description></item>
///   <item><term><c>g_nav_slot_current_index [0xEF92]</c></term><description>u16 (compared SIGNED) — the selected slot.</description></item>
///   <item><term><c>g_nav_slot_initialized_flag [0xB563]</c></term><description>u8 — 0 until the first NAV keypress.</description></item>
/// </list>
/// <para>
/// The RECORDS themselves live in <c>g_nav_slot_record_array [0xB564]</c> (stride <c>0x2C</c>:
/// <c>[+0]</c> type byte, <c>[+1..+0xC]</c> 3×i32 world position, <c>[+0xD]</c> <c>char[31]</c>
/// name) and are written by <c>nav_slot_record_register @image@0x08CE4</c> from the <c>.S</c>
/// stream.  Only the array's FIRST byte is inside a verified window, so the port keeps the records
/// beside the register file as <see cref="NavWaypointPlacement"/>s — which is where
/// <see cref="ScenarioObjectLoader"/> already put them — and reproduces only the count law
/// (<c>image@0x08CEA..0x08CF7</c>) in the register.
/// </para>
/// <para>
/// The two cycle thunks are transliterated from their bytes; see <see cref="CycleNext"/> and
/// <see cref="CyclePrevious"/>.  <c>nav_waypoint_show_current @image@0x08D31</c> is
/// <see cref="Text"/>: the NAV label format (DGROUP <c>[0x0F9E]</c>, <c>image@0x3CCFE</c>, read from the
/// tree as <see cref="Model.Cockpit.InFlightStrings.NavLabelFormat"/>) posted through
/// <c>show_cockpit_text_string @image@0x0CC5B</c>, whose appender stamps the same six-unit expiry
/// every other strip message gets.
/// </para>
/// </remarks>
public sealed class NavSlots
{
    /// <summary><c>g_nav_slot_count [0xEF90]</c> — <c>image@0x08D73</c>.</summary>
    public const int CountWord = 0xEF90;

    /// <summary><c>g_nav_slot_current_index [0xEF92]</c> — <c>image@0x08D3A</c>.</summary>
    public const int CurrentIndexWord = 0xEF92;

    /// <summary><c>g_nav_slot_initialized_flag [0xB563]</c> — <c>image@0x08D65</c>.</summary>
    public const int InitializedFlagByte = 0xB563;

    /// <summary><c>g_nav_slot_record_array [0xB564]</c> — the record array's DGROUP base.</summary>
    public const int RecordArrayBase = 0xB564;

    /// <summary>One record's stride — <c>mov ax,0x2c / imul</c> at <c>image@0x08D37</c>.</summary>
    public const int RecordStride = 0x2C;

    /// <summary>Where a record's name string starts — <c>add ax,0x0d</c> at <c>image@0x08D06</c>.</summary>
    public const int RecordNameOffset = 0x0D;

    /// <summary>The longest name a record can hold, <c>char[31]</c> including the NUL.</summary>
    public const int RecordNameBytes = 31;

    private readonly CombatRegisters _registers;
    private readonly Model.Cockpit.InFlightStrings _strings;
    private readonly List<NavWaypointPlacement?> _records = [];

    /// <summary>Creates the nav state over a register file.</summary>
    /// <param name="registers">The session's verified register file.</param>
    /// <param name="strings">The in-flight words, for the label's format.</param>
    public NavSlots(CombatRegisters registers, Model.Cockpit.InFlightStrings strings)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(strings);
        _registers = registers;
        _strings = strings;
    }

    /// <summary>
    /// <c>nav_slot_state_reset @image@0x08CD6</c> — <c>xor ax,ax</c> into the count and the index,
    /// and <c>mov byte [0xB563],0</c>.
    /// </summary>
    /// <remarks>
    /// It does NOT clear the record array (14 bytes, <c>2B C0 A3 90 EF A3 92 EF C6 06 63 B5 00
    /// CB</c>), so a slot the new mission does not register keeps the previous mission's name in the
    /// original.  The port drops the records instead, and says "not registered" where the original
    /// would show a stale label — a labelled deviation, and the safer of the two.
    /// </remarks>
    public void Reset()
    {
        _registers.SetWord(CountWord, 0);
        _registers.SetWord(CurrentIndexWord, 0);
        _registers.SetByte(InitializedFlagByte, 0);
        _records.Clear();
    }

    /// <summary>
    /// <c>nav_slot_record_register @image@0x08CE4</c>'s COUNT law, plus the record itself.
    /// </summary>
    /// <param name="waypoint">The placement the loader resolved.</param>
    /// <remarks>
    /// <c>image@0x08CEA..0x08CF7</c>: <c>mov ax,[0xEF90] / cmp [bp+0x0A],ax / jl +7 / mov
    /// ax,[bp+0x0A] / inc ax / mov [0xEF90],ax</c> — the count becomes <c>slot + 1</c> whenever the
    /// registered slot is not below it, so a sparse stream still leaves the count one past the
    /// HIGHEST slot.  The record is written unconditionally at
    /// <c>[0xB564] + 0x2C·slot</c>, so a repeated slot OVERWRITES: the last registration wins, which
    /// is why the theatre's waypoints are fed before the mission's.
    /// </remarks>
    public void Register(NavWaypointPlacement waypoint)
    {
        int slot = waypoint.Slot;
        if (slot < 0)
        {
            return;
        }

        // image@0x08CEA..0x08CF7 — the signed `jl` guard, transliterated.
        int count = unchecked((short)_registers.Word(CountWord));
        if (slot >= count)
        {
            _registers.SetWord(CountWord, unchecked((ushort)(slot + 1)));
        }

        while (_records.Count <= slot)
        {
            _records.Add(null);
        }

        _records[slot] = waypoint;
    }

    /// <summary>How many slots the load registered — <c>g_nav_slot_count [0xEF90]</c>.</summary>
    public int Count => unchecked((short)_registers.Word(CountWord));

    /// <summary>
    /// The selected slot — <c>g_nav_slot_current_index [0xEF92]</c>, read the way the original
    /// compares it (signed).
    /// </summary>
    public int CurrentIndex => unchecked((short)_registers.Word(CurrentIndexWord));

    /// <summary>Whether a NAV key has been pressed this sortie — <c>[0xB563]</c>.</summary>
    public bool Initialized => _registers.Byte(InitializedFlagByte) != 0;

    /// <summary>The records, indexed by slot; a slot nothing registered is <see langword="null"/>.</summary>
    public IReadOnlyList<NavWaypointPlacement?> Records => _records;

    /// <summary>The selected slot's record, or <see langword="null"/>.</summary>
    public NavWaypointPlacement? Current => Record(CurrentIndex);

    /// <summary>The record in one slot, or <see langword="null"/> when nothing registered it.</summary>
    /// <param name="slot">The slot index.</param>
    public NavWaypointPlacement? Record(int slot) =>
        (uint)slot < (uint)_records.Count ? _records[slot] : null;

    /// <summary>
    /// <c>nav_slot_cycle_next @image@0x08D65</c> — the <b>W</b> key (dispatch <c>image@0x0127D</c>).
    /// </summary>
    /// <returns>The cockpit text the thunk's tail posts, or null when no record answers the slot.</returns>
    /// <remarks>
    /// Bytes: <c>80 3E 63 B5 00 / 75 07 / C6 06 63 B5 01 / EB 13 / A1 90 EF / FF 06 92 EF /
    /// 39 06 92 EF / 7C 06 / C7 06 92 EF 00 00 / E8 A8 FF / CB</c>.  The first press only sets the
    /// flag and SHOWS slot 0; every later press increments and wraps to 0 once the index is no
    /// longer <c>jl</c>-below the count.
    /// <para>
    /// With <c>count = 0</c> the increment makes the index 1, the signed compare against 0 fails and the
    /// wrap puts it back to 0 — so a Test Flight's NAV key is inert but not corrupting.
    /// </para>
    /// </remarks>
    public string? CycleNext()
    {
        if (!Initialized)
        {
            _registers.SetByte(InitializedFlagByte, 1);       // image@0x08D6C
            return ShowCurrent();                             // image@0x08D71 -> 0x08D86
        }

        int count = Count;                                    // image@0x08D73
        int index = CurrentIndex + 1;                         // image@0x08D76
        _registers.SetWord(CurrentIndexWord, unchecked((ushort)index));
        if (index >= count)                                   // image@0x08D7A: jl skips the wrap
        {
            _registers.SetWord(CurrentIndexWord, 0);          // image@0x08D80
        }

        return ShowCurrent();
    }

    /// <summary>
    /// <c>nav_slot_cycle_prev @image@0x08D8A</c> — <b>Shift+W</b> (dispatch <c>image@0x0128C</c>).
    /// </summary>
    /// <returns>The cockpit text, or null when no record answers the slot.</returns>
    /// <remarks>
    /// Bytes: <c>80 3E 63 B5 00 / 75 07 / C6 06 63 B5 01 / EB 0D / FF 0E 92 EF / 79 07 /
    /// A1 90 EF / 48 / A3 92 EF / E8 89 FF / CB</c>.  The underflow test is <c>jns</c> — the SIGN of
    /// the decremented word — so the wrap target is <c>count − 1</c>.
    /// <para>
    /// With <c>count = 0</c> the wrap writes <c>0 − 1 = 0xFFFF</c> into the index and the original's
    /// <c>%s</c> then points at <c>0x2C·(−1) + 0xB571 = </c> DGROUP <c>0xB545</c> — a shipped
    /// OUT-OF-BOUNDS read, 44 bytes BELOW the record array, in the deferred-effect block. The port
    /// reproduces the index arithmetic exactly and reads no record for it, so it prints nothing
    /// where the original prints whatever that block happens to hold.  Reaching it takes two presses
    /// in a sortie with no waypoints (the first only sets <c>[0xB563]</c>).
    /// </para>
    /// </remarks>
    public string? CyclePrevious()
    {
        if (!Initialized)
        {
            _registers.SetByte(InitializedFlagByte, 1);       // image@0x08D91
            return ShowCurrent();                             // image@0x08D96 -> 0x08DA5
        }

        int index = CurrentIndex - 1;                         // image@0x08D98
        _registers.SetWord(CurrentIndexWord, unchecked((ushort)index));
        if (index < 0)                                        // image@0x08D9C: jns skips the wrap
        {
            _registers.SetWord(                               // image@0x08D9E..0x08DA2
                CurrentIndexWord, unchecked((ushort)(Count - 1)));
        }

        return ShowCurrent();
    }

    /// <summary>
    /// <c>slot_record_get_pos @image@0x08E0D</c> — where a waypoint IS this frame, in the same Q8
    /// feet the player's <c>+0x06</c> / <c>+0x0A</c> / <c>+0x0E</c> carry.
    /// </summary>
    /// <param name="waypoint">The registered record.</param>
    /// <param name="arena">The object pool.</param>
    /// <param name="registers">The register file, for <c>g_spawn_slot_nearptr_table [0xEE5A]</c>.</param>
    /// <returns>Its position, or null when the record answers no position this frame.</returns>
    /// <remarks>
    /// <para>
    /// Two paths, on the record's <c>[+0]</c> type byte (<c>cmp byte [di],0</c> at
    /// <c>image@0x08E1E</c>): a STATIC waypoint is its own recorded point, copied 12 bytes
    /// (<c>rep movsw</c> with <c>CX = 6</c>, <c>image@0x08E56</c>); a TRACKING one tries its three
    /// sub-ids at <c>[+1]</c>, <c>[+5]</c> and <c>[+9]</c> through
    /// <c>spawn_slot_pos_check_and_copy @image@0x08DA9</c> and the FIRST success wins
    /// (<c>image@0x08E22..0x08E4A</c>).
    /// </para>
    /// <para>
    /// The per-slot check is FOUR tests, and the port had only the first two: the slot's near pointer
    /// must be non-zero (<c>image@0x08DBC</c>); the object's <c>+0x02</c> flag word must have bit 0
    /// (<c>test es:[bx+2],1</c> @<c>image@0x08DCB</c>); its engagement sub-record's <c>[+0x0D]</c> type
    /// byte must NOT be 6 (<c>image@0x08DDB</c>); and its <c>+0x00</c> class pointer must NOT be the
    /// CRATER record <c>0x52A2</c> (<c>cmp word es:[si],0x52a2</c> @<c>image@0x08DE5</c>).  The last one
    /// is exactly "the tracked aeroplane is a wreck", which is why a tracking waypoint stops answering
    /// when its actors die.
    /// </para>
    /// </remarks>
    public static (int X, int Y, int Z)? ResolvePosition(
        NavWaypointPlacement waypoint, PoolArena arena, CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(registers);

        if (!waypoint.Tracks)
        {
            // image@0x08E4B — the static arm: the record's own 12 bytes.
            return (waypoint.PosX, waypoint.PosY, waypoint.PosZ);
        }

        foreach (int slot in waypoint.TrackedActorSlots)
        {
            // image@0x08DB2 — AL = 0xFF is the "no slot" sentinel; the table has 13 entries.
            if ((uint)slot >= (uint)ScenarioObjectLoader.ActorSlotCount)
            {
                continue;
            }

            ushort objectRef = registers.Word(
                ScenarioObjectLoader.NamedPlaceNearPtrTable + (slot * 2));   // image@0x08DBC
            if (objectRef == 0 || !arena.Covers(objectRef, 0x1A))
            {
                continue;
            }

            if ((arena.Word((ushort)(objectRef + 0x02)) & 0x0001) == 0)      // image@0x08DCB
            {
                continue;
            }

            ushort block = arena.EngagementBlockRef(objectRef);              // image@0x08DD2
            if (arena.Covers(block, 0x0E) && arena.Byte((ushort)(block + 0x0D)) == 6)
            {
                continue;                                                    // image@0x08DDB
            }

            if (arena.Word(objectRef) == CraterClassRecord)                  // image@0x08DE5
            {
                continue;
            }

            // image@0x08DF4 — rep movsw CX=6 from the object's +0x06: the 12-byte XYZ triple.
            CombatObjectView view = new Combat.CombatObjectView(arena, objectRef);
            CombatPosition position = view.Position;
            return (position.X, position.Y, position.Z);
        }

        return null;                                                         // image@0x08E4B-2
    }

    /// <summary>
    /// <c>g_class_record_crater [0x52A2]</c> — the class pointer a killed object's <c>+0x00</c>
    /// carries after <c>engagement_kill_finalize @image@0x0C36B</c>.
    /// </summary>
    public const ushort CraterClassRecord = 0x52A2;

    /// <summary>
    /// <c>nav_waypoint_show_current @image@0x08D31</c> — the label the two thunks post.
    /// </summary>
    /// <returns>The formatted text, or null when the selected slot has no record.</returns>
    public string? ShowCurrent() =>
        Current is { } waypoint ? Text(_strings, CurrentIndex, waypoint.Label) : null;

    /// <summary>
    /// The cockpit label — <c>printf(format, index + 1, record[+0x0D])</c> with the format string at
    /// DGROUP <c>[0x0F9E]</c> (<c>image@0x3CCFE</c>).
    /// </summary>
    /// <param name="strings">The in-flight words, for the label's format.</param>
    /// <param name="index">The zero-based slot; the printed number is <c>index + 1</c>.</param>
    /// <param name="name">The record's name field.</param>
    /// <remarks>
    /// The name is truncated to <see cref="RecordNameBytes"/> − 1 because
    /// <c>nav_slot_record_register</c> copies it into a <c>char[31]</c> field
    /// (<c>strcpy_near</c> at <c>image@0x08D0B</c>, the next record starting at <c>+0x2C</c>).
    /// </remarks>
    public static string Text(Model.Cockpit.InFlightStrings strings, int index, string name)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(name);
        string field = name.Length > RecordNameBytes - 1
            ? name[..(RecordNameBytes - 1)]
            : name;
        return Primitives.PrintfFormat.Format(strings.NavLabelFormat, index + 1, field);
    }
}
