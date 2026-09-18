using CYAC.Port.Render.Pipeline;

namespace CYAC.Port.Render.Raster;

/// <param name="Layer">The coarse sort tier.</param>
/// <param name="Priority">
/// The class's <c>MeshModel.RenderLayerPriority</c> — the original's painter order (city 0x05 under
/// airport 0x06 under road 0x14 under urban 0x15 under river 0x28), highest in front.  Under the
/// fragment raster it is a SORT KEY component, no longer a multiplicative depth bias.
/// </param>
/// <param name="Group">Which overlap group the primitive belongs to; 0 is "none".</param>
/// <param name="Combine">How this group's fragments combine where they meet on one pixel.</param>
/// <param name="SubmissionIndex">Its position in the WHAT walk — the final tie-break.</param>
/// <remarks>
/// It reaches the rasteriser through <see cref="FragmentRaster.BeginPrimitive"/> rather than through
/// <see cref="Paint"/>, because it is not what a primitive PAINTS: it is where the primitive stands
/// in the frame's order.
/// </remarks>
internal readonly record struct PrimitiveKey(
    DrawLayer Layer, int Priority, int Group, CombineRule Combine, int SubmissionIndex);

/// <summary>What one rasteriser's FRAGMENT BUFFER did in a frame.</summary>
/// <param name="Inserted">Fragments kept — (primitive, pixel) pairs that could be visible.</param>
/// <param name="Dropped">Fragments the per-pixel opaque depth guard rejected at insertion.</param>
/// <param name="MaxListLength">The longest per-pixel fragment list the frame produced.</param>
/// <param name="BackgroundPixels">
/// Pixels the TERMINAL FUNCTION was evaluated for: every pixel no fragment reached, plus every
/// pixel whose fragments stopped short of opacity.  The cost of the background, measured rather
/// than assumed.
/// </param>
/// <remarks>Geometry, not game facts: what a tuning pass reads.</remarks>
/// <param name="TrivialAccepts">
/// (primitive, tile) pairs the TRIVIAL ACCEPT took: the tile's clip rectangle lay wholly inside
/// the polygon, so every pixel took coverage 1 with no edge work.
/// </param>
internal readonly record struct FragmentStats(
    long Inserted,
    long Dropped,
    int MaxListLength,
    long BackgroundPixels = 0,
    long TrivialAccepts = 0,
    long SeamPixels = 0);
