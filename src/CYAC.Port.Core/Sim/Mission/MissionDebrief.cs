using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Sim.Mission;

/// <summary>How the sortie ended, as <c>ui_post_death_message @image@0x25BAF</c> decides it.</summary>
public enum MissionDebriefOutcome
{
    /// <summary>
    /// The pilot came back: <c>g_object_destroyed_flag [0xEE58] == 0</c>, so the function takes its
    /// LIVE path and asks the mission module for the verdict (<c>image@0x25BC2</c>, <c>je</c> to the
    /// live arm at <c>image@0x25C23</c>).
    /// </summary>
    Survived = 0,

    /// <summary>
    /// The pilot did not: <c>[0xEE58]!= 0</c>.  The module is <b>never asked</b> on this path —
    /// the screen is the random "Augured in" family plus a piece of advice, and the post-mission
    /// mode is 6 (<c>mov byte [0xBC31],6</c> @<c>image@0x25BC9</c>).
    /// </summary>
    Killed = 1,
}

/// <summary>
/// THE DEBRIEF: what the original puts on the post-mission screen, assembled from the mission
/// module's own verdict and the string catalogue.
/// </summary>
/// <remarks>
/// <para>
/// The producer is <c>ui_post_death_message @image@0x25BAF</c>, called unconditionally by
/// <c>mission_state_machine</c> once the flight loop is over; the screen it feeds is
/// <c>ui_post_mission_stats_screen @image@0x26138</c>, which plays a Yeager voice line indexed by
/// <see cref="PostMissionMode"/> (<c>image@0x26247</c>).
/// </para>
/// <para>
/// <b>The two arms.</b>  Dead ⇒ mode 6, a death line drawn from strings 0x50..0x53 and — on about
/// 86 % of deaths (<c>prng_rand8() &lt; 0xDC</c>, <c>image@0x25BF5..0x25BFD</c>) — an advice line
/// from 0x54..0x57.  Alive ⇒ the module's <c>get_debrief_text</c> writes the text and returns
/// <c>AX</c>, and <c>cmp di,1 / sbb al,al / and al,1 / add al,4</c> turns that into mode 4
/// (accomplished) or 5 (<c>image@0x25C79..0x25C82</c>).
/// </para>
/// </remarks>
/// <param name="Outcome">Which arm ran.</param>
/// <param name="PostMissionMode"><c>[0xBC31]</c>: 4 accomplished, 5 not, 6 dead.</param>
/// <param name="Text">The module's debrief text, or the death line.</param>
/// <param name="Advice">The death path's second line, when it drew one.</param>
/// <param name="ModuleResult">The module's <c>AX</c>, or null when it was not asked.</param>
/// <param name="TextKey">Which of the module's texts (<c>d0</c>, <c>d1</c>, …) it selected.</param>
public readonly record struct MissionDebrief(
    MissionDebriefOutcome Outcome,
    int PostMissionMode,
    string? Text,
    string? Advice,
    ushort? ModuleResult,
    string? TextKey)
{
    /// <summary>Whether the module scored the sortie a success — mode 4.</summary>
    public bool Accomplished => PostMissionMode == PostMissionModeAccomplished;

    /// <summary><c>[0xBC31] = 4</c>: the module returned <c>AX ≥ 1</c>.</summary>
    public const int PostMissionModeAccomplished = 4;

    /// <summary><c>[0xBC31] = 5</c>: the pilot survived but the module returned 0.</summary>
    public const int PostMissionModeNotAccomplished = 5;

    /// <summary><c>[0xBC31] = 6</c>: the pilot was destroyed.</summary>
    public const int PostMissionModeDead = 6;

    /// <summary>Builds the debrief of a sortie the pilot did not survive.</summary>
    /// <param name="catalog">The indexed UI string catalogue (<c>strings.json</c>).</param>
    /// <param name="draw">
    /// Three uniform values: the death line's <c>prng_rand_bounded(4)</c>, the advice GATE's
    /// <c>prng_rand8()</c> (0..255) and the advice line's <c>prng_rand_bounded(4)</c>.
    /// </param>
    public static MissionDebrief ForDeath(UiStringCatalogDto catalog, DeathBlurbDraw draw)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        string? death = StringAt(catalog, DeathLineFirstIndex + (draw.DeathLine & 3));
        string? advice = draw.AdviceGate < AdviceGateThreshold
            ? StringAt(catalog, AdviceLineFirstIndex + (draw.AdviceLine & 3))
            : null;
        return new MissionDebrief(
            MissionDebriefOutcome.Killed, PostMissionModeDead, death, advice, null, null);
    }

    /// <summary>Builds the debrief of a sortie the pilot survived, from the module's answer.</summary>
    /// <param name="debrief">What <c>get_debrief_text</c> selected and returned.</param>
    public static MissionDebrief ForSurvivor(WinRuleDebrief debrief) =>
        new(MissionDebriefOutcome.Survived,
            debrief.Accomplished ? PostMissionModeAccomplished : PostMissionModeNotAccomplished,
            debrief.Text,
            null,
            debrief.Result,
            debrief.TextKey);

    /// <summary>
    /// The debrief of a survivor whose mission has no rules the port can run (the unmodelled five).
    /// </summary>
    /// <remarks>
    /// Honest rather than invented: the module IS asked in the original and would answer; the port
    /// cannot, so it reports mode 5 — "not accomplished" — and says why.
    /// </remarks>
    /// <param name="assetName">The mission's asset name.</param>
    public static MissionDebrief ForUnmodelled(string assetName)
    {
        ArgumentNullException.ThrowIfNull(assetName);
        return new MissionDebrief(
            MissionDebriefOutcome.Survived,
            PostMissionModeNotAccomplished,
            $"({assetName}'s win rules are not modelled — the port cannot score this sortie.  "
                + UnmodelledMissions.MissingVocabulary(assetName) + ")",
            null,
            null,
            null);
    }

    /// <summary>String 0x50 — the first of the four "you died" lines (<c>image@0x25BDB</c>).</summary>
    public const int DeathLineFirstIndex = 0x50;

    /// <summary>String 0x54 — the first of the four advice lines (<c>image@0x25C0E</c>).</summary>
    public const int AdviceLineFirstIndex = 0x54;

    /// <summary>
    /// <c>cmp ax,0xDC / jge</c> (<c>image@0x25BFA</c>): a <c>prng_rand8()</c> below 0xDC shows the
    /// advice, so about 86 % of deaths get one.
    /// </summary>
    public const int AdviceGateThreshold = 0xDC;

    private static string? StringAt(UiStringCatalogDto catalog, int index)
    {
        foreach (UiStringDto entry in catalog.Strings ?? [])
        {
            if (entry.Index == index)
            {
                return entry.Text;
            }
        }

        return null;
    }
}

