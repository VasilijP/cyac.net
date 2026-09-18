namespace CYAC.Port.Render;

/// <summary>
/// Which camera the frame is drawn from — the ORIGINAL's own view-ids.
/// </summary>
/// <remarks>
/// <para>
/// The numbering is the scancode→view-id dispatch's, byte-derived at <c>image@0x33256..0x332CA</c>:
/// the six cockpit views are 0..5, the six external ones 6..0xB, and the "other" six are scattered
/// through 0xC..0x13. <c>view_mode_setter @image@0x32690</c> indexes the per-view flag table
/// <c>[0x2BC0..0x2BD3]</c> with exactly these ids, and <c>publish_view_mode @image@0x2376A</c>
/// subtracts 6 before its 14-entry jump table — which is why 0..5 have no table entry.
/// </para>
/// <para>
/// Seventeen of them; the F9 MAP (id 0xE) is out of scope, and id 0xC is the open pair
/// with 0x11 — this port follows §4.3's flag-table reading and treats 0xC as Shift-F8 "External
/// Target". <b>Byte-proven end to end; four ids move from the earlier reading.</b>
/// Walking the scancode dispatch's own binary search rather than the §4.1 table: <c>0x4300</c> (F9)
/// falls through <c>image@0x333AA</c>'s <c>sub ax,0x4200 / dec / dec / sub ax,0xFE</c> chain to the
/// thunk at <c>image@0x33288</c>, which is <c>mov ax,0x0C</c> — so the <b>MAP is view id 0x0C</b>,
/// which is also the id <c>mission_per_frame_render_phase</c> branches on to draw it (<c>cmp
/// [0xC320],0x0C</c> @<c>image@0x01570</c>), and the id whose per-view flag byte <c>[0x2BC0 +
/// 0x0C]</c> is the only <c>0x00</c> of the twenty (a screen, not a camera).  The same walk gives
/// Shift-F9 = <c>0x0E</c>, Shift-F10 = <c>0x0F</c> and Shift-F8 = <c>0x13</c>.
/// </para>
/// </remarks>
public enum ViewMode
{
    /// <summary>F1 — looking forward from the cockpit; the only view that can aim weapons.</summary>
    CockpitForward = 0,

    /// <summary>F2 — over the tail.</summary>
    CockpitBack = 1,

    /// <summary>F3 — over the left wing.</summary>
    CockpitLeft = 2,

    /// <summary>F4 — over the right wing.</summary>
    CockpitRight = 3,

    /// <summary>F5 — 45° up from forward.</summary>
    CockpitUp = 4,

    /// <summary>F6 — 45° down from forward.</summary>
    CockpitDown = 5,

    /// <summary>Shift-F1 — behind the aeroplane, looking forward.  H1..H7's "chase".</summary>
    ExternalForward = 6,

    /// <summary>Shift-F1, the name the PoC shipped for it before H8.</summary>
    ExternalChase = ExternalForward,

    /// <summary>Shift-F2 — in front, looking back.</summary>
    ExternalBack = 7,

    /// <summary>Shift-F3 — off the right wing.</summary>
    ExternalRight = 8,

    /// <summary>Shift-F4 — off the left wing.</summary>
    ExternalLeft = 9,

    /// <summary>Shift-F5 — from below, looking up.</summary>
    ExternalBelow = 0xA,

    /// <summary>Shift-F6 — from above, looking down.</summary>
    ExternalAbove = 0xB,

    /// <summary>
    /// The top-down MAP.  Not a camera at all: a separate 2-D screen
    /// (<see cref="Map.MapView"/>), which is why its per-view flag byte is <c>0x00</c>.
    /// The scancode dispatch sends <c>0x4300</c> to <c>mov ax,0x0C</c>
    /// @<c>image@0x33288</c>.
    /// </summary>
    Map = 0xC,

    /// <summary>F10 — a fixed point the aeroplane flies past.</summary>
    FlyBy = 0xD,

    /// <summary>
    /// Shift-F9 — circling the aeroplane.  <c>0x4302</c> → <c>mov ax,0x0E</c>
    /// @<c>image@0x332C4</c>.
    /// </summary>
    Circling = 0xE,

    /// <summary>
    /// Shift-F10 — from behind an in-flight missile.  <c>0x4402</c> → <c>mov ax,0x0F</c>
    /// @<c>image@0x332CA</c>.
    /// </summary>
    Missile = 0xF,

