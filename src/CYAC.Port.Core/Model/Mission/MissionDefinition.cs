using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// A mission's content: the theater sites it may anchor to, the directives that set up the scene, the
/// objects it places, the module that judges it, and — where modelled — the rules inside that module.
/// </summary>
/// <remarks>
/// <para>
/// A definition is loaded from
/// <c>&lt;data&gt;/missions/&lt;name&gt;.json</c> (or <c>world/&lt;name&gt;.json</c>).  The document's
/// <c>data</c> section IS the authoring model the mission-system work proved round-trip byte-exact for all
/// 54 shipped containers, and <c>cyac-transform --verify</c> re-proves it against 2b.lib on every run.
/// Nothing here parses bytes.
/// </para>
/// <para>
/// One type covers both container flavours, because they share the grammar: 51 <c>.S</c> missions and
/// 3 <c>.W</c> theater catalogs, all in <c>2b.lib</c>.  <see cref="IsTheater"/> tells them apart; a
/// theater carries the <see cref="Sites"/> and the scenery, a mission carries the actors and the
/// module.
/// </para>
/// <para>
/// <b>Positions are anchor-relative by design.</b> A mission places its engagement at a site the
/// engine picks at random when the mission loads, so the port resolves each placement chain to a
/// <see cref="MissionPosition"/> against that anchor instead of pretending to know world coordinates.
/// See <see cref="MissionPosition"/> and <see cref="MissionPlacementKind"/>.
/// </para>
/// </remarks>
public sealed class MissionDefinition
{
    private readonly MissionDataDto _model;

    private MissionDefinition(
        string assetName,
        MissionDocumentDto document,
        MissionDataDto model,
        IReadOnlyList<MissionObject> objects,
        IReadOnlyList<MissionDirective> directives,
        int anchorCount)
    {
        AssetName = assetName;
        Source = document;
        _model = model;
        Objects = objects;
        Directives = directives;
        AnchorCount = anchorCount;
        Sites = [.. (model.Sites ?? []).Select((s, i) => new MissionSite(i, s.Type, s.Subtype, s.X, s.Z))];
        Names = [.. model.Names ?? []];
        Module = document.Module is { Present: true } module ? new MissionModule(module) : null;
        WinRules = MissionWinRules.FromModule(assetName, document.Module);
    }

    /// <summary>The archive that ships every <c>.S</c> and <c>.W</c>: <c>2b.lib</c>.</summary>
    public const string ArchiveName = "2b.lib";

    /// <summary>Missions shipped as <c>.S</c> containers: 51 (the 50 catalog entries plus FREE.S).</summary>
    public const int ShippedMissionCount = 51;

    /// <summary>Theater catalogs shipped as <c>.W</c> containers: 3.</summary>
    public const int ShippedTheaterCount = 3;

    /// <summary>The folder missions live in inside the data tree.</summary>
    public const string MissionFolder = "missions";

    /// <summary>The folder theater catalogs live in inside the data tree.</summary>
    public const string TheaterFolder = "world";

    /// <summary>
    /// The highest actor slot an author may use: 12.  <c>[0xEE5A]</c> is u16[13], and the shipped data
    /// uses exactly 0..12.
    /// </summary>
    public const int MaxActorSlot = 12;

    /// <summary>The highest nav-waypoint slot: 2 (<c>g_nav_slot_record_array [0xB564]</c>, stride 0x2C).</summary>
    public const int MaxNavSlot = 2;

    /// <summary>Loads a container from its transformed document.</summary>
    /// <param name="document">A parsed <c>missions/&lt;n&gt;.json</c> or <c>world/&lt;n&gt;.json</c>.</param>
    /// <exception cref="InvalidDataException">The document carries no authoring model.</exception>
    public static MissionDefinition Load(MissionDocumentDto document)
    {
        ArgumentNullException.ThrowIfNull(document);
        MissionDataDto model = document.Data
                               ?? throw new InvalidDataException(
                                   $"{document.AssetName ?? "(unnamed)"}: the document has no \"data\" section");

        string assetName = document.AssetName ?? model.Name ?? string.Empty;
        (IReadOnlyList<MissionObject> objects, IReadOnlyList<MissionDirective> directives, int anchors) = Project(model);
        return new MissionDefinition(assetName, document, model, objects, directives, anchors);
    }

    /// <summary>The asset name this container was parsed under.</summary>
    public string AssetName { get; }

    /// <summary>True for a <c>.W</c> theater catalog, false for a <c>.S</c> mission.</summary>
    public bool IsTheater => _model.Kind == "W";

    /// <summary>The section-1 sites (theater catalogs only; missions have none).</summary>
    public IReadOnlyList<MissionSite> Sites { get; }

