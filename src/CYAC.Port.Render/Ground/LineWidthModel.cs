using System.Collections.Immutable;
using System.Globalization;

namespace CYAC.Port.Render.Ground;

/// <summary>
/// Which physical width a <c>LINE</c> shape record is drawn with — the classes the shipped data
/// actually distinguishes.
/// </summary>
/// <remarks>
/// <para>
/// The class is a property of the MESH the record belongs to, not of the record: every line record
/// of <c>city</c> is a street, every line record of <c>strip</c> is a runway marking, every line
/// record of <c>eject2</c>/<c>eject3</c>/<c>eject4</c> is a parachute shroud line, the whole of
/// <c>bullet</c> is one tracer.  <see cref="LineWidthModel.ClassOf"/> is that mapping, and it is a
/// census of the shipped meshes rather than a taste (a census lists all 167 line records
/// in the tree, and all 127 that the port draws at <c>--lod max</c>).
/// </para>
/// </remarks>
public enum LineWidthClass
{
    /// <summary>
    /// Anything with no better class: an object's own detail lines (<c>crater</c>'s cracks,
    /// <c>flare</c>'s star-burst, <c>hangar</c>'s and <c>tower</c>'s corner edges).
    /// </summary>
    Line = 0,

    /// <summary>A road or a city street — <c>road</c>, <c>city</c>, <c>urban</c>, <c>urban2</c>.</summary>
    Road = 1,

    /// <summary>A water course — <c>river</c>.</summary>
    River = 2,

    /// <summary>
    /// A tarmac marking — <c>strip</c>'s centreline dashes, <c>airport</c>'s apron outline,
    /// <c>sam</c>'s twelve-segment site perimeter and its six launcher pad marks.
    /// </summary>
    Mark = 3,

    /// <summary>A tracer — the <c>bullet</c> mesh's single 64-foot line record.</summary>
    Tracer = 4,

    /// <summary>
    /// A PARACHUTE SHROUD LINE: the twenty line records of <c>eject2</c>, <c>eject3</c> and
    /// <c>eject4</c> that run from the pilot's harness up to the canopy rim.
    /// </summary>
    Rope = 5,

    /// <summary>
    /// A STRUT: an aircraft's landing-gear leg or any other airframe line (an
    /// aerial mast, a canopy frame member).
    /// </summary>
    Strut = 6,

    /// <summary>
    /// A GREY ORDINARY ROUND: the port's own <c>round</c> mesh
    /// (<c>CYAC.Port.Core.Sim.Session.GunneryRounds.RoundMesh</c>), the N − 1 rounds of a burst that
    /// are not the tracer.  A thread, not a light source: it is widened to its own floor and dimmed
    /// to its coverage, so it reads as a faint streak rather than a second tracer.
    /// </summary>
    Round = 7,
}

