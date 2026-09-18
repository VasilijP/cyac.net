using System.Buffers.Binary;

namespace CYAC.Formats.Audio;

/// <summary>One entry of a <c>.SP</c> speech module's export header.</summary>
/// <param name="Slot">Its index in the header, 0..5.</param>
/// <param name="Name">The export's name in this project's vocabulary.</param>
/// <param name="Offset">Its module-relative offset (a code offset, except the busy byte).</param>
/// <param name="IsCode">False for slot 5, which is a DATA offset, not an entry point.</param>
/// <param name="DispatchedByGame">Whether <c>scenario_audio_thunk_dispatch</c> ever calls it.</param>
public readonly record struct SpeechExport(
    int Slot, string Name, int Offset, bool IsCode, bool DispatchedByGame);

/// <summary>
/// The per-device digitized-speech backend a <c>.SP</c> asset carries: a 12-byte export header, an
/// <c>init</c> that latches a compiled-in device descriptor, and the device engine itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Export header</b> (
/// words, byte-identical in all five shipped modules —
/// <c>6A 00 9C 01 4E 00 45 00 28 00 26 00</c> = <c>init@0x006A</c>, <c>play@0x019C</c>,
/// <c>stop@0x004E</c>, <c>wait@0x0045</c>, <c>poll@0x0028</c>, and <c>0x0026</c> = the module-relative
/// offset of the BUSY BYTE. The engine reaches the module only through
/// <c>scenario_audio_thunk_dispatch @image@0x25465</c>, which loads <c>AX = ES:[mode*2]</c> and far-calls
/// it; only modes 0/1/2 are ever dispatched, so only <c>init</c>, <c>play</c> and <c>stop</c> are ever
/// called. Between play and stop the engine reads exactly one module byte: it takes the busy-byte
/// offset from <c>ES:[0x0A]</c> (<c>image@0x2541C</c>) and polls it (<c>image@0x25446</c>).
/// </para>
/// <para>
/// <b>Device descriptors.</b> <c>init</c> is byte-identical in all five modules and begins
/// <c>55 8B EC BB FF 01</c> = <c>mov bx,0x01FF</c>, then writes its two non-zero arguments to
/// <c>cs:[bx+6]</c> and <c>cs:[bx+8]</c> and calls <c>cs:[0x04]</c>. So <b>0x01FF is the descriptor
/// address in every module</b>, not only in BLASTER, and the descriptor is five words:
/// <c>{detect, stop, play, param0, param1}</c> — <c>init</c> latches <c>cs:[0x0C] = 0x01FF</c> and
/// <c>cs:[0x0E] = cs:[0x0203]</c>, which is <c>descriptor+4</c>, the play routine <c>play</c> then
/// far-calls. A second descriptor at <c>0x0209</c> is the one a <c>mode == 1</c> (hardware-ADPCM)
/// payload re-points the vtable at. (The BLASTER instance is the documented one; the generalisation to
/// all five, and the descriptor's field order, is measured here)
/// </para>
/// <para><b>Law L6 — the body is CODE.</b> This type models the ABI and re-emits the module exactly.</para>
/// </remarks>
public sealed class SpeechDriverModule
{
    /// <summary>Words in the export header: 6.</summary>
    public const int ExportCount = 6;

    /// <summary>Bytes of export header: 12.</summary>
    public const int HeaderBytes = ExportCount * 2;

    /// <summary>The module-relative address of the compiled-in device descriptor: 0x01FF.</summary>
    public const int DescriptorOffset = 0x01FF;

    /// <summary>The module-relative address of the ADPCM (mode 1) descriptor: 0x0209.</summary>
    public const int AdpcmDescriptorOffset = 0x0209;

    /// <summary>Words in a device descriptor: 5 — detect, stop, play, param0, param1.</summary>
    public const int DescriptorWords = 5;

    private static readonly (string Name, bool IsCode, bool Dispatched)[] Slots =
    [
        ("init", true, true),
        ("play", true, true),
        ("stop", true, true),
        ("wait", true, false),
        ("poll", true, false),
        ("busy_byte", false, false),
    ];

