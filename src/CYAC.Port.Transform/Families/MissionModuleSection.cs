using System.Text.Json.Nodes;
using CYAC.Formats.EaLib;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// The <c>module</c> section of a mission document: what the <c>.S</c> trailer's native x86 win-rule
/// module IS, in semantic form, plus the named immediates the inverse writes back into it.
/// </summary>
/// <remarks>
/// <para>
/// Law L6 — the module is code, so the transform does not "convert" it: the bytes stay in
/// <c>data.trailer[].module_hex</c>.  What ships beside them is knowledge: the five-slot export ABI
/// of the module, the briefing text export 0 copies out, the rule lines of the win-condition model, and
/// the parameter catalog — every byte-stable editable scalar of the module with its offset, its
/// range, and the instruction bytes that anchor it.  <see cref="WinRuleParamCollector"/> derives that
/// catalog from the container itself: it disassembles the module, runs the rule extractor, and proves
/// each parameter by patching a copy and reading the rules back (157 parameters over the 51 shipped
/// modules, 126 of them proven semantically).
/// </para>
/// <para>
/// The document is self-contained: each parameter carries its own anchor bytes, so
/// <see cref="ApplyWinRuleParameters"/> validates and patches without consulting the catalog again.
/// A parameter whose anchor no longer matches the code is a loud failure, not a silent skip — that
/// is the difference between "your edit was applied" and "your edit was lost".
/// </para>
/// <para>
/// Every module whose code fits the synthesizer's rulebook also carries its statement IR, read out of
/// the module bytes by <see cref="WinRuleExtractor"/>: the 42 template missions and four of the nine
/// bespoke ones (ACE, PIRATE, RAMROD, STRAFE).  ALONE, BOLO, GAUNTLET, INSTR and MOOLAH do not fit, so
/// they have only their hand-decoded idiom and rule lines.  The IR is read-only here: it is a
/// description of the shipped code, accepted only when re-synthesizing it gives the shipped bytes back.
/// </para>
/// </remarks>
public static class MissionModuleSection
{
    /// <summary>Builds the section for one parsed <c>.S</c>.</summary>
    /// <param name="file">The parsed container.</param>
    /// <param name="assetName">The EALIB member name, e.g. <c>"ABB.S"</c>.</param>
    /// <param name="unknownBytes">Bytes of the module nothing explains.</param>
    public static JsonObject Build(SMissionDecoder.SFile file, string assetName, out int unknownBytes)
    {
        ArgumentNullException.ThrowIfNull(file);
        unknownBytes = 0;

        if (file.Module is not { } module)
        {
            return new JsonObject
            {
                ["present"] = false,
                ["about"] = "This container carries no trailer module — no win rules, no briefing.",
            };
        }

        JsonObject section = new JsonObject
        {
            ["present"] = true,
            ["about"] =
                "The mission's rules as the original ships them: a position-independent x86 module " +
                "with five far-called exports, copied verbatim to the far heap at load. Its bytes " +
                "live in data.trailer[].module_hex and are NOT rewritten by this transform (law " +
                "L6); the editable part is winRules.params[].value, which is written back into the " +
                "code on export.",
            ["codeBytes"] = module.Bytes.Length,
            ["blockFileOffset"] = module.FileOffset,
            ["exports"] = Exports(module),
        };

        if (module.TryGetBriefingCopy(out int src, out int len))
        {
            section["briefing"] = new JsonObject
            {
                ["_note"] = "Read-only: export 0 copies it from a fixed (source, length) inside the " +
                            "code, so changing it means re-laying-out the module.",
                ["sourceOffset"] = src,
                ["bytes"] = len,
                ["text"] = SMissionDecoder.RenderGameText(module.TextAt(src, len)),
            };
        }

        section["winRules"] = WinRules(file, module, assetName);
        return section;
    }

