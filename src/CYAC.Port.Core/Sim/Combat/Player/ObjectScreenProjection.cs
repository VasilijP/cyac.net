namespace CYAC.Port.Core.Sim.Combat.Player;

/// <summary>
/// <c>object_screen_pos_project @image@0x036EA</c> (P26) and the perspective divider it forwards to
/// — the lock-on's projection of one RENDER-LIST NODE to screen coordinates.
/// </summary>
/// <remarks>
/// <para>
/// <b>The divider is not static code.</b>  <c>object_screen_pos_project</c> writes nothing itself:
/// it widens the node's three position words into a 12-byte stack block and forwards
/// <c>[bp+4]</c>/<c>[bp+6]</c> to <c>gfx_perspective_project_trampoline @image@0x15B1C</c>, which
/// forwards to <c>gfx_trig_lookup_wrapper @image@0x1934E</c>, whose body is
/// </para>
/// <code>
/// 1934E  push bp / mov bp,sp / push si
/// 19352  mov ax,[bp+4]        ; the 12-byte vertex block
/// 19355  call 0x19366         ; ← 300 bytes of ZEROS in the L1 image
/// 19358  mov si,[bp+6] / mov [si],ax    ; the FIRST out pointer  ← AX
/// 1935D  mov si,[bp+8] / mov [si],cx    ; the SECOND out pointer ← CX
/// 19364  ret
/// </code>
/// <para>
/// <c>image@0x19366..0x19491</c> is a 300-byte JIT ARENA that
/// <c>gfx_projector_emit @image@0x19524</c> fills at run time whenever
/// <c>g_gfx_zoom_shift [0xE836]</c> changes (it caches the last value in <c>[0x0782]</c>):
/// </para>
/// <code>
/// 19527  mov bx,[0xE836] / cmp bx,[0x782] / je exit / mov [0x782],bx
/// 19535  mov ax,cs / mov ds,ax / mov es,ax             ; the arena IS the code segment
/// 1953B  mov word cs:[0x92CA],0x336                    ; re-target the template's jmp
/// 19542  mov si,0x92C2 / mov di,0x9196 / mov cx,0x15 / rep movsb    ; TEMPLATE 1 (21 B)
/// 1954D  call 0x194D6                                  ; …the SHIFT BLOCK, [0xE836] copies
/// 19559  mov si,0x92D8 / mov cx,0x11 / rep movsb       ; TEMPLATE 2 (17 B)
/// 19561  call 0x194D6                                  ; …the SHIFT BLOCK again
/// 19576  mov si,0x92EA / mov cx,0x0E / rep movsb       ; TEMPLATE 3 (14 B)
/// </code>
/// <para>
/// <c>CS</c> here is <c>0x201D</c>, so <c>cs:0x9196</c> IS <c>image@0x19366</c> and the three
/// template sources <c>cs:0x92C2</c> / <c>cs:0x92D8</c> / <c>cs:0x92EA</c> are
/// <c>image@0x19492</c> / <c>image@0x194A8</c> / <c>image@0x194BA</c> — the bytes immediately after
/// the arena.  Assembled, the emitted routine is
/// </para>
/// <code>
/// cmp byte [0xEA02],0 / je +3 / jmp image@0x196A6     ; ← the ALTERNATE divider
/// push si / push di / mov si,ax
/// mov di,[si+0x0A]        ; the DEPTH  = node[+0x0A]
/// mov ax,[si+6]  / cdq    ; the Y word = node[+0x08]
///   &lt;shift block&gt;
/// idiv di
/// mov cx,[0xE636] / sub cx,ax / jno +3 / mov cx,0x7F00
/// mov ax,[si+2]  / cdq    ; the X word = node[+0x06]
///   &lt;shift block&gt;
/// idiv di
/// add ax,[0xE634] / jno +3 / mov ax,0x7F00
/// pop di / pop si / ret                                ; AX = screen X, CX = screen Y
/// </code>
/// <para>
/// and the SHIFT BLOCK <c>gfx_projector_emit_shift @image@0x194D6</c> emits, for
/// <c>S = [0xE836]</c>:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>S &lt; 6</c> — <c>S</c> copies of <c>shl ax,1 / rcl dx,1</c> (<c>cs:0x92F8</c> =
///     <c>image@0x194C8</c>), i.e. an exact 32-bit <c>&lt;&lt; S</c>;
///   </description></item>
///   <item><description>
///     <c>6 &lt;= S &lt; 8</c> — one <c>mov dh,dl / mov dl,ah / mov ah,al</c> (<c>cs:0x9300</c> =
///     <c>image@0x194D0</c>) then <c>8 - S</c> copies of <c>sar dx,1 / rcr ax,1</c>
///     (<c>cs:0x92FC</c> = <c>image@0x194CC</c>);
///   </description></item>
///   <item><description>
///     <c>S &gt;= 8</c> — the same 6-byte block then <c>S - 8</c> copies of the <c>&lt;&lt; 1</c>.
///   </description></item>
/// </list>
/// <para>
/// The 6-byte block is <b>not</b> a clean <c>&lt;&lt; 8</c>: it moves <c>dl→dh</c>, <c>ah→dl</c>,
/// <c>al→ah</c> and leaves <c>al</c> alone, so the result is
/// <c>((v &lt;&lt; 8) &amp; 0xFFFFFFFF) | (v &amp; 0xFF)</c> — the low byte is a COPY of the old low
/// byte rather than zero.  That quirk is load-bearing: it is what makes the reconstruction
/// reproduce the machine on 15,090 of 15,090 recorded projections.
/// </para>
/// <para>
/// <b>Where the shift comes from.</b>  <c>[0xE836]</c> is
/// <c>polygon_fill_mesh_render_setup</c>'s <c>[bp+0x10]</c> argument
/// (<c>mov cx,[bp+0x10] / mov [0xE836],cx</c> @<c>image@0x1472B</c>), and the gameplay door pushes
/// <c>[0xD8A2] + [0xD8A0]</c> for it (<c>mov ax,[0xD8A2] / add ax,[0xD8A0] / push ax</c>
/// @<c>image@0x016BF..0x016C6</c>) — both inside the trace window
/// <c>view_anchor_and_zoom [0xD88E]+22</c>, which is why this routine is PORTABLE and not a seam.
/// </para>
/// </remarks>
public static class ObjectScreenProjection
{
    /// <summary>
    /// <c>g_gfx_screen_centre_x [0xE634]</c> — added to the projected X
    /// (<c>add ax,[0xE634]</c>, template 3).  Inside the window <c>g_gfx_clip_x_min [0xE628]+16</c>.
    /// </summary>
    public const int ScreenCentreX = 0xE634;

