using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Flight;

namespace CYAC.Port.Core.Sim.Combat.Player;

/// <summary>
/// <c>spawn_record_position_fill @image@0x2367F</c> — the MUZZLE position: three body-frame offsets
/// rotated by the launcher's attitude and added to its world position.
/// </summary>
/// <remarks>
/// <para>
/// An earlier pass left this a seam (<c>IPlayerCombatEvents.RotateBodyOffset</c>) because its two callees are the
/// renderer's Euler pair.  An earlier pass closed it, and found it costs nothing: <b>the flight kernel already ports both
/// of them</b> — <c>gfx_rot_mat3_from_euler @image@0x14D9C</c> and
/// <c>gfx_vec3_mat3_multiply_inner @image@0x1BF0E</c> are
/// <see cref="BodyVelocityProjection.RotationFromEuler"/> and
/// <see cref="BodyVelocityProjection.Transform"/>, written for the flight kernel's
/// <c>aircraft_project_to_camera</c>.  The wrapper the combat side calls,
/// <c>image@0x1BEFE</c>, is nine bytes of argument shuffling in front of the same inner routine
/// (<c>mov ax,[bp+8] / mov bx,[bp+6] / call 0x1bf0e</c>).
/// </para>
/// <para>
/// <b>Why it mattered.</b>  With the seam unfilled the muzzle position is <c>(0,0,0)</c>, so EVERY
/// shot — the player's at CS4 and the AI's at CS6 — is born at the world origin.  The assembled
/// driver's arena comparison is what made that visible: 2,162 + 901 wrong arena bytes, all of them
/// the shot object's position triple.  Per-stage verification could not see it because C6's
/// CS3 → CS4 run ATTRIBUTED the firing frames.
/// </para>
/// <para>
/// <b>The routine, from the bytes</b> (<c>image@0x2367F..0x2373E</c>):
/// </para>
/// <list type="number">
///   <item><description>
///     stage the three offsets as three <c>i32</c> whose LOW words are zero
///     (<c>image@0x2368D..0x236A7</c>) — i.e. each offset is a Q16 value;
///   </description></item>
///   <item><description>
///     if all three offsets are zero, SKIP the rotation entirely
///     (<c>or cx,bx / or ax,cx / je</c> <c>image@0x236AD</c>) — the muzzle is then the launcher's
///     own position;
///   </description></item>
///   <item><description>
///     build the launcher's rotation matrix from its pool object's <c>+0x16</c> roll,
///     <c>+0x14</c> pitch and <c>+0x12</c> heading (<c>image@0x236CB..0x236D7</c>, pushed in that
///     order so roll is the callee's first argument);
///   </description></item>
///   <item><description>
///     run the vector through it, then arithmetic-shift each row RIGHT BY 8 — the original does it
///     with the byte shuffle <c>al=ah / ah=dl / dl=dh / shl dh,1 / sbb dh,dh</c>
///     (<c>image@0x236EB</c>), which is a sign-propagating <c>&gt;&gt; 8</c> of the whole
///     <c>i32</c> — and add the launcher's <c>pos_x</c> / <c>pos_y</c> / <c>pos_z</c>
///     (<c>+0x06</c> / <c>+0x0A</c> / <c>+0x0E</c>).
///   </description></item>
/// </list>
/// </remarks>
public static class SpawnRecordPositionFill
{
    /// <summary>Runs the whole routine.</summary>
    /// <param name="arena">The pool arena the launcher's object lives in.</param>
    /// <param name="launcherObject">The launcher's pool near offset — the original's <c>[bp+0x0E]</c>.</param>
    /// <param name="offsetZ">The <c>[bp+0x0C]</c> offset (the table's first entry, scaled by 4).</param>
    /// <param name="offsetX">The <c>[bp+0x0A]</c> offset (its second).</param>
    /// <param name="offsetY">The <c>[bp+0x08]</c> offset (its third).</param>
    /// <returns>The muzzle position.</returns>
    public static CombatPosition Run(
        PoolArena arena, ushort launcherObject, short offsetZ, short offsetX, short offsetY)
    {
        ArgumentNullException.ThrowIfNull(arena);

        CombatObjectView launcher = new CombatObjectView(arena, launcherObject);
        CombatPosition position = launcher.Position;

        // image@0x236AD — all three zero ⇒ the staged vector is zero and the rotation is skipped.
        if ((ushort)((ushort)offsetZ | (ushort)offsetX | (ushort)offsetY) == 0)
        {
            return position;
        }

        // image@0x236CB..0x236D7 — the callee's arguments are pushed heading, pitch, ROLL, so roll
        // is its first.  The pool object's angle triple is +0x12 heading / +0x14 pitch / +0x16 roll.
        Mat3Q14 matrix = BodyVelocityProjection.RotationFromEuler(
            new Angle(unchecked((ushort)arena.Word(unchecked((ushort)(launcherObject + 0x16))))),
            new Angle(unchecked((ushort)launcher.Elevation)),
            new Angle(unchecked((ushort)launcher.Heading)));

        // image@0x236E1 → image@0x1BEFE → image@0x1BF0E.  The inner routine reads the vector's
        // three HIGH words at [di+2] / [di+6] / [di+0xA], which are exactly the three offsets.
        (int row0, int row1, int row2) = BodyVelocityProjection.Transform(matrix, offsetZ, offsetX, offsetY);

        // image@0x236EB..0x2373E — >>8 with sign, then + the launcher's own position.
        return new CombatPosition(
            unchecked(position.X + (row0 >> 8)),
            unchecked(position.Y + (row1 >> 8)),
            unchecked(position.Z + (row2 >> 8)));
    }
}

