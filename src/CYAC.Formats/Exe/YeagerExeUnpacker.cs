namespace CYAC.Formats.Exe;

/// <summary>
/// Statically unpacks the triple-packed <c>yeager.exe</c>: SLR LZH (layer 3) then the OPTLINK
/// <c>/EXEPACK</c>-shape layer 2, leaving the Microsoft C 6.0 program image (layer 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why "statically".</b> The project's reference unpacker (<c>CYAC.Tools.SlrUnpacker</c>) runs
/// the two packer stubs on an 8086 micro-emulator.  That instrument stays — it is how these
/// decoders were checked — but the port's transform tool may not reference it and, more
/// importantly, "execute the opaque bytes and see what falls out" is precisely what the transform
/// principle rules out.  Everything here is the two formats, transcribed from the stubs'
/// disassembly: no instruction decoder, no dispatch loop.
/// </para>
/// <para>
/// <b>Why it models memory and even the stack.</b> The reference artefact is a *memory snapshot*, not
/// a decompressed stream: 320 KB starting at <c>loadSeg:0000</c>.  Both stubs relocate themselves
/// into that window, both leave working tables there, and the LZH stub's recursive table builder
/// leaves 118 bytes of stack frames at the top of it.  A decoder that only emitted the decompressed
/// bytes would differ from the reference image in 98 bytes — all of them stub residue.
/// </para>
/// <para>
/// <b>Refusal.</b>  Every layer checks its stub's fingerprints before running
/// (<see cref="SlrLzhDecoder.ReadStub"/>, <see cref="ExepackUnpacker"/>): an unknown distribution
/// fails loudly with the offset and the bytes found, never silently produces a plausible-looking
/// image.
/// </para>
/// </remarks>
public static class YeagerExeUnpacker
{
    /// <summary>
    /// The load segment the project resolves the image at — the base every <c>image@</c> citation
    /// and both reference artefacts assume.
    /// </summary>
    public const ushort CanonicalLoadSegment = 0x1000;

    /// <summary>
    /// Peels both packer layers off a packed DOS executable.
    /// </summary>
    /// <param name="packedExe">The whole packed file.</param>
    /// <param name="name">An optional name for the report and for error messages.</param>
    /// <exception cref="InvalidDataException">
    /// The file is not an <c>MZ</c> executable, or is not packed in the layout this unpacker knows.
    /// </exception>
    public static UnpackedImage Unpack(ReadOnlySpan<byte> packedExe, string? name = null)
    {
        if (!MzHeader.TryRead(packedExe, out MzHeader header))
        {
            throw new InvalidDataException(
                $"{name ?? "input"} is not a DOS MZ executable (no \"MZ\" signature)");
        }

        int bodyLength = packedExe.Length - header.HeaderBytes;
        if (bodyLength <= 0)
        {
            throw new InvalidDataException(
                $"{name ?? "input"}: the MZ header claims {header.HeaderBytes} bytes but the file " +
                $"is {packedExe.Length}");
        }

        SlrLzhStubInfo stub3 = SlrLzhDecoder.ReadStub(packedExe, header);

        // The modelled window has to cover the loaded body, everything the stubs relocate into the
        // image, and the stack they run on (which is above both).  The generous tail is the LZH
        // stub's tree tables, which sit just under its stack.
        int stackTop = (header.Ss * 16) + header.Sp;
        int size = Math.Max(bodyLength, stackTop) + 0x1000;
        StubMemory memory = new StubMemory(size, CanonicalLoadSegment);
        packedExe[header.HeaderBytes..].CopyTo(memory.Bytes);

        StubStack stack3 = new StubStack(memory, header.Ss * 16, header.Sp);
        int layer3Output = SlrLzhDecoder.Expand(memory, header, stack3);

        // The LZH stub patched its inline header to absolute segments as it ran; read the successor
        // state back out of the copy it left in the working segment.
        int work = header.Ss * 16;
        ushort nextSp = memory.Read16(work + 0x07);
        ushort nextSs = memory.Read16(work + 0x09);
        ushort nextIp = memory.Read16(work + 0x0B);
        ushort nextCs = memory.Read16(work + 0x0D);

        ExepackStubInfo stub2 = ExepackUnpacker.ReadStub(memory, nextCs, nextIp);
        List<int> relocationSites = new List<int>();
        (ExepackStubInfo trailer, int layer2Output) =
            ExepackUnpacker.Expand(memory, nextCs, nextIp, nextSs, nextSp, relocationSites);

        ushort stackSs = (ushort)(trailer.UnpackedSs - CanonicalLoadSegment);
        ushort entryCs = (ushort)(trailer.UnpackedCs - CanonicalLoadSegment);

        // DOS gives the program everything up to the top of its stack; the reference dump rounds
        // that up to a whole 64 KB so no trailing data segment is clipped.
        int imageBytes = (int)((((uint)(stackSs * 16) + trailer.UnpackedSp) + 0xFFFF) & ~0xFFFFu);
        byte[] image = new byte[imageBytes];
        memory.Bytes.AsSpan(0, Math.Min(imageBytes, memory.Bytes.Length)).CopyTo(image);

        int[] relocations =
        [
            .. relocationSites.Where(o => o >= 0 && o + 1 < imageBytes).Order(),
        ];

        ExeUnpackReport report = new ExeUnpackReport(
            name,
            packedExe.Length,
            header,
            [
                new ExeLayerReport(
                    3, "SLR Systems linker LZH packer", "LZSS + two canonical-Huffman trees",
                    stub3.ImageOffset, stub3.Length, layer3Output),
                new ExeLayerReport(
                    2, "SLR OPTLINK /EXEPACK", "RLE data expansion + chained relocations",
                    stub2.ImageOffset, stub2.Length, layer2Output),
            ],
            stub3,
            stub2,
            layer3Output,
            layer2Output,
            imageBytes,
            relocations.Length,
            CanonicalLoadSegment);

        return new UnpackedImage(
            image,
            relocations,
            CanonicalLoadSegment,
            entryCs,
            trailer.UnpackedIp,
            stackSs,
            trailer.UnpackedSp,
            header.MinAlloc,
            header.MaxAlloc,
            report);
    }
}
