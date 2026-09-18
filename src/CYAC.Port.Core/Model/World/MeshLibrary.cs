using CYAC.Port.Core.Data;
using CYAC.Port.Core.Markings;
using CYAC.Port.Core.Model.Mission;

namespace CYAC.Port.Core.Model.World;

/// <summary>
/// The port's mesh reader: assembles a <see cref="MeshModel"/> from the two halves of the
/// transformed data tree, and caches one per basename.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two halves.</b>  <c>exe/meshes/&lt;name&gt;.json</c> carries the class's registry slot and,
/// per LOD, the 14-byte face descriptor and the opcode-dispatched SHAPE RECORDS (which hold vertex
/// INDICES, a tag and a colour).  <c>meshes/&lt;name&gt;.json</c> carries the <c>.PNT</c>'s vertex
/// ARRAYS and a per-record COLOUR STREAM.  A LOD whose descriptor's <c>+0x06</c> far pointer named
/// an in-image vertex array carries those vertices in the executable document instead
/// (<c>bridge</c>, <c>build</c>, <c>revet</c>, <c>hangar</c>, <c>hedge</c>, <c>sam</c>).
/// </para>
/// <para>
/// <b>Colour.</b>  Where the <c>.PNT</c> has a colour stream for the LOD, it WINS:
/// <c>pnt_sub_color_patcher @image@0x15C09</c> overwrites the descriptor's records in record order,
/// which is exactly why the stream's length equals the descriptor's <c>recordCount</c> (it does, for
/// all 112 populated LODs in the shipped tree).  A LOD without one keeps the record's own byte.
/// </para>
/// <para>
/// This is a READER over the tree's documents, not a port of <c>CYAC.Formats/Mesh/MeshDecoder.cs</c>:
/// <c>CYAC.Port.Core</c> has no project references (the data-tree rule), and the decoder's
/// 110/110-per-LOD proof is what makes the DOCUMENTS trustworthy, so the port reads the documents.
/// </para>
/// </remarks>
public sealed class MeshLibrary
{
    private readonly DataTree _tree;
    private readonly Dictionary<string, MeshModel> _byBasename = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>Opens a library over a data tree.</summary>
    /// <param name="tree">The transformed tree.</param>
    public MeshLibrary(DataTree tree)
        : this(tree, MeshConditioning.Default)
    {
    }

    /// <summary>Opens the library with explicit conditioning options.</summary>
    /// <param name="tree">The transformed data tree.</param>
    /// <param name="conditioning">the asset-conditioning options (decal lift).</param>
    public MeshLibrary(DataTree tree, MeshConditioning conditioning)
    {
        ArgumentNullException.ThrowIfNull(tree);
        _tree = tree;
        _conditioning = conditioning;
    }

    private readonly MeshConditioning _conditioning;

    /// <summary>Every decal the conditioning pass found so far, per class and LOD (a census, taken with the lift off
    /// too).</summary>
    public IReadOnlyList<(string Class, int Lod, DecalConditioning.Decal Decal)> Decals => _decals;

    private readonly List<(string Class, int Lod, DecalConditioning.Decal Decal)> _decals = [];

    /// <summary>
    /// The <see cref="SheetInflation"/> census: every zero-thickness sheet found in every class
    /// built so far, with what the pass generated for it (nothing when the pass is off).
    /// </summary>
    public IReadOnlyList<(string Class, int Lod, SheetInflation.Sheet Sheet)> Sheets => _sheets;

    private readonly List<(string Class, int Lod, SheetInflation.Sheet Sheet)> _sheets = [];

    /// <summary>Every VERTEX WELD the conditioning performed (<see cref="VertexWeld"/>).</summary>
    public IReadOnlyList<(string Class, int Lod, VertexWeld.Weld Weld)> Welds => _welds;

    private readonly List<(string Class, int Lod, VertexWeld.Weld Weld)> _welds = [];

