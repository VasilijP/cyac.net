using System.Text.Json;

namespace CYAC.Port.Core.Sim.Flight.Trace;

/// <summary>One stage or probe trap the recorder samples, as named in the trace header.</summary>
/// <param name="Id">The id carried in a record's <c>stage</c> byte (probes carry it with bit 7 set).</param>
/// <param name="Name">The trap's name, e.g. <c>"after_throttle_fuel"</c>.</param>
/// <param name="ImageOffset">The <c>image@</c> offset of the trapped instruction.</param>
/// <param name="Kind">
/// <c>"entry"</c> or <c>"exit"</c> for a probe; <see langword="null"/> for a stage.
/// </param>
public readonly record struct FlightTraceTrap(int Id, string Name, int ImageOffset, string? Kind);

/// <summary>One DGROUP window the recorder copies into every record.</summary>
/// <param name="Name"></param>
/// <param name="DgroupOffset">Its offset inside DGROUP — this project's <c>[0x….]</c> notation.</param>
/// <param name="Length">How many bytes of it the record carries.</param>
/// <param name="RecordOffset">Where the window starts inside a record.</param>
public readonly record struct FlightTraceGlobal(
    string Name, int DgroupOffset, int Length, int RecordOffset);

/// <summary>
/// The one-line JSON header of a <c>cyac-flight-trace</c> file (format v1).
/// </summary>
/// <remarks>
/// <para>
/// Implemented from alone — the port never references the emulator that writes these files.  Every layout number a reader needs comes from the header, never from a
/// constant here ("A reader MUST take every layout number from the header"), and unknown keys are
/// ignored so provenance keys can be added inside v1.
/// </para>
/// <para>
/// A trace is only meaningful for the leg that produced it: <see cref="Recording"/>,
/// <see cref="Exe"/>, <see cref="Seed"/>, <see cref="Era"/> and <see cref="Lifts"/> stamp which one.
/// A verification trace is taken <c>--no-lift</c>, because a trap on
/// <c>fme_envelope_status_eval</c>'s own entry warps over its <c>ret 2</c> and collapses
/// probe 0's count to zero.
/// </para>
/// </remarks>
public sealed class FlightTraceHeader
{
    /// <summary>The only <c>format</c> value a reader accepts.</summary>
    public const string FormatName = "cyac-flight-trace";

    /// <summary>The only wire version this reader knows.</summary>
    public const int SupportedVersion = 1;

    private readonly Dictionary<string, int> _fields;
    private readonly Dictionary<int, FlightTraceTrap> _stages;
    private readonly Dictionary<int, FlightTraceTrap> _probes;
    private readonly Dictionary<string, FlightTraceGlobal> _globals;

    private FlightTraceHeader(
        string json,
        Dictionary<string, int> fields,
        Dictionary<int, FlightTraceTrap> stages,
        Dictionary<int, FlightTraceTrap> probes,
        Dictionary<string, FlightTraceGlobal> globals)
    {
        Json = json;
        _fields = fields;
        _stages = stages;
        _probes = probes;
        _globals = globals;
    }

    /// <summary>The header line exactly as it appeared, for provenance in a test's output.</summary>
    public string Json { get; }

    /// <summary>The <c>.evq</c> recording this leg replayed, or null for a non-replay leg.</summary>
    public string? Recording { get; private set; }

    /// <summary>The guest image the leg ran.</summary>
    public string? Exe { get; private set; }

    /// <summary>The determinism seed as it was stamped (a <c>"0x…"</c> string), or null.</summary>
    public string? Seed { get; private set; }

    /// <summary>The kernel era (<c>"det_v1"</c>) or the delivery policy name.</summary>
    public string? Era { get; private set; }

    /// <summary>
    /// The lift roster the leg ran: <c>"none"</c>, <c>"default"</c> or an explicit spec.  A
    /// verification trace is <c>"none"</c> — see the type remarks.
    /// </summary>
    public string? Lifts { get; private set; }

    /// <summary>Bytes of <c>s_aircraft_master</c> in each record (298 in v1).</summary>
    public int MasterLength { get; private set; }

    /// <summary>Bytes of the player world-object block in each record (64 in v1).</summary>
    public int AltLength { get; private set; }

    /// <summary>The record stride in bytes.</summary>
    public int RecordLength { get; private set; }

    /// <summary>The DGROUP paragraph the <c>dgroupOff</c> values are relative to.</summary>
    public int DgroupSegment { get; private set; }

    /// <summary>Whether probe records can appear at all.</summary>
    public bool ProbesEnabled { get; private set; }

    /// <summary>The names of the seven register words at the <c>regs</c> field, in order.</summary>
    public IReadOnlyList<string> RegisterNames { get; private set; } = [];

    /// <summary>The stage table, by id.</summary>
    public IReadOnlyDictionary<int, FlightTraceTrap> Stages => _stages;

    /// <summary>The probe table, by id (the id WITHOUT bit 7).</summary>
    public IReadOnlyDictionary<int, FlightTraceTrap> Probes => _probes;

    /// <summary>The DGROUP windows, by name, each with its offset inside a record.</summary>
    public IReadOnlyDictionary<string, FlightTraceGlobal> Globals => _globals;

