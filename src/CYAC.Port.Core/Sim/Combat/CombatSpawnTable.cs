using System.Buffers.Binary;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// The 30-slot projectile table — <c>g_combat_spawn_table [0xB16A]</c>, 30 × <c>0x1B</c> bytes,
/// with the two live-shot counters that bracket it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Extent, byte-proven.</b>  <c>combat_spawn_slot_count @image@0x02687</c> walks
/// <c>si = 0xB479</c> down to <c>0xB16A</c> in steps of <c>0x1B</c>
/// (<c>mov si,0xb479</c> @<c>image@0x0268E</c>, <c>sub si,0x1b</c> @<c>image@0x026A0</c>,
/// <c>cmp si,0xb16a / jae</c> @<c>image@0x026A3</c>) — so the table is
/// <c>[0xB16A, 0xB16A + 30 × 0x1B) = [0xB16A, 0xB494)</c>, exactly 30 slots.
/// </para>
/// <para>
/// <b>The per-frame iteration order is slot 29 DOWN to slot 0</b>, not 0 up.  It matters: the order
/// fixes the RNG draw order inside <c>combat_object_tick</c>'s subtree, which an earlier pass measured as one of
/// only two stage transitions that draw at all.
/// </para>
/// <para>
/// <b>A slot is ACTIVE when its first word is non-zero</b> (<c>cmp word ptr [si],0 / je</c>
/// @<c>image@0x02693</c>) — that word is <see cref="CombatSpawn.WeaponClassId"/>.  The walk counts
/// the active ones into <c>g_combat_spawn_active_count [0xED20]</c>.
/// </para>
/// <para>
/// <b>"Max 15 per faction", byte-proven.</b>  <c>combat_spawn_slot_alloc_and_film_record
/// @image@0x02423</c> first decides whether the shot is the PLAYER's by comparing the owner against
/// <c>g_alt_object_farptr [0x00C0]</c> (<c>image@0x02432</c>), then bumps one of two counters and
/// refuses the shot when it is already at 15:
/// <c>cmp word ptr [0xb494],0xf / jl → inc</c> (<c>image@0x0245E</c>, the PLAYER's) and
/// <c>cmp word ptr [0xb168],0xf / jge → fail</c> (<c>image@0x0248A</c>, the AI's).  Both counters
/// sit immediately outside the table: <c>[0xB168]</c> is <c>base − 2</c> and <c>[0xB494]</c> is
/// <c>base + 810</c>.
/// </para>
/// <para>
/// Field class: INT-only spine.
/// </para>
/// </remarks>
public sealed class CombatSpawnTable
{
    /// <summary>Slots in the table: 30.</summary>
    public const int SlotCount = CombatSpawn.SlotCount;

    /// <summary>Bytes per slot: <c>0x1B</c> = 27.</summary>
    public const int SlotBytes = CombatSpawn.SlotBytes;

    /// <summary>The table's DGROUP offset.</summary>
    public const int TableDgroupOffset = 0xB16A;

    /// <summary>The AI's live-shot counter, <c>[0xB168]</c> — the table's base minus 2.</summary>
    public const int AiLiveShotCountDgroupOffset = 0xB168;

    /// <summary>The player's live-shot counter, <c>[0xB494]</c> — just past the table.</summary>
    public const int PlayerLiveShotCountDgroupOffset = 0xB494;

    /// <summary>The per-faction cap: 15 live shots.</summary>
    public const int MaxLiveShotsPerFaction = 15;

    private readonly CombatSpawn?[] _slots = new CombatSpawn?[SlotCount];

    /// <summary>Live shots the AI owns — <c>[0xB168]</c>.</summary>
    public int AiLiveShots { get; set; }

    /// <summary>Live shots the player owns — <c>[0xB494]</c>.</summary>
    public int PlayerLiveShots { get; set; }

    /// <summary>
    /// <c>g_combat_spawn_active_count [0xED20]</c> — how many slots the last per-frame walk found
    /// active.  It is an OUTPUT of the walk, not an input.
    /// </summary>
    public int ActiveCount { get; private set; }

