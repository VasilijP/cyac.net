using CYAC.Port.Core.Data;
using CYAC.Port.Host.Configuration;
using CYAC.Port.Preflight;

namespace CYAC.Port.Host.Startup;

/// <summary>Whether this run opens the window on the pre-flight dashboard, and where its home is.</summary>
/// <param name="ShowsDashboard">True when the window opens on the dashboard.</param>
/// <param name="Layout">The home folder's layout, when one resolved.</param>
/// <param name="Reason">Why the dashboard is not shown, or the rule that chose it.</param>
internal readonly record struct BootPlan(bool ShowsDashboard, PreflightLayout? Layout, string Reason);

/// <summary>
/// The one rule that decides between the boot dashboard and the path the port has always taken.
/// Pure, so it is tested rather than trusted.
/// </summary>
/// <remarks>
/// The dashboard belongs to the WINDOWED DEFAULT path alone.  A run that names its own data tree, a
/// headless run, a replay, a scene re-render and the console check are what they were; so is a run
/// for which no home folder resolves at all, where <c>DataLocator</c>'s own search still finds a
/// tree.  Direct mode (<c>--mission</c>, <c>--test-flight</c>, <c>--custom</c>) does go through the
/// dashboard: the sortie simply starts after Ready instead of the menu.
/// </remarks>
/// <remarks>
/// "no home folder resolves" is now the rare case it should be: the rules end at the executable's
/// own folder and the per-user folder, so a released game always has a home.  It remains reachable
/// — a platform that says nothing about where a user's files live, and a read-only executable
/// folder, leave nothing to resolve.
/// </remarks>
internal static class BootDecision
{
    /// <summary>Why this run cannot use the dashboard, or null when it can.</summary>
    /// <param name="options">The parsed command line.</param>
    public static string? Blocker(FlyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Preflight)
        {
            return "--preflight runs the checks on the console";
        }

        if (options.Headless)
        {
            return "--headless renders frames, not a window";
        }

        if (options.Replay)
        {
            return "--replay verifies the kernel against a trace";
        }

        if (options.RenderScene is not null)
        {
            return "--render-scene re-renders a dump and exits";
        }

        return options.DataPath is not null ? "--data names the tree to use" : null;
    }

    /// <summary>The plan for this run.</summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="resolution">What the home-folder rules resolved.</param>
    public static BootPlan Plan(FlyOptions options, HomeResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (Blocker(options) is { } blocker)
        {
            return new BootPlan(false, resolution.Layout, blocker);
        }

        return resolution.Layout is { } layout
            ? new BootPlan(true, layout, $"home from {resolution.Source}")
            : new BootPlan(false, null, resolution.Error ?? "no home folder");
    }

    /// <summary>
    /// The data tree this run must open itself, when the resolved home already holds one.
    /// </summary>
    /// <remarks>
    /// A run that does not go through the dashboard (<c>--headless</c>, <c>--replay</c>,
    /// <c>--render-scene</c>) still gets the home it was given: the tree the first start installed in
    /// <c>&lt;home&gt;/data</c> is the implicit <c>--data</c> for that run.  It is offered only when
    /// it really is a tree, so a home without one falls back to the search and its message.
    /// </remarks>
    /// <param name="plan">The plan this run is following.</param>
    public static string? ImplicitDataPath(BootPlan plan)
    {
        if (plan is { ShowsDashboard: false, Layout: { } layout }
            && File.Exists(Path.Combine(layout.DataDirectory, DataLocator.ManifestFileName)))
        {
            return layout.DataDirectory;
        }

        return null;
    }
}
