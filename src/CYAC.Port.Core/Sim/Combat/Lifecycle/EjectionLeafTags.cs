using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Sim.Combat.Lifecycle;

/// <summary>
/// The ejection meshes' per-object PART VISIBILITY — the port's read of the two per-class PREPARE
/// callbacks the <c>eject1</c> and <c>eject4</c> LOD descriptors register at <c>+0x02</c>
/// (<c>image@0x410E6</c> → <c>3C2B:0744</c> = <c>image@0x2C9F4</c>; <c>image@0x413CA</c> →
/// <c>3C2B:0818</c> = <c>image@0x2CAC8</c>), which <c>mesh_face_render_dispatch</c> calls once per
/// drawn object (<c>lcall [si+2]</c> @<c>image@0x1E1E2</c>) before the paint tree is walked.
/// </summary>
/// <remarks>
/// <para>
/// Both callbacks do to the ejection meshes what <c>mesh_lod_prepare_gear_and_flame_state
/// @image@0x2D8E9</c> does to the aircraft: they write <b>1</b> (hidden) or <b>3</b> (drawn) into BSP
/// paint-tree LEAF TAG bytes that live inside the mesh's own DGROUP data, and <c>mesh_poly_tree_walk
/// @image@0x1A8A8</c> then skips every leaf whose tag has bit 1 clear (<c>test byte [si],2 / je</c>
/// @<c>image@0x1A8D6</c>).  Rounds 14c/P587/P588 read the twenty tag bytes as "HUD ownership flags"
/// (<c>slot_search_set_hud_flags_A/B</c>); the addresses are inside the <c>eject1</c>
/// (<c>[0x5334]</c>…<c>[0x5570]</c>) and <c>eject4</c> (<c>[0x5618]</c>…<c>[0x5997]</c>) class data
/// and each is a leaf node of the LOD's paint tree (<c>data/exe/meshes/eject1.json</c>,
/// <c>eject4.json</c>), so they are part visibility, not HUD state ("the player sits in
/// a chair then jettisons the chair, but in our case it jettisons the entire player" and "four hands
/// on the man under the parachute").
/// </para>
/// <para>
/// <b>eject1 (<c>image@0x2C9F4..0x2CAC5</c>)</b> — the SAME mesh is worn by the PILOT
/// (<c>slot[+0x04]</c>) and by the SEAT (<c>slot[+0x06]</c>, H7 §0.2).  All ten leaves default to
/// drawn (<c>mov al,3</c> + ten stores @<c>image@0x2C9F4..0x2CA11</c>); the slot table is then
/// searched from <c>[0xBBD6]</c> down for the object being drawn (<c>[0xEA0C]</c>):
/// </para>
/// <list type="bullet">
/// <item>the SEAT (<c>slot[+0x06]</c> match, <c>image@0x2CA41</c>): the seven PILOT leaves are
/// hidden — torso ×2, the two arm groups, the six-gon, the head — and the harness leaf too;</item>
/// <item>the PILOT (<c>slot[+0x04]</c> match, <c>image@0x2CA5A</c>): the seat leaves and the
/// plume are hidden when the slot's <c>+0x02</c> KIND byte is <b>ZERO</b> — <c>cmp byte [bx+2],0 /
/// <b>je</b> 0x2CA7E</c> — i.e. the aircraft has NO ejection seat (a prop: the pilot BAILS OUT and
/// falls alone), or when its state is no longer 0 (<c>image@0x2CA60 jne</c>: the separate seat object
/// has lit and taken the chair).  A JET pilot (kind set) in state 0 therefore RIDES THE CHAIR, and
/// only the rocket plume goes, when <c>[0xC332]</c> bit 0 is set or the slot's stamp has been
/// reached (<c>image@0x2CA66..0x2CA7C</c>).  Reading the <c>je</c> as a <c>jne</c> inverts it; the
/// bytes' rule is that the P-51 pilot leaves with no seat and no rocket, and the MiG-15 pilot rides
/// the seat and throws it away.  A "refined" seat style is this rule re-invented for jets and wrong
/// for props, so there is none.
/// Then the HEAD disc is hidden when the object is the player's own ejected pilot
/// (<c>[0xC38A]</c>) seen from one of the six own-aircraft views (<c>[0xE470]</c>,
/// <c>image@0x2CA8C..0x2CAA5</c>);</item>
/// <item>no slot owns it (<c>image@0x2CAA7</c>): the three seat leaves and the harness are hidden,
/// and the head under the same own-view rule.</item>
/// </list>
/// <para>
/// <b>eject4 (<c>image@0x2CAC8..0x2CB7D</c>)</b> — the man under the parachute has TWO authored
/// limb sets (arms + head disc each) and the callback shows exactly one: the slot's <c>+0x03</c>
/// stage byte picks it (<c>image@0x2CB41</c>: non-zero hides set B, zero hides set A).  A slot in
/// state 1 also hides the canopy and both rope groups (<c>image@0x2CB11..0x2CB1F</c>; dormant on
/// shipped data — the pilot wears <c>eject1</c> in state 1, H7 §0.3).  The head discs follow the
/// same own-view rule as eject1's (<c>image@0x2CB22..0x2CB3E</c>) before the set choice overrides
/// one of them.
/// </para>
/// <para>
/// Not ported, <b>(open)</b>: both callbacks look the object up by <c>[0xBE]</c> instead of
/// <c>[0xEA0C]</c> when <c>g_per_mesh_state_record_ptr [0xE820] == 0x1018</c>
/// (<c>image@0x2CA18</c>, <c>image@0x2CACD</c>); no port path sets that record pointer.
/// </para>
/// <para>
/// The eject2 and eject3 descriptors register the two bare <c>retf</c>s at <c>image@0x2CAC6</c>
/// and <c>image@0x2CAC7</c> — no-op callbacks — so those meshes draw whole.
/// </para>
/// </remarks>
public static class EjectionLeafTags
{
    /// <summary><c>g_class_record_eject1 [0x5334]</c>.</summary>
    public const ushort Eject1ClassRecord = 0x5334;

