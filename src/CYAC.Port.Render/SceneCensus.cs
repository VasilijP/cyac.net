using System.Globalization;
using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Render;

/// <summary>Why an instance near the camera did not reach the rasteriser.</summary>
/// <remarks>
/// The order is the order <see cref="SceneRenderer"/> applies the tests in, so the FIRST reason an
/// instance can be given is the one it is recorded under.
/// </remarks>
public enum NearRejectReason
{
    /// <summary>It was drawn — at least one of its records painted a pixel.</summary>
    Drawn = 0,

    /// <summary>Past <see cref="SceneRenderOptions.MaxDrawDistanceWorldUnits"/>.</summary>
    DrawDistance = 1,

    /// <summary>Past its class's own cull distance under <c>--classic-cull</c>.</summary>
    ClassCull = 2,

    /// <summary>Fully transparent (<see cref="SceneInstance.Opacity"/> ≤ 0).</summary>
    Transparent = 3,

    /// <summary>Its bounding sphere lay wholly outside the frustum.</summary>
    Frustum = 4,

    /// <summary>
    /// Every one of its vertices was behind the near plane — the <c>maxZ &lt; NearPlane</c>
    /// INSTANCE-level reject the H5b brief names as a suspect.
    /// </summary>
    BehindNearPlane = 5,

    /// <summary>It reached the rasteriser but no record painted a pixel (occluded, or off screen).</summary>
    NoPixels = 6,

    /// <summary>
    /// H8 addendum — the eye is INSIDE this (small) instance's bounding sphere, so every face of
    /// it would paint a near-clipped flat colour across the frame.
    /// </summary>
    EyeInside = 7,
}

/// <summary>
/// A per-frame diagnostic census of the instances CLOSE to the camera, and of what happened to each
/// of their shape records.
/// </summary>
/// <remarks>
/// <para>
/// Built to chase down parts of a base or a road disappearing as the camera flies over them, on
/// the rule that a defect is <b>reproduced with a census before anything is fixed</b>. It is
/// a pure OBSERVER: the renderer writes only into the object the host handed it, never into
/// simulation state, and a null census costs one null check per instance.
/// </para>
/// <para>
/// "Near" is a Manhattan distance from the eye to the instance's ORIGIN, which is the same measure
/// the original's own visibility test uses (<c>mesh_visibility_lod_select @image@0x16BE8</c>
/// compares a Manhattan distance's high word).
/// </para>
/// </remarks>
public sealed class SceneCensus
{
    private readonly Dictionary<string, int[]> _byClass = [];
    private readonly int[] _totals = new int[8];

    /// <summary>Creates a census over a radius.</summary>
    /// <param name="radiusWorldUnits">How near an instance's origin must be to be counted.</param>
    public SceneCensus(double radiusWorldUnits) => RadiusWorldUnits = radiusWorldUnits;

    /// <summary>The radius, in world units of Manhattan distance from the eye.</summary>
    public double RadiusWorldUnits { get; }

    /// <summary>How many near instances the frame looked at.</summary>
    public int NearInstances { get; private set; }

    /// <summary>How many near instances did NOT reach the rasteriser at all.</summary>
    /// <remarks>
    /// Everything but <see cref="NearRejectReason.Drawn"/> and <see cref="NearRejectReason.NoPixels"/>
    /// — i.e. the instance-level rejects, which are the ones that can make a whole road or a whole
    /// airfield vanish at once.
    /// </remarks>
    public int NearRejected =>
        _totals[(int)NearRejectReason.DrawDistance]
        + _totals[(int)NearRejectReason.ClassCull]
        + _totals[(int)NearRejectReason.Transparent]
        + _totals[(int)NearRejectReason.Frustum]
        + _totals[(int)NearRejectReason.EyeInside]
        + _totals[(int)NearRejectReason.BehindNearPlane];

    /// <summary>Shape records of near instances that the near-plane clip reduced to nothing.</summary>
    public int NearFacesClippedAway { get; private set; }

    /// <summary>Shape records of near instances whose supporting plane passed through the eye.</summary>
    public int NearFacesDegenerate { get; private set; }

    /// <summary>Shape records of near instances dropped for an out-of-range vertex index.</summary>
    public int NearFacesBadIndex { get; private set; }

    /// <summary>How many near instances got a given verdict.</summary>
    /// <param name="reason">The verdict.</param>
    public int Count(NearRejectReason reason) => _totals[(int)reason];

