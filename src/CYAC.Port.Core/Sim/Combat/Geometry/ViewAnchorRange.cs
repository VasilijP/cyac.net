namespace CYAC.Port.Core.Sim.Combat.Geometry;

/// <summary>
/// <c>object_range_from_view_anchor @image@0x24448</c> — the saturated Manhattan distance from the
/// VIEW ANCHOR (the camera) to a world position, and the only reader combat has for the anchor.
/// </summary>
/// <remarks>
/// <para>
/// FAR, <c>retf 4</c>, one far-pointer argument (<c>les bx,[bp+6]</c> @<c>image@0x2444F</c>) that
/// points at an object's POSITION TRIPLE, i.e. the pool object's <c>+0x06</c>
/// (<c>lea ax,[si+6] / push es / push ax</c> @<c>image@0x03956..0x0395A</c>).  The body, in full:
/// </para>
/// <code>
/// 24452  mov ax,es:[bx]   / mov dx,es:[bx+2]     ; the object's X
/// 24459  sub ax,[0xD88E]  / sbb dx,[0xD890]      ; − the anchor's X
/// 24461  jns +7 / neg ax / adc dx,0 / neg dx     ; |ΔX| (a 32-bit negate)
/// 2446A  mov cx,ax / mov si,dx
/// 2446E  mov ax,es:[bx+8] / mov dx,es:[bx+0xA]   ; the object's Z (object +0x0E)
/// 24476  sub ax,[0xD896]  / sbb dx,[0xD898]      ; − the anchor's Z
/// 2447E  jns +7 / …                              ; |ΔZ| → [bp-8]
/// 2448D  mov ax,es:[bx+4] / mov dx,es:[bx+6]     ; the object's Y (object +0x0A)
/// 24495  sub ax,[0xD892]  / sbb dx,[0xD894]      ; − the anchor's Y
/// 2449D  jns +7 / …                              ; |ΔY|
/// 244A6  add ax,[bp-8] / adc dx,[bp-6]           ; + |ΔZ|
/// 244AC  add ax,cx     / adc dx,si               ; + |ΔX|
/// 244B0  mov al,ah / mov ah,dl / mov dl,dh       ; dx:ax >>= 8, ARITHMETIC…
/// 244B6  shl dh,1 / sbb dh,dh                    ; …the sign fill
/// 244BA  or dx,dx / je +3 / mov ax,0xFFFF        ; saturate to 0xFFFF
/// 244C1  … retf 4                                ; AX = the range
/// </code>
/// <para>
/// <b>Why this is ported now and was a seam before.</b> The six anchor words
/// <c>[0xD88E..0xD899]</c> were in NO combat-trace window, so a port seeded from a stage record did
/// not have them and C7 declared the routine a NAMED seam with a conservative <c>0xFFFF</c> answer,
/// attributing the spans that read it.  Trace format v1.6 added the stage window
/// <c>view_anchor_and_zoom [0xD88E]+22</c> (ask V1), so the inputs are on record and the routine is
/// arithmetic the port owns.
/// </para>
/// <para>
/// Its only combat reader is <c>weapon_fire_spawn_record_fill</c>'s PATH-A gate,
/// <c>cmp ax,0x7D0 / jae</c> @<c>image@0x03960</c>: a launcher within <c>0x07D0</c> of the camera
/// gets the modelled muzzle offset, one further away has its shot born at its own position.
/// </para>
/// </remarks>
public static class ViewAnchorRange
{
    /// <summary><c>s_view_anchor.x [0xD88E]</c> (i32).</summary>
    public const int AnchorX = 0xD88E;

    /// <summary><c>s_view_anchor.y [0xD892]</c> (i32).</summary>
    public const int AnchorY = 0xD892;

    /// <summary><c>s_view_anchor.z [0xD896]</c> (i32).</summary>
    public const int AnchorZ = 0xD896;

    /// <summary>The value the high-word test installs — <c>mov ax,0xFFFF</c> @<c>image@0x244BE</c>.</summary>
    public const ushort Saturated = 0xFFFF;

    /// <summary>
    /// The PATH-A threshold in <c>weapon_fire_spawn_record_fill</c> —
    /// <c>cmp ax,0x7d0 / jae</c> @<c>image@0x03960</c>.
    /// </summary>
    public const ushort PathAThreshold = 0x07D0;

    /// <summary>Reads the view anchor out of the register file.</summary>
    /// <param name="registers">The DGROUP combat register file.</param>
    /// <returns>The camera's world position.</returns>
    public static CombatPosition AnchorFrom(CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        return new CombatPosition(
            Int32At(registers, AnchorX), Int32At(registers, AnchorY), Int32At(registers, AnchorZ));
    }

    /// <summary>
    /// The routine, over a pool-arena position triple — the shape its only caller uses.
    /// </summary>
    /// <param name="registers">The DGROUP combat register file (the anchor).</param>
    /// <param name="arena">The pool arena the position lives in.</param>
    /// <param name="positionRef">The pushed near offset — an object's <c>+0x06</c>.</param>
    /// <returns>The original's <c>AX</c>.</returns>
    public static ushort Compute(CombatRegisters registers, PoolArena arena, ushort positionRef)
    {
        ArgumentNullException.ThrowIfNull(arena);
        CombatPosition position = new CombatPosition(
            Int32At(arena, positionRef),
            Int32At(arena, unchecked((ushort)(positionRef + 0x04))),
            Int32At(arena, unchecked((ushort)(positionRef + 0x08))));
        return Compute(position, AnchorFrom(registers));
    }

    /// <summary>The routine's arithmetic.</summary>
    /// <param name="position">The object's world position.</param>
    /// <param name="anchor">The view anchor's world position.</param>
    /// <returns>The original's <c>AX</c>.</returns>
    public static ushort Compute(CombatPosition position, CombatPosition anchor)
    {
        // The three |Δ| are 32-bit wrapping subtractions followed by a 32-bit negate on the sign of
        // the HIGH word (`jns` after `sbb`), which is exactly `unchecked` arithmetic plus abs.
        int dx = Abs32(unchecked(position.X - anchor.X));        // image@0x24459
        int dz = Abs32(unchecked(position.Z - anchor.Z));        // image@0x24476
        int dy = Abs32(unchecked(position.Y - anchor.Y));        // image@0x24495
        int sum = unchecked(dy + dz + dx);                       // image@0x244A6 / 0x244AC

        // `mov al,ah / mov ah,dl / mov dl,dh / shl dh,1 / sbb dh,dh` = an ARITHMETIC dx:ax >>= 8,
        // then `or dx,dx` tests the resulting HIGH word — i.e. `sum >> 24`.
        return (sum >> 24) != 0 ? Saturated : unchecked((ushort)(sum >> 8));
    }

    /// <summary>
    /// <c>neg ax / adc dx,0 / neg dx</c> — the 32-bit negate the three <c>jns</c> arms take.
    /// </summary>
    /// <param name="value">The signed difference.</param>
    /// <returns>Its absolute value, wrapping at <see cref="int.MinValue"/> as the original does.</returns>
    public static int Abs32(int value) => value < 0 ? unchecked(-value) : value;

    private static int Int32At(CombatRegisters registers, int offset) =>
        unchecked(registers.Word(offset) | (registers.Word(offset + 2) << 16));

    private static int Int32At(PoolArena arena, ushort offset) =>
        unchecked(arena.Word(offset) | (arena.Word(unchecked((ushort)(offset + 2))) << 16));
}
