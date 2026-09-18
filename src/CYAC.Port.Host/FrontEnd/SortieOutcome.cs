using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Mission;
using CYAC.Port.Core.Sim.Session;
using CYAC.Port.Host.Sim;
using CYAC.Port.Host.Stats;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// What a finished sortie came to, kept for the front end after the flight is gone.
/// </summary>
/// <param name="Choice">Which sortie it was.</param>
/// <param name="Ended">Whether it reached an outcome at all (a Test Flight abandoned mid-air did not).</param>
/// <param name="Accomplished">Whether the mission module's verdict was a win.</param>
/// <param name="Text">The module's own <c>get_debrief_text</c>, or the death blurb.</param>
/// <param name="Advice">The piece of advice the death arm adds; empty otherwise.</param>
/// <param name="PostMissionMode">
/// <c>[0xBC31]</c> — 4 / 5 / 6, which Yeager voice line <c>ui_post_mission_stats_screen</c> keys off.
/// </param>
/// <param name="Seconds">How long the sortie lasted, in simulated seconds.</param>
/// <param name="EndedByLanding">Whether it ended with the aeroplane landed and stopped.</param>
/// <param name="MissionKey">The <c>.S</c> module asset name — the key <c>stats.json</c> uses.</param>
/// <param name="Kills">The integer kernel's own kill count for this sortie.</param>
/// <param name="RoundsFired">Rounds fired.</param>
/// <param name="RoundsOnTarget">Rounds that hit.</param>
/// <param name="Stats">the eight numbers the MISSION STATS screen prints.</param>
/// <remarks>
/// <b>This is the F3 hand-off.</b>  When a front-end screen is up the sortie has already been
/// disposed — the rasterizer is gone, the tile pool has joined and the session is unreferenced — so
/// the DEBRIEFING and MISSION STATS screens cannot read any of it.  They read this instead, plus
/// <c>PortStatsStore.Record(MissionKey)</c> for the running totals.
/// </remarks>
public sealed record SortieOutcome(
    LastSortie Choice,
    bool Ended,
    bool Accomplished,
    string Text,
    string Advice,
    int PostMissionMode,
    double Seconds,
    bool EndedByLanding,
    string MissionKey,
    int Kills,
    long RoundsFired,
    long RoundsOnTarget,
    SortieStats Stats)
{
    /// <summary>
    /// Whether this sortie's end lands on the DEBRIEFING screen rather than straight back on CHOOSE
    /// ACTIVITY.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Concept §3.8 (ratified): a HISTORIC sortie that reached a verdict is debriefed; a Test Flight
    /// is not (the original shows none either), and neither is a sortie left before it ended.
    /// </para>
    /// <para>
    /// A CUSTOM sortie ALWAYS is, whether or not it reached a verdict: <c>FREE.S</c> has no win
    /// rule, so there is never a debrief record, and yet the original shows the stats screen on
    /// every exit — <c>[0xEE04]!= 0</c> puts <c>[0xBC30]</c> straight to the STATS view
    /// (<c>image@0x2614A</c>) and greys BOTH view buttons (<c>image@0x260BD</c> /
    /// <c>image@0x260D0</c>).  Confirmed by
    /// a captured frame of the original, taken after End Mission.
    /// </para>
    /// </remarks>
    public bool LandsOnDebriefing => Choice.IsCustom || (Ended && Choice.IsMission);

    /// <summary>Reads a sortie that is about to be torn down.</summary>
    /// <param name="sortie">The sortie.</param>
    /// <param name="recorder">The sortie recorder, for the mission's statistics key.</param>
    public static SortieOutcome Of(Sortie sortie, SortieRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(sortie);
        ArgumentNullException.ThrowIfNull(recorder);
        FlightSession session = sortie.Session;
        MissionOutcome? outcome = session.Outcome;
        MissionDebrief? debrief = outcome?.Debrief;
        HitCensus? hits = session.Mission?.Hits;
        double seconds = outcome is { Ended: true } ? outcome.EndedAtSeconds : session.SimulatedSeconds;
        return new SortieOutcome(
            sortie.Memory,
            debrief is not null,
            debrief?.Accomplished ?? false,
            debrief?.Text ?? string.Empty,
            debrief?.Advice ?? string.Empty,
            debrief?.PostMissionMode ?? 0,
            seconds,
            outcome?.EndedByLanding ?? false,
            recorder.Identity(session).Key,
            hits?.Kills ?? 0,
            hits?.RoundsFired ?? 0,
            hits?.RoundsOnTarget ?? 0,
            SortieStats.Of(session, debrief, seconds));
    }
}

