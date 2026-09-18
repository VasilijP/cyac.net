using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Flight;

namespace CYAC.Port.Core.Sim.Geometry;

/// <summary>
/// <c>angle_3d_orientation_combine @image@0x1C122</c> — "point an already-oriented body at an offset
/// direction, and tell me the resulting heading, elevation and bank".
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Given an orientation (heading, elevation, bank) and two SIGNED delta angles — a YAW
/// and a PITCH applied in the body frame — it rotates the body's forward and right unit vectors by
/// the deltas, then by the orientation, and reads the answer back out with three arctangents.  It
/// exists because 3D rotations do not commute: adding the deltas to the heading and elevation
/// directly (which is exactly what the routine's own cheap fourth case does, see
/// <c>TargetSelectionCluster.WeaponAimAngles</c>) diverges as soon as the body is pitched or rolled.
/// </para>
/// <para>
/// <b>Four doors, three of them cameras</b> (at <c>0x1C122</c>; census by <c>LCALL 0x201D:0xBF52</c>
/// over the original's unpacked image — exactly four sites):
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>target_angle_wrap_and_combine @image@0x07F3E</c> — the COMBAT door.  Passes the shooter pool
/// object's <c>+0x12</c> / <c>+0x14</c> / <c>+0x16</c> as the orientation and the weapon aim
/// record's two bytes, each ZERO-extended and scaled by 16, as <paramref name="deltaYaw"/> and
/// <paramref name="deltaPitch"/> (<c>image@0x07F1D..0x07F2E</c>: <c>mov al,[bx+1] / sub ah,ah /
/// shl ax,cl</c>).  It keeps the heading and elevation and DISCARDS the bank — the third
/// out-pointer is its private <c>[bp-2]</c> (<c>image@0x07F11</c>), never read again
/// (<c>image@0x07F3E..0x07F43</c>).
/// </description></item>
/// <item><description>
/// <c>mission_state_machine @image@0x015BD</c>, <c>view_frame_update_dispatch @image@0x23A05</c>
/// and <c>ui_film_review_screen @image@0x32ABF</c> — the three CAMERA doors.  The first and third
/// pass the view-anchor angle block <c>[0xD89A]</c> / <c>[0xD89C]</c> / <c>[0xD89E]</c> with
/// <c>g_film_freecam_azimuth [0xE474]</c> and <c>g_film_freecam_elevation [0xE476]</c> as the two
/// deltas, UNSCALED (<c>image@0x015A9..0x015BD</c>).  That pins the argument roles beyond doubt:
/// "arg4 = YAW, arg5 = PITCH", and the in-dispatch door at <c>image@0x23A05</c> confirms it by
/// pushing the literal <c>0x2D0</c> (+90°) as the yaw for view-mode 02 LEFT
/// (<c>image@0x239F1</c>).  The renderer needs THIS routine plus the
/// <c>[0xD89A..0xD89E]</c> anchor block; nothing else.
/// </description></item>
/// </list>
/// <para>
/// Source of truth: the bytes at <c>image@0x1C122..0x1C27B</c> (346 B + a <c>NOP</c> pad,
/// disassembled with), with read as prose only — three of its thirteen
/// steps misstate the bytes.  The callees are all already ported:
/// <see cref="BodyVelocityProjection.RotationFromEuler"/> = <c>gfx_rot_mat3_from_euler
/// @image@0x14D9C</c>, <see cref="BodyVelocityProjection.Transform"/> =
/// <c>gfx_vec3_mat3_multiply_inner @image@0x1BF0E</c> (the <c>image@0x1BEFE</c> trampoline this
/// routine actually calls is nine bytes of register marshalling in front of it and adds nothing),
/// <see cref="Mat3PairBuild"/> = <c>angle_mat3_pair_build @image@0x1BFA4</c> and
/// <see cref="Atan2Table.Atan2Bam"/> = <c>atan2_bam @image@0x15CF4</c>.
/// </para>
/// </remarks>
public static class OrientationCombine
{
    /// <summary>
    /// The unit vectors' magnitude: <c>1000</c> = <c>0x3E8</c>, planted in the HIGH word of each
    /// component's <c>i32</c> (<c>image@0x1C16C</c> / <c>0x1C172</c> / <c>0x1C178</c>).
    /// </summary>
    /// <remarks>
    /// Both stack blocks are three <c>i32</c>, and <c>gfx_vec3_mat3_multiply_inner</c> reads only
    /// the HIGH word of each (<c>[di+2]</c>, <c>[di+6]</c>, <c>[di+0xA]</c>,
    /// <c>image@0x1BF15..0x1BF1B</c>).  So the effective vectors are the FORWARD <c>(0, 0,
    /// 1000)</c> — high word set at <c>[bp-2]</c>, the third component (<c>image@0x1C172</c>) —
    /// and the RIGHT <c>(1000, 0, 0)</c> — high word set at <c>[bp-0x16]</c>, which is
    /// <c>[bp-0x18] + 2</c>, i.e. the FIRST component (<c>image@0x1C178</c>).  the byte offsets
    /// say X.
    /// </remarks>
    public const short UnitMagnitude = 1000;

