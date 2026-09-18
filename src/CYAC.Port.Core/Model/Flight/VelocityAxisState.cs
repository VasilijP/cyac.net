using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.Flight;

/// <summary>
/// Which of the three velocity blocks an instance of <see cref="VelocityAxisState"/> is, named by
/// the master-struct offset the original overlays it at.
/// </summary>
public enum VelocityBlock
{
    /// <summary>master <c>+0x00</c> — <c>vel_forward_i32</c>, the forward velocity.</summary>
    Forward = 0x00,

    /// <summary>master <c>+0x10</c> — <c>state_10_u32</c>, an angular-velocity accumulator.</summary>
    AngularA = 0x10,

    /// <summary>master <c>+0x20</c> — <c>state_20_u32</c>, an angular-velocity accumulator.</summary>
    AngularB = 0x20,
}

/// <summary>
/// One velocity axis' integration state: the 16-byte block the original overlays at master
/// <c>+0x00</c>, <c>+0x10</c> and <c>+0x20</c> — value, physics input, and a ± cap pair.
/// </summary>
/// <remarks>
/// <para>
/// Field class: the integer half of a <b>DUAL</b> pair.  <see cref="Value"/> and <see cref="Input"/>
/// are i32; the caps are i16 magnitudes the kernel widens to <c>×256</c> bounds.
/// </para>
/// <para>
/// Source of truth:  and KNOWN_FIELDS["s_aircraft_master"]</c> <c>+0x00/+0x08/+0x0C/+0x0E</c> (block
/// 0) with the <c>vel_s10_*</c> / <c>vel_s20_*</c> analogues at <c>+0x18..+0x1E</c> /
/// <c>+0x28..+0x2E</c>. The <see cref="OriginalFieldAttribute"/> offsets below name block 0, the one
/// the scanner spells out; <see cref="Block"/> records which instance a given object is.
/// </para>
/// <para>
/// <b>This is deliberately NOT <see cref="ControlAxisState"/>.</b>  The two share the field template
/// but not the semantics: <c>joystick_to_control_deflect</c> is never called with a velocity block
/// base, and <c>vel_integrate_and_cap @0x2AFB4</c> (driven once per block per frame by
/// <c>aoa_physics_tick @0x2B092</c>) reads <c>+0x0C</c>/<c>+0x0E</c> as ± velocity caps rather than
/// as control-deflection bounds.  §5b: "They share the field positions but assign velocity-domain
/// semantics."
/// </para>
/// <para>
/// <b>The cap widening is not a rounding detail.</b>  <c>vel_integrate_and_cap</c> converts the i16
/// cap to a signed <c>i32 × 256</c> bound (CDQ + byte-rotate idiom <c>image@0x2AFC2</c>), clamps the
/// incoming delta to <c>[−CapNegative×256, +CapPositive×256]</c>, then <c>ADD/ADC</c>s it into
/// <see cref="Value"/>.  Ghidra omits the whole cap phase; the bytes at <c>image@0x2AFBF</c> /
/// <c>image@0x2AFD7</c> are authoritative.
/// </para>
/// </remarks>
[OriginalStruct("s_aircraft_master")]
public sealed class VelocityAxisState
{
    /// <summary>Bytes one velocity block occupies in the original: <c>0x10</c>.</summary>
    public const int Bytes = 0x10;

    /// <summary>The scale the kernel applies to the i16 caps to get the i32 clamp bounds: 256.</summary>
    /// <remarks><c>image@0x2AFC2</c> — CDQ plus a byte rotate, i.e. <c>cap × 256</c>.</remarks>
    public const int CapScale = 256;

    /// <summary>Creates a velocity block for one of the three master-struct sites.</summary>
    /// <param name="block">Which block this is.</param>
    public VelocityAxisState(VelocityBlock block) => Block = block;

    /// <summary>Which of the three master-struct blocks this instance represents.</summary>
    public VelocityBlock Block { get; }

