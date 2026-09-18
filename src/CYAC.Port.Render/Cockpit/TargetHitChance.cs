using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Combat;

namespace CYAC.Port.Render.Cockpit;

/// <summary>
/// The <b>chance to hit</b> the HUD prints as <c>(nn%)</c> and the in-world designator prints under
/// its box.  They are the SAME number.
/// </summary>
/// <remarks>
/// <para>
/// <c>engagement_hit_pct_compute @image@0x031CB</c> has exactly two callers:
/// <c>hud_per_frame_draw</c> at <c>image@0x0C8B6</c> with <c>g_scene_misc_word_BC [0x00BC]</c>, and
/// <c>hud_engagement_label_draw</c> at <c>image@0x0CE83</c> with the engagement node's own object.
/// Both compute <c>(engagement_fire_authority_compute(target, player, weapon) × 100) &gt;&gt; 8</c>
/// (<c>muldiv16_signed_shr8</c> at <c>image@0x03216</c>).
/// </para>
/// <para>
/// The concept's <c>(open)</c> — "the HUD reads 81% and the designator 1% in the same frame, so they
/// are not the same quantity" — is <b>refuted</b>: the designator's leading digit was hidden behind
/// the cockpit's black gunsight post.
/// a captured frame of the original shows the same label clear of the
/// post reading <c>80%</c>, and <c>20_mig21_cfg02_notarget.png</c> reads <c>81%</c> plainly.
/// </para>
/// <para>
/// <b>Why the formula lives here and not in <c>EngagementFireAuthority</c>.</b>  The sim-side body
/// (<see cref="EngagementFireAuthority.Compute"/>) needs a whole
/// <c>ProjectileKernelContext</c> — a random source, the VM, the lifecycle seams — because two of
/// its arms WRITE: the evasion block seeds <c>g_fire_authority_penalty [0xB49C]</c>
/// (<c>image@0x02BFE</c>) and the cheat arm bumps a census counter.  Presentation in this port is
/// byte-inert by rule, so the HUD path is a READ-ONLY re-expression of the arms a PLAYER-fired shot
/// at a NON-player target can reach, and the two arms it omits are exactly the two it can never
/// take: <c>ApplyPlayerEvasion</c> runs only when <c>targetRef == PlayerObjectRef</c>
/// (<c>image@0x02BF3</c>) and the aircraft-type bonus only when the owner is NOT the player
/// (<c>image@0x02B87</c>).  Everything else — the range curve, the per-shot weight, the guided
/// half-range bonus, the cheat bias and the guided pitch scale — is reproduced instruction for
/// instruction below.
/// </para>
/// </remarks>
public static class TargetHitChance
{
    /// <summary>The scale the percentage comes out on: <c>0x64</c> = 100 (<c>image@0x03213</c>).</summary>
    public const int PercentScale = 0x64;

    /// <summary>The authority's own ceiling — <c>0x100</c> (<c>image@0x02CDE</c>).</summary>
    public const int AuthorityCeiling = 0x0100;

    /// <summary>
    /// The chance the player's currently selected weapon has of hitting one target, 0..100.
    /// </summary>
    /// <param name="inputs">Everything the read-only path needs, gathered by the host.</param>
    /// <returns>The percentage, or −1 when the shot has no computable chance at all.</returns>
    /// <remarks>
    /// −1 is not a value the original produces: it is the port's "do not print a suffix", used where
    /// the original would have divided by zero (<c>prototype[6 + kind] == 0</c> raises <c>#DE</c> and
    /// lands in <c>int00_divzero_isr</c>, <c>image@0x02AEF</c>), where the same <c>div</c> would have
    /// overflowed its 16-bit quotient (the other <c>#DE</c>), or where there is no weapon at all.
    /// </remarks>
    public static int Percent(in TargetHitChanceInputs inputs)
    {
        short authority = Authority(in inputs);
        return authority < 0
            ? -1
            : Fixed.MulDiv16SignedShr8(PercentScale, authority);          // image@0x03216
    }

