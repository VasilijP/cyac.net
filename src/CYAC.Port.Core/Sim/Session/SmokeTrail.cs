using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Effects;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// THE SMOKE LAYER: a presentation-only shadow of the kernel's 15-slot puff pool whose puffs are BIGGER and live
/// MUCH LONGER than the original's, so a wreck trails a real column.
/// </summary>
/// <remarks>
/// <para>
/// <b>The intent.</b> The original's smoke is short-lived because a 1991 machine had limited
/// resources; the port makes the whole smoking process a bit bigger and significantly longer.  The
/// original's own growth law at the original's own scale is the BASELINE
/// (<c>--smoke-trail off</c>), and this layer goes past it: a LABELLED DEVIATION.
/// </para>
/// <para>
/// <b>Why a layer and not a longer lifetime constant.</b>  A puff's lifetime and the 15-slot pool
/// live in the ported register file (<c>slot[+6]</c> the expiry, the LRU by <c>slot[+4]</c>;
/// <see cref="SmokePuffTable"/>), so lengthening either is a SIM change and both recorded replays
/// would diverge.  The port's rule since H22 (<see cref="GunneryRounds"/>) is the other way round:
/// the ported state is the AUTHORITY for what happens, and the port may bear MORE presentation on
/// top of it, byte-inert.  The sim's puffs therefore stay exactly as they are — they still expire
/// after 5 / 25 / 45 frame units and still recycle through 15 slots — and this layer shadows every
/// puff SPAWN and bears its own long-lived, larger puff in its place.  The host then SKIPS the sim's
/// own puff objects while the layer is on, because they are represented.
/// </para>
/// <para>
/// <b>Presentation only, by construction.</b>  <see cref="Step"/> READS the combat register file and
/// the pool arena after <see cref="MissionSession.Step"/> and WRITES neither — asserted byte-for-byte
/// on every frame of a 1,500-frame sortie by <c>SmokeTrailTests</c>, exactly as H22's acceptance
/// asserts it for the gunnery trail.  <c>--replay</c> never builds a host session, so the verified
/// replays are untouched whether or not this type exists.
/// </para>
/// <para>
/// <b>Detecting a spawn.</b>  Each of the 15 slots is shadowed with what it held last frame:
/// its object, birth frame (<c>+4</c>), expiry (<c>+6</c>), kind (<c>+8</c>), spread accumulator
/// (<c>+2</c>) and — the GHOST, H22's trick — where
/// <see cref="SmokePuffTable.PerFrameStep"/> was going to put the puff this frame.  A slot that went
/// idle → active, changed any of those identity words, had its accumulator RESET (allocation writes
/// <c>slot[+2] = 0</c>, <c>image@0x0B19A</c>) or whose object is not on its ghost has a NEW puff in
/// it — which covers LRU recycling, where the slot never goes idle at all.
/// </para>
/// <para>
/// <b>The puff then flies the original's own law</b>
/// (<c>smoke_per_frame_physics_step @image@0x0B20F</c>, ported in
/// <see cref="SmokePuffTable.PerFrameStep"/>), in <see langword="double"/> instead of the truncating
/// 16-bit product, so it keeps moving exactly as the sim's puff would have kept moving had it not
/// expired: a kind ≠ 0 puff's Y gains <see cref="SmokePuffTable.RiseRate"/> × dt / 256 sim units a
/// step, its X gains its own accumulator × dt / 256, and that accumulator grows by
/// <see cref="SmokePuffTable.SpreadGrowth"/> × dt / 256 up to
/// <see cref="SmokePuffTable.SpreadCap"/>; every puff, kind 0 included, tumbles
/// <see cref="SmokePuffTable.TumbleStep"/> BAM on all three euler words per step, and a kind-0 puff
/// stays where it was lit.  In feet, at the port's <c>dt = TickClock.StepTicks = 5</c> and 51.2 steps
/// to the master frame (<c>TickClock.MasterFrameCounter = accumulator &gt;&gt; 8</c>): the rise is
/// <c>0x1C00 × 5 / 256 / 256 = 0.547</c> ft a step = <b>28.0 ft/s</b>, and the sideways spread
/// accelerates at <b>0.10 ft/s²</b> to a terminal <c>0x1400 × 5 / 256 / 256 = 0.39</c> ft a step =
/// <b>20.0 ft/s</b>, reached after 512 steps = 10 s.  The tumble is 0x50 of the 2,880-unit BAM circle
/// per step — 10° a step, 512°/s — and is NOT scaled by dt in the original either.
/// </para>
/// <para>
/// <b>The deviations, all labelled.</b>  (1) LIFE: a layer puff lives the original's lifetime for its
/// kind × <see cref="LifeScale"/> (default 3.0 — 5 s → 15 s trail puffs, 25 s → 75 s column puffs,
/// 45 s → 135 s stationary).  (2) SIZE: H23's growth law runs over the ORIGINAL's own span and then
/// HOLDS at its 200 / 280 / 240 world units for the rest of the longer life
/// (<see cref="StretchRamp"/> spreads it over the whole life instead), and <c>--smoke-size</c>
/// (default 1.3) scales the result.  (3) FADE: <see cref="FadeFraction"/> of the life over which the instance's opacity
/// falls to 0, so a puff does not pop out — the original POPS.  (4) The layer's age is exact frame
/// time from the spawn, where the original quantises the birth to the master frame and does the
/// subtraction in 16 bits (<see cref="SmokePuffState"/>), which cannot represent a stretched span.
/// </para>
/// <para>
/// <b>What is NOT deviated: the emission cadence</b> — except for the ONE case §H28 names below.
/// The layer invents no puffs — it mirrors the
/// the kernel's emitters one for one (<see cref="EffectEmitterTable"/>, a row emits every
/// <c>+0x11</c> scaled frames, default <see cref="EffectEmitterTable.DefaultInterval"/> = 0x10 =
/// 1,024 ticks = 4 master frames ≈ 4 s).  A longer life at the SAME emission rate is precisely what
/// makes the column long: a wreck's kind-3 column holds
/// <c>25 s / 4 s = 6</c> puffs in the original and <c>75 s / 4 s = 18</c> here.
/// </para>
/// <para>
/// <b>THE WRECK TRAIL, a second labelled deviation:</b> "once enemy plane (or any plane) is shot down and falling
/// down it could emit black puffs of smoke.  The game does it at certain level of damage … but it stops after few
/// puffs, this could be … from the 'shot down' moment (after explosion) so it could fall down nicely with black trail
/// of smoke.  The emitter ROW, not the pool, caps a damage trail at
/// four beads 4 s apart, so a hit or a falling bandit leaves beads and not a ribbon.  This is the cure, for that one
/// case and no other — <see cref="WreckTrail"/> is a LAYER-OWNED EMITTER that fires
/// <see cref="WreckIntervalFrameTime"/> apart along a SHOT-DOWN aircraft's path, from the moment it is killed until
/// it hits the ground, and stops.  It is ADDITIONAL: the kernel's own emitter puffs still spawn and are still
/// mirrored one for one, so the transition reads as the original's beads followed by the port's ribbon.
/// </para>
/// <para>
/// <b>The falling seam</b> — how the layer knows an object is a falling wreck, byte-cited both times:
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>A BANDIT — its ENGAGEMENT PHASE is 6</b> (<see cref="IsFallingWreck"/>).  It is the same byte
///   the renderer already reads per object to decide whether to draw its wheels
///   (<c>mov al,es:[bx+0x25]</c> @<c>image@0x2D9B8</c>, <see cref="Combat.EnemyGearState"/>) and the
///   byte <c>EngagementNodePass</c> dispatches the whole AI on (<c>[0xED61]</c>,
///   <c>image@0x04744</c>); phase 6 is <c>Case6 @image@0x04708</c>, whose body is "allocate the
///   destruction slot, and depart at the ground".  The layer also takes the OTHER kill path's
///   signature (the crater class + the <c>0x2540</c> prototype
///   <c>engagement_kill_finalize @image@0x0C36B</c> stamps).  Measured on mission 0 with
///   <c>--foe-hp 1</c>: living bandits are in phase <c>0x0B</c>/<c>0x0C</c>, the victim enters 6 on
///   the kill frame and holds it from 10,283 ft to the ground.
///   </description></item>
///   <item><description>
///   <b>THE PLAYER.</b>  <see cref="PlayerFate"/>'s <see cref="PlayerFatePhase.Destroyed"/> phase IS
///   the fall — the machine integrates its ballistic arc and publishes it into the player's world
///   object every step, and enters <see cref="PlayerFatePhase.Wreck"/> the frame it reaches the
///   ground.  So the trail runs for exactly that phase, whatever the cause put the player into it
///   (<see cref="PlayerFateCause.ShotDown"/>, <see cref="PlayerFateCause.Rammed"/> and
///   <see cref="PlayerFateCause.Ejected"/> all fall; a <see cref="PlayerFateCause.Crash"/> never
///   enters it, because the aeroplane is already on the ground).  The POSITION is read out of the
///   arena, where <c>MissionSession.PublishPlayerPose</c> puts it — the same place everything else
///   reads the wreck from.
///   </description></item>
/// </list>
/// <para>
/// <b>The trail's own budget.</b>  A wreck puff never evicts a mirrored one and vice versa: the two
/// have separate capacities (<see cref="Capacity"/> and <see cref="WreckCapacity"/>) over one
/// birth-ordered list, so a 30-second fall at 0.25 s apart cannot push the wreck COLUMN or the
/// damage BEADS out of the layer.  A wreck puff lives a kind-1 life (5 s × <see cref="LifeScale"/>),
/// so one falling aeroplane holds <c>15 s / 0.25 s = 60</c> of them at the defaults and the 512
/// budget covers eight simultaneous wrecks.
/// </para>
/// </remarks>
public sealed class SmokeTrail
{
    /// <summary>The default life multiplier.</summary>
    public const double DefaultLifeScale = 3.0;

