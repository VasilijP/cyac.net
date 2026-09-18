using System.Buffers.Binary;
using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.Combat;

/// <summary>
/// The engagement record — the 55-byte block the combat kernel keeps for every object that can fight,
/// and the node the expiry list threads through.  The original is <c>s_engagement_state</c>; the SAME
/// bytes are also <c>s_engagement_list_node</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two homes, one layout.</b>  The block lives INSIDE a <see cref="World.WorldObject"/>'s arena
/// record — at <c>+0x12</c> when the object's flag word has bit1 (<c>NoOrientation</c>) set, else at
/// <c>+0x18</c>.  <c>object_pool_get_engagement_fieldoff @image@0x0225A</c> is literally
/// <c>mov es,[0x94] / test word es:[bx+2],2 / je → lea ax,[bx+0x18] ; else lea ax,[bx+0x12]</c>.
/// <c>engagement_state_snapshot @image@0x02290</c> copies it to the DGROUP scratch at
/// <c>[0xED54]</c>, the kernel works on the scratch, and
/// <c>engagement_state_restore @image@0x02316</c> copies it back.  Every global named
/// <c>g_engagement_*</c> in <c>[0xED54..0xED8A]</c> is therefore a FIELD of this record seen through
/// the scratch — see <see cref="EngagementStateCodec"/> for the offset table with its access-site
/// census.
/// </para>
/// <para>
/// <b>Three allocation sizes, decided by the prototype.</b>  <c>spawn_dispatch_object</c> builds the
/// block on its own stack frame (<c>lea bx,[bp-0x3e]</c> @<c>image@0x06FB6</c>), runs
/// <c>engagement_list_node_init @image@0x07046</c> over it and then hands it to
/// <c>pool_arena_write_or_abort</c> with one of three sizes:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="StubBytes"/> = 5 when the prototype's <c>+0x0C</c> bit3 is CLEAR
/// (<c>test byte [bx+0xc],8 / je</c> @<c>image@0x06FBF</c> → <c>mov ax,5</c> @<c>image@0x06FF6</c>) —
/// only <c>+0x00</c>, <c>+0x02</c> and <c>+0x04</c> exist;</description></item>
/// <item><description><see cref="StateBytesCompact"/> = 32 (<c>mov ax,0x20</c> @<c>image@0x06FCF</c>),
/// after which <c>or byte es:[bx+5],0x10</c> @<c>image@0x06FE2</c> stamps
/// <see cref="EngagementStateFlags.CompactBlock"/>;</description></item>
/// <item><description><see cref="AllocatedBytesFull"/> = 58 (<c>mov ax,0x3a</c>
/// @<c>image@0x06FED</c>) — of which snapshot/restore move only the first
/// <see cref="StateBytes"/> = 55.</description></item>
/// </list>
/// <para>
/// The three sizes are visible in the arena as record strides: measured on step 58,500, the 89-entry
/// chain from the player object shows stride <c>0x18</c> after the 77 objects with no block,
/// <c>0x1D</c> = 0x18+5 after the 3 stub objects and <c>0x52</c> = 0x18+0x3A after the 5 full ones.
/// </para>
/// <para>
/// <b>Byte-backed on purpose.</b>  Several of the original's fields OVERLAP (the <c>+0x25</c>
/// 32-bit speed and its <c>+0x26</c> "&gt;&gt;8" mid-word view; <c>+0x31</c>'s word and
/// <c>+0x32</c>'s byte), so a disjoint C# field list cannot represent the record faithfully.  This
/// type therefore owns the 55 raw bytes and exposes named accessors over them — which is also what
/// makes <see cref="EngagementStateCodec"/>'s round trip exact by construction, unknown bytes
/// included.
/// </para>
/// <para>
/// Field class: <b>INT-only</b> throughout — AI-VM state, flags, timers, list links and the RNG-seeded
/// phase index are the reproducible spine.  <see cref="SpeedQ8"/> is the integer half of a DUAL pair
/// (the float kernel keeps its own).
/// </para>
/// </remarks>
[OriginalStruct("s_engagement_state")]
public sealed class EngagementState
{
    /// <summary>Bytes snapshot/restore move for a full block: 55 (<c>0x37</c>).</summary>
    /// <remarks>
    /// <c>engagement_state_snapshot</c>: <c>mov cx,0x1b / rep movsw / movsb</c>
    /// (<c>image@0x022BB..0x022C0</c>) = 27 words + 1 byte.
    /// <c>engagement_state_restore</c>: <c>mov cx,0x37</c> (<c>image@0x0232C</c>).
    /// </remarks>
    public const int StateBytes = 0x37;

