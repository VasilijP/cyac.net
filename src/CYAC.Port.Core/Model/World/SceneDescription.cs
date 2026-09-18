using CYAC.Port.Core.Model.Mission;

namespace CYAC.Port.Core.Model.World;

/// <summary>
/// One drawable mesh instance: a <see cref="MeshModel"/> at a world position, under an orientation.
/// </summary>
/// <param name="Mesh">The geometry.</param>
/// <param name="X">World X, in WORLD units (not the <c>&lt;&lt; 8</c> position units).</param>
/// <param name="Y">World Y (up), world units.</param>
/// <param name="Z">World Z, world units.</param>
/// <param name="HeadingDegrees">
/// The instance's heading in degrees.  0 points at +Z and the angle turns toward −X — the engine's
/// own convention, which <c>angle_mat3_pair_build @image@0x1BFA4</c> establishes by NEGATING the
/// angle before its trig lookups (<c>CYAC.Formats/Mesh/SceneryFootprint.cs</c>
/// <c>HeadingConvention</c>) and which the flight kernel's own
/// <see cref="Sim.Flight.BodyVelocityProjection"/> independently reproduces for the player.
/// </param>
/// <param name="PitchDegrees">Nose-up positive; 0 for the theater's scenery.</param>
/// <param name="RollDegrees">Right-wing-down positive; 0 for the theater's scenery.</param>
/// <remarks>
/// The unit choice is deliberate: the SIM keeps positions as <c>world &lt;&lt; 8</c> (H2 §A2) and the
/// renderer works in world units, which are the same units a mesh's vertices are in once multiplied
/// by <see cref="MeshModel.WorldScale"/>.  Converting once, here, keeps the shift
/// out of the geometry.
/// </remarks>
/// <param name="GearAngleBam">
/// The instance's landing-gear angle (<c>g_gear_deploy_angle_bam [0xEF96]</c>, 0 extended …
/// <c>0x2D0</c> retracted) when the mesh has a <see cref="MeshModel.Gear"/> articulation; <c>-1</c>
/// when the sim has nothing to say and the mesh should draw un-posed.  It is SIM state that the sim
/// writes here and the renderer only reads.
/// </param>
/// <param name="Opacity">
/// A PRESENTATION-only multiplier on every face's coverage, 0…1.  1 (the default) draws the mesh
/// exactly as its records say; below 1 the whole instance fades, which is how the tiled cloud deck
/// takes a cloud to zero before the tiling's own edge can pop it
/// (<see cref="CloudDeck.FadeAt"/>).  The original has no such thing.
/// </param>
/// <param name="WorldScaleOverride">
/// A per-instance replacement for <see cref="MeshModel.WorldScale"/>; <c>0</c> (the default) means
/// "use the class's own".  Needed because one shipped class really does have a per-frame scale:
/// <c>alloc_slot_b_camera_pos_snap_update @image@0x2DD44</c> writes the ground grid's exponent into
/// the <c>spheres</c> registry slot's <c>+0x0C</c> every frame from the camera's altitude
/// (<see cref="GroundGrid.ScaleShiftExponentFor"/>).
/// </param>
/// <remarks>
/// Build one with the constructor, never <c>new SceneInstance</c>: a record struct's implicit
/// parameterless constructor does not run the primary constructor, so the defaults above would not
/// apply and the instance would be a fully transparent null-mesh (the same trap
/// <see cref="SceneSnapshot"/>'s siblings carry a note about; it cost H3 a debugging round).
/// </remarks>
/// <param name="EffectAgeFrameTime">
/// The AGE of the deferred-effect record this instance belongs to, in frame-time units of its
/// <c>0x100</c>-unit life, or <c>-1</c> when the instance is not a live effect.  It is SIM state
/// (<c>CombatSceneObject.EffectAgeFrameTime</c>); the renderer only reads it, and it is what lets an
/// opcode-4 anchor grow and fade exactly as <c>effect_particle_draw @image@0x03C5A</c> does instead
/// of being drawn at one size.
/// </param>
/// <param name="EffectFork">
/// The record's <c>+0x0C</c> byte, the fork <c>deferred_effect_render @image@0x03E30</c> takes: 0 =
/// the growing disc plus its eight shards, non-zero = the bitmap explosion or the six-disc particle
/// burst.
/// </param>
/// <param name="EffectSequence">
/// The record's <c>+0x0A</c> sequence byte, the per-explosion orientation seed the debris ring is
/// rotated by (<c>image@0x03D60</c>).
/// </param>
/// <param name="HiddenLeafNodes">
/// paint-tree LEAF NODES (<c>image@</c> addresses, the keys of <see cref="MeshLod.PaintLeaves"/>) the
/// instance's per-class prepare callback tags HIDDEN this frame, so their records are not drawn: the
/// ejection meshes' pilot/seat split and the parachutist's limb set (<c>image@0x2C9F4</c>,
/// <c>image@0x2CAC8</c>).  It is SIM state (<c>CombatSceneObject.HiddenLeafNodes</c>); the renderer
/// only reads it.  <see langword="null"/> or empty means "draw every leaf".
/// </param>
/// <param name="SmokePuff">
/// When the instance is a SMOKE PUFF, its slot's kind, age and span
/// (<see cref="Sim.Combat.Effects.SmokePuffState"/>): the renderer runs the class's prepare callback
/// law (<c>CYAC.Port.Render.SmokeLook</c>) on it and draws the three disc records at the radius and
/// colour the original gives them THIS frame — 25 growing to 200 world units over the puff's life —
/// instead of the static 16–20-unit records.  It is SIM state (<c>CombatSceneObject.SmokePuff</c>);
/// null draws the static records.
/// </param>
/// <param name="AtInfinity">
/// A PRESENTATION flag for the <c>sun</c>: the instance is drawn at infinite depth, so every world
/// object is in front of it however far away.  The original keeps its sun object 100 world units
/// above the camera and paints it in distance order like any other object, which lets an aircraft
/// farther than that pass BEHIND the sun disc.  A sun is at infinity; the port draws it there.  Depth
/// test only — position and size are still the original's (<see cref="SunDisc"/>).
/// </param>
public readonly record struct SceneInstance(
    MeshModel Mesh,
    double X,
    double Y,
    double Z,
    double HeadingDegrees,
    double PitchDegrees = 0.0,
    double RollDegrees = 0.0,
    int GearAngleBam = -1,
    double Opacity = 1.0,
    double WorldScaleOverride = 0.0,
    int EffectAgeFrameTime = -1,
    byte EffectFork = 0,
    byte EffectSequence = 0,
    IReadOnlyList<int>? HiddenLeafNodes = null,
    bool AtInfinity = false,
    Sim.Combat.Effects.SmokePuffState? SmokePuff = null,
    bool SeamMask = false)
{
    /// <summary>The scale this instance draws at: its override when set, else the class's.</summary>
    public double EffectiveWorldScale =>
        WorldScaleOverride > 0.0 ? WorldScaleOverride : Mesh.WorldScale;
}

