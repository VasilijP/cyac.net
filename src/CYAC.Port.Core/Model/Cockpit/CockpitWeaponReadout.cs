using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Model.Cockpit;

/// <summary>
/// H10a fix pass — what the cockpit's WEAPON + AMMO region prints: the selected slot's name and its
/// rounds, out of <c>exe/weapons.json</c>.
/// </summary>
/// <param name="Name">
/// The weapon's name with its padding trimmed, or the empty weapon name
/// (<see cref="InFlightStrings.EmptyWeaponName"/>) for an empty slot.
/// </param>
/// <param name="Rounds">Its rounds, or −1 when the aircraft has no weapon at all.</param>
/// <param name="Slot">Which slot it is, 0..2.</param>
/// <param name="PaddedName">
/// The name EXACTLY as the pool holds it, space-padded to four characters.  The panel's region 6
/// prints it through <c>"%4s %4d"</c>, where the padding is invisible, but the HUD overlay prints
/// <c>"%s:%d"</c> (<c>image@0x0C895</c>) and the padding SHOWS: the original's MiG-15 reads
/// <c>"Π37:40"</c>, not <c>"Π37:40"</c> (a captured frame of the original).
/// </param>
public readonly record struct CockpitWeaponReadout(
    string Name, int Rounds, int Slot, string? PaddedName = null)
{
    // InFlightStrings.EmptyWeaponName, DGROUP [0x2E6C].

    /// <summary>The readout of an aircraft that carries nothing: the empty name and no rounds.</summary>
    /// <param name="strings">The in-flight words.</param>
    /// <returns>The readout; <c>Rounds</c> is −1.</returns>
    public static CockpitWeaponReadout Empty(InFlightStrings strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return new CockpitWeaponReadout(strings.EmptyWeaponName, -1, 0);
    }

    /// <summary>
    /// The loadout <c>player_weapon_loadout_publish @image@0x27616</c> publishes for an aircraft.
    /// </summary>
    /// <param name="strings">The in-flight words, for the empty weapon name.</param>
    /// <param name="document">The <c>exe/weapons.json</c> document.</param>
    /// <param name="basename">The aircraft's port basename.</param>
    /// <param name="slot">The selected slot, or −1 for the aircraft's own default.</param>
    /// <returns>The readout; <c>Rounds</c> is −1 when the aircraft carries nothing.</returns>
    /// <remarks>
    /// <para>
    /// The publisher selects the 18-byte record <c>g_per_aircraft_weapon_table [0x43FE] + 0x12·idx</c>
    /// (<c>image@0x2761D</c>), crosses its per-slot rounds with the player prototype's weapon-class
    /// array, publishes at most three slots into <c>[0xED24 + 2i]</c> / <c>[0xED2C + 2i]</c>, and —
    /// when its <c>[bp+6]</c> argument is non-zero — selects <c>record[+0x04]</c>, the aircraft's
    /// DEFAULT slot.  Region 6 then prints <c>[0xED2C + 2·[0xED2A]]</c> beside the slot's name.
    /// </para>
    /// <para>
    /// An earlier pass named those three head words in the document itself — <c>chaffStock</c>,
    /// <c>flareStock</c>, <c>defaultSlot</c> — from <c>image@0x2762D..0x27635</c> (<c>[0xED32] =
    /// record[+0x00]</c>, <c>[0xED33] = record[+0x02]</c>, both published as BYTES), so this no longer
    /// reads them positionally out of a hex blob.
    /// </para>
    /// </remarks>
    public static CockpitWeaponReadout For(
        InFlightStrings strings, WeaponTablesDocumentDto document, string basename, int slot = -1)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(basename);

        foreach (AircraftWeaponRecordDto record in document.PerAircraftWeapons ?? [])
        {
            if (!string.Equals(record.Aircraft, basename, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int selected = slot >= 0 ? slot : record.DefaultSlot;
            List<AircraftWeaponSlotDto> slots = record.Slots ?? [];
            if ((uint)selected >= (uint)slots.Count)
            {
                selected = 0;
            }

            if (slots.Count == 0)
            {
                break;
            }

            AircraftWeaponSlotDto chosen = slots[selected];
            bool empty = string.IsNullOrWhiteSpace(chosen.Name);
            return new CockpitWeaponReadout(
                empty ? strings.EmptyWeaponName : chosen.Name!.TrimEnd(),
                chosen.Ammunition,
                selected,
                empty ? null : chosen.Name);
        }

        return Empty(strings);
    }

    /// <summary>
    /// The aircraft's countermeasure stock — <c>record[+0x00]</c> chaff and <c>record[+0x02]</c>
    /// flare, the two bytes <c>image@0x2762D..0x27635</c> copies into <c>[0xED32]</c>/<c>[0xED33]</c>.
    /// </summary>
    /// <param name="document">The <c>exe/weapons.json</c> document.</param>
    /// <param name="basename">The aircraft's port basename.</param>
    /// <returns>The chaff and flare stocks; <c>(0, 0)</c> for an aircraft that carries none.</returns>
    public static (int Chaff, int Flare) CountermeasureStock(
        WeaponTablesDocumentDto document, string basename)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(basename);

        foreach (AircraftWeaponRecordDto record in document.PerAircraftWeapons ?? [])
        {
            if (string.Equals(record.Aircraft, basename, StringComparison.OrdinalIgnoreCase))
            {
                return (record.ChaffStock, record.FlareStock);
            }
        }

        return (0, 0);
    }
}