    /// <summary>The three angles the routine writes through its three near pointers.</summary>
    /// <param name="Heading">The <c>[bp+0x10]</c> out-word (<c>image@0x1C1F3</c>).</param>
    /// <param name="Elevation">The <c>[bp+0x12]</c> out-word (<c>image@0x1C230</c>).</param>
    /// <param name="Bank">
    /// The <c>[bp+0x14]</c> out-word (<c>image@0x1C273</c>).  The combat door discards it; the
    /// camera doors keep it.
    /// </param>
    public readonly record struct Result(short Heading, short Elevation, short Bank);

    /// <summary>Runs the whole routine.</summary>
    /// <param name="heading">The original's <c>[bp+6]</c>.</param>
    /// <param name="elevation">Its <c>[bp+8]</c>.</param>
    /// <param name="bank">Its <c>[bp+0xA]</c>.</param>
    /// <param name="deltaYaw">
    /// Its <c>[bp+0xC]</c> — the YAW offset, signed.  The combat door's <c>aim[0] &lt;&lt; 4</c>,
    /// the camera doors' <c>[0xE474]</c>.
    /// </param>
    /// <param name="deltaPitch">
    /// Its <c>[bp+0xE]</c> — the PITCH offset, signed.  The combat door's <c>aim[1] &lt;&lt; 4</c>,
    /// the camera doors' <c>[0xE476]</c>.
    /// </param>
    /// <returns>The three out-words.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An angle whose table index leaves <c>[0, 0xB40]</c> — see
    /// <see cref="Mat3PairBuild.NegateAndWrap"/> for the same refusal on the sibling routine.  The
    /// original would index past its trig table; the port refuses rather than invent a value.
    /// </exception>
    public static Result Combine(
        short heading, short elevation, short bank, short deltaYaw, short deltaPitch)
    {
        // ---- the FAST PATH.  image@0x1C130..0x1C14D: both deltas zero ⇒ the three inputs are
        // copied straight through, with NO wrap and no matrix work.  Note it copies the BANK too
        // (through the shared tail at image@0x1C270), so a non-canonical input passes through.
        if (deltaYaw == 0 && deltaPitch == 0)
        {
            return new Result(heading, elevation, bank);
        }

        // ---- delta normalisation.  image@0x1C14E..0x1C15D: ONE conditional +0xB40 per delta, on
        // the SIGNED value — the same single-shot idiom as the sibling, and just as unforgiving
        // outside [-0xB40, 0xB40].
        short yaw = Normalize(deltaYaw);
        short pitch = Normalize(deltaPitch);

        // ---- phase 1.  image@0x1C187..0x1C194: gfx_rot_mat3_from_euler(&M, yaw, pitch, 0) — the
        // pushes are &M, DI=yaw, SI=pitch, 0, and the callee reads angle_x from [bp+6] (the LAST
        // push), so angle_x = 0, angle_y = pitch, angle_z = yaw.
        Mat3Q14 deltaMatrix = BodyVelocityProjection.RotationFromEuler(
            TableAngle(0, nameof(deltaYaw)),
            TableAngle(pitch, nameof(deltaPitch)),
            TableAngle(yaw, nameof(deltaYaw)));

        // ---- phase 2.  image@0x1C195..0x1C1AE: both unit vectors through it, in place.
        (int Row0, int Row1, int Row2) forward = BodyVelocityProjection.Transform(deltaMatrix, 0, 0, UnitMagnitude);
        (int Row0, int Row1, int Row2) right = BodyVelocityProjection.Transform(deltaMatrix, UnitMagnitude, 0, 0);

        // ---- phase 3.  image@0x1C1AF..0x1C1C0: the ORIENTATION matrix.  Pushes are &M, [bp+6]
        // heading, [bp+8] elevation, [bp+0xA] bank; the last push lands at the callee's angle_x, so
        // angle_x = BANK, angle_y = elevation, angle_z = HEADING. This is difftest FINDING F1:…c
        // step 5 had heading and bank positionally inverted (a heading/bank-swapped reference
        // diverges on 16,592 of 20,000 inputs).
        Mat3Q14 orientation = BodyVelocityProjection.RotationFromEuler(
            TableAngle(bank, nameof(bank)),
            TableAngle(elevation, nameof(elevation)),
            TableAngle(heading, nameof(heading)));

        // image@0x1C1C1..0x1C1DA — the SECOND multiply re-reads the first's HIGH words in place.
        forward = BodyVelocityProjection.Transform(orientation, Hi(forward.Row0), Hi(forward.Row1), Hi(forward.Row2));
        right = BodyVelocityProjection.Transform(orientation, Hi(right.Row0), Hi(right.Row1), Hi(right.Row2));

        // ---- phase 4, the HEADING.  image@0x1C1DB..0x1C1F3.
        //   AX (adjacent) = forward.Row2's high word — image@0x1C1DB, `mov ax,[bp-2]`, NOT negated;
        //   DX (opposite) = the high word of the 32-BIT negation of forward.Row0 — image@0x1C1E4,
        //     `neg cx / adc dx,0 / neg dx`, which is -(hi + (lo != 0)) and so differs from a plain
        //     16-bit `neg` of the high word whenever the low word is non-zero.
        short outHeading = Bam(Atan2Table.Atan2Bam(Hi(forward.Row2), Hi(unchecked(-forward.Row0))));

        // ---- phase 5, the ELEVATION.  image@0x1C1F5..0x1C230.
        //   angle_mat3_pair_build(bank = 0, elevation = 0, heading = outHeading) — pushes are
        //   &out_b, &out_a, heading, 0, 0 and the callee (RET 0xA, so left-to-right) reads
        //   [bp+8] = heading, [bp+6] = elevation, [bp+4] = bank.  The caller then multiplies by
        //   out_b (the matrix at [bp-0x42]); out_a at [bp-0x54] is written and never read.
        //   The forward vector is COPIED first (rep movsw cx=6, image@0x1C206..0x1C214) so the
        //   un-rotated one survives for phase 6 — but only its high words matter downstream.
        Mat3Q14 headingMatrix = Mat3PairBuild.Build(0, 0, outHeading).Full;
        (int Row0, int Row1, int Row2) forwardLevel = BodyVelocityProjection.Transform(
            headingMatrix, Hi(forward.Row0), Hi(forward.Row1), Hi(forward.Row2));

        //   image@0x1C222/0x1C225 — AX = the copy's Z high word ([bp-0x1A]), DX = its Y high word
        //   ([bp-0x1E]).  NEITHER is negated here.
        short outElevation = Bam(Atan2Table.Atan2Bam(Hi(forwardLevel.Row2), Hi(forwardLevel.Row1)));

        // ---- phase 6, the BANK.  image@0x1C232..0x1C273.
        //   angle_mat3_pair_build(bank = 0, elevation = outElevation, heading = outHeading), then
        //   the RIGHT vector's copy through out_b.
        Mat3Q14 headingElevationMatrix = Mat3PairBuild.Build(0, outElevation, outHeading).Full;
        (int Row0, int Row1, int Row2) rightLevel = BodyVelocityProjection.Transform(
            headingElevationMatrix, Hi(right.Row0), Hi(right.Row1), Hi(right.Row2));

        //   image@0x1C263..0x1C26B — AX = the copy's X high word ([bp-0x2E]), DX = its Y high word
        //   ([bp-0x2A]) negated by a plain 16-BIT `neg dx`.  Contrast phase 4, which negates the
        //   whole i32 before taking the high word: the two negations are genuinely different
        //   instructions and the port keeps both.
        short outBank = Bam(Atan2Table.Atan2Bam(
            Hi(rightLevel.Row0), unchecked((short)-Hi(rightLevel.Row1))));

        return new Result(outHeading, outElevation, outBank);
    }