/// <summary>The three draws <c>ui_post_death_message</c>'s death arm makes.</summary>
/// <param name="DeathLine"><c>prng_rand_bounded(4)</c> for the "Augured in" family.</param>
/// <param name="AdviceGate"><c>prng_rand8()</c>, compared against 0xDC.</param>
/// <param name="AdviceLine"><c>prng_rand_bounded(4)</c> for the advice family.</param>
/// <remarks>
/// The values are the CALLER's, not this type's: the original takes them from the game's own shared
/// PRNG, whose state after a whole sortie the port does not reproduce (the same
/// <c>(open)</c> as the cloud-deck draw, <c>FlightSession.ResolveCloudAltitude</c>).  A host that
/// wants a repeatable debrief derives them from something repeatable and says so.
/// </remarks>
public readonly record struct DeathBlurbDraw(int DeathLine, int AdviceGate, int AdviceLine)
{
    /// <summary>
    /// Derives the three draws from a sortie's own end state, so the same flight always shows the
    /// same blurb.
    /// </summary>
    /// <param name="seed">Anything that identifies the sortie — the host uses its step count.</param>
    /// <remarks>
    /// A LABELLED DEVIATION: not the original's draw, which is
    /// <c>prng_rand_bounded</c> on the shared LFSR.  It is a splitmix-style scramble so that
    /// consecutive seeds do not give consecutive lines.
    /// </remarks>
    public static DeathBlurbDraw FromSeed(ulong seed)
    {
        ulong x = seed + 0x9E37_79B9_7F4A_7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D0_49BB_1331_11EBUL;
        x ^= x >> 31;
        return new DeathBlurbDraw((int)(x & 3), (int)((x >> 8) & 0xFF), (int)((x >> 16) & 3));
    }
}
