using System.Numerics;
using mode13hx.Configuration;
using mode13hx.Controls;
using mode13hx.Model;
using mode13hx.Presentation;
using mode13hx.Util;

namespace mode13hx;

public class TestRasterizer(TestOptions opts) : IRasterizer
{
    private readonly FrameChart frameChart = new(8, 0, 120, Func.EncodePixelColor(0,0xFF,0), FrameBuffer.Fps);
    private Vector2 position = new(100, 100);         // Initial position of the bounding box
    private static Vector2 movement = new(500, 300);  // Movement vector (speed in pixels per second)
    private float movementRatio = 1.0f;
    private double accumulatedTime = 0;               // Accumulated time in seconds
    private const int BoxSize = 128;                  // Width&Height of the bounding box
    private readonly double deltaTime = 1/Math.Max(movement.X, movement.Y); // Delta to move at least 1px in any direction

    private int mx;
    private int my;
    private readonly Control mouseButtonLeft = opts.MouseControls[ControlEnum.MOUSE_BUTTON_LEFT];
    private readonly Control mouseDx = opts.MouseControls[ControlEnum.MOUSE_DELTA_X];
    private readonly Control mouseDy = opts.MouseControls[ControlEnum.MOUSE_DELTA_Y];
    private readonly Texture boxTexture = new() { Name = "resources/texture/box_128.tga" };
    private static readonly ControlParam<float> BoxSpeedParam = ControlParamRegistry.Get<float>("Speed multiplier", 1.0f);
    private readonly ControlParam<bool> drawFrameChart = ControlParamRegistry.Get("DrawFrameChart", opts.Dofps); // draw frametime chart
    private readonly SlideBar<float> boxSpeedRatio = new(10, 220, 400, 40, Func.EncodePixelColor(0x0, 0xA0, 0x00), 0.0f, 5.0f, BoxSpeedParam);

    public void Render(FrameBuffer buffer, double time)
    {
        // Simulate frame updates
        accumulatedTime += time;
        FrameDescriptor frame = buffer.StartNextFrame();
        
        while (accumulatedTime >= deltaTime)
        {
            // Update position based on accumulated time and movement vector
            accumulatedTime -= deltaTime;
            Vector2 displacement = movement * movementRatio * (float)deltaTime;
            position += displacement;

            // Handle edge reversal
            if (position.X < 0 || (int)(position.X + BoxSize) >= opts.Width) { movement.X = -movement.X; }
            if (position.Y < 0 || (int)(position.Y + BoxSize) >= opts.Height) { movement.Y = -movement.Y; }
        }
        
        if (position.X < 0) position.X = 0;
        if (position.X + BoxSize >= opts.Width) position.X = opts.Width - BoxSize -1; // max valid X coordinate is Program.Width-1
        if (position.Y < 0) position.Y = 0;
        if (position.Y + BoxSize >= opts.Height) position.Y = opts.Height - BoxSize -1; // max valid Y coordinate is Program.Height-1
        
        if (mouseButtonLeft.Active)
        {
            boxSpeedRatio.Click(mx, my);
            movementRatio = BoxSpeedParam.Value;
            boxSpeedRatio.ToolTip = $"Speed multiplier: {BoxSpeedParam.Value:F2}";
        }
        
        // Clear the frame with solid color
        frame.FrameSpan.Fill(Func.EncodePixelColor(190, 190, 190));
        
        // Create a canvas and draw all UI elements
        Canvas canvas = frame.Canvas;
        canvas.Draw(boxTexture, (int)position.X, (int)position.Y); //canvas.SetPenColor(0xFF, 0xFF, 0xFF).Rectangle(position.X, position.Y, BoxSize, BoxSize);
        
        // Speed slider
        boxSpeedRatio.Draw(canvas, boxSpeedRatio.ActiveArea(mx, my));
        
        // Mouse pointer
        mx = Math.Clamp(mx + Interlocked.Exchange(ref mouseDx.Delta, 0), 0, opts.Width-1);
        my = Math.Clamp(my + Interlocked.Exchange(ref mouseDy.Delta, 0), 0, opts.Height-1);
        canvas.SetPenColor(0x10, 0x10, 0).MoveTo(mx, my).Draw(0, 20, true).Draw(10, 0, true).Draw(-10, -20, true);
        
        if (drawFrameChart.Value) { frameChart.Draw(canvas); }

        buffer.FinishFrame(frame);
    }
}