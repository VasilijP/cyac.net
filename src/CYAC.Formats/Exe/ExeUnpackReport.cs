namespace CYAC.Formats.Exe;

/// <summary>
/// One packing layer the unpacker peeled, described well enough for a reader to check the claim
/// against the bytes.
/// </summary>
/// <param name="Layer">The project's layer number: 3 is the outermost, 1 the program itself.</param>
/// <param name="Producer">The tool that applied it.</param>
/// <param name="Format">The compression / packing scheme.</param>
/// <param name="StubImageOffset">Where the layer's stub sits in the image it operates on.</param>
/// <param name="StubLength">The stub's length in bytes.</param>
/// <param name="OutputBytes">How many bytes the layer emitted.</param>
public sealed record ExeLayerReport(
    int Layer,
    string Producer,
    string Format,
    int StubImageOffset,
    int StubLength,
    int OutputBytes);

/// <summary>
/// Everything the static unpack learned about a packed executable — the *knowledge* the data
/// transform ships in place of the bytes.
/// </summary>
/// <param name="Input">The input file's name, when one was given.</param>
/// <param name="InputLength">The packed file's length in bytes.</param>
/// <param name="PackedHeader">The packed file's own <c>MZ</c> header.</param>
/// <param name="Layers">The layers that were peeled, outermost first.</param>
/// <param name="Layer3">The SLR LZH stub's self-description.</param>
/// <param name="Layer2">The EXEPACK-shape stub's self-description.</param>
/// <param name="Layer3OutputBytes">Bytes the LZH expansion emitted.</param>
/// <param name="Layer2OutputBytes">Bytes the EXEPACK expansion emitted.</param>
/// <param name="ImageBytes">The length of the modelled program image.</param>
/// <param name="RelocationCount">How many relocation sites the packed chains named.</param>
/// <param name="LoadSegment">The segment the image's relocated words are resolved for.</param>
public sealed record ExeUnpackReport(
    string? Input,
    int InputLength,
    MzHeader PackedHeader,
    IReadOnlyList<ExeLayerReport> Layers,
    SlrLzhStubInfo Layer3,
    ExepackStubInfo Layer2,
    int Layer3OutputBytes,
    int Layer2OutputBytes,
    int ImageBytes,
    int RelocationCount,
    ushort LoadSegment);