    /// <summary>Byte offset of a named record field (<c>step</c>, <c>master</c>, …).</summary>
    /// <param name="name">The field's name in the header's <c>fields</c> object.</param>
    /// <exception cref="InvalidDataException">The header does not carry that field.</exception>
    public int Field(string name) => _fields.TryGetValue(name, out int offset)
        ? offset
        : throw new InvalidDataException(
            $"the flight-trace header has no 'fields.{name}' offset (format v1 defines it)");

    /// <summary>Parses one header line.</summary>
    /// <param name="json">The file's first line, without its terminating newline.</param>
    /// <exception cref="InvalidDataException">
    /// The line is not a v1 <c>cyac-flight-trace</c> header, or a required key is missing.
    /// </exception>
    public static FlightTraceHeader Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        string format = String(root, "format");
        if (!string.Equals(format, FormatName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"not a flight trace: format is '{format}', expected '{FormatName}'");
        }

        int version = Int(root, "version");
        if (version != SupportedVersion)
        {
            throw new InvalidDataException(
                $"flight-trace version {version} is not v{SupportedVersion}; this reader refuses to "
                    + "guess a layout it does not know");
        }

        Dictionary<string, int> fields = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonProperty field in root.GetProperty("fields").EnumerateObject())
        {
            fields[field.Name] = field.Value.GetInt32();
        }

        Dictionary<int, FlightTraceTrap> stages = new Dictionary<int, FlightTraceTrap>();
        foreach (FlightTraceTrap trap in ReadTraps(root, "stages"))
        {
            stages[trap.Id] = trap;
        }

        Dictionary<int, FlightTraceTrap> probes = new Dictionary<int, FlightTraceTrap>();
        foreach (FlightTraceTrap trap in ReadTraps(root, "probes"))
        {
            probes[trap.Id] = trap;
        }

        Dictionary<string, FlightTraceGlobal> globals = new Dictionary<string, FlightTraceGlobal>(StringComparer.Ordinal);
        int cursor = fields.TryGetValue("globals", out int globalsBase) ? globalsBase : 0;
        if (root.TryGetProperty("globals", out JsonElement globalList))
        {
            foreach (JsonElement entry in globalList.EnumerateArray())
            {
                string name = String(entry, "name");
                int length = Int(entry, "len");
                globals[name] = new FlightTraceGlobal(name, Int(entry, "dgroupOff"), length, cursor);
                cursor += length;
            }
        }

        FlightTraceHeader header = new FlightTraceHeader(json, fields, stages, probes, globals)
        {
            Recording = OptionalString(root, "recording"),
            Exe = OptionalString(root, "exe"),
            Seed = OptionalString(root, "seed"),
            Era = OptionalString(root, "era"),
            Lifts = OptionalString(root, "lifts"),
            MasterLength = Int(root, "masterLen"),
            AltLength = Int(root, "altLen"),
            RecordLength = Int(root, "recordLen"),
            DgroupSegment = Int(root, "dgroupSeg"),
            ProbesEnabled = root.TryGetProperty("probes_enabled", out JsonElement enabled)
                && enabled.ValueKind == JsonValueKind.True,
            RegisterNames = ReadStrings(root, "regNames"),
        };

        if (header.RecordLength <= 0)
        {
            throw new InvalidDataException(
                $"flight-trace recordLen is {header.RecordLength}; a record must have a positive stride");
        }

        return header;
    }

    /// <summary>The index of a named register inside a record's register group, or −1.</summary>
    /// <param name="name">One of the header's <c>regNames</c>, e.g. <c>"ax"</c> or <c>"arg0"</c>.</param>
    public int RegisterIndex(string name)
    {
        for (int i = 0; i < RegisterNames.Count; i++)
        {
            if (string.Equals(RegisterNames[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A one-line provenance summary for a test's output.</summary>
    public override string ToString() =>
        $"{FormatName} v{SupportedVersion} recording={Recording} exe={Exe} seed={Seed} era={Era} "
            + $"lifts={Lifts} record={RecordLength}B master={MasterLength}B alt={AltLength}B "
            + $"stages={_stages.Count} probes={_probes.Count} globals={_globals.Count}";

    private static IEnumerable<FlightTraceTrap> ReadTraps(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement list))
        {
            yield break;
        }

        foreach (JsonElement entry in list.EnumerateArray())
        {
            string image = OptionalString(entry, "image") ?? "0";
            yield return new FlightTraceTrap(
                Int(entry, "id"),
                String(entry, "name"),
                ParseImageOffset(image),
                OptionalString(entry, "kind"));
        }
    }

    private static int ParseImageOffset(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(text[2..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture)
            : int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> ReadStrings(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement list))
        {
            return [];
        }

        List<string> values = new List<string>();
        foreach (JsonElement entry in list.EnumerateArray())
        {
            values.Add(entry.GetString() ?? string.Empty);
        }

        return values;
    }

    private static string String(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.GetString() is { } text
            ? text
            : throw new InvalidDataException($"the flight-trace header has no string '{property}'");

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int number)
            ? number
            : throw new InvalidDataException($"the flight-trace header has no number '{property}'");
}
