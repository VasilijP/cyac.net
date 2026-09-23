using System.Globalization;
using System.Reflection;
using CommandLine;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Host.Configuration;
using CYAC.Port.Render;
using CYAC.Port.Render.Cockpit;
using CYAC.Port.Render.Ground;

namespace CYAC.Port.Host.Settings;

/// <summary>
/// THE TABLE.  Every port setting, once: its command-line twin, its dialog row, its
/// <c>settings.json</c> entry and its live effect.
/// </summary>
/// <remarks>
/// <para>
/// ONE table drives both the command-line options and the dialog rows, so the two can never
/// diverge.  Nothing else in the host may decide what a setting's default, range or
/// help text is: <see cref="PortSettingsStore"/> reads this table to seed itself, the dialog reads it
/// to build its rows, the file writer reads it to write the <c>$comment</c>s, and
/// <see cref="CommandLineTwins"/> proves by REFLECTION that every entry really has a
/// <c>[Option]</c> of the same name and the same default on <see cref="FlyOptions"/>.
/// </para>
/// <para>
/// <b>What is NOT here.</b> Anything that chooses the sortie (mission, aircraft, site, difficulty),
/// anything headless (frame counts, censuses, probes), and every labelled CHEAT: a settings file is
/// for the way the game LOOKS and SOUNDS, and a persisted cheat would be a trap.  Missions stay open
/// and no progression is stored.
/// </para>
/// </remarks>
public static class PortSettings
{
    /// <summary>The dialog's sections, in the order they are drawn.</summary>
    public static readonly IReadOnlyList<string> Sections =
        ["Look", "Effects", "Cockpit & HUD", "Sound", "Menu", "Developer"];

    /// <summary>The five bits of <c>g_audio_mute_mask [0xE483]</c>, low bit first.</summary>
    public static readonly IReadOnlyList<string> SoundBits =
        ["Sound", "Engine", "Radar Warning", "Stall Warning", "Lock Sounds"];

    private static readonly PortSetting[] Table = Build();

    /// <summary>Every setting, in dialog order.</summary>
    public static IReadOnlyList<PortSetting> All => Table;

    /// <summary>One setting by its command-line name, or null.</summary>
    /// <param name="name">The long option name, e.g. <c>alpha</c>.</param>
    public static PortSetting? Find(string name) =>
        Table.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The REFLECTION twin check: every table entry against its <c>[Option]</c> on
    /// <see cref="FlyOptions"/>.
    /// </summary>
    /// <returns>
    /// One line per DEFECT — a missing twin, or a twin whose <c>Default</c> is not the table's.
    /// Empty means the two halves agree.
    /// </returns>
    /// <remarks>
    /// This is what stops the command line and the dialog drifting.  It is a method rather than a
    /// test-only helper so that the host itself can print it (<c>--settings-check</c>) and a unit
    /// test can assert it is empty.
    /// </remarks>
    public static IReadOnlyList<string> CommandLineTwins()
    {
        List<string> defects = new List<string>();
        Dictionary<string, (PropertyInfo Property, OptionAttribute? Attribute)> options = typeof(FlyOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Property: p, Attribute: p.GetCustomAttribute<OptionAttribute>()))
            .Where(p => p.Attribute is not null)
            .ToDictionary(p => p.Attribute!.LongName, p => p, StringComparer.Ordinal);

        foreach (PortSetting setting in Table)
        {
            if (!options.TryGetValue(setting.Name, out (PropertyInfo Property, OptionAttribute? Attribute) twin))
            {
                defects.Add($"{setting.Name}: no [Option(\"{setting.Name}\")] on FlyOptions");
                continue;
            }

            SettingValue declared = Coerce(setting, twin.Attribute!.Default);
            if (!setting.Same(declared, setting.Default))
            {
                defects.Add(
                    $"{setting.Name}: the table's default is {setting.Format(setting.Default)} but "
                        + $"[Option] says {setting.Format(declared)}");
            }
        }

