using CYAC.Formats.EaLib;
using CYAC.Formats.Exe;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Input;

/// <summary>
/// <see cref="IOriginalData"/> over a scanned <see cref="InputSet"/>: opens an archive or unpacks the
/// executable at most once per run, then answers from memory.
/// </summary>
/// <remarks>
/// Unpacking <c>yeager.exe</c> costs a couple of hundred milliseconds, and the mask family asks for
/// the image once per mask, so the laziness is not decoration.  Everything here is read-only and
/// forward-path only; nothing it returns is ever written into the tree, which keeps the no-original-data rule intact
/// (what ships is the geometry, not the image).  The bytes come from the inputs themselves, not from a
/// path, because an input may be a zip entry.
/// </remarks>
public sealed class OriginalData : IOriginalData
{
    private readonly InputSet _inputs;
    private readonly Dictionary<string, EaLibArchive?> _archives = new(StringComparer.OrdinalIgnoreCase);
    private byte[]? _image;
    private bool _imageAttempted;

    /// <summary>Creates a provider over one scan of the originals.</summary>
    /// <param name="inputs">The scanned originals.</param>
    public OriginalData(InputSet inputs) => _inputs = inputs;

    /// <summary>Why the program image is unavailable, when it is.</summary>
    public string? ImageProblem { get; private set; }

    /// <inheritdoc/>
    public byte[]? TryGetArchiveMember(string archiveFileName, string memberName)
    {
        ArgumentNullException.ThrowIfNull(archiveFileName);
        ArgumentNullException.ThrowIfNull(memberName);
        if (!_archives.TryGetValue(archiveFileName, out EaLibArchive? archive))
        {
            InputFile? file = _inputs.Find(archiveFileName);
            archive = file is null ? null : new EaLibArchive(file.ReadAllBytes(), file.Name);
            _archives[archiveFileName] = archive;
        }

        EaLibEntry? entry = archive?.Entries.FirstOrDefault(
            e => string.Equals(e.Name, memberName, StringComparison.OrdinalIgnoreCase));
        return entry?.GetDecoded();
    }

    /// <inheritdoc/>
    public byte[]? TryGetProgramImage()
    {
        if (_imageAttempted)
        {
            return _image;
        }

        _imageAttempted = true;
        if (_inputs.Executable is not { } executable)
        {
            ImageProblem = $"{KnownDistributions.ExecutableName} is not in the originals";
            return null;
        }

        try
        {
            UnpackedImage unpacked = YeagerExeUnpacker.Unpack(executable.ReadAllBytes(), executable.Name);
            _image = unpacked.ImageAtLoadSeg(unpacked.LoadSegment);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            ImageProblem = $"{executable.Name} could not be unpacked: {ex.Message}";
        }

        return _image;
    }
}
