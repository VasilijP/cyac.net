using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// Everything combat damage does to the player's <see cref="Aircraft"/> master struct — and
/// nothing else.
/// </summary>
/// <remarks>
/// <para>
/// INT-only: every write here changes the flight model, so it is spine state.
/// </para>
/// <para>
/// <b>Scope.</b>  Damage <i>selection</i> (the weighted roulette of
/// <see cref="Model.Combat.PlayerDamageTable"/>), the cockpit messages, the ammunition and score
/// bookkeeping and the visual spawns all live in <c>weapon_fire_combat_loop @image@0x0F748</c> and
/// belong to the combat kernel.  What this class ports is the complete set of instructions in the
/// combat subsystem that write <c>s_aircraft_master</c> — the flight-kernel-adjacent half.
/// </para>
/// <para>
/// <b>The write-set census is image-exhaustive</b> and was re-derived for this build two ways over
///: (a) a disassembly sweep of every scanner-known function for a memory
/// operand with no base and no index whose displacement lands in <c>[0xEF98, 0xF0C1]</c> (the master's
/// 298 bytes), and (b) an opcode/ModRM byte-pattern scan of the whole image for the same operand form,
/// which finds the same sites plus eight hits that fall inside mesh and pointer-table DATA regions
/// (<c>image@0x391A7</c> is inside <c>mesh_me110sh</c>,:2411).  Outside the flight chain and the loader,
/// only these functions write the master:
/// </para>
/// <list type="table">
///   <listheader><term>writer</term><description>master bytes</description></listheader>
///   <item>
///     <term><c>weapon_fire_combat_loop @image@0x0F748</c> (7 absolute sites + 4 register calls)</term>
///     <description><c>+0x38/+0x3A/+0x3C/+0x3E</c>, <c>+0xDE/+0xE0</c>, <c>+0xEC</c>, and via
///     <c>aircraft_master_damage_apply2</c> <c>+0x123</c>, <c>+0xA4</c>, <c>+0xA0</c>,
///     <c>+0xEE</c>, <c>+0xF0</c> — this class</description>
///   </item>
///   <item>
///     <term><c>engagement_per_frame_tick @image@0x0FC51</c> (4 absolute sites + 4 register calls)</term>
///     <description><c>+0xC0..+0xC3</c> and via <c>aircraft_master_damage_apply2</c>
///     <c>+0x123</c>, <c>+0xA4</c>, <c>+0xA0</c> — this class</description>
///   </item>
///   <item>
///     <term><c>scene_setup_or_camera_reset @image@0x2265A</c></term>
///     <description><c>+0x122</c> — K8's flight-end channel</description>
///   </item>
///   <item>
///     <term><c>cockpit_key_dispatch @image@0x2A3FF</c></term>
///     <description><c>+0xA0</c>, <c>+0x124</c> — K5's cockpit-key channel</description>
///   </item>
///   <item>
///     <term><c>active_aircraft_load_and_state_reset @image@0x224CA</c>,
///     <c>flight_model_load_for_aircraft @image@0x2A112</c></term>
///     <description>the loader (per-flight re-arm)</description>
///   </item>
///   <item>
///     <term><c>flight_engine_per_frame_top @image@0x228A5/0x228B8</c>, the film-playback and
///     roster screens (<c>image@0x31341</c>, <c>0x31A4C</c>, <c>0x33467</c>)</term>
///     <description><c>+0x124</c> bits only</description>
///   </item>
/// </list>
/// <para>
/// <b>Runtime proof</b>, watching every write to that region of the original's memory: over
/// the whole staged shoot-down the six distinct writers of master <c>+0x38..+0x3F</c> are
/// crt0's BSS clear (<c>image@0x000A1</c>, 8 bytes), the <c>.fmd</c> LZSS load
/// (<c>image@0x2FBA4</c>, 8 bytes, value <c>0xD2</c>) and <see cref="HalveAileronAuthority"/>'s four
/// <c>sar</c> instructions (<c>image@0x0F984/0x0F988/0x0F98C/0x0F990</c>, 2 bytes each — one word
/// apiece, fired ONCE, at ticks 4,341,278,682..685).
/// </para>
/// </remarks>
public static class AircraftDamage
{
    /// <summary>
    /// The divisor every percentage-scaled damage type uses: <c>100</c>
    /// (<c>mov bx,0x64</c> @<c>image@0x2A541</c>, <c>image@0x2A55A</c>, <c>image@0x2A586</c>).
    /// </summary>
    public const short PercentBase = 100;

