using System.Text.Json.Nodes;
using CYAC.Formats.EaLib;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// The <c>.S</c> mission modules and the <c>.W</c> theater catalogs → one JSON document each: the
/// authored content of the container, the AI scripts as readable steps, and — for a <c>.S</c> — the
/// trailer module's win rules.
/// </summary>
/// <remarks>
/// <para>
/// The container grammar is <see cref="SMissionDecoder"/> (a transcription of the engine's own tag
/// interpreter <c>wld_or_s_asset_parser @image@0x096F2</c>); the authoring model on top of it is
/// <see cref="SDataModel"/>, whose synthesizer derives every framing field from content, so the
/// document holds mission CONTENT and can never carry a stale offset.  Both halves are proven
/// byte-exact over all 54 shipping containers by their own self-tests, and this transform re-proves
/// it per asset before it writes anything (<c>Forward</c> rebuilds from the document it just wrote
/// and compares).
/// </para>
/// <para>
/// <b>Two supersets of that model live in the emitted <c>data</c> section</b>, both reversible and
/// both documented in the file's own <c>about</c>: an <c>ai_script</c> attribute carries a structured
/// <c>script</c> (<see cref="AiScriptModel"/>, an ordered list of <c>[delay, opcode, operands]</c>
/// steps) instead of the raw <c>hex</c> whenever the script round-trips through
/// <see cref="AiScriptSynthesizer"/>, and every object keeps its derived <c>_</c>-annotations.  Pass
/// <c>hex</c> and it is taken verbatim, so a document the model itself produced is still accepted.
/// </para>
/// <para>
/// <b>Law L6 — code is not data.</b>  The trailer module is native x86 (the mission's win rules,
/// briefing and debrief text).  It stays in <c>data.trailer[].module_hex</c> exactly as it ships;
/// what this transform adds is its SEMANTIC form: the five exports, the briefing text, the win-rule
/// rule lines, the named editable immediates (which the inverse writes back into the code, so
/// editing a win threshold in JSON really changes the module), and — for a bespoke module — the
/// statement IR.
/// </para>
/// </remarks>
public sealed class MissionTransform : IFamilyTransform
{
    private readonly bool _world;

    /// <summary>Creates the transform for one of the two container flavours.</summary>
    /// <param name="world">True for the <c>.W</c> theater catalogs, false for the <c>.S</c> missions.</param>
    public MissionTransform(bool world) => _world = world;

    /// <summary>The folder <c>.S</c> missions land in.</summary>
    public const string MissionFolder = "missions";

    /// <summary>The folder <c>.W</c> theaters land in.</summary>
    public const string WorldFolder = "world";

    /// <summary>The authoring vocabulary the mission documents are written in.</summary>
    public const string VocabularyPath = "missions/_vocabulary.json";

    /// <inheritdoc/>
    public string Family => _world ? "w" : "s";

    /// <inheritdoc/>
    public string TreeDescription => _world
        ? "`world/<name>.json` — a theater catalog (.W): the site table a mission's \"at a random " +
          "site of type N\" placement draws from, and the scenery instances that make up the map. " +
          "Site types are 1 generic, 2 landing-zone, 6 engagement-zone."
        : "`missions/<name>.json` — one mission (.S): its sites, its named strings, the ordered " +
          "stream of directives and placed objects (aircraft, waypoints, markers) with their AI " +
          "scripts as readable steps, and its win rules. This is the file to edit to change what a " +
          "mission is.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is not null
            && source.Extension == (_world ? ".w" : ".s")
            && source.Content.Length >= 8
            && source.Content.Span[..4].SequenceEqual(SMissionDecoder.Magic);
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        byte[] body = source.Content.ToArray();
        SMissionDecoder.SFile parsed;
        SDataModel model;
        try
        {
            parsed = SMissionDecoder.Parse(body, source.Name);
            model = SDataSynthesizer.Extract(parsed, source.Name);
        }
        catch (Exception ex) when (ex is SMissionDecoder.ParseException or SDataException)
        {
            throw new InvalidDataException($"{source}: {ex.Message}");
        }

