namespace CYAC.Port.Core.Primitives;

/// <summary>
/// The fixed-point scalar helpers the original calls out to: the hand-written MASM leaves and the
/// MSC runtime routines the compiler emits for 32-bit arithmetic.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Every method here reproduces the original's exact widths, truncation direction and wrap
/// behaviour; the difftest track verified each against the original's bytes running in the project's
/// micro-emulator (registry: <c>src/CYAC.Tools.SlrUnpacker/DiffTest*.cs</c>).
/// </para>
/// <para>
/// DIVIDE FAULTS — the 8086 raises <c>#DE</c> (interrupt 0) on a zero divisor and on a quotient too
/// wide for the destination register.  The original ships an INT 0 handler, so a fault there is a
/// recoverable runtime event, not a crash; the port does NOT model that recovery.  These methods
/// throw <see cref="DivideByZeroException"/> / <see cref="OverflowException"/> instead, which makes
/// the fault loud rather than silently divergent.  If a real code path is ever found to depend on the
/// original's INT 0 behaviour, that path needs the quirk registry, not a change here.
/// </para>
/// </remarks>
public static class Fixed
{
    /// <summary>
    /// <c>(a·b) &gt;&gt; 8</c>, keeping the low 16 bits — <c>muldiv16_signed_shr8 @image@0x11980</c>
    /// (<c>IMUL DX</c> then take bits 8..23 of the 32-bit product).
    /// </summary>
    /// <remarks>
    /// The shift is arithmetic over the signed 32-bit product; only the low word is returned, so the
    /// result wraps for products beyond ±2^23.  Verified by <c>DiffTest.cs:316</c>.
    /// </remarks>
    public static short MulDiv16SignedShr8(short a, short b) => unchecked((short)(a * b >> 8));