    /// <summary>F7 — the player in the foreground over the target (needs a lock).</summary>
    PlaneToTarget = 0x10,

    /// <summary>F8 — the target in the foreground over the player (needs a lock).</summary>
    TargetToPlane = 0x11,

    /// <summary>Shift-F7 — inside the target's cockpit (needs a lock).</summary>
    TargetCockpit = 0x12,

    /// <summary>
    /// Shift-F8 — behind the TARGET (needs a lock).  <c>0x4202</c> → <c>mov ax,0x13</c>
    /// @<c>image@0x332BE</c>.
    /// </summary>
    ExternalTarget = 0x13,
}

/// <summary>
/// Where the camera is and which way it looks: an eye point in WORLD units and an orientation given
/// as the same heading / pitch / roll triple the world object carries.
/// </summary>
/// <param name="EyeX">Eye X, world units.</param>
/// <param name="EyeY">Eye Y (up), world units.</param>
/// <param name="EyeZ">Eye Z, world units.</param>
/// <param name="HeadingRadians">Heading; 0 looks at +Z and the angle turns toward −X.</param>
/// <param name="PitchRadians">Pitch; positive looks up.</param>
/// <param name="RollRadians">Roll; positive puts the right of the frame down.</param>
/// <remarks>
/// <para>
/// <b>The basis is the SIMULATION's, not a graphics convention.</b> It is read straight out of the
/// verified flight kernel: <c>BodyVelocityProjection.RotationFromEuler</c> (the ported
/// <c>gfx_rot_mat3_from_euler @image@0x14D9C</c>) composes <c>M = R_z·(R_y·R_x)</c> and
/// <c>ApplyVelocityStage</c> phase 9b integrates <c>pos += M·v</c>, so a pure forward body velocity
/// moves the aircraft along
/// </para>
/// <code>
/// forward(h, p) = ( −sin h · cos p ,  sin p ,  cos h · cos p )
/// </code>
/// <para>
/// — heading 0 at +Z turning toward −X, +Y up, nose-up pitch positive.  The same expansion gives the
/// body's right and up axes, which is what <see cref="Right"/> and <see cref="Up"/> are.  The
/// scenery layer agrees independently: <c>angle_mat3_pair_build @image@0x1BFA4</c> NEGATES a heading
/// before its trig lookups (<c>CYAC.Formats/Mesh/SceneryFootprint.cs</c> <c>HeadingConvention</c>,
/// With the negation the 507 river instances trace smooth continuous water
/// courses"), and so does the spherical accumulator <c>angle_distance_xyz_accum @image@0x2084A</c>
/// (<c>out[X] += −d·cos e·sin h</c>, <c>out[Y] += +d·sin e</c>, <c>out[Z] += +d·cos e·cos h</c>).
/// </para>
/// <para>
/// <see cref="Right"/> × <see cref="Up"/> = <see cref="Forward"/>, so view space is
/// (X right, Y up, Z forward) — the frame <see cref="HorizonRenderer"/> already works in.
/// </para>
/// </remarks>
public readonly record struct CameraPose(
    double EyeX,
    double EyeY,
    double EyeZ,
    double HeadingRadians,
    double PitchRadians,
    double RollRadians)
{
    /// <summary>The camera's orthonormal frame in world coordinates.</summary>
    public Basis3 Basis => Basis3.FromEuler(HeadingRadians, PitchRadians, RollRadians);

    /// <summary>The world direction the camera looks along.</summary>
    public Vec3 Forward => Basis.Forward;

    /// <summary>The world direction of the camera frame's +X (its right).</summary>
    public Vec3 Right => Basis.Right;

    /// <summary>The world direction of the camera frame's +Y (its up).</summary>
    public Vec3 Up => Basis.Up;

    /// <summary>The eye point.</summary>
    public Vec3 Eye => new(EyeX, EyeY, EyeZ);
}

/// <summary>
/// Builds the frame's <see cref="CameraPose"/> — the port's read of the original's view placement.
/// </summary>
/// <remarks>
/// The original keeps the live camera in <c>s_view_anchor [0xD88E]</c> (<c>{i32 X, i32 Y, i32 Z, u16
/// heading/pitch/roll}</c>, KNOWN_FIELDS</c>) and rebuilds its rotation matrix every frame
/// from the anchor's angles ("There is no camera orientation matrix stored anywhere persistent").  This
/// type produces the same two placements in <c>double</c>.
/// </remarks>
public static class CameraRig
{
    /// <summary>
    /// The floor the original clamps the view anchor's Y to: <c>0x500</c> in <c>world &lt;&lt; 8</c>
    /// units, i.e. 5 world units (<c>cmp [0xD892],0x500</c> / <c>mov [0xD892],0x500</c>
    /// @<c>image@0x22D80</c>/<c>0x22D88</c>).
    /// </summary>
    public const double AnchorFloorWorldUnits = 0x500 / 256.0;

