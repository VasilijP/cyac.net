using CYAC.Port.Core.Data;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// THE ORIGINAL'S MOUSE POINTER: its own art, its own hotspot, its own arithmetic.
/// </summary>
/// <remarks>
/// <para>
/// <b>The art.</b>  <c>cursor.pic</c> (<c>data/images/cursor.json</c>, 128 × 24) and its 1-bit
/// overlay mask <c>cursorm.msk</c> (<c>data/images/masks/cursorm.json</c>) are what
/// <c>cursor_and_msk_init @image@0x2E136</c> / <c>cursor_pic_loader @image@0x2E184</c> load into
/// <c>[0x4772]</c> / <c>[0x477C]</c>.  The file is <b>not</b> a strip of four 32-wide frames: it
/// holds TWO cursors stacked vertically — the ARROW in rows 0..10 and an HOURGLASS in rows 13..23 —
/// each replicated eight times across the row.  The replication is the classic PRE-SHIFTED SPRITE:
/// <c>cursor_draw @image@0x2E7F8</c> takes <c>frame = cursor_x &amp; 7</c>
/// (<c>AND AX,[BX-0x42B4]</c> @<c>image@0x2E8FE</c>), samples the atlas at
/// <c>src_x = frame · 16</c> (<c>SHL AX,CL</c> @<c>image@0x2E906</c>) and blits at
/// <c>dst_x = cursor_x &amp; ~7</c> (<c>image@0x2E90D</c>), so copy <i>k</i> sits at column
/// <c>16k + k</c> — the sprite shifted right by <i>k</i> inside its own 16-wide cell.  That is why
/// the copies are 17 columns apart and why the eighth is clipped at 128: at phase 7 the 9-wide
/// arrow occupies exactly columns 119..127.  <c>cursor2.pic</c> (32 × 24) is the SAME two cursors
/// with two phases instead of eight — the CGA arm, <c>frame_mask = 1</c> when
/// <c>g_cfg_sub_mode [0x15E] == 4</c> (<c>image@0x2E8EC</c>).
/// </para>
/// <para>
/// <b>The hotspot is the arrow's TIP, at (0,0) of the sprite.</b> Two independent proofs. (1)
/// The original's own screen after seven Tabs onto Credits has its pointer
/// stand at design (143, 181); the Credits button is drawn at (87, 171, 61, 15), so its
/// <c>s_ui_widget</c> rectangle is (89, 173, 57, 11) and <c>widget_mouse_cursor_position_set
/// @image@0x2E4DD</c>'s right-edge arm (<c>rect_x + rect_w − 3</c>, <c>rect_y + rect_h − 3</c>,
/// <c>image@0x2E4F9..0x2E50C</c>) puts the cursor at exactly (143, 181) — both coordinates, to the
/// pixel.  (2) Compositing this sprite at that position over the port's own CHOOSE ACTIVITY /
/// Credits frames reproduces the original's screen with ZERO mismatching
/// pixels over the whole 320 × 200 frame, and at (290, 180) it reproduces the original's screen the
/// same way.
/// </para>
/// <para>
/// <b>No hourglass.</b> The second shape is a 'loading' clock, which the port has no use for: it
/// loads instantly.  It is also unreachable in the shipped build: <c>cursor_draw</c>
/// blits from source row 0 with a height clamped to <c>0x0B</c> (<c>image@0x2E92F..0x2E934</c>), so
/// the second shape has no drawer in the image at all.
/// </para>
/// </remarks>
public sealed class FrontEndPointer
{
    /// <summary>The cursor atlas document.</summary>
    public const string ArtDocument = "images/cursor.json";

    /// <summary>Its 1-bit overlay mask (1 = opaque).</summary>
    public const string MaskDocument = "images/masks/cursorm.json";

    /// <summary>
    /// How many PRE-SHIFT phases the VGA atlas carries — <c>frame_mask = 7</c> at
    /// <c>image@0x2E8F1</c>, so eight.
    /// </summary>
    public const int PhaseCount = 8;

    /// <summary>A phase's cell width in the atlas: <c>src_x = frame &lt;&lt; 4</c> (<c>image@0x2E906</c>).</summary>
    public const int CellWidth = 16;

    /// <summary>The sprite's height — the save-under region is 32 × <c>0x0B</c> (<c>image@0x2E847</c>).</summary>
    public const int SpriteHeight = 0x0B;

    /// <summary>The arrow's own width: nine columns, 0..8 of its cell.</summary>
    public const int ArrowWidth = 9;

    /// <summary>The hotspot's column inside the sprite — the tip (see the remarks).</summary>
    public const int HotspotX = 0;

    /// <summary>The hotspot's row inside the sprite.</summary>
    public const int HotspotY = 0;

