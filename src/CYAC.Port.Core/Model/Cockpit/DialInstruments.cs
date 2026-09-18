using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Model.Cockpit;

/// <summary>
/// H10a fix pass — what <c>s_dial_instrument_record +0x0C</c> really is: <b>the per-needle palette
/// indices</b>, one byte per needle, NOT a drawing style.
/// </summary>
/// <remarks>
/// <para>
/// The whole of a dial's drawing is <c>dial_slot_lines_draw @image@0x0195C</c>, and it is nine
/// instructions long:
/// </para>
/// <code>
/// di = slot + 0x16                       // the keyframe endpoint array
/// for (si = 0; si &lt; slot[+0x14]; si++, di += 4)
///     line(pivot = slot[+0x08], slot[+0x0A],
///          end   = di[0], di[2],
///          colour = 0xFF00 | slot[si + 0x0C])       // image@0x01985: mov al,[bx+si+0x0C]
/// </code>
/// <para>
/// So byte <c>si</c> of the "kind word" is the COLOUR of needle <c>si</c>, and the number of needles
/// is <c>keyframeCount</c>.  <c>0x0C0C</c>, <c>0x0404</c>, <c>0x0909</c> and <c>0x0F0F</c> are
/// palette 12, 4, 9 and 15 with the second byte a duplicate the tool filled in — and the ONE record
/// that proves the two bytes are independent is the F-86's slot 3, <c>0x0C04</c> with
/// <c>keyframeCount = 2</c>: a colour-12 needle and a colour-4 needle on one dial.
/// </para>
/// <para>
/// Corrected here — those were an OPEN hypothesis, and the records contradicted it all along:
/// a "text" slot carries a <c>needleAngleOffset</c>, an <c>amplitude</c> and a
/// <c>directionInvert</c>, which mean nothing to text.  There is exactly ONE numeric readout in the
/// whole cockpit and it is not a kind: <c>cockpit_dial_state_compute_all @image@0x0200A</c> arms the
/// far callback <c>[0xECC8]/[0xECCA] = 0x108E:0x13A0</c> = <c>dial_numeric_text_blit
/// @image@0x01C80</c> on SLOT 0 alone, and only on the frame the altitude's THOUSANDS digit changes
/// (<c>image@0x01FF8</c> compares it against <c>[0xB164]</c>).  The F-4 arms a second callback on
/// slot 5 when the afterburner bit flips, <c>[0xEB18]/[0xEB1A] = 0x108E:0x1470</c>
/// (<c>image@0x020D8</c>).
/// </para>
/// </remarks>
public static class DialStyle
{
    /// <summary>The colour of needle <paramref name="needle"/> of a slot.</summary>
    /// <param name="kindWord">The record's <c>+0x0C</c> word.</param>
    /// <param name="needle">Which needle, 0-based.</param>
    /// <returns>A palette index.</returns>
    public static byte NeedleColor(int kindWord, int needle) =>
        (byte)((kindWord >> (8 * (needle & 1))) & 0xFF);

    /// <summary>Whether a slot draws anything at all.</summary>
    /// <param name="kindWord">The record's <c>+0x0C</c> word.</param>
    public static bool IsDrawn(int kindWord) => kindWord != 0;
}

/// <summary>What each of the ten dial slots is fed, in slot order.</summary>
/// <remarks>
/// The VALUE names the instrument; the sources are
/// <c>cockpit_dial_state_compute_all @image@0x01FCF</c> phases 3..13.
/// </remarks>
public enum DialSlotRole
{
    /// <summary>Slot 0 — altitude modulo 1,000 feet (<c>image@0x01FED</c> divides by 1,000).</summary>
    Altimeter = 0,

    /// <summary>Slot 1 — <c>g_vertical_speed_mid_i16 [0xF1C1]</c> (<c>image@0x0203B</c>).</summary>
    VerticalSpeed = 1,

    /// <summary>Slot 2 — the player object's TAS at <c>+0x26</c> (<c>image@0x02054</c>).</summary>
    Airspeed = 2,

    /// <summary>Slot 3 — the compass / directional gyro (<c>image@0x0206C</c>).</summary>
    Compass = 3,

