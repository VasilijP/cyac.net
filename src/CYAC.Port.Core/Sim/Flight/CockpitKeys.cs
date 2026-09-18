using CYAC.Port.Core.Model.Flight;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// What one keypress did to the aircraft — the arm <c>cockpit_key_dispatch @image@0x2A3FF</c> took.
/// </summary>
/// <remarks>
/// The arm names are the port's; every one cites the image address of the branch that selects it in
/// <see cref="CockpitKeys"/>.  <see cref="Ignored"/> and the three <c>…Jammed</c> arms are the
/// no-ops: they are named rather than folded together because a no-op is the only outcome a
/// cross-frame byte delta CANNOT observe, so they exist to be unit-tested.
/// </remarks>
public enum CockpitKeyArm
{
    /// <summary>The key is not one of the nine this dispatcher acts on.</summary>
    Ignored,

    /// <summary>Keys <c>'1'</c>..<c>'5'</c> — a per-aircraft throttle preset (<c>image@0x2A445</c>).</summary>
    ThrottlePreset,

    /// <summary>Key <c>'6'</c> — afterburner + full throttle (<c>image@0x2A45B</c>).</summary>
    Afterburner,

    /// <summary>Key <c>'7'</c> — throttle −5 (<c>image@0x2A470</c>).</summary>
    ThrottleDown,

    /// <summary>Key <c>'8'</c> — throttle +5 (<c>image@0x2A482</c>).</summary>
    ThrottleUp,

    /// <summary>Key <c>'b'</c> — the airbrakes toggled (<c>image@0x2A49F</c>).</summary>
    Brakes,

    /// <summary>Key <c>'b'</c> refused: the brakes are jammed (<c>image@0x2A498</c>).</summary>
    BrakesJammed,

    /// <summary>Key <c>'f'</c> — the flaps toggled (<c>image@0x2A4C4</c>).</summary>
    Flaps,

    /// <summary>Key <c>'f'</c> refused: the flaps are jammed (<c>image@0x2A4BD</c>).</summary>
    FlapsJammed,

    /// <summary>Key <c>'g'</c> — the landing gear toggled (<c>image@0x2A4E9</c>).</summary>
    LandingGear,

    /// <summary>Key <c>'g'</c> refused: the aircraft is above the ground-proximity margin (<c>image@0x2A4D5</c>).</summary>
    LandingGearAboveMargin,

    /// <summary>Key <c>'g'</c> refused: the gear is jammed (<c>image@0x2A4DB</c>).</summary>
    LandingGearJammed,

    /// <summary>Key <c>'g'</c> refused: this aircraft has no retractable gear (<c>image@0x2A4E2</c>).</summary>
    LandingGearNotRetractable,
}

/// <summary>
/// The actuator sound one cockpit key schedules, and how far ahead it schedules it.
/// </summary>
/// <remarks>
/// Each of the three toggles ends in its own FAR stub, and the three stubs are byte-identical apart
/// from one immediate: <c>[0xBB3C/0xBB3E] g_audio_time_threshold = [0xF0D2/0xF0D4]
/// g_frame_time_accum + delta</c> (<c>add ax,imm / adc dx,0</c>).  Nothing in the flight kernel
/// reads either global, so the port returns the cue instead of writing it, and the caller decides
/// (see <see cref="CockpitKeyOutcome.Cue"/>).
/// </remarks>
public enum CockpitAudioCue
{
    /// <summary>No cue was scheduled.</summary>
    None = 0,

    /// <summary>
    /// <c>audio_threshold_bump_airbrake @image@0x29B4B</c> — <c>+0x10</c> ticks
    /// (<c>add ax,0x10</c> @<c>image@0x29B52</c>).
    /// </summary>
    Airbrake = 0x10,

    /// <summary>
    /// <c>audio_threshold_bump_flap @image@0x29B36</c> — <c>+0x20</c> ticks
    /// (<c>add ax,0x20</c> @<c>image@0x29B3D</c>).
    /// </summary>
    Flaps = 0x20,

