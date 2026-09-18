namespace CYAC.Port.Core.Sim;

/// <summary>
/// What a CYEV import produced, including everything it could NOT carry across.
/// </summary>
/// <param name="Log">The imported log.</param>
/// <param name="FormatVersion">The on-disk version the file carried (7, 8 or 9).</param>
/// <param name="EventsRead">How many records the file held.</param>
/// <param name="PitTicksSkipped">
/// <c>PitTick</c> records dropped: in the det era the clock is driven by program landmarks, not by
/// queued events, so a tick record is not input.  A <c>det_v1</c> recording carries none.
/// </param>
/// <param name="UnplacedEvents">
/// Records whose step stamp was <c>NoStep</c> (0xFFFFFFFF).  Under step delivery these are never
/// delivered — the emulator's own det policy blocks on them rather than falling back to the
/// instruction count (a tick fallback made two legs apply different input).  The importer drops them
/// and counts them here.
/// </param>
/// <param name="StepZeroEvents">
/// How many events share step 0.  A recording re-indexed from the pre-det era collapses its whole
/// boot phase there; a natively recorded det session does not.
/// </param>
/// <param name="StepTicks">The recording's step length in ticks (CYEV header, v7+).</param>
/// <param name="SeedWord">The forced mission seed, or null when the session forced none.</param>
/// <param name="EraFlags">
/// The det-era rule set the session was recorded under (CYEV v8 header word; a v7 file reads 0).
/// Bit 0 = the recorder opened IDLE STEPS.
/// </param>
/// <param name="IdleInstrThreshold">
/// The idle-watchdog threshold in emulator instructions (CYEV v9; a pre-v9 file reads the legacy
/// <see cref="CyevReader.LegacyIdleInstr"/>).  It is an EMULATOR quantity and the port's simulation
/// never uses it — it is carried so a log can say which era it came from, and so a divergence
/// between two recordings of "the same" session can name the parameter that differs.
/// </param>
/// <param name="IdleStepCount">
/// How many of the recording's steps were IDLE steps, when the file alone can prove it: a recording
/// whose era flags do not include idle steps has <b>zero</b> by construction, so this is 0.  When the
/// era does open idle steps the count is not in the file (CYEV records carry a step ordinal, not a
/// step kind), and this is null — "unknown", never a guess.
/// </param>
public sealed record CyevImport(
    InputLog Log,
    int FormatVersion,
    int EventsRead,
    int PitTicksSkipped,
    int UnplacedEvents,
    int StepZeroEvents,
    int StepTicks,
    ushort? SeedWord,
    int EraFlags,
    uint IdleInstrThreshold,
    uint? IdleStepCount);

/// <summary>
/// Reads the emulator's <c>det_v1</c>-era recording format (CYEV v7, v8 and v9) into an
/// <see cref="InputLog"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The format is knowledge, not a dependency.</b> This assembly references no other project, so
/// the layouts below are transcribed from the emulator's format documentation rather than imported
/// from it; a shape change on that side is caught by
/// <see cref="MagicText"/>/<see cref="MaxFormatVersion"/> and by the cross-check test, not by the
/// compiler.
/// </para>
/// <para>
/// On-disk layout, little-endian.  All three versions share the 18-byte record
/// (<c>tick u64, kind u16, data u16, data2 u16, step u32</c>) and differ only in the header:
/// </para>
/// <code>
///   +0  'C','Y','E','V'      magic
///   +4  version   u16 (7, 8 or 9)
///   +6  semantics u16        emulator-semantics era stamp
///   +8  lodBoost  u16        LOD-distance cheat factor (1 = vanilla)
///   +10 policy    u16        delivery policy + 1
///   +12 soundArms u16        trajectory-affecting device arms
///   +14 stepTicks u16        det-era step length in INT-8 ticks (0 = not a det recording)
///   +16 seed+1    u32        forced mission seed + 1 (0 = none forced)
///   v7: +20 count u32, records at +24
///   v8: +20 eraFlags u16, +22 count u32, records at +26
///   v9: +20 eraFlags u16, +22 idleInstr u32, +26 count u32, records at +30
/// </code>
/// <para>
/// Record kinds: 0 = key scancode (<c>data</c> = scancode, break in bit 7), 1 = PIT tick,
/// 2 = joystick sample (<c>data</c> = axis X 0..1023, <c>data2</c> = axis Y in bits 0..9 |
/// buttons in bits 13..14), 3 = mouse sample (<c>data</c> = screen X, <c>data2</c> = screen Y in
/// bits 0..12 | buttons in bits 13..15).
/// </para>
/// <para>
/// <b>Why the era fields matter to the port.</b> The v8 <c>eraFlags</c> and the v9
/// <c>idleInstr</c> are not emulator trivia: together they say whether the recorded session's step
/// index includes steps in which the game ran <i>no frame at all</i> (a menu, a modal dialog, a
/// loader).  The port has the same distinction — <see cref="StepKind"/> — and a log that does not
/// carry the answer cannot be replayed against a step counter that does.  See
/// <see cref="InputLogHeader.OpensIdleSteps"/>.
/// </para>
/// <para>
/// <b>What the import deliberately discards:</b> the <c>tick</c> field (an emulated instruction
/// count — exactly the kind of stamp forbids in a replay), and the emulator-side scenario stamps
/// (semantics / lodBoost / policy / soundArms), which describe an emulator configuration the port
/// has no equivalent of.
/// </para>
/// </remarks>
public static class CyevReader
{
    /// <summary>The file magic: ASCII <c>CYEV</c>.</summary>
    public const string MagicText = "CYEV";

