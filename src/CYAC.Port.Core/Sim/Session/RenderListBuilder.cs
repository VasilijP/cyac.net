using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Lifecycle;
using CYAC.Port.Core.Sim.Combat.Player;
using CYAC.Port.Core.Sim.Flight;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// The per-frame DISPLAY LIST the player lock-on walks, built SIM-SIDE from the object pool.
/// </summary>
/// <remarks>
/// <para>
/// In the original the list is a by-product of drawing: <c>polygon_fill_mesh_render_setup
/// @image@0x146B8</c> clears <c>g_engagement_object_list_head [0xE90A]</c> (<c>image@0x147A1</c>),
/// bump-allocates a 22-byte node DOWN from <c>[0xEA00]</c> per object it puts on screen
/// (<c>sub word [0xEA00],0x16</c> @<c>image@0x16EDD</c>) and sets that object's flag bit 3 as the
/// "was drawn this frame" mark (C10: <c>or byte es:[bx+2],8</c> @<c>image@0x14AE5</c>).
/// <c>target_acquisition_state_machine_step</c> then walks the result.
/// </para>
/// <para>
/// <b>The port cannot take it from its renderer</b>: "a renderer must never write back into sim
/// state", and the port's renderer works in <c>double</c> at host resolution while the verified
/// lock-on is integer arithmetic on 320×200 screen coordinates.  So the SIM builds the list, in
/// integers, and the renderer reads the pool independently.  The two agree because they use the same
/// camera basis: <c>M = R_z·(R_y·R_x)</c> from
/// <see cref="BodyVelocityProjection.RotationFromEuler"/> — the integer flight kernel's own — and
/// the camera-space vector is <c>Mᵀ·(object − eye)</c>, view space being (X right, Y up, Z forward)
/// exactly as <c>CYAC.Port.Render.CameraPose</c> documents it.
/// </para>
/// <para>
/// The node's three projector inputs are then EXACTLY what the verified
/// <see cref="ObjectScreenProjection"/> expects: <c>+0x06</c> camera X, <c>+0x08</c> camera Y and
/// <c>+0x0A</c> the depth divisor, all in WORLD units, with the shift <c>[0xD8A2] + [0xD8A0] =
/// 7</c>.  Seven is not a guess: H3 derived the emitted projector's horizontal field of view as
/// 102.68°, and <c>160 / tan(51.34°) = 128.03 = 2^7</c>.
/// </para>
/// </remarks>
public static class RenderListBuilder
{
    /// <summary><c>[0xEA00]</c> — the node bump cursor (<c>image@0x147B0</c>).</summary>
    public const int NodeCursor = 0xEA00;

    /// <summary>The pool flag bit the render sets on every object it lists: bit 3 (C10).</summary>
    public const ushort DrawnThisFrame = 0x0008;

    /// <summary>The object flag bits the lock-on's qualifier demands: <c>0x0901</c>.</summary>
    public const ushort QualifyingFlags = 0x0901;

    /// <summary>How far, in world units, an object may be and still get a node.</summary>
    /// <remarks>
    /// A budget, not a rule: the block holds <see cref="RenderSlotNodes.NodeCapacity"/> nodes and
    /// An earlier pass measured the machine's own maximum at 45, so the port lists the nearest objects rather
    /// than overflowing.  Scenery beyond this is still DRAWN — the renderer reads the pool, not this
    /// list — it just cannot be locked onto.
    /// </remarks>
    public const int MaxNodeRangeWorldUnits = 200_000;

    /// <summary>Rebuilds the frame's display list.</summary>
    /// <param name="registers">The combat register file (the block lives in it).</param>
    /// <param name="arena">The pool arena.</param>
    /// <param name="playerRef">The player's object, which is never listed.</param>
    /// <returns>How many nodes were appended.</returns>
    public static int Build(CombatRegisters registers, PoolArena arena, ushort playerRef)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        registers.SetWord(CombatKernel.RenderListHeadWord, 0);        // image@0x147A1
        registers.SetWord(NodeCursor, RenderSlotNodes.TopNode);        // image@0x147B0

        if (playerRef == 0 || !arena.Covers(playerRef, 0x18))
        {
            return 0;
        }

        CombatObjectView player = new CombatObjectView(arena, playerRef);
        Mat3Q14 basis = BodyVelocityProjection.RotationFromEuler(
            new Angle(unchecked((ushort)arena.Word((ushort)(playerRef + 0x16)))),
            new Angle(unchecked((ushort)player.Elevation)),
            new Angle(unchecked((ushort)player.Heading)));