    /// <summary><c>g_class_record_eject4 [0x5618]</c>.</summary>
    public const ushort Eject4ClassRecord = 0x5618;

    /// <summary>
    /// <c>g_cockpit_aircraft_mode_flags [0xC332]</c> — bit 0 is the idle-slot harness gate
    /// (<c>test byte [0xC332],1</c> @<c>image@0x2CA66</c>).
    /// </summary>
    public const int CockpitAircraftModeFlags = 0xC332;

    // ── eject1's ten leaf-tag bytes (DGROUP), named by what the leaf paints ─────────────────────

    /// <summary>Seat group A — records at <c>image@0x4114A/0x4113F/0x41113/0x41108/0x410FD</c> (leaf <c>image@0x412AF</c>).</summary>
    public const int Eject1SeatA = 0x554F;

    /// <summary>Seat group B — the five closed tag-<c>0x10</c> faces on vertices 32..39 (leaf <c>image@0x412A3</c>).</summary>
    public const int Eject1SeatB = 0x5543;

    /// <summary>Seat group C — records at <c>image@0x41155/0x41134/0x41129/0x4111E/0x410F2</c> (leaf <c>image@0x41253</c>).</summary>
    public const int Eject1SeatC = 0x54F3;

    /// <summary>
    /// The two palette-15/14 (white/yellow) triangles hanging BELOW the seat on vertices 40/53..56
    /// (leaf <c>image@0x4125F</c>): the ejection seat's ROCKET PLUME.  Drawn only while the slot is
    /// armed and the seat is still under the pilot (until the stamp, <c>image@0x2CA6D</c>), gone
    /// the moment the seat separates.  Identified from the frames: a white-and-yellow flame under
    /// the chair during the armed phase. The constant keeps its name; the leaf is the plume.
    /// </summary>
    public const int Eject1Harness = 0x54FF;

