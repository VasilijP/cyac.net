using System.Text;

namespace CYAC.Formats.EaLib;

/// <summary>
/// EALIB archive <b>writer</b>.
///
/// <para>Container layout re-emitted exactly as <see cref="EaLibArchive"/> reads it
/// (re-verified on all 6 shipping libs):</para>
/// <code>
///   +0x00  5   "EALIB"
///   +0x05  u16 file_count
///   +0x07      (file_count + 1) x 18-B directory records:
///                name[13] (ASCII, NUL-padded)  flag_u8  offset_u32-LE
///              the final record is the sentinel: 13 NUL bytes, flag 0x00,
///              offset = total archive size
///   payloads   contiguous, in directory order, first payload starting exactly
///              at 7 + (file_count + 1) * 18
/// </code>
///
/// <para>All six shipping archives satisfy: directory offsets strictly
/// non-decreasing, first offset == end of directory, sentinel offset == file
/// size, sentinel flag 0x00, no gaps and no padding between payloads.  So a
/// rebuild that preserves entry ORDER and copies untouched payloads VERBATIM is
/// byte-identical to the original — that is the V0 proof
/// (--selftest-editor stage A: 6/6).</para>
///
/// <para><b>Verbatim-copy doctrine.</b>  An asset the editor did not touch is
/// copied as raw STORED bytes (still compressed / still .pic).  It is never
/// decoded and re-encoded, so no round-trip risk is introduced by editing an
/// unrelated asset, and mods stay diff-minimal.  Only a modified asset is
/// re-encoded, and only flag 0x01 (LZSS) can be re-encoded from a decoded body;
/// flag 0x03 (.pic) modification requires the caller to supply already-encoded
/// container bytes (no .pic encoder exists yet).</para>
/// </summary>
public static class EaLibWriter
{
    public const int HeaderSize = 7;
    public const int DirRecordSize = 18;
    public const int NameFieldSize = 13;

    // -----------------------------------------------------------------------
    // ENGINE CAPACITY WALL.
    //
    // The game allocates its EALIB directory read buffer EXACTLY ONCE, at boot,
    // and reuses it for whichever archive is currently open:
    //
    //   ealib_boot_init_or_abort image@0x22E35: `mov ax, 0x48`  (max_asset_idx = 72)
    //     -> ealib_lib_state_init image@0x2EDBA:
    //          image@0x2EE08 `mov cx, 0x12`      (18 = on-disk dir record)
    //          image@0x2EE0B `mov ax,[bp+0x12]`  (max_asset_idx)
    //          image@0x2EE0E `inc ax` / image@0x2EE0F `mul cx`
    //          image@0x2EE11 far_heap_alloc((72+1) * 18 = 1314 B)
    //          -> g_lib_dirbuf_seg [0xF256]
    //
    // and every archive OPEN reads the whole on-disk directory into it:
    //
    //   ealib_locate_on_drive image@0x2F697..0x2F6A1:
    //          fs_read((g_lib_dir_entry_count [0xF274] + 1) * 18, g_lib_dirbuf, ...)
    //
    // So an archive with more than 72 members overruns a far-heap block by
    // (members - 72) * 18 bytes at open time.  Shipping head-room: 1a.lib is the
    // biggest at 70 members (71 records = 1278 B) — 2 spare slots; 2b.lib has 66
    // (67 records = 1206 B) — 6 spare slots.  The writer refuses to emit an
    // archive the engine cannot open.
    // -----------------------------------------------------------------------

    /// <summary>Directory records (members + sentinel) the engine's dirbuf holds — image@0x22E35.</summary>
    public const int EngineMaxDirRecords = 73;

    /// <summary>Bytes the engine allocates for the shared directory buffer — image@0x2EE0F/0x2EE11.</summary>
    public const int EngineDirBufBytes = EngineMaxDirRecords * DirRecordSize;   // 1314

    /// <summary>Members one archive may hold before the engine's dirbuf overruns.</summary>
    public const int EngineMaxMembers = EngineMaxDirRecords - 1;                // 72

    /// <summary>
    /// One archive member as it will be written: the raw 13-byte name field,
    /// the encoding flag, and the exact STORED payload bytes (for flag 0x01
    /// that includes the 4-byte u32-LE decompressed-size header).
    /// </summary>
    public sealed record Item(byte[] NameField, EncodingFlag Encoding, ReadOnlyMemory<byte> StoredBytes)
    {
        public string DisplayName
        {
            get
            {
                int n = 0;
                while (n < NameField.Length && NameField[n] != 0) n++;
                return n == 0 ? "(unnamed)" : System.Text.Encoding.ASCII.GetString(NameField, 0, n);
            }
        }
    }

