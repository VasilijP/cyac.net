using System.Globalization;
using System.Text.Json;
using CYAC.Formats.Exe;
using CYAC.Port.Transform.Input;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// <c>yeager.exe</c> → the unpacked program image plus the knowledge of how it was packed:
/// <c>exe/image.l1.bin</c>, <c>exe/image.l1.exe</c> and <c>exe/unpack.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The shipping executable is triple-packed — SLR LZH (layer 3) over an OPTLINK <c>/EXEPACK</c>
/// layer 2 over the Microsoft C 6.0 program (layer 1); Every <c>image@</c> citation in this
/// project addresses the layer-1 image at load segment <c>0x1000</c>, which is what this family
/// writes.
/// </para>
/// <para>
/// <b>This family has no inverse.</b> Re-packing an image with a compressor nobody possesses is
/// not a goal of the data transform (the round-trip rule is about the *data* families), so
/// <see cref="Inverse"/> refuses.  What replaces it is stronger for this family: the unpack is
/// deterministic, so verification re-derives the image from the original and diffs it, and — for a
/// recognised distribution — also checks it against the digests recorded in
/// <see cref="KnownDistributions"/>.
/// </para>
/// <para>
/// The decoders live in <c>CYAC.Formats/Exe</c> and are static: no emulator, no instruction
/// dispatch (<see cref="YeagerExeUnpacker"/> remarks).
/// </para>
/// </remarks>
public sealed class ExeTransform : IFamilyTransform
{
    /// <summary>The data tree folder this family writes into.</summary>
    public const string Folder = "exe";

    /// <summary>The unpacked layer-1 image's path in the data tree.</summary>
    public const string ImagePath = "exe/image.l1.bin";

    /// <summary>The reconstructed loadable executable's path in the data tree.</summary>
    public const string ExecutablePath = "exe/image.l1.exe";

    /// <summary>The unpack knowledge document's path in the data tree.</summary>
    public const string ReportPath = "exe/unpack.json";

    /// <inheritdoc/>
    public string Family => "exe";