    /// <summary>The default trailing fraction of a puff's life spent fading out.</summary>
    public const double DefaultFadeFraction = 0.25;

    /// <summary>How many presentation puffs are kept; the OLDEST is dropped when the layer is full.</summary>
    /// <remarks>
    /// 512 is generous: the kernel's four emitter rows can light at most one puff per row per
    /// interval, so even 135-second stationary puffs at 4-second intervals cannot fill it from the
    /// emitters alone (4 × 135 / 4 = 135).  Bursts of one-off puffs (near misses, ground impacts) can,
    /// which is what the eviction rule is for.
    /// </remarks>
    public const int DefaultCapacity = 512;

    /// <summary>
    /// The base of the PORT'S OWN wreck-trail kind bytes: a puff of kind <c>0x80 + c</c> is drawn
    /// with kind 1's shape and ramp and all three discs painted in palette index <c>c</c>
    /// (<c>CYAC.Port.Render.SmokeLook</c>'s labelled port arm).
    /// </summary>
    /// <remarks>
    /// The colour rides in the kind byte because <see cref="SmokePuffState"/> is the verified
    /// kernel's neighbourhood and <c>SceneInstance</c> carries no per-instance palette, so the whole
    /// addition lives in this file and in <c>SmokeLook</c>.  The original itself leaves every kind
    /// ≥ 5 alone (<c>image@0x0B389</c>), so no shipped puff can reach the arm.
    /// </remarks>
    public const byte WreckTrailKindBase = 0x80;

    /// <summary>The largest palette index <see cref="WreckTrailKindBase"/> can carry: <c>0x7F</c>.</summary>
    public const byte WreckTrailMaxColor = 0x7F;

    /// <summary>
    /// The default wreck-trail colour: palette <b>8</b>, the original's own dark grey
    /// (<c>0x2808</c>, kind 1's <c>image@0x0B3BC</c>).
    /// </summary>
    /// <remarks>
    /// The choice, judged on the same frame at 4× against sky and against ground, between three
    /// candidates: <b>0</b> (true black, <c>0,0,0</c>), <b>27</b> (the grey
    /// ramp's <c>15,15,15</c>) and <b>8</b> (the original's own <c>21,21,21</c>).  Against SKY all
    /// three read as black smoke and 0 is the most striking; against GROUND, 0 and 27 lose the
    /// plume wherever it crosses a dark scenery face — the puff and the hill become one
    /// undifferentiated dark mass, and the beads that make it read as SMOKE rather than as a smear
    /// disappear.  8 keeps them on both backgrounds, is still clearly a dark trail against blue,
    /// and is the colour the game itself paints a damaged aeroplane's trail (<c>0x2808</c>, kind
    /// 1).  So 8 is the default and <c>--wreck-smoke-color 0</c> is one flag away for anyone who
    /// wants it absolutely black.
    /// </remarks>
    public const byte DefaultWreckColorIndex = 8;

    /// <summary>
    /// The default wreck-trail cadence in seconds — one puff every 0.12 s.
    /// </summary>
    /// <remarks>
    /// The value in their <c>settings.json</c> was 0.05.
    /// <para>
    /// Their <c>settings.json</c> holds <b>0.12</b>, which is 31 frame-time ticks at 256/s.
    /// </para>
    /// </remarks>
    public const double DefaultWreckIntervalSeconds = 0.12;

    /// <summary>How many falling wrecks the layer will trail at once.</summary>
    public const int MaxWreckTrails = 16;

    /// <summary>The most trail puffs one wreck can bear in a single step (an interval below dt).</summary>
    public const int MaxPuffsPerStep = 16;

    /// <summary>
    /// The engagement PHASE of a shot-down aeroplane on its way down: <b>6</b>
    /// (<c>EngagementNodePass.Case6 @image@0x04708</c>, dispatched from <c>[0xED61]</c> at
    /// <c>image@0x04744</c>) — the phase whose body allocates the destruction slot and whose only
    /// exit is <c>engagement_slot_impact_and_depart @image@0x08BA6</c> at the ground.
    /// </summary>
    public const byte DeathPhase = 6;

