using System.Reflection;
using CommandLine;

namespace CYAC.Port.Host.Configuration;

/// <summary>
/// Which part of the program a command-line option belongs to.
/// </summary>
/// <remarks>
/// The first four are the PLAYER's groups: <c>--help</c> prints those and nothing else.  The last
/// two are what the port is developed and measured with, and are behind <c>--help-all</c>.
/// </remarks>
public enum OptionGroup
{
    /// <summary>Where the original game and the port's own files are, and the startup checks.</summary>
    Startup,

    /// <summary>Which sortie is flown, and how it behaves.</summary>
    Flying,

    /// <summary>The window, the view, the cockpit and what is drawn over it.</summary>
    Display,

    /// <summary>The sound path and its device.</summary>
    Sound,

    /// <summary>The headless driver and the census instruments.</summary>
    Instruments,

    /// <summary>The renderer's knobs and the A/B switches a port change is measured with.</summary>
    Developer,
}

/// <summary>
/// The one table that says which <see cref="OptionGroup"/> every option belongs to.
/// </summary>
/// <remarks>
/// <para>
/// CommandLineParser has no notion of a group, so the port keeps its own table rather than an
/// attribute on each of the 196 options: one file says what a player needs and what a developer
/// needs, and it can be read end to end.  <c>OptionGroupTests</c> holds it exhaustive — every
/// option of <see cref="FlyOptions"/> and of the base it extends appears exactly once, and no
/// name in the table is missing from the options.
/// </para>
/// <para>
/// The order inside a group is the order <c>--help</c> prints it in, so it is curated for the
/// player's groups and left in declaration order for the other two.
/// </para>
/// </remarks>
public static class OptionGroups
{
    /// <summary>The groups <c>--help</c> prints; the other two need <c>--help-all</c>.</summary>
    public static readonly IReadOnlyList<OptionGroup> Player =
        [OptionGroup.Startup, OptionGroup.Flying, OptionGroup.Display, OptionGroup.Sound];

    /// <summary>Every group, in the order <c>--help-all</c> prints them.</summary>
    public static readonly IReadOnlyList<OptionGroup> All =
        [OptionGroup.Startup, OptionGroup.Flying, OptionGroup.Display, OptionGroup.Sound,
         OptionGroup.Instruments, OptionGroup.Developer];

    /// <summary>The <see cref="OptionGroup.Startup"/> options, in the order <c>--help</c> prints them.</summary>
    private static readonly string[] StartupNames =
    [
        "home", "game", "data", "settings", "stats", "preflight", "rebuild", "startup-check",
    ];

    /// <summary>The <see cref="OptionGroup.Flying"/> options, in the order <c>--help</c> prints them.</summary>
    private static readonly string[] FlyingNames =
    [
        "mission", "custom", "test-flight", "site", "difficulty", "respawn", "stick-latch",
        "debrief", "fate", "ai-admitter-cold-start",
    ];

    /// <summary>The <see cref="OptionGroup.Display"/> options, in the order <c>--help</c> prints them.</summary>
    private static readonly string[] DisplayNames =
    [
        "width", "height", "windowed", "no-vsync", "gfx", "view", "fov", "cockpit", "cockpit-fit",
        "flight-info", "windows", "hud-style", "map", "clouds", "markings",
    ];

    /// <summary>The <see cref="OptionGroup.Sound"/> options, in the order <c>--help</c> prints them.</summary>
    private static readonly string[] SoundNames =
    [
        "sound", "audio-output", "sound-volume",
    ];

    /// <summary>The <see cref="OptionGroup.Instruments"/> options, in the order <c>--help</c> prints them.</summary>
    private static readonly string[] InstrumentsNames =
    [
        "pursue", "shot", "shot-at", "seed-trace", "seed-step", "headless", "frame-count",
        "save-every", "out", "cold-start", "replay", "frame-seconds", "script", "effect-probe",
        "effect-probe-range", "effect-probe-count", "mission-kills", "debrief-at", "mask-view",
        "shot-dir", "render-scene", "foe-hp", "kill-burst", "target-cycle", "projection-census",
        "projection-from", "instrument-census", "pose-census", "ai-census", "kill-census",
        "effect-age", "effect-fork", "eject-at", "player-death-at", "readout", "no-readout", "gear-angle",
        "camera", "camera-step", "near-census", "ground-census", "line-census", "pixel-census",
        "census-rect", "crop", "crop-scale", "hud-demo", "frontend", "settings-census",
        "stats-census", "menu-wiring", "wav", "wav-seconds", "tone-log",
    ];