    /// <summary>
    /// <c>audio_threshold_bump_gear @image@0x29B6D</c> — <c>+0x100</c> ticks, the longest of the
    /// three (<c>add ax,0x100</c> @<c>image@0x29B74</c>), consistent with gear-motor travel time.
    /// </summary>
    LandingGear = 0x100,
}

/// <summary>
/// What one call to <see cref="CockpitKeys.Apply"/> did.
/// </summary>
/// <param name="Arm">The branch the dispatcher took.</param>
/// <param name="Cue">
/// The actuator sound the arm scheduled, or <see cref="CockpitAudioCue.None"/>.  Its numeric value
/// IS the tick delta the original adds to <c>g_frame_time_accum</c>.
/// </param>
public readonly record struct CockpitKeyOutcome(CockpitKeyArm Arm, CockpitAudioCue Cue)
{
    /// <summary>True when the arm changed <see cref="Aircraft"/> state (or could have).</summary>
    /// <remarks>
    /// "Could have": a throttle key whose preset equals the current target writes the same bytes
    /// back, which is an ACT with no observable delta — the distinction §2.4 turns on.
    /// </remarks>
    public bool Acted => Arm
        is CockpitKeyArm.ThrottlePreset
        or CockpitKeyArm.Afterburner
        or CockpitKeyArm.ThrottleDown
        or CockpitKeyArm.ThrottleUp
        or CockpitKeyArm.Brakes
        or CockpitKeyArm.Flaps
        or CockpitKeyArm.LandingGear;
}

/// <summary>
/// <c>cockpit_key_dispatch @image@0x2A3FF</c> (249 B, FAR) and its gated wrapper
/// <c>cockpit_key_dispatch_gated @image@0x2257C</c> (26 B, FAR) — the per-keypress cockpit
/// controller: throttle, afterburner, brakes, flaps, landing gear.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is in <c>Sim/Flight</c>.</b>  K5's cross-step census found this function to be the
/// ONLY external writer of <c>s_aircraft_master</c> between two flight frames on the first three
/// recordings, and K8/an earlier pass added two more channels beside it.  Everything it writes
/// (<c>+0xA0</c> throttle target, <c>+0x124</c> status flags) is flight-kernel state, so it belongs
/// to the integer kernel even though the keystroke that triggers it does not.
/// </para>
/// <para>
/// <b>Calling convention.</b>  Register-based and FAR: <c>AX</c> = the cooked key word,
/// <c>BX</c> = a near pointer to <c>s_aircraft_master</c>, which the prologue caches into
/// <c>g_active_aircraft_master_ptr [0xF1BC]</c> (<c>mov [0xf1bc],bx</c> @<c>image@0x2A405</c>) and
/// every arm re-reads from there.  The port passes the <see cref="Aircraft"/> directly, so the
/// <c>[0xF1BC]</c> write has no port counterpart — it is an alias of the argument and no arm can
/// observe a different value.
/// </para>
/// <para>
/// <b>The two gates above it.</b> The dispatcher's sole caller is the wrapper at
/// <c>image@0x2257C</c>, which discards the key unless <c>g_active_aircraft_load_ack [0xC316] ==
/// 0</c> (<c>cmp byte [0xC316],0; jne</c> @<c>image@0x2257F</c>) — see <see cref="WrapperAccepts"/>.
/// The wrapper's own sole caller is the fall-through arm of <c>mission_state_machine</c>'s key ladder
/// (<c>push [bp-0xa]; lcall</c> @<c>image@0x01069</c>), which runs only when the in-flight gate
/// <c>g_in_flight_flag [0xC31C]!= 0</c> (<c>image@0x0105F</c>).  That flag is itself computed once
/// per frame at <c>image@0x00DE2..0x00DFE</c> as <c>[0xC31C] = ([0xEE58] == 0 &amp;&amp; [0xC32F] ==
/// 0 &amp;&amp; [0xC316] &lt; 2)? 1: 0</c> — so a key reaches this function iff <c>[0xEE58] == 0
/// &amp;&amp; [0xC32F] == 0 &amp;&amp; [0xC316] == 0</c>.  All four bytes live outside the flight
/// kernel; the port takes the answer as a parameter rather than inventing a home for them.
/// </para>
/// <para>
/// <b>Sources.</b> Capstone disassembly of <c>image@0x2A3FF..0x2A4F7</c> and of the four callees
/// (<c>hp_damage_speed_cap_compute @image@0x2A21D</c>, <c>speed_clamp_on_hp_damage
/// @image@0x2A206</c>, <c>flight_check_alive_or_active @image@0x2C22C</c>, the three audio stubs at
/// <c>image@0x29B36/0x29B4B/0x29B6D</c>), cross-read,:1983.  Where the decoded prose and the bytes
/// disagree the bytes win.
/// </para>
/// </remarks>
public static class CockpitKeys
{
    /// <summary>
    /// The key <c>'g'</c> (<c>cmp ax,0x67</c> @<c>image@0x2A417</c>) — the landing gear.
    /// </summary>
    public const int GearKey = 0x67;

