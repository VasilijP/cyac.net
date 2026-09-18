using CYAC.Port.Core.Sim.Combat.Effects;

namespace CYAC.Port.Render;

/// <summary>
/// One of the smoke class's three disc records as the prepare callback leaves it this frame — the
/// port's view of an <c>s_sprite_class_elem</c> (<c>[0x9B58]</c>, <c>[0x9B60]</c>, <c>[0x9B68]</c>).
/// </summary>
/// <param name="ColorIndex">The record's <c>+0x03</c> palette index.</param>
/// <param name="Stipple">The record's <c>+0x04</c> stipple selector (always one of the three 25 % masks).</param>
/// <param name="Radius">The record's <c>+0x05</c> RADIUS, in model units (exponent 0 — world units).</param>
/// <param name="Flags">
/// The record's <c>+0x00</c> tag byte after the callback's bit-3/bit-4 edits.  What the JIT'd disc
/// emitter makes of bit 3 is <b>(open)</b> — the renderer ignores it.
/// </param>
public readonly record struct SmokeSpriteElement(byte ColorIndex, byte Stipple, int Radius, byte Flags);

/// <summary>
/// THE SMOKE PUFF'S SIZE AND COLOUR LAW — the port of the <c>smoke</c> class's per-object PREPARE
/// callback <c>smoke_sprite_render_params_setup @image@0x0B2FF</c> (319 B, verified; Presentation, like
/// <see cref="EffectLook"/>: the sim hands in the puff's kind, age and span
/// (<see cref="SmokePuffState"/>) and this type turns them into three records.
/// </summary>
/// <remarks>
/// <para>
/// The static class data draws three small 25 %-coverage discs of radius 18 / 20 / 16
/// (<c>image@0x458B8..0x458CF</c>: <c>03 58 9B 07 28 12 00 00 | 03 58 9B 08 14 14 00 01 |
/// 03 58 9B 07 41 10 00 02</c>).  Drawing only those gives puffs that are far too small: the
/// original draws a large column of overlapping smoke balls, because the callback rewrites those
/// records EVERY FRAME from the puff's age:
/// </para>
/// <code>
///   age  = [0xF0D2] − (slot[+4] &lt;&lt; 8)                      image@0x0B328..0x0B331
///   span = (slot[+6] − slot[+4]) &lt;&lt; 8                       image@0x0B320..0x0B326
///   [0x9B5D] = muldiv16_signed(0xAF, age, span) + 0x19    image@0x0B335..0x0B343   25 → 200
///   [0x9B65] = muldiv16_signed(0xF5, age, span) + 0x23    image@0x0B348..0x0B356   35 → 280
///   [0x9B6D] = muldiv16_signed(0xD2, age, span) + 0x1E    image@0x0B35B..0x0B369   30 → 240
///   [0x9B58] &amp;= 0xE7                                      image@0x0B36C
///   switch slot[+8]                                       image@0x0B371..0x0B389
/// </code>
/// <para>
/// So a puff GROWS by roughly ten times over its life — 45 s for kind 0, 25 s for the wreck
/// column, 5 s for a damage-trail puff — and the emitter's next puff is born small under it,
/// which is the column of overlapping balls.  The three <c>sprite_id</c> words the arms write
/// are <c>(stipple &lt;&lt; 8) | colour</c>: <c>0x28</c>/<c>0x14</c>/<c>0x41</c> are the three
/// 2-of-8 row masks (25 % coverage, <c>MeshFace.Coverage</c>) and the low byte is the palette
/// index — 7 grey, 8 dark grey, 4 dark red, 0x0C bright red, 0x0F white.
/// </para>
/// <para>
/// Per kind (<c>image@0x0B38C..0x0B437</c>): <b>0</b> writes <c>0x280C/0x1404/0x4108</c> (a red
/// fire core with grey) and then OVERRIDES all three radii with the constants 25/35/30 — a
/// stationary puff never grows; <b>1</b> (<c>0x2808</c>, dark grey) and <b>2</b> (<c>0x280F</c>,
/// white) set bit 3 on records 1 and 2 and HALVE record 0's radius (<c>sar</c> @<c>image@0x0B3D6</c>);
/// <b>3</b> writes <c>0x2807/0x1408/0x4107</c> (grey) and is gated on the graphics-detail setting
/// <c>[0xF108]</c> — <c>cmp [0xF108],1 / ja</c> @<c>image@0x0B3FB</c>: the HIGH arm only clears
/// record 2's bits 3+4, the LOW/MEDIUM arm sets bit 3 on records 0 and 2 and grows record 1 by 3/2
/// (the two arms are easy to read the wrong way round); <b>4</b> writes <c>0x280F</c> and
/// sets bit 3 on records 1 and 2; <b>≥ 5</b> leaves the ids and flags alone (the ramped radii
/// still stand).  The port is a high-detail machine and takes the HIGH arm unless asked otherwise.
/// </para>
/// <para>
/// <b>Labelled deviation.</b>  The three records are GLOBAL DGROUP state shared by every puff, and
/// kinds 1, 2 and 4 never write records 1 and 2's ids — in the original those two discs keep the
/// colour the LAST kind-0 or kind-3 puff drawn left behind (dark red + dark grey after a fire
/// puff, dark grey + grey after a column puff), an order-of-draw accident.  The port gives them
/// the static image's values (<c>0x1408</c> / <c>0x4107</c>, which are also kind 3's) — one
/// deterministic look instead of a paint-order lottery.  Bit 3 of the tag byte is carried in
/// <see cref="SmokeSpriteElement.Flags"/> but not interpreted: the disc emitter the JIT builds for
/// these records is out of the image, and no byte in it has been read for a tag test
/// <b>(open)</b>.
/// </para>
/// </remarks>
public static class SmokeLook
{
    /// <summary>How many disc records the class has: 3 (<c>image@0x458D0</c>: <c>03 03 …</c>).</summary>
    public const int ElementCount = 3;

