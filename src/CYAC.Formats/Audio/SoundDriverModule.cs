using System.Buffers.Binary;
using System.Text;

namespace CYAC.Formats.Audio;

/// <summary>One of the 16 commands a <c>.DRV</c> music driver dispatches.</summary>
/// <param name="Code">The even command code 0x00..0x1E.</param>
/// <param name="Name">The command's name in this project's vocabulary.</param>
/// <param name="Meaning">What the routine does, decoded from the driver bodies.</param>
/// <param name="SentByGame">Whether CYAC ever issues it (five are dormant).</param>
public readonly record struct SoundDriverCommand(int Code, string Name, string Meaning, bool SentByGame);

/// <summary>
/// A <c>.DRV</c> music driver: a 16-byte container header, a far entry point that bounds-checks a
/// command code, and a 16-entry near-pointer dispatch table — the ABI behind the engine's blind
/// <c>LCALL [0xBB36]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Container (header comment): <c>+0x00</c> u16 decompressed
/// module size INCLUDING this header, <c>+0x02</c> u16 entry offset (0x0010 in all four),
/// <c>+0x04</c> u16 dispatcher <c>retf</c> offset (0x002F in all four), <c>+0x06..0x0F</c> reserved
/// (zero in all four), body from <c>+0x10</c>.
/// </para>
/// <para>
/// <b>The 0x10 skew.</b> The engine loads the whole file at <c>buf_seg:0</c> and builds the entry far
/// pointer as <c>{buf_seg + ((entry+0xF)&gt;&gt;4), 0}</c> (<c>image@0x2987A</c>/<c>0x29880</c>), so
/// with <c>entry = 0x10</c> the code segment starts at file offset 0x10 and every near pointer inside
/// the module lives in the "in-driver" space, <c>file − 0x10</c>. The dispatch table is at in-driver
/// <c>0x25..0x44</c> = file <c>0x35..0x54</c> (<c>call word ptr [bx+0x25]</c> with <c>bx</c> = the
/// command code, so one entry per EVEN code 0x00..0x1E).
/// </para>
/// <para>
/// <b>Law L6 — the body is CODE.</b> Nothing here disassembles or rewrites it: this type models the
/// container and the ABI, which is what the port consumes, and re-emits the module byte for byte.
/// </para>
/// </remarks>
public sealed class SoundDriverModule
{
    /// <summary>Bytes of container header before the body: 16.</summary>
    public const int HeaderBytes = 0x10;

    /// <summary>The entry offset every shipped driver declares: 0x0010.</summary>
    public const int ShippedEntryOffset = 0x0010;

    /// <summary>The dispatcher <c>retf</c> offset every shipped driver declares: 0x002F.</summary>
    public const int ShippedRetfOffset = 0x002F;

    /// <summary>Where the dispatch table starts, in-driver: 0x25 (file 0x35).</summary>
    public const int DispatchTableInDriverOffset = 0x25;

    /// <summary>Entries in the dispatch table: 16, one per even command code 0x00..0x1E.</summary>
    public const int DispatchTableEntries = 16;

    /// <summary>The highest command code the entry point accepts (it tests <c>cmd &lt; 0x20</c>).</summary>
    public const int CommandCodeLimit = 0x20;

    private SoundDriverModule(
        string name,
        int declaredSize,
        int entryOffset,
        int retfOffset,
        byte[] reserved,
        byte[] body,
        IReadOnlyList<int> dispatch,
        string? banner)
    {
        Name = name;
        DeclaredSize = declaredSize;
        EntryOffset = entryOffset;
        RetfOffset = retfOffset;
        Reserved = reserved;
        Body = body;
        DispatchTable = dispatch;
        Banner = banner;
    }

    /// <summary>The asset name this module was parsed under, e.g. <c>"adldrive.drv"</c>.</summary>
    public string Name { get; }

    /// <summary><c>+0x00</c> — the module size the header declares, header included.</summary>
    public int DeclaredSize { get; }

