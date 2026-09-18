using System.Globalization;
using System.Text.Json;
using CYAC.Port.Host.Settings;

namespace CYAC.Port.Host.Stats;

/// <summary>
/// The port's own <c>stats.json</c>: a SIBLING of <c>settings.json</c> that accumulates what the
/// port keeps instead of a progression — number of plays, count of completions, kills/deaths
/// in that mission, also shots fired and time played" (/08).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate file.</b> Settings are written on a keypress and are
/// small; statistics are written at the end of every sortie and grow without bound — and a corrupt
/// statistics file must never cost a player their settings.  Two files, one atomic writer
/// (<see cref="AtomicJson"/>), one tolerant reader each.
/// </para>
/// <para>
/// <b>Where.</b>  Beside the data tree's PARENT — <c>&lt;data root&gt;/../stats.json</c>, the repo
/// root when the port runs out of a checkout — overridable with <c>--stats &lt;path&gt;</c>, exactly
/// as <c>--settings</c> works.  It is deliberately NOT inside <c>data/</c>, which
/// <c>cyac-transform</c> owns and regenerates.
/// </para>
/// <para>
/// <b>What the key is.</b>  <see cref="MissionIdentity"/> — the <c>.S</c> module's asset name out of
/// <c>data/scenarios.json</c>, with the title, the date and the slot stored beside each record so a
/// re-ordered catalogue orphans nothing and a human can read the file.
/// </para>
/// <para>
/// <b>When it is NOT written.</b> A headless run (<c>--headless</c>, <c>--replay</c>,
/// <c>--frame-count</c>) never writes unless <c>--stats</c> names a file explicitly: the verified
/// replays and test runs must not litter a player's statistics.  Such a run gets a
/// <see cref="Disabled"/> store, which reads nothing and writes nothing but answers every query with
/// an empty record, so nothing else in the host has to know.
/// </para>
/// </remarks>
public sealed class PortStatsStore
{
    /// <summary>The file's own name.</summary>
    public const string FileName = "stats.json";

    /// <summary>What the file's <c>format</c> string says.</summary>
    public const string FormatTag = "cyac-port-stats/1";

    /// <summary>The <c>$comment</c> the file carries — the schema, explained in the file itself.</summary>
    public const string SchemaComment =
        "Per-mission flight statistics for the CYAC port. `missions` is keyed by the mission's own "
            + ".S module asset name (moduleAssetName in data/scenarios.json), which is unique across "
            + "the 50 shipped missions and survives a re-ordered, re-titled or re-dated catalogue; "
            + "`title`, `date` and `slot` are stored beside each record for the reader and are "
            + "refreshed from the catalogue whenever the mission is flown. The Test Flight is a "
            + "mission too and keeps its own record under free.s (slot -1). `totals` is DERIVED — the "
            + "sum of every mission record — and is recomputed on every write, so it can never "
            + "disagree with the parts. Times are simulated seconds (a paused menu counts for "
            + "nothing); timestamps are ISO-8601 local. There is no reset button: delete this file.";