    /// <summary>Half a turn in degrees — the <c>0x5A0</c> the chase view adds to the heading.</summary>
    public const double HalfTurnDegrees = 180.0;

    /// <summary>
    /// The chase camera's distance in world units, <b>(open)</b>.
    /// </summary>
    /// <remarks>
    /// <c>view_anchor_pos_and_angles_apply @image@0x22D72</c> passes
    /// <c>dist = (i32)[bp+8] &lt;&lt; 8</c> to <c>angle_distance_xyz_accum</c>, i.e. zoom × 256 in
    /// <c>world &lt;&lt; 8</c> units — so the distance in WORLD units is simply the zoom level, and
    /// the zoom is a runtime value no byte in the image pins (the Review-Film screen shows
    /// "Zoom = 2" / "Zoom = 4", a captured frame of the original).  The PoC picks a
    /// distance that frames a fighter and exposes it on the command line.
    /// </remarks>
    public const double DefaultChaseDistanceWorldUnits = 260.0;

    /// <summary>
    /// The chase camera's elevation above the target, in degrees, <b>(open)</b>.
    /// </summary>
    /// <remarks>
    /// The recipe is fixed and the number is not: the anchor's elevation argument is <c>wrap([bp+4] −
    /// [0xB984])</c> and its heading <c>wrap([bp+6] + [0xB982] + 0x5A0)</c> (@<c>0x2084A</c> site 7),
    /// both runtime words.  A positive elevation puts the camera ABOVE the target (<c>out[Y] += d·sin
    /// e</c>), which is where the original's own external chase view puts it.
    /// </remarks>
    public const double DefaultChaseElevationDegrees = 8.0;

    /// <summary>The F1 forward view: the eye is the aircraft, the orientation is the aircraft's.</summary>
    /// <param name="view">The frame's simulation view.</param>
    /// <remarks>
    /// The anchor's Y is clamped to <see cref="AnchorFloorWorldUnits"/> exactly as the original does,
    /// which is what stops the eye sinking under the ground plane while the aircraft is parked on its
    /// wheels.
    /// </remarks>
    public static CameraPose Cockpit(in FlightSnapshot view) => new(
        view.X / 256.0,
        Math.Max(view.Y / 256.0, AnchorFloorWorldUnits),
        view.Z / 256.0,
        view.HeadingDegrees * Math.PI / 180.0,
        view.PitchRadians,
        view.RollRadians);

    /// <summary>
    /// The Shift-F1 external chase: behind the target along its own heading, raised by an elevation,
    /// looking back at it.
    /// </summary>
    /// <param name="view">The frame's simulation view.</param>
    /// <param name="distanceWorldUnits">How far behind, in world units.</param>
    /// <param name="elevationDegrees">How far above, in degrees of elevation.</param>
    /// <remarks>
    /// <para>
    /// The placement is <c>view_anchor_pos_and_angles_apply @image@0x22D72</c>'s: it pre-fills the
    /// anchor with the target's XYZ and then ACCUMULATES a spherical displacement through
    /// <c>angle_distance_xyz_accum @image@0x2084A</c> at <c>heading + [0xB982] + 0x5A0</c> — half a
    /// turn, i.e. BEHIND — and elevation <c>−[0xB984]</c>, with <c>dist = zoom &lt;&lt; 8</c>.  The
    /// accumulator's own (corrected) formula is <c>out[X] += −d·cos e·sin h</c>, <c>out[Y] += +d·sin
    /// e</c>, <c>out[Z] += +d·cos e·cos h</c>.
    /// </para>
    /// <para>
    /// The camera's ORIENTATION is the port's choice: the original re-uses the anchor's own angle
    /// words, which are runtime state; aiming at the target is what makes the aircraft visible, and
    /// it is what the original's chase view shows.  Roll is 0 — an external camera does not bank.
    /// </para>
    /// </remarks>
    public static CameraPose Chase(
        in FlightSnapshot view,
        double distanceWorldUnits = DefaultChaseDistanceWorldUnits,
        double elevationDegrees = DefaultChaseElevationDegrees)
    {
        double targetX = view.X / 256.0;
        double targetY = view.Y / 256.0;
        double targetZ = view.Z / 256.0;

        double behind = (view.HeadingDegrees + HalfTurnDegrees) * Math.PI / 180.0;
        double elevation = elevationDegrees * Math.PI / 180.0;
        double horizontal = distanceWorldUnits * Math.Cos(elevation);

        double eyeX = targetX - (Math.Sin(behind) * horizontal);
        double eyeY = Math.Max(targetY + (distanceWorldUnits * Math.Sin(elevation)), AnchorFloorWorldUnits);
        double eyeZ = targetZ + (Math.Cos(behind) * horizontal);

        // Aim back at the target from wherever the anchor floor left the eye — so the aircraft stays
        // framed even when the clamp fires (a target parked on its wheels).
        return LookAt(eyeX, eyeY, eyeZ, targetX, targetY, targetZ);
    }