    /// <summary><c>+0x02</c> — the entry offset; the loader rounds it up to a paragraph.</summary>
    public int EntryOffset { get; }

    /// <summary><c>+0x04</c> — the dispatcher's <c>retf</c> offset.</summary>
    public int RetfOffset { get; }

    /// <summary><c>+0x06..0x0F</c> — reserved header bytes; zero in all four shipped drivers.</summary>
    public byte[] Reserved { get; }

    /// <summary>The code+data body from <c>+0x10</c>. Machine code: recorded, never converted (L6).</summary>
    public byte[] Body { get; }

    /// <summary>The 16 dispatch-table near pointers, in-driver, indexed by <c>code / 2</c>.</summary>
    public IReadOnlyList<int> DispatchTable { get; }

    /// <summary>The build banner the module carries, when it has one.</summary>
    public string? Banner { get; }

    /// <summary>True when the header matches the shape all four shipped drivers have.</summary>
    public bool HasShippedShape =>
        EntryOffset == ShippedEntryOffset
        && RetfOffset == ShippedRetfOffset
        && DeclaredSize == HeaderBytes + Body.Length
        && Array.TrueForAll(Reserved, b => b == 0);

    /// <summary>
    /// The command vocabulary, decoded from the driver bodies (semantics corrected in
    /// and cross-checked against the engine-side census of <c>audio_driver_raw_call_near</c>
    /// call sites: the game issues 0x00/0x02/0x04/0x06/0x08/0x0A/0x0C/0x12/0x16/0x18/0x1E and never
    /// the other five, which are the library's standalone mode.
    /// </summary>
    public static IReadOnlyList<SoundDriverCommand> Commands { get; } =
    [
        new(0x00, "start_tone", "start tone id [bp+8] (< 0x2C) with optional pitch/volume overrides", true),
        new(0x02, "stop_channel", "stop the channel whose owner-tone word equals the tone id", true),
        new(0x04, "update_params", "re-read a continuous tone's params (tones 0x00-0x04 and 0x11 only)", true),
        new(0x06, "sequencer_tick", "advance the .SNG sequencer and synthesise one tick on every channel", true),
        new(0x08, "silence", "clear every channel, drop the music flag, gate the output off", true),
        new(0x0A, "pause", "set the pause gate; the tick keeps running, the output is muted", true),
        new(0x0C, "resume", "clear the pause gate", true),
        new(0x0E, "install_timer_hook", "install the driver's own INT 8 hook and 236.7 Hz PIT tick", false),
        new(0x10, "remove_timer_hook", "restore IVT[8] and the PIT, speaker off", false),
        new(0x12, "init_device", "initialise the device (no-op in ibm; full OPL2 register init in adl)", true),
        new(0x14, "query_tick_counter", "return the driver's own tick counter", false),
        new(0x16, "music_play", "start .SNG playback from the far pointer in [bp+8]/[bp+0xA]", true),
        new(0x18, "music_unload", "detach the music — literally cmd 8's routine in all four drivers", true),
        new(0x1A, "midi_event", "feed one MIDI event directly (status, note, velocity)", false),
        new(0x1C, "query_channel_owner", "return which tone owns channel [bp+8], or -1", false),
        new(0x1E, "calibrate_io_delay", "AdLib only: scale the OPL write helper's busy-waits by the CPU speed", true),
    ];

