using CYAC.Port.Core.Model.Cockpit;
using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Combat;

namespace CYAC.Port.Render.Cockpit;

/// <summary>
/// The TARGET window's CONTENTS: where every line goes and what it says.
/// </summary>
/// <remarks>
/// <para>
/// <c>cockpit_target_info_panel_draw @image@0x0EDAB</c> frames the window and
/// <c>cockpit_radar_scope_draw @image@0x0A5D1</c> fills it.  The panel is drawn inside a CLIP RECT
/// the caller builds as four words immediately before the call
/// (<c>{0xF8, g_cockpit_hud_row_bottom, 0x40, 0x30}</c>, <c>image@0x0EE18..0x0EE2B</c>), and every
/// anchor inside the panel is expressed in that rect's own terms — <c>g_gfx_clip_x_min [0xE628]</c>,
/// <c>_x_max [0xE62A]</c>, <c>_y_min [0xE62C]</c>, <c>_y_max [0xE62E]</c> and
/// <c>g_viewport_center_x [0xE634]</c>.
/// </para>
/// <para>
/// Measured on the original at the same instruction count as its own frame (whose screenshots are
/// byte-identical to the atlas): the four clip words read back <c>x[248..311] y[30..77]</c> with
/// centre <c>(279, 53)</c>, so the rect is <see cref="OverlayWindowLayout.Content"/> of the target
/// slot and the centre is <c>(x_min + x_max) &gt;&gt; 1</c> (<c>gfx_viewport_clip_rect_setup
/// @image@0x11876</c>).
/// </para>
/// <para>
/// Every row below was checked against
/// a captured frame of the original pixel by pixel; the two
/// numbers reproduce the frame EXACTLY (<c>496 MPH</c> and <c>671'</c>).
/// </para>
/// </remarks>
public static class TargetPanel
{
    /// <summary>The panel's content rectangle — <see cref="OverlayWindowLayout.Content"/> at the target slot.</summary>
    public static PanelRect Content => OverlayWindowLayout.Content(OverlayWindowLayout.TargetSlotX);

    /// <summary>
    /// The horizontal centre the panel's centred strings are measured from:
    /// <c>g_viewport_center_x [0xE634] = (x_min + x_max) &gt;&gt; 1</c> — measured 279.
    /// </summary>
    public static int CentreX => (Content.X + Content.Right - 1) >> 1;

    /// <summary>The row the AI-manoeuvre name sits on: <c>clip_y_min + 1</c> (<c>image@0x0A7EA</c>).</summary>
    public static int ManoeuvreRow => Content.Y + 1;

    /// <summary>The row the radar lock-state label sits on: <c>clip_y_min + 7</c> (<c>image@0x0A83B</c>).</summary>
    public static int LockStateRow => Content.Y + 7;

    /// <summary>The row the clock bearing sits on: <c>clip_y_max − 0x0B</c> (<c>image@0x0A8D3</c>).</summary>
    public static int ClockRow => Content.Bottom - 1 - 0x0B;

    /// <summary>
    /// The row the speed and the range share: <c>clip_y_max − 5</c> (<c>image@0x0A8AE</c>,
    /// <c>image@0x0A945</c>).
    /// </summary>
    public static int BottomRow => Content.Bottom - 1 - 5;

    /// <summary>The speed string's left edge: <c>clip_x_min + 4</c> (<c>image@0x0A8A7</c>).</summary>
    public static int SpeedColumn => Content.X + 4;

    /// <summary>
    /// The row the ENGAGEMENT CAPTION sits on, in the FOOTER band: <c>g_cockpit_hud_row + 0x3E</c> =
    /// 81 (<c>image@0x0EE88</c>).
    /// </summary>
    /// <remarks>
    /// It is centred by the same <see cref="CentredColumn"/> rule as the content's own lines, because
    /// <c>cockpit_target_info_panel_draw</c> draws it BEFORE it restores the clip rectangle
    /// (<c>image@0x0EE91</c> then <c>image@0x0EE96</c>), so <c>g_viewport_center_x</c> is still the
    /// panel's 279.  Measured: <c>Wingman</c> (7 characters) starts at design column <b>264</b>,
    /// exactly where <c>PURSUIT</c> does.
    /// </remarks>
    public static int CaptionRow => OverlayWindowLayout.Top + 0x3E;

