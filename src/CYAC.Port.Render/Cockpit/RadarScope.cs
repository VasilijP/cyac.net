namespace CYAC.Port.Render.Cockpit;

/// <summary>
/// How one contact is shown on the RWR, from <c>pixel_obj_color_select_rwr @image@0x0D374</c>.
/// </summary>
/// <remarks>
/// The callback returns <c>AL = [0x3260]</c> (the blip colour) always and varies only <c>AH</c>,
/// which <c>gfx_draw_pixel_clipped @image@0x130A6</c> feeds to <c>gfx_set_active_color
/// @image@0x139A4</c>: <c>AH = 0xFF</c> latches the SOLID sentinel, any other value latches a dither
/// mask built from it, and <c>AH = 0</c> makes that mask zero — so the Mode-X pixel drawer
/// (<c>image@0x1321E..0x13236</c>) writes nothing.  <c>AH = 0</c> is therefore "not drawn".
/// </remarks>
public enum ScopeThreat
{
    /// <summary>
    /// The object's engagement block is not radiating — <c>eng[+0x05] &amp; 8 == 0</c>
    /// (<c>image@0x0D387</c>).  The RWR shows nothing for it.
    /// </summary>
    Silent = 0,

    /// <summary>A radiating emitter that is not tracking us: a solid blip.</summary>
    Steady = 1,

    /// <summary>
    /// A radiating emitter whose acquisition state is TRACKING (<c>eng[+0x11] == 3</c>) and whose
    /// current target is the player (<c>eng[+0x1B] == [0x00C0]</c>) — the blip blanks on every EVEN
    /// value of <c>g_rwr_sample_counter [0xF1CA]</c> (<c>image@0x0D394..0x0D3AC</c>), i.e. it blinks
    /// once per frame.
    /// </summary>
    Blinking = 2,
}

/// <summary>
/// One contact as the two scopes' shared projector places it (<c>pixel_obj_world_to_screen_project
/// @image@0x0D3B4</c>).
/// </summary>
/// <param name="RightFeet">
/// Its offset to the player's RIGHT, in feet, after the projector's rotation by
/// <c>wrap(-camera heading)</c> (<c>image@0x0D403..0x0D417</c>).
/// </param>
/// <param name="AheadFeet">Its offset AHEAD of the player, in feet, from the same rotation.</param>
/// <param name="ManhattanFeet">
/// <c>|Δx| + |Δz|</c> of the UNROTATED world delta, in feet — the metric
/// <c>pixel_obj_proximity_and_register @image@0x0D482</c> gates on (rotation does not preserve it,
/// so the port keeps both).
/// </param>
/// <param name="Threat">What the RWR would show for it; the radar has no such distinction.</param>
/// <param name="Locked">
/// Whether this is the player's designated target.  It is here for <b>W6</b> (which weapon may be
/// released at what), <b>not</b> for the scope: the original has NO IFF and no lock marking on either
/// face — every blip is the same palette-10 pixel (manual, "Detecting by Radar": "the only way to
/// identify a contact is to close to visual range").  A renderer that colours this differently is
/// inventing.
/// </param>
/// <param name="AtOrAbovePlayer">
/// Whether the contact's Y is at or above the player's.  The two SCOPES ignore it; the MAP window
/// picks its dot's nibble with it (<c>image@0x0D32C..0x0D344</c>, and
/// <see cref="MapWindow.DotPaletteIndex"/>).
/// </param>
/// <param name="MapDot">
/// Which of the four colour bytes the MAP window would use for it (<c>pixel_obj_project_forward_view
/// @image@0x0D2C0</c> chooses at REGISTER time and stores the byte in <c>slot[+4]</c>).  The scopes
/// have no such distinction — theirs is one colour for everything.
/// </param>
/// <param name="Wide">
/// Whether the dot is a TWO-pixel span instead of one: <c>pixel_obj_slot_register
/// @image@0x0D5C2..0x0D5D0</c> puts <c>prototype[+0x0C] &amp; 0x80</c> into <c>slot[+5]</c>, and the
/// render pass draws a horizontal line from <c>x</c> to <c>x+1</c> when that byte is non-zero
/// (<c>image@0x0D684..0x0D6A4</c>). In the shipped engagement table bit 7 is set on exactly three
/// prototypes — the <b>B-17E</b>, the <b>B-29</b> and the <b>B-52D</b>
/// (<c>data/exe/tables/engagement.json</c>, flags <c>0x98</c>/<c>0xDC</c>/<c>0xDC</c>) — so a BOMBER
/// plots as a wide dot and a fighter as one pixel.  It applies to all three pixel-obj contexts, the
/// radar and the RWR included.
/// </param>
public readonly record struct ScopeContact(
    double RightFeet,
    double AheadFeet,
    double ManhattanFeet,
    ScopeThreat Threat,
    bool Locked = false,
    bool AtOrAbovePlayer = false,
    MapDotColour MapDot = MapDotColour.Spawn,
    bool Wide = false);

