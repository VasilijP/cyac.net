using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.World;

/// <summary>
/// One object in the scene: an instance of a <see cref="ClassRecord"/> with a position, an attitude, a
/// flag word and a place in the scene tree.  The original is <c>s_pool_arena_entry</c>, a record in
/// the pool arena (arena #4).
/// </summary>
/// <remarks>
/// <para>
/// Field classes: <see cref="Class"/>, <see cref="Flags"/> and the tree links are <b>INT-only</b>
/// spine; <see cref="X"/>/<see cref="Y"/>/<see cref="Z"/> and the attitude triple are the <b>integer
/// half of DUAL</b> pairs — the float kernel will carry its own copy, this one keeps the original's
/// exact widths and wrap semantics.
/// </para>
/// <para>
/// <b>Position units.</b> Three <see cref="int"/>s, "world units", exactly as the original stores them
/// (<c>+0x06/+0x0A/+0x0E</c>, three i32 — byte-proven: the six-word copy at
/// <c>image@0x33D70</c> is read back as three lo/hi pairs with unsigned <c>jb</c>/<c>ja</c> on the low
/// word, the textbook signed-i32 lexicographic compare).  A class's own geometry is authored in
/// <i>mesh</i> units and rendered at <c>2^ClassRecord.ScaleShiftExponent × world scale</c> — see P21
/// for the scale story, which is where the port must go for any unit conversion.  Y is up: a class's
/// <c>BoundsMinY</c> is its belly and <c>ClassRecord.GroundClearance</c> is its negation.
/// </para>
/// <para>
/// <b>Tree links are object references here, not arena offsets.</b>  The original threads siblings
/// through <c>+0x04</c> and children through <c>+0x18</c> as near offsets inside the arena segment;
/// that is an allocator detail, so the offsets survive only in the
/// <see cref="OriginalFieldAttribute"/> metadata (README: "no DGROUP offsets in game logic").
/// </para>
/// <para>
/// <b>Not modelled: <c>+0x0C bucket_ref_off</c>.</b>  The scanner itself flags it: "on
/// RENDER-LEAF clones this word is pos_y's HIGH word … P261's <c>+0x0C</c> reading belongs to a
/// different pool-record flavour — reconcile in a struct pass."  Since <c>+0x0A pos_y_i32</c> spans
/// <c>+0x0A..+0x0D</c>, the two cannot both be live in one record, and the port takes the position
/// (bytes &gt; scanner, and B20 F8 is byte-proven).  Reported, not resolved.
/// </para>
/// </remarks>
[OriginalStruct("s_pool_arena_entry")]
public sealed class WorldObject
{
    private readonly List<WorldObject> _children = [];

    /// <summary>Creates a world object of a given class.</summary>
    /// <param name="classRecord">The class this object instantiates.</param>
    internal WorldObject(ClassRecord classRecord)
    {
        ArgumentNullException.ThrowIfNull(classRecord);
        Class = classRecord;
    }

    /// <summary>
    /// <c>+0x00</c> — the object's class.  The original stores a DGROUP near pointer to the static
    /// class record; <c>pool_insert_with_bbox_or @0x153A1</c> reads it back to fetch the filter word
    /// (<c>mov bx,es:[si]</c> @<c>image@0x153BC</c>).
    /// </summary>
    [OriginalField("+0x00", "descriptor_nearptr")]
    public ClassRecord Class { get; }

    /// <summary>
    /// <c>+0x02</c> — the flag/filter word.  See <see cref="WorldObjectFlags"/> for the bit map.
    /// </summary>
    [OriginalField("+0x02", "flag_filter_word")]
    public WorldObjectFlags Flags { get; set; }

    /// <summary><c>+0x06</c> — world X, i32 (see the type remarks for units).</summary>
    [OriginalField("+0x06", "pos_x_i32")]
    public int X { get; set; }

    /// <summary><c>+0x0A</c> — world Y (up), i32.</summary>
    [OriginalField("+0x0A", "pos_y_i32")]
    public int Y { get; set; }

