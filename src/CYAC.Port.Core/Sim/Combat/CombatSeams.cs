using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// The engagement-prototype table — the DGROUP-resident <c>s_engagement_class_proto</c> records an
/// engagement block's <c>+0x00</c> points at.
/// </summary>
/// <remarks>
/// <para>
/// These records live in DGROUP at roughly <c>0x1812..0x24E8</c> (measured on the reference arena's
/// five distinct prototypes) — OUTSIDE every window a combat trace dumps.  They are mission-load
/// constants, so a seam is the honest shape: the port's own runtime will hand the kernel a table
/// built from the transformed data tree, and a verification test hands it one built from the L1
/// image it locates by walk-up.
/// </para>
/// <para>
/// A trace that carried the prototype region would let a
/// verification close over it instead of importing it.
/// </para>
/// </remarks>
public interface IEngagementPrototypes
{
    /// <summary>
    /// The prototype's <c>+0x0C</c> WORD.  Two readers with different widths use it: the expiry
    /// loop's engage-capable gate tests the low byte's bit3
    /// (<c>test byte ptr [bx+0xc],8</c> @<c>image@0x073D8</c>) and the snapshot's arc-parameter arm
    /// tests the whole word's low two bits (<c>mov ax,[si+0xc] / and ax,3</c>
    /// @<c>image@0x022EE</c>), so the seam returns the word and the callers mask.
    /// </summary>
    /// <param name="prototypeRef">The prototype's DGROUP near offset.</param>
    ushort FlagsWord(ushort prototypeRef);

    /// <summary>
    /// The prototype's arc-parameter block — what <c>engagement_state_snapshot</c> copies to
    /// <c>[0xED8E]</c> after dereferencing <c>prototype[+0x26]</c>
    /// (<c>mov si,[si+0x26]</c> @<c>image@0x02305</c>).
    /// </summary>
    /// <param name="prototypeRef">The prototype's DGROUP near offset.</param>
    /// <param name="length">28 or 2 — chosen by <see cref="FlagsWord"/>'s low two bits.</param>
    ReadOnlySpan<byte> ArcParameters(ushort prototypeRef, int length);

    /// <summary>
    /// The prototype's <c>+0x09</c> byte — the hit points a fresh block is seeded with
    /// (<c>mov cl,[si+9] / mov [bx+4],cl</c> @<c>image@0x07050</c>), whose <c>0xFF</c> value is the
    /// "this class never engages" sentinel the damage resolver tests
    /// (<c>cmp byte ptr [bx+9],0xff</c> @<c>image@0x0BDD8</c>).
    /// </summary>
    /// <param name="prototypeRef">The prototype's DGROUP near offset.</param>
    byte InitialHitPoints(ushort prototypeRef);

    /// <summary>
    /// The prototype's <c>+0x16..+0x19</c> init parameters, memcpy-ed into a block's <c>+0x12</c> at
    /// arm time (<c>image@0x070B1..0x070BF</c>).
    /// </summary>
    /// <param name="prototypeRef">The prototype's DGROUP near offset.</param>
    EngagementInitParams InitParams(ushort prototypeRef);

    /// <summary>
    /// The prototype's <c>+0x2D</c> byte — the engagement's lifetime in SECONDS, multiplied by 60
    /// to make a frame deadline (<c>mov al,0x3c / mul byte ptr [bx+0x2d]</c> @<c>image@0x070C9</c>).
    /// </summary>
    /// <param name="prototypeRef">The prototype's DGROUP near offset.</param>
    byte ExpirySeconds(ushort prototypeRef);

    /// <summary>
    /// The prototype's <c>+0x28</c> byte — non-zero means "engagements of this class count towards
    /// the player's pressure counters".
    /// </summary>
    /// <remarks>
    /// It is the second of three conditions on both player-pressure counters:
    /// <c>cmp byte ptr [bx+0x28],0 / je</c> in
    /// <c>engagement_player_F122_decrement @image@0x0C2A4</c>,
    /// <c>engagement_player_F128_decrement @image@0x0C303</c> and their two increment twins at
    /// <c>image@0x0C284</c> and <c>image@0x0C2CB</c>.
    /// </remarks>
    byte CountsTowardsPlayerPressure(ushort prototypeRef);

