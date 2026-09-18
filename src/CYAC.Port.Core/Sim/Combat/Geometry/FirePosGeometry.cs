namespace CYAC.Port.Core.Sim.Combat.Geometry;

/// <summary>
/// The eight pure leaves that measure the world from the engagement's WEAPON FIRE POSITION
/// <c>[0xED42..0xED4D]</c>: three distance metrics, the lead-enable proximity gate, the
/// sentinel-word position codec and the octant classifier.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Every one of them is a straight transliteration of the original's bytes.
/// </para>
/// <para>
/// The recurring idiom is the original's arithmetic 32-bit <c>&gt;&gt;8</c>: <c>mov al,ah / mov
/// ah,dl / mov dl,dh / shl dh,1 / sbb dh,dh</c> (e.g. <c>image@0x07516</c>) — a byte shuffle, not a
/// shift instruction, but exactly <c>(int)v &gt;&gt; 8</c>.  It appears here as <c>value &gt;&gt;
/// 8</c> with that citation.
/// </para>
/// <para>
/// A second recurring idiom is the <b>0xFFFF cap</b>: after summing three scaled absolute deltas the
/// routine tests the HIGH word (<c>cmp bx,1 / jl</c>, <c>image@0x075CB</c>) and returns
/// <c>0xFFFF</c> whenever the total does not fit 16 bits.  That saturation is the reason a very
/// distant target reads as "just out of every window" rather than wrapping into a near one.
/// </para>
/// </remarks>
public static class FirePosGeometry
{
    /// <summary>
    /// <c>engagement_subject_pos_manhattan_dist (ex-weapon_fire_pos_manhattan_dist) @image@0x074B6</c> — the ground-plane distance
    /// <c>|fireX − x| + |fireZ − z|</c>, signed 32-bit.
    /// </summary>
    /// <remarks>
    /// Register/stack ABI in the original: <c>DX:AX</c> = the X value, the Z value on the stack
    /// (<c>ret 4</c>); the answer comes back in <c>DX:AX</c>.  The negates are the standard
    /// <c>neg ax / adc dx,0 / neg dx</c> 32-bit two's complement (<c>image@0x074CA</c>), which
    /// reproduces itself on <see cref="int.MinValue"/> exactly as C#'s unchecked negate does.
    /// </remarks>
    /// <param name="firePosition">The fire position (only X and Z participate).</param>
    /// <param name="x">The other point's X.</param>
    /// <param name="z">The other point's Z.</param>
    /// <returns>The summed absolute deltas, wrapping at 32 bits.</returns>
    public static int ManhattanDistance2d(CombatPosition firePosition, int x, int z)
    {
        int dx = unchecked(firePosition.X - x);                 // image@0x074C4 sub/sbb
        if (dx < 0)
        {
            dx = unchecked(-dx);                                // image@0x074CA
        }

        int dz = unchecked(firePosition.Z - z);                 // image@0x074DC
        if (dz < 0)
        {
            dz = unchecked(-dz);                                // image@0x074E4
        }

        return unchecked(dz + dx);                              // image@0x074EB add ax,cx / adc dx,bx
    }

