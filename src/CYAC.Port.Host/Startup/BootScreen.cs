using System.Collections.Immutable;
using System.Diagnostics;
using CYAC.Port.Host.Input;
using CYAC.Port.Preflight;
using mode13hx;
using mode13hx.Presentation;

namespace CYAC.Port.Host.Startup;

/// <summary>What the boot screen's hand-over produced.</summary>
/// <param name="Game">The rasterizer the window draws from now on, or null.</param>
/// <param name="Refusal">Why the game could not be built, or null.</param>
internal readonly record struct BootHandover(IRasterizer? Game, string? Refusal);

/// <summary>
/// The window's first rasterizer: it runs the startup checks on a worker thread, draws the dashboard
/// while they run, and hands the window over to the game when they are done.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threads.</b>  Everything here happens on the RENDER thread except the pipeline itself, which
/// runs on a background thread and publishes immutable snapshots; the render thread reads the latest
/// one and draws it.  The game is built on the render thread, in the frame that hands over, so
/// nothing about the game's own construction moves off the thread that has always built it.
/// </para>
/// <para>
/// <b>Closing.</b>  Esc cancels the run through its token and raises a flag; the window closes itself
/// on its next update tick, because Silk.NET's window may only be closed from its own thread.
/// </para>
/// </remarks>
internal sealed class BootScreen : IRasterizer, IDisposable
{
    /// <summary>The port setting that says when the dashboard is drawn.</summary>
    public const string PolicySetting = "startup-check";

    private readonly Func<PreflightPipeline> _pipeline;
    private readonly IFlightInputSource _input;
    private readonly StartupCheckPolicy _policy;
    private readonly Func<PreflightResult, BootHandover> _handover;
    private readonly PreflightScreen _screen;
    private readonly Sink _sink;

    private PreflightSnapshot _snapshot = Pending();
    private PreflightResult? _result;
    private CancellationTokenSource? _cancel;
    private Thread? _worker;
    private long _finished;
    private IRasterizer? _game;
    private string? _message;
    private bool _allDetails;
    private double _seconds;
    private Action? _exitAction;
    private bool _disposed;

    /// <summary>Creates the screen and starts the first run.</summary>
    /// <param name="pipeline">Makes a pipeline; one per run, so a rescan is a fresh run.</param>
    /// <param name="input">Where the dashboard's keys come from.</param>
    /// <param name="policy">When the dashboard is drawn.</param>
    /// <param name="text">Whether the bitmap font is reachable.</param>
    /// <param name="handover">Builds the game from a finished run, on the render thread.</param>
    public BootScreen(
        Func<PreflightPipeline> pipeline,
        IFlightInputSource input,
        StartupCheckPolicy policy,
        bool text,
        Func<PreflightResult, BootHandover> handover)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(handover);
        _pipeline = pipeline;
        _input = input;
        _policy = policy;
        _handover = handover;
        _screen = new PreflightScreen(text);
        _sink = new Sink(this);
        Start();
    }

    /// <summary>Whether the window has been asked to close.</summary>
    public bool ExitRequested { get; private set; }

    /// <summary>The latest snapshot the pipeline published.</summary>
    public PreflightSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Sets what Esc runs (a flag; the window closes itself on its own thread).</summary>
    /// <param name="exitAction">The action.</param>
    public void SetExitAction(Action exitAction) => _exitAction = exitAction;

    /// <inheritdoc/>
    public void Render(FrameBuffer buffer, double secondsSinceLastFrame)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_game is { } handedOver)
        {
            handedOver.Render(buffer, secondsSinceLastFrame);
            return;
        }

        _seconds += secondsSinceLastFrame;
        PreflightSnapshot snapshot = Snapshot;
        switch (PreflightDashboard.Decide(PreflightDashboard.KeysFrom(_input.Sample(secondsSinceLastFrame)), snapshot))
        {
            case DashboardAction.Quit:
                Quit();
                break;

            case DashboardAction.Rescan:
                Rescan();
                snapshot = Snapshot;
                break;

            case DashboardAction.ToggleDetails:
                _allDetails = !_allDetails;
                break;

            case DashboardAction.Continue:
                HandOver();
                break;

            default:
                break;
        }

        // A clean run under `problems` never shows the screen at all: the window opens into the game.
        if (_game is null && _message is null
            && PreflightDashboard.HandsOverWithoutAKey(snapshot, _policy))
        {
            HandOver();
        }

        if (_game is { } started)
        {
            started.Render(buffer, secondsSinceLastFrame);
            return;
        }

        if (_message is null && PreflightDashboard.ShouldAutoRescan(snapshot, SinceFinished()))
        {
            Rescan();
            snapshot = Snapshot;
        }

        _screen.View = new DashboardView(
            snapshot,
            _message is not null || PreflightDashboard.ShouldShow(snapshot, _policy),
            _allDetails,
            _seconds,
            _message);
        _screen.Render(buffer, secondsSinceLastFrame);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancel?.Cancel();
        _worker?.Join(TimeSpan.FromSeconds(5));
        _cancel?.Dispose();
    }

    /// <summary>A snapshot with every step still pending — what the first frames draw.</summary>
    private static PreflightSnapshot Pending()
    {
        ImmutableArray<StepReport> steps = Enum.GetValues<PreflightStep>().Select(StepReport.Pending).ToImmutableArray();
        return new PreflightSnapshot(steps, StepState.Running, 0, TimeSpan.Zero);
    }

    private TimeSpan SinceFinished()
    {
        long finished = Volatile.Read(ref _finished);
        return finished == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(finished);
    }

    private void Start()
    {
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        Volatile.Write(ref _result, null);
        Volatile.Write(ref _finished, 0);
        CancellationToken token = _cancel.Token;
        Thread worker = new Thread(() => RunPipeline(token))
        {
            IsBackground = true,
            Name = "cyac-preflight",
        };
        _worker = worker;
        worker.Start();
    }

    private void RunPipeline(CancellationToken token)
    {
        try
        {
            PreflightResult result = _pipeline().Run(_sink, token);
            Volatile.Write(ref _result, result);
        }
        catch (OperationCanceledException)
        {
            // Esc: the last snapshot already shows the unfinished steps as skipped.
        }
        catch (Exception error)
        {
            // A throw the pipeline did not turn into a failed step would otherwise take the process
            // down from a background thread; the player sees it on the screen instead.
            Volatile.Write(ref _message, $"the startup checks threw: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            Volatile.Write(ref _finished, Stopwatch.GetTimestamp());
        }
    }

    private void Rescan()
    {
        if (_worker is { IsAlive: true })
        {
            return;
        }

        Volatile.Write(ref _message, null);
        Volatile.Write(ref _snapshot, Pending());
        Start();
    }

    private void HandOver()
    {
        if (Volatile.Read(ref _result) is not { } result)
        {
            return;
        }

        BootHandover handover = _handover(result);
        if (handover.Game is { } game)
        {
            _game = game;
            return;
        }

        _message = handover.Refusal ?? "the game could not be started.";
    }

    private void Quit()
    {
        ExitRequested = true;
        _cancel?.Cancel();
        _exitAction?.Invoke();
    }

    /// <summary>Takes the pipeline's snapshots on its own thread and publishes the latest one.</summary>
    /// <param name="screen">The screen to publish into.</param>
    private sealed class Sink(BootScreen screen) : IProgress<PreflightSnapshot>
    {
        /// <inheritdoc/>
        public void Report(PreflightSnapshot value) => Volatile.Write(ref screen._snapshot, value);
    }
}
