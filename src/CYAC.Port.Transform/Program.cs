using CYAC.Port.Transform;
using CYAC.Port.Transform.Cli;

// cyac-transform — thin console shell.  All behaviour lives in TransformRunner so it is reachable
// from CYAC.Port.Transform.Tests without a process.  Plan.

if (!CommandLine.TryParse(args, out TransformOptions? options, out string? error))
{
    if (error is not null)
    {
        Console.Error.WriteLine($"cyac-transform: {error}");
        Console.Error.WriteLine();
    }

    Console.Error.WriteLine(CommandLine.Usage);
    return error is null ? TransformRunner.ExitOk : TransformRunner.ExitUsage;
}

return new TransformRunner(Console.Out).Run(options!);
