namespace CYAC.Port.Core.Model.World;

/// <summary>
/// The mission's CLOUD DECK: nine <c>cloud</c> objects on a lattice that always surrounds the
/// camera, at one authored altitude.
/// </summary>
/// <remarks>
/// <para>
/// <b>Misnomer chain.</b> names the three functions that own this <c>sb_voice_pool_alloc_and_init
/// @image@0x2CC38</c>, <c>audio_voice_master_set_or_clear @image@0x2CD10</c> and
/// <c>voice_pos_update_for_idx @image@0x2CD9E</c>, and the table <c>g_voice_nearptr_table
/// [0xBC1E..0xBC2E]</c> — an "audio voice pool".  The bytes say otherwise: the spawn call is
/// <c>spawn(0x8A, &amp;record)</c> with the record's class word set to <c>0x51F6</c>, which is the
/// <c>cloud</c> registry slot (<c>data/exe/meshes/cloud.json</c> <c>slot.dgroup</c>), the altitude
/// comes from <c>[0xF100]</c> (the <c>.S</c> <c>mission_altitude</c> directive) and the
/// per-frame update is a camera-relative POSITION wrap, not a pan law.  Report-only rename
/// proposals: <c>cloud_deck_spawn</c>, <c>cloud_deck_visibility_apply</c>,
/// <c>cloud_reposition_for_idx</c>, <c>g_cloud_object_nearptr[9]</c>.  (is outside this type's
/// landing zone.)
/// </para>
/// <para>
/// INT-only content: every number here is the shipped data's.
/// </para>
/// </remarks>
public static class CloudDeck
{
    /// <summary>How many cloud objects the deck holds: 9.</summary>
    /// <remarks>
    /// <c>image@0x2CCC0..0x2CCEC</c> stores into <c>[0xBC2E]</c> stepping down by 2 while
    /// <c>si &gt;= 0xBC1E</c> — nine slots.
    /// </remarks>
    public const int Count = 9;

    /// <summary>The mesh every cloud object draws.</summary>
    public const string MeshBasename = "cloud";

    /// <summary>
    /// The <c>.S</c> <c>mission_altitude</c> value that means "this mission has no clouds":
    /// <c>−1</c> makes <c>image@0x2CC40</c> return before spawning anything.
    /// </summary>
    public const int NoDeck = -1;

    /// <summary>
    /// The value that means "roll for a deck at load": <c>0</c>, which is what <c>FREE.S</c> carries
    /// (<c>data/missions/free.json</c> directive <c>mission_altitude = 0</c>), i.e. the TEST FLIGHT.
    /// </summary>
    public const int RollAtLoad = 0;

    /// <summary>The floor of the random offset from the player's altitude: <c>0xBB8</c> = 3,000 ft.</summary>
    public const int MinimumOffsetFeet = 0xBB8;

    /// <summary>The span of the random offset ABOVE the player: <c>0x1F40</c> = 8,000 ft.</summary>
    public const int AboveSpanFeet = 0x1F40;

    /// <summary>The span of the random offset BELOW the player: <c>0x1388</c> = 5,000 ft.</summary>
    public const int BelowSpanFeet = 0x1388;

    /// <summary>
    /// The lattice period, in WORLD units: <c>0x0080_0000</c> position units &gt;&gt; 8 = 32,768.
    /// </summary>
    /// <remarks>
    /// <c>image@0x2DDFA</c> / <c>image@0x2CE35</c> add and subtract <c>0x0080_0000</c> from the
    /// cloud's position until it lies inside <see cref="HalfWindowWorldUnits"/> of the view anchor.
    /// </remarks>
    public const int PeriodWorldUnits = 0x0080_0000 >> 8;

    /// <summary>
    /// Half the window a cloud is kept inside, in WORLD units: <c>0x0040_0000</c> &gt;&gt; 8 = 16,384.
    /// </summary>
    public const int HalfWindowWorldUnits = 0x0040_0000 >> 8;