    /// <summary>
    /// <c>g_gfx_screen_centre_y [0xE636]</c> — the projected Y is SUBTRACTED from it
    /// (<c>mov cx,[0xE636] / sub cx,ax</c>, template 2).
    /// </summary>
    public const int ScreenCentreY = 0xE636;

    /// <summary><c>g_map_zoom_level [0xD8A0]</c> — the first half of the shift.</summary>
    public const int MapZoomLevel = 0xD8A0;

    /// <summary><c>g_view_zoom_shift_bias [0xD8A2]</c> — the second half.</summary>
    public const int ViewZoomShiftBias = 0xD8A2;

    /// <summary>
    /// <c>g_render_arena_mode_flag [0xEA02]</c> — non-zero routes the projection to the ALTERNATE
    /// divider at <c>image@0x196A6</c>.
    /// </summary>
    public const int RenderArenaModeFlag = 0xEA02;

    /// <summary>
    /// The value both saturating arms install on signed overflow (<c>mov cx,0x7F00</c> /
    /// <c>mov ax,0x7F00</c>, templates 2 and 3).
    /// </summary>
    public const short OverflowSentinel = 0x7F00;

    /// <summary>
    /// Reads the emitted routine's shift count out of the register file:
    /// <c>[0xD8A2] + [0xD8A0]</c>, the value the gameplay render door hands
    /// <c>polygon_fill_mesh_render_setup</c> as <c>[bp+0x10]</c> and it stores in <c>[0xE836]</c>.
    /// </summary>
    /// <param name="registers">The DGROUP combat register file.</param>
    /// <returns>The shift, as the 16-bit sum the machine forms.</returns>
    public static ushort ShiftFrom(CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        return unchecked((ushort)(registers.Word(ViewZoomShiftBias) + registers.Word(MapZoomLevel)));
    }

