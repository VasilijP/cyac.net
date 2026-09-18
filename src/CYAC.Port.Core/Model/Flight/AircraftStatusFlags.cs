namespace CYAC.Port.Core.Model.Flight;

/// <summary>
/// The aircraft status byte at master <c>+0x124</c> (<c>status_flags_u8</c>).
/// </summary>
/// <remarks>
/// <para>
/// INT-only: this byte gates drag, ground handling, ejection and crash logic, so it is part of the reproducible
/// spine.
/// </para>
/// <para>
/// Source of truth: KNOWN_FIELDS["s_aircraft_master"][0x124]</c> — bit 0 afterburner (P33), bit 1 landing gear
/// (closes P28-H1), bit 2 ground contact (<c>image@0x2A2E5</c>/<c>image@0x2A2F7</c>), bit 3 airbrake (P45), bit 4
/// crash confirmed (P39), bit 7 ground bounce (<c>XOR 0x80</c> @<c>image@0x2BE0E</c>).
/// </para>
/// <para>
/// <b>the bit-1 / bit-2 names below are WRONG, and the fix is a rename with real consequences, so it is REPORTED, not
/// applied</b>. The three cockpit-control keys each toggle one bit, each behind its own damage interlock, and the
/// manual names the keys:
/// <list type="bullet">
/// <item>key <b>B</b> — <c>xor byte [bx+0x124],8</c> @<c>image@0x2A49F</c>, blocked by
/// <c>damage_flags</c> 0x20 — "Press B to set your air brakes or wheel brakes" (manual, "Gear, Brakes, and Flaps", p. 44).</item>
/// <item>key <b>F</b> — <c>xor byte [bx+0x124],2</c> @<c>image@0x2A4C4</c>, blocked by
/// <c>damage_flags</c> 0x10 — "Press F to raise or lower flaps" (manual, "Gear, Brakes, and Flaps", p. 44).  So <b>bit 1 is
/// FLAPS</b>, not the landing gear.</item>
/// <item>key <b>G</b> — <c>xor byte [bx+0x124],4</c> @<c>image@0x2A4E9</c>, blocked by
/// <c>damage_flags</c> 0x08 <i>and</i> gated on bit 5 @<c>image@0x2A4E2</c> — "Press G to raise or
/// lower landing gear" (manual, "Gear, Brakes, and Flaps", p. 44).  So <b>bit 2 is the LANDING GEAR</b>, not ground contact:
/// <c>flight_model_load_for_aircraft</c> ORs it in at load (gear down), <c>aircraft_pose_set</c>
/// sets it for a runway spawn and clears it for an air spawn, and
/// <c>crash_conditions_valid @image@0x2C25F</c> refuses a survivable landing without it — "The red
/// light indicates the landing gear is down, enabling safe landing" (manual, "Landing Gear Indicator").</item>
/// </list>
/// That also makes <c>vel_roll_aoa_correction_accum</c>'s phase D the three-term FLAPS (<c>image@0x2AD93</c>, +0x100)
/// / GEAR (<c>image@0x2AD81</c>, +0xFE) / BRAKE (<c>image@0x2AD9E</c>, +0xFA) drag sum, and the interlocks the
/// manual's "a loss of hydraulic pressure prevents you from dropping flaps, air brakes, and landing gear" (manual, "a
/// loss of hydraulic pressure"). The port is bit-exact either way — 155,212 frames — so this was a NAMING
/// correction only. **APPLIED R1** (`` §2): bit 1 <c>LandingGear</c> → <c>Flaps</c>, bit 2 <c>GroundContact</c> →
/// <c>LandingGear</c>, bit 5 <c>Unknown5</c> → <c>HasRetractableGear</c>, bit 7 <c>GroundBounce</c> →
/// <c>PoleCrossingLatch</c>, and <c>Aircraft.IsOnOrNearGround</c> → <c>Aircraft.IsGearDown</c>.
/// <c>CockpitKeyArm</c>/<c>CockpitAudioCue</c> keep their own <c>LandingGear</c> members: those name the <b>G
/// key</b>, which really does operate the gear.
/// </para>
/// <para>
/// Both have readers: bit 5 gates the G key itself (<c>test byte [bx+0x124],0x20</c> @<c>image@0x2A4E2</c>) and so
/// reads as "this aircraft has retractable gear"; bit 6 has two (<c>image@0x2B36E</c> in the control integrator's
/// roll arm, <c>image@0x0FACA</c> in a cockpit path) and is set on the four jets only — "WW II era airplanes do not
/// have air brakes" (manual, "Gear, Brakes, and Flaps", p. 44) makes "has air brakes" the obvious reading, but
/// neither reader proves it, so it stays <b>(open)</b>. — but they are not dead: every shipped <c>.fmd</c> carries
/// <c>0x20</c> (props) or <c>0x60</c> (jets) at <c>+0x124</c>, so bit 5 is set on all six aircraft and bit 6 exactly
/// on the four jets.  The round-13b map reads the same two bytes as a <c>prop_count_u16</c> (32 / 96); the port keeps
/// the status-byte reading — it is the one with runtime evidence (the loader ORs <c>0x04</c> into this byte at
/// <c>image@0x2A190</c> and every other access is a bit test).
/// </para>
/// </remarks>
[Flags]
public enum AircraftStatusFlags : byte
{
    /// <summary>No flags set.</summary>
    None = 0x00,

    /// <summary>
    /// bit 0 — afterburner active.  Auto-clears when the throttle target drops below <c>0x6400</c>
    /// (P33); gates the <see cref="Aircraft.AfterburnerScale"/> read in the yaw drive (P44).
    /// </summary>
    Afterburner = 0x01,

