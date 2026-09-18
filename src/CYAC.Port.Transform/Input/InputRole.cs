namespace CYAC.Port.Transform.Input;

/// <summary>
/// What one file of an original CYAC installation is to the transform.
/// </summary>
/// <remarks>
/// Source of truth: (the distribution layout) and (input recognition).
/// </remarks>
public enum InputRole
{
    /// <summary>The game binary, <c>yeager.exe</c> — triple-packed (SLR LZH / OPTLINK EXEPACK / MSC 6.0).</summary>
    Executable,

    /// <summary>One of the six EALIB asset archives (<c>1a 1b 2a 2b 3a 4a</c>).</summary>
    Archive,

    /// <summary>
    /// <c>yeager.cfg</c> — a <b>save</b>, not a distribution file: it is hashed and recorded but
    /// never matched against a known-distribution digest.
    /// </summary>
    Save,
}