    /// <summary>
    /// The floor <see cref="AircraftDamageFlags.FlightModel"/> clamps the roll-dead hi bound to:
    /// <c>10</c> (<c>cmp word [bx+0x38],0xa</c> @<c>image@0x2A569</c>) — control is never fully lost
    /// on that path.
    /// </summary>
    public const short FlightModelBoundFloor = 10;

    /// <summary>
    /// Applies one damage event to the aircraft master —
    /// <c>aircraft_master_damage_apply2 @image@0x2A4F8</c> (170 B, FAR, <c>RETF</c> @0x2A5A1).
    /// </summary>
    /// <param name="aircraft">The player's aircraft (the original's <c>BX</c>, always
    /// <c>lea bx,[0xef98]</c> at all eight call sites).</param>
    /// <param name="type">The damage-type bitmask (the original's <c>AL</c>).</param>
    /// <param name="amountPercent">The percentage the scaled types use (the original's
    /// <c>DX</c>).</param>
    /// <remarks>
    /// <para>
    /// Register convention, not cdecl: <c>BX</c> = master near pointer (re-stored into
    /// <c>g_active_aircraft_master_ptr [0xF1BC]</c> @<c>image@0x2A4FE</c>, which the port does not
    /// need because the pointer is the object), <c>AL</c> = type, <c>DX</c> = amount.
    /// </para>
    /// <para>
    /// <b>The flag OR happens first and unconditionally</b>
    /// (<c>or byte [bx+0x123],al</c> @<c>image@0x2A504</c>), then a three-way dispatch:
    /// <c>== 0x40</c> hull, <c>&gt; 0x40</c> nothing, otherwise a <c>dec al</c>/<c>je</c> ladder for
    /// <c>1</c>, <c>2</c>, <c>4</c> and nothing for anything else
    /// (<c>image@0x2A508..0x2A520</c>).
    /// </para>
    /// <para>
    /// <b>Only six of the seven types are reachable.</b>  A byte sweep for
    /// <c>9A E8 03 11 3A</c> (<c>lcall 0x3A11:0x03E8</c>) finds exactly eight call sites —
    /// <c>image@0x0F9B6</c> (<c>0x40</c>), <c>0x0F9C4</c> (<c>0x02</c>), <c>0x0F9EF</c>
    /// (<c>0x08</c>), <c>0x0FAC5</c> (<c>0x01</c>), <c>0x0FD26</c> (<c>0x10</c>), <c>0x0FD33</c>
    /// (<c>0x08</c>), <c>0x0FD40</c> (<c>0x20</c>), <c>0x0FDA7</c> (<c>0x01</c>) — plus the two
    /// fall-through entries <c>image@0x0FA08</c> (<c>0x10</c>) and <c>image@0x0FA1D</c>
    /// (<c>0x20</c>) that jump into <c>0x0F9ED</c>, and <c>image@0x0FAB7</c> (<c>0x01</c>) that
    /// jumps into <c>0x0F9EF</c>.  The set of <c>AL</c> values is
    /// <c>{0x01, 0x02, 0x08, 0x10, 0x20, 0x40}</c>: <b><see cref="AircraftDamageFlags.FlightModel"/>
    /// (0x04) has no caller anywhere in the image</b> — the arm is implemented here for
    /// completeness and is marked unreachable-on-shipped-data.
    /// </para>
    /// </remarks>
    public static void Apply(Aircraft aircraft, AircraftDamageFlags type, short amountPercent)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        // image@0x2A504: or byte ptr [bx + 0x123], al — always, for every type.
        aircraft.DamageFlags |= type;

        // image@0x2A508: sub ah,ah — the dispatch is on the ZERO-EXTENDED byte.
        int selector = (byte)type;