    /// <summary>
    /// Projects one render-list node, reading the camera state out of the register file.
    /// </summary>
    /// <param name="registers">The DGROUP combat register file.</param>
    /// <param name="node">The node's DGROUP near offset.</param>
    /// <returns>The screen pair — <c>X</c> is the emitted routine's <c>AX</c>, <c>Y</c> its <c>CX</c>.</returns>
    public static (short ScreenX, short ScreenY) Project(CombatRegisters registers, ushort node)
    {
        ArgumentNullException.ThrowIfNull(registers);
        short nodeX = unchecked((short)registers.Word(unchecked((ushort)(node + 0x06))));
        short nodeY = unchecked((short)registers.Word(unchecked((ushort)(node + 0x08))));
        short depth = unchecked((short)registers.Word(unchecked((ushort)(node + 0x0A))));
        short centreX = unchecked((short)registers.Word(ScreenCentreX));
        short centreY = unchecked((short)registers.Word(ScreenCentreY));
        int shift = ShiftFrom(registers);

        // cmp byte [0xEA02],0 / je +3 / jmp image@0x196A6 — the emitted routine's first act.
        if (registers.Byte(RenderArenaModeFlag) != 0)
        {
            return ProjectAlternate(nodeX, nodeY, depth, centreX, centreY, shift);
        }

        return Project(nodeX, nodeY, depth, centreX, centreY, shift);
    }

    /// <summary>
    /// The emitted routine at <c>image@0x19366</c>, over its five inputs — INCLUDING both arms of
    /// the shipped <c>#DE</c> fix-up its two <c>idiv</c>s rely on.
    /// </summary>
    /// <param name="nodeX"><c>node[+0x06]</c> — the vertex block's <c>[si+2]</c>.</param>
    /// <param name="nodeY"><c>node[+0x08]</c> — the vertex block's <c>[si+6]</c>.</param>
    /// <param name="depth"><c>node[+0x0A]</c> — the vertex block's <c>[si+0x0A]</c>, the divisor.</param>
    /// <param name="centreX"><c>[0xE634]</c>.</param>
    /// <param name="centreY"><c>[0xE636]</c>.</param>
    /// <param name="shift"><c>[0xE836]</c>.</param>
    /// <returns>The screen pair.</returns>
    /// <remarks>
    /// <para>
    /// <b>The two divides are EXPECTED to fault, and the game catches them.</b> <c>int00_install
    /// @image@0x1927E</c> points the <c>INT 00</c> vector at <c>int00_divzero_isr @image@0x191C8</c>,
    /// which dispatches by FAULT IP: <c>gfx_projector_emit</c> stamps the two <c>idiv</c> return
    /// addresses into <c>cs:[0x8FF6]</c> (the Y one, <c>mov ax,di / add ax,2</c> @<c>image@0x19550</c>)
    /// and <c>cs:[0x8FF4]</c> (the X one, @<c>image@0x19564</c>) as it emits them, and the epilogue's
    /// address into <c>cs:[0x8FF2]</c> (<c>add ax,0xB</c> @<c>image@0x1956D</c>).  So a quotient that
    /// does not fit in <c>AX</c> is not a crash — it is the SATURATION MECHANISM.
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>The Y divide</b> (<c>int00_subhandler_screen_y_fixup @image@0x1932C</c>):
    ///     <c>AX = 0x7F00</c>, negated once if <c>[si+7] &lt; 0</c> (the Y word's sign) and once if
    ///     <c>DI &lt; 0</c> — i.e. <c>±0x7F00</c> with <c>sign(numerator) XOR sign(divisor)</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The X divide</b> (<c>int00_subhandler_screen_x_fixup @image@0x192D6</c>): the same,
    ///     via <c>[si+3]</c>, whenever <c>DI != 0</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <b><c>DI == 0</c> — the DOUBLE-FAULT CASCADE</b> (<c>image@0x192F1..0x1932B</c>).  The
    ///     Y divide faults first and its handler leaves <c>±0x7F00</c>; then the X divide re-faults
    ///     with the same zero divisor and the X handler takes its EXTENDED arm: it patches the
    ///     stacked <c>CS:IP</c> to the JIT buffer's EPILOGUE (<c>cs:[0x8FF2]</c>) and sets BOTH
    ///     results from the numerators' TRI-STATE sign —
    ///     <c>&gt; 0 ⇒ 0x2710</c>, <c>== 0 ⇒</c> the screen centre, <c>&lt; 0 ⇒ 0xD8F0</c> (−10000)
    ///     — so neither saturating add nor subtract runs at all.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Measured over a recorded sortie: 40,412 X fix-ups and 22,235 Y fix-ups.  The port
    /// therefore SATURATES where the original saturates; it throws nowhere.
    /// </para>
    /// </remarks>
    public static (short ScreenX, short ScreenY) Project(
        short nodeX, short nodeY, short depth, short centreX, short centreY, int shift)
    {
        if (depth == 0)
        {
            // image@0x192F1..0x1932B — the cascade's own results, installed straight into AX/CX
            // with the IRET redirected to the epilogue.
            return (CascadeResult(nodeX, centreX), CascadeResult(nodeY, centreY));
        }

        // The emitted body divides Y FIRST (template 2), then X (template 3).
        short y = SubtractSaturating(centreY, Divide(nodeY, depth, shift));
        short x = AddSaturating(centreX, Divide(nodeX, depth, shift));
        return (x, y);
    }

