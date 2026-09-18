namespace CYAC.Port.Render;

/// <summary>
/// H19 / PRESENTATION-side smoothing of pool-object poses between simulation updates.
/// </summary>
/// <remarks>
/// <para>
/// The original's engagement pass is a DEADLINE QUEUE (<c>EngagementExpiryLoop</c>,
/// <c>image@0x07320</c>: a node runs only when <c>node[+0x0B] &lt;= g_master_frame_counter</c>, and
/// the counter is <c>frameTime &gt;&gt; 8</c>), so an enemy far from the player is integrated in
/// COARSE steps — it moves once every several frames by the whole accumulated distance.  At 320×200
/// with the original's draw distance that stepping was never on screen; the port draws the whole
/// theatre, so a bandit seen behind the tail "teleports".
/// </para>
/// <para>
/// MEASURED (<c>fly --headless --mission 0 --pursue --pose-census</c>, 7,000 frames): the four far
/// bandits (25,000–60,000 ft out) hold one position for <b>118–120 host frames (≈2.0 s of simulated
/// time)</b> and then jump <b>800–940 ft</b>; the intervals are IRREGULAR (10, 120, 118, 75, 118,
/// 120, 76, 15, 119 frames in one object's sequence).  Every near object moves every simulation step
/// (~7 ft a step at 51.2 Hz).
/// </para>
/// <para>
/// <b>The law.</b>  This type does not touch the simulation.  Per object it keeps the last pose the
/// sim published and the velocity between the last two DISTINCT poses, and draws the object
/// <b>dead-reckoned</b>: from the sim's pose along that velocity for as long as the observed interval
/// (capped at <see cref="ExtrapolationCap"/> intervals, then it holds).  When the next sim pose
/// lands, the difference between where the object was being drawn and the new pose is carried as a
/// RESIDUAL that decays to zero over <see cref="MinBlendSeconds"/>..<see cref="MaxBlendSeconds"/>, so
/// the drawn pose is continuous through every update whatever the interval.  Angles take the same
/// residual along the shorter arc; they are not extrapolated.
/// </para>
/// <para>
/// With the measured IRREGULAR intervals the glide was often only part-way when the next pose landed,
/// and the re-based glide then jumped the drawn object forward by the unfinished remainder: hundreds
/// of feet.  Dead reckoning with a blended residual has no such discontinuity, and no lag either.
/// </para>
/// <para>
/// An object whose pose changes EVERY step (a projectile, a bandit within its short deadline) is
/// drawn at the sim's pose exactly: extrapolation only starts when an interval longer than
/// <see cref="MinSmoothedIntervalSeconds"/> is observed, and a residual only exists after a coarse
/// update.  Rules that keep it honest: a class change (the ejecting pilot's <c>eject1 → eject4</c>
/// walk) or a jump larger than <see cref="MaxJumpWorldUnits"/> resets the history (a respawn is not
/// a flight); an interval longer than <see cref="MaxIntervalSeconds"/> is not extrapolated (the
/// object was parked or off-list).
/// </para>
/// <para>
/// <see cref="TryGetPresented"/> hands the SAME drawn pose to every other consumer — the in-world
/// designator labels, the F9 map, the radar / map-window contacts and the smoke layer's wreck
/// emitter — so nothing on screen is placed at the held sim pose while the mesh is drawn elsewhere.
/// </para>
/// </remarks>
public sealed class PoseSmoother
{
    /// <summary>Intervals at or below this are "every step": drawn live, never extrapolated.</summary>
    public const double MinSmoothedIntervalSeconds = 0.03;

    /// <summary>Intervals above this are not extrapolated (the object was parked or off-list).</summary>
    public const double MaxIntervalSeconds = 3.0;

    /// <summary>A jump larger than this between two poses resets the object's history.</summary>
    public const double MaxJumpWorldUnits = 20_000.0;

