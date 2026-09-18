using System.Globalization;
using System.Reflection;
using CYAC.Port.Core.Sim.Combat;

namespace CYAC.Port.Host.Headless;

/// <summary>
/// The ENGAGEMENT-AI ARM CENSUS: which arms of the enemy-AI kernel actually ran over a
/// headless sortie.
/// </summary>
/// <remarks>
/// <para>
/// The investigation this was written for ("sometimes the AI gets stuck and just flies away") needs
/// to distinguish "the bandit decided not to engage" from "a gate the bandit never reached refused".
/// A pose census says WHERE an aeroplane is and what phase it is in; it cannot say which of the
/// <c>weapon_fire_combat_loop_per_shot @image@0x0416C</c> arms or of
/// <c>combat_target_score_and_fire @image@0x07952</c>'s five exits the sortie took.  Those counters
/// already exist inside the integer kernel (<see cref="EngagementNodeCensus"/> and the player-side
/// <c>PlayerCombatCensus</c>); nothing here computes anything — it only prints them.
/// </para>
/// <para>
/// PURE OBSERVATION: no counter is read by the simulation, and the class is only reachable from the
/// headless run's epilogue, behind <c>--ai-census</c>.
/// </para>
/// </remarks>
public static class AiArmCensus
{
    /// <summary>The report lines — one per non-zero counter.</summary>
    /// <param name="context">The live mission's combat-kernel context.</param>
    /// <returns>The lines, each section headed by its title.</returns>
    public static IEnumerable<string> Lines(CombatKernelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        yield return "AI-ARM CENSUS — engagement node pass (weapon_fire_combat_loop_per_shot"
            + " @image@0x0416C):";
        foreach ((string arm, long count) in context.Node.Census.Lines())
        {
            if (count != 0)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"     {arm,-44} {count,12:N0}");
            }
        }

        yield return "AI-ARM CENSUS — manoeuvring geometry (engagement_slot_angle_update"
            + " @image@0x066E4 and its neighbours):";
        foreach ((string arm, long count) in context.Geometry.Census.Lines())
        {
            if (count != 0)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"     {arm,-44} {count,12:N0}");
            }
        }

        // The ADMISSION half: the arm the `ai-admitter-cold-start` quirk opens lives here
        // (engagement_new_slot_select_and_commit @image@0x0BB67), and without these counters a
        // claim that a bandit was RE-COMMITTED cannot be told from a bandit that re-acquired on its
        // own.  Same reflection walk, same pure observation.
        yield return "AI-ARM CENSUS — admission and lifecycle"
            + " (engagement_new_slot_select_and_commit @image@0x0BB67):";
        foreach ((string name, long count) in Counters(context.Lifecycle.Census))
        {
            if (count != 0)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"     {name,-44} {count,12:N0}");
            }
        }

        yield return "AI-ARM CENSUS — player-side scorer (combat_target_score_and_fire"
            + " @image@0x07952) and its neighbours:";
        foreach ((string name, long count) in Counters(context.Player.Census))
        {
            if (count != 0)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"     {name,-44} {count,12:N0}");
            }
        }
    }

    /// <summary>
    /// Every public integer counter of a census object, in declaration order.
    /// </summary>
    /// <remarks>
    /// The player-side census is a plain property bag with no <c>Lines()</c> of its own, and it is
    /// added to by many slices; reflecting over it keeps this instrument correct when a counter is
    /// added without anybody remembering to list it here.
    /// </remarks>
    /// <param name="census">The census object.</param>
    /// <returns>Name/value pairs.</returns>
    internal static IEnumerable<(string Name, long Count)> Counters(object census)
    {
        foreach (PropertyInfo property in census.GetType().GetProperties(
            BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead)
            {
                continue;
            }

            object? value = property.GetValue(census);
            long count = value switch
            {
                int i => i,
                long l => l,
                _ => 0,
            };

            if (value is int or long)
            {
                yield return (property.Name, count);
            }
        }
    }
}
