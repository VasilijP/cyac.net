namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// A live, named view of one <c>s_combat_spawn_record</c> at its DGROUP near offset inside the
/// combat register file.
/// </summary>
/// <remarks>
/// <para>
/// The projectile row addresses a spawn slot exactly this way — <c>SI</c> holds the record's DGROUP
/// offset and every field is <c>[si+N]</c> — so a byte-backed view over
/// <see cref="CombatRegisters"/> is the faithful representation, and it keeps the write set
/// byte-granular for verification.  <see cref="CombatSpawnCodec.RawSpawn"/> stays the value-typed
/// reading of the same 27 bytes.
/// </para>
/// </remarks>
/// <param name="registers">The register file the record lives in.</param>
/// <param name="offset">The record's DGROUP near offset.</param>
public readonly struct SpawnRecordRef(CombatRegisters registers, ushort offset)
{
    private readonly CombatRegisters _registers =
        registers ?? throw new ArgumentNullException(nameof(registers));

    /// <summary>The record's DGROUP near offset — the original's <c>SI</c>.</summary>
    public ushort Offset { get; } = offset;

    /// <summary>The register file the record lives in — for a byte-granular verification diff.</summary>
    public CombatRegisters Registers => _registers;

    /// <summary><c>+0x00</c> — the weapon-class descriptor's DGROUP offset; 0 means the slot is free.</summary>
    public ushort WeaponClassRef
    {
        get => _registers.Word(Offset);
        set => _registers.SetWord(Offset, value);
    }

    /// <summary><c>+0x02</c> — the projectile's own pool object.</summary>
    public ushort PoolObjectRef => _registers.Word(Offset + 0x02);

    /// <summary><c>+0x04</c> — the owner's alternate slot reference.</summary>
    public ushort OwnerOrSlot1 => _registers.Word(Offset + 0x04);

    /// <summary><c>+0x06</c> — the shot's owner id (equal to <c>[0x00C0]</c> for the player's shots).</summary>
    public ushort OwnerId => _registers.Word(Offset + 0x06);

    /// <summary><c>+0x08</c> — the tracked target's pool object; 0 = untracked.</summary>
    public ushort TargetRef
    {
        get => _registers.Word(Offset + 0x08);
        set => _registers.SetWord(Offset + 0x08, value);
    }

    /// <summary><c>+0x0A</c> — the launch-time target argument.</summary>
    public ushort TargetArgumentRef => _registers.Word(Offset + 0x0A);

    /// <summary><c>+0x0C</c> — the Q8 speed, signed 32-bit.</summary>
    public int SpeedQ8
    {
        get => unchecked((int)(_registers.Word(Offset + 0x0C) | (_registers.Word(Offset + 0x0E) << 16)));
        set
        {
            _registers.SetWord(Offset + 0x0C, unchecked((ushort)value));
            _registers.SetWord(Offset + 0x0E, unchecked((ushort)(value >> 16)));
        }
    }

    /// <summary><c>+0x10</c> — the frame at which the target-selection window opens.</summary>
    public ushort TargetScoreGateFrame => _registers.Word(Offset + 0x10);

    /// <summary><c>+0x12</c> — the frame the motor stops burning.</summary>
    public ushort BoostEndFrame => _registers.Word(Offset + 0x12);

    /// <summary><c>+0x14</c> — the frame the shot expires.</summary>
    public ushort ExpireFrame => _registers.Word(Offset + 0x14);

    /// <summary>
    /// <c>+0x16</c> — the 32-bit frame-time at which the next fire-eligibility check is due
    /// (<c>image@0x02746..0x02756</c> sets it to <c>[0xF0D2:0xF0D4] + 0x55</c>).
    /// </summary>
    public int NextEligibilityCheck
    {
        get => unchecked((int)(_registers.Word(Offset + 0x16) | (_registers.Word(Offset + 0x18) << 16)));
        set
        {
            _registers.SetWord(Offset + 0x16, unchecked((ushort)value));
            _registers.SetWord(Offset + 0x18, unchecked((ushort)(value >> 16)));
        }
    }

    /// <summary>
    /// <c>+0x1A</c> — the status flags: bit0 "target locked in", bit1 "firing this frame",
    /// bit2 "altitude window armed", bit3 "level-flight / no tracking".
    /// </summary>
    public byte StatusFlags
    {
        get => _registers.Byte(Offset + 0x1A);
        set => _registers.SetByte(Offset + 0x1A, value);
    }

    /// <summary>True when the slot is in use — <c>cmp word ptr [si],0</c> @<c>image@0x02693</c>.</summary>
    public bool IsActive => WeaponClassRef != 0;
}

