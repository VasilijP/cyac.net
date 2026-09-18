using System.Globalization;
using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Model.World;

/// <summary>
/// The cloud deck's LATTICE: the authored offset of each of the <see cref="CloudDeck.Count"/> clouds,
/// read from <c>exe/tables/world.json</c>, and the wrap that places them around the camera.
/// </summary>
/// <remarks>
/// <para>
/// <c>cloud_reposition_for_idx @image@0x2CD9E</c> adds a cloud's offset (<c>[0x360A + 8·i]</c> for X,
/// <c>[0x360E + 8·i]</c> for Z, <c>image@0x2CDD2</c> / <c>image@0x2CE56</c>) to the view anchor's
/// period base and wraps the result by whole periods until it lies within
/// ±<see cref="CloudDeck.HalfWindowWorldUnits"/> of the anchor.  The offsets are the shipped table's;
/// the port carries only where it is and how it is indexed.
/// </para>
/// <para>
/// The table holds position units (world units × 256); the lattice works in world units, as the
/// renderer does, so each offset is shifted down by 8 when it is read.
/// </para>
/// </remarks>
public sealed class CloudDeckLattice
{
    /// <summary>
    /// The table's DGROUP offset, <c>[0x360A]</c> (<c>image@0x3F36A</c>): <see cref="CloudDeck.Count"/>
    /// records of two <c>i32</c> (X, Z).
    /// </summary>
    public const int OffsetsDgroupOffset = 0x360A;

    /// <summary>How far position units are shifted down into world units.</summary>
    public const int PositionShift = 8;

    private readonly int[] _x;
    private readonly int[] _z;

    private CloudDeckLattice(int[] x, int[] z)
    {
        _x = x;
        _z = z;
    }

    /// <summary>Builds a lattice from explicit offsets in position units, in cloud order.</summary>
    /// <param name="offsets"><see cref="CloudDeck.Count"/> (X, Z) pairs, world units × 256.</param>
    /// <exception cref="ArgumentException">The count is wrong.</exception>
    public static CloudDeckLattice FromPositionUnits(IReadOnlyList<(int X, int Z)> offsets)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        if (offsets.Count != CloudDeck.Count)
        {
            throw new ArgumentException(
                $"a cloud deck places {CloudDeck.Count} clouds, {offsets.Count} offsets were given",
                nameof(offsets));
        }

