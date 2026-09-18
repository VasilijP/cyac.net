namespace CYAC.Formats.Audio;

/// <summary>Which driver's sequencer a <c>.SNG</c> stream is written for.</summary>
/// <remarks>
/// The <c>.SNG</c> grammar is PER DRIVER: each of the three sequencers parses a different
/// MIDI-flavoured dialect (4). The engine picks the file from <c>g_audio_sng_name_table
/// [0x349A]</c> keyed by the driver type <c>[0xE482]</c>.
/// </remarks>
public enum SngDialect
{
    /// <summary>PC speaker — <c>yeagpc.sng</c>, parsed by <c>asset:1b/ibmdrive.drv@0x0A12</c>.</summary>
    Ibm,

    /// <summary>AdLib/OPL2 — <c>yeagadl.sng</c>, handler <c>asset:1b/adldrive.drv@0x12E4</c>.</summary>
    Adl,

    /// <summary>Tandy + CMS — <c>yeagcms.sng</c>, parsed by <c>asset:1b/tnddrive.drv@0x17D2</c>.</summary>
    Cms,
}

/// <summary>What one <c>.SNG</c> status byte tells the sequencer to do.</summary>
public enum SngEventKind
{
    /// <summary><c>0xFn lo</c> — wait <c>(n&lt;&lt;8)|lo</c> sequencer ticks.</summary>
    Delay,

    /// <summary><c>0xEn</c> — rewind the play position to the loop start; the song never ends.</summary>
    Loop,

    /// <summary>Program (instrument) change on a channel.</summary>
    Program,

    /// <summary>Note on.</summary>
    NoteOn,

    /// <summary>Note off.</summary>
    NoteOff,

    /// <summary>AdLib only, <c>0xBn v</c> — channel volume; the driver stores <c>(0x7F-v)&gt;&gt;1</c>.</summary>
    Volume,

    /// <summary>A status byte the dialect's dispatcher consumes and discards (no operand).</summary>
    Ignored,
}

/// <summary>
/// One decoded <c>.SNG</c> event, carrying everything needed to re-emit its bytes exactly.
/// </summary>
/// <param name="Offset">The status byte's offset in the stream (informational).</param>
/// <param name="Kind">What the sequencer does with it.</param>
/// <param name="StatusHigh">The status byte's high nibble, as written (several map to one kind).</param>
/// <param name="Channel">The status byte's low nibble — the channel, or the delay's high bits.</param>
/// <param name="Operand">The following byte, when this status takes one.</param>
public readonly record struct SngEvent(
    int Offset, SngEventKind Kind, byte StatusHigh, int Channel, int? Operand)
{
    /// <summary>The status byte this event was read from.</summary>
    public byte Status => (byte)(StatusHigh | Channel);

    /// <summary>Delay events only: the tick count <c>(channel&lt;&lt;8)|operand</c>.</summary>
    public int Ticks => Kind == SngEventKind.Delay ? (Channel << 8) | (Operand ?? 0) : 0;
}

/// <summary>
/// The <c>.SNG</c> music stream: a byte-level event list in one of the three driver dialects.
/// </summary>
/// <remarks>
/// <para>
/// The three shipped files are three arrangements of one ~35.8k-tick piece.
/// </para>
/// <para>
/// <b>Every byte of the stream becomes exactly one event</b>, so <see cref="Emit"/> reproduces the
/// input byte for byte. That is the property the transform's round trip rests on — no "unknown"
/// residue exists for a stream that parses.
/// </para>
/// </remarks>
public static class SngSequence
{
    /// <summary>The MIDI note number each dialect's note table starts at.</summary>
    /// <param name="dialect">The dialect.</param>
    public static int NoteBase(SngDialect dialect) => dialect switch
    {
        SngDialect.Ibm => 0x18,
        SngDialect.Adl => 0x1F,
        SngDialect.Cms => 0x21,
        _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
    };