    /// <summary>Bytes snapshot/restore move for a compact block: 32 (<c>0x20</c>).</summary>
    /// <remarks>
    /// <c>mov cx,0x10 / rep movsw</c> (<c>image@0x022A6</c>) and <c>mov cx,0x20</c>
    /// (<c>image@0x02321</c>).
    /// </remarks>
    public const int StateBytesCompact = 0x20;

    /// <summary>Arena bytes a full block occupies: 58 (<c>0x3A</c>) — three more than it copies.</summary>
    public const int AllocatedBytesFull = 0x3A;

    /// <summary>Arena bytes a non-engaging object's stub block occupies: 5.</summary>
    public const int StubBytes = 5;

    private readonly byte[] _bytes = new byte[StateBytes];

    /// <summary>Creates an all-zero block.</summary>
    public EngagementState()
    {
    }

    /// <summary>Creates a block from 55 raw bytes.</summary>
    /// <param name="source">Exactly <see cref="StateBytes"/> bytes, in the original's order.</param>
    public EngagementState(ReadOnlySpan<byte> source)
    {
        if (source.Length != StateBytes)
        {
            throw new ArgumentException(
                $"an engagement block is {StateBytes} bytes, not {source.Length}", nameof(source));
        }

        source.CopyTo(_bytes);
    }

    /// <summary>The record's raw bytes — the encode side of the codec's round trip.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>The record's raw bytes, writable — for the copy machinery only.</summary>
    internal Span<byte> MutableBytes => _bytes;

    /// <summary>
    /// <c>+0x00</c> — the DGROUP near offset of this object's <see cref="World.EngagementPrototype"/>
    /// (<c>s_engagement_class_proto</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not the object's <see cref="World.ClassRecord"/> pointer</b> — settled from the bytes,
    /// closing an open item NEGATIVE.  Measured on the 11
    /// block-carrying arena entries of <c>v11_b2_clear_det.c1pool.ctr</c> step 58,500: the object's
    /// <c>+0x00</c> descriptor takes the values <c>0x5334 / 0x6BBE / 0x7E3E / 0x8FFC / 0xA552</c> (the
    /// class registry at DGROUP <c>0x4FC0..0xA552</c>) while this word takes <c>0x1812 / 0x1B48 /
    /// 0x1BFC / 0x24E8 / 0xEF22</c> — a different DGROUP region, 0/11 equal.
    /// </para>
    /// <para>
    /// What ties them: the ENGAGEMENT PROTOTYPE's own <c>+0x00</c> word IS the class-record pointer.
    /// Read from the image, DGROUP <c>0x1812</c> (image@0x3D572) starts <c>BE 6B</c> = <c>0x6BBE</c>,
    /// <c>0x1B48</c> (image@0x3D8A8) starts <c>52 A5</c> = <c>0xA552</c>, <c>0x1BFC</c> starts
    /// <c>3E 7E</c>, <c>0x24E8</c> starts <c>34 53</c> — exactly the class pointer of every object
    /// that uses that prototype, 4/4.  So <see cref="World.EngagementPrototype"/> WRAPS
    /// <see cref="World.ClassRecord"/>; it is not a view of it.
    /// </para>
    /// <para>
    /// Readers: <c>engagement_expiry_loop</c>'s engage-capable gate
    /// (<c>mov bx,[0xED54] / test byte [bx+0xc],8</c> @<c>image@0x073D4</c>),
    /// <c>engagement_slot_fire_handler</c>'s no-engagement sentinel
    /// (<c>cmp byte [bx+9],0xff</c> @<c>image@0x0BDD8</c>), <c>engagement_state_snapshot</c>'s
    /// arc-parameter fetch (<c>mov si,[0xED54] / mov ax,[si+0xc] / and ax,3 / mov si,[si+0x26]</c>
    /// @<c>image@0x022EA..0x02305</c>), and 38 direct <c>[0xED54]</c> sites.
    /// </para>
    /// </remarks>
    [OriginalField("+0x00", "class_proto_nearptr")]
    public ushort PrototypeRef { get => Word(0x00); set => SetWord(0x00, value); }