    /// <summary>
    /// Q16.16 divide: <c>(dividend &lt;&lt; 16) / divisor</c> — <c>div32_signed_normed @image@0x1882C</c>
    /// (FAR, <c>RETF 8</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The original does NOT fault there.  The 126 bytes at <c>image@0x1882C..0x188A9</c> are a three-phase
    /// routine and this method now reproduces all three; the previous closed form was the middle phase only.
    /// </para>
    /// <list type="number">
    ///   <item><b>Sign-strip</b> (<c>image@0x18837</c> / <c>image@0x18852</c>): both operands are made
    ///     non-negative by the two's-complement <c>neg/adc/neg</c> idiom, each toggling a sign
    ///     accumulator, so the divide is unsigned and the result is re-signed at the end
    ///     (<c>image@0x1889E</c>).  That is a truncation TOWARD ZERO, like the old closed form.</item>
    ///   <item><b>Normalize</b> (<c>image@0x18869..0x18877</c>): while the divisor's high word is
    ///     non-zero, BOTH operands are arithmetically shifted right by one.  For a divisor of
    ///     magnitude 65536 or more this LOSES the shifted-out bits, so the result is not
    ///     <c>(dividend &lt;&lt; 16) / divisor</c> exactly — it is the quotient of the two normalized
    ///     values.  A divisor of <see cref="int.MinValue"/> never converges (arithmetic shift is a
    ///     fixed point at <c>0xFFFF</c> in the high word): the original HANGS, and this method throws
    ///     rather than looping.</item>
    ///   <item><b>Saturate</b> (<c>image@0x18889</c>): when the normalized dividend's high word is
    ///     &gt;= the divisor's low word (unsigned — the <c>jb</c> at <c>image@0x18891</c>), the first
    ///     <c>DIV</c> would overflow, so the routine branches away.  Its <c>mov ax,0x7fff</c> is
    ///     DEAD — the <c>jmp</c> skips <c>mov bx,ax</c> and the shared tail's <c>xor ax,ax</c>
    ///     discards it (1078) — so the arm returns the sign accumulator in
    ///     <c>DX</c> and zero in <c>AX</c>: <b>0</b> when the operands share a sign and <b>+65536</b>
    ///     when they do not.  Not a clamp, not a fault.  0 of 86,287 apply-velocity frames
    ///     reached it.</item>
    /// </list>
    /// </remarks>
    /// <param name="dividend">The value to scale and divide (the original's third and fourth stack words).</param>
    /// <param name="divisor">The divisor (the original's first and second stack words).</param>
    /// <exception cref="DivideByZeroException">
    /// <paramref name="divisor"/> is 0 — the original reaches its second <c>DIV</c> with a zero
    /// divisor and raises <c>#DE</c> (<c>image@0x18899</c>).
    /// </exception>
    /// <exception cref="OverflowException">
    /// <paramref name="divisor"/> is <see cref="int.MinValue"/>: the original's normalize loop never
    /// terminates.  Throwing is louder than hanging; no observed caller can produce it.
    /// </exception>
    public static int Div32SignedNormed(int dividend, int divisor)
    {
        uint magnitudeDividend = unchecked((uint)dividend);
        uint magnitudeDivisor = unchecked((uint)divisor);
        bool negate = false;

        if (dividend < 0)
        {
            magnitudeDividend = unchecked(0u - magnitudeDividend);
            negate = !negate;
        }

        if (divisor < 0)
        {
            magnitudeDivisor = unchecked(0u - magnitudeDivisor);
            negate = !negate;
        }

        // Phase 2 — normalize.  `sar hi,1 ; rcr lo,1` on each operand is a 32-bit ARITHMETIC shift.
        int iterations = 0;
        while ((magnitudeDivisor >> 16) != 0)
        {
            magnitudeDividend = unchecked((uint)((int)magnitudeDividend >> 1));
            magnitudeDivisor = unchecked((uint)((int)magnitudeDivisor >> 1));
            if (++iterations > 32)
            {
                throw new OverflowException(
                    "div32_signed_normed (image@0x18869): the normalize loop cannot converge for a "
                        + "divisor of int.MinValue — the original hangs here.");
            }
        }

        ushort divisorLow = unchecked((ushort)magnitudeDivisor);
        uint result;

        if ((magnitudeDividend >> 16) < divisorLow)
        {
            if (divisorLow == 0)
            {
                throw new DivideByZeroException(
                    "div32_signed_normed (image@0x1882C) divides by zero here; the original raises #DE.");
            }

            uint quotient = magnitudeDividend / divisorLow;
            uint remainder = magnitudeDividend % divisorLow;
            result = (quotient << 16) | ((remainder << 16) / divisorLow);
        }
        else
        {
            if (divisorLow == 0)
            {
                throw new DivideByZeroException(
                    "div32_signed_normed (image@0x18899) divides by zero at its second DIV; the "
                        + "original raises #DE.");
            }

            // The saturate arm: DX = the sign accumulator, AX = 0 (the 0x7FFF is dead).
            result = negate ? 0xFFFF0000u : 0u;
        }

        if (negate)
        {
            result = unchecked(0u - result);
        }

        return unchecked((int)result);
    }

    /// <summary>
    /// Signed 32-bit divide — <c>muldiv32_signed @image@0x00770</c> (FAR, <c>RETF 8</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// MISNOMER, kept for traceability: the scanner name says "muldiv" but the routine is the MSC
    /// runtime's <c>_ldiv</c> — a plain signed 32-bit division, no multiply
    /// (<c>DiffTest.Batch3.cs:11</c>).
    /// </para>
    /// <para>
    /// OPEN: the difftest characterised it to ±1 rather than exactly.  Its general path (divisors of
    /// magnitude 0x10000 or more) applies a single-decrement correction whose flag conditions the
    /// decode only approximates, so for large divisors this implementation's plain truncating divide
    /// may differ from the original by one.  For divisors that fit 16 bits — which is every observed
    /// caller — it is exact.
    /// </para>
    /// </remarks>
    /// <exception cref="DivideByZeroException"><paramref name="divisor"/> is 0 — the original's <c>#DE</c>.</exception>
    /// <exception cref="OverflowException"><c>int.MinValue / -1</c>, which has no 32-bit result.</exception>
    public static int MulDiv32Signed(int dividend, int divisor)
    {
        if (divisor == 0)
        {
            throw new DivideByZeroException(
                "muldiv32_signed / _ldiv (image@0x00770) divides by zero here; the original raises #DE.");
        }

        if (dividend == int.MinValue && divisor == -1)
        {
            throw new OverflowException(
                "muldiv32_signed: int.MinValue / -1 has no signed 32-bit result.");
        }

        return dividend / divisor;
    }