/// <summary>
/// The player side's out-calls with the ONE of them the port can compute filled in — the shipping
/// wiring, and the configuration the assembled driver verifies.
/// </summary>
/// <param name="arena">The pool arena the launcher's object lives in.</param>
/// <remarks>
/// The other value-returning out-calls stay at their neutral answers and are counted:
/// <c>ProjectPositionToScreen</c> and <c>RangeFromViewAnchor</c> are the renderer's,
/// <c>BuildQualifyingObjectList</c> / <c>ObjectStillQualifies</c> are the lock-on's display-list
/// walk, <c>ScorerLineOfSightQuery</c> is the terrain query and <c>OrientationCombine</c> is
/// <c>angle_3d_orientation_combine @image@0x1C122</c> — the last named geometry seam.
/// </remarks>
public class PortedPlayerCombatEvents(PoolArena arena) : CountingPlayerCombatEvents
{
    /// <summary>
    /// C3a's geometry context, when the driver has one — then
    /// <see cref="AccumulateShotTrajectory"/> runs the REAL snapshot instead of counting.
    /// </summary>
    /// <remarks>
    /// A door census of <c>shot_trajectory_proximity_accum @image@0x08510</c> finds EIGHT near
    /// callers and the port wired three.  <c>image@0x08483</c> — inside
    /// <c>weapon_guidance_angle_track</c>, with <c>AL = 1</c> — was one of the five it left as an
    /// out-call, and it is the ONLY caller on the recordings that passes 1, so the six-key cache's
    /// <c>[0xB547]</c> hit flag never reached 1 in the port.  The driver's CS6 comparison found it
    /// on 64 frames of 173046.
    /// </remarks>
    public Geometry.EngagementGeometryContext? Geometry { get; set; }

    /// <inheritdoc/>
    public override void AccumulateShotTrajectory(byte mode)
    {
        base.AccumulateShotTrajectory(mode);
        if (Geometry is { } geometry)
        {
            geometry.Snapshot.Refresh(geometry, mode != 0);          // image@0x08483, AL = 1
        }
    }

    /// <summary>
    /// The RENDER LIST, when the host has one.
    /// </summary>
    /// <remarks>
    /// When set, the five lock-on render-list members below run
    /// <see cref="LockOnRenderList"/> — the real
    /// <c>qualifying_object_list_build</c> / <c>engagement_object_frustum_qualify</c> / chain walk /
    /// <c>object_screen_pos_project</c> — instead of the neutral answers.  A verification host
    /// builds one over the trace's <c>render_slot_block</c> window (trace format v1.6 ask G1); a
    /// future renderer builds one over its own display list.  Left null, the members keep the
    /// counting behaviour C7 shipped so nothing that never had a list changes.
    /// </remarks>
    public LockOnRenderList? RenderList { get; set; }

