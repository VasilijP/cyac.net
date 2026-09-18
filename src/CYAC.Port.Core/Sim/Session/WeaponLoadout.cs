using CYAC.Port.Core.Sim.Combat;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// <c>player_weapon_loadout_publish @image@0x27616</c> — the routine that puts the player's WEAPONS
/// in his hands.
/// </summary>
/// <remarks>
/// <para>
/// Without it <c>[0xED1E] g_hud_current_weapon_name_ptr</c> is 0, and that is the fifth gate of the
/// player fire chain (<c>WeaponFireScheduler</c> §5): no weapon selected, no shot, ever.  It is the
/// Armament screen's commit, and a cold start has to run it because the port has no Armament screen.
/// </para>
/// <code>
/// 27620  bx := 0x43FE + 0x12 * [0xC31A]     ; an 18-byte per-aircraft LOADOUT record
/// 2762D  [0xED32] := bx[+0] ; [0xED33] := bx[+2]
/// 27643  memset([0xED24], 0, 6)              ; three weapon-class slots
/// 2764B  di := bx + 6                        ; the per-slot AMMO, stride 4
/// 27659  for bx = 0, 2, 4:                   ; `cmp bx,6 / jl` — THREE slots, no more
///          if [0xEF30 + bx] == 0: break      ; the PLAYER PROTOTYPE's +0x0E pointer array
///          if [0xF0E5] != 0:                 ; the mission-reset flag
///            [0xED24 + bx] := [0xEF30 + bx]  ; the slot's weapon-class descriptor
///            [0xED2C + bx] := [di]           ; …and its rounds
/// 27680  [0xED22] := the slot count
/// 27684  if arg: [0xED2A] := bx[+4]          ; the DEFAULT selected slot
/// 27693  weapon_slot_apply @image@0x033F8    ; [0xED1E] := [0xED24 + 2*[0xED2A]]
/// </code>
/// <para>
/// Shipped values: the P-51 gets one slot with 2,400 rounds; the F-4 gets three
/// (4 + 4 + 750) and starts on slot 2, the gun.  The pointer array is the player's own runtime
/// prototype <c>g_record_aircraft_data [0xEF22]</c>, which
/// <see cref="CombatColdStart"/> copies out of <c>[0x0FBC + 2·idx]</c>.
/// </para>
/// </remarks>
public static class WeaponLoadout
{
    /// <summary>The per-aircraft loadout table's DGROUP base (<c>add bx,0x43FE</c> @<c>image@0x27626</c>).</summary>
    public const int LoadoutTable = 0x43FE;

    /// <summary>One record's stride: 18 bytes (<c>imul 0x12</c> @<c>image@0x2761D</c>).</summary>
    public const int LoadoutStride = 0x12;

    /// <summary>The player prototype's weapon-class pointer array — <c>[0xEF22] + 0x0E</c>.</summary>
    public const int PrototypeWeaponClasses = 0xEF30;

    /// <summary>The published per-slot weapon-class descriptors, <c>u16[3]</c>.</summary>
    public const int SlotWeaponClasses = 0xED24;

    /// <summary>The published per-slot rounds, <c>u16[3]</c>.</summary>
    public const int SlotAmmo = 0xED2C;

    /// <summary>How many slots were published.</summary>
    public const int SlotCount = 0xED22;

    /// <summary>The selected slot index.</summary>
    public const int SelectedSlot = 0xED2A;

    /// <summary>The selected slot's weapon-class descriptor — the fire chain's fifth gate.</summary>
    public const int SelectedWeaponClass = 0xED1E;

    /// <summary>The most slots the loop can publish: 3 (<c>cmp bx,6 / jl</c> @<c>image@0x2767B</c>).</summary>
    public const int MaximumSlots = 3;

    /// <summary><c>g_scene_flag_F0E5 [0xF0E5]</c> — set by the mission reset, gates the publish.</summary>
    public const int MissionResetFlag = 0xF0E5;

    /// <summary>Publishes the loadout for the active aircraft.</summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="staticData">The constant DGROUP surface.</param>
    /// <param name="aircraftIndex">The Hangar slot 0..5 (<c>[0xC31A]</c>).</param>
    /// <param name="selectDefaultSlot">The original's <c>[bp+6]</c>: reset the selection too.</param>
    /// <returns>How many slots were published.</returns>
    public static int Publish(
        CombatRegisters registers,
        ICombatStaticData staticData,
        int aircraftIndex,
        bool selectDefaultSlot = true)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(staticData);

        int record = LoadoutTable + (LoadoutStride * aircraftIndex);        // image@0x27626
        registers.SetByte(0xED32, staticData.Byte(record));                 // image@0x2762F
        registers.SetByte(0xED33, staticData.Byte(record + 2));             // image@0x27635
        registers.Span(SlotWeaponClasses, 6).Clear();                       // image@0x27643

        int slots = 0;
        for (int i = 0; i < MaximumSlots; i++)                              // image@0x2767B
        {
            ushort weaponClass = registers.Word(PrototypeWeaponClasses + (i * 2));
            if (weaponClass == 0)                                           // image@0x27659
            {
                break;
            }

            if (registers.Byte(MissionResetFlag) != 0)                      // image@0x27660
            {
                registers.SetWord(SlotWeaponClasses + (i * 2), weaponClass); // image@0x2766B
                registers.SetWord(
                    SlotAmmo + (i * 2), staticData.Word(record + 6 + (i * 4))); // image@0x27671
            }

            slots++;
        }

        registers.SetWord(SlotCount, (ushort)slots);                        // image@0x27680
        if (selectDefaultSlot)                                              // image@0x27684
        {
            registers.SetWord(SelectedSlot, staticData.Word(record + 4));   // image@0x27690
        }

        Apply(registers);                                                   // image@0x27693
        return slots;
    }

    /// <summary>
    /// <c>weapon_slot_apply @image@0x033F8</c> — <c>[0xED1E] := [0xED24 + 2·[0xED2A]]</c>.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    public static void Apply(CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        int slot = registers.Word(SelectedSlot);
        registers.SetWord(
            SelectedWeaponClass,
            slot >= 0 && slot < MaximumSlots
                ? registers.Word(SlotWeaponClasses + (slot * 2))
                : (ushort)0);
    }

    /// <summary>
    /// <c>weapon_slot_prev @image@0x03406</c> — the ladder's <c>image@0x011F4</c> arm.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    public static void SelectPrevious(CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        int slot = unchecked((short)registers.Word(SelectedSlot)) - 1;      // image@0x03406
        if (slot < 0)                                                       // image@0x0340A jns
        {
            slot = registers.Word(SlotCount) - 1;                           // image@0x0340F
        }

        registers.SetWord(SelectedSlot, unchecked((ushort)slot));
        Apply(registers);
    }

    /// <summary>
    /// <c>weapon_slot_next @image@0x03419</c> — the ladder's <c>image@0x0120B</c> arm.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    public static void SelectNext(CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        int count = unchecked((short)registers.Word(SlotCount));            // image@0x03419
        int slot = unchecked((short)registers.Word(SelectedSlot)) + 1;      // image@0x0341C
        if (slot >= count)                                                  // image@0x03420 jl
        {
            slot = 0;                                                       // image@0x03426
        }

        registers.SetWord(SelectedSlot, unchecked((ushort)slot));
        Apply(registers);
    }
}