/// <summary>
/// A live, named view of one <c>s_pool_arena_entry</c> at its near offset inside the pool arena.
/// </summary>
/// <remarks>
/// The kernel reads and writes objects exactly through these offsets (<c>ES</c> = the pool segment,
/// <c>BX</c> = the near offset), so the view keeps the write set byte-granular.  The flag WORD at
/// <c>+0x02</c> is what <see cref="Model.World.WorldObjectFlags"/> names; the kernel touches three of
/// its bits through the HIGH byte, which is why they appear here as bit numbers of the word.
/// </remarks>
/// <param name="arena">The arena the object lives in.</param>
/// <param name="offset">The object's near offset.</param>
public readonly struct CombatObjectView(PoolArena arena, ushort offset)
{
    private readonly PoolArena _arena = arena ?? throw new ArgumentNullException(nameof(arena));

    /// <summary>The object's arena near offset.</summary>
    public ushort Offset { get; } = offset;

    /// <summary><c>+0x00</c> — the class record's DGROUP near offset.</summary>
    public ushort ClassRef
    {
        get => _arena.Word(Offset);
        set => _arena.SetWord(Offset, value);
    }

    /// <summary><c>+0x02</c> — the flag word.</summary>
    public ushort Flags
    {
        get => _arena.Word((ushort)(Offset + 0x02));
        set => _arena.SetWord((ushort)(Offset + 0x02), value);
    }

    /// <summary>The object's world position — <c>+0x06</c> / <c>+0x0A</c> / <c>+0x0E</c>.</summary>
    public CombatPosition Position
    {
        get => new(ReadInt32(0x06), ReadInt32(0x0A), ReadInt32(0x0E));
        set
        {
            WriteInt32(0x06, value.X);
            WriteInt32(0x0A, value.Y);
            WriteInt32(0x0E, value.Z);
        }
    }

    /// <summary><c>+0x12</c> — the heading orientation word.</summary>
    public short Heading
    {
        get => unchecked((short)_arena.Word((ushort)(Offset + 0x12)));
        set => _arena.SetWord((ushort)(Offset + 0x12), unchecked((ushort)value));
    }

    /// <summary><c>+0x14</c> — the elevation orientation word.</summary>
    public short Elevation
    {
        get => unchecked((short)_arena.Word((ushort)(Offset + 0x14)));
        set => _arena.SetWord((ushort)(Offset + 0x14), unchecked((ushort)value));
    }

    /// <summary>
    /// Word bit11 — <c>test byte es:[bx+3],8</c> (<c>image@0x0298E</c>, <c>image@0x0BDBA</c>):
    /// the object carries an engagement block.
    /// </summary>
    public bool CarriesEngagement => (Flags & 0x0800) != 0;

    /// <summary>
    /// Word bit15 — the projectile's MOTOR bit, set by the boost arm (<c>or es:[bx+3],0x80</c>
    /// @<c>image@0x0282C</c>) and cleared by the coast arm (<c>and es:[bx+3],0x7f</c>
    /// @<c>image@0x02885</c>).
    /// </summary>
    public void SetMotor(bool burning) =>
        Flags = unchecked((ushort)(burning ? Flags | 0x8000 : Flags & 0x7FFF));

    /// <summary>
    /// The engagement block's near offset: <c>+0x12</c> when the flag word's bit1 is SET (the compact
    /// record) and <c>+0x18</c> otherwise — <c>object_pool_get_engagement_fieldoff @image@0x0225A</c>.
    /// </summary>
    public ushort EngagementBlockRef => unchecked((ushort)(Offset + ((Flags & 0x0002) != 0 ? 0x12 : 0x18)));

    /// <summary>
    /// <c>object_pool_get_engagement_nearptr @image@0x02276</c> — the WORD stored at
    /// <see cref="EngagementBlockRef"/>, i.e. the engagement block's <c>+0x00</c>, which an earlier pass proved is
    /// the <c>s_engagement_class_proto</c>'s DGROUP near offset.
    /// </summary>
    public ushort EngagementPrototypeRef => _arena.Word(EngagementBlockRef);

    private int ReadInt32(int delta) => unchecked((int)(
        _arena.Word((ushort)(Offset + delta)) | (_arena.Word((ushort)(Offset + delta + 2)) << 16)));

    private void WriteInt32(int delta, int value)
    {
        _arena.SetWord((ushort)(Offset + delta), unchecked((ushort)value));
        _arena.SetWord((ushort)(Offset + delta + 2), unchecked((ushort)(value >> 16)));
    }
}

