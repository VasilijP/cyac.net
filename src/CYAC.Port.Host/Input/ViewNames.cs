using CYAC.Port.Render;

namespace CYAC.Port.Host;

/// <summary>
/// The eighteen view keys' names, for <c>--view</c> and for the readout.
/// </summary>
/// <remarks>
/// The labels are the manual's own (from the View Keys chapter pp. 45-47) and the key column is
/// the byte-derived scancode→view-id dispatch at <c>image@0x33256..0x332CA</c>.  F9 is view id
/// <b>0x0C</b> (<c>image@0x33288</c>), and the map is BUILT: <c>CYAC.Port.Render.Map.MapView</c>.
/// </remarks>
public static class ViewNames
{
    /// <summary>One view: its id, the key that selects it, and the manual's label.</summary>
    /// <param name="Mode">The view.</param>
    /// <param name="Key">The key, as the manual writes it.</param>
    /// <param name="Label">The manual's label.</param>
    /// <param name="Option">The <c>--view</c> spelling.</param>
    public readonly record struct ViewEntry(ViewMode Mode, string Key, string Label, string Option);

    /// <summary>Every view the PoC builds a camera for, in view-id order.</summary>
    public static IReadOnlyList<ViewEntry> All { get; } =
    [
        new(ViewMode.CockpitForward, "F1", "Forward", "cockpit"),
        new(ViewMode.CockpitBack, "F2", "Back", "back"),
        new(ViewMode.CockpitLeft, "F3", "Left", "left"),
        new(ViewMode.CockpitRight, "F4", "Right", "right"),
        new(ViewMode.CockpitUp, "F5", "Up 45", "up"),
        new(ViewMode.CockpitDown, "F6", "Down 45", "down"),
        new(ViewMode.ExternalForward, "Shift-F1", "External Forward", "chase"),
        new(ViewMode.ExternalBack, "Shift-F2", "External Back", "extback"),
        new(ViewMode.ExternalRight, "Shift-F3", "External Right", "extright"),
        new(ViewMode.ExternalLeft, "Shift-F4", "External Left", "extleft"),
        new(ViewMode.ExternalBelow, "Shift-F5", "External Below", "below"),
        new(ViewMode.ExternalAbove, "Shift-F6", "External Above", "above"),
        new(ViewMode.Map, "F9", "Map", "map"),
        new(ViewMode.FlyBy, "F10", "Fly-By", "flyby"),
        new(ViewMode.Circling, "Shift-F9", "Circling", "circling"),
        new(ViewMode.PlaneToTarget, "F7", "Plane to Target", "planetotarget"),
        new(ViewMode.TargetToPlane, "F8", "Target to Plane", "targettoplane"),
        new(ViewMode.TargetCockpit, "Shift-F7", "Target's Cockpit", "targetcockpit"),
        new(ViewMode.ExternalTarget, "Shift-F8", "External Target", "exttarget"),
        new(ViewMode.Missile, "Shift-F10", "Missile", "missile"),
    ];

    /// <summary>The readout's name for a view: its key and its label.</summary>
    /// <param name="mode">The view.</param>
    /// <returns>For example <c>"Shift-F3 External Right"</c>.</returns>
    public static string NameOf(ViewMode mode)
    {
        foreach (ViewEntry entry in All)
        {
            if (entry.Mode == mode)
            {
                return $"{entry.Key} {entry.Label}";
            }
        }

        return mode.ToString();
    }

    /// <summary>Parses a <c>--view</c> word.</summary>
    /// <param name="option">The word, or null.</param>
    /// <returns>The view; <see cref="ViewMode.CockpitForward"/> when the word is unknown.</returns>
    public static ViewMode Parse(string? option)
    {
        string word = (option ?? string.Empty).Trim();
        if (word.Length == 0)
        {
            return ViewMode.CockpitForward;
        }

        // The two spellings H1..H7 shipped stay valid.
        if (string.Equals(word, "external", StringComparison.OrdinalIgnoreCase))
        {
            return ViewMode.ExternalForward;
        }

        foreach (ViewEntry entry in All)
        {
            if (string.Equals(word, entry.Option, StringComparison.OrdinalIgnoreCase)
                || string.Equals(word, entry.Key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Mode;
            }
        }

        return ViewMode.CockpitForward;
    }

    /// <summary>Every <c>--view</c> spelling, for the help text.</summary>
    public static string OptionList =>
        string.Join(" | ", All.Select(e => e.Option));
}
