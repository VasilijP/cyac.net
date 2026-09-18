using CYAC.Port.Core.Sim.Combat.Trace;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// Maps a combat-trace STAGE record's globals area onto a <see cref="CombatRegisters"/> and back.
/// </summary>
/// <remarks>
/// <para>
/// The window layout is taken from the TRACE HEADER, never from
/// <see cref="CombatRegisterWindows.All"/> — a header may add or resize a window inside format v1,
/// and a reader that hard-codes offsets "deserves what it gets".
/// <see cref="CombatRegisterWindows.All"/> is the port's own default for a kernel running without a
/// trace, and <see cref="LayoutMatchesDeclaration"/> is how a test notices they diverged.
/// </para>
/// <para>
/// The round trip is exact by construction: the store IS the record's globals area, in the same
/// order, so <see cref="Encode"/> reproduces those bytes verbatim.
/// </para>
/// </remarks>
public static class CombatRegistersCodec
{
    /// <summary>The window layout a trace header declares, as port windows.</summary>
    /// <param name="header">The trace header.</param>
    public static IReadOnlyList<CombatRegisterWindow> LayoutOf(CombatTraceHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        Dictionary<string, CombatRegisterWindow> declared = CombatRegisterWindows.All.ToDictionary(w => w.Name, StringComparer.Ordinal);
        return
        [
            .. header.Globals.Values
                .OrderBy(g => g.RecordOffset)
                .Select(g => new CombatRegisterWindow(
                    g.Name,
                    g.DgroupOffset,
                    g.Length,
                    declared.TryGetValue(g.Name, out CombatRegisterWindow known)
                        ? known.Class
                        : CombatFieldClass.IntegerSpine)),
        ];
    }

    /// <summary>
    /// True when a trace header's window layout is exactly the port's declaration — same names,
    /// offsets and lengths, in the same order.
    /// </summary>
    /// <param name="header">The trace header.</param>
    public static bool LayoutMatchesDeclaration(CombatTraceHeader header)
    {
        CombatRegisterWindow[] traced = LayoutOf(header).OrderBy(w => w.DgroupOffset).ToArray();
        CombatRegisterWindow[] declared = CombatRegisterWindows.All.OrderBy(w => w.DgroupOffset).ToArray();
        if (traced.Length != declared.Length)
        {
            return false;
        }

        for (int i = 0; i < traced.Length; i++)
        {
            if (traced[i].Name != declared[i].Name
                || traced[i].DgroupOffset != declared[i].DgroupOffset
                || traced[i].Length != declared[i].Length)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads a stage record's globals area into a fresh register file.</summary>
    /// <param name="record">A STAGE record.</param>
    /// <exception cref="ArgumentException">The record is a probe record.</exception>
    public static CombatRegisters Decode(CombatTraceRecord record)
    {
        if (!record.IsStage)
        {
            throw new ArgumentException(
                "only a STAGE record carries the DGROUP windows", nameof(record));
        }

        CombatRegisters registers = new CombatRegisters(LayoutOf(record.Header));
        foreach (CombatTraceGlobal window in record.Header.Globals.Values)
        {
            record.Bytes.Slice(window.RecordOffset, window.Length)
                .CopyTo(registers.Span(window.DgroupOffset, window.Length));
        }

        return registers;
    }

    /// <summary>The register file's bytes in the record's own globals-area order.</summary>
    /// <param name="registers">The register file.</param>
    public static byte[] Encode(CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        return [.. registers.Bytes];
    }

    /// <summary>The globals area of a stage record, for a byte-for-byte diff against a decode.</summary>
    /// <param name="record">A STAGE record.</param>
    public static ReadOnlySpan<byte> GlobalsArea(CombatTraceRecord record) =>
        record.Bytes.Slice(record.Header.Field("globals"), record.Header.GlobalsLength);
}