    /// <summary>
    /// Where that phase byte lives in <c>s_engagement_state</c>: <c>+0x0D</c>
    /// (<see cref="Model.Combat.EngagementState.Phase"/>; the same byte
    /// <c>mesh_lod_prepare_gear_and_flame_state</c> reads as <c>object[+0x25]</c> at
    /// <c>image@0x2D9B8</c>).
    /// </summary>
    public const int EngagementPhaseOffset = 0x0D;

    /// <summary>The crater class a killed aeroplane is re-stamped with (<c>image@0x0C3B9</c>).</summary>
    public const ushort KilledClassRecord = 0x52A2;

    /// <summary>The prototype word a killed engagement block is re-stamped with (<c>image@0x0C3EA</c>).</summary>
    public const ushort KilledPrototypeRef = 0x2540;

    /// <summary><c>g_render_object_list_head [0x0096]</c> — the list the falling wrecks are found on.</summary>
    public const int RenderListHead = 0x0096;

    /// <summary>Frame-time ticks to one second (256 to the master frame, ≈ 1 s a frame).</summary>
    public const int FrameTimeTicksPerSecond = 256;

    /// <summary>
    /// The kind whose LIFETIME a wreck-trail puff borrows: <b>1</b>, the original's own damage trail
    /// (5 master frames, <c>image@0x0B1B6</c>) — so at <c>--smoke-life 3</c> a trail puff lives 15 s.
    /// </summary>
    public const byte TrailKind = 1;

    /// <summary>
    /// The emitter key the PLAYER's fall uses: <c>0xFFFF</c>, which is not a pool near offset (the
    /// arena's own records are far below it) and cannot collide with a bandit's.
    /// </summary>
    private const ushort PlayerKey = 0xFFFF;

    private readonly SlotShadow[] _shadows = new SlotShadow[SmokePuffTable.SlotCount];
    private readonly WreckEmitter[] _wrecks = new WreckEmitter[MaxWreckTrails];
    private readonly List<Puff> _puffs = [];
    private long _serial;
    private int _mirrorLive;
    private int _wreckLive;
    private double _fadeFraction = DefaultFadeFraction;
    private double _wreckIntervalSeconds = DefaultWreckIntervalSeconds;
    private int _wreckColorIndex = DefaultWreckColorIndex;

    /// <summary>Whether the layer bears anything at all; off is H23's look (the sim's own puffs).</summary>
    /// <remarks>
    /// <b>settable while the sortie is flying</b> (the <c>--smoke-trail</c> row of the port settings
    /// dialog, <c>PortSettings</c>).  Switching it OFF stops the layer BEARING: the slot shadows and the
    /// wreck emitters are retired and no new puff is born — but every puff already borne keeps flying,
    /// ageing and expiring, so the screen is never blanked.  Switching it ON again re-acquires whatever the
    /// 15 kernel slots hold at that moment, so the layer picks up the smoke that is on screen instead of
    /// waiting for the next spawn.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The multiplier on the original's per-kind lifetime
    /// (<see cref="SmokePuffTable.Lifetime"/>): 1.0 is the original's own duration, 3.0 the default.
    /// </summary>
    /// <remarks>
    /// <b>settable while the sortie is flying</b> (<c>--smoke-life</c>).  A puff KEEPS the life it
    /// was born with (<see cref="Puff.SpanFrameTime"/> is stamped at birth); only puffs born after
    /// the change get the new one.  Re-spanning the live ones was rejected: a puff already older
    /// than the shortened span would vanish on the spot, which is the one thing a mid-flight change
    /// must not do ("let the live ones expire").
    /// </remarks>
    public double LifeScale { get; set; } = DefaultLifeScale;

    /// <summary>
    /// The fraction of a puff's (stretched) life over which its opacity falls linearly to 0, so it
    /// does not pop out of existence.  0 reproduces the original's pop.
    /// </summary>
    /// <remarks>
    /// <b>settable while the sortie is flying</b> (<c>--smoke-fade</c>), and RE-STAMPED on every
    /// live puff the moment it changes.  The fade is a pure function of (age, span, fraction) —
    /// <see cref="FadeOpacity"/> — so re-stamping is exactly what the next <see cref="Step"/>
    /// would have computed; doing it in the setter is what makes the change visible on the FROZEN
    /// frame it is tuned on (the sim does not step behind the dialog).
    /// </remarks>
    public double FadeFraction
    {
        get => _fadeFraction;
        set
        {
            _fadeFraction = value;
            RestampFade();
        }
    }

    /// <summary>How many presentation puffs the layer will hold at once.</summary>
    public int Capacity { get; init; } = DefaultCapacity;

    /// <summary>
    /// Whether H23's SIZE ramp runs over the stretched life (a slower ramp that still ends at the
    /// law's own 200 / 280 / 240 world units) or — the DEFAULT — over the ORIGINAL's own span, in
    /// which case a puff reaches full size in the original's own 5 / 25 / 45 s and then HOLDS it for
    /// the rest of its longer life.
    /// </summary>
    /// <remarks>
    /// <b>Labelled departure.</b> The stretched ramp was the obvious default;
    /// photographed side by side it makes a puff SMALLER at any given age than the
    /// original's own would have been — 25 s into a 75-second life is a third of the ramp, not all of
    /// it — so the layer reads longer but thinner, which is the opposite of "a bit
    /// bigger".  The original ramp is therefore the default and the stretched one the knob
    /// (<c>--smoke-ramp stretched</c>); the LIFE multiplier is unaffected either way, and both end at
    /// the same maximum radii.
    /// </remarks>
    public bool StretchRamp { get; init; }

    // ------------------------------------------------------------------ H28, the wreck trail

    /// <summary>
    /// Whether a SHOT-DOWN aircraft trails the layer's own dark puffs all the way down
    /// (<c>--wreck-smoke</c>).  Off leaves H24 exactly as it was: the kernel's emitter beads and
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// <b>settable while the sortie is flying</b> (<c>--wreck-smoke</c>).  OFF retires every emitter
    /// and bears nothing more; the trail puffs already in the air keep falling and expire on their
    /// own.  ON gives a wreck that is still falling a fresh emitter, which bears its first puff at
    /// once.
    /// </remarks>
    public bool WreckTrail { get; set; } = true;

    /// <summary>
    /// Where an object is being DRAWN, in feet, when that differs from where the sim holds it.
    /// </summary>
    /// <param name="objectRef">The pool object.</param>
    /// <param name="xFeet">Its presented X, feet.</param>
    /// <param name="yFeet">Its presented Y, feet.</param>
    /// <param name="zFeet">Its presented Z, feet.</param>
    /// <returns>True when the presenter knows the object; false leaves the sim's own position in force.</returns>
    public delegate bool PresentedPositionLookup(ushort objectRef, out double xFeet, out double yFeet, out double zFeet);

