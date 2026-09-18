using CYAC.Port.Core.Model.Cockpit;
using CYAC.Port.Core.Model.Mission;

namespace CYAC.Port.Render.Cockpit;

/// <summary>
/// Which of the three Chuck Yeager PORTRAITS an advisory shows — the local
/// <c>msg_display_type</c> each handler of <c>ai_advisor_message_dispatch @image@0x0EFF1</c> leaves
/// in <c>[bp-2]</c>, used by its common tail to index the 4-byte table at <c>DGROUP[0x4380]</c>
/// (<c>image@0x400E0</c>).
/// </summary>
/// <remarks>
/// The table has exactly three live entries; the fourth word pair reads <c>"NEWS"</c>, i.e. the next
/// datum.  Measured under the emulator: a code-15 advisory carries <c>[0xBCC2]/[0xBCC4] = (256, 0)</c>
/// and a code-4 one <c>(192, 49)</c>
/// (a captured frame of the original).
/// </remarks>
public enum AdvisorDisplayType
{
    /// <summary>0 — the neutral portrait, <c>miscv.pic</c> at (256, 0).  Every code but the four below.</summary>
    Standard = 0,

    /// <summary>1 — the SMILING portrait, at (256, 49).  Only code 0, the kill advisory.</summary>
    Smiling = 1,

    /// <summary>
    /// 2 — the GRIMACING portrait, at (192, 49).  Codes 2 (the ejection advisory), 4 (the bandit-behind
    /// warning) and 20 (the heavy-damage advisory), which all reach <c>image@0x0F097</c>.
    /// </summary>
    Urgent = 2,
}

/// <summary>
/// The YEAGER ADVISOR window: where Chuck's portrait and his two lines go, and the law that decides
/// when he speaks at all.
/// </summary>
/// <remarks>
/// <para>
/// Two shipped functions, both re-disassembled here:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>cockpit_advisor_msg_draw @image@0x0EEDB</c> (278 B) — the WINDOW.  Dispatched first of the
/// four (<c>test al,8 / call 0xeedb</c> at <c>image@0x0EA8C</c>), it draws only inside the active
/// display window of the current message and is otherwise a no-op, which is why the reference
/// frames show nothing for bit 3.
/// </item>
/// <item>
/// <c>ai_advisor_message_dispatch @image@0x0EFF1</c> (834 B) — the MESSAGE.  Twenty-one action codes
/// through a CS-relative jump table at <c>image@0x0F2E2</c>, each writing a far-pointer pair into
/// <c>[0xBCBA]/[0xBCBC]</c> (line 1) and <c>[0xBCBE]/[0xBCC0]</c> (line 2) and leaving a display
/// type behind.  Its timing law is <see cref="AdvisorTiming"/>.
/// </item>
/// </list>
/// <para>
/// Every number below was predicted from those bytes and then MEASURED on frames in which Chuck
/// actually speaks — the earlier captured frames had none, so he had to be made to talk.
/// </para>
/// </remarks>
public static class AdvisorWindow
{
    /// <summary>
    /// The design column the window is drawn at: the FIRST slot, 4 — the literal
    /// <c>menu_filled_rect_draw</c> pushes at <c>image@0x0EF22</c>.
    /// </summary>
    /// <remarks>
    /// The drawer pushes the same six arguments the MAP drawer does (<c>image@0x0EAD4</c>), and
    /// <c>menu_filled_rect_draw @image@0x20F64</c>'s frame map is <c>(style, style, height, width,
    /// y, x)</c> — <c>[bp+0x10]</c> is the X (<c>mov di,[bp+0x10] / add ax,[bp+0xc] / dec ax</c> at
    /// <c>image@0x20F6C..0x20F74</c> makes <c>x + width − 1</c>).  So the advisor and the map are
    /// the SAME slot, which is why the drawer takes the map's bit away — see
    /// <see cref="Suppresses"/>.  Measured: the window's chrome occupies design columns 4..75 in
    /// every frame in which Chuck speaks.
    /// </remarks>
    public const int SlotX = OverlayWindowLayout.FirstSlotX;

    /// <summary>
    /// What the drawer advances the slot cursor to before it fills its own panel —
    /// <c>mov word [0xBCF4],0x80</c> (<c>image@0x0EF1C</c>), the same 128 the MAP drawer writes.
    /// </summary>
    public const int CursorAfter = OverlayWindowLayout.SecondSlotX;

