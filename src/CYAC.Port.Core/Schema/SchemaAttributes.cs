namespace CYAC.Port.Core.Schema;

/// <summary>
/// Metadata only — it carries no runtime behaviour.
/// </summary>
/// <remarks>
/// Convention from <c>src/CYAC.Port.Core/README.md</c> § "The schema is the ground truth of the data
/// model": port names are domain names, the original <c>s_*</c> identifiers live only in attributes,
/// so comparators and the effect-verification rig can map port state ↔ original memory without the
/// port's own model importing DGROUP vocabulary.
/// </remarks>
/// <param name="name">The scanner struct name, e.g. <c>s_aircraft_master</c>.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface,
    AllowMultiple = false, Inherited = false)]
public sealed class OriginalStructAttribute(string name) : Attribute
{
    /// <summary>The scanner struct name this type represents.</summary>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));
}

/// <summary>
/// Marks a port member as the domain representation of a field inside an original struct.
/// Metadata only — it carries no runtime behaviour.
/// </summary>
/// <param name="offset">
/// The field's offset in the project's citation form, e.g. <c>"+0xD6"</c>.  Kept as a string on
/// purpose: it is quoted verbatim in reports and matches how the docs cite it.
/// </param>
/// <param name="field">
/// Optional: the scanner's raw descriptor or field name (e.g. <c>"ctrl_aoa_block"</c>), when the port
/// name alone would not identify it.
/// </param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = false, Inherited = false)]
public sealed class OriginalFieldAttribute(string offset, string? field = null) : Attribute
{
    /// <summary>The original field offset, as cited (e.g. <c>+0xD6</c>).</summary>
    public string Offset { get; } = offset ?? throw new ArgumentNullException(nameof(offset));

    /// <summary>The original field name / descriptor, when supplied.</summary>
    public string? Field { get; } = field;
}

/// <summary>
/// Marks a port member (or type) as the domain representation of an original DGROUP global.
/// Metadata only — it carries no runtime behaviour.
/// </summary>
/// <param name="name">The scanner global name, e.g. <c>g_master_frame_counter</c>.</param>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Class | AttributeTargets.Struct,
    AllowMultiple = true, Inherited = false)]
public sealed class OriginalGlobalAttribute(string name) : Attribute
{
    /// <summary>The scanner global name this member represents.</summary>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));
}