    /// <summary>
    /// <c>(a·b) &gt;&gt; 16</c> as the original computes it — <c>mul32_shr16 @image@0x187CA</c>
    /// (FAR, <c>RETF 8</c>).
    /// </summary>
    /// <remarks>
    /// Sign-magnitude: the routine strips both signs, multiplies the magnitudes, keeps
    /// <c>(|a|·|b|) &gt;&gt; 16</c> truncated mod 2^32, then re-applies the sign.  That is a
    /// truncation TOWARD ZERO, not an arithmetic shift — <c>Mul32Shr16(1, -1)</c> is 0, where an
    /// arithmetic <c>&gt;&gt; 16</c> of the 64-bit product would give −1.  Verified EXACTLY,
    /// including the 32-bit wrap and <c>int.MinValue</c> edges, by <c>DiffTest.Batch5.cs:490</c>.
    /// </remarks>
    public static int Mul32Shr16(int a, int b) => unchecked((int)((long)a * b / 65536));

    /// <summary>
    /// <c>(a·b) / divisor</c> in 16-bit registers — <c>muldiv16_signed @image@0x11974</c>
    /// (<c>IMUL DX</c> then <c>IDIV BX</c>).  Returns the quotient; see
    /// <see cref="MulDiv16SignedWithRemainder"/> for both halves.
    /// </summary>
    public static short MulDiv16Signed(short a, short b, short divisor) =>
        MulDiv16SignedWithRemainder(a, b, divisor).Quotient;

    /// <summary>
    /// <c>(a·b) / divisor</c> and its remainder — <c>muldiv16_signed @image@0x11974</c>, which leaves
    /// the quotient in <c>AX</c> and the remainder in <c>DX</c>.
    /// </summary>
    /// <remarks>
    /// The product is a full 32-bit <c>IMUL</c>; the divide is a signed <c>IDIV</c>, so the quotient
    /// truncates toward zero and the remainder takes the DIVIDEND's sign.  Verified by
    /// <c>DiffTest.cs:283</c>.
    /// </remarks>
    /// <exception cref="DivideByZeroException"><paramref name="divisor"/> is 0 — the original's <c>#DE</c>.</exception>
    /// <exception cref="OverflowException">
    /// The quotient does not fit a signed 16-bit word — also the original's <c>#DE</c>.  Callers stay
    /// inside this domain (e.g. <c>fme_linear_interp_x_from_y @image@0x2A74A</c> is only exercised
    /// with in-range operands).
    /// </exception>
    public static (short Quotient, short Remainder) MulDiv16SignedWithRemainder(
        short a,
        short b,
        short divisor) => IDiv16(a * b, divisor);

    /// <summary>
    /// Full signed 16×16 → 32 product — <c>imul16_signed @image@0x1196C</c> (<c>DX:AX = AX · DX</c>).
    /// </summary>
    /// <remarks>No truncation: both halves of the product are kept.  Verified by <c>DiffTest.cs:458</c>.</remarks>
    public static int IMul16(short a, short b) => a * b;

    /// <summary>
    /// Signed 32 ÷ 16 divide — <c>idiv16_signed @image@0x1195E</c> (<c>DX:AX / BX</c>, quotient in
    /// <c>AX</c>, remainder in <c>DX</c>).
    /// </summary>
    /// <remarks>
    /// Truncates toward zero; the remainder takes the dividend's sign.  Verified by
    /// <c>DiffTest.cs:478</c> over the domain where the quotient fits a signed 16-bit word.
    /// </remarks>
    /// <exception cref="DivideByZeroException"><paramref name="divisor"/> is 0 — the original's <c>#DE</c>.</exception>
    /// <exception cref="OverflowException">The quotient does not fit a signed 16-bit word — also <c>#DE</c>.</exception>
    public static (short Quotient, short Remainder) IDiv16(int dividend, short divisor)
    {
        if (divisor == 0)
        {
            throw new DivideByZeroException(
                "idiv16_signed (image@0x1195E) divides by zero here; the original raises #DE.");
        }

        int quotient = dividend / divisor;
        if (quotient is > short.MaxValue or < short.MinValue)
        {
            throw new OverflowException(
                $"idiv16_signed: {dividend} / {divisor} = {quotient} does not fit a signed 16-bit "
                    + "word; the original raises #DE (divide overflow).");
        }

        return ((short)quotient, (short)(dividend % divisor));
    }
}