    /// <summary>The oldest version this reader accepts — the det era's first.  Earlier versions
    /// carry no step index at all.</summary>
    public const int MinFormatVersion = 7;

    /// <summary>The newest version this reader knows.</summary>
    public const int MaxFormatVersion = 9;

    /// <summary>The det era's original idle-watchdog threshold, in emulator instructions
    /// (<c>DetPolicy.IdleInstrLegacy</c>).  Every pre-v9 recording ran at this value, which is why a
    /// missing stamp is not "unknown" but a known number.</summary>
    public const uint LegacyIdleInstr = 4_000_000;

    /// <summary>Bit 0 of the v8 era-flags word: the recorder opened IDLE STEPS.</summary>
    public const int EraFlagIdleSteps = 1;

    /// <summary>Record size in bytes (identical in v7, v8 and v9).</summary>
    public const int RecordBytes = 18;

    /// <summary>The step stamp meaning "this event carries no step index".</summary>
    public const uint NoStep = uint.MaxValue;

    private const int KindKeyScancode = 0;
    private const int KindPitTick = 1;
    private const int KindJoystickSample = 2;
    private const int KindMouseSample = 3;

    /// <summary>The header size for a given format version.</summary>
    /// <param name="version">7, 8 or 9.</param>
    /// <exception cref="InvalidDataException">The version is outside the supported range.</exception>
    public static int HeaderBytesFor(int version) => version switch
    {
        7 => 24,
        8 => 26,
        9 => 30,
        _ => throw new InvalidDataException(
            $"CYEV version {version}: the port imports v{MinFormatVersion}..v{MaxFormatVersion}."),
    };