    /// <summary>
    /// How many intervals past the last update the extrapolation keeps flying, the interval being
    /// the longest coarse one the object has shown.
    /// </summary>
    public const double ExtrapolationCap = 1.5;

    /// <summary>The shortest time a residual takes to blend out.</summary>
    public const double MinBlendSeconds = 0.25;

    /// <summary>The longest — a residual from a 2-s hold is spread over this.</summary>
    public const double MaxBlendSeconds = 1.5;

    private readonly Dictionary<ushort, Track> _tracks = [];
    private readonly List<ushort> _stale = [];
    private long _frame;

    /// <summary>One object's pose as the renderer draws it.</summary>
    /// <param name="X">World X.</param>
    /// <param name="Y">World Y.</param>
    /// <param name="Z">World Z.</param>
    /// <param name="HeadingDegrees">Heading.</param>
    /// <param name="PitchDegrees">Pitch.</param>
    /// <param name="RollDegrees">Roll.</param>
    public readonly record struct Pose(
        double X, double Y, double Z, double HeadingDegrees, double PitchDegrees, double RollDegrees);

    /// <summary>How many objects are being tracked.</summary>
    public int Tracked => _tracks.Count;

    /// <summary>How many objects were drawn away from their sim pose on the last frame.</summary>
    public int SmoothedLastFrame { get; private set; }

    /// <summary>Diagnostics: pose updates seen with an interval of one step (≤ <see cref="MinSmoothedIntervalSeconds"/>).</summary>
    public long UpdatesEveryStep { get; private set; }

    /// <summary>Diagnostics: pose updates seen with a longer interval (the ones that are smoothed).</summary>
    public long UpdatesCoarse { get; private set; }

    /// <summary>Diagnostics: the longest update interval seen, seconds.</summary>
    public double LongestIntervalSeconds { get; private set; }

    /// <summary>
    /// M0/R8 — forgets every track: the next frame starts from scratch.
    /// </summary>
    /// <remarks>
    /// What a MISSION RESTART needs (<c>FlightRasterizer.Reopen</c>).  The tracks are keyed by POOL
    /// OBJECT REF and a fresh mission fills the same slots with different aeroplanes, so a surviving
    /// track would present the new object out of the old one's pose for a while — the
    /// <see cref="MaxJumpWorldUnits"/> guard does not catch a respawn a few hundred feet away, and
    /// the class-ref check only catches a re-classed slot.  The three LIFETIME diagnostics
    /// deliberately survive: they are what this smoother has SEEN over the run and the host prints
    /// them as run totals.
    /// </remarks>
    public void Reset()
    {
        _tracks.Clear();
        _stale.Clear();
        _frame = 0;
        SmoothedLastFrame = 0;
    }

    /// <summary>Starts a frame: everything not touched before <see cref="EndFrame"/> is forgotten.</summary>
    public void BeginFrame()
    {
        _frame++;
        SmoothedLastFrame = 0;
    }

    /// <summary>Drops the objects that were not presented this frame (departed, culled, retired).</summary>
    public void EndFrame()
    {
        _stale.Clear();
        foreach ((ushort key, Track track) in _tracks)
        {
            if (track.LastFrame != _frame)
            {
                _stale.Add(key);
            }
        }

        foreach (ushort key in _stale)
        {
            _tracks.Remove(key);
        }
    }

    /// <summary>
    /// The pose an object was last drawn at — for every consumer that is not the mesh renderer.
    /// </summary>
    /// <param name="objectRef">The pool object.</param>
    /// <param name="pose">Its presented pose, when it has one.</param>
    /// <returns>True when the object has been presented and its drawn pose is known.</returns>
    public bool TryGetPresented(ushort objectRef, out Pose pose)
    {
        if (_tracks.TryGetValue(objectRef, out Track track))
        {
            pose = track.Drawn;
            return true;
        }

        pose = default;
        return false;
    }

