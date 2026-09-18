namespace CYAC.Port.Preflight;

/// <summary>Which platform's per-user convention the last home rule follows.</summary>
public enum HomePlatform
{
    /// <summary><c>%LOCALAPPDATA%\CYAC</c>.</summary>
    Windows,

    /// <summary><c>~/Library/Application Support/CYAC</c>.</summary>
    MacOS,

    /// <summary><c>$XDG_DATA_HOME/cyac</c>, else <c>~/.local/share/cyac</c>.</summary>
    Linux,
}

/// <summary>
/// What the home rules are allowed to look at: the environment, whether a folder can be written,
/// and which platform's per-user convention applies.
/// </summary>
/// <remarks>
/// All three are injected so a test can exercise every rule — including the per-user fallback and a
/// read-only executable folder — without touching the developer's real per-user folder and without
/// depending on the machine it runs on.
/// </remarks>
public sealed record HomeRules
{
    /// <summary>The real environment, the real filesystem, this platform.</summary>
    public static HomeRules Default { get; } = new();

    /// <summary>Reads an environment variable (<c>HOME</c>, <c>LOCALAPPDATA</c>, <c>XDG_DATA_HOME</c>).</summary>
    public Func<string, string?> Variables { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>Whether a folder that exists can be written to.</summary>
    public Func<string, bool> IsWritable { get; init; } = PreflightLayout.IsDirectoryWritable;

    /// <summary>Whose per-user convention rule 5 follows.</summary>
    public HomePlatform Platform { get; init; } = CurrentPlatform;

    /// <summary>The platform this process runs on.</summary>
    public static HomePlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? HomePlatform.Windows
        : OperatingSystem.IsMacOS() ? HomePlatform.MacOS
        : HomePlatform.Linux;
}

/// <summary>Where the port keeps everything, relative to one home folder.</summary>
/// <remarks>
/// <para>
/// One folder holds the lot: the drop zone the player puts the game into, the data tree built from
/// it, the player's own files, the screenshots and the pre-flight log.  A development checkout IS
/// such a folder — its <c>sources/</c> is the drop zone and <c>data/</c> the tree — which is why
/// the same five rules serve a checkout and a released game.
/// </para>
/// <para>
/// The release rules landed: the executable's own folder when it is writable (a portable install:
/// the game unzipped anywhere), and the per-user folder as the last resort.
/// </para>
/// </remarks>
public sealed record PreflightLayout
{
    /// <summary>The drop zone a player puts the game files (or their zip) into.</summary>
    public const string GameFolder = "game";

    /// <summary>The second drop zone, the name a development checkout already uses.</summary>
    public const string SourcesFolder = "sources";

    /// <summary>The installed data tree's folder name.</summary>
    public const string DataFolder = "data";

    /// <summary>Where F12 writes, unless <c>--shot-dir</c> says otherwise.</summary>
    public const string ScreenshotsFolder = "screenshots";

    /// <summary>The port's own configuration file — never the data tree's <c>config.json</c>.</summary>
    public const string PortConfigFileName = "port.json";

    /// <summary>What every run appends its result to.</summary>
    public const string LogFileName = "preflight.log";

    /// <summary>The environment variable that names a home folder.</summary>
    public const string HomeEnvironmentVariable = "CYAC_HOME";

    /// <summary>The per-user folder's name on Windows and macOS.</summary>
    public const string PerUserFolderName = "CYAC";

    /// <summary>The per-user folder's name under the XDG convention, where lower case is the habit.</summary>
    public const string PerUserFolderNameXdg = "cyac";

    /// <summary>Rule 1: the <c>--home</c> value.</summary>
    public const string ExplicitSource = "--home";

    /// <summary>Rule 3: a folder above the executable that already holds a drop zone.</summary>
    public const string CheckoutSource = "development checkout";

    /// <summary>Rule 4: the executable's own folder, writable (a portable install).</summary>
    public const string ExecutableFolderSource = "the executable's folder";

    /// <summary>Rule 5: the platform's per-user folder.</summary>
    public const string PerUserSource = "the per-user folder";

    /// <summary>Creates the layout of one home folder.</summary>
    /// <param name="home">The home folder; made absolute.</param>
    public PreflightLayout(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        // Trimmed: rule 4 hands over AppContext.BaseDirectory, which ends in a separator, and a
        // trailing one would show up in every message and defeat the "relative to home" display. A
        // root path ("/", "C:\") keeps its separator — TrimEndingDirectorySeparator says so.
        Home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
    }

    /// <summary>The home folder, absolute.</summary>
    public string Home { get; }