/// <summary>
/// A live, named view of one engagement block (<c>s_engagement_state</c>) in the arena, limited to
/// the six fields the PROJECTILE row touches.
/// </summary>
/// <remarks>
/// <see cref="Model.Combat.EngagementState"/> is the whole 55-byte record; this view exists because
/// the damage resolver reads and writes single bytes in place and the verification wants exactly
/// those bytes in the write set, not a decode/encode round trip of all 55.
/// </remarks>
/// <param name="arena">The arena.</param>
/// <param name="offset">The block's near offset.</param>
public readonly struct EngagementBlockView(PoolArena arena, ushort offset)
{
    private readonly PoolArena _arena = arena ?? throw new ArgumentNullException(nameof(arena));

    /// <summary>The block's arena near offset.</summary>
    public ushort Offset { get; } = offset;

    /// <summary><c>+0x00</c> — the class prototype's DGROUP near offset.</summary>
    public ushort PrototypeRef => _arena.Word(Offset);

    /// <summary><c>+0x02</c> — the owning object's arena offset.</summary>
    public ushort OwnerObjectRef => _arena.Word((ushort)(Offset + 0x02));

    /// <summary><c>+0x04</c> — hit points.</summary>
    public byte HitPoints
    {
        get => _arena.Byte((ushort)(Offset + 0x04));
        set => _arena.SetByte((ushort)(Offset + 0x04), value);
    }

    /// <summary><c>+0x05</c> — the flags byte; bit6 = "counts as an enemy kill".</summary>
    public byte Flags => _arena.Byte((ushort)(Offset + 0x05));

    /// <summary><c>+0x06</c> — the load state; bit3 = "already trailing damage smoke".</summary>
    public byte LoadState
    {
        get => _arena.Byte((ushort)(Offset + 0x06));
        set => _arena.SetByte((ushort)(Offset + 0x06), value);
    }
}

