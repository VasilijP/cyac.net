using System.Diagnostics;
using mode13hx.Configuration;
using mode13hx.Model;
using mode13hx.Presentation.Compression;
using Silk.NET.Input;
using Silk.NET.Windowing;
using Timer = System.Timers.Timer;

namespace mode13hx.Presentation;

public class EngineWindow : IDisposable
{
    private readonly IWindow window;
    private VulkanRenderer renderer;
    private readonly Thread renderThread;
    private readonly IRasterizer rasterizer;
    private FrameBuffer frameBuffer;

    private readonly Stopwatch elapsedTime;
    private long frameCount;
    private long bytesTransmitted;
    private readonly Timer timer = new(1000);
    private readonly CommonOptions config;
    private volatile bool isExiting;

    private IInputContext inputContext;
    private float lastMouseX, lastMouseY, lastScrollY;
    private readonly IFrameCompressor compressor;

    public EngineWindow(IWindow window, CommonOptions config, IRasterizer rasterizer)
    {
        this.window = window;
        this.config = config;
        this.rasterizer = rasterizer;
        renderThread = new Thread(RenderThreadMain) { IsBackground = true };
        elapsedTime = Stopwatch.StartNew();

        if (config.FrameCompression)
        {
            compressor = config.Compressor.ToLowerInvariant() switch
            {
                "rle" => new FrameCompressorRle(),
                "rle2" => new FrameCompressorRle2(),
                "bl16" => new FrameCompressorBl16(config),
                "l26" => new FrameCompressorL26(config),
                "s64" => new FrameCompressorS64(config),
                "sl64" => new FrameCompressorSL64(config),
                _ => throw new ArgumentException($"Unknown compressor: {config.Compressor}. Valid options: rle, rle2, bl16, l26, s64, sl64")
            };
        }

        frameBuffer = new FrameBuffer(config, compressor);

        timer.Elapsed += (_, _) => { Console.WriteLine($"FPS copied: {frameCount / elapsedTime.Elapsed.TotalSeconds:F1}, FPS rendered: {(frameCount + frameBuffer.DroppedFrames) / elapsedTime.Elapsed.TotalSeconds:F1}, elapsed time: {elapsedTime}, frame count: {frameCount} +dropped: {frameBuffer.DroppedFrames} PCIe Bandwidth: {bytesTransmitted / elapsedTime.Elapsed.TotalSeconds / 1024 / 1024:F2} MB/s"); };
        timer.Start();

        window.Load += OnLoad;
        window.Render += OnRenderFrame;
        window.Update += OnUpdateFrame;
        window.Closing += OnClosing;
    }

    private void OnLoad()
    {
        renderer = new VulkanRenderer(window, config.Width, config.Height, config.VSync);

        if (config.FrameCompression && compressor != null)
            renderer.InitComputePipeline(compressor.DecompressShaderName);

        inputContext = window.CreateInput();

        // Grab cursor
        foreach (ICursor cursor in inputContext.Mice.Select(m => m.Cursor))
            cursor.CursorMode = CursorMode.Raw;

        elapsedTime.Restart();
        renderThread.Start();
    }

    private void RenderThreadMain()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!isExiting)
        {
            double secondsSinceLastFrame = stopwatch.Elapsed.TotalSeconds;
            stopwatch.Restart();
            rasterizer.Render(frameBuffer, secondsSinceLastFrame);
        }
    }

    private unsafe void OnRenderFrame(double deltaTime)
    {
        frameCount++;

        // Wait for GPU to finish with this frame slot before writing to shared buffers
        renderer.BeginFrame();

        // Get the latest CPU-rasterized frame and upload to GPU
        FrameDescriptor fd = frameBuffer.Use();
        bytesTransmitted += fd.Transferred;

        if (fd.Count > 0) // compressed frame — dispatch compute shader
        {
            renderer.UploadCompressedFrame(fd.Blocks.AsPointer(), fd.Count, fd.GpuDecompressionGroupsX, fd.GpuDecompressionGroupsY);
        }
        else // uncompressed frame — direct staging buffer upload (synchronous memcpy, slot safe to reuse on return)
        {
            renderer.UploadFrame(fd.Buffer.Data.AsPointer() + fd.Offset, fd.Buffer.FrameSize);
        }

        // Source pixels have been consumed (memcpy'd into Vulkan staging or compressed-blocks buffer); slot is free again.
        frameBuffer.ReleaseFrame();

        renderer.DrawFrame();
    }

    private void OnUpdateFrame(double deltaTime)
    {
        if (inputContext.Keyboards.Count > 0)
        {
            IKeyboard kb = inputContext.Keyboards[0];
            if (config.CloseOnEscape && kb.IsKeyPressed(Key.Escape)) { window.Close(); return; }

            Control.Reset();
            foreach (KeyValuePair<Key, Control> c in config.KbControls)
                c.Value.Active |= kb.IsKeyPressed(c.Key);
        }

        if (inputContext.Mice.Count > 0)
        {
            IMouse mouse = inputContext.Mice[0];

            float mx = mouse.Position.X, my = mouse.Position.Y;
            Interlocked.Add(ref config.MouseControls[ControlEnum.MOUSE_DELTA_X].Delta, (int)(mx - lastMouseX));
            Interlocked.Add(ref config.MouseControls[ControlEnum.MOUSE_DELTA_Y].Delta, (int)(my - lastMouseY));
            lastMouseX = mx; lastMouseY = my;

            float scrollY = mouse.ScrollWheels.Count > 0 ? mouse.ScrollWheels[0].Y : 0;
            Interlocked.Add(ref config.MouseControls[ControlEnum.MOUSE_DELTA_WHEEL].Delta, (int)(scrollY - lastScrollY));
            lastScrollY = scrollY;

            config.MouseControls[ControlEnum.MOUSE_BUTTON_LEFT].Active |= mouse.IsButtonPressed(MouseButton.Left);
            config.MouseControls[ControlEnum.MOUSE_BUTTON_RIGHT].Active |= mouse.IsButtonPressed(MouseButton.Right);
        }
    }

    private void OnClosing()
    {
        isExiting = true;
        if (renderThread.IsAlive) renderThread.Join(2000);
        timer.Stop();
        renderer?.Dispose();
        renderer = null;
    }

    public void Run() => window.Run();

    public void Dispose()
    {
        renderer?.Dispose();
        inputContext?.Dispose();
        timer.Dispose();
        window.Dispose();
    }
}
