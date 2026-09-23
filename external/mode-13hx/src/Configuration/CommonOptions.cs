using CommandLine;
using mode13hx.Model;
using Silk.NET.Input;

namespace mode13hx.Configuration;

public class CommonOptions
{
    [Option('w', "width", Default = 1920, HelpText = "Set the window width.")]
    public int Width { get; set; }

    [Option('h', "height", Default = 1080, HelpText = "Set the window height.")]
    public int Height { get; set; }

    [Option('f', "fullscreen", Default = false, HelpText = "Set fullscreen mode.")]
    public bool Fullscreen { get; set; }

    // CYAC local extension (see VENDOR.md): whether the window closes itself on ESC. The default keeps
    // upstream behaviour; an application that gives ESC a meaning of its own (an in-game menu) sets it
    // false and closes the window through its own action instead.
    public bool CloseOnEscape { get; set; } = true;

    // CYAC local extension (see VENDOR.md): which presenter draws the frame.
    [Option("gfx", Default = "opengl", HelpText = "opengl|vulkan — the presenter. OpenGL 3.3 core is the default and runs on every desktop driver, macOS included; Vulkan needs a loader (the GPU driver on Windows and Linux, MoltenVK on macOS) and is the one with --frame-compression.")]
    public string Gfx { get; set; }

    public bool UseVulkan => string.Equals(Gfx, "vulkan", StringComparison.OrdinalIgnoreCase);

    [Option('v', "vsync", Default = false, HelpText = "Enable or disable VSync.")]
    public bool VSync { get; set; }

    [Option('d', "dofps", Default = false, HelpText = "Toggle display of frames per second.")]
    public bool Dofps { get; set; } = true;

    // higher value could increase performance and decrease input lag but will increase system load and number of dropped frames
    [Option('l', "frames", Default = 1, HelpText = "Set the frame prerender limit. Total slot count is FramesPrerenderLimit + 1; the +1 reserves a slot the rasterizer never touches so the GPU can finish reading from the just-presented slot without contention.")]
    public int FramesPrerenderLimit { get; set; }

    // Drop intermediate frames when the rasterizer outpaces the presenter (mailbox-like behavior).
    [Option("drop-frames", Default = true, HelpText = "Drop intermediate frames when the rasterizer outpaces the presenter.")]
    public bool DropFrames { get; set; }

    // "Fast" mode: rasterizer never blocks waiting for a free slot; instead drops its own oldest unread frame.
    // This keeps the CPU continuously busy (avoiding aggressive idle / P-state ramp-up hiccups when capped by
    // VSync or a full buffer) at the cost of wasted rasterization work. The presenter always takes the latest
    // ready frame. Effective only with a small slot count; pair with --vsync for stable framepacing.
    [Option("fast", Default = false, HelpText = "Rasterizer never waits — drops its own oldest unread frame when the buffer is full. Keeps CPU hot, avoids vsync-induced idle hiccups. Presenter always takes the latest frame.")]
    public bool Fast { get; set; }

    // Compression toggle and selection.
    [Option("frame-compression", Default = false, HelpText = "Enable CPU-side frame compression with GPU decompression. Useful for PCIe-bound systems; minimal benefit on unified-memory architectures (Apple Silicon).")]
    public bool FrameCompression { get; set; }

    [Option('c', "compressor", Default = "rle", HelpText = "Compressor to use when --frame-compression is set: rle, rle2, bl16, l26, s64, sl64.")]
    public string Compressor { get; set; }

    [Option("compression-threads", Default = 16, HelpText = "Number of CPU threads used by the parallel compressors (l26, s64, sl64).")]
    public int CompressionThreads { get; set; }

    public Dictionary<Key, Control> KbControls { get; } = new()
    {
        { Key.W, Control.Create(ControlEnum.VK_FORWARD) },
        { Key.S, Control.Create(ControlEnum.VK_BACKWARD) },
        { Key.A, Control.Create(ControlEnum.VK_TURN_LEFT) },
        { Key.D, Control.Create(ControlEnum.VK_TURN_RIGHT) },
        { Key.Space, Control.Create(ControlEnum.VK_USE) },
    };

    public Dictionary<ControlEnum, Control> MouseControls { get; } = new()
    {
        { ControlEnum.MOUSE_DELTA_X, Control.Create(ControlEnum.MOUSE_DELTA_X) },
        { ControlEnum.MOUSE_DELTA_Y, Control.Create(ControlEnum.MOUSE_DELTA_Y) },
        { ControlEnum.MOUSE_DELTA_WHEEL, Control.Create(ControlEnum.MOUSE_DELTA_WHEEL) },
        { ControlEnum.MOUSE_BUTTON_LEFT, Control.Create(ControlEnum.VK_USE) },
        { ControlEnum.MOUSE_BUTTON_RIGHT, Control.Create(ControlEnum.MOUSE_BUTTON_RIGHT) }
    };
}