    /// <summary>
    /// Records the sim's current pose for an object and returns the pose to draw it at.
    /// </summary>
    /// <param name="objectRef">The pool object (the track key).</param>
    /// <param name="classRef">Its class record, so a re-classed object starts a fresh track.</param>
    /// <param name="current">The pose the sim publishes this frame.</param>
    /// <param name="nowSeconds">Simulated time now.</param>
    public Pose Present(ushort objectRef, ushort classRef, in Pose current, double nowSeconds)
    {
        if (!_tracks.TryGetValue(objectRef, out Track track) || track.ClassRef != classRef)
        {
            track = Track.Fresh(classRef, in current, nowSeconds);
        }
        else if (!Same(in track.Sim, in current))
        {
            double dx = current.X - track.Sim.X;
            double dy = current.Y - track.Sim.Y;
            double dz = current.Z - track.Sim.Z;
            double seen = nowSeconds - track.SimAt;
            if (Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) > MaxJumpWorldUnits || seen <= 0.0)
            {
                track = Track.Fresh(classRef, in current, nowSeconds);
            }
            else
            {
                bool coarse = seen > MinSmoothedIntervalSeconds;
                if (coarse)
                {
                    UpdatesCoarse++;
                    LongestIntervalSeconds = Math.Max(LongestIntervalSeconds, seen);
                }
                else
                {
                    UpdatesEveryStep++;
                }

                // Continuity: where the OLD law has the object at this very instant is where it
                // still is.  A coarse update always carries the difference to the new sim pose as
                // a residual; an every-step update only re-bases a residual that is already
                // blending out, so a live object that has never been coarse is drawn at its sim
                // pose exactly.
                Pose drawnNow = Evaluate(in track, nowSeconds);
                if (coarse && seen <= MaxIntervalSeconds)
                {
                    track.ResidualX = drawnNow.X - current.X;
                    track.ResidualY = drawnNow.Y - current.Y;
                    track.ResidualZ = drawnNow.Z - current.Z;
                    track.ResidualHeading = ShortestArc(drawnNow.HeadingDegrees, current.HeadingDegrees);
                    track.ResidualPitch = ShortestArc(drawnNow.PitchDegrees, current.PitchDegrees);
                    track.ResidualRoll = ShortestArc(drawnNow.RollDegrees, current.RollDegrees);
                    track.ResidualAt = nowSeconds;

                    // The blend is at least as long as the LONGER of this interval and the last: an
                    // EARLY update (the measured 10- and 15-frame intervals between 2-s holds) must
                    // not compress a residual born of a long hold into a dash.
                    track.BlendSeconds = Math.Clamp(
                        Math.Max(seen, track.LongestInterval), MinBlendSeconds, MaxBlendSeconds);
                    track.ResidualLive = true;
                }
                else if (track.ResidualLive)
                {
                    track.ResidualX = drawnNow.X - current.X;
                    track.ResidualY = drawnNow.Y - current.Y;
                    track.ResidualZ = drawnNow.Z - current.Z;
                    track.ResidualHeading = ShortestArc(drawnNow.HeadingDegrees, current.HeadingDegrees);
                    track.ResidualPitch = ShortestArc(drawnNow.PitchDegrees, current.PitchDegrees);
                    track.ResidualRoll = ShortestArc(drawnNow.RollDegrees, current.RollDegrees);
                }

                track.Coarse = coarse && seen <= MaxIntervalSeconds;
                track.Interval = seen;
                if (track.Coarse)
                {
                    track.LongestInterval = Math.Max(track.LongestInterval, seen);
                }

                // The velocity is remembered from EVERY update, coarse or not: the measured holds
                // begin right after a run of every-step updates, and the last of those carries the
                // best velocity estimate there is.
                track.HasVelocity = seen <= MaxIntervalSeconds;
                track.VelocityX = dx / seen;
                track.VelocityY = dy / seen;
                track.VelocityZ = dz / seen;
                track.Sim = current;
                track.SimAt = nowSeconds;
            }
        }