/// <summary>
/// A theater's static scenery, resolved once: every <c>.W</c> placement whose class has a mesh.
/// </summary>
/// <remarks>
/// <para>
/// The <c>.W</c> catalogs place scenery with position tag 1 (<c>ground</c>: x/z only, y = 0 — CYAC's
/// terrain is a flat plane with named meshes on it, <c>project_terrain_resolved</c>) plus, on most
/// instances, attr <c>0x80</c> <c>aux0_heading</c> in ⅛° (2,880 to the circle).  This type is the
/// port's read of that layer; it is IMMUTABLE and shared, and the renderer only reads it.
/// </para>
/// <para>
/// Instances whose class has no mesh in the tree are dropped and counted in
/// <see cref="SkippedPlacements"/> rather than silently lost.
/// </para>
/// </remarks>
public sealed class WorldScene
{
    private WorldScene(string name, SceneInstance[] statics, int skipped, int worldExtentX, int worldExtentZ)
    {
        Name = name;
        Statics = statics;
        SkippedPlacements = skipped;
        WorldExtentX = worldExtentX;
        WorldExtentZ = worldExtentZ;
    }

    /// <summary>The theater's asset name, e.g. <c>GERMANY.W</c>.</summary>
    public string Name { get; }

    /// <summary>Every placement that resolved to a mesh, in stream order.</summary>
    public IReadOnlyList<SceneInstance> Statics { get; }

