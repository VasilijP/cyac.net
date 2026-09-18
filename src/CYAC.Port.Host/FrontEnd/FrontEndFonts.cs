using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// The three fonts the front-end screens draw with.
/// </summary>
/// <remarks>
/// <para>
/// F1's screens used a single font (<c>propbold</c>).  F2's historic-chain screens use three, proven
/// by the atlas (verified 0-mismatch on all seven reference screens): the BOLD screen/row titles and
/// widget captions are <c>propbold</c>; the briefing body and the mission-list descriptions are the
/// regular proportional <c>prop3</c>; the small monospace <c>4x6</c> draws the <c>PAGE %d OF %d</c>
/// counter, the mission dates and the aircraft short name. Which element uses which was DETERMINED,
/// not guessed — each was brute-forced against its reference pixels until exactly one font gave a
/// zero-mismatch fit.
/// </para>
/// </remarks>
/// <param name="Bold">The bold proportional font — titles and widget captions.</param>
/// <param name="Prop">The regular proportional font — briefing and description bodies.</param>
/// <param name="Small">The 4×6 monospace font — page counter, dates, aircraft short name.</param>
public sealed record FrontEndFonts(CockpitFont Bold, CockpitFont Prop, CockpitFont Small)
{
    /// <summary>The bold font's name in the data tree.</summary>
    public const string BoldName = "propbold";

    /// <summary>The regular proportional font's name.</summary>
    public const string PropName = "prop3";

    /// <summary>The 4×6 monospace font's name.</summary>
    public const string SmallName = "4x6";

    /// <summary>Loads all three from the data tree.</summary>
    /// <param name="tree">The data tree.</param>
    /// <returns>The bundle, or <see langword="null"/> if any font is missing.</returns>
    public static FrontEndFonts? Load(Core.Data.DataTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        CockpitFont? bold = CockpitAssets.LoadFont(tree, BoldName);
        CockpitFont? prop = CockpitAssets.LoadFont(tree, PropName);
        CockpitFont? small = CockpitAssets.LoadFont(tree, SmallName);
        return bold is null || prop is null || small is null
            ? null
            : new FrontEndFonts(bold, prop, small);
    }
}