    /// <summary>Pilot torso A — records at <c>image@0x411A1/0x41160/0x41176</c> (leaf <c>image@0x41241</c>).</summary>
    public const int Eject1TorsoA = 0x54E1;

    /// <summary>Pilot torso B — records at <c>image@0x41196/0x4118B/0x41180/0x4116B</c> (leaf <c>image@0x41249</c>).</summary>
    public const int Eject1TorsoB = 0x54E9;

    /// <summary>Pilot limb group A — records at <c>image@0x41210/0x4121B</c> (leaf <c>image@0x41289</c>).</summary>
    public const int Eject1LimbA = 0x5529;

    /// <summary>Pilot limb group B — records at <c>image@0x411FB/0x41206</c> (leaf <c>image@0x41296</c>).</summary>
    public const int Eject1LimbB = 0x5536;

    /// <summary>The pilot's six-gon at <c>image@0x411AC</c> (leaf <c>image@0x4127E</c>).</summary>
    public const int Eject1Hexagon = 0x551E;

    /// <summary>The pilot's HEAD disc at <c>image@0x41239</c> (leaf <c>image@0x4127A</c>) — ex "g_hud_ownership_indicator".</summary>
    public const int Eject1Head = 0x551A;

    // ── eject4's ten leaf-tag bytes (DGROUP) ─────────────────────────────────────────────────

    /// <summary>The parachute canopy — 32 faces (leaf <c>image@0x41638</c>).</summary>
    public const int Eject4Canopy = 0x58D8;

    /// <summary>The three shroud lines from vertex 11 (leaf <c>image@0x4167A</c>).</summary>
    public const int Eject4RopesA = 0x591A;

    /// <summary>The three shroud lines from vertex 12 (leaf <c>image@0x4169C</c>).</summary>
    public const int Eject4RopesB = 0x593C;

    /// <summary>The man's torso — six faces (leaf <c>image@0x416CD</c>).</summary>
    public const int Eject4Torso = 0x596D;

    /// <summary>Limb set A, left arm (leaf <c>image@0x41682</c>).</summary>
    public const int Eject4SetALeftArm = 0x5922;

    /// <summary>Limb set A, right arm (leaf <c>image@0x416A4</c>).</summary>
    public const int Eject4SetARightArm = 0x5944;

    /// <summary>Limb set A, head disc at vertex 15 (leaf <c>image@0x416BE</c>) — ex "g_hud_flag_B_595E".</summary>
    public const int Eject4SetAHead = 0x595E;

    /// <summary>Limb set B, left arm (leaf <c>image@0x41688</c>).</summary>
    public const int Eject4SetBLeftArm = 0x5928;

    /// <summary>Limb set B, right arm (leaf <c>image@0x416AA</c>).</summary>
    public const int Eject4SetBRightArm = 0x594A;

    /// <summary>Limb set B, head disc at vertex 48 (leaf <c>image@0x416C2</c>) — ex "g_hud_B_ownership_or_lock".</summary>
    public const int Eject4SetBHead = 0x5962;

    /// <summary>
    /// Whether the current view is one of the six OWN-AIRCRAFT views (F1..F6, view modes 0..5) —
    /// the meaning of <c>[0xE470]</c>, bit 1 of the per-view flag byte
    /// (<c>image@0x23878</c>; the table's bit is set on views 0..5 only, H16 §2.4).
    /// </summary>
    /// <param name="viewMode">The original's view mode number.</param>
    public static bool IsOwnAircraftView(int viewMode) => viewMode >= 0 && viewMode <= 5;