    /// <summary>
    /// The design column a CENTRED panel string starts at — <c>glyph_blit_dispatch_2 @image@0x1F1A8</c>.
    /// </summary>
    /// <param name="text">The string, whose length is the whole of the rule.</param>
    /// <returns>The first glyph's design column.</returns>
    /// <remarks>
    /// <c>cx = 2·len; cx = −(cx − centre) + 1; cl &amp;= 0xFC</c>
    /// (<c>image@0x1F1BC..0x1F1C5</c>) — i.e. <c>(centre − 2·len + 1)</c> rounded DOWN to a multiple
    /// of four.  The mask is applied to the low byte only, which for a screen column is the same as
    /// masking the whole word.  Measured: <c>PURSUIT</c> (7) → 264 ✔ and <c>11 O'CLOCK</c> (10) →
    /// 260 ✔ against the atlas' own ink columns.
    /// </remarks>
    public static int CentredColumn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (CentreX - (2 * text.Length) + 1) & ~3;
    }

    /// <summary>
    /// The design column a RIGHT-ALIGNED panel string starts at:
    /// <c>clip_x_max − 4·len + 1</c> (<c>image@0x0A936..0x0A940</c>).
    /// </summary>
    /// <param name="text">The string.</param>
    /// <returns>The first glyph's design column.</returns>
    /// <remarks>
    /// The original computes <c>2·len</c> into <c>[bp-0x48]</c>, doubles it and subtracts.  Measured:
    /// <c>671'</c> (4) → 296 ✔.
    /// </remarks>
    public static int RightAlignedColumn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Content.Right - 1 - (4 * text.Length) + 1;
    }

    /// <summary>
    /// The target's SPEED in miles per hour, from its engagement block's <c>+0x26</c> feet-per-second.
    /// </summary>
    /// <param name="feetPerSecond">The block's <c>+0x26</c> word.</param>
    /// <returns>The whole miles per hour the panel prints.</returns>
    /// <remarks>
    /// <c>image@0x0A85D..0x0A86F</c>: <c>muldiv32_signed(block[+0x26] × 0xE10, 0x14A0)</c> —
    /// 3600 seconds an hour over 5280 feet a mile, so the units are ft/s in and mph out.  Measured
    /// against the atlas: <c>728 → 496</c> and the frame prints <c>496 MPH</c>; <c>575 → 392</c> and
    /// the F-4's frame prints <c>392 MPH</c>.  Both EXACT.
    /// </remarks>
    public static int MilesPerHour(int feetPerSecond) => (int)((feetPerSecond * 3600L) / 5280L);

    /// <summary>
    /// <c>abs3d_dist_approx_sorted @image@0x0A95B</c> — the distance metric the panel's range readout
    /// and the pipper's range ladder both measure with.
    /// </summary>
    /// <param name="a">One position, in the pool's own world units.</param>
    /// <param name="b">The other.</param>
    /// <returns>The approximation, in world units.</returns>
    /// <remarks>
    /// <para>
    /// The three absolute axis deltas are SORTED ascending by three XOR-swaps
    /// (<c>image@0x0A9D2..0x0AA67</c>) and then combined as
    /// <c>max + ((mid × 5) &gt;&gt; 4) + (min &gt;&gt; 2)</c> (<c>image@0x0AA68..0x0AA99</c>) — an
    /// octagonal norm, within about 4 % of the Euclidean distance and never below it.
    /// </para>
    /// <para>
    /// Verified against the original to the unit: the <c>a_mig21_110</c> dump's two positions give
    /// <b>671</b> after the <c>&gt;&gt; 8</c>, and its frame prints <c>671'</c>.
    /// </para>
    /// </remarks>
    public static long SortedApproxDistance(CombatPosition a, CombatPosition b)
    {
        long dx = Math.Abs((long)a.X - b.X);
        long dy = Math.Abs((long)a.Y - b.Y);
        long dz = Math.Abs((long)a.Z - b.Z);

        // image@0x0A9D2..0x0AA67 — three compare-and-swap passes leave min, mid, max.
        long min = Math.Min(dx, Math.Min(dy, dz));
        long max = Math.Max(dx, Math.Max(dy, dz));
        long mid = dx + dy + dz - min - max;

        return max + ((mid * 5) >> 4) + (min >> 2);
    }

    /// <summary>How many world units make one displayed foot: the engine's unit is 1/256 ft.</summary>
    /// <remarks>
    /// The range readout is <c>abs3d_dist_approx_sorted(…) &gt;&gt; 8</c> printed through
    /// <c>"%ld'"</c> (<c>image@0x0A8F7</c>, <c>image@0x0A900</c>).
    /// </remarks>
    public const int WorldUnitsPerFoot = 256;

    /// <summary>A full turn in the engine's angle units — <c>0xB40</c>, so one unit is 0.125°.</summary>
    public const int FullTurnUnits = 0x0B40;

    /// <summary>
    /// <c>oclock_bearing_format @image@0x0AAA4</c> — the clock face position of one object seen from
    /// another, with the manual's HI / LO suffix.
    /// </summary>
    /// <param name="strings">The in-flight words and formats: the clock format and its two suffixes.</param>
    /// <param name="observer">The player's world position.</param>
    /// <param name="observerHeadingUnits">The player object's <c>+0x12</c> heading, in angle units.</param>
    /// <param name="subject">The object being reported.</param>
    /// <returns>The formatted string: the hour in the clock format, and the low or high suffix.</returns>
    /// <remarks>
    /// <para>
    /// <c>bearing_angle_compute(player, target)</c> (<see cref="CombatGeometry.Bearing2d"/>, the
    /// SAME bytes) minus the observer's own heading (<c>image@0x0AAD8</c>), wrapped to
    /// <c>[0, 0xB40)</c>, then MIRRORED — <c>s = 0xB40 − bearing</c>, except that a bearing of
    /// exactly 0 stays 0 (<c>image@0x0AAE3..0x0AAFA</c>) — and scaled by
    /// <c>muldiv16_signed(s, 12, 0xB40)</c> with <c>0 → 12</c> (<c>image@0x0AAFC..0x0AB0F</c>).
    /// </para>
    /// <para>
    /// The suffix compares the two objects' <c>[+0x0B]</c> WORDS — an odd-offset read of the Y <c>i32</c>, i.e. the
    /// altitude in whole FEET — and appends the low suffix (DGROUP <c>[0x100C]</c>) above 2000 and the high one
    /// (<c>[0x1010]</c>) below −2000 (<c>image@0x0AB22..0x0AB41</c>).  The hour is printed with the format at
    /// <c>[0x1000]</c>; all three come from the tree.  So <b>Y is up-positive</b> and the decoded file's "ALTITUDE
    /// CONVENTION NOTE (hypothesis): higher value = LOWER altitude" is refuted: the observer being 2000 ft higher in
    /// raw Y is exactly when the subject reads LO.
    /// </para>
    /// <para>
    /// Measured on three atlas instants: <c>11 O'CLOCK</c>, <c>11 O'CLOCK</c>, <c>6 O'CLOCK</c> — all
    /// three reproduced.
    /// </para>
    /// </remarks>
    public static string ClockBearing(
        InFlightStrings strings, CombatPosition observer, int observerHeadingUnits, CombatPosition subject)
    {
        ArgumentNullException.ThrowIfNull(strings);
        int world = CombatGeometry.Bearing2d(observer, subject);          // image@0x0AAD0
        int relative = ((world - observerHeadingUnits) % FullTurnUnits + FullTurnUnits)
            % FullTurnUnits;                                              // image@0x0AAD8/0x0AADC
        int mirrored = relative == 0 ? 0 : FullTurnUnits - relative;      // image@0x0AAE3..0x0AAFA
        int clock = (mirrored * 12) / FullTurnUnits;                      // image@0x0AB04
        if (clock == 0)
        {
            clock = 12;                                                   // image@0x0AB0F
        }

        // [+0x0B] is a WORD read at an ODD offset inside the Y i32 — the altitude in feet.
        short observerFeet = unchecked((short)(observer.Y >> 8));         // image@0x0AB22
        short subjectFeet = unchecked((short)(subject.Y >> 8));
        int delta = observerFeet - subjectFeet;                           // image@0x0AB2C
        string suffix = delta > AltitudeSuffixFeet
            ? strings.LowSuffix                                           // image@0x0AB36
            : delta < -AltitudeSuffixFeet ? strings.HighSuffix : string.Empty;  // image@0x0AB41

        return PrintfFormat.Format(strings.ClockFormat, clock) + suffix;
    }

    /// <summary>The altitude difference the HI / LO suffix needs: <c>0x7D0</c> = 2000 feet.</summary>
    public const int AltitudeSuffixFeet = 0x07D0;

    /// <summary>
    /// The WEAPON-STATION ladder in the footer band: the rows a vertical run of dots covers.
    /// </summary>
    /// <param name="station">The target's engagement block <c>+0x05</c> masked with 3.</param>
    /// <returns>The ladder's first and last design rows, inclusive.</returns>
    /// <remarks>
    /// <para>
    /// <c>image@0x0EE9B..0x0EEC9</c> pushes <c>0xF8</c> (the CONTENT's own left column, 248), then
    /// <c>g_cockpit_hud_row + 0x43 − 2·(block[+5] &amp; 3)</c>, then <c>g_cockpit_hud_row + 0x43</c>,
    /// then the colour <c>0xF00F</c> — palette 15 with the stipple key <c>0xF0</c>, which is why the
    /// original's run comes out as alternate lit rows.  (The decoded file reads the colour as
    /// <c>0xFF00</c>/<c>0xFF0F</c>; the bytes are <c>sbb ax,ax / and al,0xF1 / add ax,0xF00F</c>, so
    /// it is <c>0xF00F</c> for <c>g_cfg_sub_mode &gt;= 1</c> and <c>0xF000</c> below it.)
    /// </para>
    /// <para>
    /// Measured: the MiG-21's target has <c>block[+5] &amp; 3 = 3</c> and the atlas lights rows
    /// 81/83/85; the F-4's has <c>2</c> and it lights 83/85. ✔
    /// </para>
    /// </remarks>
    public static (int First, int Last) WeaponStationRows(int station)
    {
        int last = OverlayWindowLayout.Top + 0x43;
        return (last - (2 * Math.Clamp(station, 0, 3)), last);
    }

    /// <summary>The ladder's design column: the literal <c>0xF8</c> at <c>image@0x0EE9B</c>.</summary>
    public const int WeaponStationColumn = 0xF8;

    /// <summary>
    /// The SILHOUETTE's camera distance, in world units, from the target's class record.
    /// </summary>
    /// <param name="classDistanceSteps">
    /// The class record's <c>+0x0D</c> byte (<c>data/exe/classes.json</c> <c>unknown_0x0D</c>).
    /// </param>
    /// <returns>The distance in world units, or 0 when the class takes the extent-derived branch.</returns>
    /// <remarks>
    /// <para>
    /// <c>radar_closest_approach_compute @image@0x0A4D3</c> is the reader
    /// <c>ClassRecordDto.Unknown0x0D</c>'s doc comment says does not exist: <c>al = class[+0x0D]; if (al!=
    /// 0) g_radar_range = (u32)(al &lt;&lt; 4) &lt;&lt; 8</c> (<c>image@0x0A4EF..0x0A509</c>) — a distance
    /// in 4096-world-unit, i.e. SIXTEEN-FOOT, steps. The six fighters and the two other aeroplanes carry
    /// <c>0x19</c> = 400 ft, the B-17 <c>0x32</c> = 800 ft and the two ejection-seat classes <c>0x0C</c> =
    /// 192 ft.
    /// </para>
    /// <para>
    /// Measured: <c>g_radar_range [0xB7A4] = 102400</c> in every dump, on both aircraft, against a
    /// MiG-21MF target whose class byte is <c>0x19</c>. ✔
    /// </para>
    /// <para>
    /// The arm is decoded in the three-argument overload; and the byte is NOT a registry-only fact: every
    /// fighter SLOT carries <c>0x19</c>, the P-47D and Yak-9 included, which the port's lookup over the 23
    /// <c>exe/classes.json</c> records missed, so their window stayed black.
    /// </para>
    /// </remarks>
    public static int SilhouetteCameraDistance(int classDistanceSteps) =>
        classDistanceSteps > 0 ? classDistanceSteps * 4096 : 0;

    /// <summary>
    /// <c>radar_closest_approach_compute @image@0x0A4D3</c>, both arms: the class byte when it is
    /// non-zero, otherwise the EXTENT-derived distance.
    /// </summary>
    /// <param name="classDistanceSteps">The slot's <c>+0x0D</c> byte, in 16-foot steps.</param>
    /// <param name="rawExtent">The slot's <c>+0x08</c> i32 world-space extent.</param>
    /// <param name="lodThreshold0">The slot's <c>+0x0E</c> LOD-0 switch threshold (i16).</param>
    /// <returns>The camera distance in world units (1/256 ft).</returns>
    /// <remarks>
    /// <para>
    /// Disassembled (<c>image@0x0A4EF..0x0A553</c>):
    /// </para>
    /// <code>
    /// mov al,[di+0x0D]; sub ah,ah; mov si,ax; or si,si; jz extent   ; image@0x0A4EF..0x0A4F8
    /// shl ax,4; cwd; mov cl,8; call lshl32 → range = (al &lt;&lt; 4) &lt;&lt; 8            ; image@0x0A4FA..0x0A509
    /// extent: dx:ax = [di+0x08]; mov cl,3; call lshl32 → range = extent &lt;&lt; 3      ; image@0x0A50F..0x0A51F
    ///   if (range &lt; 0x9600) range = 0x9600                     (signed 32-bit)   ; image@0x0A523..0x0A538
    ///   si = [di+0x0E] / 2                                        (cwd/sub/sar)     ; image@0x0A53A..0x0A542
    ///   if (si &lt;= hi16(range)) range = si &lt;&lt; 16                                   ; image@0x0A544..0x0A553
    /// </code>
    /// <para>
    /// The cap compares the HIGH WORD alone, so a range whose high word equals <c>si</c> is
    /// truncated to <c>si &lt;&lt; 16</c> too.  The shadow slots (<c>*sh</c>, byte 0) are the only
    /// aircraft-shaped slots that take this arm; measured for <c>p51sh</c>: <c>25600 &lt;&lt; 3</c> =
    /// 204,800 → high word 3, <c>31 / 2</c> = 15 &gt; 3, so 204,800 (800 ft).
    /// </para>
    /// </remarks>
    public static int SilhouetteCameraDistance(int classDistanceSteps, int rawExtent, int lodThreshold0)
    {
        if (classDistanceSteps > 0)
        {
            return (classDistanceSteps & 0xFF) << 12;                       // image@0x0A4FA..0x0A509
        }

        long range = (long)rawExtent << 3;                                 // image@0x0A50F..0x0A51F
        range = unchecked((int)range);                                     // lshl32 keeps 32 bits
        if (range < 0x9600)
        {
            range = 0x9600;                                                // image@0x0A523..0x0A538
        }

        int cap = unchecked((short)lodThreshold0) / 2;                     // image@0x0A53A..0x0A542
        if (cap <= (short)(range >> 16))
        {
            range = (long)cap << 16;                                       // image@0x0A54A..0x0A553
        }

        return (int)range;
    }

    /// <summary>The distance the two-arm overload gives a <see cref="Core.Model.World.MeshModel"/>.</summary>
    /// <param name="mesh">The target's mesh, whose registry-slot facts it carries.</param>
    /// <returns>The camera distance in world units (1/256 ft).</returns>
    /// <remarks>
    /// <c>+0x08 == meshExtent &lt;&lt; (8 + scaleShiftExponent)</c> is a 23/23 measured law over the
    /// registry (<c>ClassRecord</c> remarks), which is how the extent arm is fed from the model.
    /// </remarks>
    public static int SilhouetteCameraDistance(Core.Model.World.MeshModel mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        int shift = 8 + mesh.ScaleShiftExponent;
        int rawExtent = shift >= 0 ? mesh.MeshExtent << shift : mesh.MeshExtent >> -shift;
        int lod0 = mesh.LodThresholds.Count > 0 ? mesh.LodThresholds[0] : 0;
        return SilhouetteCameraDistance(mesh.TargetPanelCameraDistanceSteps, rawExtent, lod0);
    }

    /// <summary>
    /// The panel projector's zoom shift: the literal <c>7</c> pushed at <c>image@0x0A776</c>, in the
    /// same <c>polygon_fill_mesh_render_setup</c> slot the hangar's 3-D view pushes <c>8</c> in
    /// (<c>image@0x269FA</c>).
    /// </summary>
    /// <remarks>
    /// The projector's focal length is <c>2^shift</c> design pixels
    /// (<see cref="CameraLens.OriginalFovDegrees"/>), so the panel's 64-column window has a 28.07°
    /// horizontal field.  Cross-checked against the atlas: at the 102 400-unit camera distance a
    /// MiG-21's 28 928-unit wingspan subtends <c>128 · 28928 / 102400</c> = 36 design pixels, and the
    /// seven captured silhouettes measure 21..45 px across — a fighter seen nose-on or tail-on,
    /// which is what a camera on the player-to-target line sees.
    /// </remarks>
    public const int SilhouetteZoomShift = 7;

    /// <summary>
    /// The label table the panel's second line indexes — <c>g_ai_maneuver_name_table [0x0F1C]</c>,
    /// sixteen DGROUP near pointers at <c>image@0x3CC7C</c>.
    /// </summary>
    public const int ManoeuvreTableDgroup = 0x0F1C;

    /// <summary>How many entries it has.</summary>
    public const int ManoeuvreTableCount = 16;

    /// <summary>
    /// The radar lock-state label table — five DGROUP near pointers at <c>image@0x3CD50</c>, whose
    /// entry 2 is a NULL the drawer guards on (<c>image@0x0A834</c>).
    /// </summary>
    public const int LockStateTableDgroup = 0x0FF0;

    /// <summary>How many entries it has.</summary>
    public const int LockStateTableCount = 5;

    /// <summary>
    /// The per-PHASE gate byte array the lock-state line is additionally gated on:
    /// <c>test byte [bx + 0x0F0E], 1</c> with <c>bx = block[+0x0D]</c> (<c>image@0x0A829</c>).
    /// </summary>
    public const int PhaseGateTableDgroup = 0x0F0E;
}