    /// <summary>
    /// Writes the document's win-rule parameter values back into the model's trailer module.
    /// </summary>
    /// <param name="model">The mission model whose trailer carries the module.</param>
    /// <param name="section">The document's <c>module</c> section, or null.</param>
    /// <exception cref="InvalidDataException">A parameter's anchor no longer matches the module's code.</exception>
    public static void ApplyWinRuleParameters(SDataModel model, JsonObject? section)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (section?["winRules"] is not JsonObject rules || rules["params"] is not JsonArray parameters)
        {
            return;
        }

        SDataTrailerItem? trailer = model.Trailer.FirstOrDefault(t => t.Module is not null);
        if (trailer?.Module is not { } code)
        {
            return;
        }

        foreach (JsonNode? entry in parameters)
        {
            if (entry is not JsonObject parameter || parameter["editable"]?.GetValue<bool>() != true)
            {
                continue;
            }

            string id = parameter["id"]?.GetValue<string>() ?? "?";
            int offset = Int(parameter, "moduleOffset", id);
            int anchorAt = Int(parameter, "instructionOffset", id);
            byte[] anchor = Convert.FromHexString(parameter["anchorBytes"]?.GetValue<string>() ?? string.Empty);
            bool declaredOk = parameter["_anchorMatched"]?.GetValue<bool>() ?? true;
            int width = (parameter["encoding"]?.GetValue<string>() ?? "imm8") == "imm16" ? 2 : 1;

            if (!AnchorMatches(code, anchorAt, anchor, offset, width))
            {
                if (!declaredOk)
                {
                    continue;   // it did not match when the tree was written either — nothing to apply
                }

                throw new InvalidDataException(
                    $"win-rule parameter \"{id}\" no longer sits at module+0x{offset:X}: the " +
                    $"instruction bytes {parameter["anchorBytes"]} are not at module+0x{anchorAt:X}. " +
                    "The module's code was changed under the parameter list — remove the parameter " +
                    "or restore the code.");
            }

            int value = Int(parameter, "value", id);
            int min = parameter["min"]?.GetValue<int>() ?? int.MinValue;
            int max = parameter["max"]?.GetValue<int>() ?? int.MaxValue;
            if (value < min || value > max)
            {
                throw new InvalidDataException(
                    $"win-rule parameter \"{id}\" = {value} is outside its proven range [{min}..{max}]");
            }

            // The instruction decides what the bytes mean: a value the immediate cannot hold would be
            // written as a different number (200 into a sign-extended byte reads back as -56).
            WinRulesCatalog.ImmediateForm form = WinRulesCatalog.FormOf(anchor, width);
            (int lowest, int highest) = WinRulesCatalog.RangeOf(form);
            if (value < lowest || value > highest)
            {
                throw new InvalidDataException(
                    $"win-rule parameter \"{id}\" = {value} does not fit its {form} immediate " +
                    $"[{lowest}..{highest}]");
            }

            code[offset] = (byte)(value & 0xFF);
            if (width == 2)
            {
                code[offset + 1] = (byte)((value >> 8) & 0xFF);
            }
        }
    }

    private static JsonArray Exports(SMissionDecoder.ModuleBlock module)
    {
        JsonArray exports = new JsonArray();
        for (int slot = 0; slot < SMissionDecoder.ModuleBlock.SlotNames.Length; slot++)
        {
            exports.Add(new JsonObject
            {
                ["slot"] = slot,
                ["name"] = SMissionDecoder.ModuleBlock.SlotNames[slot],
                ["offset"] = module.ExportOffsets[slot],
                ["extentUpperBound"] = module.ExtentTo(slot),
                ["hasCompilerPrologue"] = module.HasMscPrologue(slot),
            });
        }

        return exports;
    }

    private static JsonObject WinRules(SMissionDecoder.SFile file, SMissionDecoder.ModuleBlock module, string assetName)
    {
        WinRuleParamCollector.Collection collection = WinRuleParamCollector.Collect(file, assetName);
        if (collection.Mission is not { } entry)
        {
            return new JsonObject
            {
                ["_note"] = $"The win rules of {assetName} could not be read from its module " +
                            $"({collection.Reason}), so nothing here is editable.",
            };
        }

        JsonObject rules = new JsonObject
        {
            ["class"] = entry.Class,
            ["_semanticCoverage"] = entry.IsTemplate
                ? "the template rule model plus these parameters describe the module completely"
                : "hand-decoded: see idiom and ruleLines",
            ["_ruleLines"] = Lines(entry.RuleLines),
        };

        if (entry.Idiom is { Length: > 0 })
        {
            rules["_idiom"] = entry.Idiom;
        }

        if (entry.BespokeSlots.Count > 0)
        {
            rules["_bespokeSlots"] = Lines(entry.BespokeSlots);
        }

        rules["params"] = Parameters(module, entry);

        // The runtime models a mission's rules from this IR (transform-plan L2), so every module the
        // extractor can read carries it, template or not.  The extractor only returns a model that
        // re-synthesizes to the module byte for byte, so irReproducesTheModule is true by
        // construction; it stays because the document format has always carried it.
        if (WinRuleExtractor.Extract(assetName, module.Bytes).Model is { } model)
        {
            rules["irReproducesTheModule"] = true;
            rules["model"] = JsonNode.Parse(model.ToJson());
        }

        return rules;
    }

    private static JsonArray Parameters(SMissionDecoder.ModuleBlock module, WinRulesCatalog.Mission entry)
    {
        JsonArray parameters = new JsonArray();
        foreach (WinRulesCatalog.Param p in entry.Params)
        {
            // The entry was read from this module, so the anchor matches; the check stays because the
            // document records it and the export path relies on it.
            byte[] anchor = Convert.FromHexString(p.ContextBytes);
            bool matched = AnchorMatches(module.Bytes, p.InsnBlockOffset, anchor, p.BlockOffset, p.EncodingBytes);
            JsonObject parameter = new JsonObject
            {
                ["id"] = p.Id,
                ["kind"] = p.Kind,
                ["name"] = p.Name,
                ["_meaning"] = p.Meaning,
                ["value"] = matched ? ReadValue(module.Bytes, p.BlockOffset, anchor, p.EncodingBytes) : p.Value,
                ["min"] = p.Min,
                ["max"] = p.Max,
                ["editable"] = p.Editable,
                ["encoding"] = p.Encoding,
                ["moduleOffset"] = p.BlockOffset,
                ["instructionOffset"] = p.InsnBlockOffset,
                ["anchorBytes"] = p.ContextBytes,
                ["_instruction"] = p.Insn,
                ["_verified"] = p.Verified,
                ["_anchorMatched"] = matched,
            };

            if (p.Counter is { } counter)
            {
                parameter["_counterAt"] = $"module+0x{counter:X}";
            }

            if (p.SharedWithDebrief)
            {
                parameter["_sharedWithDebrief"] = true;
            }

            parameters.Add(parameter);
        }

        return parameters;
    }

    private static bool AnchorMatches(byte[] code, int anchorAt, byte[] anchor, int valueAt, int width)
    {
        if (anchor.Length == 0 || anchorAt < 0 || anchorAt + anchor.Length > code.Length)
        {
            return false;
        }

        if (valueAt != anchorAt + anchor.Length || valueAt + width > code.Length)
        {
            return false;
        }

        return code.AsSpan(anchorAt, anchor.Length).SequenceEqual(anchor);
    }

    private static int ReadValue(byte[] code, int offset, byte[] anchor, int width) =>
        WinRulesCatalog.ReadImmediate(code.AsSpan(offset, width), WinRulesCatalog.FormOf(anchor, width));

    private static JsonArray Lines(IEnumerable<string> lines)
    {
        JsonArray array = new JsonArray();
        foreach (string line in lines)
        {
            array.Add(line);
        }

        return array;
    }

    private static int Int(JsonObject parameter, string field, string id) =>
        parameter[field]?.GetValue<int>()
        ?? throw new InvalidDataException($"win-rule parameter \"{id}\" has no \"{field}\"");
}
