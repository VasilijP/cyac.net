namespace CYAC.Port.Host.Sound;

/// <summary>Which live output device the host should open.</summary>
public enum AudioOutputKind
{
    /// <summary>The platform's own best answer: see <see cref="AudioOutputSelector.Order"/>.</summary>
    Auto,

    /// <summary>macOS' AudioToolbox AudioQueue (<see cref="MacAudioQueueOutput"/>).</summary>
    Mac,

    /// <summary>SDL2's audio device (<see cref="SdlAudioOutput"/>), on every desktop.</summary>
    Sdl,

    /// <summary>Open nothing; the sound path still runs and <c>--wav</c> still renders it.</summary>
    None,
}

/// <summary>Reads the <c>--audio-output</c> value.</summary>
public static class AudioOutputKinds
{
    /// <summary>The values <c>--audio-output</c> accepts, in help order.</summary>
    public static IReadOnlyList<string> Names { get; } = ["auto", "mac", "sdl", "none"];

    /// <summary>Parses one <c>--audio-output</c> value.</summary>
    /// <param name="text">What the user typed, or null for the default.</param>
    /// <param name="warning">The line to print when the value was not understood, else null.</param>
    /// <returns>The kind to use; an unreadable value falls back to <see cref="AudioOutputKind.Auto"/>.</returns>
    public static AudioOutputKind Parse(string? text, out string? warning)
    {
        warning = null;
        string value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return AudioOutputKind.Auto;
        }

        switch (value.ToLowerInvariant())
        {
            case "auto":
                return AudioOutputKind.Auto;
            case "mac":
            case "macos":
            case "audioqueue":
                return AudioOutputKind.Mac;
            case "sdl":
            case "sdl2":
                return AudioOutputKind.Sdl;
            case "none":
            case "off":
            case "silent":
                return AudioOutputKind.None;
            default:
                warning = $"--audio-output: '{value}' is not one of "
                    + $"{string.Join('|', Names)}; using auto.";
                return AudioOutputKind.Auto;
        }
    }

    /// <summary>The <c>--audio-output</c> token a kind is written as.</summary>
    /// <param name="kind">The kind.</param>
    public static string Name(AudioOutputKind kind) => kind switch
    {
        AudioOutputKind.Mac => "mac",
        AudioOutputKind.Sdl => "sdl",
        AudioOutputKind.None => "none",
        _ => "auto",
    };

    /// <summary>The name the readout uses for a kind's DEVICE.</summary>
    /// <param name="kind">The kind.</param>
    public static string Label(AudioOutputKind kind) => kind switch
    {
        AudioOutputKind.Mac => "AudioQueue",
        AudioOutputKind.Sdl => "SDL2",
        AudioOutputKind.None => "none",
        _ => "auto",
    };
}