    /// <summary><c>+0x0E</c> — world Z, i32.</summary>
    [OriginalField("+0x0E", "pos_z_i32")]
    public int Z { get; set; }

    /// <summary>
    /// <c>+0x12</c> — heading (yaw) on the 2880-unit circle.
    /// </summary>
    /// <remarks>
    /// Corrected: the <c>+0x12/+0x14/+0x16</c> words are the ORIENTATION triple, 1/8° BAM
    /// yaw/pitch/roll;  Verdict B. The HUD compass reads this word for the player object
    /// (<c>(h+4)&gt;&gt;3; neg; +360</c> @<c>image@0x0F5F7</c>), which independently re-proves the unit.
    /// </remarks>
    [OriginalField("+0x12", "heading_bam_i16")]
    public Angle Heading { get; set; }

    /// <summary><c>+0x14</c> — pitch, same units as <see cref="Heading"/>.</summary>
    [OriginalField("+0x14", "pitch_bam_i16")]
    public Angle Pitch { get; set; }

    /// <summary><c>+0x16</c> — roll, same units as <see cref="Heading"/>.</summary>
    [OriginalField("+0x16", "roll_bam_i16")]
    public Angle Roll { get; set; }

    /// <summary>
    /// The object this one hangs off, or <see langword="null"/> for a root.  The original expresses
    /// the relationship the other way round, through the parent's <c>+0x18</c> child-list head.
    /// </summary>
    [OriginalField("+0x18", "first_child_off")]
    public WorldObject? Parent { get; internal set; }

    /// <summary>
    /// This object's children, in the original's insertion order (the game appends to the tail of the
    /// <c>+0x04</c> sibling chain, <c>pool_insert_no_parent @0x152A4</c>).
    /// </summary>
    [OriginalField("+0x04", "next_sibling_off")]
    public IReadOnlyList<WorldObject> Children => _children;

    /// <summary>
    /// The engagement block embedded in this object's arena record, or <see langword="null"/> when
    /// it carries none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// *(the combat kernel's per-object state lives INSIDE the
    /// pool record, so the port's world object is where it belongs.)*  Present exactly when
    /// <see cref="WorldObjectFlags.CarriesEngagement"/> is set: measured 11 of 89 entries on
    /// echoed in the block's <see cref="Combat.EngagementState.OwnerObjectRef"/> on 11/11.
    /// </para>
    /// <para>
    /// Three of those 11 carry only the 5-byte STUB form (their prototype's <c>+0x0C</c> bit3 is
    /// clear, so <c>spawn_dispatch_object</c> asked the arena for 5 bytes) — a stub still has
    /// <see cref="Combat.EngagementState.PrototypeRef"/>, <see cref="Combat.EngagementState.OwnerObjectRef"/>
    /// and <see cref="Combat.EngagementState.HitPoints"/>, and nothing else is meaningful.
    /// <see cref="HasFullEngagementBlock"/> says which.
    /// </para>
    /// </remarks>
    public Combat.EngagementState? Engagement { get; set; }

    /// <summary>
    /// Where the engagement block starts inside the arena record: <c>+0x12</c> when
    /// <see cref="WorldObjectFlags.NoOrientation"/> is set, else <c>+0x18</c>.
    /// </summary>
    /// <remarks>
    /// <c>object_pool_get_engagement_fieldoff @image@0x0225A</c> is exactly this rule:
    /// <c>test word ptr es:[bx+2],2 / je → lea ax,[bx+0x18] ; else lea ax,[bx+0x12]</c>
    /// (<c>image@0x0225E..0x0226D</c>).  It is the same 6-byte delta as
    /// <see cref="ArenaEntryBytes"/>: the compact record simply has no orientation triple.
    /// </remarks>
    public int EngagementBlockOffset =>
        Flags.HasFlag(WorldObjectFlags.NoOrientation)
            ? WorldObjectPool.EntryBytesCompact
            : WorldObjectPool.EntryBytesWithOrientation;

