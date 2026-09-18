using CYAC.Port.Core.Data;
using CYAC.Port.Core.Sim;
using CYAC.Port.Render;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>Which part of the boot title sequence is on screen.</summary>
public enum TitlePhase
{
    /// <summary>Black → <c>title0v</c>'s palette: "Electronic Arts presents" arrives.</summary>
    FadeIn,

    /// <summary>One title stands at its full palette, for a tick-counted wait.</summary>
    Hold,

    /// <summary>One title's palette → the other's: both images show through the blend.</summary>
    Crossfade,

    /// <summary>A key was pressed: the current palette → black.</summary>
    FadeOut,

    /// <summary>Black → the game palette, with CHOOSE ACTIVITY already drawn underneath.</summary>
    MenuFadeIn,

    /// <summary>Over: the shell is an ordinary F1 shell again.</summary>
    Done,
}

/// <summary>
/// THE BOOT TITLE SEQUENCE: the original's own two title screens, its own palettes and its own
/// tick-counted waits, driven by the port's clock.
/// </summary>
/// <remarks>
/// <para>
/// <b>The decoded original.</b> <c>ui_title_screen_pair_show @image@0x24DB0</c> is called once from
/// <c>mission_state_machine @image@0x00A24</c>, between the copy-protection prompt (which the port
/// drops) and <c>ui_main_menu_enter @image@0x24334</c>.  Its VGA arm (<c>image@0x24E5E..0x2504D</c>,
/// taken when <c>g_render_state_mask [0xE64E] &gt;= 0x100</c>) does this:
/// </para>
/// <list type="number">
///   <item>zero a 768-byte palette buffer and upload it — <b>the screen goes black</b>
///   (<c>image@0x24E68..0x24E83</c>), and <c>g_vga_dac_dirty_flag [0x3420] = 1</c>
///   (<c>image@0x24E88</c>) so that whoever draws next knows the DAC is dark;</item>
///   <item>load <c>title0v.pic</c> and <c>title1v.pic</c> into two buffers
///   (<c>image@0x24EBC..0x24ED1</c>);</item>
///   <item><b>COMPOSITE them into one image</b>: in the 256-colour arm each byte becomes
///   <c>title0v + (title1v &lt;&lt; 4)</c> (<c>image@0x24F04..0x24F19</c>) — the low nibble is
///   title0's 4-bit index, the high nibble is title1's.  (The planar arm at
///   <c>image@0x24F1C..0x24F69</c> does the same OR plane by plane; the composite it builds is
///   identical.)</item>
///   <item>load <c>title0v.pal</c> into <c>[0xDBA4]</c> and <c>title1v.pal</c> into <c>[0xD8A4]</c>
///   (<c>image@0x24F6A..0x24F98</c>) and <b>SCRAMBLE both to 256 entries</b>:
///   <c>[0xD8A4][i] = pal1[i &gt;&gt; 4]</c> (the loop at <c>image@0x24FA4..0x24FD1</c>) and
///   <c>[0xDBA4][i] = pal0[i &amp; 15]</c> (the 16-entry stripe copy at
///   <c>image@0x24FD8..0x24FF8</c>).  So one palette shows the LOW nibble and the other the HIGH
///   nibble of the same composite — which is how both pictures live in one 320×200 buffer;</item>
///   <item>fade in, hold, cross-fade, hold, and on a key fade out — see
///   <see cref="Advance(double, bool)"/> for the exact bytes of each step.</item>
/// </list>
/// <para>
/// <b>Every fade is <see cref="BlendSteps"/>+1 uploads of a linearly interpolated palette</b>:
/// <c>palette_crossfade_blend_and_upload @image@0x250A5</c> runs <c>step = 0..0x80</c> inclusive and
/// writes <c>out[i] = (to[i]·step + from[i]·(0x80 − step)) &gt;&gt; 7</c> for all 256 triples
/// (<c>image@0x250CB..0x25130</c>, the three channels), uploading each one to the DAC
/// (<c>image@0x2514D</c>).  Step 0 IS the from-palette and step 0x80 IS the to-palette.
/// </para>
/// <para>
/// <b>What the port keeps and what it must choose.</b>  The HOLDS are tick-counted and therefore
/// exact: <see cref="Hold0Ticks"/> and <see cref="Hold1Ticks"/> PIT ticks through
/// <see cref="TickClock.TicksPerSecond"/>.  The FADES are not paced at all — the original simply
/// uploads 129 palettes as fast as the machine will do it, so their duration is a property of the
/// 1991 hardware and not of the game.  The port pins them to <see cref="FadeTicks"/> ticks, one
/// blend step per PIT tick, which is what the reference capture measures.
/// </para>
/// <para>
/// <b>The pointer is not drawn during any of this</b> — not even over the fading menu.  The menu's
/// fade call (<c>image@0x2573E</c>) precedes its event loop (<c>image@0x25746</c>), and the event
/// loop is the only place <c>cursor_draw @image@0x2E7F8</c> runs.  The atlas agrees: at
/// the original's blend step the whole frame matches except the 9 × 10 box at (290, 181)
/// where the original's screen — the same screen, after the loop started — draws the arrow.
/// </para>
/// </remarks>
public sealed class TitleSequence
{
    /// <summary>The <c>title0v</c> image document — "Electronic Arts presents".</summary>
    public const string Title0Document = "images/title0v.json";