    /// <summary>
    /// <c>engagement_subject_pos_3axis_dist (ex-weapon_fire_pos_3axis_dist) @image@0x074F4</c> — the 3-axis L1 distance from the fire
    /// position to a pool object, each axis scaled by <c>&gt;&gt;8</c>, saturating at
    /// <c>0xFFFF</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="applySentinelBias"/> is the original's <c>AL</c> (<c>or cl,cl</c>
    /// @<c>image@0x07566</c>): when set, the three sentinel-source words <c>[0xED83]</c>,
    /// <c>[0xED85]</c>, <c>[0xED87]</c> are sign-extended and added to the three SCALED deltas —
    /// an aim-point offset applied in the scaled domain, not the world one.
    /// </para>
    /// <para>
    /// Axis pairing is the original's, not the object's: the object's <c>+0x06</c> X pairs with <c>[0xED42]</c>, its
    /// <c>+0x0A</c> Y with <c>[0xED46]</c>, its <c>+0x0E</c> Z with <c>[0xED4A]</c> — and the bias words go to X, Y,
    /// Z in the SAME order (<c>image@0x0756A</c>/<c>0x07574</c>/<c>0x0757E</c>), i.e. <c>[0xED83]</c> (the "elevation
    /// source") biases X.  That looks like a mis-pairing and it is what the machine does.
    /// </para>
    /// </remarks>
    /// <param name="firePosition">The fire position.</param>
    /// <param name="target">The target object's position.</param>
    /// <param name="applySentinelBias">The original's <c>AL</c> flag.</param>
    /// <param name="biasX">The bias added to the scaled X delta — <c>[0xED83]</c>.</param>
    /// <param name="biasY">The bias added to the scaled Y delta — <c>[0xED85]</c>.</param>
    /// <param name="biasZ">The bias added to the scaled Z delta — <c>[0xED87]</c>.</param>
    /// <returns>The original's <c>AX</c>: the summed distance, or <c>0xFFFF</c> when it overflows.</returns>
    public static ushort Distance3dScaled(
        CombatPosition firePosition,
        CombatPosition target,
        bool applySentinelBias,
        short biasX,
        short biasY,
        short biasZ)
    {
        int dx = unchecked(target.X - firePosition.X) >> 8;     // image@0x0750E + the shuffle @0x07516
        int dy = unchecked(target.Y - firePosition.Y) >> 8;     // image@0x0752E
        int dz = unchecked(target.Z - firePosition.Z) >> 8;     // image@0x0754E

        if (applySentinelBias)                                  // image@0x07566 or cl,cl / je
        {
            dx = unchecked(dx + biasX);                         // image@0x0756A
            dy = unchecked(dy + biasY);                         // image@0x07574
            dz = unchecked(dz + biasZ);                         // image@0x0757E
        }

        // image@0x07588..0x075C9: abs Y, abs X, abs Z, then (X+Z)+Y in that association order.
        long total = unchecked((long)Abs32(dy) + Abs32(dx) + Abs32(dz));
        int sum = unchecked((int)total);
        return unchecked((short)(sum >> 16)) >= 1               // image@0x075CB cmp bx,1 / jl
            ? (ushort)0xFFFF                                    // image@0x075D0
            : unchecked((ushort)sum);                           // image@0x075DA mov ax,cx
    }

    /// <summary>
    /// <c>engagement_fire_pos_2d_dist_scaled @image@0x075E2</c> — the ground-plane distance from
    /// the fire position to the SENTINEL-CODED position, scaled by <c>&gt;&gt;8</c> and saturating.
    /// </summary>
    /// <remarks>
    /// Builds the sentinel position with <see cref="LoadSentinelPosition"/>, measures it with
    /// <see cref="ManhattanDistance2d"/>, scales and caps.
    /// </remarks>
    /// <param name="view">The register view.</param>
    /// <returns>The original's <c>AX</c>.</returns>
    public static ushort SentinelDistance2dScaled(EngagementAngleView view)
    {
        CombatPosition sentinel = LoadSentinelPosition(view);
        int raw = ManhattanDistance2d(view.FirePosition, sentinel.X, sentinel.Z);
        int scaled = raw >> 8;                                  // image@0x075FD, the byte shuffle
        return unchecked((short)(scaled >> 16)) >= 1            // image@0x0760D cmp dx,1 / jl
            ? (ushort)0xFFFF
            : unchecked((ushort)scaled);
    }

    /// <summary>
    /// <c>pos_snapshot_load @image@0x0761A</c> — decode the three sentinel words into a position
    /// triple.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each axis is stored as the HIGH word only, so the decoded value is the word shifted left 16
    /// (<c>image@0x07623</c> writes <c>0</c> to <c>[bx]</c>, <c>[bx+4]</c>, <c>[bx+8]</c> and the
    /// value to <c>[bx+2]</c>, <c>[bx+6]</c>, <c>[bx+0x0A]</c>).
    /// </para>
    /// <para>
    /// The three sources are NOT the three sentinel words in order: X takes the whole word
    /// <c>[0xED81]</c>, Y takes the single BYTE <c>[0xED83]</c> ZERO-extended (<c>xor ah,ah</c>
    /// @<c>image@0x07631</c>), and Z takes the UNALIGNED word <c>[0xED84]</c> — which straddles
    /// <c>[0xED83]</c>'s high byte and <c>[0xED85]</c>'s low byte.  That packing is how a 5-byte
    /// sentinel block carries a 3-axis position.
    /// </para>
    /// </remarks>
    /// <param name="view">The register view.</param>
    /// <returns>The decoded position.</returns>
    public static CombatPosition LoadSentinelPosition(EngagementAngleView view)
    {
        int x = unchecked(view.Registers.Word(0xED81) << 16);           // image@0x07625
        int y = unchecked(view.Registers.Byte(0xED83) << 16);           // image@0x0762E, zero-extended
        int z = unchecked(view.Registers.Word(0xED84) << 16);           // image@0x07639, UNALIGNED
        return new CombatPosition(x, y, z);
    }