    /// <summary>The key <c>'b'</c> (<c>sub al,0x2a ; je</c> @<c>image@0x2A42D</c>) — the airbrakes.</summary>
    public const int BrakeKey = 0x62;

    /// <summary>The key <c>'f'</c> (<c>sub al,4 ; jne</c> @<c>image@0x2A431</c>) — the flaps.</summary>
    public const int FlapKey = 0x66;

    /// <summary>The lowest throttle-preset key, <c>'1'</c> (<c>cmp dx,0x31 ; jl</c> @<c>image@0x2A438</c>).</summary>
    public const int FirstPresetKey = 0x31;

    /// <summary>The highest throttle-preset key, <c>'5'</c> (<c>cmp dx,0x35 ; jle</c> @<c>image@0x2A43D</c>).</summary>
    public const int LastPresetKey = 0x35;

    /// <summary>The afterburner key, <c>'6'</c> (<c>sub al,0x36 ; je</c> @<c>image@0x2A421</c>).</summary>
    public const int AfterburnerKey = 0x36;

    /// <summary>The throttle-down key, <c>'7'</c> (<c>dec al ; je</c> @<c>image@0x2A425</c>).</summary>
    public const int ThrottleDownKey = 0x37;

    /// <summary>The throttle-up key, <c>'8'</c> (<c>dec al ; je</c> @<c>image@0x2A429</c>).</summary>
    public const int ThrottleUpKey = 0x38;

    // Added while wiring the map window's zoom to PageUp/PageDown. They do not belong in
    // this class and the values are not the original's: the map zoom is NOT a cockpit key
    // (CockpitKeys.Apply has no arm for either, so they reached the flight kernel only to be counted
    // as unmodelled), and the original's own zoom arms are the ladder's cooked 0x2C `,` and 0x2E `.`
    // at image@0x012C7 / image@0x012B2 — which Render.Cockpit.MapWindow already carries with those
    // citations.  Every constant in this class is a key the ORIGINAL's cockpit dispatch tests, with
    // the byte that proves it; 0x21/0x22 were neither.  The zoom now reaches the window through
    // FlyControls.MapWindowZoomIn/Out and the menu's PageUp/PageDown edges.

    /// <summary>
    /// The step the <c>'7'</c>/<c>'8'</c> keys move the throttle by, in whole percent
    /// (<c>sub ax,5</c> @<c>image@0x2A47D</c>, <c>add ax,5</c> @<c>image@0x2A48F</c>).
    /// </summary>
    public const int ThrottleStepPercent = 5;

    /// <summary>
    /// The throttle percentage key <c>'6'</c> commands: <c>0x64</c> = 100
    /// (<c>mov ax,0x64</c> @<c>image@0x2A46B</c>).
    /// </summary>
    public const int FullThrottlePercent = 0x64;

