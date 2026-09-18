using System.Buffers.Binary;

namespace CYAC.Port.Core.Sim.Flight.Trace;

/// <summary>
/// The <c>flags</c> byte of a flight-trace record.
/// </summary>
[Flags]
public enum FlightTraceFlags : byte
{
    /// <summary>No flag set.</summary>
    None = 0x00,

    /// <summary>bit 0 — the step was opened by the idle-step watchdog, not by a frame landmark.</summary>
    IdleStep = 0x01,

    /// <summary>
    /// bit 1 — the <c>aircraft_ctrl_axes_relax_per_frame @image@0x2A5A2</c> arm ran this step.
    /// </summary>
    RelaxArm = 0x02,

    /// <summary>
    /// bit 2 — the <c>aircraft_joystick_integrate_active @image@0x2B268</c> arm ran this step.
    /// </summary>
    IntegrateArm = 0x04,

    /// <summary>bit 3 — the record is NOT for the canonical master at <c>[0xEF98]</c>.</summary>
    OffCanonicalMaster = 0x08,
}

/// <summary>
/// One fixed-size record of a flight trace: the flight kernel's whole state window at one stage or
/// probe trap.
/// </summary>
/// <remarks>
/// <para>
/// <b>The buffer is REUSED.</b>  <see cref="FlightTraceReader.Records"/> yields the same underlying
/// array over and over so a 125 MB trace costs no allocation; a record that must outlive the next
/// iteration has to be <see cref="Copy"/>-ed.  Copying is what a verifier does when it needs an
/// earlier stage's record as the input state of a later probe.
/// </para>
/// <para>
/// Every offset comes from the <see cref="Header"/>, never from a constant.
/// </para>
/// </remarks>
public readonly struct FlightTraceRecord
{
    private readonly byte[] _buffer;

    internal FlightTraceRecord(FlightTraceHeader header, byte[] buffer)
    {
        Header = header;
        _buffer = buffer;
    }

    /// <summary>The trace's header — the source of every offset below.</summary>
    public FlightTraceHeader Header { get; }

    /// <summary>The record's raw bytes.</summary>
    public ReadOnlySpan<byte> Bytes => _buffer;

    /// <summary>The determinism simulation step this record was taken in.</summary>
    public uint Step => BinaryPrimitives.ReadUInt32LittleEndian(Slice("step", 4));

    /// <summary>The raw <c>stage</c> byte: a stage id, or a probe id with bit 7 set.</summary>
    public byte StageByte => _buffer[Header.Field("stage")];

    /// <summary>True when this is a probe record.</summary>
    public bool IsProbe => (StageByte & 0x80) != 0;

    /// <summary>The stage or probe id, with the probe bit removed.</summary>
    public int Id => StageByte & 0x7F;

    /// <summary>The trap this record was taken at, or null when the header does not name that id.</summary>
    /// <remarks>
    /// An unknown id is skipped rather than fatal ("the tables are the enumeration").
    /// </remarks>
    public FlightTraceTrap? Trap =>
        (IsProbe ? Header.Probes : Header.Stages).TryGetValue(Id, out FlightTraceTrap trap) ? trap : null;

    /// <summary>The record's flag bits.</summary>
    public FlightTraceFlags Flags => (FlightTraceFlags)_buffer[Header.Field("flags")];

    /// <summary><c>g_active_aircraft_master_ptr [0xF1BC]</c> at the trap.</summary>
    public ushort MasterPointer => BinaryPrimitives.ReadUInt16LittleEndian(Slice("masterPtr", 2));

    /// <summary>The emulator's instruction count at the trap — a total order over all records.</summary>
    public ulong InstructionCount => BinaryPrimitives.ReadUInt64LittleEndian(Slice("instr", 8));

    /// <summary>The 298-byte <c>s_aircraft_master</c> snapshot.</summary>
    public ReadOnlySpan<byte> Master =>
        _buffer.AsSpan(Header.Field("master"), Header.MasterLength);

    /// <summary>
    /// The head of the player world-object block master <c>+0x11A</c> points at (K0 finding F1: it is
    /// <c>g_alt_object_farptr [0x00C0]</c>, the player's <c>WorldObject</c>, NOT an FMD copy).
    /// </summary>
    public ReadOnlySpan<byte> PlayerObject => _buffer.AsSpan(Header.Field("alt"), Header.AltLength);

    /// <summary>The player world object's <c>pos_y</c> — <c>+0x0A</c>, an <c>i32</c> in Q8 feet.</summary>
    /// <remarks>
    /// This is the altitude every FME query reads (<c>mov ax,es:[bx+0xa] ; mov dx,es:[bx+0xc]</c> at
    /// <c>image@0x2ABBE</c>, <c>image@0x2A906</c> and <c>image@0x2A9D6</c>).
    /// </remarks>
    public int PlayerAltitudeQ8Feet =>
        BinaryPrimitives.ReadInt32LittleEndian(PlayerObject.Slice(0x0A, 4));

    /// <summary>One of the seven register words the recorder captured.</summary>
    /// <param name="name">A name from the header's <c>regNames</c> (<c>"ax"</c>, <c>"arg0"</c>, …).</param>
    /// <exception cref="ArgumentException">The header does not carry that register.</exception>
    public ushort Register(string name)
    {
        int index = Header.RegisterIndex(name);
        if (index < 0)
        {
            throw new ArgumentException(
                $"the flight trace does not carry a register named '{name}'", nameof(name));
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(
            _buffer.AsSpan(Header.Field("regs") + (index * 2), 2));
    }

    /// <summary>The low byte of <c>AX</c> — the status byte an exit probe's function returned.</summary>
    public byte ResultAl => (byte)Register("ax");

    /// <summary>One DGROUP window, by the name the header gives it.</summary>
    /// <param name="name"></param>
    /// <exception cref="ArgumentException">The header does not carry that window.</exception>
    public ReadOnlySpan<byte> Global(string name)
    {
        if (!Header.Globals.TryGetValue(name, out FlightTraceGlobal global))
        {
            throw new ArgumentException(
                $"the flight trace does not carry a global window named '{name}'", nameof(name));
        }

        return _buffer.AsSpan(global.RecordOffset, global.Length);
    }

    /// <summary>The first word of a named DGROUP window.</summary>
    /// <param name="name">The global's name.</param>
    public ushort GlobalWord(string name) => BinaryPrimitives.ReadUInt16LittleEndian(Global(name));

    /// <summary>The first byte of a named DGROUP window.</summary>
    /// <param name="name">The global's name.</param>
    public byte GlobalByte(string name) => Global(name)[0];

    /// <summary>An owning copy that survives the reader's next step.</summary>
    public FlightTraceRecord Copy() => new(Header, [.. _buffer]);

    /// <summary>A one-line description for test output.</summary>
    public override string ToString() =>
        $"step {Step} {(IsProbe ? "probe" : "stage")} {Id} ({Trap?.Name ?? "?"}) "
            + $"flags=0x{(byte)Flags:X2} master=0x{MasterPointer:X4}";

    private ReadOnlySpan<byte> Slice(string field, int length) =>
        _buffer.AsSpan(Header.Field(field), length);
}