    /// <summary>The section-2 name table, as text.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Every placed object, in stream order.</summary>
    public IReadOnlyList<MissionObject> Objects { get; }

    /// <summary>Every header-level directive, in stream order.</summary>
    public IReadOnlyList<MissionDirective> Directives { get; }

    /// <summary>The trailer module, or <see langword="null"/> (the three theaters have none).</summary>
    public MissionModule? Module { get; }

    /// <summary>The modelled win rules, or <see langword="null"/> when this mission is not modelled.</summary>
    public MissionWinRules? WinRules { get; }

    /// <summary>How many independent <c>at_site</c> picks the container makes — the number of anchors.</summary>
    public int AnchorCount { get; }

    /// <summary>The document behind this adapter.</summary>
    public MissionDocumentDto Source { get; }

    /// <summary>The authoring model section of that document.</summary>
    public MissionDataDto Model => _model;

    /// <summary>The player's start object (class 0).  Every shipped mission has exactly one.</summary>
    public MissionObject? PlayerStart => Objects.FirstOrDefault(o => o.IsPlayerStart);

    /// <summary>The made-it-home reference object (class 1).  Every shipped mission has exactly one.</summary>
    public MissionObject? HomeBaseReference => Objects.FirstOrDefault(o => o.IsHomeBaseReference);

    /// <summary>The cockpit nav waypoints, in stream order.</summary>
    public IReadOnlyList<MissionObject> NavWaypoints =>
        [.. Objects.Where(o => o.Kind == MissionObjectKind.NavWaypoint)];

    /// <summary>The objects that actually spawn something (class id ≥ 6), in stream order.</summary>
    public IReadOnlyList<MissionObject> SpawnedObjects => [.. Objects.Where(o => o.Spawns)];

    /// <summary>
    /// The object registered in each actor slot, last writer winning — the table AI scripts, win rules
    /// and <see cref="MissionPlacementKind.RelativeToPlace"/> references index
    /// (<c>named_place_entry_register @0x08EE7</c> → <c>[0xEE5A]</c>).
    /// </summary>
    public IReadOnlyDictionary<int, MissionObject> ActorSlots
    {
        get
        {
            Dictionary<int, MissionObject> slots = new Dictionary<int, MissionObject>();
            foreach (MissionObject o in Objects)
            {
                if (o.ActorSlot is int slot)
                {
                    slots[slot] = o;
                }
            }

            return slots;
        }
    }

    /// <summary>Directive <c>0x95</c> — the player's aircraft index 0..5 (<c>g_active_aircraft_idx [0xC31A]</c>).</summary>
    public int? PlayerAircraftIndex => DirectiveValue("player_aircraft");

    /// <summary>Directive <c>0x9E</c> — the featured opponent's class id (<c>g_opponent_statblock_ptr [0xF1C6]</c>).</summary>
    public int? OpponentClassId => DirectiveValue("opponent_class");

    /// <summary>
    /// Directive <c>0x9A</c> — the mission-altitude datum <c>g_mission_altitude [0xF100]</c>, which
    /// seeds the cloud layer.
    /// </summary>
    /// <remarks>
    /// Not the player's start altitude — that is the class-0 object's own Y (43 of the 51
    /// missions carry the directive, values 0 or 4500..24000, and ABB's player starts at y=7000 with no
    /// directive at all).
    /// </remarks>
    public int? MissionAltitude => DirectiveValue("mission_altitude");

    /// <summary>Directive <c>0x84</c> — the container's era filter (<c>[0xEE32]</c>).</summary>
    public int? EraFilter => DirectiveValue("era_filter");

    /// <summary>Directive <c>0x83</c> — the subtype maximum (<c>[0xB54A]</c>) that sizes an era matrix.</summary>
    public int? SubtypeMax => DirectiveValue("subtype_max");

    /// <summary>
    /// Directive <c>0x88</c> — the four world-extent coordinates written to <c>[0xF0E6..0xF0F5]</c>, in
    /// authored order (which of the four is which bound is not documented).
    /// </summary>
    public IReadOnlyList<int>? WorldExtents =>
        Directives.FirstOrDefault(d => d.Name == "world_extents")?.Coords;

