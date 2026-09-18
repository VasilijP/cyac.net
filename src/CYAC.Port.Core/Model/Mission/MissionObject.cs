using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// One object a mission places: an opener (what it is), an ordered set of attributes (how it behaves)
/// and the placement that closes it (where it is).
/// </summary>
/// <remarks>
/// <para>
/// This is the view of one <c>data.stream[]</c>
/// item of <c>&lt;data&gt;/missions/&lt;name&gt;.json</c>, which carries the same authoring model
/// Proved round-trip exact for all 54 shipped containers.  The attribute semantics below are
/// that pilot's §2e table; each property names the attribute tag it reads so a reader can go back to
/// the byte.
/// </para>
/// <para>
/// <b>Stream order is semantic</b> and this type keeps it (<see cref="StreamIndex"/>): relative
/// placements chain backwards, an actor slot is last-writer-wins, and a script sees the coordinates
/// registered before it.
/// </para>
/// <para>INT-only authored content; nothing here is mutable simulation state.</para>
/// </remarks>
public sealed class MissionObject
{
    private readonly MissionStreamItemDto _source;

    private MissionObject(MissionStreamItemDto source, int streamIndex, MissionPlacement placement)
    {
        _source = source;
        StreamIndex = streamIndex;
        Placement = placement;
        Kind = source.Object switch
        {
            "marker" => MissionObjectKind.Marker,
            "named_mesh" => MissionObjectKind.NamedMesh,
            "nav_waypoint" => MissionObjectKind.NavWaypoint,
            "ground_fx" => MissionObjectKind.GroundEffect,
            "prim_4d00" => MissionObjectKind.Airport,
            _ => MissionObjectKind.ClassInstance,
        };
    }

    internal static MissionObject Create(
        MissionStreamItemDto source, int streamIndex, MissionPlacement placement) =>
        new(source, streamIndex, placement);

    /// <summary>The underlying document item, for callers that need a field this view omits.</summary>
    public MissionStreamItemDto Source => _source;

    /// <summary>The object's position in the container's tag stream (directives included).</summary>
    public int StreamIndex { get; }

    /// <summary>What the opener declared.</summary>
    public MissionObjectKind Kind { get; }

    /// <summary>The placement that closed the object, with the port's resolution of it.</summary>
    public MissionPlacement Placement { get; }

    /// <summary>
    /// The class-table id for a <see cref="MissionObjectKind.ClassInstance"/>, else −1.
    /// <see cref="MissionObjectKind.Airport"/> objects hardcode prim <c>0x4D00</c>, the descriptor
    /// class id 26 also points at.
    /// </summary>
    public int ClassId => Kind switch
    {
        MissionObjectKind.ClassInstance => _source.ClassId,
        MissionObjectKind.Airport => MissionVocabulary.AirportClassId,
        _ => -1,
    };

    /// <summary>The class's display name, or <see langword="null"/> for the non-class openers.</summary>
    public string? ClassName => ClassId < 0 ? null : MissionClassCatalog.DisplayName(ClassId);

    /// <summary>
    /// The mesh class record that draws this object, where the repo documents the class-id → mesh link
    /// (see <see cref="MissionClassCatalog"/>); <see langword="null"/> otherwise.
    /// </summary>
    public ClassRecord? MeshClass => ClassId < 0 ? null : MissionClassCatalog.MeshClass(ClassId);

    /// <summary>True for the class-0 object every mission has exactly one of: the player's start.</summary>
    public bool IsPlayerStart =>
        Kind == MissionObjectKind.ClassInstance && ClassId == MissionClassCatalog.PlayerClassId;

    /// <summary>True for the class-1 object: the made-it-home reference written to <c>g_home_base_pos [0xEE40]</c>.</summary>
    public bool IsHomeBaseReference =>
        Kind == MissionObjectKind.ClassInstance && ClassId == MissionClassCatalog.HomeBasePositionClassId;

    /// <summary>
    /// True when the opener actually spawns something: a class instance with an id at or above the
    /// first spawning class (6), or one of the three non-class openers the engine instantiates.
    /// </summary>
    public bool Spawns => Kind switch
    {
        MissionObjectKind.ClassInstance => ClassId >= MissionVocabulary.FirstSpawningClassId,
        MissionObjectKind.NamedMesh or MissionObjectKind.GroundEffect or MissionObjectKind.Airport => true,
        _ => false,
    };

