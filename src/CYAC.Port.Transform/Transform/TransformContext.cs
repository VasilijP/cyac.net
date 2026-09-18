using System.Text;

namespace CYAC.Port.Transform.Transform;

/// <summary>
/// What a family transform is given besides the bytes: where the data tree lives, whether the
/// originals were recognised, and a path allocator that keeps output names unique.
/// </summary>
/// <remarks>
/// Path uniqueness matters because EALIB member names are <b>not</b> unique: the shipping
/// <c>2a.lib</c> carries <c>90_horiz.msk</c> twice (<c>EaLibWriter.Rebuild</c> remarks).
/// Two members must never collide onto one file, or the archive's inverse would read the same bytes
/// back twice.  The allocator is also case-insensitive, because a case-insensitive file system would
/// otherwise silently merge <c>A.BIN</c> and <c>a.bin</c>.
/// </remarks>
public sealed class TransformContext
{
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a context for one run.</summary>
    /// <param name="dataDirectory">The data tree's root directory.</param>
    /// <param name="sourceVerified">Whether the originals matched a known distribution.</param>
    /// <param name="distribution">
    /// The matched distribution, when there is one.  A family that can check its own output against
    /// something the table records (the <c>exe</c> family's image digests) needs the entry itself,
    /// not just the flag.
    /// </param>
    /// <param name="originals">
    /// Read access to the other originals, for the two families whose bytes do not describe
    /// themselves (see <see cref="IOriginalData"/>).  Null on the inverse path, which never needs it.
    /// </param>
    public TransformContext(
        string dataDirectory,
        bool sourceVerified,
        Input.KnownDistribution? distribution = null,
        IOriginalData? originals = null)
    {
        DataDirectory = dataDirectory;
        SourceVerified = sourceVerified;
        Distribution = distribution;
        Originals = originals;
    }

    /// <summary>The data tree's root directory.</summary>
    public string DataDirectory { get; }

    /// <summary>Whether the input set matched a <see cref="Input.KnownDistribution"/>.</summary>
    public bool SourceVerified { get; }

    /// <summary>The matched distribution, or <see langword="null"/> when the set is unrecognised.</summary>
    public Input.KnownDistribution? Distribution { get; }

    /// <summary>
    /// The other originals, on the forward path only; <see langword="null"/> on the inverse path.
    /// </summary>
    public IOriginalData? Originals { get; }

    /// <summary>
    /// Reserves a relative output path, disambiguating a collision by appending <c>~2</c>, <c>~3</c>
    /// … before the extension.  Returns the path actually reserved.
    /// </summary>
    /// <param name="relativePath">The wanted path, forward-slashed and relative to the data root.</param>
    public string Allocate(string relativePath)
    {
        string candidate = Normalise(relativePath);
        if (_used.Add(candidate))
        {
            return candidate;
        }

        string directory = candidate.Contains('/') ? candidate[..candidate.LastIndexOf('/')] : string.Empty;
        string file = candidate[(directory.Length == 0 ? 0 : directory.Length + 1)..];
        string stem = Path.GetFileNameWithoutExtension(file);
        string extension = Path.GetExtension(file);

        for (int n = 2; ; n++)
        {
            string retry = directory.Length == 0
                ? $"{stem}~{n}{extension}"
                : $"{directory}/{stem}~{n}{extension}";
            if (_used.Add(retry))
            {
                return retry;
            }
        }
    }

    /// <summary>Whether a relative path has already been reserved in this run.</summary>
    /// <param name="relativePath">The path to test.</param>
    public bool IsAllocated(string relativePath) => _used.Contains(Normalise(relativePath));

    /// <summary>The absolute path of a data-tree-relative path.</summary>
    /// <param name="relativePath">A forward-slashed relative path.</param>
    public string Resolve(string relativePath) =>
        Path.Combine(DataDirectory, Normalise(relativePath).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Makes an original asset name safe to use as a file name, without losing information: any
    /// character a portable file name may not carry becomes <c>%NN</c>.
    /// </summary>
    /// <param name="name">The original member name.</param>
    public static string SafeFileName(string name)
    {
        if (name.Length == 0)
        {
            return "_unnamed";
        }

        StringBuilder builder = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                or '.' or '-' or '_' or '+' or '(' or ')' or ' ';
            if (ok)
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(((int)c).ToString("X2"));
            }
        }

        return builder.ToString();
    }

    private static string Normalise(string relativePath) => relativePath.Replace('\\', '/');
}
