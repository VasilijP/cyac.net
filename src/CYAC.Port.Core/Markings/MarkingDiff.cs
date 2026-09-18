using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CYAC.Port.Core.Markings;

/// <summary>What a diff was made against.</summary>
/// <param name="Generator">The generator id (<see cref="MarkingBootstrap.Generator"/>).</param>
/// <param name="Sha256">The SHA-256 of the base set's <see cref="MarkingSet.Serialize"/> text as UTF-8, lower-case hex.</param>
/// <param name="Placements">How many placements the base had.</param>
public sealed record MarkingDiffBase(string Generator, string Sha256, int Placements);

/// <summary>The fields of one base placement a diff changes.</summary>
/// <param name="Key">The base placement's <see cref="MarkingPlacement.Key"/>.</param>
/// <param name="Fields">
/// Field name (as in the placement JSON) and its new JSON value as written, or null to delete an optional
/// field; in document order.
/// </param>
public sealed record MarkingModification(string Key, IReadOnlyList<KeyValuePair<string, string?>> Fields);

/// <summary>A diff that does not fit the base it is applied to: it names a key the base does not have.</summary>
public sealed class MarkingDiffException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="markingClass">The class whose diff failed.</param>
    /// <param name="key">The key that did not resolve.</param>
    /// <param name="message">What went wrong.</param>
    public MarkingDiffException(string markingClass, string key, string message)
        : base(message)
    {
        Class = markingClass;
        Key = key;
    }

    /// <summary>The class whose diff failed.</summary>
    public string Class { get; }

    /// <summary>The key that did not resolve.</summary>
    public string Key { get; }
}