/// <summary>
/// The live state of the aircraft's RADAR: the <b>R</b> switch, the CRT sweep clock and what the two
/// scopes plot.  Only the F-4E and the MiG-21MF have either instrument (every other aircraft's region
/// rectangle is zero-width in <c>data/exe/tables/cockpit_layout.json</c>).
/// </summary>
/// <param name="On">
/// <c>g_cockpit_region_1.flags [0x3F06]</c> bit 5, which <c>radar_mode_toggle_with_sweep_reset
/// @image@0x0E1E3</c> XORs.  SET is the BLIP arm — the radar is ON; CLEAR is the sweep arm, whose
/// first act is to fill the whole face with palette 0 (<c>image@0x0E246..0x0E25A</c>). MEASURED: a
/// mission starts with it CLEAR — <c>[0x3F06] = 0x0C</c> at 88 M instructions in two independent runs
/// (a captured frame of the original), so the radar starts OFF.
/// </param>
/// <param name="PlayerEmitting">
/// Bit 3 of the PLAYER's engagement flags byte (<c>+0x05</c>), which the same key XORs through
/// <c>g_player_engagement_block_farptr [0xEF1E]</c> (<c>image@0x0E1DE</c>; measured <c>[0xEF1E] =
/// player + 0x18</c>).  It is <b>simulation</b> state, not presentation: three combat functions read
/// it, and the port's own kernel already does — <c>CombatSpawnFireEligibility.Qualifies</c> requires
/// it of a kind-1 guided weapon's owner (<c>image@0x02D42</c>), which is the AIM-7's "your radar must
/// be on".  W1 does not WRITE it (the presentation layer is byte-inert); it publishes what the switch
/// would set so W6 can.
/// </param>
/// <param name="SweepPhase">
/// The sweep animation's position <c>si ∈ [0, 469)</c> (<c>image@0x0E285..0x0E290</c>), or −1 when
/// the sweep window has expired.  <see cref="RadarScope.SweepDuration"/> explains the number.
/// </param>
/// <param name="ScaleShift">
/// <c>g_stipple_scale_shift [0x3234]</c> — 9 at load (<c>image@0x00ABD</c>), clamped 8..11 by the map
/// window's own zoom keys (<c>image@0x012B2</c> / <c>image@0x012C7</c>).  One shift serves all three
/// pixel-obj contexts, so the MAP's zoom rescales the radar and the RWR too.
/// </param>
/// <param name="RwrSampleCounter">
/// <c>g_rwr_sample_counter [0xF1CA]</c>, incremented once per frame by the RWR's own draw
/// (<c>image@0x0E1A4</c>) — the RWR blink's phase.
/// </param>
/// <param name="Contacts">What the scopes may plot this frame, before each scope's own gates.</param>
public readonly record struct RadarState(
    bool On,
    bool PlayerEmitting,
    int SweepPhase,
    int ScaleShift,
    int RwrSampleCounter,
    IReadOnlyList<ScopeContact>? Contacts)
{
    /// <summary>A radar-less aircraft, or a sortie with no combat state.</summary>
    public static RadarState None => new(false, false, -1, RadarScope.DefaultScaleShift, 0, null);

    /// <summary>Whether the CRT collapse is playing this frame.</summary>
    public bool Sweeping => !On && SweepPhase >= 0;

    /// <summary>
    /// W6's seam — the player's designated target as the scopes see it, or null when nothing is
    /// designated.  The AIM-7's own gate does NOT test it (<c>image@0x02D42</c> asks only for the
    /// owner's emitter bit), so this is here for the missile logic to reason with, not to draw.
    /// </summary>
    public ScopeContact? LockedContact
    {
        get
        {
            foreach (ScopeContact contact in Contacts ?? [])
            {
                if (contact.Locked)
                {
                    return contact;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// W6's seam — whether an enemy radar is TRACKING us this frame (the RWR's blinking state):
    /// the manual's low-pitched "preparing" alarm, and what a countermeasure would answer.
    /// </summary>
    public bool BeingTracked
    {
        get
        {
            foreach (ScopeContact contact in Contacts ?? [])
            {
                if (contact.Threat == ScopeThreat.Blinking)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
/// The LAW of the radar monitor and the RWR: the projection both scopes share, the range gate, and the
/// CRT collapse the <b>R</b> key plays when the radar goes off.
/// </summary>
/// <remarks>
/// <para>
/// Sources.  <c>region_1_radar_monitor_draw_fn @image@0x0E212</c> (the two arms and the sweep),
/// <c>region_2_rwr_draw_fn @image@0x0E186</c>, <c>radar_mode_toggle_with_sweep_reset
/// @image@0x0E1C5</c> (the key), and the shared pixel-obj engine — <c>per_frame_object_pixel_setup
/// @image@0x0D6D8</c>, <c>pixel_obj_world_to_screen_project @image@0x0D3B4</c>,
/// <c>per_frame_object_pixel_render @image@0x0D60E</c>,.
/// </para>
/// <para>
/// The blips are NOT read out of <c>[0xBC4C]</c> / <c>[0xBC3E]</c>: those are the two contexts'
/// one-word <c>tick_deadline</c> slots (<c>image@0x0D6F1</c> reads, <c>image@0x0D775</c> writes back),
/// zeroed at cockpit load by <c>image@0x0E368/0x0E36B/0x0E36E</c> and written by nothing else in the
/// image.  Both scopes plot the live object pool through the shared projector.
/// </para>
/// <para>
/// Verified against the original to the pixel (a captured frame of the original, MiG-21 at 130
/// M instructions, player at (57704440, 751339, 10397781) heading 360, shift 9): object <c>0x5399</c>
/// at Δ(−145928, +55753) is predicted at (176, 190) and measured at (176, 190); object <c>0x53EB</c>
/// at Δ(−4821358, +3400390) is predicted at (173, 168) and measured at (173, 168).
/// </para>
/// </remarks>
public static class RadarScope
{
    /// <summary>
    /// <c>g_stipple_scale_shift [0x3234]</c>'s load value — <c>mov word [0x3234], 9</c>
    /// (<c>image@0x00ABD</c>).
    /// </summary>
    public const int DefaultScaleShift = 9;

    /// <summary>The map zoom keys' floor for the shared shift (<c>cmp [0x3234],8 / jle</c> @<c>image@0x012B9</c>).</summary>
    public const int MinScaleShift = 8;

    /// <summary>The map zoom keys' ceiling (<c>cmp [0x3234],0xB / jge</c> @<c>image@0x012CE</c>).</summary>
    public const int MaxScaleShift = 11;

    /// <summary>
    /// How long the CRT collapse runs, in <c>g_frame_time_accum [0xF0D2]</c> units:
    /// <c>add ax,0x1D5</c> (<c>image@0x0E1F6</c>).  The port's own
    /// <c>TickClock.FrameTimeAccumulator</c> is the same quantity.
    /// </summary>
    public const int SweepDuration = 469;

    /// <summary>The sweep's first phase boundary — <c>cmp si,0x55</c> (<c>image@0x0E291</c>).</summary>
    public const int SweepBandEnd = 0x55;

    /// <summary>The second — <c>cmp si,0xD5</c> (<c>image@0x0E2C7</c>).</summary>
    public const int SweepLineEnd = 0xD5;

    /// <summary>The divisor the mid-phase line's half-width uses (<c>mov bx,0x80</c> @<c>image@0x0E2D8</c>).</summary>
    public const int SweepLineDivisor = 0x80;

    /// <summary>
    /// The face colour both scopes are cleared to every frame, and the sweep's own colour: palette
    /// <c>[0x325F] = 2</c>.
    /// </summary>
    /// <remarks>
    /// <c>per_frame_object_pixel_render</c>'s step 0 fills the whole clip rect with the colour its
    /// context's colour-select callback returns for <c>SI = 0</c> (<c>image@0x0D611..0x0D628</c>), and
    /// both scope callbacks return <c>[0x325F]</c> there (<c>image@0x0D360</c> / <c>image@0x0D37A</c>).
    /// The byte is 2 in the shipped static block at <c>image@0x3EFBF</c> (DGROUP <c>0x325F</c>);
    /// <c>image@0x0D5FA</c> overwrites it with 0x0D only when <c>g_cfg_sub_mode [0x015E] == 0</c>, and
    /// the shipped 320×200 build runs sub-mode 6 (measured).  Region 2 has no fill of its OWN, but the
    /// shared render pass fills its rectangle every frame; measured as 219 px of palette 2 inside
    /// (200,154,24,17) at every captured tick.
    /// </remarks>
    public const int FacePaletteIndex = 2;

    /// <summary>
    /// A contact's colour: palette <c>[0x3260] = 10</c> (<c>image@0x3EFC0</c>), returned by both
    /// colour-select callbacks for a real slot (<c>image@0x0D35C</c> / <c>image@0x0D3AE</c>).
    /// </summary>
    /// <remarks>
    /// <c>if (slot_si == 0)</c> block holds <c>image@0x0D35C</c>, which is the <c>si != 0</c>
    /// fall-through (<c>0B F6 / 74 04</c> jumps to <c>0x0D360</c> when si IS zero).  Measured: the
    /// blips are <c>55FF55</c> = palette 10, the face is <c>00AA00</c> = palette 2.
    /// </remarks>
    public const int BlipPaletteIndex = 10;

    /// <summary>
    /// The own-ship marker: a palette-15 pixel drawn unconditionally at the context's centre by
    /// <c>per_frame_object_pixel_render</c> (<c>image@0x0D6CE</c>) — the manual's "the white dot at
    /// the bottom of the readout is your own aircraft".
    /// </summary>
    public const int OwnShipPaletteIndex = 15;

    /// <summary>
    /// The face's clear colour while the radar is OFF: palette 0, from the sweep arm's own fill
    /// (<c>mov ax,0xFF00</c> @<c>image@0x0E256</c> — 0xFF is the solid tag, the low byte the index).
    /// </summary>
    public const int OffPaletteIndex = 0;

    /// <summary>
    /// How many FEET one scope pixel covers at a given scale shift.
    /// </summary>
    /// <param name="scaleShift"><c>g_stipple_scale_shift [0x3234]</c>.</param>
    /// <returns>Feet per design pixel — 1,024 at the default shift 9.</returns>
    /// <remarks>
    /// The projector shifts the world delta right by 11 (<c>sar</c> ×3 then the middle-16 byte
    /// extract, <c>image@0x0D3CD..0x0D3DD</c>) and then by <c>shift − 2</c>
    /// (<c>image@0x0D418..0x0D422</c>), so a pixel is <c>2^(9+shift)</c> world units; the world unit
    /// is 1/256 ft, which leaves <c>2^(shift+1)</c> feet.
    /// </remarks>
    public static double FeetPerPixel(int scaleShift) => 1 << (scaleShift + 1);

    /// <summary>
    /// The range gate, in feet: <c>(width + height) &lt;&lt; (8 + shift)</c> world units
    /// (<c>image@0x0D732..0x0D74B</c>), against which
    /// <c>pixel_obj_proximity_and_register @image@0x0D482</c> compares the contact's 2-D Manhattan
    /// distance.
    /// </summary>
    /// <param name="rectWidth">The region rectangle's width.</param>
    /// <param name="rectHeight">Its height.</param>
    /// <param name="scaleShift">The shared scale shift.</param>
    /// <returns>The gate in feet — 41,472 for the MiG-21's 48×33 radar at shift 9.</returns>
    public static double RangeGateFeet(int rectWidth, int rectHeight, int scaleShift) =>
        (rectWidth + rectHeight) * (double)(1 << scaleShift);

    /// <summary>
    /// Where a contact lands on a scope, in design pixels.
    /// </summary>
    /// <param name="contact">The contact.</param>
    /// <param name="centreX">The scope's own-ship column — the region's pivot, <c>[0x3F10]</c>.</param>
    /// <param name="centreY">Its own-ship row — <c>[0x3F12]</c>.</param>
    /// <param name="scaleShift">The shared scale shift.</param>
    /// <returns>The contact's design-space point.</returns>
    /// <remarks>
    /// <c>x = centre + (rx &gt;&gt; (shift−2))</c> and <c>y = centre − (rz &gt;&gt; (shift−2))</c>
    /// (<c>image@0x0D424</c> / <c>image@0x0D430..0x0D432</c>) — the Y axis is inverted, which is what
    /// puts what is AHEAD of you ABOVE the own-ship dot.  The port keeps the division in doubles so
    /// the plot is crisp at host resolution: chrome is integer-scaled, contents are drawn at host
    /// resolution.
    /// </remarks>
    public static (double X, double Y) Plot(
        in ScopeContact contact, double centreX, double centreY, int scaleShift)
    {
        double perPixel = FeetPerPixel(scaleShift);
        return (centreX + (contact.RightFeet / perPixel), centreY - (contact.AheadFeet / perPixel));
    }

    /// <summary>
    /// Whether the RADAR plots this contact: the range gate alone — the radar's REGISTER callback is
    /// <c>stc; ret</c>, an unconditional accept (<c>pixel_obj_project_radar_accept_all
    /// @image@0x0D352</c>), and the original has no IFF (manual, "Detecting by Radar").
    /// </summary>
    /// <param name="contact">The contact.</param>
    /// <param name="rectWidth">The radar rectangle's width.</param>
    /// <param name="rectHeight">Its height.</param>
    /// <param name="scaleShift">The shared scale shift.</param>
    /// <returns>True when the radar may plot it.</returns>
    public static bool RadarShows(
        in ScopeContact contact, int rectWidth, int rectHeight, int scaleShift) =>
        contact.ManhattanFeet < RangeGateFeet(rectWidth, rectHeight, scaleShift);

    /// <summary>
    /// Whether the RWR plots this contact this frame: the range gate, the emitter bit, and the blink.
    /// </summary>
    /// <param name="contact">The contact.</param>
    /// <param name="rectWidth">The RWR rectangle's width.</param>
    /// <param name="rectHeight">Its height.</param>
    /// <param name="scaleShift">The shared scale shift.</param>
    /// <param name="sampleCounter"><c>g_rwr_sample_counter [0xF1CA]</c>.</param>
    /// <returns>True when the RWR draws a blip for it this frame.</returns>
    public static bool RwrShows(
        in ScopeContact contact, int rectWidth, int rectHeight, int scaleShift, int sampleCounter) =>
        contact.Threat != ScopeThreat.Silent
            && contact.ManhattanFeet < RangeGateFeet(rectWidth, rectHeight, scaleShift)
            && (contact.Threat != ScopeThreat.Blinking || (sampleCounter & 1) != 0);

    /// <summary>
    /// The sweep's phase this frame, or −1 once the window has closed.
    /// </summary>
    /// <param name="frameTime">The port's <c>TickClock.FrameTimeAccumulator</c> — <c>[0xF0D2]</c>.</param>
    /// <param name="deadline">When the sweep window closes — <c>[0xBCAA/AC]</c>.</param>
    /// <returns><c>si ∈ [0, 469)</c>, or −1.</returns>
    /// <remarks>
    /// The draw arm runs only while <c>deadline &gt; frameTime</c> (the 32-bit compare at
    /// <c>image@0x0E266..0x0E277</c>) and then computes
    /// <c>si = frameTime − deadline + 469</c> (<c>image@0x0E285..0x0E290</c>), so <c>si</c> climbs
    /// from 0 at the switch to 469 as the window closes.
    /// </remarks>
    public static int SweepPhase(uint frameTime, uint deadline) =>
        deadline > frameTime && deadline - frameTime <= SweepDuration
            ? (int)(SweepDuration - (deadline - frameTime))
            : -1;

    /// <summary>
    /// The CRT collapse's shape at one phase.
    /// </summary>
    /// <param name="phase"><c>si</c> from <see cref="SweepPhase(uint, uint)"/>.</param>
    /// <param name="rectWidth">The region rectangle's width — the sweep's own clip width.</param>
    /// <param name="rectHeight">Its height.</param>
    /// <returns>
    /// The half-height of the full-width band (phase 1), the half-width of the centre line (phase 2),
    /// and which phase it is: 0 = band, 1 = line, 2 = a single pixel.
    /// </returns>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><c>si ≤ 0x55</c>: a filled rectangle of the region's full width and height
    ///     <c>2·hh+1</c> about the face's centre row, <c>hh = (0x55−si)·(h/2)/0x55</c>
    ///     (<c>image@0x0E296..0x0E2C0</c>);</item>
    ///   <item><c>si ≤ 0xD5</c>: a horizontal line at that row, half-width
    ///     <c>(0xD5−si)·(w/2)/0x80</c> (<c>image@0x0E2CD..0x0E2F3</c>);</item>
    ///   <item>otherwise one pixel at the own-ship column and the centre row
    ///     (<c>image@0x0E2FA..0x0E303</c>).</item>
    /// </list>
    /// The two integer divides are <c>muldiv16_signed</c>, which truncates toward zero.
    /// </remarks>
    public static (int HalfHeight, int HalfWidth, int Phase) SweepShape(
        int phase, int rectWidth, int rectHeight)
    {
        if (phase <= SweepBandEnd)
        {
            int half = ((SweepBandEnd - phase) * (rectHeight >> 1)) / SweepBandEnd;
            return (half, 0, 0);
        }

        if (phase <= SweepLineEnd)
        {
            int half = ((SweepLineEnd - phase) * (rectWidth >> 1)) / SweepLineDivisor;
            return (0, half, 1);
        }

        return (0, 0, 2);
    }

    /// <summary>
    /// The face's centre ROW — <c>gfx_viewport_clip_rect_setup @image@0x11870</c>'s own centre for
    /// the region rectangle, which the sweep draws about (<c>g_viewport_center_y [0xE636]</c>).
    /// </summary>
    /// <param name="rectY">The region rectangle's top.</param>
    /// <param name="rectHeight">Its height.</param>
    /// <returns>The centre row.</returns>
    /// <remarks>
    /// MEASURED: the MiG-21's collapse band is symmetric about y = 175 = 159 + 33/2
    /// (a captured frame of the original).  The own-ship dot is NOT at
    /// this row — it is at the region's own pivot, near the bottom.
    /// </remarks>
    public static int FaceCentreRow(int rectY, int rectHeight) => rectY + (rectHeight / 2);
}