    /// <summary>The mesh for a basename, built on first use and cached.</summary>
    /// <param name="basename">A mesh basename such as <c>strip</c> or <c>p51</c>.</param>
    /// <exception cref="DataTreeNotFoundException">The tree carries no such mesh.</exception>
    public MeshModel Get(string basename)
    {
        ArgumentNullException.ThrowIfNull(basename);
        lock (_gate)
        {
            if (_byBasename.TryGetValue(basename, out MeshModel? hit))
            {
                return hit;
            }

            MeshModel model = Build(
                basename, _tree.ExeMesh(basename), TryPnt(basename), _clamped, _disagreements,
                _conditioning, _decals, _sheets, _welds);
            _byBasename[basename] = model;
            return model;
        }
    }

    /// <summary>The mesh for a basename, or <see langword="null"/> when the tree has none.</summary>
    /// <param name="basename">A mesh basename.</param>
    public MeshModel? TryGet(string? basename) =>
        basename is not null && _tree.HasExeMesh(basename) ? Get(basename) : null;

    /// <summary>
    /// The mesh a mission/theater class id draws with, or <see langword="null"/> when the class has
    /// none (<c>MissionClassCatalog.MeshBasename</c> resolves the 19 scenery classes and the six
    /// flyable aircraft).
    /// </summary>
    /// <param name="classId">A class-table id.</param>
    public MeshModel? ForClass(int classId) => TryGet(MissionClassCatalog.MeshBasename(classId));