    /// <summary>Slot 4 — the relative bearing pointer (<c>image@0x020A6</c>, ⅛-degree BAM).</summary>
    BearingPointer = 4,

    /// <summary>
    /// Slot 5 — <c>[0xF035]</c> (<c>image@0x020F6</c>), which <c>image@0x0C728</c> prints as <c>"THR: %3d%%"</c>: the
    /// THROTTLE PERCENT.  the F-86's own THR gauge in a captured frame of the original reads 1,045 BAM against
    /// the 1,069 this record maps 100 % to, one pixel of the tip's quantisation.
    /// </summary>
    Throttle = 5,

    /// <summary>Slot 6 — the fuel level's mid word <c>[0xF059]</c> (<c>image@0x0210B</c>).</summary>
    Fuel = 6,

    /// <summary>Slot 7 — <c>g_engagement_pct_meter_a [0xF1D8]</c>, the airframe meter.</summary>
    PercentMeterA = 7,

    /// <summary>Slot 8 — <c>g_engagement_pct_meter_b [0xF1D9]</c>.</summary>
    PercentMeterB = 8,

    /// <summary>Slot 9 — <c>g_engagement_pct_meter_c [0xF1DA]</c>.</summary>
    PercentMeterC = 9,
}

/// <summary>One dial slot of one aircraft, as <c>dialinit.json</c> carries it.</summary>
/// <param name="Slot">Its index 0..9 — also its <see cref="DialSlotRole"/>.</param>
/// <param name="Present">Whether this aircraft has the instrument.</param>
/// <param name="Rect">The instrument's rectangle in the 320×200 design space.</param>
/// <param name="Pivot">Where the needle turns.</param>
/// <param name="KindWord">
/// <c>+0x0C</c> — the per-needle palette indices, byte <c>i</c> for needle <c>i</c>
/// (<see cref="DialStyle"/>).
/// </param>
/// <param name="Param0">The low end of the value range (<c>+0x0E</c>).</param>
/// <param name="Param1">The high end (<c>+0x10</c>).</param>
/// <param name="NeedleAngleOffset">The angle added after the range map, in ⅛-degree BAM (<c>+0x12</c>).</param>
/// <param name="KeyframeCount">How many NEEDLES the slot draws (<c>+0x14</c>).</param>
/// <param name="Amplitude">The needle's length in pixels (<c>+0x1E</c>).</param>
/// <param name="DirectionInvert">Whether the mapped angle is negated before the offset (<c>+0x46</c>).</param>
public readonly record struct DialSlot(
    int Slot,
    bool Present,
    PanelRect Rect,
    PanelPoint Pivot,
    int KindWord,
    short Param0,
    short Param1,
    int NeedleAngleOffset,
    int KeyframeCount,
    int Amplitude,
    bool DirectionInvert)
{
    /// <summary>What the slot is fed.</summary>
    public DialSlotRole Role => (DialSlotRole)Slot;

    /// <summary>How many needles it draws — <c>image@0x01994</c>'s loop bound.</summary>
    public int NeedleCount => Math.Max(0, KeyframeCount);

    /// <summary>The palette index of one of its needles.</summary>
    /// <param name="needle">Which needle, 0-based.</param>
    public byte NeedleColor(int needle) => DialStyle.NeedleColor(KindWord, needle);

    /// <summary>Whether the slot draws anything.</summary>
    public bool IsDrawn => Present && Rect.IsPresent && NeedleCount > 0 && DialStyle.IsDrawn(KindWord);
}

/// <summary>The ten dial slots of all six flyable aircraft (<c>dialinit.json</c>).</summary>
/// <remarks>
/// <c>cockpit_layout_load_per_aircraft @image@0x01DAA</c> copies the flown aircraft's section into
/// the ten <c>s_dial_instrument_record</c> slots, record <c>i</c> into slot <c>i</c>.
/// </remarks>
public sealed class DialLayout
{
    /// <summary>How many dial slots a cockpit has.</summary>
    public const int SlotCount = 10;

    private readonly IReadOnlyList<IReadOnlyList<DialSlot>> _byAircraft;

    private DialLayout(IReadOnlyList<IReadOnlyList<DialSlot>> byAircraft) => _byAircraft = byAircraft;

