namespace CYAC.Port.Transform.Transform;

/// <summary>
/// One file a family transform produces, before it is written into the data tree.
/// </summary>
/// <param name="Path">Its path relative to the data directory, using forward slashes.</param>
/// <param name="Bytes">Its contents.</param>
/// <param name="Role">Whether it is the round-trip source of truth, a viewer aid, or raw carry-over.</param>
/// <param name="Fidelity">
/// The fidelity claimed before verification.  <see cref="OutputRole.Data"/> outputs claim what the
/// family's <see cref="FidelityRule"/> promises; <c>--verify</c> then confirms it against the
/// original bytes and fails loudly if the promise does not hold.
/// </param>
/// <param name="FidelityNote">Why the fidelity is not <see cref="OutputFidelity.Exact"/>, when it is not.</param>
/// <param name="UnknownBytes">Bytes emitted as <c>unknown_0xNN</c> fields — named but unexplained.</param>
/// <param name="UnexplainedBytes">Bytes carried with no explanation at all (raw outputs).</param>
/// <param name="CodeBytes">
/// Bytes of MACHINE CODE the output records rather than converts: a <c>.DRV</c>/<c>.SP</c> driver body, and
/// anything else whose meaning is "instructions".  They are explained — the output says what the code IS and what
/// its ABI is — so they belong in neither the unknown nor the unexplained column; they are counted separately so
/// the L4 burn-down stays honest in both directions.
/// </param>
public sealed record TransformOutput(
    string Path,
    byte[] Bytes,
    OutputRole Role,
    OutputFidelity Fidelity,
    string? FidelityNote = null,
    int UnknownBytes = 0,
    int UnexplainedBytes = 0,
    int CodeBytes = 0)
{
    /// <summary>An editable JSON/round-trip output.</summary>
    /// <param name="path">Path relative to the data directory.</param>
    /// <param name="bytes">Contents.</param>
    /// <param name="unknownBytes">Named-but-unknown byte count.</param>
    public static TransformOutput Data(string path, byte[] bytes, int unknownBytes = 0) =>
        new(path, bytes, OutputRole.Data, OutputFidelity.Exact, null, unknownBytes);

    /// <summary>A derived viewer aid, regenerated from a data output and never read back.</summary>
    /// <param name="path">Path relative to the data directory.</param>
    /// <param name="bytes">Contents.</param>
    /// <param name="derivedFrom">The data output this view is rendered from.</param>
    public static TransformOutput View(string path, byte[] bytes, string derivedFrom) =>
        new(path, bytes, OutputRole.View, OutputFidelity.Generated,
            $"viewer aid derived from {derivedFrom}; that file is the round-trip source of truth");
}

/// <summary>
/// A data-tree output as loaded back from disk, which is what an inverse consumes.
/// </summary>
/// <param name="Path">Its path relative to the data directory.</param>
/// <param name="Bytes">Its contents as read.</param>
/// <param name="Role">Its role; inverses only ever see <see cref="OutputRole.Data"/> and <see cref="OutputRole.Raw"/>.</param>
public sealed record LoadedOutput(string Path, byte[] Bytes, OutputRole Role);

/// <summary>
/// What a family promises its inverse achieves, and — for a <see cref="OutputFidelity.Canonical"/>
/// family — the named reason the re-encode may differ from the shipped bytes.
/// </summary>
/// <param name="Expected">The fidelity a successful round trip earns.</param>
/// <param name="Reason">Why it is not <see cref="OutputFidelity.Exact"/>, when it is not.</param>
public sealed record FidelityRule(OutputFidelity Expected, string? Reason = null)
{
    /// <summary>The default promise: the inverse reproduces the source bytes exactly.</summary>
    public static FidelityRule Exact { get; } = new(OutputFidelity.Exact);
}
