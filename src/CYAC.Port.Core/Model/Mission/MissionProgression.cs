using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The campaign's save state: one progress byte per scenario record, and the gate that decides which
/// missions the picker will let you fly.  The original is <c>g_mission_unlock_array [0xEF50]</c>,
/// 50 bytes, mirrored to <c>yeager.cfg@0x24..0x55</c>.
/// </summary>
/// <remarks>
/// <para>
/// The system stayed invisible for a hundred rounds because the shipped <c>sources/yeager.cfg</c> is <b>all
/// 0x02</b> — a completed save, which makes the gate an unconditional pass.
/// </para>
/// <list type="bullet">
/// <item><description><b>Gate</b> — <c>scenario_record_unlock_gate @image@0x246DE</c>:
/// <c>arr[i] &gt;= 2</c> (<c>image@0x246E0</c>) <c>||</c> (<c>i &gt;= 1</c> — the
/// <c>cmp ax,1 / jl</c> at <c>image@0x246E7</c> — <c>&amp;&amp; arr[i-1] &gt;= 2</c>,
/// <c>image@0x246EC</c>).  See <see cref="IsUnlocked(int)"/>.</description></item>
/// <item><description><b>Writer</b> — <c>ui_post_death_message @image@0x25BAF</c>:
/// <c>arr[g_record_index_u8]++</c> with a clamp to 2 (<c>inc byte [si]</c> @<c>image@0x25C8D</c>,
/// <c>mov byte [si],2</c> @<c>image@0x25C94</c>).  See <see cref="RecordMissionEnd(int)"/>.</description></item>
/// <item><description><b>Fresh-install defaults</b> — <c>mission_unlock_memset_defaults @image@0x2D809</c>:
/// zero all 50, then 2 at records 0 / 17 / 33 = the first mission of each era.  See
/// <see cref="FreshInstall"/>.</description></item>
/// </list>
/// <para>
/// INT-only.  Indices are scenario <b>record indices</b> (the record's own <c>+0x00</c> byte), which is why they
/// must stay inside 0..49: the array's 51st byte is <c>g_record_filename_buf [0xEF82]</c>, so an out-of-range
/// index would have the mission-end writer increment the first character of the active <c>.S</c> filename
/// (<c>ScenarioBinDecoder.ValidateRecordIndex</c>).
/// </para>
/// </remarks>
[OriginalGlobal("g_mission_unlock_array")]
public sealed class MissionProgression
{
    private readonly byte[] _slots;

    /// <summary>Slots in the array: 50 — <c>memset([0xEF50], 0, 0x32)</c> @<c>image@0x2D809</c>.</summary>
    public const int SlotCount = MissionCatalog.ShippedRecordCount;

    /// <summary>
    /// The value the gate compares against: 2 (<c>cmp byte [bx+0xEF50], 2</c> / <c>jae</c>).  It is
    /// also the writer's clamp, so 2 is both "unlocked" and "completed".
    /// </summary>
    public const byte UnlockThreshold = 2;

    /// <summary>
    /// The record indices a fresh install unlocks outright: the first mission of each era.
    /// <c>image@0x2D809</c> writes 2 at <c>+0x00</c>, <c>+0x11</c> and <c>+0x21</c>, and the scanner
    /// names the latter two <c>g_mission_unlock_korea_base</c> / <c>g_mission_unlock_vietnam_base</c>.
    /// </summary>
    public static IReadOnlyList<int> EraFirstRecordIndices { get; } = [0, 17, 33];

    /// <summary>Creates an all-zero progression: nothing flown, nothing unlocked.</summary>
    public MissionProgression() => _slots = new byte[SlotCount];

    private MissionProgression(byte[] slots) => _slots = slots;

    /// <summary>
    /// The state <c>mission_unlock_memset_defaults @image@0x2D809</c> writes when the config is reset
    /// or missing: zeros everywhere except the three era-opening records.
    /// </summary>
    public static MissionProgression FreshInstall()
    {
        MissionProgression progression = new MissionProgression();
        foreach (int index in EraFirstRecordIndices)
        {
            progression._slots[index] = UnlockThreshold;
        }

        return progression;
    }

    /// <summary>Reads the 50 bytes as they sit in <c>yeager.cfg@0x24</c> (or in DGROUP).</summary>
    /// <param name="slots">Exactly <see cref="SlotCount"/> bytes.</param>
    /// <exception cref="ArgumentException">The span is not 50 bytes long.</exception>
    public static MissionProgression FromBytes(ReadOnlySpan<byte> slots)
    {
        if (slots.Length != SlotCount)
        {
            throw new ArgumentException(
                $"the mission-unlock array is {SlotCount} bytes; got {slots.Length}.", nameof(slots));
        }

        return new MissionProgression(slots.ToArray());
    }

