using System.Text;

namespace CYAC.Formats.EaLib;

// ---------------------------------------------------------------------------
// `.S` / `.W` DATA-SECTION SYNTHESIZER — extract() / synth().
//
// PROOF STANDARD: for all 51 `.S` and all
// 3 `.W`, Synth(Extract(body)) must equal the shipping body BYTE FOR BYTE, with
// zero per-mission special cases (`--selftest-datasynth`, stage D-A).  Every
// framing field is DERIVED here:
//
//   header table_off   = the file offset of the trailer block's u16 size word
//                        (recomputed from the emitted data-section length)
//   header sec1_count  = model.Sites.Count
//   name-table counts  = the emitted name blob
//   script length word = the payload length
//
// so the model can only carry content, and a content edit can never leave a
// stale offset behind.
// ---------------------------------------------------------------------------
public static class SDataSynthesizer
{
    // =======================================================================
    // extract: parsed container -> authoring model
    // =======================================================================

    public static SDataModel Extract(byte[] body, string name = "?") =>
        Extract(SMissionDecoder.Parse(body, name), name);

    public static SDataModel Extract(SMissionDecoder.SFile f, string name = "?")
    {
        if (f.Notes.Count > 0)
            throw new SDataException(
                $"{name}: parser notes present, refusing to model: {string.Join("; ", f.Notes)}");

        SDataModel m = new SDataModel
        {
            Name = name,
            Kind = name.EndsWith(".W", StringComparison.OrdinalIgnoreCase) ? "W" : "S",
        };

        foreach (SMissionDecoder.Sec1Record r in f.Sec1Records)
            m.Sites.Add(new SDataSite
            {
                Type = r.Type,
                X = Units(r.CoordA),
                Z = Units(r.CoordB),
                Subtype = r.Subtype,
            });

        foreach (byte[] n in f.Names) m.Names.Add(SDataText.FromBytes(n));

        foreach (object item in f.Items)
        {
            if (item is SMissionDecoder.Directive d)
            {
                SDataDirective jd = new SDataDirective { Directive = SDataModel.DirectiveNames[d.Tag] };
                if (d.Kind == SMissionDecoder.OperandKind.Coords4)
                    jd.Coords = d.Coords!.Select(Units).ToArray();
                else if (d.Kind != SMissionDecoder.OperandKind.None)
                    jd.Value = d.Scalar;
                m.Stream.Add(jd);
                continue;
            }

            SMissionDecoder.SObject it = (SMissionDecoder.SObject)item;
            SDataObject o = new SDataObject
            {
                Object = it.OpenerTag switch
                {
                    0x02 => "marker",
                    0x03 => "named_mesh",
                    0x04 => "nav_waypoint",
                    0x05 => "ground_fx",
                    0x19 => "prim_4d00",
                    _ => "class",
                },
                Pos = PosFrom(it.Pos),
            };
            if (o.Object == "class") o.ClassId = it.OpenerTag;
            if (it.OpenerTag == 0x04) o.NavSlot = it.OpenerByte!.Value;
            if (it.OpenerNameBytes is not null) o.Label = SDataText.FromBytes(it.OpenerNameBytes);

            foreach (SMissionDecoder.Attr a in it.Attrs)
            {
                SDataAttr attr = new SDataAttr { Attr = SDataModel.AttrNames[a.Tag] };
                switch (a.Kind)
                {
                    case SMissionDecoder.OperandKind.Script: attr.Script = a.Payload!; break;
                    case SMissionDecoder.OperandKind.String: attr.Text = SDataText.FromBytes(a.Payload!); break;
                    case SMissionDecoder.OperandKind.Word3: attr.Words = a.Words!.ToArray(); break;
                    case SMissionDecoder.OperandKind.None: break;
                    default: attr.Value = a.Scalar; break;
                }
                o.Attrs.Add(attr);
            }
            m.Stream.Add(o);
        }

        foreach (SMissionDecoder.TrailerItem t in f.Trailer)
            m.Trailer.Add(t.IsBlock
                ? new SDataTrailerItem { Module = t.Payload! }
                : new SDataTrailerItem { Skip = t.SkipByte });
        return m;
    }