    /// <summary>One aircraft's ten slots, in slot order.</summary>
    /// <param name="aircraftIndex">Its index 0..5.</param>
    public IReadOnlyList<DialSlot> For(int aircraftIndex) => _byAircraft[aircraftIndex];

    /// <summary>How many aircraft the document carries.</summary>
    public int AircraftCount => _byAircraft.Count;

    /// <summary>Reads the layout out of an opened data tree.</summary>
    /// <param name="tree">The transformed data tree.</param>
    public static DialLayout Load(DataTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return From(tree.DialInit);
    }

    /// <summary>Builds the layout from an already-read document.</summary>
    /// <param name="document">The <c>dialinit.json</c> document.</param>
    /// <exception cref="InvalidDataException">A section is missing or malformed.</exception>
    public static DialLayout From(DialInitDocumentDto document)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<DialCockpitDto> cockpits = document.Cockpits ?? [];
        List<IReadOnlyList<DialSlot>> byAircraft = new List<IReadOnlyList<DialSlot>>(cockpits.Count);
        foreach (DialCockpitDto cockpit in cockpits.OrderBy(c => c.AircraftIndex))
        {
            List<DialSlot> slots = new List<DialSlot>(SlotCount);
            foreach (DialSlotDto slot in (cockpit.Instruments ?? []).OrderBy(s => s.Slot))
            {
                List<int> rect = slot.Rect ?? [0, 0, 0, 0];
                List<int> pivot = slot.Pivot ?? [0, 0];
                if (rect.Count != 4 || pivot.Count != 2)
                {
                    throw new InvalidDataException(
                        $"dialinit.json: aircraft {cockpit.AircraftIndex} slot {slot.Slot} has a "
                            + "malformed rect or pivot");
                }

                slots.Add(new DialSlot(
                    slot.Slot,
                    slot.Present,
                    new PanelRect(rect[0], rect[1], rect[2], rect[3]),
                    new PanelPoint(pivot[0], pivot[1]),
                    PortHex.ParseOrDefault(slot.KindWord),
                    unchecked((short)slot.Param0),
                    unchecked((short)slot.Param1),
                    slot.NeedleAngleOffset,
                    slot.KeyframeCount,
                    slot.Amplitude,
                    slot.DirectionInvert != 0));
            }

            if (slots.Count != SlotCount)
            {
                throw new InvalidDataException(
                    $"dialinit.json: aircraft {cockpit.AircraftIndex} has {slots.Count} slots, "
                        + $"expected {SlotCount}");
            }

            byAircraft.Add(slots);
        }

        return new DialLayout(byAircraft);
    }
}

/// <summary>
/// The dial VALUE → NEEDLE-ANGLE law, ported from <c>dial_slot_value_compute @image@0x02162</c>.
/// </summary>
/// <remarks>
/// <para>
/// The whole of it, in the order the bytes do it:
/// </para>
/// <code>
/// v      = clamp(value, param0, param1)                       // SIGNED, image@0x02189..0x0219A
/// angle  = ((u16)(v − param0) · 0xB40) / ((u16)(param1 − param0) + 1)   // UNSIGNED, image@0x0219B..0x021AB
/// if (directionInvert) angle = −angle                         // image@0x021AC..0x021B2
/// angle += needleAngleOffset                                  // image@0x021B4
/// wrap angle into [0, 0xB40)                                  // image@0x021B7..0x021C6
/// </code>
/// <para>
/// <c>0xB40</c> = 2,880 = 360°, the engine's ⅛-degree BAM.  Two details are load-bearing and easy to
/// lose: the clamp is signed while the divide is unsigned (which is what lets the VSI's
/// <c>param0 = −800</c> work — <c>(u16)(800 − 0xFCE0) = 1600</c>), and the denominator carries a
/// <c>+1</c> (<c>inc bx</c> <c>image@0x021A9</c>), so a 0..1,000 dial sweeps 2,877 of the 2,880
/// units, never quite reaching its own top.
/// </para>
/// <para>
/// The NEEDLE ENDPOINT (<c>image@0x021CE..0x02224</c>) is
/// <c>tip = pivot + (amplitude·sin θ·k, −amplitude·cos θ)</c>: the two trig lookups are the Q14
/// navigation pair (<c>angle_sin_table_lookup @image@0x18394</c> returns <c>cos θ</c>), each product
/// is scaled by four and ROUNDED (<c>shl ax,1 / adc dx,0</c> — a rounding, not a fifth doubling),
/// and <c>16383·4/65536 ≈ 1</c>, so the needle's radius is exactly its <c>amplitude</c> in pixels.
/// The X term carries one more factor, the runtime <c>i32 g_view2d_xform_b [0x672:0x674]</c> (sole
/// writer <c>image@0x155D9</c>, a viewport ratio the mesh pipeline leaves behind); its value at dial
/// time is <b>(open)</b>, and the port uses 1, which draws the sweep circular in the 320×200 design
/// space the dial faces were painted in.
/// </para>
/// </remarks>
public static class DialNeedle
{
    /// <summary>A full turn in the engine's angle space: <c>0xB40</c> = 2,880 = 360°.</summary>
    public const int FullTurnBam = 0x0B40;

