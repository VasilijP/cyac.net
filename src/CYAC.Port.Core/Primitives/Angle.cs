namespace CYAC.Port.Core.Primitives;

/// <summary>
/// An angle on the original's 2880-unit circle (1/8° per unit), in navigation convention:
/// 0 = North / forward, increasing clockwise.
/// </summary>
/// <remarks>
/// <para>
/// INT-only: angles are part of the reproducible spine — flight-model heading, bearing, AI aim,
/// mission scripting — so this type keeps the original's 16-bit width and wrap semantics on
/// purpose.  A "fixed" overflow here is a gameplay divergence.
/// </para>
/// <para>
/// Source of truth: (full circle <c>0xB40</c> = 2880 = 360° × 8), ("BAM units [0, 0xB40)"), and the
/// original helpers <c>angle_wrap_0_to_0xB40 @image@0x18410</c>, <c>angle_delta_add_wrap_0xB40
/// @image@0x1842A</c>, <c>angle_normalize_i16 @image@0x244C8</c>. All three are behaviourally
/// verified against the original's bytes by the project's difftest track
/// (<c>src/CYAC.Tools.SlrUnpacker/DiffTest.cs:220</c> — exhaustive over all 65536 i16 inputs — and
/// <c>DiffTest.Batch3.cs:83</c>).
/// </para>
/// <para>
/// The primary constructor does NOT wrap: it is the raw 16-bit state word, exactly as the original
/// stores it.  Use <see cref="Wrap(short)"/> or <see cref="FromUnits(int)"/> to build a canonical
/// angle; <see cref="IsCanonical"/> reports whether a value is in <c>[0, 0xB40)</c>.
/// </para>
/// </remarks>
/// <param name="Units">The raw angle word, in 1/8° units.</param>
public readonly record struct Angle(ushort Units)
{
    /// <summary>Full circle: <c>0xB40</c> = 2880 units = 360°.</summary>
    public const int FullCircle = 0xB40;

    /// <summary>Half circle: <c>0x5A0</c> = 1440 units = 180°.</summary>
    public const int HalfCircle = 0x5A0;

    /// <summary>Quarter circle: <c>0x2D0</c> = 720 units = 90°.</summary>
    public const int QuarterCircle = 0x2D0;

    /// <summary>Three-quarter circle: <c>0x870</c> = 2160 units = 270°.</summary>
    public const int ThreeQuarterCircle = 0x870;

    /// <summary>Units per degree: 8 (the original's angular resolution is 1/8°).</summary>
    public const int UnitsPerDegree = 8;

    /// <summary>North / forward — the origin of the navigation circle.</summary>
    public static Angle Zero => new(0);

    /// <summary>True when <see cref="Units"/> lies in <c>[0, 0xB40)</c>, the wrapped range.</summary>
    public bool IsCanonical => Units < FullCircle;

    /// <summary>
    /// Wraps a signed 16-bit angle word into <c>[0, 0xB40)</c> — the original
    /// <c>angle_wrap_0_to_0xB40 @image@0x18410</c>.
    /// </summary>
    /// <remarks>
    /// The original repeatedly adds <c>0xB40</c> while the value is negative and subtracts it while
    /// the value is <c>&gt;= 0xB40</c> (<c>image@0x1841A</c> / <c>image@0x18420</c>); that is a
    /// positive modulo, and the difftest verified the identity EXHAUSTIVELY over all 65536 signed
    /// inputs (<c>DiffTest.cs:220</c>).  The closed form below is that identity.
    /// </remarks>
    public static Angle Wrap(short units)
    {
        int r = units % FullCircle;
        if (r < 0)
        {
            r += FullCircle;
        }

        return new Angle((ushort)r);
    }

    /// <summary>
    /// Port convenience: wraps any <see cref="int"/> into <c>[0, 0xB40)</c> by positive modulo.
    /// </summary>
    /// <remarks>
    /// This has no counterpart in the original (which only ever wraps a 16-bit register).  It agrees
    /// with <see cref="Wrap(short)"/> for every value a <see cref="short"/> can hold; for wider
    /// inputs it is a port-side helper for authoring and tests, not a claim about the game.
    /// </remarks>
    public static Angle FromUnits(int units)
    {
        int r = units % FullCircle;
        if (r < 0)
        {
            r += FullCircle;
        }

        return new Angle((ushort)r);
    }

    /// <summary>
    /// Adds a signed delta with the original's semantics: a 16-bit add that wraps mod 2^16, then a
    /// wrap into <c>[0, 0xB40)</c>.  Mirrors <c>angle_delta_add_wrap_0xB40 @image@0x1842A</c>.
    /// </summary>
    /// <remarks>
    /// The original is <c>ADD AX,[BX]</c> (16-bit, wrapping) followed by the same wrap loop as
    /// <see cref="Wrap(short)"/>, and it writes the wrapped result BACK through <c>BX</c> as well as
    /// returning it in <c>AX</c> (<c>image@0x18433</c> / <c>0x1843B</c> / <c>0x18446</c>).  The port
    /// splits that: this method returns the new angle and the caller stores it.  Verified against
    /// the original's bytes by <c>DiffTest.Batch3.cs:83</c>.
    /// </remarks>
    public Angle Add(short delta) => Wrap(unchecked((short)(Units + delta)));

    /// <summary>
    /// One normalization step toward <c>[-1440, 1440)</c> — the original
    /// <c>angle_normalize_i16 @image@0x244C8</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Byte-exact shape (<c>image@0x244C8</c>): <c>if (v &gt;= 0x5A0) v -= 0xB40; else if (v &lt;=
    /// -0x5A0) v += 0xB40;</c> — note <c>cmp ax,0xFA60</c> is the signed compare against -1440.
    /// </para>
    /// <para>
    /// It is a SINGLE step, not a full normalization: an input of 32767 comes back as 29887, still
    /// far outside the range.  That is the original's behaviour and callers rely on feeding it a
    /// difference of two already-wrapped angles.  Verified by <c>DiffTest.cs:316</c> (exhaustive
    /// over all 65536 i16 inputs).
    /// </para>
    /// </remarks>
    public static short Normalize(short value)
    {
        if (value >= HalfCircle)
        {
            return unchecked((short)(value - FullCircle));
        }

        if (value > -HalfCircle)
        {
            return value;
        }

        return unchecked((short)(value + FullCircle));
    }

    /// <summary>
    /// The shortest signed turn FROM this angle TO <paramref name="other"/>, in 1/8° units;
    /// positive is clockwise.  Result range is <c>[-1440, 1440]</c>.
    /// </summary>
    /// <remarks>
    /// Built from the original's own primitives — a 16-bit difference fed through
    /// <see cref="Normalize(short)"/> — so it agrees with the game wherever the game computes a
    /// bearing error the same way.  Both angles must be canonical (<see cref="IsCanonical"/>); the
    /// result range is only guaranteed then, because <see cref="Normalize(short)"/> is a single step.
    /// A half-turn is genuinely ambiguous and the normalize step is not symmetric about it: it comes
    /// out as -1440 measuring forward and +1440 measuring back.
    /// </remarks>
    public short Delta(Angle other) => Normalize(unchecked((short)(other.Units - Units)));

    /// <summary>Test / authoring aid: builds an angle from degrees.  Not used by simulation code.</summary>
    /// <remarks>
    /// Floating point is deliberately confined to this pair of helpers — the integer spine never touches
    /// <see cref="double"/> (keeps float on the presentation side).
    /// </remarks>
    public static Angle FromDegrees(double degrees) =>
        FromUnits((int)Math.Round(degrees * UnitsPerDegree, MidpointRounding.AwayFromZero));

    /// <summary>Test / authoring aid: this angle in degrees.  Not used by simulation code.</summary>
    public double ToDegrees() => Units / (double)UnitsPerDegree;
}