    private SpeechDriverModule(string name, int[] header, byte[] body)
    {
        Name = name;
        Body = body;
        Exports =
        [
            .. header.Select((offset, slot) =>
                new SpeechExport(slot, Slots[slot].Name, offset, Slots[slot].IsCode, Slots[slot].Dispatched)),
        ];
    }

    /// <summary>The asset name this module was parsed under, e.g. <c>"BLASTER.SP"</c>.</summary>
    public string Name { get; }

    /// <summary>The whole module, export header included — this asset has no separable container.</summary>
    public byte[] Body { get; }

    /// <summary>The six header words, in slot order.</summary>
    public IReadOnlyList<SpeechExport> Exports { get; }

    /// <summary>The module-relative offset of the busy byte the engine polls (header slot 5).</summary>
    public int BusyByteOffset => Exports[5].Offset;

    /// <summary>True when the export header is the one all five shipped modules share.</summary>
    public bool HasSharedExportHeader =>
        Exports[0].Offset == 0x006A && Exports[1].Offset == 0x019C && Exports[2].Offset == 0x004E
        && Exports[3].Offset == 0x0045 && Exports[4].Offset == 0x0028 && Exports[5].Offset == 0x0026;

    /// <summary>True for an asset name this type claims.</summary>
    /// <param name="assetName">An EALIB member name.</param>
    public static bool IsSpeechAsset(string assetName)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        return Path.GetExtension(assetName).Equals(".sp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses a <c>.SP</c> body (already decompressed).</summary>
    /// <param name="body">The asset bytes, starting at the export header.</param>
    /// <param name="assetName">The asset name, for messages.</param>
    /// <exception cref="InvalidDataException">The body cannot hold the export header.</exception>
    public static SpeechDriverModule Parse(ReadOnlySpan<byte> body, string assetName)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        if (body.Length < HeaderBytes)
        {
            throw new InvalidDataException(
                $"{assetName}: {body.Length} bytes cannot hold the {HeaderBytes}-byte .SP export header");
        }

        int[] header = new int[ExportCount];
        for (int i = 0; i < ExportCount; i++)
        {
            header[i] = BinaryPrimitives.ReadUInt16LittleEndian(body[(i * 2)..]);
        }

        return new SpeechDriverModule(assetName, header, body.ToArray());
    }

    /// <summary>Rebuilds a module from the recorded body, re-stamping the export header words.</summary>
    /// <param name="assetName">The asset name.</param>
    /// <param name="exportOffsets">The six header words.</param>
    /// <param name="body">The recorded body, export header included.</param>
    public static SpeechDriverModule FromParts(
        string assetName, IReadOnlyList<int> exportOffsets, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(exportOffsets);
        ArgumentNullException.ThrowIfNull(body);
        if (exportOffsets.Count != ExportCount)
        {
            throw new InvalidDataException(
                $"{assetName}: {exportOffsets.Count} export offsets, expected {ExportCount}");
        }

        if (body.Length < HeaderBytes)
        {
            throw new InvalidDataException($"{assetName}: recorded body is shorter than the export header");
        }

        byte[] bytes = (byte[])body.Clone();
        for (int i = 0; i < ExportCount; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)exportOffsets[i]);
        }

        return new SpeechDriverModule(assetName, [.. exportOffsets], bytes);
    }

    /// <summary>Re-emits the asset bytes exactly.</summary>
    public byte[] ToBytes() => (byte[])Body.Clone();

    /// <summary>
    /// Reads one of the two compiled-in device descriptors, or <see langword="null"/> when the module
    /// is too short to hold it.
    /// </summary>
    /// <param name="offset">
    /// <see cref="DescriptorOffset"/> for the PCM descriptor or <see cref="AdpcmDescriptorOffset"/>
    /// for the mode-1 twin.
    /// </param>
    public IReadOnlyList<int>? ReadDescriptor(int offset)
    {
        if (offset < 0 || offset + (DescriptorWords * 2) > Body.Length)
        {
            return null;
        }

        int[] words = new int[DescriptorWords];
        for (int i = 0; i < DescriptorWords; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt16LittleEndian(Body.AsSpan(offset + (i * 2)));
        }

        return words;
    }

    /// <summary>The names of the five descriptor words, in order.</summary>
    public static IReadOnlyList<string> DescriptorFieldNames { get; } =
        ["detect", "stop", "play", "param0", "param1"];
}
