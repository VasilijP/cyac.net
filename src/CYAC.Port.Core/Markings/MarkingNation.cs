using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Markings;

/// <summary>
/// VECTOR MARKINGS — which nation's insignia a class wears, from the shipped class table's own
/// <c>insigniaIndex</c> (<c>s_engagement_prototype +0x2C</c>, the <c>insigv</c> cell the title bar
/// blits and the radio kill-call's phrase key; <c>data/exe/tables/engagement.json</c>).
/// </summary>
/// <remarks>
/// Observed over the shipped table: 0 = United States (p47 p51 b17 f86 l5 b29 f4 f105 b52), 1 =
/// Germany (fw190 me109 me163 me262 me110), 2 = Soviet Union (yak9 mig15), 3 = North Vietnam
/// (mig17 mig21).  The mapping to a picture is the port's knowledge; the index is the game's own
/// fact and the only byte read.
/// </remarks>
public static class MarkingNation
{
    /// <summary>The nation names, by insignia index.</summary>
    public static readonly IReadOnlyList<string> Names = ["usa", "germany", "ussr", "vietnam"];

    /// <summary>The nation for an insignia index, or null.</summary>
    /// <param name="insigniaIndex">The class table's <c>insigniaIndex</c>.</param>
    public static string? FromInsigniaIndex(int insigniaIndex) =>
        insigniaIndex >= 0 && insigniaIndex < Names.Count ? Names[insigniaIndex] : null;

    /// <summary>The nation a class wears, from the data tree, or null when the table does not list it.</summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="basename">The mesh basename.</param>
    public static string? ForClass(DataTree tree, string basename)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(basename);
        EngagementPrototypeDto? prototype = tree.Engagement.Prototypes?
            .FirstOrDefault(p => string.Equals(p.ClassRecord, basename, StringComparison.OrdinalIgnoreCase));
        return prototype is null ? null : FromInsigniaIndex(prototype.InsigniaIndex);
    }

    /// <summary>The national insignia picture for a nation and era.</summary>
    /// <param name="nation">A name from <see cref="Names"/>.</param>
    /// <param name="postwar">Whether the class is a jet-age type (the 1947 US bars with red stripes).</param>
    public static string? Insignia(string? nation, bool postwar) => nation switch
    {
        "usa" => postwar ? "usaf-star-bar" : "usaaf-star-bar",
        "germany" => "balkenkreuz",
        "ussr" => "soviet-star",
        "vietnam" => "vpaf-insignia",
        _ => null,
    };

    /// <summary>Whether a class is a jet-age type for the insignia choice (a name rule, not a game fact).</summary>
    /// <param name="basename">The mesh basename.</param>
    public static bool IsPostwar(string basename) =>
        basename is "f86" or "f4" or "f105" or "b52" or "mig15" or "mig17" or "mig21";
}