        return defects;
    }

    /// <summary>
    /// Reads a raw value — a JSON scalar, or an <c>[Option]</c>'s boxed <c>Default</c> — as this
    /// setting's own value.
    /// </summary>
    /// <param name="setting">The setting.</param>
    /// <param name="raw">The boxed value; null gives the setting's default.</param>
    public static SettingValue Coerce(PortSetting setting, object? raw)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (raw is null)
        {
            return setting.Default;
        }

        return setting.Kind switch
        {
            PortSettingKind.Toggle => SettingValue.Toggle(raw switch
            {
                bool flag => flag,
                string word => !string.Equals(word.Trim(), "off", StringComparison.OrdinalIgnoreCase),
                _ => Convert.ToDouble(raw, CultureInfo.InvariantCulture) != 0.0,
            }),
            PortSettingKind.Choice => SettingValue.Choice(
                raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty),
            _ => SettingValue.Of(raw switch
            {
                string text => ParseNumber(text),
                bool flag => flag ? 1.0 : 0.0,
                _ => Convert.ToDouble(raw, CultureInfo.InvariantCulture),
            }),
        };
    }

    /// <summary>Reads a number that may be written in hex (the sound mask's own spelling).</summary>
    /// <param name="text">The text.</param>
    private static double ParseNumber(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex))
        {
            return hex;
        }

        return double.TryParse(
            trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0.0;
    }

    // -------------------------------------------------------------------------------------------
    // Small builders, so the table below reads as a table and not as forty constructor calls.
    // -------------------------------------------------------------------------------------------

    private static PortSetting Toggle(
        string name,
        string section,
        string label,
        bool @default,
        string help,
        Func<FlyOptions, SettingValue> read,
        Action<FlyOptions, SettingValue> write,
        Action<FlightRasterizer, SettingValue>? apply) => new()
        {
            Name = name,
            Section = section,
            Label = label,
            Kind = PortSettingKind.Toggle,
            Help = help,
            Default = SettingValue.Toggle(@default),
            Read = read,
            Write = write,
            Apply = apply,
        };

    /// <summary>
    /// The shipped <c>yeager.cfg@0x1D</c> byte's four bits — TARGET and MAP up
    /// (<c>data/config.json</c>'s <c>overlays</c>, W0) — which is what the four <c>--window-*</c>
    /// words default to.
    /// </summary>
    internal const CockpitOverlayFlags ShippedOverlays = CockpitOverlayFlags.Target | CockpitOverlayFlags.Map;

    /// <summary>The four window rows and the bit each one stands for.</summary>
    internal static IReadOnlyList<(PortSetting Setting, CockpitOverlayFlags Window)> WindowRows =>
        _windowRows ??= All
            .Where(s => s.Name.StartsWith("window-", StringComparison.Ordinal) && s.Kind == PortSettingKind.Toggle)
            .Select(s => (s, WindowOf(s.Name)))
            .ToList();

    private static List<(PortSetting, CockpitOverlayFlags)>? _windowRows;

    private static CockpitOverlayFlags WindowOf(string name) => name switch
    {
        "window-envelope" => CockpitOverlayFlags.Envelope,
        "window-target" => CockpitOverlayFlags.Target,
        "window-map" => CockpitOverlayFlags.Map,
        _ => CockpitOverlayFlags.Yeager,
    };

    /// <summary>
    /// The overlay mask the four <c>--window-*</c> words describe; the rasterizer starts from it
    /// unless an explicit <c>--windows</c> word overrides all four.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <returns>The mask.</returns>
    public static CockpitOverlayFlags WindowsFromWords(FlyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        CockpitOverlayFlags mask = CockpitOverlayFlags.None;
        if (On(options.WindowEnvelope, false)) mask |= CockpitOverlayFlags.Envelope;
        if (On(options.WindowTarget, true)) mask |= CockpitOverlayFlags.Target;
        if (On(options.WindowMap, true)) mask |= CockpitOverlayFlags.Map;
        if (On(options.WindowYeager, false)) mask |= CockpitOverlayFlags.Yeager;
        return mask;

        static bool On(string? word, bool @default) => word is null
            ? @default
            : !string.Equals(word.Trim(), "off", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a <c>--windows</c> word was given explicitly (anything but <c>cfg</c>).</summary>
    private static bool ExplicitWindowsWord(FlyOptions o) =>
        !string.IsNullOrWhiteSpace(o.Windows)
        && !string.Equals(o.Windows.Trim(), "cfg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One overlay window's toggle: a <see cref="Word"/> over its own <c>--window-*</c> option.  An
    /// EXPLICIT <c>--windows</c> word (probes, tests, a scripted capture) speaks for all four rows until
    /// the dialog changes one, which hands the word back to <c>cfg</c> so the four rows rule again.
    /// </summary>
    private static PortSetting WindowRow(
        string name, string label, CockpitOverlayFlags window, bool @default, string help,
        Func<FlyOptions, string?> get, Action<FlyOptions, string> set) => Word(
            name, "Cockpit & HUD", label, @default, help,
            o => ExplicitWindowsWord(o)
                ? (FlightRasterizer.ParseWindows(o.Windows, ShippedOverlays).HasFlag(window) ? "on" : "off")
                : get(o),
            (o, v) =>
            {
                if (ExplicitWindowsWord(o))
                {
                    // Fold the explicit word into the four words first, so the other three keep
                    // what it said, then retire it.
                    CockpitOverlayFlags mask = FlightRasterizer.ParseWindows(o.Windows, ShippedOverlays);
                    o.WindowEnvelope = mask.HasFlag(CockpitOverlayFlags.Envelope) ? "on" : "off";
                    o.WindowTarget = mask.HasFlag(CockpitOverlayFlags.Target) ? "on" : "off";
                    o.WindowMap = mask.HasFlag(CockpitOverlayFlags.Map) ? "on" : "off";
                    o.WindowYeager = mask.HasFlag(CockpitOverlayFlags.Yeager) ? "on" : "off";
                    o.Windows = "cfg";
                }

                set(o, v);
            },
            (r, v) => r.SetOverlay(window, v.Flag)) with
        {
            // These belong where the original puts them, under Help.  The rows stay (they are the
            // persisted state and the option twins) but the port dialog does not list them; the
            // Help menu's own four items drive them.
            Hidden = true,
        };

    /// <summary>A toggle whose <see cref="FlyOptions"/> twin is an <c>on</c>/<c>off</c> STRING.</summary>
    private static PortSetting Word(
        string name,
        string section,
        string label,
        bool @default,
        string help,
        Func<FlyOptions, string?> get,
        Action<FlyOptions, string> set,
        Action<FlightRasterizer, SettingValue>? apply) => Toggle(
            name,
            section,
            label,
            @default,
            help,
            // An UNSET word (a FlyOptions built outside the parser) reads as the row's default,
            // not as "on": the window rows are the first Word rows whose default is off.
            o => SettingValue.Toggle(get(o) is { } word
                ? !string.Equals(word.Trim(), "off", StringComparison.OrdinalIgnoreCase)
                : @default),
            (o, v) => set(o, v.Flag ? "on" : "off"),
            apply);

    private static PortSetting Choice(
        string name,
        string section,
        string label,
        string[] choices,
        string @default,
        string help,
        Func<FlyOptions, string?> get,
        Action<FlyOptions, string> set,
        Action<FlightRasterizer, SettingValue>? apply,
        bool liveWithoutApply = false) => new()
        {
            Name = name,
            Section = section,
            Label = label,
            Kind = PortSettingKind.Choice,
            Choices = choices,
            Help = help,
            Default = SettingValue.Choice(@default),
            Read = o => SettingValue.Choice(get(o) ?? @default),
            Write = (o, v) => set(o, v.Word),
            Apply = apply,
            LiveWithoutApply = liveWithoutApply,
        };

    private static PortSetting Number(
        string name,
        string section,
        string label,
        double @default,
        double minimum,
        double maximum,
        double step,
        int decimals,
        string unit,
        string help,
        Func<FlyOptions, double> get,
        Action<FlyOptions, double> set,
        Action<FlightRasterizer, SettingValue>? apply) => new()
        {
            Name = name,
            Section = section,
            Label = label,
            Kind = PortSettingKind.Number,
            Minimum = minimum,
            Maximum = maximum,
            Step = step,
            Decimals = decimals,
            Unit = unit,
            Help = help,
            Default = SettingValue.Of(@default),
            Read = o => SettingValue.Of(get(o)),
            Write = (o, v) => set(o, v.Number),
            Apply = apply,
        };

    private static PortSetting Colour(
        string name,
        string section,
        string label,
        int @default,
        int minimum,
        int maximum,
        string help,
        Func<FlyOptions, int> get,
        Action<FlyOptions, int> set,
        Action<FlightRasterizer, SettingValue>? apply) => new()
        {
            Name = name,
            Section = section,
            Label = label,
            Kind = PortSettingKind.Color,
            Minimum = minimum,
            Maximum = maximum,
            Step = 1,
            Help = help,
            Default = SettingValue.Of(@default),
            Read = o => SettingValue.Of(get(o)),
            Write = (o, v) => set(o, (int)Math.Round(v.Number)),
            Apply = apply,
        };

    private static LineWidthModel Widths(FlightRasterizer r) =>
        r.SceneOptions.LineWidths ?? LineWidthModel.Default;

    private static TracerHalo Halo(FlightRasterizer r) =>
        r.SceneOptions.Tracer ?? new TracerHalo();

    private static PortSetting[] Build() =>
    [
        // ------------------------------------------------------------------ Look
        Choice(
            "alpha", "Look", "Translucency", ["on", "off", "dither"], "on",
            "How stippled records are composited: ON blends the record's measured coverage, DITHER "
                + "quantises it through an 8x8 ordered matrix at the HOST pixel (the retro look), "
                + "OFF draws them solid.",
            o => o.Alpha, (o, v) => o.Alpha = v,
            (r, v) => r.SceneOptions = r.SceneOptions with
            {
                Alpha = FlightRasterizer.ParseAlpha(v.Word),
            }),
        Choice(
            "edges", "Look", "Polygon edges", ["analytic", "hard"], "analytic",
            "ANALYTIC gives every pixel the exact fraction of it a polygon covers (anti-aliasing "
                + "that is continuous in sub-pixel position); HARD is the 1991 centre sample.",
            o => o.Edges, (o, v) => o.Edges = v,
            (r, v) => r.SceneOptions = r.SceneOptions with
            {
                Edges = FlightRasterizer.ParseEdges(v.Word),
            }),
        Choice(
            "markings", "Look", "Aircraft markings", ["vector", "shipped", "off"], "vector",
            "VECTOR draws the port's own projected insignia, bands and codes (resolution-free, "
                + "chipped; edited in the resource browser's Apply Markings tab), SHIPPED draws the "
                + "1991 polygon markings as authored, OFF draws none. Applies to the aircraft at once.",
            o => o.Markings, (o, v) => o.Markings = v,
            (r, v) => r.ApplyMarkings(FlightRasterizer.ParseMarkings(v.Word))),
        Choice(
            "horizon", "Look", "Horizon band", ["refined", "classic", "flat"], "refined",
            "REFINED interpolates the original's 31-entry haze ramp (palette 224..254), CLASSIC "
                + "steps it as the 1991 screen does, FLAT is a hard sky/ground split.",
            o => o.Horizon, (o, v) => o.Horizon = v,
            (r, v) => r.SceneOptions = r.SceneOptions with
            {
                Horizon = FlightRasterizer.ParseHorizon(v.Word),
            }),
        Number(
            "horizon-band", "Look", "Band thickness",
            HorizonRenderer.DefaultBandDegrees, 0.0, 40.0, 0.25, 2, "deg",
            "How thick the haze band is, in DEGREES of elevation at low altitude. The default is "
                + "the 20 scanlines of a captured frame of the original through the original's own "
                + "2^7-pixel focal length. 0 turns the band off.",
            o => o.HorizonBand, (o, v) => o.HorizonBand = v,
            (r, v) => r.SceneOptions = r.SceneOptions with { HorizonBandDegrees = v.Number }),
        Choice(
            "clouds", "Look", "Cloud deck", ["on", "off"], "on",
            "The cloud deck (the Graphics menu's own Clouds row, g_clouds_flag [0xB6]). The command "
                + "line also takes an ALTITUDE in feet; the dialog only switches the deck.",
            o => string.Equals(o.Clouds?.Trim(), "off", StringComparison.OrdinalIgnoreCase)
                ? "off" : "on",
            (o, v) => o.Clouds = v,
            (r, v) => r.CloudsVisible = string.Equals(v.Word, "on", StringComparison.Ordinal)),
        Number(
            "draw-distance", "Look", "Draw distance",
            0.0, 0.0, 200_000.0, 2_000.0, 0, "ft",
            "A hard Manhattan draw-distance ceiling in world units. 0 is unlimited, which is the "
                + "default a play-test asked for; the original's own per-class cull is --classic-cull.",
            o => o.DrawDistance, (o, v) => o.DrawDistance = v,
            (r, v) => r.SceneOptions = r.SceneOptions with
            {
                MaxDrawDistanceWorldUnits = v.Number,
            }),
        Choice(
            "lod", "Look", "Level of detail", ["max", "classic"], "max",
            "MAX draws every instance's densest LOD and never switches geometry; CLASSIC is the "
                + "original's distance cascade (mesh_visibility_lod_select @image@0x16BE8) with "
                + "anti-shimmer hysteresis. The Graphics menu's High/Medium Detail rows.",
            o => o.Lod, (o, v) => o.Lod = v,
            (r, v) => r.Lod = FlightRasterizer.ParseLod(v.Word)),

        Word(
            "bitmap-explosions", "Look", "Bitmap explosions", true,
            "The Graphics menu's own Bitmap Explosions row (g_bitmap_explosions_flag [0xC31E]). ON "
                + "draws the exp.rle sprite for a record whose +0x0C fork byte is non-zero "
                + "(image@0x03EDF); OFF draws the six-disc particle burst the original falls back "
                + "to. The shipped yeager.cfg has it ON.",
            o => o.BitmapExplosions, (o, v) => o.BitmapExplosions = v,
            (r, v) => r.BitmapExplosions = v.Flag),

        // ------------------------------------------------------------------ Effects
        Word(
            "smoke-growth", "Effects", "Smoke growth", true,
            "Run the smoke class's own prepare callback (smoke_sprite_render_params_setup "
                + "@image@0x0B2FF) on every puff, so its three discs grow from 25 to 200/280/240 "
                + "world units over the puff's life. OFF is the static 16-20-unit records.",
            o => o.SmokeGrowth, (o, v) => o.SmokeGrowth = v,
            (r, v) => r.SceneOptions = r.SceneOptions with { SmokeGrowth = v.Flag }),
        Number(
            "smoke-size", "Effects", "Smoke size", 1.3, 0.1, 5.0, 0.05, 2, "x",
            "A multiplier on the grown smoke disc radii. 1 is the original's own law; 1.3 is the "
                + "default (a little bigger than the original's).",
            o => o.SmokeSize, (o, v) => o.SmokeSize = v,
            (r, v) => r.SceneOptions = r.SceneOptions with { SmokeSizeScale = v.Number }),
        Number(
            "smoke-density", "Effects", "Smoke density", 1.0, 0.0, 6.0, 0.1, 2, "x",
            "A multiplier on a grown smoke disc's coverage. 1 is the record's own 25 % under the "
                + "soft falloff; the original's three masks are DISJOINT, so its overlapped core is "
                + "75 % solid - try 2..3 to match it.",
            o => o.SmokeDensity, (o, v) => o.SmokeDensity = v,
            (r, v) => r.SceneOptions = r.SceneOptions with { SmokeDensity = v.Number }),
        Word(
            "smoke-trail", "Effects", "Smoke layer", true,
            "Bear a PRESENTATION puff for every puff spawn and let it live longer, so a "
                + "wreck trails a real column (an authorised deviation). Switching it off in "
                + "flight stops new puffs; the ones in the air expire in their own time.",
            o => o.SmokeTrail, (o, v) => o.SmokeTrail = v,
            (r, v) => r.Smoke.Enabled = v.Flag),
        Number(
            "smoke-life", "Effects", "Smoke life", 3.0, 0.25, 12.0, 0.25, 2, "x",
            "How much longer a presentation puff lives than the original's (5 s damage trail / "
                + "25 s wreck column / 45 s stationary). A puff keeps the life it was born with, so "
                + "a change in flight is seen on the next puffs.",
            o => o.SmokeLife, (o, v) => o.SmokeLife = v,
            (r, v) => r.Smoke.LifeScale = Math.Max(0.0, v.Number)),
        Number(
            "smoke-fade", "Effects", "Smoke fade", 0.25, 0.0, 1.0, 0.05, 2, string.Empty,
            "The trailing fraction of a presentation puff's life spent fading out, so it does not "
                + "pop (the original pops). 0 = pop. Every puff already in the air is re-faded at "
                + "once, so a frozen frame shows the change.",
            o => o.SmokeFade, (o, v) => o.SmokeFade = v,
            (r, v) => r.Smoke.FadeFraction = Math.Clamp(v.Number, 0.0, 1.0)),
        Word(
            "wreck-smoke", "Effects", "Wreck trail", true,
            "A shot-down aircraft (any bandit, and the player) trails the smoke layer's own dark "
                + "puffs from the kill to the ground. Switching it off in flight retires the "
                + "emitters; the column already in the air falls and expires.",
            o => o.WreckSmoke, (o, v) => o.WreckSmoke = v,
            (r, v) => r.Smoke.WreckTrail = v.Flag),
        Number(
            "wreck-smoke-interval", "Effects", "Wreck trail rate",
            CYAC.Port.Core.Sim.Session.SmokeTrail.DefaultWreckIntervalSeconds, 0.02, 4.0, 0.01, 2, "s",
            "Seconds between the wreck trail's puffs. The original's own damage row emits every "
                + "4 s, which is 1,600 ft apart at fighter speed. It governs the NEXT puff: the "
                + "column already in the air keeps the spacing it fell with.",
            o => o.WreckSmokeInterval, (o, v) => o.WreckSmokeInterval = v,
            (r, v) => r.Smoke.WreckIntervalSeconds = Math.Max(0.0, v.Number)),
        Colour(
            "wreck-smoke-color", "Effects", "Wreck trail colour", 8, 0, 127,
            "The palette index all three of a wreck puff's discs are painted in. 8 is the "
                + "original's dark grey for a damage trail; 0 reads as a hole against the ground. "
                + "The whole live column is re-painted at once, so a frozen frame shows the change.",
            o => o.WreckSmokeColor, (o, v) => o.WreckSmokeColor = v,
            (r, v) => r.Smoke.WreckColorIndex = Math.Clamp(
                (int)Math.Round(v.Number), 0, CYAC.Port.Core.Sim.Session.SmokeTrail.WreckTrailMaxColor)),
        Number(
            // The twin is FlyOptions.WreckSmokeSize, which carries the strikethrough note.
            "wreck-smoke-size", "Effects", "Wreck trail size", 0.68, 0.25, 4.0, 0.05, 2, "x",
            "A multiplier on a WRECK-TRAIL puff's grown radii only. It is read when the puff is "
                + "DRAWN, so the whole live column changes size at once.",
            o => o.WreckSmokeSize, (o, v) => o.WreckSmokeSize = v,
            (r, v) => r.Smoke.WreckSizeScale = Math.Max(0.0, v.Number)),
        Choice(
            "tracer-halo", "Effects", "Tracer halo", ["off", "a", "b", "ab"], "ab",
            "The tracer's special-effect halo: A is a semi-transparent tracer-coloured glow, B "
                + "LIGHTENS whatever is under it, AB is both. A DEVIATION - the original draws one "
                + "flat palette-32 line.",
            o => o.TracerHalo, (o, v) => o.TracerHalo = v,
            (r, v) => r.SceneOptions = r.SceneOptions with
            {
                Tracer = Halo(r) with { Mode = TracerHalo.ParseMode(v.Word) },
            }),
        Number(
            "tracer-halo-radius", "Effects", "Halo radius",
            TracerHalo.DefaultRadiusFeet, 0.0, 40.0, 0.25, 2, "ft",
            "The tracer halo's radius about the projectile's own segment, in feet.",
            o => o.TracerHaloRadius, (o, v) => o.TracerHaloRadius = v,
            (r, v) => r.SceneOptions = r.SceneOptions with
            {
                Tracer = Halo(r) with { RadiusFeet = v.Number },
            }),
        Number(
            "tracer-halo-alpha", "Effects", "Halo opacity",
            TracerHalo.DefaultAlpha, 0.0, 1.0, 0.02, 2, string.Empty,
            "Half A's peak opacity at the centre of the halo profile (the rim is always 0). "
                + "0 switches A off.",
            o => o.TracerHaloAlpha, (o, v) => o.TracerHaloAlpha = v,
            (r, v) => r.SceneOptions = r.SceneOptions with
            {
                Tracer = Halo(r) with { Alpha = v.Number },
            }),
        Number(
            "tracer-glow", "Effects", "Halo glow",
            TracerHalo.DefaultGlow, 0.0, 1.0, 0.02, 2, string.Empty,
            "Half B's peak lightening - a pixel under the centre of the halo moves this fraction of "
                + "its own distance to white. 0 switches B off.",
            o => o.TracerGlow, (o, v) => o.TracerGlow = v,
            (r, v) => r.SceneOptions = r.SceneOptions with
            {
                Tracer = Halo(r) with { Glow = v.Number },
            }),
        Colour(
            // The twin is FlyOptions.TracerColor, which carries the strikethrough note.
            "tracer-color", "Effects", "Halo colour", 32, -1, 255,
            "The palette index the halo's glow is drawn in. 32 is the palest red of the tracer "
                + "ramp - RGB (255,158,158) - which is also what the .PNT bullet record carries; "
                + "-1 takes the drawn record's own colour instead.",
            o => o.TracerColor, (o, v) => o.TracerColor = v,
            (r, v) => r.SceneOptions = r.SceneOptions with
            {
                Tracer = Halo(r) with { ColorIndex = (int)Math.Round(v.Number) },
            }),
        Word(
            "rounds", "Effects", "Per-round gunnery", true,
            "The weapon table's ammoPerShot is the rounds per burst: the ported bullet stays the "
                + "one tracer and the sole damage authority, and N-1 GREY rounds trail it. "
                + "Switching it off in flight bears no more rounds; the ones in the air fly on.",
            o => o.Rounds, (o, v) => o.Rounds = v,
            (r, v) => r.Gunnery.Enabled = v.Flag),
        Number(
            // The twin is FlyOptions.RoundLength, which carries the strikethrough note.
            "round-length", "Effects", "Round length", 113.0, 1.0, 500.0, 5.0, 0, "ft",
            "The grey round's streak LENGTH in feet (the shipped tracer is a 64-foot line).",
            o => o.RoundLength, (o, v) => o.RoundLength = v,
            (r, v) => r.SetRoundLength(v.Number)),
        Number(
            "round-width", "Effects", "Round width",
            LineWidthModel.DefaultRoundFeet, 0.05, 5.0, 0.05, 2, "ft",
            "The grey round's physical WIDTH in feet; a thread, so below its floor it is widened "
                + "and dimmed to the coverage it really has.",
            o => o.RoundWidth, (o, v) => o.RoundWidth = v,
            (r, v) => r.SceneOptions = r.SceneOptions with
            {
                LineWidths = Widths(r) with { RoundFeet = v.Number },
            }),
        Colour(
            "round-color", "Effects", "Round colour", 15, 0, 255,
            "The palette index an ordinary round is drawn in. 15 = white (a play-test finding: "
                + "white reads most naturally); 19 and 20 are greys.",
            o => o.RoundColor, (o, v) => o.RoundColor = v,
            (r, v) => r.SetRoundColor((int)Math.Round(v.Number))),
        Number(
            // The twin is FlyOptions.RoundSpacing, which carries the strikethrough note.
            "round-spacing", "Effects", "Round spacing", 202.0, 0.0, 2_000.0, 10.0, 0, "ft",
            "The spacing between consecutive rounds of a belt, in feet; 0 uses the cyclic law "
                + "(speed x 64 ticks / N, which is 220 ft for a 5-round .50-calibre burst). It is "
                + "read when a BURST opens, so the next burst fires at the new spacing.",
            o => o.RoundSpacing, (o, v) => o.RoundSpacing = v,
            (r, v) => r.Gunnery.SpacingWorldUnits = Math.Max(0.0, v.Number)),
        Number(
            "round-hit-scale", "Effects", "Round hit scale", 1.0, 0.05, 4.0, 0.05, 2, "x",
            "A proportional scale on every hit BOX a grey round is tested against - 1 is the "
                + "verified kernel's own class-record box to the foot. Presentation only: it moves "
                + "flashes, never damage. A round in flight is tested at the scale in force now.",
            o => o.RoundHitScale, (o, v) => o.RoundHitScale = v,
            (r, v) => r.Gunnery.HitBoxScale = Math.Max(0.0, v.Number)),

        // ------------------------------------------------------------- Cockpit & HUD
        Word(
            "cockpit", "Cockpit & HUD", "Cockpit panel", true,
            "Draw the cockpit panel, its compositor overlay, its instrument regions and its dials "
                + "(g_cockpit_visible_flag [0xE471]; BACKSPACE toggles it at the window, "
                + "image@0x013D8, and that key writes this row). The original paints it in the "
                + "FORWARD cockpit view only.",
            o => o.Cockpit, (o, v) => o.Cockpit = v,
            (r, v) => r.CockpitOptions = r.CockpitOptions with { Enabled = v.Flag && r.HasCockpitArt }),
        Choice(
            // The port ships the blocky original; the twin is FlyOptions.CockpitFilter.
            "cockpit-filter", "Cockpit & HUD", "Panel filter", ["smooth", "nearest"], "nearest",
            "How the 320x200 cockpit art is resampled: NEAREST (the default: the 1991 pixels, "
                + "enlarged) or SMOOTH (bilinear over the opaque pixels - the art was drawn for a "
                + "1.2:1 CRT).",
            o => o.CockpitFilter, (o, v) => o.CockpitFilter = v,
            (r, v) => r.CockpitOptions = r.CockpitOptions with
            {
                Filter = string.Equals(v.Word, "nearest", StringComparison.Ordinal)
                    ? CockpitFilter.Nearest
                    : CockpitFilter.Smooth,
            }),
        Choice(
            "cockpit-view", "Cockpit & HUD", "View centre", ["viewport", "screen"], "viewport",
            "Where the 3-D world's own centre falls. VIEWPORT is the ORIGINAL: the world fills the "
                + "aircraft's viewport rows alone, so the horizon sits on row 67 of 200 in a P-51 "
                + "(image@0x118A4 puts the window centre at (y_min+y_max)>>1). SCREEN is a port "
                + "addition: the world fills the whole window and the panel is drawn over it, so "
                + "the horizon is at the middle of the screen and more sky is visible.",
            o => o.CockpitView, (o, v) => o.CockpitView = v,
            (r, v) => r.CockpitViewFullScreen =
                string.Equals(v.Word, "screen", StringComparison.Ordinal)),
        Choice(
            "hud-style", "Cockpit & HUD", "HUD marks", ["refined", "classic"], "refined",
            "The pipper, lead dots, waterline, target box and lock diamond as anti-aliased strokes "
                + "at window resolution, or as the 1991 pixel runs scaled. The GEOMETRY is the "
                + "original's in both.",
            o => o.HudStyle, (o, v) => o.HudStyle = v,
            (r, v) => r.CockpitOptions = r.CockpitOptions with
            {
                Hud = string.Equals(v.Word, "classic", StringComparison.Ordinal)
                    ? HudStyle.Classic
                    : HudStyle.Refined,
            }),
        Number(
            "hud-stroke", "Cockpit & HUD", "HUD stroke", 0.15, 0.1, 3.0, 0.02, 2, "px",
            "The refined HUD marks' stroke width, in 320x200 design pixels (the 1991 runs are "
                + "1.0 wide).",
            o => o.HudStroke, (o, v) => o.HudStroke = v,
            (r, v) => r.CockpitOptions = r.CockpitOptions with { HudHalfStroke = v.Number / 2.0 }),

        // A PORT ADDITION: the labels under enemy aircraft get an on/off control, a 1:1 letter
        // scale in pixel terms, and a configurable opacity.
        Word(
            "designator-labels", "Cockpit & HUD", "Target labels", true,
            "The labels under enemy aircraft - the target's type and the chance-to-hit. The "
                + "original always draws them (hud_engagement_label_draw @image@0x0CDB4); this is a "
                + "PORT control, because a modern screen shows far more of them at once than a "
                + "320x200 one ever did. The yellow box on the selected target is not affected.",
            o => o.DesignatorLabels, (o, v) => o.DesignatorLabels = v,
            (r, v) => r.CockpitOptions = r.CockpitOptions with { DesignatorLabels = v.Flag }),
        Number(
            "designator-label-scale", "Cockpit & HUD", "Target label size", 1, 1, 4, 1, 0, "px",
            "Host pixels per FONT pixel for those labels. 1 draws them 1:1 in real pixels - the "
                + "same physical size at 1080p and at 4K - which is the point: the port is not "
                + "recreating 320x200 on a 4K screen, so the letters need not grow with the panel "
                + "art. The label's ANCHOR is still the design-space projection, so it keeps riding "
                + "its target.",
            o => o.DesignatorLabelScale, (o, v) => o.DesignatorLabelScale = (int)Math.Round(v),
            (r, v) => r.CockpitOptions = r.CockpitOptions with
            {
                DesignatorLabelScale = (int)Math.Round(v.Number),
            }),
        // The four windows are enabled from the in-game menu, which also shows which of them are
        // on.  Four toggles over ONE option (--windows, the original's
        // [0xF1CB] byte persisted at yeager.cfg@0x1D); a Shift-1..4 press at the window is
        // mirrored back into these rows through FlightRasterizer.OverlaysChanged.
        WindowRow("window-envelope", "Envelope window (Shift-2)", CockpitOverlayFlags.Envelope, false,
            "The flight-envelope plot (top centre): airspeed across, altitude up, the green region "
                + "for the current G load and a blinking marker for where you are. Shift-2 at the "
                + "window, or Help > Envelope Window.",
            o => o.WindowEnvelope, (o, v) => o.WindowEnvelope = v),
        WindowRow("window-target", "Target window (Shift-3)", CockpitOverlayFlags.Target, true,
            "The selected target's window (top right): its type, the AI's manoeuvre, a live "
                + "silhouette, clock bearing, speed and range. Needs a target (Enter). Shift-3 at "
                + "the window, or Help > Target Window.",
            o => o.WindowTarget, (o, v) => o.WindowTarget = v),
        WindowRow("window-map", "Map window (Shift-1)", CockpitOverlayFlags.Map, true,
            "The inset map (top left): contacts as dots coloured by side and altitude, heading-up, "
                + "zoom with PageUp/PageDown or , and . Shift-1 at the window, or Help > Map Window.",
            o => o.WindowMap, (o, v) => o.WindowMap = v),
        WindowRow("window-yeager", "Yeager window (Shift-4)", CockpitOverlayFlags.Yeager, false,
            "Chuck's advisor window: his portrait and a two-line call when he has something to say "
                + "(it shares the map's corner and takes it while he speaks). Shift-4 at the "
                + "window, or Help > Yeager Window.",
            o => o.WindowYeager, (o, v) => o.WindowYeager = v),
        Number(
            "window-scale", "Cockpit & HUD", "Overlay window size", 0, 0, 8, 1, 0, "px",
            "Host pixels per DESIGN pixel for the four in-flight overlay windows (MAP / ENVELOPE / "
                + "TARGET / Yeager). 0 is AUTO - a quarter of the design scale, floored at 1, the "
                + "same rule the ESC menu uses: 1 at 1080p, 2 at 4K. 1 is a strict 1:1, so the "
                + "window's four-pixel border stays four real pixels instead of being enlarged with "
                + "the panel art. The window's PLACE still comes from the panel's mapping, so it "
                + "stays in its corner.",
            o => o.WindowScale, (o, v) => o.WindowScale = (int)Math.Round(v),
            (r, v) => r.CockpitOptions = r.CockpitOptions with
            {
                OverlayWindowScale = (int)Math.Round(v.Number),
            }),
        Number(
            "designator-opacity", "Cockpit & HUD", "Target label opacity", 0.33, 0.0, 1.0, 0.05, 2,
            string.Empty,
            "How opaque those labels are. 1 is solid, which is what the original draws; 0.33 (the "
                + "default - the tuned choice after flying it) lets the scene through "
                + "so a crowded sky stays readable.",
            o => o.DesignatorOpacity, (o, v) => o.DesignatorOpacity = v,
            (r, v) => r.CockpitOptions = r.CockpitOptions with { DesignatorOpacity = v.Number }),
        Choice(
            "needles", "Cockpit & HUD", "Dial needles", ["tapered", "line"], "tapered",
            "Dial needles as tapered needles over a hub, or as a constant-width anti-aliased line. "
                + "The original draws one-pixel lines (dial_slot_needle_line_draw @image@0x0195C).",
            o => o.Needles, (o, v) => o.Needles = v,
            (r, v) => r.CockpitOptions = r.CockpitOptions with
            {
                Needles = string.Equals(v.Word, "line", StringComparison.Ordinal)
                    ? NeedleStyle.Line
                    : NeedleStyle.Tapered,
            }),
        Choice(
            "instrument-window", "Cockpit & HUD", "Instrument window",
            ["analytic", "bitmap"], "analytic",
            "The artificial horizon's round window as a circle fitted to the aircraft's _horiz mask "
                + "and anti-aliased at window resolution, or as the 320x200 mask bitmap scaled.",
            o => o.InstrumentWindow, (o, v) => o.InstrumentWindow = v,
            (r, v) => r.CockpitOptions = r.CockpitOptions with
            {
                Window = string.Equals(v.Word, "bitmap", StringComparison.Ordinal)
                    ? InstrumentWindow.Bitmap
                    : InstrumentWindow.Analytic,
            }),
        Word(
            "flight-info", "Cockpit & HUD", "Flight info", true,
            "The HUD text overlay - g_flight_info_visible [0xB0], the Graphics menu's Ctrl-F item "
                + "(the port's H key). With it off the forward view keeps only the gunsight and the "
                + "target marker, exactly as image@0x0C633's si=0x500 mask does.",
            o => o.FlightInfo, (o, v) => o.FlightInfo = v,
            (r, v) => r.FlightInfoVisible = v.Flag),

        // ------------------------------------------------------------------ Sound
        Number(
            "sound-volume", "Sound", "Master volume", 0.8, 0.0, 1.0, 0.05, 2, string.Empty,
            "The mixer's master volume, applied after the tone generators.",
            o => o.SoundVolume, (o, v) => o.SoundVolume = v,
            (r, v) => r.SoundVolume = v.Number),
        new PortSetting
        {
            Name = "sound-mask",
            Section = "Sound",
            Label = "Sound channels",
            Kind = PortSettingKind.Bits,
            Bits = SoundBits,
            Minimum = 0,
            Maximum = 255,
            Step = 1,
            Help = "g_audio_mute_mask [0xE483]: bit0 master (gates audio_event_dispatcher itself, "
                + "image@0x29A43), bit1 engine, bit2 radar warning, bit3 stall, bit4 lock. The five "
                + "rows of the System menu; the shipped yeager.cfg holds 0xFE.",
            Default = SettingValue.Of(0xFF),
            Read = o => SettingValue.Of(ParseNumber(o.SoundMask ?? "0xFF")),
            Write = (o, v) => o.SoundMask =
                "0x" + ((int)Math.Round(v.Number)).ToString("X2", CultureInfo.InvariantCulture),
            Apply = (r, v) => r.SoundMask = (int)Math.Round(v.Number),
        },

        // ------------------------------------------------------------------- Menu
        Number(
            "menu-opacity", "Menu", "Menu opacity",
            Menu.FlightMenuController.DefaultOpacity, 0.0, 1.0, 0.05, 2, string.Empty,
            "How opaque the ESC menu's strip and panels are (1 = the original's solid grey). Text "
                + "is never faded; the point is to see the frozen scene while a look is tuned.",
            o => o.MenuOpacity, (o, v) => o.MenuOpacity = v,
            (r, v) =>
            {
                if (r.Menu is { } menu)
                {
                    menu.Opacity = v.Number;
                }
            }),
        Choice(
            "menu-scale", "Menu", "Menu size", ["auto", "1", "2", "3", "4"], "auto",
            "Host pixels per 320x200 design pixel for the ESC menu and this dialog. AUTO is a "
                + "quarter of the cockpit's own design scale (1 at 1080p, 2 at 4K); the menu is a "
                + "bitmap widget and is only ever scaled by whole pixels.",
            o => o.MenuScale, (o, v) => o.MenuScale = v,
            (r, v) =>
            {
                if (r.Menu is { } menu)
                {
                    menu.ScaleSetting = string.Equals(v.Word, "auto", StringComparison.Ordinal)
                        ? Menu.FlightMenuController.AutoScale
                        : (int)ParseNumber(v.Word);
                }
            }),

        Choice(
            "frontend-scale", "Menu", "Front-end size",
            ["auto", "1", "2", "3", "4", "5", "6", "8", "10"], "auto",
            "Host pixels per 320x200 design pixel for the FRONT END (CHOOSE ACTIVITY, Credits). "
                + "AUTO is the largest whole screen that fits - 5 at 1080p, 10 at 4K - centred, "
                + "with the border in the panel's own dark tone. A different knob from 'Menu size', "
                + "which sizes the ESC bar that floats over a flying sortie.",
            o => o.FrontEndScale, (o, v) => o.FrontEndScale = v,

            // No live push through the rasterizer: the FRONT END is not the rasterizer, and
            // HostShell reads this row per frame (HostShell.Scale), so the dialog moves it live
            // anyway - and a sortie in the air is not affected by it at all.
            null,
            liveWithoutApply: true),

        // ---------------------------------------------------------------- Developer
        Number(
            "threads", "Developer", "Render threads", 0, 0, 64, 1, 0, string.Empty,
            "How many threads rasterise the 3-D world's tiles. 0 is the processor count; 1 is the "
                + "serial path through the same code. A PERFORMANCE knob only - the picture is "
                + "bit-identical at every thread count (TileInvarianceTests).",
            o => o.Threads, (o, v) => o.Threads = (int)Math.Round(v),
            (r, v) => r.SceneOptions = r.SceneOptions with
            {
                Threads = v.Number > 0
                    ? Math.Min((int)Math.Round(v.Number), 256)
                    : Environment.ProcessorCount,
            }),
        Number(
            "tile", "Developer", "Tile size",
            SceneRenderOptions.DefaultTileSize, 4, 2048, 8, 0, "px",
            "The side in TARGET pixels of the square tiles the 3-D display list is binned into. A "
                + "PERFORMANCE knob only - the picture is bit-identical at every tile size.",
            o => o.Tile, (o, v) => o.Tile = (int)Math.Round(v),
            (r, v) => r.SceneOptions = r.SceneOptions with
            {
                TileSize = Math.Clamp((int)Math.Round(v.Number), 4, 8192),
            }),

        // The startup dashboard's show policy: read by the boot screen before the game exists, so it
        // has no live surface to push into (PortSetting.ReadAtStartup).
        Choice(
            "startup-check", "Developer", "Startup check", ["always", "problems"], "always",
            "Whether the PRE-FLIGHT DASHBOARD is drawn while the game starts. ALWAYS shows the ten "
                + "startup steps and waits for Enter when they are done; PROBLEMS goes straight into "
                + "the game unless a step warns or fails. The checks themselves always run.",
            o => o.StartupCheck, (o, v) => o.StartupCheck = v,
            apply: null) with { ReadAtStartup = true },
    ];
}
