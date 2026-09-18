using System.Text.Json;
using CYAC.Port.Core.Model.Mission;

namespace CYAC.Port.Host.Settings;

/// <summary>
/// The port's OWN configuration file, <c>port.json</c>, with authored defaults.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it replaces.</b>  Until P5 the runtime read two values out of the data tree's
/// <c>config.json</c> — the original's <c>yeager.cfg</c>, transformed.  That file is a SAVED STATE:
/// whatever the last person to play the original left behind, joystick calibration and all.  A port
/// has no business starting from a stranger's saved state, so it now starts from choices it made
/// itself, written here on first run and editable by hand.  The transform still produces the tree's
/// <c>config.json</c>; nothing in the runtime reads it any more.
/// </para>
/// <para>
/// <b>Not to be confused with.</b>  <c>data/config.json</c> is the original's state (read-only, owned
/// by <c>cyac-transform</c>).  <c>settings.json</c> is the PLAYER's choices, written by the in-game
/// settings dialog on every keypress.  This file is the PORT's choices — the starting point both of
/// those sit on top of — which is why it has a name neither of them can be mistaken for.
/// </para>
/// <para>
/// <b>What is deliberately absent.</b> The joystick calibration.  The port has no joystick: the
/// flight kernel uses <c>FlightColdStart.DefaultConfigCalibration</c>, the ±105 the original's
/// <c>input_mode_set</c> installs when nothing is calibrated.  When joystick support lands it can add
/// its own entry here.
/// </para>
/// <para>
/// <b>Reading only.</b>  <see cref="Open"/> never writes: a run that names a data tree directly
/// (<c>--data</c>) must not scatter files around it.  The file is created, with defaults, by the
/// pre-flight User state step, which knows it is working inside a home folder.
/// </para>
/// </remarks>
public sealed class PortConfigStore
{
    /// <summary>The file's own name.</summary>
    public const string FileName = CYAC.Port.Preflight.PreflightLayout.PortConfigFileName;

    /// <summary>What the file's <c>format</c> string says.</summary>
    public const string FormatTag = "cyac.port/1";

    /// <summary>The word <see cref="Missions"/> carries while every mission is open.</summary>
    public const string MissionsAll = "all";

    /// <summary>The <c>$comment</c> the file carries — what it is, and what it is not.</summary>
    public const string SchemaComment =
        "The CYAC port's OWN configuration, with the port's authored defaults. These are the PORT's "
            + "choices, not the original game's saved state: the transformed data tree's config.json "
            + "(the original's yeager.cfg) is never read by the running game. Edit this file by hand "
            + "if you like; delete it and the next start writes it again with the defaults. "
            + "`windows` lists the in-flight overlay windows that start visible - envelope, target, "
            + "map, yeager - and defaults to target+map, which is what the original shipped with. "
            + "`missions` is `all`: every mission is open from the start, because the port keeps no "
            + "campaign progression. There is no joystick entry: the port has no joystick yet, and "
            + "the flight model uses its own built-in centre and extremes. Your own settings live in "
            + "settings.json and your flying record in stats.json, both beside this file.";

    /// <summary>Every overlay window, with the word this file spells it with.</summary>
    public static IReadOnlyList<(string Word, CockpitOverlayFlags Window)> WindowWords { get; } =
    [
        ("envelope", CockpitOverlayFlags.Envelope),
        ("target", CockpitOverlayFlags.Target),
        ("map", CockpitOverlayFlags.Map),
        ("yeager", CockpitOverlayFlags.Yeager),
    ];

    private readonly List<string> _warnings = [];

    private PortConfigStore(string path)
    {
        Path = path;
    }

    /// <summary>
    /// The port's default overlay windows: TARGET and MAP up, the envelope and Yeager windows down.
    /// </summary>
    /// <remarks>
    /// A PORT CHOICE, documented as one — but the same four bits the shipped <c>yeager.cfg@0x1D</c>
    /// carries, and the same four <see cref="PortSettings.ShippedOverlays"/> has always defaulted the
    /// <c>--window-*</c> words to, so dropping the tree's byte changes nothing anybody can see.
    /// </remarks>
    public static CockpitOverlayFlags DefaultWindows => CockpitOverlayFlags.Target | CockpitOverlayFlags.Map;

    /// <summary>The resolved file path — what the readout names.</summary>
    public string Path { get; }

    /// <summary>Whether the file existed and parsed when the run started.</summary>
    public bool FileLoaded { get; private set; }

    /// <summary>Anything wrong with the file — a parse failure, an unknown shape, an unknown word.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Which overlay windows a sortie starts with.</summary>
    public CockpitOverlayFlags Windows { get; private set; } = DefaultWindows;

    /// <summary>
    /// Which missions are open.  Always <see cref="MissionsAll"/>: the port keeps no progression, and
    /// nothing in it consults an unlock array.
    /// </summary>
    public string Missions { get; private set; } = MissionsAll;

    /// <summary>Where <c>port.json</c> goes for a data tree: beside the tree's PARENT directory.</summary>
    /// <param name="dataRoot">The data tree's own root directory.</param>
    /// <param name="over">An explicit path, or null.</param>
    /// <remarks>
    /// The same rule <c>settings.json</c> and <c>stats.json</c> follow, which puts all three in the
    /// home folder whose <c>data/</c> the tree is — and keeps them out of the tree itself, which
    /// <c>cyac-transform</c> owns and regenerates.
    /// </remarks>
    public static string Resolve(string dataRoot, string? over)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        if (!string.IsNullOrWhiteSpace(over))
        {
            string full = System.IO.Path.GetFullPath(over);
            return Directory.Exists(full) ? System.IO.Path.Combine(full, FileName) : full;
        }