    /// <summary>
    /// The host's pose presenter, consulted for every wreck puff's birthplace.
    /// </summary>
    /// <remarks>
    /// The engagement queue integrates a far object in coarse steps (a held position for ~2 s, then
    /// a jump of several hundred feet — measured, see <c>CYAC.Port.Render.PoseSmoother</c>), and the
    /// wreck emitter samples the arena every <see cref="WreckIntervalSeconds"/>: without this hook
    /// every puff of a 2-s hold was borne at ONE point and the column broke into clumps.  The
    /// renderer dead-reckons the wreck between the sim's updates; the emitter now bears its puffs
    /// where the wreck is DRAWN, so the trail is continuous.  Null, or a lookup that answers false,
    /// bears the puff at the sim's position as before.  Presentation-only either way: the layer's
    /// puffs are on neither kernel surface.
    /// </remarks>
    public PresentedPositionLookup? PresentedPosition { get; set; }

    /// <summary>The cadence, in seconds, at which a falling wreck bears a trail puff.</summary>
    /// <remarks>
    /// <b>settable while the sortie is flying</b> (<c>--wreck-smoke-interval</c>).  It governs the
    /// NEXT puff, never the ones already in the air (their spacing is history, written in world
    /// positions).  The setter shortens every live emitter's countdown to the new interval so a
    /// change from 4 s to 0.1 s takes hold within 0.1 s instead of within 4 s.
    /// </remarks>
    public double WreckIntervalSeconds
    {
        get => _wreckIntervalSeconds;
        set
        {
            _wreckIntervalSeconds = value;
            RetimeWreckEmitters();
        }
    }

    /// <summary>
    /// The palette index all three of a wreck puff's discs are painted in — the black puffs of a
    /// falling wreck (<see cref="DefaultWreckColorIndex"/>; 0…<see cref="WreckTrailMaxColor"/>).
    /// </summary>
    /// <remarks>
    /// <b>settable while the sortie is flying</b> (<c>--wreck-smoke-color</c>), and the setter
    /// RE-PAINTS every live wreck puff: the colour rides in the puff's <see cref="Puff.Kind"/> byte
    /// (<see cref="WreckKind"/> = <see cref="WreckTrailKindBase"/> + the index), and a colour is a
    /// LOOK, not a birth fact — so the whole column changes at once, which is what makes tuning it
    /// on a frozen frame work.
    /// </remarks>
    public int WreckColorIndex
    {
        get => _wreckColorIndex;
        set
        {
            _wreckColorIndex = value;
            RepaintWreckPuffs();
        }
    }

    /// <summary>
    /// A multiplier on a wreck puff's grown radii (<c>--wreck-smoke-size</c>, 1 = H24's own ramp).
    /// </summary>
    /// <remarks>
    /// The layer only CARRIES it: the renderer's law (<c>SmokeLook</c>) is handed the kind, the age
    /// and the span and nothing else, so the host applies this as the instance's world-scale
    /// override in <c>FlightRasterizer.AddSmoke</c> — mathematically the same thing as multiplying
    /// the three ramped radii, since a disc's drawn radius is <c>radius × scale × projection</c>.
    /// <para>
    /// <b>settable while the sortie is flying</b> (<c>--wreck-smoke-size</c>).  It needs no re-stamp
    /// at all: the host reads it at DRAW time, so every live wreck puff changes size on the very
    /// next frame, frozen or not.
    /// </para>
    /// </remarks>
    public double WreckSizeScale { get; set; } = 1.0;

    /// <summary>How many WRECK-TRAIL puffs the layer holds, separately from <see cref="Capacity"/>.</summary>
    /// <remarks>
    /// The two budgets are separate so a long fall can never evict the wreck COLUMN or the damage BEADS the
    /// layer is mirroring: at the defaults one falling aeroplane holds 15 s / 0.25 s = 60 trail puffs, so 512
    /// covers eight simultaneous wrecks and the mirror's own 512 is untouched.
    /// </remarks>
    public int WreckCapacity { get; init; } = DefaultCapacity;

    /// <summary>
    /// The PLAYER's fate machine, when the host has one: its
    /// <see cref="PlayerFatePhase.Destroyed"/> phase is the player's own fall, and it is the only
    /// place that fact exists (the player's pool object is never re-classed).  Null in a headless
    /// test or with <c>--fate off</c>, and the player then simply has no trail.
    /// </summary>
    public PlayerFate? Fate { get; init; }

    /// <summary>The cadence in the original's frame-time ticks (256 to the second).</summary>
    public int WreckIntervalFrameTime => Math.Max(
        1,
        (int)Math.Round(Math.Max(0.0, WreckIntervalSeconds) * FrameTimeTicksPerSecond));

    /// <summary>The kind byte a wreck-trail puff is borne with: <c>0x80 + the colour index</c>.</summary>
    public byte WreckKind => (byte)(
        WreckTrailKindBase + Math.Clamp(WreckColorIndex, 0, WreckTrailMaxColor));

    /// <summary>The live presentation puffs, oldest first.</summary>
    public IReadOnlyList<Puff> Puffs => _puffs;

    /// <summary>How many of them are WRECK-TRAIL puffs.</summary>
    public int WreckPuffsLive => _wreckLive;

    /// <summary>How many of the kernel's 15 slots held a live puff at the last <see cref="Step"/>.</summary>
    public int SimPuffsLive { get; private set; }

    /// <summary>The running census.</summary>
    public SmokeTrailCensus Census { get; } = new();

