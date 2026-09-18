namespace CYAC.Port.Core.Model.World;

/// <summary>
/// Which body axis one articulation block's angle turns about.
/// </summary>
/// <remarks>
/// <para>
/// The block holds three Euler words and the prepare callback writes the gear angle into exactly
/// one of them.  The JIT template at <c>image@0x1CD7A</c> hands them to
/// <c>gfx_rot_mat3_from_euler @image@0x14D9C</c> — <b>Pascal</b>, so the push order IS the source
/// order — as <c>(out, [si], [si+2], [si+4])</c> = <c>(out, angle_z, angle_y, angle_x)</c>.
/// </para>
/// <para>
/// And the matrices that name settles (the port's own verified
/// <c>BodyVelocityProjection.RotationFromEuler</c>, whose <c>NavSin</c> is mathematical cosine):
/// <c>R_x</c> is <c>[[cos, sin, 0], [−sin, cos, 0], [0,0,1]]</c> — it turns the X–Y plane, i.e.
/// rotates <b>about Z</b>; <c>R_y</c> turns Y–Z, i.e. <b>about X</b>; <c>R_z</c> turns Z–X, i.e.
/// <b>about Y</b>.  So <c>angle_x</c> = ROLL, <c>angle_y</c> = PITCH, <c>angle_z</c> = HEADING,
/// exactly as the flight kernel uses them.
/// </para>
/// </remarks>
public enum GearAxis
{
    /// <summary>Block slot 0, <c>angle_z</c> — a heading turn about the body's +Y (up). Unused by the gear.</summary>
    Heading = 0,

    /// <summary>
    /// Block slot 1, <c>angle_y</c> — a pitch turn about the body's +X (right wing).  Every NOSE
    /// gear uses this, and only the four jets have one.
    /// </summary>
    Pitch = 1,

    /// <summary>
    /// Block slot 2, <c>angle_x</c> — a roll turn about the body's +Z (forward).  Every MAIN gear
    /// uses this: all six aircraft retract their main gear inboard.
    /// </summary>
    Roll = 2,
}

/// <summary>
/// One articulated vertex group of an aircraft mesh — a landing-gear leg and its doors.
/// </summary>
/// <param name="LeafNodeImage">
/// The paint-tree leaf that owns the block, by its <c>image@</c> address — the identity the
/// document carries and the address the prepare callback writes the tag byte of.
/// </param>
/// <param name="Axis">Which Euler slot of the block the angle lands in.</param>
/// <param name="Negated">
/// True for the mirrored (left) leg, whose word gets <c>angle_wrap_0_to_0xB40(−angle)</c>
/// (<c>image@0x2D961</c>) — the same rotation the other way round.
/// </param>
/// <param name="PivotVertex">
/// <c>block[+6]</c> — the hinge, <b>read from the mesh document at load</b>
/// (<c>lods[2].paintTree[].articulation.pivotVertex</c>).  Verified 16/16 to equal
/// <c>edgeParents[FirstVertex]</c>: the group is an edge-tree subtree and the engine rotates its
/// stored DELTAS, which is the same thing as rotating the absolute vertices rigidly about this one.
/// </param>
/// <param name="FirstVertex"><c>block[+7]</c> — first vertex of the rotated run (from the document).</param>
/// <param name="LastVertex"><c>block[+8]</c> — last vertex of the rotated run, inclusive (from the document).</param>
/// <param name="HideAboveAngle">
/// The threshold override at <c>image@0x2DA84..0x2DAF9</c>: above this angle the leaf's tag byte is
/// forced to 1 and the leaf is skipped.  −1 when the leaf has no override.
/// </param>
public readonly record struct GearGroup(
    int LeafNodeImage,
    GearAxis Axis,
    bool Negated,
    int PivotVertex,
    int FirstVertex,
    int LastVertex,
    int HideAboveAngle);

/// <summary>
/// A paint-tree leaf the gear prepare callback shows and hides but does not rotate — the
/// taildraggers' tailwheels and the jets' main-gear doors.
/// </summary>
/// <param name="LeafNodeImage">The leaf node's <c>image@</c> address.</param>
public readonly record struct GearLeaf(int LeafNodeImage);

/// <summary>
/// One articulated group's RULE — everything about it the machine code states, and nothing the mesh
/// document already carries.
/// </summary>
/// <param name="LeafNodeImage">The paint-tree leaf that owns the block, by its <c>image@</c> address.</param>
/// <param name="Axis">Which Euler slot of the block the prepare callback writes the angle into.</param>
/// <param name="Negated">
/// True for the mirrored (left) leg, whose word gets <c>angle_wrap_0_to_0xB40(-angle)</c>
/// (<c>image@0x2D961</c>).
/// </param>
/// <param name="HideAboveAngle">
/// The threshold override at <c>image@0x2DA84..0x2DAF9</c>; -1 when the leaf has none.
/// </param>
public readonly record struct GearGroupRule(
    int LeafNodeImage, GearAxis Axis, bool Negated, int HideAboveAngle);