    /// <inheritdoc/>
    public string TreeDescription =>
        "`exe/image.l1.bin` — the unpacked layer-1 program image at load segment 0x1000, the base " +
        "every `image@` citation uses; `exe/image.l1.exe` is the same image wrapped in a loadable MZ " +
        "with a real relocation table; `exe/unpack.json` records how the two packer layers were " +
        "peeled (stub offsets, trailers, entry state, relocation count).";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => new(
        OutputFidelity.Exact,
        "the unpack has no inverse — re-packing is not a goal — so verification re-derives the image " +
        "from the original executable and diffs it, and checks it against the recognised " +
        "distribution's recorded image digests");

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is null
            && string.Equals(source.Name, KnownDistributions.ExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        UnpackedImage unpacked = YeagerExeUnpacker.Unpack(source.Content.Span, source.Name);
        byte[] image = unpacked.ImageAtLoadSeg(unpacked.LoadSegment);
        byte[] executable = unpacked.ToReconstructedExe();

        // A recognised distribution comes with the digests of what the unpack MUST produce; anything
        // else is honest work with no oracle behind it, and says so.
        KnownDistribution? expected = context.Distribution;
        OutputFidelity fidelity = OutputFidelity.Unverified;
        string? note =
            "no known distribution matched, so there is no recorded image digest to check this " +
            "unpack against; the layers' own stub fingerprints did match";

        if (expected is not null)
        {
            string imageDigest = InputSet.Digest(image);
            string exeDigest = InputSet.Digest(executable);
            if (!string.Equals(imageDigest, expected.L1ImageSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"{source}: the unpacked image hashes to {imageDigest} but distribution " +
                    $"\"{expected.Id}\" records {expected.L1ImageSha256}");
            }

            if (!string.Equals(exeDigest, expected.L1ExeSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"{source}: the reconstructed executable hashes to {exeDigest} but distribution " +
                    $"\"{expected.Id}\" records {expected.L1ExeSha256}");
            }

            fidelity = OutputFidelity.Exact;
            note = null;
        }

        string imagePath = context.Allocate(ImagePath);
        string exePath = context.Allocate(ExecutablePath);
        string reportPath = context.Allocate(ReportPath);
        byte[] report = Serialise(unpacked, image, executable, imagePath, exePath);

        return
        [
            new TransformOutput(imagePath, image, OutputRole.Data, fidelity, note),
            new TransformOutput(exePath, executable, OutputRole.Data, fidelity, note),
            new TransformOutput(reportPath, report, OutputRole.Data, fidelity, note),
        ];
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always: this family's transform is not invertible.</exception>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context) =>
        throw new NotSupportedException(
            "the exe family has no inverse: re-packing the image with the SLR LZH and OPTLINK " +
            "/EXEPACK compressors is not a goal of the transform (" +
            "§2, covers the data families). Verification re-derives the image from " +
            "yeager.exe and diffs it instead.");

    /// <summary>Renders the unpack knowledge document.</summary>
    /// <param name="unpacked">The unpack result.</param>
    /// <param name="image">The image bytes as written to the tree.</param>
    /// <param name="executable">The reconstructed executable as written to the tree.</param>
    /// <param name="imagePath">Where the image landed, for the document's cross-reference.</param>
    /// <param name="executablePath">Where the executable landed.</param>
    public static byte[] Serialise(
        UnpackedImage unpacked, byte[] image, byte[] executable, string imagePath, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(unpacked);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(executable);
        ExeUnpackReport r = unpacked.Report;
        MzHeader header = r.PackedHeader;

        ExeUnpackDto dto = new ExeUnpackDto
        {
            Format = "cyac.exeUnpack/1",
            About =
                "How the shipping executable was taken apart. The packing is SLR LZH (layer 3) over " +
                "an OPTLINK /EXEPACK-shape layer 2 over the Microsoft C 6.0 program (layer 1). " +
                "Offsets named \"image\" are byte offsets into the layer-1 image; segment values are " +
                "relative to the load segment unless the field says otherwise.",
            Source = r.Input,
            SourceBytes = r.InputLength,
            LoadSegment = r.LoadSegment,
            PackedHeader = new ExeMzHeaderDto
            {
                HeaderBytes = header.HeaderBytes,
                EntryCs = Hex(header.Cs),
                EntryIp = Hex(header.Ip),
                StackSs = Hex(header.Ss),
                StackSp = Hex(header.Sp),
                MinAllocParagraphs = header.MinAlloc,
                MaxAllocParagraphs = header.MaxAlloc,
                RelocationCount = header.RelocationCount,
            },
            Layers =
            [
                .. r.Layers.Select(l => new ExeLayerDto
                {
                    Layer = l.Layer,
                    Producer = l.Producer,
                    Format = l.Format,
                    StubImageOffset = Hex(l.StubImageOffset),
                    StubLength = l.StubLength,
                    OutputBytes = l.OutputBytes,
                }),
            ],
            SlrLzhStub = new ExeSlrStubDto
            {
                FileOffset = Hex(r.Layer3.FileOffset),
                ImageOffset = Hex(r.Layer3.ImageOffset),
                Length = r.Layer3.Length,
                Copyright = r.Layer3.Copyright,
                PayloadParagraphs = Hex(r.Layer3.PayloadParagraphs),
                LiteralTreeAlphabet = r.Layer3.LiteralTreeAlphabet,
                DistanceTreeAlphabet = r.Layer3.DistanceTreeAlphabet,
                BuiltInCodeLengthCounts = [.. r.Layer3.CodeLengthCounts.Select(b => (int)b)],
                NextLayerCs = Hex(r.Layer3.UnpackedCs),
                NextLayerIp = Hex(r.Layer3.UnpackedIp),
                NextLayerSs = Hex(r.Layer3.UnpackedSs),
                NextLayerSp = Hex(r.Layer3.UnpackedSp),
            },
            ExepackStub = new ExeExepackStubDto
            {
                ImageOffset = Hex(r.Layer2.ImageOffset),
                Length = r.Layer2.Length,
                PayloadParagraphs = Hex(r.Layer2.PayloadParagraphs),
                FirstOpcode = Hex(r.Layer2.FirstOpcode),
                ProgramCs = Hex(r.Layer2.UnpackedCs),
                ProgramIp = Hex(r.Layer2.UnpackedIp),
                ProgramSs = Hex(r.Layer2.UnpackedSs),
                ProgramSp = Hex(r.Layer2.UnpackedSp),
            },
            Image = new ExeImageDto
            {
                Path = imagePath,
                Bytes = image.Length,
                Sha256 = InputSet.Digest(image),
                EntryCs = Hex(unpacked.EntryCs),
                EntryIp = Hex(unpacked.EntryIp),
                StackSs = Hex(unpacked.StackSs),
                StackSp = Hex(unpacked.StackSp),
                RelocationCount = unpacked.Relocations.Count,
            },
            ReconstructedExe = new ExeReconstructedDto
            {
                Path = executablePath,
                Bytes = executable.Length,
                Sha256 = InputSet.Digest(executable),
                HeaderBytes = executable.Length - image.Length,
                RelocationCount = unpacked.Relocations.Count,
            },
        };

        return JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.ExeUnpackDto);
    }

    private static string Hex(int value) =>
        "0x" + value.ToString("X4", CultureInfo.InvariantCulture);
}
