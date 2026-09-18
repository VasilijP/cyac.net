using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Lifecycle;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>What one <see cref="PlayerEject.Execute"/> did.</summary>
/// <param name="Ejected">Whether the arm ran at all (the in-flight key gate let it through).</param>
/// <param name="AirspeedFps">The airspeed the handle was pulled at, <c>g_airspeed [0xEF99]</c>.</param>
/// <param name="AboveSafeSpeed">Whether that airspeed was over the manual's 500 mph.</param>
/// <param name="CoinFlip">
/// The <c>prng_rand8</c> draw, or −1 when the speed was safe and no draw was made.
/// </param>
/// <param name="Fatal">Whether the coin flip armed the destroyed flag — the pilot did not make it.</param>
/// <param name="SlotAddress">The destruction slot <c>slot_alloc_and_activate</c> used, or 0.</param>
/// <param name="PilotObjectRef">The slot's <c>ext_ptr_a</c> — the ejecting pilot's pool object.</param>
public readonly record struct PlayerEjectResult(
    bool Ejected,
    short AirspeedFps,
    bool AboveSafeSpeed,
    int CoinFlip,
    bool Fatal,
    ushort SlotAddress,
    ushort PilotObjectRef);

/// <summary>
/// <b>Shift-E — EJECT</b>: the keyboard-ladder arm at <c>image@0x012F8..0x0134B</c>, with the
/// manual's 500-mph rule byte-exact.
/// </summary>
/// <remarks>
/// <para>
/// H6c §1.4 identified this arm: walking <c>mission_state_machine</c>'s <c>dec ax</c>/<c>sub ax,N</c>
/// key ladder from <c>image@0x0E27</c> gives command <b>69</b> = <c>0x45</c> = <c>'E'</c> shifted,
/// and the manual says *"press Shift-E to eject"* and *"You can only eject safely at speeds of
/// 500 mph or less — at higher speeds, you risk ripping your head from your shoulders"*
/// (<c>Chuck-Yeagers-Air-Combat_Manual_DOS_EN.pdf</c> p.53).  <c>0x2DD = 733</c> and
/// <c>500 mph × 5280 / 3600 = 733.33 ft/s</c>, so the threshold is the manual's, and the "risk" is a
/// literal <c>rand8 &lt; 0x80</c> coin flip.
/// </para>
/// <para>
/// The whole arm, re-derived from the bytes for H9:
/// </para>
/// <code>
/// 012F8 cmp byte [0xc31c],0 / jne 0x1302; the in-flight key gate: NON-ZERO runs the arm
/// 012FF  jmp 0xe08                              ;   zero drops the key back to the ladder loop
/// 01302  cmp word [0xef99],0x2dd / jle 0x1319   ; airspeed vs 733 ft/s
/// 0130A  lcall prng_rand8 @image@0x19F06
/// 0130F  cmp ax,0x80 / jge 0x1319
/// 01314  lcall destroyed_flag_set_and_frame_deadline_arm @image@0x1004A
/// 01319  push [0xc0]                            ; parentRef
/// 0131D  les bx,[0xef1e] ; push es:[bx+0x27] / push es:[bx+0x25]   ; the debris seed
/// 01329  mov al,1 / mov [0xc32f],al / push ax   ; playerOwned = 1
/// 0132F  mov al,[0xef2e] / and al,0x40 / push ax ; kindFlag
/// 01335  mov al,1 / push ax                     ; stageFlag
/// 01338  lcall slot_alloc_and_activate @image@0x2C4F6
/// 0133D  lcall sfx_play_tone16_random @image@0x29AF2
/// 01342  mov word [0xbc],0                      ; g_lockon_target := 0
/// 01348  mov ax,[0xc0] / mov [0xbe],ax
/// 0134E and byte [0xf1cb],0xf8; the tail H6c's transcript did not carry
/// </code>
/// <para>
/// <b>The call order is load-bearing.</b> <c>destroyed_flag_set_and_frame_deadline_arm
/// @image@0x1004A</c> opens <c>cmp byte [0xc32f],0 / jne → retf</c>, so it does nothing once the
/// pilot has left; the arm calls it at <c>0x01314</c>, BEFORE it writes <c>[0xC32F]:= 1</c> at
/// <c>0x0132B</c>.  Reversed, the 500-mph coin flip could never kill anyone.
/// </para>
/// <para>
/// The SEED the debris integrators are built from is the unaligned <c>i32</c> at the player's
/// engagement block <c>+0x25</c>, which <c>flight_engine_per_frame_top</c> refreshes every frame from
/// <c>[0xEF98]</c> — the accumulator whose middle word <c>[0xEF99]</c> IS the airspeed
/// (<c>image@0x22960..0x22972</c>).  The wreck's debris is thrown at the speed the aeroplane was
/// doing.
/// </para>
/// </remarks>
public static class PlayerEject
{
    /// <summary>
    /// <c>g_in_flight_key_gate [0xC31C]</c> — the DERIVED latch every ladder arm opens with (<c>cmp
    /// byte [0xc31c],0 / jne</c> @<c>image@0x012F8</c>).
    /// </summary>
    /// <remarks>
    /// <b>Polarity: 1 accepts, 0 drops.</b>  Its only writers are
    /// <c>image@0x00DE2..0x00DFE</c>, which set it to 1 exactly when
    /// <c>[0xEE58] == 0 &amp;&amp; [0xC32F] == 0 &amp;&amp; [0xC316] &lt; 2</c> — the player is flying,
    /// has not already left the aeroplane and is not mid-load.  So a pilot cannot eject twice, and a
    /// pilot whose slot has already been destroyed cannot eject at all.
    /// </remarks>
    public const int InFlightKeyGate = 0xC31C;

