namespace CYAC.Formats.EaLib;

/// <summary>
/// In-place BYTE-STABLE patcher for `.S` mission-module bodies.
///
/// <para>The stage-2 MVP edit doctrine: only fixed-size immediates inside the
/// trailer module block may change, so the PIC module layout (export table,
/// string (src,len) pairs, code offsets) never moves.  This class enforces
/// that contract structurally:</para>
///
/// <list type="bullet">
/// <item>every patch must lie INSIDE the module block extent, PAST the 10-byte
/// export table (block+0..9 = the 5 u16 export offsets, P4 §2);</item>
/// <item>patches are same-length (old/new byte counts equal) — the body size
/// cannot change;</item>
/// <item>the old bytes must match the current body (stale-catalog guard);</item>
/// <item>post-patch the body must RE-PARSE cleanly (byte accounting total) with
/// the header table_off, the block file offset, the block size and all five
/// export offsets UNCHANGED, all five exported functions still carrying the
/// MSC far prologue <c>55 8B EC</c>, and the typed model must re-emit
/// byte-identically to the patched body.</item>
/// </list>
/// </summary>
public static class SMissionPatcher
{
    /// <summary>One same-length byte splice at a file offset of the DECOMPRESSED body.</summary>
    public sealed record Patch(int FileOffset, byte[] OldBytes, byte[] NewBytes, string Description)
    {
        public override string ToString() =>
            $"@0x{FileOffset:X4}: {Convert.ToHexString(OldBytes)} -> {Convert.ToHexString(NewBytes)}  ({Description})";
    }

    /// <summary>Build a patch from a catalog parameter + desired value.</summary>
    public static Patch FromParam(byte[] currentBody, WinRulesCatalog.Mission m,
                                  WinRulesCatalog.Param p, int newValue)
    {
        if (!WinRulesCatalog.ContextMatches(currentBody, m, p))
            throw new InvalidDataException(
                $"{p.Id}: instruction context at 0x{m.BlockFileOff + p.InsnBlockOffset:X} does not " +
                "match the shipping module — refusing to patch");
        byte[] oldBytes = currentBody.AsSpan(p.FileOffset, p.EncodingBytes).ToArray();
        byte[] newBytes = WinRulesCatalog.ValueBytes(p, newValue);
        return new Patch(p.FileOffset, oldBytes, newBytes,
                         $"{p.Id} \"{p.Name}\" {WinRulesCatalog.ReadValue(currentBody, p)} -> {newValue}");
    }

    /// <summary>
    /// Apply <paramref name="patches"/> to a decompressed `.S` body and return
    /// the patched copy.  Throws on any structural-validation failure; the
    /// input is never mutated.
    /// </summary>
    public static byte[] Apply(byte[] body, string assetName, IReadOnlyList<Patch> patches)
    {
        SMissionDecoder.SFile before = SMissionDecoder.Parse(body, assetName);
        SMissionDecoder.ModuleBlock mod = before.Module
                                          ?? throw new InvalidDataException($"{assetName}: no trailer module block — nothing to patch");
        int blockStart = mod.FileOffset;
        int blockEnd = blockStart + mod.Bytes.Length;

        byte[] result = (byte[])body.Clone();
        foreach (Patch p in patches)
        {
            if (p.NewBytes.Length != p.OldBytes.Length || p.NewBytes.Length == 0)
                throw new InvalidDataException($"{assetName}: patch {p} is not same-length");
            if (p.FileOffset < blockStart + 10 || p.FileOffset + p.NewBytes.Length > blockEnd)
                throw new InvalidDataException(
                    $"{assetName}: patch {p} outside the module extent " +
                    $"[0x{blockStart + 10:X}..0x{blockEnd:X}) — only module-block immediates are editable");
            for (int i = 0; i < p.OldBytes.Length; i++)
                if (result[p.FileOffset + i] != p.OldBytes[i])
                    throw new InvalidDataException(
                        $"{assetName}: patch {p}: byte at 0x{p.FileOffset + i:X} is " +
                        $"0x{result[p.FileOffset + i]:X2}, expected 0x{p.OldBytes[i]:X2} (stale patch?)");
            p.NewBytes.CopyTo(result, p.FileOffset);
        }

        // ---- structural post-conditions -----------------------------------
        SMissionDecoder.SFile after = SMissionDecoder.Parse(result, assetName);   // throws if accounting breaks
        if (after.TableOff != before.TableOff)
            throw new InvalidDataException($"{assetName}: table_off changed under patch");
        SMissionDecoder.ModuleBlock modAfter = after.Module
                                               ?? throw new InvalidDataException($"{assetName}: module block lost under patch");
        if (modAfter.FileOffset != blockStart || modAfter.Bytes.Length != mod.Bytes.Length)
            throw new InvalidDataException($"{assetName}: module extent changed under patch");
        if (!modAfter.ExportOffsets.SequenceEqual(mod.ExportOffsets))
            throw new InvalidDataException($"{assetName}: export table changed under patch");
        for (int slot = 0; slot < 5; slot++)
            if (!modAfter.HasMscPrologue(slot))
                throw new InvalidDataException(
                    $"{assetName}: slot {slot} ({SMissionDecoder.ModuleBlock.SlotNames[slot]}) " +
                    "lost its 55 8B EC prologue under patch");
        if (!SMissionDecoder.ToBytes(after).AsSpan().SequenceEqual(result))
            throw new InvalidDataException($"{assetName}: patched body no longer re-emits byte-exactly");
        return result;
    }