    /// <summary>
    /// <c>pos_snapshot_store_and_arm_octant @image@0x07644</c> — the inverse of
    /// <see cref="LoadSentinelPosition"/>, followed by arming the octant window.
    /// </summary>
    /// <remarks>
    /// Writes the triple's high words back into <c>[0xED81]</c> / <c>[0xED83]</c> (byte) /
    /// <c>[0xED84]</c>, records <c>(octant + 3) &amp; 7</c> in <c>[0xED86]</c> — the OPPOSITE
    /// octant, since the window check accepts the recorded octant and the two after it — and clears
    /// <c>[0xED59]</c> bit2, the attitude-changed latch (<c>image@0x07660</c>).
    /// </remarks>
    /// <param name="view">The register view.</param>
    /// <param name="position">The position to encode.</param>
    public static void StoreSentinelPositionAndArmOctant(
        EngagementAngleView view, CombatPosition position)
    {
        view.Registers.SetWord(0xED81, unchecked((ushort)(position.X >> 16)));       // image@0x07644
        view.Registers.SetByte(0xED83, unchecked((byte)(position.Y >> 16)));         // image@0x0764A
        view.Registers.SetWord(0xED84, unchecked((ushort)(position.Z >> 16)));       // image@0x07650

        byte octant = ClassifyOctant(view);                                          // image@0x07656
        view.Registers.SetByte(0xED86, (byte)((octant + 3) & 7));                    // image@0x07659
        view.SlotFlags = (byte)(view.SlotFlags & 0xFB);                              // image@0x07660
    }

    /// <summary>
    /// <c>fire_pos_octant_classify @image@0x076E4</c> — which of eight sectors around the fire
    /// position the sentinel-coded point falls in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The classifier is a decision tree over three comparisons: the point's X against the fire
    /// position's X, its Z against the fire position's Z, and the two deltas against each other
    /// (the diagonal).  The X and Z comparisons are 32-bit signed-high / UNSIGNED-low
    /// (<c>jl</c>/<c>jg</c>/<c>jb</c> at <c>image@0x0771F</c>), and the diagonal comparisons are the
    /// same shape (<c>ja</c> at <c>image@0x0774B</c>).
    /// </para>
    /// <para>
    /// The eight results are the original's literals: <c>1</c>/<c>0</c> and <c>2</c>/<c>3</c> in the
    /// X≥ arm (<c>image@0x07750</c>, <c>0x07756</c>, <c>0x07775</c>, <c>0x0777C</c>), <c>6</c>/
    /// <c>7</c> and <c>5</c>/<c>4</c> in the X&lt; arm (<c>image@0x077AE</c>, <c>0x077B2</c>,
    /// <c>0x077CA</c>, <c>0x077CE</c>).
    /// </para>
    /// </remarks>
    /// <param name="view">The register view.</param>
    /// <returns>The octant, 0..7.</returns>
    public static byte ClassifyOctant(EngagementAngleView view)
    {
        CombatPosition p = LoadSentinelPosition(view);          // image@0x076ED
        CombatPosition fire = view.FirePosition;

        int dx = unchecked(p.X - fire.X);                       // image@0x076F6
        int dz = unchecked(p.Z - fire.Z);                       // image@0x0770A

        if (GreaterOrEqual(p.X, fire.X))                        // image@0x0771F cmp/jl/jg/jb
        {
            if (GreaterOrEqual(p.Z, fire.Z))                    // image@0x07732
            {
                // image@0x07744: |dz| vs |dx| through the same signed-high / unsigned-low compare.
                return Above(dz, dx) ? (byte)0 : (byte)1;
            }

            int negatedZ = unchecked(-dz);                      // image@0x0775C neg/adc/neg
            return Above(negatedZ, dx) ? (byte)3 : (byte)2;     // image@0x07769
        }

        if (GreaterOrEqual(p.Z, fire.Z))                        // image@0x07789
        {
            int negatedX = unchecked(-dx);                      // image@0x07798
            // image@0x077A2: jl → 7, jg → 6, equal-high jb → 7 — i.e. "≥ takes 6".
            return GreaterOrEqual(negatedX, dz) ? (byte)6 : (byte)7;
        }

        // image@0x077BE: jl → 4, jg → 5, equal-high jb → 4 — i.e. "≥ takes 5".
        return GreaterOrEqual(dz, dx) ? (byte)5 : (byte)4;
    }

    /// <summary>
    /// <c>octant_window_check @image@0x076A8</c> — is the sentinel point inside the three-octant
    /// window <c>[0xED86]</c> opened?
    /// </summary>
    /// <remarks>
    /// Accepts the recorded octant and the next two clockwise
    /// (<c>image@0x076B4</c>/<c>0x076BD</c>/<c>0x076CB</c>), i.e. a 135° window.
    /// </remarks>
    /// <param name="view">The register view.</param>
    /// <returns><c>true</c> when the point is inside the window.</returns>
    public static bool OctantWindowCheck(EngagementAngleView view)
    {
        byte octant = ClassifyOctant(view);
        byte armed = view.Registers.Byte(0xED86);
        return armed == octant
            || ((armed + 1) & 7) == octant
            || ((armed + 2) & 7) == octant;
    }

