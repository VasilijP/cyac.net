namespace CYAC.Port.Transform.Transform;

/// <summary>
/// Read access, during a forward transform only, to originals a family needs besides its own bytes.
/// </summary>
/// <remarks>
/// <para>
/// Most families explain one member from that member alone.  Two do not, and both are facts about
/// the game rather than shortcomings of the transform:
/// </para>
/// <list type="bullet">
///   <item><c>.msk</c> masks are headerless — their geometry lives in the executable's DGROUP or in
///   the companion <c>.pic</c> (<see cref="Families.MaskTransform"/>).</item>
///   <item>a <c>.pic</c>'s palette is program state — the companion <c>.pal</c> when the screen has
///   one, otherwise the global <c>1a.lib/palette</c> (<see cref="Families.PicTransform"/>).</item>
/// </list>
/// <para>
/// The <b>inverse</b> never needs this: a data-tree image keeps its own indices and its own
/// dimensions, so <see cref="TreeVerifier"/> runs with no provider at all and the round trip still
/// closes without the originals' internals.
/// </para>
/// </remarks>
public interface IOriginalData
{
    /// <summary>
    /// The decoded body of an archive member, or <see langword="null"/> when there is no such member.
    /// </summary>
    /// <param name="archiveFileName">The archive's canonical file name, e.g. <c>"2a.lib"</c>.</param>
    /// <param name="memberName">The member name, matched case-insensitively; the first match wins.</param>
    byte[]? TryGetArchiveMember(string archiveFileName, string memberName);

    /// <summary>
    /// The unpacked layer-1 program image at load segment 0x1000, or <see langword="null"/> when
    /// this run has no executable to unpack.
    /// </summary>
    byte[]? TryGetProgramImage();
}
