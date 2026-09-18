namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// The 0x2E-byte stack frame of <c>weapon_fire_combat_loop_per_shot @image@0x0416C</c> — a real,
/// addressable part of the FSM's state, not a private local.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  The frame is modelled BYTE-BACKED rather than as C# locals for two reasons the bytes
/// force:
/// </para>
/// <list type="number">
///   <item>
///     <b>The FSM hands POINTERS into it to four callees.</b> <c>lea bx,[bp-0x16]</c> at
///     <c>image@0x04280</c>, <c>0x04286</c>, <c>0x04379</c>, <c>0x043DC</c> and <c>0x0428C</c>
///     passes a near pointer that <c>engagement_pos_snapshot_load @image@0x0761A</c> WRITES and
///     <c>engagement_evasion_bearing_setup @image@0x0491E</c> READS — and since <c>SS == DS ==
///     DGROUP</c> (crt0-proven, ``), the callee dereferences it as an ordinary near pointer.  A
///     value-typed local could not be aliased that way.
///   </item>
///   <item>
///     <b>The same bytes are read at different widths by different arms.</b>  <c>[bp-0x2C]</c> is a
///     WORD in the case-A far-pointer stage (<c>image@0x0443D</c>) and in §F5's two dead stores
///     (<c>image@0x048A1</c>), and a BYTE flag in case 9 (<c>image@0x04596</c>,
///     <c>image@0x0464F</c>).
///   </item>
/// </list>
/// <para>
/// Offsets are given as the original writes them, <c>[bp-N]</c>, so a reader can put this file
/// beside the disassembly.  The frame is <c>sub sp,0x2E</c> (<c>image@0x0416F</c>) and the original
/// NEVER writes <c>[bp-2]</c> or <c>[bp-0x18..-0x24]</c>, and this model reproduces that by simply
/// not touching them.
/// </para>
/// </remarks>
public sealed class EngagementNodeFrame
{
    /// <summary>The frame's size — the original's <c>sub sp,0x2E</c> (<c>image@0x0416F</c>).</summary>
    public const int Bytes = 0x2E;

    private readonly byte[] _bytes = new byte[Bytes];

    /// <summary>The frame's raw bytes, lowest address (<c>[bp-0x2E]</c>) first.</summary>
    public ReadOnlySpan<byte> Raw => _bytes;

    /// <summary>
    /// The three-<c>i32</c> POSITION vector at <c>[bp-0x16]</c> — the block every pointer-taking
    /// callee is handed.
    /// </summary>
    /// <remarks>
    /// Its layout is the callee's: <c>[bx+0]</c> X low, <c>[bx+2]</c> X high, <c>[bx+4]</c> Y low,
    /// <c>[bx+8]</c> Z low (<c>image@0x04948..0x04950</c> and <c>image@0x07625..0x07639</c>), i.e.
    /// X at <c>[bp-0x16]</c>, Y at <c>[bp-0x12]</c> and Z at <c>[bp-0x0E]</c>.
    /// </remarks>
    public CombatPosition Vector
    {
        get => new(Int32(0x16), Int32(0x12), Int32(0x0E));
        set
        {
            SetInt32(0x16, value.X);
            SetInt32(0x12, value.Y);
            SetInt32(0x0E, value.Z);
        }
    }

    /// <summary>
    /// <c>[bp-8]</c> — the SPEED / rate word the evasion setup is called with
    /// (<c>image@0x0427D</c>, <c>image@0x043C5</c>, <c>image@0x043CF</c>, <c>image@0x043D9</c>).
    /// </summary>
    public ushort EvasionRate
    {
        get => Word(8);
        set => SetWord(8, value);
    }

    /// <summary>
    /// <c>[bp-4]</c> — the acquisition state <c>[0xED65]</c> latched by §F2 before the acq FSM runs
    /// (<c>image@0x047B8</c>), and compared by §F3 afterwards (<c>image@0x047F6</c>).
    /// </summary>
    public byte AcquisitionStateAtGate
    {
        get => Byte(4);
        set => SetByte(4, value);
    }

    /// <summary>A byte of the frame, addressed as the original does.</summary>
    /// <param name="bpOffset">The <c>N</c> of <c>[bp-N]</c>, 1..0x2E.</param>
    public byte Byte(int bpOffset) => _bytes[Index(bpOffset, 1)];

    /// <summary>Writes a byte of the frame.</summary>
    /// <param name="bpOffset">The <c>N</c> of <c>[bp-N]</c>.</param>
    /// <param name="value">The byte.</param>
    public void SetByte(int bpOffset, byte value) => _bytes[Index(bpOffset, 1)] = value;

    /// <summary>A little-endian word of the frame.</summary>
    /// <param name="bpOffset">The <c>N</c> of <c>[bp-N]</c>.</param>
    public ushort Word(int bpOffset)
    {
        int i = Index(bpOffset, 2);
        return (ushort)(_bytes[i] | (_bytes[i + 1] << 8));
    }

    /// <summary>Writes a little-endian word of the frame.</summary>
    /// <param name="bpOffset">The <c>N</c> of <c>[bp-N]</c>.</param>
    /// <param name="value">The word.</param>
    public void SetWord(int bpOffset, ushort value)
    {
        int i = Index(bpOffset, 2);
        _bytes[i] = (byte)value;
        _bytes[i + 1] = (byte)(value >> 8);
    }

    /// <summary>A signed word of the frame.</summary>
    /// <param name="bpOffset">The <c>N</c> of <c>[bp-N]</c>.</param>
    public short Signed(int bpOffset) => unchecked((short)Word(bpOffset));

    /// <summary>Writes a signed word of the frame.</summary>
    /// <param name="bpOffset">The <c>N</c> of <c>[bp-N]</c>.</param>
    /// <param name="value">The value.</param>
    public void SetSigned(int bpOffset, short value) => SetWord(bpOffset, unchecked((ushort)value));

    /// <summary>
    /// A signed 32-bit value: its LOW word at <c>[bp-N]</c> and its HIGH word at
    /// <c>[bp-(N-2)]</c> — the stack grows down, so the high word is at the HIGHER address.
    /// </summary>
    /// <param name="bpOffset">The <c>N</c> of the low word's <c>[bp-N]</c>.</param>
    public int Int32(int bpOffset) =>
        unchecked((int)(Word(bpOffset) | ((uint)Word(bpOffset - 2) << 16)));

    /// <summary>Writes a signed 32-bit value.</summary>
    /// <param name="bpOffset">The <c>N</c> of the low word's <c>[bp-N]</c>.</param>
    /// <param name="value">The value.</param>
    public void SetInt32(int bpOffset, int value)
    {
        SetWord(bpOffset, unchecked((ushort)value));
        SetWord(bpOffset - 2, unchecked((ushort)(value >> 16)));
    }

    private static int Index(int bpOffset, int length)
    {
        if (bpOffset < length || bpOffset > Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bpOffset),
                bpOffset,
                $"[bp-0x{bpOffset:X}] is outside the {Bytes}-byte frame "
                    + "weapon_fire_combat_loop_per_shot @image@0x0416F reserves");
        }

        return Bytes - bpOffset;
    }
}
