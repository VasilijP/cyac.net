using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Render;

/// <summary>
/// How the port draws the ENGINE's visual effects — the explosion anchor, its debris, the smoke
/// puffs and the countermeasure clouds.
/// </summary>
/// <remarks>
/// <para>
/// <b>The opcode-4 record is not a shape, it is a CALLBACK.</b>
/// <c>mesh_poly_emit_effect_cb @image@0x1B55A</c> projects the record's one vertex and then
/// <c>lcall [si+3]</c> — a FAR POINTER stored in <c>record[+3..+6]</c>
/// (1107, P293).  For <c>explosio</c> that record is at
/// <c>image@0x417AC</c> and reads <c>04 00 00 38 35 8E 10 00</c>, i.e. the callback is
/// <c>0x108E:0x3538</c> = <c>image@0x03E18</c> = <c>deferred_effect_render</c>.  H6b published it as
/// <c>effectCallback</c> in <c>data/exe/meshes/explosio.json</c>.
/// </para>
/// <para>
/// <b>The callback FORKS on the record's <c>+0x0C</c> byte</b> (<c>cmp byte [bx+0xc],0 / jne</c>
/// @<c>image@0x03E30</c>), and H7 plumbed that byte and the record's AGE through to the renderer
/// (<c>SceneInstance.EffectFork</c> / <c>.EffectAgeFrameTime</c>), so the port now draws BOTH arms:
/// </para>
/// <list type="bullet">
///   <item><description><b><c>+0x0C == 0</c> — <see cref="ParticleDraw"/></b>
///   (<c>effect_particle_draw @image@0x03C5A</c>, 95.7 % of calls on a recorded sortie): one filled
///   palette-7 disc of WORLD radius <c>8 + 20·age/256</c> whose dither thins with age, plus — detail
///   ≥ 1 — eight solid palette-15 triangles of leg <c>R/8</c> at screen radii
///   <c>proj(6i·age/256)</c> and BAM angles <c>[0x0E20][(seq+i)&amp;7]</c>.</description></item>
///   <item><description><b><c>+0x0C != 0</c> — <see cref="BurstDraw"/></b>
///   (<c>image@0x03E6C</c>): with the "Bitmap Explosions" option on AND the sprite loaded, the
///   <c>exp.rle</c> bitmap at a depth-derived scale; otherwise SIX filled discs on a hexagon flying
///   outward plus SIXTEEN solid palette-12 triangles.</description></item>
/// </list>
/// <para>
/// <b>Transform ask:</b> the six DGROUP tables this type transcribed — <c>[0x0E16]</c>,
/// <c>[0x0E20]</c>, <c>[0x0E30]</c>, <c>[0x0E3C]</c>, <c>[0x0E48]</c> and <c>[0x0E52]</c>, 76 bytes
/// at <c>image@0x3CB76..0x3CBD1</c> — the two debris angle tables <c>[0x0E20]</c> and
/// <c>[0x0E52]</c> are data and now live in the tree's <c>exe/tables/effect_look.json</c>; the
/// renderer reads them through <see cref="EffectDebrisAngles"/>.  The two fade ramps and the two
/// drift tables are still carried here, pending the logical sweep.
/// </para>
/// </remarks>
public static class EffectLook
{
    /// <summary>
    /// The palette index the explosion disc is drawn in: <b>7</b>.
    /// </summary>
    /// <remarks><c>mov al,7</c> @<c>image@0x03CD3</c> — a constant, not the record's byte.</remarks>
    public const byte ExplosionColorIndex = 7;

    /// <summary>The palette index the eight debris shards are drawn in: <b>15</b>, solid.</summary>
    /// <remarks><c>mov ax,0xff0f</c> @<c>image@0x03DEF</c> — <c>AL</c> palette, <c>AH</c> 0xFF solid.</remarks>
    public const byte ShardColorIndex = 15;

    /// <summary>The palette index the BURST arm's sixteen shards are drawn in: <b>12</b>, solid.</summary>
    /// <remarks><c>mov ax,0xff0c</c> @<c>image@0x0404D</c>.</remarks>
    public const byte BurstShardColorIndex = 12;

    /// <summary>The two palette indices the BURST arm's six discs alternate between: 8 and 7.</summary>
    /// <remarks>
    /// <c>al = i&amp;1; cmp ax,1; sbb al,al; and al,1; add al,7</c> @<c>image@0x03FF5..0x04002</c> —
    /// an EVEN <c>i</c> gives 8, an odd one 7.
    /// </remarks>
    public static byte BurstDiscColorIndex(int index) => (byte)((index & 1) == 0 ? 8 : 7);