    /// <summary>Builds a model from a pair of already-parsed documents.</summary>
    /// <param name="basename">The mesh's basename.</param>
    /// <param name="executable">The <c>exe/meshes</c> document.</param>
    /// <param name="pnt">The <c>meshes</c> document, when the mesh has one.</param>
    /// <param name="clamped">
    /// H8 addendum — collects <c>(class, lod, components)</c> for every vertex block that had to be clamped
    /// into its class record's AABB; null discards the record.
    /// </param>
    /// <param name="disagreements">
    /// H8 addendum — collects <c>(class, lod, rows)</c> where the executable's inline vertex block disagrees
    /// with the <c>.PNT</c> block the port uses; null discards the record.
    /// </param>
    /// <exception cref="InvalidDataException">A document is missing a section the renderer needs.</exception>
    public static MeshModel Build(
        string basename,
        ExeMeshDocumentDto executable,
        PntMeshDocumentDto? pnt,
        ICollection<(string Class, int Lod, int Components)>? clamped = null,
        ICollection<(string Class, int Lod, int Rows)>? disagreements = null,
        MeshConditioning? conditioning = null,
        ICollection<(string Class, int Lod, DecalConditioning.Decal Decal)>? decals = null,
        ICollection<(string Class, int Lod, SheetInflation.Sheet Sheet)>? sheets = null,
        ICollection<(string Class, int Lod, VertexWeld.Weld Weld)>? welds = null)
    {
        ArgumentNullException.ThrowIfNull(basename);
        ArgumentNullException.ThrowIfNull(executable);
        MeshConditioning conditioningOptions = conditioning ?? MeshConditioning.Default;

        ExeMeshSlotDto slot = executable.Slot
                              ?? throw new InvalidDataException($"exe/meshes/{basename}.json carries no \"slot\"");
        List<ExeMeshLodDto> lodDocuments = executable.Lods
                                           ?? throw new InvalidDataException($"exe/meshes/{basename}.json carries no \"lods\"");

        int geometryBase = slot.GeometryBase is { } b ? PortHex.Parse(b) : 0;
        List<MeshLod> lods = new List<MeshLod>(lodDocuments.Count);
        foreach (ExeMeshLodDto lod in lodDocuments.OrderBy(l => l.Index))
        {
            MeshLod built = BuildLod(
                basename, lod, pnt, geometryBase, slot.Bounds, slot.ScaleShiftExponent,
                clamped, disagreements);

            // VECTOR MARKINGS — the shipped marking records a placement REPLACES are hidden BEFORE
            // inflation (their indices are the shipped ones), by emptying their index lists: both
            // passes skip a polygon with fewer than three indices and the renderer's EmitFace draws
            // nothing for it, while the paint-tree leaf lists keep their numbering. Under
            // MarkingsMode.Off every decal the census finds is hidden the same way.
            MarkingSet? markingSet = conditioningOptions.VectorMarkings
                ? conditioningOptions.MarkingLibrary!.TryGetPlacements(basename)
                : null;
            if (markingSet is not null || conditioningOptions.Markings == MarkingsMode.Off)
            {
                built = HideReplacedRecords(built, markingSet, conditioningOptions.Markings == MarkingsMode.Off);
            }

            // The VERTEX WELD first of all (VertexWeld): an un-welded corner is closed before
            // inflation and the lift, so both see the joined geometry.
            List<VertexWeld.Weld> foundWelds = new List<VertexWeld.Weld>();
            built = VertexWeld.Apply(built, conditioningOptions.VertexWeldModelUnits, foundWelds);
            if (welds is not null)
            {
                foreach (VertexWeld.Weld weld in foundWelds)
                {
                    welds.Add((basename, built.Index, weld));
                }
            }

            // SHEET INFLATION first: the zero-thickness wings, tailplanes, fins and gear doors
            // become solids with a section, and the markings on them are cloned to both skins.
            // Then the decal lift finds those clones already in front of their base.
            List<SheetInflation.Sheet> foundSheets = new List<SheetInflation.Sheet>();
            if (SheetInflation.AppliesTo(basename, PortHex.ParseOrDefault(slot.Flags), slot.GroundClearance))
            {
                built = SheetInflation.Apply(built, conditioningOptions.SheetsOrOff.ForClass(basename), foundSheets);
            }

            if (sheets is not null)
            {
                foreach (SheetInflation.Sheet sheet in foundSheets)
                {
                    sheets.Add((basename, built.Index, sheet));
                }
            }

            // The decal lift (DecalConditioning): markings painted on a surface get a small outward
            // lift so the depth test keeps them in front of it.
            List<DecalConditioning.Decal> found = new List<DecalConditioning.Decal>();
            built = DecalConditioning.Apply(built, conditioningOptions.DecalLiftModelUnits, found);
            if (decals is not null)
            {
                foreach (DecalConditioning.Decal decal in found)
                {
                    decals.Add((basename, built.Index, decal));
                }
            }

            lods.Add(built);
        }

        if (lods.Count == 0)
        {
            throw new InvalidDataException($"exe/meshes/{basename}.json has no populated LOD");
        }

        // The gear RULES name a LOD and its leaves; the vertex runs come from that LOD's own
        // articulation blocks (H6b: the transform publishes them, so no byte is transcribed here).
        GearArticulation? gear = null;
        if (GearArticulation.For(basename) is { } rules)
        {
            MeshLod gearLod = lods.FirstOrDefault(l => l.Index == rules.LodIndex)
                              ?? throw new InvalidDataException(
                                  $"{basename} has no LOD{rules.LodIndex}, which the gear rules name");
            gear = GearArticulation.Resolve(basename, gearLod);
        }

        if (gear is not null)
        {
            VerifyGear(basename, gear, lods, pnt);
        }

        // VECTOR MARKINGS — resolve the class's placements onto every LOD's final faces (after
        // inflation and the lift), skipping the gear articulation's vertex runs.
        if (conditioningOptions.VectorMarkings
            && conditioningOptions.MarkingLibrary!.TryGetPlacements(basename) is { } set)
        {
            for (int i = 0; i < lods.Count; i++)
            {
                IReadOnlyDictionary<int, SurfaceMarkingFrame[]> table = Markings.MarkingResolver.Resolve(lods[i], set, conditioningOptions.MarkingLibrary, gear);
                if (table.Count > 0)
                {
                    lods[i] = lods[i] with { Markings = table };
                }
            }
        }

        return new MeshModel(
            basename,
            slot.ScaleShiftExponent,
            (byte)PortHex.Parse(slot.RenderLayerPriority ?? "0x80"),
            slot.MeshExtent,
            slot.LodThresholds is { Count: > 0 } t ? [.. t] : [0, 0, 0],
            [.. lods],
            gear)
        {
            TargetPanelCameraDistanceSteps = slot.TargetPanelCameraDistanceSteps,
        };
    }

