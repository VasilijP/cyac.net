using System.Globalization;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Model.World;

/// <summary>
/// The two BAM angle tables an explosion's debris shards are placed at, read from
/// <c>exe/tables/effect_look.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>effect_particle_draw @image@0x03C5A</c> places each of its <see cref="ShardCount"/> shards at
/// <c>[0x0E20][(sequence + i) &amp; 7]</c> (<c>image@0x03D60..0x03D6C</c>), and the burst arm of
/// <c>deferred_effect_render</c> each of its <see cref="BurstShardCount"/> at
/// <c>[0x0E52][(sequence + i) &amp; 0x0F]</c> (<c>image@0x040CA..0x040DC</c>), where <c>sequence</c> is
/// the effect record's <c>+0x0A</c> byte.  The angles are shipped data; the port carries only these
/// counts and the indexing.
/// </para>
/// <para>
/// The renderer reads the process-wide table <see cref="DataTree.InstallGlobalTables"/> installs, unless
/// it was handed its own (tests, tools).
/// </para>
/// </remarks>
public sealed class EffectDebrisAngles
{
    /// <summary>How many shards the disc arm draws, and how many entries its table holds: 8.</summary>
    /// <remarks>
    /// <c>add word [bp-4],6 / cmp word [bp-4],0x30 / jge</c> @<c>image@0x03E04</c>, and the index mask
    /// <c>and bx,7</c> @<c>image@0x03D67</c>.
    /// </remarks>
    public const int ShardCount = 8;

    /// <summary>How many shards the burst arm draws, and how many entries its table holds: 16.</summary>
    /// <remarks>
    /// <c>cmp word [bp-0x22],0x10 / jl</c> @<c>image@0x04064</c>, and the index mask
    /// <c>and ax,0x0F</c> @<c>image@0x040CA</c>.
    /// </remarks>
    public const int BurstShardCount = 16;

    private static EffectDebrisAngles? _installed;

    private readonly ushort[] _shard;
    private readonly ushort[] _burst;

    /// <summary>Builds a table pair from explicit angles.</summary>
    /// <param name="shard">The disc arm's <see cref="ShardCount"/> angles.</param>
    /// <param name="burst">The burst arm's <see cref="BurstShardCount"/> angles.</param>
    /// <exception cref="ArgumentException">A count is wrong, or an angle is outside the circle.</exception>
    public EffectDebrisAngles(IReadOnlyList<int> shard, IReadOnlyList<int> burst)
    {
        ArgumentNullException.ThrowIfNull(shard);
        ArgumentNullException.ThrowIfNull(burst);
        _shard = Angles(shard, ShardCount, "shard", message => new ArgumentException(message, nameof(shard)));
        _burst = Angles(burst, BurstShardCount, "burst", message => new ArgumentException(message, nameof(burst)));
    }

    private EffectDebrisAngles(ushort[] shard, ushort[] burst)
    {
        _shard = shard;
        _burst = burst;
    }

    /// <summary>Whether <see cref="Install"/> has supplied the process-wide table.</summary>
    public static bool IsInstalled => _installed is not null;

    /// <summary>The process-wide table.</summary>
    /// <exception cref="InvalidOperationException">No table has been installed.</exception>
    public static EffectDebrisAngles Installed =>
        _installed ?? throw new InvalidOperationException(
            $"the explosion debris angles have not been installed. Call DataTree.InstallGlobalTables (which " +
            $"reads '{EffectLookTableDto.DataPath}' from the transformed data tree), or hand the renderer " +
            "its own table.");

    /// <summary>The disc arm's angles, in table order.</summary>
    public ReadOnlySpan<ushort> Shard => _shard;

    /// <summary>The burst arm's angles, in table order.</summary>
    public ReadOnlySpan<ushort> Burst => _burst;

    /// <summary>Reads the tables out of their tree document.</summary>
    /// <param name="table">The parsed <c>exe/tables/effect_look.json</c>.</param>
    /// <returns>The tables.</returns>
    /// <exception cref="InvalidDataException">
    /// The document breaks a rule: the wrong format, a missing table, the wrong count, or an angle outside
    /// the 2,880-unit circle.
    /// </exception>
    public static EffectDebrisAngles Load(EffectLookTableDto table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!string.Equals(table.Format, EffectLookTableDto.FormatTag, StringComparison.Ordinal))
        {
            throw Malformed($"its format is \"{table.Format}\", expected \"{EffectLookTableDto.FormatTag}\"");
        }

        List<int> shard = table.ShardAngles?.Values ?? throw Malformed("shardAngles has no values");
        List<int> burst = table.BurstShardAngles?.Values ?? throw Malformed("burstShardAngles has no values");
        return new EffectDebrisAngles(
            Angles(shard, ShardCount, "shardAngles", Malformed),
            Angles(burst, BurstShardCount, "burstShardAngles", Malformed));
    }

    /// <summary>Installs the process-wide table the renderer reads.</summary>
    /// <param name="angles">The tables, usually <c>DataTree.DebrisAngles</c>.</param>
    public static void Install(EffectDebrisAngles angles)
    {
        ArgumentNullException.ThrowIfNull(angles);
        _installed = angles;
    }

    /// <summary>Forgets the process-wide table — for tests that need the not-installed behaviour.</summary>
    public static void Uninstall() => _installed = null;

    /// <summary>The disc arm's <paramref name="index"/>-th shard angle for an effect record.</summary>
    /// <param name="sequence">The record's <c>+0x0A</c> byte.</param>
    /// <param name="index">The shard, 0 … <see cref="ShardCount"/> − 1.</param>
    /// <returns>The BAM angle.</returns>
    public ushort ShardAngle(int sequence, int index) => _shard[(sequence + index) & (ShardCount - 1)];

    /// <summary>The burst arm's <paramref name="index"/>-th shard angle for an effect record.</summary>
    /// <param name="sequence">The record's <c>+0x0A</c> byte.</param>
    /// <param name="index">The shard, 0 … <see cref="BurstShardCount"/> − 1.</param>
    /// <returns>The BAM angle.</returns>
    public ushort BurstShardAngle(int sequence, int index) => _burst[(sequence + index) & (BurstShardCount - 1)];

    private static ushort[] Angles(
        IReadOnlyList<int> values, int count, string name, Func<string, Exception> fail)
    {
        if (values.Count != count)
        {
            throw fail(string.Create(
                CultureInfo.InvariantCulture, $"{name} holds {values.Count} angles, expected {count}"));
        }

        ushort[] angles = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            if (values[i] is < 0 or >= Angle.FullCircle)
            {
                throw fail(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{name}[{i}] = {values[i]} is outside the {Angle.FullCircle}-unit circle"));
            }

            angles[i] = (ushort)values[i];
        }

        return angles;
    }

    private static InvalidDataException Malformed(string problem) =>
        new($"{EffectLookTableDto.DataPath} is malformed: {problem}");
}
