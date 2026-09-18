using System.Diagnostics;
using System.Globalization;
using CYAC.Port.Core.Model.Cockpit;
using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Sim;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Effects;
using CYAC.Port.Core.Sim.Combat.Lifecycle;
using CYAC.Port.Core.Sim.Combat.Player;
using CYAC.Port.Core.Sim.Flight;
using CYAC.Port.Core.Sim.Session;
using CYAC.Port.Host.Configuration;
using CYAC.Port.Host.Headless;
using CYAC.Port.Host.Input;
using CYAC.Port.Host.Menu;
using CYAC.Port.Host.Sound;
using CYAC.Port.Host.Settings;
using CYAC.Port.Host.Sim;
using CYAC.Port.Host.Stats;
using CYAC.Port.Render;
using CYAC.Port.Render.Cockpit;
using CYAC.Port.Render.Ground;
using CYAC.Port.Render.Map;
using mode13hx;
using mode13hx.Controls;
using mode13hx.Presentation;
using mode13hx.Util;

namespace CYAC.Port.Host;

/// <summary>
/// The option WORDS: the static parsers that turn a command-line word into the enum the
/// constructor stores.  None of them touches instance state.
/// </summary>
/// <remarks>The reader's map of every one of this class's files is at the top of
/// <c>FlightRasterizer.cs</c>.</remarks>
public sealed partial class FlightRasterizer
{
    /// <summary>Parses the <c>--markings</c> word.</summary>
    /// <param name="word">shipped | vector | off.</param>
    public static MarkingsMode ParseMarkings(string? word) => (word ?? "vector").Trim().ToLowerInvariant() switch
    {
        "shipped" => MarkingsMode.Shipped,
        "off" => MarkingsMode.Off,
        _ => MarkingsMode.Vector,
    };

    /// <summary>
    /// Reads <c>--camera x,y,z[,heading[,pitch[,roll]]]</c> — a fixed INSPECTION camera.
    /// </summary>
    /// <param name="value">The command-line value; null or empty leaves the aircraft's camera.</param>
    /// <exception cref="ArgumentException">The value is not three to six numbers.</exception>
    private static CameraPose? ParseCamera(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 3 or > 6)
        {
            throw new ArgumentException(
                $"--camera wants 'x,y,z[,heading[,pitch[,roll]]]', got '{value}'", nameof(value));
        }

