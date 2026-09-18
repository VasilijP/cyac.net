using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// A 3×3 Q14 rotation matrix, row-major, exactly as the original lays it out — nine 16-bit words.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Built by <c>gfx_rot_mat3_from_euler @image@0x14D9C</c> and consumed by
/// <c>gfx_vec3_mat3_multiply_inner @image@0x1BF0E</c> and
/// <c>mat3_mat3_multiply_to_dest @image@0x1BD6A</c>.
/// </para>
/// <para>
/// The words are kept as raw <c>ushort</c>s (not <c>short</c>s) because that is what the DGROUP
/// scratch <c>mat3_vec3_scratch [0x0CD8]</c> holds and what the K0 trace verifies against; the
/// arithmetic re-reads them as signed.
/// </para>
/// </remarks>
public sealed class Mat3Q14
{
    /// <summary>Elements in the matrix: 9.</summary>
    public const int Elements = 9;

    private readonly ushort[] _words = new ushort[Elements];

    /// <summary>One element, row-major (<c>[3·row + column]</c>) — the original's word index.</summary>
    /// <param name="index">0..8.</param>
    public ushort this[int index]
    {
        get => _words[index];
        set => _words[index] = value;
    }

    /// <summary>The nine words, in the order the original stores them.</summary>
    public ReadOnlySpan<ushort> Words => _words;

    /// <summary>The element as the signed value the multiplies read.</summary>
    /// <param name="index">0..8.</param>
    public short Signed(int index) => unchecked((short)_words[index]);
}

/// <summary>
/// <c>aircraft_project_to_camera @image@0x2BAD8</c> — rotates the aircraft's three body-axis velocity
/// accumulators into world axes, and the two matrix leaves it is built on.
/// </summary>
/// <remarks>
/// <para>
/// INT-only, and INT-class by the port's own ruling: <b>despite the name, nothing here is
/// render-only.</b> Its three outputs land in the world object's position
/// (<c>image@0x2BFED..0x2C019</c>), in <c>g_vertical_speed_i32 [0xF1C0]</c> (which
/// <c>crash_conditions_valid</c> reads) and in the phase-10/12 altitude clamps — i.e. every one of
/// them is simulation state.  <b>Nothing in this subtree is a candidate for the FLOAT half.</b> Its
/// only genuinely render-shaped by-product is the DGROUP scratch <c>mat3_vec3_scratch
/// [0x0CD8..0x0CEB]</c>, which nothing reads across a frame boundary and which the port returns
/// purely so a verification can compare it.
/// </para>
/// <para>
/// Source of truth: the bytes at <c>image@0x2BAD8..0x2BB97</c>, <c>image@0x14D9C..0x14E86</c>,
/// <c>image@0x1BD6A..0x1BEFD</c> and <c>image@0x1BF0E..0x1BFA3</c>.
/// </para>
/// </remarks>
public static class BodyVelocityProjection
{
    /// <summary>
    /// <c>gfx_rot_mat3_from_euler @image@0x14D9C</c> — <c>R_z · (R_y · R_x)</c>, Q14.
    /// </summary>
    /// <param name="rollAngle">The <c>[SP+4]</c> argument (<c>angle_x</c>), the R_x rotation.</param>
    /// <param name="pitchAngle">The <c>[SP+6]</c> argument (<c>angle_y</c>), the R_y rotation.</param>
    /// <param name="headingAngle">The <c>[SP+8]</c> argument (<c>angle_z</c>), the R_z rotation.</param>
    /// <returns>The product matrix — also the value left in <c>mat3_vec3_scratch [0x0CD8]</c>.</returns>
    /// <remarks>
    /// The trig calls go through the <c>angle_sin_x4</c>/<c>angle_cos_x4</c> shells, but each result
    /// is immediately divided by four again (<c>sar dx,1 ; rcr ax,1</c> twice), so what is STORED is
    /// the raw quarter-table word — hence <see cref="TrigTables.NavSin"/>/<see cref="TrigTables.NavCos"/>
    /// here rather than the ×4 variants (<c>RotMat3Lift</c>'s "the ×4 and the ÷4 cancel").
    /// </remarks>
    public static Mat3Q14 RotationFromEuler(Angle rollAngle, Angle pitchAngle, Angle headingAngle)
    {
        // R_x, built directly into the output block (image@0x14DAF..0x14DE1).
        Mat3Q14 product = new Mat3Q14();
        ushort sinX = unchecked((ushort)TrigTables.NavSin(rollAngle));
        ushort cosX = unchecked((ushort)TrigTables.NavCos(rollAngle));
        product[0] = sinX;
        product[1] = cosX;
        product[2] = 0;
        product[3] = unchecked((ushort)-cosX);
        product[4] = sinX;
        product[5] = 0;
        product[6] = 0;
        product[7] = 0;
        product[8] = 0x4000;

        // R_y in the stack temp, then mul#1 (image@0x14DE4..0x14E36).
        Mat3Q14 temp = new Mat3Q14();
        ushort sinY = unchecked((ushort)TrigTables.NavSin(pitchAngle));
        ushort cosY = unchecked((ushort)TrigTables.NavCos(pitchAngle));
        temp[0] = 0x4000;
        temp[1] = 0;
        temp[2] = 0;
        temp[3] = 0;
        temp[4] = sinY;
        temp[5] = cosY;
        temp[6] = 0;
        temp[7] = unchecked((ushort)-cosY);
        temp[8] = sinY;
        product = Multiply(temp, product);

        // R_z, then mul#2 — whose result is what stays in the DGROUP scratch.
        ushort sinZ = unchecked((ushort)TrigTables.NavSin(headingAngle));
        ushort cosZ = unchecked((ushort)TrigTables.NavCos(headingAngle));
        temp[0] = sinZ;
        temp[1] = 0;
        temp[2] = unchecked((ushort)-cosZ);
        temp[3] = 0;
        temp[4] = 0x4000;
        temp[5] = 0;
        temp[6] = cosZ;
        temp[7] = 0;
        temp[8] = sinZ;
        return Multiply(temp, product);
    }

