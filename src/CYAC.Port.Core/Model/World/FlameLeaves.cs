namespace CYAC.Port.Core.Model.World;

/// <summary>
/// The AFTERBURNER FLAME — which paint-tree leaves of an aeroplane's mesh are the exhaust plume, and
/// when the original draws them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mechanism</b> (<c>mesh_lod_prepare_gear_and_flame_state @image@0x2D8E9</c> PIECE 1).  The
/// per-class PREPARE callback runs once per frame for whichever aircraft mesh is about to be
/// face-rendered and writes a paint-tree LEAF TAG byte:
/// </para>
/// <code>
/// if (render_object == camera_target_object &amp;&amp; !model_preview &amp;&amp; (input_state &amp; 1))
///     tag = 3;    // image@0x2D910 — bit 1 set: mesh_poly_tree_walk DRAWS the leaf
/// else
///     tag = 1;    // image@0x2D942 — bit 1 clear: the walk RETURNS at image@0x1A8D6
/// </code>
/// <para>
/// so the plume is drawn only while the afterburner bit
/// (<c>g_input_state_bitfield [0xF0BC]</c> bit 0, the same bit
/// <c>hud_afterburner_led_draw @image@0x01D50</c> lights its LED from and the same index as
/// <c>master[+0x124]</c> bit 0 = <see cref="Flight.AircraftStatusFlags.Afterburner"/>) is lit on the
/// object the CAMERA is following, outside the model preview.
/// </para>
/// <para>
/// <b>Why the port needs a table.</b> The three tag bytes ship as <b>3</b> — drawn — so a renderer that
/// reads the shipped tag and never runs the prepare callback draws the plume on every frame of every
/// MiG-21 and F-4, lit or not.  The port runs no prepare callbacks, so it states the rule instead, the
/// same way <see cref="GearArticulation"/> states PIECE 2's.
/// </para>
/// <para>
/// <b>The addresses.</b>  The callback's three destinations are DGROUP <c>[0x8D5C]</c>,
/// <c>[0x6084]</c> and <c>[0x6067]</c>; over the DGROUP base <c>image@0x3BD60</c> they are
/// <c>image@0x44ABC</c>, <c>image@0x41DE4</c> and <c>image@0x41DC7</c> — and all three are leaves of
/// <c>data/exe/meshes/&lt;name&gt;.json</c> <c>lods[2].paintTree</c> carrying two faces each: ONE on
/// the MiG-21 and TWO on the F-4, which is the aeroplanes' own engine count (one R-25, two J79).
/// The two taildraggers and the two subsonic jets have no afterburner and no leaf.
/// </para>
/// <para>
/// <b>Not this table:</b> <c>mesh_lod_prepare_flame_side_select @image@0x2DAFC</c> writes the same
/// kind of tag into two leaves of <c>hell</c> LOD1 (<c>image@0x4335D</c> / <c>image@0x43363</c>) and
/// is NOT afterburner-gated at all — it picks a SIDE from
/// <c>g_cockpit_aircraft_mode_flags [0xC332]</c> bit 0 and only when the object carries
/// <c>+0x03 &amp; 0x80</c> or the model preview is up.  It is a different effect on a different
/// aeroplane and is left un-ported (the port draws that mesh whole, as it did before).
/// </para>
/// </remarks>
public static class FlameLeaves
{
    /// <summary>
    /// The exhaust-plume leaves of each flyable mesh, by basename — the paint-tree leaf
    /// <c>image@</c> addresses the prepare callback tags, in the order it writes them.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<int>> Rules { get; } =
        new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase)
        {
            // [0x8D5C] — image@0x2D910; mig21 LOD2, faces 0x8C79 / 0x8C83.
            ["mig21"] = [0x44ABC],

            // [0x6084] / [0x6067] — image@0x2D914 / 0x2D918; f4 LOD2, one leaf per engine.
            ["f4"] = [0x41DE4, 0x41DC7],
        };

    /// <summary>Whether this aeroplane has an afterburner plume in its mesh at all.</summary>
    /// <param name="basename">The mesh basename, or null.</param>
    public static bool Has(string? basename) =>
        basename is not null && Rules.ContainsKey(basename);

    /// <summary>
    /// The leaves to HIDE on this aeroplane this frame, or null when there is nothing to hide.
    /// </summary>
    /// <param name="basename">The mesh basename, or null.</param>
    /// <param name="lit">
    /// Whether the plume is drawn: the afterburner bit is set on an object the camera is following,
    /// outside the model preview (<c>image@0x2D8F9..0x2D909</c>).  Everything else hides it.
    /// </param>
    /// <returns>The leaf addresses, or <see langword="null"/>.</returns>
    public static IReadOnlyList<int>? Hidden(string? basename, bool lit) =>
        !lit && basename is not null && Rules.TryGetValue(basename, out IReadOnlyList<int>? leaves) ? leaves : null;

    /// <summary>
    /// <paramref name="hidden"/> with this aeroplane's plume leaves added when it is not lit.
    /// </summary>
    /// <param name="basename">The mesh basename, or null.</param>
    /// <param name="lit">Whether the plume is drawn this frame.</param>
    /// <param name="hidden">What the caller already hides (H18's ejection parts), or null.</param>
    /// <returns>The merged list, or <see langword="null"/> when nothing is hidden.</returns>
    public static IReadOnlyList<int>? Merge(
        string? basename, bool lit, IReadOnlyList<int>? hidden)
    {
        if (Hidden(basename, lit) is not { } plume)
        {
            return hidden;
        }

        if (hidden is not { Count: > 0 })
        {
            return plume;
        }

        List<int> merged = new List<int>(hidden.Count + plume.Count);
        merged.AddRange(hidden);
        merged.AddRange(plume);
        return merged;
    }
}
