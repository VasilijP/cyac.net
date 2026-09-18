using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Render;

/// <summary>
/// One aircraft mesh posed for a gear angle: which vertices rotate about which hinge, and which
/// shape records are hidden.
/// </summary>
/// <remarks>
/// <para>
/// This is the port's stand-in for the original's per-class PREPARE callback
/// <c>mesh_lod_prepare_gear_and_flame_state @image@0x2D8E9</c>, which every aircraft LOD descriptor
/// registers at <c>+0x02</c> and <c>mesh_face_render_dispatch</c> calls once per frame
/// (<c>lcall [si+2]</c> @<c>image@0x1E1E2</c>).  That callback does exactly two things to the gear:
/// </para>
/// <list type="number">
/// <item>writes <c>g_gear_deploy_angle_bam [0xEF96]</c> (and, for the mirrored leg,
/// <c>angle_wrap_0_to_0xB40(−angle)</c>) into one Euler slot of each leg's 9-byte ARTICULATION
/// BLOCK, which the per-mesh JIT then feeds to <c>gfx_rot_mat3_from_euler @image@0x14D9C</c>
/// (template <c>image@0x1CD7A</c>, block pointer SMC-patched at <c>image@0x1CD8B</c>); and</item>
/// <item>writes 1 or 3 into 22 BSP-leaf TAG bytes, where bit 1 is "draw this leaf"
/// (<c>mesh_poly_tree_walk @image@0x1A8A8</c>: <c>test byte[si],2 / je → return</c>,
/// <c>image@0x1A8D6</c>).</item>
/// </list>
/// <para>
/// The rotation is applied to the group's stored DELTAS, and every group's first vertex has the
/// block's <c>pivot</c> byte as its edge-tree parent (verified 16/16, and re-asserted at load by
/// <c>MeshLibrary</c>), so rotating the absolute vertices <c>[first..last]</c> rigidly about the
/// pivot's own position is the same operation — which is what this type does, in <c>double</c>.
/// </para>
/// <para>
/// The same leaf-tag mechanism serves the OTHER shipped prepare callbacks: the ejection meshes'
/// <c>image@0x2C9F4</c> (eject1: pilot vs seat) and <c>image@0x2CAC8</c> (eject4: one of two limb
/// sets) write 1/3 into their trees' leaf tags per drawn object.  The sim hands those in as
/// <see cref="SceneInstance.HiddenLeafNodes"/> and this type folds them into the same hidden-record
/// mask, with or without a gear articulation.
/// </para>
/// <para>
/// The pose is a per-frame, per-instance value; the renderer never writes it back anywhere.
/// </para>
/// </remarks>
public sealed class GearPose
{
    private readonly GearArticulation? _gear;
    private readonly MeshLod _lod;
    private readonly int _angleBam;
    private readonly bool[] _hiddenRecords;

    private GearPose(GearArticulation? gear, MeshLod lod, int angleBam, bool[] hiddenRecords)
    {
        _gear = gear;
        _lod = lod;
        _angleBam = angleBam;
        _hiddenRecords = hiddenRecords;
    }