    /// <summary>
    /// <c>+0x02</c> — the pool-arena near offset of the object this block belongs to (a BACK
    /// pointer).
    /// </summary>
    /// <remarks>
    /// <c>engagement_list_node_init</c> writes it (<c>mov word ptr [bx+2],ax</c>
    /// @<c>image@0x07056</c>) with the object <c>spawn_dispatch_object</c> just allocated;
    /// <c>engagement_expiry_loop</c>'s departure arm reads it back as the argument of
    /// <c>target_departure_cleanup</c> (<c>mov bx,es:[bx+2]</c> @<c>image@0x0738D</c>);
    /// <c>engagement_slot_fire_handler</c> pushes it @<c>image@0x0BF8A</c>.  Measured: on all 11
    /// block-carrying entries of the reference arena it equals the entry's own offset, i.e.
    /// <c>block_offset − 0x18</c> — a free validity check the port's codec asserts. The scanner
    /// name is the SCRATCH reading — <c>[0xED56]</c> is indeed "the slot the scratch belongs
    /// to"; in the record it is simply the owner.
    /// </remarks>
    [OriginalField("+0x02", "player_slot_nearptr")]
    public ushort OwnerObjectRef { get => Word(0x02); set => SetWord(0x02, value); }

    /// <summary>
    /// <c>+0x04</c> — <b>hit points</b> (structural strength), a byte.
    /// </summary>
    /// <remarks>
    /// Seeded from the prototype at spawn (<c>mov si,[bx] / mov cl,[si+9] / mov byte [bx+4],cl</c>
    /// @<c>image@0x0704E..0x07053</c>) and decremented by the damage resolver
    /// (<c>mov al,byte es:[bx+4]</c> @<c>image@0x0BF42</c>, <c>mov byte es:[bx+4],al</c>
    /// @<c>image@0x0C082</c>; <c>cmp byte es:[bx+4],0</c> @<c>image@0x0BF38</c> short-circuits an
    /// already-dead object).  Measured against the prototypes' <c>+0x09</c> byte on the reference
    /// arena: <c>0x50 / 0xA0 / 0x64 / 0x01</c> match 5/5 undamaged blocks.
    /// </remarks>
    public byte HitPoints { get => _bytes[0x04]; set => _bytes[0x04] = value; }

    /// <summary><c>+0x05</c> — the flags byte.  See <see cref="EngagementStateFlags"/>.</summary>
    [OriginalField("+0x05", "slot_flags_u8")]
    public EngagementStateFlags Flags
    {
        get => (EngagementStateFlags)_bytes[0x05];
        set => _bytes[0x05] = (byte)value;
    }

    /// <summary>
    /// <c>+0x06</c> — the load/latch byte.  bit1 is cleared when the engagement is (re)armed
    /// (<c>and byte ptr [si+6],0xfd</c> @<c>image@0x070D5</c> in
    /// <c>engagement_list_node_expiry_init</c>); bit3 is the approach-FX latch the damage resolver
    /// sets (<c>or byte ptr es:[bx+6],8</c> @<c>image@0x0BF9A</c>).
    /// </summary>
    [OriginalField("+0x06", "load_state_u8")]
    public byte LoadState { get => _bytes[0x06]; set => _bytes[0x06] = value; }