    /// <summary>
    /// <c>image@0x19300..0x1932B</c> — the cascade arm's tri-state result for one component.
    /// </summary>
    /// <param name="numerator">The vertex word (<c>[si+2]</c> for X, <c>[si+6]</c> for Y).</param>
    /// <param name="centre">The screen centre for that component.</param>
    /// <returns>The installed result.</returns>
    public static short CascadeResult(short numerator, short centre) => numerator switch
    {
        > 0 => 0x2710,                              // mov ax,0x2710 @image@0x19306
        < 0 => unchecked((short)0xD8F0),            // mov ax,0xD8F0 @image@0x19314
        _ => centre,                                // mov ax,[0xE634] @image@0x1930E
    };

    /// <summary>
    /// <c>cdq</c>, the emitted shift block, then <c>idiv di</c> — the quotient in <c>AX</c>, or the
    /// INT-00 handler's <c>±0x7F00</c> when it does not fit.
    /// </summary>
    /// <param name="value">The vertex word.</param>
    /// <param name="depth">The divisor.</param>
    /// <param name="shift">The emitted shift count.</param>
    /// <returns>The 16-bit quotient, or the saturated sentinel.</returns>
    /// <exception cref="DivideByZeroException">
    /// <paramref name="depth"/> is 0 — the caller must take the cascade arm instead.
    /// </exception>
    public static short Divide(short value, short depth, int shift)
    {
        if (depth == 0)
        {
            throw new DivideByZeroException(
                "object_screen_pos_project: a zero depth word is the DOUBLE-FAULT CASCADE "
                    + "(int00_subhandler_screen_x_fixup's extended arm, image@0x192F1), which "
                    + "replaces the whole projection — see Project(short,…).");
        }

        int dividend = ShiftBlock(value, shift);
        int quotient = dividend / depth;                        // truncating IDIV — platform: Intel SDM
        if (quotient is >= short.MinValue and <= short.MaxValue)
        {
            return unchecked((short)quotient);
        }

        // #DE — int00_subhandler_screen_x_fixup / _y_fixup: 0x7F00, negated once per negative
        // operand (image@0x192DF..0x192EF, image@0x19331..0x19341).
        bool negative = (value < 0) ^ (depth < 0);
        return negative ? unchecked((short)-OverflowSentinel) : OverflowSentinel;
    }