    /// <summary>
    /// bit 1 — landing gear extended.  Costs a 25 % drag penalty in
    /// <c>aircraft_fme_speed_interpolate @0x2A7FA</c> and gates drag in
    /// <c>vel_roll_aoa_correction_accum @0x2ACE2</c> (P45).  Toggled by key 'f'
    /// (<c>image@0x2A4C4</c>).
    /// </summary>
    Flaps = 0x02,

    /// <summary>
    /// bit 2 — ground contact.  + parent bytes: SET means ON/NEAR GROUND — <c>aircraft_pose_set</c> ORs it in when
    /// <c>airspeed_a + airspeed_b &gt;= playerObject.pos_y</c> @<c>image@0x2A2D3..0x2A2E5</c> and clears it above
    /// that margin @<c>0x2A2F7</c>; the 'g' key toggles it @<c>0x2A4E9</c>; no per-frame writer; CLEAR on all 86,287
    /// airborne frames: <c>aircraft_pose_set</c> ORs it in on the in-air branch (<c>image@0x2A2E5</c>) and
    /// clears it on the runway branch (<c>image@0x2A2F7</c>), and <c>flight_model_load_for_aircraft</c> ORs it in at
    /// load (<c>image@0x2A190</c>).
    /// </summary>
    LandingGear = 0x04,

    /// <summary>bit 3 — airbrake active (P45); toggled by key 'b' (<c>xor byte [bx+0x124],8</c> @<c>image@0x2A49F</c>).</summary>
    Airbrake = 0x08,

    /// <summary>bit 4 — crash confirmed; tested to skip callee validation once set (P39).</summary>
    CrashConfirmed = 0x10,

    /// <summary>
    /// bit 5 — no decoded reader.  Set in all six shipped <c>.fmd</c> files (props <c>0x20</c>, jets
    /// <c>0x60</c>), so it is authored-on for every aircraft.
    /// </summary>
    HasRetractableGear = 0x20,

    /// <summary>
    /// bit 6 — no decoded reader.  Set in the four jet <c>.fmd</c> files only (<c>0x60</c>) — the
    /// only per-aircraft distinction this byte carries.
    /// </summary>
    Unknown6 = 0x40,

    /// <summary>
    /// bit 7 — the POLE-CROSSING latch.  <c>aircraft_physics_apply_velocity</c> phase 5 XORs it
    /// (<c>image@0x2BE0E</c>) in the same breath as negating the pitch accumulator and adding
    /// <c>0xB400</c> = 180° to roll and heading, i.e. when the pitch accumulator wraps past ±90°: it
    /// is "the aircraft is inverted", not a bounce.
    /// <para>
    /// It is NOT write-only, and the reader is outside the flight kernel:
    /// <c>view_orbit_flip_own_object_gate @image@0x22B5D</c> answers AL=1 iff
    /// <c>g_engagement_mode_flag [0xF28A] == 0</c>, the object being drawn IS the player (<c>BX ==
    /// [0x00C0]</c>) and this bit is set — and <c>view_target_angles_fetch @0x22B78</c> then
    /// reflects the camera's Euler triple.  That reader addresses the byte ABSOLUTELY, as
    /// <c>[0xF0BC]</c> — which is <c>g_aircraft_master_struct [0xEF98] + 0x124</c>, this very field;
    /// Nothing inside <c>aircraft_per_frame_update</c>'s closure tests it, which is why the 12,211
    /// frames flown with it set verify bit-exactly.
    /// </para>
    /// </summary>
    PoleCrossingLatch = 0x80,
}

/// <summary>
/// The damage accumulator byte at master <c>+0x123</c> (<c>damage_flags_u8</c>).
/// </summary>
/// <remarks>
/// INT-only.  <c>aircraft_master_damage_apply2 @0x2A4F8</c> ORs the damage-type bit in
/// unconditionally (<c>image@0x2A504</c>) and then dispatches on it;
/// </remarks>
[Flags]
public enum AircraftDamageFlags : byte
{
    /// <summary>Undamaged — what the loader writes at <c>image@0x2A195</c>.</summary>
    None = 0x00,

    /// <summary>0x01 — hit points: subtracts a percentage from <see cref="Aircraft.HitPoints"/> and clamps the speed cap.</summary>
    HitPoints = 0x01,

    /// <summary>0x02 — roll authority: <i>adds</i> a percentage to <see cref="Aircraft.RollAuthorityGain"/> (<c>image@0x2A54D</c>).</summary>
    RollAuthority = 0x02,

    /// <summary>0x04 — flight model: subtracts from the roll-dead hi bound and mirrors its negation into the lo bound (<c>image@0x2A566</c>).</summary>
    FlightModel = 0x04,

    /// <summary>0x08 — flag-only: the bit is recorded, no numeric field changes.</summary>
    FlagOnly08 = 0x08,

    /// <summary>0x10 — flag-only: the bit is recorded, no numeric field changes.</summary>
    FlagOnly10 = 0x10,

    /// <summary>0x20 — flag-only: the bit is recorded, no numeric field changes.</summary>
    FlagOnly20 = 0x20,

    /// <summary>0x40 — hull: subtracts a percentage from <see cref="Aircraft.ElevatorAuthority"/> (<c>image@0x2A592</c>).</summary>
    Hull = 0x40,

    /// <summary>0x80 — never passed by any decoded caller <b>(open)</b>; represented so a byte round-trips.</summary>
    Unknown7 = 0x80,
}