    /// <summary>
    /// Where <c>widget_mouse_cursor_position_set</c>'s right-edge arm parks the cursor inside a
    /// widget rectangle: <c>rect + extent − 3</c> (<c>image@0x2E4F9..0x2E50C</c>).
    /// </summary>
    /// <remarks>
    /// The centre arm (<c>rect + extent / 2</c>, <c>image@0x2E4E6</c>) is taken only when
    /// <c>flags_u8 +0x12</c> bit 0 is SET, and it is CLEAR on every widget of every shipped
    /// front-end table (the six scenario-picker records at <c>[0x2EE4]</c>, the four briefing
    /// records at <c>[0x380A]</c> and the five comparison records at <c>[0x3AEC]</c> all carry
    /// flags 0x10 / 0x80 / 0x90 / 0x00), so the front end only ever uses this one.
    /// </remarks>
    public const int PlacementInset = 3;

    /// <summary>
    /// Where the pointer stands before anything moves it: design (290, 180).
    /// </summary>
    /// <remarks>
    /// Measured, not chosen: thirteen of the atlas's front-end frames were captured with no mouse
    /// input at all and every one of them draws the arrow with its tip at (290, 180) —
    /// <c>02</c>, <c>03</c>, <c>04</c>, <c>04c</c>, <c>05</c>, <c>05d</c>, <c>06</c>, <c>06b</c>,
    /// <c>07</c>, <c>07d</c>, <c>07e</c>, <c>10</c>, <c>11</c>, <c>12</c>
    /// (a captured frame of the original).  It is the DOS driver's own start position,
    /// which the port has no driver to inherit, so it is reproduced as a constant.
    /// </remarks>
    public static readonly (int X, int Y) StartPosition = (290, 180);

    private readonly IndexedImage _art;
    private readonly IndexedImage _mask;

    private FrontEndPointer(IndexedImage art, IndexedImage mask)
    {
        _art = art;
        _mask = mask;
        X = StartPosition.X;
        Y = StartPosition.Y;
    }

    /// <summary>Loads the arrow, or <see langword="null"/> when the tree carries no cursor art.</summary>
    /// <param name="tree">The data tree.</param>
    public static FrontEndPointer? Load(DataTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        try
        {
            IndexedImage art = tree.ImagePixels(ArtDocument);
            IndexedImage mask = tree.ImagePixels(MaskDocument);
            return art.Width < PhaseCount * CellWidth || art.Height < SpriteHeight
                || mask.Width != art.Width || mask.Height != art.Height
                ? null
                : new FrontEndPointer(art, mask);
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

    /// <summary>The hotspot's design column.</summary>
    public int X { get; private set; }

    /// <summary>The hotspot's design row.</summary>
    public int Y { get; private set; }

    /// <summary>Whether the pointer is drawn this frame.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>
    /// Puts the hotspot at a design point, clamped to the screen the original clamps to.
    /// </summary>
    /// <remarks>
    /// <c>ui_main_menu_enter @image@0x24334</c> initialises the mouse range to <b>319 × 199</b> —
    /// the whole screen — for every front-end screen (<c>cursor_range_init_screen @image@0x2E0EB</c>),
    /// so the pointer may stand anywhere in the design space and the sprite is clipped at its edges.
    /// </remarks>
    /// <param name="x">The design column.</param>
    /// <param name="y">The design row.</param>
    public void MoveTo(int x, int y)
    {
        X = Math.Clamp(x, 0, FrontEndPainter.DesignWidth - 1);
        Y = Math.Clamp(y, 0, FrontEndPainter.DesignHeight - 1);
    }

    /// <summary>
    /// Draws the arrow, the original's way: phase <c>x &amp; 7</c> of the atlas blitted at
    /// <c>x &amp; ~7</c>, through the mask, clipped to the design space.
    /// </summary>
    /// <param name="surface">The design surface — the pointer is drawn LAST, over everything.</param>
    public void Draw(in FrontEndPainter.Surface surface)
    {
        if (!Visible)
        {
            return;
        }

        int phase = X & (PhaseCount - 1);
        int sourceX = phase * CellWidth;
        int destinationX = X & ~(PhaseCount - 1);
        byte[] art = _art.Indices;
        byte[] mask = _mask.Indices;
        for (int row = 0; row < SpriteHeight; row++)
        {
            int y = Y + row - HotspotY;
            if ((uint)y >= FrontEndPainter.DesignHeight)
            {
                continue;
            }

            int line = row * _art.Width;
            for (int column = 0; column < CellWidth; column++)
            {
                int source = sourceX + column;
                int x = destinationX + column - HotspotX;
                if (source >= _art.Width || (uint)x >= FrontEndPainter.DesignWidth
                    || mask[line + source] == 0)
                {
                    continue;
                }

                surface.Pixel(x, y, art[line + source]);
            }
        }
    }
}