    /// <summary>
    /// The window's title's address — <c>DGROUP[0x3BD4]</c> = <c>image@0x3F934</c>, catalogued in
    /// <c>data/exe/strings.json</c>'s <c>str_planes_pic_and_envelope</c> zone beside the other three
    /// window titles.  The text is <see cref="InFlightStrings.AdvisorTitle"/>.
    /// </summary>
    /// <remarks>read from the tree.</remarks>
    public const int TitleDgroup = InFlightStrings.AdvisorTitleDgroup;

    /// <summary>
    /// The title's design column: the literal <c>0x10</c> = 16 that <c>image@0x0EF42</c> pushes as
    /// <c>glyph_blit_dispatch</c>'s x — no centring, exactly like the MAP's left-aligned 32.
    /// </summary>
    /// <remarks>
    /// <c>glyph_blit_dispatch @image@0x1F4AE</c> is FAR PASCAL (<c>retf 6</c>), so the first push is
    /// the string and the middle one is the column: the drawer pushes <c>0x3BD4</c>, <c>0x10</c>,
    /// <c>[0xBCF0]+3</c>.  Measured: the <c>C</c> of <c>CHUCK YEAGER</c> begins at design column 16
    /// in all four frames W5 captured.
    /// </remarks>
    public const int TitleX = 0x10;

    /// <summary>The portrait's destination column — <c>gfx_plain_blit</c>'s <c>dst_x</c>, 8.</summary>
    /// <remarks>
    /// <c>gfx_plain_blit @image@0x1CF24</c> is FAR PASCAL with the prototype <c>(src_desc, src_x,
    /// src_y, dst_desc, dst_x, dst_y, width, height)</c> .  The drawer
    /// pushes, in that order, <c>0x421A</c> (the shared instrument sheet <c>miscv.pic</c>),
    /// <c>[0xBCC2]</c>, <c>[0xBCC4]</c>, <c>0xE7EC</c> (the screen surface), <c>8</c>,
    /// <c>[0xBCFE]</c>, <c>0x40</c>, <c>0x2A</c> — <c>image@0x0EF52..0x0EF72</c>. That file reads the
    /// push list as a cdecl argument list in the wrong order; corrected here and reported. Measured:
    /// the portrait's pixels occupy design columns 8..71 and rows 30..71.
    /// </remarks>
    public const int PortraitX = 8;

    /// <summary>The portrait's width — the <c>0x40</c> pushed at <c>image@0x0EF6A</c>.</summary>
    public const int PortraitWidth = 0x40;

    /// <summary>The portrait's height — the <c>0x2A</c> pushed at <c>image@0x0EF6E</c>.</summary>
    public const int PortraitHeight = 0x2A;

    /// <summary>
    /// The portrait's destination row: <c>g_cockpit_hud_row_bottom [0xBCFE]</c>
    /// (<c>image@0x0EF66</c>) = <see cref="OverlayWindowLayout.Top"/> +
    /// <see cref="OverlayWindowLayout.BandHeight"/> = 30, the content rectangle's own first row.
    /// </summary>
    public const int PortraitRow = OverlayWindowLayout.Top + OverlayWindowLayout.BandHeight;

    /// <summary>
    /// The clip rectangle the two text lines are drawn inside:
    /// <c>gfx_viewport_clip_rect_setup(x = 8, y = g_cockpit_hud_row, width = 0x40, height = 0x46)</c>
    /// (<c>image@0x0EF7C..0x0EF8C</c>, FAR PASCAL <c>retf 8</c> — the first push is the x).
    /// </summary>
    public const int ClipX = 8;

    /// <summary>The clip rectangle's width — <c>0x40</c>.</summary>
    public const int ClipWidth = 0x40;

    /// <summary>The clip rectangle's height — <c>0x46</c>, the whole window.</summary>
    public const int ClipHeight = 0x46;

    /// <summary>
    /// <c>g_gfx_clip_y_max [0xE62E]</c> while the advisor draws: <c>y + height − 1</c>
    /// (<c>image@0x118A1..0x118A4</c>) = 19 + 70 − 1 = <b>88</b>, the window's own last row.  Both
    /// text rows are measured from it.
    /// </summary>
    public const int ClipYMax = OverlayWindowLayout.Top + ClipHeight - 1;