    /// <summary>Maps a slot's value to its needle angle in ⅛-degree BAM, 0 up and clockwise.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="value">The value the per-frame compute feeds it.</param>
    /// <returns>The angle in <c>[0, 0xB40)</c>.</returns>
    public static int Angle(in DialSlot slot, int value)
    {
        short low = slot.Param0;
        short high = slot.Param1;

        // image@0x02189..0x0219A — the clamp is signed, and the low bound is applied first.
        int v = value;
        if (v < low)
        {
            v = low;
        }
        else if (v > high)
        {
            v = high;
        }

        // image@0x0219B..0x021AB — unsigned 16-bit differences, a 32-bit multiply, a 16-bit divide.
        uint numerator = (uint)(ushort)unchecked((short)(v - low)) * FullTurnBam;
        uint span = (uint)(ushort)unchecked((short)(high - low)) + 1u;
        int angle = (int)(numerator / span);

        if (slot.DirectionInvert)
        {
            angle = -angle;                                   // image@0x021B2
        }

        angle += slot.NeedleAngleOffset;                      // image@0x021B4
        return Wrap(angle);
    }

    /// <summary>Wraps an angle into <c>[0, 0xB40)</c> — <c>image@0x021B7..0x021C6</c>.</summary>
    /// <param name="angleBam">Any angle in ⅛-degree units.</param>
    public static int Wrap(int angleBam)
    {
        int wrapped = angleBam % FullTurnBam;
        return wrapped < 0 ? wrapped + FullTurnBam : wrapped;
    }

    /// <summary>The needle's tip for an angle, in the 320×200 design space.</summary>
    /// <param name="slot">The slot, for its pivot and amplitude.</param>
    /// <param name="angleBam">The angle from <see cref="Angle"/>.</param>
    /// <returns>The tip, sub-pixel.</returns>
    /// <remarks>
    /// Continuous where the original reads a 721-entry Q14 quarter table, so the drawn needle can be
    /// anti-aliased; the two agree to the table's own ±1 rounding.
    /// </remarks>
    public static (double X, double Y) Tip(in DialSlot slot, int angleBam)
    {
        double theta = angleBam * 2.0 * Math.PI / FullTurnBam;
        return (
            slot.Pivot.X + (slot.Amplitude * Math.Sin(theta)),
            slot.Pivot.Y - (slot.Amplitude * Math.Cos(theta)));
    }

    /// <summary>
    /// Where a value sits in a slot's own range, 0..1 — the range map alone, without the inversion
    /// and the offset a needle then applies.  This is what a tape or a bar wants.
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <param name="value">The value.</param>
    public static double Fraction(in DialSlot slot, int value)
    {
        int v = Math.Clamp(value, slot.Param0, slot.Param1);
        uint span = (uint)(ushort)unchecked((short)(slot.Param1 - slot.Param0)) + 1u;
        return (ushort)unchecked((short)(v - slot.Param0)) / (double)span;
    }
}

