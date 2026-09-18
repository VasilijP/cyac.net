using System.Text.Encodings.Web;
using System.Text.Json;

namespace CYAC.Port.Host.Settings;

/// <summary>
/// The two things every port-owned JSON file beside the data tree needs: an ATOMIC whole-file write,
/// and the short path the readout prints.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="PortSettingsStore"/> so that <c>settings.json</c> and
/// <c>stats.json</c> cannot drift apart in how they are
/// written: a temp file beside the target and a rename over it, which means a crash mid-write can
/// only ever leave the PREVIOUS complete file behind — never half of the new one.
/// </para>
/// <para>
/// Both files are meant to be READ and hand-edited, so the writer is indented and uses
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> — an apostrophe in a help line stays an
/// apostrophe rather than becoming <c>'</c>.
/// </para>
/// </remarks>
public static class AtomicJson
{
    /// <summary>Writes a whole JSON document through a temp file and a rename.</summary>
    /// <param name="path">The target file.</param>
    /// <param name="body">Writes the document's contents (the root object included).</param>
    /// <returns>A warning to show the player, or null when the write succeeded.</returns>
    /// <remarks>
    /// A directory that cannot be written is a WARNING, never a crash: the game keeps flying with
    /// the values it has, and the readout says the file could not be written.
    /// </remarks>
    public static string? Write(string path, Action<Utf8JsonWriter> body)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(body);
        string temporary = path + ".tmp";
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (FileStream stream = File.Create(temporary))
            using (Utf8JsonWriter writer = new Utf8JsonWriter(
                stream,
                new JsonWriterOptions
                {
                    Indented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }))
            {
                body(writer);
            }

            File.Move(temporary, path, overwrite: true);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
                // The temp file is litter, not a failure; nothing more to do about it.
            }

            return $"could not write {path}: {error.Message}";
        }
    }

    /// <summary>The path relative to the working directory when that is shorter.</summary>
    /// <param name="path">The absolute path.</param>
    public static string Short(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            // A path inside the home folder prints as <home>/… — the readout is drawn into every
            // saved frame, and the folder somebody keeps their game in is nobody else's business.
            if (HomeDirectory is { Length: > 0 } home)
            {
                string fromHome = Path.GetRelativePath(home, path);
                if (!fromHome.StartsWith("..", StringComparison.Ordinal)
                    && !Path.IsPathRooted(fromHome))
                {
                    return "<home>/" + fromHome.Replace(Path.DirectorySeparatorChar, '/');
                }
            }

            string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
            return relative.Length < path.Length && !relative.StartsWith("..", StringComparison.Ordinal)
                ? relative
                : path;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>
    /// The home folder this run resolved, when it has one.  Paths under it are printed as
    /// <c>&lt;home&gt;/…</c> instead of the machine's own absolute path.
    /// </summary>
    public static string? HomeDirectory { get; set; }
}
