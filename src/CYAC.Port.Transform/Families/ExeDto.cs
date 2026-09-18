using System.Text.Json.Serialization;

namespace CYAC.Port.Transform.Families;

// Wire format of <data>/exe/unpack.json — the KNOWLEDGE half of the exe family (transform-plan L1):
// what was found inside the packed executable, precise enough that a reader can check every claim
// against the bytes without running anything.  The image itself lands next to it as image.l1.bin.

internal sealed class ExeUnpackDto
{
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("about")]
    public string? About { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("sourceBytes")]
    public int SourceBytes { get; init; }

    [JsonPropertyName("loadSegment")]
    public int LoadSegment { get; init; }

    [JsonPropertyName("packedHeader")]
    public ExeMzHeaderDto? PackedHeader { get; init; }

    [JsonPropertyName("layers")]
    public List<ExeLayerDto>? Layers { get; init; }

    [JsonPropertyName("slrLzhStub")]
    public ExeSlrStubDto? SlrLzhStub { get; init; }

    [JsonPropertyName("exepackStub")]
    public ExeExepackStubDto? ExepackStub { get; init; }

    [JsonPropertyName("image")]
    public ExeImageDto? Image { get; init; }

    [JsonPropertyName("reconstructedExe")]
    public ExeReconstructedDto? ReconstructedExe { get; init; }
}

internal sealed class ExeMzHeaderDto
{
    [JsonPropertyName("headerBytes")]
    public int HeaderBytes { get; init; }

    [JsonPropertyName("entryCs")]
    public string? EntryCs { get; init; }

    [JsonPropertyName("entryIp")]
    public string? EntryIp { get; init; }

    [JsonPropertyName("stackSs")]
    public string? StackSs { get; init; }

    [JsonPropertyName("stackSp")]
    public string? StackSp { get; init; }

    [JsonPropertyName("minAllocParagraphs")]
    public int MinAllocParagraphs { get; init; }

    [JsonPropertyName("maxAllocParagraphs")]
    public int MaxAllocParagraphs { get; init; }

    [JsonPropertyName("relocationCount")]
    public int RelocationCount { get; init; }
}

internal sealed class ExeLayerDto
{
    [JsonPropertyName("layer")]
    public int Layer { get; init; }

    [JsonPropertyName("producer")]
    public string? Producer { get; init; }

    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("stubImageOffset")]
    public string? StubImageOffset { get; init; }

    [JsonPropertyName("stubLength")]
    public int StubLength { get; init; }

    [JsonPropertyName("outputBytes")]
    public int OutputBytes { get; init; }
}

internal sealed class ExeSlrStubDto
{
    [JsonPropertyName("fileOffset")]
    public string? FileOffset { get; init; }

    [JsonPropertyName("imageOffset")]
    public string? ImageOffset { get; init; }

    [JsonPropertyName("length")]
    public int Length { get; init; }

    [JsonPropertyName("copyright")]
    public string? Copyright { get; init; }

    [JsonPropertyName("payloadParagraphs")]
    public string? PayloadParagraphs { get; init; }

    [JsonPropertyName("literalTreeAlphabet")]
    public int LiteralTreeAlphabet { get; init; }

    [JsonPropertyName("distanceTreeAlphabet")]
    public int DistanceTreeAlphabet { get; init; }

    [JsonPropertyName("builtInCodeLengthCounts")]
    public List<int>? BuiltInCodeLengthCounts { get; init; }

    [JsonPropertyName("nextLayerCs")]
    public string? NextLayerCs { get; init; }

    [JsonPropertyName("nextLayerIp")]
    public string? NextLayerIp { get; init; }

    [JsonPropertyName("nextLayerSs")]
    public string? NextLayerSs { get; init; }

    [JsonPropertyName("nextLayerSp")]
    public string? NextLayerSp { get; init; }
}

internal sealed class ExeExepackStubDto
{
    [JsonPropertyName("imageOffset")]
    public string? ImageOffset { get; init; }

    [JsonPropertyName("length")]
    public int Length { get; init; }

    [JsonPropertyName("payloadParagraphs")]
    public string? PayloadParagraphs { get; init; }

    [JsonPropertyName("firstOpcode")]
    public string? FirstOpcode { get; init; }

    [JsonPropertyName("programCs")]
    public string? ProgramCs { get; init; }

    [JsonPropertyName("programIp")]
    public string? ProgramIp { get; init; }

    [JsonPropertyName("programSs")]
    public string? ProgramSs { get; init; }

    [JsonPropertyName("programSp")]
    public string? ProgramSp { get; init; }
}

internal sealed class ExeImageDto
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("bytes")]
    public int Bytes { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("entryCs")]
    public string? EntryCs { get; init; }

    [JsonPropertyName("entryIp")]
    public string? EntryIp { get; init; }

    [JsonPropertyName("stackSs")]
    public string? StackSs { get; init; }

    [JsonPropertyName("stackSp")]
    public string? StackSp { get; init; }

    [JsonPropertyName("relocationCount")]
    public int RelocationCount { get; init; }
}

internal sealed class ExeReconstructedDto
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("bytes")]
    public int Bytes { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("headerBytes")]
    public int HeaderBytes { get; init; }

    [JsonPropertyName("relocationCount")]
    public int RelocationCount { get; init; }
}