    /// <summary>
    /// VECTOR MARKINGS — hides shipped marking records by emptying their index lists (see the
    /// call site).  With <paramref name="all"/>, every record the decal census finds is hidden.
    /// </summary>
    private static MeshLod HideReplacedRecords(MeshLod lod, Markings.MarkingSet? set, bool all)
    {
        HashSet<int> hidden = new HashSet<int>();
        if (set is not null)
        {
            foreach (MarkingPlacement placement in set.Placements)
            {
                if (placement.Replaces is { } replaces && replaces.TryGetValue(lod.Index, out int[]? records))
                {
                    hidden.UnionWith(records);
                }
            }
        }

        if (all)
        {
            List<DecalConditioning.Decal> found = new List<DecalConditioning.Decal>();
            DecalConditioning.Apply(lod, 0.0, found);
            foreach (DecalConditioning.Decal decal in found)
            {
                hidden.Add(decal.Record);
            }
        }

        if (hidden.Count == 0)
        {
            return lod;
        }

        MeshFace[] faces = (MeshFace[])lod.Faces.Clone();
        foreach (int r in hidden)
        {
            if (r >= 0 && r < faces.Length)
            {
                faces[r] = faces[r] with { Indices = [] };
            }
        }

        return lod.WithFaces(faces);
    }

    /// <summary>
    /// Cross-checks the resolved gear articulation against the mesh documents, so a wrong rule or a
    /// mis-parsed block cannot go unnoticed: every leaf address must be a real paint-tree leaf, and
    /// every group's hinge must be the <c>.PNT</c> edge-tree PARENT of its first vertex.
    /// </summary>
    /// <remarks>
    /// The second check is the load-bearing one.  <c>block[+6]</c> is a byte beside the vertex range;
    /// that it equals <c>edgeParents[first]</c> on all 16 shipped blocks is what proves it is the
    /// hinge and not something else, and re-checking it here means the port never rotates a group
    /// about the wrong point.
    /// </remarks>
    private static void VerifyGear(
        string basename, GearArticulation gear, List<MeshLod> lods, PntMeshDocumentDto? pnt)
    {
        MeshLod lod = lods.FirstOrDefault(l => l.Index == gear.LodIndex)
                      ?? throw new InvalidDataException(
                          $"{basename} has no LOD{gear.LodIndex}, which the gear articulation names");
        List<int>? parents = pnt?.Lods?.FirstOrDefault(l => l.Index == gear.LodIndex)?.EdgeParents;

        foreach (GearGroup group in gear.Groups)
        {
            if (!lod.PaintLeaves.ContainsKey(group.LeafNodeImage))
            {
                throw new InvalidDataException(
                    $"{basename} LOD{gear.LodIndex} has no paint-tree leaf at "
                        + $"image@0x{group.LeafNodeImage:X5}, which the gear articulation names");
            }

            if (group.LastVertex >= lod.VertexCount)
            {
                throw new InvalidDataException(
                    $"{basename} gear group at image@0x{group.LeafNodeImage:X5} names vertex "
                        + $"{group.LastVertex}, but LOD{gear.LodIndex} has {lod.VertexCount}");
            }

            if (parents is not null && parents[group.FirstVertex] != group.PivotVertex)
            {
                throw new InvalidDataException(
                    $"{basename} gear group at image@0x{group.LeafNodeImage:X5}: block[+6] says the "
                        + $"hinge is vertex {group.PivotVertex}, but edgeParents[{group.FirstVertex}]"
                        + $" is {parents[group.FirstVertex]}");
            }
        }

        foreach (GearLeaf leaf in gear.ExtraLeaves)
        {
            if (!lod.PaintLeaves.ContainsKey(leaf.LeafNodeImage))
            {
                throw new InvalidDataException(
                    $"{basename} LOD{gear.LodIndex} has no paint-tree leaf at "
                        + $"image@0x{leaf.LeafNodeImage:X5}, which the gear articulation names");
            }
        }
    }