    /// <summary>
    /// <c>+0x07</c> — the expiry list's NEXT link: the pool-arena near offset of the next block, or
    /// 0 for the tail.  This is <c>s_engagement_list_node.next_nearptr</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read and written as <c>es:[bx+7]</c> by every list routine
    /// (<c>engagement_list_node_sorted_insert @image@0x02352</c>,
    /// <c>engagement_list_sorted_insert @image@0x07194</c>,
    /// <c>engagement_list_node_unlink @image@0x070DB</c>,
    /// <c>engagement_list_sort_rebuild @image@0x07154</c>,
    /// <c>engagement_expiry_loop @image@0x072F8</c>).  It has NO scratch alias:
    /// <c>[0xED5B]</c> has zero direct access sites in the combat cluster, because the list is
    /// always walked in the arena, never through the scratch copy.
    /// </para>
    /// <para>
    /// No <see cref="OriginalFieldAttribute"/>: the scanner files this offset under
    /// <c>s_engagement_list_node</c>, a SEPARATE struct — which the C1 census shows is the same
    /// record.  The attribute goes on once the parent merges the two.
    /// </para>
    /// </remarks>
    public ushort NextNode { get => Word(0x07); set => SetWord(0x07, value); }

    /// <summary>
    /// <c>+0x09</c> — a word <c>engagement_list_node_init</c> zeroes
    /// (<c>mov word ptr [bx+9],ax</c> @<c>image@0x07074</c>, <c>ax = 0</c>); one read site
    /// (<c>[0xED5D]</c> in <c>weapon_fire_combat_loop_per_shot</c>).  Role open.
    /// </summary>
    public ushort Unknown0x09 { get => Word(0x09); set => SetWord(0x09, value); }

    /// <summary>
    /// <c>+0x0B</c> — the expiry list's SORT KEY: the frame at which this node is next due.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>engagement_expiry_loop</c> pops while <c>es:[head+0x0B] &lt;= g_master_frame_counter
    /// [0xF0C8]</c> (<c>cmp word ptr es:[bx+0xb],ax / jbe</c> @<c>image@0x07320</c> — an UNSIGNED
    /// compare), and every insert orders by it (<c>jbe</c> @<c>image@0x02392</c>,
    /// <c>jae</c> @<c>image@0x071E4</c>).  <c>spawn_dispatch_object</c> seeds it from a masked
    /// random draw (<c>image@0x0700B..0x07029</c>).
    /// </para>
    /// <para>
    /// Now <c>g_engagement_frame_deadline_u16</c>, RENAMED R1 *(the scratch name reads it as a delta; it is an
    /// ABSOLUTE frame deadline — the loop compares it against the master frame counter directly. Measured on the
    /// reference trace: counter 82, node deadlines 82 and 83.)*
    /// </para>
    /// </remarks>
    public ushort FrameDeadline { get => Word(0x0B); set => SetWord(0x0B, value); }

    /// <summary>
    /// <c>+0x0D</c> — the engagement's PHASE / subtype byte.
    /// </summary>
    /// <remarks>
    /// One field, two scanner names: <c>s_engagement_state.slot_phase_u8</c> (the VM's own reading —
    /// <c>engagement_script_interpreter</c> writes it 19× through <c>[0xED61]</c>) and
    /// <c>s_engagement_list_node.subtype_u8</c> (the expiry loop's reading —
    /// <c>cmp byte ptr es:[bx+0xd],0x0a</c> @<c>image@0x0732F</c> selects the dependent-node arm).
    /// Measured over 1,501 frames of <c>v11_b2_clear_det</c>: the list carries phases
    /// <c>2 / 6 / 0x0B / 0x0C</c>, and <c>0x0A</c> never appears — see
    /// <c>EngagementExpiryLoop</c>'s tripwire.
    /// </remarks>
    [OriginalField("+0x0D", "slot_phase_u8")]
    public byte Phase { get => _bytes[0x0D]; set => _bytes[0x0D] = value; }