        double[] numbers = new double[6];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
            {
                throw new ArgumentException($"--camera: '{parts[i]}' is not a number", nameof(value));
            }
        }

        return new CameraPose(
            numbers[0],
            numbers[1],
            numbers[2],
            numbers[3] * Math.PI / 180.0,
            numbers[4] * Math.PI / 180.0,
            numbers[5] * Math.PI / 180.0);
    }

    /// <summary>Reads the <c>--horizon</c> word.</summary>
    /// <param name="word">The command-line value.</param>
    internal static HorizonStyle ParseHorizon(string? word) => word?.ToLowerInvariant() switch
    {
        "flat" or "hard" or "off" => HorizonStyle.Flat,
        "classic" or "stepped" => HorizonStyle.Classic,
        _ => HorizonStyle.Refined,
    };

    /// <summary>Reads the <c>--alpha</c> word.</summary>
    /// <param name="word">The command-line value.</param>
    /// <remarks>
    /// <c>on|off|dither</c>; <c>stipple</c> and <c>classic</c> stay as aliases of
    /// <c>dither</c> because scripts type them.
    /// </remarks>
    internal static AlphaMode ParseAlpha(string? word) => word?.ToLowerInvariant() switch
    {
        "off" or "opaque" or "solid" => AlphaMode.Off,
        "dither" or "stipple" or "classic" or "retro" => AlphaMode.Dither,
        _ => AlphaMode.Blend,
    };

    /// <summary>Reads the <c>--edges</c> word.</summary>
    /// <param name="text">The option's text.</param>
    /// <exception cref="ArgumentException">It names neither edge rule.</exception>
    internal static EdgeMode ParseEdges(string? text) =>
        (text ?? "analytic").Trim().ToLowerInvariant() switch
        {
            "analytic" or "exact" or "aa" or "on" => EdgeMode.Analytic,
            "hard" or "classic" or "centre" or "center" or "off" => EdgeMode.Hard,
            var other => throw new ArgumentException(
                $"--edges: '{other}' is neither 'analytic' nor 'hard'", nameof(text)),
        };

    /// <summary>Reads the <c>--lod</c> word.</summary>
    /// <param name="word">The command-line value.</param>
    internal static LodPolicy ParseLod(string? word) => word?.ToLowerInvariant() switch
    {
        "classic" or "original" or "distance" => LodPolicy.Classic,
        _ => LodPolicy.Max,
    };

    /// <summary><c>--mask-view off|silhouette|interior</c>.</summary>
    /// <param name="word">The word; unknown or null reads as off.</param>
    public static MaskView ParseMaskView(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "silhouette" or "sil" or "binary" => MaskView.Silhouette,
        "interior" or "classify" => MaskView.Interior,
        _ => MaskView.Off,
    };

    /// <summary><c>--wireframe off|overlay|only</c>.</summary>
    /// <param name="word">The word; unknown or null reads as off.</param>
    public static WireframeMode ParseWireframe(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "overlay" or "over" or "on" => WireframeMode.Overlay,
        "only" or "lines" => WireframeMode.Only,
        _ => WireframeMode.Off,
    };

    /// <summary>
    /// The inverse of <see cref="ParseWindows"/>: a mask as the <c>--windows</c> word
    /// (<c>off</c>, <c>all</c> or the comma list), so the settings file can carry the four
    /// window rows in the option's own vocabulary.
    /// </summary>
    /// <param name="mask">The overlay mask.</param>
    /// <returns>The word.</returns>
    internal static string ComposeWindows(CockpitOverlayFlags mask)
    {
        const CockpitOverlayFlags All = CockpitOverlayFlags.Envelope | CockpitOverlayFlags.Target
            | CockpitOverlayFlags.Map | CockpitOverlayFlags.Yeager;
        mask &= All;
        if (mask == CockpitOverlayFlags.None)
        {
            return "off";
        }

        if (mask == All)
        {
            return "all";
        }

        List<string> parts = new List<string>(4);
        if (mask.HasFlag(CockpitOverlayFlags.Envelope)) parts.Add("envelope");
        if (mask.HasFlag(CockpitOverlayFlags.Target)) parts.Add("target");
        if (mask.HasFlag(CockpitOverlayFlags.Map)) parts.Add("map");
        if (mask.HasFlag(CockpitOverlayFlags.Yeager)) parts.Add("yeager");
        return string.Join(',', parts);
    }

    internal static CockpitOverlayFlags ParseWindows(string? word, CockpitOverlayFlags fromConfig)
    {
        string text = word?.Trim() ?? string.Empty;
        if (text.Length == 0 || string.Equals(text, "cfg", StringComparison.OrdinalIgnoreCase))
        {
            return fromConfig;
        }

        if (string.Equals(text, "off", StringComparison.OrdinalIgnoreCase))
        {
            return CockpitOverlayFlags.None;
        }

        if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
        {
            return CockpitOverlayFlags.Envelope | CockpitOverlayFlags.Target
                | CockpitOverlayFlags.Map | CockpitOverlayFlags.Yeager;
        }

        CockpitOverlayFlags mask = CockpitOverlayFlags.None;
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            mask |= part.ToLowerInvariant() switch
            {
                "envelope" => CockpitOverlayFlags.Envelope,
                "target" => CockpitOverlayFlags.Target,
                "map" => CockpitOverlayFlags.Map,
                "yeager" => CockpitOverlayFlags.Yeager,
                _ => throw new FormatException(
                    $"--windows names unknown window '{part}' "
                        + "(expected cfg|off|all or a list of envelope,target,map,yeager)"),
            };
        }

        return mask;
    }

    private static (double X, double Y) ParseNudge(string? text) => ParsePair(text, "--needle-nudge", 0.5, 0.5);

    /// <summary>A comma-separated pair of numbers with a default.</summary>
    private static (double X, double Y) ParsePair(string? text, string option, double defaultX, double defaultY)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (defaultX, defaultY);
        }

        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
        {
            return (x, y);
        }

        throw new ArgumentException($"{option} expects 'a,b', got '{text}'.");
    }
}
