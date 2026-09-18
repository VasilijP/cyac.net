namespace CYAC.Port.Render;

/// <summary>A three-component vector in <c>double</c> — the renderer's only geometric type.</summary>
/// <param name="X">First component.</param>
/// <param name="Y">Second component.</param>
/// <param name="Z">Third component.</param>
/// <remarks>
/// Renderer law D2 / (the anti-shimmer law): geometry is <c>double</c> and written from scratch.
/// Nothing of the 1991 bit-serial CSD transform, the per-mesh JIT or the Q14/Q30 fixed-point
/// projector appears here.
/// </remarks>
public readonly record struct Vec3(double X, double Y, double Z)
{
    /// <summary>The zero vector.</summary>
    public static Vec3 Zero => default;

    /// <summary>Component-wise sum.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static Vec3 operator +(Vec3 left, Vec3 right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    /// <summary>Component-wise difference.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static Vec3 operator -(Vec3 left, Vec3 right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    /// <summary>Scales a vector.</summary>
    /// <param name="v">The vector.</param>
    /// <param name="scale">The scale.</param>
    public static Vec3 operator *(Vec3 v, double scale) => new(v.X * scale, v.Y * scale, v.Z * scale);

    /// <summary>Dot product.</summary>
    /// <param name="other">The other vector.</param>
    public double Dot(Vec3 other) => (X * other.X) + (Y * other.Y) + (Z * other.Z);

    /// <summary>Cross product, right-handed.</summary>
    /// <param name="other">The other vector.</param>
    public Vec3 Cross(Vec3 other) => new(
        (Y * other.Z) - (Z * other.Y),
        (Z * other.X) - (X * other.Z),
        (X * other.Y) - (Y * other.X));

    /// <summary>Euclidean length.</summary>
    public double Length => Math.Sqrt(Dot(this));

    /// <summary>The Manhattan norm — the measure the original's own visibility test uses.</summary>
    /// <remarks>
    /// <c>mesh_visibility_lod_select @image@0x16BE8</c> computes <c>|Δx| + |Δy| + |Δz|</c> against the
    /// camera and compares its HIGH word with the class's three LOD thresholds.
    /// </remarks>
    public double Manhattan => Math.Abs(X) + Math.Abs(Y) + Math.Abs(Z);
}

/// <summary>
/// An orthonormal body frame: the world directions of an object's right, up and forward axes.
/// </summary>
/// <param name="Right">World direction of local +X — the right wing.</param>
/// <param name="Up">World direction of local +Y.</param>
/// <param name="Forward">World direction of local +Z — the nose.</param>
/// <remarks>
/// <para>
/// The one place the port's world convention is written down, and it is the SIMULATION's, not a
/// graphics convention.  Expanding the integer flight kernel's own
/// <c>BodyVelocityProjection.RotationFromEuler</c> (the ported <c>gfx_rot_mat3_from_euler
/// @image@0x14D9C</c>, whose product <c>ApplyVelocityStage</c> phase 9b integrates straight into the
/// world object's position) for a pure forward body velocity gives
/// </para>
/// <code>
/// forward(h, p) = ( −sin h · cos p ,  sin p ,  cos h · cos p )
/// </code>
/// <para>
/// so <b>heading 0 points at +Z and the heading turns toward −X, +Y is up, and a positive pitch is
/// nose-up</b>.  Two more doors agree independently: <c>angle_mat3_pair_build @image@0x1BFA4</c>
/// NEGATES its heading before the trig lookups, which is what makes a placed mesh's local +Z land on
/// <c>(−sin h, cos h)</c> (<c>CYAC.Formats/Mesh/SceneryFootprint.cs</c> <c>HeadingConvention</c>,
/// Without the negation the 507 river placements scatter; with it they trace
/// continuous water courses); and the spherical accumulator <c>angle_distance_xyz_accum
/// @image@0x2084A</c> lays out <c>out[X] += −d·cos e·sin h</c>, <c>out[Y] += +d·sin e</c>, <c>out[Z]
/// += +d·cos e·cos h</c> (*.c</c>, corrected reading).
/// </para>
/// <para>
/// The roll sign follows from the same expansion: at <c>h = p = 0</c> a positive roll tips
/// <see cref="Right"/> to <c>(cos r, −sin r, 0)</c>, i.e. right wing DOWN, which is H1's measured
/// census (361 frames agreeing, 0 against).
/// </para>
/// <para>
/// <see cref="Right"/> × <see cref="Up"/> = <see cref="Forward"/>.
/// </para>
/// </remarks>
public readonly record struct Basis3(Vec3 Right, Vec3 Up, Vec3 Forward)
{
    /// <summary>The identity frame: right +X, up +Y, forward +Z.</summary>
    public static Basis3 Identity => new(new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1));

    /// <summary>Builds the frame of a heading / pitch / roll triple, in radians.</summary>
    /// <param name="headingRadians">Heading; 0 = +Z, turning toward −X.</param>
    /// <param name="pitchRadians">Pitch; positive = nose up.</param>
    /// <param name="rollRadians">Roll; positive = right wing down.</param>
    public static Basis3 FromEuler(double headingRadians, double pitchRadians, double rollRadians)
    {
        double ch = Math.Cos(headingRadians), sh = Math.Sin(headingRadians);
        double cp = Math.Cos(pitchRadians), sp = Math.Sin(pitchRadians);
        double cr = Math.Cos(rollRadians), sr = Math.Sin(rollRadians);

        return new Basis3(
            Right: new Vec3((ch * cr) - (sh * sp * sr), -cp * sr, (sh * cr) + (ch * sp * sr)),
            Up: new Vec3((ch * sr) + (sh * sp * cr), cp * cr, (sh * sr) - (ch * sp * cr)),
            Forward: new Vec3(-sh * cp, sp, ch * cp));
    }

    /// <summary>Rotates a body-frame vector into world coordinates.</summary>
    /// <param name="local">The body-frame vector.</param>
    public Vec3 ToWorld(Vec3 local) =>
        (Right * local.X) + (Up * local.Y) + (Forward * local.Z);

    /// <summary>Rotates a world vector into this frame — the transpose, which for an orthonormal frame is the inverse.</summary>
    /// <param name="world">The world vector.</param>
    public Vec3 ToLocal(Vec3 world) =>
        new(world.Dot(Right), world.Dot(Up), world.Dot(Forward));
}