/// <summary>One aircraft class's gear RULES, before they are bound to a mesh document.</summary>
/// <param name="Basename">The mesh basename.</param>
/// <param name="LodIndex">Which LOD carries the blocks — 2 on all six.</param>
/// <param name="Groups">The rotated groups' rules.</param>
/// <param name="ExtraLeaves">The leaves that are only shown and hidden.</param>
public sealed record GearRuleSet(
    string Basename, int LodIndex, IReadOnlyList<GearGroupRule> Groups, IReadOnlyList<GearLeaf> ExtraLeaves);

/// <summary>
/// One aircraft class's landing-gear articulation, as the shipped bytes describe it.
/// </summary>
/// <param name="Basename">The mesh basename.</param>
/// <param name="LodIndex">Which LOD carries the blocks — 2 (the densest) on all six.</param>
/// <param name="Groups">The rotated groups.</param>
/// <param name="ExtraLeaves">The leaves that are only shown/hidden.</param>
public sealed record GearArticulation(
    string Basename, int LodIndex, IReadOnlyList<GearGroup> Groups, IReadOnlyList<GearLeaf> ExtraLeaves)
{
    /// <summary>Fully EXTENDED — <c>flight_gear_angle_reset @image@0x22A56</c> writes this at aircraft load.</summary>
    public const int ExtendedAngle = 0;

    /// <summary>
    /// Fully RETRACTED — <c>0x2D0</c> = 720 BAM = 90°, the clamp at <c>image@0x22A85</c>.
    /// </summary>
    /// <remarks>
    /// The SENSE is settled by two facts read together: the reset writes 0 at aircraft load, and the
    /// Test Flight's cold start is parked with the gear DOWN (H2 §D).  So 0 is extended and
    /// <c>0x2D0</c> is retracted — <c>g_gear_deploy_angle_bam</c> is really a RETRACTION angle.
    /// That also makes <c>flight_gear_angle_step</c>'s sign right: it negates the step when the gear
    /// bit <c>[0xF0BC] &amp; 4</c> ("gear down commanded") is set, walking the angle back to 0.
    /// </remarks>
    public const int RetractedAngle = 0x2D0;

    /// <summary>
    /// All 22 gear leaves are hidden outright once the angle reaches <see cref="RetractedAngle"/>
    /// (<c>image@0x2D956</c>: <c>cmp [0xEF96],0x2D0 / je set_flags_1</c>).
    /// </summary>
    /// <param name="angleBam">The current gear angle.</param>
    public static bool AllHidden(int angleBam) => angleBam >= RetractedAngle;

    /// <summary>
    /// The six flyable aircraft's gear RULES — addresses and behaviour only, never vertex data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where this comes from.</b>  <c>mesh_lod_prepare_gear_and_flame_state @image@0x2D8E9</c> —
    /// the <c>prepare_far_fnptr</c> of every aircraft LOD descriptor
    /// (<c>lcall [si+2]</c> @<c>image@0x1E1E2</c>) — writes 16 angle words and 22 tag bytes at fixed
    /// DGROUP addresses.  Resolved through the DGROUP base <c>image@0x3BD60</c> they all land inside
    /// the six flyable aircraft's LOD2 paint-tree regions: the 22 bytes ARE paint-tree leaf tags
    /// (<c>data/exe/meshes/&lt;name&gt;.json</c> <c>lods[2].paintTree[].image</c>) and the 16 words sit
    /// in the 9-byte ARTICULATION BLOCKS that follow the <c>tag 0x07</c> leaves.
    /// </para>
    /// <para>
    /// What a rule holds is what the CODE says — which leaf, which Euler slot, whether the angle is
    /// mirrored, and the threshold above which the leaf is skipped.  `cyac-transform` now publishes
    /// them as <c>lods[N].paintTree[].articulation</c>, so the pivot and the vertex run are READ FROM
    /// THE DOCUMENT by <see cref="Resolve"/> and no original byte is left in this source. Full
    /// derivation and per-row citations.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, GearRuleSet> Rules { get; } =
        new Dictionary<string, GearRuleSet>(StringComparer.OrdinalIgnoreCase)
        {
            // p51 — taildragger: two inboard main legs + a tailwheel that is only shown/hidden.
            ["p51"] = new("p51", 2,
                [
                    new(0x45399, GearAxis.Roll, false, 0x230),
                    new(0x45353, GearAxis.Roll, true, 0x230),
                ],
                [new(0x453D7)]),

            // fw190 — the same shape.
            ["fw190"] = new("fw190", 2,
                [
                    new(0x42F90, GearAxis.Roll, false, 0x230),
                    new(0x4301A, GearAxis.Roll, true, 0x230),
                ],
                [new(0x43042)]),

            // f86 — tricycle: two main legs plus a forward-folding nose leg.
            ["f86"] = new("f86", 2,
                [
                    new(0x425E4, GearAxis.Roll, false, -1),
                    new(0x42614, GearAxis.Roll, true, -1),
                    new(0x4265C, GearAxis.Pitch, false, 0x1E0),
                ],
                []),

            ["mig15"] = new("mig15", 2,
                [
                    new(0x44308, GearAxis.Roll, false, -1),
                    new(0x442B2, GearAxis.Roll, true, -1),
                    new(0x44325, GearAxis.Pitch, false, 0x1E0),
                ],
                [new(0x44302), new(0x442C1)]),

            ["f4"] = new("f4", 2,
                [
                    new(0x41E46, GearAxis.Roll, false, -1),
                    new(0x41D3F, GearAxis.Roll, true, -1),
                    new(0x41D78, GearAxis.Pitch, false, 0x1E0),
                ],
                [new(0x41E40), new(0x41D39)]),

            ["mig21"] = new("mig21", 2,
                [
                    new(0x449FE, GearAxis.Roll, false, -1),
                    new(0x44A3D, GearAxis.Roll, true, -1),
                    new(0x44A7C, GearAxis.Pitch, false, 0x1E0),
                ],
                []),
        };

    /// <summary>
    /// Binds a basename's rules to the vertex runs the LOD's own articulation blocks name.
    /// </summary>
    /// <param name="basename">A mesh basename.</param>
    /// <param name="lod">The LOD the rules name (<see cref="GearRuleSet.LodIndex"/>).</param>
    /// <returns>The resolved articulation, or null when the mesh has no gear rules.</returns>
    /// <exception cref="InvalidDataException">
    /// A rule names a leaf the document does not carry, or a leaf with no articulation block — which
    /// means the rule and the shipped mesh disagree and the port would rotate nothing, silently.
    /// </exception>
    public static GearArticulation? Resolve(string? basename, MeshLod lod)
    {
        ArgumentNullException.ThrowIfNull(lod);
        if (basename is null || !Rules.TryGetValue(basename, out GearRuleSet? rules))
        {
            return null;
        }

        IReadOnlyDictionary<int, MeshArticulationBlock> blocks = lod.PaintArticulation;
        List<GearGroup> groups = new List<GearGroup>(rules.Groups.Count);
        foreach (GearGroupRule rule in rules.Groups)
        {
            if (!blocks.TryGetValue(rule.LeafNodeImage, out MeshArticulationBlock block))
            {
                throw new InvalidDataException(
                    $"{basename} LOD{rules.LodIndex}: the gear rule names the leaf at "
                        + $"image@0x{rule.LeafNodeImage:X5}, but exe/meshes/{basename}.json carries no "
                        + "articulation block for it");
            }

            groups.Add(new GearGroup(
                rule.LeafNodeImage, rule.Axis, rule.Negated,
                block.PivotVertex, block.FirstVertex, block.LastVertex, rule.HideAboveAngle));
        }

        return new GearArticulation(rules.Basename, rules.LodIndex, groups, rules.ExtraLeaves);
    }

    /// <summary>The RULES for a mesh basename, or <see langword="null"/> when it has none.</summary>
    /// <param name="basename">A mesh basename.</param>
    public static GearRuleSet? For(string? basename) =>
        basename is not null && Rules.TryGetValue(basename, out GearRuleSet? hit) ? hit : null;

    /// <summary>Whether a group's leaf is drawn at a given gear angle.</summary>
    /// <param name="group">The group.</param>
    /// <param name="angleBam">The gear angle, 0 extended … <see cref="RetractedAngle"/> retracted.</param>
    public static bool LeafVisible(in GearGroup group, int angleBam) =>
        LeafVisible(group.HideAboveAngle, angleBam);

    /// <summary>Whether a rule's leaf is drawn at a given gear angle.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="angleBam">The gear angle, 0 extended … <see cref="RetractedAngle"/> retracted.</param>
    public static bool LeafVisible(in GearGroupRule rule, int angleBam) =>
        LeafVisible(rule.HideAboveAngle, angleBam);

    /// <summary>Whether a leaf with a given threshold override is drawn at a gear angle.</summary>
    /// <param name="hideAboveAngle">The threshold, or -1 for "no override".</param>
    /// <param name="angleBam">The gear angle.</param>
    public static bool LeafVisible(int hideAboveAngle, int angleBam) =>
        !AllHidden(angleBam) && (hideAboveAngle < 0 || angleBam <= hideAboveAngle);
}