/// <summary>
/// The ONE numeric readout in the cockpit: the altimeter's THOUSANDS drum, and where it is drawn —
/// <c>dial_numeric_text_blit @image@0x01C80</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>cockpit_dial_state_compute_all</c> arms this callback on slot 0 alone and only on the frame the
/// thousands digit changes (<c>image@0x01FF8</c> against <c>g_cockpit_redraw_sentinel [0xB164]</c>,
/// which <c>image@0x02016</c> then sets to <c>altitude_ft / 1000</c>).  The body reads slot 0's
/// geometry, and it does NOT use the rectangle's corner:
/// </para>
/// <code>
/// block.x = slot0[+0x00] (rect.x)     block.w = slot0[+0x04] (rect.w)
/// block.y = slot0[+0x0A] (pivot.y)    block.h = 6                        // image@0x01CBA..0x01CD1
/// si      = slot0[+0x08] (pivot.x) &amp; ~3                                // image@0x01CD3
/// switch (g_active_aircraft_idx) {                            // image@0x01CE8, table @0x01CED
///   case 0: case 2: si += 8; block.y -= 2; cx = 2; break;     // 0x01D0C → 0x01D14 → 0x01CF9
///   case 3:         si += 4; block.y -= 2; cx = 2; break;     // 0x01D11 → 0x01D14 → 0x01CF9
///   case 1: case 5: si -= 4; block.y -= 7; cx = 3; break;     // 0x01D00 → 0x01D27
///   case 4:         si -= 4; block.y += 3; cx = 3; break;     // 0x01D1D → 0x01D27
/// }
/// itoa_padded(buf, [0xB164], cx, '0');  glyph_blit_dispatch(buf, si, block.y);
/// </code>
/// <para>
/// The digit COUNT is per-aircraft too, and it is easy to miss because it is carried in <c>DX</c>:
/// <c>image@0x01CDA</c> sets <c>dx = 2</c> before the switch, three arms fall through
/// <c>image@0x01CF9</c>'s <c>mov cx, dx</c> and keep it, and the other three reach
/// <c>image@0x01D27</c>'s <c>mov cx, 3</c>.  So the F-86 shows <b>two</b> digits — <c>"02"</c> at
/// 2,619 ft, which is exactly what a captured frame of the original reads — and the F-4, the
/// FW-190 and the MiG-21 show three.
/// </para>
/// </remarks>
public static class DialNumericReadout
{
    /// <summary>Which slot carries it: slot 0, the altimeter (<c>image@0x0200A</c>).</summary>
    public const int Slot = 0;

    /// <summary>The block's height in design rows (<c>image@0x01CCE</c>).</summary>
    public const int Height = 6;

    // image@0x01CED — the six-entry jump table, as (dx, dy, digits): the nudge applied to
    // (pivot.x & ~3, pivot.y), and the width the arm leaves in CX.
    private static readonly (int Dx, int Dy, int Digits)[] Arms =
    [
        (8, -2, 2),    // 0 p51    → image@0x01D0C → 0x01D14 → 0x01CF9 (cx = dx = 2)
        (-4, -7, 3),   // 1 fw190  → image@0x01D00 → 0x01D27 (cx = 3)
        (8, -2, 2),    // 2 f86    → image@0x01D0C → 0x01D14 → 0x01CF9
        (4, -2, 2),    // 3 mig15  → image@0x01D11 → 0x01D14 → 0x01CF9
        (-4, 3, 3),    // 4 f4     → image@0x01D1D → 0x01D27
        (-4, -7, 3),   // 5 mig21  → image@0x01D00 → 0x01D27
    ];

    /// <summary>Where the thousands drum's text starts, in the 320×200 design space.</summary>
    /// <param name="slot">The aircraft's slot 0.</param>
    /// <param name="aircraftIndex">Its index 0..5 — <c>g_active_aircraft_idx [0xC31A]</c>.</param>
    /// <returns>The pen position the glyph blitter is given.</returns>
    public static PanelPoint Origin(in DialSlot slot, int aircraftIndex)
    {
        (int Dx, int Dy, int Digits) arm = Arms[Math.Clamp(aircraftIndex, 0, Arms.Length - 1)];
        return new PanelPoint((slot.Pivot.X & ~3) + arm.Dx, slot.Pivot.Y + arm.Dy);
    }