    /// <summary>Copy an existing entry unchanged — name field, flag and stored bytes.</summary>
    public static Item Verbatim(EaLibEntry entry) =>
        new(entry.NameRawBytes.ToArray(), entry.Encoding, entry.RawBytes);

    /// <summary>Build the 13-byte NUL-padded name field from a string.</summary>
    public static byte[] MakeNameField(string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name);
        if (bytes.Length > NameFieldSize)
            throw new InvalidDataException(
                $"EALIB name \"{name}\" is {bytes.Length} B; the directory field is {NameFieldSize} B");
        byte[] field = new byte[NameFieldSize];
        bytes.CopyTo(field, 0);
        return field;
    }

    /// <summary>
    /// A replacement member built from a DECODED body.  flag 0x01 is
    /// re-compressed with <see cref="LzssCompressor"/> (plus the u32-LE size
    /// header); flag 0x00 stores the body as-is.  flag 0x03 (.pic) is rejected —
    /// pass pre-encoded container bytes through <see cref="Replacement"/> instead.
    /// </summary>
    public static Item FromDecoded(string name, EncodingFlag flag, ReadOnlySpan<byte> decoded)
    {
        byte[] stored = flag switch
        {
            EncodingFlag.Compressed => LzssCompressor.CompressAsset(decoded),
            EncodingFlag.Raw => decoded.ToArray(),
            _ => throw new NotSupportedException(
                $"cannot re-encode a decoded body for encoding flag {flag} " +
                "(no .pic encoder exists; supply pre-encoded bytes via Replacement)"),
        };
        return new Item(MakeNameField(name), flag, stored);
    }

    /// <summary>A replacement member whose STORED bytes the caller already has.</summary>
    public static Item Replacement(string name, EncodingFlag flag, ReadOnlyMemory<byte> stored) =>
        new(MakeNameField(name), flag, stored);

    /// <summary>Serialise a member list into a complete EALIB archive image.</summary>
    public static byte[] Build(IReadOnlyList<Item> items)
    {
        if (items.Count > ushort.MaxValue)
            throw new InvalidDataException($"EALIB holds at most {ushort.MaxValue} members");
        if (items.Count > EngineMaxMembers)
            throw new InvalidDataException(
                $"EALIB directory would hold {items.Count} members ({(items.Count + 1) * DirRecordSize} B); " +
                $"the engine's shared directory buffer is {EngineDirBufBytes} B = {EngineMaxMembers} members " +
                "max (allocated once at boot from max_asset_idx=0x48, image@0x22E35 -> image@0x2EE0F; " +
                "the whole directory is read into it at image@0x2F697). Emitting this archive would " +
                "overrun a far-heap block when the game opens it.");
        // NB: NO duplicate-name check here — 2a.lib ships one ("90_horiz.msk" at
        // slots 20 and 21), so a global guard would break the V0 zero-edit
        // re-emit.  Uniqueness is enforced on the ADD path instead
        // (see the additions overload of Rebuild).

        int dirBytes = (items.Count + 1) * DirRecordSize;
        int payloadBytes = 0;
        foreach (Item it in items) payloadBytes += it.StoredBytes.Length;
        int total = HeaderSize + dirBytes + payloadBytes;

        byte[] outBuf = new byte[total];
        "EALIB"u8.CopyTo(outBuf);
        outBuf[5] = (byte)items.Count;
        outBuf[6] = (byte)(items.Count >> 8);

        int off = HeaderSize + dirBytes;
        for (int i = 0; i < items.Count; i++)
        {
            Item it = items[i];
            int rec = HeaderSize + i * DirRecordSize;
            if (it.NameField.Length != NameFieldSize)
                throw new InvalidDataException($"member {i}: name field must be {NameFieldSize} B");
            it.NameField.CopyTo(outBuf, rec);
            outBuf[rec + 13] = (byte)it.Encoding;
            WriteU32(outBuf, rec + 14, off);
            it.StoredBytes.Span.CopyTo(outBuf.AsSpan(off));
            off += it.StoredBytes.Length;
        }

        // Sentinel: zero name, flag 0x00, offset = archive size.
        int sentinel = HeaderSize + items.Count * DirRecordSize;
        WriteU32(outBuf, sentinel + 14, total);

        if (off != total) throw new InvalidOperationException("EALIB writer: payload accounting error");
        return outBuf;
    }

    /// <summary>
    /// Rebuild <paramref name="source"/>, substituting the named members.
    /// Every member NOT named in <paramref name="replacements"/> is copied
    /// byte-verbatim, so with an empty dictionary the result is byte-identical
    /// to the source archive (V0).  Lookup is case-insensitive because
    /// scenario.bin stores .S names lower-case while the directory holds them
    /// upper-case.
    /// </summary>
    public static byte[] Rebuild(
        EaLibArchive source,
        IReadOnlyDictionary<string, byte[]>? replacements = null) =>
        Rebuild(source, replacements, null);

    /// <summary>
    /// <see cref="Rebuild(EaLibArchive, IReadOnlyDictionary{string, byte[]}?)"/>
    /// plus NEW members.
    ///
    /// <para><b>Additions are APPENDED, never inserted.</b>  Every existing
    /// member keeps its directory slot index, which matters because the engine
    /// resolves a name to an index once (<c>ealib_find_or_load_by_name</c>
    /// image@0x2F3D3) and then indexes <c>g_lib_dirbuf</c> by that number
    /// (<c>entry = g_lib_dirbuf + si*0x12</c>, ealib_load_asset image@0x2EE74);
    /// appending makes the "everything before it is byte-verbatim" invariant
    /// hold for the DIRECTORY as well as the payloads.  Stored offsets of course
    /// all shift by <c>18 × additions</c> — that is the whole diff.</para>
    ///
    /// <para>New names must be unique case-insensitively: name lookup is a
    /// first-match scan with an inline tolower (<c>ealib_strcasecmp_far</c>
    /// image@0x2F9BA), so a second same-named member would be unreachable.
    /// (The shipping 2a.lib does carry one such shadowed pair, "90_horiz.msk"
    /// ×2 — evidence of the behaviour, not a licence to add more.)</para>
    /// </summary>
    public static byte[] Rebuild(
        EaLibArchive source,
        IReadOnlyDictionary<string, byte[]>? replacements,
        IReadOnlyList<Item>? additions)
    {
        List<Item> items = new List<Item>(source.Entries.Count + (additions?.Count ?? 0));
        HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (EaLibEntry e in source.Entries)
        {
            if (replacements is not null &&
                replacements.TryGetValue(e.Name, out byte[]? decoded))
            {
                used.Add(e.Name);
                items.Add(new Item(e.NameRawBytes.ToArray(), e.Encoding,
                                   Encode(e.Encoding, decoded)));
            }
            else
            {
                items.Add(Verbatim(e));
            }
        }
        if (replacements is not null)
            foreach (string k in replacements.Keys)
                if (!used.Contains(k))
                    throw new InvalidDataException(
                        $"{source.ShortName}: replacement target \"{k}\" is not a member of the archive");

        if (additions is not null && additions.Count > 0)
        {
            HashSet<string> existing = new HashSet<string>(
                source.Entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            foreach (Item add in additions)
            {
                string n = add.DisplayName;
                if (n.Length == 0 || n == "(unnamed)")
                    throw new InvalidDataException($"{source.ShortName}: a new member must have a name");
                if (!existing.Add(n))
                    throw new InvalidDataException(
                        $"{source.ShortName}: a member named \"{n}\" already exists — EALIB name " +
                        "lookup is case-insensitive first-match (image@0x2F9BA), so the new member " +
                        "would be unreachable");
                items.Add(add);
            }
        }
        return Build(items);
    }

    private static byte[] Encode(EncodingFlag flag, byte[] decoded) => flag switch
    {
        EncodingFlag.Compressed => LzssCompressor.CompressAsset(decoded),
        EncodingFlag.Raw => decoded,
        _ => throw new NotSupportedException(
            $"cannot re-encode a decoded body for encoding flag {flag}"),
    };

    private static void WriteU32(byte[] buf, int off, int value)
    {
        buf[off] = (byte)value;
        buf[off + 1] = (byte)(value >> 8);
        buf[off + 2] = (byte)(value >> 16);
        buf[off + 3] = (byte)(value >> 24);
    }
}