    /// <summary>How many placements were dropped for want of a mesh.</summary>
    public int SkippedPlacements { get; }

    /// <summary>The theater's authored X extent, from its <c>world_extents</c> directive.</summary>
    public int WorldExtentX { get; }

    /// <summary>The theater's authored Z extent.</summary>
    public int WorldExtentZ { get; }

    /// <summary>
    /// Builds a scene from an explicit instance list — for a test, an editor preview, or a theatre
    /// the port assembles itself.
    /// </summary>
    /// <param name="name">What to call it.</param>
    /// <param name="instances">The instances.</param>
    public static WorldScene FromInstances(string name, IEnumerable<SceneInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(instances);
        return new WorldScene(name, [.. instances], 0, 0, 0);
    }

    /// <summary>Builds the static scene of a theater catalog.</summary>
    /// <param name="theater">A <c>.W</c> container (<c>DataTree.Theaters["GERMANY.W"]</c>).</param>
    /// <param name="meshes">The mesh library the class ids resolve through.</param>
    /// <exception cref="ArgumentException"><paramref name="theater"/> is not a theater catalog.</exception>
    public static WorldScene FromTheater(MissionDefinition theater, MeshLibrary meshes)
    {
        ArgumentNullException.ThrowIfNull(theater);
        ArgumentNullException.ThrowIfNull(meshes);
        if (!theater.IsTheater)
        {
            throw new ArgumentException(
                $"{theater.AssetName} is a mission, not a theater catalog", nameof(theater));
        }

        List<SceneInstance> statics = new List<SceneInstance>(theater.Objects.Count);
        int skipped = 0;
        foreach (MissionObject placed in theater.Objects)
        {
            if (placed.Placement.Resolved is not { } position || !position.IsWorldAbsolute)
            {
                skipped++;
                continue;
            }

            MeshModel? mesh = meshes.ForClass(placed.ClassId);
            if (mesh is null)
            {
                skipped++;
                continue;
            }

            statics.Add(new SceneInstance(
                mesh,
                position.X,
                position.Y,
                position.Z,
                placed.HeadingUnits is int units ? units / 8.0 : 0.0));
        }

        IReadOnlyList<int>? extents = theater.Directives
            .FirstOrDefault(d => string.Equals(d.Name, "world_extents", StringComparison.Ordinal))?.Coords;
        return new WorldScene(
            theater.AssetName,
            [.. statics],
            skipped,
            extents is { Count: > 1 } ? extents[1] : 0,
            extents is { Count: > 3 } ? extents[3] : 0);
    }
}

/// <summary>
/// What the renderer draws for one frame: the theater's static scenery plus whatever the simulation
/// put in the world this frame.
/// </summary>
/// <remarks>
/// The sim side WRITES this (through <see cref="BeginFrame"/> / <see cref="Add"/>) and the renderer
/// only READS it, "a renderer must never write back into sim state".  The dynamic list is reused
/// between frames so a steady-state frame allocates nothing.
/// </remarks>
public sealed class SceneSnapshot
{
    private readonly List<SceneInstance> _dynamic = [];

    /// <summary>Creates a snapshot over a theater's static scene.</summary>
    /// <param name="world">The static scene.</param>
    public SceneSnapshot(WorldScene world)
    {
        ArgumentNullException.ThrowIfNull(world);
        World = world;
    }

    /// <summary>The static scenery.</summary>
    public WorldScene World { get; }

    /// <summary>This frame's moving objects — the player's aircraft, and later everything else.</summary>
    public IReadOnlyList<SceneInstance> Dynamic => _dynamic;

    /// <summary>Clears the dynamic list for a new frame.</summary>
    public void BeginFrame() => _dynamic.Clear();

    /// <summary>Adds one dynamic instance to this frame.</summary>
    /// <param name="instance">The instance.</param>
    public void Add(in SceneInstance instance) => _dynamic.Add(instance);
}