    /// <summary>The dialect a shipped asset name selects, or <see langword="null"/> for an unknown name.</summary>
    /// <param name="assetName">An asset name such as <c>"yeagadl.sng"</c>.</param>
    public static SngDialect? DialectForAsset(string assetName)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        string stem = Path.GetFileNameWithoutExtension(assetName).ToLowerInvariant();
        return stem switch
        {
            "yeagpc" => SngDialect.Ibm,
            "yeagadl" => SngDialect.Adl,
            "yeagcms" => SngDialect.Cms,
            _ => null,
        };
    }

    /// <summary>The <c>.DRV</c> module whose sequencer reads this dialect.</summary>
    /// <param name="dialect">The dialect.</param>
    public static string DriverModule(SngDialect dialect) => dialect switch
    {
        SngDialect.Ibm => "ibmdrive.drv",
        SngDialect.Adl => "adldrive.drv",
        SngDialect.Cms => "tnddrive.drv",
        _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
    };

    /// <summary>
    /// The high nibble an event kind is normally written with in a dialect, or <c>0xFF</c> when the
    /// kind has no single canonical spelling (so the value must be recorded).
    /// </summary>
    /// <param name="dialect">The dialect.</param>
    /// <param name="kind">The event kind.</param>
    public static byte CanonicalStatusHigh(SngDialect dialect, SngEventKind kind) => kind switch
    {
        SngEventKind.Delay => 0xF0,
        SngEventKind.Loop => 0xE0,
        SngEventKind.Program => 0xC0,
        SngEventKind.NoteOn => 0x90,
        SngEventKind.Volume when dialect == SngDialect.Adl => 0xB0,
        // 0x80 in every dialect: adl dispatches it explicitly, and the ibm/cms dispatcher takes
        // ANY nibble below 0x90 as a note-off but the shipped streams only ever write 0x8n.
        SngEventKind.NoteOff => 0x80,
        _ => 0xFF,
    };

    /// <summary>
    /// True when the AdLib dialect's note events on this channel carry NO note operand: channels
    /// 0xB..0xF are the rhythm slots, whose handler derives the drum from the channel's current
    /// program (<c>asset:1b/adldrive.drv@0x144F</c> on, <c>@0x14D3</c> off).
    /// </summary>
    /// <param name="channel">The status byte's low nibble.</param>
    public static bool IsAdlRhythmChannel(int channel) => channel >= 0xB;

    /// <summary>Decodes a stream into its events.</summary>
    /// <param name="stream">The decompressed <c>.SNG</c> body.</param>
    /// <param name="dialect">Which sequencer reads it.</param>
    /// <exception cref="InvalidDataException">The last status byte's operand is missing.</exception>
    public static IReadOnlyList<SngEvent> Parse(ReadOnlySpan<byte> stream, SngDialect dialect)
    {
        List<SngEvent> events = new List<SngEvent>();
        int i = 0;
        while (i < stream.Length)
        {
            int offset = i;
            byte status = stream[i++];
            byte high = (byte)(status & 0xF0);
            int channel = status & 0x0F;

            (SngEventKind kind, bool operand) = Classify(dialect, high, channel);
            int? value = null;
            if (operand)
            {
                if (i >= stream.Length)
                {
                    throw new InvalidDataException(
                        $".SNG ends after status 0x{status:X2} at 0x{offset:X4}, which needs an operand");
                }

                value = stream[i++];
            }

            events.Add(new SngEvent(offset, kind, high, channel, value));
        }

        return events;
    }

    /// <summary>Re-emits an event list as stream bytes.</summary>
    /// <param name="events">The events, in order.</param>
    /// <param name="dialect">The dialect they were decoded in.</param>
    /// <exception cref="InvalidDataException">An event's operand does not match what its status needs.</exception>
    public static byte[] Emit(IReadOnlyList<SngEvent> events, SngDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(events);
        List<byte> bytes = new List<byte>(events.Count * 2);
        foreach (SngEvent e in events)
        {
            if (e.Channel is < 0 or > 0xF)
            {
                throw new InvalidDataException($"channel {e.Channel} is not a nibble");
            }

            byte status = e.Status;
            (SngEventKind kind, bool operand) = Classify(dialect, e.StatusHigh, e.Channel);
            if (kind != e.Kind)
            {
                throw new InvalidDataException(
                    $"status 0x{status:X2} decodes as {kind} in the {dialect} dialect, not {e.Kind}");
            }

            bytes.Add(status);
            if (operand)
            {
                int value = e.Operand
                    ?? throw new InvalidDataException($"status 0x{status:X2} needs an operand");
                if (value is < 0 or > 0xFF)
                {
                    throw new InvalidDataException($"operand {value} of status 0x{status:X2} is not a byte");
                }

                bytes.Add((byte)value);
            }
            else if (e.Operand is not null)
            {
                throw new InvalidDataException($"status 0x{status:X2} takes no operand");
            }
        }

        return [.. bytes];
    }

    private static (SngEventKind Kind, bool Operand) Classify(SngDialect dialect, byte high, int channel)
    {
        if (high == 0xF0)
        {
            return (SngEventKind.Delay, true);
        }

        if (high == 0xE0)
        {
            return (SngEventKind.Loop, false);
        }

        if (dialect == SngDialect.Adl)
        {
            return high switch
            {
                0xD0 => (SngEventKind.Ignored, false),
                0xC0 => (SngEventKind.Program, true),
                0xB0 => (SngEventKind.Volume, true),
                0xA0 => (SngEventKind.Ignored, false),
                0x90 => (SngEventKind.NoteOn, !IsAdlRhythmChannel(channel)),
                0x80 => (SngEventKind.NoteOff, !IsAdlRhythmChannel(channel)),
                _ => (SngEventKind.Ignored, false),
            };
        }

        // ibm and cms share one dispatcher shape: 0xC0/0xD0 program, 0x90/0xA0/0xB0 note-on, and
        // anything below 0x90 is a note-off for the low nibble's channel with no operand.
        return high switch
        {
            0xC0 or 0xD0 => (SngEventKind.Program, true),
            0x90 or 0xA0 or 0xB0 => (SngEventKind.NoteOn, true),
            _ => (SngEventKind.NoteOff, false),
        };
    }
}