    /// <summary>
    /// <c>g_aircraft_master_struct [0xEF98]</c> — the FLIGHT MASTER's DGROUP base.
    /// </summary>
    /// <remarks>
    /// Which is why <c>[0xEF99]</c> is the airspeed: it is <c>master[+0x01]</c>, the unaligned
    /// middle word of <c>master[+0x00] vel_forward_i32</c> (<c>Aircraft.CurrentAirspeedFps</c>),
    /// and <c>[0xEF98]</c>/<c>[0xEF9A]</c> together ARE that i32.  The port keeps the master as an
    /// object rather than as a DGROUP window, so the two values are passed in from
    /// <c>FlightKernelState</c> instead of read out of the register file.
    /// </remarks>
    public const int AircraftMasterBase = 0xEF98;

    /// <summary><c>g_airspeed [0xEF99]</c> = <c>master[+0x01]</c>, ft/s.</summary>
    public const int AirspeedWord = 0xEF99;

    /// <summary>
    /// <c>0x2DD</c> = 733 ft/s = 500 mph — the manual's safe-ejection ceiling
    /// (<c>cmp word [0xef99],0x2dd</c> @<c>image@0x01302</c>, a signed <c>jle</c>, so the arm is safe
    /// at 733 and risks the pilot only ABOVE it).
    /// </summary>
    public const short SafeAirspeedLimitFps = 0x2DD;

    /// <summary>The coin flip's threshold: <c>cmp ax,0x80 / jge</c> (<c>image@0x0130F</c>).</summary>
    public const int CoinFlipThreshold = 0x80;

    /// <summary>
    /// <c>g_player_not_flying_flag [0xC32F]</c> — an earlier pass closed its semantics on exactly this arm.
    /// </summary>
    public const int PlayerNotFlyingFlag = 0xC32F;

    /// <summary><c>g_player_slot_destroyed_flag [0xEE58]</c>.</summary>
    public const int PlayerSlotDestroyedFlag = 0xEE58;

    /// <summary><c>g_session_end_deadline_frame [0xC390]</c>.</summary>
    public const int SessionEndDeadlineFrame = 0xC390;

    /// <summary><c>g_master_frame_counter [0xF0C8]</c>.</summary>
    public const int MasterFrameCounter = 0xF0C8;

