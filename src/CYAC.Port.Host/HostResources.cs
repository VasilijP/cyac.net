namespace CYAC.Port.Host;

/// <summary>
/// Where mode-13hx's own assets live, and how to make its relative paths resolve.
/// </summary>
/// <remarks>
/// mode-13hx loads its bitmap fonts by a path relative to the CURRENT DIRECTORY
/// (<c>Canvas.Font9X16 = new("resources/texture/oldschool_9x16.tga", …)</c>,
/// <c>external/mode-13hx/src/Util/Canvas.cs</c>), and those files are
/// <c>CopyToOutputDirectory</c> items that land beside OUR executable through the project
/// reference.  <c>dotnet run</c> starts a process whose working directory is the caller's, so the
/// font would be missing whenever the host is launched from anywhere but its own output folder.
/// </remarks>
public static class HostResources
{
    /// <summary>The font mode-13hx's <c>Canvas</c> loads first.</summary>
    public const string FontRelativePath = "resources/texture/oldschool_9x16.tga";

    /// <summary>
    /// Makes mode-13hx's relative asset paths resolve, by moving the process to the directory that
    /// holds them when the current one does not.
    /// </summary>
    /// <returns>True when the assets are reachable afterwards.</returns>
    /// <remarks>
    /// Call this only AFTER every user-supplied path has been made absolute — it changes the meaning
    /// of every relative path in the process.
    /// </remarks>
    public static bool EnsureAssetsReachable()
    {
        if (File.Exists(FontRelativePath))
        {
            return true;
        }

        string beside = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(beside, FontRelativePath)))
        {
            Directory.SetCurrentDirectory(beside);
            return true;
        }

        return false;
    }
}