    /// <summary>
    /// The pose for an instance, or <see langword="null"/> when there is nothing to do (the mesh has
    /// no gear, the LOD is not the one that carries the blocks, the sim said nothing, or the caller
    /// turned articulation off).
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="lodIndex">The LOD being drawn.</param>
    /// <param name="angleBam">The instance's gear angle; negative means "un-posed".</param>
    /// <param name="enabled">Whether articulation is on.</param>
    /// <param name="hiddenLeaves">
    /// paint-tree leaf nodes (<c>image@</c> keys of <see cref="MeshLod.PaintLeaves"/>) the
    /// instance's own prepare callback hides this frame; <see langword="null"/> for none.
    /// </param>
    public static GearPose? For(
        MeshModel mesh, int lodIndex, int angleBam, bool enabled, IReadOnlyList<int>? hiddenLeaves = null)
    {
        bool gearApplies = enabled && angleBam >= 0 && mesh.Gear is { } g && g.LodIndex == lodIndex;
        if (!gearApplies && (hiddenLeaves is null || hiddenLeaves.Count == 0))
        {
            return null;
        }

        MeshLod lod = mesh.LodByIndex(lodIndex);
        bool[] hidden = new bool[lod.Faces.Length];
        bool anyHidden = false;

        if (hiddenLeaves is not null)
        {
            foreach (int leaf in hiddenLeaves)
            {
                anyHidden |= Hide(lod, leaf, hidden);
            }
        }

        if (!gearApplies)
        {
            return anyHidden ? new GearPose(null, lod, 0, hidden) : null;
        }

        GearArticulation gear = mesh.Gear!;
        int angle = Math.Clamp(angleBam, GearArticulation.ExtendedAngle, GearArticulation.RetractedAngle);
        foreach (GearGroup group in gear.Groups)
        {
            if (!GearArticulation.LeafVisible(group, angle))
            {
                anyHidden |= Hide(lod, group.LeafNodeImage, hidden);
            }
        }

        // The 0x03 leaves (the taildraggers' tailwheels, the jets' main-gear doors) carry no block:
        // they are shown while the gear moves and hidden once it is stowed (image@0x2D956).
        if (GearArticulation.AllHidden(angle))
        {
            foreach (GearLeaf leaf in gear.ExtraLeaves)
            {
                anyHidden |= Hide(lod, leaf.LeafNodeImage, hidden);
            }
        }

        return angle == GearArticulation.ExtendedAngle && !anyHidden
            ? null
            : new GearPose(gear, lod, angle, hidden);
    }

    /// <summary>Whether a shape record of the LOD is suppressed this frame.</summary>
    /// <param name="recordIndex">Index into the LOD's faces.</param>
    public bool IsHidden(int recordIndex) => _hiddenRecords[recordIndex];

    /// <summary>Moves one model-space vertex into its posed position.</summary>
    /// <param name="vertexIndex">The vertex.</param>
    /// <param name="x">Model X, updated in place.</param>
    /// <param name="y">Model Y, updated in place.</param>
    /// <param name="z">Model Z, updated in place.</param>
    public void Transform(int vertexIndex, ref double x, ref double y, ref double z)
    {
        if (_gear is null)
        {
            return;
        }

        // SHEET INFLATION — a port-added node (MeshLod.RefinedVertices) belongs to the run its
        // SOURCE node is in, so an inflated wheel or gear door turns with its leg.
        int source = _lod.SourceVertex(vertexIndex);
        IReadOnlyList<GearGroup> groups = _gear.Groups;
        // By index, not foreach: enumerating the IReadOnlyList per VERTEX boxed an enumerator each
        // time (~7 KB a frame on the plain P-51 with the gear up, 18 KB once inflated).
        for (int g = 0; g < groups.Count; g++)
        {
            GearGroup group = groups[g];
            if (source < group.FirstVertex || source > group.LastVertex)
            {
                continue;
            }

            (double px, double py, double pz) = _lod.VertexPosition(group.PivotVertex);
            double angle = BamToRadians(group.Negated ? -_angleBam : _angleBam);
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);
            double dx = x - px, dy = y - py, dz = z - pz;

            // The matrix gfx_rot_mat3_from_euler builds for a single non-zero slot, row-major, with
            // v' = M·v (see GearAxis's remarks for how the slot names map to axes).
            switch (group.Axis)
            {
                case GearAxis.Roll:      // angle_x: turns the X–Y plane, i.e. about +Z.
                    (dx, dy) = ((dx * c) + (dy * s), (-dx * s) + (dy * c));
                    break;

                case GearAxis.Pitch:     // angle_y: turns the Y–Z plane, i.e. about +X.
                    (dy, dz) = ((dy * c) + (dz * s), (-dy * s) + (dz * c));
                    break;

                default:                 // angle_z: turns the Z–X plane, i.e. about +Y.
                    (dx, dz) = ((dx * c) - (dz * s), (dx * s) + (dz * c));
                    break;
            }

            x = px + dx;
            y = py + dy;
            z = pz + dz;
            return;
        }
    }

    /// <summary>BAM (2,880 to the circle) to radians.</summary>
    private static double BamToRadians(int bam) => bam * 2.0 * Math.PI / 2880.0;

    private static bool Hide(MeshLod lod, int leafImage, bool[] hidden)
    {
        if (!lod.PaintLeaves.TryGetValue(leafImage, out int[]? records))
        {
            return false;
        }

        bool any = false;
        foreach (int record in records)
        {
            hidden[record] = true;
            any = true;
        }

        return any;
    }
}