    /// <summary>
    /// The emitted SHIFT BLOCK — <c>gfx_projector_emit_shift @image@0x194D6</c>'s three arms,
    /// applied to <c>cdq</c>'s sign-extended <c>dx:ax</c>.
    /// </summary>
    /// <param name="value">The word, sign-extended by <c>cdq</c>.</param>
    /// <param name="shift">The emitted shift count, <c>[0xE836]</c>.</param>
    /// <returns>The 32-bit dividend.</returns>
    public static int ShiftBlock(short value, int shift)
    {
        uint v = unchecked((uint)(int)value);                   // cdq
        if (shift < 6)
        {
            // `shl ax,1 / rcl dx,1`, `shift` times (image@0x194C8).
            return unchecked((int)(v << shift));
        }

        // `mov dh,dl / mov dl,ah / mov ah,al` (image@0x194D0) — a << 8 that COPIES the old low
        // byte into the new low byte instead of zeroing it.
        uint q = unchecked((v << 8) | (v & 0xFF));
        if (shift < 8)
        {
            // `sar dx,1 / rcr ax,1`, (8 - shift) times (image@0x194CC) — an ARITHMETIC >> 1.
            return unchecked((int)q) >> (8 - shift);
        }

        // `shl ax,1 / rcl dx,1`, (shift - 8) times.
        return unchecked((int)(q << (shift - 8)));
    }

    /// <summary>
    /// The ALTERNATE divider — <c>gfx_project_alt @image@0x196A6</c>, which the emitted routine's
    /// first instruction jumps to when <c>g_render_arena_mode_flag [0xEA02]!= 0</c>.
    /// </summary>
    /// <param name="nodeX"><c>node[+0x06]</c>.</param>
    /// <param name="nodeY"><c>node[+0x08]</c>.</param>
    /// <param name="depth"><c>node[+0x0A]</c>.</param>
    /// <param name="centreX"><c>[0xE634]</c>.</param>
    /// <param name="centreY"><c>[0xE636]</c>.</param>
    /// <param name="shift"><c>[0xE836]</c>.</param>
    /// <returns>The screen pair.</returns>
    /// <remarks>
    /// <para>
    /// Where the fast path reads only the vertex block's HIGH words, this one uses the whole
    /// <c>i32</c>s — which is why <c>object_screen_pos_project</c> widens each word by
    /// <c>&lt;&lt; 16</c> in the first place (<c>mov word [bp-0xC],0 / mov [bp-0xA],ax</c>
    /// @<c>image@0x036F6</c>).  It also does X FIRST and Y second, the opposite of the fast path:
    /// </para>
    /// <code>
    /// 196A8  mov bp,ax                                  ; the vertex block
    /// 196AA  mov cx,[bp+0xA] / mov dx,[bp+8]            ; the Z i32 (hi, lo) — the DIVISOR
    /// 196B0  mov ax,[bp+2]   / mov bx,[bp+0]            ; the X i32 (hi, lo)
    /// 196B6  call 0x19582
    /// 196B9  add ax,[0xE634] / jno +3 / mov ax,0x7F00   ; …then SI = AX
    /// 196C4  mov cx,[bp+0xA] / mov dx,[bp+8]
    /// 196CA  mov ax,[bp+6]   / mov bx,[bp+4]            ; the Y i32
    /// 196D0  call 0x19582
    /// 196D3  mov cx,[0xE636] / sub cx,ax / jno +3 / mov cx,0x7F00
    /// 196DE  mov ax,si / pop si / pop bp / ret          ; AX = screen X, CX = screen Y
    /// </code>
    /// </remarks>
    public static (short ScreenX, short ScreenY) ProjectAlternate(
        short nodeX, short nodeY, short depth, short centreX, short centreY, int shift)
    {
        int divisor = depth << 16;
        short x = AddSaturating(centreX, DivideWide(nodeX << 16, divisor, shift));
        short y = SubtractSaturating(centreY, DivideWide(nodeY << 16, divisor, shift));
        return (x, y);
    }

