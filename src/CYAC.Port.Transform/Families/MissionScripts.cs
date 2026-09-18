using System.Text.Json.Nodes;
using CYAC.Formats.EaLib;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// Turns a mission document's <c>ai_script</c> attributes between their two accepted forms: the raw
/// <c>hex</c> payload the container stores, and the structured <c>script</c> a person can edit.
/// </summary>
/// <remarks>
/// <para>
/// The engagement VM's authored bytecode is <c>[delay u16][opcode][operands]</c>
/// (<see cref="AiScriptModel"/>; the opcode table is transcribed from the load-time
/// patcher <c>ai_script_named_place_patch @image@0x094EF</c> and the VM itself).  A structured form
/// is only substituted when <see cref="AiScriptSynthesizer"/> re-emits the exact bytes — otherwise
/// the hex stays, so the round trip can never depend on the script model being complete.
/// </para>
/// <para>
/// Both directions are pure JSON-node surgery over <see cref="SDataModel"/>'s own document, which is
/// what lets the transform add a superset without forking the model's schema.
/// </para>
/// </remarks>
public static class MissionScripts
{
    /// <summary>The attribute name the container's tag 0x89 carries.</summary>
    public const string ScriptAttribute = "ai_script";

    /// <summary>Replaces every round-trippable <c>hex</c> script with its structured form.</summary>
    /// <param name="data">The <see cref="SDataModel"/> document to rewrite in place.</param>
    /// <param name="total">Incremented by the number of scripts seen.</param>
    /// <param name="structured">Incremented by the number given a structured form.</param>
    public static void Structure(JsonObject data, ref int total, ref int structured)
    {
        ArgumentNullException.ThrowIfNull(data);
        foreach (JsonObject attribute in ScriptAttributes(data))
        {
            if (attribute["hex"]?.GetValue<string>() is not { } hex)
            {
                continue;
            }

            total++;
            byte[] payload = Convert.FromHexString(hex);
            AiScriptModel model;
            try
            {
                model = AiScriptSynthesizer.Extract(payload);
                if (!AiScriptSynthesizer.Synth(model).AsSpan().SequenceEqual(payload))
                {
                    continue;
                }
            }
            catch (AiScriptException)
            {
                // Not authorable under the model (a refused opcode, a desync): keep the bytes.
                continue;
            }

            attribute.Remove("hex");
            attribute["script"] = model.ToJsonNode();
            attribute["_disasm"] = Listing(model);
            structured++;
        }
    }

    /// <summary>Turns every structured <c>script</c> back into the raw <c>hex</c> the model reads.</summary>
    /// <param name="data">The <see cref="SDataModel"/> document to rewrite in place.</param>
    /// <exception cref="InvalidDataException">A script does not synthesize.</exception>
    public static void Flatten(JsonObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        foreach (JsonObject attribute in ScriptAttributes(data))
        {
            attribute.Remove("_disasm");
            if (attribute["script"] is not JsonObject script)
            {
                continue;
            }

            attribute.Remove("script");
            try
            {
                AiScriptModel model = AiScriptModel.FromJsonNode(
                    JsonNode.Parse(script.ToJsonString()) as JsonObject
                    ?? throw new InvalidDataException("an ai_script's \"script\" is not an object"));
                attribute["hex"] = Convert.ToHexString(AiScriptSynthesizer.Synth(model)).ToLowerInvariant();
            }
            catch (AiScriptException ex)
            {
                throw new InvalidDataException($"an AI script does not synthesize: {ex.Message}");
            }
        }
    }

    private static JsonArray Listing(AiScriptModel model)
    {
        JsonArray lines = new JsonArray();
        foreach (string line in AiScriptSynthesizer.Disasm(model)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            lines.Add(line.TrimEnd('\r'));
        }

        return lines;
    }

    private static IEnumerable<JsonObject> ScriptAttributes(JsonObject data)
    {
        if (data["stream"] is not JsonArray stream)
        {
            yield break;
        }

        foreach (JsonNode? item in stream)
        {
            if (item is not JsonObject obj || obj["attrs"] is not JsonArray attrs)
            {
                continue;
            }

            foreach (JsonNode? attr in attrs)
            {
                if (attr is JsonObject attribute
                    && attribute["attr"]?.GetValue<string>() == ScriptAttribute)
                {
                    yield return attribute;
                }
            }
        }
    }
}