    /// <summary>The master-struct offset this block is overlaid at (<c>0x00</c>/<c>0x10</c>/<c>0x20</c>).</summary>
    public int MasterOffset => (int)Block;

    /// <summary>
    /// <c>+0x00</c> — the integrated velocity, i32 (block 0 = <c>vel_forward_i32</c>).
    /// </summary>
    [OriginalField("+0x00", "vel_forward_i32")]
    public int Value { get; set; }

    /// <summary>
    /// <c>+0x04</c> — the template's second i32 slot.  <b>(open)</b> — the velocity domain gives it
    /// no name and no reader is known; it is zero in all six shipped <c>.fmd</c> files.
    /// </summary>
    /// <remarks>
    /// In the control domain this slot is <c>working_i32</c>.  Modelled so the 16-byte shape is
    /// complete and a comparator can map the whole block; the port never reads it.
    /// </remarks>
    public int Working { get; set; }

    /// <summary>
    /// <c>+0x08</c> — the integrator template's HI bound, i16 (block 0 = <c>vel_fwd_bound_hi_i16</c>).
    /// </summary>
    /// <remarks>
    /// RENAMED + SPLIT R1 — <c>+0x08</c> and <c>+0x0A</c> are TWO i16 bounds, widened ×256 SEPARATELY by
    /// <c>ctrl_axis_bound_check_and_step</c> @<c>image@0x2B843</c>/<c>image@0x2B85C</c>; every shipped
    /// <c>.fmd</c> authors <c>+0x0A</c> = −32, which as one i32 would read −2,096,123 for the MiG-15.
    /// The <c>mov ax,[si+8] / dx,[si+0xa]</c> pair @<c>image@0x2B006</c>/<c>0x2B00B</c> loads the two
    /// words into the two halves of one register pair; it does not make them one number.
    /// </remarks>
    [OriginalField("+0x08", "vel_fwd_bound_hi_i16")]
    public short BoundHigh { get; set; }

    /// <summary>
    /// <c>+0x0A</c> — the integrator template's LO bound, i16 (block 0 = <c>vel_fwd_bound_lo_i16</c>).
    /// </summary>
    /// <remarks>
    /// Also the Phase-A <c>IDIV</c> divisor in <c>vel_roll_aoa_correction_accum @0x2ACFF</c> — unguarded,
    /// but unreachable on shipped data (<c>|+0x08| ≥ 32</c>).  K3 §4.1.
    /// </remarks>
    [OriginalField("+0x0A", "vel_fwd_bound_lo_i16")]
    public short BoundLow { get; set; }

    /// <summary><c>+0x0C</c> — positive velocity cap, i16 (widened by <see cref="CapScale"/>).</summary>
    [OriginalField("+0x0C", "vel_fwd_cap_pos_i16")]
    public short CapPositive { get; set; }

    /// <summary><c>+0x0E</c> — negative velocity cap <i>magnitude</i>, i16 (NEG'd @<c>image@0x2AFD7</c>).</summary>
    [OriginalField("+0x0E", "vel_fwd_cap_neg_i16")]
    public short CapNegative { get; set; }

    /// <summary>The clamp window the original's kernel actually uses, in i32 units.</summary>
    /// <remarks>
    /// <c>[−CapNegative × 256, +CapPositive × 256]</c> — the widened bounds from
    /// <c>vel_integrate_and_cap</c>.  Exposed as data, not applied: no stepping lives in this type.
    /// </remarks>
    public (int Minimum, int Maximum) ClampWindow =>
        (-CapNegative * CapScale, CapPositive * CapScale);

    /// <summary>A short, stable description for test output and debugging.</summary>
    public override string ToString() =>
        $"vel[{Block}](value={Value}, bounds=[{BoundLow}, {BoundHigh}], caps=[-{CapNegative}, +{CapPositive}])";
}