    /// <summary>
    /// <c>engagement_subject_pos_3axis_proximity_lead_set (ex-weapon_fire_pos_3axis_proximity_lead_set) @image@0x06EB8</c> — set
    /// <c>g_engagement_lead_enable [0xEDE0]</c> when the fire position is within the L1 ball of
    /// radius <c>0x001F_4000</c> of <paramref name="block"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="block"/> is whatever near pointer the caller stages in <c>BX</c>, and the
    /// three call sites stage three DIFFERENT blocks: the intercept snapshot <c>[0xEDB6]</c>
    /// (simple-heading, <c>image@0x0676A</c>), the caller's own ipos LOCALS (complex-heading,
    /// <c>image@0x0682E</c>), and <c>[0xED42]</c> itself (the elevation site,
    /// <c>image@0x068D9</c> — a SELF-compare, so its distance is always 0 and it ALWAYS sets the
    /// flag).  The port takes the block by value, which makes all three the same call.
    /// </para>
    /// <para>
    /// The routine only ever SETS the flag; nothing here clears it.
    /// </para>
    /// </remarks>
    /// <param name="view">The register view.</param>
    /// <param name="block">The position to compare the fire position against.</param>
    /// <returns><c>true</c> when the flag was set.</returns>
    public static bool ProximityLeadSet(EngagementAngleView view, CombatPosition block)
    {
        CombatPosition fire = view.FirePosition;
        long total = unchecked(
            (long)Abs32(unchecked(fire.X - block.X))            // image@0x06EC6
            + Abs32(unchecked(fire.Z - block.Z))                // image@0x06EDF
            + Abs32(unchecked(fire.Y - block.Y)));              // image@0x06EFB
        int sum = unchecked((int)total);
        short high = unchecked((short)(sum >> 16));

        // image@0x06F14: cmp si,0x1f / jg no / jl yes / cmp cx,0x4000 / jae no.
        bool hit = high < 0x1F || (high == 0x1F && unchecked((ushort)sum) < 0x4000);
        if (hit)
        {
            view.LeadEnable = 1;                                // image@0x06F21
        }

        return hit;
    }

    /// <summary>
    /// <c>engagement_z_hi_clip @image@0x087EE</c> — the saturating read of a position pair's
    /// SECOND word, offset by one byte.
    /// </summary>
    /// <remarks>
    /// Deliberately unaligned: the routine tests <c>[bx+2]</c> (the pair's high word) against
    /// <c>0xFF</c> signed and, when it is smaller, returns <c>[bx+1]</c> — the word straddling the
    /// pair's bytes 1..2, i.e. <c>(value &gt;&gt; 8) &amp; 0xFFFF</c>.  Its only caller passes <c>BX =
    /// 0xED46</c> (the fire position's Y), so the value is the ALTITUDE scaled by <c>1/256</c>, capped
    /// at <c>0xFFFF</c>.
    /// </remarks>
    /// <param name="view">The register view.</param>
    /// <param name="pairOffset">The DGROUP offset of the 32-bit value (the caller's <c>BX</c>).</param>
    /// <returns>The original's <c>AX</c>.</returns>
    public static ushort HighWordClip(EngagementAngleView view, int pairOffset) =>
        view.Signed(pairOffset + 2) >= 0xFF                     // image@0x087EE cmp / jl
            ? (ushort)0xFFFF                                    // image@0x087F5
            : view.Registers.Word(pairOffset + 1);              // image@0x087FA mov ax,[bx+1]

    private static int Abs32(int v) => v < 0 ? unchecked(-v) : v;

    /// <summary>The original's 32-bit "greater or equal" — signed high word, UNSIGNED low word.</summary>
    private static bool GreaterOrEqual(int a, int b)
    {
        short ah = unchecked((short)(a >> 16));
        short bh = unchecked((short)(b >> 16));
        if (ah != bh)
        {
            return ah > bh;
        }

        return unchecked((ushort)a) >= unchecked((ushort)b);
    }

    /// <summary>The original's strict 32-bit "greater than" with the same mixed comparison.</summary>
    private static bool Above(int a, int b)
    {
        short ah = unchecked((short)(a >> 16));
        short bh = unchecked((short)(b >> 16));
        if (ah != bh)
        {
            return ah > bh;
        }

        return unchecked((ushort)a) > unchecked((ushort)b);
    }
}