    /// <summary>
    /// The fire authority itself, 0..0x100 — the read-only half of
    /// <c>engagement_fire_authority_compute @image@0x02A70</c>.
    /// </summary>
    /// <param name="inputs">The gathered inputs.</param>
    /// <returns>The authority, or −1 when it cannot be computed.</returns>
    /// <remarks>
    /// <para>
    /// <b>The law at distance</b>.  Write <c>s = manhattan &gt;&gt; 12</c>, <c>W = weapon[+5] ×
    /// 16</c> and <c>D = prototype[6 + kind]</c>; then <c>ratio = (s × 64) / D</c> and the score is
    /// <c>0x100</c> falling to <c>0x40</c> over <c>ratio ∈ [0, W]</c>, <c>0x40</c> falling to
    /// <c>0</c> over <c>[W, 2W]</c>, and <b>exactly zero at and beyond <c>2W</c></b>
    /// (<c>image@0x02B0E</c> → <c>image@0x02BC9</c>).  There is no band past <c>2W</c> in which the
    /// original reads non-zero, because every shipped divisor is 64, 96 or 128, which keeps
    /// <c>ratio</c> a non-negative <c>short</c> for every <c>s ∈ [0, 0x7FFF]</c> — the
    /// signed-compare band at <c>image@0x02AF7</c> would need <c>D ≤ 63</c>.  So the HUD needs no
    /// range cut-off of its own: the faithful arithmetic is already 0 outside twice the weapon's
    /// reach.  In Manhattan feet (1 ft = 256 world units, <c>image@0x0AB22</c>), against a fighter
    /// (<c>D = 64</c>) / against a B-52D (<c>D = 128</c>): guns 8,192 / 16,384 ft, the AIM-9 class
    /// 15,872 / 31,744 ft, the AIM-7 class 39,936 / 79,872 ft.
    /// </para>
    /// <para>
    /// <c>scaledRange</c> keeps the LOW WORD of a 32-bit shift (<c>mov [bp-0xe],ax</c> after
    /// <c>sar_i32_by_cl</c>, <c>image@0x02AC9</c>), so it turns negative at 2^27 world units and
    /// wraps to zero at 2^28 — 99 and 199 miles of Manhattan range.  That is the original's own law,
    /// not a port artefact, and both bands sit far outside anything a sortie produces.
    /// </para>
    /// </remarks>
    public static short Authority(in TargetHitChanceInputs inputs)
    {
        // image@0x031D2 — the missile MINIMUM-RANGE gate: a weapon with a non-zero [+4] scores zero
        // while the 3-D Manhattan range's HIGH WORD is under that byte.
        int manhattan = CombatGeometry.ManhattanDistance3d(inputs.Owner, inputs.Target);
        if (inputs.MinimumRange != 0
            && unchecked((short)(manhattan >> 16)) < inputs.MinimumRange)   // image@0x031FE
        {
            return 0;                                                       // image@0x03202
        }

        if (inputs.WeaponRange <= 0 || inputs.PrototypeDivisor == 0)
        {
            return -1;
        }

        // image@0x02AB0..0x02AC9 — the range, >> 12, LOW WORD ONLY.
        short scaledRange = unchecked((short)(manhattan >> 12));
        short weaponRange = unchecked((short)(inputs.WeaponRange << 4));    // image@0x02ACC
        if (weaponRange <= 0)
        {
            return -1;
        }

        // image@0x02AD8..0x02AF4 — (range << 6) / prototype[6 + kind].  The dividend is the FULL
        // 32-bit `DX:AX`: `cwd` (image@0x02ADB) sign-extends the low word and `shl_i32_by_cl`
        // (`lcall 0x1000:0x020A`, image@0x02ADE) shifts all 32 bits; the divide is
        // `9a 98 17 1d 20` = `lcall 0x201D:0x1798` → `image@0x11968` = `f7 f3 cb` = `DIV BX; RETF`,
        // an UNSIGNED 32-over-16 divide.
        // The mask made the reading WRAP every 0x400 units of `scaledRange`, i.e. every 2^22 world
        // units (16,384 ft) of Manhattan range: a far target's chance climbed to 100 %, snapped to
        // 0 and climbed again.
        uint quotient = unchecked((uint)(scaledRange << 6)) / inputs.PrototypeDivisor;
        if (quotient > ushort.MaxValue)
        {
            // The original's `div bx` raises #DE here and lands in `int00_divzero_isr`
            // (`image@0x191C8`), which is registered for the projection IDIVs only, so this site
            // takes the UNREGISTERED arm: the latch `[0x784]` goes to 0xFF and the IRET resumes
            // after the DIV with whatever partial quotient the CPU left in AX — undefined.  The
            // port prints no suffix instead (the same answer it already gives a zero divisor).
            // Unreachable on the shipped tables: every prototype divisor is 64, 96 or 128
            // (`data/exe/tables/engagement.json`), so this needs `scaledRange < 0`, i.e. a
            // Manhattan range of 2^27 world units — 524,288 ft, about 99 miles.
            return -1;
        }

        short ratio = unchecked((short)quotient);

        short authority;
        if (weaponRange >= ratio)                                           // image@0x02AF7
        {
            authority = unchecked((short)(
                AuthorityCeiling - Fixed.MulDiv16Signed(0x00C0, ratio, weaponRange)));
        }
        else if (unchecked((short)(weaponRange << 1)) <= ratio)             // image@0x02B0E
        {
            return 0;                                                       // image@0x02BC9
        }
        else
        {
            authority = unchecked((short)(
                0x0040 - Fixed.MulDiv16Signed(
                    0x0040, unchecked((short)(ratio - weaponRange)), weaponRange)));
        }

        // image@0x02B31..0x02B44 — × the class's per-shot weight (UNSIGNED mul), >> 6.
        authority = unchecked((short)(
            ((uint)(ushort)authority * inputs.WeaponWeight) >> 6));

        // image@0x02B46..0x02B84 — the owner IS the player here, so a GUIDED weapon takes the second,
        // gentler curve against half the range, and the cheat byte adds a flat 0x40.
        if (inputs.Guided)
        {
            short halfRange = unchecked((short)(weaponRange >> 1));
            if (halfRange > ratio)                                          // image@0x02B5D
            {
                short alternative = unchecked((short)-(short)(
                    Fixed.MulDiv16Signed(0x0100, ratio, halfRange) - 0x0100));
                if (alternative > authority)                                // image@0x02B77
                {
                    authority = alternative;
                }
            }

            if (inputs.CheatBias)                                           // image@0x02B7D
            {
                authority = unchecked((short)(authority + 0x40));           // image@0x02B84
            }

            // image@0x02BAA..0x02BF1 — a guided shot loses authority the further the LAUNCHER is from
            // wings-level, folded into [0, 0x5A0] and scaled against a half circle.
            short pitch = inputs.OwnerElevation;
            if (pitch >= Angle.HalfCircle)                                   // image@0x02BBA
            {
                short folded = pitch < Angle.ThreeQuarterCircle              // image@0x02BBF
                    ? unchecked((short)(Angle.FullCircle - pitch))
                    : unchecked((short)(pitch - Angle.HalfCircle));

                if (inputs.WeaponKind == 0)                                  // image@0x02BD7
                {
                    folded = unchecked((short)((folded >> 1) + Angle.QuarterCircle));
                }

                authority = Fixed.MulDiv16Signed(authority, folded, Angle.HalfCircle);
            }
        }

        if (authority < 0)                                                   // image@0x02CD7
        {
            return 0;
        }

        return authority > AuthorityCeiling ? (short)AuthorityCeiling : authority;
    }
}