    /// <summary>Both drop zones, <c>game/</c> first; either may be absent.</summary>
    public IReadOnlyList<string> DropZones => [Path.Combine(Home, GameFolder), Path.Combine(Home, SourcesFolder)];

    /// <summary>The installed data tree.</summary>
    public string DataDirectory => Path.Combine(Home, DataFolder);

    /// <summary>Where a new tree is built before it is installed.</summary>
    public string TemporaryDataDirectory => Path.Combine(Home, DataFolder + ".tmp");

    /// <summary>Where the previous tree waits during the swap.</summary>
    public string PreviousDataDirectory => Path.Combine(Home, DataFolder + ".old");

    /// <summary>The port's settings file.</summary>
    public string SettingsPath => Path.Combine(Home, "settings.json");

    /// <summary>The port's statistics file.</summary>
    public string StatsPath => Path.Combine(Home, "stats.json");

    /// <summary>
    /// The port's OWN configuration, with authored defaults.  Not the data tree's
    /// <c>config.json</c>, which is the original's <c>yeager.cfg</c> transformed and which the
    /// runtime no longer reads.
    /// </summary>
    public string PortConfigPath => Path.Combine(Home, PortConfigFileName);

    /// <summary>Where F12 writes its screenshots.</summary>
    public string ScreenshotsDirectory => Path.Combine(Home, ScreenshotsFolder);

    /// <summary>The log every pre-flight run appends its result to.</summary>
    public string LogPath => Path.Combine(Home, LogFileName);

    /// <summary>
    /// Resolves the home folder, first match wins: <c>--home</c>, <see cref="HomeEnvironmentVariable"/>,
    /// the development walk-up, the executable's own folder when it is writable, and the per-user folder.
    /// </summary>
    /// <param name="explicitHome">The <c>--home</c> value, or null.</param>
    /// <param name="environmentHome">The environment variable's value, or null.</param>
    /// <param name="startDirectory">The executable's folder: where the walk-up starts and rule 4's candidate.</param>
    /// <param name="rules">What the rules may look at; <see cref="HomeRules.Default"/> when null.</param>
    /// <remarks>
    /// <para>
    /// Nothing is created here — a resolution is an answer, not an installation; the pre-flight Home
    /// step creates the folder it is given.  Rule 4 does write (and delete) a probe file in the
    /// executable's folder, because "writable" cannot be decided any other way on either platform.
    /// </para>
    /// <para>
    /// Rules 1 to 3 answer whether or not the folder can be written: an explicit answer that turns
    /// out to be read-only must be reported, not silently replaced by another folder.  The Home step
    /// therefore fails on it, and names <see cref="HomeResolution.PerUserHome"/> as the way out.
    /// </para>
    /// </remarks>
    public static HomeResolution Resolve(
        string? explicitHome, string? environmentHome, string startDirectory, HomeRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(startDirectory);
        HomeRules rule = rules ?? HomeRules.Default;
        string? perUser = PerUserHome(rule);

        if (!string.IsNullOrWhiteSpace(explicitHome))
        {
            return Resolved(explicitHome, ExplicitSource);
        }

        if (!string.IsNullOrWhiteSpace(environmentHome))
        {
            return Resolved(environmentHome, HomeEnvironmentVariable);
        }

        // Rule 3 — a folder ABOVE the executable that already holds a drop zone: a checkout, where
        // the build output sits several folders below the home.  The executable's OWN folder holding
        // one is not a checkout but a portable install, which is rule 4's case and its writability
        // test; saying "development checkout" there would name the wrong rule for the right folder.
        if (FindDevelopmentHome(startDirectory) is { } found && !SamePath(found, startDirectory))
        {
            return Resolved(found, CheckoutSource);
        }

        // Rule 4 — a portable install: the game unzipped into a folder of the player's choosing,
        // which keeps everything it owns inside that folder.  A read-only folder (a disk image, an
        // installation under Program Files, /Applications) is not one, and falls through.
        if (rule.IsWritable(startDirectory))
        {
            return Resolved(startDirectory, ExecutableFolderSource);
        }

        // Rule 5 — the per-user folder, the answer for an installed game.  It does not have to
        // exist yet: the Home step creates it.
        if (perUser is not null)
        {
            return Resolved(perUser, PerUserSource);
        }

        return new HomeResolution(
            null,
            "none",
            $"no home folder: pass --home <dir>, set {HomeEnvironmentVariable}, or run the game from a " +
            $"writable folder ({startDirectory} is not one, and this user has no per-user folder — " +
            $"{PerUserVariableName(rule.Platform)} is not set).")
        {
            PerUserHome = null,
        };

        HomeResolution Resolved(string home, string source) =>
            new(new PreflightLayout(home), source, null) { PerUserHome = perUser };
    }