    /// <summary>
    /// <c>gfx_div32_scaled @image@0x19582</c> — the alternate divider's signed 32 ÷ 32 → 16 with
    /// the same <c>[0xE836]</c> pre-shift.
    /// </summary>
    /// <param name="numerator">The 32-bit numerator (<c>AX:BX</c> at entry).</param>
    /// <param name="divisor">The 32-bit divisor (<c>CX:DX</c> at entry).</param>
    /// <param name="shift"><c>[0xE836]</c>.</param>
    /// <returns>The original's <c>AX</c>.</returns>
    /// <remarks>
    /// <para>
    /// The body, register for register: latch the divisor in <c>SI:DI</c>; if it is zero take the
    /// tri-state arm at <c>image@0x19678</c>; otherwise make BOTH operands positive, remembering
    /// the XOR of their signs in <c>BP</c>; <c>cdq</c>; shift <c>DX:AX:BX</c> LEFT
    /// <c>[0xE836]</c> times through <b>fourteen</b> unrolled
    /// <c>shl bx,1 / rcl ax,1 / rcl dx,1</c> blocks (<c>image@0x195B1..0x1962B</c>, each but the
    /// last followed by <c>dec cx / je</c>); then two normalisation loops —
    /// <c>image@0x1962E</c> scales until <c>DX &lt; DI</c>, counting its
    /// numerator-down steps in <c>CX</c>, and <c>image@0x19647</c> scales BOTH up until one of
    /// them reaches <c>0x4000</c>; then <c>idiv di</c>, then <c>CX</c> compensating
    /// <c>shl ax,1</c>s (each checked with <c>jo</c> → <c>0x7F00</c>), then the sign.
    /// </para>
    /// <para>
    /// Its <c>idiv</c> at <c>image@0x1965F</c> is the THIRD registered <c>INT 00</c> site:
    /// <c>int00_subhandler_div32_fixup @image@0x19344</c> matches the hardcoded fault IP
    /// <c>0x201D:0x9491</c> (which is exactly <c>image@0x1965F + 2</c>) and installs <c>AX =
    /// 0x7F00</c>, positive saturation only — 8,437 recorded calls.
    /// </para>
    /// </remarks>
    public static short DivideWide(int numerator, int divisor, int shift)
    {
        ushort si = unchecked((ushort)divisor);                 // mov si,dx  @image@0x19585
        ushort di = unchecked((ushort)(divisor >> 16));         // mov di,cx
        ushort ax = unchecked((ushort)(numerator >> 16));
        ushort bx = unchecked((ushort)numerator);

        if (divisor == 0)                                       // or cx,dx / jne @image@0x19589
        {
            // image@0x19678 — the tri-state arm, which does NOT apply the sign accumulator.
            short high = unchecked((short)ax);
            if (high > 0)
            {
                return OverflowSentinel;                        // image@0x19682
            }

            if (high < 0)
            {
                return unchecked((short)0x8100);                // image@0x1968B
            }

            return bx != 0 ? OverflowSentinel : (short)0;       // image@0x1967E / 0x19687
        }

        bool negate = false;
        if (unchecked((short)ax) < 0)                           // or ax,ax / jns @image@0x19592
        {
            negate = !negate;
            uint p = unchecked((uint)-(int)(((uint)ax << 16) | bx));
            ax = unchecked((ushort)(p >> 16));
            bx = unchecked((ushort)p);
        }

        if (unchecked((short)di) < 0)                           // or di,di / jns @image@0x1959F
        {
            negate = !negate;
            uint p = unchecked((uint)-(int)(((uint)di << 16) | si));
            di = unchecked((ushort)(p >> 16));
            si = unchecked((ushort)p);
        }

        ushort dx = unchecked((short)ax) < 0 ? (ushort)0xFFFF : (ushort)0;      // cdq

        // image@0x195B1..0x1962B — FOURTEEN unrolled `shl bx,1 / rcl ax,1 / rcl dx,1` blocks, the
        // first unconditional and each of the next thirteen guarded by `dec cx / je`.  A count of
        // 0 therefore runs all fourteen (the `dec` wraps to 0xFFFF).
        int steps = shift is >= 1 and <= UnrolledShiftBlocks ? shift : UnrolledShiftBlocks;
        for (int i = 0; i < steps; i++)
        {
            int carry = bx >> 15;
            bx = unchecked((ushort)(bx << 1));
            int carry2 = ax >> 15;
            ax = unchecked((ushort)((ax << 1) | carry));
            dx = unchecked((ushort)((dx << 1) | carry2));
        }

        int compensation = 0;                                   // xor cx,cx @image@0x1962C
        while (unchecked((short)dx) >= unchecked((short)di))     // cmp dx,di / jl @image@0x1962E
        {
            if (unchecked((short)di) >= 0x4000)                 // cmp di,0x4000 / jge
            {
                int lowDx = dx & 1;
                int lowAx = ax & 1;
                dx = unchecked((ushort)(unchecked((short)dx) >> 1));            // sar dx,1
                ax = unchecked((ushort)((ax >> 1) | (lowDx << 15)));            // rcr ax,1
                bx = unchecked((ushort)((bx >> 1) | (lowAx << 15)));            // rcr bx,1
            }
            else
            {
                int carry = si >> 15;
                si = unchecked((ushort)(si << 1));                              // shl si,1
                di = unchecked((ushort)((di << 1) | carry));                    // rcl di,1
            }

            compensation++;                                                     // inc cx
        }

        while (unchecked((short)dx) < 0x4000 && unchecked((short)di) < 0x4000)   // image@0x19647
        {
            int carry = bx >> 15;
            bx = unchecked((ushort)(bx << 1));
            int carry2 = ax >> 15;
            ax = unchecked((ushort)((ax << 1) | carry));
            dx = unchecked((ushort)((dx << 1) | carry2));
            int carry3 = si >> 15;
            si = unchecked((ushort)(si << 1));
            di = unchecked((ushort)((di << 1) | carry3));
        }

        // idiv di @image@0x1965F — the registered INT-00 site 0x201D:0x9491, whose handler
        // (int00_subhandler_div32_fixup @image@0x19344) installs AX = 0x7F00 on a fault.
        int dividend = unchecked((int)(((uint)dx << 16) | ax));
        short divisorWord = unchecked((short)di);
        int quotient = divisorWord == 0 ? int.MaxValue : dividend / divisorWord;
        ushort result = quotient is >= short.MinValue and <= short.MaxValue
            ? unchecked((ushort)quotient)
            : unchecked((ushort)OverflowSentinel);

        for (int i = 0; i < compensation; i++)                  // jcxz / shl ax,1 / jo / loop
        {
            bool overflow = ((result >> 15) & 1) != ((result >> 14) & 1);       // shl r,1: OF
            result = unchecked((ushort)(result << 1));
            if (overflow)
            {
                result = unchecked((ushort)OverflowSentinel);   // image@0x19673
                break;
            }
        }

        return negate                                           // or bp,bp / neg ax @image@0x19669
            ? unchecked((short)-(short)result)
            : unchecked((short)result);
    }