    /// <summary>The explosion disc's world radius at birth: <b>8</b>.</summary>
    /// <remarks><c>add si,8</c> @<c>image@0x03C7C</c>.</remarks>
    public const double ExplosionBirthRadiusWorldUnits = 8.0;

    /// <summary>The explosion disc's world radius at the end of its life: <b>28</b>.</summary>
    /// <remarks><c>muldiv16(0x14, age, 0x100) + 8</c> @<c>image@0x03C6F</c>.</remarks>
    public const double ExplosionDeathRadiusWorldUnits = 28.0;

    /// <summary>A record's life in frame-time units: <c>0x100</c>.</summary>
    /// <remarks>
    /// <c>deferred_effect_record_schedule</c> stamps <c>record[+6] = now + 0x100</c>
    /// (<c>image@0x03B71</c>) and the per-frame tick fires it then.
    /// </remarks>
    public const int LifeFrameTime = 0x100;

    /// <summary>
    /// The radius a port draws an explosion anchor at when the sim did not say how old it is.
    /// </summary>
    /// <remarks>
    /// The age IS plumbed now (<c>SceneInstance.EffectAgeFrameTime</c>); this constant is the
    /// fallback for an anchor with no record, e.g. the host's <c>--effect-probe</c>.
    /// </remarks>
    public const double ExplosionRadiusWorldUnits =
        (ExplosionBirthRadiusWorldUnits + ExplosionDeathRadiusWorldUnits) / 2.0;

    /// <summary>
    /// <c>si = muldiv16(0x14, age, 0x100) + 8</c> @<c>image@0x03C6F</c> — the disc's world radius at
    /// an age, reproduced with the original's truncating integer arithmetic.
    /// </summary>
    /// <param name="ageFrameTime">The record's age in frame-time units.</param>
    /// <returns>The world radius, 8 at birth rising to 28 at the end of the life.</returns>
    public static double DiscRadiusWorldUnits(int ageFrameTime) =>
        8 + (0x14 * Clamp(ageFrameTime) / 0x100);

    /// <summary>
    /// <c>ax = muldiv16(0x32, age, 0x100) + 0x19</c> @<c>image@0x03E97</c> — the BURST arm's world
    /// radius, 25 at birth rising to 75.
    /// </summary>
    /// <param name="ageFrameTime">The record's age.</param>
    /// <returns>The world radius.</returns>
    public static double BurstRadiusWorldUnits(int ageFrameTime) =>
        0x19 + (0x32 * Clamp(ageFrameTime) / 0x100);

    /// <summary>
    /// The i'th debris shard's world distance from the centre:
    /// <c>muldiv16(6i, age, 0x100)</c> @<c>image@0x03D09</c> (the loop counter walks
    /// <c>[bp-4]</c> 0, 6, 12 … 42).
    /// </summary>
    /// <param name="index">The shard, 0…7.</param>
    /// <param name="ageFrameTime">The record's age.</param>
    /// <returns>The world distance.</returns>
    public static double ShardDistanceWorldUnits(int index, int ageFrameTime) =>
        6 * index * Clamp(ageFrameTime) / 256.0;

    /// <summary>
    /// The i'th BURST shard's world distance: <c>muldiv16(8i, age, 0x100)</c>
    /// @<c>image@0x0406D..0x0407A</c>.
    /// </summary>
    /// <param name="index">The shard, 0…15.</param>
    /// <param name="ageFrameTime">The record's age.</param>
    /// <returns>The world distance.</returns>
    public static double BurstShardDistanceWorldUnits(int index, int ageFrameTime) =>
        8 * index * Clamp(ageFrameTime) / 256.0;

    /// <summary>How many debris shards the disc arm draws: <b>8</b>.</summary>
    /// <remarks><c>add word [bp-4],6 / cmp word [bp-4],0x30 / jge</c> @<c>image@0x03E04</c>.</remarks>
    public const int ShardCount = EffectDebrisAngles.ShardCount;

    /// <summary>How many discs the burst arm draws: <b>6</b>.</summary>
    /// <remarks><c>cmp word [bp-0x22],6 / jge</c> @<c>image@0x04010</c>.</remarks>
    public const int BurstDiscCount = 6;

    /// <summary>How many shards the burst arm draws: <b>16</b>.</summary>
    /// <remarks><c>cmp word [bp-0x22],0x10 / jl</c> @<c>image@0x04064</c>.</remarks>
    public const int BurstShardCount = EffectDebrisAngles.BurstShardCount;