    /// <summary>
    /// <c>mat3_mat3_multiply_to_dest @image@0x1BD6A</c> — <c>left × right</c> in Q14.
    /// </summary>
    /// <param name="left">The <c>src</c> argument (<c>[SP+4]</c>, held in <c>DI</c>).</param>
    /// <param name="right">The <c>dst</c> argument (<c>[SP+6]</c>, held in <c>SI</c>; overwritten in place).</param>
    /// <returns>The product.</returns>
    /// <remarks>
    /// Nine identical 17-instruction blocks.  Each element is a three-term <c>IMUL</c> dot product
    /// accumulated with <c>ADD</c>/<c>ADC</c> in 32 bits, shifted left by two
    /// (<c>shl ax,1 ; rcl dx,1</c> twice) and reduced to its HIGH word — i.e.
    /// <c>(Q14·Q14) &gt;&gt; 14</c>.  Every step wraps mod 2^32, which is why the accumulation is
    /// <c>unchecked</c> and not a <c>long</c>.
    /// </remarks>
    public static Mat3Q14 Multiply(Mat3Q14 left, Mat3Q14 right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        Mat3Q14 result = new Mat3Q14();
        for (int k = 0; k < Mat3Q14.Elements; k++)
        {
            int column = k % 3;
            int row = k / 3;
            int sum = 0;
            for (int j = 0; j < 3; j++)
            {
                sum = unchecked(sum + (right.Signed((3 * j) + column) * left.Signed((3 * row) + j)));
            }

            result[k] = unchecked((ushort)((uint)sum << 2 >> 16));
        }

        return result;
    }

    /// <summary>
    /// <c>gfx_vec3_mat3_multiply_inner @image@0x1BF0E</c> — one vector through the matrix, three
    /// full 32-bit Q30 rows.
    /// </summary>
    /// <param name="matrix">The <c>BX</c> argument.</param>
    /// <param name="x">The vector's X word (the original reads it from <c>[di+2]</c>).</param>
    /// <param name="y">Its Y word (<c>[di+6]</c>, loaded into <c>ES</c> as a scratch GPR).</param>
    /// <param name="z">Its Z word (<c>[di+0xA]</c>, cached to <c>[0x0CEA]</c>).</param>
    /// <returns>The three rows, each a full Q30 <c>i32</c>.</returns>
    public static (int Row0, int Row1, int Row2) Transform(Mat3Q14 matrix, short x, short y, short z)
    {
        ArgumentNullException.ThrowIfNull(matrix);

        int Row(int r)
        {
            int sum = matrix.Signed((3 * r) + 0) * x;
            sum = unchecked(sum + (y * matrix.Signed((3 * r) + 1)));
            sum = unchecked(sum + (matrix.Signed((3 * r) + 2) * z));
            return unchecked((int)((uint)sum << 2));
        }

        return (Row(0), Row(1), Row(2));
    }