    /// <summary>One presentation puff — the layer's own object, never the sim's.</summary>
    /// <param name="SimX">World X in SIM units (world × 256), as the pool stores it.</param>
    /// <param name="SimY">World Y (up), sim units.</param>
    /// <param name="SimZ">World Z, sim units.</param>
    /// <param name="SpreadAccumulator">
    /// The puff's own copy of <c>slot[+2]</c>, the horizontal velocity the physics step grows and caps.
    /// </param>
    /// <param name="HeadingBam">The tumbling <c>+0x12</c> euler word, in BAM units of the 2,880 circle.</param>
    /// <param name="PitchBam">The tumbling <c>+0x14</c> word.</param>
    /// <param name="RollBam">The tumbling <c>+0x16</c> word.</param>
    /// <param name="Kind">The slot's <c>+0x08</c> kind byte, which picks the colours and the lifetime.</param>
    /// <param name="AgeFrameTime">Its age in frame-time ticks (256 to the master frame).</param>
    /// <param name="SpanFrameTime">Its STRETCHED life in frame-time ticks.</param>
    /// <param name="RampSpanFrameTime">
    /// The span the SIZE ramp runs over — the ORIGINAL's own span by default, so the puff reaches the
    /// full 200 / 280 / 240 world units in the original's own time and then HOLDS there for the rest
    /// of its longer life, or <see cref="SpanFrameTime"/> under <see cref="SmokeTrail.StretchRamp"/>
    /// (a slower ramp ending at the same radii).
    /// </param>
    /// <param name="Opacity">The fade multiplier for the instance, 1 until the fade begins.</param>
    /// <param name="SlotOffset">
    /// The ported slot whose spawn it shadows (diagnostics only), or <c>-1</c> for a
    /// <see cref="Puff.IsWreckTrail"/> puff, which shadows no slot at all.
    /// </param>
    /// <param name="Serial">A running id, so a census can follow one puff.</param>
    /// <param name="IsWreckTrail">
    /// True when the layer bore this puff ITSELF, off a falling wreck, rather than mirroring a kernel spawn.
    /// It decides which capacity the puff is charged to and whether the host gives its instance the
    /// <c>--wreck-smoke-size</c> scale.
    /// </param>
    public readonly record struct Puff(
        double SimX,
        double SimY,
        double SimZ,
        double SpreadAccumulator,
        double HeadingBam,
        double PitchBam,
        double RollBam,
        byte Kind,
        int AgeFrameTime,
        int SpanFrameTime,
        int RampSpanFrameTime,
        double Opacity,
        int SlotOffset,
        long Serial,
        bool IsWreckTrail = false)
    {
        /// <summary>World X, world units (feet).</summary>
        public double X => SimX / 256.0;

        /// <summary>World Y (up), world units.</summary>
        public double Y => SimY / 256.0;

        /// <summary>World Z, world units.</summary>
        public double Z => SimZ / 256.0;

        /// <summary>The heading in degrees, the renderer's convention.</summary>
        public double HeadingDegrees => HeadingBam * CombatSceneObjects.DegreesPerUnit;

        /// <summary>The pitch in degrees.</summary>
        public double PitchDegrees => PitchBam * CombatSceneObjects.DegreesPerUnit;

        /// <summary>The roll in degrees.</summary>
        public double RollDegrees => RollBam * CombatSceneObjects.DegreesPerUnit;

        /// <summary>
        /// What the renderer's size-and-colour law (<c>CYAC.Port.Render.SmokeLook</c>) is handed: the
        /// kind, the age and the RAMP span (<see cref="RampSpanFrameTime"/>).  The age is clamped to
        /// that span, so a puff whose ramp is the original's holds its full radius for the rest of its
        /// longer life instead of growing past the law's own 200 / 280 / 240 world units.
        /// </summary>
        public SmokePuffState State =>
            new(Kind, Math.Min(AgeFrameTime, RampSpanFrameTime), RampSpanFrameTime);

        /// <summary>The age as a fraction of the span.</summary>
        public double LifeFraction =>
            SpanFrameTime > 0 ? (double)AgeFrameTime / SpanFrameTime : 0.0;
    }

    /// <summary>What one kernel slot held last frame, and where its puff was going.</summary>
    private struct SlotShadow
    {
        public bool Live;
        public ushort ObjectRef;
        public ushort Birth;
        public ushort Expiry;
        public byte Kind;
        public int Spread;
        public CombatPosition Ghost;
    }

    /// <summary>One falling wreck the layer is trailing, and how long until its next puff.</summary>
    private struct WreckEmitter
    {
        /// <summary>The wreck's pool object, or <see cref="PlayerKey"/> for the player's own.</summary>
        public ushort Key;

        /// <summary>Whether the slot is in use.</summary>
        public bool Live;

        /// <summary>Whether this frame's sweep still found the wreck falling.</summary>
        public bool Seen;

        /// <summary>Frame-time ticks until the next puff; ≤ 0 fires one now.</summary>
        public int TicksToNext;
    }

    /// <summary>
    /// Advances the layer by one frame.  Call AFTER <see cref="MissionSession.Step"/>, like
    /// <see cref="GunneryRounds.Step"/>: the kernel's effect ticks have run, so every puff the frame
    /// lit is in a slot and every live puff has taken its own step.
    /// </summary>
    /// <param name="mission">The mission whose combat state is READ (and never written).</param>
    public void Step(MissionSession mission)
    {
        ArgumentNullException.ThrowIfNull(mission);
        Step(mission.Combat.Registers, mission.Combat.Arena);
    }