    private static SDataPos PosFrom(SMissionDecoder.Position p) => p.Tag switch
    {
        0 or 4 or 5 => new SDataPos { Pos = SDataModel.PosNames[p.Tag], Coords = p.Coords.Select(Units).ToArray() },
        1 => new SDataPos { Pos = "ground", Coords = p.Coords.Select(Units).ToArray() },
        2 => new SDataPos { Pos = "at_site", SiteType = p.ByteArg },
        3 => new SDataPos
        {
            Pos = "era_matrix",
            Rows = Enumerable.Range(0, p.Coords.Length / 3)
                .Select(i => new[] { Units(p.Coords[i * 3]), Units(p.Coords[i * 3 + 1]), Units(p.Coords[i * 3 + 2]) })
                .ToList(),
        },
        6 => new SDataPos { Pos = "rel_place", Place = p.ByteArg, Coords = p.Coords.Select(Units).ToArray() },
        7 => new SDataPos { Pos = "track_actors", Slots = p.Compact!.Select(b => (int)b).ToArray() },
        _ => throw new SDataException($"unknown position tag {p.Tag}"),
    };

    /// <summary>i24 stream value (&lt;&lt;8, low byte 0 by grammar) -> world units, exact.</summary>
    private static int Units(int v)
    {
        if ((v & 0xFF) != 0)
            throw new SDataException($"i24 value 0x{unchecked((uint)v):X8} has a nonzero low byte");
        return v >> 8;
    }

    private static int FromUnits(int u)
    {
        if (u < SDataModel.WorldUnitMin || u > SDataModel.WorldUnitMax)
            throw new SDataException($"coordinate {u} outside the i24 world-unit range");
        return u << 8;
    }

    // =======================================================================
    // synth: authoring model -> container bytes
    // =======================================================================

    public static byte[] Synth(SDataModel model) => Synth(model, out _);

    public static byte[] Synth(SDataModel model, out List<string> warnings)
    {
        warnings = new List<string>();
        return Synth(model, warnings);
    }

