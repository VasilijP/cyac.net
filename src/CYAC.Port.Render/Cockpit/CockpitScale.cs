using CYAC.Port.Core.Model.Cockpit;

namespace CYAC.Port.Render.Cockpit;

/// <summary>
/// How the game's 320×200 design space maps onto the host window, and where the 3-D viewport ends up.
/// </summary>
/// <remarks>
/// <para>
/// The original composes one 320×200 frame: the world in the aircraft's viewport rows, the panel
/// around the hole, the compositor's spans over the hole.  The port keeps the same design space and
/// scales it, so every table in <c>exe/tables/cockpit_layout.json</c> and <c>dialinit.json</c> stays
/// meaningful without a single re-authored coordinate.
/// </para>
/// <para>
/// <b>The world keeps the H3 lens.</b>  The 3-D viewport is always the full window WIDTH, so the
/// horizontal field of view is unchanged at <see cref="CameraLens.DefaultHorizontalFovDegrees"/>
/// (102.68°, derived from the original's own 2^7-pixel focal length).  Only its HEIGHT changes: the
/// aircraft's viewport rows scaled by <see cref="ScaleY"/>.  Because
/// <c>focal = width / (2·tan(hFOV/2))</c> depends on the width alone, the vertical field follows
/// from the viewport's height by itself — <c>vFOV = 2·atan(viewportHeightPixels / (2·focal))</c> —
/// which is exactly what the 1991 clip setup does when it puts the window's centre at
/// <c>(y_min + y_max) &gt;&gt; 1</c> (<c>image@0x118A4</c>): with the P-51's 135-row viewport the
/// horizon sits on row 67 of 200, not on row 100, and the port reproduces that by rendering the
/// world into the top <see cref="ViewportHeightPixels"/> rows and letting its own centre fall where
/// the same ratio puts it.
/// </para>
/// </remarks>
/// <param name="Width">The window's width in pixels.</param>
/// <param name="Height">Its height.</param>
/// <param name="ScaleX">Design columns → window columns.</param>
/// <param name="ScaleY">Design rows → window rows.</param>
/// <param name="OffsetX">Where design column 0 starts, for a pillar-boxed fit.</param>
/// <param name="OffsetY">Where design row 0 starts.</param>
/// <param name="ViewportHeightPixels">
/// How many window rows the 3-D world fills.  The world always spans the full window width.
/// </param>
public readonly record struct CockpitScale(
    int Width,
    int Height,
    double ScaleX,
    double ScaleY,
    double OffsetX,
    double OffsetY,
    int ViewportHeightPixels)
{
    /// <summary>Resolves the mapping for a window, an aircraft and a fit.</summary>
    /// <param name="width">The window's width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="viewport">The aircraft's viewport rectangle in design space.</param>
    /// <param name="fit">Stretch or pillar-boxed.</param>
    /// <param name="cockpitDrawn">
    /// Whether the cockpit is painted this frame; when it is not, the world fills the window, which
    /// is what <c>image@0x01641</c> does by writing <c>{0, 0, [0x164], [0x166] − 1}</c> into the live
    /// viewport rectangle.
    /// </param>
    public static CockpitScale For(
        int width, int height, PanelRect viewport, CockpitFit fit, bool cockpitDrawn)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        double sx = width / (double)CockpitLayout.DesignWidth;
        double sy = height / (double)CockpitLayout.DesignHeight;
        double ox = 0.0;
        if (fit == CockpitFit.Uniform)
        {
            double s = Math.Min(sx, sy);
            ox = (width - (CockpitLayout.DesignWidth * s)) / 2.0;
            sx = s;
            sy = s;
        }

        int rows = cockpitDrawn
            ? Math.Clamp((int)Math.Round(viewport.Height * sy), 1, height)
            : height;

        return new CockpitScale(width, height, sx, sy, ox, 0.0, rows);
    }

    /// <summary>A design column's left edge in window pixels.</summary>
    /// <param name="x">The design column.</param>
    public double ToWindowX(double x) => OffsetX + (x * ScaleX);

    /// <summary>A design row's top edge in window pixels.</summary>
    /// <param name="y">The design row.</param>
    public double ToWindowY(double y) => OffsetY + (y * ScaleY);

    /// <summary>A window column's design column, for a nearest-neighbour sample.</summary>
    /// <param name="x">The window column's centre.</param>
    public double ToDesignX(double x) => (x - OffsetX) / ScaleX;

    /// <summary>A window row's design row.</summary>
    /// <param name="y">The window row's centre.</param>
    public double ToDesignY(double y) => (y - OffsetY) / ScaleY;
}