    /// <summary>A pose at an eye point, aimed at a target point, with no bank.</summary>
    /// <param name="eyeX">Eye X, world units.</param>
    /// <param name="eyeY">Eye Y, world units.</param>
    /// <param name="eyeZ">Eye Z, world units.</param>
    /// <param name="targetX">Target X, world units.</param>
    /// <param name="targetY">Target Y, world units.</param>
    /// <param name="targetZ">Target Z, world units.</param>
    /// <remarks>
    /// The inverse of <see cref="CameraPose.Forward"/>: heading is <c>atan2(−Δx, Δz)</c> (the engine's
    /// convention, <c>HeadingConvention.DegreesFromDirection</c>) and pitch is
    /// <c>atan2(Δy, |Δ_horizontal|)</c>.
    /// </remarks>
    public static CameraPose LookAt(
        double eyeX, double eyeY, double eyeZ, double targetX, double targetY, double targetZ)
    {
        double dx = targetX - eyeX;
        double dy = targetY - eyeY;
        double dz = targetZ - eyeZ;
        double horizontal = Math.Sqrt((dx * dx) + (dz * dz));
        double heading = horizontal > 0 ? Math.Atan2(-dx, dz) : 0.0;
        double pitch = Math.Atan2(dy, horizontal);
        return new CameraPose(eyeX, eyeY, eyeZ, heading, pitch, 0.0);
    }

    /// <summary>A pose at an eye point, aimed at a target point, with no bank.</summary>
    /// <param name="eye">The eye point.</param>
    /// <param name="target">What to look at.</param>
    public static CameraPose LookAt(Vec3 eye, Vec3 target) =>
        LookAt(eye.X, eye.Y, eye.Z, target.X, target.Y, target.Z);

    /// <summary>
    /// The euler triple of an orthonormal camera frame — the inverse of
    /// <see cref="Basis3.FromEuler"/>.
    /// </summary>
    /// <param name="eye">The eye point.</param>
    /// <param name="basis">The camera's frame.</param>
    /// <remarks>
    /// <c>pitch = asin(F_y)</c> and <c>heading = atan2(−F_x, F_z)</c> invert the forward row;
    /// the roll then falls out of <c>Up = cos r · U₀ + sin r · R₀</c>, where <c>U₀</c> and
    /// <c>R₀</c> are the unrolled frame's up and right — which is exactly how
    /// <see cref="Basis3.FromEuler"/> builds them.
    /// </remarks>
    public static CameraPose FromBasis(Vec3 eye, Basis3 basis)
    {
        double pitch = Math.Asin(Math.Clamp(basis.Forward.Y, -1.0, 1.0));
        double heading = Math.Atan2(-basis.Forward.X, basis.Forward.Z);
        Basis3 unrolled = Basis3.FromEuler(heading, pitch, 0.0);
        double roll = Math.Atan2(basis.Up.Dot(unrolled.Right), basis.Up.Dot(unrolled.Up));
        return new CameraPose(eye.X, eye.Y, eye.Z, heading, pitch, roll);
    }
}

