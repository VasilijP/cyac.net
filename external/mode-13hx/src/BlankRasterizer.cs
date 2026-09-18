using mode13hx.Configuration;
using mode13hx.Model;
using mode13hx.Presentation;
using mode13hx.Util;

namespace mode13hx;

public class BlankRasterizer(BlankOptions opts) : IRasterizer
{
    private readonly Texture texture = new() { Name = "resources/texture/test_8bitRLE.tga" };

    public void Render(FrameBuffer buffer, double secondsSinceLastFrame)
    {
        FrameDescriptor frame = buffer.StartNextFrame();
        
        //TODO: Your rendering code goes here
        
        // buffer is organized by columns, meaning that [X, Y] has the offset of: frame.Offset + x * frame.Buffer.Height + y
        // example: (sets the pixel at (100, 100) to white)
        int x = 100, y = 100;
        frame.Buffer.Data[frame.Offset + x * frame.Buffer.Height + y] = 0x00FFFFFF;
        
        // even better is to calculate the color using functions:
        frame.Buffer.Data[frame.Offset + x * frame.Buffer.Height + y + 100] = Func.EncodePixelColorRgba(0xFF, 0, 0, 0);
        frame.Buffer.Data[frame.Offset + x * frame.Buffer.Height + y + 200] = Func.EncodePixelColorRgba(0, 0xFF, 0, 0);
        frame.Buffer.Data[frame.Offset + x * frame.Buffer.Height + y + 300] = Func.EncodePixelColorRgba(0, 0, 0xFF, 0);
        
        // or use canvas:
        Canvas canvas = frame.Canvas;
        for (int i = 0; i < 10; i++)
        {
            canvas.SetPenColor(Func.EncodePixelColor(20*i, 20*i, 20*i)).DrawString("Mode 13hx!", 120 + i, 120 + i, Canvas.Font9X16);
        }
        
        canvas.Draw(texture, 200, 200);
        
        canvas.SetPenColor(0xFFFFFF).DrawString("Press ESC to exit.", 20, frame.Buffer.Height - 20, Canvas.Font16X16);
        
        buffer.FinishFrame(frame);
    }
}