        string? parent = Directory.GetParent(System.IO.Path.GetFullPath(dataRoot))?.FullName;
        return System.IO.Path.Combine(parent ?? System.IO.Path.GetFullPath(dataRoot), FileName);
    }

    /// <summary>Opens the store, reading whatever the file holds; an absent file is the defaults.</summary>
    /// <param name="path">The resolved path.</param>
    public static PortConfigStore Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        PortConfigStore store = new PortConfigStore(path);
        store.ReadFile();
        return store;
    }

    /// <summary>Writes the whole file, atomically.</summary>
    public void Save()
    {
        if (WriteTo(Path) is { } trouble)
        {
            _warnings.Add(trouble);
        }
    }

    /// <summary>The words <see cref="Windows"/> is written as, in bit order.</summary>
    /// <param name="windows">The mask.</param>
    public static IReadOnlyList<string> WordsFor(CockpitOverlayFlags windows) =>
        [.. WindowWords.Where(w => windows.HasFlag(w.Window)).Select(w => w.Word)];

    /// <summary>The readout's own line: which file, what it says, and any complaint.</summary>
    public string ReadoutLine()
    {
        string words = Windows == CockpitOverlayFlags.None ? "none" : string.Join("+", WordsFor(Windows));
        string trouble = _warnings.Count > 0 ? $"  ! {_warnings.Count} warning(s)" : string.Empty;
        return $"port   {AtomicJson.Short(Path)} ({(FileLoaded ? "read" : "defaults")}, windows {words}, " +
               $"missions {Missions}){trouble}";
    }

    /// <summary>
    /// Writes the document <see cref="Save"/> would write, but to another path, and changes nothing
    /// else — what the startup check round-trips the loaded state through.
    /// </summary>
    /// <param name="path">Where to write.</param>
    /// <returns>A warning, or null when the write succeeded.</returns>
    internal string? SaveCopy(string path) => WriteTo(path);

    private string? WriteTo(string path) => AtomicJson.Write(path, writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("format", FormatTag);
        writer.WriteString("$comment", SchemaComment);
        writer.WriteStartArray("windows");
        foreach (string word in WordsFor(Windows))
        {
            writer.WriteStringValue(word);
        }

        writer.WriteEndArray();
        writer.WriteString("missions", Missions);
        writer.WriteEndObject();
    });

    private void ReadFile()
    {
        if (!File.Exists(Path))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(Path));
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            // A corrupt file is never fatal: the port's own defaults are a complete answer, and the
            // pre-flight check keeps a .broken copy of whatever was there.
            _warnings.Add($"{Path} is not readable JSON ({error.Message}); the port's defaults are used");
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _warnings.Add($"{Path} is not a JSON object; the port's defaults are used");
                return;
            }

            FileLoaded = true;
            if (root.TryGetProperty("format", out JsonElement format)
                && format.ValueKind == JsonValueKind.String
                && !string.Equals(format.GetString(), FormatTag, StringComparison.Ordinal))
            {
                _warnings.Add(
                    $"{Path} says format '{format.GetString()}'; this build writes {FormatTag} — " +
                    "the values are read as far as they are understood");
            }

            ReadWindows(root);
            ReadMissions(root);
        }
    }

    private void ReadWindows(JsonElement root)
    {
        if (!root.TryGetProperty("windows", out JsonElement windows))
        {
            return;
        }

        if (windows.ValueKind != JsonValueKind.Array)
        {
            _warnings.Add($"{Path}: 'windows' is not a list of words; the default {string.Join("+", WordsFor(DefaultWindows))} is used");
            return;
        }

        CockpitOverlayFlags mask = CockpitOverlayFlags.None;
        foreach (JsonElement element in windows.EnumerateArray())
        {
            string? word = element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() : null;
            (string Word, CockpitOverlayFlags Window) match = WindowWords.FirstOrDefault(w => string.Equals(w.Word, word, StringComparison.OrdinalIgnoreCase));
            if (match.Word is null)
            {
                _warnings.Add(
                    $"{Path}: 'windows' holds '{word ?? element.ValueKind.ToString()}', which is not one of " +
                    $"{string.Join(", ", WindowWords.Select(w => w.Word))}; ignored");
                continue;
            }

            mask |= match.Window;
        }

        Windows = mask;
    }

    private void ReadMissions(JsonElement root)
    {
        if (!root.TryGetProperty("missions", out JsonElement missions))
        {
            return;
        }

        string? word = missions.ValueKind == JsonValueKind.String ? missions.GetString()?.Trim() : null;
        if (string.Equals(word, MissionsAll, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Saying so is the point of the entry: the port has no progression to unlock anything with,
        // so a file that asks for one is told, rather than quietly ignored.
        _warnings.Add(
            $"{Path}: 'missions' says '{word ?? missions.ValueKind.ToString()}'; the port keeps no campaign " +
            $"progression, so every mission stays open ('{MissionsAll}')");
    }
}