/// <summary>
/// What <see cref="TargetHitChance"/> needs, gathered by the host from the register file, the arena
/// and the static data.
/// </summary>
/// <param name="Owner">The player's world position (the shooter).</param>
/// <param name="OwnerElevation">The player object's <c>+0x14</c> elevation, in angle units.</param>
/// <param name="Target">The target's world position.</param>
/// <param name="PrototypeDivisor">
/// <c>prototype[6 + weapon.Kind]</c> of the TARGET's engagement class prototype
/// (<c>image@0x02AE3</c>); zero means the original would fault.
/// </param>
/// <param name="WeaponRange">The weapon class's <c>+0x05</c> reach byte.</param>
/// <param name="WeaponWeight">Its <c>+0x01</c> per-shot weight.</param>
/// <param name="WeaponKind">Its <c>+0x00</c> kind selector.</param>
/// <param name="MinimumRange">
/// Its <c>+0x04</c> minimum-range byte — the gate <c>engagement_hit_pct_compute</c> applies before
/// anything else (<c>image@0x031D2</c>).
/// </param>
/// <param name="Guided">Its <c>+0x24</c> bit 4.</param>
/// <param name="CheatBias"><c>g_cheat_fire_authority_bonus [0xE46D] != 0</c>.</param>
public readonly record struct TargetHitChanceInputs(
    CombatPosition Owner,
    short OwnerElevation,
    CombatPosition Target,
    byte PrototypeDivisor,
    byte WeaponRange,
    byte WeaponWeight,
    byte WeaponKind,
    byte MinimumRange,
    bool Guided,
    bool CheatBias);