    // =======================================================================
    // RE-LAYOUT path — replace the WHOLE trailer module.
    // =======================================================================

    /// <summary>
    /// Splice a freshly synthesized module block into a decompressed `.S` body,
    /// replacing the existing trailer 0x00-block.  Unlike <see cref="Apply"/>
    /// this is NOT byte-stable: the module's internal layout may move (new
    /// string lengths shift every downstream offset and the export table), and
    /// the block may change SIZE.
    ///
    /// <para>What has to be re-derived, and why nothing else does (the
    /// the trailer block is <c>0x00 | u16 size | size bytes</c> at the very END
    /// of the file, so (a) the u16 SIZE WORD is recomputed — the re-emitter
    /// writes <c>Payload.Length</c>, and (b) NOTHING before the block moves, so
    /// the header's <c>table_off</c> (u16 @+4, the file offset of that size
    /// word — the pointer <c>s_asset_briefing_text_extract</c> @image@0x0966B
    /// uses to find the export table) stays valid unchanged.  The 5 export
    /// offsets are block-relative and come from the synthesizer; the game
    /// far-heap-copies the block verbatim, so no absolute fixups exist
    /// (P4: the modules are 100% PIC, 255/255 functions).</para>
    ///
    /// <para>Validation before returning: the new block passes
    /// <see cref="WinRuleSynthesizer.Sanity"/>, the emitted body RE-PARSES with
    /// total byte accounting and no parser notes, the header table_off and the
    /// block's file offset are unchanged, the size word matches, the module the
    /// parser recovers is byte-identical to the block we spliced, all five
    /// exports carry the <c>55 8B EC</c> prologue, and the re-parsed model
    /// re-emits byte-exactly.</para>
    /// </summary>
    public static byte[] ReplaceTrailerModule(byte[] body, string assetName, byte[] newBlock,
                                              WinRuleSynthesizer.Layout? layout = null)
    {
        List<string> sanity = WinRuleSynthesizer.Sanity(newBlock, layout);
        if (sanity.Count > 0)
            throw new InvalidDataException(
                $"{assetName}: synthesized module fails the sanity sweep — " + string.Join("; ", sanity));

        SMissionDecoder.SFile before = SMissionDecoder.Parse(body, assetName);
        SMissionDecoder.ModuleBlock mod = before.Module
                                          ?? throw new InvalidDataException($"{assetName}: no trailer module block to replace");

        int blockIndex = before.Trailer.FindIndex(t => t.IsBlock);
        if (blockIndex < 0)
            throw new InvalidDataException($"{assetName}: trailer carries no 0x00-block");
        if (before.Trailer.Count(t => t.IsBlock) != 1)
            throw new InvalidDataException($"{assetName}: trailer carries more than one block — " +
                                           "which one is the mission module is ambiguous");
        before.Trailer[blockIndex] = before.Trailer[blockIndex] with { Payload = newBlock };

        byte[] result = SMissionDecoder.ToBytes(before);

        // ---- structural post-conditions -----------------------------------
        SMissionDecoder.SFile after = SMissionDecoder.Parse(result, assetName);      // throws if accounting breaks
        if (after.Notes.Count > 0)
            throw new InvalidDataException(
                $"{assetName}: re-parse of the spliced body reports " + string.Join("; ", after.Notes));
        if (after.TableOff != before.TableOff)
            throw new InvalidDataException($"{assetName}: table_off changed under the splice");
        SMissionDecoder.ModuleBlock modAfter = after.Module
                                               ?? throw new InvalidDataException($"{assetName}: module block lost under the splice");
        if (modAfter.FileOffset != mod.FileOffset)
            throw new InvalidDataException(
                $"{assetName}: module moved in the file (0x{mod.FileOffset:X} -> " +
                $"0x{modAfter.FileOffset:X}) — everything before the trailer must be untouched");
        if (!modAfter.Bytes.AsSpan().SequenceEqual(newBlock))
            throw new InvalidDataException($"{assetName}: the re-parsed module is not the block we spliced");
        int sizeWord = result[modAfter.FileOffset - 2] | (result[modAfter.FileOffset - 1] << 8);
        if (sizeWord != newBlock.Length)
            throw new InvalidDataException(
                $"{assetName}: trailer size word is 0x{sizeWord:X} but the module is 0x{newBlock.Length:X} B");
        if (after.TableOff != 0 && after.TableOff != modAfter.FileOffset - 2)
            throw new InvalidDataException(
                $"{assetName}: header table_off 0x{after.TableOff:X} no longer points at the size word " +
                $"0x{modAfter.FileOffset - 2:X}");
        for (int slot = 0; slot < 5; slot++)
            if (!modAfter.HasMscPrologue(slot))
                throw new InvalidDataException(
                    $"{assetName}: slot {slot} ({SMissionDecoder.ModuleBlock.SlotNames[slot]}) " +
                    "lacks the 55 8B EC prologue after the splice");
        if (!SMissionDecoder.ToBytes(after).AsSpan().SequenceEqual(result))
            throw new InvalidDataException($"{assetName}: spliced body does not re-emit byte-exactly");
        return result;
    }