/// <summary>
/// What a view can be anchored to: a world pose the renderer is HANDED.
/// </summary>
/// <param name="X">World X, world units.</param>
/// <param name="Y">World Y (up), world units.</param>
/// <param name="Z">World Z, world units.</param>
/// <param name="HeadingDegrees">Heading in degrees; 0 = +Z, turning toward −X.</param>
/// <param name="PitchRadians">Pitch; positive nose-up.</param>
/// <param name="RollRadians">Roll; positive right-wing-down.</param>
/// <remarks>
/// The renderer never reaches into the simulation for a target or a missile: the host resolves
/// <c>g_lockon_target [0x00BC]</c> and the live projectile list and passes their poses in.
/// </remarks>
public readonly record struct CameraSubject(
    double X,
    double Y,
    double Z,
    double HeadingDegrees,
    double PitchRadians,
    double RollRadians)
{
    /// <summary>The player's own pose, out of the frame's flight snapshot.</summary>
    /// <param name="view">The snapshot.</param>
    public static CameraSubject FromSnapshot(in FlightSnapshot view) => new(
        view.X / 256.0,
        view.Y / 256.0,
        view.Z / 256.0,
        view.HeadingDegrees,
        view.PitchRadians,
        view.RollRadians);

    /// <summary>The pose as a world point.</summary>
    public Vec3 Position => new(X, Y, Z);

    /// <summary>The pose's body frame.</summary>
    public Basis3 Basis =>
        Basis3.FromEuler(HeadingDegrees * Math.PI / 180.0, PitchRadians, RollRadians);
}

/// <summary>
/// The ORIGINAL's eighteen view keys, as camera placements.
/// </summary>
/// <remarks>
/// <para>
/// §5.4 records what a per-view setup actually DOES in the original — commit the id, cache the flag
/// byte, optionally re-point the camera ANCHOR <c>[0xC38A]</c> at another object, and zero the
/// scan-key deltas.  <b>The placements themselves are not in the image</b>:
/// <c>view_anchor_pos_and_angles_apply @image@0x22D72</c> builds them from runtime words — the zoom
/// <c>[0xD8A0]</c> and the two scan-key offsets <c>[0xB982]</c>/<c>[0xB984]</c> — so every distance
/// and elevation below is the port's own choice and is marked <c>(open)</c>, exactly as H3 marked
/// the chase view's two numbers.  What IS byte-derived is the SHAPE: which object each view is
/// anchored to, and the <c>+0x5A0</c> half-turn that puts an external camera behind its subject.
/// </para>
/// <para>
/// This type is stateful because two of the views are: the fly-by latches its ground point when the
/// view is entered (the original's <c>[0xC38A]</c> re-point, §5.3) and the circling view advances a
/// bearing with time.  The host owns one instance; nothing here touches the simulation.
/// </para>
/// </remarks>
public sealed class ViewCamera
{
    /// <summary>How far an external camera sits from its subject, in world units.  <b>(open)</b></summary>
    public double DistanceWorldUnits { get; init; } = CameraRig.DefaultChaseDistanceWorldUnits;

    /// <summary>How far above its subject, in degrees.  <b>(open)</b></summary>
    public double ElevationDegrees { get; init; } = CameraRig.DefaultChaseElevationDegrees;

    /// <summary>The elevation Shift-F5 / Shift-F6 use, in degrees.  <b>(open)</b></summary>
    /// <remarks>
    /// Not ±90: straight under or over the subject the look-at direction is vertical and the
    /// horizon's heading becomes undefined, which reads as a spin.  60° is well clear of that and
    /// still unmistakably "from below" / "from above".
    /// </remarks>
    public const double SteepElevationDegrees = 60.0;

    /// <summary>How long the circling view takes to go round once, in seconds.  <b>(open)</b></summary>
    public const double CirclingPeriodSeconds = 12.0;

    /// <summary>The circling camera's elevation, in degrees.  <b>(open)</b></summary>
    public const double CirclingElevationDegrees = 12.0;

    /// <summary>
    /// How far AHEAD of the aeroplane the fly-by point is latched, in world units, and how far to
    /// one side.  <b>(open)</b>
    /// </summary>
    public const double FlyByAheadWorldUnits = 1400.0;

    /// <summary>The fly-by point's lateral offset.  <b>(open)</b></summary>
    public const double FlyBySideWorldUnits = 260.0;

    /// <summary>
    /// How far BELOW the aeroplane the fly-by point is latched, in world units.  <b>(open)</b>
    /// </summary>
    /// <remarks>
    /// Measured: at the 8,719 ft the mission starts at, a ground camera looks almost straight up and
    /// the aeroplane is a pixel — the frame is all sky. The point is latched just below the
    /// aeroplane's own track instead, floored at the anchor clamp, so the pass reads as a pass at any
    /// altitude.
    /// </remarks>
    public const double FlyByBelowWorldUnits = 120.0;