    /// <summary>
    /// How many unrolled <c>shl bx,1 / rcl ax,1 / rcl dx,1</c> blocks
    /// <c>gfx_div32_scaled</c> carries (<c>image@0x195B1..0x1962B</c>).
    /// </summary>
    public const int UnrolledShiftBlocks = 14;

    /// <summary>
    /// <c>sub cx,ax / jno +3 / mov cx,0x7F00</c> — template 2's saturating subtract.
    /// </summary>
    /// <param name="centre">The screen centre.</param>
    /// <param name="quotient">The projected offset.</param>
    /// <returns>The screen coordinate.</returns>
    public static short SubtractSaturating(short centre, short quotient)
    {
        int wide = centre - quotient;
        short narrow = unchecked((short)wide);
        return wide == narrow ? narrow : OverflowSentinel;      // OF ⇒ 0x7F00
    }

    /// <summary>
    /// <c>add ax,[0xE634] / jno +3 / mov ax,0x7F00</c> — template 3's saturating add.
    /// </summary>
    /// <param name="centre">The screen centre.</param>
    /// <param name="quotient">The projected offset.</param>
    /// <returns>The screen coordinate.</returns>
    public static short AddSaturating(short centre, short quotient)
    {
        int wide = centre + quotient;
        short narrow = unchecked((short)wide);
        return wide == narrow ? narrow : OverflowSentinel;      // OF ⇒ 0x7F00
    }
}