/// <summary>
/// THE EIGHT NUMBERS <c>mission_stats_screen @image@0x25E00</c> PRINTS, read off the integer kernel's
/// own DGROUP words while the sortie is still alive.
/// </summary>
/// <remarks>
/// <para>
/// Every field names the global the original reads for that line, so the screen is evidence rather
/// than host bookkeeping.  They are sampled ONCE, in <see cref="Of"/>, at the moment the shell tears
/// the flight down — by the time the DEBRIEFING screen is up the session is gone.
/// </para>
/// <para>
/// <b>Not <c>HitCensus.RoundsOnTarget</c>.</b> That field reads <c>[0xF1CC]</c>, which U3 renamed
/// <c>g_player_damage_accum</c> — it is the damage the PLAYER has taken, not rounds on target.  The
/// bullet-hit numerator the original's own stats line uses is <c>g_gun_rounds_hit [0xED36]</c>
/// (<c>image@0x25F26</c>), which is what <see cref="BulletsHit"/> reads.
/// </para>
/// </remarks>
/// <param name="EnemyDowned"><c>[0xF106]</c> — line 70 (<c>image@0x25E9D</c>).</param>
/// <param name="FriendlyDowned"><c>[0xF102]</c> — line 71 (<c>image@0x25ECB</c>).</param>
/// <param name="BulletsFired"><c>g_gun_rounds_fired [0xED34]</c> — line 72 (<c>image@0x25EE2</c>).</param>
/// <param name="BulletsHit"><c>g_gun_rounds_hit [0xED36]</c> — line 73 (<c>image@0x25F26</c>).</param>
/// <param name="MissilesFired"><c>g_missiles_fired [0xED38]</c> — line 74 (<c>image@0x25F41</c>).</param>
/// <param name="MissilesHit"><c>g_missiles_hit [0xED3A]</c> — line 75 (<c>image@0x25F85</c>).</param>
/// <param name="ElapsedSeconds">
/// <c>g_mission_elapsed_seconds [0xCE32]</c> — line 76 (<c>image@0x25FB4</c>).  The original writes
/// it from the master frame counter as the flight loop leaves (<c>image@0x0C84</c>); the port's
/// equivalent is the sortie's own simulated length, rounded down.
/// </param>
/// <param name="DamageAccumulated">
/// <c>g_player_damage_accum [0xF1CC]</c> — the numerator of the condition line (<c>image@0x10075</c>).
/// </param>
/// <param name="DamageCeiling">
/// <c>g_engagement_ceiling [0xF1DE]</c> — its denominator (<c>image@0x10072</c>).
/// </param>
/// <param name="Destroyed">
/// Whether <c>damage_state_blurb_renderer</c>'s DESTROYED arm applies (<c>image@0x10064</c>).
/// </param>
/// <param name="ConditionDraw">
/// 0..3 — which of the four words that arm's <c>prng_rand_bounded(4)</c> would pick.
/// </param>
public readonly record struct SortieStats(
    int EnemyDowned,
    int FriendlyDowned,
    int BulletsFired,
    int BulletsHit,
    int MissilesFired,
    int MissilesHit,
    int ElapsedSeconds,
    int DamageAccumulated,
    int DamageCeiling,
    bool Destroyed,
    int ConditionDraw)
{
    /// <summary><c>[0xF102]</c> — the FRIENDLY kill tally (<c>image@0x08746</c>).</summary>
    public const int FriendlyKillTally = 0xF102;

    /// <summary><c>[0xF106]</c> — the ENEMY kill tally (<c>image@0x08738</c>).</summary>
    public const int EnemyKillTally = 0xF106;

    /// <summary><c>g_gun_rounds_fired [0xED34]</c>.</summary>
    public const int GunRoundsFired = 0xED34;

    /// <summary><c>g_gun_rounds_hit [0xED36]</c>.</summary>
    public const int GunRoundsHit = 0xED36;

    /// <summary><c>g_missiles_fired [0xED38]</c>.</summary>
    public const int MissilesFiredWord = 0xED38;

    /// <summary><c>g_missiles_hit [0xED3A]</c>.</summary>
    public const int MissilesHitWord = 0xED3A;

    /// <summary><c>g_player_damage_accum [0xF1CC]</c>.</summary>
    public const int PlayerDamageAccum = 0xF1CC;

    /// <summary><c>g_engagement_ceiling [0xF1DE]</c>.</summary>
    public const int DamageCeilingWord = 0xF1DE;

    /// <summary>
    /// <c>[0xC32F]</c> — the augured-in / damage-suppress flag <c>damage_state_blurb_renderer</c>
    /// tests first (<c>image@0x10064</c>).
    /// </summary>
    public const int AuguredInFlag = 0xC32F;

    /// <summary>
    /// <c>g_active_aircraft_load_ack [0xC316]</c> — its second test, SIGNED &gt; 0
    /// (<c>image@0x1006B</c>).
    /// </summary>
    public const int AircraftLoadAck = 0xC316;

    /// <summary>Reads the sortie's numbers off the session that is about to be disposed.</summary>
    /// <param name="session">The session.</param>
    /// <param name="debrief">Its debrief, or null when there is none (a Test Flight).</param>
    /// <param name="seconds">The sortie's own length, in simulated seconds.</param>
    public static SortieStats Of(
        Sim.FlightSession session, MissionDebrief? debrief, double seconds)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Mission is not { } mission)
        {
            return default;
        }

        CombatRegisters registers = mission.Combat.Registers;

        // The original's own DESTROYED gate is [0xC32F]!= 0 || (signed) [0xC316] > 0
        //   (image@0x10064 / image@0x1006B).  The port never RAISES [0xC32F] — the only writer it
        //   models is the scene reset that zeroes it (image@0x0C4CB) — because the port's own model
        //   of the player's death is H9's fate machine, whose verdict reaches here as
        //   MissionDebriefOutcome.Killed.  So both are read: the original's bytes when they say so,
        //   and the port's verdict, which is the arm that actually fires today.
        bool destroyed = registers.Byte(AuguredInFlag) != 0
            || unchecked((sbyte)registers.Byte(AircraftLoadAck)) > 0
            || debrief?.Outcome == MissionDebriefOutcome.Killed;

        return new SortieStats(
            registers.Word(EnemyKillTally),
            registers.Word(FriendlyKillTally),
            registers.Word(GunRoundsFired),
            registers.Word(GunRoundsHit),
            registers.Word(MissilesFiredWord),
            registers.Word(MissilesHitWord),
            (int)Math.Max(0, Math.Floor(seconds)),
            unchecked((short)registers.Word(PlayerDamageAccum)),
            unchecked((short)registers.Word(DamageCeilingWord)),
            destroyed,
            ConditionDrawOf(session));
    }

    /// <summary>
    /// Which of the four words the condition line's <c>prng_rand_bounded(4)</c> would pick.
    /// </summary>
    /// <param name="session">The session the sortie ended in.</param>
    /// <remarks>
    /// A <b>LABELLED DEVIATION</b>, the same one an earlier pass made for the death blurb
    /// (<see cref="DeathBlurbDraw.FromSeed"/>): the original draws from the game's shared LFSR,
    /// whose state after a whole sortie the port does not reproduce, so the draw is derived from the
    /// sortie's own step count and a fixed offset — the same flight always shows the same word, and
    /// it is a different draw from the death line's.
    /// </remarks>
    public static int ConditionDrawOf(Sim.FlightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return DeathBlurbDraw.FromSeed(unchecked((ulong)session.StepsRun + ConditionDrawOffset))
            .DeathLine & 3;
    }

    /// <summary>How far the condition draw's seed sits from the death line's.</summary>
    public const ulong ConditionDrawOffset = 0x5F3D;
}