    private readonly Dictionary<string, MissionStatsRecord> _records = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];

    private PortStatsStore(string path, bool enabled, string? disabledReason)
    {
        Path = path;
        Enabled = enabled;
        DisabledReason = disabledReason;
    }

    /// <summary>The resolved file path — what the readout names.</summary>
    public string Path { get; }

    /// <summary>Whether this run reads and writes the file at all.</summary>
    public bool Enabled { get; }

    /// <summary>Why the run is not keeping statistics, when it is not.</summary>
    public string? DisabledReason { get; }

    /// <summary>Whether the file existed and parsed when the run started.</summary>
    public bool FileLoaded { get; private set; }

    /// <summary>How many times the file has been written this run.</summary>
    public int Writes { get; private set; }

    /// <summary>
    /// Bumped on every change, so a reader (the dialog) can tell when its rows are stale without
    /// rebuilding them every frame.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>Anything wrong with the file — a parse failure, an unknown shape, a bad number.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Every mission with a record, in catalogue order (the Test Flight last).</summary>
    public IReadOnlyList<MissionStatsRecord> Records =>
        [.. _records.Values
            .OrderBy(r => r.Mission.Slot < 0 ? int.MaxValue : r.Mission.Slot)
            .ThenBy(r => r.Mission.Key, StringComparer.Ordinal)];

    /// <summary>The sum of every mission record — what the file's <c>totals</c> object holds.</summary>
    public MissionStatsRecord Totals
    {
        get
        {
            MissionStatsRecord totals = new MissionStatsRecord(new MissionIdentity("totals", "All missions", string.Empty, -1));
            foreach (MissionStatsRecord record in _records.Values)
            {
                totals.Merge(record);
            }

            return totals;
        }
    }

    /// <summary>Where <c>stats.json</c> goes for a data tree: beside the tree's PARENT directory.</summary>
    /// <param name="dataRoot">The data tree's own root directory.</param>
    /// <param name="over">The <c>--stats</c> override, or null.</param>
    public static string Resolve(string dataRoot, string? over)
    {
        if (!string.IsNullOrWhiteSpace(over))
        {
            string full = System.IO.Path.GetFullPath(over);
            return Directory.Exists(full) ? System.IO.Path.Combine(full, FileName) : full;
        }

        string? parent = Directory.GetParent(System.IO.Path.GetFullPath(dataRoot))?.FullName;
        return System.IO.Path.Combine(parent ?? System.IO.Path.GetFullPath(dataRoot), FileName);
    }

    /// <summary>Opens the store, reading whatever the file holds.</summary>
    /// <param name="path">The resolved path.</param>
    public static PortStatsStore Open(string path)
    {
        PortStatsStore store = new PortStatsStore(path, enabled: true, disabledReason: null);
        store.ReadFile();
        return store;
    }

    /// <summary>
    /// A store that keeps nothing: a headless run with no explicit <c>--stats</c>.
    /// </summary>
    /// <param name="path">The path it WOULD have used, for the readout.</param>
    /// <param name="reason">Why it is off — the readout prints it.</param>
    public static PortStatsStore Disabled(string path, string reason) =>
        new(path, enabled: false, disabledReason: reason);

    /// <summary>The record for a mission, creating an empty one if this is the first time.</summary>
    /// <param name="mission">The mission's identity, from the catalogue.</param>
    /// <remarks>
    /// The catalogue is authoritative: a record found under an older title, date or slot is
    /// refreshed from the identity handed in, which is what makes a re-ordered catalogue harmless.
    /// </remarks>
    public MissionStatsRecord Record(MissionIdentity mission)
    {
        if (Find(mission) is { } found)
        {
            found.Mission = mission;
            return found;
        }

        MissionStatsRecord fresh = new MissionStatsRecord(mission);
        _records[mission.Key] = fresh;
        return fresh;
    }

    /// <summary>
    /// The record a mission already has, or null — by KEY first, then by title+date.
    /// </summary>
    /// <param name="mission">The identity to look for.</param>
    /// <remarks>
    /// The title+date fallback is what recognises a record whose module was renamed; it is
    /// deliberately not a title-alone match, because two shipped titles are duplicated
    /// ("Ground Attack", "MiGCAP") and a date tells them apart.
    /// </remarks>
    public MissionStatsRecord? Find(MissionIdentity mission)
    {
        if (_records.TryGetValue(mission.Key, out MissionStatsRecord? byKey))
        {
            return byKey;
        }

        if (string.IsNullOrEmpty(mission.Title))
        {
            return null;
        }

        foreach (MissionStatsRecord record in _records.Values)
        {
            if (string.Equals(record.Mission.Title, mission.Title, StringComparison.Ordinal)
                && string.Equals(record.Mission.Date, mission.Date, StringComparison.Ordinal))
            {
                // Re-key it, so the next read finds it directly.
                _records.Remove(record.Mission.Key);
                _records[mission.Key] = record;
                return record;
            }
        }

        return null;
    }

    /// <summary>Writes the whole file, atomically.  A disabled store writes nothing.</summary>
    public void Save()
    {
        Version++;
        if (!Enabled)
        {
            return;
        }

        string? trouble = WriteTo(Path);

        if (trouble is null)
        {
            Writes++;
            return;
        }

        Warn(trouble);
    }

    /// <summary>
    /// Writes the document <see cref="Save"/> would write, but to another path, and changes nothing else:
    /// not the store's own file, not its counters.  The startup check round-trips the loaded state through
    /// such a copy so an existing file is never rewritten just to prove it can be.
    /// </summary>
    /// <param name="path">Where to write.</param>
    /// <returns>A warning, or null when the write succeeded.</returns>
    internal string? SaveCopy(string path) => WriteTo(path);

    private string? WriteTo(string path) => AtomicJson.Write(path, writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("format", FormatTag);
        writer.WriteString("$comment", SchemaComment);
        WriteCounters(writer, "totals", Totals, identity: false);
        writer.WriteStartObject("missions");
        foreach (MissionStatsRecord record in Records)
        {
            WriteCounters(writer, record.Mission.Key, record, identity: true);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    /// <summary>The readout's own stats line: which file, what it holds, and any complaint.</summary>
    public string ReadoutLine()
    {
        if (!Enabled)
        {
            return $"stats  off - {DisabledReason} (would be {AtomicJson.Short(Path)})";
        }

        MissionStatsRecord totals = Totals;
        string trouble = _warnings.Count > 0
            ? string.Create(CultureInfo.InvariantCulture, $"  ! {_warnings.Count} warning(s)")
            : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"stats  {AtomicJson.Short(Path)} ({(FileLoaded || Writes > 0 ? $"{_records.Count} mission(s)" : "no file yet")}"
                + $", {totals.Plays} play(s), {totals.Completions} accomplished, {totals.Kills} kill(s), "
                + $"{MissionStatsRecord.Clock(totals.SecondsPlayed)} flown){trouble}");
    }

    /// <summary>One line per mission — the census a headless run can print.</summary>
    public IEnumerable<string> CensusLines()
    {
        yield return string.Create(
            CultureInfo.InvariantCulture,
            $"── {"TOTALS",-28} {Line(Totals)}");
        foreach (MissionStatsRecord record in Records)
        {
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"   {Truncate(record.Mission.DisplayName, 28),-28} {Line(record)}  [{record.Mission.Key}]");
        }
    }

    private static string Truncate(string text, int columns) =>
        text.Length <= columns ? text : text[..columns];

    private static string Line(MissionStatsRecord r) => string.Create(
        CultureInfo.InvariantCulture,
        $"plays {r.Plays,4}  done {r.Completions,4}  failed {r.Failures,4}  left {r.Abandoned,4}  "
            + $"kills {r.Kills,4}  deaths {r.Deaths,4}  land {r.Landings,3}  "
            + $"rounds {r.RoundsFired,6}/{r.RoundsOnTarget,-6} time {MissionStatsRecord.Clock(r.SecondsPlayed),8}"
            + $"{(r.BestSortieSeconds is { } best ? $"  best {MissionStatsRecord.Clock(best)}" : string.Empty)}");

    private static void WriteCounters(
        Utf8JsonWriter writer, string name, MissionStatsRecord record, bool identity)
    {
        writer.WriteStartObject(name);
        if (identity)
        {
            writer.WriteString("title", record.Mission.Title);
            writer.WriteString("date", record.Mission.Date);
            writer.WriteNumber("slot", record.Mission.Slot);
        }

        writer.WriteNumber("plays", record.Plays);
        writer.WriteNumber("completions", record.Completions);
        writer.WriteNumber("failures", record.Failures);
        writer.WriteNumber("abandoned", record.Abandoned);
        writer.WriteNumber("kills", record.Kills);
        writer.WriteStartObject("deaths");
        writer.WriteNumber("shotDown", record.DeathsShotDown);
        writer.WriteNumber("crashed", record.DeathsCrashed);
        writer.WriteNumber("rammed", record.DeathsRammed);
        writer.WriteNumber("ejected", record.DeathsEjected);
        writer.WriteEndObject();
        writer.WriteNumber("landings", record.Landings);
        writer.WriteNumber("roundsFired", record.RoundsFired);
        writer.WriteNumber("roundsOnTarget", record.RoundsOnTarget);
        writer.WriteNumber("secondsPlayed", Math.Round(record.SecondsPlayed, 1));
        if (record.FirstPlayed is { } first)
        {
            writer.WriteString("firstPlayed", first);
        }

        if (record.LastPlayed is { } last)
        {
            writer.WriteString("lastPlayed", last);
        }

        if (record.BestSortieSeconds is { } best)
        {
            writer.WriteNumber("bestSortieSeconds", Math.Round(best, 1));
        }
        else
        {
            writer.WriteNull("bestSortieSeconds");
        }

        writer.WriteEndObject();
    }

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
            // A CORRUPT stats file is never fatal, and — because it is its own file — it can never
            // cost a player their settings.  The run starts from empty statistics and says so.
            Warn($"{Path} is not readable JSON ({error.Message}); the statistics start empty");
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Warn($"{Path} is not a JSON object; the statistics start empty");
                return;
            }

            FileLoaded = true;
            if (!document.RootElement.TryGetProperty("missions", out JsonElement missions)
                || missions.ValueKind != JsonValueKind.Object)
            {
                Warn($"{Path} carries no \"missions\" object; the statistics start empty");
                return;
            }

            foreach (JsonProperty property in missions.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    Warn($"{Path}: '{property.Name}' is not a mission record; ignored");
                    continue;
                }

                string key = MissionIdentity.Normalise(property.Name);
                MissionIdentity identity = new MissionIdentity(
                    key,
                    Text(property.Value, "title"),
                    Text(property.Value, "date"),
                    (int)Number(property.Value, "slot", MissionIdentity.TestFlightSlot));
                MissionStatsRecord record = new MissionStatsRecord(identity)
                {
                    Plays = (int)Number(property.Value, "plays", 0),
                    Completions = (int)Number(property.Value, "completions", 0),
                    Failures = (int)Number(property.Value, "failures", 0),
                    Abandoned = (int)Number(property.Value, "abandoned", 0),
                    Kills = (int)Number(property.Value, "kills", 0),
                    Landings = (int)Number(property.Value, "landings", 0),
                    RoundsFired = (long)Number(property.Value, "roundsFired", 0),
                    RoundsOnTarget = (long)Number(property.Value, "roundsOnTarget", 0),
                    SecondsPlayed = Number(property.Value, "secondsPlayed", 0),
                    FirstPlayed = Text(property.Value, "firstPlayed") is { Length: > 0 } f ? f : null,
                    LastPlayed = Text(property.Value, "lastPlayed") is { Length: > 0 } l ? l : null,
                };

                if (property.Value.TryGetProperty("bestSortieSeconds", out JsonElement best)
                    && best.ValueKind == JsonValueKind.Number
                    && best.TryGetDouble(out double bestSeconds))
                {
                    record.BestSortieSeconds = bestSeconds;
                }

                if (property.Value.TryGetProperty("deaths", out JsonElement deaths)
                    && deaths.ValueKind == JsonValueKind.Object)
                {
                    record.DeathsShotDown = (int)Number(deaths, "shotDown", 0);
                    record.DeathsCrashed = (int)Number(deaths, "crashed", 0);
                    record.DeathsRammed = (int)Number(deaths, "rammed", 0);
                    record.DeathsEjected = (int)Number(deaths, "ejected", 0);
                }

                _records[key] = record;
            }
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double Number(JsonElement element, string name, double fallback) =>
        element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double number)
                ? number
                : fallback;

    private void Warn(string message) => _warnings.Add(message);
}