/// <summary>
/// How wide a LINE is drawn: a physical width in FEET, floored by an on-screen minimum in host pixels.  H17 extends
/// it with <see cref="LineWidthClass.Rope"/> and <see cref="LineWidthClass.Strut"/> and a per-MESH override table.
/// </summary>
/// <param name="RoadFeet">Width of a <see cref="LineWidthClass.Road"/> line.</param>
/// <param name="RiverFeet">Width of a <see cref="LineWidthClass.River"/> line.</param>
/// <param name="MarkFeet">Width of a <see cref="LineWidthClass.Mark"/> line.</param>
/// <param name="TracerFeet">Width of a <see cref="LineWidthClass.Tracer"/> line.</param>
/// <param name="LineFeet">Width of an unclassified <see cref="LineWidthClass.Line"/>.</param>
/// <param name="RopeFeet">width of a <see cref="LineWidthClass.Rope"/>.</param>
/// <param name="StrutFeet">width of a <see cref="LineWidthClass.Strut"/>.</param>
/// <param name="TracerFloorHostPixels">the tracer class's own on-screen floor, host pixels at 1,920.</param>
/// <param name="RoundFeet">width of a <see cref="LineWidthClass.Round"/> (a grey ordinary round).</param>
/// <param name="RoundFloorHostPixels">the round class's own on-screen floor, host pixels at 1,920.</param>
/// <param name="FloorHostPixels">
/// The smallest width a line may be DRAWN at, in host pixels at a 1,920-pixel-wide window; it is
/// scaled with the window's own width by <see cref="FloorPixelsFor"/> so a 4K frame shows the same
/// angular thickness.
/// </param>
/// <param name="MeshOverrides">
/// per-MESH widths in feet, for the handful of meshes whose lines are not any class
/// (<see cref="DefaultMeshOverrides"/>).  Null takes the shipped table; an empty map takes none.
/// </param>
/// <param name="ThreadFade">
/// Whether a THREAD thinner than the on-screen floor is DIMMED by the fraction of a pixel it really covers as well
/// as widened to the floor (<see cref="ThreadCoverage"/>).  On by default; off restores H15's one rule for every
/// class, which is the A/B.
/// </param>
/// <remarks>
/// <para>
/// <b>The design</b>: with unlimited resolution, objects need dimensions instead of lines.  A 1991 line record was a ONE-PIXEL mark on a 320×200 screen; at 1,920 pixels the same
/// record is a hair, and at 4K it disappears.  So a line becomes a CAPSULE with a real world width, and the
/// on-screen floor keeps it visible when the physical width falls below what the frame can show.
/// </para>
/// <para>
/// <b>Where the numbers come from.</b>  The original's projector is
/// <c>screen_x = centre + ((X &lt;&lt; S) / Z)</c> with <c>S = g_gfx_zoom_shift [0xE836] = 7</c>
/// (<see cref="Projection"/>), so its focal length is <c>2^7 = 128</c> pixels on a 320-pixel
/// screen and one 320×200 pixel subtends <c>atan(1/128) ≈ 7.8 mrad</c>: a feature one pixel wide at
/// range <c>d</c> is <c>d / 128</c> feet wide.
/// </para>
/// <para>
/// The shipped data agrees with the model where it bothers to state a width: <c>road</c>'s densest LOD is not a line
/// at all but a QUAD <b>48 feet</b> wide (<c>data/meshes/road.json</c> LOD1 <c>x = ±6</c> × <c>2^scaleShiftExponent
/// = 4</c>) — exactly one 320×200 pixel at 6,144 feet — while <c>river</c> is 1,024 feet and <c>strip</c> 448.  The
/// authors gave a dimension to what needed one and left everything else as a one-pixel line; this model gives the
/// rest of them one too.
/// </para>
/// <para>
/// World units ARE feet: the simulation carries positions as Q8 feet
/// (<c>CYAC.Port.Core.Sim.Flight.AoaProbes</c> <c>altitudeQ8Feet</c>) and the renderer divides by
/// 256 (<c>CYAC.Port.Core.Model.World.SceneInstance</c>).
/// </para>
/// <para>
/// <b>DEVIATION.</b> Every value here is a deviation from the original, which has no widths at all — it is a
/// presentation choice, tuned in-game through <c>--line-width</c> and <c>--line-floor</c>.
/// </para>
/// </remarks>
public readonly record struct LineWidthModel(
    double RoadFeet = LineWidthModel.DefaultRoadFeet,
    double RiverFeet = LineWidthModel.DefaultRiverFeet,
    double MarkFeet = LineWidthModel.DefaultMarkFeet,
    double TracerFeet = LineWidthModel.DefaultTracerFeet,
    double LineFeet = LineWidthModel.DefaultLineFeet,
    double RopeFeet = LineWidthModel.DefaultRopeFeet,
    double StrutFeet = LineWidthModel.DefaultStrutFeet,
    double FloorHostPixels = LineWidthModel.DefaultFloorHostPixels,
    ImmutableDictionary<string, double>? MeshOverrides = null,
    bool ThreadFade = true,
    double TracerFloorHostPixels = LineWidthModel.DefaultTracerFloorHostPixels,
    double RoundFeet = LineWidthModel.DefaultRoundFeet,
    double RoundFloorHostPixels = LineWidthModel.DefaultRoundFloorHostPixels)
{
    /// <summary>A road or street: 30 feet (a two-lane carriageway).</summary>
    public const double DefaultRoadFeet = 30.0;

    /// <summary>A river drawn as a line: 40 feet.</summary>
    public const double DefaultRiverFeet = 40.0;

    /// <summary>A tarmac marking: 3 feet (a painted stripe).</summary>
    public const double DefaultMarkFeet = 3.0;

    /// <summary>
    /// A tracer: <b>0.5 feet</b> — the projectile CORE.
    /// </summary>
    /// <remarks>
    /// At full width a tracer round reads as a sausage, which is arcade-y; the projectile is drawn
    /// smaller with a special-effect halo instead.  The bright body of a tracer is its glow, not its slug, so
    /// the core shrinks to a third and <see cref="TracerHalo"/> paints the light around it.
    /// </remarks>
    public const double DefaultTracerFeet = 0.5;

    /// <summary>
    /// An unclassified detail line: <b>0.5 feet</b>.
    /// </summary>
    /// <remarks>
    /// A crater's crack, a hangar's corner edge and a flare's star-burst are DRAWN LINES on an
    /// object, not structural members three feet through; at 3 ft every one of them read as a
    /// sausage at close range.  It is still <b>(open)</b> — no byte in the image gives an object's
    /// detail lines a width — but the on-screen floor is what decides their
    /// size at any range they are actually seen at, and 0.5 ft only bites inside about 60 feet.
    /// </remarks>
    public const double DefaultLineFeet = 0.5;

    /// <summary>
    /// A parachute shroud line: <b>0.15 feet</b> (1.8 inches).
    /// </summary>
    /// <remarks>
    /// About one twentieth of a 3 ft line, drawn anti-aliased into semi-transparent tiny ropes.  A
    /// real parachute shroud is about 3 mm of cord; at 0.15 ft the line is under the on-screen
    /// floor at every range past about 20 feet, so what is drawn is the floor width at a COVERAGE
    /// the exact raster measures — a semi-transparent anti-aliased thread.
    /// </remarks>
    public const double DefaultRopeFeet = 0.15;

    /// <summary>
    /// A landing-gear leg or airframe line: <b>0.3 feet</b> (3.6 inches).
    /// </summary>
    /// <remarks>
    /// An oleo strut on a P-51-sized fighter is about four inches across, and the aerial masts and
    /// canopy frame members the same records also cover are thinner still.
    /// </remarks>
    public const double DefaultStrutFeet = 0.3;

    /// <summary>
    /// The on-screen floor for every class but the tracer: OFF.  A minimum line width in the
    /// distance does not read well, so a thin line shrinks and fades with its range instead.
    /// <c>--line-floor 1.5</c> restores a floor.
    /// </summary>
    public const double DefaultFloorHostPixels = 0.0;

    /// <summary>
    /// The TRACER keeps its own floor (1.5 host pixels at 1,920), because a tracer is a light
    /// source, not a surface.  <c>--tracer-floor</c>.
    /// </summary>
    public const double DefaultTracerFloorHostPixels = 1.5;

    /// <summary>
    /// An ordinary round: <b>0.15 feet</b> (1.8 inches — thinner, white and longer reads most
    /// naturally).  A .50-calibre slug is half an inch; the streak is its
    /// motion over a frame, not its body, and 0.15 ft is under every floor past about 20 feet, so
    /// what is seen is the floor width at the streak's real coverage.
    /// </summary>
    public const double DefaultRoundFeet = 0.15;

    /// <summary>
    /// The round's own floor: <b>1 host pixel</b> at 1,920.  The general floor is off and a
    /// 0.25 ft streak at a thousand feet is a fifth of a pixel; without a floor of its own the
    /// raster would drop it entirely.  With one, the thread rule dims it to that fifth.
    /// </summary>
    public const double DefaultRoundFloorHostPixels = 1.0;

    /// <summary>The window width the <see cref="FloorHostPixels"/> figure is stated at: 1,920.</summary>
    public const int FloorReferenceWidth = 1920;

    /// <summary>
    /// The shipped per-MESH width table: the meshes whose lines are not any class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two entries, each from the geometry rather than from taste:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>trees</c> — records 8…11 are four VERTICAL lines from
    ///   <c>y = 0</c> to <c>y = 80</c> in palette 6 (brown), one under each of the four tree
    ///   canopies: they are TRUNKS.  <b>2 ft</b> is a trunk under an eighty-foot tree.</description></item>
    ///   <item><description><c>hedge</c> — records 4…9 are six vertical lines 288…320 units tall in
    ///   palette 58 (brown), spaced along <c>z</c>: a TREELINE's stems.  <b>3 ft</b>.</description></item>
    /// </list>
    /// <para>
    /// Everything else in the tree fits a class; <c>sam</c>'s twenty-one lines fit
    /// <see cref="LineWidthClass.Mark"/> and are mapped there by <see cref="ClassOf"/> instead of
    /// here, because a width knob should move them with the other tarmac markings.
    /// </para>
    /// </remarks>
    public static ImmutableDictionary<string, double> DefaultMeshOverrides { get; } =
        ImmutableDictionary.CreateRange(
            StringComparer.Ordinal,
            [
                new KeyValuePair<string, double>("trees", 2.0),
                new KeyValuePair<string, double>("hedge", 3.0),
            ]);

    /// <summary>
    /// The smallest coverage a sub-floor THREAD is drawn at: <b>0.25</b>.
    /// </summary>
    /// <remarks>
    /// The rule is that a rope below the on-screen floor "must render as a semi-transparent
    /// anti-aliased thread (coverage), never vanish".  The coverage is the honest one — <c>drawn
    /// width / floor width</c> — but it is clamped up to a quarter so that a shroud line at a
    /// thousand feet is still faintly there — it never vanishes; on a
    /// bright sky a quarter-coverage black thread still lands a quarter of the way to black.
    /// </remarks>
    public const double MinThreadCoverage = 0.25;

    /// <summary>The shipped model.</summary>
    public static LineWidthModel Default { get; } = new(
        DefaultRoadFeet,
        DefaultRiverFeet,
        DefaultMarkFeet,
        DefaultTracerFeet,
        DefaultLineFeet,
        DefaultRopeFeet,
        DefaultStrutFeet,
        DefaultFloorHostPixels,
        DefaultMeshOverrides,
        ThreadFade: true,
        TracerFloorHostPixels: DefaultTracerFloorHostPixels,
        RoundFeet: DefaultRoundFeet,
        RoundFloorHostPixels: DefaultRoundFloorHostPixels);

    /// <summary>
    /// Whether a class is a THREAD: a thin, dark, PHYSICAL member, as against a light source or a
    /// ground feature that has to stay crisp.
    /// </summary>
    /// <param name="lineClass">The class.</param>
    /// <remarks>
    /// <para>
    /// The rule is <i>widen, never dim</i>: <c>road</c>, <c>river</c>, <c>mark</c> and
    /// <c>tracer</c> are all things the 1991 renderer kept crisp to the horizon or things that emit
    /// light, and dimming them by their sub-pixel coverage made the roads fade out.
    /// </para>
    /// <para>
    /// A parachute shroud and a gear leg are neither.  They are matte dark members a few inches
    /// across, and at 1080p they are genuinely sub-pixel at any range you would see them from — so
    /// the honest picture — anti-aliased into semi-transparent tiny ropes — is the floor WIDTH at
    /// the real COVERAGE.
    /// <see cref="LineWidthClass.Line"/> stays out of it: it covers <c>flare</c>'s star-burst,
    /// which is a light source, as well as <c>crater</c>'s cracks.
    /// </para>
    /// </remarks>
    public static bool IsThread(LineWidthClass lineClass) =>
        lineClass is LineWidthClass.Rope or LineWidthClass.Strut or LineWidthClass.Round;   // A grey round is a thread

    /// <summary>
    /// The coverage a thread is painted at once it has been widened to the floor.
    /// </summary>
    /// <param name="projectedWidthPixels">Its true projected width, in the target's pixels.</param>
    /// <param name="floorPixels">The floor it was widened to, in the same pixels.</param>
    /// <returns>
    /// <c>1</c> when the thread is at least as wide as the floor, otherwise the ratio, clamped up to
    /// <see cref="MinThreadCoverage"/>.
    /// </returns>
    public static double ThreadCoverage(double projectedWidthPixels, double floorPixels)
    {
        if (!(floorPixels > 0.0) || !double.IsFinite(projectedWidthPixels))
        {
            return 1.0;
        }

        double ratio = projectedWidthPixels / floorPixels;
        return ratio >= 1.0 ? 1.0 : Math.Max(MinThreadCoverage, Math.Max(0.0, ratio));
    }

    /// <summary>The physical width, in feet, of one class of line.</summary>
    /// <param name="lineClass">The class.</param>
    public double WidthFeet(LineWidthClass lineClass) => lineClass switch
    {
        LineWidthClass.Road => RoadFeet,
        LineWidthClass.River => RiverFeet,
        LineWidthClass.Mark => MarkFeet,
        LineWidthClass.Tracer => TracerFeet,
        LineWidthClass.Rope => RopeFeet,
        LineWidthClass.Strut => StrutFeet,
        LineWidthClass.Round => RoundFeet,
        _ => LineFeet,
    };

    /// <summary>
    /// The physical width, in feet, of one MESH's line records: the per-mesh override when there
    /// is one, otherwise the width of the mesh's class.
    /// </summary>
    /// <param name="basename">The mesh's <c>Basename</c>.</param>
    public double WidthFeetFor(string basename)
    {
        ImmutableDictionary<string, double> table = MeshOverrides ?? DefaultMeshOverrides;
        return basename is not null && table.TryGetValue(basename, out double feet)
            ? feet
            : WidthFeet(ClassOf(basename ?? string.Empty));
    }

    /// <summary>
    /// The on-screen floor for a window of a given width, in that window's own pixels.
    /// </summary>
    /// <param name="hostWidthPixels">The window's width in HOST pixels (not super-samples).</param>
    /// <remarks>
    /// Scaled with the width so the floor is an ANGLE, not a number of pixels: 1.5 px at 1,920 is
    /// 3 px at 3,840, and a 4K frame shows the same picture as a 1080p one, larger.  That is the
    /// same rule the horizon band follows (H12 §0: the lens, not the window).
    /// </remarks>
    public double FloorPixelsFor(int hostWidthPixels) =>
        FloorHostPixels <= 0
            ? 0.0
            : FloorHostPixels * hostWidthPixels / FloorReferenceWidth;

    /// <summary>The floor for one class: the tracer takes the larger of its own and the general floor, H22 the grey
    /// round likewise its own, every other class the general one.</summary>
    /// <param name="hostWidthPixels">The window's width.</param>
    /// <param name="lineClass">The line's class.</param>
    public double FloorPixelsFor(int hostWidthPixels, LineWidthClass lineClass) => lineClass switch
    {
        LineWidthClass.Tracer => Math.Max(
            FloorPixelsFor(hostWidthPixels),
            TracerFloorHostPixels <= 0 ? 0.0 : TracerFloorHostPixels * hostWidthPixels / FloorReferenceWidth),
        LineWidthClass.Round => Math.Max(
            FloorPixelsFor(hostWidthPixels),
            RoundFloorHostPixels <= 0 ? 0.0 : RoundFloorHostPixels * hostWidthPixels / FloorReferenceWidth),
        _ => FloorPixelsFor(hostWidthPixels),
    };

    /// <summary>
    /// Which width class a mesh's line records belong to.
    /// </summary>
    /// <param name="basename">The mesh's <c>Basename</c>.</param>
    /// <remarks>
    /// <para>
    /// A census of every LINE record in the shipped meshes (167 records over 32
    /// meshes, of which 127 in 25 meshes are at a densest LOD and so are what the port draws under
    /// <c>--lod max</c>).  The identities behind the mapping:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b><c>eject2</c> / <c>eject3</c> / <c>eject4</c> = the PARACHUTE
    ///   SHROUD LINES</b> — 6 / 8 / 6 records in palette 0 (black), each running from the pilot's
    ///   harness (<c>y ≈ 14</c>) up to the canopy rim (<c>y</c> = 90, 125, 182), lengthening 19 →
    ///   29 → 47 feet as the canopy inflates through the three stages of the H7 ejection
    ///   sequence.</description></item>
    ///   <item><description><b>every jet's NOSE-GEAR LEG is one LINE record</b> in the third gear
    ///   articulation leaf — <c>f86</c> 86, <c>f4</c> 61, <c>mig15</c> 56, <c>mig21</c> 62 — and the
    ///   <c>f4</c>'s and <c>mig15</c>'s MAIN legs (records 59/60 and 54/55, at <c>x = ±34</c> and
    ///   <c>±31</c>) are lines that hang outside the leaves and are always drawn.  <c>l5</c>'s four
    ///   are a taildragger's fixed gear.  The taildraggers' <c>p51</c>/<c>fw190</c> legs are
    ///   polygons.</description></item>
    ///   <item><description><c>sam</c> — twelve palette-58 segments at <c>y = 0</c> outlining the
    ///   site, six palette-15 pad marks at the three launchers and three palette-185 rails: a tarmac
    ///   MARKING set, like <c>airport</c>'s.</description></item>
    ///   <item><description><c>canopy</c> — five palette-0 lines over four vertices: the jettisoned
    ///   canopy's own FRAME.  An airframe line, so a strut.</description></item>
    /// </list>
    /// <para>
    /// Streets and roads share a class because they are the same thing at two scales.
    /// </para>
    /// </remarks>
    public static LineWidthClass ClassOf(string basename) => basename switch
    {
        "road" or "city" or "urban" or "urban2" => LineWidthClass.Road,
        "river" => LineWidthClass.River,
        "strip" or "airport" or "sam" => LineWidthClass.Mark,
        "bullet" => LineWidthClass.Tracer,
        "round" => LineWidthClass.Round,   // The port's own grey-round mesh, not a shipped one
        "eject2" or "eject3" or "eject4" => LineWidthClass.Rope,
        _ => IsAirframe(basename) ? LineWidthClass.Strut : LineWidthClass.Line,
    };

    /// <summary>
    /// The twenty-one meshes whose line records are an AIRFRAME's — a gear leg, an aerial mast, a
    /// canopy frame member.
    /// </summary>
    /// <remarks>
    /// The eighteen aircraft meshes in <c>data/exe/meshes</c> plus the two glide bombs
    /// (<c>hell</c>, <c>hells</c>) and the jettisoned <c>canopy</c>.  Spelled out rather than
    /// derived from a class flag because the mesh library does not publish the class record's
    /// <c>+0x01</c> byte (the JET / has-an-engine bits, H11/H13), and a list that a census produced
    /// is checkable — <c>LineSceneCensus</c> prints exactly which meshes fell in which class.
    /// </remarks>
    private static bool IsAirframe(string basename) => basename switch
    {
        "b17" or "b29" or "b52" or "f4" or "f86" or "f105" or "fw190" or "l5"
            or "me109" or "me110" or "me163" or "me262" or "mig15" or "mig17" or "mig21"
            or "p47" or "p51" or "yak9" or "hell" or "hells" or "canopy" => true,
        _ => false,
    };

    /// <summary>
    /// Parses a <c>--line-width</c> list —
    /// <c>road=30,river=40,mark=3,tracer=0.5,line=0.5,rope=0.15,strut=0.3,round=0.25</c>, with
    /// <c>mesh:&lt;basename&gt;=&lt;feet&gt;</c> for a single mesh.
    /// </summary>
    /// <param name="text">The list; null or empty leaves this model unchanged.</param>
    /// <returns>The model with the named widths replaced.</returns>
    /// <exception cref="FormatException">A term is not <c>name=number</c>, or names no class.</exception>
    public LineWidthModel WithOverrides(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return this;
        }

        LineWidthModel result = this;
        foreach (string term in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = term.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || !double.TryParse(
                    term.AsSpan(equals + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double feet)
                || feet < 0)
            {
                throw new FormatException($"--line-width: '{term}' is not <class>=<feet>");
            }

            string name = term[..equals].Trim().ToLowerInvariant();
            if (name.StartsWith("mesh:", StringComparison.Ordinal))
            {
                string mesh = name["mesh:".Length..];
                if (mesh.Length == 0)
                {
                    throw new FormatException("--line-width: 'mesh:' names no mesh");
                }

                result = result with
                {
                    MeshOverrides = (result.MeshOverrides ?? DefaultMeshOverrides).SetItem(mesh, feet),
                };
                continue;
            }

            result = name switch
            {
                "road" => result with { RoadFeet = feet },
                "river" => result with { RiverFeet = feet },
                "mark" => result with { MarkFeet = feet },
                "tracer" => result with { TracerFeet = feet },
                "line" => result with { LineFeet = feet },
                "rope" => result with { RopeFeet = feet },
                "strut" => result with { StrutFeet = feet },
                "round" => result with { RoundFeet = feet },
                _ => throw new FormatException(
                    $"--line-width: '{name}' names no class "
                        + "(road, river, mark, tracer, line, rope, strut, round, or mesh:<basename>)"),
            };
        }

        return result;
    }
}