    /// <summary>
    /// <c>+0x0E</c> — the FSM's loop counter; <c>engagement_list_node_init</c> seeds it to
    /// <c>0xFFFF</c> (<c>image@0x0707D</c>).
    /// </summary>
    [OriginalField("+0x0E", "fsm_loop_count_u16")]
    public ushort FsmLoopCount { get => Word(0x0E); set => SetWord(0x0E, value); }

    /// <summary>
    /// <c>+0x10</c> — <c>g_engagement_type_slot_idx [0xED64]</c>; zeroed by
    /// <c>engagement_list_node_expiry_init</c> (<c>image@0x070AE</c>).
    /// </summary>
    public byte TypeSlotIndex { get => _bytes[0x10]; set => _bytes[0x10] = value; }

    /// <summary>
    /// <c>+0x11</c> — the target-acquisition state machine's state byte
    /// (<c>g_acq_state [0xED65]</c>); zeroed by <c>engagement_list_node_expiry_init</c>
    /// (<c>image@0x070AB</c>).
    /// </summary>
    [OriginalField("+0x11", "acq_state_u8")]
    public byte AcquisitionState { get => _bytes[0x11]; set => _bytes[0x11] = value; }

    /// <summary>
    /// <c>+0x12..+0x15</c> — the four init parameters, copied verbatim from the prototype's
    /// <c>+0x16..+0x19</c> at arm time
    /// (<c>memcpy(&amp;block+0x12, proto+0x16, 4)</c> @<c>image@0x070B1..0x070BF</c>).
    /// </summary>
    [OriginalField("+0x12", "init_params_u8x4")]
    public World.EngagementInitParams InitParams
    {
        get => new(_bytes[0x12], _bytes[0x13], _bytes[0x14], _bytes[0x15]);
        set
        {
            _bytes[0x12] = value.Byte0;
            _bytes[0x13] = value.Byte1;
            _bytes[0x14] = value.Byte2;
            _bytes[0x15] = value.Byte3;
        }
    }

    /// <summary>
    /// <c>+0x16</c> — a byte only <c>enemy_target_acquisition_state_machine</c> touches (three
    /// <c>[0xED6A]</c> sites).  Role open.
    /// </summary>
    public byte Unknown0x16 { get => _bytes[0x16]; set => _bytes[0x16] = value; }

    /// <summary>
    /// <c>+0x17</c> — the engagement's random phase index, drawn ONCE at arm time:
    /// <c>mov ax,0xc / lcall prng_rand_bounded / mov word ptr [si+0x17],ax</c>
    /// (<c>image@0x0709C..0x070A6</c>).  This is the only RNG draw
    /// <c>engagement_list_node_expiry_init</c> makes.
    /// </summary>
    [OriginalField("+0x17", "random_phase_index_u16")]
    public ushort RandomPhaseIndex { get => Word(0x17); set => SetWord(0x17, value); }

    /// <summary>
    /// <c>+0x19</c> — a word only the acquisition state machine touches
    /// (three <c>[0xED6D]</c> sites).  Role open.
    /// </summary>
    public ushort Unknown0x19 { get => Word(0x19); set => SetWord(0x19, value); }

    /// <summary>
    /// <c>+0x1B</c> — the pool-arena near offset of the object this engagement is currently tracking
    /// (<c>g_acq_current_target [0xED6F]</c>, 62 direct sites — the busiest field in the record).
    /// <c>target_departure_cleanup</c> clears it on the way out
    /// (<c>mov word ptr es:[si+0x1b],0</c> @<c>image@0x07486</c>).
    /// </summary>
    [OriginalField("+0x1B", "acq_current_target_nearptr")]
    public ushort AcquisitionTarget { get => Word(0x1B); set => SetWord(0x1B, value); }