        return new CloudDeckLattice(
            [.. offsets.Select(o => o.X >> PositionShift)],
            [.. offsets.Select(o => o.Z >> PositionShift)]);
    }

    /// <summary>Reads the lattice out of its tree document.</summary>
    /// <param name="table">The parsed <c>exe/tables/world.json</c>.</param>
    /// <returns>The lattice.</returns>
    /// <exception cref="InvalidDataException">
    /// The document breaks a rule: the wrong format, no cloud-deck section, a cloud count other than the
    /// deck's, a cloud out of order, or a value that does not fit an <c>i32</c>.
    /// </exception>
    public static CloudDeckLattice Load(WorldTableDto table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!string.Equals(table.Format, WorldTableDto.FormatTag, StringComparison.Ordinal))
        {
            throw Malformed($"its format is \"{table.Format}\", expected \"{WorldTableDto.FormatTag}\"");
        }

        CloudDeckTableDto deck = table.CloudDeck ?? throw Malformed("it has no cloudDeck");
        if (deck.Clouds != CloudDeck.Count)
        {
            throw Malformed($"cloudDeck places {deck.Clouds} clouds; the deck holds {CloudDeck.Count}");
        }

        List<CloudOffsetDto> offsets = deck.Offsets ?? throw Malformed("cloudDeck lists no offsets");
        if (offsets.Count != CloudDeck.Count)
        {
            throw Malformed($"cloudDeck lists {offsets.Count} offsets, expected {CloudDeck.Count}");
        }

        (int X, int Z)[] pairs = new (int X, int Z)[CloudDeck.Count];
        for (int i = 0; i < offsets.Count; i++)
        {
            CloudOffsetDto offset = offsets[i] ?? throw Malformed($"cloudDeck.offsets[{i}] is null");
            if (offset.Cloud != i)
            {
                throw Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"cloudDeck.offsets[{i}] is cloud {offset.Cloud}; the offsets run in cloud order"));
            }

            pairs[i] = (Int32(offset.X, i, "x"), Int32(offset.Z, i, "z"));
        }

        return FromPositionUnits(pairs);
    }

    /// <summary>One cloud's authored offset, in world units.</summary>
    /// <param name="index">0..<see cref="CloudDeck.Count"/> − 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a cloud slot.</exception>
    public (int X, int Z) Offset(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, CloudDeck.Count);
        return (_x[index], _z[index]);
    }

    /// <summary>
    /// Where cloud <paramref name="index"/> sits this frame, given where the camera is.
    /// </summary>
    /// <param name="index">0..8.</param>
    /// <param name="anchorXWorldUnits">The view anchor's X (<c>s_view_anchor [0xD88E]</c>), world units.</param>
    /// <param name="anchorZWorldUnits">The view anchor's Z (<c>s_view_anchor [0xD896]</c>), world units.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a cloud slot.</exception>
    /// <remarks>
    /// <c>cloud_reposition_for_idx @image@0x2CD9E</c> wraps the authored offset by whole periods
    /// until it lies within ±<see cref="CloudDeck.HalfWindowWorldUnits"/> of the anchor, in X and in Z
    /// independently — so the deck is an infinite tiling and the nine objects are simply the nine
    /// nearest cells.  (The original also ORs in bit 23 of the anchor before wrapping,
    /// <c>image@0x2CDCC</c> <c>and al,0x80</c>; since that adds either 0 or exactly one whole period
    /// it cannot change the wrapped result, and the port leaves it out.)
    /// </remarks>
    public (int X, int Z) Position(int index, int anchorXWorldUnits, int anchorZWorldUnits)
    {
        (int x, int z) = Offset(index);
        return (Wrap(x, anchorXWorldUnits), Wrap(z, anchorZWorldUnits));
    }

    /// <summary>
    /// Where cloud <paramref name="index"/> of lattice cell (<paramref name="tileX"/>,
    /// <paramref name="tileZ"/>) sits, given where the camera is.
    /// </summary>
    /// <param name="index">0..8 — which of the nine authored offsets.</param>
    /// <param name="tileX">Whole periods to add in X (typically −tiles..+tiles).</param>
    /// <param name="tileZ">Whole periods to add in Z.</param>
    /// <param name="anchorXWorldUnits">The view anchor's X, world units.</param>
    /// <param name="anchorZWorldUnits">The view anchor's Z, world units.</param>
    /// <remarks>
    /// Cell (0, 0) is exactly <see cref="Position(int, int, int)"/> — the original's own placement.
    /// A given (index, tile) pair names ONE fixed world position for as long as the camera stays
    /// inside the same wrap cell, and when the camera does cross a boundary the whole tiled set
    /// shifts by one cell, so the only clouds that enter or leave are the outermost ring's — which
    /// is what <see cref="CloudDeck.FadeAt"/> takes to zero.
    /// </remarks>
    public (int X, int Z) Position(
        int index, int tileX, int tileZ, int anchorXWorldUnits, int anchorZWorldUnits)
    {
        (int x, int z) = Position(index, anchorXWorldUnits, anchorZWorldUnits);
        return (x + (tileX * CloudDeck.PeriodWorldUnits), z + (tileZ * CloudDeck.PeriodWorldUnits));
    }

    private static int Wrap(int value, int anchor)
    {
        int delta = value - anchor;
        int cells = (int)Math.Floor((delta + CloudDeck.HalfWindowWorldUnits) / (double)CloudDeck.PeriodWorldUnits);
        return value - (cells * CloudDeck.PeriodWorldUnits);
    }

    private static int Int32(long value, int cloud, string axis) =>
        value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : throw Malformed(string.Create(
                CultureInfo.InvariantCulture, $"cloudDeck cloud {cloud} {axis} = {value} does not fit an i32"));

    private static InvalidDataException Malformed(string problem) =>
        new($"{WorldTableDto.DataPath} is malformed: {problem}");
}