        switch (selector)
        {
            case 0x40:                                              // image@0x2A50D → 0x2A57E
                ApplyHullDamage(aircraft, amountPercent);
                return;

            case 0x01:                                              // image@0x2A516 → 0x2A522
                ApplyHitPointDamage(aircraft, amountPercent);
                return;

            case 0x02:                                              // image@0x2A51A → 0x2A539
                ApplyRollAuthorityDamage(aircraft, amountPercent);
                return;

            case 0x04:                                              // image@0x2A51E → 0x2A553
                ApplyFlightModelDamage(aircraft, amountPercent);
                return;

            default:
                // image@0x2A511 (> 0x40) and image@0x2A520 (0x08/0x10/0x20 and any other value
                // below 0x40): the bit is recorded, nothing numeric moves.
                return;
        }
    }

    /// <summary>
    /// <see cref="AircraftDamageFlags.HitPoints"/> (<c>0x01</c>) —
    /// <c>image@0x2A522..0x2A537</c>: subtract the amount from <see cref="Aircraft.HitPoints"/>,
    /// clamp at zero, then re-clamp the throttle target.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="amount">The raw amount subtracted (NOT a percentage on this path — the
    /// original does <c>mov ax,dx ; sub [bx+0xa4],ax</c>, no <c>muldiv</c>).</param>
    /// <remarks>
    /// The two shipped amounts are <c>0x64</c> (<c>image@0x0FAC2</c>, the "ENGINE FAILURE" arm, and
    /// <c>image@0x0FDA4</c>, the engine-meter saturation in <c>engagement_per_frame_tick</c>) and
    /// <c>prng_rand_bounded(0x14) + 0x0A</c> (<c>image@0x0FAA6..0x0FAB0</c>, the "ENGINE DAMAGED"
    /// arm) — i.e. 10..29.
    /// </remarks>
    public static void ApplyHitPointDamage(Aircraft aircraft, short amount)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        // image@0x2A528: sub word ptr [bx+0xa4], ax   — 16-bit, wraps.
        short hp = unchecked((short)(aircraft.HitPoints - amount));

        // image@0x2A52C/0x2A52E: jns / mov word ptr [bx+0xa4], 0 — JNS tests the i16 sign.
        if (hp < 0)
        {
            hp = 0;
        }

        aircraft.HitPoints = hp;

        // image@0x2A534: call 0x2a206 (NEAR) — speed_clamp_on_hp_damage.
        ClampThrottleTargetToHitPoints(aircraft);
    }

    /// <summary>
    /// <c>speed_clamp_on_hp_damage @image@0x2A206</c> (13 B, NEAR) plus its callee
    /// <c>i32_clamp_range @image@0x2B88A</c> (<c>ret 6</c>, callee-clean).
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <remarks>
    /// <para>
    /// The helper pushes <c>&amp;master[+0xA0]</c>, <c>master[+0xA4]</c> and <c>master[+0xA6]</c>
    /// (<c>image@0x2A206..0x2A215</c>).  <c>i32_clamp_range</c> sign-extends each i16 argument and
    /// shifts it left by 8 (<c>cdq</c> then the four-byte shuffle
    /// <c>mov dh,dl / mov dl,ah / mov ah,al / sub al,al</c> @<c>image@0x2B899..0x2B8A0</c>, which is
    /// exactly <c>(i32)(i16)v &lt;&lt; 8</c>), then:
    /// </para>
    /// <para>
    /// <c>if (hp&lt;&lt;8 &lt; target) { target = hp&lt;&lt;8; return; }</c>
    /// <c>else if (reference&lt;&lt;8 &gt; target) { target = reference&lt;&lt;8; }</c>
    /// </para>
    /// <para>
    /// The order is load-bearing: the upper bound short-circuits, so when the reference EXCEEDS the
    /// remaining hit points the lower bound is not applied.  The 32-bit comparisons are the usual
    /// signed-high-word / unsigned-low-word pair (<c>jg</c>/<c>jl</c> then <c>jb</c>/<c>jbe</c>,
    /// <c>image@0x2B8A8..0x2B8D0</c>).
    /// </para>
    /// <para>
    /// <c>master[+0xA0]</c> is the SAME field the cockpit '7'/'8'/'1'-'5' throttle keys write (K5's
    /// cockpit channel) — an engine hit and a throttle key are two writers of one byte range.
    /// </para>
    /// </remarks>
    public static void ClampThrottleTargetToHitPoints(Aircraft aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        int upper = aircraft.HitPoints << 8;                        // image@0x2B897..0x2B8A5
        int lower = aircraft.HitPointReference << 8;                // image@0x2B8B6..0x2B8C4
        int target = aircraft.ThrottleTarget;

        if (upper < target)                                         // image@0x2B8A8..0x2B8B1
        {
            aircraft.ThrottleTarget = upper;                        // image@0x2B8D2
            return;
        }

        if (lower > target)                                         // image@0x2B8C7..0x2B8D0
        {
            aircraft.ThrottleTarget = lower;                        // image@0x2B8D2
        }
    }

    /// <summary>
    /// <see cref="AircraftDamageFlags.RollAuthority"/> (<c>0x02</c>) —
    /// <c>image@0x2A539..0x2A551</c>: <b>ADD</b> <c>roll_gain × amount ÷ 100</c> to
    /// <see cref="Aircraft.RollAuthorityGain"/>.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="amountPercent">The percentage (the only shipped caller passes
    /// <c>0x64</c> @<c>image@0x0F9C1</c>, so a wing hit DOUBLES the roll gain).</param>
    /// <remarks>
    /// The only damage path that increases a coefficient: <c>add word ptr [bx+0xf0], ax</c>
    /// @<c>image@0x2A54D</c>, a 16-bit add that wraps.  Physically a destabilising hit — the
    /// aircraft rolls harder for the same stick.
    /// </remarks>
    public static void ApplyRollAuthorityDamage(Aircraft aircraft, short amountPercent)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        short delta = Fixed.MulDiv16Signed(                          // image@0x2A544
            aircraft.RollAuthorityGain, amountPercent, PercentBase);
        aircraft.RollAuthorityGain =
            unchecked((short)(aircraft.RollAuthorityGain + delta));  // image@0x2A54D
    }

    /// <summary>
    /// <see cref="AircraftDamageFlags.FlightModel"/> (<c>0x04</c>) —
    /// <c>image@0x2A553..0x2A57C</c>: narrow the roll-dead axis' bounds to a symmetric pair, floored
    /// at <see cref="FlightModelBoundFloor"/>.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="amountPercent">The percentage.</param>
    /// <remarks>
    /// <b>UNREACHABLE on shipped data.</b>  No site in the L1 image calls
    /// <c>aircraft_master_damage_apply2</c> with <c>AL = 0x04</c> (see <see cref="Apply"/>).  It is
    /// ported so the function is complete and so an authored mission or a future build that does
    /// reach it behaves like the original.
    /// </remarks>
    public static void ApplyFlightModelDamage(Aircraft aircraft, short amountPercent)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        ControlAxisState axis = aircraft.RollDeadAxis;
        short delta = Fixed.MulDiv16Signed(axis.HiBound, amountPercent, PercentBase);  // image@0x2A55D
        short hi = unchecked((short)(axis.HiBound - delta));                           // image@0x2A566

        if (hi < FlightModelBoundFloor)                                                // image@0x2A569
        {
            hi = FlightModelBoundFloor;                                                // image@0x2A56F
        }

        axis.HiBound = hi;
        axis.LoBound = unchecked((short)-hi);                        // image@0x2A577 neg / 0x2A579
    }

    /// <summary>
    /// <see cref="AircraftDamageFlags.Hull"/> (<c>0x40</c>) — <c>image@0x2A57E..0x2A59D</c>:
    /// subtract <c>elevator × amount ÷ 100</c> from <see cref="Aircraft.ElevatorAuthority"/>,
    /// clamped at zero.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="amountPercent">The percentage (the only shipped caller passes <c>0x64</c>
    /// @<c>image@0x0F9B3</c>, so a wing hit zeroes elevator authority outright).</param>
    /// <remarks>
    /// No floor, unlike <see cref="ApplyFlightModelDamage"/>: pitch authority CAN be lost
    /// completely.
    /// </remarks>
    public static void ApplyHullDamage(Aircraft aircraft, short amountPercent)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        short delta = Fixed.MulDiv16Signed(                          // image@0x2A589
            aircraft.ElevatorAuthority, amountPercent, PercentBase);
        short elevator = unchecked((short)(aircraft.ElevatorAuthority - delta));  // image@0x2A592

        if (elevator < 0)                                            // image@0x2A596
        {
            elevator = 0;                                            // image@0x2A598
        }

        aircraft.ElevatorAuthority = elevator;
    }

    /// <summary>
    /// "AILERONS DAMAGED" — <c>image@0x0F984..0x0F990</c>: halve all FOUR words of the roll-DEAD
    /// control block, i.e. the whole roll axis' authority.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <remarks>
    /// <para>
    /// The arm of <see cref="Model.Combat.PlayerDamageEffect.AileronsDamaged"/> (index <c>0x06</c>
    /// of the 24-word jump table at <c>image@0x0FBD5</c>).  Four <c>sar word ptr [imm16], 1</c>:
    /// <c>[0xEFD0]</c> = master <c>+0x38</c> (<see cref="ControlAxisState.HiBound"/>),
    /// <c>[0xEFD2]</c> = <c>+0x3A</c> (<see cref="ControlAxisState.LoBound"/>),
    /// <c>[0xEFD4]</c> = <c>+0x3C</c> (<see cref="ControlAxisState.BaseDir"/>),
    /// <c>[0xEFD6]</c> = <c>+0x3E</c> (<see cref="ControlAxisState.DirStep"/>) — the roll-dead
    /// instance's base is master <c>+0x30</c>
    /// (<see cref="ControlAxisState.RollDeadMasterOffset"/>).
    /// </para>
    /// <para>
    /// This corrects <see cref="ControlAxisState.BaseDir"/>'s remark that the field is "a
    /// per-aircraft constant that arrives with the <c>.fmd</c> and is <b>never re-written at
    /// runtime</b>" — for the roll-dead instance it is, once, by this arm.
    /// </para>
    /// <para>
    /// <c>sar</c> is an ARITHMETIC shift, so a negative bound halves toward zero-minus-one in the
    /// usual two's-complement way (<c>-210 → -105</c>, <c>-1 → -1</c>).
    /// </para>
    /// </remarks>
    public static void HalveAileronAuthority(Aircraft aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        ControlAxisState axis = aircraft.RollDeadAxis;
        axis.HiBound = Halve(axis.HiBound);                          // image@0x0F984
        axis.LoBound = Halve(axis.LoBound);                          // image@0x0F988
        axis.BaseDir = Halve(axis.BaseDir);                          // image@0x0F98C
        axis.DirStep = Halve(axis.DirStep);                          // image@0x0F990
    }

    /// <summary>
    /// "ELEVATORS DAMAGED" — <c>image@0x0F96C..0x0F970</c>: halve the pitch axis' two BOUNDS.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <remarks>
    /// The arm of <see cref="Model.Combat.PlayerDamageEffect.ElevatorsDamaged"/> (index
    /// <c>0x05</c>).  Two <c>sar word ptr [imm16], 1</c>: <c>[0xF076]</c> = master <c>+0xDE</c>
    /// (<see cref="ControlAxisState.HiBound"/> of the pitch instance, whose base is <c>+0xD6</c>)
    /// and <c>[0xF078]</c> = <c>+0xE0</c> (<see cref="ControlAxisState.LoBound"/>).
    /// <b>Asymmetric with <see cref="HalveAileronAuthority"/>:</b> the pitch arm halves 2 of the 4
    /// words, the roll arm all 4 — <c>+0xE2</c>/<c>+0xE4</c> are untouched.
    /// </remarks>
    public static void HalveElevatorBounds(Aircraft aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        ControlAxisState axis = aircraft.PitchAxis;
        axis.HiBound = Halve(axis.HiBound);                          // image@0x0F96C
        axis.LoBound = Halve(axis.LoBound);                          // image@0x0F970
    }

    /// <summary>
    /// The third master write of the "WING DAMAGED" arm — <c>image@0x0F9C9</c>:
    /// <c>shl word ptr [0xF084], 1</c>, i.e. DOUBLE
    /// <see cref="Aircraft.InducedDragCoefficient"/> (master <c>+0xEC</c>).
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <remarks>
    /// The full arm (<see cref="Model.Combat.PlayerDamageEffect.WingDamaged"/>, index <c>0x08</c>,
    /// <c>image@0x0F9A0..0x0F9CD</c>) posts its message, then calls
    /// <see cref="Apply"/> twice — <c>(Hull, 100)</c> @<c>image@0x0F9B6</c> and
    /// <c>(RollAuthority, 100)</c> @<c>image@0x0F9C4</c> — and finishes with this shift.  A wing
    /// hit therefore does all three: elevator authority to zero, roll gain doubled, induced drag
    /// doubled.  The shift is a 16-bit <c>shl</c> and wraps.
    /// </remarks>
    public static void DoubleInducedDrag(Aircraft aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        aircraft.InducedDragCoefficient =
            unchecked((short)(aircraft.InducedDragCoefficient << 1));   // image@0x0F9C9
    }

    /// <summary>
    /// The fuel leak of a damaged tank — <c>engagement_per_frame_tick @image@0x0FC9F..0x0FCB3</c>:
    /// subtract the accumulated leak rate from <see cref="Aircraft.Fuel"/> and zero it when it goes
    /// negative.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="leakRate">The i32 leak rate <c>[0xF1CE]/[0xF1D0]</c>.</param>
    /// <returns><see langword="true"/> when the tanks just ran dry (the original then posts the
    /// cockpit string at <c>0x453D:0x01DF</c> once, latched on <c>[0xF1D2]</c>).</returns>
    /// <remarks>
    /// <para>
    /// Gated by the caller on <c>[0xF1E1] != 0</c> (<c>image@0x0FC91</c>) and on the frame counter
    /// being ODD (<c>mov si,[0xF0C8] ; test si,1</c> @<c>image@0x0FC6A..0x0FC78</c>), so it runs at
    /// most every other frame.
    /// </para>
    /// <para>
    /// The leak RATE is what <see cref="Model.Combat.PlayerDamageEffect.FuelTankDamaged"/> grows:
    /// <c>mov ax,[0xF060]; shl ax,1; cdq; add [0xF1CE],ax; adc [0xF1D0],dx</c>
    /// (<c>image@0x0F91B..0x0F925</c>) — <c>[0xF060]</c> is master <c>+0xC8</c>, the fuel capacity
    /// in pounds, so each tank hit adds <c>2 ×</c> capacity to the per-two-frame drain.
    /// </para>
    /// <para>
    /// The clamp tests the HIGH word only (<c>cmp word ptr [0xF05A],0 ; jge</c>
    /// @<c>image@0x0FCA7</c>), which is equivalent to testing the i32's sign, and then zeroes both
    /// words (<c>image@0x0FCB0/0x0FCB3</c>).  The 32-bit subtraction is <c>sub</c>/<c>sbb</c> and
    /// wraps.
    /// </para>
    /// </remarks>
    public static bool DrainLeakingFuel(Aircraft aircraft, int leakRate)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        int fuel = unchecked(aircraft.Fuel - leakRate);              // image@0x0FC9F/0x0FCA3

        if (fuel < 0)                                                // image@0x0FCA7/0x0FCAC
        {
            fuel = 0;                                                // image@0x0FCB0/0x0FCB3
        }

        aircraft.Fuel = fuel;
        return fuel == 0;                                            // image@0x0FCB6..0x0FCBD
    }

    /// <summary>One <c>sar word ptr [imm16], 1</c>: an arithmetic halving of a 16-bit signed word.</summary>
    /// <param name="value">The word.</param>
    /// <returns>The halved word.</returns>
    private static short Halve(short value) => unchecked((short)(value >> 1));
}