    /// <summary>How many frames the census has watched.</summary>
    public int Frames { get; private set; }

    /// <summary>
    /// Opens a new frame.  The counts ACCUMULATE over the whole run — a vanishing that happens on
    /// one frame of a fly-over must not be averaged away by the frames on either side of it.
    /// </summary>
    public void BeginFrame() => Frames++;

    /// <summary>Records one near instance's verdict.</summary>
    /// <param name="mesh">Its class.</param>
    /// <param name="reason">What happened to it.</param>
    internal void Note(MeshModel mesh, NearRejectReason reason)
    {
        NearInstances++;
        _totals[(int)reason]++;
        if (!_byClass.TryGetValue(mesh.Basename, out int[]? row))
        {
            row = new int[8];
            _byClass[mesh.Basename] = row;
        }

        row[(int)reason]++;
    }

    /// <summary>Records a near instance's record that the near clip removed entirely.</summary>
    internal void NoteFaceClippedAway() => NearFacesClippedAway++;

    /// <summary>Records a near instance's record whose plane contained the eye.</summary>
    internal void NoteFaceDegenerate() => NearFacesDegenerate++;

    /// <summary>Records a near instance's record with an out-of-range vertex index.</summary>
    internal void NoteFaceBadIndex() => NearFacesBadIndex++;

    /// <summary>One line per class that had a near instance REJECTED this frame.</summary>
    /// <remarks>Sorted by the number rejected, worst first — the vanishing suspects.</remarks>
    public IReadOnlyList<string> RejectedClassLines()
    {
        List<(int Rejected, string Line)> lines = new List<(int Rejected, string Line)>();
        foreach ((string name, int[] row) in _byClass)
        {
            int rejected = row[(int)NearRejectReason.DrawDistance]
                + row[(int)NearRejectReason.ClassCull]
                + row[(int)NearRejectReason.Transparent]
                + row[(int)NearRejectReason.Frustum]
                + row[(int)NearRejectReason.BehindNearPlane];
            if (rejected == 0)
            {
                continue;
            }

            lines.Add((rejected, string.Create(
                CultureInfo.InvariantCulture,
                $"{name,-10} rejected {rejected,4}  (frustum {row[(int)NearRejectReason.Frustum]}, "
                    + $"behind {row[(int)NearRejectReason.BehindNearPlane]}, "
                    + $"cull {row[(int)NearRejectReason.ClassCull]}, "
                    + $"dist {row[(int)NearRejectReason.DrawDistance]})  drawn "
                    + $"{row[(int)NearRejectReason.Drawn]}")));
        }

        lines.Sort((a, b) => b.Rejected.CompareTo(a.Rejected));
        return [.. lines.Select(entry => entry.Line)];
    }

    /// <summary>The one-line summary the host prints.</summary>
    public string Summary() => string.Create(
        CultureInfo.InvariantCulture,
        $"near({RadiusWorldUnits:N0}u) over {Frames:N0} frame(s): {NearInstances} instance-frame(s), "
            + $"{NearRejected} REJECTED "
            + $"[frustum {Count(NearRejectReason.Frustum)}, behind {Count(NearRejectReason.BehindNearPlane)}, "
            + $"cull {Count(NearRejectReason.ClassCull)}, dist {Count(NearRejectReason.DrawDistance)}, "
            + $"clear {Count(NearRejectReason.Transparent)}]; drawn {Count(NearRejectReason.Drawn)}, "
            + $"no-pixels {Count(NearRejectReason.NoPixels)}; near faces lost: "
            + $"clip {NearFacesClippedAway}, degenerate {NearFacesDegenerate}, bad-index {NearFacesBadIndex}");
}

