using CYAC.Port.Core.Data;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host;

/// <summary>
/// The in-flight advisor's twenty-one messages, resolved once out of the data tree.
/// </summary>
/// <remarks>
/// <para>
/// <c>ai_advisor_message_dispatch @image@0x0EFF1</c> dispatches an action code 0..20 through a
/// CS-relative jump table, and each handler writes a far pointer pair into <c>[0xBCBA]/[0xBCBC]</c>
/// (line 1) and <c>[0xBCBE]/[0xBCC0]</c> (line 2).
/// </para>
/// <para>
/// <b>Everything here comes from the tree</b>.  Where each handler points its lines is
/// <c>exe/tables/advisor.json</c>, which <c>cyac-transform</c> reads out of the handlers; the text is
/// <c>exe/strings.json</c>; this class joins the two by image offset.  What it keeps is code: the
/// branch logic of the one handler that chooses its first line.
/// </para>
/// </remarks>
public sealed class AdvisorMessages
{
    /// <summary>
    /// The action code whose handler chooses between first lines: code 16, the speed-reduction
    /// advisory (<c>image@0x0F203</c>).
    /// </summary>
    public const int SpeedReductionCode = 16;

    /// <summary>How many first lines code 16's branches choose between.</summary>
    public const int SpeedReductionVariantCount = 3;

    /// <summary>Code 16's first branch: the variant for a low aircraft index with the flaps bit clear.</summary>
    private const int FlapsVariant = 0;

    /// <summary>Code 16's second branch: the variant for a high aircraft index with the brake bit clear.</summary>
    private const int BrakesVariant = 1;

    /// <summary>Code 16's fall-back: the variant both branches reach when their bit is already set.</summary>
    private const int FallbackVariant = 2;

    /// <summary><c>cmp [0xC31A],1 / jg</c> (<c>image@0x0F203</c>): the highest index the first branch takes.</summary>
    private const int FlapsBranchMaxAircraftIndex = 1;

    /// <summary><c>test byte [0xF0BC],2</c> (<c>image@0x0F20A</c>): the flaps bit.</summary>
    private const int FlapsBit = 0x02;

    /// <summary><c>test byte [0xF0BC],8</c> (<c>image@0x0F226</c>): the brake bit.</summary>
    private const int BrakesBit = 0x08;

    private readonly string[] _line1;
    private readonly string[] _line2;
    private readonly string[] _speedReduction = new string[SpeedReductionVariantCount];

    /// <summary>Reads the twenty-one message pairs out of an opened data tree.</summary>
    /// <param name="tree">The tree.</param>
    /// <exception cref="DataTreeNotFoundException">The tree has no <c>exe/tables/advisor.json</c>.</exception>
    /// <exception cref="InvalidDataException">
    /// The table is malformed, does not have the shape this class reads, or points at a string
    /// <c>exe/strings.json</c> does not have.
    /// </exception>
    public AdvisorMessages(DataTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        AdvisorTableDto table = tree.Advisor;
        Dictionary<int, string> texts = TextsByImage(tree.Strings);
        List<AdvisorCodeDto>? codes = table.Codes!;
        if (codes.Count != AdvisorTiming.CodeCount)
        {
            throw Malformed(
                $"it lists {codes.Count} codes; the dispatcher handles {AdvisorTiming.CodeCount}");
        }

        _line1 = new string[codes.Count];
        _line2 = new string[codes.Count];
        string? banner = null;
        foreach (AdvisorCodeDto code in codes)
        {
            int index = code.Code;
            if (code.Variants is { } variants)
            {
                if (index != SpeedReductionCode)
                {
                    throw Malformed(
                        $"code {index} chooses between first lines; only code {SpeedReductionCode}'s choice is known");
                }

                if (variants.Count != SpeedReductionVariantCount)
                {
                    throw Malformed(
                        $"code {index} lists {variants.Count} first lines; its branches choose between "
                            + $"{SpeedReductionVariantCount}");
                }

                for (int v = 0; v < variants.Count; v++)
                {
                    _speedReduction[v] = Text(texts, variants[v], $"code {index} variant {v}");
                }

                _line1[index] = _speedReduction[FlapsVariant];
            }
            else if (index == SpeedReductionCode)
            {
                throw Malformed($"code {index} lists a single first line; its branches choose between three");
            }
            else
            {
                _line1[index] = Text(texts, code.Line1!, $"code {index} line 1");
            }

            _line2[index] = code.Line2 is { } line2
                ? Text(texts, line2, $"code {index} line 2")
                : string.Empty;

            if (code.Banner is { } pushed)
            {
                if (banner is not null)
                {
                    throw Malformed($"code {index} pushes a second banner; one was expected");
                }

                banner = Text(texts, pushed, $"code {index} banner");
            }
        }

        Banner = banner ?? throw Malformed("no code pushes a banner; one was expected");
    }

