namespace CYAC.Port.Transform.Cli;

/// <summary>What a <c>cyac-transform</c> invocation asks for.</summary>
public enum TransformCommand
{
    /// <summary>Transform originals into a data tree, optionally verifying afterwards.</summary>
    Transform,

    /// <summary>Re-verify an existing data tree against the originals its manifest names.</summary>
    VerifyTree,

    /// <summary>Print the input hash manifest for a directory of originals and stop.</summary>
    Hashes,

    /// <summary>Rebuild the original files from an existing data tree into an output directory.</summary>
    Inverse,
}

/// <summary>
/// The parsed command line.
/// </summary>
/// <param name="Command">Which of the three forms was used.</param>
/// <param name="OriginalDirectory">The directory of shipping originals, for <see cref="TransformCommand.Transform"/> and <see cref="TransformCommand.Hashes"/>; the OUTPUT directory for <see cref="TransformCommand.Inverse"/>.</param>
/// <param name="DataDirectory">The data tree, for <see cref="TransformCommand.Transform"/>, <see cref="TransformCommand.VerifyTree"/> and <see cref="TransformCommand.Inverse"/>.</param>
/// <param name="Verify">Whether a transform run should verify what it wrote.</param>
/// <param name="AllowUnknownVersion">Whether to proceed when the originals match no known distribution.</param>
/// <param name="Only">The family filter; empty means every family.</param>
public sealed record TransformOptions(
    TransformCommand Command,
    string? OriginalDirectory,
    string? DataDirectory,
    bool Verify,
    bool AllowUnknownVersion,
    IReadOnlyList<string> Only);

/// <summary>
/// The command-line parser: pure, testable, and the only place that knows the argument syntax.
/// </summary>
public static class CommandLine
{
    /// <summary>The usage text, printed on a parse error and on <c>--help</c>.</summary>
    public const string Usage = """
        cyac-transform — turn Chuck Yeager's Air Combat originals into an open data tree.

          cyac-transform <originals> <data-dir> [options]
              Transform the originals into <data-dir>.  <originals> is a directory or a .zip; it is
              searched recursively (zip entries in any folder, and a zip inside a zip), and the files
              are identified by size + SHA-256, whatever they are called.

          cyac-transform --verify <data-dir>
              Re-encode an existing tree and diff it against the originals its manifest names.

          cyac-transform --inverse <data-dir> <out-dir>
              Rebuild the original .lib / .cfg files from a data tree (your edits included).

          cyac-transform --hashes <originals>
              Print the input hash manifest — what was found, where, and how it was identified.

        Options:
          --verify                    verify the tree after writing it
          --allow-unknown-version     proceed when the originals match no known distribution
                                      (every output is then stamped source_verified: false)
          --only <family>[,<family>]  restrict the run to these families
          -h, --help                  this text

        Exit codes: 0 ok · 1 refused or verify mismatch · 2 usage.
        """;

    /// <summary>
    /// Parses an argument vector.
    /// </summary>
    /// <param name="args">The arguments, without the program name.</param>
    /// <param name="options">The parsed options when parsing succeeded.</param>
    /// <param name="error">The reason parsing failed, when it did.</param>
    public static bool TryParse(IReadOnlyList<string> args, out TransformOptions? options, out string? error)
    {
        options = null;
        error = null;
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            error = args.Count == 0 ? "no arguments" : null;
            return false;
        }

        List<string> positional = new List<string>();
        List<string> only = new List<string>();
        bool verify = false;
        bool allowUnknown = false;
        bool verifyTree = false;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--verify":
                    // The first token being --verify selects the "verify an existing tree" form.
                    if (i == 0)
                    {
                        verifyTree = true;
                    }

                    verify = true;
                    break;

                case "--allow-unknown-version":
                    allowUnknown = true;
                    break;

                case "--hashes":
                    if (i != 0)
                    {
                        error = "--hashes must be the first argument";
                        return false;
                    }

                    if (args.Count != 2)
                    {
                        error = "--hashes takes exactly one directory";
                        return false;
                    }

                    options = new TransformOptions(TransformCommand.Hashes, args[1], null, false, false, []);
                    return true;

                case "--inverse":
                    if (i != 0)
                    {
                        error = "--inverse must be the first argument";
                        return false;
                    }

                    if (args.Count != 3)
                    {
                        error = "--inverse takes a data directory and an output directory";
                        return false;
                    }

                    options = new TransformOptions(
                        TransformCommand.Inverse, args[2], args[1], false, false, []);
                    return true;

                case "--only":
                    if (i + 1 >= args.Count)
                    {
                        error = "--only needs a family list";
                        return false;
                    }

                    only.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;

                default:
                    if (arg.StartsWith('-'))
                    {
                        error = $"unknown option \"{arg}\"";
                        return false;
                    }

                    positional.Add(arg);
                    break;
            }
        }

        if (verifyTree)
        {
            if (positional.Count != 1)
            {
                error = "--verify <data-dir> takes exactly one directory";
                return false;
            }

            options = new TransformOptions(TransformCommand.VerifyTree, null, positional[0], true, allowUnknown, only);
            return true;
        }

        if (positional.Count != 2)
        {
            error = $"expected <original-dir> <data-dir>, got {positional.Count} path(s)";
            return false;
        }

        options = new TransformOptions(
            TransformCommand.Transform, positional[0], positional[1], verify, allowUnknown, only);
        return true;
    }
}