        int eyeX = player.Position.X >> 8;
        int eyeY = player.Position.Y >> 8;
        int eyeZ = player.Position.Z >> 8;

        int nodes = 0;
        ushort head = 0;
        ushort objectRef = registers.Word(LifecycleOffsets.RenderListHead);
        int guard = 0;
        while (objectRef != 0 && arena.Covers(objectRef, 0x18))
        {
            if (++guard > RenderSlotNodes.NodeCapacity * 8)
            {
                break;                                                 // a corrupt sibling chain
            }

            ushort next = arena.Word((ushort)(objectRef + 0x04));
            ushort flags = arena.Word((ushort)(objectRef + 0x02));
            arena.SetWord(
                (ushort)(objectRef + 0x02), (ushort)(flags & ~DrawnThisFrame));

            if (objectRef != playerRef
                && (flags & QualifyingFlags) == QualifyingFlags
                && nodes < RenderSlotNodes.NodeCapacity)
            {
                CombatObjectView view = new CombatObjectView(arena, objectRef);
                int dx = (view.Position.X >> 8) - eyeX;
                int dy = (view.Position.Y >> 8) - eyeY;
                int dz = (view.Position.Z >> 8) - eyeZ;

                if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) <= MaxNodeRangeWorldUnits)
                {
                    // camera = Mᵀ·d, the transpose because M is body→world.
                    int camX = Rotate(basis, dx, dy, dz, 0);
                    int camY = Rotate(basis, dx, dy, dz, 1);
                    int camZ = Rotate(basis, dx, dy, dz, 2);
                    if (camZ > 0)
                    {
                        head = Append(registers, head, objectRef, camX, camY, camZ);
                        arena.SetWord(
                            (ushort)(objectRef + 0x02),
                            (ushort)((flags & ~DrawnThisFrame) | DrawnThisFrame));
                        nodes++;
                    }
                }
            }

            objectRef = next;
        }

        registers.SetWord(CombatKernel.RenderListHeadWord, head);
        return nodes;
    }

    /// <summary>
    /// Publishes the camera constants the ported projector reads: the screen centre
    /// <c>[0xE634]/[0xE636]</c>, the clip rectangle <c>[0xE628..0xE62E]</c> and the shift halves
    /// <c>[0xD8A0]/[0xD8A2]</c>.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <remarks>
    /// They describe the ORIGINAL's 320×200 screen, not the host's, and deliberately so: the lock-on
    /// is integer arithmetic in that space and the port's renderer draws the box from its
    /// own float projection.  Mixing the two would make the kernel depend on a presentation setting,
    /// which the V5 detail-level law forbids.
    /// </remarks>
    public static void PublishCameraConstants(CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        registers.SetWord(ObjectScreenProjection.ScreenCentreX, 160);
        registers.SetWord(ObjectScreenProjection.ScreenCentreY, 100);
        registers.SetWord(ObjectScreenProjection.MapZoomLevel, 7);
        registers.SetWord(ObjectScreenProjection.ViewZoomShiftBias, 0);
        registers.SetWord(0xE628, 0);                                  // clip x min
        registers.SetWord(0xE62A, 319);                                // clip x max
        registers.SetWord(0xE62C, 0);                                  // clip y min
        registers.SetWord(0xE62E, 199);                                // clip y max
    }

    private static ushort Append(
        CombatRegisters registers, ushort head, ushort objectRef, int camX, int camY, int camZ)
    {
        ushort node = unchecked((ushort)(registers.Word(NodeCursor)));
        registers.SetWord(NodeCursor, unchecked((ushort)(node - RenderSlotNodes.NodeBytes)));
        registers.Span(node, RenderSlotNodes.NodeBytes).Clear();
        registers.SetWord(node + 0x02, head);
        registers.SetWord(node + 0x06, unchecked((ushort)Clamp(camX)));
        registers.SetWord(node + 0x08, unchecked((ushort)Clamp(camY)));
        registers.SetWord(node + 0x0A, unchecked((ushort)Clamp(camZ)));
        registers.SetWord(node + 0x14, objectRef);
        return node;
    }

    private static short Clamp(int value) =>
        (short)Math.Clamp(value, short.MinValue + 1, short.MaxValue);

    /// <summary>One component of <c>Mᵀ·d</c>, Q14 down-shifted back to world units.</summary>
    private static int Rotate(Mat3Q14 basis, int dx, int dy, int dz, int column) =>
        (int)(((long)basis.Signed(column) * dx
            + (long)basis.Signed(3 + column) * dy
            + (long)basis.Signed(6 + column) * dz) >> 14);
}
