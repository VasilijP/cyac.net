using CYAC.Port.Host.Configuration;
using CYAC.Port.Host.Settings;
using CYAC.Port.Host.Stats;
using CYAC.Port.Preflight;

namespace CYAC.Port.Host.Startup;

/// <summary>
/// The startup check of one port-owned JSON file, read and written by its own store (step 10).
/// </summary>
/// <remarks>
/// <para>
/// A missing file is created with defaults.  An existing file is opened with the store the game uses,
/// and the loaded state is written to a temporary sibling, read back and written once more; the two
/// copies must be identical.  The file itself is never rewritten by the check.
/// </para>
/// <para>
/// An unparseable file is a warning and a copy is kept as <c>&lt;name&gt;.broken-&lt;UTC stamp&gt;</c>.
/// The file is left in place: the stores do not replace a broken file when they open it — they run on
/// defaults and overwrite it the next time the game saves (a settings change, a sortie start, a sortie
/// end) — and the check does not change that.
/// </para>
/// </remarks>
internal abstract class PortFileCheck : IUserStateCheck
{
    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <summary>The clock for the broken-copy stamp.</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <inheritdoc/>
    public UserStateOutcome Run(PreflightLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        string path = PathIn(layout);
        List<string> details = new List<string>();
        StepState state = StepState.Ok;
        string summary;

        try
        {
            bool existed = File.Exists(path);
            OpenedFile file = Open(path);
            if (!existed)
            {
                if (file.SaveCopy(path) is { } trouble)
                {
                    return new UserStateOutcome(StepState.Failed, "could not be created", [trouble]);
                }

                summary = "created with defaults";
            }
            else if (!file.Loaded)
            {
                string copy = UserStateFiles.KeepBrokenCopy(path, UtcNow());
                state = StepState.Warning;
                summary = "unreadable — defaults in use";
                details.AddRange(file.Warnings);
                details.Add($"kept a copy as {Path.GetFileName(copy)}");
                details.Add("the file is left as it is; the game replaces it the next time it saves");
            }
            else if (file.Warnings.Count > 0)
            {
                state = StepState.Warning;
                summary = $"read with {file.Warnings.Count} warning{(file.Warnings.Count == 1 ? string.Empty : "s")}";
                details.AddRange(file.Warnings);
            }
            else
            {
                summary = "ok";
            }

            if (RoundTrip(path, file) is { } failure)
            {
                details.Add(failure);
                return new UserStateOutcome(StepState.Failed, "does not survive a save and reload", details);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            details.Add(ex.Message);
            return new UserStateOutcome(StepState.Failed, "cannot be checked", details);
        }

        return new UserStateOutcome(state, summary, details);
    }

    /// <summary>The file's path under a layout.</summary>
    /// <param name="layout">The layout.</param>
    protected abstract string PathIn(PreflightLayout layout);

    /// <summary>Opens the file with the game's own store.</summary>
    /// <param name="path">The file.</param>
    protected abstract OpenedFile Open(string path);

    private string? RoundTrip(string path, OpenedFile file)
    {
        string first = UserStateFiles.TemporarySibling(path);
        string second = UserStateFiles.TemporarySibling(path);
        try
        {
            if (file.SaveCopy(first) is { } written)
            {
                return written;
            }

            OpenedFile reread = Open(first);
            if (!reread.Loaded || reread.Warnings.Count > 0)
            {
                return $"the saved state does not read back cleanly: {string.Join("; ", reread.Warnings)}";
            }

            if (reread.SaveCopy(second) is { } rewritten)
            {
                return rewritten;
            }

            int at = UserStateFiles.FirstDifference(File.ReadAllBytes(first), File.ReadAllBytes(second));
            return at < 0 ? null : $"saving the reloaded state differs from the first save at byte {at}";
        }
        finally
        {
            UserStateFiles.DeleteQuietly(first);
            UserStateFiles.DeleteQuietly(second);
        }
    }

    /// <summary>A file as its store opened it.</summary>
    /// <param name="Loaded">Whether the file existed and parsed.</param>
    /// <param name="Warnings">What the store complained about.</param>
    /// <param name="SaveCopy">Writes the loaded state to a path; returns a warning or null.</param>
    protected sealed record OpenedFile(bool Loaded, IReadOnlyList<string> Warnings, Func<string, string?> SaveCopy);
}

/// <summary>The startup check of <c>settings.json</c>, through <see cref="PortSettingsStore"/>.</summary>
internal sealed class SettingsFileCheck : PortFileCheck
{
    /// <inheritdoc/>
    public override string Name => PortSettingsStore.FileName;

    /// <inheritdoc/>
    protected override string PathIn(PreflightLayout layout) => layout.SettingsPath;

    /// <inheritdoc/>
    protected override OpenedFile Open(string path)
    {
        // The store resolves its values into an options object; this one is thrown away.
        PortSettingsStore store = PortSettingsStore.Open(path, new FlyOptions(), Array.Empty<string>());
        return new OpenedFile(store.FileLoaded, store.Warnings, store.SaveCopy);
    }
}

/// <summary>
/// The startup check of the port's own <c>port.json</c>, through <see cref="PortConfigStore"/>.
/// </summary>
/// <remarks>
/// Exactly the contract the other two follow: a missing file is created with the port's authored
/// defaults, an existing one is parsed and round-tripped through a temporary sibling, and a corrupt
/// one becomes <c>port.json.broken-&lt;stamp&gt;</c> while the run continues on the defaults.
/// </remarks>
internal sealed class PortConfigFileCheck : PortFileCheck
{
    /// <inheritdoc/>
    public override string Name => PortConfigStore.FileName;

    /// <inheritdoc/>
    protected override string PathIn(PreflightLayout layout) => layout.PortConfigPath;

    /// <inheritdoc/>
    protected override OpenedFile Open(string path)
    {
        PortConfigStore store = PortConfigStore.Open(path);
        return new OpenedFile(store.FileLoaded, store.Warnings, store.SaveCopy);
    }
}

/// <summary>The startup check of <c>stats.json</c>, through <see cref="PortStatsStore"/>.</summary>
internal sealed class StatsFileCheck : PortFileCheck
{
    /// <inheritdoc/>
    public override string Name => PortStatsStore.FileName;

    /// <inheritdoc/>
    protected override string PathIn(PreflightLayout layout) => layout.StatsPath;

    /// <inheritdoc/>
    protected override OpenedFile Open(string path)
    {
        PortStatsStore store = PortStatsStore.Open(path);
        return new OpenedFile(store.FileLoaded, store.Warnings, store.SaveCopy);
    }
}