    /// <summary>
    /// H8 addendum — the classes whose INLINE vertices had to be clamped into their own class
    /// record's AABB at load, with how many components moved.
    /// </summary>
    /// <remarks>
    /// See <see cref="ClampInlineVertices"/>.  Empty on a healthy tree;
    /// </remarks>
    public IReadOnlyList<(string Class, int Lod, int Components)> ClampedVertices => _clamped;

    /// <summary>
    /// H8 addendum — the LODs whose executable INLINE vertex block disagrees with the
    /// oracle-verified <c>.PNT</c> block, and how many rows differ.
    /// </summary>
    /// <remarks>
    /// Eleven LODs of seven classes on the shipped tree.  The <c>.PNT</c> block is used; this is the
    /// transform ask kept visible rather than silently papered over.
    /// </remarks>
    public IReadOnlyList<(string Class, int Lod, int Rows)> InlineVertexDisagreements =>
        _disagreements;

    private readonly List<(string Class, int Lod, int Components)> _clamped = [];
    private readonly List<(string Class, int Lod, int Rows)> _disagreements = [];

    private static MeshLod BuildLod(
        string basename,
        ExeMeshLodDto lod,
        PntMeshDocumentDto? pnt,
        int geometryBase,
        IReadOnlyList<int>? bounds,
        int scaleShiftExponent,
        ICollection<(string Class, int Lod, int Components)>? clamped,
        ICollection<(string Class, int Lod, int Rows)>? disagreements)
    {
        ExeMeshLodDescriptorDto descriptor = lod.Descriptor
                                             ?? throw new InvalidDataException(
                                                 $"exe/meshes/{basename}.json LOD{lod.Index} carries no \"descriptor\"");

        PntLodDocumentDto? pntLod = pnt?.Lods?.FirstOrDefault(l => l.Index == lod.Index);

        // H8 addendum — the `.PNT` vertex block WINS over the executable's inline one. The two
        // disagree on ELEVEN LODs of seven classes — canopy0, ejectsh0, l5 0/1, me163sh0, me262sh0,
        // truck 0/1, yak9 0/1/2 — always at the same LENGTH, and it is the INLINE block that is
        // wrong: its vertices fall outside the class record's own AABB while the `.PNT` block's fit.
        // The one of the seven the port ever spawns is `canopy`, the ejected cockpit hood, whose two
        // colour-11 (cyan) polygons both used a rogue vertex — so after every kill the canopy drew a
        // triangle ~60 world units across instead of ~3, and whenever it drifted past the chase
        // camera that triangle straddled the eye, was clipped to the near plane and projected to
        // thousands of screen diagonals.  That is the "strange polygons on MY
        // plane".  The `.PNT` decode is the block proved against the
        // engine's own per-LOD dumps, so it is the source of truth;
        List<List<int>> vertexRows = pntLod?.Vertices ?? lod.InlineVertices
            ?? throw new InvalidDataException(
                $"{basename} LOD{lod.Index} has neither a meshes/{basename}.json LOD nor inline vertices");

        if (pntLod?.Vertices is { } authority && lod.InlineVertices is { } inline)
        {
            int rows = Math.Min(authority.Count, inline.Count);
            int differing = Math.Abs(authority.Count - inline.Count);
            for (int i = 0; i < rows; i++)
            {
                if (authority[i].Count != inline[i].Count
                    || !authority[i].SequenceEqual(inline[i]))
                {
                    differing++;
                }
            }

            if (differing > 0)
            {
                disagreements?.Add((basename, lod.Index, differing));
            }
        }

        int[] vertices = new int[vertexRows.Count * 3];
        for (int i = 0; i < vertexRows.Count; i++)
        {
            List<int> row = vertexRows[i];
            if (row.Count < 3)
            {
                throw new InvalidDataException(
                    $"{basename} LOD{lod.Index} vertex {i} has {row.Count} components, expected 3");
            }

            vertices[i * 3] = row[0];
            vertices[(i * 3) + 1] = row[1];
            vertices[(i * 3) + 2] = row[2];
        }

        // H8 addendum — a PERMANENT tripwire behind the choice above: the class record's own AABB is
        // the original's statement of where its mesh may go, so a vertex outside it is not a datum to
        // draw.  On the shipped tree this now moves NOTHING (the `.PNT` blocks all fit); it exists so
        // that a future bad vertex bounds the damage and names itself instead of filling the screen.
        int moved = ClampVerticesIntoClassBox(vertices, bounds, scaleShiftExponent);
        if (moved > 0)
        {
            clamped?.Add((basename, lod.Index, moved));
        }

        List<ExeMeshRecordDto> records = lod.Records ?? [];

        // pnt_sub_color_patcher @image@0x15C09 patches IN RECORD ORDER, so the stream is only usable
        // when it is exactly as long as the descriptor says the record run is.
        List<int>? colors = pntLod?.FaceColors is { } stream && stream.Count == descriptor.RecordCount
            ? stream
            : null;

        MeshFace[] faces = new MeshFace[records.Count];
        Dictionary<int, int> byAddress = new Dictionary<int, int>(records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            ExeMeshRecordDto record = records[i];
            faces[i] = new MeshFace(
                Primitive: (MeshPrimitive)record.Opcode,
                Indices: record.Indices is { } indices ? [.. indices] : [],
                ColorIndex: (byte)(colors is not null ? colors[i] : record.Color ?? 0),
                Tag: (byte)PortHex.Parse(record.Tag ?? "0x00"),
                Radius: record.Radius,
                Stipple: (byte)PortHex.Parse(record.Sentinel ?? "0xFF"));

            // The tree's leaves list each record by its OWN DGROUP address, which is what `image`
            // carries.  (`faceId` is record[+1..+2], a SHARED id: a single-sided record and its
            // double-sided twin hold the same one — `p51` LOD2 0x4529C / 0x452A8 both say 0x953C —
            // so it cannot identify a record.)
            if (record.Image is { } image)
            {
                byAddress.TryAdd(PortHex.Parse(image) - geometryBase, i);
            }
        }

        return new MeshLod(
            lod.Index, vertices, faces, BuildPaintLeaves(lod, byAddress), BuildArticulation(lod));
    }