/// <summary>
/// What the host feeds the TARGET window's contents.
/// </summary>
/// <param name="Manoeuvre">
/// Its current AI manoeuvre's name, or empty when the class is not engage-capable
/// (<c>proto[+0x0C] &amp; 8</c>).
/// </param>
/// <param name="LockState">
/// Its radar lock-state label, or empty — the table's entry 2 is a NULL and the phase gate can also
/// close.
/// </param>
/// <param name="Clock">The clock bearing line, e.g. <c>11 O'CLOCK</c>.</param>
/// <param name="Speed">The speed line, e.g. <c>496 MPH</c>, or empty when its two gates fail.</param>
/// <param name="Range">The range line, e.g. <c>671'</c>.</param>
/// <param name="RangeCentred">
/// Whether the range is CENTRED rather than right-aligned: the original's <c>[bp-0xC]</c> selector,
/// which is 1 until the speed line is produced and 0 afterwards (<c>image@0x0A802</c> /
/// <c>image@0x0A859</c>).  So a panel with no speed line centres its range instead.
/// </param>
/// <param name="Caption">
/// The target's own CAPTION — the mission's <c>pilot_name</c> attribute, which the scenario loader interns
/// and points at from the engagement block's <c>+0x1E</c> (<c>image@0x0A1C9</c> writes the pointer,
/// <c>image@0x0EE6C</c> reads it).  Measured on the original: a wingman in the player's own flight reads
/// <c>Wingman</c>, centred in the FOOTER band beside the weapon-station ladder.  Empty when the block carries
/// no pointer.
/// </param>
/// <param name="WeaponStation">The target's <c>block[+0x05] &amp; 3</c> — the footer ladder's length.</param>
public readonly record struct TargetPanelState(
    string Manoeuvre = "",
    string LockState = "",
    string Clock = "",
    string Speed = "",
    string Range = "",
    bool RangeCentred = false,
    string Caption = "",
    int WeaponStation = 0);