        JsonObject document = new JsonObject
        {
            ["format"] = "cyac.mission/1",
            ["about"] = About(),
            ["source"] = $"{source.OriginFile}/{source.Name}",
            ["assetName"] = source.Name,
            ["kind"] = _world ? "theater" : "mission",
        };

        OriginalNames names = OriginalNames.For(context);
        JsonObject data = model.ToJsonNode(names.ClassName);
        int scripts = 0;
        int scriptsStructured = 0;
        MissionScripts.Structure(data, ref scripts, ref scriptsStructured);
        Annotate(data, names);
        if (_world)
        {
            document["_siteCensus"] = SiteCensus(model);
        }

        document["data"] = data;

        int unknownBytes = 0;
        if (!_world)
        {
            document["module"] = MissionModuleSection.Build(parsed, source.Name, out unknownBytes);
        }

        List<TransformOutput> outputs = new List<TransformOutput>(2);
        if (!context.IsAllocated(VocabularyPath))
        {
            // Once per run, from the first container claimed: the id/tag NAME tables every document
            // in these two folders is written in.  The tree has to be self-describing for a modder,
            // and a vocabulary the runtime carries privately is a vocabulary that drifts.
            outputs.Add(TransformOutput.View(
                context.Allocate(VocabularyPath),
                MissionVocabulary.Document(names),
                "the mission and theater documents it names the tags of"));
        }

        byte[] json = System.Text.Encoding.UTF8.GetBytes(document.ToJsonString(SDataModel.JsonOptions));

        // Law L3, per asset and before anything is written: rebuild from the document just composed
        // and demand the original bytes back.  A family that cannot prove itself here declines, and
        // the runner carries the asset raw rather than shipping an unverifiable claim.
        byte[] rebuilt = ToBytes(json);
        if (!rebuilt.AsSpan().SequenceEqual(body))
        {
            throw new InvalidDataException(
                $"{source}: the document does not rebuild the asset " +
                $"({rebuilt.Length} B vs {body.Length} B, first difference at " +
                $"0x{FirstDifference(body, rebuilt):X})");
        }

        string folder = _world ? WorldFolder : MissionFolder;
        string path = context.Allocate($"{folder}/{TransformContext.SafeFileName(source.Stem)}.json");
        string note = _world
            ? $"{model.Sites.Count} sites, {model.Objects.Count()} scenery instances"
            : $"{model.Objects.Count()} objects, {scriptsStructured}/{scripts} AI scripts structured" +
              (parsed.Module is null ? ", no module" : $", module {parsed.Module.Bytes.Length} B (code)");

