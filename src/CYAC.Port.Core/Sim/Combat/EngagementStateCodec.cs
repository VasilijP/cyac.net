using CYAC.Port.Core.Model.Combat;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>One named span of the 55-byte engagement record — the reconciled layout table.</summary>
/// <param name="Offset">Byte offset inside the record.</param>
/// <param name="Length">How many bytes the field occupies.</param>
/// <param name="Name">The port's name for it.</param>
/// <param name="Original">
/// The original identifier(s), or <see langword="null"/> when the byte carries no decoded name.
/// </param>
/// <param name="Sites">
/// How many direct <c>[0xED54+N]</c> access sites the C1 census found for the field's first byte
/// across the whole image (0 = the field is only ever reached through an <c>es:</c>-relative pointer).
/// </param>
public readonly record struct EngagementFieldSpan(
    int Offset, int Length, string Name, string? Original, int Sites);

/// <summary>
/// Decodes and encodes the 55-byte engagement record, and carries its reconciled layout table.
/// </summary>
/// <remarks>
/// <para>
/// <b>The record has THREE readings in the project's own notes and they are the SAME BYTES.</b>
/// They were reconciled by a census of every direct <c>[imm16]</c> operand in
/// <c>[0xED1E..0xEDC8)</c> over every function that names one (1,201 sites, 90 distinct addresses).
/// </para>
/// <list type="number">
/// <item><description>the <b>VM scratch</b> overlay — the block copied to <c>[0xED54]</c>, which is
/// why <c>[0xED54+N]</c> and <c>record+N</c> are the same field;</description></item>
/// <item><description>the <b>expiry-list node</b> overlay — <c>+0x07</c> next, <c>+0x0B</c>
/// deadline, <c>+0x0D</c> subtype, <c>+0x2D</c> owner: four fields of the same record, reached
/// through the arena rather than the scratch;</description></item>
/// <item><description>the fields the census found — <c>+0x04</c> hit points,
/// <c>+0x25</c> speed, <c>+0x06</c> bit3 — all confirmed here from the bytes.</description></item>
/// </list>
/// <para>
/// The two conflicts are resolved as ONE field each, not two:
/// <c>+0x0D</c> (<c>slot_phase_u8</c> ≡ <c>subtype_u8</c>) and <c>+0x2D</c>
/// (<c>hit_flag_u16</c> ≡ <c>owner</c>) — see <see cref="EngagementState.Phase"/> and
/// <see cref="EngagementState.PhaseWord"/> for the site lists.
/// </para>
/// </remarks>
public static class EngagementStateCodec
{
    /// <summary>
    /// The reconciled layout: every one of the 55 bytes is inside exactly one entry, in ascending
    /// order, with no gap and no overlap.  Aliases that OVERLAP a listed field
    /// (<c>+0x26</c> the speed mid-word, <c>+0x32</c> the facing octant) are properties of
    /// <see cref="EngagementState"/>, not entries here — otherwise "every byte exactly once" could
    /// not hold, and that invariant is what makes the round trip auditable.
    /// </summary>
    public static IReadOnlyList<EngagementFieldSpan> Layout { get; } =
    [
        new(0x00, 2, "PrototypeRef",           "class_proto_nearptr",        38),
        new(0x02, 2, "OwnerObjectRef",         "player_slot_nearptr",        24),
        new(0x04, 1, "HitPoints",              null,                          0),
        new(0x05, 1, "Flags",                  "slot_flags_u8",              41),
        new(0x06, 1, "LoadState",              "load_state_u8",               6),
        new(0x07, 2, "NextNode",               "next_nearptr",                0),
        new(0x09, 2, "Unknown0x09",            null,                          1),
        new(0x0B, 2, "FrameDeadline",          "frame_deadline_u16",          1),
        new(0x0D, 1, "Phase",                  "slot_phase_u8 / subtype_u8", 31),
        new(0x0E, 2, "FsmLoopCount",           "fsm_loop_count_u16",         10),
        new(0x10, 1, "TypeSlotIndex",          null,                         17),
        new(0x11, 1, "AcquisitionState",       "acq_state_u8",               13),
        new(0x12, 4, "InitParams",             "init_params_u8x4",            0),
        new(0x16, 1, "Unknown0x16",            null,                          3),
        new(0x17, 2, "RandomPhaseIndex",       "random_phase_index_u16",     10),
        new(0x19, 2, "Unknown0x19",            null,                          3),
        new(0x1B, 2, "AcquisitionTarget",      "acq_current_target_nearptr", 62),
        new(0x1D, 1, "Unknown0x1D",            null,                          1),
        new(0x1E, 2, "Unknown0x1E",            null,                          0),
        new(0x20, 2, "ScriptSegment",          "script_seg_u16",              6),
        new(0x22, 2, "ScriptPc",               "script_pc_i16",              28),
        new(0x24, 1, "ScriptTimer",            null,                         27),
        new(0x25, 4, "SpeedQ8",                null,                         30),
        new(0x29, 2, "ExpiryFrame",            "expiry_frame_u16",            1),
        new(0x2B, 1, "Unknown0x2B",            null,                          3),
        new(0x2C, 1, "NewEngagementFlag",      "new_engagement_flag_u8",     38),
        new(0x2D, 2, "PhaseWord",              "hit_flag_u16 / owner",       47),
        new(0x2F, 2, "HeadingTarget",          "heading_target_u16",         50),
        new(0x31, 2, "ElevationTarget",        "elevation_target_u16",       35),
        new(0x33, 2, "AltitudeDelta",          "altitude_delta_i16",         21),
        new(0x35, 2, "Unknown0x35",            null,                          7),
        // +0x36 is the record's last byte and the LOW half of [0xED8A]; the u16 that DGROUP holds
        // there straddles the end of the copied region — see EngagementState.SpawnFrameThresholdLow.
    ];

    /// <summary>Reads a block from raw bytes.</summary>
    /// <param name="source">
    /// At least <see cref="EngagementState.StateBytes"/> bytes; only the first 55 are read.
    /// </param>
    public static EngagementState Decode(ReadOnlySpan<byte> source) =>
        new(source[..EngagementState.StateBytes]);

    /// <summary>Writes a block's raw bytes.</summary>
    /// <param name="state">The block.</param>
    /// <param name="destination">At least <see cref="EngagementState.StateBytes"/> bytes.</param>
    public static void Encode(EngagementState state, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Bytes.CopyTo(destination);
    }

    /// <summary>The block's bytes as a fresh array — the encode half of a round-trip test.</summary>
    /// <param name="state">The block.</param>
    public static byte[] Encode(EngagementState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return [.. state.Bytes];
    }

    /// <summary>The layout entry that contains a byte offset, or null when it is past the record.</summary>
    /// <param name="offset">A byte offset inside the record.</param>
    public static EngagementFieldSpan? FieldAt(int offset)
    {
        foreach (EngagementFieldSpan span in Layout)
        {
            if (offset >= span.Offset && offset < span.Offset + span.Length)
            {
                return span;
            }
        }

        return null;
    }

    /// <summary>A human name for a byte offset, for a verification test's diff output.</summary>
    /// <param name="offset">A byte offset inside the record.</param>
    public static string Describe(int offset) =>
        FieldAt(offset) is { } span
            ? $"+0x{offset:X2} ({span.Name}{(span.Original is null ? "" : $" / {span.Original}")})"
            : $"+0x{offset:X2} (SpawnFrameThresholdLow)";
}