/// <summary>
/// The <c>s_weapon_class_desc</c> fields the projectile row reads, addressed by the descriptor's
/// DGROUP near offset through <see cref="ICombatStaticData"/>.
/// </summary>
/// <remarks>
/// The shipped table is 20 records of <see cref="Model.Combat.WeaponClass.RecordBytes"/> bytes at
/// DGROUP <c>0x1158</c> (lists it in <c>staticTables</c>), but the row never indexes it — it
/// follows the near pointer in <c>s_combat_spawn_record[+0x00]</c>.
/// </remarks>
/// <param name="data">The constant-table seam.</param>
/// <param name="offset">The descriptor's DGROUP near offset.</param>
public readonly struct WeaponClassView(ICombatStaticData data, ushort offset)
{
    private readonly ICombatStaticData _data = data ?? throw new ArgumentNullException(nameof(data));

    /// <summary>The descriptor's DGROUP near offset.</summary>
    public ushort Offset { get; } = offset;

    /// <summary>
    /// <c>+0x00</c> — a small kind selector.  Two readers: it indexes the target's prototype in
    /// <c>engagement_fire_authority_compute</c> (<c>image@0x02AE3</c>) and gates the elevation
    /// rescale there (<c>cmp byte ptr [si],0</c> @<c>image@0x02BD7</c>); the eligibility check tests
    /// it for 1 and for 0 (<c>image@0x02D31</c>, <c>image@0x02D88</c>).
    /// </summary>
    public byte Kind => _data.Byte(Offset);

    /// <summary>
    /// <c>+0x03</c> — the altitude-window probability byte; 0 disables the window entirely
    /// (<c>image@0x038CB</c>) and it is the threshold the draw is compared against
    /// (<c>image@0x038FB</c>).
    /// </summary>
    public byte AltitudeWindowChance => _data.Byte(Offset + 0x03);

    /// <summary>
    /// <c>+0x05</c> — the weapon's effective range, scaled by 16 in the fire authority
    /// (<c>image@0x02ACE</c>).
    /// </summary>
    public byte Range => _data.Byte(Offset + 0x05);

    /// <summary>
    /// <c>+0x07</c> — the minimum aim-point altitude, shifted left 8, that
    /// <c>combat_spawn_angle_step</c> clamps a low target up to (<c>image@0x02E1C</c>).
    /// </summary>
    public short MinimumAimAltitude => unchecked((short)_data.Word(Offset + 0x07));

    /// <summary>
    /// <c>+0x09</c> — the ground-proximity radius, shifted left 8, that the same clamp compares the
    /// 2-D proximity against (<c>image@0x02E6A</c>).
    /// </summary>
    public short GroundClampRadius => unchecked((short)_data.Word(Offset + 0x09));

    /// <summary><c>+0x0E</c> — the aiming cone's half-angle (<c>image@0x02D58</c>).</summary>
    public short ConeHalfAngle => unchecked((short)_data.Word(Offset + 0x0E));

    /// <summary><c>+0x12</c> — the per-second turn rate the angle step scales by dt.</summary>
    public short TurnRate => unchecked((short)_data.Word(Offset + 0x12));

    /// <summary><c>+0x14</c> — the muzzle speed.</summary>
    public short InitialSpeed => unchecked((short)_data.Word(Offset + 0x14));

    /// <summary><c>+0x16</c> — the boost ceiling.</summary>
    public short BoostSpeedMax => unchecked((short)_data.Word(Offset + 0x16));

    /// <summary><c>+0x18</c> — the coast floor.</summary>
    public short CoastSpeedMin => unchecked((short)_data.Word(Offset + 0x18));

    /// <summary><c>+0x1A</c> — the boost acceleration per dt.</summary>
    public short BoostAcceleration => unchecked((short)_data.Word(Offset + 0x1A));

    /// <summary><c>+0x1C</c> — the coast deceleration per dt.</summary>
    public short CoastDeceleration => unchecked((short)_data.Word(Offset + 0x1C));

    /// <summary><c>+0x1E</c> — the boost duration in frames; 0 means "no envelope".</summary>
    public byte BoostFrames => _data.Byte(Offset + 0x1E);

    /// <summary><c>+0x20</c> — the target-selection window's half-extent.</summary>
    public short SelectionWindow => unchecked((short)_data.Word(Offset + 0x20));

    /// <summary><c>+0x22</c> — the damage scale.</summary>
    public byte DamageScale => _data.Byte(Offset + 0x22);

    /// <summary><c>+0x23</c> — the damage multiplier.</summary>
    public byte DamageMultiplier => _data.Byte(Offset + 0x23);

    /// <summary><c>+0x24</c> — the class flags (bit0 lethal-effect, bit4 guided).</summary>
    public byte ClassFlags => _data.Byte(Offset + 0x24);

    /// <summary><c>+0x2C</c> — the rounds this shot represents, added to the accuracy counters.</summary>
    public byte AmmoPerShot => _data.Byte(Offset + 0x2C);

    /// <summary>Builds the value-typed <see cref="Model.Combat.WeaponClass"/> fields the speed envelope wants.</summary>
    /// <returns>A weapon-class object carrying the envelope's five fields.</returns>
    public Model.Combat.WeaponClass ToEnvelopeClass() => new()
    {
        InitialSpeed = InitialSpeed,
        BoostSpeedMax = BoostSpeedMax,
        CoastSpeedMin = CoastSpeedMin,
        BoostAcceleration = BoostAcceleration,
        CoastDeceleration = CoastDeceleration,
        BoostFrames = BoostFrames,
    };
}