    /// <summary>
    /// The leaf-tag bytes the callback would write <b>1</b> (hidden) into for one drawn object, as
    /// DGROUP addresses; empty for any class but eject1/eject4.
    /// </summary>
    /// <param name="registers">The combat register file (the slot table and the focus object).</param>
    /// <param name="objectRef">The pool object being drawn — <c>g_render_current_object_id [0xEA0C]</c>.</param>
    /// <param name="classRecordRef">Its class record's DGROUP address.</param>
    /// <param name="ownAircraftView">
    /// <see cref="IsOwnAircraftView"/> for the view the frame is drawn from (<c>[0xE470]</c>).
    /// </param>
    public static int[] HiddenLeaves(
        CombatRegisters registers, ushort objectRef, ushort classRecordRef, bool ownAircraftView)
    {
        ArgumentNullException.ThrowIfNull(registers);
        return classRecordRef switch
        {
            Eject1ClassRecord => Eject1Hidden(registers, objectRef, ownAircraftView),
            Eject4ClassRecord => Eject4Hidden(registers, objectRef, ownAircraftView),
            _ => [],
        };
    }

    /// <summary>
    /// <see cref="HiddenLeaves"/> re-keyed to the paint-tree leaf nodes' <c>image@</c> addresses —
    /// the keys <c>MeshLod.PaintLeaves</c> uses.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="objectRef">The pool object being drawn.</param>
    /// <param name="classRecordRef">Its class record's DGROUP address.</param>
    /// <param name="ownAircraftView">Whether the view is one of the six own-aircraft views.</param>
    public static int[] HiddenLeafNodes(
        CombatRegisters registers, ushort objectRef, ushort classRecordRef, bool ownAircraftView)
    {
        int[] hidden = HiddenLeaves(registers, objectRef, classRecordRef, ownAircraftView);
        for (int i = 0; i < hidden.Length; i++)
        {
            hidden[i] += DgroupConstants.DgroupImageBase;
        }

        return hidden;
    }

    /// <summary>
    /// Whether the drawn object is the PLAYER'S OWN ejected pilot seen from an own-aircraft view —
    /// the shared tail of both callbacks (<c>cmp cx,[0xC38A] / jne</c>, <c>cmp byte [0xE470],0 / je</c>,
    /// <c>image@0x2CA8C..0x2CA97</c> and <c>image@0x2CB22..0x2CB2D</c>).
    /// </summary>
    private static bool HeadHidden(CombatRegisters registers, ushort objectRef, bool ownAircraftView) =>
        ownAircraftView
        && objectRef == registers.Word(LifecycleOffsets.DestructionFocusObject);

    /// <summary>The slot whose <c>+0x04</c> or <c>+0x06</c> is the object, searched top-down (<c>image@0x2CA24..0x2CA3F</c>).</summary>
    private static (int Slot, bool IsSeat) FindSlot(CombatRegisters registers, ushort objectRef, bool matchSeat)
    {
        for (int slot = ObjectSlotPool.LastSlot; slot >= ObjectSlotPool.FirstSlot; slot -= ObjectSlotPool.SlotBytes)
        {
            if (registers.Byte(slot + ObjectSlotPool.ActiveFlag) == 0)                  // image@0x2CA2D
            {
                continue;
            }

            if (registers.Word(slot + ObjectSlotPool.ExtPointerA) == objectRef)         // image@0x2CA32
            {
                return (slot, false);
            }

            if (matchSeat && registers.Word(slot + ObjectSlotPool.ExtPointerB) == objectRef)  // image@0x2CA37
            {
                return (slot, true);
            }
        }

        return (0, false);
    }