    /// <summary>
    /// <c>+0x1D</c> — one read site (<c>[0xED71]</c>, acquisition state machine).  Role open.
    /// </summary>
    public byte Unknown0x1D { get => _bytes[0x1D]; set => _bytes[0x1D] = value; }

    /// <summary>
    /// <c>+0x1E</c> — a word <c>engagement_list_node_init</c> zeroes unconditionally
    /// (<c>mov word ptr [bx+0x1e],0</c> @<c>image@0x07059</c>), with no direct scratch reader.
    /// Role open.
    /// </summary>
    public ushort Unknown0x1E { get => Word(0x1E); set => SetWord(0x1E, value); }

    /// <summary>
    /// <c>+0x20</c> — the far segment of the engagement's loaded AI program
    /// (<c>g_engagement_script_seg [0xED74]</c>); 0 means "no program".  Its only allocator is
    /// <c>ensure_script_loaded @image@0x048BF</c> (<c>far_heap_alloc(0x32)</c>).
    /// </summary>
    [OriginalField("+0x20", "script_seg_u16")]
    public ushort ScriptSegment { get => Word(0x20); set => SetWord(0x20, value); }

    /// <summary>
    /// <c>+0x22</c> — the AI program counter (<c>g_engagement_script_pc [0xED76]</c>), a SIGNED
    /// word whose <c>−1</c> is the "no program" sentinel.
    /// </summary>
    /// <remarks>
    /// <c>engagement_list_node_init</c> seeds it to <c>0xFFFF</c> (<c>image@0x0707A</c>) and
    /// <c>engagement_state_snapshot</c>'s compact arm forces it back to <c>0xFFFF</c>
    /// (<c>image@0x022B2</c>).  C0 §4.5 measured the interpreter reading PAST the 50-byte program
    /// block this indexes (max non-sentinel value 70) — a shipped out-of-bounds read C4 decides on;
    /// this type only stores the word.
    /// </remarks>
    [OriginalField("+0x22", "script_pc_i16")]
    public short ScriptPc { get => (short)Word(0x22); set => SetWord(0x22, (ushort)value); }

    /// <summary>
    /// <c>+0x24</c> — <c>g_engagement_timer_init [0xED78]</c>, a byte the interpreter increments and
    /// tests 21 times.  Role: the script's own step timer.
    /// </summary>
    public byte ScriptTimer { get => _bytes[0x24]; set => _bytes[0x24] = value; }