    /// <summary>The raw progress byte of one record.</summary>
    /// <param name="recordIndex">A scenario record index, 0..49.</param>
    public byte this[int recordIndex]
    {
        get
        {
            ThrowIfOutOfRange(recordIndex);
            return _slots[recordIndex];
        }

        set
        {
            ThrowIfOutOfRange(recordIndex);
            _slots[recordIndex] = value;
        }
    }

    /// <summary>
    /// The picker's availability gate, exactly as <c>scenario_record_unlock_gate @image@0x246DE</c>
    /// computes it: this record's own byte reached the threshold, or — for any record but the first —
    /// the record before it did.
    /// </summary>
    /// <remarks>
    /// The <c>i &gt;= 1</c> arm matters: the original's <c>cmp ax,1 / jl</c> (<c>image@0x246E7</c>)
    /// returns 0 for record 0 <b>before</b> the <c>[bx+0xEF4F]</c> load can happen, so the byte in
    /// front of the array is never actually read.  (*.c</c> Notes §A says the opposite in prose while
    /// its own C body has it right — reported.)
    /// </remarks>
    /// <param name="recordIndex">A scenario record index, 0..49.</param>
    public bool IsUnlocked(int recordIndex)
    {
        ThrowIfOutOfRange(recordIndex);
        return _slots[recordIndex] >= UnlockThreshold
            || (recordIndex >= 1 && _slots[recordIndex - 1] >= UnlockThreshold);
    }

    /// <summary>Every record index <see cref="IsUnlocked(int)"/> accepts, ascending.</summary>
    public IEnumerable<int> UnlockedRecordIndices
    {
        get
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (IsUnlocked(i))
                {
                    yield return i;
                }
            }
        }
    }

    /// <summary>
    /// The mission-end writer (<c>ui_post_death_message @image@0x25BAF</c>): bump this record's byte
    /// by one and clamp it at <see cref="UnlockThreshold"/>.
    /// </summary>
    /// <remarks>
    /// The original runs it on the way out of <i>any</i> ended mission — the increment is not
    /// conditional on success at this site, so "1" means attempted and "2" means the record (and the
    /// next one) are open.
    /// </remarks>
    /// <param name="recordIndex">The record just flown, 0..49.</param>
    public void RecordMissionEnd(int recordIndex)
    {
        ThrowIfOutOfRange(recordIndex);
        if (_slots[recordIndex] < UnlockThreshold)
        {
            _slots[recordIndex]++;
        }
        else
        {
            _slots[recordIndex] = UnlockThreshold;
        }
    }

    /// <summary>True when every record has reached the threshold — what the shipped cfg looks like.</summary>
    public bool IsCompletedSave => Array.TrueForAll(_slots, b => b >= UnlockThreshold);

    /// <summary>The 50 bytes, ready to write at <c>cfg@0x24</c>.</summary>
    public byte[] ToBytes() => _slots.ToArray();

    /// <summary>Copies the 50 bytes into <paramref name="destination"/>.</summary>
    /// <param name="destination">A span of at least <see cref="SlotCount"/> bytes.</param>
    public void CopyTo(Span<byte> destination) => _slots.CopyTo(destination);

    /// <summary>A deep copy.</summary>
    public MissionProgression Clone() => new(_slots.ToArray());

    /// <summary>
    /// Why <paramref name="recordIndex"/> may not be used as a record index, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <c>ScenarioBinDecoder.ValidateRecordIndex</c> lived in
    /// <c>CYAC.Formats</c>, which the data-tree rule retires from the runtime.  The message keeps the fact that
    /// made it worth having: the array this index keys is fixed at 50 slots (<c>g_mission_unlock_array
    /// [0xEF50]</c>, cfg@0x24..0x55), so an out-of-range index writes over whatever follows it in
    /// DGROUP — the engine's own capacity wall.
    /// </remarks>
    /// <param name="recordIndex">The candidate record index.</param>
    public static string? ValidateRecordIndex(int recordIndex) =>
        recordIndex is >= 0 and < SlotCount
            ? null
            : $"record_index_u8 = {recordIndex} is outside 0..{SlotCount - 1}: the engine uses it to " +
              $"index the {SlotCount}-byte per-mission progress array at [0xEF50] and WRITES through " +
              $"it at image@0x25C8D — index {SlotCount} would increment g_record_filename_buf[0] " +
              "([0xEF82]). Reuse an index 0..49 (e.g. the template mission's).";

    private static void ThrowIfOutOfRange(int recordIndex)
    {
        if (recordIndex is < 0 or >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordIndex), recordIndex, ValidateRecordIndex(recordIndex));
        }
    }
}
