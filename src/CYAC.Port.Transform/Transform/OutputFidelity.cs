namespace CYAC.Port.Transform.Transform;

/// <summary>
/// How faithfully one data-tree output can be turned back into the bytes it came
/// from.
/// </summary>
public enum OutputFidelity
{
    /// <summary>Re-encoding the output reproduces the original bytes exactly.</summary>
    Exact,

    /// <summary>
    /// Re-encoding differs from the shipped bytes only in a <b>named, proven-irrelevant</b> way —
    /// the shipped file is one of several valid encodings and the decoded payload is exact.  The
    /// reason is always recorded alongside (<c>fidelity_note</c>).
    /// </summary>
    Canonical,

    /// <summary>Something the original carried is not carried by the output; the note says what.</summary>
    Lossy,

    /// <summary>
    /// The output was produced correctly as far as the tool can tell, but nothing proves it: the
    /// family has no inverse to run and the input matched no known distribution, so there is no
    /// recorded digest to compare against.  Added by the <c>exe</c> family (T2), whose transform is
    /// an unpack — re-packing is not a goal, so its verification is re-derivation plus a digest
    /// check, and the digest only exists for a recognised distribution.  The note always says why.
    /// </summary>
    Unverified,

    /// <summary>
    /// The output has no original at all: a viewer aid derived from another output, or a default
    /// emitted because the source file was absent.  Nothing to round-trip.
    /// </summary>
    Generated,
}

/// <summary>What a data-tree output is for.</summary>
public enum OutputRole
{
    /// <summary>The editable source of truth for its source bytes; the inverse reads it.</summary>
    Data,

    /// <summary>A derived, human-facing rendering of a <see cref="Data"/> output (e.g. a PNG swatch).</summary>
    View,

    /// <summary>
    /// Decoded bytes no family explains yet — carried verbatim so the round trip still closes, and
    /// counted in the law-L4 burn-down.
    /// </summary>
    Raw,

    /// <summary>
    /// Recorded MACHINE CODE (code is not data): the inverse reads it back verbatim, but
    /// it is explained rather than unexplained, because the sibling document names what the code is
    /// and how it is called.  Added by the audio drivers (T7); their bodies are 8086 instructions and
    /// converting them is not a goal.
    /// </summary>
    Code,
}
