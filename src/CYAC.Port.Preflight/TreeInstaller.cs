namespace CYAC.Port.Preflight;

/// <summary>
/// The atomic swap of a freshly built tree into place, and the recovery of a swap that was interrupted.
/// </summary>
/// <remarks>
/// The swap is two directory renames: <c>data</c> → <c>data.old</c>, then <c>data.tmp</c> → <c>data</c>,
/// then <c>data.old</c> is deleted.  At every point in between, either the previous tree or the new one
/// is complete on disk, and <see cref="Recover"/> reads which from what is left:
/// <list type="bullet">
///   <item><c>data</c> absent and <c>data.old</c> present: the first rename happened, the second did not —
///   the old tree goes back.</item>
///   <item><c>data.tmp</c> present afterwards: a build that never finished (or never got verified) — deleted.</item>
///   <item><c>data.old</c> present beside <c>data</c>: the swap finished, the clean-up did not — deleted.</item>
/// </list>
/// </remarks>
public static class TreeInstaller
{
    /// <summary>Puts the directory layout back into a consistent state after an interrupted run.</summary>
    /// <param name="layout">The layout.</param>
    /// <returns>One line per action taken; empty when there was nothing to do.</returns>
    public static IReadOnlyList<string> Recover(PreflightLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        List<string> actions = new List<string>();
        if (!Directory.Exists(layout.DataDirectory) && Directory.Exists(layout.PreviousDataDirectory))
        {
            Directory.Move(layout.PreviousDataDirectory, layout.DataDirectory);
            actions.Add($"restored the previous tree from {Name(layout.PreviousDataDirectory)} (an install was interrupted)");
        }

        if (Directory.Exists(layout.TemporaryDataDirectory))
        {
            Directory.Delete(layout.TemporaryDataDirectory, recursive: true);
            actions.Add($"removed {Name(layout.TemporaryDataDirectory)} left by an interrupted build");
        }

        if (Directory.Exists(layout.PreviousDataDirectory))
        {
            Directory.Delete(layout.PreviousDataDirectory, recursive: true);
            actions.Add($"removed {Name(layout.PreviousDataDirectory)} left by an interrupted install");
        }

        return actions;
    }

    /// <summary>Swaps <see cref="PreflightLayout.TemporaryDataDirectory"/> in as the installed tree.</summary>
    /// <param name="layout">The layout.</param>
    /// <returns>Lines describing what was done.</returns>
    /// <exception cref="DirectoryNotFoundException">There is no built tree to install.</exception>
    public static IReadOnlyList<string> Install(PreflightLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!Directory.Exists(layout.TemporaryDataDirectory))
        {
            throw new DirectoryNotFoundException($"{layout.TemporaryDataDirectory} does not exist; nothing to install");
        }

        List<string> lines = new List<string>();
        if (Directory.Exists(layout.PreviousDataDirectory))
        {
            Directory.Delete(layout.PreviousDataDirectory, recursive: true);
        }

        bool hadTree = Directory.Exists(layout.DataDirectory);
        if (hadTree)
        {
            Directory.Move(layout.DataDirectory, layout.PreviousDataDirectory);
        }

        Directory.Move(layout.TemporaryDataDirectory, layout.DataDirectory);
        lines.Add($"{Name(layout.TemporaryDataDirectory)} → {Name(layout.DataDirectory)}");

        if (hadTree)
        {
            try
            {
                Directory.Delete(layout.PreviousDataDirectory, recursive: true);
                lines.Add("the previous tree was replaced");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The new tree is in place; the old one is only litter, and the next start removes it.
                lines.Add($"the previous tree could not be deleted yet ({ex.Message}); the next start removes it");
            }
        }

        return lines;
    }

    private static string Name(string path) => Path.GetFileName(path);
}
