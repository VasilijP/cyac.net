using System.Text.Json;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// <c>yeager.cfg</c> → <c>config.json</c>: the game's entire persistent state, 86 bytes, made
/// readable and editable.
/// </summary>
/// <remarks>
/// <para>
/// The layout is <c>CYAC.Port.Core.Model.Mission.GameConfig</c>'s — itself
/// KNOWN_FIELDS["YeagerCfg"]</c> as exported into <c>Schema/state_schema.json</c>, with the
/// semantics.  This transform adds no layout knowledge of its own: it names the fields for JSON and
/// hands every byte back through <see cref="GameConfig"/>'s own property setters, so an offset
/// lives in exactly one place in the port.
/// </para>
/// <para>
/// The file is a <b>save</b>, not a distribution file: the shipped copy is a completed campaign
/// (all 50 progression bytes are 2), which is why the manifest gives it role
/// <c>save</c> and never matches it against a digest.
/// </para>
/// </remarks>
public sealed class ConfigTransform : IFamilyTransform
{
    /// <summary>The output path of the transformed config.</summary>
    public const string OutputPath = "config.json";

    /// <inheritdoc/>
    public string Family => "config";

    /// <inheritdoc/>
    public string TreeDescription =>
        "`config.json` — yeager.cfg: video/input/audio selection, joystick calibration, the display " +
        "and cheat toggles, and the 50-slot campaign progression.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source) =>
        source.EntryIndex is null &&
        string.Equals(source.OriginFile, Input.KnownDistributions.ConfigName, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        GameConfig config = GameConfig.Parse(source.Content.Span);
        return [TransformOutput.Data(context.Allocate(OutputPath), Serialise(config))];
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        if (outputs.Count != 1)
        {
            throw new InvalidDataException($"config expects exactly one data output, got {outputs.Count}");
        }

        return Deserialise(outputs[0].Bytes).ToBytes();
    }

    /// <summary>Renders a config as the tree's JSON document.</summary>
    /// <param name="config">The parsed config.</param>
    public static byte[] Serialise(GameConfig config) => GameConfigCodec.Serialise(config);

    /// <summary>Reads the tree's JSON document back into a config.</summary>
    /// <param name="json">The <c>config.json</c> bytes.</param>
    /// <exception cref="InvalidDataException">The document is malformed or incomplete.</exception>
    public static GameConfig Deserialise(ReadOnlySpan<byte> json) => GameConfigCodec.Deserialise(json);
}