    /// <summary>
    /// How many <see cref="FlyByAheadWorldUnits"/> the aeroplane may get from the latched point
    /// before a new one is latched ahead of it.  <b>(open)</b>
    /// </summary>
    /// <remarks>
    /// The original re-points its anchor when the view is ENTERED and the player then flies past
    /// and away; a modern host that leaves a view selected wants the next fly-by rather than a dot
    /// on the horizon, so the pass repeats.
    /// </remarks>
    public const double FlyByRelatchFactor = 1.5;

    /// <summary>The view this rig last built, so the fly-by knows when it is (re-)entered.</summary>
    public ViewMode LastView { get; private set; } = ViewMode.CockpitForward;

    /// <summary>The latched fly-by point, or null before the view has been entered.</summary>
    public Vec3? FlyByAnchor { get; private set; }

    /// <summary>
    /// True when the LAST call fell back because the view's anchor object was missing — no lock for
    /// a target view, no live projectile for the missile view.
    /// </summary>
    public bool FellBack { get; private set; }

    /// <summary>Builds the frame's camera.</summary>
    /// <param name="view">The view mode.</param>
    /// <param name="player">The player's pose.</param>
    /// <param name="target">The locked target's pose, when there is one.</param>
    /// <param name="missile">The newest live projectile's pose, when there is one.</param>
    /// <param name="seconds">Simulated seconds, for the circling view's bearing.</param>
    /// <returns>The camera pose.</returns>
    public CameraPose Build(
        ViewMode view,
        in CameraSubject player,
        CameraSubject? target,
        CameraSubject? missile,
        double seconds)
    {
        FellBack = false;
        bool entered = view != LastView;
        LastView = view;
        bool stale = FlyByAnchor is { } latched
            && (player.Position - latched).Length > FlyByAheadWorldUnits * FlyByRelatchFactor;
        if (view == ViewMode.FlyBy && (entered || stale || FlyByAnchor is null))
        {
            // §5.3's "re-point the anchor when the view is entered", as a ground point ahead of the
            // aeroplane's current track.
            Basis3 basis = player.Basis;
            Vec3 ahead = player.Position
                         + (basis.Forward * FlyByAheadWorldUnits)
                         + (basis.Right * FlyBySideWorldUnits);
            FlyByAnchor = new Vec3(
                ahead.X,
                Math.Max(player.Y - FlyByBelowWorldUnits, CameraRig.AnchorFloorWorldUnits),
                ahead.Z);
        }

        switch (view)
        {
            case ViewMode.CockpitForward:
                return Cockpit(player, 0.0, 0.0);
            case ViewMode.CockpitBack:
                return Cockpit(player, 180.0, 0.0);
            case ViewMode.CockpitLeft:
                return Cockpit(player, 90.0, 0.0);
            case ViewMode.CockpitRight:
                return Cockpit(player, -90.0, 0.0);
            case ViewMode.CockpitUp:
                return Cockpit(player, 0.0, 45.0);
            case ViewMode.CockpitDown:
                return Cockpit(player, 0.0, -45.0);

            case ViewMode.ExternalForward:
                return External(player, CameraRig.HalfTurnDegrees, ElevationDegrees);
            case ViewMode.ExternalBack:
                return External(player, 0.0, ElevationDegrees);
            case ViewMode.ExternalRight:
                return External(player, -90.0, ElevationDegrees);
            case ViewMode.ExternalLeft:
                return External(player, 90.0, ElevationDegrees);
            case ViewMode.ExternalBelow:
                return External(player, CameraRig.HalfTurnDegrees, -SteepElevationDegrees);
            case ViewMode.ExternalAbove:
                return External(player, CameraRig.HalfTurnDegrees, SteepElevationDegrees);

            case ViewMode.ExternalTarget:
                return target is { } t1
                    ? External(t1, CameraRig.HalfTurnDegrees, ElevationDegrees)
                    : Fallback(player);
            case ViewMode.TargetCockpit:
                return target is { } t2 ? Cockpit(t2, 0.0, 0.0) : Fallback(player);
            case ViewMode.PlaneToTarget:
                // The player in the FOREGROUND, the target beyond him: stand behind the player and
                // look past him at the target.
                return target is { } t3
                    ? CameraRig.LookAt(
                        Behind(player, CameraRig.HalfTurnDegrees, ElevationDegrees),
                        t3.Position)
                    : Fallback(player);
            case ViewMode.TargetToPlane:
                // §4.3 bit 7 — "target as camera origin": stand behind the TARGET, look at the player.
                return target is { } t4
                    ? CameraRig.LookAt(
                        Behind(t4, CameraRig.HalfTurnDegrees, ElevationDegrees),
                        player.Position)
                    : Fallback(player);

            case ViewMode.Circling:
            {
                double bearing = seconds / CirclingPeriodSeconds * 360.0;
                return CameraRig.LookAt(
                    Orbit(player.Position, bearing, CirclingElevationDegrees, DistanceWorldUnits),
                    player.Position);
            }

            case ViewMode.FlyBy:
                return CameraRig.LookAt(FlyByAnchor!.Value, player.Position);

            case ViewMode.Missile:
                return missile is { } m
                    ? External(m, CameraRig.HalfTurnDegrees, ElevationDegrees / 2.0)
                    : Fallback(player);

            case ViewMode.Map:
            default:
                return Fallback(player);
        }
    }