    /// <summary>The high word of a <c>i32</c> row — what the next multiply re-reads from the buffer.</summary>
    /// <param name="row">The row.</param>
    /// <returns>Its high word, signed.</returns>
    private static short Hi(int row) => unchecked((short)(row >> 16));

    /// <summary>The <c>AX</c> the arctangent leaves, as the out-word the original stores.</summary>
    /// <param name="angle">The arctangent's answer.</param>
    /// <returns>The stored word.  Always canonical — the four arms of
    /// <c>image@0x15CF4</c> cover <c>[0, 0xB3F]</c>.</returns>
    private static short Bam(Angle angle) => unchecked((short)angle.Units);

    /// <summary>
    /// The single conditional <c>+0xB40</c> the routine applies to each delta
    /// (<c>image@0x1C14E..0x1C15D</c>: <c>or di,di / jge / add di,0xB40</c>).
    /// </summary>
    /// <param name="delta">The raw delta word.</param>
    /// <returns>The normalised delta, still a raw 16-bit word.</returns>
    private static short Normalize(short delta) =>
        delta < 0 ? unchecked((short)(delta + Angle.FullCircle)) : delta;

    /// <summary>
    /// Turns a raw angle word into the <see cref="Angle"/> the trig tables accept, refusing the
    /// values on which the original reads past its table.
    /// </summary>
    /// <remarks>
    /// Identical policy and identical proof to <see cref="Mat3PairBuild.NegateAndWrap"/>, minus the
    /// negation: <c>gfx_rot_mat3_from_euler @image@0x14D9C</c> passes its three arguments straight
    /// to <c>angle_cos_x4 @image@0x183F0</c> / <c>angle_sin_x4 @image@0x18400</c>, neither of which
    /// wraps (<c>image@0x183F0</c> is a bare <c>LCALL</c> + <c>CDQ</c> + two shifts).  Index
    /// <c>0xB40</c> is folded to 0, which the table arms prove is an identity.
    /// </remarks>
    /// <param name="units">The raw word.</param>
    /// <param name="name">The parameter name, for the diagnostic.</param>
    /// <returns>The angle.</returns>
    private static Angle TableAngle(short units, string name)
    {
        int index = units;
        if (index == Angle.FullCircle)
        {
            index = 0;
        }

        if ((uint)index >= Angle.FullCircle)
        {
            throw new ArgumentOutOfRangeException(
                name,
                units,
                $"angle_3d_orientation_combine (image@0x1C122) wraps its DELTAS once and its three "
                    + $"orientation angles not at all, so only [0, 0x{Angle.FullCircle:X}] reaches "
                    + $"the trig tables; 0x{(ushort)units:X4} would index past the 721-entry table "
                    + "in far segment 0x4438 (image@0x1834D onwards).");
        }

        return new Angle((ushort)index);
    }
}