    /// <summary>How many zero-padded digits this aircraft's drum shows.</summary>
    /// <param name="aircraftIndex">Its index 0..5.</param>
    public static int Digits(int aircraftIndex) =>
        Arms[Math.Clamp(aircraftIndex, 0, Arms.Length - 1)].Digits;

    /// <summary>The text itself — the thousands, zero-padded to this aircraft's width.</summary>
    /// <param name="thousands"><c>[0xB164]</c> = <c>altitude_ft / 1000</c> (<c>image@0x02016</c>).</param>
    /// <param name="aircraftIndex">Its index 0..5.</param>
    public static string Text(int thousands, int aircraftIndex) =>
        thousands.ToString(System.Globalization.CultureInfo.InvariantCulture)
            .PadLeft(Digits(aircraftIndex), '0');
}

/// <summary>
/// The cockpit's NAVIGATION bearing: what feeds dial slot 4 (the relative bearing pointer) and, on
/// the F-86 alone, the first needle of dial slot 3.
/// </summary>
/// <remarks>
/// <para>
/// <c>object_bearing_to_slot_compute @image@0x08E72</c> takes the world position of nav slot
/// <c>g_nav_slot_current_index [0xEF92]</c> out of <c>g_nav_slot_record_array [0xB564]</c> (via
/// <c>slot_record_get_pos @image@0x08E0D</c>) and calls <c>bearing_angle_compute @image@0x185E6</c>
/// with the player's own <c>+0x06</c> (X) and <c>+0x0E</c> (Z) — a pure XZ bearing, altitude
/// ignored.  With <c>AL = 1</c> it then subtracts the player's heading <c>+0x12</c>
/// (<c>image@0x08EBE</c>), wraps into <c>[0, 0xB40)</c> and normalises into
/// <c>(−0x5A0, +0x5A0]</c> (<c>angle_normalize_i16 @image@0x244C8</c>) — which is exactly slot 4's
/// own <c>param0/param1</c> pair, <c>−1440 … +1440</c>.  With <c>AL = 0</c> the wrap alone is
/// applied and the value is an ABSOLUTE bearing in <c>[0, 0xB40)</c>, which is slot 3's range.
/// </para>
/// <para>
/// If the nav slot is empty <c>slot_record_get_pos</c> returns 0 and the whole function returns 0
/// (<c>image@0x08E89</c>), so a Test Flight leaves both needles at their zero angle — which is
/// straight up for slot 4 (<c>needleAngleOffset 1440</c>, <c>directionInvert</c>) and north for the
/// compass card.
/// </para>
/// </remarks>
public static class NavBearing
{
    /// <summary>
    /// The ABSOLUTE bearing from one world XZ position to another, in the heading's own BAM sense.
    /// </summary>
    /// <param name="fromX">The observer's world X.</param>
    /// <param name="fromZ">Its world Z.</param>
    /// <param name="toX">The target's world X.</param>
    /// <param name="toZ">Its world Z.</param>
    /// <returns><c>[0, 0xB40)</c>.</returns>
    /// <remarks>
    /// The port computes the angle in the renderer's own world convention — heading 0 looks along
    /// <c>+Z</c> and grows toward <c>−X</c> (<c>CameraPose.HeadingDegrees</c>) — so the returned BAM
    /// is directly comparable with the aircraft's heading word, which is what the original's
    /// <c>sub si, es:[bx+0x12]</c> relies on.
    /// </remarks>
    public static int Absolute(double fromX, double fromZ, double toX, double toZ)
    {
        double radians = Math.Atan2(-(toX - fromX), toZ - fromZ);
        double bam = radians * DialNeedle.FullTurnBam / (2.0 * Math.PI);
        return DialNeedle.Wrap((int)Math.Round(bam));
    }

    /// <summary>The bearing relative to a heading, normalised the way the original's tail does.</summary>
    /// <param name="absoluteBam">The absolute bearing.</param>
    /// <param name="headingBam">The aircraft's heading word.</param>
    /// <returns><c>(−0x5A0, +0x5A0]</c> — <c>angle_normalize_i16 @image@0x244C8</c>.</returns>
    public static int Relative(int absoluteBam, int headingBam) =>
        Normalize(absoluteBam - headingBam);