    /// <summary>The value of a directive by authoring name, or <see langword="null"/>.</summary>
    /// <param name="name">An authoring name from <c>missions/_vocabulary.json</c>.</param>
    public int? DirectiveValue(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        MissionDirective? directive = Directives.FirstOrDefault(d => d.Name == name);
        return directive is null ? null : directive.Value;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{AssetName} — {Objects.Count} objects, {Sites.Count} sites, " +
        $"{(Module is null ? "no module" : "module")}";

    // -- placement resolution -------------------------------------------------------------------
    //
    // The engine's own state machine, replayed statically:
    //   * every object writes its final position back to [0xB556]              image@0x0A2FD
    //   * an actor_slot registers that position at [0xEE74 + 12k]              @0x08EE7
    //   * the class-0 player's position becomes the spawn origin [0xEE34]      image@0x0A084
    // so a chain of relative placements is exactly determined once its root is.  Roots are either
    // world-absolute (tags 0/1) or an at_site pick, which is random at load time and therefore stays
    // symbolic here: an anchor id plus the site type it draws from.
    // The document spells tags as authoring NAMES; the vocabulary turns them back into the byte the
    // engine's tag interpreter reads, which is what MissionDirective and MissionPlacementKind carry.
    private static byte DirectiveTag(string name) => (byte)MissionVocabulary.DirectiveTag(name);

    private static MissionPlacementKind PlacementKind(string? name) =>
        name is null ? MissionPlacementKind.Absolute
                     : (MissionPlacementKind)MissionVocabulary.PlacementTag(name);

    private static (IReadOnlyList<MissionObject> Objects, IReadOnlyList<MissionDirective> Directives, int Anchors)
        Project(MissionDataDto model)
    {
        List<MissionObject> objects = new List<MissionObject>();
        List<MissionDirective> directives = new List<MissionDirective>();
        Dictionary<int, MissionPosition?> slots = new Dictionary<int, MissionPosition?>();
        MissionPosition? previous = null;
        MissionPosition? spawnOrigin = null;
        int anchors = 0;
        int streamIndex = 0;

        foreach (MissionStreamItemDto item in model.Stream ?? [])
        {
            if (item.IsDirective)
            {
                directives.Add(new MissionDirective(
                    item.Directive!,
                    DirectiveTag(item.Directive!),
                    item.DirectiveValue ?? 0,
                    item.DirectiveCoords?.ToArray()));
                streamIndex++;
                continue;
            }

            MissionStreamItemDto source = item;
            MissionPosDto pos = source.Pos ?? new MissionPosDto { Pos = "abs" };
            MissionPlacementKind kind = PlacementKind(pos.Pos);
            int[] coords = [.. pos.Xyz ?? pos.Xz ?? []];

            int offsetX = kind switch
            {
                MissionPlacementKind.GroundPlane => coords.Length > 0 ? coords[0] : 0,
                MissionPlacementKind.TrackActors or MissionPlacementKind.AtSite
                    or MissionPlacementKind.EraMatrix => 0,
                _ => coords.Length > 0 ? coords[0] : 0,
            };
            int offsetY = kind switch
            {
                MissionPlacementKind.GroundPlane => 0,
                MissionPlacementKind.TrackActors or MissionPlacementKind.AtSite
                    or MissionPlacementKind.EraMatrix => 0,
                _ => coords.Length > 1 ? coords[1] : 0,
            };
            int offsetZ = kind switch
            {
                MissionPlacementKind.GroundPlane => coords.Length > 1 ? coords[1] : 0,
                MissionPlacementKind.TrackActors or MissionPlacementKind.AtSite
                    or MissionPlacementKind.EraMatrix => 0,
                _ => coords.Length > 2 ? coords[2] : 0,
            };

            MissionPosition? resolved = kind switch
            {
                MissionPlacementKind.Absolute => MissionPosition.World(offsetX, offsetY, offsetZ),
                MissionPlacementKind.GroundPlane => MissionPosition.World(offsetX, 0, offsetZ),
                MissionPlacementKind.AtSite => new MissionPosition(anchors++, pos.SiteType, 0, 0, 0),
                MissionPlacementKind.RelativeToPrevious => previous?.Offset(offsetX, offsetY, offsetZ),
                MissionPlacementKind.RelativeToSpawnOrigin => spawnOrigin?.Offset(offsetX, offsetY, offsetZ),
                MissionPlacementKind.RelativeToPlace =>
                    slots.TryGetValue(pos.Place, out MissionPosition? basePos)
                        ? basePos?.Offset(offsetX, offsetY, offsetZ)
                        : null,
                _ => null,
            };


            MissionPlacement placement = new MissionPlacement(
                kind,
                offsetX,
                offsetY,
                offsetZ,
                pos.SiteType,
                kind == MissionPlacementKind.RelativeToPlace ? pos.Place : -1,
                pos.Slots?.ToArray(),
                resolved);

            MissionObject projected = MissionObject.Create(source, streamIndex, placement);
            objects.Add(projected);

            previous = resolved;
            if (projected.ActorSlot is int slot)
            {
                slots[slot] = resolved;
            }

            if (projected.IsPlayerStart)
            {
                spawnOrigin = resolved;
            }

            streamIndex++;
        }

        return (objects, directives, anchors);
    }
}