    // =======================================================================
    // CONTENT path — regenerate the WHOLE `.S` body from the
    // data-section model, optionally with a freshly synthesized module too.
    // =======================================================================

    /// <summary>
    /// Rebuild a `.S` / `.W` body from its data-section model
    /// (<see cref="SDataSynthesizer.Synth"/>), optionally replacing the trailer
    /// module block in the same pass.
    ///
    /// <para>This is the widest edit the editor can make: the object stream, the
    /// site table and the name table may all change size, so EVERYTHING moves —
    /// including the trailer module's file offset and therefore the header's
    /// <c>table_off</c>.  That is safe precisely because the synthesizer DERIVES
    /// table_off from the emitted content rather than carrying it (the module
    /// itself is position-independent: P4 proved all 255 module functions are
    /// PIC, and the engine far-heap-copies the block verbatim).</para>
    ///
    /// <para>Composition with <see cref="ReplaceTrailerModule"/>: data sections
    /// precede the trailer, so a module re-synthesis is expressed here as
    /// <paramref name="newBlock"/> rather than as a second splice — one pass, one
    /// derivation of table_off, no intermediate body that is only half-valid.
    /// Passing null keeps the module the model carries (opaque
    /// <c>module_hex</c>).</para>
    ///
    /// <para>The validation ladder, in order (any failure throws and the input
    /// is never mutated):</para>
    /// <list type="number">
    /// <item>the model is structurally valid (<see cref="SDataSynthesizer.ValidateModel"/>:
    /// slot budget/uniqueness, backward-only place refs, script caps, one player +
    /// one home base);</item>
    /// <item>the new module block passes <see cref="WinRuleSynthesizer.Sanity"/>
    /// (when one is supplied);</item>
    /// <item>the emitted body RE-PARSES with TOTAL byte accounting and zero parser
    /// notes;</item>
    /// <item>the trailer size word and the header table_off agree with where the
    /// block actually landed, and the parser recovers exactly the block we meant
    /// to ship;</item>
    /// <item>all five exports still carry the MSC far prologue <c>55 8B EC</c>;</item>
    /// <item>the re-parsed container re-emits BYTE-EXACTLY, and the data model
    /// extracted back out of the emitted bytes equals the model we authored
    /// (canonically) — i.e. the round trip is closed at the semantic level too.</item>
    /// </list>
    /// </summary>
    /// <param name="body">The container as it is.</param>
    /// <param name="assetName">Its asset name, for the messages.</param>
    /// <param name="model">The content to put in it.</param>
    /// <param name="newBlock">A re-synthesized module block, or null to keep the model's.</param>
    /// <param name="layout">The block's layout, for its sanity sweep.</param>
    /// <param name="className">
    /// The name of a class id for the refusal messages, or null to print the id — the same lookup
    /// <see cref="SDataSynthesizer.ValidateModel"/> takes.
    /// </param>
    public static byte[] ReplaceContent(byte[] body, string assetName, SDataModel model,
                                        byte[]? newBlock = null,
                                        WinRuleSynthesizer.Layout? layout = null,
                                        Func<int, string?>? className = null)
    {
        List<string> modelErrs = SDataSynthesizer.ValidateModel(model, className: className);
        if (modelErrs.Count > 0)
            throw new InvalidDataException(
                $"{assetName}: the mission content does not validate — " + string.Join("; ", modelErrs));

        SDataModel emit = model.Clone();
        if (newBlock is not null)
        {
            List<string> sanity = WinRuleSynthesizer.Sanity(newBlock, layout);
            if (sanity.Count > 0)
                throw new InvalidDataException(
                    $"{assetName}: synthesized module fails the sanity sweep — " + string.Join("; ", sanity));
            int at = emit.Trailer.FindIndex(t => t.Module is not null);
            if (at < 0)
                throw new InvalidDataException($"{assetName}: the model carries no trailer module to replace");
            if (emit.Trailer.Count(t => t.Module is not null) != 1)
                throw new InvalidDataException(
                    $"{assetName}: the model carries more than one trailer block — which is the mission " +
                    "module is ambiguous");
            emit.Trailer[at].Module = newBlock;
        }

        byte[] expectBlock = emit.ModuleBlock
            ?? throw new InvalidDataException($"{assetName}: no trailer module block in the model");
        byte[] result = SDataSynthesizer.Synth(emit);

        // ---- structural post-conditions ---------------------------------------
        SMissionDecoder.SFile after = SMissionDecoder.Parse(result, assetName);          // throws if accounting breaks
        if (after.Notes.Count > 0)
            throw new InvalidDataException(
                $"{assetName}: re-parse of the rebuilt body reports " + string.Join("; ", after.Notes));
        SMissionDecoder.ModuleBlock modAfter = after.Module
                                               ?? throw new InvalidDataException($"{assetName}: the rebuilt body has no module block");
        if (!modAfter.Bytes.AsSpan().SequenceEqual(expectBlock))
            throw new InvalidDataException($"{assetName}: the re-parsed module is not the block we authored");
        int sizeWord = result[modAfter.FileOffset - 2] | (result[modAfter.FileOffset - 1] << 8);
        if (sizeWord != expectBlock.Length)
            throw new InvalidDataException(
                $"{assetName}: trailer size word is 0x{sizeWord:X} but the module is 0x{expectBlock.Length:X} B");
        if (after.TableOff != modAfter.FileOffset - 2)
            throw new InvalidDataException(
                $"{assetName}: header table_off 0x{after.TableOff:X} does not point at the size word " +
                $"0x{modAfter.FileOffset - 2:X} — the briefing extractor would read garbage");
        for (int slot = 0; slot < 5; slot++)
            if (!modAfter.HasMscPrologue(slot))
                throw new InvalidDataException(
                    $"{assetName}: slot {slot} ({SMissionDecoder.ModuleBlock.SlotNames[slot]}) " +
                    "lacks the 55 8B EC prologue after the rebuild");
        if (!SMissionDecoder.ToBytes(after).AsSpan().SequenceEqual(result))
            throw new InvalidDataException($"{assetName}: rebuilt body does not re-emit byte-exactly");
        if (SDataSynthesizer.Extract(after, assetName).CanonicalJson() != emit.CanonicalJson())
            throw new InvalidDataException(
                $"{assetName}: the content extracted back out of the emitted bytes is not the content " +
                "we authored");
        return result;
    }
}
