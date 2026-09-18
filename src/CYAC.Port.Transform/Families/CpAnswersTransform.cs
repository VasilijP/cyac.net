using System.Text.Json;
using CYAC.Formats.EaLib;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// <c>strings2.bin</c> → <c>strings2.json</c>: the copy-protection answer table — 102
/// manual-lookup questions with their expected answers, plus the failure message.
/// </summary>
/// <remarks>
/// <para>
/// Round 7c identified the asset and predicted "1 fail message + 102 (q,a) pairs = 205 strings"
/// from its (then-undecodable) index region; with the LZSS codec in hand the decompressed body is an
/// ordinary <see cref="StringsBinCodec"/> table of exactly 205 plain-text strings, and the
/// prediction is confirmed.  Indexing is <c>copy_protection_prompt</c>'s
/// (<c>image@0x2529C</c>): question <c>r</c> = string <c>2r+1</c>, answer = <c>2r+2</c>.
/// </para>
/// <para>
/// The port never asks the question — there is no disk to protect — but the table is part of the
/// game's knowledge and is transformed like any other data (manifest role
/// <c>copy_protection</c>).
/// </para>
/// </remarks>
public sealed class CpAnswersTransform : IFamilyTransform
{
    /// <summary>The data-tree path this family writes.</summary>
    public const string OutputPath = "strings2.json";

    /// <summary>The manifest role recorded for this output.</summary>
    public const string ManifestRole = "copy_protection";

    /// <inheritdoc/>
    public string Family => "strings2";

    /// <inheritdoc/>
    public string TreeDescription =>
        "`strings2.json` — the 1991 copy-protection quiz: 102 aircraft-specification questions " +
        "answered from the printed manual, plus the message shown after a wrong answer. The port " +
        "does not ask them; the data is here because it is part of the game.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is not null && CpAnswerTableCodec.IsAnswerTable(source.Name);
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        CpAnswerTable table = CpAnswerTableCodec.Parse(source.Content.Span);
        CpAnswerTableDto dto = new CpAnswerTableDto
        {
            Format = "cyac.copy-protection-answers/1",
            About =
                "The copy-protection answer table. copy_protection_prompt @image@0x2529C draws a " +
                "challenge with prng_rand_bounded(0x66) and compares the typed answer " +
                "case-insensitively; three wrong answers exit the game. `index` is the drawn " +
                "number r — question r is string 2r+1 of the asset and its answer 2r+2.",
            Source = $"{source.OriginFile}/{source.Name}",
            Role = ManifestRole,
            PadNuls = table.PadNulCount,
            FailureMessage = table.FailureMessage,
            Challenges =
            [
                .. table.Challenges.Select(c => new CpChallengeDto
                {
                    Index = c.Index,
                    Question = c.Question,
                    Answer = c.Answer,
                }),
            ],
        };

        string path = context.Allocate(OutputPath);
        return
        [
            new TransformOutput(
                path,
                JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.CpAnswerTableDto),
                OutputRole.Data,
                OutputFidelity.Exact,
                $"role: {ManifestRole}; {table.Challenges.Count} challenges"),
        ];
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput json = outputs.SingleOrDefault(o => o.Role == OutputRole.Data)
                            ?? throw new InvalidDataException("strings2 expects exactly one data output");
        return ToBytes(json.Bytes);
    }

    /// <summary>Rebuilds <c>strings2.bin</c>'s decompressed body from the tree's JSON.</summary>
    /// <param name="json">The <c>strings2.json</c> bytes.</param>
    /// <exception cref="InvalidDataException">The document is malformed.</exception>
    public static byte[] ToBytes(ReadOnlySpan<byte> json)
    {
        CpAnswerTableDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.CpAnswerTableDto)
                               ?? throw new InvalidDataException("strings2.json is empty");
        List<CpChallengeDto> challenges = dto.Challenges
                                          ?? throw new InvalidDataException("strings2.json has no \"challenges\"");

        return CpAnswerTableCodec.ToBytes(new CpAnswerTable(
            dto.FailureMessage ?? string.Empty,
            [
                .. challenges.Select(c => new CpChallenge(
                    c.Index, c.Question ?? string.Empty, c.Answer ?? string.Empty)),
            ],
            dto.PadNuls));
    }
}