    /// <summary>
    /// The value of <c>g_active_aircraft_load_ack [0xC316]</c> at which the wrapper forwards a key.
    /// </summary>
    public const byte WrapperReadyAck = 0;

    /// <summary>
    /// <c>cockpit_key_dispatch_gated @image@0x2257C</c> — does the wrapper forward the key?
    /// </summary>
    /// <param name="activeAircraftLoadAck"><c>g_active_aircraft_load_ack [0xC316]</c>.</param>
    /// <returns>True iff the ack byte is zero.</returns>
    /// <remarks>
    /// <c>cmp byte [0xC316],0; jne &lt;epilogue&gt;</c> (<c>image@0x2257F..0x22584</c>).  Zero is the
    /// FLYING state: <c>active_aircraft_load_and_state_reset @image@0x224CA</c> writes 0 when a flight
    /// is armed and only <c>scene_setup_or_camera_reset @image@0x22643</c> leaves it — writing 1 at
    /// <c>image@0x22655</c> in the same six instructions that clear <c>master[+0x122]</c> at
    /// <c>image@0x2265A</c> (K8's flight-end channel), after which <c>flight_engine_first_frame_arm
    /// @image@0x225CE</c> advances it to 2.  So "the ack is non-zero" and "the flight has ended" are
    /// the same event, which is why <see cref="Aircraft.ActiveState"/> is a faithful proxy for this
    /// gate during a flight.
    /// </remarks>
    public static bool WrapperAccepts(byte activeAircraftLoadAck) =>
        activeAircraftLoadAck == WrapperReadyAck;

    /// <summary>
    /// The dispatcher's own first act: fold a shifted letter onto its lower-case key
    /// (<c>test byte [bx+0xAF27],1 ; je ; add dx,0x20</c>, <c>image@0x2A40B..0x2A412</c>).
    /// </summary>
    /// <param name="key">The cooked key word the wrapper passed in <c>AX</c>.</param>
    /// <returns>The key the ladder below dispatches on.</returns>
    /// <remarks>
    /// <para>
    /// <c>DGROUP 0xAF27</c> is the Microsoft C 6.0 runtime's <c>_ctype</c> table biased by one
    /// (<c>platform</c>: MSC's <c>_ctype[]</c> is indexed <c>_ctype[c + 1]</c>, so a base of
    /// <c>_ctype + 1</c> indexes it by the character itself), and bit <c>0x01</c> is <c>_UPPER</c>.
    /// This is transliterated as the RULE, not copied as data: the 256 bytes at
    /// <c>image@0x46C87</c> (= DGROUP <c>0x3BD60</c> + <c>0xAF27</c>) were enumerated and bit 0 is
    /// set at exactly the 26 indices <c>0x41..0x5A</c> and nowhere else, so
    /// <c>isupper</c>-then-<c>+0x20</c> is <c>tolower</c> for ASCII.
    /// </para>
    /// <para>
    /// The original indexes the table with the WHOLE 16-bit key, so a key ≥ 0x100 reads past the
    /// table into unrelated DGROUP.  That read is provably inert: any key above <c>0x67</c>
    /// unsigned skips the ladder (<c>ja</c> @<c>image@0x2A41F</c>) and fails the signed
    /// <c>0x31..0x35</c> range test, with or without the <c>+0x20</c>.  The port therefore folds
    /// only <c>0x41..0x5A</c> and documents the difference instead of reproducing an out-of-bounds
    /// read.
    /// </para>
    /// </remarks>
    public static int NormaliseKey(int key) =>
        key is >= 'A' and <= 'Z' ? key + 0x20 : key;

