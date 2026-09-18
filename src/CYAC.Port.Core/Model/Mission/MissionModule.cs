using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The x86 module in a <c>.S</c> file's trailer: five exported entry points plus their interleaved
/// string data.  The port carries it as an artefact to be understood, never as code to run.
/// </summary>
/// <remarks>
/// <para>
/// This is the view of <c>module</c> in
/// <c>&lt;data&gt;/missions/&lt;name&gt;.json</c>.  The block's BYTES stay on the transform side,
/// where they belong: the module is code, and the port will never execute it. All 51
/// shipped <c>.S</c> files have a module; the three <c>.W</c> theater catalogs have none.
/// </para>
/// <para>
/// What the module <i>means</i> — the win rules, the briefing and the debrief texts — is modelled by
/// <see cref="MissionWinRules"/>; this type is the container view: the export table, their extents,
/// and the briefing string.
/// </para>
/// </remarks>
public sealed class MissionModule
{
    private readonly MissionModuleDto _document;

    internal MissionModule(MissionModuleDto document) => _document = document;

    /// <summary>Exports in the table: 5.</summary>
    public const int ExportCount = 5;

    /// <summary>The briefing buffer the engine allocates for export 0: 1200 bytes, unchecked.</summary>
    /// <remarks>
    /// <c>scenario_briefing_show @image@0x25239</c> allocates it and export 0 fills it with no length
    /// check.  A longer briefing overruns the far-heap block.
    /// </remarks>
    public const int BriefingBufferBytes = 1200;

    /// <summary>How many bytes the block occupies.</summary>
    public int CodeBytes => _document.CodeBytes;

    /// <summary>The file offset the block's first byte sits at.</summary>
    public int FileOffset => _document.BlockFileOffset;

    /// <summary>The five export offsets, relative to the block base.</summary>
    public IReadOnlyList<int> ExportOffsets =>
        [.. (_document.Exports ?? []).OrderBy(e => e.Slot).Select(e => e.Offset)];

    /// <summary>The block-relative offset of one export.</summary>
    /// <param name="export">Which entry point.</param>
    public int OffsetOf(MissionModuleExport export) => Slot(export).Offset;

    /// <summary>
    /// The distance from an export to the next one (or the block end) — an upper bound on its size,
    /// not its code size: module data is interleaved between the functions.
    /// </summary>
    /// <param name="export">Which entry point.</param>
    public int ExtentOf(MissionModuleExport export) => Slot(export).ExtentUpperBound;

    /// <summary>True when the export starts with the MSC far prologue <c>55 8B EC</c>.</summary>
    /// <param name="export">Which entry point.</param>
    public bool HasCompilerPrologue(MissionModuleExport export) => Slot(export).HasCompilerPrologue;

    /// <summary>
    /// The briefing text export 0 copies out, recovered from its own <c>mov si</c>/<c>mov cx</c> +
    /// <c>rep movsb</c> immediates (P4 §5.1 — works for 50 of the 51 shipped modules; FREE.S's slot 0
    /// is a no-op).  <see langword="null"/> when the pattern is not there.
    /// </summary>
    public string? BriefingText => _document.Briefing?.Text;

    /// <summary>The document section behind this adapter.</summary>
    public MissionModuleDto Source => _document;

    private MissionExportDto Slot(MissionModuleExport export)
    {
        foreach (MissionExportDto entry in _document.Exports ?? [])
        {
            if (entry.Slot == (int)export)
            {
                return entry;
            }
        }

        throw new InvalidDataException($"the module document has no export slot {(int)export}");
    }
}