    /// <summary>True for an asset name this type claims.</summary>
    /// <param name="assetName">An EALIB member name.</param>
    public static bool IsDriverAsset(string assetName)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        return Path.GetExtension(assetName).Equals(".drv", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses a <c>.DRV</c> body (already decompressed).</summary>
    /// <param name="body">The asset bytes, starting at the container header.</param>
    /// <param name="assetName">The asset name, for messages.</param>
    /// <exception cref="InvalidDataException">The body is too short to hold the header and the table.</exception>
    public static SoundDriverModule Parse(ReadOnlySpan<byte> body, string assetName)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        int tableEnd = HeaderBytes + DispatchTableInDriverOffset + (DispatchTableEntries * 2);
        if (body.Length < tableEnd)
        {
            throw new InvalidDataException(
                $"{assetName}: {body.Length} bytes cannot hold a {HeaderBytes}-byte header plus the " +
                $"{DispatchTableEntries}-entry dispatch table (needs {tableEnd})");
        }

        int size = BinaryPrimitives.ReadUInt16LittleEndian(body);
        int entry = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        int retf = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
        byte[] reserved = body[6..HeaderBytes].ToArray();
        byte[] moduleBody = body[HeaderBytes..].ToArray();

        int[] dispatch = new int[DispatchTableEntries];
        for (int i = 0; i < DispatchTableEntries; i++)
        {
            dispatch[i] = BinaryPrimitives.ReadUInt16LittleEndian(
                moduleBody.AsSpan(DispatchTableInDriverOffset + (i * 2)));
        }

        return new SoundDriverModule(
            assetName, size, entry, retf, reserved, moduleBody, dispatch, FindBanner(moduleBody));
    }

    /// <summary>Rebuilds a module from its header fields and its recorded body.</summary>
    /// <param name="assetName">The asset name.</param>
    /// <param name="declaredSize">The <c>+0x00</c> size word.</param>
    /// <param name="entryOffset">The <c>+0x02</c> entry offset.</param>
    /// <param name="retfOffset">The <c>+0x04</c> <c>retf</c> offset.</param>
    /// <param name="reserved">The <c>+0x06..0x0F</c> reserved bytes (10 of them).</param>
    /// <param name="body">The recorded body.</param>
    public static SoundDriverModule FromParts(
        string assetName, int declaredSize, int entryOffset, int retfOffset, byte[] reserved, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(reserved);
        ArgumentNullException.ThrowIfNull(body);
        if (reserved.Length != HeaderBytes - 6)
        {
            throw new InvalidDataException(
                $"reserved span is {reserved.Length} bytes, expected {HeaderBytes - 6}");
        }

        int[] dispatch = new int[DispatchTableEntries];
        for (int i = 0; i < DispatchTableEntries; i++)
        {
            int at = DispatchTableInDriverOffset + (i * 2);
            dispatch[i] = at + 1 < body.Length
                ? BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at))
                : 0;
        }

        return new SoundDriverModule(
            assetName, declaredSize, entryOffset, retfOffset, reserved, body, dispatch, FindBanner(body));
    }

    /// <summary>Re-emits the asset bytes exactly.</summary>
    public byte[] ToBytes()
    {
        byte[] bytes = new byte[HeaderBytes + Body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)DeclaredSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)EntryOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)RetfOffset);
        Reserved.CopyTo(bytes, 6);
        Body.CopyTo(bytes, HeaderBytes);
        return bytes;
    }

    // The four shipped drivers each carry one "**********Yeager - <DEVICE> - <date>**********"
    // banner in their data region.  cmsdrive's says TANDY: tnd and cms are one source
    // built twice, and the cms build shipped mislabeled.
    private static string? FindBanner(ReadOnlySpan<byte> body)
    {
        const string marker = "**********";
        for (int i = 0; i + marker.Length <= body.Length; i++)
        {
            if (!IsMarkerAt(body, i))
            {
                continue;
            }

            int end = i + marker.Length;
            while (end < body.Length && body[end] is >= 0x20 and < 0x7F)
            {
                if (IsMarkerAt(body, end))
                {
                    end += marker.Length;
                    return Encoding.ASCII.GetString(body[i..end]);
                }

                end++;
            }

            return null;
        }

        return null;

        static bool IsMarkerAt(ReadOnlySpan<byte> body, int at)
        {
            if (at + marker.Length > body.Length)
            {
                return false;
            }

            for (int k = 0; k < marker.Length; k++)
            {
                if (body[at + k] != (byte)'*')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