    /// <summary>
    /// <c>[0xE634]</c>, the clip rectangle's horizontal centre: <c>(x_max + x_min) &gt;&gt; 1</c>
    /// (<c>image@0x11897..0x1189B</c>) = (71 + 8) &gt;&gt; 1 = <b>39</b>.
    /// </summary>
    public const int ClipCentreX = (ClipX + ClipX + ClipWidth - 1) / 2;

    /// <summary>The LOWER line's row — <c>clip_y_max − 8</c> (<c>image@0x0EFA8</c>) = 80.</summary>
    public const int Line2Row = ClipYMax - 8;

    /// <summary>The two text lines' palette index: 15, white.</summary>
    /// <remarks>
    /// Both lines and the title come out of <c>cockpit_text_color_set @image@0x0EA26</c>, called at
    /// <c>image@0x0EF3B</c> before any of them.  Measured on four frames: every glyph pixel inside
    /// the window is palette 15, unlike the TARGET window's content rows, which an earlier pass measured as
    /// palette 0.
    /// </remarks>
    public const int TextPaletteIndex = OverlayWindowLayout.TextPaletteIndex;

    /// <summary>The UPPER line's row.</summary>
    /// <param name="hasLine2">Whether the message carries a second line.</param>
    /// <returns>
    /// <c>clip_y_max − 0x0E</c> = 74 when it does (<c>image@0x0EFB6..0x0EFBA</c>), and
    /// <c>clip_y_max − 0x0B</c> = 77 when it does not (<c>image@0x0EFBF..0x0EFC3</c>) — the
    /// single-line message rides three rows lower, halfway to where the pair would have sat.
    /// </returns>
    public static int Line1Row(bool hasLine2) => ClipYMax - (hasLine2 ? 0x0E : 0x0B);

    /// <summary>
    /// Where a message line starts, in design columns —
    /// <c>(centre − 2·len + 1) &amp; 0xFC</c>.
    /// </summary>
    /// <param name="length">The string's length in characters.</param>
    /// <returns>The design column its first glyph is drawn at.</returns>
    /// <remarks>
    /// <c>glyph_blit_dispatch_2 @image@0x1F1A8</c> measures the string with <c>repne scasb</c>,
    /// doubles it (<c>shl cx,1</c>, i.e. half of the 4-pixel cell per character), subtracts the clip
    /// centre <c>[0xE634]</c>, negates, adds one and masks to a multiple of four
    /// (<c>image@0x1F1BC..0x1F1C5</c>).  The mask is the toolkit's planar alignment, and it is what
    /// makes a 15-character line and a 16-character line start at the same column.
    /// MEASURED on four frames: code 15's two 15-character lines at 8, code 19's 12- and
    /// 9-character lines at 16 and 20, and code 4's 14- and 13-character lines at 12 — all six
    /// exactly what this returns.
    /// </remarks>
    public static int TextColumn(int length) => (ClipCentreX - (2 * length) + 1) & 0xFC;

    /// <summary>
    /// Where the portrait for a display type is cut out of <c>miscv.pic</c> — the 4-byte table at
    /// <c>DGROUP[0x4380]</c> = <c>image@0x400E0</c>, read by the dispatch's common tail
    /// (<c>mov ax,[bx+0x4380] / mov dx,[bx+0x4382]</c>, <c>image@0x0F315</c>).
    /// </summary>
    /// <param name="type">The display type the handler chose.</param>
    /// <returns>The source column and row inside the shared instrument sheet.</returns>
    /// <remarks>
    /// The sheet is 320 × 91, so a 64 × 42 cut at (256, 0) is its top-right corner, (256, 49) the
    /// bottom-right and (192, 49) the one to their left.  All three are Chuck: neutral, smiling and
    /// grimacing.  The port never retypes a pixel of them — <see cref="CockpitArt.MiscAt"/> is the
    /// sheet the loader already put in the descriptor at <c>[0x421A]</c>.
    /// </remarks>
    public static (int X, int Y) PortraitSource(AdvisorDisplayType type) => type switch
    {
        AdvisorDisplayType.Smiling => (256, 49),
        AdvisorDisplayType.Urgent => (192, 49),
        _ => (256, 0),
    };

    /// <summary>The portrait's destination rectangle for a window at <see cref="SlotX"/>.</summary>
    public static PanelRect Portrait =>
        new(PortraitX, PortraitRow, PortraitWidth, PortraitHeight);