    /// <summary>
    /// <b>A LABELLED PORT ADDITION: the WRECK-TRAIL kind, which the original never had.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// A PORT ADDITION: a plane that is shot down and falling should emit black puffs of smoke, so
    /// that it falls with a black trail behind it.  The original's damage trail is <b>kind 1</b> —
    /// <c>0x2808</c>, palette 8 dark grey (<c>image@0x0B3BC</c>) — and it fires a few beads and
    /// stops; the port's layer bears
    /// its own puffs along a falling wreck's path (<c>SmokeTrail.WreckTrail</c>) and paints them
    /// BLACK-ISH.  Kinds <c>0x80 + c</c> are that arm: the shape and the ramp are kind 1's exactly,
    /// and all three discs are painted in <b>palette index <c>c</c></b> (<c>--wreck-smoke-color</c>,
    /// 0…<see cref="WreckTrailMaxColor"/>).
    /// </para>
    /// <para>
    /// The colour rides in the kind byte rather than in a new field because <c>SmokePuffState</c>
    /// (<c>Sim/Combat/Effects</c>) is kernel neighbourhood and <c>SceneInstance</c> carries
    /// no per-instance palette: the whole addition therefore lives in this file and in
    /// <c>SmokeTrail</c>, and kinds 0–4 — the original's — are not touched by one byte.  The original
    /// itself treats any kind ≥ 5 as "leave the ids alone" (<c>image@0x0B389</c>), so no shipped puff
    /// can ever reach this arm.
    /// </para>
    /// </remarks>
    public const byte WreckTrailKindBase = CYAC.Port.Core.Sim.Session.SmokeTrail.WreckTrailKindBase;

    /// <summary>The largest palette index the wreck-trail kind byte can carry: 0x7F.</summary>
    public const byte WreckTrailMaxColor = CYAC.Port.Core.Sim.Session.SmokeTrail.WreckTrailMaxColor;

    /// <summary>The static image's tag byte on all three records: <c>0x03</c>.</summary>
    public const byte StaticFlags = 0x03;

    /// <summary>The bit the arms set: bit 3 (<c>or al,8</c>).</summary>
    public const byte ScaleFlag = 0x08;

    /// <summary>The mask the arms clear with: bits 3 and 4 (<c>and …,0xE7</c>).</summary>
    public const byte ClearMask = 0xE7;

    /// <summary>The static image's three <c>sprite_id</c> words: <c>0x2807</c>, <c>0x1408</c>, <c>0x4107</c>.</summary>
    public static ReadOnlySpan<ushort> StaticSpriteIds => [0x2807, 0x1408, 0x4107];

    /// <summary>The ramps' multipliers <c>0xAF</c>, <c>0xF5</c>, <c>0xD2</c> (<c>image@0x0B335</c>, <c>0x0B348</c>, <c>0x0B35B</c>).</summary>
    public static ReadOnlySpan<int> GrowthFactors => [0xAF, 0xF5, 0xD2];

    /// <summary>The ramps' offsets — the radii at birth — <c>0x19</c>, <c>0x23</c>, <c>0x1E</c>.</summary>
    public static ReadOnlySpan<int> BirthRadii => [0x19, 0x23, 0x1E];