    /// <summary>
    /// Advances the layer by one frame over a bare combat state — what <see cref="Step(MissionSession)"/>
    /// does, without the mission wrapper, so the law can be driven in lockstep with
    /// <see cref="SmokePuffTable.PerFrameStep"/> in a test.
    /// </summary>
    /// <param name="registers">The combat register file (READ only).</param>
    /// <param name="arena">The pool arena (READ only).</param>
    public void Step(CombatRegisters registers, PoolArena arena)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);
        int dt = unchecked((short)registers.Word(MissionSession.SceneFrameDt));

        // The order matters: the puffs already borne take THIS frame's step first, then the frame's
        // new spawns are read in at age 0 sitting where the sim's own step just left them — so a
        // newborn is never stepped twice.
        //
        // FlyPuffs runs whether the layer is Enabled or not.  Switching the layer off mid-sortie
        // must stop it BEARING, not delete what it has borne: the live puffs go on ageing, fading
        // and expiring exactly as they would have.  With the layer off from cold start the list is
        // empty and this is a no-op, so `--smoke-trail off` is unchanged.
        FlyPuffs(dt);
        if (Enabled)
        {
            DetectSpawns(registers, arena, dt);

            // And then the layer's OWN emitter, for whatever is falling out of the sky. It runs
            // last so a wreck puff is the newest thing in the list, which is also what makes the
            // two capacities independent of each other.
            EmitWreckTrails(registers, arena, dt);
        }
        else
        {
            // Nothing is being tracked while the layer is off, so nothing stale may survive into
            // the moment it is switched back on: the shadows and the emitters are dropped, and the
            // next enabled frame re-acquires the slots and the falling wrecks from scratch.
            Array.Clear(_shadows);
            Array.Clear(_wrecks);
            SimPuffsLive = 0;
        }

        Census.LivePuffs = _puffs.Count;
        Census.LiveWreckPuffs = _wreckLive;
    }

    /// <summary>Forgets every puff and every slot shadow — the session was reopened.</summary>
    public void Reset()
    {
        Array.Clear(_shadows);
        Array.Clear(_wrecks);
        _puffs.Clear();
        _mirrorLive = 0;
        _wreckLive = 0;
        SimPuffsLive = 0;
        Census.LivePuffs = 0;
        Census.LiveWreckPuffs = 0;
    }

    /// <summary>
    /// <b>THE FALLING SEAM</b>: whether this pool object is a shot-down aeroplane that has not
    /// reached the ground yet.
    /// </summary>
    /// <param name="arena">The pool arena.</param>
    /// <param name="objectRef">The object's near offset.</param>
    /// <returns>True for a falling wreck.</returns>
    /// <remarks>
    /// <para>
    /// The object must carry an engagement block (flag bit 11), and then EITHER of the two states a
    /// kill can leave it in:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   <b><see cref="DeathPhase"/> — <c>block[+0x0D] == 6</c>, the state the port's own bandit
    ///   kills actually reach.</b>  A close-range air kill does NOT run
    ///   <c>engagement_kill_finalize</c> at all: <c>weapon_fire_combat_loop</c>'s close-range arm
    ///   (<c>image@0x0C011..0x0C069</c>) advances the engagement VM to node 5 and RETURNS
    ///   (<c>locals.KillFinalized = false</c> @<c>image@0x0C066</c>), and the script drops the
    ///   engagement into phase 6.  <c>EngagementNodePass</c>'s dispatch
    ///   (<c>cmp/ja</c> @<c>image@0x04744</c> on <c>[0xED61]</c>) sends that phase to
    ///   <c>Case6 @image@0x04708</c>, whose whole body is "allocate the destruction slot
    ///   (<c>enemy_spawn_with_angle_pos_init</c> → <c>slot_alloc_and_activate</c>
    ///   @<c>image@0x08AF9</c>) and, when the fire position's altitude has reached the class
    ///   record's own ground clearance, <c>engagement_slot_impact_and_depart @image@0x08BA6</c>".
    ///   So phase 6 IS "dying, and still in the air", it is the phase the ejecting pilot comes out
    ///   of, and its only exit is the ground.  Measured, mission 0 with <c>--foe-hp 1</c>: every
    ///   living bandit sits in phase <c>0x0B</c>/<c>0x0C</c>, and the victim goes to 6 on the kill
    ///   frame and stays there from 10,283 ft down to the ground, ~1,700 frames later.
    ///   </description></item>
    ///   <item><description>
    ///   <b><see cref="KilledClassRecord"/> + <see cref="KilledPrototypeRef"/></b> — the OTHER kill
    ///   path, <c>engagement_kill_finalize @image@0x0C36B</c> (the arm a non-close-range kill takes,
    ///   <c>image@0x0C071</c>), which re-stamps the victim's own object with the crater class
    ///   (<c>mov es:[bx],0x52a2</c> @<c>image@0x0C3B9</c>) and its block's prototype with the KILLED
    ///   prototype (<c>mov es:[di],0x2540</c> @<c>image@0x0C3EA</c>) while
    ///   <c>and byte es:[si+3],0xca</c> (<c>image@0x0C3E0</c>) keeps flag bit 11.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Both END THEMSELVES: the departure clears the object's ACTIVE bit and takes it out of the
    /// render list, so the trail stops at impact with no altitude test of the layer's own.  And the
    /// pool-mounted craters of <see cref="Combat.Effects.CraterPool"/> carry NO engagement block, so
    /// a ground mark is never mistaken for a wreck.
    /// </para>
    /// </remarks>
    public static bool IsFallingWreck(PoolArena arena, ushort objectRef)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (objectRef == 0 || !arena.Covers(objectRef, 0x18))
        {
            return false;
        }

        CombatObjectView view = new CombatObjectView(arena, objectRef);
        if (!view.CarriesEngagement || !arena.Covers(view.EngagementBlockRef, 0x0E))
        {
            return false;
        }

        if (arena.Byte((ushort)(view.EngagementBlockRef + EngagementPhaseOffset)) == DeathPhase)
        {
            return true;
        }

        return view.ClassRef == KilledClassRecord
            && view.EngagementPrototypeRef == KilledPrototypeRef;
    }

    /// <summary>
    /// The STRETCHED life of a puff of one kind, in frame-time ticks: the original's own lifetime
    /// (<see cref="SmokePuffTable.Lifetime"/>, in master frames) × 256 × <paramref name="lifeScale"/>.
    /// </summary>
    /// <param name="kind">The puff's kind byte.</param>
    /// <param name="lifeScale">The life multiplier; 1 is the original's duration.</param>
    public static int SpanFrameTime(byte kind, double lifeScale) => Math.Max(
        1,
        (int)Math.Round(SmokePuffTable.Lifetime(kind) * 256.0 * Math.Max(0.0, lifeScale)));

    /// <summary>
    /// The fade multiplier for a puff: 1 until the last <paramref name="fadeFraction"/> of its life,
    /// then falling linearly to 0 at its end.  The port's own; the original pops.
    /// </summary>
    /// <param name="ageFrameTime">The puff's age in frame-time ticks.</param>
    /// <param name="spanFrameTime">Its whole (stretched) life.</param>
    /// <param name="fadeFraction">The trailing fraction of the life the fade occupies, 0…1.</param>
    public static double FadeOpacity(int ageFrameTime, int spanFrameTime, double fadeFraction)
    {
        double fade = Math.Clamp(fadeFraction, 0.0, 1.0);
        if (fade <= 0.0 || spanFrameTime <= 0)
        {
            return 1.0;
        }

        double start = spanFrameTime * (1.0 - fade);
        if (ageFrameTime <= start)
        {
            return 1.0;
        }

        return Math.Clamp(1.0 - ((ageFrameTime - start) / (spanFrameTime - start)), 0.0, 1.0);
    }

    /// <summary>
    /// How many puffs an emitter row of a given interval keeps alive at once — the COLUMN LENGTH in
    /// puffs.  The row's interval is in scaled frames (<c>g_frame_count_scaled [0xF0D0]</c> =
    /// accumulator &gt;&gt; 6, so one scaled frame is 64 frame-time ticks).
    /// </summary>
    /// <param name="kind">The kind the row emits.</param>
    /// <param name="intervalScaledFrames">Its <c>+0x11</c> interval; 0 takes the attach default.</param>
    /// <param name="lifeScale">The layer's life multiplier; 1 gives the original's column.</param>
    public static int ColumnPuffs(byte kind, int intervalScaledFrames, double lifeScale)
    {
        int interval = intervalScaledFrames != 0
            ? intervalScaledFrames
            : EffectEmitterTable.DefaultInterval;
        return SpanFrameTime(kind, lifeScale) / Math.Max(1, interval * 64);
    }

    // ------------------------------------------------------------------ the spawn detector

    private void DetectSpawns(CombatRegisters registers, PoolArena arena, int dt)
    {
        int live = 0;
        foreach (int slot in SmokePuffTable.Slots())
        {
            ref SlotShadow shadow = ref _shadows[(SmokePuffTable.LastSlot - slot) / SmokePuffTable.SlotStride];
            ushort obj = registers.Word(slot);
            if (obj == 0 || !arena.Covers(obj, 0x18) || (arena.Byte((ushort)(obj + 2)) & 1) == 0)
            {
                shadow.Live = false;                                     // image@0x0B238 — not drawn
                continue;
            }

            live++;
            CombatObjectView view = new CombatObjectView(arena, obj);
            CombatPosition position = view.Position;
            ushort birth = registers.Word(slot + 4);
            ushort expiry = registers.Word(slot + 6);
            byte kind = registers.Byte(slot + 8);
            int spread = registers.Word(slot + 2);

            bool fresh = !shadow.Live
                || shadow.ObjectRef != obj
                || shadow.Birth != birth
                || shadow.Expiry != expiry
                || shadow.Kind != kind
                // Allocation writes slot[+2] = 0 (image@0x0B19A) and the step only ever grows it, so
                // a fall in the accumulator is an allocation into a slot that never went idle — the
                // LRU eviction (image@0x0B137).
                || spread < shadow.Spread
                || position != shadow.Ghost;

            shadow.Live = true;
            shadow.ObjectRef = obj;
            shadow.Birth = birth;
            shadow.Expiry = expiry;
            shadow.Kind = kind;
            shadow.Spread = spread;
            shadow.Ghost = Ghost(position, spread, kind, dt);

            if (fresh)
            {
                Census.SpawnsSeen++;
                Bear(arena, view, position, spread, kind, slot);
            }
        }

        SimPuffsLive = live;
    }

    /// <summary>
    /// Where <see cref="SmokePuffTable.PerFrameStep"/> will put this puff on the NEXT frame: the
    /// accumulator it is holding now moves it in X, the constant rise moves it in Y, and a kind-0
    /// puff does not move at all (<c>image@0x0B25C..0x0B2A9</c>).
    /// </summary>
    private static CombatPosition Ghost(CombatPosition position, int spread, byte kind, int dt)
    {
        if (kind == 0)
        {
            return position;
        }

        return new CombatPosition(
            unchecked(position.X + SmokePuffTable.MulShr8(unchecked((short)spread), (short)dt)),
            unchecked(position.Y + SmokePuffTable.MulShr8(unchecked((short)SmokePuffTable.RiseRate), (short)dt)),
            position.Z);
    }

    private void Bear(
        PoolArena arena, CombatObjectView view, CombatPosition position, int spread, byte kind, int slot)
    {
        int span = SpanFrameTime(kind, LifeScale);
        _puffs.Add(new Puff(
            position.X,
            position.Y,
            position.Z,
            spread,
            arena.Word((ushort)(view.Offset + 0x12)),
            arena.Word((ushort)(view.Offset + 0x14)),
            arena.Word((ushort)(view.Offset + 0x16)),
            kind,
            AgeFrameTime: 0,
            span,
            RampSpanFrameTime: StretchRamp ? span : SpanFrameTime(kind, 1.0),
            Opacity: FadeOpacity(0, span, FadeFraction),
            slot,
            ++_serial));
        _mirrorLive++;
        Census.PuffsBorn++;
        Evict(wreck: false, Capacity);
    }

    // ------------------------------------------------------------------ H28, the wreck emitter

    /// <summary>
    /// Bears one trail puff per falling wreck per <see cref="WreckIntervalFrameTime"/>, and retires
    /// the emitter the frame its wreck stops falling (it has hit the ground and departed, or the
    /// player's fate machine has left <see cref="PlayerFatePhase.Destroyed"/>).
    /// </summary>
    private void EmitWreckTrails(CombatRegisters registers, PoolArena arena, int dt)
    {
        if (!WreckTrail)
        {
            // --wreck-smoke off mid-sortie: retire the emitters so nothing more is borne (and so
            // a wreck that hit the ground while the trail was off cannot hold a slot). The puffs
            // already in the air are NOT touched; FlyPuffs expires them in their own time.
            // Switching it back on gives a still-falling wreck a fresh emitter, whose
            // TicksToNext = 0 bears a puff on that very frame.
            Array.Clear(_wrecks);
            return;
        }

        for (int i = 0; i < _wrecks.Length; i++)
        {
            _wrecks[i].Seen = false;
        }

        // THE PLAYER — the fate machine's Destroyed phase IS his fall (PlayerFate.Advance), and
        // the pose it publishes every step is in the arena where everything else reads it.
        if (Fate is { Enabled: true, Passive: false, Phase: PlayerFatePhase.Destroyed })
        {
            ushort player = registers.PlayerObjectRef;
            if (player != 0 && arena.Covers(player, 0x18))
            {
                Trail(PlayerKey, arena, player, dt);
            }
        }

        // THE BANDITS — the render list, exactly as CombatSceneObjects.Live walks it
        // (g_render_object_list_head [0x0096] through each record's +0x04), taking the objects
        // engagement_kill_finalize has stamped and the departure has not yet retired.
        ushort cursor = registers.Word(RenderListHead);
        for (int visited = 0; cursor != 0 && visited < 512; visited++)
        {
            if (!arena.Covers(cursor, 0x18))
            {
                break;
            }

            ushort next = arena.Word((ushort)(cursor + 0x04));
            CombatObjectView view = new CombatObjectView(arena, cursor);
            if ((view.Flags & CombatSceneObjects.ActiveFlag) != 0 && IsFallingWreck(arena, cursor))
            {
                Trail(cursor, arena, cursor, dt);
            }

            cursor = next;
        }

        for (int i = 0; i < _wrecks.Length; i++)
        {
            if (_wrecks[i].Live && !_wrecks[i].Seen)
            {
                _wrecks[i] = default;                                    // it hit the ground
            }
        }
    }

    /// <summary>Runs one falling wreck's emitter for this frame.</summary>
    private void Trail(ushort key, PoolArena arena, ushort objectRef, int dt)
    {
        int index = -1;
        int free = -1;
        for (int i = 0; i < _wrecks.Length; i++)
        {
            if (_wrecks[i].Live && _wrecks[i].Key == key)
            {
                index = i;
                break;
            }

            if (!_wrecks[i].Live && free < 0)
            {
                free = i;
            }
        }

        if (index < 0)
        {
            if (free < 0)
            {
                return;                                                  // more than MaxWreckTrails
            }

            // A new wreck: TicksToNext = 0 puts the FIRST puff on the shot-down frame itself.
            index = free;
            _wrecks[index] = new WreckEmitter { Key = key, Live = true, TicksToNext = 0 };
            Census.WreckTrailsSeen++;
        }

        _wrecks[index].Seen = true;
        _wrecks[index].TicksToNext -= dt;

        int interval = WreckIntervalFrameTime;

        // The guard is for an interval SHORTER than the step: a knob of 0 would otherwise ask for an
        // unbounded number of puffs at one point, which is not a trail.
        for (int burst = 0; burst < MaxPuffsPerStep && _wrecks[index].TicksToNext <= 0; burst++)
        {
            BearWreckPuff(arena, objectRef);
            _wrecks[index].TicksToNext += interval;
        }
    }

    /// <summary>
    /// Bears ONE wreck-trail puff where the wreck is now: the layer's own puff, flying H24's law
    /// with a kind-1 life and the wreck kind byte's palette index
    /// (<see cref="WreckTrailKindBase"/>).
    /// </summary>
    private void BearWreckPuff(PoolArena arena, ushort objectRef)
    {
        CombatObjectView view = new CombatObjectView(arena, objectRef);
        CombatPosition position = view.Position;
        double simX = position.X, simY = position.Y, simZ = position.Z;
        if (PresentedPosition is { } presented
            && presented(objectRef, out double xFeet, out double yFeet, out double zFeet))
        {
            simX = xFeet * 256.0;
            simY = yFeet * 256.0;
            simZ = zFeet * 256.0;
        }

        // The LIFE is a damage-trail puff's — kind 1, 5 s × LifeScale — because that is what the
        // original's own trail behind a hit aeroplane is; only the colour and the cadence deviate.
        int span = SpanFrameTime(TrailKind, LifeScale);
        _puffs.Add(new Puff(
            simX,
            simY,
            simZ,
            SpreadAccumulator: 0,                                        // allocation writes slot[+2] = 0
            arena.Word((ushort)(objectRef + 0x12)),
            arena.Word((ushort)(objectRef + 0x14)),
            arena.Word((ushort)(objectRef + 0x16)),
            WreckKind,
            AgeFrameTime: 0,
            span,
            RampSpanFrameTime: StretchRamp ? span : SpanFrameTime(TrailKind, 1.0),
            Opacity: FadeOpacity(0, span, FadeFraction),
            SlotOffset: -1,
            ++_serial,
            IsWreckTrail: true));
        _wreckLive++;
        Census.WreckPuffsBorn++;
        Evict(wreck: true, WreckCapacity);
    }

    /// <summary>
    /// Drops the OLDEST puff of one origin while that origin is over its own capacity — so a long
    /// fall's trail can never evict a mirrored column puff, nor the other way round.
    /// </summary>
    private void Evict(bool wreck, int capacity)
    {
        int limit = Math.Max(1, capacity);
        while ((wreck ? _wreckLive : _mirrorLive) > limit)
        {
            for (int i = 0; i < _puffs.Count; i++)
            {
                if (_puffs[i].IsWreckTrail != wreck)
                {
                    continue;
                }

                _puffs.RemoveAt(i);
                if (wreck)
                {
                    _wreckLive--;
                    Census.WreckPuffsEvicted++;
                }
                else
                {
                    _mirrorLive--;
                    Census.PuffsEvicted++;
                }

                break;
            }
        }
    }

    // ------------------------------------------------------ M2b, the live-change re-stamps

    /// <summary>
    /// Recomputes every live puff's <see cref="Puff.Opacity"/> for the fade fraction now in force —
    /// what the next <see cref="Step"/> would have computed, done at once so the change is visible
    /// on a FROZEN frame.
    /// </summary>
    private void RestampFade()
    {
        for (int i = 0; i < _puffs.Count; i++)
        {
            Puff puff = _puffs[i];
            _puffs[i] = puff with
            {
                Opacity = FadeOpacity(puff.AgeFrameTime, puff.SpanFrameTime, _fadeFraction),
            };
        }
    }

    /// <summary>
    /// Re-paints every live WRECK-TRAIL puff in the colour now in force.  A mirrored puff is left
    /// alone: its kind is the ported slot's own <c>+0x08</c> byte and is not the port's to change.
    /// </summary>
    private void RepaintWreckPuffs()
    {
        byte kind = WreckKind;
        for (int i = 0; i < _puffs.Count; i++)
        {
            if (_puffs[i].IsWreckTrail)
            {
                _puffs[i] = _puffs[i] with { Kind = kind };
            }
        }
    }

    /// <summary>
    /// Shortens every live emitter's countdown to the interval now in force, so a shortened cadence
    /// takes hold within one NEW interval instead of within one old one.
    /// </summary>
    private void RetimeWreckEmitters()
    {
        int interval = WreckIntervalFrameTime;
        for (int i = 0; i < _wrecks.Length; i++)
        {
            if (_wrecks[i].Live && _wrecks[i].TicksToNext > interval)
            {
                _wrecks[i].TicksToNext = interval;
            }
        }
    }

    // ------------------------------------------------------------------ the flight

    private void FlyPuffs(int dt)
    {
        for (int i = _puffs.Count - 1; i >= 0; i--)
        {
            Puff puff = _puffs[i];
            int age = puff.AgeFrameTime + dt;
            if (age >= puff.SpanFrameTime)
            {
                _puffs.RemoveAt(i);
                if (puff.IsWreckTrail)
                {
                    _wreckLive--;
                    Census.WreckPuffsExpired++;
                }
                else
                {
                    _mirrorLive--;
                    Census.PuffsExpired++;
                }

                continue;
            }

            double x = puff.SimX;
            double y = puff.SimY;
            double accumulator = puff.SpreadAccumulator;
            if (puff.Kind != 0)
            {
                // image@0x0B25C..0x0B2A9 — the accumulator moves X, the constant 0x1C00 moves Y, and
                // the accumulator then grows by 0x200 up to its 0x1400 cap.  In double, so a long
                // trail does not accumulate the 16-bit product's truncation 100,000 times.
                x += accumulator * dt / 256.0;
                y += SmokePuffTable.RiseRate * dt / 256.0;
                accumulator = Math.Min(
                    SmokePuffTable.SpreadCap, accumulator + (SmokePuffTable.SpreadGrowth * dt / 256.0));
            }

            _puffs[i] = puff with
            {
                SimX = x,
                SimY = y,
                SpreadAccumulator = accumulator,
                // image@0x0B2AB..0x0B2E9 — every kind tumbles, and the step is NOT scaled by dt.
                HeadingBam = WrapBam(puff.HeadingBam + SmokePuffTable.TumbleStep),
                PitchBam = WrapBam(puff.PitchBam + SmokePuffTable.TumbleStep),
                RollBam = WrapBam(puff.RollBam + SmokePuffTable.TumbleStep),
                AgeFrameTime = age,
                Opacity = FadeOpacity(age, puff.SpanFrameTime, FadeFraction),
            };
        }
    }

    /// <summary>
    /// <c>angle_wrap_bam @image@0x18410</c> in <see langword="double"/>: fold an angle into
    /// <c>[0, 0x0B40)</c>.
    /// </summary>
    /// <param name="angle">The unwrapped angle in BAM units.</param>
    public static double WrapBam(double angle)
    {
        double wrapped = angle % SmokePuffTable.BamCircle;
        return wrapped < 0.0 ? wrapped + SmokePuffTable.BamCircle : wrapped;
    }
}

