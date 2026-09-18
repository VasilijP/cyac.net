using System.Globalization;

namespace CYAC.Port.Audio;

/// <summary>One driver call the port made, in the shape the emulator's semantic tap records.</summary>
/// <param name="Sample">The 48 kHz output sample the call landed at.</param>
/// <param name="Command">The driver command word.</param>
/// <param name="Arg0">The tone id, for the SFX commands.</param>
/// <param name="Arg1">The pitch.</param>
/// <param name="Arg2">The volume.</param>
/// <param name="Site">Which trampoline made it.</param>
public readonly record struct PortDriverCall(
    long Sample, ushort Command, ushort Arg0, ushort Arg1, ushort Arg2, string Site);

/// <summary>
/// The port's own driver-call log — a <c>&lt;log&gt;.drvcalls.csv</c>-shaped census of what the
/// sound path did.
/// </summary>
/// <remarks>
/// <para>
/// This is the PLAUSIBILITY instrument the H11 brief asks for: run a headless sortie, dump this, and
/// compare the tone histogram with a recorded session's (&lt;session&gt;.drvcalls.csv</c>, produced by
/// the emulator's <c>--refined</c> replay).  The same situations should fire the same tone ids.  It
/// is NOT a byte oracle — the two runs are different sorties — and the generator proof does not rest
/// on it.
/// </para>
/// <para>
/// The emulator's schema is <c>tick,kind,site,cmd,a0,a1,a2,ret,caller_cs,caller_ip,ax_in</c>.  The
/// port has no instruction count, no return value and no guest code addresses, so those columns
/// carry the port's own equivalents (<c>sample</c> for <c>tick</c>, the trampoline NAME for
/// <c>caller_ip</c>) and the header says so.
/// </para>
/// </remarks>
public sealed class DriverCallCensus
{
    private readonly List<PortDriverCall> _calls = [];
    private readonly long[] _toneStarts = new long[AdlDriverImage.DescriptorCount];

    /// <summary>Every call, in order.</summary>
    public IReadOnlyList<PortDriverCall> Calls => _calls;

    /// <summary>How many calls were recorded.</summary>
    public int Count => _calls.Count;

    /// <summary>The largest number of calls kept; older ones are dropped.</summary>
    public int Capacity { get; init; } = 1 << 20;

    /// <summary>How many <c>cmd 0</c> starts each tone id received.</summary>
    public IReadOnlyList<long> ToneStartHistogram => _toneStarts;

    /// <summary>Records one call.</summary>
    /// <param name="call">The call.</param>
    public void Note(in PortDriverCall call)
    {
        if (call.Command == 0 && call.Arg0 < _toneStarts.Length)
        {
            _toneStarts[call.Arg0]++;
        }

        if (_calls.Count < Capacity)
        {
            _calls.Add(call);
        }
    }

    /// <summary>Writes the census as CSV.</summary>
    /// <param name="path">Where to write it.</param>
    public void WriteCsv(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using StreamWriter writer = new StreamWriter(path);
        writer.WriteLine("sample,kind,site,cmd,a0,a1,a2");
        foreach (PortDriverCall call in _calls)
        {
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{call.Sample},DriverCall,{call.Site},0x{call.Command:X2},"
                    + $"0x{call.Arg0:X4},0x{call.Arg1:X4},0x{call.Arg2:X4}"));
        }
    }

    /// <summary>The tone histogram as a one-line summary, biggest first.</summary>
    public string HistogramLine()
    {
        List<(int Tone, long Count)> rows = new List<(int Tone, long Count)>();
        for (int tone = 0; tone < _toneStarts.Length; tone++)
        {
            if (_toneStarts[tone] > 0)
            {
                rows.Add((tone, _toneStarts[tone]));
            }
        }

        rows.Sort((a, b) => b.Count.CompareTo(a.Count));
        return rows.Count == 0
            ? "(no tone starts)"
            : string.Join(
                ", ",
                rows.Select(r => string.Create(
                    CultureInfo.InvariantCulture, $"0x{r.Tone:X2}×{r.Count:N0}")));
    }
}