    /// <summary>
    /// <c>g_effect_fade_dither_ramp [0x0E16]</c> — the five fill-mode selectors an explosion walks
    /// as it ages, read from <c>image@0x3CB76</c> (stride 2: <c>FF BE 5A 14 41</c>).
    /// </summary>
    /// <remarks>
    /// <c>ah = [bx + 0x0E16]</c> with <c>bx = 2·muldiv16(4, age, 0x100)</c>
    /// (<c>image@0x03CC2..0x03CD5</c>), i.e. the index is <c>age/64</c> ∈ [0,3] for a live record.
    /// The byte is the same 4+4-bit stipple selector the shape records carry
    /// (<c>gfx_set_active_color @image@0x139A4</c>): <c>0xFF</c> solid, else <c>(v&amp;0xF)/15</c>
    /// and <c>(v&gt;&gt;4)/15</c> row coverages.
    /// </remarks>
    public static ReadOnlySpan<byte> FadeDitherRamp => [0xFF, 0xBE, 0x5A, 0x14, 0x41];

    /// <summary>
    /// The BURST arm's own copy of the ramp, <c>[0x0E48]</c> at <c>image@0x3CBA8</c> — byte for byte
    /// the same five selectors, indexed the same way (<c>image@0x03F5C..0x03F72</c>).
    /// </summary>
    public static ReadOnlySpan<byte> BurstFadeDitherRamp => [0xFF, 0xBE, 0x5A, 0x14, 0x41];

    // / moved into the tree in P4-G2,: g_effect_particle_angle_table [0x0E20] (image@0x3CB80, read
    // by `push word [bx + 0x0E20]` with bx = 2·((record[+0x0A] + i) & 7) @image@0x03D60..0x03D6C)
    // and the burst arm's [0x0E52] (image@0x3CBB2, `push word [bx + 0x0E52]` @image@0x040DC) are
    // shipped angles, so exe/tables/effect_look.json carries them and SceneRenderer reads
    // EffectDebrisAngles.

    /// <summary>
    /// <c>[0x0E30]</c> / <c>[0x0E3C]</c> at <c>image@0x3CB90</c>/<c>0x3CB9C</c> — the six burst
    /// discs' per-unit-age X and Y drift.  They are a HEXAGON of radius 45: <c>(0,45) (39,24)
    /// (39,−24) (0,−45) (−39,−24) (−39,24)</c>, since 45·cos30° = 38.97 and 45·sin30° = 22.5.
    /// </summary>
    /// <remarks>
    /// <c>[bp-0x20] = muldiv16([0x0E30 + 2i], age, 0x100)</c> and
    /// <c>[bp-0x28] = muldiv16([0x0E3C + 2i], age, 0x100)</c> @<c>image@0x03F7F..0x03FA2</c>; the
    /// pair is staged as the X and Y of a world vector at the record's own depth and projected, so
    /// they are WORLD offsets, not screen ones.
    /// </remarks>
    public static ReadOnlySpan<short> BurstDiscDriftX => [0, 39, 39, 0, -39, -39];

    /// <summary>The Y half of <see cref="BurstDiscDriftX"/>, <c>[0x0E3C]</c>.</summary>
    public static ReadOnlySpan<short> BurstDiscDriftY => [45, 24, -24, -45, -24, 24];

    /// <summary>
    /// The dither selector an explosion of this age is drawn with — <c>ramp[age/64]</c>, clamped to
    /// the table (<c>image@0x03CC2</c>).
    /// </summary>
    /// <param name="ageFrameTime">The record's age.</param>
    /// <returns>The 4+4-bit selector; <c>0xFF</c> is solid.</returns>
    public static byte FadeSelector(int ageFrameTime)
    {
        int index = 4 * Clamp(ageFrameTime) / 0x100;
        ReadOnlySpan<byte> ramp = FadeDitherRamp;
        return ramp[Math.Clamp(index, 0, ramp.Length - 1)];
    }

    /// <summary>
    /// A stipple selector read as a COVERAGE, 0…1 — the port's "represent, don't reproduce" reading
    /// of the 4+4-bit row masks (the same conversion <c>MeshFace.Coverage</c> makes for a shape
    /// record's <c>+0x04</c>).
    /// </summary>
    /// <param name="selector">The selector byte.</param>
    /// <returns>The mean of the two nibbles' <c>n/15</c> coverages; <c>0xFF</c> gives 1.</returns>
    public static double CoverageOf(byte selector) =>
        selector == 0xFF ? 1.0 : (((selector & 0x0F) + (selector >> 4)) / 30.0);