    // The offsets are an authored placement table, so they are the tree's now —
    // exe/tables/world.json, section cloudDeck — and the placement moved with them to
    // CloudDeckLattice, which DataTree.CloudDeckLattice reads.

    /// <summary>
    /// How many whole lattice periods the port instantiates around the wrapped cell, per axis.
    /// </summary>
    /// <remarks>
    /// The original keeps exactly <see cref="Count"/> clouds inside the
    /// ±<see cref="HalfWindowWorldUnits"/> window (<c>cloud_reposition_for_idx @image@0x2CD9E</c>),
    /// which is invisible while the class's own cull distance (<c>cloud.lodThresholds[0]</c> × 256
    /// = 49,920 world units) is honoured — but the port draws to an unlimited horizon, so that
    /// window becomes a visible POP: a cloud jumps a whole period the instant the camera
    /// crosses a cell boundary.  Tiling the SAME lattice fixes it without inventing anything: a
    /// cloud's world position becomes a pure function of (authored offset, cell), never of where
    /// the camera is.
    /// </remarks>
    public const int DefaultTiles = 2;

    /// <summary>How many cloud instances a given tile radius draws.</summary>
    /// <param name="tiles">The tile radius, ≥ 0.</param>
    public static int InstanceCount(int tiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tiles);
        int side = (2 * tiles) + 1;
        return Count * side * side;
    }

    /// <summary>
    /// The opacity a tiled cloud is drawn at, so that one entering or leaving the tiled set does so
    /// at zero.
    /// </summary>
    /// <param name="deltaX">Cloud X minus camera X, world units.</param>
    /// <param name="deltaZ">Cloud Z minus camera Z, world units.</param>
    /// <param name="tiles">The tile radius in force.</param>
    /// <returns>1 inside the stable core, ramping linearly to 0 at the tiling's own edge.</returns>
    /// <remarks>
    /// A cloud can only appear or disappear when its per-axis distance crosses
    /// <c>(tiles + ½) × period</c> — the wrap window plus the outermost tile — so the fade is on the
    /// CHEBYSHEV distance <c>max(|Δx|, |Δz|)</c> and reaches 0 exactly there.  It is presentation
    /// only (the original has no such fade: at 320×200 its clouds were culled at 49,920 units long
    /// before the window mattered).
    /// </remarks>
    public static double FadeAt(double deltaX, double deltaZ, int tiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tiles);
        double edge = (tiles + 0.5) * PeriodWorldUnits;
        double band = PeriodWorldUnits / 2.0;
        double chebyshev = Math.Max(Math.Abs(deltaX), Math.Abs(deltaZ));
        if (chebyshev <= edge - band)
        {
            return 1.0;
        }

        return chebyshev >= edge ? 0.0 : (edge - chebyshev) / band;
    }

    /// <summary>
    /// The altitude the original draws for a mission whose <c>mission_altitude</c> is
    /// <see cref="RollAtLoad"/>, in world units (= feet).
    /// </summary>
    /// <param name="playerAltitudeFeet">The player's own altitude at load, <c>pos_y &gt;&gt; 8</c>.</param>
    /// <param name="offsetFeet">
    /// The draw: <c>prng_bounded(0x1F40) + 0xBB8</c> for a deck ABOVE, or the negative of
    /// <c>prng_bounded(0x1388) + 0xBB8</c> for one below.
    /// </param>
    /// <remarks>
    /// <c>image@0x2CC68..0x2CCAE</c>.  The BELOW branch is only reachable when the player is already
    /// above <c>0x0027_1000</c> position units AND a second draw is <c>&gt;= 0x80</c>; on a Test
    /// Flight (parked at 21 ft) it never fires.  There is also a <c>prng() &lt; 0x32</c> arm that
    /// sets <c>[0xF100] = −1</c> and gives the mission NO clouds at all.
    /// </remarks>
    public static int AltitudeFromDraw(int playerAltitudeFeet, int offsetFeet) =>
        playerAltitudeFeet + offsetFeet;
}