/// <summary>
/// H8 addendum — the DEGENERATE-PROJECTION watch: which instance, and which of its shape records,
/// paints an absurdly large polygon.
/// </summary>
/// <remarks>
/// <para>
/// A convex polygon that straddles the eye is clipped to the near plane
/// (<c>SceneRenderer.NearPlaneWorldUnits</c>) and then projects to screen coordinates proportional to
/// <c>1 / z</c> — so one face of a small mesh sitting ON the camera fills the frame with a single
/// flat colour bounded by a straight clip edge.  That is geometrically correct and visually a bug:
/// strange polygons appearing on the player's own aeroplane are exactly this picture.
/// </para>
/// <para>
/// This observer records, per (class, record), the largest projected screen SPAN the frame produced
/// and where the offending instance was, so the culprit can be NAMED from the pixels backwards
/// instead of guessed at.  It writes nothing the renderer reads.
/// </para>
/// </remarks>
public sealed class ProjectionWatch
{
    /// <summary>One offender.</summary>
    /// <param name="Class">The mesh basename.</param>
    /// <param name="Record">The shape record's index in its LOD.</param>
    /// <param name="SpanPixels">The projected polygon's bounding-box diagonal, in pixels.</param>
    /// <param name="SpanScreens">…as a multiple of the target's own diagonal.</param>
    /// <param name="EyeDistance">Euclidean distance from the eye to the instance's origin.</param>
    /// <param name="NearestZ">The nearest view-space Z of the record's UNCLIPPED vertices.</param>
    /// <param name="HullRadius">The instance's scaled bounding radius.</param>
    /// <param name="EyeInsideBound">True when the eye lies inside that bounding sphere.</param>
    /// <param name="CoveredPixels">How many of the target's own pixels the projected bbox covers.</param>
    /// <param name="Frame">The host frame it happened on.</param>
    public readonly record struct Offender(
        string Class,
        int Record,
        double SpanPixels,
        double SpanScreens,
        double EyeDistance,
        double NearestZ,
        double HullRadius,
        bool EyeInsideBound,
        long CoveredPixels,
        long Frame);

    private readonly Dictionary<string, Offender> _worst = [];
    private readonly Dictionary<string, Offender> _tripping = [];
    private readonly Dictionary<string, long> _pixels = [];
    private readonly Dictionary<string, (string Class, int Record, long Pixels)> _worstRecord = [];

    /// <summary>The host's frame counter, stamped onto each record.</summary>
    public long Frame { get; set; }

    /// <summary>Frames before this one are ignored — for narrowing a census onto one event.</summary>
    public long FirstFrame { get; set; }

    /// <summary>How many polygons exceeded <see cref="TripwireScreens"/>.</summary>
    public long Tripped { get; private set; }

    /// <summary>Polygons looked at.</summary>
    public long Polygons { get; private set; }

    /// <summary>
    /// The tripwire: a polygon may not project to more than this multiple of the screen diagonal
    /// UNLESS the eye is inside the instance's own bounding sphere.
    /// </summary>
    /// <remarks>
    /// A face that legitimately fills the frame spans one diagonal, and half a diagonal of slack
    /// covers a face the eye is close to but outside.  The exemption is what lets a scenery
    /// footprint the camera is flying through — <c>road</c> (bounding radius 2,048 world units),
    /// <c>city</c> (19,429), <c>rural</c> (18,160) — keep painting the ground under the aeroplane,
    /// which is H5b's item 1 and must never regress; nothing the eye is OUTSIDE may blow up like
    /// that.  Tightened with the eye-inside exemption, H8 §11.8.
    /// </remarks>
    public const double TripwireScreens = 1.5;

    /// <summary>Records one projected polygon.</summary>
    /// <param name="basename">The instance's class.</param>
    /// <param name="record">The record's index.</param>
    /// <param name="spanPixels">Its projected bounding-box diagonal.</param>
    /// <param name="screenDiagonal">The target's diagonal.</param>
    /// <param name="eyeDistance">Euclidean distance from the eye to the instance's origin.</param>
    /// <param name="nearestZ">The nearest unclipped view-space Z of the record.</param>
    /// <param name="hullRadius">The instance's scaled bounding radius.</param>
    /// <param name="coveredPixels">How many of the target's pixels the projected bbox covers.</param>
    public void Note(
        string basename,
        int record,
        double spanPixels,
        double screenDiagonal,
        double eyeDistance,
        double nearestZ,
        double hullRadius,
        long coveredPixels)
    {
        if (Frame < FirstFrame)
        {
            return;
        }

        Polygons++;
        double screens = screenDiagonal > 0 ? spanPixels / screenDiagonal : 0.0;
        bool eyeInside = eyeDistance <= hullRadius;

        // The wire only counts a polygon that actually COVERS on-screen pixels.  A face far off to
        // one side can have a bbox thousands of screens wide and land entirely outside the viewport
        // — measured: the ejected canopy at frame 13,172 spans 5.7 screens and covers 0 pixels — and
        // a census that counts those is measuring arithmetic, not pictures.
        Offender offender = new Offender(
            basename, record, spanPixels, screens, eyeDistance, nearestZ, hullRadius,
            eyeInside, coveredPixels, Frame);
        if (screens > TripwireScreens && !eyeInside && coveredPixels > 0)
        {
            Tripped++;
            if (!_tripping.TryGetValue(basename, out Offender worstTrip)
                || coveredPixels > worstTrip.CoveredPixels)
            {
                _tripping[basename] = offender;
            }
        }

        if (!_worst.TryGetValue(basename, out Offender previous) || screens > previous.SpanScreens)
        {
            _worst[basename] = offender;
        }
    }

