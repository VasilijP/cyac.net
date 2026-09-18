using System.Buffers.Binary;

namespace CYAC.Formats.Cockpit;

/// <summary>
/// Re-emits a cockpit compositor pack from its parameters — the cockpit bitmap plus the element
/// count.
/// </summary>
/// <remarks>
/// <para>
/// Source of truth: and its Python reference (<c>emit_vga</c> / <c>emit_mcga</c>), which regenerate
/// all six VGA and all six MCGA shipping packs byte-for-byte.  This is the C# port of that generator,
/// and it is what makes the pack family's law-L3 round trip real: the data tree stores the cockpit
/// art, and the inverse produces the original machine code again.
/// </para>
/// <para>
/// The generative rule: for each opaque run <c>[a…c]</c> on a row the aligned core <c>[(a+3)&amp;~3 …
/// ((c+1)&amp;~3)-1]</c> streams from the data region — the same bytes at the same offset in every
/// Mode-X plane, which is why the four plane passes' op streams are byte-identical — and the ≤3+≤3
/// unaligned edge pixels become <c>mov byte es:[abs], imm8</c> literals, left before right, after the
/// run's core copy.
/// </para>
/// </remarks>
public static class CockpitPackGenerator
{
    /// <summary>The VGA pack's paint entry point, constant across the six shipping packs.</summary>
    public const int VgaPaintEntry = 0x3E;

    /// <summary>The VGA pack's init entry point, constant across the six shipping packs.</summary>
    public const int VgaInitEntry = 0x0C;

    /// <summary>Emits a pack for a mode this generator models.</summary>
    /// <param name="mode">VGA or MCGA.</param>
    /// <param name="runs">The cockpit bitmap as opaque runs, row-major.</param>
    /// <param name="elementCount">How many entries the (inert) element list carries.</param>
    /// <exception cref="InvalidDataException">The mode has no generator in this build.</exception>
    public static byte[] Emit(CockpitPackMode mode, IReadOnlyList<CockpitPaintRun> runs, int elementCount) =>
        mode switch
        {
            CockpitPackMode.Vga => EmitVga(runs, elementCount),
            CockpitPackMode.Mcga => EmitMcga(runs, elementCount),
            _ => throw new InvalidDataException(
                $"no generator for {mode} packs — only the VGA layout has one; EGA, CGA and TANDY are " +
                "dormant modes carried as code"),
        };

    /// <summary>Emits a Mode-X (VGA) pack: four plane passes over the same op stream.</summary>
    /// <param name="runs">The cockpit bitmap as opaque runs, row-major.</param>
    /// <param name="elementCount">How many entries the element list carries.</param>
    public static byte[] EmitVga(IReadOnlyList<CockpitPaintRun> runs, int elementCount)
    {
        ArgumentNullException.ThrowIfNull(runs);
        List<byte> code = new List<byte>();
        List<byte> data = new List<byte>();

        code.AddRange(Bytes(0x55, 0x8B, 0xEC, 0x53, 0x51, 0x52, 0x56, 0x57, 0x1E));  // frame + saves
        code.AddRange(Bytes(0x2E, 0xC5, 0x36, 0x08, 0x00));                          // lds si, cs:[8]
        code.AddRange(Bytes(0x8B, 0x46, 0x06, 0x8E, 0xC0));                          // es = [bp+6]
        code.AddRange(Bytes(0x33, 0xFF));                                            // xor di, di
        code.AddRange(Bytes(0xBA, 0xCE, 0x03, 0xB8, 0x05, 0x40, 0xEF));              // GC[5] = 0x40

        for (int plane = 0; plane < 4; plane++)
        {
            code.AddRange(Bytes(0xBA, 0xC4, 0x03, 0xB8, 0x02, 1 << plane, 0xEF));    // SEQ map mask
            foreach (CockpitPaintRun run in runs)
            {
                EmitVgaRun(code, data, run, plane);
            }
        }

        code.AddRange(Bytes(0x1F, 0x5F, 0x5E, 0x5A, 0x59, 0x5B, 0x5D, 0xCB));

        List<byte> head = new List<byte>();
        head.AddRange(Bytes(0x55, 0x8B, 0xEC, 0x53, 0x51, 0x52, 0x56, 0x57, 0x1E, 0x06));
        head.AddRange(Bytes(0x2E, 0x8C, 0x0E, 0x0A, 0x00));
        byte[] tail = Bytes(0x07, 0x1F, 0x5F, 0x5E, 0x5A, 0x59, 0x5B, 0x5D, 0xCB);

        // The fourth entry slot's stub is a far NOP: the same register saves and restores with no
        // body and — unlike the init stub — no `mov cs:[0x0A], cs`.  19 B, byte-identical in all six
        // VGA packs.
        List<byte> farNop = new List<byte>();
        farNop.AddRange(Bytes(0x55, 0x8B, 0xEC, 0x53, 0x51, 0x52, 0x56, 0x57, 0x1E, 0x06));
        farNop.AddRange(tail);

        // The init stub's self-patch immediate IS the data offset, so the offset has to be known
        // before the stub is written: header(12) + head + the 7-byte self-patch + tail + far-nop + code.
        int dataStart = 0x0C + head.Count + 7 + tail.Length + farNop.Count + code.Count;

        List<byte> init = new List<byte>(head);
        init.AddRange(Bytes(0x2E, 0xC7, 0x06, 0x08, 0x00));
        init.AddRange(Word(dataStart));
        init.AddRange(tail);

        List<byte> pack = new List<byte>(dataStart + data.Count + elementCount + 1);
        pack.AddRange(Word(VgaPaintEntry));
        pack.AddRange(Word(dataStart + data.Count));    // element-list offset
        pack.AddRange(Word(VgaInitEntry));
        pack.AddRange(Word(VgaInitEntry + init.Count)); // the far-nop stub's entry
        pack.AddRange(Word(0));                         // data far pointer: patched at load time
        pack.AddRange(Word(0));
        pack.AddRange(init);
        pack.AddRange(farNop);
        pack.AddRange(code);
        pack.AddRange(data);
        pack.AddRange(IdentityElements(elementCount));
        return [.. pack];
    }

