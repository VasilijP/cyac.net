namespace CYAC.Port.Core.Model.World;

/// <summary>
/// VECTOR MARKINGS — which markings a class draws.
/// </summary>
public enum MarkingsMode
{
    /// <summary>No markings: the shipped marking records are hidden, no vector markings drawn.</summary>
    Off = 0,

    /// <summary>The 1991 polygon markings, as shipped (the PoC look; the default until the fleet is approved).</summary>
    Shipped = 1,

    /// <summary>The port's projected vector markings; shipped records a placement replaces are hidden.</summary>
    Vector = 2,
}

/// <summary>
/// The ASSET-CONDITIONING options the mesh library applies when it builds a class — LOD-skip plus
/// asset conditioning: knowledge the port adds to the shipped geometry, never geometry it copies.
/// </summary>
/// <param name="DecalLiftModelUnits">
/// The <see cref="DecalConditioning"/> lift above a decal's base plane, in model units; 0 or less
/// switches the pass off (the census is still taken).
/// </param>
/// <param name="Sheets">
/// The <see cref="SheetInflation"/> options: null switches the pass off (the census is still
/// taken).  Runs BEFORE the decal lift, so a marking on a wing is cloned to the two skins first
/// and the lift pass then finds it already in front of its base.
/// </param>
public readonly record struct MeshConditioning(
    double DecalLiftModelUnits = DecalConditioning.DefaultEpsilonModelUnits,
    SheetInflationOptions? Sheets = null,
    MarkingsMode Markings = MarkingsMode.Shipped,
    Markings.MarkingLibrary? MarkingLibrary = null,
    double VertexWeldModelUnits = VertexWeld.DefaultEpsilonModelUnits)
{
    /// <summary>Whether vector markings are resolved at build: the mode says so AND a library is given.</summary>
    public bool VectorMarkings => Markings == MarkingsMode.Vector && MarkingLibrary is not null;

    /// <summary>
    /// The port's defaults.  Built with the explicit arguments: a record struct's parameterless
    /// <c>new</c> does NOT run the primary constructor, so <c>new</c> would be the all-zero (lift
    /// OFF, sheets OFF) instance — the same trap <c>SceneInstance</c> documents.
    /// </summary>
    public static MeshConditioning Default { get; } =
        new(DecalConditioning.DefaultEpsilonModelUnits, SheetInflationOptions.Default);

    /// <summary>Everything off: the documents as they are.</summary>
    public static MeshConditioning None { get; } = new(0.0, null, VertexWeldModelUnits: 0.0);

    /// <summary>The sheet options as they apply, or the pass off.</summary>
    public SheetInflationOptions SheetsOrOff => Sheets ?? SheetInflationOptions.Off;
}