    /// <summary>
    /// Which OTHER window the advisor takes off the band while it is speaking.
    /// </summary>
    /// <param name="visible">The live <c>[0xF1CB]</c> mask.</param>
    /// <returns>The bit the drawer clears, or <see cref="CockpitOverlayFlags.None"/>.</returns>
    /// <remarks>
    /// <para>
    /// <c>image@0x0EF02..0x0EF1C</c>: <c>test byte [0xF1CB],4</c> → clear bit 2 (the MAP); else
    /// <c>test byte [0xF1CB],1</c> → clear bit 0 (the ENVELOPE).  REFUTED by W5: it is neither a
    /// blink nor persistent.  The DISPATCHER saves the byte on entry and writes it back on exit —
    /// <c>mov al,[0xF1CB] / mov [bp-1],al</c> at <c>image@0x0EA80</c> and <c>mov al,[bp-1] / mov
    /// [0xF1CB],al</c> at <c>image@0x0EABD</c> — so the clear lasts exactly the rest of ONE frame's
    /// dispatch and the user's bits, and the persisted <c>yeager.cfg@0x1D</c>, are untouched.
    /// </para>
    /// <para>
    /// It is a SLOT conflict, not an effect: the advisor and the map are both drawn at design column
    /// 4.  MEASURED, two sorties of the same mission with different cfg bytes:
    /// with <c>[0xF1CB] = 0x0F</c> the map vanishes while Chuck speaks and the envelope stays at
    /// column 128 (a captured frame of the original); with <c>0x09</c> — no
    /// map — the ENVELOPE vanishes instead (<c>b_475.png</c>); with <c>0x08</c> the window is alone
    /// on the band (<c>c_470.png</c>).  A mid-frame DGROUP dump catches the byte at <c>0x0B</c>
    /// (<c>a_472.dg</c>, <c>a_800.dg</c>) and the next one has it back at <c>0x0F</c>.
    /// </para>
    /// </remarks>
    public static CockpitOverlayFlags Suppresses(CockpitOverlayFlags visible) =>
        visible.HasFlag(CockpitOverlayFlags.Map) ? CockpitOverlayFlags.Map
        : visible.HasFlag(CockpitOverlayFlags.Envelope) ? CockpitOverlayFlags.Envelope
        : CockpitOverlayFlags.None;
}

/// <summary>
/// The TIMING law of <c>ai_advisor_message_dispatch @image@0x0EFF1</c> and its two wrappers, in the
/// units the original counts in.
/// </summary>
/// <remarks>
/// <para>
/// Two clocks, both derived from <c>g_frame_time_accum [0xF0D2]</c> by
/// <c>scene_frame_timer_advance @image@0x0C120</c>: <c>g_frame_count_scaled [0xF0D0]</c> =
/// <c>accum &gt;&gt; 6</c> and <c>g_master_frame_counter [0xF0C8]</c> = <c>accum &gt;&gt; 8</c>, so
/// the first ticks four times per tick of the second.  MEASURED on the probe's dumps: 16/4, 111/27,
/// 395/98 — a ratio of 4 throughout.  The port's <c>TickClock</c> publishes both.
/// </para>
/// <para>
/// The advisory's own window is <see cref="DisplayUnits"/> = 16 units of <c>[0xF0D0]</c>, and
/// <c>[0xF0C8]</c> counts roughly seconds (r-P9), so a message is up for about four seconds and the
/// repeat throttle of <see cref="RepeatThrottleUnits"/> is about a minute.
/// </para>
/// </remarks>
public static class AdvisorTiming
{
    /// <summary>
    /// How long a message stays up, in <c>g_frame_count_scaled [0xF0D0]</c> units:
    /// <c>next_allow = last_dispatch + 0x10</c> (<c>add ax,0x10</c>, <c>image@0x0F327</c>).
    /// </summary>
    public const int DisplayUnits = 0x10;

    /// <summary>
    /// The delay before a normal message appears: <c>last_dispatch = now + 2</c>
    /// (<c>inc ax / inc ax / mov [0xBCB8],ax</c>, <c>image@0x0F01A</c>) — and the drawer refuses to
    /// draw until <c>last_dispatch &lt;= now</c> (<c>image@0x0EEF3</c>).
    /// </summary>
    public const int DispatchDelay = 2;

