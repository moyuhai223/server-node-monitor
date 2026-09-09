using System.Diagnostics;
using SNM.Master.Runtime;

namespace SNM.Master.Background;

/// <summary>PeriodicTimer-based base class: waits for the registry, aligns the first tick, isolates failures.</summary>
public abstract class SnmBackgroundService(ILogger logger, NodeRegistry registry) : BackgroundService
{
    protected ILogger Logger { get; } = logger;
    protected NodeRegistry Registry { get; } = registry;

    protected abstract TimeSpan Period { get; }

    /// <summary>Delay before the first tick (used to align to :05 of the minute etc.).</summary>
    protected virtual TimeSpan InitialDelay(DateTime now) => TimeSpan.Zero;

    protected abstract Task TickAsync(DateTime now, CancellationToken ct);

    /// <summary>Runs once before the periodic loop (backfill work).</summary>
    protected virtual Task OnStartAsync(CancellationToken ct) => Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Registry.Ready.WaitAsync(stoppingToken);
            await RunGuarded(() => OnStartAsync(stoppingToken), "start");
            var delay = InitialDelay(DateTime.UtcNow);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, stoppingToken);
            using var timer = new PeriodicTimer(Period);
            do
            {
                await RunGuarded(() => TickAsync(DateTime.UtcNow, stoppingToken), "tick");
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private async Task RunGuarded(Func<Task> work, string what)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await work();
            if (sw.Elapsed > TimeSpan.FromSeconds(2)) Logger.LogWarning("{Service} {What} took {Elapsed:F1}s", GetType().Name, what, sw.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Service} {What} failed", GetType().Name, what);
        }
    }

    /// <summary>Delay until the next boundary of <paramref name="periodSeconds"/> plus <paramref name="offsetSeconds"/>.</summary>
    protected static TimeSpan AlignTo(DateTime now, int periodSeconds, int offsetSeconds)
    {
        var unix = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds();
        var next = unix / periodSeconds * periodSeconds + offsetSeconds;
        if (next <= unix) next += periodSeconds;
        return TimeSpan.FromSeconds(next - unix);
    }

    protected static long UnixSeconds(DateTime now) => new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds();
}