    /// <summary>
    /// <c>+0x25</c> — the object's SPEED as a 32-bit Q8 fixed-point value (feet per second &lt;&lt; 8), the integer
    /// half of a DUAL pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Byte-proven as one 32-bit quantity by the carry-chained pairs in
    /// <c>engagement_speed_step_toward (ex-engagement_arc_accum_step_toward)</c>: <c>sub word ptr [0xed79],ax / sbb word ptr
    /// [0xed7b],dx</c> (<c>image@0x04A90</c>/<c>0x04A94</c>),
    /// <c>add [0xed79],ax / adc [0xed7b],dx</c> (<c>image@0x04B3E</c>/<c>0x04B42</c>), and the
    /// matching <c>cmp ax,[0xed79] / cmp dx,[0xed7b]</c> pair — 30 sites in total on the two
    /// halves, always together.
    /// </para>
    /// <para>
    /// <see cref="SpeedFps"/> is the <c>&gt;&gt;8</c> view the code reads through the MIDDLE word
    /// <c>[0xED7A]</c> (20 read sites, never written) — the classic 8086 shortcut for the integer
    /// part of a Q8 value.  The flight kernel writes this field of the PLAYER's block every frame:
    /// C0's per-stage census records the player pool object's <c>+0x3D..+0x3F</c> moving across
    /// CS2→CS3, and <c>+0x3D = 0x18 + 0x25</c>.
    /// </para>
    /// </remarks>
    public int SpeedQ8
    {
        get => BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(0x25, 4));
        set => BinaryPrimitives.WriteInt32LittleEndian(_bytes.AsSpan(0x25, 4), value);
    }

    /// <summary>
    /// The middle word of <see cref="SpeedQ8"/> — <c>(speed &gt;&gt; 8) &amp; 0xFFFF</c>, which is
    /// what <c>[0xED7A]</c> reads.  Read-only: the original never writes through this alias.
    /// </summary>
    public ushort SpeedFps => Word(0x26);

    /// <summary>
    /// <c>+0x29</c> — the frame at which the whole engagement expires:
    /// <c>al = 0x3C; mul byte ptr [proto+0x2d]; add ax,[0xF0C8]; mov [si+0x29],ax</c>
    /// (<c>image@0x070C9..0x070D2</c>) — i.e. <c>60 × prototype.ExpirySeconds + frame counter</c>.
    /// Distinct from <see cref="FrameDeadline"/>, which is the per-node re-visit time.
    /// </summary>
    [OriginalField("+0x29", "expiry_frame_u16")]
    public ushort ExpiryFrame { get => Word(0x29); set => SetWord(0x29, value); }

    /// <summary>
    /// <c>+0x2B</c> — a byte <c>engagement_list_node_init</c> zeroes
    /// (<c>mov byte ptr [bx+0x2b],al</c> @<c>image@0x07093</c>).  Three <c>[0xED7F]</c> sites, all in
    /// <c>engagement_shot_angle_select_by_range</c>.  Role open.
    /// </summary>
    public byte Unknown0x2B { get => _bytes[0x2B]; set => _bytes[0x2B] = value; }

    /// <summary>
    /// <c>+0x2C</c> — the "this is a NEW engagement" flag
    /// (<c>g_engagement_new_engagement_flag [0xED80]</c>); the interpreter writes it 35 times.
    /// </summary>
    [OriginalField("+0x2C", "new_engagement_flag_u8")]
    public byte NewEngagementFlag { get => _bytes[0x2C]; set => _bytes[0x2C] = value; }

    /// <summary>
    /// <c>+0x2D</c> — a POLYMORPHIC word: a pool-arena object reference, an angle, or a bit pair,
    /// depending on <see cref="Phase"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scanner carries two conflicting names for the same offset —
    /// <c>s_engagement_state.hit_flag_u16</c> and <c>s_engagement_list_node.owner</c> — and the
    /// bytes say BOTH readings occur:
    /// </para>
    /// <list type="bullet">
    /// <item><description>as an OWNER reference: <c>engagement_expiry_loop</c>'s phase-<c>0x0A</c>
    /// arm loads <c>es:[head+0x2d]</c> and walks the chain looking for the NODE at that address
    /// (<c>image@0x07348..0x07365</c>).  Measured on the reference trace: phase-<c>0x0C</c> nodes
    /// carry <c>0x4DF6</c> — the player's own pool object — 2,281 times.</description></item>
    /// <item><description>as an ANGLE: <c>engagement_slot_angle_update</c> compares it against
    /// <c>0x1680</c>, <c>0x21C0</c>, <c>0x21C8</c> and <c>0x7FFE</c>
    /// (<c>image@0x06710..0x0679A</c>) and adds it to a bearing.</description></item>
    /// <item><description>as BIT FLAGS: <c>weapon_fire_outcome_and_evasion_init</c> does
    /// <c>or byte ptr [0xed81],1</c> / <c>,2</c> (<c>image@0x08883</c>, <c>image@0x088B0</c>) and
    /// <c>enemy_spawn_with_angle_pos_init</c> tests and clears them
    /// (<c>image@0x089C6</c>, <c>image@0x08B0E</c>).</description></item>
    /// </list>
    /// <para>
    /// 47 direct sites, 40 of them word-wide, so the WIDTH is settled (u16) even though the meaning
    /// is not.  The port keeps one field and one name; see the report's §4 scanner proposal.
    /// </para>
    /// </remarks>
    [OriginalField("+0x2D", "hit_flag_u16")]
    public ushort PhaseWord { get => Word(0x2D); set => SetWord(0x2D, value); }

    /// <summary>
    /// <c>+0x2F</c> — the heading the engagement is steering towards
    /// (<c>g_engagement_heading_target [0xED83]</c>, 50 sites).
    /// </summary>
    [OriginalField("+0x2F", "heading_target_u16")]
    public ushort HeadingTarget { get => Word(0x2F); set => SetWord(0x2F, value); }

    /// <summary>
    /// <c>+0x31</c> — the elevation (pitch) target
    /// (<c>g_engagement_elevation_target [0xED85]</c>, 35 sites).
    /// </summary>
    [OriginalField("+0x31", "elevation_target_u16")]
    public ushort ElevationTarget { get => Word(0x31); set => SetWord(0x31, value); }

    /// <summary>
    /// <c>+0x32</c> — <c>g_engagement_facing_octant [0xED86]</c>, a byte that OVERLAPS
    /// <see cref="ElevationTarget"/>'s high half.  Both aliases are live in the image (7 sites here,
    /// 35 there), so the port exposes both over the same byte rather than choosing.
    /// </summary>
    public byte FacingOctant { get => _bytes[0x32]; set => _bytes[0x32] = value; }

    /// <summary>
    /// <c>+0x33</c> — the altitude delta to the target
    /// (<c>g_engagement_altitude_delta [0xED87]</c>, i16, 21 sites).
    /// </summary>
    [OriginalField("+0x33", "altitude_delta_i16")]
    public short AltitudeDelta { get => (short)Word(0x33); set => SetWord(0x33, (ushort)value); }

    /// <summary>
    /// <c>+0x35</c> — a word <c>weapon_fire_combat_loop_per_shot</c> owns
    /// (<c>[0xED89]</c>, 7 sites).  Role open.
    /// </summary>
    public ushort Unknown0x35 { get => Word(0x35); set => SetWord(0x35, value); }

    /// <summary>
    /// <c>+0x36</c> — the record's LAST byte, and the LOW half of the word
    /// <c>g_engagement_frame_threshold [0xED8A]</c>.
    /// </summary>
    /// <remarks>
    /// The five sites that touch <c>[0xED8A]</c> are all word-wide (they store <c>0xFFFF</c>, or a
    /// computed frame number) — but the record is 55 bytes, i.e. <c>+0x00..+0x36</c>, so
    /// snapshot/restore carry only this LOW byte per engagement while the HIGH byte lives in DGROUP
    /// only.  Reported as a measured asymmetry, not modelled as a word here.
    /// </remarks>
    public byte SpawnFrameThresholdLow { get => _bytes[0x36]; set => _bytes[0x36] = value; }

    /// <summary>True when the block is the 32-byte compact form.</summary>
    public bool IsCompact => Flags.HasFlag(EngagementStateFlags.CompactBlock);

    /// <summary>How many bytes snapshot/restore move for this block: 32 or 55.</summary>
    public int CopyBytes => IsCompact ? StateBytesCompact : StateBytes;

    /// <summary>An owning copy.</summary>
    public EngagementState Clone() => new(_bytes);

    /// <summary>Overwrites this block's bytes from another.</summary>
    /// <param name="source">The block to copy from.</param>
    /// <param name="count">How many leading bytes to move (32 or 55).</param>
    public void CopyFrom(EngagementState source, int count)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, StateBytes);
        source._bytes.AsSpan(0, count).CopyTo(_bytes);
    }

    /// <summary>A short description for test output.</summary>
    public override string ToString() =>
        $"engagement@owner=0x{OwnerObjectRef:X4} proto=0x{PrototypeRef:X4} hp={HitPoints} "
            + $"phase=0x{Phase:X2} deadline={FrameDeadline} next=0x{NextNode:X4}";

    private ushort Word(int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(offset, 2));

    private void SetWord(int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(_bytes.AsSpan(offset, 2), value);
}