    /// <summary>
    /// The first byte of the record the prototype's <c>+0x0E</c> pointer array selects with a
    /// block's <c>+0x10</c> slot index — <c>image@0x0C254..0x0C268</c>:
    /// <c>si = block[+0x10] * 2; return word[prototype + 0x0E + si]</c>, and the caller tests
    /// <c>*(byte*)result == 1</c>.
    /// </summary>
    /// <param name="prototypeRef">The prototype's DGROUP near offset.</param>
    /// <param name="slotIndex">The block's <c>+0x10</c> type-slot index.</param>
    byte SlotDescriptorTag(ushort prototypeRef, byte slotIndex);
}

/// <summary>
/// The per-node pass the expiry loop makes for a node that is still alive —
/// <c>weapon_fire_combat_loop_per_shot @image@0x0416C</c>, the 14-arm FSM.
/// </summary>
/// <remarks>
/// <para>
/// It has EXACTLY ONE door image-wide: the <c>call 0x416c</c> at <c>image@0x073E5</c> inside
/// <c>engagement_expiry_loop</c> (re-verified here with the 64-KB wrap rule).  So the whole FSM is
/// reachable only through this seam, which is why C1 can verify the loop's own bookkeeping before
/// C3b exists: a verification implementation installs the recorded post-call state from the trace's
/// P2 exit record.
/// </para>
/// <para>
/// It takes no arguments and returns nothing: the original reads and writes the DGROUP scratch and
/// the arena in place.  The seam is handed both so an implementation has exactly what the original
/// callee reads and nothing more.
/// </para>
/// </remarks>
public interface IEngagementNodePass
{
    /// <summary>Runs the per-node FSM over the scratch and the arena.</summary>
    /// <param name="registers">The combat register file — the VM scratch is <c>[0xED54]</c>.</param>
    /// <param name="arena">The pool arena.</param>
    /// <param name="nodeRef">The node the loop popped, for an implementation that needs it.</param>
    void Run(CombatRegisters registers, PoolArena arena, ushort nodeRef);
}

/// <summary>A node pass that does nothing — for a loop assembled before C3b lands.</summary>
/// <remarks>
/// Deliberately not a silent default: a caller has to name it, so "the FSM did not run" is always a
/// visible decision in the calling code (the K5 convention).
/// </remarks>
public sealed class NoEngagementNodePass : IEngagementNodePass
{
    /// <summary>The shared instance.</summary>
    public static NoEngagementNodePass Instance { get; } = new();

    /// <inheritdoc/>
    public void Run(CombatRegisters registers, PoolArena arena, ushort nodeRef)
    {
    }
}

/// <summary>
/// The notifications the combat kernel sends OUT of the simulation.  Every one of them is a call the
/// original makes into a subsystem the combat state does not depend on.
/// </summary>
public interface ICombatEvents
{
    /// <summary>
    /// <c>film_obj_departure_record @image@0x30C46</c> — record a DEPART event in the film
    /// (<c>lcall 0x401c:0xa86</c> @<c>image@0x0749A</c>, <c>BX</c> = the object).
    /// </summary>
    /// <param name="objectRef">The departing object's pool near offset.</param>
    void RecordDeparture(ushort objectRef);

    /// <summary>
    /// <c>slot_4x19_clear_for_owner @image@0xB582</c> — drop the object's rows from the 4x19 table
    /// (<c>lcall 0x108e:0xaca2</c> @<c>image@0x074A2</c>, <c>BX</c> = the object).
    /// </summary>
    /// <param name="objectRef">The departing object's pool near offset.</param>
    void ClearSubsystem4x19(ushort objectRef);

    /// <summary>
    /// <c>scene_setup_or_camera_reset @image@0x22643</c> with <c>AL = 3</c>
    /// (<c>lcall 0x324c:0x183</c> @<c>image@0x07413</c>) — the post-drain camera reset the kill flag
    /// arms.  Its <c>+0x122 := 0</c> write is the flight-end channel an earlier pass named.
    /// </summary>
    /// <param name="mode">The mode byte the original leaves in <c>AL</c> — always 3 at this site.</param>
    void ResetSceneCamera(byte mode);

    /// <summary>
    /// <c>destroyed_flag_set_and_frame_deadline_arm @image@0x1004A</c>
    /// (<c>lcall 0x108e:0xf76a</c> @<c>image@0x07422</c>) — taken only when the post-drain random
    /// draw comes up below <c>0x80</c>.
    /// </summary>
    void ArmDestroyedFlag();
}

/// <summary>A combat-event seam that discards every notification.</summary>
public sealed class NullCombatEvents : ICombatEvents
{
    /// <summary>The shared instance.</summary>
    public static NullCombatEvents Instance { get; } = new();

