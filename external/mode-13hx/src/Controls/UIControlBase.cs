using mode13hx.Util;

namespace mode13hx.Controls;

public abstract class UiControlBase(int x, int y)
{
    public int X { get; set; } = x;
    public int Y { get; set; } = y;
    public int Width { get; set; }
    public int Height { get; set; }
    public string ToolTip { get; set; } = string.Empty;
    public int Id { get; set; } = -1;
    
    public virtual void Draw(Canvas canvas, bool active = false) {}
    public virtual void Draw(Canvas canvas, int mx, int my) { Draw(canvas, ActiveArea(mx, my)); }
    
    // is the mouse in the active area? (use for click or animation like ligting up a button, etc.)
    public abstract bool ActiveArea(int x, int y);
    
    public abstract bool Click(int mx, int my);
}
