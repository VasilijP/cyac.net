using mode13hx.Presentation;
using mode13hx.Util;

namespace mode13hx.Controls;

public class FrameChart(int x, int y, int height, uint color, FrametimeComponent frametimeComponent) : UiControlBase(x, y)
{
    private readonly int bucketCount = frametimeComponent.BucketCount;
    private readonly uint maxColor = Func.MixColors(color, 1.0f, Func.EncodePixelColor(0xFF, 0, 0), 1.0f);
    private readonly uint minColor = Func.MixColors(color, 1.0f, Func.EncodePixelColor(0x50, 0x50, 0x50), 1.0f);

    public override void Draw(Canvas canvas, bool active = false)
    {
        double maxFrametime = 1.05 * (frametimeComponent.GetMaxFrametime() + 1.0);
        double hScale = 1.0 * height / maxFrametime;
        
        
        for (int i = 0; i < bucketCount; i++) // draw each bucket as a vertical line
        {
            (double min, double average, double max) = frametimeComponent.GetFrametimeAtBucket(i);
            int barHeight = (int)(hScale * max); canvas.SetPenColor(maxColor).MoveTo(X + i, Y + height).LineTo(X + i, Y + height - barHeight);
            barHeight = (int)(hScale * average); canvas.SetPenColor(color).MoveTo(X + i, Y + height).LineTo(X + i, Y + height - barHeight);
            barHeight = (int)(hScale * min); canvas.SetPenColor(minColor).MoveTo(X + i, Y + height).LineTo(X + i, Y + height - barHeight);
        }
        
        DrawStatistics(canvas);
    }

    private void DrawStatistics(Canvas canvas)
    {
        double averageFrametime = frametimeComponent.GetAverageFrametime();
        double minFrametime = frametimeComponent.GetMinFrametime();
        double maxFrametime = frametimeComponent.GetMaxFrametime();
        
        string minText = $"Min: {minFrametime:F2} ms ({FrametimeComponent.FrametimeToFps(minFrametime):F1}fps)";
        string maxText = $"Max: {maxFrametime:F2} ms ({FrametimeComponent.FrametimeToFps(maxFrametime):F1}fps)";
        string avgText = $"Avg: {averageFrametime:F2} ms ({FrametimeComponent.FrametimeToFps(averageFrametime):F1}fps)";

        int textY = Y + height + 8, textX = X + 1;
        canvas.DrawString(minText, textX, textY, Canvas.Font9X16); textY += 16;
        canvas.DrawString(avgText, textX , textY, Canvas.Font9X16); textY += 16;
        canvas.DrawString(maxText, textX, textY, Canvas.Font9X16);
    }
    
    public override bool ActiveArea(int x, int y) { return false; }
    public override bool Click(int mx, int my) { return false; }
}