    /// <inheritdoc/>
    public void RecordDeparture(ushort objectRef)
    {
    }

    /// <inheritdoc/>
    public void ClearSubsystem4x19(ushort objectRef)
    {
    }

    /// <inheritdoc/>
    public void ResetSceneCamera(byte mode)
    {
    }

    /// <inheritdoc/>
    public void ArmDestroyedFlag()
    {
    }
}

/// <summary>
/// The random draws the C1 combat pieces make, each named after the original entry point.
/// </summary>
/// <remarks>
/// Under <c>det_v1</c> every <c>Sim.*</c> site shares ONE LFSR16 word, <c>g_prng_state_lo
/// [0x07A8]</c>, so the draw COUNT and ORDER are part of the proof: a trace record carries the
/// word before and after every stage, and the producer prints an exact per-stage advance count
/// from the LFSR's discrete log.  Every draw therefore goes through this seam so a verification
/// test can drive it from the recorded word.
/// </remarks>
public interface ICombatRandom
{
    /// <summary>
    /// <c>prng_rand8 @image@0x19F06</c> — eight bits (itself <c>prng_rand_bits(8)</c>).  The expiry
    /// loop draws it once, in the post-drain kill arm (<c>lcall 0x201d:0x9d36</c>
    /// @<c>image@0x07418</c>).
    /// </summary>
    byte Rand8();

    /// <summary>
    /// <c>prng_rand_bounded @image@0x19F0E</c> with <c>AX = bound</c> — the draw
    /// <c>engagement_list_node_expiry_init</c> makes with <c>bound = 12</c>
    /// (<c>mov ax,0xc</c> @<c>image@0x0709C</c>).
    /// </summary>
    /// <param name="bound">The exclusive upper bound the original passes in <c>AX</c>.</param>
    int RandBounded(int bound);
}

/// <summary>
/// The default combat RNG: the LFSR16 word lives IN the register file at <c>[0x07A8]</c>, exactly
/// where the original keeps it.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the RNG verifiable without any oracle feeding: the kernel draws from the same
/// word the trace records, so a stage's POST-state word proves the draw count and order outright.
/// <c>prng_lfsr_step</c> is <c>shr word [0x07A8],1</c> then <c>xor byte [0x07A9],0xB4</c> on carry
/// (<c>image@0x19ED7</c>/<c>0x19EDF</c>) — <see cref="Primitives.Lfsr16"/>'s exact recurrence.
/// </para>
/// <para>
/// Under the det era all <c>Sim.*</c> sites share this one word, so a shared register file is also
/// the correct STREAM model, not just a convenience.
/// </para>
/// </remarks>
/// <param name="registers">The register file whose <c>[0x07A8]</c> word the draws advance.</param>
public sealed class RegisterFileCombatRandom(CombatRegisters registers) : ICombatRandom
{
    private readonly CombatRegisters _registers =
        registers ?? throw new ArgumentNullException(nameof(registers));

    /// <summary>How many <c>prng_lfsr_step</c> advances this instance has made.</summary>
    /// <remarks>The verification's own count, to compare against the producer's discrete-log one.</remarks>
    public long StepsTaken { get; private set; }

    /// <inheritdoc/>
    public byte Rand8()
    {
        Lfsr16 lfsr = Primitives.Lfsr16.FromRawState(_registers.RandomState);
        ushort result = lfsr.Rand8();
        _registers.RandomState = lfsr.State;
        StepsTaken += 8;
        return (byte)result;
    }

    /// <inheritdoc/>
    public int RandBounded(int bound)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bound);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bound, 0x8000);
        Lfsr16 lfsr = Primitives.Lfsr16.FromRawState(_registers.RandomState);
        ushort before = lfsr.State;
        ushort result = lfsr.RandBounded((ushort)bound);
        _registers.RandomState = lfsr.State;
        StepsTaken += CountSteps(before, lfsr.State);
        return result;
    }

    private static long CountSteps(ushort from, ushort to)
    {
        // The LFSR has period 65,535, so the advance count is a walk — used only for the
        // instrumentation counter, never for a decision.
        if (from == 0)
        {
            return 0;
        }

        Lfsr16 probe = Primitives.Lfsr16.FromRawState(from);
        for (long steps = 0; steps < Primitives.Lfsr16.Period; steps++)
        {
            if (probe.State == to)
            {
                return steps;
            }

            probe.Step(1);
        }

        return -1;
    }
}