    /// <summary>
    /// How long <c>ai_advisor_repeated_dispatch @image@0x0F35B</c> makes an ALREADY-SEEN code wait
    /// before it may fire again, in <c>g_master_frame_counter [0xF0C8]</c> units:
    /// <c>stamp[code] + 0x3C &lt;= now</c> (<c>add ax,0x3c / cmp ax,[0xF0C8] / jae</c>,
    /// <c>image@0x0F382</c>).
    /// </summary>
    /// <remarks>
    /// It is 60 units of <c>[0xF0C8]</c>, which r-P9 measured as roughly one second each — i.e.
    /// about a MINUTE, not a second and a half.
    /// </remarks>
    public const int RepeatThrottleUnits = 0x3C;

    /// <summary>
    /// How many action codes the jump table at <c>image@0x0F2E2</c> has — 21, and
    /// <c>cmp ax,0x14 / ja</c> at <c>image@0x0F2D5</c> sends anything above it straight to the tail.
    /// </summary>
    public const int CodeCount = 21;

    /// <summary>
    /// The action code that BYPASSES the cooldown: 2, the ejection advisory
    /// (<c>cmp byte [bp-4],2 / jne</c>, <c>image@0x0F014</c>).
    /// </summary>
    public const int EjectionCode = 2;

    /// <summary>
    /// The action code that is RANDOM-GATED: 0, the kill advisory
    /// (<c>image@0x0F03F</c>).
    /// </summary>
    public const int KillCode = 0;

    /// <summary>
    /// The kill advisory's gate: <c>prng_rand8() &gt; 0x80</c> suppresses it
    /// (<c>cmp ax,0x80 / jg 0xf006</c>, <c>image@0x0F044</c>) — so it speaks on 129 of 256 draws.
    /// </summary>
    /// <remarks>
    /// The draw is the project's own <c>Fx.Indicators</c> site: assigns <c>image@0x0F03F</c> to
    /// <c>FX_prng_rand8</c> ("FLIGHT-ADVISOR message gate … Text only").  The port therefore draws it
    /// from <c>RandomStreams.FxIndicators</c>, which is byte-inert by construction — the verified
    /// kernels never see it.
    /// </remarks>
    public const int KillGateThreshold = 0x80;

    /// <summary>Whether a code stamps <c>last_dispatch = now</c> instead of <c>now + 2</c>.</summary>
    /// <param name="code">The action code.</param>
    /// <returns>True for the eight codes that refresh the timestamp.</returns>
    /// <remarks>
    /// Codes 7, 8, 10, 12, 13, 14 and 19 jump to the shared block at <c>image@0x0F176</c>
    /// (<c>mov ax,[0xF0D0] / mov [0xBCB8],ax</c>), and code 4 does the same inline at
    /// <c>image@0x0F0D1</c> before falling into <c>image@0x0F097</c>.  The effect is that these
    /// messages appear IMMEDIATELY rather than half a second late; the window is 16 units either
    /// way.
    /// </remarks>
    public static bool RefreshesTimestamp(int code) =>
        code is 4 or 7 or 8 or 10 or 12 or 13 or 14 or 19;

    /// <summary>The portrait a code selects.</summary>
    /// <param name="code">The action code.</param>
    /// <returns>Its display type.</returns>
    /// <remarks>
    /// Only four handlers touch <c>[bp-2]</c>: code 0 sets 1 (<c>image@0x0F061</c>), and codes 2, 4
    /// and 20 all reach <c>mov byte [bp-2],2</c> at <c>image@0x0F097</c> — code 2 by falling into
    /// it, code 4 through <c>jmp 0xf097</c> at <c>image@0x0F0D7</c> and code 20 through <c>jmp
    /// 0xf097</c> at <c>image@0x0F2D2</c>.  Every other code keeps the 0 written at
    /// <c>image@0x0F033</c>. Settled by W5 — it is not a mistake and not a guess: a code-4 advisory
    /// MEASURES <c>[0xBCC2]/[0xBCC4] = (192, 49)</c>, the urgent portrait,
    /// a captured frame of the original.
    /// </remarks>
    public static AdvisorDisplayType DisplayType(int code) => code switch
    {
        0 => AdvisorDisplayType.Smiling,
        2 or 4 or 20 => AdvisorDisplayType.Urgent,
        _ => AdvisorDisplayType.Standard,
    };
}
