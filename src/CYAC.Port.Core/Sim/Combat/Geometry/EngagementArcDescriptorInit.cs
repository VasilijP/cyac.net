namespace CYAC.Port.Core.Sim.Combat.Geometry;

/// <summary>
/// <c>engagement_arc_desc_init_and_speed_select @image@0x06E56</c> — install one engagement class's
/// 28-byte arc descriptor and the spawn altitude, then ANSWER THE OPENING AIRSPEED for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>PROPOSE a rename:</b> <c>engagement_arc_desc_init_and_speed_select</c>. The scanner name says
/// "heading"; the bytes return a SPEED.  Its two call sites are both in
/// <c>custom_mission_build_from_picks</c>: <c>image@0x28155</c> writes the answer to
/// <c>g_player_init_speed_seed [0xEE52]</c> — which <c>active_aircraft_load_and_state_reset
/// @image@0x22525</c> turns, <c>&lt;&lt; 8</c>, into the master's forward velocity — and
/// <c>image@0x281A9</c> MIN-accumulates the answer over the enemies to get the formation's common opening
/// speed.
/// </para>
/// <para>
/// Four steps:
/// </para>
/// <list type="number">
///   <item><description><c>[0xED54] = BX</c> (<c>image@0x06E5C</c>) — the weapon-descriptor pointer every
///   arc routine reads.</description></item>
///   <item><description><c>rep movsw cx=0x0E</c> copies 28 bytes of <c>BX[+0x26]</c> into
///   <c>[0xED8E..0xEDA9]</c> (<c>image@0x06E6D</c>).</description></item>
///   <item><description><c>sub dx,dx / mov dh,dl / mov dl,ah / mov ah,al / sub al,al</c> writes
///   <c>[0xED46]/[0xED48] = (u32)AX &lt;&lt; 8</c> (<c>image@0x06E6F..0x06E7C</c>) — ZERO-extended, which is
///   why <c>[0xED47]</c>, the straddling word, reads back as the altitude itself.</description></item>
///   <item><description><c>test byte [BX+0x0D],2</c> (<c>image@0x06E84</c>) either runs
///   <see cref="EngagementArcParams.Update"/> or writes <c>[0xED9A] = descriptor[+0x0C]</c>; then the
///   answer is <c>[0xED9A]</c>, or <c>([0xED9A] + [0xED9C]) &gt;&gt; 1</c> when <c>DL != 0</c>
///   (<c>sar ax,1</c> @<c>image@0x06EA6</c>).</description></item>
/// </list>
/// </remarks>
public static class EngagementArcDescriptorInit
{
    /// <summary>How many bytes of the arc descriptor are installed: 28 (<c>rep movsw cx=0x0E</c>).</summary>
    public const int DescriptorBytes = 28;

    /// <summary><c>g_engagement_altitude_lo [0xED46]</c> — the spawn altitude as an i32 <c>&lt;&lt; 8</c>.</summary>
    /// <remarks>Its straddling view <c>[0xED47]</c> is the altitude in world feet.</remarks>
    public const int AltitudeDgroupOffset = 0xED46;

    /// <summary>The first speed word of the installed descriptor, <c>[0xED9A]</c> (= <c>[0xED8E]+0x0C</c>).</summary>
    public const int SpeedLowDgroupOffset = 0xED9A;

    /// <summary>The second, <c>[0xED9C]</c> (= <c>[0xED8E]+0x0E</c>).</summary>
    public const int SpeedHighDgroupOffset = 0xED9C;

    /// <summary>Runs the whole routine.</summary>
    /// <param name="context">The geometry context (registers, the constant surface, the arc seam).</param>
    /// <param name="prototypeRef">
    /// The original's <c>BX</c>: an engagement class prototype (the enemy call) or a per-flyable stat
    /// block from <c>g_flyable_statblock_ptr_table [0x0FBC]</c> (the player call).
    /// </param>
    /// <param name="altitudeFeet">The original's <c>AX</c>: the spawn altitude in world feet.</param>
    /// <param name="averageSpeeds">
    /// The original's <c>DL != 0</c>: answer the mean of the descriptor's two speed words instead of the
    /// first.  The custom-mission builder derives it from the VERB
    /// (<c>image@0x2813C</c> / <c>image@0x281A1</c>).
    /// </param>
    /// <returns>The opening airspeed, in the same units as an authored <c>initial_speed</c>.</returns>
    public static short InitialiseAndSelectSpeed(
        EngagementGeometryContext context,
        ushort prototypeRef,
        ushort altitudeFeet,
        bool averageSpeeds)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters r = context.Registers;
        ICombatStaticData data = context.StaticData;

        r.SetWord(0xED54, prototypeRef);                                    // image@0x06E5C

        ushort descriptor = data.Word(prototypeRef + 0x26);                 // image@0x06E63
        for (int i = 0; i < DescriptorBytes / 2; i++)                       // image@0x06E6D rep movsw
        {
            r.SetWord(
                CombatRegisters.ArcParameterDgroupOffset + (i * 2), data.Word(descriptor + (i * 2)));
        }

        uint shifted = (uint)altitudeFeet << 8;                             // image@0x06E6F..0x06E77
        r.SetWord(AltitudeDgroupOffset, unchecked((ushort)shifted));        // image@0x06E79
        r.SetWord(AltitudeDgroupOffset + 2, unchecked((ushort)(shifted >> 16)));  // image@0x06E7C

        if ((data.Byte(prototypeRef + 0x0D) & 2) != 0)                      // image@0x06E84
        {
            EngagementArcParams.Update(context);                            // image@0x06E8A
        }
        else
        {
            r.SetWord(SpeedLowDgroupOffset, data.Word(descriptor + 0x0C));   // image@0x06E90..0x06E96
        }

        short low = unchecked((short)r.Word(SpeedLowDgroupOffset));
        if (!averageSpeeds)
        {
            return low;                                                      // image@0x06EAE
        }

        short high = unchecked((short)r.Word(SpeedHighDgroupOffset));
        return unchecked((short)(unchecked((short)(low + high)) >> 1));       // image@0x06EA2 sar ax,1
    }
}