/// <summary>What the smoke layer did.</summary>
public sealed class SmokeTrailCensus
{
    /// <summary>Kernel puff spawns the detector saw (including LRU recycling).</summary>
    public long SpawnsSeen { get; internal set; }

    /// <summary>Presentation puffs borne.</summary>
    public long PuffsBorn { get; internal set; }

    /// <summary>Presentation puffs that reached the end of their stretched life.</summary>
    public long PuffsExpired { get; internal set; }

    /// <summary>Presentation puffs dropped because the layer was full.</summary>
    public long PuffsEvicted { get; internal set; }

    /// <summary>Presentation puffs alive after the last step.</summary>
    public int LivePuffs { get; internal set; }

    /// <summary>Falling wrecks the layer-owned emitter has picked up.</summary>
    public long WreckTrailsSeen { get; internal set; }

    /// <summary>wreck-trail puffs the layer bore ITSELF (no kernel spawn behind them).</summary>
    public long WreckPuffsBorn { get; internal set; }

    /// <summary>wreck-trail puffs that reached the end of their life.</summary>
    public long WreckPuffsExpired { get; internal set; }

    /// <summary>wreck-trail puffs dropped because the TRAIL's own budget was full.</summary>
    public long WreckPuffsEvicted { get; internal set; }

    /// <summary>wreck-trail puffs alive after the last step.</summary>
    public int LiveWreckPuffs { get; internal set; }
}
