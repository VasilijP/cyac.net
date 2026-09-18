namespace CYAC.Port.Render;

/// <summary>
/// How the polygon EDGES are drawn: the port's scrutiny switch for the shipped models.
/// </summary>
/// <remarks>
/// A PRESENTATION switch: the WHAT stage emits each surviving polygon's edges as one-pixel screen
/// capsules (the same primitive a gear leg or a parachute shroud already is), nudged a fraction of a
/// percent towards the eye so an edge wins the depth test against its own face and loses it to any
/// face that is really in front — hidden lines are removed by the depth test alone, without a second
/// pass.  Nothing about the polygon list changes under <see cref="Overlay"/>; under <see cref="Only"/>
/// the polygons are not emitted at all, so every edge shows and the background shows through.
/// </remarks>
public enum WireframeMode
{
    /// <summary>The picture as shipped: filled polygons, no edge lines.</summary>
    Off = 0,

    /// <summary>Filled polygons WITH their edges drawn over them (hidden edges removed by depth).</summary>
    Overlay = 1,

    /// <summary>Edges only — the classic wireframe, every edge visible.</summary>
    Only = 2,
}

/// <summary>Where a face's colour comes from.</summary>
public enum FaceColorMode
{
    /// <summary>The record's own palette index (and the vector markings over it).</summary>
    Paint = 0,

    /// <summary>
    /// A CONTRASTING flat colour per record from <see cref="ContrastPalette"/>, so each polygon can
    /// be told from its neighbours; the markings shader is bypassed.  The assignment is a function
    /// of the record's index, its instance and <see cref="SceneRenderOptions.FaceColorSeed"/> —
    /// stepping the seed RESHUFFLES it.
    /// </summary>
    Contrast = 1,
}

/// <summary>
/// The INTERIOR MASK made visible (<see cref="Raster.InteriorMask"/>): the "roof in a
/// dark shed on a sunny day" — a BINARY silhouette of the player's aeroplane, no smooth edges, so
/// a hole poking through the model reads as a black pixel inside white and the mask's
/// edge/interior answer can be checked by eye.
/// </summary>
public enum MaskView
{
    /// <summary>The ordinary picture.</summary>
    Off = 0,

    /// <summary>
    /// The world rows replaced by the mask's raw CENTRE-RULE silhouette: white where a pixel centre
    /// is inside an opaque polygon of the masked instance, black elsewhere — and RED where the
    /// closing (dilate then erode) filled a pixel the raw silhouette did not have: a sub-pixel gap
    /// or a one-pixel hole the model leaves open.
    /// </summary>
    Silhouette = 1,

    /// <summary>
    /// The picture as rendered, with the mask's classification stamped over it: GREEN where a pixel
    /// is INTERIOR (its centre and all eight neighbours' centres inside the closed silhouette, the
    /// pixels the seam rule may heal), RED on the outline ring (inside the closed silhouette but not
    /// interior — the anti-aliased edge the rule leaves alone).
    /// </summary>
    Interior = 2,
}

/// <summary>The flat colours <see cref="FaceColorMode.Contrast"/> paints with.</summary>
public static class ContrastPalette
{
    /// <summary>
    /// The set, in order: magenta, cyan, yellow, white, black, red, green, blue, orange, purple,
    /// spring green, pink — twelve saturated colours a human tells apart at a glance.
    /// </summary>
    public static readonly Rgb24[] Colors =
    [
        new(255, 0, 255),     // magenta
        new(0, 255, 255),     // cyan
        new(255, 255, 0),     // yellow
        new(255, 255, 255),   // white
        new(0, 0, 0),         // black
        new(255, 0, 0),       // red
        new(0, 200, 0),       // green
        new(40, 80, 255),     // blue
        new(255, 140, 0),     // orange
        new(160, 0, 255),     // purple
        new(0, 255, 140),     // spring green
        new(255, 128, 192),   // pink
    ];

    /// <summary>
    /// Multipliers coprime with <see cref="Colors"/>.Length, one per reshuffle: with a coprime
    /// stride two CONSECUTIVE records of one instance can never share a colour, whatever the seed.
    /// </summary>
    private static readonly int[] Strides = [5, 7, 11, 13, 17, 19, 23, 25, 29, 31, 35, 37];

    /// <summary>The colour of one record under one seed.</summary>
    /// <param name="record">The record's index within its LOD.</param>
    /// <param name="instance">The instance's ordinal in the frame (so two copies of one mesh differ).</param>
    /// <param name="seed">The reshuffle counter; any integer.</param>
    public static int IndexOf(int record, int instance, int seed)
    {
        int n = Colors.Length;
        int s = ((seed % n) + n) % n;
        int stride = Strides[s];
        long key = ((long)record + (3L * instance)) * stride + (seed * 7L);
        return (int)(((key % n) + n) % n);
    }

    /// <summary>The wire colour that reads against a given face-colour mode: white over the paint, black over the contrast set.</summary>
    /// <param name="faces">The face-colour mode in force.</param>
    public static Rgb24 AutoWireColor(FaceColorMode faces) =>
        faces == FaceColorMode.Contrast ? new Rgb24(0, 0, 0) : new Rgb24(255, 255, 255);
}