    /// <summary>The grace the deadline arm gives: <c>add ax,4</c> (<c>image@0x10059</c>).</summary>
    public const int DeadlineGraceFrames = 4;

    /// <summary><c>g_lockon_target [0x00BC]</c>, zeroed by the tail.</summary>
    public const int LockOnTargetWord = 0x00BC;

    /// <summary><c>[0x00BE]</c> — the view subject the tail points at the player's own object.</summary>
    public const int ViewSubjectWord = 0x00BE;

    /// <summary><c>g_alt_object_farptr [0x00C0]</c> — the player's own pool object.</summary>
    public const int PlayerObjectWord = 0x00C0;

    /// <summary><c>g_player_engagement_block_farptr_off [0xEF1E]</c>.</summary>
    public const int PlayerEngagementBlockWord = 0xEF1E;

    /// <summary>The seed's unaligned offset inside that block (<c>es:[bx+0x25]</c>).</summary>
    public const int EngagementBlockSeedOffset = 0x25;

    /// <summary><c>[0xEF2E]</c> — the status byte whose bit 6 becomes the slot's kind flag.</summary>
    public const int PlayerStatusByte = 0xEF2E;

    /// <summary>The bit of it the arm keeps: <c>and al,0x40</c> (<c>image@0x01332</c>).</summary>
    public const byte KindFlagMask = 0x40;

    /// <summary>The slot's <c>stageFlag</c>: <c>mov al,1 / push ax</c> (<c>image@0x01335</c>).</summary>
    public const byte StageFlag = 1;

    /// <summary>
    /// <c>[0xF1CB]</c> — one of the project's known DUAL-USE globals; the tail clears its three low
    /// bits (<c>and byte [0xf1cb],0xf8</c>, <c>image@0x0134E</c>).
    /// </summary>
    public const int InputModeBits = 0xF1CB;

    /// <summary>The mask it is ANDed with.</summary>
    public const byte InputModeKeepMask = 0xF8;

    /// <summary>Runs the Shift-E arm.</summary>
    /// <param name="lifecycle">The lifecycle context <c>slot_alloc_and_activate</c> needs.</param>
    /// <param name="airspeedFps">
    /// <c>g_airspeed [0xEF99]</c> = <c>master[+0x01]</c> — the port's
    /// <c>Aircraft.CurrentAirspeedFps</c>, since the port keeps the master as an object.
    /// </param>
    /// <param name="forwardVelocity">
    /// <c>master[+0x00] vel_forward_i32</c> — the i32 the frame loop copies into the player's
    /// engagement block <c>+0x25</c> (<c>image@0x22960..0x22972</c>) and the arm pushes as
    /// <c>slot_alloc_and_activate</c>'s SEED.  Passed in for the same reason.
    /// </param>
    /// <returns>What happened.</returns>
    public static PlayerEjectResult Execute(
        EngagementLifecycleContext lifecycle, int airspeedFps, int forwardVelocity)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        CombatRegisters registers = lifecycle.Registers;

        // image@0x012F8 — the gate every ladder arm opens with.  1 ACCEPTS: image@0x00DE2 sets it
        // when the player is flying, has not already ejected and is not mid-load.
        if (registers.Byte(InFlightKeyGate) == 0)
        {
            return new PlayerEjectResult(false, 0, false, -1, false, 0, 0);
        }

        short airspeed = unchecked((short)airspeedFps);
        bool aboveSafe = airspeed > SafeAirspeedLimitFps;              // image@0x01302, jle skips
        int coinFlip = -1;
        bool fatal = false;
        if (aboveSafe)
        {
            coinFlip = lifecycle.Random.Rand8();                        // image@0x0130A
            if (coinFlip < CoinFlipThreshold)                           // image@0x0130F, jge skips
            {
                fatal = true;
                ArmDestroyedFlagDeadline(registers);                    // image@0x01314
            }
        }