    /// <summary>The slot at an index, or null when it is free.</summary>
    /// <param name="index">0..29.</param>
    public CombatSpawn? this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SlotCount);
            return _slots[index];
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SlotCount);
            _slots[index] = value;
        }
    }

    /// <summary>
    /// The active slots in the original's per-frame order: index 29 first, index 0 last.
    /// </summary>
    /// <remarks>See the type remarks — the order is <c>combat_spawn_slot_count</c>'s, and it is
    /// RNG-order-significant.</remarks>
    public IEnumerable<CombatSpawn> ActiveSlotsInTickOrder()
    {
        for (int i = SlotCount - 1; i >= 0; i--)
        {
            if (_slots[i] is { WeaponClassId: not 0 } slot)
            {
                yield return slot;
            }
        }
    }

    /// <summary>
    /// Recounts the active slots into <see cref="ActiveCount"/>, exactly as the per-frame walk does.
    /// </summary>
    /// <returns>The new <see cref="ActiveCount"/>.</returns>
    public int RecountActive()
    {
        int count = 0;
        foreach (CombatSpawn _ in ActiveSlotsInTickOrder())
        {
            count++;
        }

        ActiveCount = count;
        return count;
    }

    /// <summary>
    /// Whether a faction may fire another shot — the two <c>0x0F</c> gates of
    /// <c>combat_spawn_slot_alloc_and_film_record</c>.
    /// </summary>
    /// <param name="isPlayerShot">True for the player's own shot.</param>
    /// <remarks>
    /// The two gates are NOT written the same way in the original: the player's is
    /// <c>cmp …,0xf / jl</c> (allow when strictly below 15) and the AI's is
    /// <c>cmp …,0xf / jge</c> → fail (allow when strictly below 15).  Same predicate, opposite
    /// branch polarity — reproduced here as one method on purpose, with the note that they agree.
    /// </remarks>
    public bool CanFire(bool isPlayerShot) =>
        (isPlayerShot ? PlayerLiveShots : AiLiveShots) < MaxLiveShotsPerFaction;
}

/// <summary>
/// Decodes and encodes <c>s_combat_spawn_record</c> — the 27-byte projectile slot.
/// </summary>
/// <remarks>
/// Object references are near offsets into the pool arena, so the codec exposes them as raw
/// <see cref="ushort"/> and leaves the resolution to a caller that has the arena.
/// </remarks>
public static class CombatSpawnCodec
{
    /// <summary>Bytes in one record.</summary>
    public const int RecordBytes = CombatSpawn.SlotBytes;

    /// <summary>The raw near-pointer fields of one record, unresolved.</summary>
    /// <param name="WeaponClassRef"><c>+0x00</c> — the weapon-class descriptor, 0 = slot free.</param>
    /// <param name="PoolObjectRef"><c>+0x02</c> — the projectile's own pool object.</param>
    /// <param name="OwnerOrSlot1"><c>+0x04</c>.</param>
    /// <param name="OwnerId"><c>+0x06</c>.</param>
    /// <param name="TargetRef"><c>+0x08</c>.</param>
    /// <param name="TargetArgumentRef"><c>+0x0A</c>.</param>
    /// <param name="SpeedQ8"><c>+0x0C</c>, i32.</param>
    /// <param name="TargetScoreGateFrame"><c>+0x10</c>.</param>
    /// <param name="BoostEndFrame"><c>+0x12</c>.</param>
    /// <param name="ExpireFrame"><c>+0x14</c>.</param>
    /// <param name="NextEligibilityCheck"><c>+0x16</c>, i32.</param>
    /// <param name="StatusFlags"><c>+0x1A</c>.</param>
    public readonly record struct RawSpawn(
        ushort WeaponClassRef,
        ushort PoolObjectRef,
        ushort OwnerOrSlot1,
        ushort OwnerId,
        ushort TargetRef,
        ushort TargetArgumentRef,
        int SpeedQ8,
        ushort TargetScoreGateFrame,
        ushort BoostEndFrame,
        ushort ExpireFrame,
        int NextEligibilityCheck,
        byte StatusFlags)
    {
        /// <summary>True when the slot is in use — <c>cmp word ptr [si],0</c> @<c>image@0x02693</c>.</summary>
        public bool IsActive => WeaponClassRef != 0;
    }

    /// <summary>Reads one slot.</summary>
    /// <param name="source">At least <see cref="RecordBytes"/> bytes.</param>
    public static RawSpawn Decode(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadUInt16LittleEndian(source),
        BinaryPrimitives.ReadUInt16LittleEndian(source[0x02..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[0x04..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[0x06..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[0x08..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[0x0A..]),
        BinaryPrimitives.ReadInt32LittleEndian(source[0x0C..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[0x10..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[0x12..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[0x14..]),
        BinaryPrimitives.ReadInt32LittleEndian(source[0x16..]),
        source[0x1A]);

    /// <summary>Writes one slot.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="destination">At least <see cref="RecordBytes"/> bytes.</param>
    public static void Encode(RawSpawn slot, Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, slot.WeaponClassRef);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[0x02..], slot.PoolObjectRef);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[0x04..], slot.OwnerOrSlot1);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[0x06..], slot.OwnerId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[0x08..], slot.TargetRef);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[0x0A..], slot.TargetArgumentRef);
        BinaryPrimitives.WriteInt32LittleEndian(destination[0x0C..], slot.SpeedQ8);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[0x10..], slot.TargetScoreGateFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[0x12..], slot.BoostEndFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[0x14..], slot.ExpireFrame);
        BinaryPrimitives.WriteInt32LittleEndian(destination[0x16..], slot.NextEligibilityCheck);
        destination[0x1A] = slot.StatusFlags;
    }
}