    /// <summary>
    /// <c>angle_normalize_i16 @image@0x244C8</c> — the SINGLE-SHOT signed wrap into
    /// <c>(−0x5A0, +0x5A0]</c> the bearing helper's tail applies.
    /// </summary>
    /// <param name="angleBam">An angle already wrapped into <c>[0, 0xB40)</c> by the caller.</param>
    public static int Normalize(int angleBam)
    {
        int wrapped = DialNeedle.Wrap(angleBam);
        return wrapped > DialNeedle.FullTurnBam / 2 ? wrapped - DialNeedle.FullTurnBam : wrapped;
    }
}

/// <summary>
/// The ONE dial parameter the loader rewrites at run time: the FUEL gauge's full scale.
/// </summary>
/// <remarks>
/// <para>
/// <c>cockpit_layout_load_per_aircraft @image@0x01DAA</c> copies the flown aircraft's ten 72-byte
/// records out of <c>dialinit.bin</c> (<c>image@0x01DE2</c> and nine siblings, stride <c>0x48</c>,
/// aircraft stride <c>0x2D0</c>) and then patches exactly one word:
/// </para>
/// <code>
/// image@0x01EE8: if (g_active_aircraft_idx is 0 or 1 or 3)   // P-51, FW-190, MiG-15
/// image@0x01F0D:     ax = [0xF060] &lt;&lt; 1
/// image@0x01EFD: else
/// image@0x01F06:     ax = muldiv16_signed([0xF060], 4, 3)    // image@0x11974
/// image@0x01F12: [0xEB36] = ax                               // slot 6's param1
/// </code>
/// <para>
/// <c>[0xF060]</c> is master <c>+0xC8</c>, the aircraft's INITIAL FUEL LOAD in pounds (1,614 P-51 /
/// 1,632 FW-190 / 2,040 F-86 / 1,680 MiG-15 / 9,293 F-4 / 3,105 MiG-21), so the shipped
/// <c>param1 = 0</c> in <c>dialinit.json</c> is a placeholder for five of the six aircraft and the
/// P-51's 1,000 is a leftover — every one of them is overwritten before a frame is drawn.  Without
/// the patch the value clamps to zero and the needle is PINNED at <c>needleAngleOffset</c>, which is
/// what the port drew.
/// </para>
/// <para>
/// <b>Verified from the original's pixels.</b> In a captured frame of the original the F-86's
/// "F" gauge needle runs from its pivot <c>(247,157)</c> to <c>(254,162)</c> — 125.5°, i.e. 1,004
/// BAM.  With this patch <c>param1 = 2040·4/3 = 2720</c>, and 1,004 BAM inverts to a fuel level of
/// 1,969 lb of 2,040 — a 96 %-full tank a few minutes into the sortie.  Without it the angle is the
/// bare offset 1,800 BAM = 225°, pointing the other way, which is exactly the mismatch that found
/// this.  this was the second independent confirmation.)
/// </para>
/// </remarks>
public static class FuelGauge
{
    /// <summary>The dial slot the patch lands on: 6.</summary>
    public const int Slot = 6;

    /// <summary>Slot 6's run-time <c>param1</c> for one aircraft.</summary>
    /// <param name="aircraftIndex"><c>g_active_aircraft_idx [0xC31A]</c>, 0..5.</param>
    /// <param name="fuelCapacity">master <c>+0xC8</c> = <c>[0xF060]</c>, in pounds.</param>
    /// <returns>The full-scale value, as a signed word.</returns>
    public static short FullScale(int aircraftIndex, int fuelCapacity) =>
        aircraftIndex is 0 or 1 or 3
            ? unchecked((short)(fuelCapacity * 2))                   // image@0x01F10
            : unchecked((short)(fuelCapacity * 4 / 3));              // image@0x01F06

    /// <summary>The aircraft's slot 6 with its run-time full scale in place.</summary>
    /// <param name="slot">The slot as <c>dialinit.json</c> ships it.</param>
    /// <param name="aircraftIndex">Its aircraft index.</param>
    /// <param name="fuelCapacity">master <c>+0xC8</c>.</param>
    public static DialSlot Patch(in DialSlot slot, int aircraftIndex, int fuelCapacity) =>
        slot with { Param1 = FullScale(aircraftIndex, fuelCapacity) };
}
