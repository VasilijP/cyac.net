using System.Diagnostics;
using mode13hx.Util;

namespace mode13hx.Controls;

public class OptionSelector<T> : UiControlBase
{
    private readonly List<T> options;
    private int selectedIndex;
    private readonly Func<T, string> displayFunc;

    private const int ButtonWidth = 20;
    private const int ButtonDebounceMillis = 300;
    private const int Margin = 5;
    
    private readonly uint buttonColor = Func.EncodePixelColor(0x80, 0x80, 0x80);
    private readonly uint textColor = Func.EncodePixelColor(0xFF, 0xFF, 0xFF);
    private readonly Stopwatch debounceTimer = Stopwatch.StartNew();

    public Action<T> OnSelectionChanged { get; set; }

    public OptionSelector(int x, int y, int width, int height, List<T> options, Func<T, string> displayFunc, int initialIndex = 0) : base(x, y)
    {
        this.Width = width;
        this.Height = height;
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.displayFunc = displayFunc ?? throw new ArgumentNullException(nameof(displayFunc));
        this.selectedIndex = Math.Clamp(initialIndex, 0, options.Count - 1);
    }

    public T SelectedOption => options[selectedIndex];

    public override void Draw(Canvas canvas, bool active = false)
    {
        if (active) { canvas.SetPenColor(Func.EncodePixelColor(0x65, 0x65, 0x65)).Rectangle(X, Y, Width, Height); }
        
        int leftButtonX = X;
        canvas.SetPenColor(buttonColor).Rectangle(leftButtonX, Y, ButtonWidth, Height);
        canvas.SetPenColor(textColor).DrawString("<", leftButtonX + ButtonWidth / 2 - 4, Y + Height / 2 - 8, Canvas.Font9X16);
        
        int rightButtonX = X + Width - ButtonWidth;
        canvas.SetPenColor(buttonColor).Rectangle(rightButtonX, Y, ButtonWidth, Height);
        canvas.SetPenColor(textColor).DrawString(">", rightButtonX + ButtonWidth / 2 - 4, Y + Height / 2 - 8, Canvas.Font9X16);
        
        int textX = X + ButtonWidth + Margin;
        string displayText = displayFunc(SelectedOption);
        canvas.SetPenColor(textColor).DrawString(displayText, textX, Y + Height / 2 - 8, Canvas.Font9X16);
    }

    public override bool ActiveArea(int x, int y)
    {
        return x >= X && x < X + Width && y >= Y && y < Y + Height;
    }

    public override bool Click(int mx, int my)
    {
        if (!ActiveArea(mx, my) || debounceTimer.ElapsedMilliseconds < ButtonDebounceMillis ) return false;
        
        if (mx >= X && mx < X + ButtonWidth) // left
        {
            selectedIndex = (selectedIndex - 1 + options.Count) % options.Count;
            OnSelectionChanged?.Invoke(SelectedOption);
            debounceTimer.Restart();
            return true;
        }
        
        if (mx >= X + Width - ButtonWidth && mx < X + Width) // right
        {
            selectedIndex = (selectedIndex + 1) % options.Count;
            OnSelectionChanged?.Invoke(SelectedOption);
            debounceTimer.Restart();
            return true;
        }
        
        return false;
    }
}