    /// <summary>Emits a linear 8-bit (MCGA) pack.</summary>
    /// <param name="runs">The cockpit bitmap as opaque runs, row-major.</param>
    /// <param name="elementCount">How many entries the element list carries.</param>
    public static byte[] EmitMcga(IReadOnlyList<CockpitPaintRun> runs, int elementCount)
    {
        ArgumentNullException.ThrowIfNull(runs);
        List<byte> code = new List<byte>();
        List<byte> data = new List<byte>();

        code.AddRange(Bytes(0x55, 0x8B, 0xEC, 0x56, 0x57, 0x1E, 0x8C, 0xC8, 0x8E, 0xD8));
        code.AddRange(Bytes(0x8B, 0x46, 0x06, 0x8E, 0xC0));
        code.AddRange(Bytes(0xBF, 0x00, 0x00, 0xBE, 0x04, 0x00));

        foreach (CockpitPaintRun run in runs)
        {
            int at = (run.Row * CockpitPackParser.ScreenWidth) + run.X;
            switch (run.Pixels.Length)
            {
                case 1:
                    code.AddRange(Bytes(0x26, 0xC6, 0x06));
                    code.AddRange(Word(at));
                    code.Add(run.Pixels[0]);
                    break;

                case 2:
                    code.AddRange(Bytes(0x26, 0xC7, 0x06));
                    code.AddRange(Word(at));
                    code.Add(run.Pixels[0]);
                    code.Add(run.Pixels[1]);
                    break;

                default:
                    code.Add(0xBF);
                    code.AddRange(Word(at));
                    code.Add(0xB9);
                    code.AddRange(Word(run.Pixels.Length));
                    code.AddRange(Bytes(0xF3, 0xA4));
                    data.AddRange(run.Pixels);
                    break;
            }
        }

        code.AddRange(Bytes(0x1F, 0x5F, 0x5E, 0x5D, 0xCB));

        List<byte> pack = new List<byte>();
        pack.AddRange(Word(4 + data.Count + elementCount + 1));   // paint entry = after the element list
        pack.AddRange(Word(4 + data.Count));                      // element-list offset
        pack.AddRange(data);
        pack.AddRange(IdentityElements(elementCount));
        pack.AddRange(code);
        return [.. pack];
    }

    private static void EmitVgaRun(List<byte> code, List<byte> data, CockpitPaintRun run, int plane)
    {
        int a = run.X;
        int c = run.EndX - 1;
        int coreStart = (a + 3) & ~3;
        int coreEnd = ((c + 1) & ~3) - 1;
        List<int> edges = new List<int>();

        if (coreStart <= coreEnd)
        {
            int words = (coreEnd - coreStart + 1) / 4;
            code.Add(0xBF);
            code.AddRange(Word((run.Row * CockpitPackParser.ModeXPlanePitch) + (coreStart / 4)));
            int at = coreStart;
            if (words % 2 == 1)
            {
                code.Add(0xA4);                                   // movsb
                data.Add(Pixel(run, at + plane));
                at += 4;
                words--;
            }

            if (words == 2)
            {
                code.Add(0xA5);                                   // movsw
                data.Add(Pixel(run, at + plane));
                data.Add(Pixel(run, at + 4 + plane));
                at += 8;
            }
            else if (words > 2)
            {
                code.Add(0xB9);
                code.AddRange(Word(words / 2));
                code.AddRange(Bytes(0xF3, 0xA5));                 // rep movsw
                for (int i = 0; i < words; i++)
                {
                    data.Add(Pixel(run, at + plane));
                    at += 4;
                }
            }

            for (int x = a; x < coreStart; x++)
            {
                if (x % 4 == plane)
                {
                    edges.Add(x);
                }
            }

            for (int x = coreEnd + 1; x <= c; x++)
            {
                if (x % 4 == plane)
                {
                    edges.Add(x);
                }
            }
        }
        else
        {
            for (int x = a; x <= c; x++)
            {
                if (x % 4 == plane)
                {
                    edges.Add(x);
                }
            }
        }

        foreach (int x in edges)
        {
            code.AddRange(Bytes(0x26, 0xC6, 0x06));
            code.AddRange(Word((run.Row * CockpitPackParser.ModeXPlanePitch) + (x / 4)));
            code.Add(Pixel(run, x));
        }
    }

    private static byte Pixel(CockpitPaintRun run, int x)
    {
        int index = x - run.X;
        if (index < 0 || index >= run.Pixels.Length)
        {
            throw new InvalidDataException(
                $"run at row {run.Row} x{run.X}..{run.EndX - 1} has no pixel for column {x}; the run " +
                "list is not the one the generator's alignment rule produced");
        }

        return run.Pixels[index];
    }

    private static byte[] IdentityElements(int elementCount)
    {
        if (elementCount is < 0 or > 0xFF)
        {
            throw new InvalidDataException(
                $"element count {elementCount} does not fit an identity element list (0..254)");
        }

        byte[] list = new byte[elementCount + 1];
        for (int i = 0; i < elementCount; i++)
        {
            list[i] = (byte)i;
        }

        list[elementCount] = CockpitPackParser.ElementListTerminator;
        return list;
    }

    private static byte[] Bytes(params int[] values)
    {
        byte[] bytes = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i] = (byte)values[i];
        }

        return bytes;
    }

    private static byte[] Word(int value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
        return bytes;
    }
}