    public static byte[] Synth(SDataModel model, List<string> warn)
    {
        if (model.Format != SDataModel.FormatTag)
            throw new SDataException($"not a {SDataModel.FormatTag} document");

        // ---- section 1 -----------------------------------------------------
        List<byte> sec1 = new List<byte>();
        foreach (SDataSite s in model.Sites)
        {
            if (s.Type is < 0 or > 8)
                throw new SDataException($"site type {s.Type} outside the grammar (0..8)");
            sec1.Add((byte)s.Type);
            EmitI24(sec1, FromUnits(s.X));
            EmitI24(sec1, FromUnits(s.Z));
            if (s.Type is >= 1 and <= 7)
                sec1.Add((byte)(s.Subtype ?? throw new SDataException($"site type {s.Type} needs a subtype byte")));
            else if (s.Subtype is not null)
                throw new SDataException($"site type {s.Type} carries no subtype byte");
        }
        sec1.Add(0xFF);

        // ---- name table (framing derived) ----------------------------------
        List<byte> nameBlob = new List<byte>();
        foreach (SDataText n in model.Names) nameBlob.AddRange(n.ToBytes());
        List<byte> nametab = new List<byte>();
        EmitU16(nametab, model.Names.Count);
        EmitU16(nametab, nameBlob.Count);
        nametab.AddRange(nameBlob);
        nametab.Add(0xFF);

        // ---- object / directive stream --------------------------------------
        List<byte> stream = new List<byte>();
        HashSet<int> registered = new HashSet<int>();
        int? subtypeMax = null;
        foreach (SDataItem item in model.Stream)
        {
            if (item is SDataDirective d)
            {
                byte tag = d.Tag;
                stream.Add(tag);
                switch (d.Kind)
                {
                    case SMissionDecoder.OperandKind.Byte:
                        stream.Add((byte)d.Value);
                        if (tag == 0x83) subtypeMax = d.Value;
                        break;
                    case SMissionDecoder.OperandKind.Word: EmitU16(stream, d.Value); break;
                    case SMissionDecoder.OperandKind.Coords4:
                        foreach (int v in d.Coords!) EmitI24(stream, FromUnits(v));
                        break;
                }
                continue;
            }

            SDataObject o = (SDataObject)item;
            switch (o.Object)
            {
                case "marker": stream.Add(0x02); break;
                case "named_mesh":
                    stream.Add(0x03);
                    stream.AddRange(o.Label!.ToBytes());
                    break;
                case "nav_waypoint":
                    if (o.NavSlot > SDataModel.MaxNavSlot)
                        warn.Add($"nav_slot {o.NavSlot} > {SDataModel.MaxNavSlot} (shipped range 0..2; " +
                                 "[0xB564] capacity beyond 3 unproven)");
                    stream.Add(0x04);
                    stream.Add((byte)o.NavSlot);
                    stream.AddRange(o.Label!.ToBytes());
                    break;
                case "ground_fx": stream.Add(0x05); break;
                case "prim_4d00": stream.Add(0x19); break;
                case "class":
                {
                    int c = o.ClassId;
                    if (c is 0x02 or 0x80 or 0xFF
                        || SMissionDecoder.OpenerTags.Contains((byte)c)
                        || (c is >= 0x81 and <= 0x9F && SMissionDecoder.DirectiveOperands.ContainsKey((byte)c)))
                        throw new SDataException($"class 0x{c:X2} collides with a grammar tag");
                    if (c is 2 or 3 or 4 or 5 or 25)
                        warn.Add($"class {c} is a flag-2 class-table slot — the lookup @0x24058 returns " +
                                 "no prototype (unusable)");
                    stream.Add((byte)c);
                    break;
                }
                default:
                    throw new SDataException($"unknown object kind '{o.Object}'");
            }

            int? slot = null;
            foreach (SDataAttr a in o.Attrs)
            {
                byte tag = a.Tag;
                stream.Add(tag);
                if (tag == 0x89)
                {
                    byte[] blob = a.Script ?? throw new SDataException("ai_script attr carries no payload");
                    if (blob.Length > SDataModel.MaxScriptBytes)
                        throw new SDataException(
                            $"ai_script {blob.Length} B > {SDataModel.MaxScriptBytes} " +
                            "(parser staging buffer [bp-0x2C8])");
                    foreach (SDataModel.ScriptStep _ in SDataModel.ScriptWalk(blob)) { }   // validates patcher-safety
                    EmitU16(stream, blob.Length);
                    stream.AddRange(blob);
                    continue;
                }
                switch (a.Kind)
                {
                    case SMissionDecoder.OperandKind.Byte:
                        stream.Add((byte)a.Value);
                        if (tag == 0x86)
                        {
                            slot = a.Value;
                            if (a.Value > SDataModel.MaxActorSlot)
                                warn.Add($"actor_slot {a.Value} > {SDataModel.MaxActorSlot} " +
                                         "([0xEE5A] is u16[13])");
                        }
                        break;
                    case SMissionDecoder.OperandKind.Word: EmitU16(stream, a.Value); break;
                    case SMissionDecoder.OperandKind.Word3:
                        foreach (int v in a.Words!) EmitU16(stream, v);
                        break;
                    case SMissionDecoder.OperandKind.String:
                        stream.AddRange(a.Text!.ToBytes());
                        break;
                    // flag attrs: the tag byte alone
                }
            }

            SDataPos p = o.Pos;
            byte ptag = p.Tag;
            stream.Add(ptag);
            switch (ptag)
            {
                case 0: case 4: case 5:
                    Need(p.Coords, 3, "position needs x,y,z");
                    foreach (int v in p.Coords) EmitI24(stream, FromUnits(v));
                    break;
                case 1:
                    Need(p.Coords, 2, "ground position needs x,z");
                    foreach (int v in p.Coords) EmitI24(stream, FromUnits(v));
                    break;
                case 2:
                    if (p.SiteType is not (1 or 2 or 6))
                        warn.Add($"at_site type {p.SiteType} not populated by any shipped .W " +
                                 "(types 1/2/6) — engine aborts on an empty type");
                    stream.Add((byte)p.SiteType);
                    break;
                case 3:
                {
                    if (subtypeMax is null)
                        throw new SDataException(
                            "era_matrix position without a prior subtype_max directive (0x83)");
                    List<int[]> rows = p.Rows ?? throw new SDataException("era_matrix position has no rows");
                    if (rows.Count != 3 * subtypeMax.Value)
                        throw new SDataException(
                            $"era_matrix needs 3*subtype_max={3 * subtypeMax.Value} rows, got {rows.Count}");
                    foreach (int[] row in rows)
                        foreach (int v in row) EmitI24(stream, FromUnits(v));
                    break;
                }
                case 6:
                    if (!registered.Contains(p.Place))
                        warn.Add($"rel_place ref {p.Place} not registered by an earlier object " +
                                 "(engine would read stale coords)");
                    stream.Add((byte)p.Place);
                    Need(p.Coords, 3, "rel_place needs x,y,z");
                    foreach (int v in p.Coords) EmitI24(stream, FromUnits(v));
                    break;
                case 7:
                {
                    int[] slots = p.Slots ?? throw new SDataException("track_actors needs 3 slot bytes");
                    if (slots.Length != 3)
                        throw new SDataException("track_actors needs exactly 3 slot bytes (0xFF = unused)");
                    foreach (int v in slots) stream.Add((byte)v);
                    break;
                }
            }
            if (slot is not null) registered.Add(slot.Value);
        }
        stream.Add(0xFF);

        // ---- trailer + derived header ---------------------------------------
        List<byte> trailer = new List<byte>();
        int tableOff = 0;
        int preLen = 8 + sec1.Count + nametab.Count + stream.Count;
        foreach (SDataTrailerItem t in model.Trailer)
        {
            if (t.Module is not null)
            {
                if (tableOff == 0) tableOff = preLen + trailer.Count + 1;  // offset of the size word
                trailer.Add(0x00);
                EmitU16(trailer, t.Module.Length);
                trailer.AddRange(t.Module);
            }
            else trailer.Add((byte)t.Skip);
        }
        trailer.Add(0xFF);

        List<byte> outBytes = new List<byte>(preLen + trailer.Count);
        outBytes.AddRange(SMissionDecoder.Magic);
        EmitU16(outBytes, tableOff);
        EmitU16(outBytes, model.Sites.Count);
        outBytes.AddRange(sec1);
        outBytes.AddRange(nametab);
        outBytes.AddRange(stream);
        outBytes.AddRange(trailer);
        return outBytes.ToArray();

        static void Need(int[] c, int n, string what)
        {
            if (c.Length != n) throw new SDataException($"{what} (got {c.Length} coordinate(s))");
        }
    }