    /// <summary>
    /// Clamps every vertex component into the class record's own AABB.
    /// </summary>
    /// <param name="vertices">The flattened <c>(x, y, z)</c> triples, edited in place.</param>
    /// <param name="bounds">
    /// The class record's <c>+0x30..+0x47</c> box as <c>minX maxX minY maxY minZ maxZ</c>
    /// (<see cref="ClassRecord"/>, "Proposed field" B3 §4).  It is in <c>world &lt;&lt; 8</c> units
    /// while the vertex blocks are in WORLD units — <c>bridge</c>'s box maxes at 335,872 and its
    /// vertices at 656 against a <c>meshExtent</c> of 794 — so the box is shifted down by 8 here,
    /// with one unit of slack for the shift's own rounding.
    /// </param>
    /// <returns>How many components were moved.</returns>
    /// <remarks>
    /// A no-op on a healthy LOD: the box is derived from the mesh, so a correctly decoded vertex is
    /// inside it by construction.  It is the tripwire, not the fix — the fix is preferring the
    /// <c>.PNT</c> block above.
    /// </remarks>
    private static int ClampVerticesIntoClassBox(
        int[] vertices, IReadOnlyList<int>? bounds, int scaleShiftExponent)
    {
        if (bounds is not { Count: 6 })
        {
            return 0;
        }

        // The box is in world<<8; a vertex is in MODEL units, which the class's own scale exponent
        // turns into world units — `raw_extent(+0x08) == extent(+0x02) << (8 + scale_shift_exp)`
        // (ClassRecord).  So the box divides by 2^(8 + exp): 256 for an unscaled class and 64 for
        // the eject* family, whose exponent is −2 and whose vertices are four times larger.
        double divisor = Math.ScaleB(1.0, 8 + scaleShiftExponent);
        int minX = Low(bounds[0], bounds[1], divisor), maxX = High(bounds[0], bounds[1], divisor);
        int minY = Low(bounds[2], bounds[3], divisor), maxY = High(bounds[2], bounds[3], divisor);
        int minZ = Low(bounds[4], bounds[5], divisor), maxZ = High(bounds[4], bounds[5], divisor);
        if (minX == maxX && minY == maxY && minZ == maxZ)
        {
            return 0;   // a degenerate box says nothing
        }

        int moved = 0;
        for (int i = 0; i + 2 < vertices.Length; i += 3)
        {
            moved += Clamp(ref vertices[i], minX, maxX);
            moved += Clamp(ref vertices[i + 1], minY, maxY);
            moved += Clamp(ref vertices[i + 2], minZ, maxZ);
        }

        return moved;
    }

