namespace CYAC.Port.Transform.Families.ExeTables;

/// <summary>One contiguous run of original bytes an exe-resident table occupies, rebuilt.</summary>
/// <param name="Name">What the run is, for a verification message.</param>
/// <param name="ImageOffset">Its byte offset in the unpacked layer-1 image.</param>
/// <param name="Bytes">The bytes the inverse produced for it.</param>
public sealed record ExeSlice(string Name, int ImageOffset, byte[] Bytes);

/// <summary>What a table's forward transform produced.</summary>
/// <param name="Json">The document to write into the data tree.</param>
/// <param name="UnknownBytes">How many bytes it carries as <c>unknown_*</c>.</param>
/// <param name="Summary">One line for the console: what was extracted.</param>
public sealed record ExeTableResult(byte[] Json, int UnknownBytes, string Summary);

/// <summary>
/// One exe-resident data table: a document in the tree, and the exact image bytes it came from.
/// </summary>
/// <remarks>
/// <para>
/// The <c>exe-tables</c> family has no whole-source inverse — nobody re-packs <c>yeager.exe</c>
/// — so the round trip is proved <b>per table</b> instead, and it is a stronger check than
/// a container rebuild: <see cref="Inverse"/> re-encodes the document and <c>TreeVerifier</c>
/// diffs the result against exactly the image slices the table occupies.  A table that verifies
/// is one whose every byte the document explains.
/// </para>
/// <para>
/// A table may occupy several disjoint runs (the class records are 23 of them), which is why the
/// inverse returns a list rather than one buffer.
/// </para>
/// </remarks>
internal interface IExeTable
{
    /// <summary>Where the document lands, relative to the data tree root.</summary>
    string TreePath { get; }

    /// <summary>One sentence for the generated <c>data/README.md</c> and the manifest note.</summary>
    string Description { get; }

    /// <summary>Extracts the table from the unpacked layer-1 image.</summary>
    /// <param name="image">The image at load segment <c>0x1000</c>.</param>
    ExeTableResult Forward(ReadOnlySpan<byte> image);

    /// <summary>Re-encodes the document into the image bytes it came from.</summary>
    /// <param name="json">The document as read back from the tree.</param>
    IReadOnlyList<ExeSlice> Inverse(ReadOnlySpan<byte> json);
}

/// <summary>Addresses shared by every exe-resident table.</summary>
internal static class ExeAddresses
{
    public const int DgroupImageBase = 0x3BD60;

    /// <summary>The load segment every <c>image@</c> citation assumes.</summary>
    public const int LoadSegment = 0x1000;

    /// <summary>The image offset of a DGROUP offset.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    public static int Image(int dgroupOffset) => DgroupImageBase + dgroupOffset;

    /// <summary>
    /// Resolves a stored far pointer to an image offset, or 0 for a NULL pointer.
    /// </summary>
    /// <param name="offset">The pointer's offset half.</param>
    /// <param name="segment">The pointer's segment half, as relocated for <see cref="LoadSegment"/>.</param>
    public static int Resolve(ushort offset, ushort segment) =>
        segment == 0 ? 0 : ((segment - LoadSegment) * 16) + offset;
}
