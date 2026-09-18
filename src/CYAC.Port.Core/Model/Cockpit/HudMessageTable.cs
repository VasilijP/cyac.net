using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Model.Cockpit;

/// <summary>
/// The HUD MESSAGE STRIP's string table: the damage and envelope warnings the kernel posts through
/// <c>show_cockpit_text_string @image@0x0CC5B</c>.
/// </summary>
/// <remarks>
/// <para>
/// The kernel's seam <c>IKernelWorld.PostHudWarning(messageId, messageTable)</c> is the original's
/// two pushes: a byte OFFSET in <c>AX</c> and the table's far segment in <c>CX</c>, always the
/// literal <c>0x453D</c> at the flight sites (<c>image@0x2B5C6</c>, <c>image@0x2B62A</c>).  In the
/// L1 image's own numbering that segment is <c>0x353D</c> — the base is
/// <see cref="TableImageOffset"/> — and the run of NUL-terminated strings there is the
/// <c>str_mentor_and_damage</c> zone of <c>exe/strings.json</c>, so the port reads it out of the
/// DOCUMENT and never out of the image copy (H6b's law).
/// </para>
/// <para>
/// The four ids the integer flight kernel already posts are <c>0x262</c> "THRUST LIMIT",
/// <c>0x26F</c> "EXCEEDING MAXIMUM SPEED", <c>0x287</c> "WING STRUCTURE FAILING" and <c>0x29E</c>
/// "WINGS RIPPED OFF"; the other thirty-odd are the damage messages
/// <c>weapon_fire_combat_loop @image@0x0F748</c> posts.
/// </para>
/// <para>
/// <c>hud_message_append @image@0x0CBFC</c> then copies at most <see cref="MaxMessageBytes"/> bytes
/// into <c>[0xBA38]</c>, centres it as <c>x = (0x50 − len)·2</c>, and stamps the expiry
/// <c>[0xBA32:34] = g_frame_time_accum + (duration &lt;&lt; 8)</c> — the duration being <b>6</b> for
/// <c>show_cockpit_text_string</c> and 1 for its short-lived sibling at <c>image@0x0CC70</c>.
/// </para>
/// </remarks>
public sealed class HudMessageTable
{
    private readonly IReadOnlyDictionary<int, string> _byId;

    private HudMessageTable(IReadOnlyDictionary<int, string> byId) => _byId = byId;

    /// <summary>
    /// The table's base in the L1 image — run-time segment <c>0x453D</c>, image segment
    /// <c>0x353D</c>.
    /// </summary>
    public const int TableImageOffset = 0x353D0;

    /// <summary>The longest message the strip copies — <c>mov cx, 0x46</c> at <c>image@0x0CC13</c>.</summary>
    public const int MaxMessageBytes = 0x46;

    /// <summary>
    /// How long a posted message stays up, in frame-time accumulator units:
    /// <c>show_cockpit_text_string</c> passes <c>AX = 6</c> and the appender shifts it left 8
    /// (<c>image@0x0CC64</c>, <c>image@0x0CC3F..0x0CC4C</c>).
    /// </summary>
    public const int MessageTicks = 6 << 8;

    /// <summary>How many messages the table resolved.</summary>
    public int Count => _byId.Count;

    /// <summary>Reads the table out of the string catalogue.</summary>
    /// <param name="catalog">The <c>exe/strings.json</c> document.</param>
    public static HudMessageTable From(ExeStringCatalogDto catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Dictionary<int, string> byId = new Dictionary<int, string>();
        foreach (ExeStringZoneDto zone in catalog.Zones ?? [])
        {
            foreach (ExeStringDto entry in zone.Strings ?? [])
            {
                if (entry.Image is not { Length: > 0 } image || entry.Text is not { } text)
                {
                    continue;
                }

                int offset = PortHex.Parse(image) - TableImageOffset;
                if (offset >= 0 && offset < 0x1000)
                {
                    byId[offset] = text;
                }
            }
        }

        return new HudMessageTable(byId);
    }

    /// <summary>The message a posted id names, or null when the table does not carry it.</summary>
    /// <param name="messageId">The offset the kernel pushed in <c>AX</c>.</param>
    public string? Text(int messageId) =>
        _byId.TryGetValue(messageId, out string? text) ? text : null;
}