    /// <summary>The class box's lower edge in WORLD units, with a unit of slack.</summary>
    private static int Low(int a, int b, double divisor) =>
        (int)Math.Floor(Math.Min(a, b) / divisor) - VertexBoxSlackWorldUnits;

    /// <summary>…and its upper edge.</summary>
    private static int High(int a, int b, double divisor) =>
        (int)Math.Ceiling(Math.Max(a, b) / divisor) + VertexBoxSlackWorldUnits;

    /// <summary>How far outside its class box a vertex may sit before it is clamped.</summary>
    public const int VertexBoxSlackWorldUnits = 1;

    private static int Clamp(ref int value, int low, int high)
    {
        int clamped = Math.Clamp(value, low, high);
        if (clamped == value)
        {
            return 0;
        }

        value = clamped;
        return 1;
    }

    /// <summary>
    /// Maps each paint-tree leaf that owns one to its 9-byte ARTICULATION BLOCK.
    /// </summary>
    /// <param name="lod">The LOD document.</param>
    private static Dictionary<int, MeshArticulationBlock>? BuildArticulation(ExeMeshLodDto lod)
    {
        if (lod.PaintTree is not { Count: > 0 } tree)
        {
            return null;
        }

        Dictionary<int, MeshArticulationBlock>? blocks = null;
        foreach (ExeMeshPaintNodeDto node in tree)
        {
            if (node.Image is not { } image || node.Articulation is not { } block)
            {
                continue;
            }

            blocks ??= [];
            blocks[PortHex.Parse(image)] = new MeshArticulationBlock(
                block.PivotVertex, block.FirstVertex, block.LastVertex);
        }

        return blocks;
    }

    /// <summary>Maps each paint-tree LEAF's <c>image@</c> address to the record indices it emits.</summary>
    /// <param name="lod">The LOD document.</param>
    /// <param name="byAddress">Record DGROUP address → record index.</param>
    private static Dictionary<int, int[]>? BuildPaintLeaves(
        ExeMeshLodDto lod, Dictionary<int, int> byAddress)
    {
        if (lod.PaintTree is not { Count: > 0 } tree)
        {
            return null;
        }

        Dictionary<int, int[]> leaves = new Dictionary<int, int[]>(tree.Count);
        foreach (ExeMeshPaintNodeDto node in tree)
        {
            if (node.Image is not { } image
                || !string.Equals(node.Kind, "leaf", StringComparison.Ordinal))
            {
                continue;
            }

            List<int> indices = new List<int>(node.Faces?.Count ?? 0);
            foreach (string face in node.Faces ?? [])
            {
                if (byAddress.TryGetValue(PortHex.Parse(face), out int record))
                {
                    indices.Add(record);
                }
            }

            leaves[PortHex.Parse(image)] = [.. indices];
        }

        return leaves;
    }

    private PntMeshDocumentDto? TryPnt(string basename) =>
        _tree.Info.Has($"meshes/{basename}.json") ? _tree.Mesh(basename) : null;
}
