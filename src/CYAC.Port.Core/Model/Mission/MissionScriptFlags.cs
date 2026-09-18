namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The five per-object script flags an author can set (attrs <c>0x8A</c>, <c>0x8B</c>, <c>0x8C</c>,
/// <c>0x8D</c>, <c>0x8E</c>).
/// </summary>
/// <remarks>
/// The parser accumulates them in <c>[bp-0x4E]</c> and commits them to the object's <c>+0x24</c>
/// <b>only when the object also carries a script</b> (<c>image@0x0A240..0x0A243</c>), from where they
/// reach the AI VM's script-flag register <c>[0xED78]</c>.  The bit VALUES are byte-verified; what each
/// bit means to the VM is open (<c>P15_DATA_MODEL_FINDINGS.md</c> §2e).  <see cref="Bit08"/> is
/// dormant — no shipped object sets it.
/// </remarks>
[Flags]
public enum MissionScriptFlags
{
    /// <summary>No script flags.</summary>
    None = 0,

    /// <summary>0x01 — attr <c>0x8A</c>.</summary>
    Bit01 = 0x01,

    /// <summary>0x04 — attr <c>0x8B</c>.</summary>
    Bit04 = 0x04,

    /// <summary>0x08 — attr <c>0x8C</c>; dormant in the shipped data.</summary>
    Bit08 = 0x08,

    /// <summary>0x10 — attr <c>0x8D</c>.</summary>
    Bit10 = 0x10,

    /// <summary>0x20 — attr <c>0x8E</c>.</summary>
    Bit20 = 0x20,
}
