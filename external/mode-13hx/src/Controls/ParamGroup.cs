using System.Numerics;
using mode13hx.Util;

namespace mode13hx.Controls;

public class ParamGroup : UiControlBase
{
    public const int ControlHeight = 20;
    public const int ControlMargin = 35;
    
    private readonly List<UiControlBase> slideBars = [];

    public ParamGroup(int x, int y, int width) : base(x, y)
    {
        this.Width = width;
    }
    
    public void AddParam<T>(string label, ControlParam<T> param, T min, T max) where T : INumber<T>
    {
        SlideBar<T> bar = new SlideBar<T>(X, Y + slideBars.Count*(ControlHeight + ControlMargin), Width, ControlHeight, Func.EncodePixelColor(0x0, 0x0, 0xA0), min, max, param);
        bar.ToolTip = $"{label} ({SlideBar<T>.Format2SignificantDigits(bar.Param.Value)})"; // https://learn.microsoft.com/en-us/dotnet/standard/base-types/standard-numeric-format-strings
        bar.OnActivate = number => bar.ToolTip = $"{label} ({SlideBar<T>.Format2SignificantDigits(number)})";
        slideBars.Add(bar);
    }
    
    public void AddParam<T>(string label, ControlParam<T> param, List<T> options)
    {
        OptionSelector<T> selector = new(X, Y + slideBars.Count*(ControlHeight + ControlMargin), Width, ControlHeight, options, option => option.ToString(), 0);
        selector.OnSelectionChanged = option => { selector.ToolTip = $"{label} ({option})"; param.Value = option; };
        slideBars.Add(selector);
    }
    
    public void AddParam(string label, ControlParam<bool> switchParam)
    {
        CheckboxControl checkBox = new(X, Y + slideBars.Count*(ControlHeight + ControlMargin), Width, ControlHeight, label, switchParam);
        slideBars.Add(checkBox);
    }

    public override bool ActiveArea(int x, int y)
    {
        return slideBars.Any(slideBar => slideBar.ActiveArea(x, y));
    }

    public override bool Click(int mx, int my)
    {
        foreach (UiControlBase slideBar in slideBars.Where(slideBar => slideBar.ActiveArea(mx, my))) { slideBar.Click(mx, my); return true; }
        return false;
    }
    
    public override void Draw(Canvas canvas, int mx, int my)
    {
        foreach (UiControlBase slideBar in slideBars) { slideBar.Draw(canvas, slideBar.ActiveArea(mx, my)); }
    }
}