    /// <summary>The <c>title1v</c> image document — CHUCK YEAGER'S AIR COMBAT.</summary>
    public const string Title1Document = "images/title1v.json";

    /// <summary>Its companion palette document (<c>1b.lib/title0v.pal</c>, 256 × 6-bit).</summary>
    public const string Title0Palette = "title0v";

    /// <summary>Its companion palette document (<c>1b.lib/title1v.pal</c>).</summary>
    public const string Title1Palette = "title1v";

    /// <summary>
    /// <c>0x80</c> — the blend's last step; a fade is <c>0..0x80</c> inclusive, 129 uploads
    /// (<c>image@0x25153</c>: <c>cmp si, 0x80</c> / <c>ja</c>, so 0x80 itself runs).
    /// </summary>
    public const int BlendSteps = 0x80;

    /// <summary>
    /// <c>0x400</c> = 1,024 PIT ticks — the FIRST hold, title0 alone
    /// (<c>timed_tick_wait(0x400, poll) @image@0x2500C</c>).
    /// </summary>
    public const int Hold0Ticks = 0x400;

    /// <summary>
    /// <c>0xA00</c> = 2,560 PIT ticks — every hold after the first
    /// (<c>timed_tick_wait(0xA00, poll) @image@0x25037</c>, which is also the loop's own wait).
    /// </summary>
    public const int Hold1Ticks = 0xA00;

    /// <summary>
    /// How long a fade is given, in PIT ticks: <b>128</b> — one blend step per tick.
    /// </summary>
    /// <remarks>
    /// A PORT CHOICE, because the original has no answer.  Its fade is a bare loop of 129 DAC
    /// uploads with no wait in it, so it runs at whatever speed the machine has.  The number here is
    /// measured off a capture of the original on an 8 MIPS machine (a true 256 Hz wall at 8 MIPS):
    /// the fade-in occupies ≈ 4.23 M instructions and the 1,024-tick hold beside it occupies
    /// ≈ 33.8 M, so the fade is 1,024 × 4.23 / 33.8 ≈ 129 ticks ≈ 0.50 s.
    /// </remarks>
    public const int FadeTicks = 128;

    private readonly IndexedImage _composite;
    private readonly byte[] _title0Palette6 = new byte[256 * 3];
    private readonly byte[] _title1Palette6 = new byte[256 * 3];
    private readonly byte[] _gamePalette6 = new byte[256 * 3];
    private readonly IReadOnlyList<Rgb24> _gamePalette;
    private readonly Rgb24[] _live = new Rgb24[256];

    private double _elapsed;
    private bool _key;
    private int _shown;
    private int _holds;
    private int _step = -1;
    private TitlePhase _phase = TitlePhase.FadeIn;

    private TitleSequence(
        IndexedImage composite,
        PaletteDocumentDto title0,
        PaletteDocumentDto title1,
        PaletteDocumentDto game,
        IReadOnlyList<Rgb24> gamePalette)
    {
        _composite = composite;
        _gamePalette = gamePalette;

        // The two scrambles, exactly as the original's two loops write them (see the type's
        // remarks): title0's palette answers the LOW nibble, title1's the HIGH one.
        Scramble(title0, index => index & 0x0F, _title0Palette6);
        Scramble(title1, index => index >> 4, _title1Palette6);
        Scramble(game, index => index, _gamePalette6);
        Recolour();
    }