    /// <summary>The result of one <see cref="Run"/>.</summary>
    /// <param name="WorldX">
    /// <c>output[+4]</c> — the "X" slot, <c>(row0·v) &gt;&gt; 8</c>.  Phase 9b ADDS its scaled value
    /// to the world object's <c>pos_x</c> (<c>image@0x2BFF0</c>).
    /// </param>
    /// <param name="WorldY">
    /// <c>output[+8]</c> — the "Y" slot, <c>−((row1·v) &gt;&gt; 8)</c>, negated AFTER the shift.
    /// Phase 9b SUBTRACTS its scaled value from <c>pos_y</c> (<c>image@0x2C003</c>), and phase 11
    /// negates the raw value into <c>g_vertical_speed_i32</c>.
    /// </param>
    /// <param name="WorldZ">
    /// <c>output[+0]</c> — the "Z" slot, <c>(row2·v) &gt;&gt; 8</c>.  Phase 9b adds it to
    /// <c>pos_z</c> (<c>image@0x2C016</c>).
    /// </param>
    /// <param name="Matrix">
    /// The rotation matrix, which is also the nine words left in
    /// <c>mat3_vec3_scratch [0x0CD8..0x0CE9]</c>.
    /// </param>
    /// <param name="VzScratch">
    /// The word left in <c>[0x0CEA]</c> — <c>gfx_vec3_mat3_multiply_inner</c>'s cached Z component.
    /// </param>
    public readonly record struct Projection(
        int WorldX, int WorldY, int WorldZ, Mat3Q14 Matrix, ushort VzScratch);

    /// <summary>Runs the whole projection.</summary>
    /// <param name="aircraft">The aircraft — reads the three velocity blocks' CURRENT slots.</param>
    /// <param name="rollAngle">The <c>[bp+8]</c> argument, from master <c>+0x60</c>.</param>
    /// <param name="pitchAngle">The <c>[bp+6]</c> argument, from master <c>+0x70</c>.</param>
    /// <param name="headingAngle">The <c>[bp+4]</c> argument, from <c>0x10 − master[+0x80]</c>.</param>
    /// <remarks>
    /// The row-to-slot routing is a genuine permutation the bytes force and the naive reading gets
    /// wrong.  The extractor stages the three blocks into the vector as <c>slot0 = +0x10</c>, <c>slot1
    /// = −(+0x20)</c>, <c>slot2 = +0x00</c> (<c>image@0x2BAE6..0x2BB25</c>); the multiply overwrites
    /// slot <i>k</i> with <c>row_k · v</c>; and the extraction step then re-reads the slots by their
    /// ORIGINAL frame offsets, so <c>row2</c> lands in <c>output[+0]</c>, <c>row0</c> in
    /// <c>output[+4]</c> and <c>row1</c> in <c>output[+8]</c> (<c>image@0x2BB4A..0x2BB8F</c>).
    /// </remarks>
    public static Projection Run(Aircraft aircraft, Angle rollAngle, Angle pitchAngle, Angle headingAngle)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        // STEP B — the byte-shuffle extraction: `<< 8` with the top byte dropped, and the third
        // block additionally 32-bit negated (image@0x2BB1B).  Only each result's HIGH word is read
        // back by the multiply, so the vector components are (block << 8) >> 16 as i16.
        int slot0 = EulerRateTransform.ShiftLeft8(MasterBlocks.Current(aircraft, MasterBlock.AngularVelocityA));
        int slot1 = unchecked(-EulerRateTransform.ShiftLeft8(MasterBlocks.Current(aircraft, MasterBlock.AngularVelocityB)));
        int slot2 = EulerRateTransform.ShiftLeft8(MasterBlocks.Current(aircraft, MasterBlock.ForwardVelocity));

        short x = unchecked((short)(slot0 >> 16));
        short y = unchecked((short)(slot1 >> 16));
        short z = unchecked((short)(slot2 >> 16));

        Mat3Q14 matrix = RotationFromEuler(rollAngle, pitchAngle, headingAngle);
        (int row0, int row1, int row2) = Transform(matrix, x, y, z);

        return new Projection(
            WorldX: row0 >> 8,
            WorldY: unchecked(-(row1 >> 8)),
            WorldZ: row2 >> 8,
            Matrix: matrix,
            VzScratch: unchecked((ushort)z));
    }
}