    /// <summary>
    /// Rule 5's folder: where this platform keeps a program's per-user files.
    /// </summary>
    /// <param name="rules">The environment and platform to answer for.</param>
    /// <returns>An absolute path, or null when the environment does not say where the user's files live.</returns>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><b>Windows</b>: <c>%LOCALAPPDATA%\CYAC</c> — local, not roaming: the data tree is
    ///   hundreds of files rebuilt from the player's own copy of the game, and nothing here should
    ///   travel over a network profile.</item>
    ///   <item><b>macOS</b>: <c>~/Library/Application Support/CYAC</c>, the platform's own place for
    ///   exactly this.</item>
    ///   <item><b>Linux and the rest</b>: <c>$XDG_DATA_HOME/cyac</c> when that is set to an absolute
    ///   path (the specification says a relative one is ignored), else <c>~/.local/share/cyac</c>.
    ///   <c>XDG_DATA_HOME</c> and not <c>XDG_CONFIG_HOME</c>, because the home folder is mostly the
    ///   data tree and the drop zone; the port's small configuration file lives with them rather
    ///   than splitting one home in two.</item>
    /// </list>
    /// </remarks>
    public static string? PerUserHome(HomeRules? rules = null)
    {
        HomeRules rule = rules ?? HomeRules.Default;
        switch (rule.Platform)
        {
            case HomePlatform.Windows:
                return Under(rule.Variables("LOCALAPPDATA"), PerUserFolderName);

            case HomePlatform.MacOS:
                string? library = Under(rule.Variables("HOME"), "Library");
                return library is null ? null : Path.Combine(library, "Application Support", PerUserFolderName);

            default:
                return Under(rule.Variables("XDG_DATA_HOME"), PerUserFolderNameXdg)
                    ?? Under(Under(rule.Variables("HOME"), ".local"), "share", PerUserFolderNameXdg);
        }

        static string? Under(string? root, params string[] parts) =>
            string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root)
                ? null
                : Path.Combine([root, .. parts]);
    }

    /// <summary>
    /// Whether a folder that already exists can be written to, decided by writing a probe file.
    /// </summary>
    /// <param name="directory">The folder.</param>
    /// <remarks>
    /// A folder that does not exist is NOT writable for rule 4's purposes: the executable's folder
    /// always exists, and inventing one would turn a mistyped path into a silent new install.
    /// </remarks>
    public static bool IsDirectoryWritable(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (!Directory.Exists(directory))
        {
            return false;
        }

        string probe = Path.Combine(directory, $".cyac-home-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using FileStream stream = new FileStream(
                probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0x43);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Whether two paths name the same folder, under this platform's file-name comparison.</summary>
    /// <param name="a">One path.</param>
    /// <param name="b">The other.</param>
    private static bool SamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>The environment variable rule 5 needs, named so a failure can say what is missing.</summary>
    /// <param name="platform">The platform.</param>
    public static string PerUserVariableName(HomePlatform platform) => platform switch
    {
        HomePlatform.Windows => "LOCALAPPDATA",
        HomePlatform.MacOS => "HOME",
        _ => "XDG_DATA_HOME or HOME",
    };

    /// <summary>
    /// The first folder at or above <paramref name="startDirectory"/> that contains a <c>game/</c> or
    /// <c>sources/</c> folder, or null.
    /// </summary>
    /// <param name="startDirectory">Where to start.</param>
    public static string? FindDevelopmentHome(string startDirectory)
    {
        ArgumentNullException.ThrowIfNull(startDirectory);
        for (DirectoryInfo? directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, GameFolder))
                || Directory.Exists(Path.Combine(directory.FullName, SourcesFolder)))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}

/// <summary>The outcome of resolving a home folder.</summary>
/// <param name="Layout">The layout, or null when nothing resolved.</param>
/// <param name="Source">
/// Which of the five rules resolved it: <c>--home</c>, <c>CYAC_HOME</c>, the development checkout,
/// the executable's folder or the per-user folder.  Reported in the dashboard's Home row, in the
/// console reporter's Home line and in <c>preflight.log</c>.
/// </param>
/// <param name="Error">What to tell a person when nothing resolved.</param>
public sealed record HomeResolution(PreflightLayout? Layout, string Source, string? Error)
{
    /// <summary>
    /// Rule 5's folder, whichever rule actually answered: the fallback a failure names when the
    /// home it resolved cannot be written to.  Null when this platform does not offer one.
    /// </summary>
    public string? PerUserHome { get; init; }
}
