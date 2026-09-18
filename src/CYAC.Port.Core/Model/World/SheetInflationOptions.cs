namespace CYAC.Port.Core.Model.World;

/// <summary>
/// The knobs of <see cref="SheetInflation"/>: thickness ratios per sheet kind (fractions of the local chord, tapered
/// root → tip), the slab thickness for articulated parts, the chord stations the skins are subdivided at, and the
/// marking lift.  Ratios are scale-free, so one set serves a P-51 and a B-52 alike; per-class exceptions go through
/// <see cref="Overrides"/>.
/// </summary>
/// <param name="Enabled">Whether the pass generates anything (the census is always taken).</param>
/// <param name="WingRootThicknessRatio">Main wing max thickness / chord at the root (a P-51's is ≈ 0.16 by the book; 0.12 reads right at the polygon budget).</param>
/// <param name="WingTipThicknessRatio">… at the tip.</param>
/// <param name="TailRootThicknessRatio">Tailplane ratio at the root.</param>
/// <param name="TailTipThicknessRatio">… at the tip.</param>
/// <param name="FinRootThicknessRatio">Fin ratio at the root (the fuselage end).</param>
/// <param name="FinTipThicknessRatio">… at the tip.</param>
/// <param name="SlabHalfThicknessFraction">An articulated slab's HALF thickness as a fraction of its smaller in-plane extent.</param>
/// <param name="DecalLiftModelUnits">How far a cloned marking sits above its skin per stacking level, model units (<see cref="DecalConditioning.DefaultEpsilonModelUnits"/>).</param>
/// <param name="LoneSheetMinArea">A lone never-culled polygon must have at least this area (square model units) to count as a sheet rather than a marking.</param>
/// <param name="Stations">The chordwise fractions the skins are split at, ascending from 0 to 1; null = no subdivision (a slab section).</param>
public readonly record struct SheetInflationOptions(
    bool Enabled = true,
    double WingRootThicknessRatio = 0.12,
    double WingTipThicknessRatio = 0.08,
    double TailRootThicknessRatio = 0.09,
    double TailTipThicknessRatio = 0.06,
    double FinRootThicknessRatio = 0.08,
    double FinTipThicknessRatio = 0.05,
    double SlabHalfThicknessFraction = 0.08,
    double DecalLiftModelUnits = DecalConditioning.DefaultEpsilonModelUnits,
    double LoneSheetMinArea = 150.0,
    double[]? Stations = null)
{
    /// <summary>The default chord stations: a rounded nose, the thickest point, a tapered tail.</summary>
    public static readonly double[] DefaultStations = [0.0, 0.08, 0.3, 0.6, 1.0];

    /// <summary>
    /// The port's defaults.  Built with explicit arguments: a record struct's parameterless
    /// <c>new</c> is the all-zero instance (disabled), the trap <see cref="MeshConditioning"/>
    /// documents.
    /// </summary>
    public static SheetInflationOptions Default { get; } = new(
        Enabled: true,
        WingRootThicknessRatio: 0.12,
        WingTipThicknessRatio: 0.08,
        TailRootThicknessRatio: 0.09,
        TailTipThicknessRatio: 0.06,
        FinRootThicknessRatio: 0.08,
        FinTipThicknessRatio: 0.05,
        SlabHalfThicknessFraction: 0.08,
        DecalLiftModelUnits: DecalConditioning.DefaultEpsilonModelUnits,
        LoneSheetMinArea: 150.0,
        Stations: DefaultStations);

    /// <summary>The pass off; the census still runs.</summary>
    public static SheetInflationOptions Off { get; } = Default with { Enabled = false };

    /// <summary>Every thickness ratio and the slab fraction multiplied by one factor (a tuning knob).</summary>
    /// <param name="factor">1 = unchanged.</param>
    public SheetInflationOptions Scaled(double factor) => this with
    {
        WingRootThicknessRatio = WingRootThicknessRatio * factor,
        WingTipThicknessRatio = WingTipThicknessRatio * factor,
        TailRootThicknessRatio = TailRootThicknessRatio * factor,
        TailTipThicknessRatio = TailTipThicknessRatio * factor,
        FinRootThicknessRatio = FinRootThicknessRatio * factor,
        FinTipThicknessRatio = FinTipThicknessRatio * factor,
        SlabHalfThicknessFraction = SlabHalfThicknessFraction * factor,
    };

    /// <summary>
    /// Per-class exceptions, keyed by mesh basename: a function from the fleet-wide options to the
    /// class's own.  KNOWLEDGE added after looking at a model in a mesh viewer's
    /// "Enhanced models" tab — numbers, never geometry.  Empty until a class needs one.
    /// </summary>
    public static IReadOnlyDictionary<string, Func<SheetInflationOptions, SheetInflationOptions>> Overrides { get; } =
        new Dictionary<string, Func<SheetInflationOptions, SheetInflationOptions>>(StringComparer.OrdinalIgnoreCase)
        {
        };

    /// <summary>These options as they apply to one class.</summary>
    /// <param name="basename">The mesh basename, e.g. <c>p51</c>.</param>
    public SheetInflationOptions ForClass(string? basename) =>
        basename is not null && Overrides.TryGetValue(basename, out Func<SheetInflationOptions, SheetInflationOptions>? adjust) ? adjust(this) : this;
}
