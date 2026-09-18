namespace CYAC.Port.Transform.Transform;

/// <summary>
/// One resource family's forward transform and its inverse — the unit of work of
/// <c>cyac-transform</c>.
/// </summary>
/// <remarks>
/// Law L3: <c>Inverse(Forward(x)) == x</c> is the proof that the family is understood, so the two
/// halves are one interface and neither ships without the other.  Law L4: whatever the forward half
/// cannot explain is emitted as a named <c>unknown_0xNN</c> field and counted, never dropped.
/// </remarks>
public interface IFamilyTransform
{
    /// <summary>The family's identifier: the value <c>--only</c> matches and the manifest records.</summary>
    string Family { get; }

    /// <summary>One sentence for the generated <c>data/README.md</c>: what this family writes and where.</summary>
    string TreeDescription { get; }

    /// <summary>What a successful round trip earns this family, and why if it is not exact.</summary>
    FidelityRule FidelityRule { get; }

    /// <summary>Whether this family recognises a source and will transform it.</summary>
    /// <param name="source">The candidate source bytes and their origin.</param>
    bool Claims(TransformSource source);

    /// <summary>Transforms the source into data-tree outputs.</summary>
    /// <param name="source">The source this family claimed.</param>
    /// <param name="context">The run's context; use it to allocate output paths.</param>
    IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context);

    /// <summary>
    /// Rebuilds the source's decoded bytes from its data outputs, as loaded back from the tree.
    /// </summary>
    /// <param name="outputs">The family's <see cref="OutputRole.Data"/> outputs for one source.</param>
    /// <param name="context">The run's context.</param>
    byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context);

    /// <summary>
    /// Rebuilds one NAMED source's bytes, for a family where one document explains more than one
    /// archive member.
    /// </summary>
    /// <remarks>
    /// Almost every family maps one member to its own document, so the default simply forwards to
    /// <see cref="Inverse(IReadOnlyList{LoadedOutput}, TransformContext)"/>.  The exception is a
    /// family whose document is a MERGE: <c>aircraft/&lt;name&gt;.json</c> carries both the
    /// <c>.fmd</c> and the <c>.fme</c> of one aircraft, and the two members' bytes differ, so the
    /// inverse has to be told which of them is being rebuilt.  The member name is the only thing
    /// that distinguishes them — the tree path is the same file for both.
    /// </remarks>
    /// <param name="memberName">The archive member being rebuilt, e.g. <c>"P51.FME"</c>.</param>
    /// <param name="outputs">The family's <see cref="OutputRole.Data"/> outputs for that member.</param>
    /// <param name="context">The run's context.</param>
    byte[] Inverse(string memberName, IReadOnlyList<LoadedOutput> outputs, TransformContext context) =>
        Inverse(outputs, context);
}