    /// <summary>
    /// PIXEL OWNERSHIP: how many pixels each class actually painted over the window.
    /// </summary>
    /// <param name="basename">The class.</param>
    /// <param name="record">The record index.</param>
    /// <param name="pixels">Pixels the raster call wrote.</param>
    public void NotePixels(string basename, int record, long pixels)
    {
        if (Frame < FirstFrame || pixels <= 0)
        {
            return;
        }

        _pixels[basename] = _pixels.GetValueOrDefault(basename) + pixels;
        string key = $"{basename}#{record}";
        (string Class, int Record, long Pixels) previous = _worstRecord.GetValueOrDefault(key);
        _worstRecord[key] = (basename, record, previous.Pixels + pixels);
    }

    /// <summary>The classes that painted the most pixels over the window, biggest first.</summary>
    /// <returns>Class and pixel count.</returns>
    public IEnumerable<(string Class, long Pixels)> ByPixels() =>
        _pixels.Select(p => (p.Key, p.Value)).OrderByDescending(p => p.Value);

    /// <summary>The individual RECORDS that painted the most pixels.</summary>
    /// <returns>Class, record and pixel count.</returns>
    public IEnumerable<(string Class, int Record, long Pixels)> RecordsByPixels() =>
        _worstRecord.Values.OrderByDescending(r => r.Pixels);

    /// <summary>The worst polygon each class produced, biggest first.</summary>
    /// <returns>The offenders.</returns>
    public IEnumerable<Offender> Worst() =>
        _worst.Values.OrderByDescending(o => o.SpanScreens);

    /// <summary>
    /// The worst TRIPPING polygon each class produced — over the wire, eye outside its bound, and
    /// actually covering on-screen pixels.  These are the pictures a human would complain about.
    /// </summary>
    /// <returns>The offenders, most on-screen coverage first.</returns>
    public IEnumerable<Offender> Tripping() =>
        _tripping.Values.OrderByDescending(o => o.CoveredPixels);

    /// <summary>The lines a headless run prints.</summary>
    /// <returns>One header line and one line per class over a tenth of the tripwire.</returns>
    public IEnumerable<string> Lines()
    {
        yield return string.Create(
            CultureInfo.InvariantCulture,
            $"PROJECTION {Polygons:N0} polygon(s), {Tripped:N0} over {TripwireScreens:F0}× the screen diagonal");
        foreach (Offender o in Tripping())
        {
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"   TRIP {o.Class,-9} record {o.Record,3}  {o.SpanScreens,9:F1}× screen  covers {o.CoveredPixels,9:N0} px  eye-dist {o.EyeDistance,9:F0}  hull r {o.HullRadius,8:F1}  nearest Z {o.NearestZ,10:F1}  frame {o.Frame:N0}");
        }

        foreach ((string klass, long pixels) in ByPixels().Take(8))
        {
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"   pixels {klass,-9} {pixels,12:N0}");
        }

        foreach ((string klass, int record, long pixels) in RecordsByPixels().Take(24))
        {
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"   pixels {klass,-9} record {record,3}  {pixels,12:N0}");
        }

        foreach (Offender o in Worst())
        {
            if (o.SpanScreens < TripwireScreens / 5.0)
            {
                continue;
            }

            string tripped = o.SpanScreens > TripwireScreens && !o.EyeInsideBound && o.CoveredPixels > 0
                ? "  TRIPPED"
                : o.EyeInsideBound ? "  (eye inside)" : string.Empty;
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"   {o.Class,-9} record {o.Record,3}  span {o.SpanPixels,12:F0} px = {o.SpanScreens,9:F1}× screen  covers {o.CoveredPixels,9:N0} px  eye-dist {o.EyeDistance,9:F0}  hull r {o.HullRadius,8:F1}  nearest Z {o.NearestZ,10:F1}  frame {o.Frame:N0}{tripped}");
        }
    }
}