    /// <summary>
    /// The full-screen banner code 18 shows INSTEAD of the window when the Yeager bit is clear
    /// (<c>test byte [0xF1CB],8 / jne</c> before the push).  The port does not draw it yet; it is read
    /// so the finding does not have to be re-derived.
    /// </summary>
    public string Banner { get; }

    /// <summary>How many codes resolved to a non-empty first line — a test's proof the tree was read.</summary>
    public int ResolvedCount => _line1.Count(static s => s.Length > 0);

    /// <summary>The two lines an action code shows.</summary>
    /// <param name="code">The action code, 0..20.</param>
    /// <param name="aircraftIndex">
    /// <c>g_active_aircraft_idx [0xC31A]</c> — code 16's first branch
    /// (<c>cmp [0xc31a],1 / jg</c>, <c>image@0x0F203</c>).
    /// </param>
    /// <param name="statusFlags">
    /// <c>g_input_state_bitfield [0xF0BC]</c> — bit 1 (flaps) and bit 3 (airbrakes), code 16's other
    /// two branches (<c>image@0x0F20A</c>, <c>image@0x0F226</c>).
    /// </param>
    /// <returns>The upper and lower lines; the lower is empty for a single-line message.</returns>
    /// <remarks>
    /// Code 16 is the only conditional message, and its truth table is a 2 × 2 rather than the
    /// three-way the decoded file reads: <c>idx &lt;= 1</c> with the FLAPS bit already set falls
    /// through <c>image@0x0F21F</c>'s redundant re-test to the fall-back variant, and so does <c>idx
    /// &gt; 1</c> with the BRAKE bit set.
    /// </remarks>
    public (string Line1, string Line2) Lines(int code, int aircraftIndex, int statusFlags)
    {
        if ((uint)code >= (uint)_line1.Length)
        {
            return (string.Empty, string.Empty);
        }

        if (code != SpeedReductionCode)
        {
            return (_line1[code], _line2[code]);
        }

        int variant =
            aircraftIndex <= FlapsBranchMaxAircraftIndex
                ? ((statusFlags & FlapsBit) == 0 ? FlapsVariant : FallbackVariant)     // image@0x0F20A
                : (statusFlags & BrakesBit) == 0 ? BrakesVariant : FallbackVariant;   // image@0x0F226
        return (_speedReduction[variant], _line2[SpeedReductionCode]);
    }

    /// <summary>Every string of the tree's executable string catalogue, by image offset.</summary>
    private static Dictionary<int, string> TextsByImage(ExeStringCatalogDto strings)
    {
        Dictionary<int, string> texts = new Dictionary<int, string>();
        foreach (ExeStringZoneDto zone in strings.Zones ?? [])
        {
            foreach (ExeStringDto literal in zone.Strings ?? [])
            {
                if (literal.Image is { } at && literal.Text is { } text)
                {
                    texts.TryAdd(PortHex.Parse(at), text);
                }
            }
        }

        return texts;
    }

    private static string Text(Dictionary<int, string> texts, AdvisorPointerDto pointer, string what)
    {
        int image = PortHex.Parse(pointer.Image);
        return texts.TryGetValue(image, out string? text)
            ? text
            : throw Malformed($"{what} points at image@0x{image:X5}, where exe/strings.json has no string");
    }

    private static InvalidDataException Malformed(string problem) =>
        new($"{AdvisorTableDto.DataPath} cannot drive the advisor: {problem}");
}