    /// <summary>
    /// The register file the VIEW ANCHOR lives in, when the host has one.
    /// </summary>
    /// <remarks>
    /// When set, <see cref="RangeFromViewAnchor"/> runs
    /// <c>object_range_from_view_anchor @image@0x24448</c> for real over
    /// <c>view_anchor_and_zoom [0xD88E]+22</c> (trace format v1.6 ask V1) instead of answering
    /// <see cref="ViewAnchorRangeUnavailable"/>.
    /// </remarks>
    public CombatRegisters? Registers { get; set; }

    /// <summary>
    /// the anchor is no longer unknown — see <see cref="Registers"/>; this constant stays as the
    /// answer for a host that has no register file, and as the value an earlier pass measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>range_from_view_anchor @image@0x24448</c> is a Manhattan distance from the CAMERA, whose
    /// position lives at DGROUP <c>[0xD88E..0xD899]</c> — six words that no combat-trace window
    /// carries and that no combat code writes, so a port seeded from a stage record simply does not
    /// have them.  Its one combat reader is <c>weapon_fire_spawn_record_fill</c>'s PATH-A gate
    /// (<c>cmp ax,0x7d0 / jae</c> @<c>image@0x03960</c>), where a SMALL answer means "this launcher
    /// is near the camera, so give its shot the modelled muzzle offset" and a large one means "use
    /// the launcher's own position".
    /// </para>
    /// <para>
    /// Makes every gated launcher NEAR, which is the permissive direction and was measured wrong:
    /// it moved 379 arena bytes on 109 AI-shot frames of <c>173046</c>.  <c>0xFFFF</c> is the
    /// conservative one, and on the recordings it is also the MEASURED one — every AI shot then lands
    /// exactly where the machine put it.  That is evidence the gate is a player/near-camera nicety,
    /// not a claim about the camera;
    /// </para>
    /// </remarks>
    public const ushort ViewAnchorRangeUnavailable = 0xFFFF;

    private readonly PoolArena _arena = arena ?? throw new ArgumentNullException(nameof(arena));

    /// <summary>The pool arena this actor's ported answers read.</summary>
    public PoolArena Arena => _arena;

    /// <summary>How many times the unavailable view-anchor range was asked for.</summary>
    public int ViewAnchorQueries { get; private set; }

    /// <inheritdoc/>
    public override CombatPosition RotateBodyOffset(
        ushort launcherObject, short offsetZ, short offsetX, short offsetY) =>
        SpawnRecordPositionFill.Run(_arena, launcherObject, offsetZ, offsetX, offsetY);

    /// <inheritdoc/>
    public override ushort RangeFromViewAnchor(ushort positionRef)
    {
        ViewAnchorQueries++;
        return Registers is { } registers
            ? Combat.Geometry.ViewAnchorRange.Compute(registers, _arena, positionRef)
            : ViewAnchorRangeUnavailable;
    }

    /// <inheritdoc/>
    public override ushort FindInRenderList(ushort renderListHead, ushort objectRef) =>
        RenderList is { } list
            ? list.Find(renderListHead, objectRef)
            : base.FindInRenderList(renderListHead, objectRef);

    /// <inheritdoc/>
    public override ushort RenderListNodeObject(ushort node) =>
        RenderList is { } list ? list.ObjectOf(node) : base.RenderListNodeObject(node);

    /// <inheritdoc/>
    public override bool RenderPhaseFlaggedObject(ushort renderListHead, ushort objectRef) =>
        RenderList is { } list
            ? list.Find(renderListHead, objectRef) != 0
            : base.RenderPhaseFlaggedObject(renderListHead, objectRef);

    /// <inheritdoc/>
    public override bool ObjectStillQualifies(ushort listEntry) =>
        RenderList is { } list ? list.Qualifies(listEntry) : base.ObjectStillQualifies(listEntry);

    /// <inheritdoc/>
    public override int BuildQualifyingObjectList(ushort listArgument, Span<ushort> candidates) =>
        RenderList is { } list
            ? list.Build(listArgument, candidates)
            : base.BuildQualifyingObjectList(listArgument, candidates);

    /// <inheritdoc/>
    public override (short ScreenX, short ScreenY) ProjectPositionToScreen(ushort objectRef) =>
        RenderList is { } list ? list.Project(objectRef) : base.ProjectPositionToScreen(objectRef);
}