    /// <summary>The opener label of a named mesh or nav waypoint; empty otherwise.</summary>
    public string Label => _source.Label ?? string.Empty;

    /// <summary>
    /// For a <see cref="MissionObjectKind.NavWaypoint"/>: the slot index into
    /// <c>g_nav_slot_record_array [0xB564]</c> (0..2 in shipped data).
    /// </summary>
    public int NavSlot => Kind == MissionObjectKind.NavWaypoint ? _source.NavSlot : -1;

    /// <summary>
    /// Attr <c>0x86</c> — the object's actor slot: the index win rules, AI scripts and
    /// <see cref="MissionPlacementKind.RelativeToPlace"/> all point at.  <c>[0xEE5A]</c> is u16[13], so
    /// the range is 0..12 and slots are the scarce authoring resource.
    /// </summary>
    public int? ActorSlot => IntAttr("actor_slot");

    /// <summary>Attr <c>0x88</c> — the slot's place-type byte (<c>[0xEF10]</c>).</summary>
    public int? PlaceType => IntAttr("place_type");

    /// <summary>
    /// Attr <c>0x80</c> — the spawn's aux word <c>+0x12</c>; the player's goes to the view record
    /// <c>[0xEE4C]</c>.  Raw units.
    /// </summary>
    public int? HeadingUnits => IntAttr("aux0_heading");

    /// <summary>
    /// <see cref="HeadingUnits"/> read as an initial heading on the 2880-unit circle.
    /// </summary>
    /// <remarks>
    /// The routing is verified; the UNIT is P15's hypothesis — all 21 distinct shipped values are
    /// divisible by 8 and the largest (2800) is below the full circle, and ABB's bandit pair sits at
    /// 1440 = 180° facing the player.  Treat the number as certain and the interpretation as strong.
    /// </remarks>
    public Angle? Heading => HeadingUnits is int units ? Angle.FromUnits(units) : null;

    /// <summary>
    /// Attr <c>0x98</c> — the object's initial SPEED.  The player's goes to
    /// <c>g_player_init_speed_seed [0xEE52]</c> and then <c>&lt;&lt; 8</c> into the forward velocity;
    /// others land at engagement slot <c>+0x25</c>.
    /// </summary>
    /// <remarks>
    /// Corrected: the player path is decode-proven; the
    /// shipped values 0/366/733/880/953… read as ft/s, and FREE.S's player is 0, the known runway
    /// start.
    /// </remarks>
    public int? InitialSpeed => IntAttr("initial_speed");

    /// <summary>Attr <c>0x96</c> — the pilot's skill byte, engagement slot <c>+0x1D</c> (shipped {2,12,25,38,51,64,76,204}).</summary>
    public int? Skill => IntAttr("skill");

    /// <summary>Attr <c>0x97</c> — the interned pilot / nose-art name at engagement slot <c>+0x1E</c>.</summary>
    public string? PilotName => Attr("pilot_name")?.AsText;

    /// <summary>Attr <c>0x9C</c> — player only: the timer seed at <c>[0xEE56]</c> (shipped {0,50,65}).</summary>
    public int? PlayerTimerSeed => IntAttr("player_timer_seed");

    /// <summary>Attr <c>0x84</c> — the era this object is filtered on; dormant in shipped data.</summary>
    public int? EraMatch => IntAttr("era_match");

    /// <summary>Attr <c>0x99</c> — three words into the spawn aux record at <c>+0x12/+0x14/+0x16</c>.</summary>
    public IReadOnlyList<int>? AuxVector => Attr("aux_vector")?.AsWords;

    /// <summary>Attr <c>0x87</c> — the object joins <c>g_active_target_table</c> at spawn (<c>[0xEDE6]</c>).</summary>
    public bool JoinsTargetTable => HasAttr("target_table");

    /// <summary>
    /// Attrs <c>0x90</c>..<c>0x93</c> — the 2-bit engagement class code 0..3 (default 1 in the engine);
    /// <see langword="null"/> when the object authored none.
    /// </summary>
    /// <remarks>"Priority" was P4's guess; the code is verified, its meaning is open.</remarks>
    public int? EngagementClass
    {
        get
        {
            for (int code = 0; code <= 3; code++)
            {
                if (HasAttr($"engage_class_{code}"))
                {
                    return code;
                }
            }

            return null;
        }
    }