    /// <summary>
    /// <c>hp_damage_speed_cap_compute @image@0x2A21D</c> (63 B, NEAR) — set the throttle target from
    /// a raw percentage, with the three suppression gates and the hit-point clamp.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="percent">
    /// The value the caller leaves in <c>AX</c>: a preset byte, <c>0x64</c>, or the current
    /// throttle ±5.  Interpreted as a signed 16-bit word (<c>cwd</c> @<c>image@0x2A243</c>).
    /// </param>
    /// <remarks>
    /// <para>
    /// Three gates zero the requested value (<c>sub si,si</c> @<c>image@0x2A23F</c>):
    /// <list type="number">
    /// <item>the fuel <c>i32</c> at <c>+0xC0</c> is zero — <c>mov ax,[bx+0xc2] ; or ax,[bx+0xc0] ;
    /// je</c> (<c>image@0x2A224..0x2A22C</c>), a 16-bit OR of the two halves, so the test is
    /// <c>fuel == 0</c> exactly;</item>
    /// <item>… unless <c>[bx+0xB8] == 0</c> (<c>image@0x2A22E</c>), which skips gates 2 and 3;</item>
    /// <item><c>[bx+0xBA] &gt;= [bx+0xB8]</c> as signed <c>i16</c> (<c>cmp [bx+0xba],ax ; jl
    /// &lt;skip&gt;</c>, <c>image@0x2A239</c>).</item>
    /// </list>
    /// <c>+0xB8</c> and <c>+0xBA</c> have NO other reference anywhere in the flight segment
    /// (<c>image@0x2A110..0x2C500</c> disassembled and scanned): the loader's 1:1 <c>.fmd</c> copy
    /// writes them and this function is their only reader, so the port takes them from
    /// <see cref="Aircraft.ThrottleCutoffLimit"/> / <see cref="Aircraft.ThrottleCutoffCounter"/>.
    /// In all six shipped <c>.fmd</c> files <c>+0xB8</c> is 0, so gates 2 and 3 are dead on shipped
    /// data and only the fuel gate can fire.
    /// </para>
    /// <para>
    /// The store is <c>DX:AX = sext16(value) &lt;&lt; 8</c> — <c>cwd</c> then
    /// <c>_aNlshl(cl=8)</c> (<c>lcall 0x1000:0x020A</c> @<c>image@0x2A246</c>, whose <c>cl == 8</c>
    /// fast path <c>mov dh,dl / mov dl,ah / mov ah,al / xor al,al</c> @<c>image@0x0020F</c> is a
    /// 32-bit left shift by 8) — written to <c>+0xA0/+0xA2</c> and then clamped by
    /// <see cref="AircraftDamage.ClampThrottleTargetToHitPoints"/> (<c>call</c>
    /// @<c>image@0x2A257</c>), which is why an engine hit and a throttle key are two writers of one
    /// field.
    /// </para>
    /// </remarks>
    public static void SetThrottleTarget(Aircraft aircraft, int percent)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        short value = unchecked((short)percent);                      // image@0x2A21E: mov si,ax

        if (aircraft.Fuel == 0)                                       // image@0x2A224..0x2A22C
        {
            value = 0;                                                // image@0x2A23F
        }
        else if (aircraft.ThrottleCutoffLimit != 0                    // image@0x2A22E
            && aircraft.ThrottleCutoffCounter >= aircraft.ThrottleCutoffLimit)   // image@0x2A239
        {
            value = 0;                                                // image@0x2A23F
        }

        // image@0x2A243..0x2A253: cwd + _aNlshl(8) + the two word stores.  The shift is 32-bit and
        // wraps; `int` arithmetic on a sign-extended i16 cannot overflow it, so `<< 8` is exact.
        aircraft.ThrottleTarget = value << 8;