    /// <summary>
    /// The classes whose DISC records the port draws with a radial falloff.
    /// </summary>
    /// <remarks>
    /// <c>smoke</c> is three 25 %-coverage discs 16–20 is only the STATIC image; the class's
    /// prepare callback grows them 25 → 200/280/240 over the puff's life, <see cref="SmokeLook"/>
    /// at exponent 0 and <c>chaff</c> is one; drawn hard-edged at 1080p they read as flat coins.
    /// Everything else — clouds, propeller discs, the ground grid's balls — keeps its hard edge.
    /// </remarks>
    /// <param name="basename">The mesh class's basename.</param>
    /// <returns>Whether it is drawn soft.</returns>
    public static bool Soft(string basename) =>
        basename is "smoke" or "chaff" or "explosio";

    // ---------------------------------------------------------------- the bitmap-explosion arm

    /// <summary>
    /// The bitmap explosion's nominal WIDTH at unit scale, in original 320×200 pixels: <b>0x50</b>.
    /// </summary>
    /// <remarks><c>mov ax,0x50 / mov dx,[bp-0x1e] / lcall muldiv16_signed_shr8</c> @<c>image@0x03F3A</c>.</remarks>
    public const int BitmapNominalWidth = 0x50;

    /// <summary>
    /// Its nominal HEIGHT: <b>0x57</b> = 87 — exactly <c>exp.rle</c>'s own pixel height.
    /// </summary>
    /// <remarks><c>mov ax,0x57</c> @<c>image@0x03F46</c>; <c>data/images/exp.json</c> is 115 × 87.</remarks>
    public const int BitmapNominalHeight = 0x57;

    /// <summary>The multiplier the projected radius is scaled by before the cap: <b>10</b>.</summary>
    /// <remarks>
    /// <c>mov cx,ax / shl ax,1 / shl ax,1 / add ax,cx / shl ax,1</c> @<c>image@0x03EED</c> — the
    /// classic <c>(x·5)·2</c>.
    /// </remarks>
    public const int BitmapScaleMultiplier = 10;

    /// <summary>The cap on that scale: <b>0x190</b> = 400.</summary>
    /// <remarks><c>cmp ax,0x190 / jle</c> @<c>image@0x03EFA</c>.</remarks>
    public const int BitmapScaleCap = 0x190;

    /// <summary>
    /// The screen-space bounds outside which the bitmap arm draws NOTHING, in original 320×200
    /// pixels: x ∈ [−400, 720], y ∈ [−400, 600].
    /// </summary>
    /// <remarks>
    /// <c>cmp word [bp+8],0xfe70 / jge</c> … <c>cmp word [bp+8],0x2d0 / jle</c> … <c>cmp word
    /// [bp+0xa],0xfe70</c> … <c>cmp word [bp+0xa],0x258</c> @<c>image@0x03F04..0x03F27</c>.
    /// Deliberately far outside a 320×200 screen — it is a coordinate sanity guard, not a
    /// clip, and the blitter clips for real.
    /// </remarks>
    public static (int MinX, int MaxX, int MinY, int MaxY) BitmapScreenGuard => (-0x190, 0x2D0, -0x190, 0x258);

    /// <summary>
    /// The bitmap explosion's destination size in original 320×200 pixels at a projected radius.
    /// </summary>
    /// <param name="screenRadius320">
    /// The projected screen radius, in 320×200 pixels, of a world offset of
    /// <see cref="BurstRadiusWorldUnits"/> at the record's depth (<c>[bp-0x1E]</c>,
    /// <c>image@0x03EDC</c>).
    /// </param>
    /// <returns>The destination width and height, the original's own truncating arithmetic.</returns>
    public static (int Width, int Height) BitmapSize(int screenRadius320)
    {
        int scale = Math.Min(screenRadius320 * BitmapScaleMultiplier, BitmapScaleCap);
        return (BitmapNominalWidth * scale >> 8, BitmapNominalHeight * scale >> 8);
    }

    /// <summary>Clamps an age into the record's own life, so a stale record cannot grow forever.</summary>
    /// <param name="ageFrameTime">The age.</param>
    /// <returns>The age in <c>[0, 0x100]</c>.</returns>
    private static int Clamp(int ageFrameTime) => Math.Clamp(ageFrameTime, 0, LifeFrameTime);
}
