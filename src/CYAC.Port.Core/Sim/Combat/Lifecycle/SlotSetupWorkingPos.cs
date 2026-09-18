using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Flight;

namespace CYAC.Port.Core.Sim.Combat.Lifecycle;

/// <summary>
/// <c>slot_setup_working_pos @image@0x2C3D3</c> — rotate three seed words into the PARENT object's
/// frame, the debris rig's launch direction.
/// </summary>
/// <remarks>
/// <para>
/// An earlier pass left it a seam (<c>ILifecycleEvents.RotateDebrisOffset</c>) for the same reason an earlier pass left the
/// muzzle one: its callees are the renderer's Euler pair.  It is the SAME pair, so it costs the
/// same nothing — <see cref="BodyVelocityProjection.RotationFromEuler"/> and
/// <see cref="BodyVelocityProjection.Transform"/>.
/// </para>
/// <para>
/// The two differ in exactly one way, and it is visible in the bytes: the muzzle routine takes the
/// rotated rows <c>&gt;&gt; 8</c> and ADDS the launcher's world position
/// (<c>image@0x236EB</c>), while this one keeps only each row's HIGH WORD — <c>mov ax,[bp-0xa]</c>
/// / <c>mov ax,[bp-6]</c> / <c>mov ax,[bp-2]</c> at <c>image@0x2C42D..0x2C43D</c>, i.e.
/// <c>row &gt;&gt; 16</c> — and writes it back through the caller's three pointers.
/// </para>
/// </remarks>
public static class SlotSetupWorkingPos
{
    /// <summary>Rotates the three seed words into the parent's frame, in place.</summary>
    /// <param name="arena">The pool arena the parent object lives in.</param>
    /// <param name="parentRef">The parent's pool near offset — the original's <c>[bp+0x0A]</c>.</param>
    /// <param name="x">The <c>[bp+8]</c> seed; replaced by the rotated value.</param>
    /// <param name="y">The <c>[bp+6]</c> seed; replaced.</param>
    /// <param name="z">The <c>[bp+4]</c> seed; replaced.</param>
    public static void Run(
        PoolArena arena, ushort parentRef, ref short x, ref short y, ref short z)
    {
        ArgumentNullException.ThrowIfNull(arena);

        // image@0x2C40F..0x2C41B — heading, pitch, ROLL pushed in that order, so roll is first.
        CombatObjectView parent = new CombatObjectView(arena, parentRef);
        Mat3Q14 matrix = BodyVelocityProjection.RotationFromEuler(
            new Angle(unchecked((ushort)arena.Word(unchecked((ushort)(parentRef + 0x16))))),
            new Angle(unchecked((ushort)parent.Elevation)),
            new Angle(unchecked((ushort)parent.Heading)));

        // image@0x2C428 → image@0x1BEFE.  The staged vector's high words are the three seeds.
        (int row0, int row1, int row2) = BodyVelocityProjection.Transform(matrix, x, y, z);

        // image@0x2C42D..0x2C43D — only each row's HIGH word survives.
        x = unchecked((short)(row0 >> 16));
        y = unchecked((short)(row1 >> 16));
        z = unchecked((short)(row2 >> 16));
    }
}

/// <summary>
/// The lifecycle's out-calls with the one the port can compute filled in — the shipping wiring.
/// </summary>
/// <param name="arena">The pool arena the parent object lives in.</param>
public class PortedLifecycleEvents(PoolArena arena) : NullLifecycleEvents
{
    private readonly PoolArena _arena = arena ?? throw new ArgumentNullException(nameof(arena));

    /// <summary>The pool arena this instance rotates debris offsets in.</summary>
    protected PoolArena Arena => _arena;

    /// <inheritdoc/>
    public override void RotateDebrisOffset(ushort parentRef, ref short x, ref short y, ref short z) =>
        SlotSetupWorkingPos.Run(_arena, parentRef, ref x, ref y, ref z);
}