    /// <summary>Imports a recording from a stream.</summary>
    /// <param name="stream">The <c>.evq</c> stream.</param>
    /// <param name="notes">Provenance to record in the header.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    public static CyevImport Import(Stream stream, string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Import(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), notes);
    }

    /// <summary>Imports a recording from its bytes.</summary>
    /// <param name="data">The <c>.evq</c> bytes.</param>
    /// <param name="notes">Provenance to record in the header.</param>
    /// <exception cref="InvalidDataException">
    /// The magic, version or length is not a supported CYEV recording.
    /// </exception>
    public static CyevImport Import(ReadOnlySpan<byte> data, string? notes = null)
    {
        if (data.Length < 6)
        {
            throw new InvalidDataException($"CYEV blob is {data.Length} bytes, too short for a header.");
        }

        if (data[0] != (byte)'C' || data[1] != (byte)'Y' || data[2] != (byte)'E' || data[3] != (byte)'V')
        {
            throw new InvalidDataException("bad CYEV magic — this is not an emulator recording.");
        }

        int version = ReadU16(data[4..]);
        if (version is < MinFormatVersion or > MaxFormatVersion)
        {
            throw new InvalidDataException(
                $"CYEV version {version}: the port imports v{MinFormatVersion}..v{MaxFormatVersion}. "
                    + "Older versions carry no step index and a tick-indexed replay cannot be "
                    + "re-simulated (contract §3.3); a newer one carries era fields this build does "
                    + "not know, and guessing at an era is how a replay silently means something else.");
        }

        int headerBytes = HeaderBytesFor(version);
        if (data.Length < headerBytes)
        {
            throw new InvalidDataException(
                $"CYEV blob is {data.Length} bytes, too short for a v{version} header ({headerBytes}).");
        }

        int stepTicks = ReadU16(data[14..]);
        uint seedPlusOne = ReadU32(data[16..]);
        int eraFlags = version >= 8 ? ReadU16(data[20..]) : 0;
        uint idleInstr = version >= 9 ? ReadU32(data[22..]) : 0;
        if (idleInstr == 0)
        {
            idleInstr = LegacyIdleInstr;
        }

        long count = ReadU32(data[(headerBytes - 4)..]);   // count always sits in the last header word
        long expected = headerBytes + (count * RecordBytes);
        if (data.Length < expected)
        {
            throw new InvalidDataException(
                $"CYEV truncated: header says {count} records ({expected} bytes), blob is {data.Length}.");
        }

        ushort? seedWord = seedPlusOne == 0 ? null : (ushort)(seedPlusOne - 1);
        bool idleStepsEra = (eraFlags & EraFlagIdleSteps) != 0;

        List<InputEvent> events = new List<InputEvent>();
        Dictionary<uint, int> slots = new Dictionary<uint, int>();
        int pitTicks = 0;
        int unplaced = 0;
        int stepZero = 0;

        int offset = headerBytes;
        for (long i = 0; i < count; i++, offset += RecordBytes)
        {
            ReadOnlySpan<byte> record = data.Slice(offset, RecordBytes);
            int kind = ReadU16(record[8..]);
            ushort payload = ReadU16(record[10..]);
            ushort payload2 = ReadU16(record[12..]);
            uint step = ReadU32(record[14..]);

            if (kind == KindPitTick)
            {
                pitTicks++;
                continue;
            }

            if (step == NoStep)
            {
                unplaced++;
                continue;
            }

            if (step == 0)
            {
                stepZero++;
            }

            slots.TryGetValue(step, out int slot);
            slots[step] = slot + 1;

            events.Add(kind switch
            {
                KindKeyScancode => new KeyInputEvent(step, slot, (byte)payload),
                KindJoystickSample => new JoystickInputEvent(
                    step,
                    slot,
                    payload & 0x3FF,
                    payload2 & 0x3FF,
                    (JoystickButtons)((payload2 >> 13) & 0x3)),
                KindMouseSample => new MouseInputEvent(
                    step,
                    slot,
                    payload,
                    payload2 & 0x1FFF,
                    (MouseButtons)((payload2 >> 13) & 0x7)),
                _ => throw new InvalidDataException(
                    $"CYEV record {i} has kind {kind}; the reader knows 0..3."),
            });
        }

        InputLogHeader header = new InputLogHeader
        {
            KernelEra = InputLogHeader.DetV1Era,
            StepTicks = stepTicks == 0 ? TickClock.StepTicks : stepTicks,
            OpensIdleSteps = idleStepsEra,
            SourceIdleInstrThreshold = idleInstr,
            Random = new RandomStreamsConfig
            {
                MasterSeed = seedWord ?? 0,
                SimSeedWord = seedWord,
                SimMode = SimStreamMode.Split,
            },
            Notes = notes,
        };

        return new CyevImport(
            new InputLog(header, events),
            FormatVersion: version,
            EventsRead: (int)count,
            PitTicksSkipped: pitTicks,
            UnplacedEvents: unplaced,
            StepZeroEvents: stepZero,
            StepTicks: stepTicks,
            SeedWord: seedWord,
            EraFlags: eraFlags,
            IdleInstrThreshold: idleInstr,
            // Provable from the file only in the negative direction: an era with no idle steps has
            // none, full stop.  When the era DOES open them the file cannot say how many (a record
            // carries a step ordinal, not a step kind), and the honest answer is "unknown".
            IdleStepCount: idleStepsEra ? null : 0u);
    }

    private static ushort ReadU16(ReadOnlySpan<byte> b) => (ushort)(b[0] | (b[1] << 8));

    private static uint ReadU32(ReadOnlySpan<byte> b) =>
        (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
}