    /// <summary>The <see cref="OptionGroup.Developer"/> options, in the order <c>--help</c> prints them.</summary>
    private static readonly string[] DeveloperNames =
    [
        "fullscreen", "vsync", "dofps", "frames", "drop-frames", "fast", "no-fast", "frame-compression", "compressor",
        "compression-threads", "no-scenery", "chase-distance", "chase-elevation", "draw-distance",
        "classic-cull", "alpha", "edges", "lod", "cloud-tiles", "ground-balls",
        "ground-ball-tiles", "hard-effects", "smoke-growth", "smoke-size", "smoke-trail",
        "smoke-life", "smoke-ramp", "smoke-fade", "wreck-smoke", "wreck-smoke-interval",
        "wreck-smoke-color", "wreck-smoke-size", "nav-readout", "map-zoom", "map-scenery",
        "map-units", "map-rings", "map-blink", "map-labels", "map-hud", "smoke-density",
        "seam-mask", "backface-cull", "wireframe", "wire-color", "wire-width", "weld",
        "face-colors", "face-color-seed", "bitmap-explosions", "no-effect-debris",
        "designator-labels", "designator-label-scale", "designator-opacity", "window-scale",
        "no-player-mesh", "fate-hold", "no-sun", "horizon", "horizon-band", "hit-marker-alpha",
        "needle-nudge", "needles", "instrument-window", "hud-stroke", "decal-lift",
        "no-sheet-inflate", "sheet-thickness", "markings-dir", "tracer-floor", "window-envelope",
        "window-target", "window-map", "window-yeager", "smooth-objects", "ai-sleep-cap",
        "wreck-mesh", "no-gear-animation", "tree-orphans", "no-ejection-parts", "near-exempt",
        "no-aa", "tile", "threads", "line-width", "line-floor", "line-thread", "tracer-halo",
        "tracer-halo-radius", "tracer-halo-alpha", "tracer-halo-floor", "tracer-glow",
        "tracer-color", "rounds", "round-length", "round-width", "round-floor", "round-color",
        "round-hit-scale", "round-ground-hits", "round-spacing", "cockpit-view", "cockpit-filter",
        "sound-mask", "menu-opacity", "menu-scale", "frontend-scale", "engine-views",
        "sound-sources", "sound-radius", "engine-lowpass", "audio-reset",
    ];


    /// <summary>The group's heading.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Its one-word title.</returns>
    public static string Title(OptionGroup group) => group switch
    {
        OptionGroup.Startup => "STARTUP",
        OptionGroup.Flying => "FLYING",
        OptionGroup.Display => "DISPLAY",
        OptionGroup.Sound => "SOUND",
        OptionGroup.Instruments => "INSTRUMENTS",
        OptionGroup.Developer => "DEVELOPER",
        _ => group.ToString().ToUpperInvariant(),
    };

    /// <summary>What the group is for, printed under its heading.</summary>
    /// <param name="group">The group.</param>
    /// <returns>One sentence.</returns>
    public static string Blurb(OptionGroup group) => group switch
    {
        OptionGroup.Startup =>
            "where your copy of the original game is, where the port keeps its files, and the " +
            "checks it runs before it flies",
        OptionGroup.Flying =>
            "which sortie you fly and how the aircraft and the mission behave",
        OptionGroup.Display =>
            "the window, the view, the cockpit and what is drawn over it",
        OptionGroup.Sound =>
            "the sound path and the device it plays through",
        OptionGroup.Instruments =>
            "the headless driver and the census instruments: render frames without a window, " +
            "drive a scripted or recorded sortie, and print what the run measured",
        OptionGroup.Developer =>
            "the renderer, the asset conditioning and the A/B switches behind them - the knobs a " +
            "port change is measured with",
        _ => string.Empty,
    };

    /// <summary>The long names in a group, in the order they are printed.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The group's option names.</returns>
    public static IReadOnlyList<string> Names(OptionGroup group) => group switch
    {
        OptionGroup.Startup => StartupNames,
        OptionGroup.Flying => FlyingNames,
        OptionGroup.Display => DisplayNames,
        OptionGroup.Sound => SoundNames,
        OptionGroup.Instruments => InstrumentsNames,
        OptionGroup.Developer => DeveloperNames,
        _ => [],
    };

    /// <summary>Every option the program takes, keyed by its long name.</summary>
    /// <remarks>
    /// <see cref="FlyOptions"/>' own options first, then the ones it inherits — the order
    /// CommandLineParser's own help screen used before the groups existed.
    /// </remarks>
    public static IReadOnlyDictionary<string, OptionAttribute> Options { get; } = Enumerate();

    /// <summary>Which group an option belongs to.</summary>
    /// <param name="longName">The option's long name, without the dashes.</param>
    /// <returns>Its group.</returns>
    /// <exception cref="KeyNotFoundException">The option is in no group — add it to one.</exception>
    public static OptionGroup Of(string longName) => Table.TryGetValue(longName, out OptionGroup group)
        ? group
        : throw new KeyNotFoundException(
            $"--{longName} belongs to no option group; add it to one in OptionGroups.");

    /// <summary>How many options a set of groups holds.</summary>
    /// <param name="groups">The groups to count.</param>
    /// <returns>The total.</returns>
    public static int Count(IEnumerable<OptionGroup> groups) =>
        (groups ?? []).Sum(g => Names(g).Count);

    private static readonly Dictionary<string, OptionGroup> Table = BuildTable();

    private static Dictionary<string, OptionGroup> BuildTable()
    {
        Dictionary<string, OptionGroup> table = new Dictionary<string, OptionGroup>(StringComparer.Ordinal);
        foreach (OptionGroup group in All)
        {
            foreach (string name in Names(group))
            {
                table.Add(name, group);
            }
        }

        return table;
    }

    private static Dictionary<string, OptionAttribute> Enumerate()
    {
        Dictionary<string, OptionAttribute> found = new Dictionary<string, OptionAttribute>(StringComparer.Ordinal);
        for (Type? type = typeof(FlyOptions); type is not null; type = type.BaseType)
        {
            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (property.GetCustomAttribute<OptionAttribute>() is { } option
                    && !string.IsNullOrEmpty(option.LongName))
                {
                    found.TryAdd(option.LongName, option);
                }
            }
        }

        return found;
    }
}
