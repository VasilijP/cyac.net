using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.World;

/// <summary>
/// A world-object class's render descriptor — the original 14-byte
/// <c>s_class_render_desc</c>, reached through <see cref="ClassRecord.RenderDescriptor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Source of truth: KNOWN_FIELDS["s_class_render_desc"]</c> (byte-verified against the smoke
/// descriptor <c>[0x9B4A]</c> and the chaff descriptor <c>[0x50D0]</c>), plus this type's own read
/// of all 23 shipped descriptors out of (see <see cref="ClassRegistry"/>).
/// </para>
/// <para>
/// Callbacks are kept as <b>identifiers</b> — <c>image@</c> byte offsets into the unpacked L1 image —
/// never as delegates.  The port binds behaviour by name later
/// (<c>src/CYAC.Port.Core/README.md</c>: the original identifiers live in metadata, not in
/// semantics).  <c>0</c> means the original stored a NULL far pointer.
/// </para>
/// <para>
/// <b>Report-only finding:</b> this struct and KNOWN_FIELDS["s_mesh_lod_shape_desc"]</c> are the
/// <b>same</b> 14-byte record under two names — <c>+2 prepare</c>, <c>+4</c> gate, <c>+6 draw</c>,
/// <c>+0xA</c> list, <c>+0xC</c> flags line up field for field, and <c>s_mesh_lod_shape_desc</c>'s
/// finer <c>+0xC/+0xD</c> split agrees with B8's "flags bit9 = [+0xD] bit1 draw enable".
/// </para>
/// </remarks>
[OriginalStruct("s_class_render_desc")]
public sealed record ClassRenderDescriptor
{
    /// <summary>Creates a descriptor from the values read out of the original image.</summary>
    /// <param name="dgroupOffset">DGROUP offset the descriptor lives at, e.g. <c>0x5B02</c>.</param>
    /// <param name="kind">The <c>+0x00</c> kind byte.</param>
    /// <param name="elementCount">The <c>+0x01</c> element count.</param>
    /// <param name="prepareCallbackImageOffset">Resolved <c>image@</c> of the <c>+0x02</c> prepare callback; 0 = NULL.</param>
    /// <param name="drawCallbackImageOffset">Resolved <c>image@</c> of the <c>+0x06</c> draw callback; 0 = NULL.</param>
    /// <param name="elementListOffset">The <c>+0x0A</c> near pointer to the element list.</param>
    /// <param name="flags">The <c>+0x0C</c> flags word.</param>
    public ClassRenderDescriptor(
        int dgroupOffset,
        byte kind,
        byte elementCount,
        int prepareCallbackImageOffset,
        int drawCallbackImageOffset,
        ushort elementListOffset,
        ushort flags)
    {
        DgroupOffset = dgroupOffset;
        Kind = kind;
        ElementCount = elementCount;
        PrepareCallbackImageOffset = prepareCallbackImageOffset;
        DrawCallbackImageOffset = drawCallbackImageOffset;
        ElementListOffset = elementListOffset;
        Flags = flags;
    }

    /// <summary>DGROUP offset of the descriptor itself — metadata, never dereferenced by the port.</summary>
    public int DgroupOffset { get; }

    /// <summary>The <c>+0x00</c> kind byte.  Ranges 0x01..0x3C across the shipped classes.</summary>
    [OriginalField("+0x00", "kind_u8")]
    public byte Kind { get; }

    /// <summary>The <c>+0x01</c> element count: how many entries <see cref="ElementListOffset"/> holds.</summary>
    [OriginalField("+0x01", "elem_count_u8")]
    public byte ElementCount { get; }

    /// <summary>
    /// The <c>+0x02</c> prepare callback as an <c>image@</c> offset (0 = NULL on disk).
    /// </summary>
    /// <remarks>
    /// Called as <c>lcall [si+2]</c> @<c>image@0x1E1E2</c>, gated by <c>cmp word [si+4],0</c>
    /// @<c>image@0x1E1DC</c> — i.e. the gate tests the callback's <b>segment</b> word.
    /// </remarks>
    [OriginalField("+0x02", "prepare_cb_farptr")]
    public int PrepareCallbackImageOffset { get; }

    /// <summary>
    /// The <c>+0x06</c> draw callback as an <c>image@</c> offset.  NULL in all 23 shipped
    /// descriptors — it is runtime-patched with mesh-JIT heap1 code.
    /// </summary>
    [OriginalField("+0x06", "draw_cb_farptr")]
    public int DrawCallbackImageOffset { get; }

    /// <summary>The <c>+0x0A</c> near pointer to the element list (<c>s_sprite_class_elem</c>, 8 B each).</summary>
    [OriginalField("+0x0A", "elem_list_nearptr_u16")]
    public ushort ElementListOffset { get; }

    /// <summary>The <c>+0x0C</c> flags word; bit9 (= <c>[+0x0D]</c> bit1) is the draw enable.</summary>
    [OriginalField("+0x0C", "flags_u16")]
    public ushort Flags { get; }

    /// <summary>True when the original stored a non-NULL prepare callback.</summary>
    public bool HasPrepareCallback => PrepareCallbackImageOffset != 0;

    /// <summary>True when the original stored a non-NULL draw callback (none of the shipped classes do).</summary>
    public bool HasDrawCallback => DrawCallbackImageOffset != 0;
}