        outputs.Add(
            new TransformOutput(path, json, OutputRole.Data, OutputFidelity.Exact, note, unknownBytes));
        return outputs;
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput json = outputs.SingleOrDefault(o => o.Role == OutputRole.Data)
                            ?? throw new InvalidDataException("a mission expects exactly one data output");
        return ToBytes(json.Bytes);
    }

    /// <summary>Rebuilds a <c>.S</c> / <c>.W</c> body from its data-tree document.</summary>
    /// <param name="json">The <c>missions/&lt;name&gt;.json</c> (or <c>world/…</c>) bytes.</param>
    /// <exception cref="InvalidDataException">The document is malformed or its content is not synthesizable.</exception>
    public static byte[] ToBytes(ReadOnlySpan<byte> json)
    {
        JsonObject document = JsonNode.Parse(json.ToArray()) as JsonObject
                              ?? throw new InvalidDataException("the mission document is not a JSON object");
        JsonObject data = document["data"] as JsonObject
                          ?? throw new InvalidDataException("the mission document has no \"data\" section");

        JsonObject raw = JsonNode.Parse(data.ToJsonString()) as JsonObject
                         ?? throw new InvalidDataException("the \"data\" section could not be re-read");
        MissionScripts.Flatten(raw);

        SDataModel model;
        try
        {
            model = SDataModel.FromJsonNode(raw);
        }
        catch (SDataException ex)
        {
            throw new InvalidDataException($"the \"data\" section is not a mission model: {ex.Message}");
        }

        MissionModuleSection.ApplyWinRuleParameters(model, document["module"] as JsonObject);

        try
        {
            return SDataSynthesizer.Synth(model);
        }
        catch (Exception ex) when (ex is SDataException or AiScriptException)
        {
            throw new InvalidDataException($"the mission does not synthesize: {ex.Message}");
        }
    }

    private string About() => _world
        ? "A theater catalog (.W). `data.sites` is the site table a mission's `at_site` placement " +
          "draws from — the engine picks a RANDOM unvisited site of the requested type, so a " +
          "mission names a type, never a site. `data.stream` places the scenery meshes that make " +
          "the map; every one uses the `ground` position (x, z in world units) because CYAC's " +
          "terrain is a flat plane with named meshes on it. Site SEMANTICS beyond the three " +
          "populated types (1 generic, 2 landing-zone, 6 engagement-zone) are still open."
        : "One mission (.S). `data` is the authoring model of the container: `sites`, the interned " +
          "`names`, and `stream` — an ORDERED list of directives and placed objects where order is " +
          "semantic (a placement relative to another object, or a script referring to a place, may " +
          "only look BACKWARD). An object closes with its `pos`; `actor_slot` is the scarce " +
          "resource (0..12) that win rules and scripts address. An `ai_script` attribute carries " +
          "`script.steps` — [delay, opcode, operands] in engagement-VM order — and `_disasm` beside " +
          "it for reading; pass raw `hex` instead and it is used verbatim. `module` describes the " +
          "trailer's native x86 win-rule module: its code stays in `data.trailer[].module_hex`, and " +
          "the only editable things there are `module.winRules.params[].value`, which are written " +
          "back into the code on export. Fields prefixed `_` are derived and ignored on import.";

    /// <summary>
    /// The site-type roles the mission editor's catalog names (<c>MissionDesign.SiteRole</c>): a
    /// <c>.S</c> asks for a TYPE and the engine draws a random unvisited site of it.
    /// </summary>
    /// <param name="type">A section-1 record type.</param>
    public static string SiteRole(int type) => type switch
    {
        1 => "generic",
        2 => "landing-zone",
        6 => "engagement-zone",
        _ => $"type-{type} (no shipped theater populates it)",
    };

    private static JsonObject SiteCensus(SDataModel model)
    {
        JsonObject census = new JsonObject();
        foreach (IGrouping<int, SDataSite> group in model.Sites.GroupBy(s => s.Type).OrderBy(g => g.Key))
        {
            census[SiteRole(group.Key)] = group.Count();
        }

        return census;
    }

    private static void Annotate(JsonObject data, OriginalNames names)
    {
        if (data["sites"] is JsonArray sites)
        {
            foreach (JsonNode? site in sites)
            {
                if (site is JsonObject o && o["type"] is { } type)
                {
                    o["_role"] = SiteRole((int)type);
                }
            }
        }

        if (data["stream"] is not JsonArray stream)
        {
            return;
        }

        // The model annotates the aircraft class ids only (names.ClassName, right after `class`); the
        // theaters place the SCENERY prototypes (ids 26..45), whose names come from the mesh registry
        // the class table points into.
        foreach (JsonNode? item in stream)
        {
            if (item is JsonObject o
                && o["object"]?.GetValue<string>() is { } kind
                && o["class"] is { } id
                && o["_class_name"] is null)
            {
                o["_class_name"] = names.DescribeClass(kind, (int)id);
            }
        }
    }

    private static int FirstDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return n;
    }
}
