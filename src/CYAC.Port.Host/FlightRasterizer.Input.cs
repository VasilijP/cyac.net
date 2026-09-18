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
/// INPUT: the debug/cheat keys the host cooks for the rasterizer, the display switches they
/// toggle, and the two sound controls those switches drive.
/// </summary>
/// <remarks>The reader's map of every one of this class's files is at the top of
/// <c>FlightRasterizer.cs</c>.</remarks>
public sealed partial class FlightRasterizer
{
    /// <summary>The empty key list a frozen frame's input carries.</summary>
    private static readonly int[] NoKeys = [];

    /// <summary>
    /// The SOUND path, or null when it is off.  It is pumped once a frame, from the same place the
    /// camera is resolved, because the two continuous channels and the positional impact range
    /// both read the view (<c>g_current_view_mode [0xC320]</c>) and the view anchor.
    /// </summary>
    public HostAudio? Audio { get; set; }

    /// <summary>
    /// <c>g_audio_mute_mask [0xE483]</c>, the five System sound rows.  Reads 0 when the run has no
    /// sound path, which leaves the five rows unchecked and their toggles inert.
    /// </summary>
    public int SoundMask
    {
        get => Audio?.Runtime.MuteMask ?? 0;
        set
        {
            if (Audio is { } audio)
            {
                audio.Runtime.MuteMask = (byte)(value & 0xFF);
            }
        }
    }

    /// <summary>The mixer's master volume; 0 when the run has no sound path.</summary>
    public double SoundVolume
    {
        get => Audio?.Runtime.Mixer.MasterVolume ?? 0.0;
        set
        {
            if (Audio is { } audio)
            {
                audio.Runtime.Mixer.MasterVolume = Math.Clamp(value, 0.0, 1.0);
            }
        }
    }

    /// <summary>
    /// Flips a TOGGLE setting through the store when there is one, so a key that changes an option
    /// persists exactly as the dialog's own row does.
    /// </summary>
    /// <param name="name">The setting's command-line name.</param>
    /// <param name="fallback">What to do when the run has no settings store.</param>
    private void ToggleSetting(string name, Action fallback)
    {
        if (SettingsStore is { } store && PortSettings.Find(name) is { } setting)
        {
            store.Set(setting, SettingValue.Toggle(!store.Value(setting).Flag));
            return;
        }

        fallback();
    }

    /// <summary>The Graphics menu's detail rows: the port's two LOD policies.</summary>
    public LodPolicy Lod
    {
        get => _sceneOptions.Lod;
        set => _sceneOptions = _sceneOptions with { Lod = value };
    }

    /// <summary>Graphics."Dithered Horizon": the REFINED band against the flat one.</summary>
    public bool DitheredHorizon
    {
        get => _sceneOptions.Horizon != HorizonStyle.Flat;
        set => _sceneOptions = _sceneOptions with
        {
            Horizon = value ? HorizonStyle.Refined : HorizonStyle.Flat,
        };
    }

    /// <summary>Graphics."Clouds", <c>g_clouds_flag [0xB6]</c>: the deck on or off.</summary>
    public bool CloudsVisible
    {
        get => _cloudsOn;
        set => _cloudsOn = value;
    }

    /// <summary>Graphics."Bitmap Explosions", <c>g_bitmap_explosions_flag [0xC31E]</c>.</summary>
    public bool BitmapExplosions
    {
        get => _sceneOptions.BitmapExplosions;
        set => _sceneOptions = _sceneOptions with { BitmapExplosions = value };
    }

    /// <summary>Applies one RENDER SCRUTINY key edge (<see cref="DebugKey"/>).</summary>
    /// <param name="key">The edge.</param>
    internal void HandleDebugKey(DebugKey key)
    {
        switch (key)
        {
            case DebugKey.Screenshot:
                _shotRequested = true;
                break;

            case DebugKey.CullToggle:
                _sceneOptions = _sceneOptions with { BackfaceCull = !_sceneOptions.BackfaceCull };
                PostNotice(_sceneOptions.BackfaceCull ? "Backface culling ON" : "Backface culling OFF (both sides drawn)");
                break;

            case DebugKey.WireframeCycle:
                WireframeMode next = _sceneOptions.Wireframe switch
                {
                    WireframeMode.Off => WireframeMode.Overlay,
                    WireframeMode.Overlay => WireframeMode.Only,
                    _ => WireframeMode.Off,
                };
                _sceneOptions = _sceneOptions with { Wireframe = next };
                PostNotice(next switch
                {
                    WireframeMode.Overlay => "Wireframe OVER the polygons",
                    WireframeMode.Only => "Wireframe ONLY",
                    _ => "Wireframe OFF",
                });
                break;

            case DebugKey.FaceColorsToggle:
                bool contrast = _sceneOptions.FaceColors != FaceColorMode.Contrast;
                _sceneOptions = _sceneOptions with
                {
                    FaceColors = contrast ? FaceColorMode.Contrast : FaceColorMode.Paint,
                };
                PostNotice(contrast
                    ? $"Face colours CONTRAST (shuffle {_sceneOptions.FaceColorSeed}; Shift+I reshuffles)"
                    : "Face colours PAINT");
                break;

            case DebugKey.MaskViewCycle:
                MaskView view = _sceneOptions.MaskView switch
                {
                    MaskView.Off => MaskView.Silhouette,
                    MaskView.Silhouette => MaskView.Interior,
                    _ => MaskView.Off,
                };
                _sceneOptions = _sceneOptions with { MaskView = view };
                PostNotice(view switch
                {
                    MaskView.Silhouette => "Mask view: binary SILHOUETTE (white = inside, red = a hole the closing filled)",
                    MaskView.Interior => "Mask view: INTERIOR (green) and the outline ring (red)",
                    _ => "Mask view OFF",
                });
                break;

            case DebugKey.FaceColorsReshuffle:
                _sceneOptions = _sceneOptions with
                {
                    FaceColors = FaceColorMode.Contrast,
                    FaceColorSeed = _sceneOptions.FaceColorSeed + 1,
                };
                PostNotice($"Face colours CONTRAST, shuffle {_sceneOptions.FaceColorSeed}");
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The cockpit keys of a frame whose <c>+</c> / <c>−</c> press belonged to the MAP.
    /// </summary>
    /// <param name="keys">The frame's cooked cockpit keys.</param>
    /// <remarks>
    /// In the ORIGINAL <c>+</c> and <c>−</c> are not throttle keys at all — the throttle is
    /// <c>7</c> / <c>8</c> and the five presets (<c>cockpit_key_dispatch @image@0x2A3FF</c>), and
    /// <c>+</c> / <c>−</c> reach <c>gauge_zoom_in_keyhandler @image@0x0D8B5</c>.  H1 bound them to
    /// the throttle as a convenience; while the map is up they mean what they mean in 1991.
    /// </remarks>
    private static List<int> WithoutThrottleKeys(IReadOnlyList<int> keys)
    {
        List<int> kept = new List<int>(keys.Count);
        foreach (int key in keys)
        {
            if (key != CockpitKeys.ThrottleUpKey && key != CockpitKeys.ThrottleDownKey)
            {
                kept.Add(key);
            }
        }

        return kept;
    }
}