        Pose pose = Evaluate(in track, nowSeconds);
        if (track.ResidualLive && nowSeconds - track.ResidualAt >= track.BlendSeconds)
        {
            track.ResidualLive = false;                 // blended out: nothing left to carry
        }

        if (!Same(in pose, in track.Sim))
        {
            SmoothedLastFrame++;
        }

        track.Drawn = pose;
        track.LastFrame = _frame;
        _tracks[objectRef] = track;
        return pose;
    }

    /// <summary>The law, read-only: where a track puts its object at an instant.</summary>
    private static Pose Evaluate(in Track track, double nowSeconds)
    {
        Pose pose = track.Sim;
        double sinceUpdate = nowSeconds - track.SimAt;
        if (track.HasVelocity && sinceUpdate > MinSmoothedIntervalSeconds)
        {
            // Reckoning starts the moment an update is OVERDUE (an every-step object is never
            // overdue, so it is drawn at its sim pose exactly) and flies on for the longer of
            // MaxIntervalSeconds and ExtrapolationCap × the longest hold this object has shown:
            // a 10-frame interval followed by a 120-frame hold (measured) must not park it.
            double horizon = Math.Max(MaxIntervalSeconds, track.LongestInterval * ExtrapolationCap);
            double age = Math.Min(sinceUpdate, horizon);
            pose = pose with
            {
                X = pose.X + (track.VelocityX * age),
                Y = pose.Y + (track.VelocityY * age),
                Z = pose.Z + (track.VelocityZ * age),
            };
        }

        if (track.ResidualLive)
        {
            double f = 1.0 - Math.Clamp((nowSeconds - track.ResidualAt) / track.BlendSeconds, 0.0, 1.0);
            if (f > 0.0)
            {
                pose = new Pose(
                    pose.X + (track.ResidualX * f),
                    pose.Y + (track.ResidualY * f),
                    pose.Z + (track.ResidualZ * f),
                    Wrap(pose.HeadingDegrees + (track.ResidualHeading * f)),
                    Wrap(pose.PitchDegrees + (track.ResidualPitch * f)),
                    Wrap(pose.RollDegrees + (track.ResidualRoll * f)));
            }
        }

        return pose;
    }

    private static bool Same(in Pose a, in Pose b) =>
        a.X == b.X && a.Y == b.Y && a.Z == b.Z
        && a.HeadingDegrees == b.HeadingDegrees && a.PitchDegrees == b.PitchDegrees && a.RollDegrees == b.RollDegrees;

    private static double Wrap(double degrees) => ((degrees % 360.0) + 360.0) % 360.0;

    /// <summary>The signed shorter-arc difference <c>a − b</c> in degrees, in (−180, 180].</summary>
    private static double ShortestArc(double a, double b) => ((((a - b) % 360.0) + 540.0) % 360.0) - 180.0;

    /// <summary>Interpolates two angles in degrees along the shorter arc.</summary>
    /// <param name="a">From.</param>
    /// <param name="b">To.</param>
    /// <param name="t">0..1.</param>
    public static double LerpAngle(double a, double b, double t)
    {
        double delta = ((((b - a) % 360.0) + 540.0) % 360.0) - 180.0;
        double result = a + (delta * t);
        return ((result % 360.0) + 360.0) % 360.0;
    }

    private struct Track
    {
        public ushort ClassRef;
        public Pose Sim;
        public double SimAt;
        public Pose Drawn;
        public bool Coarse;
        public bool HasVelocity;
        public double Interval;
        public double LongestInterval;
        public double VelocityX, VelocityY, VelocityZ;
        public bool ResidualLive;
        public double ResidualX, ResidualY, ResidualZ;
        public double ResidualHeading, ResidualPitch, ResidualRoll;
        public double ResidualAt;
        public double BlendSeconds;
        public long LastFrame;

        public static Track Fresh(ushort classRef, in Pose current, double now) =>
            new() { ClassRef = classRef, Sim = current, SimAt = now, Drawn = current };
    }
}
