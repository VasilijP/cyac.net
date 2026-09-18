namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The authored bits of a placed object's flag word (the parser's <c>[bp-0x4C]</c>, committed to the
/// spawn's flag word at <c>+5</c>).
/// </summary>
/// <remarks>
/// <see cref="Default40"/> is set for every object at <c>image@0x09B36</c> and <b>cleared</b> by attr
/// <c>0x8F</c>; when it survives, the pool object gets <c>obj[+3] |= 4</c> at <c>image@0x0A1CD</c>.
/// The other three come from attrs <c>0x94</c> / <c>0x9B</c> / <c>0x9F</c>
/// (<c>image@0x09C5A</c> / <c>0x09C68</c> / <c>0x09C76</c>).  Bit MEANINGS are open — the routing is
/// what is verified (<c>P15_DATA_MODEL_FINDINGS.md</c> §2e).
/// </remarks>
[Flags]
public enum MissionObjectFlags
{
    /// <summary>No bits.</summary>
    None = 0,

    /// <summary>0x40 — on by default; attr <c>0x8F</c> clears it.</summary>
    Default40 = 0x40,

    /// <summary>0x80 — attr <c>0x94</c>.</summary>
    Flag80 = 0x80,

    /// <summary>0x100 — attr <c>0x9B</c>.</summary>
    Flag100 = 0x100,

    /// <summary>0x400 — attr <c>0x9F</c>.</summary>
    Flag400 = 0x400,
}