        AircraftDamage.ClampThrottleTargetToHitPoints(aircraft);      // image@0x2A257
    }

    /// <summary>
    /// <c>cockpit_key_dispatch @image@0x2A3FF</c> — apply one cooked key to the aircraft.
    /// </summary>
    /// <param name="aircraft">The active aircraft (the original's <c>BX</c> / <c>[0xF1BC]</c>).</param>
    /// <param name="key">
    /// The key word the wrapper passed in <c>AX</c> — what
    /// <c>kbd_event_poll_and_classify @image@0x20488</c> returns and
    /// <c>mission_state_machine</c>'s ladder did not claim.  See <see cref="CockpitKeyTranslator"/>.
    /// </param>
    /// <param name="playerAltitudeQ8Feet">
    /// The player world object's <c>pos_y</c> (<c>WorldObject +0x0A</c>).  Only the <c>'g'</c> key
    /// uses it, through <c>flight_check_alive_or_active @image@0x2C22C</c>
    /// (<c>call</c> @<c>image@0x2A4D0</c>), which the original calls FRESH here rather than reading
    /// the cached byte.
    /// </param>
    /// <param name="cachedGroundProximityFlag">
    /// <c>[0xF1C4] g_ground_proximity_margin_flag (ex-g_aircraft_alive_flag)</c> as the last integrate frame left it
    /// (<see cref="FlightInputs.GroundProximityFlag"/>).  Only the <c>'b'</c> key uses it, and it reads the
    /// CACHED global rather than recomputing (<c>cmp byte [0xf1c4],0</c> @<c>image@0x2A4A4</c>) —
    /// the asymmetry with <c>'g'</c> is in the bytes.
    /// </param>
    /// <returns>The arm taken and the actuator cue it scheduled.</returns>
    public static CockpitKeyOutcome Apply(
        Aircraft aircraft,
        int key,
        int playerAltitudeQ8Feet,
        byte cachedGroundProximityFlag)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        // The original's AX is 16 bits wide; the port takes an int so a caller need not cast, and
        // narrows here so every comparison below has the original's width.
        int normalised = NormaliseKey(key & 0xFFFF);                  // image@0x2A40B..0x2A415

        // image@0x2A417: cmp ax,0x67 / jne — the 'g' key is tested before the ladder.
        if (normalised == GearKey)
        {
            return LandingGearKey(aircraft, playerAltitudeQ8Feet);    // image@0x2A4D0
        }

        // image@0x2A41F: `ja` on the same compare — UNSIGNED.  Everything at or below 0x67 falls
        // into the byte ladder; because ax < 0x67 there, AH is zero and the ladder's `sub al`
        // chain cannot alias a two-byte key onto a one-byte arm.
        if ((uint)normalised < GearKey)
        {
            switch (normalised)
            {
                case AfterburnerKey:                                  // image@0x2A421
                    return AfterburnerKeyArm(aircraft);
                case ThrottleDownKey:                                 // image@0x2A425
                    return ThrottleStep(aircraft, -ThrottleStepPercent);
                case ThrottleUpKey:                                   // image@0x2A429
                    return ThrottleStep(aircraft, +ThrottleStepPercent);
                case BrakeKey:                                        // image@0x2A42D
                    return BrakeKeyArm(aircraft, cachedGroundProximityFlag);
                case FlapKey:                                         // image@0x2A431
                    return FlapKeyArm(aircraft);
                default:
                    break;
            }
        }

        // image@0x2A438..0x2A440: cmp dx,0x31 / jl ; cmp dx,0x35 / jle — a SIGNED range test on the
        // whole normalised word, so a key with the high bit set is below 0x31 and is discarded.
        if (unchecked((short)normalised) < FirstPresetKey
            || unchecked((short)normalised) > LastPresetKey)
        {
            return new CockpitKeyOutcome(CockpitKeyArm.Ignored, CockpitAudioCue.None);
        }

        // image@0x2A449: and byte [bx+0x124],0xFE — a preset always cancels the afterburner.
        aircraft.StatusFlags &= ~AircraftStatusFlags.Afterburner;

        // image@0x2A450: mov al,[bx+si+0x7b] with si = the key, i.e. master[+0xAC + (key - '1')],
        // zero-extended by `sub ah,ah` @image@0x2A453.
        byte preset = aircraft.ThrottlePresetPercent(normalised - FirstPresetKey);
        SetThrottleTarget(aircraft, preset);                          // image@0x2A455
        return new CockpitKeyOutcome(CockpitKeyArm.ThrottlePreset, CockpitAudioCue.None);
    }

    /// <summary>Key <c>'6'</c> — <c>image@0x2A45B..0x2A46E</c>.</summary>
    private static CockpitKeyOutcome AfterburnerKeyArm(Aircraft aircraft)
    {
        // image@0x2A45F: cmp word [bx+0xb6],0 / je — an aircraft with no afterburner scale gets the
        // throttle command but not the bit.  (All four shipped jets have it; both props are 0.)
        if (aircraft.AfterburnerScale != 0)
        {
            aircraft.StatusFlags |= AircraftStatusFlags.Afterburner;  // image@0x2A466
        }

        // image@0x2A46B/0x2A46E: mov ax,0x64 ; jmp into the shared call at image@0x2A455.
        SetThrottleTarget(aircraft, FullThrottlePercent);
        return new CockpitKeyOutcome(CockpitKeyArm.Afterburner, CockpitAudioCue.None);
    }

    /// <summary>Keys <c>'7'</c> and <c>'8'</c> — <c>image@0x2A470</c> / <c>image@0x2A482</c>.</summary>
    /// <remarks>
    /// The step reads the UNALIGNED word at <c>+0xA1</c> (<c>mov ax,[bx+0xa1]</c>,
    /// <c>image@0x2A479</c>/<c>image@0x2A48B</c>) — bytes 1 and 2 of the throttle-target <c>i32</c>,
    /// i.e. the target divided by 256 and truncated toward −∞, which is the percentage
    /// <see cref="SetThrottleTarget"/> shifted in.  The add/sub is a 16-bit wrap on that word and the
    /// result is then re-sign-extended, so a target below <c>0x00000500</c> steps to a negative
    /// percentage rather than clamping at zero: there is NO clamp in this function.  What bounds it is
    /// <see cref="AircraftDamage.ClampThrottleTargetToHitPoints"/> at the tail, whose lower bound is
    /// <c>hp_reference &lt;&lt; 8</c> (<c>+0xA6</c>, zero in all six shipped <c>.fmd</c> files) — so on
    /// shipped data '7' does bottom out at 0 and then goes no lower.
    /// </remarks>
    private static CockpitKeyOutcome ThrottleStep(Aircraft aircraft, int delta)
    {
        aircraft.StatusFlags &= ~AircraftStatusFlags.Afterburner;     // image@0x2A474 / 0x2A486

        ushort current = unchecked((ushort)(aircraft.ThrottleTarget >> 8));
        ushort stepped = unchecked((ushort)(current + delta));        // image@0x2A47D / 0x2A48F
        SetThrottleTarget(aircraft, unchecked((short)stepped));

        return new CockpitKeyOutcome(
            delta < 0 ? CockpitKeyArm.ThrottleDown : CockpitKeyArm.ThrottleUp,
            CockpitAudioCue.None);
    }

    /// <summary>Key <c>'b'</c> — <c>image@0x2A494..0x2A4B7</c>.</summary>
    private static CockpitKeyOutcome BrakeKeyArm(Aircraft aircraft, byte cachedGroundProximityFlag)
    {
        // image@0x2A498: test byte [bx+0x123],0x20 / jne — the BRAKES-JAMMED interlock (K9 §4:
        // damage bits 0x08/0x10/0x20 are the gear/flaps/brakes jams; the flag-only damage types).
        if ((aircraft.DamageFlags & AircraftDamageFlags.FlagOnly20) != 0)
        {
            return new CockpitKeyOutcome(CockpitKeyArm.BrakesJammed, CockpitAudioCue.None);
        }

        aircraft.StatusFlags ^= AircraftStatusFlags.Airbrake;         // image@0x2A49F: xor …,8

        // image@0x2A4A4..0x2A4B0: the CUE is conditional even though the toggle is not —
        // `cmp byte [0xf1c4],0 / jne <play>` then `cmp word [bx+0xfa],0 / je <skip>`, i.e. play iff
        // the aircraft is near the ground OR the airbrake has a drag coefficient at all.
        bool cue = cachedGroundProximityFlag != 0 || aircraft.AirbrakeDragCoefficient != 0;
        return new CockpitKeyOutcome(
            CockpitKeyArm.Brakes, cue ? CockpitAudioCue.Airbrake : CockpitAudioCue.None);
    }

    /// <summary>Key <c>'f'</c> — <c>image@0x2A4B9..0x2A4CE</c>.</summary>
    private static CockpitKeyOutcome FlapKeyArm(Aircraft aircraft)
    {
        // image@0x2A4BD: test byte [bx+0x123],0x10 / jne — the FLAPS-JAMMED interlock.
        if ((aircraft.DamageFlags & AircraftDamageFlags.FlagOnly10) != 0)
        {
            return new CockpitKeyOutcome(CockpitKeyArm.FlapsJammed, CockpitAudioCue.None);
        }

        // image@0x2A4C4: xor byte [bx+0x124],2 — bit 1.  An earlier pass named the bit FLAPS from the
        // manual's "Gear, Brakes, and Flaps"; the port enum is AircraftStatusFlags.Flaps
        // (ex-LandingGear; RENAMED R1).  Key name and bit name now agree.
        aircraft.StatusFlags ^= AircraftStatusFlags.Flaps;

        // image@0x2A4C9: the cue is UNCONDITIONAL on this arm, unlike 'b'.
        return new CockpitKeyOutcome(CockpitKeyArm.Flaps, CockpitAudioCue.Flaps);
    }

    /// <summary>Key <c>'g'</c> — <c>image@0x2A4D0..0x2A4F2</c>.</summary>
    private static CockpitKeyOutcome LandingGearKey(Aircraft aircraft, int playerAltitudeQ8Feet)
    {
        // image@0x2A4D0..0x2A4D5: call flight_check_alive_or_active ; or al,al ; jne <epilogue>.
        // The gear only operates when the aircraft is NOT within the speed-derived ground-proximity
        // margin — AL == 1 means "at or below the margin", and that arm RETURNS.
        if (ControlAxisKernel.GroundProximityFlag(aircraft, playerAltitudeQ8Feet) != 0)
        {
            return new CockpitKeyOutcome(
                CockpitKeyArm.LandingGearAboveMargin, CockpitAudioCue.None);
        }

        // image@0x2A4DB: test byte [bx+0x123],8 / jne — the GEAR-JAMMED interlock.
        if ((aircraft.DamageFlags & AircraftDamageFlags.FlagOnly08) != 0)
        {
            return new CockpitKeyOutcome(CockpitKeyArm.LandingGearJammed, CockpitAudioCue.None);
        }

        // image@0x2A4E2: test byte [bx+0x124],0x20 / je — bit 5 must be SET.  All six shipped
        // .fmd files author it on (props 0x20, jets 0x60), so on shipped data the gate never
        // refuses; it is the "has retractable gear" switch an authored aircraft could clear.
        if ((aircraft.StatusFlags & AircraftStatusFlags.HasRetractableGear) == 0)
        {
            return new CockpitKeyOutcome(
                CockpitKeyArm.LandingGearNotRetractable, CockpitAudioCue.None);
        }

        // image@0x2A4E9: xor byte [bx+0x124],4 — bit 2.  this bit is the LANDING GEAR
        // (AircraftStatusFlags.LandingGear, ex-GroundContact; RENAMED R1).  This is the fourth writer of the bit
        // and the only one outside the loader / aircraft_pose_set.
        aircraft.StatusFlags ^= AircraftStatusFlags.LandingGear;

        return new CockpitKeyOutcome(CockpitKeyArm.LandingGear, CockpitAudioCue.LandingGear);
    }
}
