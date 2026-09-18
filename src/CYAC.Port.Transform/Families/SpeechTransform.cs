using System.Text.Json;
using CYAC.Formats.Audio;
using CYAC.Port.Core.Data;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// The 25 <c>.SND</c> speech clips of <c>4a.lib</c> → <c>audio/speech/&lt;name&gt;.wav</c> plus a
/// <c>.json</c> side-car holding the header the WAV cannot carry.
/// </summary>
/// <remarks>
/// <para>
/// The asset is an 8-byte BIG-ENDIAN header <c>{u16 mode, u32 len, u16 rate}</c> followed by the
/// sample stream — read from its consumer, <c>BLASTER.SP</c>'s <c>play</c> export
/// (<see cref="SndSpeech"/>; Every shipped clip is mode 0 (unsigned 8-bit PCM) at 7,168 Hz, and its
/// body is exactly the declared length — so the WAV carries the samples losslessly and the JSON
/// carries mode, rate and the declared length, which together re-emit the asset byte for byte.
/// </para>
/// <para>
/// The port's audio rule (replace, do not reproduce) is about what the PORT plays; the
/// transform's job is to make the shipped clip readable and editable, and WAV is that form. A modder
/// drops a new WAV in and the inverse writes a valid <c>.SND</c>, provided the rate still matches what
/// the speech module was told (the JSON's <c>sampleRate</c> is what goes into the header).
/// </para>
/// </remarks>
public sealed class SpeechTransform : IFamilyTransform
{
    /// <summary>The family name.</summary>
    public const string FamilyName = "snd";

    /// <summary>The data tree folder this family writes into.</summary>
    public const string Folder = "audio/speech";

    /// <inheritdoc/>
    public string Family => FamilyName;

    /// <inheritdoc/>
    public string TreeDescription =>
        "`audio/speech/<name>.wav` — a .SND speech clip as unsigned 8-bit mono PCM (the shipped rate " +
        "is 7168 Hz); `audio/speech/<name>.json` carries the .SND header (mode, rate, declared " +
        "length) so the pair re-emits the original asset exactly.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is not null && source.Extension == ".snd";
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        SndSpeech clip = SndSpeech.Parse(source.Content.Span);
        string stem = TransformContext.SafeFileName(source.Stem);
        string jsonPath = context.Allocate($"{Folder}/{stem}.json");
        bool pcm = clip.IsPcm8;
        string audioPath = context.Allocate($"{Folder}/{stem}{(pcm ? ".wav" : ".pcm")}");

        UnknownBytes unknown = new UnknownBytes();
        if (clip.Trailing.Length != 0)
        {
            unknown.Add(SndSpeech.HeaderBytes + clip.Samples.Length, clip.Trailing);
        }

        SpeechClipDto dto = new SpeechClipDto
        {
            Format = "cyac.speech/1",
            About = pcm
                ? "One .SND speech clip. The samples live in the sibling WAV (unsigned 8-bit mono " +
                  "PCM); this file carries the .SND header the WAV has no room for. Edit the WAV to " +
                  "change the audio and `sampleRate` here to change the playback rate — the pair is " +
                  "what the inverse re-encodes."
                : "One .SND clip whose mode is not 0, so its payload is not plain PCM and is carried " +
                  "verbatim in the sibling .pcm file rather than decoded. The module's mode-1 path is " +
                  "hardware ADPCM; no shipped clip uses it.",
            Source = $"{source.OriginFile}/{source.Name}",
            AudioFile = pcm ? Path.GetFileName(audioPath) : null,
            PayloadFile = pcm ? null : Path.GetFileName(audioPath),
            Mode = clip.Mode,
            ModeName = clip.Mode switch
            {
                SndSpeech.ModePcm8 => "unsigned 8-bit PCM",
                SndSpeech.ModeAdpcm => "hardware ADPCM (module vtable at +0x209)",
                _ => "unknown device mode",
            },
            SampleRate = clip.SampleRate,
            SampleCount = clip.DeclaredLength,
            DurationMs = clip.SampleRate > 0
                ? (int)Math.Round(clip.DeclaredLength * 1000.0 / clip.SampleRate)
                : 0,
            UnknownTrailing = PortHex.Bytes(clip.Trailing),
        };

        byte[] audio = pcm
            ? WavPcm8.Encode(clip.SampleRate, clip.Samples)
            : clip.Samples;

        return
        [
            new TransformOutput(
                jsonPath,
                JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.SpeechClipDto),
                OutputRole.Data,
                OutputFidelity.Exact,
                null,
                unknown.Count),
            new TransformOutput(audioPath, audio, OutputRole.Data, OutputFidelity.Exact),
        ];
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput json = outputs.FirstOrDefault(o => o.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("a speech clip needs its .json side-car");
        SpeechClipDto dto = JsonSerializer.Deserialize(json.Bytes, PortDataJsonContext.Readable.SpeechClipDto)
                            ?? throw new InvalidDataException($"{json.Path} is empty");

        string wanted = dto.AudioFile ?? dto.PayloadFile
            ?? throw new InvalidDataException($"{json.Path} names no audio file");
        LoadedOutput audio = outputs.FirstOrDefault(
                                 o => string.Equals(Path.GetFileName(o.Path), wanted, StringComparison.OrdinalIgnoreCase))
                             ?? throw new InvalidDataException($"{json.Path} names \"{wanted}\", which is not in the tree");

        byte[] samples;
        int rate = dto.SampleRate;
        if (dto.AudioFile is not null)
        {
            samples = WavPcm8.Decode(audio.Bytes, out int wavRate);
            if (wavRate != rate)
            {
                // The header is what the speech module reads; a WAV that disagrees with the side-car
                // is an editing mistake worth naming, not something to silently pick a winner for.
                throw new InvalidDataException(
                    $"{audio.Path} is {wavRate} Hz but {json.Path} says {rate} Hz; make them agree");
            }
        }
        else
        {
            samples = audio.Bytes;
        }

        byte[] trailing = string.IsNullOrWhiteSpace(dto.UnknownTrailing)
            ? []
            : Convert.FromHexString(dto.UnknownTrailing.Trim());
        long declared = dto.SampleCount == 0 ? samples.Length : dto.SampleCount;
        if (declared != samples.Length)
        {
            // The shipped clips all declare exactly their payload; an edit that changes the sample
            // count must change the header too, and the inverse does that rather than lie.
            declared = samples.Length;
        }

        return SndSpeech.FromSamples(dto.Mode, rate, samples, trailing, declared).ToBytes();
    }
}
