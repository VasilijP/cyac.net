using CommandLine;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using mode13hx.Configuration;
using mode13hx.Presentation;

namespace mode13hx;

public static class Program
{
    public static int Main(string[] args)
    {
        return Parser.Default.ParseArguments<TestOptions, BlankOptions>(args) // https://github.com/commandlineparser/commandline
            .MapResult(
                (TestOptions opts) => RunWithOptions(opts, new TestRasterizer(opts)),
                (BlankOptions opts) => RunWithOptions(opts, new BlankRasterizer(opts)),
                errs => 1);
    }

    private static int RunWithOptions<T>(T config, IRasterizer ras) where T : CommonOptions
    {
        WindowOptions options = WindowOptions.DefaultVulkan;
        options.Title = "mode13hx";
        options.Size = new Vector2D<int>(config.Width, config.Height);
        options.WindowState = config.Fullscreen ? WindowState.Fullscreen : WindowState.Normal;
        options.VSync = config.VSync;

        IWindow window = Window.Create(options);
        using EngineWindow engine = new(window, config, ras);
        engine.Run();
        return 0;
    }
}