    /// <summary>The default when a view has no anchor: the external forward view of the player.</summary>
    private CameraPose Fallback(in CameraSubject player)
    {
        FellBack = true;
        return External(player, CameraRig.HalfTurnDegrees, ElevationDegrees);
    }

    /// <summary>
    /// A cockpit view: the eye is the subject, the orientation is the subject's body frame turned by
    /// a body-frame yaw and pitch.
    /// </summary>
    /// <param name="subject">Whose cockpit.</param>
    /// <param name="yawDegrees">Body yaw; positive turns toward the subject's LEFT (−X).</param>
    /// <param name="pitchDegrees">Body pitch; positive looks up.</param>
    /// <remarks>
    /// Composing in the BODY frame rather than adding to the euler triple is what makes F3 "over the
    /// left wing" mean the left wing at 60° of bank too.  The anchor floor
    /// (<see cref="CameraRig.AnchorFloorWorldUnits"/>) still applies, as it does in the original
    /// (<c>cmp [0xD892],0x500</c> @<c>image@0x22D80</c>).
    /// </remarks>
    private static CameraPose Cockpit(in CameraSubject subject, double yawDegrees, double pitchDegrees)
    {
        Basis3 body = subject.Basis;
        Basis3 offset = Basis3.FromEuler(
            yawDegrees * Math.PI / 180.0, pitchDegrees * Math.PI / 180.0, 0.0);
        Basis3 basis = new Basis3(
            Right: body.ToWorld(offset.Right),
            Up: body.ToWorld(offset.Up),
            Forward: body.ToWorld(offset.Forward));
        return CameraRig.FromBasis(
            new Vec3(subject.X, Math.Max(subject.Y, CameraRig.AnchorFloorWorldUnits), subject.Z),
            basis);
    }

    /// <summary>An external view: stand off the subject on a relative bearing and look back at it.</summary>
    private CameraPose External(in CameraSubject subject, double bearingDegrees, double elevationDegrees)
        => CameraRig.LookAt(Behind(subject, bearingDegrees, elevationDegrees), subject.Position);

    /// <summary>The eye point for an external view.</summary>
    private Vec3 Behind(in CameraSubject subject, double bearingDegrees, double elevationDegrees) =>
        Orbit(
            subject.Position,
            subject.HeadingDegrees + bearingDegrees,
            elevationDegrees,
            DistanceWorldUnits);

    /// <summary>
    /// <c>angle_distance_xyz_accum @image@0x2084A</c>'s own displacement, in <c>double</c>:
    /// <c>X += −d·cos e·sin h</c>, <c>Y += +d·sin e</c>, <c>Z += +d·cos e·cos h</c>.
    /// </summary>
    private static Vec3 Orbit(Vec3 centre, double bearingDegrees, double elevationDegrees, double distance)
    {
        double h = bearingDegrees * Math.PI / 180.0;
        double e = elevationDegrees * Math.PI / 180.0;
        double horizontal = distance * Math.Cos(e);
        return new Vec3(
            centre.X - (Math.Sin(h) * horizontal),
            Math.Max(centre.Y + (distance * Math.Sin(e)), CameraRig.AnchorFloorWorldUnits),
            centre.Z + (Math.Cos(h) * horizontal));
    }
}