        ushort parent = registers.Word(PlayerObjectWord);               // image@0x01319
        int seed = ReadSeed(lifecycle, forwardVelocity);                // image@0x0131D..0x01325
        registers.SetByte(PlayerNotFlyingFlag, 1);                      // image@0x0132B
        byte kind = (byte)(registers.Byte(PlayerStatusByte) & KindFlagMask);  // image@0x01332

        ushort slot = ObjectSlotPool.Allocate(
            lifecycle, StageFlag, kind, playerOwned: 1, seed, parent);  // image@0x01338

        // image@0x0133D — sfx_play_tone16_random; the PoC has no audio path yet.
        registers.SetWord(LockOnTargetWord, 0);                         // image@0x01342
        registers.SetWord(ViewSubjectWord, parent);                     // image@0x0134B
        registers.SetByte(
            InputModeBits, (byte)(registers.Byte(InputModeBits) & InputModeKeepMask));  // image@0x0134E

        ushort pilot = registers.Word(slot + ObjectSlotPool.ExtPointerA);
        return new PlayerEjectResult(true, airspeed, aboveSafe, coinFlip, fatal, slot, pilot);
    }

    /// <summary>
    /// <c>destroyed_flag_set_and_frame_deadline_arm @image@0x1004A</c> — three stores behind one gate.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <remarks>
    /// <code>
    /// 1004A  cmp byte [0xc32f],0 / jne 0x1005f
    /// 10051  mov byte [0xee58],1
    /// 10056  mov ax,[0xf0c8] / add ax,4 / mov [0xc390],ax
    /// 1005F  retf
    /// </code>
    /// The port's <c>IPlayerCombatEvents.ArmDestroyedFlagDeadline</c> seam still only COUNTS this
    /// call (the sustain tick's mission-abort arm, <c>image@0x0FE1D</c>) D3.
    /// This is its body, cited, for the one door another part of the port owns.
    /// </remarks>
    public static void ArmDestroyedFlagDeadline(CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        if (registers.Byte(PlayerNotFlyingFlag) != 0)                   // image@0x1004A
        {
            return;
        }

        registers.SetByte(PlayerSlotDestroyedFlag, 1);                  // image@0x10051
        registers.SetWord(
            SessionEndDeadlineFrame,
            unchecked((ushort)(registers.Word(MasterFrameCounter) + DeadlineGraceFrames)));
    }

    /// <summary>The unaligned <c>i32</c> at the player's engagement block <c>+0x25</c>.</summary>
    /// <param name="lifecycle">The lifecycle context (for the arena the block lives in).</param>
    /// <param name="forwardVelocity">
    /// The value <c>flight_engine_per_frame_top</c> would have written there this frame.
    /// </param>
    /// <remarks>
    /// The block field and its writer are BOTH modelled: <c>image@0x22960..0x22972</c> refreshes
    /// <c>block[+0x25]</c> from <c>master[+0x00]</c> every frame, and the port's frame loop does not
    /// run that copy, so the caller supplies the value and the port WRITES it into the block first —
    /// which keeps the block itself truthful for anything else that reads it.
    /// </remarks>
    private static int ReadSeed(EngagementLifecycleContext lifecycle, int forwardVelocity)
    {
        ushort block = lifecycle.Registers.Word(PlayerEngagementBlockWord);
        if (block == 0 || !lifecycle.Arena.Covers(block, EngagementBlockSeedOffset + 4))
        {
            return forwardVelocity;
        }

        lifecycle.Arena.SetWord(
            (ushort)(block + EngagementBlockSeedOffset), unchecked((ushort)forwardVelocity));
        lifecycle.Arena.SetWord(
            (ushort)(block + EngagementBlockSeedOffset + 2),
            unchecked((ushort)(forwardVelocity >> 16)));

        ushort low = lifecycle.Arena.Word((ushort)(block + EngagementBlockSeedOffset));
        ushort high = lifecycle.Arena.Word((ushort)(block + EngagementBlockSeedOffset + 2));
        return unchecked((int)(((uint)high << 16) | low));
    }
}
