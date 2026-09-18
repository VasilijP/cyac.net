using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.Flight;

/// <summary>
/// What the runtime does with a given 16-byte <c>.fmd</c> init block, by block index.
/// </summary>
/// <remarks>
/// Blocks 3 and 5 are the two proven <see cref="ControlAxisState"/> instances; 0/1/2 are the three
/// <see cref="VelocityAxisState"/> blocks; 6/7/8 seed the ATTITUDE axes' limits (ex-"the position axes' wrap bounds", K4 / R1).  Confidence
/// per block is on <see cref="AircraftInitBlock.RoleIsVerified"/>.
/// </remarks>
public enum AircraftInitBlockRole
{
    /// <summary>Block 0 → master <c>+0x00</c>: forward velocity (<c>vel_forward_i32</c>).</summary>
    ForwardVelocity = 0,

    /// <summary>Block 1 → master <c>+0x10</c>: angular velocity A (<c>state_10_u32</c>).</summary>
    AngularVelocityA = 1,

    /// <summary>Block 2 → master <c>+0x20</c>: angular velocity B (<c>state_20_u32</c>).</summary>
    AngularVelocityB = 2,

    /// <summary>Block 3 → master <c>+0x30</c>: the roll control axis, DEAD path.</summary>
    RollDeadControl = 3,

    /// <summary>Block 4 → master <c>+0x40</c>: the heading-from-AoA result slot (NOT a control block).</summary>
    HeadingFromAoa = 4,

    /// <summary>Block 5 → master <c>+0x50</c>: the roll control axis, ALIVE path.</summary>
    RollAliveControl = 5,

    /// <summary>Block 6 → master <c>+0x60</c>: X position and its toroidal wrap bounds.</summary>
    AttitudeRoll = 6,

    /// <summary>Block 7 → master <c>+0x70</c>: Y position (altitude) and its wrap bounds.</summary>
    AttitudePitch = 7,

    /// <summary>Block 8 → master <c>+0x80</c>: Z position and its wrap bounds.</summary>
    AttitudeHeading = 8,
}

/// <summary>
/// One of the nine 16-byte blocks that open a <c>.fmd</c> file — the original
/// <c>FlightModelDamageRecord</c>.
/// </summary>
/// <remarks>
/// <para>
/// INT-only, immutable: authored per-aircraft content.  Because the loader bulk-copies the file
/// (see <see cref="AircraftDefinition"/>), block <c>i</c> lands at master <c>+0x10×i</c> and IS the
/// initial image of that block of runtime state — so these are not "damage records" at all but the
/// nine integrator blocks, in the same 16-byte template as <see cref="ControlAxisState"/> /
/// <see cref="VelocityAxisState"/>.  The scanner's struct name is a round-13b data-pattern name and
/// is kept in the attribute only.
/// </para>
/// <para>
/// Sources: <c>src/CYAC.Formats/EaLib/FlightModelDecoder.cs</c> (<c>FmdBlockCount</c> = 9,
/// <c>FmdBlockStride</c> = 0x10, <c>BlockRoles</c>), and
/// KNOWN_FIELDS["FlightModelDamageRecord"]</c>.  The template mapping <c>param_a..param_d</c> →
/// <c>hi_bound / lo_bound / base_dir / dir_step</c> is anchored by the three proven control blocks:
/// block 3's <c>+0x08/+0x0A</c> are the master's
/// <c>ctrl_dead_hi_bound_i16</c>/<c>ctrl_dead_lo_bound_i16</c> at <c>+0x38</c>/<c>+0x3A</c>.
/// </para>
/// <para>
/// <b>On-disk stride is 16, not 24.</b> The schema's <c>FlightModelDamageRecord</c> reaches
/// <c>+0x16</c> because P22 recorded four <c>runtime_*</c> aliases there; those describe record 0
/// <i>of the heap-resident copy</i> while <c>aircraft_pose_set</c> uses it as a pose scratchpad, and
/// they physically overlap record 1.
/// </para>
/// </remarks>
[OriginalStruct("FlightModelDamageRecord")]
public sealed class AircraftInitBlock
{
    /// <summary>Bytes one block occupies on disk: <c>0x10</c> (<c>FlightModelDecoder.FmdBlockStride</c>).</summary>
    public const int Bytes = 0x10;

    /// <summary>Blocks at the head of a <c>.fmd</c>: 9, covering <c>0x00..0x8F</c>.</summary>
    public const int Count = 9;

    internal AircraftInitBlock(int index, int value, int working,
        short hiBound, short loBound, short baseDir, short dirStep)
    {
        Index = index;
        Value = value;
        Working = working;
        HiBound = hiBound;
        LoBound = loBound;
        BaseDir = baseDir;
        DirStep = dirStep;
    }

