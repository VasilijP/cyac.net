using System.Numerics;
using mode13hx.Util;

namespace mode13hx.Controls;

public class SlideBar<T> : UiControlBase where T : INumber<T>
{
    public ControlParam<T> Param { get; private set; }
    private double position;
    private readonly int defaultMarkerX;
    private const int Margin = 5;
    private readonly uint barColor;
    private readonly T min;
    private readonly T max;
    
    public Action<T> OnActivate { get; set; }
    
    public SlideBar(int x, int y, int width, int height, uint color, T min, T max, ControlParam<T> param) : base(x, y)
    {
        this.Width = width;
        this.Height = height;
        this.barColor = color;
        this.min = min;
        this.max = max;
        this.Param = param;
        position = Convert.ToDouble((param.Value - min)) / Convert.ToDouble((max - min));
        defaultMarkerX = X + Margin + (int)(Convert.ToDouble(param.Value - min) * (Width - 2 * Margin) / Convert.ToDouble(max - min));
        ToolTip = $"{Param.Name}: {Format2SignificantDigits(param.Value)}";
    }

    public override void Draw(Canvas canvas, bool active = false)
    {
        canvas.SetPenColor(Func.EncodePixelColor(0xFF, 0xFF, 0xFF)).Rectangle(X, Y, Width, Height);
        canvas.SetPenColor(barColor).Rectangle(X + Margin, Y + Margin, (int)(position * (Width - 2 * Margin)), Height - 2 * Margin);
        canvas.SetPenColor(Func.EncodePixelColor(0xFF, 0x00, 0x00)).MoveTo(defaultMarkerX, Y + Margin).Line(0, Height - 2 * Margin);
        if (active) { canvas.SetPenColor(0xFFFFFF).DrawString(ToolTip, X + 2*Margin, Y - 2*Margin - 16, Canvas.Font9X16); }
    }
    
    public override bool ActiveArea(int x, int y) { return x >= X && x < X + Width && y >= Y && y < Y + Height; }

    public override bool Click(int mx, int my)
    {
        if (!ActiveArea(mx, my)) return false;
        position = Math.Clamp((mx-X)*1.0/Width, 0, 1.0);
        double minValue = double.CreateChecked(min);
        double maxValue = double.CreateChecked(max);
        double interpolatedValue = minValue + (maxValue - minValue) * position;
        Param.Value = T.CreateChecked(interpolatedValue);
        OnActivate?.Invoke(Param.Value); return true;
    }
    
    public static string Format2SignificantDigits(T value)
    {
        int digits = 0;
        double v = double.CreateChecked(value);
        if (10.0.CompareTo(v) < 0) { digits = 0; }
        else if (1.0.CompareTo(v) < 0) { digits = 1; }
        else if (0.1.CompareTo(v) < 0) { digits = 2; }
        else if (0.01.CompareTo(v) < 0) { digits = 3; }
        else if (0.001.CompareTo(v) < 0) { digits = 4; }
        else if (0.0001.CompareTo(v) < 0) { digits = 5; }
        else if (0.00001.CompareTo(v) < 0) { digits = 6; }
        
        string format = $"{{0:F{digits}}}";
        return string.Format(format, v);
    }
}