    private static void EmitU16(List<byte> o, int v)
    {
        o.Add((byte)(v & 0xFF));
        o.Add((byte)((v >> 8) & 0xFF));
    }

    private static void EmitI24(List<byte> o, int v)
    {
        uint u = unchecked((uint)v);
        if ((u & 0xFF) != 0)
            throw new SDataException($"i24 value 0x{u:X8} has a nonzero low byte — not representable");
        o.Add((byte)((u >> 8) & 0xFF));
        o.Add((byte)((u >> 16) & 0xFF));
        o.Add((byte)((u >> 24) & 0xFF));
    }

    // =======================================================================
    // structural validation of a MODEL (engine invariants, mission-independent)
    // =======================================================================

    /// <summary>
    /// Every structural problem with a `.S` content model, in human-readable
    /// form (empty = it will synthesize and the engine's parser will accept it).
    /// These are the walls the engine itself imposes — slot capacity, backward-
    /// only place references, script caps, exactly one player and one home base
    /// — NOT taste.
    ///
    /// <para><paramref name="warnings"/> collects the ADVISORIES: shapes the
    /// shipping data itself uses that an author still wants pointed out (a
    /// marker's slot taken over by a later object — HEAD/OYSTER/RAMROD do
    /// exactly that — an `at_site` type no `.W` populates, a nav slot past the
    /// proven range).  Nothing in there blocks an export; every one of them is
    /// calibrated against the corpus, so the 54 shipping assets validate with
    /// ZERO errors (selftest stage D-C).</para>
    /// </summary>
    /// <param name="m">The model.</param>
    /// <param name="warnings">Collects the advisories, when given.</param>
    /// <param name="className">The name of a class id for the messages, or null to print the id.</param>
    public static List<string> ValidateModel(SDataModel m, List<string>? warnings = null,
                                             Func<int, string?>? className = null)
    {
        List<string> errs = new List<string>();
        List<string> warn = warnings ?? new List<string>();

        // ---- synthesize: the grammar-level refusals + advisories -------------
        try { Synth(m, warn); }
        catch (SDataException ex) { errs.Add(ex.Message); }

        // ---- slots -----------------------------------------------------------
        Dictionary<int, SDataObject> seen = new Dictionary<int, SDataObject>();
        foreach (SDataObject o in m.Objects)
        {
            SDataAttr? slotAttr = o.Attr("actor_slot");
            if (slotAttr is null) continue;
            int s = slotAttr.Value;
            if (s > SDataModel.MaxActorSlot)
                errs.Add($"actor slot {s} is out of range — [0xEE5A] is u16[{SDataModel.SlotBudget}], " +
                         $"so slots run 0..{SDataModel.MaxActorSlot}");
            if (seen.TryGetValue(s, out SDataObject? prev))
            {
                // Re-registration is last-writer-wins.  Shipped 3x as
                // marker-then-object (HEAD 10, OYSTER 9, RAMROD 0): the marker
                // defines the PLACE that earlier objects/scripts hang off, then a
                // real object takes the slot over for the rules.  Two SPAWNING
                // objects on one slot is a different animal — the first one
                // becomes unaddressable by every rule, script and placement.
                string msg = $"slot {s} is registered twice ({Describe(prev, className)}, then {Describe(o, className)}) — " +
                             "the engine takes the LAST writer";
                if (Spawns(prev) && Spawns(o))
                    errs.Add(msg + ", so the first object drops out of every rule, script and " +
                             "placement that names that slot");
                else
                    warn.Add(msg + " (the shipped marker-then-object idiom: HEAD/OYSTER/RAMROD)");
            }
            seen[s] = o;
        }
        int used = m.Objects.Count(o => o.HasAttr("actor_slot"));
        if (used > SDataModel.SlotBudget)
            errs.Add($"{used} objects carry a slot but only {SDataModel.SlotBudget} exist (0..12)");

        // ---- placements + scripts (order is semantic: refs must be BACKWARD) --
        HashSet<int> live = new HashSet<int>();
        foreach (SDataObject o in m.Objects)
        {
            string what = Describe(o, className);
            if (o.Pos.Pos == "rel_place" && !live.Contains(o.Pos.Place))
                errs.Add($"{what} is placed relative to slot {o.Pos.Place}, which no EARLIER object " +
                         "registered — the engine would read stale coordinates from [0xEE74]");
            if (o.Pos.Pos == "track_actors")
                foreach (int s in o.Pos.Slots!)
                    if (s != 0xFF && !live.Contains(s) && !m.Objects.Any(x => x.Attr("actor_slot")?.Value == s))
                        errs.Add($"{what} tracks actor slot {s}, which no object registers");
            SDataAttr? script = o.Attr("ai_script");
            if (script is not null)
            {
                string? e = SDataModel.ValidateScript(script.Script!);
                if (e is not null) errs.Add($"{what}: {e}");
                else
                {
                    (List<int> places, List<int> actors) = SDataModel.ScriptRefs(script.Script!);
                    // An object's script may name its OWN slot: the script is
                    // committed with the object (image@0x0A22F) and the slot is
                    // registered in the same close (image@0x0A2FA).  ESCORT.S's
                    // B-17 lead ships exactly that.
                    if (o.Attr("actor_slot") is { } own) live.Add(own.Value);
                    foreach (int pl in places)
                        if (!live.Contains(pl))
                            errs.Add($"{what}: its AI script references place {pl}, which is not " +
                                     "registered yet at this point in the stream (scripts are patched " +
                                     "at parse time — the reference would resolve to stale coordinates)");
                    foreach (int ac in actors)
                        if (!m.Objects.Any(x => x.Attr("actor_slot")?.Value == ac))
                            errs.Add($"{what}: its AI script references actor slot {ac}, which no " +
                                     "object registers");
                }
            }
            if (o.Attr("actor_slot") is { } sa) live.Add(sa.Value);
        }

        // ---- the .S prologue invariants --------------------------------------
        if (m.Kind == "S")
        {
            int players = m.Objects.Count(o => o.Object == "class" && o.ClassId == 0);
            int bases = m.Objects.Count(o => o.Object == "class" && o.ClassId == 1);
            if (players != 1) errs.Add($"a mission needs exactly one class-0 PLAYER object (found {players})");
            if (bases != 1) errs.Add($"a mission needs exactly one class-1 HOME-BASE-POS object (found {bases})");
            List<SDataObject> navs = m.Objects.Where(o => o.Object == "nav_waypoint").ToList();
            if (navs.Count > SDataModel.MaxNavSlot + 1)
                errs.Add($"{navs.Count} nav waypoints — the record array [0xB564] is proven only for " +
                         $"slots 0..{SDataModel.MaxNavSlot}");
            foreach (IGrouping<int, SDataObject> g in navs.GroupBy(n => n.NavSlot).Where(g => g.Count() > 1))
                errs.Add($"nav slot {g.Key} is used by {g.Count()} waypoints — the later one wins");
        }
        return errs;
    }

    /// <summary>
    /// Does this object put a craft in the world?  Class ids 6..24 spawn through
    /// <c>spawn_dispatch_object</c> @0x06F33; markers, waypoints and named
    /// meshes only define PLACES (opener 0x02 falls to image@0x0A2AC with
    /// prim == 0 = registration only).
    /// </summary>
    public static bool Spawns(SDataObject o) => o.Object == "class" && o.ClassId >= 6;

    /// <summary>A short human label for an object (a class name, its slot and its pilot's name).</summary>
    /// <param name="o">The object.</param>
    /// <param name="className">The name of a class id, or null to print the id.</param>
    public static string Describe(SDataObject o, Func<int, string?>? className = null)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(o.Object switch
        {
            "class" => className?.Invoke(o.ClassId) ?? $"class {o.ClassId}",
            "nav_waypoint" => $"waypoint \"{o.Label?.Display}\"",
            "named_mesh" => $"mesh \"{o.Label?.Display}\"",
            _ => o.Object,
        });
        if (o.Attr("actor_slot") is { } s) sb.Append($" (slot {s.Value})");
        if (o.Attr("pilot_name")?.Text?.Text is { } n) sb.Append($" \"{n}\"");
        return sb.ToString();
    }
}