    /// <summary>The block's position in the file, 0..8.</summary>
    public int Index { get; }

    /// <summary>The master-struct offset this block is copied to: <c><see cref="Index"/> × 0x10</c>.</summary>
    public int MasterOffset => Index * Bytes;

    /// <summary>What the runtime does with this block.</summary>
    public AircraftInitBlockRole Role => (AircraftInitBlockRole)Index;

    /// <summary>
    /// True when the block's runtime role is byte-proven rather than inferred.
    /// </summary>
    /// <remarks>
    /// Verified: blocks 3 and 5 (the two <c>joystick_to_control_deflect</c> call sites) and
    /// blocks 6/7/8, whose <c>+0x08</c>/<c>+0x0A</c> are the master's <c>pos_*_wrap_upper/lower_i16</c>
    /// read by <c>world_wrap_axis @0x2B8DE</c> (P603).  The bases of 0/1/2/4 are verified but their
    /// bound semantics are a hypothesis (<c>FlightModelDecoder.BlockRoles</c>).
    /// </remarks>
    public bool RoleIsVerified => Index is 3 or 5 or 6 or 7 or 8;

    /// <summary><c>+0x00</c> — the block's i32 value slot; zero in every shipped file.</summary>
    [OriginalField("+0x00", "runtime_zero_a_u32")]
    public int Value { get; }

    /// <summary><c>+0x04</c> — the block's i32 scratch slot; zero in every shipped file.</summary>
    [OriginalField("+0x04", "runtime_zero_b_u32")]
    public int Working { get; }

    /// <summary><c>+0x08</c> — the template's <c>hi_bound</c> (scanner: <c>param_a_i16</c>).</summary>
    [OriginalField("+0x08", "param_a_i16")]
    public short HiBound { get; }

    /// <summary><c>+0x0A</c> — the template's <c>lo_bound</c> (scanner: <c>param_b_i16</c>).</summary>
    [OriginalField("+0x0A", "param_b_i16")]
    public short LoBound { get; }

    /// <summary><c>+0x0C</c> — the template's <c>base_dir</c> (scanner: <c>param_c_i16</c>).</summary>
    [OriginalField("+0x0C", "param_c_i16")]
    public short BaseDir { get; }

    /// <summary><c>+0x0E</c> — the template's <c>dir_step</c> (scanner: <c>param_d_i16</c>).</summary>
    [OriginalField("+0x0E", "param_d_i16")]
    public short DirStep { get; }

    /// <summary>This block as a control axis (blocks 3 and 5).</summary>
    public ControlAxisState ToControlAxis() =>
        ControlAxisState.FromBounds(HiBound, LoBound, BaseDir, DirStep);

    /// <summary>This block as a velocity axis (blocks 0, 1 and 2).</summary>
    /// <param name="block">Which velocity block this is.</param>
    /// <remarks>
    /// <para>
    /// Mapped through the velocity-domain field map: value at <c>+0x00</c>, physics input at <c>+0x08</c>
    /// (i.e. the <c>+0x08</c>/<c>+0x0A</c> word pair read as one i32), ± caps at
    /// <c>+0x0C</c>/<c>+0x0E</c>.  Note this is NOT the control mapping: what <see cref="ToControlAxis"/>
    /// calls <see cref="HiBound"/>/<see cref="LoBound"/> is
    /// <see cref="VelocityAxisState.BoundHigh"/>/<see cref="VelocityAxisState.BoundLow"/> here too, and
    /// <see cref="BaseDir"/>/<see cref="DirStep"/> become the caps.
    /// </para>
    /// <para>
    /// CLOSED K3 §4.1 / R1 — the ± <i>pair</i> reading is right and P702's i32 was wrong: all nine shipped
    /// blocks author a pair (block 0 = <c>{683, −32}</c> for the P-51, <c>{2132, −32}</c> for the F-4, the
    /// per-aircraft maximum forward speed in fps — see
    /// <see cref="AircraftDefinition.MaxForwardSpeedFps"/>), and <c>ctrl_axis_bound_check_and_step</c>
    /// widens the two words ×256 separately.
    /// </para>
    /// </remarks>
    public VelocityAxisState ToVelocityAxis(VelocityBlock block) =>
        new(block)
        {
            Value = Value,
            Working = Working,
            BoundHigh = HiBound,
            BoundLow = LoBound,
            CapPositive = BaseDir,
            CapNegative = DirStep,
        };

    /// <summary>A short, stable description for test output and debugging.</summary>
    public override string ToString() =>
        $"blk{Index} (+0x{MasterOffset:X2} {Role}): [{LoBound}, {HiBound}] dir={BaseDir}/{DirStep}";
}