    private static int[] Eject1Hidden(CombatRegisters registers, ushort objectRef, bool ownAircraftView)
    {
        List<int> hidden = new List<int>(8);
        (int slot, bool isSeat) = FindSlot(registers, objectRef, matchSeat: true);
        if (slot != 0 && isSeat)
        {
            // image@0x2CA41..0x2CA58 — the SEAT object: every pilot part, and the plume, go.
            hidden.AddRange([Eject1Harness, Eject1LimbA, Eject1LimbB, Eject1Hexagon, Eject1Head, Eject1TorsoB, Eject1TorsoA]);
            return [.. hidden];
        }

        if (slot != 0)
        {
            // image@0x2CA5A `cmp byte [bx+2],0 / je 0x2CA7E`: a KIND of ZERO — the aircraft has no
            // ejection seat (props, [0xEF2E] bit 6 clear) — sends the pilot out WITHOUT a chair;
            // image@0x2CA60 `cmp byte [bx+0x42],0 / jne 0x2CA7E`: a jet's chair leaves the pilot the
            // frame the separate seat object lights (state 0 → 1, image@0x2C69E..0x2C6A4).  The
            // test reads the other way round than it looks — see the class remarks.
            bool separated = registers.Byte(slot + ObjectSlotPool.KindFlag) == 0          // image@0x2CA5A..0x2CA5E (je)
                || registers.Byte(slot + ObjectSlotTick.StateByte) != 0;                 // image@0x2CA60..0x2CA64 (jne)
            if (separated)
            {
                // image@0x2CA7E..0x2CA89 — the pilot is out of the seat: seat parts + plume go.
                hidden.AddRange([Eject1Harness, Eject1SeatA, Eject1SeatB, Eject1SeatC]);
            }
            else
            {
                // image@0x2CA66..0x2CA7C — a jet pilot still in his armed seat: only the rocket
                // plume, and only when [0xC332] bit 0 is set or the slot's stamp has been reached.
                bool modeBit = registers.Covers(CockpitAircraftModeFlags, 1)
                    && (registers.Byte(CockpitAircraftModeFlags) & 1) != 0;
                ushort stampPlusOne = unchecked((ushort)(registers.Word(slot + ObjectSlotPool.AllocationFrame) + 1));
                if (modeBit || stampPlusOne <= registers.MasterFrameCounter)             // image@0x2CA6D..0x2CA75 (ja)
                {
                    hidden.Add(Eject1Harness);
                }
            }
        }
        else
        {
            // image@0x2CAA7..0x2CAB2 — no slot owns the object.
            hidden.AddRange([Eject1Harness, Eject1SeatA, Eject1SeatB, Eject1SeatC]);
        }

        if (HeadHidden(registers, objectRef, ownAircraftView))                           // image@0x2CA8C / 0x2CAB5
        {
            hidden.Add(Eject1Head);
        }

        return [.. hidden];
    }

    private static int[] Eject4Hidden(CombatRegisters registers, ushort objectRef, bool ownAircraftView)
    {
        List<int> hidden = new List<int>(6);
        (int slot, _) = FindSlot(registers, objectRef, matchSeat: false);              // image@0x2CB07: +0x04 only
        bool headHidden = HeadHidden(registers, objectRef, ownAircraftView);
        if (slot == 0)
        {
            // image@0x2CB61..0x2CB79 — no slot: limb set B goes; set A's head under the own-view rule.
            hidden.AddRange([Eject4SetBHead, Eject4SetBRightArm, Eject4SetBLeftArm]);
            if (headHidden)
            {
                hidden.Add(Eject4SetAHead);
            }

            return [.. hidden];
        }

        if (registers.Byte(slot + ObjectSlotTick.StateByte) == 1)                        // image@0x2CB11
        {
            hidden.AddRange([Eject4RopesB, Eject4RopesA, Eject4Canopy]);                 // image@0x2CB19..0x2CB1F
        }

        // image@0x2CB22..0x2CB3E — both heads follow the own-view rule…
        if (headHidden)
        {
            hidden.Add(Eject4SetBHead);
            hidden.Add(Eject4SetAHead);
        }

        // …then the stage byte picks the limb set (image@0x2CB41): non-zero hides set B, zero set A.
        if (registers.Byte(slot + ObjectSlotPool.StageFlag) != 0)
        {
            hidden.AddRange([Eject4SetBHead, Eject4SetBRightArm, Eject4SetBLeftArm]);    // image@0x2CB47..0x2CB4F
        }
        else
        {
            hidden.AddRange([Eject4SetAHead, Eject4SetARightArm, Eject4SetALeftArm]);    // image@0x2CB54..0x2CB5C
        }

        return [.. hidden.Distinct()];
    }
}