    /// <summary>
    /// Loads the title pair, or <see langword="null"/> when this tree has no title art — in which
    /// case the shell is exactly the F1 shell and the menu is up on frame 0.
    /// </summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="gamePalette">
    /// The port's own widened game palette, which the menu fade ends on so that the last frame of
    /// the sequence and the first frame of the menu are the same colours.
    /// </param>
    public static TitleSequence? Load(DataTree tree, IReadOnlyList<Rgb24> gamePalette)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(gamePalette);
        try
        {
            IndexedImage title0 = tree.ImagePixels(Title0Document);
            IndexedImage title1 = tree.ImagePixels(Title1Document);
            if (title0.Width != FrontEndPainter.DesignWidth
                || title0.Height != FrontEndPainter.DesignHeight
                || title1.Width != title0.Width || title1.Height != title0.Height)
            {
                return null;
            }

            return new TitleSequence(
                Composite(title0, title1),
                tree.Palette(Title0Palette),
                tree.Palette(Title1Palette),
                tree.Palette("palette"),
                gamePalette);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The original's own composite: <c>title0v + (title1v &lt;&lt; 4)</c>
    /// (<c>image@0x24F04..0x24F19</c>).  Both pictures in one 320 × 200 buffer, told apart by which
    /// nibble the palette answers.
    /// </summary>
    /// <param name="title0">The first title's 4-bit indices.</param>
    /// <param name="title1">The second's.</param>
    /// <returns>The composited indices.</returns>
    public static IndexedImage Composite(IndexedImage title0, IndexedImage title1)
    {
        byte[] low = title0.Indices;
        byte[] high = title1.Indices;
        byte[] indices = new byte[low.Length];
        for (int i = 0; i < indices.Length && i < high.Length; i++)
        {
            indices[i] = (byte)((low[i] & 0x0F) | ((high[i] & 0x0F) << 4));
        }

        return new IndexedImage(title0.Width, title0.Height, indices, []);
    }

    /// <summary>The composited title pair — what the sequence paints while a title is up.</summary>
    public IndexedImage Picture => _composite;

    /// <summary>Which part of the sequence is on screen.</summary>
    public TitlePhase Phase => _phase;

    /// <summary>The blend step inside the current fade, <c>0..<see cref="BlendSteps"/></c>.</summary>
    public int Step => _step;

    /// <summary>Whether the sequence is over and the shell is an ordinary F1 shell again.</summary>
    public bool Done => _phase == TitlePhase.Done;

    /// <summary>
    /// Whether the COMPOSITE is what to paint this frame.  False from <see cref="TitlePhase.MenuFadeIn"/>
    /// on, where the front end's own screen is painted through <see cref="Palette"/> instead.
    /// </summary>
    public bool ShowsTitles => _phase is TitlePhase.FadeIn or TitlePhase.Hold
        or TitlePhase.Crossfade or TitlePhase.FadeOut;

    /// <summary>Which title the composite currently reads as: 0 = title0v, 1 = title1v.</summary>
    public int Showing => _shown;

    /// <summary>The palette to paint this frame with — 256 entries, already widened to 8 bits.</summary>
    public IReadOnlyList<Rgb24> Palette => _live;

    /// <summary>How long a fade lasts, in seconds.</summary>
    public static double FadeSeconds => FadeTicks / TickClock.TicksPerSecond;

    /// <summary>How long the first hold lasts, in seconds — <c>0x400</c> ticks.</summary>
    public static double Hold0Seconds => Hold0Ticks / TickClock.TicksPerSecond;

    /// <summary>How long every later hold lasts, in seconds — <c>0xA00</c> ticks.</summary>
    public static double Hold1Seconds => Hold1Ticks / TickClock.TicksPerSecond;

    /// <summary>How long the current phase lasts, in seconds.</summary>
    public double PhaseSeconds => _phase switch
    {
        TitlePhase.Hold => _holds == 0 ? Hold0Seconds : Hold1Seconds,
        TitlePhase.Done => 0,
        _ => FadeSeconds,
    };

    /// <summary>
    /// Advances the sequence by one frame's worth of time and applies the frame's keyboard.
    /// </summary>
    /// <param name="seconds">Seconds since the previous frame.</param>
    /// <param name="anyKey">
    /// Whether ANY key's press edge landed this frame.  The original polls
    /// <c>kbd_event_poll_and_classify @image@0x20488</c>, which classifies whatever is in the
    /// keyboard buffer — so it is any key, and the mouse is not one of them.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Where the original polls, and what it does about it.</b>
    /// </para>
    /// <list type="bullet">
    ///   <item><c>image@0x24FFA</c> — after the composite and the scrambles, BEFORE the first fade.
    ///   A key already waiting there jumps straight to the exit (<c>jne 0x2504E</c>) and the title
    ///   pair is never shown at all: no fade in, no fade out — any queued key skips the titles.</item>
    ///   <item>inside <c>timed_tick_wait(0x400, dl=1) @image@0x2500C</c> — a key during the FIRST
    ///   hold: <c>image@0x2501A</c> loads the CURRENT palette and jumps to the fade-out.</item>
    ///   <item>inside <c>timed_tick_wait(0xA00, dl=1) @image@0x25037</c> — a key during any later
    ///   hold: <c>image@0x25045</c> does the same.</item>
    /// </list>
    /// <para>
    /// <b>There is no poll inside a fade</b> (<c>palette_crossfade_blend_and_upload</c> is a bare
    /// 129-iteration loop).  A key pressed during one is not lost, though: it sits in the keyboard
    /// buffer until the next <c>timed_tick_wait</c> reads it.  The port latches it for exactly the
    /// same reason and acts on it at the next poll point.
    /// </para>
    /// <para>
    /// <b>Without a key the pair alternates for ever.</b>  When the <c>0xA00</c> wait times out the
    /// code jumps back to <c>image@0x2501F</c>, which SWAPS the two palette buffers
    /// (<c>near_memswap(0x300, [0xDBA4], [0xD8A4]) @image@0x25029</c>) and cross-fades again — so the
    /// screen goes title0 → title1 → title0 → … every <see cref="Hold1Ticks"/> ticks.  The first
    /// cross-fade is reached from <c>image@0x25018</c>'s <c>je 0x2502E</c>, which lands PAST the
    /// swap: that is why the first transition goes title0 → title1 and not the other way round.
    /// </para>
    /// </remarks>
    public void Advance(double seconds, bool anyKey)
    {
        if (_phase == TitlePhase.Done)
        {
            return;
        }

        // image@0x24FFA: the poll BEFORE the first fade.  A key that is already down when the
        // sequence opens skips the pair outright — no fade in and no fade out, straight to the menu.
        bool beforeTheFirstFade = _phase == TitlePhase.FadeIn && _step < 0 && _elapsed <= 0;
        _key |= anyKey;
        _elapsed += Math.Max(0, seconds);
        if (beforeTheFirstFade && _key)
        {
            _key = false;
            Enter(TitlePhase.MenuFadeIn, 0);
        }

        // A phase may end inside one frame — at a very low frame rate several may — so this settles
        // rather than steps once.  The leftover time is carried, which is what keeps the whole
        // timeline independent of the frame rate.
        while (_phase != TitlePhase.Done)
        {
            int step = Math.Min(BlendSteps, (int)(_elapsed * TickClock.TicksPerSecond));
            if (_phase == TitlePhase.Hold)
            {
                if (_key)
                {
                    // image@0x2501A / image@0x25045: the key fades the CURRENT palette to black.
                    _key = false;
                    Enter(TitlePhase.FadeOut, 0);
                    continue;
                }

                if (_elapsed >= PhaseSeconds)
                {
                    double over = _elapsed - PhaseSeconds;
                    _holds++;
                    Enter(TitlePhase.Crossfade, over);
                    continue;
                }

                // A hold's palette never changes — Enter() has already built it.
                return;
            }

            if (_elapsed >= FadeSeconds)
            {
                double over = _elapsed - FadeSeconds;
                switch (_phase)
                {
                    case TitlePhase.FadeIn:
                        Enter(TitlePhase.Hold, over);
                        break;
                    case TitlePhase.Crossfade:
                        // The swap has landed: the other title is the one on screen now.
                        _shown ^= 1;
                        Enter(TitlePhase.Hold, over);
                        break;
                    case TitlePhase.FadeOut:
                        Enter(TitlePhase.MenuFadeIn, over);
                        break;
                    default:
                        Enter(TitlePhase.Done, 0);
                        break;
                }

                continue;
            }

            if (step != _step)
            {
                _step = step;
                Recolour();
            }

            return;
        }
    }

    private void Enter(TitlePhase phase, double carried)
    {
        _phase = phase;
        _elapsed = carried;
        _step = phase == TitlePhase.Hold
            ? BlendSteps
            : Math.Min(BlendSteps, (int)(_elapsed * TickClock.TicksPerSecond));
        Recolour();
    }

    /// <summary>Rebuilds <see cref="Palette"/> for the phase and step the sequence is now at.</summary>
    /// <remarks>
    /// The blend is done on the SIX-BIT values and widened afterwards, because that is the order the
    /// original works in: it interpolates DAC bytes (0..63) and the DAC widens them to eight bits by
    /// replicating the top two bits.  Widening first and interpolating afterwards would round
    /// differently on most entries — and it is this order that makes the frames byte-identical to
    /// the atlas.
    /// </remarks>
    private void Recolour()
    {
        switch (_phase)
        {
            case TitlePhase.FadeIn:
                Blend(null, _title0Palette6, _step);
                break;
            case TitlePhase.Hold:
                Blend(null, _shown == 0 ? _title0Palette6 : _title1Palette6, BlendSteps);
                break;
            case TitlePhase.Crossfade:
                Blend(
                    _shown == 0 ? _title0Palette6 : _title1Palette6,
                    _shown == 0 ? _title1Palette6 : _title0Palette6,
                    _step);
                break;
            case TitlePhase.FadeOut:
                Blend(_shown == 0 ? _title0Palette6 : _title1Palette6, null, _step);
                break;
            case TitlePhase.MenuFadeIn:
                Blend(null, _gamePalette6, _step);
                break;
            default:
                // The sequence is over: hand back the port's own palette, so that the frame after
                // the ramp and every frame of the menu that follows are the same colours.  (The
                // ramp itself widens the DAC's way; the two differ by at most one part in 255,
                // which is a CYAC.Port.Render decision, not this one's.)
                for (int i = 0; i < _live.Length; i++)
                {
                    _live[i] = i < _gamePalette.Count ? _gamePalette[i] : default;
                }

                break;
        }
    }

    /// <summary>
    /// <c>out[i] = (to[i]·step + from[i]·(0x80 − step)) &gt;&gt; 7</c> over 256 triples, then the
    /// DAC's widening — <c>palette_crossfade_blend_and_upload @image@0x250A5</c>, exactly.
    /// </summary>
    /// <param name="from">The step-0 palette; null is the all-zero (black) buffer <c>[0xDEA4]</c>.</param>
    /// <param name="to">The step-0x80 palette; null is black.</param>
    /// <param name="step">The blend step, 0..0x80.</param>
    private void Blend(byte[]? from, byte[]? to, int step)
    {
        int forward = Math.Clamp(step, 0, BlendSteps);
        int backward = BlendSteps - forward;
        for (int i = 0; i < _live.Length; i++)
        {
            int at = i * 3;
            _live[i] = new Rgb24(
                Widen(Channel(from, to, at, forward, backward)),
                Widen(Channel(from, to, at + 1, forward, backward)),
                Widen(Channel(from, to, at + 2, forward, backward)));
        }
    }

    private static int Channel(byte[]? from, byte[]? to, int at, int forward, int backward) =>
        (((to is null ? 0 : to[at]) * forward) + ((from is null ? 0 : from[at]) * backward)) >> 7;

    /// <summary>
    /// The VGA DAC's own six-bit widening: replicate the top two bits
    /// (<c>platform</c>; the same rule <c>FrontEndPixelTests</c> compares the atlas through).
    /// </summary>
    /// <param name="sixBit">A DAC component, 0..63.</param>
    private static byte Widen(int sixBit) => (byte)(((sixBit << 2) | (sixBit >> 4)) & 0xFF);

    /// <summary>
    /// Expands a 16-entry <c>.pal</c> document to the 256 entries the composite needs, through the
    /// index map the original's own scramble loop writes.
    /// </summary>
    /// <param name="document">The palette document (256 rows of 6-bit RGB, only 16 of them used).</param>
    /// <param name="source">Which source entry answers palette index <c>i</c>.</param>
    /// <param name="into">The 768-byte buffer to fill.</param>
    private static void Scramble(PaletteDocumentDto document, Func<int, int> source, byte[] into)
    {
        List<List<int>> rows = document.Colors ?? [];
        for (int i = 0; i < 256; i++)
        {
            int at = source(i);
            List<int> row = at < rows.Count ? rows[at] : [];
            into[(i * 3) + 0] = row.Count > 0 ? (byte)Math.Clamp(row[0], 0, 63) : (byte)0;
            into[(i * 3) + 1] = row.Count > 1 ? (byte)Math.Clamp(row[1], 0, 63) : (byte)0;
            into[(i * 3) + 2] = row.Count > 2 ? (byte)Math.Clamp(row[2], 0, 63) : (byte)0;
        }
    }
}