    /// <summary>The authored script flags (committed only when the object also carries a script).</summary>
    public MissionScriptFlags ScriptFlags
    {
        get
        {
            MissionScriptFlags flags = MissionScriptFlags.None;
            if (HasAttr("script_flag_01"))
            {
                flags |= MissionScriptFlags.Bit01;
            }

            if (HasAttr("script_flag_04"))
            {
                flags |= MissionScriptFlags.Bit04;
            }

            if (HasAttr("script_flag_08"))
            {
                flags |= MissionScriptFlags.Bit08;
            }

            if (HasAttr("script_flag_10"))
            {
                flags |= MissionScriptFlags.Bit10;
            }

            if (HasAttr("script_flag_20"))
            {
                flags |= MissionScriptFlags.Bit20;
            }

            return flags;
        }
    }

    /// <summary>The object's flag word as authored: the engine's default 0x40 unless attr <c>0x8F</c> cleared it, plus the three optional bits.</summary>
    public MissionObjectFlags ObjectFlags
    {
        get
        {
            MissionObjectFlags flags = HasAttr("clear_objflag_40")
                ? MissionObjectFlags.None
                : MissionObjectFlags.Default40;

            if (HasAttr("objflag_80"))
            {
                flags |= MissionObjectFlags.Flag80;
            }

            if (HasAttr("objflag_100"))
            {
                flags |= MissionObjectFlags.Flag100;
            }

            if (HasAttr("objflag_400"))
            {
                flags |= MissionObjectFlags.Flag400;
            }

            return flags;
        }
    }

    /// <summary>
    /// Attr <c>0x89</c> — the authored AI script as ordered STEPS.
    /// </summary>
    /// <remarks>
    /// The data tree carries the script in its readable form: an ordered
    /// list of <c>[delay, opcode, operands]</c> steps that the transform substitutes for the raw hex
    /// only when re-assembling them reproduces the shipped bytes exactly.  The bytes themselves are a
    /// transform-time artefact; <see langword="null"/> when the object carries no script, or when the
    /// transform could not structure it (then the document keeps its <c>hex</c>).
    /// </remarks>
    public MissionAiScriptDto? AiScript => Attr("ai_script")?.Script;

    /// <summary>True when the object carries an AI script.</summary>
    public bool HasAiScript => Attr("ai_script") is not null;

    /// <summary>
    /// The raw <c>ai_script</c> payload the transform could not structure, as a hex string — the
    /// bytes the tag-<c>0x89</c> record holds verbatim.
    /// </summary>
    public string? AiScriptHex => Attr("ai_script")?.Hex;

    /// <summary>The transform's listing of the SHIPPED script bytes, one line per instruction.</summary>
    public IReadOnlyList<string>? AiScriptDisassembly => Attr("ai_script")?.Disassembly;

    /// <summary>
    /// The place and actor slots this object's script references, as the load-time patcher
    /// <c>ai_script_named_place_patch @image@0x094EF</c> would find them.  Empty when there is no
    /// script; throws nothing — an unwalkable script yields empty lists.
    /// </summary>
    public (IReadOnlyList<int> Places, IReadOnlyList<int> Actors) ScriptReferences
    {
        get
        {
            if (!HasAiScript)
            {
                return ([], []);
            }

            MissionScriptRefsDto? refs = Attr("ai_script")?.Refs;
            return refs is null || refs.Error is not null
                ? ([], [])
                : (refs.Places ?? [], refs.Actors ?? []);
        }
    }

    /// <summary>The attributes in authored order, as the document carries them.</summary>
    public IReadOnlyList<MissionAttrDto> Attributes => _source.Attrs ?? [];

    private MissionAttrDto? Attr(string name)
    {
        foreach (MissionAttrDto attr in _source.Attrs ?? [])
        {
            if (string.Equals(attr.Attr, name, StringComparison.Ordinal))
            {
                return attr;
            }
        }

        return null;
    }

    private bool HasAttr(string name) => Attr(name) is not null;

    private int? IntAttr(string name) => Attr(name)?.AsInt;

    /// <inheritdoc/>
    public override string ToString()
    {
        string what = Kind == MissionObjectKind.ClassInstance
            ? ClassName ?? $"class {ClassId}"
            : Kind.ToString();
        string slot = ActorSlot is int s ? $" slot {s}" : string.Empty;
        return $"#{StreamIndex} {what}{slot} @ {Placement.Kind}";
    }
}