    /// <summary>
    /// True when this object's engagement block is a full record rather than the 5-byte stub — i.e.
    /// when the prototype it points at is engage-capable.
    /// </summary>
    public bool HasFullEngagementBlock { get; set; }

    /// <summary>True when <see cref="WorldObjectFlags.Active"/> is set.</summary>
    public bool IsActive => Flags.HasFlag(WorldObjectFlags.Active);

    /// <summary>True when <see cref="WorldObjectFlags.CarriesEngagement"/> is set.</summary>
    public bool CarriesEngagement => Flags.HasFlag(WorldObjectFlags.CarriesEngagement);

    /// <summary>True when <see cref="WorldObjectFlags.Hostile"/> is set (Box View draws it lt. red).</summary>
    public bool IsHostile => Flags.HasFlag(WorldObjectFlags.Hostile);

    /// <summary>True when the orientation triple is all-zero — the original's bit1 condition.</summary>
    /// <remarks>
    /// <c>pool_insert_with_flag2 @0x1536C</c> tests exactly this
    /// (<c>cmp [si+0x12],0 … cmp [si+0x16],0</c> @<c>image@0x1537A..0x1538C</c>) before setting
    /// <see cref="WorldObjectFlags.NoOrientation"/>.
    /// </remarks>
    public bool HasZeroOrientation =>
        Heading.Units == 0 && Pitch.Units == 0 && Roll.Units == 0;

    /// <summary>
    /// The number of arena bytes this object's record occupies in the original: <c>0x12</c> when
    /// <see cref="WorldObjectFlags.NoOrientation"/> is set, <c>0x18</c> otherwise.
    /// </summary>
    /// <remarks>
    /// <c>pool_compute_bucket_head @0x1527E</c>, <c>image@0x1528A..0x15298</c>: the SBB idiom yields
    /// <c>0x12</c> for bit1 SET and <c>0x18</c> for bit1 CLEAR — hand-traced both directions.  The
    /// 6-byte delta is exactly the three orientation words.  Note the polarity: setting the bit
    /// makes the record <b>smaller</b> (an earlier reading had it the other way round).
    /// </remarks>
    public int ArenaEntryBytes =>
        Flags.HasFlag(WorldObjectFlags.NoOrientation)
            ? WorldObjectPool.EntryBytesCompact
            : WorldObjectPool.EntryBytesWithOrientation;

    /// <summary>Depth-first walk of this object and its descendants, children before siblings.</summary>
    /// <remarks>
    /// Mirrors <c>pool_arena_entry_tree_walk_dispatch @0x159BD</c>: it recurses into the child chain
    /// when <see cref="WorldObjectFlags.HasChildren"/> is set (<c>image@0x159DE</c>) and then advances
    /// along the sibling link.  This walk does not consult the flag — the port's child list is
    /// authoritative — but <see cref="WorldObjectPool"/> keeps the flag in sync so the two agree.
    /// </remarks>
    public IEnumerable<WorldObject> DescendantsAndSelf()
    {
        yield return this;
        foreach (WorldObject child in _children)
        {
            foreach (WorldObject descendant in child.DescendantsAndSelf())
            {
                yield return descendant;
            }
        }
    }

    internal void AddChild(WorldObject child)
    {
        _children.Add(child);
        child.Parent = this;
        Flags |= WorldObjectFlags.HasChildren;
    }

    internal void RemoveChild(WorldObject child)
    {
        if (!_children.Remove(child))
        {
            return;
        }

        child.Parent = null;
        if (_children.Count == 0)
        {
            Flags &= ~WorldObjectFlags.HasChildren;
        }
    }

    /// <summary>A short, stable description for test output and debugging.</summary>
    public override string ToString() =>
        $"{Class.Name} @({X}, {Y}, {Z}) flags=0x{(ushort)Flags:X4}";
}