    /// <summary>
    /// Computes the three records as the callback leaves them for one puff.
    /// </summary>
    /// <param name="puff">The puff's kind, age and span.</param>
    /// <param name="reducedArm">
    /// Take kind 3's <c>[0xF108] &lt;= 1</c> arm (the shipped LOW/MEDIUM look: bit 3 on records 0
    /// and 2, record 1 grown by 3/2).  False — the port's default — is the HIGH arm.
    /// </param>
    /// <param name="elements">Receives the three records; must hold at least <see cref="ElementCount"/>.</param>
    public static void Fill(in SmokePuffState puff, bool reducedArm, Span<SmokeSpriteElement> elements)
    {
        if (elements.Length < ElementCount)
        {
            throw new ArgumentException("Three elements are needed.", nameof(elements));
        }

        // Phase 2 — the three ramps (image@0x0B335..0x0B369).  muldiv16_signed is a 32-bit
        // product over a 16-bit quotient, truncating toward zero; C# integer division does the same.
        Span<int> radius = stackalloc int[ElementCount];
        for (int i = 0; i < ElementCount; i++)
        {
            radius[i] = MulDiv(GrowthFactors[i], puff.AgeFrameTime, puff.SpanFrameTime) + BirthRadii[i];
        }

        Span<ushort> id = stackalloc ushort[ElementCount];
        Span<byte> flags = stackalloc byte[ElementCount];
        for (int i = 0; i < ElementCount; i++)
        {
            id[i] = StaticSpriteIds[i];
            flags[i] = StaticFlags;
        }

        flags[0] &= ClearMask;                                              // image@0x0B36C

        // The PORT's own wreck-trail arm, taken before the original's dispatch: kind 1's shape
        // (id 0x2808, bit 3 on records 1 and 2, record 0's radius halved) with all three discs
        // repainted in the kind byte's own palette index.  Kinds 0–4 below are untouched.
        if (puff.Kind >= WreckTrailKindBase)
        {
            byte colour = (byte)(puff.Kind - WreckTrailKindBase);
            id[0] = (ushort)((0x28 << 8) | colour);                         // kind 1's stipple
            id[1] = (ushort)((0x14 << 8) | colour);
            id[2] = (ushort)((0x41 << 8) | colour);
            SetScale(ref flags[1]);
            SetScale(ref flags[2]);
            radius[0] >>= 1;                                                // image@0x0B3D6 sar
            Emit(id, radius, flags, elements);
            return;
        }

        // Phase 4 — the kind dispatch (image@0x0B371..0x0B437).
        switch (puff.Kind)
        {
            case 0:                                                         // image@0x0B38C
                id[0] = 0x280C;
                id[1] = 0x1404;
                id[2] = 0x4108;
                flags[1] &= ClearMask;
                flags[2] &= ClearMask;
                radius[0] = 0x19;                                           // image@0x0B3A8 — fixed
                radius[1] = 0x23;
                radius[2] = 0x1E;
                break;

            case 1:                                                         // image@0x0B3BC
                id[0] = 0x2808;
                SetScale(ref flags[1]);
                SetScale(ref flags[2]);
                radius[0] >>= 1;                                            // image@0x0B3D6 sar
                break;

            case 2:                                                         // image@0x0B3DC
                id[0] = 0x280F;
                SetScale(ref flags[1]);
                SetScale(ref flags[2]);
                radius[0] >>= 1;
                break;

            case 3:                                                         // image@0x0B3E4
                id[0] = 0x2807;
                id[1] = 0x1408;
                id[2] = 0x4107;
                flags[1] &= ClearMask;
                if (!reducedArm)                                            // image@0x0B3FB cmp,1 / ja
                {
                    flags[2] &= ClearMask;                                  // image@0x0B417
                }
                else
                {
                    SetScale(ref flags[0]);                                 // image@0x0B402
                    radius[1] += radius[1] >> 1;                            // image@0x0B40C..0x0B411 ×3/2
                    SetScale(ref flags[2]);                                 // image@0x0B42E
                }

                break;

            case 4:                                                         // image@0x0B41E
                id[0] = 0x280F;
                SetScale(ref flags[1]);
                SetScale(ref flags[2]);
                break;

            default:                                                        // image@0x0B389 — no-op
                break;
        }

        Emit(id, radius, flags, elements);
    }

    /// <summary>Packs the three <c>sprite_id</c> words, radii and tag bytes into the caller's span.</summary>
    private static void Emit(
        ReadOnlySpan<ushort> id,
        ReadOnlySpan<int> radius,
        ReadOnlySpan<byte> flags,
        Span<SmokeSpriteElement> elements)
    {
        for (int i = 0; i < ElementCount; i++)
        {
            elements[i] = new SmokeSpriteElement(
                (byte)(id[i] & 0xFF), (byte)(id[i] >> 8), radius[i], flags[i]);
        }
    }

    /// <summary><c>(a × b) / c</c> in 32-bit, truncating toward zero — <c>muldiv16_signed @image@0x11974</c>.  A zero span yields 0 (the original would fault; no shipped lifetime is 0).</summary>
    public static int MulDiv(int a, int b, int c) =>
        c == 0 ? 0 : (int)((long)a * b / c);

    private static void SetScale(ref byte flags) => flags = (byte)((flags & 0xEF) | ScaleFlag);   // and 0xEF / or 8
}
