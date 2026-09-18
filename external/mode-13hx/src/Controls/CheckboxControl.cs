using System.Diagnostics;
using mode13hx.Util;

namespace mode13hx.Controls;

public class CheckboxControl : UiControlBase
{
    private bool isChecked;
    private readonly string label;
    private readonly ControlParam<bool> param;

    private const int CheckboxSize = 20;
    private const int Margin = 5;
    private const int ButtonDebounceMillis = 300;

    private readonly uint boxColor = Func.EncodePixelColor(0x80, 0x80, 0x80);
    private readonly uint checkColor = Func.EncodePixelColor(0xFF, 0xFF, 0xFF);
    private readonly uint textColor = Func.EncodePixelColor(0xFF, 0xFF, 0xFF);
    private readonly Stopwatch debounceTimer = Stopwatch.StartNew();

    public Action<bool> OnCheckedChanged { get; set; }

    public CheckboxControl(int x, int y, int width, int height, string label, ControlParam<bool> param) : base(x, y)
    {
        this.Width = width;
        this.Height = height;
        this.label = label ?? throw new ArgumentNullException(nameof(label));
        this.param = param ?? throw new ArgumentNullException(nameof(param));
        this.isChecked = param.Value;
    }

    public override void Draw(Canvas canvas, bool active = false)
    {
        if (active) { canvas.SetPenColor(Func.EncodePixelColor(0x65, 0x65, 0x65)).Rectangle(X, Y, Width, Height); }
        
        int boxX = X;
        int boxY = Y + (Height - CheckboxSize) / 2;
        canvas.SetPenColor(boxColor).Rectangle(boxX, boxY, CheckboxSize, CheckboxSize);
        
        if (isChecked) { canvas.SetPenColor(checkColor).MoveTo(boxX, boxY).Line(CheckboxSize, CheckboxSize).MoveTo(boxX + CheckboxSize, boxY).Line(-CheckboxSize, CheckboxSize); }
        
        int textX = boxX + CheckboxSize + Margin;
        canvas.SetPenColor(textColor).DrawString(label, textX, Y + Height / 2 - 8, Canvas.Font9X16);
    }

    public override bool ActiveArea(int x, int y)
    {
        return x >= X && x < X + Width && y >= Y && y < Y + Height;
    }

    public override bool Click(int mx, int my)
    {
        if (!ActiveArea(mx, my) || debounceTimer.ElapsedMilliseconds < ButtonDebounceMillis) { return false; }

        isChecked = !isChecked;
        param.Value = isChecked;
        OnCheckedChanged?.Invoke(isChecked);
        debounceTimer.Restart();
        return true;
    }
}