/// <summary>
/// A class's placements as OUR EDITS over the generated base: the <c>placements/&lt;class&gt;.diff.json</c>
/// document (<c>cyac.markings-diff/1</c>).
/// </summary>
/// <remarks>
/// <para>
/// The base is what <see cref="MarkingBootstrap"/> proposes from the shipped meshes. Its numbers are derived from
/// the original game's geometry, so they are regenerated on every machine rather than committed; what is
/// committed is only what a person changed: base placements removed, fields of base placements changed, and
/// placements added.
/// </para>
/// <para>
/// Base placements are named by their <see cref="MarkingPlacement.Key"/>, which the generator derives from its own
/// inputs (<c>shipped:&lt;records&gt;</c>, <c>band:&lt;records&gt;</c>, <c>rule:&lt;name&gt;</c>), never from a note.
/// The resolved order is the surviving base placements in base order, then the additions; <see cref="Order"/> is
/// written only when a set cannot be expressed that way.
/// </para>
/// <para>
/// The <see cref="Base"/> record lets a changed generator or tree be noticed: the diff still applies while every
/// key resolves (the caller reports the mismatch), and a key that does not resolve throws
/// <see cref="MarkingDiffException"/>.
/// </para>
/// </remarks>
/// <param name="Class">The mesh basename.</param>
/// <param name="Base">What the diff was made against.</param>
/// <param name="Remove">Keys of base placements to drop.</param>
/// <param name="Modify">Changed fields of base placements, in base order.</param>
/// <param name="Add">Placements added by hand.</param>
/// <param name="Order">
/// The resolved order when it is not "base, then additions": base keys and <c>add:&lt;index&gt;</c> tokens, each
/// exactly once; null otherwise.
/// </param>
public sealed record MarkingDiff(
    string Class,
    MarkingDiffBase Base,
    IReadOnlyList<string> Remove,
    IReadOnlyList<MarkingModification> Modify,
    IReadOnlyList<MarkingPlacement> Add,
    IReadOnlyList<string>? Order = null)
{
    /// <summary>The document format tag.</summary>
    public const string Format = "cyac.markings-diff/1";

    /// <summary>The prefix of an <see cref="Order"/> token that names an added placement by its index.</summary>
    public const string AddToken = "add:";

    /// <summary>Whether the diff names nothing: the class is exactly the base.</summary>
    public bool IsEmpty => Remove.Count == 0 && Modify.Count == 0 && Add.Count == 0 && Order is null;

    /// <summary>The SHA-256 a diff records for a base: over <see cref="MarkingSet.Serialize"/> as UTF-8, lower-case hex.</summary>
    /// <param name="baseSet">The base set.</param>
    public static string Sha256Of(MarkingSet baseSet)
    {
        ArgumentNullException.ThrowIfNull(baseSet);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(baseSet.Serialize())));
    }

    /// <summary>Computes the canonical diff that turns <paramref name="baseSet"/> into <paramref name="current"/>.</summary>
    /// <param name="baseSet">The generated base; every placement carries a unique key.</param>
    /// <param name="current">The edited set. A placement is the base placement of the same key (the first one, if
    /// a key repeats); any other placement is an addition.</param>
    /// <param name="generator">The generator id to record.</param>
    /// <exception cref="ArgumentException">The sets are for different classes or nations.</exception>
    public static MarkingDiff Create(MarkingSet baseSet, MarkingSet current, string generator)
    {
        ArgumentNullException.ThrowIfNull(baseSet);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(generator);
        if (!string.Equals(baseSet.Class, current.Class, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"the base is for '{baseSet.Class}', the set for '{current.Class}'", nameof(current));
        }

        // The nation is the generator's choice and has no place in the diff.
        if (baseSet.Nation != current.Nation)
        {
            throw new ArgumentException($"the set's nation '{current.Nation}' is not the base's '{baseSet.Nation}'", nameof(current));
        }

        Dictionary<string, int> keys = KeysOf(baseSet);
        Dictionary<string, MarkingPlacement> matched = new Dictionary<string, MarkingPlacement>(StringComparer.Ordinal);
        List<string> sequence = new List<string>();
        List<MarkingPlacement> additions = new List<MarkingPlacement>();
        foreach (MarkingPlacement p in current.Placements)
        {
            if (p.Key is { } key && keys.ContainsKey(key) && !matched.ContainsKey(key))
            {
                matched[key] = p;
                sequence.Add(key);
            }
            else
            {
                sequence.Add(AddToken + additions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                additions.Add(p with { Key = null });
            }
        }

        List<string> remove = new List<string>();
        List<MarkingModification> modify = new List<MarkingModification>();
        List<string> canonical = new List<string>();
        for (int index = 0; index < baseSet.Placements.Count; index++)
        {
            string key = baseSet.Placements[index].Key!;
            if (!matched.TryGetValue(key, out MarkingPlacement? edited))
            {
                remove.Add(key);
                continue;
            }

            canonical.Add(key);
            MarkingPlacement original = baseSet.Placements[index];
            List<KeyValuePair<string, string?>> fields = new List<KeyValuePair<string, string?>>();
            foreach (string field in MarkingSet.FieldOrder)
            {
                string? was = MarkingSet.FieldJson(original, field);
                string? now = MarkingSet.FieldJson(edited, field);
                if (was != now)
                {
                    fields.Add(new(field, now));
                }
            }

            if (fields.Count > 0)
            {
                modify.Add(new MarkingModification(key, fields));
            }
        }

        canonical.AddRange(additions.Select((_, i) => AddToken + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new MarkingDiff(
            baseSet.Class,
            new MarkingDiffBase(generator, Sha256Of(baseSet), baseSet.Placements.Count),
            remove,
            modify,
            additions,
            sequence.SequenceEqual(canonical, StringComparer.Ordinal) ? null : sequence);
    }

    /// <summary>
    /// Why this diff's <see cref="Base"/> is not the given base, or null when it is (same generator, hash and count).
    /// </summary>
    /// <param name="baseSet">The base the diff is applied to.</param>
    /// <param name="generator">The generator that produced it.</param>
    public string? BaseMismatch(MarkingSet baseSet, string generator)
    {
        ArgumentNullException.ThrowIfNull(baseSet);
        string sha = Sha256Of(baseSet);
        if (Base.Generator == generator && Base.Sha256 == sha && Base.Placements == baseSet.Placements.Count)
        {
            return null;
        }

        return $"the diff was made against {Base.Generator} sha256 {Short(Base.Sha256)} with {Base.Placements} placements; "
            + $"the base generated now is {generator} sha256 {Short(sha)} with {baseSet.Placements.Count}";
    }

    /// <summary>Resolves the class: the base with this diff applied.</summary>
    /// <param name="baseSet">The generated base; every placement carries a unique key.</param>
    /// <returns>The set; base placements keep their keys, additions have none.</returns>
    /// <exception cref="MarkingDiffException">A key does not resolve, or a change cannot be applied.</exception>
    public MarkingSet Apply(MarkingSet baseSet)
    {
        ArgumentNullException.ThrowIfNull(baseSet);
        if (!string.Equals(baseSet.Class, Class, StringComparison.OrdinalIgnoreCase))
        {
            throw new MarkingDiffException(Class, string.Empty, $"markings for {Class}: the diff was applied to the base of '{baseSet.Class}'");
        }

        Dictionary<string, int> keys = KeysOf(baseSet);
        HashSet<string> removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in Remove)
        {
            Resolve(keys, key, "remove");
            removed.Add(key);
        }

        MarkingPlacement[] placements = baseSet.Placements.ToArray();
        HashSet<string> modified = new HashSet<string>(StringComparer.Ordinal);
        foreach (MarkingModification modification in Modify)
        {
            int index = Resolve(keys, modification.Key, "modify");
            if (removed.Contains(modification.Key) || !modified.Add(modification.Key))
            {
                throw Failure(modification.Key, $"key '{modification.Key}' is modified twice, or both removed and modified");
            }

            foreach ((string field, string? json) in modification.Fields)
            {
                try
                {
                    using JsonDocument value = JsonDocument.Parse(json ?? "null");
                    placements[index] = MarkingSet.WithField(placements[index], field, value.RootElement);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException)
                {
                    throw Failure(modification.Key, $"key '{modification.Key}', field \"{field}\": {ex.Message}");
                }
            }
        }

        List<string> survivors = baseSet.Placements.Select(p => p.Key!).Where(k => !removed.Contains(k)).ToList();
        MarkingPlacement[] additions = Add.Select(p => p with { Key = null }).ToArray();
        List<MarkingPlacement> result = new List<MarkingPlacement>();
        if (Order is null)
        {
            result.AddRange(survivors.Select(k => placements[keys[k]]));
            result.AddRange(additions);
        }
        else
        {
            HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
            foreach (string token in Order)
            {
                if (!used.Add(token))
                {
                    throw Failure(token, $"order names '{token}' twice");
                }

                if (token.StartsWith(AddToken, StringComparison.Ordinal))
                {
                    if (!int.TryParse(token.AsSpan(AddToken.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int i)
                        || i >= additions.Length)
                    {
                        throw Failure(token, $"order names '{token}', but the diff adds {additions.Length} placements");
                    }

                    result.Add(additions[i]);
                }
                else
                {
                    int index = Resolve(keys, token, "order");
                    if (removed.Contains(token))
                    {
                        throw Failure(token, $"order names the removed key '{token}'");
                    }

                    result.Add(placements[index]);
                }
            }

            if (result.Count != survivors.Count + additions.Length)
            {
                throw Failure(string.Empty, $"order lists {result.Count} placements, but {survivors.Count} survive and {additions.Length} are added");
            }
        }

        return new MarkingSet(baseSet.Class, result, baseSet.Nation);
    }

    /// <summary>Parses a diff document.</summary>
    /// <param name="json">The document text.</param>
    /// <exception cref="InvalidDataException">The document is not a well-formed diff.</exception>
    public static MarkingDiff Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json, MarkingJson.DocumentOptions);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("a markings diff is a JSON object");
        }

        string format = MarkingJson.Str(root, "format") ?? string.Empty;
        if (format != Format)
        {
            throw new InvalidDataException($"expected format '{Format}', got '{format}'");
        }

        // Strict at the top level: a misspelt section would otherwise silently drop an edit.
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name is not ("format" or "class" or "base" or "remove" or "modify" or "add" or "order"))
            {
                throw new InvalidDataException($"a markings diff has no section \"{property.Name}\"");
            }
        }

        string cls = MarkingJson.Str(root, "class") ?? throw new InvalidDataException("a markings diff needs a \"class\"");
        if (!root.TryGetProperty("base", out JsonElement baseNode) || baseNode.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("a markings diff needs a \"base\" object");
        }

        MarkingDiffBase diffBase = new MarkingDiffBase(
            MarkingJson.Str(baseNode, "generator") ?? throw new InvalidDataException("the diff's base needs a \"generator\""),
            MarkingJson.Str(baseNode, "sha256") ?? throw new InvalidDataException("the diff's base needs a \"sha256\""),
            (int)(MarkingJson.Num(baseNode, "placements") ?? throw new InvalidDataException("the diff's base needs a \"placements\" count")));

        List<MarkingModification> modify = new List<MarkingModification>();
        if (root.TryGetProperty("modify", out JsonElement modifyNode))
        {
            if (modifyNode.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("the diff's \"modify\" is an object of key → fields");
            }

            foreach (JsonProperty entry in modifyNode.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException($"the diff's modify entry '{entry.Name}' is an object of fields");
                }

                modify.Add(new MarkingModification(
                    entry.Name,
                    entry.Value.EnumerateObject()
                        .Select(f => new KeyValuePair<string, string?>(f.Name, f.Value.ValueKind == JsonValueKind.Null ? null : f.Value.GetRawText()))
                        .ToArray()));
            }
        }

        List<MarkingPlacement> add = new List<MarkingPlacement>();
        if (root.TryGetProperty("add", out JsonElement addNode))
        {
            if (addNode.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("the diff's \"add\" is an array of placements");
            }

            foreach (JsonElement p in addNode.EnumerateArray())
            {
                add.Add(MarkingSet.ParsePlacement(p));
            }
        }

        return new MarkingDiff(
            cls,
            diffBase,
            Strings(root, "remove") ?? [],
            modify,
            add,
            Strings(root, "order"));
    }

    /// <summary>Writes the document text: stable section and field order, one change per line.</summary>
    public string Serialize()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\n  \"format\": \"").Append(Format).Append("\",\n");
        sb.Append("  \"class\": ").Append(JsonSerializer.Serialize(Class)).Append(",\n");
        sb.Append("  \"base\": { \"generator\": ").Append(JsonSerializer.Serialize(Base.Generator))
          .Append(", \"sha256\": ").Append(JsonSerializer.Serialize(Base.Sha256))
          .Append(", \"placements\": ").Append(Base.Placements.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" },\n");

        WriteStrings(sb, "remove", Remove);
        sb.Append(",\n");

        sb.Append("  \"modify\": {");
        for (int i = 0; i < Modify.Count; i++)
        {
            MarkingModification m = Modify[i];
            sb.Append(i == 0 ? "\n" : ",\n").Append("    ").Append(JsonSerializer.Serialize(m.Key)).Append(": {");
            KeyValuePair<string, string?>[] fields = m.Fields.OrderBy(f => FieldRank(f.Key)).ToArray();
            for (int j = 0; j < fields.Length; j++)
            {
                sb.Append(j == 0 ? "\n" : ",\n").Append("      ").Append(JsonSerializer.Serialize(fields[j].Key)).Append(": ").Append(fields[j].Value ?? "null");
            }

            sb.Append(fields.Length == 0 ? "}" : "\n    }");
        }

        sb.Append(Modify.Count == 0 ? "},\n" : "\n  },\n");

        sb.Append("  \"add\": [");
        if (Add.Count > 0)
        {
            sb.Append('\n');
            MarkingSet.WritePlacements(sb, Add);
            sb.Append("  ]");
        }
        else
        {
            sb.Append(']');
        }

        if (Order is not null)
        {
            sb.Append(",\n");
            WriteStrings(sb, "order", Order);
        }

        sb.Append("\n}\n");
        return sb.ToString();
    }

    private static Dictionary<string, int> KeysOf(MarkingSet baseSet)
    {
        Dictionary<string, int> keys = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < baseSet.Placements.Count; i++)
        {
            string key = baseSet.Placements[i].Key
                ?? throw new InvalidOperationException($"base placement {i} of '{baseSet.Class}' has no key");
            if (!keys.TryAdd(key, i))
            {
                throw new InvalidOperationException($"the base of '{baseSet.Class}' has the key '{key}' twice");
            }
        }

        return keys;
    }

    private int Resolve(Dictionary<string, int> keys, string key, string section) =>
        keys.TryGetValue(key, out int index)
            ? index
            : throw Failure(key, $"{section} names key '{key}', which the generated base does not have");

    private MarkingDiffException Failure(string key, string message) => new(Class, key, $"markings for {Class}: {message}");

    private static int FieldRank(string field)
    {
        int rank = -1;
        for (int i = 0; i < MarkingSet.FieldOrder.Count; i++)
        {
            if (MarkingSet.FieldOrder[i] == field)
            {
                rank = i;
            }
        }

        return rank < 0 ? int.MaxValue : rank;
    }

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] + "…" : sha;

    private static string[]? Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement node))
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.Array || node.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
        {
            throw new InvalidDataException($"the diff's \"{name}\" is an array of keys");
        }

        return node.EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    private static void WriteStrings(StringBuilder sb, string name, IReadOnlyList<string> values)
    {
        sb.Append("  \"").Append(name).Append("\": [");
        for (int i = 0; i < values.Count; i++)
        {
            sb.Append(i == 0 ? "\n" : ",\n").Append("    ").Append(JsonSerializer.Serialize(values[i]));
        }

        sb.Append(values.Count == 0 ? "]" : "\n  ]");
    }
}
